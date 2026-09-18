"""Run the actual scene parser, Vulkan readback, clocks and FFmpeg encoder.

This development harness never captures a monitor, opens a window, applies a
wallpaper or changes system settings. It records failures without deleting output.
"""
from __future__ import annotations

import argparse
import array
import hashlib
import json
from pathlib import Path
import os
import shutil
import struct
import subprocess
import time
import wave
import zlib

ROOT = Path(__file__).resolve().parents[1]
CREATE_FLAGS = subprocess.CREATE_NO_WINDOW if os.name == "nt" else 0
ENV = os.environ.copy()
ENV["PATH"] = os.pathsep.join(str(ROOT / path) for path in (
    ".tools/llvm-mingw/bin", ".deps/ffmpeg/bin", ".deps/install/bin",
)) + os.pathsep + ENV.get("PATH", "")
ENV["TEMP"] = ENV["TMP"] = str(ROOT / ".tools" / "tmp")


def run(command: list[str], cwd: Path, log: Path, timeout: int = 180) -> None:
    with log.open("wb") as output:
        completed = subprocess.run(command, cwd=cwd, stdout=output, stderr=subprocess.STDOUT,
                                   timeout=timeout, creationflags=CREATE_FLAGS, env=ENV)
    if completed.returncode:
        raise RuntimeError(f"exit {completed.returncode}: {command[0]}; log {log}")


def png(path: Path, pixels: bytes, width: int, height: int) -> None:
    def chunk(tag: bytes, body: bytes) -> bytes:
        return struct.pack(">I", len(body)) + tag + body + struct.pack(">I", zlib.crc32(tag + body))
    rows = b"".join(b"\0" + pixels[y * width * 4:(y + 1) * width * 4] for y in range(height))
    path.write_bytes(b"\x89PNG\r\n\x1a\n" + chunk(b"IHDR", struct.pack(">2I5B", width, height, 8, 6, 0, 0, 0))
                     + chunk(b"IDAT", zlib.compress(rows)) + chunk(b"IEND", b""))


def render(renderer: Path, folder: Path, fixture: str, fps: int, frames: int, label: str,
           *, fps_den: int = 1, warmup: int = 0, seed: int = 123456,
           epoch_ms: int = 946684800000, source_override: Path | None = None) -> dict:
    source = source_override or ROOT / "tests" / "fixtures" / "native" / fixture
    output = folder / label
    job = {
        "schema_version": 1, "source": str(source / "scene.json"), "assets": str(source),
        "output_dir": str(output), "width": 256, "height": 128,
        "fps_num": fps, "fps_den": fps_den, "frames": frames, "warmup_frames": warmup, "seed": seed,
        "epoch_ms": epoch_ms, "raw_stdout": False,
    }
    jobfile = folder / (label + ".job.json")
    jobfile.write_text(json.dumps(job, indent=2), encoding="utf-8")
    run([str(renderer), "render", "--job", str(jobfile)], ROOT, folder / (label + ".log"))
    result = json.loads((output / "result.json").read_text(encoding="utf-8"))
    assert result["status"] == "complete" and result["written_frames"] == frames, result
    raw = (output / "frames.rgba").read_bytes()
    assert len(raw) == frames * 256 * 128 * 4, "wrong raw byte count"
    entries = [json.loads(s) for s in (output / "frames.jsonl").read_text().splitlines()]
    assert len(entries) == frames
    assert all(e["frame"] == i and e["simulation_frame"] == i + warmup and
               e["pts_num"] == i * fps_den and e["pts_den"] == fps for i, e in enumerate(entries))
    samples = (frames + warmup) * 48000 * fps_den // fps - warmup * 48000 * fps_den // fps
    assert result["audio_sample_frames"] == samples, "audio sample clock drift"
    assert (output / "audio.f32le").stat().st_size == samples * 2 * 4, "wrong audio byte count"
    hashes = [hashlib.sha256(raw[i * 131072:(i + 1) * 131072]).hexdigest() for i in range(frames)]
    png(output / "frame0.png", raw[:131072], 256, 128)
    return {"path": str(output), "sha256": hashlib.sha256(raw).hexdigest(), "frames": frames,
            "frame_hashes": hashes, "wall_seconds": result["wall_seconds"]}


