using System.Reflection;
using EFT;
using EFT.UI;
using EFT.UI.Matchmaker;
using SPT.Reflection.Patching;


namespace SkillMultiplier.Patches
{
    internal class MenuScreenPatch : ModulePatch
    {
        protected override MethodBase GetTargetMethod()
        {
            return typeof(MenuScreen).GetMethod("Show", [typeof(Profile), typeof(MatchmakerPlayersController), typeof(ESessionMode)]);
        }

        private static bool _configGenerated;
        [PatchPostfix]
        private static void Postfix()
        {
            if (_configGenerated) return;
            SkillMultiplier.Configuration.GenerateConfig();
            _configGenerated = true;
        }
    }
}
