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

    private static readonly ConcurrentDictionary<SkillManager.SkillAction, Observation> ByAction = new();

    private static int _events;

    /// <summary>Total XP events seen, so a report can tell whether anything has changed since the last one.</summary>
    internal static int Events => Volatile.Read(ref _events);

    /// <summary>
    /// Record one event's raw amount. Called on the game's main thread for every XP event, so it is one
    /// concurrent lookup and three field writes, with no allocation after the first event for an action.
    /// </summary>
    internal static void Record(SkillManager.SkillAction action, float value)
    {
        if (action == null)
        {
            return;
        }

        var observation = ByAction.GetOrAdd(action, static _ => new Observation());

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
    internal static Dictionary<SkillManager.SkillAction, (int Count, double Max)> Snapshot()
    {
        var snapshot = new Dictionary<SkillManager.SkillAction, (int, double)>();

        foreach (var (action, observation) in ByAction)
        {
            snapshot[action] = (observation.Count, observation.Max);
        }

        return snapshot;
    }
}
