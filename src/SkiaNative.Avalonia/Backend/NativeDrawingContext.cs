using Avalonia.Media;
using Avalonia.Media.Imaging;
using Avalonia.Media.Immutable;
using Avalonia.Platform;
using SkiaNative.Avalonia.Geometry;
using SkiaNative.Avalonia.Imaging;

namespace SkiaNative.Avalonia.Backend;

internal sealed class NativeDrawingContext : IDrawingContextImpl, IDrawingContextImplWithEffects
{
    private readonly NativeSessionHandle _session;
    private readonly IDisposable? _platformSession;
    private readonly NativeContextHandle? _gpuContext;
    private readonly SkiaNativeOptions _options;
    private readonly CommandBuffer _commands;
    private readonly Stack<RenderOptions> _renderOptionsStack = new();
    private readonly Stack<TextOptions> _textOptionsStack = new();
    private readonly object _directFlushLock = new();
    private SkiaNativeApiLeaseFeature? _directFeature;
    private CommandBufferFlushResult _directFlushResult;
    private Matrix _transform = Matrix.Identity;
    private RenderOptions _renderOptions;
    private TextOptions _textOptions;

    public NativeDrawingContext(NativeSessionHandle session, double scaling, IDisposable? platformSession, SkiaNativeOptions options, NativeContextHandle? gpuContext = null)
    {
        _session = session;
        _platformSession = platformSession;
        _gpuContext = gpuContext;
        _options = options;
        _commands = new CommandBuffer(options.InitialCommandBufferCapacity);
    }

    public Matrix Transform
    {
        get => _transform;
        set
        {
            _transform = value;
            _commands.SetTransform(value);
        }
    }

    public void Clear(Color color) => _commands.Clear(color);
    public void DrawLine(IPen? pen, Point p1, Point p2) => _commands.DrawLine(pen, p1, p2, _renderOptions);
    public void DrawRectangle(IBrush? brush, IPen? pen, RoundedRect rect, BoxShadows boxShadows = default)
    {
        _commands.DrawBoxShadows(rect, boxShadows, inset: false, _renderOptions);
        _commands.DrawRect(brush, pen, rect, _renderOptions);
        _commands.DrawBoxShadows(rect, boxShadows, inset: true, _renderOptions);
    }
    public void DrawEllipse(IBrush? brush, IPen? pen, Rect rect) => _commands.DrawEllipse(brush, pen, rect, _renderOptions);

    public void DrawGeometry(IBrush? brush, IPen? pen, IGeometryImpl geometry)
    {
        if (geometry is not NativeGeometry native)
        {
            return;
        }

        if (native.Kind == NativeGeometryKind.Rectangle)
        {
            _commands.DrawRect(brush, pen, new RoundedRect(native.Bounds), _renderOptions);
            return;
        }

        if (native.Kind == NativeGeometryKind.Ellipse)
        {
            _commands.DrawEllipse(brush, pen, native.Bounds, _renderOptions);
            return;
        }

        _commands.DrawGeometry(brush, pen, native, _renderOptions);
    }

    public void DrawRegion(IBrush? brush, IPen? pen, IPlatformRenderInterfaceRegion region)
    {
        foreach (var rect in region.Rects)
        {
            _commands.DrawRect(brush, pen, new RoundedRect(new Rect(rect.Left, rect.Top, rect.Right - rect.Left, rect.Bottom - rect.Top)), _renderOptions);
        }
    }

    public void DrawBitmap(IBitmapImpl source, double opacity, Rect sourceRect, Rect destRect)
    {
        _commands.DrawBitmap(source, opacity, sourceRect, destRect, _renderOptions);
    }

    public void DrawBitmap(IBitmapImpl source, IBrush opacityMask, Rect opacityMaskRect, Rect destRect)
    {
        _commands.PushOpacityMask(opacityMask, opacityMaskRect);
        DrawBitmap(source, 1, new Rect(0, 0, source.PixelSize.Width, source.PixelSize.Height), destRect);
        _commands.PopOpacityMask();
    }

    public void DrawGlyphRun(IBrush? foreground, IGlyphRunImpl glyphRun)
    {
        _commands.DrawGlyphRun(foreground, glyphRun, _textOptions, _renderOptions);
    }

    public IDrawingContextLayerImpl CreateLayer(PixelSize size) => new NativeRenderTargetBitmap(size, SkiaNativePlatform.DefaultDpi, _options);

    public void PushClip(Rect clip)
    {
        _commands.Save();
        _commands.PushClip(clip);
    }

