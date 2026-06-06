#!/usr/bin/env python3
"""
E2E A/B 集計ランナー（トークン＋速度の実測）。

オーケストレータ（Claude）が grep/ukg のサブエージェントを走らせた後、その
トランスクリプト（~/.config/claude/projects/<proj>/<session>/subagents/agent-*.jsonl）を
解析し、タスク×条件(grep/ukg)ごとに実トークンと所要時間を集計する。

実測ソース（自己申告ではない）:
  トークン: message.usage（input/cache_creation/cache_read/output）
  速度:     先頭エントリ→末尾エントリの timestamp 差（wall-clock 秒）

タスク識別はタスクファイル(--tasks)の各 task["match"]（プロンプト中の一意な部分文字列）。
条件は プロンプト中の "approach=grep|ukg"。

使い方:
  python3 tally_tokens.py <subagents_dir> --tasks arcade.local.json [--minutes 60] [--out results.local.md]
"""
import sys, os, json, glob, time, argparse
from datetime import datetime

# 実価格比（Claude）に近い加重: input 1x / cache write 1.25x / cache read 0.1x / output 5x
def metrics(entries):
    inp = cw = cr = out = 0
    ts = []
    for e in entries:
        t = e.get("timestamp")
        if t:
            try: ts.append(datetime.fromisoformat(t.replace("Z", "+00:00")))
            except: pass
        m = e.get("message")
        u = m.get("usage") if isinstance(m, dict) else None
        if isinstance(u, dict):
            inp += u.get("input_tokens", 0)
            cw  += u.get("cache_creation_input_tokens", 0)
            cr  += u.get("cache_read_input_tokens", 0)
            out += u.get("output_tokens", 0)
    new_input = inp + cw
    cost = int(inp + 1.25 * cw + 0.1 * cr + 5 * out)
    secs = (max(ts) - min(ts)).total_seconds() if len(ts) >= 2 else 0.0
    return {"new_input": new_input, "output": out, "cost": cost, "secs": secs}

def first_user_text(entries):
    for e in entries:
        m = e.get("message")
        if not isinstance(m, dict) or m.get("role") != "user":
            continue
        c = m.get("content")
        if isinstance(c, str): return c
        if isinstance(c, list):
            return " ".join(b.get("text", "") for b in c if isinstance(b, dict) and b.get("type") == "text")
    return ""

def main():
    ap = argparse.ArgumentParser()
    ap.add_argument("subagents_dir")
    ap.add_argument("--tasks", required=True, help="タスク JSON（各 task に id と match）")
    ap.add_argument("--minutes", type=float, default=60)
    ap.add_argument("--out", default=None)
    args = ap.parse_args()

    spec = json.load(open(args.tasks))
    matchers = [(t["id"], t.get("match") or t["prompt"][:16]) for t in spec["tasks"]]

    def classify(text):
        approach = "ukg" if "approach=ukg" in text else ("grep" if "approach=grep" in text else None)
        task = next((tid for tid, m in matchers if m and m in text), None)
        return task, approach

    cutoff = time.time() - args.minutes * 60
    files = [f for f in glob.glob(os.path.join(args.subagents_dir, "agent-*.jsonl"))
             if os.path.getmtime(f) >= cutoff]

    data = {}  # data[task][approach] = metrics dict
    for f in files:
        entries = []
        for line in open(f):
            line = line.strip()
            if line:
                try: entries.append(json.loads(line))
                except: pass
        task, approach = classify(first_user_text(entries))
        if task and approach:
            data.setdefault(task, {})[approach] = metrics(entries)

    lines = []
    def P(s=""): lines.append(s)
    P("# E2E 実測（トークン[コスト加重] ＋ 速度）")
    P(f"対象: {len(files)} transcript / 直近{args.minutes:.0f}分")
    P("加重: input 1x / cache write 1.25x / cache read 0.1x / output 5x")
    P("")
    P("| タスク | コスト≈(grep→ukg) | コスト削減 | 時間秒(grep→ukg) | 時間削減 |")
    P("|---|---|---|---|---|")
    tg = {"cost": 0, "secs": 0.0}; tu = {"cost": 0, "secs": 0.0}
    for task in sorted(data):
        g = data[task].get("grep"); u = data[task].get("ukg")
        if not g or not u:
            P(f"| {task} | (片側欠落) | | | |"); continue
        tg["cost"] += g["cost"]; tg["secs"] += g["secs"]
        tu["cost"] += u["cost"]; tu["secs"] += u["secs"]
        cred = (1 - u["cost"]/g["cost"]) * 100 if g["cost"] else 0
        tred = (1 - u["secs"]/g["secs"]) * 100 if g["secs"] else 0
        P(f"| {task} | {g['cost']:,}→{u['cost']:,} | {cred:+.0f}% | {g['secs']:.0f}→{u['secs']:.0f} | {tred:+.0f}% |")
    if tg["cost"] and tu["cost"]:
        cred = (1 - tu["cost"]/tg["cost"]) * 100
        tred = (1 - tu["secs"]/tg["secs"]) * 100 if tg["secs"] else 0
        P(f"| **合計** | **{tg['cost']:,}→{tu['cost']:,}** | **{cred:+.0f}%** | **{tg['secs']:.0f}→{tu['secs']:.0f}** | **{tred:+.0f}%** |")

    text = "\n".join(lines)
    print(text)
    if args.out:
        with open(args.out, "a") as fh:
            fh.write("\n\n" + text + "\n")
        print(f"\n[追記] {args.out}")

if __name__ == "__main__":
    main()
