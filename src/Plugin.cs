using System.Collections.Generic;
using BepInEx;
using BepInEx.Configuration;
using BepInEx.Logging;
using SkillMultiplier.Patches;

namespace SkillMultiplier;

/// <summary>
/// Applies the server's per-action XP multipliers inside the game client.
/// <para>
/// The server cannot reach these actions at all: their values are hardcoded in the client's
/// <c>SkillManager</c> rather than read from the <c>globals</c> tables the server owns. So the server
/// publishes a table and this plugin applies it where the numbers actually live.
/// </para>
/// <para>
/// The apply point is a prefix on <c>EFT.Skill.OnTrigger</c>, which is the point of this version: the
/// previous release patched <c>BaseSkill.OnTrigger</c>, one level too late, so the multiplied value never
/// reached the skill's own progress accounting and the green "earned this raid" bar under-reported. See
/// <see cref="SkillOnTriggerPatch"/>.
/// </para>
/// </summary>
[BepInPlugin(Guid, "SkillMultiplier", "2.1.1")]
public class Plugin : BaseUnityPlugin
{
    /// <summary>
    /// The GUID has to stay the previous release's <c>dazzuh.skillmultiplier</c>. BepInEx keys a plugin's
    /// identity, and the name of its config file, off this string rather than off the assembly name - so
    /// changing it would orphan the existing <c>dazzuh.skillmultiplier.cfg</c>, which is both where a
    /// user's old per-skill multipliers live and the thing this update has to read to migrate them.
    /// </summary>
    public const string Guid = "dazzuh.skillmultiplier";

    internal static Plugin Instance;

    internal static ManualLogSource Log;

    /// <summary>
    /// The multipliers the patch applies, keyed by skill and the action's position in that skill's action
    /// array - not by the action object. A raid builds its own <c>SkillManager</c> and its own action
    /// objects, so an instance-keyed map silently stops matching the moment play starts; the skill id and
    /// the index survive that, and they are the same two things the table's keys are written from.
    /// Volatile because it is published by the main-thread rebuild drain and read by the XP patches on
    /// that same thread, while background threads replace the reference wholesale - readers always see a
    /// complete map, never a half-built one.
    /// </summary>
    internal static volatile Dictionary<(EFT.ESkillId Skill, int Index), float> Multipliers = [];

    /// <summary>
    /// The global multiplier from the pushed table, applied on top of every per-action multiplier.
    /// <para>
    /// A plain float rather than part of the map because it is not per-action: it multiplies the gain for
    /// every skill the <see cref="Patches.SkillOnTriggerPatch"/> prefix runs for, including ones with no
    /// row set. Volatile for the same reason as <see cref="Multipliers"/> - written by the table thread,
    /// read on Unity's main thread. Never null, 1.0 means off.
    /// </para>
    /// </summary>
    internal static volatile float GlobalMultiplier = 1f;

    internal static ConfigEntry<bool> Enabled;
    internal static ConfigEntry<bool> Debug;

    private void Awake()
    {
        Instance = this;
        Log = base.Logger;

        Enabled = Config.Bind("General", "Enabled", true, "Apply the multipliers the server pushes.");

        Debug = Config.Bind(
            "Debug",
            "Verbose",
            false,
            "Log each multiplier as it is applied, plus the action catalog, the per-revision mapping "
                + "count and the connection state. Off means only warnings and errors reach the log."
        );

        new SkillOnTriggerPatch().Enable();

        // The hideout gym's payout is scaled where the game computes it: CalculateExperience reads
        // Skills.SkillProgress.Factor(...).FactorValue once and uses that same number for its popup, the skill
        // and the workout's points. Three patches put the multiplier into it - one marks a workout as running,
        // one remembers which skill it is paying, one scales the figure - so all three agree with each other
        // and with what the server pays for the same repetition.
        new WorkoutExperiencePatch().Enable();
        new WorkoutSkillMemoryPatch().Enable();
        new WorkoutPayoutPatch().Enable();

        // Installed once and always on; Plugin.FatigueDisabled is what actually switches the behaviour.
        new SkillFatiguePatch().Enable();

        TableClient.Start();
        TableClient.StartHeartbeat();

        DebugLog($"{Guid} loaded.");
    }

    /// <summary>
    /// Unity calls this on quit. The socket is closed and both background loops are told to stop, so none
    /// of them outlive the quit - see <see cref="TableClient.Shutdown"/>. Kept tiny on purpose: this runs on
    /// the game's main thread mid-shutdown, and anything slow here reads as a hang of its own.
    /// </summary>
    private void OnDestroy()
    {
        TableClient.Shutdown();
    }

    /// <summary>
    /// Whether the fatigue curve is currently floored. Read by <see cref="SkillFatiguePatch"/> on the
    /// game's main thread, written from the websocket thread when the table arrives.
    /// <para>
    /// The patch itself is installed once, at startup, and this flag is the switch - deliberately not a
    /// runtime <c>ModulePatch.Enable()</c>/<c>Disable()</c>. A Harmony detour is not a safe thing to add or
    /// remove while the game's main thread may be inside that very method: doing so against a
    /// debugger-paused process deadlocked the game outright, and the live-thread version of the same
    /// mutation is the same hazard without the safety net. A bool read costs one load per XP event.
    /// </para>
    /// </summary>
    internal static volatile bool FatigueDisabled;

    /// <summary>Set or clear the fatigue floor. Called from the websocket thread as the table arrives.</summary>
    internal static void SetFatigueDisabled(bool disabled)
    {
        if (disabled == FatigueDisabled)
        {
            return;
        }

        FatigueDisabled = disabled;

        DebugLog(
            disabled
                ? "[SkillMultiplier] Skill fatigue disabled: XP gain no longer decays after "
                    + "SkillFreshPoints + SkillPointsBeforeFatigue points in a session."
                : "[SkillMultiplier] Skill fatigue restored to vanilla."
        );
    }

    internal static void DebugLog(string message)
    {
        if (Debug != null && Debug.Value)
        {
            Log.LogMessage(message);
        }
    }
}