    public void PushClip(RoundedRect clip)
    {
        _commands.Save();
        _commands.PushClip(clip);
    }
    public void PushClip(IPlatformRenderInterfaceRegion region)
    {
        _commands.Save();
        _commands.PushClip(region);
    }
    public void PopClip() => _commands.Restore();
    public void PushLayer(Rect bounds) => _commands.SaveLayer(1, bounds);
    public void PopLayer() => _commands.Restore();
    public void PushOpacity(double opacity, Rect? bounds) => _commands.SaveLayer(opacity, bounds);
    public void PopOpacity() => _commands.Restore();
    public void PushOpacityMask(IBrush mask, Rect bounds) => _commands.PushOpacityMask(mask, bounds);
    public void PopOpacityMask() => _commands.PopOpacityMask();
    public void PushGeometryClip(IGeometryImpl clip)
    {
        _commands.Save();

        if (clip is not NativeGeometry native)
        {
            return;
        }

        if (native.Kind == NativeGeometryKind.Rectangle)
        {
            _commands.PushClip(native.Bounds);
            return;
        }

        if (native.TryGetFillPath(out _))
        {
            _commands.PushGeometryClip(native);
        }
    }
    public void PopGeometryClip() => _commands.Restore();
    public void PushRenderOptions(RenderOptions renderOptions)
    {
        _renderOptionsStack.Push(_renderOptions);
        _renderOptions = _renderOptions.MergeWith(renderOptions);
    }

    public void PopRenderOptions()
    {
        _renderOptions = _renderOptionsStack.Pop();
    }
    public void PushTextOptions(TextOptions textOptions)
    {
        _textOptionsStack.Push(_textOptions);
        _textOptions = _textOptions.MergeWith(textOptions);
    }

    public void PopTextOptions()
    {
        _textOptions = _textOptionsStack.Pop();
    }
    public void PushEffect(Rect? clipRect, IEffect effect) => _commands.Save();
    public void PopEffect() => _commands.Restore();
    public object? GetFeature(Type t)
    {
        if (t == typeof(ISkiaNativeApiLeaseFeature))
        {
            return _directFeature ??= new SkiaNativeApiLeaseFeature(
                _session,
                _options.InitialCommandBufferCapacity,
                FlushPendingCommands,
                ReportDirectFlush);
        }

        return null;
    }

    public void Dispose()
    {
        CommandBufferFlushResult result = default;
        NativeResourceCacheUsage resourceCacheUsage = default;

        try
        {
            result = CombineFlushResults(GetDirectFlushResult(), _commands.Flush(_session));
        }
        finally
        {
            _session.Dispose();
            _platformSession?.Dispose();

            if (_options.PurgeGpuResourcesAfterFrame && _gpuContext is { IsInvalid: false })
            {
                NativeMethods.ContextPurgeUnlockedResources(_gpuContext);
            }

            resourceCacheUsage = GetResourceCacheUsage();
        }

        SkiaNativeDiagnostics.Publish(_options, result, resourceCacheUsage);
    }

    private NativeResourceCacheUsage GetResourceCacheUsage()
    {
        if (_gpuContext is not { IsInvalid: false })
        {
            return default;
        }

        var hasUsage = NativeMethods.ContextGetResourceCacheUsage(
            _gpuContext,
            out var resourceCount,
            out var resourceBytes,
            out var purgeableBytes,
            out var resourceLimit);

        return hasUsage != 0
            ? new NativeResourceCacheUsage(resourceCount, resourceBytes, purgeableBytes, resourceLimit)
            : default;
    }

    private void ReportDirectFlush(CommandBufferFlushResult result)
    {
        if (result.CommandCount == 0)
        {
            return;
        }

        lock (_directFlushLock)
        {
            _directFlushResult = CombineFlushResults(_directFlushResult, result);
        }
    }

    private CommandBufferFlushResult GetDirectFlushResult()
    {
        lock (_directFlushLock)
        {
            return _directFlushResult;
        }
    }

    private CommandBufferFlushResult FlushPendingCommands() => _commands.Flush(_session);

    private static CommandBufferFlushResult CombineFlushResults(CommandBufferFlushResult first, CommandBufferFlushResult second) =>
        new(
            first.CommandCount + second.CommandCount,
            first.NativeTransitionCount + second.NativeTransitionCount,
            first.FlushElapsed + second.FlushElapsed,
            first.NativeResult != 0 ? first.NativeResult : second.NativeResult);
}
