"""Summarize QA runs without treating Rendering callbacks as presented frames."""
import argparse
import json
from pathlib import Path
from statistics import median


def summarize(root):
    groups = {}
    for path in sorted(root.rglob("*.json")):
        data = json.loads(path.read_text(encoding="utf-8-sig"))
        if "callbackIntervalsMs" not in data:
            continue
        groups.setdefault((data["mode"], data["scenario"]), []).append(data)
    lines = ["Callback pacing only; scroll response is routed-input to offset change, not input-to-photon.",
             "Cells are medians of trial metrics; layout counts are not layout cost.",
             "| Mode | Scenario | Trials | P95 ms | P99 ms | >2 refresh periods | Layouts/callback | Response P95 ms | Duplicate animation stamps |",
             "|---|---|---:|---:|---:|---:|---:|---:|---:|"]
    for (mode, scenario), trials in sorted(groups.items()):
        p95 = median(t["callbackIntervalsMs"]["p95"] for t in trials)
        p99 = median(t["callbackIntervalsMs"]["p99"] for t in trials)
        over = [t["overTwoRefreshPeriods"] for t in trials if t["overTwoRefreshPeriods"] is not None]
        layouts = median(t["layoutUpdatedCount"] / max(1, t["callbackIntervalsMs"]["count"] + 1) for t in trials)
        response = median(t["scrollResponseMs"]["p95"] for t in trials)
        dup = sum(t["duplicateAnimationStamps"] for t in trials)
        lines.append(f"| {mode} | {scenario} | {len(trials)} | {p95:.2f} | {p99:.2f} | {median(over) if over else 'unavailable'} | {layouts:.2f} | {response:.2f} | {dup} |")
    return "\n".join(lines) + "\n"


if __name__ == "__main__":
    parser = argparse.ArgumentParser(description=__doc__)
    parser.add_argument("root", type=Path)
    parser.add_argument("--output", type=Path)
    args = parser.parse_args()
    result = summarize(args.root)
    if args.output:
        args.output.write_text(result, encoding="utf-8")
    print(result)
