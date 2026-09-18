"""Focused native regression for user properties bound to a Vec2 parallax depth."""
from __future__ import annotations

import argparse
import json
import math
import os
from pathlib import Path
import shutil
import subprocess

ROOT = Path(__file__).resolve().parents[1]


def main() -> None:
    parser = argparse.ArgumentParser()
    parser.add_argument("--output", type=Path, required=True)
    parser.add_argument("--renderer", type=Path, default=ROOT / "build/native-release22/bin/wpe-render.exe")
    parser.add_argument("--device-uuid")
    args = parser.parse_args()
    output = args.output.resolve()
    output.mkdir(parents=True, exist_ok=False)
    fixture = output / "source"
    shutil.copytree(ROOT / "tests/fixtures/native/orientation", fixture)
    scene = json.loads((fixture / "scene.json").read_text(encoding="utf-8"))
    scene["objects"][0]["parallaxDepth"] = {"user": "depth", "value": "1 1"}
    (fixture / "scene.json").write_text(json.dumps(scene), encoding="utf-8")
    environment = os.environ.copy()
    environment["PATH"] = os.pathsep.join(str(ROOT / path) for path in (
        ".tools/llvm-mingw-22/bin", ".deps/ffmpeg-lgpl21/prefix/bin",
    )) + os.pathsep + environment.get("PATH", "")
    cases = [
        ("pair-string", "0.25 0.75", [0.25, 0.75]),
        ("color-string", "0.25 0.75 0.5", [0.25, 0.75]),
        ("wrapped-color", {"value": "0.75 0.25 1"}, [0.75, 0.25]),
        ("numeric-scalar", 0.375, [0.375, 0.375]),
        ("string-scalar", "0.625", [0.625, 0.625]),
        ("boolean-scalar", True, [1.0, 1.0]),
        ("pair-array", [0.125, 0.875], [0.125, 0.875]),
        ("malformed-second", "0.25 invalid", None),
        ("nonfinite", "1e100 0.75", None),
        ("unsupported-array", [0.25, 0.75, 1], None),
    ]
    report = {"status": "running", "cases": []}
    try:
        for name, value, expected in cases:
            work = output / name
            work.mkdir()
            job = {
                "schema_version": 1, "source": str(fixture / "scene.json"),
                "assets": str(fixture), "output_dir": str(work / "native"),
                "width": 64, "height": 32, "fps_num": 60, "fps_den": 1,
                "frames": 1, "raw_stdout": False, "trace_scene": True,
                "user_properties": {"depth": value},
            }
            if args.device_uuid:
                job["device_uuid"] = args.device_uuid
            job_path = work / "request.json"
            job_path.write_text(json.dumps(job), encoding="utf-8")
            with (work / "renderer.log").open("wb") as log:
                result = subprocess.run(
                    [str(args.renderer.resolve()), "render", "--job", str(job_path)],
                    cwd=ROOT, env=environment, stdout=log, stderr=subprocess.STDOUT,
                    timeout=120, creationflags=subprocess.CREATE_NO_WINDOW if os.name == "nt" else 0,
                )
            native = json.loads((work / "native/result.json").read_text(encoding="utf-8"))
            row = {"name": name, "exit_code": result.returncode, "status": native["status"]}
            if expected is None:
                if result.returncode == 0 or native["renderer_error_count"] == 0:
                    raise RuntimeError(f"{name}: invalid value was silently accepted")
            else:
                if result.returncode != 0 or native["status"] != "complete" or native["renderer_error_count"] != 0:
                    raise RuntimeError(f"{name}: valid input failed; see {work / 'renderer.log'}")
                layer = next(item for item in native["runtime_layers"] if item["id"] == 1)
                actual = layer["effective_parallax_depth"]
                row["actual_depth"] = actual
                if actual is None or len(actual) != 2 or not all(math.isclose(a, e, abs_tol=1e-6) for a, e in zip(actual, expected)):
                    raise RuntimeError(f"{name}: expected {expected}, got {actual}")
            report["cases"].append(row)
        report["status"] = "passed"
    except Exception as error:
        report["status"] = "failed"
        report["error"] = str(error)
        raise
    finally:
        (output / "report.json").write_text(json.dumps(report, indent=2) + "\n", encoding="utf-8")
        print(json.dumps({"status": report["status"], "report": str(output / "report.json")}))


if __name__ == "__main__":
    main()
