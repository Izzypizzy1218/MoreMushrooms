"""Check the narrowly scoped v0.6.1 update against immutable v0.6.0 ZIPs.

XML whitespace is ignored. Only the requested premium food mood/duration,
two new flavor descriptions, eleven-variety selector and four psychoactive
timing fields/descriptions may change. Art, poison and ecology stay intact.
This is read-only and reports all detected differences for review.
"""
from copy import deepcopy
from pathlib import Path
import hashlib
import json
import math
import re
import sys
import xml.etree.ElementTree as ET
import zipfile

ROOT = Path(__file__).resolve().parents[1]
BASE = ROOT / "Releases/v0.6.0"
PREFIXES = ("About/", "Assemblies/", "Defs/", "Languages/", "Textures/", "Credits/", "Patches/")
PREMIUM = {"Matsutake", "BlackTruffle", "Porcini", "Morel", "BlackTrumpet"}
FLAVORS = {"Cauliflower", "PurpleBlewit"}
PSYCHO = {"LibertyCap": (6, 8), "Cubensis": (6, 8),
          "FlyAgaric": (8, 12), "PantherCap": (12, 24)}
SOURCE_ALLOWLIST = {"Source/AssortedMushrooms.cs", "Source/MushroomPsychoactive.cs"}
NEW_VARIETIES = ["RMush_PlantCauliflower", "RMush_PlantPurpleBlewit"]
PSYCHO_FIRST = {
    "English": "A first standard exposure (10 mushrooms) grants",
    "Korean": "첫 표준 노출(원물 10개)은"
}
PSYCHO_APPENDIX = {
    "English": " Amount affects mood and duration; new episodes are scheduled for 6–24 hours. Repeated intake cannot extend an episode beyond 24 hours from first exposure. Sleep or incapacitation can end wandering early.",
    "Korean": " 섭취량에 따라 무드와 시간이 달라지며, 새 노출의 예정 시간은 6~24시간입니다. 반복 섭취도 최초 노출부터 24시간을 넘기지 않습니다. 수면이나 쓰러짐으로 배회가 먼저 끝날 수 있습니다."
}
ASSORTED_TEXT = {
    "English": "Sow one of eleven cultivated mushroom varieties at random in growing zones or hydroponics basins, each with an equal chance. Includes cauliflower and purple blewit; excludes matsutake and wild enoki. Each new sowing picks again. The chosen variety keeps its own growth, harvest and food effects. Requires Plants skill 6. Native hydroponics power requirements apply.",
    "Korean": "재배 구역이나 수경분지에서 재배 가능한 버섯 11종 중 하나를 같은 확률로 무작위 파종합니다. 꽃송이와 자주방망이를 포함하며, 송이와 야생 팽이는 제외합니다. 수확 후 다시 심을 때도 새로 뽑습니다. 심어진 종류의 성장·수확·섭취 효과가 그대로 적용됩니다. 식물 기술 6이 필요합니다. 수경분지는 기본 게임의 전력 규칙을 따릅니다."
}
checks = 0
errors = []


def check(condition, message):
    global checks
    checks += 1
    if not condition:
        errors.append(message)


def sha(data):
    return hashlib.sha256(data).hexdigest()


def load_zip(path):
    with zipfile.ZipFile(path) as archive:
        return {entry.filename.removeprefix("MoreMushrooms/"): archive.read(entry)
                for entry in archive.infolist() if not entry.is_dir()}


def text_normalized(value, kind):
    value = (value or "").strip()
    if kind == "premium":
        value = re.sub(r"\+\d+ mood for \d+ hours", "+MOOD mood for HOURS hours", value)
        value = re.sub(r"\d+시간 동안 무드 \+\d+", "HOURS시간 동안 무드 +MOOD", value)
    elif kind == "psycho":
        # Only the approved standard-dose wording and exact explanatory suffix
        # may change; every other sentence must still match the old release.
        for appendix in PSYCHO_APPENDIX.values():
            value = value.removesuffix(appendix)
        value = value.replace(PSYCHO_FIRST["English"], "Grants")
        value = value.replace("A standard exposure grants", "Grants")
        value = value.replace(PSYCHO_FIRST["Korean"], "섭취하면")
        value = value.replace("표준 노출은", "섭취하면")
        value = value.replace("환각성 배회를 일으킵니다.", "환각성 배회가 발생합니다.")
        value = re.sub(r"\b\d+[–~\-]\d+ hours", "MIN–MAX hours", value)
        value = re.sub(r"\d+[–~\-]\d+시간", "MIN~MAX시간", value)
    elif kind == "assorted":
        value = re.sub(r"\b(nine|eleven)\b", "COUNT", value)
        value = re.sub(r"\b(9|11)종", "COUNT종", value)
    return value


