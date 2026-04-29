# Mesh Modeler Implementation Plan

## Goal

Build a standalone Uno Platform sample that uses SkiaSharp v4 PR 3779 `SKMesh` as the rasterization layer for a lightweight 3D modeling/editor workflow.

## Current Implementation Status

- [x] Create a standalone Uno sample project using `Uno.Sdk`, `SkiaRenderer`, and SkiaSharp PR 3779 packages.
- [x] Host rendering in `SKCanvasElement`.
- [x] Build a mesh specification with custom attributes, varyings, vertex shader, fragment shader, uniforms, and indexed draw calls.
- [x] Implement built-in torus mesh generation.
- [x] Implement built-in cube OBJ loading.
- [x] Implement OBJ parser for positions, UVs, polygon faces, and negative indices.
- [x] Implement orbit, pan, and zoom controls.
- [x] Implement vertex selection and camera-plane vertex editing.
- [x] Implement UV checker texture shader mode.
- [x] Implement depth visualization shader mode.
- [x] Implement normal visualization shader mode.
- [x] Implement CPU triangle depth sorting for painter-style depth rendering.
- [x] Document Skia/SkiaSharp mesh API usage and limitations.

## Design Constraints

`SkMesh` is a custom 2D mesh primitive. It accepts vertex/index buffers and runs SkSL, but it does not own a 3D scene, depth buffer, model/view/projection pipeline, or material system. The sample must therefore own model data, camera math, OBJ parsing, editing, projection, hit testing, and triangle ordering.

## Architecture

1. Model layer
   - `MeshDocument` stores positions, original positions, recomputed normals, triangles, bounds, and radius.
   - OBJ import normalizes loaded models to a predictable unit scale.
   - Vertex edits mutate model-space positions and recompute normals.

2. Camera layer
   - Orbit camera is represented by yaw, pitch, distance, and target.
   - Camera basis is rebuilt every frame as position/forward/right/up.
   - Projection is perspective projection into screen coordinates.

3. Editing layer
   - Vertex hit testing projects all positions and picks the closest screen-space vertex under a threshold.
   - Dragging a selected vertex maps pointer deltas into camera right/up world-space deltas.

4. Render layer
   - Model triangles are projected into `ProjectedVertex` values.
   - Triangles behind the near plane or degenerate after projection are skipped.
   - Visible triangles are sorted by average camera-space depth.
   - Each triangle is submitted as three duplicated vertices so barycentric coordinates can draw wire lines.
   - `SKMesh.MakeIndexed` submits one indexed triangle mesh.

## Remaining Work

- Add bitmap texture loading and pass loaded texture as an `SKRuntimeEffectChild` shader.
- Add transform gizmos for axis-constrained vertex movement.
- Add face and edge selection modes.
- Add OBJ export for edited meshes.
- Add material groups from OBJ/MTL.
- Split large models into multiple mesh submissions to exceed the current 16-bit index limit safely.
- Add clipping against the near plane instead of dropping near-plane-crossing triangles.
- Add optional software z-buffer path for exact depth with interpenetrating triangles.
