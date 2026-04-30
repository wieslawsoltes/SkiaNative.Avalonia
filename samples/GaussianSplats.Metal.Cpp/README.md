# GaussianSplats.Metal.Cpp

Native macOS Gaussian splat renderer using AppKit, MetalKit, direct Metal draw calls, and Metal compute. This sample is intended as a rendering baseline against `MeshModeler.SkiaNative.Cpp`: it shares the same kind of input data and controls, but bypasses Skia and keeps per-frame projection, transparency, and splat shading on the GPU.

Supported inputs:

- Gaussian splat PLY files in ascii or binary little-endian format.
- PlayCanvas SOG/SOB Gaussian splat directories, `meta.json` files, JSON paths, and zip-style `.sog`/`.sob` files.

Build and run:

```bash
cmake -S samples/GaussianSplats.Metal.Cpp -B artifacts/cmake/gaussian-splats-metal-osx-arm64 -DCMAKE_BUILD_TYPE=Release -DCMAKE_OSX_ARCHITECTURES=arm64
cmake --build artifacts/cmake/gaussian-splats-metal-osx-arm64 --config Release
open artifacts/cmake/gaussian-splats-metal-osx-arm64/GaussianSplatsMetalCpp.app
```

You can also pass a model path directly:

```bash
artifacts/cmake/gaussian-splats-metal-osx-arm64/GaussianSplatsMetalCpp.app/Contents/MacOS/GaussianSplatsMetalCpp path/to/model.ply
```

Use `File > Open...` or drag a supported file/directory onto the window to load another model.

Controls:

- Left drag: orbit the camera.
- Right or middle drag: pan the view.
- Mouse wheel or trackpad scroll: zoom.
- `F` or `R`: reset camera yaw, pitch, zoom, and pan.
- `M`: toggle between fast weighted blended OIT and sorted back-to-front transparency.

The default renderer is the fast path: one static Metal buffer of normalized splats, weighted blended order-independent transparency into floating-point render targets, and a fullscreen resolve. It avoids CPU processing and avoids per-frame depth sorting, so frame cost is linear in the splat count plus fill rate. The `M` toggle switches to the quality/reference path, which uses a GPU-only depth-sort buffer; that path rebuilds the sorted index buffer only when camera orientation or scene data changes, then draws indexed instanced splats back-to-front. Depth writes remain disabled for transparent splats in sorted mode so alpha compositing stays correct.