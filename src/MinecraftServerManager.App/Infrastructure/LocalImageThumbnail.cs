using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;

namespace MinecraftServerManager.App.Infrastructure;

/// <summary>
/// Asynchronously assigns a bounded local thumbnail to an <see cref="Image"/>. Use this attached
/// behavior instead of binding a file path directly to <see cref="Image.Source"/> so WPF does not
/// decode a full-size image on the UI thread or retain a lock on the source file.
/// </summary>
public static class LocalImageThumbnail
{
    private static readonly LocalImageThumbnailLoader SharedLoader = new();
    private static readonly object PendingLoadsGate = new();
    private static readonly HashSet<Task> PendingLoads = [];

    public static readonly DependencyProperty SourcePathProperty =
        DependencyProperty.RegisterAttached(
            "SourcePath",
            typeof(string),
            typeof(LocalImageThumbnail),
            new PropertyMetadata(null, OnThumbnailPropertyChanged));

    public static readonly DependencyProperty DecodePixelWidthProperty =
        DependencyProperty.RegisterAttached(
            "DecodePixelWidth",
            typeof(int),
            typeof(LocalImageThumbnail),
            new PropertyMetadata(
                LocalImageThumbnailLoader.DefaultDecodeWidth,
                OnThumbnailPropertyChanged,
                CoerceDecodeDimension));

    public static readonly DependencyProperty DecodePixelHeightProperty =
        DependencyProperty.RegisterAttached(
            "DecodePixelHeight",
            typeof(int),
            typeof(LocalImageThumbnail),
            new PropertyMetadata(
                LocalImageThumbnailLoader.DefaultDecodeHeight,
                OnThumbnailPropertyChanged,
                CoerceDecodeDimension));

    private static readonly DependencyProperty RequestProperty =
        DependencyProperty.RegisterAttached(
            "Request",
            typeof(CancellationTokenSource),
            typeof(LocalImageThumbnail),
            new PropertyMetadata(null));

    private static readonly DependencyProperty IsHookedProperty =
        DependencyProperty.RegisterAttached(
            "IsHooked",
            typeof(bool),
            typeof(LocalImageThumbnail),
            new PropertyMetadata(false));

    public static void SetSourcePath(DependencyObject element, string? value)
        => element.SetValue(SourcePathProperty, value);

    public static string? GetSourcePath(DependencyObject element)
        => (string?)element.GetValue(SourcePathProperty);

    public static void SetDecodePixelWidth(DependencyObject element, int value)
        => element.SetValue(DecodePixelWidthProperty, value);

    public static int GetDecodePixelWidth(DependencyObject element)
        => (int)element.GetValue(DecodePixelWidthProperty);

    public static void SetDecodePixelHeight(DependencyObject element, int value)
        => element.SetValue(DecodePixelHeightProperty, value);

    public static int GetDecodePixelHeight(DependencyObject element)
        => (int)element.GetValue(DecodePixelHeightProperty);

    private static object CoerceDecodeDimension(DependencyObject _, object value)
        => Math.Clamp(
            value is int dimension ? dimension : 1,
            1,
            LocalImageThumbnailLoader.MaximumDecodeDimension);

    private static void OnThumbnailPropertyChanged(
        DependencyObject dependencyObject,
        DependencyPropertyChangedEventArgs _)
    {
        if (dependencyObject is not Image image)
        {
            return;
        }

        EnsureLifecycleHandlers(image);
        CancelCurrentRequest(image);
        image.Source = null;
        if (image.IsLoaded)
        {
            StartLoad(image);
        }
    }

    private static void EnsureLifecycleHandlers(Image image)
    {
        if ((bool)image.GetValue(IsHookedProperty))
        {
            return;
        }

        image.SetValue(IsHookedProperty, true);
        image.Loaded += OnImageLoaded;
        image.Unloaded += OnImageUnloaded;
    }

    private static void OnImageLoaded(object sender, RoutedEventArgs _)
    {
        if (sender is Image image && image.Source is null)
        {
            StartLoad(image);
        }
    }

    private static void OnImageUnloaded(object sender, RoutedEventArgs _)
    {
        if (sender is not Image image)
        {
            return;
        }

        CancelCurrentRequest(image);
        image.Source = null;
    }

    internal static async Task WaitForPendingLoadsAsync(CancellationToken cancellationToken = default)
    {
        while (true)
        {
            Task[] snapshot;
            lock (PendingLoadsGate)
            {
                snapshot = PendingLoads.ToArray();
            }

            if (snapshot.Length == 0)
            {
                return;
            }

            await Task.WhenAll(snapshot).WaitAsync(cancellationToken).ConfigureAwait(false);
        }
    }

