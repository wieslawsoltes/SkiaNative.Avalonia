#import <AppKit/AppKit.h>
#import <CoreText/CoreText.h>
#import <Metal/Metal.h>
#import <MetalKit/MetalKit.h>
#import <UniformTypeIdentifiers/UniformTypeIdentifiers.h>

#include "include/codec/SkCodec.h"
#include "include/core/SkBlender.h"
#include "include/core/SkCanvas.h"
#include "include/core/SkColorSpace.h"
#include "include/core/SkData.h"
#include "include/core/SkFont.h"
#include "include/core/SkFontMgr.h"
#include "include/core/SkFontStyle.h"
#include "include/core/SkMesh.h"
#include "include/core/SkPaint.h"
#include "include/core/SkPath.h"
#include "include/core/SkRect.h"
#include "include/core/SkString.h"
#include "include/core/SkSurface.h"
#include "include/core/SkTypeface.h"
#include "include/gpu/ganesh/GrBackendSurface.h"
#include "include/gpu/ganesh/GrContextOptions.h"
#include "include/gpu/ganesh/GrDirectContext.h"
#include "include/gpu/ganesh/SkMeshGanesh.h"
#include "include/gpu/ganesh/SkSurfaceGanesh.h"
#include "include/gpu/ganesh/mtl/GrMtlBackendContext.h"
#include "include/gpu/ganesh/mtl/GrMtlBackendSurface.h"
#include "include/gpu/ganesh/mtl/GrMtlDirectContext.h"
#include "include/gpu/ganesh/mtl/GrMtlTypes.h"
#include "include/ports/SkFontMgr_mac_ct.h"
#include "include/ports/SkTypeface_mac.h"

#include <zlib.h>

#include <algorithm>
#include <array>
#include <cctype>
#include <cmath>
#include <cstdint>
#include <cstdlib>
#include <cstring>
#include <filesystem>
#include <fstream>
#include <functional>
#include <iomanip>
#include <limits>
#include <memory>
#include <optional>
#include <sstream>
#include <stdexcept>
#include <string>
#include <string_view>
#include <unordered_map>
#include <utility>
#include <vector>

