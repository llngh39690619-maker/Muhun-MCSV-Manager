using System.ComponentModel;
using System.IO;
using System.Runtime.CompilerServices;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;
using System.Windows.Threading;
using System.Xml.Linq;
using MinecraftServerManager.App.Controls;
using MinecraftServerManager.App.Dialogs;
using MinecraftServerManager.App.Services;
using MinecraftServerManager.App.ViewModels;
using MinecraftServerManager.Contracts.Localization;
using MinecraftServerManager.Core.Models;
using MinecraftServerManager.Core.Services;
using MinecraftServerManager.Remote;
using MinecraftServerManager.Remote.Contracts;

namespace MinecraftServerManager.App.Tests;

public sealed class RemoteAccessDialogLifecycleTests
{
    [Fact]
    public void ApprovedAccountLocksAllowedGmailAndPreventsMismatchedApply()
    {
        WpfStaTestHost.Run(() =>
        {
            var store = new EphemeralRemoteSecurityStore();
            store.RegisterAccount("approved@gmail.com", "account1", "12345678");
            var coordinator = new RemoteAccessCoordinator(
                new StubRemoteBackend(),
                securityStore: store);
            try
            {
                using var viewModel = new RemoteAccessSettingsViewModel(
                    new RemoteControlSettings
                    {
                        Enabled = true,
                        AllowedLogin = "different@gmail.com"
                    },
                    coordinator,
                    _ => Task.CompletedTask,
                    Dispatcher.CurrentDispatcher);

                Assert.True(viewModel.HasApprovedAccount);
                Assert.False(viewModel.CanApply);
                viewModel.AllowedLogin = "approved@gmail.com";
                Assert.True(viewModel.CanApply);
            }
            finally
            {
                coordinator.DisposeAsync().AsTask().GetAwaiter().GetResult();
            }
        });
    }

    [Fact]
    public void ShowAndFirstLayout_DoesNotWriteBackToReadOnlyOutputProperties()
    {
        WpfStaTestHost.Run(() =>
        {
            Application.Current.ShutdownMode = ShutdownMode.OnExplicitShutdown;
            var coordinator = new RemoteAccessCoordinator(new StubRemoteBackend());
            try
            {
                using var viewModel = new RemoteAccessSettingsViewModel(
                    new RemoteControlSettings(),
                    coordinator,
                    _ => Task.CompletedTask,
                    Dispatcher.CurrentDispatcher);
                var dialog = new RemoteAccessDialog(viewModel);
                var contentRendered = false;
                var timedOut = false;
                var timeout = new DispatcherTimer
                {
                    Interval = TimeSpan.FromSeconds(5)
                };
                timeout.Tick += (_, _) =>
                {
                    timeout.Stop();
                    timedOut = true;
                    dialog.Close();
                };
                dialog.ContentRendered += (_, _) =>
                {
                    dialog.UpdateLayout();
                    contentRendered = true;
                    dialog.Close();
                };

                timeout.Start();
                var result = dialog.ShowDialog();
                timeout.Stop();

                Assert.False(timedOut);
                Assert.True(contentRendered);
                Assert.False(result);
                Assert.False(dialog.IsVisible);
            }
            finally
            {
                coordinator.DisposeAsync().AsTask().GetAwaiter().GetResult();
            }
        });
    }

    [Fact]
    public void ApprovedAccountChanged_DispatchesZeroArgumentRefreshWithoutUnhandledException()
    {
        WpfStaTestHost.Run(() =>
        {
            Application.Current.ShutdownMode = ShutdownMode.OnExplicitShutdown;
            var dispatcher = Dispatcher.CurrentDispatcher;
            Exception? unhandled = null;
            DispatcherUnhandledExceptionEventHandler handler = (_, args) =>
            {
                unhandled = args.Exception;
                args.Handled = true;
            };
            dispatcher.UnhandledException += handler;
            var coordinator = new RemoteAccessCoordinator(
                new StubRemoteBackend(),
                securityStore: new EphemeralRemoteSecurityStore());
            try
            {
                using var viewModel = new RemoteAccessSettingsViewModel(
                    new RemoteControlSettings
                    {
                        AccessMode = RemoteAccessMode.CloudflareQuickTunnel
                    },
                    coordinator,
                    _ => Task.CompletedTask,
                    dispatcher);

                coordinator.RegisterLocalApprovedAccountAsync(
                        "account1",
                        "12345678",
                        "12345678",
                        RemoteWebPermission.All)
                    .GetAwaiter()
                    .GetResult();
                PumpDispatcherUntil(() => viewModel.AccountRows.Count == 1);

                Assert.Null(unhandled);
                Assert.Equal("account1", viewModel.AccountRows[0].Username);
            }
            finally
            {
                dispatcher.UnhandledException -= handler;
                coordinator.DisposeAsync().AsTask().GetAwaiter().GetResult();
            }
        });
    }

