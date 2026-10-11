// PATCH HISTORY: the supplied original source reused the billboard's AudioSource.
// This version owns a child source instead, so DFU's random animal controller
// cannot rewind our clip or replace our gain. Animal suppression, safer async
// starts, game-volume scaling and diagnostic traces were added during this patch.
// Generation tickets, explicit range/game pause state and controlled playlist
// ownership were absent from the original. They were introduced during patching
// and later tightened as gameplay tests exposed additional lifecycle cases.
// Musician playback now belongs to SpriteSoundEnsembles. Keep the ordinary-loop
// path separate: a change for bands must not restart an animal or ambient loop.
// Provenance: compared with the user-supplied original files; see SOURCE-CHANGES.md.

// Sprite Sound Framework: per-billboard playback and spatial sound.
// Owns a child AudioSource, preserves paused loops, and cancels stale loading work.
// Grouped musician playback with optional diagnostics; non-musician fixes retained.
// A coroutine is work that can wait across frames; an emitter is one sprite's sound controller.
// The built-in DFU animal controller must never share SSF's AudioSource. Its random
// Play() calls were rewinding SSF clips, changing volume, and cutting off the fade tail.
// The logging checkbox enables playback, voice, and audio-device diagnostics. Quiet WAV passages
// and Unity choosing not to mix a voice are different from a stopped or restarted clip.

using System.Collections;
using System.Collections.Generic;
using UnityEngine;
using UnityEngine.Networking;
using DaggerfallWorkshop;
using DaggerfallWorkshop.Game;

namespace SpriteSoundFramework
{
    public class SpriteSoundEmitter : MonoBehaviour
    {
        // SSF owns the child source and filter; the parent can still host native DFU audio.
        // Think of the child as a separate player attached to this sprite. It follows the
        // sprite but has its own playback cursor and volume. Do not replace it with
        // GetComponent<AudioSource>() on the billboard: that recreates the ownership conflict.
        private AudioSource audioSource;
        private GameObject audioObject;
        // Remember native animal components and their prior enabled states for restoration.
        // Only the native random-animal preset on a 201_ key is suspended. We save whether
        // each component was enabled before taking over, so removing or disabling SSF can
        // return control without enabling a component that was originally disabled.
        private DaggerfallAudioSource nativeAnimalController;
        private AudioSource nativeAnimalSource;
        private bool nativeAnimalSuppressed;
        private bool nativeControllerWasEnabled;
        private bool nativeSourceWasEnabled;
        private AudioLowPassFilter lowPassFilter;
        private Transform playerTransform;
        private Billboard billboard;
        private int lastFrame = -1;
        // baseKey identifies the sprite; currentKey identifies its selected track or variant.
        // Several sprites can use the same key. Use the object ID in debug logs to tell
        // those separate physical animals apart; the key alone does not identify a player.
        private string baseKey;
        private string currentKey;
        private bool initialized = false;
        private bool loop = false;
        private bool isMusician = false;
        private bool isRandomized = false;
        private string musicianSyncGroup = "";
        private float musicianGroupRadius = 12f;

        // The conductor controls only looping musicians. All other sounds keep their
        // existing owner. A group label separates nearby bands; distance still applies.
        // Membership uses the musician flag and loop setting, not hearing range. A
        // member outside hearing range still constrains the shared song list. Audible
        // means eligible to schedule by distance/source state, not guaranteed heard:
        // volume, walls, silent WAV passages and Unity voice limits can still mute it.
        internal bool EnsembleMember => initialized && loop && isMusician && isActiveAndEnabled && gameObject.activeInHierarchy;
        internal bool EnsembleAudible => EnsembleMember && inPlaybackRange && audioSource != null && audioSource.isActiveAndEnabled;
        internal string EnsembleLabel => musicianSyncGroup;
        internal float EnsembleRadius => musicianGroupRadius;
        internal int EnsembleScene => gameObject.scene.handle;
        internal int EnsembleTicket => playbackGeneration;
        internal AudioSource EnsembleSource => audioSource;

        // A start time on Unity's audio clock avoids starting one musician per frame.
        // The cursor is expressed in samples so a returning listener joins the song
        // already in progress. Setting pitch and Doppler prevents accidental speed drift.
        // Only the conductor calls this method. StopPlayback cancels old source work,
        // then the new generation ticket lets the conductor detect a later range stop.
        // Scheduling does not disable native music sources or change a band's membership.
        internal void ScheduleEnsemble(AudioClip clip, string key, double dspStart, double seconds)
        {
            StopPlayback();
            currentKey = key;
            audioSource.clip = clip;
            audioSource.loop = false;
            audioSource.pitch = 1f;
            audioSource.dopplerLevel = 0f;
            audioSource.timeSamples = Mathf.Clamp((int)(seconds * clip.frequency), 0, clip.samples - 1);
            audioSource.PlayScheduled(dspStart);
        }

        internal void StopEnsemble() { StopPlayback(); }

