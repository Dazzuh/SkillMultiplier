using System.Text.RegularExpressions;
using SPTarkov.Common.Models.Logging;
using SPTarkov.DI.Annotations;
using SPTarkov.Server.Core.DI;
using SPTarkov.Server.Core.Models.Common;
using SPTarkov.Server.Core.Models.Eft.Common;
using SPTarkov.Server.Core.Models.Utils;
using SPTarkov.Server.Core.Utils;

namespace SkillMultiplier;

/// <summary>
/// HTTP surface for the UI.
/// <para>
/// Routes are namespaced under <c>/skillmultiplier/</c> so they cannot collide with SPT's own or another
/// mod's. The page itself is <em>not</em> served here - it is a static file under the mod's
/// <c>wwwroot</c>, mapped by SPT's own middleware (see <see cref="ModMetadata"/>). That split matters:
/// SPT's static middleware runs after <c>HttpServer</c>, so only these JSON routes need registering.
/// </para>
/// </summary>
[Injectable]
public sealed class SkillMultiplierRouter(
    JsonUtil jsonUtil,
    SkillMultiplierRouterCallback callback
) : StaticRouter(jsonUtil, [
    new RouteAction<EmptyRequestData>(
        "/skillmultiplier/api/catalog",
        async (url, info, sessionId, output, cancellationToken) =>
            await callback.GetCatalog(url, sessionId, cancellationToken)
    ),
    new RouteAction<SkillMultiplierConfig>(
        "/skillmultiplier/api/save",
        async (url, info, sessionId, output, cancellationToken) =>
            await callback.Save(url, info, sessionId, cancellationToken)
    ),
    new RouteAction<EmptyRequestData>(
        "/skillmultiplier/api/reset",
        async (url, info, sessionId, output, cancellationToken) =>
            await callback.Reset(url, sessionId, cancellationToken)
    ),
    new RouteAction<EmptyRequestData>(
        "/skillmultiplier/api/table",
        async (url, info, sessionId, output, cancellationToken) =>
            await callback.GetTable(url, sessionId, cancellationToken)
    ),
    new RouteAction<ClientActionReport>(
        "/skillmultiplier/api/clientcatalog",
        async (url, info, sessionId, output, cancellationToken) =>
            await callback.ReportClientCatalog(url, info, sessionId, cancellationToken)
    ),
    new RouteAction<MigrateRequest>(
        "/skillmultiplier/api/migrate",
        async (url, info, sessionId, output, cancellationToken) =>
            await callback.Migrate(url, info, sessionId, cancellationToken)
    )
])
{
}

/// <summary>
/// One action as reported by a game client. This is the client's own vocabulary: the server cannot
/// enumerate these, so every field is a claim the client made about itself and none of it is trusted for
/// anything except display.
/// </summary>
public sealed record ClientActionEntry
{
    /// <summary><c>SkillId[index]</c>.</summary>
    public string Key { get; set; } = string.Empty;

    public string Skill { get; set; } = string.Empty;

    public int Index { get; set; }

    /// <summary>
    /// The name the game gives this action - the <c>SkillManager</c> field it is held in, as the client
    /// reports it. Null when the client could not find one. Not interpreted: see the router's <c>Humanise</c>.
    /// </summary>
    public string? Member { get; set; }

    /// <summary>The action's own coefficient as the game reports it. Not the amount granted - see ObservedXp.</summary>
    public double Factor { get; set; }

    /// <summary>
    /// The highest amount the game has granted this action this session, before any multiplier, and how many
    /// events that rests on. Zero count means the action has not paid out yet and has no base to show.
    /// </summary>
    public double ObservedXp { get; set; }

    public int ObservedCount { get; set; }

    /// <summary>
    /// Whether the client's own game refuses to progress this skill locally. True means the client's progress
    /// path never runs for the skill, so no client-side multiplier - per-row or global - can reach it, and its
    /// server rows are the only lever. The client reads this off the running skill rather than from a list.
    /// </summary>
    public bool ServerAuthoritative { get; set; }
}

