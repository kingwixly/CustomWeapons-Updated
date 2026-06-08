using System.Collections.Generic;
using BepInEx.Logging;
using HarmonyLib;
using UnityEngine;

namespace CustomWeapons.Patches
{
    internal static class AutoSpawnLog
    {
        public static readonly ManualLogSource Log =
            BepInEx.Logging.Logger.CreateLogSource("CustomWeapons.AutoSpawn");
    }

    [HarmonyPatch(typeof(MissionManager), "OnStartServer")]
    public static class OrbitalStrikeAutoSpawn_MissionStart
    {
        [HarmonyPostfix]
        public static void Postfix()
        {
            AutoSpawnCore.RunOnce();
        }
    }

    [HarmonyPatch(typeof(AircraftSelectionMenu), "Initialize")]
    public static class OrbitalStrikeAutoSpawn_MenuInit
    {
        [HarmonyPostfix]
        public static void Postfix(NuclearOption.Networking.Player localPlayer, Airbase airbase)
        {
            AutoSpawnCore.RunOnce();
        }
    }

    internal static class AutoSpawnCore
    {
        private static GameObject _odxPlatformPrefab;
        private static GameObject _odxUplinkPrefab;
        private static UnitDefinition _odxPlatformDef;
        private static UnitDefinition _odxUplinkDef;
        private static readonly HashSet<string> _spawnedForFaction = new HashSet<string>();
        private static bool _rankFixApplied;

        public static void RunOnce()
        {
            try { ApplyRankFix(); } catch (System.Exception e) { AutoSpawnLog.Log.LogError("ApplyRankFix: " + e); }
            try { AutoSpawnInfrastructure(); } catch (System.Exception e) { AutoSpawnLog.Log.LogError("AutoSpawnInfrastructure: " + e); }
        }

        private static void ApplyRankFix()
        {
            if (_rankFixApplied) return;
            int hits = 0;
            var all = Resources.FindObjectsOfTypeAll<AircraftParameters>();
            foreach (var p in all)
            {
                if (p == null) continue;
                if (p.name != "CAS1Parameters_Naval") continue;
                if (p.rankRequired != 0)
                {
                    p.rankRequired = 0;
                    hits++;
                }
            }
            if (hits > 0 || all.Length > 0) _rankFixApplied = true;
        }

        private static void AutoSpawnInfrastructure()
        {
            EnsurePrefabsCached();
            if (_odxPlatformPrefab == null || _odxUplinkPrefab == null)
            {
                AutoSpawnLog.Log.LogWarning("AutoSpawn: prefabs missing, aborting.");
                return;
            }

            var spawner = NetworkSceneSingleton<Spawner>.i;
            if (spawner == null)
            {
                AutoSpawnLog.Log.LogWarning("AutoSpawn: Spawner not ready, aborting.");
                return;
            }

            int served = 0;
            int hqsSeen = 0;
            foreach (var hq in FactionRegistry.GetAllHQs())
            {
                hqsSeen++;
                if (hq == null || hq.faction == null) continue;
                var name = hq.faction.factionName;
                if (string.IsNullOrEmpty(name)) continue;
                if (_spawnedForFaction.Contains(name))
                {
                    continue;
                }

                var basePos = hq.transform.position;
                var uplinkPos = new GlobalPosition { x = basePos.x + 50000f, y = basePos.y, z = basePos.z + 50000f };


                var u = spawner.SpawnBuilding(_odxUplinkPrefab, uplinkPos, Quaternion.identity, hq, null,
                                              "AutoSpawn_ODXUPLINK_" + name, false, null);

                _spawnedForFaction.Add(name);
                served++;
            }
        }

        private static void EnsurePrefabsCached()
        {
            if (_odxPlatformPrefab != null && _odxUplinkPrefab != null) return;
            var allDefs = Resources.FindObjectsOfTypeAll<UnitDefinition>();
            foreach (var def in allDefs)
            {
                if (def == null) continue;
                if (def.jsonKey == "ODXPLATFORM") { _odxPlatformDef = def; _odxPlatformPrefab = def.unitPrefab; }
                else if (def.jsonKey == "ODXUPLINK") { _odxUplinkDef = def; _odxUplinkPrefab = def.unitPrefab; }
                if (_odxPlatformPrefab != null && _odxUplinkPrefab != null) break;
            }

            // Wire prefab Building.definition = the corresponding UnitDefinition.
            // Spawner.SpawnBuilding does `this.definition.unitName` with no null check,
            // and freshly-Instantiated copies of these Blueprinter prefabs have
            // definition=null. Setting it on the prefab propagates to every Instantiate.
            WireBuildingDefinition(_odxPlatformPrefab, _odxPlatformDef, "ODXPLATFORM");
            WireBuildingDefinition(_odxUplinkPrefab, _odxUplinkDef, "ODXUPLINK");
        }

        private static void WireBuildingDefinition(GameObject prefab, UnitDefinition def, string label)
        {
            if (prefab == null || def == null) return;
            var unit = prefab.GetComponent<Unit>();
            if (unit == null)
            {
                AutoSpawnLog.Log.LogWarning("WireBuildingDefinition: " + label + " prefab has no Unit component.");
                return;
            }
            if (unit.definition == null)
            {
                unit.definition = def;
            }
            else
            {
            }
        }
    }

    // ===========================================================================
    // ORBITAL STRIKE BYPASS — direct projectile spawn when no platform is registered
    //
    // The Blueprinter-shipped ODXPLATFORM prefab is broken on Nuclear Option 0.33+ —
    // its CustomWeapons.Satellite / OrbitalStrikePlatform MonoBehaviours don't resolve
    // to live types at runtime, so the platform component never registers with the
    // controller and ammo stays at 0. This bypass only kicks in when the original
    // controller methods report "no platforms" — it postfix-overrides the result so
    // the player can still use the weapon. If Blueprinter eventually fixes script
    // resolution (e.g. 1.8.19+) the platforms will register normally and the bypass
    // becomes inactive automatically.
    // ===========================================================================

    [HarmonyPatch(typeof(OrbitalStrikeController), "GetAmmo")]
    public static class OrbitalStrikeBypass_GetAmmo
    {
        [HarmonyPostfix]
        public static void Postfix(ref int __result, Unit caller)
        {
            if (__result > 0) return;
            if (caller == null || caller.NetworkHQ == null || caller.NetworkHQ.faction == null) return;
            __result = OrbitalStrikeBypass.GetFallbackAmmo(caller.NetworkHQ.faction.factionName);
        }
    }

    [HarmonyPatch(typeof(OrbitalStrikeController), "IsReady")]
    public static class OrbitalStrikeBypass_IsReady
    {
        [HarmonyPostfix]
        public static void Postfix(ref bool __result, Unit caller)
        {
            if (__result) return;
            if (caller == null || caller.NetworkHQ == null || caller.NetworkHQ.faction == null) return;
            __result = OrbitalStrikeBypass.GetFallbackAmmo(caller.NetworkHQ.faction.factionName) > 0;
        }
    }