        // Configuration volume before the game sound slider and distance-tail gain.
        // The final gain is configuration volume times the game's sound-effects volume.
        // Unity then applies normal 3D attenuation. A logged source volume is not the
        // actual loudness reaching the listener.
        private float originalVolume = 1.0f;
        private List<FrameSoundData> frameTriggers = null;
        private float targetCutoff = 22000f;
        private const float cutoffClear = 22000f;
        private const float cutoffMuffled = 1000f;
        private int wallLayerMask = 1 << 0;
        // Keep one playlist owner and a generation number for canceling obsolete work.
        // A generation is a session ticket. Stopping or changing the sprite increments it.
        // A WAV request that finishes later must still hold the current ticket before it
        // can touch the source. Stop only the owned playlist, not all emitter coroutines.
        private bool playlistRoutineActive = false;
        private int playbackGeneration = 0;
        private Coroutine playlistRoutine = null;
        // Range availability is separate from a paused source or a destroyed session.
        // Being outside range can preserve an established loop. Disabling the emitter or
        // changing its identity ends that session. These conditions must stay distinct.
        private bool inPlaybackRange = false;
        // Remember a loop paused because the listener moved away.
        // A source reports isPlaying=false when paused. That does not mean it needs a
        // new Play() call; resume the existing cursor with UnPause().
        private bool pausedByRange = false;
        // Keep game pause independent of distance pause.
        // Closing a menu must not wake a sound that is still outside its playback range.
        // Startup checks also inspect timeScale, because the manager and emitter can run
        // in different orders during the frame when a pause begins.
        private bool pausedByManager = false;
        // Limit periodic animal state samples to about one per second.
        // Samples run with the distance sensor, not a separate real-time timer. They are
        // not guaranteed during game pause. Explicit pause/resume events retain the cursor
        // needed to compare those boundaries.
        private float nextPlaybackTraceTime;
        // Expand logarithmic maxDistance once; linear sources retain their configured range.
        // For maxDistance=5, the calculation is 5 * 5 + 2 = 27. Do not multiply the resulting
        // AudioSource.maxDistance again. The multiplier changes reach, not the WAV contents.
        private const float playbackRangeMultiplier = 5.0f;
        private const float playbackRangePadding = 2.0f;
        public string Key => baseKey;
        public bool IsInitialized => initialized;
        // Allow an active sound two extra distance units before range suspension.
        // This small gap between the entry and exit boundaries prevents rapid toggling
        // when the player stands near the edge. Both boundaries include the fade tail.
        private const float playbackExitHysteresis = 2.0f;
        // Track voice state changes for debug logging.
        // A virtual source can still report playing=true: Unity advances the logical
        // playback but may not mix it into audible output. An initial virtual=true with
        // playing=false is not proof that a playing voice was taken away.
        private bool lastVirtualState = false;
        private bool virtualStateInitialized = false;
        // Keep author settings separate from the expanded Unity range.
        // minDistance is the near region before normal distance attenuation starts.
        // effectiveAudioDistance is assigned to Unity maxDistance, and is not the final
        // pause boundary; the additional fade tail extends beyond it.
        private float configuredMinDistance;
        private float configuredMaxDistance;
        private float effectiveAudioDistance;
        // Fade the logarithmic remainder beyond Unity maxDistance before suspending playback.
        // Unity logarithmic attenuation can leave a quiet remainder at maxDistance.
        // SSF lowers only that remainder across a clamped 6-12 unit tail. For a range
        // of 27, the tail is 6.75: entry is 33.75 and active exit is 35.75.
        private float playbackFadeTailDistance;
        private float playbackSuspendDistance;
        private const float playbackFadeTailRatio = 0.25f;
        private const float playbackFadeTailMin = 6.0f;
        private const float playbackFadeTailMax = 12.0f;
        // Lower Unity priority numbers are more important; favor nearby SSF sources.
        // The first 60% of the suspension radius keeps priority 112. Farther sources
        // gradually approach 220. This influences voice selection; it cannot guarantee
        // a real voice or increase Unity's total voice budget.
        private const int ssfNearPriority = 112;
        private const int ssfFarPriority = 220;
        private const float ssfPriorityFadeStart = 0.60f;
        // Create and configure the owned audio child, then register this emitter.
        // DFU's parent AudioSource remains separate. The low-pass filter also belongs on
        // the child so SSF muffling does not affect a native sound on the billboard.
        // No clip starts here; initialization and the distance check decide when to play.
        // ORIGINAL COMPARISON: RequireComponent and GetComponent originally used parent audio components.
        // They have been replaced by a child owned only by SSF. This changes ownership,
        // not just hierarchy: native controllers must not keep writing to the same source.
        void Awake()
        {
            audioObject = new GameObject("SSF Audio");
            audioObject.transform.SetParent(transform, false);
            audioSource = audioObject.AddComponent<AudioSource>();
            lowPassFilter = audioObject.AddComponent<AudioLowPassFilter>();
            lowPassFilter.cutoffFrequency = cutoffClear;
            audioSource.spatialBlend = 1.0f;
            audioSource.playOnAwake = false;
            audioSource.loop = false;
            billboard = GetComponent<Billboard>();
            if (SpriteSoundManager.Instance != null)
                SpriteSoundManager.Instance.RegisterEmitter(this);
        }

