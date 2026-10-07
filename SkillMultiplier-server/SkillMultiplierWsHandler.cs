using System.Net.WebSockets;
using System.Text;
using System.Text.Json;
using Microsoft.AspNetCore.Http;
using SPTarkov.Common.Models.Logging;
using SPTarkov.DI.Annotations;
using SPTarkov.Server.Core.DI;
using SPTarkov.Server.Core.Servers.Ws;

namespace SkillMultiplier;

/// <summary>
/// Pushes the live multiplier table to every connected game client.
/// <para>
/// SPT already hosts a websocket server (<c>app.UseWebSockets()</c>) and routes any websocket whose
/// request path <em>contains</em> a handler's <see cref="GetHookUrl"/> to that handler, so this needs no
/// listener of its own - implementing the interface and being <c>[Injectable]</c> is the whole
/// registration. Mod assemblies are handed to the DI handler before <c>InjectAll()</c>
/// (<c>ProgramHelpers.cs:95</c>), which registers each injectable type against every interface it
/// implements.
/// </para>
/// <para>
/// <b>Singleton is load-bearing</b> and must be stated: <c>[Injectable]</c> defaults to <c>Transient</c>,
/// which would build a fresh handler with an empty connection list for anyone who resolves it.
/// </para>
/// <para>
/// This is the push half of the client sync. It exists so a save in the UI reaches every client -
/// including every player in a Fika co-op session, who each hold their own connection - without any
/// client polling. First contact is the initial sync: <see cref="OnConnectionAsync"/> sends the table
/// immediately, so a client that connects after a save is already up to date.
/// </para>
/// </summary>
[Injectable(InjectionType.Singleton, OnLoadOrder.PostLoad + 2)]
public sealed class SkillMultiplierWsHandler(
    ISptLogger<SkillMultiplierWsHandler> logger,
    SkillMultiplierMod mod
) : IWebSocketConnectionHandler
{
    /// <summary>
    /// Deliberately distinctive. Matching is a substring test against the request path, so a hook URL
    /// that appears inside another handler's path (or is a prefix of everything) would silently receive
    /// that handler's traffic as well.
    /// </summary>
    public const string HookUrl = "/skillmultiplier/ws/";

    private sealed class Connection(WebSocket socket)
    {
        public WebSocket Socket { get; } = socket;

        /// <summary>
        /// A <see cref="WebSocket"/> throws if two sends overlap, and a broadcast can arrive while this
        /// connection is mid-handshake or mid-reply, so sends are serialised per socket.
        /// </summary>
        public SemaphoreSlim SendGate { get; } = new(1, 1);
    }

    private static readonly JsonSerializerOptions JsonOptions = new() { PropertyNamingPolicy = null };

    private readonly Dictionary<WebSocket, Connection> _connections = [];
    private readonly object _gate = new();

    public string GetHookUrl() => HookUrl;

    public string GetSocketId() => "SkillMultiplier";

    public async Task OnConnectionAsync(WebSocket ws, HttpContext context, string sessionIdContext)
    {
        var connection = new Connection(ws);

        lock (_gate)
        {
            _connections[ws] = connection;
        }

        logger.Info($"[SkillMultiplier] Client connected ({sessionIdContext}); {Count} socket(s) open. Sending the table.");

        // First contact is the initial sync - the client does not need to ask for anything.
        await SendToAsync(connection, BuildTable());
    }

    public Task OnMessageAsync(byte[] rawData, WebSocketMessageType messageType, WebSocket ws, HttpContext context)
    {
        // The only message a client is expected to send is a request to re-send the table. Anything else
        // is ignored rather than treated as an error, so a misbehaving client cannot break the server.
        if (messageType != WebSocketMessageType.Text)
        {
            return Task.CompletedTask;
        }

        var text = Encoding.UTF8.GetString(rawData).Trim();

        if (!text.Equals("refresh", StringComparison.OrdinalIgnoreCase))
        {
            logger.Debug($"[SkillMultiplier] Ignoring unrecognised websocket message: {text}");
            return Task.CompletedTask;
        }

        Connection? connection;
        lock (_gate)
        {
            _connections.TryGetValue(ws, out connection);
        }

        return connection is null ? Task.CompletedTask : SendToAsync(connection, BuildTable());
    }

    public async Task OnCloseAsync(WebSocket ws, HttpContext context, string sessionIdContext)
    {
        lock (_gate)
        {
            _connections.Remove(ws);
        }

        // Deliberately not disposed: a broadcast snapshot taken just before this close may still hold
        // this connection, and disposing its gate under a waiter strands the whole broadcast. A
        // SemaphoreSlim with no contention holds no handle worth freeing; an uncontended instance is
        // GC-collected without one. Correctness over handle hygiene.
        // A disconnected session answers nothing further: drop its report so a departed legacy client
        // cannot pin the migration latch (or a stale action list) forever. When the drop clears the
        // latch the pushed table changed, so push - otherwise the remaining clients ride a stale answer
        // until the next save.
        var latchCleared = mod.DropClientSession(sessionIdContext);

        logger.Info($"[SkillMultiplier] Client disconnected ({sessionIdContext}); {Count} socket(s) open.");

        if (latchCleared)
        {
            await BroadcastAsync();
        }
    }

    /// <summary>
    /// Send the current table to every open connection. Called after a save, and safe to call when
    /// nothing is connected - a client that is not running simply receives the table when it next
    /// connects.
    /// </summary>
    public async Task BroadcastAsync()
    {
        Connection[] snapshot;

        lock (_gate)
        {
            snapshot = [.. _connections.Values];
        }

        if (snapshot.Length == 0)
        {
            logger.Info("[SkillMultiplier] No clients connected; the saved table will be sent when one connects.");
            return;
        }

        var payload = BuildTable();
        var sent = 0;

        foreach (var connection in snapshot)
        {
            if (await SendToAsync(connection, payload))
            {
                sent++;
            }
        }

        logger.Info($"[SkillMultiplier] Pushed revision {mod.Revision} to {sent}/{snapshot.Length} client(s).");
    }

    private int Count
    {
        get
        {
            lock (_gate)
            {
                return _connections.Count;
            }
        }
    }

    /// <summary>
    /// Serialisable snapshot of the table. Shared by the websocket push and the GET fallback so the two
    /// can never disagree about what a client is being told.
    /// </summary>
    public WsTableMessage BuildTableMessage() =>
        new()
        {
            Type = "table",
            Revision = mod.Revision,
            Enabled = mod.Config.Enabled,
            DisableFatigue = mod.Config.DisableFatigue,
            MaxMultiplier = SkillMultiplierMod.MaxMultiplier,
            GlobalMultiplier = mod.Config.GlobalMultiplier,
            LegacyMigration = mod.ClientLegacyRequest,
            Multipliers = mod.Multipliers,
            Actions = mod.Actions,
        };

    private byte[] BuildTable() => JsonSerializer.SerializeToUtf8Bytes(BuildTableMessage(), JsonOptions);

    /// <summary>Returns false for a socket that is closed or faulted, so the caller can report a real count.</summary>
    private async Task<bool> SendToAsync(Connection connection, byte[] payload)
    {
        // The wait sits inside the try with acquisition tracked, so any failure before or during it
        // returns false instead of escaping into the broadcast loop: one dead socket must never stop
        // the push to the rest.
        var acquired = false;

        try
        {
            await connection.SendGate.WaitAsync();
            acquired = true;

            if (connection.Socket.State != WebSocketState.Open)
            {
                return false;
            }

            await connection.Socket.SendAsync(payload, WebSocketMessageType.Text, endOfMessage: true, CancellationToken.None);
            return true;
        }
        catch (Exception ex) when (ex is WebSocketException or InvalidOperationException)
        {
            // A dropped client is normal (game closed, raid transition, lost race with a disconnect).
            // Never let it take down a save, and never let one dead socket stop the push to the rest.
            logger.Debug($"[SkillMultiplier] Websocket send failed: {ex.Message}");
            return false;
        }
        catch (Exception ex)
        {
            // Unexpected: not a dead socket but something genuinely wrong with the send. Loud, with the
            // full exception - a bare message here would be indistinguishable from a routine drop.
            logger.Warning($"[SkillMultiplier] Unexpected websocket send failure: {ex}");
            return false;
        }
        finally
        {
            // Direct release: nothing disposes this gate any more (see OnCloseAsync), so a successful
            // wait always pairs with exactly one release and no ObjectDisposedException path exists.
            if (acquired)
            {
                connection.SendGate.Release();
            }
        }
    }
}

