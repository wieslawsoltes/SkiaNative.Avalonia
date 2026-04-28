using System.Runtime.InteropServices;
using Avalonia;
using Avalonia.Media;
using Avalonia.Media.Imaging;
using Avalonia.Platform;
using SkiaNative.Avalonia;
using SkiaNative.Avalonia.Geometry;
using SkiaNative.Avalonia.Imaging;
using Xunit;

namespace SkiaNative.Avalonia.Tests;

public sealed class AbiTests
{
    [Fact]
    public void NativeCommandLayout_IsStableEnoughForCAbi()
    {
        Assert.Equal(152, Marshal.SizeOf<NativeCommand>());
        Assert.Equal(8, Marshal.OffsetOf<NativeCommand>(nameof(NativeCommand.Resource0)).ToInt32());
        Assert.Equal(16, Marshal.OffsetOf<NativeCommand>(nameof(NativeCommand.Resource1)).ToInt32());
        Assert.Equal(24, Marshal.OffsetOf<NativeCommand>(nameof(NativeCommand.Resource2)).ToInt32());
    }

    [Fact]
    public void NativeBulkCommandLayouts_AreStableEnoughForCAbi()
    {
        Assert.Equal(48, Marshal.SizeOf<NativePathStrokeCommand>());
        Assert.Equal(60, Marshal.SizeOf<NativePathStreamElement>());
        Assert.Equal(60, Marshal.SizeOf<SkiaNativePathStreamElement>());
        Assert.Equal(40, Marshal.SizeOf<NativePathFillCommand>());
        Assert.Equal(40, Marshal.SizeOf<NativeGlyphRunCommand>());
        Assert.Equal(64, Marshal.SizeOf<NativeBitmapCommand>());
        Assert.Equal(24, Marshal.OffsetOf<NativePathStrokeCommand>(nameof(NativePathStrokeCommand.Color)).ToInt32());
        Assert.Equal(8, Marshal.OffsetOf<NativePathStreamElement>(nameof(NativePathStreamElement.Color)).ToInt32());
        Assert.Equal(8, Marshal.OffsetOf<SkiaNativePathStreamElement>(nameof(SkiaNativePathStreamElement.R)).ToInt32());
        Assert.Equal(16, Marshal.OffsetOf<NativeGlyphRunCommand>(nameof(NativeGlyphRunCommand.Color)).ToInt32());
        Assert.Equal(12, Marshal.OffsetOf<NativeBitmapCommand>(nameof(NativeBitmapCommand.Color)).ToInt32());
    }

    [Fact]
    public void NativeMeshLayouts_AreStableEnoughForCAbi()
    {
        Assert.Equal(IntPtr.Size == 8 ? 16 : 12, Marshal.SizeOf<NativeMeshAttribute>());
        Assert.Equal(IntPtr.Size == 8 ? 16 : 8, Marshal.SizeOf<NativeMeshVarying>());
        Assert.Equal(20, Marshal.SizeOf<NativeMeshUniformInfo>());
        Assert.Equal(IntPtr.Size == 8 ? 8 : 4, Marshal.OffsetOf<NativeMeshAttribute>(nameof(NativeMeshAttribute.Name)).ToInt32());
        Assert.Equal(IntPtr.Size == 8 ? 8 : 4, Marshal.OffsetOf<NativeMeshVarying>(nameof(NativeMeshVarying.Name)).ToInt32());
        Assert.Equal(12, Marshal.OffsetOf<NativeMeshUniformInfo>(nameof(NativeMeshUniformInfo.Offset)).ToInt32());
    }

