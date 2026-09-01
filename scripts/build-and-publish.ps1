param(
    [string]$Configuration = 'Release',
    [ValidateRange(1, 64)]
    [int]$MaxBuildConcurrency = 4
)

$ErrorActionPreference = 'Stop'
$projectRoot = Split-Path -Parent $PSScriptRoot
$bundledDotnet = Join-Path (Split-Path -Parent $projectRoot) 'tooling\dotnet10\dotnet.exe'
$dotnet = if (Test-Path -LiteralPath $bundledDotnet) {
    $bundledDotnet
} else {
    (Get-Command dotnet -ErrorAction Stop).Source
}

if ($IsWindows) {
    try {
        [Diagnostics.Process]::GetCurrentProcess().PriorityClass =
            [Diagnostics.ProcessPriorityClass]::BelowNormal
    } catch {
        Write-Warning "Unable to lower the build process priority: $($_.Exception.Message)"
    }
}

$solution = Join-Path $projectRoot 'MinecraftServerManager.sln'
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

    & $dotnet --version
    if ($LASTEXITCODE -ne 0) {
        throw "dotnet version probe failed with exit code $LASTEXITCODE."
    }
    & $dotnet restore $solution "-m:$MaxBuildConcurrency"
    if ($LASTEXITCODE -ne 0) {
        throw "Solution restore failed with exit code $LASTEXITCODE."
    }
    & $dotnet build $solution --configuration $Configuration --no-restore "-m:$MaxBuildConcurrency"
    if ($LASTEXITCODE -ne 0) {
        throw "Solution build failed with exit code $LASTEXITCODE."
    }
    $testProjects = @(Get-ChildItem -LiteralPath (Join-Path $projectRoot 'tests') `
        -Recurse -Filter '*.Tests.csproj' -File | Sort-Object FullName)
    foreach ($testProject in $testProjects) {
        $testArguments = @(
            'test', $testProject.FullName,
            '--configuration', $Configuration,
            '--no-build',
            '--no-restore',
            "-m:$MaxBuildConcurrency"
        )
        if ($testProject.Name -ceq 'MinecraftServerManager.App.Tests.csproj') {
            & (Join-Path $PSScriptRoot 'Invoke-IsolatedDesktopProcess.ps1') `
                -FilePath $dotnet `
                -WorkingDirectory $projectRoot `
                -ArgumentList $testArguments
        } else {
            & $dotnet @testArguments
        }
        if ($LASTEXITCODE -ne 0) {
            throw "Test project failed with exit code ${LASTEXITCODE}: $($testProject.Name)"
        }
    }

    # Scope RID restore to the only publish target. Test projects intentionally retain their
    # framework-only lock files and must not be forced into a solution-wide runtime restore.
    $appProject = Join-Path $projectRoot 'src\MinecraftServerManager.App\MinecraftServerManager.App.csproj'
    & $dotnet restore $appProject --runtime win-x64 "-m:$MaxBuildConcurrency"
    if ($LASTEXITCODE -ne 0) {
        throw "win-x64 solution restore failed with exit code $LASTEXITCODE."
    }
    & $dotnet publish $appProject `
        --configuration $Configuration `
        --runtime win-x64 `
        --self-contained true `
        --no-restore `
        --output (Join-Path $projectRoot 'artifacts\publish\win-x64') `
        "-m:$MaxBuildConcurrency" `
        -p:PublishSingleFile=true `
        -p:IncludeNativeLibrariesForSelfExtract=true `
        -p:EnableCompressionInSingleFile=true `
        -p:DebugType=None `
        -p:DebugSymbols=false
    if ($LASTEXITCODE -ne 0) {
        throw "Application publish failed with exit code $LASTEXITCODE."
    }
} finally {
    if ($heavyBuildMutexHeld) {
        $heavyBuildMutex.ReleaseMutex()
    }
    $heavyBuildMutex.Dispose()
}

Write-Host "Published to: $(Join-Path $projectRoot 'artifacts\publish\win-x64')"
