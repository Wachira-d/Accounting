// ล็อกสัญญา "ฟอร์มพนักงาน ↔ API" ด้วย **ซอร์สจริง** ของทั้งสองหน้า + DTO จริง
// (Accounting/Models/DTOs/Payroll/PayrollDtos.cs) — ไม่ใช่สำเนาในเทสต์
//
// ที่มา (A05 · D-07 · D-01 — P0 รอบ 189 · แก้รอบ 193):
//   • payroll.html เปิด "แก้ไขพนักงาน" → ช่องชื่อ/เลขบัตร/วันเริ่มงาน/รหัส/คำนำหน้ามีค่าเดิม
//     → กดบันทึก → toast "แก้ไขสำเร็จ" → เปิดใหม่ ค่าเดิมกลับมาทุกช่อง (UpdateEmployeeRequest
//     ไม่มีช่องเหล่านั้น — System.Text.Json ทิ้งคีย์ที่ไม่รู้จักเงียบ ๆ)
//   • employees.html hydrate 16 ช่อง แต่ส่งตอน update อีก 15 ช่องคนละชุด · ช่องธนาคาร/ธง ปกส.
//     ไม่ hydrate ⇒ ค้างค่าของคนก่อน แล้วถูกส่งไปทับอีกคน (เงินเดือนเข้าบัญชีผิดคน)
//   • ช่อง fTaxId มีบนฟอร์ม แต่ไม่มีใน DTO ทั้ง 3 ตัว ⇒ 50 ทวิ ภ.ง.ด.1 ไม่เคยออก
//
// สี่สัญญาที่ต้องจริงพร้อมกัน (ต่อหน้า):
//   1. ทุกช่องที่ hydrate ตอนเปิดแก้ไข → ถูกอ่านเข้า payload ตอน update  (ไม่งั้น = ช่องที่แก้แล้วเงียบ)
//   2. ทุกช่องที่ payload update อ่าน → ถูก hydrate                     (ไม่งั้น = ค้างค่าของคนก่อน)
//   3. ทุกคีย์ใน payload update อยู่ใน UpdateEmployeeRequest            (ไม่งั้น = เซิร์ฟเวอร์ทิ้งเงียบ)
//      และทุกคีย์ใน payload create อยู่ใน CreateEmployeeRequest
//   4. ทุก hydrate ที่อ่าน `e.xxx` มีอย่างน้อยหนึ่งคีย์ที่ EmployeeResponse ส่งจริง
//                                                                      (ไม่งั้น = ช่องว่างตลอดกาล)
//   5. ทุกช่องกรอกในโมดัลพนักงาน ถูก hydrate ตอนเปิดแก้ไข               (ไม่งั้น = ช่องที่โชว์ค่าคนก่อน
//      หรือโชว์ว่างทั้งที่มีค่า — fTaxId/fSso เคยเป็นแบบนี้)
// baseline (F2 ข้อ 6 — จาก `git show 58203b2:<ไฟล์>` ไม่ใช่ความจำ): ทิศที่ 1 ของซอร์สก่อนแก้ล้ม 29 ข้อ ครอบ
// A05 (payroll.html ส่ง 6 คีย์ที่ Update DTO ไม่มี) · D-07 (ธนาคาร/ธง ปกส./ชื่อ ไม่ hydrate/ไม่ส่ง) · D-01
// และ negative test (F2 ข้อ 6): ใส่บั๊กกลับเข้าไปในซอร์ส (ตัดคีย์ taxId ออกจาก payload ·
// ตัด TaxId ออกจาก Update DTO · ตัดการ hydrate ช่องธนาคาร) แล้วต้องจับได้ทุกตัว
'use strict';
const fs = require('fs');
const path = require('path');

const ROOT = path.join(__dirname, '..');
const read = (p) => fs.readFileSync(path.join(ROOT, p), 'utf8');

