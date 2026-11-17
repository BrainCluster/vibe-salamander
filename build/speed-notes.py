#!/usr/bin/env python3
# SPDX-FileCopyrightText: 2026 Vibe Salamander Authors
# SPDX-License-Identifier: GPL-3.0-or-later
"""
The "Speed" section of a release's notes: this release's PerformanceTests times next to the previous release's.

    speed-notes.py <version> <this benchmarks.md> [<previous version> <previous speed.md>]

Prints the section (Markdown). The tables are the ones PerformanceTests writes: | Measured | 123 ms | 10 s |.
"""
import re
import sys


def times(path):
    found = {}
    try:
        with open(path, encoding="utf-8") as f:
            for line in f:
                m = re.match(r"^\|\s*(.+?)\s*\|\s*([\d,.]+)\s*ms\s*\|", line)
                if m:
                    found[m.group(1)] = float(m.group(2).replace(",", ""))
    except OSError:
        pass
    return found


def ms(value):
    return f"{value:,.0f} ms"


def main():
    version, current = sys.argv[1], times(sys.argv[2])
    previous_version = sys.argv[3] if len(sys.argv) > 4 else None
    previous = times(sys.argv[4]) if previous_version else {}
    if not current:
        return
    print("## Speed")
    print()
    print("Measured on GitHub's build machine while the release was made; from run to run the times vary by about a fifth.")
    print()
    if not previous:
        print("| Measured | " + version + " |")
        print("|---|---:|")
        for what, value in current.items():
            print(f"| {what} | {ms(value)} |")
        return
    print(f"| Measured | {version} | {previous_version} | Change |")
    print("|---|---:|---:|---:|")
    for what, value in current.items():
        before = previous.get(what)
        if before is None:
            print(f"| {what} | {ms(value)} | – | new |")
            continue
        change = (value - before) / before * 100 if before > 0 else 0
        mark = " (slower)" if change >= 50 else " (faster)" if change <= -33 else ""
        print(f"| {what} | {ms(value)} | {ms(before)} | {change:+.0f} %{mark} |")


if __name__ == "__main__":
    main()
