param(
    [ValidateSet('Release')]
    [string]$Configuration = 'Release',

    [string]$OutputRoot = ''
)

# Historical Alpha-1 foundation publisher retained only for provenance and old incident
# reproduction. It is not a formal 1.0 release path. Use Build-MuhunMcsvFormalRelease.ps1 for
# every installable candidate; that pipeline enforces locked restore, all test gates, signing,
# manifest verification, packaging, and rollback validation.

$ErrorActionPreference = 'Stop'
$projectRoot = Split-Path -Parent $PSScriptRoot
$bundledDotnet = Join-Path (Split-Path -Parent $projectRoot) 'tooling\dotnet10\dotnet.exe'
$dotnet = if (Test-Path -LiteralPath $bundledDotnet) {
    $bundledDotnet
} else {
    (Get-Command dotnet -ErrorAction Stop).Source
}
$solution = Join-Path $projectRoot 'MinecraftServerManager.sln'
$serviceProject = Join-Path $projectRoot 'src\MinecraftServerManager.Service\MinecraftServerManager.Service.csproj'
$appProject = Join-Path $projectRoot 'src\MinecraftServerManager.App\MinecraftServerManager.App.csproj'
$workspaceRoot = Split-Path -Parent (Split-Path -Parent $projectRoot)
$projectArtifactRoot = [System.IO.Path]::GetFullPath((Join-Path $projectRoot 'artifacts'))
$validationArtifactRoot = [System.IO.Path]::GetFullPath((Join-Path $workspaceRoot 'work-test-results'))
$runName = "product-foundation-$((Get-Date).ToUniversalTime().ToString('yyyyMMdd-HHmmss'))-$PID"
$publishRoot = if ([string]::IsNullOrWhiteSpace($OutputRoot)) {
    Join-Path $projectArtifactRoot $runName
} else {
    [System.IO.Path]::GetFullPath($OutputRoot)
}
$servicePublishRoot = Join-Path $publishRoot 'service-win-x64'
$appPublishRoot = Join-Path $publishRoot 'gui-win-x64'
$testResultsRoot = Join-Path $publishRoot 'test-results'