    [Fact]
    public void NativeMeshSpecification_CompilesShaderAndReflectsUniforms()
    {
        var nativeLibraryPath = FindNativeLibrary();
        Assert.SkipUnless(nativeLibraryPath is not null, "Native dylib artifacts are required for native mesh ABI smoke tests.");

        NativeLibraryResolver.Configure(new SkiaNativeOptions
        {
            NativeLibraryPath = nativeLibraryPath
        });

        var attributes = new[]
        {
            new SkiaNativeMeshAttribute(SkiaNativeMeshAttributeType.Float2, 0, "position"),
            new SkiaNativeMeshAttribute(SkiaNativeMeshAttributeType.Float2, 8, "local")
        };
        var varyings = new[]
        {
            new SkiaNativeMeshVarying(SkiaNativeMeshVaryingType.Float2, "position"),
            new SkiaNativeMeshVarying(SkiaNativeMeshVaryingType.Float2, "local")
        };

        using var specification = SkiaNativeMeshSpecification.Create(
            attributes,
            16,
            varyings,
            """
Varyings main(const Attributes a) {
    Varyings v;
    v.position = a.position;
    v.local = a.local;
    return v;
}
""",
            """
uniform float u_time;

float2 main(const Varyings v, out half4 color) {
    color = half4(u_time, v.local.x, v.local.y, 1.0);
    return v.position;
}
""");

        Assert.Equal(16, specification.Stride);
        Assert.True(specification.UniformSize >= sizeof(float));
        Assert.True(specification.TryGetUniform("u_time", out var uniform));
        Assert.Equal(SkiaNativeMeshUniformType.Float, uniform.Type);
        Assert.Equal(sizeof(float), uniform.Size);
        Assert.InRange(uniform.Offset, 0, specification.UniformSize - sizeof(float));
    }

    [Fact]
    public void NativePathCommandLayout_IsStableEnoughForCAbi()
    {
        Assert.Equal(40, Marshal.SizeOf<NativePathCommand>());
        Assert.Equal(8, Marshal.OffsetOf<NativePathCommand>(nameof(NativePathCommand.X0)).ToInt32());
    }

    [Fact]
    public void DirectPathCommandLayout_MatchesNativePathCommandLayout()
    {
        Assert.Equal(Marshal.SizeOf<NativePathCommand>(), Marshal.SizeOf<SkiaNativePathCommand>());
        Assert.Equal(
            Marshal.OffsetOf<NativePathCommand>(nameof(NativePathCommand.X0)).ToInt32(),
            Marshal.OffsetOf<SkiaNativePathCommand>(nameof(SkiaNativePathCommand.X0)).ToInt32());
        Assert.Equal((uint)NativePathCommandKind.CubicTo, (uint)SkiaNativePathCommandKind.CubicTo);

        var command = SkiaNativePathCommand.QuadTo(new Point(1, 2), new Point(3, 4));
        Assert.Equal(SkiaNativePathCommandKind.QuadTo, command.Kind);
        Assert.Equal(1, command.X0);
        Assert.Equal(2, command.Y0);
        Assert.Equal(3, command.X1);
        Assert.Equal(4, command.Y1);
    }

    [Fact]
    public void DirectPathResource_CanBeReusedByCommandBuffer()
    {
        var nativeLibraryPath = FindNativeLibrary();
        Assert.SkipUnless(nativeLibraryPath is not null, "Native dylib artifacts are required for direct path resource smoke tests.");

        NativeLibraryResolver.Configure(new SkiaNativeOptions
        {
            NativeLibraryPath = nativeLibraryPath
        });

        var commands = new[]
        {
            SkiaNativePathCommand.MoveTo(0, 0),
            SkiaNativePathCommand.LineTo(100, 0)
        };

        using var path = SkiaNativePath.Create(commands);
        using var commandBuffer = new CommandBuffer(1);
        commandBuffer.StrokeNativePath(path.NativeHandle, Colors.Red, 2, NativeStrokeCap.Round, NativeStrokeJoin.Round, 10);

        Assert.Equal(1, commandBuffer.CommandCount);
        Assert.Equal(NativeCommandKind.DrawPath, commandBuffer.Commands[0].Kind);
        Assert.Equal(path.NativeHandle.DangerousGetHandle(), commandBuffer.Commands[0].Resource0);
    }

