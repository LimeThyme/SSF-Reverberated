"""Read-only source-package audit; does not build a dfmod or test Unity playback.
Usage: python validate_package.py --baseline path/to/SSF-Unified-Source.zip
The baseline is the packaged 1.4.1, including the maintainer's supplied manifest.
"""
import argparse
import hashlib
import json
from pathlib import Path
import zipfile


def require(condition, message):
    if not condition:
        raise SystemExit("FAIL: " + message)
    print("PASS: " + message)


def digest(data):
    return hashlib.sha256(data).hexdigest()


def main():
    parser = argparse.ArgumentParser(description=__doc__)
    parser.add_argument("--baseline", type=Path, required=True)
    args = parser.parse_args()
    root = Path(__file__).resolve().parent
    src = root / "SpriteSoundFramework-main"
    manifest = json.loads((src / "SpriteSoundFramework.dfmod.json").read_text(encoding="utf-8-sig"))
    require(manifest["ModVersion"] == "1.4.2", "candidate manifest version")
    require(manifest["Dependencies"] == [
        {"Name": "daggerfall.harmony", "IsOptional": False, "IsPeer": False}
    ], "required dependency without DFU-incompatible four-part Version field")
    files = manifest["Files"]
    require(len(files) == len(set(files)), "no duplicate manifest assets")
    require(all((src / Path(name).name).is_file() for name in files), "all manifest assets exist")
    require({Path(n).name for n in files if n.endswith(".cs")} ==
            {p.name for p in src.glob("*.cs")}, "manifest covers all ten source files")
    shipped = [p for p in root.rglob("*") if p.is_file() and
               not {"TestResults", "BuildOutput"}.intersection(p.relative_to(root).parts)]
    require(not any(p.suffix.lower() in {".dll", ".exe", ".nupkg"} or
                    p.name.lower().endswith(".dll.bytes") for p in shipped),
            "no runtime/reference binaries distributed in source package")
    hashes = json.loads((root / "source-hashes.json").read_text())
    require(set(hashes) == {p.name for p in src.glob("*.cs")}, "hash inventory covers current source")
    require(all(digest((src / n).read_bytes()) == value for n, value in hashes.items()),
            "current source hashes")
    evidence = json.loads((root / "HARMONY-MIGRATION-EVIDENCE.json").read_text())
    require(digest(args.baseline.read_bytes()) == evidence["baseline_zip_sha256"],
            "exact packaged 1.4.1 baseline")
    changed = {"SpriteSound_DFUIntegration.cs", "SpriteSound_NPCFRPatch.cs",
               "SpriteSoundHarmonyBootstrap.cs"}
    with zipfile.ZipFile(args.baseline) as archive:
        for p in sorted(src.glob("*.cs")):
            if p.name not in changed:
                require(p.read_bytes() == archive.read("SpriteSoundFramework-main/" + p.name),
                        "unchanged 1.4.1 source: " + p.name)
        old = json.loads(archive.read("SpriteSoundFramework-main/SpriteSoundFramework.dfmod.json"))
        require(all(manifest[k] == old[k] for k in ("GUID", "ModTitle", "ModDescription", "ModAuthor")),
                "maintainer's identity, title, description and credits retained")
    print("Package audit complete. Unity/game/platform validation is separate.")


if __name__ == "__main__":
    main()
