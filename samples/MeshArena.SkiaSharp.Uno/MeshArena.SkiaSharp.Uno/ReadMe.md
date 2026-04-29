# MeshArena.SkiaSharp.Uno

Uno Platform desktop sample showcasing SkiaSharp v4 PR 3779 `SKMesh` APIs in an original luminous forest platformer named **Luma Grove**.

The sample is a full rewrite of the previous arena sample. It is intentionally an original game and does not copy or import Ori assets, characters, names, or artwork. The target is the same genre-level feel: a glowing forest, spirit-like movement, side-scrolling platforming, collectibles, hazards, enemies, and layered atmosphere, all generated procedurally through mesh attributes and SkSL shaders.

## What It Demonstrates

- Camera-following side-scrolling world hosted in Uno `SKCanvasElement`.
- Original procedural game artwork with no sprite image assets.
- Indexed parallax forest background mesh using a custom vertex and fragment shader.
- Triangle-strip spirit aura trail mesh.
- Single indexed sprite/entity mesh batch for platforms, foliage, player, light seeds, shadow wisps, thorn hazards, and particles.
- Per-frame uniforms for time, viewport, world scale, camera, player location, and energy.
- CPU-side gameplay simulation for collision and input, with GPU-side rendering and procedural shading.
- No fallback renderer. If mesh content disappears while the Uno UI remains alive, the SkiaSharp PR artifact should be investigated.

## Prerequisites

- .NET SDK `10.0.201` or compatible latest feature roll-forward.
- Uno SDK `6.5.31`, pinned in `global.json`.
- SkiaSharp PR 3779 NuGet artifacts, expected in `~/.skiasharp/hives/pr-3779/packages`.

Fetch official PR artifacts if available:

```bash
curl -fsSL https://raw.githubusercontent.com/mono/SkiaSharp/main/scripts/get-skiasharp-pr.sh | bash -s -- 3779
```

If the latest PR build has no NuGet artifact, create a local feed from the PR source and native macOS artifact:

```bash
./eng/bootstrap-skiasharp-pr3779.sh
```

The bootstrap script writes `SkiaSharp` and `SkiaSharp.NativeAssets.macOS` packages to `~/.skiasharp/hives/pr-3779/packages` and patches the observed `SKPath` lazy-builder finalizer crash before packing.

## Run

```bash
dotnet run --project samples/MeshArena.SkiaSharp.Uno/MeshArena.SkiaSharp.Uno/MeshArena.SkiaSharp.Uno.csproj
```

To use a different local PR package folder:

```bash
dotnet run \
  -p:SkiaSharpPr3779Packages=/path/to/pr-3779/packages \
  -p:SkiaSharpPr3779Version=4.147.0-pr.3779.1 \
  --project samples/MeshArena.SkiaSharp.Uno/MeshArena.SkiaSharp.Uno/MeshArena.SkiaSharp.Uno.csproj
```

## Controls

- `A` / `D` or Left / Right: move.
- `Space`, `W`, or Up: jump.
- `Shift`, `Enter`, or mouse click: dash.
- Mouse click dashes toward the pointer when the pointer is known.
- `S` or Down: slow down on ledges.
- `R`: respawn.

## Gameplay

Luma Grove is a compact platformer loop:

- Collect all light seeds to refill the grove and respawn another collection wave.
- Avoid thorn beds on the ground.
- Dodge shadow wisps that patrol between world anchors.
- Use jump, double-jump, and dash to cross branch platforms.
- Energy regenerates over time and is spent by dashing.
- Damage applies knockback and particle bursts; health loss respawns the player when depleted.

## Mesh Passes

The game renders three mesh passes per frame.

| Pass | Mode | Geometry | Purpose |
| --- | --- | --- | --- |
| Forest background | `SKMeshMode.Triangles` + index buffer | 64 x 34 grid | Full-surface parallax forest, moon, fog, tree silhouettes, fireflies |
| Spirit trail | `SKMeshMode.TriangleStrip` | 72 sample ribbon | Additive glow following the player position history |
| Entity batch | `SKMeshMode.Triangles` + index buffer | Up to 1400 quads | Platforms, foliage, player, orbs, enemies, hazards, particles |

