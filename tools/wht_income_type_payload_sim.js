#!/usr/bin/env node
// ═══ ล็อกพฤติกรรม: ช่อง "ประเภทเงินได้ (ม.40)" ในหน้ารีวิวสแกน ห้ามล้างรหัสที่เก็บไว้เงียบ ๆ (รอบ 200 ทีม K2 · ผลตรวจรอบ 189 C-08) ═══
//
// รัน: node tools/wht_income_type_payload_sim.js     (เทียบซอร์สอื่น: PAGE=/path/to/document-scan.html node tools/...)
//
// ที่มา — `loadWhtIncomeTypes` เก็บ [] (truthy) เมื่อโหลดตารางล้ม ⇒ แคชความล้มเหลวทั้ง session ⇒ dropdown มีแต่ "— ไม่ระบุ —" ·
// payload ส่ง `v('revWhtIncomeType') ?? ''` เสมอ ⇒ "" = "ผู้ใช้สั่งล้างค่า" (กติกาช่องแก้ไขของเรพ) ทั้งที่ไม่มีใครแตะ ⇒ รหัสที่ระบบ/ผู้ใช้
// เคยตั้งไว้หาย ⇒ DocumentLine.IncomeTypeCode = null ⇒ 50 ทวิ / ภ.ง.ด.3/53 ตกไปประเภท "8"
//
// ใช้ **โค้ดจริง** จาก document-scan.html (นิพจน์ของช่องนี้ใน _buildReviewCorrection + เมธอด _whtIncomeTypePayload + loadWhtIncomeTypes)
// ล็อกสองทิศ (CLAUDE.md §H): ทิศที่พัง = ตารางล้ม/ไม่แตะช่องว่าง ⇒ ไม่แตะ (null) · ทิศที่ต้องยังทำงาน = ผู้ใช้เลือก "— ไม่ระบุ —" เอง ⇒ ล้างได้ ("")
// และค่าที่เลือกไว้ส่งไปตามเดิม · negative test (F2 ข้อ 6): ใส่บั๊กเดิมกลับเข้าไปในซอร์ส แล้วต้องจับได้ทุกตัว
'use strict';
const fs = require('fs');
const path = require('path');

const PAGE = process.env.PAGE || path.join(__dirname, '..', 'Accounting', 'wwwroot', 'pages', 'document-scan.html');
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

