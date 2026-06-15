using System.Collections.Generic;
using System.IO;
using UnityEngine;
using System.Linq;

namespace SpriteSoundFramework
{
    public class SpriteSoundManager : MonoBehaviour
    {
        public static SpriteSoundManager Instance { get; private set; }

        public const string SoundsSubFolder = "SpriteSounds";

        // CACHES
        readonly Dictionary<string, AudioClip> clipCache = new Dictionary<string, AudioClip>();
        readonly HashSet<SpriteSoundEmitter> emitters = new HashSet<SpriteSoundEmitter>();
        
        // INDICES
        readonly HashSet<string> fileIndex = new HashSet<string>();
        readonly HashSet<string> configIndex = new HashSet<string>();

        bool isPaused = false;
        
        // --- NEW: Global Sync Variables ---
        public string CurrentSongForMusicians = null;
        public double MusicianStartTime { get; private set; } // Tracks WHEN the song started

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
        }

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

                Debug.Log($"[SpriteSound] Indexed {fileIndex.Count} sounds and {configIndex.Count} configs.");
            }
            else
            {
                Debug.LogWarning($"[SpriteSound] Sound folder not found at: {folder}");
            }
        }

        void OnDestroy()
        {
            if (Instance == this)
                Instance = null;
        }

        void Update()
        {
            bool pausedNow = Time.timeScale < 0.01f;
            if (pausedNow == isPaused) return;

            isPaused = pausedNow;
            foreach (var emitter in emitters)
            {
                if (emitter != null) emitter.SetPaused(isPaused);
            }
        }

        public void RegisterEmitter(SpriteSoundEmitter emitter)
        {
            if (emitter != null) emitters.Add(emitter);
        }

        public void UnregisterEmitter(SpriteSoundEmitter emitter)
        {
            if (emitter != null) emitters.Remove(emitter);
        }

        public bool TryGetCachedClip(string key, out AudioClip clip)
        {
            return clipCache.TryGetValue(key, out clip);
        }

        public void CacheClip(string key, AudioClip clip)
        {
            if (clip != null) clipCache[key] = clip;
        }

        public bool HasSoundFile(string key)
        {
            return fileIndex.Contains(key);
        }

        public bool ShouldAttachEmitter(string key)
        {
            return fileIndex.Contains(key) || configIndex.Contains(key);
        }

        public string BuildFullPathForKey(string key)
        {
            string fileName = key.EndsWith(".wav") ? key : key + ".wav";
            return Path.Combine(Application.streamingAssetsPath, "Sound", SoundsSubFolder, fileName);
        }

        public string PickRandomMusicianTrack(string baseKey)
        {
            if (CurrentSongForMusicians != null)
                return baseKey + "-" + CurrentSongForMusicians;

            return PickRandomFromIndex(baseKey);
        }

        public string PickRandomVariant(string baseKey)
        {
            return PickRandomFromIndex(baseKey);
        }

        private string PickRandomFromIndex(string baseKey)
        {
            var candidates = new List<string>();
            string prefix = baseKey + "-";

            foreach (string file in fileIndex)
            {
                if (file.StartsWith(prefix))
                    candidates.Add(file);
            }

            if (candidates.Count > 0)
            {
                string chosen = candidates[Random.Range(0, candidates.Count)];
                
                // If this is for a musician, update the shared track AND the start time
                if (CurrentSongForMusicians == null && chosen.StartsWith(baseKey + "-"))
                {
                     CurrentSongForMusicians = chosen.Substring(baseKey.Length + 1);
                     MusicianStartTime = AudioSettings.dspTime; // <--- SYNC TIMESTAMP
                }
                
                return chosen;
            }

            return baseKey;
        }

        public void ResetMusicianTrack()
        {
            CurrentSongForMusicians = null;
        }
    }
}