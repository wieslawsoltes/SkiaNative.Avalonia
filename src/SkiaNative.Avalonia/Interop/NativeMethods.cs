using System.Runtime.CompilerServices;
using System.Runtime.InteropServices;

namespace SkiaNative.Avalonia;

internal static unsafe partial class NativeMethods
{
    internal const string LibraryName = "SkiaNativeAvalonia";

    [LibraryImport(LibraryName, EntryPoint = "skn_context_create_metal")]
    internal static partial NativeContextHandle ContextCreateMetal(nint device, nint queue, ulong maxResourceBytes, int diagnosticsEnabled, int gpuSubmitMode);

    [LibraryImport(LibraryName, EntryPoint = "skn_context_create_cpu")]
    internal static partial NativeContextHandle ContextCreateCpu(ulong maxResourceBytes, int diagnosticsEnabled, int gpuSubmitMode);

    [LibraryImport(LibraryName, EntryPoint = "skn_context_purge_unlocked_resources")]
    internal static partial void ContextPurgeUnlockedResources(NativeContextHandle context);

    [LibraryImport(LibraryName, EntryPoint = "skn_context_get_resource_cache_usage")]
    [SuppressGCTransition]
    internal static partial int ContextGetResourceCacheUsage(
        NativeContextHandle context,
        out int resourceCount,
        out ulong resourceBytes,
        out ulong purgeableBytes,
        out ulong resourceLimit);

    [LibraryImport(LibraryName, EntryPoint = "skn_context_destroy")]
    [SuppressGCTransition]
    internal static partial void ContextDestroy(nint context);

    [LibraryImport(LibraryName, EntryPoint = "skn_session_begin_metal")]
    internal static partial NativeSessionHandle SessionBeginMetal(NativeContextHandle context, nint texture, int width, int height, double scale, int isYFlipped);

    [LibraryImport(LibraryName, EntryPoint = "skn_session_begin_raster")]
    internal static partial NativeSessionHandle SessionBeginRaster(NativeContextHandle context, int width, int height, double dpiX, double dpiY);

    [LibraryImport(LibraryName, EntryPoint = "skn_session_begin_bitmap")]
    internal static partial NativeSessionHandle SessionBeginBitmap(NativeContextHandle context, NativeBitmapHandle bitmap, double dpiX, double dpiY);

    [LibraryImport(LibraryName, EntryPoint = "skn_session_end")]
    [SuppressGCTransition]
    internal static partial void SessionEnd(nint session);

    [LibraryImport(LibraryName, EntryPoint = "skn_session_flush_commands")]
    internal static partial int SessionFlushCommands(NativeSessionHandle session, NativeCommand* commands, int commandCount);

    [LibraryImport(LibraryName, EntryPoint = "skn_session_draw_path_strokes")]
    internal static partial int SessionDrawPathStrokes(NativeSessionHandle session, NativePathStrokeCommand* commands, int commandCount);

    [LibraryImport(LibraryName, EntryPoint = "skn_session_draw_path_stream")]
    internal static partial int SessionDrawPathStream(NativeSessionHandle session, SkiaNativePathStreamElement* elements, int elementCount, NativeStrokeHandle stroke, float strokeWidthScale, uint flags);

    [LibraryImport(LibraryName, EntryPoint = "skn_session_draw_path_stream_mesh")]
    internal static partial int SessionDrawPathStreamMesh(NativeSessionHandle session, NativePathStreamMeshHandle mesh);

    [LibraryImport(LibraryName, EntryPoint = "skn_session_draw_mesh")]
    internal static partial int SessionDrawMesh(NativeSessionHandle session, NativeMeshHandle mesh, nint shader, NativeColor color, uint flags);

    [LibraryImport(LibraryName, EntryPoint = "skn_session_draw_path_fills")]
    internal static partial int SessionDrawPathFills(NativeSessionHandle session, NativePathFillCommand* commands, int commandCount);

    [LibraryImport(LibraryName, EntryPoint = "skn_session_draw_glyph_runs")]
    internal static partial int SessionDrawGlyphRuns(NativeSessionHandle session, NativeGlyphRunCommand* commands, int commandCount);

    [LibraryImport(LibraryName, EntryPoint = "skn_session_draw_bitmaps")]
    internal static partial int SessionDrawBitmaps(NativeSessionHandle session, NativeBitmapCommand* commands, int commandCount);

