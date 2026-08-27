using System.Diagnostics;
using System.IO.Pipes;
using System.Security.AccessControl;
using System.Security.Principal;
using System.Text.Json;
using System.Text.Json.Serialization;
using MinecraftServerManager.Contracts;

namespace MinecraftServerManager.Updater;

internal sealed record ProductGuiActivationRequest(
    int SchemaVersion,
    string GuiExecutablePath,
    string ExpectedVersion,
    string Nonce);

internal sealed record ProductGuiActivationResponse(
    bool Accepted,
    int SessionId,
    int ProcessId,
    string Version,
    string Nonce,
    string? ErrorCode = null);

internal static class ProductGuiActivationProtocol
{
    public const int SchemaVersion = 1;
    public const string PipeName = "Muhun.MCSV.GuiActivation.v1";
    public const int MaximumFrameBytes = 8 * 1024;
    private static readonly JsonSerializerOptions StrictJson = new(JsonSerializerDefaults.Web)
    {
        PropertyNameCaseInsensitive = false,
        UnmappedMemberHandling = JsonUnmappedMemberHandling.Disallow,
    };

    public static async Task WriteAsync<T>(
        Stream stream,
        T value,
        CancellationToken cancellationToken)
    {
        var payload = JsonSerializer.SerializeToUtf8Bytes(value, StrictJson);
        if (payload.Length is < 2 or > MaximumFrameBytes)
        {
            throw new InvalidDataException("GUI activation frame has an invalid size.");
        }

        var length = BitConverter.GetBytes(payload.Length);
        await stream.WriteAsync(length, cancellationToken).ConfigureAwait(false);
        await stream.WriteAsync(payload, cancellationToken).ConfigureAwait(false);
        await stream.FlushAsync(cancellationToken).ConfigureAwait(false);
    }

    public static async Task<T> ReadAsync<T>(Stream stream, CancellationToken cancellationToken)
    {
        var lengthBytes = new byte[sizeof(int)];
        await stream.ReadExactlyAsync(lengthBytes, cancellationToken).ConfigureAwait(false);
        var length = BitConverter.ToInt32(lengthBytes);
        if (length is < 2 or > MaximumFrameBytes)
        {
            throw new InvalidDataException("GUI activation frame has an invalid size.");
        }

        var payload = GC.AllocateUninitializedArray<byte>(length);
        await stream.ReadExactlyAsync(payload, cancellationToken).ConfigureAwait(false);
        return JsonSerializer.Deserialize<T>(payload, StrictJson)
            ?? throw new InvalidDataException("GUI activation frame is empty.");
    }
}

/// <summary>
/// Stable, per-user activation broker. It is copied outside version directories and starts at
/// user logon, so a Service-session updater never attempts to create a window in Session 0.
/// </summary>
internal static class ProductGuiActivationBroker
{
    private static readonly TimeSpan RequestTimeout = TimeSpan.FromSeconds(90);
    private static readonly TimeSpan GuiInputIdleTimeout = TimeSpan.FromSeconds(25);
    private static readonly TimeSpan GuiStabilityWindow = TimeSpan.FromSeconds(2);
    private static readonly TimeSpan GuiHandoffTimeout = TimeSpan.FromSeconds(20);

    public static bool IsBrokerRequest(string[] args)
        => args is { Length: 3 } &&
           string.Equals(args[0], "--gui-activation-broker", StringComparison.Ordinal) &&
           string.Equals(args[1], "--install-root", StringComparison.Ordinal);

    public static bool IsActivateCurrentRequest(string[] args)
        => args is { Length: 3 } &&
           string.Equals(args[0], "--activate-current", StringComparison.Ordinal) &&
           string.Equals(args[1], "--install-root", StringComparison.Ordinal);

