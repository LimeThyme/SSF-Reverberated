using UnityEngine;
using DaggerfallWorkshop;
using System.Text.RegularExpressions;

namespace SpriteSoundFramework
{
    public static class SpriteSoundKeyResolver
    {
        // Regex to remove trailing dash/underscore and optional numbers at the end
        // Matches: "-0", "-", "_0", "_"
        private static readonly Regex cleanupPattern = new Regex(@"[-_]\d*$|[-_]$");

        public static string ResolveKeyFromGameObject(GameObject go)
        {
            if (go == null) return null;

            string finalKey = null;

            // --- STRATEGY 1: CHECK FOR IDENTITY TAG (NPC Flat Replacer Fix) ---
            // If the Harmony patch worked, the name is saved right here.
            var identity = go.GetComponent<SpriteSoundIdentity>();
            if (identity != null && !string.IsNullOrEmpty(identity.SoundKey))
            {
                // Clean up the key (remove trailing "-" or "-0")
                finalKey = cleanupPattern.Replace(identity.SoundKey, "");
                
                // Debug.Log($"[SpriteSound] Found Identity: {identity.SoundKey} -> {finalKey}");
                return finalKey;
            }

            // --- STRATEGY 2: TRY TEXTURE NAME (Loose Files / Other Mods) ---
            Renderer rend = go.GetComponent<Renderer>();
            if (rend != null && rend.sharedMaterial != null && rend.sharedMaterial.mainTexture != null)
            {
                string texName = rend.sharedMaterial.mainTexture.name;

                if (!string.IsNullOrEmpty(texName) && !texName.ToLower().Contains("atlas"))
                {
                    if (texName.EndsWith(" (Instance)"))
                        texName = texName.Substring(0, texName.Length - 11);

                    finalKey = cleanupPattern.Replace(texName, "");
                    return finalKey;
                }
            }

            // --- STRATEGY 3: FALLBACK TO VANILLA IDS ---
            Billboard billboard = go.GetComponent<Billboard>();
            if (billboard != null)
            {
                BillboardSummary summary = billboard.Summary;
                if (summary.Archive >= 0 && summary.Record >= 0)
                {
                    return summary.Archive.ToString() + "_" + summary.Record.ToString();
                }
            }

            return null;
        }
    }
}