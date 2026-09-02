using System.Diagnostics;
using System.Drawing;

namespace MinecraftServerManager.Installer;

internal sealed class InstallerForm : Form
{
    private static readonly Color WindowBackground = Color.FromArgb(14, 18, 24);
    private static readonly Color PanelBackground = Color.FromArgb(24, 30, 39);
    private static readonly Color Border = Color.FromArgb(52, 62, 74);
    private static readonly Color TextPrimary = Color.FromArgb(242, 246, 251);
    private static readonly Color TextSecondary = Color.FromArgb(166, 181, 198);
    private static readonly Color Accent = Color.FromArgb(36, 151, 91);
    private static readonly Color AccentHover = Color.FromArgb(45, 177, 107);

    private readonly InstallerBundle _bundle;
    private readonly TextBox _installPath = new();
    private readonly Button _browseButton = new();
    private readonly Button _installButton = new();
    private readonly ProgressBar _progress = new();
    private readonly Label _status = new();
    private readonly Label _validation = new();
    private readonly CancellationTokenSource _lifetime = new();
    private bool _installing;
    private string? _installedShortcut;

    public InstallerForm(InstallerBundle bundle)
    {
        _bundle = bundle ?? throw new ArgumentNullException(nameof(bundle));
        SuspendLayout();
        Text = "X MCSV 安裝程式";
        AutoScaleMode = AutoScaleMode.Dpi;
        ClientSize = new Size(960, 540);
        MinimumSize = new Size(800, 450);
        StartPosition = FormStartPosition.Manual;
        BackColor = WindowBackground;
        ForeColor = TextPrimary;
        Font = new Font("Microsoft JhengHei UI", 10F, FontStyle.Regular, GraphicsUnit.Point);
        FormBorderStyle = FormBorderStyle.FixedSingle;
        MaximizeBox = false;

        var title = new Label
        {
            Text = "安裝 X MCSV",
            Font = new Font(Font.FontFamily, 24F, FontStyle.Bold),
            ForeColor = TextPrimary,
            AutoSize = true,
            Location = new Point(42, 35),
        };
        var beta = new Label
        {
            Text = $"{_bundle.Metadata.Version} · {_bundle.Metadata.Channel.ToUpperInvariant()} · 研發中",
            Font = new Font(Font.FontFamily, 10.5F, FontStyle.Regular),
            ForeColor = Color.FromArgb(82, 222, 144),
            AutoSize = true,
            Location = new Point(46, 87),
        };
        var intro = new Label
        {
            Text = "單一 EXE 會建立 GUI、Windows Service、客戶端與更新所需的完整目錄。\r\n所有永久檔案都集中在下方選擇的位置，不再分散到 AppData 或 ProgramData。\r\nUAC 請以目前登入帳號直接按「是」；若要求輸入另一個管理員帳號，請取消。",
            ForeColor = TextSecondary,
            AutoSize = true,
            Location = new Point(46, 125),
        };

        var card = new Panel
        {
            Location = new Point(40, 185),
            Size = new Size(880, 220),
            BackColor = PanelBackground,
            BorderStyle = BorderStyle.FixedSingle,
        };
        var locationLabel = new Label
        {
            Text = "安裝位置",
            Font = new Font(Font.FontFamily, 11.5F, FontStyle.Bold),
            AutoSize = true,
            Location = new Point(24, 24),
            ForeColor = TextPrimary,
        };
        var locationHint = new Label
        {
            Text = "預設為 C:\\Program Files\\MCSV；也可選擇其他本機 NTFS 固定磁碟。",
            AutoSize = true,
            Location = new Point(24, 55),
            ForeColor = TextSecondary,
        };
        _installPath.Location = new Point(24, 91);
        _installPath.Size = new Size(710, 33);
        _installPath.Text = InstallerLayout.DefaultRoot;
        _installPath.BackColor = WindowBackground;
        _installPath.ForeColor = TextPrimary;
        _installPath.BorderStyle = BorderStyle.FixedSingle;
        _installPath.Font = new Font(Font.FontFamily, 11F);
        _installPath.TextChanged += (_, _) => ValidateSelectedPath();

        ConfigureButton(_browseButton, "瀏覽…", new Point(748, 88), new Size(104, 38), secondary: true);
        _browseButton.Click += BrowseButtonOnClick;
        _validation.AutoSize = false;
        _validation.Location = new Point(24, 137);
        _validation.Size = new Size(828, 50);
        _validation.ForeColor = TextSecondary;
        _validation.Text = "永久資料為 versions、service、exchange 與 users；安裝暫存完成後自動清除。";

        card.Controls.AddRange([locationLabel, locationHint, _installPath, _browseButton, _validation]);

        _status.AutoSize = false;
        _status.Location = new Point(43, 427);
        _status.Size = new Size(650, 28);
        _status.Text = "準備安裝";
        _status.ForeColor = TextSecondary;
        _progress.Location = new Point(43, 459);
        _progress.Size = new Size(650, 12);
        _progress.Style = ProgressBarStyle.Continuous;
        _progress.Minimum = 0;
        _progress.Maximum = 100;

        ConfigureButton(_installButton, "安裝", new Point(736, 433), new Size(184, 50), secondary: false);
        _installButton.Font = new Font(Font.FontFamily, 12F, FontStyle.Bold);
        _installButton.Click += InstallButtonOnClick;
        AcceptButton = _installButton;

        Controls.AddRange([title, beta, intro, card, _status, _progress, _installButton]);
        ResumeLayout(false);
        PerformLayout();
        Shown += (_, _) => PlaceOnPrimaryScreen();
        FormClosing += InstallerFormOnFormClosing;
        ValidateSelectedPath();
    }

