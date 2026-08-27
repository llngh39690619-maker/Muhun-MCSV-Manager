#requires -Version 7.4

[CmdletBinding(SupportsShouldProcess = $true, ConfirmImpact = 'High')]
param(
    [string]$InstallRoot = "$env:ProgramFiles\Muhun\MCSV",

    [string]$DataRoot = "$env:ProgramData\Muhun\MCSV",

    [switch]$RemoveData
)

$ErrorActionPreference = 'Stop'
Set-StrictMode -Version Latest
$serviceName = 'MuhunMCSV'
$expectedMarker = 'muhun.mcsv.manager:1'
$startMenuShortcutName = 'Muhun MCSV Manager.lnk'
$startupShortcutName = 'Muhun MCSV GUI Activation Broker.lnk'

function Resolve-GuardedRoot {
    param([string]$Path, [string]$Marker)

    if (-not [IO.Path]::IsPathFullyQualified($Path)) {
        throw '解除安裝目錄必須是完整路徑。'
    }

    $fullPath = [IO.Path]::GetFullPath($Path).TrimEnd('\', '/')
    $root = [IO.Path]::GetPathRoot($fullPath).TrimEnd('\', '/')
    if ($fullPath.StartsWith('\\') -or [string]::Equals($fullPath, $root, [StringComparison]::OrdinalIgnoreCase)) {
        throw '拒絕對 UNC 或磁碟根目錄執行解除安裝。'
    }

    if (-not (Test-Path -LiteralPath $fullPath -PathType Container)) {
        throw "找不到受管理的解除安裝目錄：$fullPath"
    }
    for ($cursor = Get-Item -LiteralPath $fullPath -Force;
         $null -ne $cursor;
         $cursor = $cursor.Parent) {
        if (($cursor.Attributes -band [IO.FileAttributes]::ReparsePoint) -ne 0) {
            throw "解除安裝目錄不可經過 reparse point：$($cursor.FullName)"
        }
    }

    $markerPath = Join-Path $fullPath $Marker
    if (-not (Test-Path -LiteralPath $markerPath -PathType Leaf) -or
        ((Get-Item -LiteralPath $markerPath -Force).Attributes -band
            [IO.FileAttributes]::ReparsePoint) -ne 0 -or
        (Get-Item -LiteralPath $markerPath -Force).Length -gt 64 -or
        (Get-Content -LiteralPath $markerPath -Raw).Trim() -ne $expectedMarker) {
        throw "目錄缺少有效的 Muhun MCSV 安裝標記，拒絕移除：$fullPath"
    }

    return $fullPath
}

function Test-IsUnderRoot {
    param([string]$Candidate, [string]$Root)
    $normalizedRoot = [IO.Path]::GetFullPath($Root).TrimEnd('\', '/') +
        [IO.Path]::DirectorySeparatorChar
    $normalizedCandidate = [IO.Path]::GetFullPath($Candidate).TrimEnd('\', '/') +
        [IO.Path]::DirectorySeparatorChar
    return $normalizedCandidate.StartsWith($normalizedRoot, [StringComparison]::OrdinalIgnoreCase)
}

$identity = [Security.Principal.WindowsIdentity]::GetCurrent()
$principal = [Security.Principal.WindowsPrincipal]::new($identity)
if (-not $principal.IsInRole([Security.Principal.WindowsBuiltInRole]::Administrator)) {
    throw '解除安裝 Windows Service 需要以系統管理員身分執行。'
}

$install = Resolve-GuardedRoot $InstallRoot '.muhun-mcsv-install-root'
$data = if ($RemoveData) { Resolve-GuardedRoot $DataRoot '.muhun-mcsv-data-root' } else { $null }
if ($RemoveData -and ((Test-IsUnderRoot $install $data) -or (Test-IsUnderRoot $data $install))) {
    throw '程式目錄與資料目錄不可相同或互相包含。'
}
if ($PSCmdlet.ShouldProcess($install, '解除安裝 Muhun MCSV Manager 與 Windows Service')) {
    $service = Get-Service -Name $serviceName -ErrorAction SilentlyContinue
    if ($null -ne $service) {
        if ($service.Status -ne 'Stopped') {
            Stop-Service -Name $serviceName -Force
            $service.WaitForStatus('Stopped', [TimeSpan]::FromSeconds(30))
        }

        $output = & "$env:SystemRoot\System32\sc.exe" delete $serviceName 2>&1
        if ($LASTEXITCODE -ne 0) {
            throw "Windows Service 移除失敗：$($output -join ' ')"
        }
    }

    $stableLauncherPath = Join-Path $install 'launcher\Muhun MCSV Updater.exe'
    $managedVersionsPrefix = (Join-Path $install 'versions').TrimEnd('\', '/') +
        [IO.Path]::DirectorySeparatorChar
    foreach ($process in Get-Process -Name 'Muhun MCSV Updater' -ErrorAction SilentlyContinue) {
        try {
            $processPath = [IO.Path]::GetFullPath($process.Path)
            if ([string]::Equals(
                    $processPath,
                    [IO.Path]::GetFullPath($stableLauncherPath),
                    [StringComparison]::OrdinalIgnoreCase) -or
                ($processPath.StartsWith(
                        $managedVersionsPrefix,
                        [StringComparison]::OrdinalIgnoreCase) -and
                    [IO.Path]::GetFileName($processPath) -eq 'Muhun MCSV Updater.exe')) {
                Stop-Process -Id $process.Id -Force -ErrorAction Stop
            }
        } catch [System.ComponentModel.Win32Exception] {
            # A process whose path cannot be inspected is not an authorized uninstall target.
        } finally {
            $process.Dispose()
        }
    }
    foreach ($process in Get-Process -Name 'Muhun MCSV Manager' -ErrorAction SilentlyContinue) {
        try {
            $processPath = [IO.Path]::GetFullPath($process.Path)
            if (-not $processPath.StartsWith(
                    $managedVersionsPrefix,
                    [StringComparison]::OrdinalIgnoreCase) -or
                [IO.Path]::GetFileName($processPath) -ne 'Muhun MCSV Manager.exe') {
                continue
            }

            if (-not $process.HasExited -and $process.CloseMainWindow()) {
                [void]$process.WaitForExit(20000)
            }
            if (-not $process.HasExited) {
                # Explicit uninstall owns only this exact managed GUI. If a close prompt or a
                # failed initialization retained the image lock, terminate it before deleting
                # the signed version tree.
                Stop-Process -Id $process.Id -Force -ErrorAction Stop
            }
        } catch [System.ComponentModel.Win32Exception] {
            # A process whose path cannot be inspected is not an authorized uninstall target.
        } finally {
            $process.Dispose()
        }
    }
    foreach ($shortcut in @(
        (Join-Path ([Environment]::GetFolderPath([Environment+SpecialFolder]::Programs)) `
            $startMenuShortcutName),
        (Join-Path ([Environment]::GetFolderPath([Environment+SpecialFolder]::Startup)) `
            $startupShortcutName))) {
        if (Test-Path -LiteralPath $shortcut -PathType Leaf) {
            Remove-Item -LiteralPath $shortcut -Force
        }
    }

    [void](Resolve-GuardedRoot $install '.muhun-mcsv-install-root')
    Remove-Item -LiteralPath $install -Recurse -Force
    if ($RemoveData -and $null -ne $data) {
        [void](Resolve-GuardedRoot $data '.muhun-mcsv-data-root')
        Remove-Item -LiteralPath $data -Recurse -Force
        Write-Host '程式與使用者資料已移除；資料無法由解除安裝程式復原。'
    }
    else {
        Write-Host "程式已移除；伺服器、備份與帳號資料保留於 $DataRoot。"
    }
}
