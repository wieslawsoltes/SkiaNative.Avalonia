#pragma once

#include <stdint.h>
#include <stddef.h>

#if defined(_WIN32)
#define SKN_EXPORT __declspec(dllexport)
#else
#define SKN_EXPORT __attribute__((visibility("default")))
#endif

#ifdef __cplusplus
extern "C" {
#endif

typedef struct skn_context skn_context_t;
typedef struct skn_session skn_session_t;
typedef struct skn_bitmap skn_bitmap_t;
typedef struct skn_typeface skn_typeface_t;
typedef struct skn_glyph_run skn_glyph_run_t;
typedef struct skn_path skn_path_t;
typedef struct skn_shader skn_shader_t;
typedef struct skn_stroke skn_stroke_t;
typedef struct skn_data skn_data_t;

typedef enum skn_pixel_format {
    SKN_PIXEL_FORMAT_BGRA8888 = 1,
    SKN_PIXEL_FORMAT_RGBA8888 = 2,
    SKN_PIXEL_FORMAT_RGB565 = 3
} skn_pixel_format_t;

typedef enum skn_alpha_format {
    SKN_ALPHA_FORMAT_PREMUL = 1,
    SKN_ALPHA_FORMAT_OPAQUE = 2,
    SKN_ALPHA_FORMAT_UNPREMUL = 3
} skn_alpha_format_t;

typedef enum skn_encoded_image_format {
    SKN_ENCODED_IMAGE_FORMAT_PNG = 1
} skn_encoded_image_format_t;

typedef struct skn_color {
    float r;
    float g;
    float b;
    float a;
} skn_color_t;

typedef struct skn_matrix {
    double m11;
    double m12;
    double m21;
    double m22;
    double m31;
    double m32;
} skn_matrix_t;

typedef struct skn_command {
    uint32_t kind;
    uint32_t flags;
    void* resource0;
    void* resource1;
    void* resource2;
    skn_color_t fill;
    skn_color_t stroke;
    float stroke_thickness;
    float x0;
    float y0;
    float x1;
    float y1;
    float x2;
    float y2;
    float x3;
    float y3;
    skn_matrix_t matrix;
} skn_command_t;

typedef enum skn_gradient_spread_method {
    SKN_GRADIENT_SPREAD_PAD = 0,
    SKN_GRADIENT_SPREAD_REFLECT = 1,
    SKN_GRADIENT_SPREAD_REPEAT = 2
} skn_gradient_spread_method_t;

typedef enum skn_tile_mode {
    SKN_TILE_MODE_CLAMP = 0,
    SKN_TILE_MODE_REPEAT = 1,
    SKN_TILE_MODE_MIRROR = 2,
    SKN_TILE_MODE_DECAL = 3
} skn_tile_mode_t;

typedef enum skn_stroke_cap {
    SKN_STROKE_CAP_BUTT = 0,
    SKN_STROKE_CAP_ROUND = 1,
    SKN_STROKE_CAP_SQUARE = 2
} skn_stroke_cap_t;

typedef enum skn_stroke_join {
    SKN_STROKE_JOIN_MITER = 0,
    SKN_STROKE_JOIN_ROUND = 1,
    SKN_STROKE_JOIN_BEVEL = 2
} skn_stroke_join_t;

typedef struct skn_gradient_stop {
    float offset;
    skn_color_t color;
} skn_gradient_stop_t;

typedef enum skn_path_command_kind {
    SKN_PATH_MOVE_TO = 1,
    SKN_PATH_LINE_TO = 2,
    SKN_PATH_QUAD_TO = 3,
    SKN_PATH_CUBIC_TO = 4,
    SKN_PATH_ARC_TO = 5,
    SKN_PATH_CLOSE = 6
} skn_path_command_kind_t;

typedef enum skn_path_fill_rule {
    SKN_PATH_FILL_NON_ZERO = 0,
    SKN_PATH_FILL_EVEN_ODD = 1
} skn_path_fill_rule_t;

typedef enum skn_path_op {
    SKN_PATH_OP_UNION = 0,
    SKN_PATH_OP_INTERSECT = 1,
    SKN_PATH_OP_XOR = 2,
    SKN_PATH_OP_DIFFERENCE = 3
} skn_path_op_t;

typedef struct skn_path_command {
    uint32_t kind;
    uint32_t flags;
    float x0;
    float y0;
    float x1;
    float y1;
    float x2;
    float y2;
    float x3;
    float y3;
} skn_path_command_t;

SKN_EXPORT skn_context_t* skn_context_create_metal(void* device, void* queue, uint64_t max_resource_bytes, int diagnostics_enabled);
SKN_EXPORT skn_context_t* skn_context_create_cpu(uint64_t max_resource_bytes, int diagnostics_enabled);
SKN_EXPORT void skn_context_purge_unlocked_resources(skn_context_t* context);
SKN_EXPORT int skn_context_get_resource_cache_usage(skn_context_t* context, int* resource_count, uint64_t* resource_bytes, uint64_t* purgeable_bytes, uint64_t* resource_limit);
SKN_EXPORT void skn_context_destroy(skn_context_t* context);

SKN_EXPORT skn_session_t* skn_session_begin_metal(skn_context_t* context, void* texture, int width, int height, double scale, int is_y_flipped);
SKN_EXPORT skn_session_t* skn_session_begin_raster(skn_context_t* context, int width, int height, double dpi_x, double dpi_y);
SKN_EXPORT skn_session_t* skn_session_begin_bitmap(skn_context_t* context, skn_bitmap_t* bitmap, double dpi_x, double dpi_y);
SKN_EXPORT int skn_session_flush_commands(skn_session_t* session, const skn_command_t* commands, int command_count);
SKN_EXPORT void skn_session_end(skn_session_t* session);

SKN_EXPORT skn_bitmap_t* skn_bitmap_create_raster(int width, int height, double dpi_x, double dpi_y);
SKN_EXPORT skn_bitmap_t* skn_bitmap_create_from_encoded(const void* data, int length, double dpi_x, double dpi_y);
SKN_EXPORT int skn_bitmap_upload_pixels(skn_bitmap_t* bitmap, const void* pixels, int row_bytes, skn_pixel_format_t pixel_format, skn_alpha_format_t alpha_format);
SKN_EXPORT int skn_bitmap_read_pixels(skn_bitmap_t* bitmap, void* pixels, int row_bytes, skn_pixel_format_t pixel_format, skn_alpha_format_t alpha_format);
SKN_EXPORT skn_data_t* skn_bitmap_encode(skn_bitmap_t* bitmap, skn_encoded_image_format_t format, int quality);
SKN_EXPORT int skn_bitmap_get_width(skn_bitmap_t* bitmap);
SKN_EXPORT int skn_bitmap_get_height(skn_bitmap_t* bitmap);
SKN_EXPORT void skn_bitmap_destroy(skn_bitmap_t* bitmap);

SKN_EXPORT const void* skn_data_get_bytes(skn_data_t* data);
SKN_EXPORT size_t skn_data_get_size(skn_data_t* data);
SKN_EXPORT void skn_data_destroy(skn_data_t* data);

SKN_EXPORT skn_typeface_t* skn_typeface_create_from_file(const char* path);
SKN_EXPORT void skn_typeface_destroy(skn_typeface_t* typeface);

SKN_EXPORT skn_glyph_run_t* skn_glyph_run_create(skn_typeface_t* typeface, float em_size, const uint16_t* glyph_indices, const float* positions_xy, int glyph_count, float baseline_x, float baseline_y);
SKN_EXPORT skn_glyph_run_t* skn_glyph_run_create_with_options(skn_typeface_t* typeface, float em_size, const uint16_t* glyph_indices, const float* positions_xy, int glyph_count, float baseline_x, float baseline_y, uint32_t text_options);
SKN_EXPORT int skn_glyph_run_get_intersections(skn_glyph_run_t* glyph_run, float lower_limit, float upper_limit, float* values, int value_capacity);
SKN_EXPORT void skn_glyph_run_destroy(skn_glyph_run_t* glyph_run);

SKN_EXPORT skn_path_t* skn_path_create(const skn_path_command_t* commands, int command_count, skn_path_fill_rule_t fill_rule);
SKN_EXPORT skn_path_t* skn_path_create_rect(float x, float y, float width, float height, skn_path_fill_rule_t fill_rule);
SKN_EXPORT skn_path_t* skn_path_create_ellipse(float x, float y, float width, float height, skn_path_fill_rule_t fill_rule);
SKN_EXPORT skn_path_t* skn_path_create_group(skn_path_t* const* paths, int path_count, skn_path_fill_rule_t fill_rule);
SKN_EXPORT skn_path_t* skn_path_create_combined(skn_path_t* first, skn_path_t* second, skn_path_op_t op, skn_path_fill_rule_t fill_rule);
SKN_EXPORT skn_path_t* skn_path_create_transformed(skn_path_t* source, const skn_matrix_t* transform);
SKN_EXPORT skn_path_t* skn_path_create_from_glyph_run(skn_glyph_run_t* glyph_run);
SKN_EXPORT int skn_path_get_bounds(skn_path_t* path, float* x, float* y, float* width, float* height);
SKN_EXPORT int skn_path_contains(skn_path_t* path, float x, float y);
SKN_EXPORT float skn_path_get_contour_length(skn_path_t* path);
SKN_EXPORT int skn_path_get_point_at_distance(skn_path_t* path, float distance, float* x, float* y);
SKN_EXPORT int skn_path_get_point_and_tangent_at_distance(skn_path_t* path, float distance, float* x, float* y, float* tangent_x, float* tangent_y);
SKN_EXPORT skn_path_t* skn_path_create_segment(skn_path_t* path, float start_distance, float stop_distance, int start_with_move_to);
SKN_EXPORT skn_path_t* skn_path_create_stroked(skn_path_t* path, float stroke_width, skn_stroke_t* stroke);
SKN_EXPORT void skn_path_destroy(skn_path_t* path);

SKN_EXPORT skn_shader_t* skn_shader_create_linear(float x0, float y0, float x1, float y1, const skn_gradient_stop_t* stops, int stop_count, skn_gradient_spread_method_t spread_method);
SKN_EXPORT skn_shader_t* skn_shader_create_linear_with_matrix(float x0, float y0, float x1, float y1, const skn_gradient_stop_t* stops, int stop_count, skn_gradient_spread_method_t spread_method, const skn_matrix_t* local_matrix);
SKN_EXPORT skn_shader_t* skn_shader_create_radial(float center_x, float center_y, float origin_x, float origin_y, float radius, const skn_gradient_stop_t* stops, int stop_count, skn_gradient_spread_method_t spread_method);
SKN_EXPORT skn_shader_t* skn_shader_create_radial_with_matrix(float center_x, float center_y, float origin_x, float origin_y, float radius, const skn_gradient_stop_t* stops, int stop_count, skn_gradient_spread_method_t spread_method, const skn_matrix_t* local_matrix);
SKN_EXPORT skn_shader_t* skn_shader_create_sweep(float center_x, float center_y, const skn_gradient_stop_t* stops, int stop_count, skn_gradient_spread_method_t spread_method, const skn_matrix_t* local_matrix);
SKN_EXPORT skn_shader_t* skn_shader_create_bitmap(skn_bitmap_t* bitmap, skn_tile_mode_t tile_x, skn_tile_mode_t tile_y, const skn_matrix_t* local_matrix);
SKN_EXPORT void skn_shader_destroy(skn_shader_t* shader);

SKN_EXPORT skn_stroke_t* skn_stroke_create(skn_stroke_cap_t cap, skn_stroke_join_t join, float miter_limit, const float* dashes, int dash_count, float dash_offset);
SKN_EXPORT void skn_stroke_destroy(skn_stroke_t* stroke);

#ifdef __cplusplus
}
#endif
