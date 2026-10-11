# SSF original-source comparison and debugging guide

## Reference and scope

This comparison uses the seven original files from `SpriteSoundSource.zip`

The comparison target is the latest unified source in this package, including
musician ensembles, the logging checkbox, and the corrected resume header.
Hashes at the end identify the exact reference files. No upstream commit or
release number was supplied; this is a comparison with these files, not a claim
about every published version of SSF.

This replaces the earlier comparison with an already-patched local snapshot.
The earlier snapshot was useful for tracking recent edits but hid some changes
that are clear against the original. All changes in this review are comments or
documentation. The current sound behavior, settings and header fix are unchanged.

C# syntax comparisons ignore comments and spacing. The accompanying
`ORIGINAL-METHOD-COMPARISON.txt` lists original methods as unchanged, changed,
log-routing-only, removed or added. A signature change appears as removal plus
addition; that does not mean the entire feature disappeared. Identical method
text can also call a changed helper, so the explanations below describe behavior.

## Important corrections to the earlier history

- Original SSF already attempted synchronized musician playback using one global
  song suffix and start time. Local ensembles replace that approach; synchronization
  itself is not an entirely new SSF concept.

- The original emitter already assigned the loaded clip before playback. Restoring
  a missing assignment in an intermediate patch repaired our patch history; the
  supplied original did not have that omission.

- Generation tickets, explicit range/game pause flags and controlled coroutine
  ownership are changes from the original. They were already present in the older
  local snapshot, which is why the previous guide described them as retained.

- Identity storage, key resolution, material hooks, startup file indexing, decoded
  clip caching, hybrid scans and frame-sound lists all existed in the original.
  They should not be credited as newly invented by this patch.

## 1. Who owns the sound player

**Original:** `SpriteSoundEmitter.Awake` requires and retrieves an AudioSource and
low-pass filter on the billboard itself. If another component controls that same
source, either component can stop it, change its volume, or start it again.

**Current:** SSF creates the child `SSF Audio` with its own source and filter.
The emitter removes that child when destroyed. For `201_` animal keys only, once a
replacement clip is ready, it suspends a parent DaggerfallAudioSource only when its
preset is `PlayRandomlyIfPlayerNear`. It records the previous enabled states and
restores them on disable, destruction or identity change.

**Reason and expected result:** native random animal playback cannot rewind or
change SSF's source, and the replaced native call should not play on top of it.
This is a narrow animal-specific takeover, not a global sound or music mute.

**Debugging:** compare object, source and generation IDs. Check `nativeSuppressed`
when a replacement starts. A missing clip should not trigger native suppression.
Restoring the native controller does not guarantee it immediately emits a sound;
its own random schedule resumes. Its already-running sound position is not saved.

## 2. Starting, stopping, loading and rediscovering a sprite

**Original:** OnEnable immediately starts a loop playlist when initialized.
Initialize stops sound and all coroutines, then starts again. Same-key discovery
calls Reactivate, which can treat `isPlaying == false` as a reason to restart.
A load may wait across frames without checking whether the original object/session
is still the intended owner. Destruction only unregisters the emitter.

**Current:** one playlist owner is tracked. A generation number acts as a ticket:
hard stops advance it, and loading work must still have the same ticket before
playing. Range, activity and source state are checked too. OnEnable waits a frame
before consulting the sensor. OnDisable ends the session and restores native
ownership. Same-key Initialize refreshes the sensor; same-key scanner Attach
returns without calling Reactivate. Changed keys get full reconfiguration.

**Reason and expected result:** repeated scans, pooled objects, loads finishing
late, and world transitions should not create duplicate playlists or revive an
old clip. OneShot coroutines are not all explicitly canceled; their tickets prevent
obsolete completion from playing. Do not replace this with StopAllCoroutines
without considering the unrelated work it would cancel.

**Debugging:** a stop-request prints state before generation advances. Compare the
same physical object and generation before calling a backward cursor a restart.
A fixed loop wrapping from its end to zero is expected. Coroutines waiting during
pause are not necessarily stuck loads. The original and current implementations
both obtain loop behavior from JSON; the loopDefault argument was already unused.

## 3. Range, gain, source priority and wall muffling