    public static async Task<int> RunAsync(string[] args, CancellationToken cancellationToken = default)
    {
        if (!IsBrokerRequest(args))
        {
            return 2;
        }

        try
        {
            if (!OperatingSystem.IsWindows() || Process.GetCurrentProcess().SessionId <= 0)
            {
                return 3;
            }

            using var identity = WindowsIdentity.GetCurrent();
            var principal = new WindowsPrincipal(identity);
            if (principal.IsInRole(WindowsBuiltInRole.Administrator))
            {
                // The broker is the only process permitted to create product windows during an
                // update. Refuse an elevated token so the installer cannot accidentally turn the
                // normal desktop GUI into an administrator process.
                return 3;
            }

            var installRoot = ValidateInstallRoot(args[2]);
            using var instanceLock = new Mutex(
                initiallyOwned: true,
                "Local\\Muhun.MCSV.GuiActivationBroker.v1",
                out var createdNew);
            if (!createdNew)
            {
                return 0;
            }

            while (!cancellationToken.IsCancellationRequested)
            {
                await using var pipe = CreateServer();
                await pipe.WaitForConnectionAsync(cancellationToken).ConfigureAwait(false);
                using var deadline = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
                deadline.CancelAfter(RequestTimeout);
                await HandleRequestAsync(pipe, installRoot, deadline.Token).ConfigureAwait(false);
            }

            return 0;
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
            return 0;
        }
        catch (Exception exception) when (exception is not OutOfMemoryException and not StackOverflowException)
        {
            return 4;
        }
    }

    public static async Task<int> LaunchCurrentAsync(
        string installRoot,
        CancellationToken cancellationToken = default)
    {
        try
        {
            var root = ValidateInstallRoot(installRoot);
            var activeVersion = ReadActiveVersion(root);
            var guiPath = ResolveActiveGui(root, activeVersion);
            using var process = await AcquireGuiAsync(root, guiPath, activeVersion, cancellationToken)
                .ConfigureAwait(false);
            await WaitForGuiAcknowledgementAsync(process, cancellationToken).ConfigureAwait(false);
            return 0;
        }
        catch (Exception exception) when (exception is not OutOfMemoryException and not StackOverflowException)
        {
            return 4;
        }
    }

    /// <summary>
    /// Installer-side end-to-end proof that the stable interactive broker can start the active
    /// GUI and receive its initialized, Service-compatible acknowledgement. This intentionally
    /// uses the same authenticated broker protocol as a Session 0 updater.
    /// </summary>
    public static async Task<int> ActivateCurrentAsync(
        string installRoot,
        CancellationToken cancellationToken = default)
    {
        try
        {
            var root = ValidateInstallRoot(installRoot);
            var activeVersion = ReadActiveVersion(root);
            var guiPath = ResolveActiveGui(root, activeVersion);
            var acknowledgement = await new ProductWindowsServicePlatform()
                .RequestGuiActivationAsync(guiPath, activeVersion, cancellationToken)
                .ConfigureAwait(false);
            return acknowledgement.SessionId == Process.GetCurrentProcess().SessionId ? 0 : 4;
        }
        catch (Exception exception) when (exception is not OutOfMemoryException and not StackOverflowException)
        {
            return 4;
        }
    }

    private static async Task HandleRequestAsync(
        NamedPipeServerStream pipe,
        string installRoot,
        CancellationToken cancellationToken)
    {
        ProductGuiActivationRequest? request = null;
        try
        {
            request = await ProductGuiActivationProtocol
                .ReadAsync<ProductGuiActivationRequest>(pipe, cancellationToken)
                .ConfigureAwait(false);
            ValidateRequest(request);
            var activeVersion = ReadActiveVersion(installRoot);
            if (!string.Equals(activeVersion, request.ExpectedVersion, StringComparison.Ordinal))
            {
                throw new InvalidOperationException("Requested version is not the active version.");
            }

            var guiPath = ResolveActiveGui(installRoot, activeVersion);
            if (!string.Equals(
                    guiPath,
                    Path.GetFullPath(request.GuiExecutablePath),
                    StringComparison.OrdinalIgnoreCase))
            {
                throw new InvalidDataException("Requested GUI path does not match the active version.");
            }

            using var process = await AcquireActivatedGuiAsync(
                    installRoot,
                    guiPath,
                    activeVersion,
                    request.Nonce,
                    cancellationToken)
                .ConfigureAwait(false);
            await ProductGuiActivationProtocol.WriteAsync(
                    pipe,
                    new ProductGuiActivationResponse(
                        true,
                        process.SessionId,
                        process.Id,
                        activeVersion,
                        request.Nonce),
                    cancellationToken)
                .ConfigureAwait(false);
        }
        catch (Exception exception) when (
            exception is not (OperationCanceledException or OutOfMemoryException or StackOverflowException))
        {
            if (pipe.IsConnected)
            {
                await ProductGuiActivationProtocol.WriteAsync(
                        pipe,
                        new ProductGuiActivationResponse(
                            false,
                            0,
                            0,
                            request?.ExpectedVersion ?? string.Empty,
                            request?.Nonce ?? string.Empty,
                            "gui.activation_rejected"),
                        cancellationToken)
                    .ConfigureAwait(false);
            }
        }
    }

