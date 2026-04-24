namespace MotionMark.SkiaNative.AvaloniaApp.Rendering;

internal readonly record struct FrameStats(
    int Complexity,
    int ElementCount,
    int PathRunCount,
    double AverageFrameMilliseconds,
    double AverageRenderMilliseconds,
    double FramesPerSecond,
    int NativeCommandCount,
    int NativeTransitionCount,
    ulong GpuResourceBytes);
