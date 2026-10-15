// ล็อกพฤติกรรม "เมนูล็อก 🔒 ตามโหมดบังคับแพ็กเกจที่มีผลจริง" ด้วย **โค้ดจริง** จาก layout.js — รอบ 200 ฝ่ายค้าน S200-2
//
// ที่มา: รอบ 200 ทีม S ผูกเมนู settlements/settlement-channels กับฟีเจอร์ BankReconciliation ⇒ `_renderNavItem` ล็อก 🔒 ทันทีหลัง deploy
// สำหรับแพ็กเกจเล็ก ทั้งที่เซิร์ฟเวอร์ยังเป็นโหมดเงา (ไม่บล็อกใคร) — "ล็อกครึ่งเดียว" ก่อนเจ้าของกดบังคับ + รายงานเงานับต่ำกว่าจริง
// กติกา: เมนูที่ผูกฟีเจอร์ใหม่ (`lockOnEnforce: true`) ล็อกเฉพาะเมื่อเซิร์ฟเวอร์บอกว่าโหมดที่มีผลจริง = "Enforce" (`featureGateMode`)
// · ก่อนนั้นป้าย "แพ็กเกจไม่รวม" กดเข้าได้ · เมนูที่ล็อกมาก่อนรอบ 200 คงพฤติกรรมเดิม (ห้ามหลวม)
//
// เมธอดถูกตัดมาจากซอร์สจริง (ไม่เขียนซ้ำ) + negative test: ถอดเงื่อนไขโหมดออกแล้วชุดเดียวกันต้องล้ม
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
  const parts = ['hasFeature', '_navLockState'].map(n => extractMethod(src, n));
  if (parts.some(p => !p)) return null;
  // eslint-disable-next-line no-new-func
  return new Function(`return { subscription: null, features: [], ${parts.join(',\n')} };`)();
}

/** ตัดรายการเมนู `{ id: '<id>', … }` ออกมาเป็น object (นับวงเล็บปีกกา) — รอบ 206 ใช้เทียบเมนูศูนย์นำเข้ากับหน้าเดิม */
function navItem(src, id) {
  const at = src.indexOf(`{ id: '${id}',`);
  if (at < 0) return null;
  let i = at + 1, depth = 1;
  for (; i < src.length && depth > 0; i++) {
    const ch = src[i];
    if (ch === '{') depth++;
    else if (ch === '}') depth--;
  }
  // eslint-disable-next-line no-new-func
  try { return new Function(`return ${src.slice(at, i)};`)(); } catch { return null; }
}

/** รอบ 206: เมนูศูนย์นำเข้า (import-hub) ต้องล็อกเหมือนหน้าเดิม (import-export) ทุกโหมด/แพ็กเกจ — "จำกัดตามหน้าเดิม" */
function hubSuite(src, quiet) {
  let fail = 0;
  const check = (name, cond, extra = '') => {
    if (!quiet) console.log(`  ${cond ? '✓' : '✗'} ${name}${!cond && extra ? ' — ' + extra : ''}`);
    if (!cond) fail++;
  };
  const L = build(src);
  const hub = navItem(src, 'import-hub'), old = navItem(src, 'import-export');
  if (!L || !hub || !old) { if (!quiet) console.log('  ✗ หาเมนู import-hub/import-export ใน layout.js ไม่เจอ'); return 99; }
  for (const mode of ['Off', 'Shadow', 'Enforce', undefined]) {
    for (const feats of [['DocumentEngine', 'BasicAccounting'], ['DocumentEngine', 'BulkImport']]) {
      L.subscription = { featureGateMode: mode, enabledAddOnCodes: [] }; L.features = feats.slice();
      const a = L._navLockState(hub), b = L._navLockState(old);
      check(`โหมด ${mode} · ${feats.includes('BulkImport') ? 'มี' : 'ไม่มี'} BulkImport: ศูนย์นำเข้า = หน้าเดิม (${b})`, a === b, a);
    }
  }
  L.subscription = { featureGateMode: 'Shadow', enabledAddOnCodes: [] }; L.features = ['DocumentEngine'];
  check('แพ็กเกจไม่มี BulkImport: ศูนย์นำเข้าล็อก (ทิศบล็อก)', L._navLockState(hub) === 'locked');
  L.features.push('BulkImport');
  check('แพ็กเกจมี BulkImport: ศูนย์นำเข้าเปิด (ทิศผ่าน)', L._navLockState(hub) === 'open');
  return fail;
}

