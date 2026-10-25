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
from urllib.parse import urlsplit
from integration import (broker_preflight, receipt_preflight, receipt_path, checkout_head,
                         source_preflight, source_comparison)

ROOT = Path(__file__).resolve().parent
WEB = {"/": "index.html", "/index.html": "index.html", "/app.js": "app.js",
       "/styles.css": "styles.css", "/live.js": "live.js", "/live.css": "live.css",
       "/atlas-data.js": "atlas-data.js", "/atlas.js": "atlas.js", "/atlas.css": "atlas.css",
       "/windows-lab.js": "windows-lab.js", "/windows-lab.css": "windows-lab.css"}
TYPES = {".html": "text/html", ".js": "text/javascript", ".css": "text/css"}
MODES = re.compile(r"^f[01]c[01]r[01]$")
PROFILE = re.compile(r"^[a-zA-Z0-9_-]{0,35}$")
DEFAULT_ENDPOINT = "{0.0.0.00000000}.{2ae075dc-0b84-43cf-bade-f86449a5f6fa}"
ALLOWED_SECONDS = (30, 120)
OBSERVED_METRICS = (
    "underrun_events", "reprime_events", "xrun_count", "xruns", "buffer_frames",
    "capacity_frames", "clipped_samples", "pre_quic_missing_frames", "quiche_queue_full",
    "qpc_future", "qpc_invalid", "queued_playable_frames", "playable_frames", "primed",
    "anchor_replies", "reorder_full", "budget_drops", "source_missing_frames",
)
OBSERVED_SET = frozenset(OBSERVED_METRICS)


def parse_observed_lines(contents: str) -> dict:
    # The runner retains stdout/stderr on disk; report observed counters only.
    # 'AUDIO_BACKEND' records requests and capability, not speaker latency.
    found = {}
    for line in contents.splitlines():
        if not line.startswith(("TRACE Android RECONSTRUCTION", "AUDIO Android speaker",
                                "AUDIO_BACKEND", "DONE Windows WASAPI", "DONE Android sink")):
            continue
        for key, value in re.findall(r"\b([a-z][a-z0-9_]*)=(-?[0-9]+)\b", line):
            if key in OBSERVED_SET:
                found[key] = int(value)
    return found


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


def check_setup(cfg: dict, *, verify_hashes: bool = False) -> list[str]:
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
    for kind, path in artifacts(cfg).items():
        if not path.is_file():
            failures.append(f"Missing {kind}: configure/build the matching Release binary.")
    adb = cfg.get("adb_path")
    if adb and not Path(adb).is_file():
        failures.append("Configured adb_path does not exist.")
    elif not adb and not shutil.which("adb"):
        failures.append("ADB is not on PATH and adb_path is not configured.")
    if not isinstance(cfg.get("endpoint_id"), str) or not cfg.get("endpoint_id", "").startswith("{0.0.0.00000000}."):
        failures.append("Explicit MM-1 endpoint_id is required; no Windows default-device fallback")
    for k in ("ca.pem", "server.pem", "server.key"):
        if not (cfg_path(cfg, "keys_dir") / k).is_file():
            failures.append(f"Missing locally provisioned lab credential file: {k}.")
    branch = cfg.get("expected_branch", "experiment/vnext-audio-latency")
    if not isinstance(branch, str) or branch.startswith("reference/") or branch in ("main", "master"):
        failures.append("Expected branch must be an isolated development branch.")
    if not failures:
        try:
            out = subprocess.run(["git", "-C", str(repo), "branch", "--show-current"],
                                 capture_output=True, text=True, timeout=5, check=True)
            if out.stdout.strip() != branch:
                failures.append(f"Selected checkout is on {out.stdout.strip() or 'detached HEAD'}, not {branch}.")
        except Exception:
            failures.append("Could not verify Git checkout branch.")
    if verify_hashes and not failures:
        report = receipt_preflight(cfg, artifacts(cfg), checkout_head=checkout_head(cfg))
        failures.extend(report["problems"])
    return failures


