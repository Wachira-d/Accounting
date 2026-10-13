#!/usr/bin/env node
// ═══ ล็อกพฤติกรรม: ฟอร์มเอกสาร (documents.html) — ประเภทเงินได้ที่ "ไม่แตะ" ตอนแก้ไขใบเดิม ไม่ถูกส่งกลับ (ทีม A กระดานตัดสิน 2026-10-13 · A11) ═══
//
// รัน: node tools/doc_income_type_hydrated_sim.js     (เทียบซอร์สอื่น: PAGE=/path/to/documents.html node tools/...)
//
// ที่มา — รหัสประเภทเงินได้เก่าที่ตารางกลางชี้ขาดไม่ได้ (ข้อมูลก่อนมีด่าน/OCR เก็บรูปเดิม) ถูกโหลดเป็นตัวเลือกชั่วคราว แล้ว payload ส่งกลับทุกครั้ง ⇒
// เซิร์ฟเวอร์ (ด่านรอบ 203) ปฏิเสธ 400 ⇒ "เปิดแก้แล้วบันทึกไม่ได้" ทั้งที่ผู้ใช้ไม่ได้แตะช่องนี้ · แก้สองชั้น: เซิร์ฟเวอร์รับรหัสที่ตรงตัวกับที่เก็บบนใบนี้
// (DocumentLineIncomeType.Decide · เทสต์ C#) + หน้านี้ไม่ส่งช่องที่ไม่แตะ **เฉพาะเมื่อ** ลำดับ + คำอธิบาย + รหัส เท่าตอนโหลด
// (= เงื่อนไขเดียวกับที่เซิร์ฟเวอร์ใช้คงค่าเดิม KeptFromPrevious) — ลำดับ/คำอธิบายเปลี่ยน ⇒ ส่งค่าเดิม (ไม่หายเงียบเพราะจับคู่บรรทัดไม่ได้)
//
// ใช้ **โค้ดจริง** (_lineIncomeTypePayload · _markHydratedIncomeType · จุดเรียกใน payload builder และ _hydrateLineRow) · ล็อกสองทิศ ·
// negative test (F2 ข้อ 6): ใส่บั๊กกลับเข้าไปในซอร์สแล้วต้องจับได้ทุกตัว
'use strict';
const fs = require('fs');
const path = require('path');

const PAGE = process.env.PAGE || path.join(__dirname, '..', 'Accounting', 'wwwroot', 'pages', 'documents.html');
const SRC = fs.readFileSync(PAGE, 'utf8');

function blockAfter(src, anchor) {
  const i = src.indexOf(anchor);
  if (i < 0) return null;
  const open = src.indexOf('{', i + anchor.length - 1);
  let depth = 0;
  for (let k = open; k < src.length; k++) {
    const c = src[k];
    if (c === '{') depth++;
    else if (c === '}') { depth--; if (depth === 0) return src.slice(open + 1, k); }
  }
  return null;
}

/** แถวปลอม: select ประเภทเงินได้ + ช่องคำอธิบาย */
function mkRow(code, desc) {
  const sel = { value: code, dataset: {} };
  const descEl = { value: desc };
  return {
    sel, descEl,
    querySelector: (q) => (q === '[data-f="incomeType"]' ? sel : q === '[data-f="desc"]' ? descEl : null),
  };
}