    [Fact]
    public void NativeGradientStopLayout_IsStableEnoughForCAbi()
    {
        Assert.Equal(20, Marshal.SizeOf<NativeGradientStop>());
        Assert.Equal(4, Marshal.OffsetOf<NativeGradientStop>(nameof(NativeGradientStop.Color)).ToInt32());
    }

    [Fact]
    public void MatrixConversion_PreservesAvaloniaOrder()
    {
        var matrix = new Matrix(1, 2, 3, 4, 5, 6).ToNative();
        Assert.Equal(1, matrix.M11);
        Assert.Equal(2, matrix.M12);
        Assert.Equal(3, matrix.M21);
        Assert.Equal(4, matrix.M22);
        Assert.Equal(5, matrix.M31);
        Assert.Equal(6, matrix.M32);
    }

    [Fact]
    public void SolidBrushConversion_PremultipliesOpacityIntoAlpha()
    {
        Assert.True(BrushUtil.TryGetFill(new SolidColorBrush(Color.FromArgb(128, 10, 20, 30), 0.5), out var color, out var hasFill));
        Assert.True(hasFill);
        Assert.Equal(10 / 255f, color.R, 3);
        Assert.Equal(64 / 255f, color.A, 2);
    }

    [Fact]
    public void CommandBuffer_EncodesNativeOpacityMaskLayerPushPop()
    {
        var commands = new CommandBuffer(4);

        commands.PushOpacityMask(new SolidColorBrush(Color.FromArgb(128, 0, 0, 0)), new Rect(1, 2, 30, 40));
        commands.DrawRect(Brushes.Red, null, new RoundedRect(new Rect(4, 5, 6, 7)));
        commands.PopOpacityMask();

        Assert.Equal(NativeCommandKind.PushOpacityMaskLayer, commands.Commands[0].Kind);
        Assert.Equal(NativeCommandKind.DrawRect, commands.Commands[1].Kind);
        Assert.Equal(NativeCommandKind.PopOpacityMaskLayer, commands.Commands[2].Kind);
        Assert.Equal(1, commands.Commands[0].X0);
        Assert.Equal(2, commands.Commands[0].Y0);
        Assert.Equal(30, commands.Commands[0].X1);
        Assert.Equal(40, commands.Commands[0].Y1);
        Assert.Equal(128 / 255f, commands.Commands[0].Fill.A, 3);
    }

    [Fact]
    public unsafe void NativeStrokeFactory_CreatesStyledStrokeHandle()
    {
        var nativeLibraryPath = FindNativeLibrary();
        Assert.SkipUnless(nativeLibraryPath is not null, "Native dylib artifacts are required for native stroke ABI smoke tests.");

        NativeLibraryResolver.Configure(new SkiaNativeOptions
        {
            NativeLibraryPath = nativeLibraryPath
        });

        var dashes = new[] { 6f, 3f, 1f, 3f };
        fixed (float* ptr = dashes)
        {
            using var stroke = NativeMethods.StrokeCreate(NativeStrokeCap.Round, NativeStrokeJoin.Bevel, 7, ptr, dashes.Length, 2);
            Assert.False(stroke.IsInvalid);
        }
    }

    [Fact]
    public unsafe void NativeShaderFactory_CreatesGradientShadersWithLocalMatrices()
    {
        var nativeLibraryPath = FindNativeLibrary();
        Assert.SkipUnless(nativeLibraryPath is not null, "Native dylib artifacts are required for native shader ABI smoke tests.");

        NativeLibraryResolver.Configure(new SkiaNativeOptions
        {
            NativeLibraryPath = nativeLibraryPath
        });

        var stops = new[]
        {
            new NativeGradientStop { Offset = 0, Color = new NativeColor(1, 0, 0, 1) },
            new NativeGradientStop { Offset = 1, Color = new NativeColor(0, 0, 1, 1) }
        };
        var matrix = Matrix.CreateRotation(Math.PI / 8, new Point(50, 40)).ToNative();

        fixed (NativeGradientStop* ptr = stops)
        {
            using var linear = NativeMethods.ShaderCreateLinearWithMatrix(0, 0, 100, 80, ptr, stops.Length, NativeGradientSpreadMethod.Reflect, &matrix);
            using var radial = NativeMethods.ShaderCreateRadialWithMatrix(50, 40, 35, 30, 60, ptr, stops.Length, NativeGradientSpreadMethod.Repeat, &matrix);

            Assert.False(linear.IsInvalid);
            Assert.False(radial.IsInvalid);
        }
    }

