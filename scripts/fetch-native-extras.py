"""Fetch the remaining native build inputs into project-local directories."""
from __future__ import annotations

import concurrent.futures
import importlib.util
import json
import pathlib

ROOT = pathlib.Path(__file__).resolve().parents[1]
spec = importlib.util.spec_from_file_location("bootstrap_native", ROOT / "scripts" / "bootstrap-native.py")
bootstrap = importlib.util.module_from_spec(spec)
spec.loader.exec_module(bootstrap)

INPUTS = [
    ("eigen", "https://gitlab.com/libeigen/eigen/-/archive/bc3b39870ecb690a623a3f49149a358b95c5781d/eigen-bc3b39870ecb690a623a3f49149a358b95c5781d.zip", None, bootstrap.DEPS),
    ("vulkan-loader", "https://codeload.github.com/KhronosGroup/Vulkan-Loader/zip/v1.4.321", None, bootstrap.DEPS),
]

def main() -> None:
    records, errors = [], []
    with concurrent.futures.ThreadPoolExecutor(max_workers=2) as executor:
        futures = [executor.submit(bootstrap.obtain, *entry) for entry in INPUTS]
        for future in concurrent.futures.as_completed(futures):
            try:
                records.append(future.result())
            except Exception as error:
                print("ERROR", error, flush=True)
                errors.append(str(error))
    lock = ROOT / "scripts" / "native-inputs.lock.json"
    entries = json.loads(lock.read_text(encoding="utf-8"))
    by_url = {entry["url"]: entry for entry in entries}
    by_url.update({entry["url"]: entry for entry in records})
    lock.write_text(json.dumps(list(by_url.values()), indent=2) + "\n", encoding="utf-8")
    if errors:
        raise SystemExit("; ".join(errors))

if __name__ == "__main__":
    main()
