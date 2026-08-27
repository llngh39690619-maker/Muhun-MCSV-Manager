using System.Diagnostics;
using System.Runtime.InteropServices;

namespace MinecraftServerManager.Updater;

internal sealed record ProductExplorerLaunchCommand(
    string ExecutablePath,
    string Arguments,
    string WorkingDirectory,
    int WindowStyle);

internal interface IExplorerDesktopProcessLauncher
{
    void Launch(ProductExplorerLaunchCommand command);
}

/// <summary>
/// Starts the stable per-user activation broker through the desktop Explorer automation object.
/// The caller may be elevated, but Explorer owns the resulting process and therefore preserves
/// the identity and integrity level of the interactive desktop user.
/// </summary>
internal static class ProductExplorerGuiActivationLauncher
{
    public const string CommandName = "--start-gui-activation-broker";
    private const string InstallRootArgument = "--install-root";
    private const string BrokerCommandName = "--gui-activation-broker";
    private const string StableLauncherDirectoryName = "launcher";
    private const string StableLauncherFileName = "Muhun MCSV Updater.exe";
    private const int HiddenWindowStyle = 0;
    private const int MaximumCommandArgumentLength = 4 * 1024;

    public static bool IsCommand(string[]? args)
        => args is { Length: > 0 } &&
           string.Equals(args[0], CommandName, StringComparison.Ordinal);

    public static bool IsRequest(string[]? args)
        => args is { Length: 3 } &&
           string.Equals(args[0], CommandName, StringComparison.Ordinal) &&
           string.Equals(args[1], InstallRootArgument, StringComparison.Ordinal) &&
           !string.IsNullOrWhiteSpace(args[2]);

    public static int Run(
        string[] args,
        IExplorerDesktopProcessLauncher? processLauncher = null)
    {
        if (!IsRequest(args))
        {
            return 2;
        }

        try
        {
            if (!OperatingSystem.IsWindows() || Process.GetCurrentProcess().SessionId <= 0)
            {
                return 3;
            }

            var command = CreateLaunchCommand(args[2]);
            (processLauncher ?? new ExplorerDesktopProcessLauncher()).Launch(command);
            return 0;
        }
        catch (Exception exception) when (
            exception is not OutOfMemoryException and not StackOverflowException)
        {
            Console.Error.WriteLine(
                $"Unable to start the interactive GUI activation broker through Explorer: {exception.Message}");
            return 4;
        }
    }

    internal static ProductExplorerLaunchCommand CreateLaunchCommand(string installRoot)
    {
        var root = ProductGuiActivationBroker.ValidateInstallRoot(installRoot);
        if (!Directory.Exists(root) ||
            !Path.IsPathFullyQualified(root) ||
            root.Length > 1024 ||
            root.Any(character => char.IsControl(character) || character == '\0'))
        {
            throw new InvalidDataException("Managed install root is not safe for Explorer activation.");
        }

        var launcherDirectory = Path.GetFullPath(Path.Combine(root, StableLauncherDirectoryName));
        var launcherPath = ProductActivationPathPolicy.ValidateExecutable(
            Path.Combine(launcherDirectory, StableLauncherFileName),
            StableLauncherFileName);
        if (!string.Equals(
                Path.GetDirectoryName(launcherPath),
                launcherDirectory,
                StringComparison.OrdinalIgnoreCase) ||
            !string.Equals(
                Directory.GetParent(launcherDirectory)?.FullName,
                root,
                StringComparison.OrdinalIgnoreCase))
        {
            throw new InvalidDataException("Stable GUI activation launcher escaped its managed directory.");
        }

        var arguments = string.Concat(
            BrokerCommandName,
            " ",
            InstallRootArgument,
            " ",
            QuoteWindowsArgument(root));
        if (arguments.Length > MaximumCommandArgumentLength)
        {
            throw new InvalidDataException("GUI activation broker command is too long.");
        }

        return new ProductExplorerLaunchCommand(
            launcherPath,
            arguments,
            launcherDirectory,
            HiddenWindowStyle);
    }

