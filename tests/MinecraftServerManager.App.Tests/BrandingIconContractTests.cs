using System.IO;
using System.Runtime.CompilerServices;
using System.Xml.Linq;

namespace MinecraftServerManager.App.Tests;

public sealed class BrandingIconContractTests
{
    [Fact]
    public void ApplicationProject_EmbedsValidMultiSizeWindowsIcon()
    {
        var projectPath = GetProjectPath();
        var document = XDocument.Load(projectPath);
        var iconRelativePath = document
            .Descendants("ApplicationIcon")
            .Select(element => element.Value.Trim())
            .Single();

        Assert.Equal("Assets\\MuhunMcsvManager.ico", iconRelativePath);

        var iconPath = Path.GetFullPath(Path.Combine(
            Path.GetDirectoryName(projectPath)!,
            iconRelativePath));
        Assert.True(File.Exists(iconPath), $"找不到 ApplicationIcon：{iconPath}");

        using var stream = File.OpenRead(iconPath);
        using var reader = new BinaryReader(stream);
        Assert.Equal((ushort)0, reader.ReadUInt16());
        Assert.Equal((ushort)1, reader.ReadUInt16());
        var count = reader.ReadUInt16();
        Assert.Equal(10, count);

        var sizes = new HashSet<int>();
        for (var index = 0; index < count; index++)
        {
            var widthByte = reader.ReadByte();
            var heightByte = reader.ReadByte();
            var width = widthByte == 0 ? 256 : widthByte;
            var height = heightByte == 0 ? 256 : heightByte;
            reader.ReadByte(); // Color count.
            Assert.Equal(0, reader.ReadByte());
            Assert.Equal((ushort)1, reader.ReadUInt16());
            Assert.Equal((ushort)32, reader.ReadUInt16());
            var dataLength = reader.ReadUInt32();
            var dataOffset = reader.ReadUInt32();

            Assert.Equal(width, height);
            Assert.True(dataLength > 8);
            Assert.InRange((long)dataOffset + dataLength, 1, stream.Length);
            sizes.Add(width);

            var returnPosition = stream.Position;
            stream.Position = dataOffset;
            Assert.Equal(0x89, reader.ReadByte());
            Assert.Equal((byte)'P', reader.ReadByte());
            Assert.Equal((byte)'N', reader.ReadByte());
            Assert.Equal((byte)'G', reader.ReadByte());
            stream.Position = returnPosition;
        }

        Assert.Equal(
            new[] { 16, 20, 24, 32, 40, 48, 64, 96, 128, 256 },
            sizes.OrderBy(size => size));
    }

    private static string GetProjectPath()
        => TestRepositoryPaths.AppSource("MinecraftServerManager.App.csproj");
}
