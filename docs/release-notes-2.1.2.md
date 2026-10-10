# SkillMultiplier 2.1.2

The things 2.1.1 promised now hold up in a raid.

## XP and the gym

**Server-paid XP follows your rows again.** A single guard decided whether a skill's grant got
scaled, and it suppressed too much: skills whose XP is genuinely computed from scaled values were
protected correctly, while everything else was left at vanilla. Repair Intellect and its kin paid
vanilla against their own rows; they no longer do. Crafting and Hideout Management are still left
alone, because for them the double-scaling was real.

**Rows that disagree by a rounding error no longer read as a conflict.** A hand-edited file
holding 2.0 and 2.0000001 now counts as one value, so the grant scales instead of being skipped.

**The gym works with a global multiplier on its own.** Leaving the workout row at its default
used to stop the payout popup from applying the global at all. It also no longer remembers the
previous workout's skill, so a reward-less repetition can't pay from the wrong row.

**Workout keys are matched case-insensitively,** like every other key in the table.

## Live sync

**A change that arrives while the client is rebuilding its action map is no longer dropped.**
It waits for the next rebuild instead of being discarded by the one in progress.

**A server restart mid-session no longer leaves you on stale multipliers.** The server restarts
its revision counter at 1 on every boot; the client used to reject that first table as older than
what it already held and keep the old values until the next save.

**The settings page works while a game is connected,** and a save pushed mid-raid reaches a
running client without a restart. The page survives a server restart too.

**The server no longer waits on a client that stopped responding.** A frozen raid or a dropped
connection used to hold up saves for every other player on the server.

## Saving and migration

**Applying settings with the game closed no longer wipes your action rows.** The page omits the
key space it cannot see while no game is connected; that omission used to be read as "clear it".
Omitted settings are now preserved; a field set to nothing is still cleared.

**An invalid value is refused rather than silently overwritten.** An empty or non-numeric box
reverts to what was stored instead of becoming a 0, and a bad global keeps its previous good
value instead of resetting to 1.

**One rejected save can no longer wedge every later one.** An empty or malformed request used to
leave a lock held that only a server restart could clear, after which every save, reset and
migration waited forever.

**Overlapping saves, resets and migrations no longer interleave.** Each now runs as one operation.

**A client that disconnects stops counting as connected.** Its action list and its pending
migration answer used to outlive it, holding a stale question in front of the page forever.

**A saved key no connected client has reported is now reported back to you,** so a typo doesn't
quietly do nothing. Workout keys are exempt, since no client will ever report them.

## On the page

**Empty and non-numeric inputs revert** instead of being read as 0. A 0 is still legal, and it
now gets a red border so it cannot hide.

**Group sliders drive only the rows you're looking at.** A filter no longer lets a bulk edit
silently rewrite hidden rows.

**Linking keeps a value the visible rows already share,** rather than snapping them to wherever
the header slider was left.

**The group header has a number box,** so you can type a value for a whole skill without dragging.

**Dragging a group slider previews and commits in one refresh,** which is what made large
categories stutter. Escape abandons the preview; saving commits what the header shows.

**Status notices stay put** and no longer turn "without a restart" into "Restart needed."

## Worth knowing

Two copies of the mod still compound, and the duplicate check now finds renamed and suffixed
copies it used to miss. The 1000 cap is unchanged.

Row changes land immediately, mid-raid included; the `Settings.*` half wants a game relaunch.
The skill page still compresses what a multiplier looks like once you are past level 9, which is
the game's own curve and not this mod's arithmetic.

## Installing

Unzip into your SPT root so the plugin lands in `BepInEx/plugins/` and the module in
`SPT_Runtime/user/mods/dazzuh-skillmultiplier/`, then restart `SPT.Server.exe`. Saved
multipliers and keys are unchanged.
