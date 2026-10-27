// 1.4.2: replace the loose-DLL loader and reflective Harmony calls with the
// library supplied by daggerfall.harmony. Billboard identity postfixes below
// retain 1.4.1 behavior; changing who loads Harmony must not change sound keys.
using UnityEngine;
using System.Reflection;
using System.Runtime.CompilerServices;
using System.Collections.Generic;
using System.Linq;
using System;
using DaggerfallWorkshop;
using DaggerfallWorkshop.Game.Utility.ModSupport;
using HarmonyLib;

namespace SpriteSoundFramework
{
    public static class SpriteSound_NPCFRPatch
    {
        private const string HarmonyPatchId = "com.spritesound.npcfr_fix";
        private static Harmony harmonyInstance;
        private static readonly List<MethodInfo> installedTargets = new List<MethodInfo>();

        // NoInlining keeps Harmony binding out of the dependency-free bootstrap's
        // early checks. No file lookup, Assembly.LoadFrom, or loose-DLL fallback is
        // allowed here: that would silently restore the old conflicting setup.
        [MethodImpl(MethodImplOptions.NoInlining)]
        public static bool Init(Mod dependency)
        {
            if (harmonyInstance != null) return VerifyInstalled();
            try
            {
                Assembly library = typeof(Harmony).Assembly;
                Type providerRef = dependency.GetCompiledType("Daggerfall.Harmony.HarmonyRef");
                var libraries = AppDomain.CurrentDomain.GetAssemblies()
                    .Where(a => a.GetName().Name == "0Harmony").ToArray();
                if (providerRef == null || providerRef.BaseType != typeof(Harmony) ||
                    libraries.Length != 1 || libraries[0] != library ||
                    library.GetName().Version != new Version(2, 4, 2, 0))
                {
                    SpriteSoundLog.Error("[SpriteSound][Harmony] Unsupported or conflicting library. " +
                        "Expected one 0Harmony 2.4.2.0 shared with Daggerfall.Harmony; found " +
                        string.Join("; ", libraries.Select(a => a.FullName).ToArray()));
                    return false;
                }
                SpriteSoundLog.Info("[SpriteSound][Harmony] library=" + library.FullName);
                harmonyInstance = new Harmony(HarmonyPatchId);

                // Vanilla cleanup is essential: it prevents a pooled billboard
                // retaining a replacement sprite's key after becoming vanilla.
                MethodInfo vanilla = typeof(DaggerfallBillboard).GetMethod("SetMaterial",
                    new Type[] { typeof(int), typeof(int), typeof(int) });
                if (vanilla == null)
                    throw new MissingMethodException("DaggerfallBillboard.SetMaterial(int,int,int)");
                PatchTarget(vanilla, nameof(Postfix_SetMaterial_Vanilla));

                // NPCFR remains optional. Absence is expected in a vanilla setup;
                // an installed but incompatible signature is a separate warning.
                Type replacement = FindType("FlatReplacer.ReplacementBillboard");
                if (replacement == null)
                    SpriteSoundLog.Info("[SpriteSound][Harmony] NPCFR=absent (optional)");
                else
                {
                    MethodInfo target = replacement.GetMethod("SetMaterial", new Type[] {
                        typeof(string), typeof(Vector2), typeof(Texture2D[]),
                        typeof(int), typeof(int), typeof(bool) });
                    if (target == null)
                        SpriteSoundLog.Warning("[SpriteSound][Harmony] NPCFR=unsupported-signature; replacement identity hook unavailable.");
                    else
                        PatchTarget(target, nameof(Postfix_SetMaterial_NPCFR));
                }
                return VerifyInstalled();
            }
            catch (Exception ex)
            {
                SpriteSoundLog.Error("[SpriteSound][Harmony] Installation failed: " + ex);
                Shutdown();
                return false;
            }
        }

        private static void PatchTarget(MethodInfo target, string postfixName)
        {
            MethodInfo postfix = typeof(SpriteSound_NPCFRPatch).GetMethod(postfixName);
            harmonyInstance.Patch(target, postfix: new HarmonyMethod(postfix));
            installedTargets.Add(target);
            SpriteSoundLog.Info("[SpriteSound][Harmony] patched=" + target.DeclaringType.FullName + "." + target.Name);
        }

        // Check ownership AND the expected postfix, not merely whether someone
        // patched this method. Called once after installation and again one frame
        // later. This is not a promise that another mod cannot remove it afterward.
        [MethodImpl(MethodImplOptions.NoInlining)]
        public static bool VerifyInstalled()
        {
            if (harmonyInstance == null || installedTargets.Count == 0) return false;
            foreach (MethodInfo target in installedTargets)
            {
                var info = Harmony.GetPatchInfo(target);
                if (info == null || !info.Postfixes.Any(p => p.owner == HarmonyPatchId &&
                    p.PatchMethod.DeclaringType == typeof(SpriteSound_NPCFRPatch)))
                    return false;
            }
            return true;
        }

        // Never call a global UnpatchAll. Rollback/shutdown removes only our owner
        // ID, leaving every other mod's hooks intact. Also supports fresh Editor runs.
        [MethodImpl(MethodImplOptions.NoInlining)]
        public static void Shutdown()
        {
            if (harmonyInstance != null) harmonyInstance.UnpatchAll(HarmonyPatchId);
            installedTargets.Clear();
            harmonyInstance = null;
        }

        // Search loaded assemblies for a named runtime type.
        // The optional mod may live in a different assembly. A missing type simply prevents
        // that target from being patched during this initialization pass.
        static Type FindType(string fullName)
        {
            Type t = Type.GetType(fullName);
            if (t != null)
                return t;
            foreach (var asm in AppDomain.CurrentDomain.GetAssemblies())
            {
                t = asm.GetType(fullName);
                if (t != null)
                    return t;
            }

            return null;
        }

        // Save the replacement texture prefix as a persistent sound identity.
        // Reuse the existing identity component if present; update it for the new material.
        public static void Postfix_SetMaterial_NPCFR(MonoBehaviour __instance, string namePrefix)
        {
            if (__instance == null || string.IsNullOrEmpty(namePrefix))
                return;
            var identity = __instance.gameObject.GetComponent<SpriteSoundIdentity>();
            if (identity == null)
                identity = __instance.gameObject.AddComponent<SpriteSoundIdentity>();
            identity.SoundKey = namePrefix;
        }

        // Schedule removal of a stale custom identity when vanilla material is assigned.
        // Unity Destroy is deferred until later in the frame; this is not an immediate
        // component removal. The tested cleanup behavior is preserved.
        public static void Postfix_SetMaterial_Vanilla(MonoBehaviour __instance)
        {
            if (__instance == null)
                return;
            var identity = __instance.gameObject.GetComponent<SpriteSoundIdentity>();
            if (identity != null)
            {
                UnityEngine.Object.Destroy(identity);
            }
        }
    }
}

