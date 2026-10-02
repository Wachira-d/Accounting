#!/usr/bin/env node
// ═══ ล็อกพฤติกรรม: หน้าการจองของแขก (storefront.html) — โหมด "ต้องส่งสลิปก่อนการจองจึงสำเร็จ" (คำตัดสินข้อ 128) ═══
//
// รัน: node tools/lodging_slip_confirm_sim.js
//
// ที่มา (ผู้ใช้ 2026-10-02): "ปรับให้ตั้งค่าได้ว่าต้องส่งสลิปโอนเงินก่อนถึงจะจองสำเร็จได้ หรือ จะเป็นแบบเดิมนี้ก็ได้" —
//   เดิมแขกกดจองแล้วขึ้นแบนเนอร์เขียว "จองสำเร็จ · ยืนยันแล้ว" ทั้งที่ชำระ ฿0 แล้วกล่องอัปโหลดสลิปโผล่ตามหลัง ·
//   และหน้าเว็บตัดสินเองว่าจะเปิดช่องส่งสลิปไหม (`status === 'Pending' || Confirmed && balanceDue > 0`)
//
// ใช้โค้ดจริงจากหน้า (ดึงเมธอดออกมารัน) · สองทิศ (F2 ข้อ 8):
//   (a) ต้องส่งสลิป (slipRequired) ⇒ แบนเนอร์ไม่ใช่สีเขียว · ข้อความของเซิร์ฟเวอร์ (หนีอักขระ) · ยอดที่ต้องโอน = amountToTransfer
//       (ไม่ใช่ depositRequired) · กำหนดเวลาแสดง · ช่องส่งสลิป + บัญชีรับโอนจากค่าตั้งของเว็บ (หนีอักขระ) · QR ใช้ยอดของเซิร์ฟเวอร์
//   (b) เซิร์ฟเวอร์บอกปิดช่อง (canUploadSlip=false) ⇒ ไม่มีฟอร์ม แม้สถานะ Pending · เซิร์ฟเวอร์บอกเปิด (ใบที่ระบบยกเลิกเพราะหมดเวลา) ⇒ มีฟอร์ม
//   (c) ทิศตรงข้าม: ใบปกติ (ไม่ต้องส่งสลิป) ⇒ แบนเนอร์เขียว · หัวกล่อง "อัปโหลดสลิปชำระเงิน" · ไม่มี "ยอดที่ต้องโอน"
//   (d) ไม่มีบัญชีรับโอนในค่าตั้งของเว็บ ⇒ บอกให้ติดต่อที่พัก (ไม่ปล่อยกล่องว่าง)
// negative test: (1) ใส่สูตรตัดสินช่องสลิปของหน้าเดิมกลับ (2) แบนเนอร์เขียวเสมอแบบเดิม — ต้องล้มทั้งสองแบบ
'use strict';
const fs = require('fs');
const path = require('path');

const PAGE = process.env.SF_PAGE || path.join(__dirname, '..', 'Accounting', 'wwwroot', 'storefront.html');
const REAL_SRC = fs.readFileSync(PAGE, 'utf8');
const METHODS = ['esc', 'jsArg', '_money', '_lgGuestBanner', '_lgSlipBox'];

function extract(src, name) {
  const re = new RegExp('\\n      (?:async )?' + name + '\\(');
  const m = re.exec(src);
  if (!m) return null;
  const start = m.index + 1;
  const lineEnd = src.indexOf('\n', start);
  const line = src.slice(start, lineEnd);
  if (/\},\s*$/.test(line)) return line.replace(/,\s*$/, '');
  const end = src.indexOf('\n      },', start);
  if (end < 0) return null;
  return src.slice(start, end + '\n      }'.length);
}

function makeStore(src) {
  const parts = METHODS.map(n => {
    const t = extract(src, n);
    if (!t) throw new Error('หาเมธอด ' + n + ' ใน storefront.html ไม่เจอ — ถูกลบ/เปลี่ยนชื่อ?');
    return t;
  });
  // eslint-disable-next-line no-new-func
  return new Function('return ({\n' + parts.join(',\n') + '\n});')();
}

