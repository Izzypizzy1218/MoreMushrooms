"""Verify the final immutable archives, installed payload and test-helper cleanup."""
from pathlib import Path
import hashlib
import json
import xml.etree.ElementTree as ET
import zipfile

ROOT = Path(__file__).resolve().parents[1]
GAME = Path("C:/Program Files (x86)/Steam/steamapps/common/RimWorld")


def sha(path):
    digest = hashlib.sha256()
    with path.open("rb") as stream:
        for block in iter(lambda: stream.read(1024 * 1024), b""):
            digest.update(block)
    return digest.hexdigest()


def main():
    release = ROOT / "Releases/v0.9.0"
    manifest = json.loads((release / "manifest.json").read_text(encoding="utf-8"))
    assert manifest["version"] == "0.9.0"
    target = GAME / "Mods/MoreMushrooms"
    installed = {p.relative_to(target).as_posix() for p in target.rglob("*") if p.is_file()}
    assert installed == set(manifest["files"]), "Installed file set differs from the runtime release"
    for relative, expected in manifest["files"].items():
        assert sha(ROOT / relative) == sha(target / relative) == expected, relative
    for name, expected in manifest["archives"].items():
        archive_path = release / name
        assert sha(archive_path) == expected, name
        with zipfile.ZipFile(archive_path) as archive:
            assert archive.testzip() is None, name
            if not name.endswith("-source.zip"):
                assert {n.removeprefix("MoreMushrooms/") for n in archive.namelist()} == installed
                for relative, expected_file_hash in manifest["files"].items():
                    assert hashlib.sha256(archive.read("MoreMushrooms/" + relative)).hexdigest() == expected_file_hash, relative
            else:
                assert not any(n.startswith(("MoreMushrooms/Tests/Runtime/", "MoreMushrooms/Tests/Harness/Assemblies/")) for n in archive.namelist()), "Temporary native runtime is included in source release"
    about = ET.parse(target / "About/About.xml")
    assert about.findtext("packageId") == "izzypizzy.rimmushrooms"
    assert about.findtext("modVersion") == "0.9.0"
    for helper in ("RimMushroomsTestHarness", "RimMushroomsTestHarmony", "RimMushrooms"):
        assert not (GAME / "Mods" / helper).exists(), "Temporary or duplicate mod remains: " + helper
    report = dict(status="PASS", version="0.9.0", installedPath=str(target),
                  installedFiles=len(installed), archiveCount=len(manifest["archives"]),
                  productionDllSha256=sha(target / "Assemblies/RimMushrooms.dll"),
                  testHelpersInMods=False, duplicateLegacyMod=False,
                  archives=manifest["archives"])
    (release / "postinstall-verification.json").write_text(json.dumps(report, ensure_ascii=False, indent=2) + "\n", encoding="utf-8")
    print(json.dumps(report, ensure_ascii=False, indent=2))


if __name__ == "__main__":
    main()
