using System.IO;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Media;
using System.Windows.Media.Animation;
using System.Windows.Media.Imaging;
using System.Windows.Media.Media3D;
using System.Windows.Threading;

namespace MinecraftServerManager.App.Controls;

/// <summary>
/// Renders a local Minecraft skin without a browser, network request, or per-frame geometry and
/// material rebuild. Modern 64x64 skins are used directly; legacy 64x32 skins are expanded to the
/// modern atlas for a faithful preview before the classic/slim model is rebuilt.
/// </summary>
public sealed class MinecraftSkin3DView : UserControl
{
    private const long MaximumSkinFileBytes = 1024L * 1024L;
    private const double DragYawDegreesPerPixel = 0.7d;
    private const double DragPitchDegreesPerPixel = 0.7d;
    private const double ViewInterpolationFactor = 0.24d;
    private const double ViewInterpolationEpsilon = 0.05d;
    private const double WalkSwingDegrees = 28.5d;
    private const double CameraFieldOfView = 38d;
    private const double CameraVerticalHalfExtent = 18.5d;
    private const int PreviewTextureScale = 16;
    private static readonly TimeSpan WalkHalfCycleDuration = TimeSpan.FromMilliseconds(450d);
    private static readonly TimeSpan ViewInterpolationInterval = TimeSpan.FromMilliseconds(16d);
    private static readonly DoubleAnimation ForwardWalkAnimation = CreateWalkAnimation(startsForward: true);
    private static readonly DoubleAnimation BackwardWalkAnimation = CreateWalkAnimation(startsForward: false);
    private static readonly byte[] PngSignature = [
        0x89,
        0x50,
        0x4E,
        0x47,
        0x0D,
        0x0A,
        0x1A,
        0x0A
    ];

    private static readonly DependencyPropertyKey IsUsingFallbackPropertyKey =
        DependencyProperty.RegisterReadOnly(
            nameof(IsUsingFallback),
            typeof(bool),
            typeof(MinecraftSkin3DView),
            new PropertyMetadata(true));

    private static readonly DependencyPropertyKey IsAnimatingPropertyKey =
        DependencyProperty.RegisterReadOnly(
            nameof(IsAnimating),
            typeof(bool),
            typeof(MinecraftSkin3DView),
            new PropertyMetadata(false));

    private readonly Model3DGroup _avatar = new();
    private readonly AxisAngleRotation3D _yawRotation = new(new Vector3D(0d, 1d, 0d), -18d);
    private readonly AxisAngleRotation3D _pitchRotation = new(new Vector3D(1d, 0d, 0d), -4d);
    private readonly Dictionary<MinecraftSkinPart, AxisAngleRotation3D> _limbRotations = [];
    private readonly PerspectiveCamera _camera;
    private readonly DispatcherTimer _viewInterpolationTimer;
    private readonly BitmapSource _fallbackSkin;

    private BitmapSource _activeSkin;
    private BitmapSource _previewTexture = null!;
    private Point _lastDragPoint;
    private bool _isDragging;
    private double _targetYaw = -18d;
    private double _targetPitch = -4d;
    private int _modelBuildCount;

    static MinecraftSkin3DView()
    {
        var background = new SolidColorBrush(Color.FromArgb(0x78, 0x08, 0x0D, 0x14));
        background.Freeze();
        BackgroundProperty.OverrideMetadata(
            typeof(MinecraftSkin3DView),
            new FrameworkPropertyMetadata(background));
    }

