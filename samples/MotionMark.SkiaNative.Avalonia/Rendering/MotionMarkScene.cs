using System;
using System.Collections.Generic;
using System.Runtime.InteropServices;
using Avalonia;
using Avalonia.Media;
using SkiaNativePathStreamElement = global::SkiaNative.Avalonia.SkiaNativePathStreamElement;
using SkiaNativePathStreamKind = global::SkiaNative.Avalonia.SkiaNativePathStreamKind;

namespace MotionMark.SkiaNative.AvaloniaApp.Rendering;

internal sealed class MotionMarkScene
{
    private const int GridWidth = 80;
    private const int GridHeight = 40;

    private static readonly Color[] s_palette =
    [
        Color.FromRgb(0x10, 0x10, 0x10),
        Color.FromRgb(0x80, 0x80, 0x80),
        Color.FromRgb(0xC0, 0xC0, 0xC0),
        Color.FromRgb(0x10, 0x10, 0x10),
        Color.FromRgb(0x80, 0x80, 0x80),
        Color.FromRgb(0xC0, 0xC0, 0xC0),
        Color.FromRgb(0xE0, 0x10, 0x40),
    ];

    private static readonly (int X, int Y)[] s_offsets =
    [
        (-4, 0),
        (2, 0),
        (1, -2),
        (1, 2),
    ];

    private readonly List<Element> _elements = [];
    private readonly Random _random = new();
    private GridPoint _lastGridPoint = new(GridWidth / 2, GridHeight / 2);
    private SkiaNativePathStreamElement[] _streamElements = [];
    private Size _streamSize;
    private bool _streamFastSkiaSharpParityMode;
    private bool _streamDirty = true;
    private int _complexity = 8;
    private int _pathRunCount;
    private int _version;

    public int Complexity => _complexity;
    public int ElementCount => _elements.Count;
    public int PathRunCount => _pathRunCount;

    public void SetComplexity(int complexity)
    {
        complexity = Math.Clamp(complexity, 0, 24);
        if (_complexity == complexity)
        {
            return;
        }

        _complexity = complexity;
        Resize(ComputeElementCount(_complexity));
    }

    public MotionMarkSceneRenderData GetRenderData(Size size, bool fastSkiaSharpParityMode)
    {
        Resize(ComputeElementCount(_complexity));
        EnsureStreamElements(size, fastSkiaSharpParityMode);
        return new MotionMarkSceneRenderData(_streamElements, _elements.Count, _pathRunCount, _version);
    }

    public void MutateSplitsForNextFrame(Size size, bool fastSkiaSharpParityMode)
    {
        MutateSplitFlags(size, fastSkiaSharpParityMode);
    }

    public void ClearSnapshot()
    {
        _streamElements = [];
        _streamSize = default;
        _streamFastSkiaSharpParityMode = false;
        _streamDirty = true;
        _pathRunCount = 0;
    }

    private void EnsureStreamElements(Size size, bool fastSkiaSharpParityMode)
    {
        if (!_streamDirty &&
            _streamElements.Length == _elements.Count &&
            _streamSize == size &&
            _streamFastSkiaSharpParityMode == fastSkiaSharpParityMode)
        {
            return;
        }

        if (_streamElements.Length != _elements.Count)
        {
            _streamElements = new SkiaNativePathStreamElement[_elements.Count];
        }

        var layout = CalculateLayout(size, fastSkiaSharpParityMode);
        var elements = CollectionsMarshal.AsSpan(_elements);
        for (var i = 0; i < elements.Length; i++)
        {
            _streamElements[i] = CreateStreamElement(elements[i], layout);
        }

        _streamSize = size;
        _streamFastSkiaSharpParityMode = fastSkiaSharpParityMode;
        _pathRunCount = CalculatePathRunCount(elements);
        _streamDirty = false;
        _version++;
    }

    private void Resize(int count)
    {
        var current = _elements.Count;
        if (count == current)
        {
            return;
        }

        if (count < current)
        {
            _elements.RemoveRange(count, current - count);
            _lastGridPoint = count > 0
                ? _elements[^1].End
                : new GridPoint(GridWidth / 2, GridHeight / 2);
            _streamDirty = true;
            _version++;
            return;
        }

        _elements.Capacity = Math.Max(_elements.Capacity, count);
        _lastGridPoint = current == 0
            ? new GridPoint(GridWidth / 2, GridHeight / 2)
            : _elements[^1].End;

        for (var i = current; i < count; i++)
        {
            var element = CreateRandomElement(_lastGridPoint);
            _elements.Add(element);
            _lastGridPoint = element.End;
        }

        _streamDirty = true;
        _version++;
    }

