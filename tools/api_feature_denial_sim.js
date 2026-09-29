// ล็อกพฤติกรรม "403 ฟีเจอร์/แพ็กเกจ" ของ api.js ด้วย **โค้ดจริง** (ไม่ใช่โค้ดที่เขียนซ้ำในเทสต์) — รอบ 200 คำตัดสินข้อ 24
//
// ที่มา (ทีม S Q4 · review200-S): api.js พา "ทั้งหน้า" ไปหน้าแพ็กเกจเมื่อคำขอใดก็ได้ได้ 403 FEATURE_NOT_AVAILABLE ⇒ เมื่อเปิดบังคับแพ็กเกจ
// หน้าเอกสารขาย (ที่โหลดรายการบัญชีธนาคาร/ตัวแนะนำ AI เบื้องหลัง) ของลูกค้าแพ็กเกจเล็กจะดีดผู้ใช้ออกทั้งที่งานหลัก (ขาย) อยู่ในแพ็กเกจ
// คำตัดสิน: คำขอเบื้องหลัง ⇒ แจ้งเตือนไม่ขวาง · ดีดไปหน้าแพ็กเกจเฉพาะเมื่อโหลดหลักของหน้าถูกปฏิเสธ (เซิร์ฟเวอร์ส่งชื่อฟีเจอร์มาด้วย)
//
// ทิศที่ล็อกพร้อมกัน (F2 ข้อ 8):
//   ✓ 403 ของฟีเจอร์อื่น (เบื้องหลัง) → ไม่ redirect · เตือนครั้งเดียวต่อฟีเจอร์ · ผู้เรียกยังได้ error ที่มี code/feature
//   ✓ 403 ของฟีเจอร์ของหน้าเอง (โหลดหลัก) → redirect ไปหน้าแพ็กเกจ (ทิศตรงข้าม — ด่านยังทำงาน)
//   ✓ การสมัครสมาชิกถูกยกเลิก/ระงับ → redirect เสมอ (พฤติกรรมเดิม)
//   ✓ ไม่รู้ฟีเจอร์ของหน้า / เซิร์ฟเวอร์ไม่ส่งชื่อฟีเจอร์ → เตือน ไม่ดีด
// และ negative test ของตัว sim เอง (F2 ข้อ 6): ใส่พฤติกรรมเดิม (ดีดทุก 403) กลับเข้าไปในซอร์ส แล้วชุดเดียวกันต้องล้ม
'use strict';
const fs = require('fs');
const path = require('path');

const SRC = fs.readFileSync(path.join(__dirname, '..', 'Accounting', 'wwwroot', 'js', 'api.js'), 'utf8');

function load(src, pageFeature) {
  const timers = [];
  const toasts = [];
  const window = { location: { href: '/pages/documents.html', pathname: '/pages/documents.html' }, addEventListener() {} };
  const Layout = {
    _companiesLoaded: true,
    toast: (msg, type) => toasts.push({ msg, type }),
    currentPageFeature: () => pageFeature,
  };
  const plan = [];
  const fetch = () => {
    const p = plan.shift();
    return Promise.resolve({
      status: p.status, ok: p.status >= 200 && p.status < 300,
      headers: { get: () => 'application/json' },
      json: async () => p.body,
      text: async () => '',
    });
  };
  const fakeEl = () => ({ style: {}, classList: { add() {}, remove() {}, contains: () => false }, setAttribute() {}, appendChild() {}, querySelector: () => fakeEl() });
  const document = {
    body: { appendChild() {} }, head: { appendChild() {} },
    addEventListener() {}, createElement: fakeEl, getElementById: () => null,
    querySelector: () => null, querySelectorAll: () => [],
  };
  const sandbox = {
    localStorage: { getItem: () => null, setItem() {}, removeItem() {} },
    sessionStorage: { getItem: () => null, setItem() {} },
    document, window, fetch, Layout,
    setTimeout: (fn, ms) => { timers.push({ fn, ms }); return timers.length; }, clearTimeout() {},
    setInterval: () => 0, clearInterval() {},
    CSS: { escape: (s) => s }, console,
  };
  const names = Object.keys(sandbox);
  // eslint-disable-next-line no-new-func
  const fn = new Function(...names, src + '\n;return { API };');
  const { API } = fn(...names.map(n => sandbox[n]));
  const runTimers = () => { while (timers.length) timers.shift().fn(); };
  return { API, plan, toasts, window, runTimers };
}

const denied = (feature, code = 'FEATURE_NOT_AVAILABLE') => ({
  status: 403,
  body: { success: false, data: null, message: `ฟีเจอร์ "${feature}" ไม่อยู่ในแพ็กเกจของคุณ — โปรดอัพเกรด`, code, feature, upgradeUrl: '/pages/subscription.html' },
});

