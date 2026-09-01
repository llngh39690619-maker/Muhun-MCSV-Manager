using System.Text;
using MinecraftServerManager.Core.Services;

namespace MinecraftServerManager.Core.Tests;

public sealed class ServerPropertiesPortServiceTests
{
    [Fact]
    public void TryReadServerPort_IgnoresCommentsAndInvalidValues()
    {
        const string contents = "# server-port=25560\nserver-port=70000\n\tserver-port = 25567\n";

        var found = ServerPropertiesPortEditor.TryReadServerPort(contents, out var port);

        Assert.True(found);
        Assert.Equal(25567, port);
    }

    [Fact]
    public void SetServerPort_PreservesOtherContentAndRemovesActiveDuplicates()
    {
        const string contents =
            "# generated settings\r\n" +
            "motd=測試伺服器\r\n" +
            "  server-port = 25565\r\n" +
            "# server-port=commented\r\n" +
            "server-port=25566\r\n" +
            "query.port=25565\r\n" +
            "rcon.port=25575\r\n";
        const string expected =
            "# generated settings\r\n" +
            "motd=測試伺服器\r\n" +
            "  server-port=25570\r\n" +
            "# server-port=commented\r\n" +
            "query.port=25565\r\n" +
            "rcon.port=25575\r\n";

        var updated = ServerPropertiesPortEditor.SetServerPort(contents, 25570);

        Assert.Equal(expected, updated);
    }

    [Fact]
    public void SetServerPort_WhenMissing_AppendsUsingExistingLineEnding()
    {
        const string contents = "motd=test\nmax-players=20\n";

        var updated = ServerPropertiesPortEditor.SetServerPort(contents, 25566);

        Assert.Equal("motd=test\nmax-players=20\nserver-port=25566\n", updated);
    }

    [Fact]
    public async Task ReadServerPortAsync_ReadsOnlyTheExplicitFile()
    {
        using var directory = new TemporaryDirectory();
        var requestedPath = Path.Combine(directory.Path, "requested.properties");
        await File.WriteAllTextAsync(requestedPath, "server-port=25572\n");
        await File.WriteAllTextAsync(
            Path.Combine(directory.Path, "server.properties"),
            "server-port=25599\n");
        var service = new ServerPropertiesPortService();

        var port = await service.ReadServerPortAsync(requestedPath);

        Assert.Equal(25572, port);
    }

    [Fact]
    public async Task SetServerPortAsync_CreatesBackupAndAtomicallyReplacesContent()
    {
        using var directory = new TemporaryDirectory();
        var filePath = Path.Combine(directory.Path, "server.properties");
        const string original = "# settings\r\nmotd=測試\r\nserver-port=25565\r\n";
        await File.WriteAllTextAsync(filePath, original, new UTF8Encoding(encoderShouldEmitUTF8Identifier: true));
        var service = new ServerPropertiesPortService();

        var result = await service.SetServerPortAsync(filePath, 25566);

        Assert.Equal(filePath, result.FilePath);
        Assert.Equal(filePath + ".bak", result.BackupPath);
        Assert.Equal(25566, result.Port);
        Assert.Equal(original, await File.ReadAllTextAsync(filePath + ".bak"));
        Assert.Equal(
            "# settings\r\nmotd=測試\r\nserver-port=25566\r\n",
            await File.ReadAllTextAsync(filePath));
        Assert.True((await File.ReadAllBytesAsync(filePath)).AsSpan().StartsWith(Encoding.UTF8.GetPreamble()));
        Assert.Empty(Directory.EnumerateFiles(directory.Path, "*.tmp"));
    }

    [Fact]
    public async Task SetServerPortAsync_WhenFileDoesNotExist_AppendsPropertyWithoutBackup()
    {
        using var directory = new TemporaryDirectory();
        var filePath = Path.Combine(directory.Path, "server.properties");
        var service = new ServerPropertiesPortService();

        var result = await service.SetServerPortAsync(filePath, 25565);

        Assert.Null(result.BackupPath);
        Assert.Equal("server-port=25565", await File.ReadAllTextAsync(filePath));
        Assert.False(File.Exists(filePath + ".bak"));
    }

