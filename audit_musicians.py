"""Read-only SSF musician library audit. Python 3, standard library only.

Example:
  python audit_musicians.py "C:/Game/.../Sound/SpriteSounds" --keys 182_37 182_50
Without --keys, checks every JSON marked isMusician. That is a library-wide
comparison, not a claim that those sprites occupy the same in-game stage.
"""
import argparse
import json
from pathlib import Path
import wave


def audit(folder, keys):
    errors = []
    if not folder.is_dir():
        raise ValueError(f"Directory does not exist: {folder}")
    if not keys:
        keys = []
        for config in sorted(folder.glob("*.json")):
            try:
                if json.loads(config.read_text(encoding="utf-8-sig")).get("isMusician"):
                    keys.append(config.stem)
            except (ValueError, OSError) as error:
                errors.append(f"Invalid JSON: {config.name}: {error}")
    if not keys:
        raise ValueError("No musician keys found; supply --keys or check the JSONs.")
    songs = {}
    for key in keys:
        prefix = key + "-"
        songs[key] = {p.stem[len(prefix):]: p for p in folder.glob("*.wav")
                      if p.stem.startswith(prefix) and len(p.stem) > len(prefix)}
        print(f"{key}: {', '.join(sorted(songs[key])) or '(no named songs)'}")
    common = set.intersection(*(set(songs[k]) for k in keys))
    print("Shared songs:", ", ".join(sorted(common)) or "NONE - group would stay silent")
    if not common:
        errors.append("No shared song suffix.")
    for song in sorted(common):
        durations = []
        for key in keys:
            try:
                with wave.open(str(songs[key][song]), "rb") as audio:
                    durations.append(audio.getnframes() / audio.getframerate())
            except (wave.Error, EOFError, OSError) as error:
                errors.append(f"Could not inspect {key}-{song}: {error}")
        if len(durations) != len(keys):
            continue
        shortest, longest = min(durations), max(durations)
        compatible = shortest > 0.15 and longest - shortest <= 0.10
        print(f"{song}: {shortest:.6f}..{longest:.6f}s; "
              f"{'duration check OK' if compatible else 'REJECTED duration mismatch'}")
        if not compatible:
            errors.append(f"Incompatible duration: {song}")
    for error in errors:
        print("CHECK:", error)
    print("Matching names and lengths do not prove matching tempo or musical alignment.")
    return 1 if errors else 0


if __name__ == "__main__":
    parser = argparse.ArgumentParser(description=__doc__)
    parser.add_argument("folder", type=Path)
    parser.add_argument("--keys", nargs="+")
    args = parser.parse_args()
    try:
        raise SystemExit(audit(args.folder, args.keys))
    except (ValueError, OSError) as error:
        parser.exit(2, str(error) + "\n")
