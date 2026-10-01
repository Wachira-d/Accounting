// ล็อกสัญญา "ฟอร์มสินค้า ↔ API" ด้วย **ซอร์สจริง** ของ products.html + DTO จริง
// (Accounting/Models/DTOs/Product/ProductDtos.cs) — รอบ 201 ทีม IN (A-IN1 · คำตัดสินข้อ 30)
//
// ที่มา: `Product.CostingMethod` มีผู้อ่าน 5 จุดแต่ไม่มีผู้เขียน — DTO และหน้าสินค้าไม่มีช่อง ⇒ ทุกสินค้าเป็นถัวเฉลี่ยเสมอ
// (ค่าตั้งที่ไม่มีผู้เขียน = เงื่อนไขตาย) · checklist F4 B: ฟอร์ม → payload → DTO → echo → hydrate → reset ต้องครบทุกขั้น
//
// สัญญาที่ต้องจริงพร้อมกัน:
//   1. ทุกคีย์ใน payload สร้าง ⊆ CreateProductRequest · ทุกคีย์ใน payload แก้ไข ⊆ UpdateProductRequest
//      (ไม่งั้น System.Text.Json ทิ้งคีย์เงียบ = "บันทึกสำเร็จ" แต่ไม่มีผล)
//   2. ช่องวิธีคิดต้นทุน: อยู่ในทั้งสอง payload · ProductResponse echo กลับ (CostingMethod + สถานะล็อก + เหตุผล) ·
//      openEdit hydrate จากคำตอบ · openCreate รีเซ็ต (ห้ามค้างค่าของสินค้าก่อน) · ล็อกช่องตามธงจากเซิร์ฟเวอร์ (ห้าม silent no-op)
//   3. ค่าในตัวเลือก ⊆ ชื่อ enum CostingMethod (ห้ามมี LIFO) — enum ออก API เป็นชื่อ
// negative test ในตัว (F2 ข้อ 6): ถอด CostingMethod ออกจาก UpdateProductRequest · ถอด hydrate · ใส่ตัวเลือก Lifo · ถอด reset
// แล้วต้องฟ้องทุกตัว
'use strict';
const fs = require('fs');
const path = require('path');

const ROOT = path.join(__dirname, '..');
const read = (p) => fs.readFileSync(path.join(ROOT, p), 'utf8');

function recordParams(cs, name) {
  const i = cs.indexOf(`public record ${name}(`);
  if (i < 0) return null;
  const end = cs.indexOf(');', i);
  const body = cs.slice(i + `public record ${name}(`.length, end)
    .split('\n').map(l => l.replace(/\/\/.*$/, '')).join('\n');
  const out = new Set();
  for (const part of body.split(',')) {
    const m = part.trim().match(/([A-Z]\w*)\s*(?:=.*)?$/);
    if (m) out.add(camel(m[1]));
  }
  return out;
}

/** ชื่อ property C# → ชื่อ JSON ตาม JsonNamingPolicy.CamelCase (ตัวพิมพ์ใหญ่นำหน้าทั้งก้อนเป็นตัวเล็ก: SKU → sku) */
function camel(n) {
  let i = 0;
  while (i < n.length && n[i] >= 'A' && n[i] <= 'Z') {
    if (i > 0 && i + 1 < n.length && n[i + 1] >= 'a' && n[i + 1] <= 'z') break;
    i++;
  }
  return n.slice(0, i).toLowerCase() + n.slice(i);
}

function enumNames(cs, name) {
  const i = cs.indexOf(`public enum ${name}`);
  if (i < 0) return null;
  const body = cs.slice(cs.indexOf('{', i) + 1, cs.indexOf('}', i));
  return new Set([...body.replace(/\/\/\/.*$/gm, '').matchAll(/^\s*(\w+)\s*=/gm)].map(m => m[1]));
}

/** คีย์ของ object literal ที่ส่งเข้า api.<fn>(…, { … }) — ระดับบนสุดเท่านั้น */
function payloadKeysOf(src, apiFn) {
  const i = src.indexOf(`api.${apiFn}(`);
  if (i < 0) return null;
  const open = src.indexOf('{', i);
  let depth = 0, k = open, q = null;
  for (; k < src.length; k++) {
    const c = src[k];
    if (q) { if (c === '\\') { k++; continue; } if (c === q) q = null; continue; }
    if (c === "'" || c === '"' || c === '`') { q = c; continue; }
    if (c === '{' || c === '(' || c === '[') depth++;
    else if (c === '}' || c === ')' || c === ']') { depth--; if (depth === 0) break; }
  }
  const obj = src.slice(open + 1, k);
  const keys = new Set();
  let d = 0, start = 0;
  for (let j = 0; j <= obj.length; j++) {
    const c = obj[j];
    if (c === '(' || c === '[' || c === '{') d++;
    else if (c === ')' || c === ']' || c === '}') d--;
    if ((c === ',' && d === 0) || j === obj.length) {
      const m = obj.slice(start, j).match(/^\s*(\w+)\s*:/);
      if (m) keys.add(m[1]);
      start = j + 1;
    }
  }
  return keys;
}

function methodBlock(src, name) {
  const i = src.indexOf(`\n      ${name}(`);
  if (i < 0) return null;
  const j = src.indexOf('\n      },', i);
  return src.slice(i, j < 0 ? undefined : j);
}

