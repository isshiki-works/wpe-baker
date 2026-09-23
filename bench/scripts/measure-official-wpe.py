"""Read-only Windows measurement for one already-running Wallpaper Engine process.

This tool neither starts nor stops Wallpaper Engine, changes desktop state, requests
elevation, or stops ETW sessions.  PresentMon receives a unique ETW session name.
"""
from pathlib import Path
import argparse, csv, ctypes, io, json, math, os, re, statistics, subprocess, threading, time, uuid
from ctypes import wintypes as W


def parse_args():
    parser = argparse.ArgumentParser(description=__doc__)
    parser.add_argument("--pid", type=int, required=True, help="Already-running target process ID")
    parser.add_argument("--label", required=True, help="Measurement label recorded in metadata")
    parser.add_argument("--seconds", type=int, default=30)
    parser.add_argument("--output", required=True, help="New output directory")
    parser.add_argument("--target-fps", type=float, default=120)
    parser.add_argument("--presentmon", help="Local PresentMon executable; defaults to the verified bundled copy")
    result = parser.parse_args()
    if result.pid <= 0 or result.seconds <= 0 or result.target_fps <= 0:
        parser.error("--pid, --seconds, and --target-fps must be positive")
    return result


def decode(data):
    if data.startswith(b"\xff\xfe"):
        return data.decode("utf-16")
    try:
        return data.decode("utf-8-sig")
    except UnicodeDecodeError:
        return data.decode("mbcs", errors="replace")


def stat(values):
    if not values:
        return None
    ordered = sorted(values)
    def percentile(percent):
        index = (len(ordered) - 1) * percent
        low, high = int(index), min(int(index) + 1, len(ordered) - 1)
        return ordered[low] + (ordered[high] - ordered[low]) * (index - low)
    return {"samples": len(values), "mean": statistics.mean(values), "median": statistics.median(values),
            "p50": percentile(.50), "p95": percentile(.95), "p99": percentile(.99),
            "minimum": min(values), "maximum": max(values)}


def numeric(value):
    try:
        return float(value.strip())
    except (AttributeError, ValueError):
        return None


def query_counters(typeperf, category, pid, errors):
    try:
        result = subprocess.run([str(typeperf), "-qx", category], capture_output=True, timeout=20,
                                creationflags=subprocess.CREATE_NO_WINDOW)
        if result.returncode:
            errors.append({"stage": "typeperf_query", "category": category, "stderr": decode(result.stderr), "stdout": decode(result.stdout)})
            return []
        return [line.strip() for line in decode(result.stdout).splitlines() if f"(pid_{pid}_" in line]
    except Exception as error:
        errors.append({"stage": "typeperf_query", "category": category, "error": repr(error)})
        return []


def luid(path):
    match = re.search(r"luid_(0x[0-9a-f]+_0x[0-9a-f]+_phys_[^_\\)]+)_eng_", path, re.IGNORECASE)
    return match.group(1) if match else "unparsed_adapter"


def engine_kind(path):
    match = re.search(r"engtype_([^)]*)\)", path, re.IGNORECASE)
    return match.group(1) if match else "other"


def parse_typeperf(data, counters):
    rows = list(csv.reader(io.StringIO(decode(data))))
    header = next((row for row in rows if len(row) == len(counters) + 1 and "PDH-CSV" in row[0]), None)
    if header is None:
        return [], "No matching CSV header was produced."
    values = []
    for row in rows:
        if row == header or len(row) != len(header):
            continue
        values.append([{"raw": item, "value": numeric(item)} for item in row[1:]])
    return values, None


def presentmon_result(path, target_fps, exit_code, stderr):
    if exit_code != 0:
        detail = decode(stderr).strip()
        return {"status": "permission_denied" if "access denied" in detail.lower() else "failed",
                "exit_code": exit_code, "stderr": detail,
                "reason": "PresentMon did not complete successfully; no FPS result is accepted."}
    return presentmon_summary(path, target_fps)


