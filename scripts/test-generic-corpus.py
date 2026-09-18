"""Run the fixed 12 local + 20 held-out generic Scene corpus through analyze/bake/validate.

This runner owns only its timestamped artifact directory and this script. It never edits
the source projects, production code, Workshop assets, or selection manifests.
"""
from __future__ import annotations

import hashlib
import json
import os
from pathlib import Path
import shutil
import subprocess
import sys
import time
import argparse
import ctypes
from ctypes import wintypes
from datetime import datetime, timezone

ROOT = Path(__file__).resolve().parent.parent
ASSETS = Path(r"D:/Apps/Steam/steamapps/common/wallpaper_engine/assets")
LOCAL_SUMMARY = ROOT / "artifacts/library-smoke-1788783520814886300/summary.json"
HELDOUT_MANIFEST = ROOT / "artifacts/heldout-scene-corpus-20260908/steam-download-manifest.json"
HELDOUT_META = ROOT / "artifacts/heldout-scene-corpus-20260908/metadata-summary.json"
CLI_SOURCE = ROOT / "src/Baker.Cli/bin/Release/net10.0"
RENDER_SOURCE = ROOT / "build/native-release22/bin/wpe-render.exe"
FFMPEG = ROOT / ".deps/ffmpeg-encoder-gpl2/portable/ffmpeg.exe"
FFPROBE = ROOT / ".deps/ffmpeg-encoder-gpl2/portable/ffprobe.exe"
LLVM = ROOT / ".tools/llvm-mingw-22/bin"
LGPL = ROOT / ".deps/ffmpeg-lgpl21/prefix/bin"


def sha256(path: Path) -> str:
    h = hashlib.sha256()
    with path.open("rb") as f:
        for block in iter(lambda: f.read(1024 * 1024), b""):
            h.update(block)
    return h.hexdigest()


def nested_find(value, names):
    if isinstance(value, dict):
        for key, item in value.items():
            if key in names:
                yield key, item
            yield from nested_find(item, names)
    elif isinstance(value, list):
        for item in value:
            yield from nested_find(item, names)


def _job_for_process(proc):
    """Put this invocation and every child in a kill-on-close Windows Job."""
    if os.name != "nt":
        return None, False
    kernel = ctypes.WinDLL("kernel32", use_last_error=True)
    kernel.CreateJobObjectW.restype = wintypes.HANDLE
    kernel.AssignProcessToJobObject.argtypes = [wintypes.HANDLE, wintypes.HANDLE]
    kernel.SetInformationJobObject.argtypes = [wintypes.HANDLE, wintypes.INT, wintypes.LPVOID, wintypes.DWORD]
    kernel.TerminateJobObject.argtypes = [wintypes.HANDLE, wintypes.UINT]
    kernel.CloseHandle.argtypes = [wintypes.HANDLE]

    class IO_COUNTERS(ctypes.Structure):
        _fields_ = [("ReadOperationCount", ctypes.c_ulonglong), ("WriteOperationCount", ctypes.c_ulonglong),
                    ("OtherOperationCount", ctypes.c_ulonglong), ("ReadTransferCount", ctypes.c_ulonglong),
                    ("WriteTransferCount", ctypes.c_ulonglong), ("OtherTransferCount", ctypes.c_ulonglong)]
    class BASIC(ctypes.Structure):
        _fields_ = [("PerProcessUserTime", ctypes.c_longlong), ("PerJobUserTime", ctypes.c_longlong),
                    ("LimitFlags", wintypes.DWORD), ("MinimumWorkingSetSize", ctypes.c_size_t),
                    ("MaximumWorkingSetSize", ctypes.c_size_t), ("ActiveProcessLimit", wintypes.DWORD),
                    ("Affinity", ctypes.c_size_t), ("PriorityClass", wintypes.DWORD), ("SchedulingClass", wintypes.DWORD)]
    class EXTENDED(ctypes.Structure):
        _fields_ = [("BasicLimitInformation", BASIC), ("IoInfo", IO_COUNTERS),
                    ("ProcessMemoryLimit", ctypes.c_size_t), ("JobMemoryLimit", ctypes.c_size_t),
                    ("PeakProcessMemoryUsed", ctypes.c_size_t), ("PeakJobMemoryUsed", ctypes.c_size_t)]
    job = kernel.CreateJobObjectW(None, None)
    if not job:
        return None, False
    info = EXTENDED()
    info.BasicLimitInformation.LimitFlags = 0x2000  # JOB_OBJECT_LIMIT_KILL_ON_JOB_CLOSE
    if not kernel.SetInformationJobObject(job, 9, ctypes.byref(info), ctypes.sizeof(info)):
        kernel.CloseHandle(job)
        return None, False
    if not kernel.AssignProcessToJobObject(job, wintypes.HANDLE(proc._handle)):
        kernel.CloseHandle(job)
        return None, False
    return (kernel, job), True


