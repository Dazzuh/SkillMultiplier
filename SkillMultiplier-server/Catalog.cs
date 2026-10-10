using SPTarkov.Server.Core.Models.Spt.Tables;

namespace SkillMultiplier;

/// <summary>
/// One tunable number, as the client actually reads it.
/// <para>
/// <see cref="Key"/> is the stable config key: <c>Settings.&lt;Skill&gt;.&lt;Field&gt;</c> in the client's own
/// terms. <see cref="Skill"/>, <see cref="Action"/>, <see cref="Description"/> and <see cref="SharedWith"/>
/// are display strings only and are never used to look anything up, so rewording them cannot break a saved
/// config.
/// </para>
/// <para>
/// <see cref="Description"/> is what the action is and when it fires. Every one was read off the client's
/// own trigger condition in <c>EFT.SkillManager</c> rather than guessed from the field name - several are
/// counter-intuitive (Endurance pays only while NOT overweight, Strength only WHILE overweight) and a
/// description that gets that backwards is worse than none.
/// </para>
/// <para>
/// <see cref="SharedWith"/> is set when one globals bucket feeds more than one in-game skill. Tarkov
/// does this for several weapon skills, and a UI that presents them as independent sliders would be
/// lying: they are the same number.
/// </para>
/// </summary>
public sealed record SkillActionEntry(
    string Key,
    string Skill,
    string Action,
    string Description,
    string? SharedWith,
    Func<SkillsSettings, double> Get,
    Action<SkillsSettings, double> Set,
    /// <summary>
    /// True when this row is applied where the server pays the XP rather than by scaling the globals value.
    /// <para>
    /// A row scale on a globals field only reaches XP the game derives from that field - and the client bakes
    /// those into each action's factor once, at its own startup, so a game restart is needed for a change to
    /// land. A row that no longer has a live reader (<c>WeaponTreatment.SkillPointsPerRepair</c>, whose client
    /// action belongs to a <c>ClientAuthorizedSkill</c> and never fires) needs the other treatment: the grant
    /// patch multiplies what the server pays, live.
    /// </para>
    /// <para>
    /// This flag is what stops the two being applied to one number twice. For a skill whose grants are
    /// computed from its globals fields (crafting, hideout), a row still scaling a globals value means its
    /// XP is derived from a value this mod already scaled, so the grant patch leaves it alone - otherwise
    /// that XP, which is computed from scaled fields, would be squared. Repair and quest grants are computed
    /// elsewhere, so their skills' rows still apply at grant time.
    /// </para>
    /// </summary>
    bool AppliedAtServerGrant = false);

/// <summary>
/// The catalog, derived from what the client <em>reads</em> - not from every field that happens to exist
/// in globals.json.
/// <para>
/// Method: every <c>.Factor(...)</c> call site in the client's <c>EFT.SkillManager</c> was enumerated, and
/// the entries below are the ones whose argument is a <c>Settings.*</c> read - the values the server half
/// scales in place.
/// </para>
/// <para>
/// A call site whose argument is a hardcoded literal is not listed here, and is not untunable either: the
/// client half multiplies the amount granted, so it reaches those. The case that cannot be tuned is a
/// <c>Settings</c> field with no call site reading it at all. <see cref="Unsupported"/> is that case, and
/// only that one.
/// </para>
/// </summary>
public static class Catalog
{
    /// <summary>Action fields read by weapon skills, per the shared bucket in <see cref="SkillsSettings"/>.</summary>
    private static readonly string[] WeaponFields =
    [
        "WeaponReloadAction",
        "WeaponShotAction",
        "WeaponChamberAction",
    ];

    private static readonly string[] WeaponLabels = ["Reload", "Shot", "Chamber"];

    private static readonly string[] WeaponDescriptions =
    [
        "XP for reloading the weapon.",
        "XP for each shot fired.",
        "XP for chambering a round - cycling the bolt, or racking a shotgun.",
    ];

