#import <AppKit/AppKit.h>
#import <ImageIO/ImageIO.h>
#import <Metal/Metal.h>
#import <MetalKit/MetalKit.h>
#import <QuartzCore/QuartzCore.h>
#import <UniformTypeIdentifiers/UniformTypeIdentifiers.h>

#include <zlib.h>

#include <algorithm>
#include <array>
#include <chrono>
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

#include <simd/simd.h>

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

struct GaussianSplat {
    Vec3 position;
    Vec3 axis0;
    Vec3 axis1;
    Vec3 axis2;
    Color4 color;
};

struct LoadedScene {
    std::string name = "Procedural Gaussian splats";
    std::string source = "procedural";
    int sourceSplatCount = 0;
    std::vector<GaussianSplat> splats;
};

struct GpuSplat {
    simd_float4 position;
    simd_float4 axis0;
    simd_float4 axis1;
    simd_float4 axis2;
    simd_float4 color;
};

struct GpuSortItem {
    uint32_t key = 0;
    uint32_t index = 0;
};

struct MetalUniforms {
    simd_float2 viewportSize;
    simd_float2 center;
    float focalLength = 1.0f;
    float yaw = 0.0f;
    float pitch = 0.0f;
    float cameraDistance = 4.0f;
    float nearPlane = 0.05f;
    float farPlane = 80.0f;
    uint32_t splatCount = 0;
    uint32_t sortCount = 0;
    float cosYaw = 1.0f;
    float sinYaw = 0.0f;
    float cosPitch = 1.0f;
    float sinPitch = 0.0f;
};

struct MetalSortParams {
    uint32_t count = 0;
    uint32_t k = 0;
    uint32_t j = 0;
    uint32_t padding = 0;
};

struct FrameStats {
    double fps = 0.0;
    double frameMs = 0.0;
    double encodeMs = 0.0;
    int sortPasses = 0;
    bool sortUpdated = false;
};

enum class DragMode {
    None,
    Orbit,
    Pan,
};

enum class RenderMode {
    FastWeightedOit,
    SortedBackToFront,
};

using Clock = std::chrono::steady_clock;

float Clamp(float value, float minValue, float maxValue) {
    return std::max(minValue, std::min(maxValue, value));
}

