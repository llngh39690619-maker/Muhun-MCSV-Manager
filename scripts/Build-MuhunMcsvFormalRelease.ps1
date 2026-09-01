#requires -Version 7.4

[CmdletBinding()]
param(
    [Parameter(Mandatory = $true)]
    [ValidatePattern('^(0|[1-9][0-9]*)\.(0|[1-9][0-9]*)\.(0|[1-9][0-9]*)(?:-[0-9A-Za-z-]+(?:\.[0-9A-Za-z-]+)*)?$')]
    [string]$Version,

    [Parameter(Mandatory = $true)]
    [string]$OutputDirectory,

    [Parameter(Mandatory = $true)]
    [uri]$PackageBaseUri,

    [Parameter(Mandatory = $true)]
    [string]$SigningIdentityDirectory,

    [ValidateSet('stable', 'beta')]
    [string]$Channel = 'stable',

    [ValidateSet('self-signed-local', 'public-ca')]
    [string]$PublisherTrustMode = 'self-signed-local',

    [uri]$TimestampServerUrl = 'http://timestamp.digicert.com',

    [string]$MobileArtifactDirectory,

    [ValidateRange(1, 999999999)]
    [int]$AndroidVersionCode = 10,

    [string]$ToolingRoot,

    [ValidateRange(1, 64)]
    [int]$MaxBuildConcurrency = 4,

    [switch]$KeepStaging
)

$ErrorActionPreference = 'Stop'
Set-StrictMode -Version Latest

if (-not $IsWindows) {
    throw 'Formal win-x64 releases must be built on Windows.'
}

try {
    [Diagnostics.Process]::GetCurrentProcess().PriorityClass =
        [Diagnostics.ProcessPriorityClass]::BelowNormal
} catch {
    Write-Warning "Unable to lower the formal-build process priority: $($_.Exception.Message)"
}
if ($Version -match '(?i)(?:^|[.-])(preview|alpha)(?:[.-]|$)' -or
    ($Channel -eq 'stable' -and $Version.Contains('-'))) {
    throw 'Formal releases cannot use preview/alpha versions, and the stable channel requires a final semantic version.'
}
if ($Version -eq '1.1.0' -and $AndroidVersionCode -ne 10) {
    throw 'Formal Android 1.1.0 must use the immutable versionCode 10.'
}

