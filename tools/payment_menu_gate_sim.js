// ล็อกพฤติกรรม "เมนูรับชำระออนไลน์ซ่อนเมื่อเซิร์ฟเวอร์บอกว่าไม่มีสิทธิ์" ด้วย **โค้ดจริง** จาก layout.js — รอบ 201 ทีม GW (A-GW10)
//
// ที่มา: เมนู "รายการรับชำระออนไลน์"/"กระทบยอดเงินรับออนไลน์" โผล่ให้ทุกคน กดแล้ว 403 (endpoint ต้องการสิทธิ์ดูบัญชีธนาคาร) ·
// กติกา: เซิร์ฟเวอร์ส่ง `permissionDeniedMenuIds` (ตาราง PaymentGatewayPermissionScope.MenuPermissionKeys) · `hasMenuAccess` ตัดเมนูเหล่านั้น
// **ก่อน** sentinel "*" (ผู้ใช้ไม่ผูกบทบาทเห็นทุกเมนู) · เจ้าของ/แอดมินไม่ถูกตัด · ไม่มีข้อมูลสิทธิ์ = ไม่ซ่อน (ไม่กระพริบ)
//
// เมธอดถูกตัดมาจากซอร์สจริง (ไม่เขียนซ้ำ) + negative test: ถอดบรรทัดตัดเมนูออกแล้วชุดเดียวกันต้องล้ม
'use strict';
const fs = require('fs');
const path = require('path');

const SRC = fs.readFileSync(path.join(__dirname, '..', 'Accounting', 'wwwroot', 'js', 'layout.js'), 'utf8');

/** ตัดเมธอด `name(args) { … }` ของ object literal ออกมาทั้งก้อน (นับวงเล็บปีกกา) */
function extractMethod(src, name) {
  const re = new RegExp('\\n\\s*' + name + '\\s*\\(([^)]*)\\)\\s*\\{');
  const m = re.exec(src);
  if (!m) return null;
  let i = m.index + m[0].length, depth = 1;
  for (; i < src.length && depth > 0; i++) {
    const ch = src[i];
    if (ch === '{') depth++;
    else if (ch === '}') depth--;
  }
  return `${name}(${m[1]}) {` + src.slice(m.index + m[0].length, i);
}

function build(src) {
  const part = extractMethod(src, 'hasMenuAccess');
  if (!part) return null;
  // eslint-disable-next-line no-new-func
  return new Function(`return { myPermissions: null, ${part} };`)();
}

function suite(src, quiet) {
  let fail = 0;
  const check = (name, cond) => {
    if (!quiet) console.log(`  ${cond ? '✓' : '✗'} ${name}`);
    if (!cond) fail++;
  };
  const L = build(src);
  if (!L) { if (!quiet) console.log('  ✗ หาเมธอด hasMenuAccess ใน layout.js ไม่เจอ'); return 99; }

  L.myPermissions = { isOwnerOrAdmin: false, allowedMenuIds: ['*'], permissionDeniedMenuIds: ['payment-intents', 'payment-settlements'] };
  check('ไม่ผูกบทบาท (*) แต่ไม่มีสิทธิ์ดูธนาคาร: ซ่อนรายการรับชำระ', L.hasMenuAccess('payment-intents') === false);
  check('ไม่ผูกบทบาท (*) แต่ไม่มีสิทธิ์ดูธนาคาร: ซ่อนกระทบยอด', L.hasMenuAccess('payment-settlements') === false);
  check('ทิศตรงข้าม: เมนูอื่นยังเห็น', L.hasMenuAccess('documents') === true);
  L.myPermissions = { isOwnerOrAdmin: false, allowedMenuIds: ['payment-intents'], permissionDeniedMenuIds: ['payment-intents'] };
  check('บทบาทติ๊กเมนูไว้แต่ไม่มีสิทธิ์ดูธนาคาร: ซ่อน (เปิดแล้วจะ 403)', L.hasMenuAccess('payment-intents') === false);
  L.myPermissions = { isOwnerOrAdmin: false, allowedMenuIds: ['*'], permissionDeniedMenuIds: null };
  check('ทิศตรงข้าม: มีสิทธิ์ (ไม่มีรายการตัด) = เห็น', L.hasMenuAccess('payment-intents') === true);
  L.myPermissions = { isOwnerOrAdmin: true, allowedMenuIds: [], permissionDeniedMenuIds: ['payment-intents'] };
  check('ทิศตรงข้าม: เจ้าของ/แอดมินไม่ถูกตัด', L.hasMenuAccess('payment-intents') === true);
  L.myPermissions = null;
  check('ทิศตรงข้าม: ยังไม่โหลดสิทธิ์ = ไม่ซ่อน (ไม่กระพริบ)', L.hasMenuAccess('payment-intents') === true);
  return fail;
}

console.log('payment_menu_gate_sim — โค้ดจริงจาก layout.js');
const fails = suite(SRC, false);
// negative test: ถอดบรรทัดตัดเมนูตามสิทธิ์ออก ⇒ ชุดเดียวกันต้องล้ม
const mutated = SRC.replace(/\n[^\n]*permissionDeniedMenuIds \|\| \[\]\)\.includes\(menuId\)\)\s*return false;/, '\n');
const negFails = mutated === SRC ? 0 : suite(mutated, true);
if (mutated === SRC) console.log('  ✗ negative test: หาบรรทัดตัดเมนูในซอร์สไม่เจอ (ตัวตัดเปลี่ยนรูป — แก้ sim)');
else console.log(`  ${negFails > 0 ? '✓' : '✗'} negative test: ถอดบรรทัดตัดเมนูแล้วชุดเดียวกันล้ม ${negFails} ข้อ`);
if (fails || mutated === SRC || negFails === 0) { console.log('❌ payment_menu_gate_sim ล้ม'); process.exit(1); }
console.log('✅ payment_menu_gate_sim ผ่านทุกทิศ');
