using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Globalization;
using System.IO;
using System.Runtime.InteropServices;
using System.Text;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Input;
using Microsoft.UI.Xaml.Media;
using SkiaSharp;
using Uno.WinUI.Graphics2DSK;
using Windows.Foundation;
using Windows.System;

namespace MeshModeler.SkiaSharp.Uno.Controls;

public readonly record struct MeshModelerStats(
    double FrameMilliseconds,
    double RenderMilliseconds,
    double FramesPerSecond,
    int MeshVertices,
    int MeshTriangles,
    int SubmittedVertices,
    int SubmittedIndices,
    int DrawCalls,
    int UniformBytes,
    int SelectedVertex,
    double CameraYawDegrees,
    double CameraPitchDegrees,
    double CameraDistance,
    string ShadingMode,
    string Backend,
    string Status);

public sealed partial class MeshModelerSurface : SKCanvasElement
{
    private const int MaxSubmittedVertices = 65520;
    private const int MaxSubmittedIndices = 65520;
    private const int VertexStride = 14 * sizeof(float);
    private const int SplatVertexStride = 9 * sizeof(float);
    private const int UniformFloatCount = 28;
    private const float NearPlane = 0.08f;
    private const int DepthSortedMaterialTriangleLimit = 350_000;
    private const int MaxSplatsPerBatch = MaxSubmittedIndices / 6;
    private const float SphericalHarmonicsC0 = 0.28209479177387814f;

    private const string VertexShader = @"
        uniform float4 u_view;
        uniform float4 u_light;
        uniform float4 u_texture;
        uniform float4 u_camera0;
        uniform float4 u_camera1;
        uniform float4 u_camera2;
        uniform float4 u_camera3;

        Varyings main(const Attributes attrs) {
            Varyings v;
            float3 rel = attrs.position - u_camera0.xyz;
            float3 right = normalize(u_camera1.xyz);
            float3 up = normalize(u_camera2.xyz);
            float3 forward = normalize(u_camera3.xyz);
            float vx = dot(rel, right);
            float vy = dot(rel, up);
            float vz = max(dot(rel, forward), u_light.w);
            float focal = u_camera0.w + u_texture.x * 0.0;
            v.position = float2(u_view.y * 0.5 + vx / vz * focal, u_camera1.w * 0.5 - vy / vz * focal);
            v.uv = attrs.uv;
            float3 n = normalize(attrs.normal);
            v.normal = normalize(float3(dot(n, right), dot(n, up), dot(n, forward)));
            v.depth = vz;
            v.material = attrs.material;
            v.bary = attrs.bary;
            return v;
        }";

    private const string ProjectedVertexShader = @"
        uniform float4 u_view;
        uniform float4 u_light;
        uniform float4 u_texture;
        uniform float4 u_camera0;
        uniform float4 u_camera1;
        uniform float4 u_camera2;
        uniform float4 u_camera3;

        Varyings main(const Attributes attrs) {
            Varyings v;
            float keepUniformLayout = (u_camera0.x + u_camera1.x + u_camera2.x + u_camera3.x) * 0.000000000000000001;
            v.position = attrs.position + float2(keepUniformLayout, keepUniformLayout);
            v.uv = attrs.uv;
            v.normal = normalize(attrs.normal);
            v.depth = attrs.depth + u_texture.x * 0.0;
            v.material = attrs.material;
            v.bary = attrs.bary;
            return v;
        }";

    private const string SplatVertexShader = @"
        uniform float4 u_view;
        uniform float4 u_light;
        uniform float4 u_texture;
        uniform float4 u_camera0;
        uniform float4 u_camera1;
        uniform float4 u_camera2;
        uniform float4 u_camera3;

        Varyings main(const Attributes attrs) {
            Varyings v;
            float keepUniformLayout = (u_view.x + u_light.x + u_texture.x + u_camera0.x + u_camera1.x + u_camera2.x + u_camera3.x) * 0.000000000000000001;
            v.position = attrs.position + float2(keepUniformLayout, keepUniformLayout);
            v.local = attrs.local;
            v.color = attrs.color;
            v.alpha = attrs.alpha;
            v.depth = attrs.depth;
            return v;
        }";

    private const string SplatFragmentShader = @"
        float2 main(const Varyings v, out half4 color) {
            float r2 = dot(v.local, v.local);
            float support = 1.0 - smoothstep(0.96, 1.0, r2);
            float alpha = clamp(v.alpha * exp(-4.5 * r2) * support, 0.0, 1.0);
            float halo = exp(-1.6 * r2) * 0.035 * support;
            float3 rgb = clamp(v.color * (alpha + halo), 0.0, 1.0);
            color = half4(half3(rgb), half(alpha));
            return v.position;
        }";

    private const string FragmentShaderColor = @"
        uniform float4 u_view;
        uniform float4 u_light;
        uniform float4 u_texture;

        float checker(float2 uv) {
            float2 cell = floor(uv * 10.0);
            float parity = mod(cell.x + cell.y, 2.0);
            return mix(0.35, 1.0, parity);
        }

        float gridLine(float2 uv) {
            float2 g = abs(fract(uv * 10.0) - 0.5);
            float d = min(g.x, g.y);
            return 1.0 - smoothstep(0.020, 0.055, d);
        }

        float2 main(const Varyings v, out half4 color) {
            float mode = u_view.w;
            float3 n = normalize(v.normal);
            float3 light = normalize(u_light.xyz);
            float diffuse = clamp(dot(n, light), 0.0, 1.0);
            float rim = pow(1.0 - clamp(abs(n.z), 0.0, 1.0), 2.0);
            float depth01 = clamp((v.depth - u_light.w) / max(u_view.z - u_light.w, 0.001), 0.0, 1.0);
            float3 rgb;

            if (mode < 0.5) {
                float c = checker(v.uv);
                float line = gridLine(v.uv);
                float uvTint = mix(0.86, 1.05, c);
                rgb = v.material * uvTint * (0.30 + diffuse * 0.90);
                rgb += line * float3(0.06, 0.65, 0.95) * 0.45;
                rgb += rim * (v.material + float3(0.08, 0.18, 0.24)) * 0.32;
            } else if (mode < 1.5) {
                rgb = mix(float3(0.06, 0.14, 0.30), float3(0.95, 0.42, 0.15), depth01);
                rgb *= 0.30 + diffuse * 0.70;
            } else {
                rgb = n * 0.5 + 0.5;
                rgb *= 0.45 + diffuse * 0.55;
            }

            if (u_view.x > 0.5) {
                float wire = 1.0 - smoothstep(0.012, 0.035, min(min(v.bary.x, v.bary.y), v.bary.z));
                rgb = mix(rgb, float3(0.0, 0.0, 0.0), wire * 0.72);
            }

            color = half4(half3(rgb), half(u_texture.w));
            return v.position;
        }";

    private const string FragmentShaderTextured = @"
        uniform float4 u_view;
        uniform float4 u_light;
        uniform float4 u_texture;
        uniform shader diffuseTexture;

        float checker(float2 uv) {
            float2 cell = floor(uv * 10.0);
            float parity = mod(cell.x + cell.y, 2.0);
            return mix(0.35, 1.0, parity);
        }

        float gridLine(float2 uv) {
            float2 g = abs(fract(uv * 10.0) - 0.5);
            float d = min(g.x, g.y);
            return 1.0 - smoothstep(0.020, 0.055, d);
        }

        float2 main(const Varyings v, out half4 color) {
            float mode = u_view.w;
            float3 n = normalize(v.normal);
            float3 light = normalize(u_light.xyz);
            float diffuse = clamp(dot(n, light), 0.0, 1.0);
            float rim = pow(1.0 - clamp(abs(n.z), 0.0, 1.0), 2.0);
            float depth01 = clamp((v.depth - u_light.w) / max(u_view.z - u_light.w, 0.001), 0.0, 1.0);
            float3 rgb;

            if (mode < 0.5) {
                float c = checker(v.uv);
                float line = gridLine(v.uv);
                float uvTint = mix(0.86, 1.05, c);
                float texWeight = 0.0;
                float3 base = v.material;
                if (u_texture.z > 0.5) {
                    half4 tex = diffuseTexture.eval(float2(v.uv.x * u_texture.x, v.uv.y * u_texture.y));
                    texWeight = float(tex.a);
                    base *= float3(tex.r, tex.g, tex.b);
                }

                rgb = base * mix(uvTint, 1.0, texWeight) * (0.30 + diffuse * 0.90);
                rgb += line * float3(0.06, 0.65, 0.95) * (0.45 * (1.0 - texWeight * 0.75));
                rgb += rim * (v.material + float3(0.08, 0.18, 0.24)) * 0.32;
            } else if (mode < 1.5) {
                rgb = mix(float3(0.06, 0.14, 0.30), float3(0.95, 0.42, 0.15), depth01);
                rgb *= 0.30 + diffuse * 0.70;
            } else {
                rgb = n * 0.5 + 0.5;
                rgb *= 0.45 + diffuse * 0.55;
            }

            if (u_view.x > 0.5) {
                float wire = 1.0 - smoothstep(0.012, 0.035, min(min(v.bary.x, v.bary.y), v.bary.z));
                rgb = mix(rgb, float3(0.0, 0.0, 0.0), wire * 0.72);
            }

            color = half4(half3(rgb), half(u_texture.w));
            return v.position;
        }";

    private static readonly SKMeshSpecificationAttribute[] Attributes =
    {
        new(SKMeshSpecificationAttributeType.Float3, 0, "position"),
        new(SKMeshSpecificationAttributeType.Float2, 12, "uv"),
        new(SKMeshSpecificationAttributeType.Float3, 20, "normal"),
        new(SKMeshSpecificationAttributeType.Float3, 32, "material"),
        new(SKMeshSpecificationAttributeType.Float3, 44, "bary"),
    };

    private static readonly SKMeshSpecificationAttribute[] ProjectedAttributes =
    {
        new(SKMeshSpecificationAttributeType.Float2, 0, "position"),
        new(SKMeshSpecificationAttributeType.Float2, 8, "uv"),
        new(SKMeshSpecificationAttributeType.Float3, 16, "normal"),
        new(SKMeshSpecificationAttributeType.Float, 28, "depth"),
        new(SKMeshSpecificationAttributeType.Float3, 32, "material"),
        new(SKMeshSpecificationAttributeType.Float3, 44, "bary"),
    };

    private static readonly SKMeshSpecificationVarying[] Varyings =
    {
        new(SKMeshSpecificationVaryingType.Float2, "uv"),
        new(SKMeshSpecificationVaryingType.Float3, "normal"),
        new(SKMeshSpecificationVaryingType.Float, "depth"),
        new(SKMeshSpecificationVaryingType.Float3, "material"),
        new(SKMeshSpecificationVaryingType.Float3, "bary"),
    };

    private static readonly SKMeshSpecificationAttribute[] SplatAttributes =
    {
        new(SKMeshSpecificationAttributeType.Float2, 0, "position"),
        new(SKMeshSpecificationAttributeType.Float2, 8, "local"),
        new(SKMeshSpecificationAttributeType.Float3, 16, "color"),
        new(SKMeshSpecificationAttributeType.Float, 28, "alpha"),
        new(SKMeshSpecificationAttributeType.Float, 32, "depth"),
    };

    private static readonly SKMeshSpecificationVarying[] SplatVaryings =
    {
        new(SKMeshSpecificationVaryingType.Float2, "local"),
        new(SKMeshSpecificationVaryingType.Float3, "color"),
        new(SKMeshSpecificationVaryingType.Float, "alpha"),
        new(SKMeshSpecificationVaryingType.Float, "depth"),
    };

    private readonly Stopwatch _clock = Stopwatch.StartNew();
    private readonly Stopwatch _renderClock = new();
    private readonly float[] _uniformData = new float[UniformFloatCount];
    private readonly MeshVertex[] _submittedVertices = new MeshVertex[MaxSubmittedVertices];
    private readonly ProjectedMeshVertex[] _projectedVertices = new ProjectedMeshVertex[MaxSubmittedVertices];
    private readonly SplatVertex[] _splatVertices = new SplatVertex[MaxSubmittedVertices];
    private readonly ushort[] _submittedIndices = new ushort[MaxSubmittedIndices];
    private readonly List<ProjectedTriangle> _projectedTriangles = new(4096);
    private readonly List<SplatDrawItem> _splatDrawItems = new(16384);
    private readonly List<MeshBatch> _meshBatches = new(16);
    private readonly List<MeshBatch> _projectedMeshBatches = new(16);
    private bool _meshBatchesDirty = true;
    private bool _projectedMeshBatchesDirty = true;
    private readonly SKPaint _meshPaint = new() { IsAntialias = true, BlendMode = SKBlendMode.SrcOver, Color = SKColors.White };
    private readonly SKPaint _gridPaint = new() { IsAntialias = true, StrokeWidth = 1, Color = new SKColor(62, 91, 118, 120) };
    private readonly SKPaint _axisXPaint = new() { IsAntialias = true, StrokeWidth = 2, Color = new SKColor(240, 80, 80, 190) };
    private readonly SKPaint _axisYPaint = new() { IsAntialias = true, StrokeWidth = 2, Color = new SKColor(80, 230, 130, 190) };
    private readonly SKPaint _axisZPaint = new() { IsAntialias = true, StrokeWidth = 2, Color = new SKColor(90, 155, 255, 190) };
    private readonly SKPaint _vertexPaint = new() { IsAntialias = true, Color = new SKColor(230, 245, 255, 210) };
    private readonly SKPaint _selectedVertexPaint = new() { IsAntialias = true, Color = new SKColor(0, 255, 210, 255) };
    private readonly SKPaint _textPaint = new() { IsAntialias = true, Color = new SKColor(210, 226, 242, 230) };
    private readonly SKFont _textFont = new(SKTypeface.Default, 13);

