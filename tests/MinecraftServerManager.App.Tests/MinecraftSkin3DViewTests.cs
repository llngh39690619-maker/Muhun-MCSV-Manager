using System.IO;
using System.Windows;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using System.Windows.Media.Media3D;
using MinecraftServerManager.App.Controls;

namespace MinecraftServerManager.App.Tests;

public sealed class MinecraftSkin3DViewTests
{
    [Fact]
    public void ModernAtlas_DefinesEveryBodyPartAndBothLayers()
    {
        var boxes = MinecraftSkinLayout.GetBoxes(isSlim: false);

        Assert.Equal(12, boxes.Count);
        foreach (var part in Enum.GetValues<MinecraftSkinPart>())
        {
            var layers = boxes.Where(box => box.Part == part).ToArray();
            Assert.Equal(2, layers.Length);
            Assert.Contains(layers, box => box.Layer == MinecraftSkinLayer.Base);
            Assert.Contains(layers, box => box.Layer == MinecraftSkinLayer.Outer);
            Assert.All(layers, box => Assert.Equal(6, box.Faces.Count));
        }

        Assert.Equal(
            new MinecraftSkinUvRect(8, 8, 8, 8),
            Find(boxes, MinecraftSkinPart.Head, MinecraftSkinLayer.Base)
                .Faces[MinecraftSkinFace.Front]);
        Assert.Equal(
            new MinecraftSkinUvRect(40, 8, 8, 8),
            Find(boxes, MinecraftSkinPart.Head, MinecraftSkinLayer.Outer)
                .Faces[MinecraftSkinFace.Front]);
        Assert.Equal(
            new MinecraftSkinUvRect(20, 20, 8, 12),
            Find(boxes, MinecraftSkinPart.Body, MinecraftSkinLayer.Base)
                .Faces[MinecraftSkinFace.Front]);
        Assert.Equal(
            new MinecraftSkinUvRect(20, 36, 8, 12),
            Find(boxes, MinecraftSkinPart.Body, MinecraftSkinLayer.Outer)
                .Faces[MinecraftSkinFace.Front]);
        Assert.Equal(
            new MinecraftSkinUvRect(44, 20, 4, 12),
            Find(boxes, MinecraftSkinPart.RightArm, MinecraftSkinLayer.Base)
                .Faces[MinecraftSkinFace.Front]);
        Assert.Equal(
            new MinecraftSkinUvRect(44, 36, 4, 12),
            Find(boxes, MinecraftSkinPart.RightArm, MinecraftSkinLayer.Outer)
                .Faces[MinecraftSkinFace.Front]);
        Assert.Equal(
            new MinecraftSkinUvRect(36, 52, 4, 12),
            Find(boxes, MinecraftSkinPart.LeftArm, MinecraftSkinLayer.Base)
                .Faces[MinecraftSkinFace.Front]);
        Assert.Equal(
            new MinecraftSkinUvRect(52, 52, 4, 12),
            Find(boxes, MinecraftSkinPart.LeftArm, MinecraftSkinLayer.Outer)
                .Faces[MinecraftSkinFace.Front]);
        Assert.Equal(
            new MinecraftSkinUvRect(4, 20, 4, 12),
            Find(boxes, MinecraftSkinPart.RightLeg, MinecraftSkinLayer.Base)
                .Faces[MinecraftSkinFace.Front]);
        Assert.Equal(
            new MinecraftSkinUvRect(4, 36, 4, 12),
            Find(boxes, MinecraftSkinPart.RightLeg, MinecraftSkinLayer.Outer)
                .Faces[MinecraftSkinFace.Front]);
        Assert.Equal(
            new MinecraftSkinUvRect(20, 52, 4, 12),
            Find(boxes, MinecraftSkinPart.LeftLeg, MinecraftSkinLayer.Base)
                .Faces[MinecraftSkinFace.Front]);
        Assert.Equal(
            new MinecraftSkinUvRect(4, 52, 4, 12),
            Find(boxes, MinecraftSkinPart.LeftLeg, MinecraftSkinLayer.Outer)
                .Faces[MinecraftSkinFace.Front]);

        Assert.All(boxes.SelectMany(box => box.Faces.Values), rectangle =>
        {
            Assert.True(rectangle.X >= 0);
            Assert.True(rectangle.Y >= 0);
            Assert.True(rectangle.Width > 0);
            Assert.True(rectangle.Height > 0);
            Assert.True(rectangle.X + rectangle.Width <= MinecraftSkinLayout.TextureSize);
            Assert.True(rectangle.Y + rectangle.Height <= MinecraftSkinLayout.TextureSize);
        });
    }