/** รันชุดตรวจทั้งหมดบนซอร์สหนึ่งชุด — คืนรายการข้อที่ล้ม */
async function check(src) {
  const fails = [];
  const payloadBody = blockAfter(src, '_whtIncomeTypePayload(el) {');
  const loadBody = blockAfter(src, 'async loadWhtIncomeTypes() {');
  const exprMatch = src.match(/\n\s*whtIncomeTypeCode:\s*([^\n]+?),\s*\n/);
  if (!loadBody) return ['ไม่พบ loadWhtIncomeTypes ในหน้า'];
  if (!exprMatch) return ['ไม่พบช่อง whtIncomeTypeCode ใน _buildReviewCorrection'];

  const page = {};
  page._whtIncomeTypePayload = payloadBody ? new Function('el', payloadBody) : undefined;
  const mkEl = (value, touched) => ({ value, dataset: touched ? { userTouched: '1' } : {} });
  // นิพจน์จริงของช่องนี้ใน payload builder (v = ตัวอ่านค่าของหน้า: `value || null`)
  const evalField = (el) => {
    const doc = { getElementById: (id) => (id === 'revWhtIncomeType' ? el : null) };
    const v = (id) => doc.getElementById(id)?.value || null;
    return new Function('v', 'document', 'return (' + exprMatch[1] + ');').call(page, v, doc);
  };
  const expect = (name, got, want) => { if (got !== want) fails.push(`${name}: ได้ ${JSON.stringify(got)} ต้องได้ ${JSON.stringify(want)}`); };

  try {
    // ทิศที่พัง: ตารางโหลดไม่ได้ (มีแต่ "— ไม่ระบุ —") และผู้ใช้ไม่แตะ ⇒ ไม่แตะรหัสที่เก็บไว้
    expect('ตารางล้ม/ช่องว่าง ไม่แตะ ⇒ null (ไม่ล้าง)', evalField(mkEl('', false)), null);
    // รหัสเดิมไม่อยู่ในตัวเลือก (select ว่าง) และไม่แตะ ⇒ null
    expect('รหัสเดิมไม่อยู่ในตัวเลือก ไม่แตะ ⇒ null', evalField(mkEl('', false)), null);
    // ทิศที่ต้องยังทำงาน
    expect('ค่าที่เลือกไว้ส่งตามเดิม', evalField(mkEl('40(8)', false)), '40(8)');
    expect('ผู้ใช้เลือก "— ไม่ระบุ —" เอง ⇒ "" (ล้างค่า)', evalField(mkEl('', true)), '');
    expect('ไม่มีช่องบนหน้า ⇒ null', evalField(null), null);
  } catch (e) {
    fails.push('นิพจน์ของช่องรันไม่ได้: ' + e.message);
  }

  // loadWhtIncomeTypes: ล้มครั้งแรกห้ามแคช · ครั้งถัดไปโหลดใหม่ได้
  try {
    let calls = 0;
    const API = { get: async () => { calls++; if (calls === 1) throw new Error('network'); return { data: [{ code: '40(8)', name: 'x', taxSection: '40(8)' }] }; } };
    const obj = { _whtIncomeTypes: null };
    const load = new Function('API', 'return (async function() {' + loadBody + '}).call(this);');
    const first = await load.call(obj, API);
    expect('ครั้งแรกล้ม คืนรายการว่าง', Array.isArray(first) && first.length, 0);
    expect('ครั้งแรกล้ม ห้ามแคช (ยังเป็น null)', obj._whtIncomeTypes, null);
    const second = await load.call(obj, API);
    expect('ครั้งถัดไปโหลดใหม่ได้', Array.isArray(second) ? second.length : -1, 1);
  } catch (e) {
    fails.push('loadWhtIncomeTypes รันไม่ได้: ' + e.message);
  }
  return fails;
}

(async () => {
  let bad = 0;
  const real = await check(SRC);
  if (real.length) { bad++; console.log('❌ ซอร์สจริง:\n  ' + real.join('\n  ')); }
  else console.log('✅ ซอร์สจริง: ตารางล้ม/ไม่แตะ ⇒ ไม่ล้าง · เลือก "ไม่ระบุ" เอง ⇒ ล้าง · ค่าเดิมส่งตามเดิม · ความล้มเหลวไม่ถูกแคช');

  // negative test — ใส่บั๊กเดิมกลับเข้าไปแล้วต้องจับได้
  const mutants = [
    ['payload ส่ง `?? \'\'` เสมอ (บั๊กเดิม)',
      SRC.replace(/whtIncomeTypeCode:\s*this\._whtIncomeTypePayload\(document\.getElementById\('revWhtIncomeType'\)\),/,
        "whtIncomeTypeCode: v('revWhtIncomeType') ?? '',")],
    ['แคชความล้มเหลว (catch เก็บ [])',
      SRC.replace(/this\._whtIncomeTypes = null;\s*\n\s*return \[\];/, 'this._whtIncomeTypes = [];')],
    ['ตัวช่วยคืน "" เมื่อไม่แตะ', SRC.replace("return el.dataset && el.dataset.userTouched === '1' ? '' : null;", "return '';")],
  ];
  for (const [name, src] of mutants) {
    if (src === SRC) { bad++; console.log(`❌ negative test "${name}": ใส่บั๊กไม่ได้ (ซอร์สเปลี่ยนรูป — แก้ sim)`); continue; }
    const f = await check(src);
    if (f.length === 0) { bad++; console.log(`❌ negative test "${name}": ใส่บั๊กแล้วไม่ถูกจับ`); }
    else console.log(`✅ negative test "${name}": จับได้ (${f.length} ข้อ)`);
  }
  process.exit(bad ? 1 : 0);
})();