    [Fact]
    public void ShownQuickDialog_CreateAccountSuccess_PumpsDispatcherAndAddsAccountRow()
    {
        WpfStaTestHost.Run(() =>
        {
            Application.Current.ShutdownMode = ShutdownMode.OnExplicitShutdown;
            var dispatcher = Dispatcher.CurrentDispatcher;
            Exception? unhandled = null;
            DispatcherUnhandledExceptionEventHandler handler = (_, args) =>
            {
                unhandled = args.Exception;
                args.Handled = true;
            };
            dispatcher.UnhandledException += handler;
            var coordinator = new RemoteAccessCoordinator(
                new StubRemoteBackend(),
                securityStore: new EphemeralRemoteSecurityStore());
            var viewModel = new RemoteAccessSettingsViewModel(
                new RemoteControlSettings
                {
                    AccessMode = RemoteAccessMode.CloudflareQuickTunnel
                },
                coordinator,
                _ => Task.CompletedTask,
                dispatcher);
            var dialog = new RemoteAccessDialog(viewModel);
            try
            {
                dialog.Show();
                dialog.UpdateLayout();
                viewModel.RemoteUsername = "account1";
                viewModel.RemotePin = "12345678";
                viewModel.ConfirmedRemotePin = "12345678";
                Assert.True(viewModel.RegisterAccountCommand.CanExecute(null));

                viewModel.RegisterAccountCommand.Execute(null);
                PumpDispatcherUntil(() =>
                    !viewModel.IsBusy && viewModel.AccountRows.Count == 1);
                dialog.UpdateLayout();

                Assert.Null(unhandled);
                Assert.True(dialog.IsVisible);
                Assert.False(viewModel.HasProvisioningError, viewModel.ProvisioningError);
                Assert.Equal("account1", viewModel.AccountRows[0].Username);
                Assert.Equal(string.Empty, viewModel.RemoteUsername);
                Assert.Contains("帳號已建立", viewModel.ProvisioningStatus, StringComparison.Ordinal);
            }
            finally
            {
                if (dialog.IsVisible) dialog.Close();
                dispatcher.UnhandledException -= handler;
                coordinator.DisposeAsync().AsTask().GetAwaiter().GetResult();
            }
        });
    }

    [Fact]
    public void QuickMode_HidesMixedAccountGmailProjectionAndClearsVerificationUi()
    {
        WpfStaTestHost.Run(() =>
        {
            var store = new EphemeralRemoteSecurityStore();
            store.RegisterAccount(
                "owner@gmail.com",
                "gmail01",
                "12345678",
                RemoteWebPermission.StartServer);
            store.RegisterAccount(null, "local001", "87654321", RemoteWebPermission.StopServer);
            var coordinator = new RemoteAccessCoordinator(new StubRemoteBackend(), securityStore: store);
            try
            {
                using var viewModel = new RemoteAccessSettingsViewModel(
                    new RemoteControlSettings
                    {
                        AccessMode = RemoteAccessMode.Tailscale,
                        AllowedLogin = "owner@gmail.com"
                    },
                    coordinator,
                    _ => Task.CompletedTask,
                    Dispatcher.CurrentDispatcher);

                Assert.Contains(viewModel.AccountRows, row => row.IdentityText.Contains("owner@gmail.com", StringComparison.Ordinal));
                Assert.Equal("尚未寄送 Gmail 驗證碼。", viewModel.ProvisioningStatus);
                Assert.True(viewModel.AllowStartServer);
                Assert.True(viewModel.AllowStopServer);
                Assert.True(viewModel.AllowRestartServer);

                viewModel.VerificationCode = "123456";
                viewModel.AccessMode = RemoteAccessMode.CloudflareQuickTunnel;

                Assert.Equal(string.Empty, viewModel.VerificationCode);
                Assert.Equal(string.Empty, viewModel.ProvisioningStatus);
                Assert.False(viewModel.HasProvisioningStatus);
                var localRow = Assert.Single(viewModel.AccountRows);
                Assert.Equal("local001", localRow.Username);
                Assert.All(viewModel.AccountRows, row => Assert.Equal("本機帳號", row.IdentityText));
                Assert.DoesNotContain(
                    viewModel.AccountRows,
                    row => row.IdentityText.Contains("Gmail", StringComparison.OrdinalIgnoreCase)
                           || row.IdentityText.Contains('@'));
            }
            finally
            {
                coordinator.DisposeAsync().AsTask().GetAwaiter().GetResult();
            }
        });
    }

    [Fact]
    public void FunnelMode_UsesLocalAccountsWithoutGmailOrCloudflareCredentials()
    {
        WpfStaTestHost.Run(() =>
        {
            var store = new EphemeralRemoteSecurityStore();
            store.RegisterAccount(
                "owner@gmail.com",
                "gmail01",
                "12345678",
                RemoteWebPermission.StartServer);
            store.RegisterAccount(
                null,
                "local001",
                "87654321",
                RemoteWebPermission.StopServer);
            var coordinator = new RemoteAccessCoordinator(
                new StubRemoteBackend(),
                securityStore: store);
            try
            {
                using var viewModel = new RemoteAccessSettingsViewModel(
                    new RemoteControlSettings
                    {
                        AccessMode = RemoteAccessMode.Tailscale,
                        AllowedLogin = "owner@gmail.com",
                        LocalPort = RemoteControlSettings.DefaultLocalPort
                    },
                    coordinator,
                    _ => Task.CompletedTask,
                    Dispatcher.CurrentDispatcher);
                viewModel.VerificationCode = "123456";

                viewModel.AccessMode = RemoteAccessMode.TailscaleFunnel;

                Assert.Contains(RemoteAccessMode.TailscaleFunnel, viewModel.AccessModes);
                Assert.True(viewModel.IsFunnelMode);
                Assert.True(viewModel.IsTailscaleProviderMode);
                Assert.True(viewModel.IsLocalAccountMode);
                Assert.False(viewModel.IsTailscaleMode);
                Assert.False(viewModel.IsCloudflareMode);
                Assert.False(viewModel.RequiresEmailVerification);
                Assert.True(viewModel.CanEditAccountCredentialFields);
                Assert.True(viewModel.CanApply);
                Assert.Empty(viewModel.VerificationCode);
                Assert.Equal("local001", Assert.Single(viewModel.AccountRows).Username);
                Assert.Contains("固定公開 HTTPS", viewModel.AccessModeDescription, StringComparison.Ordinal);
                Assert.Contains(
                    "公網存取",
                    viewModel.TailscaleHttpsCertificateGuidanceText,
                    StringComparison.Ordinal);

                viewModel.RemoteUsername = "local002";
                viewModel.RemotePin = "11223344";
                viewModel.ConfirmedRemotePin = "11223344";
                Assert.True(viewModel.RegisterAccountCommand.CanExecute(null));
            }
            finally
            {
                coordinator.DisposeAsync().AsTask().GetAwaiter().GetResult();
            }
        });
    }