        // Resolve and cache the player transform when it is available.
        // Distance work waits if the game has not supplied a player object yet. This
        // helper does not create a player or take ownership of the player lifecycle.
        private bool EnsurePlayerTransform()
        {
            if (playerTransform != null)
                return true;
            if (GameManager.Instance == null || GameManager.Instance.PlayerObject == null)
                return false;
            playerTransform = GameManager.Instance.PlayerObject.transform;
            return playerTransform != null;
        }

        // Check the entry radius for explicit reactivation requests.
        // Use the fade-tail endpoint here. The repeating sensor separately adds the
        // exit margin for sources that are already in range.
        private bool IsWithinPlaybackRange()
        {
            if (audioSource == null || !EnsurePlayerTransform())
                return false;
            float range = playbackSuspendDistance;
            Vector3 delta = transform.position - playerTransform.position;
            return delta.sqrMagnitude <= range * range;
        }

        // Pause an established loop; cancel a pending or active playlist routine.
        // Only an established normal loop has finished its startup routine and can
        // retain its cursor through this path. A loading or randomized playlist is
        // canceled; musicians stop their source and later rejoin the conductor clock.
        private void SuspendPlaybackForRange()
        {
            if (audioSource == null)
                return;
            if (isMusician) { StopPlayback(); return; }
            if (playlistRoutine != null || playlistRoutineActive)
            {
                StopPlayback();
                return;
            }

            if (audioSource.isPlaying)
            {
                audioSource.Pause();
                pausedByRange = true;
                TraceAnimalPlayback("range-pause");
            }
        }

        // Resume the preserved range session when neither pause condition blocks it.
        // Returning true means this session is already handled, even if the game is
        // still paused. That prevents the caller from starting a replacement loop.
        // If the source or clip is unavailable, clear the range flag and allow recovery.
        private bool ResumePlaybackFromRange()
        {
            if (!pausedByRange)
                return false;
            if (pausedByManager || Time.timeScale < 0.01f)
                return true;
            if (audioSource == null || audioSource.clip == null || !audioSource.isActiveAndEnabled)
            {
                pausedByRange = false;
                return false;
            }

            pausedByRange = false;
            if (!pausedByManager)
            {
                audioSource.UnPause();
                TraceAnimalPlayback("range-resume");
            }

            return true;
        }

        // Invalidate old loading work, cancel the owned playlist, and stop the source.
        // This is a hard stop. It clears both pause flags and increments the generation
        // even if Unity currently reports isPlaying=false. A paused source still holds
        // state that must be discarded when its sprite is disabled or reconfigured.
        // ORIGINAL COMPARISON: StopAllCoroutines used to be the broad reset. We now cancel the owned playlist
        // and advance a generation ticket. One-shot requests may still finish, but must
        // check that ticket before playing. This prevents old work reviving a reused flat.
        private void StopPlayback()
        {
            TraceAnimalPlayback("stop-request");
            pausedByRange = false;
            pausedByManager = false;
            playbackGeneration++;
            if (playlistRoutine != null)
            {
                StopCoroutine(playlistRoutine);
                playlistRoutine = null;
            }

            playlistRoutineActive = false;
            if (audioSource != null)
                audioSource.Stop();
        }

        // Start a single playlist only when the emitter is ready, audible, and unpaused.
        // This is the normal loop entry point. A paused source is not a stopped source.
        // Checking the pause flags and timeScale prevents the two menu-time restarts
        // observed before the final guard was added. Existing playback and pending
        // playlist work each prevent a second owner from starting.
        private void EnsureLoopPlayback()
        {
            if (isMusician) return; // The group conductor is the sole musician playback owner.
            if (!initialized || pausedByManager || pausedByRange || Time.timeScale < 0.01f || !loop || !inPlaybackRange || !isActiveAndEnabled || !gameObject.activeInHierarchy || audioSource == null || !audioSource.isActiveAndEnabled || audioSource.isPlaying || playlistRoutineActive || playlistRoutine != null || string.IsNullOrEmpty(currentKey))
                return;
            int generation = playbackGeneration;
            playlistRoutine = StartCoroutine(GuardedPlaylistCoroutine(currentKey, generation));
        }

        // Track playlist ownership and clear it only for the current generation.
        // A normal fixed loop returns after Play(); Unity then repeats the clip itself.
        // A randomized playlist keeps this coroutine alive between tracks. Musicians
        // are scheduled directly by the conductor and never enter this coroutine.
        // An obsolete routine must not clear the ownership state of a newer session.
        private IEnumerator GuardedPlaylistCoroutine(string key, int generation)
        {
            playlistRoutineActive = true;
            yield return PlaylistCoroutine(key, generation);
            if (generation == playbackGeneration)
            {
                playlistRoutineActive = false;
                playlistRoutine = null;
            }
        }

        // Reject work that belongs to an old, disabled, or out-of-range session.
        // Pause is handled separately because a paused session can remain valid.
        // Call this after asynchronous waits and before playback; the sprite may have
        // changed while the WAV request was waiting.
        private bool PlaybackStillValid(int generation)
        {
            return generation == playbackGeneration && initialized && inPlaybackRange && isActiveAndEnabled && gameObject.activeInHierarchy && audioSource != null && audioSource.isActiveAndEnabled;
        }