// ───────── ตัวช่วยแยกส่วนซอร์ส ─────────
/** ตัดบล็อก `{ ... }` ที่เริ่มหลัง anchor — นับวงเล็บแบบข้าม string/template พื้นฐาน */
function blockAfter(src, anchor) {
  const i = src.indexOf(anchor);
  if (i < 0) return null;
  const open = src.indexOf('{', i + anchor.length - 1);
  let depth = 0, q = null;
  for (let k = open; k < src.length; k++) {
    const c = src[k];
    if (q) {
      if (c === '\\') { k++; continue; }
      if (c === q) q = null;
      continue;
    }
    if (c === '"' || c === "'" || c === '`') { q = c; continue; }
    if (c === '/' && src[k + 1] === '/') { k = src.indexOf('\n', k); if (k < 0) break; continue; }
    if (c === '{') depth++;
    else if (c === '}') { depth--; if (depth === 0) return src.slice(open, k + 1); }
  }
  return null;
}

/** พารามิเตอร์ของ C# positional record → ชุดชื่อแบบ camelCase (ตามที่ System.Text.Json ใช้) */
function recordParams(cs, name) {
  const i = cs.indexOf(`public record ${name}(`);
  if (i < 0) return null;
  const end = cs.indexOf(');', i);
  const body = cs.slice(i, end)
    .split('\n').map(l => l.replace(/\/\/.*$/, '')).join('\n');
  const out = new Set();
  const re = /\b(?:string|bool|int|decimal|Guid|DateTime|List<[^>]+>)\??\s+([A-Z]\w*)\s*(?:=|,|$)/gm;
  let m;
  while ((m = re.exec(body))) out.add(m[1][0].toLowerCase() + m[1].slice(1));
  return out;
}

