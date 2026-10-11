# Sprite Sound Framework — unified patched source

Version **1.4.1**. See [CHANGELOG.txt](CHANGELOG.txt) for the version history from 1.1 through 1.4.1.
One build now supplies the latest musician synchronization and optional logging.
The older debug/release packages are retained only as rollback snapshots.

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

## Build and install

This is a source package; it still requires DFU Mod Builder to make a `.dfmod`.

Replace the project's SSF source folder with the enclosed `SpriteSoundFramework-main`
folder, then build its manifest. The manifest includes **modsettings.json**; that
asset must be bundled for the checkbox to appear. Keep only one SSF binary enabled.

The GUID is unchanged so this replaces the existing mod rather than adding another.
No installed project or game files were modified while preparing this package.

The musician implementation remains the latest tested experiment. The prior
four-member test was successful.

However, should you not want to build the mod yourself, a prebuilt copy for WIN/OSX/LINUX64
has been provided for you under [Builds/StandaloneLinux64](Builds/StandaloneLinux64),
[Builds/StandaloneOSX](Builds/StandaloneOSX), and [Builds/StandaloneWindows](Builds/StandaloneWindows).

## Validation

- Full source compiled against installed Unity 2019.4.41f2 and DFU assemblies.
- The actual DFU settings deserializer verified ToggleKey, false default, section,
  label, and exact tooltip.
- 10 filesystem regression checks and 12 toggle checks passed.
- 19 conductor tests passed, including playback with logging disabled.

The supplied gameplay log confirms the settings callback disables logging and
re-enables it by appending. It cannot establish uninterrupted audio during the
disabled interval. The header wording correction has automated regression coverage.

The read-only `audit_musicians.py` and simulated conductor tests remain included.
Run `DeveloperTests/Run-Tests.ps1 -UnityEditorDirectory <Unity Editor directory>`
to rerun conductor tests. Test source uses `.cs.txt` to keep stand-ins out of Unity.

## Comment review and log header correction

Re-enabling now prints `logging resumed (same session)` instead of another session
opening header. All nine source files have been reviewed and annotated for the
unified behavior. See [SOURCE-CHANGES.md](SOURCE-CHANGES.md) for provenance,
per-file changes, expected behavior and debugging limits. The comparison has now been rerun against the seven original files supplied from the source folder. The guide identifies that exact reference.

The settings menu and file toggle have now been exercised in the supplied gameplay
log. This documentation/header revision compiled and passed 13 toggle checks and
10 logger regression checks. The rest of the executable source is token-identical
to the previous unified build. No new playback feature or fix is claimed.

## Original-source comparison update

The source comments and [SOURCE-CHANGES.md](SOURCE-CHANGES.md) now use the supplied original SSF files.
Identity/key resolution are unchanged; Harmony behavior is unchanged apart from
log routing. Generation/range ownership safeguards and local ensemble coordination
are verified deviations. This update changes documentation only.


## Early source history

[CHANGELOG-1.1-to-1.3.8.txt](CHANGELOG-1.1-to-1.3.8.txt) reconstructs the original ZIP and twelve backup snapshots.
Intermediate entries use backup labels because exact release numbers are unavailable.
[EARLY-HISTORY-EVIDENCE.json](EARLY-HISTORY-EVIDENCE.json) records hashes and comparisons with the current source.
The full [CHANGELOG.txt](CHANGELOG.txt) includes both this early history and the later releases.
