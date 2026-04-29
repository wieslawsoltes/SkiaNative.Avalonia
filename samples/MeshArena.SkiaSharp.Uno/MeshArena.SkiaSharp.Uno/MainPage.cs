using MeshArena.SkiaSharp.Uno.Controls;
using Microsoft.UI.Text;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Media;

namespace MeshArena.SkiaSharp.Uno;

public sealed partial class MainPage : Page
{
    private readonly TextBlock _fps = Metric("FPS", "--");
    private readonly TextBlock _frame = Metric("Frame", "-- ms");
    private readonly TextBlock _render = Metric("Render", "-- ms");
    private readonly TextBlock _score = Metric("Light seeds", "--");
    private readonly TextBlock _shield = Metric("Health", "--%");
    private readonly TextBlock _input = Metric("Input", "--");
    private readonly TextBlock _enemies = Metric("Wisps", "--");
    private readonly TextBlock _projectiles = Metric("Particles", "--");
    private readonly TextBlock _entities = Metric("Entities", "--");
    private readonly TextBlock _vertices = Metric("Vertices", "--");
    private readonly TextBlock _drawCalls = Metric("Draw calls", "--");
    private readonly TextBlock _uniforms = Metric("Uniforms", "-- B");
    private readonly TextBlock _status = new()
    {
        Text = "Waiting for first Uno SKCanvasElement render pass.",
        Foreground = Solid(0xFF9DAEC4),
        TextWrapping = TextWrapping.Wrap
    };

    public MainPage()
    {
        Content = BuildContent();
    }

    private Grid BuildContent()
    {
        var root = new Grid
        {
            Background = Solid(0xFF060A12)
        };
        root.RowDefinitions.Add(new RowDefinition { Height = GridLength.Auto });
        root.RowDefinitions.Add(new RowDefinition { Height = new GridLength(1, GridUnitType.Star) });
        root.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(300) });
        root.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });

        var header = BuildHeader();
        Grid.SetColumnSpan(header, 2);
        root.Children.Add(header);

        var surface = new MeshArenaSurface();
        surface.StatsUpdated += OnStatsUpdated;

        var sidebar = BuildSidebar();
        Grid.SetRow(sidebar, 1);
        root.Children.Add(sidebar);

        var surfaceHost = new Border
        {
            Margin = new Thickness(0, 16, 16, 16),
            CornerRadius = new CornerRadius(18),
            BorderBrush = Solid(0xFF263E5A),
            BorderThickness = new Thickness(1),
            Child = surface
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
                new GradientStop { Color = ColorFrom(0xFF0C1422), Offset = 0 },
                new GradientStop { Color = ColorFrom(0xFF0F1D30), Offset = 0.62 },
                new GradientStop { Color = ColorFrom(0xFF08101C), Offset = 1 }
            }
        },
        Child = new StackPanel
        {
            Spacing = 4,
            Children =
            {
                new TextBlock
                {
                    Text = "Luma Grove: SkiaSharp mesh forest platformer",
                    FontSize = 26,
                    FontWeight = FontWeights.SemiBold,
                    Foreground = Solid(0xFFFFFFFF)
                },
                new TextBlock
                {
                    Text = "Original luminous forest game using PR 3779 SKMesh: parallax forest shader, spirit trail, batched procedural characters, hazards, collectibles, and particles.",
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
        CornerRadius = new CornerRadius(18),
        Background = Solid(0xFF0C1420),
        BorderBrush = Solid(0xFF304966),
        BorderThickness = new Thickness(1),
        Child = new StackPanel
        {
            Spacing = 16,
            Children =
            {
                SectionTitle("Render path"),
                _status,
                Divider(),
                SectionTitle("Frame"),
                _fps,
                _frame,
                _render,
                Divider(),
                SectionTitle("Luma Grove"),
                _score,
                _shield,
                _input,
                _enemies,
                _projectiles,
                Divider(),
                SectionTitle("Scene"),
                _entities,
                _vertices,
                _drawCalls,
                _uniforms,
                Divider(),
                new TextBlock
                {
                    Text = "Controls: A/D or arrows move, Space/W/Up jumps, Shift/Enter or mouse click dashes toward the pointer, S/Down slows on ledges, R respawns. Collect light seeds, avoid thorn beds, and dodge shadow wisps. Everything in the playfield is original procedural mesh/shader artwork; no fallback renderer is used.",
                    TextWrapping = TextWrapping.Wrap,
                    Foreground = Solid(0xFF9DAEC4)
                }
            }
        }
    };

    private void OnStatsUpdated(object? sender, MeshArenaStats stats)
    {
        _fps.Text = $"FPS {stats.FramesPerSecond:F1}";
        _frame.Text = $"Frame {stats.FrameMilliseconds:F2} ms";
        _render.Text = $"Render {stats.RenderMilliseconds:F2} ms";
        _score.Text = $"Light seeds {stats.Score:N0}";
        _shield.Text = $"Health {stats.ShieldPercent:N0}%";
        _input.Text = $"Input {stats.InputMode}";
        _enemies.Text = $"Wisps {stats.ActiveEnemies:N0}";
        _projectiles.Text = $"Particles {stats.ActiveProjectiles:N0}";
        _entities.Text = $"Entities {stats.EntityCount:N0}";
        _vertices.Text = $"Vertices {stats.VertexCount:N0}";
        _drawCalls.Text = $"Draw calls {stats.DrawCalls:N0}";
        _uniforms.Text = $"Uniforms {stats.UniformBytes:N0} B";
        _status.Text = $"Active: {stats.Backend}. {stats.Status}";
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
