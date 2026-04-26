using System;

namespace MotionMark.SkiaNative.AvaloniaApp;

internal readonly record struct MotionMarkSampleOptions(bool FastSkiaSharpParityMode)
{
    public static MotionMarkSampleOptions Parse(string[] args)
    {
        var parityMode = false;
        foreach (var arg in args)
        {
            if (IsFastSkiaSharpParityArg(arg))
            {
                parityMode = true;
            }
        }

        return new MotionMarkSampleOptions(parityMode);
    }

    public static string[] GetAvaloniaArgs(string[] args)
    {
        var count = 0;
        foreach (var arg in args)
        {
            if (!IsFastSkiaSharpParityArg(arg))
            {
                count++;
            }
        }

        if (count == args.Length)
        {
            return args;
        }

        var filtered = new string[count];
        var index = 0;
        foreach (var arg in args)
        {
            if (!IsFastSkiaSharpParityArg(arg))
            {
                filtered[index++] = arg;
            }
        }

        return filtered;
    }

    private static bool IsFastSkiaSharpParityArg(string arg) =>
        string.Equals(arg, "--fastskiasharp-parity", StringComparison.OrdinalIgnoreCase) ||
        string.Equals(arg, "--fast-skia-sharp-parity", StringComparison.OrdinalIgnoreCase);
}