function check(html, dtos, enums) {
  const errs = [];
  const create = recordParams(dtos, 'CreateProductRequest');
  const update = recordParams(dtos, 'UpdateProductRequest');
  const resp = recordParams(dtos, 'ProductResponse');
  const cKeys = payloadKeysOf(html, 'createProduct');
  const uKeys = payloadKeysOf(html, 'updateProduct');
  if (!create || !update || !resp) errs.push('หา record DTO ของสินค้าไม่เจอ');
  if (!cKeys || !uKeys) errs.push('หา payload createProduct/updateProduct ใน products.html ไม่เจอ');
  if (errs.length) return errs;
  for (const k of cKeys) if (!create.has(k)) errs.push(`payload สร้างส่ง "${k}" ที่ CreateProductRequest ไม่มี (ถูกทิ้งเงียบ)`);
  for (const k of uKeys) if (!update.has(k)) errs.push(`payload แก้ไขส่ง "${k}" ที่ UpdateProductRequest ไม่มี (ถูกทิ้งเงียบ)`);
  for (const k of ['costingMethod']) {
    if (!cKeys.has(k)) errs.push(`payload สร้างไม่ส่ง ${k}`);
    if (!uKeys.has(k)) errs.push(`payload แก้ไขไม่ส่ง ${k}`);
  }
  for (const k of ['costingMethod', 'costingMethodLocked', 'costingMethodLockReason'])
    if (!resp.has(k)) errs.push(`ProductResponse ไม่ echo ${k}`);
  const setter = methodBlock(html, '_setCostingMethod');
  if (!setter) errs.push('ไม่มีตัวเติมช่องวิธีคิดต้นทุน _setCostingMethod');
  else {
    if (!/p\.costingMethod\b/.test(setter)) errs.push('_setCostingMethod ไม่อ่าน p.costingMethod (hydrate)');
    if (!/costingMethodLocked\s*===\s*true/.test(setter) || !/\.disabled\s*=/.test(setter))
      errs.push('_setCostingMethod ไม่ล็อกช่องตาม costingMethodLocked จากเซิร์ฟเวอร์');
  }
  const openEdit = methodBlock(html, 'openEdit');
  const openCreate = methodBlock(html, 'openCreate');
  if (!openEdit || !/_setCostingMethod\(p\)/.test(openEdit)) errs.push('openEdit ไม่ hydrate วิธีคิดต้นทุน');
  if (!openCreate || !/_setCostingMethod\(null\)/.test(openCreate)) errs.push('openCreate ไม่รีเซ็ตวิธีคิดต้นทุน (ค้างค่าของสินค้าก่อน)');
  const sel = html.slice(html.indexOf('id="fCostingMethod"'), html.indexOf('</select>', html.indexOf('id="fCostingMethod"')));
  const opts = [...sel.matchAll(/<option value="(\w+)"/g)].map(m => m[1]);
  if (opts.length === 0) errs.push('ไม่มีตัวเลือกวิธีคิดต้นทุน');
  for (const o of opts) {
    if (!enums.has(o)) errs.push(`ตัวเลือก "${o}" ไม่อยู่ใน enum CostingMethod`);
    if (/lifo/i.test(o)) errs.push('มีตัวเลือก LIFO (TFRS for NPAEs บทที่ 8 ห้าม)');
  }
  return errs;
}

const html = read('Accounting/wwwroot/pages/products.html');
const dtos = read('Accounting/Models/DTOs/Product/ProductDtos.cs');
const enums = enumNames(read('Accounting/Models/Enums/AllEnums.cs'), 'CostingMethod');

let failed = false;
const real = check(html, dtos, enums);
if (real.length) { failed = true; console.log('❌ สัญญาฟอร์มสินค้า ↔ API ไม่ครบ:\n  ' + real.join('\n  ')); }

// negative test — ใส่บั๊กกลับเข้าไปในซอร์สจริงแล้วต้องฟ้อง
const negatives = [
  ['ถอด CostingMethod ออกจาก UpdateProductRequest', html,
    dtos.replace(/(public record UpdateProductRequest\([\s\S]*?)\n\s*CostingMethod\? CostingMethod = null\);/, '$1\n    string? Dummy = null);'), enums],
  ['ถอด hydrate ใน openEdit', html.replace('this._setCostingMethod(p);\n        this._toggleImageSection(p);\n        Layout.openModal', 'this._toggleImageSection(p);\n        Layout.openModal'), dtos, enums],
  ['ถอด reset ใน openCreate', html.replace('this._setCostingMethod(null);', ''), dtos, enums],
  ['ใส่ตัวเลือก Lifo', html.replace('<option value="Fifo">', '<option value="Lifo">LIFO</option><option value="Fifo">'), dtos, enums],
  ['ถอดการล็อกช่อง', html.replace('sel.disabled = locked;', ''), dtos, enums],
  ['ถอด costingMethod ออกจาก payload แก้ไข', html.replace(/,costingMethod:document\.getElementById\('fCostingMethod'\)\.value\|\|null\}\); \}\n\s*else/, '}); }\n          else'), dtos, enums],
];
for (const [label, h, d, e] of negatives) {
  if (h === html && d === dtos) { failed = true; console.log(`❌ negative test "${label}": แทนข้อความไม่สำเร็จ (ซอร์สเปลี่ยน? แก้ตัว sim)`); continue; }
  if (check(h, d, e).length === 0) { failed = true; console.log(`❌ negative test "${label}": ใส่บั๊กกลับแล้วไม่ฟ้อง`); }
}

if (failed) process.exit(1);
console.log(`✅ product_form_contract_sim: สัญญาฟอร์มสินค้า ↔ DTO ครบ (สร้าง ${payloadKeysOf(html, 'createProduct').size} คีย์ · แก้ไข ${payloadKeysOf(html, 'updateProduct').size} คีย์) · negative test ${negatives.length} ตัวจับได้ทุกตัว`);
