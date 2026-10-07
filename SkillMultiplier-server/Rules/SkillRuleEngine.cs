using SPTarkov.Common.Models.Logging;
using SPTarkov.Server.Core.Models.Enums;
using SPTarkov.Server.Core.Services.Locales;

namespace SkillMultiplier;

/// <summary>
/// The mod's single rule about who scales what: skill identity, the
/// server-grant decision, and the locale-backed display names.
/// <para>
/// Extracted verbatim from <c>SkillMultiplierMod</c> (M5 split). These stay
/// together on purpose: <see cref="ServerGrantMultiplier"/> joins the owned
/// sets, the catalog, and the config under one gate, and splitting the
/// squaring guard away from the grant math is how double-application bugs
/// are born. The locale cache relies on atomic reference assignment rather
/// than a lock, as before.
/// </para>
/// </summary>
internal sealed class SkillRuleEngine(
    ISptLogger<SkillMultiplierMod> logger,
    LocaleService localeService,
    object gate
)
{
    private readonly ISptLogger<SkillMultiplierMod> _logger = logger;
    private readonly LocaleService _localeService = localeService;
    private readonly object _gate = gate;

    /// <summary>Resolved once, on first use - see <see cref="LocaleValue"/>.</summary>
    private Dictionary<string, string>? _localeDb;

    /// <summary>
    /// The base game's skills whose XP the client cannot progress, so nothing client-side can scale them. A
    /// <c>ClientAuthorizedSkill</c> overrides <c>OnTrigger</c> to log and return without calling base. These
    /// cannot be discovered without asking, hence listed.
    /// </summary>
    public static readonly HashSet<string> ServerOwnedNames = new(StringComparer.OrdinalIgnoreCase)
    {
        "Crafting",
        "HideoutManagement",
        "WeaponTreatment",
    };

    /// <summary>
    /// Skills whose server-paid XP is computed from globals fields this mod scales in place: crafting
    /// payouts derive from the <c>Crafting.Points*</c> rows, hideout payouts from the
    /// <c>HideoutManagement.SkillPoints*</c> rows. For these, a scaled row means the grant already
    /// carries the multiplier - multiplying the grant as well would square it.
    /// <para>
    /// Every other server-paid source - repairs (computed from <c>repair.json</c>, not from globals)
    /// and quest rewards (fixed amounts) - is independent of the scaled fields, so those grants follow
    /// the skill's row. Suppressing them too, as the old per-skill guard did, left e.g. repair Intellect
    /// vanilla against user intent.
    /// </para>
    /// </summary>
    public static readonly HashSet<string> GrantsDerivedFromScaledGlobals = new(StringComparer.OrdinalIgnoreCase)
    {
        "Crafting",
        "HideoutManagement",
    };

    /// <summary>
    /// Equality within the 1e-9 epsilon the save path already uses: doubles that differ by less than that
    /// count as the same value, so hand-edited dust never reads as disagreement.
    /// </summary>
    public sealed class EpsilonDoubleComparer : IEqualityComparer<double>
    {
        public bool Equals(double x, double y) => Math.Abs(x - y) < 1e-9;

        public int GetHashCode(double value) => 0;
    }

    private readonly HashSet<string> _ambiguousGrantWarned = new(StringComparer.OrdinalIgnoreCase);

    /// <summary>
    /// Every skill the client cannot apply a multiplier to: the names known without asking, plus whatever
    /// connected clients have reported. Read by the catalog build and by the repair patch.
    /// </summary>
    public HashSet<string> ServerOwnedSkills(HashSet<string> reportedUnauthorized)
    {
        lock (_gate)
        {
            return new HashSet<string>(
                ServerOwnedNames.Concat(reportedUnauthorized),
                StringComparer.OrdinalIgnoreCase
            );
        }
    }

    /// <summary>
    /// The client-side id for a server <c>SkillTypes</c> value: the name the game client itself reports the
    /// skill under (<c>ESkillId</c>), which is what catalog skills, client action keys and config keys are
    /// all written in.
    /// <para>
    /// Resolved explicitly rather than by <c>ToString()</c>: if SPT ever renames a <c>SkillTypes</c> member
    /// without the client's <c>ESkillId</c> following, grants would silently stay vanilla. The map covers
    /// every skill this mod knows - the catalog's skills plus the server-owned names - and anything else
    /// falls back to <c>ToString()</c> with a one-time log line, so a newly grantable skill is visible
    /// rather than silently unmatched.
    /// </para>
    /// </summary>
    private static readonly HashSet<string> _unmappedSkillLogged = new(StringComparer.OrdinalIgnoreCase);

    public string ClientSkillId(SkillTypes skill)
    {
        var name = skill.ToString();

        foreach (var entry in Catalog.All)
        {
            if (entry.Skill.Equals(name, StringComparison.OrdinalIgnoreCase))
            {
                return entry.Skill;
            }
        }

        foreach (var owned in ServerOwnedNames)
        {
            if (owned.Equals(name, StringComparison.OrdinalIgnoreCase))
            {
                return owned;
            }
        }

        if (AddUnmappedSkill(name))
        {
            _logger.Debug(
                $"[SkillMultiplier] No catalog skill matches server skill '{name}': grant lookups fall back "
                + "to the enum name, which only works while SPT's SkillTypes names match the client's ids."
            );
        }

        return name;
    }

    private static bool AddUnmappedSkill(string name)
    {
        lock (_unmappedSkillLogged)
        {
            return _unmappedSkillLogged.Add(name);
        }
    }

    /// <summary>
    /// The multiplier that applies to skill XP the server grants itself, which today is the repair path.
    /// <para>
    /// Resolved from the skill's own row, because that is the number a user set: the single value configured
    /// for that skill, whether it lives in the client's key space (<c>LightVests[0]</c>) or in the server's
    /// (<c>Settings.WeaponTreatment.SkillPointsPerRepair</c>). The global multiplier rides on top for a skill
    /// the client can scale, which is exactly what the page's math column shows for that row, and stays out
    /// for a server-owned skill, which is the same rule the page uses.
    /// </para>
    /// <para>
    /// Null when the skill has no row set, when its rows disagree, or when a row of its own still scales a
    /// globals value the grant itself is computed from (crafting/hideout) - in that case the server derives
    /// the XP from a number this mod has already scaled, and multiplying the grant too would square it. Two
    /// different numbers for one skill mean there is no single answer, and picking one of them - the first,
    /// the largest, their product - would be a guess the page cannot show the user. Either way the grant is
    /// left at vanilla, and says so once in the log.
    /// </para>
    /// </summary>
    public double? ServerGrantMultiplier(SkillTypes skill, ConfigStore store)
    {
        var skillId = ClientSkillId(skill);
        var values = new HashSet<double>();

        lock (_gate)
        {
            if (!store.Config.Enabled)
            {
                return 1.0;
            }

            foreach (var (key, value) in store.Config.Actions)
            {
                // Workout rows are gym payouts, not skill XP: they must not join the single-number verdict
                // for the skill's grants - a differing Workout row would otherwise leave real grants
                // vanilla. Same exemption the save path gives unreported keys.
                if (key.EndsWith("[Workout]", StringComparison.OrdinalIgnoreCase))
                {
                    continue;
                }

                if (key.StartsWith(skillId + "[", StringComparison.OrdinalIgnoreCase))
                {
                    values.Add(value);
                }
            }

            var coveredByValueScaling = false;

            // Only skills whose grants are computed from scaled globals fields can square: for those, a
            // scaled row means the grant already carries the multiplier. Repair and quest grants are
            // computed elsewhere, so their skills' rows still apply.
            var grantDerivesFromScaledGlobals = GrantsDerivedFromScaledGlobals.Contains(skillId);

            foreach (var entry in Catalog.All)
            {
                if (!entry.Skill.Equals(skillId, StringComparison.OrdinalIgnoreCase)
                    || !store.Config.Multipliers.TryGetValue(entry.Key, out var value))
                {
                    continue;
                }

                values.Add(value);

                // A row still scaling a globals value means the server computes this skill's XP from a number
                // this mod has already scaled. Multiplying the grant as well would square it.
                if (!entry.AppliedAtServerGrant && grantDerivesFromScaledGlobals)
                {
                    coveredByValueScaling = true;
                }
            }

            if (coveredByValueScaling)
            {
                return null;
            }

            if (values.Count == 0)
            {
                return null;
            }

            // Collapse near-equal values with the same 1e-9 epsilon the save path uses
            // (SkillMultiplierRouter): 2.0 vs 2.0000001 from a hand-edited file is one value with dust
            // on it, not a disagreement worth leaving the grant vanilla over.
            var distinct = values.Distinct(new EpsilonDoubleComparer()).ToList();

            if (distinct.Count > 1)
            {
                WarnAboutAmbiguousGrant(skillId, values);
                return null;
            }

            var row = distinct.Single();
            var global = double.IsNaN(store.Config.GlobalMultiplier)
                ? 1.0
                : Math.Clamp(store.Config.GlobalMultiplier, 0.0, SkillMultiplierMod.MaxMultiplier);

            return ServerOwnedNames.Contains(skillId) ? row : row * global;
        }
    }

    /// <summary>
    /// Once per skill per distinct disagreement, not once per repair: a session can hold many repairs and
    /// the answer has not changed in between, so repeating it would be noise rather than information. But
    /// it re-arms: when the user unifies the rows (or creates a new disagreement), the changed value set
    /// warns again, and replacing the config clears the record so a save always gets a fresh voice.
    /// </summary>
    private void WarnAboutAmbiguousGrant(string skillId, HashSet<double> values)
    {
        var fingerprint = skillId + "|" + string.Join(",", values.OrderBy(v => v).Select(v => v.ToString("R")));

        if (!_ambiguousGrantWarned.Add(fingerprint))
        {
            return;
        }

        _logger.Warning(
            $"[SkillMultiplier] {skillId} has {values.Count} different multipliers set "
            + $"({string.Join(", ", values.OrderBy(v => v))}), so XP the server grants for it is left at "
            + "vanilla: there is no single number to apply. Set that skill's rows to one value and its repair "
            + "XP will follow it."
        );
    }

    public void ClearAmbiguityRecord() => _ambiguousGrantWarned.Clear();

    /// <summary>
    /// The game's own name for a skill - "Field Medicine" rather than the <c>ESkillId</c> name the client
    /// reports. Null when the locale has nothing for it, which is the honest answer for a skill that exists
    /// only inside some mod with no locale entries of its own.
    /// </summary>
    public string? SkillDisplayName(string skillId) => LocaleValue(skillId);

    /// <summary>
    /// The game's description for a skill, under the <c>&lt;SkillId&gt;Description</c> key it uses for skill
    /// tooltips. Null when it has none.
    /// </summary>
    public string? SkillDescription(string skillId) => LocaleValue(skillId + "Description");

    /// <summary>
    /// A locale lookup, held rather than re-fetched: <see cref="LocaleService.GetLocaleDb"/> merges the locale
    /// sources into a new dictionary, so calling it per request would rebuild tens of thousands of entries on
    /// every catalog fetch. The database is complete by the time a request can arrive and does not change after
    /// that. Two threads racing the assignment is harmless - reference assignment is atomic, and both
    /// dictionaries are equivalent.
    /// </summary>
    private string? LocaleValue(string key)
    {
        var db = _localeDb ??= _localeService.GetLocaleDb();

        return db.TryGetValue(key, out var value) && !string.IsNullOrWhiteSpace(value) ? value : null;
    }
}