    public MinecraftSkin3DView()
    {
        _fallbackSkin = CreateFallbackSkin();
        _activeSkin = _fallbackSkin;

        var avatarTransform = new Transform3DGroup();
        avatarTransform.Children.Add(new RotateTransform3D(_pitchRotation));
        avatarTransform.Children.Add(new RotateTransform3D(_yawRotation));
        _avatar.Transform = avatarTransform;

        var world = new Model3DGroup();
        world.Children.Add(new AmbientLight(Color.FromRgb(0xD8, 0xD8, 0xD8)));
        world.Children.Add(new DirectionalLight(
            Color.FromRgb(0x88, 0x94, 0xA8),
            new Vector3D(-0.45d, -0.65d, -1d)));
        world.Children.Add(_avatar);

        _camera = new PerspectiveCamera(
            position: new Point3D(0d, 0d, 54d),
            lookDirection: new Vector3D(0d, 0d, -54d),
            upDirection: new Vector3D(0d, 1d, 0d),
            fieldOfView: CameraFieldOfView);
        var viewport = new Viewport3D
        {
            Camera = _camera,
            ClipToBounds = true,
            IsHitTestVisible = false
        };
        viewport.Children.Add(new ModelVisual3D { Content = world });
        viewport.SizeChanged += OnViewportSizeChanged;
        Content = viewport;

        RenderOptions.SetBitmapScalingMode(this, BitmapScalingMode.NearestNeighbor);
        RenderOptions.SetBitmapScalingMode(viewport, BitmapScalingMode.NearestNeighbor);
        RenderOptions.SetEdgeMode(viewport, EdgeMode.Aliased);
        SnapsToDevicePixels = true;
        UseLayoutRounding = true;
        Focusable = false;

        _viewInterpolationTimer = new DispatcherTimer(DispatcherPriority.Render, Dispatcher)
        {
            Interval = ViewInterpolationInterval
        };
        _viewInterpolationTimer.Tick += OnViewInterpolationTick;

        Loaded += OnLoaded;
        Unloaded += OnUnloaded;
        IsVisibleChanged += OnIsVisibleChanged;
        MouseLeftButtonDown += OnMouseLeftButtonDown;
        MouseMove += OnMouseMove;
        MouseLeftButtonUp += OnMouseLeftButtonUp;
        MouseLeave += OnMouseLeave;
        LostMouseCapture += OnLostMouseCapture;

        RefreshSkinAndPreview();
    }

    public static readonly DependencyProperty SkinPathProperty = DependencyProperty.Register(
        nameof(SkinPath),
        typeof(string),
        typeof(MinecraftSkin3DView),
        new FrameworkPropertyMetadata(null, OnPreviewInputChanged));

    public static readonly DependencyProperty TextureSourceProperty = DependencyProperty.Register(
        nameof(TextureSource),
        typeof(ImageSource),
        typeof(MinecraftSkin3DView),
        new FrameworkPropertyMetadata(null, OnPreviewInputChanged));

    public static readonly DependencyProperty IsSlimProperty = DependencyProperty.Register(
        nameof(IsSlim),
        typeof(bool),
        typeof(MinecraftSkin3DView),
        new FrameworkPropertyMetadata(false, OnPreviewInputChanged));

    public static readonly DependencyProperty IsUsingFallbackProperty =
        IsUsingFallbackPropertyKey.DependencyProperty;

    public static readonly DependencyProperty IsAnimatingProperty =
        IsAnimatingPropertyKey.DependencyProperty;

    /// <summary>
    /// Gets or sets a local PNG path. <see cref="TextureSource"/> takes precedence when it contains
    /// a valid 64x64 or legacy 64x32 bitmap. Invalid, unavailable, or oversized files render the
    /// built-in fallback.
    /// </summary>
    public string? SkinPath
    {
        get => (string?)GetValue(SkinPathProperty);
        set => SetValue(SkinPathProperty, value);
    }

    /// <summary>
    /// Gets or sets an already-decoded 64x64 or legacy 64x32 bitmap. This supports direct
    /// ImageSource binding while keeping local file loading available through <see cref="SkinPath"/>.
    /// </summary>
    public ImageSource? TextureSource
    {
        get => (ImageSource?)GetValue(TextureSourceProperty);
        set => SetValue(TextureSourceProperty, value);
    }

    /// <summary>
    /// Selects the three-pixel-wide arm geometry used by the slim skin variant.
    /// </summary>
    public bool IsSlim
    {
        get => (bool)GetValue(IsSlimProperty);
        set => SetValue(IsSlimProperty, value);
    }

    public bool IsUsingFallback
        => (bool)GetValue(IsUsingFallbackProperty);

    public bool IsAnimating
        => (bool)GetValue(IsAnimatingProperty);

    internal int ModelPartCount
        => _avatar.Children.Count;

    internal int ModelBoxCount
        => _avatar.Children
            .OfType<Model3DGroup>()
            .Sum(group => group.Children.Count);

    internal int ModelBuildCount
        => _modelBuildCount;

    internal BitmapSource ActiveSkin
        => _activeSkin;

    internal TimeSpan AnimationCycleDuration
        => WalkHalfCycleDuration + WalkHalfCycleDuration;

    internal TimeSpan ViewUpdateInterval
        => _viewInterpolationTimer.Interval;

    internal bool IsViewInterpolating
        => _viewInterpolationTimer.IsEnabled;

    internal bool IsDraggingView
        => _isDragging;