    [Fact]
    public void CreateCommandFailure_ReportsErrorInDialogInsteadOfEscapingAsyncVoid()
    {
        WpfStaTestHost.Run(() =>
        {
            var store = new EphemeralRemoteSecurityStore();
            var coordinator = new RemoteAccessCoordinator(new StubRemoteBackend(), securityStore: store);
            try
            {
                using var viewModel = new RemoteAccessSettingsViewModel(
                    new RemoteControlSettings
                    {
                        AccessMode = RemoteAccessMode.CloudflareQuickTunnel
                    },
                    coordinator,
                    _ => Task.FromException(new InvalidOperationException("simulated persist failure")),
                    Dispatcher.CurrentDispatcher);
                viewModel.RemoteUsername = "account1";
                viewModel.RemotePin = "87654321";
                viewModel.ConfirmedRemotePin = "87654321";
                Assert.True(viewModel.RegisterAccountCommand.CanExecute(null));

                viewModel.RegisterAccountCommand.Execute(null);
                PumpDispatcherUntil(() => !viewModel.IsBusy && viewModel.HasProvisioningError);

                Assert.Contains("simulated persist failure", viewModel.ProvisioningError, StringComparison.Ordinal);
            }
            finally
            {
                coordinator.DisposeAsync().AsTask().GetAwaiter().GetResult();
            }
        });
    }

    [Fact]
    public void ReadOnlyOutputBindings_AreExplicitlyOneWay()
    {
        var document = XDocument.Load(GetAppSourcePath(
            Path.Combine("Dialogs", "RemoteAccessDialog.xaml")));
        XNamespace presentation = "http://schemas.microsoft.com/winfx/2006/xaml/presentation";

        Assert.Contains(
            document.Descendants(presentation + "Run"),
            element => (string?)element.Attribute("Text")
                       == "{Binding NetworkProviderStatusText, Mode=OneWay}");
        Assert.Contains(
            document.Descendants(presentation + "Run"),
            element => (string?)element.Attribute("Text")
                       == "{Binding RemoteServiceStatusText, Mode=OneWay}");
        Assert.Contains(
            document.Descendants(presentation + "TextBox"),
            element => (string?)element.Attribute("Text")
                       == "{Binding PublicUrl, Mode=OneWay}");
        Assert.Contains(
            document.Descendants(presentation + "Run"),
            element => (string?)element.Attribute("Text")
                       == "{Binding SavedSmtpSenderText, Mode=OneWay}");
        Assert.Contains(
            document.Descendants(presentation + "TextBox"),
            element => (string?)element.Attribute("AutomationProperties.Name")
                       == "{DynamicResource L10n.remote.legacy.ui.allowedGmailAria}"
                       && (string?)element.Attribute("IsReadOnly")
                       == "{Binding HasTailscaleApprovedAccount}");
        Assert.DoesNotContain(
            document.Descendants(),
            element => element.Attributes().Any(attribute =>
                attribute.Value.Contains("Pairing", StringComparison.OrdinalIgnoreCase)));
        Assert.Contains(
            document.Descendants(presentation + "Button"),
            element => (string?)element.Attribute("Content")
                       == "{DynamicResource L10n.remote.legacy.ui.openTailscaleHttps}"
                       && (string?)element.Attribute("Command")
                       == "{Binding OpenTailscaleHttpsSettingsCommand}"
                       && (string?)element.Attribute("Visibility")
                       == "{Binding RequiresTailscaleHttpsCertificateEnablement, Mode=OneWay, Converter={StaticResource BoolToVisibility}}");
        Assert.Contains(
            document.Descendants(presentation + "Button"),
            element => (string?)element.Attribute("Content")
                       == "{DynamicResource L10n.remote.legacy.ui.deleteSmtp}"
                       && (string?)element.Attribute("Command") == "{Binding DeleteSmtpCommand}");
        Assert.Contains(
            document.Descendants(presentation + "Button"),
            element => (string?)element.Attribute("Content")
                       == "{DynamicResource L10n.remote.legacy.ui.installCloudflared}"
                       && (string?)element.Attribute("Command")
                       == "{Binding InstallCloudflaredCommand}");
        Assert.Contains(
            document.Descendants(presentation + "Button"),
            element => (string?)element.Attribute("Content")
                       == "{DynamicResource L10n.remote.legacy.ui.accountSettings}"
                       && (string?)element.Attribute("Command") == "{Binding ToggleSettingsCommand}");
        Assert.Contains(
            document.Descendants(presentation + "TextBlock"),
            element => (string?)element.Attribute("Text") == "{Binding PinDisplayText}");
        Assert.Contains(
            document.Descendants(presentation + "Button"),
            element => (string?)element.Attribute("Command")
                       == "{Binding TogglePinVisibilityCommand}"
                       && (string?)element.Attribute("IsEnabled")
                       == "{Binding IsPinRevealEnabled}");
        Assert.DoesNotContain(
            document.Descendants(),
            element => element.Attributes().Any(attribute =>
                attribute.Value.Contains("只能重設", StringComparison.Ordinal)));
        Assert.Contains(
            document.Descendants(),
            element => element.Name.LocalName == nameof(RevealPasswordBox)
                       && ((string?)element.Attribute("Password"))?.Contains("NewPin", StringComparison.Ordinal) == true);
        Assert.Contains(
            document.Descendants(presentation + "Border"),
            element => (string?)element.Attribute("Visibility")
                       == "{Binding IsTailscaleMode, Converter={StaticResource BoolToVisibility}}"
                       && element.Descendants(presentation + "TextBlock").Any(text =>
                           (string?)text.Attribute("Text")
                           == "{DynamicResource L10n.remote.legacy.ui.smtpHeading}"));
        Assert.Contains(
            document.Descendants(presentation + "TextBlock"),
            element => (string?)element.Attribute("Text")
                       == "{Binding CloudflaredInstallStatus, Mode=OneWay}");
        Assert.True(
            document.Descendants(presentation + "Border").Count(
                element => (string?)element.Attribute("IsEnabled")
                           == "{Binding IsBusy, Converter={StaticResource InverseBool}}") >= 3);
    }

