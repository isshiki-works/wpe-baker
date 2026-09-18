"""One synthetic fixture exercising sampled composition validation through the CLI."""
from __future__ import annotations

import json
import os
import pathlib
import subprocess
import time

ROOT = pathlib.Path(__file__).resolve().parents[1]


def main():
    output = ROOT / "artifacts" / ("pair-api-test-" + str(time.time_ns()))
    output.mkdir()
    source = ROOT / "tests/fixtures/native/orientation"
    environment = dict(os.environ)
    environment["DOTNET_ROOT"] = str(ROOT / ".dotnet")
    environment["DOTNET_CLI_UI_LANGUAGE"] = "en-US"
    request = {"schema_version": 1, "source": str(source), "candidate": str(source), "assets": str(source),
               "width": 256, "height": 128, "fps_numerator": 120, "fps_denominator": 1,
               "frames": 2, "warmup_frames": 0, "seed": 123456}
    request["output_directory"] = str(output / "validate")
    path = output / "validate.request.json"
    path.write_text(json.dumps(request, indent=2), encoding="utf-8")
    command = [str(ROOT / ".dotnet/dotnet.exe"), str(ROOT / "src/Baker.Cli/bin/Release/net10.0/wpe-baker.dll"),
               "validate", str(path)]
    result = subprocess.run(command, cwd=ROOT, env=environment, capture_output=True,
                            creationflags=subprocess.CREATE_NO_WINDOW, timeout=120)
    (output / "validate.stdout.json").write_bytes(result.stdout)
    (output / "validate.stderr.log").write_bytes(result.stderr)
    if result.returncode:
        raise RuntimeError(f"validate exited {result.returncode}; see {output}")
    comparison = json.loads(result.stdout)
    assert comparison["status"] == "compared" and comparison["byte_identical_samples"]
    assert comparison["metrics"]["rgb_max_abs_255"] == comparison["metrics"]["alpha_max_abs_255"] == 0
    assert comparison["tiles"] and comparison["object_preservation"]["original_order_preserved"]
    assert comparison["automatic_visual_certification"] is False
    report = {"status": "passed", "comparison": comparison["report_path"]}
    (output / "report.json").write_text(json.dumps(report, indent=2), encoding="utf-8")
    print(json.dumps({"report": str(output / "report.json"), **report}, indent=2))


if __name__ == "__main__":
    main()