/** id ที่ถูก "เขียนค่า" ในบล็อก hydrate + ที่ helper กลางเขียนให้ */
function hydratedIds(block, helpers) {
  const ids = new Set();
  const re = /getElementById\('(\w+)'\)\.(?:value|checked)\s*=(?!=)/g;
  let m;
  while ((m = re.exec(block))) ids.add(m[1]);
  const re2 = /setTitleSelectValue\('(\w+)'/g;
  while ((m = re2.exec(block))) ids.add(m[1]);
  for (const [call, hIds] of helpers) if (block.includes(call)) hIds.forEach(x => ids.add(x));
  return ids;
}

/** id ที่ถูก "อ่าน" เข้า payload */
function payloadIds(block, readers, helpers) {
  const ids = new Set();
  let m;
  const re = /getElementById\('(\w+)'\)(?:\.value|\.checked)/g;
  while ((m = re.exec(block))) {
    // ข้ามบรรทัดที่เป็นการ "เขียน" (hydrate) ไม่ใช่การอ่าน
    const after = block.slice(m.index + m[0].length, m.index + m[0].length + 4);
    if (/^\s*=(?!=)/.test(after)) continue;
    ids.add(m[1]);
  }
  for (const fn of readers) {
    const r = new RegExp(`\\b${fn}\\('(\\w+)'\\)`, 'g');
    while ((m = r.exec(block))) ids.add(m[1]);
  }
  const reEl = /(\w+)\s*=\s*document\.getElementById\('(\w+)'\)/g;   // const startEl = ...
  while ((m = reEl.exec(block))) ids.add(m[2]);
  for (const [call, hIds] of helpers) if (block.includes(call)) hIds.forEach(x => ids.add(x));
  return ids;
}

/** คีย์ของ object literal ที่ประกาศด้วย `const <name> = {` + ส่วนที่เติมภายหลัง `<name>.key =` */
function payloadKeys(block, name, helperKeys) {
  const obj = blockAfter(block, `const ${name} = {`);
  if (!obj) return null;
  const keys = new Set();
  // เฉพาะคีย์ระดับบนสุดของ object (ความลึก 1)
  let depth = 0;
  for (const line of obj.split('\n')) {
    const m = depth === 1 && line.match(/^\s*(\w+)\s*:/);
    if (m) keys.add(m[1]);
    for (const c of line.replace(/'[^']*'|"[^"]*"|`[^`]*`/g, '')) {
      if (c === '{' || c === '(' || c === '[') depth++;
      else if (c === '}' || c === ')' || c === ']') depth--;
    }
  }
  const re = new RegExp(`\\b${name}\\.(\\w+)\\s*=(?!=)`, 'g');
  let m;
  while ((m = re.exec(block))) keys.add(m[1]);
  for (const [call, ks] of helperKeys) if (obj.includes(call)) ks.forEach(k => keys.add(k));
  return keys;
}

/** คีย์ `e.xxx` ต่อบรรทัด hydrate (แต่ละบรรทัดต้องมีอย่างน้อยหนึ่งคีย์ที่ response มี) */
function hydrateResponseKeys(block) {
  const rows = [];
  for (const line of block.split('\n')) {
    if (!/getElementById\('\w+'\)\.(?:value|checked)\s*=(?!=)|setTitleSelectValue\(/.test(line)) continue;
    const keys = [...line.matchAll(/\be\.(\w+)/g)].map(x => x[1]);
    if (keys.length) rows.push({ line: line.trim(), keys });
  }
  return rows;
}

/** id ของ input/select/textarea ในโมดัล (ตั้งแต่ id ของโมดัลถึงโมดัลถัดไป) */
function modalInputIds(src, modalId) {
  const i = src.indexOf(`id="${modalId}"`);
  if (i < 0) return null;
  const j = src.indexOf('class="modal-overlay"', i + 1);
  const html = src.slice(i, j < 0 ? undefined : j);
  return new Set([...html.matchAll(/<(?:input|select|textarea)\b[^>]*\bid="(\w+)"/g)].map(m => m[1]));
}

// ───────── ตรวจหนึ่งหน้า ─────────
function checkPage(cfg, dto) {
  const errs = [];
  const hyd = blockAfter(cfg.src, cfg.hydrateAnchor);
  const save = blockAfter(cfg.src, cfg.saveAnchor);
  if (!hyd) return [`หา ${cfg.hydrateAnchor} ไม่เจอ — ถูกลบ/เปลี่ยนชื่อ?`];
  if (!save) return [`หา ${cfg.saveAnchor} ไม่เจอ — ถูกลบ/เปลี่ยนชื่อ?`];
  const upd = cfg.updateSlice(save);
  if (!upd) return ['หาส่วน update ใน save ไม่เจอ'];

  const hIds = hydratedIds(hyd, cfg.hydrateHelpers);
  const pIds = payloadIds(upd, cfg.readers, cfg.readHelpers);
  for (const x of cfg.ignoreIds) { hIds.delete(x); pIds.delete(x); }
  for (const id of hIds) if (!pIds.has(id))
    errs.push(`[1] ${id} ถูก hydrate ตอนเปิดแก้ไข แต่ payload update ไม่อ่าน ⇒ แก้แล้วกดบันทึก = เงียบ`);
  for (const id of pIds) if (!hIds.has(id))
    errs.push(`[2] ${id} ถูกส่งตอน update แต่ไม่ hydrate ⇒ ค้างค่าของพนักงานคนก่อนแล้วส่งไปทับ`);

  const uKeys = payloadKeys(upd, cfg.payloadVar, cfg.helperKeys);
  if (!uKeys) errs.push(`หา const ${cfg.payloadVar} = { ในส่วน update ไม่เจอ`);
  else for (const k of uKeys) if (!dto.update.has(k))
    errs.push(`[3] คีย์ "${k}" ส่งตอน update แต่ UpdateEmployeeRequest ไม่มี ⇒ เซิร์ฟเวอร์ทิ้งเงียบ`);

  const cSlice = cfg.createSlice(save);
  const cKeys = cSlice && payloadKeys(cSlice, cfg.payloadVar, cfg.helperKeys);
  if (!cKeys) errs.push(`หา payload create ไม่เจอ`);
  else for (const k of cKeys) if (!dto.create.has(k) && !cfg.createOnlyUpdateKeys.includes(k))
    errs.push(`[3] คีย์ "${k}" ส่งตอน create แต่ CreateEmployeeRequest ไม่มี ⇒ เซิร์ฟเวอร์ทิ้งเงียบ`);

  const inputs = modalInputIds(cfg.src, cfg.modalId);
  if (!inputs || inputs.size === 0) errs.push(`หาโมดัล ${cfg.modalId} ไม่เจอ`);
  else for (const id of inputs)
    if (!hIds.has(id) && !cfg.ignoreIds.includes(id))
      errs.push(`[5] ช่อง ${id} อยู่บนฟอร์มแต่ไม่ถูก hydrate ตอนเปิดแก้ไข ⇒ โชว์ค่าของคนก่อน/ว่างทั้งที่มีค่า`);

  for (const row of hydrateResponseKeys(hyd))
    if (!row.keys.some(k => dto.response.has(k)))
      errs.push(`[4] hydrate อ่าน ${row.keys.map(k => 'e.' + k).join('/')} ที่ EmployeeResponse ไม่ส่ง ⇒ ช่องว่างตลอด: ${row.line}`);
  return errs;
}

function loadDto(cs) {
  return {
    create: recordParams(cs, 'CreateEmployeeRequest'),
    update: recordParams(cs, 'UpdateEmployeeRequest'),
    response: recordParams(cs, 'EmployeeResponse'),
  };
}

const allowanceIds = ['fAllowSpouse', 'fAllowChildren', 'fAllowChildren2561', 'fAllowParents',
  'fAllowLifeIns', 'fAllowRmf', 'fAllowDonation', 'fAllowLegacy'];
const allowanceKeys = ['hasSpouseAllowance', 'childAllowanceCount', 'secondAndLaterChildren',
  'parentAllowanceCount', 'lifeInsurancePremium', 'rmfSsfContribution', 'donationAmount', 'taxAllowances'];

function pages(empSrc, paySrc) {
  return [
    {
      name: 'employees.html',
      src: empSrc,
      modalId: 'formModal',
      hydrateAnchor: 'async openEdit(id) {',
      saveAnchor: 'async save() {',
      updateSlice: (s) => { const i = s.indexOf('// Update'); return i < 0 ? null : s.slice(i); },
      createSlice: (s) => { const i = s.indexOf('// Create'); const j = s.indexOf('// Update'); return i < 0 || j < 0 ? null : s.slice(i, j); },
      payloadVar: 'body',
      readers: ['v'],
      hydrateHelpers: [['Page._setAllowances(e)', allowanceIds]],
      readHelpers: [['Page._readAllowances()', allowanceIds]],
      helperKeys: [['Page._readAllowances()', allowanceKeys]],
      // fId = กุญแจของฟอร์ม ไม่ใช่ข้อมูล · fSalary อ่านผ่านตัวแปร sal นอกส่วน update
      ignoreIds: ['fId', 'fSalary'],
      createOnlyUpdateKeys: [],
    },
    {
      name: 'payroll.html',
      src: paySrc,
      modalId: 'empModal',
      hydrateAnchor: 'async editEmployee(id) {',
      saveAnchor: 'async saveEmployee() {',
      updateSlice: (s) => s,
      createSlice: (s) => s,
      payloadVar: 'data',
      readers: ['txt'],
      hydrateHelpers: [],
      readHelpers: [],
      helperKeys: [],
      // empId = กุญแจของฟอร์ม · empActive ส่งเฉพาะตอนแก้ไข (isActive อยู่ใน Update DTO เท่านั้น)
      ignoreIds: ['empId'],
      createOnlyUpdateKeys: ['isActive'],
    },
  ];
}

let fail = 0;
const ok = (name) => console.log(`  ✓ ${name}`);
const bad = (name, detail) => { console.log(`  ✗ ${name}${detail ? ' — ' + detail : ''}`); fail++; };

const cs = read('Accounting/Models/DTOs/Payroll/PayrollDtos.cs');
const empSrc = read('Accounting/wwwroot/pages/employees.html');
const paySrc = read('Accounting/wwwroot/pages/payroll.html');
const dto = loadDto(cs);

console.log('ทิศที่ 1 — ซอร์สปัจจุบัน: ฟอร์มพนักงานทั้งสองหน้าต้องตรงสัญญาทั้ง 5 ข้อ');
if (!dto.create || !dto.update || !dto.response) bad('อ่าน DTO พนักงานไม่ได้ (ชื่อ record เปลี่ยน?)');
else {
  ok(`อ่าน DTO: create ${dto.create.size} · update ${dto.update.size} · response ${dto.response.size} ช่อง`);
  for (const p of pages(empSrc, paySrc)) {
    const errs = checkPage(p, dto);
    if (errs.length === 0) ok(`${p.name} ตรงสัญญาครบ`);
    else errs.forEach(e => bad(p.name, e));
  }
  // ช่องที่ D-01 ต้องการ: เลขผู้เสียภาษีเขียนได้ทั้งสองทางและ echo กลับ
  for (const [set, label] of [[dto.create, 'Create'], [dto.update, 'Update'], [dto.response, 'Response']])
    set.has('taxId') ? ok(`${label} มี taxId (D-01)`) : bad(`${label} ไม่มี taxId — 50 ทวิ ภ.ง.ด.1 กลับไปเขียนไม่ได้`);
}

console.log('ทิศที่ 2 — ใส่บั๊กกลับ (negative test): ตัวตรวจต้องจับได้ทุกตัว');
const mutants = [
  {
    name: 'employees.html ตัด taxId ออกจาก payload update (สภาพก่อนแก้ D-01)',
    emp: (s) => s.replace("            taxId: v('fTaxId'),\n", ''),
    expect: /\[1\] fTaxId/,
  },
  {
    name: 'employees.html ไม่ hydrate ช่องธนาคาร (สภาพก่อนแก้ D-07 — เงินเดือนเข้าบัญชีผิดคน)',
    emp: (s) => s.replace("          document.getElementById('fBankAcct').value = e.bankAccountNumber || '';\n", ''),
    expect: /\[2\] fBankAcct/,
  },
  {
    name: 'UpdateEmployeeRequest ไม่มี FirstNameTh (สภาพก่อนแก้ A05)',
    cs: (s) => s.replace('    string? FirstNameTh = null,\n', ''),
    expect: /\[3\] คีย์ "firstNameTh" ส่งตอน update/,
  },
  {
    name: 'EmployeeResponse ไม่มี TaxId (เก็บแล้วไม่ echo)',
    cs: (s) => s.replace(/(public record EmployeeResponse\([\s\S]*?)    string\? TaxId = null,\n/, '$1'),
    expect: /\[4\] hydrate อ่าน e\.taxId/,
  },
  {
    name: 'employees.html ไม่ hydrate และไม่ส่ง fTaxId (สภาพก่อนแก้ D-01 — ช่องโชว์แต่ไม่มีใครแตะ)',
    emp: (s) => s.replace("          document.getElementById('fTaxId').value = e.taxId || '';\n", '')
                 .replace("            taxId: v('fTaxId'),\n", ''),
    expect: /\[5\] ช่อง fTaxId/,
  },
  {
    name: 'payroll.html ไม่ hydrate คำนำหน้า (select ค้าง "นาย" แล้วเขียนทับ "นางสาว")',
    pay: (s) => s.replace("      Layout.setTitleSelectValue('empTitle', e.titleTh || 'นาย');\n", ''),
    expect: /\[2\] empTitle/,
  },
];
for (const mu of mutants) {
  const e2 = mu.emp ? mu.emp(empSrc) : empSrc;
  const p2 = mu.pay ? mu.pay(paySrc) : paySrc;
  const c2 = mu.cs ? mu.cs(cs) : cs;
  if (e2 === empSrc && p2 === paySrc && c2 === cs) { bad(mu.name, 'mutation ไม่ได้เปลี่ยนซอร์ส (anchor เปลี่ยน? — ปรับ mutant ให้ตรงโค้ดจริง)'); continue; }
  const d2 = loadDto(c2);
  const errs = pages(e2, p2).flatMap(p => checkPage(p, d2));
  errs.some(e => mu.expect.test(e)) ? ok(`จับได้: ${mu.name}`) : bad(`จับไม่ได้: ${mu.name}`, JSON.stringify(errs));
}

if (fail) { console.log(`\n❌ employee_form_contract_sim ล้ม ${fail} ข้อ`); process.exit(1); }
console.log('\n✅ employee_form_contract_sim ผ่านทุกทิศ');