    [HarmonyPatch(typeof(OrbitalStrikeController), "TryFire")]
    public static class OrbitalStrikeBypass_TryFire
    {
        [HarmonyPostfix]
        public static void Postfix(ref bool __result, Unit target, Unit caller)
        {
            if (__result) return;
            if (target == null || caller == null || caller.NetworkHQ == null || caller.NetworkHQ.faction == null) return;
            try
            {
                if (OrbitalStrikeBypass.DirectStrike(target, caller))
                {
                    OrbitalStrikeBypass.RecordFire(caller.NetworkHQ.faction.factionName);
                    __result = true;
                }
            }
            catch (System.Exception e)
            {
                AutoSpawnLog.Log.LogError("Bypass TryFire: " + e);
            }
        }
    }

    internal static class OrbitalStrikeBypass
    {
        // Per-faction cooldown timer. After a fire, ammo drops to 0 and ramps back to
        // FullAmmo over CooldownSeconds. This gives the weapon HUD a real value to
        // animate (otherwise the ammo number sticks and there's no fire feedback).
        public const int FullAmmo = 5;
        public const float CooldownSeconds = 30f;
        private static readonly System.Collections.Generic.Dictionary<string, float> _lastFireTime
            = new System.Collections.Generic.Dictionary<string, float>();

        public static int GetFallbackAmmo(string team)
        {
            if (string.IsNullOrEmpty(team)) return FullAmmo;
            if (!_lastFireTime.TryGetValue(team, out var last)) return FullAmmo;
            var elapsed = Time.time - last;
            if (elapsed >= CooldownSeconds) return FullAmmo;
            // Ramp 0 -> FullAmmo over the cooldown window.
            var fraction = elapsed / CooldownSeconds;
            return (int)(FullAmmo * fraction);
        }

        public static void RecordFire(string team)
        {
            if (string.IsNullOrEmpty(team)) return;
            _lastFireTime[team] = Time.time;
        }

        private static MissileDefinition _projDef;

        public static bool DirectStrike(Unit target, Unit caller)
        {
            if (_projDef == null) CacheProjectile();
            if (_projDef == null)
            {
                AutoSpawnLog.Log.LogWarning("Bypass: ODXPROJ MissileDefinition not found.");
                return false;
            }
            var spawner = NetworkSceneSingleton<Spawner>.i;
            if (spawner == null)
            {
                AutoSpawnLog.Log.LogWarning("Bypass: Spawner singleton not ready.");
                return false;
            }

            var tgtPos = target.transform.position;
            var spawnPos = new Vector3(tgtPos.x, tgtPos.y + 10000f, tgtPos.z);
            var rotation = Quaternion.LookRotation(new Vector3(0f, -1f, 0f));
            var velocity = new Vector3(0f, -2000f, 0f);

            var spawned = spawner.SpawnMissile(_projDef, spawnPos, rotation, velocity, target, caller);
            return spawned != null;
        }

        private static void CacheProjectile()
        {
            var defs = Resources.FindObjectsOfTypeAll<MissileDefinition>();
            foreach (var d in defs)
            {
                if (d != null && d.jsonKey == "ODXPROJ") { _projDef = d; return; }
            }
        }
    }

    // ===========================================================================
    // A-19N AVAILABILITY — make CAS1_Naval spawnable from every hangar that
    // already accepts A-19 (CAS1), FS-20 Vortex (SmallFighter1), or KR-67 Ifrit
    // (Multirole1). The third covers the Hyperion-class carrier which only
    // accepts the Ifrit out-of-the-box.
    //
    // We can't just postfix Hangar.GetAvailableAircraft() because the spawn check
    // (Hangar.CanSpawnAircraft) reads the private `availableAircraft` field
    // DIRECTLY rather than going through GetAvailableAircraft. So we lazily
    // mutate the field itself on first call — once that's done, both the menu
    // and the spawn check naturally see the appended CAS1_Naval.
    // ===========================================================================
    [HarmonyPatch(typeof(Hangar), "GetAvailableAircraft")]
    public static class A19N_AllHangars
    {
        private static readonly System.Reflection.FieldInfo _availableField = typeof(Hangar)
            .GetField("availableAircraft", System.Reflection.BindingFlags.NonPublic | System.Reflection.BindingFlags.Instance);
        private static readonly HashSet<Hangar> _mutated = new HashSet<Hangar>();

        private static AircraftDefinition _a19nDef;
        private static AircraftDefinition _a19BaseDef;
        private static AircraftDefinition _fs20Def;
        private static AircraftDefinition _ifritDef;
        private static bool _searched;

        [HarmonyPrefix]
        public static void Prefix(Hangar __instance)
        {
            if (_mutated.Contains(__instance)) return;
            if (_availableField == null) return;

            if (!_searched)
            {
                _searched = true;
                var defs = Resources.FindObjectsOfTypeAll<AircraftDefinition>();
                foreach (var d in defs)
                {
                    if (d == null) continue;
                    if (d.jsonKey == "CAS1_Naval") _a19nDef = d;
                    else if (d.jsonKey == "CAS1") _a19BaseDef = d;
                    else if (d.jsonKey == "SmallFighter1") _fs20Def = d;
                    else if (d.jsonKey == "Multirole1") _ifritDef = d;
                }
            }
            if (_a19nDef == null) return;
            if (_a19BaseDef == null && _fs20Def == null && _ifritDef == null) return;

            var arr = (AircraftDefinition[])_availableField.GetValue(__instance);
            if (arr == null) { _mutated.Add(__instance); return; }

            bool triggerMatched = false;
            for (int i = 0; i < arr.Length; i++)
            {
                if (object.ReferenceEquals(arr[i], _a19nDef)) { _mutated.Add(__instance); return; }
                if (object.ReferenceEquals(arr[i], _a19BaseDef)) triggerMatched = true;
                else if (object.ReferenceEquals(arr[i], _fs20Def)) triggerMatched = true;
                else if (object.ReferenceEquals(arr[i], _ifritDef)) triggerMatched = true;
            }
            _mutated.Add(__instance);
            if (!triggerMatched) return;

            var newArr = new AircraftDefinition[arr.Length + 1];
            System.Array.Copy(arr, newArr, arr.Length);
            newArr[arr.Length] = _a19nDef;
            _availableField.SetValue(__instance, newArr);
        }
    }




    // A-19N SPAWN HOLD + AUDIO START — pins currentRPM=0 for ~3 seconds after
    // spawn (prevents spawn-jettison from hangar-door animation), and on first
    // entry per aircraft, calls Play() on our converted Turbofan.turbineAudio
    // and JetNozzle.thrustAudio sources. This postfix only fires for actually-
    // flying aircraft, so menu previews stay silent.
    // ===========================================================================
    [HarmonyPatch(typeof(Aircraft), "LocalSimFixedUpdate")]
    public static class A19N_ForceThrust
    {
        private static readonly System.Reflection.BindingFlags _flags =
            System.Reflection.BindingFlags.NonPublic | System.Reflection.BindingFlags.Public
            | System.Reflection.BindingFlags.Instance;

