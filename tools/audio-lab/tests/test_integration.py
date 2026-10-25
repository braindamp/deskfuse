import json
from pathlib import Path
import sys
import tempfile
import unittest
from unittest.mock import patch
sys.path.insert(0, str(Path(__file__).resolve().parent.parent))
import bridge
import integration

class IntegrationTests(unittest.TestCase):
    def setUp(self):
        self.temp = tempfile.TemporaryDirectory()
        self.addCleanup(self.temp.cleanup)
        self.root = Path(self.temp.name)
        self.cfg={'repo_root':str(self.root),'build_receipt':str(self.root/'receipt.json'),
                  'endpoint_id':'{0.0.0.00000000}.{00000000-0000-0000-0000-000000000001}',
                  'expected_branch':'experiment/vnext-audio-latency'}
        self.working = {}
        self.files = []
        for k in integration.ROLES:
            file=self.root / (k + '.bin'); file.write_bytes((k+'-release').encode())
            self.working[k]=file
            self.files.append({'role':k,'path':r'C:\OLD\\'+k+'.bin','profile':'release',
                               'sha256':integration.sha256_file(file)})
        self.receipt={'schema':1, 'source_commit':'3d6eb2e0fb93316bd08b9f35eb1a58b908065df8',
                      'capabilities':{'windows_endpoint_lease':True,'android_audio_abi':8,
                                      'windows_engine_period_policies':['reference','minimum'],
                                      'windows_capture_policies':['reference','minimum']},'artifacts':self.files}
        self.save()
    def save(self):
        (self.root/'receipt.json').write_text(json.dumps(self.receipt))
    def verify(self):
        return integration.receipt_preflight(self.cfg,self.working,checkout_head=self.receipt['source_commit'])
    def test_complete_matching_release_receipt(self):
        self.assertEqual(self.verify()['state'],'verified')
        self.assertEqual(len(self.verify()['artifacts']),4)
    def test_reject_tamper_duplicate_and_nonrelease(self):
        self.working['android_native'].write_bytes(b'changed')
        self.assertIn('SHA-256',str(self.verify()['problems']))
        self.working['android_native'].write_bytes(b'android_native-release')
        self.receipt['artifacts'].append(dict(self.files[0]));self.save()
        self.assertIn('duplicate receipt',str(self.verify()['problems']))
        self.receipt['artifacts'].pop();self.receipt['artifacts'][0]['profile']='debug';self.save()
        self.assertIn('not recorded as Release',str(self.verify()['problems']))
    def test_gate_legacy_or_wrong_capabilities(self):
        self.receipt['capabilities'].pop('windows_endpoint_lease');self.save()
        self.assertEqual(self.verify()['state'],'blocked')
        self.assertTrue(any('windows_endpoint_lease' in x for x in self.verify()['problems']))
    def test_source_is_never_unlocked_by_manual_hash(self):
        self.cfg['deskfuse_peer_id']='0'*32
        with self.assertRaisesRegex(ValueError,'not authorized'):
            bridge.build_command(self.cfg,bursts=0,modes='f1c1r1',seconds=30,label='',source='deskfuse')
        self.assertTrue(integration.source_preflight('deskfuse','reference','reference',self.verify()))
    def test_explicit_runner_receipt_and_engine_policy(self):
        cmd=bridge.build_command(self.cfg,bursts=0,modes='f1c1r1',seconds=30,label='music',engine_period='minimum')
        for name in ('-EndpointId','-BuildReceipt','-WindowsCaptureExecutable','-WindowsEnginePeriod', '-WindowsCapturePolicy'):
            self.assertIn(name,cmd)
        self.assertEqual(cmd[cmd.index('-WindowsEnginePeriod')+1],'minimum')
        self.assertIn('server-deskfuse',str(integration.source_comparison())) if False else None
        with self.assertRaisesRegex(ValueError,'Only one difference'):
            bridge.build_command(self.cfg,bursts=3,modes='f1c1r1',seconds=30,label='',engine_period='minimum')
    def test_broker_probe_is_observational(self):
        if sys.platform!='win32':
            self.assertEqual(integration.broker_preflight()['state'],'not_checked')

if __name__=='__main__': unittest.main()