    internal double CurrentArmWidth
        => MinecraftSkinLayout
            .GetBoxes(IsSlim)
            .First(box => box.Part == MinecraftSkinPart.RightArm)
            .Width;

    internal Rect3D GetPartBounds(MinecraftSkinPart part)
        => ((Model3DGroup)_avatar.Children[(int)part]).Bounds;

    internal IReadOnlyDictionary<MinecraftSkinPart, AxisAngleRotation3D> LimbRotations
        => _limbRotations;

    internal double YawAngle
        => _yawRotation.Angle;

    internal double PitchAngle
        => _pitchRotation.Angle;

    internal double TargetYawAngle
        => _targetYaw;

    internal double TargetPitchAngle
        => _targetPitch;

    internal void AdvanceViewInterpolation()
        => OnViewInterpolationTick(sender: null, EventArgs.Empty);

    internal void RotateView(double horizontalPixels, double verticalPixels)
    {
        SetViewTarget(
            _targetYaw + horizontalPixels * DragYawDegreesPerPixel,
            _targetPitch - verticalPixels * DragPitchDegreesPerPixel);
    }

    public void ResetView()
    {
        SetViewTarget(-18d, -4d);
    }

    private static void OnPreviewInputChanged(
        DependencyObject dependencyObject,
        DependencyPropertyChangedEventArgs e)
    {
        if (dependencyObject is MinecraftSkin3DView view)
        {
            if (e.Property == IsSlimProperty)
            {
                view.RebuildModel();
            }
            else
            {
                view.RefreshSkinAndPreview();
            }
        }
    }

    private void RefreshSkinAndPreview()
    {
        _activeSkin = ResolveSkin(out var isUsingFallback);
        _previewTexture = CreatePreviewTexture(_activeSkin);
        SetValue(IsUsingFallbackPropertyKey, isUsingFallback);
        RebuildModel();
    }

    private void RebuildModel()
    {
        _modelBuildCount++;
        var material = CreateMaterialFromPreviewTexture(_previewTexture);
        _avatar.Children.Clear();
        _limbRotations.Clear();

        foreach (var part in Enum.GetValues<MinecraftSkinPart>())
        {
            var partGroup = new Model3DGroup();
            foreach (var box in MinecraftSkinLayout.GetBoxes(IsSlim).Where(box => box.Part == part))
            {
                var model = new GeometryModel3D(MinecraftSkinMeshFactory.Create(box), material);
                if (box.Layer == MinecraftSkinLayer.Outer)
                {
                    // Only the translucent clothing layer needs to remain visible from both
                    // sides. Avoiding an interior pass for the opaque base cuts overdraw and
                    // prevents hidden reverse faces from leaking through transparent pixels.
                    model.BackMaterial = material;
                }

                model.Freeze();
                partGroup.Children.Add(model);
            }

            if (TryGetLimbPivot(part, out var pivot))
            {
                var rotation = new AxisAngleRotation3D(new Vector3D(1d, 0d, 0d), 0d);
                partGroup.Transform = new RotateTransform3D(rotation, pivot);
                _limbRotations.Add(part, rotation);
            }

            _avatar.Children.Add(partGroup);
        }

        ApplyWalkPose(0d);
        if (IsAnimating)
        {
            StartWalkAnimations();
        }
    }

    private BitmapSource ResolveSkin(out bool isUsingFallback)
    {
        if (TryNormalizeSkinForPreview(TextureSource, out var boundSkin)
            || TryLoadSkinFileForPreview(SkinPath, out boundSkin))
        {
            isUsingFallback = false;
            return boundSkin;
        }

        isUsingFallback = true;
        return _fallbackSkin;
    }

    internal static bool TryLoadSkinFileForPreview(string? path, out BitmapSource skin)
    {
        skin = null!;
        if (string.IsNullOrWhiteSpace(path))
        {
            return false;
        }

        try
        {
            var file = new FileInfo(path);
            if (!file.Exists || file.Length is <= 0 or > MaximumSkinFileBytes)
            {
                return false;
            }

            using var stream = new FileStream(
                file.FullName,
                FileMode.Open,
                FileAccess.Read,
                FileShare.ReadWrite | FileShare.Delete,
                bufferSize: 4096,
                FileOptions.SequentialScan);

            Span<byte> signature = stackalloc byte[PngSignature.Length];
            if (stream.Read(signature) != signature.Length
                || !signature.SequenceEqual(PngSignature))
            {
                return false;
            }

            stream.Position = 0;
            var decoder = BitmapDecoder.Create(
                stream,
                BitmapCreateOptions.PreservePixelFormat,
                BitmapCacheOption.OnLoad);
            return decoder.Frames.Count > 0
                && TryNormalizeSkinForPreview(decoder.Frames[0], out skin);
        }
        catch (Exception exception) when (IsRecoverableSkinFailure(exception))
        {
            return false;
        }
    }

