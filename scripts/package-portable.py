"""Publish a local Windows x64 preview bundle. Developer helper; not needed at runtime."""
from __future__ import annotations
import argparse
import hashlib
import importlib.util
import json
import os
from pathlib import Path
import shutil
import subprocess
import time
import zipfile

ROOT = Path(__file__).resolve().parents[1]
provenance_spec = importlib.util.spec_from_file_location("native_provenance", ROOT / "scripts/native-provenance.py")
if provenance_spec is None or provenance_spec.loader is None: raise RuntimeError("Could not load native provenance helper.")
provenance = importlib.util.module_from_spec(provenance_spec)
provenance_spec.loader.exec_module(provenance)
# Statically linked renderer dependencies: their licenses require the notice to ship with the
# binary. The authoritative copy is the one that took part in the build, under .deps; the
# licenses-extra/ copies (downloaded from each upstream at the pinned revision) are the
# fallback, and the only source for wavsen and vvk, whose pinned revisions predate their license files
# (the author confirmed on 2026-09-18 that the terms cover those revisions).
STATIC_LICENSES = {
    "freetype.LICENSE.TXT": ".deps/freetype/LICENSE.TXT",
    "freetype.FTL.TXT": ".deps/freetype/docs/FTL.TXT",
    "glslang.LICENSE.txt": ".deps/glslang/LICENSE.txt",
    "lz4.LICENSE": ".deps/lz4/LICENSE",
    "lz4.lib.LICENSE": ".deps/lz4/lib/LICENSE",
    "quickjs-ng.LICENSE": ".deps/quickjs/LICENSE",
    "vma.LICENSE.txt": ".deps/vma/LICENSE.txt",
    "spirv-reflect.LICENSE": ".deps/spirv-reflect/LICENSE",
    "eigen.COPYING.MPL2": ".deps/eigen/COPYING.MPL2",
    "eigen.COPYING.README": ".deps/eigen/COPYING.README",
    "vulkan-headers.LICENSE.md": ".deps/vulkan-headers/LICENSE.md",
    "vulkan-headers.Apache-2.0.txt": ".deps/vulkan-headers/LICENSES/Apache-2.0.txt",
    "vulkan-headers.MIT.txt": ".deps/vulkan-headers/LICENSES/MIT.txt",
    "vulkan-loader.LICENSE.txt": ".deps/vulkan-loader/LICENSE.txt",
    "rstd.LICENSE-MIT": ".deps/rstd/LICENSE-MIT",
    "rstd.LICENSE-APACHE": ".deps/rstd/LICENSE-APACHE",
    "wavsen.LICENSE-MIT": None,
    "wavsen.LICENSE-APACHE": None,
    "vvk.LICENSE-MIT": None,
    "vvk.LICENSE-APACHE": None,
}


def digest(path: Path) -> str:
    with path.open("rb") as stream:
        return hashlib.file_digest(stream, "sha256").hexdigest()


def run(arguments: list[str], env: dict[str, str], log: Path, timeout: int = 900) -> None:
    result = subprocess.run(arguments, cwd=ROOT, env=env, capture_output=True, timeout=timeout,
                            creationflags=subprocess.CREATE_NO_WINDOW)
    log.write_bytes(result.stdout + b"\n" + result.stderr)
    if result.returncode:
        raise RuntimeError(f"Command exited {result.returncode}; see {log}")


def copy_static_licenses(target: Path) -> dict[str, str]:
    """Copy every statically linked dependency's license text; fail if one cannot be found."""
    chosen: dict[str, str] = {}
    for name, preferred in STATIC_LICENSES.items():
        source = ROOT / preferred if preferred else None
        if source is None or not source.is_file():
            source = ROOT / "licenses-extra" / name
        if not source.is_file():
            raise FileNotFoundError(f"Missing license text for {name}; looked for {preferred} and licenses-extra/{name}")
        shutil.copy2(source, target / name)
        chosen[name] = source.relative_to(ROOT).as_posix()
    return chosen


