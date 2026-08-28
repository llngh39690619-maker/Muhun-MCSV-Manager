#requires -Version 7.4

[CmdletBinding(SupportsShouldProcess = $true)]
param(
    [Parameter(Mandatory = $true)]
    [string]$SourceDirectory,

    [string]$InstallRoot = "$env:ProgramFiles\Muhun\MCSV",

    [string]$DataRoot = "$env:ProgramData\Muhun\MCSV"
)

$ErrorActionPreference = 'Stop'
Set-StrictMode -Version Latest

$serviceName = 'MuhunMCSV'
$serviceDisplayName = 'Muhun MCSV Service'
$operatorsGroupName = 'Muhun MCSV Operators'
$operatorsGroupDescription = 'Accounts authorized to control Muhun MCSV.'
$installMarker = '.muhun-mcsv-install-root'
$dataMarker = '.muhun-mcsv-data-root'
$expectedMarker = 'muhun.mcsv.manager:1'
$manifestName = 'release-manifest.json'
$checksumName = 'SHA256SUMS.txt'
$installerOperatorSidRelativePath = 'data\installer-operator-sid.v1'
$activationStateDirectoryName = 'activation-state'
$stableLauncherDirectoryName = 'launcher'
$stableLauncherFileName = 'Muhun MCSV Updater.exe'
$productId = 'muhun.mcsv.manager'
$expectedPublisherCertificateSha256 = '1a67e65dc9c367ac3247d0483edbe94dab38c5494859a43210c1ad4719e80b71'
$startMenuShortcutName = 'X MCSV.lnk'
$legacyStartMenuShortcutName = 'Muhun MCSV Manager.lnk'
$startupShortcutName = 'Muhun MCSV GUI Activation Broker.lnk'
$installedUninstallerRelativePath = 'tools\Uninstall-MuhunMcsv.ps1'
$arpRegistrySubKey = 'SOFTWARE\Microsoft\Windows\CurrentVersion\Uninstall\MuhunMCSV'
$serviceStopTimeoutSeconds = 120

function Assert-Administrator {
    $identity = [Security.Principal.WindowsIdentity]::GetCurrent()
    $principal = [Security.Principal.WindowsPrincipal]::new($identity)
    if (-not $principal.IsInRole([Security.Principal.WindowsBuiltInRole]::Administrator)) {
        throw '安裝 Windows Service 需要以系統管理員身分執行。'
    }
}

function Assert-LocalGroupDescriptionSupported {
    $descriptionParameter = (Get-Command New-LocalGroup -ErrorAction Stop).Parameters['Description']
    $lengthValidators = @($descriptionParameter.Attributes | Where-Object {
        $_ -is [Management.Automation.ValidateLengthAttribute]
    })
    if ($lengthValidators.Count -ne 1 -or
        $operatorsGroupDescription.Length -lt $lengthValidators[0].MinLength -or
        $operatorsGroupDescription.Length -gt $lengthValidators[0].MaxLength) {
        throw "Muhun MCSV Operators 群組描述不符合 Windows 長度限制：$($operatorsGroupDescription.Length) 個字元。"
    }
}

