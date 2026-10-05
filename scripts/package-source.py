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
DEPENDENCIES = ("rstd", "vvk", "lz4", "freetype", "quickjs", "glslang", "vma",
                "spirv-reflect", "eigen", "vulkan-headers", "vulkan-loader", "nlohmann-json", "cli11", "googletest")
# GPL v2 section 3: the renderer binary must ship with the sources it was built from.
REQUIRED_FILES = ("README.md", "README.zh-CN.md", "LICENSE", "THIRD-PARTY-NOTICES.md", "SOURCE.md",
                  "scripts/dependency-patches/manifest.json", "scripts/dependency-patches/rstd.patch",
                  "scripts/dependency-patches/vvk.patch")
REQUIRED_TREES = tuple(f".deps/{name}" for name in DEPENDENCIES) + (
    "engine", ".deps/ffmpeg-lgpl21/sources/ffmpeg", ".deps/ffmpeg-lgpl21/sources/dav1d",
    ".deps/ffmpeg-encoder-gpl2/sources/ffmpeg", ".deps/ffmpeg-encoder-gpl2/sources/x264",
    ".deps/ffmpeg-encoder-gpl2/sources/x265",
    ".deps/ffmpeg-encoder-gpl2/sources/nv-codec-headers",
    ".deps/ffmpeg-encoder-gpl2/sources/amf-headers", "licenses-extra")
# Workshop wallpapers and baked masters must never enter a public archive.
PRIVATE_PATTERN = re.compile(r"(^|/)(\d{9,10})(/|$)|\.pkg$|\.tex\.bak$")
# Everything the archive picks up from the working tree, for the clean-tree check below.
PACKAGED_PREFIXES = ("src/", "bench/", "tests/", "scripts/", "engine/", "licenses-extra/")
PACKAGED_ROOT_FILES = frozenset({"README.md", "README.zh-CN.md", "LICENSE", ".gitignore",
                                 ".gitattributes", "THIRD-PARTY-NOTICES.md", "SOURCE.md"})


def check_inputs() -> None:
    missing = [name for name in REQUIRED_FILES if not (ROOT / name).is_file()]
    empty = [name for name in REQUIRED_TREES
             if not (ROOT / name).is_dir() or not any((ROOT / name).rglob("*"))]
    if missing or empty:
        raise FileNotFoundError("Source archive inputs are incomplete; "
                                f"missing files: {missing}; missing or empty trees: {empty}")
    # src/, tests/ and scripts/ are packaged as whole trees, so any stray or
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
    candidates: dict[str, Path] = {}
    skipped_names = {".git", "__pycache__", "bin", "obj", ".cache"}
    def tree(directory: Path, prefix: str) -> None:
        for path in directory.rglob("*"):
            relative = path.relative_to(directory)
            if prefix == "engine" and relative.parts[0] in {"build"}:
                continue
            if path.is_file() and not any(part in skipped_names for part in relative.parts):
                candidates[prefix + "/" + relative.as_posix()] = path
    for name in ("src", "bench", "tests", "scripts", "engine", "licenses-extra"):
        tree(ROOT / name, name)
    for name in ("README.md", "README.zh-CN.md", "LICENSE", ".gitignore",
                 "THIRD-PARTY-NOTICES.md", "SOURCE.md"):
        candidates[name] = ROOT / name
    # Repackaging the source snapshot requires the same pinned collector as the portable bundle.
    presentmon = ROOT / ".tools/presentmon/PresentMon-2.5.1-x64.exe"
    expected = json.loads((ROOT / "bench/presentmon/source.json").read_text(encoding="utf-8"))["sha256"]
    with presentmon.open("rb") as stream:
        if hashlib.file_digest(stream, "sha256").hexdigest() != expected:
            raise RuntimeError("PresentMon does not match the pinned source.json SHA256")
    candidates[".tools/presentmon/" + presentmon.name] = presentmon
    for name in ("LICENSE.txt", "THIRD_PARTY.txt", "source.json"):
        candidates[".tools/presentmon/" + name] = ROOT / "bench/presentmon" / name
    for name in DEPENDENCIES:
        tree(ROOT / ".deps" / name, ".deps/" + name)
    for flavor in ("ffmpeg-lgpl21", "ffmpeg-encoder-gpl2"):
        tree(ROOT / ".deps" / flavor / "sources", ".deps/" + flavor + "/sources")
        for name in ("build-configuration.json", "verification.json"):
            candidates[f".deps/{flavor}/{name}"] = ROOT / ".deps" / flavor / name
    # Synthetic test inputs let fresh decoder verification run without the old fixture encoder.
    decoder_tests = {item["case"]: item for item in json.loads(
        (ROOT / ".deps/ffmpeg-lgpl21/verification.json").read_text(encoding="utf-8"))["tests"]}
    for case, name in {"h264": "h264.mp4", "av1": "av1.mkv", "pcm": "pcm.wav", "aac": "aac.m4a",
                       "mp3": "mp3.mp3", "vorbis": "vorbis.ogg", "opus": "opus.ogg", "flac": "flac.flac"}.items():
        relative = ".deps/ffmpeg-lgpl21/verification/fixtures/" + name
        source = ROOT / relative
        with source.open("rb") as stream:
            if hashlib.file_digest(stream, "sha256").hexdigest() != decoder_tests[case]["fixture_sha256"]:
                raise RuntimeError("Decoder fixture differs from its recorded SHA256: " + relative)
        candidates[relative] = source
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

