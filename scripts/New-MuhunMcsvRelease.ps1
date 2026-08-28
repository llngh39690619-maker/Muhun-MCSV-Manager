#requires -Version 7.4

[CmdletBinding()]
param(
    [Parameter(Mandatory = $true)]
    [string]$PayloadDirectory,

    [Parameter(Mandatory = $true)]
    [string]$BuiltinProviderDirectory,

    [Parameter(Mandatory = $true)]
    [string]$MobileArtifactDirectory,

    [Parameter(Mandatory = $true)]
    [string]$AndroidApkSignerPath,

    [Parameter(Mandatory = $true)]
    [string]$AndroidAapt2Path,

    [ValidateRange(1, 999999999)]
    [int]$AndroidVersionCode = 10,

    [Parameter(Mandatory = $true)]
    [string]$OutputDirectory,

    [Parameter(Mandatory = $true)]
    [ValidatePattern('^(0|[1-9][0-9]*)\.(0|[1-9][0-9]*)\.(0|[1-9][0-9]*)(?:-[0-9A-Za-z-]+(?:\.[0-9A-Za-z-]+)*)?$')]
    [string]$Version,

    [Parameter(Mandatory = $true)]
    [uri]$PackageBaseUri,

    [Parameter(Mandatory = $true)]
    [string]$SigningIdentityDirectory,

    [ValidateSet('stable', 'beta')]
    [string]$Channel = 'stable',

    [ValidateSet('self-signed-local', 'public-ca')]
    [string]$PublisherTrustMode = 'self-signed-local',

    [uri]$TimestampServerUrl = 'http://timestamp.digicert.com'
)

$ErrorActionPreference = 'Stop'
Set-StrictMode -Version Latest
$PSNativeCommandUseErrorActionPreference = $false

if (-not $IsWindows) {
    throw 'Muhun MCSV Windows releases must be produced on Windows.'
}

