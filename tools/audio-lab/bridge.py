"""DeskFuse Audio Lab local-only orchestration bridge (Python standard library).

This is NOT an audio transport or Android receiver. It launches the checked-in
qualification runner with four prebuilt, matching, receipt-verified Release
artifacts. All settings are startup-only. No shell string execution, Git writes,
builds, secrets access from the browser, global settings or driver changes.
"""
from __future__ import annotations

import argparse
from collections import deque
from datetime import datetime, timezone
from hashlib import sha256
from http.server import BaseHTTPRequestHandler, ThreadingHTTPServer
import json
import os
from pathlib import Path
import re
import secrets
import shutil
import subprocess
import sys
import threading
import time
import uuid
import copy
from urllib.parse import urlsplit
from integration import (broker_preflight, receipt_preflight, receipt_path, checkout_head,
                         source_preflight, source_comparison, product_artifacts)

ROOT = Path(__file__).resolve().parent
WEB = {"/": "index.html", "/index.html": "index.html", "/app.js": "app.js",
       "/styles.css": "styles.css", "/live.js": "live.js", "/live.css": "live.css",
       "/atlas-data.js": "atlas-data.js", "/atlas.js": "atlas.js", "/atlas.css": "atlas.css",
       "/windows-lab.js": "windows-lab.js", "/windows-lab.css": "windows-lab.css"}
TYPES = {".html": "text/html", ".js": "text/javascript", ".css": "text/css"}
MODES = re.compile(r"^f[01]c[01]r[01]$")
PROFILE = re.compile(r"^[a-zA-Z0-9_-]{0,35}$")
ALLOWED_SECONDS = (30, 120)
OBSERVED_METRICS = (
    "underrun_events", "reprime_events", "xrun_count", "xruns", "buffer_frames",
    "capacity_frames", "clipped_samples", "pre_quic_missing_frames", "quiche_queue_full",
    "qpc_future", "qpc_invalid", "queued_playable_frames", "playable_frames", "primed",
    "allocated_frames", "current_period_frames", "minimum_period_frames",
    "default_period_frames", "companion_period_frames", "requested_capacity_100ns",
    "driver_verified", "driver_build", "timing_abi", "export_abi", "period_verified",
    "accepted_commit", "accepted_end_frame", "driver_dropped_frames", "source_generation", "published", "acknowledged",
    "reader_summary_available", "native_cleanup_confirmed", "geometry_qualified", "packet_frames", "notifications", "sample_rate", "channels", "bits", "queued", "expired", "dropped", "real_pcm_blocks_queued",
    "anchor_replies", "reorder_full", "budget_drops", "source_missing_frames",
)
OBSERVED_SET = frozenset(OBSERVED_METRICS)


def parse_observed_lines(contents: str, run_id: str | None = None) -> dict:
    # Fixed trusted-native prefixes; never turn unrelated control logs into evidence.
    found = {}; namespaces = {}
    prefixes = (("TRACE Android OUTPUT_HAL", "android_hal"), ("TRACE Android RENDER", "android_render"),
        ("TRACE Android RECONSTRUCTION", "android_reconstruction"), ("AUDIO Android reconstruction", "android_reconstruction"),
        ("AUDIO Android speaker", "android_audio"), ("AUDIO_CONFIG", "android_config"),
        ("AUDIO_BACKEND", "android_config"), ("WASAPI_CONFIG", "windows_config"),
        ("DIRECT_CONFIG", "windows_config"), ("DIRECT_TOTALS", "windows_source"),
        ("DIRECT_GEOMETRY", "windows_geometry"), ("NATIVE_PCM", "windows_source"),
        ("DONE committed source", "windows_source"), ("DONE Windows WASAPI", "windows_source"),
        ("DONE Android sink", "android_sink"))
    for line in contents.splitlines():
        if line.startswith(("LAB_ANDROID ", "LAB_TRIAL_RESULT ")):
            try:
                value = json.loads(line.split(" ", 1)[1])
                if not isinstance(value, dict) or value.get("version") != 1 or not run_id or value.get("run_id") != run_id: continue
                if line.startswith("LAB_ANDROID "):
                    snap = value.get("snapshot")
                    if not isinstance(snap, dict) or len(snap)>60: continue
                    if any(not isinstance(v,(int,float,bool,type(None))) for v in snap.values()): continue
                    prev = found.get("android_observation")
                    if prev and value.get("sequence",0) <= prev.get("sequence",0): continue
                    found["android_observation"] = value
                    if value.get("end_snapshot") is True: found["android_final_observation"] = value
                else: found["trial_result"] = value
            except (ValueError, TypeError): pass
            continue
        group = next((name for prefix,name in prefixes if line.startswith(prefix)), None)
        if not group: continue
        record = namespaces.setdefault(group,{})
        for key,value in re.findall(r"\b([a-z][a-z0-9_]*)=(-?[0-9]+)\b",line):
            number = int(value)
            record[key] = number
            if key in OBSERVED_SET: found[key] = number  # historical readouts
    found["measurements"] = namespaces
    # Authenticated product observations take precedence over historical log aliases.
    if "android_observation" in found:
        for key,value in found["android_observation"]["snapshot"].items():
            if key in OBSERVED_SET: found[key] = value
    return found


