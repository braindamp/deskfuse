"""Qualification-runner adapter to the existing enrolled product host.

No transport, enrollment keys, endpoint hashes, routing or driver operations.
Only invoked by run-physical-audio.ps1 after an explicit laboratory start.
"""
from __future__ import annotations
import argparse
import ctypes
from ctypes import wintypes
from datetime import datetime, timezone
import json
import os
from pathlib import Path
import re
import shutil
import subprocess
import sys
import threading
import time
from integration import (receipt_preflight, checkout_head, product_artifacts,
                         sha256_file, source_preflight)


class ProcessJob:
    """Agent waits on stdin until assigned; close kills any surviving children."""
    def __init__(self):
        kernel=ctypes.WinDLL("kernel32",use_last_error=True)
        kernel.CreateJobObjectW.argtypes=[ctypes.c_void_p,wintypes.LPCWSTR]
        kernel.CreateJobObjectW.restype=wintypes.HANDLE
        kernel.SetInformationJobObject.argtypes=[wintypes.HANDLE,ctypes.c_int,ctypes.c_void_p,wintypes.DWORD]
        kernel.AssignProcessToJobObject.argtypes=[wintypes.HANDLE,wintypes.HANDLE]
        kernel.CloseHandle.argtypes=[wintypes.HANDLE]
        self.kernel=kernel
        self.handle=kernel.CreateJobObjectW(None,None)
        if not self.handle: raise ctypes.WinError(ctypes.get_last_error())
        # JOBOBJECT_EXTENDED_LIMIT_INFORMATION: only LimitFlags is nonzero.
        class Basic(ctypes.Structure):
            _fields_=[("process_time",ctypes.c_int64),("job_time",ctypes.c_int64),
                      ("flags",wintypes.DWORD),("minimum",ctypes.c_size_t),("maximum",ctypes.c_size_t),
                      ("active",wintypes.DWORD),("affinity",ctypes.c_size_t),
                      ("priority",wintypes.DWORD),("scheduling",wintypes.DWORD)]
        class Extended(ctypes.Structure):
            _fields_=[("basic",Basic),("io",ctypes.c_uint64*6),("process_memory",ctypes.c_size_t),
                      ("job_memory",ctypes.c_size_t),("peak_process",ctypes.c_size_t),("peak_job",ctypes.c_size_t)]
        limits=Extended(); limits.basic.flags=0x2000  # JOB_OBJECT_LIMIT_KILL_ON_JOB_CLOSE
        if not kernel.SetInformationJobObject(self.handle,9,ctypes.byref(limits),ctypes.sizeof(limits)):
            self.close(); raise ctypes.WinError(ctypes.get_last_error())

    def assign(self,proc):
        if not self.kernel.AssignProcessToJobObject(self.handle,wintypes.HANDLE(int(proc._handle))):
            raise ctypes.WinError(ctypes.get_last_error())

    def close(self):
        if self.handle: self.kernel.CloseHandle(self.handle); self.handle=None


def command(cfg, backend, period, seconds):
    if backend not in ("loopback","committed") or period not in ("reference","minimum") or seconds not in (30,120):
        raise ValueError("Unsupported product experiment")
    return [str(product_artifacts(cfg)["product_agent"].resolve()),"run","--audio-lab",
            "--audio-lab-seconds",str(seconds),"--native-audio-exe",str(Path(cfg["windows_sender"]).resolve()),
            "--native-capture-exe",str(Path(cfg["windows_capture"]).resolve()),
            "--native-source-dll",str(Path(cfg["windows_source_control"]).resolve()),
            "--native-audio-backend",backend,"--native-audio-period",period,"--no-open-qr"]