/// <summary>Request body of <c>/skillmultiplier/api/clientcatalog</c>.</summary>
public sealed record ClientActionReport : IRequestData
{
    public string Type { get; set; } = "catalog";

    public List<ClientActionEntry> Actions { get; set; } = [];

    /// <summary>
    /// Whether that client still holds the previous release's config. The page asks about migrating it;
    /// the actions above are unaffected either way.
    /// </summary>
    public bool LegacyDetected { get; set; }
}

/// <summary>Request body of <c>/skillmultiplier/api/migrate</c>.</summary>
public sealed record MigrateRequest : IRequestData
{
    /// <summary>"server" for config.json, "client" for connected games' own configs.</summary>
    public string Scope { get; set; } = string.Empty;

    /// <summary>False means decline: drop the question without applying anything.</summary>
    public bool Migrate { get; set; } = true;
}

/// <summary>A reported client action plus the multiplier the server holds for it - what the UI draws.</summary>
public sealed record ClientActionView
{
    public string Key { get; set; } = string.Empty;

    public string Skill { get; set; } = string.Empty;

    public double Factor { get; set; }

    public double ObservedXp { get; set; }

    public int ObservedCount { get; set; }

    public double Multiplier { get; set; } = 1.0;
}

/// <summary>One action as the UI needs it: the current number, and the slider position over it.</summary>
public sealed record CatalogAction
{
    /// <summary><see cref="BaseSource"/> for a number read from the game's globals table.</summary>
    public const string GlobalsBase = "globals";

    /// <summary><see cref="BaseSource"/> for a number measured from the connected client.</summary>
    public const string ObservedBase = "observed";

    public string Key { get; set; } = string.Empty;

    public string Label { get; set; } = string.Empty;

    /// <summary>
    /// The vanilla figure to multiply. Only meaningful alongside <see cref="BaseSource"/>.
    /// </summary>
    public double Base { get; set; }

    /// <summary>
    /// Where <see cref="Base"/> came from, or null when there is no base to show.
    /// <para>
    /// <c>globals</c> is a number read from the game's globals table - the value the server itself scales.
    /// <c>observed</c> is the highest amount the connected client has seen the game grant the action this
    /// session, before any multiplier. Client-owned actions have no globals entry, and the client's own
    /// coefficient is not the amount (it is 1 for several actions), so an observation is the only honest
    /// figure available for them - and it does not exist until the action has actually paid out.
    /// </para>
    /// </summary>
    public string? BaseSource { get; set; } = GlobalsBase;

    public double Multiplier { get; set; } = 1.0;

    /// <summary>
    /// The action's own coefficient as the game reports it, for rows with no observed amount yet. The UI
    /// shows it as a rate, never as a grant - except a bare 1, which is the field's default rather than
    /// information. See <see cref="BaseSource"/>.
    /// </summary>
    public double Factor { get; set; }

    /// <summary>
    /// A server-side row that duplicates client-side rows for the same skill, hidden under Advanced config
    /// by the UI. Set where the row is built, from the per-request server-owned set.
    /// </summary>
    public bool Advanced { get; set; }

    /// <summary>What the action is and when it fires, so a slider's meaning is not guesswork.</summary>
    public string Description { get; set; } = string.Empty;

    /// <summary>Set when the same globals number feeds several in-game skills.</summary>
    public string? Shared { get; set; }
}

public sealed record CatalogGroup
{
    public string Skill { get; set; } = string.Empty;

    /// <summary>
    /// What the game calls this skill - "Field Medicine" for <c>FieldMedicine</c>, read from its locale
    /// database. Null when the locale has nothing for it. Presentation only: <see cref="Skill"/> is what keys
    /// every multiplier, so this never changes what a config means.
    /// </summary>
    public string? DisplayName { get; set; }

    /// <summary>The game's own description of the skill, when it has one. Shown as the heading's tooltip.</summary>
    public string? Description { get; set; }

    /// <summary>One line on where this skill's XP comes from.</summary>
    public string Summary { get; set; } = string.Empty;

