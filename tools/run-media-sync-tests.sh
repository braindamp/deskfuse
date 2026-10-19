#!/usr/bin/env bash
set -euo pipefail
ROOT="$(cd "$(dirname "$0")/.." && pwd)"
OUT="${OUT:-$(mktemp -d)}"
kotlinc "$ROOT/android/app/src/main/java/com/focuswindow/client/media/VideoSyncGate.kt" \
    "$ROOT/tools/media/VideoSyncGateCheck.kt" -include-runtime -d "$OUT/deskfuse-media-sync.jar"
java -cp "$OUT/deskfuse-media-sync.jar" com.focuswindow.client.media.VideoSyncGateCheck