$projectRoot = [IO.Path]::GetFullPath((Split-Path -Parent $PSScriptRoot)).TrimEnd('\', '/')
$payloadRoot = [IO.Path]::GetFullPath($PayloadDirectory).TrimEnd('\', '/')
$builtinProviderRoot = [IO.Path]::GetFullPath($BuiltinProviderDirectory).TrimEnd('\', '/')
$mobileArtifactRoot = [IO.Path]::GetFullPath($MobileArtifactDirectory).TrimEnd('\', '/')
$androidApkSigner = [IO.Path]::GetFullPath($AndroidApkSignerPath)
$androidAapt2 = [IO.Path]::GetFullPath($AndroidAapt2Path)
$androidBuildToolsVersion = '36.0.0'
$pinnedAapt2Sha256 = 'babf3122e515ddb954c5ac4669e085ce990536c035e3072de30127bddd6e3608'
$pinnedApkSignerBatSha256 = '549dd0028b0314a5112d6b56e2de7800e713f297da4508b513a735546e52ce38'
$pinnedApkSignerJarSha256 = '3716d9311e55d2b0918a2fd9d54ba9e406c5f6abeea700b287f11259bc163dec'
$expectedPublisherCertificateSha256 = '1a67e65dc9c367ac3247d0483edbe94dab38c5494859a43210c1ad4719e80b71'
$outputRoot = [IO.Path]::GetFullPath($OutputDirectory).TrimEnd('\', '/')
$identityRoot = [IO.Path]::GetFullPath($SigningIdentityDirectory).TrimEnd('\', '/')

function Test-IsUnderRoot {
    param([string]$Candidate, [string]$Root)
    $normalizedRoot = [IO.Path]::GetFullPath($Root).TrimEnd('\', '/') + [IO.Path]::DirectorySeparatorChar
    $normalizedCandidate = [IO.Path]::GetFullPath($Candidate).TrimEnd('\', '/') + [IO.Path]::DirectorySeparatorChar
    return $normalizedCandidate.StartsWith($normalizedRoot, [StringComparison]::OrdinalIgnoreCase)
}

function Test-SafeRelativePath {
    param([Parameter(Mandatory = $true)][string]$Path)
    if ([string]::IsNullOrWhiteSpace($Path) -or $Path.Length -gt 240 -or
        $Path.Contains('\') -or $Path.StartsWith('/') -or $Path.EndsWith('/') -or
        $Path -match '[\x00-\x1f<>:"|?*]') {
        return $false
    }

    $reserved = '^(?i:CON|PRN|AUX|NUL|COM[1-9]|LPT[1-9])(?:\.|$)'
    foreach ($segment in $Path.Split('/')) {
        if ([string]::IsNullOrWhiteSpace($segment) -or $segment -in @('.', '..') -or
            $segment.EndsWith('.') -or $segment.EndsWith(' ') -or $segment -match $reserved) {
            return $false
        }
    }
    return $true
}

function Get-Sha256Hex {
    param([Parameter(Mandatory = $true)][string]$Path)
    return (Get-FileHash -LiteralPath $Path -Algorithm SHA256).Hash.ToLowerInvariant()
}

function Assert-NoReparseAncestors {
    param(
        [Parameter(Mandatory = $true)][string]$Path,
        [Parameter(Mandatory = $true)][string]$Label
    )
    $cursor = Get-Item -LiteralPath ([IO.Path]::GetFullPath($Path)) -Force -ErrorAction Stop
    while ($null -ne $cursor) {
        if (($cursor.Attributes -band [IO.FileAttributes]::ReparsePoint) -ne 0) {
            throw "$Label traverses a reparse point: $($cursor.FullName)"
        }
        $cursor = if ($cursor -is [IO.DirectoryInfo]) { $cursor.Parent } else { $cursor.Directory }
    }
}

function Assert-ExactJsonProperties {
    param(
        [Parameter(Mandatory = $true)]$Object,
        [Parameter(Mandatory = $true)][string[]]$Expected,
        [Parameter(Mandatory = $true)][string]$Label
    )
    $actual = @($Object.psobject.Properties.Name | Sort-Object)
    $expectedSorted = @($Expected | Sort-Object)
    if ($actual.Count -ne $expectedSorted.Count -or
        @(Compare-Object $expectedSorted $actual).Count -ne 0) {
        throw "$Label JSON property set is invalid."
    }
}

function Get-PinnedAndroidBuildTools {
    $buildToolsRoot = [IO.Path]::GetFullPath((Split-Path -Parent $androidAapt2)).TrimEnd('\', '/')
    if ((Split-Path -Leaf $buildToolsRoot) -ne $androidBuildToolsVersion) {
        throw 'Android build-tools directory does not match the pinned version.'
    }
    $expected = @(
        [pscustomobject]@{
            RelativePath = 'aapt2.exe'
            Path = $androidAapt2
            Sha256 = $pinnedAapt2Sha256
            MaximumBytes = 16MB
        },
        [pscustomobject]@{
            RelativePath = 'apksigner.bat'
            Path = $androidApkSigner
            Sha256 = $pinnedApkSignerBatSha256
            MaximumBytes = 64KB
        },
        [pscustomobject]@{
            RelativePath = 'lib/apksigner.jar'
            Path = [IO.Path]::GetFullPath((Join-Path $buildToolsRoot 'lib\apksigner.jar'))
            Sha256 = $pinnedApkSignerJarSha256
            MaximumBytes = 8MB
        }
    )
    foreach ($entry in $expected) {
        $expectedPath = [IO.Path]::GetFullPath((Join-Path $buildToolsRoot $entry.RelativePath.Replace('/', '\')))
        if ($entry.Path -ne $expectedPath -or
            -not (Test-Path -LiteralPath $entry.Path -PathType Leaf)) {
            throw "Pinned Android build tool is missing or outside build-tools ${androidBuildToolsVersion}: $($entry.RelativePath)"
        }
        Assert-NoReparseAncestors -Path $entry.Path -Label "Android build tool $($entry.RelativePath)"
        $file = Get-Item -LiteralPath $entry.Path -Force
        if ($file.Length -lt 1 -or $file.Length -gt $entry.MaximumBytes -or
            (Get-Sha256Hex -Path $entry.Path) -ne $entry.Sha256) {
            throw "Android build tool failed its pinned SHA-256 or size check: $($entry.RelativePath)"
        }
    }
    return @($expected | ForEach-Object {
        [pscustomobject]@{
            relativePath = $_.RelativePath
            sizeBytes = (Get-Item -LiteralPath $_.Path -Force).Length
            sha256 = $_.Sha256
        }
    })
}

function Assert-AndroidToolchainReceipt {
    param([Parameter(Mandatory = $true)][string]$Path)
    if (-not (Test-Path -LiteralPath $Path -PathType Leaf)) {
        throw 'Android toolchain receipt is missing.'
    }
    Assert-NoReparseAncestors -Path $Path -Label 'Android toolchain receipt'
    $receiptFile = Get-Item -LiteralPath $Path -Force
    if ($receiptFile.Length -lt 1 -or $receiptFile.Length -gt 32KB) {
        throw 'Android toolchain receipt size is invalid.'
    }
    try {
        $receipt = [IO.File]::ReadAllText($Path) | ConvertFrom-Json -Depth 8
    } catch {
        throw "Android toolchain receipt is not valid JSON: $($_.Exception.Message)"
    }
    Assert-ExactJsonProperties -Object $receipt `
        -Expected @('schemaVersion', 'buildToolsVersion', 'tools') `
        -Label 'Android toolchain receipt'
    $actualTools = @(Get-PinnedAndroidBuildTools)
    if (-not ($receipt.schemaVersion -is [long]) -or
        $receipt.schemaVersion -ne 1 -or
        $receipt.buildToolsVersion -ne $androidBuildToolsVersion -or
        @($receipt.tools).Count -ne $actualTools.Count) {
        throw 'Android toolchain receipt schema or tool count is invalid.'
    }
    for ($index = 0; $index -lt $actualTools.Count; $index++) {
        $record = @($receipt.tools)[$index]
        $actual = $actualTools[$index]
        Assert-ExactJsonProperties -Object $record `
            -Expected @('relativePath', 'sizeBytes', 'sha256') `
            -Label "Android toolchain receipt record $index"
        if ($record.relativePath -cne $actual.relativePath -or
            -not ($record.sizeBytes -is [long]) -or
            $record.sizeBytes -ne $actual.sizeBytes -or
            $record.sha256 -cne $actual.sha256) {
            throw "Android toolchain receipt record does not match pinned tool bytes: $($actual.relativePath)"
        }
    }
    return [pscustomobject]@{
        Receipt = $receipt
        SizeBytes = $receiptFile.Length
        Sha256 = Get-Sha256Hex -Path $Path
    }
}

function Get-CertificateSha256 {
    param([Parameter(Mandatory = $true)]$Certificate)
    return [Convert]::ToHexString(
        [Security.Cryptography.SHA256]::HashData($Certificate.RawData)).ToLowerInvariant()
}

function Invoke-NativeCapture {
    param(
        [Parameter(Mandatory = $true)][string]$FilePath,
        [Parameter(Mandatory = $true)][string[]]$Arguments,
        [Parameter(Mandatory = $true)][string]$Label
    )

    $lines = @(& $FilePath @Arguments 2>&1 | ForEach-Object { $_.ToString() })
    if ($LASTEXITCODE -ne 0) {
        $bounded = ($lines | Select-Object -Last 80) -join [Environment]::NewLine
        throw "$Label failed with exit code $LASTEXITCODE.`n$bounded"
    }
    return $lines
}

function Assert-FormalProductVersion {
    param(
        [Parameter(Mandatory = $true)][string]$Path,
        [Parameter(Mandatory = $true)][string]$ExpectedVersion,
        [Parameter(Mandatory = $true)][string]$Label
    )

    $versionInfo = [Diagnostics.FileVersionInfo]::GetVersionInfo($Path)
    $productVersion = ([string]$versionInfo.ProductVersion).Trim()
    $versionParts = $ExpectedVersion.Split('-', 2)[0].Split('.')
    $expectedNumericVersion = "$($versionParts[0]).$($versionParts[1]).$($versionParts[2]).0"
    if ([string]::IsNullOrWhiteSpace($productVersion) -or
        $productVersion -cne $ExpectedVersion -or
        ([string]$versionInfo.FileVersion).Trim() -cne $expectedNumericVersion) {
        throw "$Label ProductVersion/FileVersion must exactly equal '$ExpectedVersion' / '$expectedNumericVersion'; actual '$productVersion' / '$($versionInfo.FileVersion)'."
    }

    $visibleIdentity = @(
        [string]$versionInfo.ProductName,
        [string]$versionInfo.FileDescription,
        [string]$versionInfo.ProductVersion,
        [string]$versionInfo.FileVersion,
        [string]$versionInfo.Comments
    ) -join "`n"
    if ($visibleIdentity -match '(?i)(?:^|[^a-z])(preview|alpha)(?:[^a-z]|$)') {
        throw "$Label still exposes a preview/alpha identity and cannot enter a formal release."
    }
}

function Write-AtomicBytes {
    param([string]$Path, [byte[]]$Bytes, [switch]$AllowReplace)
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
        if ([IO.File]::Exists($Path)) {
            if (-not $AllowReplace) {
                throw "Atomic output already exists and replacement was not authorized: $Path"
            }
            $destination = Get-Item -LiteralPath $Path -Force
            if ($destination.PSIsContainer -or
                ($destination.Attributes -band [IO.FileAttributes]::ReparsePoint) -ne 0) {
                throw "Atomic replacement target is not a regular file: $Path"
            }
            [IO.File]::Move($temporaryPath, $Path, $true)
        } else {
            [IO.File]::Move($temporaryPath, $Path, $false)
        }
    } finally {
        if (Test-Path -LiteralPath $temporaryPath -PathType Leaf) {
            Remove-Item -LiteralPath $temporaryPath -Force
        }
    }
}

function Write-AtomicUtf8Text {
    param([string]$Path, [string]$Value, [switch]$AllowReplace)
    Write-AtomicBytes -Path $Path `
        -Bytes ([Text.UTF8Encoding]::new($false).GetBytes($Value)) `
        -AllowReplace:$AllowReplace
}

function ConvertTo-ReleasePowerShellUtf8Bom {
    param([Parameter(Mandatory = $true)][string]$Path)

    $bytes = [IO.File]::ReadAllBytes($Path)
    if ($bytes.Length -lt 1) {
        throw "Release PowerShell script is empty: $Path"
    }

    $hasUtf8Bom = $bytes.Length -ge 3 -and
        $bytes[0] -eq 0xEF -and $bytes[1] -eq 0xBB -and $bytes[2] -eq 0xBF
    if (($bytes.Length -ge 2 -and
            (($bytes[0] -eq 0xFF -and $bytes[1] -eq 0xFE) -or
             ($bytes[0] -eq 0xFE -and $bytes[1] -eq 0xFF))) -or
        ($bytes.Length -ge 4 -and
            (($bytes[0] -eq 0xFF -and $bytes[1] -eq 0xFE -and
              $bytes[2] -eq 0x00 -and $bytes[3] -eq 0x00) -or
             ($bytes[0] -eq 0x00 -and $bytes[1] -eq 0x00 -and
              $bytes[2] -eq 0xFE -and $bytes[3] -eq 0xFF)))) {
        throw "Release PowerShell script must be strict UTF-8, not UTF-16/UTF-32: $Path"
    }

    $body = if ($hasUtf8Bom) {
        [byte[]]$bytes[3..($bytes.Length - 1)]
    } else {
        $bytes
    }
    $strictUtf8 = [Text.UTF8Encoding]::new($false, $true)
    try {
        $text = $strictUtf8.GetString($body)
    } catch {
        throw "Release PowerShell script is not strict UTF-8: $Path"
    }
    $signatureBlockPattern =
        '(?ms)(?:\A|\r?\n)# SIG # Begin signature block\r?\n' +
        '(?:#[^\r\n]*(?:\r?\n|$))*# SIG # End signature block(?:\r?\n)?\z'
    if ($text.Contains([char]0xFFFD) -or
        [regex]::IsMatch($text, $signatureBlockPattern)) {
        throw "Release PowerShell source contains replacement text or an existing signature: $Path"
    }

    $tokens = $null
    $parseErrors = $null
    [void][Management.Automation.Language.Parser]::ParseInput(
        $text,
        [ref]$tokens,
        [ref]$parseErrors)
    if (@($parseErrors).Count -ne 0) {
        $details = @($parseErrors | Select-Object -First 8 | ForEach-Object {
            "line $($_.Extent.StartLineNumber): $($_.Message)"
        }) -join '; '
        throw "Release PowerShell source does not parse before signing: $Path ($details)"
    }

    if (-not $hasUtf8Bom) {
        $normalized = [byte[]]::new($bytes.Length + 3)
        $normalized[0] = 0xEF
        $normalized[1] = 0xBB
        $normalized[2] = 0xBF
        [Array]::Copy($bytes, 0, $normalized, 3, $bytes.Length)
        Write-AtomicBytes -Path $Path -Bytes $normalized -AllowReplace
        return ,$normalized
    }

    return ,$bytes
}

function Assert-SignedReleasePowerShellScript {
    param(
        [Parameter(Mandatory = $true)][string]$Path,
        [Parameter(Mandatory = $true)][byte[]]$ExpectedUnsignedBytes
    )

    $signedBytes = [IO.File]::ReadAllBytes($Path)
    if ($ExpectedUnsignedBytes.Length -lt 4 -or
        $signedBytes.Length -le $ExpectedUnsignedBytes.Length -or
        $signedBytes[0] -ne 0xEF -or $signedBytes[1] -ne 0xBB -or
        $signedBytes[2] -ne 0xBF) {
        throw "Signed PowerShell script is missing its protected UTF-8 BOM/body: $Path"
    }
    for ($index = 0; $index -lt $ExpectedUnsignedBytes.Length; $index++) {
        if ($signedBytes[$index] -ne $ExpectedUnsignedBytes[$index]) {
            throw "Authenticode signing changed PowerShell script content at byte $index`: $Path"
        }
    }

    $strictUtf8 = [Text.UTF8Encoding]::new($false, $true)
    try {
        $signedText = $strictUtf8.GetString($signedBytes, 3, $signedBytes.Length - 3)
    } catch {
        throw "Signed PowerShell script is not strict UTF-8: $Path"
    }
    try {
        $signatureText = $strictUtf8.GetString(
            $signedBytes,
            $ExpectedUnsignedBytes.Length,
            $signedBytes.Length - $ExpectedUnsignedBytes.Length)
    } catch {
        throw "Signed PowerShell signature block is not strict UTF-8: $Path"
    }
    $signatureBlockPattern =
        '\A\r?\n# SIG # Begin signature block\r?\n' +
        '(?:#[^\r\n]*(?:\r?\n|$))*# SIG # End signature block(?:\r?\n)?\z'
    if ($signedText.Contains([char]0xFFFD) -or
        -not [regex]::IsMatch($signatureText, $signatureBlockPattern)) {
        throw "Signed PowerShell script has invalid text or signature framing: $Path"
    }

    $tokens = $null
    $parseErrors = $null
    [void][Management.Automation.Language.Parser]::ParseFile(
        $Path,
        [ref]$tokens,
        [ref]$parseErrors)
    if (@($parseErrors).Count -ne 0) {
        $details = @($parseErrors | Select-Object -First 8 | ForEach-Object {
            "line $($_.Extent.StartLineNumber): $($_.Message)"
        }) -join '; '
        throw "Signed PowerShell script does not parse: $Path ($details)"
    }
}

function Get-SidValueFailClosed {
    param(
        [Parameter(Mandatory = $true)]$IdentityReference,
        [Parameter(Mandatory = $true)][string]$Label
    )
    try {
        return $IdentityReference.Translate(
            [Security.Principal.SecurityIdentifier]).Value
    } catch {
        throw "$Label contains an identity that cannot be translated to a SID."
    }
}

function Assert-PrivateSigningAclNode {
    param(
        [Parameter(Mandatory = $true)][string]$Path,
        [Parameter(Mandatory = $true)][string]$ExpectedOwnerSid,
        [Parameter(Mandatory = $true)][bool]$RequireProtectedAcl
    )
    try {
        $acl = Get-Acl -LiteralPath $Path -ErrorAction Stop
        $ownerReference = $acl.GetOwner([Security.Principal.SecurityIdentifier])
    } catch {
        throw "Cannot read signing-secret owner or ACL: $Path"
    }
    $ownerSid = Get-SidValueFailClosed -IdentityReference $ownerReference `
        -Label "Signing-secret owner for $Path"
    if ($ownerSid -cne $ExpectedOwnerSid) {
        throw "Signing-secret owner must be the current Windows user: $Path"
    }
    if ($RequireProtectedAcl -and -not $acl.AreAccessRulesProtected) {
        throw "Signing identity root must disable inherited ACLs: $Path"
    }

    $allowedAllowSids = @($ExpectedOwnerSid, 'S-1-5-18') # current user and LocalSystem
    $currentUserReadable = $false
    foreach ($rule in $acl.Access) {
        $sid = Get-SidValueFailClosed -IdentityReference $rule.IdentityReference `
            -Label "Signing-secret ACL for $Path"
        if ($rule.AccessControlType -eq [Security.AccessControl.AccessControlType]::Allow) {
            if ($sid -notin $allowedAllowSids) {
                throw "Signing-secret ACL grants access to an unauthorized or broad principal: $Path ($sid)"
            }
            if ($sid -ceq $ExpectedOwnerSid -and
                (($rule.FileSystemRights -band
                    [Security.AccessControl.FileSystemRights]::ReadData) -ne 0)) {
                $currentUserReadable = $true
            }
        }
    }
    if (-not $currentUserReadable) {
        throw "Signing-secret ACL does not grant the current user required access: $Path"
    }
}

function Assert-SigningSecretPath {
    param(
        [Parameter(Mandatory = $true)][string]$Root,
        [Parameter(Mandatory = $true)][string]$Path
    )
    $normalizedRoot = [IO.Path]::GetFullPath($Root).TrimEnd('\', '/')
    $normalizedPath = [IO.Path]::GetFullPath($Path)
    $rootPrefix = $normalizedRoot + [IO.Path]::DirectorySeparatorChar
    if (-not $normalizedPath.StartsWith($rootPrefix, [StringComparison]::OrdinalIgnoreCase) -or
        -not (Test-Path -LiteralPath $normalizedPath -PathType Leaf)) {
        throw "Signing secret is not a regular child of its identity root: $normalizedPath"
    }
    Assert-NoReparseAncestors -Path $normalizedPath -Label 'Signing secret'
    $currentUserSid = [Security.Principal.WindowsIdentity]::GetCurrent().User.Value
    $cursor = Get-Item -LiteralPath $normalizedPath -Force
    while ($null -ne $cursor) {
        $isRoot = $cursor.FullName.TrimEnd('\', '/').Equals(
            $normalizedRoot,
            [StringComparison]::OrdinalIgnoreCase)
        Assert-PrivateSigningAclNode -Path $cursor.FullName `
            -ExpectedOwnerSid $currentUserSid `
            -RequireProtectedAcl:$isRoot
        if ($isRoot) {
            return
        }
        $cursor = if ($cursor -is [IO.DirectoryInfo]) { $cursor.Parent } else { $cursor.Directory }
        if ($null -eq $cursor -or
            (-not $cursor.FullName.TrimEnd('\', '/').Equals(
                $normalizedRoot,
                [StringComparison]::OrdinalIgnoreCase) -and
             -not $cursor.FullName.StartsWith($rootPrefix, [StringComparison]::OrdinalIgnoreCase))) {
            throw "Signing secret escaped its identity ACL root: $normalizedPath"
        }
    }
    throw "Signing identity root was not reached for secret: $normalizedPath"
}

function Assert-CodeSigningCertificate {
    param($Certificate, [string]$TrustMode)
    if (-not $Certificate.HasPrivateKey) {
        throw 'Signing certificate does not contain a private key.'
    }
    $rsa = [Security.Cryptography.X509Certificates.RSACertificateExtensions]::GetRSAPrivateKey(
        $Certificate)
    try {
        if ($null -eq $rsa -or $rsa.KeySize -lt 3072) {
            throw 'Signing certificate must use RSA with at least 3072 bits.'
        }
    } finally {
        if ($null -ne $rsa) { $rsa.Dispose() }
    }
    $now = [DateTime]::UtcNow
    if ($Certificate.NotBefore.ToUniversalTime() -gt $now -or
        $Certificate.NotAfter.ToUniversalTime() -le $now.AddDays(30)) {
        throw 'Signing certificate is not currently valid for at least another 30 days.'
    }
    $hasCodeSigningEku = $false
    foreach ($extension in $Certificate.Extensions) {
        if ($extension.Oid.Value -eq '2.5.29.37') {
            $eku = [Security.Cryptography.X509Certificates.X509EnhancedKeyUsageExtension]::new(
                $extension,
                $extension.Critical)
            $hasCodeSigningEku = @($eku.EnhancedKeyUsages | Where-Object {
                $_.Value -eq '1.3.6.1.5.5.7.3.3'
            }).Count -eq 1
        }
    }
    if (-not $hasCodeSigningEku) {
        throw 'Signing certificate does not contain the Code Signing EKU.'
    }
    $isSelfSigned = [Convert]::ToHexString($Certificate.SubjectName.RawData) -eq
        [Convert]::ToHexString($Certificate.IssuerName.RawData)
    if ($TrustMode -eq 'self-signed-local' -and -not $isSelfSigned) {
        throw 'PublisherTrustMode requires a self-signed certificate.'
    }
    if ($TrustMode -eq 'public-ca' -and $isSelfSigned) {
        throw 'PublisherTrustMode public-ca cannot use a self-signed certificate.'
    }
}

function Set-ProductAuthenticodeSignature {
    param([string]$Path, $Certificate)
    $result = Set-AuthenticodeSignature -LiteralPath $Path -Certificate $Certificate `
        -HashAlgorithm SHA256 -TimestampServer $TimestampServerUrl.AbsoluteUri -IncludeChain All
    if ($null -eq $result.SignerCertificate -or $null -eq $result.TimeStamperCertificate) {
        throw "Authenticode signing or trusted timestamping failed: $Path"
    }
    $actualCertificateSha256 = Get-CertificateSha256 -Certificate $result.SignerCertificate
    if ($actualCertificateSha256 -ne $script:publisherCertificateSha256 -or
        $result.Status -in @(
            [Management.Automation.SignatureStatus]::NotSigned,
            [Management.Automation.SignatureStatus]::HashMismatch,
            [Management.Automation.SignatureStatus]::NotSupportedFileFormat,
            [Management.Automation.SignatureStatus]::Incompatible)) {
        throw "Authenticode signing verification failed: $Path ($($result.Status))"
    }
    if ($PublisherTrustMode -eq 'public-ca' -and
        $result.Status -ne [Management.Automation.SignatureStatus]::Valid) {
        throw "Public-CA Authenticode chain validation failed: $Path ($($result.Status))"
    }
}

foreach ($inputRoot in @($payloadRoot, $builtinProviderRoot, $mobileArtifactRoot)) {
    if (-not (Test-Path -LiteralPath $inputRoot -PathType Container) -or
        ((Get-Item -LiteralPath $inputRoot -Force).Attributes -band
            [IO.FileAttributes]::ReparsePoint) -ne 0) {
        throw "A required release input directory is missing or unsafe: $inputRoot"
    }
}
$pinnedAndroidTools = @(Get-PinnedAndroidBuildTools)
if (-not (Test-Path -LiteralPath $identityRoot -PathType Container)) {
    throw 'SigningIdentityDirectory does not exist.'
}
foreach ($inputRoot in @($payloadRoot, $builtinProviderRoot, $mobileArtifactRoot, $identityRoot)) {
    if ((Test-IsUnderRoot $outputRoot $inputRoot) -or (Test-IsUnderRoot $inputRoot $outputRoot)) {
        throw 'Release inputs, output and signing identity directories must not contain one another.'
    }
}
if (Test-IsUnderRoot $identityRoot $projectRoot) {
    throw 'Private signing material must remain outside the source repository.'
}
if ($PackageBaseUri.Scheme -ne 'https' -or -not $PackageBaseUri.IsDefaultPort -or
    -not [string]::IsNullOrEmpty($PackageBaseUri.UserInfo) -or
    -not [string]::IsNullOrEmpty($PackageBaseUri.Fragment) -or
    -not [string]::IsNullOrEmpty($PackageBaseUri.Query) -or
    -not $PackageBaseUri.AbsoluteUri.EndsWith('/')) {
    throw 'PackageBaseUri must be a credential-free HTTPS directory URL using its default port.'
}
if ($TimestampServerUrl.Scheme -notin @('http', 'https') -or
    -not [string]::IsNullOrEmpty($TimestampServerUrl.UserInfo) -or
    -not [string]::IsNullOrEmpty($TimestampServerUrl.Fragment)) {
    throw 'TimestampServerUrl must be an HTTP(S) URL without credentials or a fragment.'
}
if ($Version -match '(?i)(?:^|[.-])(preview|alpha)(?:[.-]|$)' -or
    ($Channel -eq 'stable' -and $Version.Contains('-'))) {
    throw 'Formal releases cannot use preview/alpha versions, and the stable channel requires a final semantic version.'
}
if ($Version -eq '1.1.0' -and $AndroidVersionCode -ne 10) {
    throw 'Formal Android 1.1.0 must use the immutable versionCode 10.'
}
if (Test-Path -LiteralPath $outputRoot) {
    if (-not (Test-Path -LiteralPath $outputRoot -PathType Container) -or
        (Get-ChildItem -LiteralPath $outputRoot -Force | Select-Object -First 1)) {
        throw 'OutputDirectory must be new or empty. Existing releases are never overwritten.'
    }
} else {
    New-Item -ItemType Directory -Path $outputRoot | Out-Null
}

$requiredIdentityFiles = @(
    'muhun-mcsv-release-signing.pfx',
    'pfx-password.dpapi',
    'muhun-mcsv-release-signing.cer',
    'update-signing-public-key.json',
    'provider-signing-private-key.pem',
    'provider-key-password.dpapi',
    'provider-signing-public-key.pem',
    'provider-signing-identity.json'
)
foreach ($name in $requiredIdentityFiles) {
    $path = Join-Path $identityRoot $name
    if (-not (Test-Path -LiteralPath $path -PathType Leaf) -or
        ((Get-Item -LiteralPath $path -Force).Attributes -band [IO.FileAttributes]::ReparsePoint) -ne 0) {
        throw "Signing identity is missing a safe $name file."
    }
}
foreach ($sensitiveIdentityFile in @(
        'muhun-mcsv-release-signing.pfx',
        'pfx-password.dpapi',
        'provider-signing-private-key.pem',
        'provider-key-password.dpapi'
    )) {
    Assert-SigningSecretPath -Root $identityRoot `
        -Path (Join-Path $identityRoot $sensitiveIdentityFile)
}

$protectedPassword = (Get-Content -LiteralPath (Join-Path $identityRoot 'pfx-password.dpapi') -Raw).Trim()
$securePassword = ConvertTo-SecureString -String $protectedPassword
$passwordPointer = [Runtime.InteropServices.Marshal]::SecureStringToBSTR($securePassword)
$passwordText = $null
$certificate = $null
$providerEcdsa = $null
$providerPasswordPointer = [IntPtr]::Zero
$providerPasswordText = $null
try {
    $passwordText = [Runtime.InteropServices.Marshal]::PtrToStringBSTR($passwordPointer)
    $certificate = [Security.Cryptography.X509Certificates.X509Certificate2]::new(
        (Join-Path $identityRoot 'muhun-mcsv-release-signing.pfx'),
        $passwordText,
        [Security.Cryptography.X509Certificates.X509KeyStorageFlags]::EphemeralKeySet)
    Assert-CodeSigningCertificate -Certificate $certificate -TrustMode $PublisherTrustMode
    $script:publisherCertificateSha256 = Get-CertificateSha256 -Certificate $certificate
    if ($script:publisherCertificateSha256 -cne $expectedPublisherCertificateSha256) {
        throw 'The formal signing identity does not match the independently published X MCSV publisher fingerprint.'
    }

    $providerProtectedPassword = (Get-Content -LiteralPath `
        (Join-Path $identityRoot 'provider-key-password.dpapi') -Raw).Trim()
    $providerSecurePassword = ConvertTo-SecureString -String $providerProtectedPassword
    $providerPasswordPointer = [Runtime.InteropServices.Marshal]::SecureStringToBSTR(
        $providerSecurePassword)
    $providerPasswordText = [Runtime.InteropServices.Marshal]::PtrToStringBSTR(
        $providerPasswordPointer)
    $providerPrivatePem = Get-Content -LiteralPath `
        (Join-Path $identityRoot 'provider-signing-private-key.pem') -Raw
    $providerPublicPem = Get-Content -LiteralPath `
        (Join-Path $identityRoot 'provider-signing-public-key.pem') -Raw
    $providerIdentity = Get-Content -LiteralPath `
        (Join-Path $identityRoot 'provider-signing-identity.json') -Raw | ConvertFrom-Json
    $providerEcdsa = [Security.Cryptography.ECDsa]::Create()
    $providerEcdsa.ImportFromEncryptedPem($providerPrivatePem, $providerPasswordText)
    $providerPublicProbe = [Security.Cryptography.ECDsa]::Create()
    try {
        $providerPublicProbe.ImportFromPem($providerPublicPem)
        $providerSpki = $providerEcdsa.ExportSubjectPublicKeyInfo()
        $providerPublicSpki = $providerPublicProbe.ExportSubjectPublicKeyInfo()
        try {
            $script:providerPublicKeySha256 = [Convert]::ToHexString(
                [Security.Cryptography.SHA256]::HashData($providerSpki)).ToLowerInvariant()
            if ($providerEcdsa.KeySize -ne 256 -or $providerPublicProbe.KeySize -ne 256 -or
                -not [Security.Cryptography.CryptographicOperations]::FixedTimeEquals(
                    $providerSpki,
                    $providerPublicSpki) -or
                $providerIdentity.schemaVersion -ne 1 -or
                $providerIdentity.productId -ne 'muhun.mcsv.manager' -or
                $providerIdentity.publisherId -ne 'muhun.firstparty' -or
                $providerIdentity.algorithm -ne 'ECDSA-P256-SHA256' -or
                $providerIdentity.publicKeySha256 -ne $script:providerPublicKeySha256) {
                throw 'Provider signing identity does not match its pinned P-256 public key.'
            }
        } finally {
            [Security.Cryptography.CryptographicOperations]::ZeroMemory($providerSpki)
            [Security.Cryptography.CryptographicOperations]::ZeroMemory($providerPublicSpki)
        }
    } finally {
        $providerPublicProbe.Dispose()
    }

    $publicKeyDocument = Get-Content -LiteralPath (Join-Path $identityRoot 'update-signing-public-key.json') `
        -Raw | ConvertFrom-Json
    $certificateRsa = [Security.Cryptography.X509Certificates.RSACertificateExtensions]::GetRSAPrivateKey(
        $certificate)
    try {
        $certificateSpki = $certificateRsa.ExportSubjectPublicKeyInfo()
        if ($publicKeyDocument.schemaVersion -ne 1 -or
            $publicKeyDocument.productId -ne 'muhun.mcsv.manager' -or
            $publicKeyDocument.signatureAlgorithm -ne 'rsa-pss-sha256' -or
            $publicKeyDocument.keyId -notmatch '^[a-z][a-z0-9._-]{2,63}$' -or
            $publicKeyDocument.publisherCertificateSha256 -ne $script:publisherCertificateSha256 -or
            -not [Security.Cryptography.CryptographicOperations]::FixedTimeEquals(
                [Convert]::FromBase64String($publicKeyDocument.subjectPublicKeyInfo),
                $certificateSpki)) {
            throw 'Update public-key document does not match the private signing identity.'
        }

        $payloadFiles = @(Get-ChildItem -LiteralPath $payloadRoot -Recurse -File -Force | Sort-Object FullName)
        if ($payloadFiles.Count -lt 3 -or $payloadFiles.Count -gt 10000) {
            throw 'Payload file count is missing or exceeds the product limit.'
        }
        $totalPayloadBytes = 0L
        foreach ($file in $payloadFiles) {
            $relative = [IO.Path]::GetRelativePath($payloadRoot, $file.FullName).Replace('\', '/')
            if (-not (Test-SafeRelativePath -Path $relative) -or
                ($file.Attributes -band [IO.FileAttributes]::ReparsePoint) -ne 0) {
                throw "Payload contains an unsafe path or reparse point: $relative"
            }
            if ($relative.EndsWith('.pdb', [StringComparison]::OrdinalIgnoreCase)) {
                throw "Payload contains a debug-symbol artifact: $relative"
            }
            if ($relative -in @(
                'installed-version.v1.json',
                'release-manifest.json',
                'release-manifest.json.sig',
                'update-manifest.json',
                'update-manifest.json.sig',
                'SHA256SUMS.txt',
                '開始使用.txt')) {
                throw "Payload contains a release-reserved file name: $relative"
            }
            if ($file.Length -gt (2GB - $totalPayloadBytes)) {
                throw 'Payload exceeds the 2 GiB release limit.'
            }
            $totalPayloadBytes += $file.Length
            if ($totalPayloadBytes -gt 2GB) {
                throw 'Payload exceeds the 2 GiB release limit.'
            }
            $destination = Join-Path $outputRoot $relative.Replace('/', '\')
            [IO.Directory]::CreateDirectory([IO.Path]::GetDirectoryName($destination)) | Out-Null
            [IO.File]::Copy($file.FullName, $destination, $false)
        }

        # Make the signed package immediately capable of verifying and discovering updates.
        # ProductUpdateCoordinator resolves an empty PublicKeyDocumentPath beside the Service
        # executable, so place an immutable copy there and bind the current release channel to
        # the exact HTTPS directory supplied to this formal build. The same key is also published
        # at release-root level for offline/install verification below.
        $serviceReleaseRoot = Join-Path $outputRoot 'service-win-x64'
        $serviceSettingsPath = Join-Path $serviceReleaseRoot 'appsettings.json'
        if (-not (Test-Path -LiteralPath $serviceSettingsPath -PathType Leaf) -or
            (Get-Item -LiteralPath $serviceSettingsPath).Length -gt 64KB) {
            throw 'Formal Service publish output is missing its bounded appsettings.json.'
        }
        $serviceSettings = Get-Content -LiteralPath $serviceSettingsPath -Raw | ConvertFrom-Json
        if ($null -eq $serviceSettings.Mcsv -or
            $null -eq $serviceSettings.Mcsv.Service -or
            $null -eq $serviceSettings.Mcsv.Service.Updates) {
            throw 'Formal Service appsettings.json is missing the update configuration section.'
        }
        $releaseManifestUri = [uri]::new($PackageBaseUri, 'update-manifest.json').AbsoluteUri
        $serviceSettings.Mcsv.Service.Updates.StableManifestUrl = if ($Channel -eq 'stable') {
            $releaseManifestUri
        } else { '' }
        $serviceSettings.Mcsv.Service.Updates.BetaManifestUrl = if ($Channel -eq 'beta') {
            $releaseManifestUri
        } else { '' }
        $serviceSettings.Mcsv.Service.Updates.AllowedFeedHosts = @($PackageBaseUri.IdnHost)
        $serviceSettings.Mcsv.Service.Updates.PublicKeyDocumentPath = ''
        Write-AtomicUtf8Text -Path $serviceSettingsPath `
            -Value (($serviceSettings | ConvertTo-Json -Depth 16) + [Environment]::NewLine) `
            -AllowReplace
        [IO.File]::Copy(
            (Join-Path $identityRoot 'update-signing-public-key.json'),
            (Join-Path $serviceReleaseRoot 'update-signing-public-key.json'),
            $false)

        $requiredExecutables = @(
            'service-win-x64/Muhun MCSV Service.exe',
            'gui-win-x64/Muhun MCSV Manager.exe',
            'updater-win-x64/Muhun MCSV Updater.exe'
        )
        foreach ($relative in $requiredExecutables) {
            $path = Join-Path $outputRoot $relative.Replace('/', '\')
            if (-not (Test-Path -LiteralPath $path -PathType Leaf)) {
                throw "Formal payload is missing the expected first-party executable: $relative"
            }
            Assert-FormalProductVersion -Path $path -ExpectedVersion $Version -Label $relative
            Set-ProductAuthenticodeSignature -Path $path -Certificate $certificate
        }

        $installedVersion = [ordered]@{
            schemaVersion = 1
            productId = 'muhun.mcsv.manager'
            version = $Version
            entryPoint = 'gui-win-x64/Muhun MCSV Manager.exe'
        }
        Write-AtomicUtf8Text -Path (Join-Path $outputRoot 'installed-version.v1.json') `
            -Value (($installedVersion | ConvertTo-Json -Depth 3) + [Environment]::NewLine)

        $releaseScripts = @(
            'Install-MuhunMcsv.ps1',
            'Uninstall-MuhunMcsv.ps1',
            'Test-MuhunMcsvRelease.ps1'
        )
        $uninstallerUnsignedBytes = $null
        foreach ($name in $releaseScripts) {
            $source = Join-Path $PSScriptRoot $name
            $destination = Join-Path $outputRoot $name
            [IO.File]::Copy($source, $destination, $false)
            $unsignedScriptBytes = ConvertTo-ReleasePowerShellUtf8Bom -Path $destination
            Set-ProductAuthenticodeSignature -Path $destination -Certificate $certificate
            Assert-SignedReleasePowerShellScript -Path $destination `
                -ExpectedUnsignedBytes $unsignedScriptBytes
            if ($name -ceq 'Uninstall-MuhunMcsv.ps1') {
                $uninstallerUnsignedBytes = $unsignedScriptBytes
            }
        }
        if ($null -eq $uninstallerUnsignedBytes) {
            throw 'The signed release uninstaller was not produced.'
        }
        $versionToolsRoot = Join-Path $outputRoot 'tools'
        [IO.Directory]::CreateDirectory($versionToolsRoot) | Out-Null
        $versionUninstallerPath = Join-Path $versionToolsRoot 'Uninstall-MuhunMcsv.ps1'
        [IO.File]::Copy(
            (Join-Path $outputRoot 'Uninstall-MuhunMcsv.ps1'),
            $versionUninstallerPath,
            $false)
        Assert-SignedReleasePowerShellScript -Path $versionUninstallerPath `
            -ExpectedUnsignedBytes $uninstallerUnsignedBytes

        [IO.File]::Copy(
            (Join-Path $identityRoot 'muhun-mcsv-release-signing.cer'),
            (Join-Path $outputRoot 'publisher.cer'),
            $false)
        [IO.File]::Copy(
            (Join-Path $identityRoot 'update-signing-public-key.json'),
            (Join-Path $outputRoot 'update-signing-public-key.json'),
            $false)

        # Build the first-party provider as an independently signed package. The release RSA
        # identity Authenticode-signs its executable, while the provider-specific P-256 identity
        # signs the immutable .mcsvp archive using the host's domain-separated protocol.
        $providerDeploymentRoot = Join-Path $outputRoot 'providers\muhun.catalog'
        [IO.Directory]::CreateDirectory($providerDeploymentRoot) | Out-Null
        $providerStagingRoot = Join-Path $outputRoot ".provider-staging-$([guid]::NewGuid().ToString('N'))"
        try {
            [IO.Directory]::CreateDirectory($providerStagingRoot) | Out-Null
            $providerInputFiles = @(Get-ChildItem -LiteralPath $builtinProviderRoot -Recurse -File -Force |
                Sort-Object FullName)
            if ($providerInputFiles.Count -lt 1 -or $providerInputFiles.Count -gt 4096) {
                throw 'Builtin provider publish output is missing or exceeds its file limit.'
            }
            $providerFileDigests = [ordered]@{}
            foreach ($file in $providerInputFiles) {
                $relative = [IO.Path]::GetRelativePath($builtinProviderRoot, $file.FullName).Replace('\', '/')
                if (-not (Test-SafeRelativePath -Path $relative) -or
                    ($file.Attributes -band [IO.FileAttributes]::ReparsePoint) -ne 0 -or
                    $relative.EndsWith('.pdb', [StringComparison]::OrdinalIgnoreCase) -or
                    $relative.Equals('provider.manifest.json', [StringComparison]::OrdinalIgnoreCase)) {
                    throw "Builtin provider publish output contains an unsafe file: $relative"
                }
                $destination = Join-Path $providerStagingRoot $relative.Replace('/', '\')
                [IO.Directory]::CreateDirectory([IO.Path]::GetDirectoryName($destination)) | Out-Null
                [IO.File]::Copy($file.FullName, $destination, $false)
            }

            $providerEntryPoint = 'Muhun.MCSV.BuiltinProvider.exe'
            $providerExecutable = Join-Path $providerStagingRoot $providerEntryPoint
            if (-not (Test-Path -LiteralPath $providerExecutable -PathType Leaf)) {
                throw 'Builtin provider publish output is missing its fixed entry point.'
            }
            Assert-FormalProductVersion -Path $providerExecutable -ExpectedVersion $Version `
                -Label 'Builtin provider'
            Set-ProductAuthenticodeSignature -Path $providerExecutable -Certificate $certificate

            $stagedProviderFiles = @(Get-ChildItem -LiteralPath $providerStagingRoot -Recurse -File -Force |
                Sort-Object FullName)
            foreach ($file in $stagedProviderFiles) {
                $relative = [IO.Path]::GetRelativePath($providerStagingRoot, $file.FullName).Replace('\', '/')
                $providerFileDigests[$relative] = Get-Sha256Hex -Path $file.FullName
            }
            $providerManifest = [ordered]@{
                schemaVersion = 2
                id = 'muhun.catalog'
                displayName = 'Muhun Catalog'
                version = $Version
                apiVersion = [ordered]@{ major = 1; minor = 2 }
                entryPoint = $providerEntryPoint
                capabilities = @('modpack.catalog')
                permissions = @('provider.http')
                networkHosts = @('api.modrinth.com', 'api.feed-the-beast.com')
                fileSha256 = $providerFileDigests
            }
            Write-AtomicUtf8Text -Path (Join-Path $providerStagingRoot 'provider.manifest.json') `
                -Value (($providerManifest | ConvertTo-Json -Depth 8 -Compress) + [Environment]::NewLine)

            $providerPackagePath = Join-Path $providerDeploymentRoot 'muhun.catalog.mcsvp'
            $providerPackageStream = [IO.FileStream]::new(
                $providerPackagePath,
                [IO.FileMode]::CreateNew,
                [IO.FileAccess]::ReadWrite,
                [IO.FileShare]::None)
            try {
                $providerArchive = [IO.Compression.ZipArchive]::new(
                    $providerPackageStream,
                    [IO.Compression.ZipArchiveMode]::Create,
                    $true,
                    [Text.Encoding]::UTF8)
                try {
                    foreach ($file in @(Get-ChildItem -LiteralPath $providerStagingRoot -Recurse -File -Force |
                            Sort-Object FullName)) {
                        $relative = [IO.Path]::GetRelativePath(
                            $providerStagingRoot,
                            $file.FullName).Replace('\', '/')
                        $entry = $providerArchive.CreateEntry(
                            $relative,
                            [IO.Compression.CompressionLevel]::Optimal)
                        $entry.LastWriteTime = [DateTimeOffset]::new(
                            1980, 1, 1, 0, 0, 0, [TimeSpan]::Zero)
                        $sourceStream = $file.OpenRead()
                        $entryStream = $entry.Open()
                        try {
                            $sourceStream.CopyTo($entryStream, 131072)
                        } finally {
                            $entryStream.Dispose()
                            $sourceStream.Dispose()
                        }
                    }
                } finally {
                    $providerArchive.Dispose()
                }
                $providerPackageStream.Flush($true)
            } finally {
                $providerPackageStream.Dispose()
            }

            $providerPackage = Get-Item -LiteralPath $providerPackagePath
            $providerPackageSha256 = Get-Sha256Hex -Path $providerPackagePath
            $domainBytes = [Text.Encoding]::UTF8.GetBytes("Muhun-MCSV-Provider-Package`0v1`0")
            $lengthBytes = [byte[]]::new(8)
            [Buffers.Binary.BinaryPrimitives]::WriteInt64BigEndian(
                $lengthBytes,
                [long]$providerPackage.Length)
            $digestBytes = [Convert]::FromHexString($providerPackageSha256)
            $providerSignaturePayload = [byte[]]::new(
                $domainBytes.Length + $lengthBytes.Length + $digestBytes.Length)
            try {
                [Array]::Copy($domainBytes, 0, $providerSignaturePayload, 0, $domainBytes.Length)
                [Array]::Copy(
                    $lengthBytes,
                    0,
                    $providerSignaturePayload,
                    $domainBytes.Length,
                    $lengthBytes.Length)
                [Array]::Copy(
                    $digestBytes,
                    0,
                    $providerSignaturePayload,
                    $domainBytes.Length + $lengthBytes.Length,
                    $digestBytes.Length)
                $providerSignatureBytes = $providerEcdsa.SignData(
                    $providerSignaturePayload,
                    [Security.Cryptography.HashAlgorithmName]::SHA256,
                    [Security.Cryptography.DSASignatureFormat]::Rfc3279DerSequence)
            } finally {
                [Security.Cryptography.CryptographicOperations]::ZeroMemory($digestBytes)
                [Security.Cryptography.CryptographicOperations]::ZeroMemory($providerSignaturePayload)
            }
            $providerSignature = [ordered]@{
                publisherId = 'muhun.firstparty'
                algorithm = 'ECDSA-P256-SHA256'
                signatureBase64 = [Convert]::ToBase64String($providerSignatureBytes)
                formatVersion = 1
            }
            [Security.Cryptography.CryptographicOperations]::ZeroMemory($providerSignatureBytes)
            [IO.File]::Copy(
                (Join-Path $identityRoot 'provider-signing-public-key.pem'),
                (Join-Path $providerDeploymentRoot 'publisher-public.pem'),
                $false)
            $providerDeployment = [ordered]@{
                schemaVersion = 1
                packageFileName = 'muhun.catalog.mcsvp'
                publicKeyFileName = 'publisher-public.pem'
                publicKeySha256 = $script:providerPublicKeySha256
                expectedSha256 = $providerPackageSha256
                expectedProviderId = 'muhun.catalog'
                expectedVersion = $Version
                expectedPublisherId = 'muhun.firstparty'
                signature = $providerSignature
            }
            Write-AtomicUtf8Text -Path (Join-Path $providerDeploymentRoot 'deployment.v1.json') `
                -Value (($providerDeployment | ConvertTo-Json -Depth 8 -Compress) + [Environment]::NewLine)
        } finally {
            if ((Test-Path -LiteralPath $providerStagingRoot -PathType Container) -and
                (Test-IsUnderRoot $providerStagingRoot $outputRoot)) {
                Remove-Item -LiteralPath $providerStagingRoot -Recurse -Force
            }
        }

        $mobileApkSource = Join-Path $mobileArtifactRoot 'Muhun-MCSV-Remote.apk'
        $mobileV4SignatureSource = Join-Path $mobileArtifactRoot 'Muhun-MCSV-Remote.apk.idsig'
        $mobileMetadataSource = Join-Path $mobileArtifactRoot 'android-release.v3.json'
        $mobileToolchainReceiptSource = Join-Path $mobileArtifactRoot 'android-toolchain.v1.json'
        foreach ($mobileFile in @(
                $mobileApkSource,
                $mobileV4SignatureSource,
                $mobileMetadataSource,
                $mobileToolchainReceiptSource
            )) {
            if (-not (Test-Path -LiteralPath $mobileFile -PathType Leaf) -or
                ((Get-Item -LiteralPath $mobileFile -Force).Attributes -band
                    [IO.FileAttributes]::ReparsePoint) -ne 0) {
                throw 'The formal Android APK or its verified metadata is missing or unsafe.'
            }
            Assert-NoReparseAncestors -Path $mobileFile -Label 'Android release input'
        }
        if ((Get-Item -LiteralPath $mobileMetadataSource).Length -gt 64KB) {
            throw 'Android release metadata exceeds its size limit.'
        }
        try {
            $mobileMetadata = Get-Content -LiteralPath $mobileMetadataSource -Raw |
                ConvertFrom-Json -Depth 8
        } catch {
            throw "Android release metadata is not valid JSON: $($_.Exception.Message)"
        }
        Assert-ExactJsonProperties -Object $mobileMetadata `
            -Expected @(
                'schemaVersion', 'productId', 'packageId', 'version', 'versionCode',
                'sizeBytes', 'sha256', 'signingCertificateSha256',
                'v4SignatureFileName', 'v4SignatureSizeBytes', 'v4SignatureSha256',
                'verifiedSignatureSchemes', 'toolchainReceiptFileName',
                'toolchainReceiptSizeBytes', 'toolchainReceiptSha256'
            ) `
            -Label 'Android release metadata'
        $mobileToolchainReceipt = Assert-AndroidToolchainReceipt `
            -Path $mobileToolchainReceiptSource
        if (-not ($mobileMetadata.schemaVersion -is [long]) -or
            $mobileMetadata.schemaVersion -ne 3 -or
            $mobileMetadata.productId -ne 'muhun.mcsv.manager' -or
            $mobileMetadata.packageId -ne 'com.muhun.mcsv.remote' -or
            $mobileMetadata.version -ne $Version -or
            -not ($mobileMetadata.versionCode -is [long]) -or
            $mobileMetadata.versionCode -ne $AndroidVersionCode -or
            $mobileMetadata.versionCode -lt 1 -or
            $mobileMetadata.sizeBytes -lt 1 -or $mobileMetadata.sizeBytes -gt 512MB -or
            $mobileMetadata.sha256 -notmatch '^[a-f0-9]{64}$' -or
            $mobileMetadata.signingCertificateSha256 -notmatch '^[a-f0-9]{64}$' -or
            $mobileMetadata.v4SignatureFileName -ne 'Muhun-MCSV-Remote.apk.idsig' -or
            $mobileMetadata.v4SignatureSizeBytes -lt 1 -or
            $mobileMetadata.v4SignatureSizeBytes -gt 16MB -or
            $mobileMetadata.v4SignatureSha256 -notmatch '^[a-f0-9]{64}$' -or
            @($mobileMetadata.verifiedSignatureSchemes).Count -ne 3 -or
            $mobileMetadata.verifiedSignatureSchemes[0] -ne 'v2' -or
            $mobileMetadata.verifiedSignatureSchemes[1] -ne 'v3' -or
            $mobileMetadata.verifiedSignatureSchemes[2] -ne 'v4' -or
            $mobileMetadata.toolchainReceiptFileName -cne 'android-toolchain.v1.json' -or
            $mobileMetadata.toolchainReceiptSizeBytes -ne $mobileToolchainReceipt.SizeBytes -or
            $mobileMetadata.toolchainReceiptSha256 -cne $mobileToolchainReceipt.Sha256 -or
            (Get-Item -LiteralPath $mobileApkSource).Length -ne $mobileMetadata.sizeBytes -or
            (Get-Sha256Hex -Path $mobileApkSource) -ne $mobileMetadata.sha256 -or
            (Get-Item -LiteralPath $mobileV4SignatureSource).Length -ne
                $mobileMetadata.v4SignatureSizeBytes -or
            (Get-Sha256Hex -Path $mobileV4SignatureSource) -ne
                $mobileMetadata.v4SignatureSha256) {
            throw 'Android release artifact does not match its verified signed-build metadata.'
        }
        $apkVerification = Invoke-NativeCapture -FilePath $androidApkSigner `
            -Arguments @(
                'verify', '--verbose', '--print-certs',
                '-v4-signature-file', $mobileV4SignatureSource,
                $mobileApkSource) `
            -Label 'Android APK signature verification'
        $apkVerificationText = $apkVerification -join "`n"
        if ($apkVerificationText -notmatch
                'Verified using v2 scheme \(APK Signature Scheme v2\): true' -or
            $apkVerificationText -notmatch
                'Verified using v3 scheme \(APK Signature Scheme v3\): true' -or
            $apkVerificationText -notmatch
                'Verified using v4 scheme \(APK Signature Scheme v4\): true') {
            throw 'Android APK is missing its required v2/v3/v4 signatures.'
        }
        $apkCertificateDigests = @([regex]::Matches(
            $apkVerificationText,
            '(?im)certificate SHA-256 digest:\s*([0-9a-f]{64})') | ForEach-Object {
                $_.Groups[1].Value.ToLowerInvariant()
            } | Sort-Object -Unique)
        if ($apkCertificateDigests.Count -ne 1 -or
            $apkCertificateDigests[0] -ne $mobileMetadata.signingCertificateSha256) {
            throw 'Android APK signer does not match its verified release metadata.'
        }
        $apkBadging = (Invoke-NativeCapture -FilePath $androidAapt2 `
            -Arguments @('dump', 'badging', $mobileApkSource) `
            -Label 'Android APK identity verification') -join "`n"
        if ($apkBadging -notmatch "package: name='com\.muhun\.mcsv\.remote'" -or
            $apkBadging -notmatch "versionCode='$AndroidVersionCode'" -or
            $apkBadging -notmatch "versionName='$([regex]::Escape($Version))'") {
            throw 'Android APK package, versionName or versionCode is not the requested formal release.'
        }
        $mobileReleaseRoot = Join-Path $outputRoot 'mobile'
        [IO.Directory]::CreateDirectory($mobileReleaseRoot) | Out-Null
        [IO.File]::Copy($mobileApkSource, (Join-Path $mobileReleaseRoot 'Muhun-MCSV-Remote.apk'), $false)
        [IO.File]::Copy(
            $mobileV4SignatureSource,
            (Join-Path $mobileReleaseRoot 'Muhun-MCSV-Remote.apk.idsig'),
            $false)
        [IO.File]::Copy(
            $mobileToolchainReceiptSource,
            (Join-Path $mobileReleaseRoot 'android-toolchain.v1.json'),
            $false)
        [IO.File]::Copy(
            $mobileMetadataSource,
            (Join-Path $mobileReleaseRoot 'android-release.v3.json'),
            $false)
        $mobileMetadataRelease = Get-Item -LiteralPath `
            (Join-Path $mobileReleaseRoot 'android-release.v3.json') -Force
        $mobileMetadataReleaseSha256 = Get-Sha256Hex -Path $mobileMetadataRelease.FullName

        $packageFileName = "Muhun-MCSV-$Version-win-x64.zip"
        $packagePath = Join-Path $outputRoot $packageFileName
        $packageSourceFiles = @(Get-ChildItem -LiteralPath $outputRoot -Recurse -File -Force |
            Where-Object {
                $relative = [IO.Path]::GetRelativePath($outputRoot, $_.FullName).Replace('\', '/')
                $relative -notin @($releaseScripts + @('publisher.cer')) -and
                    $relative -ne 'installed-version.v1.json' -and
                    -not $relative.StartsWith('mobile/', [StringComparison]::OrdinalIgnoreCase)
            } | Sort-Object FullName)
        $packageStream = [IO.FileStream]::new(
            $packagePath,
            [IO.FileMode]::CreateNew,
            [IO.FileAccess]::ReadWrite,
            [IO.FileShare]::None)
        try {
            $archive = [IO.Compression.ZipArchive]::new(
                $packageStream,
                [IO.Compression.ZipArchiveMode]::Create,
                $true,
                [Text.Encoding]::UTF8)
            try {
                foreach ($file in $packageSourceFiles) {
                    $relative = [IO.Path]::GetRelativePath($outputRoot, $file.FullName).Replace('\', '/')
                    $entry = $archive.CreateEntry($relative, [IO.Compression.CompressionLevel]::Optimal)
                    $entry.LastWriteTime = [DateTimeOffset]::new(1980, 1, 1, 0, 0, 0, [TimeSpan]::Zero)
                    $input = $file.OpenRead()
                    $output = $entry.Open()
                    try {
                        $input.CopyTo($output, 131072)
                    } finally {
                        $output.Dispose()
                        $input.Dispose()
                    }
                }
            } finally {
                $archive.Dispose()
            }
            $packageStream.Flush($true)
        } finally {
            $packageStream.Dispose()
        }

        $packageFiles = @()
        foreach ($file in $packageSourceFiles) {
            $relative = [IO.Path]::GetRelativePath($outputRoot, $file.FullName).Replace('\', '/')
            $packageFiles += [ordered]@{
                path = $relative
                sizeBytes = $file.Length
                sha256 = Get-Sha256Hex -Path $file.FullName
            }
        }
        $packageUri = [uri]::new($PackageBaseUri, $packageFileName)
        $updateManifest = [ordered]@{
            schemaVersion = 1
            productId = 'muhun.mcsv.manager'
            version = $Version
            channel = $Channel
            runtimeIdentifier = 'win-x64'
            publishedAtUtc = [DateTimeOffset]::UtcNow.ToString('O')
            keyId = $publicKeyDocument.keyId
            signatureAlgorithm = 'rsa-pss-sha256'
            package = [ordered]@{
                url = $packageUri.AbsoluteUri
                sizeBytes = (Get-Item -LiteralPath $packagePath).Length
                sha256 = Get-Sha256Hex -Path $packagePath
            }
            entryPoint = 'gui-win-x64/Muhun MCSV Manager.exe'
            files = $packageFiles
        }
        $updateManifestBytes = [Text.UTF8Encoding]::new($false).GetBytes(
            ($updateManifest | ConvertTo-Json -Depth 8 -Compress) + [Environment]::NewLine)
        $updateSignature = $certificateRsa.SignData(
            $updateManifestBytes,
            [Security.Cryptography.HashAlgorithmName]::SHA256,
            [Security.Cryptography.RSASignaturePadding]::Pss)
        Write-AtomicBytes -Path (Join-Path $outputRoot 'update-manifest.json') -Bytes $updateManifestBytes
        Write-AtomicBytes -Path (Join-Path $outputRoot 'update-manifest.json.sig') -Bytes $updateSignature

        # This is an installable release directory, not a portable single-EXE bundle. Keep the
        # first-run instructions at release-root level (outside the updater payload), then bind
        # them into the signed release manifest and SHA256SUMS below like every other release file.
        $gettingStartedLines = @(
            "X MCSV $Version 正式發行包 — 開始使用",
            '',
            '重要：這不是可攜式（portable）單一 EXE。請勿直接從發行資料夾執行 GUI EXE；必須先完成安裝。',
            '',
            '安裝需求：',
            '1. Windows x64。',
            '2. PowerShell 7.4 或更新版本（命令為 pwsh，不是 Windows PowerShell 5.1）。',
            '3. 以系統管理員身分開啟 PowerShell 7。',
            '',
            '安裝步驟：',
            '1. 將完整發行包解壓縮到一般本機資料夾，並在該資料夾開啟 PowerShell 7。',
            '2. 先將下方顯示的 publisher.cer SHA-256 與 X MCSV GitHub Release 的獨立正式公告核對；只有完全一致才能輸入 YES。',
            '3. 再依序匯入本機電腦 Root 與 TrustedPublisher、啟用 Process AllSigned、驗證腳本並安裝；請勿改用 Bypass 執行政策：',
            '',
            '$release = (Get-Location).Path',
            '$expectedPublisherCertificateSha256 = ''1a67e65dc9c367ac3247d0483edbe94dab38c5494859a43210c1ad4719e80b71''',
            '$publisherCertificatePath = Join-Path $release ''publisher.cer''',
            '$publisherCertificateSha256 = (Get-FileHash -LiteralPath $publisherCertificatePath -Algorithm SHA256).Hash.ToLowerInvariant()',
            'Write-Host "publisher.cer SHA-256: $publisherCertificateSha256"',
            'Write-Host ''請與 X MCSV GitHub Release 的獨立正式公告核對上方 SHA-256。''',
            'if ($publisherCertificateSha256 -cne $expectedPublisherCertificateSha256) { throw ''publisher.cer SHA-256 與 X MCSV 固定正式發布者指紋不符。'' }',
            '$publisherConfirmation = Read-Host ''完成獨立公告核對且完全一致後輸入 YES''',
            'if ($publisherConfirmation -cne ''YES'') { throw ''尚未確認獨立正式公告中的發布者指紋，停止安裝。'' }',
            '& "$env:SystemRoot\System32\certutil.exe" -addstore -f Root $publisherCertificatePath',
            'if ($LASTEXITCODE -ne 0) { throw ''publisher.cer 匯入 LocalMachine Root 失敗。'' }',
            '& "$env:SystemRoot\System32\certutil.exe" -addstore -f TrustedPublisher $publisherCertificatePath',
            'if ($LASTEXITCODE -ne 0) { throw ''publisher.cer 匯入 LocalMachine TrustedPublisher 失敗。'' }',
            'Set-ExecutionPolicy -Scope Process -ExecutionPolicy AllSigned -Force',
            'foreach ($scriptName in @(''Test-MuhunMcsvRelease.ps1'', ''Install-MuhunMcsv.ps1'', ''Uninstall-MuhunMcsv.ps1'')) {',
            '    $signature = Get-AuthenticodeSignature -LiteralPath (Join-Path $release $scriptName)',
            '    $signatureSha256 = if ($null -eq $signature.SignerCertificate) { '''' } else { [Convert]::ToHexString([Security.Cryptography.SHA256]::HashData($signature.SignerCertificate.RawData)).ToLowerInvariant() }',
            '    if ($signature.Status -ne ''Valid'' -or $signatureSha256 -cne $expectedPublisherCertificateSha256) { throw "$scriptName 的 Authenticode 或發布者指紋無效。" }',
            '}',
            '& (Join-Path $release ''Test-MuhunMcsvRelease.ps1'') -ReleaseDirectory $release',
            '& (Join-Path $release ''Install-MuhunMcsv.ps1'') -SourceDirectory $release',
            '',
            '驗證與安裝都成功後，請從 Windows 開始功能表的「X MCSV」捷徑啟動 GUI。',
            '日後不需要再從本發行資料夾直接執行 EXE。'
        )
        # Migration note only; this legacy product name is intentionally not emitted into the
        # guide. Existing releases called the Start Menu entry Muhun MCSV Manager.
        Write-AtomicUtf8Text -Path (Join-Path $outputRoot '開始使用.txt') `
            -Value (($gettingStartedLines -join [Environment]::NewLine) + [Environment]::NewLine)

        $authenticodeFiles = @(
            $requiredExecutables +
            $releaseScripts +
            @('tools/Uninstall-MuhunMcsv.ps1')
        )
        $releaseFileObjects = @()
        $releaseFiles = @(Get-ChildItem -LiteralPath $outputRoot -Recurse -File -Force |
            Where-Object { $_.Name -notin @('release-manifest.json', 'release-manifest.json.sig', 'SHA256SUMS.txt') } |
            Sort-Object FullName)
        foreach ($file in $releaseFiles) {
            $relative = [IO.Path]::GetRelativePath($outputRoot, $file.FullName).Replace('\', '/')
            $releaseFileObjects += [ordered]@{
                path = $relative
                sizeBytes = $file.Length
                sha256 = Get-Sha256Hex -Path $file.FullName
            }
        }

        $releaseManifest = [ordered]@{
            schemaVersion = 1
            productId = 'muhun.mcsv.manager'
            version = $Version
            channel = $Channel
            runtimeIdentifier = 'win-x64'
            installable = $true
            generatedAtUtc = [DateTimeOffset]::UtcNow.ToString('O')
            signatureAlgorithm = 'rsa-pss-sha256'
            keyId = $publicKeyDocument.keyId
            publisherTrustMode = $PublisherTrustMode
            publisherCertificateSha256 = $script:publisherCertificateSha256
            entryPoint = 'gui-win-x64/Muhun MCSV Manager.exe'
            serviceEntryPoint = 'service-win-x64/Muhun MCSV Service.exe'
            updaterEntryPoint = 'updater-win-x64/Muhun MCSV Updater.exe'
            authenticodeFiles = $authenticodeFiles
            updatePublicKey = [ordered]@{ path = 'update-signing-public-key.json' }
            updateManifest = [ordered]@{
                path = 'update-manifest.json'
                signaturePath = 'update-manifest.json.sig'
            }
            builtinProvider = [ordered]@{
                deploymentPath = 'providers/muhun.catalog/deployment.v1.json'
                packagePath = 'providers/muhun.catalog/muhun.catalog.mcsvp'
                publicKeyPath = 'providers/muhun.catalog/publisher-public.pem'
                publicKeySha256 = $script:providerPublicKeySha256
                packageSha256 = $providerPackageSha256
                providerId = 'muhun.catalog'
                providerVersion = $Version
                publisherId = 'muhun.firstparty'
                signatureAlgorithm = 'ECDSA-P256-SHA256'
                authenticodeEntryPoint = 'Muhun.MCSV.BuiltinProvider.exe'
            }
            androidApk = [ordered]@{
                path = 'mobile/Muhun-MCSV-Remote.apk'
                metadataPath = 'mobile/android-release.v3.json'
                metadataSizeBytes = [long]$mobileMetadataRelease.Length
                metadataSha256 = $mobileMetadataReleaseSha256
                packageId = 'com.muhun.mcsv.remote'
                version = $Version
                versionCode = [int]$AndroidVersionCode
                sizeBytes = [long]$mobileMetadata.sizeBytes
                sha256 = [string]$mobileMetadata.sha256
                signingCertificateSha256 = [string]$mobileMetadata.signingCertificateSha256
                v4SignaturePath = 'mobile/Muhun-MCSV-Remote.apk.idsig'
                v4SignatureSizeBytes = [long]$mobileMetadata.v4SignatureSizeBytes
                v4SignatureSha256 = [string]$mobileMetadata.v4SignatureSha256
                verifiedSignatureSchemes = @('v2', 'v3', 'v4')
                toolchainReceiptPath = 'mobile/android-toolchain.v1.json'
                toolchainReceiptSizeBytes = [long]$mobileToolchainReceipt.SizeBytes
                toolchainReceiptSha256 = [string]$mobileToolchainReceipt.Sha256
            }
            package = [ordered]@{
                path = $packageFileName
                url = $packageUri.AbsoluteUri
                sizeBytes = (Get-Item -LiteralPath $packagePath).Length
                sha256 = Get-Sha256Hex -Path $packagePath
            }
            files = $releaseFileObjects
        }
        $releaseManifestBytes = [Text.UTF8Encoding]::new($false).GetBytes(
            ($releaseManifest | ConvertTo-Json -Depth 8 -Compress) + [Environment]::NewLine)
        $releaseSignature = $certificateRsa.SignData(
            $releaseManifestBytes,
            [Security.Cryptography.HashAlgorithmName]::SHA256,
            [Security.Cryptography.RSASignaturePadding]::Pss)
        Write-AtomicBytes -Path (Join-Path $outputRoot 'release-manifest.json') -Bytes $releaseManifestBytes
        Write-AtomicBytes -Path (Join-Path $outputRoot 'release-manifest.json.sig') -Bytes $releaseSignature

        $checksumLines = @($releaseFileObjects | ForEach-Object { "$($_.sha256) *$($_.path)" })
        Write-AtomicUtf8Text -Path (Join-Path $outputRoot 'SHA256SUMS.txt') `
            -Value (($checksumLines -join [Environment]::NewLine) + [Environment]::NewLine)

        $verificationArguments = @{
            ReleaseDirectory = $outputRoot
            AndroidApkSignerPath = $androidApkSigner
            AndroidAapt2Path = $androidAapt2
        }
        if ($PublisherTrustMode -eq 'self-signed-local') {
            $verificationArguments.AllowUntrustedSelfSigned = $true
        }
        & (Join-Path $PSScriptRoot 'Test-MuhunMcsvRelease.ps1') @verificationArguments | Out-Host
    } finally {
        $certificateRsa.Dispose()
    }
} catch {
    $failure = $_
    if (Test-Path -LiteralPath $outputRoot -PathType Container) {
        $failedMarker = Join-Path $outputRoot 'RELEASE-FAILED.txt'
        try {
            [IO.File]::WriteAllText(
                $failedMarker,
                "Release validation failed. This directory is not installable.`r`n",
                [Text.UTF8Encoding]::new($false))
        } catch { }
    }
    throw $failure
} finally {
    if ($null -ne $certificate) { $certificate.Dispose() }
    if ($null -ne $providerEcdsa) { $providerEcdsa.Dispose() }
    if ($passwordPointer -ne [IntPtr]::Zero) {
        [Runtime.InteropServices.Marshal]::ZeroFreeBSTR($passwordPointer)
    }
    if ($providerPasswordPointer -ne [IntPtr]::Zero) {
        [Runtime.InteropServices.Marshal]::ZeroFreeBSTR($providerPasswordPointer)
    }
    $passwordText = $null
    $providerPasswordText = $null
}

Write-Host "Verified signed release created in: $outputRoot"
if ($PublisherTrustMode -eq 'self-signed-local') {
    Write-Warning 'Self-signed Authenticode is not public trust. The installer will fail closed until the publisher certificate is explicitly trusted on that Windows machine.'
}