async function suite(src, quiet) {
  let fail = 0;
  const check = (name, cond, extra = '') => {
    if (!quiet) console.log(`  ${cond ? '✓' : '✗'} ${name}${!cond && extra ? ' — ' + extra : ''}`);
    if (!cond) fail++;
  };
  const say = (t) => { if (!quiet) console.log(t); };
  const call = async (env, url) => { try { await env.API.get(url); return null; } catch (e) { return e; } };

  {
    say('1. หน้าเอกสารขาย (DocumentEngine) โหลดบัญชีธนาคารเบื้องหลังแล้วได้ 403 BankReconciliation — ต้องไม่ดีดออก');
    const env = load(src, 'DocumentEngine');
    env.plan.push(denied('BankReconciliation'));
    const err = await call(env, '/api/companies/c/bank/transactions');
    env.runTimers();
    check('ไม่พาไปหน้าแพ็กเกจ', env.window.location.href === '/pages/documents.html', env.window.location.href);
    check('เตือน 1 ครั้ง (ไม่ใช่ error สีแดง)', env.toasts.length === 1 && env.toasts[0].type === 'warning', JSON.stringify(env.toasts));
    check('ผู้เรียกยังได้ error ที่มี code + feature', err && err.code === 'FEATURE_NOT_AVAILABLE' && err.feature === 'BankReconciliation');
  }
  {
    say('2. คำขอเบื้องหลังหลายตัวของฟีเจอร์เดียวกัน — เตือนครั้งเดียว');
    const env = load(src, 'DocumentEngine');
    env.plan.push(denied('AI_Features'), denied('AI_Features'), denied('AI_Features'));
    await call(env, '/api/companies/c/ai/payment-terms/suggest');
    await call(env, '/api/companies/c/ai/vat/infer-type');
    await call(env, '/api/companies/c/ai/discount/suggest');
    env.runTimers();
    check('เตือน 1 ครั้ง', env.toasts.length === 1, String(env.toasts.length));
    check('ไม่ดีดออก', env.window.location.href === '/pages/documents.html');
  }
  {
    say('3. ทิศตรงข้าม: โหลดหลักของหน้าเงินเดือน (Payroll) ถูกปฏิเสธ Payroll — ต้องพาไปหน้าแพ็กเกจ');
    const env = load(src, 'Payroll');
    env.plan.push(denied('Payroll'));
    const err = await call(env, '/api/companies/c/payroll/runs');
    env.runTimers();
    check('พาไปหน้าแพ็กเกจ', env.window.location.href === '/pages/subscription.html', env.window.location.href);
    check('error ยังถูกโยนให้ผู้เรียก', err && err.code === 'FEATURE_NOT_AVAILABLE');
  }
  {
    say('4. การสมัครสมาชิกถูกยกเลิก/ระงับ — พาไปหน้าแพ็กเกจเสมอ (พฤติกรรมเดิม)');
    const env = load(src, 'DocumentEngine');
    env.plan.push(denied(null, 'SUBSCRIPTION_INACTIVE'));
    await call(env, '/api/companies/c/documents');
    env.runTimers();
    check('พาไปหน้าแพ็กเกจ', env.window.location.href === '/pages/subscription.html', env.window.location.href);
  }
  {
    say('5. ไม่รู้ฟีเจอร์ของหน้า (หน้าไม่มีในเมนู) — เตือน ไม่ดีด');
    const env = load(src, null);
    env.plan.push(denied('Payroll'));
    await call(env, '/api/companies/c/payroll/runs');
    env.runTimers();
    check('ไม่ดีดออก', env.window.location.href === '/pages/documents.html');
    check('เตือน', env.toasts.length === 1);
  }
  {
    say('6. ตัวตัดสิน featureDenialAction (ตารางเล็ก)');
    const { API } = load(src, null);
    check('ฟีเจอร์ตรงหน้า → redirect', API.featureDenialAction({ code: 'FEATURE_NOT_AVAILABLE', feature: 'FixedAssets' }, 'FixedAssets') === 'redirect');
    check('ฟีเจอร์อื่น → notify', API.featureDenialAction({ code: 'FEATURE_NOT_AVAILABLE', feature: 'AI_Features' }, 'FixedAssets') === 'notify');
    check('ไม่มีชื่อฟีเจอร์ → notify', API.featureDenialAction({ code: 'FEATURE_NOT_AVAILABLE' }, 'FixedAssets') === 'notify');
    check('ยกเลิก/ระงับ → redirect', API.featureDenialAction({ code: 'SUBSCRIPTION_INACTIVE' }, null) === 'redirect');
    check('body ว่าง → notify', API.featureDenialAction(null, 'X') === 'notify');
  }
  return fail;
}

(async () => {
  console.log('api.js — 403 ฟีเจอร์/แพ็กเกจ (รอบ 200 ข้อ 24)');
  const fail = await suite(SRC, false);

  // ── negative test: พฤติกรรมเดิม "ดีดทุก 403" ต้องทำให้ชุดนี้ล้ม ──
  const broken = SRC.replace(
    /featureDenialAction\(body, pageFeature\) \{/,
    "featureDenialAction(body, pageFeature) { return 'redirect';");
  let negFail = -1;
  if (broken === SRC) {
    console.log('✗ negative test: หาจุดใส่บั๊กในซอร์สไม่เจอ (ชื่อเมธอดเปลี่ยน?) — sim ไม่มีด่าน');
  } else {
    negFail = await suite(broken, true);
    console.log(negFail > 0
      ? `✓ negative test: ใส่พฤติกรรมเดิม (ดีดทุก 403) แล้วชุดเดียวกันล้ม ${negFail} ข้อ`
      : '✗ negative test: ใส่พฤติกรรมเดิมแล้วยังผ่าน — sim ไม่มีด่าน');
  }
  const ok = fail === 0 && negFail > 0;
  console.log(ok ? 'ผ่าน' : `ล้ม (${fail} ข้อ)`);
  process.exit(ok ? 0 : 1);
})();