    [Fact]
    public async Task SetServerPortAsync_InvalidUtf8FallsBackToLatin1WithoutCorruptingOtherBytes()
    {
        using var directory = new TemporaryDirectory();
        var filePath = Path.Combine(directory.Path, "server.properties");
        var original = Encoding.Latin1.GetBytes(
            "motd=Caf\u00e9\r\n" +
            "server-port=25565\n" +
            "query.port=25565\r" +
            "rcon.port=25575\r\n");
        await File.WriteAllBytesAsync(filePath, original);
        var service = new ServerPropertiesPortService();

        await service.SetServerPortAsync(filePath, 25566);

        var expected = Encoding.Latin1.GetBytes(
            "motd=Caf\u00e9\r\n" +
            "server-port=25566\n" +
            "query.port=25565\r" +
            "rcon.port=25575\r\n");
        Assert.Equal(expected, await File.ReadAllBytesAsync(filePath));
        Assert.Equal(original, await File.ReadAllBytesAsync(filePath + ".bak"));
    }

    [Fact]
    public async Task SetServerPortAsync_ValidUtf8WithoutBomRemainsUtf8WithoutBom()
    {
        using var directory = new TemporaryDirectory();
        var filePath = Path.Combine(directory.Path, "server.properties");
        var original = new UTF8Encoding(encoderShouldEmitUTF8Identifier: false).GetBytes(
            "motd=\u6e2c\u8a66\nserver-port=25565\n");
        await File.WriteAllBytesAsync(filePath, original);
        var service = new ServerPropertiesPortService();

        await service.SetServerPortAsync(filePath, 25566);

        var updated = await File.ReadAllBytesAsync(filePath);
        Assert.False(updated.AsSpan().StartsWith(Encoding.UTF8.GetPreamble()));
        Assert.Equal(
            "motd=\u6e2c\u8a66\nserver-port=25566\n",
            new UTF8Encoding(false, true).GetString(updated));
    }

    [Fact]
    public async Task SetServerPortAsync_InvalidUtf8WithBomPreservesExactBomAndBodyBytes()
    {
        using var directory = new TemporaryDirectory();
        var filePath = Path.Combine(directory.Path, "server.properties");
        var body = Encoding.Latin1.GetBytes("motd=Caf\u00e9\nserver-port=25565\n");
        var original = Encoding.UTF8.GetPreamble().Concat(body).ToArray();
        await File.WriteAllBytesAsync(filePath, original);
        var service = new ServerPropertiesPortService();

        await service.SetServerPortAsync(filePath, 25566);

        var expectedBody = Encoding.Latin1.GetBytes("motd=Caf\u00e9\nserver-port=25566\n");
        var expected = Encoding.UTF8.GetPreamble().Concat(expectedBody).ToArray();
        Assert.Equal(expected, await File.ReadAllBytesAsync(filePath));
        Assert.Equal(original, await File.ReadAllBytesAsync(filePath + ".bak"));
    }

    [Fact]
    public async Task DocumentEditor_Latin1RoundTripPreservesRawBytesAndMixedLineEndings()
    {
        using var directory = new TemporaryDirectory();
        var filePath = Path.Combine(directory.Path, "server.properties");
        var original = Encoding.Latin1.GetBytes(
            "motd=Caf\u00e9\r\n" +
            "max-players=20\n" +
            "query.port=25565\r" +
            "rcon.port=25575\r\n");
        await File.WriteAllBytesAsync(filePath, original);
        var service = new ServerPropertiesPortService();

        var document = Assert.IsType<ServerPropertiesDocument>(
            await service.ReadDocumentAsync(filePath));
        Assert.Equal("iso-8859-1", document.FormatToken.EncodingName);
        Assert.False(document.FormatToken.HasByteOrderMark);
        Assert.Equal(0, document.FormatToken.ByteOrderMarkLength);

        var editedText = document.Text.Replace(
            "max-players=20",
            "max-players=21",
            StringComparison.Ordinal);
        var result = await service.SaveDocumentAsync(
            filePath,
            editedText,
            document.FormatToken);

        var expected = Encoding.Latin1.GetBytes(
            "motd=Caf\u00e9\r\n" +
            "max-players=21\n" +
            "query.port=25565\r" +
            "rcon.port=25575\r\n");
        Assert.Equal(expected, await File.ReadAllBytesAsync(filePath));
        Assert.Equal(original, await File.ReadAllBytesAsync(filePath + ".bak"));
        Assert.Equal(filePath + ".bak", result.BackupPath);
        Assert.Equal("iso-8859-1", result.FormatToken.EncodingName);
        Assert.Empty(Directory.EnumerateFiles(directory.Path, "*.tmp"));
    }

