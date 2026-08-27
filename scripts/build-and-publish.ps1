param(
    [string]$Configuration = 'Release'
)

$ErrorActionPreference = 'Stop'
$projectRoot = Split-Path -Parent $PSScriptRoot
$bundledDotnet = Join-Path (Split-Path -Parent $projectRoot) 'tooling\dotnet10\dotnet.exe'
$dotnet = if (Test-Path -LiteralPath $bundledDotnet) {
    $bundledDotnet
} else {
    (Get-Command dotnet -ErrorAction Stop).Source
}

& $dotnet --version
& $dotnet restore (Join-Path $projectRoot 'MinecraftServerManager.sln')
& $dotnet test (Join-Path $projectRoot 'MinecraftServerManager.sln') --configuration $Configuration --no-restore
& $dotnet publish (Join-Path $projectRoot 'src\MinecraftServerManager.App\MinecraftServerManager.App.csproj') `
    --configuration $Configuration `
    --runtime win-x64 `
    --self-contained true `
    --output (Join-Path $projectRoot 'artifacts\publish\win-x64') `
    -p:PublishSingleFile=true `
    -p:IncludeNativeLibrariesForSelfExtract=true `
    -p:EnableCompressionInSingleFile=true `
    -p:DebugType=None `
    -p:DebugSymbols=false

Write-Host "Published to: $(Join-Path $projectRoot 'artifacts\publish\win-x64')"
