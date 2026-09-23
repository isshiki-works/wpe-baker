"""Fetch the remaining native build inputs into project-local directories."""
from __future__ import annotations

import argparse
import concurrent.futures
import hashlib
import importlib.util
import json
import pathlib
import shutil

ROOT = pathlib.Path(__file__).resolve().parents[1]
spec = importlib.util.spec_from_file_location("bootstrap_native", ROOT / "scripts" / "bootstrap-native.py")
bootstrap = importlib.util.module_from_spec(spec)
spec.loader.exec_module(bootstrap)

INPUTS = [
    ("eigen", "https://gitlab.com/libeigen/eigen/-/archive/bc3b39870ecb690a623a3f49149a358b95c5781d/eigen-bc3b39870ecb690a623a3f49149a358b95c5781d.zip", None, bootstrap.DEPS),
    ("vulkan-loader", "https://codeload.github.com/KhronosGroup/Vulkan-Loader/zip/v1.4.321", None, bootstrap.DEPS),
    ("nlohmann-json", "https://github.com/nlohmann/json/releases/download/v3.12.0/include.zip", None, bootstrap.DEPS),
    ("cli11", "https://github.com/CLIUtils/CLI11/releases/download/v2.7.2/CLI11.hpp", "ffa9a30da295c5858fb5f91f9f45771bab09471d7010a34c7c68c857a330dd76", bootstrap.DEPS),
    ("googletest", "https://github.com/google/googletest/archive/refs/tags/v1.18.0.tar.gz", "6e3191c1455468b3fc35a417fb565c1c5071aee1b7e7f85e30cf48a98d37d8b5", bootstrap.DEPS),
]

def seed_from_cache(cache: pathlib.Path, inputs: list) -> None:
    """本地缓存里有 SHA256 与锁一致的包时，先放进 .deps/downloads，obtain 就不再联网。"""
    for name, url, _, base in inputs:
        expected = bootstrap.LOCKED.get(url)
        target = base / "downloads" / (name + bootstrap.archive_suffix(url))
        if expected is None or target.exists():
            continue
        for candidate in cache.iterdir():
            if candidate.is_file() and hashlib.file_digest(candidate.open("rb"), "sha256").hexdigest() == expected:
                target.parent.mkdir(parents=True, exist_ok=True)
                shutil.copyfile(candidate, target)
                print("Seeded", name, "from", candidate, flush=True)
                break

def main() -> None:
    parser = argparse.ArgumentParser()
    parser.add_argument("--cache", type=pathlib.Path, help="已下载好的包所在目录（按 SHA256 匹配锁条目）")
    parser.add_argument("names", nargs="*", help="只取这些包（默认全部）")
    options = parser.parse_args()
    selected = [entry for entry in INPUTS if not options.names or entry[0] in options.names]
    if options.cache:
        seed_from_cache(options.cache, selected)
    records, errors = [], []
    with concurrent.futures.ThreadPoolExecutor(max_workers=2) as executor:
        futures = [executor.submit(bootstrap.obtain, *entry) for entry in selected]
        for future in concurrent.futures.as_completed(futures):
            try:
                records.append(future.result())
            except Exception as error:
                print("ERROR", error, flush=True)
                errors.append(str(error))
    lock = ROOT / "scripts" / "native-inputs.lock.json"
    entries = json.loads(lock.read_text(encoding="utf-8"))
    by_url = {entry["url"]: entry for entry in entries}
    for entry in records:
        by_url[entry["url"]] = {**by_url.get(entry["url"], {}), **entry}
    lock.write_bytes((json.dumps(list(by_url.values()), indent=2) + "\n").encode("utf-8"))
    if errors:
        raise SystemExit("; ".join(errors))

if __name__ == "__main__":
    main()
