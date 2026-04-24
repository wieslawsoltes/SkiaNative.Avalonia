using System;
using System.Diagnostics;
using System.Threading;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Media;
using Avalonia.Rendering.SceneGraph;
using Avalonia.VisualTree;
using MotionMark.SkiaNative.AvaloniaApp.Rendering;
using ISkiaNativeApiLeaseFeature = global::SkiaNative.Avalonia.ISkiaNativeApiLeaseFeature;
using SkiaNativeDirectCanvas = global::SkiaNative.Avalonia.SkiaNativeDirectCanvas;
using SkiaNativeDiagnostics = global::SkiaNative.Avalonia.SkiaNativeDiagnostics;
using SkiaNativeFrameDiagnostics = global::SkiaNative.Avalonia.SkiaNativeFrameDiagnostics;
using SkiaNativeStrokeCap = global::SkiaNative.Avalonia.SkiaNativeStrokeCap;
using SkiaNativeStrokeJoin = global::SkiaNative.Avalonia.SkiaNativeStrokeJoin;

namespace MotionMark.SkiaNative.AvaloniaApp.Controls;

internal sealed class MotionMarkSurface : Control
{
    public static readonly StyledProperty<int> ComplexityProperty =
        AvaloniaProperty.Register<MotionMarkSurface, int>(
            nameof(Complexity),
            8,
            coerce: static (_, value) => Math.Clamp(value, 0, 24));

    public static readonly StyledProperty<bool> MutateSplitsProperty =
        AvaloniaProperty.Register<MotionMarkSurface, bool>(nameof(MutateSplits));

    private static readonly Color s_backgroundColor = Color.FromRgb(12, 16, 24);
    private static readonly Color s_gridColor = Color.FromArgb(38, 255, 255, 255);

    private readonly MotionMarkScene _scene = new();
    private bool _frameRequested;
    private bool _isAttached;
    private TimeSpan? _lastFrameTimestamp;
    private double _frameAccumulatorMs;
    private int _statsFrameCount;
    private long _renderTicksAccumulator;
    private long _renderFrameCount;
    private int _lastElementCount;
    private int _lastPathRunCount;
    private SkiaNativeFrameDiagnostics _lastNativeFrame;

    public event EventHandler<FrameStats>? FrameStatsUpdated;

    static MotionMarkSurface()
    {
        AffectsRender<MotionMarkSurface>(ComplexityProperty, MutateSplitsProperty);
    }

    public MotionMarkSurface()
    {
        ClipToBounds = true;
    }

    public int Complexity
    {
        get => GetValue(ComplexityProperty);
        set => SetValue(ComplexityProperty, value);
    }

    public bool MutateSplits
    {
        get => GetValue(MutateSplitsProperty);
        set => SetValue(MutateSplitsProperty, value);
    }

    protected override void OnPropertyChanged(AvaloniaPropertyChangedEventArgs change)
    {
        base.OnPropertyChanged(change);

        if (change.Property == ComplexityProperty)
        {
            _scene.SetComplexity(Complexity);
            RequestNextFrame();
        }
        else if (change.Property == MutateSplitsProperty)
        {
            RequestNextFrame();
        }
    }

    protected override void OnAttachedToVisualTree(VisualTreeAttachmentEventArgs e)
    {
        base.OnAttachedToVisualTree(e);
        _isAttached = true;
        _scene.SetComplexity(Complexity);
        SkiaNativeDiagnostics.FrameRendered += OnSkiaNativeFrameRendered;
        RequestNextFrame();
    }

    protected override void OnDetachedFromVisualTree(VisualTreeAttachmentEventArgs e)
    {
        SkiaNativeDiagnostics.FrameRendered -= OnSkiaNativeFrameRendered;
        _isAttached = false;
        _frameRequested = false;
        _lastFrameTimestamp = null;
        _frameAccumulatorMs = 0;
        _statsFrameCount = 0;
        _scene.ClearSnapshot();
        Interlocked.Exchange(ref _renderTicksAccumulator, 0);
        Interlocked.Exchange(ref _renderFrameCount, 0);
        base.OnDetachedFromVisualTree(e);
    }

    public override void Render(DrawingContext context)
    {
        base.Render(context);

        var bounds = new Rect(Bounds.Size);
        if (bounds.Width <= 0 || bounds.Height <= 0)
        {
            return;
        }

        var snapshot = _scene.GetSnapshot(bounds.Size, MutateSplits);
        _lastElementCount = snapshot.ElementCount;
        _lastPathRunCount = snapshot.PathRuns.Length;
        context.Custom(new MotionMarkDrawOperation(this, bounds, snapshot));
    }

    private void ReportRenderElapsed(TimeSpan elapsed)
    {
        Interlocked.Add(ref _renderTicksAccumulator, elapsed.Ticks);
        Interlocked.Increment(ref _renderFrameCount);
    }

