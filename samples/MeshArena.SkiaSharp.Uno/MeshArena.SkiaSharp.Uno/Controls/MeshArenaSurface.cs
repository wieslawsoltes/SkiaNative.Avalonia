using System;
using System.Diagnostics;
using System.Runtime.InteropServices;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Input;
using Microsoft.UI.Xaml.Media;
using SkiaSharp;
using Uno.WinUI.Graphics2DSK;
using Windows.Foundation;
using Windows.System;

namespace MeshArena.SkiaSharp.Uno.Controls;

public readonly record struct MeshArenaStats(
    double FrameMilliseconds,
    double RenderMilliseconds,
    double FramesPerSecond,
    int EntityCount,
    int VertexCount,
    int IndexCount,
    int DrawCalls,
    int UniformBytes,
    int Score,
    int ShieldPercent,
    int ActiveEnemies,
    int ActiveProjectiles,
    string InputMode,
    string Backend,
    string Status);

public sealed partial class MeshArenaSurface : SKCanvasElement
{
    private const float WorldWidth = 4200.0f;
    private const float WorldHeight = 900.0f;
    private const float PlayerHalfWidth = 20.0f;
    private const float PlayerHalfHeight = 31.0f;

    private const int BackgroundColumns = 64;
    private const int BackgroundRows = 34;
    private const int BackgroundVertexCount = BackgroundColumns * BackgroundRows;
    private const int BackgroundIndexCount = (BackgroundColumns - 1) * (BackgroundRows - 1) * 6;
    private const int BackgroundStride = 5 * sizeof(float);

    private const int MaxSprites = 1400;
    private const int MaxPlatforms = 30;
    private const int MaxHazards = 24;
    private const int MaxOrbs = 72;
    private const int MaxWisps = 18;
    private const int MaxParticles = 520;
    private const int SpriteVerticesPerEntity = 4;
    private const int SpriteIndicesPerEntity = 6;
    private const int SpriteStride = 11 * sizeof(float);

    private const int TrailSamples = 72;
    private const int TrailStride = 8 * sizeof(float);
    private const int UniformFloatCount = 8;

    private const string BackgroundVertexShader = @"
        uniform float4 u_view;
        uniform float4 u_player;

        Varyings main(const Attributes attrs) {
            Varyings v;
            float time = u_view.x;
            float width = u_view.y;
            float height = u_view.z;
            float scale = u_view.w;
            float2 uv = attrs.uv;
            float2 p = uv * float2(width, height);
            float2 player = u_player.yz;
            float2 rel = (p - player) / max(min(width, height), 1.0);
            float aura = 1.0 / (1.0 + dot(rel, rel) * 12.0);
            float wind = sin(uv.y * 9.0 + attrs.seed * 5.0 + time * 0.9 + u_player.x * 0.002) * 6.0;
            float canopy = smoothstep(0.12, 0.70, uv.y) * attrs.energy;
            p.x += wind * canopy + aura * sin(time * 3.0 + attrs.lane) * 2.5 * scale;
            p.y += aura * cos(time * 2.0 + attrs.seed * 7.0) * 1.5 * scale;
            v.position = p;
            v.uv = uv;
            v.energy = attrs.energy;
            v.seed = attrs.seed;
            return v;
        }";

    private const string BackgroundFragmentShader = @"
        uniform float4 u_view;
        uniform float4 u_player;

        float hash21(float2 p) {
            return fract(sin(dot(p, float2(127.1, 311.7))) * 43758.5453);
        }

        float treeLayer(float2 uv, float parallax, float density, float yStart) {
            float x = uv.x * density + u_player.x * parallax;
            float cell = floor(x);
            float local = fract(x) - 0.5;
            float rnd = hash21(float2(cell, density));
            float trunkWidth = 0.020 + rnd * 0.035;
            float trunk = 1.0 - smoothstep(trunkWidth, trunkWidth + 0.018, abs(local));
            float heightMask = smoothstep(yStart, 1.0, uv.y);
            float lean = sin(uv.y * 7.0 + rnd * 8.0) * 0.025;
            trunk *= 1.0 - smoothstep(trunkWidth, trunkWidth + 0.015, abs(local + lean));
            float crownCenter = 0.42 + rnd * 0.12;
            float crown = 1.0 - smoothstep(0.16, 0.44, length(float2(local * 1.4, uv.y - crownCenter)));
            crown *= smoothstep(0.14, 0.70, uv.y) * (0.65 + rnd * 0.35);
            return max(trunk * heightMask, crown * 0.62);
        }

        float fireflies(float2 uv, float scale, float threshold) {
            float2 drift = uv + float2(sin(u_view.x * 0.17) * 0.03, -u_view.x * 0.018);
            float2 cell = floor(drift * scale);
            float2 local = fract(drift * scale) - 0.5;
            float rnd = hash21(cell);
            float pulse = 0.45 + 0.55 * sin(u_view.x * (2.0 + rnd * 3.0) + rnd * 20.0);
            float dotMask = 1.0 - smoothstep(0.0, 0.085, length(local));
            return dotMask * pulse * step(threshold, rnd);
        }

        float2 main(const Varyings v, out half4 color) {
            float2 uv = v.uv;
            float2 n = uv * 2.0 - 1.0;
            float horizon = smoothstep(0.08, 0.86, uv.y);
            float moon = 1.0 - smoothstep(0.06, 0.34, length(uv - float2(0.72, 0.18)));
            float moonHalo = 1.0 - smoothstep(0.12, 0.65, length(uv - float2(0.72, 0.18)));
            float mist = 0.5 + 0.5 * sin(uv.x * 9.0 + uv.y * 4.0 + u_view.x * 0.08);
            mist *= smoothstep(0.42, 0.90, uv.y) * 0.22;
            float rearTrees = treeLayer(uv, 0.00018, 13.0, 0.23);
            float midTrees = treeLayer(uv, 0.00035, 21.0, 0.34);
            float frontTrees = treeLayer(uv, 0.00058, 31.0, 0.48);
            float lightColumn = exp(-abs(n.x + 0.22) * 2.3) * smoothstep(0.12, 0.86, 1.0 - uv.y) * 0.18;
            float glints = fireflies(uv, 32.0, 0.965) + fireflies(uv, 62.0, 0.985) * 0.75;
            float playerGlow = 1.0 / (1.0 + length((uv * float2(u_view.y, u_view.z) - u_player.yz) / max(min(u_view.y, u_view.z), 1.0)) * 24.0);

            float3 skyTop = float3(0.020, 0.030, 0.070);
            float3 skyBottom = float3(0.012, 0.105, 0.112);
            float3 rgb = mix(skyTop, skyBottom, horizon);
            rgb += moon * float3(0.62, 0.86, 0.80) * 0.52;
            rgb += moonHalo * float3(0.10, 0.36, 0.42) * 0.42;
            rgb += lightColumn * float3(0.11, 0.63, 0.58);
            rgb += mist * float3(0.10, 0.35, 0.42);
            rgb = mix(rgb, float3(0.010, 0.040, 0.055), rearTrees * 0.38);
            rgb = mix(rgb, float3(0.006, 0.026, 0.034), midTrees * 0.58);
            rgb = mix(rgb, float3(0.004, 0.018, 0.024), frontTrees * 0.78);
            rgb += glints * float3(0.70, 1.00, 0.52) * 0.80;
            rgb += playerGlow * float3(0.18, 0.95, 0.90) * (0.25 + u_player.w * 0.35);
            color = half4(half3(rgb), half(1.0));
            return v.position;
        }";

    private const string SpriteVertexShader = @"
        uniform float4 u_view;
        uniform float4 u_player;

