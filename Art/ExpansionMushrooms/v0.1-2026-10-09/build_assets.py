"""Mechanical artwork export, provenance audit, and offline comparison.

No drawing, recoloring, alpha removal, synthesis, game installation, or Def edits.
Matches the approved poison-mushroom exporter: alpha>=16 bounds, LANCZOS,
plant longest edge 192/bottom224; item width226/bottom217; 256x256 RGBA.

Inputs relative to this NEW art folder:
  species.json: {"species": [{"id": "Identifier", "ko": "name", "scientific": "..."}]}
  masters/<id>/{PlantA,PlantB,01Low,02Medium,03Full}.png
  prompts/<id>.json: prior-art compatible array of five generation records
  references/sources.json: {"photos": [{"id", "scientific", "file", "page", "author", "license", ...}]}
Photo files are relative to references/. Prompt records require asset id/kind,
prompt, referenced_image_paths, transparent_background=true and builtin mode.
Photo species verification is human research; this script checks its record,
not whether a photograph actually depicts the named species.

Examples: python build_assets.py --audit-inputs
          python build_assets.py --partial --species Identifier AnotherIdentifier
          python build_assets.py
          python build_assets.py --validate-only
          python build_assets.py --self-test
"""
from pathlib import Path
from PIL import Image
import argparse
import hashlib
import html
import json
import math
import re
import shutil
import sys

ROOT = Path(__file__).resolve().parent
PROJECT = ROOT.parents[2]
EXPECTED_SPECIES = 31
KINDS = ("PlantA", "PlantB", "01Low", "02Medium", "03Full")
GROWTH = (0.25, 0.50, 0.75, 1.00)
IDENTIFIER = re.compile(r"^[A-Za-z][A-Za-z0-9_]*$")
BASELINE_IDS = ("Button", "Shiitake", "Oyster", "KingOyster", "Enoki", "WoodEar", "Beech", "Maitake", "LionsMane", "Matsutake", "FlyAgaric", "DestroyingAngel", "SulfurTuft", "Tsukiyotake", "PoisonFireCoral")


def sha256(path):
    return hashlib.sha256(Path(path).read_bytes()).hexdigest()


def scoped(relative):
    path = (ROOT / relative).resolve()
    if not path.is_relative_to(ROOT):
        raise ValueError("Path leaves the new art folder: " + str(relative))
    return path


def read_json(path):
    return json.loads(path.read_text(encoding="utf-8-sig"))


def write_json(path, value):
    path = scoped(path.relative_to(ROOT))
    path.parent.mkdir(parents=True, exist_ok=True)
    path.write_text(json.dumps(value, ensure_ascii=False, indent=2) + "\n", encoding="utf-8")


def species_registry():
    value = read_json(ROOT / "species.json")
    rows = value if isinstance(value, list) else value.get("species", [])
    seen = set()
    for row in rows:
        identity = row.get("id", "")
        if not IDENTIFIER.fullmatch(identity) or identity in seen:
            raise ValueError("Invalid/duplicate species id: " + str(identity))
        if identity in BASELINE_IDS or identity == "EnokiWild":
            raise ValueError("Expansion cannot replace an existing species: " + identity)
        if not row.get("scientific") or not (row.get("ko") or row.get("name")):
            raise ValueError("Species needs scientific and ko/name: " + identity)
        seen.add(identity)
    return rows


def normalize(path, plant):
    with Image.open(path) as original:
        if original.mode != "RGBA":
            raise ValueError("Master must already be genuine RGBA: " + str(path))
        src = original.copy()
    alpha = src.getchannel("A")
    if alpha.getextrema() != (0, 255):
        raise ValueError("Master must contain transparent and opaque pixels: " + str(path))
    box = alpha.point(lambda a: 255 if a >= 16 else 0).getbbox()
    if not box:
        raise ValueError("Empty alpha content: " + str(path))
    cropped = src.crop(box)
    scale = 192 / max(cropped.size) if plant else 226 / cropped.width
    size = tuple(max(1, round(v * scale)) for v in cropped.size)
    sprite = cropped.resize(size, Image.Resampling.LANCZOS)
    bottom = 224 if plant else 217
    origin = (round(128 - sprite.width / 2), bottom - sprite.height)
    if min(origin) < 0 or origin[0] + sprite.width > 256 or origin[1] + sprite.height > 256:
        raise ValueError("Artwork exceeds established framing; regenerate the master: " + str(path))
    out = Image.new("RGBA", (256, 256))
    out.alpha_composite(sprite, origin)
    return out, {"source_size": list(src.size), "source_alpha_bbox16": list(box),
                 "export_origin": list(origin), "export_size": list(size),
                 "alpha_range": list(out.getchannel("A").getextrema())}


