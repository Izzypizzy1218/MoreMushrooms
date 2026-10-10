"""Merge reviewed photo records and credits; no game files or artwork are edited."""
import json, hashlib
from pathlib import Path
ROOT = Path(__file__).resolve().parent
REGISTRY = ROOT / "species.json"
def main():
    registry = json.loads(REGISTRY.read_text(encoding="utf-8-sig"))
    grouped = []
    for name in ("group1-sources.json", "group2-sources.json"):
        file = ROOT / "references" / name
        if file.exists():
            data = json.loads(file.read_text(encoding="utf-8-sig"))
            grouped.extend(data if isinstance(data, list) else data["photos"])
    by_id = {item["id"]: item for item in grouped}
    photos = []
    for species in registry["species"]:
        source = by_id.get(species["id"])
        if not source:
            continue
        scientific = source["scientific"]
        if species["id"] == "PurpleBlewit":
            species["scientific_aliases"] = ["Lepista sordida"]
            species["ko"] = "자주방망이버섯아재비"
            species["provisional_species_choice"] = True
            scientific = "Collybia sordida"
        species["scientific"] = scientific
        species["selection_note"] = source.get("selection_note", "")
        species["identity_status"] = "photo-verified" if source.get("identity_verified") else "pending-photo-verification"
        species["source_korean_label"] = source.get("ko", "")
        if source.get("provisional_species_choice"):
            species["provisional_species_choice"] = True
        for index, original in enumerate([source] + source.get("additional_references", [])):
            photo = {k: v for k, v in original.items() if k != "additional_references"}
            photo["id"] = species["id"]
            photo["scientific"] = scientific
            photo["page"] = photo.get("source_page", photo.get("page", ""))
            photo["author"] = photo.get("photographer", photo.get("author", ""))
            local = Path(photo.get("local_path", photo.get("file", ""))).resolve()
            local.relative_to(ROOT.resolve())
            if not local.is_file():
                raise FileNotFoundError(local)
            photo["file"] = local.relative_to(ROOT / "references").as_posix()
            photo["sha256"] = hashlib.sha256(local.read_bytes()).hexdigest()
            photo["primary"] = index == 0
            photo["reference_only"] = True
            photo["identity_verified"] = bool(source.get("identity_verified"))
            photos.append(photo)
    result = {"art_version": registry["art_version"], "date": registry["date"],
              "baseline_mod_version": registry["baseline_mod_version"],
              "photos": photos, "species_with_photos": len(by_id),
              "expected_species": registry["expected_species"]}
    (ROOT / "references" / "sources.json").write_text(json.dumps(result, ensure_ascii=False, indent=2)+"\n", encoding="utf-8")
    REGISTRY.write_text(json.dumps(registry, ensure_ascii=False, indent=2)+"\n", encoding="utf-8")
    lines = ["MORE MUSHROOMS EXPANSION ART v0.1 — PHOTO REFERENCES AND ATTRIBUTION",
             "Photographs remain unchanged, used as morphology references only.",
             "They retain their original source licenses; they are not generated game textures.",
             "Generated art: built-in OpenAI ImageGen, individual assets, exact prompts in prompts/.",
             "Existing style: MoreMushrooms v0.4.0 sprites and Sera rimshare produce-container template.",
             "Sera rimshare MIT license copied in RIMSHARE-LICENSE.txt.",
             "Game runtime and existing 15 species remain unchanged.", ""]
    for photo in photos:
        species = next(s for s in registry["species"] if s["id"] == photo["id"])
        lines += [photo["id"] + " — " + photo["scientific"],
                  "File: " + photo["file"], "Photo by: " + photo["author"],
                  "License: " + photo.get("license", ""),
                  "License URL: " + photo.get("license_url", ""),
                  "Source: " + photo["page"],
                  "Image: " + photo.get("image_url", ""), ""]
        if species.get("provisional_species_choice"):
            lines += ["Species selection: provisional art-draft representative; not a final implemented game species.", ""]
        if photo["id"] == "Sanghuang":
            lines += ["Morphology reference: ONLY panel C of Figure 1 is Sanghuangporus sanghuang; the other five panels show other species.", ""]
    sanghuang = by_id.get("Sanghuang")
    if sanghuang:
        lines += ["INTERMEDIATE RESEARCH COPIES — excluded from the 38 selected photo-reference records",
                  "Files: Sanghuang-Frontiers2019-source.pdf and all Sanghuang-Sanghuangporus-sanghuang-Frontiers2019-* extraction copies.",
                  "Derived without repainting from the same source article; not separate species identifications.",
                  "Credit: " + sanghuang["photographer"],
                  "License: " + sanghuang["license"] + " — " + sanghuang["license_url"],
                  "Source: " + sanghuang["source_page"], ""]
    (ROOT / "PHOTO-CREDITS.txt").write_text("\n".join(lines), encoding="utf-8")
    print(json.dumps({"species_with_photos": len(by_id), "photo_files": len(photos),
                      "photo_verified_species": sum(s["identity_status"]=="photo-verified" for s in registry["species"]),
                      "missing_species": [s["id"] for s in registry["species"] if s["id"] not in by_id]}, ensure_ascii=False))
if __name__ == "__main__":
    main()
