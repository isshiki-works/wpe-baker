"""Create original, redistributable WPE fixtures for the real renderer tests."""
from __future__ import annotations

import json
import math
from pathlib import Path
import struct
import wave

ROOT = Path(__file__).resolve().parents[1]
FIXTURES = ROOT / "tests" / "fixtures" / "native"

VERTEX = """// Original WpeBaker test shader, MIT.
attribute vec3 a_Position;
attribute vec2 a_TexCoord;
uniform mat4 g_ModelViewProjectionMatrix;
varying vec2 v_TexCoord;
void main() {
    v_TexCoord = a_TexCoord;
    gl_Position = mul(g_ModelViewProjectionMatrix, vec4(a_Position, 1.0));
}
"""

FRAGMENT = """// Original WpeBaker test shader, MIT.
uniform float g_Time;
uniform sampler2D g_Texture0;
varying vec2 v_TexCoord;
void main() {
    vec2 p = v_TexCoord;
    vec3 c = p.y < 0.5
        ? (p.x < 0.5 ? vec3(1.0,0.0,0.0) : vec3(0.0,1.0,0.0))
        : (p.x < 0.5 ? vec3(0.0,0.0,1.0) : vec3(1.0,1.0,0.0));
    float phase = fract(g_Time);
    if (abs(p.x - (0.2 + phase * 0.6)) < 0.03 && abs(p.y - 0.65) < 0.09)
        c = vec3(1.0);
    // A continuous color ramp complements the spatial marker at high FPS.
    if (p.y > 0.90) c = vec3(phase, 0.25, 1.0 - phase);
    gl_FragColor = vec4(c, 1.0) * texSample2D(g_Texture0, p);
}
"""


def write_json(path: Path, value: object) -> None:
    path.parent.mkdir(parents=True, exist_ok=True)
    path.write_text(json.dumps(value, indent=2, ensure_ascii=False) + "\n", encoding="utf-8")


def write_tex(path: Path, rgba: bytes, width: int, height: int) -> None:
    # TEXB0001: uncompressed RGBA8, one image and one mip level.
    assert len(rgba) == width * height * 4
    path.parent.mkdir(parents=True, exist_ok=True)
    header = b"TEXV0005\0TEXI0001\0"
    header += struct.pack("<7I", 0, 2, width, height, width, height, 0)
    header += b"TEXB0001\0" + struct.pack("<5I", 1, 1, width, height, len(rgba))
    path.write_bytes(header + rgba)


def create(name: str, *, animated: bool, script: str = "") -> None:
    root = FIXTURES / name
    width, height = 256, 128
    general = {
        "clearcolor": "0.125 0.25 0.5", "clearenabled": True,
        "orthogonalprojection": {"width": width, "height": height},
        "cameraparallax": False, "camerashake": False, "bloom": False,
        "ambientcolor": "1 1 1", "skylightcolor": "1 1 1",
    }
    obj = {
        "id": 1, "name": "asymmetric-clock-probe", "image": "models/probe.json",
        "origin": "128 64 0", "angles": "0 0 0", "scale": "1 1 1",
        "size": "256 128", "visible": True, "alpha": 1.0,
    }
    if script:
        # Tests the actual QuickJS runtime clock and seeded RNG, not Node mocks.
        obj["alpha"] = {
            "value": 1.0,
            "script": script,
        }
    write_json(root / "scene.json", {
        "camera": {"center": "0 0 0", "eye": "0 0 1", "up": "0 1 0"},
        "general": general, "objects": [obj],
    })
    write_json(root / "project.json", {
        "title": "WpeBaker native " + name + " fixture", "type": "scene",
        "file": "scene.json", "description": "Original synthetic fixture; MIT license.",
    })
    write_json(root / "models" / "probe.json", {
        "material": "materials/probe.json", "width": width, "height": height,
        "autosize": False,
    })
    write_json(root / "materials" / "probe.json", {"passes": [{
        "shader": "probe", "textures": ["probe-white"], "blending": "normal",
        "cullmode": "nocull", "depthtest": "disabled", "depthwrite": "disabled",
    }]})
    write_tex(root / "materials" / "probe-white.tex", b"\xff\xff\xff\xff", 1, 1)
    (root / "shaders").mkdir(exist_ok=True)
    (root / "shaders" / "probe.vert").write_text(VERTEX, encoding="utf-8")
    fragment = FRAGMENT if animated else FRAGMENT.replace("fract(g_Time)", "0.0")
    (root / "shaders" / "probe.frag").write_text(fragment, encoding="utf-8")
    # Per-layer alpha is applied by this shader for the script-clock fixture.
    if script:
        fragment = fragment.replace("uniform float g_Time;", "uniform float g_Time;\nuniform float g_Alpha;")
        fragment = fragment.replace("vec4(c, 1.0) *", "vec4(c * g_Alpha, 1.0) *")
        (root / "shaders" / "probe.frag").write_text(fragment, encoding="utf-8")


if __name__ == "__main__":
    create("orientation", animated=False)
    create("shader-clock", animated=True)
    create("date-clock", animated=False,
           script="export function update(value) { return (Date.now() % 1000) < 500 ? 1.0 : 0.25; }")
    create("random-clock", animated=False,
           script="export function update(value) { return 0.2 + 0.6 * Math.random(); }")
    create("authored-audio", animated=False)
    audio_root = FIXTURES / "authored-audio"
    (audio_root / "sounds").mkdir(exist_ok=True)
    # The non-frame-aligned length deliberately exercises decoder loop boundaries.
    frames = 12345
    samples = b"".join(struct.pack("<hh", round(0.4 * 32767 * math.sin(2 * math.pi * 337 * i / 48000)),
                                   round(0.25 * 32767 * math.cos(2 * math.pi * 541 * i / 48000)))
                       for i in range(frames))
    with wave.open(str(audio_root / "sounds" / "probe.wav"), "wb") as sound:
        sound.setnchannels(2)
        sound.setsampwidth(2)
        sound.setframerate(48000)
        sound.writeframes(samples)
    scene = json.loads((audio_root / "scene.json").read_text(encoding="utf-8"))
    scene["objects"].append({"id": 2, "name": "authored-audio-probe", "sound": ["sounds/probe.wav"],
                             "volume": 1.0, "playbackmode": "loop", "visible": True, "startsilent": False,
                             "origin": "0 0 0", "angles": "0 0 0", "scale": "1 1 1"})
    write_json(audio_root / "scene.json", scene)
    print(json.dumps({"fixtures": str(FIXTURES), "count": 5}))
