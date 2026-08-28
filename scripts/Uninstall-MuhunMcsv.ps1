#requires -Version 7.4

[CmdletBinding(SupportsShouldProcess = $true, ConfirmImpact = 'High')]
param(
    [string]$InstallRoot = "$env:ProgramFiles\Muhun\MCSV",

    [string]$DataRoot = "$env:ProgramData\Muhun\MCSV",

    [switch]$RemoveData,

    [switch]$RemoveUserClientData,

    [string]$UserClientDataRoot,

    [string]$UserClientDataOwnerSid
)

$ErrorActionPreference = 'Stop'
Set-StrictMode -Version Latest
$serviceName = 'MuhunMCSV'
$expectedMarker = 'muhun.mcsv.manager:1'
$productId = 'muhun.mcsv.manager'
$expectedPublisherCertificateSha256 = '1a67e65dc9c367ac3247d0483edbe94dab38c5494859a43210c1ad4719e80b71'
$startMenuShortcutName = 'X MCSV.lnk'
$legacyStartMenuShortcutName = 'Muhun MCSV Manager.lnk'
$startupShortcutName = 'Muhun MCSV GUI Activation Broker.lnk'
$arpRegistrySubKey = 'SOFTWARE\Microsoft\Windows\CurrentVersion\Uninstall\MuhunMCSV'

function Get-CertificateSha256 {
    param([Parameter(Mandatory = $true)]$Certificate)
    return [Convert]::ToHexString(
        [Security.Cryptography.SHA256]::HashData($Certificate.RawData)).ToLowerInvariant()
}

function Assert-TrustedUninstallerSelf {
    $signature = Get-AuthenticodeSignature -LiteralPath $PSCommandPath
    if ($signature.Status -ne [Management.Automation.SignatureStatus]::Valid -or
        $null -eq $signature.SignerCertificate -or
        $null -eq $signature.TimeStamperCertificate -or
        (Get-CertificateSha256 $signature.SignerCertificate) -cne
            $expectedPublisherCertificateSha256) {
        throw '解除安裝器未通過 X MCSV 固定正式發布者 Authenticode 與時間戳驗證。'
    }
}