function Test-IsUnderRoot {
    param([string]$Candidate, [string]$Root)

    $normalizedRoot = [System.IO.Path]::GetFullPath($Root).TrimEnd('\', '/') + [System.IO.Path]::DirectorySeparatorChar
    $normalizedCandidate = [System.IO.Path]::GetFullPath($Candidate).TrimEnd('\', '/') + [System.IO.Path]::DirectorySeparatorChar
    return $normalizedCandidate.StartsWith($normalizedRoot, [System.StringComparison]::OrdinalIgnoreCase)
}

if (-not (Test-IsUnderRoot $publishRoot $projectArtifactRoot) -and
    -not (Test-IsUnderRoot $publishRoot $validationArtifactRoot)) {
    throw "OutputRoot must be contained by '$projectArtifactRoot' or '$validationArtifactRoot'."
}

if (Test-Path -LiteralPath $publishRoot) {
    if (Get-ChildItem -LiteralPath $publishRoot -Force | Select-Object -First 1) {
        throw "OutputRoot must be new or empty so stale files cannot enter the artifact: $publishRoot"
    }
} else {
    New-Item -ItemType Directory -Path $publishRoot | Out-Null
}
New-Item -ItemType Directory -Path $testResultsRoot | Out-Null

function Invoke-Dotnet {
    param([Parameter(Mandatory = $true, Position = 0)][string[]]$DotnetArguments)

    & $dotnet @DotnetArguments
    if ($LASTEXITCODE -ne 0) {
        throw "dotnet command failed with exit code ${LASTEXITCODE}: $($DotnetArguments -join ' ')"
    }
}

Invoke-Dotnet @('--version')
Invoke-Dotnet @('restore', $solution, '--locked-mode', '--nologo')
Invoke-Dotnet @(
    'build', $solution,
    '--configuration', $Configuration,
    '--no-restore',
    '--nologo',
    '-p:ContinuousIntegrationBuild=true',
    '-p:Deterministic=true',
    '-p:TreatWarningsAsErrors=true'
)

# Publish the compatibility GUI slice into its own isolated folder. It is included so the new
# catalogue UI can be exercised without copying anything into the live Preview 9 directory; it
# still uses the Preview backend and is not the future Service client.
Invoke-Dotnet @('restore', $appProject, '--runtime', 'win-x64', '--locked-mode', '--nologo')
Invoke-Dotnet @(
    'publish', $appProject,
    '--configuration', $Configuration,
    '--runtime', 'win-x64',
    '--self-contained', 'true',
    '--no-restore',
    '--output', $appPublishRoot,
    '-p:PublishSingleFile=true',
    '-p:IncludeNativeLibrariesForSelfExtract=true',
    '-p:EnableCompressionInSingleFile=true',
    '-p:DebugType=None',
    '-p:DebugSymbols=false',
    '-p:ContinuousIntegrationBuild=true',
    '-p:Deterministic=true',
    '-p:TreatWarningsAsErrors=true'
)

$testProjects = [ordered]@{
    Core = 'tests\MinecraftServerManager.Core.Tests\MinecraftServerManager.Core.Tests.csproj'
    Remote = 'tests\MinecraftServerManager.Remote.Tests\MinecraftServerManager.Remote.Tests.csproj'
    App = 'tests\MinecraftServerManager.App.Tests\MinecraftServerManager.App.Tests.csproj'
    Contracts = 'tests\MinecraftServerManager.Contracts.Tests\MinecraftServerManager.Contracts.Tests.csproj'
    Service = 'tests\MinecraftServerManager.Service.Tests\MinecraftServerManager.Service.Tests.csproj'
    Data = 'tests\MinecraftServerManager.Data.Tests\MinecraftServerManager.Data.Tests.csproj'
    Notifications = 'tests\MinecraftServerManager.Notifications.Tests\MinecraftServerManager.Notifications.Tests.csproj'
    Updater = 'tests\MinecraftServerManager.Updater.Tests\MinecraftServerManager.Updater.Tests.csproj'
    Client = 'tests\MinecraftServerManager.Client.Tests\MinecraftServerManager.Client.Tests.csproj'
    ProviderHost = 'tests\MinecraftServerManager.ProviderHost.Tests\MinecraftServerManager.ProviderHost.Tests.csproj'
}
$discoveredTests = Get-ChildItem -LiteralPath (Join-Path $projectRoot 'tests') -Recurse -Filter '*.Tests.csproj' |
    ForEach-Object { [System.IO.Path]::GetRelativePath($projectRoot, $_.FullName) } |
    Sort-Object
$configuredTests = $testProjects.Values | Sort-Object
if (Compare-Object $discoveredTests $configuredTests) {
    throw 'The release test project list does not match the test projects discovered on disk.'
}

foreach ($testProject in $testProjects.GetEnumerator()) {
    Invoke-Dotnet @(
        'test', (Join-Path $projectRoot $testProject.Value),
        '--configuration', $Configuration,
        '--no-build',
        '--no-restore',
        '--nologo',
        '--logger', "trx;LogFileName=$($testProject.Key).trx",
        '--results-directory', $testResultsRoot
    )
}

# A self-contained RID publish needs RID-specific assets. Restore it explicitly so
# the following publish remains deterministic and cannot silently perform a restore.
Invoke-Dotnet @('restore', $serviceProject, '--runtime', 'win-x64', '--locked-mode', '--nologo')

Invoke-Dotnet @(
    'publish', $serviceProject,
    '--configuration', $Configuration,
    '--runtime', 'win-x64',
    '--self-contained', 'true',
    '--no-restore',
    '--output', $servicePublishRoot,
    '-p:PublishSingleFile=true',
    '-p:IncludeNativeLibrariesForSelfExtract=true',
    '-p:EnableCompressionInSingleFile=true',
    '-p:DebugType=None',
    '-p:DebugSymbols=false',
    '-p:ContinuousIntegrationBuild=true',
    '-p:Deterministic=true',
    '-p:TreatWarningsAsErrors=true'
)

Copy-Item -LiteralPath (Join-Path $projectRoot 'docs\ALPHA1-FOUNDATION-DO-NOT-INSTALL.txt') `
    -Destination (Join-Path $publishRoot 'DO-NOT-INSTALL.txt')

$serviceExecutable = Join-Path $servicePublishRoot 'Muhun MCSV Service.exe'
if (-not (Test-Path -LiteralPath $serviceExecutable -PathType Leaf)) {
    throw 'The expected single-file Windows Service executable was not published.'
}
$appExecutable = Join-Path $appPublishRoot 'Muhun MCSV Manager 0.5.0 Preview 9.exe'
if (-not (Test-Path -LiteralPath $appExecutable -PathType Leaf)) {
    throw 'The expected single-file compatibility GUI executable was not published.'
}

$signature = Get-AuthenticodeSignature -LiteralPath $serviceExecutable
$artifactFiles = Get-ChildItem -LiteralPath $publishRoot -Recurse -File |
    Where-Object { $_.Name -ne 'SHA256SUMS.txt' -and $_.Name -ne 'foundation-manifest.json' } |
    Sort-Object FullName
$checksumLines = foreach ($file in $artifactFiles) {
    $relative = [System.IO.Path]::GetRelativePath($publishRoot, $file.FullName).Replace('\', '/')
    $hash = (Get-FileHash -LiteralPath $file.FullName -Algorithm SHA256).Hash.ToLowerInvariant()
    "$hash *$relative"
}
[System.IO.File]::WriteAllLines(
    (Join-Path $publishRoot 'SHA256SUMS.txt'),
    $checksumLines,
    [System.Text.UTF8Encoding]::new($false))

$manifest = [ordered]@{
    artifactKind = 'alpha-1-foundation-with-compatibility-gui-slice'
    installable = $false
    configuration = $Configuration
    productVersion = '1.0.0-alpha.1'
    sdkVersion = (& $dotnet --version).Trim()
    runtimeIdentifier = 'win-x64'
    generatedAtUtc = (Get-Date).ToUniversalTime().ToString('O')
    authenticodeStatus = $signature.Status.ToString()
    testResultFiles = @($testProjects.Keys | ForEach-Object { "test-results/$_.trx" })
    checksumFile = 'SHA256SUMS.txt'
    limitations = @(
        'Not an installer or production release.',
        'Does not migrate or control Preview 9 servers.',
        'The gui-win-x64 compatibility build still uses the Preview backend and is not Service-backed.',
        'Windows Service ACL provisioning and code signing are not implemented.'
    )
}
$manifestJson = $manifest | ConvertTo-Json -Depth 6
[System.IO.File]::WriteAllText(
    (Join-Path $publishRoot 'foundation-manifest.json'),
    $manifestJson + [Environment]::NewLine,
    [System.Text.UTF8Encoding]::new($false))

Write-Host "Product foundation published to: $publishRoot"
