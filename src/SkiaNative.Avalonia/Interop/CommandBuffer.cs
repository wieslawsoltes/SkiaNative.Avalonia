using Avalonia;
using Avalonia.Media;
using Avalonia.Media.Imaging;
using Avalonia.Platform;
using System.Buffers;
using System.Collections.Concurrent;
using System.Diagnostics;
using System.Runtime.InteropServices;
using SkiaNative.Avalonia.Geometry;
using SkiaNative.Avalonia.Imaging;
using SkiaNative.Avalonia.Text;

namespace SkiaNative.Avalonia;

internal sealed class CommandBuffer : IDisposable
{
    private const uint BitmapSamplingMask = 0xFu;
    private const uint BitmapBlendShift = 8;
    private const uint BitmapBlendMask = 0x1Fu << (int)BitmapBlendShift;
    private const uint BitmapAntiAliasFlag = 1u << 16;
    private const uint ShapeAntiAliasFlag = 1u << 16;
    private const uint BoxShadowInsetFlag = 1u << 0;

    private NativeCommand[] _commands;
    private int _commandCount;
    private bool _disposed;
    private readonly List<SafeHandle> _resources = new();
    private readonly List<NativeShaderHandle> _ownedShaders = new();
    private readonly List<NativeStrokeHandle> _ownedStrokes = new();
    private readonly List<NativePathHandle> _ownedPaths = new();
    private readonly Stack<bool> _opacityMaskLayers = new();

    public CommandBuffer(int capacity)
    {
        _commands = ArrayPool<NativeCommand>.Shared.Rent(Math.Max(capacity, 16));
    }

    public int CommandCount => _commandCount;
    public IReadOnlyList<NativeCommand> Commands => new ArraySegment<NativeCommand>(_commands, 0, _commandCount);

    public void Save() => AddCommand(new NativeCommand { Kind = NativeCommandKind.Save });

    public void Restore() => AddCommand(new NativeCommand { Kind = NativeCommandKind.Restore });

    public void SetTransform(Matrix matrix) => AddCommand(new NativeCommand
    {
        Kind = NativeCommandKind.SetTransform,
        Matrix = matrix.ToNative()
    });

    public void Clear(Color color) => AddCommand(new NativeCommand
    {
        Kind = NativeCommandKind.Clear,
        Fill = color.ToNative()
    });

    public void DrawLine(IPen? pen, Point p1, Point p2, RenderOptions renderOptions = default)
    {
        if (!BrushUtil.TryGetStroke(pen, new Rect(p1, p2).Normalize(), out var stroke))
        {
            return;
        }

        AddStroke(stroke);
        AddCommand(new NativeCommand
        {
            Kind = NativeCommandKind.DrawLine,
            Flags = CreateShapeFlags(0u, renderOptions),
            Resource1 = stroke.ShaderHandle,
            Resource2 = stroke.StrokeHandle,
            Stroke = stroke.Color,
            StrokeThickness = stroke.Thickness,
            X0 = (float)p1.X,
            Y0 = (float)p1.Y,
            X1 = (float)p2.X,
            Y1 = (float)p2.Y
        });
    }

    public void DrawSolidLine(Color color, double thickness, Point p1, Point p2, RenderOptions renderOptions = default)
    {
        if (color.A == 0 || thickness <= 0)
        {
            return;
        }

        AddCommand(new NativeCommand
        {
            Kind = NativeCommandKind.DrawLine,
            Flags = CreateShapeFlags(0u, renderOptions),
            Stroke = color.ToNative(),
            StrokeThickness = (float)thickness,
            X0 = (float)p1.X,
            Y0 = (float)p1.Y,
            X1 = (float)p2.X,
            Y1 = (float)p2.Y
        });
    }

    public void FillSolidRect(Color color, Rect rect, RenderOptions renderOptions = default)
    {
        rect = rect.Normalize();
        if (color.A == 0 || rect.Width <= 0 || rect.Height <= 0)
        {
            return;
        }

        AddCommand(new NativeCommand
        {
            Kind = NativeCommandKind.DrawRect,
            Flags = CreateShapeFlags(1u, renderOptions),
            Fill = color.ToNative(),
            X0 = (float)rect.X,
            Y0 = (float)rect.Y,
            X1 = (float)rect.Width,
            Y1 = (float)rect.Height
        });
    }

    public void DrawRect(IBrush? brush, IPen? pen, RoundedRect rect, RenderOptions renderOptions = default)
    {
        BrushUtil.TryCreatePaint(brush, rect.Rect, out var fill);
        BrushUtil.TryGetStroke(pen, rect.Rect, out var stroke);

        if (!fill.HasPaint && !stroke.HasStroke)
        {
            return;
        }

        var radii = rect.RadiiTopLeft;
        var kind = radii.X > 0 || radii.Y > 0 ? NativeCommandKind.DrawRoundRect : NativeCommandKind.DrawRect;

        if (fill.HasPaint && stroke.HasStroke && fill.Shader is null && stroke.Paint.Shader is null)
        {
            AddStroke(stroke);
            AddCommand(new NativeCommand
            {
                Kind = kind,
                Flags = CreateShapeFlags(3u, renderOptions),
                Resource2 = stroke.StrokeHandle,
                Fill = fill.Color,
                Stroke = stroke.Color,
                StrokeThickness = stroke.Thickness,
                X0 = (float)rect.Rect.X,
                Y0 = (float)rect.Rect.Y,
                X1 = (float)rect.Rect.Width,
                Y1 = (float)rect.Rect.Height,
                X2 = (float)radii.X,
                Y2 = (float)radii.Y
            });
            return;
        }

        if (fill.HasPaint)
        {
            AddShapeCommand(kind, 1u, fill, default, rect.Rect, radii, renderOptions);
        }

        if (stroke.HasStroke)
        {
            AddShapeCommand(kind, 2u, default, stroke, rect.Rect, radii, renderOptions);
        }
    }

    public void DrawBoxShadows(RoundedRect rect, BoxShadows boxShadows, bool inset, RenderOptions renderOptions = default)
    {
        if (boxShadows.Count == 0 || rect.Rect.Width <= 0 || rect.Rect.Height <= 0)
        {
            return;
        }

        foreach (var shadow in boxShadows)
        {
            if (shadow == default || shadow.IsInset != inset || shadow.Color.A == 0)
            {
                continue;
            }

            var color = shadow.Color.ToNative();
            var radii = rect.RadiiTopLeft;
            AddCommand(new NativeCommand
            {
                Kind = NativeCommandKind.DrawBoxShadow,
                Flags = CreateShapeFlags(shadow.IsInset ? BoxShadowInsetFlag : 0u, renderOptions),
                Fill = color,
                X0 = (float)rect.Rect.X,
                Y0 = (float)rect.Rect.Y,
                X1 = (float)rect.Rect.Width,
                Y1 = (float)rect.Rect.Height,
                X2 = (float)radii.X,
                Y2 = (float)radii.Y,
                X3 = (float)Math.Max(0, shadow.Blur),
                Y3 = (float)shadow.Spread,
                Matrix = new NativeMatrix
                {
                    M11 = shadow.OffsetX,
                    M12 = shadow.OffsetY
                }
            });
        }
    }

