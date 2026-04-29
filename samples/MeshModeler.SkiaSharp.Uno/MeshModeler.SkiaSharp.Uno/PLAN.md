# Mesh Modeler Implementation Plan

## Goal

Build a standalone Uno Platform sample that uses SkiaSharp v4 PR 3779 `SKMesh` as the rasterization layer for a lightweight 3D modeling/editor workflow.

## Current Implementation Status

- [x] Create a standalone Uno sample project using `Uno.Sdk`, `SkiaRenderer`, and SkiaSharp PR 3779 packages.
- [x] Host rendering in `SKCanvasElement`.
- [x] Build a mesh specification with custom attributes, varyings, vertex shader, fragment shader, uniforms, and indexed draw calls.
- [x] Implement built-in torus mesh generation.
- [x] Implement built-in cube OBJ loading.
- [x] Implement OBJ parser for positions, UVs, normals, material libraries, material ranges, polygon faces, and negative indices.
- [x] Implement Gaussian splat PLY parser for common 3DGS exports, including ASCII and binary little-endian files.
- [x] Decode 3DGS `f_dc_0/1/2`, `opacity`, `scale_0/1/2`, and `rot_0..3` into color, alpha, and anisotropic covariance axes.
- [x] Convert PLY and SOG splats from common y-down 3DGS camera space to the viewer's y-up world by reflecting positions and covariance axes on Y.
- [x] Stream binary PLY records with byte-offset decoding and pooled buffers instead of decoding every property into temporary objects.
- [x] Implement PlayCanvas SOG v2 loading from `.sog` zip bundles, SOG directories, or unbundled `meta.json`.
- [x] Decode SOG `means`, `scales`, `quats`, and `sh0` image/codebook data into the same native splat representation as PLY, including original-order smallest-three quaternions and log-scale codebooks emitted by `splat-transform`.
- [x] Add optional bounded Gaussian splat import LOD via `MESHMODELER_MAX_SPLATS`; default import keeps all readable source splats.
- [x] Submit all visible Gaussian splats as far-to-near `SKMesh` batches and cache stable-camera splat batches.
- [x] Add a procedural Gaussian splat cloud sample and `MESHMODELER_PLY` / `MESHMODELER_SOG` / `MESHMODELER_SPLAT` startup hooks.
- [x] Replace fixed camera zoom clamps with radius-scaled near/far zoom bounds for detailed inspection and wide context views.
- [x] Ignore common Blender showcase scene-helper materials so large OBJ exports such as Bugatti load focused on the actual model instead of lights/backdrop planes.
- [x] Implement MTL parser support for `Ka`, `Kd`, `Ks`, `Ke`, `Ns`, `d`, `Tr`, and `map_Kd`.
- [x] Implement orbit, pan, and zoom controls.
- [x] Implement vertex selection and camera-plane vertex editing.
- [x] Add explicit vertex handle show/hide toggle while keeping edit-mode picking available.
- [x] Add optional mesh grid overlay, disabled by default for material rendering performance.
- [x] Implement material/texture shader mode with OBJ diffuse textures sampled through Skia mesh child shaders.
- [x] Implement depth visualization shader mode.
- [x] Implement normal visualization shader mode.
- [x] Move projection into SKMesh vertex SkSL using camera uniforms.
- [x] Cache projected vertex/index buffers for static-camera frames while rebuilding them when camera, viewport, or model data changes.
- [x] Use depth-sorted projected SKMesh path for small/medium material models, transparent models, depth mode, and normal mode so models do not self-overlap in source/material order.
- [x] Add an adaptive large-model opaque material fast path so million-triangle OBJ files use cached world-space SKMesh batches instead of sorting every triangle on the CPU.
- [x] Feed OBJ material colors and authored normals into the mesh shader.
- [x] Feed OBJ diffuse texture maps into the mesh shader with `SKRuntimeEffectChild`.
- [x] Split large OBJ models into multiple 65k-safe `SKMesh` submissions.
- [x] Split cached geometry by material so every mesh batch receives the correct child shader and material uniforms.
- [x] Render Gaussian splats as depth-sorted anisotropic `SKMesh` quad batches with Gaussian SkSL alpha falloff.
- [x] Add `MESHMODELER_OBJ` startup load hook for large-model smoke/regression runs.
- [x] Document Skia/SkiaSharp mesh API usage and limitations.

