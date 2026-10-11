// PATCH HISTORY: the supplied original source kept one global musician suffix and
// start time. Random effect selection could also change that shared state.
// Each local band now owns its selection and clock in SpriteSoundEnsembles;
// ordinary random effects can no longer influence musicians. File indexing and
// clip caching are retained. Logging is optional in this single unified build.
// Provenance: compared with the user-supplied original files; see SOURCE-CHANGES.md.

// Shared SSF clip cache, sound-file index, pause routing, and musician timeline.
// One manager serves all emitters so the same decoded WAV can be reused.
// When logging is enabled at startup, report the audio-device voice budget.

using System.Collections.Generic;
using System.IO;
using UnityEngine;
using System.Linq;

namespace SpriteSoundFramework
{
    public class SpriteSoundManager : MonoBehaviour
    {
        public static SpriteSoundManager Instance
        {
            get;
            private set;
        }

        public const string SoundsSubFolder = "SpriteSounds";
        // The cache owns reusable decoded clips for the manager lifetime; stopping one
        // emitter must not unload a clip another is using. It is not an eviction cache.
        // Indexes describe files seen at startup. Adding WAVs or JSONs requires restart.
        readonly Dictionary<string, AudioClip> clipCache = new Dictionary<string, AudioClip>();
        readonly HashSet<SpriteSoundEmitter> emitters = new HashSet<SpriteSoundEmitter>();
        readonly HashSet<string> fileIndex = new HashSet<string>();
        readonly HashSet<string> configIndex = new HashSet<string>();
        bool isPaused = false;
        private SpriteSoundEnsembles ensembles;

        // Keep one persistent manager and build the sound/config indexes.
        // The debug AudioConfig message reports the Unity voice budget; it does not
        // change that budget. Destroying a duplicate prevents two managers broadcasting pause.
        void Awake()
        {
            if (Instance != null && Instance != this)
            {
                Destroy(gameObject);
                return;
            }

            Instance = this;
            DontDestroyOnLoad(gameObject);
            BuildIndices();
            ensembles = new SpriteSoundEnsembles(this);
            if (SpriteSoundLog.Enabled)
            {
                AudioConfiguration audioConfig = AudioSettings.GetConfiguration();
                SpriteSoundLog.Info($"[SpriteSound][AudioConfig] " + $"RealVoices={audioConfig.numRealVoices}, " + $"VirtualVoices={audioConfig.numVirtualVoices}, " + $"SampleRate={audioConfig.sampleRate}");
            }
        }

        // Index top-level WAV and JSON filenames once at manager startup.
        // An index stores names, not decoded audio. Clips are loaded on demand later.
        // Files added during a running session are not automatically reindexed.
        void BuildIndices()
        {
            fileIndex.Clear();
            configIndex.Clear();
            string folder = Path.Combine(Application.streamingAssetsPath, "Sound", SoundsSubFolder);
            if (Directory.Exists(folder))
            {
                string[] files = Directory.GetFiles(folder, "*.wav", SearchOption.TopDirectoryOnly);
                foreach (string f in files)
                {
                    fileIndex.Add(Path.GetFileNameWithoutExtension(f));
                }

                string[] configs = Directory.GetFiles(folder, "*.json", SearchOption.TopDirectoryOnly);
                foreach (string c in configs)
                {
                    configIndex.Add(Path.GetFileNameWithoutExtension(c));
                }

                SpriteSoundLog.Info($"[SpriteSound] Indexed {fileIndex.Count} sounds and {configIndex.Count} configs.");
            }
            else
            {
                SpriteSoundLog.Warning($"[SpriteSound] Sound folder not found at: {folder}");
            }
        }

        // Clear the singleton only if this is the current manager.
        // A duplicate manager being destroyed must not erase the live instance.
        void OnDestroy()
        {
            if (Instance == this)
            {
                ensembles?.Reset("manager-destroy");
                Instance = null;
            }
        }

        // Broadcast pause changes to ordinary emitters; tick the conductor every frame.
        // timeScale below 0.01 is the pause signal. Emitters handle their own range and
        // loading state, including work that completes between manager updates.
        void Update()
        {
            bool pausedNow = Time.timeScale < 0.01f;
            if (pausedNow != isPaused)
            {
                isPaused = pausedNow;
                foreach (var emitter in emitters)
                    if (emitter != null) emitter.SetPaused(isPaused);
            }
            ensembles?.Tick(emitters, pausedNow);
        }

        // Include an emitter in pause notifications.
        // A set prevents duplicate registration from creating duplicate notifications.
        public void RegisterEmitter(SpriteSoundEmitter emitter)
        {
            if (emitter != null)
                emitters.Add(emitter);
        }

        // Remove a destroyed emitter from pause notifications.
        // The emitter calls this during cleanup; the manager does not own its GameObject.
        public void UnregisterEmitter(SpriteSoundEmitter emitter)
        {
            if (emitter != null)
                emitters.Remove(emitter);
        }

        // Return an already decoded clip when available.
        // A cache hit avoids another WAV request; callers still validate the playback session.
        public bool TryGetCachedClip(string key, out AudioClip clip)
        {
            return clipCache.TryGetValue(key, out clip);
        }

        // Store a successfully decoded clip for reuse.
        // Null loads are not cached as successful results. Cache entries last for this manager.
        public void CacheClip(string key, AudioClip clip)
        {
            if (clip != null)
                clipCache[key] = clip;
        }

        // Check whether the startup index contains an exact WAV key.
        // This checks the index rather than touching the filesystem for every sprite.
        public bool HasSoundFile(string key)
        {
            return fileIndex.Contains(key);
        }

        // Attach when either a WAV or JSON exists for the resolved key.
        // A config-only key can define frame sounds even if it has no base looping WAV.
        public bool ShouldAttachEmitter(string key)
        {
            return fileIndex.Contains(key) || configIndex.Contains(key);
        }

        // Map a sound key to StreamingAssets/Sound/SpriteSounds.
        // Append .wav only when needed. Existing filename conventions are preserved.
        public string BuildFullPathForKey(string key)
        {
            string fileName = key.EndsWith(".wav") ? key : key + ".wav";
            return Path.Combine(Application.streamingAssetsPath, "Sound", SoundsSubFolder, fileName);
        }

        // Each band intersects these exact suffixes across its members. "-Tavern"
        // must mean the same song arrangement for every instrument. A base WAV alone
        // has no song identity, so it cannot silently substitute for a missing part.
        internal List<string> MusicianSongs(string baseKey)
        {
            string prefix = baseKey + "-";
            return fileIndex.Where(f => f.StartsWith(prefix, System.StringComparison.Ordinal) && f.Length > prefix.Length)
                .Select(f => f.Substring(prefix.Length)).OrderBy(f => f, System.StringComparer.Ordinal).ToList();
        }

        // Ordinary random effects have no access to a band's song or clock.
        // ORIGINAL COMPARISON: The shared helper originally changed CurrentSongForMusicians even for normal
        // random effects. Variant selection now has no global song side effect. Candidates
        // are sorted and use exact ordinal prefix matching, so seeded random sequences may
        // select a different order than the old hash-set iteration did.
        public string PickRandomVariant(string baseKey)
        {
            var songs = MusicianSongs(baseKey);
            return songs.Count == 0 ? baseKey : baseKey + "-" + songs[Random.Range(0, songs.Count)];
        }

        // World transitions retire pending loads and scheduled starts together.
        // Keep the historical entry-point name for the discovery hook.
        public void ResetMusicianTrack() { ensembles?.Reset("world-transition"); }
    }
}
