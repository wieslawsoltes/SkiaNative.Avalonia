using System;
using System.Collections.Generic;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Controls.Primitives;
using Avalonia.Layout;
using Avalonia.Media;
using MotionMark.SkiaNative.AvaloniaApp.Controls;
using MotionMark.SkiaNative.AvaloniaApp.Rendering;

namespace MotionMark.SkiaNative.AvaloniaApp;

internal sealed class MainView : UserControl
{
    private readonly MainWindowViewModel _viewModel = new();
    private readonly MotionMarkSurface _surface;
    private readonly TextBlock _complexityValue;
    private readonly TextBlock _elementCountValue;
    private readonly TextBlock _pathRunCountValue;
    private readonly TextBlock _frameTimeValue;
    private readonly TextBlock _renderTimeValue;
    private readonly TextBlock _fpsValue;
    private readonly TextBlock _commandCountValue;
    private readonly TextBlock _transitionCountValue;
    private readonly TextBlock _gpuBytesValue;
    private readonly TextBlock _nativeFlushValue;
    private readonly TextBlock _nativeSessionEndValue;
    private readonly TextBlock _platformPresentValue;
    private readonly List<Action> _modeButtonRefreshers = [];
    private readonly DateTime _modeInputEnabledAtUtc = DateTime.UtcNow.AddSeconds(5);

    public MainView()
        : this(default)
    {
    }

    public MainView(MotionMarkSampleOptions options)
    {
        InitializeMode(options.FastSkiaSharpParityMode);
        DataContext = _viewModel;

        _surface = new MotionMarkSurface
        {
            Complexity = _viewModel.Complexity,
            MutateSplits = _viewModel.MutateSplits,
            UseCachedMesh = _viewModel.UseCachedMesh,
            FastSkiaSharpParityMode = _viewModel.FastSkiaSharpParityMode,
            HorizontalAlignment = HorizontalAlignment.Stretch,
            VerticalAlignment = VerticalAlignment.Stretch
        };
        _surface.FrameStatsUpdated += OnFrameStatsUpdated;

        _complexityValue = MetricValue();
        _elementCountValue = MetricValue();
        _pathRunCountValue = MetricValue();
        _frameTimeValue = MetricValue();
        _renderTimeValue = MetricValue();
        _fpsValue = MetricValue();
        _commandCountValue = MetricValue();
        _transitionCountValue = MetricValue();
        _gpuBytesValue = MetricValue();
        _nativeFlushValue = MetricValue();
        _nativeSessionEndValue = MetricValue();
        _platformPresentValue = MetricValue();

        Content = BuildLayout();
        UpdateMetrics();
    }

    protected override void OnDetachedFromVisualTree(VisualTreeAttachmentEventArgs e)
    {
        _surface.FrameStatsUpdated -= OnFrameStatsUpdated;
        base.OnDetachedFromVisualTree(e);
    }

    private Control BuildLayout()
    {
        var root = new Grid
        {
            RowDefinitions = new RowDefinitions("Auto,*"),
            Background = new SolidColorBrush(Color.FromRgb(12, 16, 24))
        };

        var header = new Border
        {
            Background = new SolidColorBrush(Color.FromRgb(20, 26, 38)),
            BorderBrush = new SolidColorBrush(Color.FromRgb(53, 63, 82)),
            BorderThickness = new Thickness(0, 0, 0, 1),
            Padding = new Thickness(18, 14),
            Child = new StackPanel
            {
                Spacing = 4,
                Children =
                {
                    new TextBlock
                    {
                        Text = "MotionMark SkiaNative.Avalonia",
                        FontSize = 24,
                        FontWeight = FontWeight.Bold,
                        Foreground = Brushes.White
                    },
                    new TextBlock
                    {
                        Text = "Port of the FastSkiaSharp MotionMark sample. Use --fastskiasharp-parity or the mode button for uniform layout, split mutation, and antialiased path rendering.",
                        FontSize = 13,
                        Foreground = new SolidColorBrush(Color.FromRgb(176, 188, 208))
                    }
                }
            }
        };
        root.Children.Add(header);

        var body = new Grid
        {
            ColumnDefinitions = new ColumnDefinitions("320,*")
        };
        Grid.SetRow(body, 1);
        root.Children.Add(body);

        var sideBar = BuildSidebar();
        Grid.SetColumn(sideBar, 0);
        body.Children.Add(sideBar);

        var surfaceHost = new Border
        {
            Margin = new Thickness(0, 16, 16, 16),
            Background = new SolidColorBrush(Color.FromRgb(9, 12, 18)),
            BorderBrush = new SolidColorBrush(Color.FromRgb(53, 63, 82)),
            BorderThickness = new Thickness(1),
            CornerRadius = new CornerRadius(14),
            ClipToBounds = true,
            Child = _surface
        };
        Grid.SetColumn(surfaceHost, 1);
        body.Children.Add(surfaceHost);

        return root;
    }

