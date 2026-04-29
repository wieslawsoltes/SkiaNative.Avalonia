using System;
using System.IO;
using MeshModeler.SkiaSharp.Uno.Controls;
using Microsoft.UI.Text;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Media;
using Windows.Storage;
using Windows.Storage.Pickers;

namespace MeshModeler.SkiaSharp.Uno;

public sealed partial class MainPage : Page
{
    private readonly MeshModelerSurface _surface = new();
    private readonly TextBlock _fps = Metric("FPS", "--");
    private readonly TextBlock _frame = Metric("Frame", "-- ms");
    private readonly TextBlock _render = Metric("Render", "-- ms");
    private readonly TextBlock _meshVertices = Metric("Model vertices", "--");
    private readonly TextBlock _meshTriangles = Metric("Model triangles", "--");
    private readonly TextBlock _submittedVertices = Metric("Submitted vertices", "--");
    private readonly TextBlock _submittedIndices = Metric("Submitted indices", "--");
    private readonly TextBlock _drawCalls = Metric("Draw calls", "--");
    private readonly TextBlock _uniforms = Metric("Uniforms", "-- B");
    private readonly TextBlock _selected = Metric("Selected", "none");
    private readonly TextBlock _camera = Metric("Camera", "--");
    private readonly TextBlock _mode = Metric("Mode", "--");
    private readonly TextBlock _status = new()
    {
        Text = "Waiting for first Uno SKCanvasElement render pass.",
        Foreground = Solid(0xFF9DAEC4),
        TextWrapping = TextWrapping.Wrap
    };

    public MainPage()
    {
        _surface.StatsUpdated += OnStatsUpdated;
        Content = BuildContent();
        TryLoadStartupModel();
    }