/// <summary>
/// The push payload. <c>Type</c> and <c>Revision</c> are carried so the client can ignore message kinds
/// it does not know and skip re-applying a table it already has.
/// <para>
/// <see cref="Multipliers"/> and <see cref="Actions"/> are two different key spaces for two different
/// mechanisms - server-side <c>globals</c> scaling, and client-side per-action multipliers - and the
/// client must not treat them interchangeably. See <see cref="SkillMultiplierConfig.Actions"/>.
/// </para>
/// </summary>
public sealed record WsTableMessage
{
    public string Type { get; set; } = "table";

    public int Revision { get; set; }

    public bool Enabled { get; set; }

    /// <summary>
    /// Ask the client to remove its in-session fatigue curve. Applied by the client against
    /// <c>SkillManager.GetEffectiveness</c>, so no globals value changes and no restart is needed.
    /// </summary>
    public bool DisableFatigue { get; set; }

    public double MaxMultiplier { get; set; }

    /// <summary>Server-owned keys (<c>Settings.Skill.Field</c>), scaled into the live globals table.</summary>
    public IReadOnlyDictionary<string, double> Multipliers { get; set; } = new Dictionary<string, double>();

    /// <summary>
    /// The global multiplier, applied by the client on top of every per-row value. Carried here rather
    /// than baked into <see cref="Actions"/> so one save cannot compound it into the rows.
    /// </summary>
    public double GlobalMultiplier { get; set; } = 1.0;

    /// <summary>
    /// The page's answer to a legacy game config, if any - see
    /// <see cref="SkillMultiplierMod.ClientLegacyRequest"/>. Null while unasked.
    /// </summary>
    public string? LegacyMigration { get; set; }

    /// <summary>Client-owned keys (<c>SkillId[index]</c>), applied by the client at <c>Skill.OnTrigger</c>.</summary>
    public IReadOnlyDictionary<string, double> Actions { get; set; } = new Dictionary<string, double>();
}