        // Defer initialized emitters until the next frame.
        // Interior exits can enable many objects at once. Do not call Play() directly
        // here; give the hierarchy a frame to settle, then let the sensor decide.
        // ORIGINAL COMPARISON: OnEnable used to cancel every coroutine and immediately launch a playlist.
        // Now it defers to a later sensor pass. Expected result: pooled/re-enabled objects
        // wait until identity, range and pause conditions are valid before starting.
        void OnEnable()
        {
            if (initialized)
                StartCoroutine(DeferredPlaybackRefresh());
        }

        // Wait one frame before checking range and playback readiness.
        // Recheck component activity after waiting. The object may have been disabled
        // again during the transition.
        private IEnumerator DeferredPlaybackRefresh()
        {
            yield return null;
            if (isActiveAndEnabled)
                SlowSensorUpdate();
        }

        // End the SSF session and restore native animal component states.
        // Disabling the component is a real lifecycle boundary, not a temporary range
        // pause. Old requests must not resume into a later use of this object.
        void OnDisable()
        {
            inPlaybackRange = false;
            StopPlayback();
            RestoreNativeAnimalAudio();
        }

        // Cache the listener and schedule distance checks five times per second.
        // The randomized first delay spreads work across frames. It does not randomize
        // the starting point inside a WAV; fixed loops still start at time zero.
        void Start()
        {
            if (GameManager.Instance != null && GameManager.Instance.PlayerObject != null)
                playerTransform = GameManager.Instance.PlayerObject.transform;
            InvokeRepeating(nameof(SlowSensorUpdate), Random.Range(0f, 0.2f), 0.2f);
        }

        // Unregister this emitter, restore native ownership, and remove its audio child.
        // The child must also be removed if only the emitter component is destroyed
        // while its billboard survives. Unity null checks tolerate prior child cleanup.
        void OnDestroy()
        {
            RestoreNativeAnimalAudio();
            if (SpriteSoundManager.Instance != null)
                SpriteSoundManager.Instance.UnregisterEmitter(this);
            if (audioObject != null)
                Destroy(audioObject);
        }

        // Temporarily suspend the matching native animal sound once its replacement is ready.
        // Limit takeover to 201_ keys with DFU's PlayRandomlyIfPlayerNear preset.
        // Save prior enabled states before stopping the native source. Waiting until a
        // replacement clip is loaded avoids silencing native audio for a missing WAV.
        // This does not disable every DFU sound or modify DFU's source files.
        private void SuspendNativeAnimalAudio()
        {
            if (nativeAnimalSuppressed || !isActiveAndEnabled || string.IsNullOrEmpty(baseKey) || !baseKey.StartsWith("201_"))
                return;
            var controller = GetComponent<DaggerfallAudioSource>();
            if (controller == null || controller.Preset != AudioPresets.PlayRandomlyIfPlayerNear)
                return;
            nativeAnimalController = controller;
            nativeAnimalSource = controller.AudioSource;
            nativeControllerWasEnabled = controller.enabled;
            nativeSourceWasEnabled = nativeAnimalSource != null && nativeAnimalSource.enabled;
            nativeAnimalSuppressed = true;
            controller.enabled = false;
            if (nativeAnimalSource != null)
            {
                nativeAnimalSource.Stop();
                nativeAnimalSource.enabled = false;
            }

            TraceAnimalPlayback("native-suspended");
        }

        // Restore the native components to their saved enabled states.
        // Restoration is safe to call more than once. It does not force an immediate
        // native animal call; the restored controller resumes its own random schedule.
        private void RestoreNativeAnimalAudio()
        {
            if (!nativeAnimalSuppressed)
                return;
            nativeAnimalSuppressed = false;
            if (nativeAnimalSource != null)
                nativeAnimalSource.enabled = nativeSourceWasEnabled;
            if (nativeAnimalController != null)
                nativeAnimalController.enabled = nativeControllerWasEnabled;
            nativeAnimalSource = null;
            nativeAnimalController = null;
        }

