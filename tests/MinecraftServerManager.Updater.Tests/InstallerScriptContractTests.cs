namespace MinecraftServerManager.Updater.Tests;

public sealed class InstallerScriptContractTests
{
    private static readonly string RepositoryRoot = FindRepositoryRoot();

    [Fact]
    public void InstallerRequiresSignatureHashServiceRecoveryAndRestrictedAcl()
    {
        var source = File.ReadAllText(Path.Combine(RepositoryRoot, "scripts", "Install-MuhunMcsv.ps1"));

        Assert.Contains("Get-AuthenticodeSignature", source, StringComparison.Ordinal);
        Assert.Contains("release-manifest.json.sig", source, StringComparison.Ordinal);
        Assert.Contains("RSASignaturePadding]::Pss", source, StringComparison.Ordinal);
        Assert.Contains("TimeStamperCertificate", source, StringComparison.Ordinal);
        Assert.Contains("publisherCertificateSha256", source, StringComparison.Ordinal);
        Assert.Contains("$previousServicePath", source, StringComparison.Ordinal);
        Assert.Contains("$previousActiveVersion", source, StringComparison.Ordinal);
        Assert.Contains("安裝失敗，且回復過程發生錯誤", source, StringComparison.Ordinal);
        Assert.Contains("Get-FileHash", source, StringComparison.Ordinal);
        Assert.Contains("'binPath=' $binaryPath", source, StringComparison.Ordinal);
        Assert.Contains("'start=' 'delayed-auto'", source, StringComparison.Ordinal);
        Assert.Contains("'DisplayName=' $serviceDisplayName", source, StringComparison.Ordinal);
        Assert.Contains("'obj=' 'NT SERVICE\\MuhunMCSV'", source, StringComparison.Ordinal);
        Assert.Contains("'reset=' '86400'", source, StringComparison.Ordinal);
        Assert.Contains(
            "'actions=' 'restart/5000/restart/15000/restart/60000'",
            source,
            StringComparison.Ordinal);
        Assert.Contains("Invoke-Sc failureflag $serviceName '1'", source, StringComparison.Ordinal);
        Assert.Contains("$PSNativeCommandArgumentPassing = 'Standard'", source, StringComparison.Ordinal);
        Assert.Contains("sc.exe 選項名稱與值必須使用兩個獨立引數", source, StringComparison.Ordinal);
        Assert.Equal(2, System.Text.RegularExpressions.Regex.Matches(
            source,
            "'start=' 'delayed-auto'").Count);
        Assert.Equal(3, System.Text.RegularExpressions.Regex.Matches(source, "'binPath='").Count);
        Assert.Equal(3, System.Text.RegularExpressions.Regex.Matches(source, "'DisplayName='").Count);
        Assert.Equal(3, System.Text.RegularExpressions.Regex.Matches(source, "'obj='").Count);
        Assert.Contains("'binPath=' $Snapshot.BinaryPath", source, StringComparison.Ordinal);
        Assert.Contains("'start=' $Snapshot.StartArgument", source, StringComparison.Ordinal);
        Assert.DoesNotContain("start= delayed-auto", source, StringComparison.Ordinal);
        Assert.DoesNotContain("failureflag= 1", source, StringComparison.Ordinal);
        Assert.Contains("NT SERVICE\\MuhunMCSV:(OI)(CI)M", source, StringComparison.Ordinal);
        Assert.Contains(
            "foreach ($serviceBrowseDirectoryName in @('servers', 'runtimes'))",
            source,
            StringComparison.Ordinal);
        Assert.Contains("$operatorsBrowseAcl = $operatorsPrincipal + ':(OI)(CI)RX'", source, StringComparison.Ordinal);
        Assert.Contains("$installerBrowseAcl = '*' + $installerSidValue + ':(OI)(CI)RX'", source, StringComparison.Ordinal);
        Assert.Contains("$serviceBrowseDirectory '/inheritance:r' '/grant:r'", source, StringComparison.Ordinal);
        Assert.Contains("$serviceBrowseDirectory '/grant:r'", source, StringComparison.Ordinal);
        Assert.Contains("$operatorsBrowseAcl $installerBrowseAcl '/T' '/C' '/Q' '/L'", source, StringComparison.Ordinal);
        Assert.DoesNotContain("Join-Path $data 'secrets'", source, StringComparison.Ordinal);
        Assert.DoesNotContain("$data '/grant:r' $operatorsBrowseAcl", source, StringComparison.Ordinal);
        Assert.DoesNotContain("$data '/grant:r' $installerBrowseAcl", source, StringComparison.Ordinal);
        Assert.Contains("/api/v1/system/activation-ready", source, StringComparison.Ordinal);
        Assert.Contains("X-MCSV-Service-Token", source, StringComparison.Ordinal);
        Assert.Contains("ExpectedInstallationId", source, StringComparison.Ordinal);
        Assert.Contains("ExpectedVersion", source, StringComparison.Ordinal);
        Assert.Contains("Service DACL 無法完整回復", source, StringComparison.Ordinal);
        Assert.Contains("Service SID type 無法完整回復", source, StringComparison.Ordinal);
        Assert.Contains("previousInstallerSidBinding", source, StringComparison.Ordinal);
        Assert.Contains("CCDCLCRPWP", source, StringComparison.Ordinal);
        Assert.Contains("NT SERVICE\\MuhunMCSV:(OI)(CI)M", source, StringComparison.Ordinal);
        Assert.Contains("installer-operator-sid.v1", source, StringComparison.Ordinal);
        var groupDescriptionMatches = System.Text.RegularExpressions.Regex.Matches(
            source,
            @"(?m)^[ \t]*\$operatorsGroupDescription[ \t]*=[ \t]*'(?<value>(?:''|[^'])*)'[ \t]*\r?$",
            System.Text.RegularExpressions.RegexOptions.CultureInvariant);
        Assert.Single(groupDescriptionMatches.Cast<System.Text.RegularExpressions.Match>());
        var groupDescription = groupDescriptionMatches[0].Groups["value"].Value.Replace(
            "''",
            "'",
            StringComparison.Ordinal);
        Assert.InRange(groupDescription.Length, 1, 48);
        var ensureGroupFunction = System.Text.RegularExpressions.Regex.Match(
            source,
            @"(?ms)^function Ensure-MuhunOperatorsGroup \{.*?^\}\r?$",
            System.Text.RegularExpressions.RegexOptions.CultureInvariant);
        Assert.True(ensureGroupFunction.Success, "Could not locate Ensure-MuhunOperatorsGroup.");
        Assert.Single(System.Text.RegularExpressions.Regex.Matches(
            ensureGroupFunction.Value,
            @"\bNew-LocalGroup\b",
            System.Text.RegularExpressions.RegexOptions.CultureInvariant)
            .Cast<System.Text.RegularExpressions.Match>());
        Assert.Matches(
            @"(?s)\bNew-LocalGroup\b.*?-Description[ \t]+\$operatorsGroupDescription\b",
            ensureGroupFunction.Value);
        Assert.Contains("Get-LocalGroupMember -Group $group", ensureGroupFunction.Value, StringComparison.Ordinal);
        Assert.Contains("Add-LocalGroupMember -Group $group", ensureGroupFunction.Value, StringComparison.Ordinal);
        Assert.DoesNotContain(
            "Get-LocalGroupMember -Group $operatorsGroupName",
            ensureGroupFunction.Value,
            StringComparison.Ordinal);
        Assert.DoesNotContain(
            "Add-LocalGroupMember -Group $operatorsGroupName",
            ensureGroupFunction.Value,
            StringComparison.Ordinal);
        Assert.Contains("Assert-LocalGroupDescriptionSupported", source, StringComparison.Ordinal);
        Assert.Contains("-Description $operatorsGroupDescription", source, StringComparison.Ordinal);
        Assert.Contains("$Mutation.GroupCreated = $true", source, StringComparison.Ordinal);
        Assert.Contains("$Mutation.MemberAdded = $true", source, StringComparison.Ordinal);
        Assert.Contains("$Mutation.GroupSid = $group.SID.Value", source, StringComparison.Ordinal);
        Assert.Contains("return '*' + $group.SID.Value", ensureGroupFunction.Value, StringComparison.Ordinal);
        Assert.DoesNotContain(
            "return \"$env:COMPUTERNAME\\$operatorsGroupName\"",
            ensureGroupFunction.Value,
            StringComparison.Ordinal);
        Assert.Contains("Get-LocalGroup -SID $operatorsGroupSid", source, StringComparison.Ordinal);
        Assert.Contains("$operatorsGroup.Name -cne $operatorsGroupName", source, StringComparison.Ordinal);
        Assert.Contains("$operatorsGroup.Description -cne $operatorsGroupDescription", source, StringComparison.Ordinal);
        Assert.Contains("Remove-LocalGroupMember", source, StringComparison.Ordinal);
        Assert.Contains("Remove-LocalGroup -InputObject $operatorsGroup", source, StringComparison.Ordinal);
        Assert.DoesNotContain("Remove-LocalGroup -Name $operatorsGroupName", source, StringComparison.Ordinal);
        Assert.Contains("activation-state", source, StringComparison.Ordinal);
        Assert.Contains("--gui-activation-broker", source, StringComparison.Ordinal);
        Assert.Contains("--start-gui-activation-broker", source, StringComparison.Ordinal);
        Assert.Contains("--launch-current", source, StringComparison.Ordinal);
        Assert.Contains("--activate-current", source, StringComparison.Ordinal);
        Assert.Contains("Invoke-InteractiveGuiActivation", source, StringComparison.Ordinal);
        Assert.Contains("Start-GuiActivationBrokerThroughExplorer", source, StringComparison.Ordinal);
        Assert.Contains("Invoke-PostInstallGuiActivation", source, StringComparison.Ordinal);
        Assert.DoesNotContain("Shell.Application", source, StringComparison.Ordinal);
        Assert.DoesNotContain("Start-UnelevatedGuiActivationBroker", source, StringComparison.Ordinal);
        Assert.DoesNotContain("Get-StableBrokerProcessIds", source, StringComparison.Ordinal);
        Assert.Contains("Stop-ExactProductGui", source, StringComparison.Ordinal);
        Assert.Contains("WaitForExit($TimeoutSeconds * 1000)", source, StringComparison.Ordinal);
        Assert.Contains("Assert-NoExistingReparsePoints", source, StringComparison.Ordinal);
        Assert.Contains("Muhun MCSV Manager.lnk", source, StringComparison.Ordinal);
        Assert.DoesNotContain(".HasValue", source, StringComparison.Ordinal);
        Assert.DoesNotContain("Start-Sleep -Seconds 3", source, StringComparison.Ordinal);
        Assert.DoesNotContain("NT SERVICE\\MuhunMCSV:(OI)(CI)F", source, StringComparison.Ordinal);
        Assert.DoesNotContain("0.0.0.0", source, StringComparison.Ordinal);
        Assert.DoesNotContain("New-NetFirewallRule", source, StringComparison.Ordinal);
        Assert.DoesNotContain("ExecutionPolicy Bypass", source, StringComparison.OrdinalIgnoreCase);

        var coreTransactionStart = source.IndexOf(
            "try {\n    if ($PSCmdlet.ShouldProcess",
            StringComparison.Ordinal);
        var coreServiceStart = source.IndexOf(
            "        Start-Service -Name $serviceName",
            coreTransactionStart,
            StringComparison.Ordinal);
        var coreReadyCheck = source.IndexOf(
            "        [void](Wait-ProductActivationReady",
            coreServiceStart,
            StringComparison.Ordinal);
        var coreCommit = source.IndexOf(
            "        $installationApplied = $true",
            coreReadyCheck,
            StringComparison.Ordinal);
        var rollbackCatch = source.IndexOf(
            "} catch {\n    $installationFailure = $_",
            coreCommit,
            StringComparison.Ordinal);
        var finalRollbackThrow = source.IndexOf(
            "    throw $installationFailure",
            rollbackCatch,
            StringComparison.Ordinal);
        var postInstallBlock = source.IndexOf(
            "if ($installationApplied) {",
            finalRollbackThrow,
            StringComparison.Ordinal);
        var postInstallCall = source.IndexOf(
            "$guiActivated = Invoke-PostInstallGuiActivation",
            postInstallBlock,
            StringComparison.Ordinal);
        Assert.True(
            coreTransactionStart >= 0 &&
            coreServiceStart > coreTransactionStart &&
            coreReadyCheck > coreServiceStart &&
            coreCommit > coreReadyCheck &&
            rollbackCatch > coreCommit &&
            finalRollbackThrow > rollbackCatch &&
            postInstallBlock > finalRollbackThrow &&
            postInstallCall > postInstallBlock,
            "Core Service activation must commit before the non-fatal desktop bootstrap runs.");
        var coreTransaction = source[coreTransactionStart..rollbackCatch];
        Assert.DoesNotContain(
            "Start-GuiActivationBrokerThroughExplorer",
            coreTransaction,
            StringComparison.Ordinal);
        Assert.DoesNotContain(
            "Invoke-InteractiveGuiActivation",
            coreTransaction,
            StringComparison.Ordinal);

        var postInstallFunction = System.Text.RegularExpressions.Regex.Match(
            source,
            @"(?ms)^function Invoke-PostInstallGuiActivation \{.*?^\}\r?$",
            System.Text.RegularExpressions.RegexOptions.CultureInvariant);
        Assert.True(postInstallFunction.Success, "Could not locate Invoke-PostInstallGuiActivation.");
        Assert.Contains("catch {", postInstallFunction.Value, StringComparison.Ordinal);
        Assert.Contains("return $false", postInstallFunction.Value, StringComparison.Ordinal);
        Assert.Contains("Write-Warning", postInstallFunction.Value, StringComparison.Ordinal);
        Assert.DoesNotContain("throw ", postInstallFunction.Value, StringComparison.Ordinal);

        if (OperatingSystem.IsWindows())
        {
            var descriptionProbe =
                "$attributes = @((Get-Command New-LocalGroup -ErrorAction Stop)." +
                "Parameters['Description'].Attributes | Where-Object { " +
                "$_ -is [Management.Automation.ValidateLengthAttribute] })\n" +
                "if ($attributes.Count -ne 1 -or $attributes[0].MaxLength -ne 48) { exit 6 }\n" +
                $"if ({groupDescription.Length} -gt $attributes[0].MaxLength) {{ exit 7 }}\n" +
                $"$description = '{groupDescription.Replace("'", "''", StringComparison.Ordinal)}'\n" +
                "$probeName = 'MuhunMCSV_DescriptionProbe_' + [Guid]::NewGuid().ToString('N').Substring(0, 8)\n" +
                "if ($null -ne (Get-LocalGroup -Name $probeName -ErrorAction SilentlyContinue)) { exit 8 }\n" +
                "New-LocalGroup -Name $probeName -Description $description -WhatIf -ErrorAction Stop | Out-Null\n" +
                "if ($null -ne (Get-LocalGroup -Name $probeName -ErrorAction SilentlyContinue)) { exit 9 }\n" +
                "exit 0\n";
            var encodedDescriptionProbe = Convert.ToBase64String(
                System.Text.Encoding.Unicode.GetBytes(descriptionProbe));
            var descriptionProbeStartInfo = new System.Diagnostics.ProcessStartInfo
            {
                FileName = "pwsh.exe",
                UseShellExecute = false,
                CreateNoWindow = true,
                RedirectStandardOutput = true,
                RedirectStandardError = true,
            };
            descriptionProbeStartInfo.ArgumentList.Add("-NoProfile");
            descriptionProbeStartInfo.ArgumentList.Add("-NonInteractive");
            descriptionProbeStartInfo.ArgumentList.Add("-EncodedCommand");
            descriptionProbeStartInfo.ArgumentList.Add(encodedDescriptionProbe);
            using (var descriptionProbeProcess = System.Diagnostics.Process.Start(descriptionProbeStartInfo)
                ?? throw new InvalidOperationException("Could not inspect New-LocalGroup metadata."))
            {
                var descriptionProbeOutput = descriptionProbeProcess.StandardOutput.ReadToEnd();
                var descriptionProbeError = descriptionProbeProcess.StandardError.ReadToEnd();
                descriptionProbeProcess.WaitForExit();
                Assert.True(
                    descriptionProbeProcess.ExitCode == 0,
                    $"The local operators description exceeds the real Windows cmdlet limit. " +
                    $"Exit={descriptionProbeProcess.ExitCode}; stdout={descriptionProbeOutput}; " +
                    $"stderr={descriptionProbeError}");
            }

            var functionMatch = System.Text.RegularExpressions.Regex.Match(
                source,
                @"(?ms)^function Invoke-Sc \{.*?^\}\r?\n(?=\r?\nfunction Initialize-ServiceFailureConfigurationInterop)");
            Assert.True(functionMatch.Success, "Could not extract the real Invoke-Sc function.");
            var probeName = $"MuhunMCSV_ArgumentProbe_{Guid.NewGuid():N}";
            var escapedInstallerPath = Path.Combine(
                    RepositoryRoot,
                    "scripts",
                    "Install-MuhunMcsv.ps1")
                .Replace("'", "''", StringComparison.Ordinal);
            var probeScript =
                "$ErrorActionPreference = 'Stop'\n" +
                functionMatch.Value + "\n" +
                $"$serviceName = '{probeName}'\n" +
                "$binaryPath = '\"C:\\Program Files\\Muhun Probe\\service.exe\" " +
                "\"--Mcsv:Service:DataRoot=C:\\ProgramData\\Muhun Probe\"'\n" +
                "$serviceDisplayName = 'Muhun MCSV Probe Service'\n" +
                "$tokens = $null\n" +
                "$parseErrors = $null\n" +
                $"$installerAst = [Management.Automation.Language.Parser]::ParseFile('{escapedInstallerPath}', " +
                "[ref]$tokens, [ref]$parseErrors)\n" +
                "$createCommands = @($installerAst.FindAll({ param($node) " +
                "$node -is [Management.Automation.Language.CommandAst] -and " +
                "$node.CommandElements.Count -ge 2 -and " +
                "$node.CommandElements[0].Value -ceq 'Invoke-Sc' -and " +
                "$node.CommandElements[1].Value -ceq 'create' }, $true))\n" +
                "if (@($parseErrors).Count -ne 0 -or $createCommands.Count -ne 1) { exit 4 }\n" +
                "$safeCommand = [regex]::Replace($createCommands[0].Extent.Text, " +
                "'^Invoke-Sc\\s+create\\b', 'Invoke-Sc config', " +
                "[Text.RegularExpressions.RegexOptions]::IgnoreCase)\n" +
                "try {\n" +
                "    Invoke-Expression $safeCommand\n" +
                "    exit 2\n" +
                "} catch {\n" +
                "    if ($_.Exception.Message -match '\\(1060\\)') { exit 0 }\n" +
                "    Write-Error $_.Exception.Message\n" +
                "    exit 3\n" +
                "}\n";
            var encodedProbe = Convert.ToBase64String(
                System.Text.Encoding.Unicode.GetBytes(probeScript));
            var startInfo = new System.Diagnostics.ProcessStartInfo
            {
                FileName = "pwsh.exe",
                UseShellExecute = false,
                CreateNoWindow = true,
                RedirectStandardOutput = true,
                RedirectStandardError = true,
            };
            startInfo.ArgumentList.Add("-NoProfile");
            startInfo.ArgumentList.Add("-NonInteractive");
            startInfo.ArgumentList.Add("-EncodedCommand");
            startInfo.ArgumentList.Add(encodedProbe);

            using var process = System.Diagnostics.Process.Start(startInfo)
                ?? throw new InvalidOperationException("Could not start the safe Invoke-Sc syntax probe.");
            var standardOutput = process.StandardOutput.ReadToEnd();
            var standardError = process.StandardError.ReadToEnd();
            process.WaitForExit();

            Assert.True(
                process.ExitCode == 0,
                $"The real Invoke-Sc wrapper did not preserve split argv. Exit={process.ExitCode}; " +
                $"stdout={standardOutput}; stderr={standardError}");

            var postInstallProbe =
                "$ErrorActionPreference = 'Stop'\n" +
                postInstallFunction.Value + "\n" +
                "$script:bootstrapCalls = 0\n" +
                "$script:activationCalls = 0\n" +
                "function Start-GuiActivationBrokerThroughExplorer {\n" +
                "    $script:bootstrapCalls++\n" +
                "    if ($script:bootstrapCalls -eq 1) { throw 'simulated explorer unavailable' }\n" +
                "}\n" +
                "function Invoke-InteractiveGuiActivation {\n" +
                "    $script:activationCalls++\n" +
                "    throw 'simulated GUI readiness rejection'\n" +
                "}\n" +
                "$first = Invoke-PostInstallGuiActivation -BootstrapperPath 'X' -LauncherPath 'Y' -InstallRoot 'Z'\n" +
                "if ($first -ne $false -or $script:bootstrapCalls -ne 1 -or $script:activationCalls -ne 0) { exit 11 }\n" +
                "$second = Invoke-PostInstallGuiActivation -BootstrapperPath 'X' -LauncherPath 'Y' -InstallRoot 'Z'\n" +
                "if ($second -ne $false -or $script:bootstrapCalls -ne 2 -or $script:activationCalls -ne 1) { exit 12 }\n" +
                "exit 0\n";
            var encodedPostInstallProbe = Convert.ToBase64String(
                System.Text.Encoding.Unicode.GetBytes(postInstallProbe));
            var postInstallStartInfo = new System.Diagnostics.ProcessStartInfo
            {
                FileName = "pwsh.exe",
                UseShellExecute = false,
                CreateNoWindow = true,
                RedirectStandardOutput = true,
                RedirectStandardError = true,
            };
            postInstallStartInfo.ArgumentList.Add("-NoProfile");
            postInstallStartInfo.ArgumentList.Add("-NonInteractive");
            postInstallStartInfo.ArgumentList.Add("-EncodedCommand");
            postInstallStartInfo.ArgumentList.Add(encodedPostInstallProbe);
            using var postInstallProcess = System.Diagnostics.Process.Start(postInstallStartInfo)
                ?? throw new InvalidOperationException("Could not probe post-install GUI failure handling.");
            var postInstallOutput = postInstallProcess.StandardOutput.ReadToEnd();
            var postInstallError = postInstallProcess.StandardError.ReadToEnd();
            postInstallProcess.WaitForExit();
            Assert.True(
                postInstallProcess.ExitCode == 0,
                $"Post-install GUI failures were not fail-soft. Exit={postInstallProcess.ExitCode}; " +
                $"stdout={postInstallOutput}; stderr={postInstallError}");
            var postInstallCombinedOutput = postInstallOutput + postInstallError;
            Assert.Contains("simulated explorer unavailable", postInstallCombinedOutput, StringComparison.Ordinal);
            Assert.Contains("simulated GUI readiness rejection", postInstallCombinedOutput, StringComparison.Ordinal);
            Assert.DoesNotContain("{0}", postInstallCombinedOutput, StringComparison.Ordinal);
        }
    }

