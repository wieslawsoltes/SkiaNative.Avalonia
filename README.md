# SkiaNative.Avalonia

`SkiaNative.Avalonia` is a standalone Avalonia rendering backend that drives Skia from native C++ through a compact C ABI. The goal is to replace `Avalonia.Skia` at the rendering-subsystem level while keeping managed code thin and reducing managed/native transitions on hot drawing paths.

The current implementation is macOS-first. It binds Avalonia Metal render sessions to Skia Ganesh Metal surfaces, renders with a bulk native command buffer, and packages native assets for `osx-arm64` and `osx-x64`.

## Status

This project is under active development and uses Avalonia private rendering APIs. It is not yet a full `Avalonia.Skia` parity replacement.

Implemented areas include:

- `AppBuilder.UseSkiaNative()` backend registration.
- macOS Metal render target binding from Avalonia `MTLTexture` to Skia Ganesh.
- CPU fallback and raster/bitmap sessions for tests and offscreen rendering.
- Bulk command buffer for common drawing operations.
- Native Skia paths, geometry operations, path measuring, widened geometry, and hit testing.
- Native bitmap decode, upload, draw, readback, resize, and PNG encoding.
- Native glyph-run rasterization from Avalonia/HarfBuzz-shaped text.
- Solid, linear, radial, conic, image, and initial scene-content brush paths.
- Opacity layers, opacity masks, styled strokes, and basic box shadows.
- Diagnostics for flush timing, command count, transition count, native result, and Ganesh resource-cache usage.
- macOS sample, smoke testing, memory measurement, and source-linked Avalonia render-test harness.

Known major gaps:

- Full effects/acrylic/drop-shadow parity.
- Complex region operations equivalent to `Avalonia.Skia` `SKRegion` behavior.
- Full VisualBrush/scalable scene-rasterization parity.
- Full image blend/interpolation and non-native bitmap import parity.
- Text fallback, metrics, and broader text layout parity hardening.
- External GPU object feature parity and resource-loss handling.
- Windows, Linux, and mobile GPU backends.

## Requirements

### Managed

- .NET SDK `10.0.201` or a compatible latest-feature SDK as defined by `global.json`.
- Avalonia `12.0.999` exact pin by default.
- Local Avalonia source at `/Users/wieslawsoltes/GitHub/Avalonia` is used automatically when present. Otherwise, package references are used.

### Native macOS

- macOS with Xcode Command Line Tools.
- `git`, `python3`, `cmake`, and `ninja` available on `PATH`.
- Skia submodule at `external/skia` pinned to commit `e7c90ecca9444fe09598f1630ab7cee2c0ee027a`.

## Installation

The package is intended to be consumed as a normal NuGet package once built or published.

### From a Local Package

Build native assets before packing so the `.nupkg` includes `runtimes/<rid>/native/libSkiaNativeAvalonia.dylib`.

```bash
./eng/build-native.sh osx-arm64 osx-x64
dotnet pack src/SkiaNative.Avalonia/SkiaNative.Avalonia.csproj -c Release -o artifacts/packages
```

Add the local package source and reference the package from an Avalonia app:

```bash
dotnet nuget add source /Users/wieslawsoltes/GitHub/SkiaNative.Avalonia/artifacts/packages -n SkiaNativeLocal
dotnet add package SkiaNative.Avalonia --source /Users/wieslawsoltes/GitHub/SkiaNative.Avalonia/artifacts/packages
```

### From a Project Reference

For repository development, reference the managed project directly:

```xml
<ItemGroup>
  <ProjectReference Include="../SkiaNative.Avalonia/src/SkiaNative.Avalonia/SkiaNative.Avalonia.csproj" />
</ItemGroup>
```

When using a project reference, either build native assets into `artifacts/native/<rid>/` or pass `SkiaNativeOptions.NativeLibraryPath` explicitly.

## Usage

Add the backend during Avalonia app startup:

