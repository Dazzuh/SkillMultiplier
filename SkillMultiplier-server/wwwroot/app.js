"use strict";

// SkillMultiplier UI.
//
// The catalog is fetched from the server and rendered dynamically. Nothing about skills or actions is
// hardcoded here: the server derives the catalog from what the game client actually reads, and a second
// hardcoded copy in this file would immediately drift from it.

const API = {
  catalog: "/skillmultiplier/api/catalog",
  save: "/skillmultiplier/api/save",
  reset: "/skillmultiplier/api/reset",
  migrate: "/skillmultiplier/api/migrate",
};

// How far the slider travels. Deliberately much shorter than the server's hard cap: the slider covers the
// range people actually tune, where a step of 0.05 is worth something. Beyond it the number box is the way to
// go further, and the slider just renders full - a value above the travel is normal, not an error.
const SLIDER_MAX = 10;
const SLIDER_STEP = 0.05;

let state = {
  enabled: true,
  maxMultiplier: 1000,
  globalMultiplier: 1,
  groups: [],
  // Both key spaces live here, because a row does not care which side owns it: server keys
  // (`Endurance.SprintAction`) and client keys (`Endurance[0]`). save() splits them apart again.
  multipliers: new Map(), // key -> number
  // Skills whose rows are adjusted independently rather than driven by their group slider.
  // Explicit choice, so it is kept across reloads; everything else derives from the row values.
  unlinked: {},
  // How many game clients have reported their action list. Zero means this page has never been told what
  // client actions exist, so it must not send an Actions set at all - see save().
  clientReporters: 0,
  clientReportCachedAt: null,
};
let dirty = false;

// Group-slider previews not yet applied to rows (skill names). A preview marks dirty and is flushed
// by Apply, so a mid-drag save stores what the header shows instead of stale rows.
const pendingGroups = new Set();
// One flush callback per rendered group header. render() rebuilds headers every load, so it clears
// these first - a stale closure would read a detached slider.
const pendingFlushers = new Set();

// A server row (a `Settings.Skill.Field` key) is a duplicate when the server says so: it knows which
// skills the client reports actions for, and which skills the client never applies (those stay put).
function isAdvancedRow(group, action) {
  return action.Advanced === true;
}

function loadUnlinked() {
  try {
    const raw = localStorage.getItem("skillmultiplier.unlinked");
    if (raw) state.unlinked = JSON.parse(raw) || {};
  } catch {
    state.unlinked = {};
  }
}

function storeUnlinked() {
  try {
    localStorage.setItem("skillmultiplier.unlinked", JSON.stringify(state.unlinked));
  } catch {
    // Private browsing or similar: linking just does not survive a reload. Nothing else breaks.
  }
}

// Clamp a typed or dragged value the same way everywhere: rows, group sliders and the global share one
// bound, and a value at vanilla is absence (1.00 is not stored).
function clampMult(value) {
  let v = Number(value);
  if (!Number.isFinite(v) || v < 0) v = 1;
  return Math.min(state.maxMultiplier, v);
}

const $ = (sel) => document.querySelector(sel);

// Two things about talking to the SPT server. Both were measured against the running 4.1 server after the
// first version of this page silently rendered an empty list, and both fail the same nasty way: a
// successful-looking request that does nothing.
//
// 1. THE JSON IS PASCALCASE. SPT's JsonUtil builds System.Text.Json options with no naming policy, so
//    properties arrive as `Groups`, `Actions`, `Multiplier`. Reading the camelCase name yields
//    `undefined`, so the list renders empty and nothing reports an error. Keys sent TO the server must
//    match too - System.Text.Json ignores unknown properties, so a lowercase `multipliers` posted a
//    valid-looking request that saved zero multipliers.
//
// 2. SPT'S HTTP SERVER COMPRESSES AND DOES NOT LABEL IT. HttpServer.SendResponseAsync falls back to
//    SendZlibJsonAsync for any output no serializer claims, writing the body through a ZLibStream with no
//    `Content-Encoding` header - `fetch(...).json()` cannot decode those bytes. It expects request bodies
//    to be zlib too, so a plain POST is read as garbage and the route answers 200 with an empty body.
//    SPT's own opt-outs are `responsecompressed: 0` and `requestcompressed: 0`; both are set on every
//    call below, via apiFetch, so neither can be forgotten on one route.
//
// The game client sends neither header and keeps using the compressed path, which is what it expects.

/** Single choke point for the two SPT headers. */
function apiFetch(url, options = {}) {
  return fetch(url, {
    ...options,
    headers: {
      Accept: "application/json",
      responsecompressed: "0",
      requestcompressed: "0",
      ...(options.headers || {}),
    },
  });
}

function setStatus(message, kind = "ok") {
  const el = $("#status");
  // The notice rides the sticky header as an overlay, so it is visible mid-scroll and never pushes
  // content down. The full server message stays on the tooltip; the bar shows the short form. Click
  // dismisses (errors persist otherwise, with no other close affordance).
  const short = kind === "ok" ? shortStatus(message) : String(message || "");
  el.textContent = short;
  el.title = String(message || "");
  el.className = `status show ${kind}`;
  el.onclick = () => {
    el.className = "status";
  };
  if (kind === "ok") {
    setTimeout(() => {
      el.className = "status";
    }, 6000);
  }
}

