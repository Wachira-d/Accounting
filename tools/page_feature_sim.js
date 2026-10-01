// ล็อก "ฟีเจอร์ของหน้า = ฟีเจอร์ของ route ข้อมูลหลักที่เซิร์ฟเวอร์บอก" ด้วย **โค้ดจริง** จาก layout.js — รอบ 200 ทีม Z (ฝ่ายค้านรอบสอง S2-3)
//
// ที่มา: api.js ดีดไปหน้าแพ็กเกจเฉพาะเมื่อฟีเจอร์ใน 403 ตรงกับ `Layout.currentPageFeature()` (คำตัดสินข้อ 24) · เดิมค่านี้มาจาก "เมนู" ของหน้า
// ⇒ cms-orders/cms-bookings (เมนู CmsWebsiteBuilder · route หลัก CmsEcommerce/CmsBooking) และ document-scan (เมนู AI_Features · route `/ocr` = DocumentOCR)
// หลังกดบังคับ: โหลดหลักได้ 403 แต่ถือเป็น "คำขอเบื้องหลัง" ⇒ toast ครั้งเดียว + หน้าว่าง
// กติกา: เซิร์ฟเวอร์ส่ง `pageFeatures` (SubscriptionGatePolicy.PageMainFeatures — ตารางเส้นทางตัวเดียวกับ middleware) มากับ /api/subscription ·
// หน้าที่อยู่ในตาราง ⇒ ค่าจากเซิร์ฟเวอร์ (null = route หลักไม่ถูก gate) · ไม่อยู่/แคชเก่าไม่มีช่องนี้ ⇒ ฟีเจอร์ของเมนู (เดิม)
//
// เมธอดถูกตัดมาจากซอร์สจริง + negative test: ถอดการอ่าน pageFeatures แล้วชุดเดียวกันต้องล้ม
'use strict';
const fs = require('fs');
const path = require('path');

const SRC = fs.readFileSync(path.join(__dirname, '..', 'Accounting', 'wwwroot', 'js', 'layout.js'), 'utf8');

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
  const part = extractMethod(src, 'currentPageFeature');
  if (!part) return null;
  // eslint-disable-next-line no-new-func
  return new Function(`return { subscription: null, currentPage: null, navItems: [], ${part} };`)();
}

const NAV = [
  { id: 'cms-orders', href: '/pages/cms-orders.html', feature: 'CmsWebsiteBuilder' },
  { id: 'cms-bookings', href: '/pages/cms-bookings.html', feature: 'CmsWebsiteBuilder' },
  { id: 'lodging', href: '/pages/lodging.html', feature: 'CmsWebsiteBuilder' },
  { id: 'document-scan', href: '/pages/document-scan.html', feature: 'AI_Features' },
  { id: 'payroll', href: '/pages/payroll.html', feature: 'Payroll' },
];
// ค่าที่ SubscriptionGatePolicy.PageMainFeatures คืน (เทสต์ C# `S23_PageMainFeatures_*` ล็อกฝั่งเซิร์ฟเวอร์)
const SERVER = { 'cms-orders': 'CmsEcommerce', 'cms-bookings': 'CmsBooking', lodging: null, 'document-scan': 'DocumentOCR' };

function suite(src, quiet) {
  let fail = 0;
  const check = (name, cond, extra = '') => {
    if (!quiet) console.log(`  ${cond ? '✓' : '✗'} ${name}${!cond && extra ? ' — ' + extra : ''}`);
    if (!cond) fail++;
  };
  const L = build(src);
  if (!L) { if (!quiet) console.log('  ✗ หาเมธอด currentPageFeature ใน layout.js ไม่เจอ'); return 99; }
  L.navItems = NAV;
  const on = (page, sub) => { L.currentPage = page; L.subscription = sub; return L.currentPageFeature(); };

  const sub = { featureGateMode: 'Enforce', pageFeatures: SERVER };
  check('cms-orders = CmsEcommerce (route หลัก ไม่ใช่เมนู)', on('cms-orders', sub) === 'CmsEcommerce', on('cms-orders', sub));
  check('cms-bookings = CmsBooking', on('cms-bookings', sub) === 'CmsBooking', on('cms-bookings', sub));
  check('document-scan = DocumentOCR', on('document-scan', sub) === 'DocumentOCR', on('document-scan', sub));
  check('lodging = null (route หลักไม่ถูก gate ⇒ ทุก 403 เป็นเบื้องหลัง)', on('lodging', sub) === null, String(on('lodging', sub)));
  // ทิศตรงข้าม — หน้าที่ไม่อยู่ในตาราง ต้องได้ฟีเจอร์ของเมนูเหมือนเดิม
  check('payroll (ไม่อยู่ในตาราง) = Payroll จากเมนู', on('payroll', sub) === 'Payroll', on('payroll', sub));
  check('แคชเก่าไม่มี pageFeatures = ฟีเจอร์ของเมนู (เดิม)', on('cms-orders', { featureGateMode: 'Enforce' }) === 'CmsWebsiteBuilder');
  check('ยังไม่มีข้อมูลแพ็กเกจ = ฟีเจอร์ของเมนู', on('document-scan', null) === 'AI_Features');
  check('หน้าไม่อยู่ในเมนู = null', on('unknown-page', sub) === null);
  return fail;
}

console.log('layout.js — ฟีเจอร์ของหน้าตาม route ข้อมูลหลักที่เซิร์ฟเวอร์บอก (รอบ 200 S2-3)');
const fail = suite(SRC, false);
const broken = SRC.replace(/const pf = this\.subscription && this\.subscription\.pageFeatures;/, 'const pf = null;');
let negFail = -1;
if (broken === SRC) {
  console.log('✗ negative test: หาจุดอ่าน pageFeatures ในซอร์สไม่เจอ — sim ไม่มีด่าน');
} else {
  negFail = suite(broken, true);
  console.log(negFail > 0
    ? `✓ negative test: ถอดการอ่าน pageFeatures (กลับไปใช้เมนู) แล้วชุดเดียวกันล้ม ${negFail} ข้อ`
    : '✗ negative test: ถอดแล้วยังผ่าน — sim ไม่มีด่าน');
}
const ok = fail === 0 && negFail > 0;
console.log(ok ? 'ผ่าน' : `ล้ม (${fail} ข้อ)`);
process.exit(ok ? 0 : 1);