```csharp
using Avalonia;

AppBuilder.Configure<App>()
    .UsePlatformDetect()
    .UseSkiaNative()
    .StartWithClassicDesktopLifetime(args);
```

With options:

```csharp
using Avalonia;
using SkiaNative.Avalonia;

AppBuilder.Configure<App>()
    .UsePlatformDetect()
    .UseSkiaNative(new SkiaNativeOptions
    {
        EnableCpuFallback = true,
        EnableDiagnostics = true,
        InitialCommandBufferCapacity = 1024,
        MaxGpuResourceBytes = 32L * 1024 * 1024,
        PurgeGpuResourcesAfterFrame = false,
        NativeLibraryPath = "/absolute/path/to/libSkiaNativeAvalonia.dylib",
        DiagnosticsCallback = frame =>
        {
            Console.WriteLine(
                $"commands={frame.CommandCount} " +
                $"transitions={frame.NativeTransitionCount} " +
                $"flushMs={frame.FlushElapsed.TotalMilliseconds:0.###} " +
                $"gpuBytes={frame.GpuResourceBytes}");
        }
    })
    .StartWithClassicDesktopLifetime(args);
```

`PurgeGpuResourcesAfterFrame` is intended for low-memory validation scenarios. It forces extra GPU synchronization and cleanup, so it can reduce throughput and should not be the default for performance testing.

## Building

### Restore and Build Managed Projects

```bash
dotnet restore SkiaNative.Avalonia.slnx
dotnet build SkiaNative.Avalonia.slnx
```

### Build Native Assets

Build both supported macOS runtime identifiers:

```bash
./eng/build-native.sh osx-arm64 osx-x64
```

Build only the current Apple Silicon target:

```bash
./eng/build-native.sh osx-arm64
```

If Skia dependencies are already synchronized, skip dependency sync for faster local native rebuilds:

```bash
SKIANATIVE_SKIP_SYNC_DEPS=1 ./eng/build-native.sh osx-arm64
```

Native outputs are copied to:

```text
artifacts/native/osx-arm64/libSkiaNativeAvalonia.dylib
artifacts/native/osx-x64/libSkiaNativeAvalonia.dylib
```

### Pack NuGet

```bash
dotnet pack src/SkiaNative.Avalonia/SkiaNative.Avalonia.csproj -c Release -o artifacts/packages
```

The package includes native dylibs only when they exist under `artifacts/native/<rid>/` before packing.

## Running the Sample

### Backend Validation Sample

Run with the SkiaNative backend:

```bash
dotnet run --project samples/SkiaNative.Avalonia.Sample/SkiaNative.Avalonia.Sample.csproj -- --skianative
```

Run with the stock Avalonia.Skia backend for comparison:

```bash
dotnet run --project samples/SkiaNative.Avalonia.Sample/SkiaNative.Avalonia.Sample.csproj -- --avalonia-skia
```

Environment variable form:

```bash
SKIANATIVE_SAMPLE_BACKEND=skianative dotnet run --project samples/SkiaNative.Avalonia.Sample/SkiaNative.Avalonia.Sample.csproj
SKIANATIVE_SAMPLE_BACKEND=avalonia-skia dotnet run --project samples/SkiaNative.Avalonia.Sample/SkiaNative.Avalonia.Sample.csproj
```

### MotionMark SkiaNative Sample

Run the MotionMark-style path workload ported from the FastSkiaSharp SkiaSharp sample:

```bash
dotnet run --project samples/MotionMark.SkiaNative.Avalonia/MotionMark.SkiaNative.Avalonia.csproj
```

FastSkiaSharp parity mode disables SkiaNative-only presentation tweaks and uses the comparison-friendly workload shape: uniform centered grid scaling, per-frame split mutation, antialiased path strokes, and no cached path mesh.

```bash
dotnet run --project samples/MotionMark.SkiaNative.Avalonia/MotionMark.SkiaNative.Avalonia.csproj -- --fastskiasharp-parity
```