    public void DrawEllipse(IBrush? brush, IPen? pen, Rect rect, RenderOptions renderOptions = default)
    {
        BrushUtil.TryCreatePaint(brush, rect, out var fill);
        BrushUtil.TryGetStroke(pen, rect, out var stroke);

        if (!fill.HasPaint && !stroke.HasStroke)
        {
            return;
        }

        if (fill.HasPaint)
        {
            AddShapeCommand(NativeCommandKind.DrawEllipse, 1u, fill, default, rect, default, renderOptions);
        }

        if (stroke.HasStroke)
        {
            AddShapeCommand(NativeCommandKind.DrawEllipse, 2u, default, stroke, rect, default, renderOptions);
        }
    }

    public void DrawPath(IBrush? brush, IPen? pen, Rect bounds, NativePathHandle? fillPath, NativePathHandle? strokePath, RenderOptions renderOptions = default)
    {
        BrushUtil.TryCreatePaint(brush, bounds, out var fill);
        BrushUtil.TryGetStroke(pen, bounds, out var stroke);

        if (fill.HasPaint && fillPath is { IsInvalid: false })
        {
            AddPathCommand(fillPath, 1u, fill, default, renderOptions);
        }

        if (stroke.HasStroke && strokePath is { IsInvalid: false })
        {
            AddPathCommand(strokePath, 2u, default, stroke, renderOptions);
        }
    }

    public unsafe void FillSolidPath(ReadOnlySpan<NativePathCommand> pathCommands, Color color, NativePathFillRule fillRule, RenderOptions renderOptions = default)
    {
        if (color.A == 0 || pathCommands.IsEmpty)
        {
            return;
        }

        NativePathHandle path;
        fixed (NativePathCommand* ptr = pathCommands)
        {
            path = NativeMethods.PathCreate(ptr, pathCommands.Length, fillRule);
        }

        if (path.IsInvalid)
        {
            path.Dispose();
            return;
        }

        _resources.Add(path);
        _ownedPaths.Add(path);
        AddCommand(new NativeCommand
        {
            Kind = NativeCommandKind.DrawPath,
            Flags = CreateShapeFlags(1u, renderOptions),
            Resource0 = path.DangerousGetHandle(),
            Fill = color.ToNative()
        });
    }

    public void FillNativePath(NativePathHandle path, Color color, RenderOptions renderOptions = default)
    {
        if (path.IsInvalid || color.A == 0)
        {
            return;
        }

        _resources.Add(path);
        AddCommand(new NativeCommand
        {
            Kind = NativeCommandKind.DrawPath,
            Flags = CreateShapeFlags(1u, renderOptions),
            Resource0 = path.DangerousGetHandle(),
            Fill = color.ToNative()
        });
    }

    public unsafe void StrokeSolidPath(
        ReadOnlySpan<NativePathCommand> pathCommands,
        Color color,
        double strokeWidth,
        NativeStrokeCap cap,
        NativeStrokeJoin join,
        double miterLimit,
        RenderOptions renderOptions = default)
    {
        if (color.A == 0 || strokeWidth <= 0 || pathCommands.IsEmpty)
        {
            return;
        }

        NativePathHandle path;
        fixed (NativePathCommand* ptr = pathCommands)
        {
            path = NativeMethods.PathCreate(ptr, pathCommands.Length, NativePathFillRule.NonZero);
        }

        if (path.IsInvalid)
        {
            path.Dispose();
            return;
        }

        var stroke = NativeStrokeCache.Get(cap, join, miterLimit, []);
        if (stroke.IsInvalid)
        {
            path.Dispose();
            return;
        }

        _resources.Add(path);
        _ownedPaths.Add(path);
        _resources.Add(stroke);
        AddCommand(new NativeCommand
        {
            Kind = NativeCommandKind.DrawPath,
            Flags = CreateShapeFlags(2u, renderOptions),
            Resource0 = path.DangerousGetHandle(),
            Resource2 = stroke.DangerousGetHandle(),
            Stroke = color.ToNative(),
            StrokeThickness = (float)strokeWidth
        });
    }

    public void StrokeNativePath(
        NativePathHandle path,
        Color color,
        double strokeWidth,
        NativeStrokeCap cap,
        NativeStrokeJoin join,
        double miterLimit,
        RenderOptions renderOptions = default)
    {
        if (path.IsInvalid || color.A == 0 || strokeWidth <= 0)
        {
            return;
        }

        var stroke = NativeStrokeCache.Get(cap, join, miterLimit, []);
        if (stroke.IsInvalid)
        {
            return;
        }

        _resources.Add(path);
        _resources.Add(stroke);
        AddCommand(new NativeCommand
        {
            Kind = NativeCommandKind.DrawPath,
            Flags = CreateShapeFlags(2u, renderOptions),
            Resource0 = path.DangerousGetHandle(),
            Resource2 = stroke.DangerousGetHandle(),
            Stroke = color.ToNative(),
            StrokeThickness = (float)strokeWidth
        });
    }

    public void DrawGeometry(IBrush? brush, IPen? pen, NativeGeometry geometry, RenderOptions renderOptions = default)
    {
        var hasFillPath = geometry.TryGetFillPath(out var fillPath);
        var hasStrokePath = geometry.TryGetStrokePath(out var strokePath);
        var bounds = NativePathGeometry.ResolveBounds(geometry.Bounds, fillPath, strokePath);
        DrawPath(brush, pen, bounds, hasFillPath ? fillPath : null, hasStrokePath ? strokePath : null, renderOptions);
    }

    public void SaveLayer(double opacity, Rect? bounds)
    {
        var flags = bounds.HasValue ? 1u : 0u;
        var rect = bounds.GetValueOrDefault();
        AddCommand(new NativeCommand
        {
            Kind = NativeCommandKind.SaveLayer,
            Flags = flags,
            Fill = new NativeColor(1, 1, 1, (float)Math.Clamp(opacity, 0, 1)),
            X0 = (float)rect.X,
            Y0 = (float)rect.Y,
            X1 = (float)rect.Width,
            Y1 = (float)rect.Height
        });
    }