    [Fact]
    public void SlimAtlas_ImmediatelyChangesArmGeometryAndUvWidth()
    {
        var classic = MinecraftSkinLayout.GetBoxes(isSlim: false);
        var slim = MinecraftSkinLayout.GetBoxes(isSlim: true);
        var classicArm = Find(
            classic,
            MinecraftSkinPart.RightArm,
            MinecraftSkinLayer.Base);
        var slimArm = Find(
            slim,
            MinecraftSkinPart.RightArm,
            MinecraftSkinLayer.Base);

        Assert.Equal(4d, classicArm.Width);
        Assert.Equal(3d, slimArm.Width);
        Assert.Equal(
            new MinecraftSkinUvRect(44, 20, 4, 12),
            classicArm.Faces[MinecraftSkinFace.Front]);
        Assert.Equal(
            new MinecraftSkinUvRect(44, 20, 3, 12),
            slimArm.Faces[MinecraftSkinFace.Front]);
        Assert.Equal(
            new MinecraftSkinUvRect(52, 52, 3, 12),
            Find(slim, MinecraftSkinPart.LeftArm, MinecraftSkinLayer.Outer)
                .Faces[MinecraftSkinFace.Front]);

        WpfStaTestHost.Run(() =>
        {
            var view = new MinecraftSkin3DView();
            Assert.Equal(4d, view.CurrentArmWidth);
            Assert.Equal(
                4.5d,
                view.GetPartBounds(MinecraftSkinPart.RightArm).SizeX,
                precision: 6);
            Assert.Equal(6, view.ModelPartCount);
            Assert.Equal(12, view.ModelBoxCount);
            Assert.Equal(9d, view.GetPartBounds(MinecraftSkinPart.Head).SizeX, precision: 6);
            Assert.Equal(8.5d, view.GetPartBounds(MinecraftSkinPart.Body).SizeX, precision: 6);

            view.IsSlim = true;

            Assert.Equal(3d, view.CurrentArmWidth);
            Assert.Equal(
                3.5d,
                view.GetPartBounds(MinecraftSkinPart.RightArm).SizeX,
                precision: 6);
            Assert.Equal(6, view.ModelPartCount);
            Assert.Equal(12, view.ModelBoxCount);
        });
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public void BodyLayout_JoinsEveryPartAndUsesShoulderAndHipPivots(bool isSlim)
    {
        var boxes = MinecraftSkinLayout.GetBoxes(isSlim);
        var head = Find(boxes, MinecraftSkinPart.Head, MinecraftSkinLayer.Base);
        var body = Find(boxes, MinecraftSkinPart.Body, MinecraftSkinLayer.Base);
        var rightArm = Find(boxes, MinecraftSkinPart.RightArm, MinecraftSkinLayer.Base);
        var leftArm = Find(boxes, MinecraftSkinPart.LeftArm, MinecraftSkinLayer.Base);
        var rightLeg = Find(boxes, MinecraftSkinPart.RightLeg, MinecraftSkinLayer.Base);
        var leftLeg = Find(boxes, MinecraftSkinPart.LeftLeg, MinecraftSkinLayer.Base);

        Assert.Equal(Bottom(head), Top(body));
        Assert.Equal(Bottom(body), Top(rightLeg));
        Assert.Equal(Bottom(body), Top(leftLeg));
        Assert.Equal(Left(body), Right(rightArm));
        Assert.Equal(Right(body), Left(leftArm));
        Assert.Equal(Right(rightLeg), Left(leftLeg));

        Assert.True(MinecraftSkin3DView.TryGetLimbPivot(
            MinecraftSkinPart.RightArm,
            out var rightShoulder));
        Assert.True(MinecraftSkin3DView.TryGetLimbPivot(
            MinecraftSkinPart.LeftArm,
            out var leftShoulder));
        Assert.True(MinecraftSkin3DView.TryGetLimbPivot(
            MinecraftSkinPart.RightLeg,
            out var rightHip));
        Assert.True(MinecraftSkin3DView.TryGetLimbPivot(
            MinecraftSkinPart.LeftLeg,
            out var leftHip));

        Assert.Equal(new Point3D(-5d, 6d, 0d), rightShoulder);
        Assert.Equal(new Point3D(5d, 6d, 0d), leftShoulder);
        Assert.Equal(new Point3D(rightLeg.Center.X, Top(rightLeg), 0d), rightHip);
        Assert.Equal(new Point3D(leftLeg.Center.X, Top(leftLeg), 0d), leftHip);

        foreach (var baseBox in boxes.Where(box => box.Layer == MinecraftSkinLayer.Base))
        {
            var outer = Find(boxes, baseBox.Part, MinecraftSkinLayer.Outer);
            Assert.Equal(baseBox.Center, outer.Center);
            Assert.True(outer.Inflation > 0d);
        }
    }

    [Fact]
    public void MeshFactory_EmitsSixUvMappedFacesWithinNormalizedAtlasSpace()
    {
        var head = Find(
            MinecraftSkinLayout.GetBoxes(isSlim: false),
            MinecraftSkinPart.Head,
            MinecraftSkinLayer.Base);
        var mesh = MinecraftSkinMeshFactory.Create(head);

        Assert.Equal(24, mesh.Positions.Count);
        Assert.Equal(24, mesh.Normals.Count);
        Assert.Equal(24, mesh.TextureCoordinates.Count);
        Assert.Equal(36, mesh.TriangleIndices.Count);
        Assert.Equal(new Point(8.5d / 64d, 8.5d / 64d), mesh.TextureCoordinates[0]);
        Assert.Equal(new Point(8.5d / 64d, 15.5d / 64d), mesh.TextureCoordinates[1]);
        Assert.Equal(new Point(15.5d / 64d, 15.5d / 64d), mesh.TextureCoordinates[2]);
        Assert.Equal(new Point(15.5d / 64d, 8.5d / 64d), mesh.TextureCoordinates[3]);
        Assert.All(mesh.TextureCoordinates, coordinate =>
        {
            Assert.InRange(coordinate.X, 0d, 1d);
            Assert.InRange(coordinate.Y, 0d, 1d);
        });
    }

    [Fact]
    public void SkinMaterial_UsesOneAbsoluteAtlasViewportForEveryBodyBox()
    {
        WpfStaTestHost.Run(() =>
        {
            var material = Assert.IsType<DiffuseMaterial>(
                MinecraftSkin3DView.CreateSkinMaterial(CreateBitmap(width: 64, height: 64)));
            var brush = Assert.IsType<ImageBrush>(material.Brush);

            Assert.Equal(BrushMappingMode.Absolute, brush.ViewportUnits);
            Assert.Equal(new Rect(0d, 0d, 1d, 1d), brush.Viewport);
            Assert.Equal(BrushMappingMode.RelativeToBoundingBox, brush.ViewboxUnits);
            Assert.Equal(BitmapScalingMode.NearestNeighbor, RenderOptions.GetBitmapScalingMode(brush));
            var previewTexture = Assert.IsAssignableFrom<BitmapSource>(brush.ImageSource);
            Assert.Equal(1024, previewTexture.PixelWidth);
            Assert.Equal(1024, previewTexture.PixelHeight);
        });
    }

    [Fact]
    public void HighDpiRender_PreservesCrispMinecraftPixelEdges()
    {
        WpfStaTestHost.Run(() =>
        {
            var view = new MinecraftSkin3DView
            {
                Width = 400d,
                Height = 500d,
                TextureSource = CreateCheckerBitmap()
            };
            var window = new Window
            {
                Width = 400d,
                Height = 500d,
                Left = -10000d,
                Top = -10000d,
                ShowInTaskbar = false,
                WindowStyle = WindowStyle.None,
                Content = view
            };
            try
            {
                window.Show();
                view.UpdateLayout();

                const int width = 800;
                const int height = 1000;
                var render = new RenderTargetBitmap(width, height, 192d, 192d, PixelFormats.Pbgra32);
                render.Render(view);
                var pixels = new byte[width * height * 4];
                render.CopyPixels(pixels, width * 4, 0);

                var skinPixels = 0;
                var blendedPixels = 0;
                for (var offset = 0; offset < pixels.Length; offset += 4)
                {
                    var blue = pixels[offset];
                    var green = pixels[offset + 1];
                    var red = pixels[offset + 2];
                    var alpha = pixels[offset + 3];
                    if (alpha < 250 || green > 8 || (red < 18 && blue < 18))
                    {
                        continue;
                    }

                    skinPixels++;
                    if (red > 12 && blue > 12)
                    {
                        blendedPixels++;
                    }
                }

                Assert.True(skinPixels > 10000, $"Expected a visible high-DPI avatar, found {skinPixels} skin pixels.");
                Assert.True(
                    blendedPixels < skinPixels / 8,
                    $"The rendered skin contained {blendedPixels} blended pixels out of {skinPixels} coloured pixels.");
            }
            finally
            {
                window.Close();
            }
        });
    }

    [Fact]
    public void SkinInputs_AcceptLocal64PngAndImageSourceThenFailClosedToFallback()
    {
        WpfStaTestHost.Run(() =>
        {
            var directory = Directory.CreateTempSubdirectory("mcsv-skin-view-");
            try
            {
                var validPath = Path.Combine(directory.FullName, "valid.png");
                var legacyPath = Path.Combine(directory.FullName, "legacy.png");
                var wrongSizePath = Path.Combine(directory.FullName, "wrong-size.png");
                var malformedPath = Path.Combine(directory.FullName, "malformed.png");
                WritePng(validPath, width: 64, height: 64);
                WritePng(legacyPath, width: 64, height: 32);
                WritePng(wrongSizePath, width: 32, height: 32);
                File.WriteAllBytes(
                    malformedPath,
                    [0x89, 0x50, 0x4E, 0x47, 0x0D, 0x0A, 0x1A, 0x0A, 0x00]);

                var view = new MinecraftSkin3DView();
                Assert.True(view.IsUsingFallback);

                view.SkinPath = validPath;
                Assert.False(view.IsUsingFallback);
                Assert.Equal(64, view.ActiveSkin.PixelWidth);
                Assert.Equal(64, view.ActiveSkin.PixelHeight);
                File.Delete(validPath);
                view.IsSlim = true;
                Assert.False(view.IsUsingFallback);
                Assert.Equal(3d, view.CurrentArmWidth);

                view.TextureSource = null;
                view.SkinPath = legacyPath;
                Assert.False(view.IsUsingFallback);
                Assert.Equal(64, view.ActiveSkin.PixelWidth);
                Assert.Equal(64, view.ActiveSkin.PixelHeight);

                view.SkinPath = wrongSizePath;
                Assert.True(view.IsUsingFallback);

                view.SkinPath = malformedPath;
                Assert.True(view.IsUsingFallback);

                view.SkinPath = Path.Combine(directory.FullName, "missing.png");
                Assert.True(view.IsUsingFallback);

                view.TextureSource = CreateBitmap(width: 64, height: 64);
                Assert.False(view.IsUsingFallback);

                view.TextureSource = CreateBitmap(width: 32, height: 32);
                Assert.True(view.IsUsingFallback);
            }
            finally
            {
                directory.Delete(recursive: true);
            }
        });
    }

    [Fact]
    public void LegacyAtlas_MirrorsRightLimbsIntoModernLeftLimbSlots()
    {
        WpfStaTestHost.Run(() =>
        {
            var legacy = CreateCoordinateBitmap(width: 64, height: 32);

            Assert.True(MinecraftSkin3DView.TryNormalizeSkinForPreview(legacy, out var normalized));

            var pixels = CopyPixels(normalized);
            var stride = normalized.PixelWidth * 4;
            var boxes = MinecraftSkinLayout.GetBoxes(isSlim: false);
            AssertMirroredPart(
                pixels,
                stride,
                Find(boxes, MinecraftSkinPart.RightArm, MinecraftSkinLayer.Base),
                Find(boxes, MinecraftSkinPart.LeftArm, MinecraftSkinLayer.Base));
            AssertMirroredPart(
                pixels,
                stride,
                Find(boxes, MinecraftSkinPart.RightLeg, MinecraftSkinLayer.Base),
                Find(boxes, MinecraftSkinPart.LeftLeg, MinecraftSkinLayer.Base));
        });
    }

    [Fact]
    public void FullyOpaqueAtlas_ClearsAccidentalOuterLayersButKeepsBaseSkin()
    {
        WpfStaTestHost.Run(() =>
        {
            Assert.True(MinecraftSkin3DView.TryNormalizeSkinForPreview(
                CreateCoordinateBitmap(width: 64, height: 64),
                out var normalized));

            var pixels = CopyPixels(normalized);
            var stride = normalized.PixelWidth * 4;
            foreach (var box in MinecraftSkinLayout.GetBoxes(isSlim: false))
            {
                foreach (var rectangle in box.Faces.Values)
                {
                    var expectedAlpha = box.Layer == MinecraftSkinLayer.Outer
                        ? (byte)0
                        : byte.MaxValue;
                    Assert.Equal(
                        expectedAlpha,
                        pixels[rectangle.Y * stride + rectangle.X * 4 + 3]);
                }
            }
        });
    }

    [Fact]
    public void Lifecycle_UsesSmoothPropertyAnimationsAndStopsAtUnloaded()
    {
        WpfStaTestHost.Run(() =>
        {
            var view = new MinecraftSkin3DView();
            Assert.False(view.IsAnimating);
            Assert.InRange(
                view.AnimationCycleDuration,
                TimeSpan.FromMilliseconds(600d),
                TimeSpan.FromMilliseconds(1200d));
            Assert.True(view.ViewUpdateInterval <= TimeSpan.FromMilliseconds(20d));
            Assert.False(view.IsViewInterpolating);

            var window = new Window
            {
                Width = 320d,
                Height = 420d,
                Opacity = 0d,
                ShowInTaskbar = false,
                WindowStyle = WindowStyle.None,
                Content = view
            };
            try
            {
                window.Show();
                Assert.True(view.IsLoaded);
                Assert.True(view.IsVisible);
                Assert.True(view.IsAnimating);
                Assert.All(view.LimbRotations.Values, rotation => Assert.True(rotation.HasAnimatedProperties));

                view.IsSlim = true;
                Assert.True(view.IsAnimating);
                Assert.All(view.LimbRotations.Values, rotation => Assert.True(rotation.HasAnimatedProperties));

                view.BeginDrag(new Point(100d, 100d));
                Assert.True(view.ContinueDrag(new Point(140d, 130d), isLeftButtonPressed: true));
                Assert.True(view.IsViewInterpolating);
                view.EndDrag(releaseCapture: false);
                Assert.False(view.IsDraggingView);
                Assert.False(view.IsViewInterpolating);

                window.Content = null;
                Assert.False(view.IsAnimating);
                Assert.False(view.IsViewInterpolating);
                Assert.All(view.LimbRotations.Values, rotation =>
                {
                    Assert.False(rotation.HasAnimatedProperties);
                    Assert.Equal(0d, rotation.Angle);
                });
            }
            finally
            {
                window.Close();
            }
        });
    }

    [Fact]
    public void Rotation_ChangesOnlyDuringLeftButtonDragAndStopsImmediately()
    {
        WpfStaTestHost.Run(() =>
        {
            var view = new MinecraftSkin3DView
            {
                Width = 400d,
                Height = 240d
            };
            view.Measure(new Size(400d, 240d));
            view.Arrange(new Rect(0d, 0d, 400d, 240d));
            var initialYaw = view.YawAngle;
            var initialPitch = view.PitchAngle;
            var initialModelBuildCount = view.ModelBuildCount;

            Assert.False(view.ContinueDrag(new Point(400d, 0d), isLeftButtonPressed: true));
            Assert.Equal(initialYaw, view.TargetYawAngle);
            Assert.Equal(initialPitch, view.TargetPitchAngle);
            Assert.Equal(initialYaw, view.YawAngle);
            Assert.Equal(initialPitch, view.PitchAngle);

            view.BeginDrag(new Point(200d, 120d));
            Assert.True(view.IsDraggingView);
            Assert.True(view.ContinueDrag(new Point(800d, 720d), isLeftButtonPressed: true));
            Assert.True(view.TargetYawAngle - view.YawAngle > 360d);
            Assert.True(view.PitchAngle - view.TargetPitchAngle > 360d);
            AdvanceViewToTarget(view);
            Assert.Equal(view.TargetYawAngle, view.YawAngle, precision: 2);
            Assert.Equal(view.TargetPitchAngle, view.PitchAngle, precision: 2);
            Assert.Equal(initialModelBuildCount, view.ModelBuildCount);

            Assert.False(view.ContinueDrag(new Point(900d, 820d), isLeftButtonPressed: false));
            Assert.False(view.IsDraggingView);
            Assert.False(view.IsViewInterpolating);
            var stoppedYaw = view.YawAngle;
            var stoppedPitch = view.PitchAngle;
            Assert.False(view.ContinueDrag(new Point(0d, 0d), isLeftButtonPressed: true));
            Assert.Equal(stoppedYaw, view.YawAngle);
            Assert.Equal(stoppedPitch, view.PitchAngle);

            view.ResetView();
            AdvanceViewToTarget(view);
            Assert.Equal(initialYaw, view.YawAngle, precision: 2);
            Assert.Equal(initialPitch, view.PitchAngle, precision: 2);
        });
    }

    [Fact]
    public void Implementation_RemainsOfflineAndAvoidsCompositionCallbacks()
    {
        var source = File.ReadAllText(TestRepositoryPaths.AppSource(
            "Controls",
            "MinecraftSkin3DView.cs"));

        Assert.DoesNotContain("HttpClient", source, StringComparison.Ordinal);
        Assert.DoesNotContain("WebBrowser", source, StringComparison.Ordinal);
        Assert.DoesNotContain("WebView", source, StringComparison.Ordinal);
        Assert.DoesNotContain("CompositionTarget.Rendering", source, StringComparison.Ordinal);
        Assert.DoesNotContain("http://", source, StringComparison.OrdinalIgnoreCase);
        Assert.DoesNotContain("https://", source, StringComparison.OrdinalIgnoreCase);
        Assert.DoesNotContain("SetViewTargetFromPointer", source, StringComparison.Ordinal);
        Assert.Contains("DoubleAnimation", source, StringComparison.Ordinal);
        Assert.Contains("DispatcherPriority.Render", source, StringComparison.Ordinal);
        Assert.Contains("IsVisibleChanged += OnIsVisibleChanged", source, StringComparison.Ordinal);
        Assert.Contains("Loaded += OnLoaded", source, StringComparison.Ordinal);
        Assert.Contains("Unloaded += OnUnloaded", source, StringComparison.Ordinal);
    }

    private static MinecraftSkinBox Find(
        IEnumerable<MinecraftSkinBox> boxes,
        MinecraftSkinPart part,
        MinecraftSkinLayer layer)
        => boxes.Single(box => box.Part == part && box.Layer == layer);

    private static double Left(MinecraftSkinBox box)
        => box.Center.X - box.Width / 2d;

    private static double Right(MinecraftSkinBox box)
        => box.Center.X + box.Width / 2d;

    private static double Top(MinecraftSkinBox box)
        => box.Center.Y + box.Height / 2d;

    private static double Bottom(MinecraftSkinBox box)
        => box.Center.Y - box.Height / 2d;

    private static void AdvanceViewToTarget(MinecraftSkin3DView view)
    {
        for (var index = 0; index < 80; index++)
        {
            view.AdvanceViewInterpolation();
        }
    }

    private static void WritePng(string path, int width, int height)
    {
        var encoder = new PngBitmapEncoder();
        encoder.Frames.Add(BitmapFrame.Create(CreateBitmap(width, height)));
        using var stream = new FileStream(path, FileMode.CreateNew, FileAccess.Write, FileShare.None);
        encoder.Save(stream);
    }

    private static BitmapSource CreateBitmap(int width, int height)
    {
        var stride = width * 4;
        var pixels = Enumerable.Repeat((byte)0xFF, stride * height).ToArray();
        var bitmap = BitmapSource.Create(
            width,
            height,
            96d,
            96d,
            PixelFormats.Bgra32,
            palette: null,
            pixels,
            stride);
        bitmap.Freeze();
        return bitmap;
    }

    private static BitmapSource CreateCheckerBitmap()
    {
        const int size = MinecraftSkinLayout.TextureSize;
        const int bytesPerPixel = 4;
        var stride = size * bytesPerPixel;
        var pixels = new byte[stride * size];
        for (var y = 0; y < size; y++)
        {
            for (var x = 0; x < size; x++)
            {
                var offset = y * stride + x * bytesPerPixel;
                if ((x + y) % 2 == 0)
                {
                    pixels[offset + 2] = byte.MaxValue;
                }
                else
                {
                    pixels[offset] = byte.MaxValue;
                }

                pixels[offset + 3] = byte.MaxValue;
            }
        }

        var bitmap = BitmapSource.Create(
            size,
            size,
            96d,
            96d,
            PixelFormats.Pbgra32,
            palette: null,
            pixels,
            stride);
        bitmap.Freeze();
        return bitmap;
    }

    private static BitmapSource CreateCoordinateBitmap(int width, int height)
    {
        var stride = width * 4;
        var pixels = new byte[stride * height];
        for (var y = 0; y < height; y++)
        {
            for (var x = 0; x < width; x++)
            {
                var offset = y * stride + x * 4;
                pixels[offset] = (byte)x;
                pixels[offset + 1] = (byte)y;
                pixels[offset + 2] = (byte)(x ^ y);
                pixels[offset + 3] = 0xFF;
            }
        }

        var bitmap = BitmapSource.Create(
            width,
            height,
            96d,
            96d,
            PixelFormats.Bgra32,
            palette: null,
            pixels,
            stride);
        bitmap.Freeze();
        return bitmap;
    }

    private static byte[] CopyPixels(BitmapSource bitmap)
    {
        var stride = bitmap.PixelWidth * 4;
        var pixels = new byte[stride * bitmap.PixelHeight];
        bitmap.CopyPixels(pixels, stride, 0);
        return pixels;
    }

    private static void AssertMirroredPart(
        byte[] pixels,
        int stride,
        MinecraftSkinBox source,
        MinecraftSkinBox destination)
    {
        foreach (var face in Enum.GetValues<MinecraftSkinFace>())
        {
            var sourceFace = face switch
            {
                MinecraftSkinFace.Right => MinecraftSkinFace.Left,
                MinecraftSkinFace.Left => MinecraftSkinFace.Right,
                _ => face
            };
            var sourceRectangle = source.Faces[sourceFace];
            var destinationRectangle = destination.Faces[face];
            for (var y = 0; y < destinationRectangle.Height; y++)
            {
                for (var x = 0; x < destinationRectangle.Width; x++)
                {
                    var sourceOffset = (sourceRectangle.Y + y) * stride
                        + (sourceRectangle.X + sourceRectangle.Width - x - 1) * 4;
                    var destinationOffset = (destinationRectangle.Y + y) * stride
                        + (destinationRectangle.X + x) * 4;
                    Assert.Equal(
                        pixels.AsSpan(sourceOffset, 4).ToArray(),
                        pixels.AsSpan(destinationOffset, 4).ToArray());
                }
            }
        }
    }
}
