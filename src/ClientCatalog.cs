using System;
using System.Collections.Generic;
using EFT;
using Newtonsoft.Json;
using SPT.Common.Http;

namespace SkillMultiplier;

/// <summary>
/// What this client sends the server about its own action list.
/// <para>
/// The server cannot enumerate these itself. An action exists only inside the running client's
/// <c>SkillManager</c>, so unless the client says what it has, the UI has nothing to render and a
/// client-keyed multiplier can only be set by hand-posting JSON. This is that introduction.
/// </para>
/// <para>
/// It is deliberately one-way and advisory: multipliers are applied by the client from the table the
/// server pushes, so a report that never arrives costs the UI its list and costs nothing else. For the
/// same reason the server treats it as a claim rather than a contract - a key the client does not
/// recognise is ignored at apply time, so a stale or Fika-mismatched report cannot break anything.
/// </para>
/// </summary>
internal sealed class ClientCatalogReport
{
    public string Type { get; set; } = "catalog";

    public List<ClientActionInfo> Actions { get; set; } = [];

    /// <summary>
    /// Whether this install still holds the previous release's config. The page asks about migrating it;
    /// the actions above are unaffected either way.
    /// </summary>
    public bool LegacyDetected { get; set; }
}

internal sealed class ClientActionInfo
{
    /// <summary><c>SkillId[index]</c> - see <see cref="ActionCatalog"/> for why the index is sound.</summary>
    public string Key { get; set; } = string.Empty;

    public string Skill { get; set; } = string.Empty;

    public int Index { get; set; }

    /// <summary>
    /// The name the game itself gives this action: the public <c>SkillManager</c> field it lives in
    /// (<c>SprintAction</c>, <c>ExamineAction</c>). Null when the action is not in one - an action a mod adds
    /// without following that arrangement. This is the only per-action name that exists, so anything built
    /// from it is naming an action, not interpreting one.
    /// </summary>
    public string? Member { get; set; }

    /// <summary>
    /// The action's own coefficient as the game reports it. Kept for reference only: it is <em>not</em> the
    /// amount granted, and is 1 for several actions. See <see cref="ClientActionInfo.ObservedXp"/>.
    /// </summary>
    public double Factor { get; set; }

    /// <summary>
    /// The highest amount the game has granted this action this session, before any multiplier, and how many
    /// events that figure rests on. An <see cref="ObservedCount"/> of zero means the action has not paid out
    /// yet, so there is nothing honest to show as its base.
    /// </summary>
    public double ObservedXp { get; set; }

    public int ObservedCount { get; set; }
}

/// <summary>Sends <see cref="ClientCatalogReport"/> once the profile - and so the skill list - exists.</summary>
internal static class ClientCatalogReporter
{
    private const string Path = "/skillmultiplier/api/clientcatalog";

    /// <summary>
    /// Minimum gap between reports. XP arrives every frame and each report carries the whole action list, so
    /// re-reporting per event would be absurd; this is how long the observed amounts can lag the UI by.
    /// </summary>
    private const int ReportIntervalMs = 20_000;

    private static volatile bool _pending = true;

    /// <summary>
    /// Monotonic, so the throttle cannot be disturbed by the system clock, and <c>System.Diagnostics</c> rather
    /// than <c>Environment.TickCount64</c> - the latter does not exist on the client's runtime
    /// (netstandard2.1 under Unity's Mono), which is how this was found.
    /// </summary>
    private static readonly System.Diagnostics.Stopwatch Since = System.Diagnostics.Stopwatch.StartNew();

    private static long _lastReport = -ReportIntervalMs;
    private static int _reportedEvents;

    /// <summary>What the server was last told about the legacy config - see below.</summary>
    private static bool _reportedLegacy;

    /// <summary>
    /// Fingerprint of the last reported action set. The set is not fixed for the life of the process: another
    /// mod can register its skills after this client first looked. Sentinel so the first comparison differs.
    /// </summary>
    private static long _reportedSignature = long.MinValue;

    /// <summary>
    /// Queue a report. Called at startup and on every successful connection, because the server holds this
    /// per session: a server restart empties it, and without a re-report the UI would show no client actions
    /// until the game was restarted too. Re-reporting is idempotent - it replaces this session's set.
    /// </summary>
    internal static void RequestReport() => _pending = true;

    /// <summary>
    /// Report when the server has not heard from this client, when the game has granted XP since the last
    /// report, or when the set of actions has changed.
    /// <para>
    /// The set check is why this runs on the interval rather than only on events. A Cecil-injected skill does
    /// not exist yet when this client first looks, so reporting once at startup missed every skill another mod
    /// adds - and they only appeared if something later forced a reconnect, which made this look
    /// intermittently broken rather than plainly broken.
    /// </para>
    /// </summary>
    internal static void Flush(SkillManager manager)
    {
        if (manager == null)
        {
            return;
        }

        var events = ActionObservations.Events;
        var due = Since.ElapsedMilliseconds - _lastReport >= ReportIntervalMs;

        if (!_pending && events == _reportedEvents && !due)
        {
            return;
        }

        // Stamped before the attempt, so a server that is refusing the request is retried on the interval
        // rather than on every heartbeat tick.
        _lastReport = Since.ElapsedMilliseconds;

        try
        {
            var actions = ActionCatalog.Describe(manager);

            if (actions.Count == 0)
            {
                return;
            }

            var signature = Signature(actions);
            var legacy = LegacyConfig.HasLegacyConfig();

            if (!_pending && signature == _reportedSignature && events == _reportedEvents && legacy == _reportedLegacy)
            {
                // The interval elapsed and neither the list, the observed amounts nor the legacy answer
                // have moved.
                return;
            }

            RequestHandler.PostJson(Path, JsonConvert.SerializeObject(new ClientCatalogReport
            {
                Actions = actions,
                LegacyDetected = legacy,
            }));
            _pending = false;
            _reportedEvents = events;
            _reportedSignature = signature;
            _reportedLegacy = legacy;

            Plugin.Log.LogInfo(
                $"[SkillMultiplier] Reported {actions.Count} tunable action(s) to the server "
                    + $"({events} XP event(s) observed so far)."
            );
        }
        catch (Exception ex)
        {
            // Not fatal: multipliers apply from the pushed table whether or not the server has the list.
            Plugin.Log.LogWarning($"[SkillMultiplier] Could not report the action catalog: {ex.Message}");
        }
    }

    /// <summary>
    /// A fingerprint of the action set, so "has the list changed?" is one comparison. The count is included so
    /// a skill losing one action reads differently from nothing changing. Enumeration order is stable within a
    /// process but not guaranteed to be, and a spurious difference costs one extra report and nothing else.
    /// </summary>
    private static long Signature(List<ClientActionInfo> actions)
    {
        long hash = actions.Count;

        foreach (var action in actions)
        {
            hash = (hash * 31) + action.Key.GetHashCode();
        }

        return hash;
    }
}
