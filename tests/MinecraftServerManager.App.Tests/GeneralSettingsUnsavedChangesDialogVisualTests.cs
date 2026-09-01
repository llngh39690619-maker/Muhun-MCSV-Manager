using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;
using System.Windows.Threading;
using System.Runtime.ExceptionServices;
using MinecraftServerManager.App.Dialogs;

namespace MinecraftServerManager.App.Tests;

public sealed class GeneralSettingsUnsavedChangesDialogVisualTests
{
    [Fact]
    public void Render_IsBoundedDarkCenteredAndXReturnsToEditing()
    {
        WpfStaTestHost.Run(() =>
        {
            var owner = CreateOwner();
            owner.Show();
            try
            {
                var dialog = new GeneralSettingsUnsavedChangesDialog { Owner = owner };
                var rendered = false;
                var timedOut = false;
                ExceptionDispatchInfo? renderFailure = null;
                var timeout = new DispatcherTimer { Interval = TimeSpan.FromSeconds(5) };
                timeout.Tick += (_, _) =>
                {
                    timeout.Stop();
                    timedOut = true;
                    if (dialog.IsVisible)
                    {
                        dialog.Close();
                    }
                };
                dialog.Loaded += (_, _) => dialog.Dispatcher.BeginInvoke(new Action(() =>
                {
                    try
                    {
                        rendered = true;
                        dialog.UpdateLayout();
                        Assert.InRange(dialog.ActualWidth, 418, 422);
                        Assert.InRange(dialog.ActualHeight, 198, 202);
                        Assert.Equal(ResizeMode.NoResize, dialog.ResizeMode);
                        // The product placement behavior consumes CenterOwner after the HWND is
                        // created and switches to Manual so later user movement is not overridden.
                        // The physical owner-centered bounds are asserted below.
                        Assert.Equal(WindowStartupLocation.Manual, dialog.WindowStartupLocation);
                        var background = Assert.IsType<SolidColorBrush>(dialog.Background);
                        Assert.False(background.Color.R > 240
                                     && background.Color.G > 240
                                     && background.Color.B > 240);
                        var buttons = FindVisualChildren<Button>(dialog).ToArray();
                        Assert.Equal(2, buttons.Length);
                        Assert.Contains(buttons, button => Equals(button.Content, "取消"));
                        Assert.Contains(buttons, button => Equals(button.Content, "儲存"));
                        Assert.InRange(
                            dialog.Left,
                            owner.Left + (owner.ActualWidth - dialog.ActualWidth) / 2 - 3,
                            owner.Left + (owner.ActualWidth - dialog.ActualWidth) / 2 + 3);
                        Assert.InRange(
                            dialog.Top,
                            owner.Top + (owner.ActualHeight - dialog.ActualHeight) / 2 - 3,
                            owner.Top + (owner.ActualHeight - dialog.ActualHeight) / 2 + 3);
                    }
                    catch (Exception error)
                    {
                        renderFailure = ExceptionDispatchInfo.Capture(error);
                    }
                    finally
                    {
                        dialog.Close();
                    }
                }), DispatcherPriority.ContextIdle);

                timeout.Start();
                var result = dialog.ShowDialog();
                timeout.Stop();

                Assert.False(timedOut);
                Assert.True(rendered);
                renderFailure?.Throw();
                Assert.NotEqual(true, result);
                Assert.Equal(GeneralSettingsCloseChoice.ContinueEditing, dialog.Choice);
            }
            finally
            {
                owner.Close();
            }
        });
    }

    [Theory]
    [InlineData("取消", "Discard")]
    [InlineData("儲存", "SaveAndApply")]
    public void Buttons_ReturnExplicitCloseChoice(
        string buttonLabel,
        string expectedChoice)
    {
        WpfStaTestHost.Run(() =>
        {
            var owner = CreateOwner();
            owner.Show();
            try
            {
                var dialog = new GeneralSettingsUnsavedChangesDialog { Owner = owner };
                var timedOut = false;
                ExceptionDispatchInfo? clickFailure = null;
                var timeout = new DispatcherTimer { Interval = TimeSpan.FromSeconds(5) };
                timeout.Tick += (_, _) =>
                {
                    timeout.Stop();
                    timedOut = true;
                    if (dialog.IsVisible)
                    {
                        dialog.Close();
                    }
                };
                dialog.Loaded += (_, _) => dialog.Dispatcher.BeginInvoke(new Action(() =>
                {
                    try
                    {
                        dialog.UpdateLayout();
                        var button = Assert.Single(
                            FindVisualChildren<Button>(dialog),
                            candidate => Equals(candidate.Content, buttonLabel));
                        button.RaiseEvent(new RoutedEventArgs(Button.ClickEvent));
                    }
                    catch (Exception error)
                    {
                        clickFailure = ExceptionDispatchInfo.Capture(error);
                        dialog.Close();
                    }
                }), DispatcherPriority.ContextIdle);

                timeout.Start();
                var result = dialog.ShowDialog();
                timeout.Stop();

                Assert.False(timedOut);
                clickFailure?.Throw();
                Assert.True(result);
                Assert.Equal(expectedChoice, dialog.Choice.ToString());
            }
            finally
            {
                owner.Close();
            }
        });
    }

    private static Window CreateOwner()
        => new()
        {
            Width = 700,
            Height = 460,
            Left = SystemParameters.WorkArea.Left + 40,
            Top = SystemParameters.WorkArea.Top + 40,
            WindowStartupLocation = WindowStartupLocation.Manual,
            ShowInTaskbar = false,
            Background = Brushes.Black,
        };

    private static IEnumerable<T> FindVisualChildren<T>(DependencyObject parent)
        where T : DependencyObject
    {
        for (var index = 0; index < VisualTreeHelper.GetChildrenCount(parent); index++)
        {
            var child = VisualTreeHelper.GetChild(parent, index);
            if (child is T match)
            {
                yield return match;
            }

            foreach (var descendant in FindVisualChildren<T>(child))
            {
                yield return descendant;
            }
        }
    }
}