    [Fact]
    public void InstallerActivationReadyJsonPreservesExplicitUtcBeforePowerShellDateConversion()
    {
        var source = File.ReadAllText(Path.Combine(RepositoryRoot, "scripts", "Install-MuhunMcsv.ps1"));
        var activationFunctions = System.Text.RegularExpressions.Regex.Match(
            source,
            @"(?ms)^function Read-ProductActivationReadyResponse \{.*?" +
            @"(?=^function New-ProductShortcut)");
        Assert.True(activationFunctions.Success, "Could not extract the real activation-ready functions.");
        Assert.DoesNotContain(
            "$response.Content | ConvertFrom-Json",
            source,
            StringComparison.Ordinal);

        var installationId = Guid.NewGuid();
        var validJson =
            $"{{\"status\":\"ready\",\"product\":\"Muhun MCSV Manager\",\"version\":\"1.0.0\"," +
            $"\"installationId\":\"{installationId:D}\",\"startedAtUtc\":\"2026-08-27T06:01:01.1234567+00:00\"," +
            "\"ready\":true}";
        var nonUtcJson = validJson.Replace("+00:00", "+08:00", StringComparison.Ordinal);
        var duplicateJson = validJson.Replace(
            "\"status\":\"ready\",",
            "\"status\":\"ready\",\"status\":\"ready\",",
            StringComparison.Ordinal);
        var probeScript =
            "$ErrorActionPreference = 'Stop'\n" +
            activationFunctions.Value + "\n" +
            $"$validJson = '{validJson}'\n" +
            "$valid = Read-ProductActivationReadyResponse $validJson\n" +
            $"if ($valid.installationId -ne [Guid]'{installationId:D}' -or " +
            "$valid.startedAtUtc.Offset -ne [TimeSpan]::Zero -or -not $valid.ready) { exit 11 }\n" +
            $"$invalidJson = '{nonUtcJson}'\n" +
            "$rejectedNonUtc = $false\n" +
            "try { Read-ProductActivationReadyResponse $invalidJson | Out-Null } " +
            "catch { $rejectedNonUtc = $_.Exception.Message -match 'UTC' }\n" +
            "if (-not $rejectedNonUtc) { exit 12 }\n" +
            $"$duplicateJson = '{duplicateJson}'\n" +
            "$rejectedDuplicate = $false\n" +
            "try { Read-ProductActivationReadyResponse $duplicateJson | Out-Null } " +
            "catch { $rejectedDuplicate = $_.Exception.Message -match '重複' }\n" +
            "if (-not $rejectedDuplicate) { exit 13 }\n" +
            "$serviceName = 'MuhunMCSV_ActivationProbe'\n" +
            "function Get-Service { [pscustomobject]@{ Status = 'Running' } }\n" +
            "function Read-SafeAsciiFile { param($Path, $Minimum, $Maximum, $Label) " +
            "if ($Path -like '*service-rest-token.v1') { return ('A' * 64) }; " +
            $"return '{installationId:D}' }}\n" +
            "function Invoke-WebRequest { [pscustomobject]@{ StatusCode = 200; " +
            "RawContentLength = [Text.Encoding]::UTF8.GetByteCount($validJson); Content = $validJson } }\n" +
            "$firstInstallIdentity = Wait-ProductActivationReady -DataRoot 'C:\\MCSV-Probe' -Port 39050 " +
            "-ExpectedVersion '1.0.0' -ExpectedInstallationId $null -TimeoutSeconds 10\n" +
            $"if ($firstInstallIdentity -ne [Guid]'{installationId:D}') {{ exit 14 }}\n" +
            "exit 0\n";
        var encodedProbe = Convert.ToBase64String(System.Text.Encoding.Unicode.GetBytes(probeScript));
        var startInfo = new System.Diagnostics.ProcessStartInfo
        {
            FileName = "pwsh.exe",
            UseShellExecute = false,
            CreateNoWindow = true,
            RedirectStandardOutput = true,
            RedirectStandardError = true,
        };
        startInfo.ArgumentList.Add("-NoLogo");
        startInfo.ArgumentList.Add("-NoProfile");
        startInfo.ArgumentList.Add("-NonInteractive");
        startInfo.ArgumentList.Add("-EncodedCommand");
        startInfo.ArgumentList.Add(encodedProbe);

        using var process = System.Diagnostics.Process.Start(startInfo)
            ?? throw new InvalidOperationException("Could not start the activation-ready JSON probe.");
        var standardOutput = process.StandardOutput.ReadToEnd();
        var standardError = process.StandardError.ReadToEnd();
        process.WaitForExit();
        Assert.True(
            process.ExitCode == 0,
            $"The real activation-ready JSON helper failed its UTC/shape regression. " +
            $"Exit={process.ExitCode}; stdout={standardOutput}; stderr={standardError}");
    }