def merge_observed(target, update):
    groups = target.setdefault("measurements",{})
    for name,values in update.pop("measurements",{}).items(): groups.setdefault(name,{}).update(values)
    previous=target.get("android_observation")
    current=update.get("android_observation")
    if previous and current and current.get("sequence",0)<=previous.get("sequence",0): update.pop("android_observation",None)
    target.update(update)
    if "android_observation" in target:
        for key,value in target["android_observation"]["snapshot"].items():
            if key in OBSERVED_SET: target[key]=value


def tail_file(path: Path, limit: int = 100_000) -> str:
    try:
        with path.open("rb") as f:
            f.seek(0, os.SEEK_END)
            f.seek(max(0, f.tell() - limit))
            return f.read().decode("utf-8", errors="replace")
    except OSError:
        return ""
CONFIG_FILENAME = ROOT / "config.json"


def utc_now() -> str:
    return datetime.now(timezone.utc).isoformat(timespec="seconds")


def load_config() -> dict:
    if not CONFIG_FILENAME.is_file():
        return {}
    data = json.loads(CONFIG_FILENAME.read_text(encoding="utf-8"))
    if not isinstance(data, dict):
        raise ValueError("config.json must be a JSON object")
    return data


def cfg_path(cfg: dict, name: str) -> Path:
    value = cfg.get(name)
    return Path(value).expanduser() if isinstance(value, str) and value else Path("__not_configured__")


def artifacts(cfg: dict) -> dict[str, Path]:
    repo = cfg_path(cfg, "repo_root")
    return {
        "windows_capture": cfg_path(cfg, "windows_capture") if cfg.get("windows_capture") else repo / "vnext/m1/build/deskfuse-wasapi-capture.exe",
        "windows_rust": cfg_path(cfg, "windows_sender"),
        "android_rust": cfg_path(cfg, "android_receiver"),
        "android_native": cfg_path(cfg, "android_renderer"),
    }


