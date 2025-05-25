using BepInEx;
using BepInEx.Logging;
using SkillMultiplier.Configuration;
using SkillMultiplier.Patches;

namespace SkillMultiplier;

[BepInPlugin(MyPluginInfo.PLUGIN_GUID, MyPluginInfo.PLUGIN_NAME, MyPluginInfo.PLUGIN_VERSION)]
public class SkillMultiplier : BaseUnityPlugin
{
    public static SkillMultiplier Instance { get; private set; }
    internal static new ManualLogSource Logger;
    public static Config Configuration { get; private set; }

    private void Awake()
    {
        Instance = this;
        Logger = base.Logger;
        Configuration = new Config(base.Config);

        new SkillClassPatch().Enable();
        new MenuScreenPatch().Enable();
        new SkillClassFatiguePatch().Enable();
        Logger.LogInfo($"Plugin {MyPluginInfo.PLUGIN_GUID} is loaded!");
    }

    public void logDebug(string message)
    {
        if (Configuration.Debug.Value)
        {
            Logger.LogDebug(message);
        }
    }
}
