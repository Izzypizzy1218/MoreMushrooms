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
    additional = config.get("additionalPoisons", [])
    additional_ids = {"PantherCap", "DeathCap", "YellowDapperling", "JackOLantern", "DeadlyWebcap", "GhostFungus"}
    check(len(additional) == 6 and {m["id"] for m in additional} == additional_ids, "six approved expansion poisoning conditions")
    hediffs = [n for path, doc in docs.items() if "Defs" in path.parts for n in doc.getroot() if n.tag == "HediffDef" and n.findtext("defName", "").startswith("RMush_Poison")]
    categories = [n for path, doc in docs.items() if "Defs" in path.parts for n in doc.getroot() if n.tag == "ThingCategoryDef" and n.findtext("defName") == "RMush_PoisonMushrooms"]
    check(len(hediffs) == 11 and {n.findtext("defName") for n in hediffs} == {"RMush_Poison" + i for i in ids | additional_ids}, "eleven unique poisoning definitions")
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
    for mushroom in species + additional:
        settings = mushroom["settings"]
        check(0 < settings.get("minimumFatalExposureUnits", .25) <= 4, "fractional fatal exposure threshold remains positive " + mushroom["id"])
    expected_timings = {"FlyAgaric": (1, 0.5, 1), "DestroyingAngel": (8, 3, 5), "SulfurTuft": (3, 2, 3), "Tsukiyotake": (0.5, 1, 2), "PoisonFireCoral": (3, 5, 8)}
    expected_injections = {"ThingDef": {}, "HediffDef": {}, "ThingCategoryDef": {"RMush_PoisonMushrooms.label": categories[0].findtext("label")}}
    for mushroom in species:
        identity = mushroom["id"]
        raw_name, plant_name, poison_name = (prefix + identity for prefix in ("RMush_Raw", "RMush_Plant", "RMush_Poison"))
        raw, plant, hediff = by_name[raw_name], by_name[plant_name], hediff_by_name[poison_name]
        psychedelic = identity == "FlyAgaric"
        check(raw.get("ParentName") == ("RMush_RawBase" if psychedelic else "RMush_PoisonRawBase"), "shared food or poison parent " + identity)
        check(raw.find("statBases/Nutrition") is None and raw.find("ingestible/preferability") is None and raw.find("thingCategories") is None, "no child override of native food restrictions " + identity)
        if psychedelic:
            outcome = raw.find("ingestible/outcomeDoers/li[@Class='RimMushrooms.IngestionOutcomeDoer_MushroomExposure']")
            exposure = raw.find("modExtensions/li[@Class='RimMushrooms.MushroomExposureProperties']")
            check(outcome is not None and exposure is not None and exposure.findtext("poisonHediff") == poison_name and exposure.findtext("psychoactive") == "true", "fly agaric raw and cooked psychoactive exposure")
            check(float(exposure.findtext("doseUnitCount")) == 10 and float(exposure.findtext("moodBonus")) == 10 and float(exposure.findtext("moodDurationHours")) == 6, "configured fly agaric standard exposure")
            check((float(exposure.findtext("hallucinationHoursMin")), float(exposure.findtext("hallucinationHoursMax"))) == (2, 3), "fly agaric control loss lasts two to three hours")
        else:
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
        check(settings["fatal"] == (identity in {"DestroyingAngel", "PoisonFireCoral"}), "legacy intrinsic fatality preserved " + identity)
        check(settings.get("conditionalFatalThreshold", 0) == (3 if identity in {"FlyAgaric", "SulfurTuft"} else 0), "high exposure fatality restricted to approved legacy species " + identity)
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
    for mushroom in additional:
        identity = mushroom["id"]
        poison_name = "RMush_Poison" + identity
        hediff = hediff_by_name[poison_name]
        settings = mushroom["settings"]
        check(hediff.findtext("hediffClass") == "RimMushrooms.Hediff_MushroomPoisoning" and hediff.findtext("tendable") == "true" and float(hediff.findtext("lethalSeverity")) == -1, "new poisoning uses ordinary tending and runtime fatality " + identity)
        tend = hediff.find("comps/li[@Class='HediffCompProperties_TendDuration']")
        check(tend is not None and float(tend.findtext("baseTendDurationHours")) == 6 and float(tend.findtext("severityPerDayTended")) == 0 and tend.find("disappearsAtTotalTendQuality") is None, "new poisoning requires repeated medical care " + identity)
        extension = hediff.find("modExtensions/li[@Class='RimMushrooms.MushroomPoisonSettings']")
        check(extension is not None and extension.find("vomitMtbHours") is None, "new poisoning uses native vomiting stages " + identity)
        for key, value in settings.items():
            if key == "vomitMtbHours": continue
            text = extension.findtext(key)
            check(text == str(value).lower() if isinstance(value, bool) else text is not None and float(text) == value, "configured new poison extension " + identity + "." + key)
        check(settings["fatal"] == (identity in {"DeathCap", "DeadlyWebcap"}) and settings.get("conditionalFatalThreshold", 0) == 0, "new intrinsic fatality limited to supported liver and kidney poisons " + identity)
        check(float(extension.findtext("responseGraceHours")) >= 24 and float(extension.findtext("stabilizationTreatment")) == common["stabilizationTreatment"], "new fatal poison response window and stabilization " + identity)
        stages = hediff.findall("stages/li")
        check(len(stages) == len(mushroom["stages"]) == 5 and [float(s.findtext("minSeverity")) for s in stages] == [0, 0.1, 0.35, 0.6, 0.85], "five ordered new poison stages " + identity)
        check(stages[0].find("capMods") is None and stages[0].find("painOffset") is None and stages[0].find("vomitMtbDays") is None, "new latent stage has no symptoms " + identity)
        for index, (stage, specification) in enumerate(zip(stages, mushroom["stages"])):
            capacities = {n.findtext("capacity"): float(n.findtext("offset")) for n in stage.findall("capMods/li")}
            check(capacities == specification.get("capacities", {}) and all(-1 < value <= 0 for value in capacities.values()), "new reversible capacity effects " + identity + "." + str(index))
            check(float(stage.findtext("painOffset", "0")) == specification.get("painOffset", 0), "new configured pain " + identity + "." + str(index))
            expected_vomit = settings.get("vomitMtbHours", 0) / 24 if index > 0 and not (identity == "DeathCap" and index == 2) else 0
            check(float(stage.findtext("vomitMtbDays", "0")) == expected_vomit, "new native vomiting and liver remission " + identity + "." + str(index))
            check(float(stage.findtext("statFactors/ImmunityGainSpeed", "1")) == specification.get("immunityGainSpeedFactor", 1), "new immunity effect " + identity + "." + str(index))
            expected_injections["HediffDef"][poison_name + f".stages.{index}.label"] = stage.findtext("label")
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


