using System.Reflection;
using EFT;
using EFT.UI;
using SPT.Reflection.Patching;
using SPT.Reflection.Utils;


namespace SkillMultiplier.Patches
{
    internal class MenuScreenPatch : ModulePatch
    {
        protected override MethodBase GetTargetMethod()
        {
            return typeof(MenuScreen).GetMethod("Show", [typeof(Profile), typeof(MatchmakerPlayerControllerClass), typeof(ESessionMode)]);
        }

        private static bool configGenerated = false;
        [PatchPostfix]
        private static void Postfix()
        {
            if (configGenerated) return;
            SkillMultiplier.Configuration.GenerateConfig();
            configGenerated = true;
        }
    }
}