function Ensure-MuhunOperatorsGroup {
    param([Parameter(Mandatory = $true)][pscustomobject]$Mutation)

    $group = Get-LocalGroup -Name $operatorsGroupName -ErrorAction SilentlyContinue
    if ($null -eq $group) {
        $group = New-LocalGroup -Name $operatorsGroupName `
            -Description $operatorsGroupDescription
        $Mutation.GroupCreated = $true
    }
    if ($null -eq $group.SID) {
        throw '無法確認 Muhun MCSV Operators 群組 SID。'
    }
    $Mutation.GroupSid = $group.SID.Value

    $identity = [Security.Principal.WindowsIdentity]::GetCurrent()
    if ($null -eq $identity.User) {
        throw '無法確認目前安裝帳號的 Windows SID。'
    }
    $localSystem = [Security.Principal.SecurityIdentifier]::new(
        [Security.Principal.WellKnownSidType]::LocalSystemSid,
        $null)
    if (-not $identity.User.Equals($localSystem)) {
        $alreadyMember = @(Get-LocalGroupMember -Group $group -ErrorAction Stop) |
            Where-Object { $null -ne $_.SID -and $_.SID.Value -eq $identity.User.Value } |
            Select-Object -First 1
        if ($null -eq $alreadyMember) {
            Add-LocalGroupMember -Group $group -Member $identity.Name -ErrorAction Stop
            $Mutation.MemberAdded = $true
            $Mutation.MemberSid = $identity.User.Value
        }
    }

    return '*' + $group.SID.Value
}

function Resolve-SafeLocalDirectory {
    param([string]$Path, [string]$Label)
    if ([string]::IsNullOrWhiteSpace($Path) -or -not [IO.Path]::IsPathFullyQualified($Path)) {
        throw "$Label 必須是完整本機路徑。"
    }
    $fullPath = [IO.Path]::GetFullPath($Path).TrimEnd('\', '/')
    $root = [IO.Path]::GetPathRoot($fullPath).TrimEnd('\', '/')
    if ($fullPath.StartsWith('\\', [StringComparison]::Ordinal) -or
        $fullPath.StartsWith('//', [StringComparison]::Ordinal) -or
        [string]::Equals($fullPath, $root, [StringComparison]::OrdinalIgnoreCase)) {
        throw "$Label 不可使用 UNC、裝置路徑或磁碟根目錄。"
    }
    return $fullPath
}

function Test-IsUnderRoot {
    param([string]$Candidate, [string]$Root)
    $normalizedRoot = [IO.Path]::GetFullPath($Root).TrimEnd('\', '/') + [IO.Path]::DirectorySeparatorChar
    $normalizedCandidate = [IO.Path]::GetFullPath($Candidate).TrimEnd('\', '/') + [IO.Path]::DirectorySeparatorChar
    return $normalizedCandidate.StartsWith($normalizedRoot, [StringComparison]::OrdinalIgnoreCase)
}

function Assert-NoExistingReparsePoints {
    param([string]$Path, [string]$Label)
    $candidate = [IO.Path]::GetFullPath($Path).TrimEnd('\', '/')
    while (-not (Test-Path -LiteralPath $candidate)) {
        $parent = [IO.Path]::GetDirectoryName($candidate)
        if ([string]::IsNullOrWhiteSpace($parent) -or
            [string]::Equals($parent, $candidate, [StringComparison]::OrdinalIgnoreCase)) {
            throw "$Label 沒有可驗證的本機祖先目錄。"
        }
        $candidate = $parent
    }
    $cursor = Get-Item -LiteralPath $candidate -Force
    while ($null -ne $cursor) {
        if (($cursor.Attributes -band [IO.FileAttributes]::ReparsePoint) -ne 0) {
            throw "$Label 不可經過 reparse point：$($cursor.FullName)"
        }
        $cursor = if ($cursor -is [IO.DirectoryInfo]) { $cursor.Parent } else { $cursor.Directory }
    }
}

function New-ParentOwnershipNonce {
    $bytes = [byte[]]::new(32)
    [Security.Cryptography.RandomNumberGenerator]::Fill($bytes)
    return [Convert]::ToHexString($bytes).ToLowerInvariant()
}

function Assert-ImmediateParentOwnershipBoundary {
    param(
        [Parameter(Mandatory = $true)]$Record,
        [Parameter(Mandatory = $true)][string]$Label
    )

    $childPath = [IO.Path]::GetFullPath([string]$Record.ChildPath).TrimEnd('\', '/')
    $parentPath = [IO.Path]::GetFullPath([string]$Record.ParentPath).TrimEnd('\', '/')
    $expectedParent = [IO.Path]::GetDirectoryName($childPath)
    $parentRoot = [IO.Path]::GetPathRoot($parentPath).TrimEnd('\', '/')
    if ([string]::IsNullOrWhiteSpace($expectedParent) -or
        [string]::Equals($parentPath, $parentRoot, [StringComparison]::OrdinalIgnoreCase) -or
        -not [string]::Equals(
            $parentPath,
            [IO.Path]::GetFullPath($expectedParent).TrimEnd('\', '/'),
            [StringComparison]::OrdinalIgnoreCase)) {
        throw "$Label 不再是已記錄目標的直接父目錄。"
    }

    $markerPath = [IO.Path]::GetFullPath([string]$Record.MarkerPath).TrimEnd('\', '/')
    $expectedMarkerPath = [IO.Path]::GetFullPath(
        (Join-Path $parentPath ([string]$Record.MarkerName))).TrimEnd('\', '/')
    if (-not [string]::Equals($markerPath, $expectedMarkerPath, [StringComparison]::OrdinalIgnoreCase) -or
        -not [string]::Equals(
            [IO.Path]::GetDirectoryName($markerPath),
            $parentPath,
            [StringComparison]::OrdinalIgnoreCase)) {
        throw "$Label 的 ownership marker 已超出已記錄的直接父目錄。"
    }

    Assert-NoExistingReparsePoints $parentPath $Label
    if (Test-Path -LiteralPath $parentPath) {
        $parentItem = Get-Item -LiteralPath $parentPath -Force
        if ($parentItem -isnot [IO.DirectoryInfo] -or
            ($parentItem.Attributes -band [IO.FileAttributes]::ReparsePoint) -ne 0 -or
            -not [string]::Equals(
                [IO.Path]::GetFullPath($parentItem.FullName).TrimEnd('\', '/'),
                $parentPath,
                [StringComparison]::OrdinalIgnoreCase)) {
            throw "$Label 的父目錄類型、canonical path 或 reparse 狀態已改變。"
        }
    }
}

function Test-OwnedParentMarker {
    param([Parameter(Mandatory = $true)]$Record)

    if (-not $Record.CreatedByAttempt -or
        -not $Record.MarkerCreated -or
        -not (Test-Path -LiteralPath $Record.MarkerPath -PathType Leaf)) {
        return $false
    }
    $markerItem = Get-Item -LiteralPath $Record.MarkerPath -Force
    if (($markerItem.Attributes -band [IO.FileAttributes]::ReparsePoint) -ne 0 -or
        -not [string]::Equals(
            [IO.Path]::GetFullPath($markerItem.FullName).TrimEnd('\', '/'),
            [IO.Path]::GetFullPath([string]$Record.MarkerPath).TrimEnd('\', '/'),
            [StringComparison]::OrdinalIgnoreCase) -or
        -not [string]::Equals(
            [IO.Path]::GetDirectoryName($markerItem.FullName),
            [IO.Path]::GetFullPath([string]$Record.ParentPath).TrimEnd('\', '/'),
            [StringComparison]::OrdinalIgnoreCase)) {
        return $false
    }

    $expectedBytes = [Text.Encoding]::UTF8.GetBytes(
        "muhun.mcsv.parent-owner:1`n$([string]$Record.Nonce)")
    if ($markerItem.Length -ne $expectedBytes.Length) {
        return $false
    }
    $actualBytes = [IO.File]::ReadAllBytes($markerItem.FullName)
    return [Security.Cryptography.CryptographicOperations]::FixedTimeEquals(
        $actualBytes,
        $expectedBytes)
}

function Undo-PartialImmediateParentOwnership {
    param([Parameter(Mandatory = $true)]$Record)

    if (-not $Record.CreatedByAttempt) {
        return
    }
    try {
        Assert-ImmediateParentOwnershipBoundary $Record 'partial immediate parent cleanup'
        if ($Record.MarkerCreated -and
            (Test-Path -LiteralPath $Record.MarkerPath -PathType Leaf)) {
            $markerItem = Get-Item -LiteralPath $Record.MarkerPath -Force
            if (($markerItem.Attributes -band [IO.FileAttributes]::ReparsePoint) -ne 0 -or
                -not [string]::Equals(
                    [IO.Path]::GetFullPath($markerItem.FullName).TrimEnd('\', '/'),
                    [IO.Path]::GetFullPath([string]$Record.MarkerPath).TrimEnd('\', '/'),
                    [StringComparison]::OrdinalIgnoreCase)) {
                return
            }
            [IO.File]::Delete([string]$Record.MarkerPath)
            $Record.MarkerCreated = $false
        }

        Assert-ImmediateParentOwnershipBoundary $Record 'partial immediate parent final cleanup'
        if ((Test-Path -LiteralPath $Record.ChildPath) -or
            [IO.Directory]::EnumerateFileSystemEntries([string]$Record.ParentPath).GetEnumerator().MoveNext()) {
            return
        }
        [IO.Directory]::Delete([string]$Record.ParentPath, $false)
    } catch {
        # This is a fail-closed best-effort path. Any concurrently added content or changed
        # boundary intentionally preserves the directory rather than broadening cleanup.
    }
}

function Add-OwnedImmediateParentDirectory {
    param(
        [Parameter(Mandatory = $true)][string]$ChildPath,
        [Parameter(Mandatory = $true)]$OwnershipRecords
    )

    $canonicalChild = [IO.Path]::GetFullPath($ChildPath).TrimEnd('\', '/')
    $canonicalParent = [IO.Path]::GetFullPath(
        [IO.Path]::GetDirectoryName($canonicalChild)).TrimEnd('\', '/')
    if ($OwnershipRecords.ContainsKey($canonicalParent)) {
        Assert-ImmediateParentOwnershipBoundary `
            $OwnershipRecords[$canonicalParent] `
            'deduplicated immediate parent ownership'
        return
    }

    $nonce = New-ParentOwnershipNonce
    $markerName = ".muhun-mcsv-parent-owner-$nonce.marker"
    $record = [pscustomobject]@{
        ChildPath = $canonicalChild
        ParentPath = $canonicalParent
        MarkerName = $markerName
        MarkerPath = Join-Path $canonicalParent $markerName
        Nonce = $nonce
        CreatedByAttempt = $false
        MarkerCreated = $false
    }

    if (Test-Path -LiteralPath $canonicalParent) {
        Assert-ImmediateParentOwnershipBoundary $record 'preexisting immediate parent boundary'
        return
    }

    # Validate before the exclusive New-Item attempt. Unlike Directory.CreateDirectory,
    # New-Item without -Force reports an already-existing final directory, allowing a
    # concurrent creator to remain unowned by this transaction.
    Assert-ImmediateParentOwnershipBoundary $record 'new immediate parent ownership'
    try {
        try {
            $exclusiveParentPath = [Management.Automation.WildcardPattern]::Escape($canonicalParent)
            $createdParent = New-Item -ItemType Directory -Path $exclusiveParentPath -ErrorAction Stop
            $record.CreatedByAttempt = $true
        } catch {
            if (Test-Path -LiteralPath $canonicalParent -PathType Container) {
                Assert-ImmediateParentOwnershipBoundary $record 'concurrently created immediate parent'
                return
            }
            throw
        }
        if ($createdParent -isnot [IO.DirectoryInfo] -or
            -not [string]::Equals(
                [IO.Path]::GetFullPath($createdParent.FullName).TrimEnd('\', '/'),
                $canonicalParent,
                [StringComparison]::OrdinalIgnoreCase)) {
            throw '無法證明直接父目錄由本次安裝建立。'
        }

        # The post-create pass detects a final component replaced by a junction.
        Assert-ImmediateParentOwnershipBoundary $record 'created immediate parent ownership'
        $OwnershipRecords.Add($canonicalParent, $record)

        $markerBytes = [Text.Encoding]::UTF8.GetBytes("muhun.mcsv.parent-owner:1`n$nonce")
        $markerStream = [IO.FileStream]::new(
            $record.MarkerPath,
            [IO.FileMode]::CreateNew,
            [IO.FileAccess]::Write,
            [IO.FileShare]::None,
            4096,
            [IO.FileOptions]::WriteThrough)
        $record.MarkerCreated = $true
        try {
            $markerStream.Write($markerBytes, 0, $markerBytes.Length)
            $markerStream.Flush($true)
        } finally {
            $markerStream.Dispose()
        }
        Assert-ImmediateParentOwnershipBoundary $record 'recorded immediate parent ownership'
        if (-not (Test-OwnedParentMarker $record)) {
            throw '無法驗證本次建立之直接父目錄 ownership marker。'
        }
    } catch {
        Undo-PartialImmediateParentOwnership $record
        throw
    }
}

function Remove-OwnedParentMarker {
    param([Parameter(Mandatory = $true)]$Record)

    Assert-ImmediateParentOwnershipBoundary $Record 'parent ownership marker cleanup'
    if (-not (Test-OwnedParentMarker $Record)) {
        return $false
    }
    # Revalidate immediately before mutation so a swapped parent/marker fails closed.
    Assert-ImmediateParentOwnershipBoundary $Record 'parent ownership marker final cleanup'
    if (-not (Test-OwnedParentMarker $Record)) {
        return $false
    }
    [IO.File]::Delete([string]$Record.MarkerPath)
    if (-not (Test-Path -LiteralPath $Record.MarkerPath)) {
        $Record.MarkerCreated = $false
        return $true
    }
    return $false
}

function Complete-OwnedImmediateParentDirectories {
    param([Parameter(Mandatory = $true)]$OwnershipRecords)

    foreach ($record in @($OwnershipRecords.Values)) {
        if (-not (Remove-OwnedParentMarker $record)) {
            throw "安裝完成但無法移除直接父目錄 ownership marker：$($record.ParentPath)"
        }
    }
}

function Remove-OwnedEmptyImmediateParentDirectory {
    param([Parameter(Mandatory = $true)]$Record)

    try {
        Assert-ImmediateParentOwnershipBoundary $Record 'owned immediate parent rollback'
    } catch {
        Write-Warning $_.Exception.Message
        return $false
    }
    if ((Test-Path -LiteralPath $Record.ChildPath) -or
        -not (Test-OwnedParentMarker $Record)) {
        return $false
    }

    $entries = @([IO.Directory]::EnumerateFileSystemEntries([string]$Record.ParentPath))
    $foreignEntries = @($entries | Where-Object {
        -not [string]::Equals(
            [IO.Path]::GetFullPath($_),
            [IO.Path]::GetFullPath([string]$Record.MarkerPath),
            [StringComparison]::OrdinalIgnoreCase)
    })
    if ($foreignEntries.Count -ne 0) {
        [void](Remove-OwnedParentMarker $Record)
        return $false
    }

    # Validate canonical path, direct-parent relationship, reparse state and nonce again
    # immediately before deleting only our marker and the now-empty parent.
    Assert-ImmediateParentOwnershipBoundary $Record 'owned immediate parent final rollback'
    if ((Test-Path -LiteralPath $Record.ChildPath) -or
        -not (Test-OwnedParentMarker $Record)) {
        return $false
    }
    $finalEntries = @([IO.Directory]::EnumerateFileSystemEntries([string]$Record.ParentPath))
    if ($finalEntries.Count -ne 1 -or
        -not [string]::Equals(
            [IO.Path]::GetFullPath($finalEntries[0]),
            [IO.Path]::GetFullPath([string]$Record.MarkerPath),
            [StringComparison]::OrdinalIgnoreCase)) {
        return $false
    }
    [IO.File]::Delete([string]$Record.MarkerPath)
    $Record.MarkerCreated = $false

    Assert-ImmediateParentOwnershipBoundary $Record 'empty immediate parent delete'
    if ((Test-Path -LiteralPath $Record.ChildPath) -or
        [IO.Directory]::EnumerateFileSystemEntries([string]$Record.ParentPath).GetEnumerator().MoveNext()) {
        return $false
    }
    [IO.Directory]::Delete([string]$Record.ParentPath, $false)
    return -not (Test-Path -LiteralPath $Record.ParentPath)
}

function Test-SafeRelativePath {
    param([string]$Path)
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

function Resolve-SafeSourceFile {
    param([string]$Source, [string]$RelativePath)
    if (-not (Test-SafeRelativePath $RelativePath)) {
        throw "正式安裝 manifest 包含不安全路徑：$RelativePath"
    }
    $candidate = [IO.Path]::GetFullPath((Join-Path $Source $RelativePath.Replace('/', '\')))
    if (-not (Test-IsUnderRoot $candidate $Source) -or
        -not (Test-Path -LiteralPath $candidate -PathType Leaf)) {
        throw "正式安裝來源缺少安全檔案：$RelativePath"
    }
    $cursor = Get-Item -LiteralPath $candidate -Force
    $normalizedSource = $Source.TrimEnd('\', '/') + [IO.Path]::DirectorySeparatorChar
    while ($null -ne $cursor -and
        $cursor.FullName.StartsWith($normalizedSource, [StringComparison]::OrdinalIgnoreCase)) {
        if (($cursor.Attributes -band [IO.FileAttributes]::ReparsePoint) -ne 0) {
            throw "正式安裝來源不可經過 reparse point：$RelativePath"
        }
        $cursor = if ($cursor -is [IO.DirectoryInfo]) { $cursor.Parent } else { $cursor.Directory }
    }
    return $candidate
}

function Get-Sha256Hex {
    param([string]$Path)
    return (Get-FileHash -LiteralPath $Path -Algorithm SHA256).Hash.ToLowerInvariant()
}

function Get-CertificateSha256 {
    param($Certificate)
    return [Convert]::ToHexString(
        [Security.Cryptography.SHA256]::HashData($Certificate.RawData)).ToLowerInvariant()
}

function Assert-FormalProductVersion {
    param([string]$Path, [string]$ExpectedVersion, [string]$Label)
    $versionInfo = [Diagnostics.FileVersionInfo]::GetVersionInfo($Path)
    $versionParts = $ExpectedVersion.Split('-', 2)[0].Split('.')
    $expectedNumericVersion = "$($versionParts[0]).$($versionParts[1]).$($versionParts[2]).0"
    if (([string]$versionInfo.ProductVersion).Trim() -cne $ExpectedVersion -or
        ([string]$versionInfo.FileVersion).Trim() -cne $expectedNumericVersion) {
        throw "$Label 的 ProductVersion/FileVersion 與已簽署版本不一致。"
    }
}

function Get-OptionalProductServiceSid {
    try {
        return ([Security.Principal.NTAccount]::new('NT SERVICE', $serviceName)).Translate(
            [Security.Principal.SecurityIdentifier])
    } catch [Security.Principal.IdentityNotMappedException] {
        return $null
    }
}

function New-InstallAclGrants {
    param(
        [Parameter(Mandatory = $true)]
        [Security.Principal.SecurityIdentifier]$InstallerSid,

        [AllowNull()]
        [Security.Principal.SecurityIdentifier]$ServiceSid,

        [ValidateSet('None', 'ReadAndExecute', 'Modify')]
        [string]$ServiceRights = 'None'
    )

    $systemSid = [Security.Principal.SecurityIdentifier]::new(
        [Security.Principal.WellKnownSidType]::LocalSystemSid,
        $null)
    $administratorsSid = [Security.Principal.SecurityIdentifier]::new(
        [Security.Principal.WellKnownSidType]::BuiltinAdministratorsSid,
        $null)
    $grants = [Collections.Generic.List[object]]::new()
    $grants.Add([pscustomobject]@{
        Sid = $systemSid
        Rights = [Security.AccessControl.FileSystemRights]::FullControl
    })
    $grants.Add([pscustomobject]@{
        Sid = $administratorsSid
        Rights = [Security.AccessControl.FileSystemRights]::FullControl
    })
    $grants.Add([pscustomobject]@{
        Sid = $InstallerSid
        Rights = [Security.AccessControl.FileSystemRights]::ReadAndExecute
    })
    if ($null -ne $ServiceSid -and $ServiceRights -cne 'None') {
        $rights = if ($ServiceRights -ceq 'Modify') {
            [Security.AccessControl.FileSystemRights]::Modify
        } else {
            [Security.AccessControl.FileSystemRights]::ReadAndExecute
        }
        $grants.Add([pscustomobject]@{
            Sid = $ServiceSid
            Rights = $rights
        })
    }
    return ,$grants.ToArray()
}

function Set-ExactProtectedPathAcl {
    param(
        [Parameter(Mandatory = $true)][string]$Path,
        [Parameter(Mandatory = $true)][object[]]$Grants,
        [Parameter(Mandatory = $true)][string]$Label
    )

    Assert-NoExistingReparsePoints $Path $Label
    $item = Get-Item -LiteralPath $Path -Force -ErrorAction Stop
    if (($item.Attributes -band [IO.FileAttributes]::ReparsePoint) -ne 0) {
        throw "$Label 不可是 reparse point。"
    }
    $isDirectory = $item -is [IO.DirectoryInfo]
    $security = if ($isDirectory) {
        [Security.AccessControl.DirectorySecurity]::new()
    } elseif ($item -is [IO.FileInfo]) {
        [Security.AccessControl.FileSecurity]::new()
    } else {
        throw "$Label 不是一般檔案或目錄。"
    }
    $security.SetAccessRuleProtection($true, $false)
    $administratorsSid = [Security.Principal.SecurityIdentifier]::new(
        [Security.Principal.WellKnownSidType]::BuiltinAdministratorsSid,
        $null)
    $security.SetOwner($administratorsSid)
    foreach ($grant in $Grants) {
        $rule = if ($isDirectory) {
            [Security.AccessControl.FileSystemAccessRule]::new(
                [Security.Principal.SecurityIdentifier]$grant.Sid,
                [Security.AccessControl.FileSystemRights]$grant.Rights,
                [Security.AccessControl.InheritanceFlags]::ContainerInherit -bor
                    [Security.AccessControl.InheritanceFlags]::ObjectInherit,
                [Security.AccessControl.PropagationFlags]::None,
                [Security.AccessControl.AccessControlType]::Allow)
        } else {
            [Security.AccessControl.FileSystemAccessRule]::new(
                [Security.Principal.SecurityIdentifier]$grant.Sid,
                [Security.AccessControl.FileSystemRights]$grant.Rights,
                [Security.AccessControl.AccessControlType]::Allow)
        }
        [void]$security.AddAccessRule($rule)
    }
    Set-Acl -LiteralPath $item.FullName -AclObject $security -ErrorAction Stop
}

function Assert-ExactProtectedPathAcl {
    param(
        [Parameter(Mandatory = $true)][string]$Path,
        [Parameter(Mandatory = $true)][object[]]$Grants,
        [Parameter(Mandatory = $true)][string]$Label
    )

    Assert-NoExistingReparsePoints $Path $Label
    $item = Get-Item -LiteralPath $Path -Force -ErrorAction Stop
    if (($item.Attributes -band [IO.FileAttributes]::ReparsePoint) -ne 0) {
        throw "$Label 驗證時發現 reparse point。"
    }
    $isDirectory = $item -is [IO.DirectoryInfo]
    $security = Get-Acl -LiteralPath $item.FullName -ErrorAction Stop
    if (-not $security.AreAccessRulesProtected) {
        throw "$Label 仍繼承不受信任的 ACL。"
    }
    $administratorsSid = [Security.Principal.SecurityIdentifier]::new(
        [Security.Principal.WellKnownSidType]::BuiltinAdministratorsSid,
        $null)
    $ownerSid = $security.GetOwner([Security.Principal.SecurityIdentifier])
    if (-not $ownerSid.Equals($administratorsSid)) {
        throw "$Label 的 owner 不是 BUILTIN\Administrators。"
    }

    $expected = [Collections.Generic.Dictionary[string, object]]::new(
        [StringComparer]::Ordinal)
    foreach ($grant in $Grants) {
        $sidValue = ([Security.Principal.SecurityIdentifier]$grant.Sid).Value
        if ($expected.ContainsKey($sidValue)) {
            throw "$Label 的預期 ACL 含有重複 SID。"
        }
        $expected.Add($sidValue, $grant)
    }
    $actualRules = @($security.GetAccessRules(
        $true,
        $true,
        [Security.Principal.SecurityIdentifier]))
    if ($actualRules.Count -ne $expected.Count) {
        throw "$Label 的明確 ACL 數量不符。"
    }
    $seen = [Collections.Generic.HashSet[string]]::new([StringComparer]::Ordinal)
    foreach ($rule in $actualRules) {
        $sidValue = ([Security.Principal.SecurityIdentifier]$rule.IdentityReference).Value
        if ($rule.IsInherited -or
            $rule.AccessControlType -ne [Security.AccessControl.AccessControlType]::Allow -or
            -not $expected.ContainsKey($sidValue) -or
            -not $seen.Add($sidValue)) {
            throw "$Label 含有繼承、拒絕、重複或非預期 ACL：$sidValue"
        }
        if ([int64]$rule.FileSystemRights -ne
            [int64]([Security.AccessControl.FileSystemRights]$expected[$sidValue].Rights)) {
            throw "$Label 的 $sidValue 權限超出或少於必要範圍。"
        }
        $expectedInheritance = if ($isDirectory) {
            [Security.AccessControl.InheritanceFlags]::ContainerInherit -bor
                [Security.AccessControl.InheritanceFlags]::ObjectInherit
        } else {
            [Security.AccessControl.InheritanceFlags]::None
        }
        if ($rule.InheritanceFlags -ne $expectedInheritance -or
            $rule.PropagationFlags -ne [Security.AccessControl.PropagationFlags]::None) {
            throw "$Label 的 $sidValue ACL 傳播範圍不符。"
        }
    }
    foreach ($sidValue in $expected.Keys) {
        if (-not $seen.Contains($sidValue)) {
            throw "$Label 缺少必要 ACL：$sidValue"
        }
    }
}

function Get-InstallTreeServiceRights {
    param(
        [Parameter(Mandatory = $true)][string]$Path,
        [Parameter(Mandatory = $true)][string]$VersionsRoot,
        [Parameter(Mandatory = $true)][string]$ActivationStateRoot,
        [Parameter(Mandatory = $true)][string]$StableLauncherRoot,
        [Parameter(Mandatory = $true)][string]$ActivePointerPath
    )

    $normalized = [IO.Path]::GetFullPath($Path).TrimEnd('\', '/')
    $normalizedVersions = [IO.Path]::GetFullPath($VersionsRoot).TrimEnd('\', '/')
    $normalizedActivation = [IO.Path]::GetFullPath($ActivationStateRoot).TrimEnd('\', '/')
    $normalizedLauncher = [IO.Path]::GetFullPath($StableLauncherRoot).TrimEnd('\', '/')
    $normalizedPointer = [IO.Path]::GetFullPath($ActivePointerPath).TrimEnd('\', '/')
    if ([string]::Equals($normalized, $normalizedPointer, [StringComparison]::OrdinalIgnoreCase) -or
        (Test-IsUnderRoot $normalized $normalizedActivation)) {
        return 'Modify'
    }
    if ([string]::Equals($normalized, $normalizedVersions, [StringComparison]::OrdinalIgnoreCase)) {
        # The service needs delete-child/create rights here for signed side-by-side updates;
        # every already provisioned executable below it is reset to read/execute only.
        return 'Modify'
    }
    if (Test-IsUnderRoot $normalized $normalizedLauncher) {
        return 'None'
    }
    return 'ReadAndExecute'
}

function Set-AndAssertInstallExecutableTreeAcl {
    param(
        [Parameter(Mandatory = $true)][string]$InstallRoot,
        [Parameter(Mandatory = $true)][string]$VersionsRoot,
        [Parameter(Mandatory = $true)][string]$ActivationStateRoot,
        [Parameter(Mandatory = $true)][string]$StableLauncherRoot,
        [Parameter(Mandatory = $true)][string]$ActivePointerPath,
        [Parameter(Mandatory = $true)]
        [Security.Principal.SecurityIdentifier]$InstallerSid,
        [AllowNull()]
        [Security.Principal.SecurityIdentifier]$ServiceSid
    )

    Assert-NoExistingReparsePoints $InstallRoot '程式安裝根目錄'
    $rootRights = Get-InstallTreeServiceRights $InstallRoot $VersionsRoot `
        $ActivationStateRoot $StableLauncherRoot $ActivePointerPath
    $rootGrants = @(New-InstallAclGrants $InstallerSid $ServiceSid $rootRights)
    Set-ExactProtectedPathAcl $InstallRoot $rootGrants '程式安裝根目錄'

    $items = @(Get-ChildItem -LiteralPath $InstallRoot -Recurse -Force -ErrorAction Stop)
    if (@($items | Where-Object {
        ($_.Attributes -band [IO.FileAttributes]::ReparsePoint) -ne 0
    }).Count -ne 0) {
        throw '程式安裝可執行樹含有 reparse point。'
    }
    foreach ($item in $items) {
        $rights = Get-InstallTreeServiceRights $item.FullName $VersionsRoot `
            $ActivationStateRoot $StableLauncherRoot $ActivePointerPath
        $grants = @(New-InstallAclGrants $InstallerSid $ServiceSid $rights)
        Set-ExactProtectedPathAcl $item.FullName $grants "程式安裝可執行樹：$($item.FullName)"
    }

    # Re-enumerate after the writes so a concurrent replacement or newly introduced object
    # cannot escape the final fail-closed ACL and reparse validation.
    $verifiedItems = @(
        Get-Item -LiteralPath $InstallRoot -Force -ErrorAction Stop
        Get-ChildItem -LiteralPath $InstallRoot -Recurse -Force -ErrorAction Stop
    )
    foreach ($item in $verifiedItems) {
        $rights = Get-InstallTreeServiceRights $item.FullName $VersionsRoot `
            $ActivationStateRoot $StableLauncherRoot $ActivePointerPath
        $grants = @(New-InstallAclGrants $InstallerSid $ServiceSid $rights)
        Assert-ExactProtectedPathAcl $item.FullName $grants `
            "程式安裝可執行樹最終驗證：$($item.FullName)"
    }
}

function Get-SignedReleaseManifestEntry {
    param(
        [Parameter(Mandatory = $true)]$VerifiedRelease,
        [Parameter(Mandatory = $true)][string]$RelativePath
    )

    $matches = @($VerifiedRelease.Manifest.files | Where-Object {
        [string]::Equals(
            [string]$_.path,
            $RelativePath,
            [StringComparison]::OrdinalIgnoreCase)
    })
    if ($matches.Count -ne 1 -or
        $matches[0].sizeBytes -lt 1 -or
        $matches[0].sizeBytes -gt 2GB -or
        $matches[0].sha256 -notmatch '^[a-f0-9]{64}$') {
        throw "已簽署 release manifest 缺少唯一且有效的檔案：$RelativePath"
    }
    return $matches[0]
}

function Remove-TrustedVerifierStaging {
    param(
        [Parameter(Mandatory = $true)][string]$StagingRoot,
        [Parameter(Mandatory = $true)][string]$InstallRoot
    )

    if (-not (Test-Path -LiteralPath $StagingRoot)) {
        return
    }
    $normalizedStage = [IO.Path]::GetFullPath($StagingRoot).TrimEnd('\', '/')
    $normalizedInstall = [IO.Path]::GetFullPath($InstallRoot).TrimEnd('\', '/')
    if (-not (Test-IsUnderRoot $normalizedStage $normalizedInstall) -or
        -not [string]::Equals(
            [IO.Path]::GetDirectoryName($normalizedStage),
            $normalizedInstall,
            [StringComparison]::OrdinalIgnoreCase) -or
        -not [IO.Path]::GetFileName($normalizedStage).StartsWith(
            '.verification-',
            [StringComparison]::Ordinal)) {
        throw '拒絕清除不在程式安裝根目錄正下方的 verifier staging。'
    }
    Assert-NoExistingReparsePoints $normalizedStage 'verifier staging cleanup'
    if (@(Get-ChildItem -LiteralPath $normalizedStage -Recurse -Force -ErrorAction Stop |
        Where-Object { ($_.Attributes -band [IO.FileAttributes]::ReparsePoint) -ne 0 }).Count -ne 0) {
        throw 'verifier staging 含有 reparse point，拒絕遞迴清除。'
    }
    [IO.Directory]::Delete($normalizedStage, $true)
}

function Invoke-TrustedReleaseVerifier {
    param(
        [Parameter(Mandatory = $true)][string]$Source,
        [Parameter(Mandatory = $true)]$VerifiedRelease,
        [Parameter(Mandatory = $true)][string]$StagingRoot,
        [Parameter(Mandatory = $true)][string]$InstallRoot
    )

    $verifierRelativePath = 'Test-MuhunMcsvRelease.ps1'
    $verifierEntry = Get-SignedReleaseManifestEntry $VerifiedRelease $verifierRelativePath
    $sourceVerifier = Resolve-SafeSourceFile $Source $verifierRelativePath
    $publisherSha256 = ([string]$VerifiedRelease.Manifest.publisherCertificateSha256).ToLowerInvariant()
    if (Test-Path -LiteralPath $StagingRoot) {
        throw '本次 verifier staging 已存在，拒絕覆寫。'
    }
    [IO.Directory]::CreateDirectory($StagingRoot) | Out-Null
    try {
        $systemSid = [Security.Principal.SecurityIdentifier]::new(
            [Security.Principal.WellKnownSidType]::LocalSystemSid,
            $null)
        $administratorsSid = [Security.Principal.SecurityIdentifier]::new(
            [Security.Principal.WellKnownSidType]::BuiltinAdministratorsSid,
            $null)
        $trustedGrants = @(
            [pscustomobject]@{
                Sid = $systemSid
                Rights = [Security.AccessControl.FileSystemRights]::FullControl
            },
            [pscustomobject]@{
                Sid = $administratorsSid
                Rights = [Security.AccessControl.FileSystemRights]::FullControl
            }
        )
        Set-ExactProtectedPathAcl $StagingRoot $trustedGrants 'verifier staging'
        Assert-ExactProtectedPathAcl $StagingRoot $trustedGrants 'verifier staging'

        $trustedVerifier = Join-Path $StagingRoot $verifierRelativePath
        [IO.File]::Copy($sourceVerifier, $trustedVerifier, $false)
        Set-ExactProtectedPathAcl $trustedVerifier $trustedGrants 'staged release verifier'
        Assert-ExactProtectedPathAcl $trustedVerifier $trustedGrants 'staged release verifier'
        $trustedVerifierItem = Get-Item -LiteralPath $trustedVerifier -Force
        if ($trustedVerifierItem.Length -ne [long]$verifierEntry.sizeBytes -or
            (Get-Sha256Hex $trustedVerifier) -cne ([string]$verifierEntry.sha256).ToLowerInvariant()) {
            throw 'staged release verifier 未通過已簽署 manifest 的大小與 SHA-256 驗證。'
        }
        $signature = Get-AuthenticodeSignature -LiteralPath $trustedVerifier
        if ($signature.Status -ne [Management.Automation.SignatureStatus]::Valid -or
            $null -eq $signature.SignerCertificate -or
            $null -eq $signature.TimeStamperCertificate -or
            (Get-CertificateSha256 $signature.SignerCertificate) -cne $publisherSha256) {
            throw 'staged release verifier 未通過相同發布者的可信 Authenticode 與時間戳驗證。'
        }

        # The call target is now administrator-owned and non-reparse.  SourceDirectory is
        # passed only as verifier input; it is never used as the executable script path.
        & $trustedVerifier -ReleaseDirectory $Source | Out-Null
    } finally {
        Remove-TrustedVerifierStaging $StagingRoot $InstallRoot
    }
}

function Assert-SafeSignedStableLauncher {
    param(
        [Parameter(Mandatory = $true)][string]$Path,
        [Parameter(Mandatory = $true)][string]$PublisherCertificateSha256,
        [Parameter(Mandatory = $true)][string]$Label
    )

    Assert-NoExistingReparsePoints $Path $Label
    if (-not (Test-Path -LiteralPath $Path -PathType Leaf)) {
        throw "$Label 不是一般檔案。"
    }
    $item = Get-Item -LiteralPath $Path -Force
    if ($item -isnot [IO.FileInfo] -or
        ($item.Attributes -band [IO.FileAttributes]::ReparsePoint) -ne 0 -or
        $item.Length -lt 1 -or $item.Length -gt 512MB) {
        throw "$Label 不是安全的一般檔案。"
    }
    $signature = Get-AuthenticodeSignature -LiteralPath $Path
    if ($signature.Status -ne [Management.Automation.SignatureStatus]::Valid -or
        $null -eq $signature.SignerCertificate -or
        (Get-CertificateSha256 $signature.SignerCertificate) -cne
            $PublisherCertificateSha256.ToLowerInvariant()) {
        throw "$Label 未通過相同發布者 Authenticode 驗證。"
    }
    return Get-Sha256Hex $Path
}

function Install-StableLauncherTransactionally {
    param(
        [Parameter(Mandatory = $true)][string]$SourcePath,
        [Parameter(Mandatory = $true)][string]$DestinationPath,
        [Parameter(Mandatory = $true)][string]$PublisherCertificateSha256,
        [Parameter(Mandatory = $true)][pscustomobject]$Mutation
    )

    $sourceHash = Assert-SafeSignedStableLauncher `
        $SourcePath $PublisherCertificateSha256 'new stable GUI launcher'
    $destinationDirectory = [IO.Path]::GetDirectoryName(
        [IO.Path]::GetFullPath($DestinationPath))
    Assert-NoExistingReparsePoints $destinationDirectory 'stable GUI launcher directory'
    $directoryItem = Get-Item -LiteralPath $destinationDirectory -Force
    if ($directoryItem -isnot [IO.DirectoryInfo] -or
        ($directoryItem.Attributes -band [IO.FileAttributes]::ReparsePoint) -ne 0) {
        throw '穩定 GUI launcher 目錄不是安全的一般目錄。'
    }

    $temporaryPath = Join-Path $destinationDirectory `
        ('.muhun-launcher-new-' + [Guid]::NewGuid().ToString('N') + '.tmp')
    $backupPath = Join-Path $destinationDirectory `
        ('.muhun-launcher-old-' + [Guid]::NewGuid().ToString('N') + '.bak')
    try {
        [IO.File]::Copy($SourcePath, $temporaryPath, $false)
        $temporaryHash = Assert-SafeSignedStableLauncher `
            $temporaryPath $PublisherCertificateSha256 'staged stable GUI launcher'
        if ($temporaryHash -cne $sourceHash) {
            throw '穩定 GUI launcher 暫存副本雜湊不符。'
        }

        if (Test-Path -LiteralPath $DestinationPath) {
            $previousHash = Assert-SafeSignedStableLauncher `
                $DestinationPath $PublisherCertificateSha256 'existing stable GUI launcher'
            $Mutation.PreviousSha256 = $previousHash
            $Mutation.BackupPath = $backupPath
            [IO.File]::Replace($temporaryPath, $DestinationPath, $backupPath, $true)
            $Mutation.Replaced = $true
        } else {
            [IO.File]::Move($temporaryPath, $DestinationPath, $false)
            $Mutation.Created = $true
        }

        $installedHash = Assert-SafeSignedStableLauncher `
            $DestinationPath $PublisherCertificateSha256 'installed stable GUI launcher'
        if ($installedHash -cne $sourceHash) {
            throw '穩定 GUI launcher 原子更新後雜湊不符。'
        }
    } finally {
        if (Test-Path -LiteralPath $temporaryPath -PathType Leaf) {
            $temporaryItem = Get-Item -LiteralPath $temporaryPath -Force
            if ($temporaryItem -is [IO.FileInfo] -and
                ($temporaryItem.Attributes -band [IO.FileAttributes]::ReparsePoint) -eq 0) {
                [IO.File]::Delete($temporaryPath)
            }
        }
    }
}

function Restore-StableLauncherTransaction {
    param(
        [Parameter(Mandatory = $true)][string]$DestinationPath,
        [Parameter(Mandatory = $true)][string]$PublisherCertificateSha256,
        [Parameter(Mandatory = $true)][pscustomobject]$Mutation
    )

    if ($Mutation.Replaced) {
        $backupHash = Assert-SafeSignedStableLauncher `
            $Mutation.BackupPath $PublisherCertificateSha256 'stable GUI launcher rollback backup'
        if ($backupHash -cne $Mutation.PreviousSha256) {
            throw '穩定 GUI launcher 回復備份雜湊不符。'
        }
        if (Test-Path -LiteralPath $DestinationPath) {
            [void](Assert-SafeSignedStableLauncher `
                $DestinationPath $PublisherCertificateSha256 'current stable GUI launcher before rollback')
            $displacedPath = Join-Path ([IO.Path]::GetDirectoryName($DestinationPath)) `
                ('.muhun-launcher-displaced-' + [Guid]::NewGuid().ToString('N') + '.tmp')
            [IO.File]::Replace($Mutation.BackupPath, $DestinationPath, $displacedPath, $true)
            [IO.File]::Delete($displacedPath)
        } else {
            [IO.File]::Move($Mutation.BackupPath, $DestinationPath, $false)
        }
        $restoredHash = Assert-SafeSignedStableLauncher `
            $DestinationPath $PublisherCertificateSha256 'restored stable GUI launcher'
        if ($restoredHash -cne $Mutation.PreviousSha256) {
            throw '穩定 GUI launcher 回復後雜湊不符。'
        }
    } elseif ($Mutation.Created -and (Test-Path -LiteralPath $DestinationPath)) {
        [void](Assert-SafeSignedStableLauncher `
            $DestinationPath $PublisherCertificateSha256 'new stable GUI launcher rollback target')
        [IO.File]::Delete($DestinationPath)
    }
}

function Complete-StableLauncherTransaction {
    param(
        [Parameter(Mandatory = $true)][string]$PublisherCertificateSha256,
        [Parameter(Mandatory = $true)][pscustomobject]$Mutation
    )

    if ($Mutation.Replaced) {
        [void](Assert-SafeSignedStableLauncher `
            $Mutation.BackupPath $PublisherCertificateSha256 'stable GUI launcher committed backup')
        [IO.File]::Delete([string]$Mutation.BackupPath)
    }
}

function Invoke-Sc {
    param([Parameter(ValueFromRemainingArguments = $true)][string[]]$Arguments)

    $combinedOption = @($Arguments | Where-Object {
        $_ -match '^(?i:(?:binPath|start|DisplayName|obj|reset|actions)=\s|failureflag=)'
    } | Select-Object -First 1)
    if ($combinedOption.Count -ne 0) {
        throw "sc.exe 選項名稱與值必須使用兩個獨立引數：$($combinedOption[0])"
    }

    # PowerShell 7.3+ preserves each array element as one native argv in Standard mode.
    # sc.exe requires option names such as start= and their values to be separate argv.
    $PSNativeCommandArgumentPassing = 'Standard'
    $output = & "$env:SystemRoot\System32\sc.exe" @Arguments 2>&1
    if ($LASTEXITCODE -ne 0) {
        throw "sc.exe 執行失敗 ($LASTEXITCODE)：$($output -join ' ')"
    }
    return $output
}

function Initialize-ServiceFailureConfigurationInterop {
    if ($null -ne ('Muhun.Mcsv.Installer.ServiceFailureConfiguration' -as [type])) {
        return
    }

    Add-Type -TypeDefinition @'
using System;
using System.ComponentModel;
using System.Runtime.InteropServices;

namespace Muhun.Mcsv.Installer
{
    public sealed class ServiceFailureAction
    {
        public int Type { get; set; }
        public uint DelayMilliseconds { get; set; }
    }

    public sealed class ServiceFailureConfigurationSnapshot
    {
        public uint ResetPeriodSeconds { get; set; }
        public string RebootMessage { get; set; } = string.Empty;
        public string Command { get; set; } = string.Empty;
        public ServiceFailureAction[] Actions { get; set; } = Array.Empty<ServiceFailureAction>();
        public bool FailureFlag { get; set; }
    }

    public static class ServiceFailureConfiguration
    {
        private const uint ScManagerConnect = 0x0001;
        private const uint ServiceQueryConfig = 0x0001;
        private const uint ServiceChangeConfig = 0x0002;
        private const uint ServiceStart = 0x0010;
        private const uint ServiceConfigFailureActions = 2;
        private const uint ServiceConfigFailureActionsFlag = 4;
        private const int ErrorInsufficientBuffer = 122;
        private const int ScActionNone = 0;
        private const int ScActionRestart = 1;
        private const int ScActionReboot = 2;
        private const int ScActionRunCommand = 3;
        private const int MaximumFailureActionCount = 1024;

        [StructLayout(LayoutKind.Sequential)]
        private struct ServiceFailureActionsNative
        {
            internal uint ResetPeriod;
            internal IntPtr RebootMessage;
            internal IntPtr Command;
            internal uint ActionCount;
            internal IntPtr Actions;
        }

        [StructLayout(LayoutKind.Sequential)]
        private struct ServiceFailureActionsFlagNative
        {
            internal int Enabled;
        }

        [StructLayout(LayoutKind.Sequential)]
        private struct ServiceActionNative
        {
            internal int Type;
            internal uint Delay;
        }

        [DllImport("advapi32.dll", CharSet = CharSet.Unicode, SetLastError = true)]
        private static extern IntPtr OpenSCManagerW(
            string machineName, string databaseName, uint desiredAccess);

        [DllImport("advapi32.dll", CharSet = CharSet.Unicode, SetLastError = true)]
        private static extern IntPtr OpenServiceW(
            IntPtr serviceManager, string serviceName, uint desiredAccess);

        [DllImport("advapi32.dll", SetLastError = true)]
        [return: MarshalAs(UnmanagedType.Bool)]
        private static extern bool QueryServiceConfig2W(
            IntPtr service, uint infoLevel, IntPtr buffer, uint bufferSize, out uint bytesNeeded);

        [DllImport("advapi32.dll", SetLastError = true)]
        [return: MarshalAs(UnmanagedType.Bool)]
        private static extern bool ChangeServiceConfig2W(
            IntPtr service, uint infoLevel, IntPtr info);

        [DllImport("advapi32.dll", SetLastError = true)]
        [return: MarshalAs(UnmanagedType.Bool)]
        private static extern bool CloseServiceHandle(IntPtr handle);

        public static ServiceFailureConfigurationSnapshot Capture(string serviceName)
        {
            IntPtr manager = IntPtr.Zero;
            IntPtr service = IntPtr.Zero;
            try
            {
                manager = OpenSCManagerW(null, null, ScManagerConnect);
                if (manager == IntPtr.Zero) ThrowLastError("OpenSCManagerW");
                service = OpenServiceW(manager, serviceName, ServiceQueryConfig);
                if (service == IntPtr.Zero) ThrowLastError("OpenServiceW");

                var snapshot = QueryFailureActions(service);
                var flag = Query<ServiceFailureActionsFlagNative>(service, ServiceConfigFailureActionsFlag);
                snapshot.FailureFlag = flag.Enabled != 0;
                return snapshot;
            }
            finally
            {
                if (service != IntPtr.Zero) CloseServiceHandle(service);
                if (manager != IntPtr.Zero) CloseServiceHandle(manager);
            }
        }

        public static void Restore(string serviceName, ServiceFailureConfigurationSnapshot snapshot)
        {
            if (snapshot == null) throw new ArgumentNullException(nameof(snapshot));
            var managedActions = snapshot.Actions ?? Array.Empty<ServiceFailureAction>();
            var requiredAccess = GetRequiredRestoreAccess(snapshot);
            IntPtr manager = IntPtr.Zero;
            IntPtr service = IntPtr.Zero;
            IntPtr actionsBuffer = IntPtr.Zero;
            IntPtr actionsArray = IntPtr.Zero;
            IntPtr rebootMessage = IntPtr.Zero;
            IntPtr command = IntPtr.Zero;
            IntPtr flagBuffer = IntPtr.Zero;
            try
            {
                manager = OpenSCManagerW(null, null, ScManagerConnect);
                if (manager == IntPtr.Zero) ThrowLastError("OpenSCManagerW");
                service = OpenServiceW(manager, serviceName, requiredAccess);
                if (service == IntPtr.Zero) ThrowLastError("OpenServiceW");

                int actionSize = Marshal.SizeOf<ServiceActionNative>();
                int actionBufferElementCount = Math.Max(managedActions.Length, 1);
                actionsArray = Marshal.AllocHGlobal(checked(actionSize * actionBufferElementCount));
                if (managedActions.Length == 0)
                {
                    // SERVICE_FAILURE_ACTIONS uses a non-null pointer with cActions == 0
                    // to delete an existing reset period/action list. A null pointer means
                    // "leave unchanged", which is not a faithful rollback of an empty list.
                    Marshal.StructureToPtr(new ServiceActionNative(), actionsArray, false);
                }
                else
                {
                    for (int index = 0; index < managedActions.Length; index++)
                    {
                        var action = managedActions[index];
                        var native = new ServiceActionNative
                        {
                            Type = action.Type,
                            Delay = action.DelayMilliseconds,
                        };
                        Marshal.StructureToPtr(native, IntPtr.Add(actionsArray, index * actionSize), false);
                    }
                }
                // Empty strings delete any values introduced after the snapshot. Passing
                // null would instead leave those values unchanged.
                rebootMessage = Marshal.StringToHGlobalUni(snapshot.RebootMessage ?? string.Empty);
                command = Marshal.StringToHGlobalUni(snapshot.Command ?? string.Empty);

                var actions = new ServiceFailureActionsNative
                {
                    ResetPeriod = snapshot.ResetPeriodSeconds,
                    RebootMessage = rebootMessage,
                    Command = command,
                    ActionCount = checked((uint)managedActions.Length),
                    Actions = actionsArray,
                };
                actionsBuffer = Marshal.AllocHGlobal(Marshal.SizeOf<ServiceFailureActionsNative>());
                Marshal.StructureToPtr(actions, actionsBuffer, false);
                if (!ChangeServiceConfig2W(service, ServiceConfigFailureActions, actionsBuffer))
                    ThrowLastError("ChangeServiceConfig2W(failure actions)");

                flagBuffer = Marshal.AllocHGlobal(Marshal.SizeOf<ServiceFailureActionsFlagNative>());
                Marshal.StructureToPtr(
                    new ServiceFailureActionsFlagNative { Enabled = snapshot.FailureFlag ? 1 : 0 },
                    flagBuffer,
                    false);
                if (!ChangeServiceConfig2W(service, ServiceConfigFailureActionsFlag, flagBuffer))
                    ThrowLastError("ChangeServiceConfig2W(failure flag)");
            }
            finally
            {
                if (flagBuffer != IntPtr.Zero) Marshal.FreeHGlobal(flagBuffer);
                if (actionsBuffer != IntPtr.Zero) Marshal.FreeHGlobal(actionsBuffer);
                if (command != IntPtr.Zero) Marshal.FreeHGlobal(command);
                if (rebootMessage != IntPtr.Zero) Marshal.FreeHGlobal(rebootMessage);
                if (actionsArray != IntPtr.Zero) Marshal.FreeHGlobal(actionsArray);
                if (service != IntPtr.Zero) CloseServiceHandle(service);
                if (manager != IntPtr.Zero) CloseServiceHandle(manager);
            }
        }

        private static uint GetRequiredRestoreAccess(
            ServiceFailureConfigurationSnapshot snapshot)
        {
            if (snapshot == null) throw new ArgumentNullException(nameof(snapshot));
            var actions = snapshot.Actions ?? Array.Empty<ServiceFailureAction>();
            if (actions.Length > MaximumFailureActionCount)
                throw new InvalidOperationException("The Service failure action count is unreasonable.");

            uint requiredAccess = ServiceChangeConfig;
            foreach (var action in actions)
            {
                if (action == null)
                    throw new InvalidOperationException("A failure action is null.");
                switch (action.Type)
                {
                    case ScActionNone:
                    case ScActionReboot:
                    case ScActionRunCommand:
                        break;
                    case ScActionRestart:
                        // ChangeServiceConfig2 requires SERVICE_START on the handle when
                        // any configured failure action restarts the service.
                        requiredAccess |= ServiceStart;
                        break;
                    default:
                        throw new InvalidOperationException(
                            $"Unsupported Service failure action type: {action.Type}.");
                }
            }
            return requiredAccess;
        }

        public static bool Equivalent(
            ServiceFailureConfigurationSnapshot left,
            ServiceFailureConfigurationSnapshot right)
        {
            if (left == null || right == null ||
                left.ResetPeriodSeconds != right.ResetPeriodSeconds ||
                !string.Equals(left.RebootMessage, right.RebootMessage, StringComparison.Ordinal) ||
                !string.Equals(left.Command, right.Command, StringComparison.Ordinal) ||
                left.FailureFlag != right.FailureFlag)
                return false;
            var leftActions = left.Actions ?? Array.Empty<ServiceFailureAction>();
            var rightActions = right.Actions ?? Array.Empty<ServiceFailureAction>();
            if (leftActions.Length != rightActions.Length) return false;
            for (int index = 0; index < leftActions.Length; index++)
            {
                if (leftActions[index] == null || rightActions[index] == null ||
                    leftActions[index].Type != rightActions[index].Type ||
                    leftActions[index].DelayMilliseconds != rightActions[index].DelayMilliseconds)
                    return false;
            }
            return true;
        }

        private static T Query<T>(IntPtr service, uint infoLevel) where T : struct
        {
            uint bytesNeeded;
            if (QueryServiceConfig2W(service, infoLevel, IntPtr.Zero, 0, out bytesNeeded) ||
                Marshal.GetLastWin32Error() != ErrorInsufficientBuffer || bytesNeeded == 0)
                ThrowLastError("QueryServiceConfig2W(size)");
            IntPtr buffer = Marshal.AllocHGlobal(checked((int)bytesNeeded));
            try
            {
                if (!QueryServiceConfig2W(service, infoLevel, buffer, bytesNeeded, out bytesNeeded))
                    ThrowLastError("QueryServiceConfig2W(data)");
                return Marshal.PtrToStructure<T>(buffer);
            }
            finally
            {
                Marshal.FreeHGlobal(buffer);
            }
        }

        private static ServiceFailureConfigurationSnapshot QueryFailureActions(IntPtr service)
        {
            uint bytesNeeded;
            if (QueryServiceConfig2W(
                    service,
                    ServiceConfigFailureActions,
                    IntPtr.Zero,
                    0,
                    out bytesNeeded) ||
                Marshal.GetLastWin32Error() != ErrorInsufficientBuffer || bytesNeeded == 0)
                ThrowLastError("QueryServiceConfig2W(failure actions size)");
            IntPtr buffer = Marshal.AllocHGlobal(checked((int)bytesNeeded));
            try
            {
                if (!QueryServiceConfig2W(
                        service,
                        ServiceConfigFailureActions,
                        buffer,
                        bytesNeeded,
                        out bytesNeeded))
                    ThrowLastError("QueryServiceConfig2W(failure actions data)");
                var actions = Marshal.PtrToStructure<ServiceFailureActionsNative>(buffer);
                if (actions.ActionCount > MaximumFailureActionCount)
                    throw new InvalidOperationException("The Service failure action count is unreasonable.");
                var managedActions = new ServiceFailureAction[actions.ActionCount];
                int actionSize = Marshal.SizeOf<ServiceActionNative>();
                for (int index = 0; index < managedActions.Length; index++)
                {
                    var native = Marshal.PtrToStructure<ServiceActionNative>(
                        IntPtr.Add(actions.Actions, checked(index * actionSize)));
                    managedActions[index] = new ServiceFailureAction
                    {
                        Type = native.Type,
                        DelayMilliseconds = native.Delay,
                    };
                }
                return new ServiceFailureConfigurationSnapshot
                {
                    ResetPeriodSeconds = actions.ResetPeriod,
                    RebootMessage = Marshal.PtrToStringUni(actions.RebootMessage) ?? string.Empty,
                    Command = Marshal.PtrToStringUni(actions.Command) ?? string.Empty,
                    Actions = managedActions,
                };
            }
            finally
            {
                Marshal.FreeHGlobal(buffer);
            }
        }

        private static void ThrowLastError(string operation)
        {
            throw new Win32Exception(Marshal.GetLastWin32Error(), operation);
        }
    }
}
'@
}

function Get-ServiceFailureConfigurationSnapshot {
    Initialize-ServiceFailureConfigurationInterop
    return [Muhun.Mcsv.Installer.ServiceFailureConfiguration]::Capture($serviceName)
}

function Restore-ServiceFailureConfigurationSnapshot {
    param([Parameter(Mandatory = $true)]$Snapshot)
    Initialize-ServiceFailureConfigurationInterop
    [Muhun.Mcsv.Installer.ServiceFailureConfiguration]::Restore($serviceName, $Snapshot)
    $restored = [Muhun.Mcsv.Installer.ServiceFailureConfiguration]::Capture($serviceName)
    if (-not [Muhun.Mcsv.Installer.ServiceFailureConfiguration]::Equivalent(
            $Snapshot,
            $restored)) {
        throw 'Service failure actions/failure flag 無法完整回復。'
    }
}

function Get-ServiceDelayedAutoStart {
    $serviceRegistryPath = "HKLM:\SYSTEM\CurrentControlSet\Services\$serviceName"
    $delayed = Get-ItemPropertyValue `
        -LiteralPath $serviceRegistryPath `
        -Name 'DelayedAutoStart' `
        -ErrorAction SilentlyContinue
    return $null -ne $delayed -and [int]$delayed -eq 1
}

function Get-ServiceStartArgument {
    param([Parameter(Mandatory = $true)][string]$StartMode)

    switch ($StartMode) {
        'Auto' { return $(if (Get-ServiceDelayedAutoStart) { 'delayed-auto' } else { 'auto' }) }
        'Manual' { return 'demand' }
        'Disabled' { return 'disabled' }
        default { throw "現有 Service start mode 無效：$StartMode" }
    }
}

function New-ServiceRollbackSnapshot {
    param(
        [Parameter(Mandatory = $true)]$Definition,
        [Parameter(Mandatory = $true)][string]$SecurityDescriptor,
        [Parameter(Mandatory = $true)][string]$SidType,
        [Parameter(Mandatory = $true)][bool]$WasRunning,
        [Parameter(Mandatory = $true)][int]$RestPort
    )

    foreach ($requiredValue in @(
        [string]$Definition.PathName,
        [string]$Definition.DisplayName,
        [string]$Definition.StartName,
        $SecurityDescriptor,
        $SidType)) {
        if ([string]::IsNullOrWhiteSpace($requiredValue) -or $requiredValue -match '[\r\n]') {
            throw '現有 Service 回復資料遺失或含有控制字元。'
        }
    }
    return [pscustomobject]@{
        BinaryPath = [string]$Definition.PathName
        DisplayName = [string]$Definition.DisplayName
        Description = if ($null -eq $Definition.Description) { '' } else { [string]$Definition.Description }
        StartArgument = Get-ServiceStartArgument ([string]$Definition.StartMode)
        Account = [string]$Definition.StartName
        FailureConfiguration = Get-ServiceFailureConfigurationSnapshot
        SecurityDescriptor = $SecurityDescriptor
        SidType = $SidType
        WasRunning = $WasRunning
        RestPort = $RestPort
    }
}

function Restore-ServiceRollbackSnapshot {
    param([Parameter(Mandatory = $true)][pscustomobject]$Snapshot)

    Invoke-Sc config $serviceName `
        'binPath=' $Snapshot.BinaryPath `
        'start=' $Snapshot.StartArgument `
        'DisplayName=' $Snapshot.DisplayName `
        'obj=' $Snapshot.Account | Out-Null
    Invoke-Sc description $serviceName $Snapshot.Description | Out-Null
    Restore-ServiceFailureConfigurationSnapshot $Snapshot.FailureConfiguration
    Invoke-Sc sdset $serviceName $Snapshot.SecurityDescriptor | Out-Null
    if ((Get-ServiceSecurityDescriptor) -cne $Snapshot.SecurityDescriptor) {
        throw 'Service DACL 無法完整回復。'
    }
    Invoke-Sc sidtype $serviceName $Snapshot.SidType | Out-Null
    if ((Get-ServiceSidType) -cne $Snapshot.SidType) {
        throw 'Service SID type 無法完整回復。'
    }
}

function Get-ServiceSecurityDescriptor {
    $output = & "$env:SystemRoot\System32\sc.exe" sdshow $serviceName 2>&1
    if ($LASTEXITCODE -ne 0) {
        throw "無法讀取 Muhun MCSV Service DACL：$($output -join ' ')"
    }
    $sddl = (@($output) | ForEach-Object { [string]$_ } |
        Where-Object { $_ -match '^D:' } | Select-Object -First 1).Trim()
    if ([string]::IsNullOrWhiteSpace($sddl) -or $sddl.Length -gt 8192) {
        throw 'Muhun MCSV Service DACL 回應遺失或過大。'
    }
    return $sddl
}

function Grant-ServiceSelfUpdateRights {
    param([string]$ServiceSid)
    if ($ServiceSid -notmatch '^S-1-5-80-(?:[0-9]+-){4}[0-9]+$') {
        throw 'Muhun MCSV Service SID 格式無效。'
    }
    $current = Get-ServiceSecurityDescriptor
    $ace = "(A;;CCDCLCRPWP;;;$ServiceSid)"
    if ($current.Contains($ace, [StringComparison]::Ordinal)) {
        return $current
    }
    $saclIndex = $current.IndexOf('S:', [StringComparison]::Ordinal)
    $updated = if ($saclIndex -ge 0) {
        $current.Insert($saclIndex, $ace)
    } else {
        $current + $ace
    }
    if ($updated.Length -gt 8192) {
        throw '更新後的 Muhun MCSV Service DACL 過大。'
    }
    $output = & "$env:SystemRoot\System32\sc.exe" sdset $serviceName $updated 2>&1
    if ($LASTEXITCODE -ne 0) {
        throw "無法授予簽署 Updater 精確的 Service 自我更新權限：$($output -join ' ')"
    }
    $committed = Get-ServiceSecurityDescriptor
    if (-not $committed.Contains($ace, [StringComparison]::Ordinal)) {
        throw 'Service DACL 寫入後未保留精確的自我更新 ACE。'
    }
    return $current
}

function Get-ServiceSidType {
    $output = & "$env:SystemRoot\System32\sc.exe" qsidtype $serviceName 2>&1
    if ($LASTEXITCODE -ne 0) {
        throw "無法讀取 Muhun MCSV Service SID type：$($output -join ' ')"
    }
    $text = $output -join [Environment]::NewLine
    if ($text -notmatch 'SERVICE_SID_TYPE\s*:\s*(NONE|UNRESTRICTED|RESTRICTED)') {
        throw 'Muhun MCSV Service SID type 回應無效。'
    }
    return $Matches[1].ToLowerInvariant()
}

function Read-SafeAsciiFile {
    param([string]$Path, [int]$MinimumBytes, [int]$MaximumBytes, [string]$Label)
    Assert-NoExistingReparsePoints $Path $Label
    if (-not (Test-Path -LiteralPath $Path -PathType Leaf)) {
        throw "$Label 尚未建立。"
    }
    $item = Get-Item -LiteralPath $Path -Force
    if (($item.Attributes -band [IO.FileAttributes]::ReparsePoint) -ne 0 -or
        $item.Length -lt $MinimumBytes -or $item.Length -gt $MaximumBytes) {
        throw "$Label 的檔案類型或大小無效。"
    }
    $bytes = [IO.File]::ReadAllBytes($Path)
    if (@($bytes | Where-Object { $_ -gt 0x7f }).Count -ne 0) {
        throw "$Label 不是有效 ASCII。"
    }
    return [Text.Encoding]::ASCII.GetString($bytes).Trim()
}

function Get-ServiceRestPort {
    param([string]$ServiceExecutable)
    Assert-NoExistingReparsePoints $ServiceExecutable 'Service executable'
    $settingsPath = Join-Path ([IO.Path]::GetDirectoryName($ServiceExecutable)) 'appsettings.json'
    if (-not (Test-Path -LiteralPath $settingsPath -PathType Leaf)) {
        return 39050
    }
    $item = Get-Item -LiteralPath $settingsPath -Force
    if (($item.Attributes -band [IO.FileAttributes]::ReparsePoint) -ne 0 -or
        $item.Length -lt 2 -or $item.Length -gt 64KB) {
        throw 'Service appsettings.json 的檔案類型或大小無效。'
    }
    $settings = Get-Content -LiteralPath $settingsPath -Raw | ConvertFrom-Json
    $port = if ($null -ne $settings.Mcsv -and $null -ne $settings.Mcsv.Service -and
        $null -ne $settings.Mcsv.Service.Port) { [int]$settings.Mcsv.Service.Port } else { 39050 }
    if ($port -lt 1024 -or $port -gt 65535) {
        throw 'Service REST Port 超出允許範圍。'
    }
    return $port
}

function Read-ProductActivationReadyResponse {
    param([string]$Content)
    if ([string]::IsNullOrWhiteSpace($Content) -or
        [Text.Encoding]::UTF8.GetByteCount($Content) -gt 16KB) {
        throw 'activation-ready JSON 內容為空或超出大小限制。'
    }

    $document = $null
    try {
        $document = [Text.Json.JsonDocument]::Parse($Content)
        $root = $document.RootElement
        if ($root.ValueKind -ne [Text.Json.JsonValueKind]::Object) {
            throw 'activation-ready JSON 根節點不是物件。'
        }

        $properties = @($root.EnumerateObject())
        $actualProperties = @($properties | ForEach-Object { $_.Name } | Sort-Object)
        $expectedProperties = @('installationId', 'product', 'ready', 'startedAtUtc', 'status', 'version') |
            Sort-Object
        if (($actualProperties -join '|') -cne ($expectedProperties -join '|')) {
            throw 'activation-ready JSON 欄位不完整、重複或含有未預期欄位。'
        }

        $statusElement = $root.GetProperty('status')
        $productElement = $root.GetProperty('product')
        $versionElement = $root.GetProperty('version')
        $identityElement = $root.GetProperty('installationId')
        $startedAtElement = $root.GetProperty('startedAtUtc')
        $readyElement = $root.GetProperty('ready')
        if ($statusElement.ValueKind -ne [Text.Json.JsonValueKind]::String -or
            $productElement.ValueKind -ne [Text.Json.JsonValueKind]::String -or
            $versionElement.ValueKind -ne [Text.Json.JsonValueKind]::String -or
            $identityElement.ValueKind -ne [Text.Json.JsonValueKind]::String -or
            $startedAtElement.ValueKind -ne [Text.Json.JsonValueKind]::String -or
            $readyElement.ValueKind -ne [Text.Json.JsonValueKind]::True) {
            throw 'activation-ready JSON 欄位型別無效。'
        }

        $installationId = [Guid]::Empty
        if (-not [Guid]::TryParseExact($identityElement.GetString(), 'D', [ref]$installationId) -or
            $installationId -eq [Guid]::Empty) {
            throw 'activation-ready installationId 格式無效。'
        }

        # ConvertFrom-Json in PowerShell 7.4+ automatically converts ISO timestamps to a local
        # DateTime.  On a non-UTC machine, converting that value back to text loses the original
        # +00:00 marker and makes a valid UTC response fail closed.  Parse the raw JSON string so
        # the signed installer still verifies that the Service explicitly emitted UTC.
        $startedAtText = $startedAtElement.GetString()
        $startedAtUtc = [DateTimeOffset]::MinValue
        if ($startedAtText -notmatch '(?:Z|\+00:00)\z' -or
            -not [DateTimeOffset]::TryParse(
                $startedAtText,
                [Globalization.CultureInfo]::InvariantCulture,
                [Globalization.DateTimeStyles]::RoundtripKind,
                [ref]$startedAtUtc) -or
            $startedAtUtc.Offset -ne [TimeSpan]::Zero) {
            throw 'activation-ready startedAtUtc 必須是有效且明確標示的 UTC 時間。'
        }

        return [pscustomobject]@{
            status = $statusElement.GetString()
            product = $productElement.GetString()
            version = $versionElement.GetString()
            installationId = $installationId
            startedAtUtc = $startedAtUtc
            ready = $readyElement.GetBoolean()
        }
    } catch [Text.Json.JsonException] {
        throw "activation-ready JSON 無效：$($_.Exception.Message)"
    } finally {
        if ($null -ne $document) {
            $document.Dispose()
        }
    }
}

function Wait-ProductActivationReady {
    param(
        [string]$DataRoot,
        [int]$Port,
        [string]$ExpectedVersion,
        [Nullable[Guid]]$ExpectedInstallationId,
        [int]$TimeoutSeconds = 90
    )
    if ($ExpectedVersion -notmatch '^(0|[1-9][0-9]*)\.(0|[1-9][0-9]*)\.(0|[1-9][0-9]*)(?:-[0-9A-Za-z-]+(?:\.[0-9A-Za-z-]+)*)?$' -or
        $Port -lt 1024 -or $Port -gt 65535 -or
        $TimeoutSeconds -lt 10 -or $TimeoutSeconds -gt 300) {
        throw 'Service activation-ready 輪詢參數無效。'
    }
    $tokenPath = Join-Path $DataRoot 'secrets\service-rest-token.v1'
    $identityPath = Join-Path $DataRoot 'data\installation-id.v1'
    $deadline = [DateTimeOffset]::UtcNow.AddSeconds($TimeoutSeconds)
    $lastError = 'Service 尚未回應。'
    while ([DateTimeOffset]::UtcNow -lt $deadline) {
        try {
            $service = Get-Service -Name $serviceName -ErrorAction Stop
            if ($service.Status -ne 'Running') {
                throw "SCM 狀態為 $($service.Status)。"
            }
            $token = Read-SafeAsciiFile $tokenPath 64 128 'Service REST token'
            if ($token -notmatch '^[A-Fa-f0-9]{64}$') {
                throw 'Service REST token 格式無效。'
            }
            $identityText = Read-SafeAsciiFile $identityPath 36 128 'installation identity'
            $installationId = [Guid]::Empty
            if (-not [Guid]::TryParseExact($identityText, 'D', [ref]$installationId) -or
                $installationId -eq [Guid]::Empty) {
                throw 'installation identity 格式無效。'
            }
            if ($null -ne $ExpectedInstallationId -and
                $installationId -ne [Guid]$ExpectedInstallationId) {
                throw 'installation identity 與回復點不一致。'
            }
            $response = Invoke-WebRequest `
                -Uri "http://127.0.0.1:$Port/api/v1/system/activation-ready" `
                -Method Get `
                -Headers @{ 'X-MCSV-Service-Token' = $token; 'Cache-Control' = 'no-store' } `
                -MaximumRedirection 0 `
                -TimeoutSec 3 `
                -SkipHttpErrorCheck
            if ($response.StatusCode -ne 200 -or
                $response.RawContentLength -lt 2 -or $response.RawContentLength -gt 16KB) {
                throw "activation-ready HTTP 狀態/大小無效：$($response.StatusCode)"
            }
            $ready = Read-ProductActivationReadyResponse $response.Content
            if ($ready.status -cne 'ready' -or $ready.product -cne 'Muhun MCSV Manager' -or
                $ready.version -cne $ExpectedVersion -or $ready.ready -ne $true -or
                $ready.installationId -ne $installationId -or
                $ready.startedAtUtc -gt [DateTimeOffset]::UtcNow.AddMinutes(1)) {
                throw 'activation-ready 回應未精確符合版本、安裝識別或 ready 狀態。'
            }
            return $installationId
        } catch {
            $lastError = $_.Exception.Message
        }
        Start-Sleep -Milliseconds 250
    }
    throw "Service 未在期限內通過受認證 activation-ready 驗證：$lastError"
}

function New-ProductShortcut {
    param([string]$Path, [string]$TargetPath, [string]$Arguments, [int]$WindowStyle = 1)
    $parent = [IO.Path]::GetDirectoryName($Path)
    [IO.Directory]::CreateDirectory($parent) | Out-Null
    $shell = New-Object -ComObject WScript.Shell
    $shortcut = $null
    try {
        $shortcut = $shell.CreateShortcut($Path)
        $shortcut.TargetPath = $TargetPath
        $shortcut.Arguments = $Arguments
        $shortcut.WorkingDirectory = [IO.Path]::GetDirectoryName($TargetPath)
        $shortcut.WindowStyle = $WindowStyle
        $shortcut.Description = 'X MCSV stable A/B launcher'
        $shortcut.Save()
    } finally {
        if ($null -ne $shortcut) { [Runtime.InteropServices.Marshal]::FinalReleaseComObject($shortcut) | Out-Null }
        [Runtime.InteropServices.Marshal]::FinalReleaseComObject($shell) | Out-Null
    }
    if (-not (Test-Path -LiteralPath $Path -PathType Leaf)) {
        throw "無法建立穩定 GUI 快捷方式：$Path"
    }
}

function Assert-TrustedPowerShellHost {
    $powerShellPath = [IO.Path]::GetFullPath([Environment]::ProcessPath)
    if ([IO.Path]::GetFileName($powerShellPath) -cne 'pwsh.exe' -or
        $PSVersionTable.PSVersion -lt [Version]'7.4' -or
        -not (Test-Path -LiteralPath $powerShellPath -PathType Leaf)) {
        throw 'Apps & Features 解除安裝入口必須由目前可信的 PowerShell 7.4+ pwsh.exe 建立。'
    }
    Assert-NoExistingReparsePoints $powerShellPath 'PowerShell 7 host'
    $trustedHostRoots = @($env:ProgramFiles, ${env:ProgramW6432}) |
        Where-Object { -not [string]::IsNullOrWhiteSpace($_) } |
        ForEach-Object { [IO.Path]::GetFullPath($_).TrimEnd('\', '/') } |
        Select-Object -Unique
    if (@($trustedHostRoots | Where-Object {
        Test-IsUnderRoot $powerShellPath $_
    }).Count -eq 0) {
        throw 'PowerShell 7 host 不在受保護的 Program Files 目錄內。'
    }
    $hostSignature = Get-AuthenticodeSignature -LiteralPath $powerShellPath
    if ($hostSignature.Status -ne [Management.Automation.SignatureStatus]::Valid -or
        $null -eq $hostSignature.SignerCertificate) {
        throw 'PowerShell 7 host 未通過 Windows Authenticode 驗證。'
    }

    $privilegedWriterSids = @(
        'S-1-5-18',
        'S-1-5-32-544',
        'S-1-5-80-956008885-3418522649-1831038044-1853292631-2271478464'
    )
    $writeMask =
        [Security.AccessControl.FileSystemRights]::Write -bor
        [Security.AccessControl.FileSystemRights]::Modify -bor
        [Security.AccessControl.FileSystemRights]::FullControl -bor
        [Security.AccessControl.FileSystemRights]::Delete -bor
        [Security.AccessControl.FileSystemRights]::DeleteSubdirectoriesAndFiles -bor
        [Security.AccessControl.FileSystemRights]::ChangePermissions -bor
        [Security.AccessControl.FileSystemRights]::TakeOwnership
    $hostAcl = Get-Acl -LiteralPath $powerShellPath -ErrorAction Stop
    foreach ($rule in @($hostAcl.Access)) {
        if ($rule.AccessControlType -ne [Security.AccessControl.AccessControlType]::Allow -or
            (([int64]$rule.FileSystemRights -band [int64]$writeMask) -eq 0)) {
            continue
        }
        try {
            $ruleSid = $rule.IdentityReference.Translate(
                [Security.Principal.SecurityIdentifier]).Value
        } catch {
            throw '無法驗證 PowerShell 7 host 的 ACL 身分。'
        }
        if ($ruleSid -notin $privilegedWriterSids) {
            throw 'PowerShell 7 host 可由非系統身分修改，拒絕建立系統解除安裝入口。'
        }
    }

    return $powerShellPath
}

function Get-ArpRegistrationSnapshot {
    param([Parameter(Mandatory = $true)][string]$InstallRoot)

    $baseKey = [Microsoft.Win32.RegistryKey]::OpenBaseKey(
        [Microsoft.Win32.RegistryHive]::LocalMachine,
        [Microsoft.Win32.RegistryView]::Registry64)
    try {
        $key = $baseKey.OpenSubKey($arpRegistrySubKey, $false)
        if ($null -eq $key) {
            return [pscustomobject]@{ Existed = $false; Values = @() }
        }
        try {
            if ($key.SubKeyCount -ne 0 -or
                [string]$key.GetValue('ProductId', $null,
                    [Microsoft.Win32.RegistryValueOptions]::DoNotExpandEnvironmentNames) -cne $productId -or
                [string]$key.GetValue('PublisherCertificateSha256', $null,
                    [Microsoft.Win32.RegistryValueOptions]::DoNotExpandEnvironmentNames) -cne
                    $expectedPublisherCertificateSha256) {
                throw '現有 Apps & Features 登錄鍵不是 X MCSV 擁有，拒絕覆寫。'
            }
            $registeredInstallRoot = [string]$key.GetValue(
                'InstallLocation',
                $null,
                [Microsoft.Win32.RegistryValueOptions]::DoNotExpandEnvironmentNames)
            if ([string]::IsNullOrWhiteSpace($registeredInstallRoot) -or
                -not [string]::Equals(
                    [IO.Path]::GetFullPath($registeredInstallRoot).TrimEnd('\', '/'),
                    [IO.Path]::GetFullPath($InstallRoot).TrimEnd('\', '/'),
                    [StringComparison]::OrdinalIgnoreCase)) {
                throw '現有 X MCSV Apps & Features 項目屬於不同安裝目錄。'
            }

            $values = [Collections.Generic.List[object]]::new()
            foreach ($valueName in @($key.GetValueNames())) {
                $values.Add([pscustomobject]@{
                    Name = $valueName
                    Kind = $key.GetValueKind($valueName)
                    Value = $key.GetValue(
                        $valueName,
                        $null,
                        [Microsoft.Win32.RegistryValueOptions]::DoNotExpandEnvironmentNames)
                })
            }
            return [pscustomobject]@{ Existed = $true; Values = @($values) }
        } finally {
            $key.Dispose()
        }
    } finally {
        $baseKey.Dispose()
    }
}

function Set-ArpRegistrationTransactionally {
    param(
        [Parameter(Mandatory = $true)][pscustomobject]$Snapshot,
        [Parameter(Mandatory = $true)][pscustomobject]$Mutation,
        [Parameter(Mandatory = $true)][string]$Version,
        [Parameter(Mandatory = $true)][string]$InstallRoot,
        [Parameter(Mandatory = $true)][string]$DataRoot,
        [Parameter(Mandatory = $true)][string]$UninstallerPath,
        [Parameter(Mandatory = $true)][string]$PowerShellPath
    )

    Assert-NoExistingReparsePoints $UninstallerPath 'installed X MCSV uninstaller'
    if (-not (Test-Path -LiteralPath $UninstallerPath -PathType Leaf)) {
        throw '安裝版本缺少可信解除安裝器。'
    }
    $uninstallerSignature = Get-AuthenticodeSignature -LiteralPath $UninstallerPath
    if ($uninstallerSignature.Status -ne [Management.Automation.SignatureStatus]::Valid -or
        $null -eq $uninstallerSignature.SignerCertificate -or
        $null -eq $uninstallerSignature.TimeStamperCertificate -or
        (Get-CertificateSha256 $uninstallerSignature.SignerCertificate) -cne
            $expectedPublisherCertificateSha256) {
        throw '安裝版本的解除安裝器未通過固定正式發布者 Authenticode 與時間戳驗證。'
    }
    foreach ($commandPath in @($PowerShellPath, $UninstallerPath, $InstallRoot, $DataRoot)) {
        if ($commandPath.IndexOf('"') -ge 0 -or $commandPath.IndexOf("`r") -ge 0 -or
            $commandPath.IndexOf("`n") -ge 0) {
            throw '解除安裝命令含有不安全的路徑字元。'
        }
    }
    $uninstallString = '"' + $PowerShellPath +
        '" -NoProfile -ExecutionPolicy AllSigned -File "' +
        $UninstallerPath + '" -InstallRoot "' + $InstallRoot + '" -DataRoot "' + $DataRoot + '"'

    $baseKey = [Microsoft.Win32.RegistryKey]::OpenBaseKey(
        [Microsoft.Win32.RegistryHive]::LocalMachine,
        [Microsoft.Win32.RegistryView]::Registry64)
    $key = $null
    try {
        if ($Snapshot.Existed) {
            $key = $baseKey.OpenSubKey($arpRegistrySubKey, $true)
        } else {
            $unexpectedKey = $baseKey.OpenSubKey($arpRegistrySubKey, $false)
            if ($null -ne $unexpectedKey) {
                $unexpectedKey.Dispose()
                throw 'Apps & Features 登錄鍵在快照後意外出現，拒絕覆寫。'
            }
            $key = $baseKey.CreateSubKey(
                $arpRegistrySubKey,
                [Microsoft.Win32.RegistryKeyPermissionCheck]::ReadWriteSubTree)
            $Mutation.Created = $true
        }
        if ($null -eq $key) {
            throw '無法開啟或建立 X MCSV Apps & Features 登錄鍵。'
        }
        if ($Snapshot.Existed -and
            ($key.SubKeyCount -ne 0 -or
                [string]$key.GetValue('ProductId', $null,
                    [Microsoft.Win32.RegistryValueOptions]::DoNotExpandEnvironmentNames) -cne $productId -or
                [string]$key.GetValue('PublisherCertificateSha256', $null,
                    [Microsoft.Win32.RegistryValueOptions]::DoNotExpandEnvironmentNames) -cne
                    $expectedPublisherCertificateSha256)) {
            throw 'Apps & Features 登錄鍵在快照後失去 X MCSV 所有權標記。'
        }
        $Mutation.Touched = $true
        if ($Mutation.Created -and $key.SubKeyCount -ne 0) {
            throw '本次建立的 Apps & Features 登錄鍵含有非預期子鍵。'
        }
        $key.SetValue('ProductId', $productId, [Microsoft.Win32.RegistryValueKind]::String)
        $key.SetValue(
            'PublisherCertificateSha256',
            $expectedPublisherCertificateSha256,
            [Microsoft.Win32.RegistryValueKind]::String)
        $key.SetValue('DisplayName', 'X MCSV', [Microsoft.Win32.RegistryValueKind]::String)
        $key.SetValue('DisplayVersion', $Version, [Microsoft.Win32.RegistryValueKind]::String)
        $key.SetValue('Publisher', 'Muhun', [Microsoft.Win32.RegistryValueKind]::String)
        $key.SetValue('InstallLocation', $InstallRoot, [Microsoft.Win32.RegistryValueKind]::String)
        $key.SetValue('UninstallString', $uninstallString, [Microsoft.Win32.RegistryValueKind]::String)
        $key.SetValue('NoModify', 1, [Microsoft.Win32.RegistryValueKind]::DWord)
        $key.SetValue('NoRepair', 1, [Microsoft.Win32.RegistryValueKind]::DWord)
        $key.SetValue('WindowsInstaller', 0, [Microsoft.Win32.RegistryValueKind]::DWord)
        $key.Flush()
    } finally {
        if ($null -ne $key) { $key.Dispose() }
        $baseKey.Dispose()
    }
}

function Restore-ArpRegistrationTransaction {
    param(
        [Parameter(Mandatory = $true)][pscustomobject]$Snapshot,
        [Parameter(Mandatory = $true)][pscustomobject]$Mutation
    )
    if (-not $Mutation.Touched) { return }

    $baseKey = [Microsoft.Win32.RegistryKey]::OpenBaseKey(
        [Microsoft.Win32.RegistryHive]::LocalMachine,
        [Microsoft.Win32.RegistryView]::Registry64)
    try {
        $key = $baseKey.OpenSubKey($arpRegistrySubKey, $true)
        if ($null -eq $key) {
            if ($Snapshot.Existed) {
                throw 'X MCSV Apps & Features 舊登錄鍵已消失，無法回復。'
            }
            return
        }
        try {
            if ($Snapshot.Existed) {
                if ($key.SubKeyCount -ne 0 -or
                    [string]$key.GetValue('ProductId', $null,
                        [Microsoft.Win32.RegistryValueOptions]::DoNotExpandEnvironmentNames) -cne $productId -or
                    [string]$key.GetValue('PublisherCertificateSha256', $null,
                        [Microsoft.Win32.RegistryValueOptions]::DoNotExpandEnvironmentNames) -cne
                        $expectedPublisherCertificateSha256) {
                    throw 'Apps & Features 登錄鍵的所有權標記已變更，拒絕回復。'
                }
                foreach ($valueName in @($key.GetValueNames())) {
                    $key.DeleteValue($valueName, $false)
                }
                foreach ($value in @($Snapshot.Values)) {
                    $key.SetValue($value.Name, $value.Value, $value.Kind)
                }
                $key.Flush()
                return
            }
            if (-not $Mutation.Created -or $key.SubKeyCount -ne 0) {
                throw 'Apps & Features 登錄鍵不是本次安裝新建，拒絕刪除。'
            }
            $knownNewValues = @(
                'ProductId', 'PublisherCertificateSha256', 'DisplayName', 'DisplayVersion',
                'Publisher', 'InstallLocation', 'UninstallString',
                'NoModify', 'NoRepair', 'WindowsInstaller'
            )
            foreach ($valueName in @($key.GetValueNames())) {
                if ($valueName -notin $knownNewValues) {
                    throw '本次新建的 Apps & Features 登錄鍵出現非預期值，拒絕刪除。'
                }
            }
            $partialProductId = [string]$key.GetValue(
                'ProductId', $null,
                [Microsoft.Win32.RegistryValueOptions]::DoNotExpandEnvironmentNames)
            $partialPublisher = [string]$key.GetValue(
                'PublisherCertificateSha256', $null,
                [Microsoft.Win32.RegistryValueOptions]::DoNotExpandEnvironmentNames)
            if ((-not [string]::IsNullOrEmpty($partialProductId) -and
                    $partialProductId -cne $productId) -or
                (-not [string]::IsNullOrEmpty($partialPublisher) -and
                    $partialPublisher -cne $expectedPublisherCertificateSha256)) {
                throw '本次新建的 Apps & Features 登錄鍵含有不同所有權標記。'
            }
        } finally {
            $key.Dispose()
        }
        $baseKey.DeleteSubKey($arpRegistrySubKey, $false)
    } finally {
        $baseKey.Dispose()
    }
}

function Invoke-InteractiveGuiActivation {
    param([string]$LauncherPath, [string]$InstallRoot, [int]$TimeoutSeconds = 120)
    Assert-NoExistingReparsePoints $LauncherPath 'Stable GUI launcher'
    if ($TimeoutSeconds -lt 30 -or $TimeoutSeconds -gt 180) {
        throw 'GUI activation deadline is invalid.'
    }
    $arguments = '--activate-current --install-root "' + $InstallRoot.Replace('"', '') + '"'
    $process = Start-Process -FilePath $LauncherPath `
        -ArgumentList $arguments `
        -WorkingDirectory ([IO.Path]::GetDirectoryName($LauncherPath)) `
        -WindowStyle Hidden `
        -PassThru
    if ($null -eq $process) {
        throw '無法啟動穩定 GUI activation client。'
    }
    try {
        if (-not $process.WaitForExit($TimeoutSeconds * 1000)) {
            Stop-Process -Id $process.Id -Force -ErrorAction SilentlyContinue
            throw '互動使用者 GUI 未在期限內完成 Service-compatible readiness ACK。'
        }
        if ($process.ExitCode -ne 0) {
            throw "互動使用者 GUI activation client 拒絕啟用（ExitCode=$($process.ExitCode)）。"
        }
    } finally {
        $process.Dispose()
    }
}

function Start-GuiActivationBrokerThroughExplorer {
    param(
        [string]$BootstrapperPath,
        [string]$InstallRoot,
        [int]$TimeoutSeconds = 30
    )
    Assert-NoExistingReparsePoints $BootstrapperPath 'GUI broker bootstrapper'
    if ($TimeoutSeconds -lt 10 -or $TimeoutSeconds -gt 60) {
        throw 'GUI broker bootstrapper deadline is invalid.'
    }
    $arguments = '--start-gui-activation-broker --install-root "' +
        $InstallRoot.Replace('"', '') + '"'
    $process = Start-Process -FilePath $BootstrapperPath `
        -ArgumentList $arguments `
        -WorkingDirectory ([IO.Path]::GetDirectoryName($BootstrapperPath)) `
        -WindowStyle Hidden `
        -PassThru
    if ($null -eq $process) {
        throw '無法啟動 Explorer GUI broker bootstrapper。'
    }
    try {
        if (-not $process.WaitForExit($TimeoutSeconds * 1000)) {
            Stop-Process -Id $process.Id -Force -ErrorAction SilentlyContinue
            throw 'Explorer GUI broker bootstrapper 未在期限內完成。'
        }
        if ($process.ExitCode -ne 0) {
            throw "Explorer GUI broker bootstrapper 無法交付一般權限啟動（ExitCode=$($process.ExitCode)）。"
        }
    } finally {
        $process.Dispose()
    }
}

function Invoke-PostInstallGuiActivation {
    param(
        [string]$BootstrapperPath,
        [string]$LauncherPath,
        [string]$InstallRoot
    )
    try {
        Start-GuiActivationBrokerThroughExplorer `
            -BootstrapperPath $BootstrapperPath `
            -InstallRoot $InstallRoot `
            -TimeoutSeconds 30
        Invoke-InteractiveGuiActivation `
            -LauncherPath $LauncherPath `
            -InstallRoot $InstallRoot `
            -TimeoutSeconds 120
        return $true
    } catch {
        Write-Warning `
            (("Windows Service 已安裝並通過健康驗證，但桌面管理器無法自動開啟：{0} " +
              "請從開始功能表開啟『X MCSV』；下次登入也會自動啟動 broker。") -f
                $_.Exception.Message) `
            -WarningAction Continue
        return $false
    }
}

function Stop-ExactProductGui {
    param([string]$ExecutablePath)
    if (-not (Test-Path -LiteralPath $ExecutablePath -PathType Leaf)) {
        return
    }
    $expected = [IO.Path]::GetFullPath($ExecutablePath)
    foreach ($process in Get-Process -Name 'Muhun MCSV Manager' -ErrorAction SilentlyContinue) {
        try {
            if (-not [string]::Equals(
                    [IO.Path]::GetFullPath($process.Path),
                    $expected,
                    [StringComparison]::OrdinalIgnoreCase)) {
                continue
            }
            if (-not $process.HasExited -and $process.CloseMainWindow()) {
                [void]$process.WaitForExit(5000)
            }
            if (-not $process.HasExited) {
                Stop-Process -Id $process.Id -Force -ErrorAction Stop
            }
        } finally {
            $process.Dispose()
        }
    }
}

function Assert-RsaPssSignature {
    param([Security.Cryptography.RSA]$Rsa, [byte[]]$Content, [byte[]]$Signature, [string]$Label)
    if ($Rsa.KeySize -lt 3072 -or $Signature.Length -ne ($Rsa.KeySize / 8) -or
        -not $Rsa.VerifyData(
            $Content,
            $Signature,
            [Security.Cryptography.HashAlgorithmName]::SHA256,
            [Security.Cryptography.RSASignaturePadding]::Pss)) {
        throw "$Label 的 RSA-PSS 簽章無效。"
    }
}

function Assert-ReleasePayload {
    param([string]$Source)
    foreach ($name in @(
        $manifestName,
        'release-manifest.json.sig',
        $checksumName,
        'publisher.cer',
        'update-manifest.json',
        'update-manifest.json.sig')) {
        [void](Resolve-SafeSourceFile $Source $name)
    }

    $manifestPath = Join-Path $Source $manifestName
    $manifestBytes = [IO.File]::ReadAllBytes($manifestPath)
    if ($manifestBytes.Length -lt 1 -or $manifestBytes.Length -gt 1MB) {
        throw 'release-manifest.json 大小無效。'
    }
    $manifest = [Text.Encoding]::UTF8.GetString($manifestBytes) | ConvertFrom-Json
    if ($manifest.schemaVersion -ne 1 -or
        $manifest.productId -ne $productId -or
        $manifest.installable -ne $true -or
        $manifest.runtimeIdentifier -ne 'win-x64' -or
        $manifest.version -notmatch '^(0|[1-9][0-9]*)\.(0|[1-9][0-9]*)\.(0|[1-9][0-9]*)(?:-[0-9A-Za-z-]+(?:\.[0-9A-Za-z-]+)*)?$' -or
        $manifest.channel -notin @('stable', 'beta') -or
        $manifest.signatureAlgorithm -ne 'rsa-pss-sha256' -or
        $manifest.publisherTrustMode -notin @('self-signed-local', 'public-ca') -or
        $manifest.publisherCertificateSha256 -notmatch '^[a-f0-9]{64}$' -or
        $manifest.publisherCertificateSha256 -cne $expectedPublisherCertificateSha256 -or
        $manifest.serviceEntryPoint -ne 'service-win-x64/Muhun MCSV Service.exe' -or
        $manifest.entryPoint -ne 'gui-win-x64/Muhun MCSV Manager.exe' -or
        $manifest.updaterEntryPoint -ne 'updater-win-x64/Muhun MCSV Updater.exe') {
        throw '正式安裝 manifest 的產品、版本、平台或安全中繼資料無效。'
    }
    if ($manifest.version -match '(?i)(?:^|[.-])(preview|alpha)(?:[.-]|$)' -or
        ($manifest.channel -eq 'stable' -and ([string]$manifest.version).Contains('-'))) {
        throw '正式安裝拒絕 Preview/Alpha，且 stable 頻道必須使用最終版本。'
    }

    $publisherCertificate = [Security.Cryptography.X509Certificates.X509Certificate2]::new(
        [IO.File]::ReadAllBytes((Join-Path $Source 'publisher.cer')))
    try {
        $certificateSha256 = Get-CertificateSha256 $publisherCertificate
        $rsa = [Security.Cryptography.X509Certificates.RSACertificateExtensions]::GetRSAPublicKey(
            $publisherCertificate)
        try {
            if ($null -eq $rsa -or $rsa.KeySize -lt 3072 -or
                $certificateSha256 -ne $manifest.publisherCertificateSha256) {
                throw '發布者憑證的演算法、強度或指紋不符。'
            }
            Assert-RsaPssSignature $rsa $manifestBytes `
                ([IO.File]::ReadAllBytes((Join-Path $Source 'release-manifest.json.sig'))) `
                'release-manifest.json'

            $selfSignature = Get-AuthenticodeSignature -LiteralPath $PSCommandPath
            if ($selfSignature.Status -ne [Management.Automation.SignatureStatus]::Valid -or
                $null -eq $selfSignature.SignerCertificate -or
                $null -eq $selfSignature.TimeStamperCertificate -or
                (Get-CertificateSha256 $selfSignature.SignerCertificate) -ne $certificateSha256) {
                throw '安裝程式本身未通過相同發布者的可信 Authenticode 與時間戳驗證。'
            }

            $entries = @{}
            foreach ($entry in @($manifest.files)) {
                $relative = [string]$entry.path
                $key = $relative.ToLowerInvariant()
                if (-not (Test-SafeRelativePath $relative) -or
                    $entry.sizeBytes -lt 0 -or $entry.sizeBytes -gt 2GB -or
                    $entry.sha256 -notmatch '^[a-f0-9]{64}$' -or $entries.ContainsKey($key)) {
                    throw '正式安裝 manifest 含有無效或重複檔案。'
                }
                $entries[$key] = $entry
            }
            if ($entries.Count -lt 8 -or $entries.Count -gt 10000) {
                throw '正式安裝 manifest 檔案清單遺失或過大。'
            }

            $checksums = @{}
            foreach ($line in Get-Content -LiteralPath (Join-Path $Source $checksumName)) {
                if ($line -notmatch '^([a-f0-9]{64}) \*(.+)$' -or
                    -not (Test-SafeRelativePath $Matches[2])) {
                    throw 'SHA256SUMS.txt 格式無效。'
                }
                $key = $Matches[2].ToLowerInvariant()
                if ($checksums.ContainsKey($key)) {
                    throw 'SHA256SUMS.txt 含有重複檔案。'
                }
                $checksums[$key] = $Matches[1]
            }
            foreach ($entry in $entries.Values) {
                $path = Resolve-SafeSourceFile $Source $entry.path
                if ((Get-Item -LiteralPath $path).Length -ne $entry.sizeBytes -or
                    (Get-Sha256Hex $path) -ne $entry.sha256 -or
                    -not $checksums.ContainsKey($entry.path.ToLowerInvariant()) -or
                    $checksums[$entry.path.ToLowerInvariant()] -ne $entry.sha256) {
                    throw "正式安裝檔案的大小或雜湊驗證失敗：$($entry.path)"
                }
            }
            if ($checksums.Count -ne $entries.Count) {
                throw 'SHA256SUMS.txt 未與已簽署 manifest 完全一致。'
            }
            $excludedReleaseFiles = @(
                'release-manifest.json',
                'release-manifest.json.sig',
                'SHA256SUMS.txt'
            )
            $actualReleaseFiles = @(Get-ChildItem -LiteralPath $Source -Recurse -File -Force |
                ForEach-Object {
                    if (($_.Attributes -band [IO.FileAttributes]::ReparsePoint) -ne 0) {
                        throw "正式安裝來源含有 reparse-point 檔案：$($_.FullName)"
                    }
                    [IO.Path]::GetRelativePath($Source, $_.FullName).Replace('\', '/')
                } | Where-Object { $_ -notin $excludedReleaseFiles })
            if ($actualReleaseFiles.Count -ne $entries.Count -or
                @($actualReleaseFiles | Where-Object {
                    -not $entries.ContainsKey($_.ToLowerInvariant())
                }).Count -ne 0) {
                throw '正式安裝來源含有未簽署、遺失或非預期檔案。'
            }

            $requiredAuthenticode = @(
                'service-win-x64/Muhun MCSV Service.exe',
                'gui-win-x64/Muhun MCSV Manager.exe',
                'updater-win-x64/Muhun MCSV Updater.exe',
                'Install-MuhunMcsv.ps1',
                'Uninstall-MuhunMcsv.ps1',
                'Test-MuhunMcsvRelease.ps1',
                'tools/Uninstall-MuhunMcsv.ps1'
            )
            $listedAuthenticode = @($manifest.authenticodeFiles)
            if ($listedAuthenticode.Count -ne $requiredAuthenticode.Count -or
                @($requiredAuthenticode | Where-Object { $_ -notin $listedAuthenticode }).Count -ne 0) {
                throw '正式安裝 manifest 缺少必要的 Authenticode 檔案。'
            }
            foreach ($relative in $requiredAuthenticode) {
                $path = Resolve-SafeSourceFile $Source $relative
                if ($relative.EndsWith('.exe', [StringComparison]::OrdinalIgnoreCase)) {
                    Assert-FormalProductVersion $path $manifest.version $relative
                }
                $signature = Get-AuthenticodeSignature -LiteralPath $path
                if ($signature.Status -ne [Management.Automation.SignatureStatus]::Valid -or
                    $null -eq $signature.SignerCertificate -or
                    $null -eq $signature.TimeStamperCertificate -or
                    (Get-CertificateSha256 $signature.SignerCertificate) -ne $certificateSha256) {
                    throw "$relative 尚未通過相同發布者的可信 Authenticode 與時間戳驗證。"
                }
            }
            if ((Get-Sha256Hex (Resolve-SafeSourceFile $Source 'Uninstall-MuhunMcsv.ps1')) -cne
                (Get-Sha256Hex (Resolve-SafeSourceFile $Source 'tools/Uninstall-MuhunMcsv.ps1'))) {
                throw '發行根目錄與版本樹的解除安裝器不是相同的已簽署檔案。'
            }

            $updateManifestPath = Resolve-SafeSourceFile $Source $manifest.updateManifest.path
            $updateManifestSignaturePath = Resolve-SafeSourceFile $Source $manifest.updateManifest.signaturePath
            $updateManifestBytes = [IO.File]::ReadAllBytes($updateManifestPath)
            Assert-RsaPssSignature $rsa $updateManifestBytes `
                ([IO.File]::ReadAllBytes($updateManifestSignaturePath)) 'update-manifest.json'
            $updateManifest = [Text.Encoding]::UTF8.GetString($updateManifestBytes) | ConvertFrom-Json
            if ($updateManifest.schemaVersion -ne 1 -or
                $updateManifest.productId -ne 'muhun.mcsv.manager' -or
                $updateManifest.version -ne $manifest.version -or
                $updateManifest.runtimeIdentifier -ne 'win-x64' -or
                $updateManifest.keyId -ne $manifest.keyId -or
                $updateManifest.signatureAlgorithm -ne 'rsa-pss-sha256' -or
                $updateManifest.entryPoint -ne $manifest.entryPoint) {
                throw '更新 manifest 與正式安裝 manifest 不一致。'
            }

            $copyEntries = @{}
            foreach ($entry in @($updateManifest.files)) {
                $relative = [string]$entry.path
                $key = $relative.ToLowerInvariant()
                if (-not (Test-SafeRelativePath $relative) -or
                    $entry.sizeBytes -lt 0 -or $entry.sha256 -notmatch '^[a-fA-F0-9]{64}$' -or
                    $copyEntries.ContainsKey($key)) {
                    throw '更新 manifest 含有無效或重複檔案。'
                }
                $sourcePath = Resolve-SafeSourceFile $Source $relative
                if ((Get-Item -LiteralPath $sourcePath).Length -ne $entry.sizeBytes -or
                    (Get-Sha256Hex $sourcePath) -ne ([string]$entry.sha256).ToLowerInvariant()) {
                    throw "更新安裝檔案驗證失敗：$relative"
                }
                $copyEntries[$key] = $entry
            }
            foreach ($required in @(
                $manifest.serviceEntryPoint,
                $manifest.entryPoint,
                $manifest.updaterEntryPoint,
                $manifest.updatePublicKey.path)) {
                if (-not $copyEntries.ContainsKey(([string]$required).ToLowerInvariant())) {
                    throw "更新 manifest 缺少必要檔案：$required"
                }
            }

            if ($copyEntries.ContainsKey('installed-version.v1.json')) {
                throw '更新 manifest 不得包含由 updater 管理的 installed-version metadata。'
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
                if (-not $copyEntries.ContainsKey($requiredPackageFile)) {
                    throw "更新 manifest 缺少必要的 nested-layout 檔案：$requiredPackageFile"
                }
            }

            $installedMetadataPath = Resolve-SafeSourceFile $Source 'installed-version.v1.json'
            if ((Get-Item -LiteralPath $installedMetadataPath).Length -gt 16KB) {
                throw '初次安裝版本 metadata 超過大小限制。'
            }
            $installedMetadata = Get-Content -LiteralPath $installedMetadataPath -Raw | ConvertFrom-Json
            if ($installedMetadata.schemaVersion -ne 1 -or
                $installedMetadata.productId -ne 'muhun.mcsv.manager' -or
                $installedMetadata.version -ne $manifest.version -or
                $installedMetadata.entryPoint -ne $manifest.entryPoint) {
                throw '初次安裝版本 metadata 與已簽署 release 不一致。'
            }
        } finally {
            if ($null -ne $rsa) { $rsa.Dispose() }
        }
    } finally {
        $publisherCertificate.Dispose()
    }

    return [pscustomobject]@{
        Manifest = $manifest
        CopyEntries = @($copyEntries.Values)
    }
}

function Write-AtomicText {
    param([string]$Path, [string]$Value)
    Assert-NoExistingReparsePoints ([IO.Path]::GetDirectoryName($Path)) 'Atomic write directory'
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
            $bytes = [Text.UTF8Encoding]::new($false).GetBytes($Value + [Environment]::NewLine)
            $stream.Write($bytes, 0, $bytes.Length)
            $stream.Flush($true)
        } finally {
            $stream.Dispose()
        }
        [IO.File]::Move($temporaryPath, $Path, $true)
    } finally {
        if (Test-Path -LiteralPath $temporaryPath -PathType Leaf) {
            Remove-Item -LiteralPath $temporaryPath -Force
        }
    }
}

function Assert-ManagedRootOrEmpty {
    param([string]$Path, [string]$MarkerName)
    if (-not (Test-Path -LiteralPath $Path -PathType Container)) {
        return
    }
    $markerPath = Join-Path $Path $MarkerName
    if (Test-Path -LiteralPath $markerPath -PathType Leaf) {
        if ((Get-Content -LiteralPath $markerPath -Raw).Trim() -ne $expectedMarker) {
            throw "受管理目錄標記無效：$Path"
        }
        return
    }
    if (Get-ChildItem -LiteralPath $Path -Force | Select-Object -First 1) {
        throw "拒絕使用沒有 Muhun MCSV 管理標記的非空目錄：$Path"
    }
}

function Get-ExistingVersionPayloadState {
    param(
        [string]$VersionRoot,
        [string]$Source,
        [object[]]$CopyEntries)

    if (-not (Test-Path -LiteralPath $VersionRoot -PathType Container)) {
        if (Test-Path -LiteralPath $VersionRoot) {
            throw "既有版本路徑不是目錄：$VersionRoot"
        }
        return 'Missing'
    }

    Assert-NoExistingReparsePoints $VersionRoot '既有版本目錄'
    $items = @(Get-ChildItem -LiteralPath $VersionRoot -Recurse -Force -ErrorAction Stop)
    if (@($items | Where-Object {
        ($_.Attributes -band [IO.FileAttributes]::ReparsePoint) -ne 0
    }).Count -ne 0) {
        throw "既有版本目錄含有 reparse point，拒絕重用或隔離：$VersionRoot"
    }

    $expectedFiles = [Collections.Generic.Dictionary[string, object]]::new(
        [StringComparer]::OrdinalIgnoreCase)
    $expectedDirectories = [Collections.Generic.HashSet[string]]::new(
        [StringComparer]::OrdinalIgnoreCase)
    foreach ($entry in $CopyEntries) {
        $relative = [string]$entry.path
        if (-not (Test-SafeRelativePath $relative) -or $expectedFiles.ContainsKey($relative)) {
            throw '已驗證 release 含有不安全或重複的版本檔案。'
        }
        $expectedFiles.Add($relative, [pscustomobject]@{
            SizeBytes = [long]$entry.sizeBytes
            Sha256 = ([string]$entry.sha256).ToLowerInvariant()
        })
        $segments = $relative.Split('/')
        for ($segmentIndex = 1; $segmentIndex -lt $segments.Length; $segmentIndex++) {
            [void]$expectedDirectories.Add(($segments[0..($segmentIndex - 1)] -join '/'))
        }
    }

    $installedMetadataRelativePath = 'installed-version.v1.json'
    $installedMetadataSource = Resolve-SafeSourceFile $Source $installedMetadataRelativePath
    $installedMetadataExpectation = [pscustomobject]@{
        SizeBytes = (Get-Item -LiteralPath $installedMetadataSource -Force).Length
        Sha256 = Get-Sha256Hex $installedMetadataSource
    }
    if ($expectedFiles.ContainsKey($installedMetadataRelativePath)) {
        throw '已驗證 release 不得重複宣告 installed-version metadata。'
    }
    $expectedFiles.Add($installedMetadataRelativePath, $installedMetadataExpectation)

    $metadataPath = Join-Path $VersionRoot $installedMetadataRelativePath
    $metadataMatches = (Test-Path -LiteralPath $metadataPath -PathType Leaf) -and
        (Get-Item -LiteralPath $metadataPath -Force).Length -eq $installedMetadataExpectation.SizeBytes -and
        (Get-Sha256Hex $metadataPath) -eq $installedMetadataExpectation.Sha256

    $actualFiles = @($items | Where-Object { $_ -is [IO.FileInfo] })
    $actualDirectories = @($items | Where-Object { $_ -is [IO.DirectoryInfo] })
    if ($actualFiles.Count -ne $expectedFiles.Count -or
        $actualDirectories.Count -ne $expectedDirectories.Count) {
        return $(if ($metadataMatches) { 'RecognizedPartial' } else { 'Unrecognized' })
    }

    foreach ($directory in $actualDirectories) {
        $relative = [IO.Path]::GetRelativePath($VersionRoot, $directory.FullName).Replace('\', '/')
        if (-not (Test-SafeRelativePath $relative) -or
            -not $expectedDirectories.Contains($relative)) {
            return $(if ($metadataMatches) { 'RecognizedPartial' } else { 'Unrecognized' })
        }
    }

    foreach ($file in $actualFiles) {
        $relative = [IO.Path]::GetRelativePath($VersionRoot, $file.FullName).Replace('\', '/')
        if (-not (Test-SafeRelativePath $relative) -or -not $expectedFiles.ContainsKey($relative)) {
            return $(if ($metadataMatches) { 'RecognizedPartial' } else { 'Unrecognized' })
        }
        $expected = $expectedFiles[$relative]
        if ($file.Length -ne $expected.SizeBytes -or
            (Get-Sha256Hex $file.FullName) -ne $expected.Sha256) {
            return $(if ($metadataMatches) { 'RecognizedPartial' } else { 'Unrecognized' })
        }
    }
    return 'Exact'
}

function Move-ProvisionedVersionToQuarantine {
    param(
        [string]$VersionRoot,
        [string]$VersionsRoot,
        [string]$Version)

    if (-not (Test-Path -LiteralPath $VersionRoot -PathType Container)) {
        return $null
    }
    $normalizedVersionsRoot = [IO.Path]::GetFullPath($VersionsRoot).TrimEnd('\', '/')
    $normalizedVersionRoot = [IO.Path]::GetFullPath($VersionRoot).TrimEnd('\', '/')
    if (-not (Test-IsUnderRoot $normalizedVersionRoot $normalizedVersionsRoot) -or
        -not [string]::Equals(
            [IO.Path]::GetDirectoryName($normalizedVersionRoot),
            $normalizedVersionsRoot,
            [StringComparison]::OrdinalIgnoreCase)) {
        throw '拒絕隔離不在受管理 versions 根目錄正下方的版本目錄。'
    }
    Assert-NoExistingReparsePoints $normalizedVersionRoot '待隔離版本目錄'
    if (@(Get-ChildItem -LiteralPath $normalizedVersionRoot -Recurse -Force -ErrorAction Stop |
        Where-Object { ($_.Attributes -band [IO.FileAttributes]::ReparsePoint) -ne 0 }).Count -ne 0) {
        throw '待隔離版本目錄含有 reparse point。'
    }

    $safeVersion = $Version -replace '[^0-9A-Za-z.-]', '_'
    $quarantine = Join-Path $normalizedVersionsRoot `
        ".failed-install-$safeVersion-$([guid]::NewGuid().ToString('N'))"
    [IO.Directory]::Move($normalizedVersionRoot, $quarantine)
    return $quarantine
}

Assert-Administrator
Assert-LocalGroupDescriptionSupported
$source = Resolve-SafeLocalDirectory $SourceDirectory '安裝來源'
$install = Resolve-SafeLocalDirectory $InstallRoot '程式安裝目錄'
$data = Resolve-SafeLocalDirectory $DataRoot '資料目錄'
if (-not (Test-Path -LiteralPath $source -PathType Container)) {
    throw '找不到正式安裝來源目錄。'
}
Assert-NoExistingReparsePoints $source '安裝來源'
Assert-NoExistingReparsePoints $install '程式安裝目錄'
Assert-NoExistingReparsePoints $data '資料目錄'
if ((Test-IsUnderRoot $data $install) -or (Test-IsUnderRoot $install $data) -or
    (Test-IsUnderRoot $source $install) -or (Test-IsUnderRoot $install $source)) {
    throw '安裝來源、程式目錄與資料目錄不可互相包含。'
}
$scriptDirectory = [IO.Path]::GetFullPath((Split-Path -Parent $PSCommandPath)).TrimEnd('\', '/')
if (-not [string]::Equals($scriptDirectory, $source, [StringComparison]::OrdinalIgnoreCase)) {
    throw '必須執行正式發行目錄內已簽署的 Install-MuhunMcsv.ps1。'
}

$verifiedRelease = Assert-ReleasePayload $source
$manifest = $verifiedRelease.Manifest
$installExistedBefore = Test-Path -LiteralPath $install -PathType Container
$dataExistedBefore = Test-Path -LiteralPath $data -PathType Container
Assert-ManagedRootOrEmpty $install $installMarker
Assert-ManagedRootOrEmpty $data $dataMarker
$versionsRoot = Join-Path $install 'versions'
$versionRoot = Join-Path $versionsRoot $manifest.version
$stagingRoot = Join-Path $install ".staging-$([guid]::NewGuid().ToString('N'))"
$verifierStagingRoot = Join-Path $install ".verification-$([guid]::NewGuid().ToString('N'))"
if (-not (Test-IsUnderRoot $versionRoot $install) -or
    -not (Test-IsUnderRoot $stagingRoot $install) -or
    -not (Test-IsUnderRoot $verifierStagingRoot $install)) {
    throw '解析後的安裝目標超出程式安裝目錄。'
}

$serviceCreated = $false
$versionProvisioned = $false
$retryQuarantineRoot = $null
$startMenuShortcutCreated = $false
$startupShortcutCreated = $false
$serviceWasRunning = $false
$previousServiceSnapshot = $null
$previousServicePath = $null
$previousServiceExecutablePath = $null
$previousServicePort = $null
$previousServiceSddl = $null
$previousServiceSidType = $null
$previousActiveVersion = $null
$previousInstallationId = $null
$previousInstallerSidBinding = $null
$installerSidBindingExisted = $false
$installationApplied = $false
$postInstallBootstrapperPath = $null
$arpMutation = [pscustomobject]@{ Touched = $false; Created = $false }
$ownedParentDirectories = [Collections.Generic.Dictionary[string, object]]::new(
    [StringComparer]::OrdinalIgnoreCase)
$activePointerPath = Join-Path $install 'active-version.v1'
$activationStateRoot = Join-Path $install $activationStateDirectoryName
$stableLauncherRoot = Join-Path $install $stableLauncherDirectoryName
$stableLauncherPath = Join-Path $stableLauncherRoot $stableLauncherFileName
$stableLauncherMutation = [pscustomobject]@{
    Created = $false
    Replaced = $false
    BackupPath = $null
    PreviousSha256 = $null
}
$targetGuiExecutable = Join-Path $versionRoot 'gui-win-x64\Muhun MCSV Manager.exe'
$installedUninstallerPath = Join-Path $versionRoot $installedUninstallerRelativePath
$programsDirectory = [Environment]::GetFolderPath([Environment+SpecialFolder]::Programs)
$startupDirectory = [Environment]::GetFolderPath([Environment+SpecialFolder]::Startup)
if ([string]::IsNullOrWhiteSpace($programsDirectory) -or
    [string]::IsNullOrWhiteSpace($startupDirectory)) {
    throw '無法解析目前安裝帳號的 Start Menu/Startup 目錄。'
}
$startMenuShortcutPath = Join-Path $programsDirectory $startMenuShortcutName
$legacyStartMenuShortcutPath = Join-Path $programsDirectory $legacyStartMenuShortcutName
$startupShortcutPath = Join-Path $startupDirectory $startupShortcutName
$installerIdentity = [Security.Principal.WindowsIdentity]::GetCurrent()
if ($null -eq $installerIdentity.User -or
    -not $installerIdentity.User.IsAccountSid() -or
    $installerIdentity.User.IsWellKnown([Security.Principal.WellKnownSidType]::LocalSystemSid)) {
    throw '正式安裝必須由一個可辨識的互動式 Windows 使用者帳號執行。'
}
$installerSidValue = $installerIdentity.User.Value
$installerSid = $installerIdentity.User
$trustedPowerShellPath = Assert-TrustedPowerShellHost
$arpSnapshot = Get-ArpRegistrationSnapshot -InstallRoot $install
$installTreeServiceSid = Get-OptionalProductServiceSid
$operatorsGroupMutation = [pscustomobject]@{
    GroupCreated = $false
    MemberAdded = $false
    GroupSid = $null
    MemberSid = $installerSidValue
}
$existing = Get-Service -Name $serviceName -ErrorAction SilentlyContinue
if ($null -ne $existing) {
    $serviceWasRunning = $existing.Status -ne 'Stopped'
    $existingDefinition = Get-CimInstance -ClassName Win32_Service -Filter "Name='$serviceName'"
    $previousServicePath = $existingDefinition.PathName
    $expectedPreviousPrefix = (Join-Path $install 'versions').TrimEnd('\', '/') + `
        [IO.Path]::DirectorySeparatorChar
    $previousExecutablePath = ([string]$previousServicePath).TrimStart('"').Split('"', 2)[0]
    $previousServiceExecutablePath = [IO.Path]::GetFullPath($previousExecutablePath)
    if ($existingDefinition.StartName -ne 'NT SERVICE\MuhunMCSV' -or
        -not [IO.Path]::GetFullPath($previousExecutablePath).StartsWith(
            $expectedPreviousPrefix,
            [StringComparison]::OrdinalIgnoreCase)) {
        throw '現有同名 Windows Service 不是此安裝目錄管理的 Muhun MCSV Service。'
    }
    $previousServicePort = Get-ServiceRestPort $previousServiceExecutablePath
    $previousServiceSddl = Get-ServiceSecurityDescriptor
    $previousServiceSidType = Get-ServiceSidType
    $previousServiceSnapshot = New-ServiceRollbackSnapshot `
        -Definition $existingDefinition `
        -SecurityDescriptor $previousServiceSddl `
        -SidType $previousServiceSidType `
        -WasRunning $serviceWasRunning `
        -RestPort $previousServicePort
}
if (Test-Path -LiteralPath $activePointerPath -PathType Leaf) {
    Assert-NoExistingReparsePoints $activePointerPath 'active-version.v1'
    $previousActiveVersion = (Get-Content -LiteralPath $activePointerPath -Raw).Trim()
    if ($previousActiveVersion -notmatch '^(0|[1-9][0-9]*)\.(0|[1-9][0-9]*)\.(0|[1-9][0-9]*)(?:-[0-9A-Za-z-]+(?:\.[0-9A-Za-z-]+)*)?$') {
        throw '現有 active-version.v1 無效，為避免破壞回復點而停止。'
    }
}
if ($null -ne $existing -and [string]::IsNullOrWhiteSpace($previousActiveVersion)) {
    throw '現有 Service 缺少 active-version.v1，無法建立可驗證回復點。'
}
if (Test-Path -LiteralPath (Join-Path $data 'data\installation-id.v1') -PathType Leaf) {
    $existingIdentityText = Read-SafeAsciiFile `
        (Join-Path $data 'data\installation-id.v1') 36 128 'existing installation identity'
    $parsedExistingIdentity = [Guid]::Empty
    if (-not [Guid]::TryParseExact($existingIdentityText, 'D', [ref]$parsedExistingIdentity) -or
        $parsedExistingIdentity -eq [Guid]::Empty) {
        throw '現有 installation identity 無效，無法建立可驗證回復點。'
    }
    $previousInstallationId = [Nullable[Guid]]$parsedExistingIdentity
}
$installerSidBindingPath = Join-Path $data $installerOperatorSidRelativePath
if (Test-Path -LiteralPath $installerSidBindingPath -PathType Leaf) {
    $previousInstallerSidBinding = Read-SafeAsciiFile `
        $installerSidBindingPath 5 192 'existing installer operator SID'
    try {
        $previousInstallerSid = [Security.Principal.SecurityIdentifier]::new(
            $previousInstallerSidBinding)
    } catch [ArgumentException] {
        throw '現有 installer operator SID 無效，無法建立可驗證回復點。'
    }
    if (-not $previousInstallerSid.IsAccountSid()) {
        throw '現有 installer operator SID 不是帳號 SID。'
    }
    $installerSidBindingExisted = $true
}

try {
    if ($PSCmdlet.ShouldProcess($install, "安裝 X MCSV $($manifest.version)")) {
        Add-OwnedImmediateParentDirectory $install $ownedParentDirectories
        Add-OwnedImmediateParentDirectory $data $ownedParentDirectories
        New-Item -ItemType Directory -Path $install -Force | Out-Null
        New-Item -ItemType Directory -Path $versionsRoot -Force | Out-Null
        New-Item -ItemType Directory -Path $activationStateRoot -Force | Out-Null
        New-Item -ItemType Directory -Path $stableLauncherRoot -Force | Out-Null
        # Before any release script is invoked, remove inherited/custom-user write access from
        # a custom InstallRoot.  Existing service access is retained only when its virtual SID
        # can already be resolved during an upgrade.
        Set-AndAssertInstallExecutableTreeAcl `
            -InstallRoot $install `
            -VersionsRoot $versionsRoot `
            -ActivationStateRoot $activationStateRoot `
            -StableLauncherRoot $stableLauncherRoot `
            -ActivePointerPath $activePointerPath `
            -InstallerSid $installerSid `
            -ServiceSid $installTreeServiceSid
        # The independently signed verifier runs only from this administrator-owned staging.
        # It validates the provider signature/digests and the Android metadata before any
        # Service or data-root mutation takes place.
        Invoke-TrustedReleaseVerifier `
            -Source $source `
            -VerifiedRelease $verifiedRelease `
            -StagingRoot $verifierStagingRoot `
            -InstallRoot $install
        New-Item -ItemType Directory -Path $data -Force | Out-Null
        $serviceDataDirectories = @(
            'data', 'secrets', 'operations', 'imports', 'servers', 'runtimes',
            'backups', 'updates', 'plugins', 'logs'
        )
        foreach ($directoryName in $serviceDataDirectories) {
            New-Item -ItemType Directory -Path (Join-Path $data $directoryName) -Force | Out-Null
        }
        Write-AtomicText (Join-Path $install $installMarker) $expectedMarker
        Write-AtomicText (Join-Path $data $dataMarker) $expectedMarker
        Write-AtomicText (Join-Path $data $installerOperatorSidRelativePath) $installerSidValue
        $existingVersionState = Get-ExistingVersionPayloadState `
            -VersionRoot $versionRoot `
            -Source $source `
            -CopyEntries $verifiedRelease.CopyEntries
        if ($existingVersionState -eq 'RecognizedPartial') {
            $targetWasActive = [string]::Equals(
                $previousActiveVersion,
                [string]$manifest.version,
                [StringComparison]::OrdinalIgnoreCase)
            $serviceUsedTarget = -not [string]::IsNullOrWhiteSpace($previousServiceExecutablePath) -and
                (Test-IsUnderRoot $previousServiceExecutablePath $versionRoot)
            if ($targetWasActive -or $serviceUsedTarget) {
                throw "既有版本目錄不完整但仍被啟用，拒絕自動隔離：$versionRoot"
            }
            $retryQuarantineRoot = Move-ProvisionedVersionToQuarantine `
                -VersionRoot $versionRoot `
                -VersionsRoot $versionsRoot `
                -Version $manifest.version
            $existingVersionState = 'Missing'
        } elseif ($existingVersionState -eq 'Unrecognized') {
            throw "版本目錄已存在但無法證明屬於相同已驗證版本，為避免覆寫而停止：$versionRoot"
        }

        if ($existingVersionState -eq 'Missing') {
            New-Item -ItemType Directory -Path $stagingRoot | Out-Null
            foreach ($entry in $verifiedRelease.CopyEntries) {
                $sourcePath = Resolve-SafeSourceFile $source $entry.path
                $destinationPath = Join-Path $stagingRoot ([string]$entry.path).Replace('/', '\')
                if (-not (Test-IsUnderRoot $destinationPath $stagingRoot)) {
                    throw '更新檔案解析後超出暫存目錄。'
                }
                New-Item -ItemType Directory -Path ([IO.Path]::GetDirectoryName($destinationPath)) -Force | Out-Null
                [IO.File]::Copy($sourcePath, $destinationPath, $false)
                if ((Get-Sha256Hex $destinationPath) -ne ([string]$entry.sha256).ToLowerInvariant()) {
                    throw "暫存檔案複製後雜湊不符：$($entry.path)"
                }
            }
            $installedMetadataSource = Resolve-SafeSourceFile $source 'installed-version.v1.json'
            $installedMetadataEntry = Get-SignedReleaseManifestEntry `
                $verifiedRelease 'installed-version.v1.json'
            $installedMetadataDestination = Join-Path $stagingRoot 'installed-version.v1.json'
            [IO.File]::Copy($installedMetadataSource, $installedMetadataDestination, $false)
            if ((Get-Item -LiteralPath $installedMetadataDestination -Force).Length -ne
                    [long]$installedMetadataEntry.sizeBytes -or
                (Get-Sha256Hex $installedMetadataDestination) -cne
                    ([string]$installedMetadataEntry.sha256).ToLowerInvariant()) {
                throw '初次安裝版本 metadata 未通過已簽署 manifest 驗證。'
            }
            Move-Item -LiteralPath $stagingRoot -Destination $versionRoot
            $versionProvisioned = $true
        }

        $serviceExecutable = Join-Path $versionRoot $manifest.serviceEntryPoint.Replace('/', '\')
        $sourceStableLauncher = Join-Path $versionRoot $manifest.updaterEntryPoint.Replace('/', '\')
        Install-StableLauncherTransactionally `
            -SourcePath $sourceStableLauncher `
            -DestinationPath $stableLauncherPath `
            -PublisherCertificateSha256 $manifest.publisherCertificateSha256 `
            -Mutation $stableLauncherMutation
        $serviceArguments = '--Mcsv:Service:DataRoot=' + $data
        $binaryPath = '"' + $serviceExecutable + '" "' + $serviceArguments + '"'
        if ($null -eq $existing) {
            Invoke-Sc create $serviceName `
                'binPath=' $binaryPath `
                'start=' 'delayed-auto' `
                'DisplayName=' $serviceDisplayName `
                'obj=' 'NT SERVICE\MuhunMCSV' | Out-Null
            $serviceCreated = $true
        } else {
            if ($existing.Status -ne 'Stopped') {
                Stop-Service -Name $serviceName -Force -NoWait
                $existing.WaitForStatus(
                    'Stopped',
                    [TimeSpan]::FromSeconds($serviceStopTimeoutSeconds))
            }
            Invoke-Sc config $serviceName `
                'binPath=' $binaryPath `
                'start=' 'delayed-auto' `
                'DisplayName=' $serviceDisplayName `
                'obj=' 'NT SERVICE\MuhunMCSV' | Out-Null
        }

        Invoke-Sc sidtype $serviceName 'unrestricted' | Out-Null
        $installTreeServiceSid = ([Security.Principal.NTAccount]::new(
            'NT SERVICE',
            $serviceName)).Translate([Security.Principal.SecurityIdentifier])
        $serviceSid = $installTreeServiceSid.Value
        if ($null -eq $previousServiceSddl -and -not $serviceCreated) {
            $previousServiceSddl = Get-ServiceSecurityDescriptor
        }
        [void](Grant-ServiceSelfUpdateRights $serviceSid)

        Invoke-Sc description $serviceName 'Muhun MCSV headless server, Web, backup and notification service.' | Out-Null
        Invoke-Sc failure $serviceName `
            'reset=' '86400' `
            'actions=' 'restart/5000/restart/15000/restart/60000' | Out-Null
        Invoke-Sc failureflag $serviceName '1' | Out-Null
        $operatorsPrincipal = Ensure-MuhunOperatorsGroup -Mutation $operatorsGroupMutation
        $operatorsTraverseAcl = $operatorsPrincipal + ':(RX)'
        $installerTraverseAcl = '*' + $installerSidValue + ':(RX)'
        $aclOutput = & "$env:SystemRoot\System32\icacls.exe" $data '/inheritance:r' `
            '/grant:r' 'SYSTEM:(OI)(CI)F' 'BUILTIN\Administrators:(OI)(CI)F' `
            'NT SERVICE\MuhunMCSV:(OI)(CI)M' $operatorsTraverseAcl $installerTraverseAcl 2>&1
        if ($LASTEXITCODE -ne 0) {
            throw "資料目錄 ACL 建立失敗：$($aclOutput -join ' ')"
        }
        # The data root deliberately grants operators and the interactive installer only
        # non-inheriting RX.  Desktop browsing is limited to these two non-secret trees;
        # never propagate this access through the whole data root (especially secrets).
        $operatorsBrowseAcl = $operatorsPrincipal + ':(OI)(CI)RX'
        $installerBrowseAcl = '*' + $installerSidValue + ':(OI)(CI)RX'
        foreach ($serviceBrowseDirectoryName in @('servers', 'runtimes')) {
            $serviceBrowseDirectory = Join-Path $data $serviceBrowseDirectoryName
            $serviceBrowseAclOutput = & "$env:SystemRoot\System32\icacls.exe" `
                $serviceBrowseDirectory '/inheritance:r' '/grant:r' `
                'SYSTEM:(OI)(CI)F' 'BUILTIN\Administrators:(OI)(CI)F' `
                'NT SERVICE\MuhunMCSV:(OI)(CI)M' `
                $operatorsBrowseAcl $installerBrowseAcl 2>&1
            if ($LASTEXITCODE -ne 0) {
                throw "Service 可瀏覽目錄 ACL 建立失敗 ($serviceBrowseDirectoryName)：$($serviceBrowseAclOutput -join ' ')"
            }

            # Upgrade installs can already contain worlds, mods and managed Java files.
            # /T applies the new read-only principals to existing descendants, while /L
            # changes reparse-point ACLs themselves instead of following their targets.
            $serviceBrowseTreeAclOutput = & "$env:SystemRoot\System32\icacls.exe" `
                $serviceBrowseDirectory '/grant:r' `
                $operatorsBrowseAcl $installerBrowseAcl '/T' '/C' '/Q' '/L' 2>&1
            if ($LASTEXITCODE -ne 0) {
                throw "Service 既有可瀏覽資料 ACL 更新失敗 ($serviceBrowseDirectoryName)：$($serviceBrowseTreeAclOutput -join ' ')"
            }
        }
        $operatorsImportAcl = $operatorsPrincipal + ':(OI)(CI)M'
        $installerImportAcl = '*' + $installerSidValue + ':(OI)(CI)M'
        $importsDirectory = Join-Path $data 'imports'
        $importsAclOutput = & "$env:SystemRoot\System32\icacls.exe" $importsDirectory `
            '/inheritance:r' '/grant:r' 'SYSTEM:(OI)(CI)F' `
            'BUILTIN\Administrators:(OI)(CI)F' 'NT SERVICE\MuhunMCSV:(OI)(CI)M' `
            $operatorsImportAcl $installerImportAcl 2>&1
        if ($LASTEXITCODE -ne 0) {
            throw "匯入暫存目錄 ACL 建立失敗：$($importsAclOutput -join ' ')"
        }


        Write-AtomicText $activePointerPath $manifest.version
        # Finalize and recursively attest the executable tree after every installed file and
        # mutable pointer exists, but before the service or GUI launcher can execute it.
        Set-AndAssertInstallExecutableTreeAcl `
            -InstallRoot $install `
            -VersionsRoot $versionsRoot `
            -ActivationStateRoot $activationStateRoot `
            -StableLauncherRoot $stableLauncherRoot `
            -ActivePointerPath $activePointerPath `
            -InstallerSid $installerSid `
            -ServiceSid $installTreeServiceSid

        $quotedInstallRoot = '"' + $install.Replace('"', '') + '"'
        if (-not (Test-Path -LiteralPath $startMenuShortcutPath -PathType Leaf)) {
            New-ProductShortcut $startMenuShortcutPath $stableLauncherPath `
                "--launch-current --install-root $quotedInstallRoot" 1
            $startMenuShortcutCreated = $true
        }
        if (-not (Test-Path -LiteralPath $startupShortcutPath -PathType Leaf)) {
            New-ProductShortcut $startupShortcutPath $stableLauncherPath `
                "--gui-activation-broker --install-root $quotedInstallRoot" 7
            $startupShortcutCreated = $true
        }
        Start-Service -Name $serviceName
        $currentService = Get-Service -Name $serviceName
        $currentService.WaitForStatus('Running', [TimeSpan]::FromSeconds(30))
        $newServicePort = Get-ServiceRestPort $serviceExecutable
        [void](Wait-ProductActivationReady `
            -DataRoot $data `
            -Port $newServicePort `
            -ExpectedVersion $manifest.version `
             -ExpectedInstallationId $previousInstallationId `
             -TimeoutSeconds 90)
        Set-ArpRegistrationTransactionally `
            -Snapshot $arpSnapshot `
            -Mutation $arpMutation `
            -Version $manifest.version `
            -InstallRoot $install `
            -DataRoot $data `
            -UninstallerPath $installedUninstallerPath `
            -PowerShellPath $trustedPowerShellPath
        $postInstallBootstrapperPath = $sourceStableLauncher
        Complete-OwnedImmediateParentDirectories $ownedParentDirectories
        Complete-StableLauncherTransaction `
            -PublisherCertificateSha256 $manifest.publisherCertificateSha256 `
            -Mutation $stableLauncherMutation
        $installationApplied = $true
    }
} catch {
    $installationFailure = $_
    $rollbackErrors = [Collections.Generic.List[string]]::new()
    try {
        Restore-ArpRegistrationTransaction -Snapshot $arpSnapshot -Mutation $arpMutation
    } catch { $rollbackErrors.Add($_.Exception.Message) }
    try {
        $current = Get-Service -Name $serviceName -ErrorAction SilentlyContinue
        if ($null -ne $current -and $current.Status -ne 'Stopped') {
            Stop-Service -Name $serviceName -Force -NoWait
            $current.WaitForStatus(
                'Stopped',
                [TimeSpan]::FromSeconds($serviceStopTimeoutSeconds))
        }
    } catch { $rollbackErrors.Add($_.Exception.Message) }

    try {
        if ([string]::IsNullOrWhiteSpace($previousActiveVersion)) {
            if (Test-Path -LiteralPath $activePointerPath -PathType Leaf) {
                Remove-Item -LiteralPath $activePointerPath -Force
            }
        } else {
            Write-AtomicText $activePointerPath $previousActiveVersion
            $restoredPointerGrants = @(New-InstallAclGrants `
                $installerSid $installTreeServiceSid 'Modify')
            Set-ExactProtectedPathAcl `
                $activePointerPath $restoredPointerGrants 'active-version.v1 回復 ACL'
            Assert-ExactProtectedPathAcl `
                $activePointerPath $restoredPointerGrants 'active-version.v1 回復 ACL'
        }
    } catch { $rollbackErrors.Add($_.Exception.Message) }

    try {
        if ($installerSidBindingExisted) {
            Write-AtomicText $installerSidBindingPath $previousInstallerSidBinding
        } elseif (Test-Path -LiteralPath $installerSidBindingPath -PathType Leaf) {
            Remove-Item -LiteralPath $installerSidBindingPath -Force
        }
    } catch { $rollbackErrors.Add($_.Exception.Message) }

    if ($serviceCreated) {
        try { Invoke-Sc delete $serviceName | Out-Null } catch { $rollbackErrors.Add($_.Exception.Message) }
    } elseif ($null -ne $previousServiceSnapshot) {
        try {
            Restore-ServiceRollbackSnapshot $previousServiceSnapshot
            if ($previousServiceSnapshot.WasRunning) {
                Start-Service -Name $serviceName
                (Get-Service -Name $serviceName).WaitForStatus('Running', [TimeSpan]::FromSeconds(30))
                if ([string]::IsNullOrWhiteSpace($previousActiveVersion) -or
                    $null -eq $previousServiceSnapshot.RestPort -or
                    $null -eq $previousInstallationId) {
                    throw '舊版本 Service 缺少可驗證的版本、Port 或 installation identity 回復點。'
                }
                [void](Wait-ProductActivationReady `
                    -DataRoot $data `
                    -Port $previousServiceSnapshot.RestPort `
                    -ExpectedVersion $previousActiveVersion `
                    -ExpectedInstallationId $previousInstallationId `
                    -TimeoutSeconds 90)
            }
        } catch { $rollbackErrors.Add($_.Exception.Message) }
    }

    try {
        $operatorsGroup = $null
        if (-not [string]::IsNullOrWhiteSpace($operatorsGroupMutation.GroupSid)) {
            $operatorsGroupSid = [Security.Principal.SecurityIdentifier]::new(
                $operatorsGroupMutation.GroupSid)
            $operatorsGroup = Get-LocalGroup -SID $operatorsGroupSid -ErrorAction SilentlyContinue
        }
        if ($null -ne $operatorsGroup -and $operatorsGroupMutation.MemberAdded) {
            $addedMember = @(Get-LocalGroupMember -Group $operatorsGroup -ErrorAction Stop) |
                Where-Object {
                    $null -ne $_.SID -and $_.SID.Value -eq $operatorsGroupMutation.MemberSid
                } |
                Select-Object -First 1
            if ($null -ne $addedMember) {
                Remove-LocalGroupMember -Group $operatorsGroup -Member $addedMember -ErrorAction Stop
            }
        }
        if ($null -ne $operatorsGroup -and $operatorsGroupMutation.GroupCreated) {
            if ($operatorsGroup.Name -cne $operatorsGroupName -or
                $operatorsGroup.Description -cne $operatorsGroupDescription) {
                throw '本次新建的 Muhun MCSV Operators 群組識別資料已被變更，拒絕在回復時刪除。'
            }
            $remainingMembers = @(Get-LocalGroupMember -Group $operatorsGroup -ErrorAction Stop)
            if ($remainingMembers.Count -ne 0) {
                throw '本次新建的 Muhun MCSV Operators 群組出現非安裝器新增的成員，拒絕在回復時刪除。'
            }
            Remove-LocalGroup -InputObject $operatorsGroup -ErrorAction Stop
        }
    } catch { $rollbackErrors.Add($_.Exception.Message) }

    try {
        Stop-ExactProductGui $targetGuiExecutable
    } catch { $rollbackErrors.Add($_.Exception.Message) }

    $failedVersionQuarantine = $null
    if ($versionProvisioned -and (Test-Path -LiteralPath $versionRoot -PathType Container)) {
        try {
            # Free the canonical version path before recursive cleanup.  If Windows still has a
            # file open, a retained quarantine cannot make the next signed retry overwrite data.
            $failedVersionQuarantine = Move-ProvisionedVersionToQuarantine `
                -VersionRoot $versionRoot `
                -VersionsRoot $versionsRoot `
                -Version $manifest.version
        } catch { $rollbackErrors.Add($_.Exception.Message) }
    }
    try {
        Remove-TrustedVerifierStaging $verifierStagingRoot $install
    } catch { $rollbackErrors.Add($_.Exception.Message) }
    $temporaryPaths = @($stagingRoot)
    if (-not [string]::IsNullOrWhiteSpace($failedVersionQuarantine)) {
        $temporaryPaths += $failedVersionQuarantine
    } elseif ($versionProvisioned) {
        $temporaryPaths += $versionRoot
    }
    foreach ($temporary in $temporaryPaths) {
        if (-not [string]::IsNullOrWhiteSpace($temporary) -and
            (Test-Path -LiteralPath $temporary) -and (Test-IsUnderRoot $temporary $install)) {
            try { Remove-Item -LiteralPath $temporary -Recurse -Force } catch { $rollbackErrors.Add($_.Exception.Message) }
        }
    }
    foreach ($shortcut in @(
        [pscustomobject]@{ Path = $startMenuShortcutPath; Created = $startMenuShortcutCreated },
        [pscustomobject]@{ Path = $startupShortcutPath; Created = $startupShortcutCreated })) {
        if ($shortcut.Created -and (Test-Path -LiteralPath $shortcut.Path -PathType Leaf)) {
            try { Remove-Item -LiteralPath $shortcut.Path -Force } catch { $rollbackErrors.Add($_.Exception.Message) }
        }
    }
    try {
        Restore-StableLauncherTransaction `
            -DestinationPath $stableLauncherPath `
            -PublisherCertificateSha256 $manifest.publisherCertificateSha256 `
            -Mutation $stableLauncherMutation
    } catch { $rollbackErrors.Add($_.Exception.Message) }
    if ($installExistedBefore -and (Test-Path -LiteralPath $install -PathType Container)) {
        try {
            Set-AndAssertInstallExecutableTreeAcl `
                -InstallRoot $install `
                -VersionsRoot $versionsRoot `
                -ActivationStateRoot $activationStateRoot `
                -StableLauncherRoot $stableLauncherRoot `
                -ActivePointerPath $activePointerPath `
                -InstallerSid $installerSid `
                -ServiceSid $installTreeServiceSid
        } catch { $rollbackErrors.Add($_.Exception.Message) }
    }
    if (-not $installExistedBefore -and (Test-Path -LiteralPath $install -PathType Container)) {
        try {
            $remaining = @(Get-ChildItem -LiteralPath $install -Recurse -Force)
            if (@($remaining | Where-Object { $_.FullName -notin @(
                (Join-Path $install $installMarker),
                $activationStateRoot,
                $stableLauncherRoot,
                $versionsRoot) }).Count -eq 0) {
                Remove-Item -LiteralPath $install -Recurse -Force
            }
        } catch { $rollbackErrors.Add($_.Exception.Message) }
    }
    if (-not $dataExistedBefore -and (Test-Path -LiteralPath $data -PathType Container)) {
        try {
            Assert-NoExistingReparsePoints $data 'new data rollback root'
            $dataMarkerPath = Join-Path $data $dataMarker
            if (-not (Test-Path -LiteralPath $dataMarkerPath -PathType Leaf) -or
                (Get-Content -LiteralPath $dataMarkerPath -Raw).Trim() -cne $expectedMarker) {
                throw '新資料目錄缺少受管理標記，拒絕在回復時刪除。'
            }
            Remove-Item -LiteralPath $data -Recurse -Force
        } catch { $rollbackErrors.Add($_.Exception.Message) }
    }
    foreach ($ownedParent in @($ownedParentDirectories.Values)) {
        try {
            [void](Remove-OwnedEmptyImmediateParentDirectory $ownedParent)
        } catch {
            $rollbackErrors.Add($_.Exception.Message)
        }
    }
    if ($rollbackErrors.Count -gt 0) {
        throw [AggregateException]::new(
            "安裝失敗，且回復過程發生錯誤：$($rollbackErrors -join ' | ')",
            $installationFailure.Exception)
    }
    throw $installationFailure
}

if ($installationApplied -and
    -not [string]::IsNullOrWhiteSpace($retryQuarantineRoot) -and
    (Test-Path -LiteralPath $retryQuarantineRoot -PathType Container)) {
    try {
        $normalizedRetryQuarantine = [IO.Path]::GetFullPath($retryQuarantineRoot).TrimEnd('\', '/')
        $normalizedVersionsRoot = [IO.Path]::GetFullPath($versionsRoot).TrimEnd('\', '/')
        if (-not (Test-IsUnderRoot $normalizedRetryQuarantine $normalizedVersionsRoot) -or
            -not [string]::Equals(
                [IO.Path]::GetDirectoryName($normalizedRetryQuarantine),
                $normalizedVersionsRoot,
                [StringComparison]::OrdinalIgnoreCase) -or
            -not [IO.Path]::GetFileName($retryQuarantineRoot).StartsWith(
                '.failed-install-',
                [StringComparison]::Ordinal)) {
            throw '拒絕清除 versions 根目錄外的舊失敗安裝隔離目錄。'
        }
        Assert-NoExistingReparsePoints $retryQuarantineRoot '舊失敗安裝隔離目錄'
        if (@(Get-ChildItem -LiteralPath $retryQuarantineRoot -Recurse -Force -ErrorAction Stop |
            Where-Object { ($_.Attributes -band [IO.FileAttributes]::ReparsePoint) -ne 0 }).Count -ne 0) {
            throw '舊失敗安裝隔離目錄含有 reparse point。'
        }
        Remove-Item -LiteralPath $retryQuarantineRoot -Recurse -Force
    } catch {
        Write-Warning "新版本已啟用，但舊失敗安裝隔離目錄無法清除：$($_.Exception.Message)"
    }
}

if ($installationApplied) {
    if (Test-Path -LiteralPath $legacyStartMenuShortcutPath -PathType Leaf) {
        try {
            Remove-Item -LiteralPath $legacyStartMenuShortcutPath -Force
        } catch {
            Write-Warning "X MCSV 已安裝，但無法移除舊版開始功能表捷徑：$($_.Exception.Message)"
        }
    }
    $guiActivated = Invoke-PostInstallGuiActivation `
        -BootstrapperPath $postInstallBootstrapperPath `
        -LauncherPath $stableLauncherPath `
        -InstallRoot $install
    if ($guiActivated) {
        Write-Host '桌面管理器已在目前的互動式 Windows 工作階段安全開啟。'
    }
    Write-Host "X MCSV $($manifest.version) 安裝完成；舊版本已保留供回復。"
}
