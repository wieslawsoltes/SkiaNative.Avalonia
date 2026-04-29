# MeshModeler.SkiaSharp.Uno

Uno Platform desktop 3D modeling sample using SkiaSharp v4 PR 3779 `SKMesh` APIs.

The sample demonstrates a pragmatic way to build a lightweight model editor on top of Skia's new mesh API. `SkMesh` is still a 2D custom mesh draw operation, not a 3D engine, so this sample performs camera projection, hit testing, editing, and depth sorting in C#, then submits projected triangles through `SKMesh.MakeIndexed`.

## Features

- Uno `SKCanvasElement` host.
- Orbit, pan, and zoom camera controls.
- Built-in procedural torus and built-in textured cube OBJ model.
- OBJ file loading for `v`, `vt`, and polygonal `f` records, triangulated with a fan.
- UV texture coordinates rendered by a procedural checker texture in SkSL.
- Depth visualization shader mode.
- Normal visualization shader mode.
- Vertex editing mode: click a projected vertex and drag it in the camera plane.
- CPU back-to-front triangle sorting for painter-style depth rendering.
- Wire overlay generated in the mesh shader from barycentric coordinates.

## Run

Install or bootstrap the SkiaSharp PR 3779 packages first:

```bash
./eng/bootstrap-skiasharp-pr3779.sh
```

Run the sample:

```bash
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
- `E`: toggle vertex edit mode.
- Edit mode left click: select nearest projected vertex.
- Edit mode drag: move selected vertex in the current camera plane.
- `Delete` / `Backspace`: reset selected vertex to its original position.
- `F` / `R`: reset camera to model bounds.
- `1`: UV checker texture shading.
- `2`: depth visualization.
- `3`: normal visualization.

## Why Projection Is CPU-Side

Native `SkMesh` lets callers define custom vertex attributes, varyings, vertex SkSL, fragment SkSL, uniforms, optional child shaders, vertex/index buffers, and bounds. It does not provide a camera abstraction, clipping volume, hierarchical transforms, material system, or hardware depth buffer.

For this reason the sample uses this split:

| Responsibility | Location |
| --- | --- |
| OBJ parsing | C# |
| Orbit/pan/zoom camera math | C# |
| Vertex hit testing/editing | C# |
| 3D-to-2D projection | C# |
| Triangle depth sort | C# |
| UV checker texture | SkSL fragment shader |
| Normal/depth visualization | SkSL fragment shader |
| Wire overlay | SkSL fragment shader using barycentric attributes |
| Final triangle rasterization | `SKCanvas.DrawMesh` |

This keeps the sample honest about what `SkMesh` currently is: a powerful custom 2D mesh primitive that can host projected 3D data efficiently, but not a replacement for a full 3D graphics API.

## SKMesh API Usage

The mesh specification uses six attributes and five varyings:

```csharp
private static readonly SKMeshSpecificationAttribute[] Attributes =
{
    new(SKMeshSpecificationAttributeType.Float2, 0, "position"),
    new(SKMeshSpecificationAttributeType.Float2, 8, "uv"),
    new(SKMeshSpecificationAttributeType.Float3, 16, "normal"),
    new(SKMeshSpecificationAttributeType.Float, 28, "depth"),
    new(SKMeshSpecificationAttributeType.Float, 32, "selected"),
    new(SKMeshSpecificationAttributeType.Float3, 36, "bary"),
};
```

The vertex shader forwards projected screen position and varyings:

```c
Varyings main(const Attributes attrs) {
    Varyings v;
    v.position = attrs.position;
    v.uv = attrs.uv;
    v.normal = normalize(attrs.normal);
    v.depth = attrs.depth;
    v.selected = attrs.selected;
    v.bary = attrs.bary;
    return v;
}
```

Each frame builds transient vertex and index buffers from the projected, sorted triangles:

```csharp
using var vertexBuffer = SKMeshVertexBuffer.Make(
    MemoryMarshal.AsBytes(_submittedVertices.AsSpan(0, _submittedVertexCount)));
using var indexBuffer = SKMeshIndexBuffer.Make(
    MemoryMarshal.AsBytes(_submittedIndices.AsSpan(0, _submittedIndexCount)));
using var mesh = SKMesh.MakeIndexed(
    _specification,
    SKMeshMode.Triangles,
    vertexBuffer,
    _submittedVertexCount,
    0,
    indexBuffer,
    _submittedIndexCount,
    0,
    uniforms,
    bounds,
    out var errors);
canvas.DrawMesh(mesh, _meshPaint);
```

The current PR 3779 binding also exposes overloads that accept `ReadOnlySpan<SKRuntimeEffectChild>`, matching the native `SkMesh` child shader model. This sample keeps texture mapping procedural so it has no external asset dependency, but the next natural step is to pass an `SKShader` child for loaded bitmap textures and sample it from the mesh fragment program.

## OBJ Support

The parser intentionally supports the subset needed for a focused modeling sample:

- `v x y z`
- `vt u v`
- `f v`, `f v/vt`, `f v/vt/vn`, and negative OBJ indices
- polygons with more than three corners, triangulated as a fan

Normals are recomputed after load and after vertex edits. If UVs are missing, deterministic fallback UVs are generated so the shader path still renders.

## Depth Rendering

Skia does not depth-test `SkMesh` triangles for this path. The sample therefore sorts triangles by average camera-space depth, far to near, before writing the index buffer. This is correct enough for simple opaque model editing and depth visualization, but it is not a substitute for a real z-buffer when triangles interpenetrate.

## Native Skia References

Primary source material used for this sample:

- [SkMesh.h](https://skia.googlesource.com/skia/+/refs/heads/main/include/core/SkMesh.h)
- [gm/mesh.cpp native mesh tests](https://skia.googlesource.com/skia/+/refs/heads/main/gm/mesh.cpp)
- [SkCanvas reference](https://skia.googlesource.com/skia/+/1321a3d/site/user/api/SkCanvas_Reference.md)
