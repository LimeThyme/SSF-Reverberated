# Sprite Sound Framework - Reverberated 1.4.2

This build replaces SSF's loose Harmony DLL loader with a required dependency
on **daggerfall.harmony.dfmod**. It is based on the final packaged 1.4.1, including
the maintainer's Reverberated manifest. The 1.4.1 archive is preserved separately.

**Status:** source compiled; 32 migration checks and 19 conductor checks passed.
This is not a gameplay-tested release or a set of prebuilt platform .dfmod files.
The known Tavern timing observation remains unresolved. This change concerns
startup and compatibility hooks; sound configuration and recordings are unchanged.

## Requirements and installation for players

- DFU 1.1.1 is the development target.
- Install and enable Daggerfall.Harmony **2.4.2.2**, providing Harmony **2.4.2.0**,
  above Reverberated. Later provider builds pass the minimum-version check but
  must still supply the supported library and startup contract. Future Harmony
  library versions need a deliberate compatibility review.
- Install the built Reverberated .dfmod and the desired SSF sound pack. Only one
  SSF/Reverberated edition should be enabled.
- Reverberated no longer supplies or opens a loose 0Harmony.dll. An old loose DLL
  may cause a version conflict. Back it up outside active loading folders only
  after checking whether another mod requires it; this patch never deletes it.
- The old instruction to pair Harmony .dfmod with SSF's loose DLL is obsolete for
  1.4.2. It still describes the older 1.4.1 setup, not this build.

Provider: https://www.nexusmods.com/daggerfallunity/mods/1192

Upstream instructions: https://github.com/VNGNCTeam/Daggerfall.Harmony

## Enable Mod Logging

In DFU Mod Options, open Sprite Sound Framework and its **Options** section.

The checkbox is titled **Enable Mod Logging**, defaults to **off**, and has this
exact tooltip:

> Creates separate log file directory in the Mods Folder under 'SSF/Logs'. Used for diagnosing issues with the mod.

Apply the setting through DFU's settings window. The callback applies it during
play without restarting audio or rebuilding the mod. DFU saves the preference.

When off, no SSF diagnostic directory is created and periodic voice, animal and
ensemble samples are skipped. Existing logs remain untouched. Actionable warnings
and errors still use Unity's normal log, as in the former release edition.

When on, diagnostics go to the loaded mod's own `SSF/Logs/SSF.log`. The first
activation in a new game session rotates the old file to `SSF-prev.log`. Disabling
closes the file. Re-enabling in the same session appends without another rotation.
An entirely disabled session leaves both existing logs unchanged. If enabled
logging cannot open its directory, it warns once and falls back to Unity's log;
disabling the checkbox also stops those fallback diagnostic messages.

## Configuration and code both matter

Configuration supplies compatible recordings. Code chooses a song for the band
and starts each recording at the same position on Unity's audio clock.

For example, `182_37-Tavern.wav` and `182_50-Tavern.wav` share the song name
`Tavern`. That suffix is case-sensitive. It must identify the same composition,
tempo, arrangement, and start point. Separate drum/flute/lute parts work if
exported against that same timeline. Complete mixes also work, but playing several
copies can make the result louder or produce interference. This version does not
detect duplicate arrangements, remix instruments, or synchronize sprite animation.

Extra solo songs are allowed. A trio chooses only suffixes present for all three
members. With no shared playable song, the entire band stays silent and logs why;
a missing part never falls back to an unrelated track or the unsuffixed base WAV.

## Group settings

Existing musician JSONs work without edits. These optional fields are merged into
each sprite's existing JSON; preserve its volume, distance and other settings:

```json

{
  "isMusician": true,
  "loop": true,
  "musicianSyncGroup": "",
  "musicianGroupRadius": 12.0
}

```

- Empty group labels enable automatic grouping of nearby musicians. Different
  labels, such as `stage-a` and `stage-b`, keep nearby ensembles separate.

- Every pair in a group must be within the smaller configured radius. The default
  is 12 world units; the accepted range is 0.5â€“100. Groups must share a Unity scene.

- Distance grouping does not recognize rooms or walls. Use smaller radii or
  different labels where necessary. Labels belong to sprite-key JSONs: two placed
  copies of the same key cannot receive different labels through these JSONs alone.

- `randomize` is ignored for musicians; the conductor chooses their songs. Other
  randomized effects retain their own playback and cannot change musician songs.

- Looping musicians are controlled by the conductor. Frame-triggered one-shots
  are suppressed on musician sources so unrelated sounds cannot intrude on a part.

## What the conductor does

1. Finds active musician sprites twice per second and waits 0.75 seconds after a
   group forms. This gives discovery scans time to populate a stage.

2. Intersects the members' song suffixes, chooses one, and loads all needed parts.

