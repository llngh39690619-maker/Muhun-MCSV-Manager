using System.ComponentModel;
using System.Runtime.ExceptionServices;
using System.Runtime.InteropServices;
using System.Text;
using System.Threading;
using System.Windows;
using System.Windows.Threading;

namespace MinecraftServerManager.App.Tests;

internal static class WpfStaTestHost
{
    private const uint DesktopReadObjects = 0x0001;
    private const string PrivateDesktopPrefix = "X-MCSV-Tests-";
    private const int UoiName = 2;
    private static readonly HashSet<string> InteractiveDesktopNames = new(
        ["Default", "Disconnect", "Screen-saver", "Winlogon"],
        StringComparer.OrdinalIgnoreCase);
    private static readonly Lazy<Host> Shared = new(
        () => new Host(),
        LazyThreadSafetyMode.ExecutionAndPublication);

    public static void Run(Action action)
    {
        ArgumentNullException.ThrowIfNull(action);
        Shared.Value.Run(action);
    }

    internal static bool IsIsolatedDesktop => Shared.Value.IsIsolatedDesktop;

    private sealed class Host
    {
        private readonly Dispatcher _dispatcher;
        private ExceptionDispatchInfo? _initializationFailure;

        public bool IsIsolatedDesktop { get; private set; }

        public Host()
        {
            using var ready = new ManualResetEventSlim();
            Dispatcher? dispatcher = null;
            var thread = new Thread(() =>
            {
                try
                {
                    VerifyIsolatedProcessDesktop();
                    IsIsolatedDesktop = true;
                    dispatcher = Dispatcher.CurrentDispatcher;
                    // Automated WPF windows must never interrupt a foreground game or appear on
                    // whichever monitor happens to contain the test runner's mouse cursor.
                    MinecraftServerManager.App.Infrastructure.PrimaryDisplayWindowPlacement
                        .SuppressActivationForNewWindows = true;
                    // App.InitializeComponent loads BAML whose x:Class is the concrete App type.
                    // Calling it on an App subclass fails before any resources are available.
                    // We deliberately run only the Dispatcher (not Application.Run), so WPF never
                    // raises Startup and the production App.OnStartup composition root cannot run.
                    var application = new MinecraftServerManager.App.App
                    {
                        ShutdownMode = ShutdownMode.OnExplicitShutdown
                    };
                    application.InitializeComponent();
                }
                catch (Exception exception)
                {
                    _initializationFailure = ExceptionDispatchInfo.Capture(exception);
                }

                if (_initializationFailure is not null)
                {
                    ready.Set();
                    return;
                }

                // InitializeComponent can queue deferred WPF resource work. Publishing the host
                // before that work drains lets a test replace an application resource and then
                // have the queued initialization restore the XAML default underneath it. Signal
                // readiness only after all higher-priority startup work has crossed this idle
                // barrier; the Dispatcher remains alive for the shared process-global test host.
                _ = dispatcher!.BeginInvoke(
                    new Action(ready.Set),
                    DispatcherPriority.ContextIdle);
                Dispatcher.Run();
            })
            {
                IsBackground = true,
                Name = "MinecraftServerManager.App.Tests.WPF.STA"
            };
            thread.SetApartmentState(ApartmentState.STA);
            thread.Start();

            if (!ready.Wait(TimeSpan.FromSeconds(15)))
            {
                throw new TimeoutException("WPF STA test host initialization timed out.");
            }

            _initializationFailure?.Throw();
            _dispatcher = dispatcher
                ?? throw new InvalidOperationException("WPF STA dispatcher was not initialized.");
        }

        public void Run(Action action)
        {
            _initializationFailure?.Throw();
            _dispatcher.Invoke(
                () =>
                {
                    // App resources are process-global and individual tests may exercise startup
                    // state. Reassert this guard at every test boundary before any Window exists.
                    MinecraftServerManager.App.Infrastructure.PrimaryDisplayWindowPlacement
                        .SuppressActivationForNewWindows = true;
                    action();
                },
                DispatcherPriority.Send);
        }