    /// <summary>
    /// Whether the page's global multiplier reaches this skill. False for the server-owned skills, whose
    /// override never runs the client's patch point - the UI's math column must not compound a global
    /// that the game will never apply. Set where the group is built, from the per-request server-owned set.
    /// </summary>
    public bool GlobalApplies { get; set; } = true;

    public List<CatalogAction> Actions { get; set; } = [];
}

public sealed record UnsupportedNote
{
    public string Skill { get; set; } = string.Empty;

    /// <summary>What the game calls it; see <see cref="CatalogGroup.DisplayName"/>.</summary>
    public string? DisplayName { get; set; }

    public string Note { get; set; } = string.Empty;
}

public sealed record CatalogResponse
{
    public bool Enabled { get; set; }

    public double MaxMultiplier { get; set; }

    /// <summary>The global multiplier, applied by the client on top of every per-row value.</summary>
    public double GlobalMultiplier { get; set; } = 1.0;

    /// <summary>
    /// Whether the server's own config file still holds the previous release's settings. The page shows
    /// the migration question only for this, so it never nags a fresh install.
    /// </summary>
    public bool LegacyServerConfig { get; set; }

    /// <summary>
    /// Whether any connected game client reports a previous release's config of its own. Absent while no
    /// game is running: a game config can only be migrated by its game, so there is nothing to ask.
    /// </summary>
    public bool ClientLegacyDetected { get; set; }

    public List<CatalogGroup> Groups { get; set; } = [];

    public List<UnsupportedNote> Unsupported { get; set; } = [];

    /// <summary>
    /// Actions reported by connected game clients, keyed <c>SkillId[index]</c>. Empty until a client has
    /// introduced itself, which is also why it is a separate list rather than extra entries in
    /// <see cref="Groups"/>: these cannot be discovered server-side, so their absence means "no client has
    /// told us", not "this skill has no actions".
    /// </summary>
    public List<ClientActionView> ClientActions { get; set; } = [];

    /// <summary>How many clients have reported, so the UI can say whether the list above is the whole picture.</summary>
    public int ClientReporters { get; set; }

    /// <summary>
    /// When the list above was received, if it came from a report kept across a restart rather than from a
    /// connected client. Null means it is live. The UI needs the difference: an observed amount is a
    /// per-session figure, so carried over it means "in a previous session", not "now".
    /// </summary>
    public DateTimeOffset? ClientReportCachedAt { get; set; }
}

public sealed record SaveResult
{
    public bool Saved { get; set; }

    public string Message { get; set; } = string.Empty;
}

