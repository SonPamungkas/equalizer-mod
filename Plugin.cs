using BepInEx;
using HarmonyLib;
using UnityEngine;
using System.Collections;
using System.Collections.Generic;
using System.Linq;

namespace EqualizerMod
{
    [BepInPlugin("com.equalizer.unified", "Equalizer Mod", "2.2.0")]
    public class EqualizerPlugin : BaseUnityPlugin
    {
        public static EqualizerPlugin Instance;
        public static BepInEx.Configuration.ConfigEntry<bool> EqualizeEnabled;
        public static BepInEx.Configuration.ConfigEntry<bool> AntiCheatEnabled;
        public static BepInEx.Configuration.ConfigEntry<bool> VerboseLogging;
        
        // Ground specific
        public static BepInEx.Configuration.ConfigEntry<float> SpawnDelay;
        public static BepInEx.Configuration.ConfigEntry<bool> EnableSideSpawn;

        // Aircraft configs
        public static Dictionary<string, BepInEx.Configuration.ConfigEntry<bool>> AircraftToggles = new Dictionary<string, BepInEx.Configuration.ConfigEntry<bool>>();
        public static Dictionary<string, BepInEx.Configuration.ConfigEntry<int>> AircraftRestrictions = new Dictionary<string, BepInEx.Configuration.ConfigEntry<int>>();
        public static Dictionary<string, BepInEx.Configuration.ConfigEntry<float>> AircraftMultipliers = new Dictionary<string, BepInEx.Configuration.ConfigEntry<float>>();

        // Ground configs
        public static Dictionary<string, BepInEx.Configuration.ConfigEntry<string>> GroundLinkedUnits = new Dictionary<string, BepInEx.Configuration.ConfigEntry<string>>();
        public static Dictionary<string, BepInEx.Configuration.ConfigEntry<int>> GroundRestrictions = new Dictionary<string, BepInEx.Configuration.ConfigEntry<int>>();
        public static Dictionary<string, BepInEx.Configuration.ConfigEntry<float>> GroundMultipliers = new Dictionary<string, BepInEx.Configuration.ConfigEntry<float>>();

        private void Awake()
        {
            Instance = this;
            
            EqualizeEnabled = Config.Bind("General", "Equalize Enabled", true, "Global toggle for the equalization logic.");
            AntiCheatEnabled = Config.Bind("General", "Anti-Cheat Enabled", true, "Prevents infinite modded aircraft printing from reservation refunds.");
            VerboseLogging = Config.Bind("General", "Verbose Logging", false, "Enable verbose logging for deliveries and spawns.");
            
            SpawnDelay = Config.Bind("Ground", "Spawn Delay", 1.5f, "Delay between staggered modded ground vehicle physical spawns (in seconds).");
            EnableSideSpawn = Config.Bind("Ground", "Enable Side Spawn", true, "If enabled, modded linked units will spawn to the side of the depot instead of the front door.");

            var harmony = new Harmony("com.equalizer.unified");
            harmony.PatchAll();
            Logger.LogInfo("Equalizer Mod v2.2.0 loaded!");
        }

        public bool IsAircraftAllowed(AircraftDefinition ac, FactionHQ hq)
        {
            if (ac == null || hq == null || hq.faction == null) return false;
            string key = ac.jsonKey.ToLower();

            if (!AircraftToggles.ContainsKey(key))
            {
                BindAircraftConfig(ac);
            }

            if (!AircraftToggles[key].Value) return false;

            int restriction = AircraftRestrictions[key].Value;
            if (restriction == 0) return true;

            string factionName = hq.faction.factionName.ToLower();
            if (restriction == 1 && (factionName.Contains("primeva") || factionName.Contains("pala"))) return false;
            if (restriction == 2 && (factionName.Contains("boscali") || factionName.Contains("bdf"))) return false;

            return true;
        }

