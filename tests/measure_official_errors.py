"""Failed collectors must not masquerade as idle playback or valid stale CSVs."""
from pathlib import Path
import runpy

module = runpy.run_path(str(Path(__file__).resolve().parents[1] / "scripts/measure-official-wpe.py"))
summarize = module["presentmon_result"]
missing = Path(__file__).with_name("nonexistent-presentmon.csv")
denied = summarize(missing, 60, 6, b"error: failed to start trace session: access denied.\n")
assert denied["status"] == "permission_denied" and denied["exit_code"] == 6
assert "access denied" in denied["stderr"]
failed = summarize(missing, 60, 1, b"unexpected collector failure")
assert failed["status"] == "failed" and failed["stderr"] == "unexpected collector failure"
assert summarize(missing, 60, 0, b"")["status"] == "not_measured"
print("passed: permission failure, other collector failure, and successful empty capture stay distinct")
