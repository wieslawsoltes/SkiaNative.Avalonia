using Avalonia;
using Avalonia.Controls;
using Avalonia.Controls.ApplicationLifetimes;
using Avalonia.Controls.Primitives;
using Avalonia.Controls.Shapes;
using Avalonia.Layout;
using Avalonia.Media;
using Avalonia.Media.Imaging;
using Avalonia.Media.TextFormatting;
using Avalonia.Platform;
using Avalonia.Styling;
using Avalonia.Themes.Simple;
using Avalonia.Threading;
using System.Runtime.InteropServices;
using SkiaNative.Avalonia;

namespace SkiaNative.Avalonia.Sample;

internal sealed class App : Application
{
    public override void Initialize()
    {
        RequestedThemeVariant = ThemeVariant.Light;
        Styles.Add(new SimpleTheme());
    }

    public override void OnFrameworkInitializationCompleted()
    {
        if (ApplicationLifetime is IClassicDesktopStyleApplicationLifetime desktop)
        {
            var window = new Window
            {
                Width = 720,
                Height = 540,
                MinWidth = 640,
                MinHeight = 480,
                Title = $"{Program.SelectedBackend.DisplayName} Backend Validation",
                Content = new SampleShell(Program.SelectedBackend)
            };
            desktop.MainWindow = window;
            SampleSmokeMode.Configure(desktop, window);
        }

        base.OnFrameworkInitializationCompleted();
    }
}

internal sealed class SampleShell : UserControl
{
    private readonly TextBlock _status;
    private readonly ScrollViewer _scrollViewer;
    private readonly SampleBackend _backend;

    public SampleShell(SampleBackend backend)
    {
        _backend = backend;
        _status = new TextBlock
        {
            Text = $"Ready. Backend: {_backend.DisplayName}. Interact with controls while watching the custom render surface below.",
            FontSize = 13,
            Foreground = Brushes.DimGray
        };

        var root = new DockPanel
        {
            Background = new SolidColorBrush(Color.FromRgb(246, 248, 251))
        };

        var statusBar = new Border
        {
            Background = Brushes.White,
            BorderBrush = new SolidColorBrush(Color.FromRgb(220, 226, 235)),
            BorderThickness = new Thickness(1, 1, 1, 0),
            Padding = new Thickness(14, 8),
            Child = _status
        };
        DockPanel.SetDock(statusBar, Dock.Top);
        root.Children.Add(statusBar);

        var content = new StackPanel
        {
            Spacing = 16,
            Margin = new Thickness(18)
        };

        content.Children.Add(Header(_backend));
        content.Children.Add(Section("Controls", ControlsPanel()));
        content.Children.Add(Section("Text", TextPanel()));
        content.Children.Add(Section("Shapes From Controls", ShapePanel()));
        content.Children.Add(Section("Custom DrawingContext Surface", new DiagnosticCanvas { MinHeight = 760, HorizontalAlignment = HorizontalAlignment.Stretch }));

        root.Children.Add(_scrollViewer = new ScrollViewer
        {
            HorizontalScrollBarVisibility = ScrollBarVisibility.Disabled,
            VerticalScrollBarVisibility = ScrollBarVisibility.Auto,
            Content = content
        });

        Content = root;
    }

    public void SmokeScrollTo(double verticalOffset)
    {
        _scrollViewer.Offset = new Vector(_scrollViewer.Offset.X, verticalOffset);
    }

    private static Control Header(SampleBackend backend)
    {
        return new Border
        {
            Background = new SolidColorBrush(Color.FromRgb(20, 30, 44)),
            CornerRadius = new CornerRadius(16),
            Padding = new Thickness(20),
            Child = new StackPanel
            {
                Spacing = 6,
                Children =
                {
                    new TextBlock
                    {
                        Text = $"{backend.DisplayName} renderer validation",
                        FontSize = 28,
                        FontWeight = FontWeight.Bold,
                        Foreground = Brushes.White
                    },
                    new TextBlock
                    {
                        Text = $"{backend.BuilderCall}. Exercises controls, glyph runs, bitmaps, clips, transforms, opacity masks, geometry, and primitive drawing.",
                        FontSize = 14,
                        Foreground = new SolidColorBrush(Color.FromRgb(194, 207, 228))
                    }
                }
            }
        };
    }