This keeps draw calls low while still exercising vertex attributes, index buffers, varyings, uniforms, and custom SkSL fragment generation.

## Sprite Mesh Attributes

Entity quads use non-uniform size attributes so the same batch can render square particles, tall character silhouettes, wide platforms, and long thorn strips:

```csharp
private static readonly SKMeshSpecificationAttribute[] SpriteAttributes =
{
    new(SKMeshSpecificationAttributeType.Float2, 0, "local"),
    new(SKMeshSpecificationAttributeType.Float2, 8, "center"),
    new(SKMeshSpecificationAttributeType.Float2, 16, "size"),
    new(SKMeshSpecificationAttributeType.Float, 24, "kind"),
    new(SKMeshSpecificationAttributeType.Float, 28, "hue"),
    new(SKMeshSpecificationAttributeType.Float, 32, "alpha"),
    new(SKMeshSpecificationAttributeType.Float, 36, "angle"),
    new(SKMeshSpecificationAttributeType.Float, 40, "energy"),
};
```

The vertex shader rotates the local quad coordinate and scales by the per-entity `size`:

```c
float c = cos(attrs.angle);
float s = sin(attrs.angle);
float2 rotated = float2(
    attrs.local.x * c - attrs.local.y * s,
    attrs.local.x * s + attrs.local.y * c);
v.position = attrs.center + rotated * attrs.size;
```

The fragment shader switches on `kind` to draw different original procedural shapes:

- `0`: mossy branch/stone platform.
- `1`: Luma spirit player silhouette.
- `2`: light seed orb or energy aura.
- `3`: spark/leaf particle.
- `4`: shadow wisp enemy.
- `5`: thorn hazard strip.
- `6`: glowing foliage.

## Game Architecture

The gameplay simulation stays CPU-side because collision and input state need deterministic branching:

- Platform AABB collision resolves horizontal and vertical movement separately.
- Jump buffering and coyote time make keyboard movement responsive.
- Dash direction is either the pointer direction or the current facing/input direction.
- Camera smoothing follows the player in world coordinates.
- Only visible entities are appended to the sprite mesh batch.

Rendering remains GPU-oriented: all visible entities are encoded into one vertex buffer and one index buffer, then submitted with `SKCanvas.DrawMesh`.

## Native Skia References

The sample is based on native Skia mesh usage patterns rather than a SkiaSharp compatibility layer:

- Native `SkMesh` exposes `Make` and `MakeIndexed`, validates with `isValid()`, and invalid meshes fail when passed to `SkCanvas::drawMesh`.
- `SkCanvas::drawMesh` is documented as experimental, requires a GPU backend or SkSL support, and ignores `SkMaskFilter`, `SkPathEffect`, and `SkPaint` antialiasing.
- Skia release notes describe `SkMesh` as custom vertex/index mesh data with custom attributes/varyings using SkSL; mesh data can be created on `GrDirectContext` to avoid re-uploading.
- Skia's `gm/mesh.cpp` tests cover non-indexed meshes, indexed meshes, triangle strips, uniforms, buffer offsets, shaders/blenders, and buffer updates.

Primary source links:

- [SkMesh.h](https://skia.googlesource.com/skia/+/refs/heads/main/include/core/SkMesh.h)
- [SkCanvas.h drawMesh](https://skia.googlesource.com/skia/+/refs/heads/main/include/core/SkCanvas.h)
- [gm/mesh.cpp native mesh tests](https://skia.googlesource.com/skia/+/refs/heads/main/gm/mesh.cpp)
- [Skia release notes](https://skia.googlesource.com/skia/+/refs/heads/main/RELEASE_NOTES.md)
- [SkSL documentation](https://skia.googlesource.com/skia/+/refs/heads/main/site/docs/user/sksl.md)
