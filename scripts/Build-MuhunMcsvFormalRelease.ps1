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

    [switch]$KeepStaging
)

$ErrorActionPreference = 'Stop'
Set-StrictMode -Version Latest

if (-not $IsWindows) {
    throw 'Formal win-x64 releases must be built on Windows.'
}
if ($Version -match '(?i)(?:^|[.-])(preview|alpha)(?:[.-]|$)' -or
    ($Channel -eq 'stable' -and $Version.Contains('-'))) {
    throw 'Formal releases cannot use preview/alpha versions, and the stable channel requires a final semantic version.'
}

$projectRoot = [IO.Path]::GetFullPath((Split-Path -Parent $PSScriptRoot)).TrimEnd('\', '/')
$solution = Join-Path $projectRoot 'MinecraftServerManager.sln'
$bundledDotnet = Join-Path (Split-Path -Parent $projectRoot) 'tooling\dotnet10\dotnet.exe'
$dotnet = if (Test-Path -LiteralPath $bundledDotnet -PathType Leaf) {
    $bundledDotnet
} else {
    (Get-Command dotnet -ErrorAction Stop).Source
}
$stagingParent = Join-Path $projectRoot 'artifacts\formal-staging'
$stagingRoot = Join-Path $stagingParent "$Version-$([guid]::NewGuid().ToString('N'))"
$stagingMarker = Join-Path $stagingRoot '.muhun-formal-staging'
$payloadRoot = Join-Path $stagingRoot 'payload'
$builtinProviderRoot = Join-Path $stagingRoot 'builtin-provider-win-x64'
$testResultsRoot = Join-Path $stagingRoot 'test-results'
$androidBuildToolsRoot = Join-Path (Split-Path -Parent $projectRoot) `
    'tooling\android-sdk\build-tools\36.0.0'
$androidApkSigner = Join-Path $androidBuildToolsRoot 'apksigner.bat'
$androidAapt2 = Join-Path $androidBuildToolsRoot 'aapt2.exe'
$mobileArtifactRoot = if ([string]::IsNullOrWhiteSpace($MobileArtifactDirectory)) {
    Join-Path $projectRoot 'artifacts\android-release-staging\mobile'
} else {
    [IO.Path]::GetFullPath($MobileArtifactDirectory)
}

function Invoke-Dotnet {
    param([Parameter(Mandatory = $true)][string[]]$Arguments)
    & $dotnet @Arguments
    if ($LASTEXITCODE -ne 0) {
        throw "dotnet failed with exit code ${LASTEXITCODE}: $($Arguments -join ' ')"
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
        '--output', $Destination,
        '--nologo',
        '-p:PublishSingleFile=true',
        '-p:IncludeNativeLibrariesForSelfExtract=true',
        '-p:EnableCompressionInSingleFile=true',
        '-p:DebugType=None',
        '-p:DebugSymbols=false',
        '-p:ContinuousIntegrationBuild=true',
        '-p:Deterministic=true',
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

Assert-FormalSourceIdentity

if (Test-Path -LiteralPath $OutputDirectory) {
    if (-not (Test-Path -LiteralPath $OutputDirectory -PathType Container) -or
        (Get-ChildItem -LiteralPath $OutputDirectory -Force | Select-Object -First 1)) {
        throw 'OutputDirectory must be new or empty.'
    }
}
foreach ($androidVerifier in @($androidApkSigner, $androidAapt2)) {
    if (-not (Test-Path -LiteralPath $androidVerifier -PathType Leaf) -or
        ((Get-Item -LiteralPath $androidVerifier -Force).Attributes -band
            [IO.FileAttributes]::ReparsePoint) -ne 0) {
        throw "Pinned Android release verifier is missing or unsafe: $androidVerifier"
    }
}

New-Item -ItemType Directory -Path $payloadRoot -Force | Out-Null
New-Item -ItemType Directory -Path $testResultsRoot -Force | Out-Null
[IO.File]::WriteAllText($stagingMarker, 'muhun.mcsv.formal-staging:1', [Text.UTF8Encoding]::new($false))

$buildCompleted = $false
try {
    Invoke-Dotnet @('--version')
    Invoke-Dotnet @('restore', $solution, '--locked-mode', '--nologo')
    Assert-NoVulnerablePackages
    Invoke-Dotnet @(
        'build', $solution,
        '--configuration', 'Release',
        '--no-restore',
        '--nologo',
        '-p:ContinuousIntegrationBuild=true',
        '-p:Deterministic=true',
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
        'MinecraftServerManager.Notifications.Tests.csproj',
        'MinecraftServerManager.ProviderHost.Tests.csproj',
        'MinecraftServerManager.Remote.Tests.csproj',
        'MinecraftServerManager.Service.Tests.csproj',
        'MinecraftServerManager.Updater.Tests.csproj'
    )
    $actualTestProjects = @($testProjects | ForEach-Object Name | Sort-Object)
    if ($actualTestProjects.Count -ne $expectedTestProjects.Count -or
        @(Compare-Object $expectedTestProjects $actualTestProjects).Count -ne 0) {
        throw "Formal release requires the exact ten test projects: $($actualTestProjects -join ', ')"
    }
    foreach ($testProject in $testProjects) {
        Invoke-Dotnet @('restore', $testProject.FullName, '--locked-mode', '--nologo')
        Invoke-Dotnet @(
            'test', $testProject.FullName,
            '--configuration', 'Release',
            '--no-restore',
            '--nologo',
            '--logger', "trx;LogFileName=$($testProject.BaseName).trx",
            '--results-directory', $testResultsRoot,
            '-p:TreatWarningsAsErrors=true'
        )
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
    foreach ($publish in $publishProjects) {
        Invoke-Dotnet @('restore', $publish.Project, '--runtime', 'win-x64', '--locked-mode', '--nologo')
        Invoke-Dotnet (Get-PublishArguments `
            -Project $publish.Project `
            -Destination $publish.Destination)
        Remove-PublishDebugArtifacts -Destination $publish.Destination
    }

    $providerProject = Join-Path $projectRoot `
        'src\MinecraftServerManager.BuiltinProvider\MinecraftServerManager.BuiltinProvider.csproj'
    Invoke-Dotnet @('restore', $providerProject, '--runtime', 'win-x64', '--locked-mode', '--nologo')
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
}

Write-Host "Formal release completed: $([IO.Path]::GetFullPath($OutputDirectory))"
