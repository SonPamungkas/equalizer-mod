using BepInEx;
using HarmonyLib;
using UnityEngine;
using System.Collections.Generic;
using System.Linq;

namespace EqualizerMod
{
    [BepInPlugin("com.raksaputra.equalizermod", "Equalizer Mod", "1.0.0")]
    public class EqualizerPlugin : BaseUnityPlugin
    {
        public static EqualizerPlugin Instance;
        public static BepInEx.Configuration.ConfigEntry<bool> EqualizeEnabled;
        public static Dictionary<string, BepInEx.Configuration.ConfigEntry<bool>> AircraftToggles = new Dictionary<string, BepInEx.Configuration.ConfigEntry<bool>>();

        private bool _initialScanDone = false;

        private void Awake()
        {
            Instance = this;
            
            EqualizeEnabled = Config.Bind("General", "Equalize Enabled", true, "Global toggle for the equalization logic.");

            var harmony = new Harmony("com.raksaputra.equalizermod");
            harmony.PatchAll();
            Logger.LogInfo("Equalizer Mod loaded!");
        }

        private void Update()
        {
            // Perform an initial scan after a few seconds to populate the config menu with modded aircraft
            if (!_initialScanDone && Time.time > 10f)
            {
                EqualizerLogic.ScanAircraft();
                _initialScanDone = true;
                Logger.LogInfo("Initial aircraft scan complete. Configuration toggles should be available.");
            }
        }

        public bool IsAircraftEnabled(AircraftDefinition ac)
        {
            if (ac == null) return false;
            string key = ac.jsonKey.ToLower();

            // Toggle per modded aircraft. Vanilla are always "enabled" for comparison.
            if (EqualizerLogic.IsVanilla(ac)) return true;

            if (!AircraftToggles.ContainsKey(key))
            {
                Debug.Log($"[EqualizerMod] Binding new config toggle for: {ac.unitName} ({ac.jsonKey})");
                AircraftToggles[key] = Config.Bind("Toggles - Aircraft", $"Equalize {ac.unitName}", true, $"Enable or disable equalization for {ac.unitName} ({ac.jsonKey}).");
            }

            return AircraftToggles[key].Value;
        }
    }

    [HarmonyPatch(typeof(FactionHQ), "OnMissionLoad")]
    public static class FactionHQ_OnMissionLoad_Patch
    {
        public static void Postfix(FactionHQ __instance)
        {
            // This is called when a mission is loaded for each HQ.
            EqualizerLogic.EqualizeInventory(__instance);
        }
    }

    [HarmonyPatch(typeof(FactionHQ), "AddSupplyUnit")]
    public static class FactionHQ_AddSupplyUnit_Patch
    {
        private static bool _isEqualizing = false;

        public static void Postfix(FactionHQ __instance, UnitDefinition unitDefinition, int amount)
        {
            // Avoid infinite recursion and only process positive additions
            if (_isEqualizing || amount <= 0) return;

            // We only care about aircraft
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
        // Stored information about aircraft tiers
        public static Dictionary<int, AircraftTierInfo> TierInfoMap = new Dictionary<int, AircraftTierInfo>();

        // Vanilla aircraft jsonKeys (to distinguish from modded)
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
                    Debug.Log($"[EqualizerMod] Found Modded: {ac.unitName} (Key: {ac.jsonKey}, Rank: {rank})");
                    
                    // Register the toggle in ConfigurationManager
                    EqualizerPlugin.Instance.IsAircraftEnabled(ac);
                }
                else
                {
                    TierInfoMap[rank].VanillaAircraft.Add(ac);
                    Debug.Log($"[EqualizerMod] Found Vanilla: {ac.unitName} (Key: {ac.jsonKey}, Rank: {rank})");
                }
            }