    public void PushOpacityMask(IBrush mask, Rect bounds)
    {
        if (!BrushUtil.TryCreatePaint(mask, bounds, out var paint))
        {
            _opacityMaskLayers.Push(false);
            SaveLayer(mask.Opacity, bounds);
            return;
        }

        AddShader(paint);
        AddCommand(new NativeCommand
        {
            Kind = NativeCommandKind.PushOpacityMaskLayer,
            Flags = 1u,
            Resource1 = paint.ShaderHandle,
            Fill = paint.Color,
            X0 = (float)bounds.X,
            Y0 = (float)bounds.Y,
            X1 = (float)bounds.Width,
            Y1 = (float)bounds.Height
        });
        _opacityMaskLayers.Push(true);
    }

    public void PopOpacityMask()
    {
        if (_opacityMaskLayers.Count == 0)
        {
            return;
        }

        if (_opacityMaskLayers.Pop())
        {
            AddCommand(new NativeCommand { Kind = NativeCommandKind.PopOpacityMaskLayer });
        }
        else
        {
            Restore();
        }
    }

    public void DrawBitmap(IBitmapImpl source, double opacity, Rect sourceRect, Rect destRect, RenderOptions renderOptions)
    {
        if (source is not NativeWriteableBitmap native || opacity <= 0 || sourceRect.Width <= 0 || sourceRect.Height <= 0 || destRect.Width <= 0 || destRect.Height <= 0)
        {
            return;
        }

        var bitmap = native.NativeBitmap;
        _resources.Add(bitmap);
        AddCommand(new NativeCommand
        {
            Kind = NativeCommandKind.DrawBitmap,
            Flags = CreateBitmapFlags(renderOptions, sourceRect, destRect),
            Resource0 = bitmap.DangerousGetHandle(),
            Fill = new NativeColor(1, 1, 1, (float)Math.Clamp(opacity, 0, 1)),
            X0 = (float)sourceRect.X,
            Y0 = (float)sourceRect.Y,
            X1 = (float)sourceRect.Width,
            Y1 = (float)sourceRect.Height,
            X2 = (float)destRect.X,
            Y2 = (float)destRect.Y,
            X3 = (float)destRect.Width,
            Y3 = (float)destRect.Height
        });
    }

    private static uint CreateBitmapFlags(RenderOptions renderOptions, Rect sourceRect, Rect destRect)
    {
        var isUpscaling = destRect.Width > sourceRect.Width || destRect.Height > sourceRect.Height;
        var flags = ToNativeSampling(renderOptions.BitmapInterpolationMode, isUpscaling);
        flags |= ToNativeBlendMode(renderOptions.BitmapBlendingMode) << (int)BitmapBlendShift;
        if (renderOptions.EdgeMode != EdgeMode.Aliased)
        {
            flags |= BitmapAntiAliasFlag;
        }

        return flags & (BitmapSamplingMask | BitmapBlendMask | BitmapAntiAliasFlag);
    }

    private static uint CreateShapeFlags(uint baseFlags, RenderOptions renderOptions)
    {
        return renderOptions.EdgeMode == EdgeMode.Aliased
            ? baseFlags
            : baseFlags | ShapeAntiAliasFlag;
    }

    private static uint ToNativeSampling(BitmapInterpolationMode interpolationMode, bool isUpscaling)
    {
        return interpolationMode switch
        {
            BitmapInterpolationMode.None => 1u,
            BitmapInterpolationMode.Unspecified or BitmapInterpolationMode.LowQuality => 2u,
            BitmapInterpolationMode.MediumQuality => 3u,
            BitmapInterpolationMode.HighQuality => isUpscaling ? 4u : 3u,
            _ => 2u
        };
    }

    private static uint ToNativeBlendMode(BitmapBlendingMode blendingMode)
    {
        return blendingMode switch
        {
            BitmapBlendingMode.Unspecified or BitmapBlendingMode.SourceOver => 3u,
            BitmapBlendingMode.Source => 1u,
            BitmapBlendingMode.Destination => 2u,
            BitmapBlendingMode.DestinationOver => 4u,
            BitmapBlendingMode.SourceIn => 5u,
            BitmapBlendingMode.DestinationIn => 6u,
            BitmapBlendingMode.SourceOut => 7u,
            BitmapBlendingMode.DestinationOut => 8u,
            BitmapBlendingMode.SourceAtop => 9u,
            BitmapBlendingMode.DestinationAtop => 10u,
            BitmapBlendingMode.Xor => 11u,
            BitmapBlendingMode.Plus => 12u,
            BitmapBlendingMode.Screen => 14u,
            BitmapBlendingMode.Overlay => 15u,
            BitmapBlendingMode.Darken => 16u,
            BitmapBlendingMode.Lighten => 17u,
            BitmapBlendingMode.ColorDodge => 18u,
            BitmapBlendingMode.ColorBurn => 19u,
            BitmapBlendingMode.HardLight => 20u,
            BitmapBlendingMode.SoftLight => 21u,
            BitmapBlendingMode.Difference => 22u,
            BitmapBlendingMode.Exclusion => 23u,
            BitmapBlendingMode.Multiply => 24u,
            BitmapBlendingMode.Hue => 25u,
            BitmapBlendingMode.Saturation => 26u,
            BitmapBlendingMode.Color => 27u,
            BitmapBlendingMode.Luminosity => 28u,
            _ => 3u
        };
    }

    public void DrawGlyphRun(IBrush? foreground, IGlyphRunImpl glyphRun, TextOptions textOptions = default, RenderOptions renderOptions = default)
    {
        if (glyphRun is not NativeGlyphRun native || !BrushUtil.TryCreatePaint(foreground, glyphRun.Bounds, out var fill))
        {
            return;
        }

        var handle = native.GetNativeGlyphRunHandle(textOptions, renderOptions);
        if (handle.IsInvalid)
        {
            return;
        }

        AddShader(fill);
        _resources.Add(handle);
        AddCommand(new NativeCommand
        {
            Kind = NativeCommandKind.DrawGlyphRun,
            Resource0 = handle.DangerousGetHandle(),
            Resource1 = fill.ShaderHandle,
            Fill = fill.Color
        });
    }

    public void PushClip(Rect rect) => AddCommand(new NativeCommand
    {
        Kind = NativeCommandKind.PushClipRect,
        X0 = (float)rect.X,
        Y0 = (float)rect.Y,
        X1 = (float)rect.Width,
        Y1 = (float)rect.Height
    });

    public void PushClip(RoundedRect rect)
    {
        var radii = rect.RadiiTopLeft;
        AddCommand(new NativeCommand
        {
            Kind = radii.X > 0 || radii.Y > 0 ? NativeCommandKind.PushClipRoundRect : NativeCommandKind.PushClipRect,
            X0 = (float)rect.Rect.X,
            Y0 = (float)rect.Rect.Y,
            X1 = (float)rect.Rect.Width,
            Y1 = (float)rect.Rect.Height,
            X2 = (float)radii.X,
            Y2 = (float)radii.Y
        });
    }

