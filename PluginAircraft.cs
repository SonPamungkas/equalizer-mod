using BepInEx;
using HarmonyLib;
using UnityEngine;
using System.Collections.Generic;
using System.Linq;

namespace EqualizerMod
{
    [BepInPlugin("com.equalizer.aircraft", "Equalizer Mod (Aircraft)", "1.0.0")]
    public class EqualizerPlugin : BaseUnityPlugin
    {
        public static EqualizerPlugin Instance;
        public static BepInEx.Configuration.ConfigEntry<bool> EqualizeEnabled;
        public static Dictionary<string, BepInEx.Configuration.ConfigEntry<bool>> AircraftToggles = new Dictionary<string, BepInEx.Configuration.ConfigEntry<bool>>();
        public static Dictionary<string, BepInEx.Configuration.ConfigEntry<int>> FactionRestrictions = new Dictionary<string, BepInEx.Configuration.ConfigEntry<int>>();
        public static Dictionary<string, BepInEx.Configuration.ConfigEntry<float>> EqualizeMultipliers = new Dictionary<string, BepInEx.Configuration.ConfigEntry<float>>();

        private bool _initialScanDone = false;

        private void Awake()
        {
            Instance = this;
            
            EqualizeEnabled = Config.Bind("General", "Equalize Enabled", true, "Global toggle for the aircraft equalization logic.");

            var harmony = new Harmony("com.equalizer.aircraft");
            harmony.PatchAll();
            Logger.LogInfo("Equalizer Mod (Aircraft) loaded!");
        }

        public bool IsFactionAllowed(AircraftDefinition ac, FactionHQ hq)
        {
            if (ac == null || hq == null || hq.faction == null) return false;
            string key = ac.jsonKey.ToLower();

            if (!AircraftToggles.ContainsKey(key))
            {
                IsAircraftEnabled(ac);
            }

            if (!AircraftToggles[key].Value) return false;

            int restriction = FactionRestrictions[key].Value;
            if (restriction == 0) return true;

            string factionName = hq.faction.factionName.ToLower();
            if (restriction == 1 && (factionName.Contains("primeva") || factionName.Contains("pala"))) return false;
            if (restriction == 2 && (factionName.Contains("boscali") || factionName.Contains("bdf"))) return false;

            return true;
        }

        private void Update()
        {
            if (!_initialScanDone && Time.time > 10f)
            {
                EqualizerLogic.ScanAircraft();
                _initialScanDone = true;
                Logger.LogInfo("Initial aircraft scan complete.");
            }
        }

        public bool IsAircraftEnabled(AircraftDefinition ac)
        {
            if (ac == null) return false;
            string key = ac.jsonKey.ToLower();

            if (EqualizerLogic.IsVanilla(ac)) return true;

            if (!AircraftToggles.ContainsKey(key))
            {
                Debug.Log($"[EqualizerMod] Binding new aircraft toggle for: {ac.unitName} ({ac.jsonKey})");
                AircraftToggles[key] = Config.Bind("Toggles - Aircraft", $"Equalize {ac.unitName}", true, $"Enable or disable equalization for {ac.unitName} ({ac.jsonKey}).");
                
                FactionRestrictions[key] = Config.Bind("Toggles - Faction Restriction", $"{ac.unitName} Restriction", 0, 
                    new BepInEx.Configuration.ConfigDescription($"Restriction for {ac.unitName}: 0=Both, 1=No PALA, 2=No BDF", 
                    new BepInEx.Configuration.AcceptableValueRange<int>(0, 2)));

                EqualizeMultipliers[key] = Config.Bind("Multipliers - Aircraft", $"{ac.unitName} Multiplier", 1.0f,
                    new BepInEx.Configuration.ConfigDescription($"Equalization multiplier for {ac.unitName} (0-10)",
                    new BepInEx.Configuration.AcceptableValueRange<float>(0f, 10f)));
            }

            return AircraftToggles[key].Value;
        }
    }