    private Control ControlsPanel()
    {
        var button = new Button { Content = "Button", Padding = new Thickness(18, 8) };
        button.Click += (_, _) => _status.Text = $"Button clicked at {DateTime.Now:T}";

        var toggle = new ToggleButton { Content = "Toggle", IsChecked = true, Padding = new Thickness(18, 8) };
        toggle.Click += (_, _) => _status.Text = toggle.IsChecked == true ? "Toggle checked" : "Toggle unchecked";

        var sliderValue = new TextBlock { Text = "42", Width = 42, VerticalAlignment = VerticalAlignment.Center };
        var slider = new Slider { Minimum = 0, Maximum = 100, Value = 42, Width = 220 };
        slider.PropertyChanged += (_, e) =>
        {
            if (e.Property == RangeBase.ValueProperty)
            {
                sliderValue.Text = slider.Value.ToString("0");
            }
        };

        return new WrapPanel
        {
            ItemSpacing = 14,
            LineSpacing = 14,
            Children =
            {
                button,
                toggle,
                new CheckBox { Content = "CheckBox", IsChecked = true },
                new RadioButton { Content = "Radio A", GroupName = "sample-radio", IsChecked = true },
                new RadioButton { Content = "Radio B", GroupName = "sample-radio" },
                new TextBox { Text = "Editable text", Width = 180 },
                new ComboBox { SelectedIndex = 1, Width = 160, ItemsSource = new[] { "CPU fallback", "Metal GPU", "Stress mode" } },
                new StackPanel { Orientation = Orientation.Horizontal, Spacing = 8, Children = { slider, sliderValue } },
                new ProgressBar { Width = 180, Height = 18, Minimum = 0, Maximum = 100, Value = 66 },
                new ListBox { Width = 180, Height = 104, ItemsSource = new[] { "ListBox row 1", "Bitmap upload", "Glyph run", "Metal texture" }, SelectedIndex = 1 },
                new Expander { Header = "Expander", Content = new TextBlock { Text = "Expanded content validates presenter, border, and text rendering.", TextWrapping = TextWrapping.Wrap, Width = 260 }, IsExpanded = true }
            }
        };
    }

    private static Control TextPanel()
    {
        return new StackPanel
        {
            Spacing = 10,
            Children =
            {
                new TextBlock
                {
                    Text = "TextBlock: ASCII, Polish diacritics: zażółć gęślą jaźń, emoji fallback: ★ ✓ →",
                    FontSize = 18,
                    TextWrapping = TextWrapping.Wrap
                },
                new TextBlock
                {
                    Text = "Bold / italic / underline / wrapping validation. This paragraph should wrap cleanly and exercise HarfBuzz shaping plus native Skia glyph rasterization.",
                    FontSize = 16,
                    FontWeight = FontWeight.SemiBold,
                    FontStyle = FontStyle.Italic,
                    TextDecorations = TextDecorations.Underline,
                    TextWrapping = TextWrapping.Wrap,
                    MaxWidth = 920
                },
                new SelectableTextBlock
                {
                    Text = "SelectableTextBlock: copy/select path, glyph hit testing, and text layout visuals.",
                    FontSize = 14
                }
            }
        };
    }

    private static Control ShapePanel()
    {
        return new WrapPanel
        {
            ItemSpacing = 18,
            LineSpacing = 18,
            Children =
            {
                new Rectangle { Width = 120, Height = 70, RadiusX = 12, RadiusY = 12, Fill = Brushes.DodgerBlue, Stroke = Brushes.Navy, StrokeThickness = 3 },
                new Ellipse { Width = 120, Height = 70, Fill = Brushes.Orange, Stroke = Brushes.DarkRed, StrokeThickness = 3 },
                new Line { StartPoint = new Point(0, 60), EndPoint = new Point(140, 10), Stroke = Brushes.SeaGreen, StrokeThickness = 6, Width = 140, Height = 70 },
                new global::Avalonia.Controls.Shapes.Path { Width = 140, Height = 80, Stretch = Stretch.Uniform, Fill = Brushes.MediumPurple, Stroke = Brushes.Black, StrokeThickness = 2, Data = global::Avalonia.Media.Geometry.Parse("M 10,70 L 70,5 L 130,70 Z") }
            }
        };
    }