def target_for(identity, kind):
    plant = kind.startswith("Plant")
    filename = identity + kind[-1] if plant else kind
    return scoped(Path("Textures/Things") / ("Plant" if plant else "Item") / "RimMushrooms" / identity / (filename + ".png"))


def input_audit(rows):
    photos_path = ROOT / "references/sources.json"
    photos = read_json(photos_path).get("photos", []) if photos_path.exists() else []
    audit, errors = [], []
    for row in rows:
        identity = row["id"]
        current = {"id": identity, "masters": [], "prompts": [], "photos": [], "errors": []}
        prompts_path = ROOT / "prompts" / (identity + ".json")
        prompts = read_json(prompts_path) if prompts_path.exists() else []
        if isinstance(prompts, dict):
            prompts = prompts.get("assets", prompts.get("prompts", []))
        relevant_photos = [photo for photo in photos if photo.get("id") == identity]
        for photo in relevant_photos:
            try:
                photo = dict(photo)
                for canonical, alias in (("file", "local_path"), ("page", "source_page"), ("author", "photographer")):
                    if not photo.get(canonical) and photo.get(alias):
                        photo[canonical] = photo[alias]
                for field in ("scientific", "file", "page", "author", "license"):
                    if not photo.get(field):
                        raise ValueError("photo record missing " + field)
                accepted_names = {name.strip().casefold() for name in [row["scientific"], *row.get("scientific_aliases", [])]}
                if photo["scientific"].strip().casefold() not in accepted_names:
                    raise ValueError("photo scientific name differs; list reviewed synonyms in scientific_aliases")
                if not photo["page"].startswith(("http://", "https://")):
                    raise ValueError("photo record needs its original source page URL")
                verified = photo.get("identity_verified") is True or row.get("identity_status") in (
                    "photo-verified", "verified-photo", "verified-from-photo-sources", "verified")
                if photo.get("identity_verified") is False or not verified:
                    raise ValueError("photo identity review is not recorded: identity_verified=true or registry identity_status=photo-verified required")
                relative = Path(photo["file"])
                source = scoped(relative if relative.parts[0] == "references" else Path("references") / relative)
                with Image.open(source) as img:
                    img.verify()
                current["photos"].append({**photo, "file": source.relative_to(ROOT).as_posix(), "sha256": sha256(source),
                                          "identity_review_recorded": verified})
            except (ValueError, OSError, KeyError) as error:
                current["errors"].append(str(error))
        if not current["photos"]:
            current["errors"].append("no complete local real-photo/source record")
        for kind in KINDS:
            master = ROOT / "masters" / identity / (kind + ".png")
            try:
                _, metadata = normalize(master, kind.startswith("Plant"))
                current["masters"].append({"kind": kind, "file": master.relative_to(ROOT).as_posix(),
                                           "sha256": sha256(master), **metadata})
            except (ValueError, OSError) as error:
                current["errors"].append(kind + ": " + str(error))
            matching = [p for p in prompts if p.get("asset") in (identity + "/" + kind, kind)]
            if len(matching) != 1:
                current["errors"].append(kind + ": exactly one final prompt record required")
                continue
            prompt = matching[0]
            if not prompt.get("prompt") or not prompt.get("referenced_image_paths") or prompt.get("transparent_background") is not True:
                current["errors"].append(kind + ": incomplete prompt/refs/transparency record")
            if prompt.get("mode", "") not in ("built-in-imagegen", "built-in ImageGen", "built-in"):
                current["errors"].append(kind + ": built-in ImageGen mode must be recorded")
            for reference in prompt.get("referenced_image_paths", []):
                reference_path = Path(reference)
                if not reference_path.is_absolute():
                    reference_path = scoped(reference_path)
                if not reference_path.is_file():
                    current["errors"].append(kind + ": missing generation reference " + str(reference))
            current["prompts"].append({"kind": kind, "record": prompt})
        current["complete"] = not current["errors"]
        if prompts_path.exists():
            current["prompt_file"] = prompts_path.relative_to(ROOT).as_posix()
            current["prompt_file_sha256"] = sha256(prompts_path)
        errors.extend(identity + ": " + error for error in current["errors"])
        audit.append(current)
    return audit, errors