    /// <summary>
    /// One globals bucket -> the in-game skills that read it.
    /// <para>
    /// Measured from <c>SkillManager.CreateCombat</c>, which passes <c>Settings.&lt;X&gt;.ArrayValues()</c> into
    /// each <c>WeaponSkill</c>. SMG/LMG/HMG are constructed with the <em>Assault</em> bucket, and
    /// Launcher/AttachedLauncher/Melee with the <em>Pistol</em> bucket - so those skills genuinely have no
    /// numbers of their own.
    /// </para>
    /// </summary>
    private static readonly (string Bucket, string Label, string[] Users)[] WeaponBuckets =
    [
        ("Pistol", "Pistol", ["Pistol", "Launcher", "AttachedLauncher", "Melee"]),
        ("Revolver", "Revolver", ["Revolver"]),
        ("Assault", "Assault", ["Assault", "SMG", "LMG", "HMG"]),
        ("Shotgun", "Shotgun", ["Shotgun"]),
        ("Sniper", "Sniper", ["Sniper"]),
        ("DMR", "DMR", ["DMR"]),
    ];

    public static IReadOnlyList<SkillActionEntry> All { get; } = Build();

    /// <summary>Keys the UI may present as <c>Skill -&gt; Action</c> groups, in display order.</summary>
    public static IReadOnlyList<string> SkillOrder { get; } =
        All.Select(e => e.Skill).Distinct(StringComparer.Ordinal).ToArray();

    /// <summary>One line per skill saying where its XP actually comes from, for the group heading.</summary>
    public static IReadOnlyDictionary<string, string> SkillSummaries { get; } = new Dictionary<string, string>
    {
        ["Endurance"] = "Sprinting and on-foot movement. Only pays out while you are NOT overweight.",
        ["Strength"] = "Sprinting and moving while overweight, plus push-ups. Only pays out WHILE overweight.",
        ["Vitality"] = "Taking damage and bleeding.",
        ["Health"] = "No action of its own: gains XP as Strength, Endurance and Vitality gain XP.",
        ["StressResistance"] = "Pain effects, and time spent at low health.",
        ["Metabolism"] = "Recovering hydration and energy.",
        ["Immunity"] = "Intoxication and poisoning, plus stimulator side effects.",
        ["Intellect"] = "Examining items, repairing gear, and the XP you gain in Lockpicking.",
        ["Attention"] = "Examining items, and the outcome of container searches.",
        ["Charisma"] = "No action of its own: gains XP as Intellect, Attention and Perception gain XP.",
        ["Search"] = "Searching containers, and finding items inside them.",
        ["MagDrills"] = "Loading, unloading and checking magazines.",
        ["Throwing"] = "Throwing grenades.",
        ["AimDrills"] = "Shots taken immediately after aiming down sights.",
        ["TroubleShooting"] = "Clearing weapon malfunctions.",
        ["Surgery"] = "Performing surgery, plus a share of the XP FieldMedicine and FirstAid earn.",
        ["CovertMovement"] = "Moving while your noise level is low.",
        ["WeaponTreatment"] = "Repairing weapons.",
        ["Crafting"] = "Hideout crafting cycles.",
        ["HideoutManagement"] = "Hideout crafts, zone upgrades, and resource consumption.",
        ["Pistol"] = "Handling pistols - also used by launchers, attached launchers and melee.",
        ["Revolver"] = "Handling revolvers.",
        ["Assault"] = "Handling assault rifles - also used by SMG, LMG and HMG.",
        ["Shotgun"] = "Handling shotguns.",
        ["Sniper"] = "Handling sniper rifles.",
        ["DMR"] = "Handling designated marksman rifles.",
    };

    /// <summary>
    /// Skills a user will reasonably expect to tune but which have no experience to multiply in this install -
    /// neither a globals value the server can scale nor an action the game client builds.
    /// <para>
    /// One entry per skill, so each can be dropped independently: the list is filtered against the live
    /// catalog (see <see cref="Unsupported"/>) and a skill that another mod gives XP to stops being listed.
    /// That is why the notes avoid naming other skills - a note has to survive the rest of its entry's skills
    /// disappearing from the list.
    /// </para>
    /// </summary>
    public static IReadOnlyList<(string Skill, string Note)> Unsupported { get; } =
    [
        ("Memory", "Its value exists in the server's globals but nothing reads it, so there is no XP to scale."),
        ("RecoilControl", "Driven only by a per-level recoil buff - there is no XP action to multiply."),
        ("WeaponModding", "No XP action."),
        ("AdvancedModding", "No XP action."),
        ("NightOps", "No XP action."),
        ("Freetrading", "A trader skill, with no XP action."),
        ("Auctions", "A trader skill, with no XP action."),
        ("Barter", "A trader skill, with no XP action."),
        ("Cleanoperations", "No XP action."),
        ("Taskperformance", "No XP action."),
        ("BotReload", "Bot-only skill, no XP action."),
        ("BotSound", "Bot-only skill, no XP action."),
    ];