// "Saved 1 multiplier(s) and 18 action multiplier(s). Global is 3.00x, live on clients
// without a restart. ..." becomes "Saved 1 multiplier(s) and 18 action multiplier(s).
// Restart needed." The first sentence is the what; the tail is reduced to the one directive
// that changes what the user does next (a pending restart dominates "already live").
function shortStatus(message) {
  const text = String(message || "").trim();
  if (text.length <= 170) return text;
  const head = text.split(". ")[0] || text;
  // Live signal first: it contains the word "restart" ("without a restart"), so testing it second
  // would mislabel a no-restart-needed save as "Restart needed."
  let tail = "";
  if (/without a restart|already live|live now|no restart/i.test(text)) tail = " Live now.";
  else if (/restart/i.test(text)) tail = " Restart needed.";
  const short = (head + tail).trim();
  return short.length <= 190 ? short : head.slice(0, 167) + "...";
}

function markDirty(value) {
  dirty = value;
  $("#dirty").hidden = !value;
}

function fmtNum(value) {
  // Base values come from the game's own globals and can be tiny (0.00015). Show enough precision to
  // distinguish them without turning the column into noise.
  if (value === 0) return "0";
  const abs = Math.abs(value);
  if (abs < 0.001) return value.toExponential(2);
  if (abs < 1) return value.toFixed(4).replace(/0+$/, "").replace(/\.$/, "");
  return value.toFixed(2).replace(/0+$/, "").replace(/\.$/, "");
}

// "3 hours ago", for a figure that is being shown from a previous session.
function agoText(iso) {
  const minutes = Math.max(0, Math.round((Date.now() - new Date(iso).getTime()) / 60000));
  if (minutes < 1) return "just now";
  if (minutes < 60) return `${minutes} minute${minutes === 1 ? "" : "s"} ago`;
  const hours = Math.round(minutes / 60);
  if (hours < 24) return `${hours} hour${hours === 1 ? "" : "s"} ago`;
  const days = Math.round(hours / 24);
  return `${days} day${days === 1 ? "" : "s"} ago`;
}

// "0.04 × 2.00 = 0.08" — the point of the whole page in one line. The middle term is the ONLY thing a
// slider changes, and at 1.00 the right-hand side equals the left, which is what "no change" looks like.
// The global compounds on top when set, so the right-hand side is what the game actually grants:
// base × row × global.
function mathHtml(base, mult, source, global, factor) {
  const tuned = Math.abs(mult - 1) > 1e-9;
  const g = global === undefined ? 1 : global;
  const gTuned = Math.abs(g - 1) > 1e-9;

  const globalHtml = gTuned
    ? ` <span class="m-op">&times;</span> <span class="m-mult" title="Global multiplier, applied on top of every row">${g.toFixed(2)}</span>`
    : "";

  // No base yet: this action is owned by the game client and its figure is *observed* rather than declared,
  // so it appears once the game has actually paid the action out. Until then show the multiplier alone -
  // the client's own coefficient is not the amount granted (it is 1 for several actions), so multiplying it
  // would be a confident fiction.
  if (base === null) {
    // No grant to show yet - but the game reports most actions' own rate, so show that, labeled as
    // what it is. A rate is not a grant, and a bare 1 is the field's default rather than information,
    // so it is not shown at all.
    const rateHtml =
      factor > 0 && Math.abs(factor - 1) > 1e-9
        ? `<span class="m-rate" title="The action's own rate, as the game reports it - not an amount it has granted">rate ${fmtNum(factor)}</span>`
        : "";
    return (
      `<span class="m-mult" title="Your multiplier">${mult.toFixed(2)}&times;</span>` +
      globalHtml +
      rateHtml +
      `<span class="m-note">no XP seen yet</span>`
    );
  }

  // An observed amount is a per-session measurement, so one that arrived before the server restarted is a
  // figure from that session - a different quantity, and not something to present as current. Declared
  // before the titles below, which read it.
  const stale = source === "observed" && state.clientReportCachedAt !== null;

  const baseTitle =
    source === "observed"
      ? stale
        ? "Highest amount the game granted this action in a previous session, before any multiplier"
        : "Highest amount the game has granted this action this session, before any multiplier"
      : "Vanilla value, from the game's globals";

  const effTitle =
    source === "observed"
      ? "What the game will grant per event once your multiplier is applied"
      : "What the game will use after a restart";

  // "observed" is worth saying on the row itself: it is a measurement from this session, not a declared
  // vanilla constant, and it moves as the game pays the action out under different conditions.
  const note = source === "observed" ? (stale ? "from last session" : "observed") : tuned ? "" : "vanilla";

  return (
    `<span class="m-base" title="${baseTitle}">${fmtNum(base)}</span>` +
    ` <span class="m-op">&times;</span> ` +
    `<span class="m-mult" title="Your multiplier">${mult.toFixed(2)}</span>` +
    globalHtml +
    ` <span class="m-op">=</span> ` +
    `<strong class="m-eff" title="${effTitle}">${fmtNum(base * mult * g)}</strong>` +
    (note ? `<span class="m-note">${note}</span>` : "")
  );
}

// Recomputes every row's arithmetic from state. A few dozen rows, so cheaper than tracking which row
// changed - and it keeps one source of truth for the numbers on screen.
function refreshMath() {
  // innerHTML invariant: only numbers and this file's own literals are interpolated below (mathHtml
  // takes base/mult/factor numbers plus constant titles). No server, locale or mod string may reach
  // innerHTML here - those all go through textContent at row build time.
  for (const row of document.querySelectorAll(".action")) {
    const cell = row.querySelector(".math");
    if (!cell) continue;

    // No base means the server sent none: a client-owned action the game has not paid out yet. See mathHtml.
    const base = row.dataset.base === undefined ? null : Number(row.dataset.base);
    const mult = multiplierFor(row.dataset.key);
    const global = row._globalApplies === false ? 1 : state.globalMultiplier;
    const factor = Number(row.dataset.factor || 0);

    cell.innerHTML = mathHtml(base, mult, row.dataset.baseSource, global, factor);
    row.classList.toggle("tuned", Math.abs(mult - 1) > 1e-9);
  }
}

