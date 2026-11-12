// รอบ 266 ฝ่ายค้าน H1 — ล็อก "ใบต้นทางของใบเสร็จใน payload ตอนบันทึก" ด้วย **โค้ดจริง** (`_receiptSourcePayload` ใน
// wwwroot/pages/documents.html) ไม่ใช่สำเนาในเทสต์
//
// ที่มา: ฟอร์มแก้ไขส่ง `relatedDocumentId: this._receiptSourceId || Guid.Empty` ทุกครั้ง · `_receiptSourceId` เป็น null ระหว่างลิสต์ใบค้างยังโหลด
// (หรือส่วน "รับชำระใบค้าง" ถูกซ่อน/ยังไม่เลือกผู้ติดต่อ) ⇒ เซิร์ฟเวอร์ได้ Guid.Empty = "ล้าง" ⇒ ใบเสร็จร่างที่แปลงจากใบแจ้งหนี้ถูกถอดใบต้นทางเงียบ ๆ
// แล้วอนุมัติเป็นขายสด (รายได้/VAT เบิ้ล · ลูกหนี้ไม่ถูกตัด) — "ยังไม่ได้ตรวจ" ต้องไม่เท่ากับ "ล้าง" (F2 ข้อ 5)
//
// สองทิศ (F2 ข้อ 8):
//   ✓ ใบร่างที่แปลงมา เปิดแก้แล้วบันทึกโดยไม่แตะช่องใบต้นทาง (ทุกสภาพ: ยังไม่ hydrate · hydrate แล้ว) → **ไม่ส่งคีย์** (ใบต้นทางคงเดิม)
//   ✓ ผู้ใช้เลือก "ใบเสร็จอิสระ" เอง → ส่ง Guid.Empty (ล้างได้จริง) · เลือกใบอื่น → ส่งใบนั้น · เลือกกลับใบเดิม → ไม่ส่ง
//   ✓ ใบที่เซิร์ฟเวอร์ไม่ให้แก้ → ไม่ส่งไม่ว่ากรณีใด · สร้างใหม่ → ส่งเฉพาะเมื่อเลือกใบ (พฤติกรรมเดิม)
'use strict';
const fs = require('fs');
const path = require('path');

const src = fs.readFileSync(path.join(__dirname, '..', 'Accounting', 'wwwroot', 'pages', 'documents.html'), 'utf8');
const m = src.match(/_receiptSourcePayload\(st\)\s*\{[\s\S]*?\n      \},/);
if (!m) {
  console.log('❌ หา _receiptSourcePayload ใน documents.html ไม่เจอ — ถูกลบ/เปลี่ยนชื่อ?');
  process.exit(1);
}
// eslint-disable-next-line no-new-func
const payload = new Function('st', m[0].replace(/^_receiptSourcePayload\(st\)\s*\{/, '').replace(/\},\s*$/, ''));

// จุดเรียกในตัวประกอบ payload ต้องใช้ตัวนี้ (ไม่ประกอบ relatedDocumentId ของใบเสร็จเอง) และต้องส่งธง hydrated/userTouched
if (!/this\._receiptSourcePayload\(\{[\s\S]{0,400}hydrated:[\s\S]{0,200}userTouched:/.test(src)) {
  console.log('❌ ตัวประกอบ payload ไม่ได้เรียก _receiptSourcePayload พร้อมธง hydrated/userTouched');
  process.exit(1);
}
if (/relatedDocumentId:\s*this\._receiptSourceId\s*\|\|/.test(src)) {
  console.log('❌ ยังมีรูป "relatedDocumentId: this._receiptSourceId || …" (ส่ง Guid.Empty เมื่อค่ายังไม่ถูกเติม) — รูปของบั๊ก H1');
  process.exit(1);
}

const EMPTY = '00000000-0000-0000-0000-000000000000';
const INV = 'inv-1', OTHER = 'inv-2';
let fail = 0;
const check = (name, got, want) => {
  const ok = JSON.stringify(got) === JSON.stringify(want);
  console.log(`  ${ok ? '✓' : '✗'} ${name}${ok ? '' : ' — ได้ ' + JSON.stringify(got) + ' คาด ' + JSON.stringify(want)}`);
  if (!ok) fail++;
};
const edit = (o) => Object.assign({ editing: true, editable: true, hydrated: true, userTouched: false, selectedId: INV, pinnedId: INV }, o);

console.log('ใบเสร็จร่างที่แปลงจากใบแจ้งหนี้ — เปิดแก้แล้วบันทึก (ไม่แตะช่องใบต้นทาง)');
check('ลิสต์ยังโหลดไม่เสร็จ (selectedId ยัง null) → ไม่ส่ง', payload(edit({ hydrated: false, selectedId: null })), {});
check('ส่วนรับชำระถูกซ่อน/ไม่มีผู้ติดต่อ (ไม่เคย hydrate) → ไม่ส่ง', payload(edit({ hydrated: false, selectedId: null, userTouched: false })), {});
check('hydrate แล้ว ไม่แตะ → ไม่ส่ง (ใบต้นทางคงเดิม)', payload(edit({})), {});
check('hydrate แล้ว แต่ค่าถูกล้างด้วยโค้ด (ไม่ใช่ผู้ใช้) → ไม่ส่ง', payload(edit({ selectedId: null })), {});

console.log('ทิศตรงข้าม — ผู้ใช้ตั้งใจเปลี่ยน');
check('ผู้ใช้เลือก "ใบเสร็จอิสระ" → ล้าง (Guid.Empty)', payload(edit({ userTouched: true, selectedId: null })), { relatedDocumentId: EMPTY });
check('ผู้ใช้เลือกใบอื่น → ส่งใบนั้น', payload(edit({ userTouched: true, selectedId: OTHER })), { relatedDocumentId: OTHER });
check('ผู้ใช้เลือกแล้วกลับมาใบเดิม → ไม่ส่ง', payload(edit({ userTouched: true, selectedId: INV })), {});
check('ใบร่างที่ยังไม่ผูก ผู้ใช้เลือกใบ → ส่ง', payload(edit({ userTouched: true, selectedId: INV, pinnedId: null })), { relatedDocumentId: INV });
check('ใบร่างที่ยังไม่ผูก ไม่แตะ → ไม่ส่ง (ไม่ใช่ Guid.Empty)', payload(edit({ selectedId: null, pinnedId: null })), {});

console.log('ใบที่เซิร์ฟเวอร์ไม่ให้แก้ · สร้างใหม่');
check('canChangeReceiptSource=false แม้ผู้ใช้แตะ → ไม่ส่ง', payload(edit({ editable: false, userTouched: true, selectedId: null })), {});
check('สร้างใหม่ เลือกใบ → ส่ง', payload({ editing: false, selectedId: INV }), { relatedDocumentId: INV });
check('สร้างใหม่ ไม่เลือก → ไม่ส่ง', payload({ editing: false, selectedId: null }), {});

if (fail) { console.log(`❌ receipt_source_payload_sim: ${fail} ข้อไม่ผ่าน`); process.exit(1); }
console.log('✅ receipt_source_payload_sim ผ่าน');
