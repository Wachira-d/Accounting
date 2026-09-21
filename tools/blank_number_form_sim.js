// ล็อกพฤติกรรม "ช่องตัวเลขที่เว้นว่าง" ด้วย **โค้ดจริง** (`_readNumberInto` ใน
// wwwroot/pages/lodging-settings.html) ไม่ใช่โค้ดที่เขียนซ้ำในเทสต์
//
// ที่มา: ผู้ใช้รายงาน 2026-09-21 (สร้างประเภทห้อง "Nordic Tent") — ช่อง "เตียงเสริมสูงสุด"
// ว่าง ⇒ ส่ง `maxExtraBeds: null` ⇒ System.Text.Json แปลงเข้า `int` ไม่ได้ ⇒ โยน body
// ทิ้งทั้งก้อน ⇒ "dto: The dto field is required.; $.maxExtraBeds: ...แปลงไม่ได้"
// และ **ไม่มีอะไรถูกบันทึกเลย**
//
// สามทิศที่ต้องล็อกพร้อมกัน (F2 ข้อ 8):
//   ✓ ว่าง + ไม่มี data-blank  → **ตัดคีย์ทิ้ง** (ปล่อยให้ค่า default ของ DTO ทำงาน)
//   ✓ ว่าง + data-blank="0"    → 0 (พฤติกรรมเดิมของช่องเงิน/% ต้องไม่เปลี่ยน)
//   ✓ มีค่า                     → ตัวเลขนั้น (รวม 0 ที่ผู้ใช้พิมพ์เอง — ห้ามกลายเป็นตัดทิ้ง)
'use strict';
const fs = require('fs');
const path = require('path');

const src = fs.readFileSync(
  path.join(__dirname, '..', 'Accounting', 'wwwroot', 'pages', 'lodging-settings.html'), 'utf8');

// ดึงเฉพาะเมธอด `_readNumberInto` ออกมาจากหน้าเว็บจริง — ถ้าวันหนึ่งเมธอดนี้ถูกลบ
// หรือเปลี่ยนชื่อ sim จะล้มทันที (ดีกว่าผ่านเงียบ ๆ กับสำเนาที่ค้างอยู่ในเทสต์)
const m = src.match(/_readNumberInto\(d, el\)\s*\{[\s\S]*?\n      \},/);
if (!m) {
  console.log('❌ หา _readNumberInto ใน lodging-settings.html ไม่เจอ — ถูกลบ/เปลี่ยนชื่อ?');
  process.exit(1);
}
// eslint-disable-next-line no-new-func
const readNumberInto = new Function('d', 'el', m[0]
  .replace(/^_readNumberInto\(d, el\)\s*\{/, '')
  .replace(/\},\s*$/, ''));

const el = (name, value, blank) => ({ name, value, dataset: blank === undefined ? {} : { blank } });

let fail = 0;
const check = (name, cond, got) => {
  if (cond) console.log(`  ✓ ${name}`);
  else { console.log(`  ✗ ${name} — ได้ ${JSON.stringify(got)}`); fail++; }
};

{
  console.log('ทิศที่ 1 — ว่าง + ไม่มี data-blank: ต้องตัดคีย์ทิ้ง ห้ามเป็น null');
  const d = {};
  readNumberInto(d, el('maxExtraBeds', ''));
  check('คีย์หายไปจาก payload', !('maxExtraBeds' in d), d);
  check('ไม่ใช่ null (null = พัง System.Text.Json)', d.maxExtraBeds !== null, d);

  // เคสสำคัญ: payload ถูก seed มาจากใบเดิม (readProp ทำแบบนั้น) — ต้อง delete จริง
  const seeded = { maxAdvanceDays: 365 };
  readNumberInto(seeded, el('maxAdvanceDays', ''));
  check('ค่าที่ seed มาจากใบเดิมถูกลบด้วย', !('maxAdvanceDays' in seeded), seeded);
}

{
  console.log('ทิศที่ 2 — ว่าง + data-blank="0": ต้องได้ 0 (พฤติกรรมเดิมห้ามเปลี่ยน)');
  const d = {};
  for (const n of ['depositPercent', 'weekendMultiplier', 'noShowChargePercent',
                   'depositMinAmount', 'serviceChargePercent', 'extraGuestPrice',
                   'earlyCheckInFee', 'lateCheckOutFee']) {
    readNumberInto(d, el(n, '', '0'));
  }
  check('ช่องเงิน/% ทั้ง 8 ได้ 0', Object.values(d).every(v => v === 0) && Object.keys(d).length === 8, d);
}

{
  console.log('ทิศที่ 3 — มีค่า: ต้องได้ตัวเลขนั้น รวม 0 ที่ผู้ใช้พิมพ์เอง');
  const d = {};
  readNumberInto(d, el('baseRate', '1000'));
  readNumberInto(d, el('sizeSqm', '9.5'));
  readNumberInto(d, el('sortOrder', '0'));            // 0 ที่พิมพ์เอง ≠ ว่าง
  readNumberInto(d, el('starRating', '0', '0'));
  check('ค่าปกติถูกแปลงเป็นตัวเลข', d.baseRate === 1000 && d.sizeSqm === 9.5, d);
  check('เลข 0 ที่ผู้ใช้พิมพ์เองต้องอยู่ ไม่ถูกตัดทิ้ง', d.sortOrder === 0 && 'sortOrder' in d, d);
  check('0 ที่พิมพ์เองในช่อง data-blank ก็ยังเป็น 0', d.starRating === 0, d);
}

{
  console.log('ทิศที่ 4 — ทั้งหน้าต้องไม่เหลือรูปแบบ "ว่าง → null" อีก');
  const bad = /type\s*===?\s*['"]number['"][^\n]*==?=\s*['"]['"]\s*\?\s*null/.test(src);
  check('ไม่มีตัวอ่านฟอร์มที่ส่ง null ให้ช่องตัวเลข', !bad);
  const lists = /\[\s*'minNights'\s*,\s*'maxNights'/.test(src);
  check('ลิสต์ชื่อฟิลด์ที่ฮาร์ดโค้ดถูกถอดแล้ว (สำเนาที่สองของกติกา DTO)', !lists);
}

console.log(fail === 0 ? '\n✅ blank_number_form_sim ผ่านทุกทิศ'
                       : `\n❌ blank_number_form_sim ล้ม ${fail} ข้อ`);
process.exit(fail === 0 ? 0 : 1);