def check_setup(cfg: dict, *, verify_hashes: bool = False, source: str = "mm1") -> list[str]:
    failures = []
    if os.name != "nt":
        failures.append("The physical runner requires Windows; preview works on other systems.")
    repo = cfg_path(cfg, "repo_root")
    runner = repo / "vnext/qualification/run-physical-audio.ps1"
    if not runner.is_file():
        failures.append("Repo/qualification script missing. Set repo_root to the GitHub checkout.")
    if not shutil.which("git"):
        failures.append("Git is not on PATH.")
    if not shutil.which("powershell.exe"):
        failures.append("Windows PowerShell is not on PATH.")
    selected_artifacts = artifacts(cfg) if source == "mm1" else product_artifacts(cfg)
    for kind, path in selected_artifacts.items():
        if not path.is_file():
            failures.append(f"Missing {kind}: configure/build the matching Release binary.")
    adb = cfg.get("adb_path")
    if adb and not Path(adb).is_file():
        failures.append("Configured adb_path does not exist.")
    elif not adb and not shutil.which("adb"):
        failures.append("ADB is not on PATH and adb_path is not configured.")
    if source == "mm1" and (not isinstance(cfg.get("endpoint_id"), str) or not cfg.get("endpoint_id", "").startswith("{0.0.0.00000000}.")):
        failures.append("Explicit MM-1 endpoint_id is required; no Windows default-device fallback")
    if source != "mm1":
        if cfg.get("deskfuse_peer_id"): failures.append("Manual peer identities are forbidden; product enrollment is required")
        if cfg.get("product_android_package") not in ("com.focuswindow.client", "com.focuswindow.client.debug"): failures.append("Configure the already-installed product Android package")
    for k in (("ca.pem", "server.pem", "server.key") if source == "mm1" else ()):
        if not (cfg_path(cfg, "keys_dir") / k).is_file():
            failures.append(f"Missing locally provisioned lab credential file: {k}.")
    branch = cfg.get("expected_branch", "experiment/vnext-audio-latency")
    if not isinstance(branch, str) or branch.startswith("reference/") or branch in ("main", "master"):
        failures.append("Expected branch must be an isolated development branch.")
    if not failures:
        try:
            out = subprocess.run(["git", "-c", "safe.directory="+str(repo.resolve()), "-C", str(repo), "branch", "--show-current"],
                                 capture_output=True, text=True, timeout=5, check=True)
            if out.stdout.strip() != branch:
                failures.append(f"Selected checkout is on {out.stdout.strip() or 'detached HEAD'}, not {branch}.")
        except Exception:
            failures.append("Could not verify Git checkout branch.")
    if verify_hashes and not failures:
        report = receipt_preflight(cfg, selected_artifacts, checkout_head=checkout_head(cfg), product=source!="mm1")
        failures.extend(report["problems"])
    return failures