def run_command(case_dir: Path, name: str, argv: list[str], env: dict[str, str], timeout: int = 1200):
    started = time.perf_counter()
    proc = subprocess.Popen(argv, cwd=ROOT, env=env, stdout=subprocess.PIPE, stderr=subprocess.PIPE,
                            creationflags=0x08000000)
    job_state, job_assigned = _job_for_process(proc)
    try:
        stdout, stderr = proc.communicate(timeout=timeout)
        out = stdout.decode("utf-8", "replace")
        err = stderr.decode("utf-8", "replace")
        (case_dir / f"{name}.stdout.log").write_text(out, encoding="utf-8")
        (case_dir / f"{name}.stderr.log").write_text(err, encoding="utf-8")
        parsed = None
        try:
            parsed = json.loads(out)
        except Exception:
            pass
        result = {
            "returncode": proc.returncode,
            "timed_out": False,
            "job_assigned": job_assigned,
            "seconds": round(time.perf_counter() - started, 3),
            "stdout": parsed,
            "stderr_tail": err[-4000:],
        }
        if proc.returncode != 0:
            result.update(structured_error(result["stderr_tail"]) or {})
        return result
    except subprocess.TimeoutExpired as exc:
        if job_assigned:
            job_state[0].TerminateJobObject(job_state[1], 124)
        else:
            proc.kill()
        stdout, stderr = proc.communicate()
        out = (stdout or b"").decode("utf-8", "replace")
        err = (stderr or b"").decode("utf-8", "replace")
        (case_dir / f"{name}.stdout.log").write_text(out, encoding="utf-8")
        (case_dir / f"{name}.stderr.log").write_text(err, encoding="utf-8")
        result = {"returncode": None, "timed_out": True, "job_assigned": job_assigned, "seconds": timeout, "stdout": None, "stderr_tail": err[-4000:]}
        result.update(structured_error(result["stderr_tail"]) or {"error_type": "TimeoutExpired", "error_message": "command timed out", "normalized_error": "TimeoutExpired:command timed out"})
        return result
    finally:
        if job_assigned:
            job_state[0].CloseHandle(job_state[1])


def compact_step(step):
    stdout = step.get("stdout")
    summary = None
    if isinstance(stdout, dict):
        summary = {k: stdout.get(k) for k in ("status", "error", "error_type", "loop_validation", "project_path", "video_layers", "static_layers") if k in stdout}
    return {k: step.get(k) for k in ("returncode", "timed_out", "job_assigned", "seconds", "stderr_tail", "error_type", "error_message", "normalized_error") if k in step} | {"stdout_summary": summary}


def save_json(path: Path, value):
    path.write_text(json.dumps(value, ensure_ascii=False, indent=2), encoding="utf-8")


def structured_error(stderr_tail: str):
    text = stderr_tail or ""
    decoder = json.JSONDecoder()
    objects = []
    for offset, char in enumerate(text):
        if char != "{":
            continue
        try:
            obj, _ = decoder.raw_decode(text[offset:])
        except Exception:
            continue
        objects.append(obj)
    for obj in reversed(objects):
        if isinstance(obj, dict) and (obj.get("status") == "failed" or obj.get("error_type") or obj.get("error")):
            error_type = obj.get("error_type") or obj.get("status") or "command_error"
            message = obj.get("message") or obj.get("error") or ""
            return {"error_type": error_type, "error_message": message,
                    "normalized_error": f"{error_type}:{message}"}
    return None