    internal static bool TryNormalizeSkinForPreview(ImageSource? source, out BitmapSource skin)
    {
        skin = null!;
        if (source is BitmapImage
                {
                    UriSource: { IsAbsoluteUri: true, IsFile: false }
                }
            || source is not BitmapSource bitmap
            || bitmap.PixelWidth != MinecraftSkinLayout.TextureSize
            || bitmap.PixelHeight is not (32 or MinecraftSkinLayout.TextureSize))
        {
            return false;
        }

        try
        {
            var converted = bitmap.Format == PixelFormats.Pbgra32
                ? bitmap
                : new FormatConvertedBitmap(bitmap, PixelFormats.Pbgra32, null, 0d);
            const int bytesPerPixel = 4;
            var stride = MinecraftSkinLayout.TextureSize * bytesPerPixel;
            var sourcePixels = new byte[stride * bitmap.PixelHeight];
            converted.CopyPixels(sourcePixels, stride, 0);
            var pixels = new byte[stride * MinecraftSkinLayout.TextureSize];
            Buffer.BlockCopy(sourcePixels, 0, pixels, 0, sourcePixels.Length);
            if (!HasTransparency(sourcePixels))
            {
                ClearOpaqueOverlayFaces(
                    pixels,
                    stride,
                    includeModernBodyLayers: bitmap.PixelHeight == MinecraftSkinLayout.TextureSize);
            }

            if (bitmap.PixelHeight == 32)
            {
                ExpandLegacyLimbs(pixels, stride);
            }

            skin = BitmapSource.Create(
                MinecraftSkinLayout.TextureSize,
                MinecraftSkinLayout.TextureSize,
                96d,
                96d,
                PixelFormats.Pbgra32,
                palette: null,
                pixels,
                stride);
            skin.Freeze();
            return true;
        }
        catch (Exception exception) when (IsRecoverableSkinFailure(exception))
        {
            skin = null!;
            return false;
        }
    }

    private static bool HasTransparency(byte[] pixels)
    {
        const int bytesPerPixel = 4;
        for (var alpha = bytesPerPixel - 1; alpha < pixels.Length; alpha += bytesPerPixel)
        {
            if (pixels[alpha] != byte.MaxValue)
            {
                return true;
            }
        }

        return false;
    }

    private static void ClearOpaqueOverlayFaces(
        byte[] pixels,
        int stride,
        bool includeModernBodyLayers)
    {
        foreach (var box in MinecraftSkinLayout.GetBoxes(isSlim: false)
                     .Where(box => box.Layer == MinecraftSkinLayer.Outer
                         && (includeModernBodyLayers || box.Part == MinecraftSkinPart.Head)))
        {
            foreach (var face in box.Faces.Values)
            {
                ClearRectangle(pixels, stride, face);
            }
        }
    }

    private static void ClearRectangle(
        byte[] pixels,
        int stride,
        MinecraftSkinUvRect rectangle)
    {
        const int bytesPerPixel = 4;
        for (var y = rectangle.Y; y < rectangle.Y + rectangle.Height; y++)
        {
            Array.Clear(
                pixels,
                y * stride + rectangle.X * bytesPerPixel,
                rectangle.Width * bytesPerPixel);
        }
    }

    private static void ExpandLegacyLimbs(byte[] pixels, int stride)
    {
        var boxes = MinecraftSkinLayout.GetBoxes(isSlim: false);
        CopyMirroredPart(
            pixels,
            stride,
            boxes.Single(box => box.Part == MinecraftSkinPart.RightArm
                && box.Layer == MinecraftSkinLayer.Base),
            boxes.Single(box => box.Part == MinecraftSkinPart.LeftArm
                && box.Layer == MinecraftSkinLayer.Base));
        CopyMirroredPart(
            pixels,
            stride,
            boxes.Single(box => box.Part == MinecraftSkinPart.RightLeg
                && box.Layer == MinecraftSkinLayer.Base),
            boxes.Single(box => box.Part == MinecraftSkinPart.LeftLeg
                && box.Layer == MinecraftSkinLayer.Base));
    }