def build_command(cfg: dict, *, bursts: int, modes: str, seconds: int, label: str,
                  source: str = "mm1", engine_period: str = "reference",
                  capture_policy: str = "reference", sharing: int = 0, usage: int = 0, reserve: int = 1440, run_id: str | None = None) -> list[str]:
    if source not in ("mm1", "deskfuse", "direct_export") or engine_period not in ("reference", "minimum") or capture_policy not in ("reference", "minimum"):
        raise ValueError("Unsupported Windows source or period policy")
    if source != "mm1" and cfg.get("deskfuse_peer_id"):
        raise ValueError("Manual peer identity is not authorized; use product enrollment")
    if source == "mm1" and engine_period == "minimum":
        raise ValueError("Minimum engine period requires the DeskFuse running-driver gate, not MM-1")
    if source == "direct_export" and capture_policy != "reference":
        raise ValueError("WASAPI allocation does not apply to committed PCM")
    if type(sharing) is not int or sharing not in (0,1) or type(usage) is not int or usage not in (0,1) or type(reserve) is not int or reserve not in (1440,1200,960,720,480):
        raise ValueError("Unsupported native output policy")
    if run_id is None: run_id = uuid.uuid4().hex
    if not re.fullmatch(r"[0-9a-f]{32}",run_id): raise ValueError("Invalid run ID")
    if bursts not in (0, 2, 3) or not MODES.fullmatch(modes) or seconds not in ALLOWED_SECONDS:
        raise ValueError("Unsupported startup parameters")
    if not PROFILE.fullmatch(label):
        raise ValueError("Use up to 35 ASCII letters, digits, underscores or hyphens for labels")
    # One-factor protocol: safe initial reference is buffer 0 + f1c1r1.
    # Other matrix choices remain a model, not executable settings.
    modified = int(sharing != 0) + int(usage != 0) + int(reserve != 1440) + int(bursts != 0) + sum(modes[i] != "1" for i in (1, 3, 5)) + int(engine_period != "reference") + int(capture_policy != "reference")
    if modified > 1:
        raise ValueError("Only one difference from the listening reference is allowed per physical test")
    stamp = datetime.now(timezone.utc).strftime("%Y%m%d-%H%M%S")
    profile = "audiolab-" + run_id + "-" + stamp + ("-" + label if label else "") + f"-{source}-{engine_period}-{capture_policy}-b{bursts}-{modes}"
    repo = cfg_path(cfg, "repo_root")
    if source != "mm1":
        return ["powershell.exe", "-NoProfile", "-ExecutionPolicy", "Bypass", "-File",
                str(repo / "vnext/qualification/run-physical-audio.ps1"),
                "-ProductConfig",str(CONFIG_FILENAME),"-PythonExecutable",sys.executable,
                "-WindowsSourceBackend","committed" if source=="direct_export" else "loopback",
                "-WindowsEnginePeriod",engine_period,"-WindowsCapturePolicy",capture_policy,
                "-Seconds",str(seconds),"-Profile",profile,"-RunId",run_id,
                "-Modes",modes,"-AndroidBufferBursts",str(bursts),"-AndroidSharingPolicy",str(sharing),
                "-AndroidUsagePolicy",str(usage),"-NativeReserveFrames",str(reserve)]
    endpoint=cfg.get("endpoint_id")
    if not isinstance(endpoint,str) or not endpoint.startswith("{0.0.0.00000000}."):
        raise ValueError("Explicit MM-1 endpoint required; no default-device fallback")
    cmd = ["powershell.exe", "-NoProfile", "-ExecutionPolicy", "Bypass", "-File",
           str(repo / "vnext/qualification/run-physical-audio.ps1"),
           "-EndpointId", endpoint,
           "-WindowsExecutable", str(cfg_path(cfg, "windows_sender")),
           "-AndroidExecutable", str(cfg_path(cfg, "android_receiver")),
           "-AndroidNativeSo", str(cfg_path(cfg, "android_renderer")),
           "-LabRoot", str(repo),
           "-ExpectedBranch", cfg.get("expected_branch", "experiment/vnext-audio-latency"),
           "-WindowsCaptureExecutable", str(artifacts(cfg)["windows_capture"]),
           "-BuildReceipt", str(receipt_path(cfg)),
           "-WindowsEnginePeriod", engine_period,
           "-WindowsCapturePolicy", capture_policy,
           "-EvidenceRoot", str(cfg_path(cfg, "evidence_root")),
           "-KeysDir", str(cfg_path(cfg, "keys_dir")),
           "-Modes", modes, "-AndroidBufferBursts", str(bursts),
           "-Seconds", str(seconds), "-Profile", profile,"-RunId",run_id,
           "-AndroidSharingPolicy",str(sharing),"-AndroidUsagePolicy",str(usage),"-NativeReserveFrames",str(reserve)]
    if cfg.get("windows_ip"):
        cmd.extend(["-WindowsIP", cfg["windows_ip"]])
    if cfg.get("phone_ip"):
        cmd.extend(["-PhoneIP", cfg["phone_ip"]])
    return cmd


