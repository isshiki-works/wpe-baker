"""Check removed entry points reject input before creating output or starting tools."""
import json
import os
from pathlib import Path
import subprocess
import tempfile

ROOT = Path(__file__).resolve().parents[1]
CLI = [str(ROOT / '.dotnet/dotnet.exe'), str(ROOT / 'src/Baker.Cli/bin/Release/net10.0/wpe-baker.dll')]
ENV = {**os.environ, 'DOTNET_ROOT': str(ROOT / '.dotnet')}

def invoke(*args):
    return subprocess.run([*CLI, *map(str, args)], cwd=ROOT, env=ENV, capture_output=True,
                          timeout=30, creationflags=subprocess.CREATE_NO_WINDOW)

def report(result):
    """stderr 先是一段 JSON，analyze 之后还会跟一行按 --lang 出的人话结论；只解析前面那段 JSON。"""
    return json.JSONDecoder().raw_decode(result.stderr.decode('utf-8'))[0]

def verdict(result):
    """JSON 之后的那一行人话结论。"""
    text = result.stderr.decode('utf-8')
    return text[json.JSONDecoder().raw_decode(text)[1]:].strip()

with tempfile.TemporaryDirectory(prefix='wpe-scene-cli-') as temporary:
    folder = Path(temporary)
    checks = []
    for kind, entry in [('video', 'video.mp4'), ('web', 'index.html')]:
        source = folder / kind
        source.mkdir()
        descriptor = json.dumps({'type': kind, 'file': entry}).encode()
        (source / 'project.json').write_bytes(descriptor)
        (source / entry).write_bytes(b'unchanged')
        output = folder / (kind + '-plan.json')
        result = invoke('analyze', source, '--out', output)
        message = report(result)['message']
        # 文案必须点名壁纸类型和内容文件，而不是只说"仅限 Scene 项目"。
        assert result.returncode == 1 and 'bakes Scene wallpapers only' in message
        assert f'type={kind}' in message and entry in message
        assert not output.exists() and not Path(str(output) + '.work').exists()
        assert (source / 'project.json').read_bytes() == descriptor and (source / entry).read_bytes() == b'unchanged'
        checks.append(kind + ' rejected by type without writing')
    # 预设包既不是 video 也不是 web：单独的退出码与指路文案。
    preset = folder / 'preset'
    preset.mkdir()
    (preset / 'project.json').write_text(json.dumps({'dependency': '3172471800', 'alignment': 'center'}), encoding='utf-8')
    output = folder / 'preset-plan.json'
    result = invoke('analyze', preset, '--out', output)
    preset_report = report(result)
    assert result.returncode == 3 and preset_report['kind'] == 'preset' and preset_report['dependency'] == '3172471800'
    assert '431960\\3172471800' in preset_report['message'] and not output.exists()
    # 预设包以前只吐一段英文 JSON 就退出，中文用户看不到任何人话；现在必须也有结论那一行。
    assert '431960\\3172471800' in verdict(result)
    checks.append('preset reported separately from video and web, with a verdict line')
    # 子命令帮助不再把 --help 当成壁纸路径。
    for flag in ('--help', '-h'):
        result = invoke('analyze', flag)
        assert result.returncode == 0 and result.stdout.startswith(b'wpe-baker analyze SOURCE')
        assert b'--loop-preference' in result.stdout
        checks.append('analyze ' + flag + ' prints its own usage')
    for name, request in [('legacy', {'schema_version': 1, 'actions': []}),
                          ('media-plan', {'schema_version': 2, 'kind': 'media_optimization'})]:
        path = folder / (name + '.json')
        path.write_text(json.dumps(request), encoding='utf-8')
        output = folder / (name + '-output')
        args = ['bake', path] + (['--out', output] if 'kind' in request else [])
        result = invoke(*args)
        assert result.returncode == 1 and 'version 2 Scene bake request' in report(result)['message']
        assert not output.exists()
        checks.append(name + ' rejected before source/tools')
    for command in ('benchmark', 'benchmark-official'):
        result = invoke(command, folder / 'not-opened.json')
        assert result.returncode == 1 and 'Unknown command' in report(result)['message']
        checks.append(command + ' removed')
    print(json.dumps({'status': 'passed', 'checks': checks}))
