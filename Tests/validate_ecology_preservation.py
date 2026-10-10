"""Compare the ecology update with the immutable v0.5.0 runtime archive.

Only wild spawn registration, the new ecology extension, and the two plant
glowers may differ within existing definitions. Other gameplay and artwork
must remain byte-identical or semantically identical, as appropriate.
"""
from copy import deepcopy
from pathlib import Path
import hashlib
import json
import xml.etree.ElementTree as ET
import zipfile

ROOT = Path(__file__).resolve().parents[1]
BASELINE = ROOT / "Releases/v0.5.0/MoreMushrooms-v0.5.0.zip"


def signature(element):
    return (element.tag, tuple(sorted(element.attrib.items())),
            (element.text or "").strip(), tuple(signature(c) for c in element))


def strip_ecology_description(text):
    for marker in (" Wild ecology: ", " 야생 생태: "):
        text = text.split(marker, 1)[0]
    return text


def without_ecology(element):
    element = deepcopy(element)
    for parent in element.iter():
        for child in list(parent):
            if child.tag == "wildBiomes":
                parent.remove(child)
            elif child.get("Class") == "RimMushrooms.MushroomEcologyExtension":
                parent.remove(child)
            elif (child.get("Class") == "CompProperties_Glower"
                  and element.findtext("defName") in {
                      "RMush_PlantGhostFungus", "RMush_PlantChlorophos"}):
                parent.remove(child)
    for container in ("modExtensions", "comps"):
        child = element.find(container)
        if child is not None and not len(child):
            element.remove(child)
    if (element.findtext("defName") or "").startswith("RMush_Plant"):
        description = element.find("description")
        if description is not None:
            description.text = strip_ecology_description(description.text or "")
    return signature(element)


def main():
    checks = 0
    definition_checks = 0
    with zipfile.ZipFile(BASELINE) as archive:
        for entry in archive.infolist():
            if entry.is_dir():
                continue
            relative = entry.filename.removeprefix("MoreMushrooms/")
            current = ROOT / relative
            previous = archive.read(entry)
            if relative.startswith("Defs/") and relative.endswith(".xml"):
                old_tree, new_tree = ET.fromstring(previous), ET.parse(current).getroot()
                assert len(old_tree) == len(new_tree), relative
                for old_def, new_def in zip(old_tree, new_tree):
                    assert without_ecology(old_def) == without_ecology(new_def), (
                        relative, old_def.findtext("defName") or old_def.get("Name"))
                    definition_checks += 1
                    checks += 1
            elif relative.startswith("Languages/") and relative.endswith(".xml"):
                old_tree, new_tree = ET.fromstring(previous), ET.parse(current).getroot()
                for language_tree in (old_tree, new_tree):
                    for translation in language_tree:
                        if translation.tag.startswith("RMush_Plant") and translation.tag.endswith(".description"):
                            translation.text = strip_ecology_description(translation.text or "")
                assert signature(old_tree) == signature(new_tree), relative
                checks += 1
            elif relative.startswith(("Textures/", "Credits/", "Patches/")):
                assert current.read_bytes() == previous, relative
                checks += 1
            elif relative.startswith("About/") and relative != "About/About.xml":
                assert current.read_bytes() == previous, relative
                checks += 1
        old_about = ET.fromstring(archive.read("MoreMushrooms/About/About.xml"))
        new_about = ET.parse(ROOT / "About/About.xml").getroot()
        for node in (old_about, new_about):
            version = node.find("modVersion")
            if version is not None:
                node.remove(version)
        assert signature(old_about) == signature(new_about), "About metadata except version"
        checks += 1

    before = json.loads((ROOT / "Tests/Runtime/v0.6.0-before.json").read_text(encoding="utf-8-sig"))
    for relative, expected in before["releaseHashes"].items():
        assert hashlib.sha256((ROOT / relative).read_bytes()).hexdigest() == expected, relative
        checks += 1
    assert hashlib.sha256((ROOT / before["legacySave"]).read_bytes()).hexdigest().upper() == before["legacySaveHash"]
    checks += 1
    result = {"status": "PASS", "checks": checks, "baseline": "v0.5.0",
              "existingDefinitions": definition_checks,
              "originalReleaseArchives": len(before["releaseHashes"]),
              "legacySaveSha256": before["legacySaveHash"].lower(),
              "scope": "Only wildBiomes, ecology extensions, the two plant glowers, and appended plant ecology descriptions may change in existing Defs."}
    print(json.dumps(result, indent=2))


if __name__ == "__main__":
    main()