def baseline_references():
    entries = []
    for identity in BASELINE_IDS:
        for kind in KINDS:
            plant = kind.startswith("Plant")
            name = identity + kind[-1] if plant else kind
            source = PROJECT / "Textures/Things" / ("Plant" if plant else "Item") / "RimMushrooms" / identity / (name + ".png")
            if not source.is_file():
                raise FileNotFoundError("Established style reference is missing: " + str(source))
            target = scoped(Path("references/style/runtime") / identity / (kind + ".png"))
            target.parent.mkdir(parents=True, exist_ok=True)
            shutil.copy2(source, target)
            entries.append({"species": identity, "kind": kind, "source": str(source),
                            "file": target.relative_to(ROOT).as_posix(), "sha256": sha256(target),
                            "copied_byte_for_byte": sha256(source) == sha256(target)})
    return entries


def write_preview(rows, entries, baseline, complete):
    payload = {"rows": rows, "files": entries, "baseline": baseline, "complete": complete}
    data = json.dumps(payload, ensure_ascii=False).replace("<", "\\u003c")
    page = """<!doctype html><html lang="ko"><meta charset="utf-8"><meta name="viewport" content="width=device-width,initial-scale=1">
<title>More Mushrooms · 확장 아트 비교</title><style>
body{margin:0;background:#181d18;color:#f1eee3;font:15px/1.5 system-ui,sans-serif}header{padding:24px;position:sticky;top:0;background:#181d18ee;z-index:1}main{padding:0 24px 36px;max-width:1400px;margin:auto}h1{font-size:24px;margin:0}h2{margin:28px 0 12px}.muted{color:#b8bdaf}button,input{font:inherit;background:#343b31;color:#fff;border:1px solid #68755f;border-radius:6px;padding:7px 12px}button{cursor:pointer}.card{padding:20px 0;border-bottom:1px solid #49513f}.row{display:grid;grid-template-columns:repeat(5,minmax(0,1fr));gap:10px}.growth{grid-template-columns:repeat(4,minmax(0,1fr))}figure{margin:0;text-align:center}img{width:100%;max-width:256px;aspect-ratio:1;object-fit:contain;background:repeating-conic-gradient(#323b30 0% 25%,#252e24 0% 50%) 0/20px 20px;border-radius:8px}.scene{position:relative;aspect-ratio:1;overflow:hidden;background:#554834;border-radius:8px}.scene img{position:absolute;background:none;border:0;border-radius:0;max-width:none}.tag{font-size:12px;color:#d3dfbe}.baseline-grid{display:grid;grid-template-columns:repeat(3,minmax(0,1fr));gap:24px}small{display:block}.status{font-weight:700;color:#efd498}@media(max-width:700px){header{position:static}.row{grid-template-columns:repeat(3,minmax(0,1fr))}.growth{grid-template-columns:repeat(2,minmax(0,1fr))}.baseline-grid{grid-template-columns:1fr}main{padding:0 12px 24px}}
</style><header><h1>More Mushrooms · 확장 아트 비교</h1><p class="status" id="status"></p><p class="muted">성장 25·50·75·100%는 native 규칙을 이용한 배치 시뮬레이션이며 게임 화면 캡처가 아닙니다. A/B는 성체 형태의 변형이며 성장 단계가 아닙니다.</p><input id="filter" placeholder="종 이름 검색" aria-label="종 이름 검색"> <button id="baseline-toggle" type="button">기존 15종 비교 열기</button></header><main><section id="baseline" hidden><h2>기존 15종 원본 스타일 — 복사본, 수정 없음</h2><div class="baseline-grid" id="baseline-cards"></div></section><section id="cards"></section></main><script type="application/json" id="data">__DATA__</script><script>
const D=JSON.parse(document.querySelector('#data').textContent);const kinds=['01Low','02Medium','03Full','PlantA','PlantB'];
const path=(id,k,list=D.files)=>list.find(f=>f.species===id&&f.kind===k)?.file;
const img=(src)=>{const i=document.createElement('img');i.src=src;i.alt='';i.loading='lazy';return i};
const fig=(src,label)=>{const f=document.createElement('figure');f.append(img(src));const c=document.createElement('figcaption');c.textContent=label;f.append(c);return f};
function scene(id,g){const f=document.createElement('figure'),s=document.createElement('div');s.className='scene';const slots=[4,0,8,2,6,1,7,3,5];const n=Math.max(1,Math.ceil(9*g)),size=.3+.35*g;slots.slice(0,n).map((slot,j)=>({j,x:(slot%3+.5)/3,z:(Math.floor(slot/3)+.5)/3})).sort((a,b)=>a.z-b.z).forEach(({j,x,z})=>{const i=img(path(id,j%2?'PlantB':'PlantA'));const w=size*68;i.style.width=w+'%';i.style.left=(16+x*68-w/2)+'%';i.style.top=(13+z*65-w*.80)+'%';if(j%3===0)i.style.transform='scaleX(-1)';s.append(i)});f.append(s);const c=document.createElement('figcaption');c.textContent=`${Math.round(g*100)}% · ${n}개 · ${size.toFixed(4)}칸`;f.append(c);return f}
function card(row,list,withGrowth){const a=document.createElement('article');a.className='card';a.dataset.search=[row.id,row.ko,row.name,row.en,row.scientific].filter(Boolean).join(' ').toLowerCase();const h=document.createElement('h2');h.textContent=row.ko||row.name||row.id;a.append(h);const p=document.createElement('p');p.className='muted';p.textContent=row.scientific||row.id;a.append(p);if(row.provisional_species_choice){const note=document.createElement('p');note.className='tag';note.textContent='그림 초안의 잠정 대표종';a.append(note)}const r=document.createElement('div');r.className='row';kinds.forEach((k,j)=>r.append(fig(path(row.id,k,list),['Low · 1~25','Medium · 26~50','Full · 51~75','성체 변형 A','성체 변형 B'][j])));a.append(r);if(withGrowth){const gr=document.createElement('div');gr.className='row growth';gr.style.marginTop='12px';[.25,.5,.75,1].forEach(g=>gr.append(scene(row.id,g)));a.append(gr)}return a}
const done=D.rows.filter(r=>kinds.every(k=>path(r.id,k)));done.forEach(r=>document.querySelector('#cards').append(card(r,D.files,true)));document.querySelector('#status').textContent=(D.complete?'완성 검증 완료':'진행 중 · 전체 31종 완성 아님')+` — 내보낸 ${done.length}종 / ${D.files.length}개 텍스처`;
[...new Set(D.baseline.map(x=>x.species))].forEach(id=>document.querySelector('#baseline-cards').append(card({id},D.baseline,false)));
document.querySelector('#baseline-toggle').onclick=()=>{const el=document.querySelector('#baseline');el.hidden=!el.hidden};document.querySelector('#filter').oninput=e=>{const q=e.target.value.toLowerCase();document.querySelectorAll('#cards .card').forEach(el=>el.hidden=!el.dataset.search.includes(q))};
</script></html>""".replace("__DATA__", data)
    scoped("comparison.html").write_text(page, encoding="utf-8")


