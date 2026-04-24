#!/usr/bin/env bash
set -euo pipefail

ROOT="$(cd "$(dirname "${BASH_SOURCE[0]}")/.." && pwd)"
SKIA_ROOT="$ROOT/external/skia"
if [ "$#" -eq 0 ]; then
  RIDS=(osx-arm64 osx-x64)
else
  RIDS=("$@")
fi

if [ ! -d "$SKIA_ROOT/.git" ] && [ ! -f "$SKIA_ROOT/.git" ]; then
  git -C "$ROOT" submodule update --init external/skia
fi

git -C "$SKIA_ROOT" remote set-url origin https://skia.googlesource.com/skia

if [ ! -x "$SKIA_ROOT/bin/gn" ]; then
  (cd "$SKIA_ROOT" && python3 bin/fetch-gn)
fi

if [ "${SKIANATIVE_SKIP_SYNC_DEPS:-0}" != "1" ]; then
  if [ -d "$SKIA_ROOT/third_party/externals/libpng/.git" ]; then
    git -C "$SKIA_ROOT/third_party/externals/libpng" remote set-url origin https://skia.googlesource.com/third_party/libpng.git
  fi

  # Avoid user-level git URL rewrites changing skia.googlesource.com dependencies to
  # incompatible mirrors during Skia dependency sync.
  if ! (cd "$SKIA_ROOT" && GIT_CONFIG_GLOBAL=/dev/null python3 tools/git-sync-deps); then
    echo "warning: Skia dependency sync did not complete; continuing because the configured build may not require the failed optional dependencies." >&2
    echo "warning: set SKIANATIVE_REQUIRE_FULL_SYNC_DEPS=1 to treat sync failures as fatal." >&2
    if [ "${SKIANATIVE_REQUIRE_FULL_SYNC_DEPS:-0}" = "1" ]; then
      exit 1
    fi
  fi
fi

for rid in "${RIDS[@]}"; do
  case "$rid" in
    osx-arm64) skia_arch="arm64"; cmake_arch="arm64" ;;
    osx-x64) skia_arch="x64"; cmake_arch="x86_64" ;;
    *) echo "Unsupported RID: $rid" >&2; exit 2 ;;
  esac

  skia_out="$SKIA_ROOT/out/$skia_arch"
  cmake_out="$ROOT/artifacts/cmake/$rid"
  native_out="$ROOT/artifacts/native/$rid"
  mkdir -p "$native_out"

  (cd "$SKIA_ROOT" && bin/gn gen "$skia_out" --args="target_os=\"mac\" target_cpu=\"$skia_arch\" is_debug=false is_official_build=true skia_use_metal=true skia_use_gl=false skia_use_vulkan=false skia_use_dawn=false skia_enable_tools=false skia_enable_ganesh=true skia_use_system_expat=false skia_use_system_harfbuzz=false skia_use_system_icu=false skia_use_system_libjpeg_turbo=false skia_use_system_libpng=false skia_use_system_libwebp=false skia_use_system_zlib=false")
  ninja -C "$skia_out" skia

  cmake -S "$ROOT/native/SkiaNative.Avalonia" -B "$cmake_out" \
    -DCMAKE_BUILD_TYPE=Release \
    -DCMAKE_OSX_ARCHITECTURES="$cmake_arch" \
    -DSKIANATIVE_WITH_SKIA=ON \
    -DSKIANATIVE_SKIA_ROOT="$SKIA_ROOT" \
    -DSKIANATIVE_SKIA_OUT="$skia_out"
  cmake --build "$cmake_out" --config Release
  cp "$cmake_out/libSkiaNativeAvalonia.dylib" "$native_out/libSkiaNativeAvalonia.dylib"
done
