#!/usr/bin/env node
// ═══ ล็อกพฤติกรรม "เพิ่มผู้ติดต่อด่วน" ของตัวเลือกผู้ติดต่อกลาง (Layout.contactPicker) ด้วยโค้ดจริงจาก layout.js ═══
//
// รัน: node tools/contact_picker_sim.js
//
// ที่มา (คำสั่งเจ้าของ 2026-10-14): ทุกหน้าที่เลือกผู้ติดต่อต้องมีปุ่มเพิ่มแบบหน้าออกเอกสาร และเป็นแนวทางเดียวทั้งระบบ
// เดิมหน้าเอกสารสร้างเงียบเมื่อพิมพ์เลขภาษีครบ ด้วยชื่อที่แต่งขึ้น ("นิติบุคคล 0105…" เมื่อทะเบียนไม่ตอบ) และเดาฝั่งผู้ติดต่อ
// จากสำเนารายชื่อชนิดเอกสาร (ใบลด/เพิ่มหนี้ฝั่งขาย ⇒ ผู้ขาย — ผิด) · ตัวใหม่: เปิดหน้าต่างให้ผู้ใช้เห็นก่อนบันทึก ·
// กติกาทั้งหมดอยู่ที่ POST document/contacts ฝั่งเซิร์ฟเวอร์ — หน้าแค่ประกอบ payload ตามที่ผู้ใช้เลือก
//
// ดึงเมธอดจริงจาก layout.js (ไม่ใช่สำเนาในเทสต์): _contactSeed · _contactSideFlags · _canCreateContact · _contactQuickPayload ·
// _contactCreateOutcome · taxIdCheck — ล็อกสองทิศ (F2 ข้อ 8): ค่าที่ต้องถูกตัด/ถูกเติม และค่าที่ถูกอยู่แล้วต้องไม่ถูกแตะ
// และ negative test ในตัว (F2 ข้อ 6): ใส่บั๊กกลับทีละแบบลงสำเนาซอร์สแล้วชุดเดียวกันต้องล้ม
'use strict';
const fs = require('fs');
const path = require('path');

const SRC = fs.readFileSync(path.join(__dirname, '..', 'Accounting', 'wwwroot', 'js', 'layout.js'), 'utf8');
const METHODS = ['taxIdCheck', '_contactSeed', '_contactSideFlags', '_canCreateContact', '_contactQuickPayload', '_contactCreateOutcome'];

function extract(src, name) {
  const m = new RegExp('\\n  (async )?' + name + '\\(').exec(src);
  if (!m) return null;
  const start = m.index + 1;
  const end = src.indexOf('\n  },', start);
  return end < 0 ? null : src.slice(start, end + '\n  }'.length);
}

function build(src) {
  const parts = METHODS.map(n => {
    const t = extract(src, n);
    if (!t) throw new Error('หาเมธอด ' + n + ' ใน layout.js ไม่เจอ — ถูกลบ/เปลี่ยนชื่อ?');
    return t;
  });
  // eslint-disable-next-line no-new-func
  const L = new Function('return ({\n' + parts.join(',\n') + '\n});')();
  globalThis.Layout = L;   // เมธอดเรียก Layout.taxIdCheck ตรง ๆ
  return L;
}

// เลขผู้เสียภาษีที่ checksum ถูก (คิดตามสูตรกรมสรรพากร — ใช้สร้าง fixture เท่านั้น ไม่ได้ตรวจตัวเอง: ตัวตรวจคือ taxIdCheck ของ layout.js)
function withCheck(d12) {
  let s = 0;
  for (let i = 0; i < 12; i++) s += Number(d12[i]) * (13 - i);
  return d12 + String((11 - (s % 11)) % 10);
}
const JUR = withCheck('010555600000');   // ขึ้นต้น 0 = นิติบุคคล
const IND = withCheck('110170000000');   // บุคคลธรรมดา
const BAD = JUR.slice(0, 12) + String((Number(JUR[12]) + 1) % 10);