    internal static string QuoteWindowsArgument(string value)
    {
        ArgumentNullException.ThrowIfNull(value);
        if (value.IndexOf('\0') >= 0)
        {
            throw new InvalidDataException("Windows process argument contains a null character.");
        }

        if (value.Length == 0)
        {
            return "\"\"";
        }

        if (!value.Any(character => char.IsWhiteSpace(character) || character == '"'))
        {
            return value;
        }

        var quoted = new System.Text.StringBuilder(value.Length + 2);
        quoted.Append('"');
        var slashCount = 0;
        foreach (var character in value)
        {
            if (character == '\\')
            {
                slashCount++;
                continue;
            }

            if (character == '"')
            {
                quoted.Append('\\', checked((slashCount * 2) + 1));
                quoted.Append('"');
                slashCount = 0;
                continue;
            }

            quoted.Append('\\', slashCount);
            slashCount = 0;
            quoted.Append(character);
        }

        quoted.Append('\\', checked(slashCount * 2));
        quoted.Append('"');
        return quoted.ToString();
    }
}

/// <summary>
/// Managed COM implementation of Microsoft's Execute-in-Explorer pattern. It obtains the
/// desktop ShellFolderView first and invokes ShellExecute only through that view's Application
/// object; it never instantiates Shell.Application and never manipulates an access token.
/// </summary>
internal sealed class ExplorerDesktopProcessLauncher : IExplorerDesktopProcessLauncher
{
    private const int DesktopShellWindowClass = 8;
    private const int NeedDispatchOption = 1;
    private const uint BackgroundViewObject = 0;
    private static readonly Guid TopLevelBrowserService =
        new("4C96BE40-915C-11CF-99D3-00AA004AE837");

    public void Launch(ProductExplorerLaunchCommand command)
    {
        ArgumentNullException.ThrowIfNull(command);
        if (!OperatingSystem.IsWindows())
        {
            throw new PlatformNotSupportedException(
                "Explorer desktop activation is available only on Windows.");
        }

        ValidateCommand(command);
        LaunchCore(command);
    }

    private static void LaunchCore(ProductExplorerLaunchCommand command)
    {
        object? shellWindowsObject = null;
        object? desktopWindowObject = null;
        object? shellBrowserObject = null;
        object? shellViewObject = null;
        object? folderViewObject = null;
        object? explorerApplicationObject = null;

        try
        {
            shellWindowsObject = Activator.CreateInstance(typeof(ShellWindowsComClass))
                ?? throw new InvalidOperationException("Windows ShellWindows automation is unavailable.");
            var shellWindows = (IShellWindows)shellWindowsObject;
            object desktopLocation = 0;
            object emptyLocationRoot = new object();
            desktopWindowObject = shellWindows.FindWindowSW(
                ref desktopLocation,
                ref emptyLocationRoot,
                DesktopShellWindowClass,
                out var desktopWindowHandle,
                NeedDispatchOption);
            if (desktopWindowObject is null || desktopWindowHandle == 0)
            {
                throw new InvalidOperationException("The interactive Explorer desktop was not found.");
            }

            var serviceProvider = (IComServiceProvider)desktopWindowObject;
            var service = TopLevelBrowserService;
            var browserInterface = typeof(IShellBrowser).GUID;
            shellBrowserObject = serviceProvider.QueryService(ref service, ref browserInterface);
            var shellBrowser = (IShellBrowser)shellBrowserObject;
            shellViewObject = shellBrowser.QueryActiveShellView();
            var shellView = (IShellView)shellViewObject;
            var dispatchInterface = typeof(IComDispatch).GUID;
            folderViewObject = shellView.GetItemObject(BackgroundViewObject, ref dispatchInterface);
            var folderView = (IShellFolderViewDual)folderViewObject;

            // This is deliberately the desktop ShellFolderView.Application automation object,
            // hosted by Explorer. Creating Shell.Application directly would bind to the caller's
            // security context and would not provide the required interactive-user handoff.
            explorerApplicationObject = folderView.Application;
            var explorerShell = (IShellDispatch2)explorerApplicationObject;
            explorerShell.ShellExecute(
                command.ExecutablePath,
                command.Arguments,
                command.WorkingDirectory,
                "open",
                command.WindowStyle);
        }
        finally
        {
            var released = new HashSet<object>(ReferenceEqualityComparer.Instance);
            FinalRelease(explorerApplicationObject, released);
            FinalRelease(folderViewObject, released);
            FinalRelease(shellViewObject, released);
            FinalRelease(shellBrowserObject, released);
            FinalRelease(desktopWindowObject, released);
            FinalRelease(shellWindowsObject, released);
        }
    }

