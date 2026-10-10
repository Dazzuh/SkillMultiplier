using System.Reflection;
using SPTarkov.Common.Models.Logging;
using SPTarkov.DI.Annotations;
using SPTarkov.Server.Core.DI;
using SPTarkov.Server.Core.Helpers.Server;
using SPTarkov.Server.Core.Models.Enums;
using SPTarkov.Server.Core.Models.Spt.Tables;
using SPTarkov.Server.Core.Services.Locales;

namespace SkillMultiplier;

/// <summary>
/// Owns the tuning state and pushes it into the live <see cref="GlobalTable"/>.
/// <para>
/// The transformation is expressed as <c>current = base * multiplier</c>, where <c>base</c> is snapshotted
/// from the in-memory table the first time this runs. That makes applying idempotent: calling it
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
/// Facade since M5: the logic lives in <see cref="ConfigStore"/>, <see cref="GlobalsApplier"/>,
/// <see cref="ClientReportStore"/>, <see cref="LegacyMigrator"/> and <see cref="SkillRuleEngine"/>. This class
/// keeps the DI wiring, the startup order, the revision counter, the save gate and the exact public
/// surface the router, the websocket handler and the grant patch program against. One shared gate object
/// is passed to the stateful components (<c>ConfigStore</c>, <c>GlobalsApplier</c>,
/// <c>ClientReportStore</c>, <see cref="SkillRuleEngine"/>) so the mutual exclusion the single
/// <c>_gate</c> lock used to give is unchanged; the config swap itself stays atomic-reference, and the
/// revision bump stays <c>Interlocked</c> (eventually consistent with the state it announces).
/// </para>
/// </summary>
[Injectable(InjectionType.Singleton, OnLoadOrder.PostLoad + 1)]
public sealed class SkillMultiplierMod : IOnLoad
{
    private readonly ISptLogger<SkillMultiplierMod> _logger;
    private readonly ModHelper _modHelper;
    private readonly GlobalTable _globalTable;

    private readonly object _gate = new();

    // Constructed here rather than injected so the container graph does not change:
    // nothing else resolves these types, and the router keeps talking to this facade.
    private readonly ConfigStore _config;
    private readonly GlobalsApplier _applier;
    private readonly ClientReportStore _reports;
    private readonly LegacyMigrator _legacy;
    private readonly SkillRuleEngine _rules;

    public SkillMultiplierMod(
        ISptLogger<SkillMultiplierMod> logger,
        ModHelper modHelper,
        GlobalTable globalTable,
        LocaleService localeService)
    {
        _logger = logger;
        _modHelper = modHelper;
        _globalTable = globalTable;
        _config = new ConfigStore(logger, modHelper, _gate);
        _applier = new GlobalsApplier(logger, _gate);
        _reports = new ClientReportStore(logger, _gate);
        _legacy = new LegacyMigrator(logger);
        _rules = new SkillRuleEngine(logger, localeService, _gate);
    }

    public SkillMultiplierConfig Config => _config.Config;

    /// <summary>
    /// One locked read of the whole tuning state for the save path: both maps and the scalars from a
    /// single generation, so cleaning can never mix a pre-swap map with a post-swap scalar.
    /// </summary>
    internal (Dictionary<string, double> Multipliers, Dictionary<string, double> Actions,
        bool Enabled, bool DisableFatigue, double GlobalMultiplier) SnapshotConfig()
        => _config.SnapshotAll();

    public string ModFolder { get; private set; } = string.Empty;

    public string ConfigPath => Path.Combine(ModFolder, "config.json");

    /// <summary>
    /// Bumped whenever the effective multiplier set changes, so a pushed table can be recognised as
    /// newer than the one a client already holds.
    /// </summary>
    public int Revision => Volatile.Read(ref _revision);

    private int _revision;

    /// <summary>Set the revision outright. Only the startup sequence uses this; everything else bumps.</summary>
    internal void ResetRevision(int value) => Interlocked.Exchange(ref _revision, value);

    /// <summary>
    /// Serializes overlapping UI saves: the save pipeline (replace, apply, persist, restore, broadcast)
    /// must not interleave with itself, or two near-simultaneous saves come out last-writer-wins with a
    /// skipped revision. Held across network IO, so it is a semaphore rather than the in-memory
    /// shared gate.
    /// </summary>
    internal SemaphoreSlim SaveGate { get; } = new(1, 1);

    /// <summary>
    /// The page's answer to a game client's legacy config, latched until the server restarts: "migrate"
    /// or "decline", null while unasked. Stored in the report store under the shared gate, so a set can
    /// never race a session drop's clear-check; late-connecting clients get the same answer rather
    /// than re-asking.
    /// </summary>
    public string? ClientLegacyRequest
    {
        get => _reports.LegacyRequest;
        set => _reports.SetLegacyRequest(value);
    }

    /// <summary>Bump the revision when the pushed table changed without the config doing so.</summary>
    public void BumpRevision() => Interlocked.Increment(ref _revision);

