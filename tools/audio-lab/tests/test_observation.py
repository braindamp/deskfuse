import io
import json
from pathlib import Path
import sys
import unittest
sys.path.insert(0, str(Path(__file__).resolve().parent.parent))
import bridge
import product_runner


class ObservationTests(unittest.TestCase):
    def test_real_native_log_spellings_and_namespaces(self):
        data=bridge.parse_observed_lines(
            'AUDIO_CONFIG sharing_request=1 actual_usage=14 reserve_frames=960\n'
            'TRACE Android OUTPUT_HAL frame=100 monotonic_ns=123456789 timestamp_result=-899 generation=1 buffer_frames=480\n'
            'TRACE Android RENDER committed_source=240 queued_frames=960 underruns=3 rebuffer=2\n'
            'AUDIO Android reconstruction clipped_samples=4 xrun_count=5\n'
            'DONE Windows WASAPI source queued=100 pre_quic_missing_frames=240\n')
        self.assertEqual(data['measurements']['android_config']['actual_usage'],14)
        self.assertEqual(data['measurements']['android_hal']['timestamp_result'],-899)
        self.assertEqual(data['measurements']['android_render']['underruns'],3)
        self.assertEqual(data['xrun_count'],5)
        self.assertEqual(data['pre_quic_missing_frames'],240)
        self.assertNotIn('latency_ms',data)

    def test_observations_cannot_cross_run_generations(self):
        run='a'*32
        value={'version':1,'run_id':run,'sequence':2,'snapshot':{'version':1,'underrun_events':3,'xrun_count':None}}
        line='LAB_ANDROID '+json.dumps(value)
        self.assertNotIn('android_observation',bridge.parse_observed_lines(line,'b'*32))
        result=bridge.parse_observed_lines(line,run)
        self.assertIsNone(result['xrun_count'])
        self.assertEqual(result['underrun_events'],3)
        value['sequence']=1
        bridge.merge_observed(result,bridge.parse_observed_lines('LAB_ANDROID '+json.dumps(value),run))
        self.assertEqual(result['android_observation']['sequence'],2)
        runner=bridge.Runner({})
        runner.run_id='b'*32;runner.active=True
        class OldProcess:
            stdout=io.StringIO(line+'\nEvidence folder: stale\n')
            def wait(self): return 0
        runner._collect(OldProcess(),run)
        self.assertTrue(runner.active)
        self.assertIsNone(runner.last_evidence)
        self.assertIsNone(runner.return_code)
        self.assertEqual(runner.observed,{})

    def test_all_supported_policies_reach_both_runner_paths(self):
        cfg={key:str(Path('/example')/key) for key in ('product_agent','windows_sender','windows_capture','windows_source_control')}
        cfg['endpoint_id']='{0.0.0.00000000}.{test}'
        for changes,flag,value in [({'bursts':2},'-AndroidBufferBursts','2'),
                ({'sharing':1},'-AndroidSharingPolicy','1'),({'usage':1},'-AndroidUsagePolicy','1'),
                ({'reserve':960},'-NativeReserveFrames','960'),({'modes':'f0c1r1'},'-Modes','f0c1r1')]:
            for source in ('mm1','deskfuse','direct_export'):
                params=dict(bursts=0,modes='f1c1r1',seconds=30,label='',source=source,run_id='a'*32)
                params.update(changes)
                cmd=bridge.build_command(cfg,**params)
                self.assertEqual(cmd[cmd.index(flag)+1],value)
                self.assertEqual(cmd[cmd.index('-RunId')+1],'a'*32)
            cmd=product_runner.command(cfg,'committed','reference',30,run_id='a'*32,**changes)
            agent_flag={'-AndroidBufferBursts':'--audio-lab-bursts','-AndroidSharingPolicy':'--audio-lab-sharing',
                '-AndroidUsagePolicy':'--audio-lab-usage','-NativeReserveFrames':'--audio-lab-reserve','-Modes':'--audio-lab-modes'}[flag]
            self.assertEqual(cmd[cmd.index(agent_flag)+1],value)
        with self.assertRaises(ValueError):
            product_runner.command(cfg,'committed','minimum',30,reserve=960)
        with self.assertRaises(ValueError):
            bridge.build_command(cfg,bursts=0,modes='f1c1r1',seconds=30,label='',sharing=True)


if __name__=='__main__': unittest.main()
