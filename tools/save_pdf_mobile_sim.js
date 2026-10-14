// ล็อกพฤติกรรม "บันทึก PDF แล้วต้องได้ไฟล์ — โดยเฉพาะบนมือถือ" ด้วย **โค้ดจริง** (`Layout.savePdf` / `deliverFile` /
// `_offerFileTap` / `isMobileDevice` / `isIosDevice` ใน wwwroot/js/layout.js + `API.fetchFile` ใน wwwroot/js/api.js)
//
// ที่มา: ผู้ใช้รายงาน 2026-10-14 (ภาพจากมือถือ) — "กดบันทึก pdf ขึ้นแต่หน้าปริ้น บนมือถือกดมาเป็นไฟล์ไม่ได้"
// หน้า 50 ทวิ เปิด about:blank + window.print() เป็นทางเดียว ⇒ Android Chrome / iOS Safari ไม่ได้ไฟล์ ·
// ทางใหม่: ไฟล์ PDF ของเซิร์ฟเวอร์ → มือถือ = แผ่นแชร์ของระบบ (navigator.share({files})) · เดสก์ท็อป = <a download> ·
// iOS ที่แชร์ไม่ได้ = เปิดไฟล์ในแท็บเดิม · ผู้ใช้ปิดแผ่นแชร์ (AbortError) = เงียบ · ล้ม = toast ข้อผิดพลาด
//
// ทิศที่ล็อกพร้อมกัน (F2 ข้อ 8): แชร์ได้ → แชร์ (ไม่ดาวน์โหลดซ้ำ) · แชร์ไม่ได้ → ดาวน์โหลด · ยกเลิก → ไม่มี error ·
// สัมผัสหมดอายุ → แถบ "แตะเพื่อบันทึก" · เดสก์ท็อปที่มี navigator.share → ยังดาวน์โหลด (ไม่เด้งแผ่นแชร์ของ Windows) ·
// ดึงไฟล์ล้ม → toast error + ไม่ส่งไฟล์ · 401 → ไม่ส่งไฟล์
// + สายไฟของหน้า (wht/documents/tax/etax เรียก Layout.savePdf · ไม่มี fetch ดิบ/a.download เอง)
//
// negative test ของตัว sim เอง (F2 ข้อ 6): ใส่บั๊กกลับเข้าซอร์สแล้วชุดเดียวกันต้องล้ม
'use strict';
const fs = require('fs');
const path = require('path');

const ROOT = path.join(__dirname, '..', 'Accounting', 'wwwroot');
const LAYOUT = fs.readFileSync(path.join(ROOT, 'js', 'layout.js'), 'utf8');
const APIJS = fs.readFileSync(path.join(ROOT, 'js', 'api.js'), 'utf8');
const PAGES = {
  wht: fs.readFileSync(path.join(ROOT, 'pages', 'wht.html'), 'utf8'),
  documents: fs.readFileSync(path.join(ROOT, 'pages', 'documents.html'), 'utf8'),
  tax: fs.readFileSync(path.join(ROOT, 'pages', 'tax.html'), 'utf8'),
  etax: fs.readFileSync(path.join(ROOT, 'pages', 'etax.html'), 'utf8'),
};

/** ตัดเมธอด `[async] name(args) { … }` ของ object literal ออกมาทั้งก้อน (นับวงเล็บปีกกา) */
function extractMethod(src, name) {
  const re = new RegExp('\\n\\s*((?:async\\s+)?)' + name + '\\s*\\(([^)]*)\\)\\s*\\{');
  const m = re.exec(src);
  if (!m) return null;
  let i = m.index + m[0].length, depth = 1;
  for (; i < src.length && depth > 0; i++) {
    const ch = src[i];
    if (ch === '{') depth++;
    else if (ch === '}') depth--;
  }
  return `${m[1]}${name}(${m[2]}) {` + src.slice(m.index + m[0].length, i);
}