    private MeshDocument _document = MeshDocument.CreateTorus();
    private GaussianSplatCloud? _splatCloud;
    private SKMeshSpecification? _colorSpecification;
    private SKMeshSpecification? _textureSpecification;
    private SKMeshSpecification? _projectedColorSpecification;
    private SKMeshSpecification? _projectedTextureSpecification;
    private SKMeshSpecification? _splatSpecification;
    private float _yaw = -0.58f;
    private float _pitch = 0.38f;
    private float _distance = 4.6f;
    private Vec3 _target = new(0, 0, 0);
    private CameraBasis _camera;
    private float _mode;
    private float _lastWidth;
    private float _lastHeight;
    private int _submittedVertexCount;
    private int _submittedIndexCount;
    private int _projectedVisibleTriangleCount;
    private float _projectedCacheWidth;
    private float _projectedCacheHeight;
    private float _projectedCacheYaw;
    private float _projectedCachePitch;
    private float _projectedCacheDistance;
    private float _projectedCacheTargetX;
    private float _projectedCacheTargetY;
    private float _projectedCacheTargetZ;
    private int _projectedCacheModeBucket;
    private bool _projectedCacheUsesPerMaterialUniforms;
    private int _drawCalls;
    private int _selectedVertex = -1;
    private bool _editMode;
    private bool _dragging;
    private bool _draggingVertex;
    private PointerAction _pointerAction;
    private Point _lastPointer;
    private double _lastFrameSeconds;
    private double _lastStatsSeconds;
    private double _framesPerSecond;
    private int _frameCount;
    private bool _showVertexHandles;
    private bool _showMeshGrid;
    private string _status = "Waiting for first render.";

    public MeshModelerSurface()
    {
        HorizontalAlignment = HorizontalAlignment.Stretch;
        VerticalAlignment = VerticalAlignment.Stretch;
        IsTabStop = true;
        Loaded += OnLoaded;
        Unloaded += OnUnloaded;
        PointerPressed += OnPointerPressed;
        PointerMoved += OnPointerMoved;
        PointerReleased += OnPointerReleased;
        PointerWheelChanged += OnPointerWheelChanged;
        KeyDown += OnKeyDown;
    }

    public event EventHandler<MeshModelerStats>? StatsUpdated;

    public void LoadSampleTorus()
    {
        ReplaceDocument(MeshDocument.CreateTorus());
        ResetView();
        _status = "Loaded procedural torus sample.";
        Invalidate();
    }

    public void LoadSampleCube()
    {
        ReplaceDocument(MeshDocument.ParseObj(BuiltInCubeObj, "Textured cube OBJ"));
        ResetView();
        _status = "Loaded built-in textured cube OBJ.";
        Invalidate();
    }

    public void LoadSampleSplats()
    {
        ReplaceSplatCloud(GaussianSplatCloud.CreateSample());
        ResetView();
        _status = BuildSplatLoadStatus(_splatCloud!.Name);
        Invalidate();
    }

    public void LoadObjText(string objText, string name)
    {
        ReplaceDocument(MeshDocument.ParseObj(objText, name));
        ResetView();
        _status = BuildLoadStatus(name);
        Invalidate();
    }

    public void LoadObjFile(string path)
    {
        var name = System.IO.Path.GetFileName(path);
        var baseDirectory = System.IO.Path.GetDirectoryName(path);
        ReplaceDocument(MeshDocument.ParseObj(File.ReadAllText(path), name, baseDirectory));
        ResetView();
        _status = BuildLoadStatus(name);
        Invalidate();
    }

    public void LoadGaussianSplatFile(string path)
    {
        var name = System.IO.Path.GetFileName(path);
        ReplaceSplatCloud(GaussianSplatCloud.LoadPly(path, name));
        ResetView();
        _status = BuildSplatLoadStatus(name);
        Invalidate();
    }

    public void ToggleEditMode()
    {
        if (_splatCloud is not null)
        {
            _editMode = false;
            _selectedVertex = -1;
            _status = "Edit mode is disabled for Gaussian splat clouds; splats are rendered as oriented density kernels rather than editable mesh vertices.";
            Invalidate();
            return;
        }

        _editMode = !_editMode;
        _status = _editMode
            ? "Edit mode enabled. Click a projected vertex, then drag it in the camera plane."
            : "Edit mode disabled. Left drag orbits the camera.";
        Invalidate();
    }

    public void ToggleVertexHandles()
    {
        _showVertexHandles = !_showVertexHandles;
        _status = _showVertexHandles
            ? "Vertex handles visible. Press H or Handles to hide them."
            : "Vertex handles hidden. Edit mode still shows them for picking.";
        Focus(FocusState.Programmatic);
        Invalidate();
    }

    public void ToggleMeshGrid()
    {
        _showMeshGrid = !_showMeshGrid;
        _status = _showMeshGrid
            ? "Mesh grid overlay visible. Press G or Mesh Grid to hide it."
            : "Mesh grid overlay hidden for material rendering.";
        Focus(FocusState.Programmatic);
        Invalidate();
    }

    public void SetShadingMode(int mode)
    {
        _mode = Math.Clamp(mode, 0, 2);
        _status = _mode switch
        {
            < 0.5f => "Shading: OBJ material/texture child shader + UV checker + Lambert lighting.",
            < 1.5f => "Shading: depth visualization.",
            _ => "Shading: normal visualization."
        };
        Focus(FocusState.Programmatic);
        Invalidate();
    }

    private string BuildLoadStatus(string name)
    {
        var normalText = _document.UseAuthoredNormals ? "authored normals" : "computed normals";
        var materialText = _document.MaterialCount == 1 ? "1 material" : $"{_document.MaterialCount:N0} materials";
        var skippedText = _document.SkippedFaceCount > 0
            ? $" Skipped {_document.SkippedFaceCount:N0} non-triangulatable face record{(_document.SkippedFaceCount == 1 ? string.Empty : "s")}."
            : string.Empty;
        var helperText = _document.SkippedSceneHelperFaceCount > 0
            ? $" Ignored {_document.SkippedSceneHelperFaceCount:N0} Blender scene-helper face record{(_document.SkippedSceneHelperFaceCount == 1 ? string.Empty : "s")}."
            : string.Empty;

        var textureText = _document.TextureMaterialCount == 1 ? "1 textured material" : $"{_document.TextureMaterialCount:N0} textured materials";
        return $"Loaded OBJ '{name}' with {_document.Positions.Count:N0} vertices, {_document.Triangles.Count:N0} triangles, {materialText}, {textureText}, and {normalText}.{skippedText}{helperText}";
    }

    private string BuildSplatLoadStatus(string name)
    {
        if (_splatCloud is null)
        {
            return $"Loaded Gaussian splat PLY '{name}'.";
        }

        var format = _splatCloud.SourceFormat.Length == 0 ? "PLY" : _splatCloud.SourceFormat;
        return $"Loaded Gaussian splat PLY '{name}' with {_splatCloud.Splats.Length:N0} splats from {format}; rendering as sorted anisotropic SKMesh quads.";
    }

    protected override void RenderOverride(SKCanvas canvas, Size area)
    {
        _lastWidth = Math.Max(1, (float)area.Width);
        _lastHeight = Math.Max(1, (float)area.Height);
        _renderClock.Restart();
        DrawScene(canvas, _lastWidth, _lastHeight);
        _renderClock.Stop();
        PublishStats(_renderClock.Elapsed.TotalMilliseconds);
    }

    private void OnLoaded(object sender, RoutedEventArgs e)
    {
        CompositionTarget.Rendering += OnRendering;
        Focus(FocusState.Programmatic);
        Invalidate();
    }

    private void OnUnloaded(object sender, RoutedEventArgs e)
    {
        CompositionTarget.Rendering -= OnRendering;
        _colorSpecification?.Dispose();
        _textureSpecification?.Dispose();
        _projectedColorSpecification?.Dispose();
        _projectedTextureSpecification?.Dispose();
        _splatSpecification?.Dispose();
        _colorSpecification = null;
        _textureSpecification = null;
        _projectedColorSpecification = null;
        _projectedTextureSpecification = null;
        _splatSpecification = null;
        DisposeMeshBatches();
        DisposeProjectedMeshBatches();
        _document.Dispose();
    }

    private void OnRendering(object? sender, object e) => Invalidate();

    private void OnPointerPressed(object sender, PointerRoutedEventArgs e)
    {
        Focus(FocusState.Pointer);
        CapturePointer(e.Pointer);
        _dragging = true;
        _lastPointer = e.GetCurrentPoint(this).Position;
        var props = e.GetCurrentPoint(this).Properties;

        if (_editMode && props.IsLeftButtonPressed)
        {
            _selectedVertex = FindNearestVertex((float)_lastPointer.X, (float)_lastPointer.Y, _lastWidth, _lastHeight, maxDistance: 18.0f);
            _draggingVertex = _selectedVertex >= 0;
            _pointerAction = _draggingVertex ? PointerAction.EditVertex : PointerAction.Orbit;
            _status = _draggingVertex
                ? $"Editing vertex {_selectedVertex}. Drag in the camera plane."
                : "No vertex under pointer. Left drag orbits.";
        }
        else if (props.IsRightButtonPressed || props.IsMiddleButtonPressed)
        {
            _pointerAction = PointerAction.Pan;
        }
        else
        {
            _pointerAction = PointerAction.Orbit;
        }

        Invalidate();
    }

    private void OnPointerMoved(object sender, PointerRoutedEventArgs e)
    {
        if (!_dragging)
        {
            return;
        }

        var point = e.GetCurrentPoint(this).Position;
        var dx = (float)(point.X - _lastPointer.X);
        var dy = (float)(point.Y - _lastPointer.Y);
        _lastPointer = point;

        switch (_pointerAction)
        {
            case PointerAction.Orbit:
                _yaw += dx * 0.008f;
                _pitch = Math.Clamp(_pitch + dy * 0.006f, -1.35f, 1.35f);
                _status = "Orbit camera: left drag. Pan: right/middle drag. Zoom: wheel.";
                break;
            case PointerAction.Pan:
                PanCamera(dx, dy);
                _status = "Panning camera target.";
                break;
            case PointerAction.EditVertex:
                MoveSelectedVertex(dx, dy);
                _status = $"Editing vertex {_selectedVertex}.";
                break;
        }

        Invalidate();
    }

    private void OnPointerReleased(object sender, PointerRoutedEventArgs e)
    {
        _dragging = false;
        _draggingVertex = false;
        _pointerAction = PointerAction.None;
        ReleasePointerCapture(e.Pointer);
    }

    private void OnPointerWheelChanged(object sender, PointerRoutedEventArgs e)
    {
        var delta = e.GetCurrentPoint(this).Properties.MouseWheelDelta;
        var factor = delta > 0 ? 0.88f : 1.13f;
        _distance = Math.Clamp(_distance * factor, 1.25f, 25.0f);
        _status = $"Zoom {_distance:F2}.";
        e.Handled = true;
        Invalidate();
    }

    private void OnKeyDown(object sender, KeyRoutedEventArgs e)
    {
        switch (e.Key)
        {
            case VirtualKey.E:
                ToggleEditMode();
                e.Handled = true;
                break;
            case VirtualKey.H:
                ToggleVertexHandles();
                e.Handled = true;
                break;
            case VirtualKey.G:
            case VirtualKey.W:
                ToggleMeshGrid();
                e.Handled = true;
                break;
            case VirtualKey.Number1:
                _mode = 0;
                _status = "Shading: OBJ material/texture child shader + UV checker + Lambert lighting.";
                e.Handled = true;
                break;
            case VirtualKey.Number2:
                _mode = 1;
                _status = "Shading: depth visualization.";
                e.Handled = true;
                break;
            case VirtualKey.Number3:
                _mode = 2;
                _status = "Shading: normal visualization.";
                e.Handled = true;
                break;
            case VirtualKey.F:
            case VirtualKey.R:
                ResetView();
                _status = "View reset to model bounds.";
                e.Handled = true;
                break;
            case VirtualKey.Delete:
            case VirtualKey.Back:
                if (_splatCloud is null && _selectedVertex >= 0)
                {
                    _document.Positions[_selectedVertex] = _document.OriginalPositions[_selectedVertex];
                    _document.RecomputeNormals();
                    _meshBatchesDirty = true;
                    _projectedMeshBatchesDirty = true;
                    _status = $"Reset vertex {_selectedVertex} to original position.";
                    e.Handled = true;
                }

                break;
        }
    }

    private void ReplaceDocument(MeshDocument document)
    {
        var old = _document;
        _document = document;
        _splatCloud = null;
        _meshBatchesDirty = true;
        _projectedMeshBatchesDirty = true;
        _selectedVertex = -1;
        DisposeMeshBatches();
        DisposeProjectedMeshBatches();
        old.Dispose();
    }

    private void ReplaceSplatCloud(GaussianSplatCloud cloud)
    {
        _splatCloud = cloud;
        _meshBatchesDirty = true;
        _projectedMeshBatchesDirty = true;
        _selectedVertex = -1;
        _editMode = false;
        DisposeMeshBatches();
        DisposeProjectedMeshBatches();
    }

    private void DrawScene(SKCanvas canvas, float width, float height)
    {
        EnsureSpecs();
        canvas.Clear(new SKColor(5, 9, 15));
        _camera = BuildCameraBasis();
        DrawReferenceGrid(canvas, width, height);
        if (_splatCloud is not null)
        {
            DrawGaussianSplats(canvas, width, height);
        }
        else if (ShouldUseDepthSortedProjectedPath)
        {
            DrawDepthSortedSubmittedMesh(canvas, width, height);
        }
        else
        {
            BuildSubmittedMesh(width, height);
            DrawSubmittedMesh(canvas, width, height);
        }

        if (_showVertexHandles || _editMode || _selectedVertex >= 0)
        {
            DrawVertexHandles(canvas, width, height);
        }

        DrawOverlay(canvas, width, height);
    }

    private bool ShouldUseDepthSortedProjectedPath
    {
        get
        {
            if (_mode >= 0.5f || _document.RequiresDepthSortedRendering)
            {
                return true;
            }

            return _document.Triangles.Count <= DepthSortedMaterialTriangleLimit;
        }
    }

    private bool IsLargeModelFastMaterialPath =>
        _mode < 0.5f &&
        !_document.RequiresDepthSortedRendering &&
        _document.Triangles.Count > DepthSortedMaterialTriangleLimit;