    [Fact]
    public void PasswordInputs_ResolveDarkImplicitStyleAndClickRevealRoundTripsTwoWayEdit()
    {
        WpfStaTestHost.Run(() =>
        {
            Application.Current.ShutdownMode = ShutdownMode.OnExplicitShutdown;
            var passwordBox = new PasswordBox();
            var source = new PasswordBindingSource { Password = "12345678" };
            var reveal = new RevealPasswordBox { MaxLength = 12 };
            reveal.SetBinding(
                RevealPasswordBox.PasswordProperty,
                new System.Windows.Data.Binding(nameof(PasswordBindingSource.Password))
                {
                    Source = source,
                    Mode = System.Windows.Data.BindingMode.TwoWay,
                    UpdateSourceTrigger = System.Windows.Data.UpdateSourceTrigger.PropertyChanged
                });
            var panel = new StackPanel();
            panel.Children.Add(passwordBox);
            panel.Children.Add(reveal);
            var window = new Window
            {
                Width = 320,
                Height = 180,
                Content = panel,
                ShowInTaskbar = false
            };
            try
            {
                window.Show();
                window.UpdateLayout();
                var expectedBackground = Assert.IsType<SolidColorBrush>(
                    Application.Current.Resources["WindowBrush"]);
                var actualBackground = Assert.IsType<SolidColorBrush>(passwordBox.Background);
                Assert.Equal(expectedBackground.Color, actualBackground.Color);
                Assert.Equal("12345678", reveal.Password);
                Assert.Equal(12, reveal.MaxLength);

                var eye = Assert.IsType<Button>(reveal.FindName("RevealButton"));
                var revealedInput = Assert.IsType<TextBox>(reveal.FindName("RevealedInput"));
                var maskedInput = Assert.IsType<PasswordBox>(reveal.FindName("MaskedInput"));
                Assert.False(reveal.IsPasswordRevealed);
                Assert.Equal(string.Empty, revealedInput.Text);

                source.Password = "24681357";
                Assert.Equal("24681357", reveal.Password);
                Assert.Equal(string.Empty, revealedInput.Text);

                maskedInput.Password = "13572468";
                Assert.Equal("13572468", source.Password);
                Assert.Equal("13572468", reveal.Password);
                Assert.Equal(string.Empty, revealedInput.Text);

                eye.RaiseEvent(new RoutedEventArgs(Button.ClickEvent));
                Assert.True(reveal.IsPasswordRevealed);
                Assert.Equal("13572468", revealedInput.Text);

                revealedInput.Text = "87654321";
                Assert.Equal("87654321", reveal.Password);
                Assert.Equal("87654321", source.Password);

                eye.RaiseEvent(new RoutedEventArgs(Button.ClickEvent));
                Assert.False(reveal.IsPasswordRevealed);
                Assert.Equal(string.Empty, revealedInput.Text);
                Assert.Equal("87654321", reveal.Password);

                source.Password = "11223344";
                Assert.Equal("11223344", reveal.Password);
                Assert.Equal("11223344", maskedInput.Password);
                Assert.Equal(string.Empty, revealedInput.Text);
            }
            finally
            {
                window.Close();
            }
        });
    }

    [Fact]
    public void AccountRow_EyeClickTogglesRecoverablePinAndHideClearsPlaintextProjection()
    {
        var fetchCount = 0;
        var row = new RemoteAccountRowViewModel(
            new RemoteApprovedAccount(
                "account1",
                null,
                null,
                DateTimeOffset.UtcNow,
                RemoteWebPermission.All,
                HasRecoverablePin: true),
            showTailscaleIdentity: false,
            () =>
            {
                fetchCount++;
                return "12345678";
            });

        Assert.Equal("密碼：••••••••", row.PinDisplayText);
        Assert.Equal("顯示密碼", row.PinVisibilityToolTip);
        Assert.True(row.TogglePinVisibilityCommand.CanExecute(null));
        Assert.Equal(0, fetchCount);

        row.TogglePinVisibilityCommand.Execute(null);
        Assert.Equal(1, fetchCount);
        Assert.True(row.IsPinRevealed);
        Assert.Equal("密碼：12345678", row.PinDisplayText);
        Assert.Equal("隱藏密碼", row.PinVisibilityToolTip);

        row.HideRevealedPin();
        Assert.False(row.IsPinRevealed);
        Assert.Equal("密碼：••••••••", row.PinDisplayText);
        Assert.DoesNotContain("12345678", row.PinDisplayText, StringComparison.Ordinal);
    }

