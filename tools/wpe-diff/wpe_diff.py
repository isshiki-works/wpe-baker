"""wpe-diff：渲染器差分工具（对照版 vs 候选版），只用 Python 标准库。

用法：
  python wpe_diff.py run --exe <wpe-render.exe> --jobs <job 目录> --out <运行目录> [--only 模式,模式]
  python wpe_diff.py compare <运行目录A> <运行目录B> [--expected expected-deltas.json] [--json 结果.json]

run：对 <job 目录>/*.job.json 逐个注入 output_dir=<运行目录>/<id>/native 后执行
  `<exe> render --job`，stdout 存 stdout.bin（raw_stdout 时即逐帧 RGBA），stderr 存 stderr.log，
  退出码存 meta.json。已有 meta.json 的 job 跳过（分批排队续跑）；没有 meta.json 的残留目录先删再跑。
  渲染器运行要经单机队列：queue.py run --name ... -- python wpe_diff.py run ... --only <一小批>。
compare：按 PLAN §3 判据逐 job 比对。二进制输出（帧、采样、音频、留帧、编码成品）逐字节；
  result.json 逐字段、frames.jsonl 逐行逐字段，排除下方 VOLATILE_* 列出的易变字段。
  差异项若匹配 expected-deltas.json 的登记条目算“已登记”，否则算“未登记”；有未登记差异退出码 1。

expected-deltas.json：{"deltas": [{"job": "c-3257*", "item": "file:native/frames.rgba",
  "reason": "为什么", "step": "R2"}]}，job/item 用 fnmatch 通配。差异项命名：
  exit_code | job:missing | file:<相对路径>（存在性或字节）| result:<点路径> |
  frames.jsonl:<行号>.<键>（数组下标也用点，如 result:diagnostics.0.message）。
"""
from __future__ import annotations

import argparse
import fnmatch
import hashlib
import json
import re
import shutil
import subprocess
import sys
import time
from pathlib import Path

# 易变字段：同一 exe、同一 job 连跑两遍也会变的值，比较前剔除。每项都经 T0a 自比消融实测：
# 去掉它就在对应 job 上出现差异。不在这里的字段一律参与比较。
VOLATILE_RESULT = {
    # 墙钟耗时，取决于机器负载与队列里的其它进程（去掉则 57/57 个 job 报差异）。
    "wall_seconds",
}
VOLATILE_RESULT_PATTERNS = [
    # GPU 时间戳实测毫秒数 total_ms/draw_ms（job 里 gpu_timing=true 才有值；去掉则 26 个 job 报差异）。
    "gpu_timing.*_ms",
]
# frames.jsonl 每帧 CPU/GPU 计时 step_ms、render_ms、gpu_*_ms、cpu_*_ms（去掉则 56 个 job 报差异）。
VOLATILE_FRAME_KEY = re.compile(r"_ms$")
# 不参与比较的文件：
#   job.json / native/request.json  本工具写的输入及渲染器的原样回显，只差 output_dir；回显无下游读者；
#   meta.json  本工具记的退出码与耗时（退出码单独比）；
#   stderr.log 进度行与带时间戳的日志，语义内容（错误计数、诊断）已在 result.json 里逐字段比；
#   native/shader-cache/  着色器编译缓存，中间产物，C# 不读；自比时逐字节稳定，排除它是为了
#                  改名/改缓存格式的步骤不出一片误报（像素变化照样会在帧里暴露）。
SKIP_FILES = {"job.json", "meta.json", "stderr.log", "native/request.json"}
SKIP_DIRS = ("native/shader-cache/",)
# 编码成品的感知门（renderer-ruling 判据表）：同 QP/GOP 时要求逐字节；否则解码后比。接口预留，未实现。
VIDEO_GATE = {"ssim_min": 0.999, "psnr_min_db": 50.0, "tile": 64, "tile_mae_max": None}


def video_gate(a: Path, b: Path) -> dict | None:
    """编码成品逐字节不同时的感知比对入口。返回 {"pass": bool, ...} 或 None（未实现，按字节差异处理）。"""
    return None


