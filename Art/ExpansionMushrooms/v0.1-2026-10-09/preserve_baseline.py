"""Preserve the currently implemented runtime and 15-species art during staging."""
import argparse, hashlib, json
from pathlib import Path
ROOT = Path(__file__).resolve().parent
PROJECT = ROOT.parents[2]
BASELINE = ROOT / "baseline-preservation.json"
def snapshot():
    result = {}
    for folder in ("About", "Assemblies", "Defs", "Languages", "Textures", "Source"):
        source = PROJECT / folder
        if not source.exists():
            continue
        for file in sorted(source.rglob("*")):
            if file.is_file():
                result[file.relative_to(PROJECT).as_posix()] = hashlib.sha256(file.read_bytes()).hexdigest()
    return result
def main():
    parser = argparse.ArgumentParser()
    parser.add_argument("--validate", action="store_true")
    args = parser.parse_args()
    current = snapshot()
    if args.validate:
        original = json.loads(BASELINE.read_text(encoding="utf-8"))["files"]
        added = sorted(current.keys() - original.keys())
        removed = sorted(original.keys() - current.keys())
        changed = sorted(p for p in current.keys() & original.keys() if current[p] != original[p])
        result = {"unchanged": not (added or removed or changed), "tracked_files": len(original),
                  "added": added, "removed": removed, "changed": changed}
        print(json.dumps(result, ensure_ascii=False))
        raise SystemExit(0 if result["unchanged"] else 1)
    if BASELINE.exists():
        raise SystemExit("Baseline already exists; use --validate.")
    BASELINE.write_text(json.dumps({"baseline_mod_version":"0.4.0","folders":["About","Assemblies","Defs","Languages","Textures","Source"],"files":current}, ensure_ascii=False, indent=2)+"\n",encoding="utf-8")
    print(json.dumps({"baseline_saved":True,"tracked_files":len(current)}))
if __name__ == "__main__":
    main()