    private void EnsureSpecs()
    {
        if (_colorSpecification is not null &&
            _textureSpecification is not null &&
            _projectedColorSpecification is not null &&
            _projectedTextureSpecification is not null &&
            _splatSpecification is not null)
        {
            return;
        }

        using var colorSpace = SKColorSpace.CreateSrgb();
        if (_colorSpecification is null)
        {
            _colorSpecification = SKMeshSpecification.Make(
                Attributes,
                VertexStride,
                Varyings,
                VertexShader,
                FragmentShaderColor,
                colorSpace,
                SKAlphaType.Premul,
                out var colorErrors);

            if (_colorSpecification is null)
            {
                _status = string.IsNullOrWhiteSpace(colorErrors) ? "Color SKMeshSpecification.Make failed." : colorErrors;
            }
        }

        if (_textureSpecification is null)
        {
            _textureSpecification = SKMeshSpecification.Make(
                Attributes,
                VertexStride,
                Varyings,
                VertexShader,
                FragmentShaderTextured,
                colorSpace,
                SKAlphaType.Premul,
                out var textureErrors);

            if (_textureSpecification is null && _colorSpecification is not null)
            {
                _status = string.IsNullOrWhiteSpace(textureErrors) ? "Textured SKMeshSpecification.Make failed." : textureErrors;
            }
        }

        if (_projectedColorSpecification is null)
        {
            _projectedColorSpecification = SKMeshSpecification.Make(
                ProjectedAttributes,
                VertexStride,
                Varyings,
                ProjectedVertexShader,
                FragmentShaderColor,
                colorSpace,
                SKAlphaType.Premul,
                out var projectedColorErrors);

            if (_projectedColorSpecification is null && _colorSpecification is not null)
            {
                _status = string.IsNullOrWhiteSpace(projectedColorErrors)
                    ? "Projected color SKMeshSpecification.Make failed."
                    : projectedColorErrors;
            }
        }

        if (_projectedTextureSpecification is null)
        {
            _projectedTextureSpecification = SKMeshSpecification.Make(
                ProjectedAttributes,
                VertexStride,
                Varyings,
                ProjectedVertexShader,
                FragmentShaderTextured,
                colorSpace,
                SKAlphaType.Premul,
                out var projectedTextureErrors);

            if (_projectedTextureSpecification is null && _projectedColorSpecification is not null)
            {
                _status = string.IsNullOrWhiteSpace(projectedTextureErrors)
                    ? "Projected textured SKMeshSpecification.Make failed."
                    : projectedTextureErrors;
            }
        }

        if (_splatSpecification is null)
        {
            _splatSpecification = SKMeshSpecification.Make(
                SplatAttributes,
                SplatVertexStride,
                SplatVaryings,
                SplatVertexShader,
                SplatFragmentShader,
                colorSpace,
                SKAlphaType.Premul,
                out var splatErrors);

            if (_splatSpecification is null && _projectedColorSpecification is not null)
            {
                _status = string.IsNullOrWhiteSpace(splatErrors)
                    ? "Gaussian splat SKMeshSpecification.Make failed."
                    : splatErrors;
            }
        }
    }

    private void BuildSubmittedMesh(float width, float height)
    {
        if (!_meshBatchesDirty)
        {
            return;
        }

        DisposeMeshBatches();
        _submittedVertexCount = 0;
        _submittedIndexCount = 0;

        var batchVertexCount = 0;
        var batchIndexCount = 0;
        var batchMaterialIndex = -1;

        foreach (var triangle in _document.Triangles)
        {
            if (batchVertexCount > 0 &&
                (triangle.MaterialIndex != batchMaterialIndex ||
                 batchVertexCount + 3 > MaxSubmittedVertices ||
                 batchIndexCount + 3 > MaxSubmittedIndices))
            {
                AddMeshBatch(batchVertexCount, batchIndexCount, batchMaterialIndex);
                batchVertexCount = 0;
                batchIndexCount = 0;
            }

            if (batchVertexCount == 0)
            {
                batchMaterialIndex = triangle.MaterialIndex;
            }

            AppendTriangleToBatch(triangle, ref batchVertexCount, ref batchIndexCount);
        }

        if (batchVertexCount > 0)
        {
            AddMeshBatch(batchVertexCount, batchIndexCount, batchMaterialIndex);
        }

        _meshBatchesDirty = false;
    }

    private void DrawSubmittedMesh(SKCanvas canvas, float width, float height)
    {
        _drawCalls = 0;

        if (_colorSpecification is null || _meshBatches.Count == 0)
        {
            return;
        }

        foreach (var batch in _meshBatches)
        {
            if (!DrawMeshBatch(canvas, width, height, batch))
            {
                return;
            }
        }

        var fastPathText = IsLargeModelFastMaterialPath
            ? $" Large-model fast path is active over the {DepthSortedMaterialTriangleLimit:N0}-triangle projected-sort budget; material/source order is used instead of CPU depth sorting."
            : string.Empty;
        _status = $"Rendered {_document.Triangles.Count:N0} OBJ triangles through {_drawCalls:N0} cached world-space material SKMesh batch{(_drawCalls == 1 ? string.Empty : "es")}.{fastPathText}";
    }

    private void DrawGaussianSplats(SKCanvas canvas, float width, float height)
    {
        _drawCalls = 0;
        _submittedVertexCount = 0;
        _submittedIndexCount = 0;
        _splatDrawItems.Clear();

        var cloud = _splatCloud;
        if (cloud is null || _splatSpecification is null)
        {
            return;
        }

        foreach (var splat in cloud.Splats)
        {
            if (TryProjectSplat(splat, width, height, out var item))
            {
                _splatDrawItems.Add(item);
            }
        }

        if (_splatDrawItems.Count == 0)
        {
            _status = "No visible Gaussian splats after camera near-plane and viewport culling.";
            return;
        }

        _splatDrawItems.Sort(static (a, b) => b.Depth.CompareTo(a.Depth));

        var batchVertexCount = 0;
        var batchIndexCount = 0;
        foreach (var item in _splatDrawItems)
        {
            if (batchVertexCount > 0 &&
                (batchVertexCount + 4 > MaxSubmittedVertices || batchIndexCount + 6 > MaxSubmittedIndices))
            {
                if (!DrawSplatBatch(canvas, width, height, batchVertexCount, batchIndexCount))
                {
                    return;
                }

                batchVertexCount = 0;
                batchIndexCount = 0;
            }

            AppendSplatToBatch(item, ref batchVertexCount, ref batchIndexCount);
        }

        if (batchVertexCount > 0)
        {
            if (!DrawSplatBatch(canvas, width, height, batchVertexCount, batchIndexCount))
            {
                return;
            }
        }

        var renderedText = _splatDrawItems.Count == cloud.Splats.Length
            ? $"{cloud.Splats.Length:N0}"
            : $"{_splatDrawItems.Count:N0}/{cloud.Splats.Length:N0} visible";
        _status = $"Rendered {renderedText} Gaussian splats through {_drawCalls:N0} depth-sorted SKMesh quad batch{(_drawCalls == 1 ? string.Empty : "es")}.";
    }

    private bool TryProjectSplat(GaussianSplat splat, float width, float height, out SplatDrawItem item)
    {
        item = default;
        var rel = splat.Position - _camera.Position;
        var cx = Vec3.Dot(rel, _camera.Right);
        var cy = Vec3.Dot(rel, _camera.Up);
        var cz = Vec3.Dot(rel, _camera.Forward);
        if (cz <= NearPlane || splat.Alpha <= 0.003f)
        {
            return false;
        }

        var focal = MathF.Min(width, height) * 0.78f;
        var centerX = width * 0.5f + cx / cz * focal;
        var centerY = height * 0.5f - cy / cz * focal;

        ProjectAxisToCovariance(splat.Axis0, cx, cy, cz, focal, out var c00, out var c01, out var c11);
        AccumulateAxisToCovariance(splat.Axis1, cx, cy, cz, focal, ref c00, ref c01, ref c11);
        AccumulateAxisToCovariance(splat.Axis2, cx, cy, cz, focal, ref c00, ref c01, ref c11);
        c00 += 0.35f;
        c11 += 0.35f;

        ComputeEllipseAxes(c00, c01, c11, MathF.Min(width, height) * 0.22f, out var ax, out var ay, out var bx, out var by);
        var maxExtent = MathF.Max(MathF.Sqrt(ax * ax + ay * ay), MathF.Sqrt(bx * bx + by * by));
        if (maxExtent < 0.25f)
        {
            return false;
        }

        const float padding = 96.0f;
        if (centerX + maxExtent < -padding ||
            centerX - maxExtent > width + padding ||
            centerY + maxExtent < -padding ||
            centerY - maxExtent > height + padding)
        {
            return false;
        }

        item = new SplatDrawItem(centerX, centerY, ax, ay, bx, by, splat.ColorR, splat.ColorG, splat.ColorB, splat.Alpha, cz);
        return true;
    }

    private void ProjectAxisToCovariance(Vec3 axis, float cx, float cy, float cz, float focal, out float c00, out float c01, out float c11)
    {
        var ax = Vec3.Dot(axis, _camera.Right);
        var ay = Vec3.Dot(axis, _camera.Up);
        var az = Vec3.Dot(axis, _camera.Forward);
        var invZ = 1.0f / cz;
        var invZ2 = invZ * invZ;
        var sx = focal * (ax * invZ - cx * az * invZ2);
        var sy = focal * (-ay * invZ + cy * az * invZ2);
        c00 = sx * sx;
        c01 = sx * sy;
        c11 = sy * sy;
    }

    private void AccumulateAxisToCovariance(Vec3 axis, float cx, float cy, float cz, float focal, ref float c00, ref float c01, ref float c11)
    {
        ProjectAxisToCovariance(axis, cx, cy, cz, focal, out var a00, out var a01, out var a11);
        c00 += a00;
        c01 += a01;
        c11 += a11;
    }

    private static void ComputeEllipseAxes(float c00, float c01, float c11, float maxRadius, out float ax, out float ay, out float bx, out float by)
    {
        var trace = c00 + c11;
        var delta = MathF.Sqrt(MathF.Max(0.0f, (c00 - c11) * (c00 - c11) + 4.0f * c01 * c01));
        var lambda0 = Math.Clamp((trace + delta) * 0.5f, 0.04f, maxRadius * maxRadius);
        var lambda1 = Math.Clamp((trace - delta) * 0.5f, 0.04f, maxRadius * maxRadius);
        var vx = c01;
        var vy = lambda0 - c00;
        var length = MathF.Sqrt(vx * vx + vy * vy);
        if (length < 0.00001f)
        {
            vx = 1.0f;
            vy = 0.0f;
        }
        else
        {
            vx /= length;
            vy /= length;
        }

        var sigma0 = MathF.Sqrt(lambda0) * 3.0f;
        var sigma1 = MathF.Sqrt(lambda1) * 3.0f;
        ax = vx * sigma0;
        ay = vy * sigma0;
        bx = -vy * sigma1;
        by = vx * sigma1;
    }

    private void AppendSplatToBatch(SplatDrawItem item, ref int batchVertexCount, ref int batchIndexCount)
    {
        var baseIndex = (ushort)batchVertexCount;
        _splatVertices[batchVertexCount++] = CreateSplatVertex(item, -1, -1);
        _splatVertices[batchVertexCount++] = CreateSplatVertex(item, 1, -1);
        _splatVertices[batchVertexCount++] = CreateSplatVertex(item, 1, 1);
        _splatVertices[batchVertexCount++] = CreateSplatVertex(item, -1, 1);
        _submittedIndices[batchIndexCount++] = baseIndex;
        _submittedIndices[batchIndexCount++] = (ushort)(baseIndex + 1);
        _submittedIndices[batchIndexCount++] = (ushort)(baseIndex + 2);
        _submittedIndices[batchIndexCount++] = baseIndex;
        _submittedIndices[batchIndexCount++] = (ushort)(baseIndex + 2);
        _submittedIndices[batchIndexCount++] = (ushort)(baseIndex + 3);
    }

    private static SplatVertex CreateSplatVertex(SplatDrawItem item, float localX, float localY) =>
        new(
            item.CenterX + localX * item.AxisAX + localY * item.AxisBX,
            item.CenterY + localX * item.AxisAY + localY * item.AxisBY,
            localX,
            localY,
            item.ColorR,
            item.ColorG,
            item.ColorB,
            item.Alpha,
            item.Depth);

    private bool DrawSplatBatch(SKCanvas canvas, float width, float height, int vertexCount, int indexCount)
    {
        if (_splatSpecification is null)
        {
            _status = "Gaussian splat SKMeshSpecification.Make failed; cannot render splats.";
            return false;
        }

        FillUniforms(width, height, _document.GetMaterial(0));
        using var uniforms = SKData.CreateCopy(MemoryMarshal.AsBytes(_uniformData.AsSpan()));
        using var vertices = SKMeshVertexBuffer.Make(MemoryMarshal.AsBytes(_splatVertices.AsSpan(0, vertexCount)));
        using var indices = SKMeshIndexBuffer.Make(MemoryMarshal.AsBytes(_submittedIndices.AsSpan(0, indexCount)));
        using var mesh = SKMesh.MakeIndexed(
            _splatSpecification,
            SKMeshMode.Triangles,
            vertices,
            vertexCount,
            0,
            indices,
            indexCount,
            0,
            uniforms,
            new SKRect(-64, -64, width + 64, height + 64),
            out var errors);

        if (mesh is null || !mesh.IsValid)
        {
            _status = string.IsNullOrWhiteSpace(errors) ? "Gaussian splat SKMesh.MakeIndexed failed." : errors;
            return false;
        }

        canvas.DrawMesh(mesh, _meshPaint);
        _drawCalls++;
        _submittedVertexCount += vertexCount;
        _submittedIndexCount += indexCount;
        return true;
    }

