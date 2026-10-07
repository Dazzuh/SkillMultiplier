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
/// pushed table, because the game is the only thing that can read its own file. Each install acts exactly
/// once: whichever answer arrives, the old values are then deleted from the file, so there is nothing left
/// to ask about on the next launch and no marker to keep.
/// </para>
/// <para>
/// It reproduces the old mod's arithmetic rather than its inputs. The old patch multiplied by
/// <c>perSkill * global</c>, with the global reaching every skill including Crafting and
/// HideoutManagement - which the new global multiplier deliberately does not reach - so the old global
/// is baked into each carried action instead of being set as the new global. Same numbers either way.
/// </para>
/// <para>
/// It carries into the client key space only. Skills the game will not progress from the client, which is
/// Crafting, HideoutManagement and anything else built the same way, are left to the server: it scales their
/// globals and migrates the old server-side fields itself, and a value carried onto their client rows would
/// sit there inert.
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

    private const string TablePath = "/skillmultiplier/api/table";
    private const string SavePath = "/skillmultiplier/api/save";

    /// <summary>
    /// Whether this install still holds the previous release's config. Read once and kept: the file only
    /// changes when the user edits it by hand, and re-parsing it on every heartbeat tick would be pure cost.
    /// </summary>
    private static bool? _hasLegacy;

    /// <summary>
    /// Whether this session has already answered the migration question, by migrating or by declining.
    /// Answering deletes the old values from the file, so the next launch detects nothing - this only stops
    /// the question coming back for the rest of this session.
    /// </summary>
    private static bool _acted;

    /// <summary>
    /// Whether there is anything to ask about. Reported to the server with the action catalog, so the page
    /// can offer the migration question - and only then.
    /// </summary>
    internal static bool HasLegacyConfig()
    {
        if (_acted)
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
    /// The latest main-thread action description, for the background migration tick. Written by
    /// <c>TableClient.DrainMainThread</c>, read by <see cref="Tick"/>: the tick itself does HTTP and file
    /// IO, which must stay off the game's main thread, so it consumes this instead of describing again.
    /// </summary>
    private static volatile List<ClientActionInfo> _described = new();

    /// <summary>Hand the background tick a fresh action description. Safe from any thread.</summary>
    internal static void OfferDescribed(List<ClientActionInfo> described) => _described = described ?? new();

    /// <summary>
    /// Act on the page's answer, if one has arrived and this install has not acted yet. Runs on the
    /// heartbeat thread: file and network IO are fine here. Game state arrives via
    /// <see cref="OfferDescribed"/> rather than a live manager, so nothing here touches game objects.
    /// </summary>
    internal static void Tick()
    {
        if (_acted)
        {
            return;
        }

        switch (TableClient.LegacyRequest)
        {
            case "decline":
                // Asked and answered: the old entries are deleted, so the question never comes back.
                MarkActed();
                Plugin.DebugLog("[SkillMultiplier] Leaving the previous release's multipliers unmigrated, as asked.");
                break;

            case "migrate":
                RunMigration(_described);
                break;

            default:
                // Unasked. Detection alone migrates nothing.
                break;
        }
    }

    /// <summary>
    /// Runs until it succeeds or finds nothing to do - answering deletes the old values, which stops it
    /// re-reading the file on every tick.
    /// </summary>
    internal static void RunMigration(List<ClientActionInfo> described)
    {
        if (_acted)
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
            MarkActed();
            return;
        }

        try
        {
            var actions = described ?? new List<ClientActionInfo>();

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

                // A skill the game will not progress from the client is left alone: a value carried onto its
                // client rows would sit there inert, since the client's progress path never runs for it. The
                // server migrates those skills itself.
                if (!action.ServerAuthoritative
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
                MarkActed();
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

            MarkActed();

            Plugin.DebugLog(
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
    /// Answer once: latch the session flag, forget any cached detection, and delete the old values from
    /// the file so the next launch detects nothing. Runs after a migrate, a decline, or finding nothing to
    /// carry - all three mean the question must never come back.
    /// </summary>
    private static void MarkActed()
    {
        _acted = true;
        _hasLegacy = false;
        ClearLegacyEntries();
    }

    /// <summary>
    /// Delete the previous release's values from this plugin's own config file: the whole
    /// <c>[Multipliers]</c> section and the <c>Global Multiplier</c> line under <c>[General]</c>. Everything
    /// else in the file is left alone, comments included.
    /// <para>
    /// The old entries are bound by nothing, so BepInEx holds them as orphans: editing the disk alone would
    /// let a later in-memory save write them straight back, which is why the config is reloaded afterwards.
    /// Reloading only re-reads what is on disk - every live entry keeps its value.
    /// </para>
    /// </summary>
    private static void ClearLegacyEntries()
    {
        var config = Plugin.Instance != null ? Plugin.Instance.Config : null;

        if (config == null)
        {
            return;
        }

        var path = config.ConfigFilePath;

        if (!File.Exists(path))
        {
            return;
        }

        var kept = new List<string>();
        var section = string.Empty;
        var changed = false;

        foreach (var line in File.ReadAllLines(path))
        {
            var trimmed = line.Trim();

            if (trimmed.StartsWith("["))
            {
                section = trimmed;
                if (trimmed == $"[{LegacySection}]")
                {
                    changed = true;
                    continue;
                }
                kept.Add(line);
                continue;
            }

            if (section == $"[{LegacySection}]")
            {
                changed = true;
                continue;
            }

            if (section == $"[{GlobalSection}]")
            {
                var separator = trimmed.IndexOf('=');

                if (separator > 0
                    && trimmed.Substring(0, separator).Trim() == GlobalKey)
                {
                    changed = true;
                    continue;
                }
            }

            kept.Add(line);
        }

        if (!changed)
        {
            return;
        }

        File.WriteAllLines(path, kept);
        config.Reload();
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