    private static NamedPipeServerStream CreateServer()
    {
        var currentSid = WindowsIdentity.GetCurrent().User
            ?? throw new InvalidOperationException("Interactive user does not have a SID.");
        var serviceSid = (SecurityIdentifier)new NTAccount("NT SERVICE", "MuhunMCSV")
            .Translate(typeof(SecurityIdentifier));
        var administratorsSid = new SecurityIdentifier(
            WellKnownSidType.BuiltinAdministratorsSid,
            null);
        var security = new PipeSecurity();
        security.SetAccessRuleProtection(isProtected: true, preserveInheritance: false);
        security.SetOwner(currentSid);
        security.AddAccessRule(new PipeAccessRule(
            currentSid,
            PipeAccessRights.FullControl,
            AccessControlType.Allow));
        security.AddAccessRule(new PipeAccessRule(
            serviceSid,
            PipeAccessRights.ReadWrite,
            AccessControlType.Allow));
        security.AddAccessRule(new PipeAccessRule(
            administratorsSid,
            PipeAccessRights.FullControl,
            AccessControlType.Allow));
        return NamedPipeServerStreamAcl.Create(
            ProductGuiActivationProtocol.PipeName,
            PipeDirection.InOut,
            1,
            PipeTransmissionMode.Byte,
            PipeOptions.Asynchronous | PipeOptions.WriteThrough | PipeOptions.FirstPipeInstance,
            8 * 1024,
            8 * 1024,
            security,
            HandleInheritability.None,
            (PipeAccessRights)0);
    }

    private static async Task<Process> AcquireGuiAsync(
        string installRoot,
        string path,
        string expectedVersion,
        CancellationToken cancellationToken)
    {
        var actualVersion = NormalizeVersion(FileVersionInfo.GetVersionInfo(path).ProductVersion);
        if (!string.Equals(actualVersion, expectedVersion, StringComparison.Ordinal))
        {
            throw new InvalidDataException("Active GUI ProductVersion does not match its version directory.");
        }

        Process? currentTarget = null;
        foreach (var candidate in Process.GetProcessesByName("Muhun MCSV Manager"))
        {
            if (candidate.SessionId != Process.GetCurrentProcess().SessionId || candidate.HasExited)
            {
                candidate.Dispose();
                continue;
            }

            string candidatePath;
            try
            {
                candidatePath = candidate.MainModule?.FileName ?? string.Empty;
            }
            catch (Exception exception) when (exception is InvalidOperationException or System.ComponentModel.Win32Exception)
            {
                candidate.Dispose();
                continue;
            }

            if (!IsManagedGuiPath(candidatePath, installRoot))
            {
                candidate.Dispose();
                continue;
            }

            if (string.Equals(candidatePath, path, StringComparison.OrdinalIgnoreCase))
            {
                currentTarget ??= candidate;
                if (!ReferenceEquals(currentTarget, candidate))
                {
                    candidate.Dispose();
                }

                continue;
            }

            try
            {
                if (!candidate.CloseMainWindow())
                {
                    throw new InvalidOperationException(
                        "Previous GUI did not acknowledge the graceful A/B handoff request.");
                }

                using var handoffDeadline = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
                handoffDeadline.CancelAfter(GuiHandoffTimeout);
                await candidate.WaitForExitAsync(handoffDeadline.Token).ConfigureAwait(false);
            }
            catch (OperationCanceledException) when (!cancellationToken.IsCancellationRequested)
            {
                throw new TimeoutException("Previous GUI did not exit before the A/B handoff deadline.");
            }
            finally
            {
                candidate.Dispose();
            }
        }

        if (currentTarget is not null)
        {
            return currentTarget;
        }

        return Process.Start(new ProcessStartInfo
        {
            FileName = path,
            WorkingDirectory = Path.GetDirectoryName(path)!,
            UseShellExecute = false,
            CreateNoWindow = false,
        }) ?? throw new InvalidOperationException("Active GUI process could not be started.");
    }