    [Fact]
    public async Task DocumentEditor_Utf8BomIsPreservedWhileEditorLineEndingsAreHonored()
    {
        using var directory = new TemporaryDirectory();
        var filePath = Path.Combine(directory.Path, "server.properties");
        const string originalText = "motd=\u6e2c\u8a66\r\nserver-port=25565\r\n";
        var original = Encoding.UTF8.GetPreamble()
            .Concat(new UTF8Encoding(false, true).GetBytes(originalText))
            .ToArray();
        await File.WriteAllBytesAsync(filePath, original);
        var service = new ServerPropertiesPortService();

        var document = Assert.IsType<ServerPropertiesDocument>(
            await service.ReadDocumentAsync(filePath));
        Assert.Equal("utf-8", document.FormatToken.EncodingName);
        Assert.True(document.FormatToken.HasByteOrderMark);
        Assert.Equal(Encoding.UTF8.GetPreamble().Length, document.FormatToken.ByteOrderMarkLength);

        const string editedText = "motd=\u65b0\u6e2c\u8a66\nserver-port=25566\n";
        await service.SaveDocumentAsync(filePath, editedText, document.FormatToken);

        var expected = Encoding.UTF8.GetPreamble()
            .Concat(new UTF8Encoding(false, true).GetBytes(editedText))
            .ToArray();
        Assert.Equal(expected, await File.ReadAllBytesAsync(filePath));
        Assert.Equal(original, await File.ReadAllBytesAsync(filePath + ".bak"));
        Assert.Empty(Directory.EnumerateFiles(directory.Path, "*.tmp"));
    }

    [Fact]
    public async Task SaveBoundedDocumentAsync_Utf32OutputIncludingBomExceedsBound_LeavesFilesUnchanged()
    {
        using var directory = new TemporaryDirectory();
        var filePath = Path.Combine(directory.Path, "server.properties");
        var original = Encoding.UTF32.GetPreamble()
            .Concat(Encoding.UTF32.GetBytes("x"))
            .ToArray();
        var existingBackupPath = filePath + ".bak";
        var existingBackup = Encoding.UTF8.GetBytes("existing backup");
        await File.WriteAllBytesAsync(filePath, original);
        await File.WriteAllBytesAsync(existingBackupPath, existingBackup);
        var service = new ServerPropertiesPortService();
        var document = Assert.IsType<ServerPropertiesDocument>(
            await service.ReadDocumentAsync(filePath));
        Assert.Equal("utf-32", document.FormatToken.EncodingName);

        // Three UTF-32 characters require 12 body bytes. The retained four-byte BOM makes the
        // exact output 16 bytes, so a 15-byte bound must reject the update.
        await Assert.ThrowsAsync<InvalidDataException>(() =>
            service.SaveBoundedDocumentAsync(
                filePath,
                "abc",
                maximumBytes: 15,
                formatToken: document.FormatToken));

        Assert.Equal(original, await File.ReadAllBytesAsync(filePath));
        Assert.Equal(existingBackup, await File.ReadAllBytesAsync(existingBackupPath));
        Assert.Equal(
            [existingBackupPath],
            Directory.EnumerateFiles(directory.Path, "server.properties.bak*")
                .Order(StringComparer.Ordinal)
                .ToArray());
        Assert.Empty(Directory.EnumerateFiles(directory.Path, "*.tmp"));
    }