function renderHead() {
  const head = document.createElement("div");
  head.className = "action-head";

  const action = document.createElement("span");
  action.className = "h-action";
  action.textContent = "Skill action";

  const mult = document.createElement("span");
  mult.className = "h-mult";
  mult.textContent = "Multiplier — 1.00× is vanilla";

  const eff = document.createElement("span");
  eff.className = "h-eff";
  eff.textContent = "What the game uses";

  head.append(action, mult, eff);
  return head;
}

function renderAction(skillName, action, advanced = false, globalApplies = true) {
  const row = document.createElement("div");
  row.className = "action";
  row.dataset.key = action.Key;

  // `BaseSource` is what says whether there is a figure to show at all. A server-side row always has one
  // (read from the game's globals). A client-side row has one only once the game has paid that action out,
  // because it is measured rather than declared - so the absence of a source is the honest "not yet".
  if (action.BaseSource) {
    row.dataset.base = String(action.Base);
    row.dataset.baseSource = action.BaseSource;
  }
  row.dataset.factor = String(action.Factor || 0);

  const label = document.createElement("div");
  label.className = "action-label";
  label.textContent = action.Label;
  if (action.Shared) {
    const note = document.createElement("small");
    note.textContent = action.Shared;
    label.append(note);
  }

  // What this action is and when it fires, taken from the client's own trigger condition.
  const desc = document.createElement("p");
  desc.className = "action-desc";
  desc.textContent = action.Description || "";

  const slider = document.createElement("input");
  slider.type = "range";
  slider.min = "0";
  slider.max = String(SLIDER_MAX);
  slider.step = String(SLIDER_STEP);
  slider.value = String(Math.min(SLIDER_MAX, multiplierFor(action.Key)));
  slider.setAttribute("aria-label", `${skillName} ${action.Label} multiplier`);

  const num = document.createElement("input");
  num.type = "number";
  num.min = "0";
  num.max = String(state.maxMultiplier);
  num.step = "0.05";
  num.value = String(multiplierFor(action.Key));
  num.setAttribute("aria-label", `${skillName} ${action.Label} multiplier, exact`);

  const math = document.createElement("div");
  math.className = "math";

  const setValue = (value, from, defer = false) => {
    // Refuse, don't coerce: an empty or non-numeric box (a cleared field, "nan" - a number input
    // reports "" for text it cannot parse) used to become 0 via Number(""), silently storing a
    // zero-XP multiplier. Both controls revert to the stored value - including the box that holds
    // the rejected text - so the display always matches what a save would store.
    const raw = typeof value === "string" ? value.trim() : value;
    const parsed = Number(raw);
    if (raw === "" || !Number.isFinite(parsed)) {
      slider.value = String(Math.min(SLIDER_MAX, multiplierFor(action.Key)));
      num.value = String(multiplierFor(action.Key));
      return;
    }
    const v = parsed < 0 ? 1 : Math.min(state.maxMultiplier, parsed);

    // Keep both controls in agreement without re-triggering each other's handlers.
    if (from !== "slider") slider.value = String(Math.min(SLIDER_MAX, v));
    if (from !== "number") num.value = String(v);

    if (Math.abs(v - 1) < 1e-9) {
      state.multipliers.delete(action.Key);
    } else {
      state.multipliers.set(action.Key, v);
    }

    markDirty(true);
    // Bulk drivers (group slider release, link, group box) pass defer and refresh once afterwards:
    // a full refresh per row is what made large linked categories lag. The per-row header sync is
    // skipped too - applyToRows syncs once at the end.
    if (!defer) {
      refreshBadges();
      refreshMath();
    }
    if (defer) return;
    if (!advanced) {
      // A hand-moved row stops following its group slider: the rows no longer agree, so the group is
      // independent now whether the user pressed Unlink or not. Advanced rows are never driven, so
      // moving one leaves the group's link state alone.
      if (from === "slider" || from === "number") {
        setUnlinked(skillName, true);
      }
      syncGroupControl(skillName);
    }
  };

  slider.addEventListener("input", () => setValue(slider.value, "slider"));
  num.addEventListener("change", () => setValue(num.value, "number"));

  row.append(label, slider, num, math, desc);
  // The group slider drives rows through this setter rather than through state, so clamping,
  // vanilla-absence and math refresh stay in exactly one place.
  row._set = (v, defer = false) => setValue(v, "group", defer);
  row._key = action.Key;
  // Whether this row's effective figure compounds the global. Server-owned skills never run the client's
  // patch point, so their rows show base x row exactly as the game will use it.
  row._globalApplies = globalApplies;
  return row;
}

function multiplierFor(key) {
  const v = state.multipliers.get(key);
  return v === undefined ? 1 : v;
}

