"use strict";
// View only: reads window.MODEL (TestResults/dashboard/data.js) and draws it. M = the snapshot, D = one session's detail.
// Every M.x / M.x.y and D.x read here must exist in the data (checked by a test, so a renamed field can't break the page silently).

const esc = v => String(v ?? "").replace(/[&<>"']/g, c => ({ "&": "&amp;", "<": "&lt;", ">": "&gt;", '"': "&quot;", "'": "&#39;" }[c]));
const $ = id => document.getElementById(id);
const cmd = c => c ? `<span class="cmd">${esc(c)}</span>` : "";
const chip = (text, cls) => `<span class="chip ${cls || ""}">${esc(text)}</span>`;
const tag = (text, cls) => `<span class="tag ${cls || ""}">${esc(text)}</span>`;
const table = (head, rows) => `<div class="table-wrap"><table><thead><tr>${head.map(h => `<th>${esc(h)}</th>`).join("")}</tr></thead><tbody>${rows.join("")}</tbody></table></div>`;
const kb = n => n >= 1048576 ? (n / 1048576).toFixed(1) + " MB" : Math.max(1, Math.round(n / 1024)) + " KB";

const FEATURE = { DONE: ["Xong", "ok"], IN_PROGRESS: ["Đang làm", "info"], PLANNED: ["Chưa làm", ""], BLOCKED: ["Bị chặn", "bad"], DEFERRED: ["Hoãn", ""], DROPPED: ["Bỏ", ""] };
const REQ = {
  VERIFIED: ["Đã kiểm chứng", "ok"], ACCEPTED: ["Người đã duyệt", "ok"], NO_TEST: ["Chưa có test", "warn"], NOT_RUN: ["Test chưa chạy", "warn"],
  FAILING: ["Test đỏ", "bad"], UNVERIFIED: ["Code đổi, chưa chạy lại", "warn"], STALE: ["Plan đổi, test chưa theo", "warn"],
  NEEDS_REVIEW: ["Chờ người xem", "info"], DEFERRED: ["Hoãn", ""], DROPPED: ["Bỏ", ""]
};
const TRUST = { violation: ["Vi phạm", "bad"], denied: ["Bị chặn trước", "warn"], unobserved: ["Không rõ ai làm", "warn"], failed: ["Thất bại", "bad"],
  verified: ["Đã kiểm chứng", "ok"], observed: ["Quan sát được", ""], pending: ["Chờ Unity", "info"], planned: ["Dự kiến", ""], claim: ["Lời khai", "info"], info: ["", ""] };
const label = (map, k) => (map[k] || [k, ""]);

const open = new Set(); // which <details> are open, kept across live refreshes

function render(M) {
  if (!M) { $("noData").hidden = false; return; }
  document.title = (M.project || "Project") + " · Dashboard";
  $("project").textContent = M.project || "Dashboard";
  $("generated").textContent = "Cập nhật " + M.generated + (M.task ? " · task " + M.task.id + ": " + M.task.title + " (" + M.task.scope + ")" : " · không có task đang mở");
  $("health").innerHTML =
    chip(M.chain.ok ? "Nhật ký nguyên vẹn" : "Nhật ký BỊ SỬA tại #" + M.chain.brokenAt, M.chain.ok ? "ok" : "bad") +
    chip(M.counts.sessions + " phiên · " + M.counts.events + " sự kiện");

  renderAttention(M);
  renderProgress(M);
  renderExecution(M);
  renderAnomalies(M);
  renderDetails(M);
  document.querySelectorAll("details[data-key]").forEach(d => {
    d.open = open.has(d.dataset.key);
    d.addEventListener("toggle", () => { d.open ? open.add(d.dataset.key) : open.delete(d.dataset.key); });
  });
}

// ---------------------------------------------------------------- 1. what the human has to do
function renderAttention(M) {
  const a = M.attention, items = [];
  for (const e of a.planErrors) items.push(`<li>${tag("Plan lỗi", "bad")}${esc(e)}${cmd("sửa plan/features.md (xem docs/PLANNING.md)")}</li>`);
  for (const f of a.humanFixes) items.push(`<li>${tag("Sửa", "bad")}${esc(f.problem)}${cmd(f.hint)}</li>`);
  for (const p of a.approvals) items.push(`<li>${tag({ task: "Duyệt phạm vi", requirement: "Duyệt requirement", art: "Duyệt art", proposal: "Đề xuất plan" }[p.kind] || p.kind, "info")}${esc(p.what)}${cmd(p.command)}</li>`);
  $("attentionBody").innerHTML = items.length
    ? `<p class="muted">Agent không tự làm được những việc này. Lệnh chạy trong terminal của bạn.</p><ul class="items">${items.join("")}</ul>`
    : `<p class="allclear">Không có việc nào chờ bạn.</p>`;
}

// ---------------------------------------------------------------- 2. where the project is
function renderProgress(M) {
  const r = M.roadmap;
  if (!r || !r.hasPlan) { $("progressBody").innerHTML = `<p class="muted">Chưa có plan/features.md.</p>`; return; }
  const done = r.features.filter(f => f.status === "DONE").length;
  const rows = r.features.map(f => {
    const [st, cls] = label(FEATURE, f.status);
    const pct = f.total ? Math.round(100 * f.verified / f.total) : 0;
    const notes = [...f.blockedBy.map(b => "chờ " + b), ...f.debt.map(d => "nợ: " + d), ...f.artDebt.map(d => "art: " + d)];
    const reqs = f.requirements.map(q => {
      const [qs, qc] = label(REQ, q.status);
      return `<div class="req"><code>${esc(q.id)}</code><span>${tag(qs, qc)}</span><span>${esc(q.text)}${q.detail ? `<br><span class="muted">${esc(q.detail)}</span>` : ""}</span></div>`;
    }).join("");
    return `<tr class="feature" data-f="${esc(f.id)}"><td><b>${esc(f.id)}</b><br>${esc(f.title)}</td><td>${esc(f.priority)}</td><td>${tag(st, cls)}</td>` +
      `<td>${f.verified}/${f.total}<div class="bar"><i style="width:${pct}%"></i></div></td>` +
      `<td>${f.storyTotal ? f.storyVerified + "/" + f.storyTotal : "-"}</td><td>${esc(f.artLevel || "-")}${f.artRequired ? " → " + esc(f.artRequired) : ""}</td>` +
      `<td>${esc(notes.join(" · ") || "")}</td></tr><tr class="reqs" data-for="${esc(f.id)}" hidden><td colspan="7">${reqs || '<span class="muted">Chưa có requirement.</span>'}</td></tr>`;
  });
  const now = r.next.filter(n => n.lane === "now"), later = r.next.length - now.length;
  const next = now.map(n => `<li><b>${esc(n.priority)} ${esc(n.feature)}</b> ${esc(n.title)}${n.rush ? " " + tag("rush", "warn") : ""}<br><span class="muted">${esc(n.why.join(" · "))}</span>${cmd(n.command)}</li>`);
  const art = r.artLane.map(g => `<li>${tag(g.kind, "info")}${esc(g.feature)}: ${esc(g.detail)}</li>`);
  const view = pref("progressView", "graph");
  $("progressBody").innerHTML =
    `<p class="muted">${esc(r.title || "")} · ${done}/${r.features.length} feature xong · kết quả test lúc ${esc(r.resultsUtc || "chưa chạy")}. Bấm vào một feature để xem requirement.</p>` +
    `<div class="switch" role="tablist"><button data-view="graph" class="${view === "graph" ? "on" : ""}">Đồ thị</button><button data-view="table" class="${view === "table" ? "on" : ""}">Bảng</button></div>` +
    (view === "graph" ? dagHtml(r) : table(["Feature", "Ưu tiên", "Trạng thái", "Đã kiểm chứng", "Story", "Art", "Ghi chú"], rows)) +
    `<h3>Làm được ngay (song song, không đụng module của nhau)</h3>` + (next.length ? `<ul class="items">${next.join("")}</ul>` : `<p class="muted">Không có.</p>`) +
    (later ? `<p class="muted">+ ${later} việc phải đợi (phụ thuộc hoặc trùng module): <code>./tools/test-gate.sh roadmap next</code></p>` : "") +
    (art.length ? `<h3>Việc của art</h3><ul class="items">${art.join("")}</ul>` : "");
  document.querySelectorAll("#progressBody .switch button").forEach(b => b.addEventListener("click", () => { setPref("progressView", b.dataset.view); renderProgress(M); }));
  document.querySelectorAll("#dag .node").forEach(n => {
    const pick = () => { selFeature = n.dataset.f; renderProgress(M); };
    n.addEventListener("click", pick); n.addEventListener("keydown", e => { if (e.key === "Enter") pick(); });
  });
  document.querySelectorAll("tr.feature[data-f]").forEach(tr => tr.addEventListener("click", () => {
    const sub = document.querySelector(`tr.reqs[data-for="${CSS.escape(tr.dataset.f)}"]`);
    sub.hidden = !sub.hidden;
  }));
}

// ---------------------------------------------------------------- node graphs (SVG)
function pref(k, d) { try { return localStorage.getItem("dash-" + k) || d; } catch (e) { return d; } }
function setPref(k, v) { try { localStorage.setItem("dash-" + k, v); } catch (e) { } }
const C = k => `var(--${k})`;                        // colour tokens, used through style="" so light/dark both work
const NODE = { verified: "ok", observed: "muted", pending: "info", planned: "accent", claim: "accent", info: "line",
  denied: "warn", unobserved: "warn", failed: "bad", violation: "bad" };
const FEATURE_C = { DONE: "ok", IN_PROGRESS: "info", PLANNED: "muted", BLOCKED: "bad", DEFERRED: "line", DROPPED: "line" };
const cut = (t, n) => { t = String(t ?? ""); return t.length > n ? t.slice(0, n - 1) + "…" : t; };
const ARROW = id => `<defs><marker id="${id}" viewBox="0 0 10 10" refX="9" refY="5" markerWidth="7" markerHeight="7" orient="auto-start-reverse"><path d="M0 0L10 5L0 10z" style="fill:${C("muted")}"/></marker></defs>`;
let selFeature = null, selStep = null;

/** Plan DAG: one column per dependency depth, edges hard (solid) / contract (dashed) / soft (dotted); green = satisfied. */
function dagHtml(r) {
  const fs = r.features, cols = {};
  fs.forEach(f => (cols[f.depth] = cols[f.depth] || []).push(f));
  const W = 230, H = 64, GX = 70, GY = 20, P = 16, depths = Object.keys(cols).map(Number).sort((a, b) => a - b);
  const pos = new Map(); let rows = 0;
  depths.forEach((d, ci) => { cols[d].forEach((f, ri) => pos.set(f.id, { x: P + ci * (W + GX), y: P + ri * (H + GY) })); rows = Math.max(rows, cols[d].length); });
  const width = P * 2 + depths.length * (W + GX) - GX, height = P * 2 + rows * (H + GY) - GY;
  let svg = `<svg xmlns="http://www.w3.org/2000/svg" width="${width}" height="${height}" role="img" aria-label="Plan DAG">${ARROW("ah")}`;
  fs.forEach(f => f.dependencies.forEach(d => {
    const a = pos.get(d.to), b = pos.get(f.id); if (!a || !b) return;
    const dash = d.kind === "hard" ? "" : d.kind === "contract" ? "7 5" : "2 5";
    const col = d.satisfied ? "ok" : d.kind === "soft" ? "muted" : "bad";
    const x1 = a.x + W, y1 = a.y + H / 2, x2 = b.x, y2 = b.y + H / 2, mx = (x1 + x2) / 2;
    svg += `<path d="M${x1} ${y1} C ${mx} ${y1}, ${mx} ${y2}, ${x2 - 2} ${y2}" fill="none" style="stroke:${C(col)};stroke-width:2;${dash ? "stroke-dasharray:" + dash : ""}" marker-end="url(#ah)"/>`;
    if (d.contract) svg += `<text x="${mx}" y="${(y1 + y2) / 2 - 5}" text-anchor="middle" font-size="10" style="fill:${C("muted")}">${esc(d.contract)}${d.frozen ? " ✓" : " ?"}</text>`;
  }));
  fs.forEach(f => {
    const p = pos.get(f.id), c = FEATURE_C[f.status] || "muted", rush = f.tags.includes("rush"), pct = f.total ? f.verified / f.total : 0;
    const planned = f.status === "PLANNED" || f.status === "DEFERRED";
    svg += `<g class="node${selFeature === f.id ? " sel" : ""}" data-f="${esc(f.id)}" tabindex="0"><title>${esc(f.id + " · " + f.title)}</title>` +
      `<rect class="box" x="${p.x}" y="${p.y}" width="${W}" height="${H}" rx="9" style="fill:${C("panel")};stroke:${C(c)};stroke-width:${rush ? 4 : 2};${planned ? "stroke-dasharray:5 4" : ""}"/>` +
      `<text x="${p.x + 12}" y="${p.y + 20}" font-size="12.5" font-weight="700" style="fill:${C("text")}">${esc(f.id)}${rush ? " · RUSH" : ""}</text>` +
      `<text x="${p.x + 12}" y="${p.y + 36}" font-size="11.5" style="fill:${C("muted")}">${esc(cut(f.title, 30))}</text>` +
      `<rect x="${p.x + 12}" y="${p.y + 46}" width="${W - 80}" height="6" rx="3" style="fill:${C("line")}"/><rect x="${p.x + 12}" y="${p.y + 46}" width="${(W - 80) * pct}" height="6" rx="3" style="fill:${C("ok")}"/>` +
      `<text x="${p.x + W - 60}" y="${p.y + 53}" font-size="10.5" style="fill:${C("muted")}">${f.verified}/${f.total}${f.debt.length ? " · nợ " + f.debt.length : ""}</text>` +
      (f.artLevel ? `<text x="${p.x + W - 10}" y="${p.y + 20}" text-anchor="end" font-size="9.5" font-weight="700" style="fill:${C(f.artLevel === "FINAL" || f.artLevel === "APPROVED" ? "ok" : f.artLevel === "STALE" ? "warn" : "muted")}">${esc(f.artLevel)}</text>` : "") + `</g>`;
  });
  const legend = Object.entries(FEATURE).map(([k, [t]]) => `<span><span class="dot" style="background:${C(FEATURE_C[k])}"></span>${esc(t)}</span>`).join("") +
    `<span>── hard</span><span>- - contract</span><span>··· soft</span><span>xanh = phụ thuộc đã thỏa</span><span>viền dày = rush</span>`;
  const f = fs.find(x => x.id === selFeature);
  const detail = !f ? `<p class="muted">Bấm vào một node để xem requirement và phụ thuộc.</p>` :
    `<p><b>${esc(f.id)} · ${esc(f.title)}</b> ${tag(...label(FEATURE, f.status))} <span class="muted">${esc(f.priority)} · modules: ${esc(f.modules.join(", ") || "-")}</span></p>` +
    (f.dependencies.length ? `<p class="muted">${f.dependencies.map(d => esc(d.kind + " " + d.to + (d.contract ? " (" + d.contract + (d.frozen ? ", đã chốt" : ", CHƯA chốt") + ")" : "") + (d.satisfied ? " ✓" : " ✗"))).join(" · ")}</p>` : "") +
    [...f.blockedBy.map(b => "Chờ: " + b), ...f.debt.map(d => "Nợ: " + d), ...f.artDebt.map(d => "Nợ art: " + d)].map(x => `<p class="muted">${esc(x)}</p>`).join("") +
    f.requirements.map(q => { const [qs, qc] = label(REQ, q.status); return `<div class="req"><code>${esc(q.id)}</code><span>${tag(qs, qc)}</span><span>${esc(q.text)}${q.detail ? `<br><span class="muted">${esc(q.detail)}</span>` : ""}</span></div>`; }).join("");
  return `<div class="graph" id="dag">${svg}</svg></div><div class="legend">${legend}</div><div class="panel-detail">${detail}</div>`;
}

/** Execution: one column per agent (main, subagents), one node per event, coloured by trust; spawn edges between lanes. */
function execHtml(D) {
  const only = pref("trust", "");
  let steps = D.steps.filter(s => !only || s.trust === only);
  if (steps.length > 400) steps = steps.slice(steps.length - 400);
  const lanes = D.lanes.filter(l => steps.some(s => s.lane === l.id) || D.lanes.some(c => c.parent === l.id));
  const W = 250, H = 46, GAP = 12, TOP = 52, P = 16, col = new Map(lanes.map((l, i) => [l.id, i]));
  const width = Math.max(1, lanes.length) * W + P * 2, height = TOP + steps.length * (H + GAP) + 16;
  const pos = new Map(); steps.forEach((s, i) => pos.set(s, { x: P + col.get(s.lane) * W, y: TOP + i * (H + GAP) }));
  let svg = `<svg xmlns="http://www.w3.org/2000/svg" width="${width}" height="${height}" role="img" aria-label="Đồ thị thực thi">${ARROW("ae")}`;
  lanes.forEach((l, i) => {
    const x = P + i * W;
    svg += `<rect x="${x}" y="8" width="${W - 16}" height="32" rx="8" style="fill:${C("panel")};stroke:${C("line")}"/>` +
      `<text x="${x + 12}" y="29" font-size="13" font-weight="700" style="fill:${C("text")}">${esc(cut(l.label, 30))}</text>` +
      `<line x1="${x + (W - 16) / 2}" y1="40" x2="${x + (W - 16) / 2}" y2="${height - 8}" style="stroke:${C("line")};stroke-dasharray:3 5"/>`;
  });
  lanes.filter(l => l.parent && col.has(l.parent)).forEach(l => {
    const first = steps.find(s => s.lane === l.id); if (!first) return;
    const spawn = steps.filter(s => s.lane === l.parent && s.seq <= first.seq).pop(); if (!spawn) return;
    const a = pos.get(spawn), b = pos.get(first);
    svg += `<path d="M${a.x + W - 16} ${a.y + H / 2} C ${a.x + W} ${a.y + H / 2}, ${b.x - 16} ${b.y + H / 2}, ${b.x} ${b.y + H / 2}" fill="none" style="stroke:${C("muted")};stroke-dasharray:5 4" marker-end="url(#ae)"/>`;
  });
  steps.forEach(s => {
    const p = pos.get(s), c = NODE[s.trust] || "muted", dashed = s.trust === "claim" || s.trust === "planned";
    svg += `<g class="node${selStep === s.seq ? " sel" : ""}" data-seq="${s.seq}" tabindex="0"><title>${esc(s.title)}</title>` +
      `<rect class="box" x="${p.x}" y="${p.y}" width="${W - 16}" height="${H}" rx="8" style="fill:${C("panel")};stroke:${C(c)};stroke-width:2;${dashed ? "stroke-dasharray:5 4" : ""}"/>` +
      `<rect x="${p.x}" y="${p.y}" width="6" height="${H}" rx="3" style="fill:${C(c)}"/>` +
      `<text x="${p.x + 14}" y="${p.y + 19}" font-size="12.5" font-weight="600" style="fill:${C("text")}">${esc(cut(s.title, 34))}</text>` +
      `<text x="${p.x + 14}" y="${p.y + 36}" font-size="11" style="fill:${C("muted")}">#${s.seq} · ${esc(label(TRUST, s.trust)[0] || s.trust)} · ${esc(s.source)}</text></g>`;
  });
  const s = D.steps.find(x => x.seq === selStep);
  const detail = !s ? `<p class="muted">Bấm vào một node để xem sự kiện gốc.</p>` :
    `<p>${tag(...label(TRUST, s.trust))}<b>${esc(s.title)}</b></p><div class="pre">nguồn: ${esc(s.source)} · sự kiện ${esc(s.seqs.map(x => "#" + x).join(", "))} · hash ${esc(s.hash)}\n\n${esc(s.detail)}</div>`;
  const legend = ["verified", "observed", "pending", "claim", "denied", "unobserved", "violation", "failed"]
    .map(k => `<span><span class="dot" style="background:${C(NODE[k])}"></span>${esc(label(TRUST, k)[0])}</span>`).join("");
  return (steps.length ? `<div class="graph" id="exec">${svg}</svg></div>` : `<p class="muted">Không có node nào khớp bộ lọc.</p>`) +
    `<div class="legend">${legend}</div><div class="panel-detail">${detail}</div>`;
}

function renderExecution(M) {
  const box = $("executionBody");
  if (!M.sessions.length) { box.innerHTML = `<p class="muted">Chưa có phiên nào.</p>`; return; }
  let id = pref("session", M.sessions[0].id);
  if (!M.sessions.some(s => s.id === id)) id = M.sessions[0].id;
  const only = pref("trust", "");
  const head = `<p>Phiên <select id="sessionPick">${M.sessions.map(s => `<option value="${esc(s.id)}"${s.id === id ? " selected" : ""}>${esc(s.id)} · ${esc((s.started || "").replace("T", " ").slice(5, 16))} · ${esc(cut(s.task || "không task", 24))}${s.problems ? " · ⚠ " + s.problems : ""}</option>`).join("")}</select> ` +
    `Lọc <select id="trustPick"><option value="">tất cả</option>${Object.keys(NODE).filter(k => k !== "info").map(k => `<option value="${k}"${k === only ? " selected" : ""}>${esc(label(TRUST, k)[0])}</option>`).join("")}</select></p>`;
  box.innerHTML = head + `<div id="execGraph"><p class="muted">Đang tải…</p></div>`;
  $("sessionPick").addEventListener("change", e => { setPref("session", e.target.value); selStep = null; renderExecution(M); });
  $("trustPick").addEventListener("change", e => { setPref("trust", e.target.value); renderExecution(M); });
  loadSession(id, D => {
    const g = $("execGraph"); if (!g) return;
    g.innerHTML = D ? execHtml(D) : renderSession(null);
    if (D) renderExecutionBind(g, D);
  });
}

function renderExecutionBind(g, D) {
  g.querySelectorAll(".node").forEach(n => {
    const pick = () => { selStep = +n.dataset.seq; g.innerHTML = execHtml(D); renderExecutionBind(g, D); };
    n.addEventListener("click", pick); n.addEventListener("keydown", e => { if (e.key === "Enter") pick(); });
  });
}

// ---------------------------------------------------------------- 3. what is abnormal
function renderAnomalies(M) {
  const c = M.counts, an = M.anomalies;
  const chips = [
    chip(c.violations + " vi phạm", c.violations ? "bad" : "ok"),
    chip(c.denied + " lần bị chặn trước", c.denied ? "warn" : "ok"),
    chip(c.unobserved + " thay đổi không rõ ai làm", c.unobserved ? "warn" : "ok"),
    chip(c.stopBlocked + " lần Stop bị chặn", c.stopBlocked ? "warn" : "ok"),
    chip(c.gatesPassed + " gate xanh / " + c.gatesFailed + " đỏ", c.gatesFailed ? "warn" : "ok")
  ].join("");
  // One line each; the full text is in the tooltip and in the session's detail.
  const row = x => { const [t, cls] = label(TRUST, x.trust); return `<tr><td>${esc((x.ts || "").replace("T", " ").slice(5, 16))}</td><td>${tag(t, cls)}</td><td>${esc(x.title)}<span class="one" title="${esc(x.detail)}">${esc(x.detail)}</span></td><td><code>${esc(x.session || "cli")}</code></td></tr>`; };
  const recent = an.recent.slice(0, 5).map(row), older = an.recent.slice(5).map(row);
  const files = an.files.map(f => { const [t, cls] = label(TRUST, f.trust); return `<tr><td><code>${esc(f.path)}</code></td><td>${tag(t, cls)}</td><td>${esc(f.via)}</td><td>${esc(f.scope || "")}</td></tr>`; });
  $("anomaliesBody").innerHTML = `<div class="chips">${chips}</div>` +
    (files.length ? `<h3>File cần xem</h3>` + table(["File", "", "Bởi", "Phạm vi"], files) : "") +
    (recent.length ? `<h3>Gần nhất</h3>` + table(["Lúc", "", "Chuyện gì", "Phiên"], recent) : `<p class="allclear">Không có gì bất thường.</p>`) +
    (older.length ? `<details data-key="older"><summary class="muted">${older.length} mục trước đó</summary>` + table(["Lúc", "", "Chuyện gì", "Phiên"], older) + `</details>` : "");
}

// ---------------------------------------------------------------- details (collapsed)
function renderDetails(M) {
  const parts = [];
  const sessions = M.sessions.map(s => `<tr class="feature" data-s="${esc(s.id)}"><td><code>${esc(s.id)}</code></td><td>${esc(s.started)} → ${esc((s.ended || "").slice(11))}</td>` +
    `<td>${esc(s.task || "-")}</td><td>${s.files}</td><td>${s.problems ? tag(s.problems, "warn") : "0"}</td><td>${esc(s.gate || "-")}</td>` +
    `<td>${esc(s.claim || "")}${s.verdict ? `<br>${tag(s.verdict, /mâu thuẫn/.test(s.verdict) ? "bad" : /có bằng chứng$/.test(s.verdict) ? "ok" : "warn")}` : ""}</td></tr>` +
    `<tr class="reqs" data-sfor="${esc(s.id)}" hidden><td colspan="7"><span class="muted">Đang tải…</span></td></tr>`);
  parts.push(`<details data-key="sessions"><summary>Phiên gần đây <span class="muted">(${M.sessions.length}/${M.counts.sessions}; bấm một phiên để xem từng bước)</span></summary>` +
    table(["Phiên", "Thời gian", "Task", "File", "Bất thường", "Gate", "Agent nói / so với bằng chứng"], sessions) + `</details>`);

  const r = M.roadmap;
  if (r && r.slots && r.slots.length)
    parts.push(`<details data-key="art"><summary>Art slot <span class="muted">(${r.slots.length})</span></summary>` +
      table(["Slot", "Mức", "File gắn", "Art gate", "Ghi chú"], r.slots.map(s => `<tr><td><code>${esc(s.id)}</code></td><td>${esc(s.level)}</td><td><code>${esc(s.boundFile || "-")}</code></td><td>${esc(s.gate)}</td><td>${esc(s.detail)}</td></tr>`)) + `</details>`);
  if (r && r.openTasks && r.openTasks.length)
    parts.push(`<details data-key="tasks"><summary>Task đang mở ở các worktree <span class="muted">(${r.openTasks.length})</span></summary>` +
      table(["Task", "Module", "Worktree", "Bắt đầu"], r.openTasks.map(t => `<tr><td>${esc(t.id)} ${esc(t.title)}</td><td>${esc(t.modules.join(", "))}</td><td><code>${esc(t.worktree)}</code></td><td>${esc(t.startedUtc)}</td></tr>`)) + `</details>`);

  const h = M.housekeeping;
  if (h) {
    const loose = h.loose.map(f => `<tr><td><code>${esc(f.path)}</code></td><td>${esc(f.origin)}${f.session ? " · " + esc(f.session) : ""}</td><td>${esc([f.orphan ? "mồ côi" : "", ...f.problems].filter(Boolean).join(" · "))}</td><td>${f.safeToDelete ? tag("xóa an toàn", "ok") : ""}</td></tr>`);
    const disk = h.disk.map(d => `<tr><td><code>${esc(d.path)}</code></td><td>${kb(d.bytes)}</td><td>${d.regenerable ? "sinh lại được" : ""} ${esc(d.note || "")}</td></tr>`);
    parts.push(`<details data-key="files"><summary>File <span class="muted">(${h.loose.length} chưa commit · ${h.unusedCount} asset không ai dùng · ${h.violations.length} lỗi vị trí)</span></summary>` +
      (h.violations.length ? `<ul class="items">${h.violations.map(v => `<li>${esc(v)}</li>`).join("")}</ul>` : "") +
      (loose.length ? `<h3>Chưa commit</h3>` + table(["File", "Ai tạo", "Vấn đề", ""], loose) + cmd("./tools/test-gate.sh files clean --orphans") : "") +
      (h.unused.length ? `<h3>Asset không ai dùng</h3>` + table(["File", "Vì sao"], h.unused.map(u => `<tr><td><code>${esc(u.path)}</code></td><td>${esc(u.why)}</td></tr>`)) : "") +
      (disk.length ? `<h3>Dung lượng</h3>` + table(["Thư mục", "Dung lượng", ""], disk) : "") + `</details>`);
  }
  $("detailsBody").innerHTML = parts.join("");

  document.querySelectorAll("tr[data-s]").forEach(tr => tr.addEventListener("click", () => {
    const sub = document.querySelector(`tr[data-sfor="${CSS.escape(tr.dataset.s)}"]`);
    sub.hidden = !sub.hidden;
    if (!sub.hidden) loadSession(tr.dataset.s, D => { sub.firstElementChild.innerHTML = renderSession(D); });
  }));
}

// One session's detail lives in its own file, loaded on click (a <script>, so it also works when opened from disk).
function loadSession(id, done) {
  const have = () => window.SESSION_DETAIL && window.SESSION_DETAIL[id];
  if (have()) return done(have());
  const s = document.createElement("script");
  s.src = "../../TestResults/dashboard/sessions/s-" + id.replace(/[^A-Za-z0-9]/g, "").slice(0, 40) + ".js";
  s.onload = () => done(have() || null);
  s.onerror = () => done(null);
  document.head.appendChild(s);
}

function renderSession(D) {
  if (!D) return `<span class="muted">Không có chi tiết cho phiên này. Tạo: <code>./tools/test-gate.sh dash --session &lt;id&gt;</code></span>`;
  const lanes = Object.fromEntries(D.lanes.map(l => [l.id, l.label]));
  const steps = D.steps.map(s => { const [t, cls] = label(TRUST, s.trust); return `<tr><td>${esc((s.ts || "").slice(11, 19))}</td><td>${esc(lanes[s.lane] || s.lane)}</td><td>${t ? tag(t, cls) : ""}${esc(s.title)}<div class="pre">${esc(s.detail)}</div></td></tr>`; });
  const claims = D.claims.map(c => `<li><b>Agent nói:</b> ${esc(c.text)}<br>${tag(c.verdict, /mâu thuẫn/.test(c.verdict) ? "bad" : /^có/.test(c.verdict) ? "ok" : "warn")}<div class="pre">${esc(c.evidence.join("\n"))}</div></li>`);
  const gates = D.gates.map(g => `<li>${tag(g.result, g.result === "passed" ? "ok" : g.result === "pending" ? "info" : "bad")}${esc(g.plan || "")}<div class="pre">${esc(g.tests.join("\n"))}</div></li>`);
  return (claims.length ? `<h3>Lời khai so với bằng chứng</h3><ul class="items">${claims.join("")}</ul>` : "") +
    (gates.length ? `<h3>Gate</h3><ul class="items">${gates.join("")}</ul>` : "") +
    `<h3>Từng bước (${D.steps.length})</h3><div class="table-wrap"><table class="steps"><tbody>${steps.join("")}</tbody></table></div>` +
    (D.files.length ? `<h3>File</h3><p class="pre">${esc(D.files.map(f => f.path + "  (" + (label(TRUST, f.trust)[0] || f.trust) + ")").join("\n"))}</p>` : "");
}

render(window.MODEL);

// Live (./tools/test-gate.sh serve): refresh the data every 5 s. Opened from disk or as a bundle: static.
if (location.protocol.startsWith("http") && !window.BUNDLED)
  setInterval(() => fetch("../../TestResults/dashboard/data.json", { cache: "no-store" }).then(r => r.json()).then(render).catch(() => {}), 5000);