def main() -> int:
    parser = argparse.ArgumentParser()
    parser.add_argument("--limit", type=int, default=0)
    parser.add_argument("--start", type=int, default=1)
    parser.add_argument("--device", default="b22adf1f455b2bcc8bdbefd93eab85ee")
    args = parser.parse_args()
    stamp = datetime.now(timezone.utc).strftime("%Y%m%dT%H%M%SZ")
    out = ROOT / "artifacts" / f"generic-corpus-20260908-{stamp}"
    out.mkdir(parents=True)
    runtime = out / "ownedruntime"
    cli = runtime / "cli"
    cli.parent.mkdir(parents=True, exist_ok=True)
    shutil.copytree(CLI_SOURCE, cli)
    shutil.copy2(RENDER_SOURCE, runtime / "wpe-render.exe")
    tools = out / "tools.json"
    save_json(tools, {
        "renderer": str((runtime / "wpe-render.exe").resolve()),
        "ffmpeg": str(FFMPEG.resolve()),
        "ffprobe": str(FFPROBE.resolve()),
        "runtime_directories": [str(LLVM.resolve()), str(LGPL.resolve())],
    })
    cli_exe = cli / "wpe-baker.exe"
    env = os.environ.copy()
    env["DOTNET_ROOT"] = str((ROOT / ".dotnet").resolve())
    env["PATH"] = str((ROOT / ".dotnet").resolve()) + os.pathsep + env.get("PATH", "")

    local = json.loads(LOCAL_SUMMARY.read_text(encoding="utf-8"))["projects"]
    heldout = json.loads(HELDOUT_MANIFEST.read_text(encoding="utf-8"))["items"]
    heldout_meta = {x["id"]: x for x in json.loads(HELDOUT_META.read_text(encoding="utf-8"))["items"]}
    cases = []
    for item in local:
        cases.append({"id": item["id"], "kind": "local", "source": item["source"], "project": item["project_path"]})
    for item in heldout:
        ident = item["id"]
        base = ROOT / "artifacts/heldout-scene-corpus-20260908" / item["path"]
        project = base / "project.json"
        project_meta = json.loads(project.read_text(encoding="utf-8"))
        package = base / (Path(project_meta.get("file", "scene.json")).with_suffix(".pkg"))
        cases.append({"id": ident, "kind": "heldout", "source": str(package), "project": str(project)})
    save_json(out / "selection.json", {"count": len(cases), "cases": cases, "selection_fixed": True})

    summary = {"schema_version": 1, "status": "running", "output": str(out), "cases": []}
    save_json(out / "summary.json", summary)
    heldout_canvas = {x["id"]: x.get("canvas") for x in json.loads(HELDOUT_META.read_text(encoding="utf-8"))["items"]}

    selected_cases = cases[max(0, args.start - 1):]
    if args.limit > 0:
        selected_cases = selected_cases[:args.limit]
    previous_failure = None
    for index, case in enumerate(selected_cases, args.start):
        case_dir = out / f"{index:02d}-{case['id']}"
        case_dir.mkdir()
        started_hash = sha256(Path(case["source"]))
        result = {**case, "index": index, "source_sha256_before": started_hash, "status": "started", "steps": {}}
        try:
            # Metadata-only inspect selects portrait output dimensions without touching source.
            inspect = run_command(case_dir, "inspect", [str(cli_exe), "inspect", case["project"], "--assets", str(ASSETS), "--out", str(case_dir / "inspect.json")], env)
            result["steps"]["inspect"] = inspect
            inspect_json = inspect.get("stdout") or {}
            canvas = None
            for key, value in nested_find(inspect_json, {"orthogonal_projection", "orthogonalprojection", "canvas"}):
                if isinstance(value, dict) and "width" in value and "height" in value:
                    canvas = value
                    break
            if case["kind"] == "heldout":
                canvas = heldout_canvas.get(case["id"]) or canvas
            width, height = 960, 540
            if isinstance(canvas, dict) and float(canvas.get("height", 0)) > float(canvas.get("width", 0)):
                width, height = 540, 960
            result["dimensions"] = {"width": width, "height": height, "source_canvas": canvas}

            plan_path = case_dir / "plan.json"
            analyze = run_command(case_dir, "analyze", [str(cli_exe), "analyze", case["source"], "--assets", str(ASSETS), "--tools", str(tools), "--out", str(plan_path), "--width", str(width), "--height", str(height), "--fps", "120", "--device", args.device, "--view-mode", "preserve", "--local-seam-repair", "true", "--max-retime", "2"], env)
            result["steps"]["analyze"] = analyze
            plan = analyze.get("stdout")
            if plan is None and plan_path.exists():
                plan = json.loads(plan_path.read_text(encoding="utf-8"))
            if not isinstance(plan, dict):
                raise RuntimeError("analyze produced no JSON plan")
            result["blockers"] = plan.get("blockers", [])
            if result["blockers"]:
                result["status"] = "blocked_analyze"
            else:
                probe_request = case_dir / "probe-request.json"
                save_json(probe_request, {"schema_version": 2, "plan": plan, "output_directory": str(case_dir / "probe-bake"), "probe_frames": 240, "device_uuid": args.device})
                result["steps"]["probe_bake"] = run_command(case_dir, "probe-bake", [str(cli_exe), "bake", str(probe_request), "--tools", str(tools)], env)
                probe_json = None
                if (case_dir / "probe-bake/bake.json").exists():
                    probe_json = json.loads((case_dir / "probe-bake/bake.json").read_text(encoding="utf-8"))
                result["probe_status"] = (probe_json or {}).get("status")
                if (case_dir / "probe-bake/capture-source").exists() and (case_dir / "probe-bake/project").exists():
                    compare_request = case_dir / "probe-validate-request.json"
                    save_json(compare_request, {"schema_version": 1, "source": str(case_dir / "probe-bake/capture-source"), "candidate": str(case_dir / "probe-bake/project"), "assets": str(ASSETS), "output_directory": str(case_dir / "probe-validation"), "width": width, "height": height, "fps_numerator": 120, "fps_denominator": 1, "frames": 48, "warmup_frames": 0, "seed": 17, "user_properties": plan.get("snapshot_properties", {}), "tile_size": 64})
                    result["steps"]["probe_validate"] = run_command(case_dir, "probe-validate", [str(cli_exe), "validate", str(compare_request), "--tools", str(tools)], env)
                production_request = case_dir / "production-request.json"
                save_json(production_request, {"schema_version": 2, "plan": plan, "output_directory": str(case_dir / "production-bake"), "probe_frames": 0, "device_uuid": args.device})
                result["steps"]["production_bake"] = run_command(case_dir, "production-bake", [str(cli_exe), "bake", str(production_request), "--tools", str(tools)], env)
                production_json = None
                if (case_dir / "production-bake/bake.json").exists():
                    production_json = json.loads((case_dir / "production-bake/bake.json").read_text(encoding="utf-8"))
                result["production"] = {"status": (production_json or {}).get("status"), "loop_validation": (production_json or {}).get("loop_validation"), "error_type": (production_json or {}).get("error_type"), "error": (production_json or {}).get("error"), "project_path": (production_json or {}).get("project_path"), "video_layers": (production_json or {}).get("video_layers"), "static_layers": (production_json or {}).get("static_layers")}
                bake_status = (production_json or {}).get("status")
                # static_only：1 帧、0 个视频层，烘完等于一张静态图加全部实时，不算省电成品。
                result["status"] = "production_complete" if bake_status == "candidate_generated" else ("static_only" if bake_status == "static_only" else ("seam_rejected" if bake_status == "candidate_rejected_seam" else "production_failed"))
        except Exception as exc:
            result["status"] = "runner_error"
            result["error_type"] = type(exc).__name__
            result["error"] = str(exc)
        result["source_sha256_after"] = sha256(Path(case["source"]))
        result["source_unchanged"] = result["source_sha256_before"] == result["source_sha256_after"]
        result["log_paths"] = {name: str(case_dir / f"{name}.stdout.log") for name in result["steps"]}
        result["steps"] = {name: compact_step(step) for name, step in result["steps"].items()}
        current_failure = next((step.get("normalized_error") for step in result["steps"].values() if step.get("normalized_error")), None)
        if current_failure and current_failure == previous_failure:
            result["status"] = "paused_systemic_error"
            result["systemic_error"] = current_failure
        summary["cases"].append(result)
        summary["completed"] = index
        save_json(out / "summary.json", summary)
        print(json.dumps({"index": index, "id": case["id"], "status": result["status"], "source_unchanged": result["source_unchanged"]}, ensure_ascii=False), flush=True)
        if current_failure and current_failure == previous_failure:
            summary["status"] = "paused_systemic_error"
            save_json(out / "summary.json", summary)
            break
        previous_failure = current_failure
    summary["status"] = "complete"
    save_json(out / "summary.json", summary)
    print(json.dumps({"status": summary["status"], "output": str(out), "cases": len(cases)}), flush=True)
    return 0


if __name__ == "__main__":
    raise SystemExit(main())
