// PATCH HISTORY: the supplied original source already had hybrid discovery scans and
// replacement-billboard hooks. This version adds mod-local logging initialization,
// the saved Mod Options callback, and orderly logger shutdown. Transition resets
// now retire whole musician groups, including their pending loads and starts.
// Discovery finds objects; it must not become a second owner of audio playback.
// Provenance: compared with the user-supplied original files; see SOURCE-CHANGES.md.

// DFU startup and billboard discovery for Sprite Sound Framework.
// Scans discover eligible sprites. Emitters, not scans, own ongoing playback.

using UnityEngine;
using DaggerfallWorkshop;
using DaggerfallWorkshop.Game;
using DaggerfallWorkshop.Game.Utility.ModSupport;
using DaggerfallWorkshop.Game.Serialization;
using System.Collections;
using DaggerfallWorkshop.Game.Utility.ModSupport.ModSettings;
using System.Reflection;
using System;

namespace SpriteSoundFramework
{
    public static class SpriteSound_DFUIntegration
    {
        private static Mod mod;
        // Install compatibility hooks and create the persistent discovery component.
        // DFU invokes this entry point at startup. Harmony support is initialized before
        // the scanner begins attaching emitters.
        [Invoke(StateManager.StateTypes.Start, 0)]
        public static void Init(InitParams initParams)
        {
            mod = initParams.Mod;
            SpriteSoundLog.BeginSession(mod != null ? mod.DirPath : null);
            // Load saved checkbox state before patches or emitters produce diagnostics.
            mod.LoadSettingsCallback = ApplySettings;
            mod.LoadSettings();
            SpriteSound_NPCFRPatch.Init();
            var go = new GameObject("SpriteSoundFramework_Hook");
            UnityEngine.Object.DontDestroyOnLoad(go);
            go.AddComponent<SpriteSoundHook>();
            SpriteSoundLog.Info("[SpriteSound] DFU integration init (Hybrid Scan Mode)");
        }
        // DFU invokes this both for initial settings and when the player applies edits.
        // Read its saved boolean; do not invent a second preference file. The callback
        // only changes diagnostics. It must not rescan sprites or reset audio timelines.
        // modsettings.json must be in the build manifest for this key to exist in DFU.
        private static void ApplySettings(ModSettings settings, ModSettingsChange change)
        {
            SpriteSoundLog.SetEnabled(settings.GetValue<bool>("Options", "Enable Mod Logging"));
        }
    }

    public class SpriteSoundHook : MonoBehaviour
    {
        public static SpriteSoundHook Instance
        {
            get;
            private set;
        }

        private float scanTimer = 0f;
        private const float ScanInterval = 5.0f;
        // Publish the hook instance and begin the initial rapid scan sequence.
        // The routine waits before its first scan; manager setup occurs in OnEnable.
        private void Awake()
        {
            Instance = this;
            UnityEngine.Object.DontDestroyOnLoad(gameObject);
            StartCoroutine("RapidScanRoutine");
        }

        // Ensure a manager exists and subscribe to load/transition events.
        // Each event restarts discovery because the visible billboard set may change.
        private void OnEnable()
        {
            EnsureManager();
            PlayerEnterExit.OnTransitionInterior += OnTransitionInterior;
            PlayerEnterExit.OnTransitionDungeonInterior += OnTransitionDungeonInterior;
            PlayerEnterExit.OnTransitionExterior += OnTransitionExterior;
            SaveLoadManager.OnLoad += OnSaveGameLoaded;
        }

        // Remove load/transition subscriptions.
        // Unsubscription prevents a disabled hook from receiving later transition callbacks.
        private void OnDisable()
        {
            PlayerEnterExit.OnTransitionInterior -= OnTransitionInterior;
            PlayerEnterExit.OnTransitionDungeonInterior -= OnTransitionDungeonInterior;
            PlayerEnterExit.OnTransitionExterior -= OnTransitionExterior;
            SaveLoadManager.OnLoad -= OnSaveGameLoaded;
        }

        // Close the debug file when this persistent hook is destroyed, including
        // Editor Play Mode exit. This does not change discovery or audio ownership.
        private void OnDestroy()
        {
            if (Instance == this) SpriteSoundLog.EndSession();
        }

