// 1.4.2: Daggerfall.Harmony owns loading Harmony. This class owns only SSF startup.
// The supported provider creates an active SubModule in Init and clears patches
// synchronously in SubModule.Start. IsReady is not set by that provider, so waiting
// on its IsReady would never finish. Instead, require its active component BEFORE
// yielding one frame. Unity runs Start before that component's first Update; the
// next coroutine continuation is after the active component's Start opportunity.
// This contract must be reviewed if the provider changes to asynchronous cleanup.
using System;
using System.Collections;
using DaggerfallWorkshop.Game.Utility.ModSupport;
using UnityEngine;

namespace SpriteSoundFramework
{
    public sealed class SpriteSoundHarmonyBootstrap : MonoBehaviour
    {
        public const string DependencyName = "daggerfall.harmony";
        private static SpriteSoundHarmonyBootstrap instance;
        private Mod owner;
        private bool ownsSession;
        public static bool IsRunning { get { return instance != null; } }

        public void Configure(Mod mod)
        {
            owner = mod;
            instance = this;
            ownsSession = true;
        }

        // Keep this class free of Harmony types. Missing dependencies should reach
        // an ordinary error message, not fail while the entry point is being JITed.
        public IEnumerator Start()
        {
            Mod dependency;
            string error;
            if (!TryGetDependency(out dependency, out error))
            {
                Fail(error);
                yield break;
            }

            Type providerType = dependency.GetCompiledType("Daggerfall.Harmony.SubModule");
            if (providerType == null || !typeof(MonoBehaviour).IsAssignableFrom(providerType))
            {
                Fail("The dependency has no supported SubModule component.");
                yield break;
            }

            // Seeing an assembly alone is not proof that its cleanup has run.
            // Disabled/delayed providers are rejected instead of guessed ready.
            MonoBehaviour provider = null;
            foreach (var component in UnityEngine.Object.FindObjectsOfType(providerType))
            {
                var candidate = component as MonoBehaviour;
                if (candidate != null && candidate.isActiveAndEnabled)
                {
                    if (provider != null)
                    {
                        Fail("Multiple active Harmony provider components were found.");
                        yield break;
                    }
                    provider = candidate;
                }
            }
            if (provider == null)
            {
                Fail("Harmony's startup component is missing or disabled. Load it before SSF.");
                yield break;
            }

            SpriteSoundLog.Info("[SpriteSound][Harmony] dependency=" + dependency.FileName +
                " modVersion=" + dependency.ModInfo.ModVersion + " stage=waiting-for-Start");
            yield return null;
            if (provider == null || !provider.isActiveAndEnabled || !dependency.Enabled)
            {
                Fail("Harmony became unavailable during startup.");
                yield break;
            }

            // The separately compiled patch helper is entered only after the
            // dependency checks. Catch assembly-resolution and patch failures here.
            bool installed = false;
            try { installed = SpriteSound_NPCFRPatch.Init(dependency); }
            catch (Exception ex) { SpriteSoundLog.Error("[SpriteSound][Harmony] " + ex); }
            if (!installed)
            {
                Fail("Compatibility hooks could not be installed. See the preceding error.");
                yield break;
            }

            // A second frame catches startup code which clears our hooks after
            // installation. Do not automatically fight another mod by repatching.
            yield return null;
            bool retained = false;
            try { retained = SpriteSound_NPCFRPatch.VerifyInstalled(); }
            catch (Exception ex) { SpriteSoundLog.Error("[SpriteSound][Harmony] " + ex); }
            if (!retained)
            {
                Fail("SSF hooks were removed during startup; discovery was not started.");
                yield break;
            }

            gameObject.AddComponent<SpriteSoundHook>();
            owner.IsReady = true;
            SpriteSoundLog.Info("[SpriteSound][Harmony] stage=ready hooks-retained=true; discovery started.");
        }

        private bool TryGetDependency(out Mod dependency, out string error)
        {
            dependency = null;
            error = null;
            if (owner == null || ModManager.Instance == null)
            {
                error = "DFU mod startup context is unavailable.";
                return false;
            }
            // GetMod(string) searches the display title. DFU dependencies instead
            // use FileName (without .dfmod); mirror that exact matching rule.
            foreach (var mod in ModManager.Instance.GetAllMods())
                if (mod.FileName == DependencyName) dependency = mod;
            if (dependency == null || !dependency.Enabled)
            {
                error = "Install and enable daggerfall.harmony.dfmod before Reverberated.";
                return false;
            }
            if (dependency.LoadPriority >= owner.LoadPriority)
            {
                error = "Place daggerfall.harmony above Reverberated in the load order.";
                return false;
            }
            Version version;
            if (!Version.TryParse(dependency.ModInfo.ModVersion, out version) ||
                version < new Version(2, 4, 2, 2))
            {
                error = "Use Daggerfall.Harmony 2.4.2.2 or later; its loaded library is checked separately.";
                return false;
            }
            return true;
        }

        private void Fail(string reason)
        {
            if (owner != null) owner.IsReady = false;
            // Errors remain visible in Unity's normal log with SSF logging off.
            SpriteSoundLog.Error("[SpriteSound][Harmony] startup failed: " + reason);
            try { SpriteSound_NPCFRPatch.Shutdown(); }
            catch (Exception ex) { SpriteSoundLog.Error("[SpriteSound][Harmony] cleanup: " + ex); }
        }

        private void OnDestroy()
        {
            if (!ownsSession) return;
            try { SpriteSound_NPCFRPatch.Shutdown(); }
            catch (Exception ex) { SpriteSoundLog.Error("[SpriteSound][Harmony] shutdown: " + ex); }
            if (owner != null) owner.IsReady = false;
            if (instance == this) instance = null;
            SpriteSoundLog.EndSession();
        }
    }
}