namespace {

constexpr float kPi = 3.14159265358979323846f;
constexpr float kTau = 6.28318530717958647692f;
constexpr float kSphericalHarmonicsC0 = 0.28209479177387814f;

struct Vec2 {
    float x = 0.0f;
    float y = 0.0f;
};

struct Vec3 {
    float x = 0.0f;
    float y = 0.0f;
    float z = 0.0f;
};

struct Color4 {
    float r = 0.70f;
    float g = 0.78f;
    float b = 0.82f;
    float a = 1.0f;
};

struct MeshTriangle {
    Vec3 p0;
    Vec3 p1;
    Vec3 p2;
    Vec3 normal;
    Color4 color;
};

struct GaussianSplat {
    Vec3 position;
    Vec3 axis0;
    Vec3 axis1;
    Vec3 axis2;
    Color4 color;
};

enum class SceneKind {
    Mesh,
    Splats,
};

struct LoadedScene {
    SceneKind kind = SceneKind::Mesh;
    std::string name = "Procedural torus";
    std::string source = "procedural";
    std::vector<MeshTriangle> triangles;
    std::vector<GaussianSplat> splats;
};

struct FrameStats {
    double fps = 0.0;
    double frameMs = 0.0;
    int gpuResourceCount = 0;
    uint64_t gpuResourceBytes = 0;
    uint64_t gpuResourceLimit = 0;
};

struct RenderStats {
    std::string mode = "initializing";
    size_t primitiveCount = 0;
    size_t vertexCount = 0;
    size_t vertexBytes = 0;
    int drawMeshCalls = 0;
};

enum class DragMode {
    None,
    Orbit,
    Pan,
};

struct ScreenVertex {
    float x;
    float y;
    uint32_t color;
};

struct SplatVertex {
    float x;
    float y;
    float localX;
    float localY;
    uint32_t color;
    uint32_t padding;
};

static_assert(sizeof(ScreenVertex) == 12);
static_assert(sizeof(SplatVertex) == 24);

float Clamp(float value, float minValue, float maxValue) {
    return std::max(minValue, std::min(maxValue, value));
}

float Clamp01(float value) {
    return Clamp(value, 0.0f, 1.0f);
}

uint8_t ToByte(float value) {
    return static_cast<uint8_t>(std::lround(Clamp01(value) * 255.0f));
}

uint32_t PackRgba(Color4 color) {
    const uint32_t r = ToByte(color.r);
    const uint32_t g = ToByte(color.g);
    const uint32_t b = ToByte(color.b);
    const uint32_t a = ToByte(color.a);
    return r | (g << 8) | (b << 16) | (a << 24);
}

Vec3 operator+(Vec3 a, Vec3 b) {
    return {a.x + b.x, a.y + b.y, a.z + b.z};
}

Vec3 operator-(Vec3 a, Vec3 b) {
    return {a.x - b.x, a.y - b.y, a.z - b.z};
}

Vec3 operator*(Vec3 a, float value) {
    return {a.x * value, a.y * value, a.z * value};
}

Vec3 operator/(Vec3 a, float value) {
    return {a.x / value, a.y / value, a.z / value};
}

float Dot(Vec3 a, Vec3 b) {
    return a.x * b.x + a.y * b.y + a.z * b.z;
}

Vec3 Cross(Vec3 a, Vec3 b) {
    return {
        a.y * b.z - a.z * b.y,
        a.z * b.x - a.x * b.z,
        a.x * b.y - a.y * b.x,
    };
}

float Length(Vec3 value) {
    return std::sqrt(Dot(value, value));
}

Vec3 Normalize(Vec3 value, Vec3 fallback = {0.0f, 0.0f, 1.0f}) {
    const float length = Length(value);
    if (length <= 0.000001f) {
        return fallback;
    }
    return value / length;
}

Vec3 RotateYThenX(Vec3 value, float yaw, float pitch) {
    const float cy = std::cos(yaw);
    const float sy = std::sin(yaw);
    const Vec3 yRotated = {
        cy * value.x + sy * value.z,
        value.y,
        -sy * value.x + cy * value.z,
    };

    const float cp = std::cos(pitch);
    const float sp = std::sin(pitch);
    return {
        yRotated.x,
        cp * yRotated.y - sp * yRotated.z,
        sp * yRotated.y + cp * yRotated.z,
    };
}

std::string ToLower(std::string value) {
    std::transform(value.begin(), value.end(), value.begin(), [](unsigned char c) {
        return static_cast<char>(std::tolower(c));
    });
    return value;
}

std::string Trim(std::string_view text) {
    size_t start = 0;
    while (start < text.size() && std::isspace(static_cast<unsigned char>(text[start]))) {
        ++start;
    }
    size_t end = text.size();
    while (end > start && std::isspace(static_cast<unsigned char>(text[end - 1]))) {
        --end;
    }
    return std::string(text.substr(start, end - start));
}

std::vector<std::string> SplitWhitespace(std::string_view line) {
    std::vector<std::string> parts;
    std::istringstream stream{std::string(line)};
    std::string token;
    while (stream >> token) {
        parts.push_back(token);
    }
    return parts;
}

std::string StripInlineComment(std::string_view line) {
    const size_t index = line.find('#');
    if (index == std::string_view::npos) {
        return std::string(line);
    }
    return std::string(line.substr(0, index));
}

std::string ReadTextFile(const std::filesystem::path& path) {
    std::ifstream input(path, std::ios::binary);
    if (!input) {
        throw std::runtime_error("Could not open " + path.string());
    }
    std::ostringstream buffer;
    buffer << input.rdbuf();
    return buffer.str();
}

std::vector<uint8_t> ReadBinaryFile(const std::filesystem::path& path) {
    std::ifstream input(path, std::ios::binary);
    if (!input) {
        throw std::runtime_error("Could not open " + path.string());
    }
    input.seekg(0, std::ios::end);
    const auto size = input.tellg();
    input.seekg(0, std::ios::beg);
    std::vector<uint8_t> bytes(static_cast<size_t>(std::max<std::streamoff>(0, size)));
    if (!bytes.empty()) {
        input.read(reinterpret_cast<char*>(bytes.data()), static_cast<std::streamsize>(bytes.size()));
    }
    return bytes;
}

std::string FileNameForDisplay(const std::filesystem::path& path) {
    const auto name = path.filename().string();
    return name.empty() ? path.string() : name;
}

std::string FormatFixed(double value, int decimals) {
    std::ostringstream stream;
    stream << std::fixed << std::setprecision(decimals) << value;
    return stream.str();
}

std::string FormatMiB(uint64_t bytes) {
    return FormatFixed(static_cast<double>(bytes) / 1024.0 / 1024.0, 1) + " MiB";
}

sk_sp<SkTypeface> HudTypeface() {
    static sk_sp<SkTypeface> typeface = [] {
        sk_sp<SkTypeface> selected;
        sk_sp<SkFontMgr> fontManager = SkFontMgr_New_CoreText(nullptr);
        if (fontManager) {
            selected = fontManager->matchFamilyStyle("Helvetica Neue", SkFontStyle::Normal());
            if (!selected) {
                selected = fontManager->matchFamilyStyle("Helvetica", SkFontStyle::Normal());
            }
            if (!selected) {
                selected = fontManager->legacyMakeTypeface(nullptr, SkFontStyle::Normal());
            }
        }
        if (!selected) {
            CTFontRef systemFont = CTFontCreateUIFontForLanguage(kCTFontUIFontSystem, 13.0, nullptr);
            if (systemFont) {
                selected = SkMakeTypefaceFromCTFont(systemFont);
                CFRelease(systemFont);
            }
        }
        return selected;
    }();

    return typeface;
}

void NormalizeMesh(std::vector<MeshTriangle>& triangles) {
    if (triangles.empty()) {
        return;
    }

    Vec3 minPoint = triangles.front().p0;
    Vec3 maxPoint = triangles.front().p0;
    auto expand = [&](Vec3 p) {
        minPoint.x = std::min(minPoint.x, p.x);
        minPoint.y = std::min(minPoint.y, p.y);
        minPoint.z = std::min(minPoint.z, p.z);
        maxPoint.x = std::max(maxPoint.x, p.x);
        maxPoint.y = std::max(maxPoint.y, p.y);
        maxPoint.z = std::max(maxPoint.z, p.z);
    };

    for (const auto& triangle : triangles) {
        expand(triangle.p0);
        expand(triangle.p1);
        expand(triangle.p2);
    }

    const Vec3 center = (minPoint + maxPoint) * 0.5f;
    float radius = 0.001f;
    for (const auto& triangle : triangles) {
        radius = std::max(radius, Length(triangle.p0 - center));
        radius = std::max(radius, Length(triangle.p1 - center));
        radius = std::max(radius, Length(triangle.p2 - center));
    }

    const float scale = radius <= 0.0001f ? 1.0f : 1.65f / radius;
    for (auto& triangle : triangles) {
        triangle.p0 = (triangle.p0 - center) * scale;
        triangle.p1 = (triangle.p1 - center) * scale;
        triangle.p2 = (triangle.p2 - center) * scale;
        triangle.normal = Normalize(triangle.normal);
        if (Length(triangle.normal) <= 0.000001f) {
            triangle.normal = Normalize(Cross(triangle.p1 - triangle.p0, triangle.p2 - triangle.p0));
        }
    }
}

void NormalizeSplats(std::vector<GaussianSplat>& splats) {
    if (splats.empty()) {
        return;
    }

    Vec3 minPoint = splats.front().position;
    Vec3 maxPoint = splats.front().position;
    for (const auto& splat : splats) {
        minPoint.x = std::min(minPoint.x, splat.position.x);
        minPoint.y = std::min(minPoint.y, splat.position.y);
        minPoint.z = std::min(minPoint.z, splat.position.z);
        maxPoint.x = std::max(maxPoint.x, splat.position.x);
        maxPoint.y = std::max(maxPoint.y, splat.position.y);
        maxPoint.z = std::max(maxPoint.z, splat.position.z);
    }

    const Vec3 center = (minPoint + maxPoint) * 0.5f;
    float radius = 0.001f;
    for (const auto& splat : splats) {
        radius = std::max(radius, Length(splat.position - center));
    }

    const float scale = radius <= 0.0001f ? 1.0f : 1.65f / radius;
    for (auto& splat : splats) {
        splat.position = (splat.position - center) * scale;
        splat.axis0 = splat.axis0 * scale;
        splat.axis1 = splat.axis1 * scale;
        splat.axis2 = splat.axis2 * scale;
    }
}

LoadedScene CreateProceduralTorus() {
    LoadedScene scene;
    scene.kind = SceneKind::Mesh;
    scene.name = "Procedural torus";
    scene.source = "built-in OBJ-style mesh";

    constexpr int majorSegments = 56;
    constexpr int minorSegments = 18;
    constexpr float majorRadius = 1.05f;
    constexpr float minorRadius = 0.34f;

    std::vector<Vec3> positions;
    positions.reserve(majorSegments * minorSegments);
    for (int y = 0; y < minorSegments; ++y) {
        const float v = static_cast<float>(y) / static_cast<float>(minorSegments);
        const float minor = v * kTau;
        const float ringRadius = majorRadius + std::cos(minor) * minorRadius;
        const float py = std::sin(minor) * minorRadius;
        for (int x = 0; x < majorSegments; ++x) {
            const float u = static_cast<float>(x) / static_cast<float>(majorSegments);
            const float major = u * kTau;
            positions.push_back({std::cos(major) * ringRadius, py, std::sin(major) * ringRadius});
        }
    }

    auto addTriangle = [&](int a, int b, int c, Color4 color) {
        MeshTriangle triangle;
        triangle.p0 = positions[static_cast<size_t>(a)];
        triangle.p1 = positions[static_cast<size_t>(b)];
        triangle.p2 = positions[static_cast<size_t>(c)];
        triangle.normal = Normalize(Cross(triangle.p1 - triangle.p0, triangle.p2 - triangle.p0));
        triangle.color = color;
        scene.triangles.push_back(triangle);
    };

    for (int y = 0; y < minorSegments; ++y) {
        const int y1 = (y + 1) % minorSegments;
        for (int x = 0; x < majorSegments; ++x) {
            const int x1 = (x + 1) % majorSegments;
            const int a = y * majorSegments + x;
            const int b = y * majorSegments + x1;
            const int c = y1 * majorSegments + x1;
            const int d = y1 * majorSegments + x;
            const float hue = static_cast<float>(x) / static_cast<float>(majorSegments);
            Color4 color = {0.38f + 0.32f * std::sin(hue * kTau + 0.6f), 0.64f + 0.22f * std::sin(hue * kTau + 2.2f), 0.80f + 0.18f * std::sin(hue * kTau + 4.0f), 1.0f};
            color.r = Clamp01(color.r);
            color.g = Clamp01(color.g);
            color.b = Clamp01(color.b);
            addTriangle(a, b, c, color);
            addTriangle(a, c, d, color);
        }
    }

    NormalizeMesh(scene.triangles);
    return scene;
}

struct ObjMaterial {
    Color4 color = {0.70f, 0.78f, 0.82f, 1.0f};
};

std::unordered_map<std::string, ObjMaterial> LoadMtlFile(const std::filesystem::path& path) {
    std::unordered_map<std::string, ObjMaterial> materials;
    std::ifstream input(path);
    if (!input) {
        return materials;
    }

    std::string currentName;
    ObjMaterial current;
    bool hasCurrent = false;
    auto commit = [&]() {
        if (hasCurrent && !currentName.empty()) {
            materials[ToLower(currentName)] = current;
        }
    };

    std::string rawLine;
    while (std::getline(input, rawLine)) {
        const auto line = Trim(StripInlineComment(rawLine));
        if (line.empty()) {
            continue;
        }
        const auto parts = SplitWhitespace(line);
        if (parts.empty()) {
            continue;
        }
        if (parts[0] == "newmtl" && parts.size() >= 2) {
            commit();
            currentName = line.substr(line.find(parts[1]));
            current = ObjMaterial{};
            hasCurrent = true;
        } else if (parts[0] == "Kd" && parts.size() >= 4) {
            current.color.r = Clamp01(std::stof(parts[1]));
            current.color.g = Clamp01(std::stof(parts[2]));
            current.color.b = Clamp01(std::stof(parts[3]));
        } else if (parts[0] == "d" && parts.size() >= 2) {
            current.color.a = Clamp01(std::stof(parts[1]));
        } else if (parts[0] == "Tr" && parts.size() >= 2) {
            current.color.a = Clamp01(1.0f - std::stof(parts[1]));
        }
    }
    commit();
    return materials;
}

std::optional<int> ParseObjIndex(const std::string& token, int count) {
    if (token.empty()) {
        return std::nullopt;
    }
    const int value = std::stoi(token);
    if (value == 0) {
        return std::nullopt;
    }
    const int resolved = value < 0 ? count + value : value - 1;
    if (resolved < 0 || resolved >= count) {
        return std::nullopt;
    }
    return resolved;
}

struct ObjCorner {
    int positionIndex = -1;
    Vec3 normal = {0.0f, 0.0f, 1.0f};
    bool hasNormal = false;
};

std::optional<ObjCorner> ParseObjCorner(const std::string& token, int positionCount, const std::vector<Vec3>& normals) {
    std::array<std::string, 3> pieces;
    size_t pieceIndex = 0;
    size_t start = 0;
    while (pieceIndex < pieces.size()) {
        const size_t slash = token.find('/', start);
        pieces[pieceIndex++] = token.substr(start, slash == std::string::npos ? std::string::npos : slash - start);
        if (slash == std::string::npos) {
            break;
        }
        start = slash + 1;
    }

    auto positionIndex = ParseObjIndex(pieces[0], positionCount);
    if (!positionIndex) {
        return std::nullopt;
    }

    ObjCorner corner;
    corner.positionIndex = *positionIndex;
    if (!pieces[2].empty() && !normals.empty()) {
        auto normalIndex = ParseObjIndex(pieces[2], static_cast<int>(normals.size()));
        if (normalIndex) {
            corner.normal = normals[static_cast<size_t>(*normalIndex)];
            corner.hasNormal = true;
        }
    }
    return corner;
}

LoadedScene LoadObj(const std::filesystem::path& path) {
    LoadedScene scene;
    scene.kind = SceneKind::Mesh;
    scene.name = FileNameForDisplay(path);
    scene.source = "OBJ";

    const std::string text = ReadTextFile(path);
    const auto baseDirectory = path.parent_path();
    std::vector<Vec3> positions;
    std::vector<Vec3> normals;
    std::unordered_map<std::string, ObjMaterial> materials;
    ObjMaterial currentMaterial;

    std::istringstream input(text);
    std::string rawLine;
    std::string pending;
    while (std::getline(input, rawLine)) {
        if (!rawLine.empty() && rawLine.back() == '\r') {
            rawLine.pop_back();
        }
        if (!rawLine.empty() && rawLine.back() == '\\') {
            pending += rawLine.substr(0, rawLine.size() - 1) + " ";
            continue;
        }

        const std::string logicalLine = pending + rawLine;
        pending.clear();
        const std::string line = Trim(StripInlineComment(logicalLine));
        if (line.empty()) {
            continue;
        }

        const auto parts = SplitWhitespace(line);
        if (parts.empty()) {
            continue;
        }

        if (parts[0] == "v" && parts.size() >= 4) {
            positions.push_back({std::stof(parts[1]), std::stof(parts[2]), std::stof(parts[3])});
        } else if (parts[0] == "vn" && parts.size() >= 4) {
            normals.push_back(Normalize({std::stof(parts[1]), std::stof(parts[2]), std::stof(parts[3])}));
        } else if (parts[0] == "mtllib" && parts.size() >= 2) {
            for (size_t i = 1; i < parts.size(); ++i) {
                auto loaded = LoadMtlFile(baseDirectory / parts[i]);
                materials.insert(loaded.begin(), loaded.end());
            }
        } else if (parts[0] == "usemtl" && parts.size() >= 2) {
            const std::string materialName = ToLower(line.substr(line.find(parts[1])));
            auto found = materials.find(materialName);
            currentMaterial = found != materials.end() ? found->second : ObjMaterial{};
        } else if (parts[0] == "f" && parts.size() >= 4) {
            std::vector<ObjCorner> corners;
            corners.reserve(parts.size() - 1);
            bool valid = true;
            for (size_t i = 1; i < parts.size(); ++i) {
                auto corner = ParseObjCorner(parts[i], static_cast<int>(positions.size()), normals);
                if (!corner) {
                    valid = false;
                    break;
                }
                corners.push_back(*corner);
            }
            if (!valid || corners.size() < 3) {
                continue;
            }

            for (size_t i = 1; i + 1 < corners.size(); ++i) {
                const auto& a = corners[0];
                const auto& b = corners[i];
                const auto& c = corners[i + 1];
                MeshTriangle triangle;
                triangle.p0 = positions[static_cast<size_t>(a.positionIndex)];
                triangle.p1 = positions[static_cast<size_t>(b.positionIndex)];
                triangle.p2 = positions[static_cast<size_t>(c.positionIndex)];
                if (a.hasNormal && b.hasNormal && c.hasNormal) {
                    triangle.normal = Normalize(a.normal + b.normal + c.normal);
                } else {
                    triangle.normal = Normalize(Cross(triangle.p1 - triangle.p0, triangle.p2 - triangle.p0));
                }
                triangle.color = currentMaterial.color;
                scene.triangles.push_back(triangle);
            }
        }
    }

    if (scene.triangles.empty()) {
        throw std::runtime_error("OBJ did not contain triangulatable faces.");
    }

    NormalizeMesh(scene.triangles);
    return scene;
}

struct PlyProperty {
    std::string name;
    std::string type;
};

struct PlyHeader {
    std::string format;
    int vertexCount = 0;
    std::vector<PlyProperty> properties;
};

int BinaryScalarSize(const std::string& type) {
    const std::string lower = ToLower(type);
    if (lower == "char" || lower == "int8" || lower == "uchar" || lower == "uint8") {
        return 1;
    }
    if (lower == "short" || lower == "int16" || lower == "ushort" || lower == "uint16") {
        return 2;
    }
    if (lower == "int" || lower == "int32" || lower == "uint" || lower == "uint32" || lower == "float" || lower == "float32") {
        return 4;
    }
    if (lower == "double" || lower == "float64") {
        return 8;
    }
    throw std::runtime_error("Unsupported PLY scalar type: " + type);
}

template <typename T>
T ReadUnaligned(const uint8_t* data) {
    T value;
    std::memcpy(&value, data, sizeof(T));
    return value;
}

double ReadBinaryScalar(const uint8_t* data, const std::string& type) {
    const std::string lower = ToLower(type);
    if (lower == "char" || lower == "int8") {
        return static_cast<int8_t>(*data);
    }
    if (lower == "uchar" || lower == "uint8") {
        return *data;
    }
    if (lower == "short" || lower == "int16") {
        return ReadUnaligned<int16_t>(data);
    }
    if (lower == "ushort" || lower == "uint16") {
        return ReadUnaligned<uint16_t>(data);
    }
    if (lower == "int" || lower == "int32") {
        return ReadUnaligned<int32_t>(data);
    }
    if (lower == "uint" || lower == "uint32") {
        return ReadUnaligned<uint32_t>(data);
    }
    if (lower == "float" || lower == "float32") {
        return ReadUnaligned<float>(data);
    }
    if (lower == "double" || lower == "float64") {
        return ReadUnaligned<double>(data);
    }
    throw std::runtime_error("Unsupported PLY scalar type: " + type);
}

PlyHeader ReadPlyHeader(std::istream& input) {
    std::vector<std::string> lines;
    std::string line;
    while (std::getline(input, line)) {
        if (!line.empty() && line.back() == '\r') {
            line.pop_back();
        }
        lines.push_back(line);
        if (line == "end_header") {
            break;
        }
    }

    if (lines.empty() || lines.front() != "ply") {
        throw std::runtime_error("File is not a PLY file.");
    }

    PlyHeader header;
    std::string currentElement;
    for (const auto& headerLine : lines) {
        const auto parts = SplitWhitespace(headerLine);
        if (parts.empty()) {
            continue;
        }
        if (parts[0] == "format" && parts.size() >= 2) {
            header.format = parts[1];
        } else if (parts[0] == "element" && parts.size() >= 3) {
            currentElement = parts[1];
            if (currentElement == "vertex") {
                header.vertexCount = std::stoi(parts[2]);
            }
        } else if (parts[0] == "property" && currentElement == "vertex" && parts.size() >= 3 && parts[1] != "list") {
            header.properties.push_back({parts[2], parts[1]});
        }
    }

    if (header.vertexCount <= 0) {
        throw std::runtime_error("PLY does not contain a vertex element.");
    }
    return header;
}

class PlyLayout {
public:
    explicit PlyLayout(const std::vector<PlyProperty>& properties) {
        for (size_t i = 0; i < properties.size(); ++i) {
            indices_[ToLower(properties[i].name)] = i;
        }
    }