        private static readonly Dictionary<int, float> _firstSeen = new Dictionary<int, float>();
        private static readonly HashSet<int> _audioStarted = new HashSet<int>();
        public const float SpawnHoldSeconds = 3f;

        [HarmonyPostfix]
        public static void Postfix(Aircraft __instance)
        {
            try
            {
                if (__instance == null || __instance.definition == null) return;
                if (__instance.definition.jsonKey != "CAS1_Naval") return;

                int id = __instance.GetInstanceID();
                if (!_firstSeen.ContainsKey(id)) _firstSeen[id] = Time.time;
                bool inSpawnHold = (Time.time - _firstSeen[id]) < SpawnHoldSeconds;

                var allTfs = __instance.GetComponentsInChildren<Turbofan>(true);
                if (allTfs == null) return;

                if (inSpawnHold)
                {
                    // Pin currentRPM=0 during spawn-hold to prevent forward jettison.
                    foreach (var tf in allTfs)
                    {
                        if (tf == null) continue;
                        if (!A19N_TurbofanConversion.AllConvertedTurbofans.Contains(tf)) continue;
                        var rpmField = typeof(Turbofan).GetField("currentRPM", _flags);
                        if (rpmField != null) rpmField.SetValue(tf, 0f);
                    }
                    return;
                }

                // Past spawn hold — start audio once per aircraft if not already.
                if (_audioStarted.Contains(id)) return;
                _audioStarted.Add(id);
                StartAudioForAircraft(__instance, allTfs);
            }
            catch { }
        }

        private static void StartAudioForAircraft(Aircraft aircraft, Turbofan[] allTfs)
        {
            var tfType = typeof(Turbofan);
            var jnType = typeof(JetNozzle);

            int turbineStarted = 0, thrustStarted = 0;

            foreach (var tf in allTfs)
            {
                if (tf == null) continue;
                if (!A19N_TurbofanConversion.AllConvertedTurbofans.Contains(tf)) continue;

                // turbineAudio (the high-pitch whine that Animate modulates with RPM)
                var ta = tfType.GetField("turbineAudio", _flags)?.GetValue(tf);
                if (TryPlay(ta)) turbineStarted++;

                // Each turbofan's nozzles' thrustAudio (the low whoosh)
                var nozzles = tfType.GetField("nozzles", _flags)?.GetValue(tf) as JetNozzle[];
                if (nozzles == null) continue;
                foreach (var jn in nozzles)
                {
                    if (jn == null) continue;
                    var tha = jnType.GetField("thrustAudio", _flags)?.GetValue(jn);
                    if (TryPlay(tha)) thrustStarted++;
                }
            }

        }

        private static bool TryPlay(object audio)
        {
            if (audio == null) return false;
            try
            {
                var t = audio.GetType();
                var isPlayingProp = t.GetProperty("isPlaying");
                if (isPlayingProp != null)
                {
                    var playing = isPlayingProp.GetValue(audio, null);
                    if (playing is bool b && b) return false;   // already playing
                }
                t.GetMethod("Play", new System.Type[0])?.Invoke(audio, null);
                return true;
            }
            catch { return false; }
        }
    }



    // ===========================================================================
    // TURBOFAN AWAKE FIX — prefix patch that injects the bare minimum fields
    // Turbofan.Awake needs to complete without NRE. Without this, an
    // AddComponent<Turbofan>() at runtime NREs in Awake on `part.parentUnit`,
    // Unity silently benches the component, and its FixedUpdate is never called.
    //
    // No-op for stock instances (their `part` is already serialized in the prefab).
    // Safe vs AB-4X mod (which patches Turbojet, not Turbofan).
    // ===========================================================================
    [HarmonyPatch(typeof(Turbofan), "Awake")]
    public static class TurbofanAwakeFix
    {
        [HarmonyPrefix]
        public static void Prefix(Turbofan __instance)
        {
            try { InjectFields(__instance); }
            catch (System.Exception e) { AutoSpawnLog.Log.LogWarning("TurbofanAwakeFix: " + e.Message); }
        }

        private static void InjectFields(Turbofan tf)
        {
            var t = typeof(Turbofan);
            var flags = System.Reflection.BindingFlags.NonPublic | System.Reflection.BindingFlags.Public
                      | System.Reflection.BindingFlags.Instance;

            var partField = t.GetField("part", flags);
            if (partField != null)
            {
                var existing = partField.GetValue(tf) as UnitPart;
                if (existing == null)
                {
                    var found = tf.GetComponentInChildren<UnitPart>(true);
                    if (found == null) found = tf.GetComponentInParent<UnitPart>();
                    if (found != null) partField.SetValue(tf, found);
                }
            }

            var critField = t.GetField("criticalParts", flags);
            if (critField != null && critField.GetValue(tf) == null)
            {
                var critType = t.GetNestedType("CriticalPart",
                    System.Reflection.BindingFlags.NonPublic | System.Reflection.BindingFlags.Public);
                if (critType != null) critField.SetValue(tf, System.Array.CreateInstance(critType, 0));
            }

            // vectoringTransforms — Turbofan.FixedUpdate reads `vectoringTransforms.Length`
            // (ldlen at IL_03C6) AFTER the thrust calc but BEFORE the nozzle iteration.
            // Null → NRE → FixedUpdate aborts → no natural JetNozzle.Thrust calls and
            // no UseFuel (so range stays at ∞). Empty Transform[] makes the loop a no-op.
            var vectField = t.GetField("vectoringTransforms", flags);
            if (vectField != null && vectField.GetValue(tf) == null)
            {
                vectField.SetValue(tf, new Transform[0]);
            }

            // turbineAudio — Turbofan.Animate() (called as the FIRST instruction of
            // Turbofan.FixedUpdate) dereferences turbineAudio unconditionally. If null,
            // NRE → entire FixedUpdate aborts before fuel calc, nozzle iteration,
            // and UseFuel. Filling it in restores all three of those code paths.
            var turbineAudioField = t.GetField("turbineAudio", flags);
            if (turbineAudioField != null && turbineAudioField.GetValue(tf) == null)
            {
                object audio = null;
                object stockAudio = FindStockTurbineAudio(tf);

                var audioType = System.Type.GetType("UnityEngine.AudioSource, UnityEngine.AudioModule");
                if (audioType != null)
                {
                    var addMethod = typeof(GameObject).GetMethod("AddComponent", new System.Type[] { typeof(System.Type) });
                    if (addMethod != null) audio = addMethod.Invoke(tf.gameObject, new object[] { audioType });
                }

                if (audio != null && stockAudio != null)
                {
                    var atype = audio.GetType();
                    foreach (var prop in atype.GetProperties())
                    {
                        if (!prop.CanRead || !prop.CanWrite) continue;
                        if (prop.Name == "name" || prop.Name == "tag" || prop.Name == "hideFlags") continue;
                        try { prop.SetValue(audio, prop.GetValue(stockAudio, null), null); } catch { }
                    }
                    TrySet(atype, audio, "enabled",      true);
                    TrySet(atype, audio, "mute",         false);
                    TrySet(atype, audio, "loop",         true);
                    TrySet(atype, audio, "playOnAwake",  false);
                    TrySet(atype, audio, "spatialBlend", 1f);
                    TrySet(atype, audio, "volume",       1f);
                    // No explicit Play() — Turbofan.Animate() calls Play() when
                    // currentRPM > 0 (which only happens during actual flight,
                    // not in the spawn menu preview).
                }

                if (audio != null)
                {
                    turbineAudioField.SetValue(tf, audio);
                }
            }
        }