    [HarmonyPatch(typeof(FactionHQ), "OnMissionLoad")]
    public static class FactionHQ_OnMissionLoad_Patch
    {
        public static void Postfix(FactionHQ __instance)
        {
            EqualizerLogic.EqualizeInventory(__instance);
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

            _isEqualizing = true;
            try
            {
                EqualizerLogic.EqualizeProduction(__instance, aircraftDefinition, amount);
            }
            finally
            {
                _isEqualizing = false;
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
        public static Dictionary<int, AircraftTierInfo> TierInfoMap = new Dictionary<int, AircraftTierInfo>();

        private static readonly HashSet<string> VanillaKeys = new HashSet<string>
        {
            "coin", "trainer", "utilityhelo1", "attackhelo1", "cas1", "fighter1", 
            "smallfighter1", "quadvtol1", "multirole1", "ew1", "darkreach", "fastbomber1"
        };

        public static bool IsVanilla(AircraftDefinition ac)
        {
            if (ac == null) return false;
            return VanillaKeys.Contains(ac.jsonKey.ToLower());
        }

        public static void ScanAircraft()
        {
            Debug.Log("[EqualizerMod] Scanning for aircraft definitions...");
            TierInfoMap.Clear();

            var allAircraft = Resources.FindObjectsOfTypeAll<AircraftDefinition>();
            foreach (var ac in allAircraft)
            {
                if (ac.aircraftParameters == null) continue;

                int rank = ac.aircraftParameters.rankRequired;
                bool isModded = !IsVanilla(ac);

                if (!TierInfoMap.ContainsKey(rank))
                {
                    TierInfoMap[rank] = new AircraftTierInfo { Rank = rank };
                }

                if (isModded)
                {
                    TierInfoMap[rank].ModdedAircraft.Add(ac);
                    EqualizerPlugin.Instance.IsAircraftEnabled(ac);
                }
                else
                {
                    TierInfoMap[rank].VanillaAircraft.Add(ac);
                }
            }
        }

        public static void EqualizeInventory(FactionHQ hq)
        {
            if (EqualizerPlugin.EqualizeEnabled != null && !EqualizerPlugin.EqualizeEnabled.Value) return;
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
                    if (!EqualizerPlugin.Instance.IsFactionAllowed(modded, hq)) continue;

                    if (hq.restrictedAircraft != null && hq.restrictedAircraft.Contains(modded.jsonKey))
                        hq.restrictedAircraft.Remove(modded.jsonKey);

                    string modKey = modded.jsonKey.ToLower();
                    float multiplier = 1.0f;
                    if (EqualizerPlugin.EqualizeMultipliers.ContainsKey(modKey))
                    {
                        multiplier = EqualizerPlugin.EqualizeMultipliers[modKey].Value;
                    }

                    int targetCount = Mathf.RoundToInt(minVanillaCount * multiplier);
                    int currentCount = hq.GetUnitSupply(modded);
                    if (currentCount < targetCount)
                        hq.AddSupplyUnit(modded, targetCount - currentCount);
                }
            }
        }

        public static void EqualizeProduction(FactionHQ hq, AircraftDefinition aircraftDefinition, int amount)
        {
            if (EqualizerPlugin.EqualizeEnabled != null && !EqualizerPlugin.EqualizeEnabled.Value) return;
            if (hq == null || aircraftDefinition == null || aircraftDefinition.aircraftParameters == null) return;
            if (TierInfoMap.Count == 0) ScanAircraft();

            string key = aircraftDefinition.jsonKey.ToLower();
            if (!VanillaKeys.Contains(key)) return;

            int rank = aircraftDefinition.aircraftParameters.rankRequired;
            if (!TierInfoMap.ContainsKey(rank)) return;

            var tier = TierInfoMap[rank];
            foreach (var modded in tier.ModdedAircraft)
            {
                if (!EqualizerPlugin.Instance.IsFactionAllowed(modded, hq)) continue;

                if (hq.restrictedAircraft != null && hq.restrictedAircraft.Contains(modded.jsonKey))
                    hq.restrictedAircraft.Remove(modded.jsonKey);

                string modKey = modded.jsonKey.ToLower();
                float multiplier = 1.0f;
                if (EqualizerPlugin.EqualizeMultipliers.ContainsKey(modKey))
                {
                    multiplier = EqualizerPlugin.EqualizeMultipliers[modKey].Value;
                }

                int addAmount = Mathf.RoundToInt(amount * multiplier);
                if (addAmount > 0)
                {
                    hq.AddSupplyUnit(modded, addAmount);
                }
            }
        }
    }
}