// Rows a group slider may drive: everything shown in the group body. Advanced rows are excluded - they
// are the other half of a duplicate, and driving them too would set both sides at once. Skill ids come
// from the server, so the selector is escaped rather than interpolated raw.
// Visible rows only for bulk drivers: a filtered-out row is not being looked at, so Link and the group
// slider must not silently rewrite it (the group label says so).
function groupRows(skill) {
  const el = document.querySelector(`.skill[data-skill="${CSS.escape(skill)}"]`);
  if (!el) return [];
  return [...el.querySelectorAll(".action")];
}

function visibleGroupRows(skill) {
  return groupRows(skill).filter((r) => !r.hidden);
}

function setUnlinked(skill, value) {
  if (value) {
    state.unlinked[skill] = true;
  } else {
    delete state.unlinked[skill];
  }
  storeUnlinked();
}

// Bring one group header in line with its rows: the slider shows their common value when they agree (and
// the group counts as linked), and is disabled with a "mixed" readout when they do not or the user
// unlinked them. Called after every row change, so the header can never claim agreement that is not there.
function syncGroupControl(skill) {
  const el = document.querySelector(`.skill[data-skill="${CSS.escape(skill)}"]`);
  if (!el) return;

  const slider = el.querySelector(".group-slider");
  const readout = el.querySelector(".group-value");
  const btn = el.querySelector(".link-btn");
  if (!slider || !readout || !btn) return;

  const rows = groupRows(skill);
  // Agreement is judged on the rows the filter shows: the bulk drivers only touch those, so judging
  // hidden rows too would claim "mixed" for a set the header can actually drive as one.
  const visible = rows.filter((r) => !r.hidden);
  const values = visible.map((r) => multiplierFor(r._key));
  const agree = values.length > 0 && values.every((v) => Math.abs(v - values[0]) < 1e-9);
  const linked = agree && !state.unlinked[skill];

  // The readout shows the common value whenever the rows agree - even unlinked, where every row is
  // simply 1.00. "mixed" is reserved for actual disagreement; the button alone conveys independence.
  if (agree) {
    readout.placeholder = "";
    readout.value = String(values[0]);
  } else {
    // A number input cannot show "mixed" as a value, so the readout empties and the word goes in
    // the placeholder. Typing a number here links and bulk-sets, exactly like pressing Link.
    readout.value = "";
    readout.placeholder = values.length ? "mixed" : "—";
  }

  if (linked) {
    slider.disabled = false;
    slider.value = String(Math.min(SLIDER_MAX, values[0]));
    btn.textContent = "Unlink";
    btn.title = "Let each row in this skill move independently";
  } else {
    slider.disabled = true;
    btn.textContent = "Link";
    btn.title = "Set every visible row in this skill to one value, then drive them together";
  }
}

function renderGroupControl(skill, group) {
  const box = document.createElement("div");
  box.className = "group-controls";

  const label = document.createElement("span");
  label.className = "group-label";
  label.textContent = "All in this skill";
  label.title = "One slider for every live row in this skill. Server-side duplicates under Advanced config keep their own values. Only rows the filter shows are driven.";

  const slider = document.createElement("input");
  slider.type = "range";
  slider.className = "group-slider";
  slider.min = "0";
  slider.max = String(SLIDER_MAX);
  slider.step = String(SLIDER_STEP);

  const readout = document.createElement("input");
  readout.type = "number";
  readout.className = "group-value";
  readout.min = "0";
  readout.max = String(state.maxMultiplier);
  readout.step = "0.05";
  readout.setAttribute("aria-label", `All ${skill} rows multiplier, exact`);

  const btn = document.createElement("button");
  btn.type = "button";
  btn.className = "ghost link-btn";

  // Bulk-set through each row's own setter: one clamping path, and each row's math refreshes itself.
  // Advanced rows are deliberately not driven - see groupRows.
  // Dragging previews on the readout only; the rows (and their full-page math refresh) apply once, on
  // release - driving every row live is what made large linked categories lag. The preview marks dirty
  // and is flushed by Apply, so saving mid-drag saves what the header shows, not stale rows.
  const applyToRows = (v) => {
    for (const row of visibleGroupRows(skill)) row._set(v, true);
    setUnlinked(skill, false);
    pendingGroups.delete(skill);
    refreshBadges();
    refreshMath();
    syncGroupControl(skill);
  };

  const flushPending = () => {
    // Detached-slider guard: flushers die with render(), but never trust a closure over a node that
    // may not be in the document.
    if (!document.contains(slider)) {
      pendingGroups.delete(skill);
      return;
    }
    if (!pendingGroups.has(skill)) return;
    applyToRows(clampMult(slider.value));
  };
  pendingFlushers.add({ skill, flush: flushPending });

  slider.addEventListener("input", () => {
    const v = clampMult(slider.value);
    slider.value = String(Math.min(SLIDER_MAX, v));
    readout.placeholder = "";
    readout.value = String(v);
    pendingGroups.add(skill);
    markDirty(true);
    // The commit-time red border only sees stored rows, so a mid-drag 0x would show no warning until
    // release: mirror the verdict onto this header from the preview value plus the stored rest.
    const host = document.querySelector(`.skill[data-skill="${CSS.escape(skill)}"]`);
    if (host) {
      const previewZero =
        v === 0 || groupRows(skill).some((r) => !r.hidden && multiplierFor(r._key) === 0);
      host.classList.toggle("has-zero", previewZero);
      host.title = previewZero ? "Contains a 0x multiplier: that action grants no XP at all." : "";
    }
  });

  slider.addEventListener("change", () => {
    pendingGroups.delete(skill);
    applyToRows(clampMult(slider.value));
  });

  // Esc abandons the preview: restore the header to the stored rows instead of showing a number
  // nothing holds - including the zero verdict, which recomputes from stored rows only.
  slider.addEventListener("keydown", (e) => {
    if (e.key === "Escape" && pendingGroups.has(skill)) {
      pendingGroups.delete(skill);
      syncGroupControl(skill);
      refreshZero();
    }
  });

  // The readout is a number box like every row's: typing a value links and bulk-sets. Empty or
  // non-numeric input reverts to the header state instead of storing a zero.
  readout.addEventListener("change", () => {
    const raw = readout.value.trim();
    if (raw === "" || !Number.isFinite(Number(raw))) {
      syncGroupControl(skill);
      return;
    }
    pendingGroups.delete(skill);
    applyToRows(clampMult(raw));
  });

  btn.addEventListener("click", () => {
    if (state.unlinked[skill] || !groupAgree(skill)) {
      // Link prefers the rows' own agreement when there is one: the disabled slider keeps its last
      // position while unlinked, which may be ancient, and snapping tuned rows back to it discards
      // numbers nobody sees anymore. Only a genuinely mixed group falls back to the slider.
      const rows = visibleGroupRows(skill);
      const v =
        rows.length > 0 && rows.every((r) => Math.abs(multiplierFor(r._key) - multiplierFor(rows[0]._key)) < 1e-9)
          ? multiplierFor(rows[0]._key)
          : clampMult(slider.value);
      applyToRows(v);
    } else {
      setUnlinked(skill, true);
      syncGroupControl(skill);
    }
  });

  box.append(label, slider, readout, btn);
  return box;
}