This sample intentionally does not reference SkiaSharp and does not render its workload through Avalonia `Geometry` / `Pen` primitives. It prepares `SkiaNativePathCommand` snapshots, submits an Avalonia `ICustomDrawOperation` only for render-thread scheduling, leases `ISkiaNativeApiLeaseFeature`, and encodes the background, grid, and path strokes directly into the SkiaNative command buffer. With the SkiaNative backend on macOS, those commands target the Metal/Ganesh GPU surface and execute through the native C++ Skia path. The UI exposes complexity, FastSkiaSharp parity mode, optional per-frame path split mutation, frame timing, native command count, transition count, and GPU cache usage.

## Validation

### Binding Benchmarks

Run the managed/native binding microbenchmarks in Release mode:

```bash
dotnet run -c Release --project benchmarks/SkiaNative.Avalonia.Benchmarks/SkiaNative.Avalonia.Benchmarks.csproj -- --filter '*DirectPathBindingBenchmarks*'
```

The benchmark project looks for `artifacts/native/<rid>/libSkiaNativeAvalonia.dylib`. Set `SKIANATIVE_NATIVE_LIBRARY=/absolute/path/libSkiaNativeAvalonia.dylib` to override discovery.

Current short-run result on Apple M3 Pro / .NET 10.0.5:

| Case | 128 paths | 2048 paths |
| --- | ---: | ---: |
| Create native path per draw | 56.9 us | 923.5 us |
| Reuse native path handles | 3.3 us | 54.2 us |

The reusable path path is roughly 17x faster in this binding-level workload and cuts managed allocation by about 60%.

### Unit Tests

```bash
dotnet test --project tests/SkiaNative.Avalonia.Tests/SkiaNative.Avalonia.Tests.csproj
```

### Source-Linked Avalonia Render Tests

```bash
dotnet test --project tests/SkiaNative.Avalonia.RenderTests/SkiaNative.Avalonia.RenderTests.csproj
```

This project source-links local Avalonia render-test sources and is used as a parity baseline. Some tests are expected to fail until the remaining `Avalonia.Skia` feature gaps are closed.

### macOS Smoke Test

```bash
./eng/smoke-sample-macos.sh
```

The smoke script stages Avalonia's native macOS dependency when needed, launches the sample, waits for `SKIANATIVE_SMOKE_READY`, captures a screenshot, and writes artifacts under `artifacts/smoke/`.

### macOS Resize/Scroll Memory Test

```bash
SKIANATIVE_SAMPLE_BACKEND=skianative ./eng/measure-sample-memory-macos.sh
SKIANATIVE_SAMPLE_BACKEND=avalonia-skia ./eng/measure-sample-memory-macos.sh
```

The memory script runs deterministic in-app resize/scroll phases, records RSS/VSZ samples, captures selected `vmmap --summary` snapshots, and writes artifacts under `artifacts/memory/`.

## GitHub Actions

The repository includes two workflows:

- `.github/workflows/ci.yml` runs managed build/test validation on Ubuntu, native macOS builds for `osx-arm64` and `osx-x64`, targeted native-backed render tests, and package validation with both native assets.
- `.github/workflows/release.yml` builds both macOS native assets, packs `SkiaNative.Avalonia`, verifies package contents, uploads workflow artifacts, creates or updates a GitHub Release for `v*` tags or manual runs, and can publish to nuget.org when `NUGET_API_KEY` is configured.

Both workflows clone the pinned Avalonia source fork/ref into `../Avalonia` and pass `AvaloniaSourceRoot` explicitly. This is required because the renderer uses Avalonia private APIs and the fallback package version is intentionally exact-pinned to the local Avalonia 12 source shape.

## Architecture

### Managed Layer

The managed layer owns Avalonia integration and object lifetime:

- `SkiaNativeApplicationExtensions` registers the backend with `UseRenderingSubsystem`.
- `SkiaNativeOptions` configures diagnostics, native library loading, command-buffer sizing, CPU fallback, and GPU cache limits.
- `NativePlatformRenderInterface` implements Avalonia rendering factory contracts.
- `NativeRenderInterfaceContext` owns the native context and creates render targets.
- `NativeMetalRenderTarget` binds Avalonia Metal platform sessions to native Skia sessions.
- `NativeFramebufferRenderTarget` provides CPU fallback and framebuffer copy-back.
- `NativeDrawingContext` records Avalonia drawing calls into a bulk command buffer.
- Geometry, imaging, text, and region wrappers hold native handles through `SafeHandle` types.

### Native Layer

The native layer is C++ and exports only a stable C ABI:

- Opaque handles represent contexts, sessions, bitmaps, paths, shaders, strokes, typefaces, glyph runs, and data blobs.
- `skn_session_flush_commands` executes a packed array of blittable drawing commands in one native transition.
- Metal sessions wrap Avalonia-provided `MTLTexture` handles into `GrBackendRenderTarget` and `SkSurface` objects.
- Raster and bitmap sessions support tests, CPU fallback, render-target bitmaps, and readback.
- Skia Ganesh resource cache limits and low-memory purge operations are controlled from managed options.

### Render Flow

```text
Avalonia visual tree
  -> Avalonia rendering subsystem
  -> SkiaNative IPlatformRenderInterface
  -> NativeDrawingContext command buffer
  -> C ABI bulk flush
  -> C++ Skia renderer
  -> Metal Ganesh surface or raster/bitmap surface
```

### Text Flow

```text
Avalonia text layout and HarfBuzz shaping
  -> glyph indices and positions
  -> native Skia typeface/glyph-run wrapper
  -> command-buffer glyph draw
  -> Skia glyph rasterization
```

Text shaping remains in Avalonia/HarfBuzz for v1. Skia `modules/skshaper` is available in the pinned Skia tree, but native shaping is intentionally deferred until it can be validated as a separate parity slice.

## Private API Policy

This package intentionally uses Avalonia private rendering APIs. The project sets:

```xml
<AvaloniaAccessUnstablePrivateApis>true</AvaloniaAccessUnstablePrivateApis>
<Avalonia_I_Want_To_Use_Private_Apis_In_Nuget_Package_And_Promise_To_Pin_The_Exact_Avalonia_Version_In_Package_Dependency>true</Avalonia_I_Want_To_Use_Private_Apis_In_Nuget_Package_And_Promise_To_Pin_The_Exact_Avalonia_Version_In_Package_Dependency>
```

Because these APIs are unstable, Avalonia package dependencies are exact-pinned. Do not widen Avalonia dependency ranges without revalidating the rendering API shape and the test suite.

## Repository Layout

```text
src/SkiaNative.Avalonia/          Managed Avalonia backend and interop layer
native/SkiaNative.Avalonia/       C++ C ABI wrapper around Skia
external/skia/                    Skia submodule
samples/SkiaNative.Avalonia.Sample/  Backend validation sample
tests/SkiaNative.Avalonia.Tests/  ABI/unit/render-focused tests
tests/SkiaNative.Avalonia.RenderTests/  Source-linked Avalonia render-test harness
eng/                              Native build, smoke, and memory scripts
artifacts/                        Generated build/test/smoke/memory outputs
plan/                             Ignored local planning and implementation notes
```

## Development Notes

- Keep hot paths batchable. Prefer extending the native command buffer over adding per-draw P/Invokes.
- Keep native exports ABI-stable and opaque-handle based.
- Keep managed resources deterministically disposable through `SafeHandle` ownership.
- Keep SkiaSharp out of v1. This backend is not a SkiaSharp compatibility bridge.
- Use `plan/` for local work plans, status notes, and measurement logs. The folder is intentionally git-ignored.

## License

MIT. See `LICENSE`.
