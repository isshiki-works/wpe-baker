"""Record local dependency changes against verified pinned source archives."""
from __future__ import annotations

import difflib
import hashlib
import json
import pathlib
import zipfile

ROOT = pathlib.Path(__file__).resolve().parents[1]
NAMES = ["rstd", "vvk", "wavsen", "spirv-reflect", "glslang", "quickjs", "vma", "lz4", "freetype", "vulkan-headers", "vulkan-loader", "eigen"]

def main() -> None:
    output = ROOT / "scripts/dependency-patches"
    output.mkdir(exist_ok=True)
    records = []
    for name in NAMES:
        archive_path = ROOT / ".deps/downloads" / (name + ".zip")
        directory = ROOT / ".deps" / name
        if not archive_path.exists() or not directory.exists(): continue
        with zipfile.ZipFile(archive_path) as archive:
            original = {}
            for info in archive.infolist():
                if info.is_dir(): continue
                parts = pathlib.PurePosixPath(info.filename).parts
                original["/".join(parts[1:])] = archive.read(info)
        current = {path.relative_to(directory).as_posix(): path.read_bytes() for path in directory.rglob("*") if path.is_file() and ".git" not in path.parts}
        patch = []
        changed = []
        for relative in sorted(set(original) | set(current)):
            before, after = original.get(relative, b""), current.get(relative, b"")
            if before == after: continue
            try:
                old_text, new_text = before.decode("utf-8"), after.decode("utf-8")
            except UnicodeDecodeError as error:
                raise RuntimeError("Changed non-text dependency input: " + name + "/" + relative) from error
            old_name = "a/" + relative if relative in original else "/dev/null"
            new_name = "b/" + relative if relative in current else "/dev/null"
            for line in difflib.unified_diff(old_text.splitlines(keepends=True), new_text.splitlines(keepends=True), fromfile=old_name, tofile=new_name):
                patch.append(line if line.endswith("\n") else line + "\n\\ No newline at end of file\n")
            changed.append({"path": relative, "before_sha256": hashlib.sha256(before).hexdigest() if relative in original else None, "after_sha256": hashlib.sha256(after).hexdigest() if relative in current else None})
        if not changed: continue
        path = output / (name + ".patch")
        contents = "".join(patch)
        if not path.exists() or path.read_text(encoding="utf-8") != contents:
            path.write_text(contents, encoding="utf-8", newline="")
        records.append({"dependency": name, "patch": path.relative_to(ROOT).as_posix(), "sha256": hashlib.sha256(path.read_bytes()).hexdigest(), "files": changed})
        print(name + ": " + str(len(changed)) + " modified/added files")
    (output / "manifest.json").write_text(json.dumps({"schema": "wpe-native-dependency-patches-v1", "dependencies": records}, indent=2) + "\n", encoding="utf-8")

if __name__ == "__main__":
    main()