        public bool IsGroundFactionAllowed(VehicleDefinition vd, FactionHQ hq)
        {
            if (vd == null || hq == null || hq.faction == null) return false;
            string key = vd.jsonKey.ToLower();

            if (EqualizerLogic.IsVanillaGround(vd)) return true;

            if (!GroundLinkedUnits.ContainsKey(key) || GroundLinkedUnits[key].Value == "None") return false;

            int restriction = GroundRestrictions[key].Value;
            if (restriction == 0) return true;

            string factionName = hq.faction.factionName.ToLower();
            if (restriction == 1 && (factionName.Contains("primeva") || factionName.Contains("pala"))) return false;
            if (restriction == 2 && (factionName.Contains("boscali") || factionName.Contains("bdf"))) return false;

            return true;
        }

        public void BindAircraftConfig(AircraftDefinition ac)
        {
            if (ac == null) return;
            string key = ac.jsonKey.ToLower();

            if (EqualizerLogic.IsVanillaAircraft(ac)) return;

            if (!AircraftToggles.ContainsKey(key))
            {
                Debug.Log($"[EqualizerMod] Binding new aircraft config for: {ac.unitName} ({ac.jsonKey})");
                string pName = ac.unitPrefab != null ? ac.unitPrefab.name : ac.name;
                string dispName = string.IsNullOrEmpty(ac.unitName) ? pName : $"{ac.unitName} ({pName})";
                AircraftToggles[key] = Config.Bind("Aircraft Toggles", $"Equalize {dispName}", true, $"Enable or disable equalization for {dispName}.");
                
                AircraftRestrictions[key] = Config.Bind("Aircraft Restrictions", $"{dispName} Restriction", 0, 
                    new BepInEx.Configuration.ConfigDescription($"Restriction for {dispName}: 0=Both, 1=No PALA, 2=No BDF", 
                    new BepInEx.Configuration.AcceptableValueRange<int>(0, 2)));

                AircraftMultipliers[key] = Config.Bind("Aircraft Multipliers", $"{dispName} Multiplier", 1.0f,
                    new BepInEx.Configuration.ConfigDescription($"Equalization multiplier for {dispName} (0-10)",
                    new BepInEx.Configuration.AcceptableValueRange<float>(0f, 10f)));
            }
        }

        public void BindGroundConfig(VehicleDefinition vd)
        {
            if (vd == null) return;
            string key = vd.jsonKey.ToLower();

            if (EqualizerLogic.IsVanillaGround(vd)) return;

            if (!GroundLinkedUnits.ContainsKey(key))
            {
                Debug.Log($"[EqualizerMod] Binding new ground config for: {vd.unitName} ({vd.jsonKey})");
                string pName = vd.unitPrefab != null ? vd.unitPrefab.name : vd.name;
                string dispName = string.IsNullOrEmpty(vd.unitName) ? pName : $"{vd.unitName} ({pName})";
                
                var acceptableValues = new BepInEx.Configuration.AcceptableValueList<string>(EqualizerLogic.VanillaVehicleNames.ToArray());
                GroundLinkedUnits[key] = Config.Bind("Ground Links", $"{dispName} Linked Vanilla Unit", "None",
                    new BepInEx.Configuration.ConfigDescription($"Vanilla vehicle to link production with.", acceptableValues));

                GroundMultipliers[key] = Config.Bind("Ground Multipliers", $"{dispName} Multiplier", 1.0f,
                    new BepInEx.Configuration.ConfigDescription($"Equalization multiplier for {dispName} (0-10)",
                    new BepInEx.Configuration.AcceptableValueRange<float>(0f, 10f)));

                GroundRestrictions[key] = Config.Bind("Ground Restrictions", $"{dispName} Restriction", 0, 
                    new BepInEx.Configuration.ConfigDescription($"Restriction for {dispName}: 0=Both, 1=No PALA, 2=No BDF", 
                    new BepInEx.Configuration.AcceptableValueRange<int>(0, 2)));
            }
        }
    }