    private void DrawDepthSortedSubmittedMesh(SKCanvas canvas, float width, float height)
    {
        _drawCalls = 0;
        _submittedVertexCount = 0;
        _submittedIndexCount = 0;

        if (!EnsureDepthSortedProjectedMeshBatches(width, height))
        {
            return;
        }

        foreach (var batch in _projectedMeshBatches)
        {
            if (!DrawProjectedMeshBatch(canvas, width, height, batch))
            {
                return;
            }
        }

        var renderedText = _projectedVisibleTriangleCount == _document.Triangles.Count
            ? $"{_document.Triangles.Count:N0}"
            : $"{_projectedVisibleTriangleCount:N0}/{_document.Triangles.Count:N0} visible";
        _status = $"Rendered {renderedText} triangles through {_drawCalls:N0} depth-sorted projected SKMesh batch{(_drawCalls == 1 ? string.Empty : "es")}.";
    }

    private bool EnsureDepthSortedProjectedMeshBatches(float width, float height)
    {
        if (ProjectedMeshBatchCacheMatches(width, height))
        {
            return _projectedMeshBatches.Count > 0;
        }

        DisposeProjectedMeshBatches();
        _projectedVisibleTriangleCount = 0;
        _projectedTriangles.Clear();

        foreach (var triangle in _document.Triangles)
        {
            if (TryProjectTriangle(triangle, width, height, out var projected))
            {
                _projectedTriangles.Add(projected);
            }
        }

        if (_projectedTriangles.Count == 0)
        {
            _status = "No visible mesh triangles after camera near-plane and viewport culling.";
            _projectedMeshBatchesDirty = false;
            StoreProjectedMeshBatchCacheKey(width, height);
            return false;
        }

        _projectedTriangles.Sort(static (a, b) => b.Depth.CompareTo(a.Depth));
        _projectedVisibleTriangleCount = _projectedTriangles.Count;

        var batchVertexCount = 0;
        var batchIndexCount = 0;
        var batchMaterialIndex = -1;

        foreach (var triangle in _projectedTriangles)
        {
            if (batchVertexCount > 0 &&
                (triangle.MaterialIndex != batchMaterialIndex ||
                 batchVertexCount + 3 > MaxSubmittedVertices ||
                 batchIndexCount + 3 > MaxSubmittedIndices))
            {
                AddProjectedMeshBatch(batchVertexCount, batchIndexCount, batchMaterialIndex);
                batchVertexCount = 0;
                batchIndexCount = 0;
            }

            if (batchVertexCount == 0)
            {
                batchMaterialIndex = triangle.MaterialIndex;
            }

            AppendProjectedTriangleToBatch(triangle, ref batchVertexCount, ref batchIndexCount);
        }

        if (batchVertexCount > 0)
        {
            AddProjectedMeshBatch(batchVertexCount, batchIndexCount, batchMaterialIndex);
        }

        _projectedTriangles.Clear();
        StoreProjectedMeshBatchCacheKey(width, height);
        _projectedMeshBatchesDirty = false;
        return _projectedMeshBatches.Count > 0;
    }

    private bool ProjectedPathUsesPerMaterialUniforms =>
        _mode < 0.5f && (_document.TextureMaterialCount > 0 || _document.RequiresDepthSortedRendering);

    private int ProjectedCacheModeBucket => _mode < 0.5f ? 0 : 1;

    private bool ProjectedMeshBatchCacheMatches(float width, float height)
    {
        const float epsilon = 0.0001f;
        return !_projectedMeshBatchesDirty &&
               MathF.Abs(_projectedCacheWidth - width) < 0.5f &&
               MathF.Abs(_projectedCacheHeight - height) < 0.5f &&
               MathF.Abs(_projectedCacheYaw - _yaw) < epsilon &&
               MathF.Abs(_projectedCachePitch - _pitch) < epsilon &&
               MathF.Abs(_projectedCacheDistance - _distance) < epsilon &&
               MathF.Abs(_projectedCacheTargetX - _target.X) < epsilon &&
               MathF.Abs(_projectedCacheTargetY - _target.Y) < epsilon &&
               MathF.Abs(_projectedCacheTargetZ - _target.Z) < epsilon &&
               _projectedCacheModeBucket == ProjectedCacheModeBucket &&
               _projectedCacheUsesPerMaterialUniforms == ProjectedPathUsesPerMaterialUniforms;
    }

    private void StoreProjectedMeshBatchCacheKey(float width, float height)
    {
        _projectedCacheWidth = width;
        _projectedCacheHeight = height;
        _projectedCacheYaw = _yaw;
        _projectedCachePitch = _pitch;
        _projectedCacheDistance = _distance;
        _projectedCacheTargetX = _target.X;
        _projectedCacheTargetY = _target.Y;
        _projectedCacheTargetZ = _target.Z;
        _projectedCacheModeBucket = ProjectedCacheModeBucket;
        _projectedCacheUsesPerMaterialUniforms = ProjectedPathUsesPerMaterialUniforms;
    }

    private bool TryProjectTriangle(Triangle triangle, float width, float height, out ProjectedTriangle projected)
    {
        projected = default;
        if (!TryProjectCorner(triangle.A, triangle.MaterialColor, width, height, out var a) ||
            !TryProjectCorner(triangle.B, triangle.MaterialColor, width, height, out var b) ||
            !TryProjectCorner(triangle.C, triangle.MaterialColor, width, height, out var c))
        {
            return false;
        }

        var area = (b.X - a.X) * (c.Y - a.Y) - (b.Y - a.Y) * (c.X - a.X);
        if (MathF.Abs(area) < 0.01f)
        {
            return false;
        }

        const float padding = 96.0f;
        var minX = MathF.Min(a.X, MathF.Min(b.X, c.X));
        var maxX = MathF.Max(a.X, MathF.Max(b.X, c.X));
        var minY = MathF.Min(a.Y, MathF.Min(b.Y, c.Y));
        var maxY = MathF.Max(a.Y, MathF.Max(b.Y, c.Y));
        if (maxX < -padding || minX > width + padding || maxY < -padding || minY > height + padding)
        {
            return false;
        }

        var materialIndex = ProjectedPathUsesPerMaterialUniforms ? triangle.MaterialIndex : 0;
        projected = new ProjectedTriangle(materialIndex, (a.Depth + b.Depth + c.Depth) / 3.0f, a, b, c);
        return true;
    }

    private bool TryProjectCorner(Corner corner, Vec3 materialColor, float width, float height, out ProjectedVertex vertex)
    {
        vertex = default;
        var position = _document.Positions[corner.PositionIndex];
        var rel = position - _camera.Position;
        var vx = Vec3.Dot(rel, _camera.Right);
        var vy = Vec3.Dot(rel, _camera.Up);
        var vz = Vec3.Dot(rel, _camera.Forward);
        if (vz <= NearPlane)
        {
            return false;
        }

        var normal = corner.HasNormal && _document.UseAuthoredNormals
            ? corner.Normal
            : _document.Normals[corner.PositionIndex];
        var viewNormal = new Vec3(
            Vec3.Dot(normal, _camera.Right),
            Vec3.Dot(normal, _camera.Up),
            Vec3.Dot(normal, _camera.Forward)).Normalized();
        var focal = MathF.Min(width, height) * 0.78f;
        vertex = new ProjectedVertex(
            width * 0.5f + vx / vz * focal,
            height * 0.5f - vy / vz * focal,
            corner.U,
            corner.V,
            viewNormal.X,
            viewNormal.Y,
            viewNormal.Z,
            vz,
            materialColor.X,
            materialColor.Y,
            materialColor.Z);
        return true;
    }

    private void AppendProjectedTriangleToBatch(ProjectedTriangle triangle, ref int batchVertexCount, ref int batchIndexCount)
    {
        var baseIndex = (ushort)batchVertexCount;

        _projectedVertices[batchVertexCount++] = triangle.A.WithBarycentric(1, 0, 0);
        _projectedVertices[batchVertexCount++] = triangle.B.WithBarycentric(0, 1, 0);
        _projectedVertices[batchVertexCount++] = triangle.C.WithBarycentric(0, 0, 1);
        _submittedIndices[batchIndexCount++] = baseIndex;
        _submittedIndices[batchIndexCount++] = (ushort)(baseIndex + 1);
        _submittedIndices[batchIndexCount++] = (ushort)(baseIndex + 2);
    }

    private void AddProjectedMeshBatch(int vertexCount, int indexCount, int materialIndex)
    {
        var vertices = SKMeshVertexBuffer.Make(MemoryMarshal.AsBytes(_projectedVertices.AsSpan(0, vertexCount)));
        var indices = SKMeshIndexBuffer.Make(MemoryMarshal.AsBytes(_submittedIndices.AsSpan(0, indexCount)));
        _projectedMeshBatches.Add(new MeshBatch(materialIndex, vertexCount, indexCount, vertices, indices));
    }

    private bool DrawProjectedMeshBatch(SKCanvas canvas, float width, float height, MeshBatch batch)
    {
        var material = _document.GetMaterial(batch.MaterialIndex);
        var specification = material.HasDiffuseTexture && _mode < 0.5f
            ? _projectedTextureSpecification
            : _projectedColorSpecification;
        if (specification is null)
        {
            _status = material.HasDiffuseTexture
                ? "Projected textured SKMeshSpecification.Make failed; cannot render material."
                : "Projected color SKMeshSpecification.Make failed; cannot render material.";
            return false;
        }

        FillUniforms(width, height, material);
        using var uniforms = SKData.CreateCopy(MemoryMarshal.AsBytes(_uniformData.AsSpan()));
        var bounds = new SKRect(-64, -64, width + 64, height + 64);
        string errors;
        SKMesh? mesh;
        if (material.HasDiffuseTexture && _mode < 0.5f)
        {
            var children = material.Children;
            mesh = SKMesh.MakeIndexed(
                specification,
                SKMeshMode.Triangles,
                batch.VertexBuffer,
                batch.VertexCount,
                0,
                batch.IndexBuffer,
                batch.IndexCount,
                0,
                uniforms,
                children,
                bounds,
                out errors);
        }
        else
        {
            mesh = SKMesh.MakeIndexed(
                specification,
                SKMeshMode.Triangles,
                batch.VertexBuffer,
                batch.VertexCount,
                0,
                batch.IndexBuffer,
                batch.IndexCount,
                0,
                uniforms,
                bounds,
                out errors);
        }

        if (mesh is null || !mesh.IsValid)
        {
            _status = string.IsNullOrWhiteSpace(errors) ? "Projected SKMesh.MakeIndexed failed." : errors;
            return false;
        }

        using (mesh)
        {
            canvas.DrawMesh(mesh, _meshPaint);
            _drawCalls++;
            _submittedVertexCount += batch.VertexCount;
            _submittedIndexCount += batch.IndexCount;
        }

        return true;
    }

    private void AppendTriangleToBatch(Triangle triangle, ref int batchVertexCount, ref int batchIndexCount)
    {
        var baseIndex = (ushort)batchVertexCount;

        _submittedVertices[batchVertexCount++] = CreateMeshVertex(triangle.A, triangle.MaterialColor, 1, 0, 0);
        _submittedVertices[batchVertexCount++] = CreateMeshVertex(triangle.B, triangle.MaterialColor, 0, 1, 0);
        _submittedVertices[batchVertexCount++] = CreateMeshVertex(triangle.C, triangle.MaterialColor, 0, 0, 1);
        _submittedIndices[batchIndexCount++] = baseIndex;
        _submittedIndices[batchIndexCount++] = (ushort)(baseIndex + 1);
        _submittedIndices[batchIndexCount++] = (ushort)(baseIndex + 2);
    }

    private MeshVertex CreateMeshVertex(Corner corner, Vec3 materialColor, float bx, float by, float bz)
    {
        var position = _document.Positions[corner.PositionIndex];
        var normal = corner.HasNormal && _document.UseAuthoredNormals
            ? corner.Normal
            : _document.Normals[corner.PositionIndex];
        return new MeshVertex(
            position.X,
            position.Y,
            position.Z,
            corner.U,
            corner.V,
            normal.X,
            normal.Y,
            normal.Z,
            materialColor.X,
            materialColor.Y,
            materialColor.Z,
            bx,
            by,
            bz);
    }

    private void AddMeshBatch(int vertexCount, int indexCount, int materialIndex)
    {
        var vertices = SKMeshVertexBuffer.Make(MemoryMarshal.AsBytes(_submittedVertices.AsSpan(0, vertexCount)));
        var indices = SKMeshIndexBuffer.Make(MemoryMarshal.AsBytes(_submittedIndices.AsSpan(0, indexCount)));
        _meshBatches.Add(new MeshBatch(materialIndex, vertexCount, indexCount, vertices, indices));
        _submittedVertexCount += vertexCount;
        _submittedIndexCount += indexCount;
    }

    private bool DrawMeshBatch(SKCanvas canvas, float width, float height, MeshBatch batch)
    {
        var material = _document.GetMaterial(batch.MaterialIndex);
        var specification = material.HasDiffuseTexture ? _textureSpecification : _colorSpecification;
        if (specification is null)
        {
            _status = material.HasDiffuseTexture
                ? "Textured SKMeshSpecification.Make failed; cannot render textured material."
                : "Color SKMeshSpecification.Make failed; cannot render material.";
            return false;
        }

        FillUniforms(width, height, material);
        using var uniforms = SKData.CreateCopy(MemoryMarshal.AsBytes(_uniformData.AsSpan()));
        var bounds = new SKRect(-64, -64, width + 64, height + 64);
        string errors;
        SKMesh? mesh;
        if (material.HasDiffuseTexture)
        {
            var children = material.Children;
            mesh = SKMesh.MakeIndexed(
                specification,
                SKMeshMode.Triangles,
                batch.VertexBuffer,
                batch.VertexCount,
                0,
                batch.IndexBuffer,
                batch.IndexCount,
                0,
                uniforms,
                children,
                bounds,
                out errors);
        }
        else
        {
            mesh = SKMesh.MakeIndexed(
                specification,
                SKMeshMode.Triangles,
                batch.VertexBuffer,
                batch.VertexCount,
                0,
                batch.IndexBuffer,
                batch.IndexCount,
                0,
                uniforms,
                bounds,
                out errors);
        }

        if (mesh is null || !mesh.IsValid)
        {
            _status = string.IsNullOrWhiteSpace(errors) ? "SKMesh.MakeIndexed failed." : errors;
            return false;
        }

        using (mesh)
        {
            canvas.DrawMesh(mesh, _meshPaint);
            _drawCalls++;
        }

        return true;
    }