def validate_expansion(defs, thoughts, docs, balance, assorted_choices):
    """Check species coverage, gameplay categories and immutable expansion art."""
    config = json.loads((ROOT / "Balance/expansion_mushrooms.json").read_text(encoding="utf-8"))
    catalog = json.loads((ROOT / config["catalog"]).read_text(encoding="utf-8"))
    registry = {m["id"]: m for m in catalog["species"]}
    species = config["mushrooms"]
    check(config["contentVersion"] == balance["version"] == "0.5.0", "expansion and release versions match")
    check(len(species) == len(registry) == 31 and {m["id"] for m in species} == set(registry), "all thirty-one approved expansion species represented exactly once")
    check(not set(registry).intersection(catalog["existing_species_excluded"]), "expansion does not redefine existing fifteen species")
    check({m["id"] for m in species if m["cultivable"]} == {"Cauliflower", "PurpleBlewit"}, "exactly two new cultivated varieties")
    check({m["id"] for m in species if m["use"] == "psychoactive"} == {"LibertyCap", "Cubensis", "PantherCap"}, "exact new psychoactive species set")
    check({m["id"] for m in species if m["use"] == "poisonous"} == {"DeathCap", "YellowDapperling", "JackOLantern", "DeadlyWebcap", "GhostFungus"}, "exact new poison species set")
    check({m["id"] for m in species if m["use"] == "resource"} == {"Reishi", "TurkeyTail", "Sanghuang", "Cordyceps", "Chaga", "ZombieAntFungus", "DryRot", "DevilsFingers", "BirdsNest", "Chlorophos"}, "paused medicinal and nonfood specimen species remain resources")
    check({use: sum(m["use"] == use for m in species) for use in ("food", "psychoactive", "poisonous", "resource")} == {"food": 13, "psychoactive": 3, "poisonous": 5, "resource": 10}, "all new species assigned approved food and resource uses")
    by_name = {n.findtext("defName"): n for n in defs}
    thought_by_name = {n.findtext("defName"): n for n in thoughts}
    resource_base = ET.parse(ROOT / "Defs/ExpansionMushrooms/Mushrooms.xml").find("ThingDef[@Name='RMush_ExpansionResourceBase']")
    check(resource_base.get("ParentName") == "OrganicProductBase" and resource_base.findtext("statBases/Nutrition") == "0", "paused medicinal and specimen resources supply no food nutrition")
    check(resource_base.find("ingestible").attrib == {"Inherit": "False", "IsNull": "True"} and {n.text for n in resource_base.findall("thingCategories/li")} == {"RMush_FungalResources"}, "nonfood fungal resources cannot be eaten or used by food recipes")
    categories = [n for p, doc in docs.items() if "Defs" in p.parts for n in doc.getroot() if n.tag == "ThingCategoryDef"]
    check(len(categories) == 2 and {n.findtext("defName") for n in categories} == {"RMush_FungalResources", "RMush_PoisonMushrooms"} and all(n.findtext("parent") == "ResourcesRaw" for n in categories), "fungal and poison resource categories remain outside Foods")
    expected_text = {"ThingDef": {}, "ThoughtDef": {}, "ThingCategoryDef": {"RMush_FungalResources.label": "fungal resources"}}
    wild_totals = {}
    for mushroom in species:
        identity = mushroom["id"]
        original = registry[identity]
        check(all(mushroom[field] == original[field] for field in ("ko", "en", "scientific", "group")), "approved species identity and display names " + identity)
        raw_name, plant_name = "RMush_Raw" + identity, "RMush_Plant" + identity
        raw, plant = by_name[raw_name], by_name[plant_name]
        use = mushroom["use"]
        food = use in ("food", "psychoactive")
        parent = "RMush_RawBase" if food else "RMush_PoisonRawBase" if use == "poisonous" else "RMush_ExpansionResourceBase"
        check(raw.get("ParentName") == parent and raw.findtext("label") == mushroom["en"], "correct expansion harvest classification " + identity)
        check(float(raw.findtext("statBases/MarketValue")) == mushroom["value"] and float(raw.findtext("comps/li[@Class='CompProperties_Rottable']/daysToRotStart")) == mushroom["rotDays"], "configured expansion value and spoilage " + identity)
        check(plant.get("ParentName") == "RMush_PlantBase" and plant.findtext("plant/harvestedThingDef") == raw_name, "expansion plant inherits growth renderer and harvests its own species " + identity)
        check(plant.find("ingestible").attrib == {"Inherit": "False", "IsNull": "True"} and plant.findtext("statBases/Nutrition") == "0", "expansion foliage cannot bypass harvested item effects " + identity)
        check(float(plant.findtext("plant/growDays")) == mushroom["growDays"] and int(plant.findtext("plant/harvestYield")) == mushroom["yield"], "configured expansion growth and yield " + identity)
        tags = {n.text for n in plant.findall("plant/sowTags/li")}
        check(tags == ({"Ground", "Hydroponic"} if mushroom["cultivable"] else set()), "new cultivar supports hydroponics and wild species cannot be sown " + identity)
        check(plant_name not in assorted_choices, "original nine-variety assorted selection remains unchanged " + identity)
        if mushroom["cultivable"]:
            check(int(plant.findtext("plant/sowMinSkill")) == mushroom["skill"], "configured new cultivation skill " + identity)
        else:
            check(plant.find("plant/sowMinSkill") is None, "wild-only variety has no sow skill " + identity)
        check(plant.findtext("plant/humanFoodPlant") == str(food).lower() and plant.findtext("plant/purpose") == ("Food" if food else "Misc"), "new plant purpose matches harvest use " + identity)
        check(plant.findtext("plant/wildClusterWeight") == "1" and plant.findtext("plant/wildClusterRadius") == "2", "small expansion wild clusters " + identity)
        biomes = {n.tag: float(n.text) for n in plant.findall("plant/wildBiomes/*")}
        check(biomes == mushroom["biomes"] and all(0 < weight <= .002 for weight in biomes.values()), "low configured expansion wild-selection weights " + identity)
        for biome, weight in biomes.items(): wild_totals[biome] = round(wild_totals.get(biome, 0) + weight, 8)
        if use == "psychoactive":
            exposure = raw.find("modExtensions/li[@Class='RimMushrooms.MushroomExposureProperties']")
            check(exposure is not None and exposure.findtext("psychoactive") == "true" and float(exposure.findtext("doseUnitCount")) == 10, "raw and meal psychoactive exposure contract " + identity)
            check(float(exposure.findtext("moodBonus")) == mushroom["mood"] and float(exposure.findtext("moodDurationHours")) == 6, "six-hour psychoactive mood " + identity)
            check([float(exposure.findtext(field)) for field in ("hallucinationHoursMin", "hallucinationHoursMax")] == mushroom["hallucinationHours"], "configured loss-of-control duration " + identity)
            check(raw.find("ingestible/outcomeDoers/li[@Class='RimMushrooms.IngestionOutcomeDoer_MushroomExposure']") is not None and raw.find("ingestible/specialThoughtDirect") is None and raw.find("ingestible/specialThoughtAsIngredient") is None, "psychoactive effects use one unified ingestion path " + identity)
            check(exposure.findtext("poisonHediff") == mushroom.get("poisonHediff"), "only panther cap links an expansion psychoactive poison " + identity)
        elif use == "poisonous":
            outcome = raw.find("ingestible/outcomeDoers/li[@Class='RimMushrooms.IngestionOutcomeDoer_MushroomPoison']")
            exposure = raw.find("modExtensions/li[@Class='RimMushrooms.MushroomExposureProperties']")
            check(outcome is not None and outcome.findtext("hediff") == mushroom["poisonHediff"] and float(outcome.findtext("doseUnitCount")) == 1, "manual-only new poison ingestion link " + identity)
            check(exposure is not None and exposure.findtext("poisonHediff") == mushroom["poisonHediff"] and float(exposure.findtext("doseUnitCount")) == 1 and exposure.find("psychoactive") is None, "poison provenance available without enabling ordinary cooking " + identity)
            check(raw.find("thingCategories") is None and raw.find("statBases/Nutrition") is None and raw.find("ingestible/preferability") is None, "new poison cannot override automatic food exclusions " + identity)
        elif use == "food" and mushroom["mood"]:
            thought_name = "RMush_Ate" + identity
            thought = thought_by_name[thought_name]
            check(thought.findtext("thoughtClass") == "RimMushrooms.Thought_MushroomEnjoyment" and float(thought.findtext("durationDays")) == .25 and thought.findtext("stackLimit") == "1", "new edible six-hour nonstacking enjoyment " + identity)
            check(float(thought.findtext("stages/li/baseMoodEffect")) == mushroom["mood"] and mushroom["mood"] in (3, 5, 7), "new edible belongs to approved taste groups " + identity)
            check(all(raw.findtext("ingestible/" + tag) == thought_name for tag in ("specialThoughtDirect", "specialThoughtAsIngredient")), "new edible taste applies raw and cooked " + identity)
            for field in ("label", "description"):
                expected_text["ThoughtDef"][thought_name + ".stages.0." + field] = thought.findtext("stages/li/" + field)
        else:
            check(raw.find("ingestible/specialThoughtDirect") is None and raw.find("ingestible/outcomeDoers") is None and raw.find("modExtensions") is None, "plain food or paused resource has no unapproved medical effect " + identity)
        for name, definition in ((raw_name, raw), (plant_name, plant)):
            for suffix in ("label", "description"): expected_text["ThingDef"][name + "." + suffix] = definition.findtext(suffix)
    check(wild_totals == config["newWildCommonalityTotals"] and wild_totals["TemperateForest"] <= .03 and wild_totals["BorealForest"] <= .01, "aggregate expansion wild budgets remain small")
    for language in ("English", "Korean"):
        for def_type, expected in expected_text.items():
            entries = list(ET.parse(ROOT / "Languages" / language / "DefInjected" / def_type / "ExpansionMushrooms.xml").getroot())
            translated = {n.tag: n.text for n in entries}
            check(len(entries) == len(translated) and set(translated) == set(expected) and all(value and value.strip() for value in translated.values()), language + " complete nonempty expansion " + def_type + " translations")
            if language == "English": check(translated == expected, "English expansion text matches defaults " + def_type)
            if language == "Korean" and def_type == "ThingDef":
                check(all(translated["RMush_Raw" + m["id"] + ".label"] == m["ko"] for m in species), "all thirty-one Korean harvest names preserved")
    manifest = json.loads((ROOT / config["assetBase"] / "manifest.json").read_text(encoding="utf-8"))
    approved = [entry for entry in manifest["files"] if entry["file"].startswith("Textures/")]
    check(len(approved) == len({entry["file"] for entry in approved}) == 155 and {entry["species"] for entry in approved} == set(registry), "approved thirty-one species and 155 unique expansion PNGs")
    for entry in approved:
        source, runtime = ROOT / config["assetBase"] / entry["file"], ROOT / entry["file"]
        check(sha(source) == sha(runtime) == entry["sha256"], "exact approved expansion PNG " + entry["file"])
        check(entry["dimensions"] == [256, 256] and entry["mode"] == "RGBA", "approved expansion art format " + entry["file"])


