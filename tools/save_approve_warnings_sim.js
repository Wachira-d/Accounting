#!/usr/bin/env node
// ═══ ล็อกพฤติกรรม: "บันทึกและอนุมัติ" / เงินสดทันที / ปุ่มอนุมัติ ต้องให้คนเห็นคำเตือนก่อนรับทราบ ═══
//
// รัน: node tools/save_approve_warnings_sim.js
// (เทียบโค้ดรุ่นก่อนแก้: DOC=/path/to/old/documents.html node tools/save_approve_warnings_sim.js — รุ่นก่อนไม่มี
//  _postSaveApproval/_approveConfirmingWarnings ⇒ ล้มตั้งแต่ขั้นดึงเมธอด ซึ่งคือสิ่งที่ต้องการ)
//
// ที่มา (รอบ 199 ทีม W · ค้างจากทีม H): ปุ่ม "บันทึกและอนุมัติ" และ chain "ลูกค้าจ่ายเงินแล้ว" ใน documents.html เรียก
// approve พร้อม acknowledgeWarnings:true ตั้งแต่ครั้งแรก ⇒ [Σ-GAP] ยอดสแกนไม่ตรงกระดาษ / VAT ไม่ได้พิมพ์บนกระดาษ /
// คำเตือนอื่น ถูกบันทึกเป็น "ผู้ใช้รับทราบ" (APPROVE-ACK-WARNINGS · "ยืนยันโดย …") ทั้งที่ไม่มีใครเห็นสักข้อ
// (คำตัดสินเจ้าของ #12 · รอบ 198 ข้อ 6 — คนต้องรับทราบเอง)
//
// ใช้ **โค้ดจริง** จาก documents.html (ดึงเมธอด _postSaveApproval · _approvalDeclinedMessage · _approveConfirmingWarnings ·
// _showApprovalWarnings · approve มาประกอบเป็น object) ไม่ใช่สำเนาในเทสต์ · ล็อกสองทิศ (F2 ข้อ 8):
//   (a) มีคำเตือน ⇒ ครั้งแรกส่ง false · แสดงหน้าต่าง · ส่ง true เฉพาะหลังกด "ยอมรับและอนุมัติต่อ"
//   (b) ไม่มีคำเตือน ⇒ อนุมัติเลยในครั้งแรก (ไม่มีหน้าต่าง ไม่มีคลิกเพิ่ม — ทิศที่ห้ามแย่ลง)
//   (c) กด "กลับไปแก้ไข" / × / Escape ⇒ ใบคงเป็นร่าง · ไม่หักมัดจำ · ไม่บันทึกชำระ · ไม่ถามซ้ำ · บอกผู้ใช้ว่าเป็นร่าง
// และ negative test ในตัว: ใส่บั๊กกลับทีละแบบ (ส่ง true ตั้งแต่แรก · ข้ามหน้าต่าง · ยกเลิกแล้วนับเป็นอนุมัติ · ไม่ต่อสายยกเลิก ·
// ถามซ้ำหลังยกเลิก · Escape ไม่ยกเลิก) แล้วชุดเดียวกันต้องล้ม
'use strict';
const fs = require('fs');
const path = require('path');

const ROOT = path.join(__dirname, '..', 'Accounting', 'wwwroot');
const DOC_PATH = process.env.DOC || path.join(ROOT, 'pages', 'documents.html');
const REAL_SRC = fs.readFileSync(DOC_PATH, 'utf8');
const LAYOUT_SRC = fs.readFileSync(path.join(ROOT, 'js', 'layout.js'), 'utf8');

const METHODS = ['_postSaveApproval', '_approvalDeclinedMessage', '_approveConfirmingWarnings', '_showApprovalWarnings', 'approve'];

// ดึงเมธอดจาก object literal ของหน้า (เมธอดระดับบนเยื้อง 6 ช่อง · ปิดด้วย "      }," บรรทัดเดียว)
function extract(src, name) {
  const re = new RegExp('\\n      (async )?' + name + '\\(');
  const m = re.exec(src);
  if (!m) return null;
  const start = m.index + 1;
  const end = src.indexOf('\n      },', start);
  if (end < 0) return null;
  return src.slice(start, end + '\n      }'.length);
}