        private static object FindStockTurbineAudio(Turbofan ourTf)
        {
            try
            {
                var ttype = typeof(Turbofan);
                var flags = System.Reflection.BindingFlags.NonPublic | System.Reflection.BindingFlags.Public
                          | System.Reflection.BindingFlags.Instance;
                var turbineAudioField = ttype.GetField("turbineAudio", flags);
                if (turbineAudioField == null) return null;

                var all = Resources.FindObjectsOfTypeAll<Turbofan>();
                if (all == null) return null;
                foreach (var stf in all)
                {
                    if (stf == null || stf == ourTf) continue;
                    var ta = turbineAudioField.GetValue(stf);
                    if (ta == null) continue;
                    var clipProp = ta.GetType().GetProperty("clip");
                    if (clipProp == null) continue;
                    var clip = clipProp.GetValue(ta, null);
                    if (clip == null) continue;
                    return ta;
                }
            }
            catch { }
            return null;
        }

        private static void TrySet(System.Type t, object obj, string propName, object value)
        {
            try { t.GetProperty(propName)?.SetValue(obj, value, null); } catch { }
        }
    }

    // ===========================================================================
    // JETNOZZLE AWAKE FIX — same idea for JetNozzle. Pre-fills part, thrustAudio,
    // thrustTransform, afterburners, thrustProportion so Awake doesn't NRE.
    // ===========================================================================
    [HarmonyPatch(typeof(JetNozzle), "Awake")]
    public static class JetNozzleAwakeFix
    {
        private static bool _audioDiagLogged = false;

        private static void TrySet(System.Type t, object obj, string propName, object value)
        {
            try { t.GetProperty(propName)?.SetValue(obj, value, null); } catch { }
        }

        [HarmonyPrefix]
        public static void Prefix(JetNozzle __instance)
        {
            try { InjectFields(__instance); }
            catch (System.Exception e) { AutoSpawnLog.Log.LogWarning("JetNozzleAwakeFix: " + e.Message); }
        }

        private static object FindStockThrustAudio(JetNozzle ourJn)
        {
            try
            {
                var jnType = typeof(JetNozzle);
                var flags = System.Reflection.BindingFlags.NonPublic | System.Reflection.BindingFlags.Public
                          | System.Reflection.BindingFlags.Instance;
                var thrustAudioField = jnType.GetField("thrustAudio", flags);
                if (thrustAudioField == null) return null;

                var all = Resources.FindObjectsOfTypeAll<JetNozzle>();
                if (all == null) return null;
                foreach (var sjn in all)
                {
                    if (sjn == null || sjn == ourJn) continue;
                    // Accept prefabs — they DO have thrustAudio.clip configured even
                    // before any aircraft of that type spawns in-scene.
                    var ta = thrustAudioField.GetValue(sjn);
                    if (ta == null) continue;
                    var clipProp = ta.GetType().GetProperty("clip");
                    if (clipProp == null) continue;
                    var clip = clipProp.GetValue(ta, null);
                    if (clip == null) continue;
                    return ta;
                }
            }
            catch { }
            return null;
        }

        private static void InjectFields(JetNozzle jn)
        {
            var t = typeof(JetNozzle);
            var flags = System.Reflection.BindingFlags.NonPublic | System.Reflection.BindingFlags.Public
                      | System.Reflection.BindingFlags.Instance;

            // part — required by Awake's `aircraft = part.parentUnit as Aircraft`.
            var partField = t.GetField("part", flags);
            if (partField != null)
            {
                var existing = partField.GetValue(jn) as UnitPart;
                if (existing == null)
                {
                    var found = jn.GetComponentInChildren<UnitPart>(true);
                    if (found == null) found = jn.GetComponentInParent<UnitPart>();
                    if (found != null) partField.SetValue(jn, found);
                }
            }

            // thrustTransform — required by CreateIRSource() and JetNozzle.Thrust force application.
            var thrustTransformField = t.GetField("thrustTransform", flags);
            if (thrustTransformField != null && thrustTransformField.GetValue(jn) == null)
            {
                thrustTransformField.SetValue(jn, jn.transform);
            }

            // thrustAudio — required by `thrustAudio.set_time(...)` in Awake.
            // Strategy: ALWAYS create a fresh AudioSource on the JetNozzle's GameObject
            // and copy its config (clip, volume, spatialBlend, loop, etc.) from a
            // stock JetNozzle's thrustAudio. This way our jet gets a real jet engine
            // sound, and JetNozzle.AudioEffects() modulates pitch with RPM properly.
            var thrustAudioField = t.GetField("thrustAudio", flags);
            if (thrustAudioField != null && thrustAudioField.GetValue(jn) == null)
            {
                object audio = null;

                // Locate a stock JetNozzle whose thrustAudio is fully configured (has a clip).
                object stockAudio = FindStockThrustAudio(jn);

                // Create a fresh AudioSource on our JetNozzle's GameObject.
                var audioType = System.Type.GetType("UnityEngine.AudioSource, UnityEngine.AudioModule");
                if (audioType != null)
                {
                    var addMethod = typeof(GameObject).GetMethod("AddComponent", new System.Type[] { typeof(System.Type) });
                    if (addMethod != null) audio = addMethod.Invoke(jn.gameObject, new object[] { audioType });
                }

                // Copy properties from stock thrustAudio to our fresh one.
                if (audio != null && stockAudio != null)
                {
                    var atype = audio.GetType();
                    foreach (var prop in atype.GetProperties())
                    {
                        if (!prop.CanRead || !prop.CanWrite) continue;
                        if (prop.Name == "name" || prop.Name == "tag" || prop.Name == "hideFlags") continue;
                        try { prop.SetValue(audio, prop.GetValue(stockAudio, null), null); } catch { }
                    }
                    // Explicit overrides — make sure the audio is actually audible.
                    TrySet(atype, audio, "enabled",      true);
                    TrySet(atype, audio, "mute",         false);
                    TrySet(atype, audio, "loop",         true);
                    TrySet(atype, audio, "playOnAwake",  false);
                    TrySet(atype, audio, "spatialBlend", 1f);    // 3D
                    TrySet(atype, audio, "volume",       1f);    // AudioEffects overwrites per-frame
                    // No explicit Play() — natural mechanisms will start it when
                    // the engine spools up (prevents spawn-menu audio bleed).

                    // One-shot diag: log key audio properties so we can verify in the log.
                    if (!_audioDiagLogged)
                    {
                        _audioDiagLogged = true;
                        var clipName = "?";
                        try
                        {
                            var clip = atype.GetProperty("clip")?.GetValue(audio, null);
                            if (clip != null) clipName = clip.GetType().GetProperty("name")?.GetValue(clip, null)?.ToString() ?? "?";
                        }
                        catch { }
                    }
                }
                // Fallback: any AudioSource that already exists on the aircraft.
                if (audio == null || stockAudio == null)
                {
                    if (audio == null)
                    {
                        foreach (var c in jn.GetComponentsInChildren<Component>(true))
                        {
                            if (c != null && c.GetType().FullName == "UnityEngine.AudioSource") { audio = c; break; }
                        }
                    }
                }
                if (audio != null) thrustAudioField.SetValue(jn, audio);
            }

            // afterburners — iterated in Thrust(); must be at least an empty array.
            var afterField = t.GetField("afterburners", flags);
            if (afterField != null && afterField.GetValue(jn) == null)
            {
                var aType = t.GetNestedType("Afterburner",
                    System.Reflection.BindingFlags.NonPublic | System.Reflection.BindingFlags.Public);
                if (aType != null) afterField.SetValue(jn, System.Array.CreateInstance(aType, 0));
            }

            // thrustProportion — must be > 0 for AddForceAtPosition to fire.
            var propField = t.GetField("thrustProportion", flags);
            if (propField != null)
            {
                var v = propField.GetValue(jn);
                if (v is float f && f <= 0) propField.SetValue(jn, 1f);
            }
        }
    }

