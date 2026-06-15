using System.Collections.Generic;
using System.IO;
using UnityEngine;

namespace SpriteSoundFramework
{
    // 1. Define the structure for a frame trigger
    [System.Serializable]
    public struct FrameSoundData
    {
        public int frame;       // The frame number (0, 1, 2...)
        public string soundKey; // The sound to play (e.g. "BroomSweep")
    }

    [System.Serializable]
    public class SpriteSoundConfigData
    {
        public bool loop = true;
        public float volume = 1.0f;
        public float minDistance = 1.0f;
        public float maxDistance = 12.0f;
        public string rolloff = "logarithmic";
        
        // 2. Replaced the broken Dictionary with a List
        public List<FrameSoundData> frameSounds = new List<FrameSoundData>();

        public bool isMusician = false;
        public bool randomize = false;
    }

    public static class SpriteSoundConfig
    {
        private static readonly Dictionary<string, SpriteSoundConfigData> cache =
            new Dictionary<string, SpriteSoundConfigData>();

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
                    Debug.LogError($"[SpriteSound] Failed reading JSON for {key}: {ex}");
                }
            }

            SpriteSoundConfigData def = new SpriteSoundConfigData();
            cache[key] = def;
            return def;
        }

        private static string BuildJsonPath(string key)
        {
            return Path.Combine(
                Application.streamingAssetsPath,
                "Sound",
                "SpriteSounds",
                key + ".json"
            );
        }
    }
}