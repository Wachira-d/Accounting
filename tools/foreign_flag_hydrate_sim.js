#!/usr/bin/env node
// ═══ ล็อกพฤติกรรม: ติ๊ก "ซื้อบริการจากต่างประเทศ" ตอนเปิดแก้ใบเดิม (ฝ่ายค้าน F2+F3 P2-4) ═══
//
// รัน: node tools/foreign_flag_hydrate_sim.js
// (เทียบโค้ดรุ่นก่อนแก้: DOC=/path/to/old/documents.html node tools/foreign_flag_hydrate_sim.js — รุ่นเก่าไม่มีเมธอดนี้ ⇒ ล้ม)
//
// ที่มา: onDocTypeChange ซ่อนช่อง + ล้าง checkbox ของชนิดที่ติ๊กไม่ได้ (ใบขาย/CIL) แต่ขั้น hydrate ถัดมาติ๊กกลับเป็น true
// ในช่องที่ซ่อน ⇒ payload ส่ง isForeignService=true ⇒ เซิร์ฟเวอร์ปฏิเสธ (Pp36Lifecycle.FlagTypeError) ⇒ ใบร่างเก่าบันทึกไม่ได้เลย
// ใช้ **โค้ดจริง** (_hydrateForeignServiceFlag จาก documents.html) · สองทิศ: ชนิดที่ติ๊กได้ยังติ๊กกลับ / ชนิดที่ติ๊กไม่ได้ถอด + แจ้ง
const fs = require('fs'), path = require('path');
const ROOT = path.join(__dirname, '..', 'Accounting', 'wwwroot');
const src = fs.readFileSync(process.env.DOC || (ROOT + '/pages/documents.html'), 'utf8');
const head = '      _hydrateForeignServiceFlag(d) {';
const start = src.indexOf(head);
if (start < 0) { console.error('❌ ไม่พบ _hydrateForeignServiceFlag ใน documents.html'); process.exit(1); }
const end = src.indexOf('\n      },', start);
const body = src.slice(start + head.length, end);
const hydrate = new Function('d', 'document', 'Layout', body);

let fail = 0;
const check = (name, cond) => { if (cond) console.log('  ✅ ' + name); else { console.log('  ❌ ' + name); fail++; } };

function run(wrapShown, isForeignService) {
  const cb = { checked: true };   // ค่าค้างจากใบก่อน — ต้องถูกตั้งใหม่เสมอ
  const wrap = { style: { display: wrapShown ? 'inline-flex' : 'none' } };
  const toasts = [];
  const doc = { getElementById: (id) => id === 'fIsForeignService' ? cb : id === 'fIsForeignServiceWrap' ? wrap : null };
  const ret = hydrate({ isForeignService }, doc, { toast: (m) => toasts.push(m) });
  return { checked: cb.checked, ret, toasts };
}

console.log('ทิศที่พัง — ชนิดที่ติ๊กไม่ได้ (ช่องซ่อน) แต่ใบเดิมมีธง:');
let r = run(false, true);
check('checkbox ถูกถอด (payload ไม่ส่ง true)', r.checked === false && r.ret === false);
check('ผู้ใช้เห็นว่าทำไม (toast)', r.toasts.length === 1 && r.toasts[0].includes('ติ๊กไม่ได้'));

console.log('ทิศที่ต้องยังทำงาน — ชนิดที่ติ๊กได้ (ช่องแสดง):');
r = run(true, true);
check('ใบต่างประเทศติ๊กกลับ', r.checked === true && r.ret === true && r.toasts.length === 0);
r = run(true, false);
check('ใบในประเทศไม่ติ๊ก (ล้างค่าค้างใบก่อน)', r.checked === false && r.toasts.length === 0);
r = run(false, false);
check('ชนิดติ๊กไม่ได้ + ไม่มีธง ⇒ เงียบ', r.checked === false && r.toasts.length === 0);

if (fail) { console.error(`❌ foreign_flag_hydrate_sim: ${fail} เคสล้ม`); process.exit(1); }
console.log('✅ foreign_flag_hydrate_sim: ผ่านทุกเคส');