    private static async Task<Process> AcquireActivatedGuiAsync(
        string installRoot,
        string path,
        string expectedVersion,
        string nonce,
        CancellationToken cancellationToken)
    {
        var actualVersion = NormalizeVersion(FileVersionInfo.GetVersionInfo(path).ProductVersion);
        if (!string.Equals(actualVersion, expectedVersion, StringComparison.Ordinal) ||
            nonce.Length != 64 || nonce.Any(character => !Uri.IsHexDigit(character)))
        {
            throw new InvalidDataException("Activated GUI identity binding is invalid.");
        }

        await CloseAllManagedGuisAsync(installRoot, cancellationToken).ConfigureAwait(false);
        var pipeName = ProductGuiActivationAcknowledgement.PipePrefix +
                       Convert.ToHexString(
                           System.Security.Cryptography.RandomNumberGenerator.GetBytes(16));
        await using var readyPipe = new NamedPipeServerStream(
            pipeName,
            PipeDirection.In,
            1,
            PipeTransmissionMode.Byte,
            PipeOptions.Asynchronous |
            PipeOptions.WriteThrough |
            PipeOptions.CurrentUserOnly |
            PipeOptions.FirstPipeInstance,
            8 * 1024,
            8 * 1024);
        var startInfo = new ProcessStartInfo
        {
            FileName = path,
            WorkingDirectory = Path.GetDirectoryName(path)!,
            UseShellExecute = false,
            CreateNoWindow = false,
        };
        startInfo.ArgumentList.Add(ProductGuiActivationAcknowledgement.PipeArgument);
        startInfo.ArgumentList.Add(pipeName);
        startInfo.ArgumentList.Add(ProductGuiActivationAcknowledgement.NonceArgument);
        startInfo.ArgumentList.Add(nonce);
        startInfo.ArgumentList.Add(ProductGuiActivationAcknowledgement.VersionArgument);
        startInfo.ArgumentList.Add(expectedVersion);
        var process = Process.Start(startInfo)
            ?? throw new InvalidOperationException("Active GUI process could not be started.");
        try
        {
            await WaitForGuiAcknowledgementAsync(process, cancellationToken).ConfigureAwait(false);
            await WaitForExplicitGuiReadyAsync(
                    readyPipe,
                    process,
                    expectedVersion,
                    nonce,
                    RequestTimeout,
                    cancellationToken)
                .ConfigureAwait(false);

            return process;
        }
        catch
        {
            await TerminateUnreadyGuiAsync(process).ConfigureAwait(false);
            process.Dispose();
            throw;
        }
    }

    private static async Task TerminateUnreadyGuiAsync(Process process)
    {
        try
        {
            if (process.HasExited)
            {
                return;
            }

            if (process.CloseMainWindow())
            {
                using var closeDeadline = new CancellationTokenSource(TimeSpan.FromSeconds(5));
                try
                {
                    await process.WaitForExitAsync(closeDeadline.Token).ConfigureAwait(false);
                    return;
                }
                catch (OperationCanceledException)
                {
                }
            }

            // This process was created by this broker for this one activation request and never
            // produced the initialized readiness ACK. Terminating only that exact child releases
            // the cross-version GUI lock so the verified previous version can be restored.
            process.Kill(entireProcessTree: false);
            using var killDeadline = new CancellationTokenSource(TimeSpan.FromSeconds(5));
            await process.WaitForExitAsync(killDeadline.Token).ConfigureAwait(false);
        }
        catch (Exception exception) when (
            exception is InvalidOperationException or System.ComponentModel.Win32Exception or
                         OperationCanceledException)
        {
            // The rollback path still attempts a formal old-version activation and fails closed
            // if the stale process retained the single-instance lock.
        }
    }

