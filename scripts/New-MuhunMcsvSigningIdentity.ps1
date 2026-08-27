#requires -Version 7.4

[CmdletBinding()]
param(
    [Parameter(Mandatory = $true)]
    [string]$OutputDirectory,

    [ValidateRange(3072, 8192)]
    [int]$KeySize = 4096,

    [ValidateRange(1, 5)]
    [int]$ValidityYears = 3,

    [ValidatePattern('^CN=')]
    [string]$Subject = 'CN=Muhun MCSV Manager Release Signing, O=Muhun'
)

$ErrorActionPreference = 'Stop'
Set-StrictMode -Version Latest

if (-not $IsWindows) {
    throw 'The Muhun MCSV signing identity must be generated on Windows.'
}

$projectRoot = [IO.Path]::GetFullPath((Split-Path -Parent $PSScriptRoot)).TrimEnd('\', '/')
$identityRoot = [IO.Path]::GetFullPath($OutputDirectory).TrimEnd('\', '/')
$directoryRoot = [IO.Path]::GetPathRoot($identityRoot).TrimEnd('\', '/')
if ([string]::IsNullOrWhiteSpace($identityRoot) -or
    [string]::Equals($identityRoot, $directoryRoot, [StringComparison]::OrdinalIgnoreCase) -or
    $identityRoot.StartsWith('\\', [StringComparison]::Ordinal)) {
    throw 'OutputDirectory must be a dedicated local directory, not a drive root or UNC path.'
}

$normalizedProjectRoot = $projectRoot + [IO.Path]::DirectorySeparatorChar
$normalizedIdentityRoot = $identityRoot + [IO.Path]::DirectorySeparatorChar
if ($normalizedIdentityRoot.StartsWith($normalizedProjectRoot, [StringComparison]::OrdinalIgnoreCase)) {
    throw 'Private signing material must be generated outside the source repository.'
}

if (Test-Path -LiteralPath $identityRoot) {
    if (-not (Test-Path -LiteralPath $identityRoot -PathType Container) -or
        (Get-ChildItem -LiteralPath $identityRoot -Force | Select-Object -First 1)) {
        throw 'OutputDirectory must be new or empty. Existing signing material is never overwritten.'
    }
} else {
    New-Item -ItemType Directory -Path $identityRoot | Out-Null
}

function Set-PrivateDirectoryAcl {
    param([Parameter(Mandatory = $true)][string]$Path)

    $currentSid = [Security.Principal.WindowsIdentity]::GetCurrent().User.Value
    $icacls = Join-Path $env:SystemRoot 'System32\icacls.exe'
    $output = & $icacls $Path '/inheritance:r' '/grant:r' `
        "*${currentSid}:(OI)(CI)F" '*S-1-5-18:(OI)(CI)F' 2>&1
    if ($LASTEXITCODE -ne 0) {
        throw "Unable to restrict the signing identity directory ACL: $($output -join ' ')"
    }
}

function Write-AtomicBytes {
    param(
        [Parameter(Mandatory = $true)][string]$Path,
        [Parameter(Mandatory = $true)][byte[]]$Bytes
    )

    $temporaryPath = Join-Path ([IO.Path]::GetDirectoryName($Path)) `
        ".$([IO.Path]::GetFileName($Path)).$([guid]::NewGuid().ToString('N')).tmp"
    try {
        $stream = [IO.FileStream]::new(
            $temporaryPath,
            [IO.FileMode]::CreateNew,
            [IO.FileAccess]::Write,
            [IO.FileShare]::None,
            4096,
            [IO.FileOptions]::WriteThrough)
        try {
            $stream.Write($Bytes, 0, $Bytes.Length)
            $stream.Flush($true)
        } finally {
            $stream.Dispose()
        }

        [IO.File]::Move($temporaryPath, $Path, $false)
    } finally {
        if (Test-Path -LiteralPath $temporaryPath -PathType Leaf) {
            Remove-Item -LiteralPath $temporaryPath -Force
        }
    }
}

function Write-AtomicUtf8Text {
    param(
        [Parameter(Mandatory = $true)][string]$Path,
        [Parameter(Mandatory = $true)][string]$Value
    )

    Write-AtomicBytes -Path $Path -Bytes ([Text.UTF8Encoding]::new($false).GetBytes($Value))
}

Set-PrivateDirectoryAcl -Path $identityRoot

$rsa = $null
$providerEcdsa = $null
$certificate = $null
$roundTripCertificate = $null
$passwordText = $null
try {
    $randomPassword = [byte[]]::new(48)
    [Security.Cryptography.RandomNumberGenerator]::Fill($randomPassword)
    $passwordText = [Convert]::ToBase64String($randomPassword)
    [Array]::Clear($randomPassword, 0, $randomPassword.Length)

    $rsa = [Security.Cryptography.RSA]::Create()
    $rsa.KeySize = $KeySize
    if ($rsa.KeySize -lt 3072) {
        throw 'The cryptographic provider did not create an RSA key of at least 3072 bits.'
    }

    $request = [Security.Cryptography.X509Certificates.CertificateRequest]::new(
        $Subject,
        $rsa,
        [Security.Cryptography.HashAlgorithmName]::SHA256,
        [Security.Cryptography.RSASignaturePadding]::Pkcs1)
    $request.CertificateExtensions.Add(
        [Security.Cryptography.X509Certificates.X509BasicConstraintsExtension]::new($false, $false, 0, $true))
    $request.CertificateExtensions.Add(
        [Security.Cryptography.X509Certificates.X509KeyUsageExtension]::new(
            [Security.Cryptography.X509Certificates.X509KeyUsageFlags]::DigitalSignature,
            $true))
    $codeSigningOids = [Security.Cryptography.OidCollection]::new()
    [void]$codeSigningOids.Add([Security.Cryptography.Oid]::new('1.3.6.1.5.5.7.3.3', 'Code Signing'))
    $request.CertificateExtensions.Add(
        [Security.Cryptography.X509Certificates.X509EnhancedKeyUsageExtension]::new($codeSigningOids, $true))
    $request.CertificateExtensions.Add(
        [Security.Cryptography.X509Certificates.X509SubjectKeyIdentifierExtension]::new($request.PublicKey, $false))

    $notBefore = [DateTimeOffset]::UtcNow.AddMinutes(-5)
    $notAfter = $notBefore.AddYears($ValidityYears)
    $certificate = $request.CreateSelfSigned($notBefore, $notAfter)
    if (-not $certificate.HasPrivateKey) {
        throw 'The generated release certificate does not contain its private key.'
    }

    $certificateSha256 = [Convert]::ToHexString(
        [Security.Cryptography.SHA256]::HashData($certificate.RawData)).ToLowerInvariant()
    $keyId = "muhun.release.$($certificateSha256.Substring(0, 16))"
    $subjectPublicKeyInfo = $rsa.ExportSubjectPublicKeyInfo()
    $subjectPublicKeyInfoSha256 = [Convert]::ToHexString(
        [Security.Cryptography.SHA256]::HashData($subjectPublicKeyInfo)).ToLowerInvariant()

    $pfxBytes = $certificate.Export(
        [Security.Cryptography.X509Certificates.X509ContentType]::Pfx,
        $passwordText)
    $cerBytes = $certificate.Export(
        [Security.Cryptography.X509Certificates.X509ContentType]::Cert)
    $securePassword = ConvertTo-SecureString -String $passwordText -AsPlainText -Force
    $protectedPassword = ConvertFrom-SecureString -SecureString $securePassword

    Write-AtomicBytes -Path (Join-Path $identityRoot 'muhun-mcsv-release-signing.pfx') -Bytes $pfxBytes
    Write-AtomicUtf8Text -Path (Join-Path $identityRoot 'pfx-password.dpapi') `
        -Value ($protectedPassword + [Environment]::NewLine)
    Write-AtomicBytes -Path (Join-Path $identityRoot 'muhun-mcsv-release-signing.cer') -Bytes $cerBytes

    $publicKeyDocument = [ordered]@{
        schemaVersion = 1
        productId = 'muhun.mcsv.manager'
        keyId = $keyId
        signatureAlgorithm = 'rsa-pss-sha256'
        keyAlgorithm = 'RSA'
        keySize = $rsa.KeySize
        subjectPublicKeyInfoSha256 = $subjectPublicKeyInfoSha256
        subjectPublicKeyInfo = [Convert]::ToBase64String($subjectPublicKeyInfo)
        publisherCertificateSha256 = $certificateSha256
        publisherCertificateSubject = $certificate.Subject
        notBeforeUtc = $certificate.NotBefore.ToUniversalTime().ToString('O')
        notAfterUtc = $certificate.NotAfter.ToUniversalTime().ToString('O')
    }
    Write-AtomicUtf8Text -Path (Join-Path $identityRoot 'update-signing-public-key.json') `
        -Value (($publicKeyDocument | ConvertTo-Json -Depth 4 -Compress) + [Environment]::NewLine)

    $pemBody = [Convert]::ToBase64String($subjectPublicKeyInfo, [Base64FormattingOptions]::InsertLineBreaks)
    $pem = "-----BEGIN PUBLIC KEY-----`n$pemBody`n-----END PUBLIC KEY-----`n"
    Write-AtomicUtf8Text -Path (Join-Path $identityRoot 'update-signing-public-key.pem') -Value $pem

    # Provider packages use a protocol-specific P-256 key. Keeping it separate from the
    # Authenticode/update RSA identity prevents a signature from being replayed across trust
    # domains and permits rotating a provider publisher without replacing the product publisher.
    $providerPasswordBytes = [byte[]]::new(48)
    [Security.Cryptography.RandomNumberGenerator]::Fill($providerPasswordBytes)
    $providerPasswordText = [Convert]::ToBase64String($providerPasswordBytes)
    [Security.Cryptography.CryptographicOperations]::ZeroMemory($providerPasswordBytes)
    $providerEcdsa = [Security.Cryptography.ECDsa]::Create(
        [Security.Cryptography.ECCurve]::CreateFromFriendlyName('nistP256'))
    if ($providerEcdsa.KeySize -ne 256) {
        throw 'The cryptographic provider did not create an ECDSA P-256 key.'
    }
    $providerPbe = [Security.Cryptography.PbeParameters]::new(
        [Security.Cryptography.PbeEncryptionAlgorithm]::Aes256Cbc,
        [Security.Cryptography.HashAlgorithmName]::SHA256,
        210000)
    $providerPrivatePem = $providerEcdsa.ExportEncryptedPkcs8PrivateKeyPem(
        $providerPasswordText,
        $providerPbe)
    $providerPublicSpki = $providerEcdsa.ExportSubjectPublicKeyInfo()
    $providerPublicPem = $providerEcdsa.ExportSubjectPublicKeyInfoPem()
    $providerPublicKeySha256 = [Convert]::ToHexString(
        [Security.Cryptography.SHA256]::HashData($providerPublicSpki)).ToLowerInvariant()
    $providerSecurePassword = ConvertTo-SecureString -String $providerPasswordText -AsPlainText -Force
    $providerProtectedPassword = ConvertFrom-SecureString -SecureString $providerSecurePassword
    Write-AtomicUtf8Text -Path (Join-Path $identityRoot 'provider-signing-private-key.pem') `
        -Value ($providerPrivatePem + [Environment]::NewLine)
    Write-AtomicUtf8Text -Path (Join-Path $identityRoot 'provider-key-password.dpapi') `
        -Value ($providerProtectedPassword + [Environment]::NewLine)
    Write-AtomicUtf8Text -Path (Join-Path $identityRoot 'provider-signing-public-key.pem') `
        -Value ($providerPublicPem + [Environment]::NewLine)

    $providerIdentityDocument = [ordered]@{
        schemaVersion = 1
        productId = 'muhun.mcsv.manager'
        publisherId = 'muhun.firstparty'
        algorithm = 'ECDSA-P256-SHA256'
        publicKeySha256 = $providerPublicKeySha256
        createdAtUtc = [DateTimeOffset]::UtcNow.ToString('O')
        passwordProtection = 'windows-dpapi-current-user'
        privateKeyFile = 'provider-signing-private-key.pem'
        passwordFile = 'provider-key-password.dpapi'
        publicKeyFile = 'provider-signing-public-key.pem'
    }
    Write-AtomicUtf8Text -Path (Join-Path $identityRoot 'provider-signing-identity.json') `
        -Value (($providerIdentityDocument | ConvertTo-Json -Depth 4) + [Environment]::NewLine)

    $providerRoundTrip = [Security.Cryptography.ECDsa]::Create()
    try {
        $providerRoundTrip.ImportFromEncryptedPem($providerPrivatePem, $providerPasswordText)
        if ($providerRoundTrip.KeySize -ne 256 -or
            -not [Security.Cryptography.CryptographicOperations]::FixedTimeEquals(
                $providerRoundTrip.ExportSubjectPublicKeyInfo(),
                $providerPublicSpki)) {
            throw 'The exported provider signing identity failed its private-key round-trip check.'
        }
    } finally {
        $providerRoundTrip.Dispose()
        [Security.Cryptography.CryptographicOperations]::ZeroMemory($providerPublicSpki)
        $providerPasswordText = $null
    }

    $identityDocument = [ordered]@{
        schemaVersion = 1
        productId = 'muhun.mcsv.manager'
        identityType = 'self-signed-local-development'
        keyId = $keyId
        keySize = $rsa.KeySize
        publisherCertificateSha256 = $certificateSha256
        createdAtUtc = [DateTimeOffset]::UtcNow.ToString('O')
        passwordProtection = 'windows-dpapi-current-user'
        privateKeyFile = 'muhun-mcsv-release-signing.pfx'
        passwordFile = 'pfx-password.dpapi'
        publicCertificateFile = 'muhun-mcsv-release-signing.cer'
        publicKeyFile = 'update-signing-public-key.json'
        providerSigningIdentityFile = 'provider-signing-identity.json'
        trustStatement = 'Not publicly trusted. Never bypass SmartScreen; use only after explicit local trust or replace with a public-CA code-signing certificate.'
    }
    Write-AtomicUtf8Text -Path (Join-Path $identityRoot 'signing-identity.json') `
        -Value (($identityDocument | ConvertTo-Json -Depth 4) + [Environment]::NewLine)

    $roundTripCertificate = [Security.Cryptography.X509Certificates.X509Certificate2]::new(
        $pfxBytes,
        $passwordText,
        [Security.Cryptography.X509Certificates.X509KeyStorageFlags]::EphemeralKeySet)
    $roundTripRsa = [Security.Cryptography.X509Certificates.RSACertificateExtensions]::GetRSAPrivateKey(
        $roundTripCertificate)
    try {
        if (-not $roundTripCertificate.HasPrivateKey -or $roundTripRsa.KeySize -lt 3072 -or
            -not [Security.Cryptography.CryptographicOperations]::FixedTimeEquals(
                $roundTripRsa.ExportSubjectPublicKeyInfo(),
                $subjectPublicKeyInfo)) {
            throw 'The exported signing identity failed its private-key round-trip check.'
        }
    } finally {
        $roundTripRsa.Dispose()
    }

    Set-PrivateDirectoryAcl -Path $identityRoot
    Write-Host "Created a private signing identity in: $identityRoot"
    Write-Host "Key ID: $keyId"
    Write-Host "Provider publisher key: $providerPublicKeySha256"
    Write-Warning 'This is a self-signed identity. It does not establish public trust or bypass Microsoft SmartScreen.'
} finally {
    if ($null -ne $roundTripCertificate) { $roundTripCertificate.Dispose() }
    if ($null -ne $certificate) { $certificate.Dispose() }
    if ($null -ne $providerEcdsa) { $providerEcdsa.Dispose() }
    if ($null -ne $rsa) { $rsa.Dispose() }
    $passwordText = $null
}
