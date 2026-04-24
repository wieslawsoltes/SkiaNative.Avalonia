using Avalonia;
using Avalonia.Media;
using Avalonia.Media.Imaging;
using Avalonia.Platform;
using System.Buffers;
using System.Collections.Concurrent;
using System.Diagnostics;
using System.Runtime.CompilerServices;
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
    private readonly PooledReferenceList<SafeHandle> _resources;
    private readonly PooledReferenceList<NativeShaderHandle> _ownedShaders;
    private readonly PooledReferenceList<NativeStrokeHandle> _ownedStrokes;
    private readonly PooledReferenceList<NativePathHandle> _ownedPaths;
    private readonly TileBrushIntermediateCache _tileBrushCache = new();
    private readonly Stack<bool> _opacityMaskLayers = new();

    public CommandBuffer(int capacity)
    {
        _commands = ArrayPool<NativeCommand>.Shared.Rent(Math.Max(capacity, 16));
        _resources = new PooledReferenceList<SafeHandle>(Math.Min(Math.Max(capacity, 16), 256));
        _ownedShaders = new PooledReferenceList<NativeShaderHandle>();
        _ownedStrokes = new PooledReferenceList<NativeStrokeHandle>();
        _ownedPaths = new PooledReferenceList<NativePathHandle>();
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
        if (!BrushUtil.TryGetStroke(pen, new Rect(p1, p2).Normalize(), out var stroke, _tileBrushCache))
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
        BrushUtil.TryCreatePaint(brush, rect.Rect, out var fill, _tileBrushCache);
        BrushUtil.TryGetStroke(pen, rect.Rect, out var stroke, _tileBrushCache);

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
        BrushUtil.TryCreatePaint(brush, rect, out var fill, _tileBrushCache);
        BrushUtil.TryGetStroke(pen, rect, out var stroke, _tileBrushCache);

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
        BrushUtil.TryCreatePaint(brush, bounds, out var fill, _tileBrushCache);
        BrushUtil.TryGetStroke(pen, bounds, out var stroke, _tileBrushCache);

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
        if (!BrushUtil.TryCreatePaint(mask, bounds, out var paint, _tileBrushCache))
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
        if (glyphRun is not NativeGlyphRun native || !BrushUtil.TryCreatePaint(foreground, glyphRun.Bounds, out var fill, _tileBrushCache))
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
        if (paint.OwnsShader)
        {
            _ownedShaders.Add(paint.Shader);
        }
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
            var transitionCount = 0;
            try
            {
                var index = 0;
                while (index < commandCount)
                {
                    var kind = _commands[index].Kind;
                    if (kind is NativeCommandKind.DrawBitmap or NativeCommandKind.DrawGlyphRun)
                    {
                        var batchCount = CountConsecutiveCommands(index, kind, commandCount);
                        if (batchCount > 1)
                        {
                            nativeResult = kind == NativeCommandKind.DrawBitmap
                                ? FlushBitmapBatch(session, ptr + index, batchCount)
                                : FlushGlyphRunBatch(session, ptr + index, batchCount);
                            transitionCount++;
                            index += batchCount;
                            continue;
                        }
                    }

                    var genericStart = index++;
                    while (index < commandCount)
                    {
                        kind = _commands[index].Kind;
                        if (kind is NativeCommandKind.DrawBitmap or NativeCommandKind.DrawGlyphRun &&
                            CountConsecutiveCommands(index, kind, commandCount) > 1)
                        {
                            break;
                        }

                        index++;
                    }

                    nativeResult = NativeMethods.SessionFlushCommands(session, ptr + genericStart, index - genericStart);
                    transitionCount++;
                }

                return new CommandBufferFlushResult(commandCount, transitionCount, stopwatch.Elapsed, nativeResult);
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
        _resources.Dispose();
        _ownedShaders.Dispose();
        _ownedStrokes.Dispose();
        _ownedPaths.Dispose();
        _tileBrushCache.Dispose();
        _disposed = true;
    }

    private void Clear()
    {
        _commandCount = 0;
        _resources.Clear();
        for (var i = 0; i < _ownedShaders.Count; i++)
        {
            _ownedShaders[i].Dispose();
        }
        _ownedShaders.Clear();
        for (var i = 0; i < _ownedStrokes.Count; i++)
        {
            _ownedStrokes[i].Dispose();
        }
        _ownedStrokes.Clear();
        for (var i = 0; i < _ownedPaths.Count; i++)
        {
            _ownedPaths[i].Dispose();
        }
        _ownedPaths.Clear();
        _tileBrushCache.Clear();
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

    private int CountConsecutiveCommands(int start, NativeCommandKind kind, int commandCount)
    {
        var end = start + 1;
        while (end < commandCount && _commands[end].Kind == kind)
        {
            end++;
        }

        return end - start;
    }

    private static unsafe int FlushBitmapBatch(NativeSessionHandle session, NativeCommand* source, int count)
    {
        var rented = ArrayPool<NativeBitmapCommand>.Shared.Rent(count);
        try
        {
            for (var i = 0; i < count; i++)
            {
                var command = source[i];
                rented[i] = new NativeBitmapCommand
                {
                    Bitmap = command.Resource0,
                    Flags = command.Flags,
                    Color = command.Fill,
                    X0 = command.X0,
                    Y0 = command.Y0,
                    X1 = command.X1,
                    Y1 = command.Y1,
                    X2 = command.X2,
                    Y2 = command.Y2,
                    X3 = command.X3,
                    Y3 = command.Y3
                };
            }

            fixed (NativeBitmapCommand* ptr = rented)
            {
                return NativeMethods.SessionDrawBitmaps(session, ptr, count);
            }
        }
        finally
        {
            ArrayPool<NativeBitmapCommand>.Shared.Return(rented);
        }
    }

    private static unsafe int FlushGlyphRunBatch(NativeSessionHandle session, NativeCommand* source, int count)
    {
        var rented = ArrayPool<NativeGlyphRunCommand>.Shared.Rent(count);
        try
        {
            for (var i = 0; i < count; i++)
            {
                var command = source[i];
                rented[i] = new NativeGlyphRunCommand
                {
                    GlyphRun = command.Resource0,
                    Shader = command.Resource1,
                    Color = command.Fill
                };
            }

            fixed (NativeGlyphRunCommand* ptr = rented)
            {
                return NativeMethods.SessionDrawGlyphRuns(session, ptr, count);
            }
        }
        finally
        {
            ArrayPool<NativeGlyphRunCommand>.Shared.Return(rented);
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
    public NativePaintSource(NativeColor color, NativeShaderHandle? shader = null, bool ownsShader = true)
    {
        Color = color;
        Shader = shader;
        OwnsShader = ownsShader;
        HasPaint = color.A > 0 || shader is not null;
    }

    public bool HasPaint { get; }
    public bool OwnsShader { get; }
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

internal static unsafe class NativeShaderCache
{
    private static readonly ConcurrentDictionary<GradientShaderKey, NativeShaderHandle> s_gradientCache = new(new GradientShaderKeyComparer());

    public static NativeShaderHandle GetLinear(
        float x0,
        float y0,
        float x1,
        float y1,
        NativeGradientStop[] stops,
        NativeGradientSpreadMethod spreadMethod,
        bool hasLocalMatrix,
        NativeMatrix localMatrix)
    {
        var key = new GradientShaderKey(
            GradientShaderKind.Linear,
            x0,
            y0,
            x1,
            y1,
            0,
            0,
            spreadMethod,
            hasLocalMatrix,
            localMatrix,
            stops);
        return GetOrCreate(key);
    }

    public static NativeShaderHandle GetRadial(
        float centerX,
        float centerY,
        float originX,
        float originY,
        float radius,
        NativeGradientStop[] stops,
        NativeGradientSpreadMethod spreadMethod,
        bool hasLocalMatrix,
        NativeMatrix localMatrix)
    {
        var key = new GradientShaderKey(
            GradientShaderKind.Radial,
            centerX,
            centerY,
            originX,
            originY,
            radius,
            0,
            spreadMethod,
            hasLocalMatrix,
            localMatrix,
            stops);
        return GetOrCreate(key);
    }

    public static NativeShaderHandle GetSweep(
        float centerX,
        float centerY,
        NativeGradientStop[] stops,
        NativeGradientSpreadMethod spreadMethod,
        bool hasLocalMatrix,
        NativeMatrix localMatrix)
    {
        var key = new GradientShaderKey(
            GradientShaderKind.Sweep,
            centerX,
            centerY,
            0,
            0,
            0,
            0,
            spreadMethod,
            hasLocalMatrix,
            localMatrix,
            stops);
        return GetOrCreate(key);
    }

    private static NativeShaderHandle GetOrCreate(GradientShaderKey key)
    {
        if (s_gradientCache.TryGetValue(key, out var cached) && !cached.IsInvalid)
        {
            return cached;
        }

        var created = CreateShader(key);
        if (created.IsInvalid)
        {
            return created;
        }

        cached = s_gradientCache.GetOrAdd(key, created);
        if (!ReferenceEquals(cached, created))
        {
            created.Dispose();
        }

        return cached;
    }

    private static NativeShaderHandle CreateShader(GradientShaderKey key)
    {
        fixed (NativeGradientStop* ptr = key.Stops)
        {
            var matrix = key.LocalMatrix;
            var matrixPtr = key.HasLocalMatrix ? &matrix : null;
            return key.Kind switch
            {
                GradientShaderKind.Linear when key.HasLocalMatrix => NativeMethods.ShaderCreateLinearWithMatrix(
                    key.A,
                    key.B,
                    key.C,
                    key.D,
                    ptr,
                    key.Stops.Length,
                    key.SpreadMethod,
                    matrixPtr),
                GradientShaderKind.Linear => NativeMethods.ShaderCreateLinear(
                    key.A,
                    key.B,
                    key.C,
                    key.D,
                    ptr,
                    key.Stops.Length,
                    key.SpreadMethod),
                GradientShaderKind.Radial when key.HasLocalMatrix => NativeMethods.ShaderCreateRadialWithMatrix(
                    key.A,
                    key.B,
                    key.C,
                    key.D,
                    key.E,
                    ptr,
                    key.Stops.Length,
                    key.SpreadMethod,
                    matrixPtr),
                GradientShaderKind.Radial => NativeMethods.ShaderCreateRadial(
                    key.A,
                    key.B,
                    key.C,
                    key.D,
                    key.E,
                    ptr,
                    key.Stops.Length,
                    key.SpreadMethod),
                GradientShaderKind.Sweep => NativeMethods.ShaderCreateSweep(
                    key.A,
                    key.B,
                    ptr,
                    key.Stops.Length,
                    key.SpreadMethod,
                    matrixPtr),
                _ => throw new InvalidOperationException("Unknown native gradient shader kind.")
            };
        }
    }

    private enum GradientShaderKind
    {
        Linear,
        Radial,
        Sweep
    }

    private readonly record struct GradientShaderKey(
        GradientShaderKind Kind,
        float A,
        float B,
        float C,
        float D,
        float E,
        float F,
        NativeGradientSpreadMethod SpreadMethod,
        bool HasLocalMatrix,
        NativeMatrix LocalMatrix,
        NativeGradientStop[] Stops);

    private sealed class GradientShaderKeyComparer : IEqualityComparer<GradientShaderKey>
    {
        public bool Equals(GradientShaderKey x, GradientShaderKey y)
        {
            if (x.Kind != y.Kind ||
                x.A != y.A ||
                x.B != y.B ||
                x.C != y.C ||
                x.D != y.D ||
                x.E != y.E ||
                x.F != y.F ||
                x.SpreadMethod != y.SpreadMethod ||
                x.HasLocalMatrix != y.HasLocalMatrix ||
                !Equals(x.LocalMatrix, y.LocalMatrix) ||
                x.Stops.Length != y.Stops.Length)
            {
                return false;
            }

            for (var i = 0; i < x.Stops.Length; i++)
            {
                if (x.Stops[i].Offset != y.Stops[i].Offset ||
                    !Equals(x.Stops[i].Color, y.Stops[i].Color))
                {
                    return false;
                }
            }

            return true;
        }

        public int GetHashCode(GradientShaderKey key)
        {
            var hash = new HashCode();
            hash.Add(key.Kind);
            hash.Add(key.A);
            hash.Add(key.B);
            hash.Add(key.C);
            hash.Add(key.D);
            hash.Add(key.E);
            hash.Add(key.F);
            hash.Add(key.SpreadMethod);
            hash.Add(key.HasLocalMatrix);
            Add(ref hash, key.LocalMatrix);
            foreach (var stop in key.Stops)
            {
                hash.Add(stop.Offset);
                Add(ref hash, stop.Color);
            }

            return hash.ToHashCode();
        }

        private static bool Equals(NativeMatrix x, NativeMatrix y) =>
            x.M11 == y.M11 &&
            x.M12 == y.M12 &&
            x.M21 == y.M21 &&
            x.M22 == y.M22 &&
            x.M31 == y.M31 &&
            x.M32 == y.M32;

        private static bool Equals(NativeColor x, NativeColor y) =>
            x.R == y.R &&
            x.G == y.G &&
            x.B == y.B &&
            x.A == y.A;

        private static void Add(ref HashCode hash, NativeMatrix matrix)
        {
            hash.Add(matrix.M11);
            hash.Add(matrix.M12);
            hash.Add(matrix.M21);
            hash.Add(matrix.M22);
            hash.Add(matrix.M31);
            hash.Add(matrix.M32);
        }

        private static void Add(ref HashCode hash, NativeColor color)
        {
            hash.Add(color.R);
            hash.Add(color.G);
            hash.Add(color.B);
            hash.Add(color.A);
        }
    }
}

internal sealed class TileBrushIntermediateCache : IDisposable
{
    private readonly Dictionary<TileBrushCacheKey, TileBrushCacheEntry> _entries = new(TileBrushCacheKeyComparer.Instance);

    public unsafe bool TryCreatePaint(
        TileBrushCacheKey key,
        NativeTileMode tileX,
        NativeTileMode tileY,
        NativeMatrix localMatrix,
        float opacity,
        Func<NativeRenderTargetBitmap> createIntermediate,
        out NativePaintSource paint)
    {
        paint = default;
        if (!_entries.TryGetValue(key, out var entry))
        {
            var intermediate = createIntermediate();
            var matrix = localMatrix;
            var shader = NativeMethods.ShaderCreateBitmap(intermediate.NativeBitmap, tileX, tileY, &matrix);
            if (shader.IsInvalid)
            {
                shader.Dispose();
                intermediate.Dispose();
                return false;
            }

            entry = new TileBrushCacheEntry(intermediate, shader);
            _entries.Add(key, entry);
        }

        paint = new NativePaintSource(new NativeColor(1, 1, 1, opacity), entry.Shader, ownsShader: false);
        return true;
    }

    public void Clear()
    {
        foreach (var entry in _entries.Values)
        {
            entry.Dispose();
        }

        _entries.Clear();
    }

    public void Dispose() => Clear();
}

internal sealed class TileBrushCacheEntry : IDisposable
{
    public TileBrushCacheEntry(NativeRenderTargetBitmap intermediate, NativeShaderHandle shader)
    {
        Intermediate = intermediate;
        Shader = shader;
    }

    public NativeRenderTargetBitmap Intermediate { get; }
    public NativeShaderHandle Shader { get; }

    public void Dispose()
    {
        Shader.Dispose();
        Intermediate.Dispose();
    }
}

internal readonly record struct TileBrushCacheKey(
    TileBrushCacheKind Kind,
    object? Identity,
    nint SourceHandle,
    int SourceVersion,
    PixelSize SourcePixelSize,
    Vector SourceDpi,
    Size ContentSize,
    Size TargetSize,
    Rect SourceRect,
    Rect DestinationRect,
    Size IntermediateSize,
    Rect IntermediateClip,
    Matrix IntermediateTransform,
    Matrix PaintTransform,
    TileMode TileMode,
    Stretch Stretch,
    AlignmentX AlignmentX,
    AlignmentY AlignmentY,
    double Opacity);

internal enum TileBrushCacheKind
{
    Image,
    Scene
}

internal sealed class TileBrushCacheKeyComparer : IEqualityComparer<TileBrushCacheKey>
{
    public static readonly TileBrushCacheKeyComparer Instance = new();

    public bool Equals(TileBrushCacheKey x, TileBrushCacheKey y) =>
        x.Kind == y.Kind &&
        ReferenceEquals(x.Identity, y.Identity) &&
        x.SourceHandle == y.SourceHandle &&
        x.SourceVersion == y.SourceVersion &&
        x.SourcePixelSize == y.SourcePixelSize &&
        x.SourceDpi == y.SourceDpi &&
        x.ContentSize == y.ContentSize &&
        x.TargetSize == y.TargetSize &&
        x.SourceRect == y.SourceRect &&
        x.DestinationRect == y.DestinationRect &&
        x.IntermediateSize == y.IntermediateSize &&
        x.IntermediateClip == y.IntermediateClip &&
        x.IntermediateTransform == y.IntermediateTransform &&
        x.PaintTransform == y.PaintTransform &&
        x.TileMode == y.TileMode &&
        x.Stretch == y.Stretch &&
        x.AlignmentX == y.AlignmentX &&
        x.AlignmentY == y.AlignmentY &&
        x.Opacity == y.Opacity;

    public int GetHashCode(TileBrushCacheKey key)
    {
        var hash = new HashCode();
        hash.Add(key.Kind);
        hash.Add(key.Identity is null ? 0 : RuntimeHelpers.GetHashCode(key.Identity));
        hash.Add(key.SourceHandle);
        hash.Add(key.SourceVersion);
        hash.Add(key.SourcePixelSize);
        hash.Add(key.SourceDpi);
        hash.Add(key.ContentSize);
        hash.Add(key.TargetSize);
        hash.Add(key.SourceRect);
        hash.Add(key.DestinationRect);
        hash.Add(key.IntermediateSize);
        hash.Add(key.IntermediateClip);
        hash.Add(key.IntermediateTransform);
        hash.Add(key.PaintTransform);
        hash.Add(key.TileMode);
        hash.Add(key.Stretch);
        hash.Add(key.AlignmentX);
        hash.Add(key.AlignmentY);
        hash.Add(key.Opacity);
        return hash.ToHashCode();
    }
}

internal static unsafe class BrushUtil
{
    private const float DuplicateStopEpsilon = 0.0001f;

    public static bool TryCreatePaint(IBrush? brush, Rect bounds, out NativePaintSource paint, TileBrushIntermediateCache? tileBrushCache = null)
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
            return content is not null && TryCreateSceneBrushPaint(content, bounds, out paint, tileBrushCache, sceneBrush);
        }

        if (brush is ISceneBrushContent sceneBrushContent)
        {
            return TryCreateSceneBrushPaint(sceneBrushContent, bounds, out paint, tileBrushCache, sceneBrushContent);
        }

        if (brush is IImageBrush imageBrush)
        {
            return TryCreateImageBrushPaint(imageBrush, bounds, out paint, tileBrushCache);
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

    public static bool TryGetStroke(IPen? pen, Rect bounds, out NativeStrokeSource stroke, TileBrushIntermediateCache? tileBrushCache = null)
    {
        stroke = default;

        if (pen?.Brush is null || pen.Thickness <= 0)
        {
            return false;
        }

        if (!TryCreatePaint(pen.Brush, bounds, out var paint, tileBrushCache))
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
        var shader = NativeShaderCache.GetLinear(
            (float)start.X,
            (float)start.Y,
            (float)end.X,
            (float)end.Y,
            stops,
            ToNativeSpread(brush.SpreadMethod),
            hasLocalMatrix,
            localMatrix);

        if (shader.IsInvalid)
        {
            shader.Dispose();
            return false;
        }

        paint = new NativePaintSource(new NativeColor(1, 1, 1, 1), shader, ownsShader: false);
        return true;
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

        var shader = NativeShaderCache.GetRadial(
            (float)center.X,
            (float)center.Y,
            (float)origin.X,
            (float)origin.Y,
            (float)radiusX,
            stops,
            ToNativeSpread(brush.SpreadMethod),
            hasLocalMatrix,
            nativeLocalMatrix);

        if (shader.IsInvalid)
        {
            shader.Dispose();
            return false;
        }

        paint = new NativePaintSource(new NativeColor(1, 1, 1, 1), shader, ownsShader: false);
        return true;
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
        var shader = NativeShaderCache.GetSweep(
            (float)center.X,
            (float)center.Y,
            stops,
            ToNativeSpread(brush.SpreadMethod),
            hasLocalMatrix: true,
            nativeLocalMatrix);

        if (shader.IsInvalid)
        {
            shader.Dispose();
            return false;
        }

        paint = new NativePaintSource(new NativeColor(1, 1, 1, 1), shader, ownsShader: false);
        return true;
    }

    private static bool TryCreateImageBrushPaint(IImageBrush brush, Rect bounds, out NativePaintSource paint, TileBrushIntermediateCache? tileBrushCache)
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
        return TryCreateTileBrushPaint(brush, source, contentSize, bounds, out paint, tileBrushCache);
    }

    private static bool TryCreateSceneBrushPaint(ISceneBrushContent content, Rect bounds, out NativePaintSource paint, TileBrushIntermediateCache? tileBrushCache, object cacheIdentity)
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

        var paintTransform = CreateTileBrushPaintTransform(content.Brush, calc, bounds);
        var nativeMatrix = paintTransform.ToNative();
        var opacity = (float)Math.Clamp(content.Brush.Opacity, 0, 1);
        if (tileBrushCache is not null)
        {
            var key = CreateTileBrushCacheKey(
                TileBrushCacheKind.Scene,
                cacheIdentity,
                sourceHandle: 0,
                sourceVersion: 0,
                sourcePixelSize: default,
                sourceDpi: default,
                contentRect.Size,
                bounds.Size,
                content.Brush,
                calc,
                paintTransform);
            return tileBrushCache.TryCreatePaint(
                key,
                ToNativeTileModeX(content.Brush.TileMode),
                ToNativeTileModeY(content.Brush.TileMode),
                nativeMatrix,
                opacity,
                () => RenderSceneBrushIntermediate(content, contentRect, calc),
                out paint);
        }

        using var intermediate = RenderSceneBrushIntermediate(content, contentRect, calc);
        return TryCreateTileBrushShaderPaint(content.Brush, intermediate, nativeMatrix, opacity, out paint);
    }

    private static bool TryCreateTileBrushPaint(ITileBrush brush, NativeWriteableBitmap source, Size contentSize, Rect bounds, out NativePaintSource paint, TileBrushIntermediateCache? tileBrushCache)
    {
        paint = default;
        var calc = new TileBrushCalculation(brush, contentSize, bounds.Size);
        if (!calc.IsValid)
        {
            return false;
        }

        var paintTransform = CreateTileBrushPaintTransform(brush, calc, bounds);
        var nativeMatrix = paintTransform.ToNative();
        var opacity = (float)Math.Clamp(brush.Opacity, 0, 1);
        if (tileBrushCache is not null)
        {
            var key = CreateTileBrushCacheKey(
                TileBrushCacheKind.Image,
                source,
                source.NativeBitmap.DangerousGetHandle(),
                source.Version,
                source.PixelSize,
                source.Dpi,
                contentSize,
                bounds.Size,
                brush,
                calc,
                paintTransform);
            return tileBrushCache.TryCreatePaint(
                key,
                ToNativeTileModeX(brush.TileMode),
                ToNativeTileModeY(brush.TileMode),
                nativeMatrix,
                opacity,
                () => RenderImageBrushIntermediate(source, contentSize, calc),
                out paint);
        }

        using var intermediate = RenderImageBrushIntermediate(source, contentSize, calc);
        return TryCreateTileBrushShaderPaint(brush, intermediate, nativeMatrix, opacity, out paint);
    }

    private static bool TryCreateTileBrushShaderPaint(ITileBrush brush, NativeWriteableBitmap intermediate, NativeMatrix nativeMatrix, float opacity, out NativePaintSource paint)
    {
        paint = default;
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

        paint = new NativePaintSource(new NativeColor(1, 1, 1, opacity), shader);
        return true;
    }

    private static NativeRenderTargetBitmap RenderSceneBrushIntermediate(ISceneBrushContent content, Rect contentRect, TileBrushCalculation calc)
    {
        var intermediate = new NativeRenderTargetBitmap(ToPixelSize(calc.IntermediateSize), SkiaNativePlatform.DefaultDpi, new SkiaNativeOptions());
        try
        {
            using var context = intermediate.CreateDrawingContext();
            var contentTransform = contentRect.Position == default
                ? calc.IntermediateTransform
                : Matrix.CreateTranslation(-contentRect.X, -contentRect.Y) * calc.IntermediateTransform;

            context.Clear(Colors.Transparent);
            context.PushClip(calc.IntermediateClip);
            content.Render(context, contentTransform);
            context.PopClip();
            return intermediate;
        }
        catch
        {
            intermediate.Dispose();
            throw;
        }
    }

    private static NativeRenderTargetBitmap RenderImageBrushIntermediate(NativeWriteableBitmap source, Size contentSize, TileBrushCalculation calc)
    {
        var intermediate = new NativeRenderTargetBitmap(ToPixelSize(calc.IntermediateSize), SkiaNativePlatform.DefaultDpi, new SkiaNativeOptions());
        try
        {
            using var context = intermediate.CreateDrawingContext();
            var sourceRect = new Rect(contentSize);
            var targetRect = new Rect(contentSize);

            context.Clear(Colors.Transparent);
            context.PushClip(calc.IntermediateClip);
            context.Transform = calc.IntermediateTransform;
            context.DrawBitmap(source, 1, sourceRect, targetRect);
            context.Transform = Matrix.Identity;
            context.PopClip();
            return intermediate;
        }
        catch
        {
            intermediate.Dispose();
            throw;
        }
    }

    private static Matrix CreateTileBrushPaintTransform(ITileBrush brush, TileBrushCalculation calc, Rect bounds)
    {
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

        return paintTransform;
    }

    private static TileBrushCacheKey CreateTileBrushCacheKey(
        TileBrushCacheKind kind,
        object? identity,
        nint sourceHandle,
        int sourceVersion,
        PixelSize sourcePixelSize,
        Vector sourceDpi,
        Size contentSize,
        Size targetSize,
        ITileBrush brush,
        TileBrushCalculation calc,
        Matrix paintTransform) =>
        new(
            kind,
            identity,
            sourceHandle,
            sourceVersion,
            sourcePixelSize,
            sourceDpi,
            contentSize,
            targetSize,
            calc.SourceRect,
            calc.DestinationRect,
            calc.IntermediateSize,
            calc.IntermediateClip,
            calc.IntermediateTransform,
            paintTransform,
            brush.TileMode,
            brush.Stretch,
            brush.AlignmentX,
            brush.AlignmentY,
            brush.Opacity);

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
        var count = brush.GradientStops.Count;
        if (count == 0)
        {
            return [];
        }

        var stops = new NativeGradientStop[count];
        for (var i = 0; i < count; i++)
        {
            var stop = brush.GradientStops[i];
            stops[i] = new NativeGradientStop
            {
                Offset = (float)Math.Clamp(stop.Offset, 0, 1),
                Color = ToNativeColor(stop.Color, brush.Opacity)
            };
        }

        Array.Sort(stops, static (left, right) => left.Offset.CompareTo(right.Offset));

        var writeIndex = 0;
        var lastOffset = -1f;
        foreach (var stop in stops)
        {
            var offset = stop.Offset;
            if (writeIndex > 0 && offset <= lastOffset)
            {
                offset = Math.Min(1, lastOffset + DuplicateStopEpsilon);
            }

            if (writeIndex > 0 && offset <= lastOffset)
            {
                continue;
            }

            stops[writeIndex++] = new NativeGradientStop
            {
                Offset = offset,
                Color = stop.Color
            };
            lastOffset = offset;
        }

        if (writeIndex == 0)
        {
            return [];
        }

        if (writeIndex == 1)
        {
            var only = stops[0];
            return
            [
                new NativeGradientStop { Offset = 0, Color = only.Color },
                new NativeGradientStop { Offset = 1, Color = only.Color }
            ];
        }

        if (writeIndex == stops.Length)
        {
            return stops;
        }

        return stops.AsSpan(0, writeIndex).ToArray();
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
