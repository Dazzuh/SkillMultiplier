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

        // Subscribe to config change events
        Configuration.Enable.SettingChanged += (sender, args) =>
        {
            ApplyPatches();
        };
        Configuration.DisableFatigue.SettingChanged += (sender, args) =>
        {
            ApplyPatches();
        };

        new MenuScreenPatch().Enable();
        ApplyPatches();
        Logger.LogInfo($"Plugin {MyPluginInfo.PLUGIN_GUID} is loaded!");
    }

    private void ApplyPatches()
    {
        if (Configuration.Enable.Value)
        {
            new SkillClassPatch().Enable();
        }
        else
        {
            new SkillClassPatch().Disable();
        }
        if (Configuration.DisableFatigue.Value && Configuration.Enable.Value)
        {
            new SkillClassFatiguePatch().Enable();
        }
        else
        {
            new SkillClassFatiguePatch().Disable();
        }
    }

    public void logDebug(string message)
    {
        if (Configuration.Debug.Value)
        {
            Logger.LogDebug(message);
        }
    }
}
