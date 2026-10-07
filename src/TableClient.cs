using System;
using System.Collections.Generic;
using System.Threading;
using Newtonsoft.Json;
using SPT.Common.Http;

namespace SkillMultiplier;

/// <summary>
/// Keeps the client's multiplier table in step with the server.
/// <para>
/// Sync is push-based: the server hosts a websocket (SPT enables <c>app.UseWebSockets()</c> and routes any
/// connection whose path contains a handler's hook URL to it), and this plugin holds one open for the
/// session. The server sends the table on connect and again whenever the UI saves, so a change reaches
/// every connected client - including each player in a Fika co-op session - without any polling.
/// </para>
/// <para>
/// A one-shot HTTP fetch runs first as a fallback, so the multipliers are still applied if the websocket
/// cannot be established. It is not a poll; it happens once, and the socket keeps it current afterwards.
/// </para>
/// <para>
/// No port or certificate handling appears here on purpose: <see cref="RequestHandler"/> resolves the host
/// and session id from the process arguments SPT launched the game with, and accepts the self-signed cert.
/// </para>
/// <para>
/// Threading contract: the socket and heartbeat threads parse and store pushed data only - they never
/// touch game objects. Everything that reads the live <c>SkillManager</c> (resolving it, walking action
/// arrays, reading <c>FactorValue</c> getters, reflective member mapping) runs in
/// <see cref="DrainMainThread"/>, which only ever executes on the game's main thread via the XP patches'
/// prefixes. Unity object reads off the main thread are not safe, so background threads request work
/// with <see cref="RequestRebuild"/> and the reporter's <c>SendStaged</c> instead of doing it.
/// </para>
/// </summary>
internal static class TableClient
{
    private const string TablePath = "/skillmultiplier/api/table";
    private const string SocketPath = "/skillmultiplier/ws/";

    private static readonly object Gate = new();

    /// <summary>
    /// Set once, from <see cref="Plugin.OnDestroy"/>, when the game is quitting. Both loops below check it:
    /// without this the socket is never closed and the reconnect loop never exits, which keeps the process
    /// alive after "quit game" - background threads die with the process, but an open socket held by the
    /// library's own threads does not let it get there.
    /// </summary>
    internal static volatile bool Quitting;

    /// <summary>The live socket, so shutdown can close it. Only touched under <see cref="Gate"/>.</summary>
    private static WebSocketSharp.WebSocket _socket;

    /// <summary>The table's actions, as pushed. Kept so the map can be rebuilt once the profile loads.</summary>
    private static Dictionary<string, float> _actions = [];

    /// <summary>
    /// A rebuild is owed to the game's main thread. Set by the socket thread (each push), the HTTP fallback
    /// and the heartbeat; cleared by <see cref="DrainMainThread"/> once the game objects have been read.
    /// Starts set so the first profile load builds the map without waiting for a push.
    /// </summary>
    private static volatile bool _rebuildRequested = true;

    /// <summary>
    /// Ask the game's main thread to re-read the action list and republish the map. Safe from any thread:
    /// it only sets the flag. The actual game-object reads happen in <see cref="DrainMainThread"/>.
    /// </summary>
    internal static void RequestRebuild() => _rebuildRequested = true;

    private static int _revision = -1;
    private static int _loggedRevision = int.MinValue;
    private static int _warnedRevision = int.MinValue;
    private static bool _catalogLogged;
    private static volatile string _socketUrl;

    /// <summary>
    /// The page's answer to a legacy game config, if one has arrived - see
    /// <see cref="LegacyConfig"/>. Read by the migration tick, not the table apply: the answer may arrive
    /// before the profile exists, and carrying needs the live action list.
    /// </summary>
    internal static volatile string LegacyRequest;

    internal static void Start()
    {
        // Warm the legacy-config answer now, during plugin load: the first per-frame drain must not be
        // the one that discovers the file read.
        LegacyConfig.WarmLegacyCache();

        var thread = new Thread(Run) { IsBackground = true, Name = "SkillMultiplier.Table" };
        thread.Start();
    }

