#!/usr/bin/env node
// ═══ ล็อกพฤติกรรม: ใครมีสิทธิ์ตั้ง "อัตรา VAT ของบรรทัด" (Layout.setLineVat) ═══
//
// รัน: node tools/vat_line_source_sim.js
// (เทียบโค้ดรุ่นก่อนแก้: DOC=/path/to/old/documents.html node tools/vat_line_source_sim.js)
//
// ที่มา — บั๊กจริงที่ผู้ใช้รายงาน 2026-09-11: บิลเขียนมือของผู้ขายที่ไม่จด VAT
// (ยอด 3,500 · ช่อง VAT ว่าง · รวม 3,500) หน้าสแกนอ่านถูกและฟอร์มเติม 0% ถูกแล้ว
// แต่พอผู้ใช้แตะช่อง "รายละเอียด" แล้วคลิกที่อื่น เหตุการณ์ blur เรียก
// suggestVatType → endpoint heuristic ตอบ "7%" (ไม่เคยเห็นกระดาษ) แล้วทับทันที
// ⇒ ยอดสุทธิ 3,500 → 3,745 · VAT ผี 245 บาทเข้า ภ.พ.30
//
// เทสต์นี้ใช้ **โค้ดจริง** (ดึง suggestVatType จาก documents.html + โหลด layout.js
// ตัวจริงใน vm) ไม่ใช่โค้ดเลียนแบบ — และล็อก **สองทิศ** ตามกฎเหล็ก #4 H:
//   • ทิศที่พัง: OCR / เอกสารที่บันทึกไว้ / ชนิดเอกสาร / ผู้ใช้ ต้องไม่ถูกทับ
//   • ทิศที่ต้องยังทำงาน: บรรทัดใหม่ที่ยังไม่มีใครตัดสิน ตัวแนะนำต้องยังเติมได้
//     (ไม่งั้น "แก้บั๊ก" กลายเป็น "ปิดด่านทิ้ง" ซึ่งผ่านเทสต์ครึ่งเดียวได้เหมือนกัน)
// บิลเขียนมือของผู้ขายที่ไม่จด VAT: ยอด 3,500 · ช่อง VAT ว่าง · รวม 3,500
// สแกนอ่านถูก ฟอร์มเติม 0% ถูก — แต่พอ blur ช่องรายละเอียด ตัวแนะนำดัน 7%
// ⇒ 3,500 → 3,745. ใช้ **โค้ดจริง** จาก documents.html + layout.js
const fs = require('fs'), vm = require('vm');
const path = require('path');
const ROOT = path.join(__dirname, '..', 'Accounting', 'wwwroot');

// ---- โหลด Layout ตัวจริง (เฉพาะส่วน object literal) ----
const layoutSrc = fs.readFileSync(ROOT + '/js/layout.js', 'utf8');
const ctx = {
  window: { addEventListener() {}, location: { pathname: '/', search: '' }, matchMedia: () => ({ matches: false, addEventListener() {} }) },
  document: { addEventListener() {}, getElementById: () => null, querySelectorAll: () => [], querySelector: () => null,
              createElement: () => ({ style: {}, classList: { add() {}, remove() {} }, appendChild() {} }),
              body: { appendChild() {} }, documentElement: { style: {} } },
  localStorage: { getItem: () => null, setItem() {}, removeItem() {} },
  sessionStorage: { getItem: () => null, setItem() {}, removeItem() {} },
  navigator: { userAgent: 'node', serviceWorker: null },
  fetch: async () => ({ json: async () => ({}) }),
  console, setTimeout, clearTimeout, setInterval, clearInterval,
};
ctx.globalThis = ctx; ctx.self = ctx;
vm.createContext(ctx);
try { vm.runInContext(layoutSrc + '\n;globalThis.__L = Layout;', ctx); } catch (e) { console.log('⚠️ layout.js โหลดไม่จบ:', e.message); }
const Layout = ctx.__L;
if (!Layout || !Layout.setLineVat) { console.error('❌ โหลด Layout.setLineVat ไม่ได้'); process.exit(1); }

