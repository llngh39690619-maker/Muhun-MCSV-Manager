using System.Collections.ObjectModel;
using System.Windows.Media.Media3D;

namespace MinecraftServerManager.App.Controls;

internal enum MinecraftSkinPart
{
    Head,
    Body,
    RightArm,
    LeftArm,
    RightLeg,
    LeftLeg
}

internal enum MinecraftSkinLayer
{
    Base,
    Outer
}

internal enum MinecraftSkinFace
{
    Top,
    Bottom,
    Right,
    Front,
    Left,
    Back
}

internal readonly record struct MinecraftSkinUvRect(
    int X,
    int Y,
    int Width,
    int Height);

internal sealed record MinecraftSkinBox(
    string Name,
    MinecraftSkinPart Part,
    MinecraftSkinLayer Layer,
    double Width,
    double Height,
    double Depth,
    Point3D Center,
    double Inflation,
    IReadOnlyDictionary<MinecraftSkinFace, MinecraftSkinUvRect> Faces);

/// <summary>
/// Describes the 64x64 modern Minecraft skin atlas. Coordinates are kept in source pixels so the
/// model remains deterministic and testable independently from WPF's normalized texture space.
/// </summary>
internal static class MinecraftSkinLayout
{
    public const int TextureSize = 64;
    public const double ClothingInflation = 0.25d;
    public const double HeadLayerInflation = 0.5d;

    private static readonly IReadOnlyList<MinecraftSkinBox> Classic = Build(isSlim: false);
    private static readonly IReadOnlyList<MinecraftSkinBox> Slim = Build(isSlim: true);

    public static IReadOnlyList<MinecraftSkinBox> GetBoxes(bool isSlim)
        => isSlim ? Slim : Classic;

    private static IReadOnlyList<MinecraftSkinBox> Build(bool isSlim)
    {
        var armWidth = isSlim ? 3 : 4;
        var armX = 4d + armWidth / 2d;
        var boxes = new List<MinecraftSkinBox>(capacity: 12);

        AddLayers(
            boxes,
            MinecraftSkinPart.Head,
            width: 8d,
            height: 8d,
            depth: 8d,
            center: new Point3D(0d, 12d, 0d),
            baseFaces: Faces(
                top: Rect(8, 0, 8, 8),
                bottom: Rect(16, 0, 8, 8),
                right: Rect(0, 8, 8, 8),
                front: Rect(8, 8, 8, 8),
                left: Rect(16, 8, 8, 8),
                back: Rect(24, 8, 8, 8)),
            outerFaces: Faces(
                top: Rect(40, 0, 8, 8),
                bottom: Rect(48, 0, 8, 8),
                right: Rect(32, 8, 8, 8),
                front: Rect(40, 8, 8, 8),
                left: Rect(48, 8, 8, 8),
                back: Rect(56, 8, 8, 8)),
            outerInflation: HeadLayerInflation);

        AddLayers(
            boxes,
            MinecraftSkinPart.Body,
            width: 8d,
            height: 12d,
            depth: 4d,
            center: new Point3D(0d, 2d, 0d),
            baseFaces: Faces(
                top: Rect(20, 16, 8, 4),
                bottom: Rect(28, 16, 8, 4),
                right: Rect(16, 20, 4, 12),
                front: Rect(20, 20, 8, 12),
                left: Rect(28, 20, 4, 12),
                back: Rect(32, 20, 8, 12)),
            outerFaces: Faces(
                top: Rect(20, 32, 8, 4),
                bottom: Rect(28, 32, 8, 4),
                right: Rect(16, 36, 4, 12),
                front: Rect(20, 36, 8, 12),
                left: Rect(28, 36, 4, 12),
                back: Rect(32, 36, 8, 12)));

        AddLayers(
            boxes,
            MinecraftSkinPart.RightArm,
            width: armWidth,
            height: 12d,
            depth: 4d,
            center: new Point3D(-armX, 2d, 0d),
            baseFaces: ArmFaces(40, 16, armWidth),
            outerFaces: ArmFaces(40, 32, armWidth));

        AddLayers(
            boxes,
            MinecraftSkinPart.LeftArm,
            width: armWidth,
            height: 12d,
            depth: 4d,
            center: new Point3D(armX, 2d, 0d),
            baseFaces: ArmFaces(32, 48, armWidth),
            outerFaces: ArmFaces(48, 48, armWidth));

        AddLayers(
            boxes,
            MinecraftSkinPart.RightLeg,
            width: 4d,
            height: 12d,
            depth: 4d,
            center: new Point3D(-2d, -10d, 0d),
            baseFaces: LimbFaces(0, 16, width: 4),
            outerFaces: LimbFaces(0, 32, width: 4));

        AddLayers(
            boxes,
            MinecraftSkinPart.LeftLeg,
            width: 4d,
            height: 12d,
            depth: 4d,
            center: new Point3D(2d, -10d, 0d),
            baseFaces: LimbFaces(16, 48, width: 4),
            outerFaces: LimbFaces(0, 48, width: 4));

        return new ReadOnlyCollection<MinecraftSkinBox>(boxes);
    }