        // Configure a new sound identity; preserve playback when the key is unchanged.
        // Repeated billboard scans are normal. The same key only refreshes the sensor.
        // A different key stops old playback and restores the previous native controller
        // before applying the next configuration. JSON cfg.loop controls looping; the
        // loopDefault argument is retained for compatibility but is not used here.
        // Initialization sets distance and gain but leaves startup to the range checks.
        // ORIGINAL COMPARISON: Initialize always stopped/restarted sound before. Same-key initialization now
        // only refreshes the sensor; a changed identity invalidates old work. The old
        // loopDefault parameter was already unused, so JSON loop still decides behavior.
        public void Initialize(string key, bool loopDefault)
        {
            if (string.IsNullOrEmpty(key))
                return;
            if (initialized && baseKey == key)
            {
                if (isActiveAndEnabled)
                    SlowSensorUpdate();
                return;
            }

            StopPlayback();
            RestoreNativeAnimalAudio();
            baseKey = key;
            currentKey = key;
            SpriteSoundConfigData cfg = SpriteSoundConfig.GetConfig(baseKey);
            if (cfg.frameSounds != null && cfg.frameSounds.Count > 0)
                frameTriggers = cfg.frameSounds;
            else
                frameTriggers = null;
            originalVolume = cfg.volume;
            audioSource.volume = originalVolume * DaggerfallUnity.Settings.SoundVolume;
            configuredMinDistance = Mathf.Max(0.01f, cfg.minDistance);
            configuredMaxDistance = Mathf.Max(configuredMinDistance + 0.1f, cfg.maxDistance);
            audioSource.minDistance = configuredMinDistance;
            switch (cfg.rolloff.ToLowerInvariant())
            {
                case "linear":
                    audioSource.rolloffMode = AudioRolloffMode.Linear;
                    effectiveAudioDistance = configuredMaxDistance;
                    break;
                default:
                    audioSource.rolloffMode = AudioRolloffMode.Logarithmic;
                    effectiveAudioDistance = configuredMaxDistance * playbackRangeMultiplier + playbackRangePadding;
                    break;
            }

            audioSource.maxDistance = effectiveAudioDistance;
            if (audioSource.rolloffMode == AudioRolloffMode.Logarithmic)
            {
                playbackFadeTailDistance = Mathf.Clamp(effectiveAudioDistance * playbackFadeTailRatio, playbackFadeTailMin, playbackFadeTailMax);
                playbackSuspendDistance = effectiveAudioDistance + playbackFadeTailDistance;
            }
            else
            {
                playbackFadeTailDistance = 0f;
                playbackSuspendDistance = effectiveAudioDistance;
            }

            loop = cfg.loop;
            isMusician = cfg.isMusician;
            musicianSyncGroup = (cfg.musicianSyncGroup ?? "").Trim();
            musicianGroupRadius = float.IsNaN(cfg.musicianGroupRadius) || float.IsInfinity(cfg.musicianGroupRadius)
                ? 12f : Mathf.Clamp(cfg.musicianGroupRadius, 0.5f, 100f);
            isRandomized = cfg.randomize;
            if (SpriteSoundManager.Instance != null)
            {
                if (!isMusician && isRandomized)
                    currentKey = SpriteSoundManager.Instance.PickRandomVariant(baseKey);
            }

            initialized = true;
            SlowSensorUpdate();
        }

        // Refresh a known emitter and update random variant selection when required.
        // This compatibility entry point is not used to restart same-key emitters on
        // every scan. A preserved range loop is resumed first; a genuinely different
        // selected key ends the old playback generation.
        public void Reactivate()
        {
            if (!initialized || !isActiveAndEnabled)
                return;
            if (billboard == null)
                billboard = GetComponent<Billboard>();
            if (!IsWithinPlaybackRange())
            {
                if (inPlaybackRange)
                {
                    inPlaybackRange = false;
                    SuspendPlaybackForRange();
                }

                return;
            }

            inPlaybackRange = true;
            if (!ResumePlaybackFromRange())
                EnsureLoopPlayback();
            inPlaybackRange = true;
            string targetKey = currentKey;
            if (SpriteSoundManager.Instance != null)
            {
                if (!isMusician && isRandomized && !audioSource.isPlaying && !playlistRoutineActive)
                {
                    targetKey = SpriteSoundManager.Instance.PickRandomVariant(baseKey);
                }
            }

            if (targetKey != currentKey)
            {
                StopPlayback();
                currentKey = targetKey;
                inPlaybackRange = true;
            }

            EnsureLoopPlayback();
        }

        // Load the chosen clip safely, then run fixed-loop or playlist playback.
        // Loading may take several frames, so check the generation again afterward.
        // A failed or missing clip waits before retrying instead of spinning every frame.
        // If loading finishes during game pause, keep waiting without discarding the
        // session. Normal loops start at zero once and hand repetition to Unity.
        // Randomized playlists choose a new variant after completion. Musicians use
        // SpriteSoundEnsembles instead. A pause must not be mistaken for a track ending.
        // ORIGINAL COMPARISON: The original already assigned audioSource.clip before calling Play for every
        // playlist mode. An intermediate patch briefly lost that assignment; restoring it
        // was a regression repair, not a correction to this original. Added generation,
        // range and pause checks protect when a successfully loaded clip is allowed to start.
        IEnumerator PlaylistCoroutine(string startKey, int generation)
        {
            string playKey = startKey;
            var mgr = SpriteSoundManager.Instance;
            if (!PlaybackStillValid(generation))
                yield break;
            currentKey = playKey;
            while (true)
            {
                if (mgr == null || !PlaybackStillValid(generation))
                    yield break;
                AudioClip clip = null;
                if (!mgr.TryGetCachedClip(playKey, out clip) || clip == null)
                {
                    string path = mgr.BuildFullPathForKey(playKey);
                    if (System.IO.File.Exists(path))
                    {
                        using (UnityWebRequest www = UnityWebRequestMultimedia.GetAudioClip("file://" + path, AudioType.WAV))
                        {
                            yield return www.SendWebRequest();
                            if (!PlaybackStillValid(generation))
                                yield break;
                            if (!www.isNetworkError && !www.isHttpError)
                            {
                                clip = DownloadHandlerAudioClip.GetContent(www);
                                mgr.CacheClip(playKey, clip);
                            }
                        }
                    }
                }

                if (!PlaybackStillValid(generation))
                    yield break;
                if (clip == null)
                {
                    yield return new WaitForSeconds(1.0f);
                    if (!PlaybackStillValid(generation))
                        yield break;
                    continue;
                }

                while (PlaybackStillValid(generation) && (pausedByManager || Time.timeScale < 0.01f))
                    yield return null;
                if (!PlaybackStillValid(generation))
                    yield break;
                if (!isMusician && !isRandomized)
                {
                    if (audioSource.isPlaying && audioSource.clip == clip && audioSource.loop)
                    {
                        yield break;
                    }

                    audioSource.clip = clip;
                    audioSource.loop = true;
                    if (!PlaybackStillValid(generation))
                        yield break;
                    SuspendNativeAnimalAudio();
                    audioSource.time = 0f;
                    audioSource.Play();
                    TraceAnimalPlayback("loop-start");
                    yield break;
                }

                audioSource.clip = clip;
                audioSource.time = 0f;

                audioSource.loop = false;
                SuspendNativeAnimalAudio();
                audioSource.Play();
                while (PlaybackStillValid(generation) && (audioSource.isPlaying || pausedByManager || Time.timeScale < 0.01f))
                {
                    yield return new WaitForSeconds(0.5f);
                }

                if (!PlaybackStillValid(generation))
                    yield break;
                if (isRandomized)
                {
                    playKey = mgr.PickRandomVariant(baseKey);
                }

                currentKey = playKey;
            }
        }

