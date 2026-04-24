using System.Runtime.InteropServices;
using Avalonia;
using Avalonia.Media;

namespace SkiaNative.Avalonia;

/// <summary>
/// Provides direct access to the active SkiaNative render session for custom render-thread draw operations.
/// </summary>
public interface ISkiaNativeApiLeaseFeature
{
    /// <summary>
    /// Opens a short-lived command encoder for the current drawing context.
    /// </summary>
    SkiaNativeApiLease Lease();
}

/// <summary>
/// A short-lived direct SkiaNative drawing lease. Dispose it before the custom draw operation returns.
/// </summary>
public sealed class SkiaNativeApiLease : IDisposable
{
    internal SkiaNativeApiLease(SkiaNativeDirectCanvas canvas)
    {
        Canvas = canvas;
    }

    public SkiaNativeDirectCanvas Canvas { get; }

    public void Dispose() => Canvas.Dispose();
}

/// <summary>
/// Direct command encoder that bypasses Avalonia geometry and pen rendering.
/// </summary>
public sealed class SkiaNativeDirectCanvas : IDisposable
{
    private readonly NativeSessionHandle _session;
    private readonly CommandBuffer _commands;
    private readonly Action<CommandBufferFlushResult> _reportFlush;
    private bool _disposed;

    internal SkiaNativeDirectCanvas(NativeSessionHandle session, int initialCapacity, Action<CommandBufferFlushResult> reportFlush)
    {
        _session = session;
        _commands = new CommandBuffer(initialCapacity);
        _reportFlush = reportFlush;
    }

    public void Save()
    {
        ThrowIfDisposed();
        _commands.Save();
    }

    public void Restore()
    {
        ThrowIfDisposed();
        _commands.Restore();
    }

    public void SetTransform(Matrix matrix)
    {
        ThrowIfDisposed();
        _commands.SetTransform(matrix);
    }

    public void Clear(Color color)
    {
        ThrowIfDisposed();
        _commands.Clear(color);
    }

    public void PushClip(Rect rect)
    {
        ThrowIfDisposed();
        _commands.PushClip(rect);
    }

    public void FillRectangle(Color color, Rect rect)
    {
        ThrowIfDisposed();
        _commands.FillSolidRect(color, rect);
    }

    public void DrawLine(Color color, double thickness, Point p1, Point p2)
    {
        ThrowIfDisposed();
        _commands.DrawSolidLine(color, thickness, p1, p2);
    }

    public void StrokePath(
        ReadOnlySpan<SkiaNativePathCommand> commands,
        Color color,
        double strokeWidth,
        SkiaNativeStrokeCap cap = SkiaNativeStrokeCap.Butt,
        SkiaNativeStrokeJoin join = SkiaNativeStrokeJoin.Miter,
        double miterLimit = 10)
    {
        ThrowIfDisposed();
        var nativeCommands = MemoryMarshal.Cast<SkiaNativePathCommand, NativePathCommand>(commands);
        _commands.StrokeSolidPath(
            nativeCommands,
            color,
            strokeWidth,
            (NativeStrokeCap)cap,
            (NativeStrokeJoin)join,
            miterLimit);
    }

    public void FillPath(
        ReadOnlySpan<SkiaNativePathCommand> commands,
        Color color,
        SkiaNativePathFillRule fillRule = SkiaNativePathFillRule.NonZero)
    {
        ThrowIfDisposed();
        var nativeCommands = MemoryMarshal.Cast<SkiaNativePathCommand, NativePathCommand>(commands);
        _commands.FillSolidPath(nativeCommands, color, (NativePathFillRule)fillRule);
    }

    public SkiaNativeDirectFlushResult Flush()
    {
        ThrowIfDisposed();
        var result = _commands.Flush(_session);
        _reportFlush(result);
        return result.ToDirectResult();
    }

    public void Dispose()
    {
        if (_disposed)
        {
            return;
        }

        Flush();
        _disposed = true;
    }

    private void ThrowIfDisposed()
    {
        ObjectDisposedException.ThrowIf(_disposed, this);
    }
}

public readonly record struct SkiaNativeDirectFlushResult(
    int CommandCount,
    int NativeTransitionCount,
    TimeSpan FlushElapsed,
    int NativeResult);

public enum SkiaNativeStrokeCap : uint
{
    Butt = 0,
    Round = 1,
    Square = 2,
}

public enum SkiaNativeStrokeJoin : uint
{
    Miter = 0,
    Round = 1,
    Bevel = 2,
}

public enum SkiaNativePathFillRule : uint
{
    NonZero = 0,
    EvenOdd = 1,
}