        Varyings main(const Attributes attrs) {
            Varyings v;
            float c = cos(attrs.angle);
            float s = sin(attrs.angle);
            float2 rotated = float2(attrs.local.x * c - attrs.local.y * s, attrs.local.x * s + attrs.local.y * c);
            v.position = attrs.center + rotated * attrs.size;
            v.local = attrs.local;
            v.kind = attrs.kind;
            v.hue = attrs.hue;
            v.alpha = attrs.alpha;
            v.energy = attrs.energy;
            return v;
        }";

    private const string SpriteFragmentShader = @"
        uniform float4 u_view;
        uniform float4 u_player;

        float hash21(float2 p) {
            return fract(sin(dot(p, float2(41.31, 289.17))) * 12973.113);
        }

        float roundedBox(float2 p, float2 b, float r) {
            float2 q = abs(p) - b + r;
            return length(max(q, float2(0.0))) + min(max(q.x, q.y), 0.0) - r;
        }

        float ring(float r, float inner, float outer) {
            return smoothstep(inner, inner + 0.04, r) * (1.0 - smoothstep(outer, outer + 0.06, r));
        }

        float2 main(const Varyings v, out half4 color) {
            float2 p = v.local;
            float r = length(p);
            float time = u_view.x;
            float mask = 0.0;
            float3 rgb = float3(0.0);

            if (v.kind < 0.5) {
                float body = 1.0 - smoothstep(-0.02, 0.04, roundedBox(p, float2(0.96, 0.72), 0.18));
                float moss = smoothstep(0.08, -0.04, p.y + 0.58) * (0.65 + 0.35 * sin(p.x * 13.0 + v.hue * 17.0));
                float bark = 0.50 + 0.18 * sin(p.x * 18.0 + v.hue * 11.0) + 0.10 * sin(p.y * 23.0 + v.energy * 5.0);
                float rim = (1.0 - smoothstep(0.72, 0.98, abs(p.x))) * smoothstep(0.42, 0.72, abs(p.y));
                rgb = mix(float3(0.10, 0.060, 0.035), float3(0.32, 0.19, 0.09), bark);
                rgb = mix(rgb, float3(0.10, 0.42, 0.26), moss * 0.58);
                rgb += rim * float3(0.02, 0.18, 0.16);
                mask = body;
            } else if (v.kind < 1.5) {
                float body = 1.0 - smoothstep(0.30, 0.54, length((p - float2(0.0, 0.10)) * float2(0.86, 1.25)));
                float head = 1.0 - smoothstep(0.18, 0.33, length((p - float2(0.0, -0.34)) * float2(1.0, 0.92)));
                float earL = 1.0 - smoothstep(0.0, 0.16, length((p - float2(-0.22, -0.55)) * float2(1.8, 0.82)));
                float earR = 1.0 - smoothstep(0.0, 0.16, length((p - float2(0.22, -0.55)) * float2(1.8, 0.82)));
                float tail = (1.0 - smoothstep(0.0, 0.24, abs(p.y - 0.42 + p.x * 0.30))) * smoothstep(0.05, 0.82, -p.x);
                float glow = pow(max(0.0, 1.0 - r), 2.0);
                float eyes = (1.0 - smoothstep(0.0, 0.045, length(p - float2(-0.07, -0.38))))
                           + (1.0 - smoothstep(0.0, 0.045, length(p - float2(0.07, -0.38))));
                mask = max(max(max(body, head), max(earL, earR) * 0.82), tail * 0.50);
                rgb = float3(0.74, 1.00, 0.92) * mask;
                rgb += glow * float3(0.08, 0.95, 0.88) * (0.55 + v.energy * 0.65);
                rgb += eyes * float3(0.02, 0.18, 0.14) * 1.3;
            } else if (v.kind < 2.5) {
                float core = 1.0 - smoothstep(0.0, 0.26, r);
                float halo = pow(max(0.0, 1.0 - r), 2.2);
                float pulse = 0.65 + 0.35 * sin(time * 5.5 + v.hue * 28.0);
                mask = max(core, halo * 0.55) * (0.70 + pulse * 0.30);
                rgb = mix(float3(0.15, 0.92, 0.78), float3(0.96, 1.00, 0.46), core) * (0.8 + v.energy * 0.8);
            } else if (v.kind < 3.5) {
                float spark = pow(max(0.0, 1.0 - r), 3.2);
                float leaf = (1.0 - smoothstep(0.0, 0.35, abs(p.y + p.x * 0.34))) * (1.0 - smoothstep(0.12, 0.94, abs(p.x)));
                mask = max(spark, leaf * 0.46) * v.alpha;
                rgb = mix(float3(0.18, 0.92, 0.68), float3(1.0, 0.72, 0.20), v.hue) * (0.85 + v.energy);
            } else if (v.kind < 4.5) {
                float smoke = pow(max(0.0, 1.0 - r), 1.7) * (0.72 + 0.18 * sin(time * 7.0 + p.x * 9.0));
                float core = 1.0 - smoothstep(0.20, 0.64, length(p * float2(0.85, 1.2)));
                float eyeL = 1.0 - smoothstep(0.0, 0.06, length(p - float2(-0.13, -0.12)));
                float eyeR = 1.0 - smoothstep(0.0, 0.06, length(p - float2(0.13, -0.12)));
                mask = max(smoke * 0.75, core);
                rgb = float3(0.08, 0.035, 0.13) * mask + (eyeL + eyeR) * float3(1.0, 0.45, 0.16) * 1.2;
                rgb += ring(r, 0.45, 0.60) * float3(0.45, 0.12, 0.55) * (0.4 + v.energy);
            } else if (v.kind < 5.5) {
                float cell = floor((p.x + 1.0) * 5.0);
                float local = fract((p.x + 1.0) * 5.0) * 2.0 - 1.0;
                float blade = (1.0 - smoothstep(0.04, 0.50, abs(local))) * smoothstep(0.95, -0.52, p.y) * smoothstep(-1.0, 0.90, p.y);
                float base = smoothstep(0.75, 1.0, p.y) * (1.0 - smoothstep(0.92, 1.04, abs(p.x)));
                float edge = 0.45 + 0.35 * hash21(float2(cell, v.hue));
                mask = max(blade, base);
                rgb = mix(float3(0.10, 0.012, 0.018), float3(0.68, 0.08, 0.08), edge) + blade * float3(0.22, 0.0, 0.02);
            } else {
                float leaf = 1.0 - smoothstep(0.0, 0.55, length((p - float2(0.0, 0.06)) * float2(0.72, 1.35)));
                float vein = 1.0 - smoothstep(0.0, 0.045, abs(p.x + p.y * 0.12));
                float bloom = pow(max(0.0, 1.0 - r), 2.5);
                mask = max(leaf, bloom * 0.30) * v.alpha;
                rgb = mix(float3(0.04, 0.28, 0.13), float3(0.16, 0.86, 0.42), leaf);
                rgb += vein * float3(0.52, 1.00, 0.60) * 0.35;
                rgb += bloom * float3(0.12, 0.94, 0.72) * (0.25 + v.energy * 0.40);
            }

            color = half4(half3(rgb * mask), half(clamp(mask * v.alpha, 0.0, 1.0)));
            return v.position;
        }";

    private const string TrailVertexShader = @"
        uniform float4 u_view;
        uniform float4 u_player;

        Varyings main(const Attributes attrs) {
            Varyings v;
            float2 tangent = normalize(attrs.dir + float2(0.0002, 0.0001));
            float2 normal = float2(-tangent.y, tangent.x);
            v.position = attrs.center + normal * attrs.side * attrs.width;
            v.age = attrs.age;
            v.energy = attrs.energy;
            v.side = attrs.side;
            return v;
        }";

