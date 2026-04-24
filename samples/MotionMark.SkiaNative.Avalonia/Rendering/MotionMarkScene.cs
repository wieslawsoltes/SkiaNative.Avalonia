using System;
using System.Collections.Generic;
using System.Runtime.InteropServices;
using Avalonia;
using Avalonia.Media;
using SkiaNativePathCommand = global::SkiaNative.Avalonia.SkiaNativePathCommand;

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
    private readonly Random _random = new(12345);
    private GridPoint _lastGridPoint = new(GridWidth / 2, GridHeight / 2);
    private MotionMarkSceneSnapshot? _snapshot;
    private int _complexity = 8;
    private int _version;

    public int Complexity => _complexity;
    public int ElementCount => _elements.Count;
    public int PathRunCount => _snapshot?.PathRuns.Length ?? 0;

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

    public MotionMarkSceneSnapshot GetSnapshot(Size size, bool mutateSplits)
    {
        Resize(ComputeElementCount(_complexity));

        if (mutateSplits)
        {
            MutateSplitFlags();
        }

        if (_snapshot is { } snapshot && snapshot.Version == _version && snapshot.Size == size)
        {
            return snapshot;
        }

        _snapshot = BuildSnapshot(size);
        return _snapshot;
    }

    private MotionMarkSceneSnapshot BuildSnapshot(Size size)
    {
        var pathRuns = new List<MotionMarkPathRun>(Math.Max(32, _elements.Count / 2));
        var scaleX = (float)(size.Width / (GridWidth + 1));
        var scaleY = (float)(size.Height / (GridHeight + 1));
        var uniformScale = MathF.Max(1, MathF.Min(scaleX, scaleY));
        var offsetX = (float)((size.Width - uniformScale * (GridWidth + 1)) * 0.5);
        var offsetY = (float)((size.Height - uniformScale * (GridHeight + 1)) * 0.5);

        Span<Element> elements = CollectionsMarshal.AsSpan(_elements);
        var commands = new List<SkiaNativePathCommand>(128);
        var pathStarted = false;

        for (var i = 0; i < elements.Length; i++)
        {
            ref readonly var element = ref elements[i];
            if (!pathStarted)
            {
                commands.Add(SkiaNativePathCommand.MoveTo(element.Start.ToPoint(uniformScale, offsetX, offsetY)));
                pathStarted = true;
            }

            switch (element.Kind)
            {
                case SegmentKind.Line:
                    commands.Add(SkiaNativePathCommand.LineTo(element.End.ToPoint(uniformScale, offsetX, offsetY)));
                    break;

                case SegmentKind.Quad:
                    commands.Add(SkiaNativePathCommand.QuadTo(
                        element.Control1.ToPoint(uniformScale, offsetX, offsetY),
                        element.End.ToPoint(uniformScale, offsetX, offsetY)));
                    break;

                case SegmentKind.Cubic:
                    commands.Add(SkiaNativePathCommand.CubicTo(
                        element.Control1.ToPoint(uniformScale, offsetX, offsetY),
                        element.Control2.ToPoint(uniformScale, offsetX, offsetY),
                        element.End.ToPoint(uniformScale, offsetX, offsetY)));
                    break;
            }

            var finalize = element.Split || i == elements.Length - 1;
            if (finalize)
            {
                pathRuns.Add(new MotionMarkPathRun(commands.ToArray(), element.Color, element.Width));
                commands.Clear();
                pathStarted = false;
            }
        }

        return new MotionMarkSceneSnapshot(size, _version, _elements.Count, pathRuns.ToArray());
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

        _version++;
    }

    private void MutateSplitFlags()
    {
        var changed = false;
        Span<Element> elements = CollectionsMarshal.AsSpan(_elements);
        for (var i = 0; i < elements.Length; i++)
        {
            if (_random.NextDouble() > 0.995)
            {
                elements[i].Split = !elements[i].Split;
                changed = true;
            }
        }

        if (changed)
        {
            _version++;
        }
    }

    private Element CreateRandomElement(GridPoint last)
    {
        var segmentType = _random.Next(4);
        var next = RandomPoint(last);

        var element = new Element
        {
            Start = last,
            Color = s_palette[_random.Next(s_palette.Length)],
            Width = Math.Pow(_random.NextDouble(), 5) * 20.0 + 1.0,
            Split = _random.Next(2) == 0
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
        public double Width;
        public bool Split;
    }

    private readonly struct GridPoint(int x, int y)
    {
        public int X { get; } = x;
        public int Y { get; } = y;

        public Point ToPoint(float scale, float offsetX, float offsetY)
        {
            var px = offsetX + (X + 0.5f) * scale;
            var py = offsetY + (Y + 0.5f) * scale;
            return new Point(px, py);
        }
    }
}

internal sealed record MotionMarkSceneSnapshot(Size Size, int Version, int ElementCount, MotionMarkPathRun[] PathRuns);

internal readonly record struct MotionMarkPathRun(SkiaNativePathCommand[] Commands, Color Color, double Width);