    // ===========================================================================
    // A-19N TURBOFAN CONVERSION — disables the prop drivetrain (PropFans stop
    // producing thrust, blade meshes hidden) and adds a Turbofan + JetNozzle to
    // each engine GameObject, configured with the field values we captured from
    // a stock NO Turbofan.
    //
    // Pattern for safe runtime AddComponent: disable the engine GameObject before
    // adding the component (so its Awake doesn't fire), reflect-set the private
    // fields, then re-enable. Once enabled, Turbofan.Awake reads `part`, resolves
    // its parent Aircraft, and registers itself with Aircraft.engineStates.
    // ===========================================================================
    [HarmonyPatch(typeof(Aircraft), "Awake")]
    public static class A19N_TurbofanConversion
    {
        private static readonly HashSet<Aircraft> _converted = new HashSet<Aircraft>();
        internal static readonly List<Turbofan> AllConvertedTurbofans = new List<Turbofan>();

        static A19N_TurbofanConversion()
        {
        }

        [HarmonyPostfix]
        public static void Postfix(Aircraft __instance)
        {
            try
            {
                if (_converted.Contains(__instance)) return;
                var def = __instance.definition;
                if (def == null || def.jsonKey != "CAS1_Naval") return;
                _converted.Add(__instance);
                Convert(__instance);
            }
            catch (System.Exception e) { AutoSpawnLog.Log.LogError("A19N_CONVERT Postfix: " + e); }
        }

