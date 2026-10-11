// RETAINED ORIGINAL CODE: lookup precedence and suffix cleanup are unchanged.
// Investigate the resolved base key before diagnosing a missing WAV. Cleanup can
// remove a trailing underscore-number even from a name resembling archive_record;
// only the final vanilla fallback guarantees that archive_record spelling.
// Changing normalization is a separate compatibility change, not a comment fix.
// Provenance: compared with the user-supplied original files; see SOURCE-CHANGES.md.

// Resolve a billboard to an SSF WAV/JSON lookup key.
// Resolution order is custom identity, usable texture name, then vanilla archive/record.

using UnityEngine;
using DaggerfallWorkshop;
using System.Text.RegularExpressions;

namespace SpriteSoundFramework
{
    public static class SpriteSoundKeyResolver
    {
        // Normalize trailing dash/underscore suffixes using the existing filename convention.
        // The rule removes a final separator and optional digits. It is applied to identity
        // and texture names, not the archive_record fallback. This cleanup policy is
        // preserved; changing it could affect existing replacement-asset names.
        private static readonly Regex cleanupPattern = new Regex(@"[-_]\d*$|[-_]$");
        // Prefer saved identity, then texture name, then vanilla billboard IDs.
        // Texture names containing atlas are skipped because a shared atlas does not
        // identify one sprite. Remove the Unity instance suffix before normalization.
        // A usable texture name returns immediately; this method does not check whether
        // a matching sound file exists. The discovery hook performs that check later.
        public static string ResolveKeyFromGameObject(GameObject go)
        {
            if (go == null)
                return null;
            string finalKey = null;
            var identity = go.GetComponent<SpriteSoundIdentity>();
            if (identity != null && !string.IsNullOrEmpty(identity.SoundKey))
            {
                finalKey = cleanupPattern.Replace(identity.SoundKey, "");
                return finalKey;
            }

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