        private static void VerifyIsolatedProcessDesktop()
        {
            var expected = Environment.GetEnvironmentVariable("X_MCSV_ISOLATED_TEST_DESKTOP");
            var desktop = GetThreadDesktop(GetCurrentThreadId());
            if (desktop == IntPtr.Zero)
            {
                throw new Win32Exception(
                    Marshal.GetLastWin32Error(),
                    "無法取得 WPF 測試執行緒的 Windows desktop。");
            }

            var actual = GetDesktopName(desktop, "WPF 測試執行緒 desktop");
            var inputDesktop = OpenInputDesktop(0, false, DesktopReadObjects);
            if (inputDesktop == IntPtr.Zero)
            {
                throw new Win32Exception(
                    Marshal.GetLastWin32Error(),
                    "無法開啟目前可見的 Windows input desktop 以驗證 WPF 測試隔離。");
            }

            string inputDesktopName;
            try
            {
                inputDesktopName = GetDesktopName(inputDesktop, "目前可見的 Windows input desktop");
            }
            finally
            {
                _ = CloseDesktop(inputDesktop);
            }

            ValidateIsolatedDesktopNames(expected, actual, inputDesktopName);
        }

        private static string GetDesktopName(IntPtr desktop, string description)
        {
            _ = GetUserObjectInformation(desktop, UoiName, null, 0, out var requiredBytes);
            if (requiredBytes <= sizeof(char))
            {
                throw new Win32Exception(
                    Marshal.GetLastWin32Error(),
                    $"無法取得{description}名稱長度。");
            }

            var name = new StringBuilder(requiredBytes / sizeof(char));
            if (!GetUserObjectInformation(
                    desktop,
                    UoiName,
                    name,
                    requiredBytes,
                    out _))
            {
                throw new Win32Exception(
                    Marshal.GetLastWin32Error(),
                    $"無法讀取{description}名稱。");
            }

            return name.ToString();
        }
    }

    internal static void ValidateIsolatedDesktopNames(
        string? expected,
        string? actual,
        string? inputDesktop)
    {
        if (string.IsNullOrWhiteSpace(expected))
        {
            throw new InvalidOperationException(
                "WPF tests must be launched through Invoke-IsolatedDesktopProcess.ps1.");
        }

        if (!expected.StartsWith(PrivateDesktopPrefix, StringComparison.Ordinal)
            || InteractiveDesktopNames.Contains(expected))
        {
            throw new InvalidOperationException(
                $"WPF test desktop '{expected}' is not a private X MCSV test desktop.");
        }

        if (string.IsNullOrWhiteSpace(actual)
            || InteractiveDesktopNames.Contains(actual)
            || !actual.StartsWith(PrivateDesktopPrefix, StringComparison.Ordinal))
        {
            throw new InvalidOperationException(
                $"WPF test thread is attached to a non-private desktop ('{actual}').");
        }

        if (!string.Equals(actual, expected, StringComparison.Ordinal))
        {
            throw new InvalidOperationException(
                $"WPF test desktop mismatch. Expected '{expected}', actual '{actual}'.");
        }

        if (string.IsNullOrWhiteSpace(inputDesktop))
        {
            throw new InvalidOperationException(
                "The visible Windows input desktop could not be identified.");
        }

        if (string.Equals(actual, inputDesktop, StringComparison.OrdinalIgnoreCase))
        {
            throw new InvalidOperationException(
                $"WPF test desktop '{actual}' is currently visible and is not isolated from the user session.");
        }
    }

    [DllImport("user32.dll", SetLastError = true)]
    private static extern IntPtr OpenInputDesktop(
        uint flags,
        [MarshalAs(UnmanagedType.Bool)] bool inherit,
        uint desiredAccess);

    [DllImport("user32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool CloseDesktop(IntPtr desktop);

    [DllImport("kernel32.dll")]
    private static extern uint GetCurrentThreadId();

    [DllImport("user32.dll", SetLastError = true)]
    private static extern IntPtr GetThreadDesktop(uint threadId);

    [DllImport("user32.dll", EntryPoint = "GetUserObjectInformationW", CharSet = CharSet.Unicode, SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool GetUserObjectInformation(
        IntPtr handle,
        int index,
        StringBuilder? information,
        int informationLength,
        out int requiredLength);
}
