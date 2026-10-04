"""Verify shipped assets against the approved handoff and generated XML links."""
from pathlib import Path
import hashlib
import json
import struct
import xml.etree.ElementTree as ET

ROOT = Path(__file__).resolve().parents[1]
HANDOFF = ROOT.parent / "RimWorld-Mushrooms-Handoff-2026-10-03"
checks = 0

def check(condition, message):
    global checks
    if not condition: raise AssertionError(message)
    checks += 1

def sha(path): return hashlib.sha256(path.read_bytes()).hexdigest()

def main():
    docs = {p: ET.parse(p) for folder in ("About","Defs","Languages") for p in (ROOT / folder).rglob("*.xml")}
    balance = json.loads((ROOT / "Balance/mushrooms.json").read_text(encoding="utf-8"))
    check(ET.parse(ROOT / "About/About.xml").findtext("modVersion") == balance["version"], "consistent release version")
    defs = [n for p, doc in docs.items() if "Defs" in p.parts for n in doc.getroot() if n.tag == "ThingDef" and n.find("defName") is not None]
    thoughts = [n for p, doc in docs.items() if "Defs" in p.parts for n in doc.getroot() if n.tag == "ThoughtDef" and n.find("defName") is not None]
    names = [n.findtext("defName") for n in defs]
    check(len(names) == 21 and len(set(names)) == 21, "21 unique definitions")
    thought_by_name = {n.findtext("defName"): n for n in thoughts}
    check(len(thoughts) == len(thought_by_name) == 10, "10 unique mushroom memories")
    check(balance["common"]["moodDurationHours"] == 6, "six game-hour duration")
    check(len({m["mood"] for m in balance["mushrooms"]}) == 10, "different bonus for each variety")
    for m in balance["mushrooms"]:
        thought_id = "RMush_Ate" + m["id"]
        thought = thought_by_name[thought_id]
        check(float(thought.findtext("durationDays")) == 0.25 and int(thought.findtext("stackLimit")) == 1, "duration and stacking " + thought_id)
        check(float(thought.findtext("stages/li/baseMoodEffect")) == m["mood"] and m["mood"] > 0, "positive configured bonus " + thought_id)
        raw = next(n for n in defs if n.findtext("defName") == "RMush_Raw" + m["id"])
        check(all(raw.findtext("ingestible/" + tag) == thought_id for tag in ("specialThoughtDirect", "specialThoughtAsIngredient")), "raw and cooked links " + thought_id)
    for n in defs:
        target = n.findtext("plant/harvestedThingDef")
        if target: check(target in names, "resolved harvest target " + target)
        path = ROOT / "Textures" / n.findtext("graphicData/texPath")
        files = sorted(path.glob("*.png"))
        check(len(files) == (2 if n.find("plant") is not None else 3), "graphic collection size " + str(path))
    for lang in ("English", "Korean"):
        tags = [n.tag for n in ET.parse(ROOT / "Languages" / lang / "DefInjected/ThingDef/Mushrooms.xml").getroot()]
        check(len(tags) == 42 and len(set(tags)) == 42, lang + " complete translation")
        check(all(n+suffix in tags for n in names for suffix in (".label", ".description")), lang + " translation targets")
        mood_tags = [n.tag for n in ET.parse(ROOT / "Languages" / lang / "DefInjected/ThoughtDef/MushroomEnjoyment.xml").getroot()]
        check(len(mood_tags) == len(set(mood_tags)) == 20 and all(n+suffix in mood_tags for n in thought_by_name for suffix in (".stages.0.label", ".stages.0.description")), lang + " memory translations")
    pngs = list((ROOT / "Textures").rglob("*.png"))
    check(len(pngs) == 52, "52 shipped textures")
    sources = {sha(p) for folder in ("mushrooms-boxed/sprites-256", "mushrooms-growing/textures-256") for p in (HANDOFF / "outputs" / folder).glob("*.png")}
    for p in pngs:
        data = p.read_bytes()
        check(sha(p) in sources, "approved pixels " + str(p))
        check(data[:8] == b"\x89PNG\r\n\x1a\n" and struct.unpack(">II",data[16:24]) == (256,256) and data[25] == 6, "256px RGBA " + str(p))
    check({p.name for p in (ROOT / "Assemblies").glob("*.dll")} == {"RimMushrooms.dll"}, "no game or test DLL shipped")
    print(json.dumps({"status":"PASS", "checks":checks,"xml_files":len(docs),"definitions":len(names)+len(thoughts),"textures":len(pngs)}, indent=2))

if __name__ == "__main__": main()