function groupAgree(skill) {
  // Same set the header judges and the bulk drivers touch: visible rows only. Judging hidden rows too
  // made Unlink unreachable exactly when a filter hid a disagreement.
  const rows = visibleGroupRows(skill);
  if (!rows.length) return true;
  const values = rows.map((r) => multiplierFor(r._key));
  return values.every((v) => Math.abs(v - values[0]) < 1e-9);
}

function render() {
  const host = $("#skills");
  host.textContent = "";
  pendingGroups.clear();
  pendingFlushers.clear();
  // Previews die with the old headers, so the dirty flag dies with them: keeping it would offer to
  // save rows that no longer show anything unsaved. (Migration reload already refuses to load while
  // dirty; this covers any other path that rebuilds mid-preview.)
  markDirty(false);

  for (const group of state.groups) {
    const skill = document.createElement("details");
    skill.className = "skill";
    skill.dataset.skill = group.Skill;
    skill.open = state.groups.length <= 3;

    const summary = document.createElement("summary");

    const name = document.createElement("span");
    name.className = "skill-name";
    // The name the game itself shows, where its locale has one ("Field Medicine", "BEAR Raw Power"). The
    // reported id stays in the tooltip, because that is what a multiplier key refers to.
    name.textContent = group.DisplayName || group.Skill;
    name.title = group.Description ? `${group.Skill} - ${group.Description}` : group.Skill;

    const badge = document.createElement("span");
    badge.className = "badge";
    badge.textContent = `${group.Actions.length} action${group.Actions.length === 1 ? "" : "s"}`;

    summary.append(name, badge);
    skill.append(summary);

    const body = document.createElement("div");
    body.className = "actions";

    // The skill's own line: where its XP actually comes from. Several skills only pay out under a
    // condition nobody would guess from the action names (Strength only while overweight, Endurance only
    // while NOT), so the heading says it once instead of the rows repeating it.
    if (group.Summary) {
      const note = document.createElement("p");
      note.className = "skill-summary";
      note.textContent = group.Summary;
      body.append(note);
    }

    // Column headings, so "0.04 × 2.00 = 0.08" reads unambiguously as
    // (vanilla × your multiplier = what the game will use).
    body.append(renderGroupControl(group.Skill, group));
    body.append(renderHead());

    for (const action of group.Actions) {
      if (isAdvancedRow(group, action)) continue;
      body.append(renderAction(group.Skill, action, false, group.GlobalApplies !== false));
    }

    skill.append(body);
    host.append(skill);
  }

  renderAdvanced();

  for (const group of state.groups) syncGroupControl(group.Skill);

  refreshBadges();
  refreshMath();
}

// Server-side duplicates live here instead of in their skill group, with the reason stated once on the
// section rather than repeated per row. Rows are the same elements with the same setters - only their
// parent differs - so save, search and math treat them identically.
function renderAdvanced() {
  const section = $("#advanced");
  const host = $("#advancedBody");
  host.textContent = "";

  let count = 0;

  for (const group of state.groups) {
    const advanced = group.Actions.filter((a) => isAdvancedRow(group, a));
    if (!advanced.length) continue;

    const block = document.createElement("div");
    block.className = "adv-skill";
    block.dataset.skill = group.Skill;

    const head = document.createElement("h4");
    head.className = "adv-name";
    head.textContent = group.DisplayName || group.Skill;
    head.title = group.Description ? `${group.Skill} - ${group.Description}` : group.Skill;
    block.append(head);

    for (const action of advanced) {
      block.append(renderAction(group.Skill, action, true, group.GlobalApplies !== false));
      count++;
    }

    host.append(block);
  }

  section.hidden = count === 0;
}