def run(args: argparse.Namespace) -> int:
    exe = Path(args.exe).resolve()
    out = Path(args.out).resolve()
    out.mkdir(parents=True, exist_ok=True)
    exe_sha = hashlib.sha256(exe.read_bytes()).hexdigest()
    info_path = out / "run.json"
    if info_path.exists():
        info = json.loads(info_path.read_text(encoding="utf-8"))
        if info["exe_sha256"] != exe_sha:
            print(f"运行目录已属于另一个 exe（{info['exe_sha256'][:12]}），拒绝混用", file=sys.stderr)
            return 2
    else:
        info_path.write_text(json.dumps({"exe": str(exe), "exe_sha256": exe_sha, "jobs": str(Path(args.jobs).resolve())},
                                        indent=1, ensure_ascii=False) + "\n", encoding="utf-8")
    patterns = [p for p in (args.only or "").split(",") if p]
    failed = 0
    for job_file in sorted(Path(args.jobs).glob("*.job.json")):
        jid = job_file.name[:-len(".job.json")]
        if patterns and not any(fnmatch.fnmatchcase(jid, p) for p in patterns):
            continue
        jdir = out / jid
        if (jdir / "meta.json").exists():
            continue
        if jdir.exists():
            shutil.rmtree(jdir)
        jdir.mkdir()
        job = json.loads(job_file.read_text(encoding="utf-8"))
        job["output_dir"] = str(jdir / "native")
        (jdir / "job.json").write_text(json.dumps(job, indent=1, ensure_ascii=False) + "\n", encoding="utf-8")
        start = time.monotonic()
        with open(jdir / "stdout.bin", "wb") as so, open(jdir / "stderr.log", "wb") as se:
            code = subprocess.call([str(exe), "render", "--job", str(jdir / "job.json")], stdout=so, stderr=se)
        seconds = round(time.monotonic() - start, 3)
        (jdir / "meta.json").write_text(json.dumps({"exit_code": code, "seconds": seconds}) + "\n", encoding="utf-8")
        failed += code != 0
        print(f"{jid}: 退出码 {code}，{seconds} s", flush=True)
    return 0 if not failed else 3


def leaves(value, prefix=""):
    if isinstance(value, dict):
        if not value and prefix:
            yield prefix, value
        for k, v in value.items():
            yield from leaves(v, f"{prefix}.{k}" if prefix else str(k))
    elif isinstance(value, list):
        if not value and prefix:
            yield prefix, value
        for i, v in enumerate(value):
            yield from leaves(v, f"{prefix}.{i}" if prefix else str(i))
    else:
        yield prefix, value


def volatile_result(path: str) -> bool:
    return path in VOLATILE_RESULT or any(fnmatch.fnmatchcase(path, p) for p in VOLATILE_RESULT_PATTERNS)


def diff_json(tag: str, a, b, skip) -> list[dict]:
    la, lb = dict(leaves(a)), dict(leaves(b))
    out = []
    for key in sorted(set(la) | set(lb)):
        if skip(key):
            continue
        va, vb = la.get(key, "<缺失>"), lb.get(key, "<缺失>")
        if va != vb or type(va) is not type(vb):
            out.append({"item": f"{tag}:{key}", "a": va, "b": vb})
    return out


def diff_bytes(rel: str, fa: Path, fb: Path) -> list[dict]:
    """逐字节比；不同时报首个不同字节的偏移（RGBA 帧号 = 偏移 // (宽×高×4)）。"""
    offset = 0
    with open(fa, "rb") as a, open(fb, "rb") as b:
        while True:
            ca, cb = a.read(1 << 20), b.read(1 << 20)
            if ca != cb:
                offset += next((i for i, (x, y) in enumerate(zip(ca, cb)) if x != y), min(len(ca), len(cb)))
                item = {"item": f"file:{rel}", "a": f"{fa.stat().st_size} 字节", "b": f"{fb.stat().st_size} 字节",
                        "first_diff_offset": offset}
                if rel.endswith(".mp4"):
                    item["video_gate"] = video_gate(fa, fb)
                return [item]
            if not ca:
                return []
            offset += len(ca)


def files_of(jdir: Path) -> set[str]:
    out = set()
    for p in jdir.rglob("*"):
        rel = p.relative_to(jdir).as_posix()
        if p.is_file() and rel not in SKIP_FILES and not rel.startswith(SKIP_DIRS):
            out.add(rel)
    return out


def load_json(p: Path):
    return json.loads(p.read_text(encoding="utf-8"))


