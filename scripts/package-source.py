"""Package corresponding project and dependency sources for a local portable build."""
from __future__ import annotations
import argparse
import hashlib
import importlib.util
import json
from pathlib import Path
import re
import subprocess
import zipfile

ROOT = Path(__file__).resolve().parents[1]
provenance_spec = importlib.util.spec_from_file_location("native_provenance", ROOT / "scripts/native-provenance.py")
if provenance_spec is None or provenance_spec.loader is None: raise RuntimeError("Could not load native provenance helper.")
provenance = importlib.util.module_from_spec(provenance_spec)
provenance_spec.loader.exec_module(provenance)
DEPENDENCIES = ("rstd", "vvk", "wavsen", "lz4", "freetype", "quickjs", "glslang", "vma",
                "spirv-reflect", "eigen", "vulkan-headers", "vulkan-loader")
# GPL v2 section 3: the renderer binary must ship with the sources it was built from,
# including the multithreaded-decode patches applied on top of the pinned engine.
REQUIRED_FILES = ("README.md", "README.zh-CN.md", "LICENSE", "THIRD-PARTY-NOTICES.md", "SOURCE.md",
                  "patches/renderer-mt/README.md", "patches/renderer-mt/sha256.txt",
                  "patches/renderer-mt/engine-perf-video-decode-threads.patch",
                  "patches/renderer-mt/parent-perf-video-decode-threads.patch",
                  "scripts/dependency-patches/manifest.json", "scripts/dependency-patches/rstd.patch",
                  "scripts/dependency-patches/vvk.patch", "scripts/dependency-patches/wavsen.patch")
REQUIRED_TREES = tuple(f".deps/{name}" for name in DEPENDENCIES) + (
    "engine", ".deps/ffmpeg-lgpl21/sources/ffmpeg", ".deps/ffmpeg-lgpl21/sources/dav1d",
    ".deps/ffmpeg-encoder-gpl2/sources/ffmpeg", ".deps/ffmpeg-encoder-gpl2/sources/x264",
    ".deps/ffmpeg-encoder-gpl2/sources/x265", "licenses-extra")
# Workshop wallpapers and baked masters must never enter a public archive.
PRIVATE_PATTERN = re.compile(r"(^|/)(\d{9,10})(/|$)|\.pkg$|\.tex\.bak$")
# Everything the archive picks up from the working tree, for the clean-tree check below.
PACKAGED_PREFIXES = ("src/", "tests/", "scripts/", "patches/", "engine/", "licenses-extra/")
PACKAGED_ROOT_FILES = frozenset({"README.md", "README.zh-CN.md", "LICENSE", ".gitignore",
                                 ".gitattributes", "THIRD-PARTY-NOTICES.md", "SOURCE.md"})


def check_inputs() -> None:
    missing = [name for name in REQUIRED_FILES if not (ROOT / name).is_file()]
    empty = [name for name in REQUIRED_TREES
             if not (ROOT / name).is_dir() or not any((ROOT / name).rglob("*"))]
    if missing or empty:
        raise FileNotFoundError("Source archive inputs are incomplete; "
                                f"missing files: {missing}; missing or empty trees: {empty}")
    # src/, tests/, scripts/ and patches/ are packaged as whole trees, so any stray or
    # modified working file under them would silently enter a published archive. Only those
    # paths are checked: the build machine keeps unrelated untracked directories at the
    # repository root, and "git clean" is exactly what must not be run there (it would rewrite
    # the line endings of the files the renderer's source binding is pinned to).
    status = subprocess.run(["git", "-C", str(ROOT), "status", "--porcelain"], capture_output=True,
                            text=True, encoding="utf-8", errors="replace",
                            creationflags=subprocess.CREATE_NO_WINDOW)
    if status.returncode:
        raise RuntimeError("Could not read Git status of the working tree: " + status.stderr.strip())
    dirty = []
    for line in status.stdout.splitlines():
        if not line.strip():
            continue
        path = line[3:].split(" -> ")[-1].strip().strip('"')
        if path.startswith(PACKAGED_PREFIXES) or path in PACKAGED_ROOT_FILES or path == "engine":
            dirty.append(line.rstrip())
    if dirty:
        raise RuntimeError("Working tree is not clean inside the packaged paths; commit or remove "
                           "these before packaging:\n" + "\n".join(dirty))