function refreshBadges() {
  for (const skill of document.querySelectorAll(".skill")) {
    const badge = skill.querySelector(".badge");
    let changed = 0;
    let total = 0;

    for (const row of skill.querySelectorAll(".action")) {
      total++;
      const v = multiplierFor(row.dataset.key);
      if (Math.abs(v - 1) > 1e-9) changed++;
    }

    badge.className = changed ? "badge changed" : "badge";
    badge.textContent = changed
      ? `${changed} of ${total} tuned`
      : `${total} action${total === 1 ? "" : "s"}`;
  }

  // The advanced section's own count, so a tuned duplicate is visible without opening it.
  const advBadge = $("#advancedBadge");
  if (advBadge) {
    let advChanged = 0;
    let advTotal = 0;
    for (const row of document.querySelectorAll("#advancedBody .action")) {
      advTotal++;
      if (Math.abs(multiplierFor(row._key) - 1) > 1e-9) advChanged++;
    }
    advBadge.className = advChanged ? "badge changed" : "badge";
    advBadge.textContent = advChanged ? `${advChanged} of ${advTotal} tuned` : `${advTotal} rows`;
  }

  refreshZero();
}

// A 0x row is legal and means exactly what it says: that action grants no XP at all. It is easy to
// park a slider at zero without noticing, so any category holding one gets a red border plus an
// explicit tooltip - the math column already shows the zero result, but only for rows being looked at.
function refreshZero() {
  for (const skill of document.querySelectorAll(".skill")) {
    let hasZero = false;
    for (const row of skill.querySelectorAll(".action")) {
      if (multiplierFor(row.dataset.key) === 0) {
        hasZero = true;
        break;
      }
    }
    skill.classList.toggle("has-zero", hasZero);
    skill.title = hasZero ? "Contains a 0x multiplier: that action grants no XP at all." : "";
  }

  for (const block of document.querySelectorAll(".adv-skill")) {
    let hasZero = false;
    for (const row of block.querySelectorAll(".action")) {
      if (multiplierFor(row._key) === 0) {
        hasZero = true;
        break;
      }
    }
    block.classList.toggle("has-zero", hasZero);
    block.title = hasZero ? "Contains a 0x multiplier: that action grants no XP at all." : "";
  }

  // Global 0x zeroes every client-side action at once, so the global section warns the same way.
  const globalSection = document.querySelector("section.global");
  if (globalSection) {
    const zero = state.globalMultiplier === 0;
    globalSection.classList.toggle("has-zero", zero);
    globalSection.title = zero
      ? "Global multiplier is 0x: every client-side action grants no XP at all."
      : "";
  }
}

function applyFilter() {
  const q = $("#search").value.trim().toLowerCase();

  for (const skill of document.querySelectorAll(".skill")) {
    // The id as well as the shown name, so both "fieldmedicine" and "field medicine" find it.
    const shown = skill.querySelector(".skill-name");
    const name = `${skill.dataset.skill} ${shown ? shown.textContent : ""}`.toLowerCase();
    let anyVisible = false;

    for (const row of skill.querySelectorAll(".action")) {
      // Descriptions are searchable too - "overweight" is easier to remember than "SprintActionMin".
      const label = row.querySelector(".action-label").textContent;
      const desc = row.querySelector(".action-desc")?.textContent || "";
      const text = `${label} ${desc}`.toLowerCase();
      const hit = !q || name.includes(q) || text.includes(q);
      row.hidden = !hit;
      if (hit) anyVisible = true;
    }

    skill.hidden = !anyVisible;
    if (q && anyVisible) skill.open = true;
  }

  // Advanced rows filter the same way, per skill block. The section itself stays put - it is collapsed
  // by default, so a filtered-out block costs nothing and the explanation stays findable.
  for (const block of document.querySelectorAll(".adv-skill")) {
    let anyVisible = false;
    for (const row of block.querySelectorAll(".action")) {
      const label = row.querySelector(".action-label").textContent;
      const desc = row.querySelector(".action-desc")?.textContent || "";
      const skillName = `${block.dataset.skill} ${block.querySelector(".adv-name")?.textContent || ""}`.toLowerCase();
      const hit = !q || skillName.includes(q) || `${label} ${desc}`.toLowerCase().includes(q);
      row.hidden = !hit;
      if (hit) anyVisible = true;
    }
    block.hidden = !anyVisible;
  }

  // Filtering changes which rows each group header speaks for: resync every header so link state and
  // readouts reflect the newly visible set rather than the pre-filter one. Groups with a mid-drag
  // preview are skipped, not resynced: resyncing would reset the slider from still-stale rows and
  // destroy the preview while the pending bit still claims it.
  for (const group of state.groups) {
    if (!pendingGroups.has(group.Skill)) syncGroupControl(group.Skill);
  }
}