    [Fact]
    public void AccountRows_OnlyRevealOnePinAndModeSwitchOrDisposeMasksIt()
    {
        WpfStaTestHost.Run(() =>
        {
            Application.Current.ShutdownMode = ShutdownMode.OnExplicitShutdown;
            var store = new EphemeralRemoteSecurityStore();
            store.RegisterAccount(null, "account1", "11111111");
            store.RegisterAccount(null, "account2", "22222222");
            var coordinator = new RemoteAccessCoordinator(new StubRemoteBackend(), securityStore: store);
            try
            {
                var viewModel = new RemoteAccessSettingsViewModel(
                    new RemoteControlSettings
                    {
                        AccessMode = RemoteAccessMode.CloudflareQuickTunnel
                    },
                    coordinator,
                    _ => Task.CompletedTask,
                    Dispatcher.CurrentDispatcher);
                var first = viewModel.AccountRows.Single(row => row.Username == "account1");
                var second = viewModel.AccountRows.Single(row => row.Username == "account2");

                first.TogglePinVisibilityCommand.Execute(null);
                Assert.Equal("密碼：11111111", first.PinDisplayText);
                second.TogglePinVisibilityCommand.Execute(null);
                Assert.Equal("密碼：••••••••", first.PinDisplayText);
                Assert.Equal("密碼：22222222", second.PinDisplayText);

                viewModel.AccessMode = RemoteAccessMode.Tailscale;
                Assert.Equal("密碼：••••••••", second.PinDisplayText);
                viewModel.AccessMode = RemoteAccessMode.CloudflareQuickTunnel;
                var rebuilt = viewModel.AccountRows.Single(row => row.Username == "account1");
                rebuilt.TogglePinVisibilityCommand.Execute(null);
                Assert.Equal("密碼：11111111", rebuilt.PinDisplayText);

                viewModel.Dispose();
                Assert.Equal("密碼：••••••••", rebuilt.PinDisplayText);
            }
            finally
            {
                coordinator.DisposeAsync().AsTask().GetAwaiter().GetResult();
            }
        });
    }

    [Fact]
    public void DialogDeactivated_MasksStoredAndEditablePasswordDisplaysWithoutLosingEdit()
    {
        WpfStaTestHost.Run(() =>
        {
            Application.Current.ShutdownMode = ShutdownMode.OnExplicitShutdown;
            var store = new EphemeralRemoteSecurityStore();
            store.RegisterAccount(null, "account1", "11111111");
            var coordinator = new RemoteAccessCoordinator(new StubRemoteBackend(), securityStore: store);
            var viewModel = new RemoteAccessSettingsViewModel(
                new RemoteControlSettings
                {
                    AccessMode = RemoteAccessMode.CloudflareQuickTunnel
                },
                coordinator,
                _ => Task.CompletedTask,
                Dispatcher.CurrentDispatcher)
            {
                RemotePin = "87654321",
                ConfirmedRemotePin = "87654321"
            };
            var dialog = new RemoteAccessDialog(viewModel);
            try
            {
                dialog.Show();
                dialog.UpdateLayout();
                var row = Assert.Single(viewModel.AccountRows);
                row.TogglePinVisibilityCommand.Execute(null);
                var editable = FindVisualChildren<RevealPasswordBox>(dialog)
                    .First(control => string.Equals(control.Password, "87654321", StringComparison.Ordinal));
                var eye = Assert.IsType<Button>(editable.FindName("RevealButton"));
                eye.RaiseEvent(new RoutedEventArgs(Button.ClickEvent));
                Assert.True(editable.IsPasswordRevealed);
                Assert.True(row.IsPinRevealed);

                typeof(RemoteAccessDialog)
                    .GetMethod(
                        "OnWindowDeactivated",
                        System.Reflection.BindingFlags.Instance | System.Reflection.BindingFlags.NonPublic)!
                    .Invoke(dialog, [dialog, EventArgs.Empty]);

                Assert.False(editable.IsPasswordRevealed);
                Assert.Equal(string.Empty, Assert.IsType<TextBox>(editable.FindName("RevealedInput")).Text);
                Assert.Equal("87654321", editable.Password);
                Assert.Equal("87654321", viewModel.RemotePin);
                Assert.False(row.IsPinRevealed);
                Assert.Equal("密碼：••••••••", row.PinDisplayText);
            }
            finally
            {
                if (dialog.IsVisible) dialog.Close();
                coordinator.DisposeAsync().AsTask().GetAwaiter().GetResult();
            }
        });
    }

    [Fact]
    public void LegacyAccountWithoutRecoverablePin_DisablesEyeWithResetGuidance()
    {
        var row = new RemoteAccountRowViewModel(
            new RemoteApprovedAccount(
                "account1",
                null,
                null,
                DateTimeOffset.UtcNow,
                RemoteWebPermission.All,
                HasRecoverablePin: false),
            showTailscaleIdentity: false,
            () => "12345678");

        Assert.False(row.IsPinRevealEnabled);
        Assert.False(row.TogglePinVisibilityCommand.CanExecute(null));
        Assert.Equal("重新設定一次後即可顯示", row.PinVisibilityToolTip);
        Assert.Equal("密碼：••••••••", row.PinDisplayText);
    }

    [Fact]
    public void RemoteApprovedAccount_ExposesOnlyRecoverableAvailabilityNotPlaintextPin()
    {
        var pinProperties = typeof(RemoteApprovedAccount)
            .GetProperties()
            .Where(property => property.Name.Contains("Pin", StringComparison.OrdinalIgnoreCase))
            .Select(property => property.Name)
            .ToArray();

        Assert.Equal(new[] { nameof(RemoteApprovedAccount.HasRecoverablePin) }, pinProperties);

        var outboundResponseTypes = new[]
        {
            typeof(RemoteDashboardDto),
            typeof(RemoteServerSummaryDto),
            typeof(RemoteServerDetailDto),
            typeof(RemoteConsolePageDto),
            typeof(RemotePlayerListDto),
            typeof(RemoteAuthStatusDto),
            typeof(RemoteOperationResultDto)
        };
        Assert.DoesNotContain(
            outboundResponseTypes.SelectMany(type => type.GetProperties()),
            property => string.Equals(property.Name, "Pin", StringComparison.OrdinalIgnoreCase)
                        || property.Name.Contains("Recoverable", StringComparison.OrdinalIgnoreCase)
                        || property.Name.Contains("Verifier", StringComparison.OrdinalIgnoreCase));
    }

