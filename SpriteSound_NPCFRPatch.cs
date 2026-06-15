using UnityEngine;
using System.Reflection;
using System;
using System.IO;
using DaggerfallWorkshop; // Needed for DaggerfallBillboard

namespace SpriteSoundFramework
{
    public static class SpriteSound_NPCFRPatch
    {
        // Configuration for Manual Loading
        private const string HarmonyAssemblyPath = "Mods/0Harmony.dll";
        private const string HarmonyPatchId = "com.spritesound.npcfr_fix";

        private static object harmonyInstance;
        private static Type harmonyType;

        public static void Init()
        {
            try
            {
                // 1. Load Harmony DLL manually
                string fullPath = Path.Combine(Application.streamingAssetsPath, HarmonyAssemblyPath);
                if (!File.Exists(fullPath))
                {
                    Debug.LogError($"[SpriteSound] Could not find Harmony DLL at {fullPath}");
                    return;
                }

                Assembly harmonyAssembly = Assembly.LoadFrom(fullPath);
                harmonyType = harmonyAssembly.GetType("HarmonyLib.Harmony");
                if (harmonyType == null) { Debug.LogError("[SpriteSound] Harmony type not found."); return; }

                // 2. Instantiate Harmony
                ConstructorInfo ctor = harmonyType.GetConstructor(new[] { typeof(string) });
                harmonyInstance = ctor.Invoke(new object[] { HarmonyPatchId });

                // 3. Find the Patch Method
                MethodInfo patchMethod = GetHarmonyPatchMethod();
                if (patchMethod == null) { Debug.LogError("[SpriteSound] Patch method not found."); return; }

                // --- PATCH 1: NPC FLAT REPLACER (Adds Identity) ---
                // We use a helper to find the type safely since it might not be loaded yet or is in another assembly
                Type bbType = FindType("FlatReplacer.ReplacementBillboard");
                if (bbType != null)
                {
                    MethodInfo targetMethod = bbType.GetMethod("SetMaterial", 
                        new Type[] { typeof(string), typeof(Vector2), typeof(Texture2D[]), typeof(int), typeof(int), typeof(bool) });

                    MethodInfo postfix = typeof(SpriteSound_NPCFRPatch).GetMethod(nameof(Postfix_SetMaterial_NPCFR), BindingFlags.Static | BindingFlags.Public);

                    if (targetMethod != null && postfix != null)
                    {
                        PatchTarget(patchMethod, harmonyAssembly, targetMethod, postfix);
                    }
                }

                // --- PATCH 2: VANILLA BILLBOARD (Removes Identity) ---
                MethodInfo vanillaMethod = typeof(DaggerfallBillboard).GetMethod("SetMaterial",
                    new Type[] { typeof(int), typeof(int), typeof(int) });

                MethodInfo vanillaPostfix = typeof(SpriteSound_NPCFRPatch).GetMethod(nameof(Postfix_SetMaterial_Vanilla), BindingFlags.Static | BindingFlags.Public);

                if (vanillaMethod != null && vanillaPostfix != null)
                {
                    PatchTarget(patchMethod, harmonyAssembly, vanillaMethod, vanillaPostfix);
                }
                
                Debug.Log("[SpriteSound] Patches applied manually (NPCFR + Vanilla cleanup).");
            }
            catch (Exception e)
            {
                Debug.LogError($"[SpriteSound] Error applying patches: {e}");
            }
        }

        // --- HARMONY REFLECTION HELPERS ---

        static MethodInfo GetHarmonyPatchMethod()
        {
            var patchMethods = harmonyType.GetMethods(BindingFlags.Instance | BindingFlags.Public | BindingFlags.NonPublic);
            foreach (var method in patchMethods)
            {
                if (method.Name != "Patch") continue;
                var parameters = method.GetParameters();
                // Check signature: Patch(MethodBase original, HarmonyMethod prefix, HarmonyMethod postfix, ...)
                if (parameters.Length >= 3 && parameters[0].ParameterType == typeof(MethodBase))
                {
                    return method;
                }
            }
            return null;
        }

        static void PatchTarget(MethodInfo patchMethod, Assembly harmonyAss, MethodInfo original, MethodInfo postfix)
        {
            Type harmonyMethodType = harmonyAss.GetType("HarmonyLib.HarmonyMethod");
            ConstructorInfo harmonyMethodCtor = harmonyMethodType.GetConstructor(new[] { typeof(MethodInfo) });
            object postfixObj = harmonyMethodCtor.Invoke(new object[] { postfix });

            // Determine if Patch() takes 4 or 5 arguments (older vs newer Harmony versions)
            int paramCount = patchMethod.GetParameters().Length;
            if (paramCount == 5)
                patchMethod.Invoke(harmonyInstance, new object[] { original, null, postfixObj, null, null });
            else
                patchMethod.Invoke(harmonyInstance, new object[] { original, null, postfixObj, null });
        }

        static Type FindType(string fullName)
        {
            Type t = Type.GetType(fullName);
            if (t != null) return t;

            foreach (var asm in AppDomain.CurrentDomain.GetAssemblies())
            {
                t = asm.GetType(fullName);
                if (t != null) return t;
            }
            return null;
        }

        // --- POSTFIX LOGIC (Kept exactly as before) ---

        // NPCFR: ADD TAG
        public static void Postfix_SetMaterial_NPCFR(MonoBehaviour __instance, string namePrefix)
        {
            if (__instance == null || string.IsNullOrEmpty(namePrefix)) return;
            var identity = __instance.gameObject.GetComponent<SpriteSoundIdentity>();
            if (identity == null) identity = __instance.gameObject.AddComponent<SpriteSoundIdentity>();
            identity.SoundKey = namePrefix;
        }

        // VANILLA: REMOVE TAG (Cleanup)
        public static void Postfix_SetMaterial_Vanilla(MonoBehaviour __instance)
        {
            if (__instance == null) return;
            var identity = __instance.gameObject.GetComponent<SpriteSoundIdentity>();
            if (identity != null)
            {
                // We are reverting to vanilla, so destroy the custom tag to prevent stale IDs
                UnityEngine.Object.Destroy(identity);
            }
        }
    }
}