#!/usr/bin/env python3
"""
E2E A/B トークン集計ランナー。

オーケストレータ（Claude）が grep/ukg のサブエージェントを走らせた後、その
トランスクリプト（~/.config/claude/projects/<proj>/<session>/subagents/agent-*.jsonl）を
解析し、タスク×条件(grep/ukg)ごとに実トークンを集計する。

トークンは agent-*.jsonl の message.usage から取得（自己申告ではなく実測）:
  new_input  = input_tokens + cache_creation_input_tokens   … 新規にコンテキストへ入った分（ファイル/ツール結果/プロンプト）
  cache_read = cache_read_input_tokens                        … 再読込（静的コンテキスト, 安価）
  output     = output_tokens                                  … 生成
  total      = 上記合計

使い方:
  python3 tally_tokens.py <subagents_dir> [--minutes 60] [--out results.local.md]

タスク識別はプロンプト中のキーワード、条件は "approach=grep|ukg" で判定する。
"""
import sys, os, json, glob, time, argparse

# プロンプト中のキーワード → タスクID
TASK_KEYWORDS = {
    "選択肢ボタン": "loc-optionbtn",
    "Google Drive": "loc-drive",
    "service-account": "loc-driveauth",
    "クイズプレイ中の体力": "loc-quizlogic",
    "netcode": "neg-netcode",
}

def first_user_text(entries):
    for e in entries:
        m = e.get("message")
        if not isinstance(m, dict) or m.get("role") != "user":
            continue
        c = m.get("content")
        if isinstance(c, str):
            return c
        if isinstance(c, list):
            return " ".join(b.get("text", "") for b in c if isinstance(b, dict) and b.get("type") == "text")
    return ""

# 実価格比（Claude）に近い加重: input 1x / cache write 1.25x / cache read 0.1x / output 5x
def sum_usage(entries):
    inp = cw = cr = out = 0
    for e in entries:
        m = e.get("message")
        u = m.get("usage") if isinstance(m, dict) else None
        if not isinstance(u, dict):
            continue
        inp += u.get("input_tokens", 0)
        cw += u.get("cache_creation_input_tokens", 0)
        cr += u.get("cache_read_input_tokens", 0)
        out += u.get("output_tokens", 0)
    new_input = inp + cw                                   # 新規にコンテキストへ入った分
    cost = inp + 1.25 * cw + 0.1 * cr + 5 * out            # コスト加重（≒課金単位）
    return new_input, out, int(cost)

def classify(text):
    approach = "ukg" if "approach=ukg" in text else ("grep" if "approach=grep" in text else None)
    task = next((tid for kw, tid in TASK_KEYWORDS.items() if kw in text), None)
    return task, approach

def main():
    ap = argparse.ArgumentParser()
    ap.add_argument("subagents_dir")
    ap.add_argument("--minutes", type=float, default=60)
    ap.add_argument("--out", default=None)
    args = ap.parse_args()

    cutoff = time.time() - args.minutes * 60
    files = [f for f in glob.glob(os.path.join(args.subagents_dir, "agent-*.jsonl"))
             if os.path.getmtime(f) >= cutoff]

    # data[task][approach] = (new_input, cache_read, output, total)
    data = {}
    for f in files:
        entries = []
        for line in open(f):
            line = line.strip()
            if line:
                try: entries.append(json.loads(line))
                except: pass
        task, approach = classify(first_user_text(entries))
        if not task or not approach:
            continue
        data.setdefault(task, {})[approach] = sum_usage(entries)  # (new_input, output, cost)

    # 出力
    lines = []
    def P(s=""): lines.append(s)
    P(f"# E2E トークン実測（agent-*.jsonl の usage より / コスト加重）")
    P(f"対象: {len(files)} transcript / 直近{args.minutes:.0f}分")
    P(f"加重: input 1x / cache write 1.25x / cache read 0.1x / output 5x")
    P("")
    P("| タスク | new_input(grep→ukg) | output(grep→ukg) | コスト≈(grep→ukg) | コスト削減 |")
    P("|---|---|---|---|---|")
    tot = {"grep": [0,0,0], "ukg": [0,0,0]}
    for task in sorted(data):
        g = data[task].get("grep"); u = data[task].get("ukg")
        if not g or not u:
            P(f"| {task} | (片側欠落) | | | |"); continue
        for i in range(3):
            tot["grep"][i] += g[i]; tot["ukg"][i] += u[i]
        red = (1 - u[2]/g[2]) * 100 if g[2] else 0
        P(f"| {task} | {g[0]:,}→{u[0]:,} | {g[1]:,}→{u[1]:,} | {g[2]:,}→{u[2]:,} | {red:+.0f}% |")
    if tot["grep"][2] and tot["ukg"][2]:
        red = (1 - tot["ukg"][2]/tot["grep"][2]) * 100
        P(f"| **合計** | **{tot['grep'][0]:,}→{tot['ukg'][0]:,}** | **{tot['grep'][1]:,}→{tot['ukg'][1]:,}** | **{tot['grep'][2]:,}→{tot['ukg'][2]:,}** | **{red:+.0f}%** |")

    text = "\n".join(lines)
    print(text)
    if args.out:
        with open(args.out, "a") as fh:
            fh.write("\n\n" + text + "\n")
        print(f"\n[追記] {args.out}")

if __name__ == "__main__":
    main()