    [Fact]
    public async Task SaveBoundedDocumentAsync_EncodingFailure_LeavesFilesUnchanged()
    {
        using var directory = new TemporaryDirectory();
        var filePath = Path.Combine(directory.Path, "server.properties");
        var original = Encoding.Latin1.GetBytes("motd=Caf\u00e9\n");
        var existingBackupPath = filePath + ".bak";
        var existingBackup = Encoding.UTF8.GetBytes("existing backup");
        await File.WriteAllBytesAsync(filePath, original);
        await File.WriteAllBytesAsync(existingBackupPath, existingBackup);
        var service = new ServerPropertiesPortService();
        var document = Assert.IsType<ServerPropertiesDocument>(
            await service.ReadDocumentAsync(filePath));
        Assert.Equal("iso-8859-1", document.FormatToken.EncodingName);

        await Assert.ThrowsAsync<EncoderFallbackException>(() =>
            service.SaveBoundedDocumentAsync(
                filePath,
                "motd=\u6e2c\u8a66\n",
                maximumBytes: 1024,
                formatToken: document.FormatToken));

        Assert.Equal(original, await File.ReadAllBytesAsync(filePath));
        Assert.Equal(existingBackup, await File.ReadAllBytesAsync(existingBackupPath));
        Assert.Equal(
            [existingBackupPath],
            Directory.EnumerateFiles(directory.Path, "server.properties.bak*")
                .Order(StringComparer.Ordinal)
                .ToArray());
        Assert.Empty(Directory.EnumerateFiles(directory.Path, "*.tmp"));
    }

    [Fact]
    public async Task SetServerPortAsync_DoesNotOverwriteExistingBackup()
    {
        using var directory = new TemporaryDirectory();
        var filePath = Path.Combine(directory.Path, "server.properties");
        await File.WriteAllTextAsync(filePath, "server-port=25565\n");
        await File.WriteAllTextAsync(filePath + ".bak", "user-owned-backup");
        var service = new ServerPropertiesPortService();

        var result = await service.SetServerPortAsync(filePath, 25566);

        Assert.Equal("user-owned-backup", await File.ReadAllTextAsync(filePath + ".bak"));
        Assert.Equal("server-port=25565\n", await File.ReadAllTextAsync(filePath + ".bak.2"));
        Assert.Equal(filePath + ".bak.2", result.BackupPath);
    }

    [Fact]
    public async Task SetServerPortAsync_WhenReplacementFails_DeletesTemporaryFile()
    {
        using var directory = new TemporaryDirectory();
        var targetDirectory = Path.Combine(directory.Path, "server.properties");
        Directory.CreateDirectory(targetDirectory);
        var service = new ServerPropertiesPortService();

        await Assert.ThrowsAnyAsync<IOException>(() =>
            service.SetServerPortAsync(targetDirectory, 25565));

        Assert.Empty(Directory.EnumerateFiles(directory.Path, "*.tmp"));
    }

    [Fact]
    public async Task SetServerPortAsync_WhenCancelled_LeavesNoTemporaryFile()
    {
        using var directory = new TemporaryDirectory();
        var filePath = Path.Combine(directory.Path, "server.properties");
        await File.WriteAllTextAsync(filePath, "server-port=25565\n");
        var service = new ServerPropertiesPortService();
        using var cancellation = new CancellationTokenSource();
        cancellation.Cancel();

        await Assert.ThrowsAnyAsync<OperationCanceledException>(() =>
            service.SetServerPortAsync(filePath, 25566, cancellation.Token));

        Assert.Equal("server-port=25565\n", await File.ReadAllTextAsync(filePath));
        Assert.False(File.Exists(filePath + ".bak"));
        Assert.Empty(Directory.EnumerateFiles(directory.Path, "*.tmp"));
    }
}
