using System;
using System.ComponentModel;
using System.Runtime.CompilerServices;
using MotionMark.SkiaNative.AvaloniaApp.Rendering;

namespace MotionMark.SkiaNative.AvaloniaApp;

internal sealed class MainWindowViewModel : INotifyPropertyChanged
{
    private int _complexity = 8;
    private int _elementCount;
    private int _pathRunCount;
    private double _averageFrameMs;
    private double _averageRenderMs;
    private double _fps;
    private int _nativeCommandCount;
    private int _nativeTransitionCount;
    private ulong _gpuResourceBytes;
    private double _nativeFlushMs;
    private double _nativeSessionEndMs;
    private double _platformPresentMs;
    private bool _mutateSplits;
    private bool _useCachedMesh;
    private bool _fastSkiaSharpParityMode;

    public event PropertyChangedEventHandler? PropertyChanged;

    public int Complexity
    {
        get => _complexity;
        set
        {
            value = Math.Clamp(value, 0, 24);
            if (_complexity != value)
            {
                _complexity = value;
                OnPropertyChanged();
            }
        }
    }

    public bool MutateSplits
    {
        get => _mutateSplits;
        set
        {
            if (_mutateSplits != value)
            {
                _mutateSplits = value;
                OnPropertyChanged();
            }
        }
    }

    public bool UseCachedMesh
    {
        get => _useCachedMesh;
        set
        {
            if (_useCachedMesh != value)
            {
                _useCachedMesh = value;
                OnPropertyChanged();
            }
        }
    }

    public bool FastSkiaSharpParityMode
    {
        get => _fastSkiaSharpParityMode;
        set
        {
            if (_fastSkiaSharpParityMode != value)
            {
                _fastSkiaSharpParityMode = value;
                OnPropertyChanged();
            }
        }
    }

    public int ElementCount
    {
        get => _elementCount;
        private set
        {
            if (_elementCount != value)
            {
                _elementCount = value;
                OnPropertyChanged();
            }
        }
    }

    public int PathRunCount
    {
        get => _pathRunCount;
        private set
        {
            if (_pathRunCount != value)
            {
                _pathRunCount = value;
                OnPropertyChanged();
            }
        }
    }

    public double AverageFrameMilliseconds
    {
        get => _averageFrameMs;
        private set
        {
            if (Math.Abs(_averageFrameMs - value) > 0.0001)
            {
                _averageFrameMs = value;
                OnPropertyChanged();
            }
        }
    }

    public double AverageRenderMilliseconds
    {
        get => _averageRenderMs;
        private set
        {
            if (Math.Abs(_averageRenderMs - value) > 0.0001)
            {
                _averageRenderMs = value;
                OnPropertyChanged();
            }
        }
    }

    public double FramesPerSecond
    {
        get => _fps;
        private set
        {
            if (Math.Abs(_fps - value) > 0.0001)
            {
                _fps = value;
                OnPropertyChanged();
            }
        }
    }

    public int NativeCommandCount
    {
        get => _nativeCommandCount;
        private set
        {
            if (_nativeCommandCount != value)
            {
                _nativeCommandCount = value;
                OnPropertyChanged();
            }
        }
    }

    public int NativeTransitionCount
    {
        get => _nativeTransitionCount;
        private set
        {
            if (_nativeTransitionCount != value)
            {
                _nativeTransitionCount = value;
                OnPropertyChanged();
            }
        }
    }

    public ulong GpuResourceBytes
    {
        get => _gpuResourceBytes;
        private set
        {
            if (_gpuResourceBytes != value)
            {
                _gpuResourceBytes = value;
                OnPropertyChanged();
            }
        }
    }

    public double NativeFlushMilliseconds
    {
        get => _nativeFlushMs;
        private set
        {
            if (Math.Abs(_nativeFlushMs - value) > 0.0001)
            {
                _nativeFlushMs = value;
                OnPropertyChanged();
            }
        }
    }

    public double NativeSessionEndMilliseconds
    {
        get => _nativeSessionEndMs;
        private set
        {
            if (Math.Abs(_nativeSessionEndMs - value) > 0.0001)
            {
                _nativeSessionEndMs = value;
                OnPropertyChanged();
            }
        }
    }

    public double PlatformPresentMilliseconds
    {
        get => _platformPresentMs;
        private set
        {
            if (Math.Abs(_platformPresentMs - value) > 0.0001)
            {
                _platformPresentMs = value;
                OnPropertyChanged();
            }
        }
    }

    public void Update(FrameStats stats)
    {
        Complexity = stats.Complexity;
        ElementCount = stats.ElementCount;
        PathRunCount = stats.PathRunCount;
        AverageFrameMilliseconds = stats.AverageFrameMilliseconds;
        AverageRenderMilliseconds = stats.AverageRenderMilliseconds;
        FramesPerSecond = stats.FramesPerSecond;
        NativeCommandCount = stats.NativeCommandCount;
        NativeTransitionCount = stats.NativeTransitionCount;
        GpuResourceBytes = stats.GpuResourceBytes;
        NativeFlushMilliseconds = stats.NativeFlushMilliseconds;
        NativeSessionEndMilliseconds = stats.NativeSessionEndMilliseconds;
        PlatformPresentMilliseconds = stats.PlatformPresentMilliseconds;
    }

    private void OnPropertyChanged([CallerMemberName] string? propertyName = null)
        => PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(propertyName));
}