## Design Constraints

`SkMesh` is a custom mesh primitive. It accepts vertex/index buffers and runs SkSL, but it does not own a 3D scene, depth buffer, model hierarchy, material system, or Gaussian splat tile renderer. The sample must therefore own model data, camera math, OBJ/PLY parsing, splat covariance projection, editing, material batching, hit testing, and draw ordering. Mesh triangles use an adaptive draw path: projected, far-to-near sorted `SKMesh` batches for correctness-sensitive views, and cached world-space `SKMesh` batches for very large opaque material views. Gaussian splats are represented as sorted screen-space `SKMesh` quads whose fragment shader evaluates the Gaussian density.

## Architecture

1. Model layer
   - `MeshDocument` stores positions, original positions, authored/computed normals, material-indexed triangles, material definitions, bounds, and radius.
   - `MeshMaterial` owns diffuse color, secondary material terms, alpha/shininess, optional encoded diffuse texture, and the shader child used by `SKMesh.MakeIndexed`.
   - `GaussianSplatCloud` stores normalized splat positions, covariance axes, colors, alpha, source format, and bounds.
   - OBJ import normalizes loaded models to a predictable unit scale.
   - OBJ import skips common Blender scene-helper materials (`Studio_Lights`, `sun`, `back_drop`) and computes bounds from referenced triangle vertices.
   - PLY import supports common 3DGS fields, RGB/alpha/direct-scale fallbacks, optional bounded import LOD, and normalizes captures to a predictable unit radius.
   - SOG import supports PlayCanvas SOG v2 `meta.json`, zipped `.sog` bundles, unbundled image directories, position unlog decode, linear or log scale codebooks, color codebooks, smallest-three quaternions, and `sh0` alpha.
   - Vertex edits mutate model-space positions and recompute normals.

2. Camera layer
   - Orbit camera is represented by yaw, pitch, distance, and target.
   - Camera basis is rebuilt every frame as position/forward/right/up.
   - Camera basis is sent to SKMesh as uniforms.

3. Editing layer
   - Vertex hit testing projects all positions and picks the closest screen-space vertex under a threshold.
   - Dragging a selected vertex maps pointer deltas into camera right/up world-space deltas.

4. Render layer
   - Mesh triangles can be projected for the current camera and sorted by average camera depth before `SKMesh` submission.
   - Projected triangle batches are cached and reused while the camera, viewport, and model remain unchanged.
   - Material mode uses projected sorting for small/medium and transparent models, then falls back to cached world-space batches for very large opaque models where CPU sorting would dominate interaction.
   - Each triangle is submitted as three duplicated vertices so barycentric coordinates can draw optional mesh-grid lines.
   - Material color is carried as a mesh attribute/varying and combined with texture sampling, UV/grid lighting, and Lambert lighting in SkSL.
   - `uniform shader diffuseTexture` is bound through `ReadOnlySpan<SKRuntimeEffectChild>` only for textured draw batches.
   - `SKMesh.MakeIndexed` submits one or more material/texture-aware 65k-safe indexed triangle mesh batches.
   - Gaussian splat rendering projects 3D covariance axes into screen-space covariance, derives ellipse axes, emits one quad per visible splat, and uses SkSL to evaluate premultiplied Gaussian density.
   - Gaussian splat rendering projects every loaded splat, submits all visible splats, sorts them far-to-near, and caches resulting `SKMesh` batches until the camera or viewport changes.
   - Gaussian splat batches are split at the same 65k vertex/index limits as mesh batches.

## Remaining Work

- Add transform gizmos for axis-constrained vertex movement.
- Add face and edge selection modes.
- Add OBJ export for edited meshes.
- Add clipping against the near plane instead of whole-triangle culling in the depth-sorted path and vertex-shader near-plane clamping in the cached path.
- Add optional software z-buffer path for exact depth with interpenetrating triangles if this sample needs correctness beyond painter sorting.
- Add optional tile/bin acceleration for projected mesh sorting if large animated models need lower camera-motion rebuild cost.
- Add tile/bin accelerated Gaussian splat culling and approximate per-tile ordering if very large captures need better camera-motion rebuild cost.
- Add spherical harmonics levels above DC color for production-quality Gaussian splat lighting.
