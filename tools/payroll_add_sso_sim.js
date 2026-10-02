// ล็อกพฤติกรรมโมดัล "➕ เพิ่มพนักงานเข้ารอบ" ฝั่ง ปกส. ด้วย **โค้ดจริง** จาก payroll.html (รอบ 202)
// ที่มา: ผู้ใช้รายงาน "แก้ไขยอดประกันสังคมไม่ได้" — พนักงานเงินเดือน 30,000 เข้ากลางเดือน แก้เงินเดือนงวดนี้เป็น 7,000
//   แต่ฐาน ปกส. ค้าง 30,000 (เติมจากข้อมูลพนักงานครั้งเดียว) ⇒ ปกส. 875 (เพดาน) และช่องยอดสมทบเป็น readonly แก้ไม่ได้
// สัญญาที่ล็อก (สองทิศ):
//   1. ฐานที่ระบบเติม "ตามเงินเดือน" ต้องตามช่องเงินเดือน (30,000 → 7,000 ⇒ ฐาน 7,000 · ลูกจ้าง 350 · นายจ้าง 350)
//   2. ผู้ใช้แก้ฐานเองแล้ว ⇒ แก้เงินเดือนต่อ ฐานต้องไม่ถูกทับ (ค่าผู้ใช้ชนะ)
//   3. ช่องยอดสมทบฝั่งลูกจ้างพิมพ์ได้ในโหมดเพิ่ม ⇒ ย้อนหาฐานให้ + ฝั่งนายจ้างตาม · ฝั่งนายจ้างยัง readonly
//   4. ฐาน > รายได้งวดนี้ ⇒ ขึ้นคำเตือน · ฐาน ≤ รายได้ ⇒ ไม่ขึ้น
// negative test ในตัว: ถอดการตามเงินเดือนออกจากซอร์สจริง แล้วสัญญาข้อ 1 ต้องล้ม
'use strict';
const fs = require('fs');
const path = require('path');
const vm = require('vm');

const SRC = fs.readFileSync(path.join(__dirname, '..', 'Accounting/wwwroot/pages/payroll.html'), 'utf8');

function scriptOf(src) {
  const m = [...src.matchAll(/<script(?![^>]*src)[^>]*>([\s\S]*?)<\/script>/g)].map(x => x[1]);
  const s = m.find(x => x.includes('const Page = {'));
  if (!s) throw new Error('ไม่พบ const Page ใน payroll.html');
  return s;
}

function makeEnv() {
  const els = {};
  const mkEl = (id) => {
    const el = { id, value: '', dataset: {}, style: {}, innerHTML: '', textContent: '', disabled: false,
      insertAdjacentHTML(_, h) { this.innerHTML = h + this.innerHTML; } };
    els[id] = el; return el;
  };
  const document = {
    getElementById: (id) => els[id] || null,
    createElement: () => mkEl('_tmp' + Math.random()),
    addEventListener() {}, querySelectorAll: () => [], querySelector: () => null,
  };
  const Layout = { esc: (s) => String(s ?? ''), money: (n) => Number(n || 0).toFixed(2), toast() {}, api: () => null,
    date: (d) => String(d), openModal() {}, closeModal() {} };
  const sandbox = { document, Layout, window: {}, console, localStorage: { getItem: () => null, setItem() {} },
    setTimeout, clearTimeout, URLSearchParams, location: { search: '' }, navigator: {}, alert() {}, confirm: () => true, prompt: () => null };
  sandbox.window = sandbox;
  return { sandbox, els, mkEl };
}

