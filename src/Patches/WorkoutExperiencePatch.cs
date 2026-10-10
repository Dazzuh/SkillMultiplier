using System;
using System.Reflection;
using EFT.Hideout;
using SPT.Reflection.Patching;

namespace SkillMultiplier.Patches;

/// <summary>
/// Marks the method the hideout gym pays XP from, so <see cref="WorkoutPayoutPatch"/> knows a workout is
/// running.
/// <para>
/// The gym cannot be reached the way every other source is. <c>WorkoutBehaviour.CalculateExperience</c>
/// works out the payout itself and then calls <c>Skill.SetCurrent</c> directly, never going through
/// <c>Skill.OnTrigger</c> - which is the one place <see cref="SkillOnTriggerPatch"/> multiplies. So the
/// workout needs its own hook, and this flag is what keeps it to the workout: the payout patches do
/// nothing unless this is set.
/// </para>
/// <para>
/// This method has exactly one caller, inside the workout itself, so the flag cannot be set by anything
/// else.
/// </para>
/// </summary>
internal class WorkoutExperiencePatch : ModulePatch
{
    /// <summary>
    /// The key index a workout's XP is scaled by, alongside the skill it pays. Not a position in any action
    /// array - a workout is not an action - so the table spells it <c>Strength[Workout]</c>.
    /// </summary>
    internal const int WorkoutIndex = -1;

    [ThreadStatic]
    private static bool _inWorkout;

    /// <summary>True only while the game is inside a workout's payout, on this thread.</summary>
    internal static bool InWorkout => _inWorkout;

    protected override MethodBase GetTargetMethod()
    {
        // Fail fast, not cryptic: a game update that renames this method must surface here at startup,
        // naming the expected signature, rather than as a silent no-op with a vanilla gym. Pinned by
        // parameter types, so a future overload surfaces as "not found" rather than AmbiguousMatch.
        return typeof(WorkoutBehaviour).GetMethod(
            "CalculateExperience",
            BindingFlags.Instance | BindingFlags.Public | BindingFlags.NonPublic,
            null, Type.EmptyTypes, null
        ) ?? throw new InvalidOperationException(
            "WorkoutBehaviour.CalculateExperience (instance) was not found - check the supported game version.");
    }

    [PatchPrefix]
    private static void Prefix()
    {
        // Game thread, like SkillOnTriggerPatch.Prefix: drain a requested rebuild here too, so a table
        // pushed during a workout-only session still lands without waiting for a raid XP event.
        _inWorkout = true;
        TableClient.DrainMainThread();
    }

    /// <summary>
    /// Cleared in a finalizer rather than a postfix: the method returns early when a workout produced no
    /// reward at all, and a postfix would leave the flag set for whatever runs next on this thread. A
    /// thread-static field keeps that from reaching another thread in the meantime. The remembered skill
    /// is cleared too: without this a reward-less workout leaves a stale skill behind for the next one.
    /// </summary>
    [PatchFinalizer]
    private static void Finalizer()
    {
        _inWorkout = false;
        WorkoutSkillMemoryPatch.Forget();
    }
}