function Assert-OwnedArpRegistration {
    param([Parameter(Mandatory = $true)][string]$InstallRoot)

    $baseKey = [Microsoft.Win32.RegistryKey]::OpenBaseKey(
        [Microsoft.Win32.RegistryHive]::LocalMachine,
        [Microsoft.Win32.RegistryView]::Registry64)
    try {
        $key = $baseKey.OpenSubKey($arpRegistrySubKey, $false)
        if ($null -eq $key) { return $false }
        try {
            $registeredInstallRoot = [string]$key.GetValue(
                'InstallLocation',
                $null,
                [Microsoft.Win32.RegistryValueOptions]::DoNotExpandEnvironmentNames)
            if ($key.SubKeyCount -ne 0 -or
                [string]$key.GetValue('ProductId', $null,
                    [Microsoft.Win32.RegistryValueOptions]::DoNotExpandEnvironmentNames) -cne $productId -or
                [string]$key.GetValue('PublisherCertificateSha256', $null,
                    [Microsoft.Win32.RegistryValueOptions]::DoNotExpandEnvironmentNames) -cne
                    $expectedPublisherCertificateSha256 -or
                [string]::IsNullOrWhiteSpace($registeredInstallRoot) -or
                -not [string]::Equals(
                    [IO.Path]::GetFullPath($registeredInstallRoot).TrimEnd('\', '/'),
                    [IO.Path]::GetFullPath($InstallRoot).TrimEnd('\', '/'),
                    [StringComparison]::OrdinalIgnoreCase)) {
                throw 'Apps & Features 登錄鍵不是此 X MCSV 安裝所擁有，拒絕刪除。'
            }
            return $true
        } finally {
            $key.Dispose()
        }
    } finally {
        $baseKey.Dispose()
    }
}

function Remove-OwnedArpRegistration {
    param([Parameter(Mandatory = $true)][string]$InstallRoot)

    if (-not (Assert-OwnedArpRegistration -InstallRoot $InstallRoot)) { return }
    $baseKey = [Microsoft.Win32.RegistryKey]::OpenBaseKey(
        [Microsoft.Win32.RegistryHive]::LocalMachine,
        [Microsoft.Win32.RegistryView]::Registry64)
    try {
        # Revalidate through the same exact ownership contract immediately before deleting only
        # this product-owned key. No caller-controlled registry path is ever accepted.
        $key = $baseKey.OpenSubKey($arpRegistrySubKey, $false)
        if ($null -eq $key) { return }
        try {
            if ($key.SubKeyCount -ne 0 -or
                [string]$key.GetValue('ProductId', $null,
                    [Microsoft.Win32.RegistryValueOptions]::DoNotExpandEnvironmentNames) -cne $productId -or
                [string]$key.GetValue('PublisherCertificateSha256', $null,
                    [Microsoft.Win32.RegistryValueOptions]::DoNotExpandEnvironmentNames) -cne
                    $expectedPublisherCertificateSha256 -or
                -not [string]::Equals(
                    [IO.Path]::GetFullPath([string]$key.GetValue(
                        'InstallLocation',
                        $null,
                        [Microsoft.Win32.RegistryValueOptions]::DoNotExpandEnvironmentNames)).TrimEnd('\', '/'),
                    [IO.Path]::GetFullPath($InstallRoot).TrimEnd('\', '/'),
                    [StringComparison]::OrdinalIgnoreCase)) {
                throw 'X MCSV Apps & Features 所有權標記在刪除前已變更。'
            }
        } finally {
            $key.Dispose()
        }
        $baseKey.DeleteSubKey($arpRegistrySubKey, $false)
    } finally {
        $baseKey.Dispose()
    }
}

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

function Resolve-GuardedUserClientDataRoot {
    param(
        [Parameter(Mandatory = $true)][string]$Path,
        [Parameter(Mandatory = $true)][string]$OwnerSid
    )

    if (-not [IO.Path]::IsPathFullyQualified($Path)) {
        throw '使用者客戶端資料目錄必須是完整路徑。'
    }
    try {
        $expectedOwnerSid = [Security.Principal.SecurityIdentifier]::new($OwnerSid)
    }
    catch [ArgumentException] {
        throw 'UserClientDataOwnerSid 必須是有效的 Windows SID。'
    }

    $profileKey = "Registry::HKEY_LOCAL_MACHINE\SOFTWARE\Microsoft\Windows NT\CurrentVersion\ProfileList\$OwnerSid"
    $profile = Get-ItemProperty -LiteralPath $profileKey -Name ProfileImagePath -ErrorAction Stop
    $profileRoot = [IO.Path]::GetFullPath(
        [Environment]::ExpandEnvironmentVariables([string]$profile.ProfileImagePath)).TrimEnd('\', '/')
    $expectedPath = [IO.Path]::GetFullPath(
        (Join-Path $profileRoot 'AppData\Local\Muhun\MCSV\client')).TrimEnd('\', '/')
    $fullPath = [IO.Path]::GetFullPath($Path).TrimEnd('\', '/')
    if ($fullPath.StartsWith('\\') -or
        -not [string]::Equals($fullPath, $expectedPath, [StringComparison]::OrdinalIgnoreCase)) {
        throw "使用者客戶端資料路徑必須精確對應 SID $OwnerSid 的 LocalAppData：$expectedPath"
    }
    if (-not (Test-Path -LiteralPath $fullPath -PathType Container)) {
        return $null
    }

    for ($cursor = Get-Item -LiteralPath $fullPath -Force;
         $null -ne $cursor -and (Test-IsUnderRoot $cursor.FullName $profileRoot);
         $cursor = $cursor.Parent) {
        if (($cursor.Attributes -band [IO.FileAttributes]::ReparsePoint) -ne 0) {
            throw "使用者客戶端資料路徑不可經過 reparse point：$($cursor.FullName)"
        }
    }

    $acl = Get-Acl -LiteralPath $fullPath
    $actualOwnerSid = try {
        ([Security.Principal.NTAccount]$acl.Owner).Translate(
            [Security.Principal.SecurityIdentifier])
    }
    catch [Security.Principal.IdentityNotMappedException] {
        [Security.Principal.SecurityIdentifier]::new($acl.Owner)
    }
    if ($actualOwnerSid.Value -ne $expectedOwnerSid.Value) {
        throw "使用者客戶端資料目錄 ACL 擁有者與指定 SID 不符，拒絕移除：$fullPath"
    }

    return $fullPath
}

function Wait-WindowsServiceAbsent {
    param(
        [Parameter(Mandatory = $true)][string]$Name,
        [ValidateRange(1, 120)][int]$TimeoutSeconds = 30
    )

    $timer = [Diagnostics.Stopwatch]::StartNew()
    try {
        while ($timer.Elapsed -lt [TimeSpan]::FromSeconds($TimeoutSeconds)) {
            $queryOutput = & "$env:SystemRoot\System32\sc.exe" query $Name 2>&1
            $queryExitCode = $LASTEXITCODE
            if ($queryExitCode -eq 1060) {
                return
            }
            if ($queryExitCode -ne 0 -and $queryExitCode -ne 1072) {
                throw "無法確認 Windows Service '$Name' 的移除狀態（ExitCode $queryExitCode）：$($queryOutput -join ' ')"
            }

            Start-Sleep -Milliseconds 250
        }
    }
    finally {
        $timer.Stop()
    }

    throw "Windows Service '$Name' 在 $TimeoutSeconds 秒內仍未完全移除；可能仍有程序持有 ServiceController handle。請關閉相關管理工具後再重新安裝。"
}

function Remove-WindowsServiceAndWait {
    param([Parameter(Mandatory = $true)][string]$Name)

    $service = Get-Service -Name $Name -ErrorAction SilentlyContinue
    if ($null -ne $service) {
        try {
            if ($service.Status -ne 'Stopped') {
                Stop-Service -InputObject $service -Force
                $service.WaitForStatus('Stopped', [TimeSpan]::FromSeconds(30))
            }
        }
        finally {
            # sc.exe delete cannot finish while this process still owns a ServiceController handle.
            $service.Dispose()
        }
    }

    $output = & "$env:SystemRoot\System32\sc.exe" delete $Name 2>&1
    $deleteExitCode = $LASTEXITCODE
    if ($deleteExitCode -ne 0 -and $deleteExitCode -ne 1060 -and $deleteExitCode -ne 1072) {
        throw "Windows Service 移除失敗（ExitCode $deleteExitCode）：$($output -join ' ')"
    }

    # ERROR_SERVICE_MARKED_FOR_DELETE (1072) is safe to wait through, but returning before the
    # service database entry disappears makes an immediate reinstall fail unpredictably.
    Wait-WindowsServiceAbsent -Name $Name -TimeoutSeconds 30
}

$identity = [Security.Principal.WindowsIdentity]::GetCurrent()
$principal = [Security.Principal.WindowsPrincipal]::new($identity)
if (-not $principal.IsInRole([Security.Principal.WindowsBuiltInRole]::Administrator)) {
    throw '解除安裝 Windows Service 需要以系統管理員身分執行。'
}

Assert-TrustedUninstallerSelf

$install = Resolve-GuardedRoot $InstallRoot '.muhun-mcsv-install-root'
$ownedArpRegistrationExists = Assert-OwnedArpRegistration -InstallRoot $install
$data = if ($RemoveData) { Resolve-GuardedRoot $DataRoot '.muhun-mcsv-data-root' } else { $null }
$userClientData = if ($RemoveUserClientData) {
    if ([string]::IsNullOrWhiteSpace($UserClientDataRoot) -or
        [string]::IsNullOrWhiteSpace($UserClientDataOwnerSid)) {
        throw ('提升權限後不可推測互動使用者的 LocalAppData。若要移除 per-user client 資料，' +
            '必須同時明確指定 -UserClientDataRoot 與 -UserClientDataOwnerSid。')
    }
    Resolve-GuardedUserClientDataRoot $UserClientDataRoot $UserClientDataOwnerSid
}
else {
    $null
}
if ($RemoveData -and ((Test-IsUnderRoot $install $data) -or (Test-IsUnderRoot $data $install))) {
    throw '程式目錄與資料目錄不可相同或互相包含。'
}
# Migration contract: previous releases used this equivalent authorization boundary:
# if ($PSCmdlet.ShouldProcess($install, '解除安裝 Muhun MCSV Manager 與 Windows Service'))
if ($PSCmdlet.ShouldProcess($install, '解除安裝 X MCSV 與 Windows Service')) {
    Remove-WindowsServiceAndWait -Name $serviceName

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
        (Join-Path ([Environment]::GetFolderPath([Environment+SpecialFolder]::Programs)) `
            $legacyStartMenuShortcutName),
        (Join-Path ([Environment]::GetFolderPath([Environment+SpecialFolder]::Startup)) `
            $startupShortcutName))) {
        if (Test-Path -LiteralPath $shortcut -PathType Leaf) {
            Remove-Item -LiteralPath $shortcut -Force
        }
    }

    [void](Resolve-GuardedRoot $install '.muhun-mcsv-install-root')
    Remove-Item -LiteralPath $install -Recurse -Force
    if ($ownedArpRegistrationExists) {
        Remove-OwnedArpRegistration -InstallRoot $install
    }
    Write-Host 'X MCSV 程式與 Windows Service 已移除。'
    if (-not $RemoveData) {
        Write-Host "程式已移除；伺服器、備份與服務帳號資料保留於 $DataRoot。"
    }
}

if ($RemoveData -and $null -ne $data -and $PSCmdlet.ShouldProcess(
        $data,
        '永久刪除所有伺服器、世界地圖、模組、設定、備份、服務帳號與遠端控制資料；此動作無法復原')) {
    [void](Resolve-GuardedRoot $data '.muhun-mcsv-data-root')
    Remove-Item -LiteralPath $data -Recurse -Force
    Write-Host '伺服器、備份與服務帳號資料已永久移除；資料無法由解除安裝程式復原。'
}

if ($RemoveUserClientData) {
    if ($null -eq $userClientData) {
        Write-Host '指定 SID 的 per-user client 資料目錄不存在，未移除任何使用者資料。'
    }
    elseif ($PSCmdlet.ShouldProcess(
            $userClientData,
            "移除 SID $UserClientDataOwnerSid 的 Minecraft client 實例、快取、runtime 與 Microsoft 帳號保存庫")) {
        [void](Resolve-GuardedUserClientDataRoot $userClientData $UserClientDataOwnerSid)
        Remove-Item -LiteralPath $userClientData -Recurse -Force
        Write-Host "已移除 SID $UserClientDataOwnerSid 的 per-user client 資料；無法由解除安裝程式復原。"
    }
}
else {
    Write-Host ('per-user client 資料預設保留。解除安裝程式不會從提升權限的管理員帳號推測其他使用者；' +
        '如需移除，請明確使用 -RemoveUserClientData、-UserClientDataRoot 與 -UserClientDataOwnerSid。')
}
