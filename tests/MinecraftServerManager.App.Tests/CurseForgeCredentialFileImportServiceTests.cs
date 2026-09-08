using System.Diagnostics;
using System.IO;
using System.Runtime.InteropServices;
using System.Security;
using System.Security.Cryptography;
using System.Text;
using MinecraftServerManager.App.Services;

namespace MinecraftServerManager.App.Tests;

public sealed class CurseForgeCredentialFileImportServiceTests
{
    [Fact]
    public void ApplicationPaths_KeepPlaintextImportInsideCurrentUsersClientSecrets()
    {
        using var directory = new AppearanceThemeServiceTests.TestDirectory();
        var paths = new ApplicationPaths(directory.Path);

        Assert.Equal(
            Path.Combine(paths.ClientSecrets, "curseforge-api-key.import.txt"),
            paths.CurseForgeCredentialImportFile);
        Assert.StartsWith(
            Path.GetFullPath(paths.ClientSecrets) + Path.DirectorySeparatorChar,
            Path.GetFullPath(paths.CurseForgeCredentialImportFile),
            StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public void PrepareEditableFile_CreatesAKeylessUtf8TemplateAndPreservesExistingEdits()
    {
        using var directory = new AppearanceThemeServiceTests.TestDirectory();
        using var store = new RecordingCredentialStore();
        var paths = new ApplicationPaths(directory.Path);
        var service = new CurseForgeCredentialFileImportService(paths, store);

        var prepared = service.PrepareEditableFile();

        Assert.Equal(paths.CurseForgeCredentialImportFile, prepared);
        var templateBytes = File.ReadAllBytes(prepared);
        var template = new UTF8Encoding(false, true).GetString(templateBytes);
        Assert.Contains("one-time import", template, StringComparison.Ordinal);
        Assert.Empty(EnumerateCredentialLines(template));

        var synthetic = CreateSyntheticCredential();
        File.AppendAllText(prepared, synthetic + Environment.NewLine, new UTF8Encoding(false));
        var editedBytes = File.ReadAllBytes(prepared);

        Assert.Equal(prepared, service.PrepareEditableFile());
        Assert.Equal(editedBytes, File.ReadAllBytes(prepared));
    }

    [Fact]
    public void ImportIfPresent_WhenFileIsMissing_ReturnsMissingWithoutCallingStore()
    {
        using var directory = new AppearanceThemeServiceTests.TestDirectory();
        using var store = new RecordingCredentialStore();
        var service = new CurseForgeCredentialFileImportService(
            new ApplicationPaths(directory.Path),
            store);

        var result = service.ImportIfPresent();

        Assert.Equal(CurseForgeCredentialImportResult.Missing, result);
        Assert.Equal(0, store.SaveCount);
    }

    [Fact]
    public void ImportIfPresent_WithUntouchedTemplate_ReturnsTemplateEmptyAndKeepsTemplate()
    {
        using var directory = new AppearanceThemeServiceTests.TestDirectory();
        using var store = new RecordingCredentialStore();
        var service = new CurseForgeCredentialFileImportService(
            new ApplicationPaths(directory.Path),
            store);
        var path = service.PrepareEditableFile();

        var result = service.ImportIfPresent();

        Assert.Equal(CurseForgeCredentialImportResult.TemplateEmpty, result);
        Assert.True(File.Exists(path));
        Assert.Equal(0, store.SaveCount);
    }

    [Fact]
    public void ImportIfPresent_WithOneValue_SavesReadOnlySecureStringThenDeletesPlaintext()
    {
        using var directory = new AppearanceThemeServiceTests.TestDirectory();
        using var store = new RecordingCredentialStore();
        var service = new CurseForgeCredentialFileImportService(
            new ApplicationPaths(directory.Path),
            store);
        var path = service.PrepareEditableFile();
        var synthetic = CreateSyntheticCredential();
        File.AppendAllText(path, synthetic + Environment.NewLine, new UTF8Encoding(false));

        var result = service.ImportIfPresent();

        Assert.Equal(CurseForgeCredentialImportResult.Imported, result);
        Assert.Equal(1, store.SaveCount);
        Assert.True(store.ReceivedReadOnlyCredential);
        Assert.Equal(synthetic, store.ReadForAssertion());
        Assert.False(File.Exists(path));
    }

    [Theory]
    [InlineData("test-only-first\ntest-only-second\n")]
    [InlineData("test-only-first\r\ntest-only-second\r\n")]
    [InlineData("test-only\tvalue\n")]
    [InlineData("test-only\0value\n")]
    [InlineData("test-only-lone-carriage-return\r")]
    [InlineData(" test-only-leading-space\n")]
    [InlineData("test-only-trailing-space \n")]
    public void ImportIfPresent_WithMultipleValuesOrInvalidCharacters_FailsClosed(string contents)
    {
        using var directory = new AppearanceThemeServiceTests.TestDirectory();
        using var store = new RecordingCredentialStore();
        var paths = new ApplicationPaths(directory.Path);
        Directory.CreateDirectory(paths.ClientSecrets);
        File.WriteAllText(
            paths.CurseForgeCredentialImportFile,
            contents,
            new UTF8Encoding(false));
        var service = new CurseForgeCredentialFileImportService(paths, store);

        var result = service.ImportIfPresent();

        Assert.Equal(CurseForgeCredentialImportResult.Invalid, result);
        Assert.Equal(0, store.SaveCount);
        Assert.True(File.Exists(paths.CurseForgeCredentialImportFile));
    }

    [Fact]
    public void ImportIfPresent_WithOversizedInput_FailsClosedWithoutReadingIntoStore()
    {
        using var directory = new AppearanceThemeServiceTests.TestDirectory();
        using var store = new RecordingCredentialStore();
        var paths = new ApplicationPaths(directory.Path);
        Directory.CreateDirectory(paths.ClientSecrets);
        File.WriteAllBytes(paths.CurseForgeCredentialImportFile, new byte[4097]);
        var service = new CurseForgeCredentialFileImportService(paths, store);

        var result = service.ImportIfPresent();

        Assert.Equal(CurseForgeCredentialImportResult.Invalid, result);
        Assert.Equal(0, store.SaveCount);
        Assert.Equal(4097, new FileInfo(paths.CurseForgeCredentialImportFile).Length);
    }

    [Fact]
    public void ImportIfPresent_WithMalformedUtf8_FailsClosedWithoutCallingStore()
    {
        using var directory = new AppearanceThemeServiceTests.TestDirectory();
        using var store = new RecordingCredentialStore();
        var paths = new ApplicationPaths(directory.Path);
        Directory.CreateDirectory(paths.ClientSecrets);
        File.WriteAllBytes(paths.CurseForgeCredentialImportFile, [0xc3, 0x28]);
        var service = new CurseForgeCredentialFileImportService(paths, store);

        var result = service.ImportIfPresent();

        Assert.Equal(CurseForgeCredentialImportResult.Invalid, result);
        Assert.Equal(0, store.SaveCount);
        Assert.True(File.Exists(paths.CurseForgeCredentialImportFile));
    }

    [Fact]
    public void ImportIfPresent_WhenCredentialStoreFails_RemovesPlaintextAndReportsFailure()
    {
        using var directory = new AppearanceThemeServiceTests.TestDirectory();
        var paths = new ApplicationPaths(directory.Path);
        var service = new CurseForgeCredentialFileImportService(
            paths,
            new ThrowingCredentialStore());
        var path = service.PrepareEditableFile();
        var synthetic = CreateSyntheticCredential();
        File.AppendAllText(path, synthetic + Environment.NewLine, new UTF8Encoding(false));

        var result = service.ImportIfPresent();

        Assert.Equal(CurseForgeCredentialImportResult.Failed, result);
        Assert.False(File.Exists(path));
    }

    [Fact]
    public void ImportAndTemplatePreparation_RejectAReparsePointSecretsDirectory()
    {
        if (!OperatingSystem.IsWindows())
        {
            return;
        }

        using var directory = new AppearanceThemeServiceTests.TestDirectory();
        var paths = new ApplicationPaths(directory.Path);
        Directory.CreateDirectory(paths.ClientRoot);
        var outside = Path.Combine(directory.Path, "outside-secrets");
        Directory.CreateDirectory(outside);
        CreateDirectoryJunction(paths.ClientSecrets, outside);
        try
        {
            using var store = new RecordingCredentialStore();
            var service = new CurseForgeCredentialFileImportService(paths, store);

            Assert.Throws<InvalidDataException>(() => service.PrepareEditableFile());
            Assert.Equal(
                CurseForgeCredentialImportResult.Invalid,
                service.ImportIfPresent());
            Assert.Equal(0, store.SaveCount);
            Assert.Empty(Directory.EnumerateFileSystemEntries(outside));
        }
        finally
        {
            if (Directory.Exists(paths.ClientSecrets))
            {
                Directory.Delete(paths.ClientSecrets);
            }
        }
    }

    [Fact]
    public void SourceContract_BoundsAndZeroesPlaintextWithoutLoggingOrReturningIt()
    {
        var source = File.ReadAllText(TestRepositoryPaths.AppSource(
            Path.Combine("Services", "CurseForgeCredentialFileImportService.cs")));

        Assert.Contains("MaximumImportBytes", source, StringComparison.Ordinal);
        Assert.Contains("FileShare.None", source, StringComparison.Ordinal);
        Assert.Contains("FileAttributes.ReparsePoint", source, StringComparison.Ordinal);
        Assert.Contains("CryptographicOperations.ZeroMemory", source, StringComparison.Ordinal);
        Assert.Contains("TryScrubPlaintext", source, StringComparison.Ordinal);
        Assert.Contains("TryDeleteFile(_importFilePath)", source, StringComparison.Ordinal);
        Assert.DoesNotContain("ILogger", source, StringComparison.Ordinal);
        Assert.DoesNotContain("Console.", source, StringComparison.Ordinal);
        Assert.DoesNotContain("ProcessStartInfo", source, StringComparison.Ordinal);
        Assert.DoesNotContain("Environment.SetEnvironmentVariable", source, StringComparison.Ordinal);
        Assert.DoesNotContain("PtrToString", source, StringComparison.Ordinal);
    }

    private static IEnumerable<string> EnumerateCredentialLines(string contents)
        => contents
            .Split(['\r', '\n'], StringSplitOptions.RemoveEmptyEntries)
            .Where(line => !line.StartsWith('#'));

    private static string CreateSyntheticCredential()
        => $"test-only-import-{Guid.NewGuid():N}";

    private static string ReadForAssertion(SecureString credential)
    {
        var native = IntPtr.Zero;
        try
        {
            native = Marshal.SecureStringToBSTR(credential);
            return Marshal.PtrToStringBSTR(native);
        }
        finally
        {
            if (native != IntPtr.Zero)
            {
                Marshal.ZeroFreeBSTR(native);
            }
        }
    }

    private static void CreateDirectoryJunction(string linkPath, string targetPath)
    {
        var startInfo = new ProcessStartInfo(
            Environment.GetEnvironmentVariable("ComSpec") ?? "cmd.exe")
        {
            CreateNoWindow = true,
            RedirectStandardError = true,
            RedirectStandardOutput = true,
            UseShellExecute = false
        };
        startInfo.ArgumentList.Add("/d");
        startInfo.ArgumentList.Add("/c");
        startInfo.ArgumentList.Add("mklink");
        startInfo.ArgumentList.Add("/J");
        startInfo.ArgumentList.Add(linkPath);
        startInfo.ArgumentList.Add(targetPath);
        using var process = Process.Start(startInfo)
            ?? throw new InvalidOperationException("Could not create test junction.");
        var standardOutput = process.StandardOutput.ReadToEnd();
        var standardError = process.StandardError.ReadToEnd();
        process.WaitForExit();
        Assert.True(
            process.ExitCode == 0,
            $"Could not create test junction: {standardError}{standardOutput}");
        Assert.True(File.GetAttributes(linkPath).HasFlag(FileAttributes.ReparsePoint));
    }

    private sealed class RecordingCredentialStore : ICurseForgeCredentialStore, IDisposable
    {
        private SecureString? _credential;

        public int SaveCount { get; private set; }

        public bool ReceivedReadOnlyCredential { get; private set; }

        public bool HasCredential => _credential is not null;

        public SecureString? AcquireReadOnly()
        {
            var copy = _credential?.Copy();
            copy?.MakeReadOnly();
            return copy;
        }

        public void Save(SecureString credential)
        {
            SaveCount++;
            ReceivedReadOnlyCredential = credential.IsReadOnly();
            var copy = credential.Copy();
            copy.MakeReadOnly();
            _credential?.Dispose();
            _credential = copy;
        }

        public bool Delete()
        {
            var existed = _credential is not null;
            _credential?.Dispose();
            _credential = null;
            return existed;
        }

        public string? ReadForAssertion()
            => _credential is null ? null : CurseForgeCredentialFileImportServiceTests.ReadForAssertion(_credential);

        public void Dispose()
        {
            _credential?.Dispose();
            _credential = null;
        }
    }

    private sealed class ThrowingCredentialStore : ICurseForgeCredentialStore
    {
        public bool HasCredential => false;

        public SecureString? AcquireReadOnly() => null;

        public void Save(SecureString credential)
            => throw new CryptographicException("Synthetic DPAPI failure.");

        public bool Delete() => false;
    }
}
