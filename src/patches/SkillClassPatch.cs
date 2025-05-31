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
        private static void Prefix(ref float val, SkillClass instance)
        {
            var skillIds = SkillMultiplier.Configuration.SkillIds;
            SkillMultiplier.LogDebug($"SkillClassPatch.Prefix called for skill: {instance.Id}");

            float multiplier = 1f;
            if (skillIds.Contains(instance.Id.ToString()))
            {
                multiplier = SkillMultiplier.Configuration.GetMultiplier(instance.Id.ToString());
            }

            var beforeGlobal = multiplier;
            float globalMultiplier = SkillMultiplier.Configuration.GlobalMultiplier.Value;
            multiplier *= globalMultiplier;

            SkillMultiplier.LogDebug($"Skill {instance.Id} has a multiplier of {beforeGlobal} with a global multiplier of {globalMultiplier} becomes {multiplier}.");

            val *= multiplier;
            SkillMultiplier.LogDebug($"Skill {instance.Id} value adjusted to {val} after applying multiplier.");
        }
    }

    internal class SkillClassFatiguePatch : ModulePatch
    {
        protected override MethodBase GetTargetMethod()
        {
            return typeof(SkillClass).GetMethod("UseEffectiveness", BindingFlags.Instance | BindingFlags.Public | BindingFlags.NonPublic);
        }

        [PatchPostfix]
        private static void Postfix(SkillClass instance)
        {
            instance.float_3 = 1.0f; // Effectiveness
            instance.float_4 = float.MaxValue; // Fatigue reset timer
        }
    }
}
