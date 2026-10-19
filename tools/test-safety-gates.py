#!/usr/bin/env python3
"""Non-Windows static safety checks; NOT a substitute for Windows runtime testing."""
from pathlib import Path
p=Path(__file__).resolve().parent
runner=(p/'safe-driver-proof.ps1').read_text()
watchdog=(p/'safety-watchdog.ps1').read_text()
rollback=(p/'rollback-focusidd.ps1').read_text()
boot=(p/'safety-boot-recovery.ps1').read_text()
entry=(p/'run-proof.ps1').read_text()
checks={
  'SYSTEM watchdog before PnP': runner.index('Start-ScheduledTask -TaskName $taskName') < runner.index('/add-driver $inf'),
  'watchdog verify before PnP': runner.index("State -ne 'Running'") < runner.index('/add-driver $inf'),
  'boot recovery before PnP': runner.index('Register-ScheduledTask -TaskName $bootTaskName') < runner.index('/add-driver $inf'),
  'no Windows test-signing adjustment': all('bcdedit' not in s.lower() for s in (runner,watchdog,rollback,boot)),
  'no broad PnP device wildcard deletion': '/remove-device $instanceId' in rollback and '/remove-device *' not in rollback,
  'scoped hardware identity': '(?i)^ROOT\\\\FOCUSIDD\\\\[^\\\\]+$' in rollback,
  'three UI confirmations': all(x in runner for x in ('@{Index=1;AtMs=60000}','@{Index=2;AtMs=180000}','@{Index=3;AtMs=300000}')),
  'three unattended expiration gates': all(x in watchdog for x in ('@{Step=1;DeadlineMs=85000}','@{Step=2;DeadlineMs=205000}','@{Step=3;DeadlineMs=325000}')),
  'async watchdog via SYSTEM': "New-ScheduledTaskPrincipal -UserId 'SYSTEM'" in runner,
  'default driver removal on success': "Write-Mark 'abort.flag'" in runner and "Write-Mark 'done.flag'" in runner,
  'elevated install only': 'Assert-Admin' in runner,
  'direct proof uses safe runner': "'safe-driver-proof.ps1'" in entry,
  'boot recovery attempts cleanup': "'rollback-focusidd.ps1'" in boot,
  'staged driver not force deleted': '/delete-driver' not in runner+watchdog+rollback,
}
for k,ok in checks.items(): print(('PASS' if ok else 'FAIL')+' '+k)
if not all(checks.values()): raise SystemExit(1)