def build_command(cfg: dict, *, bursts: int, modes: str, seconds: int, label: str,
                  source: str = "mm1", engine_period: str = "reference",
                  capture_policy: str = "reference") -> list[str]:
    if source not in ("mm1", "deskfuse") or engine_period not in ("reference", "minimum") or capture_policy not in ("reference", "minimum"):
        raise ValueError("Unsupported Windows source or period policy")
    if source == "deskfuse":
        raise ValueError("Dedicated endpoint is not authorized: no trusted product enrollment-to-QUIC certificate binding exists in this lab")
    if bursts not in (0, 2, 3) or not MODES.fullmatch(modes) or seconds not in ALLOWED_SECONDS:
        raise ValueError("Unsupported startup parameters")
    if not PROFILE.fullmatch(label):
        raise ValueError("Use up to 35 ASCII letters, digits, underscores or hyphens for labels")
    # One-factor protocol: safe initial reference is buffer 0 + f1c1r1.
    # Other matrix choices remain a model, not executable settings.
    modified = int(bursts != 0) + sum(modes[i] != "1" for i in (1, 3, 5)) + int(engine_period != "reference") + int(capture_policy != "reference")
    if modified > 1:
        raise ValueError("Only one difference from the listening reference is allowed per physical test")
    stamp = datetime.now(timezone.utc).strftime("%Y%m%d-%H%M%S")
    profile = "audiolab-" + stamp + ("-" + label if label else "") + f"-mm1-{engine_period}-{capture_policy}-b{bursts}-{modes}"
    repo = cfg_path(cfg, "repo_root")
    cmd = ["powershell.exe", "-NoProfile", "-ExecutionPolicy", "Bypass", "-File",
           str(repo / "vnext/qualification/run-physical-audio.ps1"),
           "-EndpointId", cfg.get("endpoint_id", DEFAULT_ENDPOINT),
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
           "-Seconds", str(seconds), "-Profile", profile]
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

    def _collect(self, proc):
        try:
            for line in proc.stdout:
                line = line.strip()
                with self.lock:
                    if line.startswith("Evidence folder:"):
                        self.last_evidence = line.partition(":")[2].strip()
                    self.log.append(line[:350])
        finally:
            code = proc.wait()
            with self.lock:
                self.return_code = code
                self.finished_at = utc_now()
                self.log.append("Physical session finished (runner exit code " + str(code) + ").")

    def start(self, body: dict):
        if not isinstance(body, dict):
            raise ValueError("JSON request must be an object")
        if isinstance(body.get("buffer_bursts"), bool) or isinstance(body.get("seconds"), bool):
            raise ValueError("Invalid numeric parameter")
        bursts = body.get("buffer_bursts", 0)
        modes = body.get("modes", "f1c1r1")
        seconds = body.get("seconds", 30)
        label = body.get("label", "")
        source = body.get("source", "mm1")
        engine_period = body.get("engine_period", "reference")
        capture_policy = body.get("capture_policy", "reference")
        if not isinstance(bursts, int) or not isinstance(seconds, int) or not isinstance(modes, str) or not isinstance(label, str):
            raise ValueError("Wrong parameter types")
        if not all(isinstance(x, str) for x in (source, engine_period, capture_policy)):
            raise ValueError("Invalid Windows source selection")
        cmd = build_command(self.cfg, bursts=bursts, modes=modes, seconds=seconds, label=label,
                            source=source, engine_period=engine_period, capture_policy=capture_policy)
        with self.lock:
            if self.process is not None and self.process.poll() is None:
                raise ValueError("A physical session is already running")
            problems = check_setup(self.cfg, verify_hashes=True)
            if not problems:
                report = receipt_preflight(self.cfg, artifacts(self.cfg), checkout_head=checkout_head(self.cfg))
                problems.extend(source_preflight(source, engine_period, capture_policy, report))
            if problems:
                raise ValueError("Preflight failed: " + "; ".join(problems))
            env = os.environ.copy()
            adb = self.cfg.get("adb_path")
            if adb:
                env["PATH"] = str(Path(adb).parent) + os.pathsep + env.get("PATH", "")
            env.pop("DESKFUSE_WASAPI_PACKET_TRACE", None)
            self.log.clear()
            self.last_evidence = None
            self.finished_at = None
            self.return_code = None
            self.started_at = utc_now()
            self.started_meta = {"buffer_bursts": bursts, "modes": modes, "seconds": seconds, "label": label,
                                 "source": source, "engine_period": engine_period, "capture_policy": capture_policy}
            self.last_profile = next(cmd[i+1] for i, v in enumerate(cmd[:-1]) if v == "-Profile")
            self.log.append("Launching existing GitHub qualification runner; matching Release hashes verified.")
            self.process = subprocess.Popen(cmd, cwd=str(cfg_path(self.cfg, "repo_root")),
                                            env=env, stdout=subprocess.PIPE, stderr=subprocess.STDOUT,
                                            text=True, encoding="utf-8", errors="replace", bufsize=1,
                                            creationflags=getattr(subprocess, "CREATE_NO_WINDOW", 0))
            threading.Thread(target=self._collect, args=(self.process,), daemon=True).start()
        return {"started": True, "profile": self.last_profile}

    def status(self) -> dict:
        with self.lock:
            running = bool(self.process is not None and self.process.poll() is None)
            status = {"running": running, "started_at": self.started_at,
                      "finished_at": self.finished_at, "return_code": self.return_code,
                      "profile": self.last_profile, "evidence_folder": self.last_evidence,
                      "session": self.started_meta, "log": list(self.log)[-45:]}
        evidence = self.last_evidence
        if evidence is None and self.last_profile:
            # The checked-in PowerShell runner creates its timestamped folder
            # before writing any audio. Search only the configured evidence root.
            try:
                folders = (p for p in cfg_path(self.cfg, "evidence_root").iterdir()
                           if p.is_dir() and self.last_profile in p.name)
                evidence = str(max(folders, key=lambda p: p.stat().st_mtime))
            except (OSError, ValueError):
                pass
        if evidence:
            status["evidence_folder"] = evidence
            root = cfg_path(self.cfg, "evidence_root").resolve()
            candidate = Path(evidence).resolve()
            if candidate.parent == root:
                records = {}
                for name in ("windows-source.stdout.txt", "windows-source.stderr.txt", "android-receiver.txt"):
                    records.update(parse_observed_lines(tail_file(candidate / name)))
                status["telemetry"] = records
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
                running = bool(self.server.runner.process and self.server.runner.process.poll() is None)
                # Do not rehash binaries, query broker or touch local evidence
                # repeatedly while real-time audio is running.
                if self.server.integration_snapshot is None or (not running and now - self.server.integration_at > 60):
                    summary = receipt_preflight(cfg, artifacts(cfg), checkout_head=checkout_head(cfg))
                    self.server.integration_snapshot = {"source_options": source_comparison(),
                        "broker": broker_preflight(), "receipt": summary,
                        "windows_policies": ["reference", "minimum"],
                        "dedicated_launch_enabled": False,
                        "dedicated_reason": "Requires trusted enrollment/certificate binding from Codex Ultra; no manual peer hashes"}
                    self.server.integration_at = now
                snapshot = self.server.integration_snapshot
            return self._json(200, snapshot)
        if path == "/api/status":
            cfg = self.server.runner.cfg
            return self._json(200, {"connected": True, "configured": bool(cfg),
                                    "preflight": check_setup(cfg, verify_hashes=False),
                                    "token": self.server.token,
                                    "supported": {"buffer_bursts": [0, 2, 3], "modes": "f[01]c[01]r[01]", "seconds": list(ALLOWED_SECONDS), "mode": "startup-only", "one_factor": True,
                                                  "windows_sources": ["mm1"], "engine_period": ["reference", "minimum"],
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
        origin = self.headers.get("Origin", "")
        if origin not in (f"http://localhost:{self.server.server_port}", f"http://127.0.0.1:{self.server.server_port}"):
            return self._json(403, {"error": "Nonlocal origin rejected"})
        if self.headers.get("X-DeskFuse-Lab-Token") != self.server.token:
            return self._json(403, {"error": "Invalid session token"})
        if urlsplit(self.path).path != "/api/start":
            return self._json(404, {"error": "Not found"})
        try:
            length = int(self.headers.get("Content-Length", "0"))
            if not (2 <= length <= 4096):
                raise ValueError("Request length exceeds limit")
            body = json.loads(self.rfile.read(length))
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
