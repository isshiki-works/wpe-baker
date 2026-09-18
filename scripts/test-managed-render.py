"""Exercise the compiled C# CLI, C++ renderer, FFmpeg and ffprobe together."""
from __future__ import annotations
import hashlib
import json
import os
from pathlib import Path
import subprocess
import time

ROOT = Path(__file__).resolve().parent.parent

def main() -> None:
    if not __debug__:
        raise RuntimeError("Tests require Python assertions enabled")
    output = ROOT / "artifacts" / f"managed-integration-{time.time_ns()}"
    output.mkdir(parents=True)
    ffdir = ROOT / ".deps/ffmpeg-encoder-gpl2/portable"
    tools = {"renderer": str(ROOT / "build/native-release22/bin/wpe-render.exe"),
             "ffmpeg": str(ffdir / "ffmpeg.exe"), "ffprobe": str(ffdir / "ffprobe.exe"),
             "runtime_directories": [str(ROOT / ".tools/llvm-mingw-22/bin"), str(ROOT / ".deps/ffmpeg-lgpl21/prefix/bin")]}
    tools_path = output / "tools.json"
    tools_path.write_text(json.dumps(tools), encoding="utf-8")
    request = {"source": str(ROOT / "tests/fixtures/native/authored-audio"),
               "assets": "D:/Apps/Steam/steamapps/common/wallpaper_engine/assets",
               "output_directory": str(output / "中文成品"), "width": 128, "height": 96,
               "fps_numerator": 120000, "fps_denominator": 1001, "frames": 120,
               "warmup_frames": 37, "seed": 7, "include_audio": True}
    request_path = output / "request.json"
    request_path.write_text(json.dumps(request), encoding="utf-8")
    cmd = [str(ROOT / ".dotnet/dotnet.exe"), str(ROOT / "src/Baker.Cli/bin/Release/net10.0/wpe-baker.dll"),
           "render", str(request_path), "--tools", str(tools_path)]
    flags = getattr(subprocess, "CREATE_NO_WINDOW", 0)
    result = subprocess.run(cmd, cwd=ROOT, capture_output=True, timeout=180, creationflags=flags)
    (output / "cli.stdout.json").write_bytes(result.stdout)
    (output / "cli.stderr.log").write_bytes(result.stderr)
    if result.returncode:
        raise RuntimeError(f"C# CLI exited {result.returncode}: {result.stderr.decode('utf-8', errors='replace')}\nArtifacts: {output}")
    manifest = json.loads(result.stdout)
    assert manifest["status"] == "completed"
    assert manifest["optimization_validated"] is False
    assert manifest["renderer_sha256"] == hashlib.sha256(Path(tools["renderer"]).read_bytes()).hexdigest()
    native = manifest["native_result"]
    assert native["written_frames"] == 120 and native["renderer_error_count"] == 0
    expected_audio = ((37 + 120) * 1001 * 48000 // 120000) - (37 * 1001 * 48000 // 120000)
    assert native["audio_sample_frames"] == expected_audio
    encoded = manifest["encoded_stream"]["streams"][0]
    assert encoded["nb_read_frames"] == "120" and encoded["avg_frame_rate"] == "120000/1001"
    assert encoded["time_base"] == "1/120000" and encoded["duration_ts"] == 120 * 1001
    video = Path(request["output_directory"]) / "preview.mp4"
    probe = subprocess.run([tools["ffprobe"], "-v", "error", "-show_streams", "-of", "json", str(video)],
                           capture_output=True, timeout=30, creationflags=flags, check=True)
    streams = json.loads(probe.stdout)["streams"]
    assert any(s["codec_type"] == "audio" and s["sample_rate"] == "48000" and s["channels"] == 2 for s in streams)
    report = {"status": "passed", "root": str(output), "native_frames": 120,
              "fps": "120000/1001", "warmup_frames": 37, "pcm_sample_frames": expected_audio,
              "video_sha256": hashlib.sha256(video.read_bytes()).hexdigest(), "manifest": manifest}
    (output / "report.json").write_text(json.dumps(report, indent=2), encoding="utf-8")
    print(json.dumps({k: v for k, v in report.items() if k != "manifest"}, indent=2))

if __name__ == "__main__":
    main()