    private static void ValidateCommand(ProductExplorerLaunchCommand command)
    {
        if (!Path.IsPathFullyQualified(command.ExecutablePath) ||
            !Path.IsPathFullyQualified(command.WorkingDirectory) ||
            !File.Exists(command.ExecutablePath) ||
            !Directory.Exists(command.WorkingDirectory) ||
            command.WindowStyle is < 0 or > 11 ||
            command.Arguments.Length is < 1 or > 4 * 1024 ||
            command.ExecutablePath.Any(char.IsControl) ||
            command.WorkingDirectory.Any(char.IsControl) ||
            command.Arguments.IndexOf('\0') >= 0)
        {
            throw new InvalidDataException("Explorer launch command is invalid.");
        }

        ProductActivationPathPolicy.RejectExistingReparsePoints(command.ExecutablePath);
        ProductActivationPathPolicy.RejectExistingReparsePoints(command.WorkingDirectory);
    }

    private static void FinalRelease(object? value, HashSet<object> released)
    {
        if (value is null || !released.Add(value))
        {
            return;
        }

        try
        {
            if (Marshal.IsComObject(value))
            {
                _ = Marshal.FinalReleaseComObject(value);
            }
        }
        catch (InvalidComObjectException)
        {
            // Two interface projections can share one RCW identity. A prior reverse-order
            // release already completed the cleanup in that case.
        }
    }

    [ComImport]
    [Guid("9BA05972-F6A8-11CF-A442-00A0C90A8F39")]
    [ClassInterface(ClassInterfaceType.None)]
    private sealed class ShellWindowsComClass;

    [ComImport]
    [Guid("85CB6900-4D95-11CF-960C-0080C7F4EE85")]
    [InterfaceType(ComInterfaceType.InterfaceIsIDispatch)]
    private interface IShellWindows
    {
        [return: MarshalAs(UnmanagedType.IDispatch)]
        object FindWindowSW(
            [MarshalAs(UnmanagedType.Struct)] ref object location,
            [MarshalAs(UnmanagedType.Struct)] ref object locationRoot,
            int shellWindowClass,
            out int windowHandle,
            int options);
    }

    [ComImport]
    [Guid("6D5140C1-7436-11CE-8034-00AA006009FA")]
    [InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
    private interface IComServiceProvider
    {
        [return: MarshalAs(UnmanagedType.Interface)]
        object QueryService(ref Guid service, ref Guid requestedInterface);
    }

    [ComImport]
    [Guid("000214E2-0000-0000-C000-000000000046")]
    [InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
    private interface IShellBrowser
    {
        void Slot01();
        void Slot02();
        void Slot03();
        void Slot04();
        void Slot05();
        void Slot06();
        void Slot07();
        void Slot08();
        void Slot09();
        void Slot10();
        void Slot11();
        void Slot12();
        IShellView QueryActiveShellView();
    }

    [ComImport]
    [Guid("000214E3-0000-0000-C000-000000000046")]
    [InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
    private interface IShellView
    {
        void Slot01();
        void Slot02();
        void Slot03();
        void Slot04();
        void Slot05();
        void Slot06();
        void Slot07();
        void Slot08();
        void Slot09();
        void Slot10();
        void Slot11();
        void Slot12();

        [return: MarshalAs(UnmanagedType.Interface)]
        object GetItemObject(uint aspectOfView, ref Guid requestedInterface);
    }

    [ComImport]
    [Guid("00020400-0000-0000-C000-000000000046")]
    [InterfaceType(ComInterfaceType.InterfaceIsIDispatch)]
    private interface IComDispatch;

    [ComImport]
    [Guid("E7A1AF80-4D96-11CF-960C-0080C7F4EE85")]
    [InterfaceType(ComInterfaceType.InterfaceIsIDispatch)]
    private interface IShellFolderViewDual
    {
        object Application
        {
            [return: MarshalAs(UnmanagedType.IDispatch)]
            get;
        }
    }

    [ComImport]
    [Guid("A4C6892C-3BA9-11D2-9DEA-00C04FB16162")]
    [InterfaceType(ComInterfaceType.InterfaceIsIDispatch)]
    private interface IShellDispatch2
    {
        void ShellExecute(
            [MarshalAs(UnmanagedType.BStr)] string file,
            [MarshalAs(UnmanagedType.Struct)] object arguments,
            [MarshalAs(UnmanagedType.Struct)] object directory,
            [MarshalAs(UnmanagedType.Struct)] object operation,
            [MarshalAs(UnmanagedType.Struct)] object show);
    }
}