def validate_exports(manifest):
    errors = []
    for entry in manifest["files"]:
        target = scoped(entry["file"])
        try:
            with Image.open(target) as image:
                if image.mode != "RGBA" or image.size != (256, 256) or image.getchannel("A").getextrema() != (0, 255):
                    errors.append("Export RGBA/size/alpha failure: " + entry["file"])
            if sha256(target) != entry["sha256"]:
                errors.append("Changed export hash: " + entry["file"])
            if sha256(scoped(entry["source_file"])) != entry["source_sha256"]:
                errors.append("Changed master hash: " + entry["source_file"])
        except (OSError, ValueError) as error:
            errors.append(str(error))
    expected = {entry["file"] for entry in manifest["files"]}
    actual = {path.relative_to(ROOT).as_posix() for path in scoped("Textures").rglob("*.png")}
    if manifest["complete"] and expected != actual:
        errors.append("Staging texture file set differs from complete manifest")
    if len(expected) != len(manifest["files"]):
        errors.append("Duplicate export entries")
    if manifest["texture_count"] != len(manifest["files"]) or manifest["texture_count"] != 5 * manifest["species_count"]:
        errors.append("Per-species five-asset count mismatch")
    for key, relative in (("species_registry_sha256", "species.json"), ("photo_sources_sha256", "references/sources.json"), ("comparison_sha256", "comparison.html")):
        if manifest.get(key) and sha256(scoped(relative)) != manifest[key]:
            errors.append("Changed provenance/preview hash: " + relative)
    for species in manifest["input_audit"]:
        if species.get("prompt_file") and sha256(scoped(species["prompt_file"])) != species["prompt_file_sha256"]:
            errors.append("Changed prompt hash: " + species["prompt_file"])
        for photo in species["photos"]:
            if sha256(scoped(photo["file"])) != photo["sha256"]:
                errors.append("Changed real-photo hash: " + photo["file"])
    for reference in manifest["baseline_references"]:
        if sha256(scoped(reference["file"])) != reference["sha256"]:
            errors.append("Changed approved style reference hash: " + reference["file"])
    return errors


