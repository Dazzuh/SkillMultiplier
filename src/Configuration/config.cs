using System.Collections.Generic;
using BepInEx.Configuration;
using BepInEx.Logging;
using EFT.Communications;
using SPT.Reflection.Utils;
// ReSharper disable CanSimplifyDictionaryRemovingWithSingleCall

namespace SkillMultiplier.Configuration
{
    public class Config
    {
        private readonly ConfigFile _configFile;
        private static ManualLogSource Logger => SkillMultiplier.Logger;

        public ConfigEntry<bool> Debug { get; private set; }
        public ConfigEntry<bool> Enable { get; private set; }
        public ConfigEntry<bool> DisableFatigue { get; private set; }
        public ConfigEntry<bool> IncreaseLimits { get; private set; }
        public ConfigEntry<float> GlobalMultiplier { get; private set; }

        public List<string> SkillIds { get; private set; } = [];
        public readonly List<string> SkillIdsToExclude =
        [
            "BotReload",
            "BotSound"
        ];

        public readonly ConfigDefinition Ph;

        public Config(ConfigFile configFile)
        {
            Logger.LogInfo("Initializing configuration...");
            _configFile = configFile;
            Ph = new ConfigDefinition("Multipliers", "Loading...");

            Debug = _configFile.Bind("Debug", "Enabled", false, new ConfigDescription(
                "Enable or disable debug logging, may negatively impact performance, don't leave this enabled. Default is false (disabled).",
                null,
                new ConfigurationManagerAttributes { IsAdvanced = true }
            ));

            Enable = _configFile.Bind("General", "Enabled", true, new ConfigDescription(
                "Enable or disable the mod. Default is true (enabled).",
                null,
                new ConfigurationManagerAttributes { Order = 30 }
            ));

            DisableFatigue = _configFile.Bind("General", "Disable Fatigue", true, new ConfigDescription(
                "If enabled, fatigue will be disabled for all skills. Default is true (fatigue disabled).", null,
                new ConfigurationManagerAttributes { Order = 20 }
            ));

            IncreaseLimits = _configFile.Bind("General", "Increase Limits", false, new ConfigDescription(
                "If enabled, raises the range of skill multipliers to -1000 to 1000. REQUIRES RESTART. Default is false (no change).",
                null,
                new ConfigurationManagerAttributes { Order = 10, IsAdvanced = true }
            ));
            var range = GetGlobalMultiplierRange();
            var rangeText = IncreaseLimits.Value ? "-1000 to 1000" : "-100 to 100";
            GlobalMultiplier = _configFile.Bind(
                "General",
                "Global Multiplier",
                1f,
                new ConfigDescription(
                    $"Global multiplier for all skills. Range: {rangeText}. Default is 1 (no change).",
                    range,
                    new ConfigurationManagerAttributes { Order = 1 }
                )
            );

            IncreaseLimits.SettingChanged += (_, _) => OnLimitsChanged();

            _configFile.Bind(Ph, "Please wait, loading skills...", new ConfigDescription("This entry will be replaced with skill multipliers once loaded."));
            Logger.LogInfo("Configuration initialized.");
        }

        public void GenerateConfig()
        {
            if (_configFile.ContainsKey(Ph))
            {
                _configFile.Remove(Ph);
            }
            var session = ClientAppUtils.GetClientApp()?.GetClientBackEndSession();
            var profile = session!.Profile;
            profile.Skills.Skills.ExecuteForEach(skill =>
            {
                if (SkillIdsToExclude.Contains(skill.Id.ToString()))
                {
                    SkillMultiplier.LogDebug($"Skipping excluded skill: {skill.Id}");
                    return;
                }
                SkillIds.Add(skill.Id.ToString());
                SkillMultiplier.LogDebug($"Skill: {skill.Id} added to SkillIds list.");
            });

            SkillIds.ExecuteForEach(skillId =>
            {
                var range = GetSkillMultiplierRange();
                var rangeText = IncreaseLimits.Value ? "-1000 to 1000" : "-100 to 100";
                _configFile.Bind(
                    "Multipliers",
                    skillId,
                    1f,
                    new ConfigDescription(
                        $"Multiplier for skill {skillId}. Range: {rangeText}. Default is 1 (no change).",
                        range
                    )
                );
            });
            _configFile.Save();
        }

        public float GetMultiplier(string skillId)
        {
            if (string.IsNullOrEmpty(skillId))
            {
                Logger.LogWarning("Skill ID is null or empty, returning default multiplier of 1.");
                return 1; // Default multiplier if skillId is invalid
            }
            _configFile.TryGetEntry("Multipliers", skillId, out ConfigEntry<float> multiplierEntry);
            if (multiplierEntry != null)
            {
                float multiplier = multiplierEntry.Value;
                return multiplier;
            }

            Logger.LogWarning($"No multiplier found for skill {skillId}, returning default value of 1.");
            return 1; // Default multiplier if not found
        }

        private static void OnLimitsChanged()
        {
            NotificationManagerClass.DisplayMessageNotification("Game restart required to change limits.", ENotificationDurationType.Long);
        }

        private AcceptableValueRange<float> GetGlobalMultiplierRange()
        {
            return IncreaseLimits.Value ? new AcceptableValueRange<float>(-1000, 1000) : new AcceptableValueRange<float>(-100, 100);
        }

        private AcceptableValueRange<float> GetSkillMultiplierRange()
        {
            return IncreaseLimits.Value ? new AcceptableValueRange<float>(-1000, 1000) : new AcceptableValueRange<float>(-100, 100);
        }
    }
}
