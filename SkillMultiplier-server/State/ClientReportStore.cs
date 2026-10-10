using SPTarkov.Common.Models.Logging;

namespace SkillMultiplier;

/// <summary>
/// Holds what connected game clients report about themselves, plus the kept
/// report on disk and the legacy-migration latch.
/// <para>
/// Extracted verbatim from <c>SkillMultiplierMod</c> (M5 split). The latch
/// rule is the subtle part: <see cref="LegacyRequest"/> is set once from the
/// migrate endpoint and cleared only when no connected session reports a
/// legacy config of its own. Clearing on broadcast or on first ack would
/// break late-join Fika clients; never clearing pins a stale answer on every
/// future table. Mutators return whether they cleared the latch so the facade
/// can bump the revision atomically under the same gate.
/// </para>
/// </summary>
internal sealed class ClientReportStore(
    ISptLogger<SkillMultiplierMod> logger,
    object gate
)
{
    private readonly ISptLogger<SkillMultiplierMod> _logger = logger;
    private readonly object _gate = gate;

    /// <summary>
    /// The page's answer to a game client's legacy config, latched until the server restarts: "migrate"
    /// or "decline", null while unasked. Volatile for readers; all writes go through
    /// <see cref="SetLegacyRequest"/> or under <c>_gate</c> in <see cref="Report"/>/
    /// <see cref="DropSession"/>, so a set can never race those methods' clear-checks.
    /// </summary>
    public volatile string? LegacyRequest;

    /// <summary>Set the migration latch under the shared gate. See <see cref="LegacyRequest"/>.</summary>
    public void SetLegacyRequest(string? value)
    {
        lock (_gate)
        {
            LegacyRequest = value;
        }
    }

    private readonly Dictionary<string, Dictionary<string, ClientActionEntry>> _actionsBySession =
        new(StringComparer.OrdinalIgnoreCase);

    /// <summary>
    /// Which sessions' games still hold the previous release's config. Tracked alongside the action lists
    /// so the page can ask about migrating them; a game config can only be migrated by its game.
    /// </summary>
    private readonly Dictionary<string, bool> _legacyBySession = new(StringComparer.OrdinalIgnoreCase);

    /// <summary>
    /// Skills a connected client has reported as ones its own game refuses to progress locally. Kept as the
    /// union across sessions rather than per session: the question is whether the client can apply anything to
    /// the skill at all, and one install that cannot is enough to say its rows are not a duplicate.
    /// </summary>
    private readonly HashSet<string> _unauthorized = new(StringComparer.OrdinalIgnoreCase);

    /// <summary>
    /// The report kept on disk, or null. Read once at startup and refreshed whenever a client reports.
    /// Volatile: assigned outside the gate on the report path, so unsynchronized readers (the cached-at
    /// timestamp) still observe a whole reference, never a half-written one.
    /// <para>
    /// Display only, deliberately - no multiplier is ever derived from it, so a wrong cache costs a wrong
    /// list, never wrong numbers in the game.
    /// </para>
    /// </summary>
    private volatile CachedClientReport? _cachedReport;

    public string CachePath { get; set; } = string.Empty;

    /// <summary>
    /// Snapshot copy of the client-keyed action multipliers is not here; this is the reported action
    /// lists, merged for display. Advisory by design: the server cannot enumerate these - an action exists
    /// only inside the running client - so this is a claim, not a contract.
    /// </summary>
    public IReadOnlyDictionary<string, ClientActionEntry> UnionActions()
    {
        lock (_gate)
        {
            var union = new Dictionary<string, ClientActionEntry>(StringComparer.OrdinalIgnoreCase);

            foreach (var session in _actionsBySession.Values)
            {
                foreach (var (key, entry) in session)
                {
                    union[key] = entry;
                }
            }

            // Nothing live to go on: fall back to the last report kept on disk, so restarting the server
            // does not empty the page's client rows.
            if (union.Count == 0 && _cachedReport != null)
            {
                foreach (var action in _cachedReport.Actions)
                {
                    if (!string.IsNullOrWhiteSpace(action.Key))
                    {
                        union[action.Key] = action;
                    }
                }
            }

            return union;
        }
    }

    /// <summary>How many clients have introduced themselves, so the UI can say whether the list is complete.</summary>
    public int ReporterCount
    {
        get
        {
            lock (_gate)
            {
                return _actionsBySession.Count;
            }
        }
    }

    /// <summary>Whether any connected game reports a legacy config of its own.</summary>
    public bool LegacyDetected
    {
        get
        {
            lock (_gate)
            {
                return _legacyBySession.Values.Any(detected => detected);
            }
        }
    }

    /// <summary>When the client list being shown was received. Null while it comes from live clients.</summary>
    public DateTimeOffset? CachedReportAt => _cachedReport?.CapturedUtc;

    /// <summary>
    /// Whether the client rows are coming from the cache rather than from a connected client, so the UI can
    /// say that the observed amounts in them are from a previous session.
    /// </summary>
    public bool ActionsFromCache
    {
        get
        {
            lock (_gate)
            {
                return _actionsBySession.Count == 0 && _cachedReport != null;
            }
        }
    }

    public HashSet<string> UnauthorizedSnapshot()
    {
        lock (_gate)
        {
            return new HashSet<string>(_unauthorized, StringComparer.OrdinalIgnoreCase);
        }
    }

    public void LoadCache()
    {
        _cachedReport = ClientReportCache.Load(CachePath);

        if (_cachedReport != null)
        {
            _logger.Info(
                $"[SkillMultiplier] Client actions restored from a kept report: {_cachedReport.Actions.Count} "
                + $"action(s) captured {_cachedReport.CapturedUtc:u}."
            );
        }
    }

    /// <summary>Keep the current client list, so the next boot starts with it instead of an empty page.</summary>
    public void SaveCache()
    {
        Dictionary<string, ClientActionEntry> union;
        int reporters;

        lock (_gate)
        {
            reporters = _actionsBySession.Count;
            union = new Dictionary<string, ClientActionEntry>(StringComparer.OrdinalIgnoreCase);

            foreach (var session in _actionsBySession.Values)
            {
                foreach (var (key, entry) in session)
                {
                    union[key] = entry;
                }
            }
        }

        if (union.Count == 0)
        {
            return;
        }

        var snapshot = new CachedClientReport
        {
            CapturedUtc = DateTimeOffset.UtcNow,
            Reporters = reporters,
            Actions = [.. union.Values],
        };

        _cachedReport = snapshot;

        try
        {
            ClientReportCache.Save(CachePath, snapshot);
        }
        catch (Exception ex)
        {
            // Losing the cache costs a refresh after the next restart; failing here would cost the report.
            _logger.Warning($"[SkillMultiplier] Could not keep the client report: {ex.Message}");
        }
    }

    /// <summary>
    /// Whether the kept report disagrees with an arriving one about the action set, in which case the
    /// cache goes rather than being merged (keys are positions in each client's own arrays, so a
    /// changed set means the same key can name a different action).
    /// Pure: reads the volatile reference without taking the gate.
    /// </summary>
    private bool CacheDisagrees(IReadOnlyDictionary<string, ClientActionEntry> reported)
    {
        var cached = _cachedReport;

        if (cached == null)
        {
            return false;
        }

        var cachedKeys = cached.Actions.Select(action => action.Key).ToHashSet(StringComparer.OrdinalIgnoreCase);

        if (cachedKeys.SetEquals(reported.Keys))
        {
            return false;
        }

        _logger.Info(
            $"[SkillMultiplier] The client's action list changed ({cachedKeys.Count} kept, {reported.Count} "
            + "reported), so the kept names and observed amounts were discarded rather than merged."
        );

        return true;
    }

    /// <summary>
    /// Record one client's action list. A re-report replaces that client's previous set rather than adding
    /// to it, so a client that loses a mod's actions stops advertising them.
    /// Returns true when the migration latch was cleared and the facade must bump the revision.
    /// </summary>
    public bool Report(IEnumerable<ClientActionEntry> actions, string sessionId, bool legacyDetected)
    {
        var reported = new Dictionary<string, ClientActionEntry>(StringComparer.OrdinalIgnoreCase);
        var unauthorized = new HashSet<string>(StringComparer.OrdinalIgnoreCase);

        foreach (var action in actions)
        {
            if (!string.IsNullOrWhiteSpace(action.Key))
            {
                reported[action.Key] = action;
            }

            // A skill this client's own game will not progress locally. Its rows exist and are settable, but
            // the client can never apply them.
            if (action.ServerAuthoritative && !string.IsNullOrWhiteSpace(action.Skill))
            {
                unauthorized.Add(action.Skill);
            }
        }

        // Decided off-gate (pure read of the volatile cache); the nulling happens under the gate
        // below and the file delete after it, so neither IO nor the lock is held for the other.
        var dropCache = CacheDisagrees(reported);

        bool cleared;

        lock (_gate)
        {
            if (dropCache)
            {
                _cachedReport = null;
            }

            _actionsBySession[sessionId] = reported;
            _legacyBySession[sessionId] = legacyDetected;

            foreach (var skill in unauthorized)
            {
                _unauthorized.Add(skill);
            }

            // The migration answer latches so late-connecting clients get it too - but once no connected
            // session reports a legacy config of its own, there is nobody left to answer and the latch
            // would ride every table push forever. Clear it and bump, so the pushed table stops carrying
            // a stale answer.
            cleared = LegacyRequest != null && !_legacyBySession.Values.Any(detected => detected);

            if (cleared)
            {
                LegacyRequest = null;
            }
        }

        _logger.Info($"[SkillMultiplier] Client {sessionId} reported {reported.Count} tunable action(s).");

        if (dropCache)
        {
            try
            {
                ClientReportCache.Delete(CachePath);
            }
            catch (Exception ex)
            {
                _logger.Warning($"[SkillMultiplier] Could not discard the kept client report: {ex.Message}");
            }
        }

        SaveCache();

        return cleared;
    }

    /// <summary>
    /// Forget one session's report when its socket closes. Without this a disconnected client's entries
    /// linger: a departed legacy client would pin the migration latch and a departed action list would
    /// outlive its game.
    /// Returns true when the migration latch was cleared and the facade must bump the revision.
    /// </summary>
    public bool DropSession(string sessionId)
    {
        lock (_gate)
        {
            _actionsBySession.Remove(sessionId);
            _legacyBySession.Remove(sessionId);

            if (LegacyRequest != null && !_legacyBySession.Values.Any(detected => detected))
            {
                LegacyRequest = null;
                return true;
            }

            return false;
        }
    }
}