    [HarmonyPatch(typeof(FactionHQ), "OnMissionLoad")]
    public static class FactionHQ_OnMissionLoad_Patch
    {
        public static void Postfix(FactionHQ __instance)
        {
            if (EqualizerPlugin.EqualizeEnabled != null && !EqualizerPlugin.EqualizeEnabled.Value) return;
            EqualizerLogic.EqualizeAircraftInventory(__instance);
            EqualizerLogic.EqualizeGroundInventory(__instance);
        }
    }

    [HarmonyPatch(typeof(FactionHQ), "AddSupplyUnit")]
    public static class FactionHQ_AddSupplyUnit_Patch
    {
        private static bool _isEqualizing = false;

        public static void Postfix(FactionHQ __instance, UnitDefinition unitDefinition, int amount)
        {
            if (_isEqualizing || amount <= 0) return;
            if (!(unitDefinition is AircraftDefinition aircraftDefinition)) return;

            if (EqualizerPlugin.AntiCheatEnabled.Value)
            {
                var stackTrace = new System.Diagnostics.StackTrace();
                bool isRefund = false;
                for (int i = 0; i < stackTrace.FrameCount; i++)
                {
                    string methodName = stackTrace.GetFrame(i).GetMethod().Name.ToLower();
                    if (methodName.Contains("cancel") || methodName.Contains("refund") || 
                        methodName.Contains("return") || methodName.Contains("store") || 
                        methodName.Contains("despawn") || methodName.Contains("recycle"))
                    {
                        isRefund = true;
                        break;
                    }
                }
                
                if (isRefund)
                {
                    if (EqualizerPlugin.VerboseLogging.Value)
                        Debug.Log($"[EqualizerMod] Anti-Cheat blocked aircraft printing from refund for: {aircraftDefinition.unitName}");
                    return;
                }
            }

            _isEqualizing = true;
            try
            {
                EqualizerLogic.EqualizeAircraftProduction(__instance, aircraftDefinition, amount);
            }
            finally
            {
                _isEqualizing = false;
            }
        }
    }

    [HarmonyPatch(typeof(Factory), "ProduceUnit")]
    public static class Factory_ProduceUnit_Patch
    {
        public static void Postfix(Factory __instance)
        {
            if (EqualizerPlugin.EqualizeEnabled != null && !EqualizerPlugin.EqualizeEnabled.Value) return;
            EqualizerLogic.HandleGroundProduction(__instance);
        }
    }

    [HarmonyPatch(typeof(VehicleDepot), "TrySpawnVehicle")]
    public static class VehicleDepot_TrySpawnVehicle_Patch
    {
        private static readonly AccessTools.FieldRef<VehicleDepot, float> LastSpawnedTimeRef =
            AccessTools.FieldRefAccess<VehicleDepot, float>("lastSpawnedTime");

        private static readonly AccessTools.FieldRef<VehicleDepot, Transform> SpawnTransformRef =
            AccessTools.FieldRefAccess<VehicleDepot, Transform>("spawnTransform");

        public static void Postfix(VehicleDepot __instance, VehicleDefinition vehicleDefinition, bool __result)
        {
            if (!__result) return;
            if (EqualizerPlugin.EqualizeEnabled != null && !EqualizerPlugin.EqualizeEnabled.Value) return;
            if (!EqualizerLogic.IsVanillaGround(vehicleDefinition)) return;

            string vanillaDispName = EqualizerLogic.GetVehicleDisplayName(vehicleDefinition);

            FactionHQ hq = __instance.NetworkHQ;
            if (hq == null) return;

            int delayIndex = 1;
            foreach (var modded in EqualizerLogic.ModdedVehiclesList)
            {
                string key = EqualizerLogic.ModdedKeys[modded];
                if (!EqualizerPlugin.GroundLinkedUnits.ContainsKey(key)) continue;

                string linkedVanillaName = EqualizerPlugin.GroundLinkedUnits[key].Value;
                if (linkedVanillaName != vanillaDispName) continue;

                if (!EqualizerPlugin.Instance.IsGroundFactionAllowed(modded, hq)) continue;

                int moddedStock = hq.GetUnitSupply(modded);
                if (moddedStock > 0)
                {
                    EqualizerPlugin.Instance.StartCoroutine(SpawnModdedWithDelay(__instance, modded, vehicleDefinition, EqualizerPlugin.SpawnDelay.Value * delayIndex, delayIndex));
                    delayIndex++;
                }
            }
        }

