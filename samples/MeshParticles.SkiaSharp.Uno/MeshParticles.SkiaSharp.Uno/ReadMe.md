# MeshParticles.SkiaSharp.Uno

Uno Platform desktop sample for the SkiaSharp v4 `SKMesh` API from PR 3779.

The UI intentionally mirrors `samples/MeshParticles.SkiaNative.Avalonia`: a compact metrics sidebar plus a dark mesh-particle render surface. Rendering uses Uno's `SKCanvasElement` and SkiaSharp PR 3779 APIs:

- `SKMeshSpecification.Make`
- `SKMeshVertexBuffer.Make`
- `SKMeshIndexBuffer.Make`
- `SKMesh.MakeIndexed`
- `SKCanvas.DrawMesh`

The particle buffers are static. Animation is driven by per-frame uniform data consumed by SkSL vertex and fragment shaders.

Current PR 3779 caveat: the sample intentionally does not use a fallback renderer. It submits only the `SKMesh` path every frame. If the render surface stays empty while frame metrics update, capture that as a `DrawMesh` rasterization failure using `plan/SKIASHARP_PR3779_MESH_ISSUE.md`. A minimal offscreen raster probe also produced no pixels with the same PR package build.

## Prerequisites

- .NET SDK `10.0.201` or compatible latest feature roll-forward.
- Uno SDK `6.5.31`, pinned in `global.json`.
- SkiaSharp PR 3779 NuGet artifacts, expected in `~/.skiasharp/hives/pr-3779/packages`.

Fetch the PR artifacts with the SkiaSharp helper script:

```bash
curl -fsSL https://raw.githubusercontent.com/mono/SkiaSharp/main/scripts/get-skiasharp-pr.sh | bash -s -- 3779
```

If the latest PR build has no NuGet artifact, create a local feed from the PR source and the available native macOS artifact:

```bash
./eng/bootstrap-skiasharp-pr3779.sh
```

The bootstrap script writes `SkiaSharp` and `SkiaSharp.NativeAssets.macOS` packages to `~/.skiasharp/hives/pr-3779/packages`. It also patches the current PR source to avoid an `SKPath` lazy-builder finalizer crash observed in `sk_pathbuilder_detach_path`, then clears the matching NuGet cache entries so restore uses the patched packages. If a newer PR build publishes a different package version, pass `-p:SkiaSharpPr3779Version=<version>` to restore/run.

## Run

```bash
dotnet run --project samples/MeshParticles.SkiaSharp.Uno/MeshParticles.SkiaSharp.Uno/MeshParticles.SkiaSharp.Uno.csproj
```

To use a different local PR package folder:

```bash
dotnet run \
  -p:SkiaSharpPr3779Packages=/path/to/pr-3779/packages \
  -p:SkiaSharpPr3779Version=4.147.0-pr.3779.1 \
  --project samples/MeshParticles.SkiaSharp.Uno/MeshParticles.SkiaSharp.Uno/MeshParticles.SkiaSharp.Uno.csproj
```
