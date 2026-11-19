"use strict";
// View only, read-only: reads window.MODEL (Dashboard/Data/data.js, written by ./Tools/test-gate.sh dash) and draws 3 tabs.
// Sources: Docs/Plan/Core.md (M.core) · Docs/Plan/features.md + test results (M.roadmap) · Docs/Plan/Notes.md (M.notes).
// Every M.x / M.x.y read here must exist in the data (checked by a test, so a renamed field can't break the page silently).

const esc = v => String(v ?? "").replace(/[&<>"']/g, c => ({ "&": "&amp;", "<": "&lt;", ">": "&gt;", '"': "&quot;", "'": "&#39;" }[c]));
const $ = id => document.getElementById(id);
const tag = (text, cls) => `<span class="tag ${cls || ""}">${esc(text)}</span>`;
function pref(k, d) { try { return localStorage.getItem("dash-" + k) || d; } catch (e) { return d; } }
function setPref(k, v) { try { localStorage.setItem("dash-" + k, v); } catch (e) { } }

const FEATURE = { DONE: ["Xong", "ok"], IN_PROGRESS: ["Đang làm", "info"], PLANNED: ["Chưa làm", ""], BLOCKED: ["Bị chặn", "bad"], DEFERRED: ["Hoãn", ""], DROPPED: ["Bỏ", ""] };
// Requirement statuses, in chart order: [label, colour token]. VERIFIED/ACCEPTED count as done.
const REQ = {
  VERIFIED: ["Đã kiểm chứng", "ok"], ACCEPTED: ["Người đã duyệt", "ok"], NEEDS_REVIEW: ["Chờ người xem", "info"],
  UNVERIFIED: ["Code đổi, chưa chạy lại", "warn"], STALE: ["Plan đổi, test chưa theo", "warn"], NOT_RUN: ["Test chưa chạy", "warn"],
  FAILING: ["Test đỏ", "bad"], TAMPERED: ["Test bị sửa sau niêm phong", "bad"], DISPUTED: ["Đang tranh chấp (trọng tài)", "info"],
  UNSEALED: ["Test chưa niêm phong", "warn"], NO_TEST: ["Chưa có test", "none"], DEFERRED: ["Hoãn", "none"], DROPPED: ["Bỏ", "none"]
};
const DONE = new Set(["VERIFIED", "ACCEPTED"]);
const label = (map, k) => map[k] || [k, ""];

let tab = pref("tab", "foundation");
let selected = pref("feature", "");
// Feature filter/sort, shared by both tabs that list features (remembered per browser).
const filt = { status: pref("fstatus", ""), prio: pref("fprio", ""), now: pref("fnow", "") === "1", sort: pref("fsort", "id") };
const STATUS_ORDER = ["IN_PROGRESS", "BLOCKED", "PLANNED", "DONE", "DEFERRED", "DROPPED"];
const pct = f => f.total ? Math.round(100 * f.verified / f.total) : 0;
const staleCount = f => f.requirements.filter(q => q.status === "STALE").length;
const SORTS = {
  id: ["Mã", (a, b) => a.id.localeCompare(b.id)],
  prio: ["Ưu tiên", (a, b) => a.priority.localeCompare(b.priority) || a.id.localeCompare(b.id)],
  status: ["Trạng thái", (a, b) => STATUS_ORDER.indexOf(a.status) - STATUS_ORDER.indexOf(b.status) || a.id.localeCompare(b.id)],
  progress: ["Tiến độ", (a, b) => pct(b) - pct(a) || a.id.localeCompare(b.id)]
};

// "Làm ngay" = the gate's own next list: not blocked, and no open task uses the same modules.
const nowIds = M => new Set(M.roadmap && M.roadmap.next ? M.roadmap.next.filter(i => i.lane === "now").map(i => i.feature) : []);

function visible(M) {
  const now = nowIds(M);
  return featuresOf(M)
    .filter(f => (!filt.status || f.status === filt.status) && (!filt.prio || f.priority === filt.prio) && (!filt.now || now.has(f.id)))
    .sort((SORTS[filt.sort] || SORTS.id)[1]);
}

function toolbar(M) {
  const all = featuresOf(M), now = nowIds(M);
  const opt = (v, text, cur) => `<option value="${esc(v)}"${v === cur ? " selected" : ""}>${esc(text)}</option>`;
  const statuses = STATUS_ORDER.filter(k => all.some(f => f.status === k));
  const prios = [...new Set(all.map(f => f.priority))].sort();
  return `<div class="toolbar">` +
    `<label>Trạng thái <select data-filter="status">${opt("", "Tất cả", filt.status)}${statuses.map(k => opt(k, label(FEATURE, k)[0] + " (" + all.filter(f => f.status === k).length + ")", filt.status)).join("")}</select></label>` +
    `<label>Ưu tiên <select data-filter="prio">${opt("", "Tất cả", filt.prio)}${prios.map(p => opt(p, p, filt.prio)).join("")}</select></label>` +
    `<label><input type="checkbox" data-filter="now"${filt.now ? " checked" : ""}> Làm ngay (${now.size})</label>` +
    `<label>Sắp xếp <select data-filter="sort">${Object.keys(SORTS).map(k => opt(k, SORTS[k][0], filt.sort)).join("")}</select></label></div>`;
}

// Labels next to a feature: requirements gone stale, ready to start now, art level vs what the feature needs.
function badges(f, now) {
  const out = [], n = staleCount(f);
  if (now.has(f.id)) out.push(tag("Làm ngay", "ok"));
  if (n) out.push(tag(n + " requirement cũ", "warn"));
  if (f.slots.length) out.push(tag("Art: " + (f.artLevel || "?") + (f.artRequired ? " / cần " + f.artRequired : ""), f.artDebt.length ? "warn" : ""));
  return out.join(" ");
}

function render(M) {
  if (!M) { $("noData").hidden = false; return; }
  document.title = (M.project || "Project") + " · Dashboard";
  $("project").textContent = M.project || "Dashboard";
  $("generated").textContent = "Dữ liệu chỉ đọc · cập nhật " + M.generated + " · muốn đổi: nhờ AI sửa Docs/Plan/";
  $("foundation").innerHTML = foundation(M);
  $("progress").innerHTML = progress(M);
  $("notes").innerHTML = notes(M);
  showTab(tab);
}

// ---------------------------------------------------------------- tab 1: core idea + feature list
const featuresOf = M => M.roadmap && M.roadmap.hasPlan ? M.roadmap.features : [];

function foundation(M) {
  const features = visible(M), now = nowIds(M);
  const doc = M.core && M.core.trim() ? `<div class="doc">${markdown(M.core)}</div>`
    : `<p class="muted">Chưa có <code>Docs/Plan/Core.md</code>. Nhờ AI soạn ý tưởng cốt lõi vào file đó.</p>`;
  const rows = features.map(f => { const [t, c] = label(FEATURE, f.status); return `<tr><td><code>${esc(f.id)}</code></td><td>${esc(f.title)} ${badges(f, now)}</td><td>${tag(t, c)}</td><td class="muted">${esc(f.priority)}</td></tr>`; });
  const list = rows.length ? `<table><thead><tr><th>Mã</th><th>Feature</th><th>Trạng thái</th><th>Ưu tiên</th></tr></thead><tbody>${rows.join("")}</tbody></table>`
    : `<p class="muted">${featuresOf(M).length ? "Không có feature nào khớp bộ lọc." : "Chưa có <code>Docs/Plan/features.md</code>."}</p>`;
  const errors = M.roadmap && M.roadmap.errors.length ? M.roadmap.errors.map(e => `<p class="err">${esc(e)}</p>`).join("") : "";
  return `<div class="card"><h2>Ý tưởng cốt lõi</h2>${doc}</div><div class="card"><h2>Danh sách feature (${features.length}/${featuresOf(M).length})</h2>${toolbar(M)}${list}${errors}</div>`;
}

// Small Markdown: headings, bullets, paragraphs, `code`, **bold**. Escaped first: the file can't inject HTML.
function markdown(text) {
  const inline = s => esc(s).replace(/`([^`]+)`/g, "<code>$1</code>").replace(/\*\*([^*]+)\*\*/g, "<b>$1</b>");
  const out = []; let list = false, para = [];
  const flush = () => { if (para.length) out.push(`<p>${inline(para.join(" "))}</p>`); para = []; if (list) { out.push("</ul>"); list = false; } };
  for (const raw of text.replace(/\r\n/g, "\n").split("\n")) {
    const line = raw.trimEnd(), h = /^(#{1,4})\s+(.*)$/.exec(line), li = /^\s*[-*]\s+(.*)$/.exec(line);
    if (h) { flush(); const n = Math.min(4, h[1].length + 1); out.push(`<h${n}>${inline(h[2])}</h${n}>`); }
    else if (li) { if (para.length) { out.push(`<p>${inline(para.join(" "))}</p>`); para = []; } if (!list) { out.push("<ul>"); list = true; } out.push(`<li>${inline(li[1])}</li>`); }
    else if (!line.trim()) flush();
    else { if (list) { out.push("</ul>"); list = false; } para.push(line.trim()); }
  }
  flush();
  return out.join("");
}

// ---------------------------------------------------------------- tab 2: feature list left, detailed progress right
function progress(M) {
  const everything = featuresOf(M), features = visible(M), now = nowIds(M);
  if (!everything.length) return `<div class="card"><p class="muted">Chưa có <code>Docs/Plan/features.md</code>.</p></div>`;
  if (selected && !everything.some(f => f.id === selected)) selected = "";
  const all = `<button data-f="" class="${selected ? "" : "sel"}"><div class="row"><b>Toàn dự án</b><span class="muted">${everything.length} feature</span></div></button>`;
  const items = features.map(f => {
    const [t, c] = label(FEATURE, f.status), b = badges(f, now);
    return `<button data-f="${esc(f.id)}" class="${selected === f.id ? "sel" : ""}"><div class="row"><span class="name"><code>${esc(f.id)}</code> ${esc(f.title)}</span>${tag(t, c)}</div>` +
      (b ? `<div class="badges">${b}</div>` : "") +
      `<div class="mini"><i style="width:${pct(f)}%"></i></div><div class="muted">${f.verified}/${f.total} requirement đã kiểm chứng · ${esc(f.priority)}</div></button>`;
  });
  const f = everything.find(x => x.id === selected);
  const empty = items.length ? "" : `<p class="muted" style="padding:8px 10px">Không có feature nào khớp bộ lọc.</p>`;
  return `<div class="card">${toolbar(M)}</div><div class="split"><div class="card flist">${all}${items.join("")}${empty}</div>` +
    `<div class="card">${f ? featureDetail(f, now) : overall(everything)}</div></div>`;
}

function chart(reqs) {
  const counts = {};
  reqs.forEach(q => { counts[q.status] = (counts[q.status] || 0) + 1; });
  const total = reqs.length, done = reqs.filter(q => DONE.has(q.status)).length;
  const keys = Object.keys(REQ).filter(k => counts[k]).concat(Object.keys(counts).filter(k => !REQ[k]));
  const colour = k => "var(--" + (label(REQ, k)[1] || "none") + ")";
  const bar = keys.map(k => `<i title="${esc(label(REQ, k)[0])}: ${counts[k]}" style="width:${100 * counts[k] / total}%;background:${colour(k)}"></i>`).join("");
  const legend = keys.map(k => `<span><span class="dot" style="background:${colour(k)}"></span>${esc(label(REQ, k)[0])}: ${counts[k]}</span>`).join("");
  return `<div><span class="big">${total ? Math.round(100 * done / total) : 0}%</span> <span class="muted">${done}/${total} requirement xong</span></div>` +
    (total ? `<div class="stack">${bar}</div><div class="legend">${legend}</div>` : `<p class="muted">Chưa có requirement.</p>`);
}

function featureDetail(f, now) {
  const [t, c] = label(FEATURE, f.status);
  const rows = f.requirements.map(q => { const [qs, qc] = label(REQ, q.status); return `<tr><td><code>${esc(q.id)}</code></td><td>${tag(qs, qc === "none" ? "" : qc)}</td><td>${esc(q.text)}${q.detail ? `<div class="muted">${esc(q.detail)}</div>` : ""}</td></tr>`; });
  const blocked = f.blockedBy && f.blockedBy.length ? `<p class="err">Bị chặn bởi: ${esc(f.blockedBy.join(", "))}</p>` : "";
  const art = f.slots.length ? `<p class="muted">Art slot: ${esc(f.slots.join(", "))} · mức hiện tại <b>${esc(f.artLevel || "?")}</b>` +
    (f.artRequired ? ` · feature cần <b>${esc(f.artRequired)}</b>` : " · feature không đòi mức art") + `</p>` +
    f.artDebt.map(d => `<p class="err">Nợ art: ${esc(d)}</p>`).join("") : "";
  return `<h2><code>${esc(f.id)}</code> ${esc(f.title)} ${tag(t, c)}</h2><div class="badges">${badges(f, now)}</div>${blocked}${art}${chart(f.requirements)}` +
    (rows.length ? `<table class="reqs" style="margin-top:14px"><thead><tr><th>Requirement</th><th>Trạng thái</th><th>Nội dung</th></tr></thead><tbody>${rows.join("")}</tbody></table>` : "");
}

function overall(features) {
  const byStatus = Object.keys(FEATURE).map(k => [k, features.filter(f => f.status === k).length]).filter(([, n]) => n);
  const fs = byStatus.map(([k, n]) => { const [t, c] = FEATURE[k]; return `<span>${tag(t, c)} ${n}</span>`; }).join("");
  return `<h2>Toàn dự án</h2>${chart(features.flatMap(f => f.requirements))}<h3 style="margin:18px 0 6px">Feature theo trạng thái</h3><div class="legend">${fs}</div>` +
    `<p class="muted" style="margin-top:14px">Chọn một feature bên trái để xem chi tiết từng requirement.</p>`;
}

// ---------------------------------------------------------------- tab 3: the owner's notes (newest first)
function notes(M) {
  const list = M.notes ? M.notes.notes : [];
  const errors = M.notes ? M.notes.errors.map(e => `<p class="err">${esc(e)}</p>`).join("") : "";
  const items = list.map(x => `<article><h3>${esc(x.title)}</h3><p class="muted"><code>${esc(x.id)}</code> · ${esc(x.date)}</p><div class="body">${esc(x.body)}</div></article>`);
  return `<div class="card notes">${items.length ? items.join("") : `<p class="muted">Chưa có ghi chú. Nhờ AI thêm vào <code>Docs/Plan/Notes.md</code> (thêm, sửa, xóa đều qua AI).</p>`}${errors}</div>`;
}

// ---------------------------------------------------------------- tabs + selection
function showTab(name) {
  tab = ["foundation", "progress", "notes"].includes(name) ? name : "foundation";
  document.querySelectorAll(".tabs button").forEach(b => b.setAttribute("aria-selected", String(b.dataset.tab === tab)));
  ["foundation", "progress", "notes"].forEach(id => { $(id).hidden = id !== tab; });
}
document.addEventListener("change", e => {
  const k = e.target.dataset && e.target.dataset.filter; if (!k) return;
  filt[k] = e.target.type === "checkbox" ? e.target.checked : e.target.value;
  setPref("f" + k, e.target.type === "checkbox" ? (e.target.checked ? "1" : "") : e.target.value);
  render(window.MODEL);
});
document.querySelector(".tabs").addEventListener("click", e => { const b = e.target.closest("button[data-tab]"); if (b) { setPref("tab", b.dataset.tab); showTab(b.dataset.tab); } });
$("progress").addEventListener("click", e => {
  const b = e.target.closest("button[data-f]"); if (!b) return;
  selected = b.dataset.f; setPref("feature", selected); render(window.MODEL);
});

render(window.MODEL);

// Live (./Tools/test-gate.sh serve): refresh the data every 5 s. Opened from disk or as a bundle: static.
if (location.protocol.startsWith("http") && !window.BUNDLED)
  setInterval(() => fetch("Data/data.json", { cache: "no-store" }).then(r => r.json()).then(m => { window.MODEL = m; if (document.activeElement.tagName !== "SELECT") render(m); }).catch(() => {}), 5000);