    public unsafe void PushClip(IPlatformRenderInterfaceRegion region)
    {
        if (region is not NativeRegion nativeRegion || nativeRegion.Rects.Count == 0)
        {
            PushClip(default(Rect));
            return;
        }

        if (nativeRegion.Rects.Count == 1)
        {
            var rect = nativeRegion.Rects[0];
            PushClip(new Rect(rect.Left, rect.Top, rect.Right - rect.Left, rect.Bottom - rect.Top));
            return;
        }

        var commands = new NativePathCommand[nativeRegion.Rects.Count * 5];
        var commandIndex = 0;
        foreach (var rect in nativeRegion.Rects)
        {
            commands[commandIndex++] = NativePathCommands.MoveTo(new Point(rect.Left, rect.Top));
            commands[commandIndex++] = NativePathCommands.LineTo(new Point(rect.Right, rect.Top));
            commands[commandIndex++] = NativePathCommands.LineTo(new Point(rect.Right, rect.Bottom));
            commands[commandIndex++] = NativePathCommands.LineTo(new Point(rect.Left, rect.Bottom));
            commands[commandIndex++] = NativePathCommands.Close();
        }

        NativePathHandle path;
        fixed (NativePathCommand* ptr = commands)
        {
            path = NativeMethods.PathCreate(ptr, commandIndex, NativePathFillRule.NonZero);
        }

        if (path.IsInvalid)
        {
            path.Dispose();
            return;
        }

        _resources.Add(path);
        _ownedPaths.Add(path);
        AddCommand(new NativeCommand
        {
            Kind = NativeCommandKind.PushClipPath,
            Resource0 = path.DangerousGetHandle()
        });
    }

    public void PushGeometryClip(NativeGeometry geometry)
    {
        if (!geometry.TryGetFillPath(out var path) || path is null || path.IsInvalid)
        {
            return;
        }

        _resources.Add(path);
        AddCommand(new NativeCommand
        {
            Kind = NativeCommandKind.PushClipPath,
            Resource0 = path.DangerousGetHandle()
        });
    }

    private void AddPathCommand(NativePathHandle path, uint flags, NativePaintSource fill, NativeStrokeSource stroke, RenderOptions renderOptions)
    {
        var paint = (flags & 1u) != 0 ? fill : stroke.Paint;
        if ((flags & 2u) != 0)
        {
            AddStroke(stroke);
        }
        else
        {
            AddShader(paint);
        }

        _resources.Add(path);
        AddCommand(new NativeCommand
        {
            Kind = NativeCommandKind.DrawPath,
            Flags = CreateShapeFlags(flags, renderOptions),
            Resource0 = path.DangerousGetHandle(),
            Resource1 = paint.ShaderHandle,
            Resource2 = stroke.StrokeHandle,
            Fill = fill.Color,
            Stroke = stroke.Color,
            StrokeThickness = stroke.Thickness
        });
    }

    private void AddShapeCommand(NativeCommandKind kind, uint flags, NativePaintSource fill, NativeStrokeSource stroke, Rect rect, Vector radii, RenderOptions renderOptions)
    {
        var paint = (flags & 1u) != 0 ? fill : stroke.Paint;
        if ((flags & 2u) != 0)
        {
            AddStroke(stroke);
        }
        else
        {
            AddShader(paint);
        }

        AddCommand(new NativeCommand
        {
            Kind = kind,
            Flags = CreateShapeFlags(flags, renderOptions),
            Resource1 = paint.ShaderHandle,
            Resource2 = stroke.StrokeHandle,
            Fill = fill.Color,
            Stroke = stroke.Color,
            StrokeThickness = stroke.Thickness,
            X0 = (float)rect.X,
            Y0 = (float)rect.Y,
            X1 = (float)rect.Width,
            Y1 = (float)rect.Height,
            X2 = (float)radii.X,
            Y2 = (float)radii.Y
        });
    }

    private void AddShader(NativePaintSource paint)
    {
        if (paint.Shader is null)
        {
            return;
        }

        _resources.Add(paint.Shader);
        _ownedShaders.Add(paint.Shader);
    }

    public unsafe CommandBufferFlushResult Flush(NativeSessionHandle session)
    {
        ObjectDisposedException.ThrowIf(_disposed, this);

        if (_commandCount == 0)
        {
            return new CommandBufferFlushResult(0, 0, TimeSpan.Zero, 0);
        }

        var commandCount = _commandCount;
        fixed (NativeCommand* ptr = _commands)
        {
            var stopwatch = Stopwatch.StartNew();
            var nativeResult = 0;
            try
            {
                nativeResult = NativeMethods.SessionFlushCommands(session, ptr, commandCount);
                return new CommandBufferFlushResult(commandCount, 1, stopwatch.Elapsed, nativeResult);
            }
            finally
            {
                stopwatch.Stop();
                Clear();
            }
        }
    }

    public void Dispose()
    {
        if (_disposed)
        {
            return;
        }

        Clear();
        ArrayPool<NativeCommand>.Shared.Return(_commands);
        _commands = [];
        _disposed = true;
    }

    private void Clear()
    {
        _commandCount = 0;
        _resources.Clear();
        foreach (var shader in _ownedShaders)
        {
            shader.Dispose();
        }
        _ownedShaders.Clear();
        foreach (var stroke in _ownedStrokes)
        {
            stroke.Dispose();
        }
        _ownedStrokes.Clear();
        foreach (var path in _ownedPaths)
        {
            path.Dispose();
        }
        _ownedPaths.Clear();
    }

    private void AddCommand(NativeCommand command)
    {
        ObjectDisposedException.ThrowIf(_disposed, this);
        if (_commandCount == _commands.Length)
        {
            GrowCommands();
        }

        _commands[_commandCount++] = command;
    }

    private void GrowCommands()
    {
        var next = ArrayPool<NativeCommand>.Shared.Rent(_commands.Length * 2);
        Array.Copy(_commands, next, _commandCount);
        ArrayPool<NativeCommand>.Shared.Return(_commands);
        _commands = next;
    }

    private void AddStroke(NativeStrokeSource stroke)
    {
        AddShader(stroke.Paint);
        if (stroke.Stroke is null)
        {
            return;
        }

        _resources.Add(stroke.Stroke);
        if (stroke.OwnsStroke)
        {
            _ownedStrokes.Add(stroke.Stroke);
        }
    }
}

internal readonly record struct CommandBufferFlushResult(
    int CommandCount,
    int NativeTransitionCount,
    TimeSpan FlushElapsed,
    int NativeResult);