    /// <summary>
    /// Snapshot copy, taken under the shared gate, so a broadcast can never observe
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
    public string? SkillDisplayName(string skillId) => _rules.SkillDisplayName(skillId);

    /// <summary>
    /// The game's description for a skill, under the <c>&lt;SkillId&gt;Description</c> key it uses for skill
    /// tooltips. Null when it has none.
    /// </summary>
    public string? SkillDescription(string skillId) => _rules.SkillDescription(skillId);

    /// <summary>
    /// Every skill the client cannot apply a multiplier to: the names known without asking, plus whatever
    /// connected clients have reported. Read by the catalog build and by the repair patch.
    /// </summary>
    public HashSet<string> ServerOwnedSkills() => _rules.ServerOwnedSkills(_reports.UnauthorizedSnapshot());

    /// <summary>
    /// The multiplier that applies to skill XP the server grants itself. Null when the skill has no row
    /// set, when its rows disagree, or when the grant is already covered by value scaling - see
    /// <see cref="SkillRuleEngine.ServerGrantMultiplier"/>.
    /// </summary>
    public double? ServerGrantMultiplier(SkillTypes skill) => _rules.ServerGrantMultiplier(skill, _config);

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
    /// session and merged for display. Advisory by design - see <see cref="ClientReportStore"/>.
    /// </summary>
    public IReadOnlyDictionary<string, ClientActionEntry> ClientActions => _reports.UnionActions();

    /// <summary>How many clients have introduced themselves, so the UI can say whether the list is complete.</summary>
    public int ClientReporterCount => _reports.ReporterCount;

    /// <summary>Whether any connected game reports a legacy config of its own.</summary>
    public bool ClientLegacyDetected => _reports.LegacyDetected;

    /// <summary>When the client list being shown was received. Null while it comes from live clients.</summary>
    public DateTimeOffset? CachedReportAt => _reports.CachedReportAt;

    /// <summary>
    /// Whether the client rows are coming from the cache rather than from a connected client, so the UI can
    /// say that the observed amounts in them are from a previous session.
    /// </summary>
    public bool ClientActionsFromCache => _reports.ActionsFromCache;

    public string ClientReportCachePath => Path.Combine(ModFolder, "clientreport.json");

    /// <summary>
    /// Record one client's action list. A re-report replaces that client's previous set rather than adding
    /// to it, so a client that loses a mod's actions stops advertising them.
    /// </summary>
    public void ReportClientActions(IEnumerable<ClientActionEntry> actions, string sessionId, bool legacyDetected)
    {
        if (_reports.Report(actions, sessionId, legacyDetected))
        {
            Interlocked.Increment(ref _revision);
        }
    }

    /// <summary>
    /// Forget one session's report when its socket closes. Without this a disconnected client's entries
    /// linger: a departed legacy client would pin <see cref="ClientLegacyRequest"/> and a departed action
    /// list would outlive its game.
    /// Returns true when the migration latch was cleared and the caller must push the new table.
    /// </summary>
    public bool DropClientSession(string sessionId)
    {
        if (_reports.DropSession(sessionId))
        {
            Interlocked.Increment(ref _revision);
            return true;
        }

        return false;
    }

    public Task OnLoadAsync(CancellationToken cancellationToken)
    {
        ModFolder = _modHelper.GetAbsolutePathToModFolder(Assembly.GetExecutingAssembly());

        LoadConfig();
        _reports.CachePath = ClientReportCachePath;
        _reports.LoadCache();
        CaptureBase();
        Apply();
        ResetRevision(1);

        _logger.Success(
            $"[SkillMultiplier] Loaded. {Catalog.All.Count} tunable action(s), {_applier.BaseCount} base value(s) captured. "
            + $"Config: {ConfigPath}"
        );

        WarnOnConflicts();

        return Task.CompletedTask;
    }

    private void LoadConfig()
    {
        _config.Load(ConfigPath, ModFolder);

        // Detection only. Migrating here would take the choice away: the page asks first, and the migrate
        // endpoint runs this on demand.
        if (_legacy.Has(ConfigPath))
        {
            _logger.Info(
                "[SkillMultiplier] Found settings from the previous release (CraftingExpMultiplier and/or "
                + "HideoutExpMultiplier). They are left alone until the page asks to migrate them."
            );
        }
    }

    /// <summary>
    /// Whether the previous release's settings are still sitting in config.json. Read off the file rather
    /// than memory: the page asks about them, and the answer must still be right if the file changed.
    /// </summary>
    public bool HasLegacyServerConfig() => _legacy.Has(ConfigPath);

    /// <summary>
    /// Carry the previous release's settings over, on demand from the migrate endpoint - never at boot.
    /// </summary>
    /// <returns>True when legacy fields were present (and are now gone).</returns>
    public bool RunLegacyMigration() => _legacy.Run(_config, ConfigPath, SaveConfig);