    private static Border Section(string title, Control content)
    {
        return new Border
        {
            Background = Brushes.White,
            BorderBrush = new SolidColorBrush(Color.FromRgb(222, 228, 238)),
            BorderThickness = new Thickness(1),
            CornerRadius = new CornerRadius(14),
            Padding = new Thickness(16),
            Child = new StackPanel
            {
                Spacing = 12,
                Children =
                {
                    new TextBlock { Text = title, FontSize = 18, FontWeight = FontWeight.Bold, Foreground = new SolidColorBrush(Color.FromRgb(32, 43, 58)) },
                    content
                }
            }
        };
    }
}

internal sealed class DiagnosticCanvas : Control
{
    private readonly WriteableBitmap _bitmap = CreateTestBitmap();
    private readonly global::Avalonia.Media.Geometry _triangle = global::Avalonia.Media.Geometry.Parse("M 0,90 L 80,0 L 160,90 Z");
    private readonly global::Avalonia.Media.Geometry _wave = global::Avalonia.Media.Geometry.Parse("M 0,40 C 35,0 70,80 105,40 S 175,80 210,40");
    private readonly global::Avalonia.Media.Geometry _groupGeometry = CreateGroupGeometry();
    private readonly global::Avalonia.Media.Geometry _combinedGeometry = CreateCombinedGeometry();

    public override void Render(DrawingContext context)
    {
        var bounds = new Rect(Bounds.Size);
        context.FillRectangle(new SolidColorBrush(Color.FromRgb(250, 252, 255)), bounds);
        DrawGrid(context, bounds);

        DrawPrimitiveRow(context);
        DrawClipAndTransformRow(context);
        DrawBitmapRow(context);
        DrawTextRow(context);
        DrawUnsupportedCoverageRow(context);
    }

    private static void DrawGrid(DrawingContext context, Rect bounds)
    {
        var pen = new Pen(new SolidColorBrush(Color.FromRgb(226, 232, 242)), 1);
        for (var x = 0; x <= bounds.Width; x += 40)
        {
            context.DrawLine(pen, new Point(x, 0), new Point(x, bounds.Height));
        }

        for (var y = 0; y <= bounds.Height; y += 40)
        {
            context.DrawLine(pen, new Point(0, y), new Point(bounds.Width, y));
        }
    }

    private static void DrawPrimitiveRow(DrawingContext context)
    {
        Label(context, "Primitives: clear/background, lines, rects, rounded rects, ellipses, styled pens", 20, 18);
        context.DrawRectangle(new SolidColorBrush(Color.FromRgb(32, 126, 214)), new Pen(Brushes.Black, 2), new Rect(24, 52, 150, 78), 0);
        context.DrawRectangle(new SolidColorBrush(Color.FromRgb(121, 87, 213)), new Pen(Brushes.Black, 3), new Rect(196, 52, 150, 78), 18, 18);
        context.DrawEllipse(new SolidColorBrush(Color.FromRgb(245, 132, 38)), new Pen(Brushes.DarkRed, 3), new Rect(372, 50, 118, 86));
        context.DrawLine(new Pen(new SolidColorBrush(Color.FromRgb(24, 150, 92)), 7), new Point(520, 122), new Point(760, 56));
        context.DrawLine(new Pen(Brushes.Crimson, 2), new Point(520, 56), new Point(760, 122));
        context.DrawLine(new Pen(Brushes.MidnightBlue, 9, DashStyle.DashDot, PenLineCap.Round, PenLineJoin.Round), new Point(802, 118), new Point(1070, 58));
    }