The engine and dependency source files already contain the modifications used
by the build. Keep these files and their .input-* markers when preparing tools;
do not replace them with fresh upstream checkouts.

Run the commands below in PowerShell from the unpacked WpeBaker-source directory.
Python 3.11 or newer and Git for Windows are external build requirements. The recipes use
C:/Program Files/Git/bin/bash.exe and its usr/bin POSIX utilities, patch.exe and
MSYS runtime. Git is also needed to restore x264's version metadata; the unpacked
project root itself does not need a Git checkout for native or portable builds.

Prepare the locked project-local tools. Skip the corresponding fetch commands
for already prepared locked tools; running fetch requires cached download
archives or network access:

    python scripts/bootstrap-native.py tools
    python scripts/fetch-pkgconf.py
    python scripts/fetch-ffmpeg-lgpl21-inputs.py
    python scripts/fetch-dotnet.py

Use the versions and hashes in scripts/native-inputs.lock.json,
scripts/distribution-inputs.lock.json and scripts/encoder-build-tools.lock.json.
These commands do not install tools globally. Existing locked tools may be reused;
the FFmpeg prefixes, native third-party libraries and renderer must be built from
this source root. The following PATH change affects only this PowerShell session.

Bootstrap the shader compiler from the included sources before FFmpeg. The
third-party-only CMake configuration does not require an existing FFmpeg prefix:

    $env:PATH = (Join-Path (Get-Location) '.tools/llvm-mingw-22/bin') + ';' + $env:PATH
    .tools/cmake/bin/cmake.exe -S engine --preset release -B build/bootstrap-third-party -DWPE_THIRD_PARTY_ONLY=ON
    .tools/cmake/bin/cmake.exe --build build/bootstrap-third-party --target glslang-standalone -j 4
    New-Item -ItemType Directory -Force .tools/ffmpeg-build/bin, .tools/tmp | Out-Null
    Copy-Item build/bootstrap-third-party/bin/glslang.exe .tools/ffmpeg-build/bin/glslang.exe

Recreate x264's Git identity from the included bundle without replacing files:

    git -C .deps/ffmpeg-encoder-gpl2/sources/x264 init
    git -C .deps/ffmpeg-encoder-gpl2/sources/x264 fetch ../../../../.tools/downloads/x264-b35605ace3ddf7c1a5d67a2eb553f034aef41d55.bundle refs/heads/source-pin:refs/heads/source-pin refs/heads/master-pin:refs/remotes/origin/master
    git -C .deps/ffmpeg-encoder-gpl2/sources/x264 reset --mixed source-pin

Build and verify the decoder, then build and verify the encoder:

    python scripts/build-ffmpeg-lgpl21.py --stage all --jobs 4
    python scripts/verify-ffmpeg-lgpl21.py
    python scripts/build-ffmpeg-encoder-gpl2.py --stage all --jobs 4
    python scripts/verify-ffmpeg-encoder-gpl2.py

The included eight synthetic decoder fixtures are checked against the original
verification record when packaging. Verification runs them through the newly
built decoder and replaces verification.json with fresh DLL and license results;
the archived verification record is not evidence for rebuilt libraries. Encoder
verification similarly creates this root's portable encoder and checks the new
decoder baseline. Hardware probes are optional and are not part of these commands.

Build the renderer with its own third-party libraries and provenance, then publish
the self-contained .NET applications and assemble a new portable directory:

    python scripts/build-native-cmake.py --target wpe-render --build-dir build/source-rebuild22 --jobs 4
    python scripts/package-portable.py --native-build-dir build/source-rebuild22 --out dist/source-rebuild

Use a new --out directory if that directory already exists. Do not copy another
root's compiled libraries or build provenance into this build. The portable helper
also runs startup and device enumeration checks; scene rendering and official
Wallpaper Engine playback remain separate verification steps.

The distributed renderer includes the current rendering and encoding changes.
The authoritative source fingerprint and packaged binary SHA256 are in
build-records/build-wpe-render.json.

See README.md, SOURCE.md and scripts/DISTRIBUTION-DEPENDENCIES.md for background.
The matching runtime's build-records name its exact source digest. The checked
Windows LLVM-MinGW compiler is the llvm-mingw-22 entry.

Third-party components, versions and licenses are listed in
THIRD-PARTY-NOTICES.md; license texts are under licenses-extra/ and inside each
dependency's own source tree.

The source ZIP contains sources, not a preinstalled development toolchain.
Downloading the pinned tools requires network access once; thereafter native
builds use the local sources and fully disconnected dependency resolution.
The finished portable application itself does not require Python or network.

The pinned PresentMon collector and its notices are included under
`.tools/presentmon/`, so `package-portable.py` needs no separate collector download.
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