def validate_psychoactive(docs, thoughts):
    elements = [n for p, doc in docs.items() if "Defs" in p.parts for n in doc.getroot() if n.find("defName") is not None]
    by_name = {n.findtext("defName"): n for n in elements}
    hediffs = {n.findtext("defName") for n in elements if n.tag == "HediffDef"}
    check(len(hediffs) == 13 and {"RMush_Hallucination", "RMush_PsychedelicTolerance"}.issubset(hediffs), "eleven poisons plus hallucination and shared tolerance")
    hallucination = by_name["RMush_Hallucination"]
    check(hallucination.findtext("hediffClass") == "RimMushrooms.Hediff_MushroomHallucination" and hallucination.findtext("tendable") == "false", "hallucinations use a timed effect without medical crafting")
    capacities = {n.findtext("capacity"): float(n.findtext("offset")) for n in hallucination.findall("stages/li/capMods/li")}
    check(capacities == {"Consciousness": -.1, "Manipulation": -.1}, "hallucination carries approved work-capacity cost")
    check(by_name["RMush_PsychedelicTolerance"].findtext("hediffClass") == "RimMushrooms.Hediff_MushroomTolerance", "all psychoactive mushrooms share one tolerance")
    mood = by_name["RMush_PsychedelicExperience"]
    check(mood.findtext("thoughtClass") == "RimMushrooms.Thought_MushroomHallucination" and float(mood.findtext("durationDays")) == .25 and mood.findtext("stackLimit") == "1" and len(mood.findall("stages/li")) == 3, "one dynamic six-hour mushroom experience with positive and panic stages")
    state = by_name["RMush_HallucinatoryWander"]
    check(state.findtext("stateClass") == "RimMushrooms.MentalState_MushroomWander" and state.findtext("category") == "Misc" and state.findtext("stopsJobs") == "true" and state.findtext("blockNormalThoughts") == "false", "temporary controlled loss preserves mushroom mood")
    tree = by_name["RMush_HallucinatoryWanderBehavior"]
    check(tree.findtext("thinkRoot/state") == "RMush_HallucinatoryWander" and tree.find(".//li[@Class='RimMushrooms.JobGiver_MushroomWander']") is not None, "hallucination state has a matching wander behavior")
    check(not any(n.tag in {"RecipeDef", "ResearchProjectDef"} for n in elements), "no medicinal crafting or separate psychedelic drug production added")
    for def_type, identities in (("HediffDef", ("RMush_Hallucination", "RMush_PsychedelicTolerance")), ("MentalStateDef", ("RMush_HallucinatoryWander",)), ("ThoughtDef", ("RMush_PsychedelicExperience",))):
        entries = list(ET.parse(ROOT / "Languages/Korean/DefInjected" / def_type / "PsychoactiveMushrooms.xml").getroot())
        translated = {n.tag: n.text for n in entries}
        check(len(entries) == len(translated) and all(value and value.strip() for value in translated.values()), "unique nonempty Korean psychoactive " + def_type + " translations")
        for identity in identities:
            expected = [identity + ".stages." + str(index) + "." + field for index in range(3) for field in ("label", "description")] if def_type == "ThoughtDef" else [identity + ".label", identity + ".description"] if def_type == "HediffDef" else [identity + ".label", identity + ".beginLetter", identity + ".recoveryMessage", identity + ".baseInspectLine"]
            check(all(key in translated for key in expected), "complete Korean psychoactive effect strings " + identity)

