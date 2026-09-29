using System;
using System.Collections.Generic;

namespace SkillMultiplier;

/// <summary>
/// Human names and descriptions for the client's own actions, keyed <c>SkillId[index]</c>.
/// <para>
/// This has to be curated, because there is nowhere to read it at runtime: a <c>SkillAction</c> carries no
/// name, no id and no index - only <c>FactorValue</c>, <c>SimpleCalculation</c>, <c>StartTime</c> and its
/// event. Nothing on the server can see an action at all.
/// </para>
/// <para>
/// Every entry here comes from the game's own skill construction: <c>SkillManager.method_2</c> through
/// <c>method_4</c> and <c>WeaponSkill</c>'s constructor in the deployed <c>Assembly-CSharp.dll</c>, which
/// name the trigger each action is bound to (<c>SprintAction</c>, <c>DamageTakenAction</c>,
/// <c>WeaponReloadAction</c>...) and, in the closures, the exact condition it pays out under. Where a
/// trigger's firing site is what identifies it, that was read with <c>get_xrefs_to</c> rather than guessed -
/// <c>PushUp</c> comes from <c>MovementContext.set_IsInPronePose</c> and <c>SetPoseLevel</c>, and
/// <c>OnlineAction</c> from the session-end path.
/// </para>
/// <para>
/// The wording deliberately matches the server-side catalog's (<c>Catalog.cs</c>): the same
/// "XP per second of ..." / "XP for ..." shape and the same terse condition clause, because the page shows
/// both halves in one list and a row that reads differently invites the reader to assume it describes
/// something different. Where a trigger is shared with a server-side row, the two texts are the same text.
/// </para>
/// <para>
/// The index is a position in the skill's own action array, so a mod that reorders its actions shifts these
/// names - the same caveat the <c>SkillId[index]</c> keys themselves carry.
/// </para>
/// </summary>
internal static class ClientActionNames
{
    internal readonly record struct Entry(string Name, string Description);

    private static readonly Dictionary<string, Entry> ByKey = new(StringComparer.OrdinalIgnoreCase);

    internal static bool TryGet(string key, out Entry entry) => ByKey.TryGetValue(key, out entry);

