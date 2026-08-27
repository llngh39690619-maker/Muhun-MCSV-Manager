[CmdletBinding()]
param(
    [string]$ToolingRoot = (Join-Path $PSScriptRoot '..\..\tooling'),
    [string]$SigningRoot,
    [string]$StagingRoot = (Join-Path $PSScriptRoot '..\artifacts\android-release-staging'),
    [string]$VersionName = '1.0.0',
    [int]$VersionCode = 1,
    [switch]$AcceptAndroidSdkLicenses
)

Set-StrictMode -Version Latest
$ErrorActionPreference = 'Stop'
$ProgressPreference = 'SilentlyContinue'
$PSNativeCommandUseErrorActionPreference = $false

$productId = 'muhun.mcsv.manager'
$packageId = 'com.muhun.mcsv.remote'
$wrapperSha256 = '497c8c2a7e5031f6aa847f88104aa80a93532ec32ee17bdb8d1d2f67a194a9c7'
$gradleSha256 = '553c78f50dafcd54d65b9a444649057857469edf836431389695608536d6b746'
$repositoryRoot = [IO.Path]::GetFullPath((Join-Path $PSScriptRoot '..'))
$androidRoot = [IO.Path]::GetFullPath((Join-Path $repositoryRoot 'android\MuhunMcsvRemote'))
$ToolingRoot = [IO.Path]::GetFullPath($ToolingRoot)
if ([string]::IsNullOrWhiteSpace($SigningRoot)) {
    $SigningRoot = Join-Path $ToolingRoot 'android-signing'
}
$SigningRoot = [IO.Path]::GetFullPath($SigningRoot)
$StagingRoot = [IO.Path]::GetFullPath($StagingRoot)

if ($VersionName -notmatch '^(?:0|[1-9][0-9]*)\.(?:0|[1-9][0-9]*)\.(?:0|[1-9][0-9]*)(?:-[0-9A-Za-z-]+(?:\.[0-9A-Za-z-]+)*)?$' -or
    $VersionName.Length -gt 64) {
    throw 'Android VersionName must be a bounded semantic version.'
}
if ($VersionCode -lt 1 -or $VersionCode -gt 999999999) {
    throw 'Android VersionCode must be between 1 and 999999999.'
}

function Get-Sha256([string]$Path) {
    return (Get-FileHash -LiteralPath $Path -Algorithm SHA256).Hash.ToLowerInvariant()
}

