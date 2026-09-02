using System.Diagnostics;

namespace MinecraftServerManager.Installer;

internal static class Program
{
    [STAThread]
    private static int Main(string[] args)
    {
        if (args.Length > 0)
        {
            return VerifyBundleWithoutUi(args);
        }

        if (!OperatingSystem.IsWindows() || !Environment.Is64BitOperatingSystem)
        {
            MessageBox.Show(
                "X MCSV 安裝程式僅支援 Windows x64。",
                "無法安裝",
                MessageBoxButtons.OK,
                MessageBoxIcon.Error);
            return 2;
        }

        ApplicationConfiguration.Initialize();
        try
        {
            var executable = Environment.ProcessPath
                ?? Process.GetCurrentProcess().MainModule?.FileName
                ?? throw new InvalidOperationException("無法取得目前安裝 EXE 路徑。");
            // Keep the WinForms message loop on the original STA entry thread. Awaiting before
            // Application.Run has no UI synchronization context and may resume on an MTA worker.
            using var bundle = InstallerBundle.OpenAsync(executable).GetAwaiter().GetResult();
            Application.Run(new InstallerForm(bundle));
            return 0;
        }
        catch (Exception exception) when (exception is not OutOfMemoryException and not StackOverflowException)
        {
            MessageBox.Show(
                "安裝 EXE 驗證失敗，沒有變更任何 MCSV 資料。\r\n\r\n" + exception.Message,
                "X MCSV 安裝程式",
                MessageBoxButtons.OK,
                MessageBoxIcon.Error);
            return 10;
        }
    }

    private static int VerifyBundleWithoutUi(string[] args)
    {
        if (args.Length != 2 ||
            !string.Equals(args[0], "--verify-bundle", StringComparison.Ordinal) ||
            string.IsNullOrWhiteSpace(args[1]))
        {
            Console.Error.WriteLine("Usage: Muhun MCSV Setup.dll --verify-bundle <signed-setup.exe>");
            return 64;
        }

        try
        {
            using var bundle = InstallerBundle.OpenAsync(args[1]).GetAwaiter().GetResult();
            Console.WriteLine(
                $"Verified X MCSV installer bundle: {bundle.Metadata.Version} ({bundle.Metadata.Channel})");
            return 0;
        }
        catch (Exception exception) when (exception is not OutOfMemoryException and not StackOverflowException)
        {
            Console.Error.WriteLine("Installer bundle verification failed: " + exception.Message);
            return 10;
        }
    }
}
