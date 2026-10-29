#!/usr/bin/env python3
"""Source/build evidence only. Never installs, probes devices or qualifies hardware.

This schema is separate from Audio Lab's playback-admission receipt. A successful
build receipt is not evidence about installed drivers, ACLs or physical behavior.
"""
from __future__ import annotations

import argparse
from datetime import datetime, timezone
import hashlib
import json
from pathlib import Path
import shutil
import subprocess
import sys
import time

SOURCE_SCHEMA = "deskfuse.source-snapshot.v1"
CHECK_SCHEMA = "deskfuse.executed-check.v1"
BUILD_SCHEMA = "deskfuse.build-receipt.v1"


def sha256(path: Path) -> str:
    digest = hashlib.sha256()
    with path.open("rb") as stream:
        for block in iter(lambda: stream.read(1024 * 1024), b""):
            digest.update(block)
    return digest.hexdigest()


def canonical_hash(value) -> str:
    return hashlib.sha256(json.dumps(value, sort_keys=True, ensure_ascii=True,
                                     separators=(",", ":")).encode()).hexdigest()


def write_json(path: Path, value) -> None:
    path.parent.mkdir(parents=True, exist_ok=True)
    path.write_text(json.dumps(value, indent=2, ensure_ascii=True) + "\n", encoding="utf-8")


def git(root: Path, *args: str) -> bytes:
    return subprocess.check_output(["git", "-C", str(root), *args])


def snapshot(root: Path) -> dict:
    root = root.resolve(strict=True)
    head = git(root, "rev-parse", "HEAD").decode().strip()
    status = git(root, "status", "--porcelain=v1", "-z", "--untracked-files=all")
    names = set(git(root, "ls-files", "-z", "--cached", "--others",
                    "--exclude-standard").decode("utf-8").split("\0")) - {""}
    files = []
    for name in sorted(names):
        path = root / name
        if path.is_symlink():
            files.append({"path": name, "link": str(path.readlink())})
        elif path.is_file():
            files.append({"path": name, "sha256": sha256(path)})
        elif not path.exists():
            files.append({"path": name, "deleted": True})
        else:
            raise ValueError(f"Unsupported source entry (including submodule): {name}")
    diff = git(root, "diff", "--binary", "HEAD", "--")
    if (head != git(root, "rev-parse", "HEAD").decode().strip() or
            status != git(root, "status", "--porcelain=v1", "-z", "--untracked-files=all")):
        raise ValueError("Source changed during snapshot; retry from a stable checkout")
    identity = {"commit": head, "files": files,
                "status_sha256": hashlib.sha256(status).hexdigest(),
                "diff_sha256": hashlib.sha256(diff).hexdigest()}
    return {"schema": SOURCE_SCHEMA, "root": str(root),
            "branch": git(root, "branch", "--show-current").decode().strip(),
            "dirty": bool(status), "snapshot_sha256": canonical_hash(identity),
            "atomic": False, **identity}


def execute_check(args) -> int:
    command = args.command[1:] if args.command[:1] == ["--"] else args.command
    if not command:
        raise ValueError("check requires a command after --")
    args.log.parent.mkdir(parents=True, exist_ok=True)
    source_before = snapshot(args.root)["snapshot_sha256"]
    started = datetime.now(timezone.utc).isoformat()
    before = time.monotonic()
    with args.log.open("wb") as log:
        try:
            result = subprocess.run(command, cwd=args.root, stdout=log,
                                    stderr=subprocess.STDOUT, check=False)
            code = result.returncode
        except OSError as error:
            log.write((str(error) + "\n").encode())
            code = 127
    source_after = snapshot(args.root)["snapshot_sha256"]
    record = {"schema": CHECK_SCHEMA, "name": args.name, "kind": args.kind,
              "command": command, "cwd": str(args.root.resolve()),
              "started_utc": started, "duration_seconds": time.monotonic() - before,
              "exit_code": code, "passed": code == 0 and source_before == source_after,
              "source_before_sha256": source_before, "source_after_sha256": source_after,
              "log": str(args.log.resolve()), "log_sha256": sha256(args.log)}
    write_json(args.report, record)
    with args.log.open("rb") as log:
        shutil.copyfileobj(log, sys.stdout.buffer)
    return code if code != 0 and 0 <= code <= 255 else (0 if record["passed"] else 1)


