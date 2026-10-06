"""Verify shipped assets against the approved handoff and generated XML links."""
from pathlib import Path
import hashlib
import json
import re
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

def validate_poison(defs, docs, assorted_choices):
    """Check the new poisoning content independently of the edible balance."""
    config = json.loads((ROOT / "Balance/poison_mushrooms.json").read_text(encoding="utf-8"))
    common = config["common"]
    species = config["mushrooms"]
    ids = {"FlyAgaric", "DestroyingAngel", "SulfurTuft", "Tsukiyotake", "PoisonFireCoral"}
    check(len(species) == 5 and {m["id"] for m in species} == ids, "five approved poison species")
    by_name = {n.findtext("defName"): n for n in defs}
    hediffs = [n for path, doc in docs.items() if "Defs" in path.parts for n in doc.getroot() if n.tag == "HediffDef"]
    categories = [n for path, doc in docs.items() if "Defs" in path.parts for n in doc.getroot() if n.tag == "ThingCategoryDef"]
    check(len(hediffs) == 5 and {n.findtext("defName") for n in hediffs} == {"RMush_Poison" + i for i in ids}, "five unique poisoning definitions")
    check(len(categories) == 1 and categories[0].findtext("defName") == "RMush_PoisonMushrooms" and categories[0].findtext("parent") == "ResourcesRaw", "poison stockpile category outside food tree")
    hediff_by_name = {n.findtext("defName"): n for n in hediffs}
    raw_base = ET.parse(ROOT / "Defs/PoisonMushrooms/PoisonMushrooms.xml").find("ThingDef[@Name='RMush_PoisonRawBase']")
    check(raw_base.get("ParentName") == "OrganicProductBase", "poison resources do not inherit edible-food categories")
    check(common["nutrition"] == 0 and float(raw_base.findtext("statBases/Nutrition")) == 0, "zero nutrition excludes poison from native food search")
    check(raw_base.findtext("ingestible/preferability") == "NeverForNutrition" and raw_base.findtext("ingestible/foodType") == "Fungus", "native fungus flag with never-for-nutrition restriction")
    check(raw_base.findtext("ingestible/drugCategory") == "None" and raw_base.find("comps/li[@Class='CompProperties_Drug']") is None, "poison never participates in automatic drug policies")
    check({n.text for n in raw_base.findall("thingCategories/li")} == {"RMush_PoisonMushrooms"}, "poison excluded from ordinary food recipe category filters")
    check(int(raw_base.findtext("ingestible/maxNumToIngestAtOnce")) == int(raw_base.findtext("ingestible/defaultNumToIngestAtOnce")) == 1, "one mushroom per explicit ingestion")
    check(int(raw_base.findtext("stackLimit")) == common["stackLimit"] == 75, "poison stack limit")
    thresholds = raw_base.find("modExtensions/li[@Class='RimMushrooms.MushroomStackThresholds']")
    check(int(thresholds.findtext("lowMaxCount")) == common["lowMaxCount"] == 25 and int(thresholds.findtext("mediumMaxCount")) == common["mediumMaxCount"] == 50, "poison uses approved three-count graphic thresholds")
    check(common["responseGraceHours"] >= 24 and common["tendDurationHours"] == 6, "fatal response grace and repeat medical-care interval")
    expected_timings = {"FlyAgaric": (1, 0.5, 1), "DestroyingAngel": (8, 3, 5), "SulfurTuft": (3, 2, 3), "Tsukiyotake": (0.5, 1, 2), "PoisonFireCoral": (3, 5, 8)}
    expected_injections = {"ThingDef": {}, "HediffDef": {}, "ThingCategoryDef": {"RMush_PoisonMushrooms.label": categories[0].findtext("label")}}
    for mushroom in species:
        identity = mushroom["id"]
        raw_name, plant_name, poison_name = (prefix + identity for prefix in ("RMush_Raw", "RMush_Plant", "RMush_Poison"))
        raw, plant, hediff = by_name[raw_name], by_name[plant_name], hediff_by_name[poison_name]
        check(raw.get("ParentName") == "RMush_PoisonRawBase", "shared poison ingestion restrictions " + identity)
        check(raw.find("statBases/Nutrition") is None and raw.find("ingestible/preferability") is None and raw.find("thingCategories") is None, "no child override of native food restrictions " + identity)
        outcome = raw.find("ingestible/outcomeDoers/li[@Class='RimMushrooms.IngestionOutcomeDoer_MushroomPoison']")
        check(outcome is not None and outcome.findtext("hediff") == poison_name and int(outcome.findtext("doseUnitCount")) == 1, "correct poisoning ingestion link " + identity)
        check(raw.find("ingestible/specialThoughtDirect") is None and raw.find("ingestible/specialThoughtAsIngredient") is None, "poison grants no edible-mushroom enjoyment " + identity)
        check(float(raw.findtext("statBases/MarketValue")) == mushroom["value"] and float(raw.findtext("comps/li[@Class='CompProperties_Rottable']/daysToRotStart")) == mushroom["rotDays"], "configured poison item value and spoilage " + identity)
        check(plant.get("ParentName") == "RMush_PlantBase" and plant.find("plant/sowTags") is None and plant.find("plant/sowMinSkill") is None, "wild-only poison excluded from sowing and hydroponics " + identity)
        check(plant_name not in assorted_choices, "poison excluded from assorted sowing " + identity)
        check(plant.findtext("plant/humanFoodPlant") == "false" and plant.findtext("plant/purpose") == "Misc" and float(plant.findtext("statBases/Nutrition")) == 0, "wild foliage excluded from grazing and human food search " + identity)
        foliage = plant.find("ingestible")
        check(foliage is not None and foliage.get("Inherit") == "False" and foliage.get("IsNull") == "True" and len(foliage) == 0, "native null ingestible removes invalid or edible wild foliage " + identity)
        check(float(plant.findtext("plant/growDays")) == mushroom["growDays"] and int(plant.findtext("plant/harvestYield")) == mushroom["yield"] and plant.findtext("plant/harvestedThingDef") == raw_name, "configured poison plant growth and harvest " + identity)
        biomes = {n.tag: float(n.text) for n in plant.findall("plant/wildBiomes/*")}
        check(biomes == mushroom["biomes"] and all(0.002 <= w <= 0.006 for w in biomes.values()), "conservative native poison wild selection weights " + identity)
        check(hediff.findtext("hediffClass") == "RimMushrooms.Hediff_MushroomPoisoning" and hediff.findtext("tendable") == "true" and float(hediff.findtext("lethalSeverity")) == -1, "native tending with runtime-controlled lethal grace " + identity)
        tend = hediff.find("comps/li[@Class='HediffCompProperties_TendDuration']")
        check(tend is not None and float(tend.findtext("baseTendDurationHours")) == 6 and float(tend.findtext("tendOverlapHours")) == 0 and float(tend.findtext("severityPerDayTended")) == 0 and tend.find("disappearsAtTotalTendQuality") is None, "repeat care without one-tend instant cure " + identity)
        extension = hediff.find("modExtensions/li[@Class='RimMushrooms.MushroomPoisonSettings']")
        check(extension is not None and extension.find("vomitMtbHours") is None, "native stage vomiting has no duplicate runtime mechanism " + identity)
        settings = mushroom["settings"]
        check((settings["latencyHoursMin"], settings["recoveryDaysMin"], settings["recoveryDaysMax"]) == expected_timings[identity] and settings["latencyHoursMin"] == settings["latencyHoursMax"], "approved latency and recovery design " + identity)
        for key, value in settings.items():
            if key == "vomitMtbHours": continue
            text = extension.findtext(key)
            check(text == str(value).lower() if isinstance(value, bool) else text is not None and float(text) == value, "configured poison extension " + identity + "." + key)
        check(float(extension.findtext("responseGraceHours")) >= 24 and float(extension.findtext("stabilizationTreatment")) == common["stabilizationTreatment"], "response grace and accumulated stabilization treatment " + identity)
        check(settings["fatal"] == (identity in {"DestroyingAngel", "PoisonFireCoral"}), "only approved high-risk poisons are intrinsically fatal " + identity)
        stages = hediff.findall("stages/li")
        check([float(s.findtext("minSeverity")) for s in stages] == [0, 0.1, 0.35, 0.6, 0.85], "ordered poison severity stages " + identity)
        check(stages[0].find("capMods") is None and stages[0].find("painOffset") is None and stages[0].find("vomitMtbDays") is None, "latent stage has no clinical penalties or vomiting " + identity)
        for index, (stage, specification) in enumerate(zip(stages, mushroom["stages"])):
            capacities = {n.findtext("capacity"): float(n.findtext("offset")) for n in stage.findall("capMods/li")}
            check(capacities == specification.get("capacities", {}) and all(-1 < v <= 0 for v in capacities.values()), "configured nonzero surviving-capacity offsets " + identity + "." + str(index))
            check(float(stage.findtext("painOffset", "0")) == specification.get("painOffset", 0), "configured stage pain " + identity + "." + str(index))
            vomit_hours = settings.get("vomitMtbHours", 0)
            expected_vomit = vomit_hours / 24 if index > 0 and vomit_hours > 0 and not (identity == "DestroyingAngel" and index == 2) else 0
            check(float(stage.findtext("vomitMtbDays", "0")) == expected_vomit, "native clinical vomiting and apparent angel remission " + identity + "." + str(index))
            check(float(stage.findtext("statFactors/ImmunityGainSpeed", "1")) == specification.get("immunityGainSpeedFactor", 1), "configured stage immunity gain " + identity + "." + str(index))
            expected_injections["HediffDef"][poison_name + f".stages.{index}.label"] = stage.findtext("label")
        for name, definition in ((raw_name, raw), (plant_name, plant)):
            for suffix in ("label", "description"):
                expected_injections["ThingDef"][name + "." + suffix] = definition.findtext(suffix)
        for suffix in ("ingestCommandString", "ingestReportString"):
            expected_injections["ThingDef"][raw_name + ".ingestible." + suffix] = raw.findtext("ingestible/" + suffix)
        for suffix in ("label", "description"):
            expected_injections["HediffDef"][poison_name + "." + suffix] = hediff.findtext(suffix)
    angel_critical = hediff_by_name["RMush_PoisonDestroyingAngel"].findall("stages/li")[-1]
    check(angel_critical.findtext("capMods/li[capacity='BloodFiltration']/offset") == "-0.5", "angel liver injury uses reversible blood filtration penalty")
    fire_critical = hediff_by_name["RMush_PoisonPoisonFireCoral"].findall("stages/li")[-1]
    check(fire_critical.findtext("statFactors/ImmunityGainSpeed") == "0.5" and fire_critical.find("capMods/li[capacity='BloodFiltration']") is None, "fire coral immune weakness is not mislabeled blood filtration")
    fly_critical = hediff_by_name["RMush_PoisonFlyAgaric"].findall("stages/li")[-1]
    check(float(fly_critical.findtext("capMods/li[capacity='Consciousness']/offset")) == -0.8, "severe repeated fly agaric exposure can cause reversible coma")
    for lang in ("English", "Korean"):
        for def_type, expected in expected_injections.items():
            entries = list(ET.parse(ROOT / "Languages" / lang / "DefInjected" / def_type / "PoisonMushrooms.xml").getroot())
            translated = {n.tag: n.text for n in entries}
            check(len(entries) == len(translated) and set(translated) == set(expected), lang + " complete unique poison " + def_type + " translations")
            check(all(value and value.strip() for value in translated.values()), lang + " nonempty poison " + def_type + " translations")
            if lang == "English":
                check(translated == expected, "English poison text matches definitions " + def_type)
            else:
                for mushroom in species:
                    suffix = "RMush_Poison" if def_type == "HediffDef" else "RMush_Raw"
                    if def_type in ("HediffDef", "ThingDef"):
                        check(translated[suffix + mushroom["id"] + ".label"] == mushroom["ko"] + (" 중독" if def_type == "HediffDef" else ""), "Korean poison labels " + mushroom["id"] + "." + def_type)
        keyed_entries = list(ET.parse(ROOT / "Languages" / lang / "Keyed/PoisonMushrooms.xml").getroot())
        keyed = {n.tag: n.text for n in keyed_entries}
        expected_keyed = {"RMush_PoisonPhase" + p: set() for p in ("Latent", "Worsening", "Stabilized", "Recovering")}
        expected_keyed.update({"RMush_Poison" + event + suffix: ({"{0}", "{1}"} if suffix == "Text" else set()) for event in ("Exposure", "Onset", "Stabilized") for suffix in ("Label", "Text")})
        expected_keyed.update({"RMush_PoisonDangerNote": set()})
        expected_keyed.update({"RMush_PoisonTip" + tip: {"{0}"} for tip in ("Phase", "Onset", "Treatment", "Recovery", "Grace")})
        check(len(keyed_entries) == len(keyed) == len(expected_keyed) and set(keyed) == set(expected_keyed), lang + " complete unique poison notification and health tooltip keys")
        for key, parameters in expected_keyed.items():
            check(bool(keyed[key]) and set(re.findall(r"\{\d+\}", keyed[key])) == parameters, lang + " poison translation placeholders " + key)
    handoff = ROOT / config["handoff"]
    manifest = json.loads((handoff / "art/manifest.json").read_text(encoding="utf-8-sig"))
    approved = [entry for entry in manifest["files"] if entry["file"].startswith("Textures/")]
    check(len(approved) == 25 and {entry["species"] for entry in approved} == ids, "approved five-species 25-texture handoff")
    check(len({entry["file"] for entry in approved}) == 25, "approved unique poison texture paths")
    for entry in approved:
        source = handoff / "art" / entry["file"]
        installed = ROOT / entry["file"]
        check(sha(source) == sha(installed) == entry["sha256"], "exact approved poison PNG hash " + entry["file"])
        check(entry["dimensions"] == [256, 256] and entry["mode"] == "RGBA", "approved poison texture format " + entry["file"])
    return len(hediffs) + len(categories)