    private const string TrailFragmentShader = @"
        uniform float4 u_view;
        uniform float4 u_player;

        float2 main(const Varyings v, out half4 color) {
            float edge = 1.0 - smoothstep(0.35, 1.0, abs(v.side));
            float fade = pow(max(0.0, 1.0 - v.age), 1.85);
            float pulse = 0.68 + 0.32 * sin(u_view.x * 9.0 + v.age * 18.0);
            float3 rgb = mix(float3(0.12, 0.95, 0.84), float3(0.76, 1.0, 0.38), v.age) * pulse * (0.72 + v.energy);
            color = half4(half3(rgb * edge * fade), half(edge * fade * 0.70));
            return v.position;
        }";

    private static readonly SKMeshSpecificationAttribute[] BackgroundAttributes =
    {
        new(SKMeshSpecificationAttributeType.Float2, 0, "uv"),
        new(SKMeshSpecificationAttributeType.Float, 8, "lane"),
        new(SKMeshSpecificationAttributeType.Float, 12, "energy"),
        new(SKMeshSpecificationAttributeType.Float, 16, "seed"),
    };

    private static readonly SKMeshSpecificationVarying[] BackgroundVaryings =
    {
        new(SKMeshSpecificationVaryingType.Float2, "uv"),
        new(SKMeshSpecificationVaryingType.Float, "energy"),
        new(SKMeshSpecificationVaryingType.Float, "seed"),
    };

    private static readonly SKMeshSpecificationAttribute[] SpriteAttributes =
    {
        new(SKMeshSpecificationAttributeType.Float2, 0, "local"),
        new(SKMeshSpecificationAttributeType.Float2, 8, "center"),
        new(SKMeshSpecificationAttributeType.Float2, 16, "size"),
        new(SKMeshSpecificationAttributeType.Float, 24, "kind"),
        new(SKMeshSpecificationAttributeType.Float, 28, "hue"),
        new(SKMeshSpecificationAttributeType.Float, 32, "alpha"),
        new(SKMeshSpecificationAttributeType.Float, 36, "angle"),
        new(SKMeshSpecificationAttributeType.Float, 40, "energy"),
    };

    private static readonly SKMeshSpecificationVarying[] SpriteVaryings =
    {
        new(SKMeshSpecificationVaryingType.Float2, "local"),
        new(SKMeshSpecificationVaryingType.Float, "kind"),
        new(SKMeshSpecificationVaryingType.Float, "hue"),
        new(SKMeshSpecificationVaryingType.Float, "alpha"),
        new(SKMeshSpecificationVaryingType.Float, "energy"),
    };

    private static readonly SKMeshSpecificationAttribute[] TrailAttributes =
    {
        new(SKMeshSpecificationAttributeType.Float2, 0, "center"),
        new(SKMeshSpecificationAttributeType.Float2, 8, "dir"),
        new(SKMeshSpecificationAttributeType.Float, 16, "side"),
        new(SKMeshSpecificationAttributeType.Float, 20, "age"),
        new(SKMeshSpecificationAttributeType.Float, 24, "width"),
        new(SKMeshSpecificationAttributeType.Float, 28, "energy"),
    };

    private static readonly SKMeshSpecificationVarying[] TrailVaryings =
    {
        new(SKMeshSpecificationVaryingType.Float, "age"),
        new(SKMeshSpecificationVaryingType.Float, "energy"),
        new(SKMeshSpecificationVaryingType.Float, "side"),
    };

    private readonly Stopwatch _clock = Stopwatch.StartNew();
    private readonly Stopwatch _renderClock = new();
    private readonly float[] _uniformData = new float[UniformFloatCount];
    private readonly SpriteVertex[] _spriteVertices = new SpriteVertex[MaxSprites * SpriteVerticesPerEntity];
    private readonly ushort[] _spriteIndices = new ushort[MaxSprites * SpriteIndicesPerEntity];
    private readonly TrailVertex[] _trailVertices = new TrailVertex[TrailSamples * 2];
    private readonly Platform[] _platforms = new Platform[MaxPlatforms];
    private readonly Hazard[] _hazards = new Hazard[MaxHazards];
    private readonly Orb[] _orbs = new Orb[MaxOrbs];
    private readonly Wisp[] _wisps = new Wisp[MaxWisps];
    private readonly Particle[] _particles = new Particle[MaxParticles];
    private readonly Vec2[] _trailPoints = new Vec2[TrailSamples];
    private readonly SKPaint _entityPaint = new() { IsAntialias = true, Color = SKColors.White, BlendMode = SKBlendMode.SrcOver };
    private readonly SKPaint _additivePaint = new() { IsAntialias = true, Color = SKColors.White, BlendMode = SKBlendMode.Plus };
    private readonly SKPaint _screenPaint = new() { IsAntialias = true, Color = new SKColor(255, 255, 255, 245), BlendMode = SKBlendMode.SrcOver };

    private SKMeshSpecification? _backgroundSpec;
    private SKMeshSpecification? _spriteSpec;
    private SKMeshSpecification? _trailSpec;
    private SKMeshVertexBuffer? _backgroundVertexBuffer;
    private SKMeshIndexBuffer? _backgroundIndexBuffer;
    private SKMeshIndexBuffer? _spriteIndexBuffer;

    private float _playerX = 140.0f;
    private float _playerY = 520.0f;
    private float _playerVx;
    private float _playerVy;
    private float _facing = 1.0f;
    private float _cameraX;
    private float _health = 1.0f;
    private float _energy = 0.70f;
    private float _dashCooldown;
    private float _dashTimer;
    private float _damageCooldown;
    private float _jumpBuffer;
    private float _coyoteTimer;
    private float _hitFlash;
    private float _pointerWorldX;
    private float _pointerWorldY;
    private float _viewScale = 1.0f;
    private bool _keyLeft;
    private bool _keyRight;
    private bool _keyJump;
    private bool _keyDown;
    private bool _keyDash;
    private bool _pointerKnown;
    private bool _dashQueued;
    private bool _grounded;
    private bool _gameInitialized;
    private int _airJumps;
    private int _platformCount;
    private int _hazardCount;
    private int _orbCount;
    private int _wispCount;
    private int _particleCursor;
    private int _collectedOrbs;
    private int _activeOrbs;
    private int _activeWisps;
    private int _activeParticles;
    private int _spriteCount;
    private int _spriteVertexCount;
    private int _spriteIndexCount;
    private int _trailVertexCount;
    private int _drawCalls;
    private int _totalVertices;
    private int _totalIndices;
    private int _frameCount;
    private double _lastStatsSeconds;
    private double _lastFrameSeconds;
    private double _lastGameSeconds;
    private double _framesPerSecond;
    private string _inputMode = "keyboard";
    private string _status = "Waiting for first render.";

    public MeshArenaSurface()
    {
        HorizontalAlignment = HorizontalAlignment.Stretch;
        VerticalAlignment = VerticalAlignment.Stretch;
        IsTabStop = true;
        Loaded += OnLoaded;
        Unloaded += OnUnloaded;
        PointerMoved += OnPointerMoved;
        PointerPressed += OnPointerPressed;
        PointerReleased += OnPointerReleased;
        KeyDown += OnKeyDown;
        KeyUp += OnKeyUp;
        LostFocus += OnLostFocus;
    }