def main() -> None:
    parser = argparse.ArgumentParser()
    parser.add_argument("--out", type=Path, default=ROOT / "dist" / f"preview-{time.time_ns()}")
    parser.add_argument("--native-build-dir", type=Path, default=ROOT / "build/native-release22",
                        help="Verified native build directory to package (default: build/native-release22)")
    parser.add_argument("--acceptance-report", type=Path,
                        help="Existing validation record to copy and reference without changing its result")
    args = parser.parse_args()
    native_build, native_renderer, native_record = provenance.load_verified_build(args.native_build_dir)
    presentmon_records = ROOT / "scripts/presentmon"
    presentmon_source = json.loads((presentmon_records / "source.json").read_text(encoding="utf-8"))
    presentmon_binary = ROOT / ".tools/presentmon" / presentmon_source["file"]
    if digest(presentmon_binary) != presentmon_source["sha256"]:
        raise RuntimeError("PresentMon does not match the recorded upstream binary.")
    output = args.out.resolve()
    output.mkdir(parents=True, exist_ok=False)
    bundle = output / "WpeBaker"
    bundle.mkdir()
    env = os.environ.copy()
    env.update(DOTNET_ROOT=str(ROOT / ".dotnet"), DOTNET_CLI_HOME=str(ROOT / ".tools/dotnet-home"),
               NUGET_PACKAGES=str(ROOT / ".tools/nuget-packages"), DOTNET_CLI_UI_LANGUAGE="en-US",
               DOTNET_NOLOGO="1", DOTNET_CLI_TELEMETRY_OPTOUT="1", DOTNET_SKIP_FIRST_TIME_EXPERIENCE="1")
    dotnet = str(ROOT / ".dotnet/dotnet.exe")
    for name in ("Baker.App", "Baker.Cli"):
        print(f"Publishing {name}", flush=True)
        run([dotnet, "publish", str(ROOT / "src" / name / f"{name}.csproj"), "-c", "Release", "-r", "win-x64",
             "--self-contained", "true", "--nologo", "-p:PublishSingleFile=false", "-p:PublishTrimmed=false",
             "-p:NuGetAudit=false", "-p:UseSharedCompilation=false", "-p:RestoreSources=https://api.nuget.org/v3/index.json",
             "-o", str(bundle)], env, output / f"{name}.publish.log")
    renderer = bundle / "renderer"
    renderer.mkdir()
    shutil.copy2(native_renderer, renderer)
    for name in ("libc++.dll", "libunwind.dll"):
        shutil.copy2(ROOT / ".tools/llvm-mingw-22/bin" / name, renderer)
    for library in (ROOT / ".deps/ffmpeg-lgpl21/prefix/bin").glob("*.dll"):
        shutil.copy2(library, renderer)
    shutil.copytree(ROOT / ".deps/ffmpeg-encoder-gpl2/portable", bundle / "encoder")
    performance = bundle / "performance"
    performance.mkdir()
    shutil.copy2(presentmon_binary, performance / "PresentMon.exe")
    shutil.copy2(presentmon_records / "source.json", performance / "source.json")
    tools = {"renderer": "renderer/wpe-render.exe", "ffmpeg": "encoder/ffmpeg.exe",
             "ffprobe": "encoder/ffprobe.exe", "runtime_directories": ["renderer"]}
    (bundle / "tools.json").write_text(json.dumps(tools, indent=2) + "\n", encoding="utf-8")
    # SOURCE.md states where the corresponding GPL/LGPL sources are; GPL v2 section 3 needs it
    # inside the binary package, not only on the download page.
    for name in ("README.md", "README.zh-CN.md", "LICENSE", "SOURCE.md", "THIRD-PARTY-NOTICES.md"):
        shutil.copy2(ROOT / name, bundle)
    licenses = bundle / "licenses"
    licenses.mkdir()
    shutil.copy2(presentmon_records / "LICENSE.txt", licenses / "PresentMon.LICENSE.txt")
    shutil.copy2(presentmon_records / "THIRD_PARTY.txt", licenses / "PresentMon.THIRD_PARTY.txt")
    shutil.copy2(ROOT / "engine/LICENSE", licenses / "open-wallpaper-engine.LICENSE")
    shutil.copy2(ROOT / ".tools/llvm-mingw-22/LICENSE.TXT", licenses / "llvm-mingw.LICENSE.txt")
    shutil.copy2(ROOT / ".dotnet/LICENSE.txt", licenses / "dotnet.LICENSE.txt")
    shutil.copy2(ROOT / ".dotnet/ThirdPartyNotices.txt", licenses / "dotnet.ThirdPartyNotices.txt")
    shutil.copytree(ROOT / ".deps/ffmpeg-lgpl21/prefix/share/licenses", licenses / "renderer-codecs")
    license_sources = copy_static_licenses(licenses)
    records_directory = bundle / "build-records"
    records_directory.mkdir()
    for name in ("build-wpe-render.json", "source-wpe-render.json"):
        shutil.copy2(native_build / "provenance" / name, records_directory)
    acceptance = None
    if args.acceptance_report is not None:
        report = args.acceptance_report.resolve()
        if not report.is_file():
            raise FileNotFoundError(f"Acceptance report does not exist: {report}")
        validation = bundle / "validation"
        validation.mkdir()
        copied = validation / report.name
        shutil.copy2(report, copied)
        acceptance = {"report": copied.relative_to(bundle).as_posix(), "sha256": digest(copied)}
    # Check the package's native binaries with no developer toolchain in PATH.
    isolated = env.copy()
    isolated["PATH"] = str(renderer) + os.pathsep + str(Path(os.environ["SystemRoot"]) / "System32")
    isolated["DOTNET_ROOT"] = str(bundle / "no-installed-dotnet")
    run([str(renderer / "wpe-render.exe"), "--version"], isolated, output / "renderer-version.log", 30)
    run([str(bundle / "wpe-baker.exe"), "devices"], isolated, output / "devices.log", 30)
    run([str(bundle / "encoder/ffmpeg.exe"), "-version"], isolated, output / "encoder-version.log", 30)
    manifest = {"schema_version": 1, "status": "local_preview_packaged", "official_playback": "not_verified",
                "clean_windows": "not_verified", "native_build_dir": native_build.relative_to(ROOT).as_posix(),
                "native_renderer_sha256": native_record["binary"]["sha256"],
                "distribution_note": "Contains GPL v2 and LGPL v2.1 programs; distribute only together with "
                                     "WpeBaker-source.zip from the same location. See SOURCE.md.",
                "license_texts": license_sources, "files": {}}
    if acceptance is not None:
        manifest["acceptance_report"] = acceptance
    for file in sorted(bundle.rglob("*")):
        if file.is_file():
            manifest["files"][file.relative_to(bundle).as_posix()] = {"bytes": file.stat().st_size, "sha256": digest(file)}
    (bundle / "package.json").write_text(json.dumps(manifest, indent=2) + "\n", encoding="utf-8")
    archive = output / "WpeBaker-win-x64-preview.zip"
    with zipfile.ZipFile(archive, "x", zipfile.ZIP_DEFLATED, compresslevel=6) as zipped:
        for file in sorted(bundle.rglob("*")):
            if file.is_file():
                zipped.write(file, file.relative_to(output).as_posix())
    with zipfile.ZipFile(archive) as zipped:
        bad = zipped.testzip()
        if bad: raise RuntimeError(f"ZIP verification failed: {bad}")
    result = {"directory": str(bundle), "archive": str(archive), "bytes": archive.stat().st_size, "sha256": digest(archive),
              "note": "Local preview; distribute only together with the corresponding source bundle and dependency notices."}
    (output / "package-result.json").write_text(json.dumps(result, indent=2) + "\n", encoding="utf-8")
    print(json.dumps(result, indent=2), flush=True)


if __name__ == "__main__":
    main()
