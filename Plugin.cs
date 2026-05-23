using BepInEx;
using HarmonyLib;
using UnityEngine;
using System.Collections;
using System.Collections.Generic;
using System.Linq;

namespace EqualizerGroundMod
{
    [BepInPlugin("com.equalizer.ground", "Equalizer Mod (Ground Vehicles)", "1.0.0")]
    public class EqualizerGroundPlugin : BaseUnityPlugin
    {
        public static EqualizerGroundPlugin Instance;
        public static BepInEx.Configuration.ConfigEntry<bool> EqualizeEnabled;
        public static BepInEx.Configuration.ConfigEntry<bool> VerboseLogging;
        public static Dictionary<string, BepInEx.Configuration.ConfigEntry<int>> FactionRestrictions = new Dictionary<string, BepInEx.Configuration.ConfigEntry<int>>();
        public static Dictionary<string, BepInEx.Configuration.ConfigEntry<float>> VehicleMultipliers = new Dictionary<string, BepInEx.Configuration.ConfigEntry<float>>();
        public static Dictionary<string, BepInEx.Configuration.ConfigEntry<string>> LinkedVanillaUnits = new Dictionary<string, BepInEx.Configuration.ConfigEntry<string>>();

        private bool _initialScanDone = false;

        private void Awake()
        {
            Instance = this;
            
            EqualizeEnabled = Config.Bind("General", "Equalize Enabled", true, "Global toggle for the ground vehicle equalization logic.");
            VerboseLogging = Config.Bind("General", "Verbose Logging", false, "Enable verbose logging for deliveries and spawns.");

            var harmony = new Harmony("com.equalizer.ground");
            harmony.PatchAll();
            Logger.LogInfo("Equalizer Mod (Ground Vehicles) loaded!");
        }

        private void Update()
        {
            if (!_initialScanDone && Time.time > 10f)
            {
                EqualizerGround.ScanVehicles();
                _initialScanDone = true;
                Logger.LogInfo("Initial vehicle scan complete.");
            }
        }

        public void InitializeVehicleConfig(VehicleDefinition vd)
        {
            if (vd == null) return;
            string key = vd.jsonKey.ToLower();

            if (EqualizerGround.IsVanilla(vd)) return;

            if (!LinkedVanillaUnits.ContainsKey(key))
            {
                Debug.Log($"[EqualizerGround] Binding new vehicle config for: {vd.unitName} ({vd.jsonKey})");
                string pName = vd.unitPrefab != null ? vd.unitPrefab.name : vd.name;
                string dispName = string.IsNullOrEmpty(vd.unitName) ? pName : $"{vd.unitName} ({pName})";
                
                var acceptableValues = new BepInEx.Configuration.AcceptableValueList<string>(EqualizerGround.VanillaVehicleNames.ToArray());
                LinkedVanillaUnits[key] = Config.Bind("1 - Unit Link", $"{dispName} Linked Vanilla Unit", "None",
                    new BepInEx.Configuration.ConfigDescription($"Vanilla vehicle to link production with.", acceptableValues));

                VehicleMultipliers[key] = Config.Bind("2 - Multipliers", $"{dispName} Multiplier", 1.0f,
                    new BepInEx.Configuration.ConfigDescription($"Equalization multiplier for {dispName} (0-10)",
                    new BepInEx.Configuration.AcceptableValueRange<float>(0f, 10f)));

                FactionRestrictions[key] = Config.Bind("3 - Faction Restriction", $"{dispName} Restriction", 0, 
                    new BepInEx.Configuration.ConfigDescription($"Restriction for {dispName}: 0=Both, 1=No PALA, 2=No BDF", 
                    new BepInEx.Configuration.AcceptableValueRange<int>(0, 2)));
            }
        }

        public bool IsFactionAllowed(VehicleDefinition vd, FactionHQ hq)
        {
            if (vd == null || hq == null || hq.faction == null) return false;
            string key = vd.jsonKey.ToLower();

            if (EqualizerGround.IsVanilla(vd)) return true;

            if (!LinkedVanillaUnits.ContainsKey(key) || LinkedVanillaUnits[key].Value == "None") return false;

            int restriction = FactionRestrictions[key].Value;
            if (restriction == 0) return true;

            string factionName = hq.faction.factionName.ToLower();
            if (restriction == 1 && (factionName.Contains("primeva") || factionName.Contains("pala"))) return false;
            if (restriction == 2 && (factionName.Contains("boscali") || factionName.Contains("bdf"))) return false;

            return true;
        }
    }