    private static void CopyMirroredPart(
        byte[] pixels,
        int stride,
        MinecraftSkinBox source,
        MinecraftSkinBox destination)
    {
        foreach (var face in Enum.GetValues<MinecraftSkinFace>())
        {
            var mirroredSourceFace = face switch
            {
                MinecraftSkinFace.Right => MinecraftSkinFace.Left,
                MinecraftSkinFace.Left => MinecraftSkinFace.Right,
                _ => face,
            };
            CopyMirroredRectangle(
                pixels,
                stride,
                source.Faces[mirroredSourceFace],
                destination.Faces[face]);
        }
    }

    private static void CopyMirroredRectangle(
        byte[] pixels,
        int stride,
        MinecraftSkinUvRect source,
        MinecraftSkinUvRect destination)
    {
        if (source.Width != destination.Width || source.Height != destination.Height)
        {
            throw new InvalidOperationException("Legacy skin faces must have matching dimensions.");
        }

        const int bytesPerPixel = 4;
        for (var y = 0; y < destination.Height; y++)
        {
            for (var x = 0; x < destination.Width; x++)
            {
                var sourceOffset = (source.Y + y) * stride
                    + (source.X + source.Width - x - 1) * bytesPerPixel;
                var destinationOffset = (destination.Y + y) * stride
                    + (destination.X + x) * bytesPerPixel;
                Buffer.BlockCopy(pixels, sourceOffset, pixels, destinationOffset, bytesPerPixel);
            }
        }
    }

    internal static Material CreateSkinMaterial(BitmapSource skin)
    {
        // WPF's 3D texture sampler remains bilinear even when an ImageBrush requests nearest
        // neighbour scaling. Feeding it the original 64x64 atlas therefore blends adjacent
        // Minecraft pixels over a visibly wide area on high-DPI displays. Pixel-replicating the
        // tiny atlas once makes that unavoidable one-texel filter footprint sub-pixel-sized while
        // keeping every UV coordinate and every source colour exact.
        return CreateMaterialFromPreviewTexture(CreatePreviewTexture(skin));
    }

    private static Material CreateMaterialFromPreviewTexture(BitmapSource previewTexture)
    {
        var brush = new ImageBrush(previewTexture)
        {
            AlignmentX = AlignmentX.Left,
            AlignmentY = AlignmentY.Top,
            Stretch = Stretch.Fill,
            TileMode = TileMode.None,
            Viewbox = new Rect(0d, 0d, 1d, 1d),
            ViewboxUnits = BrushMappingMode.RelativeToBoundingBox,
            Viewport = new Rect(0d, 0d, 1d, 1d),
            // A relative viewport is recalculated for every GeometryModel3D and stretches the
            // complete atlas over each body box. Texture coordinates are already normalized to
            // the complete 64x64 atlas, so the material viewport must remain the absolute unit
            // square shared by all boxes.
            ViewportUnits = BrushMappingMode.Absolute
        };
        RenderOptions.SetBitmapScalingMode(brush, BitmapScalingMode.NearestNeighbor);
        brush.Freeze();

        var material = new DiffuseMaterial(brush);
        material.Freeze();
        return material;
    }

    internal static BitmapSource CreatePreviewTexture(BitmapSource skin)
    {
        if (skin.PixelWidth != MinecraftSkinLayout.TextureSize
            || skin.PixelHeight != MinecraftSkinLayout.TextureSize)
        {
            throw new ArgumentException("The preview skin must be a normalized 64x64 atlas.", nameof(skin));
        }

        var source = skin.Format == PixelFormats.Pbgra32
            ? skin
            : new FormatConvertedBitmap(skin, PixelFormats.Pbgra32, null, 0d);
        const int bytesPerPixel = 4;
        var sourceStride = MinecraftSkinLayout.TextureSize * bytesPerPixel;
        var sourcePixels = new byte[sourceStride * MinecraftSkinLayout.TextureSize];
        source.CopyPixels(sourcePixels, sourceStride, 0);

        var scaledSize = MinecraftSkinLayout.TextureSize * PreviewTextureScale;
        var scaledStride = scaledSize * bytesPerPixel;
        var scaledPixels = new byte[scaledStride * scaledSize];
        var expandedRow = new byte[scaledStride];
        for (var sourceY = 0; sourceY < MinecraftSkinLayout.TextureSize; sourceY++)
        {
            var sourceRowOffset = sourceY * sourceStride;
            for (var sourceX = 0; sourceX < MinecraftSkinLayout.TextureSize; sourceX++)
            {
                var sourceOffset = sourceRowOffset + sourceX * bytesPerPixel;
                for (var repeatedX = 0; repeatedX < PreviewTextureScale; repeatedX++)
                {
                    var destinationOffset = (sourceX * PreviewTextureScale + repeatedX)
                        * bytesPerPixel;
                    expandedRow[destinationOffset] = sourcePixels[sourceOffset];
                    expandedRow[destinationOffset + 1] = sourcePixels[sourceOffset + 1];
                    expandedRow[destinationOffset + 2] = sourcePixels[sourceOffset + 2];
                    expandedRow[destinationOffset + 3] = sourcePixels[sourceOffset + 3];
                }
            }

            for (var repeatedY = 0; repeatedY < PreviewTextureScale; repeatedY++)
            {
                Buffer.BlockCopy(
                    expandedRow,
                    0,
                    scaledPixels,
                    (sourceY * PreviewTextureScale + repeatedY) * scaledStride,
                    scaledStride);
            }
        }

        var texture = BitmapSource.Create(
            scaledSize,
            scaledSize,
            96d,
            96d,
            PixelFormats.Pbgra32,
            palette: null,
            scaledPixels,
            scaledStride);
        texture.Freeze();
        return texture;
    }

