# SkillMultiplier — agent pipeline

Two halves, one repo: `src/` (BepInEx client, netstandard2.1) + `SkillMultiplier-server/`
(SPT module, net10.0). A release is one zip (install root). Port or ship both halves or the
feature silently does nothing.

## Build / deploy / run

```powershell
dotnet build src/SkillMultiplier.csproj -c Release
dotnet build SkillMultiplier-server/SkillMultiplier-server.csproj -c Release
pwsh build-release.ps1 -SPTDir 'S:\SPT-Dev\4.1'   # builds, zips, hash-verifies (incl. wwwroot/app.js)
pwsh tools/deploy.ps1 -SPTDir 'S:\SPT-Dev\4.1'    # builds + deploys both halves, hash-verified
```

`-SPTDir` is mandatory and hash-verified. Never target `S:\SPT4.1` (live install).
`S:\SPT-Dev\4.1` is the dev install. Server needs a restart to load the server half;
client-side values push live over the socket.

## Definition of done (every change, no exceptions)

1. Server Debug + Release green, 0 warnings. Client Release green, 0 warnings.
2. `node --check` on any touched `wwwroot/*.js`.
3. `build-release.ps1` end to end (it rebuilds + hash-verifies DLLs and `app.js`).
4. Adversarial pre-review (below) with every finding verified-then-fixed or disputed
   with evidence. Push only when green.
5. Behavior changes: live evidence, not build evidence. Server endpoint probes
   (catalog/table/save with `responsecompressed: 0` over HTTPS + `-SkipCertificateCheck`),
   client `LogOutput.log` lines, and profile diffs where XP accounting is involved.
   A build is not a load.

## Review gate (the no-round-trip machine)

- Fan out fresh reviewer subagents per area (server / client / UI+scripts), NOT the
  implementer continuing. Give each: exact file scope, the invariant list that must
  hold (single shared gate, SaveGate spans clean-to-broadcast, latch clears only at
  zero legacy sessions, epsilon 1e-9, revision is Interlocked), and the severity
  rubric (ship-blocker / should-fix / nit) with file:line + concrete fix required.
- Verify every finding against source before fixing. Dispute with evidence when the
  reviewer is wrong (it happens; record the dispute, don't silently skip).
- Keep CodeRabbit as the last gate, not the first. If he finds real issues, the
  pre-review was too thin — fix the gate, not just the findings.

## Standing rules

- No pushes, no deploys to live, no merges without explicit instruction.
- No review docs, plans, or machine-specific paths inside this repo. Plans and baselines
  live as workspace-root files next to `refs/` (e.g. `SkillMultiplier-M5-split-plan.md`),
  never in this repo. Name the file, not the drive: the workspace root is environment,
  not content.
- Omitted save fields preserve; empty objects reset. Explicit-invalid global is rejected and
  preserves; an omitted global preserves silently. A save replaces the whole config —
  hand-rolled partial POSTs only preserve what they omit, never what they skip.
- `0` is a legal multiplier and means XP off; the page warns with red borders.
- Small commits: never mix a refactor with behavior changes in one commit.