    public static class EqualizerGround
    {
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

        public static bool IsVanilla(VehicleDefinition vd)
        {
            if (vd == null) return false;
            string prefabName = vd.unitPrefab != null ? vd.unitPrefab.name.ToLower() : vd.jsonKey.ToLower();
            return VanillaVehicleKeys.Contains(prefabName);
        }

        public static string GetVehicleDisplayName(VehicleDefinition vd)
        {
            if (vd == null) return "None";
            string pName = vd.unitPrefab != null ? vd.unitPrefab.name : vd.name;
            return string.IsNullOrEmpty(vd.unitName) ? pName : $"{vd.unitName} ({pName})";
        }

        public static void ScanVehicles()
        {
            Debug.Log("[EqualizerGround] Scanning for ground vehicle definitions...");

            var allVehicles = Resources.FindObjectsOfTypeAll<VehicleDefinition>();
            var vanillaVehicles = allVehicles.Where(v => IsVanilla(v)).ToList();
            ModdedVehiclesList = allVehicles.Where(v => !IsVanilla(v)).ToList();

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
                // Trigger config generation for modded vehicles
                EqualizerGroundPlugin.Instance.InitializeVehicleConfig(modded);
            }
        }

        public static void HandleProduction(Factory factory)
        {
            if (factory == null || factory.ProductionUnit == null) return;
            if (!(factory.ProductionUnit is VehicleDefinition vanillaDef)) return;
            if (!IsVanilla(vanillaDef)) return;

            string vanillaDispName = GetVehicleDisplayName(vanillaDef);

            FactionHQ hq = null;
            if (factory.attachedUnit != null) hq = factory.attachedUnit.NetworkHQ;
            if (hq == null) return;

            foreach (var modded in ModdedVehiclesList)
            {
                string key = ModdedKeys[modded];
                if (!EqualizerGroundPlugin.LinkedVanillaUnits.ContainsKey(key)) continue;
                
                string linkedVanillaName = EqualizerGroundPlugin.LinkedVanillaUnits[key].Value;
                if (linkedVanillaName != vanillaDispName) continue;

                if (!EqualizerGroundPlugin.Instance.IsFactionAllowed(modded, hq)) continue;

                float multiplier = 1.0f;
                if (EqualizerGroundPlugin.VehicleMultipliers.ContainsKey(key))
                {
                    multiplier = EqualizerGroundPlugin.VehicleMultipliers[key].Value;
                }

                int vanillaStock = hq.GetUnitSupply(vanillaDef);
                int moddedStock = hq.GetUnitSupply(modded);

                int targetCap = Mathf.RoundToInt(vanillaStock * multiplier);
                int addedAmount = Mathf.RoundToInt(1.0f * multiplier);

                if (moddedStock < targetCap && addedAmount > 0)
                {
                    int finalAdd = Mathf.Min(addedAmount, targetCap - moddedStock);
                    if (EqualizerGroundPlugin.VerboseLogging.Value)
                        Debug.Log($"[EqualizerGround] Ground delivery: adding {finalAdd}x {modded.unitName} to {hq.faction.factionName}");
                    hq.AddSupplyUnit(modded, finalAdd);
                }
            }
        }