    bool TryGet(const std::vector<double>& values, std::string_view name, double& value) const {
        auto found = indices_.find(ToLower(std::string(name)));
        if (found == indices_.end() || found->second >= values.size()) {
            value = 0.0;
            return false;
        }
        value = values[found->second];
        return true;
    }

private:
    std::unordered_map<std::string, size_t> indices_;
};

class PlyBinaryLayout {
public:
    explicit PlyBinaryLayout(const std::vector<PlyProperty>& properties) {
        int offset = 0;
        for (const auto& property : properties) {
            properties_[ToLower(property.name)] = {offset, property.type};
            offset += BinaryScalarSize(property.type);
        }
        recordStride_ = offset;
    }

    int recordStride() const { return recordStride_; }

    bool TryGet(const uint8_t* row, int rowSize, std::string_view name, double& value) const {
        auto found = properties_.find(ToLower(std::string(name)));
        if (found == properties_.end()) {
            value = 0.0;
            return false;
        }
        const int size = BinaryScalarSize(found->second.type);
        if (found->second.offset < 0 || found->second.offset + size > rowSize) {
            value = 0.0;
            return false;
        }
        value = ReadBinaryScalar(row + found->second.offset, found->second.type);
        return true;
    }

private:
    struct BinaryProperty {
        int offset = 0;
        std::string type;
    };

    int recordStride_ = 0;
    std::unordered_map<std::string, BinaryProperty> properties_;
};

float NormalizeColorChannel(float value) {
    return value > 1.0f ? Clamp01(value / 255.0f) : Clamp01(value);
}

float Sigmoid(float value) {
    if (value >= 0.0f) {
        const float z = std::exp(-value);
        return 1.0f / (1.0f + z);
    }
    const float z = std::exp(value);
    return z / (1.0f + z);
}

float LogScaleToRadius(float value) {
    return std::exp(Clamp(value, -12.0f, 4.0f));
}

void QuaternionToAxes(float w, float x, float y, float z, Vec3& axis0, Vec3& axis1, Vec3& axis2) {
    const float xx = x * x;
    const float yy = y * y;
    const float zz = z * z;
    const float xy = x * y;
    const float xz = x * z;
    const float yz = y * z;
    const float wx = w * x;
    const float wy = w * y;
    const float wz = w * z;
    axis0 = {1.0f - 2.0f * (yy + zz), 2.0f * (xy + wz), 2.0f * (xz - wy)};
    axis1 = {2.0f * (xy - wz), 1.0f - 2.0f * (xx + zz), 2.0f * (yz + wx)};
    axis2 = {2.0f * (xz + wy), 2.0f * (yz - wx), 1.0f - 2.0f * (xx + yy)};
}

Vec3 FlipSplatY(Vec3 value) {
    return {value.x, -value.y, value.z};
}

template <typename Values>
bool TryCreateSplat(const Values& values, GaussianSplat& splat) {
    double x = 0.0;
    double y = 0.0;
    double z = 0.0;
    if (!values.TryGet("x", x) || !values.TryGet("y", y) || !values.TryGet("z", z)) {
        return false;
    }

    double dc0 = 0.0;
    double dc1 = 0.0;
    double dc2 = 0.0;
    Color4 color;
    if (values.TryGet("f_dc_0", dc0) && values.TryGet("f_dc_1", dc1) && values.TryGet("f_dc_2", dc2)) {
        color.r = Clamp01(0.5f + kSphericalHarmonicsC0 * static_cast<float>(dc0));
        color.g = Clamp01(0.5f + kSphericalHarmonicsC0 * static_cast<float>(dc1));
        color.b = Clamp01(0.5f + kSphericalHarmonicsC0 * static_cast<float>(dc2));
    } else {
        double r = 0.85;
        double g = 0.90;
        double b = 1.00;
        double tmp = 0.0;
        if (values.TryGet("red", tmp) || values.TryGet("r", tmp)) r = tmp;
        if (values.TryGet("green", tmp) || values.TryGet("g", tmp)) g = tmp;
        if (values.TryGet("blue", tmp) || values.TryGet("b", tmp)) b = tmp;
        color.r = NormalizeColorChannel(static_cast<float>(r));
        color.g = NormalizeColorChannel(static_cast<float>(g));
        color.b = NormalizeColorChannel(static_cast<float>(b));
    }

    double opacity = 0.0;
    double alpha = 0.0;
    if (values.TryGet("opacity", opacity)) {
        color.a = Sigmoid(static_cast<float>(opacity));
    } else if (values.TryGet("alpha", alpha)) {
        color.a = NormalizeColorChannel(static_cast<float>(alpha));
    } else {
        color.a = 0.45f;
    }
    if (color.a <= 0.003f) {
        return false;
    }

    double s0 = 0.0;
    double s1 = 0.0;
    double s2 = 0.0;
    Vec3 scale;
    if (values.TryGet("scale_0", s0) && values.TryGet("scale_1", s1) && values.TryGet("scale_2", s2)) {
        scale = {LogScaleToRadius(static_cast<float>(s0)), LogScaleToRadius(static_cast<float>(s1)), LogScaleToRadius(static_cast<float>(s2))};
    } else {
        double sx = 0.025;
        double sy = sx;
        double sz = sy;
        double tmp = 0.0;
        if (values.TryGet("scale_x", tmp) || values.TryGet("sx", tmp)) sx = tmp;
        if (values.TryGet("scale_y", tmp) || values.TryGet("sy", tmp)) sy = tmp;
        if (values.TryGet("scale_z", tmp) || values.TryGet("sz", tmp)) sz = tmp;
        scale = {
            std::max(0.00001f, static_cast<float>(sx)),
            std::max(0.00001f, static_cast<float>(sy)),
            std::max(0.00001f, static_cast<float>(sz)),
        };
    }

    double r0 = 1.0;
    double r1 = 0.0;
    double r2 = 0.0;
    double r3 = 0.0;
    if (!(values.TryGet("rot_0", r0) && values.TryGet("rot_1", r1) && values.TryGet("rot_2", r2) && values.TryGet("rot_3", r3))) {
        double qw = 1.0;
        double qx = 0.0;
        double qy = 0.0;
        double qz = 0.0;
        values.TryGet("qw", qw);
        values.TryGet("qx", qx);
        values.TryGet("qy", qy);
        values.TryGet("qz", qz);
        r0 = qw == 0.0 ? 1.0 : qw;
        r1 = qx;
        r2 = qy;
        r3 = qz;
    }

    float qw = static_cast<float>(r0);
    float qx = static_cast<float>(r1);
    float qy = static_cast<float>(r2);
    float qz = static_cast<float>(r3);
    const float qLength = std::sqrt(qw * qw + qx * qx + qy * qy + qz * qz);
    if (qLength <= 0.00001f) {
        qw = 1.0f;
        qx = qy = qz = 0.0f;
    } else {
        qw /= qLength;
        qx /= qLength;
        qy /= qLength;
        qz /= qLength;
    }

    Vec3 basis0;
    Vec3 basis1;
    Vec3 basis2;
    QuaternionToAxes(qw, qx, qy, qz, basis0, basis1, basis2);

    splat.position = FlipSplatY({static_cast<float>(x), static_cast<float>(y), static_cast<float>(z)});
    splat.axis0 = FlipSplatY(basis0 * scale.x);
    splat.axis1 = FlipSplatY(basis1 * scale.y);
    splat.axis2 = FlipSplatY(basis2 * scale.z);
    splat.color = color;
    return true;
}

class PlyAsciiValues {
public:
    PlyAsciiValues(const PlyLayout& layout, const std::vector<double>& values) : layout_(layout), values_(values) {}
    bool TryGet(std::string_view name, double& value) const { return layout_.TryGet(values_, name, value); }
private:
    const PlyLayout& layout_;
    const std::vector<double>& values_;
};

class PlyBinaryValues {
public:
    PlyBinaryValues(const PlyBinaryLayout& layout, const uint8_t* row, int rowSize) : layout_(layout), row_(row), rowSize_(rowSize) {}
    bool TryGet(std::string_view name, double& value) const { return layout_.TryGet(row_, rowSize_, name, value); }
private:
    const PlyBinaryLayout& layout_;
    const uint8_t* row_;
    int rowSize_;
};

int MaxLoadedSplats(int sourceCount) {
    const char* value = std::getenv("MESHMODELER_MAX_SPLATS");
    if (value != nullptr) {
        const int parsed = std::atoi(value);
        if (parsed > 0) {
            return std::min(sourceCount, std::max(10000, parsed));
        }
    }
    return sourceCount;
}

LoadedScene LoadPlySplats(const std::filesystem::path& path) {
    std::ifstream input(path, std::ios::binary);
    if (!input) {
        throw std::runtime_error("Could not open " + path.string());
    }

    const PlyHeader header = ReadPlyHeader(input);
    LoadedScene scene;
    scene.kind = SceneKind::Splats;
    scene.name = FileNameForDisplay(path);
    scene.source = "Gaussian PLY " + header.format;

    const int maxSplats = MaxLoadedSplats(header.vertexCount);
    const int sampleStep = std::max(1, (header.vertexCount + maxSplats - 1) / maxSplats);

    if (header.format == "ascii") {
        PlyLayout layout(header.properties);
        std::vector<double> values(header.properties.size());
        std::string line;
        for (int i = 0; i < header.vertexCount && std::getline(input, line); ++i) {
            if (sampleStep > 1 && i % sampleStep != 0) {
                continue;
            }
            const auto parts = SplitWhitespace(line);
            if (parts.size() < header.properties.size()) {
                continue;
            }
            for (size_t p = 0; p < header.properties.size(); ++p) {
                values[p] = std::stod(parts[p]);
            }
            GaussianSplat splat;
            if (TryCreateSplat(PlyAsciiValues(layout, values), splat)) {
                scene.splats.push_back(splat);
            }
        }
    } else if (header.format == "binary_little_endian") {
        PlyBinaryLayout layout(header.properties);
        std::vector<uint8_t> row(static_cast<size_t>(layout.recordStride()));
        for (int i = 0; i < header.vertexCount; ++i) {
            input.read(reinterpret_cast<char*>(row.data()), static_cast<std::streamsize>(row.size()));
            if (!input) {
                break;
            }
            if (sampleStep > 1 && i % sampleStep != 0) {
                continue;
            }
            GaussianSplat splat;
            if (TryCreateSplat(PlyBinaryValues(layout, row.data(), static_cast<int>(row.size())), splat)) {
                scene.splats.push_back(splat);
            }
        }
    } else {
        throw std::runtime_error("Unsupported PLY format: " + header.format);
    }

    if (scene.splats.empty()) {
        throw std::runtime_error("PLY did not contain readable Gaussian splats.");
    }

    NormalizeSplats(scene.splats);
    return scene;
}

uint16_t ReadLe16(const std::vector<uint8_t>& data, size_t offset) {
    if (offset + 2 > data.size()) {
        throw std::runtime_error("Unexpected end of zip data.");
    }
    return static_cast<uint16_t>(static_cast<uint16_t>(data[offset]) | (static_cast<uint16_t>(data[offset + 1]) << 8));
}

uint32_t ReadLe32(const std::vector<uint8_t>& data, size_t offset) {
    if (offset + 4 > data.size()) {
        throw std::runtime_error("Unexpected end of zip data.");
    }
    return static_cast<uint32_t>(data[offset]) |
        (static_cast<uint32_t>(data[offset + 1]) << 8) |
        (static_cast<uint32_t>(data[offset + 2]) << 16) |
        (static_cast<uint32_t>(data[offset + 3]) << 24);
}

std::string NormalizeSogPath(std::string path) {
    std::replace(path.begin(), path.end(), '\\', '/');
    while (!path.empty() && path.front() == '/') {
        path.erase(path.begin());
    }
    return path;
}

std::string BaseName(std::string path) {
    path = NormalizeSogPath(std::move(path));
    const size_t slash = path.find_last_of('/');
    return slash == std::string::npos ? path : path.substr(slash + 1);
}

std::vector<uint8_t> InflateRawDeflate(const uint8_t* input, size_t inputSize, size_t outputSize) {
    std::vector<uint8_t> output(outputSize);
    z_stream stream = {};
    stream.next_in = const_cast<Bytef*>(reinterpret_cast<const Bytef*>(input));
    stream.avail_in = static_cast<uInt>(inputSize);
    stream.next_out = reinterpret_cast<Bytef*>(output.data());
    stream.avail_out = static_cast<uInt>(output.size());

    if (inflateInit2(&stream, -MAX_WBITS) != Z_OK) {
        throw std::runtime_error("Could not initialize deflate decoder.");
    }
    const int result = inflate(&stream, Z_FINISH);
    inflateEnd(&stream);
    if (result != Z_STREAM_END) {
        throw std::runtime_error("Could not inflate SOG zip entry.");
    }
    output.resize(stream.total_out);
    return output;
}

class ZipArchive {
public:
    explicit ZipArchive(std::vector<uint8_t> bytes) : bytes_(std::move(bytes)) {
        parse();
    }

