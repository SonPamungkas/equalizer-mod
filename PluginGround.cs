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
        public static BepInEx.Configuration.ConfigEntry<float> GroundDelayMultiplier;
        public static Dictionary<string, BepInEx.Configuration.ConfigEntry<bool>> VehicleToggles = new Dictionary<string, BepInEx.Configuration.ConfigEntry<bool>>();
        public static Dictionary<string, BepInEx.Configuration.ConfigEntry<int>> FactionRestrictions = new Dictionary<string, BepInEx.Configuration.ConfigEntry<int>>();
        public static Dictionary<string, BepInEx.Configuration.ConfigEntry<float>> VehicleMultipliers = new Dictionary<string, BepInEx.Configuration.ConfigEntry<float>>();

        private bool _initialScanDone = false;

        private void Awake()
        {
            Instance = this;
            
            EqualizeEnabled = Config.Bind("General", "Equalize Enabled", true, "Global toggle for the ground vehicle equalization logic.");
            GroundDelayMultiplier = Config.Bind("Ground Equalizer", "Production Delay Multiplier", 0.5f, "Delay added to modded ground vehicle production, as a multiplier of the factory's production interval (e.g. 0.5 = half the factory speed).");

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

        public bool IsVehicleEnabled(VehicleDefinition vd)
        {
            if (vd == null) return false;
            string key = vd.jsonKey.ToLower();

            if (EqualizerGround.IsVanilla(vd)) return true;

            if (!VehicleToggles.ContainsKey(key))
            {
                Debug.Log($"[EqualizerGround] Binding new vehicle toggle for: {vd.unitName} ({vd.jsonKey})");
                VehicleToggles[key] = Config.Bind("Toggles - Ground Vehicles", $"Equalize {vd.unitName}", true, $"Enable or disable equalization for {vd.unitName} ({vd.jsonKey}).");
                
                FactionRestrictions[key] = Config.Bind("Toggles - Ground Faction Restriction", $"{vd.unitName} Restriction", 0, 
                    new BepInEx.Configuration.ConfigDescription($"Restriction for {vd.unitName}: 0=Both, 1=No PALA, 2=No BDF", 
                    new BepInEx.Configuration.AcceptableValueRange<int>(0, 2)));

                VehicleMultipliers[key] = Config.Bind("Multipliers - Ground Vehicles", $"{vd.unitName} Multiplier", 1.0f,
                    new BepInEx.Configuration.ConfigDescription($"Equalization multiplier for {vd.unitName} (0-10)",
                    new BepInEx.Configuration.AcceptableValueRange<float>(0f, 10f)));
            }

            return VehicleToggles[key].Value;
        }

        public bool IsFactionAllowed(VehicleDefinition vd, FactionHQ hq)
        {
            if (vd == null || hq == null || hq.faction == null) return false;
            string key = vd.jsonKey.ToLower();

            if (!IsVehicleEnabled(vd)) return false;

            int restriction = FactionRestrictions[key].Value;
            if (restriction == 0) return true;

            string factionName = hq.faction.factionName.ToLower();
            if (restriction == 1 && (factionName.Contains("primeva") || factionName.Contains("pala"))) return false;
            if (restriction == 2 && (factionName.Contains("boscali") || factionName.Contains("bdf"))) return false;

            return true;
        }
    }

    public class VehiclePriceGroup
    {
        public VehicleDefinition VanillaUnit;
        public List<VehicleDefinition> ModdedVehicles = new List<VehicleDefinition>();
    }

    public static class EqualizerGround
    {
        public static List<VehiclePriceGroup> PriceGroups = new List<VehiclePriceGroup>();

        private static readonly HashSet<string> VanillaVehicleKeys = new HashSet<string>
        {
            "truck", "tank", "apc", "mobile_sam", "mobile_aaa", "mobile_radar",
            "jeep", "ifv", "scout_car"
        };

        public static bool IsVanilla(VehicleDefinition vd)
        {
            if (vd == null) return false;
            string key = vd.jsonKey.ToLower();
            return VanillaVehicleKeys.Contains(key) || (key.Length < 15 && !key.Contains("_") && !key.Contains("."));
        }

        public static void ScanVehicles()
        {
            Debug.Log("[EqualizerGround] Scanning for ground vehicle definitions...");
            PriceGroups.Clear();

            var allVehicles = Resources.FindObjectsOfTypeAll<VehicleDefinition>();
            var vanillaVehicles = allVehicles.Where(v => IsVanilla(v)).ToList();
            var moddedVehicles = allVehicles.Where(v => !IsVanilla(v)).ToList();

            foreach (var vanilla in vanillaVehicles)
            {
                var group = new VehiclePriceGroup { VanillaUnit = vanilla };
                float minPrice = vanilla.value * 0.8f;
                float maxPrice = vanilla.value * 1.2f;

                foreach (var modded in moddedVehicles)
                {
                    if (modded.value >= minPrice && modded.value <= maxPrice)
                    {
                        group.ModdedVehicles.Add(modded);
                        EqualizerGroundPlugin.Instance.IsVehicleEnabled(modded);
                    }
                }
                
                if (group.ModdedVehicles.Count > 0)
                    PriceGroups.Add(group);
            }
        }

        public static void HandleProduction(Factory factory)
        {
            if (factory == null || factory.ProductionUnit == null) return;
            if (!(factory.ProductionUnit is VehicleDefinition vanillaDef)) return;
            if (!IsVanilla(vanillaDef)) return;

            var matchingGroup = PriceGroups.FirstOrDefault(g => g.VanillaUnit == vanillaDef);
            if (matchingGroup == null) return;

            FactionHQ hq = null;
            if (factory.attachedUnit != null) hq = factory.attachedUnit.NetworkHQ;
            if (hq == null) return;

            float interval = factory.ProductionInterval;
            float delay = interval * EqualizerGroundPlugin.GroundDelayMultiplier.Value;

            foreach (var modded in matchingGroup.ModdedVehicles)
            {
                if (!EqualizerGroundPlugin.Instance.IsFactionAllowed(modded, hq)) continue;
                EqualizerGroundPlugin.Instance.StartCoroutine(DelayedDelivery(hq, modded, vanillaDef, delay));
            }
        }

        private static IEnumerator DelayedDelivery(FactionHQ hq, VehicleDefinition modded, VehicleDefinition vanilla, float delay)
        {
            if (delay > 0)
                yield return new WaitForSeconds(delay);

            if (hq == null || modded == null || vanilla == null) yield break;

            string modKey = modded.jsonKey.ToLower();
            float multiplier = 1.0f;
            if (EqualizerGroundPlugin.VehicleMultipliers.ContainsKey(modKey))
            {
                multiplier = EqualizerGroundPlugin.VehicleMultipliers[modKey].Value;
            }

            int vanillaStock = hq.GetUnitSupply(vanilla);
            int moddedStock = hq.GetUnitSupply(modded);

            int targetCap = Mathf.RoundToInt(vanillaStock * multiplier);
            int addedAmount = Mathf.RoundToInt(1.0f * multiplier);

            if (moddedStock < targetCap && addedAmount > 0)
            {
                int finalAdd = Mathf.Min(addedAmount, targetCap - moddedStock);
                Debug.Log($"[EqualizerGround] Ground delivery: adding {finalAdd}x {modded.unitName} to {hq.faction.factionName}");
                hq.AddSupplyUnit(modded, finalAdd);
            }
        }

        public static void EqualizeInventory(FactionHQ hq)
        {
            if (PriceGroups.Count == 0) ScanVehicles();

            foreach (var group in PriceGroups)
            {
                int vanillaStock = hq.GetUnitSupply(group.VanillaUnit);
                if (vanillaStock <= 0) continue;

                foreach (var modded in group.ModdedVehicles)
                {
                    if (!EqualizerGroundPlugin.Instance.IsFactionAllowed(modded, hq)) continue;

                    string modKey = modded.jsonKey.ToLower();
                    float multiplier = 1.0f;
                    if (EqualizerGroundPlugin.VehicleMultipliers.ContainsKey(modKey))
                    {
                        multiplier = EqualizerGroundPlugin.VehicleMultipliers[modKey].Value;
                    }

                    int targetCap = Mathf.RoundToInt(vanillaStock * multiplier);
                    int moddedStock = hq.GetUnitSupply(modded);
                    if (moddedStock < targetCap)
                    {
                        hq.AddSupplyUnit(modded, targetCap - moddedStock);
                    }
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
}