        private static IEnumerator SpawnModdedWithDelay(VehicleDepot depot, VehicleDefinition modded, VehicleDefinition vanilla, float delay, int spawnIndex)
        {
            yield return new WaitForSeconds(delay);
            if (depot == null || modded == null) yield break;

            Transform spawnTransform = SpawnTransformRef(depot);
            Vector3 originalPos = spawnTransform.position;

            if (EqualizerPlugin.EnableSideSpawn != null && EqualizerPlugin.EnableSideSpawn.Value)
            {
                Vector3 anchorRight = spawnTransform.right; anchorRight.y = 0; anchorRight.Normalize();
                Vector3 anchorForward = spawnTransform.forward; anchorForward.y = 0; anchorForward.Normalize();
                
                float zOffset = -(spawnIndex - 1) * 12f;
                Vector3 worldOffset = (anchorRight * -20f) + (anchorForward * zOffset);
                
                GlobalPosition spawnPos = GlobalPositionExtensions.ToGlobalPosition(spawnTransform.position) + worldOffset;
                Quaternion randomRot = Quaternion.Euler(0, spawnTransform.eulerAngles.y, 0);

                FactionHQ hq = null;
                var unit = depot.GetComponent<Unit>();
                if (unit != null) hq = unit.MapHQ ?? unit.NetworkHQ;

                Spawner.i.SpawnVehicle(modded.unitPrefab, spawnPos, randomRot, Vector3.zero, hq, $"ModdedUnit_{System.Guid.NewGuid().ToString().Substring(0,4)}", 0f, false, null);
                yield break;
            }

            float prevTime = LastSpawnedTimeRef(depot);
            LastSpawnedTimeRef(depot) = -9999f;
            bool spawnedFlag = depot.TrySpawnVehicle(modded);
            LastSpawnedTimeRef(depot) = prevTime;
            
            if (spawnedFlag)
            {
                if (EqualizerPlugin.VerboseLogging.Value)
                {
                    Debug.Log($"[EqualizerMod] Linked spawn: {modded.unitName} physically spawned alongside {vanilla.unitName}");
                }
            }
        }
    }

    public class AircraftTierInfo
    {
        public int Rank;
        public List<AircraftDefinition> VanillaAircraft = new List<AircraftDefinition>();
        public List<AircraftDefinition> ModdedAircraft = new List<AircraftDefinition>();
    }

    public static class EqualizerLogic
    {
        // Aircraft fields
        public static Dictionary<int, AircraftTierInfo> TierInfoMap = new Dictionary<int, AircraftTierInfo>();
        private static readonly HashSet<string> VanillaAircraftKeys = new HashSet<string>
        {
            "coin", "trainer", "utilityhelo1", "attackhelo1", "cas1", "fighter1", 
            "smallfighter1", "quadvtol1", "multirole1", "ew1", "darkreach", "fastbomber1"
        };
        public static Dictionary<string, float> FractionalProductionCache = new Dictionary<string, float>();

