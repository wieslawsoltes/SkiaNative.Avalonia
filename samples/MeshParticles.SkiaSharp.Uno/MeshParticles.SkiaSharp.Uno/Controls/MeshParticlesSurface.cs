using System;
using System.Diagnostics;
using System.Runtime.InteropServices;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Media;
using SkiaSharp;
using Uno.WinUI.Graphics2DSK;
using Windows.Foundation;

namespace MeshParticles.SkiaSharp.Uno.Controls;

public readonly record struct MeshParticlesStats(
    double FrameMilliseconds,
    double RenderMilliseconds,
    double FramesPerSecond,
    int ParticleCount,
    int VertexCount,
    int IndexCount,
    int DrawCalls,
    int UniformBytes,
    string Backend,
    string Status);

public sealed partial class MeshParticlesSurface : SKCanvasElement
{
    private const int ParticleCount = 4096;
    private const int VerticesPerParticle = 4;
    private const int IndicesPerParticle = 6;
    private const int VertexStride = 9 * sizeof(float);
    private const int UniformFloatCount = 4;

    private const string VertexShader = @"
        uniform float4 u_data;

        Varyings main(const Attributes attrs) {
            Varyings v;
            float u_time = u_data.x;
            float u_width = u_data.y;
            float u_height = u_data.z;
            float minSide = min(u_width, u_height);
            float2 center = float2(u_width * 0.5, u_height * 0.5);
            float angle = attrs.angle + u_time * attrs.speed;
            float orbit = attrs.radius * minSide * 0.46;
            float wobble = sin(attrs.phase + u_time * 1.37) * minSide * 0.045;
            float curl = cos(attrs.phase * 0.71 + u_time * 0.91) * minSide * 0.035;
            float2 radial = float2(cos(angle), sin(angle * 0.86 + attrs.phase * 0.11));
            float2 cross = float2(cos(angle * 2.13 + attrs.phase), sin(angle * 1.61 - attrs.phase));
            float2 particleCenter = center + radial * (orbit + wobble) + cross * curl;
            float pulse = 0.74 + 0.26 * sin(u_time * 2.2 + attrs.phase);
            float particleSize = attrs.size * pulse;

            v.position = particleCenter + attrs.local * particleSize;
            v.local = attrs.local;
            v.hue = attrs.hue + u_time * 0.015;
            v.alpha = attrs.alpha * (0.78 + 0.22 * sin(u_time * 1.7 + attrs.phase));
            v.phase = attrs.phase + u_time * attrs.speed;
            return v;
        }";

    private const string FragmentShader = @"
        uniform float4 u_data;

        float3 hsv2rgb(float3 c) {
            float4 K = float4(1.0, 2.0 / 3.0, 1.0 / 3.0, 3.0);
            float3 p = abs(fract(c.xxx + K.xyz) * 6.0 - K.www);
            return c.z * mix(K.xxx, clamp(p - K.xxx, 0.0, 1.0), c.y);
        }

        float2 main(const Varyings v, out half4 color) {
            float u_time = u_data.x;
            float radius = length(v.local);
            float edge = 1.0 - smoothstep(0.78, 1.0, radius);
            float core = 1.0 - smoothstep(0.0, 0.34, radius);
            float ring = smoothstep(0.28, 0.42, radius) * (1.0 - smoothstep(0.66, 0.86, radius));
            float sparkle = 0.5 + 0.5 * sin(v.phase * 3.0 + u_time * 4.2);
            float alpha = clamp(v.alpha * edge, 0.0, 1.0);
            float3 rgb = hsv2rgb(float3(fract(v.hue), 0.76, 1.0));
            float intensity = 0.72 + core * 0.35 + ring * 0.22 + sparkle * 0.08;
            color = half4(half3(rgb * intensity * alpha), half(alpha));
            return v.position;
        }";

    private static readonly SKMeshSpecificationAttribute[] Attributes =
    {
        new(SKMeshSpecificationAttributeType.Float2, 0, "local"),
        new(SKMeshSpecificationAttributeType.Float, 8, "radius"),
        new(SKMeshSpecificationAttributeType.Float, 12, "angle"),
        new(SKMeshSpecificationAttributeType.Float, 16, "speed"),
        new(SKMeshSpecificationAttributeType.Float, 20, "size"),
        new(SKMeshSpecificationAttributeType.Float, 24, "hue"),
        new(SKMeshSpecificationAttributeType.Float, 28, "alpha"),
        new(SKMeshSpecificationAttributeType.Float, 32, "phase"),
    };

