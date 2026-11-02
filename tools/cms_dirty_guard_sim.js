// รอบ 228 (erp-review/2026-11-02/CMS_SETTINGS_UX_AUDIT.md แพ็ก B · §1 ข้อ 7) — ล็อก "ตัวกันค่าหาย" ด้วย **โค้ดจริง** ของ cms-site-context.js (CmsDirtyGuard)
// ที่มา: สลับกลุ่ม/สลับเว็บ/ปิดหน้า ในหน้าตั้งค่า CMS แล้วค่าที่พิมพ์หายเงียบ (เจ้าของคิดว่าบันทึกแล้ว)
// กติกาที่ล็อก: (ก) แก้ช่อง ⇒ ค้าง · สลับต้องถาม · ไม่ยืนยัน = อยู่ต่อ (ค่ายังค้าง) · ยืนยัน = ทิ้ง (ล้างสถานะ)
//               (ข) ไม่แก้อะไร ⇒ ไม่ถาม · ช่องไฟล์ไม่นับ · reset หลังบันทึก ⇒ ไม่ถาม
//               (ค) หน้าใช้งานจริง: cms-site-settings go()/ตัวสลับเว็บ · cms-store-settings ตัวสลับเว็บ เรียก confirmLeave
// negative tests: ถอด markDirty ออกจากซอร์สจริง / ถอดการเรียกในหน้า ⇒ ชุดเดียวกันต้องล้ม
'use strict';
const fs = require('fs');
const path = require('path');
const vm = require('vm');

const WWW = path.join(__dirname, '..', 'Accounting', 'wwwroot');
const SRC = fs.readFileSync(path.join(WWW, 'js', 'cms-site-context.js'), 'utf8');
const SETTINGS = fs.readFileSync(path.join(WWW, 'pages', 'cms-site-settings.html'), 'utf8');
const STORE = fs.readFileSync(path.join(WWW, 'pages', 'cms-store-settings.html'), 'utf8');

function el() {
  const h = {};
  return { isConnected: true, __h: h, addEventListener: (t, f) => { h[t] = f; }, querySelector: () => null, insertAdjacentHTML() {} };
}
function input(type) { return { type: type || 'text', dataset: {}, matches: (q) => /input/.test(q) }; }

function load(src) {
  let asked = 0, answer = false;
  const ctx = { console, document: { getElementById: () => null, addEventListener() {} }, location: { href: '' },
    confirm: () => { asked++; return answer; } };
  ctx.window = { addEventListener() {}, Layout: null };
  ctx.Layout = null;
  vm.createContext(ctx);
  try { vm.runInContext(src, ctx); } catch (e) { return null; }
  const G = ctx.window.CmsDirtyGuard;
  if (!G) return null;
  return { G, asked: () => asked, setAnswer: (a) => { answer = a; } };
}

function scenario(src) {
  const fails = [];
  const ok = (name, cond) => { if (!cond) fails.push(name); };
  const r = load(src);
  if (!r) return ['โหลด CmsDirtyGuard ไม่ได้'];
  const { G } = r;
  const card = el();
  G.watch(card);
  ok('(ข) ไม่แก้อะไร ⇒ ไม่ถาม', G.confirmLeave() === true && r.asked() === 0);
  card.__h.change && card.__h.change({ target: input('file') });
  ok('(ข) ช่องไฟล์ไม่นับ', G.isDirty() === false);
  const auto = input('text'); auto.dataset.noDirty = '1';
  card.__h.input && card.__h.input({ target: auto });
  ok('(ข) ช่องบันทึกเองทันที (data-no-dirty) ไม่นับ', G.isDirty() === false);
  card.__h.input && card.__h.input({ target: input('text') });
  ok('(ก) แก้ช่อง ⇒ ค้าง', G.isDirty() === true);
  r.setAnswer(false);
  ok('(ก) ไม่ยืนยัน ⇒ อยู่ต่อ', G.confirmLeave() === false && r.asked() === 1 && G.isDirty() === true);
  r.setAnswer(true);
  ok('(ก) ยืนยัน ⇒ ทิ้ง', G.confirmLeave() === true && G.isDirty() === false);
  card.__h.input && card.__h.input({ target: input('text') });
  G.reset(card);
  ok('(ข) reset หลังบันทึก ⇒ ไม่ถาม', G.confirmLeave() === true && r.asked() === 2);
  return fails;
}

function pageWired(settings, store) {
  const fails = [];
  const go = (settings.match(/async go\(key\) \{[\s\S]*?\n      \},/) || [''])[0];
  if (!/CmsDirtyGuard\.confirmLeave\(\)/.test(go)) fails.push('(ค) cms-site-settings go() ไม่ถามก่อนสลับกลุ่ม');
  if (!/renderCrumb\([^;]*'cms-site-settings',\s*\{\s*beforeLeave[^;]*confirmLeave/.test(settings)) fails.push('(ค) cms-site-settings ตัวสลับเว็บไม่ถาม');
  const sc = (store.match(/async onSiteChange\(\) \{[\s\S]*?\n      \},/) || [''])[0];
  if (!/if \(this\.siteId && window\.CmsDirtyGuard && !CmsDirtyGuard\.confirmLeave\(\)\)/.test(sc)) fails.push('(ค) cms-store-settings เปลี่ยนเว็บไม่ถาม');
  return fails;
}

let bad = 0;
const real = scenario(SRC).concat(pageWired(SETTINGS, STORE));
for (const f of real) { console.log('❌ ' + f); bad++; }

// negative tests — ถอดด่านแล้วต้องล้ม
const negs = [
  ['ถอดการข้าม data-no-dirty', () => scenario(SRC.replace(" || t.dataset.noDirty === '1'", ''))],
  ['ถอด markDirty ในตัวฟัง', () => scenario(SRC.replace('CmsDirtyGuard.markDirty(el);\n', '\n'))],
  ['ถอด confirm (ทิ้งเงียบ)', () => scenario(SRC.replace('if (!CmsDirtyGuard.isDirty()) return true;', 'CmsDirtyGuard.reset(); return true;'))],
  ['ถอดการถามใน go()', () => pageWired(SETTINGS.replace(/if \(key !== this\.group && window\.CmsDirtyGuard && !CmsDirtyGuard\.confirmLeave\(\)\)/, 'if (false)'), STORE)],
  ['ถอดการถามตอนเปลี่ยนเว็บร้านค้า', () => pageWired(SETTINGS, STORE.replace(/!CmsDirtyGuard\.confirmLeave\(\)\) \{ document/, 'false) { document'))],
];
for (const [name, run] of negs) {
  const f = run();
  if (!f.length) { console.log(`❌ negative "${name}": ถอดด่านแล้วยังผ่าน (sim ไม่จับ)`); bad++; }
}
if (bad) process.exit(1);
console.log(`cms_dirty_guard_sim: ผ่าน (${negs.length} negative tests ล้มตามคาด)`);