def presentmon_summary(path, target_fps):
    if not path.exists() or path.stat().st_size == 0:
        return {"status": "not_measured", "reason": "PresentMon produced no CSV; the target may have had no active presentation."}
    try:
        with path.open("r", encoding="utf-8-sig", newline="") as file:
            rows = list(csv.DictReader(file))
    except Exception as error:
        return {"status": "unreadable", "error": repr(error)}
    if not rows:
        return {"status": "not_measured", "reason": "PresentMon CSV has no presentation rows."}
    chains = {}
    for row in rows:
        key = row.get("SwapChainAddress") or row.get("SwapChain") or row.get("SwapChainID") or "unidentified_swapchain"
        chains.setdefault(key, []).append(row)
    report = {}
    for chain, entries in chains.items():
        intervals = []
        dropped = 0
        dropped_available = False
        for entry in entries:
            fields = {key.lower(): value for key, value in entry.items() if key is not None}
            value = numeric(fields.get("msbetweenpresents"))
            if value is None:
                value = numeric(fields.get("msbetweendisplaychange"))
            if value is not None and value > 0:
                intervals.append(value)
            if "dropped" in fields or "wasdropped" in fields:
                dropped_available = True
                flag = (fields.get("dropped") or fields.get("wasdropped") or "").lower()
                if flag in ("1", "true", "yes"):
                    dropped += 1
        presentation_fps = [1000 / item for item in intervals]
        report[chain] = {"rows": len(entries), "interval_ms": stat(intervals),
                         "effective_fps": 1000 / statistics.mean(intervals) if intervals else None,
                         "instantaneous_fps": stat(presentation_fps),
                         "dropped_rows": dropped if dropped_available else None, "dropped_row_ratio": dropped / len(entries) if dropped_available else None,
                         "below_target_fps_ratio": (sum(item < target_fps for item in presentation_fps) / len(presentation_fps)) if presentation_fps else None}
    return {"status": "sampled", "swapchains": report, "csv_rows": len(rows),
            "metric_note": "Intervals are PresentMon per-swapchain measurements; missing or unsupported columns remain unmeasured."}


def summarize_gpu(samples, counters, gpu):
    adapters = {}
    if not samples:
        return adapters
    grouped = {}
    for index, path in enumerate(counters):
        if path in gpu:
            key = {"3d": "3d_percent", "videodecode": "video_decode_percent", "copy": "copy_percent"}[engine_kind(path).lower()]
            grouped.setdefault((luid(path), key), []).append(index)
    for (adapter, key), indices in grouped.items():
        counters_report = []
        for index in indices:
            invalid = [row[index]["raw"] for row in samples if row[index]["value"] is None or not math.isfinite(row[index]["value"]) or row[index]["value"] < 0 or row[index]["value"] > 100]
            counters_report.append({"counter_path": counters[index], "valid_samples": len(samples) - len(invalid),
                                    "invalid_sample_count": len(invalid), "invalid_raw_values": invalid})
        aggregate = []
        for row in samples:
            values = [row[index]["value"] for index in indices]
            if all(value is not None and math.isfinite(value) and 0 <= value <= 100 for value in values): aggregate.append(sum(values))
        adapters.setdefault(adapter, {})[key] = {"utilization_percent": stat(aggregate), "timepoints": len(samples),
            "missing_timepoints": len(samples) - len(aggregate), "valid_coverage_ratio": len(aggregate) / len(samples), "counters": counters_report}
    return adapters


