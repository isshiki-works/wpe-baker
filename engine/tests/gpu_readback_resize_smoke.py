"""Real GPU readback check: original references and alpha hidden by downsizing.

python engine/tests/gpu_readback_resize_smoke.py --renderer build/native-main/bin/wpe-render.exe \
    --assets PATH_TO_WALLPAPER_ENGINE_ASSETS --output-dir NEW_EVIDENCE_DIRECTORY
The renderer and its runtime DLLs must be available through the supplied path/PATH.
"""
import argparse
import json
import pathlib
import shutil
import subprocess


def main():
    args = argparse.ArgumentParser(description=__doc__)
    args.add_argument("--renderer", required=True, type=pathlib.Path)
    args.add_argument("--assets", required=True, type=pathlib.Path)
    args.add_argument("--output-dir", required=True, type=pathlib.Path)
    args = args.parse_args()
    root = pathlib.Path(__file__).resolve().parents[2]
    output = args.output_dir.resolve()
    output.mkdir(parents=True, exist_ok=False)
    source = root / "tests/fixtures/native/orientation/scene.json"

    def render(name, **options):
        job = dict(schema_version=1, source=str(source), assets=str(args.assets.resolve()),
                   output_dir=str(output / name), width=256, height=128, fps_num=60,
                   fps_den=1, frames=3, warmup_frames=1, seed=17, raw_stdout=True, write_audio=False)
        job.update(options)
        request = output / (name + "-job.json")
        request.write_text(json.dumps(job), encoding="utf-8")
        process = subprocess.run([str(args.renderer.resolve()), "render", "--job", str(request)],
                                 capture_output=True, timeout=60,
                                 creationflags=getattr(subprocess, "CREATE_NO_WINDOW", 0))
        (output / (name + "-stderr.txt")).write_bytes(process.stderr)
        (output / (name + "-stdout.rgba")).write_bytes(process.stdout)
        assert process.returncode == 0, process.stderr.decode("utf-8", errors="replace")
        result = json.loads((output / name / "result.json").read_text(encoding="utf-8"))
        assert result["status"] == "complete" and result["written_frames"] == 3
        return process.stdout, result

    full, _ = render("original")
    resize = dict(output_resize_width=32, output_resize_height=16, encoded_frames=2,
                  retain_frames=[0, 2], require_opaque_pixels=True)
    reduced, result = render("resized", **resize)
    assert len(full) == 256 * 128 * 4 * 3 and len(reduced) == 32 * 16 * 4 * 3
    retained = (output / "resized/retained-frames.rgba").read_bytes()
    frame_bytes = 256 * 128 * 4
    assert retained == full[:frame_bytes] + full[2 * frame_bytes:]
    opaque = result["gpu_capture"]["opaque_pixels"]
    assert opaque["verified"] and opaque["checked_pixels"] == 256 * 128 * 3
    assert result["gpu_capture"]["retained_frames"]["frame_indices"] == [0, 2]

    transparent = output / "one-pixel-alpha"
    shutil.copytree(source.parent, transparent)
    shader = transparent / "shaders/probe.frag"
    shader.write_text(shader.read_text(encoding="utf-8").replace(
        "texSample2D(g_Texture0, p);", "texSample2D(g_Texture0, p);\n"
        "    if (p.x < 0.005 && p.y < 0.01) gl_FragColor.a = 254.0 / 255.0;"), encoding="utf-8")
    reduced, result = render("transparent", source=str(transparent / "scene.json"),
                             layer_selection=dict(include_layers=[1], transparent_background=True,
                                                  include_postprocessing=False), **resize)
    opaque = result["gpu_capture"]["opaque_pixels"]
    assert not opaque["verified"] and opaque["minimum_alpha"] == 254
    assert opaque["checked_frames"] == 3 and opaque["first_nonopaque_frame"] == 0
    bad = (output / "transparent" / opaque["first_nonopaque_frame_rgba_path"]).read_bytes()
    assert len(bad) == frame_bytes and sum(alpha != 255 for alpha in bad[3::4]) == 1
    assert min(reduced[3::4]) == 255, "Negative fixture must hide source alpha after resize"
    print("Passed: resized stdout, full-size identical references, every-frame source alpha, hidden-alpha failure.")


if __name__ == "__main__":
    main()