        // Update range, gain, priority, playback readiness, and occlusion periodically.
        // The sensor manages two boundaries: enter at the fade-tail endpoint, leave
        // after the additional exit margin. A healthy normal loop pauses on range exit.
        // Do not force-enable a disabled SSF source as a recovery shortcut.
        // The visibility probes sample several listener heights/offsets, so one blocked
        // ray does not automatically mean the whole sound should be muffled.
        // ORIGINAL COMPARISON: The original disabled the source outside its limit, except in small interiors.
        // Now all locations use the same entry/exit rules. Established fixed loops pause;
        // loading/random playlists cancel; musicians stop and later rejoin their group.
        // Do not re-enable an externally disabled source just because the player is near.
        void SlowSensorUpdate()
        {
            if (audioSource == null || !initialized || !EnsurePlayerTransform())
                return;
            float distance = Vector3.Distance(transform.position, playerTransform.position);
            UpdateVoicePriority(distance);
            if (SpriteSoundLog.Enabled && Time.realtimeSinceStartup >= nextPlaybackTraceTime)
            {
                nextPlaybackTraceTime = Time.realtimeSinceStartup + 1f;
                TraceAnimalPlayback("sample");
            }

            float managedVolume = GetDistanceManagedVolume(distance);
            if (Mathf.Abs(audioSource.volume - managedVolume) > 0.001f)
            {
                audioSource.volume = managedVolume;
            }

            float playbackLimit = playbackSuspendDistance;
            float exitLimit = playbackLimit + playbackExitHysteresis;
            bool shouldStop = inPlaybackRange ? distance > exitLimit : distance > playbackLimit;
            if (shouldStop)
            {
                if (inPlaybackRange)
                {
                    inPlaybackRange = false;
                    SuspendPlaybackForRange();
                }

                return;
            }

            inPlaybackRange = true;
            if (!audioSource.isActiveAndEnabled)
            {
                inPlaybackRange = false;
                if (audioSource.isPlaying || playlistRoutineActive || playlistRoutine != null)
                {
                    StopPlayback();
                }

                return;
            }

            inPlaybackRange = true;
            if (!ResumePlaybackFromRange())
                EnsureLoopPlayback();
            Vector3 src = transform.position + Vector3.up * 0.5f;
            Vector3 pPos = playerTransform.position;
            bool isVisible = false;
            if (!HasStaticOccluder(src, pPos + Vector3.up * 0.5f))
            {
                isVisible = true;
            }
            else if (!HasStaticOccluder(src, pPos + Vector3.up * 1.6f))
            {
                isVisible = true;
            }
            else if (!HasStaticOccluder(src, pPos + Vector3.up * 1.0f - playerTransform.right * 0.4f))
            {
                isVisible = true;
            }
            else if (!HasStaticOccluder(src, pPos + Vector3.up * 1.0f + playerTransform.right * 0.4f))
            {
                isVisible = true;
            }

            targetCutoff = isVisible ? cutoffClear : cutoffMuffled;
            if (!SpriteSoundLog.Enabled) { virtualStateInitialized = false; return; }
            bool virtualNow = audioSource.isVirtual;
            if (!virtualStateInitialized || virtualNow != lastVirtualState)
            {
                virtualStateInitialized = true;
                lastVirtualState = virtualNow;
                TraceAnimalPlayback("voice-change");
                SpriteSoundLog.Info($"[SpriteSound][Voice] " +
                $"key={baseKey} " +
                $"object={gameObject.GetInstanceID()} " +
                $"virtual={virtualNow} " +
                $"playing={audioSource.isPlaying} " +
                $"distance={distance:F2} " +
                $"maxDistance={audioSource.maxDistance:F2} " +
                $"volume={audioSource.volume:F3} " +
                $"priority={audioSource.priority}");
            }
        }