        private static void Convert(Aircraft aircraft)
        {
            int disabled = 0, hidden = 0, added = 0;
            var transforms = aircraft.GetComponentsInChildren<Transform>(true);
            int aircraftId = aircraft.GetInstanceID();

            // STEP 1 — add Turbofans + JetNozzles. The JetNozzle is what actually
            // applies force (Rigidbody.AddForceAtPosition) in JetNozzle.Thrust(),
            // so the Turbofan can't push the airframe without at least one valid
            // JetNozzle in its nozzles[] array.
            try
            {
                foreach (var t in transforms)
                {
                    if (t == null) continue;
                    if (t.name != "engine_L" && t.name != "engine_R") continue;

                    var rootGo = aircraft.gameObject;
                    // Note: we don't check `go.GetComponent<Turbofan>() != null` anymore
                    // because we'd find the FIRST Turbofan we added when processing engine_R.
                    // The HashSet on Aircraft instance is what dedupes.

                    // v39: prefix patches on Turbofan.Awake and JetNozzle.Awake inject
                    // the required fields BEFORE Awake's body runs, so Awake completes
                    // normally (registers in engineStates, schedules SlowUpdate via
                    // StartSlowUpdate, etc.). We just AddComponent and configure the
                    // numeric/curve fields post-Awake.

                    var tf = rootGo.AddComponent<Turbofan>();   // Awake runs (prefix made it safe)
                    var stockTf = FindStockTurbofan();
                    if (stockTf != null) CopyStockFieldsTurbofan(tf, stockTf);

                    var flagsR = System.Reflection.BindingFlags.NonPublic | System.Reflection.BindingFlags.Public
                               | System.Reflection.BindingFlags.Instance;
                    Set(typeof(Turbofan), tf, "operable",     true,    flagsR);
                    Set(typeof(Turbofan), tf, "hasFuel",      true,    flagsR);
                    // staticThrust: 55 kN per engine (110 kN total). T/W ~0.94 at full
                    // load — enough margin for carrier takeoff without JATO buffing.
                    // Top speed is independently capped by the speedThrust curve below,
                    // so this number can be tuned for takeoff feel without affecting
                    // cruise/max speed.
                    Set(typeof(Turbofan), tf, "staticThrust", 55000f,  flagsR);

                    // Fuel efficiency boost — CAS aircraft (A-10, base A-19) are designed
                    // for long loiter and use unusually efficient engines (TF34 SFC ~0.37
                    // lb/lb-hr, much better than a typical fighter turbofan). Cut both
                    // idle and max fuel consumption to 50% of the stock Compass turbofan
                    // we copy from; roughly doubles loiter time at any throttle setting.
                    try
                    {
                        const float FuelEfficiencyMultiplier = 0.5f;
                        var minF = typeof(Turbofan).GetField("fuelConsumptionMin", flagsR);
                        var maxF = typeof(Turbofan).GetField("fuelConsumptionMax", flagsR);
                        if (minF != null) minF.SetValue(tf, (float)minF.GetValue(tf) * FuelEfficiencyMultiplier);
                        if (maxF != null) maxF.SetValue(tf, (float)maxF.GetValue(tf) * FuelEfficiencyMultiplier);
                    }
                    catch (System.Exception e) { AutoSpawnLog.Log.LogWarning("A19N fuel efficiency: " + e.Message); }

                    // speedThrust curve override — model straight-wing transonic drag rise.
                    // Stock curve was inherited from Compass/Medusa (swept-wing supersonic
                    // jets) which doesn't fall off until Mach 0.9+. For an A-10-clone
                    // airframe with straight wings, thrust effectively dies above Mach 0.6
                    // (~720 km/h at sea level) as drag overwhelms thrust. Hard physics
                    // ceiling regardless of staticThrust value, weight, or fuel state.
                    try
                    {
                        var speedThrustField = typeof(Turbofan).GetField("speedThrust", flagsR);
                        var curveType = System.Type.GetType("UnityEngine.AnimationCurve, UnityEngine.CoreModule");
                        if (speedThrustField != null && curveType != null)
                        {
                            var newCurve = System.Activator.CreateInstance(curveType);
                            var addKey = curveType.GetMethod("AddKey", new[] { typeof(float), typeof(float) });
                            if (addKey != null)
                            {
                                // (airspeed m/s, thrust multiplier). Tighter dropoff than v51 to
                                // hold top speed firmly in A-10 territory (~700 km/h) even with
                                // higher base thrust for takeoff margin.
                                addKey.Invoke(newCurve, new object[] {   0f, 1.00f });  //   0 km/h
                                addKey.Invoke(newCurve, new object[] { 120f, 1.00f });  // 432 km/h
                                addKey.Invoke(newCurve, new object[] { 160f, 0.95f });  // 576 km/h
                                addKey.Invoke(newCurve, new object[] { 180f, 0.70f });  // 648 km/h
                                addKey.Invoke(newCurve, new object[] { 195f, 0.30f });  // 702 km/h ← A-10 top
                                addKey.Invoke(newCurve, new object[] { 210f, 0.05f });  // 756 km/h
                                addKey.Invoke(newCurve, new object[] { 250f, 0.00f });  // 900 km/h
                                speedThrustField.SetValue(tf, newCurve);
                            }
                        }
                    }
                    catch (System.Exception e) { AutoSpawnLog.Log.LogWarning("A19N_CONVERT speedThrust override: " + e.Message); }
                    // No currentRPM override — let it default to 0 so the engine spools up
                    // naturally (preventing the spawn-jettison forward at hangar-door open).

                    var nozzle = rootGo.AddComponent<JetNozzle>();   // Awake runs (prefix made it safe)
                    var stockJn = FindStockJetNozzle();
                    if (stockJn != null) CopyStockFieldsJetNozzle(nozzle, stockJn);
                    Set(typeof(JetNozzle), nozzle, "thrustProportion", 1f, flagsR);
                    Set(typeof(JetNozzle), nozzle, "thrustTransform", aircraft.transform, flagsR);

                    // Wire the nozzle into Turbofan.nozzles[].
                    var nozzlesField = typeof(Turbofan).GetField("nozzles",
                        System.Reflection.BindingFlags.NonPublic | System.Reflection.BindingFlags.Instance);
                    if (nozzlesField != null) nozzlesField.SetValue(tf, new JetNozzle[] { nozzle });

                    // Verify alive.
                    var verifyTf = rootGo.GetComponent<Turbofan>();
                    var verifyJn = rootGo.GetComponent<JetNozzle>();
                    if (verifyTf != null) AllConvertedTurbofans.Add(verifyTf);

                    added++;
                }
            }
            catch (System.Exception e) { AutoSpawnLog.Log.LogError("A19N_CONVERT step1 (add Turbofans): " + e); }

            // Subscribe to aircraft destruction so we can stop our audio sources
            // immediately on death (instead of waiting for the engines and their
            // surrounding assets to despawn naturally, which leaves jet noise
            // playing for several seconds after the plane explodes).
            try
            {
                aircraft.onDisableUnit -= OnAircraftDisabled;
                aircraft.onDisableUnit += OnAircraftDisabled;
            }
            catch { }

            // STEP 2 — disable PropFans now that Turbofans should be producing real thrust.
            try
            {
                var fans = aircraft.GetComponentsInChildren<PropFan>(true);
                foreach (var pf in fans)
                {
                    if (pf == null) continue;
                    pf.enabled = false;
                    disabled++;
                }
            }
            catch (System.Exception e) { AutoSpawnLog.Log.LogError("A19N_CONVERT step2 (disable PropFans): " + e); }

            // STEP 3 — hide prop visuals. Deactivate hub_L and hub_R themselves
            // (hides the spinner cone visible at the engine pod nose, plus all
            // descendant prop blade meshes/disc). PropFan was already disabled
            // in step 2, so we don't need to keep the hub GameObjects active.
            try
            {
                foreach (var t in transforms)
                {
                    if (t == null) continue;
                    if (t.name != "hub_L" && t.name != "hub_R") continue;
                    if (!t.gameObject.activeSelf) continue;
                    t.gameObject.SetActive(false);
                    hidden++;
                }
            }
            catch (System.Exception e) { AutoSpawnLog.Log.LogError("A19N_CONVERT step3 (hide visuals): " + e); }

        }

        // Names of Turbofan fields we should NOT copy from the stock prefab —
        // they're either instance-identity (aircraft, part, etc.), populated by
        // us (nozzles, operable), or runtime-computed state (currentThrust...).
        private static readonly HashSet<string> _turbofanSkipFields = new HashSet<string>
        {
            "aircraft", "part", "controlInputs", "nozzles", "vectoringTransforms", "criticalParts",
            "currentRPM", "currentThrust", "operable", "hasFuel", "damageIndex",
            "dynamicThrustFactorSmoothed", "dynamicThrustFactorSmoothingVel",
            "spoolRatio", "lastFuelCheck", "hitPoints",
            "onReportDamage", "OnEngineDisable", "OnEngineDamage",
            // turbineAudio: keep our fresh AudioSource (on aircraft.gameObject) instead of
            // copying stock's reference (which lives on a stock aircraft elsewhere — would
            // cause audio to play at the wrong 3D position).
            "turbineAudio",
        };

        private static Turbofan _stockTurbofanCache;

        private static Turbofan FindStockTurbofan()
        {
            if (_stockTurbofanCache != null && _stockTurbofanCache.staticThrust > 0) return _stockTurbofanCache;
            _stockTurbofanCache = null;
            var all = Resources.FindObjectsOfTypeAll<Turbofan>();
            if (all == null) return null;
            foreach (var tf in all)
            {
                if (tf == null) continue;
                if (AllConvertedTurbofans.Contains(tf)) continue;
                if (tf.staticThrust <= 0) continue;   // filter out template / default instances
                _stockTurbofanCache = tf;
                return tf;
            }
            AutoSpawnLog.Log.LogWarning("A19N_CONVERT: no stock Turbofan with staticThrust > 0 found in resource pool.");
            return null;
        }

        private static void CopyStockFieldsTurbofan(Turbofan tf, Turbofan stock)
        {
            var type = typeof(Turbofan);
            const System.Reflection.BindingFlags flags = System.Reflection.BindingFlags.NonPublic
                                                       | System.Reflection.BindingFlags.Public
                                                       | System.Reflection.BindingFlags.Instance;
            int copied = 0;
            foreach (var f in type.GetFields(flags))
            {
                if (_turbofanSkipFields.Contains(f.Name)) continue;
                try { f.SetValue(tf, f.GetValue(stock)); copied++; }
                catch (System.Exception e) { AutoSpawnLog.Log.LogWarning("A19N_CONVERT copy-tf '" + f.Name + "': " + e.Message); }
            }
        }