    private void FillUniforms(float width, float height, MeshMaterial material)
    {
        _uniformData[0] = _showMeshGrid ? 1.0f : 0.0f;
        _uniformData[1] = width;
        _uniformData[2] = Math.Max(0.1f, _distance + _document.Radius);
        _uniformData[3] = _mode;
        _uniformData[4] = 0.42f;
        _uniformData[5] = 0.74f;
        _uniformData[6] = 0.52f;
        _uniformData[7] = Math.Max(NearPlane, _distance - _document.Radius * 1.4f);
        _uniformData[8] = Math.Max(1, material.TextureWidth);
        _uniformData[9] = Math.Max(1, material.TextureHeight);
        _uniformData[10] = material.HasDiffuseTexture ? 1.0f : 0.0f;
        _uniformData[11] = Math.Clamp(material.Alpha, 0.0f, 1.0f);
        var focal = MathF.Min(width, height) * 0.78f;
        _uniformData[12] = _camera.Position.X;
        _uniformData[13] = _camera.Position.Y;
        _uniformData[14] = _camera.Position.Z;
        _uniformData[15] = focal;
        _uniformData[16] = _camera.Right.X;
        _uniformData[17] = _camera.Right.Y;
        _uniformData[18] = _camera.Right.Z;
        _uniformData[19] = height;
        _uniformData[20] = _camera.Up.X;
        _uniformData[21] = _camera.Up.Y;
        _uniformData[22] = _camera.Up.Z;
        _uniformData[23] = _distance;
        _uniformData[24] = _camera.Forward.X;
        _uniformData[25] = _camera.Forward.Y;
        _uniformData[26] = _camera.Forward.Z;
        _uniformData[27] = _document.Radius;
    }

    private void DisposeMeshBatches()
    {
        foreach (var batch in _meshBatches)
        {
            batch.Dispose();
        }

        _meshBatches.Clear();
    }

    private void DisposeProjectedMeshBatches()
    {
        foreach (var batch in _projectedMeshBatches)
        {
            batch.Dispose();
        }

        _projectedMeshBatches.Clear();
    }

    private void DrawReferenceGrid(SKCanvas canvas, float width, float height)
    {
        var extent = MathF.Max(2, MathF.Ceiling(ActiveRadius * 1.8f));
        for (var i = -extent; i <= extent; i++)
        {
            DrawWorldLine(canvas, new Vec3(i, -extent, 0), new Vec3(i, extent, 0), _gridPaint, width, height);
            DrawWorldLine(canvas, new Vec3(-extent, i, 0), new Vec3(extent, i, 0), _gridPaint, width, height);
        }

        DrawWorldLine(canvas, new Vec3(0, 0, 0), new Vec3(extent, 0, 0), _axisXPaint, width, height);
        DrawWorldLine(canvas, new Vec3(0, 0, 0), new Vec3(0, extent, 0), _axisYPaint, width, height);
        DrawWorldLine(canvas, new Vec3(0, 0, 0), new Vec3(0, 0, extent), _axisZPaint, width, height);
    }

    private void DrawWorldLine(SKCanvas canvas, Vec3 a, Vec3 b, SKPaint paint, float width, float height)
    {
        if (ProjectPoint(a, width, height, out var pa) && ProjectPoint(b, width, height, out var pb))
        {
            canvas.DrawLine(pa.X, pa.Y, pb.X, pb.Y, paint);
        }
    }

    private bool ProjectPoint(Vec3 world, float width, float height, out Vec2 screen)
    {
        var rel = world - _camera.Position;
        var vx = Vec3.Dot(rel, _camera.Right);
        var vy = Vec3.Dot(rel, _camera.Up);
        var vz = Vec3.Dot(rel, _camera.Forward);
        if (vz <= NearPlane)
        {
            screen = default;
            return false;
        }

        var focal = MathF.Min(width, height) * 0.78f;
        screen = new Vec2(width * 0.5f + vx / vz * focal, height * 0.5f - vy / vz * focal);
        return true;
    }

    private void DrawVertexHandles(SKCanvas canvas, float width, float height)
    {
        if (_splatCloud is not null)
        {
            return;
        }

        var stride = Math.Max(1, _document.Positions.Count / 600);
        for (var i = 0; i < _document.Positions.Count; i += stride)
        {
            if (!ProjectPoint(_document.Positions[i], width, height, out var p))
            {
                continue;
            }

            var selected = i == _selectedVertex;
            if (!selected && !_showVertexHandles && !_editMode)
            {
                continue;
            }

            canvas.DrawCircle(p.X, p.Y, selected ? 5.5f : 2.3f, selected ? _selectedVertexPaint : _vertexPaint);
        }
    }

    private void DrawOverlay(SKCanvas canvas, float width, float height)
    {
        var mode = ShadingModeName;
        var edit = _editMode ? "EDIT" : "VIEW";
        var text = _splatCloud is not null
            ? $"{ActiveName} // {mode} // {edit} // splats {_splatCloud.Splats.Length:N0} // selected none"
            : $"{_document.Name} // {mode} // {edit} // vertices {_document.Positions.Count:N0} // triangles {_document.Triangles.Count:N0} // selected {(_selectedVertex >= 0 ? _selectedVertex.ToString(CultureInfo.InvariantCulture) : "none")}";
        canvas.DrawText(text, 18, height - 22, _textFont, _textPaint);
    }

    private int FindNearestVertex(float x, float y, float width, float height, float maxDistance)
    {
        if (_splatCloud is not null)
        {
            return -1;
        }

        var best = -1;
        var bestDistance = maxDistance * maxDistance;
        for (var i = 0; i < _document.Positions.Count; i++)
        {
            if (!ProjectPoint(_document.Positions[i], width, height, out var screen))
            {
                continue;
            }

            var dx = screen.X - x;
            var dy = screen.Y - y;
            var distance = dx * dx + dy * dy;
            if (distance < bestDistance)
            {
                bestDistance = distance;
                best = i;
            }
        }

        return best;
    }

    private void MoveSelectedVertex(float dx, float dy)
    {
        if (_splatCloud is not null || _selectedVertex < 0 || _selectedVertex >= _document.Positions.Count)
        {
            return;
        }

        var factor = _distance / MathF.Max(1.0f, MathF.Min(_lastWidth, _lastHeight) * 0.78f);
        var delta = _camera.Right * (dx * factor) - _camera.Up * (dy * factor);
        _document.Positions[_selectedVertex] += delta;
        _document.InvalidateAuthoredNormals();
        _document.RecomputeNormals();
        _meshBatchesDirty = true;
    }

    private void PanCamera(float dx, float dy)
    {
        var factor = _distance / MathF.Max(1.0f, MathF.Min(_lastWidth, _lastHeight) * 0.78f);
        _target -= _camera.Right * (dx * factor);
        _target += _camera.Up * (dy * factor);
    }

    private CameraBasis BuildCameraBasis()
    {
        var cp = MathF.Cos(_pitch);
        var forward = new Vec3(cp * MathF.Sin(_yaw), MathF.Sin(_pitch), cp * MathF.Cos(_yaw)).Normalized();
        var position = _target - forward * _distance;
        var right = Vec3.Cross(new Vec3(0, 1, 0), forward).Normalized();
        if (right.LengthSquared < 0.0001f)
        {
            right = new Vec3(1, 0, 0);
        }

        var up = Vec3.Cross(forward, right).Normalized();
        return new CameraBasis(position, forward, right, up);
    }

    private void ResetView()
    {
        if (_splatCloud is null)
        {
            _document.RecomputeBounds();
        }

        _target = ActiveCenter;
        _distance = Math.Clamp(ActiveRadius * 3.2f, 2.2f, 18.0f);
        _yaw = -0.58f;
        _pitch = 0.38f;
        _selectedVertex = -1;
    }

    private Vec3 ActiveCenter => _splatCloud?.Center ?? _document.Center;
    private float ActiveRadius => _splatCloud?.Radius ?? _document.Radius;
    private string ActiveName => _splatCloud?.Name ?? _document.Name;

    private string ShadingModeName => _splatCloud is not null
        ? "Gaussian Splats"
        : _mode switch
        {
            < 0.5f => "Material UV",
            < 1.5f => "Depth",
            _ => "Normals"
        };

    private void PublishStats(double renderMilliseconds)
    {
        _frameCount++;
        var now = _clock.Elapsed.TotalSeconds;
        var frameMilliseconds = _lastFrameSeconds <= 0 ? 0 : (now - _lastFrameSeconds) * 1000.0;
        _lastFrameSeconds = now;

        if (now - _lastStatsSeconds >= 0.25)
        {
            _framesPerSecond = _frameCount / (now - _lastStatsSeconds);
            _frameCount = 0;
            _lastStatsSeconds = now;
        }

        var splatCloud = _splatCloud;
        var meshVertexCount = splatCloud?.Splats.Length ?? _document.Positions.Count;
        var meshTriangleCount = splatCloud is null ? _document.Triangles.Count : 0;
        var backend = splatCloud is null
            ? "Uno SKCanvasElement + SkiaSharp v4 SKMesh"
            : "Uno SKCanvasElement + SkiaSharp v4 SKMesh Gaussian splats";

        StatsUpdated?.Invoke(this, new MeshModelerStats(
            frameMilliseconds,
            renderMilliseconds,
            _framesPerSecond,
            meshVertexCount,
            meshTriangleCount,
            _submittedVertexCount,
            _submittedIndexCount,
            _drawCalls,
            UniformFloatCount * sizeof(float),
            _selectedVertex,
            _yaw * 180.0 / Math.PI,
            _pitch * 180.0 / Math.PI,
            _distance,
            ShadingModeName,
            backend,
            _status));
    }

    private enum PointerAction
    {
        None,
        Orbit,
        Pan,
        EditVertex
    }

    private static readonly Vec3 DefaultMeshColor = new(0.70f, 0.78f, 0.82f);

    [StructLayout(LayoutKind.Sequential, Pack = 4)]
    private readonly record struct MeshVertex(
        float X,
        float Y,
        float Z,
        float U,
        float V,
        float Nx,
        float Ny,
        float Nz,
        float MaterialR,
        float MaterialG,
        float MaterialB,
        float BaryX,
        float BaryY,
        float BaryZ);

    [StructLayout(LayoutKind.Sequential, Pack = 4)]
    private readonly record struct ProjectedMeshVertex(
        float X,
        float Y,
        float U,
        float V,
        float Nx,
        float Ny,
        float Nz,
        float Depth,
        float MaterialR,
        float MaterialG,
        float MaterialB,
        float BaryX,
        float BaryY,
        float BaryZ);

    private readonly record struct ProjectedVertex(
        float X,
        float Y,
        float U,
        float V,
        float Nx,
        float Ny,
        float Nz,
        float Depth,
        float MaterialR,
        float MaterialG,
        float MaterialB)
    {
        public ProjectedMeshVertex WithBarycentric(float bx, float by, float bz) =>
            new(X, Y, U, V, Nx, Ny, Nz, Depth, MaterialR, MaterialG, MaterialB, bx, by, bz);
    }

    private readonly record struct ProjectedTriangle(
        int MaterialIndex,
        float Depth,
        ProjectedVertex A,
        ProjectedVertex B,
        ProjectedVertex C);

    [StructLayout(LayoutKind.Sequential, Pack = 4)]
    private readonly record struct SplatVertex(
        float X,
        float Y,
        float LocalX,
        float LocalY,
        float ColorR,
        float ColorG,
        float ColorB,
        float Alpha,
        float Depth);

    private readonly record struct SplatDrawItem(
        float CenterX,
        float CenterY,
        float AxisAX,
        float AxisAY,
        float AxisBX,
        float AxisBY,
        float ColorR,
        float ColorG,
        float ColorB,
        float Alpha,
        float Depth);

    private readonly record struct GaussianSplat(
        Vec3 Position,
        Vec3 Axis0,
        Vec3 Axis1,
        Vec3 Axis2,
        float ColorR,
        float ColorG,
        float ColorB,
        float Alpha);

    private readonly record struct CameraBasis(Vec3 Position, Vec3 Forward, Vec3 Right, Vec3 Up);
    private readonly record struct Vec2(float X, float Y);
    private readonly record struct Corner(int PositionIndex, float U, float V, Vec3 Normal, bool HasNormal)
    {
        public Corner(int positionIndex, float u, float v)
            : this(positionIndex, u, v, default, false)
        {
        }
    }

    private readonly record struct Triangle(Corner A, Corner B, Corner C, Vec3 MaterialColor, int MaterialIndex)
    {
        public Triangle(Corner a, Corner b, Corner c)
            : this(a, b, c, DefaultMeshColor, 0)
        {
        }

        public Triangle(Corner a, Corner b, Corner c, Vec3 materialColor)
            : this(a, b, c, materialColor, 0)
        {
        }
    }

    private struct Vec3
    {
        public float X;
        public float Y;
        public float Z;

        public Vec3(float x, float y, float z)
        {
            X = x;
            Y = y;
            Z = z;
        }

        public float LengthSquared => X * X + Y * Y + Z * Z;
        public float Length => MathF.Sqrt(LengthSquared);
        public Vec3 Normalized()
        {
            var length = Length;
            return length <= 0.000001f ? new Vec3(0, 0, 1) : this / length;
        }

        public static float Dot(Vec3 a, Vec3 b) => a.X * b.X + a.Y * b.Y + a.Z * b.Z;
        public static Vec3 Cross(Vec3 a, Vec3 b) => new(
            a.Y * b.Z - a.Z * b.Y,
            a.Z * b.X - a.X * b.Z,
            a.X * b.Y - a.Y * b.X);

        public static Vec3 operator +(Vec3 a, Vec3 b) => new(a.X + b.X, a.Y + b.Y, a.Z + b.Z);
        public static Vec3 operator -(Vec3 a, Vec3 b) => new(a.X - b.X, a.Y - b.Y, a.Z - b.Z);
        public static Vec3 operator *(Vec3 a, float b) => new(a.X * b, a.Y * b, a.Z * b);
        public static Vec3 operator /(Vec3 a, float b) => new(a.X / b, a.Y / b, a.Z / b);
    }