    static ClientActionNames()
    {
        AddWeaponSkills();

        // --- Physical ---------------------------------------------------------------------------------

        Add("Endurance[0]", "Sprinting",
            "XP per second of sprinting. Only while NOT overweight, and rises with fatigue.");
        Add("Endurance[1]", "Moving on foot",
            "XP per second of walking or running. Only while NOT overweight, and rises with fatigue.");

        Add("Strength[0]", "Sprinting while overweight",
            "XP per second sprinting while overweight - rises with how far past the weight limit you are.");
        Add("Strength[1]", "Moving while overweight",
            "XP per second moving while overweight - rises with how far past the weight limit you are.");
        Add("Strength[2]", "Pushing up while overweight",
            "XP for push-up movement while overweight - rises with how far past the weight limit you are.");
        Add("Strength[3]", "Fistfights",
            "XP for an unarmed melee hit.");
        Add("Strength[4]", "Throwing grenades",
            "XP for throwing a grenade. The same throw also pays Throwing.");

        Add("Vitality[0]", "Taking damage",
            "XP for taking damage.");
        Add("Vitality[1]", "Bleeding",
            "XP for bleeding - fires when a bleed is applied.");

        Add("Health[0]", "Follows Strength, Endurance and Vitality",
            "XP awarded whenever Strength, Endurance or Vitality gains XP. Health has no action of its own.");

        Add("StressResistance[0]", "Pain",
            "XP for pain effects.");
        Add("StressResistance[1]", "Time spent at low health",
            "XP for time spent at low health - the longer you stay hurt, the more it pays.");

        Add("Metabolism[0]", "Hydration restored",
            "XP for regaining hydration (positive changes only, so eating and drinking).");
        Add("Metabolism[1]", "Energy restored",
            "XP for regaining energy (positive changes only).");

        Add("Immunity[0]", "Poisoning",
            "XP for intoxication - poisoning from food, water or gas.");
        Add("Immunity[1]", "Stimulator side effects",
            "XP when a stimulator's negative effect is applied.");

        // --- Mental -----------------------------------------------------------------------------------

        Add("Perception[0]", "Finishing a raid",
            "XP for finishing a raid - paid once, at session end.");
        Add("Perception[1]", "Finding unique loot",
            "XP for looting an item you have not found before (the game's unique-loot trigger).");

        Add("Intellect[0]", "Examining items",
            "XP for examining an item.");
        Add("Intellect[1]", "Follows Lockpicking",
            "XP awarded whenever Lockpicking gains XP.");
        Add("Intellect[2]", "Repairing items",
            "XP for repairing a weapon or armour.");
        Add("Intellect[3]", "Follows Crafting",
            "XP awarded for Crafting progress, converted at the game's crafting-to-Intellect rate.");

        Add("Attention[0]", "Examining under instruction",
            "XP for examining an item you already hold information about.");
        Add("Attention[1]", "Successful search",
            "XP when searching a container turns something up.");
        Add("Attention[2]", "Unsuccessful search",
            "XP when searching a container comes up empty - Attention is the one skill that pays for this.");

        Add("Charisma[0]", "Follows Intellect",
            "XP awarded whenever Intellect gains XP.");
        Add("Charisma[1]", "Follows Attention",
            "XP awarded whenever Attention gains XP.");
        Add("Charisma[2]", "Follows Perception",
            "XP awarded whenever Perception gains XP.");

        // --- Combat, non-weapon -----------------------------------------------------------------------

        Add("AimDrills[0]", "Fast-aim shots",
            "XP for a shot taken immediately after aiming down sights - a fast-aim shot.");
        Add("TroubleShooting[0]", "Clearing malfunctions",
            "XP for clearing a weapon malfunction.");
        Add("Throwing[0]", "Throwing grenades",
            "XP for throwing a grenade. The same throw also pays Strength.");

        // --- Practical --------------------------------------------------------------------------------

        Add("CovertMovement[0]", "Moving quietly",
            "XP for moving while your noise level is low. Pays nothing when you are too loud.");

        Add("Search[0]", "Searching containers",
            "XP for searching a container.");
        Add("Search[1]", "Finding something while searching",
            "XP for finding an item while searching.");

        Add("MagDrills[0]", "Loading magazines in raid",
            "XP for loading rounds into a magazine during a raid.");
        Add("MagDrills[1]", "Unloading magazines in raid",
            "XP for unloading rounds out of a magazine during a raid.");
        Add("MagDrills[2]", "Checking magazines",
            "XP for checking a magazine to see what is in it.");

        Add("Surgery[0]", "Performing surgery",
            "XP for performing surgery on a wound.");
        Add("Surgery[1]", "Follows Field Medicine and First Aid",
            "XP awarded whenever Field Medicine or First Aid gains XP. This pays Surgery, not those two.");

        Add("LightVests[0]", "Taking damage in light gear",
            "XP for taking damage while wearing light armour or a rig.");
        Add("HeavyVests[0]", "Taking damage in heavy armour",
            "XP for taking damage while wearing heavy armour.");

        Add("WeaponTreatment[0]", "Repairing weapons",
            "XP for repairing a weapon.");

        Add("Sniping[0]", "Long-range headshots",
            "XP for a headshot from 70 m or further, taken while holding your breath.");

        Add("Crafting[0]", "Craft time",
            "XP per second of craft time - longer recipes pay more.");
        Add("Crafting[1]", "First completion of a craft",
            "XP the first time you complete a given recipe.");

        Add("HideoutManagement[0]", "Completing a hideout craft",
            "XP for finishing a craft in the hideout.");
        Add("HideoutManagement[1]", "Upgrading a hideout zone",
            "XP for upgrading a hideout zone.");

        // The four consumption actions are built by looping the game's per-area rate table, so which area a
        // position is bound to is not fixed: the table enumerates in a different order in the server's view
        // than in the file, which was measured. Naming them as a set is the honest version.
        foreach (var index in new[] { 2, 3, 4, 5 })
        {
            Add($"HideoutManagement[{index}]", "Consuming hideout resources",
                "XP as a hideout zone consumes resources - generator fuel, air filters or collected water. "
                + "Which zone this row is bound to follows the game's rate table.");
        }
    }

    /// <summary>
    /// Weapon skills are all built by <c>WeaponSkill</c>'s constructor from a shared set of three triggers
    /// filtered by weapon type, so the names and descriptions follow from the type rather than from anything
    /// per-skill. The scope is the part worth stating: the globals row of the same name covers several weapon
    /// skills at once, where this one covers a single type.
    /// </summary>
    private static void AddWeaponSkills()
    {
        (string Skill, string Type)[] weaponTypes =
        [
            ("Pistol", "pistols"),
            ("Revolver", "revolvers"),
            ("SMG", "SMGs"),
            ("Assault", "assault rifles"),
            ("Shotgun", "shotguns"),
            ("Sniper", "sniper rifles"),
            ("LMG", "light machine guns"),
            ("HMG", "heavy machine guns"),
            ("Launcher", "launchers"),
            ("AttachedLauncher", "underbarrel launchers"),
            ("Melee", "melee weapons"),
            ("DMR", "marksman rifles"),
        ];

        foreach (var (skill, type) in weaponTypes)
        {
            Add($"{skill}[0]", $"Reloading {type}",
                $"XP for reloading the weapon, {type} only.");
            Add($"{skill}[1]", $"Shooting {type}",
                $"XP for each shot fired, {type} only.");
            Add($"{skill}[2]", $"Chambering a round with {type}",
                $"XP for chambering a round - cycling the bolt, or racking a shotgun - {type} only.");
        }
    }

    private static void Add(string key, string name, string description) =>
        ByKey[key] = new Entry(name, description);
}