    internal static bool TryGetLimbPivot(
        MinecraftSkinPart part,
        out Point3D pivot)
    {
        pivot = part switch
        {
            // Minecraft's arm joint is two pixels below the top and one classic-arm pixel
            // toward the torso. Rotating at the box's top-centre pulls the shoulder apart.
            MinecraftSkinPart.RightArm => new Point3D(-5d, 6d, 0d),
            MinecraftSkinPart.LeftArm => new Point3D(5d, 6d, 0d),
            MinecraftSkinPart.RightLeg => new Point3D(-2d, -4d, 0d),
            MinecraftSkinPart.LeftLeg => new Point3D(2d, -4d, 0d),
            _ => default
        };
        return part is MinecraftSkinPart.RightArm
            or MinecraftSkinPart.LeftArm
            or MinecraftSkinPart.RightLeg
            or MinecraftSkinPart.LeftLeg;
    }

    private void OnLoaded(object sender, RoutedEventArgs e)
        => UpdateAnimationState();

    private void OnUnloaded(object sender, RoutedEventArgs e)
    {
        SetWalkAnimationActive(false);
        StopViewInterpolation(keepTarget: false);
        EndDrag();
    }

    private void OnIsVisibleChanged(object sender, DependencyPropertyChangedEventArgs e)
        => UpdateAnimationState();

    private void UpdateAnimationState()
    {
        var isActive = IsLoaded && IsVisible;
        SetWalkAnimationActive(isActive);
        if (!isActive)
        {
            StopViewInterpolation(keepTarget: false);
        }
        else if (!IsViewAtTarget())
        {
            _viewInterpolationTimer.Start();
        }
    }

    private void SetWalkAnimationActive(bool isActive)
    {
        if (isActive)
        {
            StartWalkAnimations();
            SetValue(IsAnimatingPropertyKey, true);
            return;
        }

        StopWalkAnimations();
        SetValue(IsAnimatingPropertyKey, false);
    }

    private void StartWalkAnimations()
    {
        StartLimbAnimation(MinecraftSkinPart.RightArm, startsForward: true);
        StartLimbAnimation(MinecraftSkinPart.LeftArm, startsForward: false);
        StartLimbAnimation(MinecraftSkinPart.RightLeg, startsForward: false);
        StartLimbAnimation(MinecraftSkinPart.LeftLeg, startsForward: true);
    }

    private void StartLimbAnimation(MinecraftSkinPart part, bool startsForward)
    {
        if (!_limbRotations.TryGetValue(part, out var rotation))
        {
            return;
        }

        rotation.BeginAnimation(
            AxisAngleRotation3D.AngleProperty,
            startsForward ? ForwardWalkAnimation : BackwardWalkAnimation,
            HandoffBehavior.SnapshotAndReplace);
    }

    private static DoubleAnimation CreateWalkAnimation(bool startsForward)
    {
        var from = startsForward ? WalkSwingDegrees : -WalkSwingDegrees;
        var animation = new DoubleAnimation
        {
            From = from,
            To = -from,
            Duration = new Duration(WalkHalfCycleDuration),
            AutoReverse = true,
            RepeatBehavior = RepeatBehavior.Forever,
            EasingFunction = new SineEase { EasingMode = EasingMode.EaseInOut }
        };
        animation.Freeze();
        return animation;
    }

