using System.Text.Json;
using SPTarkov.Common.Models.Logging;
using SPTarkov.Server.Core.DI;
using SPTarkov.Server.Core.Helpers.Server;

namespace SkillMultiplier;

/// <summary>
/// Owns the tuning state: load, replace, persist, and validate.
/// <para>
/// Extracted from <c>SkillMultiplierMod</c> (M5 split). All state access holds
/// the shared gate: the config reference is swapped wholesale by
/// <see cref="Replace"/>, so an unlocked read can mix generations.
/// Re-entrancy is safe (the gate is a <c>Monitor</c> lock): <c>Apply</c> calls
/// back in while already holding it.
/// </para>
/// </summary>
internal sealed class ConfigStore(
    ISptLogger<SkillMultiplierMod> logger,
    ModHelper modHelper,
    object gate
)
{
    private readonly ISptLogger<SkillMultiplierMod> _logger = logger;
    private readonly ModHelper _modHelper = modHelper;
    private readonly object _gate = gate;

    public SkillMultiplierConfig Config { get; private set; } = new();

    public void Load(string configPath, string modFolder)
    {
        lock (_gate)
        {
        try
        {
            if (File.Exists(configPath))
            {
                Config = _modHelper.GetJsonDataFromFile<SkillMultiplierConfig>(modFolder, "config.json")
                         ?? new SkillMultiplierConfig();
            }
            else
            {
                Config = new SkillMultiplierConfig();
                Save(configPath, modFolder);
            }
        }
        catch (Exception ex)
        {
            // A malformed config must not stop the server from booting: fall back to vanilla.
            _logger.Error($"[SkillMultiplier] config.json could not be read, falling back to vanilla values. {ex.Message}");
            Config = new SkillMultiplierConfig();
        }

        Config.Multipliers = new Dictionary<string, double>(
            Config.Multipliers ?? [],
            StringComparer.OrdinalIgnoreCase
        );

        Config.Actions = new Dictionary<string, double>(
            Config.Actions ?? [],
            StringComparer.OrdinalIgnoreCase
        );

        // A hand-edited config bypasses the save endpoint's validation, so the same bound is enforced here:
        // per-row values are guarded by GetMultiplier at apply time, and the global gets the same treatment
        // at load. The game itself is doubly protected - the client clamps the pushed table again - but the
        // page should never show a value the game will not use.
        Config.GlobalMultiplier = ClampGlobal(Config.GlobalMultiplier);
        }
    }

    public void Replace(SkillMultiplierConfig incoming)
    {
        lock (_gate)
        {
        // Field-by-field on purpose (the incoming record is request data and is not trusted), but it means
        // every new setting must be added here too: a field omitted from this list silently reverts to the
        // record's default, so the UI would accept a change and the server would ignore it.
        Config = new SkillMultiplierConfig
        {
            Enabled = incoming.Enabled,
            DisableFatigue = incoming.DisableFatigue,
            GlobalMultiplier = ClampGlobal(incoming.GlobalMultiplier),
            Multipliers = new Dictionary<string, double>(incoming.Multipliers ?? [], StringComparer.OrdinalIgnoreCase),
            Actions = new Dictionary<string, double>(incoming.Actions ?? [], StringComparer.OrdinalIgnoreCase),
        };
        }
    }

    /// <summary>
    /// One locked read of the whole tuning state: both maps (as copies) and the scalars from a single
    /// generation, so the save path can never mix a pre-swap map with a post-swap scalar.
    /// </summary>
    public (Dictionary<string, double> Multipliers, Dictionary<string, double> Actions,
        bool Enabled, bool DisableFatigue, double GlobalMultiplier) SnapshotAll()
    {
        lock (_gate)
        {
            return (
                new Dictionary<string, double>(Config.Multipliers, StringComparer.OrdinalIgnoreCase),
                new Dictionary<string, double>(Config.Actions, StringComparer.OrdinalIgnoreCase),
                Config.Enabled,
                Config.DisableFatigue,
                Config.GlobalMultiplier
            );
        }
    }

    /// <summary>
    /// Fill-not-overwrite for the legacy migration, under the gate: the only sanctioned in-place write,
    /// so it can never race a reader's enumeration. Returns true when the key was added.
    /// </summary>
    public bool SetIfAbsent(string key, double value)
    {
        lock (_gate)
        {
            return Config.Multipliers.TryAdd(key, value);
        }
    }

    /// <summary>Writes via a temp file and a move, so a crash mid-write cannot leave a truncated config.</summary>
    public void Save(string configPath, string modFolder)
    {
        var json = JsonSerializer.Serialize(Config, new JsonSerializerOptions { WriteIndented = true });
        var tmp = configPath + ".tmp";

        Directory.CreateDirectory(modFolder);
        File.WriteAllText(tmp, json);
        File.Move(tmp, configPath, overwrite: true);
    }

    public double GetMultiplier(SkillMultiplierConfig cfg, string key)
    {
        lock (_gate)
        {
            if (!cfg.Multipliers.TryGetValue(key, out var value))
            {
                return 1.0;
            }

            if (double.IsNaN(value) || double.IsInfinity(value) || value < 0)
            {
                _logger.Warning($"[SkillMultiplier] Ignoring invalid multiplier {value} for {key}; using 1.0.");
                return 1.0;
            }

            return Math.Clamp(value, 0.0, SkillMultiplierMod.MaxMultiplier);
        }
    }

    /// <summary>
    /// The save endpoint's bound, shared with file load: a stored value the page would reject must not
    /// reach the table either. Invalid becomes 1.0 rather than 0 - a corrupt file should read as vanilla,
    /// not as zero XP.
    /// </summary>
    public static double ClampGlobal(double value)
    {
        if (double.IsNaN(value) || double.IsInfinity(value) || value < 0)
        {
            return 1.0;
        }

        return Math.Clamp(value, 0.0, SkillMultiplierMod.MaxMultiplier);
    }
}