def run(cfg, args):
    if os.name!="nt": raise ValueError("Product runner requires Windows")
    if cfg.get("deskfuse_peer_id"): raise ValueError("Remove manual peer identity; the product registry is authoritative")
    repo=Path(cfg["repo_root"]).resolve()
    branch=subprocess.run(["git","-c","safe.directory="+str(repo),"-C",str(repo),"branch","--show-current"],
                          check=True,capture_output=True,text=True,timeout=5).stdout.strip()
    if branch!="experiment/vnext-audio-latency" or branch!=cfg.get("expected_branch",branch):
        raise ValueError("Product experiments require experiment/vnext-audio-latency")
    head=checkout_head(cfg)
    if head is None: raise ValueError("Could not verify checkout HEAD")
    report=receipt_preflight(cfg,product_artifacts(cfg),checkout_head=head,product=True)
    problems=report["problems"]+source_preflight("direct_export" if args.backend=="committed" else "deskfuse",args.engine_period,args.capture_policy,report)
    if problems: raise ValueError("; ".join(problems))
    adb=cfg.get("adb_path") or shutil.which("adb")
    if not adb: raise ValueError("ADB is needed to verify the already-installed product APK; no installation is performed")
    if cfg.get("product_android_package") not in ("com.focuswindow.client","com.focuswindow.client.debug"):
        raise ValueError("Explicit product_android_package required")
    stamp=datetime.now(timezone.utc).strftime("%Y%m%dT%H%M%S%fZ")
    folder=Path(cfg["evidence_root"]).resolve()/(stamp+"-"+args.profile)
    folder.mkdir(parents=True,exist_ok=False)
    print("Evidence folder: "+str(folder),flush=True)
    manifest={"source":args.backend,"engine_period":args.engine_period,"capture_policy":args.capture_policy,
              "git_commit":head,"git_branch":branch,"receipt":report,"physical_qualified":False,
              "android_telemetry":"Unavailable over product control; no acoustic latency inferred",
              "comparison":"Compare actual LAB_ENDPOINT identity, period and the same product APK between dedicated runs. MM-1 uses the standalone receiver; cross-path timing is confounded unless native/profile differences are accounted for"}
    manifest_path=folder/"product-session.json"
    def save(): manifest_path.write_text(json.dumps(manifest,indent=2),encoding="utf-8")
    save()
    # Read back the installed APK; never install, start an APK or stop another host.
    remote=subprocess.run([adb,"shell","pm","path",cfg["product_android_package"]],check=True,
                          capture_output=True,text=True,timeout=10).stdout.splitlines()
    paths=[line[8:].strip() for line in remote if line.startswith("package:")]
    if len(paths)!=1 or not re.fullmatch(r"/data/app/[A-Za-z0-9_./=+~-]+\.apk",paths[0]):
        raise ValueError("A single installed matching product APK is required (split APKs are not qualified)")
    installed=folder/"installed-product.apk"
    subprocess.run([adb,"pull",paths[0],str(installed)],check=True,capture_output=True,timeout=30)
    manifest["installed_apk_sha256"]=sha256_file(installed);save()
    if manifest["installed_apk_sha256"]!=report["artifacts"]["product_android_apk"]["sha256"]:
        raise ValueError("Installed product APK differs from receipt; no deployment or playback attempted")
    env=os.environ.copy()
    for key in ("DESKFUSE_WASAPI_PACKET_TRACE","DESKFUSE_AUDIO_MEASURE_CALLBACK"):
        env.pop(key,None)
    env["DESKFUSE_WASAPI_CAPTURE_POLICY"]=args.capture_policy
    env["DESKFUSE_WASAPI_ENGINE_PERIOD"]=args.engine_period
    process=None; job=ProcessJob(); reader=None
    log=(folder/"windows-source.stdout.txt").open("w",encoding="utf-8")
    try:
        process=subprocess.Popen(command(cfg,args.backend,args.engine_period,args.seconds),cwd=repo,
            stdin=subprocess.PIPE,stdout=subprocess.PIPE,stderr=subprocess.STDOUT,
            text=True,encoding="utf-8",errors="replace",env=env,creationflags=subprocess.CREATE_NO_WINDOW)
        def drain():
            for line in process.stdout:
                log.write(line);log.flush()
                # Browser sees only source observations, not enrollment/control details.
                if line.startswith(("LAB_","WASAPI_CONFIG ","DIRECT_CONFIG ","DIRECT_TOTALS ","DIRECT_BLOCKED ","DIRECT_GEOMETRY ","NATIVE_PCM ","DONE Windows WASAPI ","DONE committed source ")):
                    print(line.rstrip(),flush=True)
        reader=threading.Thread(target=drain,daemon=True);reader.start()
        job.assign(process)
        process.stdin.write("start\n");process.stdin.flush()
        print("Waiting for your already-enrolled phone in speaker-only mode. Explicitly route the application to the reported DeskFuse endpoint. Existing host port conflicts fail; nothing is stopped automatically.",flush=True)
        try: code=process.wait(timeout=args.seconds+80)
        except subprocess.TimeoutExpired:
            process.stdin.close()
            try: process.wait(timeout=15)
            except subprocess.TimeoutExpired: job.close();process.wait(timeout=10)
            code=2
        reader.join(timeout=10)
        if reader.is_alive(): raise RuntimeError("Product output did not retire")
        text=(folder/"windows-source.stdout.txt").read_text(encoding="utf-8")
        if "LAB_RESULT media_ready=1" not in text or not re.search(r"LAB_RESULT .* queued=[1-9][0-9]* .*exit=0",text): code=2
        from bridge import parse_observed_lines
        manifest["observed"]=parse_observed_lines(text)
        manifest["endpoint_identities"]=[json.loads(line[13:])["endpoint"] for line in text.splitlines() if line.startswith("LAB_ENDPOINT ")]
        manifest["exit_code"]=code;save()
        if code!=0: raise ValueError("Product run blocked/failed; inspect local product evidence. No physical qualification claimed")
        return 0
    finally:
        if process is not None and process.poll() is None:
            try: process.stdin.close();process.wait(timeout=15)
            except (OSError,subprocess.TimeoutExpired): pass
        job.close()
        if process is not None: process.wait(timeout=10)
        if reader is not None: reader.join(timeout=10)
        log.close()


def main():
    p=argparse.ArgumentParser()
    p.add_argument("--config",required=True);p.add_argument("--backend",choices=("loopback","committed"),required=True)
    p.add_argument("--engine-period",choices=("reference","minimum"),required=True)
    p.add_argument("--capture-policy",choices=("reference","minimum"),required=True)
    p.add_argument("--seconds",type=int,choices=(30,120),required=True);p.add_argument("--profile",required=True)
    args=p.parse_args()
    if not re.fullmatch(r"[A-Za-z0-9_-]{1,120}",args.profile): p.error("Invalid profile")
    try: return run(json.loads(Path(args.config).read_text(encoding="utf-8-sig")),args)
    except (OSError,ValueError,KeyError,RuntimeError,subprocess.SubprocessError) as error:
        print("Product preflight/run failed: "+str(error),file=sys.stderr);return 2


if __name__=="__main__": sys.exit(main())
