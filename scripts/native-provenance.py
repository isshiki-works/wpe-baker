"""Bind native renderer artifacts to exact local source and dependency bytes."""
from __future__ import annotations

import datetime
import hashlib
import os
import json
import pathlib
import subprocess

ROOT = pathlib.Path(__file__).resolve().parents[1]
ENGINE_BASE = "b866e8e711fdd7762385b23601affa1ea5539e3b"
SOURCE_DEPENDENCIES = ["rstd", "vvk", "spirv-reflect", "glslang", "quickjs", "vma", "lz4", "freetype", "vulkan-headers", "vulkan-loader", "eigen", "nlohmann-json"]

def digest_file(path: pathlib.Path) -> str:
    with path.open("rb") as stream:
        return hashlib.file_digest(stream, "sha256").hexdigest()

def load_verified_build(build_directory: pathlib.Path) -> tuple[pathlib.Path, pathlib.Path, dict]:
    """Load one current, source-bound wpe-render build for distribution."""
    build = build_directory.resolve()
    try:
        build.relative_to(ROOT)
    except ValueError as error:
        raise ValueError("--native-build-dir must be inside the repository.") from error
    renderer = build / "bin" / "wpe-render.exe"
    build_record_path = build / "provenance" / "build-wpe-render.json"
    source_record_path = build / "provenance" / "source-wpe-render.json"
    for path in (renderer, build_record_path, source_record_path):
        if not path.is_file():
            raise FileNotFoundError(f"Selected native build is incomplete: {path}")
    build_record = json.loads(build_record_path.read_text(encoding="utf-8"))
    source_record = json.loads(source_record_path.read_text(encoding="utf-8"))
    if build_record.get("schema") != "wpe-native-build-v1" or build_record.get("target") != "wpe-render":
        raise RuntimeError("Selected build provenance is not a wpe-render build record.")
    if source_record.get("schema") != "wpe-native-source-v1":
        raise RuntimeError("Selected source provenance is not a native source record.")
    if build_record.get("status") != "verified-source-binding":
        raise RuntimeError("Selected renderer provenance is not verified-source-binding.")
    binary = build_record.get("binary")
    if not isinstance(binary, dict) or binary.get("path") != renderer.relative_to(ROOT).as_posix() or binary.get("sha256") != digest_file(renderer):
        raise RuntimeError("Selected renderer does not match its build provenance.")
    if (build_record.get("source_manifest") != source_record_path.name or
            build_record.get("compiled_source_sha256") != source_record.get("source_sha256") or
            build_record.get("post_build_source_sha256") != source_record.get("source_sha256")):
        raise RuntimeError("Selected native build and source provenance disagree.")
    files = source_record.get("files")
    if not isinstance(files, list):
        raise RuntimeError("Selected source provenance has no file snapshot.")
    for item in files:
        if not isinstance(item, dict) or not isinstance(item.get("path"), str):
            raise RuntimeError("Selected source provenance has an invalid file entry.")
        path = ROOT / item["path"]
        if not path.is_file() or digest_file(path) != item.get("sha256"):
            raise RuntimeError(f"Native source no longer matches selected build: {item['path']}")
    return build, renderer, build_record