def main():
    docs = {p: ET.parse(p) for folder in ("About","Defs","Languages","Patches") for p in (ROOT / folder).rglob("*.xml")}
    balance = json.loads((ROOT / "Balance/mushrooms.json").read_text(encoding="utf-8"))
    check(ET.parse(ROOT / "About/About.xml").findtext("modVersion") == balance["version"], "consistent release version")
    cover_record = json.loads((ROOT / "Art/Covers/v0.3.1/cover-v1-generation.json").read_text(encoding="utf-8"))
    cover = ROOT / "About/Preview.png"
    source_cover = ROOT / "Art/Covers/v0.3.1/MoreMushrooms-cover-v1.jpg"
    check(cover.read_bytes() == source_cover.read_bytes(), "approved Ratkin cover configured at native preview path")
    check(sha(cover) == cover_record["outputs"]["MoreMushrooms-cover-v1.jpg"]["sha256"], "approved cover hash")
    check(cover.read_bytes()[:3] == b"\xff\xd8\xff" and cover.stat().st_size < 1000000, "native-supported JPEG preview below upload size limit")
    defs = [n for p, doc in docs.items() if "Defs" in p.parts for n in doc.getroot() if n.tag == "ThingDef" and n.find("defName") is not None]
    thoughts = [n for p, doc in docs.items() if "Defs" in p.parts for n in doc.getroot() if n.tag == "ThoughtDef" and n.find("defName") is not None]
    names = [n.findtext("defName") for n in defs]
    check(len(names) == 32 and len(set(names)) == 32, "22 existing plus 10 poison plant/item definitions")
    legacy_names = {"RMush_Raw" + m["id"] for m in balance["mushrooms"]} | {"RMush_Plant" + m["id"] for m in balance["mushrooms"]} | {"RMush_PlantEnokiWild", "RMush_PlantAssorted"}
    assorted = next(n for n in defs if n.findtext("defName") == "RMush_PlantAssorted")
    choices = [n.text for n in assorted.findall("modExtensions/li[@Class='RimMushrooms.AssortedMushroomSettings']/varieties/li")]
    check(len(choices) == len(set(choices)) == 9 and set(choices) == {"RMush_Plant" + m["id"] for m in balance["mushrooms"] if not m.get("wildOnly", False)}, "assorted chooses exactly nine cultivable species")
    check(int(assorted.findtext("plant/sowMinSkill")) == max(m["skill"] for m in balance["mushrooms"] if not m.get("wildOnly", False)), "assorted requires skill for all nine species")
    check(assorted.find("plant/wildBiomes") is None, "assorted menu selection never spawns wild")
    check({n.text for n in assorted.findall("plant/sowTags/li")} == {"Ground", "Hydroponic"}, "assorted supports soil and hydroponics")
    icon = ROOT / "Textures/UI/Icons/AssortedMushrooms.png"
    check(assorted.findtext("uiIconPath") == "UI/Icons/AssortedMushrooms" and icon.is_file(), "dedicated assorted crop selection icon")
    check(icon.read_bytes() == (ROOT / "Art/Icons/AssortedMushrooms.png").read_bytes(), "assorted icon matches preserved generated source")
    record = json.loads((ROOT / "Art/Icons/AssortedMushrooms.json").read_text(encoding="utf-8"))
    check(sha(icon) == record["sha256"] and record["species"] == ["Button", "LionsMane", "Shiitake", "Beech"], "assorted icon provenance and four requested species")
    icon_data = icon.read_bytes()
    icon_size = struct.unpack(">II", icon_data[16:24])
    check(icon_data[:8] == b"\x89PNG\r\n\x1a\n" and icon_size[0] == icon_size[1] and 256 <= icon_size[0] <= 2048 and icon_data[24:26] == b"\x08\x06", "square RGBA assorted icon")
    for m in balance["mushrooms"]:
        p = next(n for n in defs if n.findtext("defName") == "RMush_Plant" + m["id"])
        tags = {n.text for n in p.findall("plant/sowTags/li")}
        check(tags == (set() if m.get("wildOnly", False) else {"Ground", "Hydroponic"}), "cultivation tags " + m["id"])
        check(float(p.findtext("plant/growDays")) == m["growDays"] and int(p.findtext("plant/harvestYield")) == m["yield"], "configured growth days and yield " + m["id"])
    wild_enoki = next(n for n in defs if n.findtext("defName") == "RMush_PlantEnokiWild")
    check(wild_enoki.find("plant/sowTags") is None, "wild enoki stays unsowable")
    patch = ET.parse(ROOT / "Patches/AssortedMushroomWork.xml")
    check(len(patch.findall("Operation/match/value/giverClass")) == 2, "scoped native sow and harvest workers shipped")
    plant_base = ET.parse(ROOT / "Defs/ThingDefs_Plants/Mushrooms.xml").find("ThingDef[@Name='RMush_PlantBase']")
    check(plant_base.findtext("thingClass") == "RimMushrooms.Plant_Mushroom", "shade-aware mushroom plant class")
    light = plant_base.find("modExtensions/li[@Class='RimMushrooms.MushroomLightSettings']")
    check(float(light.findtext("shadeMaxGlow")) == balance["common"]["shadeMaxGlow"] and 0 <= balance["common"]["shadeMaxGlow"] < 1, "configured shade threshold")
    check(float(light.findtext("fullLightGrowthFactor")) == balance["common"]["fullLightGrowthFactor"] and 0 < balance["common"]["fullLightGrowthFactor"] <= 1, "configured bright light multiplier")
    thought_by_name = {n.findtext("defName"): n for n in thoughts}
    check(len(thoughts) == len(thought_by_name) == 10, "10 unique mushroom memories")
    check(balance["common"]["moodDurationHours"] == 6, "six game-hour duration")
    check(sorted(m["mood"] for m in balance["mushrooms"]) == [3,3,3,5,5,5,7,7,7,10], "three mood groups and one premium mushroom")
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
        check(len(tags) == 44 and len(set(tags)) == 44, lang + " complete translation")
        check(all(n+suffix in tags for n in legacy_names for suffix in (".label", ".description")), lang + " existing translation targets")
        mood_tags = [n.tag for n in ET.parse(ROOT / "Languages" / lang / "DefInjected/ThoughtDef/MushroomEnjoyment.xml").getroot()]
        check(len(mood_tags) == len(set(mood_tags)) == 20 and all(n+suffix in mood_tags for n in thought_by_name for suffix in (".stages.0.label", ".stages.0.description")), lang + " memory translations")
        check("{0}" in ET.parse(ROOT / "Languages" / lang / "Keyed/Mushrooms.xml").findtext("MM_BrightLightGrowthFactor"), lang + " growth tooltip translation")
    pngs = list((ROOT / "Textures").rglob("*.png"))
    check(len(pngs) == 78, "52 original textures, assorted selection icon, and 25 poison textures")
    sources = {sha(p) for folder in ("mushrooms-boxed/sprites-256", "mushrooms-growing/textures-256") for p in (HANDOFF / "outputs" / folder).glob("*.png")}
    sources.update(sha(p) for p in (ROOT / "Art/PoisonMushrooms").rglob("*.png") if p.read_bytes()[16:24] == struct.pack(">II", 256, 256))
    for p in (p for p in pngs if p != icon):
        data = p.read_bytes()
        check(sha(p) in sources, "approved pixels " + str(p))
        check(data[:8] == b"\x89PNG\r\n\x1a\n" and struct.unpack(">II",data[16:24]) == (256,256) and data[25] == 6, "256px RGBA " + str(p))
    check({p.name for p in (ROOT / "Assemblies").glob("*.dll")} == {"RimMushrooms.dll"}, "no game or test DLL shipped")
    poison_extra_defs = validate_poison(defs, docs, choices)
    print(json.dumps({"status":"PASS", "checks":checks,"xml_files":len(docs),"definitions":len(names)+len(thoughts)+poison_extra_defs,"textures":len(pngs)}, indent=2))

if __name__ == "__main__": main()
