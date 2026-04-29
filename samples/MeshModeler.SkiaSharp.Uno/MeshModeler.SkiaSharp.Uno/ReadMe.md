# MeshModeler.SkiaSharp.Uno

Uno Platform desktop 3D modeling sample using SkiaSharp v4 PR 3779 `SKMesh` APIs.

The sample demonstrates a pragmatic way to build a lightweight model editor on top of Skia's new mesh API. `SkMesh` is still a 2D custom mesh draw operation, not a 3D engine, so this sample owns OBJ parsing, Gaussian splat parsing, camera math, hit testing, editing, material batching, and texture binding. Mesh models use an adaptive path: small and medium models are projected and far-to-near sorted in C# before being cached into 65k-safe projected `SKMesh` batches, while very large opaque material views use cached world-space `SKMesh` batches to stay interactive. Gaussian splats use the same camera-space projection model for covariance expansion.

## Features

- Uno `SKCanvasElement` host.
- Orbit, pan, and zoom camera controls.
- Built-in procedural torus, built-in cube OBJ model, and built-in procedural Gaussian splat cloud.
- OBJ file loading for `v`, `vt`, `vn`, `mtllib`, `usemtl`, and polygonal `f` records, triangulated with a fan.
- Gaussian splat PLY loading for common 3D Gaussian Splatting exports with `x/y/z`, `f_dc_0/1/2`, `opacity`, `scale_0/1/2`, and `rot_0..3` properties.
- PlayCanvas SOG v2 loading from `.sog` zip bundles, unbundled `meta.json`, or SOG directories.
- Streaming binary little-endian PLY parsing that extracts only required splat fields, supports optional import LOD, and avoids loading multi-gigabyte PLY files into memory.
- ASCII PLY parsing fallback, with RGB/alpha/direct-scale fallbacks for non-standard exporters.
- Gaussian splats are rendered as camera-projected anisotropic `SKMesh` quads with a Gaussian SkSL fragment shader, all-visible splat submission, far-to-near ordering, and cached static-camera batches.
- Blender scene-helper materials such as `Studio_Lights`, `sun`, and `back_drop` are ignored so exported showcase scenes focus on model geometry.
- Material-aware UV shading using OBJ material colors, `map_Kd` image textures, and Skia mesh child shaders.
- MTL loading for diffuse color, ambient/specular/emissive color, alpha, shininess, and diffuse texture maps.
- Depth visualization shader mode.
- Normal visualization shader mode.
- Vertex editing mode: click a projected vertex and drag it in the camera plane.
- Vertex handle overlay is hidden by default and can be toggled when needed.
- Mesh grid overlay is hidden by default and can be toggled for topology inspection.
- Cached projected `SKMeshVertexBuffer` / `SKMeshIndexBuffer` batches; static camera frames reuse depth-sorted projected buffers while camera moves rebuild the sort.
- Adaptive large-model material path: opaque OBJ models above the projected-sort budget use cached world-space `SKMesh` batches instead of sorting every triangle on the CPU.
- Depth-sorted projected `SKMesh` path for depth/normal modes, small material-mode models, and transparent models where painter ordering matters more than buffer reuse.
- Mesh grid overlay generated in the mesh shader from barycentric coordinates.
- Large OBJ models are split into multiple 65k-safe `SKMesh` batches by material and index limits.

## Run

Install or bootstrap the SkiaSharp PR 3779 packages first:

```bash
./eng/bootstrap-skiasharp-pr3779.sh
```

Run the sample:

```bash
dotnet run --project samples/MeshModeler.SkiaSharp.Uno/MeshModeler.SkiaSharp.Uno/MeshModeler.SkiaSharp.Uno.csproj
```

Load an OBJ at startup without using the file picker:

```bash
MESHMODELER_OBJ="/path/to/model.obj" \
dotnet run --project samples/MeshModeler.SkiaSharp.Uno/MeshModeler.SkiaSharp.Uno/MeshModeler.SkiaSharp.Uno.csproj
```

Load a Gaussian splat PLY or SOG at startup:

```bash
MESHMODELER_SPLAT="/path/to/scene.ply" \
dotnet run --project samples/MeshModeler.SkiaSharp.Uno/MeshModeler.SkiaSharp.Uno/MeshModeler.SkiaSharp.Uno.csproj
```

`MESHMODELER_PLY` and `MESHMODELER_SOG` are accepted as format-specific aliases. SOG inputs can be a `.sog` zip file, a `meta.json` file, or a directory containing `meta.json` and the referenced images.

