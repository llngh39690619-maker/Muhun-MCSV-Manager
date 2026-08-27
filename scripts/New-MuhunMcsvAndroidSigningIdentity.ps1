[CmdletBinding()]
param(
    [string]$ToolingRoot = (Join-Path $PSScriptRoot '..\..\tooling'),
    [string]$SigningRoot,
    [string]$KeyAlias = 'muhun-mcsv-remote'
)

Set-StrictMode -Version Latest
$ErrorActionPreference = 'Stop'
$ProgressPreference = 'SilentlyContinue'
$PSNativeCommandUseErrorActionPreference = $false

if (-not $IsWindows) {
    throw 'Android release credentials use Windows DPAPI and can only be created on Windows.'
}
if ($KeyAlias -notmatch '^[a-z][a-z0-9-]{2,31}$') {
    throw 'Android signing alias is invalid.'
}

$ToolingRoot = [IO.Path]::GetFullPath($ToolingRoot)
if ([string]::IsNullOrWhiteSpace($SigningRoot)) {
    $SigningRoot = Join-Path $ToolingRoot 'android-signing'
}
$SigningRoot = [IO.Path]::GetFullPath($SigningRoot)
$safeToolingPrefix = $ToolingRoot.TrimEnd('\') + '\'
if (-not $SigningRoot.StartsWith($safeToolingPrefix, [StringComparison]::OrdinalIgnoreCase)) {
    throw 'Android signing directory must remain inside the dedicated workspace tooling root.'
}

$initializer = Join-Path $PSScriptRoot 'Initialize-MuhunMcsvAndroidToolchain.ps1'
$toolchain = & $initializer -ToolingRoot $ToolingRoot -JavaOnly
$keystorePath = Join-Path $SigningRoot 'muhun-mcsv-remote-release.p12'
$credentialsPath = Join-Path $SigningRoot 'android-signing.v1.dpapi.json'
if (Test-Path -LiteralPath $keystorePath -PathType Leaf) {
    throw "Android release private key already exists and will not be overwritten: $keystorePath"
}
if (Test-Path -LiteralPath $credentialsPath -PathType Leaf) {
    throw "Android DPAPI credential record already exists and will not be overwritten: $credentialsPath"
}

[IO.Directory]::CreateDirectory($SigningRoot) | Out-Null
$signingItem = Get-Item -LiteralPath $SigningRoot -Force
if (($signingItem.Attributes -band [IO.FileAttributes]::ReparsePoint) -ne 0) {
    throw 'Android signing directory cannot be a reparse point.'
}
$currentSid = [Security.Principal.WindowsIdentity]::GetCurrent().User.Value
& icacls.exe $SigningRoot /inheritance:r /grant:r `
    "*$currentSid`:(OI)(CI)F" '*S-1-5-18:(OI)(CI)F' /T /C 2>&1 |
    ForEach-Object { Write-Host $_ }
if ($LASTEXITCODE -ne 0) {
    throw 'Could not restrict the Android signing directory ACL.'
}

$randomBytes = [byte[]]::new(36)
[Security.Cryptography.RandomNumberGenerator]::Fill($randomBytes)
$password = [Convert]::ToBase64String($randomBytes).TrimEnd('=').Replace('+', '-').Replace('/', '_')
$securePassword = ConvertTo-SecureString $password -AsPlainText -Force
$encryptedStorePassword = ConvertFrom-SecureString $securePassword
$encryptedKeyPassword = ConvertFrom-SecureString $securePassword
$temporaryKeystore = Join-Path $SigningRoot ".release-key.$PID.$([Guid]::NewGuid().ToString('N')).tmp"
$temporaryCredentials = Join-Path $SigningRoot ".release-credentials.$PID.$([Guid]::NewGuid().ToString('N')).tmp"
$certificatePath = Join-Path $SigningRoot ".release-certificate.$PID.$([Guid]::NewGuid().ToString('N')).cer"

$savedStorePassword = $env:MCSV_ANDROID_NEW_STORE_PASSWORD
$savedKeyPassword = $env:MCSV_ANDROID_NEW_KEY_PASSWORD
$keytool = $toolchain.Keytool
try {
    $env:MCSV_ANDROID_NEW_STORE_PASSWORD = $password
    $env:MCSV_ANDROID_NEW_KEY_PASSWORD = $password
    & $keytool @(
        '-genkeypair',
        '-keystore', $temporaryKeystore,
        '-storetype', 'PKCS12',
        '-storepass:env', 'MCSV_ANDROID_NEW_STORE_PASSWORD',
        '-keypass:env', 'MCSV_ANDROID_NEW_KEY_PASSWORD',
        '-alias', $KeyAlias,
        '-keyalg', 'RSA',
        '-keysize', '3072',
        '-sigalg', 'SHA256withRSA',
        '-validity', '36500',
        '-dname', 'CN=Muhun MCSV Remote, OU=Product Release, O=Muhun, C=TW',
        '-noprompt'
    ) 2>&1 | ForEach-Object { Write-Host $_ }
    if ($LASTEXITCODE -ne 0) {
        throw 'Java keytool could not create the Android release identity.'
    }

    & $keytool @(
        '-exportcert',
        '-keystore', $temporaryKeystore,
        '-storetype', 'PKCS12',
        '-storepass:env', 'MCSV_ANDROID_NEW_STORE_PASSWORD',
        '-alias', $KeyAlias,
        '-file', $certificatePath
    ) 2>&1 | ForEach-Object { Write-Host $_ }
    if ($LASTEXITCODE -ne 0) {
        throw 'Java keytool could not export the Android release certificate.'
    }

    $certificate = [Security.Cryptography.X509Certificates.X509Certificate2]::new(
        [IO.File]::ReadAllBytes($certificatePath))
    try {
        if (-not $certificate.HasPrivateKey -and
            $certificate.NotAfter.ToUniversalTime() -gt [DateTime]::UtcNow.AddYears(20)) {
            $certificateSha256 = $certificate.GetCertHashString(
                [Security.Cryptography.HashAlgorithmName]::SHA256).ToLowerInvariant()
        }
        else {
            throw 'Generated Android signing certificate failed its identity or lifetime check.'
        }
    }
    finally {
        $certificate.Dispose()
    }

    $record = [ordered]@{
        schemaVersion = 1
        keyStoreFile = 'muhun-mcsv-remote-release.p12'
        keyStoreType = 'PKCS12'
        keyAlias = $KeyAlias
        encryptedStorePassword = $encryptedStorePassword
        encryptedKeyPassword = $encryptedKeyPassword
        signingCertificateSha256 = $certificateSha256
        createdAtUtc = [DateTimeOffset]::UtcNow.ToString('O')
    }
    $json = $record | ConvertTo-Json -Depth 3
    [IO.File]::WriteAllText(
        $temporaryCredentials,
        $json + "`n",
        [Text.UTF8Encoding]::new($false))
    [IO.File]::Move($temporaryKeystore, $keystorePath, $false)
    [IO.File]::Move($temporaryCredentials, $credentialsPath, $false)

    foreach ($protectedFile in @($keystorePath, $credentialsPath)) {
        & icacls.exe $protectedFile /reset 2>&1 | ForEach-Object { Write-Host $_ }
        if ($LASTEXITCODE -ne 0) {
            throw "Could not inherit the protected Android signing ACL: $protectedFile"
        }
    }

    [PSCustomObject]@{
        SigningRoot = $SigningRoot
        KeyStore = $keystorePath
        Credentials = $credentialsPath
        KeyAlias = $KeyAlias
        SigningCertificateSha256 = $certificateSha256
    }
}
finally {
    [Environment]::SetEnvironmentVariable(
        'MCSV_ANDROID_NEW_STORE_PASSWORD',
        $savedStorePassword,
        'Process')
    [Environment]::SetEnvironmentVariable(
        'MCSV_ANDROID_NEW_KEY_PASSWORD',
        $savedKeyPassword,
        'Process')
    $password = $null
    [Array]::Clear($randomBytes, 0, $randomBytes.Length)
    foreach ($temporary in @($temporaryKeystore, $temporaryCredentials, $certificatePath)) {
        if (Test-Path -LiteralPath $temporary -PathType Leaf) {
            Remove-Item -LiteralPath $temporary -Force
        }
    }
}
