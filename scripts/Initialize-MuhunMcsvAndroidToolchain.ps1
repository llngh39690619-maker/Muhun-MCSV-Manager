[CmdletBinding()]
param(
    [string]$ToolingRoot = (Join-Path $PSScriptRoot '..\..\tooling'),
    [switch]$AcceptAndroidSdkLicenses,
    [switch]$JavaOnly
)

Set-StrictMode -Version Latest
$ErrorActionPreference = 'Stop'
$ProgressPreference = 'SilentlyContinue'
$PSNativeCommandUseErrorActionPreference = $false

$commandLineToolsVersion = '15859902'
$commandLineToolsUri = 'https://dl.google.com/android/repository/commandlinetools-win-15859902_latest.zip'
$commandLineToolsSha256 = '90ae805d20434428bffcb699c290860f19bb5f66a67e6b330067e3de801fb04a'
$gradleVersion = '9.5.0'
$gradleUri = 'https://services.gradle.org/distributions/gradle-9.5.0-bin.zip'
$gradleSha256 = '553c78f50dafcd54d65b9a444649057857469edf836431389695608536d6b746'
$jdkVersion = '17.0.20.1'
$jdkUri = 'https://aka.ms/download-jdk/microsoft-jdk-17.0.20.1-windows-x64.zip'
$jdkSha256 = '3d9006956fc8af5601cd24ffc4f468bef48279c7ebd8171b9bdf90d0aabfbf1f'

function Resolve-FullPath([string]$Path) {
    return [IO.Path]::GetFullPath($Path)
}