public enum SkiaNativePathCommandKind : uint
{
    MoveTo = 1,
    LineTo = 2,
    QuadTo = 3,
    CubicTo = 4,
    ArcTo = 5,
    Close = 6,
}

/// <summary>
/// Path command layout intentionally mirrors the native SkiaNative ABI for zero-copy command encoding.
/// </summary>
[StructLayout(LayoutKind.Sequential)]
public readonly struct SkiaNativePathCommand
{
    public const uint LargeArc = 1u;
    public const uint Clockwise = 1u << 1;

    public readonly SkiaNativePathCommandKind Kind;
    public readonly uint Flags;
    public readonly float X0;
    public readonly float Y0;
    public readonly float X1;
    public readonly float Y1;
    public readonly float X2;
    public readonly float Y2;
    public readonly float X3;
    public readonly float Y3;

    public SkiaNativePathCommand(
        SkiaNativePathCommandKind kind,
        uint flags,
        float x0,
        float y0,
        float x1 = 0,
        float y1 = 0,
        float x2 = 0,
        float y2 = 0,
        float x3 = 0,
        float y3 = 0)
    {
        Kind = kind;
        Flags = flags;
        X0 = x0;
        Y0 = y0;
        X1 = x1;
        Y1 = y1;
        X2 = x2;
        Y2 = y2;
        X3 = x3;
        Y3 = y3;
    }

    public static SkiaNativePathCommand MoveTo(float x, float y) =>
        new(SkiaNativePathCommandKind.MoveTo, 0, x, y);

    public static SkiaNativePathCommand MoveTo(Point point) =>
        MoveTo((float)point.X, (float)point.Y);

    public static SkiaNativePathCommand LineTo(float x, float y) =>
        new(SkiaNativePathCommandKind.LineTo, 0, x, y);

    public static SkiaNativePathCommand LineTo(Point point) =>
        LineTo((float)point.X, (float)point.Y);

    public static SkiaNativePathCommand QuadTo(float controlX, float controlY, float endX, float endY) =>
        new(SkiaNativePathCommandKind.QuadTo, 0, controlX, controlY, endX, endY);

    public static SkiaNativePathCommand QuadTo(Point control, Point end) =>
        QuadTo((float)control.X, (float)control.Y, (float)end.X, (float)end.Y);

    public static SkiaNativePathCommand CubicTo(float control1X, float control1Y, float control2X, float control2Y, float endX, float endY) =>
        new(SkiaNativePathCommandKind.CubicTo, 0, control1X, control1Y, control2X, control2Y, endX, endY);

    public static SkiaNativePathCommand CubicTo(Point control1, Point control2, Point end) =>
        CubicTo((float)control1.X, (float)control1.Y, (float)control2.X, (float)control2.Y, (float)end.X, (float)end.Y);

    public static SkiaNativePathCommand ArcTo(float radiusX, float radiusY, float rotationAngle, float endX, float endY, bool isLargeArc, bool clockwise) =>
        new(
            SkiaNativePathCommandKind.ArcTo,
            (isLargeArc ? LargeArc : 0u) | (clockwise ? Clockwise : 0u),
            radiusX,
            radiusY,
            rotationAngle,
            0,
            endX,
            endY);

    public static SkiaNativePathCommand Close() =>
        new(SkiaNativePathCommandKind.Close, 0, 0, 0);
}

internal sealed class SkiaNativeApiLeaseFeature : ISkiaNativeApiLeaseFeature
{
    private readonly NativeSessionHandle _session;
    private readonly int _initialCapacity;
    private readonly Func<CommandBufferFlushResult> _flushPendingCommands;
    private readonly Action<CommandBufferFlushResult> _reportFlush;

    public SkiaNativeApiLeaseFeature(
        NativeSessionHandle session,
        int initialCapacity,
        Func<CommandBufferFlushResult> flushPendingCommands,
        Action<CommandBufferFlushResult> reportFlush)
    {
        _session = session;
        _initialCapacity = initialCapacity;
        _flushPendingCommands = flushPendingCommands;
        _reportFlush = reportFlush;
    }

    public SkiaNativeApiLease Lease()
    {
        _reportFlush(_flushPendingCommands());
        return new SkiaNativeApiLease(new SkiaNativeDirectCanvas(_session, _initialCapacity, _reportFlush));
    }
}

internal static class SkiaNativeDirectFlushResultExtensions
{
    public static SkiaNativeDirectFlushResult ToDirectResult(this CommandBufferFlushResult result) =>
        new(result.CommandCount, result.NativeTransitionCount, result.FlushElapsed, result.NativeResult);
}