    private static void Run()
    {
        FetchOnce();
        SocketLoop();
    }

    /// <summary>First sync without the socket, so a failed websocket still leaves multipliers applied.</summary>
    private static void FetchOnce()
    {
        try
        {
            var json = RequestHandler.GetJson(TablePath);

            if (!string.IsNullOrWhiteSpace(json))
            {
                Apply(json);
            }
        }
        catch (Exception ex)
        {
            Plugin.Log.LogWarning($"[SkillMultiplier] Could not fetch the table over HTTP: {ex.Message}");
        }
    }

    private static void SocketLoop()
    {
        var url = ResolveSocketUrl();

        if (url == null)
        {
            Plugin.Log.LogWarning(
                "[SkillMultiplier] No server address available for the websocket; multipliers will only be "
                    + "refreshed on the next game start."
            );
            return;
        }

        _socketUrl = url;
        var backoffSeconds = 5;

        while (!Quitting)
        {
            try
            {
                using var socket = new WebSocketSharp.WebSocket(url);
                lock (Gate)
                {
                    _socket = socket;

                    // A new connection may mean a new server epoch: the server resets its revision to 1
                    // on every boot and re-pushes its current table on connect. Without this reset a
                    // client holding a higher revision would drop that re-push as stale and run the old
                    // table until the next save. Re-applying the same revision after a transient drop is
                    // harmless (rebuilds are idempotent).
                    _revision = -1;
                }
                socket.SslConfiguration.ServerCertificateValidationCallback = (_, _, _, _) => true;
                socket.OnMessage += (_, e) =>
                {
                    if (e.IsText)
                    {
                        Apply(e.Data);
                    }
                };
                socket.OnError += (_, e) => Plugin.Log.LogWarning($"[SkillMultiplier] Websocket error: {e.Message}");

                socket.Connect();

                if (!socket.IsAlive)
                {
                    throw new Exception("the websocket did not open");
                }

                Plugin.DebugLog($"[SkillMultiplier] Connected to the server at {url}.");
                backoffSeconds = 5;

                // The server may have restarted while this client kept running, taking the stored report with
                // it, so every connection re-arms it.
                ClientCatalogReporter.RequestReport();

                // Hold the connection; the library pumps messages on its own thread.
                while (!Quitting && socket.IsAlive)
                {
                    Thread.Sleep(1000);
                }

                lock (Gate)
                {
                    if (ReferenceEquals(_socket, socket))
                    {
                        _socket = null;
                    }
                }
            }
            catch (Exception ex)
            {
                if (!Quitting)
                {
                    Plugin.Log.LogWarning(
                        $"[SkillMultiplier] Websocket unavailable ({ex.Message}); retrying in {backoffSeconds}s."
                    );
                }
            }

            if (!Quitting)
            {
                Thread.Sleep(backoffSeconds * 1000);
                backoffSeconds = Math.Min(backoffSeconds * 2, 60);
            }
        }
    }

    /// <summary>
    /// Close the live socket and stop both loops. Called from <see cref="Plugin.OnDestroy"/> on the game's
    /// main thread while quitting. Closing unblocks the hold loop and the flag stops the reconnect loop, so
    /// neither outlives the quit.
    /// </summary>
    internal static void Shutdown()
    {
        Quitting = true;

        try
        {
            lock (Gate)
            {
                _socket?.Close();
                _socket = null;
            }
        }
        catch (Exception ex)
        {
            Plugin.Log.LogWarning($"[SkillMultiplier] Could not close the websocket on quit: {ex.Message}");
        }
    }

    /// <summary>
    /// Derive <c>wss://host:port/skillmultiplier/ws/</c> from the address SPT already handed the game, so no
    /// configuration is needed and the port can change freely.
    /// </summary>
    private static string ResolveSocketUrl()
    {
        try
        {
            var host = RequestHandler.Host;

            if (string.IsNullOrEmpty(host))
            {
                return null;
            }

            var scheme = host.StartsWith("https://", StringComparison.OrdinalIgnoreCase) ? "wss://" : "ws://";
            var rest = host.Substring(host.IndexOf("://", StringComparison.Ordinal) + 3);

            return scheme + rest.TrimEnd('/') + SocketPath;
        }
        catch (Exception ex)
        {
            Plugin.DebugLog($"[SkillMultiplier] Could not resolve the server address: {ex.Message}");
            return null;
        }
    }