function check(src) {
  const fails = [];
  const payloadBody = blockAfter(src, '_lineIncomeTypePayload(row, payloadIdx) {');
  const markBody = blockAfter(src, '_markHydratedIncomeType(row) {');
  const hydrateBody = blockAfter(src, '_hydrateLineRow(row, l, opts = {}) {');
  if (!payloadBody) return ['ไม่พบ _lineIncomeTypePayload(row, payloadIdx) ในหน้า'];
  if (!markBody) return ['ไม่พบ _markHydratedIncomeType(row) ในหน้า'];
  if (!hydrateBody) return ['ไม่พบ _hydrateLineRow ในหน้า'];
  // จุดเรียก (มี ≠ ถูกเรียก): payload builder ส่งลำดับใน payload · ตัวเติมแถวจำค่าเฉพาะตอนแก้ไขใบเดิม
  if (!/this\._lineIncomeTypePayload\(row,\s*lines\.length\)/.test(src))
    fails.push('payload builder ไม่ส่งลำดับบรรทัด (lines.length) ให้ _lineIncomeTypePayload — กติกา A11 ไม่มีผล');
  if (!/if\s*\(opts\.carryStoredVat\s*!==\s*false\)\s*this\._markHydratedIncomeType\(row\)/.test(hydrateBody))
    fails.push('_hydrateLineRow ไม่จำค่าที่โหลด (หรือจำทุกทางเข้า รวมใบลดหนี้ที่เติมจากใบต้นทาง)');

  let rows = [];
  const document = { querySelectorAll: (q) => (q === '#linesBody .line-card' ? rows : []) };
  const payload = new Function('document', 'row', 'payloadIdx', payloadBody);
  const mark = new Function('document', 'row', markBody);
  const expect = (name, got, want) => { if (got !== want) fails.push(`${name}: ได้ ${JSON.stringify(got)} ต้องได้ ${JSON.stringify(want)}`); };

  try {
    const LEG = 'LEGACY-ค่าบริการเก่า';
    const hydrate = (list) => { rows = list.map(([c, d]) => mkRow(c, d)); rows.forEach(r => mark(document, r)); return rows; };

    // ทิศที่พัง: ไม่แตะ + ลำดับ/คำอธิบาย/รหัสเท่าตอนโหลด ⇒ ไม่ส่ง (เซิร์ฟเวอร์คงค่าเดิม — รหัสเก่าไม่ทำให้ 400)
    let [r0, r1] = hydrate([[LEG, 'ค่าบริการ'], ['8', 'ค่าขนส่ง']]);
    expect('ไม่แตะ ตรงตอนโหลด (รหัสเก่า) ⇒ null', payload(document, r0, 0), null);
    expect('ไม่แตะ ตรงตอนโหลด (รหัสปกติ) ⇒ null', payload(document, r1, 1), null);

    // ทิศที่ต้องยังทำงาน: แก้คำอธิบาย/ลำดับเลื่อน ⇒ ส่งค่าเดิม (เซิร์ฟเวอร์รับรหัสที่ตรงตัวกับที่เก็บบนใบนี้ — ไม่หายเงียบ)
    r0.descEl.value = 'ค่าบริการ (แก้แล้ว)';
    expect('แก้คำอธิบาย ⇒ ส่งรหัสเดิม', payload(document, r0, 0), LEG);
    r0.descEl.value = 'ค่าบริการ';
    expect('ลำดับเลื่อน (แทรกแถวข้างบน) ⇒ ส่งรหัสเดิม', payload(document, r0, 1), LEG);
    // ผู้ใช้เลือกเอง ⇒ ส่งค่าที่เลือก · เลือก "ไม่ระบุ" เอง ⇒ ""
    r1.sel.value = '5'; r1.sel.dataset.userTouched = '1';
    expect('ผู้ใช้เลือกใหม่ ⇒ ส่งค่าใหม่', payload(document, r1, 1), '5');
    r1.sel.value = '';
    expect('ผู้ใช้เลือก "ไม่ระบุ" เอง ⇒ ""', payload(document, r1, 1), '');
    // ผู้ใช้เลือกกลับเป็นค่าเดิมเอง ⇒ ส่ง (ไม่ใช่ "ไม่แตะ")
    r0.sel.dataset.userTouched = '1';
    expect('ผู้ใช้แตะแล้วเลือกค่าเดิม ⇒ ส่ง', payload(document, r0, 0), LEG);
    // ค่าเปลี่ยนจากตัวเติมอื่น (ไม่ใช่ผู้ใช้) ⇒ ส่งค่าใหม่
    [r0] = hydrate([['8', 'ค่าบริการ']]);
    r0.sel.value = '8ad';
    expect('ค่าเปลี่ยนโดยตัวเติมอื่น ⇒ ส่ง', payload(document, r0, 0), '8ad');
    // แถวใหม่ (ไม่ได้โหลดจากใบเดิม) — พฤติกรรมเดิม
    const fresh = mkRow('8', 'ใหม่');
    expect('แถวใหม่มีค่า ⇒ ส่ง', payload(document, fresh, 0), '8');
    const freshEmpty = mkRow('', 'ใหม่');
    expect('แถวใหม่ว่าง ไม่แตะ ⇒ null', payload(document, freshEmpty, 0), null);
    // ผู้เรียกที่ไม่ส่งลำดับ ⇒ ไม่ใช้กติกา A11 (ส่งค่าเหมือนเดิม)
    [r0] = hydrate([[LEG, 'ค่าบริการ']]);
    expect('ไม่ส่ง payloadIdx ⇒ ส่งค่า (กติกาเดิม)', payload(document, r0, undefined), LEG);
  } catch (e) {
    fails.push('รันโค้ดจริงไม่ได้: ' + e.message);
  }
  return fails;
}