float Clamp01(float value) {
    return Clamp(value, 0.0f, 1.0f);
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

uint32_t NextPowerOfTwo(uint32_t value) {
    if (value <= 1u) {
        return 1u;
    }
    --value;
    value |= value >> 1u;
    value |= value >> 2u;
    value |= value >> 4u;
    value |= value >> 8u;
    value |= value >> 16u;
    return value + 1u;
}

double MillisecondsBetween(Clock::time_point start, Clock::time_point end) {
    return std::chrono::duration<double, std::milli>(end - start).count();
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

LoadedScene CreateProceduralSplats() {
    LoadedScene scene;
    scene.name = "Procedural Gaussian splats";
    scene.source = "procedural";
    constexpr int count = 60000;
    scene.sourceSplatCount = count;
    scene.splats.reserve(count);
    for (int i = 0; i < count; ++i) {
        const float t = static_cast<float>(i) / static_cast<float>(count - 1);
        const float angle = t * kTau * 13.0f;
        const float radius = 0.15f + 1.75f * t;
        const Vec3 position = {
            std::cos(angle) * radius,
            std::sin(t * kTau * 5.0f) * 0.55f,
            std::sin(angle) * radius,
        };
        const float size = 0.018f + 0.030f * (0.5f + 0.5f * std::sin(angle * 1.7f));
        Color4 color = {
            Clamp01(0.55f + 0.45f * std::sin(angle + 0.2f)),
            Clamp01(0.55f + 0.45f * std::sin(angle + 2.1f)),
            Clamp01(0.65f + 0.35f * std::sin(angle + 4.0f)),
            0.34f,
        };
        scene.splats.push_back({
            position,
            {size * 1.8f, 0.0f, 0.0f},
            {0.0f, size, 0.0f},
            {0.0f, 0.0f, size * 0.8f},
            color,
        });
    }
    NormalizeSplats(scene.splats);
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

int BinaryScalarSize(const std::string& type) {
    const std::string lower = ToLower(type);
    if (lower == "char" || lower == "int8" || lower == "uchar" || lower == "uint8") return 1;
    if (lower == "short" || lower == "int16" || lower == "ushort" || lower == "uint16") return 2;
    if (lower == "int" || lower == "int32" || lower == "uint" || lower == "uint32" || lower == "float" || lower == "float32") return 4;
    if (lower == "double" || lower == "float64") return 8;
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
    if (lower == "char" || lower == "int8") return static_cast<int8_t>(*data);
    if (lower == "uchar" || lower == "uint8") return *data;
    if (lower == "short" || lower == "int16") return ReadUnaligned<int16_t>(data);
    if (lower == "ushort" || lower == "uint16") return ReadUnaligned<uint16_t>(data);
    if (lower == "int" || lower == "int32") return ReadUnaligned<int32_t>(data);
    if (lower == "uint" || lower == "uint32") return ReadUnaligned<uint32_t>(data);
    if (lower == "float" || lower == "float32") return ReadUnaligned<float>(data);
    if (lower == "double" || lower == "float64") return ReadUnaligned<double>(data);
    throw std::runtime_error("Unsupported PLY scalar type: " + type);
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
        scale = {std::max(0.00001f, static_cast<float>(sx)), std::max(0.00001f, static_cast<float>(sy)), std::max(0.00001f, static_cast<float>(sz))};
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
    scene.name = FileNameForDisplay(path);
    scene.source = "Gaussian PLY " + header.format;
    scene.sourceSplatCount = header.vertexCount;

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
    if (encoded.empty()) {
        throw std::runtime_error("SOG image is empty: " + name);
    }

    CFDataRef data = CFDataCreate(kCFAllocatorDefault, encoded.data(), static_cast<CFIndex>(encoded.size()));
    if (data == nullptr) {
        throw std::runtime_error("Could not create image data for " + name);
    }
    CGImageSourceRef source = CGImageSourceCreateWithData(data, nullptr);
    CFRelease(data);
    if (source == nullptr) {
        throw std::runtime_error("Could not create image source for " + name);
    }
    CGImageRef image = CGImageSourceCreateImageAtIndex(source, 0, nullptr);
    CFRelease(source);
    if (image == nullptr) {
        throw std::runtime_error("Could not decode SOG image " + name);
    }

    SogImage result;
    result.width = static_cast<int>(CGImageGetWidth(image));
    result.height = static_cast<int>(CGImageGetHeight(image));
    const size_t rowBytes = static_cast<size_t>(result.width) * 4u;
    result.pixels.resize(rowBytes * static_cast<size_t>(result.height));

    CGColorSpaceRef colorSpace = CGColorSpaceCreateDeviceRGB();
    CGBitmapInfo bitmapInfo = static_cast<CGBitmapInfo>(kCGBitmapByteOrder32Big | static_cast<CGBitmapInfo>(kCGImageAlphaLast));
    CGContextRef context = CGBitmapContextCreate(result.pixels.data(), result.width, result.height, 8, rowBytes, colorSpace, bitmapInfo);
    if (context == nullptr) {
        bitmapInfo = static_cast<CGBitmapInfo>(kCGBitmapByteOrder32Big | static_cast<CGBitmapInfo>(kCGImageAlphaPremultipliedLast));
        context = CGBitmapContextCreate(result.pixels.data(), result.width, result.height, 8, rowBytes, colorSpace, bitmapInfo);
    }
    CGColorSpaceRelease(colorSpace);
    if (context == nullptr) {
        CGImageRelease(image);
        throw std::runtime_error("Could not create RGBA decode surface for " + name);
    }
    CGContextSetBlendMode(context, kCGBlendModeCopy);
    CGContextDrawImage(context, CGRectMake(0.0, 0.0, result.width, result.height), image);
    CGContextRelease(context);
    CGImageRelease(image);
    return result;
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
    scene.name = name;
    scene.source = source;
    scene.sourceSplatCount = meta.count;
    scene.splats.reserve(static_cast<size_t>(std::min(sourceCount, maxSplats)));
    for (int i = 0; i < sourceCount; ++i) {
        if (sampleStep > 1 && i % sampleStep != 0) {
            continue;
        }
        const Vec3 position = {
            DecodeSogUnlogLerp(DecodeSogUInt16(meansLow, meansHigh, i, 0), meta.means.mins[0], meta.means.maxs[0]),
            DecodeSogUnlogLerp(DecodeSogUInt16(meansLow, meansHigh, i, 1), meta.means.mins[1], meta.means.maxs[1]),
            DecodeSogUnlogLerp(DecodeSogUInt16(meansLow, meansHigh, i, 2), meta.means.mins[2], meta.means.maxs[2]),
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
        scene.splats.push_back({
            FlipSplatY(position),
            FlipSplatY(basis0 * scale.x),
            FlipSplatY(basis1 * scale.y),
            FlipSplatY(basis2 * scale.z),
            {
                Clamp01(0.5f + kSphericalHarmonicsC0 * meta.sh0.codebook[static_cast<size_t>(rIndex)]),
                Clamp01(0.5f + kSphericalHarmonicsC0 * meta.sh0.codebook[static_cast<size_t>(gIndex)]),
                Clamp01(0.5f + kSphericalHarmonicsC0 * meta.sh0.codebook[static_cast<size_t>(bIndex)]),
                alpha,
            },
        });
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
    throw std::runtime_error("Unsupported file extension: " + extension);
}

NSString* ToNSString(const std::string& value) {
    return [NSString stringWithUTF8String:value.c_str()];
}

std::string ToStdString(NSString* value) {
    return value == nil ? std::string() : std::string([value UTF8String]);
}

constexpr const char* kMetalShaderSource = R"(
#include <metal_stdlib>
using namespace metal;

struct Splat {
    float4 position;
    float4 axis0;
    float4 axis1;
    float4 axis2;
    float4 color;
};

struct Uniforms {
    float2 viewportSize;
    float2 center;
    float focalLength;
    float yaw;
    float pitch;
    float cameraDistance;
    float nearPlane;
    float farPlane;
    uint splatCount;
    uint sortCount;
    float cosYaw;
    float sinYaw;
    float cosPitch;
    float sinPitch;
};

struct SortItem {
    uint key;
    uint index;
};

struct SortParams {
    uint count;
    uint k;
    uint j;
    uint padding;
};

struct VertexOut {
    float4 position [[position]];
    float2 local;
    float4 color;
    float viewZ;
};

struct OitFragmentOut {
    float4 accumulation [[color(0)]];
    float4 revealage [[color(1)]];
};

float3 rotateYThenX(float3 value, constant Uniforms& uniforms) {
    float3 yRotated = float3(uniforms.cosYaw * value.x + uniforms.sinYaw * value.z,
                             value.y,
                             -uniforms.sinYaw * value.x + uniforms.cosYaw * value.z);
    return float3(yRotated.x,
                  uniforms.cosPitch * yRotated.y - uniforms.sinPitch * yRotated.z,
                  uniforms.sinPitch * yRotated.y + uniforms.cosPitch * yRotated.z);
}

float2 projectViewPoint(float3 value, constant Uniforms& uniforms) {
    float z = max(value.z, uniforms.nearPlane);
    return uniforms.center + float2(value.x * uniforms.focalLength / z,
                                    -value.y * uniforms.focalLength / z);
}

uint depthSortKey(float viewZ, constant Uniforms& uniforms) {
    if (viewZ <= uniforms.nearPlane) {
        return 0xfffffffeu;
    }
    float normalizedDepth = clamp((viewZ - uniforms.nearPlane) / max(0.0001, uniforms.farPlane - uniforms.nearPlane), 0.0, 1.0);
    uint depth24 = uint(normalizedDepth * 16777215.0);
    return 0xfffffffeu - depth24;
}

bool shouldSwap(SortItem a, SortItem b) {
    return a.key > b.key || (a.key == b.key && a.index > b.index);
}

kernel void buildDepthKeys(constant Splat* splats [[buffer(0)]],
                           device SortItem* sortItems [[buffer(1)]],
                           constant Uniforms& uniforms [[buffer(2)]],
                           uint id [[thread_position_in_grid]]) {
    if (id >= uniforms.sortCount) {
        return;
    }
    if (id >= uniforms.splatCount) {
        sortItems[id] = SortItem{0xffffffffu, id};
        return;
    }
    float3 center = rotateYThenX(splats[id].position.xyz, uniforms);
    center.z += uniforms.cameraDistance;
    sortItems[id] = SortItem{depthSortKey(center.z, uniforms), id};
}

kernel void bitonicSortDepthKeys(device SortItem* sortItems [[buffer(0)]],
                                 constant SortParams& params [[buffer(1)]],
                                 uint id [[thread_position_in_grid]]) {
    if (id >= params.count) {
        return;
    }

    uint other = id ^ params.j;
    if (other <= id || other >= params.count) {
        return;
    }

    SortItem currentItem = sortItems[id];
    SortItem otherItem = sortItems[other];
    bool ascending = (id & params.k) == 0u;
    bool swapItems = ascending ? shouldSwap(currentItem, otherItem) : shouldSwap(otherItem, currentItem);
    if (swapItems) {
        sortItems[id] = otherItem;
        sortItems[other] = currentItem;
    }
}

vertex VertexOut splatVertex(uint vertexId [[vertex_id]],
                             uint instanceId [[instance_id]],
                             constant Splat* splats [[buffer(0)]],
                             constant Uniforms& uniforms [[buffer(1)]],
                             constant SortItem* sortItems [[buffer(2)]]) {
    uint sortedIndex = sortItems[instanceId].index;
    constant Splat& splat = splats[sortedIndex];
    float2 local = float2(vertexId == 0 || vertexId == 2 ? -1.0 : 1.0,
                          vertexId < 2 ? -1.0 : 1.0);

    float3 center3 = rotateYThenX(splat.position.xyz, uniforms);
    center3.z += uniforms.cameraDistance;
    if (center3.z <= uniforms.nearPlane) {
        VertexOut clipped;
        clipped.position = float4(2.0, 2.0, 1.0, 1.0);
        clipped.local = local;
        clipped.color = float4(0.0);
        clipped.viewZ = center3.z;
        return clipped;
    }

    float2 centerScreen = projectViewPoint(center3, uniforms);
    float3 axis3A = rotateYThenX(splat.axis0.xyz, uniforms);
    float3 axis3B = rotateYThenX(splat.axis1.xyz, uniforms);
    float3 axis3C = rotateYThenX(splat.axis2.xyz, uniforms);
    float2 a = projectViewPoint(center3 + axis3A, uniforms) - centerScreen;
    float2 b = projectViewPoint(center3 + axis3B, uniforms) - centerScreen;
    float2 c = projectViewPoint(center3 + axis3C, uniforms) - centerScreen;

    float2 axis0 = a;
    float2 axis1 = b;
    float len0 = dot(axis0, axis0);
    float len1 = dot(axis1, axis1);
    if (len1 > len0) {
        float2 tmpAxis = axis0;
        axis0 = axis1;
        axis1 = tmpAxis;
        float tmpLen = len0;
        len0 = len1;
        len1 = tmpLen;
    }
    float len2 = dot(c, c);
    if (len2 > len0) {
        axis1 = axis0;
        axis0 = c;
    } else if (len2 > len1) {
        axis1 = c;
    }
    if (dot(axis0, axis0) < 0.25) {
        axis0 = float2(1.0, 0.0);
    }
    if (dot(axis1, axis1) < 0.25) {
        axis1 = float2(0.0, 1.0);
    }

    axis0 *= 2.65;
    axis1 *= 2.65;
    float2 screen = centerScreen + axis0 * local.x + axis1 * local.y;
    float2 clip = float2(screen.x / uniforms.viewportSize.x * 2.0 - 1.0,
                         1.0 - screen.y / uniforms.viewportSize.y * 2.0);
    float depth = clamp((center3.z - uniforms.nearPlane) / max(0.0001, uniforms.farPlane - uniforms.nearPlane), 0.0, 1.0);

    VertexOut out;
    out.position = float4(clip, depth, 1.0);
    out.local = local;
    out.color = splat.color;
    out.viewZ = center3.z;
    return out;
}

vertex VertexOut splatVertexFast(uint vertexId [[vertex_id]],
                                 uint instanceId [[instance_id]],
                                 constant Splat* splats [[buffer(0)]],
                                 constant Uniforms& uniforms [[buffer(1)]]) {
    constant Splat& splat = splats[instanceId];
    float2 local = float2(vertexId == 0 || vertexId == 2 ? -1.0 : 1.0,
                          vertexId < 2 ? -1.0 : 1.0);

    float3 center3 = rotateYThenX(splat.position.xyz, uniforms);
    center3.z += uniforms.cameraDistance;
    if (center3.z <= uniforms.nearPlane) {
        VertexOut clipped;
        clipped.position = float4(2.0, 2.0, 1.0, 1.0);
        clipped.local = local;
        clipped.color = float4(0.0);
        clipped.viewZ = center3.z;
        return clipped;
    }

    float2 centerScreen = projectViewPoint(center3, uniforms);
    float3 axis3A = rotateYThenX(splat.axis0.xyz, uniforms);
    float3 axis3B = rotateYThenX(splat.axis1.xyz, uniforms);
    float3 axis3C = rotateYThenX(splat.axis2.xyz, uniforms);
    float2 a = projectViewPoint(center3 + axis3A, uniforms) - centerScreen;
    float2 b = projectViewPoint(center3 + axis3B, uniforms) - centerScreen;
    float2 c = projectViewPoint(center3 + axis3C, uniforms) - centerScreen;

    float2 axis0 = a;
    float2 axis1 = b;
    float len0 = dot(axis0, axis0);
    float len1 = dot(axis1, axis1);
    if (len1 > len0) {
        float2 tmpAxis = axis0;
        axis0 = axis1;
        axis1 = tmpAxis;
        float tmpLen = len0;
        len0 = len1;
        len1 = tmpLen;
    }
    float len2 = dot(c, c);
    if (len2 > len0) {
        axis1 = axis0;
        axis0 = c;
    } else if (len2 > len1) {
        axis1 = c;
    }
    if (dot(axis0, axis0) < 0.25) {
        axis0 = float2(1.0, 0.0);
    }
    if (dot(axis1, axis1) < 0.25) {
        axis1 = float2(0.0, 1.0);
    }

    axis0 *= 2.65;
    axis1 *= 2.65;
    float2 screen = centerScreen + axis0 * local.x + axis1 * local.y;
    float2 clip = float2(screen.x / uniforms.viewportSize.x * 2.0 - 1.0,
                         1.0 - screen.y / uniforms.viewportSize.y * 2.0);
    float depth = clamp((center3.z - uniforms.nearPlane) / max(0.0001, uniforms.farPlane - uniforms.nearPlane), 0.0, 1.0);

    VertexOut out;
    out.position = float4(clip, depth, 1.0);
    out.local = local;
    out.color = splat.color;
    out.viewZ = center3.z;
    return out;
}

fragment half4 splatFragment(VertexOut in [[stage_in]]) {
    float r2 = dot(in.local, in.local);
    float alpha = in.color.a * exp(-r2 * 3.25);
    alpha *= 1.0 - smoothstep(0.88, 1.0, sqrt(r2));
    float3 rgb = in.color.rgb * alpha;
    return half4(half3(rgb), half(alpha));
}

fragment OitFragmentOut splatOitFragment(VertexOut in [[stage_in]]) {
    float r2 = dot(in.local, in.local);
    float alpha = in.color.a * exp(-r2 * 3.25);
    alpha *= 1.0 - smoothstep(0.88, 1.0, sqrt(r2));
    alpha = clamp(alpha, 0.0, 0.98);

    float depthWeight = clamp(6.0 / max(0.16, in.viewZ * in.viewZ), 0.04, 48.0);
    float weight = max(0.0001, alpha * depthWeight);

    OitFragmentOut out;
    out.accumulation = float4(in.color.rgb * alpha * weight, alpha * weight);
    out.revealage = float4(alpha, alpha, alpha, alpha);
    return out;
}

struct ResolveVertexOut {
    float4 position [[position]];
    float2 uv;
};

vertex ResolveVertexOut resolveVertex(uint vertexId [[vertex_id]]) {
    float2 position = float2(vertexId == 1 ? 3.0 : -1.0,
                             vertexId == 2 ? -3.0 : 1.0);
    ResolveVertexOut out;
    out.position = float4(position, 0.0, 1.0);
    out.uv = float2((position.x + 1.0) * 0.5, (1.0 - position.y) * 0.5);
    return out;
}

fragment half4 resolveOitFragment(ResolveVertexOut in [[stage_in]],
                                  texture2d<float> accumulation [[texture(0)]],
                                  texture2d<float> revealage [[texture(1)]]) {
    constexpr sampler pointSampler(coord::normalized, address::clamp_to_edge, filter::nearest);
    float4 accum = accumulation.sample(pointSampler, in.uv);
    float reveal = clamp(revealage.sample(pointSampler, in.uv).r, 0.0, 1.0);
    float3 splatColor = accum.a > 0.00001 ? accum.rgb / accum.a : float3(0.0);
    float3 background = float3(0.02, 0.03, 0.05);
    float3 resolved = splatColor * (1.0 - reveal) + background * reveal;
    return half4(half3(resolved), half(1.0));
}
)";

} // namespace

@interface MetalSplatView : MTKView <MTKViewDelegate, NSDraggingDestination>
- (instancetype)initWithFrame:(NSRect)frameRect;
- (void)openPath:(NSString*)path;
@end

@implementation MetalSplatView {
    id<MTLCommandQueue> _queue;
    id<MTLRenderPipelineState> _pipeline;
    id<MTLRenderPipelineState> _oitPipeline;
    id<MTLRenderPipelineState> _resolvePipeline;
    id<MTLComputePipelineState> _depthKeyPipeline;
    id<MTLComputePipelineState> _sortPipeline;
    id<MTLDepthStencilState> _depthState;
    id<MTLBuffer> _splatBuffer;
    id<MTLBuffer> _sortBuffer;
    id<MTLTexture> _oitAccumulationTexture;
    id<MTLTexture> _oitRevealageTexture;
    CGSize _oitTextureSize;
    uint32_t _sortCount;
    RenderMode _renderMode;
    bool _sortDirty;
    LoadedScene _scene;
    std::string _status;
    FrameStats _frameStats;
    CFTimeInterval _lastFrameTimestamp;
    double _fpsWindowSeconds;
    int _fpsWindowFrames;
    float _cameraYaw;
    float _cameraPitch;
    float _cameraZoom;
    Vec2 _cameraPan;
    DragMode _dragMode;
    NSPoint _lastDragPoint;
    NSTextField* _hud;
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
    _scene = CreateProceduralSplats();
    _status = "Ready. Fast direct Metal weighted OIT splats.";
    _cameraYaw = -0.72f;
    _cameraPitch = -0.38f;
    _cameraZoom = 1.0f;
    _cameraPan = {0.0f, 0.0f};
    _dragMode = DragMode::None;
    _lastDragPoint = NSZeroPoint;
    _lastFrameTimestamp = 0.0;
    _fpsWindowSeconds = 0.0;
    _fpsWindowFrames = 0;
    _sortCount = 0;
    _renderMode = RenderMode::FastWeightedOit;
    _sortDirty = true;
    _oitTextureSize = CGSizeZero;

    self.delegate = self;
    self.colorPixelFormat = MTLPixelFormatBGRA8Unorm;
    self.depthStencilPixelFormat = MTLPixelFormatDepth32Float;
    self.sampleCount = 1;
    self.preferredFramesPerSecond = 120;
    self.paused = NO;
    self.enableSetNeedsDisplay = NO;
    self.clearColor = MTLClearColorMake(0.02, 0.03, 0.05, 1.0);

    [self createPipeline];
    [self uploadScene];
    [self createHud];
    [self registerForDraggedTypes:@[NSPasteboardTypeFileURL]];
    return self;
}

- (BOOL)acceptsFirstResponder {
    return YES;
}

- (BOOL)becomeFirstResponder {
    return YES;
}

- (void)layout {
    [super layout];
    const CGFloat width = std::min<CGFloat>(900.0, std::max<CGFloat>(240.0, self.bounds.size.width - 28.0));
    _hud.frame = NSMakeRect(14.0, self.bounds.size.height - 204.0, width, 190.0);
}

- (void)createHud {
    _hud = [[NSTextField alloc] initWithFrame:NSMakeRect(14.0, 14.0, 860.0, 190.0)];
    _hud.editable = NO;
    _hud.selectable = NO;
    _hud.bezeled = NO;
    _hud.drawsBackground = YES;
    _hud.backgroundColor = [NSColor colorWithCalibratedRed:0.93 green:0.96 blue:0.98 alpha:0.94];
    _hud.textColor = [NSColor colorWithCalibratedRed:0.02 green:0.05 blue:0.08 alpha:1.0];
    _hud.font = [NSFont monospacedSystemFontOfSize:12.0 weight:NSFontWeightRegular];
    _hud.usesSingleLineMode = NO;
    _hud.lineBreakMode = NSLineBreakByWordWrapping;
    _hud.autoresizingMask = NSViewWidthSizable | NSViewMinYMargin;
    [self addSubview:_hud];
    [self updateHud];
}

- (void)createPipeline {
    NSError* error = nil;
    NSString* shaderSource = [NSString stringWithUTF8String:kMetalShaderSource];
    id<MTLLibrary> library = [self.device newLibraryWithSource:shaderSource options:nil error:&error];
    if (library == nil) {
        _status = std::string("Metal shader compile failed: ") + (error.localizedDescription.UTF8String ?: "unknown error");
        NSLog(@"%@", error.localizedDescription);
        return;
    }

    MTLRenderPipelineDescriptor* descriptor = [[MTLRenderPipelineDescriptor alloc] init];
    descriptor.vertexFunction = [library newFunctionWithName:@"splatVertex"];
    descriptor.fragmentFunction = [library newFunctionWithName:@"splatFragment"];
    descriptor.colorAttachments[0].pixelFormat = self.colorPixelFormat;
    descriptor.depthAttachmentPixelFormat = self.depthStencilPixelFormat;
    descriptor.colorAttachments[0].blendingEnabled = YES;
    descriptor.colorAttachments[0].rgbBlendOperation = MTLBlendOperationAdd;
    descriptor.colorAttachments[0].alphaBlendOperation = MTLBlendOperationAdd;
    descriptor.colorAttachments[0].sourceRGBBlendFactor = MTLBlendFactorOne;
    descriptor.colorAttachments[0].destinationRGBBlendFactor = MTLBlendFactorOneMinusSourceAlpha;
    descriptor.colorAttachments[0].sourceAlphaBlendFactor = MTLBlendFactorOne;
    descriptor.colorAttachments[0].destinationAlphaBlendFactor = MTLBlendFactorOneMinusSourceAlpha;
    _pipeline = [self.device newRenderPipelineStateWithDescriptor:descriptor error:&error];
    if (_pipeline == nil) {
        _status = std::string("Metal pipeline creation failed: ") + (error.localizedDescription.UTF8String ?: "unknown error");
        NSLog(@"%@", error.localizedDescription);
    }

    MTLRenderPipelineDescriptor* oitDescriptor = [[MTLRenderPipelineDescriptor alloc] init];
    oitDescriptor.vertexFunction = [library newFunctionWithName:@"splatVertexFast"];
    oitDescriptor.fragmentFunction = [library newFunctionWithName:@"splatOitFragment"];
    oitDescriptor.colorAttachments[0].pixelFormat = MTLPixelFormatRGBA16Float;
    oitDescriptor.colorAttachments[0].blendingEnabled = YES;
    oitDescriptor.colorAttachments[0].rgbBlendOperation = MTLBlendOperationAdd;
    oitDescriptor.colorAttachments[0].alphaBlendOperation = MTLBlendOperationAdd;
    oitDescriptor.colorAttachments[0].sourceRGBBlendFactor = MTLBlendFactorOne;
    oitDescriptor.colorAttachments[0].destinationRGBBlendFactor = MTLBlendFactorOne;
    oitDescriptor.colorAttachments[0].sourceAlphaBlendFactor = MTLBlendFactorOne;
    oitDescriptor.colorAttachments[0].destinationAlphaBlendFactor = MTLBlendFactorOne;
    oitDescriptor.colorAttachments[1].pixelFormat = MTLPixelFormatRGBA16Float;
    oitDescriptor.colorAttachments[1].blendingEnabled = YES;
    oitDescriptor.colorAttachments[1].rgbBlendOperation = MTLBlendOperationAdd;
    oitDescriptor.colorAttachments[1].alphaBlendOperation = MTLBlendOperationAdd;
    oitDescriptor.colorAttachments[1].sourceRGBBlendFactor = MTLBlendFactorZero;
    oitDescriptor.colorAttachments[1].destinationRGBBlendFactor = MTLBlendFactorOneMinusSourceColor;
    oitDescriptor.colorAttachments[1].sourceAlphaBlendFactor = MTLBlendFactorZero;
    oitDescriptor.colorAttachments[1].destinationAlphaBlendFactor = MTLBlendFactorOneMinusSourceAlpha;
    oitDescriptor.depthAttachmentPixelFormat = MTLPixelFormatInvalid;
    _oitPipeline = [self.device newRenderPipelineStateWithDescriptor:oitDescriptor error:&error];
    if (_oitPipeline == nil) {
        _status = std::string("Metal OIT pipeline creation failed: ") + (error.localizedDescription.UTF8String ?: "unknown error");
        NSLog(@"%@", error.localizedDescription);
    }

    MTLRenderPipelineDescriptor* resolveDescriptor = [[MTLRenderPipelineDescriptor alloc] init];
    resolveDescriptor.vertexFunction = [library newFunctionWithName:@"resolveVertex"];
    resolveDescriptor.fragmentFunction = [library newFunctionWithName:@"resolveOitFragment"];
    resolveDescriptor.colorAttachments[0].pixelFormat = self.colorPixelFormat;
    _resolvePipeline = [self.device newRenderPipelineStateWithDescriptor:resolveDescriptor error:&error];
    if (_resolvePipeline == nil) {
        _status = std::string("Metal OIT resolve pipeline creation failed: ") + (error.localizedDescription.UTF8String ?: "unknown error");
        NSLog(@"%@", error.localizedDescription);
    }

    id<MTLFunction> depthKeyFunction = [library newFunctionWithName:@"buildDepthKeys"];
    _depthKeyPipeline = [self.device newComputePipelineStateWithFunction:depthKeyFunction error:&error];
    if (_depthKeyPipeline == nil) {
        _status = std::string("Metal depth-key pipeline creation failed: ") + (error.localizedDescription.UTF8String ?: "unknown error");
        NSLog(@"%@", error.localizedDescription);
    }

    id<MTLFunction> sortFunction = [library newFunctionWithName:@"bitonicSortDepthKeys"];
    _sortPipeline = [self.device newComputePipelineStateWithFunction:sortFunction error:&error];
    if (_sortPipeline == nil) {
        _status = std::string("Metal sort pipeline creation failed: ") + (error.localizedDescription.UTF8String ?: "unknown error");
        NSLog(@"%@", error.localizedDescription);
    }

    MTLDepthStencilDescriptor* depthDescriptor = [[MTLDepthStencilDescriptor alloc] init];
    depthDescriptor.depthCompareFunction = MTLCompareFunctionLessEqual;
    depthDescriptor.depthWriteEnabled = NO;
    _depthState = [self.device newDepthStencilStateWithDescriptor:depthDescriptor];
}

- (void)uploadScene {
    std::vector<GpuSplat> gpuSplats;
    gpuSplats.reserve(_scene.splats.size());
    for (const auto& splat : _scene.splats) {
        gpuSplats.push_back({
            {splat.position.x, splat.position.y, splat.position.z, 0.0f},
            {splat.axis0.x, splat.axis0.y, splat.axis0.z, 0.0f},
            {splat.axis1.x, splat.axis1.y, splat.axis1.z, 0.0f},
            {splat.axis2.x, splat.axis2.y, splat.axis2.z, 0.0f},
            {splat.color.r, splat.color.g, splat.color.b, splat.color.a},
        });
    }

    const size_t byteSize = gpuSplats.size() * sizeof(GpuSplat);
    _splatBuffer = byteSize == 0 ? nil : [self.device newBufferWithBytes:gpuSplats.data() length:byteSize options:MTLResourceStorageModeShared];
    if (_splatBuffer == nil && byteSize > 0) {
        _status = "Could not allocate Metal splat buffer.";
    }

    if (_scene.splats.empty()) {
        _sortCount = 0;
        _sortBuffer = nil;
        return;
    }
    const auto splatCount = static_cast<uint32_t>(std::min<size_t>(_scene.splats.size(), std::numeric_limits<uint32_t>::max() / 2u));
    _sortCount = NextPowerOfTwo(splatCount);
    _sortBuffer = [self.device newBufferWithLength:static_cast<NSUInteger>(_sortCount) * sizeof(GpuSortItem) options:MTLResourceStorageModePrivate];
    if (_sortBuffer == nil) {
        _status = "Could not allocate Metal depth-sort buffer.";
    }
    _sortDirty = true;
}

- (void)ensureOitTexturesForSize:(CGSize)drawableSize {
    const NSUInteger width = static_cast<NSUInteger>(std::max<CGFloat>(1.0, drawableSize.width));
    const NSUInteger height = static_cast<NSUInteger>(std::max<CGFloat>(1.0, drawableSize.height));
    if (_oitAccumulationTexture != nil &&
        _oitRevealageTexture != nil &&
        static_cast<NSUInteger>(_oitTextureSize.width) == width &&
        static_cast<NSUInteger>(_oitTextureSize.height) == height) {
        return;
    }

    MTLTextureDescriptor* descriptor = [MTLTextureDescriptor texture2DDescriptorWithPixelFormat:MTLPixelFormatRGBA16Float width:width height:height mipmapped:NO];
    descriptor.usage = MTLTextureUsageRenderTarget | MTLTextureUsageShaderRead;
    descriptor.storageMode = MTLStorageModePrivate;
    _oitAccumulationTexture = [self.device newTextureWithDescriptor:descriptor];
    _oitRevealageTexture = [self.device newTextureWithDescriptor:descriptor];
    _oitTextureSize = CGSizeMake(width, height);
    if (_oitAccumulationTexture == nil || _oitRevealageTexture == nil) {
        _status = "Could not allocate weighted OIT render targets.";
    }
}

- (void)openPath:(NSString*)path {
    if (path == nil) {
        return;
    }
    try {
        _scene = LoadScene(std::filesystem::path(ToStdString(path)));
        [self uploadScene];
        _status = "Loaded " + ToStdString(path);
        self.window.title = ToNSString(_scene.name);
    } catch (const std::exception& ex) {
        _status = std::string("Load failed: ") + ex.what();
    }
    [self updateHud];
}

- (void)drawInMTKView:(MTKView*)view {
    const auto encodeStart = Clock::now();
    const CFTimeInterval frameStart = CACurrentMediaTime();
    if (_lastFrameTimestamp > 0.0) {
        const double deltaSeconds = frameStart - _lastFrameTimestamp;
        if (deltaSeconds > 0.0 && deltaSeconds < 0.250) {
            _fpsWindowSeconds += deltaSeconds;
            _fpsWindowFrames++;
            if (_fpsWindowSeconds >= 0.5 && _fpsWindowFrames > 0) {
                _frameStats.fps = static_cast<double>(_fpsWindowFrames) / _fpsWindowSeconds;
                _fpsWindowSeconds = 0.0;
                _fpsWindowFrames = 0;
            }
        }
    }
    _lastFrameTimestamp = frameStart;

    if (_queue == nil || _splatBuffer == nil || _scene.splats.empty()) {
        [self updateHud];
        return;
    }
    if (_renderMode == RenderMode::FastWeightedOit && (_oitPipeline == nil || _resolvePipeline == nil)) {
        [self updateHud];
        return;
    }
    if (_renderMode == RenderMode::SortedBackToFront && (_pipeline == nil || _depthKeyPipeline == nil || _sortPipeline == nil || _sortBuffer == nil)) {
        [self updateHud];
        return;
    }
    id<CAMetalDrawable> drawable = view.currentDrawable;
    MTLRenderPassDescriptor* passDescriptor = view.currentRenderPassDescriptor;
    if (drawable == nil || passDescriptor == nil) {
        return;
    }

    const CGSize drawableSize = view.drawableSize;
    MetalUniforms uniforms;
    uniforms.viewportSize = {static_cast<float>(drawableSize.width), static_cast<float>(drawableSize.height)};
    uniforms.center = {
        static_cast<float>(drawableSize.width) * 0.5f + _cameraPan.x,
        static_cast<float>(drawableSize.height) * 0.52f + _cameraPan.y,
    };
    uniforms.focalLength = static_cast<float>(std::min(drawableSize.width, drawableSize.height)) * 0.88f * _cameraZoom;
    uniforms.yaw = _cameraYaw;
    uniforms.pitch = _cameraPitch;
    uniforms.cameraDistance = 4.2f;
    uniforms.nearPlane = 0.04f;
    uniforms.farPlane = 80.0f;
    uniforms.splatCount = static_cast<uint32_t>(std::min<size_t>(_scene.splats.size(), std::numeric_limits<uint32_t>::max() / 2u));
    uniforms.sortCount = _sortCount;
    uniforms.cosYaw = std::cos(_cameraYaw);
    uniforms.sinYaw = std::sin(_cameraYaw);
    uniforms.cosPitch = std::cos(_cameraPitch);
    uniforms.sinPitch = std::sin(_cameraPitch);

    id<MTLCommandBuffer> commandBuffer = [_queue commandBuffer];
    int sortPasses = 0;
    bool sortUpdated = false;

    if (_renderMode == RenderMode::FastWeightedOit) {
        [self ensureOitTexturesForSize:drawableSize];
        if (_oitAccumulationTexture == nil || _oitRevealageTexture == nil) {
            [self updateHud];
            return;
        }

        MTLRenderPassDescriptor* oitPassDescriptor = [MTLRenderPassDescriptor renderPassDescriptor];
        oitPassDescriptor.colorAttachments[0].texture = _oitAccumulationTexture;
        oitPassDescriptor.colorAttachments[0].loadAction = MTLLoadActionClear;
        oitPassDescriptor.colorAttachments[0].storeAction = MTLStoreActionStore;
        oitPassDescriptor.colorAttachments[0].clearColor = MTLClearColorMake(0.0, 0.0, 0.0, 0.0);
        oitPassDescriptor.colorAttachments[1].texture = _oitRevealageTexture;
        oitPassDescriptor.colorAttachments[1].loadAction = MTLLoadActionClear;
        oitPassDescriptor.colorAttachments[1].storeAction = MTLStoreActionStore;
        oitPassDescriptor.colorAttachments[1].clearColor = MTLClearColorMake(1.0, 1.0, 1.0, 1.0);

        id<MTLRenderCommandEncoder> oitEncoder = [commandBuffer renderCommandEncoderWithDescriptor:oitPassDescriptor];
        [oitEncoder setRenderPipelineState:_oitPipeline];
        [oitEncoder setVertexBuffer:_splatBuffer offset:0 atIndex:0];
        [oitEncoder setVertexBytes:&uniforms length:sizeof(uniforms) atIndex:1];
        [oitEncoder drawPrimitives:MTLPrimitiveTypeTriangleStrip vertexStart:0 vertexCount:4 instanceCount:uniforms.splatCount];
        [oitEncoder endEncoding];

        id<MTLRenderCommandEncoder> resolveEncoder = [commandBuffer renderCommandEncoderWithDescriptor:passDescriptor];
        [resolveEncoder setRenderPipelineState:_resolvePipeline];
        [resolveEncoder setFragmentTexture:_oitAccumulationTexture atIndex:0];
        [resolveEncoder setFragmentTexture:_oitRevealageTexture atIndex:1];
        [resolveEncoder drawPrimitives:MTLPrimitiveTypeTriangle vertexStart:0 vertexCount:3];
        [resolveEncoder endEncoding];
    } else {
        if (_sortDirty) {
            id<MTLComputeCommandEncoder> computeEncoder = [commandBuffer computeCommandEncoder];
            const MTLSize threadgroupSize = MTLSizeMake(256, 1, 1);
            const MTLSize sortGridSize = MTLSizeMake(_sortCount, 1, 1);
            [computeEncoder setComputePipelineState:_depthKeyPipeline];
            [computeEncoder setBuffer:_splatBuffer offset:0 atIndex:0];
            [computeEncoder setBuffer:_sortBuffer offset:0 atIndex:1];
            [computeEncoder setBytes:&uniforms length:sizeof(uniforms) atIndex:2];
            [computeEncoder dispatchThreads:sortGridSize threadsPerThreadgroup:threadgroupSize];

            [computeEncoder setComputePipelineState:_sortPipeline];
            [computeEncoder setBuffer:_sortBuffer offset:0 atIndex:0];
            for (uint64_t k = 2u; k <= _sortCount; k <<= 1u) {
                for (uint64_t j = k >> 1u; j > 0u; j >>= 1u) {
                    MetalSortParams sortParams;
                    sortParams.count = _sortCount;
                    sortParams.k = static_cast<uint32_t>(k);
                    sortParams.j = static_cast<uint32_t>(j);
                    [computeEncoder setBytes:&sortParams length:sizeof(sortParams) atIndex:1];
                    [computeEncoder dispatchThreads:sortGridSize threadsPerThreadgroup:threadgroupSize];
                    ++sortPasses;
                }
            }
            [computeEncoder endEncoding];
            sortUpdated = true;
            _sortDirty = false;
        }

        id<MTLRenderCommandEncoder> encoder = [commandBuffer renderCommandEncoderWithDescriptor:passDescriptor];
        [encoder setRenderPipelineState:_pipeline];
        [encoder setDepthStencilState:_depthState];
        [encoder setVertexBuffer:_splatBuffer offset:0 atIndex:0];
        [encoder setVertexBytes:&uniforms length:sizeof(uniforms) atIndex:1];
        [encoder setVertexBuffer:_sortBuffer offset:0 atIndex:2];
        [encoder drawPrimitives:MTLPrimitiveTypeTriangleStrip vertexStart:0 vertexCount:4 instanceCount:uniforms.splatCount];
        [encoder endEncoding];
    }
    [commandBuffer presentDrawable:drawable];
    [commandBuffer commit];

    const auto encodeEnd = Clock::now();
    _frameStats.encodeMs = MillisecondsBetween(encodeStart, encodeEnd);
    _frameStats.frameMs = (CACurrentMediaTime() - frameStart) * 1000.0;
    _frameStats.sortPasses = sortPasses;
    _frameStats.sortUpdated = sortUpdated;
    [self updateHud];
}

- (void)mtkView:(MTKView*)view drawableSizeWillChange:(CGSize)size {
    (void)view;
    (void)size;
}

- (void)updateHud {
    const std::string loadedText = _scene.sourceSplatCount == static_cast<int>(_scene.splats.size())
        ? std::to_string(_scene.splats.size())
        : std::to_string(_scene.splats.size()) + "/" + std::to_string(_scene.sourceSplatCount);
    const uint64_t sceneBufferBytes = static_cast<uint64_t>(_scene.splats.size() * sizeof(GpuSplat));
    const uint64_t sortBufferBytes = static_cast<uint64_t>(static_cast<size_t>(_sortCount) * sizeof(GpuSortItem));
    const uint64_t oitBufferBytes = static_cast<uint64_t>(_oitTextureSize.width * _oitTextureSize.height * 16.0);
    const uint64_t bufferBytes = sceneBufferBytes + (_renderMode == RenderMode::FastWeightedOit ? oitBufferBytes : sortBufferBytes);
    const std::string modeText = _renderMode == RenderMode::FastWeightedOit
        ? "fast weighted OIT"
        : "sorted back-to-front";
    std::string text;
    text += _scene.name + "\n";
    text += _scene.source + " | " + loadedText + " splats | direct Metal " + modeText + "\n";
    text += _status + "\n";
    text += "FPS " + FormatFixed(_frameStats.fps, 1) + "   Frame " + FormatFixed(_frameStats.frameMs, 2) + " ms   Encode " + FormatFixed(_frameStats.encodeMs, 2) + " ms   Sort passes " + std::to_string(_frameStats.sortPasses) + "\n";
    text += "GPU buffers " + FormatMiB(bufferBytes) + "   Instances " + std::to_string(_scene.splats.size()) + "   Sort " + (_frameStats.sortUpdated ? "on" : "off") + "   Vertices " + std::to_string(_scene.splats.size() * 4u) + "\n";
    text += "Camera yaw " + FormatFixed(_cameraYaw * 180.0 / kPi, 0) + " pitch " + FormatFixed(_cameraPitch * 180.0 / kPi, 0) + " zoom " + FormatFixed(_cameraZoom, 2) + " pan " + FormatFixed(_cameraPan.x, 0) + "," + FormatFixed(_cameraPan.y, 0) + "\n";
    text += "Left drag orbit, right/middle drag pan, wheel zoom, F/R reset, M toggle mode, File > Open or drop PLY/SOG/SOB\n";
    text += _renderMode == RenderMode::FastWeightedOit
        ? "Fast path: perspective 3D splats with weighted blended OIT and no per-frame sort."
        : "Quality path: perspective 3D splats blended back-to-front after GPU depth sort.";
    _hud.stringValue = ToNSString(text);
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
    if (_dragMode != mode) {
        return;
    }
    NSPoint current = [self localPointForEvent:event];
    const float dx = static_cast<float>(current.x - _lastDragPoint.x);
    const float dy = static_cast<float>(current.y - _lastDragPoint.y);
    _lastDragPoint = current;
    if (mode == DragMode::Orbit) {
        _cameraYaw += dx * 0.010f;
        _cameraPitch = Clamp(_cameraPitch + dy * 0.010f, -1.35f, 1.35f);
        _sortDirty = true;
    } else if (mode == DragMode::Pan) {
        _cameraPan.x += dx;
        _cameraPan.y -= dy;
    }
}

- (void)endDrag {
    _dragMode = DragMode::None;
}

- (void)mouseDown:(NSEvent*)event { [self beginDrag:event mode:DragMode::Orbit]; }
- (void)mouseDragged:(NSEvent*)event { [self continueDrag:event expectedMode:DragMode::Orbit]; }
- (void)mouseUp:(NSEvent*)event { (void)event; [self endDrag]; }
- (void)rightMouseDown:(NSEvent*)event { [self beginDrag:event mode:DragMode::Pan]; }
- (void)rightMouseDragged:(NSEvent*)event { [self continueDrag:event expectedMode:DragMode::Pan]; }
- (void)rightMouseUp:(NSEvent*)event { (void)event; [self endDrag]; }
- (void)otherMouseDown:(NSEvent*)event { [self beginDrag:event mode:DragMode::Pan]; }
- (void)otherMouseDragged:(NSEvent*)event { [self continueDrag:event expectedMode:DragMode::Pan]; }
- (void)otherMouseUp:(NSEvent*)event { (void)event; [self endDrag]; }

- (void)scrollWheel:(NSEvent*)event {
    const float factor = std::exp(static_cast<float>(event.scrollingDeltaY) * 0.0018f);
    _cameraZoom = Clamp(_cameraZoom * factor, 0.18f, 6.0f);
}

- (void)keyDown:(NSEvent*)event {
    NSString* characters = event.charactersIgnoringModifiers.lowercaseString;
    if ([characters isEqualToString:@"f"] || [characters isEqualToString:@"r"]) {
        _cameraYaw = -0.72f;
        _cameraPitch = -0.38f;
        _cameraZoom = 1.0f;
        _cameraPan = {0.0f, 0.0f};
        _sortDirty = true;
        return;
    }
    if ([characters isEqualToString:@"m"]) {
        _renderMode = _renderMode == RenderMode::FastWeightedOit ? RenderMode::SortedBackToFront : RenderMode::FastWeightedOit;
        if (_renderMode == RenderMode::SortedBackToFront) {
            _sortDirty = true;
        }
        _frameStats.sortPasses = 0;
        _frameStats.sortUpdated = false;
        _status = _renderMode == RenderMode::FastWeightedOit
            ? "Fast mode: weighted blended OIT, no per-frame sort."
            : "Quality mode: GPU depth sort and back-to-front blending.";
        [self updateHud];
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
    MetalSplatView* _metalView;
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
    _metalView = [[MetalSplatView alloc] initWithFrame:frame];
    _window = [[NSWindow alloc]
        initWithContentRect:frame
        styleMask:(NSWindowStyleMaskTitled | NSWindowStyleMaskClosable | NSWindowStyleMaskMiniaturizable | NSWindowStyleMaskResizable)
        backing:NSBackingStoreBuffered
        defer:NO];
    _window.title = @"Gaussian Splats Metal C++";
    _window.restorable = NO;
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

- (BOOL)applicationShouldRestoreApplicationState:(NSApplication*)sender {
    (void)sender;
    return NO;
}

- (BOOL)applicationShouldSaveApplicationState:(NSApplication*)sender {
    (void)sender;
    return NO;
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

    NSMenu* appMenu = [[NSMenu alloc] initWithTitle:@"Gaussian Splats Metal C++"];
    [appMenu addItemWithTitle:@"Quit Gaussian Splats Metal C++" action:@selector(terminate:) keyEquivalent:@"q"];
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