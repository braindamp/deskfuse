#!/usr/bin/env bash
set -euo pipefail
ROOT="$(cd "$(dirname "$0")/.." && pwd)"
mkdir -p "$ROOT/windows/tests"
cd "$ROOT/windows/driver/tests"
# Pure EDID/ABI logic only. NOT a WDK build, NOT proof of driver installation.
OUT="${OUT:-/tmp/desklink-phase01-tests}" bash ./run_tests.sh
python3 "$ROOT/tools/check-source-gates.py"