internal static class NativeConversions
{
    public static NativeColor ToNative(this Color color) => new(
        color.R / 255f,
        color.G / 255f,
        color.B / 255f,
        color.A / 255f);

    public static NativeMatrix ToNative(this Matrix matrix) => new()
    {
        M11 = matrix.M11,
        M12 = matrix.M12,
        M21 = matrix.M21,
        M22 = matrix.M22,
        M31 = matrix.M31,
        M32 = matrix.M32
    };
}

internal readonly struct NativePaintSource
{
    public NativePaintSource(NativeColor color, NativeShaderHandle? shader = null)
    {
        Color = color;
        Shader = shader;
        HasPaint = color.A > 0 || shader is not null;
    }

    public bool HasPaint { get; }
    public NativeColor Color { get; }
    public NativeShaderHandle? Shader { get; }
    public nint ShaderHandle => Shader is null ? 0 : Shader.DangerousGetHandle();
}

internal readonly struct NativeStrokeSource
{
    public NativeStrokeSource(NativePaintSource paint, double thickness, NativeStrokeHandle? stroke, bool ownsStroke)
    {
        Paint = paint;
        Thickness = (float)thickness;
        Stroke = stroke;
        OwnsStroke = ownsStroke;
        HasStroke = paint.HasPaint && thickness > 0 && stroke is { IsInvalid: false };
    }

    public bool HasStroke { get; }
    public bool OwnsStroke { get; }
    public NativePaintSource Paint { get; }
    public NativeColor Color => Paint.Color;
    public NativeShaderHandle? Shader => Paint.Shader;
    public nint ShaderHandle => Paint.ShaderHandle;
    public float Thickness { get; }
    public NativeStrokeHandle? Stroke { get; }
    public nint StrokeHandle => Stroke is null ? 0 : Stroke.DangerousGetHandle();
}

internal static class NativeStrokeCache
{
    private static readonly ConcurrentDictionary<StrokeStyleKey, NativeStrokeHandle> s_cache = new(new StrokeStyleKeyComparer());

    public static NativeStrokeHandle Get(
        NativeStrokeCap cap,
        NativeStrokeJoin join,
        double miterLimit,
        ReadOnlySpan<float> dashes,
        float dashOffset = 0)
    {
        var key = StrokeStyleKey.Create(cap, join, miterLimit, dashes, dashOffset);
        return s_cache.GetOrAdd(key, static key => CreateStroke(key));
    }

    private static unsafe NativeStrokeHandle CreateStroke(StrokeStyleKey key)
    {
        if (key.Dashes.Length == 0)
        {
            return NativeMethods.StrokeCreate(key.Cap, key.Join, key.MiterLimit, null, 0, 0);
        }

        fixed (float* ptr = key.Dashes)
        {
            return NativeMethods.StrokeCreate(key.Cap, key.Join, key.MiterLimit, ptr, key.Dashes.Length, key.DashOffset);
        }
    }

    private readonly record struct StrokeStyleKey(
        NativeStrokeCap Cap,
        NativeStrokeJoin Join,
        float MiterLimit,
        float[] Dashes,
        float DashOffset)
    {
        public static StrokeStyleKey Create(
            NativeStrokeCap cap,
            NativeStrokeJoin join,
            double miterLimit,
            ReadOnlySpan<float> dashes,
            float dashOffset)
        {
            var copiedDashes = dashes.IsEmpty ? [] : dashes.ToArray();
            return new StrokeStyleKey(cap, join, (float)Math.Max(0, miterLimit), copiedDashes, dashOffset);
        }
    }

    private sealed class StrokeStyleKeyComparer : IEqualityComparer<StrokeStyleKey>
    {
        public bool Equals(StrokeStyleKey x, StrokeStyleKey y)
        {
            if (x.Cap != y.Cap ||
                x.Join != y.Join ||
                x.MiterLimit != y.MiterLimit ||
                x.DashOffset != y.DashOffset ||
                x.Dashes.Length != y.Dashes.Length)
            {
                return false;
            }

            for (var i = 0; i < x.Dashes.Length; i++)
            {
                if (x.Dashes[i] != y.Dashes[i])
                {
                    return false;
                }
            }

            return true;
        }

        public int GetHashCode(StrokeStyleKey key)
        {
            var hash = new HashCode();
            hash.Add(key.Cap);
            hash.Add(key.Join);
            hash.Add(key.MiterLimit);
            hash.Add(key.DashOffset);
            foreach (var dash in key.Dashes)
            {
                hash.Add(dash);
            }

            return hash.ToHashCode();
        }
    }
}

internal static unsafe class BrushUtil
{
    private const float DuplicateStopEpsilon = 0.0001f;

    public static bool TryCreatePaint(IBrush? brush, Rect bounds, out NativePaintSource paint)
    {
        paint = default;
        if (brush is null)
        {
            return false;
        }

        if (brush is ISolidColorBrush solid)
        {
            var color = ToNativeColor(solid.Color, solid.Opacity);
            paint = new NativePaintSource(color);
            return paint.HasPaint;
        }

        if (brush is IConicGradientBrush conic)
        {
            return TryCreateConicGradientPaint(conic, bounds, out paint);
        }

        if (brush is ILinearGradientBrush linear)
        {
            return TryCreateLinearGradientPaint(linear, bounds, out paint);
        }

        if (brush is IRadialGradientBrush radial)
        {
            return TryCreateRadialGradientPaint(radial, bounds, out paint);
        }

        if (brush is ISceneBrush sceneBrush)
        {
            using var content = sceneBrush.CreateContent();
            return content is not null && TryCreateSceneBrushPaint(content, bounds, out paint);
        }

        if (brush is ISceneBrushContent sceneBrushContent)
        {
            return TryCreateSceneBrushPaint(sceneBrushContent, bounds, out paint);
        }

        if (brush is IImageBrush imageBrush)
        {
            return TryCreateImageBrushPaint(imageBrush, bounds, out paint);
        }

        return false;
    }

    public static bool TryGetFill(IBrush? brush, out NativeColor color, out bool hasFill)
    {
        color = default;
        hasFill = false;

        if (brush is ISolidColorBrush solid)
        {
            var c = solid.Color;
            var opacity = Math.Clamp(solid.Opacity, 0, 1);
            color = new NativeColor(c.R / 255f, c.G / 255f, c.B / 255f, (float)(c.A / 255f * opacity));
            hasFill = color.A > 0;
            return hasFill;
        }

        return false;
    }