    /// <summary>
    /// The user declined the migration: drop the legacy fields so the page stops asking, without applying
    /// anything. Explicit and permanent, which is why it only runs from the migrate endpoint.
    /// </summary>
    /// <returns>True when legacy fields were present (and are now gone).</returns>
    public bool DeclineLegacyMigration() => _legacy.Decline(_config, ConfigPath, SaveConfig);

    /// <summary>
    /// The previous release's fields still sitting in config.json. A rewrite of the file - Reset, or any
    /// save - clears what the page shows, never what it cannot see, so callers that rewrite put this back.
    /// </summary>
    public Dictionary<string, double> LegacySnapshot() => _legacy.Snapshot(ConfigPath);

    /// <summary>
    /// Put back what <see cref="LegacySnapshot"/> took. Never throws: losing the snapshot costs a pending
    /// migration question, which is exactly what this exists to prevent - but failing a save over it would
    /// be worse.
    /// </summary>
    public void RestoreLegacySnapshot(Dictionary<string, double> snapshot) => _legacy.Restore(ConfigPath, snapshot);

    /// <summary>Snapshot the untouched values. Only ever runs once, and only before any multiplier.</summary>
    private void CaptureBase() => _applier.CaptureBase(_globalTable.Configuration.SkillsSettings);

    /// <summary>Push the current config into the live table. Safe to call repeatedly.</summary>
    public void Apply() => _applier.Apply(_globalTable.Configuration.SkillsSettings, _config);

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

    public double GetBase(string key) => _applier.GetBase(key);

    public void ReplaceConfig(SkillMultiplierConfig incoming)
    {
        // Locked with the clear: a repair grant resolving ambiguity must not add to the warned set
        // while a save clears it (re-entrant with the grant path, which only reads).
        lock (_gate)
        {
            _config.Replace(incoming);

            // The rows changed, so any ambiguity verdict from before may no longer hold: clear the warned set
            // so a still-ambiguous skill warns again against the new values (and a resolved one goes quiet
            // until it disagrees anew).
            _rules.ClearAmbiguityRecord();

            Interlocked.Increment(ref _revision);
        }
    }

    /// <summary>Writes via a temp file and a move, so a crash mid-write cannot leave a truncated config.</summary>
    public void SaveConfig() => _config.Save(ConfigPath, ModFolder);

    /// <summary>
    /// Two mods scaling the same numbers compound, and this mod's snapshot would capture an
    /// already-scaled base, which is unrecoverable without a server restart. Detect and say so.
    /// </summary>
    private void WarnOnConflicts()
    {
        try
        {
            // ModFolder is already .../user/mods/<own-folder>: enumerate it directly. (An earlier version
            // went one level further up to .../user/, so a second copy inside user/mods - the exact
            // failure the README warns compounds - was never found.)
            var modsRoot = Path.GetDirectoryName(ModFolder) ?? ModFolder;

            if (!Directory.Exists(modsRoot))
            {
                return;
            }

            var ownPath = Path.GetFullPath(ModFolder).TrimEnd(Path.DirectorySeparatorChar);

            // Filesystem-appropriate case rules: Windows folds case, so an Ordinal check would treat a
            // differently-cased sibling as this mod and miss a real duplicate; elsewhere the folders really
            // are different, so only an exact match excludes. Server mods run on Linux hosts too.
            var pathComparison = OperatingSystem.IsWindows()
                ? StringComparison.OrdinalIgnoreCase
                : StringComparison.Ordinal;

            var conflicting = Directory
                .GetDirectories(modsRoot)
                .Where(dir => !Path.GetFullPath(dir).TrimEnd(Path.DirectorySeparatorChar)
                    .Equals(ownPath, pathComparison))
                .Select(Path.GetFileName)
                .Where(name => name != null && IsSameModFolder(name));

            foreach (var name in conflicting)
            {
                _logger.Warning(
                    $"[SkillMultiplier] '{name}' appears to scale the same skill values. Two multipliers "
                    + "compound, and this mod captures its base values at startup, so a value it captured "
                    + "may already have been scaled. Disable one of the two mods."
                );
            }
        }
        catch (Exception ex)
        {
            _logger.Debug($"[SkillMultiplier] Conflict scan skipped: {ex.Message}");
        }
    }

    /// <summary>
    /// Whether a sibling mod folder looks like another copy of this mod: the normalized name contains the
    /// mod name, so renamed copies (<c>dazzuh-skillmultiplier</c>), suffixed backups left inside
    /// <c>user/mods</c> (<c>SkillMultiplier.bak</c>) and plain duplicates all match. Our own folder is
    /// excluded by full-path comparison before this is asked, so a contains-match cannot self-fire.
    /// </summary>
    private static bool IsSameModFolder(string name)
    {
        var normalized = new string(name.Where(char.IsLetterOrDigit).ToArray()).ToLowerInvariant();

        return normalized.Contains("skillmultiplier", StringComparison.Ordinal);
    }
}
