// RETAINED COMPATIBILITY CODE: this Harmony/material-identity approach was
// already present in the supplied original source. This patch routes its messages through
// the optional logger; it does not redesign the reflection targets. A future mod
// or Harmony update may change those signatures. Treat each missing target as a
// compatibility question before changing the sound playback logic.
// Provenance: compared with the user-supplied original files; see SOURCE-CHANGES.md.

// Runtime Harmony integration for NPC Flat Replacer and vanilla billboard identity.
// Reflection avoids a direct compiled dependency on Harmony or NPC Flat Replacer.
// Compatibility depends on the target types and method signatures being available.

using UnityEngine;
using System.Reflection;
using System;
using System.IO;
using DaggerfallWorkshop;

namespace SpriteSoundFramework
{
    public static class SpriteSound_NPCFRPatch
    {
        // This retained dependency path is relative to StreamingAssets. It is separate
        // from Mod.DirPath, which is used for the optional SSF log directory. Moving logs
        // to the loaded mod's directory did not change where this code searches for Harmony.
        private const string HarmonyAssemblyPath = "Mods/0Harmony.dll";
        private const string HarmonyPatchId = "com.spritesound.npcfr_fix";
        private static object harmonyInstance;
        private static Type harmonyType;
        // Load Harmony from Mods/0Harmony.dll and patch available material setters.
        // NPC replacement materials carry useful names that can be lost later. The postfix
        // saves that name on the billboard. The vanilla postfix removes the custom identity
        // when a sprite returns to vanilla material assignment. Failures are reported.
        public static void Init()
        {
            try
            {
                string fullPath = Path.Combine(Application.streamingAssetsPath, HarmonyAssemblyPath);
                if (!File.Exists(fullPath))
                {
                    SpriteSoundLog.Error($"[SpriteSound] Could not find Harmony DLL at {fullPath}");
                    return;
                }

                Assembly harmonyAssembly = Assembly.LoadFrom(fullPath);
                harmonyType = harmonyAssembly.GetType("HarmonyLib.Harmony");
                if (harmonyType == null)
                {
                    SpriteSoundLog.Error("[SpriteSound] Harmony type not found.");
                    return;
                }

                ConstructorInfo ctor = harmonyType.GetConstructor(new[]{typeof(string)});
                harmonyInstance = ctor.Invoke(new object[]{HarmonyPatchId});
                MethodInfo patchMethod = GetHarmonyPatchMethod();
                if (patchMethod == null)
                {
                    SpriteSoundLog.Error("[SpriteSound] Patch method not found.");
                    return;
                }

                Type bbType = FindType("FlatReplacer.ReplacementBillboard");
                if (bbType != null)
                {
                    MethodInfo targetMethod = bbType.GetMethod("SetMaterial", new Type[]{typeof(string), typeof(Vector2), typeof(Texture2D[]), typeof(int), typeof(int), typeof(bool)});
                    MethodInfo postfix = typeof(SpriteSound_NPCFRPatch).GetMethod(nameof(Postfix_SetMaterial_NPCFR), BindingFlags.Static | BindingFlags.Public);
                    if (targetMethod != null && postfix != null)
                    {
                        PatchTarget(patchMethod, harmonyAssembly, targetMethod, postfix);
                    }
                }

                MethodInfo vanillaMethod = typeof(DaggerfallBillboard).GetMethod("SetMaterial", new Type[]{typeof(int), typeof(int), typeof(int)});
                MethodInfo vanillaPostfix = typeof(SpriteSound_NPCFRPatch).GetMethod(nameof(Postfix_SetMaterial_Vanilla), BindingFlags.Static | BindingFlags.Public);
                if (vanillaMethod != null && vanillaPostfix != null)
                {
                    PatchTarget(patchMethod, harmonyAssembly, vanillaMethod, vanillaPostfix);
                }

                // This historical notice is not proof that every optional target was
                // found: missing types/signatures above are skipped without a count.
                SpriteSoundLog.Info("[SpriteSound] Patches applied manually (NPCFR + Vanilla cleanup).");
            }
            catch (Exception e)
            {
                SpriteSoundLog.Error($"[SpriteSound] Error applying patches: {e}");
            }
        }

        // Locate an instance Patch method through reflection.
        // The existing discovery rule uses method name and the first parameter type.
        // It is a compatibility heuristic, not proof of support for all Harmony versions.
        static MethodInfo GetHarmonyPatchMethod()
        {
            var patchMethods = harmonyType.GetMethods(BindingFlags.Instance | BindingFlags.Public | BindingFlags.NonPublic);
            foreach (var method in patchMethods)
            {
                if (method.Name != "Patch")
                    continue;
                var parameters = method.GetParameters();
                if (parameters.Length >= 3 && parameters[0].ParameterType == typeof(MethodBase))
                {
                    return method;
                }
            }

            return null;
        }

        // Build a HarmonyMethod and invoke the supported patch argument shape.
        // The existing implementation branches between five arguments and four arguments.
        // Keep this behavior unchanged unless testing a different Harmony API explicitly.
        static void PatchTarget(MethodInfo patchMethod, Assembly harmonyAss, MethodInfo original, MethodInfo postfix)
        {
            Type harmonyMethodType = harmonyAss.GetType("HarmonyLib.HarmonyMethod");
            ConstructorInfo harmonyMethodCtor = harmonyMethodType.GetConstructor(new[]{typeof(MethodInfo)});
            object postfixObj = harmonyMethodCtor.Invoke(new object[]{postfix});
            int paramCount = patchMethod.GetParameters().Length;
            if (paramCount == 5)
                patchMethod.Invoke(harmonyInstance, new object[]{original, null, postfixObj, null, null});
            else
                patchMethod.Invoke(harmonyInstance, new object[]{original, null, postfixObj, null});
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