        private static void CopyStockFieldsJetNozzle(JetNozzle jn, JetNozzle stock)
        {
            var type = typeof(JetNozzle);
            const System.Reflection.BindingFlags flags = System.Reflection.BindingFlags.NonPublic
                                                       | System.Reflection.BindingFlags.Public
                                                       | System.Reflection.BindingFlags.Instance;
            int copied = 0;
            foreach (var f in type.GetFields(flags))
            {
                if (_jetNozzleSkipFields.Contains(f.Name)) continue;
                try { f.SetValue(jn, f.GetValue(stock)); copied++; } catch { }
            }
        }


        private static readonly HashSet<string> _jetNozzleSkipFields = new HashSet<string>
        {
            "aircraft", "part", "engine", "turbojet", "thrustTransform", "thrustAudio",
            "afterburners", "irSource", "totalThrust", "thrustRatio", "rpmRatio", "fuelConsumption",
        };

        private static JetNozzle _stockJetNozzleCache;

        private static JetNozzle FindStockJetNozzle()
        {
            if (_stockJetNozzleCache != null) return _stockJetNozzleCache;
            var all = Resources.FindObjectsOfTypeAll<JetNozzle>();
            if (all == null) return null;
            var flags = System.Reflection.BindingFlags.NonPublic | System.Reflection.BindingFlags.Instance;
            var thrustPropField = typeof(JetNozzle).GetField("thrustProportion", flags);
            foreach (var jn in all)
            {
                if (jn == null) continue;
                // Filter out template / default instances with thrustProportion = 0.
                if (thrustPropField != null)
                {
                    var v = thrustPropField.GetValue(jn);
                    if (v is float f && f <= 0) continue;
                }
                _stockJetNozzleCache = jn;
                return jn;
            }
            AutoSpawnLog.Log.LogWarning("A19N_CONVERT: no stock JetNozzle with thrustProportion > 0 found.");
            return null;
        }


        // Called by aircraft.onDisableUnit when one of our converted aircraft
        // is destroyed. Stops the turbineAudio and thrustAudio sources right away
        // so the jet noise doesn't keep playing through the explosion animation.
        private static void OnAircraftDisabled(Unit unit)
        {
            try
            {
                var aircraft = unit as Aircraft;
                if (aircraft == null) return;
                StopAudioForAircraft(aircraft);
            }
            catch (System.Exception e) { AutoSpawnLog.Log.LogWarning("A19N audio-stop: " + e.Message); }
        }

        private static void StopAudioForAircraft(Aircraft aircraft)
        {
            var flags = System.Reflection.BindingFlags.NonPublic | System.Reflection.BindingFlags.Public
                      | System.Reflection.BindingFlags.Instance;
            var tfs = aircraft.GetComponentsInChildren<Turbofan>(true);
            if (tfs == null) return;
            foreach (var tf in tfs)
            {
                if (tf == null) continue;
                if (!AllConvertedTurbofans.Contains(tf)) continue;
                TryStop(typeof(Turbofan).GetField("turbineAudio", flags)?.GetValue(tf));
                var nozzles = typeof(Turbofan).GetField("nozzles", flags)?.GetValue(tf) as JetNozzle[];
                if (nozzles == null) continue;
                foreach (var jn in nozzles)
                {
                    if (jn == null) continue;
                    TryStop(typeof(JetNozzle).GetField("thrustAudio", flags)?.GetValue(jn));
                }
            }
        }

        private static void TryStop(object audio)
        {
            if (audio == null) return;
            try { audio.GetType().GetMethod("Stop", new System.Type[0])?.Invoke(audio, null); } catch { }
        }

        private static void Set(System.Type type, object obj, string name, object value, System.Reflection.BindingFlags flags)
        {
            var f = type.GetField(name, flags);
            if (f != null) f.SetValue(obj, value);
            else AutoSpawnLog.Log.LogWarning("A19N_CONVERT: Turbofan field '" + name + "' not found.");
        }
    }

    // ===========================================================================
    // STONEHENGE RUNTIME FIXES — Harmony patches that correct the bugs in the
    // already-compiled Stonehenge classes inside the base CustomWeapons DLL.
    // The source files in /Stonehenge are also fixed; these runtime patches just
    // make sure the shipped DLL behaves correctly until the next clean rebuild.
    // ===========================================================================

    // StonehengeControl.OnDestroy: original unconditionally accessed
    // attachedUnit.onDisableUnit/-onChangeFaction after a null check on
    // attachedUnit.NetworkHQ, which NREs when attachedUnit itself is gone.
    [HarmonyPatch(typeof(global::CustomWeapons.Stonehenge.StonehengeControl), "OnDestroy")]
    public static class StonehengeControl_OnDestroy_Fix
    {
        [HarmonyPrefix]
        public static bool Prefix(global::CustomWeapons.Stonehenge.StonehengeControl __instance)
        {
            try
            {
                var t = typeof(global::CustomWeapons.Stonehenge.StonehengeControl);
                var flags = System.Reflection.BindingFlags.NonPublic | System.Reflection.BindingFlags.Public
                          | System.Reflection.BindingFlags.Instance;
                var unit = t.GetField("attachedUnit", flags)?.GetValue(__instance) as Unit;
                var coord = t.GetField("turretCoordinator", flags)?.GetValue(__instance);

                if (coord != null)
                {
                    var deregM = coord.GetType().GetMethod("DeregisterController");
                    deregM?.Invoke(coord, new object[] { __instance });
                }
                if (unit != null && unit.NetworkHQ != null)
                {
                    global::CustomWeapons.Stonehenge.StonehengeRegistry.DeregisterTurret(__instance, unit.NetworkHQ);
                }
                if (unit != null)
                {
                    var disableM = t.GetMethod("StonehengeControl_OnUnitDisable", flags);
                    var factionM = t.GetMethod("StonehengeControl_OnChangeFaction", flags);
                    if (disableM != null)
                    {
                        var d = System.Delegate.CreateDelegate(typeof(System.Action<Unit>), __instance, disableM);
                        unit.onDisableUnit -= (System.Action<Unit>)d;
                    }
                    if (factionM != null)
                    {
                        var d = System.Delegate.CreateDelegate(typeof(System.Action<Unit>), __instance, factionM);
                        unit.onChangeFaction -= (System.Action<Unit>)d;
                    }
                }
            }
            catch (System.Exception e) { AutoSpawnLog.Log.LogWarning("StonehengeControl OnDestroy fix: " + e.Message); }
            return false;   // skip original
        }
    }

