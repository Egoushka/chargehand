#!/usr/bin/env python3
"""Prepare the ADR 0078 step 3 measurement: clone each repository of scripts/mise-spike.json at its pinned commit,
commit a mise.toml with the tools the entry pins (the repository's own pins win: no file is written when one exists),
and write the spike.json that MiseSpikeTests reads.

    scripts/mise-spike-prepare.py <manifest> <spike-dir>
"""
import json
import subprocess
import sys
from pathlib import Path

OWN_PINS = ("mise.toml", ".mise.toml", ".tool-versions")


def git(cwd: Path, *args: str) -> str:
    return subprocess.run(["git", "-C", str(cwd), *args], check=True, capture_output=True, text=True).stdout.strip()


def main() -> None:
    manifest, spike_dir = Path(sys.argv[1]), Path(sys.argv[2])
    spike_dir.mkdir(parents=True, exist_ok=True)
    entries = []
    for entry in json.loads(manifest.read_text()):
        checkout = spike_dir / entry["name"]
        checkout.mkdir()
        git(checkout, "init", "-q")
        git(checkout, "fetch", "-q", "--depth", "1", entry["repository"], entry["commit"])
        git(checkout, "checkout", "-q", "FETCH_HEAD")
        if not any((checkout / name).exists() for name in OWN_PINS):
            tools = "".join(f'{tool} = "{version}"\n' for tool, version in entry["tools"].items())
            (checkout / "mise.toml").write_text(f"[tools]\n{tools}")
            git(checkout, "add", "mise.toml")
            git(checkout, "-c", "user.name=spike", "-c", "user.email=spike@localhost", "commit", "-q", "-m", "pin tools")
        entries.append({"name": entry["name"], "directory": entry["name"], "commit": git(checkout, "rev-parse", "HEAD"), "test": entry["test"]})
    (spike_dir / "spike.json").write_text(json.dumps(entries, indent=2))
    print("\n".join(f"{e['name']} {e['commit']}" for e in entries))


if __name__ == "__main__":
    main()