        // Test layer-zero geometry, ignoring triggers, billboards, and SSF emitters.
        // This is a filtering rule, not a guarantee that every remaining collider is
        // a static wall. The sprite must not block itself, and another animal or flat
        // should not be mistaken for a solid acoustic barrier.
        // ORIGINAL COMPARISON: Four raw Linecasts became four filtered multi-hit ray tests. Self-colliders,
        // triggers and other sound-bearing billboards no longer count as walls. The four
        // listener heights/offsets and the 1000/22000 Hz smoothing targets are retained.
        private bool HasStaticOccluder(Vector3 from, Vector3 to)
        {
            Vector3 direction = to - from;
            float distance = direction.magnitude;
            if (distance <= 0.001f)
                return false;
            RaycastHit[] hits = Physics.RaycastAll(from, direction.normalized, distance, wallLayerMask, QueryTriggerInteraction.Ignore);
            for (int i = 0; i < hits.Length; i++)
            {
                Collider col = hits[i].collider;
                if (col == null)
                    continue;
                if (col.transform == transform || col.transform.IsChildOf(transform))
                {
                    continue;
                }

                if (col.GetComponentInParent<Billboard>() != null)
                    continue;
                if (col.GetComponentInParent<SpriteSoundEmitter>() != null)
                    continue;
                return true;
            }

            return false;
        }

        // Smooth the low-pass filter and dispatch sounds when the billboard frame changes.
        // Distance checks run more slowly, but filter movement runs each frame to
        // avoid a sudden muffling step. Musicians skip frame one-shots: unrelated
        // sounds on their source could mask or disrupt the synchronized performance.
        void Update()
        {
            if (!initialized || !inPlaybackRange)
                return;
            if (billboard == null)
            {
                billboard = GetComponent<Billboard>();
                if (billboard == null)
                    return;
            }

            if (Mathf.Abs(lowPassFilter.cutoffFrequency - targetCutoff) > 10f)
            {
                lowPassFilter.cutoffFrequency = Mathf.Lerp(lowPassFilter.cutoffFrequency, targetCutoff, Time.deltaTime * 3f);
            }

            int currentFrame = billboard.Summary.CurrentFrame;
            if (!isMusician && currentFrame != lastFrame)
            {
                CheckFrameTriggers(currentFrame);
                lastFrame = currentFrame;
            }
        }

        // Play configured one-shots for the newly observed animation frame.
        // Each frame entry supplies a sound key. Variant selection occurs before
        // loading; this path does not replace or advance the continuous loop playlist.
        void CheckFrameTriggers(int frame)
        {
            if (frameTriggers == null)
                return;
            for (int i = 0; i < frameTriggers.Count; i++)
            {
                if (frameTriggers[i].frame == frame)
                {
                    string triggerKey = frameTriggers[i].soundKey;
                    if (SpriteSoundManager.Instance != null)
                        triggerKey = SpriteSoundManager.Instance.PickRandomVariant(triggerKey);
                    StartCoroutine(PlayOneShot(triggerKey));
                }
            }
        }

        // Load/cache a frame sound and play it only while its generation remains valid.
        // Frame-triggered loads need the same stale-work protection as loops.
        // Stopping the playlist alone does not cancel these independent coroutines,
        // so checks after loading and before PlayOneShot remain essential.
        // Known boundary: the generation check protects identity/range/lifecycle, not
        // game pause. Unlike PlaylistCoroutine, this path does not wait for an unpause
        // after its load completes. Do not describe the loop pause fix as covering every
        // frame-triggered sound. Musicians skip frame triggers in Update altogether.
        IEnumerator PlayOneShot(string key)
        {
            int generation = playbackGeneration;
            if (!PlaybackStillValid(generation))
                yield break;
            var mgr = SpriteSoundManager.Instance;
            if (mgr == null)
                yield break;
            AudioClip clip = null;
            if (!mgr.TryGetCachedClip(key, out clip) || clip == null)
            {
                string path = mgr.BuildFullPathForKey(key);
                if (!System.IO.File.Exists(path))
                    yield break;
                using (UnityWebRequest www = UnityWebRequestMultimedia.GetAudioClip("file://" + path, AudioType.WAV))
                {
                    yield return www.SendWebRequest();
                    if (!PlaybackStillValid(generation))
                        yield break;
                    if (!www.isNetworkError && !www.isHttpError)
                    {
                        clip = DownloadHandlerAudioClip.GetContent(www);
                        mgr.CacheClip(key, clip);
                    }
                }
            }

            if (clip != null && PlaybackStillValid(generation))
                audioSource.PlayOneShot(clip);
        }

        // Gradually reduce source importance near the suspension radius.
        // Lower numbers mean stronger preference in Unity. This changes scheduling
        // priority, not clip volume, and keeps distant SSF voices from having the same
        // importance as nearby ones.
        private void UpdateVoicePriority(float distance)
        {
            if (audioSource == null || effectiveAudioDistance <= 0.01f)
                return;
            float normalizedDistance = Mathf.Clamp01(distance / playbackSuspendDistance);
            float priorityBlend = Mathf.InverseLerp(ssfPriorityFadeStart, 1.0f, normalizedDistance);
            int desiredPriority = Mathf.RoundToInt(Mathf.Lerp(ssfNearPriority, ssfFarPriority, priorityBlend));
            if (audioSource.priority != desiredPriority)
                audioSource.priority = desiredPriority;
        }