function suite(L) {
  const fails = [];
  const eq = (name, got, want) => {
    const g = JSON.stringify(got), w = JSON.stringify(want);
    if (g !== w) fails.push(`${name}: ได้ ${g} ต้องการ ${w}`);
  };
  const base = { name: 'บริษัท ทดสอบ จำกัด', taxId: JUR, type: 'JuristicPerson', branchMode: 'hq', branchCode: '', branchName: '',
                 isCustomer: false, isSupplier: true, address: ' 1 ถ.สุขุมวิท ', phone: '', email: 'a@b.co ' };
  const P = (over) => L._contactQuickPayload(Object.assign({}, base, over));

  // ── ค่าที่พิมพ์ค้างไหลเข้าหน้าต่าง ──
  eq('seed เลข 13 หลัก', L._contactSeed(' ' + JUR + ' '), { name: '', taxId: JUR });
  eq('seed เลขมีขีด/วรรค', L._contactSeed('0-1055-56000-00-' + JUR[12]), { name: '', taxId: JUR });
  eq('seed ชื่อ', L._contactSeed('  ร้านป้าแดง '), { name: 'ร้านป้าแดง', taxId: '' });
  eq('seed เลขไม่ครบ = ชื่อ', L._contactSeed('12345'), { name: '12345', taxId: '' });

  // ── ฝั่งตั้งต้นตามบริบทหน้า ──
  eq('side supplier', L._contactSideFlags('supplier'), { isCustomer: false, isSupplier: true });
  eq('side customer', L._contactSideFlags('customer'), { isCustomer: true, isSupplier: false });
  eq('side both', L._contactSideFlags('both'), { isCustomer: true, isSupplier: true });

  // ── สิทธิ์ (ซ่อนปุ่มเฉพาะเมื่อรู้แน่ว่าไม่มี) ──
  eq('perm ยังไม่โหลด', L._canCreateContact(null), true);
  eq('perm เจ้าของ', L._canCreateContact({ isOwnerOrAdmin: true, allowedMenuIds: [] }), true);
  eq('perm legacy *', L._canCreateContact({ allowedMenuIds: ['*'] }), true);
  eq('perm role ไม่มีคีย์', L._canCreateContact({ allowedMenuIds: ['documents', 'perm:Document.Approve'] }), false);
  eq('perm role มีคีย์', L._canCreateContact({ allowedMenuIds: ['perm:Contact.Edit'] }), true);

  // ── payload: ทางที่ถูก ──
  eq('นิติบุคคล สนญ.', P({}).payload, { name: 'บริษัท ทดสอบ จำกัด', taxId: JUR, branchCode: '00000', branchName: null,
    contactType: 'JuristicPerson', isCustomer: false, isSupplier: true, address: '1 ถ.สุขุมวิท', phone: null, email: 'a@b.co', contactPerson: null });
  eq('สาขาที่ 1 เติมศูนย์', P({ branchMode: 'sub', branchCode: '1', branchName: ' สาขาบางนา ' }).payload.branchCode, '00001');
  eq('สาขา ชื่อสาขาไปด้วย', P({ branchMode: 'sub', branchCode: '00012', branchName: ' สาขาบางนา ' }).payload.branchName, 'สาขาบางนา');
  eq('บุคคลธรรมดา ไม่มีสาขา', P({ taxId: IND, type: 'Individual' }).payload.branchCode, null);
  eq('ให้ระบบตัดสิน = contactType null', P({ type: 'auto' }).payload.contactType, null);
  // ให้ระบบตัดสิน + สนญ. ⇒ ห้ามส่ง 00000 (เซิร์ฟเวอร์ InferContactType ถือ 00000+เลข 13 หลัก = นิติบุคคล ⇒ บุคคลธรรมดาจะกลายเป็นนิติบุคคล)
  eq('ให้ระบบตัดสิน + สนญ. ⇒ branch null', P({ taxId: IND, type: 'auto' }).payload.branchCode, null);
  eq('ให้ระบบตัดสิน + สาขา ⇒ ส่งสาขา', P({ type: 'auto', branchMode: 'sub', branchCode: '2' }).payload.branchCode, '00002');
  eq('ไม่มีเลขภาษี ⇒ ไม่มีสาขา', P({ taxId: '', type: 'JuristicPerson' }).payload.branchCode, null);
  eq('เลขภาษีมีขีด ⇒ ตัดเหลือเลข', P({ taxId: JUR.slice(0, 1) + '-' + JUR.slice(1) }).payload.taxId, JUR);
  eq('ราชการ สนญ.', P({ type: 'GovernmentAgency' }).payload.branchCode, '00000');

  // ── payload: ทางที่ต้องถูกกัน (บอกช่องที่ผิด) ──
  eq('ไม่มีชื่อ', P({ name: '  ' }).field, 'cpqName');
  eq('เลขภาษี checksum ผิด', P({ taxId: BAD }).field, 'cpqTaxId');
  eq('เลขภาษีไม่ครบ', P({ taxId: '12345' }).field, 'cpqTaxId');
  eq('ไม่เลือกประเภท', P({ type: '' }).field, 'cpqType');
  eq('ให้ระบบตัดสินแต่ไม่มีเลข', P({ type: 'auto', taxId: '' }).field, 'cpqType');
  eq('สาขาที่ว่าง', P({ branchMode: 'sub', branchCode: '' }).field, 'cpqBranchCode');
  eq('สาขาที่ 00000', P({ branchMode: 'sub', branchCode: '00000' }).field, 'cpqBranchCode');
  eq('สาขา 6 หลัก', P({ branchMode: 'sub', branchCode: '123456' }).field, 'cpqBranchCode');
  eq('ไม่เลือกฝั่ง', P({ isCustomer: false, isSupplier: false }).field, 'cpqIsCustomer');
  eq('ผิด ⇒ ไม่มี payload', P({ name: '' }).payload, undefined);

  // ── ผลจากเซิร์ฟเวอร์ ──
  eq('สร้างใหม่', L._contactCreateOutcome({ id: 'n1', name: 'X', isActive: true }, ['e1']).kind, 'created');
  eq('เซิร์ฟเวอร์คืนรายเดิม (เลข+สาขาซ้ำ)', L._contactCreateOutcome({ id: 'e1', name: 'X', isActive: true }, ['e1']).kind, 'existing');
  eq('รายเดิมที่ปิดใช้งาน ⇒ ห้ามเลือก', L._contactCreateOutcome({ id: 'e1', name: 'X', isActive: false }, ['e1']).kind, 'inactive');
  eq('ปิดใช้งานแม้ไม่รู้จักมาก่อน', L._contactCreateOutcome({ id: 'z', name: 'X', isActive: false }, []).kind, 'inactive');
  eq('ไม่มีผลตอบ', L._contactCreateOutcome(null, []).kind, 'error');
  return fails;
}