// ───────── DOM จิ๋ว ─────────
class FakeEl {
  constructor(tag, doc) {
    this.tag = tag; this.doc = doc; this.attrs = {}; this.style = {}; this.children = [];
    this.innerHTML = ''; this.clicked = 0; this._btns = {};
  }
  setAttribute(k, v) { this.attrs[k] = String(v); }
  appendChild(c) { this.children.push(c); c.parent = this; return c; }
  click() { this.clicked++; this.doc.clicks.push({ href: this.href, download: this.download }); }
  remove() { if (this.parent) this.parent.children = this.parent.children.filter(x => x !== this); this.parent = null; }
  querySelector(sel) {
    const m = /data-act="(\w+)"/.exec(sel);
    const key = m ? m[1] : sel;
    return this._btns[key] || (this._btns[key] = new FakeEl('button', this.doc));
  }
}
function makeDoc() {
  const doc = { clicks: [] };
  doc.body = new FakeEl('body', doc);
  doc.createElement = (t) => new FakeEl(t, doc);
  doc.getElementById = (id) => doc.body.children.find(c => c.id === id) || null;
  return doc;
}

const UA = {
  android: { userAgent: 'Mozilla/5.0 (Linux; Android 14; Pixel 8) AppleWebKit/537.36 Chrome/129.0 Mobile Safari/537.36', platform: 'Linux armv8l', maxTouchPoints: 5 },
  iphone: { userAgent: 'Mozilla/5.0 (iPhone; CPU iPhone OS 17_5 like Mac OS X) AppleWebKit/605.1.15 Version/17.5 Mobile/15E148 Safari/604.1', platform: 'iPhone', maxTouchPoints: 5 },
  ipados: { userAgent: 'Mozilla/5.0 (Macintosh; Intel Mac OS X 10_15_7) AppleWebKit/605.1.15 Version/17.5 Safari/605.1.15', platform: 'MacIntel', maxTouchPoints: 5 },
  mac: { userAgent: 'Mozilla/5.0 (Macintosh; Intel Mac OS X 10_15_7) AppleWebKit/605.1.15 Version/17.5 Safari/605.1.15', platform: 'MacIntel', maxTouchPoints: 0 },
  windows: { userAgent: 'Mozilla/5.0 (Windows NT 10.0; Win64; x64) AppleWebKit/537.36 Chrome/129.0 Safari/537.36', platform: 'Win32', maxTouchPoints: 0 },
};

/** สภาพแวดล้อมของ deliverFile — share: undefined = ไม่มี API · 'ok' · 'abort' · 'notallowed' · 'notallowed-once' · 'type' */
function makeEnv(device, share, canShare = true) {
  const doc = makeDoc();
  const shares = [];
  let calls = 0;
  const nav = Object.assign({}, UA[device]);
  if (share !== undefined) {
    nav.share = async (data) => {
      calls++;
      shares.push(data);
      const mode = share === 'notallowed-once' ? (calls === 1 ? 'notallowed' : 'ok') : share;
      if (mode === 'ok') return;
      const e = new Error(mode);
      e.name = mode === 'abort' ? 'AbortError' : mode === 'notallowed' ? 'NotAllowedError' : 'TypeError';
      throw e;
    };
    nav.canShare = () => canShare;
  }
  class FakeFile { constructor(parts, name, o) { this.parts = parts; this.name = name; this.type = o && o.type; } }
  const loc = { href: 'https://app.example/pages/wht.html' };
  return {
    env: {
      navigator: nav, document: doc, location: loc,
      URL: { createObjectURL: () => 'blob:https://app.example/1234', revokeObjectURL: () => {} },
      File: FakeFile, setTimeout: () => 0,
    },
    doc, shares, loc,
  };
}

