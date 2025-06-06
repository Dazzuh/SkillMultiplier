import {DependencyContainer} from "tsyringe";

import {IPostDBLoadMod} from "@spt/models/external/IPostDBLoadMod";
import {ILogger} from "@spt/models/spt/utils/ILogger";
import {ConfigServer} from "@spt/servers/ConfigServer";
import {ConfigTypes} from "@spt/models/enums/ConfigTypes";
import {IHideoutConfig} from "@spt/models/spt/config/IHideoutConfig";
import {DatabaseServer} from "@spt/servers/DatabaseServer";
import {IDatabaseTables} from "@spt/models/spt/server/IDatabaseTables";

class SkillMultiplier implements IPostDBLoadMod {
    private configServer: ConfigServer;
    private databaseServer: DatabaseServer;
    private logger: ILogger;
    private modConfig = require("../config/config.json");

    public postDBLoad(container: DependencyContainer): void {
        this.logger = container.resolve<ILogger>("WinstonLogger");
        this.configServer = container.resolve<ConfigServer>("ConfigServer");
        this.databaseServer = container.resolve<DatabaseServer>("DatabaseServer");

        if (!this.modConfig.enabled) return;

        this.setSkillMultiplier("crafting", this.modConfig?.crafting);
        this.setSkillMultiplier("hideoutmanagement", this.modConfig?.hideoutManagement);
    }

    public setSkillMultiplier(skill: string, multiplier: number): void {
        if (skill === "crafting") {
            const config = this.configServer.getConfig<IHideoutConfig>(ConfigTypes.HIDEOUT);

            const originalExpCraftAmount = config.expCraftAmount;
            config.expCraftAmount = originalExpCraftAmount * multiplier;
            this.logger.info(`[SkillMultiplier] Crafting Multiplier: ${multiplier}. Original expCraftAmount: ${originalExpCraftAmount}, New expCraftAmount: ${config.expCraftAmount}`);
        }

        if (skill === "hideoutmanagement") {
            const tables: IDatabaseTables = this.databaseServer.getTables();

            const skillPointsPerCraft = tables.globals.config.SkillsSettings.HideoutManagement.SkillPointsPerCraft;
            const skillPointsPerAreaUpgrade = tables.globals.config.SkillsSettings.HideoutManagement.SkillPointsPerAreaUpgrade

            tables.globals.config.SkillsSettings.HideoutManagement.SkillPointsPerCraft = skillPointsPerCraft * multiplier;
            tables.globals.config.SkillsSettings.HideoutManagement.SkillPointsPerAreaUpgrade = skillPointsPerAreaUpgrade * multiplier;

            this.logger.info(`[SkillMultiplier] HideoutManagement Multiplier: ${multiplier}. Original SkillPointsPerCraft: ${skillPointsPerCraft}, New SkillPointsPerCraft: ${tables.globals.config.SkillsSettings.HideoutManagement.SkillPointsPerCraft}`);
            this.logger.info(`[SkillMultiplier] Original SkillPointsPerAreaUpgrade: ${skillPointsPerAreaUpgrade}, New SkillPointsPerAreaUpgrade: ${tables.globals.config.SkillsSettings.HideoutManagement.SkillPointsPerAreaUpgrade}`);
        }
    }
}

// noinspection JSUnusedGlobalSymbols
export const mod = new SkillMultiplier();