    [Theory]
    [InlineData("https://mc.example.com", true)]
    [InlineData(" HTTPS://MC.EXAMPLE.COM/ ", true)]
    [InlineData("http://mc.example.com", false)]
    [InlineData("https://mc.example.com:8443", false)]
    [InlineData("https://mc.example.com/path", false)]
    [InlineData("https://quiet-lake.trycloudflare.com", false)]
    [InlineData("https://mc.example.ts.net", false)]
    [InlineData("https://mc.local", false)]
    [InlineData("https://bad_label.example.com", false)]
    [InlineData("https://127.0.0.1", false)]
    public void NamedTunnelPublicOrigin_AcceptsOnlyFixedHttpsDnsOrigin(
        string value,
        bool expectedValid)
    {
        var valid = CloudflareNamedTunnelConfiguration.TryNormalizePublicOrigin(
            value,
            out var normalized);

        Assert.Equal(expectedValid, valid);
        if (expectedValid)
        {
            Assert.NotNull(normalized);
            Assert.Equal("https://mc.example.com/", normalized.AbsoluteUri);
        }
        else
        {
            Assert.Null(normalized);
        }
    }

    [Fact]
    public void NamedTunnelToken_IsWriteOnlyInDialogAndCanBeReplacedOrDeleted()
    {
        WpfStaTestHost.Run(() =>
        {
            Application.Current.ShutdownMode = ShutdownMode.OnExplicitShutdown;
            var firstToken = "eyJ" + new string('A', 80) + "=";
            var replacementToken = "eyJ" + new string('B', 80) + "=";
            var store = new EphemeralRemoteSecurityStore();
            var coordinator = new RemoteAccessCoordinator(
                new StubRemoteBackend(),
                securityStore: store);
            var viewModel = new RemoteAccessSettingsViewModel(
                new RemoteControlSettings
                {
                    AccessMode = RemoteAccessMode.CloudflareNamedTunnel,
                    CloudflareNamedPublicOrigin = "https://mc.example.com"
                },
                coordinator,
                _ => Task.CompletedTask,
                Dispatcher.CurrentDispatcher);
            var dialog = new RemoteAccessDialog(viewModel);
            try
            {
                dialog.Show();
                dialog.UpdateLayout();
                var passwordInput = FindVisualChildren<PasswordBox>(dialog)
                    .Single(input => string.Equals(
                        System.Windows.Automation.AutomationProperties.GetName(input),
                        "Cloudflare Tunnel Token（永不顯示明文）",
                        StringComparison.Ordinal));

                Assert.Empty(passwordInput.Password);
                Assert.Empty(viewModel.CloudflareNamedTunnelToken);
                Assert.False(viewModel.HasCloudflareNamedTunnelToken);

                passwordInput.Password = "eyJ" + new string('X', 80) + "=";
                viewModel.AccessMode = RemoteAccessMode.CloudflareQuickTunnel;
                Assert.Empty(passwordInput.Password);
                Assert.Empty(viewModel.CloudflareNamedTunnelToken);
                viewModel.AccessMode = RemoteAccessMode.CloudflareNamedTunnel;

                passwordInput.Password = firstToken;
                Assert.Equal(firstToken, viewModel.CloudflareNamedTunnelToken);
                Assert.True(viewModel.SaveCloudflareNamedTunnelTokenCommand.CanExecute(null));
                viewModel.SaveCloudflareNamedTunnelTokenCommand.Execute(null);

                Assert.Empty(passwordInput.Password);
                Assert.Empty(viewModel.CloudflareNamedTunnelToken);
                Assert.True(store.HasCloudflareNamedTunnelToken);
                Assert.Equal(firstToken, store.GetCloudflareNamedTunnelCredential().Token);
                Assert.DoesNotContain(firstToken, viewModel.CloudflareNamedTunnelTokenStatus, StringComparison.Ordinal);

                passwordInput.Password = replacementToken;
                viewModel.SaveCloudflareNamedTunnelTokenCommand.Execute(null);

                Assert.Empty(passwordInput.Password);
                Assert.Equal(replacementToken, store.GetCloudflareNamedTunnelCredential().Token);
                Assert.DoesNotContain(replacementToken, viewModel.CloudflareNamedTunnelTokenStatus, StringComparison.Ordinal);
                Assert.True(viewModel.DeleteCloudflareNamedTunnelTokenCommand.CanExecute(null));

                viewModel.DeleteCloudflareNamedTunnelTokenCommand.Execute(null);
                PumpDispatcherUntil(() =>
                    !viewModel.IsBusy && !store.HasCloudflareNamedTunnelToken);

                Assert.Empty(passwordInput.Password);
                Assert.False(viewModel.HasCloudflareNamedTunnelToken);
                Assert.Contains("已刪除", viewModel.CloudflareNamedTunnelTokenStatus, StringComparison.Ordinal);
            }
            finally
            {
                if (dialog.IsVisible) dialog.Close();
                coordinator.DisposeAsync().AsTask().GetAwaiter().GetResult();
            }
        });
    }

