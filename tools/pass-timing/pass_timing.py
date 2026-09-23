"""逐 pass GPU 计时汇总：读渲染器按 WPE_PASS_TIMING 写的 jsonl（pass-timing/1，格式见
engine/src/Scene/VulkanRender/PassTiming.hpp），按 pass 出中位/p95/均值与占比。

用法：python tools/pass-timing/pass_timing.py <pass-timing.jsonl> <输出前缀> [--warmup N]
  --warmup N  丢掉帧序号 < N 的帧（渲染器帧序号含 job 的 warmup_frames；首几帧含管线创建，建议丢掉）
输出：<前缀>.passes.csv（每个 pass 一行）、<前缀>.summary.json（整帧、按图层/效果汇总、前 15 名），并打印前 15 名。

同一帧里标注相同的 pass（名字、输出 RT、尺寸、图层、role、效果、效果序号都相同）按出现次序区分；
某帧没录制的 pass 那帧按 0 计。
"""
import argparse
import csv
import json
import statistics
from collections import defaultdict
from pathlib import Path

SCHEMA = "pass-timing/1"


def pct(xs, q):
    xs = sorted(xs)
    k = (len(xs) - 1) * q
    lo, hi = int(k), min(int(k) + 1, len(xs) - 1)
    return xs[lo] + (xs[hi] - xs[lo]) * (k - lo)


def main():
    ap = argparse.ArgumentParser()
    ap.add_argument("jsonl")
    ap.add_argument("out")
    ap.add_argument("--warmup", type=int, default=0)
    a = ap.parse_args()

    frames = []
    for n, line in enumerate(open(a.jsonl, encoding="utf-8"), 1):
        r = json.loads(line)
        if r.get("schema") != SCHEMA:
            raise SystemExit(f"{a.jsonl}:{n}: schema 是 {r.get('schema')!r}，本工具只读 {SCHEMA}")
        frames.append(r)
    used = [f for f in frames if f["frame"] >= a.warmup]
    if not used:
        raise SystemExit(f"{len(frames)} 帧全部在 --warmup {a.warmup} 之前")

    per_pass = defaultdict(list)  # (标注, 次序) -> [(帧下标, ms)]
    first = {}                    # (标注, 次序) -> (首次出现位置, 条目)
    for fi, f in enumerate(used):
        ordinal = defaultdict(int)
        for pi, p in enumerate(f["passes"]):
            k = (p["pass"], p["rt"], p["w"], p["h"], p["layer"], p["role"], p["effect"], p["effect_index"])
            pk = (k, ordinal[k])
            ordinal[k] += 1
            first.setdefault(pk, ((fi, pi), p))
            per_pass[pk].append(p["gpu_ns"] / 1e6)
    totals = [f["gpu_ns"] / 1e6 for f in used]
    uploads = [f["uploads_ns"] / 1e6 for f in used]
    n, mean_total = len(used), statistics.fmean(totals)

    rows = []
    for pk, xs in per_pass.items():
        pos, p = first[pk]
        xs = xs + [0.0] * (n - len(xs))
        mean = statistics.fmean(xs)
        rows.append({
            "first_seen": pos, "pass": p["pass"], "layer": p["layer"], "role": p["role"], "effect": p["effect"],
            "effect_index": p["effect_index"], "rt": p["rt"], "w": p["w"], "h": p["h"],
            "rt_mpix": round(p["w"] * p["h"] / 1e6, 3), "median_ms": pct(xs, 0.5), "p95_ms": pct(xs, 0.95),
            "mean_ms": mean, "share": mean / mean_total if mean_total > 0 else None,
            "cv": statistics.pstdev(xs) / mean if mean > 0 else None,
        })
    rows.sort(key=lambda r: r["first_seen"])

    def plain(r):
        return {k: (round(v, 6) if isinstance(v, float) else v) for k, v in r.items() if k != "first_seen"}

    out = Path(a.out)
    with open(out.with_suffix(".passes.csv"), "w", newline="", encoding="utf-8") as fh:
        w = csv.DictWriter(fh, fieldnames=list(plain(rows[0])))
        w.writeheader()
        w.writerows(plain(r) for r in rows)

    by_layer, by_effect = defaultdict(float), defaultdict(float)
    for r in rows:
        by_layer[r["layer"]] += r["mean_ms"]
        by_effect[(r["layer"], r["effect_index"], r["effect"] or r["role"] or r["pass"])] += r["mean_ms"]
    top = sorted(rows, key=lambda r: -r["mean_ms"])[:15]
    summary = {
        "source": str(Path(a.jsonl)), "schema": SCHEMA, "frames_total": len(frames), "frames_used": n,
        "warmup_dropped": a.warmup,
        "total_ms": {"median": pct(totals, 0.5), "p95": pct(totals, 0.95), "p5": pct(totals, 0.05),
                     "mean": mean_total, "min": min(totals), "max": max(totals),
                     "cv": statistics.pstdev(totals) / mean_total if mean_total > 0 else None},
        "uploads_ms_median": pct(uploads, 0.5),
        "by_layer_share": {str(k): round(v / mean_total, 4) if mean_total > 0 else None
                           for k, v in sorted(by_layer.items(), key=lambda kv: -kv[1])},
        "by_effect_ms": [{"layer": k[0], "effect_index": k[1], "effect": k[2], "mean_ms": round(v, 4),
                          "share": round(v / mean_total, 4) if mean_total > 0 else None}
                         for k, v in sorted(by_effect.items(), key=lambda kv: -kv[1])[:25]],
        "top15": [plain(r) for r in top],
    }
    out.with_suffix(".summary.json").write_text(json.dumps(summary, indent=1, ensure_ascii=False) + "\n",
                                                encoding="utf-8")
    print(f"帧 {n}/{len(frames)}（丢前 {a.warmup}），整帧中位 {summary['total_ms']['median']:.3f} ms，"
          f"p95 {summary['total_ms']['p95']:.3f}，pass {len(rows)} 个")
    print(f"{'pass':58s} {'layer':>5s} {'RT':>10s} {'中位ms':>8s} {'p95':>8s} {'占比':>6s}")
    for r in top:
        label = f"{r['pass'].split('/')[-1]}[{r['effect'] or r['role']}#{r['effect_index']}]"
        share = f"{r['share'] * 100:5.1f}%" if r["share"] is not None else "    -"
        rt = f"{r['w']}x{r['h']}"
        print(f"{label[:58]:58s} {r['layer']:>5} {rt:>10s} {r['median_ms']:8.3f} {r['p95_ms']:8.3f} {share}")


if __name__ == "__main__":
    main()