| Behavior | Original | Current and expected result |
| --- | --- | --- |
| Range suspension | Disables AudioSource past the silence limit outside small interiors; re-enables it when eligible | Explicit range state in all locations. Established fixed loops pause; pending/random playlists stop; musicians rejoin their group later. Does not force-enable an externally disabled source. |
| Logarithmic reach | Unity maxDistance stays at the configured distance; SSF fades across a five-times-distance tail | Unity maxDistance becomes configured maxDistance × 5 + 2. SSF then adds a 6–12 unit smooth tail. This intentionally changes audible reach. |
| Boundary behavior | One distance threshold with source enabled toggles | Extra two-unit exit margin prevents repeated stop/start at the boundary. Returning must reach the entry boundary. |
| Gain | Config volume and a quadratic outer fade | Config volume × DFU SoundVolume, with a smooth outer fade for logarithmic sources. Unity still applies its own 3D attenuation. |
| Voice priority | No explicit SSF distance-based priority | Nearby sources use 112; farther sources approach 220. Lower numbers have higher priority. This cannot increase Unity's voice capacity. |
| Wall checks | Four raw layer-zero Linecasts | Four filtered multi-hit tests, excluding triggers, self and billboard/emitter colliders. Ordinary remaining blockers muffle the sound. |

For configured logarithmic maxDistance=5, the current Unity range is 27, the tail
is 6.75, the entry boundary is 33.75, and exit is beyond 35.75 units. Linear sounds
retain their configured range without the added logarithmic tail.

The sensor still runs five times per second with a randomized first delay. The
four listener test positions and 1000/22000 Hz filter targets are retained from
the original. The helper named HasStaticOccluder does not check Unity's static flag;
it filters collider types. Do not infer that every remaining hit is a static wall.

Logged volume is source gain before full spatial mixing, not listener loudness.

## 4. Pause behavior differs by playback mode

**Original:** SetPaused simply calls Pause or UnPause on the source. Playlist
completion waits on playing/enabled state without explicit manager pause tracking.
Musician start time remains a global DSP timestamp.

**Current:** ordinary sources record whether game pause actually paused them;
unpause also checks range and source validity. A fixed loop paused for distance
must not wake just because a menu closes. Playlist startup checks pause before
starting and again after a load. Random playlists wait through pause without
mistaking it for the end of a song.

Musicians bypass the emitter's pause method. The conductor saves one group cursor,
stops its scheduled/playing sources, then schedules eligible members together from
that cursor on resume. This differs from ordinary loop UnPause and deliberately
keeps the group on one timeline. Game time acceleration does not accelerate songs.

**Remaining boundary:** frame-triggered one-shots have generation/range checks,
but no equivalent post-load wait for unpause. The pause protection is not universal
across every sound path. The comment review documents this without changing it.

## 5. Musician selection and timing

**Original:** the manager stores CurrentSongForMusicians and MusicianStartTime for
all musicians. Each emitter constructs its own baseKey plus the chosen suffix,
loads that part, seeks into the global timeline, and can clear the song when it
finishes. The selection helper can also set musician state for an ordinary random
effect. There is no per-stage membership or all-members file compatibility check.

**Current:** SpriteSoundEnsembles is new. It forms local groups whose members all
share a scene and label and are pairwise within the smaller radius (default 12).
It checks membership twice per second, using sorted instance IDs, then waits 0.75
seconds after a new group forms. Distance grouping cannot recognize walls and is
not a designer-authored stage map. Labels come from per-key JSON, not each placed
copy of a flat.

The conductor intersects exact, case-sensitive song suffixes, avoids immediately
repeating the previous song when another choice exists, and loads needed parts
sequentially. It rejects lengths differing by more than 100 ms; shorter differences
are treated as export padding. The shortest part determines the group's end, so
a longer tail can be cut. It schedules eligible members 150 ms ahead on Unity's
audio clock, using sample positions, pitch 1 and Doppler off.

**Expected result:** one group changes song once; a member with an extra solo song
cannot choose it independently. Returning members seek to the current position.
New/removed members cause regrouping and a fresh coordinated start; hearing range
alone does not alter membership. Groups with no playable shared song stay silent
rather than choosing unrelated tracks. Load failures/timeouts reject a suffix for
that group lifetime. A reset aborts old requests and scheduled starts.

**Limits:** musical arrangement, tempo and leading silence must match in the WAVs.
The code does not edit or time-stretch recordings, deduplicate full mixes, animate
instruments to the beat, guarantee voices, or preload gapless song changes.
Frame one-shots are suppressed for musician emitters; the original allowed them.

The ordinary random variant helper no longer touches musician state; it now sorts
candidates and uses ordinal matching, so exact random sequences can differ.

**Debugging:** inspect formed/member events before checking timing. A Voice event
alone does not mean a flat joined an ensemble: isMusician and loop must be true.
Compare group/song/scheduled DSP/cursor values first, then actual sample reports.
0.0 ms is rounded state, not proof of perfect speaker timing. The earlier roughly
63 ms Tavern offset remains unproven as fixed. The later successful four-member
run did not include that song. No automatic seek correction chases tiny drift.

## 6. Logging and the settings checkbox

