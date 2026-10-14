"""Prove the habitat/clump release preserves v0.7 food, health, art and saves."""
from copy import deepcopy
from pathlib import Path
import hashlib
import json
import xml.etree.ElementTree as ET
import zipfile

ROOT = Path(__file__).resolve().parents[1]
PREFIXES = ("About/", "Assemblies/", "Defs/", "Languages/", "Textures/", "Credits/", "Patches/")
HABITAT_FIELDS = {"habitatTree", "habitatTreeMinGrowth", "habitatTreeRadius"}
CLUSTER_FIELDS = {"smallClusterMinCount", "smallClusterMaxCount", "smallClusterSearchRadius", "smallClusterCellRadius"}
checks = 0


def check(value, message):
    global checks
    checks += 1
    if not value:
        raise AssertionError(message)


def sha(path):
    digest = hashlib.sha256()
    with path.open("rb") as stream:
        for block in iter(lambda: stream.read(1024 * 1024), b""):
            digest.update(block)
    return digest.hexdigest()


def sig(node):
    return (node.tag, tuple(sorted(node.attrib.items())), (node.text or "").strip(), tuple(sig(c) for c in node))


def normalized(node):
    node = deepcopy(node)
    if node.tag == "RimMushrooms.WildMushroomEcologyDef":
        for child in list(node):
            if child.tag in CLUSTER_FIELDS:
                node.remove(child)
    for extension in node.findall("modExtensions/li[@Class='RimMushrooms.MushroomEcologyExtension']"):
        for child in list(extension):
            if child.tag in HABITAT_FIELDS:
                extension.remove(child)
    if (node.findtext("defName") or "").startswith("RMush_Plant"):
        description = node.find("description")
        if description is not None:
            description.text = description.text.split(" Wild ecology: ")[0]
    return sig(node)


def main():
    with zipfile.ZipFile(ROOT / "Releases/v0.7.0/MoreMushrooms-v0.7.0.zip") as archive:
        old = {n.removeprefix("MoreMushrooms/"): archive.read(n) for n in archive.namelist()
               if n.removeprefix("MoreMushrooms/").startswith(PREFIXES)}
        check((ROOT / "Docs/History/README-v0.7.0.md").read_bytes() == archive.read("MoreMushrooms/README.md"), "previous README preserved exactly")
    current = {p.relative_to(ROOT).as_posix(): p.read_bytes() for prefix in PREFIXES
               for p in (ROOT / prefix).rglob("*") if p.is_file()}
    check(set(old) == set(current), "no added or removed gameplay payload identities")
    definition_count = 0
    for name, data in old.items():
        if name == "Assemblies/RimMushrooms.dll":
            continue
        if name == "About/About.xml":
            before, after = ET.fromstring(data), ET.fromstring(current[name])
            before.find("modVersion").text = after.findtext("modVersion")
            check(sig(before) == sig(after), "About metadata preserved except version")
        elif name.startswith("Defs/") and name.endswith(".xml"):
            before, after = ET.fromstring(data), ET.fromstring(current[name])
            check(len(before) == len(after), "definition count " + name)
            for left, right in zip(before, after):
                check(normalized(left) == normalized(right), "preserved non-habitat definition " + (left.findtext("defName") or left.get("Name", left.tag)))
                definition_count += 1
        elif name.startswith("Languages/") and name.endswith(".xml"):
            before, after = ET.fromstring(data), ET.fromstring(current[name])
            for document in (before, after):
                for entry in document:
                    if entry.tag.startswith("RMush_Plant") and entry.tag.endswith(".description"):
                        for marker in (" Wild ecology: ", " 야생 생태: "):
                            entry.text = entry.text.split(marker)[0]
            check(sig(before) == sig(after), "preserved translations apart from plant ecology " + name)
        else:
            check(current[name] == data, "unchanged payload " + name)
    with zipfile.ZipFile(ROOT / "Releases/v0.7.0/MoreMushrooms-v0.7.0-source.zip") as archive:
        old_sources = {n.removeprefix("MoreMushrooms/") for n in archive.namelist() if n.startswith("MoreMushrooms/Source/")}
        new_sources = {p.relative_to(ROOT).as_posix() for p in (ROOT / "Source").glob("*.cs")}
        check(new_sources - old_sources == {"Source/MushroomSmallClusters.cs"} and not old_sources - new_sources,
              "only the bounded cluster helper is added to production source")
        for name in ("mushrooms", "poison_mushrooms", "expansion_mushrooms", "wild_ecology"):
            before = json.loads(archive.read("MoreMushrooms/Balance/" + name + ".json"))
            after = json.loads((ROOT / "Balance" / (name + ".json")).read_text(encoding="utf-8"))
            for document in (before, after):
                for key in ("version", "contentVersion", "status"):
                    document.pop(key, None)
            if name == "wild_ecology":
                for document in (before, after):
                    for key in ("scope", "excluded", "smallClusterPolicy", "habitatPolicy"):
                        document.pop(key, None)
                    for key in CLUSTER_FIELDS:
                        document["settings"].pop(key, None)
                    for value in document["species"].values():
                        for key in HABITAT_FIELDS:
                            value.pop(key, None)
            elif name == "expansion_mushrooms":
                before["notes"][3] = after["notes"][3]
            check(before == after, "all unrelated balance values preserved " + name)
        for entry in archive.namelist():
            relative = entry.removeprefix("MoreMushrooms/")
            if (relative.startswith("Source/") or relative == "MoreMushrooms.csproj") and relative != "Source/WildMushroomEcology.cs":
                check((ROOT / relative).read_bytes() == archive.read(entry), "unrelated production source preserved " + relative)
    snapshot = json.loads((ROOT / "Tests/Runtime/v080-before.json").read_text(encoding="utf-8"))
    for relative, expected in snapshot["archives"].items():
        check(sha(ROOT / relative) == expected, "immutable prior release " + relative)
    user = Path.home() / "AppData/LocalLow/Ludeon Studios/RimWorld by Ludeon Studios"
    for key, folder in (("userConfig", "Config"), ("userSaves", "Saves")):
        check({p.relative_to(user / folder).as_posix() for p in (user / folder).rglob("*") if p.is_file()} == set(snapshot[key]), "user file set preserved " + folder)
        for relative, expected in snapshot[key].items():
            check(sha(user / folder / relative) == expected, "user file preserved " + folder + "/" + relative)
    report = dict(status="PASS", checks=checks, baselineVersion="0.7.0", existingDefinitions=definition_count,
                  gameplayPayloadFiles=len(current), priorReleaseFilesPreserved=len(snapshot["archives"]),
                  userConfigFilesPreserved=len(snapshot["userConfig"]), userSaveFilesPreserved=len(snapshot["userSaves"]),
                  scope="Only bounded clump settings, six habitat extensions, appended ecology descriptions, version and the wild-ecology runtime may change.")
    (ROOT / "Docs/TestResults/v0.8.0-preservation.json").write_text(json.dumps(report, ensure_ascii=False, indent=2) + "\n", encoding="utf-8")
    print(json.dumps(report, ensure_ascii=False, indent=2))


if __name__ == "__main__":
    main()