    public static bool TryGetStroke(IPen? pen, out NativeColor color, out double thickness)
    {
        color = default;
        thickness = 0;

        if (pen?.Brush is ISolidColorBrush solid && pen.Thickness > 0)
        {
            var c = solid.Color;
            var opacity = Math.Clamp(solid.Opacity, 0, 1);
            color = new NativeColor(c.R / 255f, c.G / 255f, c.B / 255f, (float)(c.A / 255f * opacity));
            thickness = pen.Thickness;
            return color.A > 0;
        }

        return false;
    }

    public static bool TryGetStroke(IPen? pen, Rect bounds, out NativeStrokeSource stroke)
    {
        stroke = default;

        if (pen?.Brush is null || pen.Thickness <= 0)
        {
            return false;
        }

        if (!TryCreatePaint(pen.Brush, bounds, out var paint))
        {
            return false;
        }

        var strokeHandle = GetCachedStrokeStyle(pen);
        if (strokeHandle.IsInvalid)
        {
            return false;
        }

        stroke = new NativeStrokeSource(paint, pen.Thickness, strokeHandle, ownsStroke: false);
        return stroke.HasStroke;
    }

    internal static NativeStrokeHandle GetCachedStrokeStyle(IPen pen)
    {
        var dashes = CreateDashes(pen, out var dashOffset);
        return NativeStrokeCache.Get(
            ToNativeStrokeCap(pen.LineCap),
            ToNativeStrokeJoin(pen.LineJoin),
            pen.MiterLimit,
            dashes,
            dashOffset);
    }

    internal static NativeStrokeHandle? CreateStrokeStyle(IPen pen)
    {
        var dashes = CreateDashes(pen, out var dashOffset);
        if (dashes.Length == 0)
        {
            return NativeMethods.StrokeCreate(ToNativeStrokeCap(pen.LineCap), ToNativeStrokeJoin(pen.LineJoin), (float)pen.MiterLimit, null, 0, 0);
        }

        fixed (float* ptr = dashes)
        {
            return NativeMethods.StrokeCreate(ToNativeStrokeCap(pen.LineCap), ToNativeStrokeJoin(pen.LineJoin), (float)pen.MiterLimit, ptr, dashes.Length, dashOffset);
        }
    }

    private static float[] CreateDashes(IPen pen, out float dashOffset)
    {
        dashOffset = 0;
        if (pen.DashStyle?.Dashes is not { Count: > 0 } source)
        {
            return Array.Empty<float>();
        }

        var count = source.Count % 2 == 0 ? source.Count : source.Count * 2;
        var dashes = new float[count];
        for (var i = 0; i < count; ++i)
        {
            dashes[i] = Math.Max(0, (float)(source[i % source.Count] * pen.Thickness));
        }

        dashOffset = (float)(pen.DashStyle.Offset * pen.Thickness);
        return dashes;
    }

    private static NativeStrokeCap ToNativeStrokeCap(PenLineCap cap) =>
        cap switch
        {
            PenLineCap.Round => NativeStrokeCap.Round,
            PenLineCap.Square => NativeStrokeCap.Square,
            _ => NativeStrokeCap.Butt
        };

    private static NativeStrokeJoin ToNativeStrokeJoin(PenLineJoin join) =>
        join switch
        {
            PenLineJoin.Round => NativeStrokeJoin.Round,
            PenLineJoin.Bevel => NativeStrokeJoin.Bevel,
            _ => NativeStrokeJoin.Miter
        };

    private static bool TryCreateLinearGradientPaint(ILinearGradientBrush brush, Rect bounds, out NativePaintSource paint)
    {
        paint = default;
        var stops = CreateStops(brush);
        if (stops.Length == 0)
        {
            return false;
        }

        var start = brush.StartPoint.ToPixels(bounds);
        var end = brush.EndPoint.ToPixels(bounds);
        var hasLocalMatrix = TryCreateBrushLocalMatrix(brush, bounds, out var localMatrix);
        fixed (NativeGradientStop* ptr = stops)
        {
            var shader = hasLocalMatrix
                ? NativeMethods.ShaderCreateLinearWithMatrix(
                    (float)start.X,
                    (float)start.Y,
                    (float)end.X,
                    (float)end.Y,
                    ptr,
                    stops.Length,
                    ToNativeSpread(brush.SpreadMethod),
                    &localMatrix)
                : NativeMethods.ShaderCreateLinear(
                    (float)start.X,
                    (float)start.Y,
                    (float)end.X,
                    (float)end.Y,
                    ptr,
                    stops.Length,
                    ToNativeSpread(brush.SpreadMethod));

            if (shader.IsInvalid)
            {
                shader.Dispose();
                return false;
            }

            paint = new NativePaintSource(new NativeColor(1, 1, 1, 1), shader);
            return true;
        }
    }

    private static bool TryCreateRadialGradientPaint(IRadialGradientBrush brush, Rect bounds, out NativePaintSource paint)
    {
        paint = default;
        var stops = CreateStops(brush);
        if (stops.Length == 0)
        {
            return false;
        }

        var center = brush.Center.ToPixels(bounds);
        var origin = brush.GradientOrigin.ToPixels(bounds);
        var radiusX = Math.Abs(brush.RadiusX.ToValue(bounds.Width));
        var radiusY = Math.Abs(brush.RadiusY.ToValue(bounds.Height));

        if (radiusX <= 0 || radiusY <= 0)
        {
            return false;
        }

        var localMatrix = Matrix.Identity;
        if (!AreClose(radiusX, radiusY))
        {
            localMatrix =
                Matrix.CreateTranslation(-center.X, -center.Y) *
                Matrix.CreateScale(1, radiusY / radiusX) *
                Matrix.CreateTranslation(center.X, center.Y);

            if (!AreClose(origin.X, center.X) || !AreClose(origin.Y, center.Y))
            {
                origin = new Point(origin.X, (origin.Y - center.Y) * radiusX / radiusY + center.Y);
            }
        }

        if (TryCreateBrushTransform(brush, bounds, out var brushTransform))
        {
            localMatrix = localMatrix.IsIdentity ? brushTransform : localMatrix * brushTransform;
        }

        var hasLocalMatrix = !localMatrix.IsIdentity;
        var nativeLocalMatrix = localMatrix.ToNative();

        fixed (NativeGradientStop* ptr = stops)
        {
            var shader = hasLocalMatrix
                ? NativeMethods.ShaderCreateRadialWithMatrix(
                    (float)center.X,
                    (float)center.Y,
                    (float)origin.X,
                    (float)origin.Y,
                    (float)radiusX,
                    ptr,
                    stops.Length,
                    ToNativeSpread(brush.SpreadMethod),
                    &nativeLocalMatrix)
                : NativeMethods.ShaderCreateRadial(
                    (float)center.X,
                    (float)center.Y,
                    (float)origin.X,
                    (float)origin.Y,
                    (float)radiusX,
                    ptr,
                    stops.Length,
                    ToNativeSpread(brush.SpreadMethod));

            if (shader.IsInvalid)
            {
                shader.Dispose();
                return false;
            }

            paint = new NativePaintSource(new NativeColor(1, 1, 1, 1), shader);
            return true;
        }
    }