    private sealed class TableMessage
    {
        public string Type { get; set; }

        public int Revision { get; set; }

        public bool Enabled { get; set; }

        public bool DisableFatigue { get; set; }

        public double GlobalMultiplier { get; set; } = 1.0;

        public string LegacyMigration { get; set; }

        public Dictionary<string, double> Actions { get; set; }
    }

    private static void Apply(string json)
    {
        try
        {
            var message = JsonConvert.DeserializeObject<TableMessage>(json);

            if (message == null || !"table".Equals(message.Type, StringComparison.OrdinalIgnoreCase))
            {
                return;
            }

            bool disableFatigue;

            lock (Gate)
            {
                // A push that is not newer is either a duplicate, the HTTP fallback racing the socket, or
                // an out-of-order delivery - applying a stale table would regress live multipliers, so
                // anything at or below the held revision is dropped. (An equal revision with different
                // content, e.g. a hand-posted table reusing a revision, is dropped too: staleness wins
                // over freshness here because revisions only ever increase from the server.)
                if (message.Revision <= _revision && _revision >= 0)
                {
                    return;
                }

                _revision = message.Revision;
                _actions = new Dictionary<string, float>(StringComparer.OrdinalIgnoreCase);

                if (message.Enabled && message.Actions != null)
                {
                    foreach (var (key, value) in message.Actions)
                    {
                        // Clamped per row, like the global below: a hand-posted push carrying NaN, infinity
                        // or a negative must not reach live game maths. (float)NaN would otherwise flow
                        // straight into FactorValue multiplications.
                        _actions[key] = double.IsNaN(value) || double.IsInfinity(value) || value < 0
                            ? 1f
                            : (float)Math.Min(value, 1000.0);
                    }
                }

                // Gated on the master switch, like the multipliers: turning the mod off must leave the game
                // vanilla, not leave one of its effects behind.
                disableFatigue = message.Enabled && message.DisableFatigue;

                // Stored raw, not gated: the patch checks Enabled before reading it, so disabling the mod
                // still leaves the game vanilla, and re-enabling applies the right value without a re-push.
                // Clamped for the same reason the server clamps on save - a hand-posted table must not be
                // able to push a negative, NaN or absurd value into the game's maths.
                var global = message.GlobalMultiplier;

                if (double.IsNaN(global) || double.IsInfinity(global) || global < 0)
                {
                    global = 1.0;
                }

                Plugin.GlobalMultiplier = (float)Math.Min(global, 1000.0);
            }

            // The page's answer to a legacy config, if the server has one to pass on. Stored, not acted on:
            // carrying needs the SkillManager, which may not exist yet - see LegacyConfig.Tick.
            var legacyRequest = message.LegacyMigration;

            if (!string.IsNullOrEmpty(legacyRequest))
            {
                LegacyRequest = legacyRequest;
            }

            // Outside the lock: this detours a method the game's main thread is calling right now, and the
            // HTTP fallback may have already applied this same table.
            Plugin.SetFatigueDisabled(disableFatigue);

            // Game objects are read on the game's main thread, not here: queue the rebuild for the next
            // XP patch tick rather than walking the SkillManager off-thread.
            RequestRebuild();
        }
        catch (Exception ex)
        {
            // A malformed table must never take the client down; vanilla multipliers continue to apply.
            Plugin.Log.LogWarning($"[SkillMultiplier] Could not read the multiplier table: {ex.Message}");
        }
    }