async function load() {
  try {
    const res = await apiFetch(API.catalog);
    if (!res.ok) throw new Error(`HTTP ${res.status}`);

    const data = await res.json();

    // A compressed (undecodable) body would have thrown above; an empty list here means the property
    // names did not match, which is silent otherwise. Say so rather than showing a blank page.
    if (!Array.isArray(data.Groups)) {
      throw new Error("unexpected catalog shape (Groups missing) - server JSON casing may have changed");
    }

    state.enabled = data.Enabled;
    state.maxMultiplier = data.MaxMultiplier || 1000;
    state.globalMultiplier = clampMult(data.GlobalMultiplier ?? 1);
    state.groups = data.Groups;
    state.multipliers = new Map();
    state.clientReporters = data.ClientReporters || 0;
    // Set when the server is showing a report from before its own restart: the observed figures in it are
    // from that session, and the rows say so rather than presenting them as current.
    state.clientReportCachedAt = data.ClientReportCachedAt || null;

    for (const group of state.groups) {
      for (const action of group.Actions) {
        if (Math.abs(action.Multiplier - 1) > 1e-9) {
          state.multipliers.set(action.Key, action.Multiplier);
        }
      }
    }

    $("#enabled").checked = state.enabled;

    loadUnlinked();
    wireGlobal();
    refreshGlobal();

    // The fatigue switch and the client-keyed multipliers are not in the catalog - the catalog is the
    // server's view of its own globals. Both come from the table endpoint, which is the exact payload the
    // running game client is sent, so the switch shows the live state instead of a guess.
    await loadTableSettings();

    render();
    renderUnsupported(data.Unsupported || []);
    showMigration(data);
    markDirty(false);

    const count = state.groups.reduce((n, g) => n + g.Actions.length, 0);
    setStatus(
      `Loaded ${count} tunable actions across ${state.groups.length} skills.` +
        (state.clientReportCachedAt
          ? ` The actions that live in the game client are from a previous session, ${agoText(
              state.clientReportCachedAt
            )} — start the game to refresh them.`
          : "")
    );
  } catch (err) {
    setStatus(`Could not load the catalog: ${err.message}. Is the SPT server running?`, "err");
  }
}

// The global slider and its number box: same travel, same bound, same clamping as every row. Kept in
// state rather than in the multipliers map because it is a separate compounding number, not a row value.
function setGlobal(value, from) {
  // Same refusal as the rows: an empty or non-numeric box reports "" (a number input never yields
  // "nan" as its value), and Number("") is 0 - which used to store global 0.00x live. Both controls
  // revert to the stored value, including the box holding the rejected text.
  const raw = typeof value === "string" ? value.trim() : value;
  const parsed = Number(raw);
  const slider = $("#globalSlider");
  const num = $("#globalNumber");
  // Static shell nodes; guarded anyway so a page variant missing one throws nothing inside an input
  // handler. The reject path below writes both elements, which widened this from cosmetic to load-bearing.
  if (!slider || !num) return;
  if (raw === "" || !Number.isFinite(parsed)) {
    slider.value = String(Math.min(SLIDER_MAX, state.globalMultiplier));
    num.value = String(state.globalMultiplier);
    return;
  }
  const v = parsed < 0 ? 1 : Math.min(state.maxMultiplier, parsed);
  state.globalMultiplier = v;

  if (from !== "slider") slider.value = String(Math.min(SLIDER_MAX, v));
  if (from !== "number") num.value = String(v);

  markDirty(true);
  // Every row's right-hand side compounds the global, so moving it refigures the whole page.
  refreshMath();
  refreshZero();
}

function refreshGlobal() {
  const slider = $("#globalSlider");
  const num = $("#globalNumber");
  if (!slider || !num) return;
  slider.max = String(SLIDER_MAX);
  slider.value = String(Math.min(SLIDER_MAX, state.globalMultiplier));
  num.max = String(state.maxMultiplier);
  num.value = String(state.globalMultiplier);
  refreshZero();
}

function wireGlobal() {
  const slider = $("#globalSlider");
  const num = $("#globalNumber");
  // load() runs again after a migration answer, but these nodes are static - never wire twice, or
  // every handler runs doubled from then on.
  if (slider.dataset.wired) return;
  slider.dataset.wired = "1";
  slider.addEventListener("input", () => setGlobal(slider.value, "slider"));
  num.addEventListener("change", () => setGlobal(num.value, "number"));
}

// The fatigue flag is not in the catalog: the catalog describes multipliers, and this is a switch. It comes
// from the table endpoint instead, which is the exact payload the running game client is sent.
async function loadTableSettings() {
  const box = $("#nofatigue");

  try {
    const res = await apiFetch("/skillmultiplier/api/table");
    if (!res.ok) throw new Error(`HTTP ${res.status}`);

    const table = await res.json();

    box.checked = Boolean(table.Enabled && table.DisableFatigue);
    state.disableFatigue = box.checked;
  } catch {
    box.disabled = true;
    box.closest("label").title = "Could not read the current table from the server";
    state.disableFatigue = undefined;
  }

  // Same reload concern as wireGlobal: load() runs again after migration, this node persists.
  if (!box.dataset.wired) {
    box.dataset.wired = "1";
    box.addEventListener("change", markDirty.bind(null, true));
  }
}

// The migration question. Shown only when the catalog says there is something to carry - a fresh
// install never sees it. Each scope answers separately: the server's old fields migrate here, a game's
// own old entries are migrated by its game once the answer is pushed to it.
function showMigration(data) {
  const section = $("#migrate");
  const host = $("#migrateRows");
  host.textContent = "";

  const scopes = [];
  if (data.LegacyServerConfig) {
    scopes.push({
      scope: "server",
      text: "Server settings from the previous version (hideout XP). Migrating applies them at once; the game needs a restart to load server-side values.",
    });
  }
  if (data.ClientLegacyDetected) {
    scopes.push({
      scope: "client",
      text: "A connected game's own settings from the previous version. Migrating carries each skill onto every one of its actions; it lands live, without a restart.",
    });
  }

  section.hidden = scopes.length === 0;
  if (!scopes.length) return;

  for (const { scope, text } of scopes) {
    const row = document.createElement("div");
    row.className = "migrate-row";

    const label = document.createElement("span");
    label.textContent = text;

    const yes = document.createElement("button");
    yes.type = "button";
    yes.className = "primary";
    yes.textContent = "Migrate";

    const no = document.createElement("button");
    no.type = "button";
    no.className = "ghost";
    no.textContent = "Don't migrate";

    // One answer per scope: disable both once either is pressed, so a double click cannot migrate and
    // then decline the same legacy config.
    yes.addEventListener("click", () => answerMigration(scope, true, [yes, no]));
    no.addEventListener("click", () => answerMigration(scope, false, [yes, no]));

    row.append(label, yes, no);
    host.append(row);
  }
}