$projectRoot = [IO.Path]::GetFullPath((Split-Path -Parent $PSScriptRoot)).TrimEnd('\', '/')
$solution = Join-Path $projectRoot 'MinecraftServerManager.sln'
$resolvedToolingRoot = if ([string]::IsNullOrWhiteSpace($ToolingRoot)) {
    [IO.Path]::GetFullPath(
        (Join-Path (Split-Path -Parent $projectRoot) 'tooling')).TrimEnd('\', '/')
} else {
    if (-not [IO.Path]::IsPathFullyQualified($ToolingRoot)) {
        throw 'ToolingRoot must be an explicit fully-qualified physical directory path.'
    }
    [IO.Path]::GetFullPath($ToolingRoot).TrimEnd('\', '/')
}
$dotnet = Join-Path $resolvedToolingRoot 'dotnet10\dotnet.exe'
$isolatedDesktopRunner = Join-Path $PSScriptRoot 'Invoke-IsolatedDesktopProcess.ps1'
$stagingParent = Join-Path $projectRoot 'artifacts\formal-staging'
$stagingRoot = Join-Path $stagingParent "$Version-$([guid]::NewGuid().ToString('N'))"
$stagingMarker = Join-Path $stagingRoot '.muhun-formal-staging'
$payloadRoot = Join-Path $stagingRoot 'payload'
$builtinProviderRoot = Join-Path $stagingRoot 'builtin-provider-win-x64'
$testResultsRoot = Join-Path $stagingRoot 'test-results'
$androidBuildToolsRoot = Join-Path $resolvedToolingRoot 'android-sdk\build-tools\36.0.0'
$androidApkSigner = Join-Path $androidBuildToolsRoot 'apksigner.bat'
$androidAapt2 = Join-Path $androidBuildToolsRoot 'aapt2.exe'
$androidBuildToolsVersion = '36.0.0'
$pinnedAapt2Sha256 = 'babf3122e515ddb954c5ac4669e085ce990536c035e3072de30127bddd6e3608'
$pinnedApkSignerBatSha256 = '549dd0028b0314a5112d6b56e2de7800e713f297da4508b513a735546e52ce38'
$pinnedApkSignerJarSha256 = '3716d9311e55d2b0918a2fd9d54ba9e406c5f6abeea700b287f11259bc163dec'
$mobileArtifactRoot = if ([string]::IsNullOrWhiteSpace($MobileArtifactDirectory)) {
    Join-Path $projectRoot 'artifacts\android-release-staging\mobile'
} else {
    [IO.Path]::GetFullPath($MobileArtifactDirectory)
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

function Assert-PhysicalToolingRoot {
    $volumeRoot = [IO.Path]::GetPathRoot($resolvedToolingRoot).TrimEnd('\', '/')
    if ($resolvedToolingRoot.StartsWith('\\') -or
        [string]::Equals(
            $resolvedToolingRoot,
            $volumeRoot,
            [StringComparison]::OrdinalIgnoreCase)) {
        throw 'ToolingRoot must be a dedicated local physical directory, not UNC or a volume root.'
    }
    if (-not (Test-Path -LiteralPath $resolvedToolingRoot -PathType Container)) {
        throw "ToolingRoot does not exist as a physical directory: $resolvedToolingRoot"
    }
    Assert-NoReparseAncestors -Path $resolvedToolingRoot -Label 'ToolingRoot'
    $toolingItem = Get-Item -LiteralPath $resolvedToolingRoot -Force -ErrorAction Stop
    if ($toolingItem -isnot [IO.DirectoryInfo] -or
        ($toolingItem.Attributes -band [IO.FileAttributes]::ReparsePoint) -ne 0) {
        throw 'ToolingRoot must be a physical non-reparse directory.'
    }

    $expectedDotnet = [IO.Path]::GetFullPath(
        (Join-Path $resolvedToolingRoot 'dotnet10\dotnet.exe'))
    if ([IO.Path]::GetFullPath($dotnet) -cne $expectedDotnet -or
        -not (Test-Path -LiteralPath $dotnet -PathType Leaf)) {
        throw 'Pinned formal-release dotnet10 host is missing or outside ToolingRoot.'
    }
    Assert-NoReparseAncestors -Path $dotnet -Label 'Formal-release dotnet10 host'
    $dotnetItem = Get-Item -LiteralPath $dotnet -Force -ErrorAction Stop
    if ($dotnetItem -isnot [IO.FileInfo] -or $dotnetItem.Length -lt 1 -or
        $dotnetItem.Length -gt 16MB -or
        ($dotnetItem.Attributes -band [IO.FileAttributes]::ReparsePoint) -ne 0) {
        throw 'Pinned formal-release dotnet10 host is not a safe regular file.'
    }

    $expectedAndroidRoot = [IO.Path]::GetFullPath(
        (Join-Path $resolvedToolingRoot 'android-sdk\build-tools\36.0.0'))
    if ([IO.Path]::GetFullPath($androidBuildToolsRoot) -cne $expectedAndroidRoot) {
        throw 'Android build-tools root escaped the explicit ToolingRoot.'
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
    $expected = @(
        [pscustomobject]@{
            relativePath = 'aapt2.exe'
            path = $androidAapt2
            sha256 = $pinnedAapt2Sha256
            maximumBytes = 16MB
        },
        [pscustomobject]@{
            relativePath = 'apksigner.bat'
            path = $androidApkSigner
            sha256 = $pinnedApkSignerBatSha256
            maximumBytes = 64KB
        },
        [pscustomobject]@{
            relativePath = 'lib/apksigner.jar'
            path = Join-Path $androidBuildToolsRoot 'lib\apksigner.jar'
            sha256 = $pinnedApkSignerJarSha256
            maximumBytes = 8MB
        }
    )
    if ((Split-Path -Leaf $androidBuildToolsRoot) -cne $androidBuildToolsVersion) {
        throw 'Android build-tools directory does not match the pinned version.'
    }
    foreach ($tool in $expected) {
        $tool.path = [IO.Path]::GetFullPath($tool.path)
        $expectedPath = [IO.Path]::GetFullPath(
            (Join-Path $androidBuildToolsRoot $tool.relativePath.Replace('/', '\')))
        if ($tool.path -cne $expectedPath -or
            -not (Test-Path -LiteralPath $tool.path -PathType Leaf)) {
            throw "Pinned Android tool is missing or outside build-tools ${androidBuildToolsVersion}: $($tool.relativePath)"
        }
        Assert-NoReparseAncestors -Path $tool.path -Label "Android tool $($tool.relativePath)"
        $file = Get-Item -LiteralPath $tool.path -Force
        if ($file.Length -lt 1 -or $file.Length -gt $tool.maximumBytes -or
            (Get-Sha256Hex -Path $tool.path) -cne $tool.sha256) {
            throw "Pinned Android tool failed its SHA-256 or size check: $($tool.relativePath)"
        }
    }
    return @($expected | ForEach-Object {
        [pscustomobject]@{
            relativePath = $_.relativePath
            sizeBytes = (Get-Item -LiteralPath $_.path -Force).Length
            sha256 = $_.sha256
        }
    })
}

function Assert-AndroidStagingContract {
    $apkPath = Join-Path $mobileArtifactRoot 'Muhun-MCSV-Remote.apk'
    $idsigPath = Join-Path $mobileArtifactRoot 'Muhun-MCSV-Remote.apk.idsig'
    $metadataPath = Join-Path $mobileArtifactRoot 'android-release.v3.json'
    $receiptPath = Join-Path $mobileArtifactRoot 'android-toolchain.v1.json'
    foreach ($path in @($apkPath, $idsigPath, $metadataPath, $receiptPath)) {
        if (-not (Test-Path -LiteralPath $path -PathType Leaf)) {
            throw "Android staging artifact is missing: $path"
        }
        Assert-NoReparseAncestors -Path $path -Label 'Android staging artifact'
    }

    $pinnedTools = @(Get-PinnedAndroidBuildTools)
    if ((Get-Item -LiteralPath $receiptPath -Force).Length -gt 32KB -or
        (Get-Item -LiteralPath $metadataPath -Force).Length -gt 64KB) {
        throw 'Android metadata or toolchain receipt exceeds its size limit.'
    }
    try {
        $receipt = Get-Content -LiteralPath $receiptPath -Raw | ConvertFrom-Json -Depth 8
        $metadata = Get-Content -LiteralPath $metadataPath -Raw | ConvertFrom-Json -Depth 8
    } catch {
        throw "Android metadata or toolchain receipt is invalid JSON: $($_.Exception.Message)"
    }
    Assert-ExactJsonProperties -Object $receipt `
        -Expected @('schemaVersion', 'buildToolsVersion', 'tools') `
        -Label 'Android toolchain receipt'
    if (-not ($receipt.schemaVersion -is [long]) -or
        $receipt.schemaVersion -ne 1 -or
        $receipt.buildToolsVersion -cne $androidBuildToolsVersion -or
        @($receipt.tools).Count -ne $pinnedTools.Count) {
        throw 'Android toolchain receipt schema or tool count is invalid.'
    }
    for ($index = 0; $index -lt $pinnedTools.Count; $index++) {
        $record = @($receipt.tools)[$index]
        $tool = $pinnedTools[$index]
        Assert-ExactJsonProperties -Object $record `
            -Expected @('relativePath', 'sizeBytes', 'sha256') `
            -Label "Android toolchain receipt record $index"
        if ($record.relativePath -cne $tool.relativePath -or
            -not ($record.sizeBytes -is [long]) -or
            $record.sizeBytes -ne $tool.sizeBytes -or
            $record.sha256 -cne $tool.sha256) {
            throw "Android toolchain receipt does not bind the pinned tool: $($tool.relativePath)"
        }
    }

    Assert-ExactJsonProperties -Object $metadata `
        -Expected @(
            'schemaVersion', 'productId', 'packageId', 'version', 'versionCode',
            'sizeBytes', 'sha256', 'signingCertificateSha256',
            'v4SignatureFileName', 'v4SignatureSizeBytes', 'v4SignatureSha256',
            'verifiedSignatureSchemes', 'toolchainReceiptFileName',
            'toolchainReceiptSizeBytes', 'toolchainReceiptSha256'
        ) `
        -Label 'Android release metadata'
    $receiptFile = Get-Item -LiteralPath $receiptPath -Force
    if (-not ($metadata.schemaVersion -is [long]) -or
        $metadata.schemaVersion -ne 3 -or
        $metadata.productId -cne 'muhun.mcsv.manager' -or
        $metadata.packageId -cne 'com.muhun.mcsv.remote' -or
        $metadata.version -cne $Version -or
        -not ($metadata.versionCode -is [long]) -or
        $metadata.versionCode -ne $AndroidVersionCode -or
        $metadata.versionCode -lt 1 -or
        $metadata.sizeBytes -lt 1 -or $metadata.sizeBytes -gt 512MB -or
        $metadata.sha256 -notmatch '^[a-f0-9]{64}$' -or
        $metadata.signingCertificateSha256 -notmatch '^[a-f0-9]{64}$' -or
        $metadata.v4SignatureSizeBytes -lt 1 -or
        $metadata.v4SignatureSizeBytes -gt 16MB -or
        $metadata.v4SignatureSha256 -notmatch '^[a-f0-9]{64}$' -or
        @($metadata.verifiedSignatureSchemes).Count -ne 3 -or
        $metadata.verifiedSignatureSchemes[0] -cne 'v2' -or
        $metadata.verifiedSignatureSchemes[1] -cne 'v3' -or
        $metadata.verifiedSignatureSchemes[2] -cne 'v4' -or
        $metadata.toolchainReceiptFileName -cne 'android-toolchain.v1.json' -or
        $metadata.toolchainReceiptSizeBytes -ne $receiptFile.Length -or
        $metadata.toolchainReceiptSha256 -cne (Get-Sha256Hex -Path $receiptPath) -or
        $metadata.sizeBytes -ne (Get-Item -LiteralPath $apkPath -Force).Length -or
        $metadata.sha256 -cne (Get-Sha256Hex -Path $apkPath) -or
        $metadata.v4SignatureFileName -cne 'Muhun-MCSV-Remote.apk.idsig' -or
        $metadata.v4SignatureSizeBytes -ne (Get-Item -LiteralPath $idsigPath -Force).Length -or
        $metadata.v4SignatureSha256 -cne (Get-Sha256Hex -Path $idsigPath)) {
        throw 'Android staging metadata is not hash-bound to the requested release artifacts.'
    }
}

function Invoke-Dotnet {
    param([Parameter(Mandatory = $true)][string[]]$Arguments)
    & $dotnet @Arguments
    if ($LASTEXITCODE -ne 0) {
        throw "dotnet failed with exit code ${LASTEXITCODE}: $($Arguments -join ' ')"
    }
}

function Invoke-IsolatedDotnet {
    param([Parameter(Mandatory = $true)][string[]]$Arguments)
    if (-not (Test-Path -LiteralPath $isolatedDesktopRunner -PathType Leaf)) {
        throw 'The fail-closed isolated desktop test runner is missing.'
    }
    & $isolatedDesktopRunner `
        -FilePath $dotnet `
        -WorkingDirectory $projectRoot `
        -ArgumentList $Arguments
    if ($LASTEXITCODE -ne 0) {
        throw "isolated dotnet failed with exit code ${LASTEXITCODE}: $($Arguments -join ' ')"
    }
}

function Assert-NoVulnerablePackages {
    # `dotnet list package --vulnerable` exits successfully when vulnerabilities are found.
    # Treat its versioned JSON report as data and fail closed on command, schema, coverage,
    # or advisory-shape failures instead of relying on the native process exit code alone.
    $arguments = @(
        'list', $solution, 'package',
        '--vulnerable',
        '--include-transitive',
        '--format', 'json',
        '--no-restore'
    )
    $output = @(& $dotnet @arguments 2>&1 | ForEach-Object { $_.ToString() })
    $exitCode = $LASTEXITCODE
    if ($exitCode -ne 0) {
        throw "NuGet vulnerability audit failed with exit code ${exitCode}: $($output -join [Environment]::NewLine)"
    }

    $reportText = $output -join [Environment]::NewLine
    try {
        $report = $reportText | ConvertFrom-Json -Depth 32
    } catch {
        throw "NuGet vulnerability audit did not produce valid JSON: $($_.Exception.Message)"
    }
    if ($null -eq $report) {
        throw 'NuGet vulnerability audit JSON document is empty.'
    }
    $versionProperty = $report.psobject.Properties['version']
    $parametersProperty = $report.psobject.Properties['parameters']
    $projectsProperty = $report.psobject.Properties['projects']
    if ($null -eq $versionProperty -or
        $null -eq $parametersProperty -or $null -eq $projectsProperty -or
        [int]$versionProperty.Value -ne 1 -or
        ([string]$parametersProperty.Value) -notmatch '(?:^|\s)--vulnerable(?:\s|$)' -or
        ([string]$parametersProperty.Value) -notmatch '(?:^|\s)--include-transitive(?:\s|$)' -or
        @($projectsProperty.Value).Count -lt 1) {
        throw 'NuGet vulnerability audit JSON schema, parameters, or project coverage is unsupported.'
    }

    $projectPaths = [Collections.Generic.HashSet[string]]::new(
        [StringComparer]::OrdinalIgnoreCase)
    $vulnerabilities = [Collections.Generic.List[object]]::new()
    foreach ($project in @($projectsProperty.Value)) {
        $pathProperty = $project.psobject.Properties['path']
        $frameworksProperty = $project.psobject.Properties['frameworks']
        $projectPath = if ($null -eq $pathProperty) { '' } else { [string]$pathProperty.Value }
        if ([string]::IsNullOrWhiteSpace($projectPath) -or
            -not [IO.Path]::IsPathFullyQualified($projectPath) -or
            -not $projectPaths.Add([IO.Path]::GetFullPath($projectPath))) {
            throw 'NuGet vulnerability audit contains an invalid or duplicate project path.'
        }

        if ($null -eq $frameworksProperty) {
            continue
        }
        foreach ($framework in @($frameworksProperty.Value)) {
            $frameworkProperty = $framework.psobject.Properties['framework']
            $frameworkName = if ($null -eq $frameworkProperty) { '' } else {
                [string]$frameworkProperty.Value
            }
            if ([string]::IsNullOrWhiteSpace($frameworkName)) {
                throw 'NuGet vulnerability audit contains an unsupported framework record.'
            }
            $topLevelProperty = $framework.psobject.Properties['topLevelPackages']
            $transitiveProperty = $framework.psobject.Properties['transitivePackages']
            if ($null -eq $topLevelProperty -and $null -eq $transitiveProperty) {
                throw 'NuGet vulnerability audit framework uses an unsupported package schema.'
            }
            $packageCollections = @(
                [pscustomobject]@{
                    Kind = 'top-level'
                    Items = if ($null -eq $topLevelProperty) { @() } else { @($topLevelProperty.Value) }
                },
                [pscustomobject]@{
                    Kind = 'transitive'
                    Items = if ($null -eq $transitiveProperty) { @() } else { @($transitiveProperty.Value) }
                }
            )
            foreach ($collection in $packageCollections) {
                foreach ($package in $collection.Items) {
                    $idProperty = $package.psobject.Properties['id']
                    $resolvedVersionProperty = $package.psobject.Properties['resolvedVersion']
                    $vulnerabilitiesProperty = $package.psobject.Properties['vulnerabilities']
                    $packageId = if ($null -eq $idProperty) { '' } else { [string]$idProperty.Value }
                    $resolvedVersion = if ($null -eq $resolvedVersionProperty) { '' } else {
                        [string]$resolvedVersionProperty.Value
                    }
                    $advisories = if ($null -eq $vulnerabilitiesProperty) {
                        @()
                    } else {
                        @($vulnerabilitiesProperty.Value)
                    }
                    if ([string]::IsNullOrWhiteSpace($packageId) -or
                        [string]::IsNullOrWhiteSpace($resolvedVersion) -or
                        $advisories.Count -lt 1) {
                        throw 'NuGet vulnerability audit contains an unsupported vulnerable-package record.'
                    }
                    foreach ($advisory in $advisories) {
                        $urlProperty = $advisory.psobject.Properties['advisoryUrl']
                        $severityProperty = $advisory.psobject.Properties['severity']
                        $advisoryUrl = if ($null -eq $urlProperty) { '' } else {
                            [string]$urlProperty.Value
                        }
                        $severity = if ($null -eq $severityProperty) { '' } else {
                            [string]$severityProperty.Value
                        }
                        if ([string]::IsNullOrWhiteSpace($advisoryUrl) -or
                            [string]::IsNullOrWhiteSpace($severity)) {
                            throw 'NuGet vulnerability audit contains an unsupported advisory record.'
                        }
                        $vulnerabilities.Add([pscustomobject]@{
                            Project = $projectPath
                            Framework = $frameworkName
                            Kind = $collection.Kind
                            Package = $packageId
                            Version = $resolvedVersion
                            Severity = $severity
                            AdvisoryUrl = $advisoryUrl
                        })
                    }
                }
            }
        }
    }

    $solutionText = [IO.File]::ReadAllText($solution)
    $expectedAuditProjects = @([regex]::Matches(
            $solutionText,
            '(?m)^Project\("[^"]+"\) = "[^"]+", "([^"]+\.csproj)",') |
        ForEach-Object {
            [IO.Path]::GetFullPath((Join-Path $projectRoot $_.Groups[1].Value))
        })
    if ($expectedAuditProjects.Count -lt 1) {
        throw 'NuGet vulnerability audit could not determine the solution project set.'
    }
    if ($projectPaths.Count -ne $expectedAuditProjects.Count -or
        @($expectedAuditProjects | Where-Object { -not $projectPaths.Contains($_) }).Count -ne 0) {
        throw 'NuGet vulnerability audit did not cover the exact source and test project set.'
    }

    if ($vulnerabilities.Count -ne 0) {
        $summary = @($vulnerabilities | ForEach-Object {
            "$($_.Package) $($_.Version) [$($_.Severity)] $($_.AdvisoryUrl) ($($_.Framework), $($_.Kind), $($_.Project))"
        }) -join [Environment]::NewLine
        throw "Formal release dependency audit found vulnerable packages:`n$summary"
    }
    Write-Host "NuGet vulnerability audit: projects=$($projectPaths.Count), vulnerable advisories=0"
}

function Get-PublishArguments {
    param([string]$Project, [string]$Destination)
    $versionParts = $Version.Split('-', 2)[0].Split('.')
    $numericVersion = "$($versionParts[0]).$($versionParts[1]).$($versionParts[2]).0"
    return @(
        'publish', $Project,
        '--configuration', 'Release',
        '--runtime', 'win-x64',
        '--self-contained', 'true',
        '--no-restore',
        "-m:$MaxBuildConcurrency",
        '--output', $Destination,
        '--nologo',
        '-p:PublishSingleFile=true',
        '-p:IncludeNativeLibrariesForSelfExtract=true',
        '-p:EnableCompressionInSingleFile=true',
        '-p:DebugType=None',
        '-p:DebugSymbols=false',
        '-p:ContinuousIntegrationBuild=true',
        '-p:Deterministic=true',
        '-p:IncludeSourceRevisionInInformationalVersion=false',
        '-p:TreatWarningsAsErrors=true',
        "-p:Version=$Version",
        "-p:AssemblyVersion=$numericVersion",
        "-p:FileVersion=$numericVersion",
        "-p:InformationalVersion=$Version"
    )
}

function Remove-PublishDebugArtifacts {
    param([Parameter(Mandatory = $true)][string]$Destination)

    $resolvedDestination = [IO.Path]::GetFullPath($Destination).TrimEnd('\', '/')
    $resolvedStagingPrefix = [IO.Path]::GetFullPath($stagingRoot).TrimEnd('\', '/') +
        [IO.Path]::DirectorySeparatorChar
    if (-not ($resolvedDestination + [IO.Path]::DirectorySeparatorChar).StartsWith(
            $resolvedStagingPrefix,
            [StringComparison]::OrdinalIgnoreCase) -or
        -not [IO.File]::Exists($stagingMarker) -or
        [IO.File]::ReadAllText($stagingMarker) -cne 'muhun.mcsv.formal-staging:1') {
        throw 'Refusing to remove publish debug artifacts outside the marked formal staging tree.'
    }

    $symbols = @(Get-ChildItem -LiteralPath $resolvedDestination -Recurse -Filter '*.pdb' -File -Force)
    foreach ($symbol in $symbols) {
        $cursor = $symbol
        while ($null -ne $cursor -and
            ($cursor.FullName + [IO.Path]::DirectorySeparatorChar).StartsWith(
                $resolvedDestination + [IO.Path]::DirectorySeparatorChar,
                [StringComparison]::OrdinalIgnoreCase)) {
            if (($cursor.Attributes -band [IO.FileAttributes]::ReparsePoint) -ne 0) {
                throw "Publish debug artifact traverses a reparse point: $($symbol.FullName)"
            }
            $cursor = if ($cursor -is [IO.DirectoryInfo]) { $cursor.Parent } else { $cursor.Directory }
        }
        [IO.File]::Delete($symbol.FullName)
        if ([IO.File]::Exists($symbol.FullName)) {
            throw "Publish debug artifact could not be removed: $($symbol.FullName)"
        }
    }
    if ($symbols.Count -gt 0) {
        Write-Host "Removed publish debug artifacts: $($symbols.Count)"
    }
}

function Assert-FormalSourceIdentity {
    $versionParts = $Version.Split('-', 2)[0].Split('.')
    $expectedNumericVersion = "$($versionParts[0]).$($versionParts[1]).$($versionParts[2]).0"
    $sourceProjects = @(Get-ChildItem -LiteralPath (Join-Path $projectRoot 'src') `
        -Recurse -Filter '*.csproj' -File | Sort-Object FullName)
    if ($sourceProjects.Count -lt 1) {
        throw 'No source projects were found for formal version validation.'
    }
    foreach ($sourceProject in $sourceProjects) {
        [xml]$projectDocument = Get-Content -LiteralPath $sourceProject.FullName -Raw
        $properties = @($projectDocument.Project.PropertyGroup.ChildNodes)
        $projectVersion = @($properties | Where-Object Name -eq 'Version')
        $assemblyVersion = @($properties | Where-Object Name -eq 'AssemblyVersion')
        $fileVersion = @($properties | Where-Object Name -eq 'FileVersion')
        $informationalVersion = @($properties | Where-Object Name -eq 'InformationalVersion')
        if ($projectVersion.Count -ne 1 -or $projectVersion[0].InnerText -cne $Version -or
            $assemblyVersion.Count -ne 1 -or $assemblyVersion[0].InnerText -cne $expectedNumericVersion -or
            $fileVersion.Count -ne 1 -or $fileVersion[0].InnerText -cne $expectedNumericVersion -or
            $informationalVersion.Count -ne 1 -or $informationalVersion[0].InnerText -cne $Version) {
            throw "Formal source metadata does not exactly match $Version / ${expectedNumericVersion}: $($sourceProject.FullName)"
        }
    }

    $formalAssemblyNames = [ordered]@{
        'src\MinecraftServerManager.Service\MinecraftServerManager.Service.csproj' = 'Muhun MCSV Service'
        'src\MinecraftServerManager.App\MinecraftServerManager.App.csproj' = 'Muhun MCSV Manager'
        'src\MinecraftServerManager.Updater\MinecraftServerManager.Updater.csproj' = 'Muhun MCSV Updater'
        'src\MinecraftServerManager.BuiltinProvider\MinecraftServerManager.BuiltinProvider.csproj' = 'Muhun.MCSV.BuiltinProvider'
    }
    foreach ($entry in $formalAssemblyNames.GetEnumerator()) {
        $projectPath = Join-Path $projectRoot $entry.Key
        [xml]$projectDocument = Get-Content -LiteralPath $projectPath -Raw
        $assemblyNameNodes = @($projectDocument.Project.PropertyGroup.ChildNodes |
            Where-Object Name -eq 'AssemblyName')
        if ($assemblyNameNodes.Count -ne 1 -or
            $assemblyNameNodes[0].InnerText -cne $entry.Value) {
            throw "Formal executable AssemblyName is missing or unexpected: $($entry.Key)"
        }
    }

    $staleLockFiles = @(Get-ChildItem -LiteralPath $projectRoot -Recurse -Filter 'packages.lock.json' -File |
        Where-Object { $_.FullName -notmatch '[\\/](?:bin|obj)[\\/]' } |
        Where-Object { [IO.File]::ReadAllText($_.FullName) -match '1\.0\.0-(?:alpha|preview)' })
    if ($staleLockFiles.Count -ne 0) {
        throw "Locked dependency graph still contains prerelease product versions: $($staleLockFiles.FullName -join ', ')"
    }

    $runtimeIdentityFiles = @(
        'src\MinecraftServerManager.App\MinecraftServerManager.App.csproj',
        'src\MinecraftServerManager.Core\MinecraftServerManager.Core.csproj',
        'src\MinecraftServerManager.Remote\MinecraftServerManager.Remote.csproj',
        'src\MinecraftServerManager.App\ViewModels\MainWindowViewModel.cs',
        'src\MinecraftServerManager.App\Services\OnlineModpackWorkflow.cs',
        'src\MinecraftServerManager.App\Services\CoreServerCreationWorkflow.Composition.cs',
        'src\MinecraftServerManager.App\MainWindow.xaml.cs',
        'src\MinecraftServerManager.Remote\Web\service-worker.js'
    )
    $forbiddenPatterns = @(
        'MuhunMCSVManager/0\.5\.0-preview\.9',
        'local-development-build',
        '<AssemblyName>Muhun MCSV Manager 0\.5\.0 Preview 9</AssemblyName>',
        '<Version>0\.5\.0-preview\.9</Version>',
        'mcsv-offline-preview9',
        '第一版尚未使用獨立 Windows Service'
    )
    foreach ($relative in $runtimeIdentityFiles) {
        $path = Join-Path $projectRoot $relative
        if (-not (Test-Path -LiteralPath $path -PathType Leaf)) {
            throw "Formal runtime identity input is missing: $relative"
        }
        $source = [IO.File]::ReadAllText($path)
        foreach ($pattern in $forbiddenPatterns) {
            if ($source -match $pattern) {
                throw "Formal release source still contains a frozen Preview identity: $relative"
            }
        }
    }
}

Assert-PhysicalToolingRoot
Assert-FormalSourceIdentity

if (Test-Path -LiteralPath $OutputDirectory) {
    if (-not (Test-Path -LiteralPath $OutputDirectory -PathType Container) -or
        (Get-ChildItem -LiteralPath $OutputDirectory -Force | Select-Object -First 1)) {
        throw 'OutputDirectory must be new or empty.'
    }
}
Assert-AndroidStagingContract

New-Item -ItemType Directory -Path $payloadRoot -Force | Out-Null
New-Item -ItemType Directory -Path $testResultsRoot -Force | Out-Null
[IO.File]::WriteAllText($stagingMarker, 'muhun.mcsv.formal-staging:1', [Text.UTF8Encoding]::new($false))

$buildCompleted = $false
$heavyBuildMutex = [Threading.Mutex]::new($false, 'Local\Muhun.Mcsv.HeavyBuild.v1')
$heavyBuildMutexHeld = $false
try {
    try {
        $heavyBuildMutexHeld = $heavyBuildMutex.WaitOne(0)
    } catch [Threading.AbandonedMutexException] {
        $heavyBuildMutexHeld = $true
    }
    if (-not $heavyBuildMutexHeld) {
        throw 'Another Muhun MCSV heavy build pipeline is already running in this Windows session.'
    }

    Invoke-Dotnet @('--version')
    Invoke-Dotnet @('restore', $solution, '--locked-mode', '--nologo', "-m:$MaxBuildConcurrency")
    Assert-NoVulnerablePackages
    Invoke-Dotnet @(
        'build', $solution,
        '--configuration', 'Release',
        '--no-restore',
        '--nologo',
        "-m:$MaxBuildConcurrency",
        '-p:ContinuousIntegrationBuild=true',
        '-p:Deterministic=true',
        '-p:IncludeSourceRevisionInInformationalVersion=false',
        '-p:TreatWarningsAsErrors=true'
    )

    $testProjects = @(Get-ChildItem -LiteralPath (Join-Path $projectRoot 'tests') `
        -Recurse -Filter '*.Tests.csproj' | Sort-Object FullName)
    $expectedTestProjects = @(
        'MinecraftServerManager.App.Tests.csproj',
        'MinecraftServerManager.Client.Tests.csproj',
        'MinecraftServerManager.Contracts.Tests.csproj',
        'MinecraftServerManager.Core.Tests.csproj',
        'MinecraftServerManager.Data.Tests.csproj',
        'MinecraftServerManager.GameClient.Tests.csproj',
        'MinecraftServerManager.Notifications.Tests.csproj',
        'MinecraftServerManager.ProviderHost.Tests.csproj',
        'MinecraftServerManager.Remote.Tests.csproj',
        'MinecraftServerManager.Service.Tests.csproj',
        'MinecraftServerManager.Updater.Tests.csproj'
    )
    $actualTestProjects = @($testProjects | ForEach-Object Name | Sort-Object)
    if ($actualTestProjects.Count -ne $expectedTestProjects.Count -or
        @(Compare-Object $expectedTestProjects $actualTestProjects).Count -ne 0) {
        throw "Formal release requires the exact eleven test projects: $($actualTestProjects -join ', ')"
    }
    foreach ($testProject in $testProjects) {
        $testArguments = @(
            'test', $testProject.FullName,
            '--configuration', 'Release',
            '--no-build',
            '--no-restore',
            '--nologo',
            "-m:$MaxBuildConcurrency",
            '--logger', "trx;LogFileName=$($testProject.BaseName).trx",
            '--results-directory', $testResultsRoot,
            '-p:IncludeSourceRevisionInInformationalVersion=false',
            '-p:TreatWarningsAsErrors=true'
        )
        if ($testProject.Name -ceq 'MinecraftServerManager.App.Tests.csproj') {
            Invoke-IsolatedDotnet $testArguments
        } else {
            Invoke-Dotnet $testArguments
        }
    }

    $testTotals = [ordered]@{ total = 0L; executed = 0L; passed = 0L; failed = 0L }
    $trxFiles = @(Get-ChildItem -LiteralPath $testResultsRoot -Filter '*.trx' -File)
    if ($trxFiles.Count -ne $testProjects.Count) {
        throw 'Release test result count does not match the discovered test-project count.'
    }
    foreach ($trxFile in $trxFiles) {
        [xml]$trx = Get-Content -LiteralPath $trxFile.FullName -Raw
        $counters = $trx.TestRun.ResultSummary.Counters
        if ($null -eq $counters) {
            throw "Release test result has no counters: $($trxFile.Name)"
        }
        foreach ($name in @('total', 'executed', 'passed', 'failed')) {
            $testTotals[$name] += [long]$counters.$name
        }
    }
    if ($testTotals.total -lt 1 -or $testTotals.executed -ne $testTotals.total -or
        $testTotals.passed -ne $testTotals.total -or $testTotals.failed -ne 0) {
        throw "Release test summary is not fully passing: $($testTotals | ConvertTo-Json -Compress)"
    }
    Write-Host "Formal test summary: projects=$($testProjects.Count), total=$($testTotals.total), passed=$($testTotals.passed), failed=$($testTotals.failed)"

    $publishProjects = @(
        [pscustomobject]@{
            Project = Join-Path $projectRoot 'src\MinecraftServerManager.Service\MinecraftServerManager.Service.csproj'
            Destination = Join-Path $payloadRoot 'service-win-x64'
        },
        [pscustomobject]@{
            Project = Join-Path $projectRoot 'src\MinecraftServerManager.App\MinecraftServerManager.App.csproj'
            Destination = Join-Path $payloadRoot 'gui-win-x64'
        },
        [pscustomobject]@{
            Project = Join-Path $projectRoot 'src\MinecraftServerManager.Updater\MinecraftServerManager.Updater.csproj'
            Destination = Join-Path $payloadRoot 'updater-win-x64'
        }
    )
    $providerProject = Join-Path $projectRoot `
        'src\MinecraftServerManager.BuiltinProvider\MinecraftServerManager.BuiltinProvider.csproj'
    # RID-specific lock data is intentionally scoped to publishable source projects. Including
    # test projects in a solution-wide RID restore is invalid because their lock files are
    # framework-only. These restores are incremental and never rebuild the solution.
    foreach ($publishRestoreProject in @($publishProjects.Project) + @($providerProject)) {
        Invoke-Dotnet @(
            'restore', $publishRestoreProject,
            '--runtime', 'win-x64',
            '--locked-mode',
            '--nologo',
            "-m:$MaxBuildConcurrency")
    }
    foreach ($publish in $publishProjects) {
        Invoke-Dotnet (Get-PublishArguments `
            -Project $publish.Project `
            -Destination $publish.Destination)
        Remove-PublishDebugArtifacts -Destination $publish.Destination
    }

    foreach ($releaseDocument in @(
            [pscustomobject]@{ Source = 'THIRD-PARTY-NOTICES.txt'; Destination = 'THIRD-PARTY-NOTICES.txt' },
            [pscustomobject]@{ Source = 'LICENSE'; Destination = 'LICENSE.txt' }
        )) {
        $sourcePath = Join-Path $projectRoot $releaseDocument.Source
        if (-not [IO.File]::Exists($sourcePath)) {
            throw "Required release document is missing: $($releaseDocument.Source)"
        }
        [IO.File]::Copy(
            $sourcePath,
            (Join-Path $payloadRoot $releaseDocument.Destination),
            $false)
    }

    Invoke-Dotnet (Get-PublishArguments `
        -Project $providerProject `
        -Destination $builtinProviderRoot)
    Remove-PublishDebugArtifacts -Destination $builtinProviderRoot

    & (Join-Path $PSScriptRoot 'New-MuhunMcsvRelease.ps1') `
        -PayloadDirectory $payloadRoot `
        -BuiltinProviderDirectory $builtinProviderRoot `
        -MobileArtifactDirectory $mobileArtifactRoot `
        -AndroidApkSignerPath $androidApkSigner `
        -AndroidAapt2Path $androidAapt2 `
        -AndroidVersionCode $AndroidVersionCode `
        -OutputDirectory $OutputDirectory `
        -Version $Version `
        -PackageBaseUri $PackageBaseUri `
        -SigningIdentityDirectory $SigningIdentityDirectory `
        -Channel $Channel `
        -PublisherTrustMode $PublisherTrustMode `
        -TimestampServerUrl $TimestampServerUrl
    $buildCompleted = $true
} finally {
    if ($buildCompleted -and -not $KeepStaging -and
        [IO.File]::Exists($stagingMarker) -and
        [IO.File]::ReadAllText($stagingMarker) -eq 'muhun.mcsv.formal-staging:1') {
        $resolvedStaging = [IO.Path]::GetFullPath($stagingRoot)
        $resolvedParent = [IO.Path]::GetFullPath($stagingParent).TrimEnd('\', '/') + `
            [IO.Path]::DirectorySeparatorChar
        if (-not $resolvedStaging.StartsWith($resolvedParent, [StringComparison]::OrdinalIgnoreCase)) {
            throw 'Refusing to clean a staging directory outside artifacts/formal-staging.'
        }
        Remove-Item -LiteralPath $resolvedStaging -Recurse -Force
    }
    if ($heavyBuildMutexHeld) {
        $heavyBuildMutex.ReleaseMutex()
    }
    $heavyBuildMutex.Dispose()
}

Write-Host "Formal release completed: $([IO.Path]::GetFullPath($OutputDirectory))"