function suite(src, quiet) {
  let fail = 0;
  const check = (name, cond, extra = '') => {
    if (!quiet) console.log(`  ${cond ? '✓' : '✗'} ${name}${!cond && extra ? ' — ' + extra : ''}`);
    if (!cond) fail++;
  };
  const L = build(src);
  if (!L) { if (!quiet) console.log('  ✗ หาเมธอด hasFeature/_navLockState ใน layout.js ไม่เจอ'); return 99; }
  const settlements = { id: 'settlements', feature: 'BankReconciliation', lockOnEnforce: true };
  const payroll = { id: 'payroll', feature: 'Payroll' };
  const docs = { id: 'documents', feature: 'DocumentEngine' };
  const free = (mode) => { L.subscription = { featureGateMode: mode, enabledAddOnCodes: [] }; L.features = ['DocumentEngine', 'BasicAccounting']; };

  free('Shadow');
  check('โหมดเงา: เมนูใหม่ที่แพ็กเกจไม่รวม = ป้าย ไม่ล็อก', L._navLockState(settlements) === 'notInPlan', L._navLockState(settlements));
  check('โหมดเงา: เมนูเดิมที่แพ็กเกจไม่รวม = ล็อกเหมือนเดิม (ไม่หลวม)', L._navLockState(payroll) === 'locked');
  check('ฟีเจอร์ที่มี = เปิด', L._navLockState(docs) === 'open');
  free('Off');
  check('ปิด: เมนูใหม่ไม่ล็อก', L._navLockState(settlements) === 'notInPlan');
  free(undefined);
  check('แคชเก่าไม่มี featureGateMode: เมนูใหม่ไม่ล็อก (ไม่รู้ = ยังไม่บังคับ)', L._navLockState(settlements) === 'notInPlan');
  free('Enforce');
  check('บังคับ: เมนูใหม่ล็อก (ทิศตรงข้าม — ด่านยังทำงาน)', L._navLockState(settlements) === 'locked');
  check('บังคับ: เมนูเดิมล็อก', L._navLockState(payroll) === 'locked');
  L.features.push('BankReconciliation');
  check('บังคับ + แพ็กเกจรวมฟีเจอร์ = เปิด', L._navLockState(settlements) === 'open');
  L.subscription = null;
  check('ยังไม่มีข้อมูลแพ็กเกจ = เปิด (graceful เดิม)', L._navLockState(settlements) === 'open' && L._navLockState(payroll) === 'open');
  return fail;
}

console.log('layout.js — เมนูล็อกตามโหมดบังคับแพ็กเกจที่มีผลจริง (รอบ 200 S200-2)');
const fail = suite(SRC, false);
const broken = SRC.replace("this.subscription.featureGateMode !== 'Enforce'", 'false');
let negFail = -1;
if (broken === SRC) {
  console.log('✗ negative test: หาเงื่อนไขโหมดในซอร์สไม่เจอ — sim ไม่มีด่าน');
} else {
  negFail = suite(broken, true);
  console.log(negFail > 0
    ? `✓ negative test: ถอดเงื่อนไขโหมด (ล็อกทันทีแบบเดิม) แล้วชุดเดียวกันล้ม ${negFail} ข้อ`
    : '✗ negative test: ถอดเงื่อนไขแล้วยังผ่าน — sim ไม่มีด่าน');
}
console.log('layout.js — เมนูศูนย์นำเข้าล็อกเหมือนหน้านำเข้าเดิม (รอบ 206)');
const hubFail = hubSuite(SRC, false);
const hubBroken = SRC.replace("href: '/pages/import-hub.html', feature: 'BulkImport',", "href: '/pages/import-hub.html',");
let hubNeg = -1;
if (hubBroken === SRC) {
  console.log('✗ negative test: หาฟีเจอร์ของเมนู import-hub ในซอร์สไม่เจอ — sim ไม่มีด่าน');
} else {
  hubNeg = hubSuite(hubBroken, true);
  console.log(hubNeg > 0
    ? `✓ negative test: ถอด feature ของเมนูศูนย์นำเข้า (สภาพก่อนรอบ 206) แล้วล้ม ${hubNeg} ข้อ`
    : '✗ negative test: ถอดแล้วยังผ่าน — sim ไม่มีด่าน');
}
const ok = fail === 0 && negFail > 0 && hubFail === 0 && hubNeg > 0;
console.log(ok ? 'ผ่าน' : `ล้ม (${fail + hubFail} ข้อ)`);
process.exit(ok ? 0 : 1);