const real = check(SRC);
if (real.length) {
  console.error('❌ ซอร์สจริง:\n  ' + real.join('\n  '));
  process.exit(1);
}
console.log('✅ ซอร์สจริง: ไม่แตะ+ตรงตอนโหลด ⇒ ไม่ส่ง · แก้คำอธิบาย/ลำดับเลื่อน ⇒ ส่งรหัสเดิม · ผู้ใช้เลือก/ล้างเอง ⇒ ส่ง · แถวใหม่เดิม');

// negative tests — ใส่บั๊กกลับเข้าไปในซอร์ส แล้วต้องจับได้
const mutants = [
  ['ไม่มีกติกา A11 (ส่งรหัสที่ไม่แตะกลับเสมอ ⇒ 400 กับรหัสเก่า)',
    (s) => s.replace("if (sel.dataset.userTouched !== '1' && sel.dataset.hydratedCode !== undefined", "if (false && sel.dataset.userTouched !== '1' && sel.dataset.hydratedCode !== undefined")],
  ['ไม่ดูคำอธิบาย (แก้คำอธิบายแล้วรหัสหายเงียบ)',
    (s) => s.replace("&& (row.querySelector('[data-f=\"desc\"]')?.value || '').trim() === sel.dataset.hydratedDesc) return null;", ') return null;')],
  ['ไม่ดูลำดับ (แทรกแถวแล้วรหัสหายเงียบ)',
    (s) => s.replace('&& String(payloadIdx) === sel.dataset.hydratedIdx', '')],
  ['ไม่ดูว่าผู้ใช้แตะ (เลือกค่าเดิมเองแล้วไม่ถูกส่ง)',
    (s) => s.replace("if (sel.dataset.userTouched !== '1' && sel.dataset.hydratedCode !== undefined", 'if (sel.dataset.hydratedCode !== undefined')],
  ['payload builder ไม่ส่งลำดับ',
    (s) => s.replace('this._lineIncomeTypePayload(row, lines.length)', 'this._lineIncomeTypePayload(row)')],
  ['จำค่าทุกทางเข้า (ใบลดหนี้ที่เติมจากใบต้นทางไม่ส่งรหัส)',
    (s) => s.replace('if (opts.carryStoredVat !== false) this._markHydratedIncomeType(row);', 'this._markHydratedIncomeType(row);')],
];
let bad = 0;
for (const [name, mutate] of mutants) {
  const mutated = mutate(SRC);
  if (mutated === SRC) { console.error(`❌ negative test "${name}": mutation ไม่ติด (ซอร์สเปลี่ยนรูป — ปรับ sim)`); bad++; continue; }
  const f = check(mutated);
  if (f.length === 0) { console.error(`❌ negative test "${name}": ไม่ถูกจับ`); bad++; }
  else console.log(`✅ negative test "${name}": จับได้ (${f.length} ข้อ)`);
}
process.exit(bad ? 1 : 0);