    [Fact]
    public void ReleasePipelineProtectsPrivateKeySignsAndVerifiesBeforePublishing()
    {
        var identity = File.ReadAllText(Path.Combine(
            RepositoryRoot,
            "scripts",
            "New-MuhunMcsvSigningIdentity.ps1"));
        var release = File.ReadAllText(Path.Combine(
            RepositoryRoot,
            "scripts",
            "New-MuhunMcsvRelease.ps1"));
        var verifier = File.ReadAllText(Path.Combine(
            RepositoryRoot,
            "scripts",
            "Test-MuhunMcsvRelease.ps1"));
        var formalBuild = File.ReadAllText(Path.Combine(
            RepositoryRoot,
            "scripts",
            "Build-MuhunMcsvFormalRelease.ps1"));

        Assert.Contains("[ValidateRange(3072, 8192)]", identity, StringComparison.Ordinal);
        Assert.Contains("ConvertFrom-SecureString", identity, StringComparison.Ordinal);
        Assert.Contains("pfx-password.dpapi", identity, StringComparison.Ordinal);
        Assert.Contains("/inheritance:r", identity, StringComparison.Ordinal);
        Assert.Contains("outside the source repository", identity, StringComparison.Ordinal);
        Assert.DoesNotContain("Import-Certificate", identity, StringComparison.OrdinalIgnoreCase);
        Assert.DoesNotContain("Cert:\\LocalMachine\\Root", identity, StringComparison.OrdinalIgnoreCase);

        Assert.Contains("Set-AuthenticodeSignature", release, StringComparison.Ordinal);
        Assert.Contains("ConvertTo-ReleasePowerShellUtf8Bom", release, StringComparison.Ordinal);
        Assert.Contains("Assert-SignedReleasePowerShellScript", release, StringComparison.Ordinal);
        Assert.Contains("[Text.UTF8Encoding]::new($false, $true)", release, StringComparison.Ordinal);
        Assert.Contains("Authenticode signing changed PowerShell script content", release, StringComparison.Ordinal);
        Assert.Contains("$signatureText = $strictUtf8.GetString", release, StringComparison.Ordinal);
        Assert.Contains("# SIG # End signature block(?:\\r?\\n)?\\z", release, StringComparison.Ordinal);
        Assert.Contains("Management.Automation.Language.Parser]::ParseFile", release, StringComparison.Ordinal);
        Assert.Contains(
            "$unsignedScriptBytes = ConvertTo-ReleasePowerShellUtf8Bom -Path $destination",
            release,
            StringComparison.Ordinal);
        Assert.Contains(
            "Assert-SignedReleasePowerShellScript -Path $destination",
            release,
            StringComparison.Ordinal);
        Assert.Contains(
            "-ExpectedUnsignedBytes $unsignedScriptBytes",
            release,
            StringComparison.Ordinal);
        Assert.Contains("[IO.File]::Move($temporaryPath, $Path, $true)", release, StringComparison.Ordinal);
        Assert.Contains("Atomic replacement target is not a regular file", release, StringComparison.Ordinal);
        Assert.Contains("TimestampServer", release, StringComparison.Ordinal);
        Assert.Contains("RSASignaturePadding]::Pss", release, StringComparison.Ordinal);
        Assert.Contains("SHA256SUMS.txt", release, StringComparison.Ordinal);
        Assert.Contains("Test-MuhunMcsvRelease.ps1", release, StringComparison.Ordinal);
        Assert.Contains("RELEASE-FAILED.txt", release, StringComparison.Ordinal);
        Assert.DoesNotContain("-ExecutionPolicy Bypass", release, StringComparison.OrdinalIgnoreCase);
        Assert.DoesNotContain("Import-PfxCertificate", release, StringComparison.OrdinalIgnoreCase);

        Assert.Contains("FixedTimeEquals", verifier, StringComparison.Ordinal);
        Assert.Contains("return ,$payload", verifier, StringComparison.Ordinal);
        Assert.Contains("Get-AuthenticodeSignature", verifier, StringComparison.Ordinal);
        Assert.Contains("Assert-ReleasePowerShellScript", verifier, StringComparison.Ordinal);
        Assert.Contains("must use UTF-8 with BOM", verifier, StringComparison.Ordinal);
        Assert.Contains("does not parse after Authenticode signing", verifier, StringComparison.Ordinal);
        Assert.Contains("Management.Automation.Language.Parser]::ParseFile", verifier, StringComparison.Ordinal);
        Assert.Contains(
            "Assert-ReleasePowerShellScript -Path $path -Label $relativePath",
            verifier,
            StringComparison.Ordinal);
        Assert.Contains("$signatureMatch = [regex]::Match", verifier, StringComparison.Ordinal);
        Assert.Contains("Update package file hash is invalid", verifier, StringComparison.Ordinal);
        Assert.Contains("Release directory contains missing or unexpected files", verifier, StringComparison.Ordinal);

        Assert.Contains("--locked-mode", formalBuild, StringComparison.Ordinal);
        Assert.Contains("'--vulnerable'", formalBuild, StringComparison.Ordinal);
        Assert.Contains("'--include-transitive'", formalBuild, StringComparison.Ordinal);
        Assert.Contains("'--format', 'json'", formalBuild, StringComparison.Ordinal);
        Assert.Contains("ConvertFrom-Json -Depth 32", formalBuild, StringComparison.Ordinal);
        Assert.Contains("['vulnerabilities']", formalBuild, StringComparison.Ordinal);
        Assert.Contains("found vulnerable packages", formalBuild, StringComparison.Ordinal);
        Assert.Contains("@(Compare-Object $expectedTestProjects $actualTestProjects).Count", formalBuild, StringComparison.Ordinal);
        Assert.Contains("$formalAssemblyNames", formalBuild, StringComparison.Ordinal);
        Assert.Contains("Remove-PublishDebugArtifacts", formalBuild, StringComparison.Ordinal);
        Assert.DoesNotContain("-p:AssemblyName=", formalBuild, StringComparison.Ordinal);
        Assert.Contains("-p:TreatWarningsAsErrors=true", formalBuild, StringComparison.Ordinal);
        Assert.Contains("-p:Deterministic=true", formalBuild, StringComparison.Ordinal);
        Assert.Contains("New-MuhunMcsvRelease.ps1", formalBuild, StringComparison.Ordinal);
        Assert.Contains("Muhun MCSV Manager", formalBuild, StringComparison.Ordinal);
        Assert.DoesNotContain("ExecutionPolicy Bypass", formalBuild, StringComparison.OrdinalIgnoreCase);

        Assert.Contains("service-win-x64", release, StringComparison.Ordinal);
        Assert.Contains("update-signing-public-key.json", release, StringComparison.Ordinal);
        Assert.Contains("StableManifestUrl", release, StringComparison.Ordinal);
        Assert.Contains("AllowedFeedHosts", release, StringComparison.Ordinal);
        Assert.Contains("Service update feed/key configuration", verifier, StringComparison.Ordinal);
        Assert.Contains("Update package contains updater-owned installed-version metadata", verifier, StringComparison.Ordinal);
        Assert.Contains("Formal release contains a debug-symbol artifact", verifier, StringComparison.Ordinal);
        Assert.Contains("Payload contains a debug-symbol artifact", release, StringComparison.Ordinal);
        Assert.Contains("providers/muhun.catalog/muhun.catalog.mcsvp", verifier, StringComparison.Ordinal);
        Assert.Contains("Muhun.MCSV.BuiltinProvider.exe", release, StringComparison.Ordinal);
        Assert.Contains("Muhun.MCSV.BuiltinProvider.exe", verifier, StringComparison.Ordinal);
        Assert.Contains("$archive.Entries.Count -gt 10000", verifier, StringComparison.Ordinal);
        Assert.Contains("$totalUncompressedLength", verifier, StringComparison.Ordinal);
        Assert.Contains("$zipEntry.CompressedLength", verifier, StringComparison.Ordinal);
        Assert.Contains("[double]$zipEntry.Length / [double]$zipEntry.CompressedLength", verifier, StringComparison.Ordinal);
        Assert.Contains("$providerTotalUncompressedLength", verifier, StringComparison.Ordinal);
        Assert.Contains("[double]$entry.Length / [double]$entry.CompressedLength", verifier, StringComparison.Ordinal);
        Assert.Contains("$packageUri.Scheme -cne 'https'", verifier, StringComparison.Ordinal);
        Assert.Contains("$packageUri.Query", verifier, StringComparison.Ordinal);
        Assert.Contains("$expectedPackageUri.AbsoluteUri", verifier, StringComparison.Ordinal);
        Assert.Contains("$relative -ne 'installed-version.v1.json'", release, StringComparison.Ordinal);
        Assert.Contains("$installedMetadataDestination", File.ReadAllText(Path.Combine(
            RepositoryRoot,
            "scripts",
            "Install-MuhunMcsv.ps1")), StringComparison.Ordinal);

        foreach (var scriptName in new[]
                 {
                     "Install-MuhunMcsv.ps1",
                     "Uninstall-MuhunMcsv.ps1",
                     "Test-MuhunMcsvRelease.ps1"
                 })
        {
            Assert.StartsWith(
                "#requires -Version 7.4",
                File.ReadAllText(Path.Combine(RepositoryRoot, "scripts", scriptName)),
                StringComparison.Ordinal);
        }
    }

