"""Verify the isolated FFmpeg DLL licenses and real software file decoding."""
from __future__ import annotations

import ctypes
import datetime
import hashlib
import json
import os
import pathlib
import re
import shutil
import subprocess

ROOT = pathlib.Path(__file__).resolve().parents[1]
DEST = ROOT / ".deps/ffmpeg-lgpl21"
PREFIX = DEST / "prefix"
BIN = PREFIX / "bin"
CHECKS = DEST / "verification"
EXPECTED_LICENSE = "LGPL version 2.1 or later"


def sha256(path):
    with path.open("rb") as stream:
        return hashlib.file_digest(stream, "sha256").hexdigest()


def run(command, label, environment=None):
    result = subprocess.run(list(map(str, command)), cwd=ROOT, env=environment,
                            capture_output=True, text=True, encoding="utf-8",
                            errors="replace", creationflags=subprocess.CREATE_NO_WINDOW)
    (CHECKS / (label + ".stdout.txt")).write_text(result.stdout, encoding="utf-8")
    (CHECKS / (label + ".stderr.txt")).write_text(result.stderr, encoding="utf-8")
    if result.returncode:
        raise RuntimeError(f"{label}: exit {result.returncode}: {result.stderr[-2000:]}")
    return result.stdout, result.stderr


def main():
    CHECKS.mkdir(parents=True, exist_ok=True)
    # Only the new bin directory and Windows system paths are used at runtime.
    # The existing LGPLv3 FFmpeg and build-tool directories are excluded.
    runtime_environment = dict(os.environ)
    windows = pathlib.Path(os.environ["SystemRoot"])
    runtime_environment["PATH"] = os.pathsep.join(map(str, [BIN, windows / "System32", windows]))
    ffprobe = BIN / "ffprobe.exe"
    license_output = " ".join("\n".join(run([ffprobe, "-L"], "ffprobe-license", runtime_environment)).split())
    if "GNU Lesser General Public License" not in license_output or "version 2.1" not in license_output:
        raise RuntimeError("ffprobe -L does not report LGPL 2.1")
    run([ffprobe, "-buildconf"], "ffprobe-buildconf", runtime_environment)
    config = (DEST / "build/ffmpeg/config.h").read_text(encoding="utf-8")
    for symbol, value in {"CONFIG_GPL": 0, "CONFIG_NONFREE": 0, "CONFIG_VERSION3": 0,
                          "CONFIG_GPLV3": 0, "CONFIG_LIBDAV1D": 1}.items():
        if not re.search(rf"^#define {symbol} {value}$", config, re.MULTILINE):
            raise RuntimeError(f"Unexpected generated configuration: {symbol}")

    libraries = []
    with os.add_dll_directory(str(BIN)):
        for name, major in [("avutil", 60), ("swresample", 6), ("swscale", 9),
                            ("avcodec", 62), ("avformat", 62)]:
            path = BIN / f"{name}-{major}.dll"
            library = ctypes.CDLL(str(path))
            license_function = getattr(library, name + "_license")
            license_function.restype = ctypes.c_char_p
            actual_license = license_function().decode("utf-8")
            if actual_license != EXPECTED_LICENSE:
                raise RuntimeError(f"{path.name} reports {actual_license!r}")
            configuration_function = getattr(library, name + "_configuration")
            configuration_function.restype = ctypes.c_char_p
            actual_configuration = configuration_function().decode("utf-8")
            version_function = getattr(library, name + "_version")
            version_function.restype = ctypes.c_uint
            version = version_function()
            imports, _ = run([ROOT / ".tools/llvm-mingw-22/bin/llvm-objdump.exe", "-p", path], name + "-imports")
            libraries.append({"file": path.relative_to(ROOT).as_posix(), "sha256": sha256(path),
                              "size": path.stat().st_size, "license": actual_license,
                              "version": [version >> 16, (version >> 8) & 255, version & 255],
                              "configuration": actual_configuration,
                              "imports": re.findall(r"DLL Name: (\S+)", imports)})

    fixtures = CHECKS / "fixtures"
    fixtures.mkdir(exist_ok=True)
    # The old FFmpeg is used solely as a fixture encoder. It is never copied
    # into the new prefix and no LGPLv3 DLL participates in the decode checks.
    fixture_encoder = ROOT / ".deps/ffmpeg/bin/ffmpeg.exe"
    cases = [
        ("h264", "h264.mp4", ["-f", "lavfi", "-i", "testsrc2=size=128x96:rate=24", "-frames:v", "6", "-c:v", "libopenh264", "-b:v", "1M"], "h264", 6),
        ("av1", "av1.mkv", ["-f", "lavfi", "-i", "testsrc2=size=128x96:rate=24", "-frames:v", "6", "-c:v", "libaom-av1", "-cpu-used", "8", "-crf", "30", "-b:v", "0"], "av1", 6),
        ("pcm", "pcm.wav", ["-f", "lavfi", "-i", "sine=frequency=997:sample_rate=48000:duration=0.25", "-c:a", "pcm_s16le"], "pcm_s16le", None),
        ("aac", "aac.m4a", ["-f", "lavfi", "-i", "sine=frequency=997:sample_rate=48000:duration=0.25", "-c:a", "aac"], "aac", None),
        ("mp3", "mp3.mp3", ["-f", "lavfi", "-i", "sine=frequency=997:sample_rate=48000:duration=0.25", "-c:a", "libmp3lame"], "mp3", None),
        ("vorbis", "vorbis.ogg", ["-f", "lavfi", "-i", "sine=frequency=997:sample_rate=48000:duration=0.25", "-c:a", "libvorbis"], "vorbis", None),
        ("opus", "opus.ogg", ["-f", "lavfi", "-i", "sine=frequency=997:sample_rate=48000:duration=0.25", "-c:a", "libopus"], "opus", None),
        ("flac", "flac.flac", ["-f", "lavfi", "-i", "sine=frequency=997:sample_rate=48000:duration=0.25", "-c:a", "flac"], "flac", None),
    ]
    results = []
    for label, filename, options, codec, expected_frames in cases:
        path = fixtures / filename
        if not path.exists():
            run([fixture_encoder, "-hide_banner", "-v", "error", "-y", *options, path], label + "-encode")
        decoder_options = ["-c:v", "libdav1d"] if label == "av1" else []
        output, stderr = run([ffprobe, "-v", "error", *decoder_options, "-show_frames", "-show_streams",
                              "-count_frames", "-of", "json", path], label + "-decode", runtime_environment)
        if stderr.strip():
            raise RuntimeError(f"{label}: decoder reported an error: {stderr}")
        decoded = json.loads(output)
        frames, streams = decoded.get("frames", []), decoded.get("streams", [])
        if not frames or len(streams) != 1 or streams[0].get("codec_name") != codec:
            raise RuntimeError(f"{label}: missing decoded frames or wrong codec")
        if expected_frames is not None and len(frames) != expected_frames:
            raise RuntimeError(f"{label}: expected {expected_frames} frames, got {len(frames)}")
        samples = sum(frame.get("nb_samples", 0) for frame in frames)
        if expected_frames is None and not 11000 <= samples <= 14000:
            raise RuntimeError(f"{label}: unexpected decoded audio sample count {samples}")
        if label in ("pcm", "flac", "opus") and samples != 12000:
            raise RuntimeError(f"{label}: expected exactly 12000 decoded samples, got {samples}")
        results.append({"case": label, "codec": codec, "forced_decoder": "libdav1d" if label == "av1" else None,
                        "fixture_sha256": sha256(path), "frames": len(frames), "audio_samples": samples,
                        "decoder_errors": 0})
        print(f"PASS {label}: {len(frames)} frames, {samples} audio samples", flush=True)

    license_dir = PREFIX / "share/licenses"
    license_files = []
    for source, relative in [
        (DEST / "sources/ffmpeg/LICENSE.md", "ffmpeg/LICENSE.md"),
        (DEST / "sources/ffmpeg/COPYING.LGPLv2.1", "ffmpeg/COPYING.LGPLv2.1"),
        (DEST / "sources/dav1d/COPYING", "dav1d/COPYING"),
    ]:
        target = license_dir / relative
        target.parent.mkdir(parents=True, exist_ok=True)
        shutil.copyfile(source, target)
        license_files.append({"file": target.relative_to(ROOT).as_posix(), "sha256": sha256(target)})
    lock = ROOT / "scripts/distribution-inputs.lock.json"
    provenance = {
        "schema_version": 1, "verified_utc": datetime.datetime.now(datetime.timezone.utc).isoformat(),
        "configuration": json.loads((DEST / "build-configuration.json").read_text(encoding="utf-8")),
        "generated_config_sha256": sha256(DEST / "build/ffmpeg/config.h"),
        "input_lock_sha256": sha256(lock), "inputs": json.loads(lock.read_text(encoding="utf-8")),
        "libraries": libraries, "ffprobe_sha256": sha256(ffprobe), "licenses": license_files,
        "runtime_path": runtime_environment["PATH"], "tests": results,
        "scope": "Software file decoding and runtime license queries; hardware decode and renderer relink are separate checks.",
    }
    (DEST / "verification.json").write_text(json.dumps(provenance, indent=2) + "\n", encoding="utf-8")
    print("Verified LGPL 2.1 DLLs and 8 real decode cases:", DEST / "verification.json")


if __name__ == "__main__":
    main()
