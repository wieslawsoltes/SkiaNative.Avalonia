#!/usr/bin/env bash
set -euo pipefail

ROOT="$(cd "$(dirname "${BASH_SOURCE[0]}")/.." && pwd)"
if [ "$(uname -s)" != "Darwin" ]; then
  echo "macOS sample memory measurement requires Darwin; current OS is $(uname -s)." >&2
  exit 2
fi

case "$(uname -m)" in
  arm64) RID="osx-arm64" ;;
  x86_64) RID="osx-x64" ;;
  *) echo "Unsupported macOS architecture: $(uname -m)" >&2; exit 2 ;;
esac

ARTIFACT_DIR="${SKIANATIVE_MEMORY_ARTIFACT_DIR:-$ROOT/artifacts/memory}"
BACKEND="${SKIANATIVE_SAMPLE_BACKEND:-skianative}"
EXIT_MS="${SKIANATIVE_SMOKE_EXIT_MS:-20000}"
SAMPLE_COUNT="${SKIANATIVE_MEMORY_SAMPLE_COUNT:-80}"
SAMPLE_INTERVAL_SECONDS="${SKIANATIVE_MEMORY_SAMPLE_INTERVAL_SECONDS:-0.2}"
TIMESTAMP="$(date +%Y%m%d-%H%M%S)"
LOG_PATH="$ARTIFACT_DIR/sample-memory-$RID-$BACKEND-$TIMESTAMP.log"
RSS_PATH="$ARTIFACT_DIR/sample-memory-$RID-$BACKEND-$TIMESTAMP.rss.tsv"
SAMPLE_PROJECT="$ROOT/samples/SkiaNative.Avalonia.Sample/SkiaNative.Avalonia.Sample.csproj"
SAMPLE_OUTPUT="$ROOT/samples/SkiaNative.Avalonia.Sample/bin/Debug/net10.0"
SAMPLE_APP="$SAMPLE_OUTPUT/SkiaNative.Avalonia.Sample"
NATIVE_LIBRARY="$ROOT/artifacts/native/$RID/libSkiaNativeAvalonia.dylib"

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

echo "Launching sample memory measurement. Log: $LOG_PATH"
(
  cd "$ROOT"
  SKIANATIVE_SMOKE=1 \
  SKIANATIVE_SMOKE_INTERACTIONS="${SKIANATIVE_SMOKE_INTERACTIONS:-1}" \
  SKIANATIVE_SAMPLE_BACKEND="$BACKEND" \
  SKIANATIVE_SMOKE_EXIT_MS="$EXIT_MS" \
  "$SAMPLE_APP" --skianative-smoke --backend "$BACKEND"
) >"$LOG_PATH" 2>&1 &
LAUNCH_PID=$!
TARGET_PID="$LAUNCH_PID"

cleanup() {
  if kill -0 "$TARGET_PID" >/dev/null 2>&1; then
    kill "$TARGET_PID" >/dev/null 2>&1 || true
  fi

  if kill -0 "$LAUNCH_PID" >/dev/null 2>&1; then
    kill "$LAUNCH_PID" >/dev/null 2>&1 || true
  fi
}
trap cleanup EXIT

deadline=$((SECONDS + 30))
while ! grep -q "SKIANATIVE_SMOKE_READY" "$LOG_PATH" 2>/dev/null; do
  if ! kill -0 "$LAUNCH_PID" >/dev/null 2>&1; then
    echo "Sample exited before readiness marker. Log follows:" >&2
    cat "$LOG_PATH" >&2
    exit 1
  fi

  if [ "$SECONDS" -ge "$deadline" ]; then
    echo "Timed out waiting for SKIANATIVE_SMOKE_READY after 30s. Log follows:" >&2
    cat "$LOG_PATH" >&2
    exit 1
  fi

  sleep 0.1
done

READY_PID="$(sed -n 's/.*SKIANATIVE_SMOKE_READY pid=\([0-9][0-9]*\).*/\1/p' "$LOG_PATH" | tail -n 1)"
if [ -n "$READY_PID" ]; then
  TARGET_PID="$READY_PID"
fi

printf 'sample\tphase\trss_kb\tvsz_kb\n' >"$RSS_PATH"
peak_rss=0
peak_sample=0

for i in $(seq 0 "$SAMPLE_COUNT"); do
  if ! ps -p "$TARGET_PID" >/dev/null 2>&1; then
    break
  fi

  stats="$(ps -o rss= -o vsz= -p "$TARGET_PID" | awk '{print $1"\t"$2}')"
  rss="$(printf '%s' "$stats" | cut -f1)"
  if [ -n "$rss" ] && [ "$rss" -gt "$peak_rss" ]; then
    peak_rss="$rss"
    peak_sample="$i"
  fi

  phase="$(grep 'SKIANATIVE_SMOKE_MARK' "$LOG_PATH" | tail -n 1 | sed -n 's/.*phase=\([^ ]*\).*/\1/p')"
  printf '%s\t%s\t%s\n' "$i" "${phase:-none}" "$stats" >>"$RSS_PATH"

  case "$i" in
    5) vmmap --summary "$TARGET_PID" >"$ARTIFACT_DIR/sample-memory-$RID-$BACKEND-$TIMESTAMP.initial.vmmap.txt" 2>/dev/null || true ;;
    30) vmmap --summary "$TARGET_PID" >"$ARTIFACT_DIR/sample-memory-$RID-$BACKEND-$TIMESTAMP.after-interactions.vmmap.txt" 2>/dev/null || true ;;
    70) vmmap --summary "$TARGET_PID" >"$ARTIFACT_DIR/sample-memory-$RID-$BACKEND-$TIMESTAMP.idle.vmmap.txt" 2>/dev/null || true ;;
  esac

  sleep "$SAMPLE_INTERVAL_SECONDS"
done

set +e
wait "$LAUNCH_PID"
APP_EXIT=$?
set -e
trap - EXIT

if [ "$APP_EXIT" -ne 0 ]; then
  echo "Sample exited with code $APP_EXIT. Log follows:" >&2
  cat "$LOG_PATH" >&2
  exit "$APP_EXIT"
fi

echo "Memory backend: $BACKEND"
echo "Peak RSS: ${peak_rss} KB at sample ${peak_sample}"
echo "Memory samples: $RSS_PATH"
echo "Smoke log: $LOG_PATH"
echo "Latest smoke markers:"
grep "SKIANATIVE_SMOKE_MARK" "$LOG_PATH" | tail -n 8 || true
echo "Latest frame diagnostics:"
grep "SKIANATIVE_FRAME" "$LOG_PATH" | tail -n 8 || true