        public static void EqualizeInventory(FactionHQ hq)
        {
            if (VanillaVehicleNames.Count <= 1) ScanVehicles();

            foreach (var modded in ModdedVehiclesList)
            {
                if (!EqualizerGroundPlugin.Instance.IsFactionAllowed(modded, hq)) continue;

                string key = ModdedKeys[modded];
                if (!EqualizerGroundPlugin.LinkedVanillaUnits.ContainsKey(key)) continue;

                string linkedVanillaName = EqualizerGroundPlugin.LinkedVanillaUnits[key].Value;
                if (linkedVanillaName == "None" || !VanillaVehicleDict.ContainsKey(linkedVanillaName)) continue;

                VehicleDefinition vanillaDef = VanillaVehicleDict[linkedVanillaName];
                int vanillaStock = hq.GetUnitSupply(vanillaDef);
                if (vanillaStock <= 0) continue;

                float multiplier = 1.0f;
                if (EqualizerGroundPlugin.VehicleMultipliers.ContainsKey(key))
                {
                    multiplier = EqualizerGroundPlugin.VehicleMultipliers[key].Value;
                }

                int targetCap = Mathf.RoundToInt(vanillaStock * multiplier);
                int moddedStock = hq.GetUnitSupply(modded);
                if (moddedStock < targetCap)
                {
                    int addAmount = targetCap - moddedStock;
                    if (EqualizerGroundPlugin.VerboseLogging.Value)
                        Debug.Log($"[EqualizerGround] Equalized Initial Supply: Adding {addAmount}x {modded.unitName} to {hq.faction.factionName} (Linked to {vanillaDef.unitName})");
                    hq.AddSupplyUnit(modded, addAmount);
                }
            }
        }
    }

    [HarmonyPatch(typeof(FactionHQ), "OnMissionLoad")]
    public static class FactionHQ_OnMissionLoad_Patch
    {
        public static void Postfix(FactionHQ __instance)
        {
            EqualizerGround.EqualizeInventory(__instance);
        }
    }

    [HarmonyPatch(typeof(Factory), "ProduceUnit")]
    public static class Factory_ProduceUnit_Patch
    {
        public static void Postfix(Factory __instance)
        {
            if (EqualizerGroundPlugin.EqualizeEnabled != null && !EqualizerGroundPlugin.EqualizeEnabled.Value) return;
            EqualizerGround.HandleProduction(__instance);
        }
    }

    [HarmonyPatch(typeof(VehicleDepot), "TrySpawnVehicle")]
    public static class VehicleDepot_TrySpawnVehicle_Patch
    {
        private static readonly AccessTools.FieldRef<VehicleDepot, float> LastSpawnedTimeRef =
            AccessTools.FieldRefAccess<VehicleDepot, float>("lastSpawnedTime");

        public static void Postfix(VehicleDepot __instance, VehicleDefinition vehicleDefinition, bool __result)
        {
            if (!__result) return;
            if (EqualizerGroundPlugin.EqualizeEnabled != null && !EqualizerGroundPlugin.EqualizeEnabled.Value) return;
            if (!EqualizerGround.IsVanilla(vehicleDefinition)) return;

            string vanillaDispName = EqualizerGround.GetVehicleDisplayName(vehicleDefinition);

            FactionHQ hq = __instance.NetworkHQ;
            if (hq == null) return;

            int delayIndex = 1;
            foreach (var modded in EqualizerGround.ModdedVehiclesList)
            {
                string key = EqualizerGround.ModdedKeys[modded];
                if (!EqualizerGroundPlugin.LinkedVanillaUnits.ContainsKey(key)) continue;

                string linkedVanillaName = EqualizerGroundPlugin.LinkedVanillaUnits[key].Value;
                if (linkedVanillaName != vanillaDispName) continue;

                if (!EqualizerGroundPlugin.Instance.IsFactionAllowed(modded, hq)) continue;

                int moddedStock = hq.GetUnitSupply(modded);
                if (moddedStock > 0)
                {
                    EqualizerGroundPlugin.Instance.StartCoroutine(SpawnModdedWithDelay(__instance, modded, vehicleDefinition, 1.5f * delayIndex));
                    delayIndex++;
                }
            }
        }

        private static IEnumerator SpawnModdedWithDelay(VehicleDepot depot, VehicleDefinition modded, VehicleDefinition vanilla, float delay)
        {
            yield return new WaitForSeconds(delay);
            if (depot == null || modded == null) yield break;

            LastSpawnedTimeRef(depot) = -9999f;
            bool spawned = depot.TrySpawnVehicle(modded);
            if (spawned)
            {
                LastSpawnedTimeRef(depot) = Time.time;
                if (EqualizerGroundPlugin.VerboseLogging.Value)
                {
                    Debug.Log($"[EqualizerGround] Linked spawn: {modded.unitName} physically spawned alongside {vanilla.unitName}");
                }
            }
        }
    }
}