function Assert-SafeChildPath([string]$Root, [string]$Path) {
    $fullRoot = [IO.Path]::GetFullPath($Root).TrimEnd('\') + '\'
    $fullPath = [IO.Path]::GetFullPath($Path)
    if (-not $fullPath.StartsWith($fullRoot, [StringComparison]::OrdinalIgnoreCase)) {
        throw "Android release path escaped its dedicated root: $fullPath"
    }
    return $fullPath
}

function Get-PlainText([Security.SecureString]$Secret) {
    $pointer = [Runtime.InteropServices.Marshal]::SecureStringToBSTR($Secret)
    try {
        return [Runtime.InteropServices.Marshal]::PtrToStringBSTR($pointer)
    }
    finally {
        [Runtime.InteropServices.Marshal]::ZeroFreeBSTR($pointer)
    }
}

function Invoke-NativeCapture([string]$FilePath, [string[]]$Arguments) {
    $lines = @(& $FilePath @Arguments 2>&1 | ForEach-Object { $_.ToString() })
    if ($LASTEXITCODE -ne 0) {
        $bounded = ($lines | Select-Object -Last 80) -join "`n"
        throw "Android verification tool failed with exit code $LASTEXITCODE.`n$bounded"
    }
    return $lines
}

if (-not $IsWindows) {
    throw 'The signed Android release workflow currently supports Windows x64 only.'
}
if (-not (Test-Path -LiteralPath $androidRoot -PathType Container)) {
    throw "Android project is missing: $androidRoot"
}
$initializer = Join-Path $PSScriptRoot 'Initialize-MuhunMcsvAndroidToolchain.ps1'
$toolchain = & $initializer `
    -ToolingRoot $ToolingRoot `
    -AcceptAndroidSdkLicenses:$AcceptAndroidSdkLicenses

$wrapperJar = Join-Path $androidRoot 'gradle\wrapper\gradle-wrapper.jar'
$wrapperProperties = Join-Path $androidRoot 'gradle\wrapper\gradle-wrapper.properties'
$gradlew = Join-Path $androidRoot 'gradlew.bat'
foreach ($required in @($wrapperJar, $wrapperProperties, $gradlew)) {
    if (-not (Test-Path -LiteralPath $required -PathType Leaf)) {
        throw 'Pinned Gradle wrapper is missing. Regenerate it only with the verified Gradle 9.5.0 distribution.'
    }
}
if ((Get-Sha256 $wrapperJar) -ne $wrapperSha256) {
    throw 'Gradle wrapper JAR failed the official pinned SHA-256 check.'
}
$wrapperText = [IO.File]::ReadAllText($wrapperProperties)
if ($wrapperText -notmatch '(?m)^distributionUrl=https\\://services\.gradle\.org/distributions/gradle-9\.5\.0-bin\.zip\s*$' -or
    $wrapperText -notmatch "(?m)^distributionSha256Sum=$gradleSha256\s*$") {
    throw 'Gradle wrapper distribution URL or SHA-256 pin is invalid.'
}

$safeToolingPrefix = $ToolingRoot.TrimEnd('\') + '\'
if (-not $SigningRoot.StartsWith($safeToolingPrefix, [StringComparison]::OrdinalIgnoreCase)) {
    throw 'Android signing directory must remain inside the dedicated workspace tooling root.'
}
$signingDirectory = Get-Item -LiteralPath $SigningRoot -Force -ErrorAction SilentlyContinue
if ($null -eq $signingDirectory -or
    -not $signingDirectory.PSIsContainer -or
    (($signingDirectory.Attributes -band [IO.FileAttributes]::ReparsePoint) -ne 0)) {
    throw 'Android release signing identity is missing or unsafe. Run New-MuhunMcsvAndroidSigningIdentity.ps1 first.'
}
$credentialsPath = Join-Path $SigningRoot 'android-signing.v1.dpapi.json'
if (-not (Test-Path -LiteralPath $credentialsPath -PathType Leaf) -or
    (Get-Item -LiteralPath $credentialsPath).Length -gt 64KB) {
    throw 'Android DPAPI signing record is missing or invalid.'
}
$credentials = [IO.File]::ReadAllText($credentialsPath) | ConvertFrom-Json
if ($credentials.schemaVersion -ne 1 -or
    $credentials.keyStoreFile -ne 'muhun-mcsv-remote-release.p12' -or
    $credentials.keyStoreType -ne 'PKCS12' -or
    $credentials.keyAlias -notmatch '^[a-z][a-z0-9-]{2,31}$' -or
    $credentials.signingCertificateSha256 -notmatch '^[a-f0-9]{64}$' -or
    [string]::IsNullOrWhiteSpace($credentials.encryptedStorePassword) -or
    [string]::IsNullOrWhiteSpace($credentials.encryptedKeyPassword)) {
    throw 'Android DPAPI signing record schema is invalid.'
}
$keystorePath = Assert-SafeChildPath $SigningRoot (Join-Path $SigningRoot $credentials.keyStoreFile)
if (-not (Test-Path -LiteralPath $keystorePath -PathType Leaf) -or
    ((Get-Item -LiteralPath $keystorePath -Force).Attributes -band [IO.FileAttributes]::ReparsePoint)) {
    throw 'Android release keystore is missing or unsafe.'
}

try {
    $storeSecure = ConvertTo-SecureString $credentials.encryptedStorePassword
    $keySecure = ConvertTo-SecureString $credentials.encryptedKeyPassword
}
catch {
    throw 'Android signing secrets cannot be decrypted by this Windows account.'
}

$savedEnvironment = @{
    JAVA_HOME = $env:JAVA_HOME
    ANDROID_HOME = $env:ANDROID_HOME
    ANDROID_SDK_ROOT = $env:ANDROID_SDK_ROOT
    ANDROID_USER_HOME = $env:ANDROID_USER_HOME
    ANDROID_PREFS_ROOT = $env:ANDROID_PREFS_ROOT
    GRADLE_USER_HOME = $env:GRADLE_USER_HOME
    MCSV_ANDROID_KEYSTORE = $env:MCSV_ANDROID_KEYSTORE
    MCSV_ANDROID_STORE_PASSWORD = $env:MCSV_ANDROID_STORE_PASSWORD
    MCSV_ANDROID_KEY_ALIAS = $env:MCSV_ANDROID_KEY_ALIAS
    MCSV_ANDROID_KEY_PASSWORD = $env:MCSV_ANDROID_KEY_PASSWORD
    JAVA_TOOL_OPTIONS = $env:JAVA_TOOL_OPTIONS
}
$gradleUserHome = Assert-SafeChildPath $ToolingRoot (Join-Path $ToolingRoot 'android-gradle-user-home')
[IO.Directory]::CreateDirectory($gradleUserHome) | Out-Null
try {
    $env:JAVA_HOME = $toolchain.JavaHome
    $env:ANDROID_HOME = $toolchain.AndroidSdkRoot
    $env:ANDROID_SDK_ROOT = $toolchain.AndroidSdkRoot
    $env:ANDROID_USER_HOME = $toolchain.AndroidUserHome
    $env:ANDROID_PREFS_ROOT = $null
    $env:GRADLE_USER_HOME = $gradleUserHome
    $env:MCSV_ANDROID_KEYSTORE = $keystorePath
    $env:MCSV_ANDROID_STORE_PASSWORD = Get-PlainText $storeSecure
    $env:MCSV_ANDROID_KEY_ALIAS = $credentials.keyAlias
    $env:MCSV_ANDROID_KEY_PASSWORD = Get-PlainText $keySecure
    $env:JAVA_TOOL_OPTIONS = '-Duser.language=en -Duser.country=US -Dfile.encoding=UTF-8'

    Push-Location $androidRoot
    try {
        & $gradlew @(
            '--no-daemon',
            '--stacktrace',
            'clean',
            'testDebugUnitTest',
            'lintRelease',
            'assembleRelease',
            "-PMCSV_VERSION_NAME=$VersionName",
            "-PMCSV_VERSION_CODE=$VersionCode"
        ) 2>&1 | ForEach-Object { Write-Host $_ }
        if ($LASTEXITCODE -ne 0) {
            throw 'Android unit tests, lint, or signed Release assembly failed.'
        }
    }
    finally {
        Pop-Location
    }

    $apkCandidates = @(Get-ChildItem -LiteralPath (Join-Path $androidRoot 'app\build\outputs\apk\release') `
        -Filter '*.apk' -File)
    if ($apkCandidates.Count -ne 1 -or $apkCandidates[0].Name -ne 'app-release.apk') {
        throw 'Android release build did not produce exactly one signed release APK.'
    }
    $builtApk = $apkCandidates[0].FullName
    $builtV4Signature = "$builtApk.idsig"
    if (-not (Test-Path -LiteralPath $builtV4Signature -PathType Leaf) -or
        (Get-Item -LiteralPath $builtV4Signature).Length -lt 1 -or
        (Get-Item -LiteralPath $builtV4Signature).Length -gt 16MB -or
        (((Get-Item -LiteralPath $builtV4Signature -Force).Attributes -band
            [IO.FileAttributes]::ReparsePoint) -ne 0)) {
        throw 'Android release build did not produce a safe v4 .idsig signature.'
    }
    $verification = Invoke-NativeCapture $toolchain.ApkSigner @(
        'verify', '--verbose', '--print-certs',
        '-v4-signature-file', $builtV4Signature,
        $builtApk)
    $verificationText = $verification -join "`n"
    if ($verificationText -notmatch 'Verified using v2 scheme \(APK Signature Scheme v2\): true' -or
        $verificationText -notmatch 'Verified using v3 scheme \(APK Signature Scheme v3\): true' -or
        $verificationText -notmatch 'Verified using v4 scheme \(APK Signature Scheme v4\): true') {
        throw 'APK is missing required v2/v3/v4 release signatures.'
    }
    $certificateMatches = [regex]::Matches(
        $verificationText,
        '(?im)certificate SHA-256 digest:\s*([0-9a-f]{64})')
    $certificateDigests = @($certificateMatches | ForEach-Object {
        $_.Groups[1].Value.ToLowerInvariant()
    } | Sort-Object -Unique)
    if ($certificateDigests.Count -ne 1 -or
        $certificateDigests[0] -ne $credentials.signingCertificateSha256) {
        throw 'APK signing certificate does not match the protected local release identity.'
    }

    $badging = (Invoke-NativeCapture $toolchain.Aapt2 @('dump', 'badging', $builtApk)) -join "`n"
    $escapedVersion = [regex]::Escape($VersionName)
    if ($badging -notmatch "package: name='$([regex]::Escape($packageId))'" -or
        $badging -notmatch "versionCode='$VersionCode'" -or
        $badging -notmatch "versionName='$escapedVersion'") {
        throw 'APK package or version identity does not match the requested release.'
    }

    $mobileRoot = Join-Path $StagingRoot 'mobile'
    [IO.Directory]::CreateDirectory($mobileRoot) | Out-Null
    $artifactPath = Join-Path $mobileRoot 'Muhun-MCSV-Remote.apk'
    $artifactPartial = Join-Path $mobileRoot ".Muhun-MCSV-Remote.apk.$PID.partial"
    $v4ArtifactPath = Join-Path $mobileRoot 'Muhun-MCSV-Remote.apk.idsig'
    $v4ArtifactPartial = Join-Path $mobileRoot ".Muhun-MCSV-Remote.apk.idsig.$PID.partial"
    [IO.File]::Copy($builtApk, $artifactPartial, $true)
    [IO.File]::Move($artifactPartial, $artifactPath, $true)
    [IO.File]::Copy($builtV4Signature, $v4ArtifactPartial, $true)
    [IO.File]::Move($v4ArtifactPartial, $v4ArtifactPath, $true)
    $artifact = Get-Item -LiteralPath $artifactPath
    $v4Artifact = Get-Item -LiteralPath $v4ArtifactPath
    $artifactSha256 = Get-Sha256 $artifactPath
    $v4ArtifactSha256 = Get-Sha256 $v4ArtifactPath

    $metadata = [ordered]@{
        schemaVersion = 2
        productId = $productId
        packageId = $packageId
        version = $VersionName
        sizeBytes = $artifact.Length
        sha256 = $artifactSha256
        signingCertificateSha256 = $certificateDigests[0]
        v4SignatureFileName = $v4Artifact.Name
        v4SignatureSizeBytes = $v4Artifact.Length
        v4SignatureSha256 = $v4ArtifactSha256
        verifiedSignatureSchemes = @('v2', 'v3', 'v4')
    }
    $metadataPath = Join-Path $mobileRoot 'android-release.v2.json'
    $metadataPartial = Join-Path $mobileRoot ".android-release.v2.json.$PID.partial"
    [IO.File]::WriteAllText(
        $metadataPartial,
        (($metadata | ConvertTo-Json -Depth 3) + "`n"),
        [Text.UTF8Encoding]::new($false))
    [IO.File]::Move($metadataPartial, $metadataPath, $true)

    [PSCustomObject]@{
        Apk = $artifactPath
        V4Signature = $v4ArtifactPath
        Metadata = $metadataPath
        PackageId = $packageId
        Version = $VersionName
        VersionCode = $VersionCode
        SizeBytes = $artifact.Length
        Sha256 = $artifactSha256
        SigningCertificateSha256 = $certificateDigests[0]
        V4SignatureSizeBytes = $v4Artifact.Length
        V4SignatureSha256 = $v4ArtifactSha256
        UnitTestReport = (Join-Path $androidRoot 'app\build\reports\tests\testDebugUnitTest\index.html')
        LintReport = (Join-Path $androidRoot 'app\build\reports\lint-results-release.html')
    }
}
finally {
    foreach ($name in $savedEnvironment.Keys) {
        [Environment]::SetEnvironmentVariable($name, $savedEnvironment[$name], 'Process')
    }
}