    std::vector<uint8_t> readFile(std::string name) const {
        name = NormalizeSogPath(std::move(name));
        const std::string lowerName = ToLower(name);
        auto found = entries_.find(lowerName);
        if (found == entries_.end()) {
            const std::string lowerBaseName = ToLower(BaseName(name));
            for (const auto& item : entries_) {
                if (ToLower(BaseName(item.second.name)) == lowerBaseName) {
                    found = entries_.find(item.first);
                    break;
                }
                const std::string suffix = "/" + lowerName;
                if (item.first.size() >= suffix.size() && item.first.compare(item.first.size() - suffix.size(), suffix.size(), suffix) == 0) {
                    found = entries_.find(item.first);
                    break;
                }
            }
        }
        if (found == entries_.end()) {
            throw std::runtime_error("SOG zip does not contain " + name);
        }

        const Entry& entry = found->second;
        if (ReadLe32(bytes_, entry.localHeaderOffset) != 0x04034b50u) {
            throw std::runtime_error("Invalid local zip header.");
        }
        const uint16_t nameLength = ReadLe16(bytes_, entry.localHeaderOffset + 26);
        const uint16_t extraLength = ReadLe16(bytes_, entry.localHeaderOffset + 28);
        const size_t dataOffset = entry.localHeaderOffset + 30u + nameLength + extraLength;
        if (dataOffset + entry.compressedSize > bytes_.size()) {
            throw std::runtime_error("Zip entry exceeds archive size.");
        }

        const uint8_t* data = bytes_.data() + dataOffset;
        if (entry.method == 0) {
            return std::vector<uint8_t>(data, data + entry.compressedSize);
        }
        if (entry.method == 8) {
            return InflateRawDeflate(data, entry.compressedSize, entry.uncompressedSize);
        }
        throw std::runtime_error("Unsupported zip compression method in SOG file.");
    }

private:
    struct Entry {
        std::string name;
        uint16_t method = 0;
        uint32_t compressedSize = 0;
        uint32_t uncompressedSize = 0;
        uint32_t localHeaderOffset = 0;
    };

    void parse() {
        if (bytes_.size() < 22) {
            throw std::runtime_error("SOG zip is too small.");
        }

        size_t eocd = std::string::npos;
        const size_t minOffset = bytes_.size() > 66000 ? bytes_.size() - 66000 : 0;
        for (size_t offset = bytes_.size() - 22; offset + 4 <= bytes_.size() && offset >= minOffset; --offset) {
            if (ReadLe32(bytes_, offset) == 0x06054b50u) {
                eocd = offset;
                break;
            }
            if (offset == 0) {
                break;
            }
        }
        if (eocd == std::string::npos) {
            throw std::runtime_error("SOG zip end-of-central-directory record was not found.");
        }

        const uint16_t entryCount = ReadLe16(bytes_, eocd + 10);
        const uint32_t centralOffset = ReadLe32(bytes_, eocd + 16);
        size_t offset = centralOffset;
        for (uint16_t i = 0; i < entryCount; ++i) {
            if (ReadLe32(bytes_, offset) != 0x02014b50u) {
                throw std::runtime_error("Invalid central zip header.");
            }
            Entry entry;
            const uint16_t flags = ReadLe16(bytes_, offset + 8);
            if ((flags & 1u) != 0u) {
                throw std::runtime_error("Encrypted SOG zip entries are not supported.");
            }
            entry.method = ReadLe16(bytes_, offset + 10);
            entry.compressedSize = ReadLe32(bytes_, offset + 20);
            entry.uncompressedSize = ReadLe32(bytes_, offset + 24);
            const uint16_t nameLength = ReadLe16(bytes_, offset + 28);
            const uint16_t extraLength = ReadLe16(bytes_, offset + 30);
            const uint16_t commentLength = ReadLe16(bytes_, offset + 32);
            entry.localHeaderOffset = ReadLe32(bytes_, offset + 42);
            if (offset + 46u + nameLength > bytes_.size()) {
                throw std::runtime_error("Invalid zip entry name.");
            }
            entry.name = NormalizeSogPath(std::string(reinterpret_cast<const char*>(bytes_.data() + offset + 46), nameLength));
            if (!entry.name.empty() && entry.name.back() != '/') {
                entries_[ToLower(entry.name)] = entry;
            }
            offset += 46u + nameLength + extraLength + commentLength;
        }
    }

    std::vector<uint8_t> bytes_;
    std::unordered_map<std::string, Entry> entries_;
};

struct SogImage {
    int width = 0;
    int height = 0;
    std::vector<uint8_t> pixels;

    int pixelCount() const { return width * height; }