3. Checks decoded durations. Parts differing by more than 100 ms are rejected.
   A smaller difference is treated as export padding; the next song is governed
   by the shortest part. Equal durations still cannot prove musical compatibility.

4. Queues every audible part for the same audio-clock start, 150 ms ahead. Pitch
   remains 1 and Doppler is disabled for these sources to prevent speed changes.

5. Freezes the group cursor on pause. On resume, all audible parts are scheduled
   together from that cursor. Leaving hearing range stops only that local source;
   returning joins the group's current song position.
   
6. Advances the playlist once per group. New/destroyed musicians cause regrouping
   and a fresh coordinated song after the settling delay. Range changes alone do
   not alter membership. World transitions cancel pending requests and starts.

Songs load sequentially per band and come from the existing shared clip cache.
A failed load or incompatible length excludes that song for the group's lifetime.
Requests time out after 15 seconds. If nothing compatible remains, one `blocked`
event explains the silence. Restart after editing files; filenames and JSONs are
cached. There is a small scheduling/loading gap between songs; this is not a
gapless mixer. An inaudible band does not preload its next song.

## Debugging

The existing logger still writes `<loaded mod directory>/SSF/Logs/SSF.log` and
rotates the previous session to `SSF-prev.log`. Search for `[Ensemble]`:

| Event | Plain-language meaning |
| --- | --- |
| `formed`, `member` | Group identity, member IDs, sprite keys and shared songs |
| `selected`, `load` | Song selected and a part being loaded |
| `timeline`, `scheduled` | Shared audio-clock start and each source's seek cursor |
| `pause`, `resume` | Saved group song position |
| `sample` | About every 10 seconds: playing/virtual state and cursor difference |
| `recover` | A stopped source rejoined the current group position |
| `rejected`, `blocked` | Unusable part, mismatched duration, or no shared song |
| `song-end`, `retired` | Group advances or is removed/reset |

`driftMs` compares Unity's reported sample cursor with the conductor. It is a
diagnostic estimate, not a measurement of audible speaker timing. Virtual voices
may advance without being mixed. This version does not repeatedly seek a playing
source to chase small reported differences. Device voice limits still apply.

`audit_musicians.py` is a read-only, standard-library Python 3 tool. Pass the
SpriteSounds directory and optionally `--keys 182_37 182_50` to check a prospective
group's common filenames and WAV durations without launching the game. With no
keys it compares all musician configs; runtime grouping is local, not library-wide.

## Build this source package

Use `DeveloperTests/Build-build.ps1` with `-UnityEditorDirectory`,
`-DfuProjectDirectory`, and `-HarmonyAssembly`. Supply Lib.Harmony 2.4.2's **net472**
0Harmony.dll as the compile reference, stored outside the release package.
The script uses the project's existing Library/ScriptAssemblies and Unity 2019.4
compiler. It creates `BuildOutput/SSF-Reverberated-142` locally.

Copy that generated folder to `Assets/Untracked/ModBuilder/SSF-Reverberated-142`
in the DFU project, open its generated manifest in Mod Builder, and build the
required platform asset bundles. The staging manifest includes SSF's precompiled
`.dll.bytes` and modsettings.json, not the C# files or Harmony DLL. No game or
project folder is changed by the script itself.

Keep the old SSF source folder out of Assets when testing the new staged assembly;
duplicate classes from old source can obscure which implementation is running.
Do not install a .dll.bytes file directly as a mod. It must be bundled by Mod Builder.

This separate compilation step supplies the explicit Harmony reference without
modifying DFU's ModAssemblyBuilder or copying Harmony into the mod. The normal
source manifest is retained for review and custom build pipelines; the generated
staging manifest is the intended packaging route for this build.

## Developer validation and evidence

- `DeveloperTests/Run-HarmonyTests.ps1`: real Harmony 2.4.2 patching under Unity Mono,
  with DFU/Unity stand-ins, in vanilla and NPCFR-present configurations.
- `DeveloperTests/Run-Tests.ps1`: the existing 19 simulated conductor checks.
- `DeveloperTests/Run-SchemaTests.ps1`: manifest dependency and logging settings
  checked with DFU's actual serializer and types.
- `validate_package.py`: hashes, dependency declaration, excluded DLLs, source assets,
  and comparison of unchanged playback files with the supplied 1.4.1 ZIP.
- `audit_musicians.py`: existing read-only WAV/config audit; it does not test Harmony.
- `HARMONY-MIGRATION.md`: rationale, file changes, expected logs, limits and manual
  game checks. Read this before changing the bootstrap ordering.
- `HARMONY-MIGRATION-EVIDENCE.json`: upstream commit and reference hashes.
- `VALIDATION.txt`: what actually passed and what still needs game/platform testing.

See [CHANGELOG.txt](CHANGELOG.txt) for 1.1 through this build. Historical notes describe the
versions named in their headings and do not override current installation steps.