**Original:** operational messages use Unity Debug calls. There is no dedicated
writer or logging checkbox in the supplied seven source files.

**Current:** SpriteSoundLog and the modsettings.json asset are new. Init loads the
saved Enable Mod Logging setting before diagnostics start. The default is false;
the exact requested tooltip is included. Applying options changes diagnostics
without resetting playback. Off skips periodic diagnostic sampling and closes the
writer; actionable warnings/errors still reach Unity. On writes below Mod.DirPath
in SSF/Logs, keeping SSF.log and SSF-prev.log.

First enable in a game run rotates the previous log. Later enables append. An
entirely disabled run leaves old files unchanged. Failures fall back to Unity while
enabled, and off/on permits a fresh open attempt. Shutdown closes the writer and
blocks late teardown messages. An exclusive writer lock prevents two instances
from rotating the same current log concurrently. Rotation copies before truncating;
a failed backup therefore leaves current content intact.

First header: `# SSF log session opened ...`.
Re-enable header: `# SSF logging resumed (same session) ...`.

Both are followed by the normal enable event. These are logging intervals, not
new gameplay sessions. No messages during off cannot prove what audio did then.

## 7. Verified retained code

- **SpriteSoundIdentity.cs:** executable tokens are identical to the original.

- **SpriteSoundKeyResolver.cs:** executable tokens are identical, including identity,
  texture and vanilla fallback order and trailing-number cleanup. Cleanup can
  remove a record-looking suffix; this is inherited behavior, not a new fix.

- **SpriteSound_NPCFRPatch.cs:** identical executable tokens after mapping logger
  calls back to Unity Debug calls. Harmony path, reflection signatures and postfixes
  are unchanged. Its general success notice does not count optional targets.

- **SpriteSoundConfig.cs:** loading/default caching is unchanged apart from error
  routing. Group label/radius fields are added. FrameSoundData and existing defaults
  are retained. Missing/bad configs cache defaults; restart to see edits.

- **Manager cache/index methods:** retained except message routing. Files are
  indexed once; successful clips are cached without an eviction policy.

- **Discovery schedule:** sixteen half-second startup/transition scans and five-second
  exterior/dungeon/castle scans are retained. Same-key Attach behavior changed as
  described above. The unchanged ResetMusicianTrack call now retires group state.

- **Emitter Start and CheckFrameTriggers bodies:** unchanged. Their callers and
  playback guards changed, so overall behavior is not necessarily identical.

## 8. What to inspect first for a future failure

| Symptom | First checks | Avoid assuming |
| --- | --- | --- |
| Flat makes no sound | Resolved base key, indexed base WAV/JSON, activity, range, JSON values | A suffixed WAV alone guarantees attachment |
| Animal restarts | Physical object and generation, owned source, nativeSuppressed, loop boundary | Every cursor wrap is an unwanted restart |
| Band member missing | isMusician/loop, scene, label, radius, member events | Every musician-looking sprite is configured as a musician |
| Band silent | Shared suffixes, rejected/blocked events, WAV duration/load state | A missing part should fall back to any available song |
| Parts disagree | Shared song/start/cursor, actual samples, WAV alignment | Equal lengths prove equal arrangements |
| Resume sounds wrong | Fixed loop vs playlist vs musician vs one-shot path | Every path uses UnPause in the same way |
| No SSF.log updates | Checkbox state, disable/enable markers, writable Mod.DirPath, fallback warnings | The absence of diagnostics proves no audio work occurred |
| NPC replacement sound key wrong | Identity tag, texture naming, normalization, Harmony targets | The generic patch success line proves every optional hook was installed |

## Exact original file hashes (SHA-256)

- SpriteSound_DFUIntegration.cs: `c515b8c62375f01fe044fb5897451e900828e0a21072f4e1531b1719edd64027`
- SpriteSound_NPCFRPatch.cs: `9646f5cc263609b990be537bf68f1dbade13d859e25060223a5940b75947f8d2`
- SpriteSoundConfig.cs: `b9ce1272d98e9fcadc7e01c50d85219fbfc3f6b400d11b20c4af2fd1439ef0ff`
- SpriteSoundEmitter.cs: `6c1825a9c110e448de8298a290f86ecdb902967ba110d6f56273f412f4bc5212`
- SpriteSoundIdentity.cs: `fe3254a790eb107ed86deca7fcf63ce0b4e1c67fafb32c5a0e4cf21d4e0ae23f`
- SpriteSoundKeyResolver.cs: `fca72eeae4d5210f4f4514eb9bb38baa183fc35f888428c262f08ce9edcbf552`
- SpriteSoundManager.cs: `9dcf47d8bab2df4c8c899473ca35c5bfe478924487990dd4519f91070586c7db`
