"""Exercise the public generic analysis/bake/compare path on any local Scene source."""
import argparse
import json
import os
from pathlib import Path
import subprocess
import time

ROOT = Path(__file__).resolve().parent.parent
ap = argparse.ArgumentParser()
ap.add_argument('source')
ap.add_argument('--interaction', choices=['keep', 'fixed', 'off'], default='keep')
ap.add_argument('--seconds', type=int, default=2)
ap.add_argument('--width', type=int, default=960)
ap.add_argument('--height', type=int, default=540)
ap.add_argument('--retain-live', default=None)
ap.add_argument('--device', default=None, help='Exact Vulkan device UUID from the CLI devices command.')
args = ap.parse_args()
out = ROOT / 'artifacts' / f'hybrid-probe-{time.time_ns()}'
out.mkdir()
env = os.environ.copy()
env['DOTNET_ROOT'] = str(ROOT / '.dotnet')
env['PATH'] = str(ROOT / '.dotnet') + ';' + env.get('PATH', '')
cli = ROOT / 'src/Baker.Cli/bin/Release/net10.0/wpe-baker.exe'
assets = 'D:/Apps/Steam/steamapps/common/wallpaper_engine/assets'
tools = out / 'tools.json'
tools.write_text(json.dumps({
    'renderer': str(ROOT / 'build/native-release22/bin/wpe-render.exe'),
    'ffmpeg': str(ROOT / '.deps/ffmpeg-encoder-gpl2/portable/ffmpeg.exe'),
    'ffprobe': str(ROOT / '.deps/ffmpeg-encoder-gpl2/portable/ffprobe.exe'),
    'runtime_directories': [str(ROOT / '.tools/llvm-mingw-22/bin'), str(ROOT / '.deps/ffmpeg-lgpl21/prefix/bin')]
}), encoding='utf-8')

def run(name, command):
    started = time.perf_counter()
    proc = subprocess.run([str(cli), *command], env=env, capture_output=True,
                          creationflags=0x08000000, timeout=1200)
    (out / (name + '.stdout.json')).write_bytes(proc.stdout)
    (out / (name + '.stderr.log')).write_bytes(proc.stderr)
    if proc.returncode:
        raise RuntimeError(name + ': ' + proc.stderr.decode('utf-8', 'replace')[-2500:])
    return json.loads(proc.stdout), time.perf_counter() - started

print(out, flush=True)
plan, elapsed = run('analyze', ['analyze', args.source, '--assets', assets, '--tools', str(tools),
                               '--out', str(out/'plan.json'), '--width', str(args.width), '--height', str(args.height),
                               '--fps', '120', '--interaction', args.interaction] +
                               (['--device', args.device] if args.device else []) +
                               (['--retain-live', args.retain_live] if args.retain_live else []))
print(json.dumps({'groups': [dict(id=g['id'], layers=len(g['layer_ids'])) for g in plan['video_groups']],
                  'live': len(plan['live_layer_ids']), 'blockers': plan['blockers'], 'analysis_seconds': elapsed,
                  'loop_candidates': [c['seconds'] for c in plan['loop'].get('candidates', [])]}, ensure_ascii=False), flush=True)
request = out / 'bake-request.json'
request.write_text(json.dumps({'schema_version': 2, 'plan': plan, 'output_directory': str(out/'bake'),
                               'probe_frames': args.seconds * 120}, ensure_ascii=False), encoding='utf-8')
bake, elapsed = run('bake', ['bake', str(request), '--tools', str(tools)])
print(json.dumps({'bake_seconds': elapsed, 'project': bake['project_path'], 'video_layers': bake['video_layers']}, ensure_ascii=False), flush=True)
reference = out / 'bake/capture-source'
validation = {'schema_version': 1, 'source': str(reference), 'candidate': bake['project_path'], 'assets': assets,
              'output_directory': str(out/'comparison'), 'width': args.width, 'height': args.height,
              'fps_numerator': 120, 'fps_denominator': 1, 'frames': 48, 'warmup_frames': 0, 'seed': 17,
              'user_properties': plan['snapshot_properties'], 'tile_size': 64}
if args.device:
    validation['device_uuid'] = args.device
(out/'compare-request.json').write_text(json.dumps(validation, ensure_ascii=False), encoding='utf-8')
comparison, _ = run('compare', ['validate', str(out/'compare-request.json'), '--tools', str(tools)])
print(json.dumps({'metrics': comparison['metrics'], 'passes': [comparison['source_compiled_scene_passes'], comparison['candidate_compiled_scene_passes']]}, ensure_ascii=False), flush=True)
(out/'report.json').write_text(json.dumps({'status': 'probe_compared', 'source': args.source,
    'project': bake['project_path'], 'bake_seconds': elapsed, 'plan': str(out/'plan.json'),
    'comparison': str(out/'comparison/comparison.json'), 'metrics': comparison['metrics'],
    'passes': [comparison['source_compiled_scene_passes'], comparison['candidate_compiled_scene_passes']],
    'loop_validated': False, 'official_playback': False}, ensure_ascii=False, indent=2), encoding='utf-8')