        // Apply the game volume slider and the extra logarithmic fade tail.
        // Within effectiveAudioDistance, return config volume times SoundVolume;
        // Unity handles ordinary 3D attenuation there. Only beyond that point does
        // SSF fade the remainder to zero. Linear sources do not need this extra tail.
        // ORIGINAL COMPARISON: The original multiplied config volume by its own quadratic tail, without the
        // DFU Sound slider. The patch applies SoundVolume and a smooth outer tail after
        // Unity's expanded logarithmic range. This changes audible reach and loudness;
        // it is not merely a safer way to stop an otherwise identical attenuation curve.
        private float GetDistanceManagedVolume(float distance)
        {
            float baseVolume = originalVolume * DaggerfallUnity.Settings.SoundVolume;
            if (audioSource == null || audioSource.rolloffMode != AudioRolloffMode.Logarithmic)
            {
                return baseVolume;
            }

            if (distance <= effectiveAudioDistance)
                return baseVolume;
            if (distance >= playbackSuspendDistance)
                return 0f;
            float tailProgress = Mathf.InverseLerp(effectiveAudioDistance, playbackSuspendDistance, distance);
            float tailGain = 1.0f - Mathf.SmoothStep(0.0f, 1.0f, tailProgress);
            return baseVolume * tailGain;
        }

        // Record animal lifecycle and cursor information without adding stack traces.
        // rt is runtime wall-clock time, not video time; dsp is the audio clock.
        // Compare object and generation before comparing sample or clipTime. An equal
        // sample at pause and resume shows the position was preserved. A backward
        // cursor near the end may be a normal wrap, not a restart. Initial virtual
        // state alone is not evidence of lost audio. Distant idle samples are skipped.
        // The unified build retains this method, but the first guard skips all
        // sampling/formatting work when diagnostics are disabled.
        private void TraceAnimalPlayback(string reason)
        {
            if (!SpriteSoundLog.Enabled) return;
            if (string.IsNullOrEmpty(baseKey) || !baseKey.StartsWith("201_") || audioSource == null)
                return;
            AudioClip clip = audioSource.clip;
            float distance = playerTransform != null ? Vector3.Distance(transform.position, playerTransform.position) : -1f;
            if (reason == "sample" && !audioSource.isPlaying && (distance < 0f || distance > playbackSuspendDistance + playbackExitHysteresis))
                return;
            SpriteSoundLog.Info("[SpriteSound][Playback] " +
                $"event={reason} rt={Time.realtimeSinceStartup:F3} dsp={AudioSettings.dspTime:F3} " +
                $"frame={Time.frameCount} key={baseKey} object={gameObject.GetInstanceID()} " +
                $"generation={playbackGeneration} playing={audioSource.isPlaying} " +
                $"virtual={audioSource.isVirtual} active={isActiveAndEnabled} " +
                $"sourceActive={audioSource.isActiveAndEnabled} range={inPlaybackRange} " +
                $"source={audioSource.GetInstanceID()} nativeSuppressed={nativeAnimalSuppressed} " +
                $"rangePaused={pausedByRange} managerPaused={pausedByManager} " +
                $"routine={playlistRoutineActive} timeScale={Time.timeScale:F3} " +
                $"clipTime={(clip != null ? audioSource.time : -1f):F3} " +
                $"sample={(clip != null ? audioSource.timeSamples : -1)} " +
                $"length={(clip != null ? clip.length : 0f):F3} " +
                $"distance={distance:F2} volume={audioSource.volume:F3} " +
                $"priority={audioSource.priority} pitch={audioSource.pitch:F3} " +
                $"cutoff={(lowPassFilter != null ? lowPassFilter.cutoffFrequency : -1f):F0}");
        }

        // Pause active audio and resume only the sessions this manager paused.
        // Range-paused sources must stay paused after a menu closes. Record a manager
        // pause only for a playing source; on resume require an active source, a clip,
        // and an in-range session. Startup and playlist guards cover asynchronous work
        // that crosses the pause boundary.
        // This method governs ordinary sources only. Musicians return immediately so
        // the conductor can save one group cursor and reschedule all parts together.
        // Never use a logging flag as a condition for Pause, UnPause, Stop or Play.
        // ORIGINAL COMPARISON: Pause/UnPause originally applied unconditionally to the source. Ordinary
        // sources now track whether the manager actually paused them and whether range
        // still allows resume. Musician pause is handled once by the group conductor.
        public void SetPaused(bool p)
        {
            if (isMusician) return; // The conductor freezes and reschedules the whole band.
            if (audioSource == null)
                return;
            if (p)
            {
                if (audioSource.isPlaying)
                {
                    audioSource.Pause();
                    pausedByManager = true;
                    TraceAnimalPlayback("manager-pause");
                }
            }
            else
            {
                if (!pausedByManager)
                    return;
                pausedByManager = false;
                if (!pausedByRange && inPlaybackRange && audioSource.clip != null && audioSource.isActiveAndEnabled)
                {
                    audioSource.UnPause();
                    TraceAnimalPlayback("manager-resume");
                }
            }
        }
    }
}
