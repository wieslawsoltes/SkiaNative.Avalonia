#include "SkiaNativeApi.h"

#include <algorithm>
#include <chrono>
#include <cmath>
#include <cstdint>
#include <cstring>
#include <memory>
#include <vector>

#if defined(__APPLE__)
extern "C" void* objc_autoreleasePoolPush(void);
extern "C" void objc_autoreleasePoolPop(void* context);
#endif

static_assert(sizeof(skn_color_t) == 16, "skn_color_t must stay ABI-compatible with NativeColor.");
static_assert(sizeof(skn_matrix_t) == 48, "skn_matrix_t must stay ABI-compatible with NativeMatrix.");
static_assert(sizeof(skn_command_t) == 152, "skn_command_t must stay ABI-compatible with NativeCommand.");
static_assert(sizeof(skn_gradient_stop_t) == 20, "skn_gradient_stop_t must stay ABI-compatible with NativeGradientStop.");
static_assert(sizeof(skn_path_command_t) == 40, "skn_path_command_t must stay ABI-compatible with NativePathCommand.");

#if defined(SKIANATIVE_WITH_SKIA)
#include "include/core/SkCanvas.h"
#include "include/core/SkBlendMode.h"
#include "include/core/SkClipOp.h"
#include "include/core/SkColor.h"
#include "include/core/SkColorSpace.h"
#include "include/core/SkData.h"
#include "include/core/SkFont.h"
#include "include/core/SkFontMgr.h"
#include "include/core/SkFontTypes.h"
#include "include/core/SkImage.h"
#include "include/core/SkMatrix.h"
#include "include/core/SkPaint.h"
#include "include/core/SkPath.h"
#include "include/core/SkPathBuilder.h"
#include "include/core/SkPathMeasure.h"
#include "include/core/SkPathUtils.h"
#include "include/core/SkPixmap.h"
#include "include/core/SkRRect.h"
#include "include/core/SkRect.h"
#include "include/core/SkSamplingOptions.h"
#include "include/core/SkShader.h"
#include "include/core/SkSpan.h"
#include "include/core/SkSurface.h"
#include "include/core/SkTextBlob.h"
#include "include/core/SkTileMode.h"
#include "include/core/SkTypeface.h"
#include "include/effects/SkDashPathEffect.h"
#include "include/effects/SkGradient.h"
#include "include/effects/SkImageFilters.h"
#include "include/encode/SkPngEncoder.h"
#include "include/gpu/ganesh/GrContextOptions.h"
#include "include/gpu/ganesh/GrBackendSurface.h"
#include "include/gpu/ganesh/GrDirectContext.h"
#include "include/gpu/ganesh/SkSurfaceGanesh.h"
#include "include/gpu/ganesh/GrTypes.h"
#include "include/gpu/ganesh/mtl/GrMtlBackendContext.h"
#include "include/gpu/ganesh/mtl/GrMtlBackendSurface.h"
#include "include/gpu/ganesh/mtl/GrMtlDirectContext.h"
#include "include/gpu/ganesh/mtl/GrMtlTypes.h"
#include "include/pathops/SkPathOps.h"
#include "include/ports/SkFontMgr_mac_ct.h"
#endif

namespace {

#if defined(__APPLE__)
class ScopedAutoreleasePool {
public:
    ScopedAutoreleasePool() : context_(objc_autoreleasePoolPush()) {}
    ~ScopedAutoreleasePool() { objc_autoreleasePoolPop(context_); }

    ScopedAutoreleasePool(const ScopedAutoreleasePool&) = delete;
    ScopedAutoreleasePool& operator=(const ScopedAutoreleasePool&) = delete;

private:
    void* context_;
};
#else
class ScopedAutoreleasePool {
public:
    ScopedAutoreleasePool() = default;
};
#endif

enum CommandKind : uint32_t {
    Save = 1,
    Restore = 2,
    SetTransform = 3,
    Clear = 4,
    DrawLine = 5,
    DrawRect = 6,
    DrawRoundRect = 7,
    DrawEllipse = 8,
    PushClipRect = 9,
    PushClipRoundRect = 10,
    DrawBitmap = 11,
    DrawGlyphRun = 12,
    DrawPath = 13,
    PushClipPath = 14,
    SaveLayer = 15,
    PushOpacityMaskLayer = 16,
    PopOpacityMaskLayer = 17,
    DrawBoxShadow = 18,
};

#if defined(SKIANATIVE_WITH_SKIA)
struct OpacityMaskFrame {
    SkMatrix transform;
    skn_shader_t* shader = nullptr;
    skn_color_t color = {};
};

static SkColor4f to_sk_color(const skn_color_t& c) {
    return {c.r, c.g, c.b, c.a};
}

static SkPaint make_paint(const skn_color_t& color, SkPaint::Style style, float stroke_width = 1.0f) {
    SkPaint paint;
    paint.setAntiAlias(true);
    paint.setColor4f(to_sk_color(color));
    paint.setStyle(style);
    paint.setStrokeWidth(std::max(stroke_width, 0.0f));
    return paint;
}

static SkPaint make_command_paint(const skn_color_t& color, SkPaint::Style style, float stroke_width, skn_shader_t* shader, skn_stroke_t* stroke);

static constexpr uint32_t kBitmapSamplingMask = 0xFu;
static constexpr uint32_t kBitmapBlendShift = 8u;
static constexpr uint32_t kBitmapBlendMask = 0x1Fu << kBitmapBlendShift;
static constexpr uint32_t kBitmapAntiAliasFlag = 1u << 16u;
static constexpr uint32_t kShapeAntiAliasFlag = 1u << 16u;
static constexpr uint32_t kBoxShadowInsetFlag = 1u << 0u;
static constexpr uint32_t kTextEdgingMask = 0x3u;
static constexpr uint32_t kTextHintingShift = 2u;
static constexpr uint32_t kTextHintingMask = 0x3u << kTextHintingShift;
static constexpr uint32_t kTextForceAutoHintingFlag = 1u << 4u;
static constexpr uint32_t kTextSubpixelFlag = 1u << 5u;
static constexpr uint32_t kTextBaselineSnapFlag = 1u << 6u;

static SkSamplingOptions bitmap_sampling_options(uint32_t flags) {
    switch (flags & kBitmapSamplingMask) {
        case 1u:
            return SkSamplingOptions(SkFilterMode::kNearest, SkMipmapMode::kNone);
        case 3u:
            return SkSamplingOptions(SkFilterMode::kLinear, SkMipmapMode::kLinear);
        case 4u:
            return SkSamplingOptions(SkCubicResampler::Mitchell());
        case 2u:
        default:
            return SkSamplingOptions(SkFilterMode::kLinear, SkMipmapMode::kNone);
    }
}

static SkBlendMode bitmap_blend_mode(uint32_t flags) {
    auto mode = (flags & kBitmapBlendMask) >> kBitmapBlendShift;
    if (mode > static_cast<uint32_t>(SkBlendMode::kLastMode)) {
        return SkBlendMode::kSrcOver;
    }

    return static_cast<SkBlendMode>(mode);
}

static void apply_shape_paint_flags(SkPaint& paint, uint32_t flags) {
    paint.setAntiAlias((flags & kShapeAntiAliasFlag) != 0);
}

static void apply_text_font_options(SkFont& font, uint32_t options) {
    switch (options & kTextEdgingMask) {
        case 1u:
            font.setEdging(SkFont::Edging::kAlias);
            break;
        case 2u:
            font.setEdging(SkFont::Edging::kAntiAlias);
            break;
        case 3u:
        case 0u:
        default:
            font.setEdging(SkFont::Edging::kSubpixelAntiAlias);
            break;
    }

    switch ((options & kTextHintingMask) >> kTextHintingShift) {
        case 1u:
            font.setHinting(SkFontHinting::kNone);
            break;
        case 2u:
            font.setHinting(SkFontHinting::kSlight);
            break;
        case 3u:
        case 0u:
        default:
            font.setHinting(SkFontHinting::kFull);
            break;
    }

    font.setForceAutoHinting((options & kTextForceAutoHintingFlag) != 0);
    font.setSubpixel((options & kTextSubpixelFlag) != 0);
    font.setBaselineSnap((options & kTextBaselineSnapFlag) != 0);
}

static float blur_radius_to_sigma(float radius) {
    return radius <= 0.0f ? 0.0f : 0.288675f * radius + 0.5f;
}

static SkAlphaType to_sk_alpha(skn_alpha_format_t alpha_format) {
    switch (alpha_format) {
        case SKN_ALPHA_FORMAT_OPAQUE:
            return kOpaque_SkAlphaType;
        case SKN_ALPHA_FORMAT_UNPREMUL:
            return kUnpremul_SkAlphaType;
        case SKN_ALPHA_FORMAT_PREMUL:
        default:
            return kPremul_SkAlphaType;
    }
}

static SkColorType to_sk_color_type(skn_pixel_format_t pixel_format) {
    switch (pixel_format) {
        case SKN_PIXEL_FORMAT_RGBA8888:
            return kRGBA_8888_SkColorType;
        case SKN_PIXEL_FORMAT_RGB565:
            return kRGB_565_SkColorType;
        case SKN_PIXEL_FORMAT_BGRA8888:
        default:
            return kBGRA_8888_SkColorType;
    }
}

static SkPathFillType to_sk_fill_type(skn_path_fill_rule_t fill_rule) {
    return fill_rule == SKN_PATH_FILL_NON_ZERO ? SkPathFillType::kWinding : SkPathFillType::kEvenOdd;
}

static SkPathDirection to_sk_direction(uint32_t flags) {
    constexpr uint32_t clockwise = 1u << 1;
    return (flags & clockwise) != 0 ? SkPathDirection::kCW : SkPathDirection::kCCW;
}

static SkMatrix to_sk_matrix(const skn_matrix_t& matrix) {
    SkMatrix result;
    result.setAll(
        static_cast<SkScalar>(matrix.m11), static_cast<SkScalar>(matrix.m21), static_cast<SkScalar>(matrix.m31),
        static_cast<SkScalar>(matrix.m12), static_cast<SkScalar>(matrix.m22), static_cast<SkScalar>(matrix.m32),
        0, 0, 1);
    return result;
}

static SkPathOp to_sk_path_op(skn_path_op_t op) {
    switch (op) {
        case SKN_PATH_OP_INTERSECT:
            return kIntersect_SkPathOp;
        case SKN_PATH_OP_XOR:
            return kXOR_SkPathOp;
        case SKN_PATH_OP_DIFFERENCE:
            return kDifference_SkPathOp;
        case SKN_PATH_OP_UNION:
        default:
            return kUnion_SkPathOp;
    }
}

static SkTileMode to_sk_tile_mode(skn_gradient_spread_method_t spread_method) {
    switch (spread_method) {
        case SKN_GRADIENT_SPREAD_REFLECT:
            return SkTileMode::kMirror;
        case SKN_GRADIENT_SPREAD_REPEAT:
            return SkTileMode::kRepeat;
        case SKN_GRADIENT_SPREAD_PAD:
        default:
            return SkTileMode::kClamp;
    }
}

static SkTileMode to_sk_tile_mode(skn_tile_mode_t tile_mode) {
    switch (tile_mode) {
        case SKN_TILE_MODE_REPEAT:
            return SkTileMode::kRepeat;
        case SKN_TILE_MODE_MIRROR:
            return SkTileMode::kMirror;
        case SKN_TILE_MODE_DECAL:
            return SkTileMode::kDecal;
        case SKN_TILE_MODE_CLAMP:
        default:
            return SkTileMode::kClamp;
    }
}

static SkPaint::Cap to_sk_cap(skn_stroke_cap_t cap) {
    switch (cap) {
        case SKN_STROKE_CAP_ROUND:
            return SkPaint::kRound_Cap;
        case SKN_STROKE_CAP_SQUARE:
            return SkPaint::kSquare_Cap;
        case SKN_STROKE_CAP_BUTT:
        default:
            return SkPaint::kButt_Cap;
    }
}

static SkPaint::Join to_sk_join(skn_stroke_join_t join) {
    switch (join) {
        case SKN_STROKE_JOIN_ROUND:
            return SkPaint::kRound_Join;
        case SKN_STROKE_JOIN_BEVEL:
            return SkPaint::kBevel_Join;
        case SKN_STROKE_JOIN_MITER:
        default:
            return SkPaint::kMiter_Join;
    }
}

static sk_sp<SkImage> bitmap_image(skn_bitmap_t* bitmap);
#endif

} // namespace