class Runner:
    def __init__(self, cfg: dict):
        self.cfg = cfg
        self.lock = threading.Lock()
        self.process = None
        self.log = deque(maxlen=100)
        self.started_at = None
        self.finished_at = None
        self.return_code = None
        self.last_profile = None
        self.last_evidence = None
        self.started_meta = None
        self.started_mono = None
        self.finished_mono = None
        self.launch_failed = False
        self.run_id = None
        self.active = False
        self.observed = {}
        self.receipt = None
        self.history = deque(maxlen=20)

    def _collect(self, proc, run_id):
        try:
            for line in proc.stdout:
                line = line.strip()
                with self.lock:
                    if run_id != self.run_id: continue
                    merge_observed(self.observed,parse_observed_lines(line,run_id))
                    if line.startswith("Evidence folder:"):
                        self.last_evidence = line.partition(":")[2].strip()
                    self.log.append(line[:350])
        finally:
            code = proc.wait()
            with self.lock:
                if run_id != self.run_id: return
                self.return_code = code
                self.finished_at = utc_now()
                self.finished_mono = time.monotonic()
                self.log.append("Physical session finished (runner exit code " + str(code) + ").")
            # Finish evidence collection before permitting the next generation.
            try:
                final = self.status()
                record = {k:final.get(k) for k in ("run_id","session","started_at","finished_at","return_code","profile","evidence_folder","telemetry","endpoint_identity","artifacts","runner_duration_ms","termination_reason")}
                path = final.get("evidence_folder")
                if path and Path(path).resolve().parent == cfg_path(self.cfg,"evidence_root").resolve():
                    (Path(path)/"lab-run.json").write_text(json.dumps(record,indent=2),encoding="utf-8")
                with self.lock:
                    if self.run_id == run_id: self.history.append({k:record.get(k) for k in ("run_id","session","profile","return_code","evidence_folder")})
            finally:
                with self.lock:
                    if self.run_id == run_id: self.active = False

    def start(self, body: dict):
        if not isinstance(body, dict):
            raise ValueError("JSON request must be an object")
        if isinstance(body.get("buffer_bursts"), bool) or isinstance(body.get("seconds"), bool):
            raise ValueError("Invalid numeric parameter")
        bursts = body.get("buffer_bursts", 0)
        modes = body.get("modes", "f1c1r1")
        seconds = body.get("seconds", 30)
        label = body.get("label", "")
        sharing=body.get("sharing_policy",0); usage=body.get("usage_policy",0); reserve=body.get("reserve_frames",1440)
        run_id=uuid.uuid4().hex
        source = body.get("source", "mm1")
        engine_period = body.get("engine_period", "reference")
        capture_policy = body.get("capture_policy", "reference")
        if not isinstance(bursts, int) or not isinstance(seconds, int) or not isinstance(modes, str) or not isinstance(label, str):
            raise ValueError("Wrong parameter types")
        if not all(isinstance(x, str) for x in (source, engine_period, capture_policy)):
            raise ValueError("Invalid Windows source selection")
        cmd = build_command(self.cfg, bursts=bursts, modes=modes, seconds=seconds, label=label,
                            source=source, engine_period=engine_period, capture_policy=capture_policy, sharing=sharing, usage=usage, reserve=reserve, run_id=run_id)
        # Never honor a browser-supplied config/path. Product runner independently
        # reloads the protected local file and rechecks hashes before host launch.
        if source!="mm1" and not CONFIG_FILENAME.is_file():
            raise ValueError("Product launch requires the local config.json")
        with self.lock:
            if self.active or self.process is not None and self.process.poll() is None:
                raise ValueError("A physical session is already running")
            problems = check_setup(self.cfg, verify_hashes=True, source=source)
            if not problems:
                report = receipt_preflight(self.cfg, artifacts(self.cfg) if source=="mm1" else product_artifacts(self.cfg), checkout_head=checkout_head(self.cfg), product=source!="mm1")
                problems.extend(source_preflight(source, engine_period, capture_policy, report))
                if checkout_head(self.cfg) is None: problems.append("Could not verify checkout HEAD")
            if problems:
                raise ValueError("Preflight failed: " + "; ".join(problems))
            env = os.environ.copy()
            adb = self.cfg.get("adb_path")
            if adb:
                env["PATH"] = str(Path(adb).parent) + os.pathsep + env.get("PATH", "")
            env.pop("DESKFUSE_WASAPI_PACKET_TRACE", None)
            self.log.clear()
            self.run_id = run_id
            self.observed = {}
            self.receipt = copy.deepcopy(report)
            self.last_evidence = None
            self.finished_at = None
            self.return_code = None
            self.started_at = utc_now()
            self.started_mono = time.monotonic()
            self.finished_mono = None
            self.launch_failed = False
            self.started_meta = {"buffer_bursts": bursts, "modes": modes, "seconds": seconds, "label": label,
                                 "source": source, "engine_period": engine_period, "capture_policy": capture_policy,
                                 "sharing_policy":sharing,"usage_policy":usage,"reserve_frames":reserve,
                                 "output_policy_version":1}
            self.last_profile = next(cmd[i+1] for i, v in enumerate(cmd[:-1]) if v == "-Profile")
            self.log.append("Launching existing GitHub qualification runner; matching Release hashes verified.")
            try:
                self.process = subprocess.Popen(cmd, cwd=str(cfg_path(self.cfg, "repo_root")),
                                            env=env, stdout=subprocess.PIPE, stderr=subprocess.STDOUT,
                                            text=True, encoding="utf-8", errors="replace", bufsize=1,
                                            creationflags=getattr(subprocess, "CREATE_NO_WINDOW", 0))
            except OSError:
                self.process=None; self.launch_failed=True; self.return_code=2
                self.finished_at=utc_now(); self.finished_mono=time.monotonic()
                raise
            self.active = True
            threading.Thread(target=self._collect, args=(self.process,run_id), daemon=True).start()
        return {"started": True, "profile": self.last_profile,"run_id":run_id}

    def status(self) -> dict:
        with self.lock:
            running = self.active or bool(self.process is not None and self.process.poll() is None)
            status = {"running": running, "started_at": self.started_at,
                      "finished_at": self.finished_at, "return_code": self.return_code,
                      "profile": self.last_profile, "evidence_folder": self.last_evidence,
                      "session": copy.deepcopy(self.started_meta), "log": list(self.log)[-45:],
                      "android_telemetry_available":"android_observation" in self.observed,
                      "run_id":self.run_id,"artifacts":copy.deepcopy(self.receipt),
                      "telemetry":copy.deepcopy(self.observed),"recent_runs":copy.deepcopy(list(self.history))}
            status["runner_duration_ms"]=(1000*((self.finished_mono or time.monotonic())-self.started_mono)) if self.started_mono is not None else None
            status["termination_reason"]="runner_launch_failed" if self.launch_failed else None
            evidence=self.last_evidence; profile=self.last_profile
        if evidence is None and profile:
            # The checked-in PowerShell runner creates its timestamped folder
            # before writing any audio. Search only the configured evidence root.
            try:
                folders = (p for p in cfg_path(self.cfg, "evidence_root").iterdir()
                           if p.is_dir() and profile in p.name)
                evidence = str(max(folders, key=lambda p: p.stat().st_mtime))
            except (OSError, ValueError):
                pass
        if evidence:
            status["evidence_folder"] = evidence
            root = cfg_path(self.cfg, "evidence_root").resolve()
            candidate = Path(evidence).resolve()
            if candidate.parent == root and status["run_id"] and status["run_id"] in candidate.name:
                records = status["telemetry"]
                for name in ("windows-source.stdout.txt", "windows-source.stderr.txt", "android-receiver.txt"):
                    merge_observed(records,parse_observed_lines(tail_file(candidate / name),status["run_id"]))
                status["telemetry"] = records
                text=tail_file(candidate / "windows-source.stdout.txt")
                for line in text.splitlines():
                    if line.startswith("LAB_ENDPOINT "):
                        try: status["endpoint_identity"]=json.loads(line[13:])["endpoint"]
                        except (ValueError,KeyError,TypeError): pass
                if (status.get("session") or {}).get("source")=="mm1": status["endpoint_identity"]=self.cfg.get("endpoint_id")
                status["physically_qualified"]=False
                if (status.get("session") or {}).get("source")!="mm1":
                    status["android_telemetry_available"]="android_observation" in records
        groups=status["telemetry"].get("measurements",{})
        control="android_observation" in status["telemetry"]
        log_only=(status.get("session") or {}).get("source")=="mm1" and any(k.startswith("android_") for k in groups)
        status["android_telemetry_available"]=control or log_only
        status["android_observation_source"]="authenticated_control" if control else "native_log" if log_only else None
        trial=status["telemetry"].get("trial_result")
        status["termination_reason"]=status["termination_reason"] or (trial.get("termination_reason") if trial else
            ("runner_failed_without_native_result" if status["return_code"] not in (None,0) else
             "runner_completed_without_native_window_evidence" if status["return_code"]==0 else "running" if status["running"] else None))
        return status