    private void MutateSplitFlags(Size size, bool fastSkiaSharpParityMode)
    {
        var changed = false;
        var layout = CalculateLayout(size, fastSkiaSharpParityMode);
        var elements = CollectionsMarshal.AsSpan(_elements);
        for (var i = 0; i < elements.Length; i++)
        {
            if (_random.NextDouble() <= 0.995)
            {
                continue;
            }

            elements[i].Split = !elements[i].Split;
            if (!_streamDirty && _streamSize == size && i < _streamElements.Length)
            {
                _streamElements[i] = CreateStreamElement(elements[i], layout);
            }

            changed = true;
        }

        if (changed)
        {
            _pathRunCount = CalculatePathRunCount(elements);
            _version++;
        }
    }

    private Element CreateRandomElement(GridPoint last)
    {
        var segmentType = _random.Next(4);
        var next = RandomPoint(last);

        var element = new Element
        {
            Start = last
        };

        if (segmentType < 2)
        {
            element.Kind = SegmentKind.Line;
            element.End = next;
        }
        else if (segmentType == 2)
        {
            var end = RandomPoint(next);
            element.Kind = SegmentKind.Quad;
            element.Control1 = next;
            element.End = end;
        }
        else
        {
            var control2 = RandomPoint(next);
            var end = RandomPoint(next);
            element.Kind = SegmentKind.Cubic;
            element.Control1 = next;
            element.Control2 = control2;
            element.End = end;
        }

        element.Color = s_palette[_random.Next(s_palette.Length)];
        element.Width = (float)(Math.Pow(_random.NextDouble(), 5) * 20.0 + 1.0);
        element.Split = _random.Next(2) == 0;
        return element;
    }

    private GridPoint RandomPoint(GridPoint last)
    {
        var offset = s_offsets[_random.Next(s_offsets.Length)];

        var x = last.X + offset.X;
        if (x < 0 || x > GridWidth)
        {
            x -= offset.X * 2;
        }

        var y = last.Y + offset.Y;
        if (y < 0 || y > GridHeight)
        {
            y -= offset.Y * 2;
        }

        return new GridPoint(x, y);
    }

    private static SkiaNativePathStreamElement CreateStreamElement(Element element, MotionMarkSceneLayout layout)
    {
        var kind = element.Kind switch
        {
            SegmentKind.Quad => SkiaNativePathStreamKind.Quad,
            SegmentKind.Cubic => SkiaNativePathStreamKind.Cubic,
            _ => SkiaNativePathStreamKind.Line
        };

        return new SkiaNativePathStreamElement(
            kind,
            element.Split ? SkiaNativePathStreamElement.Split : 0,
            element.Color,
            element.Width,
            element.Start.ToPoint(layout),
            element.Control1.ToPoint(layout),
            element.Control2.ToPoint(layout),
            element.End.ToPoint(layout));
    }

    private static MotionMarkSceneLayout CalculateLayout(Size size, bool fastSkiaSharpParityMode)
    {
        var scaleX = size.Width / (GridWidth + 1);
        var scaleY = size.Height / (GridHeight + 1);
        if (!fastSkiaSharpParityMode)
        {
            return new MotionMarkSceneLayout(Math.Max(1, scaleX), Math.Max(1, scaleY), 0, 0);
        }

        var uniformScale = Math.Min(scaleX, scaleY);
        var offsetX = (size.Width - uniformScale * (GridWidth + 1)) * 0.5;
        var offsetY = (size.Height - uniformScale * (GridHeight + 1)) * 0.5;
        return new MotionMarkSceneLayout(uniformScale, uniformScale, offsetX, offsetY);
    }

    private static int CalculatePathRunCount(ReadOnlySpan<Element> elements)
    {
        if (elements.IsEmpty)
        {
            return 0;
        }

        var count = 1;
        for (var i = 0; i < elements.Length - 1; i++)
        {
            if (elements[i].Split)
            {
                count++;
            }
        }

        return count;
    }

    private static int ComputeElementCount(int complexity)
    {
        if (complexity < 10)
        {
            return (complexity + 1) * 1_000;
        }

        var extended = (complexity - 8) * 10_000;
        return Math.Min(extended, 120_000);
    }

    private enum SegmentKind : byte
    {
        Line,
        Quad,
        Cubic
    }

    private struct Element
    {
        public SegmentKind Kind;
        public GridPoint Start;
        public GridPoint Control1;
        public GridPoint Control2;
        public GridPoint End;
        public Color Color;
        public float Width;
        public bool Split;
    }

    private readonly struct GridPoint(int x, int y)
    {
        public int X { get; } = x;
        public int Y { get; } = y;

        public Point ToPoint(MotionMarkSceneLayout layout) => new(
            layout.OffsetX + (X + 0.5) * layout.ScaleX,
            layout.OffsetY + (Y + 0.5) * layout.ScaleY);
    }
}

internal readonly record struct MotionMarkSceneLayout(double ScaleX, double ScaleY, double OffsetX, double OffsetY);

internal readonly record struct MotionMarkSceneRenderData(
    SkiaNativePathStreamElement[] Elements,
    int ElementCount,
    int PathRunCount,
    int Version);
