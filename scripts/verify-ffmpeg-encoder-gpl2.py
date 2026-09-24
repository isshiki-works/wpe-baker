"""Verify GPL 2 encoder licensing, a standalone runtime, and real export paths."""
from __future__ import annotations

import ctypes
import argparse
import datetime
from fractions import Fraction
import hashlib
import json
import math
import os
import pathlib
import re
import shutil
import struct
import subprocess
import tarfile
import time
import zipfile

ROOT = pathlib.Path(__file__).resolve().parents[1]
DEST = ROOT / ".deps/ffmpeg-encoder-gpl2"
PREFIX = DEST / "prefix"
RUNTIME = DEST / "portable-hevc"
CHECKS = DEST / "verification"
EXPECTED_LICENSE = "GPL version 2 or later"


def sha256(path):
    with path.open("rb") as stream:
        return hashlib.file_digest(stream, "sha256").hexdigest()


def execute(command, label, environment=None, input_bytes=None):
    result = subprocess.run(list(map(str, command)), cwd=ROOT, env=environment, input=input_bytes,
                            capture_output=True, creationflags=subprocess.CREATE_NO_WINDOW)
    (CHECKS / (label + ".stderr.txt")).write_bytes(result.stderr)
    if result.returncode:
        raise RuntimeError(f"{label}: exit {result.returncode}: {result.stderr[-2500:].decode('utf-8', 'replace')}")
    return result.stdout, result.stderr


def source_matches_archive(source, archive_path):
    count = 0
    if archive_path.name.endswith(".tar.gz"):
        with tarfile.open(archive_path) as archive:
            for member in archive:
                if not member.isfile():
                    continue
                path = source.joinpath(*pathlib.PurePosixPath(member.name).parts[1:])
                with archive.extractfile(member) as stream:
                    if not path.is_file() or path.read_bytes() != stream.read():
                        raise RuntimeError("Source differs from pinned archive: " + str(path))
                count += 1
        return {"files_verified": count, "archive_sha256": sha256(archive_path), "modified_files": 0}
    with zipfile.ZipFile(archive_path) as archive:
        for member in archive.infolist():
            if member.is_dir():
                continue
            relative = pathlib.PurePosixPath(member.filename).parts[1:]
            path = source.joinpath(*relative)
            if not path.is_file() or path.read_bytes() != archive.read(member):
                raise RuntimeError("Source differs from pinned archive: " + str(path))
            count += 1
    return {"files_verified": count, "archive_sha256": sha256(archive_path), "modified_files": 0}