// ── DOM ปลอมขั้นต่ำ: พอให้ _showApprovalWarnings ตัวจริงสร้าง/แสดง/ปิดหน้าต่างได้ ──
function makeDom() {
  const reg = {};
  const mk = (id) => {
    const classes = new Set();
    return {
      id: id || '', className: '', innerHTML: '', onclick: null, _onDismiss: null,
      classList: { add: c => classes.add(c), remove: c => classes.delete(c), contains: c => classes.has(c) },
    };
  };
  const doc = {
    getElementById(id) {
      if (reg[id]) return reg[id];
      // ปุ่ม/ลิสต์ที่อยู่ใน innerHTML ของหน้าต่าง — เกิดเมื่อหน้าต่างถูกแปะลง body แล้วเท่านั้น
      if (reg.approvalWarningsModal && ['awList', 'awCancel', 'awConfirm', 'awClose'].includes(id)) {
        if (id === 'awClose' && !/id="awClose"/.test(reg.approvalWarningsModal.innerHTML)) return null;
        return (reg[id] = mk(id));
      }
      if (id === 'fDate') return { value: '2026-09-28' };
      return null;
    },
    createElement: () => mk(''),
    body: { appendChild(el) { if (el.id) reg[el.id] = el; } },
  };
  return { doc, reg };
}

// ── ประกอบ Page จากโค้ดจริง + stub เฉพาะเมธอดที่ไม่ได้อยู่ในขอบเขต ──
function makePage(src, env) {
  const parts = [];
  for (const n of METHODS) {
    const t = extract(src, n);
    if (!t) throw new Error('หาเมธอด ' + n + ' ใน documents.html ไม่เจอ — ถูกลบ/เปลี่ยนชื่อ?');
    parts.push(t);
  }
  // eslint-disable-next-line no-new-func
  const factory = new Function('Layout', 'API', 'document', 'confirm', 'return ({\n' + parts.join(',\n') + '\n});');
  const page = factory(env.Layout, env.API, env.dom.doc, env.confirm);
  page._deriveMethodFromChannel = () => ({ method: 'Cash' });
  page._applyPendingDeposits = async () => { env.log.push('applyPendingDeposits'); return null; };
  page._maybePromptFixedAssetReview = async () => {};
  page.load = async () => { env.log.push('load'); };
  page._pendingDepositApplies = env.pendingDeposits ? [{ kind: 'doc', depositDocumentId: 'dep-1', amount: 500 }] : [];
  return page;
}

// เซิร์ฟเวอร์ปลอม: ใบมีคำเตือน ⇒ ไม่รับทราบ = 422 + รายการ · รับทราบ = อนุมัติ (ตรงกับ ApprovalAckSource.User)
const GAP = 'ยอดจากสแกนไม่ตรงกระดาษ [Σ-GAP]: ผลรวมรายการ 24,110.00 ≠ ยอดรวมทั้งสิ้น 23,812.25';
function makeEnv(opts) {
  const log = [];
  const toasts = [];
  const dom = makeDom();
  const state = { status: 'Draft' };
  const api = {
    async approveDocument(id, body) {
      const ack = !!(body && body.acknowledgeWarnings);
      log.push('approve:' + (ack ? 'ack' : 'noack'));
      if (opts.forbidden) { const e = new Error('ไม่มีสิทธิ์อนุมัติ'); e.status = 403; throw e; }
      if (opts.warnings && opts.warnings.length && !ack) {
        const e = new Error('เอกสารมีจุดที่ต้องตรวจก่อนยืนยันการอนุมัติ');
        e.status = 422; e.body = { data: { warnings: opts.warnings, aiHints: null } };
        throw e;
      }
      state.status = 'Approved';
      state.ackedWarnings = ack ? (opts.warnings || []) : [];
      return { data: { id, status: 'Approved' } };
    },
    async getDocument() { log.push('getDocument'); return { data: { balanceDue: 1070, totalAmount: 1070 } }; },
    async createPayment() { log.push('createPayment'); return { data: {} }; },
    async submitForApproval() { log.push('submitForApproval'); },
  };
  const Layout = {
    toast: (m, t) => toasts.push({ m: String(m), t }),
    money: n => String(n), esc: s => String(s), getCompanyId: () => 'c-1',
    closeModal: id => log.push('closeModal:' + id), api: () => api,
  };
  const API = { post: async () => ({}) };
  const env = { log, toasts, dom, state, api, Layout, API, confirm: () => false, pendingDeposits: !!opts.pendingDeposits };
  return env;
}