        // Ground fields
        public static Dictionary<string, VehicleDefinition> VanillaVehicleDict = new Dictionary<string, VehicleDefinition>();
        public static List<string> VanillaVehicleNames = new List<string>();
        public static List<VehicleDefinition> ModdedVehiclesList = new List<VehicleDefinition>();
        public static Dictionary<VehicleDefinition, string> ModdedKeys = new Dictionary<VehicleDefinition, string>();
        private static readonly HashSet<string> VanillaVehicleKeys = new HashSet<string>
        {
            "horse1", "6x6_1_at", "hlt-ft", "cramtrailer1", "ugvdozer1", "afv8_sam",
            "truck2-rsam", "6x6_1_aa", "lighttruck1_aa", "hlt-m", "truck2-fc", "afv8_ifv",
            "mbt1", "linebreaker_ifv", "linebreaker_apc", "linebreaker_sam", "spaag2",
            "truck2-mrap", "lighttruck1_at", "truck2-ft", "truck2-m", "truck2-l", "radarsam1",
            "hlt-t", "truck2-t", "hlt-l", "lcv45", "ugv1_grenade", "6x6_1_apc", "6x6_1_ifv",
            "ugv1_sam", "hlt-fc", "afv8_apc", "samturret1", "spaag1", "mbt", "samtrailer1",
            "radarcontainer1", "lasertrailer1", "hlt-r", "truck2-lads", "truck2-r", "truck2-cram",
            "truck2-tbm", "truck2-tbm-n"
        };

        public static bool IsVanillaAircraft(AircraftDefinition ac)
        {
            if (ac == null) return false;
            return VanillaAircraftKeys.Contains(ac.jsonKey.ToLower());
        }

        public static bool IsVanillaGround(VehicleDefinition vd)
        {
            if (vd == null) return false;
            string prefabName = vd.unitPrefab != null ? vd.unitPrefab.name.ToLower() : vd.jsonKey.ToLower();
            return VanillaVehicleKeys.Contains(prefabName);
        }

        private static bool IsSegregatedAircraft(AircraftDefinition definition)
        {
            if (definition == null) return false;

            if (!string.IsNullOrEmpty(definition.jsonKey) && definition.jsonKey.StartsWith("kar_", System.StringComparison.OrdinalIgnoreCase))
                return true;
            if (!string.IsNullOrEmpty(definition.name) && definition.name.StartsWith("kar_", System.StringComparison.OrdinalIgnoreCase))
                return true;

            if (!string.IsNullOrEmpty(definition.jsonKey) && definition.jsonKey.StartsWith("bote_", System.StringComparison.OrdinalIgnoreCase))
                return true;
            if (!string.IsNullOrEmpty(definition.name) && definition.name.StartsWith("bote_", System.StringComparison.OrdinalIgnoreCase))
                return true;

            if (definition.unitPrefab != null && definition.unitPrefab.GetComponent("ShipPartBridge") != null)
                return true;

            return false;
        }

        public static string GetVehicleDisplayName(VehicleDefinition vd)
        {
            if (vd == null) return "None";
            string pName = vd.unitPrefab != null ? vd.unitPrefab.name : vd.name;
            return string.IsNullOrEmpty(vd.unitName) ? pName : $"{vd.unitName} ({pName})";
        }

        public static void ScanAircraft()
        {
            Debug.Log("[EqualizerMod] Scanning for aircraft definitions...");
            TierInfoMap.Clear();

            var allAircraft = Resources.FindObjectsOfTypeAll<AircraftDefinition>();
            foreach (var ac in allAircraft)
            {
                if (ac == null || ac.aircraftParameters == null) continue;

                if (IsSegregatedAircraft(ac))
                {
                    Debug.Log($"[EqualizerMod] Skipping segregated aircraft {ac.jsonKey ?? ac.name}");
                    continue;
                }

                int rank = ac.aircraftParameters.rankRequired;
                bool isModded = !IsVanillaAircraft(ac);

                if (!TierInfoMap.ContainsKey(rank))
                {
                    TierInfoMap[rank] = new AircraftTierInfo { Rank = rank };
                }

                if (isModded)
                {
                    TierInfoMap[rank].ModdedAircraft.Add(ac);
                    EqualizerPlugin.Instance.BindAircraftConfig(ac);
                }
                else
                {
                    TierInfoMap[rank].VanillaAircraft.Add(ac);
                }
            }
        }