    int get(int index, int channel) const {
        const size_t offset = static_cast<size_t>(index) * 4u + static_cast<size_t>(channel);
        return offset < pixels.size() ? pixels[offset] : 0;
    }
};

SogImage DecodeSogImage(const std::vector<uint8_t>& encoded, const std::string& name) {
    sk_sp<SkData> data = SkData::MakeWithCopy(encoded.data(), encoded.size());
    std::unique_ptr<SkCodec> codec = SkCodec::MakeFromData(std::move(data));
    if (!codec) {
        throw std::runtime_error("Could not decode SOG image " + name);
    }

    SkImageInfo info = codec->getInfo().makeColorType(kRGBA_8888_SkColorType).makeAlphaType(kUnpremul_SkAlphaType);
    SogImage image;
    image.width = info.width();
    image.height = info.height();
    const size_t rowBytes = info.minRowBytes();
    image.pixels.resize(rowBytes * static_cast<size_t>(image.height));
    const SkCodec::Result result = codec->getPixels(info, image.pixels.data(), rowBytes);
    if (result != SkCodec::kSuccess && result != SkCodec::kIncompleteInput) {
        throw std::runtime_error("Could not decode SOG image pixels " + name);
    }
    return image;
}

struct SogSection {
    std::vector<std::string> files;
    std::vector<float> mins;
    std::vector<float> maxs;
    std::vector<float> codebook;
};

struct SogMeta {
    int version = 0;
    int count = 0;
    SogSection means;
    SogSection scales;
    SogSection quats;
    SogSection sh0;
};

std::vector<std::string> ReadStringArray(NSDictionary* dictionary, NSString* key) {
    std::vector<std::string> values;
    id object = [dictionary objectForKey:key];
    if (![object isKindOfClass:[NSArray class]]) {
        return values;
    }
    for (id item in (NSArray*)object) {
        if ([item isKindOfClass:[NSString class]]) {
            values.push_back([(NSString*)item UTF8String]);
        }
    }
    return values;
}

std::vector<float> ReadFloatArray(NSDictionary* dictionary, NSString* key) {
    std::vector<float> values;
    id object = [dictionary objectForKey:key];
    if (![object isKindOfClass:[NSArray class]]) {
        return values;
    }
    for (id item in (NSArray*)object) {
        if ([item respondsToSelector:@selector(floatValue)]) {
            values.push_back([item floatValue]);
        }
    }
    return values;
}

SogSection ReadSogSection(NSDictionary* dictionary, NSString* key) {
    SogSection section;
    id object = [dictionary objectForKey:key];
    if (![object isKindOfClass:[NSDictionary class]]) {
        return section;
    }
    NSDictionary* sectionDictionary = (NSDictionary*)object;
    section.files = ReadStringArray(sectionDictionary, @"files");
    section.mins = ReadFloatArray(sectionDictionary, @"mins");
    section.maxs = ReadFloatArray(sectionDictionary, @"maxs");
    section.codebook = ReadFloatArray(sectionDictionary, @"codebook");
    return section;
}

SogMeta ParseSogMeta(const std::vector<uint8_t>& bytes) {
    @autoreleasepool {
        NSData* data = [NSData dataWithBytes:bytes.data() length:bytes.size()];
        NSError* error = nil;
        id json = [NSJSONSerialization JSONObjectWithData:data options:0 error:&error];
        if (error != nil || ![json isKindOfClass:[NSDictionary class]]) {
            throw std::runtime_error("SOG meta.json is invalid.");
        }

        NSDictionary* dictionary = (NSDictionary*)json;
        SogMeta meta;
        meta.version = [[dictionary objectForKey:@"version"] intValue];
        meta.count = [[dictionary objectForKey:@"count"] intValue];
        meta.means = ReadSogSection(dictionary, @"means");
        meta.scales = ReadSogSection(dictionary, @"scales");
        meta.quats = ReadSogSection(dictionary, @"quats");
        meta.sh0 = ReadSogSection(dictionary, @"sh0");
        return meta;
    }
}

void ValidateSogMeta(const SogMeta& meta) {
    if (meta.version != 2) {
        throw std::runtime_error("Only SOG version 2 is supported.");
    }
    if (meta.count <= 0) {
        throw std::runtime_error("SOG count must be greater than zero.");
    }
    if (meta.means.files.size() < 2 || meta.scales.files.empty() || meta.quats.files.empty() || meta.sh0.files.empty()) {
        throw std::runtime_error("SOG meta.json is missing required file entries.");
    }
    if (meta.means.mins.size() < 3 || meta.means.maxs.size() < 3) {
        throw std::runtime_error("SOG means min/max data is incomplete.");
    }
    if (meta.scales.codebook.size() < 256 || meta.sh0.codebook.size() < 256) {
        throw std::runtime_error("SOG codebooks must contain at least 256 entries.");
    }
}

int DecodeSogUInt16(const SogImage& low, const SogImage& high, int index, int channel) {
    return low.get(index, channel) | (high.get(index, channel) << 8);
}

float DecodeSogUnlogLerp(int quantized, float minValue, float maxValue) {
    const float encoded = minValue + (maxValue - minValue) * (static_cast<float>(quantized) / 65535.0f);
    return encoded < 0.0f ? 1.0f - std::exp(-encoded) : std::exp(encoded) - 1.0f;
}

bool IsSogLogScaleCodebook(const std::vector<float>& codebook) {
    for (float value : codebook) {
        if (value <= 0.0f) {
            return true;
        }
    }
    return false;
}

float ReadSogScale(const std::vector<float>& codebook, int index, bool logEncoded) {
    if (index < 0 || static_cast<size_t>(index) >= codebook.size()) {
        throw std::runtime_error("SOG scale codebook index is out of range.");
    }
    const float value = codebook[static_cast<size_t>(index)];
    return logEncoded ? LogScaleToRadius(value) : std::max(0.00001f, value);
}

float DecodeSogQuatComponent(int value) {
    return (static_cast<float>(value) / 255.0f - 0.5f) * (2.0f / std::sqrt(2.0f));
}

void DecodeSogQuaternion(const SogImage& quats, int index, float& qw, float& qx, float& qy, float& qz) {
    const float a = DecodeSogQuatComponent(quats.get(index, 0));
    const float b = DecodeSogQuatComponent(quats.get(index, 1));
    const float c = DecodeSogQuatComponent(quats.get(index, 2));
    const int mode = quats.get(index, 3);
    const float d = std::sqrt(std::max(0.0f, 1.0f - a * a - b * b - c * c));

    switch (mode) {
        case 252: qw = d; qx = a; qy = b; qz = c; break;
        case 253: qw = a; qx = d; qy = b; qz = c; break;
        case 254: qw = a; qx = b; qy = d; qz = c; break;
        case 255: qw = a; qx = b; qy = c; qz = d; break;
        default: qw = 1.0f; qx = qy = qz = 0.0f; break;
    }

    const float length = std::sqrt(qw * qw + qx * qx + qy * qy + qz * qz);
    if (length <= 0.00001f) {
        qw = 1.0f;
        qx = qy = qz = 0.0f;
        return;
    }
    qw /= length;
    qx /= length;
    qy /= length;
    qz /= length;
}

LoadedScene LoadSogFromBytes(
    const std::vector<uint8_t>& metaBytes,
    const std::function<std::vector<uint8_t>(const std::string&)>& readFile,
    const std::string& name,
    const std::string& source) {
    const SogMeta meta = ParseSogMeta(metaBytes);
    ValidateSogMeta(meta);

    const SogImage meansLow = DecodeSogImage(readFile(meta.means.files[0]), meta.means.files[0]);
    const SogImage meansHigh = DecodeSogImage(readFile(meta.means.files[1]), meta.means.files[1]);
    const SogImage scales = DecodeSogImage(readFile(meta.scales.files[0]), meta.scales.files[0]);
    const SogImage quats = DecodeSogImage(readFile(meta.quats.files[0]), meta.quats.files[0]);
    const SogImage sh0 = DecodeSogImage(readFile(meta.sh0.files[0]), meta.sh0.files[0]);

    const int imageCapacity = std::min({meansLow.pixelCount(), meansHigh.pixelCount(), scales.pixelCount(), quats.pixelCount(), sh0.pixelCount()});
    const int sourceCount = std::min(meta.count, imageCapacity);
    if (sourceCount <= 0) {
        throw std::runtime_error("SOG does not contain addressable splats.");
    }

    const int maxSplats = MaxLoadedSplats(sourceCount);
    const int sampleStep = std::max(1, (sourceCount + maxSplats - 1) / maxSplats);
    const bool scalesAreLogEncoded = IsSogLogScaleCodebook(meta.scales.codebook);

    LoadedScene scene;
    scene.kind = SceneKind::Splats;
    scene.name = name;
    scene.source = source;
    scene.splats.reserve(static_cast<size_t>(std::min(sourceCount, maxSplats)));

    for (int i = 0; i < sourceCount; ++i) {
        if (sampleStep > 1 && i % sampleStep != 0) {
            continue;
        }

        const int x = DecodeSogUInt16(meansLow, meansHigh, i, 0);
        const int y = DecodeSogUInt16(meansLow, meansHigh, i, 1);
        const int z = DecodeSogUInt16(meansLow, meansHigh, i, 2);
        const Vec3 position = {
            DecodeSogUnlogLerp(x, meta.means.mins[0], meta.means.maxs[0]),
            DecodeSogUnlogLerp(y, meta.means.mins[1], meta.means.maxs[1]),
            DecodeSogUnlogLerp(z, meta.means.mins[2], meta.means.maxs[2]),
        };

        const Vec3 scale = {
            ReadSogScale(meta.scales.codebook, scales.get(i, 0), scalesAreLogEncoded),
            ReadSogScale(meta.scales.codebook, scales.get(i, 1), scalesAreLogEncoded),
            ReadSogScale(meta.scales.codebook, scales.get(i, 2), scalesAreLogEncoded),
        };

        float qw;
        float qx;
        float qy;
        float qz;
        DecodeSogQuaternion(quats, i, qw, qx, qy, qz);
        Vec3 basis0;
        Vec3 basis1;
        Vec3 basis2;
        QuaternionToAxes(qw, qx, qy, qz, basis0, basis1, basis2);

        const int rIndex = sh0.get(i, 0);
        const int gIndex = sh0.get(i, 1);
        const int bIndex = sh0.get(i, 2);
        const float alpha = static_cast<float>(sh0.get(i, 3)) / 255.0f;
        if (alpha <= 0.003f) {
            continue;
        }

        GaussianSplat splat;
        splat.position = FlipSplatY(position);
        splat.axis0 = FlipSplatY(basis0 * scale.x);
        splat.axis1 = FlipSplatY(basis1 * scale.y);
        splat.axis2 = FlipSplatY(basis2 * scale.z);
        splat.color = {
            Clamp01(0.5f + kSphericalHarmonicsC0 * meta.sh0.codebook[static_cast<size_t>(rIndex)]),
            Clamp01(0.5f + kSphericalHarmonicsC0 * meta.sh0.codebook[static_cast<size_t>(gIndex)]),
            Clamp01(0.5f + kSphericalHarmonicsC0 * meta.sh0.codebook[static_cast<size_t>(bIndex)]),
            alpha,
        };
        scene.splats.push_back(splat);
    }

    if (scene.splats.empty()) {
        throw std::runtime_error("SOG did not contain readable splats.");
    }

    NormalizeSplats(scene.splats);
    return scene;
}

LoadedScene LoadSog(const std::filesystem::path& path) {
    if (std::filesystem::is_directory(path)) {
        const std::filesystem::path root = std::filesystem::absolute(path);
        const auto metaPath = root / "meta.json";
        const auto metaBytes = ReadBinaryFile(metaPath);
        return LoadSogFromBytes(metaBytes, [root](const std::string& relativePath) {
            return ReadBinaryFile(root / NormalizeSogPath(relativePath));
        }, FileNameForDisplay(path), "PlayCanvas SOG directory");
    }

    const std::string extension = ToLower(path.extension().string());
    if (extension == ".sog" || extension == ".sob") {
        auto archive = std::make_shared<ZipArchive>(ReadBinaryFile(path));
        const auto metaBytes = archive->readFile("meta.json");
        return LoadSogFromBytes(metaBytes, [archive](const std::string& relativePath) {
            return archive->readFile(relativePath);
        }, FileNameForDisplay(path), "PlayCanvas SOG zip");
    }

    const std::filesystem::path root = path.parent_path();
    const auto metaBytes = ReadBinaryFile(path);
    return LoadSogFromBytes(metaBytes, [root](const std::string& relativePath) {
        return ReadBinaryFile(root / NormalizeSogPath(relativePath));
    }, FileNameForDisplay(path), "PlayCanvas SOG meta.json");
}

LoadedScene LoadScene(const std::filesystem::path& path) {
    const std::string extension = ToLower(path.extension().string());
    if (std::filesystem::is_directory(path) || extension == ".sog" || extension == ".sob" || extension == ".json" || ToLower(path.filename().string()) == "meta.json") {
        return LoadSog(path);
    }
    if (extension == ".ply") {
        return LoadPlySplats(path);
    }
    if (extension == ".obj") {
        return LoadObj(path);
    }
    throw std::runtime_error("Unsupported file extension: " + extension);
}

class MeshRenderer {
public:
    MeshRenderer() : scene_(CreateProceduralTorus()) {
        updateSummary();
    }

