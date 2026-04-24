using System;
using System.IO;
using System.Runtime.InteropServices;
using Avalonia;
using Avalonia.Native;
using SkiaNativeOptions = global::SkiaNative.Avalonia.SkiaNativeOptions;

namespace MotionMark.SkiaNative.AvaloniaApp;

internal static class Program
{
    [STAThread]
    public static void Main(string[] args)
    {
        BuildAvaloniaApp().StartWithClassicDesktopLifetime(args);
    }

    public static AppBuilder BuildAvaloniaApp()
    {
        var builder = AppBuilder.Configure<App>();
        if (OperatingSystem.IsMacOS() && TryFindAvaloniaNativeLibrary() is { } avaloniaNativeLibrary)
        {
            builder = builder.With(new AvaloniaNativePlatformOptions
            {
                AvaloniaNativeLibraryPath = avaloniaNativeLibrary
            });
        }

        return builder
            .UsePlatformDetect()
            .UseSkiaNative(new SkiaNativeOptions
            {
                EnableDiagnostics = true,
                EnableCpuFallback = true,
                InitialCommandBufferCapacity = 16_384,
                MaxGpuResourceBytes = 32L * 1024 * 1024,
                NativeLibraryPath = TryFindNativeLibrary()
            })
            .LogToTrace();
    }

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
            var candidate = Path.Combine(dir.FullName, "artifacts", "native", rid, "libSkiaNativeAvalonia.dylib");
            if (File.Exists(candidate))
            {
                return candidate;
            }
        }

        return null;
    }

    private static string? TryFindAvaloniaNativeLibrary()
    {
        var environmentOverride = Environment.GetEnvironmentVariable("AVALONIA_NATIVE_LIBRARY_PATH");
        if (File.Exists(environmentOverride))
        {
            return environmentOverride;
        }

        var baseDirectory = new DirectoryInfo(AppContext.BaseDirectory);
        var outputNative = Path.Combine(baseDirectory.FullName, "runtimes", "osx", "native", "libAvaloniaNative.dylib");
        if (File.Exists(outputNative))
        {
            return outputNative;
        }

        for (var dir = baseDirectory; dir is not null; dir = dir.Parent)
        {
            var sourceBuild = Path.GetFullPath(Path.Combine(dir.FullName, "..", "Avalonia", "Build", "Products", "Release", "libAvalonia.Native.OSX.dylib"));
            if (File.Exists(sourceBuild))
            {
                return sourceBuild;
            }
        }

        var packageRoot = Path.Combine(
            Environment.GetFolderPath(Environment.SpecialFolder.UserProfile),
            ".nuget",
            "packages",
            "avalonia.native");

        if (!Directory.Exists(packageRoot))
        {
            return null;
        }

        foreach (var versionDirectory in Directory.EnumerateDirectories(packageRoot).OrderDescending())
        {
            var packageNative = Path.Combine(versionDirectory, "runtimes", "osx", "native", "libAvaloniaNative.dylib");
            if (File.Exists(packageNative))
            {
                return packageNative;
            }
        }

        return null;
    }
}
