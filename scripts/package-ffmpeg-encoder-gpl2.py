"""Bundle verified encoder binaries together with their corresponding sources."""
from __future__ import annotations

import hashlib
import json
import pathlib
import subprocess
import zipfile

ROOT = pathlib.Path(__file__).resolve().parents[1]
DEST = ROOT / ".deps/ffmpeg-encoder-gpl2"


def sha256(path):
    with path.open("rb") as stream:
        return hashlib.file_digest(stream, "sha256").hexdigest()


def main():
    verification = json.loads((DEST / "verification.json").read_text(encoding="utf-8"))
    runtime = DEST / "portable"
    for item in verification["runtime_files"]:
        if sha256(runtime / item["file"]) != item["sha256"]:
            raise RuntimeError("Verified runtime changed: " + item["file"])
    for item in verification["licenses"]:
        if sha256(runtime / item["file"]) != item["sha256"]:
            raise RuntimeError("Verified license notice changed: " + item["file"])
    native_inputs = json.loads((ROOT / "scripts/native-inputs.lock.json").read_text(encoding="utf-8"))
    distribution_inputs = json.loads((ROOT / "scripts/distribution-inputs.lock.json").read_text(encoding="utf-8"))
    build_tools = [item for item in native_inputs if item["name"] in ("llvm-mingw-22", "pkgconf", "cmake", "ninja")]
    build_tools += [item for item in distribution_inputs if item["name"] in ("nasm", "make-msys")]
    git_version = subprocess.run(["git", "--version"], capture_output=True, text=True,
                                 creationflags=subprocess.CREATE_NO_WINDOW, check=True).stdout.strip()
    tools_record = {"archives": build_tools, "git_for_windows": git_version,
                    "actual_tool_files": verification["build_tools"],
                    "shell_requirement": "Git for Windows supplies Bash, MSYS runtime and standard POSIX build utilities; no WSL is used."}
    tools_path = ROOT / "scripts/encoder-build-tools.lock.json"
    tools_path.write_text(json.dumps(tools_record, indent=2) + "\n", encoding="utf-8")
    readme = """# FFmpeg 8.1.2 / x264 / x265 portable H.264 and HEVC encoder

This package contains the two Windows x64 programs, their six adjacent DLLs,
license notices, complete corresponding FFmpeg/x264/x265 source archives, an offline
x264 Git bundle, exact configure settings, build recipes, and verification data.
The programs and DLLs identify their configuration as GPL version 2 or later.
Version-3 and nonfree code are disabled. x264 is GPL-2.0-or-later, as stated in
its original COPYING and public-header notice. x265 uses the same license terms.

Keep the eight files in bin together. The encoder's DLLs belong to this process;
the renderer uses its separately verified LGPL 2.1 library directory.
The encoder set is libx264, libx264rgb, libx265, h264_mf, hevc_mf, av1_mf,
h264_nvenc, hevc_nvenc, h264_amf, hevc_amf, rawvideo, AAC, PCM f32le and PCM s16le.
NVENC and AMF are compiled from MIT-licensed headers only (nv-codec-headers
n12.1.14.0, AMF-headers v1.5.2); the NVIDIA or AMD driver supplies the runtime
library, so these entries open only on machines with that driver. The three Media Foundation encoder
entry points and D3D11VA decoding of H.264/HEVC/AV1 are compiled; availability
depends on the actual adapter, driver and format and must be probed at runtime.
An encoder listing alone does not establish hardware support. x265 is a static
8-bit build with x86 assembly and runtime CPU detection; its C++ runtime is also
linked statically. Software and lossless RGB paths remain available.

The source archives are unmodified and were compared file by file with the built
source trees. FFmpeg is pinned to 38b88335f99e76ed89ff3c93f877fdefce736c13 (n8.1.2).
x264 is pinned to b35605ace3ddf7c1a5d67a2eb553f034aef41d55 (r3222), with its
version.sh comparison ref pinned to 0480cb05fa188d37ae87e8f4fd8f1aea3711f7ee.
The x264 bundle includes both pinned histories so the version number can be
reproduced without contacting VideoLAN. The original source filenames are kept;
the FFmpeg archive name reflects the initial download, not this build's license.
x265 uses the original VideoLAN x265_4.2.tar.gz archive, SHA256
40b1ea0453e0309f0eba934e0ddf533f8f6295966679e8894e8f1c1c8d5e1210.
Its embedded version metadata reports 4.2+1-e444744. No x265 source patch is used.

To rebuild in the wpe-baker-next project layout:

1. Copy rebuild/scripts into the project's scripts directory and sources/* into
   .tools/downloads. Preserve the pinned filenames in encoder-inputs.lock.json.
2. Prepare the project-local LLVM-MinGW 22.1.8 UCRT, NASM 3.02, pkgconf 3.0.7 and
   MSYS Make 4.4.1, CMake 4.4.3 and Ninja 1.13.2 tools at the paths in
   encoder-build-tools.lock.json. The lock
   records upstream archive URLs/hashes and actual tool hashes. Git for Windows
   supplies its existing Bash/MSYS utilities at C:/Program Files/Git; no global
   installation is performed by these scripts.
3. Run python scripts/fetch-encoder-inputs.py. When the included bundle exists,
   x264 can be restored from it without a network source fetch. The included
   x265 source archive is likewise verified and extracted offline.
4. Run python scripts/build-ffmpeg-encoder-gpl2.py --stage all.
5. Run python scripts/verify-ffmpeg-encoder-gpl2.py. Its final renderer-baseline
   check additionally expects the project's independently verified LGPL prefix.
   It tests portable-hevc first, backs up an existing portable directory, then
   copies the verified encoder into portable. Run package-ffmpeg-encoder-gpl2.py
   to create this binary-and-source ZIP.

For an explicitly requested local hardware probe, also pass --hardware-source
SAMPLE.mp4 and --hardware-vendor-id VENDOR_ID to the verification script. It
forces D3D11VA download of five NV12 frames on the selected vendor, then tries
five-frame h264_mf/hevc_mf/av1_mf hardware encodes with system MFT selection.
Exit codes, progress, adapter/MFT identities and original errors are recorded
in verification.json. Unavailable MFTs do not disable the software encoder.

The build uses four jobs by default, native Win32 threads, x264 assembly with
all 8/10-bit and chroma modes, 8-bit x265, and a fixed SOURCE_DATE_EPOCH. This is a source/build
recipe record; byte-identical binaries across different hosts are not claimed.

Verification exercised the current export command shape: 12 frames at exact
120000/1001 FPS, BT.709 limited-range H.264 yuv420p, byte-exact RGB H.264,
byte-exact side-by-side RGB+alpha packing, the corresponding compatibility
packing, HEVC Main yuv420p hvc1 encode/decode with both ordinary and packed
frames, and stereo AAC mux/decode. It used only bin plus Windows system paths
at runtime. verification.json includes hashes, actual license API results,
DLL imports and measurements. Full original license notices are included.
"""
    readme_path = DEST / "ENCODER-BUILD.md"
    readme_path.write_text(readme, encoding="utf-8")
    archive_path = DEST / "ffmpeg-8.1.2-x264-x265-gpl2-win-x64-with-source.zip"
    inputs = verification["inputs"]
    files = [(runtime / item["file"], "bin/" + item["file"]) for item in verification["runtime_files"]]
    files += [(path, path.relative_to(runtime).as_posix()) for path in (runtime / "licenses").rglob("*") if path.is_file()]
    for name in ("ffmpeg", "x264", "x265"):
        path = ROOT / inputs[name]["archive"]
        if sha256(path) != inputs[name]["sha256"]:
            raise RuntimeError("Pinned source archive changed: " + name)
        files.append((path, "sources/" + path.name))
    bundle = ROOT / inputs["x264"]["bundle"]
    files.append((bundle, "sources/" + bundle.name))
    files.append((ROOT / inputs["x264"]["patch"], "patches/x264.patch"))
    for name in ("fetch-encoder-inputs.py", "build-ffmpeg-encoder-gpl2.py", "verify-ffmpeg-encoder-gpl2.py",
                 "package-ffmpeg-encoder-gpl2.py", "encoder-inputs.lock.json", "encoder-build-tools.lock.json"):
        files.append((ROOT / "scripts" / name, "rebuild/scripts/" + name))
    files += [(readme_path, "ENCODER-BUILD.md"), (DEST / "verification.json", "verification.json"),
              (DEST / "build-configuration.json", "build-configuration.json"),
              (DEST / "build/x264/x264_config.h", "configured/x264_config.h"),
              (DEST / "build/x264/config.mak", "configured/x264-config.mak"),
              (DEST / "build/x265-amd64/x265_config.h", "configured/x265_config.h"),
              (DEST / "build/x265-amd64/CMakeCache.txt", "configured/x265-CMakeCache.txt"),
              (DEST / "build/x265-amd64/x265.pc", "configured/x265.pc"),
              (DEST / "build/ffmpeg-hevc/config.h", "configured/ffmpeg-config.h"),
              (DEST / "build/ffmpeg-hevc/config_components.h", "configured/ffmpeg-config_components.h"),
              (DEST / "build/ffmpeg-hevc/ffbuild/config.mak", "configured/ffmpeg-config.mak")]
    with zipfile.ZipFile(archive_path, "w", zipfile.ZIP_DEFLATED, compresslevel=6) as archive:
        for source, name in files:
            archive.write(source, name)
    with zipfile.ZipFile(archive_path) as archive:
        error = archive.testzip()
        if error:
            raise RuntimeError("ZIP integrity check failed: " + error)
        if json.loads(archive.read("verification.json"))["input_lock_sha256"] != verification["input_lock_sha256"]:
            raise RuntimeError("Packaged verification mismatch")
    result = {"archive": str(archive_path), "sha256": sha256(archive_path), "bytes": archive_path.stat().st_size,
              "members": len(files), "runtime": str(runtime), "source_archives": [inputs[name]["archive"] for name in ("ffmpeg", "x264", "x265")],
              "x264_bundle": inputs["x264"]["bundle"], "zip_integrity": "passed"}
    (DEST / "package.json").write_text(json.dumps(result, indent=2) + "\n", encoding="utf-8")
    print(json.dumps(result, indent=2))


if __name__ == "__main__":
    main()