const tick = () => new Promise(r => setImmediate(r));
async function settle(p, ms = 80) {
  let done = false, val, err;
  p.then(v => { done = true; val = v; }, e => { done = true; err = e; });
  const t0 = Date.now();
  while (!done && Date.now() - t0 < ms) await tick();
  return { done, val, err };
}
const modal = env => env.dom.reg.approvalWarningsModal;
const modalOpen = env => !!(modal(env) && modal(env).classList.contains('active'));
async function click(env, id) {
  const b = env.dom.reg[id];
  if (!b || typeof b.onclick !== 'function') return false;
  await b.onclick();
  return true;
}

// ── ชุดเหตุการณ์ (คืนรายการที่ล้ม — ว่าง = ผ่าน) ──
async function suite(src, layoutSrc) {
  const fails = [];
  const check = (name, cond, got) => { if (!cond) fails.push(name + (got === undefined ? '' : ' — ได้ ' + JSON.stringify(got))); };
  const ctx = { savedDocId: 'd-1', savedStatus: 'Draft', approveAfter: true, paidNow: false, chainAllowed: true, paymentDate: '2026-09-28' };

  // (a) บันทึกและอนุมัติ + มีคำเตือน → ยืนยัน
  {
    const env = makeEnv({ warnings: [GAP] });
    const page = makePage(src, env);
    const p = page._postSaveApproval(env.api, Object.assign({}, ctx));
    await settle(p, 30);
    check('(a) ก่อนยืนยัน: ส่งอนุมัติแบบไม่รับทราบครั้งเดียว ไม่มี ack', JSON.stringify(env.log) === JSON.stringify(['approve:noack']), env.log);
    check('(a) ก่อนยืนยัน: หน้าต่างคำเตือนเปิดอยู่', modalOpen(env));
    check('(a) หน้าต่างแสดงข้อความคำเตือนจริง', modal(env) && env.dom.reg.awList && env.dom.reg.awList.innerHTML.includes('[Σ-GAP]'));
    check('(a) ก่อนยืนยัน: ใบยังเป็นร่าง', env.state.status === 'Draft', env.state.status);
    await click(env, 'awConfirm');
    const r = await settle(p);
    check('(a) หลังยืนยัน: ส่ง ack ครั้งที่สอง', JSON.stringify(env.log.slice(0, 2)) === JSON.stringify(['approve:noack', 'approve:ack']), env.log);
    check('(a) หลังยืนยัน: อนุมัติแล้ว', r.done && r.val && r.val.savedStatus === 'Approved' && env.state.status === 'Approved', r.val);
    check('(a) หลังยืนยัน: หักมัดจำต่อ', env.log.includes('applyPendingDeposits'), env.log);
  }
  // (b) บันทึกและอนุมัติ + ไม่มีคำเตือน → อนุมัติเลย
  {
    const env = makeEnv({ warnings: [] });
    const page = makePage(src, env);
    const r = await settle(page._postSaveApproval(env.api, Object.assign({}, ctx)));
    check('(b) ไม่มีคำเตือน: อนุมัติครั้งเดียวแบบไม่รับทราบ', JSON.stringify(env.log.filter(x => x.startsWith('approve'))) === JSON.stringify(['approve:noack']), env.log);
    check('(b) ไม่มีคำเตือน: ไม่เปิดหน้าต่าง', !modal(env));
    check('(b) ไม่มีคำเตือน: อนุมัติแล้ว + toast สำเร็จ', r.done && r.val.savedStatus === 'Approved' && env.toasts.some(t => t.t === 'success'), env.toasts);
  }
  // (c) บันทึกและอนุมัติ + มีคำเตือน → "กลับไปแก้ไข"
  {
    const env = makeEnv({ warnings: [GAP], pendingDeposits: true });
    const page = makePage(src, env);
    const p = page._postSaveApproval(env.api, Object.assign({}, ctx));
    await settle(p, 30);
    await click(env, 'awCancel');
    const r = await settle(p);
    check('(c) ยกเลิก: ขั้นหลังบันทึกจบ (ไม่ค้างรอ)', r.done, r);
    check('(c) ยกเลิก: ไม่เคยส่ง ack', !env.log.includes('approve:ack'), env.log);
    check('(c) ยกเลิก: ใบคงเป็นร่าง', env.state.status === 'Draft' && r.val && r.val.approvalDeclined === true && r.val.savedStatus === 'Draft', r.val);
    check('(c) ยกเลิก: ไม่หักมัดจำ', !env.log.includes('applyPendingDeposits'), env.log);
    check('(c) ยกเลิก: บอกผู้ใช้ว่าเป็นร่าง + ยังไม่อนุมัติ', env.toasts.some(t => t.m.includes('ร่าง') && t.m.includes('ยังไม่อนุมัติ')), env.toasts);
    check('(c) ยกเลิก: หน้าต่างปิด', !modalOpen(env));
  }
  // (c2) Escape = ยกเลิก (Layout เรียก _onDismiss ของ modal บนสุด)
  {
    check('(c2) layout.js: Escape เรียก _onDismiss ของ modal บนสุด',
      /e\.key === 'Escape'[\s\S]{0,400}?top\.classList\.remove\('active'\);[\s\S]{0,300}?top\._onDismiss\(\)/.test(layoutSrc));
    const env = makeEnv({ warnings: [GAP] });
    const page = makePage(src, env);
    const p = page._postSaveApproval(env.api, Object.assign({}, ctx));
    await settle(p, 30);
    const w = modal(env);
    if (w) { w.classList.remove('active'); if (typeof w._onDismiss === 'function') w._onDismiss(); }
    const r = await settle(p);
    check('(c2) Escape: จบเป็นร่าง ไม่ค้าง ไม่ส่ง ack', r.done && r.val && r.val.approvalDeclined === true && !env.log.includes('approve:ack'), { r, log: env.log });
  }
  // (c3) × ปิดหน้าต่าง = ยกเลิก
  {
    const env = makeEnv({ warnings: [GAP] });
    const page = makePage(src, env);
    const p = page._postSaveApproval(env.api, Object.assign({}, ctx));
    await settle(p, 30);
    const clicked = await click(env, 'awClose');
    const r = await settle(p);
    check('(c3) ×: ต่อสายยกเลิกแล้ว — จบเป็นร่าง', clicked && r.done && r.val && r.val.approvalDeclined === true && !env.log.includes('approve:ack'), { clicked, r });
  }
  // (d) เงินสดทันที + มีคำเตือน → ยกเลิก ⇒ ไม่บันทึกชำระ และไม่ถามซ้ำในขั้น "บันทึกและอนุมัติ"
  {
    const env = makeEnv({ warnings: [GAP] });
    const page = makePage(src, env);
    const p = page._postSaveApproval(env.api, Object.assign({}, ctx, { paidNow: true }));
    await settle(p, 30);
    check('(d) เงินสด: ก่อนยืนยันไม่มี ack', !env.log.includes('approve:ack'), env.log);
    await click(env, 'awCancel');
    const r = await settle(p);
    check('(d) เงินสด+ยกเลิก: ไม่ถามซ้ำ (อนุมัติถูกเรียกครั้งเดียว)', env.log.filter(x => x.startsWith('approve')).length === 1, env.log);
    check('(d) เงินสด+ยกเลิก: ไม่บันทึกชำระ ไม่ดึงยอด', !env.log.includes('createPayment') && !env.log.includes('getDocument'), env.log);
    check('(d) เงินสด+ยกเลิก: จบเป็นร่าง', r.done && r.val && r.val.approvalDeclined === true, r);
  }
  // (e) เงินสดทันที + มีคำเตือน → ยืนยัน ⇒ ack แล้วค่อยบันทึกชำระ
  {
    const env = makeEnv({ warnings: [GAP] });
    const page = makePage(src, env);
    const p = page._postSaveApproval(env.api, Object.assign({}, ctx, { paidNow: true }));
    await settle(p, 30);
    await click(env, 'awConfirm');
    const r = await settle(p);
    const iAck = env.log.indexOf('approve:ack'), iPay = env.log.indexOf('createPayment');
    check('(e) เงินสด+ยืนยัน: ack ก่อนบันทึกชำระ', iAck >= 0 && iPay > iAck, env.log);
    check('(e) เงินสด+ยืนยัน: ไม่อนุมัติซ้ำในขั้นถัดไป', env.log.filter(x => x.startsWith('approve')).length === 2 && r.done, env.log);
  }
  // (f) ปุ่ม "อนุมัติ" ในหน้ารายละเอียด ใช้ทางเดียวกัน
  {
    const env = makeEnv({ warnings: [GAP] });
    const page = makePage(src, env);
    const p = page.approve('d-9');
    await settle(p, 30);
    check('(f) ปุ่มอนุมัติ: ครั้งแรกไม่มี ack + เปิดหน้าต่าง', JSON.stringify(env.log) === JSON.stringify(['approve:noack']) && modalOpen(env), env.log);
    await click(env, 'awConfirm');
    await settle(p);
    check('(f) ปุ่มอนุมัติ: ยืนยันแล้วส่ง ack + ปิด detail', env.log.includes('approve:ack') && env.log.includes('closeModal:detailModal'), env.log);
    const env2 = makeEnv({ warnings: [GAP] });
    const page2 = makePage(src, env2);
    const p2 = page2.approve('d-9');
    await settle(p2, 30);
    await click(env2, 'awCancel');
    const r2 = await settle(p2);
    check('(f) ปุ่มอนุมัติ+ยกเลิก: ไม่ ack ไม่ปิด detail', r2.done && !env2.log.includes('approve:ack') && !env2.log.includes('closeModal:detailModal'), env2.log);
  }
  // (g) error อื่น (403) ไม่ถูกตีความเป็นคำเตือน
  {
    const env = makeEnv({ forbidden: true });
    const page = makePage(src, env);
    const r = await settle(page._postSaveApproval(env.api, Object.assign({}, ctx)));
    check('(g) 403: ไม่เปิดหน้าต่าง ไม่ ack + บอกเรื่องสิทธิ์', r.done && !modal(env) && !env.log.includes('approve:ack')
      && env.toasts.some(t => t.m.includes('สิทธิ์')), { toasts: env.toasts, log: env.log });
  }
  // (h) static: ทั้งหน้าเรียก approveDocument เฉพาะใน _approveConfirmingWarnings · ไม่มี acknowledgeWarnings: true นอกตัวนั้น
  {
    const code = src.replace(/\/\*[\s\S]*?\*\//g, '').replace(/(^|[^:])\/\/[^\n]*/g, '$1');
    const helper = extract(code, '_approveConfirmingWarnings') || '';
    const rest = code.replace(helper, '');
    const outside = (rest.match(/approveDocument\s*\(/g) || []).length;
    check('(h) approveDocument( นอก _approveConfirmingWarnings = 0 จุด', outside === 0, outside);
    const ackTrue = (rest.match(/acknowledgeWarnings\s*:\s*(true|!!)/g) || []).length;
    check('(h) acknowledgeWarnings:true นอก _approveConfirmingWarnings = 0 จุด', ackTrue === 0, ackTrue);
  }
  return fails;
}

// ── negative test: ใส่บั๊กกลับทีละแบบ แล้วชุดเดียวกันต้องล้ม ──
const MUTATIONS = [
  { name: 'M1 ส่ง ack ตั้งแต่ครั้งแรก (บั๊กเดิม)',
    from: "await api.approveDocument(docId, { acknowledgeWarnings: false });",
    to: "await api.approveDocument(docId, { acknowledgeWarnings: true });" },
  { name: 'M2 บันทึกและอนุมัติข้ามหน้าต่าง เรียก approve+ack ตรง',
    from: /const ar = await this\._approveConfirmingWarnings\(api, savedDocId\);/g,
    to: "const ar = (await api.approveDocument(savedDocId, { acknowledgeWarnings: true }), { approved: true });" },
  { name: 'M3 ยกเลิกแล้วนับเป็นอนุมัติ',
    from: "resolve({ approved: false, cancelled: true",
    to: "resolve({ approved: true, cancelled: true" },
  { name: 'M4 ไม่ต่อสายยกเลิก (ผู้เรียกค้างรอตลอดกาล)',
    from: "if (typeof onCancel === 'function') onCancel();",
    to: "" },
  { name: 'M5 ถามซ้ำหลังผู้ใช้ยกเลิกในขั้นเงินสด',
    from: "if (approveAfter && savedDocId && !approvalDeclined",
    to: "if (approveAfter && savedDocId" },
  { name: 'M6 × ไม่ยกเลิก (กลับไปเป็น inline onclick แค่ซ่อน)',
    from: "if (closeBtn) closeBtn.onclick = cancel;",
    to: "" },
  { name: 'M7 Escape ไม่ยกเลิก (Layout ไม่เรียก _onDismiss)', layout: true,
    from: "if (typeof top._onDismiss === 'function') top._onDismiss();",
    to: "" },
];

function mutate(src, m) {
  if (typeof m.from === 'string') {
    if (!src.includes(m.from)) return null;
    return src.split(m.from).join(m.to);
  }
  if (!m.from.test(src)) return null;
  m.from.lastIndex = 0;
  return src.replace(m.from, m.to);
}

(async () => {
  let bad = 0;
  console.log('บันทึกและอนุมัติ / เงินสดทันที / ปุ่มอนุมัติ — คนต้องเห็นคำเตือนก่อนรับทราบ (โค้ดจริงจาก documents.html)');
  let fails;
  try { fails = await suite(REAL_SRC, LAYOUT_SRC); }
  catch (e) { console.log('  ✗ รันชุดกับโค้ดจริงไม่ได้: ' + e.message); process.exit(1); }
  if (fails.length) { fails.forEach(f => console.log('  ✗ ' + f)); bad++; }
  else console.log('  ✓ โค้ดจริงผ่านทุกเหตุการณ์ (a)–(h)');

  console.log('negative test — ใส่บั๊กกลับแล้วต้องถูกจับ');
  for (const m of MUTATIONS) {
    const base = m.layout ? LAYOUT_SRC : REAL_SRC;
    const mut = mutate(base, m);
    if (mut === null) { console.log(`  ✗ ${m.name}: หาจุดกลายพันธุ์ไม่เจอ — โค้ดเปลี่ยนแล้ว ปรับ sim ให้ตรง`); bad++; continue; }
    let mf;
    try { mf = await suite(m.layout ? REAL_SRC : mut, m.layout ? mut : LAYOUT_SRC); }
    catch (e) { mf = ['throw: ' + e.message]; }
    if (mf.length) console.log(`  ✓ ${m.name} → จับได้ (${mf[0]})`);
    else { console.log(`  ✗ ${m.name} → ชุดยังผ่าน = ด่านไม่มีจริง`); bad++; }
  }
  if (bad) { console.log(`❌ save_approve_warnings_sim ล้ม ${bad} ข้อ`); process.exit(1); }
  console.log('✅ save_approve_warnings_sim ผ่าน');
})();
