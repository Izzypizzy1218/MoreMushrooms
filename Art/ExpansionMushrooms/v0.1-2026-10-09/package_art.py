"""Build an immutable ZIP only after the complete art pack passes validation."""
import hashlib,json,subprocess,sys,zipfile
from pathlib import Path
ROOT=Path(__file__).resolve().parent
def checked(script,*args):
    result=subprocess.run([sys.executable,str(ROOT/script),*args],capture_output=True,text=True,encoding="utf-8")
    if result.returncode:
        raise RuntimeError(script+" failed:\n"+result.stdout[-6000:]+"\n"+result.stderr[-2000:])
    return result.stdout.strip()
def main():
    print(checked("build_assets.py","--validate-only"))
    print(checked("preserve_baseline.py","--validate"))
    manifest=json.loads((ROOT/"manifest.json").read_text(encoding="utf-8"))
    if not manifest.get("complete") or manifest["species_count"]!=31 or manifest["texture_count"]!=155:
        raise ValueError("A partial pack cannot be packaged as complete.")
    species=json.loads((ROOT/"species.json").read_text(encoding="utf-8"))["species"]
    missing_reviews=[s["id"] for s in species if not (ROOT/"reviews"/(s["id"]+".json")).is_file()]
    if missing_reviews:
        raise ValueError("Missing actual visual reviews: "+",".join(missing_reviews))
    tag="MoreMushrooms-art-v0.1-2026-10-09"
    archive=ROOT.parent/(tag+".zip")
    if archive.exists():
        raise FileExistsError("Immutable archive already exists: "+str(archive))
    with zipfile.ZipFile(archive,"x",compression=zipfile.ZIP_DEFLATED,compresslevel=6) as z:
        for path in sorted(ROOT.rglob("*")):
            if path.is_file() and "__pycache__" not in path.parts and path.suffix not in (".pyc",".tmp"):
                z.write(path,tag+"/"+path.relative_to(ROOT).as_posix())
    with zipfile.ZipFile(archive) as z:
        error=z.testzip()
        if error:
            raise ValueError("ZIP CRC mismatch: "+error)
        names=z.namelist()
        counts={
            "masters":sum(n.startswith(tag+"/masters/") and n.endswith(".png") for n in names),
            "textures":sum(n.startswith(tag+"/Textures/") and n.endswith(".png") for n in names)}
        if counts!={"masters":155,"textures":155}:
            raise ValueError("ZIP sprite count mismatch: "+str(counts))
    digest=hashlib.sha256(archive.read_bytes()).hexdigest()
    archive.with_suffix(".sha256.txt").write_text(digest+"  "+archive.name+"\n",encoding="utf-8")
    print(json.dumps({"archive":str(archive),"bytes":archive.stat().st_size,"sha256":digest,
                      "crc":"pass","master_count":counts["masters"],"texture_count":counts["textures"]},ensure_ascii=False))
if __name__=="__main__":
    main()