    internal static async Task WaitForExplicitGuiReadyAsync(
        NamedPipeServerStream readyPipe,
        Process process,
        string expectedVersion,
        string nonce,
        TimeSpan timeout,
        CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(readyPipe);
        ArgumentNullException.ThrowIfNull(process);
        ProductUpdateManifestParser.ValidateVersion(expectedVersion);
        if (nonce.Length != 64 || nonce.Any(character => !Uri.IsHexDigit(character)) ||
            timeout <= TimeSpan.Zero || timeout > TimeSpan.FromMinutes(2))
        {
            throw new InvalidDataException("GUI readiness acknowledgement binding is invalid.");
        }

        using var readyDeadline = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        readyDeadline.CancelAfter(timeout);
        try
        {
            await readyPipe.WaitForConnectionAsync(readyDeadline.Token).ConfigureAwait(false);
            var acknowledgement = await ProductGuiActivationProtocol
                .ReadAsync<ProductGuiReadyAcknowledgement>(readyPipe, readyDeadline.Token)
                .ConfigureAwait(false);
            if (acknowledgement.SchemaVersion != ProductGuiActivationAcknowledgement.SchemaVersion ||
                !acknowledgement.Ready ||
                acknowledgement.ProcessId != process.Id ||
                acknowledgement.SessionId != process.SessionId ||
                acknowledgement.SessionId <= 0 ||
                acknowledgement.ApiVersion.CompareTo(ProductApiProtocol.MinimumSupportedVersion) < 0 ||
                acknowledgement.ApiVersion.CompareTo(ProductApiProtocol.CurrentVersion) > 0 ||
                !string.Equals(acknowledgement.Version, expectedVersion, StringComparison.Ordinal) ||
                !string.Equals(acknowledgement.Nonce, nonce, StringComparison.Ordinal) ||
                process.HasExited)
            {
                throw new InvalidOperationException(
                    "GUI readiness acknowledgement did not match its activation request.");
            }
        }
        catch (OperationCanceledException) when (!cancellationToken.IsCancellationRequested)
        {
            throw new TimeoutException(
                "GUI did not acknowledge initialized Service-compatible readiness before the deadline.");
        }
    }

    private static async Task CloseAllManagedGuisAsync(
        string installRoot,
        CancellationToken cancellationToken)
    {
        foreach (var candidate in Process.GetProcessesByName("Muhun MCSV Manager"))
        {
            try
            {
                if (candidate.SessionId != Process.GetCurrentProcess().SessionId || candidate.HasExited)
                {
                    continue;
                }

                string candidatePath;
                try
                {
                    candidatePath = candidate.MainModule?.FileName ?? string.Empty;
                }
                catch (Exception exception) when (
                    exception is InvalidOperationException or System.ComponentModel.Win32Exception)
                {
                    continue;
                }

                if (!IsManagedGuiPath(candidatePath, installRoot))
                {
                    continue;
                }

                if (!candidate.CloseMainWindow())
                {
                    throw new InvalidOperationException(
                        "Previous GUI did not acknowledge the graceful A/B handoff request.");
                }

                using var handoffDeadline = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
                handoffDeadline.CancelAfter(GuiHandoffTimeout);
                try
                {
                    await candidate.WaitForExitAsync(handoffDeadline.Token).ConfigureAwait(false);
                }
                catch (OperationCanceledException) when (!cancellationToken.IsCancellationRequested)
                {
                    throw new TimeoutException(
                        "Previous GUI did not exit before the A/B handoff deadline.");
                }
            }
            finally
            {
                candidate.Dispose();
            }
        }
    }

    private static bool IsManagedGuiPath(string candidatePath, string installRoot)
    {
        if (string.IsNullOrWhiteSpace(candidatePath) || !Path.IsPathFullyQualified(candidatePath))
        {
            return false;
        }

        var fullPath = Path.GetFullPath(candidatePath);
        var guiDirectory = Directory.GetParent(fullPath);
        var versionDirectory = guiDirectory?.Parent;
        var versionsDirectory = versionDirectory?.Parent;
        if (guiDirectory is null || versionDirectory is null || versionsDirectory is null ||
            !string.Equals(guiDirectory.Name, "gui-win-x64", StringComparison.OrdinalIgnoreCase) ||
            !string.Equals(versionsDirectory.Name, "versions", StringComparison.OrdinalIgnoreCase) ||
            !string.Equals(
                versionsDirectory.FullName,
                Path.Combine(Path.GetFullPath(installRoot), "versions"),
                StringComparison.OrdinalIgnoreCase) ||
            !string.Equals(
                Path.GetFileName(fullPath),
                "Muhun MCSV Manager.exe",
                StringComparison.OrdinalIgnoreCase))
        {
            return false;
        }

        try
        {
            ProductUpdateManifestParser.ValidateVersion(versionDirectory.Name);
            ProductActivationPathPolicy.RejectExistingReparsePoints(fullPath);
            return true;
        }
        catch (InvalidDataException)
        {
            return false;
        }
    }

