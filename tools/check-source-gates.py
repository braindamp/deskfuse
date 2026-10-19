#!/usr/bin/env python3
"""Non-hardware source gates. Run independently of Windows WDK or dotnet."""
from pathlib import Path
import re
ROOT = Path(__file__).resolve().parent.parent
core = (ROOT/'windows/agent/FocusWindow.Core/Display/DensityNegotiator.cs').read_text()
program = (ROOT/'windows/agent/FocusWindow.Agent/Program.cs').read_text()
cpp = (ROOT/'windows/shared/FocusShared.h').read_text()
cs = (ROOT/'windows/agent/FocusWindow.Core/Display/DriverContract.cs').read_text()
checks = {
 'undocumented DPI write defaults OFF': bool(re.search(r'AllowExperimentalScaleWrite\s*\{\s*get;\s*init;\s*\}\s*=\s*false;',core)),
 'write is gated': 'else if (!_options.AllowExperimentalScaleWrite)' in core,
 'probe failure cannot certify native': 'var windowMatch = false;' in core and 'var rulerOk = false;' in core and '&& rulerOk' in core,
 'probe CLI explicit opt-in': '--experimental-scale-write' in program,
 'driver v2 matches C#': '#define FOCUSIDD_PROTOCOL_VERSION 2u' in cpp and 'ProtocolVersion = 2;' in cs,
 'driver max pixels covers flagship': '#define FW_MAX_PIXELS 8294400u' in cpp and 1440*3120 <= 8294400,
 'driver raw section v2': 'Global\\\\FocusWindowSurface.v2' in cpp and 'Global\\FocusWindowSurface.v2' in cs,
 'EDID runtime not fixed': 'EdidBuilder' in (ROOT/'windows/driver/FocusIdd/Driver.cpp').read_text(),
 'Windows source present': (ROOT/'windows/agent/FocusWindow.Agent/Display/ProbeWindow.cs').exists(),
 'Android source present': (ROOT/'android/app/src/main/java/com/focuswindow/client/profile/PhoneProfile.kt').exists(),
}
for title,ok in checks.items(): print(('PASS' if ok else 'FAIL'), title)
if not all(checks.values()): raise SystemExit(1)
print(f'PASS {len(checks)} source gates (NOT hardware/runtime validation)')