def snapshot(ffmpeg_root: pathlib.Path | None = None) -> dict:
    ffmpeg_root = pathlib.Path(os.path.abspath(ffmpeg_root or ROOT / ".deps/ffmpeg-lgpl21/prefix"))
    roots = [ROOT / "engine/src", ROOT / "engine/tools/SceneBake", ROOT / "engine/tests/offline-vulkan"]
    roots += [ROOT / ".deps" / name for name in SOURCE_DEPENDENCIES]
    roots += [ffmpeg_root / "include", ffmpeg_root / "lib"]
    paths = {ROOT / "engine/CMakeLists.txt", ROOT / "engine/CMakePresets.json", ROOT / "engine/LICENSE"}
    paths.update(ROOT / "scripts" / name for name in ["build-native-cmake.py", "native-provenance.py", "native-inputs.lock.json"])
    paths.update((ffmpeg_root / "bin").glob("*.dll"))
    if ffmpeg_root == pathlib.Path(os.path.abspath(ROOT / ".deps/ffmpeg-lgpl21/prefix")):
        paths.update(ROOT / "scripts" / name for name in ["distribution-inputs.lock.json", "fetch-ffmpeg-lgpl21-inputs.py", "build-ffmpeg-lgpl21.py"])
    vk_import = ROOT / ".deps/install/lib/vulkan-1.dll.a"
    if vk_import.exists(): paths.add(vk_import)
    for source_root in roots:
        for path in source_root.rglob("*"):
            if path.is_file() and not any(part in (".git", "__pycache__", ".DS_Store") for part in path.parts):
                paths.add(path)
    files = []
    combined = hashlib.sha256()
    for path in sorted(paths, key=lambda item: item.relative_to(ROOT).as_posix()):
        name = path.relative_to(ROOT).as_posix()
        digest = digest_file(path)
        combined.update(name.encode("utf-8") + b"\0" + bytes.fromhex(digest))
        files.append({"path": name, "sha256": digest, "bytes": path.stat().st_size})
    return {"schema": "wpe-native-source-v1", "engine_base": ENGINE_BASE, "ffmpeg_root": ffmpeg_root.relative_to(ROOT).as_posix(), "source_sha256": combined.hexdigest(), "files": files}

def write(path: pathlib.Path, value: dict) -> None:
    path.parent.mkdir(parents=True, exist_ok=True)
    path.write_text(json.dumps(value, indent=2) + "\n", encoding="utf-8")

def finalize(before: dict, target: str, binary: pathlib.Path, environment: dict[str, str], build_directory: pathlib.Path, toolchain: pathlib.Path) -> dict:
    ffmpeg_root = ROOT / before["ffmpeg_root"]
    after = snapshot(ffmpeg_root)
    stable = before["source_sha256"] == after["source_sha256"]
    tools = {}
    for name, relative in [("clang", (toolchain / "bin/clang++.exe").relative_to(ROOT).as_posix()), ("cmake", ".tools/cmake/bin/cmake.exe"), ("ninja", ".tools/ninja/ninja.exe")]:
        executable = ROOT / relative
        result = subprocess.run([str(executable), "--version"], env=environment, capture_output=True, text=True, encoding="utf-8", errors="replace", timeout=30, creationflags=subprocess.CREATE_NO_WINDOW)
        tools[name] = {"path": relative, "sha256": digest_file(executable), "version": result.stdout.strip(), "returncode": result.returncode}
    result = {
        "schema": "wpe-native-build-v1",
        "created_utc": datetime.datetime.now(datetime.timezone.utc).isoformat(),
        "status": "verified-source-binding" if stable else "sources-changed-during-build",
        "engine_base": ENGINE_BASE,
        "target": target,
        "compiled_source_sha256": before["source_sha256"],
        "post_build_source_sha256": after["source_sha256"],
        "binary": {"path": binary.relative_to(ROOT).as_posix(), "sha256": digest_file(binary), "bytes": binary.stat().st_size},
        "native_inputs_lock_sha256": digest_file(ROOT / "scripts/native-inputs.lock.json"),
        "tools": tools,
        "cmake_cache_sha256": digest_file(build_directory / "CMakeCache.txt"),
        "compile_commands_sha256": digest_file(build_directory / "compile_commands.json"),
        "source_manifest": "source-" + target + ".json",
        "ffmpeg_root": before["ffmpeg_root"],
        "runtime_ffmpeg_dlls": [{"name": path.name, "sha256": digest_file(path)} for path in sorted((ffmpeg_root / "bin").glob("*.dll"))],
    }
    write(build_directory / "provenance" / ("source-" + target + ".json"), before)
    write(build_directory / "provenance" / ("build-" + target + ".json"), result)
    return result