    private static void DrawClipAndTransformRow(DrawingContext context)
    {
        Label(context, "Clips, transforms, opacity, opacity masks", 20, 164);

        using (context.PushClip(new Rect(24, 200, 190, 82)))
        {
            context.DrawRectangle(new SolidColorBrush(Color.FromRgb(255, 215, 64)), null, new Rect(0, 188, 250, 118));
            context.DrawLine(new Pen(Brushes.Black, 8), new Point(0, 292), new Point(260, 184));
        }
        context.DrawRectangle(null, new Pen(Brushes.Black, 1), new Rect(24, 200, 190, 82));

        using (context.PushClip(new RoundedRect(new Rect(244, 200, 190, 82), 24)))
        {
            context.DrawRectangle(new SolidColorBrush(Color.FromRgb(80, 185, 180)), null, new Rect(220, 190, 240, 112));
            context.DrawEllipse(new SolidColorBrush(Color.FromArgb(190, 255, 255, 255)), null, new Rect(300, 180, 115, 115));
        }
        context.DrawRectangle(null, new Pen(Brushes.Black, 1), new Rect(244, 200, 190, 82), 24);

        using (context.PushTransform(Matrix.CreateRotation(Math.PI / 12, new Point(560, 244))))
        {
            context.DrawRectangle(new SolidColorBrush(Color.FromRgb(235, 84, 100)), new Pen(Brushes.Black, 2), new Rect(496, 206, 145, 72), 10);
        }

        using (context.PushClip(new Rect(690, 195, 170, 110)))
        using (context.PushOpacity(0.45))
        {
            context.DrawEllipse(Brushes.DodgerBlue, null, new Rect(690, 195, 110, 110));
            context.DrawRectangle(Brushes.OrangeRed, null, new Rect(750, 216, 110, 74), 14);
        }

        var opacityMask = new LinearGradientBrush
        {
            StartPoint = new RelativePoint(0, 0, RelativeUnit.Relative),
            EndPoint = new RelativePoint(1, 0, RelativeUnit.Relative),
            GradientStops =
            {
                new GradientStop(Colors.Transparent, 0),
                new GradientStop(Colors.Black, 0.52),
                new GradientStop(Colors.Transparent, 1)
            }
        };
        using (context.PushOpacityMask(opacityMask, new Rect(900, 190, 190, 108)))
        {
            context.DrawRectangle(new SolidColorBrush(Color.FromRgb(18, 96, 190)), null, new Rect(900, 190, 190, 108), 18);
            context.DrawEllipse(new SolidColorBrush(Color.FromRgb(255, 203, 65)), null, new Rect(940, 200, 110, 88));
        }
        context.DrawRectangle(null, new Pen(Brushes.Black, 1), new Rect(900, 190, 190, 108), 18);
    }

    private void DrawBitmapRow(DrawingContext context)
    {
        Label(context, "Bitmap upload and scaling", 20, 326);
        context.DrawImage(_bitmap, new Rect(24, 360, 128, 128));
        context.DrawImage(_bitmap, new Rect(0, 0, 96, 96), new Rect(176, 360, 220, 128));
        context.DrawRectangle(null, new Pen(Brushes.Black, 1), new Rect(24, 360, 128, 128));
        context.DrawRectangle(null, new Pen(Brushes.Black, 1), new Rect(176, 360, 220, 128));
    }

    private static void DrawTextRow(DrawingContext context)
    {
        Label(context, "TextLayout and glyph runs", 20, 530);
        using var layout = new TextLayout(
            "Native glyph path: The quick brown fox jumps over 13 lazy dogs. Polish: zażółć gęślą jaźń.",
            new Typeface("Arial"),
            24,
            new SolidColorBrush(Color.FromRgb(25, 37, 52)),
            textWrapping: TextWrapping.Wrap,
            maxWidth: 720);
        layout.Draw(context, new Point(24, 566));

        using var mono = new TextLayout(
            "0123456789 +-*/ SIMD Span<T> LibraryImport Metal MTLTexture",
            new Typeface("Menlo"),
            15,
            Brushes.DarkSlateGray,
            maxWidth: 720);
        mono.Draw(context, new Point(24, 645));
    }