def main():
    docs = {p: ET.parse(p) for folder in ("About","Defs","Languages","Patches") for p in (ROOT / folder).rglob("*.xml")}
    balance = json.loads((ROOT / "Balance/mushrooms.json").read_text(encoding="utf-8"))
    preserved_balance = {
        "Button": (6, 10, 0, 1.1, 20, 3), "Shiitake": (7, 12, 4, 1.5, 25, 5),
        "Oyster": (5.5, 10, 0, 1, 15, 3), "KingOyster": (6.5, 11, 3, 1.4, 20, 5),
        "Enoki": (5, 9, 2, 1, 12, 3), "WoodEar": (6.5, 10, 3, 1.3, 30, 7),
        "Beech": (6.5, 12, 4, 1.3, 20, 5), "Maitake": (8, 14, 6, 1.8, 18, 7),
        "LionsMane": (8, 12, 6, 2, 15, 7), "Matsutake": (12, 6, 0, 4, 12, 10),
    }
    check({m["id"]: tuple(m[field] for field in ("growDays", "yield", "skill", "value", "rotDays", "mood")) for m in balance["mushrooms"]} == preserved_balance, "original ten species balance preserved from v0.4.0")
    check(ET.parse(ROOT / "About/About.xml").findtext("modVersion") == balance["version"], "consistent release version")
    about = ET.parse(ROOT / "About/About.xml")
    check(about.findtext("description") == "46종류의 버섯을 추가합니다.", "concise forty-six species mod description")
    check(about.findtext("packageId") == "izzypizzy.rimmushrooms", "existing save and workshop package identity preserved")
    cover_record = json.loads((ROOT / "Art/Covers/v0.3.1/cover-v1-generation.json").read_text(encoding="utf-8"))
    cover = ROOT / "About/Preview.png"
    source_cover = ROOT / "Art/Covers/v0.3.1/MoreMushrooms-cover-v1.jpg"
    check(cover.read_bytes() == source_cover.read_bytes(), "approved Ratkin cover configured at native preview path")
    check(sha(cover) == cover_record["outputs"]["MoreMushrooms-cover-v1.jpg"]["sha256"], "approved cover hash")
    check(cover.read_bytes()[:3] == b"\xff\xd8\xff" and cover.stat().st_size < 1000000, "native-supported JPEG preview below upload size limit")
    defs = [n for p, doc in docs.items() if "Defs" in p.parts for n in doc.getroot() if n.tag == "ThingDef" and n.find("defName") is not None]
    thoughts = [n for p, doc in docs.items() if "Defs" in p.parts for n in doc.getroot() if n.tag == "ThoughtDef" and n.find("defName") is not None]
    names = [n.findtext("defName") for n in defs]
    check(len(names) == 94 and len(set(names)) == 94, "94 unique plant and harvest definitions for forty-six species")
    legacy_names = {"RMush_Raw" + m["id"] for m in balance["mushrooms"]} | {"RMush_Plant" + m["id"] for m in balance["mushrooms"]} | {"RMush_PlantEnokiWild", "RMush_PlantAssorted"}
    expansion = json.loads((ROOT / "Balance/expansion_mushrooms.json").read_text(encoding="utf-8"))
    poison = json.loads((ROOT / "Balance/poison_mushrooms.json").read_text(encoding="utf-8"))
    all_species = {m["id"] for m in balance["mushrooms"] + poison["mushrooms"] + expansion["mushrooms"]}
    check(len(all_species) == 46 and set(names) == {"RMush_Raw" + identity for identity in all_species} | {"RMush_Plant" + identity for identity in all_species} | {"RMush_PlantEnokiWild", "RMush_PlantAssorted"}, "exact existing fifteen and approved new thirty-one species definition set")
    check({n.findtext("plant/harvestedThingDef") for n in defs if n.find("plant/harvestedThingDef") is not None} == {"RMush_Raw" + identity for identity in all_species}, "all forty-six distinct harvest species are connected to plants")
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
        raw = next(n for n in defs if n.findtext("defName") == "RMush_Raw" + m["id"])
        check(float(raw.findtext("statBases/MarketValue")) == m["value"] and float(raw.findtext("comps/li[@Class='CompProperties_Rottable']/daysToRotStart")) == m["rotDays"], "preserved original item value and spoilage " + m["id"])
        for source_count, runtime_count in (("low", "01Low"), ("medium", "02Medium"), ("full", "03Full")):
            source = HANDOFF / "outputs/mushrooms-boxed/sprites-256" / (m["asset"] + "_" + source_count + ".png")
            runtime = ROOT / "Textures/Things/Item/RimMushrooms" / m["id"] / (runtime_count + ".png")
            check(sha(source) == sha(runtime), "preserved species-specific original boxed PNG " + m["id"] + "." + runtime_count)
        for variant in ("A", "B"):
            source = HANDOFF / "outputs/mushrooms-growing/textures-256" / (m["asset"] + "_" + variant + ".png")
            runtime = ROOT / "Textures/Things/Plant/RimMushrooms" / m["id"] / (m["id"] + variant + ".png")
            check(sha(source) == sha(runtime), "preserved species-specific original plant PNG " + m["id"] + variant)
    wild_enoki = next(n for n in defs if n.findtext("defName") == "RMush_PlantEnokiWild")
    check(wild_enoki.find("plant/sowTags") is None, "wild enoki stays unsowable")
    for variant in ("A", "B"):
        check(sha(HANDOFF / "outputs/mushrooms-growing/textures-256" / ("05_enoki_wild_" + variant + ".png")) == sha(ROOT / "Textures/Things/Plant/RimMushrooms/EnokiWild" / ("EnokiWild" + variant + ".png")), "preserved wild enoki plant PNG " + variant)
    patch = ET.parse(ROOT / "Patches/AssortedMushroomWork.xml")
    check(len(patch.findall("Operation/match/value/giverClass")) == 2, "scoped native sow and harvest workers shipped")
    plant_base = ET.parse(ROOT / "Defs/ThingDefs_Plants/Mushrooms.xml").find("ThingDef[@Name='RMush_PlantBase']")
    check(plant_base.findtext("thingClass") == "RimMushrooms.Plant_Mushroom", "shade-aware mushroom plant class")
    light = plant_base.find("modExtensions/li[@Class='RimMushrooms.MushroomLightSettings']")
    check(float(light.findtext("shadeMaxGlow")) == balance["common"]["shadeMaxGlow"] and 0 <= balance["common"]["shadeMaxGlow"] < 1, "configured shade threshold")
    check(float(light.findtext("fullLightGrowthFactor")) == balance["common"]["fullLightGrowthFactor"] and 0 < balance["common"]["fullLightGrowthFactor"] <= 1, "configured bright light multiplier")
    thought_by_name = {n.findtext("defName"): n for n in thoughts}
    legacy_thought_names = {"RMush_Ate" + m["id"] for m in balance["mushrooms"]}
    expected_thought_names = legacy_thought_names | {"RMush_Ate" + m["id"] for m in expansion["mushrooms"] if m["use"] == "food" and m["mood"]} | {"RMush_PsychedelicExperience"}
    check(len(thoughts) == len(thought_by_name) == 22 and set(thought_by_name) == expected_thought_names, "ten preserved edible memories plus eleven expansion tastes and one shared psychoactive experience")
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
        check(len(mood_tags) == len(set(mood_tags)) == 20 and all(n+suffix in mood_tags for n in legacy_thought_names for suffix in (".stages.0.label", ".stages.0.description")), lang + " unchanged legacy memory translations")
        check("{0}" in ET.parse(ROOT / "Languages" / lang / "Keyed/Mushrooms.xml").findtext("MM_BrightLightGrowthFactor"), lang + " growth tooltip translation")
    pngs = list((ROOT / "Textures").rglob("*.png"))
    check(len(pngs) == 233, "52 original textures, one assorted icon, 25 original poison textures and 155 expansion textures")
    sources = {sha(p) for folder in ("mushrooms-boxed/sprites-256", "mushrooms-growing/textures-256") for p in (HANDOFF / "outputs" / folder).glob("*.png")}
    sources.update(sha(p) for p in (ROOT / "Art/PoisonMushrooms").rglob("*.png") if p.read_bytes()[16:24] == struct.pack(">II", 256, 256))
    sources.update(sha(p) for p in (ROOT / expansion["assetBase"] / "Textures").rglob("*.png"))
    for p in (p for p in pngs if p != icon):
        data = p.read_bytes()
        check(sha(p) in sources, "approved pixels " + str(p))
        check(data[:8] == b"\x89PNG\r\n\x1a\n" and struct.unpack(">II",data[16:24]) == (256,256) and data[25] == 6, "256px RGBA " + str(p))
    check({p.name for p in (ROOT / "Assemblies").glob("*.dll")} == {"RimMushrooms.dll"}, "no game or test DLL shipped")
    validate_poison(defs, docs, choices)
    validate_expansion(defs, thoughts, docs, balance, choices)
    validate_psychoactive(docs, thoughts)
    total_definitions = sum(1 for path, doc in docs.items() if "Defs" in path.parts for definition in doc.getroot() if definition.find("defName") is not None)
    print(json.dumps({"status":"PASS", "checks":checks,"xml_files":len(docs),"definitions":total_definitions,"species":len(all_species),"textures":len(pngs)}, indent=2))

if __name__ == "__main__": main()