    // TurretCoordinator.Coordinator_OnTeamChanged: original tried to assign
    // NetworkHQ (a Mirage NetworkVariable owned by each controller's own Unit)
    // across foreign units, which throws. Replace with a safe re-search trigger:
    // drop the controller list and let the periodic SearchTurretControllers
    // pass repopulate it under the new faction.
    [HarmonyPatch(typeof(global::CustomWeapons.Stonehenge.TurretCoordinator), "Coordinator_OnTeamChanged")]
    public static class TurretCoordinator_OnTeamChanged_Fix
    {
        [HarmonyPrefix]
        public static bool Prefix(global::CustomWeapons.Stonehenge.TurretCoordinator __instance, Unit unit)
        {
            try
            {
                var t = typeof(global::CustomWeapons.Stonehenge.TurretCoordinator);
                var flags = System.Reflection.BindingFlags.NonPublic | System.Reflection.BindingFlags.Public
                          | System.Reflection.BindingFlags.Instance;
                var attachedUnit = t.GetField("attachedUnit", flags)?.GetValue(__instance) as Unit;
                var oldHqField = t.GetField("oldHQ", flags);
                var oldHQ = oldHqField?.GetValue(__instance) as FactionHQ;

                if (oldHQ != null)
                {
                    global::CustomWeapons.Stonehenge.StonehengeRegistry.DeregisterCoordinator(__instance, oldHQ);
                }

                if (attachedUnit != null)
                {
                    oldHqField?.SetValue(__instance, attachedUnit.NetworkHQ);
                    if (attachedUnit.NetworkHQ != null)
                    {
                        global::CustomWeapons.Stonehenge.StonehengeRegistry.RegisterCoordinator(__instance, attachedUnit.NetworkHQ);
                    }
                }

                // Clear the local controller list so the next periodic scan
                // (SearchTurretControllers) re-discovers under the new faction.
                var controllers = t.GetField("controllers", flags)?.GetValue(__instance) as System.Collections.IList;
                controllers?.Clear();
            }
            catch (System.Exception e) { AutoSpawnLog.Log.LogWarning("TurretCoordinator OnTeamChanged fix: " + e.Message); }
            return false;
        }
    }

    // TurretCoordinator.OnDestroy: same null-guard issue as StonehengeControl.
    [HarmonyPatch(typeof(global::CustomWeapons.Stonehenge.TurretCoordinator), "OnDestroy")]
    public static class TurretCoordinator_OnDestroy_Fix
    {
        [HarmonyPrefix]
        public static bool Prefix(global::CustomWeapons.Stonehenge.TurretCoordinator __instance)
        {
            try
            {
                var t = typeof(global::CustomWeapons.Stonehenge.TurretCoordinator);
                var flags = System.Reflection.BindingFlags.NonPublic | System.Reflection.BindingFlags.Public
                          | System.Reflection.BindingFlags.Instance;
                var unit = t.GetField("attachedUnit", flags)?.GetValue(__instance) as Unit;

                if (unit != null && unit.NetworkHQ != null)
                {
                    global::CustomWeapons.Stonehenge.StonehengeRegistry.DeregisterCoordinator(__instance, unit.NetworkHQ);
                }
                if (unit != null)
                {
                    var disableM = t.GetMethod("Coordinator_OnUnitDisabled", flags);
                    var teamM = t.GetMethod("Coordinator_OnTeamChanged", flags);
                    if (disableM != null)
                    {
                        var d = System.Delegate.CreateDelegate(typeof(System.Action<Unit>), __instance, disableM);
                        unit.onDisableUnit -= (System.Action<Unit>)d;
                    }
                    if (teamM != null)
                    {
                        var d = System.Delegate.CreateDelegate(typeof(System.Action<Unit>), __instance, teamM);
                        unit.onChangeFaction -= (System.Action<Unit>)d;
                    }
                }
            }
            catch (System.Exception e) { AutoSpawnLog.Log.LogWarning("TurretCoordinator OnDestroy fix: " + e.Message); }
            return false;
        }
    }

    // ===========================================================================
    // MISSILE / WEAPON NAME CLEANUP — strips "(Broken?)" and similar broken-tag
    // suffixes from weapon display names. Triggered from two events:
    //   1. AircraftSelectionMenu.Initialize  — fires before the spawn-menu shows,
    //      catches ODX09 (Broken?), AAM-72 Stormlance (Broken?), etc. as soon as
    //      the player opens the hangar.
    //   2. MissionManager.OnStartServer       — catches anything loaded later or
    //      added by other mods.
    // The shared _ran flag means the actual work happens once per session.
    // ===========================================================================
    internal static class BrokenTagStripperImpl
    {
        private static bool _ran;

        public static void Run()
        {
            if (_ran) return;
            try
            {
                int hits = 0;
                foreach (var w in Resources.FindObjectsOfTypeAll<WeaponInfo>())
                {
                    if (w == null) continue;
                    var n1 = CleanName(w.weaponName);
                    if (n1 != w.weaponName) { w.weaponName = n1; hits++; }
                    var n2 = CleanName(w.shortName);
                    if (n2 != w.shortName) { w.shortName = n2; hits++; }
                    var n3 = CleanName(w.description);
                    if (n3 != w.description) { w.description = n3; hits++; }
                }
                foreach (var u in Resources.FindObjectsOfTypeAll<UnitDefinition>())
                {
                    if (u == null) continue;
                    var n1 = CleanName(u.unitName);
                    if (n1 != u.unitName) { u.unitName = n1; hits++; }
                    var n2 = CleanName(u.bogeyName);
                    if (n2 != u.bogeyName) { u.bogeyName = n2; hits++; }
                }
                _ran = true;
                AutoSpawnLog.Log.LogWarning("BrokenTagStripper: cleaned " + hits + " name(s).");
            }
            catch (System.Exception e) { AutoSpawnLog.Log.LogWarning("BrokenTagStripper: " + e.Message); }
        }

        // Strips parenthetical broken-tag suffixes. Matches "(Broken?)",
        // "(Broken)", "(broken?)", "(BROKEN)", "(WIP)", " - Broken", etc.
        public static string CleanName(string s)
        {
            if (string.IsNullOrEmpty(s)) return s;
            var result = System.Text.RegularExpressions.Regex.Replace(
                s, @"\s*[\(\[\-]\s*(Broken\??|BROKEN\??|broken\??|WIP|wip|Wip|TODO)\s*[\)\]]?\s*$", "",
                System.Text.RegularExpressions.RegexOptions.IgnoreCase);
            return result.Trim();
        }
    }

    [HarmonyPatch(typeof(AircraftSelectionMenu), "Initialize")]
    public static class BrokenTagStripper_OnMenuInit
    {
        [HarmonyPostfix] public static void Postfix() { BrokenTagStripperImpl.Run(); }
    }

    [HarmonyPatch(typeof(MissionManager), "OnStartServer")]
    public static class BrokenTagStripper_OnStartServer
    {
        [HarmonyPostfix] public static void Postfix() { BrokenTagStripperImpl.Run(); }
    }

}