    private void DrawUnsupportedCoverageRow(DrawingContext context)
    {
        Label(context, "Coverage probes: native paths, transformed gradients, path ops, widened paths, text decorations, opacity save layers", 510, 326);

        context.DrawGeometry(new SolidColorBrush(Color.FromRgb(118, 88, 214)), new Pen(Brushes.Black, 2), _triangle);
        using (context.PushTransform(Matrix.CreateTranslation(710, 358)))
        {
            context.DrawGeometry(null, new Pen(Brushes.SeaGreen, 5), _wave);
        }

        var gradient = new LinearGradientBrush
        {
            StartPoint = new RelativePoint(0, 0, RelativeUnit.Relative),
            EndPoint = new RelativePoint(1, 1, RelativeUnit.Relative),
            Transform = new RotateTransform(18),
            TransformOrigin = RelativePoint.Center,
            GradientStops =
            {
                new GradientStop(Colors.DeepSkyBlue, 0),
                new GradientStop(Colors.Gold, 0.55),
                new GradientStop(Colors.OrangeRed, 1)
            }
        };
        context.DrawRectangle(gradient, new Pen(Brushes.Black, 1), new Rect(510, 438, 220, 92), 16);

        var radial = new RadialGradientBrush
        {
            Center = new RelativePoint(0.45, 0.45, RelativeUnit.Relative),
            GradientOrigin = new RelativePoint(0.2, 0.2, RelativeUnit.Relative),
            RadiusX = new RelativeScalar(0.55, RelativeUnit.Relative),
            RadiusY = new RelativeScalar(0.32, RelativeUnit.Relative),
            Transform = new RotateTransform(-24),
            TransformOrigin = RelativePoint.Center,
            GradientStops =
            {
                new GradientStop(Colors.White, 0),
                new GradientStop(Colors.HotPink, 0.45),
                new GradientStop(Colors.MidnightBlue, 1)
            }
        };
        context.DrawEllipse(radial, new Pen(Brushes.Black, 1), new Rect(760, 438, 180, 92));

        using (context.PushClip(new Rect(970, 438, 118, 92)))
        using (context.PushOpacity(0.45))
        {
            context.DrawEllipse(Brushes.DodgerBlue, null, new Rect(970, 438, 92, 92));
            context.DrawRectangle(Brushes.OrangeRed, null, new Rect(1018, 458, 70, 52), 12);
        }

        using (context.PushTransform(Matrix.CreateTranslation(510, 585)))
        {
            context.DrawGeometry(new SolidColorBrush(Color.FromArgb(210, 57, 155, 96)), new Pen(Brushes.Black, 2), _groupGeometry);
        }

        using (context.PushTransform(Matrix.CreateTranslation(760, 585)))
        {
            context.DrawGeometry(new SolidColorBrush(Color.FromArgb(220, 231, 87, 113)), new Pen(Brushes.Black, 2), _combinedGeometry);
        }

        using (context.PushTransform(Matrix.CreateTranslation(975, 604)))
        {
            var styledPen = new Pen(Brushes.MidnightBlue, 8, DashStyle.DashDotDot, PenLineCap.Round, PenLineJoin.Bevel, 5);
            context.DrawGeometry(null, styledPen, _triangle);
        }

        var widenedWave = _wave.GetWidenedGeometry(new Pen(Brushes.Black, 14, DashStyle.Dash, PenLineCap.Round, PenLineJoin.Round));
        using (context.PushTransform(Matrix.CreateTranslation(510, 675)))
        {
            context.DrawGeometry(new SolidColorBrush(Color.FromArgb(120, 255, 185, 0)), null, widenedWave);
        }

        Label(context, "Path measure, widened geometry, GeometryGroup, CombinedGeometry, dashed caps, joins, and miter limits should use native Skia resources.", 510, 730, 14, Brushes.DimGray);
    }

    private static void Label(DrawingContext context, string text, double x, double y, double size = 13, IBrush? brush = null)
    {
        using var layout = new TextLayout(text, new Typeface("Arial"), size, brush ?? Brushes.Black, maxWidth: 540);
        layout.Draw(context, new Point(x, y));
    }

    private static WriteableBitmap CreateTestBitmap()
    {
        var bitmap = new WriteableBitmap(new PixelSize(96, 96), new Vector(96, 96), PixelFormats.Bgra8888, AlphaFormat.Premul);
        using var framebuffer = bitmap.Lock();
        unsafe
        {
            var basePtr = (byte*)framebuffer.Address;
            for (var y = 0; y < framebuffer.Size.Height; y++)
            {
                var row = basePtr + y * framebuffer.RowBytes;
                for (var x = 0; x < framebuffer.Size.Width; x++)
                {
                    var c = row + x * 4;
                    var checker = ((x / 12) + (y / 12)) % 2 == 0;
                    c[0] = checker ? (byte)220 : (byte)(40 + y * 2);      // B
                    c[1] = checker ? (byte)(80 + x) : (byte)220;          // G
                    c[2] = checker ? (byte)40 : (byte)(230 - x);          // R
                    c[3] = 255;                                           // A
                }
            }
        }

        return bitmap;
    }

    private static global::Avalonia.Media.Geometry CreateGroupGeometry()
    {
        var group = new GeometryGroup
        {
            FillRule = FillRule.NonZero
        };

        group.Children.Add(new RectangleGeometry(new Rect(0, 22, 100, 70)));
        group.Children.Add(new EllipseGeometry(new Rect(62, 0, 110, 110)));
        group.Children.Add(global::Avalonia.Media.Geometry.Parse("M 25,110 L 92,52 L 165,110 Z"));
        return group;
    }

