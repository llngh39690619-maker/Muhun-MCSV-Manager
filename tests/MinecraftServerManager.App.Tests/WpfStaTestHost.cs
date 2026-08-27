using System.Runtime.ExceptionServices;
using System.Threading;
using System.Windows;
using System.Windows.Threading;

namespace MinecraftServerManager.App.Tests;

internal static class WpfStaTestHost
{
    private static readonly Lazy<Host> Shared = new(
        () => new Host(),
        LazyThreadSafetyMode.ExecutionAndPublication);

    public static void Run(Action action)
    {
        ArgumentNullException.ThrowIfNull(action);
        Shared.Value.Run(action);
    }

    private sealed class Host
    {
        private readonly Dispatcher _dispatcher;
        private ExceptionDispatchInfo? _initializationFailure;

        public Host()
        {
            using var ready = new ManualResetEventSlim();
            Dispatcher? dispatcher = null;
            var thread = new Thread(() =>
            {
                try
                {
                    dispatcher = Dispatcher.CurrentDispatcher;
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
            _dispatcher.Invoke(action, DispatcherPriority.Send);
        }
    }
}