const base = {
  reservationNumber: 'RES-A-2610-0001', status: 'Pending', depositRequired: 5450, depositPaid: 0, balanceDue: 5450,
  propertyPhone: '081-111-2222', propertyLineId: '@resort', slipUploadBlocked: false, paymentSlipUrl: null,
};

function run(src) {
  const fails = [];
  let S;
  try { S = makeStore(src); } catch (e) { return [e.message]; }
  let ppCalls = [];
  S.makePromptPayPayload = (id, amt) => { ppCalls.push([id, amt]); return 'PAYLOAD-' + amt; };
  const pay = { promptPayId: '0812345678', bankName: 'กสิกร<b>', bankAccountNumber: '123-4-56789-0', bankAccountName: 'บจก. ริมน้ำ "จริง"' };

  // (a) ต้องส่งสลิปก่อน
  const slipRes = Object.assign({}, base, {
    slipRequired: true, canUploadSlip: true, amountToTransfer: 2500, slipDueAt: '2026-10-02T05:30:00Z',
    guestStatusLabel: 'ยังไม่สำเร็จ — รอสลิปโอนเงิน',
    guestNote: 'การจองยังไม่สำเร็จ — กรุณาโอน ฿2,500.00 และส่งสลิปภายใน 02/10/2026 12:30 น. <x>',
  });
  const banner = S._lgGuestBanner(slipRes, 'การจอง RES-A-2610-0001 — การจองยังไม่สำเร็จ');
  if (banner.includes('lg-badge ok')) fails.push('(a) ต้องส่งสลิป ⇒ แบนเนอร์ห้ามเป็นสีเขียว (ยังไม่สำเร็จ)');
  if (banner.includes('จองสำเร็จ')) fails.push('(a) หน้าห้ามแต่งคำว่า "จองสำเร็จ" เอง');
  if (!banner.includes('&lt;x&gt;') || banner.includes('<x>')) fails.push('(a) ข้อความถึงแขกต้องแสดงและหนีอักขระ');
  const box = S._lgSlipBox(slipRes, "tok'en", pay);
  if (!box.includes('ยอดที่ต้องโอน') || !box.includes('2,500.00')) fails.push('(a) ยอดที่ต้องโอนต้องเป็น amountToTransfer ของเซิร์ฟเวอร์');
  if (box.includes('5,450.00')) fails.push('(a) ห้ามใช้ depositRequired/balanceDue แทนยอดที่ต้องโอน');
  if (!box.includes('ส่งสลิปภายใน')) fails.push('(a) ต้องแสดงกำหนดเวลาส่งสลิป');
  if (!box.includes('<form') || !box.includes('lodgingUploadSlip')) fails.push('(a) ต้องมีช่องส่งสลิป');
  if (!box.includes("tok\\'en") || box.includes("'tok'en'")) fails.push('(a) token ใน onsubmit ต้องผ่าน jsArg');
  if (box.includes('กสิกร<b>') || !box.includes('กสิกร&lt;b&gt;') || box.includes('"จริง"')) fails.push('(a) บัญชีรับโอนต้องหนีอักขระ');
  if (!box.includes('123-4-56789-0') || !box.includes('0812345678')) fails.push('(a) ต้องแสดงบัญชี/พร้อมเพย์จากค่าตั้งของเว็บ');
  if (!ppCalls.some(c => c[1] === 2500)) fails.push('(a) QR พร้อมเพย์ต้องใช้ยอดของเซิร์ฟเวอร์ (amountToTransfer)');

  // (b) เซิร์ฟเวอร์ตัดสินช่องส่งสลิป
  if (S._lgSlipBox(Object.assign({}, slipRes, { canUploadSlip: false }), 't', pay) !== '')
    fails.push('(b) canUploadSlip=false ⇒ ห้ามมีฟอร์ม (หน้าห้ามตัดสินจากสถานะเอง)');
  const expired = Object.assign({}, base, { status: 'Cancelled', balanceDue: 5450, slipRequired: false, canUploadSlip: true,
    guestStatusLabel: 'หมดเวลาส่งสลิป — การจองถูกยกเลิก', guestNote: 'หากโอนเงินไปแล้ว ส่งสลิปด้านล่างได้' });
  if (!S._lgSlipBox(expired, 't', pay).includes('<form')) fails.push('(b) ใบที่ระบบยกเลิกเพราะหมดเวลา (เซิร์ฟเวอร์เปิดช่อง) ⇒ ต้องมีฟอร์ม');

  // (c) ทิศตรงข้าม: ใบปกติ
  const normal = Object.assign({}, base, { status: 'Confirmed', slipRequired: false, canUploadSlip: true, amountToTransfer: null, guestNote: null,
    guestStatusLabel: 'ยืนยันแล้ว' });
  const nb = S._lgGuestBanner(normal, 'จองสำเร็จ เลขที่ RES-A-2610-0001');
  if (!nb.includes('lg-badge ok')) fails.push('(c) ใบปกติที่ยืนยันแล้ว ⇒ แบนเนอร์สีเขียวตามเดิม');
  const nbox = S._lgSlipBox(normal, 't', pay);
  if (!nbox.includes('อัปโหลดสลิปชำระเงิน') || nbox.includes('ยอดที่ต้องโอน') || nbox.includes('ส่งสลิปภายใน'))
    fails.push('(c) ใบปกติ ⇒ หัวกล่องเดิม ไม่มียอดที่ต้องโอน/กำหนดเวลา');
  if (S._lgGuestBanner(normal, null) !== '') fails.push('(c) ไม่มีแบนเนอร์และไม่มีข้อความ ⇒ ว่าง');

  // (d) ไม่มีบัญชีรับโอน
  const nopay = S._lgSlipBox(slipRes, 't', null);
  if (!nopay.includes('ติดต่อที่พัก') || !nopay.includes('081-111-2222')) fails.push('(d) ไม่มีบัญชีรับโอน ⇒ บอกให้ติดต่อที่พักพร้อมเบอร์');
  return fails;
}