[Injectable]
public sealed class SkillMultiplierRouterCallback(
    ISptLogger<SkillMultiplierRouterCallback> logger,
    HttpResponseUtil httpResponseUtil,
    SkillMultiplierMod mod,
    SkillMultiplierWsHandler wsHandler
)
{
    /// <summary>
    /// Client-side action keys are <c>SkillId[index]</c>, plus <c>SkillId[Workout]</c> for the hideout gym.
    /// Shape only - see the note in Save.
    /// <para>
    /// The gym gets its own key because it is not an action: the server pays it (see
    /// <c>HideoutController.ApplyWorkoutSkillGain</c>), so there is no index and no client-side action to
    /// match. The grant patch still finds it, because that lookup scans these keys by skill name rather
    /// than by action identity - which is why the key belongs in this space and not in the catalog, where
    /// every row has to name a globals field it reads and writes.
    /// </para>
    /// </summary>
    private static readonly Regex ActionKeyPattern = new(
        @"^[A-Za-z]+\[(\d+|Workout)\]$",
        RegexOptions.Compiled | RegexOptions.IgnoreCase
    );

    /// <summary>
    /// Shown on a skill that is scaled from both halves. Not a prohibition: the two mechanisms are
    /// independent and the user may well want both. What must not happen is it being a surprise.
    /// </summary>
    private const string BothHalvesNote =
        "Scaled from BOTH halves. This skill has a server-side multiplier and a client-side one, and the two "
        + "multiply together: 2.00 on each gives 4.00 in game, not 2.00.";

    /// <summary>
    /// The skills the hideout gym pays into: one per successful repetition, drawn at random. Observed in
    /// game rather than read from a table, because the reward table lives in the client and no client sends
    /// it - the server is handed the rewards per repetition and picks from them.
    /// </summary>
    private static readonly HashSet<string> WorkoutSkills = new(StringComparer.OrdinalIgnoreCase)
    {
        "Strength",
        "Endurance",
    };

    /// <summary>
    /// Adds the gym's row for a skill.
    /// <para>
    /// Listed from the config rather than from a client report, because the gym is not an action: the server
    /// pays it (see <c>HideoutController.ApplyWorkoutSkillGain</c>), so there is no action for the client to
    /// report and the row would otherwise be settable only by hand-editing the file.
    /// </para>
    /// <para>
    /// It carries no base on purpose: the amount comes out of the hideout's own reward table, which is neither
    /// a globals field nor something the client measures, so there is no vanilla figure to show.
    /// </para>
    /// </summary>
    private void AppendWorkoutRow(CatalogGroup group)
    {
        var key = group.Skill + "[Workout]";

        if (group.Actions.Any(a => a.Key.Equals(key, StringComparison.OrdinalIgnoreCase)))
        {
            return;
        }

        group.Actions.Add(new CatalogAction
        {
            Key = key,
            Label = "Workout (hideout gym)",
            Base = 0,
            BaseSource = null,
            Multiplier = ResolveActionMultiplier(key),
            Advanced = false,
            Description =
                "XP for a successful repetition on the hideout gym. The server pays it, and picks strength or "
                + "endurance at random for each repetition, so this row applies to whichever it picks. Muscle "
                + "pain halves the payout while it lasts.",
        });
    }

    /// <summary>
    /// Skills the client never applies multipliers to, so their server rows are the only lever rather than a
    /// duplicate of rows that do nothing. The reason belongs to the game: a <c>ClientAuthorizedSkill</c>
    /// overrides <c>OnTrigger</c> to log and return without calling base, which puts the client's entire
    /// progress path - this mod's prefix and the global multiplier with it - out of reach for that skill.
    /// Read from the running game via what clients report, so a mod that builds a skill the same way is
    /// covered without being named. See <see cref="SkillMultiplierMod.ServerOwnedSkills"/>.
    /// </summary>
    public ValueTask<string> GetCatalog(string url, MongoId sessionId, CancellationToken cancellationToken)
    {
        var groups = new List<CatalogGroup>();
        var reported = mod.ClientActions;

        // Which skills the client cannot apply anything to, for this request.
        var serverOwned = mod.ServerOwnedSkills();

        // The skills the server is actually scaling: the catalog entries that currently hold a multiplier,
        // mapped to their skill through the catalog rather than by parsing the key. A catalog key is
        // `Settings.<Skill>.<Field>`, so splitting it on '.' yields "Settings" and would never match a skill.
        var scaledByServer = Catalog
            .All.Where(entry => mod.Config.Multipliers.ContainsKey(entry.Key))
            .Select(entry => entry.Skill)
            .ToHashSet(StringComparer.OrdinalIgnoreCase);

        foreach (var skill in Catalog.SkillOrder)
        {
            var group = new CatalogGroup
            {
                Skill = skill,
                DisplayName = mod.SkillDisplayName(skill),
                Description = mod.SkillDescription(skill),
                GlobalApplies = !serverOwned.Contains(skill),
                Summary = Catalog.SkillSummaries.TryGetValue(skill, out var summary) ? summary : string.Empty,
            };

            // A server row duplicates the client's rows when the client reports actions for the same skill -
            // except for the server-owned skills, whose client rows the client never applies. Those stay.
            var duplicated = !serverOwned.Contains(skill)
                && reported.Values.Any(a => a.Skill.Equals(skill, StringComparison.OrdinalIgnoreCase));

            foreach (var entry in Catalog.All.Where(e => e.Skill == skill))
            {
                group.Actions.Add(new CatalogAction
                {
                    Key = entry.Key,
                    Label = entry.Action,
                    Base = mod.GetBase(entry.Key),
                    Multiplier = ResolveMultiplier(entry.Key),
                    Advanced = duplicated,
                    Description = entry.Description,
                    Shared = entry.SharedWith,
                });
            }

            if (WorkoutSkills.Contains(skill))
            {
                AppendWorkoutRow(group);
            }

            AppendClientActions(group, reported, scaledByServer, serverOwned);

            if (group.Actions.Count > 0)
            {
                groups.Add(group);
            }
        }

        // Skills the server catalog knows nothing about: a skills mod's own additions, or a skill whose
        // value is not in globals at all. One of these exists only because a client reported it, so rendering
        // them from the catalog alone would leave them permanently invisible.
        foreach (var skill in reported.Values.Select(a => a.Skill).Distinct(StringComparer.OrdinalIgnoreCase))
        {
            if (groups.Any(g => g.Skill.Equals(skill, StringComparison.OrdinalIgnoreCase)))
            {
                continue;
            }

            var group = new CatalogGroup
            {
                Skill = skill,
                DisplayName = mod.SkillDisplayName(skill),
                Description = mod.SkillDescription(skill),
                GlobalApplies = !serverOwned.Contains(skill),
                Summary = "Not in the server's globals - these actions exist only inside the game client.",
            };

            AppendClientActions(group, reported, scaledByServer, serverOwned);

            if (group.Actions.Count > 0)
            {
                groups.Add(group);
            }
        }

        // Which skills have something to tune right now, from both halves. The not-tunable panel's claim is
        // "there is no slider for this, and here is why", which stops being true the moment a skills mod adds
        // an action to one of them - so the list is filtered rather than trusted.
        var skillsWithActions = new HashSet<string>(StringComparer.OrdinalIgnoreCase);

        foreach (var entry in Catalog.All)
        {
            skillsWithActions.Add(entry.Skill);
        }

        foreach (var action in reported.Values)
        {
            skillsWithActions.Add(action.Skill);
        }

        var response = new CatalogResponse
        {
            Enabled = mod.Config.Enabled,
            MaxMultiplier = SkillMultiplierMod.MaxMultiplier,
            GlobalMultiplier = mod.Config.GlobalMultiplier,
            LegacyServerConfig = mod.HasLegacyServerConfig(),
            ClientLegacyDetected = mod.ClientLegacyDetected,
            Groups = groups,
            Unsupported = Catalog.Unsupported
                .Where(u => u.Skill
                    .Split('/', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries)
                    .All(skill => !skillsWithActions.Contains(skill)))
                .Select(u => new UnsupportedNote
                {
                    Skill = u.Skill,
                    DisplayName = mod.SkillDisplayName(u.Skill),
                    Note = u.Note,
                })
                .ToList(),
            // The same set as the rows merged into Groups above, kept in client-key form so the reported
            // list can be read straight off the endpoint without parsing labels.
            ClientActions = reported
                .Values.OrderBy(a => a.Skill, StringComparer.OrdinalIgnoreCase)
                .ThenBy(a => a.Index)
                .Select(a => new ClientActionView
                {
                    Key = a.Key,
                    Skill = a.Skill,
                    Factor = a.Factor,
                    ObservedXp = a.ObservedXp,
                    ObservedCount = a.ObservedCount,
                    Multiplier = ResolveActionMultiplier(a.Key),
                })
                .ToList(),
            ClientReporters = mod.ClientReporterCount,
            ClientReportCachedAt = mod.ClientActionsFromCache ? mod.CachedReportAt : null,
        };

        return new ValueTask<string>(httpResponseUtil.NoBody(response));
    }

    /// <summary>
    /// <c>WeaponReloadAction</c> becomes "Weapon Reload Action". The words are the game's own - this only puts
    /// spaces in, so a label built from it says what the action is without inventing anything.
    /// </summary>
    private static string Humanise(string member)
    {
        var text = string.Empty;

        for (var i = 0; i < member.Length; i++)
        {
            if (i > 0 && char.IsUpper(member[i]) && !char.IsUpper(member[i - 1]))
            {
                text += " ";
            }

            text += member[i];
        }

        return text;
    }

    /// <summary>
    /// Merge a skill's client-reported actions into its group, and flag the group if the server scales the
    /// same skill - the one case where two configured numbers produce a third.
    /// </summary>
    private void AppendClientActions(
        CatalogGroup group,
        IReadOnlyDictionary<string, ClientActionEntry> reported,
        HashSet<string> scaledByServer,
        HashSet<string> serverOwned)
    {
        var mine = reported
            .Values.Where(a => a.Skill.Equals(group.Skill, StringComparison.OrdinalIgnoreCase))
            .OrderBy(a => a.Index)
            .ToList();

        if (mine.Count == 0)
        {
            return;
        }

        foreach (var action in mine)
        {
            var named = ClientActionNames.TryGet(action.Key, out var entry);

            // The game names every action it constructs, by the field it holds it in, and the client reports
            // that name. It is what an action outside the curated table can be labelled with - which is every
            // action a mod adds. Its position is only the last resort, for an action not held in a field.
            var member = string.IsNullOrEmpty(action.Member) ? null : Humanise(action.Member);

            group.Actions.Add(new CatalogAction
            {
                Key = action.Key,
                Label = named ? entry.Name : member ?? $"Action {action.Index + 1}",
                // The observed amount, deliberately not the client's own coefficient - see
                // CatalogAction.BaseSource. Absent until the game has paid this action out at least once.
                Base = action.ObservedCount > 0 ? action.ObservedXp : 0,
                BaseSource = action.ObservedCount > 0 ? CatalogAction.ObservedBase : null,
                Multiplier = ResolveActionMultiplier(action.Key),
                Factor = action.Factor,
                Description = named
                    ? entry.Description
                    : member != null
                        ? $"The game's own name for this action, from the skill data it builds. Its base figure "
                            + "is what the game has granted it so far."
                        : "The game gives this action no name of its own, and this version does not describe it. "
                            + "Its base figure is what the game has granted it so far.",
            });
        }

        if (scaledByServer.Contains(group.Skill)
            && !serverOwned.Contains(group.Skill)
            && mine.Any(a => Tuned(ResolveActionMultiplier(a.Key))))
        {
            group.Summary = string.IsNullOrEmpty(group.Summary)
                ? BothHalvesNote
                : $"{group.Summary} {BothHalvesNote}";
        }
    }

    private static bool Tuned(double multiplier) => Math.Abs(multiplier - 1.0) > 1e-9;

    /// <summary>
    /// Accept a client's action list. Nothing here is validated against anything: a client is the only
    /// authority on what its own actions are, and the worst a false claim achieves is a slider that does
    /// nothing, because a key the client does not recognise is ignored when the table is applied.
    /// </summary>
    public ValueTask<string> ReportClientCatalog(
        string url,
        ClientActionReport info,
        MongoId sessionId,
        CancellationToken cancellationToken)
    {
        var actions = info?.Actions ?? [];

        mod.ReportClientActions(actions, sessionId.ToString(), info?.LegacyDetected ?? false);

        return new ValueTask<string>(httpResponseUtil.NoBody(new SaveResult
        {
            Saved = true,
            Message = $"Recorded {actions.Count} action(s) from client {sessionId}.",
        }));
    }

    /// <summary>
    /// The page's answer to a legacy config. Server scope migrates or drops config.json's old fields;
    /// client scope latches an answer into the pushed table, because only each game can migrate its own
    /// file. Either way the legacy fields end up gone, so this answers once.
    /// </summary>
    public async ValueTask<string> Migrate(
        string url,
        MigrateRequest info,
        MongoId sessionId,
        CancellationToken cancellationToken)
    {
        var scope = info?.Scope ?? string.Empty;
        var migrate = info?.Migrate ?? true;

        if (scope.Equals("server", StringComparison.OrdinalIgnoreCase))
        {
            var changed = migrate ? mod.RunLegacyMigration() : mod.DeclineLegacyMigration();

            if (!changed)
            {
                return httpResponseUtil.NoBody(new SaveResult
                {
                    Saved = true,
                    Message = "Nothing to migrate: the previous release's settings are already gone.",
                });
            }

            mod.Apply();
            mod.SaveConfig();
            await wsHandler.BroadcastAsync();

            var message = migrate
                ? "Migrated the previous release's server settings. Restart the game to load them."
                : "Left the previous release's server settings behind. This won't be asked again.";

            logger.Info($"[SkillMultiplier] {message}");

            return httpResponseUtil.NoBody(new SaveResult { Saved = true, Message = message });
        }

        if (scope.Equals("client", StringComparison.OrdinalIgnoreCase))
        {
            // Latched, not broadcast-and-cleared: a game that connects later gets the same answer instead
            // of re-asking, and each game's own marker still makes it act exactly once.
            mod.ClientLegacyRequest = migrate ? "migrate" : "decline";
            mod.BumpRevision();
            await wsHandler.BroadcastAsync();

            var message = migrate
                ? "Migration requested. Each connected game carries its own config over within a few seconds."
                : "Game configs left alone. Each game was told not to ask again.";

            logger.Info($"[SkillMultiplier] {message}");

            return httpResponseUtil.NoBody(new SaveResult { Saved = true, Message = message });
        }

        return httpResponseUtil.NoBody(new SaveResult
        {
            Saved = false,
            Message = $"Unknown migration scope '{scope}': use 'server' or 'client'.",
        });
    }

    /// <summary>
    /// The same payload the websocket pushes. Fallback for a client whose websocket could not be
    /// established, and a curl-able view of exactly what clients are being told.
    /// </summary>
    public ValueTask<string> GetTable(string url, MongoId sessionId, CancellationToken cancellationToken)
    {
        return new ValueTask<string>(httpResponseUtil.NoBody(wsHandler.BuildTableMessage()));
    }

    public async ValueTask<string> Save(
        string url,
        SkillMultiplierConfig info,
        MongoId sessionId,
        CancellationToken cancellationToken)
    {
        // Never trust the caller: the UI clamps too, but a hand-rolled POST must not be able to push a
        // negative, NaN or absurd value into the game's maths.
        var known = Catalog.All.Select(e => e.Key).ToHashSet(StringComparer.OrdinalIgnoreCase);
        var cleaned = new Dictionary<string, double>(StringComparer.OrdinalIgnoreCase);
        var rejected = 0;
        var unknown = new List<string>();

        foreach (var (key, value) in info.Multipliers ?? [])
        {
            if (string.IsNullOrWhiteSpace(key))
            {
                continue;
            }

            // The catalog is derived at runtime from what the client actually reads, so a key outside it
            // can never be applied. Storing it would be dead weight that looks like a working setting.
            if (!known.Contains(key))
            {
                unknown.Add(key);
                continue;
            }

            if (double.IsNaN(value) || double.IsInfinity(value) || value < 0)
            {
                rejected++;
                continue;
            }

            // Values at or below vanilla are simply not stored - absence means 1.0.
            var clamped = Math.Clamp(value, 0.0, SkillMultiplierMod.MaxMultiplier);

            if (Math.Abs(clamped - 1.0) > 1e-9)
            {
                cleaned[key] = clamped;
            }
        }

        if (unknown.Count > 0)
        {
            logger.Warning($"[SkillMultiplier] Ignored {unknown.Count} unknown key(s): {string.Join(", ", unknown)}");
        }

        // Client-keyed action multipliers. Unlike the catalog keys above, these cannot be validated against
        // a server-side table - the actions only exist inside the running client - so only the key's shape
        // and the value's range are checked, and a well-formed unknown key is kept rather than dropped.
        var cleanedActions = new Dictionary<string, double>(StringComparer.OrdinalIgnoreCase);
        var badActionKeys = 0;

        foreach (var (key, value) in info.Actions ?? [])
        {
            if (string.IsNullOrWhiteSpace(key))
            {
                continue;
            }

            if (!ActionKeyPattern.IsMatch(key))
            {
                badActionKeys++;
                continue;
            }

            if (double.IsNaN(value) || double.IsInfinity(value) || value < 0)
            {
                rejected++;
                continue;
            }

            var clampedAction = Math.Clamp(value, 0.0, SkillMultiplierMod.MaxMultiplier);

            if (Math.Abs(clampedAction - 1.0) > 1e-9)
            {
                cleanedActions[key] = clampedAction;
            }
        }

        if (badActionKeys > 0)
        {
            logger.Warning($"[SkillMultiplier] Ignored {badActionKeys} action key(s) not shaped like SkillId[index].");
        }

        // The global is one compounding number, validated like a row value. It is stored as a scalar on the
        // config - never distributed into the rows - so repeated saves cannot compound it into them.
        var globalRaw = info.GlobalMultiplier;
        var global = 1.0;

        if (double.IsNaN(globalRaw) || double.IsInfinity(globalRaw) || globalRaw < 0)
        {
            rejected++;
        }
        else
        {
            global = Math.Clamp(globalRaw, 0.0, SkillMultiplierMod.MaxMultiplier);
        }

        // A save rewrites the whole file, which would silently drop a pending migration with it. Snapshot
        // first - after the rewrite there is nothing left to take - and put the legacy fields back
        // untouched: the page cannot see them, so answering them stays the popup's job.
        var legacy = mod.LegacySnapshot();

        mod.ReplaceConfig(new SkillMultiplierConfig
        {
            Enabled = info.Enabled,
            DisableFatigue = info.DisableFatigue,
            GlobalMultiplier = global,
            Multipliers = cleaned,
            Actions = cleanedActions,
        });
        mod.Apply();
        mod.SaveConfig();
        mod.RestoreLegacySnapshot(legacy);

        // Push to every connected client. The multiplier and the fatigue switch are both applied
        // client-side, so this lands without a game restart; a client that is not running gets the table
        // when it next connects.
        await wsHandler.BroadcastAsync();

        var message = $"Saved {cleaned.Count} multiplier(s) and {cleanedActions.Count} action multiplier(s).";

        if (Math.Abs(global - 1.0) > 1e-9)
        {
            message += $" Global is {global:F2}x, live on clients without a restart.";
        }

        if (rejected > 0)
        {
            message += $" Rejected {rejected} invalid value(s).";
        }

        if (unknown.Count > 0)
        {
            message += $" Ignored {unknown.Count} unknown key(s).";
        }

        // Reported, not just logged: a key dropped for its shape is a setting the user made that will not
        // apply, and the message is the only place they would ever find that out.
        if (badActionKeys > 0)
        {
            message += $" Ignored {badActionKeys} unrecognised action key(s).";
        }

        // Only the globals half is read at client startup. Saying "restart the game" unconditionally was
        // wrong once the client learned to consume the push, and it is wrong the other way when nothing
        // server-side was touched at all.
        message += cleaned.Count > 0
            ? " Restart the game to load the server-side values; client-side ones are already live."
            : " Client-side values are already live.";

        logger.Info($"[SkillMultiplier] {message}");
        return httpResponseUtil.NoBody(new SaveResult { Saved = true, Message = message });
    }

    public async ValueTask<string> Reset(string url, MongoId sessionId, CancellationToken cancellationToken)
    {
        // Reset clears what the page shows - not the legacy fields it cannot see. Without the snapshot the
        // rewrite below would silently answer a pending migration with "gone".
        var legacy = mod.LegacySnapshot();

        mod.ReplaceConfig(new SkillMultiplierConfig { Enabled = true });
        mod.Apply();
        mod.SaveConfig();
        mod.RestoreLegacySnapshot(legacy);
        await wsHandler.BroadcastAsync();

        logger.Info("[SkillMultiplier] All multipliers reset to vanilla.");

        return httpResponseUtil.NoBody(new SaveResult
        {
            Saved = true,
            Message = "All multipliers reset to vanilla. Pushed to connected clients."
                + (legacy.Count > 0 ? " The previous release's settings are untouched - the migration question stays." : string.Empty),
        });
    }

    private double ResolveMultiplier(string key) =>
        mod.Config.Multipliers.TryGetValue(key, out var v) ? v : 1.0;

    /// <summary>The multiplier currently held for a client-keyed action; 1.0 when unset.</summary>
    private double ResolveActionMultiplier(string key) =>
        mod.Config.Actions.TryGetValue(key, out var v) ? v : 1.0;
}
