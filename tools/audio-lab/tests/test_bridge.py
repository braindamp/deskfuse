import http.client
from pathlib import Path
import json
import os
import sys
import threading
import unittest
sys.path.insert(0, str(Path(__file__).resolve().parent.parent))
import bridge


class BridgeTests(unittest.TestCase):
    def test_one_factor_modes(self):
        for bursts, modes in ((0,'f1c1r1'), (2,'f1c1r1'), (3,'f1c1r1'),
                              (0,'f0c1r1'), (0,'f1c0r1'), (0,'f1c1r0')):
            cmd = bridge.build_command({}, bursts=bursts, modes=modes, seconds=30, label='test_01')
            self.assertIn('-AndroidBufferBursts', cmd)
            self.assertEqual(cmd[cmd.index('-Modes')+1], modes)
        for bursts, modes in ((3,'f0c1r1'),(0,'f0c0r1'),(2,'f1c1r0')):
            with self.assertRaisesRegex(ValueError, 'Only one difference'):
                bridge.build_command({},bursts=bursts,modes=modes,seconds=30,label='')

    def test_reject_arbitrary_arguments(self):
        bad = [(-1,'f1c1r1',30,''), (4,'f1c1r1',30,''), (0,'f1c1r2',30,''),
               (0,'f1c1r1',300,''), (0,'f1c1r1',30,'hello world; rm -rf /')]
        for bursts,modes,seconds,label in bad:
            with self.assertRaises(ValueError):
                bridge.build_command({},bursts=bursts,modes=modes,seconds=seconds,label=label)

    def test_parse_only_observed_counters(self):
        txt = ('AUDIO_BACKEND performance=10 buffer_frames=4104 capacity_frames=4104 xruns=0\n'
               'TRACE Android RECONSTRUCTION clipped_samples=0 xrun_count=0 last_callback_frames=960 primed=1\n'
               'AUDIO Android speaker accepted_frames=1200 underrun_events=2 reprime_events=2 stream_error=0\n'
               'DONE Windows WASAPI source pre_quic_missing_frames=240 qpc_future=0\n'
               'SCRIPT_SECRET=oops\n')
        result = bridge.parse_observed_lines(txt)
        self.assertEqual(result['buffer_frames'],4104)
        self.assertEqual(result['underrun_events'],2)
        self.assertEqual(result['pre_quic_missing_frames'],240)
        self.assertNotIn('SCRIPT_SECRET',result)

    def test_http_same_origin_controls_and_static_safety(self):
        server = bridge.LabServer(('127.0.0.1',0),bridge.Runner({}))
        th = threading.Thread(target=server.serve_forever,daemon=True);th.start()
        try:
            port=server.server_port
            def req(method, path, headers=None, body=None):
                conn = http.client.HTTPConnection('127.0.0.1',port,timeout=5)
                headers=dict(headers or {})
                headers['Host']=f'127.0.0.1:{port}'
                conn.request(method,path,body=body,headers=headers)
                response=conn.getresponse();status=response.status
                payload=response.read(); headers=dict(response.getheaders());conn.close()
                return status,payload,headers
            status,payload,headers=req('GET','/api/status')
            data=json.loads(payload)
            self.assertEqual(status,200)
            self.assertFalse(data['running'])
            self.assertEqual(data['supported']['buffer_bursts'],[0,2,3])
            self.assertIn('nosniff', headers.get('X-Content-Type-Options',''))
            status,_,_=req('GET','/config.example.json')
            self.assertEqual(status,404)
            status,body,_=req('POST','/api/start',headers={'Origin':'https://attacker.example',
                                 'X-DeskFuse-Lab-Token':data['token']},body='{}')
            self.assertEqual(status,403)
            status,body,_=req('POST','/api/start',headers={'Origin':f'http://127.0.0.1:{port}',
                                 'X-DeskFuse-Lab-Token':'wrong'},body='{}')
            self.assertEqual(status,403)
            headers={'Origin':f'http://127.0.0.1:{port}','X-DeskFuse-Lab-Token':data['token']}
            status,body,_=req('POST','/api/start',headers=headers,body=json.dumps({'buffer_bursts':3,'modes':'f0c1r1'}))
            self.assertEqual(status,400)
            status,_,_=req('GET','/')
            self.assertEqual(status,200)
            # Extend only the explicitly allowed static UI files, never config or secrets.
            for asset in ('/atlas-data.js','/atlas.js','/atlas.css'):
                code,content,_=req('GET',asset)
                self.assertEqual(code,200)
                self.assertGreater(len(content),100)
            status,_,_=req('GET','/atlas-controls.json')
            self.assertEqual(status,404)
        finally:
            server.shutdown();server.server_close();th.join(2)

if __name__ == '__main__': unittest.main()