    private void PlaceOnPrimaryScreen()
    {
        var primary = Screen.PrimaryScreen?.WorkingArea ?? Screen.AllScreens[0].WorkingArea;
        Location = new Point(
            primary.Left + Math.Max(0, (primary.Width - Width) / 2),
            primary.Top + Math.Max(0, (primary.Height - Height) / 2));
        Activate();
    }

    private void ValidateSelectedPath()
    {
        try
        {
            var layout = InstallerLayout.Resolve(_installPath.Text, _bundle.Metadata.Channel);
            _validation.Text = $"完整資料將集中於：{layout.Root}";
            _validation.ForeColor = Color.FromArgb(99, 220, 154);
            _installButton.Enabled = !_installing;
        }
        catch (Exception exception) when (exception is ArgumentException or IOException or InvalidOperationException)
        {
            _validation.Text = exception.Message;
            _validation.ForeColor = Color.FromArgb(242, 113, 113);
            _installButton.Enabled = false;
        }
    }

    private void BrowseButtonOnClick(object? sender, EventArgs e)
    {
        using var dialog = new FolderBrowserDialog
        {
            Description = "選擇 X MCSV 的完整安裝位置",
            UseDescriptionForTitle = true,
            ShowNewFolderButton = true,
            InitialDirectory = Directory.Exists(_installPath.Text)
                ? _installPath.Text
                : Environment.GetFolderPath(Environment.SpecialFolder.ProgramFiles),
        };
        if (dialog.ShowDialog(this) == DialogResult.OK)
        {
            _installPath.Text = dialog.SelectedPath;
        }
    }