// ---- ดึง suggestVatType ตัวจริงจาก documents.html ----
const src = fs.readFileSync(process.env.DOC || (ROOT + '/pages/documents.html'), 'utf8');
const start = src.indexOf('      async suggestVatType(row) {');
const end = src.indexOf('\n      },', start);
const body = src.slice(start, end).replace(/^      async suggestVatType\(row\) \{/, '');
const suggestVatType = new Function('row', 'Layout', 'localStorage', 'fetch', 'document',
  'return (async () => {' + body + '})()');

// ---- DOM stub ขั้นต่ำ ----
const makeRow = (desc, price, vatValue, src_) => {
  const vat = { value: vatValue, dataset: {}, title: '' };
  if (src_) vat.dataset.vatSrc = src_;
  const els = { desc: { value: desc }, price: { value: String(price) }, vat, vatClaim: { dataset: {}, style: {} } };
  return { querySelector: (s) => els[s.match(/data-f="(\w+)"/)[1]] || null, _vat: vat };
};
const docStub = { getElementById: (id) => ({ value: id === 'fContactId' ? 'ct-1' : '' }) };
const ls = { getItem: () => 'tok' };
// เซิร์ฟเวอร์จริง: ThaiVatTypeRule.Suggest("<คำทั่วไป>", null, null) → 7 (conf 0.95)
const fetchStub = async () => ({ json: async () => ({ success: true, data: { vatType: 7, confidence: 0.95, feedbackId: 'f1' } }) });
const page = { calcSum() {}, syncVatClaimWithRate() {}, _aiHint: () => 'AI: มาตรฐาน 7%' };

const money = (r) => (3500 * (1 + (parseFloat(r) || 0) / 100)).toFixed(2);
let fail = 0;
const check = (name, got, want) => {
  const ok = String(got) === String(want);
  if (!ok) fail++;
  console.log(`${ok ? '✅' : '❌'} ${name}: ได้ ${got} (ต้องได้ ${want})`);
};

(async () => {
  // 1) ใบที่กระดาษไม่มี VAT — เคสที่ผู้ใช้เจอ
  const r1 = makeRow('ซื้อของซ่อมแซม', 3500, '0', 'ocr');
  await suggestVatType.call(page, r1, Layout, ls, fetchStub, docStub);
  check('บิลไม่มี VAT (ocr) blur แล้วต้องคง 0% · ยอด ' + money(r1._vat.value), r1._vat.value, '0');

  // 2) ใบรับรองแทนใบเสร็จ — ยกเว้นตามชนิดเอกสาร (ไม่มีใบกำกับ §82/5(1))
  const r2 = makeRow('ค่าจ้างซ่อมแซม', 3500, '-1', 'doctype');
  await suggestVatType.call(page, r2, Layout, ls, fetchStub, docStub);
  check('ใบรับรองแทนใบเสร็จต้องคง "ยกเว้น"', r2._vat.value, '-1');

  // 3) เปิดแก้เอกสารเดิมที่บันทึก 0% ไว้
  const r3 = makeRow('ค่าบริการ', 3500, '0', 'doc');
  await suggestVatType.call(page, r3, Layout, ls, fetchStub, docStub);
  check('เอกสารที่บันทึกไว้ 0% ต้องไม่ถูกทับ', r3._vat.value, '0');

  // 4) ผู้ใช้เลือกเอง
  const r4 = makeRow('ค่าบริการ', 3500, '0'); r4._vat.dataset.userTouched = '1';
  await suggestVatType.call(page, r4, Layout, ls, fetchStub, docStub);
  check('ค่าที่ผู้ใช้เลือกเองต้องไม่ถูกทับ', r4._vat.value, '0');

  // 5) NEGATIVE — บรรทัดที่ผู้ใช้เพิ่มเองในฟอร์มเปล่า (ไม่มีใครตัดสิน)
  //    ตัวแนะนำต้องยังทำงาน ไม่ใช่ "ปิดด่านทิ้ง"
  const r5 = makeRow('ค่าบริการทั่วไป', 3500, '0');
  await suggestVatType.call(page, r5, Layout, ls, fetchStub, docStub);
  check('บรรทัดใหม่ที่ยังไม่มีใครตัดสิน ตัวแนะนำต้องยังเติม 7% ได้', r5._vat.value, '7');

  // 6) ตัวแนะนำแก้คำตอบของตัวเองได้ (rank เท่ากัน)
  const r6 = makeRow('นมสด', 3500, '7', 'ai');
  const fetchExempt = async () => ({ json: async () => ({ success: true, data: { vatType: 'Exempt', feedbackId: 'f2' } }) });
  await suggestVatType.call(page, r6, Layout, ls, fetchExempt, docStub);
  check('ตัวแนะนำแก้ค่าของตัวเองได้', r6._vat.value, '-1');

  // 7) ผูกสินค้าจากคลัง ชนะค่าที่ OCR อ่านมา
  const r7 = makeRow('น้ำดื่ม', 3500, '0', 'ocr');
  check('คลังสินค้าเขียนทับ OCR ได้', Layout.setLineVat(r7._vat, 7, 'product'), 'true');

  console.log(fail ? `\n❌ ตก ${fail} ข้อ` : '\n✅ ผ่านครบทุกข้อ');
  process.exit(fail ? 1 : 0);
})();