    /// <summary>
    /// Run the game-object half of a pending rebuild. Must only ever run on the game's main thread - it is
    /// called from the XP patches' prefixes and from <c>Plugin.Update</c>, which the game invokes there.
    /// Safe to call repeatedly: the flag check first keeps the unowed case to one volatile read.
    /// <para>
    /// When the profile has not loaded yet the manager resolves to null and the request stays set, so the
    /// next tick retries - which is what makes table/push-vs-profile-load ordering irrelevant.
    /// </para>
    /// </summary>
    internal static void DrainMainThread()
    {
        if (!_rebuildRequested)
        {
            return;
        }

        var manager = ActionCatalog.Resolve();

        if (manager == null)
        {
            return;
        }

        // Cleared only on the way out, once the work below is done: an exception anywhere in between
        // leaves the request set for the next tick instead of silently dropping a rebuild. This also
        // runs inside Harmony prefixes and Update, so an exception escaping here would propagate into
        // game code - everything fallible underneath already guards itself, and this is the backstop.
        try
        {
            // Dump the catalog once, unconditionally: it is the evidence for whether the client's keys line up
            // with what the server is being told, and requiring a debug flag to see it makes that evidence
            // unavailable exactly when a mapping surprise needs explaining.
            if (!_catalogLogged)
            {
                _catalogLogged = true;
                ActionCatalog.LogCatalog(manager);
            }

            Dictionary<string, float> actions;

            lock (Gate)
            {
                actions = _actions;
            }

            Plugin.Multipliers = ActionCatalog.BuildMap(manager, actions);

            // Log once per revision. The heartbeat re-requests this, so an empty table would otherwise print a
            // line every few seconds for the whole session.
            if (_revision != _loggedRevision)
            {
                _loggedRevision = _revision;

                foreach (var (key, value) in actions)
                {
                    Plugin.DebugLog($"[SkillMultiplier] table: {key} = {value}");
                }

                Plugin.DebugLog(
                    $"[SkillMultiplier] Revision {_revision}: {Plugin.Multipliers.Count} of {actions.Count} action multiplier(s) "
                        + "mapped to actions in this client."
                );
            }

            // One Describe feeds both consumers, so the reflection walk happens once per drain: the migration
            // tick reads the staged copy on its background thread, and the reporter stages it for a
            // background send. Neither re-walks game objects.
            var described = ActionCatalog.Describe(manager);
            LegacyConfig.OfferDescribed(described);
            ClientCatalogReporter.StageForSend(described);

            _rebuildRequested = false;
        }
        catch (Exception ex)
        {
            // Once per revision, not once per frame: a deterministically-throwing drain rides the 60Hz
            // Update pump, and per-frame warnings would bury the log. The flag stays set either way, so a
            // transient failure still retries next tick.
            if (_revision != _warnedRevision)
            {
                _warnedRevision = _revision;
                Plugin.Log.LogWarning($"[SkillMultiplier] Table rebuild failed, will retry: {ex.Message}");
            }
        }
    }

    /// <summary>
    /// Retry building the map on a slow timer. Only the first successful build matters in practice; this is
    /// what makes ordering between "table arrived" and "profile loaded" irrelevant.
    /// <para>
    /// Game objects are never touched here: this only re-requests the rebuild (drained on the game's main
    /// thread) and sends an already-staged catalog report. Both are safe from a background thread.
    /// </para>
    /// </summary>
    internal static void StartHeartbeat()
    {
        var thread = new Thread(() =>
        {
            while (!TableClient.Quitting)
            {
                Thread.Sleep(5000);

                try
                {
                    // Also runs when the catalog has not been dumped yet, so a client whose sync failed still
                    // records what its own action list looks like - on the next main-thread tick.
                    if (!_catalogLogged || Plugin.Multipliers.Count == 0)
                    {
                        RequestRebuild();
                    }

                    // The migration tick (file + network IO) and the staged report send both run here, on a
                    // background thread, consuming what the main-thread drain staged. Neither touches game
                    // objects - and while a report is still owed, this keeps retrying on the slow timer.
                    LegacyConfig.Tick();
                    ClientCatalogReporter.SendStaged();
                }
                catch (Exception ex)
                {
                    Plugin.DebugLog($"[SkillMultiplier] Heartbeat rebuild failed: {ex.Message}");
                }
            }
        })
        {
            IsBackground = true,
            Name = "SkillMultiplier.Heartbeat",
        };

        thread.Start();
    }
}