function Assert-SafeChildPath([string]$Root, [string]$Path) {
    $fullRoot = (Resolve-FullPath $Root).TrimEnd('\') + '\'
    $fullPath = Resolve-FullPath $Path
    if (-not $fullPath.StartsWith($fullRoot, [StringComparison]::OrdinalIgnoreCase)) {
        throw "Android tooling path escaped its dedicated root: $fullPath"
    }
    return $fullPath
}

function Assert-NoReparsePath([string]$Root, [string]$Path) {
    $fullRoot = Resolve-FullPath $Root
    $fullPath = Assert-SafeChildPath $fullRoot $Path
    $current = Get-Item -LiteralPath $fullPath -Force -ErrorAction SilentlyContinue
    while ($null -ne $current) {
        if (($current.Attributes -band [IO.FileAttributes]::ReparsePoint) -ne 0) {
            throw "Android tooling path cannot traverse a reparse point: $($current.FullName)"
        }
        if ($current.FullName.TrimEnd('\').Equals(
                $fullRoot.TrimEnd('\'),
                [StringComparison]::OrdinalIgnoreCase)) {
            return
        }
        $current = $current.Parent
    }
    throw "Android tooling path is outside its dedicated root: $fullPath"
}

function Get-Sha256([string]$Path) {
    return (Get-FileHash -LiteralPath $Path -Algorithm SHA256).Hash.ToLowerInvariant()
}

function Get-VerifiedArchive(
    [string]$Uri,
    [string]$ExpectedHost,
    [string]$ExpectedSha256,
    [string]$Destination,
    [long]$MaximumBytes) {
    $parsed = [Uri]$Uri
    if ($parsed.Scheme -ne 'https' -or
        -not $parsed.DnsSafeHost.Equals($ExpectedHost, [StringComparison]::OrdinalIgnoreCase)) {
        throw "Tool archive URI is not an approved official HTTPS origin: $Uri"
    }
    if ($ExpectedSha256 -notmatch '^[a-f0-9]{64}$') {
        throw 'Pinned tool archive SHA-256 is malformed.'
    }

    if (Test-Path -LiteralPath $Destination -PathType Leaf) {
        $existing = Get-Item -LiteralPath $Destination
        if ($existing.Length -lt 1 -or $existing.Length -gt $MaximumBytes -or
            (Get-Sha256 $Destination) -ne $ExpectedSha256) {
            throw "Cached tool archive failed its pinned SHA-256 check: $Destination"
        }
        return
    }

    $partial = "$Destination.partial.$PID.$([Guid]::NewGuid().ToString('N'))"
    try {
        Invoke-WebRequest -UseBasicParsing -Uri $Uri -OutFile $partial -MaximumRedirection 8
        $downloaded = Get-Item -LiteralPath $partial
        if ($downloaded.Length -lt 1 -or $downloaded.Length -gt $MaximumBytes) {
            throw "Downloaded tool archive has an invalid size: $Uri"
        }
        if ((Get-Sha256 $partial) -ne $ExpectedSha256) {
            throw "Downloaded tool archive failed its pinned SHA-256 check: $Uri"
        }
        [IO.File]::Move($partial, $Destination, $false)
    }
    finally {
        if (Test-Path -LiteralPath $partial -PathType Leaf) {
            Remove-Item -LiteralPath $partial -Force
        }
    }
}

function Expand-VerifiedRoot(
    [string]$Archive,
    [string]$ExpectedRelativeRoot,
    [string]$Destination,
    [string]$RequiredRelativeFile) {
    if (Test-Path -LiteralPath (Join-Path $Destination $RequiredRelativeFile) -PathType Leaf) {
        return
    }
    if (Test-Path -LiteralPath $Destination) {
        throw "Incomplete Android tool directory already exists: $Destination"
    }

    $stage = Assert-SafeChildPath $ToolingRoot (Join-Path $ToolingRoot ".android-expand-$([Guid]::NewGuid().ToString('N'))")
    try {
        [IO.Directory]::CreateDirectory($stage) | Out-Null
        Expand-Archive -LiteralPath $Archive -DestinationPath $stage
        $source = Assert-SafeChildPath $stage (Join-Path $stage $ExpectedRelativeRoot)
        if (-not (Test-Path -LiteralPath (Join-Path $source $RequiredRelativeFile) -PathType Leaf)) {
            throw "Verified archive did not contain its expected tool layout: $Archive"
        }
        $null = Assert-SafeChildPath $ToolingRoot $Destination
        Move-Item -LiteralPath $source -Destination $Destination
    }
    finally {
        if (Test-Path -LiteralPath $stage -PathType Container) {
            $null = Assert-SafeChildPath $ToolingRoot $stage
            Remove-Item -LiteralPath $stage -Recurse -Force
        }
    }
}

function Invoke-NativeChecked([string]$FilePath, [string[]]$Arguments) {
    & $FilePath @Arguments 2>&1 | ForEach-Object { Write-Host $_ }
    if ($LASTEXITCODE -ne 0) {
        throw "Android tool failed with exit code ${LASTEXITCODE}: $FilePath"
    }
}

if (-not $IsWindows) {
    throw 'The pinned Android release toolchain currently supports Windows x64 only.'
}

$ToolingRoot = Resolve-FullPath $ToolingRoot
[IO.Directory]::CreateDirectory($ToolingRoot) | Out-Null
if ((Get-Item -LiteralPath $ToolingRoot -Force).Attributes -band [IO.FileAttributes]::ReparsePoint) {
    throw 'Android tooling root cannot be a reparse point.'
}
$downloads = Assert-SafeChildPath $ToolingRoot (Join-Path $ToolingRoot 'downloads')
[IO.Directory]::CreateDirectory($downloads) | Out-Null
Assert-NoReparsePath $ToolingRoot $downloads

$jdkArchive = Join-Path $downloads "microsoft-jdk-$jdkVersion-windows-x64.zip"
Get-VerifiedArchive $jdkUri 'aka.ms' $jdkSha256 $jdkArchive 500MB
$javaHome = Assert-SafeChildPath $ToolingRoot (Join-Path $ToolingRoot "microsoft-jdk-$jdkVersion-windows-x64")
Expand-VerifiedRoot $jdkArchive "jdk-$jdkVersion+1" $javaHome 'bin\java.exe'
$java = Join-Path $javaHome 'bin\java.exe'
$keytool = Join-Path $javaHome 'bin\keytool.exe'
if (-not (Test-Path -LiteralPath $java -PathType Leaf) -or
    -not (Test-Path -LiteralPath $keytool -PathType Leaf)) {
    throw 'Pinned JDK extraction is incomplete.'
}
$javaVersionOutput = (& $java -version 2>&1) -join "`n"
if ($LASTEXITCODE -ne 0 -or $javaVersionOutput -notmatch '17\.0\.20\.1') {
    throw 'Extracted Java runtime identity does not match pinned JDK 17.0.20.1.'
}

if ($JavaOnly) {
    [PSCustomObject]@{
        ToolingRoot = $ToolingRoot
        JavaHome = $javaHome
        Java = $java
        Keytool = $keytool
        JdkSha256 = $jdkSha256
    }
    return
}

$gradleArchive = Join-Path $downloads "gradle-$gradleVersion-bin.zip"
Get-VerifiedArchive $gradleUri 'services.gradle.org' $gradleSha256 $gradleArchive 250MB
$gradleHome = Assert-SafeChildPath $ToolingRoot (Join-Path $ToolingRoot "gradle-$gradleVersion")
Expand-VerifiedRoot $gradleArchive "gradle-$gradleVersion" $gradleHome 'bin\gradle.bat'
$gradle = Join-Path $gradleHome 'bin\gradle.bat'

$commandLineArchive = Join-Path $downloads "commandlinetools-win-$commandLineToolsVersion.zip"
Get-VerifiedArchive $commandLineToolsUri 'dl.google.com' $commandLineToolsSha256 $commandLineArchive 250MB
$androidSdkRoot = Assert-SafeChildPath $ToolingRoot (Join-Path $ToolingRoot 'android-sdk')
[IO.Directory]::CreateDirectory($androidSdkRoot) | Out-Null
Assert-NoReparsePath $ToolingRoot $androidSdkRoot
$commandLineRoot = Assert-SafeChildPath $ToolingRoot (Join-Path $androidSdkRoot 'cmdline-tools\latest')
if (-not (Test-Path -LiteralPath (Join-Path $commandLineRoot 'bin\sdkmanager.bat') -PathType Leaf)) {
    [IO.Directory]::CreateDirectory((Split-Path -Parent $commandLineRoot)) | Out-Null
    Expand-VerifiedRoot $commandLineArchive 'cmdline-tools' $commandLineRoot 'bin\sdkmanager.bat'
}
$sdkManager = Join-Path $commandLineRoot 'bin\sdkmanager.bat'

$savedEnvironment = @{
    JAVA_HOME = $env:JAVA_HOME
    ANDROID_HOME = $env:ANDROID_HOME
    ANDROID_SDK_ROOT = $env:ANDROID_SDK_ROOT
    ANDROID_USER_HOME = $env:ANDROID_USER_HOME
    ANDROID_PREFS_ROOT = $env:ANDROID_PREFS_ROOT
}
$androidUserHome = Assert-SafeChildPath $ToolingRoot (Join-Path $ToolingRoot 'android-user-home')
[IO.Directory]::CreateDirectory($androidUserHome) | Out-Null
try {
    $env:JAVA_HOME = $javaHome
    $env:ANDROID_HOME = $androidSdkRoot
    $env:ANDROID_SDK_ROOT = $androidSdkRoot
    $env:ANDROID_USER_HOME = $androidUserHome
    $env:ANDROID_PREFS_ROOT = $null

    if ($AcceptAndroidSdkLicenses) {
        ((1..100 | ForEach-Object { 'y' }) -join "`n") |
            & $sdkManager "--sdk_root=$androidSdkRoot" --licenses 2>&1 |
            ForEach-Object { Write-Host $_ }
        if ($LASTEXITCODE -ne 0) {
            throw 'Android SDK license acceptance failed.'
        }
    }

    Invoke-NativeChecked $sdkManager @(
        "--sdk_root=$androidSdkRoot",
        '--channel=3',
        'platforms;android-37.0',
        'build-tools;36.0.0'
    )
}
finally {
    foreach ($name in $savedEnvironment.Keys) {
        [Environment]::SetEnvironmentVariable($name, $savedEnvironment[$name], 'Process')
    }
}

$androidJar = Join-Path $androidSdkRoot 'platforms\android-37.0\android.jar'
$buildTools = Join-Path $androidSdkRoot 'build-tools\36.0.0'
$apksigner = Join-Path $buildTools 'apksigner.bat'
$aapt2 = Join-Path $buildTools 'aapt2.exe'
foreach ($required in @($gradle, $sdkManager, $androidJar, $apksigner, $aapt2)) {
    if (-not (Test-Path -LiteralPath $required -PathType Leaf)) {
        throw "Android toolchain is incomplete: $required"
    }
}

[PSCustomObject]@{
    ToolingRoot = $ToolingRoot
    JavaHome = $javaHome
    Java = $java
    Keytool = $keytool
    GradleHome = $gradleHome
    Gradle = $gradle
    GradleArchive = $gradleArchive
    GradleSha256 = $gradleSha256
    AndroidSdkRoot = $androidSdkRoot
    AndroidUserHome = $androidUserHome
    SdkManager = $sdkManager
    BuildTools = $buildTools
    ApkSigner = $apksigner
    Aapt2 = $aapt2
    CommandLineToolsSha256 = $commandLineToolsSha256
    JdkSha256 = $jdkSha256
}
