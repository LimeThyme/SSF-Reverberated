// PATCH HISTORY: the existing per-key JSON format is retained. The two new
// musician group fields supply optional grouping hints; missing fields keep the
// 12-unit automatic grouping default. The Enable Mod Logging checkbox lives in
// DFU's separate modsettings.json asset, not in these individual sound configs.
// Provenance: compared with the user-supplied original files; see SOURCE-CHANGES.md.

// JSON configuration records and per-key configuration cache.
// Public field names are the on-disk format. Keep them compatible with existing
// JSONs. FrameSoundData links an animation frame number to a sound key.

using System.Collections.Generic;
using System.IO;
using UnityEngine;

namespace SpriteSoundFramework
{
    [System.Serializable]
    public struct FrameSoundData
    {
        public int frame;
        public string soundKey;
    }

    [System.Serializable]
    public class SpriteSoundConfigData
    {
        // Default playback settings used when a config file is missing.
        // These defaults are retained from the supplied original source.
        // The JSON may override them; volume is later multiplied by the game sound slider.
        // Here loop=true requests continuous playback, not necessarily Unity clip.loop.
        // Ordinary fixed sounds loop in Unity. Randomized sounds and musicians instead
        // advance their playlists. A musician needs loop=true to join the conductor.
        public bool loop = true;
        public float volume = 1.0f;
        public float minDistance = 1.0f;
        public float maxDistance = 12.0f;
        public string rolloff = "logarithmic";
        // Optional animation-frame sound triggers.
        // A list is used so Unity JSON serialization can read each frame/soundKey pair.
        public List<FrameSoundData> frameSounds = new List<FrameSoundData>();
        public bool isMusician = false;
        // Nearby musicians with the same label form a band. An empty label enables
        // automatic grouping. Labels are per sprite key, not per placed stage instance.
        // Every pair must be within the smaller of their radii, measured in world units.
        public string musicianSyncGroup = "";
        public float musicianGroupRadius = 12f;
        public bool randomize = false;
    }

    public static class SpriteSoundConfig
    {
        // Bad JSON falls back to defaults for that key and emits an operational error.
        // A fallback is cached too, so fixing the file does not repair the live instance.
        // Field values are not comprehensively validated here; the emitter clamps ranges.
        // When debugging, inspect the JSON and resolved key as well as the audible result.
        private static readonly Dictionary<string, SpriteSoundConfigData> cache = new Dictionary<string, SpriteSoundConfigData>();
        // Read and cache a key's JSON, or cache defaults when loading fails.
        // Errors are logged. Both loaded settings and fallback defaults are cached, so
        // editing a JSON during a session does not automatically refresh an existing key.
        public static SpriteSoundConfigData GetConfig(string key)
        {
            if (cache.TryGetValue(key, out SpriteSoundConfigData cfg))
                return cfg;
            string path = BuildJsonPath(key);
            if (File.Exists(path))
            {
                try
                {
                    string json = File.ReadAllText(path);
                    SpriteSoundConfigData loaded = JsonUtility.FromJson<SpriteSoundConfigData>(json);
                    if (loaded != null)
                    {
                        cache[key] = loaded;
                        return loaded;
                    }
                }
                catch (System.Exception ex)
                {
                    SpriteSoundLog.Error($"[SpriteSound] Failed reading JSON for {key}: {ex}");
                }
            }

            SpriteSoundConfigData def = new SpriteSoundConfigData();
            cache[key] = def;
            return def;
        }

        // Locate the key's JSON beside its WAV assets.
        // No asset contents are rewritten by this loader.
        private static string BuildJsonPath(string key)
        {
            return Path.Combine(Application.streamingAssetsPath, "Sound", "SpriteSounds", key + ".json");
        }
    }
}