struct skn_context {
    void* metal_device = nullptr;
    void* metal_queue = nullptr;
    uint64_t max_resource_bytes = 0;
    bool diagnostics_enabled = false;
#if defined(SKIANATIVE_WITH_SKIA)
    sk_sp<GrDirectContext> direct_context;
#endif
};

struct skn_session {
    skn_context_t* context = nullptr;
    skn_bitmap_t* bitmap_target = nullptr;
    int width = 0;
    int height = 0;
    double scale = 1.0;
    void* metal_texture = nullptr;
#if defined(SKIANATIVE_WITH_SKIA)
    std::unique_ptr<GrBackendRenderTarget> backend_render_target;
    sk_sp<SkSurface> surface;
#endif
};

struct skn_bitmap {
    int width = 0;
    int height = 0;
    double dpi_x = 96.0;
    double dpi_y = 96.0;
#if defined(SKIANATIVE_WITH_SKIA)
    sk_sp<SkSurface> surface;
    sk_sp<SkImage> image;
#endif
};

struct skn_typeface {
#if defined(SKIANATIVE_WITH_SKIA)
    sk_sp<SkTypeface> typeface;
#endif
};

struct skn_glyph_run {
    float em_size = 0.0f;
    float baseline_x = 0.0f;
    float baseline_y = 0.0f;
#if defined(SKIANATIVE_WITH_SKIA)
    sk_sp<SkTypeface> typeface;
    SkFont font;
    std::vector<SkGlyphID> glyphs;
    std::vector<SkPoint> positions;
    sk_sp<SkTextBlob> text_blob;
#endif
};

struct skn_path {
#if defined(SKIANATIVE_WITH_SKIA)
    SkPath path;
#endif
};

struct skn_shader {
#if defined(SKIANATIVE_WITH_SKIA)
    sk_sp<SkShader> shader;
#endif
};

struct skn_stroke {
#if defined(SKIANATIVE_WITH_SKIA)
    SkPaint::Cap cap = SkPaint::kButt_Cap;
    SkPaint::Join join = SkPaint::kMiter_Join;
    float miter_limit = 10.0f;
    std::vector<SkScalar> dashes;
    float dash_offset = 0.0f;
    sk_sp<SkPathEffect> path_effect;
#endif
};

struct skn_data {
#if defined(SKIANATIVE_WITH_SKIA)
    sk_sp<SkData> data;
#endif
};

