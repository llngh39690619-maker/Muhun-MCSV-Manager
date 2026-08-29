using System.Windows;
using System.Windows.Media;
using System.Windows.Media.Media3D;

namespace MinecraftServerManager.App.Controls;

internal static class MinecraftSkinMeshFactory
{
    private const double AtlasSize = MinecraftSkinLayout.TextureSize;

    public static MeshGeometry3D Create(MinecraftSkinBox box)
    {
        ArgumentNullException.ThrowIfNull(box);

        var halfWidth = box.Width / 2d + box.Inflation;
        var halfHeight = box.Height / 2d + box.Inflation;
        var halfDepth = box.Depth / 2d + box.Inflation;
        var center = box.Center;
        var mesh = new MeshGeometry3D();

        AddFace(
            mesh,
            box.Faces[MinecraftSkinFace.Front],
            normal: new Vector3D(0d, 0d, 1d),
            new Point3D(center.X - halfWidth, center.Y + halfHeight, center.Z + halfDepth),
            new Point3D(center.X - halfWidth, center.Y - halfHeight, center.Z + halfDepth),
            new Point3D(center.X + halfWidth, center.Y - halfHeight, center.Z + halfDepth),
            new Point3D(center.X + halfWidth, center.Y + halfHeight, center.Z + halfDepth));

        AddFace(
            mesh,
            box.Faces[MinecraftSkinFace.Back],
            normal: new Vector3D(0d, 0d, -1d),
            new Point3D(center.X + halfWidth, center.Y + halfHeight, center.Z - halfDepth),
            new Point3D(center.X + halfWidth, center.Y - halfHeight, center.Z - halfDepth),
            new Point3D(center.X - halfWidth, center.Y - halfHeight, center.Z - halfDepth),
            new Point3D(center.X - halfWidth, center.Y + halfHeight, center.Z - halfDepth));

        // The avatar faces +Z. Its anatomical right is therefore -X when viewed from the front.
        AddFace(
            mesh,
            box.Faces[MinecraftSkinFace.Right],
            normal: new Vector3D(-1d, 0d, 0d),
            new Point3D(center.X - halfWidth, center.Y + halfHeight, center.Z - halfDepth),
            new Point3D(center.X - halfWidth, center.Y - halfHeight, center.Z - halfDepth),
            new Point3D(center.X - halfWidth, center.Y - halfHeight, center.Z + halfDepth),
            new Point3D(center.X - halfWidth, center.Y + halfHeight, center.Z + halfDepth));

        AddFace(
            mesh,
            box.Faces[MinecraftSkinFace.Left],
            normal: new Vector3D(1d, 0d, 0d),
            new Point3D(center.X + halfWidth, center.Y + halfHeight, center.Z + halfDepth),
            new Point3D(center.X + halfWidth, center.Y - halfHeight, center.Z + halfDepth),
            new Point3D(center.X + halfWidth, center.Y - halfHeight, center.Z - halfDepth),
            new Point3D(center.X + halfWidth, center.Y + halfHeight, center.Z - halfDepth));

        AddFace(
            mesh,
            box.Faces[MinecraftSkinFace.Top],
            normal: new Vector3D(0d, 1d, 0d),
            new Point3D(center.X - halfWidth, center.Y + halfHeight, center.Z - halfDepth),
            new Point3D(center.X - halfWidth, center.Y + halfHeight, center.Z + halfDepth),
            new Point3D(center.X + halfWidth, center.Y + halfHeight, center.Z + halfDepth),
            new Point3D(center.X + halfWidth, center.Y + halfHeight, center.Z - halfDepth));

        AddFace(
            mesh,
            box.Faces[MinecraftSkinFace.Bottom],
            normal: new Vector3D(0d, -1d, 0d),
            new Point3D(center.X - halfWidth, center.Y - halfHeight, center.Z + halfDepth),
            new Point3D(center.X - halfWidth, center.Y - halfHeight, center.Z - halfDepth),
            new Point3D(center.X + halfWidth, center.Y - halfHeight, center.Z - halfDepth),
            new Point3D(center.X + halfWidth, center.Y - halfHeight, center.Z + halfDepth));

        mesh.Freeze();
        return mesh;
    }

    private static void AddFace(
        MeshGeometry3D mesh,
        MinecraftSkinUvRect uv,
        Vector3D normal,
        Point3D topLeft,
        Point3D bottomLeft,
        Point3D bottomRight,
        Point3D topRight)
    {
        var start = mesh.Positions.Count;
        mesh.Positions.Add(topLeft);
        mesh.Positions.Add(bottomLeft);
        mesh.Positions.Add(bottomRight);
        mesh.Positions.Add(topRight);

        for (var index = 0; index < 4; index++)
        {
            mesh.Normals.Add(normal);
        }

        // Sample inside the edge texels instead of exactly on atlas cell boundaries. This keeps
        // WPF's texture sampler from pulling colours from an adjacent face while the model moves.
        var left = (uv.X + 0.5d) / AtlasSize;
        var top = (uv.Y + 0.5d) / AtlasSize;
        var right = (uv.X + uv.Width - 0.5d) / AtlasSize;
        var bottom = (uv.Y + uv.Height - 0.5d) / AtlasSize;
        mesh.TextureCoordinates.Add(new Point(left, top));
        mesh.TextureCoordinates.Add(new Point(left, bottom));
        mesh.TextureCoordinates.Add(new Point(right, bottom));
        mesh.TextureCoordinates.Add(new Point(right, top));

        mesh.TriangleIndices.Add(start);
        mesh.TriangleIndices.Add(start + 1);
        mesh.TriangleIndices.Add(start + 2);
        mesh.TriangleIndices.Add(start);
        mesh.TriangleIndices.Add(start + 2);
        mesh.TriangleIndices.Add(start + 3);
    }
}