    private void RequestNextFrame()
    {
        if (!_isAttached || _frameRequested)
        {
            return;
        }

        var topLevel = TopLevel.GetTopLevel(this);
        if (topLevel is null)
        {
            return;
        }

        _frameRequested = true;
        topLevel.RequestAnimationFrame(OnAnimationFrame);
    }

    private void OnAnimationFrame(TimeSpan timestamp)
    {
        _frameRequested = false;
        if (!_isAttached)
        {
            return;
        }

        if (_lastFrameTimestamp is TimeSpan last)
        {
            var deltaMs = (timestamp - last).TotalMilliseconds;
            if (deltaMs > 0 && deltaMs < 250)
            {
                _frameAccumulatorMs += deltaMs;
                _statsFrameCount++;

                const double statsWindowMs = 500.0;
                if (_frameAccumulatorMs >= statsWindowMs && _statsFrameCount > 0)
                {
                    var averageFrameMs = _frameAccumulatorMs / _statsFrameCount;
                    var renderTicks = Interlocked.Exchange(ref _renderTicksAccumulator, 0);
                    var renderFrames = Interlocked.Exchange(ref _renderFrameCount, 0);
                    var averageRenderMs = renderFrames > 0
                        ? TimeSpan.FromTicks(renderTicks / renderFrames).TotalMilliseconds
                        : 0;
                    var fps = averageFrameMs > 0 ? 1000.0 / averageFrameMs : 0;
                    FrameStatsUpdated?.Invoke(
                        this,
                        new FrameStats(
                            Complexity,
                            _lastElementCount,
                            _lastPathRunCount,
                            averageFrameMs,
                            averageRenderMs,
                            fps,
                            _lastNativeFrame.CommandCount,
                            _lastNativeFrame.NativeTransitionCount,
                            _lastNativeFrame.GpuResourceBytes));

                    _frameAccumulatorMs = 0;
                    _statsFrameCount = 0;
                }
            }
        }

        _lastFrameTimestamp = timestamp;
        InvalidateVisual();
        RequestNextFrame();
    }

    private void OnSkiaNativeFrameRendered(SkiaNativeFrameDiagnostics diagnostics)
    {
        _lastNativeFrame = diagnostics;
    }

    private sealed class MotionMarkDrawOperation : ICustomDrawOperation
    {
        private readonly MotionMarkSurface _owner;
        private readonly Rect _bounds;
        private readonly MotionMarkSceneSnapshot _snapshot;

        public MotionMarkDrawOperation(MotionMarkSurface owner, Rect bounds, MotionMarkSceneSnapshot snapshot)
        {
            _owner = owner;
            _bounds = bounds;
            _snapshot = snapshot.AddReference();
        }

        public Rect Bounds => _bounds;

        public void Dispose()
        {
            _snapshot.Release();
        }

        public bool HitTest(Point p) => _bounds.Contains(p);

        public void Render(ImmediateDrawingContext context)
        {
            var stopwatch = Stopwatch.StartNew();
            if (context.TryGetFeature<ISkiaNativeApiLeaseFeature>(out var directFeature))
            {
                using var lease = directFeature.Lease();
                var canvas = lease.Canvas;
                canvas.Save();
                canvas.PushClip(_bounds);
                canvas.FillRectangle(s_backgroundColor, _bounds);
                DrawGrid(canvas, _bounds);

                canvas.StrokePaths(
                    _snapshot.PathRuns,
                    SkiaNativeStrokeCap.Round,
                    SkiaNativeStrokeJoin.Round);

                canvas.Restore();
            }
            else
            {
                using (context.PushClip(_bounds))
                {
                    context.FillRectangle(Brushes.Black, _bounds);
                }
            }

            stopwatch.Stop();
            _owner.ReportRenderElapsed(stopwatch.Elapsed);
        }

        public bool Equals(ICustomDrawOperation? other)
        {
            return other is MotionMarkDrawOperation operation
                   && ReferenceEquals(operation._owner, _owner)
                   && operation._bounds == _bounds
                   && ReferenceEquals(operation._snapshot, _snapshot);
        }

        private static void DrawGrid(SkiaNativeDirectCanvas canvas, Rect bounds)
        {
            var spacing = Math.Max(20, Math.Min(bounds.Width / 12, bounds.Height / 8));
            for (var x = bounds.Left; x <= bounds.Right; x += spacing)
            {
                canvas.DrawLine(s_gridColor, 1, new Point(x, bounds.Top), new Point(x, bounds.Bottom));
            }

            for (var y = bounds.Top; y <= bounds.Bottom; y += spacing)
            {
                canvas.DrawLine(s_gridColor, 1, new Point(bounds.Left, y), new Point(bounds.Right, y));
            }
        }
    }
}