    /// <summary>
    /// Loads one thumbnail for an off-screen diagnostic visual. WPF does not raise Loaded for the
    /// hidden preview window, so the normal lifecycle handler cannot populate its image source.
    /// </summary>
    internal static async Task LoadForDiagnosticsAsync(
        Image image,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(image);
        var sourcePath = GetSourcePath(image);
        if (string.IsNullOrWhiteSpace(sourcePath))
        {
            return;
        }

        var source = await SharedLoader.LoadAsync(
                sourcePath,
                GetDecodePixelWidth(image),
                GetDecodePixelHeight(image),
                cancellationToken)
            .ConfigureAwait(false);
        if (source is null || image.Dispatcher.HasShutdownStarted)
        {
            return;
        }

        await image.Dispatcher.InvokeAsync(() =>
        {
            if (string.Equals(GetSourcePath(image), sourcePath, StringComparison.Ordinal))
            {
                image.Source = source;
            }
        });
    }

    private static void StartLoad(Image image)
    {
        var task = LoadAndAssignAsync(image);
        lock (PendingLoadsGate)
        {
            PendingLoads.Add(task);
        }

        _ = ObserveCompletionAsync(task);
    }

    private static async Task ObserveCompletionAsync(Task task)
    {
        try
        {
            await task.ConfigureAwait(false);
        }
        finally
        {
            lock (PendingLoadsGate)
            {
                PendingLoads.Remove(task);
            }
        }
    }

    private static async Task LoadAndAssignAsync(Image image)
    {
        CancelCurrentRequest(image);
        var sourcePath = GetSourcePath(image);
        if (string.IsNullOrWhiteSpace(sourcePath))
        {
            return;
        }

        var cancellation = new CancellationTokenSource();
        image.SetValue(RequestProperty, cancellation);
        try
        {
            var source = await SharedLoader.LoadAsync(
                    sourcePath,
                    GetDecodePixelWidth(image),
                    GetDecodePixelHeight(image),
                    cancellation.Token)
                .ConfigureAwait(false);
            if (image.Dispatcher.HasShutdownStarted)
            {
                return;
            }

            await image.Dispatcher.InvokeAsync(() =>
            {
                if (ReferenceEquals(image.GetValue(RequestProperty), cancellation)
                    && image.IsLoaded
                    && string.Equals(
                        GetSourcePath(image),
                        sourcePath,
                        StringComparison.Ordinal))
                {
                    image.Source = source;
                }
            });
        }
        catch (OperationCanceledException) when (cancellation.IsCancellationRequested)
        {
            // A changed binding or an unloaded/recycled item intentionally cancels stale work.
        }
        catch (TaskCanceledException) when (image.Dispatcher.HasShutdownStarted)
        {
            // Application shutdown can cancel a queued Dispatcher operation.
        }
        catch (InvalidOperationException) when (image.Dispatcher.HasShutdownStarted)
        {
            // The Dispatcher stopped between the shutdown check and InvokeAsync.
        }
        catch (Exception exception) when (IsRecoverableOptionalImageFailure(exception))
        {
            // Optional artwork must never terminate the UI. The loader already rejects malformed
            // input; this guard also handles a race with a recycled or shutting-down visual.
        }
        finally
        {
            if (!image.Dispatcher.HasShutdownStarted)
            {
                try
                {
                    await image.Dispatcher.InvokeAsync(() =>
                    {
                        if (ReferenceEquals(image.GetValue(RequestProperty), cancellation))
                        {
                            image.ClearValue(RequestProperty);
                        }
                    });
                }
                catch (TaskCanceledException) when (image.Dispatcher.HasShutdownStarted)
                {
                    // Nothing remains to clean in a terminated Dispatcher.
                }
                catch (InvalidOperationException) when (image.Dispatcher.HasShutdownStarted)
                {
                    // Nothing remains to clean in a terminated Dispatcher.
                }
            }

            cancellation.Dispose();
        }
    }

    private static void CancelCurrentRequest(Image image)
    {
        if (image.GetValue(RequestProperty) is not CancellationTokenSource cancellation)
        {
            return;
        }

        image.ClearValue(RequestProperty);
        cancellation.Cancel();
    }

    private static bool IsRecoverableOptionalImageFailure(Exception exception)
        => exception is IOException
            or UnauthorizedAccessException
            or NotSupportedException
            or ArgumentException
            or InvalidOperationException
            or System.Runtime.InteropServices.ExternalException
            or DllNotFoundException
            or EntryPointNotFoundException
            or BadImageFormatException
            or TypeInitializationException;
}
