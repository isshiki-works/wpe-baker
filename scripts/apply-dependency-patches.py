"""Apply recorded platform fixes only to matching pinned dependency inputs."""
from __future__ import annotations

import hashlib
import json
import pathlib
import subprocess

ROOT = pathlib.Path(__file__).resolve().parents[1]

def sha(path: pathlib.Path) -> str | None:
    if not path.exists(): return None
    with path.open("rb") as stream:
        return hashlib.file_digest(stream, "sha256").hexdigest()

def main() -> None:
    manifest = ROOT / "scripts/dependency-patches/manifest.json"
    for dependency in json.loads(manifest.read_text(encoding="utf-8"))["dependencies"]:
        directory = (ROOT / ".deps" / dependency["dependency"]).resolve()
        if not directory.is_relative_to((ROOT / ".deps").resolve()):
            raise RuntimeError("Dependency path is outside .deps")
        states = []
        for record in dependency["files"]:
            path = (directory / record["path"]).resolve()
            if not path.is_relative_to(directory): raise RuntimeError("Patch file escaped dependency directory")
            value = sha(path)
            if value == record["after_sha256"]: states.append("after")
            elif value == record["before_sha256"]: states.append("before")
            else: raise RuntimeError("Unrecognized local edits; preserving " + str(path))
        if all(state == "after" for state in states):
            print(dependency["dependency"] + ": recorded patch already applied")
            continue
        if any(state == "after" for state in states):
            raise RuntimeError("Partial patch state; preserving " + str(directory))
        patch = ROOT / dependency["patch"]
        if sha(patch) != dependency["sha256"]: raise RuntimeError("Patch digest mismatch: " + str(patch))
        # 干净 clone 若继承系统级 core.autocrlf=true，git apply 会写出 CRLF，应用后摘要必然对不上。
        command = ["git", "-c", "core.autocrlf=false", "apply", "--directory=" + directory.relative_to(ROOT).as_posix()]
        for arguments in [["--check", str(patch)], [str(patch)]]:
            result = subprocess.run(command + arguments, cwd=ROOT, capture_output=True, text=True, encoding="utf-8", errors="replace", creationflags=subprocess.CREATE_NO_WINDOW)
            if result.returncode: raise RuntimeError(result.stdout + result.stderr)
        if any(sha(directory / record["path"]) != record["after_sha256"] for record in dependency["files"]):
            raise RuntimeError("Post-application digest mismatch: " + str(directory))
        print(dependency["dependency"] + ": patch applied and verified")

if __name__ == "__main__":
    main()