async function answerMigration(scope, migrate, buttons) {
  for (const b of buttons) b.disabled = true;

  try {
    const res = await apiFetch(API.migrate, {
      method: "POST",
      headers: { "Content-Type": "application/json" },
      body: JSON.stringify({ Scope: scope, Migrate: migrate }),
    });
    if (!res.ok) throw new Error(`HTTP ${res.status}`);

    const result = await res.json();
    const message = result.Message || "Answered.";
    setStatus(message);

    // The answer needs a round trip before the page can show it: a server migration rewrites the file
    // at once, while a game migration travels by pushed table and back by the game's own save. Unsaved
    // slider edits are left alone: reloading would discard them, so the page says so instead.
    setTimeout(() => {
      if (dirty) {
        setStatus(`${message} You have unsaved changes - reload the page to see the migrated values.`);
        return;
      }
      load();
    }, scope === "client" ? 6000 : 1500);
  } catch (err) {
    setStatus(`Migration answer failed: ${err.message}`, "err");
    for (const b of buttons) b.disabled = false;
  }
}

function renderUnsupported(list) {
  const host = $("#unsupportedList");
  host.textContent = "";
  for (const item of list) {
    const li = document.createElement("li");
    const strong = document.createElement("strong");
    strong.textContent = `${item.DisplayName || item.Skill}: `;
    li.append(strong, document.createTextNode(item.Note));
    host.append(li);
  }
}

async function save() {
  // A group slider left mid-drag shows a preview the rows do not hold yet: commit every pending
  // preview first, so Apply stores what the headers show.
  for (const { flush } of pendingFlushers) flush();

  // PascalCase out, matching what the server's deserializer expects. Lowercase keys are not an error the
  // server can report - System.Text.Json ignores unknown properties - so they would save nothing silently.
  // One Map holds both key spaces: they are disjoint (`Endurance.SprintAction` vs `Endurance[0]`), so the
  // slider code does not need to know which is which. They are split apart again here, on the same
  // distinction the server's own validation uses for a client key: the presence of `[index]`.
  const serverKeys = {};
  const clientKeys = {};

  for (const [key, value] of state.multipliers) {
    (key.includes("[") ? clientKeys : serverKeys)[key] = value;
  }

  const payload = {
    Enabled: $("#enabled").checked,
    GlobalMultiplier: state.globalMultiplier,
    Multipliers: serverKeys,
  };

  // A save replaces the server's whole config, so a key space this page cannot see must be omitted rather
  // than sent as empty: omitted means "no change", an empty object means "reset".
  if (state.disableFatigue !== undefined) {
    payload.DisableFatigue = $("#nofatigue").checked;
  }
  if (state.clientReporters > 0) {
    payload.Actions = clientKeys;
  }

  try {
    const res = await apiFetch(API.save, {
      method: "POST",
      headers: { "Content-Type": "application/json" },
      body: JSON.stringify(payload),
    });
    if (!res.ok) throw new Error(`HTTP ${res.status}`);

    const result = await res.json();
    markDirty(false);
    setStatus(result.Message || "Saved.");
  } catch (err) {
    setStatus(`Save failed: ${err.message}`, "err");
  }
}

async function resetAll() {
  // Global-only tuning leaves the map empty, so the map alone cannot decide whether anything would
  // be lost: a bare global change still deserves the question.
  if (
    (state.multipliers.size > 0 || Math.abs(state.globalMultiplier - 1) > 1e-9) &&
    !confirm("Reset every multiplier to 1.00x?")
  )
    return;

  try {
    const res = await apiFetch(API.reset, {
      method: "POST",
      headers: { "Content-Type": "application/json" },
      body: "{}",
    });
    if (!res.ok) throw new Error(`HTTP ${res.status}`);

    const result = await res.json();
    state.multipliers.clear();
    state.globalMultiplier = 1;
    state.unlinked = {};
    storeUnlinked();
    refreshGlobal();
    for (const num of document.querySelectorAll('input[type="number"]')) num.value = "1";
    for (const slider of document.querySelectorAll('input[type="range"]')) slider.value = "1";
    for (const group of state.groups) syncGroupControl(group.Skill);
    markDirty(false);
    refreshBadges();
    refreshMath();
    setStatus(result.Message || "Reset.");
  } catch (err) {
    setStatus(`Reset failed: ${err.message}`, "err");
  }
}

document.addEventListener("DOMContentLoaded", () => {
  $("#apply").addEventListener("click", save);
  $("#reset").addEventListener("click", resetAll);
  $("#search").addEventListener("input", applyFilter);

  $("#enabled").addEventListener("change", markDirty.bind(null, true));

  $("#expandAll").addEventListener("click", () => {
    for (const s of document.querySelectorAll(".skill")) s.open = true;
  });
  $("#collapseAll").addEventListener("click", () => {
    for (const s of document.querySelectorAll(".skill")) s.open = false;
  });

  window.addEventListener("beforeunload", (e) => {
    if (!dirty) return;
    e.preventDefault();
    e.returnValue = "";
  });

  load();
});
