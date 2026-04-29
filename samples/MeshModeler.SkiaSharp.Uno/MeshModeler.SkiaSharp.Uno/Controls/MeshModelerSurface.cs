using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Globalization;
using System.Runtime.InteropServices;
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
    private const int VertexStride = 12 * sizeof(float);
    private const int UniformFloatCount = 8;
    private const float NearPlane = 0.08f;

    private const string VertexShader = @"
        uniform float4 u_view;
        uniform float4 u_light;

        Varyings main(const Attributes attrs) {
            Varyings v;
            v.position = attrs.position;
            v.uv = attrs.uv;
            v.normal = normalize(attrs.normal);
            v.depth = attrs.depth;
            v.selected = attrs.selected;
            v.bary = attrs.bary;
            return v;
        }";

    private const string FragmentShader = @"
        uniform float4 u_view;
        uniform float4 u_light;

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
            float wire = 1.0 - smoothstep(0.012, 0.035, min(min(v.bary.x, v.bary.y), v.bary.z));
            float3 rgb;

            if (mode < 0.5) {
                float c = checker(v.uv);
                float line = gridLine(v.uv);
                float3 texA = float3(0.05, 0.18, 0.28);
                float3 texB = float3(0.92, 0.78, 0.38);
                rgb = mix(texA, texB, c) * (0.24 + diffuse * 0.86);
                rgb += line * float3(0.06, 0.65, 0.95) * 0.45;
                rgb += rim * float3(0.14, 0.42, 0.62);
            } else if (mode < 1.5) {
                rgb = mix(float3(0.06, 0.14, 0.30), float3(0.95, 0.42, 0.15), depth01);
                rgb *= 0.30 + diffuse * 0.70;
            } else {
                rgb = n * 0.5 + 0.5;
                rgb *= 0.45 + diffuse * 0.55;
            }

            rgb = mix(rgb, float3(0.0, 0.0, 0.0), wire * 0.72);
            rgb += v.selected * float3(0.0, 1.0, 0.85) * 0.85;
            color = half4(half3(rgb), half(1.0));
            return v.position;
        }";

    private static readonly SKMeshSpecificationAttribute[] Attributes =
    {
        new(SKMeshSpecificationAttributeType.Float2, 0, "position"),
        new(SKMeshSpecificationAttributeType.Float2, 8, "uv"),
        new(SKMeshSpecificationAttributeType.Float3, 16, "normal"),
        new(SKMeshSpecificationAttributeType.Float, 28, "depth"),
        new(SKMeshSpecificationAttributeType.Float, 32, "selected"),
        new(SKMeshSpecificationAttributeType.Float3, 36, "bary"),
    };

    private static readonly SKMeshSpecificationVarying[] Varyings =
    {
        new(SKMeshSpecificationVaryingType.Float2, "uv"),
        new(SKMeshSpecificationVaryingType.Float3, "normal"),
        new(SKMeshSpecificationVaryingType.Float, "depth"),
        new(SKMeshSpecificationVaryingType.Float, "selected"),
        new(SKMeshSpecificationVaryingType.Float3, "bary"),
    };

    private readonly Stopwatch _clock = Stopwatch.StartNew();
    private readonly Stopwatch _renderClock = new();
    private readonly float[] _uniformData = new float[UniformFloatCount];
    private readonly MeshVertex[] _submittedVertices = new MeshVertex[MaxSubmittedVertices];
    private readonly ushort[] _submittedIndices = new ushort[MaxSubmittedIndices];
    private readonly List<SortTriangle> _sortTriangles = new(4096);
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
    private SKMeshSpecification? _specification;
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
        _document = MeshDocument.CreateTorus();
        ResetView();
        _status = "Loaded procedural torus sample.";
        Invalidate();
    }

    public void LoadSampleCube()
    {
        _document = MeshDocument.ParseObj(BuiltInCubeObj, "Textured cube OBJ");
        ResetView();
        _status = "Loaded built-in textured cube OBJ.";
        Invalidate();
    }

    public void LoadObjText(string objText, string name)
    {
        _document = MeshDocument.ParseObj(objText, name);
        ResetView();
        _status = $"Loaded OBJ '{name}' with {_document.Positions.Count:N0} vertices and {_document.Triangles.Count:N0} triangles.";
        Invalidate();
    }

    public void ToggleEditMode()
    {
        _editMode = !_editMode;
        _status = _editMode
            ? "Edit mode enabled. Click a projected vertex, then drag it in the camera plane."
            : "Edit mode disabled. Left drag orbits the camera.";
        Invalidate();
    }

    public void SetShadingMode(int mode)
    {
        _mode = Math.Clamp(mode, 0, 2);
        _status = _mode switch
        {
            < 0.5f => "Shading: UV checker texture + Lambert lighting.",
            < 1.5f => "Shading: depth visualization.",
            _ => "Shading: normal visualization."
        };
        Focus(FocusState.Programmatic);
        Invalidate();
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
        _specification?.Dispose();
        _specification = null;
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
            case VirtualKey.Number1:
                _mode = 0;
                _status = "Shading: UV checker texture + Lambert lighting.";
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
                if (_selectedVertex >= 0)
                {
                    _document.Positions[_selectedVertex] = _document.OriginalPositions[_selectedVertex];
                    _document.RecomputeNormals();
                    _status = $"Reset vertex {_selectedVertex} to original position.";
                    e.Handled = true;
                }

                break;
        }
    }

    private void DrawScene(SKCanvas canvas, float width, float height)
    {
        EnsureSpec();
        canvas.Clear(new SKColor(5, 9, 15));
        _camera = BuildCameraBasis();
        DrawReferenceGrid(canvas, width, height);
        BuildSubmittedMesh(width, height);
        DrawSubmittedMesh(canvas, width, height);
        DrawVertexHandles(canvas, width, height);
        DrawOverlay(canvas, width, height);
    }

    private void EnsureSpec()
    {
        if (_specification is not null)
        {
            return;
        }

        using var colorSpace = SKColorSpace.CreateSrgb();
        _specification = SKMeshSpecification.Make(
            Attributes,
            VertexStride,
            Varyings,
            VertexShader,
            FragmentShader,
            colorSpace,
            SKAlphaType.Premul,
            out var errors);

        if (_specification is null)
        {
            _status = string.IsNullOrWhiteSpace(errors) ? "SKMeshSpecification.Make failed." : errors;
        }
    }

    private void BuildSubmittedMesh(float width, float height)
    {
        _sortTriangles.Clear();
        _submittedVertexCount = 0;
        _submittedIndexCount = 0;

        for (var i = 0; i < _document.Triangles.Count; i++)
        {
            var triangle = _document.Triangles[i];
            if (!TryProjectTriangle(triangle, width, height, out var a, out var b, out var c, out var depth))
            {
                continue;
            }

            _sortTriangles.Add(new SortTriangle(i, depth, a, b, c));
        }

        _sortTriangles.Sort(static (a, b) => b.Depth.CompareTo(a.Depth));

        foreach (var tri in _sortTriangles)
        {
            if (_submittedVertexCount + 3 > MaxSubmittedVertices || _submittedIndexCount + 3 > MaxSubmittedIndices)
            {
                _status = "Mesh truncated at 65k submitted vertices; split large OBJ files for this sample.";
                break;
            }

            var baseIndex = (ushort)_submittedVertexCount;
            var selectedA = tri.A.SourceVertex == _selectedVertex ? 1.0f : 0.0f;
            var selectedB = tri.B.SourceVertex == _selectedVertex ? 1.0f : 0.0f;
            var selectedC = tri.C.SourceVertex == _selectedVertex ? 1.0f : 0.0f;

            _submittedVertices[_submittedVertexCount++] = tri.A.WithBarycentric(1, 0, 0, selectedA);
            _submittedVertices[_submittedVertexCount++] = tri.B.WithBarycentric(0, 1, 0, selectedB);
            _submittedVertices[_submittedVertexCount++] = tri.C.WithBarycentric(0, 0, 1, selectedC);
            _submittedIndices[_submittedIndexCount++] = baseIndex;
            _submittedIndices[_submittedIndexCount++] = (ushort)(baseIndex + 1);
            _submittedIndices[_submittedIndexCount++] = (ushort)(baseIndex + 2);
        }
    }

    private bool TryProjectTriangle(Triangle triangle, float width, float height, out ProjectedVertex a, out ProjectedVertex b, out ProjectedVertex c, out float depth)
    {
        a = default;
        b = default;
        c = default;
        depth = 0;

        if (!ProjectCorner(triangle.A, width, height, out a) ||
            !ProjectCorner(triangle.B, width, height, out b) ||
            !ProjectCorner(triangle.C, width, height, out c))
        {
            return false;
        }

        var area = (b.ScreenX - a.ScreenX) * (c.ScreenY - a.ScreenY) - (b.ScreenY - a.ScreenY) * (c.ScreenX - a.ScreenX);
        if (MathF.Abs(area) < 0.02f)
        {
            return false;
        }

        depth = (a.Depth + b.Depth + c.Depth) / 3.0f;
        return true;
    }

    private bool ProjectCorner(Corner corner, float width, float height, out ProjectedVertex projected)
    {
        var world = _document.Positions[corner.PositionIndex];
        var normal = _document.Normals[corner.PositionIndex];
        var rel = world - _camera.Position;
        var vx = Vec3.Dot(rel, _camera.Right);
        var vy = Vec3.Dot(rel, _camera.Up);
        var vz = Vec3.Dot(rel, _camera.Forward);
        if (vz <= NearPlane)
        {
            projected = default;
            return false;
        }

        var focal = MathF.Min(width, height) * 0.78f;
        var sx = width * 0.5f + vx / vz * focal;
        var sy = height * 0.5f - vy / vz * focal;
        var viewNormal = new Vec3(
            Vec3.Dot(normal, _camera.Right),
            Vec3.Dot(normal, _camera.Up),
            Vec3.Dot(normal, _camera.Forward)).Normalized();

        projected = new ProjectedVertex(sx, sy, corner.U, corner.V, viewNormal.X, viewNormal.Y, viewNormal.Z, vz, corner.PositionIndex);
        return true;
    }

    private void DrawSubmittedMesh(SKCanvas canvas, float width, float height)
    {
        _drawCalls = 0;
        if (_specification is null || _submittedVertexCount <= 0 || _submittedIndexCount <= 0)
        {
            return;
        }

        FillUniforms(width, height);
        using var uniforms = SKData.CreateCopy(MemoryMarshal.AsBytes(_uniformData.AsSpan()));
        using var vertexBuffer = SKMeshVertexBuffer.Make(MemoryMarshal.AsBytes(_submittedVertices.AsSpan(0, _submittedVertexCount)));
        using var indexBuffer = SKMeshIndexBuffer.Make(MemoryMarshal.AsBytes(_submittedIndices.AsSpan(0, _submittedIndexCount)));
        using var mesh = SKMesh.MakeIndexed(
            _specification,
            SKMeshMode.Triangles,
            vertexBuffer,
            _submittedVertexCount,
            0,
            indexBuffer,
            _submittedIndexCount,
            0,
            uniforms,
            new SKRect(-64, -64, width + 64, height + 64),
            out var errors);

        if (mesh is null || !mesh.IsValid)
        {
            _status = string.IsNullOrWhiteSpace(errors) ? "SKMesh.MakeIndexed failed." : errors;
            return;
        }

        canvas.DrawMesh(mesh, _meshPaint);
        _drawCalls = 1;
    }

    private void FillUniforms(float width, float height)
    {
        _uniformData[0] = (float)_clock.Elapsed.TotalSeconds;
        _uniformData[1] = width;
        _uniformData[2] = Math.Max(0.1f, _distance + _document.Radius);
        _uniformData[3] = _mode;
        _uniformData[4] = 0.42f;
        _uniformData[5] = 0.74f;
        _uniformData[6] = 0.52f;
        _uniformData[7] = Math.Max(NearPlane, _distance - _document.Radius * 1.4f);
    }

    private void DrawReferenceGrid(SKCanvas canvas, float width, float height)
    {
        var extent = MathF.Max(2, MathF.Ceiling(_document.Radius * 1.8f));
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
        var stride = Math.Max(1, _document.Positions.Count / 600);
        for (var i = 0; i < _document.Positions.Count; i += stride)
        {
            if (!ProjectPoint(_document.Positions[i], width, height, out var p))
            {
                continue;
            }

            var selected = i == _selectedVertex;
            canvas.DrawCircle(p.X, p.Y, selected ? 5.5f : 2.3f, selected ? _selectedVertexPaint : _vertexPaint);
        }
    }

    private void DrawOverlay(SKCanvas canvas, float width, float height)
    {
        var mode = ShadingModeName;
        var edit = _editMode ? "EDIT" : "VIEW";
        var text = $"{_document.Name} // {mode} // {edit} // vertices {_document.Positions.Count:N0} // triangles {_document.Triangles.Count:N0} // selected {(_selectedVertex >= 0 ? _selectedVertex.ToString(CultureInfo.InvariantCulture) : "none")}";
        canvas.DrawText(text, 18, height - 22, _textFont, _textPaint);
    }

    private int FindNearestVertex(float x, float y, float width, float height, float maxDistance)
    {
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
        if (_selectedVertex < 0 || _selectedVertex >= _document.Positions.Count)
        {
            return;
        }

        var factor = _distance / MathF.Max(1.0f, MathF.Min(_lastWidth, _lastHeight) * 0.78f);
        var delta = _camera.Right * (dx * factor) - _camera.Up * (dy * factor);
        _document.Positions[_selectedVertex] += delta;
        _document.RecomputeNormals();
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
        _document.RecomputeBounds();
        _target = _document.Center;
        _distance = Math.Clamp(_document.Radius * 3.2f, 2.2f, 18.0f);
        _yaw = -0.58f;
        _pitch = 0.38f;
        _selectedVertex = -1;
    }

    private string ShadingModeName => _mode switch
    {
        < 0.5f => "UV texture",
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

        StatsUpdated?.Invoke(this, new MeshModelerStats(
            frameMilliseconds,
            renderMilliseconds,
            _framesPerSecond,
            _document.Positions.Count,
            _document.Triangles.Count,
            _submittedVertexCount,
            _submittedIndexCount,
            _drawCalls,
            UniformFloatCount * sizeof(float),
            _selectedVertex,
            _yaw * 180.0 / Math.PI,
            _pitch * 180.0 / Math.PI,
            _distance,
            ShadingModeName,
            "Uno SKCanvasElement + SkiaSharp v4 SKMesh",
            _status));
    }

    private enum PointerAction
    {
        None,
        Orbit,
        Pan,
        EditVertex
    }

    [StructLayout(LayoutKind.Sequential, Pack = 4)]
    private readonly record struct MeshVertex(
        float X,
        float Y,
        float U,
        float V,
        float Nx,
        float Ny,
        float Nz,
        float Depth,
        float Selected,
        float BaryX,
        float BaryY,
        float BaryZ);

    private readonly record struct ProjectedVertex(
        float ScreenX,
        float ScreenY,
        float U,
        float V,
        float Nx,
        float Ny,
        float Nz,
        float Depth,
        int SourceVertex)
    {
        public MeshVertex WithBarycentric(float bx, float by, float bz, float selected)
            => new(ScreenX, ScreenY, U, V, Nx, Ny, Nz, Depth, selected, bx, by, bz);
    }

    private readonly record struct SortTriangle(int SourceIndex, float Depth, ProjectedVertex A, ProjectedVertex B, ProjectedVertex C);
    private readonly record struct CameraBasis(Vec3 Position, Vec3 Forward, Vec3 Right, Vec3 Up);
    private readonly record struct Vec2(float X, float Y);
    private readonly record struct Corner(int PositionIndex, float U, float V);
    private readonly record struct Triangle(Corner A, Corner B, Corner C);

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

    private sealed class MeshDocument
    {
        public string Name { get; private set; } = "Untitled";
        public List<Vec3> Positions { get; } = new();
        public List<Vec3> OriginalPositions { get; } = new();
        public List<Vec3> Normals { get; } = new();
        public List<Triangle> Triangles { get; } = new();
        public Vec3 Center { get; private set; }
        public float Radius { get; private set; } = 1.0f;

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
                    doc.Triangles.Add(new Triangle(new Corner(a, u0, v0), new Corner(b, u1, v0), new Corner(c, u1, v1)));
                    doc.Triangles.Add(new Triangle(new Corner(a, u0, v0), new Corner(c, u1, v1), new Corner(d, u0, v1)));
                }
            }

            doc.FinalizeDocument(normalize: false);
            return doc;
        }

        public static MeshDocument ParseObj(string text, string name)
        {
            var doc = new MeshDocument { Name = name };
            var uvs = new List<Vec2>();
            var faceCorners = new List<Corner>(8);
            var culture = CultureInfo.InvariantCulture;
            var lines = text.Split(new[] { '\r', '\n' }, StringSplitOptions.RemoveEmptyEntries);

            foreach (var raw in lines)
            {
                var line = raw.Trim();
                if (line.Length == 0 || line[0] == '#')
                {
                    continue;
                }

                var parts = line.Split(' ', StringSplitOptions.RemoveEmptyEntries);
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
                    case "f" when parts.Length >= 4:
                        faceCorners.Clear();
                        for (var i = 1; i < parts.Length; i++)
                        {
                            faceCorners.Add(ParseCorner(parts[i], doc.Positions.Count, uvs));
                        }

                        for (var i = 1; i < faceCorners.Count - 1; i++)
                        {
                            doc.Triangles.Add(new Triangle(faceCorners[0], faceCorners[i], faceCorners[i + 1]));
                        }

                        break;
                }
            }

            if (doc.Positions.Count == 0 || doc.Triangles.Count == 0)
            {
                throw new InvalidOperationException("OBJ did not contain any triangulatable faces.");
            }

            doc.FinalizeDocument(normalize: true);
            return doc;

            float Parse(string value) => float.Parse(value, NumberStyles.Float, culture);
        }

        private static Corner ParseCorner(string token, int positionCount, List<Vec2> uvs)
        {
            var pieces = token.Split('/');
            var positionIndex = ParseObjIndex(pieces[0], positionCount);
            var uv = DefaultUv(positionIndex, positionCount);
            if (pieces.Length > 1 && pieces[1].Length > 0 && uvs.Count > 0)
            {
                var uvIndex = ParseObjIndex(pieces[1], uvs.Count);
                if (uvIndex >= 0 && uvIndex < uvs.Count)
                {
                    uv = uvs[uvIndex];
                }
            }

            return new Corner(positionIndex, uv.X, uv.Y);
        }

        private static int ParseObjIndex(string token, int count)
        {
            var index = int.Parse(token, CultureInfo.InvariantCulture);
            return index < 0 ? count + index : index - 1;
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

        public void RecomputeBounds()
        {
            if (Positions.Count == 0)
            {
                Center = new Vec3(0, 0, 0);
                Radius = 1.0f;
                return;
            }

            var min = Positions[0];
            var max = Positions[0];
            foreach (var p in Positions)
            {
                min = new Vec3(MathF.Min(min.X, p.X), MathF.Min(min.Y, p.Y), MathF.Min(min.Z, p.Z));
                max = new Vec3(MathF.Max(max.X, p.X), MathF.Max(max.Y, p.Y), MathF.Max(max.Z, p.Z));
            }

            Center = (min + max) * 0.5f;
            Radius = 0.001f;
            foreach (var p in Positions)
            {
                Radius = MathF.Max(Radius, (p - Center).Length);
            }
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