    private void StopWalkAnimations()
    {
        foreach (var rotation in _limbRotations.Values)
        {
            rotation.BeginAnimation(AxisAngleRotation3D.AngleProperty, null);
            rotation.Angle = 0d;
        }
    }

    private void SetViewTarget(double yaw, double pitch)
    {
        _targetYaw = yaw;
        _targetPitch = pitch;
        if (IsLoaded && IsVisible)
        {
            _viewInterpolationTimer.Start();
        }
    }

    private void OnViewInterpolationTick(object? sender, EventArgs e)
    {
        var yawDelta = _targetYaw - _yawRotation.Angle;
        var pitchDelta = _targetPitch - _pitchRotation.Angle;
        _yawRotation.Angle = Math.Abs(yawDelta) <= ViewInterpolationEpsilon
            ? _targetYaw
            : _yawRotation.Angle + yawDelta * ViewInterpolationFactor;
        _pitchRotation.Angle = Math.Abs(pitchDelta) <= ViewInterpolationEpsilon
            ? _targetPitch
            : _pitchRotation.Angle + pitchDelta * ViewInterpolationFactor;

        if (IsViewAtTarget())
        {
            StopViewInterpolation(keepTarget: true);
            NormalizeSettledViewAngles();
        }
    }

    private bool IsViewAtTarget()
        => Math.Abs(_targetYaw - _yawRotation.Angle) <= ViewInterpolationEpsilon
            && Math.Abs(_targetPitch - _pitchRotation.Angle) <= ViewInterpolationEpsilon;

    private void StopViewInterpolation(bool keepTarget)
    {
        _viewInterpolationTimer.Stop();
        if (!keepTarget)
        {
            _targetYaw = _yawRotation.Angle;
            _targetPitch = _pitchRotation.Angle;
        }
    }

    private void NormalizeSettledViewAngles()
    {
        if (Math.Abs(_yawRotation.Angle) > 3600d)
        {
            _yawRotation.Angle %= 360d;
            _targetYaw = _yawRotation.Angle;
        }

        if (Math.Abs(_pitchRotation.Angle) > 3600d)
        {
            _pitchRotation.Angle %= 360d;
            _targetPitch = _pitchRotation.Angle;
        }
    }

    private void OnViewportSizeChanged(object sender, SizeChangedEventArgs e)
    {
        if (e.NewSize.Width <= 0d || e.NewSize.Height <= 0d)
        {
            return;
        }

        // PerspectiveCamera.FieldOfView is horizontal in WPF. Increase the distance for a wide,
        // shallow card so the complete 32-pixel-tall avatar remains visible without a render loop.
        var aspectRatio = e.NewSize.Width / e.NewSize.Height;
        var halfFieldOfViewRadians = CameraFieldOfView * Math.PI / 360d;
        var distance = Math.Max(
            48d,
            CameraVerticalHalfExtent * aspectRatio / Math.Tan(halfFieldOfViewRadians));
        _camera.Position = new Point3D(0d, 0d, distance);
        _camera.LookDirection = new Vector3D(0d, 0d, -distance);
    }

    private void ApplyWalkPose(double angle)
    {
        SetLimbAngle(MinecraftSkinPart.RightArm, angle);
        SetLimbAngle(MinecraftSkinPart.LeftArm, -angle);
        SetLimbAngle(MinecraftSkinPart.RightLeg, -angle);
        SetLimbAngle(MinecraftSkinPart.LeftLeg, angle);
    }

    private void SetLimbAngle(MinecraftSkinPart part, double angle)
    {
        if (_limbRotations.TryGetValue(part, out var rotation))
        {
            rotation.Angle = angle;
        }
    }

    private void OnMouseLeftButtonDown(object sender, MouseButtonEventArgs e)
    {
        if (CaptureMouse())
        {
            BeginDrag(e.GetPosition(this));
            e.Handled = true;
        }
    }

    private void OnMouseMove(object sender, MouseEventArgs e)
    {
        if (ContinueDrag(e.GetPosition(this), e.LeftButton == MouseButtonState.Pressed))
        {
            e.Handled = true;
        }
    }

    private void OnMouseLeftButtonUp(object sender, MouseButtonEventArgs e)
    {
        if (!_isDragging)
        {
            return;
        }

        EndDrag();
        e.Handled = true;
    }

    private void OnLostMouseCapture(object sender, MouseEventArgs e)
        => EndDrag(releaseCapture: false);

