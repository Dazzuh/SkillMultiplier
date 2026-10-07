using System.Text.Json;
using System.Text.Json.Nodes;
using SPTarkov.Common.Models.Logging;

namespace SkillMultiplier;

/// <summary>
/// Carries the previous release's two-field server config into the key map,
/// on demand from the migrate endpoint, never at boot.
/// <para>
/// Extracted verbatim from <c>SkillMultiplierMod</c> (M5 split). Stateless:
/// every method takes the config path (and the store) explicitly so a test
/// can point it at a scratch file. Detection reads the file rather than
/// memory, so the answer stays right even if the file changed under the
/// running server.
/// </para>
/// </summary>
internal sealed class LegacyMigrator(
    ISptLogger<SkillMultiplierMod> logger
)
{
    private readonly ISptLogger<SkillMultiplierMod> _logger = logger;

    /// <summary>
    /// Whether the previous release's settings are still sitting in config.json. Read off the file rather
    /// than memory: the page asks about them, and the answer must still be right if the file changed.
    /// </summary>
    public bool Has(string configPath)
    {
        var root = ReadRoot(configPath);

        return root != null
            && (Value(root, "CraftingExpMultiplier") is not null
                || Value(root, "HideoutExpMultiplier") is not null);
    }

    /// <summary>
    /// Carry the previous release's settings over, on demand from the migrate endpoint - never at boot.
    /// <para>
    /// The old mod's config was two fields rather than a key map. Writing the file back in the new shape
    /// is what makes this a one-shot: the legacy fields are gone afterwards, so asking again finds nothing
    /// and a value the user has since changed on the page is never overwritten.
    /// </para>
    /// </summary>
    /// <returns>True when legacy fields were present (and are now gone).</returns>
    public bool Run(ConfigStore store, string configPath, Action saveConfig)
    {
        var root = ReadRoot(configPath);

        if (root == null)
        {
            return false;
        }

        var crafting = Value(root, "CraftingExpMultiplier");
        var hideout = Value(root, "HideoutExpMultiplier");

        if (crafting is null && hideout is null)
        {
            return false;
        }

        var migrated = new List<string>();

        if (hideout is { } hideoutValue && Math.Abs(hideoutValue - 1.0) > 1e-9)
        {
            // Exactly the two values the old mod scaled, so these are the same levers and not an approximation.
            MapLegacy(store, "HideoutManagement", "SkillPointsPerCraft", hideoutValue, migrated);
            MapLegacy(store, "HideoutManagement", "SkillPointsPerAreaUpgrade", hideoutValue, migrated);
        }

        if (crafting is { } craftingValue && Math.Abs(craftingValue - 1.0) > 1e-9)
        {
            // Deliberately not mapped onto Crafting.PointsPerCraftingCycle, which is the near-miss. The old
            // field scaled hideoutConfig.CraftingExpAmount, added once per *alternating* craft in a module;
            // PointsPerCraftingCycle is the separate rate paid for *hours spent crafting*, added a few lines
            // further down the same method in HideoutController. One is not the other, and pointing at it
            // would multiply the wrong crafting XP while looking like it had worked.
            _logger.Warning(
                $"[SkillMultiplier] Your old config set CraftingExpMultiplier = {craftingValue}. That scaled "
                + "crafting XP per alternating craft in a hideout module, which this version has no multiplier "
                + "for. For the same effect set craftingExpAmount in SPT_Data\\configs\\hideout.json directly; "
                + "the Crafting multipliers on the page scale the separate hours-of-crafting rate."
            );
        }

        if (migrated.Count > 0)
        {
            _logger.Success($"[SkillMultiplier] Carried over the old config: {string.Join(", ", migrated)}");
        }

        saveConfig();

        return true;
    }

    /// <summary>
    /// The user declined the migration: drop the legacy fields so the page stops asking, without applying
    /// anything. Explicit and permanent, which is why it only runs from the migrate endpoint.
    /// </summary>
    /// <returns>True when legacy fields were present (and are now gone).</returns>
    public bool Decline(ConfigStore store, string configPath, Action saveConfig)
    {
        if (!Has(configPath))
        {
            return false;
        }

        _logger.Info("[SkillMultiplier] Leaving the previous release's settings unmigrated, as asked.");

        // A save in the new shape is exactly a file without the legacy fields.
        saveConfig();

        return true;
    }

    /// <summary>The config file as JSON, or null when it is missing or malformed. Never throws.</summary>
    public JsonObject? ReadRoot(string configPath)
    {
        try
        {
            if (!File.Exists(configPath))
            {
                return null;
            }

            return JsonNode.Parse(File.ReadAllText(configPath)) as JsonObject;
        }
        catch (Exception ex)
        {
            _logger.Warning($"[SkillMultiplier] Could not read config.json to look for old settings: {ex.Message}");

            return null;
        }
    }

    /// <summary>
    /// The previous release's fields still sitting in config.json. A rewrite of the file - Reset, or any
    /// save - clears what the page shows, never what it cannot see, so callers that rewrite put this back.
    /// </summary>
    public Dictionary<string, double> Snapshot(string configPath)
    {
        var snapshot = new Dictionary<string, double>(StringComparer.OrdinalIgnoreCase);
        var root = ReadRoot(configPath);

        if (root == null)
        {
            return snapshot;
        }

        foreach (var key in new[] { "CraftingExpMultiplier", "HideoutExpMultiplier" })
        {
            if (Value(root, key) is { } value)
            {
                snapshot[key] = value;
            }
        }

        return snapshot;
    }

    /// <summary>
    /// Put back what <see cref="Snapshot"/> took. Never throws: losing the snapshot costs a pending
    /// migration question, which is exactly what this exists to prevent - but failing a save over it would
    /// be worse.
    /// </summary>
    public void Restore(string configPath, Dictionary<string, double> snapshot)
    {
        if (snapshot.Count == 0)
        {
            return;
        }

        try
        {
            if (JsonNode.Parse(File.ReadAllText(configPath)) is not JsonObject root)
            {
                return;
            }

            foreach (var (key, value) in snapshot)
            {
                root[key] = value;
            }

            var tmp = configPath + ".tmp";
            File.WriteAllText(tmp, root.ToJsonString(new JsonSerializerOptions { WriteIndented = true }));
            File.Move(tmp, configPath, overwrite: true);
        }
        catch (Exception ex)
        {
            _logger.Warning($"[SkillMultiplier] Could not keep the previous release's settings: {ex.Message}");
        }
    }

    private void MapLegacy(
        ConfigStore store,
        string skill,
        string field,
        double value,
        List<string> migrated)
    {
        var key = $"Settings.{skill}.{field}";

        if (!Catalog.All.Any(entry => entry.Key == key))
        {
            _logger.Warning($"[SkillMultiplier] No catalog entry for {key}; leaving the old value {value} alone.");
            return;
        }

        // A value typed on the page wins: this fills in what is not already set, it does not impose.
        // Locked inside the store: the only sanctioned in-place write, so it can never tear a
        // concurrent reader's enumeration.
        if (store.SetIfAbsent(key, value))
        {
            migrated.Add($"{key} = {value}");
        }
    }

    private static double? Value(JsonObject root, string name)
    {
        if (root.TryGetPropertyValue(name, out var node)
            && node is JsonValue value
            && value.TryGetValue<double>(out var parsed))
        {
            return parsed;
        }

        return null;
    }
}