    [LibraryImport(LibraryName, EntryPoint = "skn_bitmap_create_raster")]
    internal static partial NativeBitmapHandle BitmapCreateRaster(int width, int height, double dpiX, double dpiY);

    [LibraryImport(LibraryName, EntryPoint = "skn_bitmap_create_from_encoded")]
    internal static partial NativeBitmapHandle BitmapCreateFromEncoded(byte* data, int length, double dpiX, double dpiY);

    [LibraryImport(LibraryName, EntryPoint = "skn_bitmap_upload_pixels")]
    internal static partial int BitmapUploadPixels(NativeBitmapHandle bitmap, byte* pixels, int rowBytes, NativePixelFormat pixelFormat, NativeAlphaFormat alphaFormat);

    [LibraryImport(LibraryName, EntryPoint = "skn_bitmap_read_pixels")]
    internal static partial int BitmapReadPixels(NativeBitmapHandle bitmap, byte* pixels, int rowBytes, NativePixelFormat pixelFormat, NativeAlphaFormat alphaFormat);

    [LibraryImport(LibraryName, EntryPoint = "skn_bitmap_encode")]
    internal static partial NativeDataHandle BitmapEncode(NativeBitmapHandle bitmap, NativeEncodedImageFormat format, int quality);

    [LibraryImport(LibraryName, EntryPoint = "skn_bitmap_get_width")]
    [SuppressGCTransition]
    internal static partial int BitmapGetWidth(NativeBitmapHandle bitmap);

    [LibraryImport(LibraryName, EntryPoint = "skn_bitmap_get_height")]
    [SuppressGCTransition]
    internal static partial int BitmapGetHeight(NativeBitmapHandle bitmap);

    [LibraryImport(LibraryName, EntryPoint = "skn_bitmap_destroy")]
    [SuppressGCTransition]
    internal static partial void BitmapDestroy(nint bitmap);

    [LibraryImport(LibraryName, EntryPoint = "skn_data_get_bytes")]
    [SuppressGCTransition]
    internal static partial byte* DataGetBytes(NativeDataHandle data);

    [LibraryImport(LibraryName, EntryPoint = "skn_data_get_size")]
    [SuppressGCTransition]
    internal static partial nuint DataGetSize(NativeDataHandle data);

    [LibraryImport(LibraryName, EntryPoint = "skn_data_destroy")]
    [SuppressGCTransition]
    internal static partial void DataDestroy(nint data);

    [LibraryImport(LibraryName, EntryPoint = "skn_typeface_create_from_file")]
    internal static partial NativeTypefaceHandle TypefaceCreateFromFile(byte* path);

    [LibraryImport(LibraryName, EntryPoint = "skn_typeface_destroy")]
    [SuppressGCTransition]
    internal static partial void TypefaceDestroy(nint typeface);

    [LibraryImport(LibraryName, EntryPoint = "skn_glyph_run_create")]
    internal static partial NativeGlyphRunHandle GlyphRunCreate(NativeTypefaceHandle typeface, float emSize, ushort* glyphIndices, NativeGlyphPosition* positions, int glyphCount, float baselineX, float baselineY);

    [LibraryImport(LibraryName, EntryPoint = "skn_glyph_run_create_with_options")]
    internal static partial NativeGlyphRunHandle GlyphRunCreateWithOptions(NativeTypefaceHandle typeface, float emSize, ushort* glyphIndices, NativeGlyphPosition* positions, int glyphCount, float baselineX, float baselineY, uint textOptions);

    [LibraryImport(LibraryName, EntryPoint = "skn_glyph_run_get_intersections")]
    internal static partial int GlyphRunGetIntersections(NativeGlyphRunHandle glyphRun, float lowerLimit, float upperLimit, float* values, int valueCapacity);

    [LibraryImport(LibraryName, EntryPoint = "skn_glyph_run_destroy")]
    [SuppressGCTransition]
    internal static partial void GlyphRunDestroy(nint glyphRun);

    [LibraryImport(LibraryName, EntryPoint = "skn_path_create")]
    internal static partial NativePathHandle PathCreate(NativePathCommand* commands, int commandCount, NativePathFillRule fillRule);

    [LibraryImport(LibraryName, EntryPoint = "skn_path_create_rect")]
    internal static partial NativePathHandle PathCreateRect(float x, float y, float width, float height, NativePathFillRule fillRule);