    private void OnMouseLeave(object sender, MouseEventArgs e)
        => EndDrag();

    internal void BeginDrag(Point point)
    {
        StopViewInterpolation(keepTarget: false);
        _lastDragPoint = point;
        _isDragging = true;
        Cursor = Cursors.SizeAll;
    }

    internal bool ContinueDrag(Point point, bool isLeftButtonPressed)
    {
        if (!_isDragging)
        {
            return false;
        }

        if (!isLeftButtonPressed)
        {
            EndDrag();
            return false;
        }

        var delta = point - _lastDragPoint;
        _lastDragPoint = point;
        RotateView(delta.X, delta.Y);
        return true;
    }

    internal void EndDrag(bool releaseCapture = true)
    {
        _isDragging = false;
        Cursor = null;
        if (releaseCapture && IsMouseCaptured)
        {
            ReleaseMouseCapture();
        }

        StopViewInterpolation(keepTarget: false);
    }

    private static BitmapSource CreateFallbackSkin()
    {
        const int bytesPerPixel = 4;
        var stride = MinecraftSkinLayout.TextureSize * bytesPerPixel;
        var pixels = new byte[stride * MinecraftSkinLayout.TextureSize];

        foreach (var box in MinecraftSkinLayout.GetBoxes(isSlim: false)
                     .Where(box => box.Layer == MinecraftSkinLayer.Base))
        {
            var color = box.Part switch
            {
                MinecraftSkinPart.Head => Color.FromRgb(0xC9, 0xA4, 0x82),
                MinecraftSkinPart.Body => Color.FromRgb(0x23, 0x8B, 0x91),
                MinecraftSkinPart.RightArm or MinecraftSkinPart.LeftArm =>
                    Color.FromRgb(0xB8, 0x91, 0x72),
                _ => Color.FromRgb(0x2F, 0x4D, 0x78)
            };

            foreach (var face in box.Faces.Values)
            {
                FillRectangle(pixels, stride, face, color, alpha: 0xFF);
            }
        }

        var headFront = MinecraftSkinLayout.GetBoxes(isSlim: false)
            .Single(box => box.Part == MinecraftSkinPart.Head
                && box.Layer == MinecraftSkinLayer.Outer)
            .Faces[MinecraftSkinFace.Front];
        FillBorder(
            pixels,
            stride,
            headFront,
            Color.FromRgb(0x19, 0x25, 0x32),
            alpha: 0xC8);

        var bitmap = BitmapSource.Create(
            MinecraftSkinLayout.TextureSize,
            MinecraftSkinLayout.TextureSize,
            96d,
            96d,
            PixelFormats.Bgra32,
            palette: null,
            pixels,
            stride);
        bitmap.Freeze();
        return bitmap;
    }

    private static void FillRectangle(
        byte[] pixels,
        int stride,
        MinecraftSkinUvRect rectangle,
        Color color,
        byte alpha)
    {
        for (var y = rectangle.Y; y < rectangle.Y + rectangle.Height; y++)
        {
            for (var x = rectangle.X; x < rectangle.X + rectangle.Width; x++)
            {
                SetPixel(pixels, stride, x, y, color, alpha);
            }
        }
    }

    private static void FillBorder(
        byte[] pixels,
        int stride,
        MinecraftSkinUvRect rectangle,
        Color color,
        byte alpha)
    {
        for (var y = rectangle.Y; y < rectangle.Y + rectangle.Height; y++)
        {
            for (var x = rectangle.X; x < rectangle.X + rectangle.Width; x++)
            {
                if (x == rectangle.X
                    || x == rectangle.X + rectangle.Width - 1
                    || y == rectangle.Y
                    || y == rectangle.Y + rectangle.Height - 1)
                {
                    SetPixel(pixels, stride, x, y, color, alpha);
                }
            }
        }
    }

    private static void SetPixel(
        byte[] pixels,
        int stride,
        int x,
        int y,
        Color color,
        byte alpha)
    {
        var offset = y * stride + x * 4;
        pixels[offset] = color.B;
        pixels[offset + 1] = color.G;
        pixels[offset + 2] = color.R;
        pixels[offset + 3] = alpha;
    }

    private static bool IsRecoverableSkinFailure(Exception exception)
        => exception is IOException
            or UnauthorizedAccessException
            or ArgumentException
            or FormatException
            or NotSupportedException
            or InvalidOperationException
            or System.Runtime.InteropServices.ExternalException
            or System.Security.SecurityException;
}
