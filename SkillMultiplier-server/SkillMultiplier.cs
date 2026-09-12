using System.Reflection;
using SPTarkov.Common.Models.Logging;
using SPTarkov.DI.Annotations;
using SPTarkov.Server.Core.DI;
using SPTarkov.Server.Core.Helpers.Server;
using SPTarkov.Server.Core.Models.Spt.Config;
using SPTarkov.Server.Core.Models.Spt.Tables;

namespace SkillMultiplier;

[Injectable(TypePriority = OnLoadOrder.PostLoad + 1)]
public class SkillMultiplier(
    HideoutConfig hideoutConfig,
    ISptLogger<SkillMultiplier> logger,
    GlobalTable globalTable,
    ModHelper modHelper
) : IOnLoad
{
    public Task OnLoadAsync(CancellationToken cancellationToken)
    {
        var pathToMod = modHelper.GetAbsolutePathToModFolder(Assembly.GetExecutingAssembly());
        var config = modHelper.GetJsonDataFromFile<ModConfig>(pathToMod, "config.json");

        if (config.CraftingExpMultiplier == 1.0 && config.HideoutExpMultiplier == 1.0)
        {
            logger.Warning("[SkillMultiplier] Skill multiplier for Hideout Management and Crafting skill is disabled, please edit config.json in the mod folder to modify their multipliers.");
            return Task.CompletedTask;
        }
        var craftingExpAmount = hideoutConfig.CraftingExpAmount;
        var skillPointsPerCraft = globalTable.Configuration.SkillsSettings.HideoutManagement.SkillPointsPerCraft;
        var skillPointsPerAreaUpgrade = globalTable.Configuration.SkillsSettings.HideoutManagement.SkillPointsPerAreaUpgrade;

        var newCraftingExpAmount = craftingExpAmount * config.CraftingExpMultiplier;
        var newSkillPointsPerCraft = skillPointsPerCraft * config.HideoutExpMultiplier;
        var newSkillPointsPerAreaUpgrade = skillPointsPerAreaUpgrade * config.HideoutExpMultiplier;

        hideoutConfig.CraftingExpAmount = newCraftingExpAmount;

        globalTable.Configuration.SkillsSettings.HideoutManagement.SkillPointsPerCraft = newSkillPointsPerCraft;
        globalTable.Configuration.SkillsSettings.HideoutManagement.SkillPointsPerAreaUpgrade = newSkillPointsPerAreaUpgrade;
        logger.Success($"[SkillMultiplier] Configs Edited Successfully, new values: crafting exp amount: {newCraftingExpAmount}, SkillPointsPerCraft: {newSkillPointsPerCraft}, SkillPointsPerAreaUpgrade: {newSkillPointsPerAreaUpgrade}");

        // Return a completed task
        return Task.CompletedTask;
    }
}


public record ModConfig
{
    public required double CraftingExpMultiplier { get; set; }
    public required double HideoutExpMultiplier { get; set; }
}