def build_receipt(root: Path, baseline_path: Path, reports: list[Path],
                  artifacts: list[Path], component: str, profile: str,
                  architecture: str) -> dict:
    baseline = json.loads(baseline_path.read_text(encoding="utf-8"))
    if baseline.get("schema") != SOURCE_SCHEMA:
        raise ValueError("Unsupported source snapshot schema")
    current = snapshot(root)
    problems = []
    if baseline.get("snapshot_sha256") != current["snapshot_sha256"]:
        problems.append("Source differs from the pre-build snapshot")
    checks = []
    for path in reports:
        if not path.is_file():
            problems.append(f"Missing executed-check report: {path}")
            continue
        record = json.loads(path.read_text(encoding="utf-8"))
        if record.get("schema") != CHECK_SCHEMA:
            raise ValueError(f"Unsupported executed-check schema: {path}")
        log = Path(record["log"])
        if not log.is_file() or sha256(log) != record["log_sha256"]:
            problems.append(f"Missing or changed check log: {path}")
        if record.get("passed") is not True or record.get("exit_code") != 0:
            problems.append(f"Check failed: {record.get('name')}")
        if (record.get("source_before_sha256") != baseline.get("snapshot_sha256") or
                record.get("source_after_sha256") != baseline.get("snapshot_sha256")):
            problems.append(f"Check source does not match the pre-build snapshot: {path}")
        checks.append(record)
    if not checks:
        problems.append("No executed checks were recorded")
    outputs = []
    for path in artifacts:
        if not path.is_file():
            problems.append(f"Missing artifact: {path}")
        else:
            outputs.append({"path": str(path.resolve()), "bytes": path.stat().st_size,
                            "sha256": sha256(path)})
    return {"schema": BUILD_SCHEMA, "created_utc": datetime.now(timezone.utc).isoformat(),
            "component": component, "declared_profile": profile,
            "declared_architecture": architecture, "source": current,
            "pre_build_snapshot_sha256": baseline.get("snapshot_sha256"),
            "checks": checks, "artifacts": outputs, "problems": problems,
            "recorded_checks_passed": not problems,
            "installed_state_verified": False, "hardware_qualified": False,
            "attestation": False}


def main() -> int:
    parser = argparse.ArgumentParser(description=__doc__)
    commands = parser.add_subparsers(dest="operation", required=True)
    source = commands.add_parser("source")
    check = commands.add_parser("check")
    create = commands.add_parser("create")
    for command in (source, check, create):
        command.add_argument("--root", type=Path, default=Path.cwd())
    source.add_argument("--output", type=Path, required=True)
    check.add_argument("--report", type=Path, required=True)
    check.add_argument("--log", type=Path, required=True)
    check.add_argument("--name", required=True)
    check.add_argument("--kind", choices=("build", "test", "toolchain", "verification"), required=True)
    check.add_argument("command", nargs=argparse.REMAINDER)
    create.add_argument("--baseline", type=Path, required=True)
    create.add_argument("--check-report", type=Path, action="append", required=True)
    create.add_argument("--artifact", type=Path, action="append", default=[])
    create.add_argument("--artifact-dir", type=Path, action="append", default=[])
    create.add_argument("--component", required=True)
    create.add_argument("--profile", required=True)
    create.add_argument("--architecture", required=True)
    create.add_argument("--output", type=Path, required=True)
    args = parser.parse_args()
    if args.operation == "source":
        write_json(args.output, snapshot(args.root))
        return 0
    if args.operation == "check":
        return execute_check(args)
    artifacts = list(args.artifact)
    for directory in args.artifact_dir:
        files = sorted(path for path in directory.rglob("*") if path.is_file()) if directory.is_dir() else []
        artifacts.extend(files or [directory])
    receipt = build_receipt(args.root, args.baseline, args.check_report, artifacts,
                            args.component, args.profile, args.architecture)
    write_json(args.output, receipt)
    for problem in receipt["problems"]:
        print(problem, file=sys.stderr)
    return 0 if receipt["recorded_checks_passed"] else 1


if __name__ == "__main__":
    try:
        raise SystemExit(main())
    except (OSError, ValueError, KeyError, subprocess.CalledProcessError) as error:
        print(f"Receipt failed: {error}", file=sys.stderr)
        raise SystemExit(1)