def self_test():
    previous = PROJECT / "Art/PoisonMushrooms/Handoff-2026-10-05/art"
    checked = 0
    for identity in ("FlyAgaric", "DestroyingAngel", "SulfurTuft", "Tsukiyotake", "PoisonFireCoral"):
        for kind in KINDS:
            image, metadata = normalize(previous / "masters" / identity / (kind + ".png"), kind.startswith("Plant"))
            old = PROJECT / "Textures/Things" / ("Plant" if kind.startswith("Plant") else "Item") / "RimMushrooms" / identity / ((identity + kind[-1] if kind.startswith("Plant") else kind) + ".png")
            with Image.open(old) as expected:
                if image.tobytes() != expected.convert("RGBA").tobytes():
                    raise AssertionError("Mechanical export differs from approved prior pixels: " + str(old))
            if metadata["alpha_range"] != [0, 255]:
                raise AssertionError("Alpha invariant failed")
            checked += 1
    print(json.dumps({"self_test": "PASS", "prior_export_pixel_matches": checked, "writes": 0}))


def main():
    parser = argparse.ArgumentParser(description=__doc__, formatter_class=argparse.RawDescriptionHelpFormatter)
    parser.add_argument("--partial", action="store_true", help="Allow incomplete inputs; manifest/HTML remain explicitly unfinished")
    parser.add_argument("--species", nargs="+", help="Build this subset only; requires --partial")
    parser.add_argument("--audit-inputs", action="store_true", help="Read-only audit; no output files written")
    parser.add_argument("--validate-only", action="store_true", help="Read-only manifest/input/hash verification")
    parser.add_argument("--self-test", action="store_true", help="Read-only compare 25 prior master exports pixel-for-pixel")
    args = parser.parse_args()
    if args.self_test:
        self_test()
        return 0
    rows = species_registry()
    if args.species:
        if not args.partial:
            parser.error("--species requires --partial")
        unknown = set(args.species) - {r["id"] for r in rows}
        if unknown:
            parser.error("Unknown species: " + ", ".join(sorted(unknown)))
        rows = [r for r in rows if r["id"] in args.species]
    audit, errors = input_audit(rows)
    if len(rows) != EXPECTED_SPECIES and not args.partial:
        errors.append("Expected exactly 31 expansion species; found " + str(len(rows)))
    if args.audit_inputs:
        print(json.dumps({"expected_species": EXPECTED_SPECIES, "checked_species": len(rows),
                          "complete_species": sum(a["complete"] for a in audit), "errors": errors, "audit": audit}, ensure_ascii=False, indent=2))
        return 0 if not errors else 2
    if args.validate_only:
        manifest = read_json(ROOT / "manifest.json")
        errors += validate_exports(manifest)
        if not manifest["complete"]:
            errors.append("Manifest remains incomplete")
        print(json.dumps({"valid": not errors, "errors": errors}, ensure_ascii=False, indent=2))
        return 0 if not errors else 2
    if errors and not args.partial:
        raise ValueError("Inputs incomplete; no textures written:\n" + "\n".join(errors))
    ready_ids = {a["id"] for a in audit if a["complete"]}
    ready = [r for r in rows if r["id"] in ready_ids]
    entries = []
    for row in ready:
        for kind in KINDS:
            master = scoped(Path("masters") / row["id"] / (kind + ".png"))
            source_hash = sha256(master)
            image, metadata = normalize(master, kind.startswith("Plant"))
            target = target_for(row["id"], kind)
            target.parent.mkdir(parents=True, exist_ok=True)
            image.save(target, optimize=True)
            entries.append({"species": row["id"], "kind": kind, "file": target.relative_to(ROOT).as_posix(),
                            "sha256": sha256(target), "dimensions": [256, 256], "mode": "RGBA",
                            "source_file": master.relative_to(ROOT).as_posix(), "source_sha256": source_hash, **metadata})
            if sha256(master) != source_hash:
                raise ValueError("Master changed during build: " + str(master))
    baseline = baseline_references()
    complete = len(ready) == EXPECTED_SPECIES and len(entries) == EXPECTED_SPECIES * len(KINDS) and not errors
    manifest = {"date": "2026-10-09", "art_version": "v0.1", "deliverable": "art only; runtime unmodified",
                "generator": "built-in ImageGen; individual prompt provenance required", "complete": complete,
                "expected_species_count": EXPECTED_SPECIES, "species_count": len(ready), "texture_count": len(entries),
                "expected_texture_count": EXPECTED_SPECIES * len(KINDS), "files": entries, "input_audit": audit,
                "input_errors": errors, "species_registry_sha256": sha256(ROOT / "species.json"),
                "photo_sources_sha256": sha256(ROOT / "references/sources.json") if (ROOT / "references/sources.json").exists() else None,
                "baseline_references": baseline, "mechanical_operations": ["alpha bbox threshold16 crop", "LANCZOS resize", "transparent RGBA packing"],
                "native_growth_preview": {"capture": False, "simulation": True, "variants": "A/B mature alternatives",
                    "mesh_count": "max(1,ceil(9*g))", "plane_size_tiles": "0.3+0.35*g", "draw_size": 1,
                    "stages": [{"growth": g, "meshes": max(1, math.ceil(9 * g)), "plane_tiles": round(.3 + .35 * g, 4)} for g in GROWTH],
                    "limitations": "Illustrative browser projection/layout; native engine seeded positions/materials are not reproduced exactly."}}
    validation_errors = validate_exports(manifest)
    if validation_errors:
        raise ValueError("\n".join(validation_errors))
    write_preview(rows, entries, baseline, complete)
    manifest["comparison_sha256"] = sha256(ROOT / "comparison.html")
    write_json(ROOT / "manifest.json", manifest)
    print(json.dumps({"complete": complete, "species": len(ready), "textures": len(entries), "expected": 155,
                      "preview": str(ROOT / "comparison.html"), "incomplete_input_count": len(errors)}, ensure_ascii=False))
    return 0


if __name__ == "__main__":
    try:
        sys.exit(main())
    except (ValueError, OSError, AssertionError) as error:
        print("ERROR: " + str(error), file=sys.stderr)
        sys.exit(2)
