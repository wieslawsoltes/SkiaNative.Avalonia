# MeshModeler.SkiaNative.Cpp

Native macOS Mesh Modeler sample using AppKit, MetalKit, Skia Ganesh Metal, and the new `SkMesh` API directly from C++/Objective-C++.

Gaussian splats default to a fast source-order `SkMesh` path that skips CPU depth sorting and reuses the cached vertex buffer while the camera is static. Press `M` to toggle a sorted back-to-front reference path when painter ordering matters more than rebuild cost.

Supported inputs:

- Wavefront OBJ meshes, including basic MTL diffuse colors and alpha.
- Gaussian splat PLY files in ascii or binary little-endian format.
- PlayCanvas SOG/SOB Gaussian splat directories, `meta.json` files, JSON paths, and zip-style `.sog`/`.sob` files.

Build Skia first, then configure and run the sample:

```bash
./eng/build-native.sh osx-arm64
cmake -S samples/MeshModeler.SkiaNative.Cpp -B artifacts/cmake/mesh-modeler-native-osx-arm64 -DCMAKE_BUILD_TYPE=Release -DCMAKE_OSX_ARCHITECTURES=arm64
cmake --build artifacts/cmake/mesh-modeler-native-osx-arm64 --config Release
open artifacts/cmake/mesh-modeler-native-osx-arm64/MeshModelerSkiaNativeCpp.app
```

You can also pass a model path directly:

```bash
artifacts/cmake/mesh-modeler-native-osx-arm64/MeshModelerSkiaNativeCpp.app/Contents/MacOS/MeshModelerSkiaNativeCpp path/to/model.obj
```

Use `File > Open...` or drag a supported file/directory onto the window to load another model.

Controls:

- Left drag: orbit the camera.
- Right or middle drag: pan the view.
- Mouse wheel or trackpad scroll: zoom.
- `F` or `R`: reset camera yaw, pitch, zoom, and pan.
- `M`: toggle Gaussian splats between fast source-order submission and sorted back-to-front submission.