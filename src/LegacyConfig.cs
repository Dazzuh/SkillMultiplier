using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using BepInEx.Configuration;
using EFT;
using Newtonsoft.Json;
using SPT.Common.Http;

namespace SkillMultiplier;

/// <summary>
/// Carries the previous release's multipliers out of this plugin's own config file - but only when asked.
/// <para>
/// The old mod scaled whole skills from <c>[Multipliers]</c> entries in this same file, plus a
/// <c>[General] Global Multiplier</c>. This version scales individual actions from the table the server
/// pushes, so those entries mean nothing to it: without this, an upgrading user's tuning would be dropped in
/// silence and their XP would go back to vanilla wherever they had raised it.
/// </para>
/// <para>
/// Nothing here runs on its own. The page asks first - but only when there is something to ask about, which
/// is what <see cref="HasLegacyConfig"/> reports into the catalog report - and the answer arrives in the
/// pushed table, because the game is the only thing that can read its own file. The marker still makes each
/// install act exactly once, whichever answer arrives.
/// </para>
/// <para>
/// It reproduces the old mod's arithmetic rather than its inputs. The old patch multiplied by
/// <c>perSkill * global</c>, with the global reaching every skill including Crafting and
/// HideoutManagement - which the new global multiplier deliberately does not reach - so the old global
/// is baked into each carried action instead of being set as the new global. Same numbers either way.
/// </para>
/// <para>
/// It carries into the client key space only. Crafting and HideoutManagement are left to the server, which
/// scales their globals and migrates the old server-side fields itself - a skill whose event is covered by
/// both spaces would be multiplied twice.
/// </para>
/// <para>
/// Carried actions land one whole skill at a time - the old file has no per-action values - so every row
/// in an affected skill ends up equal, which is exactly what the page's group sliders show as linked.
/// </para>
/// </summary>
internal static class LegacyConfig
{
    private const string LegacySection = "Multipliers";

    private const string GlobalSection = "General";
    private const string GlobalKey = "Global Multiplier";

    /// <summary>
    /// The skills the old mod deliberately left to its server component (its own exclusion list), and which
    /// this half must not carry: the server migrates the same skills, and both spaces cover the same event.
    /// </summary>
    private static readonly string[] ServerOwned = ["Crafting", "HideoutManagement"];

    private const string TablePath = "/skillmultiplier/api/table";
    private const string SavePath = "/skillmultiplier/api/save";

    /// <summary>
    /// Whether this install still holds the previous release's config. Read once and kept: the file only
    /// changes when the user edits it by hand, and re-parsing it on every heartbeat tick would be pure cost.
    /// The marker answers for migrated installs without touching the file at all.
    /// </summary>
    private static bool? _hasLegacy;

    /// <summary>
    /// Whether there is anything to ask about. Reported to the server with the action catalog, so the page
    /// can offer the migration question - and only then.
    /// </summary>
    internal static bool HasLegacyConfig()
    {
        if (Plugin.CarriedLegacyConfig != null && Plugin.CarriedLegacyConfig.Value)
        {
            return false;
        }

        _hasLegacy ??= DetectLegacy();

        return _hasLegacy.Value;
    }

    /// <summary>Read the file for old values without touching anything. False when there is nothing to carry.</summary>
    private static bool DetectLegacy()
    {
        var config = Plugin.Instance != null ? Plugin.Instance.Config : null;

        if (config == null)
        {
            return false;
        }

        foreach (var entry in ReadSection(config.ConfigFilePath, LegacySection))
        {
            if (Math.Abs(entry.Value - 1.0) > 1e-9)
            {
                return true;
            }
        }

        var globalSection = ReadSection(config.ConfigFilePath, GlobalSection);

        return globalSection.TryGetValue(GlobalKey, out var globalValue)
            && Math.Abs(globalValue - 1.0) > 1e-9;
    }

    /// <summary>
    /// Act on the page's answer, if one has arrived and this install has not acted yet. Runs on ticks that
    /// already have a <see cref="SkillManager"/>, because carrying needs the live action list.
    /// </summary>
    internal static void Tick(SkillManager manager)
    {
        var marker = Plugin.CarriedLegacyConfig;

        if (manager == null || marker == null || marker.Value)
        {
            return;
        }

        switch (TableClient.LegacyRequest)
        {
            case "decline":
                // Asked and answered: the old entries stay in the file, inert - nothing reads them any more -
                // and the marker stops the question coming back.
                marker.Value = true;
                Plugin.Log.LogInfo("[SkillMultiplier] Leaving the previous release's multipliers unmigrated, as asked.");
                break;

            case "migrate":
                RunMigration(manager);
                break;

            default:
                // Unasked. Detection alone migrates nothing.
                break;
        }
    }

