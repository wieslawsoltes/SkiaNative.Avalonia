using System.Runtime.InteropServices;

namespace SkiaNative.Avalonia;

internal enum NativeCommandKind : uint
{
    Save = 1,
    Restore = 2,
    SetTransform = 3,
    Clear = 4,
    DrawLine = 5,
    DrawRect = 6,
    DrawRoundRect = 7,
    DrawEllipse = 8,
    PushClipRect = 9,
    PushClipRoundRect = 10,
    DrawBitmap = 11,
    DrawGlyphRun = 12,
    DrawPath = 13,
    PushClipPath = 14,
    SaveLayer = 15,
    PushOpacityMaskLayer = 16,
    PopOpacityMaskLayer = 17,
    DrawBoxShadow = 18,
    ConcatTransform = 19,
    DrawPathStream = 20,
}

internal enum NativePixelFormat : uint
{
    Bgra8888 = 1,
    Rgba8888 = 2,
    Rgb565 = 3,
}

internal enum NativeAlphaFormat : uint
{
    Premul = 1,
    Opaque = 2,
    Unpremul = 3,
}

internal enum NativeEncodedImageFormat : uint
{
    Png = 1,
}

internal enum NativePathCommandKind : uint
{
    MoveTo = 1,
    LineTo = 2,
    QuadTo = 3,
    CubicTo = 4,
    ArcTo = 5,
    Close = 6,
}

internal enum NativePathStreamKind : uint
{
    Line = 1,
    Quad = 2,
    Cubic = 3,
}

internal enum NativePathFillRule : uint
{
    NonZero = 0,
    EvenOdd = 1,
}

internal enum NativePathOp : uint
{
    Union = 0,
    Intersect = 1,
    Xor = 2,
    Difference = 3,
}

internal enum NativeGradientSpreadMethod : uint
{
    Pad = 0,
    Reflect = 1,
    Repeat = 2,
}

internal enum NativeTileMode : uint
{
    Clamp = 0,
    Repeat = 1,
    Mirror = 2,
    Decal = 3,
}

internal enum NativeStrokeCap : uint
{
    Butt = 0,
    Round = 1,
    Square = 2,
}

internal enum NativeStrokeJoin : uint
{
    Miter = 0,
    Round = 1,
    Bevel = 2,
}

internal enum NativeMeshAttributeType : uint
{
    Float = 0,
    Float2 = 1,
    Float3 = 2,
    Float4 = 3,
    UByte4Unorm = 4,
}

internal enum NativeMeshVaryingType : uint
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

internal enum NativeMeshMode : uint
{
    Triangles = 0,
    TriangleStrip = 1,
}

internal enum NativeMeshUniformType : uint
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

[StructLayout(LayoutKind.Sequential)]
internal struct NativeColor
{
    public float R;
    public float G;
    public float B;
    public float A;

    public NativeColor(float r, float g, float b, float a)
    {
        R = r;
        G = g;
        B = b;
        A = a;
    }
}

[StructLayout(LayoutKind.Sequential)]
internal struct NativeMatrix
{
    public double M11;
    public double M12;
    public double M21;
    public double M22;
    public double M31;
    public double M32;
}

[StructLayout(LayoutKind.Sequential)]
internal struct NativeCommand
{
    public NativeCommandKind Kind;
    public uint Flags;
    public nint Resource0;
    public nint Resource1;
    public nint Resource2;
    public NativeColor Fill;
    public NativeColor Stroke;
    public float StrokeThickness;
    public float X0;
    public float Y0;
    public float X1;
    public float Y1;
    public float X2;
    public float Y2;
    public float X3;
    public float Y3;
    public NativeMatrix Matrix;
}

[StructLayout(LayoutKind.Sequential)]
internal struct NativePathStrokeCommand
{
    public nint Path;
    public nint Stroke;
    public nint Shader;
    public NativeColor Color;
    public float StrokeThickness;
    public uint Flags;
}

[StructLayout(LayoutKind.Sequential)]
internal readonly struct NativePathStreamElement
{
    public readonly NativePathStreamKind Kind;
    public readonly uint Flags;
    public readonly NativeColor Color;
    public readonly float StrokeThickness;
    public readonly float StartX;
    public readonly float StartY;
    public readonly float Control1X;
    public readonly float Control1Y;
    public readonly float Control2X;
    public readonly float Control2Y;
    public readonly float EndX;
    public readonly float EndY;
}

[StructLayout(LayoutKind.Sequential)]
internal struct NativePathFillCommand
{
    public nint Path;
    public nint Shader;
    public NativeColor Color;
    public uint Flags;
}

[StructLayout(LayoutKind.Sequential)]
internal struct NativeGlyphRunCommand
{
    public nint GlyphRun;
    public nint Shader;
    public NativeColor Color;
    public uint Flags;
}

[StructLayout(LayoutKind.Sequential)]
internal struct NativeBitmapCommand
{
    public nint Bitmap;
    public uint Flags;
    public NativeColor Color;
    public float X0;
    public float Y0;
    public float X1;
    public float Y1;
    public float X2;
    public float Y2;
    public float X3;
    public float Y3;
    public uint Reserved;
}

[StructLayout(LayoutKind.Sequential)]
internal struct NativeGlyphPosition
{
    public float X;
    public float Y;

    public NativeGlyphPosition(float x, float y)
    {
        X = x;
        Y = y;
    }
}

[StructLayout(LayoutKind.Sequential)]
internal struct NativePathCommand
{
    public NativePathCommandKind Kind;
    public uint Flags;
    public float X0;
    public float Y0;
    public float X1;
    public float Y1;
    public float X2;
    public float Y2;
    public float X3;
    public float Y3;
}

[StructLayout(LayoutKind.Sequential)]
internal struct NativeGradientStop
{
    public float Offset;
    public NativeColor Color;
}

[StructLayout(LayoutKind.Sequential)]
internal struct NativeMeshAttribute
{
    public NativeMeshAttributeType Type;
    public uint Offset;
    public nint Name;
}

[StructLayout(LayoutKind.Sequential)]
internal struct NativeMeshVarying
{
    public NativeMeshVaryingType Type;
    public nint Name;
}

[StructLayout(LayoutKind.Sequential)]
internal struct NativeMeshUniformInfo
{
    public NativeMeshUniformType Type;
    public uint Count;
    public uint Flags;
    public uint Offset;
    public uint Size;
}
