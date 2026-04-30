cmake_minimum_required(VERSION 3.25)
project(GaussianSplatsMetalCpp LANGUAGES CXX OBJCXX)

if(NOT APPLE)
    message(FATAL_ERROR "GaussianSplats.Metal.Cpp is a macOS Metal sample.")
endif()

add_executable(GaussianSplatsMetalCpp MACOSX_BUNDLE
    src/main.mm)

target_compile_features(GaussianSplatsMetalCpp PRIVATE cxx_std_20)
target_compile_options(GaussianSplatsMetalCpp PRIVATE
    -fobjc-arc
    $<$<CONFIG:Release>:-O3;-DNDEBUG>)

target_link_libraries(GaussianSplatsMetalCpp PRIVATE
    z
    "-framework AppKit"
    "-framework Foundation"
    "-framework Metal"
    "-framework MetalKit"
    "-framework QuartzCore"
    "-framework UniformTypeIdentifiers"
    "-framework CoreFoundation"
    "-framework CoreGraphics"
    "-framework CoreServices"
    "-framework ImageIO")

set_target_properties(GaussianSplatsMetalCpp PROPERTIES
    OUTPUT_NAME "GaussianSplatsMetalCpp"
    MACOSX_BUNDLE_BUNDLE_NAME "Gaussian Splats Metal C++"
    MACOSX_BUNDLE_GUI_IDENTIFIER "io.github.wieslawsoltes.GaussianSplatsMetalCpp")