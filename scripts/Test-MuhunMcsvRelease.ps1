#requires -Version 7.4

[CmdletBinding()]
param(
    [Parameter(Mandatory = $true)]
    [string]$ReleaseDirectory,

    [string]$AndroidApkSignerPath,

    [string]$AndroidAapt2Path,

    [switch]$AllowUntrustedSelfSigned
)

$ErrorActionPreference = 'Stop'
Set-StrictMode -Version Latest
$PSNativeCommandUseErrorActionPreference = $false

if (-not $IsWindows) {
    throw 'Muhun MCSV Windows release verification must run on Windows.'
}

$releaseRoot = [IO.Path]::GetFullPath($ReleaseDirectory).TrimEnd('\', '/')
$expectedPublisherCertificateSha256 = '1a67e65dc9c367ac3247d0483edbe94dab38c5494859a43210c1ad4719e80b71'
$androidBuildToolsVersion = '36.0.0'
$pinnedAndroidToolRecords = @(
    [pscustomobject]@{
        relativePath = 'aapt2.exe'
        sizeBytes = 5423200L
        sha256 = 'babf3122e515ddb954c5ac4669e085ce990536c035e3072de30127bddd6e3608'
        maximumBytes = 16MB
    },
    [pscustomobject]@{
        relativePath = 'apksigner.bat'
        sizeBytes = 3233L
        sha256 = '549dd0028b0314a5112d6b56e2de7800e713f297da4508b513a735546e52ce38'
        maximumBytes = 64KB
    },
    [pscustomobject]@{
        relativePath = 'lib/apksigner.jar'
        sizeBytes = 1100545L
        sha256 = '3716d9311e55d2b0918a2fd9d54ba9e406c5f6abeea700b287f11259bc163dec'
        maximumBytes = 8MB
    }
)
if (-not (Test-Path -LiteralPath $releaseRoot -PathType Container)) {
    throw 'ReleaseDirectory does not exist.'
}
if ([string]::IsNullOrWhiteSpace($AndroidApkSignerPath) -ne
    [string]::IsNullOrWhiteSpace($AndroidAapt2Path)) {
    throw 'Android APK verifier and identity tool must be supplied together.'
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

function Resolve-ReleaseFile {
    param([Parameter(Mandatory = $true)][string]$RelativePath)

    if (-not (Test-SafeRelativePath -Path $RelativePath)) {
        throw "Release metadata contains an unsafe path: $RelativePath"
    }

    $candidate = [IO.Path]::GetFullPath((Join-Path $releaseRoot $RelativePath.Replace('/', '\')))
    $normalizedRoot = $releaseRoot + [IO.Path]::DirectorySeparatorChar
    if (-not $candidate.StartsWith($normalizedRoot, [StringComparison]::OrdinalIgnoreCase)) {
        throw "Release path escapes the release root: $RelativePath"
    }

    if (-not (Test-Path -LiteralPath $candidate -PathType Leaf)) {
        throw "Release file is missing: $RelativePath"
    }

    $cursor = Get-Item -LiteralPath $candidate -Force
    while ($null -ne $cursor -and
        $cursor.FullName.StartsWith($normalizedRoot, [StringComparison]::OrdinalIgnoreCase)) {
        if (($cursor.Attributes -band [IO.FileAttributes]::ReparsePoint) -ne 0) {
            throw "Release file traverses a reparse point: $RelativePath"
        }
        $cursor = if ($cursor -is [IO.DirectoryInfo]) { $cursor.Parent } else { $cursor.Directory }
    }

    return $candidate
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
    if ([string]::IsNullOrWhiteSpace($AndroidApkSignerPath) -or
        [string]::IsNullOrWhiteSpace($AndroidAapt2Path)) {
        throw 'Pinned Android tools were not supplied.'
    }
    $aapt2 = [IO.Path]::GetFullPath($AndroidAapt2Path)
    $apkSigner = [IO.Path]::GetFullPath($AndroidApkSignerPath)
    $buildToolsRoot = [IO.Path]::GetFullPath((Split-Path -Parent $aapt2)).TrimEnd('\', '/')
    if ((Split-Path -Leaf $buildToolsRoot) -cne $androidBuildToolsVersion) {
        throw 'Android build-tools directory does not match the pinned version.'
    }
    $paths = @{
        'aapt2.exe' = $aapt2
        'apksigner.bat' = $apkSigner
        'lib/apksigner.jar' = [IO.Path]::GetFullPath(
            (Join-Path $buildToolsRoot 'lib\apksigner.jar'))
    }
    foreach ($record in $pinnedAndroidToolRecords) {
        $path = $paths[$record.relativePath]
        $expectedPath = [IO.Path]::GetFullPath(
            (Join-Path $buildToolsRoot $record.relativePath.Replace('/', '\')))
        if ($path -cne $expectedPath -or
            -not (Test-Path -LiteralPath $path -PathType Leaf)) {
            throw "Pinned Android build tool is missing or outside build-tools ${androidBuildToolsVersion}: $($record.relativePath)"
        }
        Assert-NoReparseAncestors -Path $path -Label "Android build tool $($record.relativePath)"
        $file = Get-Item -LiteralPath $path -Force
        if ($file.Length -ne $record.sizeBytes -or $file.Length -gt $record.maximumBytes -or
            (Get-Sha256Hex -Path $path) -cne $record.sha256) {
            throw "Android build tool failed its fixed SHA-256 or size check: $($record.relativePath)"
        }
    }
    return [pscustomobject]@{
        ApkSigner = $apkSigner
        Aapt2 = $aapt2
    }
}

function Assert-AndroidToolchainReceipt {
    param([Parameter(Mandatory = $true)][string]$Path)
    $file = Get-Item -LiteralPath $Path -Force -ErrorAction Stop
    if ($file.Length -lt 1 -or $file.Length -gt 32KB) {
        throw 'Android toolchain receipt has an invalid size.'
    }
    try {
        $receipt = Get-Content -LiteralPath $Path -Raw | ConvertFrom-Json -Depth 8
    } catch {
        throw "Android toolchain receipt is invalid JSON: $($_.Exception.Message)"
    }
    Assert-ExactJsonProperties -Object $receipt `
        -Expected @('schemaVersion', 'buildToolsVersion', 'tools') `
        -Label 'Android toolchain receipt'
    if (-not ($receipt.schemaVersion -is [long]) -or
        $receipt.schemaVersion -ne 1 -or
        $receipt.buildToolsVersion -cne $androidBuildToolsVersion -or
        @($receipt.tools).Count -ne $pinnedAndroidToolRecords.Count) {
        throw 'Android toolchain receipt schema or tool count is invalid.'
    }
    for ($index = 0; $index -lt $pinnedAndroidToolRecords.Count; $index++) {
        $actual = @($receipt.tools)[$index]
        $expected = $pinnedAndroidToolRecords[$index]
        Assert-ExactJsonProperties -Object $actual `
            -Expected @('relativePath', 'sizeBytes', 'sha256') `
            -Label "Android toolchain receipt record $index"
        if ($actual.relativePath -cne $expected.relativePath -or
            -not ($actual.sizeBytes -is [long]) -or
            $actual.sizeBytes -ne $expected.sizeBytes -or
            $actual.sha256 -cne $expected.sha256) {
            throw "Android toolchain receipt does not bind the pinned tool: $($expected.relativePath)"
        }
    }
    return [pscustomobject]@{
        Receipt = $receipt
        SizeBytes = $file.Length
        Sha256 = Get-Sha256Hex -Path $Path
    }
}

function Get-CertificateSha256 {
    param([Parameter(Mandatory = $true)]$Certificate)
    return [Convert]::ToHexString(
        [Security.Cryptography.SHA256]::HashData($Certificate.RawData)).ToLowerInvariant()
}

function Assert-ReleasePowerShellScript {
    param(
        [Parameter(Mandatory = $true)][string]$Path,
        [Parameter(Mandatory = $true)][string]$Label
    )

    $bytes = [IO.File]::ReadAllBytes($Path)
    if ($bytes.Length -lt 4 -or $bytes[0] -ne 0xEF -or
        $bytes[1] -ne 0xBB -or $bytes[2] -ne 0xBF) {
        throw "$Label must use UTF-8 with BOM so Authenticode cannot apply a lossy ANSI round-trip."
    }

    $strictUtf8 = [Text.UTF8Encoding]::new($false, $true)
    try {
        $text = $strictUtf8.GetString($bytes, 3, $bytes.Length - 3)
    } catch {
        throw "$Label is not strict UTF-8 after Authenticode signing."
    }
    $signatureBlockPattern =
        '(?ms)(?:\A|\r?\n)# SIG # Begin signature block\r?\n' +
        '(?:#[^\r\n]*(?:\r?\n|$))*# SIG # End signature block(?:\r?\n)?\z'
    $signatureMatch = [regex]::Match($text, $signatureBlockPattern)
    if ($text.Contains([char]0xFFFD) -or -not $signatureMatch.Success -or
        $text.Substring(0, $signatureMatch.Index) -notmatch
            '^#requires\s+-Version\s+7\.4(?:\r?\n)') {
        throw "$Label contains damaged text, invalid signature framing or no PowerShell 7.4 requirement."
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
        throw "$Label does not parse after Authenticode signing: $details"
    }
}

function Assert-FormalProductVersion {
    param(
        [Parameter(Mandatory = $true)][string]$Path,
        [Parameter(Mandatory = $true)][string]$ExpectedVersion,
        [Parameter(Mandatory = $true)][string]$Label
    )

    $versionParts = $ExpectedVersion.Split('-', 2)[0].Split('.')
    $expectedNumericVersion = "$($versionParts[0]).$($versionParts[1]).$($versionParts[2]).0"
    $maximumVersionInfoReadAttempts = 4
    $versionInfo = $null
    $productVersion = ''
    $fileVersion = ''
    for ($attempt = 1; $attempt -le $maximumVersionInfoReadAttempts; $attempt++) {
        # Keep bounded protection for a genuinely transient double-empty metadata read after
        # extraction; partial or incorrect metadata still fails at once.
        $versionInfo = [Diagnostics.FileVersionInfo]::GetVersionInfo($Path)
        $productVersion = ([string]$versionInfo.ProductVersion).Trim()
        $fileVersion = ([string]$versionInfo.FileVersion).Trim()
        if ($productVersion -ceq $ExpectedVersion -and
            $fileVersion -ceq $expectedNumericVersion) {
            break
        }
        if (-not ([string]::IsNullOrWhiteSpace($productVersion) -and
                [string]::IsNullOrWhiteSpace($fileVersion))) {
            break
        }
        if ($attempt -lt $maximumVersionInfoReadAttempts) {
            [Threading.Thread]::Sleep(100)
        }
    }
    if ([string]::IsNullOrWhiteSpace($productVersion) -or
        $productVersion -cne $ExpectedVersion -or
        $fileVersion -cne $expectedNumericVersion) {
        throw "$Label ProductVersion/FileVersion does not equal signed release '$ExpectedVersion' / '$expectedNumericVersion'."
    }

    $visibleIdentity = @(
        [string]$versionInfo.ProductName,
        [string]$versionInfo.FileDescription,
        [string]$versionInfo.ProductVersion,
        [string]$versionInfo.FileVersion,
        [string]$versionInfo.Comments
    ) -join "`n"
    if ($visibleIdentity -match '(?i)(?:^|[^a-z])(preview|alpha)(?:[^a-z]|$)') {
        throw "$Label exposes a preview/alpha identity inside a formal release."
    }
}

function Assert-DetachedRsaPssSignature {
    param(
        [Parameter(Mandatory = $true)][Security.Cryptography.RSA]$Rsa,
        [Parameter(Mandatory = $true)][byte[]]$Content,
        [Parameter(Mandatory = $true)][byte[]]$Signature,
        [Parameter(Mandatory = $true)][string]$Label
    )

    if ($Rsa.KeySize -lt 3072 -or
        $Signature.Length -ne ($Rsa.KeySize / 8) -or
        -not $Rsa.VerifyData(
            $Content,
            $Signature,
            [Security.Cryptography.HashAlgorithmName]::SHA256,
            [Security.Cryptography.RSASignaturePadding]::Pss)) {
        throw "$Label RSA-PSS signature is invalid."
    }
}

function Get-ProviderSignaturePayload {
    param(
        [Parameter(Mandatory = $true)][long]$PackageLength,
        [Parameter(Mandatory = $true)][string]$PackageSha256
    )

    if ($PackageLength -lt 1 -or $PackageSha256 -notmatch '^[a-f0-9]{64}$') {
        throw 'Provider package trust context is invalid.'
    }
    $domain = [Text.Encoding]::UTF8.GetBytes("Muhun-MCSV-Provider-Package`0v1`0")
    $lengthBytes = [byte[]]::new(8)
    [Buffers.Binary.BinaryPrimitives]::WriteInt64BigEndian($lengthBytes, $PackageLength)
    $digest = [Convert]::FromHexString($PackageSha256)
    $payload = [byte[]]::new($domain.Length + $lengthBytes.Length + $digest.Length)
    [Array]::Copy($domain, 0, $payload, 0, $domain.Length)
    [Array]::Copy($lengthBytes, 0, $payload, $domain.Length, $lengthBytes.Length)
    [Array]::Copy(
        $digest,
        0,
        $payload,
        $domain.Length + $lengthBytes.Length,
        $digest.Length)
    [Security.Cryptography.CryptographicOperations]::ZeroMemory($digest)
    # Prevent PowerShell's pipeline from unrolling byte[] into Object[]; the verifier
    # needs the original typed buffer for ECDSA verification and secure zeroing.
    return ,$payload
}

function Assert-PublisherCertificate {
    param(
        [Parameter(Mandatory = $true)]$Certificate,
        [Parameter(Mandatory = $true)][string]$TrustMode
    )

    $now = [DateTime]::UtcNow
    if ($Certificate.NotBefore.ToUniversalTime() -gt $now -or
        $Certificate.NotAfter.ToUniversalTime() -le $now) {
        throw 'Publisher certificate is outside its validity period.'
    }

    $rsa = [Security.Cryptography.X509Certificates.RSACertificateExtensions]::GetRSAPublicKey(
        $Certificate)
    try {
        if ($null -eq $rsa -or $rsa.KeySize -lt 3072) {
            throw 'Publisher certificate must use RSA with at least 3072 bits.'
        }
    } finally {
        if ($null -ne $rsa) { $rsa.Dispose() }
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
        throw 'Publisher certificate does not contain the Code Signing EKU.'
    }

    $isSelfSigned = [Convert]::ToHexString($Certificate.SubjectName.RawData) -eq
        [Convert]::ToHexString($Certificate.IssuerName.RawData)
    if ($TrustMode -eq 'self-signed-local' -and -not $isSelfSigned) {
        throw 'Release trust mode says self-signed, but the publisher certificate is not self-signed.'
    }
    if ($TrustMode -eq 'public-ca' -and $isSelfSigned) {
        throw 'A public-CA release cannot use a self-signed publisher certificate.'
    }
}

$requiredMetadata = @(
    'release-manifest.json',
    'release-manifest.json.sig',
    'SHA256SUMS.txt',
    'publisher.cer',
    '開始使用.txt'
)
foreach ($relative in $requiredMetadata) {
    [void](Resolve-ReleaseFile -RelativePath $relative)
}

$manifestPath = Join-Path $releaseRoot 'release-manifest.json'
$manifestSignaturePath = Join-Path $releaseRoot 'release-manifest.json.sig'
$manifestBytes = [IO.File]::ReadAllBytes($manifestPath)
if ($manifestBytes.Length -lt 1 -or $manifestBytes.Length -gt 1MB) {
    throw 'release-manifest.json has an invalid size.'
}
$manifest = [Text.Encoding]::UTF8.GetString($manifestBytes) | ConvertFrom-Json
if ($manifest.schemaVersion -ne 1 -or
    $manifest.productId -ne 'muhun.mcsv.manager' -or
    $manifest.installable -ne $true -or
    $manifest.runtimeIdentifier -ne 'win-x64' -or
    $manifest.version -notmatch '^(0|[1-9][0-9]*)\.(0|[1-9][0-9]*)\.(0|[1-9][0-9]*)(?:-[0-9A-Za-z-]+(?:\.[0-9A-Za-z-]+)*)?$' -or
    $manifest.channel -notin @('stable', 'beta') -or
    $manifest.signatureAlgorithm -ne 'rsa-pss-sha256' -or
    $manifest.publisherTrustMode -notin @('self-signed-local', 'public-ca') -or
    $manifest.publisherCertificateSha256 -notmatch '^[a-f0-9]{64}$' -or
    $manifest.publisherCertificateSha256 -cne $expectedPublisherCertificateSha256 -or
    $manifest.keyId -notmatch '^[a-z][a-z0-9._-]{2,63}$') {
    throw 'Release manifest metadata is invalid or unsupported.'
}
if ($manifest.version -match '(?i)(?:^|[.-])(preview|alpha)(?:[.-]|$)' -or
    ($manifest.channel -eq 'stable' -and ([string]$manifest.version).Contains('-'))) {
    throw 'Formal release metadata contains a preview/alpha identity or a prerelease stable version.'
}

$publisherCertificate = [Security.Cryptography.X509Certificates.X509Certificate2]::new(
    [IO.File]::ReadAllBytes((Join-Path $releaseRoot 'publisher.cer')))
try {
    Assert-PublisherCertificate -Certificate $publisherCertificate -TrustMode $manifest.publisherTrustMode
    $publisherCertificateSha256 = Get-CertificateSha256 -Certificate $publisherCertificate
    if ($publisherCertificateSha256 -ne $manifest.publisherCertificateSha256) {
        throw 'Publisher certificate fingerprint does not match the signed release manifest.'
    }

    $publisherRsa = [Security.Cryptography.X509Certificates.RSACertificateExtensions]::GetRSAPublicKey(
        $publisherCertificate)
    try {
        Assert-DetachedRsaPssSignature -Rsa $publisherRsa -Content $manifestBytes `
            -Signature ([IO.File]::ReadAllBytes($manifestSignaturePath)) -Label 'Release manifest'

        $entries = @{}
        foreach ($entry in @($manifest.files)) {
            $relativePath = [string]$entry.path
            if (-not (Test-SafeRelativePath -Path $relativePath) -or
                $entry.sizeBytes -lt 0 -or $entry.sizeBytes -gt 2GB -or
                $entry.sha256 -notmatch '^[a-f0-9]{64}$' -or
                $entries.ContainsKey($relativePath.ToLowerInvariant())) {
                throw 'Release manifest contains an invalid or duplicate file entry.'
            }
            $entries[$relativePath.ToLowerInvariant()] = $entry
        }
        if ($entries.Count -lt 8 -or $entries.Count -gt 10000) {
            throw 'Release manifest file list is missing or too large.'
        }

        $checksumEntries = @{}
        foreach ($line in Get-Content -LiteralPath (Join-Path $releaseRoot 'SHA256SUMS.txt')) {
            if ($line -notmatch '^([a-f0-9]{64}) \*(.+)$' -or
                -not (Test-SafeRelativePath -Path $Matches[2])) {
                throw 'SHA256SUMS.txt format is invalid.'
            }
            $checksumKey = $Matches[2].ToLowerInvariant()
            if ($checksumEntries.ContainsKey($checksumKey)) {
                throw 'SHA256SUMS.txt contains a duplicate path.'
            }
            $checksumEntries[$checksumKey] = $Matches[1]
        }

        foreach ($entry in $entries.Values) {
            $path = Resolve-ReleaseFile -RelativePath $entry.path
            $actualHash = Get-Sha256Hex -Path $path
            if ((Get-Item -LiteralPath $path).Length -ne $entry.sizeBytes -or
                $actualHash -ne $entry.sha256 -or
                -not $checksumEntries.ContainsKey($entry.path.ToLowerInvariant()) -or
                $checksumEntries[$entry.path.ToLowerInvariant()] -ne $entry.sha256) {
                throw "Release file failed its signed hash or size check: $($entry.path)"
            }
        }
        if ($checksumEntries.Count -ne $entries.Count) {
            throw 'SHA256SUMS.txt does not exactly match the signed release file list.'
        }

        $excludedFiles = @(
            'release-manifest.json',
            'release-manifest.json.sig',
            'SHA256SUMS.txt'
        )
        $actualFiles = @(Get-ChildItem -LiteralPath $releaseRoot -Recurse -File -Force | ForEach-Object {
            if (($_.Attributes -band [IO.FileAttributes]::ReparsePoint) -ne 0) {
                throw "Release contains a reparse-point file: $($_.FullName)"
            }
            [IO.Path]::GetRelativePath($releaseRoot, $_.FullName).Replace('\', '/')
        } | Where-Object { $_ -notin $excludedFiles })
        if (@($actualFiles | Where-Object {
                $_.EndsWith('.pdb', [StringComparison]::OrdinalIgnoreCase)
            }).Count -ne 0) {
            throw 'Formal release contains a debug-symbol artifact.'
        }
        if ($actualFiles.Count -ne $entries.Count -or
            @($actualFiles | Where-Object { -not $entries.ContainsKey($_.ToLowerInvariant()) }).Count -ne 0) {
            throw 'Release directory contains missing or unexpected files.'
        }

        $gettingStartedRelativePath = '開始使用.txt'
        if (-not $entries.ContainsKey($gettingStartedRelativePath.ToLowerInvariant())) {
            throw 'The signed release manifest is missing 開始使用.txt.'
        }
        $gettingStartedPath = Resolve-ReleaseFile -RelativePath $gettingStartedRelativePath
        $gettingStartedBytes = [IO.File]::ReadAllBytes($gettingStartedPath)
        if ($gettingStartedBytes.Length -lt 1 -or $gettingStartedBytes.Length -gt 32KB) {
            throw '開始使用.txt has an invalid size.'
        }
        try {
            $gettingStartedText = [Text.UTF8Encoding]::new($false, $true).GetString(
                $gettingStartedBytes)
        } catch {
            throw '開始使用.txt must be strict UTF-8.'
        }
        $releaseStageLabel = if ($manifest.channel -eq 'beta') { '研發中' } else { '正式版' }
        $requiredGettingStartedText = @(
            "X MCSV $($manifest.version) 安裝程式 — $releaseStageLabel",
            "版本狀態：$releaseStageLabel。",
            '可直接安裝的單一 Setup EXE',
            "雙擊「Muhun-MCSV-$($manifest.version)-Setup.exe」",
            'Windows 使用者帳戶控制（UAC）',
            '自由選擇安全的本機安裝位置',
            '所有程式與永久資料都保存在所選安裝根目錄',
            'D:\MCSV 與其所有子目錄',
            '不會讀取、寫入、移動或刪除其中任何內容',
            'Windows 開始功能表的「X MCSV」捷徑啟動 GUI'
        )
        $forbiddenGettingStartedPattern =
            '(?i)PowerShell|pwsh|certutil|Set-ExecutionPolicy|Get-AuthenticodeSignature|' +
            'Install-MuhunMcsv\.ps1|Test-MuhunMcsvRelease\.ps1|Read-Host|publisher\.cer'
        $guideWithoutProtectedLegacyRoot = $gettingStartedText.Replace(
            'D:\MCSV',
            '',
            [StringComparison]::OrdinalIgnoreCase)
        if (@($requiredGettingStartedText | Where-Object {
                    -not $gettingStartedText.Contains($_, [StringComparison]::Ordinal)
                }).Count -ne 0 -or
            $gettingStartedText -match $forbiddenGettingStartedPattern -or
            $guideWithoutProtectedLegacyRoot -match '(?i)\b[a-z]:[\\/]' -or
            $gettingStartedText -match '(?i)(?:formal-release-output|new-chat[\\/]work)') {
            throw '開始使用.txt is incomplete, has the wrong version, requires a manual script, or contains an unexpected fixed build path.'
        }
        $orderedGettingStartedText = @(
            "雙擊「Muhun-MCSV-$($manifest.version)-Setup.exe」",
            'Windows 使用者帳戶控制（UAC）',
            '自由選擇安全的本機安裝位置',
            '按下「安裝」',
            '所有程式與永久資料都保存在所選安裝根目錄',
            'D:\MCSV 與其所有子目錄',
            'Windows 開始功能表的「X MCSV」捷徑啟動 GUI'
        )
        $previousGuideOffset = -1
        foreach ($orderedText in $orderedGettingStartedText) {
            $guideOffset = $gettingStartedText.IndexOf(
                $orderedText,
                $previousGuideOffset + 1,
                [StringComparison]::Ordinal)
            if ($guideOffset -lt 0) {
                throw '開始使用.txt does not preserve the required single-EXE installation order.'
            }
            $previousGuideOffset = $guideOffset
        }

        $requiredAuthenticodeFiles = @(
            "Muhun-MCSV-$($manifest.version)-Setup.exe",
            'service-win-x64/Muhun MCSV Service.exe',
            'gui-win-x64/Muhun MCSV Manager.exe',
            'updater-win-x64/Muhun MCSV Updater.exe',
            'Install-MuhunMcsv.ps1',
            'Uninstall-MuhunMcsv.ps1',
            'Test-MuhunMcsvRelease.ps1',
            'tools/Uninstall-MuhunMcsv.ps1'
        )
        $listedAuthenticodeFiles = @($manifest.authenticodeFiles)
        if ($listedAuthenticodeFiles.Count -ne $requiredAuthenticodeFiles.Count -or
            @($requiredAuthenticodeFiles | Where-Object { $_ -notin $listedAuthenticodeFiles }).Count -ne 0) {
            throw 'Release manifest does not contain the exact required Authenticode file set.'
        }
        foreach ($relativePath in $requiredAuthenticodeFiles) {
            $path = Resolve-ReleaseFile -RelativePath $relativePath
            if ($relativePath.EndsWith('.exe', [StringComparison]::OrdinalIgnoreCase)) {
                Assert-FormalProductVersion -Path $path -ExpectedVersion $manifest.version `
                    -Label $relativePath
            } elseif ($relativePath.EndsWith('.ps1', [StringComparison]::OrdinalIgnoreCase)) {
                Assert-ReleasePowerShellScript -Path $path -Label $relativePath
            }
            $signature = Get-AuthenticodeSignature -LiteralPath $path
            if ($null -eq $signature.SignerCertificate -or
                (Get-CertificateSha256 -Certificate $signature.SignerCertificate) -ne $publisherCertificateSha256 -or
                $null -eq $signature.TimeStamperCertificate) {
                throw "Authenticode publisher or timestamp is invalid: $relativePath"
            }

            if ($manifest.publisherTrustMode -eq 'public-ca') {
                if ($signature.Status -ne [Management.Automation.SignatureStatus]::Valid) {
                    throw "Public-CA Authenticode verification failed: $relativePath"
                }
            } elseif ($signature.Status -ne [Management.Automation.SignatureStatus]::Valid) {
                if (-not $AllowUntrustedSelfSigned -or
                    $signature.Status -notin @(
                        [Management.Automation.SignatureStatus]::NotTrusted,
                        [Management.Automation.SignatureStatus]::UnknownError)) {
                    throw "Self-signed Authenticode is not locally trusted: $relativePath"
                }
            }
        }
        $rootUninstallerHash = Get-Sha256Hex -Path (
            Resolve-ReleaseFile -RelativePath 'Uninstall-MuhunMcsv.ps1')
        $versionUninstallerHash = Get-Sha256Hex -Path (
            Resolve-ReleaseFile -RelativePath 'tools/Uninstall-MuhunMcsv.ps1')
        if ($rootUninstallerHash -cne $versionUninstallerHash) {
            throw 'The release-root and version-tree uninstallers are not byte-identical.'
        }

        $publicKeyPath = Resolve-ReleaseFile -RelativePath $manifest.updatePublicKey.path
        $publicKey = Get-Content -LiteralPath $publicKeyPath -Raw | ConvertFrom-Json
        if ($publicKey.schemaVersion -ne 1 -or
            $publicKey.productId -ne 'muhun.mcsv.manager' -or
            $publicKey.keyId -ne $manifest.keyId -or
            $publicKey.signatureAlgorithm -ne 'rsa-pss-sha256' -or
            $publicKey.keySize -lt 3072 -or
            $publicKey.publisherCertificateSha256 -ne $publisherCertificateSha256) {
            throw 'Update public-key document does not match the signed release manifest.'
        }
        $spki = [Convert]::FromBase64String($publicKey.subjectPublicKeyInfo)
        $spkiHash = [Convert]::ToHexString(
            [Security.Cryptography.SHA256]::HashData($spki)).ToLowerInvariant()
        if ($spkiHash -ne $publicKey.subjectPublicKeyInfoSha256 -or
            -not [Security.Cryptography.CryptographicOperations]::FixedTimeEquals(
                $spki,
                $publisherRsa.ExportSubjectPublicKeyInfo())) {
            throw 'Update public key does not match the publisher certificate.'
        }

        $updateManifestPath = Resolve-ReleaseFile -RelativePath $manifest.updateManifest.path
        $updateSignaturePath = Resolve-ReleaseFile -RelativePath $manifest.updateManifest.signaturePath
        $updateManifestBytes = [IO.File]::ReadAllBytes($updateManifestPath)
        Assert-DetachedRsaPssSignature -Rsa $publisherRsa -Content $updateManifestBytes `
            -Signature ([IO.File]::ReadAllBytes($updateSignaturePath)) -Label 'Update manifest'
        $updateManifest = [Text.Encoding]::UTF8.GetString($updateManifestBytes) | ConvertFrom-Json
        $expectedPackageFileName = "Muhun-MCSV-$($manifest.version)-win-x64.zip"
        if ($updateManifest.schemaVersion -ne 1 -or
            $updateManifest.productId -ne 'muhun.mcsv.manager' -or
            $updateManifest.version -ne $manifest.version -or
            $updateManifest.channel -ne $manifest.channel -or
            $updateManifest.runtimeIdentifier -ne 'win-x64' -or
            $updateManifest.keyId -ne $manifest.keyId -or
            $updateManifest.signatureAlgorithm -ne 'rsa-pss-sha256' -or
            $updateManifest.entryPoint -ne $manifest.entryPoint -or
            $manifest.package.path -cne $expectedPackageFileName -or
            ([string]$updateManifest.package.url) -cne ([string]$manifest.package.url) -or
            $updateManifest.package.sha256 -ne $manifest.package.sha256 -or
            $updateManifest.package.sizeBytes -ne $manifest.package.sizeBytes -or
            $updateManifest.package.sizeBytes -lt 1 -or
            $updateManifest.package.sizeBytes -gt 2GB -or
            $updateManifest.package.sha256 -notmatch '^[a-f0-9]{64}$') {
            throw 'Update manifest is inconsistent with the signed release manifest.'
        }

        [uri]$packageUri = $null
        $packageUrl = [string]$updateManifest.package.url
        if (-not [uri]::TryCreate($packageUrl, [UriKind]::Absolute, [ref]$packageUri) -or
            $packageUri.Scheme -cne 'https' -or
            -not $packageUri.IsDefaultPort -or
            [string]::IsNullOrWhiteSpace($packageUri.IdnHost) -or
            -not [string]::IsNullOrEmpty($packageUri.UserInfo) -or
            -not [string]::IsNullOrEmpty($packageUri.Query) -or
            -not [string]::IsNullOrEmpty($packageUri.Fragment)) {
            throw 'Update package URL must be a credential-free HTTPS default-port URL without query or fragment.'
        }
        $packageBaseUri = [uri]::new($packageUri, '.')
        $expectedPackageUri = [uri]::new($packageBaseUri, $expectedPackageFileName)
        if ($packageUrl -cne $expectedPackageUri.AbsoluteUri) {
            throw 'Update package URL host, base path, or package filename is not canonical.'
        }

        $servicePublicKeyPath = Resolve-ReleaseFile `
            -RelativePath 'service-win-x64/update-signing-public-key.json'
        if ((Get-Sha256Hex -Path $servicePublicKeyPath) -ne
            (Get-Sha256Hex -Path $publicKeyPath)) {
            throw 'Service-local update public key does not match the signed release key.'
        }
        $serviceSettingsPath = Resolve-ReleaseFile `
            -RelativePath 'service-win-x64/appsettings.json'
        if ((Get-Item -LiteralPath $serviceSettingsPath).Length -gt 64KB) {
            throw 'Service update settings exceed their formal size limit.'
        }
        $serviceSettings = Get-Content -LiteralPath $serviceSettingsPath -Raw | ConvertFrom-Json
        $expectedFeedUri = [uri]::new($packageUri, 'update-manifest.json').AbsoluteUri
        $updates = $serviceSettings.Mcsv.Service.Updates
        $configuredFeed = if ($manifest.channel -eq 'stable') {
            [string]$updates.StableManifestUrl
        } else {
            [string]$updates.BetaManifestUrl
        }
        $inactiveFeed = if ($manifest.channel -eq 'stable') {
            [string]$updates.BetaManifestUrl
        } else {
            [string]$updates.StableManifestUrl
        }
        $allowedFeedHosts = @($updates.AllowedFeedHosts)
        if ($configuredFeed -cne $expectedFeedUri -or
            -not [string]::IsNullOrEmpty($inactiveFeed) -or
            $allowedFeedHosts.Count -ne 1 -or
            ([string]$allowedFeedHosts[0]) -cne $packageUri.IdnHost -or
            -not [string]::IsNullOrEmpty([string]$updates.PublicKeyDocumentPath)) {
            throw 'Service update feed/key configuration is inconsistent with the signed package.'
        }

        $packagePath = Resolve-ReleaseFile -RelativePath $manifest.package.path
        if ((Get-Item -LiteralPath $packagePath).Length -ne $manifest.package.sizeBytes -or
            (Get-Sha256Hex -Path $packagePath) -ne $manifest.package.sha256) {
            throw 'Update package hash or size is invalid.'
        }

        $expectedPackageFiles = @{}
        $expectedTotalLength = 0L
        foreach ($file in @($updateManifest.files)) {
            if (-not (Test-SafeRelativePath -Path $file.path) -or
                $file.sizeBytes -lt 0 -or $file.sizeBytes -gt 2GB -or
                $file.sizeBytes -gt (2GB - $expectedTotalLength) -or
                $file.sha256 -notmatch '^[a-fA-F0-9]{64}$' -or
                $expectedPackageFiles.ContainsKey($file.path.ToLowerInvariant())) {
                throw 'Update manifest package file list is invalid.'
            }
            $expectedTotalLength += [long]$file.sizeBytes
            $expectedPackageFiles[$file.path.ToLowerInvariant()] = $file
        }
        if ($expectedPackageFiles.Count -lt 1 -or $expectedPackageFiles.Count -gt 10000) {
            throw 'Update manifest package file count is outside the product limit.'
        }
        if ($expectedPackageFiles.ContainsKey('installed-version.v1.json')) {
            throw 'Update package contains updater-owned installed-version metadata.'
        }
        foreach ($requiredPackageFile in @(
            'service-win-x64/Muhun MCSV Service.exe',
            'gui-win-x64/Muhun MCSV Manager.exe',
            'updater-win-x64/Muhun MCSV Updater.exe',
            'tools/Uninstall-MuhunMcsv.ps1',
            'service-win-x64/update-signing-public-key.json',
            'providers/muhun.catalog/deployment.v1.json',
            'providers/muhun.catalog/muhun.catalog.mcsvp',
            'providers/muhun.catalog/publisher-public.pem')) {
            if (-not $expectedPackageFiles.ContainsKey($requiredPackageFile)) {
                throw "Update package manifest is missing a required nested-layout file: $requiredPackageFile"
            }
        }

        $archive = [IO.Compression.ZipFile]::OpenRead($packagePath)
        try {
            if ($archive.Entries.Count -lt 1 -or
                $archive.Entries.Count -gt 10000 -or
                $archive.Entries.Count -ne $expectedPackageFiles.Count) {
                throw 'Update package entry count is outside the product limit or manifest contract.'
            }
            $seen = @{}
            $totalUncompressedLength = 0L
            $totalCompressedLength = 0L
            foreach ($zipEntry in $archive.Entries) {
                $entryPath = $zipEntry.FullName
                $key = $entryPath.ToLowerInvariant()
                if (-not (Test-SafeRelativePath -Path $entryPath) -or
                    -not $expectedPackageFiles.ContainsKey($key) -or
                    $seen.ContainsKey($key)) {
                    throw 'Update package contains an unsafe, unexpected or duplicate entry.'
                }
                $expected = $expectedPackageFiles[$key]
                if ($zipEntry.Length -lt 0 -or $zipEntry.Length -gt 2GB -or
                    $zipEntry.Length -gt (2GB - $totalUncompressedLength) -or
                    $zipEntry.CompressedLength -lt 0 -or
                    $zipEntry.CompressedLength -gt
                        ($manifest.package.sizeBytes - $totalCompressedLength) -or
                    ($zipEntry.Length -gt 0 -and $zipEntry.CompressedLength -eq 0) -or
                    ($zipEntry.Length -gt 1MB -and
                        ([double]$zipEntry.Length / [double]$zipEntry.CompressedLength) -gt 1000) -or
                    $zipEntry.Length -ne $expected.sizeBytes) {
                    throw "Update package file size is invalid: $entryPath"
                }
                $totalUncompressedLength += $zipEntry.Length
                $totalCompressedLength += $zipEntry.CompressedLength
                $stream = $zipEntry.Open()
                try {
                    $actualHash = (Get-FileHash -InputStream $stream -Algorithm SHA256).Hash.ToLowerInvariant()
                } finally {
                    $stream.Dispose()
                }
                if ($actualHash -ne ([string]$expected.sha256).ToLowerInvariant()) {
                    throw "Update package file hash is invalid: $entryPath"
                }
                $seen[$key] = $true
            }
            if ($seen.Count -ne $expectedPackageFiles.Count) {
                throw 'Update package is missing one or more signed files.'
            }
            if ($totalUncompressedLength -ne $expectedTotalLength) {
                throw 'Update package total expanded size does not match its signed manifest.'
            }
        } finally {
            $archive.Dispose()
        }

        $installedMetadataPath = Resolve-ReleaseFile -RelativePath 'installed-version.v1.json'
        if ((Get-Item -LiteralPath $installedMetadataPath).Length -gt 16KB) {
            throw 'Initial-install version metadata exceeds its size limit.'
        }
        $installedMetadata = Get-Content -LiteralPath $installedMetadataPath -Raw | ConvertFrom-Json
        if ($installedMetadata.schemaVersion -ne 1 -or
            $installedMetadata.productId -ne 'muhun.mcsv.manager' -or
            $installedMetadata.version -ne $manifest.version -or
            $installedMetadata.entryPoint -ne $manifest.entryPoint) {
            throw 'Initial-install version metadata does not match the signed release.'
        }

        $providerMetadata = $manifest.builtinProvider
        if ($providerMetadata.deploymentPath -ne 'providers/muhun.catalog/deployment.v1.json' -or
            $providerMetadata.packagePath -ne 'providers/muhun.catalog/muhun.catalog.mcsvp' -or
            $providerMetadata.publicKeyPath -ne 'providers/muhun.catalog/publisher-public.pem' -or
            $providerMetadata.providerId -ne 'muhun.catalog' -or
            $providerMetadata.providerVersion -ne $manifest.version -or
            $providerMetadata.publisherId -ne 'muhun.firstparty' -or
            $providerMetadata.signatureAlgorithm -ne 'ECDSA-P256-SHA256' -or
            $providerMetadata.authenticodeEntryPoint -ne 'Muhun.MCSV.BuiltinProvider.exe' -or
            $providerMetadata.publicKeySha256 -notmatch '^[a-f0-9]{64}$' -or
            $providerMetadata.packageSha256 -notmatch '^[a-f0-9]{64}$') {
            throw 'Builtin provider metadata is missing or invalid.'
        }

        $providerDescriptorPath = Resolve-ReleaseFile -RelativePath $providerMetadata.deploymentPath
        $providerPackagePath = Resolve-ReleaseFile -RelativePath $providerMetadata.packagePath
        $providerPublicKeyPath = Resolve-ReleaseFile -RelativePath $providerMetadata.publicKeyPath
        if ((Get-Item -LiteralPath $providerDescriptorPath).Length -gt 64KB -or
            (Get-Item -LiteralPath $providerPackagePath).Length -gt 256MB -or
            (Get-Item -LiteralPath $providerPublicKeyPath).Length -gt 16KB) {
            throw 'Builtin provider deployment files exceed their safety limits.'
        }
        $providerDescriptor = Get-Content -LiteralPath $providerDescriptorPath -Raw | ConvertFrom-Json
        $providerPackageLength = (Get-Item -LiteralPath $providerPackagePath).Length
        $providerPackageSha256 = Get-Sha256Hex -Path $providerPackagePath
        if ($providerDescriptor.schemaVersion -ne 1 -or
            $providerDescriptor.packageFileName -ne 'muhun.catalog.mcsvp' -or
            $providerDescriptor.publicKeyFileName -ne 'publisher-public.pem' -or
            $providerDescriptor.publicKeySha256 -ne $providerMetadata.publicKeySha256 -or
            $providerDescriptor.expectedSha256 -ne $providerPackageSha256 -or
            $providerDescriptor.expectedSha256 -ne $providerMetadata.packageSha256 -or
            $providerDescriptor.expectedProviderId -ne 'muhun.catalog' -or
            $providerDescriptor.expectedVersion -ne $manifest.version -or
            $providerDescriptor.expectedPublisherId -ne 'muhun.firstparty' -or
            $providerDescriptor.signature.publisherId -ne 'muhun.firstparty' -or
            $providerDescriptor.signature.algorithm -ne 'ECDSA-P256-SHA256' -or
            $providerDescriptor.signature.formatVersion -ne 1) {
            throw 'Builtin provider descriptor does not match the signed release.'
        }

        $providerVerifier = [Security.Cryptography.ECDsa]::Create()
        try {
            $providerVerifier.ImportFromPem((Get-Content -LiteralPath $providerPublicKeyPath -Raw))
            $providerSpki = $providerVerifier.ExportSubjectPublicKeyInfo()
            try {
                $providerSpkiSha256 = [Convert]::ToHexString(
                    [Security.Cryptography.SHA256]::HashData($providerSpki)).ToLowerInvariant()
                if ($providerVerifier.KeySize -ne 256 -or
                    $providerSpkiSha256 -ne $providerMetadata.publicKeySha256) {
                    throw 'Builtin provider public key is invalid.'
                }
            } finally {
                [Security.Cryptography.CryptographicOperations]::ZeroMemory($providerSpki)
            }
            $providerTrustPayload = Get-ProviderSignaturePayload `
                -PackageLength $providerPackageLength `
                -PackageSha256 $providerPackageSha256
            $providerDetachedSignature = [Convert]::FromBase64String(
                $providerDescriptor.signature.signatureBase64)
            try {
                if (-not $providerVerifier.VerifyData(
                        $providerTrustPayload,
                        $providerDetachedSignature,
                        [Security.Cryptography.HashAlgorithmName]::SHA256,
                        [Security.Cryptography.DSASignatureFormat]::Rfc3279DerSequence)) {
                    throw 'Builtin provider detached package signature is invalid.'
                }
            } finally {
                [Security.Cryptography.CryptographicOperations]::ZeroMemory($providerTrustPayload)
                [Security.Cryptography.CryptographicOperations]::ZeroMemory($providerDetachedSignature)
            }
        } finally {
            $providerVerifier.Dispose()
        }

        $providerArchive = [IO.Compression.ZipFile]::OpenRead($providerPackagePath)
        try {
            if ($providerArchive.Entries.Count -lt 2 -or $providerArchive.Entries.Count -gt 4096) {
                throw 'Builtin provider archive entry count is invalid.'
            }
            $providerArchiveEntries = @{}
            $providerTotalUncompressedLength = 0L
            $providerTotalCompressedLength = 0L
            foreach ($entry in $providerArchive.Entries) {
                if ($entry.FullName.EndsWith('/') -or
                    -not (Test-SafeRelativePath -Path $entry.FullName) -or
                    $providerArchiveEntries.ContainsKey($entry.FullName.ToLowerInvariant()) -or
                    $entry.Length -lt 0 -or $entry.Length -gt 128MB -or
                    $entry.Length -gt (512MB - $providerTotalUncompressedLength) -or
                    $entry.CompressedLength -lt 0 -or
                    $entry.CompressedLength -gt
                        ($providerPackageLength - $providerTotalCompressedLength) -or
                    ($entry.Length -gt 0 -and
                        ($entry.CompressedLength -eq 0 -or
                            ([double]$entry.Length / [double]$entry.CompressedLength) -gt 200))) {
                    throw 'Builtin provider archive contains an unsafe or duplicate entry.'
                }
                $providerTotalUncompressedLength += $entry.Length
                $providerTotalCompressedLength += $entry.CompressedLength
                $providerArchiveEntries[$entry.FullName.ToLowerInvariant()] = $entry
            }
            if (-not $providerArchiveEntries.ContainsKey('provider.manifest.json')) {
                throw 'Builtin provider archive is missing its manifest.'
            }
            $providerManifestEntry = $providerArchiveEntries['provider.manifest.json']
            if ($providerManifestEntry.Length -lt 2 -or $providerManifestEntry.Length -gt 128KB) {
                throw 'Builtin provider manifest size is invalid.'
            }
            $providerManifestStream = $providerManifestEntry.Open()
            try {
                $providerReader = [IO.StreamReader]::new(
                    $providerManifestStream,
                    [Text.UTF8Encoding]::new($false, $true),
                    $true,
                    4096,
                    $true)
                try {
                    $providerManifest = $providerReader.ReadToEnd() | ConvertFrom-Json
                } finally {
                    $providerReader.Dispose()
                }
            } finally {
                $providerManifestStream.Dispose()
            }
            if ($providerManifest.schemaVersion -ne 2 -or
                $providerManifest.id -ne 'muhun.catalog' -or
                $providerManifest.version -ne $manifest.version -or
                $providerManifest.apiVersion.major -ne 1 -or
                $providerManifest.apiVersion.minor -ne 2 -or
                $providerManifest.entryPoint -ne 'Muhun.MCSV.BuiltinProvider.exe' -or
                @($providerManifest.capabilities).Count -ne 1 -or
                $providerManifest.capabilities[0] -ne 'modpack.catalog' -or
                @($providerManifest.permissions).Count -ne 1 -or
                $providerManifest.permissions[0] -ne 'provider.http') {
                throw 'Builtin provider manifest identity or capability boundary is invalid.'
            }
            $providerFileEntries = @($providerManifest.fileSha256.psobject.Properties)
            if ($providerFileEntries.Count -ne ($providerArchiveEntries.Count - 1)) {
                throw 'Builtin provider digest table does not exactly match its archive.'
            }
            foreach ($digestEntry in $providerFileEntries) {
                $relativePath = [string]$digestEntry.Name
                $expectedDigest = [string]$digestEntry.Value
                $key = $relativePath.ToLowerInvariant()
                if (-not (Test-SafeRelativePath -Path $relativePath) -or
                    $relativePath.Equals('provider.manifest.json', [StringComparison]::OrdinalIgnoreCase) -or
                    $expectedDigest -notmatch '^[a-f0-9]{64}$' -or
                    -not $providerArchiveEntries.ContainsKey($key)) {
                    throw 'Builtin provider digest table is invalid.'
                }
                $payloadStream = $providerArchiveEntries[$key].Open()
                try {
                    $actualDigest = (Get-FileHash -InputStream $payloadStream -Algorithm SHA256).Hash.ToLowerInvariant()
                } finally {
                    $payloadStream.Dispose()
                }
                if ($actualDigest -ne $expectedDigest) {
                    throw "Builtin provider payload digest is invalid: $relativePath"
                }
            }

            $providerExecutableEntry = $providerArchiveEntries[
                ([string]$providerMetadata.authenticodeEntryPoint).ToLowerInvariant()]
            if ($null -eq $providerExecutableEntry) {
                throw 'Builtin provider archive is missing its Authenticode entry point.'
            }
            $providerExecutableTemp = Join-Path ([IO.Path]::GetTempPath()) `
                "muhun-provider-$([guid]::NewGuid().ToString('N')).exe"
            try {
                $providerSource = $providerExecutableEntry.Open()
                $providerDestination = [IO.FileStream]::new(
                    $providerExecutableTemp,
                    [IO.FileMode]::CreateNew,
                    [IO.FileAccess]::Write,
                    [IO.FileShare]::None)
                try {
                    $providerSource.CopyTo($providerDestination, 131072)
                    $providerDestination.Flush($true)
                } finally {
                    $providerDestination.Dispose()
                    $providerSource.Dispose()
                }
                $providerAuthenticode = Get-AuthenticodeSignature -LiteralPath $providerExecutableTemp
                Assert-FormalProductVersion -Path $providerExecutableTemp `
                    -ExpectedVersion $manifest.version -Label 'Builtin provider'
                if ($null -eq $providerAuthenticode.SignerCertificate -or
                    (Get-CertificateSha256 $providerAuthenticode.SignerCertificate) -ne
                        $publisherCertificateSha256 -or
                    $null -eq $providerAuthenticode.TimeStamperCertificate -or
                    ($manifest.publisherTrustMode -eq 'public-ca' -and
                        $providerAuthenticode.Status -ne
                            [Management.Automation.SignatureStatus]::Valid) -or
                    ($manifest.publisherTrustMode -eq 'self-signed-local' -and
                        $providerAuthenticode.Status -ne
                            [Management.Automation.SignatureStatus]::Valid -and
                        (-not $AllowUntrustedSelfSigned -or
                            $providerAuthenticode.Status -notin @(
                                [Management.Automation.SignatureStatus]::NotTrusted,
                                [Management.Automation.SignatureStatus]::UnknownError)))) {
                    throw 'Builtin provider Authenticode signature is invalid.'
                }
            } finally {
                if (Test-Path -LiteralPath $providerExecutableTemp -PathType Leaf) {
                    Remove-Item -LiteralPath $providerExecutableTemp -Force
                }
            }
        } finally {
            $providerArchive.Dispose()
        }

        $android = $manifest.androidApk
        Assert-ExactJsonProperties -Object $android `
            -Expected @(
                'path', 'metadataPath', 'metadataSizeBytes', 'metadataSha256',
                'packageId', 'version', 'versionCode', 'sizeBytes', 'sha256',
                'signingCertificateSha256', 'v4SignaturePath',
                'v4SignatureSizeBytes', 'v4SignatureSha256',
                'verifiedSignatureSchemes', 'toolchainReceiptPath',
                'toolchainReceiptSizeBytes', 'toolchainReceiptSha256'
            ) `
            -Label 'Signed Android release manifest entry'
        if ($android.path -ne 'mobile/Muhun-MCSV-Remote.apk' -or
            $android.v4SignaturePath -ne 'mobile/Muhun-MCSV-Remote.apk.idsig' -or
            $android.metadataPath -ne 'mobile/android-release.v3.json' -or
            $android.toolchainReceiptPath -ne 'mobile/android-toolchain.v1.json' -or
            $android.packageId -ne 'com.muhun.mcsv.remote' -or
            $android.version -ne $manifest.version -or
            -not ($android.versionCode -is [long]) -or
            $android.versionCode -lt 1 -or $android.versionCode -gt 999999999 -or
            ($manifest.version -eq '1.1.0' -and $android.versionCode -ne 10) -or
            $android.metadataSizeBytes -lt 1 -or $android.metadataSizeBytes -gt 64KB -or
            $android.metadataSha256 -notmatch '^[a-f0-9]{64}$' -or
            $android.sizeBytes -lt 1 -or $android.sizeBytes -gt 512MB -or
            $android.sha256 -notmatch '^[a-f0-9]{64}$' -or
            $android.signingCertificateSha256 -notmatch '^[a-f0-9]{64}$' -or
            $android.v4SignatureSizeBytes -lt 1 -or
            $android.v4SignatureSizeBytes -gt 16MB -or
            $android.v4SignatureSha256 -notmatch '^[a-f0-9]{64}$' -or
            $android.toolchainReceiptSizeBytes -lt 1 -or
            $android.toolchainReceiptSizeBytes -gt 32KB -or
            $android.toolchainReceiptSha256 -notmatch '^[a-f0-9]{64}$' -or
            @($android.verifiedSignatureSchemes).Count -ne 3 -or
            $android.verifiedSignatureSchemes[0] -ne 'v2' -or
            $android.verifiedSignatureSchemes[1] -ne 'v3' -or
            $android.verifiedSignatureSchemes[2] -ne 'v4') {
            throw 'Android APK release metadata is invalid.'
        }
        $androidApkPath = Resolve-ReleaseFile -RelativePath $android.path
        $androidV4SignaturePath = Resolve-ReleaseFile -RelativePath $android.v4SignaturePath
        $androidMetadataPath = Resolve-ReleaseFile -RelativePath $android.metadataPath
        $androidToolchainReceiptPath = Resolve-ReleaseFile `
            -RelativePath $android.toolchainReceiptPath
        try {
            $androidMetadata = Get-Content -LiteralPath $androidMetadataPath -Raw |
                ConvertFrom-Json -Depth 8
        } catch {
            throw "Android release metadata is invalid JSON: $($_.Exception.Message)"
        }
        Assert-ExactJsonProperties -Object $androidMetadata `
            -Expected @(
                'schemaVersion', 'productId', 'packageId', 'version', 'versionCode',
                'sizeBytes', 'sha256', 'signingCertificateSha256',
                'v4SignatureFileName', 'v4SignatureSizeBytes', 'v4SignatureSha256',
                'verifiedSignatureSchemes', 'toolchainReceiptFileName',
                'toolchainReceiptSizeBytes', 'toolchainReceiptSha256'
            ) `
            -Label 'Android release metadata'
        $androidToolchainReceipt = Assert-AndroidToolchainReceipt `
            -Path $androidToolchainReceiptPath
        if ((Get-Item -LiteralPath $androidApkPath).Length -ne $android.sizeBytes -or
            (Get-Sha256Hex -Path $androidApkPath) -ne $android.sha256 -or
            (Get-Item -LiteralPath $androidV4SignaturePath).Length -ne
                $android.v4SignatureSizeBytes -or
            (Get-Sha256Hex -Path $androidV4SignaturePath) -ne
                $android.v4SignatureSha256 -or
            (Get-Item -LiteralPath $androidMetadataPath).Length -ne
                $android.metadataSizeBytes -or
            (Get-Sha256Hex -Path $androidMetadataPath) -ne $android.metadataSha256 -or
            (Get-Item -LiteralPath $androidToolchainReceiptPath).Length -ne
                $android.toolchainReceiptSizeBytes -or
            (Get-Sha256Hex -Path $androidToolchainReceiptPath) -ne
                $android.toolchainReceiptSha256 -or
            -not ($androidMetadata.schemaVersion -is [long]) -or
            $androidMetadata.schemaVersion -ne 3 -or
            $androidMetadata.productId -ne 'muhun.mcsv.manager' -or
            $androidMetadata.packageId -ne $android.packageId -or
            $androidMetadata.version -ne $android.version -or
            -not ($androidMetadata.versionCode -is [long]) -or
            $androidMetadata.versionCode -ne $android.versionCode -or
            $androidMetadata.versionCode -lt 1 -or
            $androidMetadata.sizeBytes -ne $android.sizeBytes -or
            $androidMetadata.sha256 -ne $android.sha256 -or
            $androidMetadata.signingCertificateSha256 -ne $android.signingCertificateSha256 -or
            $androidMetadata.v4SignatureFileName -ne 'Muhun-MCSV-Remote.apk.idsig' -or
            $androidMetadata.v4SignatureSizeBytes -ne $android.v4SignatureSizeBytes -or
            $androidMetadata.v4SignatureSha256 -ne $android.v4SignatureSha256 -or
            $androidMetadata.toolchainReceiptFileName -cne 'android-toolchain.v1.json' -or
            $androidMetadata.toolchainReceiptSizeBytes -ne
                $androidToolchainReceipt.SizeBytes -or
            $androidMetadata.toolchainReceiptSha256 -cne
                $androidToolchainReceipt.Sha256 -or
            @($androidMetadata.verifiedSignatureSchemes).Count -ne 3 -or
            $androidMetadata.verifiedSignatureSchemes[0] -ne 'v2' -or
            $androidMetadata.verifiedSignatureSchemes[1] -ne 'v3' -or
            $androidMetadata.verifiedSignatureSchemes[2] -ne 'v4') {
            throw 'Android APK does not match its signed release metadata.'
        }

        if (-not [string]::IsNullOrWhiteSpace($AndroidApkSignerPath)) {
            $pinnedAndroidTools = Get-PinnedAndroidBuildTools
            $apkSigner = $pinnedAndroidTools.ApkSigner
            $aapt2 = $pinnedAndroidTools.Aapt2

            $apkVerification = @(& $apkSigner @(
                'verify', '--verbose', '--print-certs',
                '-v4-signature-file', $androidV4SignaturePath,
                $androidApkPath) 2>&1 | ForEach-Object { $_.ToString() })
            if ($LASTEXITCODE -ne 0) {
                throw 'Android APK signature verification failed.'
            }
            $apkVerificationText = $apkVerification -join "`n"
            if ($apkVerificationText -notmatch
                    'Verified using v2 scheme \(APK Signature Scheme v2\): true' -or
                $apkVerificationText -notmatch
                    'Verified using v3 scheme \(APK Signature Scheme v3\): true' -or
                $apkVerificationText -notmatch
                    'Verified using v4 scheme \(APK Signature Scheme v4\): true') {
                throw 'Android APK does not contain its required v2/v3/v4 signatures.'
            }
            $apkCertificateDigests = @([regex]::Matches(
                $apkVerificationText,
                '(?im)certificate SHA-256 digest:\s*([0-9a-f]{64})') | ForEach-Object {
                    $_.Groups[1].Value.ToLowerInvariant()
                } | Sort-Object -Unique)
            if ($apkCertificateDigests.Count -ne 1 -or
                $apkCertificateDigests[0] -ne $android.signingCertificateSha256) {
                throw 'Android APK signer does not match the signed release manifest.'
            }

            $apkBadging = @(& $aapt2 @('dump', 'badging', $androidApkPath) 2>&1 |
                ForEach-Object { $_.ToString() })
            if ($LASTEXITCODE -ne 0) {
                throw 'Android APK package identity verification failed.'
            }
            $apkBadgingText = $apkBadging -join "`n"
            if ($apkBadgingText -notmatch "package: name='com\.muhun\.mcsv\.remote'" -or
                $apkBadgingText -notmatch "versionCode='$($android.versionCode)'" -or
                $apkBadgingText -notmatch "versionName='$([regex]::Escape([string]$manifest.version))'") {
                throw 'Android APK package, versionName or versionCode identity is invalid.'
            }
        }
    } finally {
        $publisherRsa.Dispose()
    }
} finally {
    $publisherCertificate.Dispose()
}

[pscustomobject]@{
    ProductId = $manifest.productId
    Version = $manifest.version
    Channel = $manifest.channel
    RuntimeIdentifier = $manifest.runtimeIdentifier
    PublisherTrustMode = $manifest.publisherTrustMode
    PublisherCertificateSha256 = $manifest.publisherCertificateSha256
    KeyId = $manifest.keyId
    VerifiedFileCount = @($manifest.files).Count
    Package = $manifest.package.path
}
