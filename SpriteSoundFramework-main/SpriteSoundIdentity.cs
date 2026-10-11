// RETAINED ORIGINAL CODE: this field and component carry the replacement name
// across material changes. Our playback and grouping changes do not change its
// meaning. It is neither a stable save-game identifier nor an ensemble label.
// Provenance: compared with the user-supplied original files; see SOURCE-CHANGES.md.

// Persistent sound-name tag used by replacement billboards.
// NPC Flat Replacer can replace materials and textures after object creation.
// This component keeps the intended sound identity available to the resolver.

using UnityEngine;

namespace SpriteSoundFramework
{
    public class SpriteSoundIdentity : MonoBehaviour
    {
        // Replacement name/prefix before resolver normalization.
        // This is a sound lookup hint, not a unique ID for a physical NPC or animal.
        public string SoundKey;
    }
}