def main():
    args = parse_args()
    output = Path(args.output).resolve()
    if output.exists():
        raise FileExistsError("--output must be a new directory: " + str(output))
    root = Path(__file__).resolve().parents[2]  # bench/scripts/ → 仓库根
    presentmon = Path(args.presentmon).resolve() if args.presentmon else root / ".tools/presentmon/PresentMon-2.5.1-x64.exe"
    if not presentmon.is_file():
        raise FileNotFoundError("PresentMon executable is missing: " + str(presentmon))
    output.mkdir(parents=True)
    flags = subprocess.CREATE_NO_WINDOW
    errors = []
    metadata = {"label": args.label, "pid": args.pid, "seconds_requested": args.seconds, "target_fps": args.target_fps,
                "started_utc": time.strftime("%Y-%m-%dT%H:%M:%SZ", time.gmtime()), "presentmon": str(presentmon),
                "scope": "Read-only measurements of one specified existing process. No desktop or application control occurred.",
                "power_scope": "NVIDIA board power is the total board reading, not an increment attributable to this wallpaper.",
                "gpu_scope": "GPU Engine counters are grouped by adapter LUID from counter paths; process engine attribution is not a per-wallpaper isolation guarantee."}
    (output / "metadata.json").write_text(json.dumps(metadata, indent=2) + "\n", encoding="utf-8")
    typeperf = Path(os.environ.get("SystemRoot", r"C:\\Windows")) / "System32/typeperf.exe"
    counter_errors = []
    gpu = query_counters(typeperf, "GPU Engine", args.pid, counter_errors)
    gpu = [path for path in gpu if path.endswith("\\Utilization Percentage") and engine_kind(path).lower() in ("3d", "videodecode", "copy")]
    memory = [path for path in query_counters(typeperf, "GPU Process Memory", args.pid, counter_errors)
              if path.endswith("\\Dedicated Usage") or path.endswith("\\Shared Usage")]
    counters = gpu + memory
    (output / "counter-paths.json").write_text(json.dumps(counters, indent=2) + "\n", encoding="utf-8")
    (output / "counter-discovery-errors.json").write_text(json.dumps(counter_errors, indent=2) + "\n", encoding="utf-8")
    kernel = ctypes.WinDLL("kernel32", use_last_error=True)
    kernel.OpenProcess.argtypes = [W.DWORD, W.BOOL, W.DWORD]; kernel.OpenProcess.restype = W.HANDLE
    kernel.GetProcessTimes.argtypes = [W.HANDLE, *([ctypes.POINTER(W.FILETIME)] * 4)]; kernel.GetProcessTimes.restype = W.BOOL
    kernel.CloseHandle.argtypes = [W.HANDLE]
    handle = kernel.OpenProcess(0x1000, False, args.pid)
    cpu_error = None
    def cpu_seconds():
        times = [W.FILETIME() for _ in range(4)]
        if not kernel.GetProcessTimes(handle, *map(ctypes.byref, times)):
            raise ctypes.WinError(ctypes.get_last_error())
        return sum((item.dwHighDateTime << 32) | item.dwLowDateTime for item in times[2:]) * 1e-7
    power, power_errors, stop = [], [], threading.Event()
    smi = Path(os.environ.get("SystemRoot", r"C:\\Windows")) / "System32/nvidia-smi.exe"
    gpu_identity = None
    if smi.exists():
        try:
            identity = subprocess.run([str(smi), "-i", "0", "--query-gpu=name,uuid,pci.bus_id", "--format=csv,noheader,nounits"], capture_output=True, timeout=5, creationflags=flags)
            if identity.returncode: raise RuntimeError(decode(identity.stderr))
            name, gpu_uuid, pci_bus_id = next(csv.reader(io.StringIO(decode(identity.stdout))))
            gpu_identity = {"index": 0, "name": name, "uuid": gpu_uuid, "pci_bus_id": pci_bus_id}
        except Exception as error:
            power_errors.append("identity: " + repr(error))
    else:
        power_errors.append("nvidia-smi.exe is unavailable")
    def poll_power():
        if not smi.exists():
            power_errors.append("nvidia-smi.exe is unavailable")
            return
        while not stop.is_set():
            try:
                result = subprocess.run([str(smi), "-i", "0", "--query-gpu=power.draw,clocks.current.graphics", "--format=csv,noheader,nounits"], capture_output=True, timeout=5, creationflags=flags)
                if result.returncode: raise RuntimeError(decode(result.stderr))
                fields = next(csv.reader(io.StringIO(decode(result.stdout))))
                power.append({"time_utc": time.time(), "board_watts": float(fields[0]), "graphics_clock_mhz": float(fields[1])})
            except Exception as error:
                power_errors.append(repr(error)); return
            stop.wait(1)
    session = "wpe-baker-" + uuid.uuid4().hex
    present_csv = output / "presentmon-v1.csv"
    present_command = [str(presentmon), "--v1_metrics", "--process_id", str(args.pid), "--timed", str(args.seconds), "--terminate_after_timed", "--no_console_stats", "--no_track_input", "--session_name", session, "--output_file", str(present_csv)]
    (output / "presentmon-command.json").write_text(json.dumps(present_command, indent=2) + "\n", encoding="utf-8")
    power_thread = threading.Thread(target=poll_power, daemon=True)
    typeperf_process = None
    present_process = None
    before_cpu = None
    started = time.perf_counter()
    try:
        if handle:
            try: before_cpu = cpu_seconds()
            except Exception as error: cpu_error = repr(error)
        else: cpu_error = repr(ctypes.WinError(ctypes.get_last_error()))
        power_thread.start()
        present_process = subprocess.Popen(present_command, stdout=subprocess.PIPE, stderr=subprocess.PIPE, creationflags=flags)
        if counters:
            typeperf_process = subprocess.Popen([str(typeperf), *counters, "-si", "1", "-sc", str(args.seconds)], stdout=subprocess.PIPE, stderr=subprocess.PIPE, creationflags=flags)
        drained = {}
        def drain(name, process):
            try: drained[name] = process.communicate(timeout=args.seconds + 30)
            except subprocess.TimeoutExpired:
                process.kill(); drained[name] = process.communicate(); errors.append(name + " timed out and only its own process was terminated.")
        drains = [threading.Thread(target=drain, args=("presentmon", present_process), daemon=True)]
        if typeperf_process: drains.append(threading.Thread(target=drain, args=("typeperf", typeperf_process), daemon=True))
        for thread in drains: thread.start()
        for thread in drains: thread.join()
        present_stdout, present_stderr = drained.get("presentmon", (b"", b""))
        typeperf_stdout, typeperf_stderr = drained.get("typeperf", (b"", b""))
        if handle:
            try: after_cpu = cpu_seconds()
            except Exception as error: cpu_error = cpu_error or repr(error)
        else: after_cpu = None
        elapsed = time.perf_counter() - started
    finally:
        stop.set(); power_thread.join(timeout=7)
        if handle:
            kernel.CloseHandle(handle)
    (output / "presentmon.stdout.log").write_bytes(locals().get("present_stdout", b""))
    (output / "presentmon.stderr.log").write_bytes(locals().get("present_stderr", b""))
    (output / "typeperf.stdout.csv").write_bytes(locals().get("typeperf_stdout", b""))
    (output / "typeperf.stderr.log").write_bytes(locals().get("typeperf_stderr", b""))
    samples, typeperf_parse_error = parse_typeperf(locals().get("typeperf_stdout", b""), counters) if counters else ([], "No matching GPU process counters were found.")
    adapters = summarize_gpu(samples, counters, gpu)
    def memory_stats(suffix):
        indices = [index for index, path in enumerate(counters) if path.endswith(suffix)]
        values = [sum(row[index]["value"] for index in indices) for row in samples
                  if all(row[index]["value"] is not None and math.isfinite(row[index]["value"]) for index in indices)]
        return stat(values) if values else None
    presentation = presentmon_result(present_csv, args.target_fps, present_process.returncode, present_stderr)
    if presentation["status"] in ("permission_denied", "failed"):
        errors.append({"stage": "presentmon", **presentation})
    report = {"status": "sampled" if presentation.get("status") == "sampled" else "partial_or_not_measured",
              "metadata": metadata, "elapsed_seconds": elapsed, "presentmon": presentation,
              "typeperf": {"samples": len(samples), "parse_error": typeperf_parse_error, "adapters": adapters,
                           "dedicated_bytes": memory_stats("\\Dedicated Usage"), "shared_bytes": memory_stats("\\Shared Usage")},
              "cpu": {"process_cpu_seconds": (after_cpu - before_cpu) if before_cpu is not None and after_cpu is not None else None,
                      "one_core_percent": (100 * (after_cpu - before_cpu) / elapsed) if before_cpu is not None and after_cpu is not None else None,
                      "machine_percent": (100 * (after_cpu - before_cpu) / elapsed / os.cpu_count()) if before_cpu is not None and after_cpu is not None else None,
                      "error": cpu_error}, "nvidia_board_power": {"gpu_identity": gpu_identity, "watts": stat([item["board_watts"] for item in power]), "samples": power, "errors": power_errors},
              "errors": errors, "limitations": [metadata["scope"], metadata["power_scope"], metadata["gpu_scope"], "No CSV, counter samples, or permission-denied reads are represented as zero or a pass."]}
    (output / "report.json").write_text(json.dumps(report, indent=2) + "\n", encoding="utf-8")
    print(json.dumps({"report": str(output / "report.json"), "status": report["status"], "presentmon": report["presentmon"]["status"]}, indent=2))


if __name__ == "__main__":
    main()