By default, Gaussian splat captures import all readable splats. Set `MESHMODELER_MAX_SPLATS` when you explicitly want a bounded representative import LOD for very large captures:

```bash
MESHMODELER_MAX_SPLATS=1500000 \
MESHMODELER_SPLAT="/path/to/scene.sog" \
dotnet run --project samples/MeshModeler.SkiaSharp.Uno/MeshModeler.SkiaSharp.Uno/MeshModeler.SkiaSharp.Uno.csproj
```

If the local package source is elsewhere:

```bash
dotnet run \
  -p:SkiaSharpPr3779Packages=/path/to/pr-3779/packages \
  -p:SkiaSharpPr3779Version=4.147.0-pr.3779.1 \
  --project samples/MeshModeler.SkiaSharp.Uno/MeshModeler.SkiaSharp.Uno/MeshModeler.SkiaSharp.Uno.csproj
```

## Controls

- Left drag: orbit the camera.
- Right or middle drag: pan the camera target.
- Mouse wheel: zoom.
- `Load Splats`: load a Gaussian splat cloud from a local `.ply`, `.sog`, or SOG `meta.json` file.
- `Splats`: load the built-in procedural Gaussian splat cloud.
- `E`: toggle vertex edit mode. Editing is disabled for Gaussian splat clouds because splats are oriented density kernels, not mesh vertices.
- `H`: toggle white vertex handles.
- `G`: toggle mesh grid overlay.
- `W`: toggle mesh grid overlay alias.
- Edit mode left click: select nearest projected vertex.
- Edit mode drag: move selected vertex in the current camera plane.
- `Delete` / `Backspace`: reset selected vertex to its original position.
- `F` / `R`: reset camera to model bounds.
- `1`: material + texture shader + UV checker shading.
- `2`: depth visualization.
- `3`: normal visualization.

## Projection And Rendering Split

Native `SkMesh` lets callers define custom vertex attributes, varyings, vertex SkSL, fragment SkSL, uniforms, optional child shaders, vertex/index buffers, and bounds. It does not provide a camera abstraction, clipping volume, hierarchical transforms, material system, or hardware depth buffer.

For this reason the sample uses this split:

| Responsibility | Location |
| --- | --- |
| OBJ parsing | C# |
| Orbit/pan/zoom camera math | C# |
| Vertex hit testing/editing | C# |
| 3D-to-2D projection | C# projected stream for depth-sorted mesh and splat views; vertex SkSL for large opaque material views |
| Material batching | C# |
| OBJ/MTL diffuse texture loading | C# |
| UV texture sampling | `uniform shader` child sampled by SkSL fragment shader |
| UV checker overlay | SkSL fragment shader |
| Normal/depth visualization | SkSL fragment shader |
| Mesh grid overlay | SkSL fragment shader using barycentric attributes |
| Gaussian splat projection and ellipse construction | C# camera-space covariance projection |
| Gaussian splat density falloff | SkSL fragment shader |
| Final triangle rasterization | `SKCanvas.DrawMesh` |

This keeps the sample honest about what `SkMesh` currently is: a powerful custom mesh primitive with programmable vertex and fragment stages, but not a replacement for a full 3D graphics API. There is no hardware depth buffer in this path, so this sample prioritizes fast material/model inspection over exact interpenetrating-triangle depth.

## SKMesh API Usage

The mesh specification uses six attributes and five varyings. The sample deliberately keeps the varying count at five because the current PR 3779 artifact rejects a sixth varying on macOS:

```csharp
private static readonly SKMeshSpecificationAttribute[] Attributes =
{
    new(SKMeshSpecificationAttributeType.Float3, 0, "position"),
    new(SKMeshSpecificationAttributeType.Float2, 12, "uv"),
    new(SKMeshSpecificationAttributeType.Float3, 20, "normal"),
    new(SKMeshSpecificationAttributeType.Float3, 32, "material"),
    new(SKMeshSpecificationAttributeType.Float3, 44, "bary"),
};
```

The vertex shader projects model-space vertices using camera uniforms and forwards material/normal varyings:

```c
Varyings main(const Attributes attrs) {
    Varyings v;
    float3 rel = attrs.position - u_camera0.xyz;
    float vx = dot(rel, normalize(u_camera1.xyz));
    float vy = dot(rel, normalize(u_camera2.xyz));
    float vz = max(dot(rel, normalize(u_camera3.xyz)), u_light.w);
    v.position = float2(u_view.y * 0.5 + vx / vz * u_camera0.w,
                        u_camera1.w * 0.5 - vy / vz * u_camera0.w);
    v.uv = attrs.uv;
    v.depth = vz;
    v.material = attrs.material;
    v.bary = attrs.bary;
    return v;
}
```