def normalize_definition(element):
    element = deepcopy(element)
    name = element.findtext("defName") or ""
    if name in {"RMush_Ate" + item for item in PREMIUM}:
        for path in ("durationDays", "stages/li/baseMoodEffect"):
            element.find(path).text = "APPROVED_CHANGE"
    if name in {"RMush_Ate" + item for item in FLAVORS}:
        element.find("stages/li/description").text = "APPROVED_CHANGE"
    description = element.find("description")
    if name in {"RMush_Raw" + item for item in PREMIUM}:
        description.text = text_normalized(description.text, "premium")
    if name in {"RMush_Raw" + item for item in PSYCHO}:
        description.text = text_normalized(description.text, "psycho")
        for extension in element.findall("modExtensions/li"):
            if extension.get("Class") == "RimMushrooms.MushroomExposureProperties":
                for path in ("hallucinationHoursMin", "hallucinationHoursMax"):
                    extension.find(path).text = "APPROVED_CHANGE"
    if name == "RMush_PlantAssorted":
        description.text = "APPROVED_CHANGE"
        element.find("plant/growDays").text = "APPROVED_CHANGE"
        for extension in element.findall("modExtensions/li"):
            if extension.get("Class") == "RimMushrooms.AssortedMushroomSettings":
                varieties = extension.find("varieties")
                for variety in list(varieties):
                    if variety.text in NEW_VARIETIES:
                        varieties.remove(variety)
    return element


def normalize_translation(element):
    element = deepcopy(element)
    for entry in element:
        if entry.tag in {"RMush_Raw" + item + ".description" for item in PREMIUM}:
            entry.text = text_normalized(entry.text, "premium")
        elif entry.tag in {"RMush_Raw" + item + ".description" for item in PSYCHO}:
            entry.text = text_normalized(entry.text, "psycho")
        elif entry.tag == "RMush_PlantAssorted.description":
            entry.text = "APPROVED_CHANGE"
        elif entry.tag in {"RMush_Ate" + item + ".stages.0.description" for item in FLAVORS}:
            entry.text = "APPROVED_CHANGE"
    return element


def flatten(element, path=""):
    """Preserve child order, attributes and meaningful text in useful diff paths."""
    path += "/" + element.tag
    values = {path + "/@" + key: value for key, value in element.attrib.items()}
    values[path + "/#text"] = (element.text or "").strip()
    counts = {}
    for child in element:
        counts[child.tag] = counts.get(child.tag, 0) + 1
        values.update(flatten(child, path + "[" + str(counts[child.tag]) + "]"))
    return values


def compare_xml(old, new, label):
    old_values, new_values = flatten(old), flatten(new)
    differences = [f"{key}: {old_values.get(key, '<MISSING>')!r} -> {new_values.get(key, '<MISSING>')!r}"
                   for key in sorted(set(old_values) | set(new_values))
                   if old_values.get(key) != new_values.get(key)]
    check(not differences, label + (": " + "; ".join(differences[:8]) if differences else ""))