            foreach (var tier in TierInfoMap.Values)
            {
                Debug.Log($"[EqualizerMod] Summary Rank {tier.Rank}: {tier.VanillaAircraft.Count} vanilla, {tier.ModdedAircraft.Count} modded.");
            }
        }

        public static void EqualizeInventory(FactionHQ hq)
        {
            if (EqualizerPlugin.EqualizeEnabled != null && !EqualizerPlugin.EqualizeEnabled.Value) return;

            if (hq == null || hq.faction == null) return;

            // Ensure we have scanned the aircraft
            if (TierInfoMap.Count == 0)
            {
                ScanAircraft();
            }

            Debug.Log($"[EqualizerMod] Equalizing inventory for faction: {hq.faction.factionName}");

            foreach (var tier in TierInfoMap.Values)
            {
                if (tier.ModdedAircraft.Count == 0) continue;
                
                // Find smallest amount of stored vanilla airframes of the same tier (rank)
                int minVanillaCount = int.MaxValue;
                bool foundVanillaInInventory = false;

                foreach (var vanilla in tier.VanillaAircraft)
                {
                    int count = hq.GetUnitSupply(vanilla);
                    Debug.Log($"[EqualizerMod] Vanilla {vanilla.unitName} (Rank {tier.Rank}) inventory: {count}");
                    
                    if (count < minVanillaCount)
                        minVanillaCount = count;
                    
                    foundVanillaInInventory = true;
                }

                if (!foundVanillaInInventory)
                {
                    Debug.Log($"[EqualizerMod] No vanilla aircraft of Rank {tier.Rank} found in {hq.faction.factionName} inventory.");
                    continue;
                }

                if (minVanillaCount == int.MaxValue) minVanillaCount = 0;

                Debug.Log($"[EqualizerMod] Target count for Rank {tier.Rank} modded aircraft: {minVanillaCount}");

                // Equalize modded aircraft
                foreach (var modded in tier.ModdedAircraft)
                {
                    // Check individual aircraft toggle
                    if (!EqualizerPlugin.Instance.IsAircraftEnabled(modded))
                    {
                        Debug.Log($"[EqualizerMod] {modded.unitName} is blacklisted (disabled in config). Skipping.");
                        continue;
                    }

                    // If the modded aircraft is in the restricted list, remove it
                    if (hq.restrictedAircraft != null && hq.restrictedAircraft.Contains(modded.jsonKey))
                    {
                        Debug.Log($"[EqualizerMod] Unrestricting modded aircraft: {modded.unitName} ({modded.jsonKey})");
                        hq.restrictedAircraft.Remove(modded.jsonKey);
                    }

                    int currentModdedCount = hq.GetUnitSupply(modded);
                    
                    if (currentModdedCount < minVanillaCount)
                    {
                        int needed = minVanillaCount - currentModdedCount;
                        Debug.Log($"[EqualizerMod] Equalizing {modded.unitName} (Rank {tier.Rank}): adding {needed} airframes (target: {minVanillaCount})");
                        hq.AddSupplyUnit(modded, needed);
                    }
                    else
                    {
                        Debug.Log($"[EqualizerMod] Modded {modded.unitName} already has enough airframes ({currentModdedCount} >= {minVanillaCount})");
                    }
                }
            }
        }

        public static void EqualizeProduction(FactionHQ hq, AircraftDefinition aircraftDefinition, int amount)
        {
            if (EqualizerPlugin.EqualizeEnabled != null && !EqualizerPlugin.EqualizeEnabled.Value) return;

            if (hq == null || aircraftDefinition == null || aircraftDefinition.aircraftParameters == null) return;

            // Ensure we have scanned the aircraft
            if (TierInfoMap.Count == 0)
            {
                ScanAircraft();
            }

            // Check if the aircraft that received delivery is vanilla
            string key = aircraftDefinition.jsonKey.ToLower();
            if (!VanillaKeys.Contains(key)) return;

            int rank = aircraftDefinition.aircraftParameters.rankRequired;
            if (!TierInfoMap.ContainsKey(rank)) return;

            var tier = TierInfoMap[rank];
            if (tier.ModdedAircraft.Count == 0) return;

            Debug.Log($"[EqualizerMod] Vanilla delivery detected: {aircraftDefinition.unitName} (+{amount}). Equalizing Rank {rank} modded aircraft production.");

            foreach (var modded in tier.ModdedAircraft)
            {
                // Check individual aircraft toggle
                if (!EqualizerPlugin.Instance.IsAircraftEnabled(modded))
                {
                    continue;
                }

                // Unrestrict if necessary (re-applying the fix from inventory equalization)
                if (hq.restrictedAircraft != null && hq.restrictedAircraft.Contains(modded.jsonKey))
                {
                    hq.restrictedAircraft.Remove(modded.jsonKey);
                }

                Debug.Log($"[EqualizerMod] Delivering modded aircraft: {modded.unitName} (+{amount})");
                hq.AddSupplyUnit(modded, amount);
            }
        }
    }
}
