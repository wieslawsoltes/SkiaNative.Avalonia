#!/usr/bin/env bash
set -euo pipefail

ROOT="$(cd "$(dirname "${BASH_SOURCE[0]}")/.." && pwd)"
if [ "$(uname -s)" != "Darwin" ]; then
  echo "macOS sample smoke test requires Darwin; current OS is $(uname -s)." >&2
  exit 2
fi

case "$(uname -m)" in
  arm64) RID="osx-arm64" ;;
  x86_64) RID="osx-x64" ;;
  *) echo "Unsupported macOS architecture: $(uname -m)" >&2; exit 2 ;;
esac

ARTIFACT_DIR="${SKIANATIVE_SMOKE_ARTIFACT_DIR:-$ROOT/artifacts/smoke}"
BACKEND="${SKIANATIVE_SAMPLE_BACKEND:-skianative}"
READY_TIMEOUT_SECONDS="${SKIANATIVE_SMOKE_READY_TIMEOUT_SECONDS:-30}"
CAPTURE_DELAY_SECONDS="${SKIANATIVE_SMOKE_CAPTURE_DELAY_SECONDS:-2}"
EXIT_MS="${SKIANATIVE_SMOKE_EXIT_MS:-8000}"
TIMESTAMP="$(date +%Y%m%d-%H%M%S)"
LOG_PATH="$ARTIFACT_DIR/sample-macos-$RID-$BACKEND-$TIMESTAMP.log"
SCREENSHOT_PATH="$ARTIFACT_DIR/sample-macos-$RID-$BACKEND-$TIMESTAMP.png"
NATIVE_LIBRARY="$ROOT/artifacts/native/$RID/libSkiaNativeAvalonia.dylib"
SAMPLE_PROJECT="$ROOT/samples/SkiaNative.Avalonia.Sample/SkiaNative.Avalonia.Sample.csproj"
SAMPLE_OUTPUT="$ROOT/samples/SkiaNative.Avalonia.Sample/bin/Debug/net10.0"

mkdir -p "$ARTIFACT_DIR"

if [ "$BACKEND" = "skianative" ] && [ ! -f "$NATIVE_LIBRARY" ]; then
  echo "Native library not found for $RID; building native asset first."
  SKIANATIVE_SKIP_SYNC_DEPS="${SKIANATIVE_SKIP_SYNC_DEPS:-1}" "$ROOT/eng/build-native.sh" "$RID"
fi

dotnet build "$SAMPLE_PROJECT" --no-restore

stage_avalonia_native() {
  local output_native_dir="$SAMPLE_OUTPUT/runtimes/osx/native"
  local output_native="$output_native_dir/libAvaloniaNative.dylib"
  if [ -f "$output_native" ]; then
    return
  fi

  local candidates=()
  if [ -n "${AVALONIA_NATIVE_LIBRARY_PATH:-}" ]; then
    candidates+=("$AVALONIA_NATIVE_LIBRARY_PATH")
  fi
  candidates+=("$ROOT/../Avalonia/Build/Products/Release/libAvalonia.Native.OSX.dylib")

  local package_candidate
  package_candidate="$(find "${HOME}/.nuget/packages/avalonia.native" -path "*/runtimes/osx/native/libAvaloniaNative.dylib" 2>/dev/null | sort | tail -n 1 || true)"
  if [ -n "$package_candidate" ]; then
    candidates+=("$package_candidate")
  fi

  for candidate in "${candidates[@]}"; do
    if [ -f "$candidate" ]; then
      mkdir -p "$output_native_dir"
      cp "$candidate" "$output_native"
      echo "Staged Avalonia native dependency: $candidate -> $output_native"
      return
    fi
  done

  echo "Could not find libAvaloniaNative.dylib. Set AVALONIA_NATIVE_LIBRARY_PATH or build Avalonia native macOS artifacts." >&2
  exit 1
}

stage_avalonia_native

echo "Launching sample smoke test. Log: $LOG_PATH"
(
  cd "$ROOT"
  SKIANATIVE_SMOKE=1 \
  SKIANATIVE_SAMPLE_BACKEND="$BACKEND" \
  SKIANATIVE_SMOKE_EXIT_MS="$EXIT_MS" \
  dotnet run --no-build --project "$SAMPLE_PROJECT" -- --skianative-smoke --backend "$BACKEND"
) >"$LOG_PATH" 2>&1 &
APP_PID=$!

cleanup() {
  if kill -0 "$APP_PID" >/dev/null 2>&1; then
    kill "$APP_PID" >/dev/null 2>&1 || true
  fi
}
trap cleanup EXIT

deadline=$((SECONDS + READY_TIMEOUT_SECONDS))
while ! grep -q "SKIANATIVE_SMOKE_READY" "$LOG_PATH" 2>/dev/null; do
  if ! kill -0 "$APP_PID" >/dev/null 2>&1; then
    echo "Sample exited before readiness marker. Log follows:" >&2
    cat "$LOG_PATH" >&2
    exit 1
  fi

  if [ "$SECONDS" -ge "$deadline" ]; then
    echo "Timed out waiting for SKIANATIVE_SMOKE_READY after ${READY_TIMEOUT_SECONDS}s. Log follows:" >&2
    cat "$LOG_PATH" >&2
    exit 1
  fi

  sleep 0.25
done

sleep "$CAPTURE_DELAY_SECONDS"
screencapture -x "$SCREENSHOT_PATH"

set +e
wait "$APP_PID"
APP_EXIT=$?
set -e
trap - EXIT

if [ "$APP_EXIT" -ne 0 ]; then
  echo "Sample exited with code $APP_EXIT. Log follows:" >&2
  cat "$LOG_PATH" >&2
  exit "$APP_EXIT"
fi

if [ ! -s "$SCREENSHOT_PATH" ]; then
  echo "Screenshot was not created or is empty: $SCREENSHOT_PATH" >&2
  exit 1
fi

if [ "$BACKEND" = "skianative" ] && ! grep -q "SKIANATIVE_FRAME" "$LOG_PATH"; then
  echo "No SKIANATIVE_FRAME diagnostics were emitted. Log follows:" >&2
  cat "$LOG_PATH" >&2
  exit 1
fi

echo "Smoke backend: $BACKEND"
echo "Smoke screenshot: $SCREENSHOT_PATH"
echo "Smoke log: $LOG_PATH"