    private static bool TryCreateConicGradientPaint(IConicGradientBrush brush, Rect bounds, out NativePaintSource paint)
    {
        paint = default;
        var stops = CreateStops(brush);
        if (stops.Length == 0)
        {
            return false;
        }

        var center = brush.Center.ToPixels(bounds);
        var localMatrix = Matrix.CreateRotation(Matrix.ToRadians(brush.Angle - 90), center);

        if (TryCreateBrushTransform(brush, bounds, out var brushTransform))
        {
            localMatrix = localMatrix.Prepend(brushTransform);
        }

        var nativeLocalMatrix = localMatrix.ToNative();
        fixed (NativeGradientStop* ptr = stops)
        {
            var shader = NativeMethods.ShaderCreateSweep(
                (float)center.X,
                (float)center.Y,
                ptr,
                stops.Length,
                ToNativeSpread(brush.SpreadMethod),
                &nativeLocalMatrix);

            if (shader.IsInvalid)
            {
                shader.Dispose();
                return false;
            }

            paint = new NativePaintSource(new NativeColor(1, 1, 1, 1), shader);
            return true;
        }
    }

    private static bool TryCreateImageBrushPaint(IImageBrush brush, Rect bounds, out NativePaintSource paint)
    {
        paint = default;
        var source = TryGetNativeImageBrushSource(brush.Source);
        if (source is null || source.NativeBitmap.IsInvalid || bounds.Width <= 0 || bounds.Height <= 0)
        {
            return false;
        }

        var contentSize = new Size(
            source.PixelSize.Width * 96.0 / source.Dpi.X,
            source.PixelSize.Height * 96.0 / source.Dpi.Y);
        return TryCreateTileBrushPaint(brush, source, contentSize, bounds, out paint);
    }

    private static bool TryCreateSceneBrushPaint(ISceneBrushContent content, Rect bounds, out NativePaintSource paint)
    {
        paint = default;
        var contentRect = content.Rect;
        if (contentRect.Width <= 0 || contentRect.Height <= 0 || bounds.Width <= 0 || bounds.Height <= 0)
        {
            return false;
        }

        var calc = new TileBrushCalculation(content.Brush, contentRect.Size, bounds.Size);
        if (!calc.IsValid)
        {
            return false;
        }

        using var intermediate = new NativeRenderTargetBitmap(ToPixelSize(calc.IntermediateSize), SkiaNativePlatform.DefaultDpi, new SkiaNativeOptions());
        using (var context = intermediate.CreateDrawingContext())
        {
            var contentTransform = contentRect.Position == default
                ? calc.IntermediateTransform
                : Matrix.CreateTranslation(-contentRect.X, -contentRect.Y) * calc.IntermediateTransform;

            context.Clear(Colors.Transparent);
            context.PushClip(calc.IntermediateClip);
            content.Render(context, contentTransform);
            context.PopClip();
        }

        return TryCreateTileBrushShaderPaint(content.Brush, intermediate, calc, bounds, out paint);
    }

    private static bool TryCreateTileBrushPaint(ITileBrush brush, NativeWriteableBitmap source, Size contentSize, Rect bounds, out NativePaintSource paint)
    {
        paint = default;
        var calc = new TileBrushCalculation(brush, contentSize, bounds.Size);
        if (!calc.IsValid)
        {
            return false;
        }

        using var intermediate = new NativeRenderTargetBitmap(ToPixelSize(calc.IntermediateSize), SkiaNativePlatform.DefaultDpi, new SkiaNativeOptions());
        using (var context = intermediate.CreateDrawingContext())
        {
            var sourceRect = new Rect(contentSize);
            var targetRect = new Rect(contentSize);

            context.Clear(Colors.Transparent);
            context.PushClip(calc.IntermediateClip);
            context.Transform = calc.IntermediateTransform;
            context.DrawBitmap(source, 1, sourceRect, targetRect);
            context.Transform = Matrix.Identity;
            context.PopClip();
        }

        return TryCreateTileBrushShaderPaint(brush, intermediate, calc, bounds, out paint);
    }

    private static bool TryCreateTileBrushShaderPaint(ITileBrush brush, NativeWriteableBitmap intermediate, TileBrushCalculation calc, Rect bounds, out NativePaintSource paint)
    {
        paint = default;
        var paintTransform =
            brush.TileMode == TileMode.None
                ? Matrix.Identity
                : Matrix.CreateTranslation(-calc.DestinationRect.X, -calc.DestinationRect.Y);

        if (TryCreateBrushTransform(brush, bounds, out var brushTransform))
        {
            paintTransform = paintTransform * brushTransform;
        }

        if (brush.DestinationRect.Unit == RelativeUnit.Relative)
        {
            paintTransform = paintTransform * Matrix.CreateTranslation(bounds.X, bounds.Y);
        }

        var nativeMatrix = paintTransform.ToNative();
        var shader = NativeMethods.ShaderCreateBitmap(
            intermediate.NativeBitmap,
            ToNativeTileModeX(brush.TileMode),
            ToNativeTileModeY(brush.TileMode),
            &nativeMatrix);

        if (shader.IsInvalid)
        {
            shader.Dispose();
            return false;
        }

        paint = new NativePaintSource(new NativeColor(1, 1, 1, (float)Math.Clamp(brush.Opacity, 0, 1)), shader);
        return true;
    }

    private static bool TryCreateBrushLocalMatrix(IBrush brush, Rect bounds, out NativeMatrix matrix)
    {
        matrix = default;
        if (!TryCreateBrushTransform(brush, bounds, out var transform))
        {
            return false;
        }

        matrix = transform.ToNative();
        return true;
    }

    private static NativeWriteableBitmap? TryGetNativeImageBrushSource(IImageBrushSource? source)
    {
        if (source is null)
        {
            return null;
        }

        if (TryGetRefItem(source, "Bitmap", out var bitmap))
        {
            return bitmap;
        }

        var platformImpl = source
            .GetType()
            .GetProperty("PlatformImpl", System.Reflection.BindingFlags.Instance | System.Reflection.BindingFlags.NonPublic)
            ?.GetValue(source);

        return TryGetRefItem(platformImpl, "Item", out bitmap) ? bitmap : null;
    }