    [Fact]
    public void NamedTunnelDialog_ExposesFixedOriginLocalServiceAndNonRevealableTokenControls()
    {
        var document = XDocument.Load(GetAppSourcePath(
            Path.Combine("Dialogs", "RemoteAccessDialog.xaml")));
        XNamespace presentation = "http://schemas.microsoft.com/winfx/2006/xaml/presentation";

        Assert.Contains(
            document.Descendants(presentation + "RadioButton"),
            element => (string?)element.Attribute("Content")
                       == "{DynamicResource L10n.remote.legacy.ui.mode.named}"
                       && (string?)element.Attribute("IsChecked") == "{Binding IsNamedTunnelMode}");
        Assert.Contains(
            document.Descendants(presentation + "TextBox"),
            element => (string?)element.Attribute("Text")
                           == "{Binding CloudflareNamedPublicOrigin, UpdateSourceTrigger=PropertyChanged}"
                       && (string?)element.Attribute("AutomationProperties.Name")
                           == "{DynamicResource L10n.remote.legacy.ui.namedPublicUrlAria}");
        Assert.Contains(
            document.Descendants(presentation + "TextBox"),
            element => (string?)element.Attribute("Text") == "{Binding LocalServiceUrl, Mode=OneWay}"
                       && (string?)element.Attribute("IsReadOnly") == "True");
        Assert.Contains(
            document.Descendants(presentation + "PasswordBox"),
            element => (string?)element.Attribute("PasswordChanged")
                           == "OnCloudflareNamedTunnelTokenPasswordChanged"
                       && (string?)element.Attribute("AutomationProperties.Name")
                           == "{DynamicResource L10n.remote.legacy.ui.tokenAria}");
        Assert.Contains(
            document.Descendants(presentation + "Button"),
            element => (string?)element.Attribute("Content")
                       == "{DynamicResource L10n.remote.legacy.ui.saveToken}"
                       && (string?)element.Attribute("Command")
                           == "{Binding SaveCloudflareNamedTunnelTokenCommand}");
        Assert.Contains(
            document.Descendants(presentation + "Button"),
            element => (string?)element.Attribute("Content")
                       == "{DynamicResource L10n.remote.legacy.ui.deleteToken}"
                       && (string?)element.Attribute("Command")
                           == "{Binding DeleteCloudflareNamedTunnelTokenCommand}");
        Assert.DoesNotContain(
            document.Descendants(),
            element => element.Name.LocalName == nameof(RevealPasswordBox)
                       && element.Attributes().Any(attribute => attribute.Value.Contains(
                           "CloudflareNamedTunnelToken",
                           StringComparison.Ordinal)));
        Assert.DoesNotContain(
            document.Descendants(presentation + "TextBox"),
            element => element.Attributes().Any(attribute => attribute.Value.Contains(
                "CloudflareNamedTunnelToken",
                StringComparison.Ordinal)));
    }

    [Fact]
    public void FunnelDialog_ExplainsPublicBetaScopeAndKeepsProviderSpecificSecretsHidden()
    {
        var document = XDocument.Load(GetAppSourcePath(
            Path.Combine("Dialogs", "RemoteAccessDialog.xaml")));
        XNamespace presentation = "http://schemas.microsoft.com/winfx/2006/xaml/presentation";

        Assert.Contains(
            document.Descendants(presentation + "RadioButton"),
            element => (string?)element.Attribute("Content")
                           == "{DynamicResource L10n.remote.legacy.ui.mode.funnel}"
                       && (string?)element.Attribute("IsChecked")
                           == "{Binding IsFunnelMode}");
        Assert.Contains(
            document.Descendants(presentation + "Button"),
            element => (string?)element.Attribute("Content")
                           == "{DynamicResource L10n.remote.legacy.ui.openTailscaleDownload}"
                       && (string?)element.Attribute("Visibility")
                           == "{Binding IsTailscaleProviderMode, Converter={StaticResource BoolToVisibility}}");

        var funnelPanel = Assert.Single(
            document.Descendants(presentation + "Border"),
            element => (string?)element.Attribute("Visibility")
                           == "{Binding IsFunnelMode, Converter={StaticResource BoolToVisibility}}");
        var funnelResources = funnelPanel.Descendants()
            .Select(element => (string?)element.Attribute("Text"))
            .Where(text => text is not null)
            .ToHashSet(StringComparer.Ordinal);
        Assert.Contains("{DynamicResource L10n.remote.legacy.ui.funnelDescription}", funnelResources);
        Assert.Contains("{DynamicResource L10n.remote.legacy.ui.funnelBoundary}", funnelResources);
        Assert.Contains("{DynamicResource L10n.remote.legacy.ui.funnelAuth}", funnelResources);

        var traditionalChinese = ProductLocalizationCatalog.GetDocument("zh-TW").Strings;
        Assert.Contains("第一次啟用", traditionalChinese["remote.legacy.ui.funnelDescription"], StringComparison.Ordinal);
        Assert.Contains("Beta", traditionalChinese["remote.legacy.ui.funnelDescription"], StringComparison.Ordinal);
        Assert.Contains("頻寬有限", traditionalChinese["remote.legacy.ui.funnelDescription"], StringComparison.Ordinal);
        Assert.Contains("只代理 MCSV Web", traditionalChinese["remote.legacy.ui.funnelBoundary"], StringComparison.Ordinal);
        Assert.Contains("不代理 Minecraft", traditionalChinese["remote.legacy.ui.funnelBoundary"], StringComparison.Ordinal);
        Assert.Contains(
            "不使用 Gmail／SMTP、cloudflared 或 Tunnel Token",
            traditionalChinese["remote.legacy.ui.funnelAuth"],
            StringComparison.Ordinal);
        Assert.Contains(
            funnelPanel.Descendants(presentation + "TextBox"),
            element => (string?)element.Attribute("Text")
                           == "{Binding LocalServiceUrl, Mode=OneWay}"
                       && (string?)element.Attribute("IsReadOnly") == "True");
        Assert.Contains(
            funnelPanel.Descendants(presentation + "Button"),
            element => (string?)element.Attribute("Command")
                           == "{Binding OpenTailscaleFunnelDocumentationCommand}");
        Assert.Empty(funnelPanel.Descendants(presentation + "PasswordBox"));
        Assert.DoesNotContain(
            funnelPanel.Descendants(),
            element => element.Attributes().Any(attribute => attribute.Value.Contains(
                "CloudflareNamedTunnelToken",
                StringComparison.Ordinal)));
        Assert.Contains(
            document.Descendants(presentation + "TextBlock"),
            element => (string?)element.Attribute("Text")
                           == "{Binding TailscaleHttpsCertificateGuidanceText, Mode=OneWay}");

        Assert.Contains(
            document.Descendants(presentation + "Border"),
            element => (string?)element.Attribute("Visibility")
                       == "{Binding IsTailscaleMode, Converter={StaticResource BoolToVisibility}}"
                       && element.Descendants(presentation + "TextBlock").Any(text =>
                           (string?)text.Attribute("Text")
                           == "{DynamicResource L10n.remote.legacy.ui.smtpHeading}"));
    }