    private static List<SkillActionEntry> Build()
    {
        var list = new List<SkillActionEntry>();

        // Key is always "Settings.<bucket>.<globals field>", so a saved config survives any change to the
        // display label. <paramref name="label"/> only affects what the UI shows.
        void Add(
            string skill,
            string field,
            Func<SkillsSettings, double> get,
            Action<SkillsSettings, double> set,
            string description,
            string? shared = null,
            string? label = null,
            bool appliedAtServerGrant = false) =>
            list.Add(new SkillActionEntry(
                $"Settings.{skill}.{field}", skill, label ?? field, description, shared, get, set, appliedAtServerGrant));

        // --- Endurance ---
        // Note the inverted condition: these pay out only while NOT overweight. Strength covers the
        // overweight case instead, so the two skills deliberately do not overlap.
        Add("Endurance", "SprintAction", s => s.Endurance.SprintAction, (s, v) => s.Endurance.SprintAction = v,
            "XP per second of sprinting. Only while NOT overweight, and rises with fatigue.");
        Add("Endurance", "MovementAction", s => s.Endurance.MovementAction, (s, v) => s.Endurance.MovementAction = v,
            "XP per second of walking or running. Only while NOT overweight, and rises with fatigue.");
        // GainPerFatigueStack is a bonus scaling on the two above, not XP on its own; excluded deliberately.

        // --- Strength ---
        // Min/Max are the ends of a lerp across how overweight you are: Min applies as soon as you are over
        // the limit, Max at the heaviest. All of these are zero when you are NOT overweight.
        Add("Strength", "SprintActionMin", s => s.Strength.SprintActionMin, (s, v) => s.Strength.SprintActionMin = v,
            "XP per second sprinting while overweight - the value at the lightest end of the overweight range.");
        Add("Strength", "SprintActionMax", s => s.Strength.SprintActionMax, (s, v) => s.Strength.SprintActionMax = v,
            "XP per second sprinting while overweight - the value at the heaviest end of the overweight range.");
        Add("Strength", "MovementActionMin", s => s.Strength.MovementActionMin, (s, v) => s.Strength.MovementActionMin = v,
            "XP per second moving while overweight - lightest end of the overweight range.");
        Add("Strength", "MovementActionMax", s => s.Strength.MovementActionMax, (s, v) => s.Strength.MovementActionMax = v,
            "XP per second moving while overweight - heaviest end of the overweight range.");
        Add("Strength", "PushUpMin", s => s.Strength.PushUpMin, (s, v) => s.Strength.PushUpMin = v,
            "XP for push-up movement while overweight - lightest end of the overweight range.");
        Add("Strength", "PushUpMax", s => s.Strength.PushUpMax, (s, v) => s.Strength.PushUpMax = v,
            "XP for push-up movement while overweight - heaviest end of the overweight range.");

        // --- Vitality ---
        Add("Vitality", "DamageTakenAction", s => s.Vitality.DamageTakenAction, (s, v) => s.Vitality.DamageTakenAction = v,
            "XP for taking damage.");
        Add("Vitality", "HealthNegativeEffect", s => s.Vitality.HealthNegativeEffect, (s, v) => s.Vitality.HealthNegativeEffect = v,
            "XP for bleeding - fires when a bleed is applied.");

        // --- Health ---
        Add("Health", "SkillProgress", s => s.Health.SkillProgress, (s, v) => s.Health.SkillProgress = v,
            "XP awarded whenever Strength, Endurance or Vitality gains XP. Health has no action of its own.");

        // --- StressResistance ---
        Add("StressResistance", "HealthNegativeEffect", s => s.StressResistance.HealthNegativeEffect, (s, v) => s.StressResistance.HealthNegativeEffect = v,
            "XP for pain effects.");
        Add("StressResistance", "LowHPDuration", s => s.StressResistance.LowHPDuration, (s, v) => s.StressResistance.LowHPDuration = v,
            "XP for time spent at low health - the longer you stay hurt, the more it pays.");

        // --- Metabolism ---
        Add("Metabolism", "HydrationRecoveryRate", s => s.Metabolism.HydrationRecoveryRate, (s, v) => s.Metabolism.HydrationRecoveryRate = v,
            "XP for regaining hydration (positive changes only, so eating and drinking).");
        Add("Metabolism", "EnergyRecoveryRate", s => s.Metabolism.EnergyRecoveryRate, (s, v) => s.Metabolism.EnergyRecoveryRate = v,
            "XP for regaining energy (positive changes only).");

        // --- Immunity ---
        Add("Immunity", "HealthNegativeEffect", s => s.Immunity.HealthNegativeEffect, (s, v) => s.Immunity.HealthNegativeEffect = v,
            "XP for intoxication - poisoning from food, water or gas.");
        Add("Immunity", "StimulatorNegativeBuff", s => s.Immunity.StimulatorNegativeBuff, (s, v) => s.Immunity.StimulatorNegativeBuff = v,
            "XP when a stimulator's negative effect is applied.");

        // --- Intellect ---
        Add("Intellect", "ExamineAction", s => s.Intellect.ExamineAction, (s, v) => s.Intellect.ExamineAction = v,
            "XP for examining an item.");
        Add("Intellect", "SkillProgress", s => s.Intellect.SkillProgress, (s, v) => s.Intellect.SkillProgress = v,
            "XP awarded whenever Lockpicking gains XP.");
        Add("Intellect", "RepairAction", s => s.Intellect.RepairAction, (s, v) => s.Intellect.RepairAction = v,
            "XP for repairing a weapon or armour.");

        // --- Attention ---
        Add("Attention", "ExamineWithInstruction", s => s.Attention.ExamineWithInstruction, (s, v) => s.Attention.ExamineWithInstruction = v,
            "XP for examining an item you already hold information about.");
        Add("Attention", "FindActionTrue", s => s.Attention.FindActionTrue, (s, v) => s.Attention.FindActionTrue = v,
            "XP when searching a container turns something up.");
        Add("Attention", "FindActionFalse", s => s.Attention.FindActionFalse, (s, v) => s.Attention.FindActionFalse = v,
            "XP when searching a container comes up empty.");

        // --- Charisma ---
        Add("Charisma", "SkillProgressInt", s => s.Charisma.SkillProgressInt, (s, v) => s.Charisma.SkillProgressInt = v,
            "XP awarded whenever Intellect gains XP.");
        Add("Charisma", "SkillProgressAtn", s => s.Charisma.SkillProgressAtn, (s, v) => s.Charisma.SkillProgressAtn = v,
            "XP awarded whenever Attention gains XP.");
        Add("Charisma", "SkillProgressPer", s => s.Charisma.SkillProgressPer, (s, v) => s.Charisma.SkillProgressPer = v,
            "XP awarded whenever Perception gains XP.");

        // --- Search ---
        Add("Search", "SearchAction", s => s.Search.SearchAction, (s, v) => s.Search.SearchAction = v,
            "XP for searching a container.");
        Add("Search", "FindAction", s => s.Search.FindAction, (s, v) => s.Search.FindAction = v,
            "XP for finding an item while searching.");

        // --- MagDrills ---
        Add("MagDrills", "RaidLoadedAmmoAction", s => s.MagDrills.RaidLoadedAmmoAction, (s, v) => s.MagDrills.RaidLoadedAmmoAction = v,
            "XP for loading rounds into a magazine during a raid.");
        Add("MagDrills", "RaidUnloadedAmmoAction", s => s.MagDrills.RaidUnloadedAmmoAction, (s, v) => s.MagDrills.RaidUnloadedAmmoAction = v,
            "XP for unloading rounds out of a magazine during a raid.");
        Add("MagDrills", "MagazineCheckAction", s => s.MagDrills.MagazineCheckAction, (s, v) => s.MagDrills.MagazineCheckAction = v,
            "XP for checking a magazine to see what is in it.");

        // --- Throwing ---
        Add("Throwing", "ThrowAction", s => s.Throwing.ThrowAction, (s, v) => s.Throwing.ThrowAction = v,
            "XP for throwing a grenade.");

        // --- AimDrills ---
        Add("AimDrills", "WeaponShotAction", s => s.AimDrills.WeaponShotAction, (s, v) => s.AimDrills.WeaponShotAction = v,
            "XP for a shot taken immediately after aiming down sights - a fast-aim shot.");

        // --- TroubleShooting ---
        Add("TroubleShooting", "SkillPointsPerMalfFix", s => s.TroubleShooting.SkillPointsPerMalfFix, (s, v) => s.TroubleShooting.SkillPointsPerMalfFix = v,
            "XP for clearing a weapon malfunction.");

        // --- Surgery ---
        // SurgeryAction fires on surgery itself. The SkillProgress line is the receiving side of the
        // FieldMedicine/FirstAid relationship: Surgery gains XP when either of those gains XP.
        Add("Surgery", "SurgeryAction", s => s.Surgery.SurgeryAction, (s, v) => s.Surgery.SurgeryAction = v,
            "XP for performing surgery on a wound.");
        Add("Surgery", "SkillProgress", s => s.Surgery.SkillProgress, (s, v) => s.Surgery.SkillProgress = v,
            "XP awarded whenever FieldMedicine or FirstAid gains XP. This tunes SURGERY, not those two - they have no XP actions of their own.");

        // --- CovertMovement ---
        Add("CovertMovement", "MovementAction", s => s.CovertMovement.MovementAction, (s, v) => s.CovertMovement.MovementAction = v,
            "XP for moving while your noise level is low. Pays nothing when you are too loud.");

        // --- WeaponTreatment ---
        // The client cannot progress this skill at all: its weapon-repair action belongs to a
        // ClientAuthorizedSkill and never fires, so this field has no live reader. The server pays for repairs
        // from its own repair config, so this row is applied there - see SkillActionEntry.AppliedAtServerGrant.
        Add("WeaponTreatment", "SkillPointsPerRepair", s => s.WeaponTreatment.SkillPointsPerRepair, (s, v) => s.WeaponTreatment.SkillPointsPerRepair = v,
            "XP for repairing a weapon. Repairs are paid by the server, and this row is what scales them.",
            appliedAtServerGrant: true);

        // --- Crafting ---
        // The client derives PointsPerSecond and PointsPerOriginalCraft from these four, so these are the
        // real inputs; tuning the derived values is not possible and not needed.
        Add("Crafting", "PointsPerCraftingCycle", s => s.Crafting.PointsPerCraftingCycle, (s, v) => s.Crafting.PointsPerCraftingCycle = v,
            "XP per completed crafting cycle.");
        Add("Crafting", "CraftingCycleHours", s => s.Crafting.CraftingCycleHours, (s, v) => s.Crafting.CraftingCycleHours = v,
            "Hours per crafting cycle, used for one thing: turning the per-cycle payment into a per-second "
                + "rate. Lower means more XP per second, and it does not change how long a craft takes.");
        Add("Crafting", "PointsPerUniqueCraftCycle", s => s.Crafting.PointsPerUniqueCraftCycle, (s, v) => s.Crafting.PointsPerUniqueCraftCycle = v,
            "XP the first time you complete a given recipe.");
        Add("Crafting", "UniqueCraftsPerCycle", s => s.Crafting.UniqueCraftsPerCycle, (s, v) => s.Crafting.UniqueCraftsPerCycle = v,
            "How many first-time crafts count towards a cycle.");

        // --- HideoutManagement ---
        Add("HideoutManagement", "SkillPointsPerCraft", s => s.HideoutManagement.SkillPointsPerCraft, (s, v) => s.HideoutManagement.SkillPointsPerCraft = v,
            "XP for finishing a craft in the hideout.");
        Add("HideoutManagement", "SkillPointsPerAreaUpgrade", s => s.HideoutManagement.SkillPointsPerAreaUpgrade, (s, v) => s.HideoutManagement.SkillPointsPerAreaUpgrade = v,
            "XP for upgrading a hideout zone.");
        Add("HideoutManagement", "SkillPointsRate.Generator.PointsGained",
            s => s.HideoutManagement.SkillPointsRate.Generator.PointsGained,
            (s, v) => s.HideoutManagement.SkillPointsRate.Generator.PointsGained = v,
            "XP earned as the generator burns fuel, per unit of resource consumed.");
        Add("HideoutManagement", "SkillPointsRate.AirFilteringUnit.PointsGained",
            s => s.HideoutManagement.SkillPointsRate.AirFilteringUnit.PointsGained,
            (s, v) => s.HideoutManagement.SkillPointsRate.AirFilteringUnit.PointsGained = v,
            "XP earned as the air filtering unit consumes filters.");
        Add("HideoutManagement", "SkillPointsRate.WaterCollector.PointsGained",
            s => s.HideoutManagement.SkillPointsRate.WaterCollector.PointsGained,
            (s, v) => s.HideoutManagement.SkillPointsRate.WaterCollector.PointsGained = v,
            "XP earned as the water collector produces water.");
        Add("HideoutManagement", "SkillPointsRate.SolarPower.PointsGained",
            s => s.HideoutManagement.SkillPointsRate.SolarPower.PointsGained,
            (s, v) => s.HideoutManagement.SkillPointsRate.SolarPower.PointsGained = v,
            "Not a source of its own: a BONUS added on top of every other hideout area's rate while solar power is built.");

        // --- Weapon skills, one entry per shared bucket ---
        foreach (var (bucket, label, users) in WeaponBuckets)
        {
            var shared = users.Length > 1 ? "Read by: " + string.Join(", ", users) : null;

            for (var i = 0; i < WeaponFields.Length; i++)
            {
                Add(label, WeaponFields[i], GetWeapon(bucket, WeaponFields[i]), SetWeapon(bucket, WeaponFields[i]),
                    WeaponDescriptions[i], shared, WeaponLabels[i]);
            }
        }

        return list;
    }