    private static global::Avalonia.Media.Geometry CreateCombinedGeometry()
    {
        return new CombinedGeometry(
            GeometryCombineMode.Exclude,
            new RectangleGeometry(new Rect(0, 0, 170, 110)),
            new EllipseGeometry(new Rect(42, 18, 88, 74)));
    }
}

internal static class Program
{
    private const string SmokeArgument = "--skianative-smoke";
    private const string BackendArgumentPrefix = "--backend=";

    public static SampleBackend SelectedBackend { get; private set; } = SampleBackend.SkiaNative;

    private static string? TryFindNativeLibrary()
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
            var candidate = System.IO.Path.Combine(dir.FullName, "artifacts", "native", rid, "libSkiaNativeAvalonia.dylib");
            if (System.IO.File.Exists(candidate))
            {
                return candidate;
            }
        }

        return null;
    }

    [STAThread]
    public static void Main(string[] args)
    {
        BuildAvaloniaApp(args).StartWithClassicDesktopLifetime(args);
    }

    public static AppBuilder BuildAvaloniaApp(string[]? args = null)
    {
        SelectedBackend = ResolveBackend(args);
        var smokeMode = IsSmokeMode(args);
        var builder = AppBuilder.Configure<App>().UsePlatformDetect();

        if (SelectedBackend == SampleBackend.AvaloniaSkia)
        {
            return builder.UseSkia();
        }

        return builder.UseSkiaNative(new SkiaNativeOptions
            {
                EnableDiagnostics = true,
                EnableCpuFallback = true,
                InitialCommandBufferCapacity = 1024,
                MaxGpuResourceBytes = 4L * 1024 * 1024,
                PurgeGpuResourcesAfterFrame = true,
                NativeLibraryPath = TryFindNativeLibrary(),
                DiagnosticsCallback = smokeMode ? static frame =>
                {
                    Console.WriteLine(
                        "SKIANATIVE_FRAME " +
                        $"id={frame.FrameId} " +
                        $"commands={frame.CommandCount} " +
                        $"transitions={frame.NativeTransitionCount} " +
                        $"nativeResult={frame.NativeResult} " +
                        $"flushMs={frame.FlushElapsed.TotalMilliseconds:0.###} " +
                        $"gpuBytes={frame.GpuResourceBytes} " +
                        $"gpuPurgeableBytes={frame.GpuPurgeableBytes} " +
                        $"gpuCount={frame.GpuResourceCount} " +
                        $"gpuLimit={frame.GpuResourceLimit}");
                } : null
            });
    }

    private static SampleBackend ResolveBackend(string[]? args)
    {
        var requested = Environment.GetEnvironmentVariable("SKIANATIVE_SAMPLE_BACKEND");

        if (args is { Length: > 0 })
        {
            for (var i = 0; i < args.Length; i++)
            {
                var arg = args[i];
                if (arg.StartsWith(BackendArgumentPrefix, StringComparison.OrdinalIgnoreCase))
                {
                    requested = arg[BackendArgumentPrefix.Length..];
                }
                else if (string.Equals(arg, "--backend", StringComparison.OrdinalIgnoreCase) && i + 1 < args.Length)
                {
                    requested = args[++i];
                }
                else if (string.Equals(arg, "--avalonia-skia", StringComparison.OrdinalIgnoreCase))
                {
                    requested = "avalonia-skia";
                }
                else if (string.Equals(arg, "--skianative", StringComparison.OrdinalIgnoreCase))
                {
                    requested = "skianative";
                }
            }
        }

        return SampleBackend.Parse(requested);
    }

    internal static bool IsSmokeMode(string[]? args) =>
        string.Equals(Environment.GetEnvironmentVariable("SKIANATIVE_SMOKE"), "1", StringComparison.Ordinal)
        || args?.Any(static arg => string.Equals(arg, SmokeArgument, StringComparison.OrdinalIgnoreCase)) == true;
}