    [Fact]
    public void UninstallerKeepsDataUnlessExplicitlyRequestedAndUsesMarkers()
    {
        var source = File.ReadAllText(Path.Combine(RepositoryRoot, "scripts", "Uninstall-MuhunMcsv.ps1"));

        Assert.StartsWith("#requires -Version 7.4", source, StringComparison.Ordinal);
        Assert.Contains("[switch]$RemoveData", source, StringComparison.Ordinal);
        Assert.Contains(".muhun-mcsv-install-root", source, StringComparison.Ordinal);
        Assert.Contains(".muhun-mcsv-data-root", source, StringComparison.Ordinal);
        Assert.Contains("if ($RemoveData", source, StringComparison.Ordinal);
        Assert.Contains("Muhun MCSV GUI Activation Broker.lnk", source, StringComparison.Ordinal);
        Assert.Contains("Muhun MCSV Manager.lnk", source, StringComparison.Ordinal);
        Assert.Contains("FileAttributes]::ReparsePoint", source, StringComparison.Ordinal);
        Assert.Contains("Test-IsUnderRoot", source, StringComparison.Ordinal);
        Assert.Contains("Resolve-GuardedRoot $install", source, StringComparison.Ordinal);
        Assert.Contains("CloseMainWindow", source, StringComparison.Ordinal);
        Assert.Contains("managedVersionsPrefix", source, StringComparison.Ordinal);
    }

    private static string FindRepositoryRoot()
    {
        for (var directory = new DirectoryInfo(AppContext.BaseDirectory);
             directory is not null;
             directory = directory.Parent)
        {
            if (File.Exists(Path.Combine(directory.FullName, "MinecraftServerManager.sln")))
            {
                return directory.FullName;
            }
        }

        throw new DirectoryNotFoundException("Could not locate repository root.");
    }
}
