using System;
using System.Reflection;
using EFT;
using SPT.Reflection.Patching;

namespace SkillMultiplier.Patches;

/// <summary>
/// Remembers which skill the gym is paying, so <see cref="WorkoutPayoutPatch"/> can scale the right row.
/// <para>
/// The workout picks its reward effect - and therefore its skill - at random per repetition, and holds it in a
/// local. It does fetch the skill through <c>SkillManager.GetSkill</c> a few lines before it asks for the
/// payout, which is the only place the choice is visible to a patch. Kept only while the workout flag is set,
/// so this costs one bool read on the thousands of <c>GetSkill</c> calls the rest of the game makes.
/// </para>
/// </summary>
internal class WorkoutSkillMemoryPatch : ModulePatch
{
    [ThreadStatic]
    private static ESkillId _skill;

    [ThreadStatic]
    private static bool _known;

    protected override MethodBase GetTargetMethod()
    {
        return typeof(SkillManager).GetMethod(
            "GetSkill",
            BindingFlags.Instance | BindingFlags.Public,
            null,
            new[] { typeof(ESkillId) },
            null
        );
    }

    [PatchPostfix]
    private static void Postfix(Skill __result)
    {
        if (!WorkoutExperiencePatch.InWorkout || __result == null)
        {
            return;
        }

        _skill = __result.Id;
        _known = true;
    }

    /// <summary>
    /// The skill the current workout is paying. Only meaningful while
    /// <see cref="WorkoutExperiencePatch.InWorkout"/> is set; a stale value from the previous workout is
    /// harmless, because the workout fetches its skill again before it asks for a payout.
    /// </summary>
    internal static bool TryGet(out ESkillId skill)
    {
        skill = _skill;
        return _known;
    }
}

/// <summary>
/// Makes the gym's own numbers true, rather than correcting them afterwards.
/// <para>
/// <c>WorkoutBehaviour.CalculateExperience</c> gets its figure from
/// <c>Skills.SkillProgress.Factor(num4, true).FactorValue</c> and then uses that same value three times: to
/// build the "Skill 'X' increased by N" popup, to set the skill, and to add the workout's points. Scaling
/// where the game computes it means the popup, the skill and the points all carry the multiplier and agree
/// with each other - and with what the server pays, which is the same rule applied server-side.
/// </para>
/// <para>
/// This is the one place the multiplier can be applied and still reach the popup: the number is not read back
/// from what the skill gained, so scaling the applied value alone would leave the popup saying 1.6 while 8.0
/// landed. Only runs while the workout flag is set, which is what keeps it off the same method's many other
/// callers, none of which are inside a workout.
/// </para>
/// </summary>
internal class WorkoutPayoutPatch : ModulePatch
{
    protected override MethodBase GetTargetMethod()
    {
        return typeof(SkillManager.SkillAction).GetMethod(
            "Factor",
            BindingFlags.Instance | BindingFlags.Public,
            null,
            new[] { typeof(float), typeof(bool) },
            null
        );
    }

    [PatchPostfix]
    private static void Postfix(SkillManager.SkillAction __result)
    {
        if (!WorkoutExperiencePatch.InWorkout || __result == null)
        {
            return;
        }

        if (!Plugin.Enabled.Value)
        {
            return;
        }

        if (!WorkoutSkillMemoryPatch.TryGet(out var skillId))
        {
            return;
        }

        // Default 1.0 so a global-only change still reaches the gym: mirrors SkillOnTriggerPatch, where
        // the row defaults the same way and the global compounds on top. Returning only when both are 1
        // keeps the two taps in agreement and matches the UI math column. (TryGetValue writes default(float)
        // into out on a miss, so the row goes through a temp instead of doubling as the default.)
        var multiplier = 1f;

        if (Plugin.Multipliers.TryGetValue((skillId, WorkoutExperiencePatch.WorkoutIndex), out var row))
        {
            multiplier = row;
        }

        var global = Plugin.GlobalMultiplier;

        if (multiplier == 1f && global == 1f)
        {
            return;
        }

        var vanilla = __result.FactorValue;
        __result.FactorValue = vanilla * multiplier * global;

        Plugin.DebugLog(
            $"[SkillMultiplier] {skillId} workout payout shown as {__result.FactorValue} "
                + $"({vanilla} x{multiplier} x{global})."
        );
    }
}
