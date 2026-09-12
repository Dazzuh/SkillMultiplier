using System.Reflection;
using SPT.Reflection.Patching;
using static SkillMultiplier.SkillMultiplier;

namespace SkillMultiplier.Patches
{
    internal class SkillClassPatch : ModulePatch
    {
        protected override MethodBase GetTargetMethod()
        {
            return typeof(EFT.BaseSkill).GetMethod("OnTrigger", BindingFlags.Instance | BindingFlags.Public | BindingFlags.NonPublic);
        }

        [PatchPrefix]
        private static void Prefix(object skillAction, ref float val, EFT.BaseSkill __instance)
        {
            var skillIds = SkillMultiplier.Configuration.SkillIds;

            float multiplier = 1f;
            if (skillIds.Contains(__instance.Id.ToString()))
            {
                multiplier = SkillMultiplier.Configuration.GetMultiplier(__instance.Id.ToString());
            }

            var beforeGlobal = multiplier;
            float globalMultiplier = SkillMultiplier.Configuration.GlobalMultiplier.Value;
            multiplier *= globalMultiplier;

            LogDebug($"Skill {__instance.Id} gained exp {val} with a multiplier of {beforeGlobal} and a global multiplier of {globalMultiplier} becomes {multiplier}.");
            val *= multiplier;
            LogDebug($"Skill {__instance.Id} exp adjusted to {val} after applying multiplier.");
        }
    }

    internal class SkillClassFatiguePatch : ModulePatch
    {
        protected override MethodBase GetTargetMethod()
        {
            return typeof(EFT.Skill).GetMethod("UseEffectiveness", BindingFlags.Instance | BindingFlags.Public | BindingFlags.NonPublic);
        }

        [PatchPostfix]
        private static void Postfix(EFT.Skill __instance)
        {
            __instance._effectiveness = 1.0f; // Effectiveness
            __instance._fatigueTimer = float.MaxValue; // Fatigue reset time
        }
    }
}