    public event EventHandler<MeshArenaStats>? StatsUpdated;

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
        Focus(FocusState.Programmatic);
        Invalidate();
    }

    private void OnUnloaded(object sender, RoutedEventArgs e)
    {
        CompositionTarget.Rendering -= OnRendering;
        DisposeMeshResources();
    }

    private void OnRendering(object? sender, object e) => Invalidate();

    private void OnPointerMoved(object sender, PointerRoutedEventArgs e) => UpdatePointerWorld(e);

    private void OnPointerPressed(object sender, PointerRoutedEventArgs e)
    {
        Focus(FocusState.Pointer);
        CapturePointer(e.Pointer);
        UpdatePointerWorld(e);
        _dashQueued = true;
        Invalidate();
    }

    private void OnPointerReleased(object sender, PointerRoutedEventArgs e) => ReleasePointerCapture(e.Pointer);

    private void UpdatePointerWorld(PointerRoutedEventArgs e)
    {
        var p = e.GetCurrentPoint(this).Position;
        var scale = Math.Max(0.001f, _viewScale);
        _pointerWorldX = _cameraX + (float)p.X / scale;
        _pointerWorldY = (float)p.Y / scale;
        _pointerKnown = true;
    }

    private void OnKeyDown(object sender, KeyRoutedEventArgs e)
    {
        if (SetKeyState(e.Key, true))
        {
            e.Handled = true;
            Invalidate();
        }
    }

    private void OnKeyUp(object sender, KeyRoutedEventArgs e)
    {
        if (SetKeyState(e.Key, false))
        {
            e.Handled = true;
        }
    }

    private void OnLostFocus(object sender, RoutedEventArgs e)
    {
        _keyLeft = false;
        _keyRight = false;
        _keyJump = false;
        _keyDown = false;
        _keyDash = false;
        _dashQueued = false;
    }

    private bool SetKeyState(VirtualKey key, bool pressed)
    {
        switch (key)
        {
            case VirtualKey.A:
            case VirtualKey.Left:
                _keyLeft = pressed;
                return true;
            case VirtualKey.D:
            case VirtualKey.Right:
                _keyRight = pressed;
                return true;
            case VirtualKey.W:
            case VirtualKey.Up:
            case VirtualKey.Space:
                if (pressed && !_keyJump)
                {
                    _jumpBuffer = 0.12f;
                }

                _keyJump = pressed;
                return true;
            case VirtualKey.S:
            case VirtualKey.Down:
                _keyDown = pressed;
                return true;
            case VirtualKey.Shift:
            case VirtualKey.Enter:
                if (pressed && !_keyDash)
                {
                    _dashQueued = true;
                }

                _keyDash = pressed;
                return true;
            case VirtualKey.R:
                if (pressed)
                {
                    ResetPlayer(fullReset: true);
                }

                return true;
            default:
                return false;
        }
    }

    private void DrawScene(SKCanvas canvas, int width, int height)
    {
        _status = string.Empty;
        EnsureStaticResources();
        canvas.Clear(new SKColor(3, 9, 15));

        var time = (float)_clock.Elapsed.TotalSeconds;
        var dt = GetDeltaTime(time);
        var scale = Math.Max(0.35f, height / WorldHeight);
        var viewWorldWidth = width / scale;
        _viewScale = scale;

        EnsureGameInitialized();
        UpdateGame(time, dt, viewWorldWidth);
        UpdateCamera(dt, viewWorldWidth);

        var playerScreen = ToScreen(_playerX, _playerY, scale);
        FillUniforms(time, width, height, scale, playerScreen);
        using var uniforms = SKData.CreateCopy(MemoryMarshal.AsBytes(_uniformData.AsSpan()));

        _drawCalls = 0;
        _totalVertices = 0;
        _totalIndices = 0;

        DrawBackground(canvas, uniforms, width, height);
        BuildTrail(time, scale);
        DrawTrail(canvas, uniforms, width, height);
        BuildSprites(time, scale, width, height);
        DrawSprites(canvas, uniforms, width, height);

        if (string.IsNullOrWhiteSpace(_status))
        {
            _status = _drawCalls == 3
                ? $"Luma Grove: {_collectedOrbs}/{_orbCount} light seeds, {_activeWisps} shadow wisps, {_activeParticles} particles. A/D move, Space jump, Shift or click dash. Original procedural art, no fallback renderer."
                : $"Expected 3 mesh draw calls, but submitted {_drawCalls}.";
        }
    }

    private float GetDeltaTime(float time)
    {
        if (_lastGameSeconds <= 0)
        {
            _lastGameSeconds = time;
            return 1.0f / 60.0f;
        }

        var dt = (float)(time - _lastGameSeconds);
        _lastGameSeconds = time;
        return Math.Clamp(dt, 1.0f / 240.0f, 1.0f / 18.0f);
    }

    private void EnsureGameInitialized()
    {
        if (_gameInitialized)
        {
            return;
        }

        BuildWorld();
        ResetPlayer(fullReset: true);
        for (var i = 0; i < TrailSamples; i++)
        {
            _trailPoints[i] = new Vec2(_playerX, _playerY);
        }

        _gameInitialized = true;
    }

    private void BuildWorld()
    {
        _platformCount = 0;
        _hazardCount = 0;
        _orbCount = 0;
        _wispCount = 0;

        AddPlatform(-160, 760, WorldWidth + 320, 190, 0.10f, 0);
        AddPlatform(240, 660, 360, 30, 0.20f, 0);
        AddPlatform(700, 590, 290, 28, 0.25f, 0);
        AddPlatform(1040, 510, 360, 28, 0.30f, 0);
        AddPlatform(1510, 632, 330, 28, 0.38f, 0);
        AddPlatform(1960, 560, 420, 30, 0.44f, 0);
        AddPlatform(2470, 468, 330, 28, 0.48f, 0);
        AddPlatform(2920, 610, 380, 30, 0.56f, 0);
        AddPlatform(3420, 510, 470, 30, 0.63f, 0);
        AddPlatform(3800, 680, 300, 28, 0.70f, 0);
        AddPlatform(1220, 375, 210, 24, 0.34f, 0);
        AddPlatform(2240, 350, 260, 24, 0.52f, 0);
        AddPlatform(3210, 345, 240, 24, 0.66f, 0);

        AddHazard(520, 727, 185, 44, 0.16f);
        AddHazard(1400, 727, 220, 44, 0.22f);
        AddHazard(1850, 727, 165, 44, 0.30f);
        AddHazard(2620, 727, 240, 44, 0.42f);
        AddHazard(3360, 727, 200, 44, 0.54f);

        AddOrb(320, 610, 0.02f);
        AddOrb(475, 610, 0.08f);
        AddOrb(820, 540, 0.14f);
        AddOrb(1110, 458, 0.18f);
        AddOrb(1285, 330, 0.22f);
        AddOrb(1360, 458, 0.26f);
        AddOrb(1580, 582, 0.31f);
        AddOrb(1750, 582, 0.35f);
        AddOrb(2030, 510, 0.42f);
        AddOrb(2210, 510, 0.48f);
        AddOrb(2325, 300, 0.52f);
        AddOrb(2550, 418, 0.56f);
        AddOrb(2740, 418, 0.62f);
        AddOrb(3050, 560, 0.68f);
        AddOrb(3260, 295, 0.72f);
        AddOrb(3520, 458, 0.80f);
        AddOrb(3740, 458, 0.88f);
        AddOrb(3910, 630, 0.94f);

        AddWisp(900, 548, 720, 1010, 0.15f);
        AddWisp(1710, 585, 1520, 1840, 0.32f);
        AddWisp(2350, 305, 2210, 2500, 0.50f);
        AddWisp(3070, 560, 2920, 3300, 0.68f);
        AddWisp(3600, 465, 3410, 3890, 0.86f);
    }

    private void AddPlatform(float x, float y, float width, float height, float hue, int kind)
    {
        if (_platformCount >= MaxPlatforms)
        {
            return;
        }

        _platforms[_platformCount++] = new Platform(x, y, width, height, hue, kind);
    }

    private void AddHazard(float x, float y, float width, float height, float hue)
    {
        if (_hazardCount >= MaxHazards)
        {
            return;
        }

        _hazards[_hazardCount++] = new Hazard(x, y, width, height, hue);
    }

    private void AddOrb(float x, float y, float hue)
    {
        if (_orbCount >= MaxOrbs)
        {
            return;
        }

        _orbs[_orbCount++] = new Orb { Active = true, X = x, Y = y, Hue = hue, Phase = hue * MathF.Tau };
    }

    private void AddWisp(float x, float y, float minX, float maxX, float hue)
    {
        if (_wispCount >= MaxWisps)
        {
            return;
        }

        _wisps[_wispCount++] = new Wisp
        {
            Active = true,
            X = x,
            Y = y,
            MinX = minX,
            MaxX = maxX,
            Vx = 85.0f + hue * 25.0f,
            Phase = hue * MathF.Tau,
            Hue = hue
        };
    }

    private void ResetPlayer(bool fullReset)
    {
        _playerX = 130.0f;
        _playerY = 650.0f;
        _playerVx = 0;
        _playerVy = 0;
        _facing = 1.0f;
        _grounded = false;
        _airJumps = 1;
        _health = 1.0f;
        _energy = fullReset ? 0.75f : Math.Max(0.35f, _energy);
        _dashCooldown = 0;
        _dashTimer = 0;
        _damageCooldown = 0.60f;
        _hitFlash = 0.65f;
        _cameraX = 0;

        if (fullReset)
        {
            _collectedOrbs = 0;
            for (var i = 0; i < _orbCount; i++)
            {
                _orbs[i].Active = true;
            }
        }

        AddParticleBurst(_playerX, _playerY, 0.42f, 40, 260.0f);
    }

    private void UpdateGame(float time, float dt, float viewWorldWidth)
    {
        _dashCooldown = Math.Max(0, _dashCooldown - dt);
        _dashTimer = Math.Max(0, _dashTimer - dt);
        _damageCooldown = Math.Max(0, _damageCooldown - dt);
        _hitFlash = Math.Max(0, _hitFlash - dt * 4.2f);
        _jumpBuffer = Math.Max(0, _jumpBuffer - dt);
        _coyoteTimer = _grounded ? 0.10f : Math.Max(0, _coyoteTimer - dt);
        _energy = Math.Min(1.0f, _energy + dt * (_grounded ? 0.12f : 0.07f));

        UpdatePlayer(dt);
        ResolveCollectibles(time);
        UpdateWisps(time, dt);
        UpdateParticles(dt);

        if (_playerY > WorldHeight + 90.0f)
        {
            DamagePlayer(0.40f, -_facing, 0.0f);
            ResetPlayer(fullReset: false);
        }

        if (_collectedOrbs == _orbCount && _orbCount > 0)
        {
            for (var i = 0; i < _orbCount; i++)
            {
                _orbs[i].Active = true;
            }

            _collectedOrbs = 0;
            _energy = 1.0f;
            AddParticleBurst(_playerX, _playerY - 30, 0.64f, 90, 360.0f);
        }
    }

    private void UpdatePlayer(float dt)
    {
        var move = (_keyRight ? 1.0f : 0.0f) - (_keyLeft ? 1.0f : 0.0f);
        if (move != 0)
        {
            _facing = MathF.Sign(move);
        }

        var acceleration = _grounded ? 2700.0f : 1750.0f;
        var maxSpeed = _keyDown ? 185.0f : 350.0f;
        _playerVx += move * acceleration * dt;
        _playerVx = Math.Clamp(_playerVx, -maxSpeed, maxSpeed);

        if (move == 0 && _grounded)
        {
            _playerVx = Approach(_playerVx, 0.0f, 2550.0f * dt);
        }
        else if (move == 0)
        {
            _playerVx = Approach(_playerVx, 0.0f, 300.0f * dt);
        }

        if (_jumpBuffer > 0 && (_coyoteTimer > 0 || _airJumps > 0))
        {
            if (!_grounded && _coyoteTimer <= 0)
            {
                _airJumps--;
            }

            _playerVy = -710.0f;
            _grounded = false;
            _coyoteTimer = 0;
            _jumpBuffer = 0;
            AddParticleBurst(_playerX, _playerY + PlayerHalfHeight, 0.55f, 22, 170.0f);
        }

        if (_dashQueued)
        {
            TryDash();
            _dashQueued = false;
        }

        var gravity = _dashTimer > 0 ? 760.0f : 1760.0f;
        _playerVy += gravity * dt;
        if (!_keyJump && _playerVy < -120.0f)
        {
            _playerVy += gravity * 0.55f * dt;
        }

        _playerVy = Math.Clamp(_playerVy, -900.0f, 980.0f);

        MoveHorizontal(_playerVx * dt);
        MoveVertical(_playerVy * dt);

        _playerX = Math.Clamp(_playerX, 30.0f, WorldWidth - 30.0f);
        _inputMode = _dashTimer > 0 ? "dash" : _grounded ? "ground" : "air";
    }

    private void TryDash()
    {
        if (_dashCooldown > 0 || _energy < 0.18f)
        {
            return;
        }

        var dirX = _facing;
        var dirY = 0.0f;
        if (_pointerKnown)
        {
            dirX = _pointerWorldX - _playerX;
            dirY = _pointerWorldY - _playerY;
            var len = MathF.Sqrt(dirX * dirX + dirY * dirY);
            if (len > 0.001f)
            {
                dirX /= len;
                dirY /= len;
            }
            else
            {
                dirX = _facing;
                dirY = 0.0f;
            }
        }
        else if (_keyLeft || _keyRight || _keyDown)
        {
            dirX = (_keyRight ? 1.0f : 0.0f) - (_keyLeft ? 1.0f : 0.0f);
            dirY = _keyDown ? 0.35f : 0.0f;
            var len = MathF.Sqrt(dirX * dirX + dirY * dirY);
            if (len > 0.001f)
            {
                dirX /= len;
                dirY /= len;
            }
        }

        _playerVx = dirX * 760.0f;
        _playerVy = Math.Min(_playerVy, dirY * 460.0f - 80.0f);
        _dashTimer = 0.16f;
        _dashCooldown = 0.42f;
        _energy = Math.Max(0, _energy - 0.18f);
        _facing = MathF.Sign(dirX == 0 ? _facing : dirX);
        AddParticleBurst(_playerX, _playerY, 0.62f, 36, 300.0f);
    }

    private void MoveHorizontal(float amount)
    {
        _playerX += amount;
        var player = PlayerRect();
        for (var i = 0; i < _platformCount; i++)
        {
            var platform = _platforms[i];
            if (!Intersects(player, platform.X, platform.Y, platform.Width, platform.Height))
            {
                continue;
            }

            if (amount > 0)
            {
                _playerX = platform.X - PlayerHalfWidth;
            }
            else if (amount < 0)
            {
                _playerX = platform.X + platform.Width + PlayerHalfWidth;
            }

            _playerVx = 0;
            player = PlayerRect();
        }
    }

    private void MoveVertical(float amount)
    {
        _playerY += amount;
        _grounded = false;
        var player = PlayerRect();
        for (var i = 0; i < _platformCount; i++)
        {
            var platform = _platforms[i];
            if (!Intersects(player, platform.X, platform.Y, platform.Width, platform.Height))
            {
                continue;
            }

            if (amount > 0)
            {
                _playerY = platform.Y - PlayerHalfHeight;
                _playerVy = 0;
                _grounded = true;
                _airJumps = 1;
            }
            else if (amount < 0)
            {
                _playerY = platform.Y + platform.Height + PlayerHalfHeight;
                _playerVy = 0;
            }

            player = PlayerRect();
        }
    }

    private void ResolveCollectibles(float time)
    {
        _activeOrbs = 0;
        for (var i = 0; i < _orbCount; i++)
        {
            ref var orb = ref _orbs[i];
            if (!orb.Active)
            {
                continue;
            }

            orb.Phase += 1.2f / 60.0f;
            var ox = orb.X;
            var oy = orb.Y + MathF.Sin(time * 2.4f + orb.Phase) * 7.0f;
            var dx = ox - _playerX;
            var dy = oy - _playerY;
            if (dx * dx + dy * dy < 44.0f * 44.0f)
            {
                orb.Active = false;
                _collectedOrbs++;
                _energy = Math.Min(1.0f, _energy + 0.22f);
                AddParticleBurst(ox, oy, orb.Hue, 34, 230.0f);
                continue;
            }

            _activeOrbs++;
        }
    }

    private void UpdateWisps(float time, float dt)
    {
        _activeWisps = 0;
        for (var i = 0; i < _wispCount; i++)
        {
            ref var wisp = ref _wisps[i];
            if (!wisp.Active)
            {
                continue;
            }

            _activeWisps++;
            wisp.Phase += dt * 2.2f;
            wisp.X += wisp.Vx * dt;
            if (wisp.X < wisp.MinX)
            {
                wisp.X = wisp.MinX;
                wisp.Vx = MathF.Abs(wisp.Vx);
            }
            else if (wisp.X > wisp.MaxX)
            {
                wisp.X = wisp.MaxX;
                wisp.Vx = -MathF.Abs(wisp.Vx);
            }

            var hoverY = wisp.Y + MathF.Sin(time * 2.1f + wisp.Phase) * 15.0f;
            var dx = _playerX - wisp.X;
            var dy = _playerY - hoverY;
            var hitRadius = 48.0f;
            if (dx * dx + dy * dy < hitRadius * hitRadius && _damageCooldown <= 0)
            {
                var knock = MathF.Sign(dx == 0 ? _facing : dx);
                DamagePlayer(0.16f, knock, -0.35f);
                AddParticleBurst(wisp.X, hoverY, 0.90f, 28, 210.0f);
            }
        }

        for (var i = 0; i < _hazardCount; i++)
        {
            var hazard = _hazards[i];
            if (Intersects(PlayerRect(), hazard.X + 8, hazard.Y + 8, hazard.Width - 16, hazard.Height - 8) && _damageCooldown <= 0)
            {
                DamagePlayer(0.22f, _playerX < hazard.X + hazard.Width * 0.5f ? -1.0f : 1.0f, -0.70f);
                AddParticleBurst(_playerX, _playerY, 0.03f, 30, 230.0f);
            }
        }
    }

    private void DamagePlayer(float damage, float knockX, float knockY)
    {
        _health = Math.Max(0, _health - damage);
        _damageCooldown = 0.55f;
        _hitFlash = 1.0f;
        _playerVx = knockX * 360.0f;
        _playerVy = Math.Min(_playerVy, knockY * 520.0f - 160.0f);

        if (_health <= 0)
        {
            ResetPlayer(fullReset: false);
        }
    }

    private void UpdateParticles(float dt)
    {
        _activeParticles = 0;
        for (var i = 0; i < MaxParticles; i++)
        {
            ref var particle = ref _particles[i];
            if (!particle.Active)
            {
                continue;
            }

            particle.X += particle.Vx * dt;
            particle.Y += particle.Vy * dt;
            particle.Vy += 280.0f * dt;
            particle.Vx *= Math.Max(0, 1.0f - dt * 0.85f);
            particle.Vy *= Math.Max(0, 1.0f - dt * 0.55f);
            particle.Life -= dt;
            if (particle.Life <= 0)
            {
                particle.Active = false;
                continue;
            }

            _activeParticles++;
        }
    }

    private void AddParticleBurst(float x, float y, float hue, int count, float speed)
    {
        for (var i = 0; i < count; i++)
        {
            var index = _particleCursor++ % MaxParticles;
            var seed = Hash01(index * 271 + i * 37 + _collectedOrbs * 11);
            var angle = seed * MathF.Tau;
            var s = speed * (0.22f + Hash01(index * 53 + i * 19) * 0.78f);
            _particles[index] = new Particle
            {
                Active = true,
                X = x,
                Y = y,
                Vx = MathF.Cos(angle) * s,
                Vy = MathF.Sin(angle) * s - 70.0f,
                Life = 0.30f + Hash01(index * 149 + i * 13) * 0.70f,
                Size = 4.0f + Hash01(index * 83 + i) * 9.0f,
                Hue = hue + (Hash01(index * 17 + i) - 0.5f) * 0.08f,
                Energy = 0.45f + Hash01(index * 29 + i) * 0.75f
            };
        }
    }

    private void UpdateCamera(float dt, float viewWorldWidth)
    {
        var target = Math.Clamp(_playerX - viewWorldWidth * 0.42f, 0.0f, Math.Max(0.0f, WorldWidth - viewWorldWidth));
        _cameraX = Lerp(_cameraX, target, Math.Clamp(dt * 5.0f, 0.0f, 1.0f));
    }

    private void EnsureStaticResources()
    {
        using var colorSpace = SKColorSpace.CreateSrgb();

        _backgroundSpec ??= CreateSpec(BackgroundAttributes, BackgroundStride, BackgroundVaryings, BackgroundVertexShader, BackgroundFragmentShader, colorSpace, "background");
        _spriteSpec ??= CreateSpec(SpriteAttributes, SpriteStride, SpriteVaryings, SpriteVertexShader, SpriteFragmentShader, colorSpace, "sprite");
        _trailSpec ??= CreateSpec(TrailAttributes, TrailStride, TrailVaryings, TrailVertexShader, TrailFragmentShader, colorSpace, "trail");

        if (_backgroundVertexBuffer is null || _backgroundIndexBuffer is null)
        {
            BuildBackgroundBuffers();
        }

        if (_spriteIndexBuffer is null)
        {
            BuildSpriteIndexBuffer();
        }
    }

    private SKMeshSpecification? CreateSpec(
        SKMeshSpecificationAttribute[] attributes,
        int stride,
        SKMeshSpecificationVarying[] varyings,
        string vertexShader,
        string fragmentShader,
        SKColorSpace colorSpace,
        string name)
    {
        var spec = SKMeshSpecification.Make(
            attributes,
            stride,
            varyings,
            vertexShader,
            fragmentShader,
            colorSpace,
            SKAlphaType.Premul,
            out var errors);

        if (spec is null)
        {
            _status = $"{name} SKMeshSpecification.Make failed: {errors}";
        }

        return spec;
    }

    private void BuildBackgroundBuffers()
    {
        var vertices = new BackgroundVertex[BackgroundVertexCount];
        var indices = new ushort[BackgroundIndexCount];
        var vi = 0;
        for (var y = 0; y < BackgroundRows; y++)
        {
            for (var x = 0; x < BackgroundColumns; x++)
            {
                var u = x / (float)(BackgroundColumns - 1);
                var v = y / (float)(BackgroundRows - 1);
                var lane = (x % 11) * 0.12f + (y % 7) * 0.07f;
                var energy = 0.28f + MathF.Pow(v, 1.25f) * 0.72f;
                var seed = Hash01(x * 31 + y * 131);
                vertices[vi++] = new BackgroundVertex(u, v, lane, energy, seed);
            }
        }

        var ii = 0;
        for (var y = 0; y < BackgroundRows - 1; y++)
        {
            for (var x = 0; x < BackgroundColumns - 1; x++)
            {
                var a = (ushort)(y * BackgroundColumns + x);
                var b = (ushort)(a + 1);
                var c = (ushort)(a + BackgroundColumns);
                var d = (ushort)(c + 1);
                indices[ii++] = a;
                indices[ii++] = b;
                indices[ii++] = d;
                indices[ii++] = a;
                indices[ii++] = d;
                indices[ii++] = c;
            }
        }

        _backgroundVertexBuffer = SKMeshVertexBuffer.Make(MemoryMarshal.AsBytes(vertices.AsSpan()));
        _backgroundIndexBuffer = SKMeshIndexBuffer.Make(MemoryMarshal.AsBytes(indices.AsSpan()));
    }

    private void BuildSpriteIndexBuffer()
    {
        var ii = 0;
        for (var sprite = 0; sprite < MaxSprites; sprite++)
        {
            var baseVertex = (ushort)(sprite * SpriteVerticesPerEntity);
            _spriteIndices[ii++] = baseVertex;
            _spriteIndices[ii++] = (ushort)(baseVertex + 1);
            _spriteIndices[ii++] = (ushort)(baseVertex + 2);
            _spriteIndices[ii++] = baseVertex;
            _spriteIndices[ii++] = (ushort)(baseVertex + 2);
            _spriteIndices[ii++] = (ushort)(baseVertex + 3);
        }

        _spriteIndexBuffer = SKMeshIndexBuffer.Make(MemoryMarshal.AsBytes(_spriteIndices.AsSpan()));
    }

    private void FillUniforms(float time, int width, int height, float scale, Vec2 playerScreen)
    {
        _uniformData[0] = time;
        _uniformData[1] = width;
        _uniformData[2] = height;
        _uniformData[3] = scale;
        _uniformData[4] = _cameraX;
        _uniformData[5] = playerScreen.X;
        _uniformData[6] = playerScreen.Y;
        _uniformData[7] = _energy;
    }

    private void DrawBackground(SKCanvas canvas, SKData uniforms, int width, int height)
    {
        if (_backgroundSpec is null || _backgroundVertexBuffer is null || _backgroundIndexBuffer is null)
        {
            return;
        }

        using var mesh = SKMesh.MakeIndexed(
            _backgroundSpec,
            SKMeshMode.Triangles,
            _backgroundVertexBuffer,
            BackgroundVertexCount,
            0,
            _backgroundIndexBuffer,
            BackgroundIndexCount,
            0,
            uniforms,
            new SKRect(-64, -64, width + 64, height + 64),
            out var errors);

        if (mesh is null || !mesh.IsValid)
        {
            _status = string.IsNullOrWhiteSpace(errors) ? "Background mesh failed." : errors;
            return;
        }

        canvas.DrawMesh(mesh, _screenPaint);
        _drawCalls++;
        _totalVertices += BackgroundVertexCount;
        _totalIndices += BackgroundIndexCount;
    }

    private void BuildTrail(float time, float scale)
    {
        for (var i = TrailSamples - 1; i > 0; i--)
        {
            _trailPoints[i] = _trailPoints[i - 1];
        }

        _trailPoints[0] = new Vec2(_playerX, _playerY);
        _trailVertexCount = TrailSamples * 2;

        for (var i = 0; i < TrailSamples; i++)
        {
            var age = i / (float)(TrailSamples - 1);
            var current = _trailPoints[i];
            var next = _trailPoints[Math.Min(TrailSamples - 1, i + 1)];
            var dx = current.X - next.X;
            var dy = current.Y - next.Y;
            if (dx * dx + dy * dy < 0.01f)
            {
                dx = -_facing;
                dy = 0.15f;
            }

            var screen = ToScreen(current.X, current.Y, scale);
            var flutter = MathF.Sin(time * 18.0f + age * 16.0f) * age * 4.0f;
            var width = (22.0f * (1.0f - age) + 4.0f) * scale * (0.55f + _energy * 0.65f + _dashTimer * 2.0f);
            var left = i * 2;
            _trailVertices[left] = new TrailVertex(screen.X, screen.Y + flutter, dx, dy, -1, age, width, _energy + _dashTimer);
            _trailVertices[left + 1] = new TrailVertex(screen.X, screen.Y - flutter, dx, dy, 1, age, width, _energy + _dashTimer);
        }
    }

    private void DrawTrail(SKCanvas canvas, SKData uniforms, int width, int height)
    {
        if (_trailSpec is null || _trailVertexCount <= 0)
        {
            return;
        }

        using var vertexBuffer = SKMeshVertexBuffer.Make(MemoryMarshal.AsBytes(_trailVertices.AsSpan(0, _trailVertexCount)));
        using var mesh = SKMesh.Make(
            _trailSpec,
            SKMeshMode.TriangleStrip,
            vertexBuffer,
            _trailVertexCount,
            0,
            uniforms,
            new SKRect(-256, -256, width + 256, height + 256),
            out var errors);

        if (mesh is null || !mesh.IsValid)
        {
            _status = string.IsNullOrWhiteSpace(errors) ? "Trail mesh failed." : errors;
            return;
        }

        canvas.DrawMesh(mesh, _additivePaint);
        _drawCalls++;
        _totalVertices += _trailVertexCount;
    }

    private void BuildSprites(float time, float scale, int screenWidth, int screenHeight)
    {
        _spriteCount = 0;
        _spriteVertexCount = 0;
        _spriteIndexCount = 0;

        for (var i = 0; i < _platformCount; i++)
        {
            var p = _platforms[i];
            AddWorldQuad(p.X + p.Width * 0.5f, p.Y + p.Height * 0.5f, p.Width * 0.5f, p.Height * 0.5f, 0, p.Hue, 1.0f, 0, 0.24f + p.Hue, scale, screenWidth, screenHeight);

            if (p.Width > 120.0f)
            {
                var leafCount = Math.Min(6, Math.Max(1, (int)(p.Width / 80.0f)));
                for (var j = 0; j < leafCount; j++)
                {
                    var t = (j + 0.5f) / leafCount;
                    var lx = p.X + p.Width * t + MathF.Sin(time + p.Hue * 17.0f + j) * 5.0f;
                    AddWorldQuad(lx, p.Y - 9.0f, 13.0f, 23.0f, 6, p.Hue + j * 0.03f, 0.58f, -0.35f + j * 0.11f, 0.35f, scale, screenWidth, screenHeight);
                }
            }
        }

        for (var i = 0; i < _hazardCount; i++)
        {
            var h = _hazards[i];
            AddWorldQuad(h.X + h.Width * 0.5f, h.Y + h.Height * 0.5f, h.Width * 0.5f, h.Height * 0.5f, 5, h.Hue, 0.98f, 0, 0.55f, scale, screenWidth, screenHeight);
        }

        for (var i = 0; i < _orbCount; i++)
        {
            ref var orb = ref _orbs[i];
            if (!orb.Active)
            {
                continue;
            }

            var bob = MathF.Sin(time * 2.4f + orb.Phase) * 7.0f;
            var pulse = 1.0f + MathF.Sin(time * 5.5f + orb.Phase) * 0.14f;
            AddWorldQuad(orb.X, orb.Y + bob, 18.0f * pulse, 18.0f * pulse, 2, orb.Hue, 0.92f, 0, 0.80f, scale, screenWidth, screenHeight);
        }

        for (var i = 0; i < _wispCount; i++)
        {
            ref var w = ref _wisps[i];
            if (!w.Active)
            {
                continue;
            }

            var hover = MathF.Sin(time * 2.1f + w.Phase) * 15.0f;
            var pulse = 1.0f + MathF.Sin(time * 5.0f + w.Phase) * 0.11f;
            AddWorldQuad(w.X, w.Y + hover, 32.0f * pulse, 37.0f * pulse, 4, w.Hue, 0.88f, MathF.Sign(w.Vx) * 0.08f, 0.75f, scale, screenWidth, screenHeight);
        }

        for (var i = 0; i < MaxParticles; i++)
        {
            ref var particle = ref _particles[i];
            if (!particle.Active)
            {
                continue;
            }

            var alpha = Math.Clamp(particle.Life * 2.0f, 0.0f, 1.0f);
            AddWorldQuad(particle.X, particle.Y, particle.Size, particle.Size * (0.75f + particle.Energy * 0.25f), 3, particle.Hue, alpha, MathF.Atan2(particle.Vy, particle.Vx), particle.Energy, scale, screenWidth, screenHeight);
        }

        var flash = _hitFlash * 0.45f;
        AddWorldQuad(_playerX, _playerY, 43.0f + flash * 18.0f, 55.0f + flash * 16.0f, 1, 0.58f, 0.94f, _facing < 0 ? MathF.PI : 0, 0.65f + _energy + flash, scale, screenWidth, screenHeight);
        if (_dashTimer > 0 || _energy > 0.82f)
        {
            AddWorldQuad(_playerX, _playerY, 72.0f, 72.0f, 2, 0.55f, 0.16f + _dashTimer, 0, 0.65f + _energy, scale, screenWidth, screenHeight);
        }
    }

    private void AddWorldQuad(
        float worldX,
        float worldY,
        float halfWidth,
        float halfHeight,
        float kind,
        float hue,
        float alpha,
        float angle,
        float energy,
        float scale,
        int screenWidth,
        int screenHeight)
    {
        var screen = ToScreen(worldX, worldY, scale);
        var sx = halfWidth * scale;
        var sy = halfHeight * scale;
        if (screen.X + sx < -120 || screen.X - sx > screenWidth + 120 || screen.Y + sy < -120 || screen.Y - sy > screenHeight + 120)
        {
            return;
        }

        AddQuad(screen.X, screen.Y, sx, sy, kind, hue, alpha, angle, energy);
    }

    private void AddQuad(float x, float y, float sizeX, float sizeY, float kind, float hue, float alpha, float angle, float energy)
    {
        if (_spriteCount >= MaxSprites)
        {
            return;
        }

        var vi = _spriteCount * SpriteVerticesPerEntity;
        _spriteVertices[vi] = new SpriteVertex(-1, -1, x, y, sizeX, sizeY, kind, hue, alpha, angle, energy);
        _spriteVertices[vi + 1] = new SpriteVertex(1, -1, x, y, sizeX, sizeY, kind, hue, alpha, angle, energy);
        _spriteVertices[vi + 2] = new SpriteVertex(1, 1, x, y, sizeX, sizeY, kind, hue, alpha, angle, energy);
        _spriteVertices[vi + 3] = new SpriteVertex(-1, 1, x, y, sizeX, sizeY, kind, hue, alpha, angle, energy);
        _spriteCount++;
        _spriteVertexCount += SpriteVerticesPerEntity;
        _spriteIndexCount += SpriteIndicesPerEntity;
    }

    private void DrawSprites(SKCanvas canvas, SKData uniforms, int width, int height)
    {
        if (_spriteSpec is null || _spriteIndexBuffer is null || _spriteVertexCount <= 0)
        {
            return;
        }

        using var vertexBuffer = SKMeshVertexBuffer.Make(MemoryMarshal.AsBytes(_spriteVertices.AsSpan(0, _spriteVertexCount)));
        using var mesh = SKMesh.MakeIndexed(
            _spriteSpec,
            SKMeshMode.Triangles,
            vertexBuffer,
            _spriteVertexCount,
            0,
            _spriteIndexBuffer,
            _spriteIndexCount,
            0,
            uniforms,
            new SKRect(-256, -256, width + 256, height + 256),
            out var errors);

        if (mesh is null || !mesh.IsValid)
        {
            _status = string.IsNullOrWhiteSpace(errors) ? "Sprite mesh failed." : errors;
            return;
        }

        canvas.DrawMesh(mesh, _entityPaint);
        _drawCalls++;
        _totalVertices += _spriteVertexCount;
        _totalIndices += _spriteIndexCount;
    }

    private void DisposeMeshResources()
    {
        _backgroundSpec?.Dispose();
        _spriteSpec?.Dispose();
        _trailSpec?.Dispose();
        _backgroundVertexBuffer?.Dispose();
        _backgroundIndexBuffer?.Dispose();
        _spriteIndexBuffer?.Dispose();
        _backgroundSpec = null;
        _spriteSpec = null;
        _trailSpec = null;
        _backgroundVertexBuffer = null;
        _backgroundIndexBuffer = null;
        _spriteIndexBuffer = null;
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

        StatsUpdated?.Invoke(this, new MeshArenaStats(
            frameMilliseconds,
            renderMilliseconds,
            _framesPerSecond,
            _spriteCount,
            _totalVertices,
            _totalIndices,
            _drawCalls,
            UniformFloatCount * sizeof(float),
            _collectedOrbs,
            (int)MathF.Round(_health * 100),
            _activeWisps,
            _activeParticles,
            _inputMode,
            "Uno SKCanvasElement + SkiaSharp v4 SKMesh",
            _status));
    }

    private RectF PlayerRect() => new(_playerX - PlayerHalfWidth, _playerY - PlayerHalfHeight, PlayerHalfWidth * 2.0f, PlayerHalfHeight * 2.0f);

    private Vec2 ToScreen(float worldX, float worldY, float scale) => new((worldX - _cameraX) * scale, worldY * scale);

    private static bool Intersects(RectF a, float x, float y, float width, float height)
        => a.X < x + width && a.X + a.Width > x && a.Y < y + height && a.Y + a.Height > y;

    private static float Approach(float value, float target, float delta)
    {
        if (value < target)
        {
            return Math.Min(value + delta, target);
        }

        return Math.Max(value - delta, target);
    }

    private static float Lerp(float a, float b, float t) => a + (b - a) * t;
    private static float Fract(float value) => value - MathF.Floor(value);
    private static float Hash01(int value) => Fract(MathF.Sin(value * 12.9898f) * 43758.5453f);

    private readonly record struct Vec2(float X, float Y);
    private readonly record struct RectF(float X, float Y, float Width, float Height);

    [StructLayout(LayoutKind.Sequential, Pack = 4)]
    private readonly record struct BackgroundVertex(float U, float V, float Lane, float Energy, float Seed);

    [StructLayout(LayoutKind.Sequential, Pack = 4)]
    private readonly record struct SpriteVertex(
        float LocalX,
        float LocalY,
        float CenterX,
        float CenterY,
        float SizeX,
        float SizeY,
        float Kind,
        float Hue,
        float Alpha,
        float Angle,
        float Energy);

    [StructLayout(LayoutKind.Sequential, Pack = 4)]
    private readonly record struct TrailVertex(
        float CenterX,
        float CenterY,
        float DirX,
        float DirY,
        float Side,
        float Age,
        float Width,
        float Energy);

    private readonly record struct Platform(float X, float Y, float Width, float Height, float Hue, int Kind);
    private readonly record struct Hazard(float X, float Y, float Width, float Height, float Hue);

    private struct Orb
    {
        public bool Active;
        public float X;
        public float Y;
        public float Hue;
        public float Phase;
    }

    private struct Wisp
    {
        public bool Active;
        public float X;
        public float Y;
        public float MinX;
        public float MaxX;
        public float Vx;
        public float Phase;
        public float Hue;
    }

    private struct Particle
    {
        public bool Active;
        public float X;
        public float Y;
        public float Vx;
        public float Vy;
        public float Life;
        public float Size;
        public float Hue;
        public float Energy;
    }
}
