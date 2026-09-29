using System.Reflection;
using System.Text.Json;
using System.Text.Json.Nodes;
using SPTarkov.Common.Models.Logging;
using SPTarkov.DI.Annotations;
using SPTarkov.Server.Core.DI;
using SPTarkov.Server.Core.Helpers.Server;
using SPTarkov.Server.Core.Models.Spt.Tables;
using SPTarkov.Server.Core.Services.Locales;

namespace SkillMultiplier;

/// <summary>
/// Owns the tuning state and pushes it into the live <see cref="GlobalTable"/>.
/// <para>
/// The transformation is expressed as <c>current = base * multiplier</c>, where <c>base</c> is snapshotted
/// from the in-memory table the first time this runs. That makes <see cref="Apply"/> idempotent: calling it
/// ten times is the same as calling it once, so a save cannot compound into runaway XP. It also means the
/// on-disk <c>globals.json</c> is never written to - the base is re-read from it on every boot, so a game
/// patch that rebalances a skill is inherited rather than reverted by a stale file.
/// </para>
/// <para>
/// <b>Singleton is load-bearing.</b> <c>[Injectable]</c> defaults to <c>Transient</c>, which would hand the
/// router its own instance with an empty base snapshot - every base would read 0 and every save would
/// silently apply nothing. The base snapshot only means anything if one instance owns it.
/// </para>
/// <para>
/// <c>PostLoad + 1</c> is 1000001, which puts it in the <c>&gt;= 200000</c> window that SPT's startup hosted
/// service runs (the other window is <c>&gt;= 0 and &lt; 200000</c>, run before the web host is built). That is
/// late enough for the database and web host to exist, and early enough to be in place before play.
/// </para>
/// <para>
/// The skill names the client reports are <c>ESkillId</c> names - <c>FieldMedicine</c>, not "Field Medicine".
/// <see cref="LocaleService"/> holds the game's own locale database, which SPT also merges every mod's locale
/// entries into, so a real name (and description) is available for any skill that has them - including one
/// another mod adds. This is presentation only: multipliers stay keyed by the reported id.
/// </para>
/// </summary>
[Injectable(InjectionType.Singleton, OnLoadOrder.PostLoad + 1)]
public sealed class SkillMultiplierMod(
    ISptLogger<SkillMultiplierMod> logger,
    ModHelper modHelper,
    GlobalTable globalTable,
    LocaleService localeService
) : IOnLoad
{
    private readonly Dictionary<string, double> _base = new(StringComparer.OrdinalIgnoreCase);
    private readonly object _gate = new();

    /// <summary>Resolved once, on first use - see <see cref="LocaleValue"/>.</summary>
    private Dictionary<string, string>? _localeDb;

    public SkillMultiplierConfig Config { get; private set; } = new();

    public string ModFolder { get; private set; } = string.Empty;

    public string ConfigPath => Path.Combine(ModFolder, "config.json");

    /// <summary>
    /// Bumped whenever the effective multiplier set changes, so a pushed table can be recognised as
    /// newer than the one a client already holds.
    /// </summary>
    public int Revision { get; private set; }

    /// <summary>
    /// The page's answer to a game client's legacy config, latched until the server restarts: "migrate"
    /// or "decline", null while unasked. It rides the pushed table because the client cannot be asked
    /// any other way - and it latches because a client that connects later gets the same answer rather
    /// than re-asking. The client's own marker still makes each client act on it once.
    /// </summary>
    public volatile string? ClientLegacyRequest;

    /// <summary>Bump the revision when the pushed table changed without the config doing so.</summary>
    public void BumpRevision() => Revision++;

    /// <summary>
    /// Snapshot copy, taken under the same gate as <see cref="Apply"/>, so a broadcast can never observe
    /// a half-replaced multiplier set.
    /// </summary>
    public IReadOnlyDictionary<string, double> Multipliers
    {
        get
        {
            lock (_gate)
            {
                return new Dictionary<string, double>(Config.Multipliers, StringComparer.OrdinalIgnoreCase);
            }
        }
    }

    /// <summary>
    /// The game's own name for a skill - "Field Medicine" rather than the <c>ESkillId</c> name the client
    /// reports. Null when the locale has nothing for it, which is the honest answer for a skill that exists
    /// only inside some mod with no locale entries of its own.
    /// </summary>
    public string? SkillDisplayName(string skillId) => LocaleValue(skillId);

    /// <summary>
    /// The game's description for a skill, under the <c>&lt;SkillId&gt;Description</c> key it uses for skill
    /// tooltips. Null when it has none.
    /// </summary>
    public string? SkillDescription(string skillId) => LocaleValue(skillId + "Description");

    /// <summary>
    /// A locale lookup, held rather than re-fetched: <see cref="LocaleService.GetLocaleDb"/> merges the locale
    /// sources into a new dictionary, so calling it per request would rebuild tens of thousands of entries on
    /// every catalog fetch. The database is complete by the time a request can arrive and does not change after
    /// that. Two threads racing the assignment is harmless - reference assignment is atomic, and both
    /// dictionaries are equivalent.
    /// </summary>
    private string? LocaleValue(string key)
    {
        var db = _localeDb ??= localeService.GetLocaleDb();

        return db.TryGetValue(key, out var value) && !string.IsNullOrWhiteSpace(value) ? value : null;
    }

    /// <summary>
    /// Snapshot copy of the client-keyed action multipliers, pushed to clients. These are applied by the
    /// client at <c>Skill.OnTrigger</c>; nothing on the server consumes them.
    /// </summary>
    public IReadOnlyDictionary<string, double> Actions
    {
        get
        {
            lock (_gate)
            {
                return new Dictionary<string, double>(Config.Actions, StringComparer.OrdinalIgnoreCase);
            }
        }
    }

    /// <summary>
    /// The actions connected game clients say they have, keyed <c>SkillId[index]</c>, held per reporting
    /// session and merged for display.
    /// <para>
    /// Advisory by design: the server cannot enumerate these - an action exists only inside the running
    /// client - so this is a claim, not a contract. A key one client has and another does not is inert on
    /// the client that lacks it (an unrecognised key is ignored when the table is applied), which is what
    /// makes a union safe to show for a Fika session where players run different action lists.
    /// </para>
    /// </summary>
    public IReadOnlyDictionary<string, ClientActionEntry> ClientActions
    {
        get
        {
            lock (_gate)
            {
                var union = new Dictionary<string, ClientActionEntry>(StringComparer.OrdinalIgnoreCase);

                foreach (var session in _clientActionsBySession.Values)
                {
                    foreach (var (key, entry) in session)
                    {
                        union[key] = entry;
                    }
                }

                // Nothing live to go on: fall back to the last report kept on disk, so restarting the server
                // does not empty the page's client rows. ClientActionsFromCache is what tells the UI not to
                // read the figures in it as current.
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
    }

    /// <summary>How many clients have introduced themselves, so the UI can say whether the list is complete.</summary>
    public int ClientReporterCount
    {
        get
        {
            lock (_gate)
            {
                return _clientActionsBySession.Count;
            }
        }
    }

    private readonly Dictionary<string, Dictionary<string, ClientActionEntry>> _clientActionsBySession =
        new(StringComparer.OrdinalIgnoreCase);

    /// <summary>
    /// Which sessions' games still hold the previous release's config. Tracked alongside the action lists
    /// so the page can ask about migrating them; a game config can only be migrated by its game.
    /// </summary>
    private readonly Dictionary<string, bool> _clientLegacyBySession = new(StringComparer.OrdinalIgnoreCase);

    /// <summary>Whether any connected game reports a legacy config of its own.</summary>
    public bool ClientLegacyDetected
    {
        get
        {
            lock (_gate)
            {
                return _clientLegacyBySession.Values.Any(detected => detected);
            }
        }
    }

    /// <summary>
    /// The report kept on disk, or null. Read once at startup and refreshed whenever a client reports.
    /// <para>
    /// Display only, deliberately - see <see cref="CachedClientReport"/>. No multiplier is ever derived from
    /// it, so a wrong cache costs a wrong list, never wrong numbers in the game.
    /// </para>
    /// </summary>
    private CachedClientReport? _cachedReport;

    /// <summary>When the client list being shown was received. Null while it comes from live clients.</summary>
    public DateTimeOffset? CachedReportAt => _cachedReport?.CapturedUtc;

    /// <summary>
    /// Whether the client rows are coming from the cache rather than from a connected client, so the UI can
    /// say that the observed amounts in them are from a previous session.
    /// </summary>
    public bool ClientActionsFromCache
    {
        get
        {
            lock (_gate)
            {
                return _clientActionsBySession.Count == 0 && _cachedReport != null;
            }
        }
    }

    public string ClientReportCachePath => Path.Combine(ModFolder, "clientreport.json");

    private void LoadClientReportCache()
    {
        _cachedReport = ClientReportCache.Load(ClientReportCachePath);

        if (_cachedReport != null)
        {
            logger.Info(
                $"[SkillMultiplier] Client actions restored from a kept report: {_cachedReport.Actions.Count} "
                + $"action(s) captured {_cachedReport.CapturedUtc:u}."
            );
        }
    }

    /// <summary>Keep the current client list, so the next boot starts with it instead of an empty page.</summary>
    private void SaveClientReportCache()
    {
        Dictionary<string, ClientActionEntry> union;
        int reporters;

        lock (_gate)
        {
            reporters = _clientActionsBySession.Count;
            union = new Dictionary<string, ClientActionEntry>(StringComparer.OrdinalIgnoreCase);

            foreach (var session in _clientActionsBySession.Values)
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
            ClientReportCache.Save(ClientReportCachePath, snapshot);
        }
        catch (Exception ex)
        {
            // Losing the cache costs a refresh after the next restart; failing here would cost the report.
            logger.Warning($"[SkillMultiplier] Could not keep the client report: {ex.Message}");
        }
    }

    /// <summary>
    /// Discard the kept report when an arriving one describes a different set of actions.
    /// <para>
    /// The keys are positions in each client's own action arrays, so a client that gains or loses a mod's
    /// actions shifts them and the same key can mean a different action. Keeping old names and observed
    /// amounts across that would attribute them to the wrong rows, so the cache goes rather than being merged.
    /// </para>
    /// </summary>
    private void DropCacheIfKeySetsDisagree(IReadOnlyDictionary<string, ClientActionEntry> reported)
    {
        var cached = _cachedReport;

        if (cached == null)
        {
            return;
        }

        var cachedKeys = cached.Actions.Select(action => action.Key).ToHashSet(StringComparer.OrdinalIgnoreCase);

        if (cachedKeys.SetEquals(reported.Keys))
        {
            return;
        }

        _cachedReport = null;

        try
        {
            ClientReportCache.Delete(ClientReportCachePath);
        }
        catch (Exception ex)
        {
            logger.Warning($"[SkillMultiplier] Could not discard the kept client report: {ex.Message}");
        }

        logger.Info(
            $"[SkillMultiplier] The client's action list changed ({cachedKeys.Count} kept, {reported.Count} "
            + "reported), so the kept names and observed amounts were discarded rather than merged."
        );
    }

    /// <summary>
    /// Record one client's action list. A re-report replaces that client's previous set rather than adding
    /// to it, so a client that loses a mod's actions stops advertising them.
    /// </summary>
    public void ReportClientActions(IEnumerable<ClientActionEntry> actions, string sessionId, bool legacyDetected)
    {
        var reported = new Dictionary<string, ClientActionEntry>(StringComparer.OrdinalIgnoreCase);

        foreach (var action in actions)
        {
            if (!string.IsNullOrWhiteSpace(action.Key))
            {
                reported[action.Key] = action;
            }
        }

        DropCacheIfKeySetsDisagree(reported);

        lock (_gate)
        {
            _clientActionsBySession[sessionId] = reported;
            _clientLegacyBySession[sessionId] = legacyDetected;
        }

        logger.Info($"[SkillMultiplier] Client {sessionId} reported {reported.Count} tunable action(s).");

        SaveClientReportCache();
    }

    public Task OnLoadAsync(CancellationToken cancellationToken)
    {
        ModFolder = modHelper.GetAbsolutePathToModFolder(Assembly.GetExecutingAssembly());

        LoadConfig();
        LoadClientReportCache();
        CaptureBase();
        Apply();
        Revision = 1;

        logger.Success(
            $"[SkillMultiplier] Loaded. {Catalog.All.Count} tunable action(s), {_base.Count} base value(s) captured. "
            + $"Config: {ConfigPath}"
        );

        WarnOnConflicts();

        return Task.CompletedTask;
    }

    private void LoadConfig()
    {
        try
        {
            if (File.Exists(ConfigPath))
            {
                Config = modHelper.GetJsonDataFromFile<SkillMultiplierConfig>(ModFolder, "config.json")
                         ?? new SkillMultiplierConfig();
            }
            else
            {
                Config = new SkillMultiplierConfig();
                SaveConfig();
            }
        }
        catch (Exception ex)
        {
            // A malformed config must not stop the server from booting: fall back to vanilla.
            logger.Error($"[SkillMultiplier] config.json could not be read, falling back to vanilla values. {ex.Message}");
            Config = new SkillMultiplierConfig();
        }

        Config.Multipliers = new Dictionary<string, double>(
            Config.Multipliers ?? [],
            StringComparer.OrdinalIgnoreCase
        );

        Config.Actions = new Dictionary<string, double>(
            Config.Actions ?? [],
            StringComparer.OrdinalIgnoreCase
        );

        // A hand-edited config bypasses the save endpoint's validation, so the same bound is enforced here:
        // per-row values are guarded by GetMultiplier at apply time, and the global gets the same treatment
        // at load. The game itself is doubly protected - the client clamps the pushed table again - but the
        // page should never show a value the game will not use.
        Config.GlobalMultiplier = ClampGlobal(Config.GlobalMultiplier);

        // Detection only. Migrating here would take the choice away: the page asks first, and the migrate
        // endpoint runs this on demand. See RunLegacyMigration.
        if (HasLegacyServerConfig())
        {
            logger.Info(
                "[SkillMultiplier] Found settings from the previous release (CraftingExpMultiplier and/or "
                + "HideoutExpMultiplier). They are left alone until the page asks to migrate them."
            );
        }
    }

    /// <summary>
    /// Whether the previous release's settings are still sitting in config.json. Read off the file rather
    /// than memory: the page asks about them, and the answer must still be right if the file changed.
    /// </summary>
    public bool HasLegacyServerConfig()
    {
        var root = ReadLegacyRoot();

        return root != null
            && (LegacyValue(root, "CraftingExpMultiplier") is not null
                || LegacyValue(root, "HideoutExpMultiplier") is not null);
    }

    /// <summary>
    /// Carry the previous release's settings over, on demand from the migrate endpoint - never at boot.
    /// <para>
    /// The old mod's config was two fields rather than a key map. Writing the file back in the new shape
    /// is what makes this a one-shot: the legacy fields are gone afterwards, so asking again finds nothing
    /// and a value the user has since changed on the page is never overwritten.
    /// </para>
    /// </summary>
    /// <returns>True when legacy fields were present (and are now gone).</returns>
    public bool RunLegacyMigration()
    {
        var root = ReadLegacyRoot();

        if (root == null)
        {
            return false;
        }

        var crafting = LegacyValue(root, "CraftingExpMultiplier");
        var hideout = LegacyValue(root, "HideoutExpMultiplier");

        if (crafting is null && hideout is null)
        {
            return false;
        }

        var migrated = new List<string>();

        if (hideout is { } hideoutValue && Math.Abs(hideoutValue - 1.0) > 1e-9)
        {
            // Exactly the two values the old mod scaled, so these are the same levers and not an approximation.
            MapLegacy("HideoutManagement", "SkillPointsPerCraft", hideoutValue, migrated);
            MapLegacy("HideoutManagement", "SkillPointsPerAreaUpgrade", hideoutValue, migrated);
        }

        if (crafting is { } craftingValue && Math.Abs(craftingValue - 1.0) > 1e-9)
        {
            // Deliberately not mapped onto Crafting.PointsPerCraftingCycle, which is the near-miss. The old
            // field scaled hideoutConfig.CraftingExpAmount, added once per *alternating* craft in a module;
            // PointsPerCraftingCycle is the separate rate paid for *hours spent crafting*, added a few lines
            // further down the same method in HideoutController. One is not the other, and pointing at it
            // would multiply the wrong crafting XP while looking like it had worked.
            logger.Warning(
                $"[SkillMultiplier] Your old config set CraftingExpMultiplier = {craftingValue}. That scaled "
                + "crafting XP per alternating craft in a hideout module, which this version has no multiplier "
                + "for. For the same effect set craftingExpAmount in SPT_Data\\configs\\hideout.json directly; "
                + "the Crafting multipliers on the page scale the separate hours-of-crafting rate."
            );
        }

        if (migrated.Count > 0)
        {
            logger.Success($"[SkillMultiplier] Carried over the old config: {string.Join(", ", migrated)}");
        }

        SaveConfig();

        return true;
    }

    /// <summary>
    /// The user declined the migration: drop the legacy fields so the page stops asking, without applying
    /// anything. Explicit and permanent, which is why it only runs from the migrate endpoint.
    /// </summary>
    /// <returns>True when legacy fields were present (and are now gone).</returns>
    public bool DeclineLegacyMigration()
    {
        if (!HasLegacyServerConfig())
        {
            return false;
        }

        logger.Info("[SkillMultiplier] Leaving the previous release's settings unmigrated, as asked.");

        // A save in the new shape is exactly a file without the legacy fields.
        SaveConfig();

        return true;
    }

    /// <summary>The config file as JSON, or null when it is missing or malformed. Never throws.</summary>
    private JsonObject? ReadLegacyRoot()
    {
        try
        {
            if (!File.Exists(ConfigPath))
            {
                return null;
            }

            return JsonNode.Parse(File.ReadAllText(ConfigPath)) as JsonObject;
        }
        catch (Exception ex)
        {
            logger.Warning($"[SkillMultiplier] Could not read config.json to look for old settings: {ex.Message}");

            return null;
        }
    }

    /// <summary>
    /// The previous release's fields still sitting in config.json. A rewrite of the file - Reset, or any
    /// save - clears what the page shows, never what it cannot see, so callers that rewrite put this back.
    /// </summary>
    public Dictionary<string, double> LegacySnapshot()
    {
        var snapshot = new Dictionary<string, double>(StringComparer.OrdinalIgnoreCase);
        var root = ReadLegacyRoot();

        if (root == null)
        {
            return snapshot;
        }

        foreach (var key in new[] { "CraftingExpMultiplier", "HideoutExpMultiplier" })
        {
            if (LegacyValue(root, key) is { } value)
            {
                snapshot[key] = value;
            }
        }

        return snapshot;
    }

    /// <summary>
    /// Put back what <see cref="LegacySnapshot"/> took. Never throws: losing the snapshot costs a pending
    /// migration question, which is exactly what this exists to prevent - but failing a save over it would
    /// be worse.
    /// </summary>
    public void RestoreLegacySnapshot(Dictionary<string, double> snapshot)
    {
        if (snapshot.Count == 0)
        {
            return;
        }

        try
        {
            if (JsonNode.Parse(File.ReadAllText(ConfigPath)) is not JsonObject root)
            {
                return;
            }

            foreach (var (key, value) in snapshot)
            {
                root[key] = value;
            }

            var tmp = ConfigPath + ".tmp";
            File.WriteAllText(tmp, root.ToJsonString(new JsonSerializerOptions { WriteIndented = true }));
            File.Move(tmp, ConfigPath, overwrite: true);
        }
        catch (Exception ex)
        {
            logger.Warning($"[SkillMultiplier] Could not keep the previous release's settings: {ex.Message}");
        }
    }

    private void MapLegacy(string skill, string field, double value, List<string> migrated)
    {
        var key = $"Settings.{skill}.{field}";

        if (!Catalog.All.Any(entry => entry.Key == key))
        {
            logger.Warning($"[SkillMultiplier] No catalog entry for {key}; leaving the old value {value} alone.");
            return;
        }

        // A value typed on the page wins: this fills in what is not already set, it does not impose.
        if (Config.Multipliers.ContainsKey(key))
        {
            return;
        }

        Config.Multipliers[key] = value;
        migrated.Add($"{key} = {value}");
    }

    private static double? LegacyValue(JsonObject root, string name)
    {
        if (root.TryGetPropertyValue(name, out var node)
            && node is JsonValue value
            && value.TryGetValue<double>(out var parsed))
        {
            return parsed;
        }

        return null;
    }

    /// <summary>
    /// Snapshot the untouched values. Only ever runs once, and only before any multiplier has been
    /// applied - re-capturing later would fold the previous multiplier into the new base.
    /// </summary>
    private void CaptureBase()
    {
        if (_base.Count > 0)
        {
            return;
        }

        var settings = globalTable.Configuration.SkillsSettings;

        foreach (var entry in Catalog.All)
        {
            try
            {
                _base[entry.Key] = entry.Get(settings);
            }
            catch (Exception ex)
            {
                logger.Warning($"[SkillMultiplier] Could not read base value for {entry.Key}: {ex.Message}");
            }
        }
    }

    /// <summary>Push the current config into the live table. Safe to call repeatedly.</summary>
    public void Apply()
    {
        lock (_gate)
        {
            var settings = globalTable.Configuration.SkillsSettings;
            var changed = 0;

            foreach (var entry in Catalog.All)
            {
                if (!_base.TryGetValue(entry.Key, out var baseValue))
                {
                    continue;
                }

                // Row multiplier only - the global must never enter here. It is applied exactly once, by the
                // client at Skill.OnTrigger; baking it into globals as well would multiply every both-halves
                // skill by it twice, silently.
                var multiplier = Config.Enabled ? GetMultiplier(entry.Key) : 1.0;
                var target = baseValue * multiplier;

                try
                {
                    entry.Set(settings, target);

                    if (Math.Abs(multiplier - 1.0) > double.Epsilon)
                    {
                        changed++;
                    }
                }
                catch (Exception ex)
                {
                    logger.Warning($"[SkillMultiplier] Could not write {entry.Key}: {ex.Message}");
                }
            }

            logger.Info(
                $"[SkillMultiplier] Applied {changed} non-default multiplier(s) "
                + $"(mod {(Config.Enabled ? "enabled" : "DISABLED, values are vanilla")})."
            );
        }
    }

    private double GetMultiplier(string key)
    {
        if (!Config.Multipliers.TryGetValue(key, out var value))
        {
            return 1.0;
        }

        if (double.IsNaN(value) || double.IsInfinity(value) || value < 0)
        {
            logger.Warning($"[SkillMultiplier] Ignoring invalid multiplier {value} for {key}; using 1.0.");
            return 1.0;
        }

        return Math.Clamp(value, 0.0, MaxMultiplier);
    }

    /// <summary>
    /// Hard ceiling, enforced on the server so a hand-edited config cannot push a value nobody typed into the
    /// game's maths. It is a sanity bound, not a measured game limit.
    /// <para>
    /// Deliberately well above the page's slider travel (<c>SLIDER_MAX</c>, 10): the slider covers the range
    /// people actually tune, and the number box is how anyone goes further. A value above the slider's travel
    /// is not an error - the slider simply renders full, and this is the bound the server re-clamps to on save.
    /// </para>
    /// </summary>
    public const double MaxMultiplier = 1000.0;

    public double GetBase(string key) => _base.TryGetValue(key, out var v) ? v : 0.0;

    public void ReplaceConfig(SkillMultiplierConfig incoming)
    {
        // Field-by-field on purpose (the incoming record is request data and is not trusted), but it means
        // every new setting must be added here too: a field omitted from this list silently reverts to the
        // record's default, so the UI would accept a change and the server would ignore it.
        Config = new SkillMultiplierConfig
        {
            Enabled = incoming.Enabled,
            DisableFatigue = incoming.DisableFatigue,
            GlobalMultiplier = ClampGlobal(incoming.GlobalMultiplier),
            Multipliers = new Dictionary<string, double>(incoming.Multipliers ?? [], StringComparer.OrdinalIgnoreCase),
            Actions = new Dictionary<string, double>(incoming.Actions ?? [], StringComparer.OrdinalIgnoreCase),
        };

        Revision++;
    }

    /// <summary>
    /// The save endpoint's bound, shared with file load: a stored value the page would reject must not
    /// reach the table either. Invalid becomes 1.0 rather than 0 - a corrupt file should read as vanilla,
    /// not as zero XP.
    /// </summary>
    private static double ClampGlobal(double value)
    {
        if (double.IsNaN(value) || double.IsInfinity(value) || value < 0)
        {
            return 1.0;
        }

        return Math.Clamp(value, 0.0, MaxMultiplier);
    }

    /// <summary>Writes via a temp file and a move, so a crash mid-write cannot leave a truncated config.</summary>
    public void SaveConfig()
    {
        var json = JsonSerializer.Serialize(Config, new JsonSerializerOptions { WriteIndented = true });
        var tmp = ConfigPath + ".tmp";

        Directory.CreateDirectory(ModFolder);
        File.WriteAllText(tmp, json);
        File.Move(tmp, ConfigPath, overwrite: true);
    }

    /// <summary>
    /// Two mods scaling the same numbers compound, and this mod's snapshot would capture an
    /// already-scaled base, which is unrecoverable without a server restart. Detect and say so.
    /// </summary>
    private void WarnOnConflicts()
    {
        try
        {
            var modsRoot = Path.Combine(Path.GetDirectoryName(ModFolder) ?? ModFolder);
            var parent = Directory.GetParent(modsRoot)?.FullName ?? modsRoot;

            if (!Directory.Exists(parent))
            {
                return;
            }

            var conflicting = Directory
                .GetDirectories(parent)
                .Select(Path.GetFileName)
                .Where(name => name != null &&
                               name.Contains("SkillMultiplier", StringComparison.OrdinalIgnoreCase));

            foreach (var name in conflicting)
            {
                logger.Warning(
                    $"[SkillMultiplier] '{name}' appears to scale the same skill values. Two multipliers "
                    + "compound, and this mod captures its base values at startup, so a value it captured "
                    + "may already have been scaled. Disable one of the two mods."
                );
            }
        }
        catch (Exception ex)
        {
            logger.Debug($"[SkillMultiplier] Conflict scan skipped: {ex.Message}");
        }
    }
}