    [Fact]
    public void BrushUtil_CreatesPaintForTransformedAndEllipticalGradients()
    {
        var nativeLibraryPath = FindNativeLibrary();
        Assert.SkipUnless(nativeLibraryPath is not null, "Native dylib artifacts are required for native brush shader smoke tests.");

        NativeLibraryResolver.Configure(new SkiaNativeOptions
        {
            NativeLibraryPath = nativeLibraryPath
        });

        var linear = new LinearGradientBrush
        {
            StartPoint = new RelativePoint(0, 0, RelativeUnit.Relative),
            EndPoint = new RelativePoint(1, 0, RelativeUnit.Relative),
            Transform = new RotateTransform(30),
            TransformOrigin = RelativePoint.Center,
            GradientStops =
            {
                new GradientStop(Colors.Red, 0),
                new GradientStop(Colors.Blue, 1)
            }
        };

        var radial = new RadialGradientBrush
        {
            Center = RelativePoint.Center,
            GradientOrigin = new RelativePoint(0.25, 0.25, RelativeUnit.Relative),
            RadiusX = new RelativeScalar(0.6, RelativeUnit.Relative),
            RadiusY = new RelativeScalar(0.35, RelativeUnit.Relative),
            Transform = new ScaleTransform(0.75, 1.25),
            TransformOrigin = RelativePoint.Center,
            GradientStops =
            {
                new GradientStop(Colors.White, 0),
                new GradientStop(Colors.Black, 1)
            }
        };

        Assert.True(BrushUtil.TryCreatePaint(linear, new Rect(0, 0, 100, 80), out var linearPaint));
        Assert.True(BrushUtil.TryCreatePaint(radial, new Rect(10, 20, 120, 90), out var radialPaint));
        Assert.NotNull(linearPaint.Shader);
        Assert.NotNull(radialPaint.Shader);
        Assert.False(linearPaint.Shader.IsInvalid);
        Assert.False(radialPaint.Shader.IsInvalid);
        Assert.False(linearPaint.OwnsShader);
        Assert.False(radialPaint.OwnsShader);

        Assert.True(BrushUtil.TryCreatePaint(linear, new Rect(0, 0, 100, 80), out var repeatedLinearPaint));
        Assert.Same(linearPaint.Shader, repeatedLinearPaint.Shader);
    }

    [Fact]
    public void BrushUtil_CachesImageBrushIntermediatesUntilBitmapVersionChanges()
    {
        var nativeLibraryPath = FindNativeLibrary();
        Assert.SkipUnless(nativeLibraryPath is not null, "Native dylib artifacts are required for native image brush cache smoke tests.");

        NativeLibraryResolver.Configure(new SkiaNativeOptions
        {
            NativeLibraryPath = nativeLibraryPath
        });

        var source = new NativeWriteableBitmap(new PixelSize(4, 4), new Vector(96, 96), PixelFormats.Bgra8888, AlphaFormat.Premul);
        using var bitmap = new TestBitmap(source);
        using var cache = new TileBrushIntermediateCache();
        var brush = new ImageBrush(bitmap)
        {
            TileMode = TileMode.Tile,
            Stretch = Stretch.Fill,
            DestinationRect = new RelativeRect(0, 0, 8, 8, RelativeUnit.Absolute)
        };

        Assert.True(BrushUtil.TryCreatePaint(brush, new Rect(0, 0, 32, 32), out var first, cache));
        Assert.True(BrushUtil.TryCreatePaint(brush, new Rect(0, 0, 32, 32), out var second, cache));
        Assert.False(first.OwnsShader);
        Assert.Same(first.Shader, second.Shader);

        using (source.Lock())
        {
        }

        Assert.True(BrushUtil.TryCreatePaint(brush, new Rect(0, 0, 32, 32), out var afterMutation, cache));
        Assert.NotSame(first.Shader, afterMutation.Shader);
    }

