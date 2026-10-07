# SkillMultiplier

Skill XP multipliers for SPT / Escape From Tarkov, set from a page served by your own SPT server. Every
skill breaks down into the individual actions that award it, and each action gets its own multiplier.
`1.00` means "leave it alone".

Two halves ship together:

| | |
| --- | --- |
| Server module | scales the skill values the game reads out of the server's `globals`, in memory |
| Client plugin | scales the per-action values the game hardcodes, which the server can never reach |

Installing one without the other means part of the list does nothing.

## Install

Unzip into your SPT install root (the folder with `EscapeFromTarkov.exe`), then restart `SPT.Server.exe`.
The page is at:

**<https://127.0.0.1:6969/skillmultiplier/index.html>** — port from `SPT_Data\configs\http.json`.

That port speaks HTTPS with a self-signed certificate and nothing else; a plain `http://` request is
refused rather than redirected. Your browser warns once, accept it. `curl` needs `-k`.

## Using it

Each row shows the arithmetic - `vanilla × multiplier = what the game uses` - so a row tells you what it
will do before you touch it. `Reset all` clears every multiplier back to vanilla (toggles restored);
`Mod enabled` turns every multiplier off while keeping them.

The slider covers the range people actually tune. To go past it, type into the box beside it: the slider
goes to 10x, the box accepts up to 1000x and the slider simply shows full above its own travel.
A cleared or non-numeric box is refused (the control reverts); a negative becomes 1.00x. 0 is legal
and means that action grants no XP at all - its whole skill section gets a red border so it cannot
hide.

Rows come in two kinds, and they apply at different moments. A row named after the game's own config
(`Settings.<Skill>.<Field>`) is a value the server hands the game, so it takes effect when the game next
loads that - relaunch the client after changing one. A row keyed by skill and action (`Endurance[0]`) is
applied by the client plugin as each event happens, so it lands immediately. Setting both for the same
action compounds them rather than adding, because they are two separate knobs. One ordering caveat:
the client multiplies before the game's own first-levels curve, so below level 9 a row does not pay
a flat multiple - a 5x row at level 3 pays noticeably more than 5x vanilla, exactly as patching that
point implies.

## Where the XP comes from, and why this needs two halves

Most per-action XP is worked out by the game client and lands through one path, which the client plugin
scales. Three of those are hardcoded in the client and never appear in the server's config at all - the XP
for finding unique loot, the armour-wear XP for light and heavy vests, and the sniper kill action - so the
plugin is the only thing that can reach them. They appear in the same list, keyed by the skill they belong
to.

The rest the client cannot pay at all. Tarkov marks Crafting, Hideout Management and Weapon Treatment as
skills the client is not allowed to progress, and repairs are paid by the server outright: an armour kit
pays the vest skills according to the armour's type, a weapon repair pays Weapon Treatment, and a trader
repair pays Charisma. None of that passes through the client's skill code, so any client-side patch leaves
it untouched - those rows are scaled on the server, at the moment it hands the points over. Same list, same
keys. The same row also scales anything else the server pays that skill directly, such as quest and prestige
rewards - a 5x row means a 100-point quest reward lands as 500. That holds where the row is the single
value for the skill: conflicting row values, or a skill whose globals-backed rows suppress the grant
(Crafting, Hideout Management), leave the server-paid amount unchanged.

The hideout gym is paid the same way, but not for an action: it draws strength or endurance at random for each
successful repetition. It therefore gets a row under each skill it can pay - `Strength[Workout]` and
`Endurance[Workout]` - and the in-game popup for a repetition reports what you actually earned
(above level 9; below that the game's own first-levels curve applies after the popup figure, as in
vanilla - only the absolute gap grows with the multiplier).

`FieldMedicine`, `FirstAid`, `RecoilControl`, `WeaponModding`, `AdvancedModding`, `NightOps`, `SilentOps`,
`ProneMovement` and `Lockpicking` have no XP action of their own in base 4.1, so there is nothing to
scale - unless another mod gives them one, in which case they appear and are tunable like anything else.

## Skills added by other mods

The list is built from what the running game actually has, not from a fixed set of skills, so a mod that adds
a skill or gives an existing one its actions - Skills Extended, for example - is picked up on its own.

**Skill names come from the game.** What the client can report is a skill's id (`FieldMedicine`), but the
game's own locale database holds the name it puts on screen ("Field Medicine") and its description, and SPT
merges every installed mod's locale entries into that database. So a skill another mod adds is named the way
the game names it. The id stays in the heading's tooltip, since that is what a multiplier key refers to, and
the "Not tunable" panel names skills the same way.

