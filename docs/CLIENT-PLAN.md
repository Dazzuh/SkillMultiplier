# Client half + SkillMultiplier migration — design

Status: **done and verified live.** The client half, the push channel and the server-side broadcast all run
against a real game client (§11); the merge into `SkillMultiplier` sits on the `feature/skill-xp-tuner`
branch, with both config migrations verified against seeded v1 configs. Every claim below was measured; each
carries its evidence so it can be re-checked.

> **Naming.** This was built as a standalone mod called SkillXPTuner, and the record below was written while
> that was true: it refers to the routes `/skillxptuner/...`, the files `SkillXPTuner.Server.csproj` and
> `SkillXPTuner.Client.csproj`, the log prefix `[SkillXPTuner]`, and classes named `SkillXpTuner*`. Those are
> now `/skillmultiplier/...`, `SkillMultiplier-server.csproj` and `src\SkillMultiplier.csproj`,
> `[SkillMultiplier]`, and `SkillMultiplier*`. Quoted log lines are kept verbatim, prefix and all, because
> they are evidence rather than documentation.

Version pin: SPT **4.1.5** (`7d7add`), read from the live server
(`curl -sk -H "responsecompressed: 0" https://127.0.0.1:6969/singleplayer/settings/version`).
Server source: `https://github.com/SP-Tushonka/server-csharp` @ tag `4.1.5`.

## 1. Goal

Stage SkillXPTuner as an update to the existing `SkillMultiplier` mod, adding a **client-side
BepInEx half** so that per-action multipliers reach the actions the server cannot touch.

## 2. What the client half can and cannot reach

| | Skills | Reachable client-side? |
| --- | --- | --- |
| **A. Hardcoded literals** | `Perception.OnlineAction` 2.5f / `UniqueLoot` 0.334f; `Strength.FistfightAction` / `ThrowAction` 0.2f; `Sniping.KillAction` 1f; `LightVests` 0.05f; `HeavyVests` 0.025f — 7 actions, 5 skills | **Yes.** Each is `Factor(literal)` → `FactorValue` → `InvokeExternal` → `OnTrigger` (`SkillManager.cs:2341, 2419, 2548, 2553, 2561`). |
| **B. Empty action array** | `RecoilControl`, `FieldMedicine`, `ProneMovement`, `FirstAid`, `WeaponModding`, `AdvancedModding`, `NightOps`, `SilentOps`, `Lockpicking` (`:2049, 2529, 2551-52, 2569-73`) | **Not in vanilla** — `Array.Empty<SkillAction>()`, so there is no XP event and no multiplier can apply. Needs a new XP source (feature work), not tuning. |

Group B is exactly the set a skills mod repurposes — see §6. Coverage is therefore
runtime-dependent, not a fixed list.

`RecoilControl` deserves a note: BSG removed its XP source (user-confirmed), which matches the
measurement — empty action array, and in the server it appears only inside the `SkillsSettings`
record (`SPT.Server.Core`, `GlobalConfiguration`), never as an XP grant.

## 3. Apply point — one patch, three consumers

`Skill.OnTrigger` (`EFT.Skill`), verified on the deployed `Assembly-CSharp.dll`:

```csharp
public override void OnTrigger(SkillManager.SkillAction skillAction, float val)
{
    SkillManager.SkillProgress.Complete(this, val);   // cross-skill XP      (raw today)
    if (!skillAction.SimpleCalculation)
    {
        val = UseEffectiveness(val);                  // _pointsEarned += …  (raw today)
        val = (float)SkillManager.BonusController.Calculate(this, val);
    }
    if (base.Level < 9) val = CalculateExpOnFirstLevels(val);
    base.OnTrigger(skillAction, val);                 // ◀ SkillMultiplier patches HERE
}
```

`UseEffectiveness` accrues `_pointsEarned` (`Skill.cs:150`), and
`ProgressValue => Round(InRaid ? PointsEarned : …)` with `PointsEarned => _pointsEarned`
(`Skill.cs:42-44`) — that is the **green "XP gained this raid" bar**. Because the existing
multiplier is a prefix on `BaseSkill.OnTrigger`, it lands *after* that accrual:

| Consumer | Today | After retarget to `Skill.OnTrigger` |
| --- | --- | --- |
| `_pointsEarned` → green raid bar | raw | multiplied |
| `SkillProgress.Complete(this, val)` → cross-skill XP | raw | multiplied |
| `SetCurrent` → real progress + white bar | multiplied | multiplied |

So the reported green-bar bug and the unscaled cross-skill XP are one fix. Note also
`SelfPlayerInfo.AddChangeSkillExperiencePacket(byte skillId, float value, float effectiveness,
float pointsEarned)` (`SelfPlayerInfo.cs:120`) — the client reports `value` and `pointsEarned`
together, so today the server is receiving internally inconsistent skill data each raid.

Deliberate side effect: for levels 0–8 the multiply now happens *before* the non-linear
`CalculateExpOnFirstLevels` curve, so those levels gain differently than today.

`ClientAuthorizedSkill.OnTrigger` overrides without chaining (it only logs "can not be progressed
locally"), so it is unaffected — its XP is server-authorised. `WeaponSkill : Skill` inherits the
patched override, so one prefix on `Skill.OnTrigger` covers every locally-progressable skill.

## 4. Action identity — proven

`SkillAction` carries no name, id or index — only `FactorValue`, `SimpleCalculation`, `StartTime`,
`_externalEvent` (`SkillManager.cs:380`). Identity therefore comes from position, and the
positional lookup is sound because of how `BaseSkill` wires itself up:

```csharp
public BaseSkill(SkillManager.SkillAction[] actions) {
    _actions = actions;
    var actions2 = Actions;
    for (int i = 0; i < actions2.Length; i++)
        actions2[i].ExternalEvent += OnTrigger;   // the array element IS the OnTrigger caller
}
```

So the `skillAction` argument is always an element of `__instance.Actions` and
`Array.IndexOf(Actions, skillAction)` yields a stable index for the lifetime of the SkillManager.

Enumeration at runtime (both are **public** on `SkillManager`, verified on the deployed binary):

- `public readonly Skill[] Skills;` (`SkillManager.cs:1435`)
- `public readonly Dictionary<Type, WeaponSkill> WeaponSkills` (`:1444`) — a *separate* store;
  weapon skills are **not** in `Skills`, so both must be walked.

Key scheme: `"<SkillId>[<actionIndex>]"`, e.g. `Endurance[0]`. `SkillId` is
`public ESkillId Id` (a public field on `BaseSkill`), which stays meaningful for mod-injected ids.

Caveat to document: a mod that reorders its own action array shifts index-based keys.

**The client's keys cannot be derived from the server's catalog — measured.** That was the original plan,
and a count check over the decompiled `SkillManager` killed it: the server catalog is a list of *tunable
globals fields*, not of actions, and the two are not one-to-one.

| Skill | catalog entries | client actions |
| --- | --- | --- |
| Strength | 6 | **5** |
| Crafting | 4 | **2** |
| 16 others | match | |
| 8 others | not statically extractable (weapon skills build from a shared table) | |

Strength's six are Min/Max pairs (`SprintActionMin`/`Max` …, feeding a single *lerped* action), and an
action's expression can read several fields — Endurance's `SprintAction` expression also reads
`GainPerFatigueStack`. Pairing the two lists positionally would apply multipliers to the wrong actions,
silently. So the client owns its own key space and the halves keep **separate** ones: `Multipliers`
(server/globals, `Settings.Skill.Field`) and `Actions` (client/action, `SkillId[index]`). Both ride the
same payload, and neither is derived from the other.

`SkillManager` exposes a second thing worth knowing: `Skills` is a *curated* array
(`Skills = [BotReload, BotSoundGoef].Concat(DisplayList)`), and vanilla constructs `RecoilControl`,
`Lockpicking`, `WeaponModding`, `AdvancedModding` and `ProneMovement` **without** putting them in it — the
very skills a skills mod gives actions to. Enumerating only that array would leave them untunable, so the
client also reflects over every skill-typed field on the manager.

## 5. Transport — settled, source-verified

**Client → server (pull / fallback).** `SPT.Common.Http.RequestHandler` in
`BepInEx\plugins\spt\spt-common.dll` exposes `GetJson` / `PostJson` / `PutJson`. It resolves
`Host` from the launch args (`-config={"BackendUrl":…}`) and `SessionId` from `-token=`
(`spt-common.cs:624-635`), accepts the self-signed cert
(`ServerCertificateCustomValidationCallback => true`, `:443-447`), auths via a `PHPSESSID` cookie
(`:455-459`), zlib-compresses request bodies and **auto-decompresses responses** (`:463-483`).
Consequence: no port, cert or compression handling in mod code, and the `responsecompressed: 0`
opt-out that the *browser* needed is unnecessary here.

**Server → client (push).** SPT has a mod-facing websocket server; `app.UseWebSockets()` is
enabled (`SPT.Server.dll:414`). `WebSocketServer` takes `IEnumerable<IWebSocketConnectionHandler>`
and routes every websocket whose path `Contains` a handler's `GetHookUrl()`
(`Servers/WebSocketServer.cs:16-37`), calling `OnConnectionAsync` / `OnMessageAsync` /
`OnCloseAsync`. Keepalive 60s, timeout 15s, 4 MB ceiling.

A mod's handler reaches that collection with no extra wiring —
`SPTushonka.Server/Helpers/ProgramHelpers.cs:95` feeds every loaded mod's assemblies into the DI
handler before `InjectAll()` (`:100`), and `DependencyInjectionHandler.InjectAll` registers each
`[Injectable]` type against every implemented interface, with `HandleSingletonRegistration`
mapping interface → implementation. Requirements:

- **`InjectionType.Singleton` explicitly** — `[Injectable]` defaults to `Transient`, and the
  handler must hold the socket list.
- A **distinctive** hook URL (`/skillxptuner/ws/`); matching is `Contains`, so a path that is a
  substring of `/notifierServer/getwebsocket/` would receive that handler's traffic.

SPT's own `SptWebSocketConnectionHandler` (`/notifierServer/getwebsocket/`) keeps a
session-keyed socket dictionary with per-socket send gates — the pattern to copy for multi-client
(Fika) broadcast. Its close-frame comment names the client library SPT expects:
*"WebsocketSharp requires this"* — `websocket-sharp.dll` is in `Managed`.

Design: push is primary (immediate table push in `OnConnectionAsync`, so first contact *is* the
initial sync; broadcast on UI save), the GET route is fallback and debugging aid. No polling.

## 6. Mod compatibility (measured: Skills Extended)

`CJ-SPT/Skills-Extended` is a **BepInEx preloader patcher using Mono.Cecil**
(`Client/SkillsExtended.Client.Prepatch/Patcher.cs`): `CreateNewEnum` injects members into
`EFT.EBuffId` **from constant 1000** carrying `JsonEnumNameAttribute`, then `PatchNewBuffs` /
`PatchHacking` / `PatchSkillManager` rewrite `EFT.SkillManager`. Its `Skills/` tree patches
`FieldMedicine`, `FirstAid`, `ProneMovement`, `SilentOps`, `Strength`, `WeaponSkills` — precisely
group B — plus new `Electronics` and `LockPicking`.

Consequences:

1. Runtime enumeration gives compatibility for free: Cecil-injected skills are ordinary `Skill`
   instances with populated `Actions` by the time the patch runs. **No hardcoded skill list
   anywhere** — that is the extensibility requirement, met structurally.
2. Mod-added skills have no `globals.json` `SkillsSettings` entry, so the server has no field to
   scale. This is why the client half is the general mechanism, and why `Settings.<Skill>.<Field>`
   cannot be the universal key.
3. `skill.Id` stays a valid discriminator for injected ids (≥1000).

Also noted for later: server-side enum injection exists too (`ModLoader` honours
`HasPrepatcher`; `Modding/EnumPatcher.cs`, `PrepatchAssemblyWriter.cs`).

## 7. Architecture

- **Server** owns the catalog, the UI and the config — single source of truth.
- **Server** pushes the table to every connected client on connect and on save. Fika is handled by
  construction: N clients, N sockets, one broadcast.
- **Client** applies multipliers as a prefix on `Skill.OnTrigger`, keyed `(skillId, actionIndex)`.
- **One owner per action.** If the server scales `Settings.Endurance.SprintAction` *and* the client
  scales that action, XP compounds. Today's split is safe only because the halves touch disjoint
  skills (server: `Crafting` + `HideoutManagement`). Any per-action client work must keep it that
  way, and the catalog should carry the owner per entry so the UI cannot configure a conflict.

## 8. Migration into SkillMultiplier

- Client patch retargets `BaseSkill.OnTrigger` → `Skill.OnTrigger` (§3).
- Per-skill config maps to `` all actions of that skill `` for back-compat with existing users.
- Drop or surface the no-op sliders: the current per-skill config generates entries for skills
  with no action (e.g. `RecoilControl`), which silently do nothing.
- Server half has no base snapshot (it multiplies the live table each load) — unify on
  SkillXPTuner's idempotent `base × multiplier` snapshot.
- `Reference` blocks lack `<Private>false</Private>` → game assemblies are copied into `bin\`.
- Delete the upstream `PostBuild` deploy target (`<Exec Command='copy /Y "$(TargetPath)" …'/>`);
  build should produce a DLL and stop.
- `S:\SPT4.1` (live install) stays untouched; `tools/deploy.ps1` already refuses it.

## 9. Open items

1. ~~Whether `BaseSkill.Actions` order matches the server catalog order.~~ **Answered: it does not, and
   cannot — the two lists describe different things (§4). The client owns its key space instead.**
2. Client-side TLS for `wss://` — trivial via `websocket-sharp`'s
   `SslConfiguration.ServerCertificateValidationCallback`; less certain via `ClientWebSocket` on
   Unity's Mono runtime.
3. Whether mod injectables are registered before `WebSocketServer` is first resolved (the
   singleton is resolved lazily, so this should hold — confirm at implementation time).

## 10. Implementation order

1. ~~Server: register the WS handler; broadcast on save; keep the GET route.~~ **Done — see §11.**
2. Client: project skeleton, `Skill.OnTrigger` patch, runtime catalog builder, fetch + apply.
3. Verify: build; then in-game raid to confirm the green bar tracks the multiplied value.

## 11. What is built

`SkillXpTunerWsHandler.cs` implements `IWebSocketConnectionHandler` with hook URL
`/skillxptuner/ws/`, `[Injectable(InjectionType.Singleton, OnLoadOrder.PostLoad + 2)]`. It keeps a
socket set guarded by a lock, serialises sends per socket with a `SemaphoreSlim` (a `WebSocket`
throws on overlapping sends), sends the table in `OnConnectionAsync` so first contact *is* the
initial sync, answers a client `refresh` message, and prunes closed sockets on send failure.
`SkillXpTunerMod` gained a `Revision` counter and a lock-guarded `Multipliers` snapshot; the router
callback broadcasts after save and reset. The project needed
`<FrameworkReference Include="Microsoft.AspNetCore.App" />` for `HttpContext` / `WebSocket`.

Verified live against the running server (SPT 4.1.5):

- `[WebSocket Request] /skillxptuner/ws/` — SPT dispatched to the handler.
- On connect, an independent browser `WebSocket` client received
  `{"Type":"table","Revision":1,"Enabled":true,"MaxMultiplier":100,"Multipliers":{}}`.
- A save of `Settings.Endurance.SprintAction = 1.5` pushed a second message to that client,
  observed as `Rev 2, 1 multiplier, value 1.5`.
- A reset pushed `revision 3` (`[SkillXPTuner] Pushed revision 3 to 1/1 client(s).`).
- The deployed `config.json` was returned to its no-op state afterwards.

**Known follow-up:** the save response still says *"Restart the game to load the new values."* That
was true when the values were only read at client startup; now that the client consumes the push it
should be reworded (the server-side `globals` half still needs a client restart to be re-read).

### Client half (built, not yet run in-game)

`SkillXPTuner.Client` — a BepInEx plugin, `netstandard2.1`, output contains **only** its own DLL (every
reference is `Private=false`, so no game assembly can shadow the real one).

| File | Role |
| --- | --- |
| `Plugin.cs` | `BaseUnityPlugin`; enables the patch, starts the table client and its heartbeat |
| `Patches/SkillOnTriggerPatch.cs` | prefix on `EFT.Skill.OnTrigger`, multiplying `ref float val` |
| `ActionCatalog.cs` | runtime enumeration (display array + skill-typed fields), key building, catalog dump |
| `TableClient.cs` | one-shot HTTP fetch, then the websocket, with reconnect backoff |

Design notes worth keeping: the action→multiplier `Dictionary` is **swapped whole** on a `volatile` field
rather than mutated, so the main thread never reads a half-built map; the patch costs one dictionary
lookup and returns immediately when nothing is configured; a malformed table is logged and ignored rather
than thrown; and a heartbeat rebuilds the map because the table can arrive before the profile (and
therefore the `SkillManager`) exists.

Server additions to match: `/skillxptuner/api/table` (GET fallback, same payload as the push),
`SkillXpTunerConfig.Actions` as the second key space, and shape validation on save — verified live:
`{"Lockpicking[0]":2.5,"Endurance[0]":1.5,"bogus-key":3}` stored the two well-formed keys and rejected
`bogus-key`.

### Verified in the running game (measured, not inferred)

- The client loads, fetches the table over HTTP, and holds a websocket to the server; the action catalog
  resolves to 87 actions and `Revision N: 4 of 4 action multiplier(s) mapped` confirms the key space.
- Emulating an XP grant through the debugger (`EFT.Skill.OnTrigger` on the game's main thread) makes the
  prefix fire and scale by the table's value: `Endurance gained 10 x5 = 50` (x3), while the same call with
  no multiplier for that action logged nothing and scaled nothing.
- The scaled value reaches the green bar's accumulator: `Skill.PointsEarned` 0 -> 7.987 on the first armed
  grant.
- `Skill.UseEffectiveness` was read from the live assembly and its curve checked against the client's own
  globals (`SkillFreshPoints=1`, `SkillPointsBeforeFatigue=1`, `SkillFatiguePerPoint=0.6`,
  `SkillFreshEffectiveness=1.3`): decay starts after **2** session points, `0.6^(points-1)`, floored at
  0.0001, and the accrual is sub-linear in its input (at most 1 point per loop iteration) - so a x5
  multiplier cannot display as x5 above a handful of session points.
- Fatigue removal verified by A/B in one process: `SkillManager.GetEffectiveness(6)` returns 1 with
  `Plugin.FatigueDisabled` set and `0.07776002` (= `0.6^5`, computed from the client's live globals)
  without it.

### Backlog

- ~~**UI: a "back to server home" button**~~ - **done.** A `Server home` link in the page header. Measured
  rather than guessed, as the note here asked: `/` answers 200 and serves the SPT landing page
  (`<title>SPT Server</title>`, `<h1>Server Information Center (SIC)</h1>`) with no redirect, so the link
  points at `/` directly.
- ~~**Persist what the client reports, so a server restart does not lose it.**~~ - **done**, as
  `clientreport.json` beside `config.json`: written when a report is accepted, loaded at startup, and served
  only while no client is connected, with `ClientReportCachedAt` saying so. Verified live: with the game
  closed and the server restarted, the page keeps all 87 client actions where it previously showed none, and
  a report describing a different key set discarded the kept names and amounts rather than merging them (87
  kept vs 1 reported left a file holding 1, not 88). The four points below are what it had to satisfy:
  1. **Never let it drive anything.** It is display data and a claim about someone else's process; the
     multipliers applied come from `config.json` and nothing else. That already holds and must keep holding -
     no behaviour may be keyed on a cached report.
  2. **Do not let a cached figure pass as current.** An observation is a per-client-process number, so a
     persisted "highest amount the game granted this session" silently becomes "highest ever granted" - the
     same label over a different quantity. Either carry the age and say so in the UI, or fall back to the
     unobserved state on load. The failure this prevents is a plausible-looking number from a previous
     client build being read as live.
  3. **Key it to what it describes.** `SkillId[index]` is positional, so a mod that adds or reorders an action
     shifts every key after it. Store the key set and, when an arriving report disagrees, throw the kept one
     away rather than extending it - a disagreement *replaces* the kept report with the new one, it does not
     leave a union of the two.
  4. **Write it atomically** - temp file plus move, as `SkillXpTunerMod.SaveConfig` already does - so a crash
     mid-write cannot leave a truncated cache that parses as a smaller and quietly wrong catalog.
  Requested 2026-09-28.