The renderer has two mesh submission paths. The correctness-biased path builds 65k-safe projected batches for the current camera. This is required because Skia's mesh API does not expose a hardware depth buffer; drawing 3D models in OBJ/material source order can produce incorrect self-overlap. The renderer projects visible triangles, sorts them far-to-near, and caches the resulting projected buffers until the camera, viewport, or model changes. Each triangle is duplicated into three submitted vertices so the fragment shader receives barycentric coordinates for optional mesh-grid rendering:

```csharp
foreach (var triangle in document.Triangles)
{
    if (batchVertexCount + 3 > MaxSubmittedVertices ||
        batchIndexCount + 3 > MaxSubmittedIndices)
    {
        AddMeshBatch(batchVertexCount, batchIndexCount, materialIndex);
        batchVertexCount = 0;
        batchIndexCount = 0;
    }

    AppendTriangleToBatch(triangle, ref batchVertexCount, ref batchIndexCount);
}

AddMeshBatch(batchVertexCount, batchIndexCount, materialIndex);
```

The current PR 3779 binding exposes overloads that accept `ReadOnlySpan<SKRuntimeEffectChild>`, matching the native `SkMesh` child shader model. The sample uses this path for OBJ diffuse textures:

```c
uniform float4 u_texture;      // width, height, hasTexture, alpha
uniform shader diffuseTexture; // SKRuntimeEffectChild created from SKImage.ToShader(...)

half4 tex = diffuseTexture.eval(float2(v.uv.x * u_texture.x, v.uv.y * u_texture.y));
float texWeight = u_texture.z * float(tex.a);
float3 base = mix(v.material, v.material * float3(tex.r, tex.g, tex.b), texWeight);
```

The renderer uses two specifications for performance. Untextured materials use a color-only fragment shader and the `SKMesh.MakeIndexed` overload without child shaders. Textured materials use the child-shader overload with `SKImage.FromEncodedData(...).ToShader(...)`.

## Gaussian Splatting

The sample can load and render 3D Gaussian Splatting PLY and PlayCanvas SOG files through the same `SKMesh` API. This is implemented as a Skia mesh technique rather than a CUDA/Metal 3DGS renderer:

1. The PLY loader reads each splat position, color, opacity, log-scale, and quaternion rotation.
2. Binary PLY uses record-stride offsets and `BinaryPrimitives` over pooled 4 MiB buffers, so unused high-order SH properties such as `f_rest_*` are skipped instead of decoded.
3. The SOG loader reads PlayCanvas SOG v2 `meta.json`, decodes lossless `means`, `scales`, `quats`, and `sh0` images through Skia, and supports both zipped and unbundled layouts.
4. When `MESHMODELER_MAX_SPLATS` is set, very large files are sampled at import time with a deterministic stride cap.
5. SH DC color coefficients are converted with `rgb = clamp(0.5 + 0.28209479 * f_dc, 0, 1)`.
6. PLY opacity is converted with sigmoid, matching common 3DGS training output; SOG uses the `sh0` alpha channel directly.
7. Log scales are exponentiated for PLY. SOG scale codebooks are treated as linear only when all entries are positive; `splat-transform` SOG files with non-positive scale codebook entries are detected as log-scale codebooks and exponentiated.
8. Quaternion rotations are expanded into covariance basis axes.
9. The current camera projects each 3D covariance axis into screen-space covariance.
10. The renderer eigen-decomposes the 2D covariance and emits one oriented quad per visible splat.
11. A SkSL fragment shader evaluates Gaussian falloff from local quad coordinates and outputs premultiplied alpha.
12. All visible splats are sorted far-to-near and cached as 65k-safe indexed `SKMesh` submissions.

Supported standard 3DGS PLY properties:

- `x`, `y`, `z`
- `f_dc_0`, `f_dc_1`, `f_dc_2`
- `opacity`
- `scale_0`, `scale_1`, `scale_2`
- `rot_0`, `rot_1`, `rot_2`, `rot_3`

Fallback properties:

- `red/green/blue` or `r/g/b`
- `alpha`
- `scale_x/scale_y/scale_z` or `sx/sy/sz`
- `qw/qx/qy/qz`

Supported PlayCanvas SOG v2 data:

