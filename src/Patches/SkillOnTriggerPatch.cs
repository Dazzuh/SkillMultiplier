using System;
using System.Reflection;
using EFT;
using SPT.Reflection.Patching;

namespace SkillMultiplier.Patches;

/// <summary>
/// Multiplies skill XP at the point where every consumer of the gain can see it.
/// <para>
/// <b>Why <c>Skill.OnTrigger</c> and not <c>BaseSkill.OnTrigger</c>.</b> <c>Skill.OnTrigger</c> runs these in
/// order (decompiled from the deployed <c>Assembly-CSharp.dll</c>, SPT 4.1.5):
/// </para>
/// <code>
/// public override void OnTrigger(SkillManager.SkillAction skillAction, float val)
/// {
///     SkillManager.SkillProgress.Complete(this, val);   // cross-skill XP
///     if (!skillAction.SimpleCalculation)
///     {
///         val = UseEffectiveness(val);                  // accrues _pointsEarned
///         val = (float)SkillManager.BonusController.Calculate(this, val);
///     }
///     if (base.Level &lt; 9) val = CalculateExpOnFirstLevels(val);
///     base.OnTrigger(skillAction, val);                 // &lt;- BaseSkill: SetCurrent only
/// }
/// </code>
/// <para>
/// <c>UseEffectiveness</c> is what banks the "XP gained this raid" figure that the skill page draws as the
/// green bar (<c>ProgressValue =&gt; PointsEarned =&gt; _pointsEarned</c>), and the client reports that same
/// value to the server in <c>AddChangeSkillExperiencePacket</c>. Scaling at <c>BaseSkill.OnTrigger</c>
/// therefore scales only the real progress while leaving the green bar - and the number the server is told -
/// at the unmultiplied value. Multiplying here instead means the gain is scaled before all three consumers.
/// </para>
/// <para>
/// <b>Deliberate consequence:</b> for levels 0-8 the multiply now happens before the non-linear
/// <c>CalculateExpOnFirstLevels</c> curve, so those levels gain differently than they did when the
/// multiplier was applied after it.
/// </para>
/// </summary>
internal class SkillOnTriggerPatch : ModulePatch
{
    protected override MethodBase GetTargetMethod()
    {
        // Fail fast, not cryptic: a game update that renames this method must surface here at startup,
        // naming the expected signature, rather than as a silent no-op with vanilla XP. Pinned by
        // parameter types, so a future overload surfaces as "not found" rather than AmbiguousMatch.
        return typeof(Skill).GetMethod("OnTrigger", BindingFlags.Instance | BindingFlags.Public | BindingFlags.NonPublic,
                null, new[] { typeof(SkillManager.SkillAction), typeof(float) }, null)
            ?? throw new InvalidOperationException(
                "Skill.OnTrigger (instance) was not found - check the supported game version.");
    }

    [PatchPrefix]
    private static void Prefix(Skill __instance, SkillManager.SkillAction skillAction, ref float val)
    {
        // This prefix runs on the game's main thread, so it is the pump for everything that must read
        // game objects: a requested table rebuild is drained here before any multiplier is applied.
        TableClient.DrainMainThread();

        if (skillAction == null)
        {
            return;
        }

        // Capture what the game is granting before anything of ours touches val. This is the only honest base
        // for an action - see ActionObservations for why FactorValue is not one, and why it is captured even
        // when this mod is off (it describes the game, not us).
        ActionObservations.Record(__instance, skillAction, val);

        if (!Plugin.Enabled.Value)
        {
            return;
        }

        // One lookup against an already-built map. Doing nothing further is the common case: a player with no
        // multipliers configured must pay no measurable cost per XP event. The key is the skill plus the
        // action's position within it, which is what the table's keys are written from - matching on the
        // action object instead looked right and did nothing in a raid, because a raid builds new ones.
        var multipliers = Plugin.Multipliers;
        var multiplier = 1f;

        if (multipliers.Count != 0 && __instance != null && __instance.Actions != null)
        {
            var index = System.Array.IndexOf(__instance.Actions, skillAction);

            if (index >= 0 && multipliers.TryGetValue((__instance.Id, index), out var perAction))
            {
                multiplier = perAction;
            }
        }

        // The global compounds on top of the row, exactly once, here - never in the server's globals, so a
        // skill scaled from both halves cannot count it twice. Skills the server owns outright
        // (Crafting, HideoutManagement) never run through this prefix - their override does not call base -
        // so the global does not reach them; their own rows do.
        var global = Plugin.GlobalMultiplier;

        if (multiplier == 1f && global == 1f)
        {
            return;
        }

        // Captured before the multiply: dividing the scaled value back down prints NaN/Infinity for a
        // zero row, while the baseline is always the honest number.
        var baseline = val;
        val *= multiplier * global;

        if (Plugin.IsDebugEnabled)
        {
            Plugin.DebugLog($"[SkillMultiplier] {__instance.Id} gained {baseline} x{multiplier} x{global} = {val}.");
        }
    }
}
