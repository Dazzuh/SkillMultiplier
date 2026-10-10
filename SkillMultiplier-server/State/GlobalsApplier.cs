using SPTarkov.Common.Models.Logging;
using SPTarkov.Server.Core.Models.Spt.Tables;

namespace SkillMultiplier;

/// <summary>
/// Snapshots the untouched globals values once, then scales the live table.
/// <para>
/// Extracted verbatim from <c>SkillMultiplierMod</c> (M5 split). The snapshot
/// invariant is load-bearing: re-capturing after any multiplier has been
/// applied folds the previous multiplier into the new base, which is
/// unrecoverable without a server restart. <see cref="GetBase"/> is lock-free
/// by the same reasoning as before: <c>_base</c> is only ever written during
/// the once-only <see cref="CaptureBase"/> at boot, before any request can
/// arrive.
/// </para>
/// </summary>
internal sealed class GlobalsApplier(
    ISptLogger<SkillMultiplierMod> logger,
    object gate
)
{
    private readonly ISptLogger<SkillMultiplierMod> _logger = logger;
    private readonly object _gate = gate;
    private readonly Dictionary<string, double> _base = new(StringComparer.OrdinalIgnoreCase);

    public int BaseCount
    {
        get
        {
            lock (_gate)
            {
                return _base.Count;
            }
        }
    }

    /// <summary>
    /// Snapshot the untouched values. Only ever runs once, and only before any multiplier has been
    /// applied - re-capturing later would fold the previous multiplier into the new base.
    /// </summary>
    public void CaptureBase(SkillsSettings settings)
    {
        if (_base.Count > 0)
        {
            return;
        }

        foreach (var entry in Catalog.All)
        {
            try
            {
                _base[entry.Key] = entry.Get(settings);
            }
            catch (Exception ex)
            {
                _logger.Warning($"[SkillMultiplier] Could not read base value for {entry.Key}: {ex.Message}");
            }
        }
    }

    /// <summary>Push the current config into the live table. Safe to call repeatedly.</summary>
    public void Apply(SkillsSettings settings, ConfigStore store)
    {
        lock (_gate)
        {
            // One generation for the whole pass: the reference swap is atomic, but reading it twice
            // could mix one generation's switch with another's rows.
            var cfg = store.Config;
            var enabled = cfg.Enabled;
            var changed = 0;

            foreach (var entry in Catalog.All)
            {
                if (!_base.TryGetValue(entry.Key, out var baseValue))
                {
                    continue;
                }

                // Row multiplier only - the global must never enter here. It is applied exactly once, by the
                // client at Skill.OnTrigger; baking it into globals as well would multiply every both-halves
                // skill by it twice, silently.
                var multiplier = enabled ? store.GetMultiplier(cfg, entry.Key) : 1.0;
                var target = baseValue * multiplier;

                try
                {
                    entry.Set(settings, target);

                    if (Math.Abs(multiplier - 1.0) > 1e-9)
                    {
                        changed++;
                    }
                }
                catch (Exception ex)
                {
                    _logger.Warning($"[SkillMultiplier] Could not write {entry.Key}: {ex.Message}");
                }
            }

            _logger.Info(
                $"[SkillMultiplier] Applied {changed} non-default multiplier(s) "
                + $"(mod {(enabled ? "enabled" : "DISABLED, values are vanilla")})."
            );
        }
    }

    public double GetBase(string key) => _base.TryGetValue(key, out var v) ? v : 0.0;
}