#if defined(SKIANATIVE_WITH_SKIA)
namespace {

static SkPaint make_command_paint(const skn_color_t& color, SkPaint::Style style, float stroke_width, skn_shader_t* shader, skn_stroke_t* stroke) {
    auto paint = make_paint(color, style, stroke_width);
    if (shader != nullptr && shader->shader) {
        paint.setShader(shader->shader);
    }

    if (style == SkPaint::kStroke_Style && stroke != nullptr) {
        paint.setStrokeCap(stroke->cap);
        paint.setStrokeJoin(stroke->join);
        paint.setStrokeMiter(std::max(stroke->miter_limit, 0.0f));
        if (stroke->path_effect) {
            paint.setPathEffect(stroke->path_effect);
        }
    }

    return paint;
}

static SkPaint make_stroke_path_paint(float stroke_width, skn_stroke_t* stroke) {
    skn_color_t color = {0, 0, 0, 1};
    return make_command_paint(color, SkPaint::kStroke_Style, stroke_width, nullptr, stroke);
}

static sk_sp<SkImage> bitmap_image(skn_bitmap_t* bitmap) {
    if (bitmap == nullptr) {
        return nullptr;
    }

    if (bitmap->image) {
        return bitmap->image;
    }

    if (bitmap->surface) {
        bitmap->image = bitmap->surface->makeImageSnapshot();
        return bitmap->image;
    }

    return nullptr;
}

static SkPath build_path(const skn_path_command_t* commands, int command_count, skn_path_fill_rule_t fill_rule) {
    SkPathBuilder builder(to_sk_fill_type(fill_rule));
    builder.setIsVolatile(false);

    if (commands == nullptr || command_count <= 0) {
        return builder.detach();
    }

    constexpr uint32_t large_arc = 1u;
    for (int i = 0; i < command_count; ++i) {
        const auto& command = commands[i];
        switch (command.kind) {
            case SKN_PATH_MOVE_TO:
                builder.moveTo(command.x0, command.y0);
                break;
            case SKN_PATH_LINE_TO:
                builder.lineTo(command.x0, command.y0);
                break;
            case SKN_PATH_QUAD_TO:
                builder.quadTo(command.x0, command.y0, command.x1, command.y1);
                break;
            case SKN_PATH_CUBIC_TO:
                builder.cubicTo(command.x0, command.y0, command.x1, command.y1, command.x2, command.y2);
                break;
            case SKN_PATH_ARC_TO: {
                const auto arc_size = (command.flags & large_arc) != 0
                    ? SkPathBuilder::ArcSize::kLarge_ArcSize
                    : SkPathBuilder::ArcSize::kSmall_ArcSize;
                builder.arcTo(
                    SkPoint::Make(std::abs(command.x0), std::abs(command.y0)),
                    command.x1,
                    arc_size,
                    to_sk_direction(command.flags),
                    SkPoint::Make(command.x2, command.y2));
                break;
            }
            case SKN_PATH_CLOSE:
                builder.close();
                break;
            default:
                break;
        }
    }

    return builder.detach();
}

static SkPath build_rect_path(float x, float y, float width, float height, skn_path_fill_rule_t fill_rule) {
    SkPathBuilder builder(to_sk_fill_type(fill_rule));
    builder.setIsVolatile(false);
    builder.addRect(SkRect::MakeXYWH(x, y, width, height));
    return builder.detach();
}

static SkPath build_ellipse_path(float x, float y, float width, float height, skn_path_fill_rule_t fill_rule) {
    SkPathBuilder builder(to_sk_fill_type(fill_rule));
    builder.setIsVolatile(false);
    builder.addOval(SkRect::MakeXYWH(x, y, width, height));
    return builder.detach();
}

static SkPath build_group_path(skn_path_t* const* paths, int path_count, skn_path_fill_rule_t fill_rule) {
    SkPathBuilder builder(to_sk_fill_type(fill_rule));
    builder.setIsVolatile(false);

    if (paths == nullptr || path_count <= 0) {
        return builder.detach();
    }

    for (int i = 0; i < path_count; ++i) {
        if (paths[i] != nullptr) {
            builder.addPath(paths[i]->path);
        }
    }

    return builder.detach();
}

static SkGradient build_gradient(const skn_gradient_stop_t* stops, int stop_count, skn_gradient_spread_method_t spread_method, std::vector<SkColor4f>& colors, std::vector<float>& positions) {
    colors.clear();
    positions.clear();

    if (stops != nullptr && stop_count > 0) {
        colors.reserve(static_cast<size_t>(stop_count));
        positions.reserve(static_cast<size_t>(stop_count));
        for (int i = 0; i < stop_count; ++i) {
            colors.push_back(to_sk_color(stops[i].color));
            positions.push_back(std::clamp(stops[i].offset, 0.0f, 1.0f));
        }
    }

    if (colors.empty()) {
        colors.push_back({0, 0, 0, 0});
        colors.push_back({0, 0, 0, 0});
        positions.push_back(0);
        positions.push_back(1);
    } else if (colors.size() == 1) {
        const auto color = colors[0];
        colors.clear();
        positions.clear();
        colors.push_back(color);
        colors.push_back(color);
        positions.push_back(0);
        positions.push_back(1);
    }

    SkGradient::Colors gradient_colors(
        SkSpan<const SkColor4f>(colors.data(), colors.size()),
        SkSpan<const float>(positions.data(), positions.size()),
        to_sk_tile_mode(spread_method),
        SkColorSpace::MakeSRGB());

    SkGradient::Interpolation interpolation;
    interpolation.fInPremul = SkGradient::Interpolation::InPremul::kYes;
    interpolation.fColorSpace = SkGradient::Interpolation::ColorSpace::kSRGB;
    return SkGradient(gradient_colors, interpolation);
}

static void flush_session_surface(skn_session_t* session) {
    if (session == nullptr || !session->surface) {
        return;
    }

    if (session->context != nullptr && session->context->direct_context) {
        session->context->direct_context->flushAndSubmit(session->surface.get());
    }

    if (session->bitmap_target != nullptr) {
        session->bitmap_target->image = session->surface->makeImageSnapshot();
    }
}

} // namespace
#endif

