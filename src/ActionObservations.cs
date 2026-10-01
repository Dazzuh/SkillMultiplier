using System.Collections.Concurrent;
using System.Collections.Generic;
using System.Threading;
using EFT;

namespace SkillMultiplier;

/// <summary>
/// What the game itself grants each action, measured rather than inferred.
/// <para>
/// This exists because the obvious candidate is wrong. <c>SkillManager.SkillAction.FactorValue</c> looks like
/// the action's vanilla amount, and for most actions it is - measured on one client: <c>Perception[1]</c>
/// reports 0.1336, which is its hardcoded <c>0.334</c> times the globals' <c>SkillProgressRate</c> of 0.4, and
/// <c>HideoutManagement[1]</c> reports 12, which is <c>SkillPointsPerAreaUpgrade</c> 30 times 0.4. But it is
/// not the amount for every action: the field is <em>reassigned</em> to <c>SkillProgressRate</c> for
/// dependency-based actions, and left at its default of <c>1</c> for the ones whose value is computed and
/// passed in at invoke time. Measured: <c>Endurance[0]</c> and <c>Endurance[1]</c> report 1 while the globals
/// values they actually read are <c>SprintAction</c> 0.04 and <c>MovementAction</c> 0.005, and so do
/// <c>Strength[0..2]</c> - the lerped Min/Max pairs - and <c>Crafting[0]</c>.
/// </para>
/// <para>
/// The value handed to <c>Skill.OnTrigger</c> has no such exception: it is the amount, for every kind of
/// action. This plugin already sees it on every event, so it is recorded there - before anything of ours
/// touches it - and reported to the server for display.
/// </para>
/// </summary>
internal static class ActionObservations
{
    internal sealed class Observation
    {
        public int Count;

        public double Total;

        /// <summary>
        /// The highest amount seen is kept rather than the average: a session carries the conditions of the
        /// moment - fatigue, weight, range, weapon - so an average drifts toward whatever the player happened
        /// to be doing, while the highest amount is the action's real per-event value at full rates.
        /// </summary>
        public double Max;
    }

    /// <summary>
    /// Keyed by skill and the action's position in that skill's action array, never by the action object
    /// itself: a raid builds fresh action objects, so an instance-keyed dictionary records nothing once play
    /// starts - which is what it did.
    /// </summary>
    private static readonly ConcurrentDictionary<(EFT.ESkillId Skill, int Index), Observation> ByAction = new();

    private static int _events;

    /// <summary>Total XP events seen, so a report can tell whether anything has changed since the last one.</summary>
    internal static int Events => Volatile.Read(ref _events);

    /// <summary>
    /// Record one event's raw amount. Called on the game's main thread for every XP event, so it is one
    /// concurrent lookup and three field writes, with no allocation after the first event for an action.
    /// The skill is passed in because the key is built from it - the action alone cannot say which skill
    /// fired it, and it is a different object in every raid.
    /// </summary>
    internal static void Record(Skill skill, SkillManager.SkillAction action, float value)
    {
        if (skill == null || action == null || skill.Actions == null)
        {
            return;
        }

        var index = System.Array.IndexOf(skill.Actions, action);

        if (index < 0)
        {
            return;
        }

        var observation = ByAction.GetOrAdd((skill.Id, index), static _ => new Observation());

        observation.Count++;
        observation.Total += value;

        if (value > observation.Max)
        {
            observation.Max = value;
        }

        Interlocked.Increment(ref _events);
    }

    /// <summary>
    /// A copy for the reporting thread. The entries are read without a lock: these are telemetry values, a
    /// torn read would misreport one figure in one report, and locking on the game's main thread per XP event
    /// to prevent that would be the wrong trade.
    /// </summary>
    internal static Dictionary<(EFT.ESkillId Skill, int Index), (int Count, double Max)> Snapshot()
    {
        var snapshot = new Dictionary<(EFT.ESkillId, int), (int, double)>();

        foreach (var (key, observation) in ByAction)
        {
            snapshot[key] = (observation.Count, observation.Max);
        }

        return snapshot;
    }
}