function loadPage(src) {
  const env = makeEnv();
  const code = scriptOf(src).replace(/const Page = \{/, 'globalThis.Page = {');
  try { vm.runInNewContext(code, env.sandbox, { timeout: 2000 }); }
  catch (e) { if (!env.sandbox.Page) throw e; }   // โค้ดเริ่มหน้า (DOMContentLoaded ฯลฯ) ไม่ใช่ส่วนที่ทดสอบ
  const Page = env.sandbox.Page;
  if (!Page) throw new Error('โหลด Page ไม่ได้');
  // ช่องของโมดัล + กล่องสรุป
  ['baseSalary','overtimePay','allowances','commission','bonus','otherIncome','socialSecurityBase','socialSecurityEmployee',
   'withholdingTax','providentFundEmployee','loanDeduction','otherDeductions','socialSecurityEmployer','providentFundEmployer',
   'employee','taxBasis','defaultNote','ssoWarn'].forEach(k => env.mkEl('ed_' + k));
  ['edGross','edDed','edNet','edNetOld','edDiffHint'].forEach(k => env.mkEl(k));
  Page._curRun = { id: 'r1', ssoRatePercent: 5, ssoEmployerRatePercent: 5, ssoWageCeiling: 17500 };
  Page._edMode = 'add';
  Page._edAddable = [{ id: 'e1', employeeCode: 'EMP-0024', employeeName: 'ทดสอบ', salaryType: 'Monthly', baseSalary: 30000,
    isSubjectToSocialSecurity: true, hasProvidentFund: false }];
  return { Page, els: env.els };
}

const fails = [];
const check = (cond, msg) => { if (!cond) fails.push(msg); };
const num = (els, k) => Number(els['ed_' + k].value || 0);

function typeInto(Page, els, k, v) {
  const el = els['ed_' + k]; el.value = String(v); el.dataset.userTouched = '1'; Page._edRecalc(k);
}

function run(src, label) {
  const local = [];
  const ok = (c, m) => { if (!c) local.push(m); };
  // 1) ฐานตามเงินเดือน
  {
    const { Page, els } = loadPage(src);
    els.ed_employee.value = 'e1'; Page._edAddPick();
    ok(num(els, 'socialSecurityBase') === 30000, `${label}: ค่าเริ่มต้นฐานควร 30,000 ได้ ${num(els, 'socialSecurityBase')}`);
    typeInto(Page, els, 'baseSalary', 7000);
    ok(num(els, 'socialSecurityBase') === 7000, `${label}: แก้เงินเดือน 7,000 แล้วฐานต้องตาม = 7,000 ได้ ${num(els, 'socialSecurityBase')}`);
    ok(num(els, 'socialSecurityEmployee') === 350, `${label}: ปกส. ลูกจ้างต้อง 350 ได้ ${num(els, 'socialSecurityEmployee')}`);
    ok(num(els, 'socialSecurityEmployer') === 350, `${label}: ปกส. นายจ้างต้อง 350 ได้ ${num(els, 'socialSecurityEmployer')}`);
    ok(els.ed_ssoWarn.style.display !== 'block', `${label}: ฐาน = รายได้ ไม่ควรเตือน`);
  }
  // 2) ผู้ใช้แก้ฐานเองแล้ว ฐานไม่ถูกทับ
  {
    const { Page, els } = loadPage(src);
    els.ed_employee.value = 'e1'; Page._edAddPick();
    typeInto(Page, els, 'socialSecurityBase', 5000);
    typeInto(Page, els, 'baseSalary', 7000);
    ok(num(els, 'socialSecurityBase') === 5000, `${label}: ฐานที่ผู้ใช้พิมพ์ต้องไม่ถูกทับ ได้ ${num(els, 'socialSecurityBase')}`);
    ok(num(els, 'socialSecurityEmployee') === 250, `${label}: ฐาน 5,000 ⇒ 250 ได้ ${num(els, 'socialSecurityEmployee')}`);
  }
  // 3) ยอดสมทบลูกจ้างพิมพ์ได้ + ย้อนฐาน · นายจ้าง readonly
  {
    const { Page, els } = loadPage(src);
    const html = Page._edInputsHtml(Page._edFields.concat(Page._edAddExtraFields), () => '', true);
    const tag = (k) => (html.match(new RegExp(`<input[^>]*id="ed_${k}"[^>]*>`)) || [''])[0];
    ok(!/readonly/.test(tag('socialSecurityEmployee')), `${label}: ช่อง ปกส. ลูกจ้างในโหมดเพิ่มต้องพิมพ์ได้`);
    ok(/readonly/.test(tag('socialSecurityEmployer')), `${label}: ช่อง ปกส. นายจ้างในโหมดเพิ่มต้อง readonly (คิดจากฝั่งลูกจ้าง)`);
    els.ed_employee.value = 'e1'; Page._edAddPick();
    typeInto(Page, els, 'baseSalary', 7000);
    typeInto(Page, els, 'socialSecurityEmployee', 300);
    ok(num(els, 'socialSecurityBase') === 6000, `${label}: พิมพ์ ปกส. 300 ⇒ ฐานย้อน 6,000 ได้ ${num(els, 'socialSecurityBase')}`);
    ok(num(els, 'socialSecurityEmployer') === 300, `${label}: นายจ้างตามลูกจ้าง 300 ได้ ${num(els, 'socialSecurityEmployer')}`);
    typeInto(Page, els, 'baseSalary', 8000);
    ok(num(els, 'socialSecurityBase') === 6000, `${label}: หลังพิมพ์ยอดสมทบเอง ฐานต้องเลิกตามเงินเดือน ได้ ${num(els, 'socialSecurityBase')}`);
  }
  // 4) เตือนฐาน > รายได้
  {
    const { Page, els } = loadPage(src);
    els.ed_employee.value = 'e1'; Page._edAddPick();
    typeInto(Page, els, 'socialSecurityBase', 30000);
    typeInto(Page, els, 'baseSalary', 7000);
    ok(els.ed_ssoWarn.style.display === 'block', `${label}: ฐาน 30,000 > รายได้ 7,000 ต้องเตือน`);
  }
  return local;
}

fails.push(...run(SRC, 'ซอร์สจริง'));

// negative test: ถอดการตามเงินเดือนออกจากซอร์สจริง ⇒ สัญญาข้อ 1 ต้องล้ม
const broken = SRC.replace("if (changed === 'baseSalary' && this._edMode === 'add') {", "if (false) {");
if (broken === SRC) fails.push('negative test: หา anchor การตามเงินเดือนไม่เจอ (ซอร์สเปลี่ยน — ปรับ sim)');
else {
  const neg = run(broken, 'ถอดการตามเงินเดือน');
  if (!neg.some(m => m.includes('ฐานต้องตาม'))) fails.push('negative test: ถอดการตามเงินเดือนแล้ว sim ไม่ฟ้อง (sim ไม่มีฟัน)');
}

if (fails.length) { console.error('❌ payroll_add_sso_sim:\n  ' + fails.join('\n  ')); process.exit(1); }
console.log('✅ payroll_add_sso_sim: ฐาน ปกส. ตามเงินเดือนงวดนี้ · ยอดสมทบลูกจ้างแก้ได้ · ค่าผู้ใช้ชนะ · เตือนฐาน > รายได้ (+ negative test)');