extern "C" {

SKN_EXPORT skn_context_t* skn_context_create_metal(void* device, void* queue, uint64_t max_resource_bytes, int diagnostics_enabled) {
    ScopedAutoreleasePool autorelease_pool;
    auto* context = new skn_context_t();
    context->metal_device = device;
    context->metal_queue = queue;
    context->max_resource_bytes = max_resource_bytes;
    context->diagnostics_enabled = diagnostics_enabled != 0;
#if defined(SKIANATIVE_WITH_SKIA)
    if (device != nullptr && queue != nullptr) {
        GrMtlBackendContext backend_context = {};
        backend_context.fDevice.retain(static_cast<GrMTLHandle>(device));
        backend_context.fQueue.retain(static_cast<GrMTLHandle>(queue));

        GrContextOptions options;
        options.fAvoidStencilBuffers = true;
        if (max_resource_bytes > 0) {
            options.fGlyphCacheTextureMaximumBytes = std::min<size_t>(
                options.fGlyphCacheTextureMaximumBytes,
                std::max<size_t>(1024 * 1024, static_cast<size_t>(max_resource_bytes / 4)));
            if (max_resource_bytes <= 16ull * 1024ull * 1024ull) {
                options.fAllowMultipleGlyphCacheTextures = GrContextOptions::Enable::kNo;
            }
        }

        context->direct_context = GrDirectContexts::MakeMetal(backend_context, options);
        if (context->direct_context && max_resource_bytes > 0) {
            context->direct_context->setResourceCacheLimit(static_cast<size_t>(max_resource_bytes));
        }
    }
#endif
    return context;
}

SKN_EXPORT skn_context_t* skn_context_create_cpu(uint64_t max_resource_bytes, int diagnostics_enabled) {
    auto* context = new skn_context_t();
    context->max_resource_bytes = max_resource_bytes;
    context->diagnostics_enabled = diagnostics_enabled != 0;
    return context;
}

SKN_EXPORT void skn_context_purge_unlocked_resources(skn_context_t* context) {
    ScopedAutoreleasePool autorelease_pool;
#if defined(SKIANATIVE_WITH_SKIA)
    if (context != nullptr && context->direct_context) {
        context->direct_context->submit(GrSyncCpu::kYes);
        context->direct_context->performDeferredCleanup(
            std::chrono::milliseconds(0),
            GrPurgeResourceOptions::kAllResources);
        context->direct_context->purgeUnlockedResources(GrPurgeResourceOptions::kAllResources);
        context->direct_context->freeGpuResources();
        context->direct_context->submit(GrSyncCpu::kYes);
    }
#else
    (void)context;
#endif
}

SKN_EXPORT int skn_context_get_resource_cache_usage(
    skn_context_t* context,
    int* resource_count,
    uint64_t* resource_bytes,
    uint64_t* purgeable_bytes,
    uint64_t* resource_limit) {
    if (resource_count != nullptr) {
        *resource_count = 0;
    }

    if (resource_bytes != nullptr) {
        *resource_bytes = 0;
    }

    if (purgeable_bytes != nullptr) {
        *purgeable_bytes = 0;
    }

    if (resource_limit != nullptr) {
        *resource_limit = 0;
    }

#if defined(SKIANATIVE_WITH_SKIA)
    if (context == nullptr || !context->direct_context) {
        return 0;
    }

    int count = 0;
    size_t bytes = 0;
    context->direct_context->getResourceCacheUsage(&count, &bytes);

    if (resource_count != nullptr) {
        *resource_count = count;
    }

    if (resource_bytes != nullptr) {
        *resource_bytes = static_cast<uint64_t>(bytes);
    }

    if (purgeable_bytes != nullptr) {
        *purgeable_bytes = static_cast<uint64_t>(context->direct_context->getResourceCachePurgeableBytes());
    }

    if (resource_limit != nullptr) {
        *resource_limit = static_cast<uint64_t>(context->direct_context->getResourceCacheLimit());
    }

    return 1;
#else
    (void)context;
    return 0;
#endif
}

SKN_EXPORT void skn_context_destroy(skn_context_t* context) {
    delete context;
}

SKN_EXPORT skn_session_t* skn_session_begin_metal(skn_context_t* context, void* texture, int width, int height, double scale, int is_y_flipped) {
    ScopedAutoreleasePool autorelease_pool;
    auto* session = new skn_session_t();
    session->context = context;
    session->width = std::max(width, 1);
    session->height = std::max(height, 1);
    session->scale = scale;
    session->metal_texture = texture;
#if defined(SKIANATIVE_WITH_SKIA)
    if (context != nullptr && context->direct_context && texture != nullptr) {
        GrMtlTextureInfo texture_info;
        texture_info.fTexture.retain(static_cast<GrMTLHandle>(texture));

        session->backend_render_target = std::make_unique<GrBackendRenderTarget>(
            GrBackendRenderTargets::MakeMtl(session->width, session->height, texture_info));

        const auto origin = is_y_flipped != 0 ? kBottomLeft_GrSurfaceOrigin : kTopLeft_GrSurfaceOrigin;
        session->surface = SkSurfaces::WrapBackendRenderTarget(
            context->direct_context.get(),
            *session->backend_render_target,
            origin,
            kBGRA_8888_SkColorType,
            nullptr,
            nullptr);
    }
#endif
    return session;
}

SKN_EXPORT skn_session_t* skn_session_begin_raster(skn_context_t* context, int width, int height, double dpi_x, double /*dpi_y*/) {
    auto* session = new skn_session_t();
    session->context = context;
    session->width = std::max(width, 1);
    session->height = std::max(height, 1);
    session->scale = dpi_x / 96.0;
#if defined(SKIANATIVE_WITH_SKIA)
    session->surface = SkSurfaces::Raster(SkImageInfo::MakeN32Premul(session->width, session->height));
#endif
    return session;
}

SKN_EXPORT skn_session_t* skn_session_begin_bitmap(skn_context_t* context, skn_bitmap_t* bitmap, double dpi_x, double /*dpi_y*/) {
    if (bitmap == nullptr) {
        return nullptr;
    }

    auto* session = new skn_session_t();
    session->context = context;
    session->bitmap_target = bitmap;
    session->width = std::max(bitmap->width, 1);
    session->height = std::max(bitmap->height, 1);
    session->scale = dpi_x / 96.0;
#if defined(SKIANATIVE_WITH_SKIA)
    if (!bitmap->surface) {
        bitmap->surface = SkSurfaces::Raster(SkImageInfo::MakeN32Premul(session->width, session->height));
        if (bitmap->surface) {
            bitmap->surface->getCanvas()->clear(SK_ColorTRANSPARENT);
        }
    }

    bitmap->image = nullptr;
    session->surface = bitmap->surface;
#endif
    return session;
}

SKN_EXPORT int skn_session_flush_commands(skn_session_t* session, const skn_command_t* commands, int command_count) {
    ScopedAutoreleasePool autorelease_pool;
    if (session == nullptr || commands == nullptr || command_count < 0) {
        return -1;
    }

#if defined(SKIANATIVE_WITH_SKIA)
    if (!session->surface) {
        return 0;
    }

    SkCanvas* canvas = session->surface->getCanvas();
    std::vector<OpacityMaskFrame> opacity_mask_stack;
    for (int i = 0; i < command_count; ++i) {
        const auto& command = commands[i];
        switch (command.kind) {
            case Save:
                canvas->save();
                break;
            case Restore:
                canvas->restore();
                break;
            case SetTransform: {
                canvas->setMatrix(to_sk_matrix(command.matrix));
                break;
            }
            case Clear:
                canvas->clear(to_sk_color(command.fill));
                break;
            case DrawLine: {
                auto paint = make_command_paint(command.stroke, SkPaint::kStroke_Style, command.stroke_thickness, static_cast<skn_shader_t*>(command.resource1), static_cast<skn_stroke_t*>(command.resource2));
                apply_shape_paint_flags(paint, command.flags);
                canvas->drawLine(command.x0, command.y0, command.x1, command.y1, paint);
                break;
            }
            case DrawRect: {
                auto rect = SkRect::MakeXYWH(command.x0, command.y0, command.x1, command.y1);
                if ((command.flags & 1u) != 0) {
                    auto paint = make_command_paint(command.fill, SkPaint::kFill_Style, 1.0f, static_cast<skn_shader_t*>(command.resource1), nullptr);
                    apply_shape_paint_flags(paint, command.flags);
                    canvas->drawRect(rect, paint);
                }
                if ((command.flags & 2u) != 0) {
                    auto paint = make_command_paint(command.stroke, SkPaint::kStroke_Style, command.stroke_thickness, static_cast<skn_shader_t*>(command.resource1), static_cast<skn_stroke_t*>(command.resource2));
                    apply_shape_paint_flags(paint, command.flags);
                    canvas->drawRect(rect, paint);
                }
                break;
            }
            case DrawRoundRect: {
                auto rect = SkRect::MakeXYWH(command.x0, command.y0, command.x1, command.y1);
                auto rr = SkRRect::MakeRectXY(rect, command.x2, command.y2);
                if ((command.flags & 1u) != 0) {
                    auto paint = make_command_paint(command.fill, SkPaint::kFill_Style, 1.0f, static_cast<skn_shader_t*>(command.resource1), nullptr);
                    apply_shape_paint_flags(paint, command.flags);
                    canvas->drawRRect(rr, paint);
                }
                if ((command.flags & 2u) != 0) {
                    auto paint = make_command_paint(command.stroke, SkPaint::kStroke_Style, command.stroke_thickness, static_cast<skn_shader_t*>(command.resource1), static_cast<skn_stroke_t*>(command.resource2));
                    apply_shape_paint_flags(paint, command.flags);
                    canvas->drawRRect(rr, paint);
                }
                break;
            }
            case DrawEllipse: {
                auto rect = SkRect::MakeXYWH(command.x0, command.y0, command.x1, command.y1);
                if ((command.flags & 1u) != 0) {
                    auto paint = make_command_paint(command.fill, SkPaint::kFill_Style, 1.0f, static_cast<skn_shader_t*>(command.resource1), nullptr);
                    apply_shape_paint_flags(paint, command.flags);
                    canvas->drawOval(rect, paint);
                }
                if ((command.flags & 2u) != 0) {
                    auto paint = make_command_paint(command.stroke, SkPaint::kStroke_Style, command.stroke_thickness, static_cast<skn_shader_t*>(command.resource1), static_cast<skn_stroke_t*>(command.resource2));
                    apply_shape_paint_flags(paint, command.flags);
                    canvas->drawOval(rect, paint);
                }
                break;
            }
            case DrawBoxShadow: {
                auto rect = SkRect::MakeXYWH(command.x0, command.y0, command.x1, command.y1);
                const auto radius_x = std::max(command.x2, 0.0f);
                const auto radius_y = std::max(command.y2, 0.0f);
                const auto blur = std::max(command.x3, 0.0f);
                const auto spread = command.y3;
                const auto offset_x = static_cast<SkScalar>(command.matrix.m11);
                const auto offset_y = static_cast<SkScalar>(command.matrix.m12);
                const auto inset = (command.flags & kBoxShadowInsetFlag) != 0;
                const auto is_round = radius_x > 0.0f || radius_y > 0.0f;

                auto paint = make_command_paint(command.fill, SkPaint::kFill_Style, 1.0f, nullptr, nullptr);
                apply_shape_paint_flags(paint, command.flags);
                const auto sigma = blur_radius_to_sigma(blur);
                if (sigma > 0.0f) {
                    paint.setImageFilter(SkImageFilters::Blur(sigma, sigma, nullptr));
                }

                auto clip_rrect = SkRRect::MakeRectXY(rect, radius_x, radius_y);
                canvas->save();
                if (is_round) {
                    canvas->clipRRect(clip_rrect, inset ? SkClipOp::kIntersect : SkClipOp::kDifference, true);
                } else {
                    canvas->clipRect(rect, inset ? SkClipOp::kIntersect : SkClipOp::kDifference, true);
                }

                canvas->translate(offset_x, offset_y);
                if (inset) {
                    auto inner_rect = rect;
                    inner_rect.inset(spread, spread);

                    auto outer_rect = rect;
                    const auto inflate = std::abs(spread) + blur + std::max(std::abs(offset_x), std::abs(offset_y)) + 1.0f;
                    outer_rect.outset(inflate, inflate);

                    const auto outer = SkRRect::MakeRect(outer_rect);
                    const auto inner = SkRRect::MakeRectXY(inner_rect, std::max(radius_x - spread, 0.0f), std::max(radius_y - spread, 0.0f));
                    canvas->drawDRRect(outer, inner, paint);
                } else {
                    auto shadow_rect = rect;
                    shadow_rect.outset(spread, spread);
                    if (is_round) {
                        canvas->drawRRect(SkRRect::MakeRectXY(shadow_rect, std::max(radius_x + spread, 0.0f), std::max(radius_y + spread, 0.0f)), paint);
                    } else {
                        canvas->drawRect(shadow_rect, paint);
                    }
                }

                canvas->restore();
                break;
            }
            case PushClipRect: {
                auto rect = SkRect::MakeXYWH(command.x0, command.y0, command.x1, command.y1);
                canvas->clipRect(rect, SkClipOp::kIntersect, true);
                break;
            }
            case PushClipRoundRect: {
                auto rect = SkRect::MakeXYWH(command.x0, command.y0, command.x1, command.y1);
                canvas->clipRRect(SkRRect::MakeRectXY(rect, command.x2, command.y2), SkClipOp::kIntersect, true);
                break;
            }
            case DrawBitmap: {
                auto* bitmap = static_cast<skn_bitmap_t*>(command.resource0);
                auto image = bitmap_image(bitmap);
                if (!image) {
                    break;
                }

                auto src = SkRect::MakeXYWH(command.x0, command.y0, command.x1, command.y1);
                auto dst = SkRect::MakeXYWH(command.x2, command.y2, command.x3, command.y3);
                SkPaint paint;
                paint.setAntiAlias((command.flags & kBitmapAntiAliasFlag) != 0);
                paint.setAlphaf(std::clamp(command.fill.a, 0.0f, 1.0f));
                paint.setBlendMode(bitmap_blend_mode(command.flags));
                canvas->drawImageRect(
                    image,
                    src,
                    dst,
                    bitmap_sampling_options(command.flags),
                    &paint,
                    SkCanvas::kStrict_SrcRectConstraint);
                break;
            }
            case DrawGlyphRun: {
                auto* glyph_run = static_cast<skn_glyph_run_t*>(command.resource0);
                if (glyph_run == nullptr || !glyph_run->text_blob) {
                    break;
                }

                auto paint = make_command_paint(command.fill, SkPaint::kFill_Style, 1.0f, static_cast<skn_shader_t*>(command.resource1), nullptr);
                canvas->drawTextBlob(glyph_run->text_blob, glyph_run->baseline_x, glyph_run->baseline_y, paint);
                break;
            }
            case DrawPath: {
                auto* path = static_cast<skn_path_t*>(command.resource0);
                if (path == nullptr) {
                    break;
                }

                if ((command.flags & 1u) != 0) {
                    auto paint = make_command_paint(command.fill, SkPaint::kFill_Style, 1.0f, static_cast<skn_shader_t*>(command.resource1), nullptr);
                    apply_shape_paint_flags(paint, command.flags);
                    canvas->drawPath(path->path, paint);
                }
                if ((command.flags & 2u) != 0) {
                    auto paint = make_command_paint(command.stroke, SkPaint::kStroke_Style, command.stroke_thickness, static_cast<skn_shader_t*>(command.resource1), static_cast<skn_stroke_t*>(command.resource2));
                    apply_shape_paint_flags(paint, command.flags);
                    canvas->drawPath(path->path, paint);
                }
                break;
            }
            case PushClipPath: {
                auto* path = static_cast<skn_path_t*>(command.resource0);
                if (path != nullptr) {
                    canvas->clipPath(path->path, SkClipOp::kIntersect, true);
                }
                break;
            }
            case SaveLayer: {
                SkPaint paint;
                paint.setAlphaf(std::clamp(command.fill.a, 0.0f, 1.0f));
                if ((command.flags & 1u) != 0) {
                    auto rect = SkRect::MakeXYWH(command.x0, command.y0, command.x1, command.y1);
                    canvas->saveLayer(rect, &paint);
                } else {
                    canvas->saveLayer(nullptr, &paint);
                }
                break;
            }
            case PushOpacityMaskLayer: {
                opacity_mask_stack.push_back(OpacityMaskFrame{
                    canvas->getLocalToDeviceAs3x3(),
                    static_cast<skn_shader_t*>(command.resource1),
                    command.fill
                });

                if ((command.flags & 1u) != 0) {
                    auto rect = SkRect::MakeXYWH(command.x0, command.y0, command.x1, command.y1);
                    canvas->saveLayer(rect, nullptr);
                } else {
                    canvas->saveLayer(nullptr, nullptr);
                }
                break;
            }
            case PopOpacityMaskLayer: {
                if (opacity_mask_stack.empty()) {
                    break;
                }

                SkPaint mask_layer_paint;
                mask_layer_paint.setBlendMode(SkBlendMode::kDstIn);
                canvas->saveLayer(nullptr, &mask_layer_paint);

                const auto frame = opacity_mask_stack.back();
                opacity_mask_stack.pop_back();
                canvas->setMatrix(frame.transform);

                auto mask_paint = make_command_paint(frame.color, SkPaint::kFill_Style, 1.0f, frame.shader, nullptr);
                canvas->drawPaint(mask_paint);
                canvas->restore();
                canvas->restore();
                break;
            }
            default:
                break;
        }
    }
#endif

    return command_count;
}

SKN_EXPORT void skn_session_end(skn_session_t* session) {
    ScopedAutoreleasePool autorelease_pool;
#if defined(SKIANATIVE_WITH_SKIA)
    flush_session_surface(session);
#endif
    delete session;
}

SKN_EXPORT skn_bitmap_t* skn_bitmap_create_raster(int width, int height, double dpi_x, double dpi_y) {
    auto* bitmap = new skn_bitmap_t();
    bitmap->width = std::max(width, 1);
    bitmap->height = std::max(height, 1);
    bitmap->dpi_x = dpi_x;
    bitmap->dpi_y = dpi_y;
#if defined(SKIANATIVE_WITH_SKIA)
    bitmap->surface = SkSurfaces::Raster(SkImageInfo::MakeN32Premul(bitmap->width, bitmap->height));
    if (bitmap->surface) {
        bitmap->surface->getCanvas()->clear(SK_ColorTRANSPARENT);
    }
#endif
    return bitmap;
}

SKN_EXPORT skn_bitmap_t* skn_bitmap_create_from_encoded(const void* data, int length, double dpi_x, double dpi_y) {
    if (data == nullptr || length <= 0) {
        return nullptr;
    }

#if defined(SKIANATIVE_WITH_SKIA)
    auto encoded = SkData::MakeWithCopy(data, static_cast<size_t>(length));
    auto image = SkImages::DeferredFromEncodedData(encoded);
    if (!image) {
        return nullptr;
    }

    auto* bitmap = new skn_bitmap_t();
    bitmap->width = std::max(image->width(), 1);
    bitmap->height = std::max(image->height(), 1);
    bitmap->dpi_x = dpi_x;
    bitmap->dpi_y = dpi_y;
    bitmap->image = std::move(image);
    return bitmap;
#else
    (void)dpi_x;
    (void)dpi_y;
    return nullptr;
#endif
}

SKN_EXPORT int skn_bitmap_upload_pixels(skn_bitmap_t* bitmap, const void* pixels, int row_bytes, skn_pixel_format_t pixel_format, skn_alpha_format_t alpha_format) {
    if (bitmap == nullptr || pixels == nullptr || row_bytes <= 0) {
        return -1;
    }

#if defined(SKIANATIVE_WITH_SKIA)
    if (!bitmap->surface) {
        bitmap->surface = SkSurfaces::Raster(SkImageInfo::Make(
            bitmap->width,
            bitmap->height,
            to_sk_color_type(pixel_format),
            to_sk_alpha(alpha_format)));
    }

    if (!bitmap->surface) {
        return -2;
    }

    auto info = SkImageInfo::Make(bitmap->width, bitmap->height, to_sk_color_type(pixel_format), to_sk_alpha(alpha_format));
    SkPixmap pixmap(info, pixels, static_cast<size_t>(row_bytes));
    bitmap->surface->writePixels(pixmap, 0, 0);
    bitmap->image = bitmap->surface->makeImageSnapshot();
#else
    (void)pixel_format;
    (void)alpha_format;
#endif
    return 0;
}

SKN_EXPORT int skn_bitmap_read_pixels(skn_bitmap_t* bitmap, void* pixels, int row_bytes, skn_pixel_format_t pixel_format, skn_alpha_format_t alpha_format) {
    if (bitmap == nullptr || pixels == nullptr || row_bytes <= 0) {
        return -1;
    }

#if defined(SKIANATIVE_WITH_SKIA)
    auto info = SkImageInfo::Make(bitmap->width, bitmap->height, to_sk_color_type(pixel_format), to_sk_alpha(alpha_format));
    if (static_cast<size_t>(row_bytes) < info.minRowBytes()) {
        return -2;
    }

    if (bitmap->surface && bitmap->surface->readPixels(info, pixels, static_cast<size_t>(row_bytes), 0, 0)) {
        return 0;
    }

    auto image = bitmap_image(bitmap);
    if (image && image->readPixels(nullptr, info, pixels, static_cast<size_t>(row_bytes), 0, 0, SkImage::kDisallow_CachingHint)) {
        return 0;
    }
#else
    (void)pixel_format;
    (void)alpha_format;
#endif
    return -3;
}

SKN_EXPORT skn_data_t* skn_bitmap_encode(skn_bitmap_t* bitmap, skn_encoded_image_format_t format, int quality) {
    if (bitmap == nullptr) {
        return nullptr;
    }

#if defined(SKIANATIVE_WITH_SKIA)
    if (format != SKN_ENCODED_IMAGE_FORMAT_PNG) {
        return nullptr;
    }

    auto image = bitmap_image(bitmap);
    if (!image) {
        return nullptr;
    }

    SkPngEncoder::Options options;
    if (quality >= 0) {
        options.fZLibLevel = std::clamp(static_cast<int>(std::lround(quality * 9.0 / 100.0)), 0, 9);
    }

    auto encoded = SkPngEncoder::Encode(nullptr, image.get(), options);
    if (!encoded || encoded->empty()) {
        return nullptr;
    }

    auto* result = new skn_data_t();
    result->data = std::move(encoded);
    return result;
#else
    (void)format;
    (void)quality;
    return nullptr;
#endif
}

SKN_EXPORT int skn_bitmap_get_width(skn_bitmap_t* bitmap) {
    return bitmap == nullptr ? 0 : bitmap->width;
}

SKN_EXPORT int skn_bitmap_get_height(skn_bitmap_t* bitmap) {
    return bitmap == nullptr ? 0 : bitmap->height;
}

SKN_EXPORT void skn_bitmap_destroy(skn_bitmap_t* bitmap) {
    delete bitmap;
}

SKN_EXPORT const void* skn_data_get_bytes(skn_data_t* data) {
    if (data == nullptr) {
        return nullptr;
    }

#if defined(SKIANATIVE_WITH_SKIA)
    return data->data ? data->data->data() : nullptr;
#else
    return nullptr;
#endif
}

SKN_EXPORT size_t skn_data_get_size(skn_data_t* data) {
    if (data == nullptr) {
        return 0;
    }

#if defined(SKIANATIVE_WITH_SKIA)
    return data->data ? data->data->size() : 0;
#else
    return 0;
#endif
}

SKN_EXPORT void skn_data_destroy(skn_data_t* data) {
    delete data;
}

SKN_EXPORT skn_typeface_t* skn_typeface_create_from_file(const char* path) {
    if (path == nullptr || path[0] == '\0') {
        return nullptr;
    }

    auto* typeface = new skn_typeface_t();
#if defined(SKIANATIVE_WITH_SKIA)
    auto font_mgr = SkFontMgr_New_CoreText(nullptr);
    if (font_mgr) {
        typeface->typeface = font_mgr->makeFromFile(path, 0);
    }

    if (!typeface->typeface) {
        delete typeface;
        return nullptr;
    }
#endif
    return typeface;
}

SKN_EXPORT void skn_typeface_destroy(skn_typeface_t* typeface) {
    delete typeface;
}

SKN_EXPORT skn_glyph_run_t* skn_glyph_run_create_with_options(skn_typeface_t* typeface, float em_size, const uint16_t* glyph_indices, const float* positions_xy, int glyph_count, float baseline_x, float baseline_y, uint32_t text_options) {
    if (typeface == nullptr || glyph_indices == nullptr || positions_xy == nullptr || glyph_count <= 0 || em_size <= 0) {
        return nullptr;
    }

    auto* glyph_run = new skn_glyph_run_t();
    glyph_run->em_size = em_size;
    glyph_run->baseline_x = baseline_x;
    glyph_run->baseline_y = baseline_y;
#if defined(SKIANATIVE_WITH_SKIA)
    glyph_run->typeface = typeface->typeface;
    if (!glyph_run->typeface) {
        delete glyph_run;
        return nullptr;
    }

    SkFont font(glyph_run->typeface, em_size);
    apply_text_font_options(font, text_options);
    glyph_run->font = font;
    glyph_run->glyphs.resize(static_cast<size_t>(glyph_count));
    glyph_run->positions.resize(static_cast<size_t>(glyph_count));

    SkTextBlobBuilder builder;
    const auto& run = builder.allocRunPos(font, glyph_count);
    std::memcpy(run.glyphs, glyph_indices, sizeof(uint16_t) * static_cast<size_t>(glyph_count));
    std::memcpy(run.points(), positions_xy, sizeof(float) * 2 * static_cast<size_t>(glyph_count));
    for (int i = 0; i < glyph_count; ++i) {
        glyph_run->glyphs[static_cast<size_t>(i)] = glyph_indices[i];
        glyph_run->positions[static_cast<size_t>(i)] = SkPoint::Make(positions_xy[i * 2], positions_xy[i * 2 + 1]);
    }
    glyph_run->text_blob = builder.make();

    if (!glyph_run->text_blob) {
        delete glyph_run;
        return nullptr;
    }
#endif
    return glyph_run;
}

SKN_EXPORT skn_glyph_run_t* skn_glyph_run_create(skn_typeface_t* typeface, float em_size, const uint16_t* glyph_indices, const float* positions_xy, int glyph_count, float baseline_x, float baseline_y) {
    return skn_glyph_run_create_with_options(
        typeface,
        em_size,
        glyph_indices,
        positions_xy,
        glyph_count,
        baseline_x,
        baseline_y,
        3u | (3u << kTextHintingShift) | kTextSubpixelFlag | kTextBaselineSnapFlag);
}

SKN_EXPORT int skn_glyph_run_get_intersections(skn_glyph_run_t* glyph_run, float lower_limit, float upper_limit, float* values, int value_capacity) {
    if (glyph_run == nullptr) {
        return 0;
    }

#if defined(SKIANATIVE_WITH_SKIA)
    if (!glyph_run->text_blob) {
        return 0;
    }

    SkScalar bounds[2] = { lower_limit, upper_limit };
    const auto required = glyph_run->text_blob->getIntercepts(bounds, nullptr, nullptr);
    if (values != nullptr && value_capacity > 0) {
        if (value_capacity < required) {
            return required;
        }

        glyph_run->text_blob->getIntercepts(bounds, values, nullptr);
        return required;
    }

    return required;
#else
    (void)lower_limit;
    (void)upper_limit;
    (void)values;
    (void)value_capacity;
    return 0;
#endif
}

SKN_EXPORT void skn_glyph_run_destroy(skn_glyph_run_t* glyph_run) {
    delete glyph_run;
}

SKN_EXPORT skn_path_t* skn_path_create(const skn_path_command_t* commands, int command_count, skn_path_fill_rule_t fill_rule) {
    auto* path = new skn_path_t();
#if defined(SKIANATIVE_WITH_SKIA)
    path->path = build_path(commands, command_count, fill_rule);
#else
    (void)commands;
    (void)command_count;
    (void)fill_rule;
#endif
    return path;
}

SKN_EXPORT skn_path_t* skn_path_create_rect(float x, float y, float width, float height, skn_path_fill_rule_t fill_rule) {
    auto* path = new skn_path_t();
#if defined(SKIANATIVE_WITH_SKIA)
    path->path = build_rect_path(x, y, width, height, fill_rule);
#else
    (void)x;
    (void)y;
    (void)width;
    (void)height;
    (void)fill_rule;
#endif
    return path;
}

SKN_EXPORT skn_path_t* skn_path_create_ellipse(float x, float y, float width, float height, skn_path_fill_rule_t fill_rule) {
    auto* path = new skn_path_t();
#if defined(SKIANATIVE_WITH_SKIA)
    path->path = build_ellipse_path(x, y, width, height, fill_rule);
#else
    (void)x;
    (void)y;
    (void)width;
    (void)height;
    (void)fill_rule;
#endif
    return path;
}

SKN_EXPORT skn_path_t* skn_path_create_group(skn_path_t* const* paths, int path_count, skn_path_fill_rule_t fill_rule) {
    auto* path = new skn_path_t();
#if defined(SKIANATIVE_WITH_SKIA)
    path->path = build_group_path(paths, path_count, fill_rule);
#else
    (void)paths;
    (void)path_count;
    (void)fill_rule;
#endif
    return path;
}

SKN_EXPORT skn_path_t* skn_path_create_combined(skn_path_t* first, skn_path_t* second, skn_path_op_t op, skn_path_fill_rule_t fill_rule) {
    if (first == nullptr || second == nullptr) {
        return nullptr;
    }

    auto* path = new skn_path_t();
#if defined(SKIANATIVE_WITH_SKIA)
    if (auto result = Op(first->path, second->path, to_sk_path_op(op))) {
        path->path = *result;
        return path;
    }

    delete path;
    return nullptr;
#else
    (void)op;
    (void)fill_rule;
    return path;
#endif
}

SKN_EXPORT skn_path_t* skn_path_create_transformed(skn_path_t* source, const skn_matrix_t* transform) {
    if (source == nullptr || transform == nullptr) {
        return nullptr;
    }

    auto* path = new skn_path_t();
#if defined(SKIANATIVE_WITH_SKIA)
    path->path = source->path.makeTransform(to_sk_matrix(*transform));
#else
    (void)source;
    (void)transform;
#endif
    return path;
}

SKN_EXPORT skn_path_t* skn_path_create_from_glyph_run(skn_glyph_run_t* glyph_run) {
    if (glyph_run == nullptr) {
        return nullptr;
    }

    auto* path = new skn_path_t();
#if defined(SKIANATIVE_WITH_SKIA)
    if (!glyph_run->typeface || glyph_run->glyphs.empty()) {
        delete path;
        return nullptr;
    }

    SkPathBuilder builder(SkPathFillType::kWinding);
    for (size_t i = 0; i < glyph_run->glyphs.size(); ++i) {
        auto glyph_path = glyph_run->font.getPath(glyph_run->glyphs[i]);
        if (!glyph_path || glyph_path->isEmpty()) {
            continue;
        }

        const auto& position = glyph_run->positions[i];
        auto matrix = SkMatrix::Translate(glyph_run->baseline_x + position.x(), glyph_run->baseline_y + position.y());
        builder.addPath(*glyph_path, matrix);
    }

    path->path = builder.detach();
#endif
    return path;
}

SKN_EXPORT int skn_path_get_bounds(skn_path_t* path, float* x, float* y, float* width, float* height) {
    if (path == nullptr || x == nullptr || y == nullptr || width == nullptr || height == nullptr) {
        return 0;
    }

#if defined(SKIANATIVE_WITH_SKIA)
    auto bounds = path->path.computeTightBounds();
    if (!bounds.isFinite()) {
        return 0;
    }

    *x = bounds.x();
    *y = bounds.y();
    *width = bounds.width();
    *height = bounds.height();
    return 1;
#else
    *x = 0;
    *y = 0;
    *width = 0;
    *height = 0;
    return 1;
#endif
}

SKN_EXPORT int skn_path_contains(skn_path_t* path, float x, float y) {
    if (path == nullptr) {
        return 0;
    }

#if defined(SKIANATIVE_WITH_SKIA)
    return path->path.contains(x, y) ? 1 : 0;
#else
    (void)x;
    (void)y;
    return 0;
#endif
}

SKN_EXPORT float skn_path_get_contour_length(skn_path_t* path) {
    if (path == nullptr) {
        return 0;
    }

#if defined(SKIANATIVE_WITH_SKIA)
    SkPathMeasure measure(path->path, false);
    return measure.getLength();
#else
    return 0;
#endif
}

SKN_EXPORT int skn_path_get_point_at_distance(skn_path_t* path, float distance, float* x, float* y) {
    if (path == nullptr || x == nullptr || y == nullptr) {
        return 0;
    }

#if defined(SKIANATIVE_WITH_SKIA)
    SkPathMeasure measure(path->path, false);
    SkPoint point;
    SkVector tangent;
    if (!measure.getPosTan(distance, &point, &tangent)) {
        return 0;
    }

    *x = point.x();
    *y = point.y();
    return 1;
#else
    (void)distance;
    *x = 0;
    *y = 0;
    return 0;
#endif
}

SKN_EXPORT int skn_path_get_point_and_tangent_at_distance(skn_path_t* path, float distance, float* x, float* y, float* tangent_x, float* tangent_y) {
    if (path == nullptr || x == nullptr || y == nullptr || tangent_x == nullptr || tangent_y == nullptr) {
        return 0;
    }

#if defined(SKIANATIVE_WITH_SKIA)
    SkPathMeasure measure(path->path, false);
    SkPoint point;
    SkVector tangent;
    if (!measure.getPosTan(distance, &point, &tangent)) {
        return 0;
    }

    *x = point.x();
    *y = point.y();
    *tangent_x = tangent.x();
    *tangent_y = tangent.y();
    return 1;
#else
    (void)distance;
    *x = 0;
    *y = 0;
    *tangent_x = 0;
    *tangent_y = 0;
    return 0;
#endif
}

SKN_EXPORT skn_path_t* skn_path_create_segment(skn_path_t* path, float start_distance, float stop_distance, int start_with_move_to) {
    if (path == nullptr) {
        return nullptr;
    }

#if defined(SKIANATIVE_WITH_SKIA)
    SkPathMeasure measure(path->path, false);
    SkPathBuilder builder(path->path.getFillType());
    if (!measure.getSegment(start_distance, stop_distance, &builder, start_with_move_to != 0)) {
        return nullptr;
    }

    auto* segment = new skn_path_t();
    segment->path = builder.detach();
    return segment;
#else
    (void)start_distance;
    (void)stop_distance;
    (void)start_with_move_to;
    return new skn_path_t();
#endif
}

SKN_EXPORT skn_path_t* skn_path_create_stroked(skn_path_t* path, float stroke_width, skn_stroke_t* stroke) {
    if (path == nullptr || stroke_width <= 0) {
        return nullptr;
    }

#if defined(SKIANATIVE_WITH_SKIA)
    auto paint = make_stroke_path_paint(stroke_width, stroke);
    SkPathBuilder builder(SkPathFillType::kWinding);
    if (!skpathutils::FillPathWithPaint(path->path, paint, &builder)) {
        return nullptr;
    }

    auto* stroked = new skn_path_t();
    stroked->path = builder.detach();
    stroked->path.setFillType(SkPathFillType::kWinding);
    return stroked;
#else
    (void)stroke;
    return new skn_path_t();
#endif
}

SKN_EXPORT void skn_path_destroy(skn_path_t* path) {
    delete path;
}

static skn_shader_t* create_linear_shader(float x0, float y0, float x1, float y1, const skn_gradient_stop_t* stops, int stop_count, skn_gradient_spread_method_t spread_method, const skn_matrix_t* local_matrix) {
    if (stop_count <= 0 || stops == nullptr) {
        return nullptr;
    }

    auto* shader = new skn_shader_t();
#if defined(SKIANATIVE_WITH_SKIA)
    std::vector<SkColor4f> colors;
    std::vector<float> positions;
    auto gradient = build_gradient(stops, stop_count, spread_method, colors, positions);
    SkPoint points[2] = {SkPoint::Make(x0, y0), SkPoint::Make(x1, y1)};
    SkMatrix matrix;
    const SkMatrix* matrix_ptr = nullptr;
    if (local_matrix != nullptr) {
        matrix = to_sk_matrix(*local_matrix);
        matrix_ptr = &matrix;
    }

    shader->shader = SkShaders::LinearGradient(points, gradient, matrix_ptr);
    if (!shader->shader) {
        delete shader;
        return nullptr;
    }
#else
    (void)x0;
    (void)y0;
    (void)x1;
    (void)y1;
    (void)spread_method;
    (void)local_matrix;
#endif
    return shader;
}

SKN_EXPORT skn_shader_t* skn_shader_create_linear(float x0, float y0, float x1, float y1, const skn_gradient_stop_t* stops, int stop_count, skn_gradient_spread_method_t spread_method) {
    return create_linear_shader(x0, y0, x1, y1, stops, stop_count, spread_method, nullptr);
}

SKN_EXPORT skn_shader_t* skn_shader_create_linear_with_matrix(float x0, float y0, float x1, float y1, const skn_gradient_stop_t* stops, int stop_count, skn_gradient_spread_method_t spread_method, const skn_matrix_t* local_matrix) {
    return create_linear_shader(x0, y0, x1, y1, stops, stop_count, spread_method, local_matrix);
}

static skn_shader_t* create_radial_shader(float center_x, float center_y, float origin_x, float origin_y, float radius, const skn_gradient_stop_t* stops, int stop_count, skn_gradient_spread_method_t spread_method, const skn_matrix_t* local_matrix) {
    if (stop_count <= 0 || stops == nullptr || radius <= 0) {
        return nullptr;
    }

    auto* shader = new skn_shader_t();
#if defined(SKIANATIVE_WITH_SKIA)
    std::vector<skn_gradient_stop_t> reversed_stops;
    const auto origin_moved = std::abs(center_x - origin_x) > 0.001f || std::abs(center_y - origin_y) > 0.001f;
    const auto reverse = origin_moved && std::abs(stops[stop_count - 1].offset - 1.0f) <= 0.001f;
    if (reverse) {
        reversed_stops.resize(static_cast<size_t>(stop_count));
        for (int i = 0; i < stop_count; ++i) {
            auto offset = 1.0f - stops[i].offset;
            if (std::abs(offset) <= 0.000001f) {
                offset = 0.0f;
            }

            reversed_stops[static_cast<size_t>(stop_count - 1 - i)] = skn_gradient_stop_t{offset, stops[i].color};
        }

        stops = reversed_stops.data();
    }

    std::vector<SkColor4f> colors;
    std::vector<float> positions;
    auto gradient = build_gradient(stops, stop_count, spread_method, colors, positions);
    auto start = SkPoint::Make(origin_x, origin_y);
    auto end = SkPoint::Make(center_x, center_y);
    auto start_radius = 0.001f;
    auto end_radius = radius;
    if (reverse) {
        start = SkPoint::Make(center_x, center_y);
        end = SkPoint::Make(origin_x, origin_y);
        start_radius = radius;
        end_radius = 0.0f;
    }

    SkMatrix matrix;
    const SkMatrix* matrix_ptr = nullptr;
    if (local_matrix != nullptr) {
        matrix = to_sk_matrix(*local_matrix);
        matrix_ptr = &matrix;
    }

    if (origin_moved) {
        auto conical = SkShaders::TwoPointConicalGradient(start, start_radius, end, end_radius, gradient, matrix_ptr);
        shader->shader = SkShaders::Blend(
            SkBlendMode::kSrcOver,
            SkShaders::Color(colors.empty() ? SkColor4f{0, 0, 0, 0} : colors[0], SkColorSpace::MakeSRGB()),
            conical);
    } else {
        shader->shader = SkShaders::RadialGradient(SkPoint::Make(center_x, center_y), radius, gradient, matrix_ptr);
    }

    if (!shader->shader) {
        delete shader;
        return nullptr;
    }
#else
    (void)center_x;
    (void)center_y;
    (void)origin_x;
    (void)origin_y;
    (void)spread_method;
    (void)local_matrix;
#endif
    return shader;
}

SKN_EXPORT skn_shader_t* skn_shader_create_radial(float center_x, float center_y, float origin_x, float origin_y, float radius, const skn_gradient_stop_t* stops, int stop_count, skn_gradient_spread_method_t spread_method) {
    return create_radial_shader(center_x, center_y, origin_x, origin_y, radius, stops, stop_count, spread_method, nullptr);
}

SKN_EXPORT skn_shader_t* skn_shader_create_radial_with_matrix(float center_x, float center_y, float origin_x, float origin_y, float radius, const skn_gradient_stop_t* stops, int stop_count, skn_gradient_spread_method_t spread_method, const skn_matrix_t* local_matrix) {
    return create_radial_shader(center_x, center_y, origin_x, origin_y, radius, stops, stop_count, spread_method, local_matrix);
}

SKN_EXPORT skn_shader_t* skn_shader_create_sweep(float center_x, float center_y, const skn_gradient_stop_t* stops, int stop_count, skn_gradient_spread_method_t spread_method, const skn_matrix_t* local_matrix) {
    if (stop_count <= 0 || stops == nullptr) {
        return nullptr;
    }

    auto* shader = new skn_shader_t();
#if defined(SKIANATIVE_WITH_SKIA)
    std::vector<SkColor4f> colors;
    std::vector<float> positions;
    auto gradient = build_gradient(stops, stop_count, spread_method, colors, positions);
    SkMatrix matrix;
    const SkMatrix* matrix_ptr = nullptr;
    if (local_matrix != nullptr) {
        matrix = to_sk_matrix(*local_matrix);
        matrix_ptr = &matrix;
    }

    shader->shader = SkShaders::SweepGradient(SkPoint::Make(center_x, center_y), gradient, matrix_ptr);
    if (!shader->shader) {
        delete shader;
        return nullptr;
    }
#else
    (void)center_x;
    (void)center_y;
    (void)spread_method;
    (void)local_matrix;
#endif
    return shader;
}

SKN_EXPORT skn_shader_t* skn_shader_create_bitmap(skn_bitmap_t* bitmap, skn_tile_mode_t tile_x, skn_tile_mode_t tile_y, const skn_matrix_t* local_matrix) {
    auto image = bitmap_image(bitmap);
    if (!image) {
        return nullptr;
    }

    const auto matrix = local_matrix != nullptr ? to_sk_matrix(*local_matrix) : SkMatrix::I();
    auto* shader = new skn_shader_t();
    shader->shader = image->makeShader(
        to_sk_tile_mode(tile_x),
        to_sk_tile_mode(tile_y),
        bitmap_sampling_options(2u),
        matrix);

    if (!shader->shader) {
        delete shader;
        return nullptr;
    }

    return shader;
}

SKN_EXPORT void skn_shader_destroy(skn_shader_t* shader) {
    delete shader;
}

SKN_EXPORT skn_stroke_t* skn_stroke_create(skn_stroke_cap_t cap, skn_stroke_join_t join, float miter_limit, const float* dashes, int dash_count, float dash_offset) {
    auto* stroke = new skn_stroke_t();
#if defined(SKIANATIVE_WITH_SKIA)
    stroke->cap = to_sk_cap(cap);
    stroke->join = to_sk_join(join);
    stroke->miter_limit = miter_limit;
    stroke->dash_offset = dash_offset;

    if (dashes != nullptr && dash_count > 0) {
        const auto count = dash_count % 2 == 0 ? dash_count : dash_count * 2;
        stroke->dashes.reserve(static_cast<size_t>(count));
        for (int i = 0; i < count; ++i) {
            stroke->dashes.push_back(std::max(static_cast<SkScalar>(dashes[i % dash_count]), 0.0f));
        }

        stroke->path_effect = SkDashPathEffect::Make(SkSpan<const SkScalar>(stroke->dashes.data(), stroke->dashes.size()), dash_offset);
    }
#else
    (void)cap;
    (void)join;
    (void)miter_limit;
    (void)dashes;
    (void)dash_count;
    (void)dash_offset;
#endif
    return stroke;
}

SKN_EXPORT void skn_stroke_destroy(skn_stroke_t* stroke) {
    delete stroke;
}

} // extern "C"