    /// <summary>
    /// Runs on a tick that already has a <see cref="SkillManager"/>, and only until it succeeds or finds
    /// nothing to do - the marker in the config then stops it re-reading the file on every tick.
    /// </summary>
    internal static void RunMigration(SkillManager manager)
    {
        var marker = Plugin.CarriedLegacyConfig;

        if (manager == null || marker == null || marker.Value)
        {
            return;
        }

        var config = Plugin.Instance != null ? Plugin.Instance.Config : null;

        if (config == null)
        {
            return;
        }

        var perSkill = new Dictionary<string, double>(StringComparer.OrdinalIgnoreCase);

        foreach (var entry in ReadSection(config.ConfigFilePath, LegacySection))
        {
            // Anything left at 1 is "no change" and is not worth carrying.
            if (Math.Abs(entry.Value - 1.0) > 1e-9)
            {
                perSkill[entry.Key] = entry.Value;
            }
        }

        var globalSection = ReadSection(config.ConfigFilePath, GlobalSection);
        var global = globalSection.TryGetValue(GlobalKey, out var globalValue) ? globalValue : 1.0;

        if (perSkill.Count == 0 && Math.Abs(global - 1.0) < 1e-9)
        {
            // Nothing to carry from this install.
            marker.Value = true;
            return;
        }

        try
        {
            var actions = ActionCatalog.Describe(manager);

            if (actions.Count == 0)
            {
                // The profile is not up yet; the next tick has the skill list.
                return;
            }

            var table = JsonConvert.DeserializeObject<TableResponse>(RequestHandler.GetJson(TablePath));

            if (table == null)
            {
                return;
            }

            var merged = table.Actions != null
                ? new Dictionary<string, double>(table.Actions, StringComparer.OrdinalIgnoreCase)
                : new Dictionary<string, double>(StringComparer.OrdinalIgnoreCase);

            var carried = 0;

            foreach (var action in actions)
            {
                var own = 1.0;

                if (Array.IndexOf(ServerOwned, action.Skill) < 0
                    && perSkill.TryGetValue(action.Skill, out var legacyValue))
                {
                    own = legacyValue;
                }

                var effective = own * global;

                if (Math.Abs(effective - 1.0) < 1e-9)
                {
                    continue;
                }

                // A value already set on the page wins: this fills in what is not set, it does not impose.
                if (!merged.TryAdd(action.Key, effective))
                {
                    continue;
                }

                carried++;
            }

            if (carried == 0)
            {
                marker.Value = true;
                return;
            }

            RequestHandler.PostJson(
                SavePath,
                JsonConvert.SerializeObject(
                    new SaveRequest
                    {
                        Enabled = table.Enabled,
                        DisableFatigue = table.DisableFatigue,
                        GlobalMultiplier = table.GlobalMultiplier,
                        Multipliers = table.Multipliers ?? new Dictionary<string, double>(),
                        Actions = merged,
                    }
                )
            );

            marker.Value = true;

            Plugin.Log.LogInfo(
                $"[SkillMultiplier] Carried the old config over: {carried} action(s), from {perSkill.Count} "
                    + $"skill(s) with a value of their own and a global multiplier of {global}. They are on the "
                    + "page now, and editable there."
            );
        }
        catch (Exception ex)
        {
            // Not fatal, and deliberately retried: the mod works either way, it just starts from 1.0 until
            // the server answers.
            Plugin.Log.LogWarning($"[SkillMultiplier] Could not carry the old config over yet: {ex.Message}");
        }
    }

    /// <summary>
    /// Read one section of the config file as numbers.
    /// <para>
    /// Straight from the file, because there is no way in: the old mod's <c>[Multipliers]</c> entries are
    /// bound by nothing any more, so BepInEx holds them as orphans, and <c>ConfigFile</c> keeps that set
    /// private - they appear in neither <c>Keys</c> nor the indexer. The file is the only place they exist.
    /// </para>
    /// <para>
    /// The format is a bracketed section header followed by <c>key = value</c> lines, with BepInEx's own
    /// comment lines starting with <c>#</c>. A value that is not a number - such as the placeholder the old
    /// mod wrote while it enumerated skills - simply does not parse and is skipped.
    /// </para>
    /// </summary>
    private static Dictionary<string, double> ReadSection(string path, string section)
    {
        var result = new Dictionary<string, double>(StringComparer.OrdinalIgnoreCase);

        if (!File.Exists(path))
        {
            return result;
        }

        var inSection = false;
        var header = $"[{section}]";

        foreach (var line in File.ReadAllLines(path))
        {
            var trimmed = line.Trim();

            if (trimmed.StartsWith("["))
            {
                inSection = trimmed == header;
                continue;
            }

            if (!inSection)
            {
                continue;
            }

            var separator = trimmed.IndexOf('=');

            if (separator <= 0)
            {
                continue;
            }

            var key = trimmed.Substring(0, separator).Trim();
            var text = trimmed.Substring(separator + 1).Trim();

            if (!double.TryParse(text, NumberStyles.Float, CultureInfo.InvariantCulture, out var value))
            {
                continue;
            }

            result[key] = value;
        }

        return result;
    }

    /// <summary>The part of the server's table payload this reads.</summary>
    private sealed class TableResponse
    {
        public bool Enabled { get; set; }

        public bool DisableFatigue { get; set; }

        public double GlobalMultiplier { get; set; } = 1.0;

        public Dictionary<string, double> Multipliers { get; set; }

        public Dictionary<string, double> Actions { get; set; }
    }

    /// <summary>
    /// The server replaces the whole config on save, so this echoes back the fields it did not touch -
    /// omitting <see cref="Multipliers"/> would clear every server-keyed multiplier the user has set, and
    /// omitting the global would reset it to 1.
    /// </summary>
    private sealed class SaveRequest
    {
        public bool Enabled { get; set; }

        public bool DisableFatigue { get; set; }

        public double GlobalMultiplier { get; set; } = 1.0;

        public Dictionary<string, double> Multipliers { get; set; } = new();

        public Dictionary<string, double> Actions { get; set; } = new();
    }
}
