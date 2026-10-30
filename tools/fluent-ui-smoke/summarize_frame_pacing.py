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
        responses = [t["scrollResponseMs"]["p95"] for t in trials if t["scrollResponseMs"]["count"]]
        response = f"{median(responses):.2f}" if responses else "unavailable"
        dup = sum(t["duplicateAnimationStamps"] for t in trials)
        lines.append(f"| {mode} | {scenario} | {len(trials)} | {p95:.2f} | {p99:.2f} | {median(over) if over else 'unavailable'} | {layouts:.2f} | {response} | {dup} |")
    if not groups:
        lines.append("INCOMPLETE: no saved trials found.")
    completion = list(root.rglob("frame-pacing-report.txt"))
    if not completion:
        lines.append("INCOMPLETE: no completed harness report; saved trials may belong to an aborted run.")
    for (mode, scenario), trials in sorted(groups.items()):
        if len(trials) != 3 or {t["trial"] for t in trials} != {1, 2, 3}:
            lines.append(f"INCOMPLETE: {mode}/{scenario} requires exactly trials 1, 2, 3; do not pool multiple runs.")
        if any(t["droppedSamples"] for t in trials):
            lines.append(f"INCOMPLETE: {mode}/{scenario} dropped probe samples.")
    for path in sorted(root.rglob("*-trace-unavailable.txt")):
        lines.append(f"Trace limitation ({path.name}): {path.read_text(encoding='utf-8-sig').strip()}")
    lines.append("Root cause: unassigned; interval statistics alone do not explain spikes. Presentation and exact layout cost require trace analysis.")
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