    private Grid BuildContent()
    {
        var root = new Grid
        {
            Background = Solid(0xFF070A0F)
        };
        root.RowDefinitions.Add(new RowDefinition { Height = GridLength.Auto });
        root.RowDefinitions.Add(new RowDefinition { Height = new GridLength(1, GridUnitType.Star) });
        root.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(340) });
        root.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });

        var header = BuildHeader();
        Grid.SetColumnSpan(header, 2);
        root.Children.Add(header);

        var sidebar = BuildSidebar();
        Grid.SetRow(sidebar, 1);
        root.Children.Add(sidebar);

        var surfaceHost = new Border
        {
            Margin = new Thickness(0, 16, 16, 16),
            CornerRadius = new CornerRadius(20),
            BorderBrush = Solid(0xFF2E4964),
            BorderThickness = new Thickness(1),
            Background = Solid(0xFF090E16),
            Child = _surface
        };
        Grid.SetRow(surfaceHost, 1);
        Grid.SetColumn(surfaceHost, 1);
        root.Children.Add(surfaceHost);

        return root;
    }

    private static Border BuildHeader() => new()
    {
        Padding = new Thickness(24, 18, 24, 18),
        BorderBrush = Solid(0x467897BC),
        BorderThickness = new Thickness(0, 0, 0, 1),
        Background = new LinearGradientBrush
        {
            StartPoint = new Windows.Foundation.Point(0, 0),
            EndPoint = new Windows.Foundation.Point(1, 0),
            GradientStops =
            {
                new GradientStop { Color = ColorFrom(0xFF101621), Offset = 0 },
                new GradientStop { Color = ColorFrom(0xFF15263A), Offset = 0.55 },
                new GradientStop { Color = ColorFrom(0xFF071018), Offset = 1 }
            }
        },
        Child = new StackPanel
        {
            Spacing = 4,
            Children =
            {
                new TextBlock
                {
                    Text = "Mesh Modeler: Uno + SkiaSharp v4 SKMesh",
                    FontSize = 26,
                    FontWeight = FontWeights.SemiBold,
                    Foreground = Solid(0xFFFFFFFF)
                },
                new TextBlock
                {
                    Text = "OBJ loading, Gaussian splat PLY loading, orbit/pan/zoom controls, vertex editing, UV texture coordinates, depth sorting, and shader-driven visualization.",
                    FontSize = 14,
                    Foreground = Solid(0xFFB2C2D8)
                }
            }
        }
    };

    private Border BuildSidebar() => new()
    {
        Margin = new Thickness(16),
        Padding = new Thickness(18),
        CornerRadius = new CornerRadius(20),
        Background = Solid(0xFF0B121D),
        BorderBrush = Solid(0xFF304966),
        BorderThickness = new Thickness(1),
        Child = new ScrollViewer
        {
            VerticalScrollBarVisibility = ScrollBarVisibility.Auto,
            Content = new StackPanel
            {
                Spacing = 14,
                Children =
                {
                    SectionTitle("Model"),
                    ButtonRow(
                        Button("Load OBJ", OnLoadObjClicked),
                        Button("Load PLY", OnLoadPlyClicked),
                        Button("Splats", (_, _) => _surface.LoadSampleSplats())),
                    ButtonRow(
                        Button("Torus", (_, _) => _surface.LoadSampleTorus()),
                        Button("Cube", (_, _) => _surface.LoadSampleCube())),
                    ButtonRow(
                        Button("Edit", (_, _) => _surface.ToggleEditMode()),
                        Button("Handles", (_, _) => _surface.ToggleVertexHandles()),
                        Button("Mesh Grid", (_, _) => _surface.ToggleMeshGrid())),
                    ButtonRow(
                        Button("UV", (_, _) => _surface.SetShadingMode(0)),
                        Button("Depth", (_, _) => _surface.SetShadingMode(1)),
                        Button("Normals", (_, _) => _surface.SetShadingMode(2))),
                    _status,
                    Divider(),
                    SectionTitle("Frame"),
                    _fps,
                    _frame,
                    _render,
                    Divider(),
                    SectionTitle("Geometry"),
                    _meshVertices,
                    _meshTriangles,
                    _submittedVertices,
                    _submittedIndices,
                    _drawCalls,
                    _uniforms,
                    Divider(),
                    SectionTitle("View/Edit"),
                    _selected,
                    _camera,
                    _mode,
                    Divider(),
                    new TextBlock
                    {
                        Text = "Mouse: left drag orbits, right/middle drag pans, wheel zooms. Load OBJ for mesh editing or PLY for Gaussian splats. Press E for edit mode, H toggles vertex handles, G toggles mesh grid, then click a vertex and drag it in the camera plane. Delete resets selected vertex. F/R resets view. 1/2/3 switch material/depth/normal shader modes.",
                        TextWrapping = TextWrapping.Wrap,
                        Foreground = Solid(0xFF9DAEC4)
                    }
                }
            }
        }
    };

    private async void OnLoadObjClicked(object sender, RoutedEventArgs e)
    {
        try
        {
            var picker = new FileOpenPicker();
            picker.FileTypeFilter.Add(".obj");
            var file = await picker.PickSingleFileAsync();
            if (file is null)
            {
                return;
            }

            if (!string.IsNullOrWhiteSpace(file.Path) && File.Exists(file.Path))
            {
                _surface.LoadObjFile(file.Path);
            }
            else
            {
                var text = await FileIO.ReadTextAsync(file);
                _surface.LoadObjText(text, file.Name);
            }
        }
        catch (Exception ex)
        {
            _status.Text = $"OBJ load failed: {ex.Message}";
        }
    }

    private async void OnLoadPlyClicked(object sender, RoutedEventArgs e)
    {
        try
        {
            var picker = new FileOpenPicker();
            picker.FileTypeFilter.Add(".ply");
            var file = await picker.PickSingleFileAsync();
            if (file is null)
            {
                return;
            }

            if (!string.IsNullOrWhiteSpace(file.Path) && File.Exists(file.Path))
            {
                _surface.LoadGaussianSplatFile(file.Path);
            }
            else
            {
                _status.Text = "PLY load failed: Uno file picker did not provide a local path for streaming binary Gaussian splats.";
            }
        }
        catch (Exception ex)
        {
            _status.Text = $"PLY load failed: {ex.Message}";
        }
    }

    private void TryLoadStartupModel()
    {
        var splatPath = Environment.GetEnvironmentVariable("MESHMODELER_PLY");
        if (string.IsNullOrWhiteSpace(splatPath))
        {
            splatPath = Environment.GetEnvironmentVariable("MESHMODELER_SPLAT");
        }

        if (!string.IsNullOrWhiteSpace(splatPath) && File.Exists(splatPath))
        {
            try
            {
                _surface.LoadGaussianSplatFile(splatPath);
                return;
            }
            catch (Exception ex)
            {
                _status.Text = $"Startup PLY load failed: {ex.Message}";
                return;
            }
        }

        var path = Environment.GetEnvironmentVariable("MESHMODELER_OBJ");
        if (string.IsNullOrWhiteSpace(path) || !File.Exists(path))
        {
            return;
        }

        try
        {
            _surface.LoadObjFile(path);
        }
        catch (Exception ex)
        {
            _status.Text = $"Startup OBJ load failed: {ex.Message}";
        }
    }

    private void OnStatsUpdated(object? sender, MeshModelerStats stats)
    {
        _fps.Text = $"FPS {stats.FramesPerSecond:F1}";
        _frame.Text = $"Frame {stats.FrameMilliseconds:F2} ms";
        _render.Text = $"Render {stats.RenderMilliseconds:F2} ms";
        _meshVertices.Text = $"Model vertices {stats.MeshVertices:N0}";
        _meshTriangles.Text = $"Model triangles {stats.MeshTriangles:N0}";
        _submittedVertices.Text = $"Submitted vertices {stats.SubmittedVertices:N0}";
        _submittedIndices.Text = $"Submitted indices {stats.SubmittedIndices:N0}";
        _drawCalls.Text = $"Draw calls {stats.DrawCalls:N0}";
        _uniforms.Text = $"Uniforms {stats.UniformBytes:N0} B";
        _selected.Text = stats.SelectedVertex >= 0 ? $"Selected v{stats.SelectedVertex:N0}" : "Selected none";
        _camera.Text = $"Camera yaw {stats.CameraYawDegrees:F0} pitch {stats.CameraPitchDegrees:F0} dist {stats.CameraDistance:F2}";
        _mode.Text = $"Mode {stats.ShadingMode}";
        _status.Text = $"Active: {stats.Backend}. {stats.Status}";
    }

    private static StackPanel ButtonRow(params Button[] buttons)
    {
        var row = new StackPanel
        {
            Orientation = Orientation.Horizontal,
            Spacing = 8
        };

        foreach (var button in buttons)
        {
            row.Children.Add(button);
        }

        return row;
    }

    private static Button Button(string text, RoutedEventHandler handler)
    {
        var button = new Button
        {
            Content = text,
            Padding = new Thickness(10, 6, 10, 6),
            MinWidth = 0
        };
        button.Click += handler;
        return button;
    }

    private static TextBlock SectionTitle(string text) => new()
    {
        Text = text,
        FontSize = 16,
        FontWeight = FontWeights.SemiBold,
        Foreground = Solid(0xFFFFFFFF)
    };

    private static TextBlock Metric(string label, string value) => new()
    {
        Text = $"{label} {value}",
        FontSize = 14,
        Foreground = Solid(0xFFDBE5F2)
    };

    private static Border Divider() => new()
    {
        Height = 1,
        Background = Solid(0x467897BC),
        Margin = new Thickness(0, 2, 0, 2)
    };

    private static SolidColorBrush Solid(uint argb) => new(ColorFrom(argb));

    private static Windows.UI.Color ColorFrom(uint argb) => Windows.UI.Color.FromArgb(
        (byte)(argb >> 24),
        (byte)(argb >> 16),
        (byte)(argb >> 8),
        (byte)argb);
}