const real = run(REAL_SRC);
const negs = [
  ['สูตรช่องสลิปของหน้าเดิม', REAL_SRC.replace("if (r.canUploadSlip !== true) return '';",
    "if (!((r.status === 'Pending' || (r.status === 'Confirmed' && r.balanceDue > 0)) && !r.slipUploadBlocked)) return '';")],
  ['แบนเนอร์เขียวเสมอแบบเดิม', REAL_SRC.replace("let html = banner ? box(banner, warn ? '' : 'ok') : '';", "let html = banner ? box(banner, 'ok') : '';")],
];
const negResults = negs.map(([name, src]) => [name, src === REAL_SRC ? ['แทนรุ่นเดิมไม่ได้ (โค้ดขยับ — ปรับ sim)'] : run(src), src === REAL_SRC]);
if (real.length) {
  console.log('❌ lodging_slip_confirm_sim: หน้าการจองของแขกผิดพฤติกรรม');
  real.forEach(f => console.log('   ' + f));
  process.exit(1);
}
const silent = negResults.filter(([, f, same]) => same || !f.length);
if (silent.length) {
  silent.forEach(([n, f, same]) => console.log(`❌ lodging_slip_confirm_sim: negative test "${n}" ${same ? f[0] : 'ไม่ล้ม — sim จับรุ่นเดิมไม่ได้'}`));
  process.exit(1);
}
console.log(`✅ lodging_slip_confirm_sim: ต้องส่งสลิป ⇒ ไม่ใช่ "จองสำเร็จ" · ยอด/เวลา/ช่องสลิปจากเซิร์ฟเวอร์ · บัญชีรับโอนหนีอักขระ · ใบปกติคงเดิม · รุ่นเดิมถูกจับ (${negResults.map(([, f]) => f.length).join('/')} ข้อ)`);