    private async void InstallButtonOnClick(object? sender, EventArgs e)
    {
        if (_installing)
        {
            return;
        }

        _installing = true;
        _installButton.Enabled = false;
        _browseButton.Enabled = false;
        _installPath.ReadOnly = true;
        UseWaitCursor = true;
        try
        {
            var progress = new Progress<InstallerProgress>(value =>
            {
                _progress.Value = Math.Clamp(value.Percentage, 0, 100);
                _status.Text = value.Message;
            });
            _ = await new InstallerEngine()
                .InstallAsync(_bundle, _installPath.Text, progress, _lifetime.Token);
            _status.Text = "X MCSV 已安裝完成，可從開始功能表開啟。";
            _installedShortcut = Path.Combine(
                Environment.GetFolderPath(Environment.SpecialFolder.CommonPrograms),
                "X MCSV",
                $"X MCSV ({_bundle.Metadata.Channel}).lnk");
            _installButton.Text = "開啟 X MCSV";
            _installButton.Enabled = true;
            _installButton.Click -= InstallButtonOnClick;
            _installButton.Click += OpenInstalledProduct;
            MessageBox.Show(
                this,
                "安裝完成。\r\n\r\n所有永久檔案都位於：\r\n" + Path.GetFullPath(_installPath.Text),
                "X MCSV",
                MessageBoxButtons.OK,
                MessageBoxIcon.Information);
        }
        catch (OperationCanceledException) when (_lifetime.IsCancellationRequested)
        {
            _status.Text = "安裝已取消。";
        }
        catch (Exception exception) when (exception is not OutOfMemoryException and not StackOverflowException)
        {
            _status.Text = exception is InstallerStageException { RollbackHadErrors: true }
                ? "安裝失敗；部分回復步驟未完成，請查看詳細資訊。"
                : "安裝失敗；作業已停止，請查看詳細資訊。";
            MessageBox.Show(
                this,
                exception.Message,
                "X MCSV 安裝失敗",
                MessageBoxButtons.OK,
                MessageBoxIcon.Error);
            _installing = false;
            _browseButton.Enabled = true;
            _installPath.ReadOnly = false;
            ValidateSelectedPath();
        }
        finally
        {
            UseWaitCursor = false;
        }
    }

    private void OpenInstalledProduct(object? sender, EventArgs e)
    {
        try
        {
            if (string.IsNullOrWhiteSpace(_installedShortcut) || !File.Exists(_installedShortcut))
            {
                throw new FileNotFoundException("開始功能表捷徑不存在。", _installedShortcut);
            }

            var explorer = Path.Combine(
                Environment.GetFolderPath(Environment.SpecialFolder.Windows),
                "explorer.exe");
            var startInfo = new ProcessStartInfo
            {
                FileName = explorer,
                UseShellExecute = false,
            };
            startInfo.ArgumentList.Add(_installedShortcut);
            _ = Process.Start(startInfo)
                ?? throw new InvalidOperationException("Windows Explorer 未接受啟動要求。");
            Close();
        }
        catch (Exception exception) when (exception is IOException or InvalidOperationException)
        {
            MessageBox.Show(
                this,
                "無法自動開啟，但安裝已完成。請從開始功能表開啟 X MCSV。\r\n\r\n" + exception.Message,
                "X MCSV",
                MessageBoxButtons.OK,
                MessageBoxIcon.Warning);
        }
    }

    private void InstallerFormOnFormClosing(object? sender, FormClosingEventArgs e)
    {
        if (_installing && _progress.Value < 100)
        {
            e.Cancel = true;
            MessageBox.Show(
                this,
                "安裝進行中，完成前不可直接關閉視窗。",
                "X MCSV",
                MessageBoxButtons.OK,
                MessageBoxIcon.Information);
        }
    }

    private static void ConfigureButton(
        Button button,
        string text,
        Point location,
        Size size,
        bool secondary)
    {
        button.Text = text;
        button.Location = location;
        button.Size = size;
        button.FlatStyle = FlatStyle.Flat;
        button.FlatAppearance.BorderSize = 1;
        button.FlatAppearance.BorderColor = secondary ? Border : Color.FromArgb(64, 220, 136);
        button.BackColor = secondary ? PanelBackground : Accent;
        button.ForeColor = TextPrimary;
        button.Cursor = Cursors.Hand;
        button.MouseEnter += (_, _) => button.BackColor = secondary
            ? Color.FromArgb(34, 42, 53)
            : AccentHover;
        button.MouseLeave += (_, _) => button.BackColor = secondary ? PanelBackground : Accent;
    }
}