        public static void ScanVehicles()
        {
            Debug.Log("[EqualizerMod] Scanning for ground vehicle definitions...");

            var allVehicles = Resources.FindObjectsOfTypeAll<VehicleDefinition>();
            var vanillaVehicles = allVehicles.Where(v => IsVanillaGround(v)).ToList();
            ModdedVehiclesList = allVehicles.Where(v => !IsVanillaGround(v)).ToList();

            VanillaVehicleDict.Clear();
            VanillaVehicleNames.Clear();
            VanillaVehicleNames.Add("None");

            foreach (var vanilla in vanillaVehicles)
            {
                string dispName = GetVehicleDisplayName(vanilla);
                if (!VanillaVehicleDict.ContainsKey(dispName))
                {
                    VanillaVehicleDict[dispName] = vanilla;
                    VanillaVehicleNames.Add(dispName);
                }
            }

            foreach (var modded in ModdedVehiclesList)
            {
                ModdedKeys[modded] = modded.jsonKey.ToLower();
                EqualizerPlugin.Instance.BindGroundConfig(modded);
            }
        }

        public static void EqualizeAircraftInventory(FactionHQ hq)
        {
            if (hq == null || hq.faction == null) return;
            if (TierInfoMap.Count == 0) ScanAircraft();

            foreach (var tier in TierInfoMap.Values)
            {
                if (tier.ModdedAircraft.Count == 0) continue;
                
                int minVanillaCount = int.MaxValue;
                bool foundVanilla = false;

                foreach (var vanilla in tier.VanillaAircraft)
                {
                    int count = hq.GetUnitSupply(vanilla);
                    if (count < minVanillaCount) minVanillaCount = count;
                    foundVanilla = true;
                }

                if (!foundVanilla) continue;
                if (minVanillaCount == int.MaxValue) minVanillaCount = 0;

                foreach (var modded in tier.ModdedAircraft)
                {
                    if (!EqualizerPlugin.Instance.IsAircraftAllowed(modded, hq)) continue;

                    if (hq.restrictedAircraft != null && hq.restrictedAircraft.Contains(modded.jsonKey))
                        hq.restrictedAircraft.Remove(modded.jsonKey);

                    string modKey = modded.jsonKey.ToLower();
                    float multiplier = 1.0f;
                    if (EqualizerPlugin.AircraftMultipliers.ContainsKey(modKey))
                    {
                        multiplier = EqualizerPlugin.AircraftMultipliers[modKey].Value;
                    }

                    int targetCount = Mathf.RoundToInt(minVanillaCount * multiplier);
                    int currentCount = hq.GetUnitSupply(modded);
                    if (currentCount < targetCount)
                        hq.AddSupplyUnit(modded, targetCount - currentCount);
                }
            }
        }

        public static void EqualizeGroundInventory(FactionHQ hq)
        {
            if (VanillaVehicleNames.Count <= 1) ScanVehicles();

            foreach (var modded in ModdedVehiclesList)
            {
                if (!EqualizerPlugin.Instance.IsGroundFactionAllowed(modded, hq)) continue;

                string key = ModdedKeys[modded];
                if (!EqualizerPlugin.GroundLinkedUnits.ContainsKey(key)) continue;

                string linkedVanillaName = EqualizerPlugin.GroundLinkedUnits[key].Value;
                if (linkedVanillaName == "None" || !VanillaVehicleDict.ContainsKey(linkedVanillaName)) continue;

                VehicleDefinition vanillaDef = VanillaVehicleDict[linkedVanillaName];
                int vanillaStock = hq.GetUnitSupply(vanillaDef);
                if (vanillaStock <= 0) continue;

                float multiplier = 1.0f;
                if (EqualizerPlugin.GroundMultipliers.ContainsKey(key))
                {
                    multiplier = EqualizerPlugin.GroundMultipliers[key].Value;
                }

                int targetCap = Mathf.RoundToInt(vanillaStock * multiplier);
                int moddedStock = hq.GetUnitSupply(modded);
                if (moddedStock < targetCap)
                {
                    int addAmount = targetCap - moddedStock;
                    if (EqualizerPlugin.VerboseLogging.Value)
                        Debug.Log($"[EqualizerMod] Ground Initial Supply: Adding {addAmount}x {modded.unitName} to {hq.faction.factionName}");
                    hq.AddSupplyUnit(modded, addAmount);
                }
            }
        }