    private sealed class MeshBatch : IDisposable
    {
        public MeshBatch(int materialIndex, int vertexCount, int indexCount, SKMeshVertexBuffer vertexBuffer, SKMeshIndexBuffer indexBuffer)
        {
            MaterialIndex = materialIndex;
            VertexCount = vertexCount;
            IndexCount = indexCount;
            VertexBuffer = vertexBuffer;
            IndexBuffer = indexBuffer;
        }

        public int MaterialIndex { get; }
        public int VertexCount { get; }
        public int IndexCount { get; }
        public SKMeshVertexBuffer VertexBuffer { get; }
        public SKMeshIndexBuffer IndexBuffer { get; }

        public void Dispose()
        {
            VertexBuffer.Dispose();
            IndexBuffer.Dispose();
        }
    }

    private sealed class MeshMaterial : IDisposable
    {
        private SKImage? _diffuseImage;
        private SKShader? _diffuseShader;
        private SKRuntimeEffectChild[]? _children;

        public MeshMaterial(string name, Vec3 diffuse)
        {
            Name = string.IsNullOrWhiteSpace(name) ? "default" : name;
            Diffuse = diffuse;
            Ambient = diffuse * 0.35f;
            Specular = new Vec3(0.20f, 0.20f, 0.20f);
            Emission = new Vec3(0, 0, 0);
            Alpha = 1.0f;
            Shininess = 16.0f;
        }

        public string Name { get; }
        public Vec3 Diffuse { get; set; }
        public Vec3 Ambient { get; set; }
        public Vec3 Specular { get; set; }
        public Vec3 Emission { get; set; }
        public float Alpha { get; set; }
        public float Shininess { get; set; }
        public string? DiffuseTexturePath { get; set; }
        public bool HasDiffuseTexture => _diffuseImage is { Width: > 0, Height: > 0 };
        public int TextureWidth => HasDiffuseTexture ? _diffuseImage!.Width : 1;
        public int TextureHeight => HasDiffuseTexture ? _diffuseImage!.Height : 1;
        public bool RequiresDepthSortedRendering => Alpha < 0.999f;

        public ReadOnlySpan<SKRuntimeEffectChild> Children
        {
            get
            {
                EnsureShader();
                return _children!;
            }
        }

        public void TryLoadDiffuseTexture()
        {
            if (string.IsNullOrWhiteSpace(DiffuseTexturePath) || !File.Exists(DiffuseTexturePath))
            {
                return;
            }

            using var encoded = SKData.Create(DiffuseTexturePath);
            var image = encoded is null ? null : SKImage.FromEncodedData(encoded);
            if (image is null || image.Width <= 0 || image.Height <= 0)
            {
                image?.Dispose();
                return;
            }

            _children = null;
            _diffuseShader?.Dispose();
            _diffuseShader = null;
            _diffuseImage?.Dispose();
            _diffuseImage = image;
        }

        public void Dispose()
        {
            _diffuseShader?.Dispose();
            _diffuseImage?.Dispose();
            _diffuseShader = null;
            _diffuseImage = null;
            _children = null;
        }

        private void EnsureShader()
        {
            if (_children is not null)
            {
                return;
            }

            if (HasDiffuseTexture)
            {
                _diffuseShader ??= _diffuseImage!.ToShader(
                    SKShaderTileMode.Repeat,
                    SKShaderTileMode.Repeat,
                    new SKSamplingOptions(SKFilterMode.Linear, SKMipmapMode.Linear));
            }
            else
            {
                _diffuseShader ??= SKShader.CreateColor(ToColor(Diffuse, Alpha));
            }

            _children = new[] { new SKRuntimeEffectChild(_diffuseShader!) };
        }

        private static SKColor ToColor(Vec3 color, float alpha)
            => new(
                ToByte(color.X),
                ToByte(color.Y),
                ToByte(color.Z),
                ToByte(alpha));

        private static byte ToByte(float value)
            => (byte)Math.Clamp((int)MathF.Round(value * 255.0f), 0, 255);
    }

    private sealed class GaussianSplatCloud
    {
        private GaussianSplatCloud(string name, GaussianSplat[] splats, string sourceFormat)
        {
            Name = name;
            SourceFormat = sourceFormat;
            Splats = NormalizeSplats(splats, out var radius);
            Center = new Vec3(0, 0, 0);
            Radius = radius;
        }

        public string Name { get; }
        public string SourceFormat { get; }
        public GaussianSplat[] Splats { get; }
        public Vec3 Center { get; }
        public float Radius { get; }

        public static GaussianSplatCloud CreateSample()
        {
            const int count = 3200;
            var random = new Random(2741);
            var splats = new GaussianSplat[count];
            for (var i = 0; i < count; i++)
            {
                var t = i / (float)(count - 1);
                var angle = t * MathF.Tau * 7.0f;
                var radius = 0.25f + t * 2.6f;
                var y = MathF.Sin(t * MathF.Tau * 3.0f) * 0.32f + (NextFloat(random) - 0.5f) * 0.18f;
                var position = new Vec3(MathF.Cos(angle) * radius, y, MathF.Sin(angle) * radius);
                var tangent = new Vec3(-MathF.Sin(angle), 0.12f, MathF.Cos(angle)).Normalized();
                var normal = new Vec3(MathF.Cos(angle), 0.0f, MathF.Sin(angle)).Normalized();
                var up = Vec3.Cross(tangent, normal).Normalized();
                var size = 0.025f + MathF.Pow(NextFloat(random), 2.0f) * 0.075f;
                var axis0 = tangent * (size * 1.8f);
                var axis1 = up * (size * 0.75f);
                var axis2 = normal * (size * 1.15f);
                var hue = t * 0.82f + 0.10f;
                var color = HsvToRgb(hue, 0.72f, 1.0f);
                var alpha = 0.18f + NextFloat(random) * 0.34f;
                splats[i] = new GaussianSplat(position, axis0, axis1, axis2, color.X, color.Y, color.Z, alpha);
            }

            return new GaussianSplatCloud("Procedural Gaussian splat spiral", splats, "procedural");
        }

        public static GaussianSplatCloud LoadPly(string path, string name)
        {
            using var stream = File.OpenRead(path);
            var header = ReadHeader(stream);
            if (header.VertexCount <= 0)
            {
                throw new InvalidOperationException("PLY does not contain a vertex element.");
            }

            var splats = header.Format switch
            {
                "ascii" => ReadAsciiSplats(stream, header),
                "binary_little_endian" => ReadBinarySplats(stream, header),
                _ => throw new NotSupportedException($"PLY format '{header.Format}' is not supported. Supported formats: ascii, binary_little_endian.")
            };

            if (splats.Count == 0)
            {
                throw new InvalidOperationException("PLY did not contain any readable Gaussian splats.");
            }

            return new GaussianSplatCloud(name, splats.ToArray(), header.Format);
        }

        private static GaussianSplat[] NormalizeSplats(GaussianSplat[] splats, out float radius)
        {
            if (splats.Length == 0)
            {
                radius = 1.0f;
                return splats;
            }

            var min = splats[0].Position;
            var max = splats[0].Position;
            foreach (var splat in splats)
            {
                ExpandSplatBounds(splat.Position, ref min, ref max);
            }

            var center = (min + max) * 0.5f;
            radius = 0.001f;
            foreach (var splat in splats)
            {
                radius = MathF.Max(radius, (splat.Position - center).Length);
            }

            var scale = radius <= 0.0001f ? 1.0f : 1.65f / radius;
            var normalized = new GaussianSplat[splats.Length];
            radius = 0.001f;
            for (var i = 0; i < splats.Length; i++)
            {
                var splat = splats[i];
                var position = (splat.Position - center) * scale;
                normalized[i] = splat with
                {
                    Position = position,
                    Axis0 = splat.Axis0 * scale,
                    Axis1 = splat.Axis1 * scale,
                    Axis2 = splat.Axis2 * scale
                };
                radius = MathF.Max(radius, position.Length);
            }

            return normalized;
        }

        private static void ExpandSplatBounds(Vec3 point, ref Vec3 min, ref Vec3 max)
        {
            min = new Vec3(MathF.Min(min.X, point.X), MathF.Min(min.Y, point.Y), MathF.Min(min.Z, point.Z));
            max = new Vec3(MathF.Max(max.X, point.X), MathF.Max(max.Y, point.Y), MathF.Max(max.Z, point.Z));
        }

        private static PlyHeader ReadHeader(Stream stream)
        {
            var lines = new List<string>(64);
            var bytes = new List<byte>(128);
            while (true)
            {
                var value = stream.ReadByte();
                if (value < 0)
                {
                    throw new InvalidDataException("Unexpected end of PLY header.");
                }

                if (value == '\n')
                {
                    var line = Encoding.ASCII.GetString(bytes.ToArray()).TrimEnd('\r');
                    lines.Add(line);
                    bytes.Clear();
                    if (line == "end_header")
                    {
                        break;
                    }
                }
                else
                {
                    bytes.Add((byte)value);
                }
            }

            if (lines.Count == 0 || lines[0] != "ply")
            {
                throw new InvalidDataException("File is not a PLY file.");
            }

            var format = string.Empty;
            var currentElement = string.Empty;
            var vertexCount = 0;
            var vertexProperties = new List<PlyProperty>(64);
            foreach (var line in lines)
            {
                var parts = SplitWhitespace(line);
                if (parts.Length == 0)
                {
                    continue;
                }

                switch (parts[0])
                {
                    case "format" when parts.Length >= 2:
                        format = parts[1];
                        break;
                    case "element" when parts.Length >= 3:
                        currentElement = parts[1];
                        if (currentElement == "vertex")
                        {
                            vertexCount = int.Parse(parts[2], NumberStyles.Integer, CultureInfo.InvariantCulture);
                        }

                        break;
                    case "property" when currentElement == "vertex" && parts.Length >= 3 && parts[1] != "list":
                        vertexProperties.Add(new PlyProperty(parts[2], parts[1]));
                        break;
                }
            }

            return new PlyHeader(format, vertexCount, vertexProperties.ToArray());
        }

        private static List<GaussianSplat> ReadAsciiSplats(Stream stream, PlyHeader header)
        {
            var splats = new List<GaussianSplat>(header.VertexCount);
            using var reader = new StreamReader(stream, Encoding.ASCII, detectEncodingFromByteOrderMarks: false, bufferSize: 1 << 16, leaveOpen: true);
            var values = new double[header.Properties.Length];
            var layout = new PlyLayout(header.Properties);
            for (var i = 0; i < header.VertexCount; i++)
            {
                var line = reader.ReadLine();
                if (line is null)
                {
                    break;
                }

                var parts = SplitWhitespace(line);
                if (parts.Length < header.Properties.Length)
                {
                    continue;
                }

                for (var p = 0; p < header.Properties.Length; p++)
                {
                    values[p] = double.Parse(parts[p], NumberStyles.Float, CultureInfo.InvariantCulture);
                }

                if (TryCreateSplat(layout, values, out var splat))
                {
                    splats.Add(splat);
                }
            }

            return splats;
        }

        private static List<GaussianSplat> ReadBinarySplats(Stream stream, PlyHeader header)
        {
            var splats = new List<GaussianSplat>(header.VertexCount);
            using var reader = new BinaryReader(stream, Encoding.ASCII, leaveOpen: true);
            var values = new double[header.Properties.Length];
            var layout = new PlyLayout(header.Properties);
            for (var i = 0; i < header.VertexCount; i++)
            {
                for (var p = 0; p < header.Properties.Length; p++)
                {
                    values[p] = ReadBinaryScalar(reader, header.Properties[p].Type);
                }

                if (TryCreateSplat(layout, values, out var splat))
                {
                    splats.Add(splat);
                }
            }

            return splats;
        }

        private static bool TryCreateSplat(PlyLayout layout, double[] values, out GaussianSplat splat)
        {
            splat = default;
            if (!layout.TryGet(values, "x", out var x) ||
                !layout.TryGet(values, "y", out var y) ||
                !layout.TryGet(values, "z", out var z))
            {
                return false;
            }

            var position = new Vec3((float)x, (float)y, (float)z);
            var color = ReadSplatColor(layout, values);
            var alpha = ReadSplatAlpha(layout, values);
            if (alpha <= 0.003f)
            {
                return false;
            }

            var scale = ReadSplatScale(layout, values);
            ReadSplatRotation(layout, values, out var qw, out var qx, out var qy, out var qz);
            QuaternionToAxes(qw, qx, qy, qz, out var basis0, out var basis1, out var basis2);
            splat = new GaussianSplat(
                position,
                basis0 * scale.X,
                basis1 * scale.Y,
                basis2 * scale.Z,
                color.X,
                color.Y,
                color.Z,
                alpha);
            return true;
        }

        private static Vec3 ReadSplatColor(PlyLayout layout, double[] values)
        {
            if (layout.TryGet(values, "f_dc_0", out var dc0) &&
                layout.TryGet(values, "f_dc_1", out var dc1) &&
                layout.TryGet(values, "f_dc_2", out var dc2))
            {
                return new Vec3(
                    Math.Clamp(0.5f + SphericalHarmonicsC0 * (float)dc0, 0.0f, 1.0f),
                    Math.Clamp(0.5f + SphericalHarmonicsC0 * (float)dc1, 0.0f, 1.0f),
                    Math.Clamp(0.5f + SphericalHarmonicsC0 * (float)dc2, 0.0f, 1.0f));
            }

            var r = ReadColorChannel(layout, values, "red", "r", fallback: 0.85f);
            var g = ReadColorChannel(layout, values, "green", "g", fallback: 0.90f);
            var b = ReadColorChannel(layout, values, "blue", "b", fallback: 1.0f);
            return new Vec3(r, g, b);
        }