    private Control BuildSidebar()
    {
        var slider = new Slider
        {
            Minimum = 0,
            Maximum = 24,
            TickFrequency = 1,
            IsSnapToTickEnabled = true,
            Value = _viewModel.Complexity,
            HorizontalAlignment = HorizontalAlignment.Stretch
        };
        slider.PropertyChanged += (_, e) =>
        {
            if (e.Property == RangeBase.ValueProperty)
            {
                _viewModel.Complexity = (int)Math.Round(slider.Value);
                _surface.Complexity = _viewModel.Complexity;
                UpdateMetrics();
            }
        };

        var parityMode = ModeButton(
            "FastSkiaSharp parity",
            () => _viewModel.FastSkiaSharpParityMode,
            SetFastSkiaSharpParityMode);

        var mutate = ModeButton(
            "Split mutation stress",
            () => _viewModel.MutateSplits,
            value =>
            {
                _viewModel.FastSkiaSharpParityMode = false;
                _viewModel.MutateSplits = value;
                ApplyMotionModes();
                RefreshModeButtons();
            });

        var cacheMesh = ModeButton(
            "Cached path mesh",
            () => _viewModel.UseCachedMesh,
            value =>
            {
                _viewModel.FastSkiaSharpParityMode = false;
                _viewModel.UseCachedMesh = value;
                ApplyMotionModes();
                RefreshModeButtons();
            });

        return new Border
        {
            Margin = new Thickness(16),
            Padding = new Thickness(16),
            Background = new SolidColorBrush(Color.FromRgb(17, 23, 34)),
            BorderBrush = new SolidColorBrush(Color.FromRgb(53, 63, 82)),
            BorderThickness = new Thickness(1),
            CornerRadius = new CornerRadius(14),
            Child = new StackPanel
            {
                Spacing = 14,
                Children =
                {
                    SectionTitle("Workload"),
                    new TextBlock
                    {
                        Text = "Complexity controls generated path segments. Values above 10 are intentionally aggressive.",
                        TextWrapping = TextWrapping.Wrap,
                        Foreground = new SolidColorBrush(Color.FromRgb(176, 188, 208))
                    },
                    slider,
                    MetricRow("Complexity", _complexityValue),
                    parityMode,
                    mutate,
                    cacheMesh,
                    new Border
                    {
                        Height = 1,
                        Background = new SolidColorBrush(Color.FromRgb(53, 63, 82)),
                        Margin = new Thickness(0, 6)
                    },
                    SectionTitle("Scene"),
                    MetricRow("Elements", _elementCountValue),
                    MetricRow("Path runs", _pathRunCountValue),
                    new Border
                    {
                        Height = 1,
                        Background = new SolidColorBrush(Color.FromRgb(53, 63, 82)),
                        Margin = new Thickness(0, 6)
                    },
                    SectionTitle("Frame"),
                    MetricRow("Frame", _frameTimeValue),
                    MetricRow("Render", _renderTimeValue),
                    MetricRow("FPS", _fpsValue),
                    new Border
                    {
                        Height = 1,
                        Background = new SolidColorBrush(Color.FromRgb(53, 63, 82)),
                        Margin = new Thickness(0, 6)
                    },
                    SectionTitle("SkiaNative"),
                    MetricRow("Commands", _commandCountValue),
                    MetricRow("Transitions", _transitionCountValue),
                    MetricRow("Flush", _nativeFlushValue),
                    MetricRow("Submit", _nativeSessionEndValue),
                    MetricRow("Present", _platformPresentValue),
                    MetricRow("GPU cache", _gpuBytesValue)
                }
            }
        };
    }

    private void OnFrameStatsUpdated(object? sender, FrameStats stats)
    {
        _viewModel.Update(stats);
        UpdateMetrics();
    }

    private void SetFastSkiaSharpParityMode(bool enabled)
    {
        InitializeMode(enabled);
        ApplyMotionModes();
        RefreshModeButtons();
    }