    private static bool TryGetRefItem(object? source, string propertyName, out NativeWriteableBitmap? bitmap)
    {
        bitmap = null;
        if (source is null)
        {
            return false;
        }

        var value = source
            .GetType()
            .GetProperty(propertyName, System.Reflection.BindingFlags.Instance | System.Reflection.BindingFlags.Public | System.Reflection.BindingFlags.NonPublic)
            ?.GetValue(source);

        if (value is NativeWriteableBitmap direct)
        {
            bitmap = direct;
            return true;
        }

        bitmap = value
            ?.GetType()
            .GetProperty("Item", System.Reflection.BindingFlags.Instance | System.Reflection.BindingFlags.Public | System.Reflection.BindingFlags.NonPublic)
            ?.GetValue(value) as NativeWriteableBitmap;

        return bitmap is not null;
    }

    private static bool TryCreateBrushTransform(IBrush brush, Rect bounds, out Matrix transform)
    {
        transform = Matrix.Identity;
        var brushTransform = brush.Transform?.Value ?? Matrix.Identity;
        if (brushTransform.IsIdentity)
        {
            return false;
        }

        var origin = brush.TransformOrigin.ToPixels(bounds);
        transform =
            Matrix.CreateTranslation(-origin.X, -origin.Y) *
            brushTransform *
            Matrix.CreateTranslation(origin.X, origin.Y);

        return !transform.IsIdentity;
    }

    private static NativeGradientStop[] CreateStops(IGradientBrush brush)
    {
        if (brush.GradientStops.Count == 0)
        {
            return [];
        }

        var stops = brush.GradientStops
            .Select(x => new
            {
                Offset = (float)Math.Clamp(x.Offset, 0, 1),
                Color = ToNativeColor(x.Color, brush.Opacity)
            })
            .OrderBy(x => x.Offset)
            .ToList();

        var normalized = new List<NativeGradientStop>(Math.Max(stops.Count, 2));
        var lastOffset = -1f;
        foreach (var stop in stops)
        {
            var offset = stop.Offset;
            if (normalized.Count > 0 && offset <= lastOffset)
            {
                offset = Math.Min(1, lastOffset + DuplicateStopEpsilon);
            }

            if (normalized.Count > 0 && offset <= lastOffset)
            {
                continue;
            }

            normalized.Add(new NativeGradientStop
            {
                Offset = offset,
                Color = stop.Color
            });
            lastOffset = offset;
        }

        if (normalized.Count == 1)
        {
            var only = normalized[0];
            normalized[0] = new NativeGradientStop { Offset = 0, Color = only.Color };
            normalized.Add(new NativeGradientStop { Offset = 1, Color = only.Color });
        }

        return normalized.ToArray();
    }

    private static NativeColor ToNativeColor(Color color, double opacity) => new(
        color.R / 255f,
        color.G / 255f,
        color.B / 255f,
        (float)(color.A / 255f * Math.Clamp(opacity, 0, 1)));

    private static NativeGradientSpreadMethod ToNativeSpread(GradientSpreadMethod spreadMethod) => spreadMethod switch
    {
        GradientSpreadMethod.Reflect => NativeGradientSpreadMethod.Reflect,
        GradientSpreadMethod.Repeat => NativeGradientSpreadMethod.Repeat,
        _ => NativeGradientSpreadMethod.Pad
    };

    private static NativeTileMode ToNativeTileModeX(TileMode tileMode) => tileMode switch
    {
        TileMode.None => NativeTileMode.Decal,
        TileMode.FlipX or TileMode.FlipXY => NativeTileMode.Mirror,
        _ => NativeTileMode.Repeat
    };

    private static NativeTileMode ToNativeTileModeY(TileMode tileMode) => tileMode switch
    {
        TileMode.None => NativeTileMode.Decal,
        TileMode.FlipY or TileMode.FlipXY => NativeTileMode.Mirror,
        _ => NativeTileMode.Repeat
    };

    private static PixelSize ToPixelSize(Size size) => new(
        Math.Max(1, (int)Math.Ceiling(size.Width)),
        Math.Max(1, (int)Math.Ceiling(size.Height)));

    private static bool AreClose(double left, double right) => Math.Abs(left - right) < 0.000001;

    private readonly record struct TileBrushCalculation(
        Rect SourceRect,
        Rect DestinationRect,
        Size IntermediateSize,
        Rect IntermediateClip,
        Matrix IntermediateTransform,
        bool IsValid)
    {
        public TileBrushCalculation(ITileBrush brush, Size contentSize, Size targetSize)
            : this(Create(brush, contentSize, targetSize))
        {
        }

        private TileBrushCalculation(TileBrushCalculation source)
            : this(source.SourceRect, source.DestinationRect, source.IntermediateSize, source.IntermediateClip, source.IntermediateTransform, source.IsValid)
        {
        }

        private static TileBrushCalculation Create(ITileBrush brush, Size contentSize, Size targetSize)
        {
            var sourceRect = brush.SourceRect.ToPixels(contentSize);
            var destinationRect = brush.DestinationRect.ToPixels(targetSize);
            if (sourceRect.Width <= 0 || sourceRect.Height <= 0 || destinationRect.Width <= 0 || destinationRect.Height <= 0)
            {
                return new TileBrushCalculation(default, default, default, default, Matrix.Identity, false);
            }

            var scale = brush.Stretch.CalculateScaling(destinationRect.Size, sourceRect.Size);
            var translate = CalculateTranslate(brush.AlignmentX, brush.AlignmentY, sourceRect.Size * scale, destinationRect.Size);
            var transform =
                Matrix.CreateTranslation(-sourceRect.Position) *
                Matrix.CreateScale(scale) *
                Matrix.CreateTranslation(translate);

            Rect drawRect;
            Size intermediateSize;
            if (brush.TileMode == TileMode.None)
            {
                drawRect = destinationRect;
                intermediateSize = targetSize;
                transform *= Matrix.CreateTranslation(destinationRect.Position);
            }
            else
            {
                drawRect = new Rect(destinationRect.Size);
                intermediateSize = destinationRect.Size;
            }

            return new TileBrushCalculation(sourceRect, destinationRect, intermediateSize, drawRect, transform, true);
        }

        private static Vector CalculateTranslate(AlignmentX alignmentX, AlignmentY alignmentY, Size sourceSize, Size destinationSize)
        {
            var x = alignmentX switch
            {
                AlignmentX.Center => (destinationSize.Width - sourceSize.Width) / 2,
                AlignmentX.Right => destinationSize.Width - sourceSize.Width,
                _ => 0
            };

            var y = alignmentY switch
            {
                AlignmentY.Center => (destinationSize.Height - sourceSize.Height) / 2,
                AlignmentY.Bottom => destinationSize.Height - sourceSize.Height,
                _ => 0
            };

            return new Vector(x, y);
        }
    }
}
