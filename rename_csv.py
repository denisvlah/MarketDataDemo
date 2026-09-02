#!/usr/bin/env python3
"""
Rename Kraken OHLCVT CSV files to the CsvLoader naming convention.

Input pattern:  {Symbol}_{IntervalMinutes}.csv          (e.g. ETHEUR_60.csv)
Output pattern: {Symbol}_{IntervalMinutes}_{Start}_{End}.csv
DateTime format: yyyy-MM-dd HH:mm:ss  (UTC, e.g. 2024-01-01 00:00:00)

Each CSV row (no header): timestamp_unix, open, high, low, close, volume, trade_count

Usage:
    python rename_csv.py            # renames files in the current directory tree (recursive)
    python rename_csv.py /path/to   # renames files in the given directory tree (recursive)
    python rename_csv.py --dry-run  # preview without renaming
"""

import sys
import os
import re
from datetime import datetime, timezone

# Source filename: ETHEUR_60.csv  (no timestamps yet)
SOURCE_PATTERN = re.compile(r'^([A-Za-z0-9]+)_(\d+)\.csv$')

# Target filename already contains timestamps — skip these
TARGET_PATTERN = re.compile(
    r'^[A-Za-z0-9]+_\d+_\d{4}-\d{2}-\d{2} \d{2}:\d{2}:\d{2}_\d{4}-\d{2}-\d{2} \d{2}:\d{2}:\d{2}\.csv$'
)

DATE_FORMAT = "%Y-%m-%d %H:%M:%S"


def unix_to_utc_str(seconds: int) -> str:
    return datetime.fromtimestamp(seconds, tz=timezone.utc).strftime(DATE_FORMAT)


def read_first_line(path: str) -> str | None:
    """Return the first non-empty line of a file."""
    with open(path, "r", encoding="ascii", errors="replace") as f:
        for line in f:
            line = line.strip()
            if line:
                return line
    return None


def read_last_line(path: str) -> str | None:
    """
    Efficiently read the last non-empty line by seeking to the last 512 bytes.
    512 bytes is well beyond the length of any single Kraken CSV row.
    """
    with open(path, "rb") as f:
        f.seek(0, os.SEEK_END)
        file_size = f.tell()
        if file_size == 0:
            return None
        block = min(512, file_size)
        f.seek(-block, os.SEEK_END)
        tail = f.read().decode("ascii", errors="replace")

    lines = [l.strip() for l in tail.splitlines() if l.strip()]
    return lines[-1] if lines else None


def parse_unix_timestamp(line: str) -> int:
    """Extract the first comma-delimited field and parse it as a Unix timestamp."""
    part = line.split(",")[0].strip()
    return int(part)


def get_timestamp_range(path: str) -> tuple[int, int] | None:
    """Return (first_unix, last_unix) or None if the file is empty/unreadable."""
    first_line = read_first_line(path)
    if not first_line:
        return None
    last_line = read_last_line(path)
    if not last_line:
        return None
    first_ts = parse_unix_timestamp(first_line)
    last_ts = parse_unix_timestamp(last_line)
    return first_ts, last_ts


def process_directory(directory: str, dry_run: bool) -> None:
    # Collect CSV files recursively
    csv_paths = sorted(
        os.path.join(root, f)
        for root, _dirs, files in os.walk(directory)
        for f in files
        if f.lower().endswith(".csv")
    )

    if not csv_paths:
        print("No CSV files found.")
        return

    renamed = skipped = errors = 0

    for src_path in csv_paths:
        rel_path = os.path.relpath(src_path, directory)
        filename = os.path.basename(src_path)

        # Skip files already in the target format
        if TARGET_PATTERN.match(filename):
            print(f"  [skip]   {rel_path}  (already renamed)")
            skipped += 1
            continue

        match = SOURCE_PATTERN.match(filename)
        if not match:
            print(f"  [skip]   {rel_path}  (unrecognized name pattern)")
            skipped += 1
            continue

        symbol = match.group(1)
        interval = match.group(2)

        try:
            range_ = get_timestamp_range(src_path)
        except Exception as exc:
            print(f"  [error]  {rel_path}  — could not read timestamps: {exc}")
            errors += 1
            continue

        if range_ is None:
            print(f"  [skip]   {rel_path}  (empty file)")
            skipped += 1
            continue

        first_ts, last_ts = range_
        start_str = unix_to_utc_str(first_ts)
        end_str = unix_to_utc_str(last_ts)
        new_name = f"{symbol}_{interval}_{start_str}_{end_str}.csv"
        dst_path = os.path.join(os.path.dirname(src_path), new_name)

        if os.path.exists(dst_path):
            print(f"  [skip]   {rel_path}  → {new_name}  (target already exists)")
            skipped += 1
            continue

        if dry_run:
            print(f"  [dry]    {rel_path}  →  {new_name}")
        else:
            os.rename(src_path, dst_path)
            print(f"  [done]   {rel_path}  →  {new_name}")

        renamed += 1

    action = "Would rename" if dry_run else "Renamed"
    print(f"\n{action}: {renamed}  |  Skipped: {skipped}  |  Errors: {errors}")


def main() -> None:
    args = sys.argv[1:]
    dry_run = "--dry-run" in args
    args = [a for a in args if a != "--dry-run"]
    directory = args[0] if args else "."

    if not os.path.isdir(directory):
        print(f"Error: '{directory}' is not a directory.", file=sys.stderr)
        sys.exit(1)

    print(f"{'[DRY RUN] ' if dry_run else ''}Scanning: {os.path.abspath(directory)}\n")
    process_directory(directory, dry_run)


if __name__ == "__main__":
    main()
