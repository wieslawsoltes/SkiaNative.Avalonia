using System.Runtime.InteropServices;

namespace SkiaNative.Avalonia;

internal abstract class NativeSafeHandle : SafeHandle
{
    protected NativeSafeHandle() : base(IntPtr.Zero, true)
    {
    }

    public override bool IsInvalid => handle == IntPtr.Zero;
}

internal sealed class NativeContextHandle : NativeSafeHandle
{
    protected override bool ReleaseHandle()
    {
        NativeMethods.ContextDestroy(handle);
        return true;
    }
}

internal sealed class NativeSessionHandle : NativeSafeHandle
{
    protected override bool ReleaseHandle()
    {
        NativeMethods.SessionEnd(handle);
        return true;
    }
}

internal sealed class NativeBitmapHandle : NativeSafeHandle
{
    protected override bool ReleaseHandle()
    {
        NativeMethods.BitmapDestroy(handle);
        return true;
    }
}

internal sealed class NativeDataHandle : NativeSafeHandle
{
    protected override bool ReleaseHandle()
    {
        NativeMethods.DataDestroy(handle);
        return true;
    }
}

internal sealed class NativeTypefaceHandle : NativeSafeHandle
{
    protected override bool ReleaseHandle()
    {
        NativeMethods.TypefaceDestroy(handle);
        return true;
    }
}

internal sealed class NativeGlyphRunHandle : NativeSafeHandle
{
    protected override bool ReleaseHandle()
    {
        NativeMethods.GlyphRunDestroy(handle);
        return true;
    }
}

internal sealed class NativePathHandle : NativeSafeHandle
{
    protected override bool ReleaseHandle()
    {
        NativeMethods.PathDestroy(handle);
        return true;
    }
}

internal sealed class NativePathStreamMeshHandle : NativeSafeHandle
{
    protected override bool ReleaseHandle()
    {
        NativeMethods.PathStreamMeshDestroy(handle);
        return true;
    }
}

internal sealed class NativeMeshSpecificationHandle : NativeSafeHandle
{
    protected override bool ReleaseHandle()
    {
        NativeMethods.MeshSpecDestroy(handle);
        return true;
    }
}

internal sealed class NativeMeshHandle : NativeSafeHandle
{
    protected override bool ReleaseHandle()
    {
        NativeMethods.MeshDestroy(handle);
        return true;
    }
}

internal sealed class NativeShaderHandle : NativeSafeHandle
{
    protected override bool ReleaseHandle()
    {
        NativeMethods.ShaderDestroy(handle);
        return true;
    }
}

internal sealed class NativeStrokeHandle : NativeSafeHandle
{
    protected override bool ReleaseHandle()
    {
        NativeMethods.StrokeDestroy(handle);
        return true;
    }
}
