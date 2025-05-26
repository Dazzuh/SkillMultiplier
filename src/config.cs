using BepInEx.Configuration;
using BepInEx.Logging;
using SPT.Reflection.Utils;
using System.Collections.Generic;

namespace SkillMultiplier.Configuration
{
    public class Config
    {
        private readonly ConfigFile _configFile;
        private static SkillMultiplier _plugin => SkillMultiplier.Instance;
        private static ManualLogSource _logger => SkillMultiplier.Logger;

        public ConfigEntry<bool> Debug { get; private set; }
        public ConfigEntry<bool> Enable { get; private set; }
        public ConfigEntry<bool> DisableFatigue { get; private set; }
        public ConfigEntry<float> GlobalMultiplier { get; private set; }

        public List<string> SkillIds { get; private set; } = new List<string>();
        public List<string> SkillIdsToExclude = new List<string>
        {
            "BotReload",
            "BotSound"
        };

        public ConfigDefinition ph;

        public Config(ConfigFile configFile)
        {
            _logger.LogInfo("Initializing configuration...");
            _configFile = configFile;

            Debug = _configFile.Bind("Debug", "Enabled", false, new ConfigDescription(
                "Enable or disable debug logging. Default is false (disabled).",
                null,
                new ConfigurationManagerAttributes { IsAdvanced = true }
            ));

            Enable = _configFile.Bind("General", "Enabled", true, new ConfigDescription(
                "Enable or disable the mod. Default is true (enabled).",
                null,
                new ConfigurationManagerAttributes { Order = 3 }
            ));

            DisableFatigue = _configFile.Bind("General", "Disable Fatigue", true, new ConfigDescription(
                "If enabled, fatigue will be disabled for all skills. Default is true (fatigue disabled).", null,
                new ConfigurationManagerAttributes { Order = 2 }
            ));

            GlobalMultiplier = _configFile.Bind(
                "General",
                "Global Multiplier",
                1f,
                new ConfigDescription(
                    "Global multiplier for all skills. Range: -100 to 100. Default is 1 (no change).",
                    new AcceptableValueRange<float>(-100, 100),
                    new ConfigurationManagerAttributes { Order = 1 }
                )
            );

            ph = new ConfigDefinition("Multipliers", "Loading...");

            _configFile.Bind(ph, "Please wait, loading skills...", new ConfigDescription("This entry will be replaced with skill multipliers once loaded."));
            _logger.LogInfo("Configuration initialized.");
        }

        public void GenerateConfig()
        {
            if (_configFile.ContainsKey(ph))
            {
                _configFile.Remove(ph);
            }
            var session = ClientAppUtils.GetClientApp()?.GetClientBackEndSession();
            var profile = session.Profile;
            profile.Skills.Skills.ExecuteForEach(skill =>
            {
                if (SkillIdsToExclude.Contains(skill.Id.ToString()))
                {
                    _plugin.logDebug($"Skipping excluded skill: {skill.Id}");
                    return;
                }
                SkillIds.Add(skill.Id.ToString());
                _plugin.logDebug($"Skill: {skill.Id} added to SkillIds list.");
            });

            SkillIds.ExecuteForEach(SkillId =>
            {
                _configFile.Bind(
                "Multipliers",
                SkillId,
                1f,
                new ConfigDescription(
                    $"Multiplier for skill {SkillId}. Range: -100 to 100. Default is 1 (no change).",
                    new AcceptableValueRange<float>(-100, 100)
                )
            );
            });
            _configFile.Save();
        }

        public float GetMultiplier(string skillId)
        {
            if (string.IsNullOrEmpty(skillId))
            {
                _logger.LogWarning("Skill ID is null or empty, returning default multiplier of 1.");
                return 1; // Default multiplier if skillId is invalid
            }
            _configFile.TryGetEntry("Multipliers", skillId, out ConfigEntry<float> multiplierEntry);
            if (multiplierEntry != null)
            {
                float multiplier = multiplierEntry.Value;
                return multiplier;
            }
            else
            {
                _logger.LogWarning($"No multiplier found for skill {skillId}, returning default value of 1.");
                return 1; // Default multiplier if not found
            }
        }
    }
}