internal readonly record struct SampleBackend(string Key, string DisplayName, string BuilderCall)
{
    public static readonly SampleBackend SkiaNative = new(
        "skianative",
        "SkiaNative.Avalonia",
        "Uses AppBuilder.UsePlatformDetect().UseSkiaNative().");

    public static readonly SampleBackend AvaloniaSkia = new(
        "avalonia-skia",
        "Avalonia.Skia",
        "Uses AppBuilder.UsePlatformDetect().UseSkia().");

    public static SampleBackend Parse(string? value)
    {
        if (string.IsNullOrWhiteSpace(value))
        {
            return SkiaNative;
        }

        var normalized = value.Trim().Replace("_", "-", StringComparison.Ordinal).ToLowerInvariant();
        return normalized switch
        {
            "avalonia-skia" or "skia" or "old" or "baseline" => AvaloniaSkia,
            "skianative" or "skia-native" or "native" or "new" => SkiaNative,
            _ => throw new ArgumentOutOfRangeException(nameof(value), value, "Unknown sample backend. Use 'skianative' or 'avalonia-skia'.")
        };
    }
}

internal static class SampleSmokeMode
{
    public static void Configure(IClassicDesktopStyleApplicationLifetime lifetime, Window window)
    {
        if (!Program.IsSmokeMode(lifetime.Args))
        {
            return;
        }

        window.Opened += (_, _) =>
        {
            window.Activate();
            Console.WriteLine($"SKIANATIVE_SMOKE_READY pid={Environment.ProcessId}");
            PrintMarker("opened", window);

            if (IsInteractionStressEnabled())
            {
                ScheduleInteractionStress(window);
            }

            var exitDelay = GetDelay("SKIANATIVE_SMOKE_EXIT_MS", 7000);
            DispatcherTimer.RunOnce(
                () =>
                {
                    PrintMarker("before-exit", window);
                    Console.WriteLine("SKIANATIVE_SMOKE_EXIT");
                    lifetime.TryShutdown(0);
                },
                exitDelay);
        };
    }

    private static bool IsInteractionStressEnabled() =>
        string.Equals(Environment.GetEnvironmentVariable("SKIANATIVE_SMOKE_INTERACTIONS"), "1", StringComparison.Ordinal)
        || string.Equals(Environment.GetEnvironmentVariable("SKIANATIVE_SMOKE_RESIZE_SCROLL"), "1", StringComparison.Ordinal);

    private static void ScheduleInteractionStress(Window window)
    {
        var shell = window.Content as SampleShell;

        Schedule(window, shell, "resize-large", 600, static (w, _) =>
        {
            w.Width = 1180;
            w.Height = 820;
        });

        Schedule(window, shell, "scroll-down-1", 1300, static (_, s) => s?.SmokeScrollTo(520));
        Schedule(window, shell, "scroll-down-2", 1900, static (_, s) => s?.SmokeScrollTo(1040));

        Schedule(window, shell, "resize-small", 2600, static (w, _) =>
        {
            w.Width = 720;
            w.Height = 540;
        });

        Schedule(window, shell, "scroll-top", 3400, static (_, s) => s?.SmokeScrollTo(0));
        Schedule(window, shell, "resize-medium", 4200, static (w, _) =>
        {
            w.Width = 900;
            w.Height = 640;
        });

        Schedule(window, shell, "settled", 5600, static (_, _) =>
        {
            if (string.Equals(Environment.GetEnvironmentVariable("SKIANATIVE_SMOKE_FORCE_GC"), "1", StringComparison.Ordinal))
            {
                GC.Collect(2, GCCollectionMode.Forced, blocking: true, compacting: true);
            }
        });
    }

    private static void Schedule(Window window, SampleShell? shell, string phase, int milliseconds, Action<Window, SampleShell?> action)
    {
        DispatcherTimer.RunOnce(
            () =>
            {
                PrintMarker($"{phase}-before", window);
                action(window, shell);
                PrintMarker($"{phase}-after", window);
            },
            TimeSpan.FromMilliseconds(milliseconds));
    }

    private static void PrintMarker(string phase, Window window)
    {
        var managed = GC.GetGCMemoryInfo();
        Console.WriteLine(
            "SKIANATIVE_SMOKE_MARK " +
            $"phase={phase} " +
            $"window={window.Width:0}x{window.Height:0} " +
            $"workingSet={Environment.WorkingSet} " +
            $"gcHeap={managed.HeapSizeBytes} " +
            $"managedTotal={GC.GetTotalMemory(forceFullCollection: false)}");
    }

    private static TimeSpan GetDelay(string name, int defaultMilliseconds)
    {
        return int.TryParse(Environment.GetEnvironmentVariable(name), out var value) && value > 0
            ? TimeSpan.FromMilliseconds(value)
            : TimeSpan.FromMilliseconds(defaultMilliseconds);
    }
}
