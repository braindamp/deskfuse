#!/usr/bin/env bash
# Builds and runs the driver's pure-logic tests with a plain C++ compiler.
# This covers EDID synthesis and the shared-memory contract. It does NOT cover the
# IddCx/WDF/D3D code in Driver.cpp, which needs the Windows Driver Kit (see ../README.md).
set -euo pipefail
cd "$(dirname "$0")"
OUT="${OUT:-/tmp/focus-driver-tests}"
mkdir -p "$OUT"
CXX="${CXX:-g++}"
FLAGS="-std=c++17 -Wall -Wextra -Werror -O1 -I shim"
echo "== edid_test =="
$CXX $FLAGS edid_test.cpp ../FocusIdd/Edid.cpp -o "$OUT/edid_test"
"$OUT/edid_test"
echo
echo "== contract_test =="
$CXX $FLAGS contract_test.cpp -o "$OUT/contract_test"
"$OUT/contract_test" > "$OUT/contract.generated.txt"
cp "$OUT/contract.generated.txt" "$(dirname "$0")/../../tests/contract.generated.txt" 2>/dev/null || true
wc -l "$OUT/contract.generated.txt"
echo "wrote $OUT/contract.generated.txt"