    [Fact]
    public void NativePathFactories_CreatePrimitiveGroupAndCombinedHandles()
    {
        var nativeLibraryPath = FindNativeLibrary();
        Assert.SkipUnless(nativeLibraryPath is not null, "Native dylib artifacts are required for native path ABI smoke tests.");

        NativeLibraryResolver.Configure(new SkiaNativeOptions
        {
            NativeLibraryPath = nativeLibraryPath
        });

        using var rect = NativePathCommands.CreateRect(new Rect(0, 0, 100, 80), FillRule.NonZero);
        using var ellipse = NativePathCommands.CreateEllipse(new Rect(20, 10, 60, 60), FillRule.NonZero);
        using var group = NativePathCommands.CreateGroup(new[] { rect, ellipse }, FillRule.NonZero);
        using var combined = NativePathCommands.CreateCombined(rect, ellipse, GeometryCombineMode.Exclude, FillRule.NonZero);
        using var translated = NativePathCommands.CreateTransformed(rect, Matrix.CreateTranslation(10, 20));
        using var line = NativePathCommands.Create(
            new[] { NativePathCommands.MoveTo(new Point(0, 0)), NativePathCommands.LineTo(new Point(100, 0)) },
            FillRule.NonZero);
        using var segment = NativePathCommands.CreateSegment(line, 10, 40, true)!;
        using var stroked = NativePathCommands.CreateStroked(line, new Pen(Brushes.Black, 10, DashStyle.Dash, PenLineCap.Round, PenLineJoin.Round))!;

        Assert.False(rect.IsInvalid);
        Assert.False(ellipse.IsInvalid);
        Assert.False(group.IsInvalid);
        Assert.NotNull(combined);
        Assert.False(combined.IsInvalid);
        Assert.NotNull(translated);
        Assert.False(translated.IsInvalid);
        Assert.False(line.IsInvalid);
        Assert.False(segment.IsInvalid);
        Assert.False(stroked.IsInvalid);
        Assert.True(NativePathCommands.Contains(rect, new Point(10, 10)));
        Assert.False(NativePathCommands.Contains(rect, new Point(140, 10)));
        Assert.True(NativePathCommands.TryGetBounds(translated, out var bounds));
        Assert.Equal(new Rect(10, 20, 100, 80), bounds);
        Assert.Equal(100, NativePathCommands.GetContourLength(line), 3);
        Assert.True(NativePathCommands.TryGetPointAndTangentAtDistance(line, 50, out var point, out var tangent));
        Assert.Equal(new Point(50, 0), point);
        Assert.Equal(new Point(1, 0), tangent);
        Assert.True(NativePathCommands.TryGetBounds(segment, out var segmentBounds));
        Assert.Equal(new Rect(10, 0, 30, 0), segmentBounds);
        Assert.True(NativePathCommands.Contains(stroked, new Point(50, 2)));
        Assert.False(NativePathCommands.Contains(stroked, new Point(50, 20)));
    }

    private static string? FindNativeLibrary()
    {
        var rid = RuntimeInformation.ProcessArchitecture switch
        {
            Architecture.Arm64 => "osx-arm64",
            Architecture.X64 => "osx-x64",
            _ => null
        };

        if (rid is null)
        {
            return null;
        }

        for (var dir = new DirectoryInfo(AppContext.BaseDirectory); dir is not null; dir = dir.Parent)
        {
            var candidate = Path.Combine(dir.FullName, "artifacts", "native", rid, "libSkiaNativeAvalonia.dylib");
            if (File.Exists(candidate))
            {
                return candidate;
            }
        }

        return null;
    }

    private sealed class TestBitmap : Bitmap
    {
        public TestBitmap(IBitmapImpl impl)
            : base(impl)
        {
        }
    }
}