    private static void AddLayers(
        ICollection<MinecraftSkinBox> boxes,
        MinecraftSkinPart part,
        double width,
        double height,
        double depth,
        Point3D center,
        IReadOnlyDictionary<MinecraftSkinFace, MinecraftSkinUvRect> baseFaces,
        IReadOnlyDictionary<MinecraftSkinFace, MinecraftSkinUvRect> outerFaces,
        double outerInflation = ClothingInflation)
    {
        boxes.Add(new MinecraftSkinBox(
            $"{part}.Base",
            part,
            MinecraftSkinLayer.Base,
            width,
            height,
            depth,
            center,
            Inflation: 0d,
            baseFaces));
        boxes.Add(new MinecraftSkinBox(
            $"{part}.Outer",
            part,
            MinecraftSkinLayer.Outer,
            width,
            height,
            depth,
            center,
            outerInflation,
            outerFaces));
    }

    private static IReadOnlyDictionary<MinecraftSkinFace, MinecraftSkinUvRect> ArmFaces(
        int x,
        int y,
        int width)
        => Faces(
            top: Rect(x + 4, y, width, 4),
            bottom: Rect(x + 4 + width, y, width, 4),
            right: Rect(x, y + 4, 4, 12),
            front: Rect(x + 4, y + 4, width, 12),
            left: Rect(x + 4 + width, y + 4, 4, 12),
            back: Rect(x + 8 + width, y + 4, width, 12));

    private static IReadOnlyDictionary<MinecraftSkinFace, MinecraftSkinUvRect> LimbFaces(
        int x,
        int y,
        int width)
        => Faces(
            top: Rect(x + 4, y, width, 4),
            bottom: Rect(x + 4 + width, y, width, 4),
            right: Rect(x, y + 4, 4, 12),
            front: Rect(x + 4, y + 4, width, 12),
            left: Rect(x + 4 + width, y + 4, 4, 12),
            back: Rect(x + 8 + width, y + 4, width, 12));

    private static IReadOnlyDictionary<MinecraftSkinFace, MinecraftSkinUvRect> Faces(
        MinecraftSkinUvRect top,
        MinecraftSkinUvRect bottom,
        MinecraftSkinUvRect right,
        MinecraftSkinUvRect front,
        MinecraftSkinUvRect left,
        MinecraftSkinUvRect back)
        => new ReadOnlyDictionary<MinecraftSkinFace, MinecraftSkinUvRect>(
            new Dictionary<MinecraftSkinFace, MinecraftSkinUvRect>
            {
                [MinecraftSkinFace.Top] = top,
                [MinecraftSkinFace.Bottom] = bottom,
                [MinecraftSkinFace.Right] = right,
                [MinecraftSkinFace.Front] = front,
                [MinecraftSkinFace.Left] = left,
                [MinecraftSkinFace.Back] = back
            });

    private static MinecraftSkinUvRect Rect(int x, int y, int width, int height)
        => new(x, y, width, height);
}