        private static float ReadSplatAlpha(PlyLayout layout, double[] values)
        {
            if (layout.TryGet(values, "opacity", out var opacity))
            {
                return Sigmoid((float)opacity);
            }

            if (layout.TryGet(values, "alpha", out var alpha))
            {
                return NormalizeColorChannel((float)alpha);
            }

            return 0.45f;
        }

        private static Vec3 ReadSplatScale(PlyLayout layout, double[] values)
        {
            if (layout.TryGet(values, "scale_0", out var s0) &&
                layout.TryGet(values, "scale_1", out var s1) &&
                layout.TryGet(values, "scale_2", out var s2))
            {
                return new Vec3(LogScaleToRadius((float)s0), LogScaleToRadius((float)s1), LogScaleToRadius((float)s2));
            }

            var sx = ReadDirectScale(layout, values, "scale_x", "sx", 0.025f);
            var sy = ReadDirectScale(layout, values, "scale_y", "sy", sx);
            var sz = ReadDirectScale(layout, values, "scale_z", "sz", sy);
            return new Vec3(sx, sy, sz);
        }

        private static void ReadSplatRotation(PlyLayout layout, double[] values, out float qw, out float qx, out float qy, out float qz)
        {
            qw = 1.0f;
            qx = 0.0f;
            qy = 0.0f;
            qz = 0.0f;
            if (layout.TryGet(values, "rot_0", out var r0) &&
                layout.TryGet(values, "rot_1", out var r1) &&
                layout.TryGet(values, "rot_2", out var r2) &&
                layout.TryGet(values, "rot_3", out var r3))
            {
                qw = (float)r0;
                qx = (float)r1;
                qy = (float)r2;
                qz = (float)r3;
            }
            else
            {
                layout.TryGet(values, "qw", out var qwv);
                layout.TryGet(values, "qx", out var qxv);
                layout.TryGet(values, "qy", out var qyv);
                layout.TryGet(values, "qz", out var qzv);
                qw = qwv == 0 ? 1.0f : (float)qwv;
                qx = (float)qxv;
                qy = (float)qyv;
                qz = (float)qzv;
            }

            var length = MathF.Sqrt(qw * qw + qx * qx + qy * qy + qz * qz);
            if (length <= 0.00001f)
            {
                qw = 1.0f;
                qx = qy = qz = 0.0f;
                return;
            }

            qw /= length;
            qx /= length;
            qy /= length;
            qz /= length;
        }

        private static void QuaternionToAxes(float w, float x, float y, float z, out Vec3 axis0, out Vec3 axis1, out Vec3 axis2)
        {
            var xx = x * x;
            var yy = y * y;
            var zz = z * z;
            var xy = x * y;
            var xz = x * z;
            var yz = y * z;
            var wx = w * x;
            var wy = w * y;
            var wz = w * z;
            axis0 = new Vec3(1.0f - 2.0f * (yy + zz), 2.0f * (xy + wz), 2.0f * (xz - wy));
            axis1 = new Vec3(2.0f * (xy - wz), 1.0f - 2.0f * (xx + zz), 2.0f * (yz + wx));
            axis2 = new Vec3(2.0f * (xz + wy), 2.0f * (yz - wx), 1.0f - 2.0f * (xx + yy));
        }

        private static double ReadBinaryScalar(BinaryReader reader, string type) => type switch
        {
            "char" or "int8" => reader.ReadSByte(),
            "uchar" or "uint8" => reader.ReadByte(),
            "short" or "int16" => reader.ReadInt16(),
            "ushort" or "uint16" => reader.ReadUInt16(),
            "int" or "int32" => reader.ReadInt32(),
            "uint" or "uint32" => reader.ReadUInt32(),
            "float" or "float32" => reader.ReadSingle(),
            "double" or "float64" => reader.ReadDouble(),
            _ => throw new NotSupportedException($"PLY scalar type '{type}' is not supported.")
        };

        private static float ReadColorChannel(PlyLayout layout, double[] values, string name, string shortName, float fallback)
        {
            if (layout.TryGet(values, name, out var value) || layout.TryGet(values, shortName, out value))
            {
                return NormalizeColorChannel((float)value);
            }

            return fallback;
        }

        private static float NormalizeColorChannel(float value) => value > 1.0f ? Math.Clamp(value / 255.0f, 0.0f, 1.0f) : Math.Clamp(value, 0.0f, 1.0f);
        private static float LogScaleToRadius(float value) => MathF.Exp(Math.Clamp(value, -12.0f, 4.0f));
        private static float ReadDirectScale(PlyLayout layout, double[] values, string name, string shortName, float fallback)
            => layout.TryGet(values, name, out var value) || layout.TryGet(values, shortName, out value)
                ? MathF.Max(0.00001f, (float)value)
                : fallback;

        private static float Sigmoid(float value)
        {
            if (value >= 0)
            {
                var z = MathF.Exp(-value);
                return 1.0f / (1.0f + z);
            }

            var ez = MathF.Exp(value);
            return ez / (1.0f + ez);
        }

        private static string[] SplitWhitespace(string line) => line.Split(new[] { ' ', '\t' }, StringSplitOptions.RemoveEmptyEntries);
        private static float NextFloat(Random random) => (float)random.NextDouble();

        private static Vec3 HsvToRgb(float h, float s, float v)
        {
            h -= MathF.Floor(h);
            var c = v * s;
            var x = c * (1.0f - MathF.Abs(h * 6.0f % 2.0f - 1.0f));
            var m = v - c;
            var sector = (int)MathF.Floor(h * 6.0f);
            var rgb = sector switch
            {
                0 => new Vec3(c, x, 0),
                1 => new Vec3(x, c, 0),
                2 => new Vec3(0, c, x),
                3 => new Vec3(0, x, c),
                4 => new Vec3(x, 0, c),
                _ => new Vec3(c, 0, x)
            };

            return new Vec3(rgb.X + m, rgb.Y + m, rgb.Z + m);
        }

        private readonly record struct PlyHeader(string Format, int VertexCount, PlyProperty[] Properties);
        private readonly record struct PlyProperty(string Name, string Type);

        private sealed class PlyLayout
        {
            private readonly Dictionary<string, int> _indices;

            public PlyLayout(PlyProperty[] properties)
            {
                _indices = new Dictionary<string, int>(properties.Length, StringComparer.OrdinalIgnoreCase);
                for (var i = 0; i < properties.Length; i++)
                {
                    _indices[properties[i].Name] = i;
                }
            }

            public bool TryGet(double[] values, string name, out double value)
            {
                if (_indices.TryGetValue(name, out var index) && index >= 0 && index < values.Length)
                {
                    value = values[index];
                    return true;
                }

                value = 0;
                return false;
            }
        }
    }

    private sealed class MeshDocument : IDisposable
    {
        private readonly Dictionary<string, int> _materialIndices = new(StringComparer.OrdinalIgnoreCase);

        private MeshDocument()
        {
            Materials.Add(new MeshMaterial("default", DefaultMeshColor));
            _materialIndices[string.Empty] = 0;
        }

        public string Name { get; private set; } = "Untitled";
        public List<Vec3> Positions { get; } = new();
        public List<Vec3> OriginalPositions { get; } = new();
        public List<Vec3> Normals { get; } = new();
        public List<Triangle> Triangles { get; } = new();
        public List<MeshMaterial> Materials { get; } = new();
        public Vec3 Center { get; private set; }
        public float Radius { get; private set; } = 1.0f;
        public int MaterialCount => Materials.Count;
        public int TextureMaterialCount
        {
            get
            {
                var count = 0;
                foreach (var material in Materials)
                {
                    if (material.HasDiffuseTexture)
                    {
                        count++;
                    }
                }

                return count;
            }
        }

        public bool RequiresDepthSortedRendering
        {
            get
            {
                foreach (var material in Materials)
                {
                    if (material.RequiresDepthSortedRendering)
                    {
                        return true;
                    }
                }

                return false;
            }
        }

        public int SkippedFaceCount { get; private set; }
        public int SkippedSceneHelperFaceCount { get; private set; }
        public bool UseAuthoredNormals { get; private set; }

        public MeshMaterial GetMaterial(int index)
            => index >= 0 && index < Materials.Count ? Materials[index] : Materials[0];

        public void Dispose()
        {
            foreach (var material in Materials)
            {
                material.Dispose();
            }
        }

        public static MeshDocument CreateTorus()
        {
            var doc = new MeshDocument { Name = "Procedural UV torus" };
            const int majorSegments = 48;
            const int minorSegments = 16;
            const float majorRadius = 1.05f;
            const float minorRadius = 0.34f;

            for (var y = 0; y < minorSegments; y++)
            {
                var v = y / (float)minorSegments;
                var minor = v * MathF.Tau;
                var ringRadius = majorRadius + MathF.Cos(minor) * minorRadius;
                var py = MathF.Sin(minor) * minorRadius;

                for (var x = 0; x < majorSegments; x++)
                {
                    var u = x / (float)majorSegments;
                    var major = u * MathF.Tau;
                    doc.Positions.Add(new Vec3(MathF.Cos(major) * ringRadius, py, MathF.Sin(major) * ringRadius));
                }
            }

            for (var y = 0; y < minorSegments; y++)
            {
                var y1 = (y + 1) % minorSegments;
                for (var x = 0; x < majorSegments; x++)
                {
                    var x1 = (x + 1) % majorSegments;
                    var a = y * majorSegments + x;
                    var b = y * majorSegments + x1;
                    var c = y1 * majorSegments + x1;
                    var d = y1 * majorSegments + x;
                    var u0 = x / (float)majorSegments;
                    var u1 = (x + 1) / (float)majorSegments;
                    var v0 = y / (float)minorSegments;
                    var v1 = (y + 1) / (float)minorSegments;
                    doc.Triangles.Add(new Triangle(new Corner(a, u0, v0), new Corner(b, u1, v0), new Corner(c, u1, v1), DefaultMeshColor));
                    doc.Triangles.Add(new Triangle(new Corner(a, u0, v0), new Corner(c, u1, v1), new Corner(d, u0, v1), DefaultMeshColor));
                }
            }

            doc.FinalizeDocument(normalize: false);
            return doc;
        }

        public static MeshDocument ParseObj(string text, string name, string? baseDirectory = null)
        {
            var doc = new MeshDocument { Name = name };
            var uvs = new List<Vec2>();
            var authoredNormals = new List<Vec3>();
            var faceCorners = new List<Corner>(8);
            var materialDefinitions = new Dictionary<string, MeshMaterial>(StringComparer.OrdinalIgnoreCase);
            var currentMaterialIndex = 0;
            var currentMaterialColor = doc.Materials[0].Diffuse;
            var sawAuthoredNormal = false;
            var culture = CultureInfo.InvariantCulture;

            foreach (var raw in EnumerateLogicalObjLines(text))
            {
                var line = StripInlineComment(raw).Trim();
                if (line.Length == 0 || line[0] == '#')
                {
                    continue;
                }

                var parts = SplitWhitespace(line);
                if (parts.Length == 0)
                {
                    continue;
                }

                switch (parts[0])
                {
                    case "v" when parts.Length >= 4:
                        doc.Positions.Add(new Vec3(Parse(parts[1]), Parse(parts[2]), Parse(parts[3])));
                        break;
                    case "vt" when parts.Length >= 3:
                        uvs.Add(new Vec2(Parse(parts[1]), 1.0f - Parse(parts[2])));
                        break;
                    case "vn" when parts.Length >= 4:
                        authoredNormals.Add(new Vec3(Parse(parts[1]), Parse(parts[2]), Parse(parts[3])).Normalized());
                        break;
                    case "mtllib":
                        LoadMaterialLibraries(RestAfterKeyword(line, "mtllib"), baseDirectory, materialDefinitions);
                        break;
                    case "usemtl":
                        currentMaterialIndex = doc.ResolveMaterialIndex(RestAfterKeyword(line, "usemtl"), materialDefinitions);
                        currentMaterialColor = doc.Materials[currentMaterialIndex].Diffuse;
                        break;
                    case "f" when parts.Length >= 4:
                        var skipSceneHelper = IsSceneHelperMaterial(doc.GetMaterial(currentMaterialIndex).Name);
                        faceCorners.Clear();
                        for (var i = 1; i < parts.Length; i++)
                        {
                            if (!TryParseCorner(parts[i], doc.Positions.Count, uvs, authoredNormals, out var corner))
                            {
                                faceCorners.Clear();
                                doc.SkippedFaceCount++;
                                break;
                            }

                            sawAuthoredNormal |= corner.HasNormal;
                            faceCorners.Add(corner);
                        }

                        if (skipSceneHelper)
                        {
                            doc.SkippedSceneHelperFaceCount++;
                            break;
                        }

                        for (var i = 1; i < faceCorners.Count - 1; i++)
                        {
                            doc.Triangles.Add(new Triangle(faceCorners[0], faceCorners[i], faceCorners[i + 1], currentMaterialColor, currentMaterialIndex));
                        }

                        break;
                    case "f":
                        doc.SkippedFaceCount++;
                        break;
                }
            }

            if (doc.Positions.Count == 0 || doc.Triangles.Count == 0)
            {
                throw new InvalidOperationException("OBJ did not contain any triangulatable faces.");
            }

            doc.UseAuthoredNormals = sawAuthoredNormal;
            doc.FinalizeDocument(normalize: true);
            return doc;

            float Parse(string value) => float.Parse(value, NumberStyles.Float, culture);
        }

        private static bool TryParseCorner(string token, int positionCount, List<Vec2> uvs, List<Vec3> authoredNormals, out Corner corner)
        {
            corner = default;
            var pieces = token.Split('/');
            if (pieces.Length == 0 || !TryParseObjIndex(pieces[0], positionCount, out var positionIndex))
            {
                return false;
            }

            var uv = DefaultUv(positionIndex, positionCount);
            if (pieces.Length > 1 &&
                pieces[1].Length > 0 &&
                uvs.Count > 0 &&
                TryParseObjIndex(pieces[1], uvs.Count, out var uvIndex))
            {
                uv = uvs[uvIndex];
            }

            var normal = default(Vec3);
            var hasNormal = false;
            if (pieces.Length > 2 &&
                pieces[2].Length > 0 &&
                authoredNormals.Count > 0 &&
                TryParseObjIndex(pieces[2], authoredNormals.Count, out var normalIndex))
            {
                normal = authoredNormals[normalIndex];
                hasNormal = true;
            }

            corner = new Corner(positionIndex, uv.X, uv.Y, normal, hasNormal);
            return true;
        }

