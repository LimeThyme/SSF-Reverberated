# Harmony migration: 1.4.2 candidate

## Purpose and scope

1.4.1 opened `StreamingAssets/Mods/0Harmony.dll` itself. 1.4.2 declares
`daggerfall.harmony` as a required, earlier-loading mod and uses its library.
The dependency name is the filename without `.dfmod`, not the display title.
The original mod GUID and the maintainer's Reverberated title/description remain.

This candidate changes how compatibility hooks start. The emitter, ensemble,
manager, settings data, identity component, key resolver, config loader and logger
retain their packaged 1.4.1 contents. It does not change WAVs or song timing.
The two material-identity postfix bodies also retain their behavior.

## Why startup needs more than a dependency entry

The reviewed Daggerfall.Harmony provider creates a SubModule during mod Init.
Its Unity Start method checks its library version and calls UnpatchAll. Installing
SSF hooks in our old Init location could let that later cleanup remove them.
The provider does not mark its Mod.IsReady true; waiting for that flag would hang.

Our new bootstrap does the following:

1. Checks the exact dependency filename, enabled state, earlier load position,
   minimum provider version, and presence of exactly one active SubModule.
2. Yields one frame. The provider was already active before this yield, giving
   its synchronous Start a chance to finish before our continuation.
3. Checks that the provider is still active, then enters the Harmony-using helper.
4. Confirms there is exactly one loaded 0Harmony assembly, version 2.4.2.0, and
   that Daggerfall.Harmony's HarmonyRef derives from that same Harmony type.
5. Installs and checks the expected postfixes.
6. Yields another frame and checks ownership again. Only then does it create
   SpriteSoundHook, start normal discovery, and set SSF's IsReady true.

The first barrier relies on the reviewed provider's synchronous Start implementation.
It is not a general readiness protocol for arbitrary future provider versions.
The second barrier detects removal during the tested startup window; it does not
monitor every later frame or automatically fight another mod by repatching.
Material assignments made by other mods before our hooks exist cannot be observed
retroactively. Normal texture/vanilla key fallbacks remain; verify NPCFR identities
on initial game load as well as on later scene changes.

## Version checks and failure behavior

The supported provider is 2.4.2.2, with library version 2.4.2.0. These numbers mean
different things: the fourth provider number is its own build revision.
Later provider revisions satisfy the minimum check but must still meet the active
component/type/library contract. A new library version deliberately requires review.

DFU 1.1.1's IsVersionLowerOrEqual accepts at most three numeric components in either
input. Putting the provider's four-part version into manifest Version would produce
a false incompatibility result. We therefore omit Version there and perform the
four-part check at runtime. Do not "fix" this by putting 2.4.2 in the manifest:
the provider's four-part target string would still fail that DFU checker.

On startup failure, SSF reports an error and leaves discovery unstarted and IsReady
false. It removes any hooks it installed using its own owner ID only. Restart the
game after correcting the dependency; logging toggles do not retry initialization.
DFU may also report missing assembly references before SSF can issue its own error.
This is particularly relevant when the dependency is entirely absent.

NPC Flat Replacer remains optional. A missing type is reported as absent. A found
type with a different SetMaterial signature produces a warning and skips that
optional hook. Missing vanilla cleanup, patch exceptions, library conflicts or
lost installed hooks fail startup. No blanket success message hides skipped hooks.

## Changed code and expected results

| File | Change | Expected result |
| --- | --- | --- |
| SpriteSound_DFUIntegration.cs | Starts bootstrap after settings; guards duplicate entry calls | One diagnostic session and one discovery owner |
| SpriteSoundHarmonyBootstrap.cs | New dependency and lifecycle checks | Cleanup precedes SSF patching; discovery waits for retention check |
| SpriteSound_NPCFRPatch.cs | Typed Harmony calls, assembly checks, target reports, owner cleanup | Shared library; no loose-file load; other owners survive shutdown |
| SpriteSoundFramework.dfmod.json | Required non-peer dependency; version 1.4.2; new source entry | DFU can identify dependency and order it ahead of SSF |
| DeveloperTests/Build-Candidate.ps1 | Explicit compilation reference and staged assembly | Harmony is available to the compiler but absent from SSF distribution |

