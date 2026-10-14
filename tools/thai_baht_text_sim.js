#!/usr/bin/env node
// ═══ ล็อก: สำเนา JS "จำนวนเงินเป็นตัวอักษร" (พรีวิวสด) ต้องตรงกับตัวตั้ง C# Helpers/ThaiBahtText.Format ทุกตัวอักษร ═══
//
// รัน: node tools/thai_baht_text_sim.js
// (เทียบไฟล์รุ่นอื่น: UX=/path/to/ux-helpers.js node tools/thai_baht_text_sim.js
//  เช่น `git show b3f9855f:Accounting/wwwroot/js/ux-helpers.js > /tmp/old.js` แล้ว UX=/tmp/old.js ⇒ ต้องล้ม)
//
// ที่มา (2026-10-14 · ทีม PDF): สูตรจำนวนเงินตัวอักษรเคยมี 4 สำเนา (C# 2 · JS 2) ไม่ตรงกันเอง และฝั่งพิมพ์ใช้
// units[pos % 7] ไม่วน "ล้าน" ⇒ ยอด ≥ 10 ล้านพิมพ์ผิดบนใบกำกับ/50 ทวิ · พรีวิว JS พิมพ์ 101 = "หนึ่งร้อยหนึ่ง" ขณะที่กระดาษ = "หนึ่งร้อยเอ็ด"
// หลังแก้: ตัวพิมพ์ทุกตัวเรียก C# · wht.html รับข้อความจาก DTO · เหลือสำเนา JS เดียว (ux-helpers.js — พรีวิวทุกครั้งที่กดแป้น)
//
// ตารางคาดหวัง **ไม่ได้เขียนซ้ำที่นี่** — อ่านจาก [InlineData("ยอด", "ตัวอักษร")] ใน Accounting.Tests/ThaiBahtTextTests.cs
// (เทสต์ xUnit ตัวเดียวกับที่ล็อก C#) ⇒ ตารางเดียว ตรวจสองภาษา
//
// negative test ในตัว (รันทุกครั้ง): กลายพันธุ์โค้ดจริงกลับเป็นบั๊กเดิมทีละแบบ แล้วตารางต้องจับได้ — ถ้าจับไม่ได้ = sim ไม่มีฟัน ⇒ ล้ม
const fs = require('fs'), vm = require('vm'), path = require('path');
const ROOT = path.join(__dirname, '..');
const uxPath = process.env.UX || path.join(ROOT, 'Accounting/wwwroot/js/ux-helpers.js');
const testPath = path.join(ROOT, 'Accounting.Tests/ThaiBahtTextTests.cs');