def check_phase(run_result: dict, fps_num: int, fps_den: int, indices: list[int], warmup: int = 0) -> None:
    raw = (Path(run_result["path"]) / "frames.rgba").read_bytes()
    for index in indices:
        row = raw[index * 131072 + 83 * 256 * 4:index * 131072 + 84 * 256 * 4]
        white = [x for x in range(256) if row[x * 4:x * 4 + 3] == b"\xff\xff\xff"]
        assert len(white) >= 4, ("moving marker missing", index)
        phase = ((index + warmup) * fps_den % fps_num) / fps_num
        expected_center = (0.2 + phase * 0.6) * 256
        actual_center = sum(x + 0.5 for x in white) / len(white)
        assert abs(actual_center - expected_center) <= 0.75, ("wrong animation phase", index, actual_center, expected_center)


def red_pixel(run_result: dict, frame: int) -> int:
    with (Path(run_result["path"]) / "frames.rgba").open("rb") as stream:
        stream.seek(frame * 131072 + (16 * 256 + 32) * 4)
        return stream.read(1)[0]


def main() -> None:
    if not __debug__:
        raise RuntimeError("validation must run with Python assertions enabled")
    (ROOT / ".tools/tmp").mkdir(parents=True, exist_ok=True)
    parser = argparse.ArgumentParser()
    parser.add_argument("--renderer", type=Path, required=True)
    parser.add_argument("--ffmpeg", type=Path, required=True)
    parser.add_argument("--ffprobe", type=Path, required=True)
    parser.add_argument("--runtime-bin", type=Path, help="DLL directory of the toolchain that built the renderer")
    parser.add_argument("--ffmpeg-dll-bin", type=Path, help="FFmpeg DLL directory used by the renderer")
    args = parser.parse_args()
    if args.ffmpeg_dll_bin:
        ENV["PATH"] = str(args.ffmpeg_dll_bin.resolve()) + os.pathsep + ENV["PATH"]
    if args.runtime_bin:
        ENV["PATH"] = str(args.runtime_bin.resolve()) + os.pathsep + ENV["PATH"]
    folder = ROOT / "artifacts" / ("native-test-" + str(time.time_ns()))
    folder.mkdir(parents=True)
    report = {"schema_version": 1, "status": "running", "renderer": str(args.renderer.resolve()),
              "renderer_sha256": hashlib.sha256(args.renderer.read_bytes()).hexdigest(), "tests": {}}
    try:
        orientation = render(args.renderer.resolve(), folder, "orientation", 120, 2, "orientation")
        raw = (Path(orientation["path"]) / "frames.rgba").read_bytes()
        for x, y, rgb in [(32, 16, (255, 0, 0)), (224, 16, (0, 255, 0)),
                          (32, 80, (0, 0, 255)), (224, 80, (255, 255, 0))]:
            pos = (y * 256 + x) * 4
            assert tuple(raw[pos:pos+3]) == rgb, ("orientation/channel mismatch", x, y, tuple(raw[pos:pos+4]))
        report["tests"]["orientation"] = orientation
        unicode_source = folder / "中文 场景"
        shutil.copytree(ROOT / "tests/fixtures/native/orientation", unicode_source)
        (unicode_source / "materials/颜色.tex").write_bytes((unicode_source / "materials/probe-white.tex").read_bytes())
        material_path = unicode_source / "materials/probe.json"
        material = json.loads(material_path.read_text(encoding="utf-8"))
        material["passes"][0]["textures"][0] = "颜色"
        material_path.write_text(json.dumps(material, ensure_ascii=False), encoding="utf-8")
        unicode_result = render(args.renderer.resolve(), folder, "orientation", 120, 2, "中文 输出", source_override=unicode_source)
        assert unicode_result["sha256"] == orientation["sha256"], "Unicode/space paths changed render output"
        report["tests"]["unicode-paths"] = unicode_result
        for fps in (60, 120, 144, 165, 240):
            first = render(args.renderer.resolve(), folder, "shader-clock", fps, fps + 1, f"clock-{fps}-a")
            second = render(args.renderer.resolve(), folder, "shader-clock", fps, fps + 1, f"clock-{fps}-b")
            assert first["sha256"] == second["sha256"], "repeated fixed-step export differs"
            raw_clock = (Path(first["path"]) / "frames.rgba").read_bytes()
            ramp = (123 * 256 + 200) * 4
            assert raw_clock[ramp] == 0 and raw_clock[ramp + 2] == 255, "frame zero is not scene time zero"
            assert first["frame_hashes"][0] == first["frame_hashes"][fps], "one-second phase did not close"
            assert len(set(first["frame_hashes"])) > fps // 2, "clock did not produce high-rate independent samples"
            check_phase(first, fps, 1, [0, fps // 7, fps // 3, fps // 2, fps - 1, fps])
            report["tests"][f"clock-{fps}"] = first
        report["tests"]["fractional-clock"] = render(args.renderer.resolve(), folder, "shader-clock",
                                                       120000, 120, "clock-120000-1001", fps_den=1001)
        check_phase(report["tests"]["fractional-clock"], 120000, 1001, [0, 17, 53, 119])
        report["tests"]["warmup-audio-clock"] = render(args.renderer.resolve(), folder, "shader-clock",
                                                         165, 91, "warmup-165", warmup=37)
        check_phase(report["tests"]["warmup-audio-clock"], 165, 1, [0, 23, 90], warmup=37)
        dates = render(args.renderer.resolve(), folder, "date-clock", 120, 121, "date")
        shifted = render(args.renderer.resolve(), folder, "date-clock", 120, 1, "date-shifted", epoch_ms=946684800500)
        assert all(red_pixel(dates, i) == 255 for i in (0, 30, 120)), "Date light phase is wrong"
        assert 0 < red_pixel(dates, 60) < 220 and red_pixel(shifted, 0) == red_pixel(dates, 60), "Date clock or epoch is ignored"
        report["tests"]["date"] = dates
        for label, seed in (("random-a", 123456), ("random-b", 123456), ("random-c", 987654)):
            report["tests"][label] = render(args.renderer.resolve(), folder, "random-clock", 120, 121, label, seed=seed)
        assert report["tests"]["random-a"]["sha256"] == report["tests"]["random-b"]["sha256"], "same seed is not reproducible"
        assert report["tests"]["random-a"]["sha256"] != report["tests"]["random-c"]["sha256"], "seed is ignored"
        assert len(set(report["tests"]["random-a"]["frame_hashes"])) > 10, "random script did not advance"
        soundtrack = render(args.renderer.resolve(), folder, "authored-audio", 120, 301, "authored-audio")
        with wave.open(str(ROOT / "tests/fixtures/native/authored-audio/sounds/probe.wav"), "rb") as wav:
            original = array.array("h", wav.readframes(wav.getnframes()))
        decoded = array.array("f", (Path(soundtrack["path"]) / "audio.f32le").read_bytes())
        error = max(abs(value - original[i % len(original)] / 32768.0) for i, value in enumerate(decoded))
        assert error < 5e-6, ("authored soundtrack mismatch/loop gap", error)
        report["tests"]["authored-audio"] = {**soundtrack, "max_pcm_error": error}
        video = folder / "clock-120.mp4"
        run([str(args.ffmpeg.resolve()), "-hide_banner", "-v", "error", "-f", "rawvideo", "-pixel_format", "rgba",
             "-video_size", "256x128", "-framerate", "120", "-i", str(folder / "clock-120-a" / "frames.rgba"),
             "-frames:v", "120", "-an", "-c:v", "libx264", "-preset", "fast", "-crf", "18", "-pix_fmt", "yuv420p", str(video)],
            ROOT, folder / "encode.log")
        probe = subprocess.run([str(args.ffprobe.resolve()), "-v", "error", "-count_frames", "-select_streams", "v:0",
                                "-show_entries", "stream=width,height,r_frame_rate,nb_read_frames,duration", "-of", "json", str(video)],
                               check=True, capture_output=True, creationflags=CREATE_FLAGS, env=ENV)
        metadata = json.loads(probe.stdout)["streams"][0]
        assert metadata["r_frame_rate"] == "120/1" and metadata["nb_read_frames"] == "120", metadata
        assert abs(float(metadata["duration"]) - 1.0) < 1e-5, metadata
        report["tests"]["encoded_120fps"] = metadata
        report["status"] = "passed"
    except Exception as error:
        report["status"] = "failed"
        report["error"] = repr(error)
        raise
    finally:
        (folder / "report.json").write_text(json.dumps(report, indent=2), encoding="utf-8")
        print(json.dumps({"report": str(folder / "report.json"), "status": report["status"]}))


if __name__ == "__main__":
    main()
