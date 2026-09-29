using SPTarkov.Server.Core.Models.Utils;

namespace SkillMultiplier;

/// <summary>
/// What the user tunes, and the whole of what is persisted.
/// <para>
/// Only <em>multipliers</em> are stored, never absolute values. The base numbers live in the game's
/// own <c>globals.json</c> and change with game patches; storing a multiplier means a patch that
/// rebalances a skill is picked up automatically instead of being silently reverted by a stale
/// absolute value in this file.
/// </para>
/// <para>
/// Keys are the catalog keys (for example <c>Endurance.SprintAction</c>). Vanilla is <c>1.0</c>, so an
/// empty dictionary is a no-op mod - which is the correct default for something that ships disabled
/// by inaction.
/// </para>
/// </summary>
public sealed record SkillMultiplierConfig : IRequestData
{
    /// <summary>Master switch. False leaves every value at vanilla without discarding the multipliers.</summary>
    public bool Enabled { get; set; } = true;

    /// <summary>
    /// Remove the client's skill fatigue: the geometric decay of XP gain once a session has earned more
    /// than <c>SkillFreshPoints + SkillPointsBeforeFatigue</c> points (1 + 1 in the shipped globals), where
    /// each further point is worth <c>SkillFatiguePerPoint</c> (0.6) of the one before it.
    /// <para>
    /// Defaults to <c>true</c> because the mod this replaces shipped the option enabled - carrying that
    /// over means an existing user's behaviour does not change underneath them on migration.
    /// </para>
    /// <para>
    /// This is client-side regardless of where it is configured: it patches the client's own effectiveness
    /// curve, so the server publishes the flag in the table rather than scaling a globals value. That also
    /// makes it the one setting that applies live, without a game restart.
    /// </para>
    /// </summary>
    public bool DisableFatigue { get; set; } = true;

    /// <summary>Catalog key -> multiplier. Missing keys mean 1.0.</summary>
    public Dictionary<string, double> Multipliers { get; set; } = new(StringComparer.OrdinalIgnoreCase);

    /// <summary>
    /// One multiplier applied on top of every per-row value, by the game client, while it runs.
    /// <para>
    /// Separate from the rows on purpose: it is a single compounding number, not a bulk edit - setting it
    /// to 2.00 doubles whatever each row says. It is applied exactly once, in the client's
    /// <c>Skill.OnTrigger</c> prefix, and never baked into the server's globals, so a skill scaled from
    /// both halves cannot count it twice. Absence means 1.0.
    /// </para>
    /// <para>
    /// The client's patch point is <c>EFT.Skill.OnTrigger</c>, which the server-owned skills
    /// (<c>Crafting</c>, <c>HideoutManagement</c> - a <c>ClientAuthorizedSkill</c> whose override never
    /// calls base) do not run through. The global therefore does not reach those two; their own rows do.
    /// </para>
    /// </summary>
    public double GlobalMultiplier { get; set; } = 1.0;

    /// <summary>
    /// Client-side action key (<c>SkillId[index]</c>) -> multiplier, for actions the server cannot reach
    /// because the client hardcodes their value.
    /// <para>
    /// This is deliberately a <em>separate key space</em> from <see cref="Multipliers"/>. Those keys address
    /// numbers in the server's <c>globals</c> tables. These address an action object in the running
    /// client's <c>SkillManager</c>, and the two do not correspond one-to-one: one action can read several
    /// globals fields (Strength's Min/Max pairs feed a single lerped action), and one globals field can
    /// feed several skills. Deriving one list from the other produces silently wrong pairings.
    /// </para>
    /// </summary>
    public Dictionary<string, double> Actions { get; set; } = new(StringComparer.OrdinalIgnoreCase);
}
