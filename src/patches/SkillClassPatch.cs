using System.Reflection;
using SPT.Reflection.Patching;

namespace SkillMultiplier.Patches
{
    internal class SkillClassPatch : ModulePatch
    {
        protected override MethodBase GetTargetMethod()
        {
            return typeof(SkillClass).GetMethod("OnTrigger", BindingFlags.Instance | BindingFlags.Public | BindingFlags.NonPublic);
        }

        [PatchPrefix]
        private static void Prefix(object skillAction, ref float val, SkillClass __instance)
        {
            var skillIds = SkillMultiplier.Configuration.SkillIds;
            SkillMultiplier.LogDebug($"SkillClassPatch.Prefix called for skill: {__instance.Id}");

            float multiplier = 1f;
            if (skillIds.Contains(__instance.Id.ToString()))
            {
                multiplier = SkillMultiplier.Configuration.GetMultiplier(__instance.Id.ToString());
            }

            var beforeGlobal = multiplier;
            float globalMultiplier = SkillMultiplier.Configuration.GlobalMultiplier.Value;
            multiplier *= globalMultiplier;

            SkillMultiplier.LogDebug($"Skill {__instance.Id} has a multiplier of {beforeGlobal} with a global multiplier of {globalMultiplier} becomes {multiplier}.");

            val *= multiplier;
            SkillMultiplier.LogDebug($"Skill {__instance.Id} value adjusted to {val} after applying multiplier.");
        }
    }

    internal class SkillClassFatiguePatch : ModulePatch
    {
        protected override MethodBase GetTargetMethod()
        {
            return typeof(SkillClass).GetMethod("UseEffectiveness", BindingFlags.Instance | BindingFlags.Public | BindingFlags.NonPublic);
        }

        [PatchPostfix]
        private static void Postfix(SkillClass __instance)
        {
            __instance.float_3 = 1.0f; // Effectiveness
            __instance.float_4 = float.MaxValue; // Fatigue reset timer
        }
    }
}
