cmake_minimum_required(VERSION 3.25)
project(MeshModelerSkiaNativeCpp LANGUAGES CXX OBJCXX)

if(NOT APPLE)
    message(FATAL_ERROR "MeshModeler.SkiaNative.Cpp is a macOS Metal sample.")
endif()

set(SKIANATIVE_REPO_ROOT "${CMAKE_CURRENT_LIST_DIR}/../.." CACHE PATH "SkiaNative.Avalonia repository root")
set(SKIANATIVE_SKIA_ROOT "${SKIANATIVE_REPO_ROOT}/external/skia" CACHE PATH "Skia source root")

if(NOT CMAKE_OSX_ARCHITECTURES)
    execute_process(COMMAND uname -m OUTPUT_VARIABLE SKIANATIVE_HOST_ARCH OUTPUT_STRIP_TRAILING_WHITESPACE)
    set(CMAKE_OSX_ARCHITECTURES "${SKIANATIVE_HOST_ARCH}" CACHE STRING "macOS target architecture" FORCE)
endif()

if(CMAKE_OSX_ARCHITECTURES MATCHES "arm64")
    set(SKIANATIVE_SKIA_ARCH "arm64")
elseif(CMAKE_OSX_ARCHITECTURES MATCHES "x86_64")
    set(SKIANATIVE_SKIA_ARCH "x64")
else()
    message(FATAL_ERROR "Unsupported CMAKE_OSX_ARCHITECTURES='${CMAKE_OSX_ARCHITECTURES}'. Use arm64 or x86_64.")
endif()

set(SKIANATIVE_SKIA_OUT "${SKIANATIVE_SKIA_ROOT}/out/${SKIANATIVE_SKIA_ARCH}" CACHE PATH "Skia GN output directory")

if(NOT EXISTS "${SKIANATIVE_SKIA_OUT}/libskia.a")
    message(FATAL_ERROR "Skia library not found at ${SKIANATIVE_SKIA_OUT}/libskia.a. Run ./eng/build-native.sh osx-arm64 or osx-x64 first.")
endif()

add_executable(MeshModelerSkiaNativeCpp MACOSX_BUNDLE
    src/main.mm)

target_compile_features(MeshModelerSkiaNativeCpp PRIVATE cxx_std_20)
target_include_directories(MeshModelerSkiaNativeCpp PRIVATE "${SKIANATIVE_SKIA_ROOT}")
target_compile_options(MeshModelerSkiaNativeCpp PRIVATE
    -fobjc-arc
    $<$<CONFIG:Release>:-O3;-DNDEBUG>)
target_link_options(MeshModelerSkiaNativeCpp PRIVATE
    "-Wl,-force_load,${SKIANATIVE_SKIA_OUT}/libskia.a")

file(GLOB SKIANATIVE_SKIA_PATHOPS_OBJECTS "${SKIANATIVE_SKIA_OUT}/obj/src/pathops/*.o")
target_link_libraries(MeshModelerSkiaNativeCpp PRIVATE
    ${SKIANATIVE_SKIA_PATHOPS_OBJECTS}
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
    "-framework CoreText")

set_target_properties(MeshModelerSkiaNativeCpp PROPERTIES
    OUTPUT_NAME "MeshModelerSkiaNativeCpp"
    MACOSX_BUNDLE_BUNDLE_NAME "MeshModeler SkiaNative C++"
    MACOSX_BUNDLE_GUI_IDENTIFIER "io.github.wieslawsoltes.MeshModelerSkiaNativeCpp")