def hardware_probes(ffmpeg, ffprobe, environment, source, vendor_id):
    source_info, _ = execute([ffprobe, "-v", "error", "-show_streams", "-of", "json", source],
                             "hardware-source-probe", environment)
    def probe(command, label):
        started = time.monotonic()
        try:
            result = subprocess.run(list(map(str, command)), cwd=ROOT, env=environment, capture_output=True,
                                    timeout=30, creationflags=subprocess.CREATE_NO_WINDOW)
            stdout, stderr, code = result.stdout, result.stderr, result.returncode
        except subprocess.TimeoutExpired as error:
            stdout, stderr, code = error.stdout or b"", error.stderr or b"", None
        (CHECKS / (label + ".stdout.txt")).write_bytes(stdout)
        (CHECKS / (label + ".stderr.txt")).write_bytes(stderr)
        progress = stdout.decode("utf-8", "replace")
        errors = stderr.decode("utf-8", "replace")
        frames = re.findall(r"^frame=(\d+)$", progress, re.MULTILINE)
        frame_count = int(frames[-1]) if frames else 0
        passed = code == 0 and frame_count == 5 and "progress=end" in progress
        return {"case": label, "status": "passed" if passed else "timeout" if code is None else "failed",
                "command": list(map(str, command)), "exit_code": code, "progress_frames": frame_count,
                "seconds": round(time.monotonic() - started, 3), "progress": progress, "stderr": errors}

    selector = "d3d11va=probe:,vendor_id=" + vendor_id
    decode = probe([ffmpeg, "-hide_banner", "-nostdin", "-v", "verbose", "-init_hw_device", selector,
                    "-hwaccel", "d3d11va", "-hwaccel_device", "probe", "-hwaccel_output_format", "d3d11",
                    "-i", source, "-an", "-frames:v", "5", "-vf", "hwdownload,format=nv12",
                    "-c:v", "rawvideo", "-f", "null", "-", "-progress", "pipe:1"], "d3d11va-five-frame-download")
    decode["adapter_identity"] = re.findall(r"Using device [^\r\n]+", decode["stderr"])
    decode["adapter_index"] = re.findall(r"Selecting d3d11va adapter (\d+)", decode["stderr"])
    print("Hardware probe", decode["case"], decode["status"], "frames=" + str(decode["progress_frames"]), flush=True)
    input_nv12 = CHECKS / "mf-input.nv12"
    input_nv12.write_bytes(b"".join(bytes([64 + frame * 16]) * (640 * 360) + bytes([128]) * (640 * 360 // 2)
                                   for frame in range(5)))
    encoders = []
    for encoder in ("h264_mf", "hevc_mf", "av1_mf"):
        output = CHECKS / (encoder + "-hardware.mp4")
        encoded = probe([ffmpeg, "-hide_banner", "-nostdin", "-y", "-v", "info", "-f", "rawvideo",
                         "-pixel_format", "nv12", "-video_size", "640x360", "-framerate", "30", "-i", input_nv12,
                         "-an", "-frames:v", "5",
                         "-c:v", encoder, "-hw_encoding", "1", "-b:v", "2M", "-progress", "pipe:1", output], encoder)
        encoded["mft_name"] = re.findall(r"MFT name: '([^']+)'", encoded["stderr"])
        encoded["adapter_selection"] = "Media Foundation system selection; no explicit adapter was requested"
        encoded["requested_stream"] = {"codec_name": encoder.removesuffix("_mf"), "width": 640,
                                        "height": 360, "frames": 5, "fps": "30/1"}
        if encoded["status"] == "passed":
            data, _ = execute([ffprobe, "-v", "error", "-count_packets", "-show_streams", "-of", "json", output],
                              encoder + "-hardware-probe", environment)
            stream = json.loads(data)["streams"][0]
            encoded["stream"] = stream
            if (int(stream["nb_read_packets"]) != 5 or stream["codec_name"] != encoder.removesuffix("_mf")
                    or (stream["width"], stream["height"]) != (640, 360) or Fraction(stream["avg_frame_rate"]) != 30):
                encoded["status"] = "failed"
                encoded["validation_error"] = "Encoded stream differs from requested codec, dimensions, frame count or frame rate"
            encoded["sha256"] = sha256(output)
        encoders.append(encoded)
        print("Hardware probe", encoder, encoded["status"], "frames=" + str(encoded["progress_frames"]), flush=True)
    return {"source": str(source), "source_sha256": sha256(source), "adapter_selector": selector,
            "source_streams": json.loads(source_info)["streams"],
            "scope": "Observed on this host and these drivers only; no guarantee for other adapters or formats",
            "d3d11va": decode, "media_foundation": encoders}


def main():
    parser = argparse.ArgumentParser()
    parser.add_argument("--hardware-source", type=pathlib.Path)
    parser.add_argument("--hardware-vendor-id")
    options = parser.parse_args()
    if bool(options.hardware_source) != bool(options.hardware_vendor_id):
        parser.error("Hardware probes require both --hardware-source and --hardware-vendor-id")
    if options.hardware_source and not options.hardware_source.is_file():
        parser.error("Hardware probe source does not exist")
    CHECKS.mkdir(parents=True, exist_ok=True)
    RUNTIME.mkdir(exist_ok=True)
    for source in (PREFIX / "bin").iterdir():
        if source.suffix.lower() == ".dll" or source.name in ("ffmpeg.exe", "ffprobe.exe"):
            shutil.copyfile(source, RUNTIME / source.name)
    ffmpeg, ffprobe = RUNTIME / "ffmpeg.exe", RUNTIME / "ffprobe.exe"
    windows = pathlib.Path(os.environ["SystemRoot"])
    environment = dict(os.environ)
    environment["PATH"] = os.pathsep.join(map(str, [RUNTIME, windows / "System32", windows]))
    for program in (ffmpeg, ffprobe):
        stdout, stderr = execute([program, "-L"], program.stem + "-license", environment)
        (CHECKS / (program.stem + "-license.stdout.txt")).write_bytes(stdout)
        text = " ".join((stdout + b"\n" + stderr).decode("utf-8").split())
        if "GNU General Public License" not in text or "version 2" not in text or "GNU Lesser" in text:
            raise RuntimeError(program.name + " did not identify the GPL 2 configuration")
    encoder_list, _ = execute([ffmpeg, "-hide_banner", "-encoders"], "encoders", environment)
    (CHECKS / "encoders.stdout.txt").write_bytes(encoder_list)
    encoders = re.findall(r"^ [VAS][A-Z.]{5}\s+(\w+)", encoder_list.decode("utf-8"), re.MULTILINE)
    expected_encoders = {"libx264", "libx264rgb", "libx265", "h264_mf", "hevc_mf", "av1_mf", "h264_nvenc", "hevc_nvenc",
                         "rawvideo", "aac", "pcm_s16le", "pcm_f32le"}
    if set(encoders) != expected_encoders:
        raise RuntimeError("Unexpected active encoder set: " + repr(encoders))
    config = (DEST / "build/ffmpeg-hevc/config.h").read_text(encoding="utf-8")
    for symbol, value in {"CONFIG_GPL": 1, "CONFIG_NONFREE": 0, "CONFIG_VERSION3": 0,
                          "CONFIG_GPLV3": 0, "CONFIG_LIBX264": 1, "CONFIG_LIBX265": 1,
                          "CONFIG_D3D11VA": 1, "CONFIG_MEDIAFOUNDATION": 1}.items():
        if not re.search(rf"^#define {symbol} {value}$", config, re.MULTILINE):
            raise RuntimeError("Unexpected configuration: " + symbol)
    components = (DEST / "build/ffmpeg-hevc/config_components.h").read_text(encoding="utf-8")
    for symbol in ("CONFIG_H264_D3D11VA2_HWACCEL", "CONFIG_HEVC_D3D11VA2_HWACCEL",
                   "CONFIG_AV1_D3D11VA2_HWACCEL", "CONFIG_HWDOWNLOAD_FILTER"):
        if not re.search(rf"^#define {symbol} 1$", components, re.MULTILINE):
            raise RuntimeError("Required hardware-probe component was not compiled: " + symbol)

    libraries = []
    with os.add_dll_directory(str(RUNTIME)):
        for name, major in [("avutil", 60), ("swresample", 6), ("swscale", 9),
                            ("avcodec", 62), ("avformat", 62), ("avfilter", 11)]:
            path = RUNTIME / f"{name}-{major}.dll"
            library = ctypes.CDLL(str(path))
            license_function = getattr(library, name + "_license")
            license_function.restype = ctypes.c_char_p
            actual_license = license_function().decode("utf-8")
            if actual_license != EXPECTED_LICENSE:
                raise RuntimeError(path.name + " reports " + actual_license)
            configuration_function = getattr(library, name + "_configuration")
            configuration_function.restype = ctypes.c_char_p
            actual_configuration = configuration_function().decode("utf-8")
            imports, _ = execute([ROOT / ".tools/llvm-mingw-22/bin/llvm-objdump.exe", "-p", path], name + "-imports")
            imported_names = re.findall(r"DLL Name: (\S+)", imports.decode("utf-8"))
            if any(any(runtime in item.lower() for runtime in ("msys", "cygwin", "libc++", "libunwind", "x265"))
                   for item in imported_names):
                raise RuntimeError("Unexpected build-runtime dependency in " + path.name)
            libraries.append({"file": path.name, "sha256": sha256(path), "size": path.stat().st_size,
                              "license": actual_license, "configuration": actual_configuration, "imports": imported_names})

    hardware = hardware_probes(ffmpeg, ffprobe, environment, options.hardware_source.resolve(), options.hardware_vendor_id) if options.hardware_source else None
    width, height, count = 64, 48, 12
    rate = Fraction(120000, 1001)
    rgba = bytearray()
    rgb = bytearray()
    packed_rgb = bytearray()
    for frame in range(count):
        for y in range(height):
            row, mask = bytearray(), bytearray()
            for x in range(width):
                values = ((x * 7 + frame * 13) % 256, (y * 11 + frame * 17) % 256,
                          ((x + y) * 5 + frame * 19) % 256, (x * 3 + y * 7 + frame * 23) % 256)
                rgba.extend(values)
                row.extend(values[:3])
                mask.extend((values[3],) * 3)
            rgb.extend(row)
            packed_rgb.extend(row + mask)
    (CHECKS / "source.rgba").write_bytes(rgba)
    color_filter = "scale=in_range=full:out_range=limited:out_color_matrix=bt709,format=yuv420p,setparams=range=limited:color_primaries=bt709:color_trc=bt709:colorspace=bt709"
    tests = []
    for label, lossless, packed in [("h264-compat", False, False), ("h264-rgb", True, False),
                                    ("packed-rgb", True, True), ("packed-compat", False, True),
                                    ("hevc-compat", False, False), ("hevc-packed-compat", False, True)]:
        codec = "hevc" if label.startswith("hevc") else "h264"
        output = CHECKS / (label + ".mp4")
        arguments = [ffmpeg, "-hide_banner", "-nostdin", "-y", "-f", "rawvideo", "-pixel_format", "rgba",
                     "-video_size", f"{width}x{height}", "-framerate", str(rate), "-i", "pipe:0", "-an"]
        final_filter = "format=rgb24" if lossless else color_filter
        if packed:
            arguments += ["-filter_complex", "[0:v]split=2[color][mask];[color]format=rgb24[rgb];[mask]alphaextract,format=rgb24[alpha];[rgb][alpha]hstack=inputs=2," + final_filter + "[packed]", "-map", "[packed]"]
        else:
            arguments += ["-vf", final_filter]
        if codec == "hevc":
            arguments += ["-tag:v", "hvc1", "-x265-params", "pools=2:frame-threads=2"]
        arguments += ["-c:v", "libx265" if codec == "hevc" else "libx264rgb" if lossless else "libx264", "-preset", "fast", "-crf", "0" if lossless else "16",
                      "-pix_fmt", "rgb24" if lossless else "yuv420p", "-fps_mode", "passthrough", "-enc_time_base",
                      f"{rate.denominator}:{rate.numerator}", "-movflags", "+faststart", output]
        execute(arguments, label + "-encode", environment, bytes(rgba))
        probe, _ = execute([ffprobe, "-v", "error", "-count_frames", "-show_streams", "-of", "json", output], label + "-probe", environment)
        (CHECKS / (label + ".probe.json")).write_bytes(probe)
        stream = json.loads(probe)["streams"][0]
        if stream["codec_name"] != codec or stream["width"] != width * (2 if packed else 1) or stream["height"] != height:
            raise RuntimeError(label + ": unexpected codec or frame size")
        if int(stream["nb_read_frames"]) != count or Fraction(stream["avg_frame_rate"]) != rate:
            raise RuntimeError(label + ": incorrect frame count or fractional frame rate")
        maximum_error = None
        if lossless:
            decoded, _ = execute([ffmpeg, "-v", "error", "-i", output, "-c:v", "rawvideo", "-pix_fmt", "rgb24", "-f", "rawvideo", "pipe:1"], label + "-decode", environment)
            expected = packed_rgb if packed else rgb
            if decoded != expected:
                raise RuntimeError(label + ": RGB round trip is not byte-exact")
            maximum_error = 0
        else:
            if stream.get("pix_fmt") != "yuv420p" or any(stream.get(key) != value for key, value in
                    {"color_range": "tv", "color_space": "bt709", "color_transfer": "bt709", "color_primaries": "bt709"}.items()):
                raise RuntimeError(label + ": compatibility color format/tags are incorrect")
        if codec == "hevc":
            bitstream_id = re.search(rb"x265 \(build [\x20-\x7e]+", output.read_bytes())
            if not bitstream_id or b"4.2+1-e444744" not in bitstream_id[0]:
                raise RuntimeError(label + ": bitstream does not identify the pinned x265 version")
            if stream.get("codec_tag_string") != "hvc1" or stream.get("profile") != "Main":
                raise RuntimeError(label + ": unexpected HEVC sample entry/profile")
            decoded, _ = execute([ffmpeg, "-v", "error", "-i", output, "-c:v", "rawvideo", "-pix_fmt", "yuv420p",
                                  "-f", "rawvideo", "pipe:1"], label + "-decode", environment)
            if len(decoded) != stream["width"] * height * count * 3 // 2:
                raise RuntimeError(label + ": HEVC decode did not return every frame")
        else:
            bitstream_id = re.search(rb"x264 - core [^\x00]+", output.read_bytes())
            if not bitstream_id or b"b35605a" not in bitstream_id[0]:
                raise RuntimeError(label + ": bitstream does not identify the pinned x264 revision")
        tests.append({"case": label, "frames": count, "fps": str(rate), "width": stream["width"], "height": stream["height"],
                      "pixel_format": stream["pix_fmt"], "maximum_rgb_error": maximum_error,
                      "codec": codec, "bitstream_id": bitstream_id[0].decode("ascii"), "sha256": sha256(output)})
        print("PASS", label, "frames=12 fps=" + str(rate), "exact RGB" if lossless else "BT.709 yuv420p", flush=True)

    samples = round(Fraction(count, 1) / rate * 48000)
    pcm = b"".join(struct.pack("<ff", 0.15 * math.sin(2 * math.pi * 997 * index / 48000),
                              0.1 * math.sin(2 * math.pi * 601 * index / 48000)) for index in range(samples))
    pcm_path = CHECKS / "audio.f32le"
    pcm_path.write_bytes(pcm)
    muxed = CHECKS / "h264-aac.mp4"
    execute([ffmpeg, "-hide_banner", "-nostdin", "-y", "-i", CHECKS / "h264-compat.mp4", "-f", "f32le", "-ar", "48000", "-ac", "2", "-i", pcm_path,
             "-map", "0:v:0", "-map", "1:a:0", "-c:v", "copy", "-c:a", "aac", "-b:a", "192k", "-movflags", "+faststart", muxed], "audio-mux", environment)
    probe, _ = execute([ffprobe, "-v", "error", "-show_streams", "-of", "json", muxed], "audio-probe", environment)
    (CHECKS / "audio.probe.json").write_bytes(probe)
    streams = json.loads(probe)["streams"]
    audio_stream = next(item for item in streams if item["codec_type"] == "audio")
    if audio_stream["codec_name"] != "aac" or audio_stream["sample_rate"] != "48000" or audio_stream["channels"] != 2:
        raise RuntimeError("Incorrect AAC audio stream")
    decoded_pcm, _ = execute([ffmpeg, "-v", "error", "-i", muxed, "-map", "0:a", "-c:a", "pcm_f32le", "-f", "f32le", "pipe:1"], "audio-decode", environment)
    if len(decoded_pcm) % 8 or not samples <= len(decoded_pcm) // 8 <= samples + 2048:
        raise RuntimeError("Unexpected AAC decoded sample count")
    values = struct.unpack("<" + "f" * (len(decoded_pcm) // 4), decoded_pcm)
    rms = math.sqrt(sum(value * value for value in values) / len(values))
    if not 0.05 < rms < 0.2:
        raise RuntimeError("AAC round trip is silent or has invalid amplitude")
    tests.append({"case": "h264-aac-mux", "input_audio_samples": samples, "decoded_audio_samples": len(decoded_pcm) // 8,
                  "decoded_rms": rms, "codec": "aac", "sample_rate": 48000, "channels": 2, "sha256": sha256(muxed)})
    print("PASS H.264 stream copy + AAC stereo mux and decode", flush=True)

    lock_path = ROOT / "scripts/encoder-inputs.lock.json"
    inputs = json.loads(lock_path.read_text(encoding="utf-8"))
    integrity = {name: source_matches_archive(DEST / "sources" / name, ROOT / inputs[name]["archive"]) for name in ("ffmpeg", "x264", "x265")}
    for name, record in integrity.items():
        if record["archive_sha256"] != inputs[name]["sha256"]:
            raise RuntimeError("Source archive hash differs from input lock: " + name)
    renderer_baseline = json.loads((ROOT / ".deps/ffmpeg-lgpl21/verification.json").read_text(encoding="utf-8"))
    for library in renderer_baseline["libraries"]:
        if sha256(ROOT / library["file"]) != library["sha256"]:
            raise RuntimeError("Renderer LGPL library differs from its verified baseline: " + library["file"])
    licenses = []
    for source, relative in [(DEST / "sources/ffmpeg/LICENSE.md", "ffmpeg/LICENSE.md"),
                             (DEST / "sources/ffmpeg/COPYING.GPLv2", "ffmpeg/COPYING.GPLv2"),
                             (DEST / "sources/ffmpeg/COPYING.LGPLv2.1", "ffmpeg/COPYING.LGPLv2.1"),
                             (DEST / "sources/x264/COPYING", "x264/COPYING"),
                             (DEST / "sources/x264/x264.h", "x264/x264.h"),
                             (DEST / "sources/x265/COPYING", "x265/COPYING"),
                             (DEST / "sources/x265/source/x265.h", "x265/x265.h"),
                             (ROOT / ".tools/llvm-mingw-22/LICENSE.TXT", "llvm/LICENSE.TXT"),
                             (ROOT / ".tools/llvm-mingw-22/x86_64-w64-mingw32/share/mingw32/COPYING.MinGW-w64-runtime.txt",
                              "mingw/COPYING.MinGW-w64-runtime.txt")]:
        target = RUNTIME / "licenses" / relative
        target.parent.mkdir(parents=True, exist_ok=True)
        shutil.copyfile(source, target)
        licenses.append({"file": target.relative_to(RUNTIME).as_posix(), "sha256": sha256(target)})
    build_tools = []
    for path in [ROOT / ".tools/llvm-mingw-22/bin/clang.exe", ROOT / ".tools/llvm-mingw-22/bin/ld.lld.exe",
                 ROOT / ".tools/nasm/nasm.exe", ROOT / ".tools/ffmpeg-build/usr/bin/make.exe",
                 ROOT / ".tools/cmake/bin/cmake.exe", ROOT / ".tools/ninja/ninja.exe",
                 ROOT / ".tools/llvm-mingw-22/x86_64-w64-mingw32/lib/libc++.a",
                 ROOT / ".tools/llvm-mingw-22/x86_64-w64-mingw32/lib/libunwind.a",
                 ROOT / ".tools/pkgconf/bin/pkgconf.exe", pathlib.Path(r"C:\Program Files\Git\bin\bash.exe"),
                 pathlib.Path(r"C:\Program Files\Git\usr\bin\msys-2.0.dll")]:
        build_tools.append({"file": str(path), "sha256": sha256(path)})
    record = {"schema_version": 1, "verified_utc": datetime.datetime.now(datetime.timezone.utc).isoformat(),
              "inputs": inputs, "input_lock_sha256": sha256(lock_path), "source_integrity": integrity,
              "configuration": json.loads((DEST / "build-configuration.json").read_text(encoding="utf-8")),
              "x264_config": (DEST / "build/x264/x264_config.h").read_text(encoding="utf-8"),
              "x265_config": (DEST / "build/x265-amd64/x265_config.h").read_text(encoding="utf-8"),
              "build_tools": build_tools, "libraries": libraries, "licenses": licenses, "tests": tests, "encoders": encoders,
              "hardware_probes": hardware,
              "runtime_files": [{"file": path.name, "sha256": sha256(path), "size": path.stat().st_size}
                                for path in sorted(RUNTIME.iterdir()) if path.is_file()],
              "runtime_path": environment["PATH"], "renderer_library_baseline_verified": True}
    (DEST / "verification.json").write_text(json.dumps(record, indent=2) + "\n", encoding="utf-8")
    portable = DEST / "portable"
    if portable.exists():
        backup = DEST / "backups" / ("portable-" + datetime.datetime.now().strftime("%Y%m%d-%H%M%S-%f"))
        shutil.copytree(portable, backup)
    shutil.copytree(RUNTIME, portable, dirs_exist_ok=True)
    for item in record["runtime_files"]:
        if sha256(portable / item["file"]) != item["sha256"]:
            raise RuntimeError("Promoted runtime differs from verified candidate: " + item["file"])
    print("Verified portable GPL 2 H.264/HEVC encoder:", portable)


if __name__ == "__main__":
    main()