    bool loadPath(const std::string& path) {
        try {
            scene_ = LoadScene(std::filesystem::path(path));
            meshBuffer_ = nullptr;
            meshBufferSize_ = 0;
            status_ = "Loaded " + path;
            updateSummary();
            return true;
        } catch (const std::exception& ex) {
            status_ = std::string("Load failed: ") + ex.what();
            return false;
        }
    }

    const std::string& title() const { return scene_.name; }

    void setFrameStats(FrameStats stats) {
        frameStats_ = stats;
    }

    void orbit(float deltaX, float deltaY) {
        cameraYaw_ += deltaX * 0.010f;
        cameraPitch_ = Clamp(cameraPitch_ + deltaY * 0.010f, -1.35f, 1.35f);
    }

    void pan(float deltaX, float deltaY) {
        cameraPan_.x += deltaX;
        cameraPan_.y += deltaY;
    }

    void zoom(float wheelDelta) {
        const float factor = std::exp(wheelDelta * 0.0018f);
        cameraZoom_ = Clamp(cameraZoom_ * factor, 0.18f, 6.0f);
    }

    void resetCamera() {
        cameraYaw_ = -0.72f;
        cameraPitch_ = -0.38f;
        cameraZoom_ = 1.0f;
        cameraPan_ = {0.0f, 0.0f};
    }

    void draw(SkCanvas* canvas, GrDirectContext* context, int width, int height, double timeSeconds) {
        canvas->clear(SkColorSetRGB(5, 8, 14));
        drawGrid(canvas, width, height);

        renderStats_.drawMeshCalls = 0;
        renderStats_.vertexBytes = 0;
        renderStats_.vertexCount = 0;

        if (!context) {
            status_ = "No GrDirectContext.";
            drawHud(canvas, width, height);
            return;
        }

        if (scene_.kind == SceneKind::Mesh) {
            drawMeshScene(canvas, context, width, height, timeSeconds);
        } else {
            drawSplatScene(canvas, context, width, height, timeSeconds);
        }

        drawHud(canvas, width, height);
    }

private:
    struct DrawTriangle {
        float z = 0.0f;
        ScreenVertex vertices[3];
    };

    struct SplatDrawItem {
        float z = 0.0f;
        SplatVertex vertices[6];
    };

    static constexpr const char* kMeshVertexShader = R"(
Varyings main(const Attributes a) {
    Varyings v;
    v.position = a.position;
    v.color = a.color;
    return v;
}
)";

    static constexpr const char* kMeshFragmentShader = R"(
float2 main(const Varyings v, out half4 color) {
    half alpha = v.color.a;
    color = half4(v.color.rgb * alpha, alpha);
    return v.position;
}
)";

    static constexpr const char* kSplatVertexShader = R"(
Varyings main(const Attributes a) {
    Varyings v;
    v.position = a.position;
    v.local = a.local;
    v.color = a.color;
    return v;
}
)";

    static constexpr const char* kSplatFragmentShader = R"(
