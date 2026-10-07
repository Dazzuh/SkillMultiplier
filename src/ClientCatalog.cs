using System;
using System.Collections.Generic;
using System.Threading;
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
    public string Member { get; set; }

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

    /// <summary>
    /// True when the game refuses to progress this skill from the client. A <c>ClientAuthorizedSkill</c>
    /// overrides <c>OnTrigger</c> with a log-and-return that never calls base, so the client's whole progress
    /// path - our prefix and the global multiplier along with it - is unreachable for that skill. Its
    /// server-side rows are therefore the only lever that does anything.
    /// </summary>
    public bool ServerAuthoritative { get; set; }
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
    /// Guards the stage/send handoff and all report bookkeeping below: staging runs on the game's main
    /// thread while sending runs on the heartbeat thread, so the two race each other without this.
    /// </summary>
    private static readonly object SendGate = new();

    /// <summary>A described action list waiting for the heartbeat thread to send, if one is owed.</summary>
    private static List<ClientActionInfo> _staged;

    private static int _stagedEvents;

    private static long _stagedSignature;

    private static bool _stagedLegacy;

    private static volatile bool _sendRequested;

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
    /// not exist yet when this client first looked, so reporting once at startup missed every skill another mod
    /// adds - and they only appeared if something later forced a reconnect, which made this look
    /// intermittently broken rather than plainly broken.
    /// </para>
    /// <para>
    /// Split across threads by design: <see cref="StageForSend"/> runs on the game's main thread (via
    /// <c>TableClient.DrainMainThread</c>) with an already-described list, and <see cref="SendStaged"/>
    /// runs on the heartbeat thread, where the HTTP send belongs. The handoff and all bookkeeping are
    /// guarded by <c>SendGate</c>, since staging and sending race each other.
    /// </para>
    /// </summary>
    internal static void StageForSend(List<ClientActionInfo> described)
    {
        var actions = described ?? new List<ClientActionInfo>();

        if (actions.Count == 0)
        {
            return;
        }

        var events = ActionObservations.Events;
        var due = Since.ElapsedMilliseconds - Volatile.Read(ref _lastReport) >= ReportIntervalMs;

        lock (SendGate)
        {
            if (!_pending && events == _reportedEvents && !due)
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

            _staged = actions;
            _stagedEvents = events;
            _stagedSignature = signature;
            _stagedLegacy = legacy;
            _sendRequested = true;
        }
    }

    /// <summary>
    /// Send a staged report, if one is owed. Runs on the heartbeat thread: serialization touches only the
    /// staged DTOs and the POST is plain IO, so no game objects are involved.
    /// </summary>
    internal static void SendStaged()
    {
        List<ClientActionInfo> staged;
        int stagedEvents;
        long stagedSignature;
        bool stagedLegacy;

        lock (SendGate)
        {
            if (!_sendRequested || _staged == null)
            {
                return;
            }

            // Stamped before the attempt, so a server that is refusing the request is retried on the interval
            // rather than on every heartbeat tick.
            _lastReport = Since.ElapsedMilliseconds;

            staged = _staged;
            stagedEvents = _stagedEvents;
            stagedSignature = _stagedSignature;
            stagedLegacy = _stagedLegacy;
            _staged = null;
            _sendRequested = false;
        }

        try
        {
            RequestHandler.PostJson(Path, JsonConvert.SerializeObject(new ClientCatalogReport
            {
                Actions = staged,
                LegacyDetected = stagedLegacy,
            }));

            lock (SendGate)
            {
                _pending = false;
                _reportedEvents = stagedEvents;
                _reportedSignature = stagedSignature;
                _reportedLegacy = stagedLegacy;
            }

            Plugin.DebugLog(
                $"[SkillMultiplier] Reported {staged.Count} tunable action(s) to the server "
                    + $"({stagedEvents} XP event(s) observed so far)."
            );
        }
        catch (Exception ex)
        {
            // Not fatal: multipliers apply from the pushed table whether or not the server has the list.
            // Bookkeeping is untouched, so the next heartbeat stages and retries.
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
