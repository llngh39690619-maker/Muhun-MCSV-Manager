#requires -Version 7.4

[CmdletBinding()]
param(
    [Parameter(Mandatory = $true)]
    [string]$InstallerHostPath,

    [Parameter(Mandatory = $true)]
    [string]$ReleaseDirectory,

    [Parameter(Mandatory = $true)]
    [string]$OutputPath,

    [Parameter(Mandatory = $true)]
    [ValidatePattern('^(0|[1-9][0-9]*)\.(0|[1-9][0-9]*)\.(0|[1-9][0-9]*)(?:-[0-9A-Za-z-]+(?:\.[0-9A-Za-z-]+)*)?$')]
    [string]$Version,

    [ValidateSet('stable', 'beta')]
    [string]$Channel = 'beta'
)

$ErrorActionPreference = 'Stop'
Set-StrictMode -Version Latest

if (-not $IsWindows) {
    throw 'The X MCSV installer bundle must be produced on Windows.'
}

$hostPath = [IO.Path]::GetFullPath($InstallerHostPath)
$releaseRoot = [IO.Path]::GetFullPath($ReleaseDirectory).TrimEnd('\', '/')
$output = [IO.Path]::GetFullPath($OutputPath)
$outputParent = [IO.Path]::GetDirectoryName($output)
if (-not [IO.File]::Exists($hostPath) -or
    -not [IO.Directory]::Exists($releaseRoot) -or
    [string]::IsNullOrWhiteSpace($outputParent)) {
    throw 'Installer host, release directory or output path is invalid.'
}
if ([IO.File]::Exists($output) -or [IO.Directory]::Exists($output)) {
    throw 'Installer output must not already exist.'
}

function Assert-NoReparseAncestors {
    param([Parameter(Mandatory = $true)][string]$Path)
    $cursor = Get-Item -LiteralPath $Path -Force -ErrorAction Stop
    while ($null -ne $cursor) {
        if (($cursor.Attributes -band [IO.FileAttributes]::ReparsePoint) -ne 0) {
            throw "Installer input or output traverses a reparse point: $($cursor.FullName)"
        }
        $cursor = if ($cursor -is [IO.DirectoryInfo]) { $cursor.Parent } else { $cursor.Directory }
    }
}

Assert-NoReparseAncestors $hostPath
Assert-NoReparseAncestors $releaseRoot
[IO.Directory]::CreateDirectory($outputParent) | Out-Null
Assert-NoReparseAncestors $outputParent

$manifestPath = Join-Path $releaseRoot 'update-manifest.json'
$signaturePath = Join-Path $releaseRoot 'update-manifest.json.sig'
$publicKeyPath = Join-Path $releaseRoot 'update-signing-public-key.json'
$packageName = "Muhun-MCSV-$Version-win-x64.zip"
$packagePath = Join-Path $releaseRoot $packageName
foreach ($required in @($manifestPath, $signaturePath, $publicKeyPath, $packagePath)) {
    if (-not [IO.File]::Exists($required)) {
        throw "Formal release is missing an installer input: $required"
    }
    Assert-NoReparseAncestors $required
}

if ((Get-Item -LiteralPath $manifestPath -Force).Length -gt 256KB -or
    (Get-Item -LiteralPath $signaturePath -Force).Length -gt 1KB -or
    (Get-Item -LiteralPath $publicKeyPath -Force).Length -gt 16KB -or
    (Get-Item -LiteralPath $packagePath -Force).Length -gt 2GB) {
    throw 'An installer input exceeds its bounded size.'
}

$manifest = Get-Content -LiteralPath $manifestPath -Raw | ConvertFrom-Json -Depth 16
$package = Get-Item -LiteralPath $packagePath -Force
$packageSha256 = (Get-FileHash -LiteralPath $packagePath -Algorithm SHA256).Hash.ToLowerInvariant()
if ($manifest.schemaVersion -ne 1 -or
    $manifest.productId -cne 'muhun.mcsv.manager' -or
    $manifest.version -cne $Version -or
    $manifest.channel -cne $Channel -or
    $manifest.runtimeIdentifier -cne 'win-x64' -or
    $manifest.package.sizeBytes -ne $package.Length -or
    ([string]$manifest.package.sha256).ToLowerInvariant() -cne $packageSha256) {
    throw 'Signed update manifest does not match the requested installer bundle.'
}

$temporaryBundle = Join-Path $outputParent `
    ('.muhun-installer-bundle-' + [Guid]::NewGuid().ToString('N') + '.zip')
$temporaryOutput = $output + '.' + [Guid]::NewGuid().ToString('N') + '.tmp'
try {
    $bundleMetadata = [ordered]@{
        schemaVersion = 1
        productId = 'muhun.mcsv.manager'
        version = $Version
        channel = $Channel
        packageFileName = $packageName
        packageSizeBytes = $package.Length
        packageSha256 = $packageSha256
    }
    $metadataBytes = [Text.UTF8Encoding]::new($false).GetBytes(
        ($bundleMetadata | ConvertTo-Json -Depth 4 -Compress) + [Environment]::NewLine)
    $bundleStream = [IO.FileStream]::new(
        $temporaryBundle,
        [IO.FileMode]::CreateNew,
        [IO.FileAccess]::ReadWrite,
        [IO.FileShare]::None)
    try {
        $archive = [IO.Compression.ZipArchive]::new(
            $bundleStream,
            [IO.Compression.ZipArchiveMode]::Create,
            $true,
            [Text.Encoding]::UTF8)
        try {
            $inputs = @(
                [pscustomobject]@{ Name='installer-bundle.v1.json'; Bytes=$metadataBytes; Path=$null },
                [pscustomobject]@{ Name='update-manifest.json'; Bytes=$null; Path=$manifestPath },
                [pscustomobject]@{ Name='update-manifest.json.sig'; Bytes=$null; Path=$signaturePath },
                [pscustomobject]@{ Name='update-signing-public-key.json'; Bytes=$null; Path=$publicKeyPath },
                [pscustomobject]@{ Name=$packageName; Bytes=$null; Path=$packagePath }
            )
            foreach ($input in $inputs) {
                $compressionLevel = if ($input.Name -ceq $packageName) {
                    [IO.Compression.CompressionLevel]::NoCompression
                } else {
                    [IO.Compression.CompressionLevel]::Optimal
                }
                $entry = $archive.CreateEntry(
                    $input.Name,
                    $compressionLevel)
                $entry.LastWriteTime = [DateTimeOffset]::new(
                    1980, 1, 1, 0, 0, 0, [TimeSpan]::Zero)
                $entryStream = $entry.Open()
                try {
                    if ($null -ne $input.Bytes) {
                        $entryStream.Write($input.Bytes, 0, $input.Bytes.Length)
                    } else {
                        $source = [IO.File]::OpenRead($input.Path)
                        try { $source.CopyTo($entryStream, 131072) } finally { $source.Dispose() }
                    }
                } finally {
                    $entryStream.Dispose()
                }
            }
        } finally {
            $archive.Dispose()
        }
        $bundleStream.Flush($true)
    } finally {
        $bundleStream.Dispose()
    }

    $bundle = Get-Item -LiteralPath $temporaryBundle -Force
    if ($bundle.Length -lt 1 -or $bundle.Length -gt (2GB + 2MB)) {
        throw 'Generated installer bundle has an invalid size.'
    }
    $bundleHash = [Convert]::FromHexString(
        (Get-FileHash -LiteralPath $temporaryBundle -Algorithm SHA256).Hash)
    $magic = [Text.Encoding]::ASCII.GetBytes('MCSV-INSTALL-V1!')
    if ($magic.Length -ne 16 -or $bundleHash.Length -ne 32) {
        throw 'Installer trailer constants are invalid.'
    }

    [IO.File]::Copy($hostPath, $temporaryOutput, $false)
    $outputStream = [IO.FileStream]::new(
        $temporaryOutput,
        [IO.FileMode]::Append,
        [IO.FileAccess]::Write,
        [IO.FileShare]::None)
    try {
        $inputStream = [IO.File]::OpenRead($temporaryBundle)
        try { $inputStream.CopyTo($outputStream, 131072) } finally { $inputStream.Dispose() }
        $lengthBytes = [BitConverter]::GetBytes([long]$bundle.Length)
        if (-not [BitConverter]::IsLittleEndian) { [Array]::Reverse($lengthBytes) }
        $outputStream.Write($lengthBytes, 0, $lengthBytes.Length)
        $outputStream.Write($bundleHash, 0, $bundleHash.Length)
        $outputStream.Write($magic, 0, $magic.Length)
        $outputStream.Flush($true)
    } finally {
        $outputStream.Dispose()
    }

    [IO.File]::Move($temporaryOutput, $output)
} finally {
    foreach ($temporary in @($temporaryBundle, $temporaryOutput)) {
        if ([IO.File]::Exists($temporary)) {
            [IO.File]::Delete($temporary)
        }
    }
}

Write-Host "Single-EXE installer bundle created: $output"