    private static readonly SKMeshSpecificationVarying[] Varyings =
    {
        new(SKMeshSpecificationVaryingType.Float2, "local"),
        new(SKMeshSpecificationVaryingType.Float, "hue"),
        new(SKMeshSpecificationVaryingType.Float, "alpha"),
        new(SKMeshSpecificationVaryingType.Float, "phase"),
    };

    private readonly Stopwatch _clock = Stopwatch.StartNew();
    private readonly Stopwatch _renderClock = new();
    private readonly float[] _uniformData = new float[UniformFloatCount];
    private readonly SKPaint _meshPaint = new() { IsAntialias = true, Color = SKColors.White, BlendMode = SKBlendMode.Plus };
    private readonly SKPaint _gridPaint = new() { IsAntialias = false, Color = new SKColor(120, 156, 196, 36), StrokeWidth = 1 };
    private readonly SKPaint _borderPaint = new() { IsAntialias = true, Color = new SKColor(120, 156, 196, 80), StrokeWidth = 1, Style = SKPaintStyle.Stroke };

    private SKMeshSpecification? _specification;
    private SKMeshVertexBuffer? _vertexBuffer;
    private SKMeshIndexBuffer? _indexBuffer;
    private int _vertexCount;
    private int _indexCount;
    private int _lastDrawCalls;
    private int _frameCount;
    private double _lastStatsSeconds;
    private double _lastFrameSeconds;
    private double _framesPerSecond;
    private string _status = "Waiting for first render.";

    public MeshParticlesSurface()
    {
        HorizontalAlignment = HorizontalAlignment.Stretch;
        VerticalAlignment = VerticalAlignment.Stretch;
        Loaded += OnLoaded;
        Unloaded += OnUnloaded;
    }

    public event EventHandler<MeshParticlesStats>? StatsUpdated;

    protected override void RenderOverride(SKCanvas canvas, Size area)
    {
        var width = Math.Max(1, (int)MathF.Ceiling((float)area.Width));
        var height = Math.Max(1, (int)MathF.Ceiling((float)area.Height));

        _renderClock.Restart();
        DrawScene(canvas, width, height);
        _renderClock.Stop();

        PublishStats(_renderClock.Elapsed.TotalMilliseconds);
    }

    private void OnLoaded(object sender, RoutedEventArgs e)
    {
        CompositionTarget.Rendering += OnRendering;
        Invalidate();
    }

    private void OnUnloaded(object sender, RoutedEventArgs e)
    {
        CompositionTarget.Rendering -= OnRendering;
        DisposeMeshResources();
    }

    private void OnRendering(object? sender, object e)
    {
        Invalidate();
    }

    private void DrawScene(SKCanvas canvas, int width, int height)
    {
        canvas.Clear(new SKColor(8, 14, 24));
        DrawGrid(canvas, width, height);

        EnsureMeshResources();

        if (_specification is null || _vertexBuffer is null || _indexBuffer is null)
        {
            _status = "SKMesh API is unavailable. Restore SkiaSharp PR 3779 artifacts.";
            return;
        }

        var time = (float)_clock.Elapsed.TotalSeconds;
        _uniformData[0] = time;
        _uniformData[1] = width;
        _uniformData[2] = height;
        _uniformData[3] = 0;

        using var uniforms = SKData.CreateCopy(MemoryMarshal.AsBytes(_uniformData.AsSpan()));
        using var mesh = SKMesh.MakeIndexed(
            _specification,
            SKMeshMode.Triangles,
            _vertexBuffer,
            _vertexCount,
            0,
            _indexBuffer,
            _indexCount,
            0,
            uniforms,
            new SKRect(0, 0, width, height),
            out var errors);

        if (mesh is null || !mesh.IsValid)
        {
            _status = string.IsNullOrWhiteSpace(errors) ? "SKMesh.MakeIndexed failed." : errors;
            return;
        }

        _lastDrawCalls = 1;
        canvas.DrawMesh(mesh, _meshPaint);
        _status = "SKMeshSpecification + cached vertex/index buffers; per-frame uniforms drive animation. No fallback renderer is used.";
    }

