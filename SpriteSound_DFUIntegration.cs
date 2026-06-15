using UnityEngine;
using DaggerfallWorkshop;
using DaggerfallWorkshop.Game;
using DaggerfallWorkshop.Game.Utility.ModSupport;
using DaggerfallWorkshop.Game.Serialization; 
using System.Collections;
using System.Reflection;
using System; 

namespace SpriteSoundFramework
{
    public static class SpriteSound_DFUIntegration
    {
        private static Mod mod;

        [Invoke(StateManager.StateTypes.Start, 0)]
        public static void Init(InitParams initParams)
        {
            mod = initParams.Mod;
            
            SpriteSound_NPCFRPatch.Init();

            var go = new GameObject("SpriteSoundFramework_Hook");
            UnityEngine.Object.DontDestroyOnLoad(go);
            go.AddComponent<SpriteSoundHook>();
            
            Debug.Log("[SpriteSound] DFU integration init (Hybrid Scan Mode)");
        }
    }

    public class SpriteSoundHook : MonoBehaviour
    {
        public static SpriteSoundHook Instance { get; private set; }
        private float scanTimer = 0f;
        
        // SETTING: How often (in seconds) to scan for new blocks in Dungeons/Exteriors
        // 5.0f is a good balance between responsiveness and performance.
        private const float ScanInterval = 5.0f; 

        private void Awake()
        {
            Instance = this;
            UnityEngine.Object.DontDestroyOnLoad(gameObject);
            StartCoroutine("RapidScanRoutine");
        }

        private void OnEnable()
        {
            EnsureManager();
            PlayerEnterExit.OnTransitionInterior += OnTransitionInterior;
            PlayerEnterExit.OnTransitionDungeonInterior += OnTransitionDungeonInterior;
            PlayerEnterExit.OnTransitionExterior += OnTransitionExterior;
            SaveLoadManager.OnLoad += OnSaveGameLoaded;
        }

        private void OnDisable()
        {
            PlayerEnterExit.OnTransitionInterior -= OnTransitionInterior;
            PlayerEnterExit.OnTransitionDungeonInterior -= OnTransitionDungeonInterior;
            PlayerEnterExit.OnTransitionExterior -= OnTransitionExterior;
            SaveLoadManager.OnLoad -= OnSaveGameLoaded;
        }

        private void OnSaveGameLoaded(SaveData_v1 saveData) => RestartRapidScan();
        private void OnTransitionInterior(PlayerEnterExit.TransitionEventArgs args) => RestartRapidScan();
        private void OnTransitionDungeonInterior(PlayerEnterExit.TransitionEventArgs args) => RestartRapidScan();
        private void OnTransitionExterior(PlayerEnterExit.TransitionEventArgs args) => RestartRapidScan();

        private void RestartRapidScan()
        {
            SpriteSoundManager.Instance?.ResetMusicianTrack();
            StopCoroutine("RapidScanRoutine");
            StartCoroutine("RapidScanRoutine");
        }

        // RAPID SCAN: Checks frequently for the first 8 seconds to catch initial load
        private IEnumerator RapidScanRoutine()
        {
            for (int i = 0; i < 16; i++) 
            {
                yield return new WaitForSeconds(0.5f);
                ScanAllBillboards();
            }
        }

        private void Update()
        {
            // "Small Building" = A house, shop, or tavern (Everything loads at once).
            bool isSmallBuilding = GameManager.Instance.IsPlayerInside 
                                   && !GameManager.Instance.IsPlayerInsideDungeon 
                                   && !GameManager.Instance.IsPlayerInsideCastle;

            // If we are NOT in a small building (i.e. we are in Exterior, Dungeon, or Castle),
            // keep scanning periodically to catch new blocks loading in.
            if (!isSmallBuilding)
            {
                scanTimer += Time.deltaTime;
                if (scanTimer >= ScanInterval) {
                    scanTimer = 0f;
                    ScanAllBillboards();
                }
            }
        }

        private void ScanAllBillboards()
        {
            if (SpriteSoundManager.Instance == null) EnsureManager();

            foreach (var bb in UnityEngine.Object.FindObjectsOfType<Billboard>())
            {
                if (bb != null) Attach(bb.gameObject);
            }
        }

        private void EnsureManager()
        {
            if (SpriteSoundManager.Instance == null)
            {
                var mgr = new GameObject("SpriteSoundManager");
                UnityEngine.Object.DontDestroyOnLoad(mgr);
                mgr.AddComponent<SpriteSoundManager>();
            }
        }

        public void Attach(GameObject go)
        {
            string key = SpriteSoundKeyResolver.ResolveKeyFromGameObject(go);
            if (string.IsNullOrEmpty(key)) return;

            if (!SpriteSoundManager.Instance.ShouldAttachEmitter(key)) return; 

            var emitter = go.GetComponent<SpriteSoundEmitter>();
            
            // Handle Pooled Objects / State Refresh
            if (emitter != null)
            {
                if (emitter.Key != key) 
                {
                    emitter.Initialize(key, true);
                }
                else 
                {
                    emitter.Reactivate();
                }
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