float2 main(const Varyings v, out half4 color) {
    float r2 = dot(v.local, v.local);
    float alpha = float(v.color.a) * exp(-r2 * 3.25);
    alpha *= 1.0 - smoothstep(0.88, 1.0, sqrt(r2));
    color = half4(half3(v.color.rgb * half(alpha)), half(alpha));
    return v.position;
}
)";

    bool ensureMeshSpec() {
        if (meshSpec_) {
            return true;
        }
        using Attribute = SkMeshSpecification::Attribute;
        using Varying = SkMeshSpecification::Varying;
        const std::array<Attribute, 2> attributes = {{
            {Attribute::Type::kFloat2, offsetof(ScreenVertex, x), SkString("position")},
            {Attribute::Type::kUByte4_unorm, offsetof(ScreenVertex, color), SkString("color")},
        }};
        const std::array<Varying, 1> varyings = {{
            {Varying::Type::kHalf4, SkString("color")},
        }};
        auto result = SkMeshSpecification::Make(
            SkSpan<const Attribute>(attributes.data(), attributes.size()),
            sizeof(ScreenVertex),
            SkSpan<const Varying>(varyings.data(), varyings.size()),
            SkString(kMeshVertexShader),
            SkString(kMeshFragmentShader),
            SkColorSpace::MakeSRGB(),
            kPremul_SkAlphaType);
        if (!result.specification) {
            status_ = "SkMesh mesh spec failed: " + std::string(result.error.c_str());
            return false;
        }
        meshSpec_ = std::move(result.specification);
        return true;
    }

    bool ensureSplatSpec() {
        if (splatSpec_) {
            return true;
        }
        using Attribute = SkMeshSpecification::Attribute;
        using Varying = SkMeshSpecification::Varying;
        const std::array<Attribute, 3> attributes = {{
            {Attribute::Type::kFloat2, offsetof(SplatVertex, x), SkString("position")},
            {Attribute::Type::kFloat2, offsetof(SplatVertex, localX), SkString("local")},
            {Attribute::Type::kUByte4_unorm, offsetof(SplatVertex, color), SkString("color")},
        }};
        const std::array<Varying, 2> varyings = {{
            {Varying::Type::kFloat2, SkString("local")},
            {Varying::Type::kHalf4, SkString("color")},
        }};
        auto result = SkMeshSpecification::Make(
            SkSpan<const Attribute>(attributes.data(), attributes.size()),
            sizeof(SplatVertex),
            SkSpan<const Varying>(varyings.data(), varyings.size()),
            SkString(kSplatVertexShader),
            SkString(kSplatFragmentShader),
            SkColorSpace::MakeSRGB(),
            kPremul_SkAlphaType);
        if (!result.specification) {
            status_ = "SkMesh splat spec failed: " + std::string(result.error.c_str());
            return false;
        }
        splatSpec_ = std::move(result.specification);
        return true;
    }

    template <typename Vertex>
    bool uploadVertices(GrDirectContext* context, const std::vector<Vertex>& vertices) {
        const size_t byteSize = vertices.size() * sizeof(Vertex);
        if (byteSize == 0) {
            return false;
        }
        if (!meshBuffer_ || meshBufferSize_ != byteSize) {
            meshBuffer_ = SkMeshes::MakeVertexBuffer(context, vertices.data(), byteSize);
            meshBufferSize_ = byteSize;
            return meshBuffer_ != nullptr;
        }
        return meshBuffer_->update(context, vertices.data(), 0, byteSize);
    }

    void drawMeshScene(SkCanvas* canvas, GrDirectContext* context, int width, int height, double timeSeconds) {
        if (!ensureMeshSpec()) {
            return;
        }

        const float scale = static_cast<float>(std::min(width, height)) * 0.39f * cameraZoom_;
        const Vec2 center = {width * 0.5f + cameraPan_.x, height * 0.52f + cameraPan_.y};
        const float yaw = cameraYaw_;
        const float pitch = cameraPitch_;
        const Vec3 light = Normalize({-0.35f, 0.55f, 0.76f});
        (void)timeSeconds;

        drawTriangles_.clear();
        drawTriangles_.reserve(scene_.triangles.size());
        for (const auto& triangle : scene_.triangles) {
            const Vec3 p0 = RotateYThenX(triangle.p0, yaw, pitch);
            const Vec3 p1 = RotateYThenX(triangle.p1, yaw, pitch);
            const Vec3 p2 = RotateYThenX(triangle.p2, yaw, pitch);
            const Vec3 normal = Normalize(RotateYThenX(triangle.normal, yaw, pitch));
            const float diffuse = 0.28f + 0.72f * std::abs(Dot(normal, light));
            const float rim = 0.10f * std::pow(1.0f - Clamp01(std::abs(normal.z)), 2.0f);
            Color4 color = triangle.color;
            color.r = Clamp01(color.r * diffuse + rim);
            color.g = Clamp01(color.g * diffuse + rim);
            color.b = Clamp01(color.b * diffuse + rim);

            DrawTriangle item;
            item.z = (p0.z + p1.z + p2.z) / 3.0f;
            const uint32_t packed = PackRgba(color);
            item.vertices[0] = {center.x + p0.x * scale, center.y - p0.y * scale, packed};
            item.vertices[1] = {center.x + p1.x * scale, center.y - p1.y * scale, packed};
            item.vertices[2] = {center.x + p2.x * scale, center.y - p2.y * scale, packed};
            drawTriangles_.push_back(item);
        }

        std::sort(drawTriangles_.begin(), drawTriangles_.end(), [](const DrawTriangle& a, const DrawTriangle& b) {
            return a.z < b.z;
        });

        meshVertices_.clear();
        meshVertices_.reserve(drawTriangles_.size() * 3u);
        for (const auto& item : drawTriangles_) {
            meshVertices_.push_back(item.vertices[0]);
            meshVertices_.push_back(item.vertices[1]);
            meshVertices_.push_back(item.vertices[2]);
        }

        if (!uploadVertices(context, meshVertices_)) {
            status_ = "Could not upload OBJ mesh vertices.";
            return;
        }

        SkRect bounds = SkRect::MakeWH(static_cast<float>(width), static_cast<float>(height));
        SkMesh::Result result = SkMesh::Make(meshSpec_, SkMesh::Mode::kTriangles, meshBuffer_, meshVertices_.size(), 0, nullptr, {}, bounds);
        if (!result.mesh.isValid()) {
            status_ = "SkMesh mesh creation failed: " + std::string(result.error.c_str());
            return;
        }

        SkPaint paint;
        paint.setAntiAlias(true);
        paint.setColor(SK_ColorWHITE);
        canvas->drawMesh(result.mesh, nullptr, paint);

        renderStats_.mode = "GPU-backed SkMesh OBJ triangles";
        renderStats_.primitiveCount = scene_.triangles.size();
        renderStats_.vertexCount = meshVertices_.size();
        renderStats_.vertexBytes = meshVertices_.size() * sizeof(ScreenVertex);
        renderStats_.drawMeshCalls = 1;
    }

    void drawSplatScene(SkCanvas* canvas, GrDirectContext* context, int width, int height, double timeSeconds) {
        if (!ensureSplatSpec()) {
            return;
        }

        const float scale = static_cast<float>(std::min(width, height)) * 0.42f * cameraZoom_;
        const Vec2 center = {width * 0.5f + cameraPan_.x, height * 0.52f + cameraPan_.y};
        const float yaw = cameraYaw_;
        const float pitch = cameraPitch_;
        (void)timeSeconds;

        splatDrawItems_.clear();
        splatDrawItems_.reserve(scene_.splats.size());
        for (const auto& splat : scene_.splats) {
            const Vec3 center3 = RotateYThenX(splat.position, yaw, pitch);
            std::array<Vec2, 3> axes = {{
                ProjectAxis(RotateYThenX(splat.axis0, yaw, pitch), scale),
                ProjectAxis(RotateYThenX(splat.axis1, yaw, pitch), scale),
                ProjectAxis(RotateYThenX(splat.axis2, yaw, pitch), scale),
            }};
            std::sort(axes.begin(), axes.end(), [](Vec2 a, Vec2 b) {
                return a.x * a.x + a.y * a.y > b.x * b.x + b.y * b.y;
            });

            Vec2 axis0 = axes[0];
            Vec2 axis1 = axes[1];
            if (axis0.x * axis0.x + axis0.y * axis0.y < 0.25f) {
                axis0 = {1.0f, 0.0f};
            }
            if (axis1.x * axis1.x + axis1.y * axis1.y < 0.25f) {
                axis1 = {0.0f, 1.0f};
            }

            const float extent = 2.65f;
            axis0.x *= extent;
            axis0.y *= extent;
            axis1.x *= extent;
            axis1.y *= extent;

            const Vec2 c = {center.x + center3.x * scale, center.y - center3.y * scale};
            const uint32_t packed = PackRgba(splat.color);
            auto makeVertex = [&](float lx, float ly) -> SplatVertex {
                return {
                    c.x + axis0.x * lx + axis1.x * ly,
                    c.y + axis0.y * lx + axis1.y * ly,
                    lx,
                    ly,
                    packed,
                    0u,
                };
            };

            SplatDrawItem item;
            item.z = center3.z;
            item.vertices[0] = makeVertex(-1.0f, -1.0f);
            item.vertices[1] = makeVertex( 1.0f, -1.0f);
            item.vertices[2] = makeVertex( 1.0f,  1.0f);
            item.vertices[3] = makeVertex(-1.0f, -1.0f);
            item.vertices[4] = makeVertex( 1.0f,  1.0f);
            item.vertices[5] = makeVertex(-1.0f,  1.0f);
            splatDrawItems_.push_back(item);
        }

        std::sort(splatDrawItems_.begin(), splatDrawItems_.end(), [](const SplatDrawItem& a, const SplatDrawItem& b) {
            return a.z < b.z;
        });

        splatVertices_.clear();
        splatVertices_.reserve(splatDrawItems_.size() * 6u);
        for (const auto& item : splatDrawItems_) {
            for (const auto& vertex : item.vertices) {
                splatVertices_.push_back(vertex);
            }
        }

        if (!uploadVertices(context, splatVertices_)) {
            status_ = "Could not upload Gaussian splat vertices.";
            return;
        }

        SkRect bounds = SkRect::MakeWH(static_cast<float>(width), static_cast<float>(height));
        SkMesh::Result result = SkMesh::Make(splatSpec_, SkMesh::Mode::kTriangles, meshBuffer_, splatVertices_.size(), 0, nullptr, {}, bounds);
        if (!result.mesh.isValid()) {
            status_ = "SkMesh splat creation failed: " + std::string(result.error.c_str());
            return;
        }

        SkPaint paint;
        paint.setAntiAlias(false);
        paint.setColor(SK_ColorWHITE);
        canvas->drawMesh(result.mesh, nullptr, paint);

        renderStats_.mode = "GPU-backed SkMesh Gaussian splats";
        renderStats_.primitiveCount = scene_.splats.size();
        renderStats_.vertexCount = splatVertices_.size();
        renderStats_.vertexBytes = splatVertices_.size() * sizeof(SplatVertex);
        renderStats_.drawMeshCalls = 1;
    }

    Vec2 ProjectAxis(Vec3 axis, float scale) const {
        return {axis.x * scale, -axis.y * scale};
    }

    void drawGrid(SkCanvas* canvas, int width, int height) {
        SkPaint paint;
        paint.setAntiAlias(false);
        paint.setStrokeWidth(1.0f);
        paint.setColor(SkColorSetARGB(38, 77, 112, 152));
        const float spacing = std::max(40.0f, std::min(width / 14.0f, height / 10.0f));
        for (float x = std::fmod(width * 0.5f, spacing); x <= width; x += spacing) {
            canvas->drawLine(x, 0.0f, x, static_cast<float>(height), paint);
        }
        for (float y = std::fmod(height * 0.5f, spacing); y <= height; y += spacing) {
            canvas->drawLine(0.0f, y, static_cast<float>(width), y, paint);
        }
        paint.setColor(SkColorSetARGB(80, 87, 201, 235));
        paint.setStrokeWidth(1.5f);
        canvas->drawLine(width * 0.5f, 0.0f, width * 0.5f, static_cast<float>(height), paint);
        canvas->drawLine(0.0f, height * 0.5f, static_cast<float>(width), height * 0.5f, paint);
    }

    void drawHud(SkCanvas* canvas, int width, int height) {
        SkPaint panelPaint;
        panelPaint.setColor(SkColorSetARGB(238, 236, 242, 248));
        canvas->drawRect(SkRect::MakeXYWH(14.0f, 14.0f, std::min(720.0f, width - 28.0f), 196.0f), panelPaint);

        SkPaint accentPaint;
        accentPaint.setColor(SkColorSetRGB(38, 92, 132));
        canvas->drawRect(SkRect::MakeXYWH(14.0f, 14.0f, 4.0f, 196.0f), accentPaint);

        sk_sp<SkTypeface> hudTypeface = HudTypeface();
        SkFont titleFont(hudTypeface, 18.0f);
        SkFont bodyFont(hudTypeface, 13.0f);
        SkFont metricFont(hudTypeface, 14.0f);
        titleFont.setEdging(SkFont::Edging::kAntiAlias);
        bodyFont.setEdging(SkFont::Edging::kAntiAlias);
        metricFont.setEdging(SkFont::Edging::kAntiAlias);
        SkPaint textPaint;
        textPaint.setAntiAlias(true);
        textPaint.setColor(SK_ColorBLACK);
        textPaint.setAlphaf(1.0f);
        canvas->drawString(scene_.name.c_str(), 28.0f, 40.0f, titleFont, textPaint);

        textPaint.setColor(SkColorSetRGB(34, 52, 68));
        canvas->drawString(summary_.c_str(), 28.0f, 64.0f, bodyFont, textPaint);
        canvas->drawString(status_.c_str(), 28.0f, 84.0f, bodyFont, textPaint);

        textPaint.setColor(SkColorSetRGB(10, 20, 30));
        const std::string frameLine = "FPS " + FormatFixed(frameStats_.fps, 1) +
            "   Frame " + FormatFixed(frameStats_.frameMs, 2) + " ms" +
            "   DrawMesh " + std::to_string(renderStats_.drawMeshCalls);
        canvas->drawString(frameLine.c_str(), 28.0f, 112.0f, metricFont, textPaint);

        const std::string meshLine = "Mode " + renderStats_.mode +
            "   Primitives " + std::to_string(renderStats_.primitiveCount) +
            "   Vertices " + std::to_string(renderStats_.vertexCount) +
            "   Buffer " + FormatMiB(static_cast<uint64_t>(renderStats_.vertexBytes));
        canvas->drawString(meshLine.c_str(), 28.0f, 136.0f, metricFont, textPaint);

        const std::string gpuLine = "GPU cache " + FormatMiB(frameStats_.gpuResourceBytes) +
            " / " + FormatMiB(frameStats_.gpuResourceLimit) +
            "   Resources " + std::to_string(frameStats_.gpuResourceCount);
        canvas->drawString(gpuLine.c_str(), 28.0f, 160.0f, metricFont, textPaint);

        const std::string cameraLine = "Camera yaw " + FormatFixed(cameraYaw_ * 180.0 / kPi, 0) +
            " pitch " + FormatFixed(cameraPitch_ * 180.0 / kPi, 0) +
            " zoom " + FormatFixed(cameraZoom_, 2) +
            " pan " + FormatFixed(cameraPan_.x, 0) + "," + FormatFixed(cameraPan_.y, 0);
        canvas->drawString(cameraLine.c_str(), 28.0f, 184.0f, metricFont, textPaint);

        textPaint.setColor(SkColorSetRGB(54, 72, 88));
        canvas->drawString("Left drag orbit, right/middle drag pan, wheel zoom, F/R reset, File > Open or drop OBJ/PLY/SOG/SOB", 28.0f, 204.0f, bodyFont, textPaint);

        (void)height;
    }

    void updateSummary() {
        if (scene_.kind == SceneKind::Mesh) {
            summary_ = scene_.source + " | " + std::to_string(scene_.triangles.size()) + " triangles | SkMesh triangles";
        } else {
            summary_ = scene_.source + " | " + std::to_string(scene_.splats.size()) + " splats | SkMesh Gaussian billboards";
        }
        if (status_.empty()) {
            status_ = "Ready.";
        }
    }

    LoadedScene scene_;
    std::string summary_;
    std::string status_ = "Ready.";
    FrameStats frameStats_;
    RenderStats renderStats_;
    float cameraYaw_ = -0.72f;
    float cameraPitch_ = -0.38f;
    float cameraZoom_ = 1.0f;
    Vec2 cameraPan_ = {0.0f, 0.0f};
    sk_sp<SkMeshSpecification> meshSpec_;
    sk_sp<SkMeshSpecification> splatSpec_;
    sk_sp<SkMesh::VertexBuffer> meshBuffer_;
    size_t meshBufferSize_ = 0;
    std::vector<DrawTriangle> drawTriangles_;
    std::vector<SplatDrawItem> splatDrawItems_;
    std::vector<ScreenVertex> meshVertices_;
    std::vector<SplatVertex> splatVertices_;
};

NSString* ToNSString(const std::string& value) {
    return [NSString stringWithUTF8String:value.c_str()];
}

std::string ToStdString(NSString* value) {
    return value == nil ? std::string() : std::string([value UTF8String]);
}

} // namespace

@interface MeshMetalView : MTKView <MTKViewDelegate, NSDraggingDestination>
- (instancetype)initWithFrame:(NSRect)frameRect;
- (void)openPath:(NSString*)path;
@end

@implementation MeshMetalView {
    id<MTLCommandQueue> _queue;
    sk_sp<GrDirectContext> _context;
    std::unique_ptr<MeshRenderer> _renderer;
    CFTimeInterval _startTime;
    CFTimeInterval _lastFrameTimestamp;
    double _fpsWindowSeconds;
    int _fpsWindowFrames;
    double _currentFps;
    double _lastFrameMs;
    DragMode _dragMode;
    NSPoint _lastDragPoint;
}

