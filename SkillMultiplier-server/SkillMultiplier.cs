using System.Reflection;
using SPTarkov.DI.Annotations;
using SPTarkov.Server.Core.DI;
using SPTarkov.Server.Core.Helpers;
using SPTarkov.Server.Core.Models.Spt.Config;
using SPTarkov.Server.Core.Models.Utils;
using SPTarkov.Server.Core.Servers;

namespace SkillMultiplier;

[Injectable(TypePriority = OnLoadOrder.PostSptModLoader)]
public class SkillMultiplier(
    ISptLogger<SkillMultiplier> logger,
    DatabaseServer databaseServer,
    ConfigServer configServer,
    ModHelper modHelper
) : IOnLoad
{
    public Task OnLoad()
    {
        var hideoutConfig = configServer.GetConfig<HideoutConfig>();
        var pathToMod = modHelper.GetAbsolutePathToModFolder(Assembly.GetExecutingAssembly());
        var config = modHelper.GetJsonDataFromFile<ModConfig>(pathToMod, "config.json");

        if (config.CraftingExpMultiplier == 1.0 && config.HideoutExpMultiplier == 1.0)
        {
            logger.Warning("[SkillMultiplier] Skill multiplier for Hideout Management and Crafting skill is disabled, please edit config.json in the mod folder to modify their multipliers.");
            return Task.CompletedTask;
        }
        var craftingExpAmount = hideoutConfig.CraftingExpAmount;
        var skillsSettings = databaseServer.GetTables().Globals.Configuration.SkillsSettings;
        var skillPointsPerCraft = skillsSettings.HideoutManagement.SkillPointsPerCraft;
        var skillPointsPerAreaUpgrade = skillsSettings.HideoutManagement.SkillPointsPerAreaUpgrade;

        var newCraftingExpAmount = craftingExpAmount * config.CraftingExpMultiplier;
        var newSkillPointsPerCraft = skillPointsPerCraft * config.HideoutExpMultiplier;
        var newSkillPointsPerAreaUpgrade = skillPointsPerAreaUpgrade * config.HideoutExpMultiplier;

        hideoutConfig.CraftingExpAmount = newCraftingExpAmount;

        skillsSettings.HideoutManagement.SkillPointsPerCraft = newSkillPointsPerCraft;
        skillsSettings.HideoutManagement.SkillPointsPerAreaUpgrade = newSkillPointsPerAreaUpgrade;
        logger.Success($"[SkillMultiplier] Configs Edited Successfully, new values: crafting exp amount: {newCraftingExpAmount}, SkillPointsPerCraft: {newSkillPointsPerCraft}, SkillPointsPerAreaUpgrade: {newSkillPointsPerAreaUpgrade}");

        return Task.CompletedTask;
    }
}


public record ModConfig
{
    public required double CraftingExpMultiplier { get; set; }
    public required double HideoutExpMultiplier { get; set; }
}