    [LibraryImport(LibraryName, EntryPoint = "skn_path_create_ellipse")]
    internal static partial NativePathHandle PathCreateEllipse(float x, float y, float width, float height, NativePathFillRule fillRule);

    [LibraryImport(LibraryName, EntryPoint = "skn_path_create_group")]
    internal static partial NativePathHandle PathCreateGroup(nint* paths, int pathCount, NativePathFillRule fillRule);

    [LibraryImport(LibraryName, EntryPoint = "skn_path_create_combined")]
    internal static partial NativePathHandle PathCreateCombined(NativePathHandle first, NativePathHandle second, NativePathOp op, NativePathFillRule fillRule);

    [LibraryImport(LibraryName, EntryPoint = "skn_path_create_transformed")]
    internal static partial NativePathHandle PathCreateTransformed(NativePathHandle source, NativeMatrix* transform);

    [LibraryImport(LibraryName, EntryPoint = "skn_path_create_from_glyph_run")]
    internal static partial NativePathHandle PathCreateFromGlyphRun(NativeGlyphRunHandle glyphRun);

    [LibraryImport(LibraryName, EntryPoint = "skn_path_get_bounds")]
    [SuppressGCTransition]
    internal static partial int PathGetBounds(NativePathHandle path, float* x, float* y, float* width, float* height);

    [LibraryImport(LibraryName, EntryPoint = "skn_path_contains")]
    [SuppressGCTransition]
    internal static partial int PathContains(NativePathHandle path, float x, float y);

    [LibraryImport(LibraryName, EntryPoint = "skn_path_get_contour_length")]
    [SuppressGCTransition]
    internal static partial float PathGetContourLength(NativePathHandle path);

    [LibraryImport(LibraryName, EntryPoint = "skn_path_get_point_at_distance")]
    [SuppressGCTransition]
    internal static partial int PathGetPointAtDistance(NativePathHandle path, float distance, float* x, float* y);

    [LibraryImport(LibraryName, EntryPoint = "skn_path_get_point_and_tangent_at_distance")]
    [SuppressGCTransition]
    internal static partial int PathGetPointAndTangentAtDistance(NativePathHandle path, float distance, float* x, float* y, float* tangentX, float* tangentY);

    [LibraryImport(LibraryName, EntryPoint = "skn_path_create_segment")]
    internal static partial NativePathHandle PathCreateSegment(NativePathHandle path, float startDistance, float stopDistance, int startWithMoveTo);

    [LibraryImport(LibraryName, EntryPoint = "skn_path_create_stroked")]
    internal static partial NativePathHandle PathCreateStroked(NativePathHandle path, float strokeWidth, NativeStrokeHandle stroke);

    [LibraryImport(LibraryName, EntryPoint = "skn_path_destroy")]
    [SuppressGCTransition]
    internal static partial void PathDestroy(nint path);

    [LibraryImport(LibraryName, EntryPoint = "skn_path_stream_mesh_create")]
    internal static partial NativePathStreamMeshHandle PathStreamMeshCreate(SkiaNativePathStreamElement* elements, int elementCount, float strokeWidthScale);

    [LibraryImport(LibraryName, EntryPoint = "skn_path_stream_mesh_destroy")]
    [SuppressGCTransition]
    internal static partial void PathStreamMeshDestroy(nint mesh);

    [LibraryImport(LibraryName, EntryPoint = "skn_mesh_spec_create")]
    internal static partial NativeMeshSpecificationHandle MeshSpecCreate(
        NativeMeshAttribute* attributes,
        int attributeCount,
        int vertexStride,
        NativeMeshVarying* varyings,
        int varyingCount,
        byte* vertexSksl,
        byte* fragmentSksl,
        byte* error,
        int errorCapacity);

    [LibraryImport(LibraryName, EntryPoint = "skn_mesh_spec_get_stride")]
    [SuppressGCTransition]
    internal static partial int MeshSpecGetStride(NativeMeshSpecificationHandle spec);

    [LibraryImport(LibraryName, EntryPoint = "skn_mesh_spec_get_uniform_size")]
    [SuppressGCTransition]
    internal static partial int MeshSpecGetUniformSize(NativeMeshSpecificationHandle spec);

    [LibraryImport(LibraryName, EntryPoint = "skn_mesh_spec_get_uniform")]
    internal static partial int MeshSpecGetUniform(NativeMeshSpecificationHandle spec, byte* name, out NativeMeshUniformInfo info);