    [Fact]
    public void SafeCloudflaredInstallCommand_FillsVerifiedExecutablePath()
    {
        WpfStaTestHost.Run(() =>
        {
            var coordinator = new RemoteAccessCoordinator(new StubRemoteBackend());
            var expectedPath = Path.GetFullPath(Path.Combine(
                Path.GetTempPath(),
                "mcsv-cloudflared-test",
                "cloudflared.exe"));
            try
            {
                using var viewModel = new RemoteAccessSettingsViewModel(
                    new RemoteControlSettings
                    {
                        AccessMode = RemoteAccessMode.CloudflareQuickTunnel,
                    },
                    coordinator,
                    _ => Task.CompletedTask,
                    Dispatcher.CurrentDispatcher,
                    new SuccessfulBootstrapService(expectedPath));

                Assert.True(viewModel.InstallCloudflaredCommand.CanExecute(null));
                viewModel.InstallCloudflaredCommand.Execute(null);

                Assert.Equal(expectedPath, viewModel.CloudflaredExecutablePath);
                Assert.Contains("SHA-256", viewModel.CloudflaredInstallStatus);
                Assert.True(coordinator.HasCloudflaredInstallationReceipt);
                Assert.False(viewModel.IsBusy);
            }
            finally
            {
                coordinator.DisposeAsync().AsTask().GetAwaiter().GetResult();
            }
        });
    }

    [Fact]
    public void RemoteWebConsoleDialog_XamlAndCodeBehindRemainInternal()
    {
        var document = XDocument.Load(GetAppSourcePath(
            Path.Combine("Dialogs", "RemoteWebConsoleDialog.xaml")));
        XNamespace xaml = "http://schemas.microsoft.com/winfx/2006/xaml";

        Assert.Equal("internal", (string?)document.Root?.Attribute(xaml + "ClassModifier"));
        Assert.False(typeof(RemoteWebConsoleDialog).IsPublic);
    }

    private static string GetAppSourcePath(string relativePath)
        => TestRepositoryPaths.AppSource(relativePath);

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

    private static void PumpDispatcherUntil(Func<bool> condition)
    {
        var deadline = DateTime.UtcNow.AddSeconds(5);
        while (!condition())
        {
            if (DateTime.UtcNow >= deadline)
            {
                throw new TimeoutException("WPF dispatcher condition timed out.");
            }

            var frame = new DispatcherFrame();
            _ = Dispatcher.CurrentDispatcher.BeginInvoke(
                () => frame.Continue = false,
                DispatcherPriority.Background);
            Dispatcher.PushFrame(frame);
        }
    }

    private sealed class StubRemoteBackend : IRemoteControlBackend
    {
        public ValueTask<RemoteDashboardDto> GetDashboardAsync(CancellationToken cancellationToken)
            => ValueTask.FromResult(new RemoteDashboardDto(
                DateTimeOffset.UtcNow,
                Array.Empty<RemoteServerSummaryDto>()));

        public ValueTask<RemoteServerDetailDto?> GetServerAsync(
            string serverId,
            CancellationToken cancellationToken)
            => ValueTask.FromResult<RemoteServerDetailDto?>(null);

        public ValueTask<RemoteConsolePageDto?> GetConsoleAsync(
            string serverId,
            RemoteConsoleQuery query,
            CancellationToken cancellationToken)
            => ValueTask.FromResult<RemoteConsolePageDto?>(null);

        public ValueTask<RemotePlayerListDto?> GetPlayersAsync(
            string serverId,
            CancellationToken cancellationToken)
            => ValueTask.FromResult<RemotePlayerListDto?>(null);

        public ValueTask<RemoteOperationResultDto> StartServerAsync(
            string serverId,
            CancellationToken cancellationToken)
            => Success();

        public ValueTask<RemoteOperationResultDto> StopServerAsync(
            string serverId,
            CancellationToken cancellationToken)
            => Success();

        public ValueTask<RemoteOperationResultDto> RestartServerAsync(
            string serverId,
            CancellationToken cancellationToken)
            => Success();

        public ValueTask<RemoteOperationResultDto> SendConsoleCommandAsync(
            string serverId,
            string command,
            CancellationToken cancellationToken)
            => Success();

        public ValueTask<RemoteOperationResultDto> PerformPlayerActionAsync(
            string serverId,
            RemotePlayerActionRequestDto request,
            CancellationToken cancellationToken)
            => Success();

        public ValueTask<RemoteOperationResultDto> CreateBackupAsync(
            string serverId,
            CancellationToken cancellationToken)
            => Success();

        private static ValueTask<RemoteOperationResultDto> Success()
            => ValueTask.FromResult(new RemoteOperationResultDto(true, "ok"));
    }

    private sealed class SuccessfulBootstrapService(string executablePath)
        : ICloudflaredBootstrapService
    {
        public Task<CloudflaredBootstrapResult> InstallLatestAsync(
            IProgress<CloudflaredBootstrapProgress>? progress = null,
            CancellationToken cancellationToken = default)
        {
            cancellationToken.ThrowIfCancellationRequested();
            progress?.Report(new CloudflaredBootstrapProgress("已完成", 100));
            return Task.FromResult(new CloudflaredBootstrapResult(
                executablePath,
                "2026.8.1",
                123,
                new string('a', 64)));
        }

        public void Dispose() { }
    }

    private sealed class PasswordBindingSource : INotifyPropertyChanged
    {
        private string _password = string.Empty;

        public event PropertyChangedEventHandler? PropertyChanged;

        public string Password
        {
            get => _password;
            set
            {
                if (string.Equals(_password, value, StringComparison.Ordinal)) return;
                _password = value;
                PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(nameof(Password)));
            }
        }
    }
}