- `meta.json` version `2`
- `means.files`: low/high 8-bit RGBA images for quantized unlog position
- `means.mins` / `means.maxs`: position decode bounds
- `scales.files` and `scales.codebook`
- `quats.files`: smallest-three quaternion encoding with alpha values `252..255`
- `sh0.files` and `sh0.codebook`: DC color plus alpha

The renderer currently ignores `shN` higher-order spherical harmonics because the sample splat shader only uses DC color. SOG WebP images are decoded into disposable Skia bitmaps during import instead of copied into retained managed arrays, which keeps the final retained heap closer to the PLY path.

This path is useful for validating whether `SKMesh` can host Gaussian-style rasterization and blending. It is not a full production 3DGS renderer: there is no tile binning, no hardware z-buffer, no per-tile depth-sort acceleration, no spherical harmonics beyond DC color, and no GPU compute culling. Large captures therefore submit all visible splats and can become CPU/GPU bound. `MESHMODELER_MAX_SPLATS` remains available as an explicit import-memory limiter.

## OBJ Support

The parser intentionally supports the subset needed for a focused modeling sample:

- `v x y z`
- `vt u v`
- `vn x y z`
- `mtllib file.mtl` with material files resolved relative to the OBJ
- `usemtl name` material ranges with deterministic fallback colors for missing MTL files
- MTL `Ka`, `Kd`, `Ks`, `Ke`, `Ns`, `d`, `Tr`, and `map_Kd`
- `f v`, `f v/vt`, `f v//vn`, `f v/vt/vn`, and negative OBJ indices
- polygons with more than three corners, triangulated as a fan
- obvious Blender showcase helper faces using `Studio_Lights`, `sun`, or `back_drop` materials are skipped

Authored OBJ normals are used when present. Normals are recomputed after vertex edits because edited geometry invalidates imported normals. If UVs are missing, deterministic fallback UVs are generated so the shader path still renders.

Bounds and camera normalization are computed from referenced triangle vertices instead of every `v` record. This matters for Blender exports that include curves, lights, backdrop planes, or other helper objects in the same OBJ file.

Diffuse textures are loaded through `SKImage.FromEncodedData` instead of `SKBitmap.Decode`, because the PR 3779 macOS artifact successfully exposes encoded image decoding through `SKImage` and that object maps naturally to GPU-backed shader creation.

Large OBJ files are supported by chunking the world-space triangle stream into multiple indexed `SKMesh` submissions. The per-batch vertex/index count stays below the 16-bit index boundary, while the model document can contain far more source vertices and triangles. Very large opaque material-mode models use this world-space path by default because sorting more than a million projected triangles per camera change is not interactive in a UI sample.

## Depth Rendering

Skia does not depth-test `SkMesh` triangles for this path. The sample therefore uses two strategies:

- Material mode uses a CPU-projected triangle stream sorted far-to-near for small and medium opaque models and for transparent models. This avoids the source-order artifacts that appear on car interiors/exteriors when no real z-buffer is available.
- Very large opaque material-mode models use cached world-space batches instead of projected sorting. This is an explicit responsiveness tradeoff for files such as high-poly Blender exports with more than a million triangles.
- Depth and normal views use the projected sorted path because their visual purpose is depth/normal inspection rather than maximum large-model throughput.
- Static-camera frames reuse cached projected vertex/index buffers. Orbit, pan, zoom, viewport resize, and vertex edits invalidate and rebuild that cache.

The depth-sorted path is still not a true z-buffer. It sorts whole triangles by average camera depth, so intersecting triangles and highly concave transparent geometry can still show painter-order artifacts. Exact per-pixel depth would require a real 3D API path or a separate software depth buffer.

## Native Skia References

Primary source material used for this sample:

- [SkMesh.h](https://skia.googlesource.com/skia/+/refs/heads/main/include/core/SkMesh.h)
- [gm/mesh.cpp native mesh tests](https://skia.googlesource.com/skia/+/refs/heads/main/gm/mesh.cpp)
- [SkCanvas reference](https://skia.googlesource.com/skia/+/1321a3d/site/user/api/SkCanvas_Reference.md)
- [3D Gaussian Splatting for Real-Time Radiance Field Rendering](https://repo-sam.inria.fr/fungraph/3d-gaussian-splatting/)
- [StopThePop: Sorted Gaussian Splatting for View-Consistent Real-time Rendering](https://r4dl.github.io/StopThePop/)
- [PlayCanvas SOG format](https://developer.playcanvas.com/user-manual/gaussian-splatting/formats/sog/)