        public static void EqualizeAircraftProduction(FactionHQ hq, AircraftDefinition aircraftDefinition, int amount)
        {
            if (EqualizerPlugin.EqualizeEnabled != null && !EqualizerPlugin.EqualizeEnabled.Value) return;
            if (hq == null || aircraftDefinition == null || aircraftDefinition.aircraftParameters == null) return;
            if (TierInfoMap.Count == 0) ScanAircraft();

            string key = aircraftDefinition.jsonKey.ToLower();
            if (!VanillaAircraftKeys.Contains(key)) return;

            int rank = aircraftDefinition.aircraftParameters.rankRequired;
            if (!TierInfoMap.ContainsKey(rank)) return;

            var tier = TierInfoMap[rank];
            foreach (var modded in tier.ModdedAircraft)
            {
                if (!EqualizerPlugin.Instance.IsAircraftAllowed(modded, hq)) continue;

                if (hq.restrictedAircraft != null && hq.restrictedAircraft.Contains(modded.jsonKey))
                    hq.restrictedAircraft.Remove(modded.jsonKey);

                string modKey = modded.jsonKey.ToLower();
                float multiplier = 1.0f;
                if (EqualizerPlugin.AircraftMultipliers.ContainsKey(modKey))
                {
                    multiplier = EqualizerPlugin.AircraftMultipliers[modKey].Value;
                }

                string cacheKey = hq.faction.factionName + "_" + modKey;
                if (!FractionalProductionCache.ContainsKey(cacheKey))
                {
                    FractionalProductionCache[cacheKey] = 0f;
                }

                FractionalProductionCache[cacheKey] += (amount * multiplier);

                int addAmount = Mathf.FloorToInt(FractionalProductionCache[cacheKey]);
                if (addAmount > 0)
                {
                    FractionalProductionCache[cacheKey] -= addAmount;
                    hq.AddSupplyUnit(modded, addAmount);
                }
            }
        }

        public static void HandleGroundProduction(Factory factory)
        {
            if (factory == null || factory.ProductionUnit == null) return;
            if (!(factory.ProductionUnit is VehicleDefinition vanillaDef)) return;
            if (!IsVanillaGround(vanillaDef)) return;

            string vanillaDispName = GetVehicleDisplayName(vanillaDef);

            FactionHQ hq = null;
            if (factory.attachedUnit != null) hq = factory.attachedUnit.NetworkHQ;
            if (hq == null) return;

            foreach (var modded in ModdedVehiclesList)
            {
                string key = ModdedKeys[modded];
                if (!EqualizerPlugin.GroundLinkedUnits.ContainsKey(key)) continue;
                
                string linkedVanillaName = EqualizerPlugin.GroundLinkedUnits[key].Value;
                if (linkedVanillaName != vanillaDispName) continue;

                if (!EqualizerPlugin.Instance.IsGroundFactionAllowed(modded, hq)) continue;

                float multiplier = 1.0f;
                if (EqualizerPlugin.GroundMultipliers.ContainsKey(key))
                {
                    multiplier = EqualizerPlugin.GroundMultipliers[key].Value;
                }

                int vanillaStock = hq.GetUnitSupply(vanillaDef);
                int moddedStock = hq.GetUnitSupply(modded);

                int targetCap = Mathf.RoundToInt(vanillaStock * multiplier);
                int addedAmount = Mathf.RoundToInt(1.0f * multiplier);

                if (moddedStock < targetCap && addedAmount > 0)
                {
                    int finalAdd = Mathf.Min(addedAmount, targetCap - moddedStock);
                    if (EqualizerPlugin.VerboseLogging.Value)
                        Debug.Log($"[EqualizerMod] Ground delivery: adding {finalAdd}x {modded.unitName} to {hq.faction.factionName}");
                    hq.AddSupplyUnit(modded, finalAdd);
                }
            }
        }
    }
}
