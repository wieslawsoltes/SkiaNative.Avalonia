using System.Buffers;
using System.Diagnostics;
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
    private const uint ShapeAntiAliasFlag = 1u << 16;

    private readonly NativeSessionHandle _session;
    private readonly NativeContextHandle? _context;
    private readonly CommandBuffer _commands;
    private readonly Action<CommandBufferFlushResult> _reportFlush;
    private bool _disposed;

    internal SkiaNativeDirectCanvas(NativeSessionHandle session, NativeContextHandle? context, int initialCapacity, Action<CommandBufferFlushResult> reportFlush)
    {
        _session = session;
        _context = context;
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

    public void ConcatTransform(Matrix matrix)
    {
        ThrowIfDisposed();
        _commands.ConcatTransform(matrix);
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

    public void StrokePath(
        SkiaNativePath path,
        Color color,
        double strokeWidth,
        SkiaNativeStrokeCap cap = SkiaNativeStrokeCap.Butt,
        SkiaNativeStrokeJoin join = SkiaNativeStrokeJoin.Miter,
        double miterLimit = 10)
    {
        ArgumentNullException.ThrowIfNull(path);
        ThrowIfDisposed();
        _commands.StrokeNativePath(
            path.NativeHandle,
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

    public void FillPath(SkiaNativePath path, Color color)
    {
        ArgumentNullException.ThrowIfNull(path);
        ThrowIfDisposed();
        _commands.FillNativePath(path.NativeHandle, color);
    }

    public unsafe SkiaNativeDirectFlushResult StrokePaths(
        ReadOnlySpan<SkiaNativePathStroke> strokes,
        SkiaNativeStrokeCap cap = SkiaNativeStrokeCap.Butt,
        SkiaNativeStrokeJoin join = SkiaNativeStrokeJoin.Miter,
        double miterLimit = 10,
        bool antiAlias = true) =>
        StrokePaths(strokes, 1, cap, join, miterLimit, antiAlias);

    public unsafe SkiaNativeDirectFlushResult StrokePaths(
        ReadOnlySpan<SkiaNativePathStroke> strokes,
        double strokeWidthScale,
        SkiaNativeStrokeCap cap = SkiaNativeStrokeCap.Butt,
        SkiaNativeStrokeJoin join = SkiaNativeStrokeJoin.Miter,
        double miterLimit = 10,
        bool antiAlias = true)
    {
        ThrowIfDisposed();
        if (strokes.IsEmpty || strokeWidthScale <= 0 || !double.IsFinite(strokeWidthScale))
        {
            return default;
        }

        var pending = FlushCommandBuffer();
        var strokeHandle = NativeStrokeCache.Get((NativeStrokeCap)cap, (NativeStrokeJoin)join, miterLimit, []);
        if (strokeHandle.IsInvalid)
        {
            return pending.ToDirectResult();
        }

        var rented = ArrayPool<NativePathStrokeCommand>.Shared.Rent(strokes.Length);
        var commandCount = 0;
        try
        {
            for (var i = 0; i < strokes.Length; i++)
            {
                var stroke = strokes[i];
                var strokeWidth = stroke.Width * strokeWidthScale;
                if (stroke.Path is null || stroke.Color.A == 0 || strokeWidth <= 0 || !double.IsFinite(strokeWidth))
                {
                    continue;
                }

                rented[commandCount++] = new NativePathStrokeCommand
                {
                    Path = stroke.Path.NativeHandle.DangerousGetHandle(),
                    Stroke = strokeHandle.DangerousGetHandle(),
                    Color = stroke.Color.ToNative(),
                    StrokeThickness = (float)strokeWidth,
                    Flags = antiAlias ? ShapeAntiAliasFlag : 0
                };
            }

            if (commandCount == 0)
            {
                return pending.ToDirectResult();
            }

            fixed (NativePathStrokeCommand* ptr = rented)
            {
                var stopwatch = Stopwatch.StartNew();
                var nativeResult = NativeMethods.SessionDrawPathStrokes(_session, ptr, commandCount);
                stopwatch.Stop();
                var batch = new CommandBufferFlushResult(commandCount, 1, stopwatch.Elapsed, nativeResult);
                _reportFlush(batch);
                return CombineFlushResults(pending, batch).ToDirectResult();
            }
        }
        finally
        {
            ArrayPool<NativePathStrokeCommand>.Shared.Return(rented);
        }
    }

    public unsafe SkiaNativeDirectFlushResult StrokePathStream(
        ReadOnlySpan<SkiaNativePathStreamElement> elements,
        double strokeWidthScale,
        SkiaNativeStrokeCap cap = SkiaNativeStrokeCap.Butt,
        SkiaNativeStrokeJoin join = SkiaNativeStrokeJoin.Miter,
        double miterLimit = 10,
        bool antiAlias = true)
    {
        ThrowIfDisposed();
        if (elements.IsEmpty || strokeWidthScale <= 0 || !double.IsFinite(strokeWidthScale))
        {
            return default;
        }

        var pending = FlushCommandBuffer();
        var strokeHandle = NativeStrokeCache.Get((NativeStrokeCap)cap, (NativeStrokeJoin)join, miterLimit, []);
        if (strokeHandle.IsInvalid)
        {
            return pending.ToDirectResult();
        }

        fixed (SkiaNativePathStreamElement* ptr = elements)
        {
            var stopwatch = Stopwatch.StartNew();
            var nativeResult = NativeMethods.SessionDrawPathStream(
                _session,
                ptr,
                elements.Length,
                strokeHandle,
                (float)strokeWidthScale,
                antiAlias ? ShapeAntiAliasFlag : 0);
            stopwatch.Stop();
            var batch = new CommandBufferFlushResult(elements.Length, 1, stopwatch.Elapsed, nativeResult);
            _reportFlush(batch);
            return CombineFlushResults(pending, batch).ToDirectResult();
        }
    }

    public SkiaNativeDirectFlushResult DrawPathStreamMesh(SkiaNativePathStreamMesh mesh)
    {
        ArgumentNullException.ThrowIfNull(mesh);
        ThrowIfDisposed();

        var pending = FlushCommandBuffer();
        var stopwatch = Stopwatch.StartNew();
        var nativeResult = NativeMethods.SessionDrawPathStreamMesh(_session, mesh.NativeHandle);
        stopwatch.Stop();
        var batch = new CommandBufferFlushResult(mesh.ElementCount, 1, stopwatch.Elapsed, nativeResult);
        _reportFlush(batch);
        return CombineFlushResults(pending, batch).ToDirectResult();
    }

    public unsafe SkiaNativeMesh CreateMesh<TVertex>(
        SkiaNativeMeshSpecification specification,
        SkiaNativeMeshMode mode,
        ReadOnlySpan<TVertex> vertices,
        ReadOnlySpan<ushort> indices,
        ReadOnlySpan<byte> uniforms,
        Rect bounds)
        where TVertex : unmanaged
    {
        ArgumentNullException.ThrowIfNull(specification);
        ThrowIfDisposed();
        if (vertices.Length < 3)
        {
            throw new ArgumentException("A mesh requires at least three vertices.", nameof(vertices));
        }

        var vertexBytes = MemoryMarshal.AsBytes(vertices);
        return CreateMesh(specification, mode, vertexBytes, vertices.Length, indices, uniforms, bounds);
    }

    public unsafe SkiaNativeMesh CreateMesh(
        SkiaNativeMeshSpecification specification,
        SkiaNativeMeshMode mode,
        ReadOnlySpan<byte> vertexBytes,
        int vertexCount,
        ReadOnlySpan<ushort> indices,
        ReadOnlySpan<byte> uniforms,
        Rect bounds)
    {
        ArgumentNullException.ThrowIfNull(specification);
        ThrowIfDisposed();
        if (vertexBytes.IsEmpty || vertexCount < 3)
        {
            throw new ArgumentException("A mesh requires at least three vertices.", nameof(vertexBytes));
        }

        if (vertexBytes.Length % 4 != 0)
        {
            throw new ArgumentException("Vertex data size must be 4-byte aligned.", nameof(vertexBytes));
        }

        if (!indices.IsEmpty && indices.Length < 3)
        {
            throw new ArgumentException("An indexed mesh requires at least three indices.", nameof(indices));
        }

        if (!indices.IsEmpty && (indices.Length * sizeof(ushort)) % 4 != 0)
        {
            throw new ArgumentException("Index data size must be 4-byte aligned for GPU updates.", nameof(indices));
        }

        fixed (byte* vertexPtr = vertexBytes)
        fixed (ushort* indexPtr = indices)
        fixed (byte* uniformPtr = uniforms)
        {
            var handle = NativeMethods.MeshCreate(
                GetContextHandle(),
                specification.NativeHandle,
                (NativeMeshMode)mode,
                vertexPtr,
                vertexBytes.Length,
                vertexCount,
                indices.IsEmpty ? null : indexPtr,
                indices.Length,
                uniforms.IsEmpty ? null : uniformPtr,
                uniforms.Length,
                (float)bounds.Left,
                (float)bounds.Top,
                (float)bounds.Right,
                (float)bounds.Bottom);

            if (handle.IsInvalid)
            {
                handle.Dispose();
                throw new InvalidOperationException("Native Skia mesh creation failed.");
            }

            GC.KeepAlive(_context);
            return new SkiaNativeMesh(handle, _context, specification);
        }
    }

    public SkiaNativeDirectFlushResult DrawMesh(SkiaNativeMesh mesh, Color color, bool antiAlias = true)
    {
        ArgumentNullException.ThrowIfNull(mesh);
        ThrowIfDisposed();

        var pending = FlushCommandBuffer();
        var stopwatch = Stopwatch.StartNew();
        var nativeResult = NativeMethods.SessionDrawMesh(
            _session,
            mesh.NativeHandle,
            0,
            color.ToNative(),
            antiAlias ? ShapeAntiAliasFlag : 0);
        stopwatch.Stop();
        var batch = new CommandBufferFlushResult(1, 1, stopwatch.Elapsed, nativeResult);
        _reportFlush(batch);
        return CombineFlushResults(pending, batch).ToDirectResult();
    }

    public unsafe SkiaNativeDirectFlushResult FillPaths(ReadOnlySpan<SkiaNativePathFill> fills)
    {
        ThrowIfDisposed();
        if (fills.IsEmpty)
        {
            return default;
        }

        var pending = FlushCommandBuffer();
        var rented = ArrayPool<NativePathFillCommand>.Shared.Rent(fills.Length);
        var commandCount = 0;
        try
        {
            for (var i = 0; i < fills.Length; i++)
            {
                var fill = fills[i];
                if (fill.Path is null || fill.Color.A == 0)
                {
                    continue;
                }

                rented[commandCount++] = new NativePathFillCommand
                {
                    Path = fill.Path.NativeHandle.DangerousGetHandle(),
                    Color = fill.Color.ToNative(),
                    Flags = ShapeAntiAliasFlag
                };
            }

            if (commandCount == 0)
            {
                return pending.ToDirectResult();
            }

            fixed (NativePathFillCommand* ptr = rented)
            {
                var stopwatch = Stopwatch.StartNew();
                var nativeResult = NativeMethods.SessionDrawPathFills(_session, ptr, commandCount);
                stopwatch.Stop();
                var batch = new CommandBufferFlushResult(commandCount, 1, stopwatch.Elapsed, nativeResult);
                _reportFlush(batch);
                return CombineFlushResults(pending, batch).ToDirectResult();
            }
        }
        finally
        {
            ArrayPool<NativePathFillCommand>.Shared.Return(rented);
        }
    }

    public SkiaNativeDirectFlushResult Flush()
    {
        ThrowIfDisposed();
        return FlushCommandBuffer().ToDirectResult();
    }

    public void Dispose()
    {
        if (_disposed)
        {
            return;
        }

        Flush();
        _commands.Dispose();
        _disposed = true;
    }

    private void ThrowIfDisposed()
    {
        ObjectDisposedException.ThrowIf(_disposed, this);
    }

    private CommandBufferFlushResult FlushCommandBuffer()
    {
        var result = _commands.Flush(_session);
        _reportFlush(result);
        return result;
    }

    private nint GetContextHandle() => _context?.DangerousGetHandle() ?? 0;

    private static CommandBufferFlushResult CombineFlushResults(CommandBufferFlushResult first, CommandBufferFlushResult second) =>
        new(
            first.CommandCount + second.CommandCount,
            first.NativeTransitionCount + second.NativeTransitionCount,
            first.FlushElapsed + second.FlushElapsed,
            first.NativeResult != 0 ? first.NativeResult : second.NativeResult);
}

public readonly record struct SkiaNativeDirectFlushResult(
    int CommandCount,
    int NativeTransitionCount,
    TimeSpan FlushElapsed,
    int NativeResult);

/// <summary>
/// One reusable native path stroke for specialized bulk direct rendering.
/// </summary>
public readonly record struct SkiaNativePathStroke(SkiaNativePath Path, Color Color, double Width);

/// <summary>
/// One reusable native path fill for specialized bulk direct rendering.
/// </summary>
public readonly record struct SkiaNativePathFill(SkiaNativePath Path, Color Color);

/// <summary>
/// Reusable native vertex mesh built from streamed path segments for hot non-antialiased direct rendering.
/// </summary>
public sealed class SkiaNativePathStreamMesh : IDisposable
{
    private NativePathStreamMeshHandle? _handle;

    private SkiaNativePathStreamMesh(NativePathStreamMeshHandle handle, int elementCount)
    {
        _handle = handle;
        ElementCount = elementCount;
    }

    public bool IsDisposed => _handle is null;

    public int ElementCount { get; }

    internal NativePathStreamMeshHandle NativeHandle
    {
        get
        {
            ObjectDisposedException.ThrowIf(_handle is null, this);
            return _handle;
        }
    }

    public static unsafe SkiaNativePathStreamMesh Create(
        ReadOnlySpan<SkiaNativePathStreamElement> elements,
        double strokeWidthScale = 1)
    {
        if (elements.IsEmpty)
        {
            throw new ArgumentException("Path stream mesh requires at least one element.", nameof(elements));
        }

        if (strokeWidthScale <= 0 || !double.IsFinite(strokeWidthScale))
        {
            throw new ArgumentOutOfRangeException(nameof(strokeWidthScale));
        }

        NativePathStreamMeshHandle handle;
        fixed (SkiaNativePathStreamElement* ptr = elements)
        {
            handle = NativeMethods.PathStreamMeshCreate(ptr, elements.Length, (float)strokeWidthScale);
        }

        if (handle.IsInvalid)
        {
            handle.Dispose();
            throw new InvalidOperationException("Native Skia path stream mesh creation failed.");
        }

        return new SkiaNativePathStreamMesh(handle, elements.Length);
    }

    public void Dispose()
    {
        var handle = _handle;
        if (handle is null)
        {
            return;
        }

        _handle = null;
        handle.Dispose();
    }
}

/// <summary>
/// Attribute type used by a custom Skia mesh vertex buffer.
/// </summary>
public enum SkiaNativeMeshAttributeType : uint
{
    Float = 0,
    Float2 = 1,
    Float3 = 2,
    Float4 = 3,
    UByte4Unorm = 4,
}

/// <summary>
/// Varying type passed from a custom mesh vertex shader to its fragment shader.
/// </summary>
public enum SkiaNativeMeshVaryingType : uint
{
    Float = 0,
    Float2 = 1,
    Float3 = 2,
    Float4 = 3,
    Half = 4,
    Half2 = 5,
    Half3 = 6,
    Half4 = 7,
}

public enum SkiaNativeMeshMode : uint
{
    Triangles = 0,
    TriangleStrip = 1,
}

public enum SkiaNativeMeshUniformType : uint
{
    Float = 0,
    Float2 = 1,
    Float3 = 2,
    Float4 = 3,
    Float2x2 = 4,
    Float3x3 = 5,
    Float4x4 = 6,
    Int = 7,
    Int2 = 8,
    Int3 = 9,
    Int4 = 10,
}

public readonly record struct SkiaNativeMeshAttribute(SkiaNativeMeshAttributeType Type, int Offset, string Name);

public readonly record struct SkiaNativeMeshVarying(SkiaNativeMeshVaryingType Type, string Name);

public readonly record struct SkiaNativeMeshUniformInfo(
    SkiaNativeMeshUniformType Type,
    int Count,
    uint Flags,
    int Offset,
    int Size);

/// <summary>
/// Compiled Skia custom mesh shader specification. Create once and reuse for compatible meshes.
/// </summary>
public sealed class SkiaNativeMeshSpecification : IDisposable
{
    private NativeMeshSpecificationHandle? _handle;

    private SkiaNativeMeshSpecification(NativeMeshSpecificationHandle handle)
    {
        _handle = handle;
        Stride = NativeMethods.MeshSpecGetStride(handle);
        UniformSize = NativeMethods.MeshSpecGetUniformSize(handle);
    }

    public bool IsDisposed => _handle is null;

    public int Stride { get; }

    public int UniformSize { get; }

    internal NativeMeshSpecificationHandle NativeHandle
    {
        get
        {
            ObjectDisposedException.ThrowIf(_handle is null, this);
            return _handle;
        }
    }

    public static unsafe SkiaNativeMeshSpecification Create(
        ReadOnlySpan<SkiaNativeMeshAttribute> attributes,
        int vertexStride,
        ReadOnlySpan<SkiaNativeMeshVarying> varyings,
        string vertexShader,
        string fragmentShader)
    {
        if (attributes.IsEmpty)
        {
            throw new ArgumentException("A mesh specification requires at least one attribute.", nameof(attributes));
        }

        if (vertexStride <= 0 || vertexStride % 4 != 0)
        {
            throw new ArgumentOutOfRangeException(nameof(vertexStride), "Mesh vertex stride must be positive and 4-byte aligned.");
        }

        ArgumentException.ThrowIfNullOrWhiteSpace(vertexShader);
        ArgumentException.ThrowIfNullOrWhiteSpace(fragmentShader);

        var nativeAttributes = new NativeMeshAttribute[attributes.Length];
        var nativeVaryings = new NativeMeshVarying[varyings.Length];
        var namePointers = new nint[attributes.Length + varyings.Length];
        nint vertexShaderPtr = 0;
        nint fragmentShaderPtr = 0;

        try
        {
            for (var i = 0; i < attributes.Length; i++)
            {
                if (string.IsNullOrWhiteSpace(attributes[i].Name))
                {
                    throw new ArgumentException("Mesh attribute names must be non-empty.", nameof(attributes));
                }

                var ptr = Marshal.StringToCoTaskMemUTF8(attributes[i].Name);
                namePointers[i] = ptr;
                nativeAttributes[i] = new NativeMeshAttribute
                {
                    Type = (NativeMeshAttributeType)attributes[i].Type,
                    Offset = checked((uint)attributes[i].Offset),
                    Name = ptr
                };
            }

            for (var i = 0; i < varyings.Length; i++)
            {
                if (string.IsNullOrWhiteSpace(varyings[i].Name))
                {
                    throw new ArgumentException("Mesh varying names must be non-empty.", nameof(varyings));
                }

                var ptr = Marshal.StringToCoTaskMemUTF8(varyings[i].Name);
                namePointers[attributes.Length + i] = ptr;
                nativeVaryings[i] = new NativeMeshVarying
                {
                    Type = (NativeMeshVaryingType)varyings[i].Type,
                    Name = ptr
                };
            }

            vertexShaderPtr = Marshal.StringToCoTaskMemUTF8(vertexShader);
            fragmentShaderPtr = Marshal.StringToCoTaskMemUTF8(fragmentShader);

            Span<byte> error = stackalloc byte[4096];
            fixed (NativeMeshAttribute* attributePtr = nativeAttributes)
            fixed (NativeMeshVarying* varyingPtr = nativeVaryings)
            fixed (byte* errorPtr = error)
            {
                var handle = NativeMethods.MeshSpecCreate(
                    attributePtr,
                    nativeAttributes.Length,
                    vertexStride,
                    varyingPtr,
                    nativeVaryings.Length,
                    (byte*)vertexShaderPtr,
                    (byte*)fragmentShaderPtr,
                    errorPtr,
                    error.Length);

                if (handle.IsInvalid)
                {
                    handle.Dispose();
                    var message = Marshal.PtrToStringUTF8((nint)errorPtr);
                    throw new InvalidOperationException(string.IsNullOrWhiteSpace(message)
                        ? "Native Skia mesh specification creation failed."
                        : message);
                }

                return new SkiaNativeMeshSpecification(handle);
            }
        }
        finally
        {
            foreach (var ptr in namePointers)
            {
                if (ptr != 0)
                {
                    Marshal.FreeCoTaskMem(ptr);
                }
            }

            if (vertexShaderPtr != 0)
            {
                Marshal.FreeCoTaskMem(vertexShaderPtr);
            }

            if (fragmentShaderPtr != 0)
            {
                Marshal.FreeCoTaskMem(fragmentShaderPtr);
            }
        }
    }

    public unsafe bool TryGetUniform(string name, out SkiaNativeMeshUniformInfo info)
    {
        ObjectDisposedException.ThrowIf(_handle is null, this);
        ArgumentException.ThrowIfNullOrWhiteSpace(name);

        var namePtr = Marshal.StringToCoTaskMemUTF8(name);
        try
        {
            var found = NativeMethods.MeshSpecGetUniform(_handle, (byte*)namePtr, out var nativeInfo) != 0;
            info = found
                ? new SkiaNativeMeshUniformInfo(
                    (SkiaNativeMeshUniformType)nativeInfo.Type,
                    checked((int)nativeInfo.Count),
                    nativeInfo.Flags,
                    checked((int)nativeInfo.Offset),
                    checked((int)nativeInfo.Size))
                : default;
            return found;
        }
        finally
        {
            Marshal.FreeCoTaskMem(namePtr);
        }
    }

    public void Dispose()
    {
        var handle = _handle;
        if (handle is null)
        {
            return;
        }

        _handle = null;
        handle.Dispose();
    }
}

/// <summary>
/// Reusable native Skia custom mesh with GPU-backed buffers when the active backend has a GPU context.
/// </summary>
public sealed class SkiaNativeMesh : IDisposable
{
    private readonly NativeContextHandle? _context;
    private readonly SkiaNativeMeshSpecification _specification;
    private NativeMeshHandle? _handle;

    internal SkiaNativeMesh(NativeMeshHandle handle, NativeContextHandle? context, SkiaNativeMeshSpecification specification)
    {
        _handle = handle;
        _context = context;
        _specification = specification;
    }

    public bool IsDisposed => _handle is null;

    internal NativeMeshHandle NativeHandle
    {
        get
        {
            ObjectDisposedException.ThrowIf(_handle is null, this);
            return _handle;
        }
    }

    public unsafe void UpdateVertices<TVertex>(ReadOnlySpan<TVertex> vertices)
        where TVertex : unmanaged
    {
        var bytes = MemoryMarshal.AsBytes(vertices);
        UpdateVertices(bytes, vertices.Length);
    }

    public unsafe void UpdateVertices(ReadOnlySpan<byte> vertexBytes, int vertexCount)
    {
        ObjectDisposedException.ThrowIf(_handle is null, this);
        if (vertexBytes.IsEmpty || vertexCount < 3)
        {
            throw new ArgumentException("A mesh requires at least three vertices.", nameof(vertexBytes));
        }

        if (vertexBytes.Length % 4 != 0)
        {
            throw new ArgumentException("Vertex data size must be 4-byte aligned.", nameof(vertexBytes));
        }

        fixed (byte* vertexPtr = vertexBytes)
        {
            if (NativeMethods.MeshUpdateVertices(GetContextHandle(), _handle, vertexPtr, 0, vertexBytes.Length, vertexCount) == 0)
            {
                throw new InvalidOperationException("Native Skia mesh vertex update failed.");
            }
        }

        GC.KeepAlive(_context);
    }

    public unsafe void UpdateIndices(ReadOnlySpan<ushort> indices)
    {
        ObjectDisposedException.ThrowIf(_handle is null, this);
        if (indices.Length < 3)
        {
            throw new ArgumentException("An indexed mesh requires at least three indices.", nameof(indices));
        }

        if ((indices.Length * sizeof(ushort)) % 4 != 0)
        {
            throw new ArgumentException("Index data size must be 4-byte aligned for GPU updates.", nameof(indices));
        }

        fixed (ushort* indexPtr = indices)
        {
            if (NativeMethods.MeshUpdateIndices(GetContextHandle(), _handle, indexPtr, 0, indices.Length) == 0)
            {
                throw new InvalidOperationException("Native Skia mesh index update failed.");
            }
        }

        GC.KeepAlive(_context);
    }

    public unsafe void UpdateUniforms(ReadOnlySpan<byte> uniforms)
    {
        ObjectDisposedException.ThrowIf(_handle is null, this);
        if (_specification.UniformSize > 0 && uniforms.Length < _specification.UniformSize)
        {
            throw new ArgumentException("Uniform data is smaller than the mesh specification requires.", nameof(uniforms));
        }

        fixed (byte* uniformPtr = uniforms)
        {
            if (NativeMethods.MeshUpdateUniforms(_handle, uniforms.IsEmpty ? null : uniformPtr, uniforms.Length) == 0)
            {
                throw new InvalidOperationException("Native Skia mesh uniform update failed.");
            }
        }
    }

    public void SetBounds(Rect bounds)
    {
        ObjectDisposedException.ThrowIf(_handle is null, this);
        if (NativeMethods.MeshSetBounds(_handle, (float)bounds.Left, (float)bounds.Top, (float)bounds.Right, (float)bounds.Bottom) == 0)
        {
            throw new InvalidOperationException("Native Skia mesh bounds update failed.");
        }
    }

    public void Dispose()
    {
        var handle = _handle;
        if (handle is null)
        {
            return;
        }

        _handle = null;
        handle.Dispose();
    }

    private nint GetContextHandle() => _context?.DangerousGetHandle() ?? 0;
}

/// <summary>
/// Reusable native Skia path resource for hot custom drawing paths.
/// </summary>
public sealed class SkiaNativePath : IDisposable
{
    private NativePathHandle? _handle;

    private SkiaNativePath(NativePathHandle handle)
    {
        _handle = handle;
    }

    public bool IsDisposed => _handle is null;

    internal NativePathHandle NativeHandle
    {
        get
        {
            ObjectDisposedException.ThrowIf(_handle is null, this);
            return _handle;
        }
    }

    public static unsafe SkiaNativePath Create(
        ReadOnlySpan<SkiaNativePathCommand> commands,
        SkiaNativePathFillRule fillRule = SkiaNativePathFillRule.NonZero)
    {
        var nativeCommands = MemoryMarshal.Cast<SkiaNativePathCommand, NativePathCommand>(commands);
        NativePathHandle handle;
        fixed (NativePathCommand* ptr = nativeCommands)
        {
            handle = NativeMethods.PathCreate(ptr, nativeCommands.Length, (NativePathFillRule)fillRule);
        }

        if (handle.IsInvalid)
        {
            handle.Dispose();
            throw new InvalidOperationException("Native Skia path creation failed.");
        }

        return new SkiaNativePath(handle);
    }

    public static SkiaNativePath CreateRectangle(Rect rect, SkiaNativePathFillRule fillRule = SkiaNativePathFillRule.NonZero)
    {
        rect = rect.Normalize();
        var handle = NativeMethods.PathCreateRect((float)rect.X, (float)rect.Y, (float)rect.Width, (float)rect.Height, (NativePathFillRule)fillRule);
        if (handle.IsInvalid)
        {
            handle.Dispose();
            throw new InvalidOperationException("Native Skia rectangle path creation failed.");
        }

        return new SkiaNativePath(handle);
    }

    public static SkiaNativePath CreateEllipse(Rect rect, SkiaNativePathFillRule fillRule = SkiaNativePathFillRule.NonZero)
    {
        rect = rect.Normalize();
        var handle = NativeMethods.PathCreateEllipse((float)rect.X, (float)rect.Y, (float)rect.Width, (float)rect.Height, (NativePathFillRule)fillRule);
        if (handle.IsInvalid)
        {
            handle.Dispose();
            throw new InvalidOperationException("Native Skia ellipse path creation failed.");
        }

        return new SkiaNativePath(handle);
    }

    public static unsafe SkiaNativePath CreateTransformed(SkiaNativePath source, Matrix transform)
    {
        ArgumentNullException.ThrowIfNull(source);
        var nativeTransform = transform.ToNative();
        var handle = NativeMethods.PathCreateTransformed(source.NativeHandle, &nativeTransform);
        if (handle.IsInvalid)
        {
            handle.Dispose();
            throw new InvalidOperationException("Native Skia transformed path creation failed.");
        }

        return new SkiaNativePath(handle);
    }

    public void Dispose()
    {
        var handle = _handle;
        if (handle is null)
        {
            return;
        }

        _handle = null;
        handle.Dispose();
    }
}

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

public enum SkiaNativePathStreamKind : uint
{
    Line = 1,
    Quad = 2,
    Cubic = 3,
}

/// <summary>
/// Streamed path segment layout used by the native Skia path-stream renderer.
/// </summary>
[StructLayout(LayoutKind.Sequential)]
public readonly struct SkiaNativePathStreamElement
{
    public const uint Split = 1u;

    public readonly SkiaNativePathStreamKind Kind;
    public readonly uint Flags;
    public readonly float R;
    public readonly float G;
    public readonly float B;
    public readonly float A;
    public readonly float StrokeThickness;
    public readonly float StartX;
    public readonly float StartY;
    public readonly float Control1X;
    public readonly float Control1Y;
    public readonly float Control2X;
    public readonly float Control2Y;
    public readonly float EndX;
    public readonly float EndY;

    public SkiaNativePathStreamElement(
        SkiaNativePathStreamKind kind,
        uint flags,
        Color color,
        float strokeThickness,
        Point start,
        Point control1,
        Point control2,
        Point end)
    {
        Kind = kind;
        Flags = flags;
        R = color.R / 255f;
        G = color.G / 255f;
        B = color.B / 255f;
        A = color.A / 255f;
        StrokeThickness = strokeThickness;
        StartX = (float)start.X;
        StartY = (float)start.Y;
        Control1X = (float)control1.X;
        Control1Y = (float)control1.Y;
        Control2X = (float)control2.X;
        Control2Y = (float)control2.Y;
        EndX = (float)end.X;
        EndY = (float)end.Y;
    }
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
    private readonly NativeContextHandle? _context;
    private readonly int _initialCapacity;
    private readonly Func<CommandBufferFlushResult> _flushPendingCommands;
    private readonly Action<CommandBufferFlushResult> _reportFlush;

    public SkiaNativeApiLeaseFeature(
        NativeSessionHandle session,
        NativeContextHandle? context,
        int initialCapacity,
        Func<CommandBufferFlushResult> flushPendingCommands,
        Action<CommandBufferFlushResult> reportFlush)
    {
        _session = session;
        _context = context;
        _initialCapacity = initialCapacity;
        _flushPendingCommands = flushPendingCommands;
        _reportFlush = reportFlush;
    }

    public SkiaNativeApiLease Lease()
    {
        _reportFlush(_flushPendingCommands());
        return new SkiaNativeApiLease(new SkiaNativeDirectCanvas(_session, _context, _initialCapacity, _reportFlush));
    }
}

internal static class SkiaNativeDirectFlushResultExtensions
{
    public static SkiaNativeDirectFlushResult ToDirectResult(this CommandBufferFlushResult result) =>
        new(result.CommandCount, result.NativeTransitionCount, result.FlushElapsed, result.NativeResult);
}
