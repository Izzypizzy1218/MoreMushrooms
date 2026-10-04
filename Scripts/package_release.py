"""Create immutable runtime and source ZIPs, retaining provenance and file hashes."""
from pathlib import Path
import hashlib
import json
import zipfile

ROOT = Path(__file__).resolve().parents[1]

def package():
    version = json.loads((ROOT / "Balance/mushrooms.json").read_text(encoding="utf-8"))["version"]
    dest = ROOT / "Releases" / ("v" + version)
    if dest.exists(): raise SystemExit("Release already exists. Increase the version instead of overwriting it.")
    runtime_dirs = {"About","Assemblies","Defs","Languages","Textures","Credits"}
    runtime_files = [p for p in ROOT.rglob("*") if p.is_file() and (p.relative_to(ROOT).parts[0] in runtime_dirs or p.relative_to(ROOT).as_posix() in {"README.md","CHANGELOG.md","Docs/VALIDATION.md"})]
    excluded = {".git","Releases","Backups","obj","__pycache__"}
    source_files = [p for p in ROOT.rglob("*") if p.is_file() and not any(part in excluded for part in p.relative_to(ROOT).parts) and not p.relative_to(ROOT).as_posix().startswith(("Tests/Runtime/","Tests/Harness/Assemblies/"))]
    dest.mkdir(parents=True)
    manifest = {p.relative_to(ROOT).as_posix(): hashlib.sha256(p.read_bytes()).hexdigest() for p in runtime_files}
    for suffix, files in (("",runtime_files),("-source",source_files)):
        target = dest / ("RimMushrooms-v" + version + suffix + ".zip")
        with zipfile.ZipFile(target, "w", compression=zipfile.ZIP_DEFLATED) as archive:
            for path in files: archive.write(path, "RimMushrooms/" + path.relative_to(ROOT).as_posix())
        with zipfile.ZipFile(target) as archive:
            if archive.testzip() is not None: raise RuntimeError("ZIP integrity check failed.")
    record = {"version":version,"runtimeFileCount":len(runtime_files),"sourceFileCount":len(source_files),"files":manifest,"archives":{p.name:hashlib.sha256(p.read_bytes()).hexdigest() for p in dest.glob("*.zip")}}
    (dest / "manifest.json").write_text(json.dumps(record,indent=2,ensure_ascii=False),encoding="utf-8")
    print(json.dumps({"release":str(dest),"runtimeFileCount":len(runtime_files),"sourceFileCount":len(source_files)},indent=2))

if __name__ == "__main__": package()
