import {DependencyContainer} from "tsyringe";

import {IPostDBLoadMod} from "@spt/models/external/IPostDBLoadMod";
import {ILogger} from "@spt/models/spt/utils/ILogger";
import {ConfigServer} from "@spt/servers/ConfigServer";
import {ConfigTypes} from "@spt/models/enums/ConfigTypes";
import {IHideoutConfig} from "@spt/models/spt/config/IHideoutConfig";
import {DatabaseServer} from "@spt/servers/DatabaseServer";
import {IDatabaseTables} from "@spt/models/spt/server/IDatabaseTables";

class SkillMultiplier implements IPostDBLoadMod {
    private container: DependencyContainer;
    private logger: ILogger;
    private modConfig = require("../config/config.json");

    public postDBLoad(container: DependencyContainer): void {
        this.logger = container.resolve<ILogger>("WinstonLogger");
        this.container = container;

        if (!this.modConfig.enabled) return;

        this.setSkillMultiplier("crafting", this.modConfig?.crafting);
        this.setSkillMultiplier("hideoutmanagement", this.modConfig?.hideoutManagement);
    }

    public setSkillMultiplier(skill: string, multiplier: number): void {
        if (skill === "crafting") {
            const configServer: ConfigServer = this.container.resolve<ConfigServer>("ConfigServer");
            const config: IHideoutConfig = configServer.getConfig<IHideoutConfig>(ConfigTypes.HIDEOUT);

            const originalExpCraftAmount = config.expCraftAmount;
            config.expCraftAmount = originalExpCraftAmount * multiplier;
            this.logger.info(`[SkillMultiplier] Crafting Multiplier: ${multiplier}. Original expCraftAmount: ${originalExpCraftAmount}, New expCraftAmount: ${config.expCraftAmount}`);
        }

        if (skill === "hideoutmanagement") {
            const databaseServer: DatabaseServer = this.container.resolve<DatabaseServer>("DatabaseServer");
            const tables: IDatabaseTables = databaseServer.getTables();

            const hideoutManagement = tables.globals.config.SkillsSettings.HideoutManagement;
            const { SkillPointsPerCraft, SkillPointsPerAreaUpgrade } = hideoutManagement;

            hideoutManagement.SkillPointsPerCraft = SkillPointsPerCraft * multiplier;
            hideoutManagement.SkillPointsPerAreaUpgrade = SkillPointsPerAreaUpgrade * multiplier;

            this.logger.info(`[SkillMultiplier] HideoutManagement Multiplier: ${multiplier}. Original SkillPointsPerCraft: ${SkillPointsPerCraft}, New SkillPointsPerCraft: ${hideoutManagement.SkillPointsPerCraft}`);
            this.logger.info(`[SkillMultiplier] Original SkillPointsPerAreaUpgrade: ${SkillPointsPerAreaUpgrade}, New SkillPointsPerAreaUpgrade: ${hideoutManagement.SkillPointsPerAreaUpgrade}`);
        }
    }
}

// noinspection JSUnusedGlobalSymbols
export const mod = new SkillMultiplier();
