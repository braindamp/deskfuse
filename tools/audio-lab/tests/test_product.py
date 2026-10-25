import json
import os
import subprocess
from pathlib import Path
import sys
import tempfile
import unittest
sys.path.insert(0,str(Path(__file__).resolve().parent.parent))
import bridge
import integration
import product_runner


class ProductTests(unittest.TestCase):
    def test_product_receipt_binds_managed_security_dependencies(self):
        with tempfile.TemporaryDirectory() as temp:
            root=Path(temp)
            base=root/'product';base.mkdir()
            artifacts={role:root/(role+'.bin') for role in integration.PRODUCT_ROLES}
            artifacts['product_agent']=base/'FocusWindow.Agent.exe'
            for role,file in artifacts.items(): file.write_bytes(role.encode())
            bundle=[{'path':'FocusWindow.Agent.exe','sha256':integration.sha256_file(artifacts['product_agent'])}]
            for name in ('FocusWindow.Agent.dll','FocusWindow.Core.dll','DeskFuse.Audio.dll',
                         'FocusWindow.Agent.deps.json','FocusWindow.Agent.runtimeconfig.json'):
                file=base/name;file.write_bytes(name.encode())
                bundle.append({'path':name,'sha256':integration.sha256_file(file)})
            receipt={'schema':1,'source_commit':'a'*40,
                     'capabilities':{**integration.REQUIRED_CAPS,**integration.PRODUCT_CAPS,
                                     'windows_engine_period_policies':['reference','minimum'],
                                     'windows_capture_policies':['reference','minimum']},
                     'artifacts':[{'role':role,'sha256':integration.sha256_file(file),
                                   'profile':'debug' if role=='product_android_apk' else 'release'}
                                  for role,file in artifacts.items()],
                     'product_agent_bundle':bundle}
            cfg={'build_receipt':str(root/'receipt.json')}
            def verify():
                (root/'receipt.json').write_text(json.dumps(receipt),encoding='utf-8')
                return integration.receipt_preflight(cfg,artifacts,checkout_head='a'*40,product=True)
            self.assertEqual(verify()['state'],'verified')
            (base/'FocusWindow.Core.dll').write_bytes(b'changed')
            self.assertIn('dependency hash mismatch',str(verify()['problems']))
            receipt['product_agent_bundle']=[]
            self.assertIn('dependency bundle',str(verify()['problems']))

    @unittest.skipUnless(os.name=='nt','Windows job ownership')
    def test_job_assignment_precedes_child_start_and_closes_cleanly(self):
        child=subprocess.Popen([sys.executable,'-u','-c',
            "import sys; line=sys.stdin.readline(); print('armed' if line=='start\\n' else 'blocked',flush=True); sys.stdin.readline()"],
            stdin=subprocess.PIPE,stdout=subprocess.PIPE,text=True,
            creationflags=getattr(subprocess,'CREATE_NO_WINDOW',0))
        job=product_runner.ProcessJob()
        try:
            job.assign(child)
            child.stdin.write('start\n');child.stdin.flush()
            self.assertEqual(child.stdout.readline().strip(),'armed')
            child.stdin.close()
            self.assertEqual(child.wait(timeout=5),0)
        finally:
            job.close()
            child.wait(timeout=5)
            child.stdout.close()

    def test_product_command_never_accepts_identity_or_certificate_shortcuts(self):
        cfg={key:str(Path('/example')/key) for key in ('product_agent','windows_sender','windows_capture','windows_source_control')}
        cmd=product_runner.command(cfg,'committed','minimum',30)
        self.assertIn('--audio-lab',cmd)
        self.assertIn('--native-audio-backend',cmd)
        self.assertNotIn('--auto-approve',cmd)
        self.assertNotIn('--single-session',cmd)
        self.assertNotIn('--peer-id',cmd)
        for backend,period,seconds in [('anything','minimum',30),('committed','anything',30),('committed','minimum',1800)]:
            with self.assertRaises(ValueError): product_runner.command(cfg,backend,period,seconds)

    def test_source_observations_are_distinct_and_not_acoustic_claims(self):
        value=bridge.parse_observed_lines('DIRECT_CONFIG driver_verified=1 driver_build=138077316354 current_period_frames=120 period_verified=1\nDIRECT_GEOMETRY geometry_qualified=1 packet_frames=120 notifications=2\nDONE committed source queued=80 expired=2 dropped=0\n')
        self.assertEqual(value['packet_frames'],120)
        self.assertEqual(value['current_period_frames'],120)
        self.assertEqual(value['expired'],2)
        self.assertNotIn('latency_ms',value)


if __name__=='__main__': unittest.main()