The patch owner remains `com.spritesound.npcfr_fix`. Cleanup always supplies that
owner to UnpatchAll; it never requests global cleanup. Only one SSF edition should
be enabled because two editions using the same owner are not independent mods.
Failure and destruction clean up partial installations; repeated patch Init checks
the existing hooks rather than adding a second postfix.

## Build and packaging

Obtain Lib.Harmony 2.4.2 from NuGet and extract `lib/net472/0Harmony.dll` outside
the package. Its identity matches the provider's expected 2.4.2.0 library.
The reference hash and package URL are recorded in HARMONY-MIGRATION-EVIDENCE.json.

Run DeveloperTests/Build-Candidate.ps1 with the Unity Editor directory, existing
DFU project directory and reference DLL path. It writes a local BuildOutput folder.
The generated `.dll.bytes` is an SSF assembly, not a Harmony copy. The generated
manifest includes that assembly plus modsettings.json; it retains dependencies.
Copy the generated staging folder to the exact Assets path described in README.md,
then use Mod Builder for Windows/macOS/Linux bundles. No prebuilt platform bundle
has been produced or tested for this candidate.

Do not use the old source-only build instructions without adding a proper compile
reference. Do not place both old SSF source and the new assembly under Assets when
testing. Do not include TestResults, BuildOutput caches, or a reference 0Harmony.dll
in a source release. The validator checks distribution files separately from local
test/build outputs. Our scripts do not alter installed mods or delete shared DLLs.

## Validation tools

- Run-HarmonyTests.ps1 compiles production bootstrap/patch code against the real
  Harmony library. Lightweight Unity/DFU stand-ins let us advance coroutine barriers
  deliberately. It runs once without NPCFR and once with a compatible stand-in.
- The tests exercise missing/disabled/old/wrong-order dependencies, missing/disabled/
  duplicate providers, provider loss, incompatible library provider type, startup
  cleanup, actual postfix execution, repeated Init, owner-only cleanup, late hook
  removal and shutdown. They do not simulate Unity's actual frame scheduler.
- Run-Tests.ps1 retains the existing 19 ensemble tests. These check scheduling
  decisions using a simulated audio device, not audible output.
- Run-SchemaTests.ps1 uses DFU's actual serializer and types to verify dependency
  flags, the omitted Version field, and the retained logging checkbox/default.
- validate_package.py checks the manifest, files, dependency name/flags, hashes,
  forbidden distributed DLLs, and byte preservation of seven 1.4.1 source files.
- audit_musicians.py is unchanged: it checks filenames/configuration/WAV durations.
  It cannot establish that Harmony loaded or that recordings sound aligned.

## Manual game checks still required

Use a backed-up test installation and a fresh process for each startup scenario.
Enable SSF logging before launching the successful test so the startup sequence
is captured. Keep Player.log as well; dependency errors may precede the SSF logger.

| Scenario | Expected observation |
| --- | --- |
| Provider 2.4.2.2 first, no conflicting loose library | One library identity; required hook; ready marker; normal discovery |
| Dependency missing/disabled/below SSF | DFU dependency notice and/or explicit SSF error; no successful ready marker |
| Old loose 2.3.6 library conflicts | Unsupported/conflicting-library report; no silent fallback |
| NPCFR absent | Optional absent notice; vanilla sound identities still work |
| NPCFR present | Replacement hook listed; initial and later replacements retain correct sound keys |
| Vanilla material reused on a replaced billboard | Old custom identity disappears |
| Enter/leave interiors and reload a save | Sounds attach correctly without repeated restarts |
| Animal, pause/resume, hearing range exit/return | Same behavior as the 1.4.1 baseline |
| Musician group, pause, range return and song changes | Shared timeline preserved; record any Tavern offset separately |
| Logging off/on | No playback reset; expected log rotation/append behavior |
| Exit/relaunch and Editor Play Mode cycles | No duplicate bootstrap or stale owned patches |
| Windows, macOS and Linux64 builds | Each actual bundle loads dependency and executes hooks; no cross-platform claim from Windows tests |

Passing a simulated cleanup test is not proof of Unity startup timing. Capture
the ready marker and check actual sound identities in-game before promoting the
candidate to a tested release. If a future provider changes cleanup timing, review
its source and update the lifecycle tests before relaxing the checks.
