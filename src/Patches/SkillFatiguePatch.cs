using System.Reflection;
using EFT;
using SPT.Reflection.Patching;

namespace SkillMultiplier.Patches;

/// <summary>
/// Removes skill fatigue: the geometric decay of XP gain as points accrue within a session.
/// <para>
/// <b>Why <c>SkillManager.GetEffectiveness</c> rather than <c>Skill.UseEffectiveness</c>.</b> The game's
/// curve, read from the deployed <c>Assembly-CSharp.dll</c>, is:
/// </para>
/// <code>
/// points &lt; SkillFreshPoints (1)                     -> SkillFreshEffectiveness      (1.3)
/// points &lt; SkillFreshPoints + SkillPointsBeforeFatigue -> 1.0                        (1 + 1 = 2)
/// otherwise                                           -> max(SkillMinEffectiveness,
///                                                            SkillFatiguePerPoint ^ (1 + points - 2))
///                                                                                     (0.6 ^ (points - 1))
/// </code>
/// <para>
/// The previous implementation of this option - inherited from the mod this replaces - post-fixed
/// <c>UseEffectiveness</c> and wrote <c>_effectiveness = 1</c> after each call. That rescues only the
/// <em>first</em> point of an event: <c>UseEffectiveness</c> loops <c>CeilToInt(input)</c> times and
/// recomputes <c>_effectiveness</c> from the points accrued so far whenever the integer part advances,
/// so every later iteration of the same event still banks at the decayed rate. Per-frame ticks (0.04)
/// are a single iteration and looked correct; an event pushed past 1.0 by a multiplier - which is the
/// entire point of this mod - spans several and was being truncated.
/// </para>
/// <para>
/// Flooring the curve's own output fixes every iteration and every caller. Blast radius was checked
/// rather than assumed: <c>GetEffectiveness</c> has exactly three callers - <c>Skill::.ctor</c>,
/// <c>Skill::SetPointsEarnedInSession</c> and <c>Skill::UseEffectiveness</c> - all XP accrual, none of
/// them display code, so nothing shown in the UI changes.
/// </para>
/// <para>
/// The floor is <c>max(1, result)</c> and not a flat 1: <c>SkillFreshEffectiveness</c> (1.3) is a
/// <em>bonus</em> for the first point of a session, and removing fatigue should not also remove it.
/// </para>
/// <para>
/// This also sidesteps the two private fields the old patch wrote. <c>_fatigueTimer</c> had to be
/// pinned to <c>float.MaxValue</c> to stop the reset branch, which as a side effect also disabled that
/// branch's <c>_pointsEarned = min(_pointsEarned, SkillFreshPoints)</c> clamp - a change to the green
/// bar's value that had nothing to do with fatigue.
/// </para>
/// </summary>
internal class SkillFatiguePatch : ModulePatch
{
    protected override MethodBase GetTargetMethod()
    {
        return typeof(SkillManager).GetMethod(
            "GetEffectiveness",
            BindingFlags.Instance | BindingFlags.Public | BindingFlags.NonPublic
        );
    }

    [PatchPostfix]
    private static void Postfix(ref float __result)
    {
        // Installed unconditionally at startup and switched by a flag, so no Harmony detour is ever added
        // or removed while the game is running - see Plugin.FatigueDisabled for why that matters.
        // A comparison rather than Mathf.Max: this project references both the UnityEngine facade and
        // UnityEngine.CoreModule, so naming Mathf is a CS0433 ambiguity.
        if (Plugin.FatigueDisabled && __result < 1f)
        {
            __result = 1f;
        }
    }
}