function buildLayout(src, fetchFile) {
  const names = ['savePdf', 'deliverFile', '_offerFileTap', 'isMobileDevice', 'isIosDevice'];
  const parts = names.map(n => extractMethod(src, n));
  if (parts.some(p => !p)) return null;
  const toasts = [];
  const API = { fetchFile };
  // eslint-disable-next-line no-new-func
  const L = new Function('API', `return { ${parts.join(',\n')} };`)(API);
  L.toasts = toasts;
  L.toast = (msg, type) => toasts.push({ msg: String(msg), type });
  L.esc = (s) => String(s).replace(/[&<>"']/g, c => ({ '&': '&amp;', '<': '&lt;', '>': '&gt;', '"': '&quot;', "'": '&#39;' })[c]);
  L._t = (k, f) => f;
  return L;
}

function buildApi(src, fetchImpl, store) {
  const m = extractMethod(src, 'fetchFile');
  if (!m) return null;
  const ApiBusy = { begin: () => ({}), end: () => {} };
  const localStorage = { getItem: (k) => store[k] ?? null, removeItem: (k) => { delete store[k]; } };
  const window = { location: { href: '/pages/wht.html' } };
  // eslint-disable-next-line no-new-func
  const api = new Function('ApiBusy', 'localStorage', 'window', 'fetch',
    `return { baseUrl: '', token: null, _t: (k, f) => f, ${m} };`)(ApiBusy, localStorage, window, fetchImpl);
  api._window = window;
  return api;
}

const tick = () => new Promise(r => setImmediate(r));

async function suite(layoutSrc, apiSrc, quiet) {
  let fail = 0;
  const check = (name, cond, extra = '') => {
    if (!quiet) console.log(`  ${cond ? '✓' : '✗'} ${name}${!cond && extra ? ' — ' + extra : ''}`);
    if (!cond) fail++;
  };
  const blob = { size: 1234, type: 'application/pdf' };
  const okFetch = async () => ({ blob, name: 'WHT-WHT-202610-0002.pdf' });

  const L0 = buildLayout(layoutSrc, okFetch);
  if (!L0) { if (!quiet) console.log('  ✗ หาเมธอด savePdf/deliverFile/... ใน layout.js ไม่เจอ'); return 99; }

  const run = async (device, share, canShare) => {
    const L = buildLayout(layoutSrc, okFetch);
    const e = makeEnv(device, share, canShare);
    const r = await L.deliverFile(blob, 'WHT-1.pdf', 'application/pdf', { env: e.env });
    return { L, e, r };
  };
  const errToasts = (L) => L.toasts.filter(t => t.type === 'error');

  // A) Android + แชร์ไฟล์ได้ → แผ่นแชร์ (ไฟล์ PDF ชื่อถูก) · ไม่ดาวน์โหลดซ้ำ
  {
    const { L, e, r } = await run('android', 'ok');
    const f = e.shares[0] && e.shares[0].files && e.shares[0].files[0];
    check('Android + share ได้ → แผ่นแชร์ของระบบ', r === 'shared', r);
    check('  ไฟล์ที่แชร์ = WHT-1.pdf ชนิด application/pdf', !!f && f.name === 'WHT-1.pdf' && f.type === 'application/pdf');
    check('  ไม่ดาวน์โหลดซ้ำ · ไม่มี error', e.doc.clicks.length === 0 && errToasts(L).length === 0);
  }
  // B) Android ไม่มี navigator.share → ดาวน์โหลด
  {
    const { L, e, r } = await run('android', undefined);
    check('Android ไม่มี share → <a download>', r === 'downloaded' && e.doc.clicks.length === 1 && e.doc.clicks[0].download === 'WHT-1.pdf', r);
    check('  แจ้งสำเร็จ (ไม่เงียบ)', L.toasts.some(t => t.type === 'success'));
  }
  // C) ผู้ใช้ปิดแผ่นแชร์ → เงียบ (ไม่ใช่ error · ไม่ดาวน์โหลดเอง)
  {
    const { L, e, r } = await run('android', 'abort');
    check('ปิดแผ่นแชร์ (AbortError) → จบเงียบ ไม่มี error toast', r === 'cancelled' && errToasts(L).length === 0 && e.doc.clicks.length === 0, r);
  }
  // D) สัมผัสหมดอายุระหว่างรอเซิร์ฟเวอร์ → แถบ "แตะเพื่อบันทึก" → แตะ = แชร์อีกครั้ง
  {
    const { L, e, r } = await run('iphone', 'notallowed-once');
    const bar = e.doc.getElementById('fileTapBar');
    check('NotAllowedError → แถบแตะเพื่อบันทึก', r === 'tap' && !!bar, r);
    check('  ชื่อไฟล์บนแถบถูก escape', !!bar && bar.innerHTML.includes('WHT-1.pdf'));
    if (bar) { bar.querySelector('[data-act="save"]').onclick(); await tick(); await tick(); }
    check('  แตะแล้วแชร์ครั้งที่สอง (การแตะใหม่ = user activation ใหม่)', e.shares.length === 2 && !e.doc.getElementById('fileTapBar'));
    check('  ไม่มี error toast', errToasts(L).length === 0);
  }
  // D2) แตะแล้วยัง NotAllowed → ไม่วนแถบซ้ำ ไปทางเปิดไฟล์ (iOS)
  {
    const { e } = await run('iphone', 'notallowed');
    const bar = e.doc.getElementById('fileTapBar');
    if (bar) { bar.querySelector('[data-act="save"]').onclick(); await tick(); await tick(); }
    check('แตะแล้วยังแชร์ไม่ได้ → ไม่วนแถบซ้ำ · เปิดไฟล์ในแท็บเดิม (iOS)',
      !e.doc.getElementById('fileTapBar') && e.loc.href.startsWith('blob:'), e.loc.href);
  }
  // E) iPhone ไม่มี share → เปิดไฟล์ในแท็บเดิม + บอกวิธีบันทึก
  {
    const { L, e, r } = await run('iphone', undefined);
    check('iOS แชร์ไม่ได้ → เปิดไฟล์ในแท็บเดิม (download ถูกเมิน)', r === 'opened' && e.loc.href.startsWith('blob:') && e.doc.clicks.length === 0, r);
    check('  บอกวิธี "บันทึกไปยังไฟล์"', L.toasts.some(t => t.type === 'info' && t.msg.includes('บันทึกไปยังไฟล์')));
  }
  // F) เดสก์ท็อป Windows ที่มี navigator.share → ยังดาวน์โหลด (ทิศตรงข้าม: ไม่เด้งแผ่นแชร์บนเดสก์ท็อป)
  {
    const { e, r } = await run('windows', 'ok');
    check('เดสก์ท็อปที่มี share → ดาวน์โหลด ไม่ใช่แผ่นแชร์', r === 'downloaded' && e.shares.length === 0, r);
  }
  {
    const { r } = await run('mac', 'ok');
    check('Mac (ไม่มีจอสัมผัส) → ดาวน์โหลด', r === 'downloaded', r);
  }
  // G) canShare({files}) = false → ดาวน์โหลด
  {
    const { e, r } = await run('android', 'ok', false);
    check('canShare(files)=false → ดาวน์โหลด', r === 'downloaded' && e.shares.length === 0, r);
  }
  // H) แชร์ล้มด้วยเหตุอื่น → ยังได้ไฟล์ทางดาวน์โหลด
  {
    const { r } = await run('android', 'type');
    check('แชร์ล้ม (TypeError) → ดาวน์โหลดแทน (ไม่เงียบ)', r === 'downloaded', r);
  }
  // L) iPadOS แสดงตัวเป็น Mac → นับเป็นมือถือ + iOS
  check('iPadOS (MacIntel + จอสัมผัส) = มือถือ + iOS',
    L0.isMobileDevice({ navigator: UA.ipados }) === true && L0.isIosDevice({ navigator: UA.ipados }) === true);
  check('Android ≠ iOS · Windows ≠ มือถือ',
    L0.isIosDevice({ navigator: UA.android }) === false && L0.isMobileDevice({ navigator: UA.windows }) === false);

  // I/J/K) savePdf — ดึงไฟล์ล้ม / 401 / ชื่อไฟล์
  {
    const L = buildLayout(layoutSrc, async () => { throw new Error('ไม่มีสิทธิ์ดาวน์โหลดหนังสือรับรองนี้'); });
    let delivered = 0; L.deliverFile = async () => { delivered++; return 'downloaded'; };
    const r = await L.savePdf('/api/x/pdf', 'WHT-1.pdf', { method: 'POST' });
    check('ดึงไฟล์ล้ม → toast error ข้อความเซิร์ฟเวอร์ · ไม่ส่งไฟล์', r === 'error' && delivered === 0
      && L.toasts.some(t => t.type === 'error' && t.msg.includes('ไม่มีสิทธิ์')), r);
    check('  แจ้ง "กำลังสร้างไฟล์" ก่อน (มีความคืบหน้าให้เห็น)', L.toasts[0] && L.toasts[0].type === 'info');
  }
  {
    const L = buildLayout(layoutSrc, async () => null);
    let delivered = 0; L.deliverFile = async () => { delivered++; return 'downloaded'; };
    const r = await L.savePdf('/api/x/pdf', 'WHT-1.pdf');
    check('401 (fetchFile คืน null) → ไม่ส่งไฟล์', r === 'auth' && delivered === 0, r);
  }
  {
    let got = null;
    const L = buildLayout(layoutSrc, async (url, name, o) => { got = { url, name, o }; return { blob, name: 'a/b:c' }; });
    let deliveredName = null; L.deliverFile = async (b, n, t) => { deliveredName = n + '|' + t; return 'shared'; };
    await L.savePdf('/api/companies/c/document-templates/withholding-tax/1/pdf', 'WHT-1.pdf', { method: 'POST', body: { a: 1 } });
    check('savePdf ส่ง method/body ถึง fetchFile', !!got && got.o.method === 'POST' && got.o.body && got.o.body.a === 1);
    check('ชื่อไฟล์ปลอดภัย + ลงท้าย .pdf · ชนิด application/pdf', deliveredName === 'a_b_c.pdf|application/pdf', deliveredName);
  }

  // M) API.fetchFile — แนบ JWT · POST body เป็น JSON · ชื่อไฟล์จาก Content-Disposition · ล้ม = ข้อความเซิร์ฟเวอร์ · 401 = login
  {
    const store = { token: 'jwt-123' };
    let req = null;
    const api = buildApi(apiSrc, async (u, init) => {
      req = { u, init };
      return { status: 200, ok: true, blob: async () => blob,
        headers: { get: () => "attachment; filename=\"x.pdf\"; filename*=UTF-8''%E0%B8%A3%E0%B8%B2%E0%B8%A2%E0%B8%87%E0%B8%B2%E0%B8%99.pdf" } };
    }, store);
    if (!api) { check('หา API.fetchFile ใน api.js', false); return fail + 1; }
    const f = await api.fetchFile('/api/x/pdf', 'fb.pdf', { method: 'POST', body: { documentId: 'd1' } });
    check('fetchFile แนบ Authorization Bearer', !!req && req.init.headers.Authorization === 'Bearer jwt-123');
    check('fetchFile POST + body JSON', !!req && req.init.method === 'POST' && req.init.body === '{"documentId":"d1"}'
      && req.init.headers['Content-Type'] === 'application/json');
    check('ชื่อไฟล์จาก filename* (UTF-8 ไทย)', f && f.name === 'รายงาน.pdf' && f.blob === blob, f && f.name);
    const api2 = buildApi(apiSrc, async () => ({ status: 403, ok: false, json: async () => ({ message: 'ต้องมีสิทธิ์ Tax.View' }), headers: { get: () => '' } }), {});
    let msg = '';
    try { await api2.fetchFile('/api/x/pdf', 'a.pdf'); } catch (e) { msg = e.message; }
    check('ล้ม → throw ข้อความของเซิร์ฟเวอร์ (ไม่กลืน)', msg === 'ต้องมีสิทธิ์ Tax.View', msg);
    const st3 = { token: 't', user: 'u' };
    const api3 = buildApi(apiSrc, async () => ({ status: 401, ok: false, headers: { get: () => '' } }), st3);
    const r3 = await api3.fetchFile('/api/x/pdf', 'a.pdf');
    check('401 → คืน null + ล้าง token + ไปหน้า login', r3 === null && !st3.token && api3._window.location.href === '/login.html');
    const api4 = buildApi(apiSrc, async () => { throw new TypeError('Failed to fetch'); }, {});
    let m4 = '';
    try { await api4.fetchFile('/api/x/pdf', 'a.pdf'); } catch (e) { m4 = e.message; }
    check('เครือข่ายล้ม → ข้อความภาษาไทย', m4.includes('เชื่อมต่อเซิร์ฟเวอร์'), m4);
  }
  return fail;
}

// ───────── สายไฟของหน้า (ต่อจาก Layout.savePdf จริง · ไม่มี fetch ดิบ/a.download เอง) ─────────
function wiring(pages, quiet) {
  let fail = 0;
  const check = (name, cond) => { if (!quiet) console.log(`  ${cond ? '✓' : '✗'} ${name}`); if (!cond) fail++; };
  const w = pages.wht;
  const save = extractMethod(w, 'savePdfCert') || '';
  check('wht: savePdfCert → Layout.savePdf(api.generateWhtPdfUrl(id) … POST)',
    /Layout\.savePdf\(api\.generateWhtPdfUrl\(id\),[^;]*\{ method: 'POST' \}\)/.test(save));
  const print = extractMethod(w, 'printCert') || '';
  check('wht: printCert บนมือถือไปไฟล์ PDF ทันที (ก่อนเปิดหน้าต่างพิมพ์)',
    /^async printCert\(id\) \{\s*(\/\/[^\n]*\n\s*)*if \(Layout\.isMobileDevice\(\)\) return this\.savePdfCert\(id\);/.test(print));
  check('wht: หน้าต่างพิมพ์ถูกบล็อก → ส่งไฟล์แทน (ไม่ TypeError เงียบ)', /if \(!printWin\) \{[^}]*savePdfCert/.test(print));
  check('wht: หน้าต่างพิมพ์มีปุ่ม "ดาวน์โหลดไฟล์ PDF" ผ่านหน้าหลัก', print.includes('o.Page.savePdfCert(') && print.includes('⬇️ ดาวน์โหลดไฟล์ PDF'));
  check('wht: ป้ายเดิม "เลือกปลายทางเป็น Save as PDF" ถูกแทน', !w.includes('เลือกปลายทางเป็น "Save as PDF'));
  check('wht: ปุ่มแถว + ปุ่มรายละเอียด (ออกแล้ว/ยกเลิก) + หน้าต่างพิมพ์ เรียก savePdfCert (4 จุด)', (w.match(/Page\.savePdfCert\('\$\{[wd]\.id\}'/g) || []).length === 4);
  check('wht: ไม่มี downloadPdf ตายค้าง · ไม่มี a.download เอง', !/downloadPdf\(/.test(w) && !/\ba\.download\s*=/.test(w));
  const d = pages.documents;
  for (const fn of ['downloadServerPdf', 'downloadEtaxPdf', 'downloadEtaxPdfFromDetail']) {
    const body = extractMethod(d, fn) || '';
    check(`documents: ${fn} → Layout.savePdf (ไม่มี fetch ดิบ)`, body.includes('Layout.savePdf(') && !body.includes('fetch(') && !body.includes('.download ='));
  }
  check('documents: printPdf บนมือถือ → ไฟล์', /if \(Layout\.isMobileDevice\(\)\) return this\.downloadServerPdf\(\);/.test(extractMethod(d, 'printPdf') || ''));
  const t = extractMethod(pages.tax, 'exportPdf') || '';
  check('tax: exportPdf → Layout.savePdf(... export-pdf ... download=true)', /Layout\.savePdf\(`[^`]*export-pdf\?kind=\$\{k\}&download=true`/.test(t) && !t.includes('window.open'));
  check('tax: ฟอร์มราชการบนมือถือ → ชี้ปุ่มไฟล์ (ไม่เปิดหน้าพิมพ์)', /if \(Layout\.isMobileDevice\(\)\) \{ Layout\.toast\(/.test(extractMethod(pages.tax, 'printOfficialForm') || ''));
  const e = extractMethod(pages.etax, 'downloadPdfA3') || '';
  check('etax: downloadPdfA3 → Layout.savePdf (เดิมไม่แนบ JWT ⇒ 401)', e.includes('Layout.savePdf(') && !e.includes("credentials: 'include'"));
  return fail;
}

(async () => {
  console.log('layout.js/api.js — บันทึก PDF ได้ "ไฟล์" บนมือถือ (2026-10-14 · 50 ทวิ)');
  const fail = await suite(LAYOUT, APIJS, false);
  const wfail = wiring(PAGES, false);

  const mutations = [
    ['ถอดทาง AbortError (ปิดแผ่นแชร์กลายเป็นดาวน์โหลด)', 'L', "if (n === 'AbortError') return 'cancelled';", ''],
    ['ปิดทางมือถือ (กลับเป็นดาวน์โหลดอย่างเดียว)', 'L', 'if (this.isMobileDevice(env) && typeof nav.share', 'if (false && typeof nav.share'],
    ['ถอดทาง iOS (เปิดไฟล์ในแท็บเดิม)', 'L', 'if (this.isIosDevice(env)) {', 'if (false) {'],
    ['ถอดแถบแตะเพื่อบันทึก', 'L', "if (n === 'NotAllowedError' && !opts.fromTap) {", 'if (false) {'],
    ['savePdf กลืน error เงียบ', 'L', "this.toast((e && e.message) || 'ดาวน์โหลด PDF ไม่สำเร็จ', 'error');", ''],
    ['fetchFile ทิ้ง body ของ POST', 'A', 'init.body = JSON.stringify(opts.body);', ''],
    ['fetchFile ให้ filename= ชนะ filename* (ชื่อไทยหาย)', 'A', 'if (star) {', 'if (plain) { name = plain[1]; } else if (star) {'],
    ['fetchFile ไม่แนบ JWT', 'A', "if (this.token) headers['Authorization'] = `Bearer ${this.token}`;\n      const init", 'const init'],
  ];
  let negOk = true;
  for (const [label, which, from, to] of mutations) {
    const src = which === 'L' ? LAYOUT : APIJS;
    if (!src.includes(from)) { console.log(`✗ negative test "${label}": หาโค้ดในซอร์สไม่เจอ — sim ไม่มีด่าน`); negOk = false; continue; }
    const broken = src.replace(from, to);
    const n = await suite(which === 'L' ? broken : LAYOUT, which === 'A' ? broken : APIJS, true);
    console.log(n > 0 ? `✓ negative test: ${label} → ชุดเดียวกันล้ม ${n} ข้อ` : `✗ negative test: ${label} แล้วยังผ่าน — sim ไม่มีด่าน`);
    if (!(n > 0)) negOk = false;
  }
  // สายไฟ: ถอดทางมือถือของ printCert / ใส่ fetch ดิบกลับ → ต้องล้ม
  const wMut = [
    ['wht printCert ไม่มีทางมือถือ', 'wht', 'if (Layout.isMobileDevice()) return this.savePdfCert(id);', ''],
    ['etax กลับไป fetch credentials:include', 'etax', 'return Layout.savePdf(`/api/companies/${this.companyId}/etax/${etaxId}/pdf`', "fetch(url, { credentials: 'include' }); return Layout.savePdf(`/api/companies/${this.companyId}/etax/${etaxId}/pdf`"],
  ];
  for (const [label, page, from, to] of wMut) {
    if (!PAGES[page].includes(from)) { console.log(`✗ negative test "${label}": หาโค้ดไม่เจอ`); negOk = false; continue; }
    const n = wiring(Object.assign({}, PAGES, { [page]: PAGES[page].replace(from, to) }), true);
    console.log(n > 0 ? `✓ negative test: ${label} → สายไฟล้ม ${n} ข้อ` : `✗ negative test: ${label} แล้วยังผ่าน`);
    if (!(n > 0)) negOk = false;
  }
  const ok = fail === 0 && wfail === 0 && negOk;
  console.log(ok ? 'ผ่าน' : `ล้ม (${fail + wfail} ข้อ${negOk ? '' : ' · negative test ไม่ครบ'})`);
  process.exit(ok ? 0 : 1);
})();