    private static async Task WaitForGuiAcknowledgementAsync(
        Process process,
        CancellationToken cancellationToken)
    {
        if (process.SessionId <= 0)
        {
            throw new InvalidOperationException("GUI was not launched in an interactive session.");
        }

        var inputIdle = await Task.Run(
                () => process.WaitForInputIdle(checked((int)GuiInputIdleTimeout.TotalMilliseconds)),
                cancellationToken)
            .ConfigureAwait(false);
        if (!inputIdle || process.HasExited)
        {
            throw new InvalidOperationException("GUI did not acknowledge an interactive message loop.");
        }

        await Task.Delay(GuiStabilityWindow, cancellationToken).ConfigureAwait(false);
        if (process.HasExited)
        {
            throw new InvalidOperationException("GUI exited during its activation stability window.");
        }
    }

    private static void ValidateRequest(ProductGuiActivationRequest request)
    {
        if (request.SchemaVersion != ProductGuiActivationProtocol.SchemaVersion ||
            string.IsNullOrWhiteSpace(request.GuiExecutablePath) ||
            !Path.IsPathFullyQualified(request.GuiExecutablePath) ||
            request.GuiExecutablePath.Length > 1024 ||
            request.Nonce.Length != 64 ||
            request.Nonce.Any(character => !Uri.IsHexDigit(character)))
        {
            throw new InvalidDataException("GUI activation request is invalid.");
        }

        ProductUpdateManifestParser.ValidateVersion(request.ExpectedVersion);
    }

    internal static string ValidateInstallRoot(string path)
    {
        if (!Path.IsPathFullyQualified(path))
        {
            throw new InvalidDataException("Install root must be absolute.");
        }

        var root = Path.GetFullPath(path).TrimEnd(
            Path.DirectorySeparatorChar,
            Path.AltDirectorySeparatorChar);
        if (root.StartsWith(@"\\", StringComparison.Ordinal) || root.IndexOf('"') >= 0)
        {
            throw new InvalidDataException("Install root must be a safe local path.");
        }

        ProductActivationPathPolicy.RejectExistingReparsePoints(root);
        var marker = Path.Combine(root, ".muhun-mcsv-install-root");
        if (!File.Exists(marker) ||
            (File.GetAttributes(marker) & FileAttributes.ReparsePoint) != 0 ||
            new FileInfo(marker).Length is < 1 or > 64 ||
            !string.Equals(File.ReadAllText(marker).Trim(), "muhun.mcsv.manager:1", StringComparison.Ordinal))
        {
            throw new InvalidDataException("Managed install root marker is invalid.");
        }

        return root;
    }

    private static string ReadActiveVersion(string installRoot)
    {
        var pointer = Path.Combine(installRoot, "active-version.v1");
        ProductActivationPathPolicy.RejectExistingReparsePoints(pointer);
        if (!File.Exists(pointer) ||
            (File.GetAttributes(pointer) & FileAttributes.ReparsePoint) != 0 ||
            new FileInfo(pointer).Length is < 1 or > 128)
        {
            throw new InvalidDataException("Active-version pointer is missing or invalid.");
        }

        var version = File.ReadAllText(pointer).Trim();
        ProductUpdateManifestParser.ValidateVersion(version);
        return version;
    }

    private static string ResolveActiveGui(string installRoot, string version)
    {
        var versionsRoot = Path.Combine(installRoot, "versions") + Path.DirectorySeparatorChar;
        var versionRoot = Path.GetFullPath(Path.Combine(versionsRoot, version));
        if (!versionRoot.StartsWith(versionsRoot, StringComparison.OrdinalIgnoreCase))
        {
            throw new InvalidDataException("Active version escaped the managed versions root.");
        }

        return ProductActivationPathPolicy.ValidateExecutable(
            Path.Combine(versionRoot, "gui-win-x64", "Muhun MCSV Manager.exe"),
            "Muhun MCSV Manager.exe");
    }

    private static string NormalizeVersion(string? value)
    {
        var version = value?.Split('+', 2)[0] ?? string.Empty;
        ProductUpdateManifestParser.ValidateVersion(version);
        return version;
    }
}