- (instancetype)initWithFrame:(NSRect)frameRect {
    id<MTLDevice> device = MTLCreateSystemDefaultDevice();
    self = [super initWithFrame:frameRect device:device];
    if (self == nil) {
        return nil;
    }

    if (device == nil) {
        NSLog(@"Metal is not available on this Mac.");
        return self;
    }

    _queue = [device newCommandQueue];
    GrMtlBackendContext backendContext = {};
    backendContext.fDevice.retain((__bridge GrMTLHandle)device);
    backendContext.fQueue.retain((__bridge GrMTLHandle)_queue);
    GrContextOptions options;
    options.fAvoidStencilBuffers = true;
    _context = GrDirectContexts::MakeMetal(backendContext, options);

    self.delegate = self;
    self.colorPixelFormat = MTLPixelFormatBGRA8Unorm;
    self.depthStencilPixelFormat = MTLPixelFormatInvalid;
    self.sampleCount = 1;
    self.preferredFramesPerSecond = 120;
    self.paused = NO;
    self.enableSetNeedsDisplay = NO;
    self.clearColor = MTLClearColorMake(0.02, 0.03, 0.05, 1.0);

    _renderer = std::make_unique<MeshRenderer>();
    _startTime = CACurrentMediaTime();
    _lastFrameTimestamp = 0.0;
    _fpsWindowSeconds = 0.0;
    _fpsWindowFrames = 0;
    _currentFps = 0.0;
    _lastFrameMs = 0.0;
    _dragMode = DragMode::None;
    _lastDragPoint = NSZeroPoint;
    [self registerForDraggedTypes:@[NSPasteboardTypeFileURL]];
    return self;
}

- (BOOL)acceptsFirstResponder {
    return YES;
}

- (BOOL)becomeFirstResponder {
    return YES;
}

- (void)openPath:(NSString*)path {
    if (path == nil || _renderer == nullptr) {
        return;
    }
    _renderer->loadPath(ToStdString(path));
    self.window.title = ToNSString(_renderer->title());
}

- (void)drawInMTKView:(MTKView*)view {
    if (_context == nullptr || _queue == nil || _renderer == nullptr) {
        return;
    }

    const CFTimeInterval frameStart = CACurrentMediaTime();
    if (_lastFrameTimestamp > 0.0) {
        const double deltaSeconds = frameStart - _lastFrameTimestamp;
        if (deltaSeconds > 0.0 && deltaSeconds < 0.250) {
            _fpsWindowSeconds += deltaSeconds;
            _fpsWindowFrames++;
            if (_fpsWindowSeconds >= 0.5 && _fpsWindowFrames > 0) {
                _currentFps = static_cast<double>(_fpsWindowFrames) / _fpsWindowSeconds;
                _fpsWindowSeconds = 0.0;
                _fpsWindowFrames = 0;
            }
        }
    }
    _lastFrameTimestamp = frameStart;

    id<CAMetalDrawable> drawable = view.currentDrawable;
    if (drawable == nil) {
        return;
    }

    const CGSize drawableSize = view.drawableSize;
    const int width = std::max(1, static_cast<int>(std::lround(drawableSize.width)));
    const int height = std::max(1, static_cast<int>(std::lround(drawableSize.height)));

    GrMtlTextureInfo textureInfo;
    textureInfo.fTexture.retain((__bridge GrMTLHandle)drawable.texture);
    GrBackendRenderTarget backendRenderTarget = GrBackendRenderTargets::MakeMtl(width, height, textureInfo);
    sk_sp<SkSurface> surface = SkSurfaces::WrapBackendRenderTarget(
        _context.get(),
        backendRenderTarget,
        kTopLeft_GrSurfaceOrigin,
        kBGRA_8888_SkColorType,
        SkColorSpace::MakeSRGB(),
        nullptr);

    if (surface == nullptr) {
        return;
    }

    int resourceCount = 0;
    size_t resourceBytes = 0;
    _context->getResourceCacheUsage(&resourceCount, &resourceBytes);

    FrameStats frameStats;
    frameStats.fps = _currentFps;
    frameStats.frameMs = _lastFrameMs;
    frameStats.gpuResourceCount = resourceCount;
    frameStats.gpuResourceBytes = static_cast<uint64_t>(resourceBytes);
    frameStats.gpuResourceLimit = static_cast<uint64_t>(_context->getResourceCacheLimit());
    _renderer->setFrameStats(frameStats);

    const double timeSeconds = CACurrentMediaTime() - _startTime;
    _renderer->draw(surface->getCanvas(), _context.get(), width, height, timeSeconds);
    _context->flushAndSubmit(surface.get());
    surface = nullptr;

    id<MTLCommandBuffer> commandBuffer = [_queue commandBuffer];
    [commandBuffer presentDrawable:drawable];
    [commandBuffer commit];
    _lastFrameMs = (CACurrentMediaTime() - frameStart) * 1000.0;
}

- (void)mtkView:(MTKView*)view drawableSizeWillChange:(CGSize)size {
    (void)view;
    (void)size;
}

- (NSPoint)localPointForEvent:(NSEvent*)event {
    return [self convertPoint:event.locationInWindow fromView:nil];
}

- (void)beginDrag:(NSEvent*)event mode:(DragMode)mode {
    _dragMode = mode;
    _lastDragPoint = [self localPointForEvent:event];
    [[self window] makeFirstResponder:self];
}

- (void)continueDrag:(NSEvent*)event expectedMode:(DragMode)mode {
    if (_dragMode != mode || _renderer == nullptr) {
        return;
    }

    NSPoint current = [self localPointForEvent:event];
    const float dx = static_cast<float>(current.x - _lastDragPoint.x);
    const float dy = static_cast<float>(current.y - _lastDragPoint.y);
    _lastDragPoint = current;

    if (mode == DragMode::Orbit) {
        _renderer->orbit(dx, dy);
    } else if (mode == DragMode::Pan) {
        _renderer->pan(dx, -dy);
    }
}

- (void)endDrag {
    _dragMode = DragMode::None;
}

- (void)mouseDown:(NSEvent*)event {
    [self beginDrag:event mode:DragMode::Orbit];
}

- (void)mouseDragged:(NSEvent*)event {
    [self continueDrag:event expectedMode:DragMode::Orbit];
}

- (void)mouseUp:(NSEvent*)event {
    (void)event;
    [self endDrag];
}

- (void)rightMouseDown:(NSEvent*)event {
    [self beginDrag:event mode:DragMode::Pan];
}

- (void)rightMouseDragged:(NSEvent*)event {
    [self continueDrag:event expectedMode:DragMode::Pan];
}

- (void)rightMouseUp:(NSEvent*)event {
    (void)event;
    [self endDrag];
}

- (void)otherMouseDown:(NSEvent*)event {
    [self beginDrag:event mode:DragMode::Pan];
}

- (void)otherMouseDragged:(NSEvent*)event {
    [self continueDrag:event expectedMode:DragMode::Pan];
}

- (void)otherMouseUp:(NSEvent*)event {
    (void)event;
    [self endDrag];
}

- (void)scrollWheel:(NSEvent*)event {
    if (_renderer == nullptr) {
        return;
    }
    _renderer->zoom(static_cast<float>(event.scrollingDeltaY));
}

- (void)keyDown:(NSEvent*)event {
    NSString* characters = event.charactersIgnoringModifiers.lowercaseString;
    if ([characters isEqualToString:@"f"] || [characters isEqualToString:@"r"]) {
        if (_renderer != nullptr) {
            _renderer->resetCamera();
        }
        return;
    }

    [super keyDown:event];
}

- (NSDragOperation)draggingEntered:(id<NSDraggingInfo>)sender {
    (void)sender;
    return NSDragOperationCopy;
}

- (BOOL)performDragOperation:(id<NSDraggingInfo>)sender {
    NSPasteboard* pasteboard = [sender draggingPasteboard];
    NSArray<NSURL*>* urls = [pasteboard readObjectsForClasses:@[[NSURL class]] options:@{}];
    NSURL* url = urls.firstObject;
    if (url == nil || !url.isFileURL) {
        return NO;
    }
    [self openPath:url.path];
    return YES;
}

@end

@interface AppDelegate : NSObject <NSApplicationDelegate>
- (instancetype)initWithInitialPath:(NSString*)path;
@end

@implementation AppDelegate {
    NSWindow* _window;
    MeshMetalView* _metalView;
    NSString* _initialPath;
}

- (instancetype)initWithInitialPath:(NSString*)path {
    self = [super init];
    if (self != nil) {
        _initialPath = [path copy];
    }
    return self;
}

- (void)applicationDidFinishLaunching:(NSNotification*)notification {
    (void)notification;

    const NSRect frame = NSMakeRect(0, 0, 1280, 820);
    _metalView = [[MeshMetalView alloc] initWithFrame:frame];
    _window = [[NSWindow alloc]
        initWithContentRect:frame
        styleMask:(NSWindowStyleMaskTitled | NSWindowStyleMaskClosable | NSWindowStyleMaskMiniaturizable | NSWindowStyleMaskResizable)
        backing:NSBackingStoreBuffered
        defer:NO];
    _window.title = @"MeshModeler SkiaNative C++";
    _window.contentView = _metalView;
    [_window center];
    [_window makeKeyAndOrderFront:nil];

    if (_initialPath.length > 0) {
        [_metalView openPath:_initialPath];
    }

    [NSApp activateIgnoringOtherApps:YES];
}

- (BOOL)applicationShouldTerminateAfterLastWindowClosed:(NSApplication*)sender {
    (void)sender;
    return YES;
}

- (BOOL)application:(NSApplication*)sender openFile:(NSString*)filename {
    (void)sender;
    [_metalView openPath:filename];
    return YES;
}

- (void)openDocument:(id)sender {
    (void)sender;
    NSOpenPanel* panel = [NSOpenPanel openPanel];
    panel.canChooseFiles = YES;
    panel.canChooseDirectories = YES;
    panel.allowsMultipleSelection = NO;
    panel.allowedContentTypes = @[
        [UTType typeWithFilenameExtension:@"obj"],
        [UTType typeWithFilenameExtension:@"ply"],
        [UTType typeWithFilenameExtension:@"sog"],
        [UTType typeWithFilenameExtension:@"sob"],
        UTTypeJSON,
    ];
    if ([panel runModal] == NSModalResponseOK) {
        [_metalView openPath:panel.URL.path];
    }
}

@end

void BuildMenu() {
    NSMenu* menuBar = [[NSMenu alloc] init];
    NSMenuItem* appMenuItem = [[NSMenuItem alloc] init];
    [menuBar addItem:appMenuItem];

    NSMenu* appMenu = [[NSMenu alloc] initWithTitle:@"MeshModeler SkiaNative C++"];
    NSString* quitTitle = @"Quit MeshModeler SkiaNative C++";
    [appMenu addItemWithTitle:quitTitle action:@selector(terminate:) keyEquivalent:@"q"];
    appMenuItem.submenu = appMenu;

    NSMenuItem* fileMenuItem = [[NSMenuItem alloc] init];
    [menuBar addItem:fileMenuItem];
    NSMenu* fileMenu = [[NSMenu alloc] initWithTitle:@"File"];
    [fileMenu addItemWithTitle:@"Open..." action:@selector(openDocument:) keyEquivalent:@"o"];
    fileMenuItem.submenu = fileMenu;

    NSApp.mainMenu = menuBar;
}

int main(int argc, const char* argv[]) {
    @autoreleasepool {
        NSString* initialPath = nil;
        if (argc > 1 && argv[1] != nullptr) {
            initialPath = [NSString stringWithUTF8String:argv[1]];
        }

        NSApplication* application = [NSApplication sharedApplication];
        [application setActivationPolicy:NSApplicationActivationPolicyRegular];
        BuildMenu();
        AppDelegate* delegate = [[AppDelegate alloc] initWithInitialPath:initialPath];
        application.delegate = delegate;
        [application run];
    }
    return 0;
}