    private static Func<SkillsSettings, double> GetWeapon(string bucket, string field) => bucket switch
    {
        "Pistol" => s => ReadWeapon(s.Pistol, field),
        "Revolver" => s => ReadWeapon(s.Revolver, field),
        "Assault" => s => ReadWeapon(s.Assault, field),
        "Shotgun" => s => ReadWeapon(s.Shotgun, field),
        "Sniper" => s => ReadWeapon(s.Sniper, field),
        "DMR" => s => ReadWeapon(s.DMR, field),
        _ => throw new ArgumentOutOfRangeException(nameof(bucket), bucket, "unknown weapon bucket"),
    };

    private static Action<SkillsSettings, double> SetWeapon(string bucket, string field) => bucket switch
    {
        "Pistol" => (s, v) => WriteWeapon(s.Pistol, field, v),
        "Revolver" => (s, v) => WriteWeapon(s.Revolver, field, v),
        "Assault" => (s, v) => WriteWeapon(s.Assault, field, v),
        "Shotgun" => (s, v) => WriteWeapon(s.Shotgun, field, v),
        "Sniper" => (s, v) => WriteWeapon(s.Sniper, field, v),
        "DMR" => (s, v) => WriteWeapon(s.DMR, field, v),
        _ => throw new ArgumentOutOfRangeException(nameof(bucket), bucket, "unknown weapon bucket"),
    };

    private static double ReadWeapon(WeaponSkills w, string field) => field switch
    {
        "WeaponReloadAction" => w.WeaponReloadAction,
        "WeaponShotAction" => w.WeaponShotAction,
        "WeaponChamberAction" => w.WeaponChamberAction,
        _ => throw new ArgumentOutOfRangeException(nameof(field), field, "unknown weapon field"),
    };

    private static void WriteWeapon(WeaponSkills w, string field, double v)
    {
        switch (field)
        {
            case "WeaponReloadAction": w.WeaponReloadAction = v; break;
            case "WeaponShotAction": w.WeaponShotAction = v; break;
            case "WeaponChamberAction": w.WeaponChamberAction = v; break;
            default: throw new ArgumentOutOfRangeException(nameof(field), field, "unknown weapon field");
        }
    }
}
