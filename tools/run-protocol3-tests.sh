#!/usr/bin/env bash
set -euo pipefail
ROOT="$(cd "$(dirname "$0")/.." && pwd)"
OUT="${OUT:-$(mktemp -d)}"
mkdir -p "$OUT"
# No Android SDK required. Compile the exact Kotlin source used by the Android app.
kotlinc \
  "$ROOT/tools/protocol3/jvm-stubs/android/util/Base64.kt"   "$ROOT/tools/protocol3/jvm-stubs/android/net/Uri.kt"   "$ROOT/android/app/src/main/java/com/focuswindow/client/transport/PairingPayload.kt" \
  "$ROOT/android/app/src/main/java/com/focuswindow/client/transport/SecureChannel.kt" \
  "$ROOT/android/app/src/main/java/com/focuswindow/client/transport/SecureRecordWriter.kt" \
  "$ROOT/tools/protocol3/SecureRecordWriterCheck.kt" \
  "$ROOT/tools/protocol3/ProtocolV3KotlinCheck.kt" \
  -include-runtime -d "$OUT/deskfuse-protocol-v3-kotlin.jar"
java -cp "$OUT/deskfuse-protocol-v3-kotlin.jar" \
  com.focuswindow.protocol3.ProtocolV3KotlinCheck vectors \
  "$ROOT/tests/protocol3/protocol-v3-vectors.properties"
java -cp "$OUT/deskfuse-protocol-v3-kotlin.jar" \
  com.focuswindow.client.transport.SecureRecordWriterCheck \
  "$ROOT/tests/protocol3/protocol-v3-vectors.properties"
if command -v dotnet >/dev/null 2>&1; then
    DESKFUSE_KOTLIN_INTEROP_JAR="$OUT/deskfuse-protocol-v3-kotlin.jar" \
      dotnet test "$ROOT/windows/agent/FocusWindow.Tests/FocusWindow.Tests.csproj" \
      --filter FullyQualifiedName~ProtocolV3InteropTests --nologo
else
    echo 'C# .NET 8 unavailable: Kotlin/vector lane passed; Kotlin <-> C# live bridge NOT executed.'
fi
