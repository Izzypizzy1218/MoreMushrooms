"""Check that a disposable native health demonstration preserved user data."""
import hashlib
import json
import sys
from pathlib import Path

profile = Path(sys.argv[1]).resolve()
project = Path(__file__).resolve().parents[1]
game = Path(r"C:\Program Files (x86)\Steam\steamapps\common\RimWorld")
user_data = Path.home() / "AppData/LocalLow/Ludeon Studios/RimWorld by Ludeon Studios"
before = json.loads((profile / "preservation-before.json").read_text(encoding="utf-8-sig"))

def digest(path):
    h = hashlib.sha256()
    with path.open("rb") as stream:
        for block in iter(lambda: stream.read(1024 * 1024), b""):
            h.update(block)
    return h.hexdigest()

roots = {
    "userConfig": user_data / "Config",
    "userSaveHashes": user_data / "Saves",
    "installed": game / "Mods/MoreMushrooms",
    "releases": project / "Releases",
}
results = {}
for group, root in roots.items():
    actual = {str(p.relative_to(root)): digest(p) for p in root.rglob("*") if p.is_file()}
    expected = before[group]
    changed = [key for key in actual.keys() | expected.keys() if actual.get(key) != expected.get(key)]
    results[group] = {"matched": len(expected) - len(changed), "changed": changed, "pass": not changed}
results["testHelpersRemoved"] = all(
    not (game / "Mods" / name).exists()
    for name in ("RimMushroomsTestHarness", "RimMushroomsTestHarmony")
)
results["pass"] = all(row["pass"] for row in results.values() if isinstance(row, dict)) and results["testHelpersRemoved"]
(profile / "preservation-after.json").write_text(json.dumps(results, ensure_ascii=False, indent=2), encoding="utf-8")
print(json.dumps(results, ensure_ascii=False, indent=2))
sys.exit(0 if results["pass"] else 1)
