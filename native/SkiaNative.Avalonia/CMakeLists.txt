cmake_minimum_required(VERSION 3.25)
project(SkiaNativeAvalonia LANGUAGES CXX)

option(SKIANATIVE_WITH_SKIA "Build with Skia headers and library" ON)
set(SKIANATIVE_SKIA_ROOT "${CMAKE_CURRENT_LIST_DIR}/../../external/skia" CACHE PATH "Skia source root")
set(SKIANATIVE_SKIA_OUT "${SKIANATIVE_SKIA_ROOT}/out/${CMAKE_OSX_ARCHITECTURES}" CACHE PATH "Skia GN output directory")

add_library(SkiaNativeAvalonia SHARED
    src/SkiaNativeApi.cpp)

target_include_directories(SkiaNativeAvalonia
    PUBLIC include)

target_compile_features(SkiaNativeAvalonia PRIVATE cxx_std_20)
target_compile_definitions(SkiaNativeAvalonia PRIVATE $<$<BOOL:${SKIANATIVE_WITH_SKIA}>:SKIANATIVE_WITH_SKIA=1>)

if(SKIANATIVE_WITH_SKIA)
    target_include_directories(SkiaNativeAvalonia PRIVATE "${SKIANATIVE_SKIA_ROOT}")
    target_link_libraries(SkiaNativeAvalonia PRIVATE "${SKIANATIVE_SKIA_OUT}/libskia.a")
endif()

if(APPLE)
    target_link_libraries(SkiaNativeAvalonia PRIVATE
        "-framework Foundation"
        "-framework CoreFoundation"
        "-framework CoreGraphics"
        "-framework CoreServices"
        "-framework CoreText"
        "-framework Metal"
        "-framework QuartzCore")
endif()

set_target_properties(SkiaNativeAvalonia PROPERTIES
    OUTPUT_NAME "SkiaNativeAvalonia"
    CXX_VISIBILITY_PRESET hidden
    VISIBILITY_INLINES_HIDDEN YES)