**Action names come from the game too, wherever it holds one.** Every action the base game builds is held in
a named field on the skill manager (`SprintAction`, `ExamineAction`, `WeaponReloadAction`, `KillAction`), and
the client reads those names directly - including for actions a mod adds using the same arrangement, and for
the conditioned variants built from a field (`MovementAction` as used by Covert Movement). The id stays in the
heading's tooltip, since that is what a multiplier key refers to.

**Where a name cannot be read, the row says so rather than inventing one.** An action held only inside a mod's
own private objects, behind its own statics, is not reachable from anything the client already has - so it
shows as `Action 1`, `Action 2` and so on, with the row explaining. In practice that is about one action in
seven, all of them from other mods; every action the base game builds is named.

**The "Not tunable" panel describes your install, not vanilla.** If a mod gives a skill XP, that skill drops
out of the panel and its rows appear in the list above.

This mod interoperates by reading the state of the running game, its locale database included, and ships no
part of any other mod. Skills Extended is licensed CC BY-NC-ND, which permits neither derivative works nor
commercial use, so none of its naming, code or assets is reproduced here.

## Global, per-skill and per-row multipliers

Three levels, from broadest to narrowest. Each level multiplies the ones below it.

- **Global multiplier**, at the top of the page. One number on top of everything: at 2.00 each row pays
  double whatever it says. Applied by the game client the moment you press Apply - no restart. It does
  not reach Crafting, Hideout Management or Weapon Treatment, which are server-side skills the client
  never sees.
- **All in this skill**, on each skill section. One slider driving every live row in that skill. Press
  **Unlink** to adjust the rows independently; moving any row unlinks the group on its own. **Link**
  keeps the rows' shared value when they agree and takes the header slider when they don't, then
  drives them together again. While a filter hides rows, only the visible ones are driven.
- **Rows.** The individual actions, as before.

The group sliders drive the live rows only - never the server-side duplicates under Advanced config,
which keep their own values so a bulk change cannot set both sides of a duplicate at once.

## When a change takes effect

| | |
| --- | --- |
| Typed in the page | Pushed to a running game over its socket; applies without restarting anything |
| The `globals`-backed half | The game reads those once, at its own startup - a game restart applies them |
| `No fatigue` | Applies immediately, like a client-keyed multiplier |

A save reaches a running game even mid-raid. Either way it only affects future XP: points already
earned are untouched.

## Upgrading from 1.x

If the previous release's settings are still around, the page asks whether to carry them over - nothing
migrates on its own, and a fresh install never sees the question:

- `[Multipliers] <skill>` and `[General] Global Multiplier` from the plugin's own config file become
  per-action multipliers, one whole skill at a time: the old mod multiplied by `skill × global`, so each
  of that skill's actions ends up at `skill × global` - the same numbers it used to produce. Every
  affected skill lands with all its rows equal, so its group slider shows linked. Negative multipliers
  become 0.
- `HideoutExpMultiplier` from the server's `config.json` becomes the two HideoutManagement values the old
  mod scaled.
- `CraftingExpMultiplier` **has no equivalent and is not carried.** It scaled `craftingExpAmount` in
  the hideout config - XP for alternating crafts in a hideout module - which is a different
  number from the crafting cycle rates this mod exposes. The server log says so when it finds the old value.
- `Increase Limits` is no longer a setting: its only job was raising the cap, which is now 1000 for everyone.

Answering "Don't migrate" drops the question for good: the old server fields are removed unapplied, and
each game's own old entries are deleted too (nothing reads them any more, so keeping them would only
ask again). The carried values appear on the page afterwards and are editable there, like any other. Make sure no
second copy of this mod is installed under a different folder name - two copies both scale the same numbers,
and the result is not what either of them says.

## Notes for the curious

The game takes its skill values from two places, which is why this mod has two halves. The per-action
numbers it multiplies by normally come from the server's in-memory `GlobalTable`, so the server half can
scale them directly. Nothing on disk is rewritten: `globals.json` is never touched, the base is re-read from
it every boot, and the transformation is `base × multiplier`, so applying it twice is the same as applying
it once. The values baked into the client are reached by a client plugin at `EFT.Skill.OnTrigger`, which is
late enough that the skill's own progress accounting and its "earned this raid" bar both see the multiplied
amount.

The page's list is derived from what the game actually reads, not from every field that exists; where a
value is used by more than one skill, the row says so.

## Build

```
pwsh tools/deploy.ps1              # build both halves and install into your SPT install
pwsh build-release.ps1             # package a release zip in release\
```

`deploy.ps1` takes the SPT install as a parameter, refuses to run when a second copy of this
mod is already installed, stops the server for you (it holds its own DLL), and never overwrites an existing
`config.json`. All game references are `Private=false`, and the deploy fails if any non-mod assembly lands
in the build output.

## License

MIT