def main():
    baseline = load_zip(BASE / "MoreMushrooms-v0.6.0.zip")
    old_sources = load_zip(BASE / "MoreMushrooms-v0.6.0-source.zip")
    runtime = {path: data for path, data in baseline.items() if path.startswith(PREFIXES)}
    current_paths = {path.relative_to(ROOT).as_posix()
                     for prefix in PREFIXES for path in (ROOT / prefix).rglob("*") if path.is_file()}
    check(current_paths == set(runtime), "Runtime file set changed: " +
          repr({"added": sorted(current_paths - set(runtime)), "removed": sorted(set(runtime) - current_paths)}))
    definitions = {}
    old_definitions = {}
    definition_count = 0
    for relative, old_data in runtime.items():
        current = ROOT / relative
        if not current.exists():
            continue
        new_data = current.read_bytes()
        if relative.startswith("Defs/") and relative.endswith(".xml"):
            old_tree, new_tree = ET.fromstring(old_data), ET.fromstring(new_data)
            check(len(old_tree) == len(new_tree), relative + " definition count changed")
            for old_def, new_def in zip(old_tree, new_tree):
                name = old_def.findtext("defName") or old_def.get("Name") or old_def.tag
                definition_count += 1
                compare_xml(normalize_definition(old_def), normalize_definition(new_def), relative + ": " + name)
                if old_def.findtext("defName"):
                    old_definitions[name] = old_def
                    definitions[name] = new_def
        elif relative.startswith("Languages/") and relative.endswith(".xml"):
            compare_xml(normalize_translation(ET.fromstring(old_data)),
                        normalize_translation(ET.fromstring(new_data)), relative)
        elif relative == "About/About.xml":
            old_tree, new_tree = ET.fromstring(old_data), ET.fromstring(new_data)
            check(new_tree.findtext("modVersion") == "0.6.1", "About version must be 0.6.1")
            for tree in (old_tree, new_tree):
                tree.find("modVersion").text = "APPROVED_CHANGE"
            compare_xml(old_tree, new_tree, "About metadata except approved version")
        elif relative == "Assemblies/RimMushrooms.dll":
            check(bool(new_data), "Runtime assembly is empty")
        else:
            check(old_data == new_data, relative + " changed bytes")

    for species in sorted(PREMIUM):
        thought = definitions["RMush_Ate" + species]
        check(float(thought.findtext("durationDays")) == 2, species + " must last 48 hours")
        check(float(thought.findtext("stages/li/baseMoodEffect")) == 15, species + " must grant +15 mood")
        check("+15 mood for 48 hours" in definitions["RMush_Raw" + species].findtext("description"),
              species + " raw description must disclose +15 for 48 hours")
    for species, expected in PSYCHO.items():
        extension = definitions["RMush_Raw" + species].find(
            "modExtensions/li[@Class='RimMushrooms.MushroomExposureProperties']")
        actual = (float(extension.findtext("hallucinationHoursMin")),
                  float(extension.findtext("hallucinationHoursMax")))
        check(actual == expected, species + " control-loss range: " + repr(actual) + " expected " + repr(expected))
        check(f"{expected[0]}–{expected[1]} hours" in definitions["RMush_Raw" + species].findtext("description"),
              species + " raw description must disclose its updated timing range")
        base_description = definitions["RMush_Raw" + species].findtext("description")
        check(PSYCHO_FIRST["English"] in base_description,
              species + " base description must identify first standard exposure (10 mushrooms)")
        check(base_description.endswith(PSYCHO_APPENDIX["English"]),
              species + " base description must disclose dose scaling, episode cap and early recovery")
        filename = "PoisonMushrooms.xml" if species == "FlyAgaric" else "ExpansionMushrooms.xml"
        for language in ("English", "Korean"):
            path = ROOT / "Languages" / language / "DefInjected/ThingDef" / filename
            description = ET.parse(path).getroot().findtext("RMush_Raw" + species + ".description")
            check(PSYCHO_FIRST[language] in description, language + " " + species + " standard exposure clarification")
            check(description.endswith(PSYCHO_APPENDIX[language]), language + " " + species + " exact dose/cap/recovery appendix")
            timing = (f"{expected[0]}–{expected[1]} hours" if language == "English"
                      else f"{expected[0]}~{expected[1]}시간")
            check(timing in description, language + " " + species + " translated timing range")
    assorted = definitions["RMush_PlantAssorted"]
    check(assorted.findtext("description") == ASSORTED_TEXT["English"], "Assorted base description must disclose eleven and both additions")
    for language, expected in ASSORTED_TEXT.items():
        translation_path = ROOT / "Languages" / language / "DefInjected/ThingDef/Mushrooms.xml"
        translations = ET.parse(translation_path).getroot()
        check(translations.findtext("RMush_PlantAssorted.description") == expected, language + " assorted description")
    path = "modExtensions/li[@Class='RimMushrooms.AssortedMushroomSettings']/varieties/li"
    old_varieties = [entry.text for entry in old_definitions["RMush_PlantAssorted"].findall(path)]
    varieties = [entry.text for entry in assorted.findall(path)]
    check(varieties == old_varieties + NEW_VARIETIES, "Assorted varieties must retain nine in order then add the two cultivated species")
    mean_growth = sum(float(definitions[name].findtext("plant/growDays")) for name in varieties) / len(varieties)
    check(math.isclose(float(assorted.findtext("plant/growDays")), mean_growth, rel_tol=1e-12),
          "Assorted menu growth must equal the eleven-species mean")

    source_files = {name: data for name, data in old_sources.items() if name.startswith("Source/")}
    current_sources = {path.relative_to(ROOT).as_posix() for path in (ROOT / "Source").rglob("*") if path.is_file()}
    check(current_sources == set(source_files), "Production source file set changed")
    for relative, old_data in source_files.items():
        if relative not in SOURCE_ALLOWLIST:
            check((ROOT / relative).read_bytes() == old_data, relative + " unapproved source change")

    before = json.loads((ROOT / "Tests/Runtime/v0.6.1-before.json").read_text(encoding="utf-8-sig"))
    for relative, expected in before["releaseHashes"].items():
        check(sha((ROOT / relative).read_bytes()).upper() == expected.upper(), relative + " immutable release changed")
    for key in ("coreLegacy", "dlcLegacy"):
        path, expected = before[key + "Save"], before[key + "Hash"]
        check(sha((ROOT / path).read_bytes()).upper() == expected.upper(), key + " original fixture changed")
    user_config = Path.home() / "AppData/LocalLow/Ludeon Studios/RimWorld by Ludeon Studios/Config/ModsConfig.xml"
    check(sha(user_config.read_bytes()).upper() == before["userModsConfigHash"].upper(), "User ModsConfig changed")

    result = {"status": "FAIL" if errors else "PASS", "checks": checks,
              "baseline": "v0.6.0", "existingDefinitions": definition_count,
              "runtimeFiles": len(runtime), "productionSourceFiles": len(source_files),
              "unchangedSourceFiles": len(source_files) - len(SOURCE_ALLOWLIST),
              "originalReleaseArchives": len(before["releaseHashes"]),
              "scope": "Five premium moods; two flavor descriptions; eleven-species assorted selector; four psychoactive timing profiles. Other runtime definitions, art, poison, ecology and seven production sources preserved.",
              "differences": errors}
    print(json.dumps(result, indent=2, ensure_ascii=False))
    return 1 if errors else 0


if __name__ == "__main__":
    sys.exit(main())
