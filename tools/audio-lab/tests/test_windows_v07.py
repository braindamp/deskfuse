"""Fail-closed Windows architecture planning and runner admission tests."""
import json
from pathlib import Path
import sys
import unittest
sys.path.insert(0,str(Path(__file__).resolve().parent.parent))
import bridge
import integration


class WindowsRoadmapTests(unittest.TestCase):
    def test_minimum_uses_native_driver_gate_not_receipt_attestation(self):
        report={"capabilities":{"windows_capture_policies":["reference","minimum"],
                                "windows_engine_period_policies":["reference","minimum"],
                                **integration.PRODUCT_CAPS}}
        self.assertTrue(integration.source_preflight("mm1","minimum","reference",report))
        self.assertEqual(integration.source_preflight("deskfuse","minimum","reference",report),[])
        cmd=bridge.build_command({},bursts=0,modes="f1c1r1",seconds=30,label="",
                                 source="deskfuse",engine_period="minimum")
        self.assertIn("-ProductConfig",cmd)
        self.assertEqual(cmd[cmd.index("-WindowsEnginePeriod")+1],"minimum")
        self.assertNotIn("-DeskFusePeerId",cmd)
        self.assertNotIn("-KeysDir",cmd)
        with self.assertRaisesRegex(ValueError,"running-driver gate"):
            bridge.build_command({},bursts=0,modes="f1c1r1",seconds=30,label="",engine_period="minimum")

    def test_direct_export_calls_product_pipeline_and_rejects_irrelevant_knobs(self):
        cmd=bridge.build_command({},bursts=0,modes="f1c1r1",seconds=30,label="",source="direct_export")
        self.assertEqual(cmd[cmd.index("-WindowsSourceBackend")+1],"committed")
        self.assertTrue(integration.source_preflight("direct_export","reference","reference",{}))
        for changes in ({"capture_policy":"minimum"},{"bursts":2},{"modes":"f0c1r1"}):
            options=dict(bursts=0,modes="f1c1r1",seconds=30,label="",source="direct_export")
            options.update(changes)
            with self.assertRaises(ValueError): bridge.build_command({},**options)

    def test_planning_catalog_and_comparison_are_not_native_controls(self):
        root=Path(__file__).resolve().parent.parent
        data=json.loads((root/"atlas-controls.json").read_text(encoding="utf-8"))
        self.assertEqual(data["count"],len(data["controls"]))
        by_id={v["id"]:v for v in data["controls"]}
        for control in ["win_engine_driver_gate","win_own_endpoint_activation",
                        "win_capture_backend","win_commit_export_credits",
                        "win_capture_commit_age","win_source_recovery_fence"]:
            self.assertIn(control,by_id)
            self.assertNotEqual(by_id[control]["status"],"physical")
        html=(root/"index.html").read_text(encoding="utf-8")
        for id in ['win-pipeline','win-gates','win-evidence-rows','win-export-plan']:
            self.assertIn('id="'+id+'"',html)
        script=(root/"windows-lab.js").read_text(encoding="utf-8")
        self.assertIn("NON_EXECUTABLE_PLANNING_EXPORT",script)
        self.assertNotIn("/api/start",script)

if __name__=='__main__':unittest.main()