class LabServer(ThreadingHTTPServer):
    daemon_threads = True
    allow_reuse_address = True

    def __init__(self, addr, runner: Runner):
        super().__init__(addr, LabHandler)
        self.runner = runner
        self.token = secrets.token_urlsafe(32)
        self.integration_lock = threading.Lock()
        self.integration_snapshot = None
        self.integration_at = 0.0
        self.setup_snapshot=None
        self.setup_at=0.0


class LabHandler(BaseHTTPRequestHandler):
    server: LabServer
    def log_message(self, fmt, *args):
        # Suppress query logging. No credentials are used in any URI.
        if not self.path.startswith("/api/status"):
            print("[lab] " + (fmt % args))

    def _trusted_host(self) -> bool:
        p = self.headers.get("Host", "")
        return p in (f"localhost:{self.server.server_port}", f"127.0.0.1:{self.server.server_port}")

    def _headers(self, status: int, mime: str, length: int):
        self.send_response(status)
        self.send_header("Content-Type", mime)
        self.send_header("Content-Length", str(length))
        self.send_header("Cache-Control", "no-store")
        self.send_header("X-Content-Type-Options", "nosniff")
        self.send_header("X-Frame-Options", "DENY")
        self.send_header("Referrer-Policy", "no-referrer")
        self.send_header("Content-Security-Policy", "default-src 'self'; script-src 'self'; style-src 'self' 'unsafe-inline'; img-src 'self' data: blob:; media-src 'self' blob:; connect-src 'self'; object-src 'none'; base-uri 'none'; frame-ancestors 'none'")
        self.end_headers()

    def _json(self, code: int, data: dict):
        payload = json.dumps(data).encode("utf-8")
        self._headers(code, "application/json; charset=utf-8", len(payload))
        self.wfile.write(payload)

    def do_GET(self):
        if not self._trusted_host():
            return self._json(403, {"error": "Invalid local Host header"})
        path = urlsplit(self.path).path
        if path == "/api/windows-integration":
            cfg = self.server.runner.cfg
            with self.server.integration_lock:
                now = time.monotonic()
                running = self.server.runner.active or bool(self.server.runner.process and self.server.runner.process.poll() is None)
                # Do not rehash binaries, query broker or touch local evidence
                # repeatedly while real-time audio is running.
                if running and self.server.integration_snapshot is None:
                    # Do not initiate a multi-binary hash or named-pipe probe
                    # on first page load during an active physical experiment.
                    snapshot = {"source_options": source_comparison(),
                        "broker": {"state": "deferred", "detail": "Read-only broker probe deferred during playback"},
                        "receipt": {"state": "deferred", "problems": [], "artifacts": {},
                                    "source_commit": None, "capabilities": {}},
                        "windows_policies": ["reference", "minimum"],
                        "native_runtime_checks_required": True, "physical_qualified": False,
                        "dedicated_request_supported": True,
                        "dedicated_launch_enabled": False,
                        "minimum_period_launch_enabled": False,
                        "minimum_period_reason": "Conditional request only: native exact-endpoint driver verification + actual period readback before PCM; no receipt-only bypass",
                        "audit_commit": "2421b68",
                        "dedicated_reason": "Existing enrolled product control only; no manual peer hashes. Receipt and installed APK verification precede launch."}
                else:
                    if self.server.integration_snapshot is None or (not running and now - self.server.integration_at > 60):
                        head=checkout_head(cfg)
                        summary = receipt_preflight(cfg, artifacts(cfg), checkout_head=head)
                        product_summary=receipt_preflight(cfg,product_artifacts(cfg),checkout_head=head,product=True)
                        broker=broker_preflight()
                        product_ready=head is not None and product_summary["state"]=="verified" and broker["state"]=="reachable"
                        self.server.integration_snapshot = {"source_options": source_comparison(),
                            "broker": broker, "receipt": summary,
                            "product_receipt": product_summary,
                            "windows_policies": ["reference", "minimum"],
                            "dedicated_request_supported": True,
                            "dedicated_launch_enabled": product_ready,
                            "minimum_period_launch_enabled": product_ready,
                            "minimum_period_reason": "Conditional request only: native exact-endpoint driver verification + actual period readback before PCM; no receipt-only bypass",
                            "audit_commit": "2421b68",
                            "dedicated_reason": "Existing enrolled product control only; no manual peer hashes. Receipt and installed APK verification precede launch."}
                        self.server.integration_at = now
                    snapshot = self.server.integration_snapshot
            return self._json(200, snapshot)
        if path == "/api/status":
            cfg = self.server.runner.cfg
            with self.server.integration_lock:
                running=self.server.runner.active or bool(self.server.runner.process and self.server.runner.process.poll() is None)
                if self.server.setup_snapshot is None or (not running and time.monotonic()-self.server.setup_at>60):
                    self.server.setup_snapshot={source:(["Setup checks deferred during playback"] if running else check_setup(cfg,source=source))
                                                for source in ("mm1","deskfuse","direct_export")}
                    self.server.setup_at=time.monotonic()
                setup=self.server.setup_snapshot
            return self._json(200, {"connected": True, "configured": bool(cfg),
                                    "preflight": setup["mm1"],
                                    "source_preflight": setup,
                                    "token": self.server.token,
                                    "supported": {"sharing_policy":[0,1],"usage_policy":[0,1],"reserve_frames":[1440,1200,960,720,480],"buffer_bursts": [0, 2, 3], "modes": "f[01]c[01]r[01]", "seconds": list(ALLOWED_SECONDS), "mode": "startup-only", "one_factor": True,
                                                  "windows_sources": ["mm1","deskfuse","direct_export"], "engine_period": ["reference","minimum"],
                                                  "capture_policy": ["reference", "minimum"]},
                                    **self.server.runner.status()})
        if path not in WEB:
            return self._json(404, {"error": "Not found"})
        f = ROOT / WEB[path]
        try:
            data = f.read_bytes()
        except OSError:
            return self._json(404, {"error": "Site asset missing"})
        self._headers(200, TYPES[f.suffix] + "; charset=utf-8", len(data))
        self.wfile.write(data)

    def do_POST(self):
        if not self._trusted_host():
            return self._json(403, {"error": "Invalid local Host header"})
        # Consume only a bounded request body before rejecting origin/token.
        # Closing a Windows socket with unread POST bytes can reset the
        # connection instead of delivering the intended 403 response.
        try:
            length=int(self.headers.get("Content-Length","0"))
            if not 2 <= length <= 4096: raise ValueError("Request length exceeds limit")
            self.connection.settimeout(5)
            raw=self.rfile.read(length)
            if len(raw)!=length: raise ValueError("Incomplete request body")
        except (OSError,ValueError) as error:
            return self._json(400,{"error":str(error)})
        origin = self.headers.get("Origin", "")
        if origin not in (f"http://localhost:{self.server.server_port}", f"http://127.0.0.1:{self.server.server_port}"):
            return self._json(403, {"error": "Nonlocal origin rejected"})
        if self.headers.get("X-DeskFuse-Lab-Token") != self.server.token:
            return self._json(403, {"error": "Invalid session token"})
        if urlsplit(self.path).path != "/api/start":
            return self._json(404, {"error": "Not found"})
        try:
            body = json.loads(raw)
            reply = self.server.runner.start(body)
            return self._json(200, reply)
        except (ValueError, json.JSONDecodeError) as exc:
            return self._json(400, {"error": str(exc)})
        except (OSError, subprocess.SubprocessError) as exc:
            return self._json(500, {"error": "Local runner could not start: " + str(exc)})


def main() -> int:
    parser = argparse.ArgumentParser(description="DeskFuse Audio Lab local bridge (no Git writes or builds)")
    parser.add_argument("--port", type=int, default=8765)
    args = parser.parse_args()
    if not (1024 <= args.port <= 65535):
        parser.error("Port must be 1024..65535")
    try:
        cfg = load_config()
        server = LabServer(("127.0.0.1", args.port), Runner(cfg))
    except (OSError, ValueError, json.JSONDecodeError) as exc:
        print("Audio Lab setup error:", exc, file=sys.stderr)
        return 1
    print(f"DeskFuse Audio Lab local-only: http://localhost:{args.port}")
    if not cfg:
        print("No config.json yet. Copy config.example.json to config.json and edit paths to enable physical runs.")
    print("No audio session will start until you click START PHYSICAL TEST in the browser.")
    try:
        server.serve_forever(poll_interval=0.2)
    except KeyboardInterrupt:
        pass
    finally:
        server.server_close()
    return 0

if __name__ == "__main__":
    sys.exit(main())