def main() -> None:
    parser = argparse.ArgumentParser()
    parser.add_argument("--output", type=Path, required=True, help="New source ZIP, beside the portable ZIP")
    parser.add_argument("--native-build-dir", type=Path, default=ROOT / "build/native-release22",
                        help="Verified native build directory represented by this source ZIP (default: build/native-release22)")
    args = parser.parse_args()
    check_inputs()
    native_build, _native_renderer, native_record = provenance.load_verified_build(args.native_build_dir)
    output = args.output.resolve()
    if output.exists(): raise FileExistsError(output)
    output.parent.mkdir(parents=True, exist_ok=True)
    bundle = output.with_suffix(".engine.bundle")
    subprocess.run(["git", "-C", str(ROOT / "engine"), "bundle", "create", str(bundle), "HEAD"], check=True,
                   capture_output=True, creationflags=subprocess.CREATE_NO_WINDOW)
    candidates: dict[str, Path] = {}
    skipped_names = {".git", "__pycache__", "bin", "obj", ".cache"}
    def tree(directory: Path, prefix: str) -> None:
        for path in directory.rglob("*"):
            relative = path.relative_to(directory)
            if prefix == "engine" and relative.parts[0] in {"build", ".lito"}:
                continue
            if path.is_file() and not any(part in skipped_names for part in relative.parts):
                candidates[prefix + "/" + relative.as_posix()] = path
    for name in ("src", "tests", "scripts", "patches", "engine", "licenses-extra"):
        tree(ROOT / name, name)
    for name in ("README.md", "README.zh-CN.md", "LICENSE", ".gitignore",
                 "THIRD-PARTY-NOTICES.md", "SOURCE.md"):
        candidates[name] = ROOT / name
    candidates["engine-upstream.bundle"] = bundle
    for name in DEPENDENCIES:
        tree(ROOT / ".deps" / name, ".deps/" + name)
    for flavor in ("ffmpeg-lgpl21", "ffmpeg-encoder-gpl2"):
        tree(ROOT / ".deps" / flavor / "sources", ".deps/" + flavor + "/sources")
        for name in ("build-configuration.json", "verification.json"):
            candidates[f".deps/{flavor}/{name}"] = ROOT / ".deps" / flavor / name
    # The encoder build guide only existed inside the source-inclusive encoder ZIP.
    encoder_guide = ROOT / ".deps/ffmpeg-encoder-gpl2/ENCODER-BUILD.md"
    if encoder_guide.is_file():
        candidates[".deps/ffmpeg-encoder-gpl2/ENCODER-BUILD.md"] = encoder_guide
    else:
        raise FileNotFoundError(f"Encoder build guide is missing: {encoder_guide}")
    for path in (native_build / "provenance").glob("*wpe-render.json"):
        candidates["build-records/" + path.name] = path
    # The official x264 Git bundle is needed by its version script for offline builds.
    encoder_inputs = json.loads((ROOT / "scripts/encoder-inputs.lock.json").read_text(encoding="utf-8"))
    for component in ("ffmpeg", "x264", "x265"):
        relative = encoder_inputs[component]["archive"]
        candidates[relative] = ROOT / relative
    for key in ("bundle", "patch"):
        relative = encoder_inputs["x264"][key]
        candidates[relative] = ROOT / relative
    private = sorted(name for name in candidates if PRIVATE_PATTERN.search(name))
    if private:
        raise RuntimeError(f"Refusing to package private wallpaper material: {private[:10]}")
    unreadable = sorted(name for name, path in candidates.items() if not path.is_file())
    if unreadable:
        raise FileNotFoundError(f"Source archive inputs are missing on disk: {unreadable[:10]}")
    instructions = """# Rebuild this source snapshot

This archive contains the modified engine, patched dependencies, decoder and
encoder sources, C# tool layer, tests, input locks and build recipes. Workshop
projects and generated user wallpapers are excluded.

The engine source files already contain the modifications used by the build.
If the build provenance helper requires the upstream Git identity, initialize
its index without replacing those working files:

    git -C engine init
    git -C engine fetch ../engine-upstream.bundle HEAD
    git -C engine reset --mixed FETCH_HEAD

Recreate x264's Git identity from the included bundle without replacing files:

    git -C .deps/ffmpeg-encoder-gpl2/sources/x264 init
    git -C .deps/ffmpeg-encoder-gpl2/sources/x264 fetch ../../../../.tools/downloads/x264-b35605ace3ddf7c1a5d67a2eb553f034aef41d55.bundle refs/heads/source-pin:refs/heads/source-pin refs/heads/master-pin:refs/remotes/origin/master
    git -C .deps/ffmpeg-encoder-gpl2/sources/x264 reset --mixed source-pin

The distributed renderer includes the current rendering and encoding changes.
The authoritative source fingerprint and packaged binary SHA256 are in
build-records/build-wpe-render.json. The files in patches/renderer-mt/ identify
the historical decode-threading snapshot; do not reapply them or compare this
later renderer to that snapshot's binary hash.

Use the project-local tool versions in scripts/native-inputs.lock.json and
scripts/encoder-build-tools.lock.json. See README.md, SOURCE.md and the two
build guides under scripts/. The matching runtime's build-records name its exact
source digest. The checked Windows LLVM-MinGW compiler is the llvm-mingw-22 entry.

Third-party components, versions and licenses are listed in
THIRD-PARTY-NOTICES.md; license texts are under licenses-extra/ and inside each
dependency's own source tree.

The source ZIP contains sources, not a preinstalled development toolchain.
Downloading the pinned tools requires network access once; thereafter native
builds use the local sources and fully disconnected dependency resolution.
The finished portable application itself does not require Python or network.
"""
    print(f"Packaging {len(candidates)} source files", flush=True)
    record = {"schema_version": 1, "native_build_dir": native_build.relative_to(ROOT).as_posix(),
              "native_renderer_sha256": native_record["binary"]["sha256"], "files": {}}
    with zipfile.ZipFile(output, "x", zipfile.ZIP_DEFLATED, compresslevel=6) as zipped:
        for relative, path in sorted(candidates.items()):
            with path.open("rb") as stream:
                record["files"][relative] = hashlib.file_digest(stream, "sha256").hexdigest()
            zipped.write(path, "WpeBaker-source/" + relative)
        zipped.writestr("WpeBaker-source/REBUILD.md", instructions)
        zipped.writestr("WpeBaker-source/source-bundle.json", json.dumps(record, indent=2) + "\n")
    with zipfile.ZipFile(output) as zipped:
        bad = zipped.testzip()
        if bad: raise RuntimeError(f"Source ZIP CRC failed: {bad}")
    with output.open("rb") as stream:
        sha = hashlib.file_digest(stream, "sha256").hexdigest()
    print(json.dumps({"archive": str(output), "bytes": output.stat().st_size, "sha256": sha, "source_files": len(candidates)}, indent=2), flush=True)


if __name__ == "__main__":
    main()