    private void EnsureMeshResources()
    {
        if (_specification is null)
        {
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
                return;
            }
        }

        if (_vertexBuffer is not null && _indexBuffer is not null)
        {
            return;
        }

        var vertices = new ParticleVertex[ParticleCount * VerticesPerParticle];
        var indices = new ushort[ParticleCount * IndicesPerParticle];
        var random = new Random(1337);
        var vi = 0;
        var ii = 0;

        for (var particle = 0; particle < ParticleCount; particle++)
        {
            var radius = 0.05f + NextFloat(random) * 0.95f;
            var angle = NextFloat(random) * MathF.Tau;
            var speed = 0.12f + NextFloat(random) * 0.62f;
            var size = 2.8f + MathF.Pow(NextFloat(random), 1.7f) * 13.5f;
            var hue = 0.53f + NextFloat(random) * 0.36f;
            var alpha = 0.16f + NextFloat(random) * 0.52f;
            var phase = NextFloat(random) * MathF.Tau;
            var baseVertex = (ushort)vi;

            vertices[vi++] = new ParticleVertex(-1, -1, radius, angle, speed, size, hue, alpha, phase);
            vertices[vi++] = new ParticleVertex(1, -1, radius, angle, speed, size, hue, alpha, phase);
            vertices[vi++] = new ParticleVertex(1, 1, radius, angle, speed, size, hue, alpha, phase);
            vertices[vi++] = new ParticleVertex(-1, 1, radius, angle, speed, size, hue, alpha, phase);

            indices[ii++] = baseVertex;
            indices[ii++] = (ushort)(baseVertex + 1);
            indices[ii++] = (ushort)(baseVertex + 2);
            indices[ii++] = baseVertex;
            indices[ii++] = (ushort)(baseVertex + 2);
            indices[ii++] = (ushort)(baseVertex + 3);
        }

        _vertexCount = vertices.Length;
        _indexCount = indices.Length;
        _vertexBuffer = SKMeshVertexBuffer.Make(MemoryMarshal.AsBytes(vertices.AsSpan()));
        _indexBuffer = SKMeshIndexBuffer.Make(MemoryMarshal.AsBytes(indices.AsSpan()));
    }

    private void DisposeMeshResources()
    {
        _specification?.Dispose();
        _vertexBuffer?.Dispose();
        _indexBuffer?.Dispose();
        _specification = null;
        _vertexBuffer = null;
        _indexBuffer = null;
        _vertexCount = 0;
        _indexCount = 0;
    }

    private void DrawGrid(SKCanvas canvas, int width, int height)
    {
        const int grid = 12;
        var stepX = width / (float)grid;
        var stepY = height / (float)grid;

        for (var i = 0; i <= grid; i++)
        {
            var x = MathF.Round(i * stepX) + 0.5f;
            var y = MathF.Round(i * stepY) + 0.5f;
            canvas.DrawLine(x, 0, x, height, _gridPaint);
            canvas.DrawLine(0, y, width, y, _gridPaint);
        }

        canvas.DrawRect(new SKRect(0.5f, 0.5f, width - 0.5f, height - 0.5f), _borderPaint);
    }

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

        StatsUpdated?.Invoke(this, new MeshParticlesStats(
            frameMilliseconds,
            renderMilliseconds,
            _framesPerSecond,
            ParticleCount,
            _vertexCount,
            _indexCount,
            _lastDrawCalls,
            UniformFloatCount * sizeof(float),
            "Uno SKCanvasElement + SkiaSharp v4 SKMesh",
            _status));
    }

    private static float NextFloat(Random random) => (float)random.NextDouble();

    [StructLayout(LayoutKind.Sequential, Pack = 4)]
    private readonly record struct ParticleVertex(
        float LocalX,
        float LocalY,
        float Radius,
        float Angle,
        float Speed,
        float Size,
        float Hue,
        float Alpha,
        float Phase);
}