def compare_job(ja: Path, jb: Path) -> list[dict]:
    diffs = []
    ma, mb = load_json(ja / "meta.json"), load_json(jb / "meta.json")
    if ma["exit_code"] != mb["exit_code"]:
        diffs.append({"item": "exit_code", "a": ma["exit_code"], "b": mb["exit_code"]})
    fa, fb = files_of(ja), files_of(jb)
    for rel in sorted(fa ^ fb):
        diffs.append({"item": f"file:{rel}", "a": "有" if rel in fa else "无", "b": "有" if rel in fb else "无"})
    for rel in sorted(fa & fb):
        pa, pb = ja / rel, jb / rel
        if rel == "native/result.json":
            diffs += diff_json("result", load_json(pa), load_json(pb), volatile_result)
        elif rel == "native/frames.jsonl":
            la = pa.read_text(encoding="utf-8").splitlines()
            lb = pb.read_text(encoding="utf-8").splitlines()
            if len(la) != len(lb):
                diffs.append({"item": "frames.jsonl:lines", "a": len(la), "b": len(lb)})
            for i, (x, y) in enumerate(zip(la, lb)):
                for d in diff_json("frames.jsonl", json.loads(x), json.loads(y), lambda k: bool(VOLATILE_FRAME_KEY.search(k))):
                    d["item"] = f"frames.jsonl:{i}.{d['item'].split(':', 1)[1]}"
                    diffs.append(d)
        else:
            diffs += diff_bytes(rel, pa, pb)
    return diffs


def compare(args: argparse.Namespace) -> int:
    a, b = Path(args.a).resolve(), Path(args.b).resolve()
    deltas = load_json(Path(args.expected))["deltas"] if args.expected else []
    ids_a = {p.parent.name for p in a.glob("*/meta.json")}
    ids_b = {p.parent.name for p in b.glob("*/meta.json")}
    report = {"schema": "wpe-diff-report-v1", "a": str(a), "b": str(b), "expected": args.expected,
              "volatile": {"result": sorted(VOLATILE_RESULT) + VOLATILE_RESULT_PATTERNS,
                           "frames.jsonl": VOLATILE_FRAME_KEY.pattern, "files": sorted(SKIP_FILES) + list(SKIP_DIRS)},
              "jobs": {}}
    counts = {"same": 0, "registered": 0, "diff": 0}
    for jid in sorted(ids_a | ids_b):
        if jid in ids_a and jid in ids_b:
            diffs = compare_job(a / jid, b / jid)
        else:
            diffs = [{"item": "job:missing", "a": "有" if jid in ids_a else "无", "b": "有" if jid in ids_b else "无"}]
        unregistered = 0
        for d in diffs:
            for e in deltas:
                if fnmatch.fnmatchcase(jid, e["job"]) and fnmatch.fnmatchcase(d["item"], e["item"]):
                    d["registered"] = f"{e.get('step', '')}: {e['reason']}"
                    break
            else:
                unregistered += 1
        status = "diff" if unregistered else "registered" if diffs else "same"
        counts[status] += 1
        report["jobs"][jid] = {"status": status, "diff_count": len(diffs), "unregistered": unregistered,
                               "diffs": diffs}
    report["summary"] = dict(counts, jobs=len(report["jobs"]))
    if args.json:
        Path(args.json).write_text(json.dumps(report, indent=1, ensure_ascii=False, default=str) + "\n", encoding="utf-8")
    print(f"wpe-diff：{len(report['jobs'])} 个 job，一致 {counts['same']}，只有已登记差异 {counts['registered']}，"
          f"有未登记差异 {counts['diff']}")
    for jid, j in report["jobs"].items():
        if j["status"] == "same":
            continue
        items = sorted({re.sub(r"^frames\.jsonl:\d+\.", "frames.jsonl:*.", d["item"]) for d in j["diffs"]
                        if "registered" not in d})
        print(f"  {jid}：{j['diff_count']} 处差异，未登记 {j['unregistered']}" + (f"：{', '.join(items[:8])}" if items else ""))
    return 1 if counts["diff"] else 0


def main() -> int:
    parser = argparse.ArgumentParser(description="渲染器差分工具")
    sub = parser.add_subparsers(dest="cmd", required=True)
    r = sub.add_parser("run")
    r.add_argument("--exe", required=True)
    r.add_argument("--jobs", required=True)
    r.add_argument("--out", required=True)
    r.add_argument("--only")
    c = sub.add_parser("compare")
    c.add_argument("a")
    c.add_argument("b")
    c.add_argument("--expected")
    c.add_argument("--json")
    args = parser.parse_args()
    return run(args) if args.cmd == "run" else compare(args)


if __name__ == "__main__":
    sys.exit(main())