    private void InitializeMode(bool fastSkiaSharpParityMode)
    {
        _viewModel.FastSkiaSharpParityMode = fastSkiaSharpParityMode;
        _viewModel.MutateSplits = fastSkiaSharpParityMode;
        _viewModel.UseCachedMesh = false;
    }

    private void ApplyMotionModes()
    {
        _surface.FastSkiaSharpParityMode = _viewModel.FastSkiaSharpParityMode;
        _surface.MutateSplits = _viewModel.MutateSplits;
        _surface.UseCachedMesh = _viewModel.UseCachedMesh;
    }

    private void RefreshModeButtons()
    {
        foreach (var refresh in _modeButtonRefreshers)
        {
            refresh();
        }
    }

    private void UpdateMetrics()
    {
        _complexityValue.Text = $"x{_viewModel.Complexity}";
        _elementCountValue.Text = _viewModel.ElementCount.ToString("N0");
        _pathRunCountValue.Text = _viewModel.PathRunCount.ToString("N0");
        _frameTimeValue.Text = $"{_viewModel.AverageFrameMilliseconds:F2} ms";
        _renderTimeValue.Text = $"{_viewModel.AverageRenderMilliseconds:F2} ms";
        _fpsValue.Text = $"{_viewModel.FramesPerSecond:F1}";
        _commandCountValue.Text = _viewModel.NativeCommandCount.ToString("N0");
        _transitionCountValue.Text = _viewModel.NativeTransitionCount.ToString("N0");
        _nativeFlushValue.Text = $"{_viewModel.NativeFlushMilliseconds:F2} ms";
        _nativeSessionEndValue.Text = $"{_viewModel.NativeSessionEndMilliseconds:F2} ms";
        _platformPresentValue.Text = $"{_viewModel.PlatformPresentMilliseconds:F2} ms";
        _gpuBytesValue.Text = FormatBytes(_viewModel.GpuResourceBytes);
    }

    private static TextBlock SectionTitle(string text)
    {
        return new TextBlock
        {
            Text = text,
            FontSize = 15,
            FontWeight = FontWeight.Bold,
            Foreground = Brushes.White
        };
    }

    private static Control MetricRow(string label, TextBlock value)
    {
        var grid = new Grid
        {
            ColumnDefinitions = new ColumnDefinitions("*,Auto")
        };

        grid.Children.Add(new TextBlock
        {
            Text = label,
            Foreground = new SolidColorBrush(Color.FromRgb(176, 188, 208)),
            VerticalAlignment = VerticalAlignment.Center
        });

        Grid.SetColumn(value, 1);
        grid.Children.Add(value);
        return grid;
    }

    private static TextBlock MetricValue()
    {
        return new TextBlock
        {
            Foreground = Brushes.White,
            FontWeight = FontWeight.SemiBold,
            VerticalAlignment = VerticalAlignment.Center
        };
    }

    private Button ModeButton(string label, Func<bool> getValue, Action<bool> setValue)
    {
        var button = new Button
        {
            Focusable = false,
            HorizontalAlignment = HorizontalAlignment.Stretch,
            HorizontalContentAlignment = HorizontalAlignment.Center,
            Padding = new Thickness(10, 7)
        };

        void Update()
        {
            var enabled = getValue();
            button.Content = $"{label}: {(enabled ? "ON" : "OFF")}";
            button.Background = new SolidColorBrush(enabled
                ? Color.FromRgb(0, 127, 255)
                : Color.FromRgb(31, 39, 53));
            button.BorderBrush = new SolidColorBrush(enabled
                ? Color.FromRgb(64, 170, 255)
                : Color.FromRgb(75, 88, 112));
            button.Foreground = Brushes.White;
        }

        _modeButtonRefreshers.Add(Update);

        button.Click += (_, _) =>
        {
            if (DateTime.UtcNow < _modeInputEnabledAtUtc)
            {
                Update();
                return;
            }

            setValue(!getValue());
            Update();
        };

        Update();
        return button;
    }

    private static string FormatBytes(ulong bytes)
    {
        const double kib = 1024;
        const double mib = kib * 1024;
        if (bytes >= (ulong)mib)
        {
            return $"{bytes / mib:F1} MiB";
        }

        if (bytes >= (ulong)kib)
        {
            return $"{bytes / kib:F1} KiB";
        }

        return $"{bytes} B";
    }
}