        private static bool TryParseObjIndex(string token, int count, out int resolvedIndex)
        {
            resolvedIndex = -1;
            if (!int.TryParse(token, NumberStyles.Integer, CultureInfo.InvariantCulture, out var index) || index == 0)
            {
                return false;
            }

            resolvedIndex = index < 0 ? count + index : index - 1;
            return resolvedIndex >= 0 && resolvedIndex < count;
        }

        private static IEnumerable<string> EnumerateLogicalObjLines(string text)
        {
            using var reader = new StringReader(text);
            var pending = string.Empty;
            string? raw;
            while ((raw = reader.ReadLine()) is not null)
            {
                var line = raw.TrimEnd();
                if (line.EndsWith("\\", StringComparison.Ordinal))
                {
                    pending += line.Substring(0, line.Length - 1) + " ";
                    continue;
                }

                yield return pending + line;
                pending = string.Empty;
            }

            if (pending.Length > 0)
            {
                yield return pending;
            }
        }

        private static string StripInlineComment(string line)
        {
            var index = line.IndexOf('#', StringComparison.Ordinal);
            return index >= 0 ? line.Substring(0, index) : line;
        }

        private static string[] SplitWhitespace(string line)
            => line.Split(new[] { ' ', '\t' }, StringSplitOptions.RemoveEmptyEntries);

        private static string RestAfterKeyword(string line, string keyword)
            => line.Length > keyword.Length ? line.Substring(keyword.Length).Trim() : string.Empty;

        private static void LoadMaterialLibraries(string materialLibraries, string? baseDirectory, Dictionary<string, MeshMaterial> materialDefinitions)
        {
            if (string.IsNullOrWhiteSpace(materialLibraries) || string.IsNullOrWhiteSpace(baseDirectory))
            {
                return;
            }

            var combinedPath = System.IO.Path.Combine(baseDirectory, materialLibraries);
            if (File.Exists(combinedPath))
            {
                ParseMtl(File.ReadAllText(combinedPath), System.IO.Path.GetDirectoryName(combinedPath), materialDefinitions);
                return;
            }

            foreach (var token in SplitWhitespace(materialLibraries))
            {
                var path = System.IO.Path.Combine(baseDirectory, token);
                if (File.Exists(path))
                {
                    ParseMtl(File.ReadAllText(path), System.IO.Path.GetDirectoryName(path), materialDefinitions);
                }
            }
        }

        private static void ParseMtl(string text, string? materialDirectory, Dictionary<string, MeshMaterial> materialDefinitions)
        {
            MeshMaterial? currentMaterial = null;
            foreach (var raw in EnumerateLogicalObjLines(text))
            {
                var line = StripInlineComment(raw).Trim();
                if (line.Length == 0)
                {
                    continue;
                }

                var parts = SplitWhitespace(line);
                if (parts.Length == 0)
                {
                    continue;
                }

                switch (parts[0])
                {
                    case "newmtl":
                        currentMaterial = GetOrCreateMaterialDefinition(
                            materialDefinitions,
                            RestAfterKeyword(line, "newmtl"));
                        break;
                    case "Ka" when currentMaterial is not null && parts.Length >= 4:
                        currentMaterial.Ambient = new Vec3(
                            ParseMtlFloat(parts[1]),
                            ParseMtlFloat(parts[2]),
                            ParseMtlFloat(parts[3]));
                        break;
                    case "Kd" when currentMaterial is not null && parts.Length >= 4:
                        currentMaterial.Diffuse = new Vec3(
                            ParseMtlFloat(parts[1]),
                            ParseMtlFloat(parts[2]),
                            ParseMtlFloat(parts[3]));
                        break;
                    case "Ks" when currentMaterial is not null && parts.Length >= 4:
                        currentMaterial.Specular = new Vec3(
                            ParseMtlFloat(parts[1]),
                            ParseMtlFloat(parts[2]),
                            ParseMtlFloat(parts[3]));
                        break;
                    case "Ke" when currentMaterial is not null && parts.Length >= 4:
                        currentMaterial.Emission = new Vec3(
                            ParseMtlFloat(parts[1]),
                            ParseMtlFloat(parts[2]),
                            ParseMtlFloat(parts[3]));
                        break;
                    case "Ns" when currentMaterial is not null && parts.Length >= 2:
                        currentMaterial.Shininess = Math.Clamp(float.Parse(parts[1], NumberStyles.Float, CultureInfo.InvariantCulture), 0.0f, 1000.0f);
                        break;
                    case "d" when currentMaterial is not null && parts.Length >= 2:
                        currentMaterial.Alpha = ParseMtlFloat(parts[1]);
                        break;
                    case "Tr" when currentMaterial is not null && parts.Length >= 2:
                        currentMaterial.Alpha = 1.0f - ParseMtlFloat(parts[1]);
                        break;
                    case "map_Kd" when currentMaterial is not null:
                        currentMaterial.DiffuseTexturePath = ResolveMaterialMapPath(
                            ExtractMaterialMapPath(line, "map_Kd"),
                            materialDirectory);
                        currentMaterial.TryLoadDiffuseTexture();
                        break;
                }
            }
        }

        private static float ParseMtlFloat(string value)
            => Math.Clamp(float.Parse(value, NumberStyles.Float, CultureInfo.InvariantCulture), 0.0f, 1.0f);

        private static MeshMaterial GetOrCreateMaterialDefinition(Dictionary<string, MeshMaterial> materialDefinitions, string materialName)
        {
            if (!materialDefinitions.TryGetValue(materialName, out var material))
            {
                material = new MeshMaterial(materialName, GuessMaterialColor(materialName));
                materialDefinitions[materialName] = material;
            }

            return material;
        }

        private int ResolveMaterialIndex(string materialName, Dictionary<string, MeshMaterial> materialDefinitions)
        {
            if (string.IsNullOrWhiteSpace(materialName))
            {
                return 0;
            }

            if (_materialIndices.TryGetValue(materialName, out var index))
            {
                return index;
            }

            var material = materialDefinitions.TryGetValue(materialName, out var defined)
                ? defined
                : new MeshMaterial(materialName, GuessMaterialColor(materialName));

            index = Materials.Count;
            Materials.Add(material);
            _materialIndices[materialName] = index;
            return index;
        }

        private static bool IsSceneHelperMaterial(string materialName)
        {
            if (string.IsNullOrWhiteSpace(materialName))
            {
                return false;
            }

            var name = materialName.ToLowerInvariant();
            return name.Contains("studio_lights", StringComparison.Ordinal) ||
                   name.Contains("back_drop", StringComparison.Ordinal) ||
                   name is "sun";
        }

        private static string ResolveMaterialMapPath(string mapPath, string? materialDirectory)
        {
            if (string.IsNullOrWhiteSpace(mapPath))
            {
                return string.Empty;
            }

            var normalized = mapPath.Trim().Trim('"');
            if (System.IO.Path.IsPathRooted(normalized))
            {
                return normalized;
            }

            if (string.IsNullOrWhiteSpace(materialDirectory))
            {
                return normalized;
            }

            return System.IO.Path.Combine(materialDirectory, normalized);
        }

        private static string ExtractMaterialMapPath(string line, string keyword)
        {
            var rest = RestAfterKeyword(line, keyword);
            var tokens = SplitWhitespace(rest);
            if (tokens.Length == 0)
            {
                return string.Empty;
            }

            var fileTokens = new List<string>(tokens.Length);
            for (var i = 0; i < tokens.Length; i++)
            {
                var token = tokens[i];
                if (token.StartsWith("-", StringComparison.Ordinal))
                {
                    i += MaterialMapOptionArgumentCount(token, tokens, i + 1);
                    continue;
                }

                fileTokens.Add(token);
            }

            return string.Join(' ', fileTokens);
        }

        private static int MaterialMapOptionArgumentCount(string option, string[] tokens, int start)
        {
            var lower = option.ToLowerInvariant();
            if (lower is "-mm")
            {
                return 2;
            }

            if (lower is "-o" or "-s" or "-t")
            {
                var count = 0;
                while (start + count < tokens.Length &&
                       count < 3 &&
                       !tokens[start + count].StartsWith("-", StringComparison.Ordinal))
                {
                    count++;
                }

                return count;
            }

            return lower is "-blendu" or "-blendv" or "-boost" or "-bm" or "-cc" or "-clamp" or "-imfchan" or "-texres" or "-type"
                ? 1
                : 0;
        }

        private static Vec3 GuessMaterialColor(string materialName)
        {
            var name = materialName.ToLowerInvariant();
            if (name.Contains("polar", StringComparison.Ordinal) || name.Contains("white", StringComparison.Ordinal))
            {
                return new Vec3(0.86f, 0.88f, 0.84f);
            }

            if (name.Contains("interior", StringComparison.Ordinal))
            {
                return new Vec3(0.045f, 0.044f, 0.040f);
            }

            if (name.Contains("under", StringComparison.Ordinal) || name.Contains("tire", StringComparison.Ordinal) || name.Contains("rubber", StringComparison.Ordinal))
            {
                return new Vec3(0.035f, 0.038f, 0.040f);
            }

            if (name.Contains("glass", StringComparison.Ordinal) || name.Contains("window", StringComparison.Ordinal))
            {
                return new Vec3(0.12f, 0.20f, 0.24f);
            }

            if (name.Contains("color_m02", StringComparison.Ordinal) || name.Contains("chrome", StringComparison.Ordinal))
            {
                return new Vec3(0.52f, 0.56f, 0.54f);
            }

            return DefaultMeshColor;
        }

        private static Vec2 DefaultUv(int index, int count)
        {
            var t = count <= 1 ? 0 : index / (float)(count - 1);
            return new Vec2(t, 1.0f - t);
        }

        private void FinalizeDocument(bool normalize)
        {
            RecomputeBounds();
            if (normalize)
            {
                var scale = Radius <= 0.0001f ? 1.0f : 1.65f / Radius;
                for (var i = 0; i < Positions.Count; i++)
                {
                    Positions[i] = (Positions[i] - Center) * scale;
                }
            }

            OriginalPositions.Clear();
            OriginalPositions.AddRange(Positions);
            RecomputeBounds();
            RecomputeNormals();
        }

        public void InvalidateAuthoredNormals()
        {
            UseAuthoredNormals = false;
        }

        public void RecomputeBounds()
        {
            if (Positions.Count == 0 || Triangles.Count == 0)
            {
                if (Positions.Count == 0)
                {
                    Center = new Vec3(0, 0, 0);
                    Radius = 1.0f;
                    return;
                }

                var fallbackMin = Positions[0];
                var fallbackMax = Positions[0];
                foreach (var p in Positions)
                {
                    ExpandBounds(p, ref fallbackMin, ref fallbackMax);
                }

                ApplyBounds(fallbackMin, fallbackMax);
                return;
            }

            var first = Positions[Triangles[0].A.PositionIndex];
            var min = first;
            var max = first;
            foreach (var triangle in Triangles)
            {
                ExpandBounds(Positions[triangle.A.PositionIndex], ref min, ref max);
                ExpandBounds(Positions[triangle.B.PositionIndex], ref min, ref max);
                ExpandBounds(Positions[triangle.C.PositionIndex], ref min, ref max);
            }

            ApplyBounds(min, max);
        }

        private void ApplyBounds(Vec3 min, Vec3 max)
        {
            Center = (min + max) * 0.5f;
            Radius = 0.001f;
            if (Triangles.Count == 0)
            {
                foreach (var p in Positions)
                {
                    Radius = MathF.Max(Radius, (p - Center).Length);
                }

                return;
            }

            foreach (var triangle in Triangles)
            {
                Radius = MathF.Max(Radius, (Positions[triangle.A.PositionIndex] - Center).Length);
                Radius = MathF.Max(Radius, (Positions[triangle.B.PositionIndex] - Center).Length);
                Radius = MathF.Max(Radius, (Positions[triangle.C.PositionIndex] - Center).Length);
            }
        }

        private static void ExpandBounds(Vec3 point, ref Vec3 min, ref Vec3 max)
        {
            min = new Vec3(MathF.Min(min.X, point.X), MathF.Min(min.Y, point.Y), MathF.Min(min.Z, point.Z));
            max = new Vec3(MathF.Max(max.X, point.X), MathF.Max(max.Y, point.Y), MathF.Max(max.Z, point.Z));
        }

        public void RecomputeNormals()
        {
            Normals.Clear();
            for (var i = 0; i < Positions.Count; i++)
            {
                Normals.Add(new Vec3(0, 0, 0));
            }

            foreach (var triangle in Triangles)
            {
                var a = Positions[triangle.A.PositionIndex];
                var b = Positions[triangle.B.PositionIndex];
                var c = Positions[triangle.C.PositionIndex];
                var normal = Vec3.Cross(b - a, c - a).Normalized();
                Normals[triangle.A.PositionIndex] = Normals[triangle.A.PositionIndex] + normal;
                Normals[triangle.B.PositionIndex] = Normals[triangle.B.PositionIndex] + normal;
                Normals[triangle.C.PositionIndex] = Normals[triangle.C.PositionIndex] + normal;
            }

            for (var i = 0; i < Normals.Count; i++)
            {
                Normals[i] = Normals[i].Normalized();
            }

            RecomputeBounds();
        }
    }

    private const string BuiltInCubeObj = """
# Textured cube with explicit UVs
v -1 -1 -1
v  1 -1 -1
v  1  1 -1
v -1  1 -1
v -1 -1  1
v  1 -1  1
v  1  1  1
v -1  1  1
vt 0 0
vt 1 0
vt 1 1
vt 0 1
f 1/1 2/2 3/3 4/4
f 5/1 8/4 7/3 6/2
f 1/1 5/2 6/3 2/4
f 2/1 6/2 7/3 3/4
f 3/1 7/2 8/3 4/4
f 5/1 1/2 4/3 8/4
""";
}