let bad = 0;
const real = suite(build(SRC));
if (real.length) {
  console.log('❌ contact_picker_sim — โค้ดจริงไม่ผ่าน:');
  real.forEach(f => console.log('   ' + f));
  bad = 1;
}

// ── negative test: ใส่บั๊กกลับทีละแบบ ชุดเดียวกันต้องล้ม ──
const mutants = [
  ['ส่ง 00000 ตอนให้ระบบตัดสิน', "} else if (type !== 'auto') {", '} else {'],
  ['ไม่ตรวจ checksum', 'if (!chk.valid) return', 'if (false) return'],
  ['ไม่บังคับประเภท', "if (!type) return { error:", "if (false) return { error:"],
  ['เลือกรายที่ปิดใช้งาน', 'if (created.isActive === false)', 'if (false)'],
  ['ซ่อนปุ่มทุกคนที่ไม่ใช่เจ้าของ', "return a.includes('*') || a.includes('perm:Contact.Edit');", 'return false;'],
  ['ฝั่งตั้งต้นสลับ', "if (side === 'supplier') return { isCustomer: false, isSupplier: true };", "if (side === 'supplier') return { isCustomer: true, isSupplier: false };"],
  ['เลขมีขีดไม่ถือเป็นเลขภาษี', "const digits = t.replace(/[\\s-]/g, '');", 'const digits = t;'],
];
for (const [name, from, to] of mutants) {
  if (!SRC.includes(from)) { console.log(`❌ negative test '${name}': หาข้อความต้นฉบับไม่เจอ — แก้ sim ให้ตรงโค้ด`); bad = 1; continue; }
  let f;
  try { f = suite(build(SRC.replace(from, to))); } catch (e) { f = ['build: ' + e.message]; }
  if (!f.length) { console.log(`❌ negative test '${name}': ใส่บั๊กแล้วชุดเทสต์ยังผ่าน`); bad = 1; }
}

if (!bad) console.log(`✅ contact_picker_sim: ผ่าน · negative test ${mutants.length} แบบถูกจับครบ`);
process.exit(bad);