// ---- ตารางจากเทสต์ C# ----
const testSrc = fs.readFileSync(testPath, 'utf8');
const cases = [...testSrc.matchAll(/\[InlineData\("(-?[0-9.]+)",\s*"([^"]+)"\)\]/g)].map(m => [m[1], m[2]]);
if (cases.length < 30) { console.error(`❌ อ่านตารางจาก ThaiBahtTextTests.cs ได้แค่ ${cases.length} แถว (คาด ≥ 30) — รูปแบบ InlineData เปลี่ยน?`); process.exit(1); }
for (const must of ['10000000', '12345678', '1000000000000', '101', '1000001'])
  if (!cases.some(c => c[0] === must)) { console.error(`❌ ตารางขาดเคสบังคับ ${must}`); process.exit(1); }

// ---- โหลด ux-helpers.js ตัวจริง ----
const load = (src) => {
  const ctx = { window: {}, document: { addEventListener() {}, getElementById: () => null }, localStorage: { getItem: () => null, setItem() {} }, console };
  vm.createContext(ctx);
  vm.runInContext(src, ctx);
  const A = ctx.window.UxHelpers && ctx.window.UxHelpers.AmountInWords;
  if (!A || typeof A.convert !== 'function') throw new Error('โหลด UxHelpers.AmountInWords ไม่ได้');
  return A;
};
const run = (A) => {
  const bad = [];
  for (const [amt, want] of cases) {
    let got;
    try { got = A.convert(Number(amt)); } catch (e) { got = 'THROW ' + e.message; }
    if (got !== want) bad.push(`${amt}: ได้ "${got}" · คาด "${want}"`);
  }
  return bad;
};

const src = fs.readFileSync(uxPath, 'utf8');
const bad = run(load(src));
if (bad.length) {
  console.error(`❌ ux-helpers.js AmountInWords ไม่ตรงตัวตั้ง C# (ThaiBahtTextTests) ${bad.length}/${cases.length} เคส:`);
  bad.slice(0, 20).forEach(b => console.error('   ' + b));
  process.exit(1);
}
console.log(`✅ ux-helpers.js AmountInWords ตรงตาราง C# ครบ ${cases.length} เคส`);

// ---- negative test: ใส่บั๊กเดิมกลับทีละแบบ ต้องถูกจับ ----
if (!process.env.UX) {
  const mutants = [
    ['ไม่วน "ล้าน" (บั๊ก ≥ 10 ล้าน)', "if (n >= 1000000) {", "if (false) {"],
    ['เอ็ดเฉพาะเมื่อหลักสิบไม่ศูนย์ (101 = หนึ่งร้อยหนึ่ง)', "(hasHigher || v > 9)", "(Math.floor(v / 10) % 10 > 0)"],
    ['ยอดติดลบขึ้น "ลบ"', "if (baht === 0 && satang === 0) return 'ศูนย์บาทถ้วน';",
      "if (baht === 0 && satang === 0) return 'ศูนย์บาทถ้วน'; if (amount < 0) return 'ลบ' + this.convert(-amount);"],
    ['ปัดสตางค์แบบทศนิยมฐานสอง (1.005 → 1.00)', "Math.round(Number((Math.abs(Number(amount)) * 100).toPrecision(15)))",
      "Math.round(Math.abs(Number(amount)) * 100)"],
    ['0.50 พิมพ์ "ศูนย์บาทถ้วน"', "if (baht === 0 && satang === 0)", "if (baht === 0)"],
  ];
  let teeth = true;
  for (const [name, from, to] of mutants) {
    if (!src.includes(from)) { console.error(`❌ negative test "${name}": หาโค้ดต้นแบบไม่เจอ (${from}) — ปรับ sim ตามโค้ด`); teeth = false; continue; }
    const caught = run(load(src.replace(from, to))).length > 0;
    if (!caught) { console.error(`❌ negative test "${name}": ตารางจับไม่ได้`); teeth = false; }
  }
  if (!teeth) process.exit(1);
  console.log(`✅ negative test: บั๊กเดิม ${mutants.length} แบบถูกตารางจับได้ทุกแบบ`);

  // ---- ratchet ตัวตั้งเดียว: ห้ามมีสูตรประกอบ "…บาท…ถ้วน" ชุดที่สามในโค้ดแอป (.cs/.js/.html) ----
  // สัญญาณ = สตริงลิเทอรัล "ถ้วน" ตัวเดี่ยว (ตัวประกอบท้ายข้อความ) · ที่อนุญาต: ตัวตั้ง C# · สำเนาพรีวิว JS ที่ sim นี้ล็อก ·
  // ตัวอ่านขากลับ (OCR) ที่ใช้ "ถ้วน" เป็นคำค้น
  const ALLOW = new Set([
    'Accounting/Helpers/ThaiBahtText.cs', 'Accounting/wwwroot/js/ux-helpers.js',
    'Accounting/Helpers/ThaiAmountInWords.cs', 'Accounting/Helpers/OcrNonItemRow.cs',
  ]);
  const LIT = /["'`]ถ้วน["'`]/;
  const scan = (rel, text) => text.split('\n').some(l => !/^\s*(\/\/|\*|\/\*)/.test(l) && LIT.test(l)) && !ALLOW.has(rel);
  const offenders = [];
  const walk = (dir) => {
    for (const e of fs.readdirSync(path.join(ROOT, dir), { withFileTypes: true })) {
      const rel = dir + '/' + e.name;
      if (e.isDirectory()) { if (!['bin', 'obj', 'node_modules', 'lib'].includes(e.name)) walk(rel); continue; }
      if (!/\.(cs|js|html)$/.test(e.name)) continue;
      if (scan(rel, fs.readFileSync(path.join(ROOT, rel), 'utf8'))) offenders.push(rel);
    }
  };
  walk('Accounting');
  // negative: สำเนาเดิมของ wht.html (ก่อน 2026-10-14) ต้องถูกจับ
  if (!scan('Accounting/wwwroot/pages/wht.html', "          return cv(baht) + 'บาท' + (satang > 0 ? cv(satang) + 'สตางค์' : 'ถ้วน');")) {
    console.error('❌ negative test ratchet: สำเนาสูตรเดิมของ wht.html ไม่ถูกจับ'); process.exit(1);
  }
  if (offenders.length) {
    console.error(`❌ พบสูตรจำนวนเงินตัวอักษรนอกตัวตั้ง (${offenders.length}) — เรียก Helpers/ThaiBahtText.Format (เซิร์ฟเวอร์) หรือ UxHelpers.AmountInWords (พรีวิว) แทน:`);
    offenders.forEach(o => console.error('   ' + o));
    process.exit(1);
  }
  console.log('✅ ไม่มีสูตรจำนวนเงินตัวอักษรนอกตัวตั้ง (C# ThaiBahtText + พรีวิว ux-helpers.js)');
}