    [LibraryImport(LibraryName, EntryPoint = "skn_mesh_spec_destroy")]
    [SuppressGCTransition]
    internal static partial void MeshSpecDestroy(nint spec);

    [LibraryImport(LibraryName, EntryPoint = "skn_mesh_create")]
    internal static partial NativeMeshHandle MeshCreate(
        nint context,
        NativeMeshSpecificationHandle spec,
        NativeMeshMode mode,
        byte* vertices,
        int vertexBytes,
        int vertexCount,
        ushort* indices,
        int indexCount,
        byte* uniforms,
        int uniformBytes,
        float left,
        float top,
        float right,
        float bottom);

    [LibraryImport(LibraryName, EntryPoint = "skn_mesh_update_vertices")]
    internal static partial int MeshUpdateVertices(nint context, NativeMeshHandle mesh, byte* vertices, int byteOffset, int byteCount, int vertexCount);

    [LibraryImport(LibraryName, EntryPoint = "skn_mesh_update_indices")]
    internal static partial int MeshUpdateIndices(nint context, NativeMeshHandle mesh, ushort* indices, int indexOffset, int indexCount);

    [LibraryImport(LibraryName, EntryPoint = "skn_mesh_update_uniforms")]
    internal static partial int MeshUpdateUniforms(NativeMeshHandle mesh, byte* uniforms, int uniformBytes);

    [LibraryImport(LibraryName, EntryPoint = "skn_mesh_set_bounds")]
    [SuppressGCTransition]
    internal static partial int MeshSetBounds(NativeMeshHandle mesh, float left, float top, float right, float bottom);

    [LibraryImport(LibraryName, EntryPoint = "skn_mesh_destroy")]
    [SuppressGCTransition]
    internal static partial void MeshDestroy(nint mesh);

    [LibraryImport(LibraryName, EntryPoint = "skn_shader_create_linear")]
    internal static partial NativeShaderHandle ShaderCreateLinear(float x0, float y0, float x1, float y1, NativeGradientStop* stops, int stopCount, NativeGradientSpreadMethod spreadMethod);

    [LibraryImport(LibraryName, EntryPoint = "skn_shader_create_linear_with_matrix")]
    internal static partial NativeShaderHandle ShaderCreateLinearWithMatrix(float x0, float y0, float x1, float y1, NativeGradientStop* stops, int stopCount, NativeGradientSpreadMethod spreadMethod, NativeMatrix* localMatrix);

    [LibraryImport(LibraryName, EntryPoint = "skn_shader_create_radial")]
    internal static partial NativeShaderHandle ShaderCreateRadial(float centerX, float centerY, float originX, float originY, float radius, NativeGradientStop* stops, int stopCount, NativeGradientSpreadMethod spreadMethod);

    [LibraryImport(LibraryName, EntryPoint = "skn_shader_create_radial_with_matrix")]
    internal static partial NativeShaderHandle ShaderCreateRadialWithMatrix(float centerX, float centerY, float originX, float originY, float radius, NativeGradientStop* stops, int stopCount, NativeGradientSpreadMethod spreadMethod, NativeMatrix* localMatrix);

    [LibraryImport(LibraryName, EntryPoint = "skn_shader_create_sweep")]
    internal static partial NativeShaderHandle ShaderCreateSweep(float centerX, float centerY, NativeGradientStop* stops, int stopCount, NativeGradientSpreadMethod spreadMethod, NativeMatrix* localMatrix);

    [LibraryImport(LibraryName, EntryPoint = "skn_shader_create_bitmap")]
    internal static partial NativeShaderHandle ShaderCreateBitmap(NativeBitmapHandle bitmap, NativeTileMode tileX, NativeTileMode tileY, NativeMatrix* localMatrix);

    [LibraryImport(LibraryName, EntryPoint = "skn_shader_destroy")]
    [SuppressGCTransition]
    internal static partial void ShaderDestroy(nint shader);

    [LibraryImport(LibraryName, EntryPoint = "skn_stroke_create")]
    internal static partial NativeStrokeHandle StrokeCreate(NativeStrokeCap cap, NativeStrokeJoin join, float miterLimit, float* dashes, int dashCount, float dashOffset);

    [LibraryImport(LibraryName, EntryPoint = "skn_stroke_destroy")]
    [SuppressGCTransition]
    internal static partial void StrokeDestroy(nint stroke);
}