        private void OnSaveGameLoaded(SaveData_v1 saveData) => RestartRapidScan();
        private void OnTransitionInterior(PlayerEnterExit.TransitionEventArgs args) => RestartRapidScan();
        private void OnTransitionDungeonInterior(PlayerEnterExit.TransitionEventArgs args) => RestartRapidScan();
        private void OnTransitionExterior(PlayerEnterExit.TransitionEventArgs args) => RestartRapidScan();
        // Retire musician groups and replace the rapid discovery routine.
        // Stop the named rapid routine before starting another; do not accumulate overlapping
        // scan bursts after consecutive transitions.
        private void RestartRapidScan()
        {
            SpriteSoundManager.Instance?.ResetMusicianTrack();
            StopCoroutine("RapidScanRoutine");
            StartCoroutine("RapidScanRoutine");
        }

        // Scan sixteen times at half-second intervals after startup or a transition.
        // The nominal eight-second window catches objects that appear over several frames.
        // WaitForSeconds uses game time, so pausing can extend its real-world duration.
        private IEnumerator RapidScanRoutine()
        {
            for (int i = 0; i < 16; i++)
            {
                yield return new WaitForSeconds(0.5f);
                ScanAllBillboards();
            }
        }

        // Continue five-second discovery scans outside small interiors.
        // Exteriors, dungeons, and castles may reveal additional objects as the player moves.
        // Small buildings rely on the initial rapid scans because their contents load together.
        private void Update()
        {
            bool isSmallBuilding = GameManager.Instance.IsPlayerInside && !GameManager.Instance.IsPlayerInsideDungeon && !GameManager.Instance.IsPlayerInsideCastle;
            if (!isSmallBuilding)
            {
                scanTimer += Time.deltaTime;
                if (scanTimer >= ScanInterval)
                {
                    scanTimer = 0f;
                    ScanAllBillboards();
                }
            }
        }

        // Find active billboards and attach eligible emitters.
        // This scans the loaded active billboard set, not just audible sources. Emitters
        // perform their own distance checks before starting sounds.
        private void ScanAllBillboards()
        {
            if (SpriteSoundManager.Instance == null)
                EnsureManager();
            foreach (var bb in UnityEngine.Object.FindObjectsOfType<Billboard>())
            {
                if (bb != null)
                    Attach(bb.gameObject);
            }
        }

        // Create the shared persistent manager only when missing.
        // The manager owns indexes and cached clips, while the hook owns discovery.
        private void EnsureManager()
        {
            if (SpriteSoundManager.Instance == null)
            {
                var mgr = new GameObject("SpriteSoundManager");
                UnityEngine.Object.DontDestroyOnLoad(mgr);
                mgr.AddComponent<SpriteSoundManager>();
            }
        }

        // Resolve the sound key and create or reconfigure its emitter.
        // Same-key rediscovery returns without restarting a healthy sound. A different key
        // means the object has changed identity and needs full emitter reconfiguration.
        // Keys with neither indexed WAV nor JSON are skipped.
        // A suffixed WAV alone does not pass ShouldAttachEmitter for a base key.
        // Supply that base key's JSON for a variant-only musician library. A missing
        // emitter is a discovery/configuration issue before it is a synchronization issue.
        // ORIGINAL COMPARISON: The original called Reactivate on every same-key scan. That call could restart
        // a paused or not-yet-playing source. The current scanner returns for an existing
        // same-key emitter; its own sensor/lifecycle code decides whether playback is due.
        // Scan frequencies and transition subscriptions themselves are unchanged.
        public void Attach(GameObject go)
        {
            string key = SpriteSoundKeyResolver.ResolveKeyFromGameObject(go);
            if (string.IsNullOrEmpty(key))
                return;
            if (!SpriteSoundManager.Instance.ShouldAttachEmitter(key))
                return;
            var emitter = go.GetComponent<SpriteSoundEmitter>();
            if (emitter != null)
            {
                if (emitter.Key != key)
                    emitter.Initialize(key, true);
                return;
            }

            if (emitter == null)
            {
                emitter = go.AddComponent<SpriteSoundEmitter>();
                emitter.Initialize(key, true);
            }
        }
    }
}
