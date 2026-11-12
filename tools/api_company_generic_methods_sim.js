// รอบ 263: ล็อกว่า `API.c(cid)` (ที่ `Layout.api()` คืน) มีเมธอดทั่วไป get/post/put/del ด้วยโค้ดจริงใน wwwroot/js/api.js
//
// ที่มา: ผู้ใช้รายงาน "โหลดส่วน ลิงก์ชำระเงิน & ส่งเอกสารอัตโนมัติ ไม่สำเร็จ — api.get is not a function" —
// หน้าเรียก `Layout.api().get(url)` 8 จุด (documents · email-schedule · settings) แต่วัตถุต่อบริษัทไม่มี .get
//
// ทิศที่ล็อก: ✓ get/post/put/del มีและส่งต่อ URL/ข้อมูลเดิมถึง API.request · ✓ เมธอดเฉพาะเดิม (dashboard) ยังอยู่
// negative test ของ sim เอง (F2 ข้อ 6): ถอดบรรทัดเติม .get ออกจากซอร์ส ⇒ ชุดเดียวกันต้องล้ม
// และสแกนหน้าเว็บ: ทุกจุดที่เรียก `<ตัวแปรจาก Layout.api()>.get|post|put|del(` ต้องมีเมธอดนั้นจริง
'use strict';
const fs = require('fs');
const path = require('path');

const ROOT = path.join(__dirname, '..', 'Accounting', 'wwwroot');
const SRC = fs.readFileSync(path.join(ROOT, 'js', 'api.js'), 'utf8');

function load(src) {
  const calls = [];
  const sandbox = {
    localStorage: { getItem: () => null, setItem: () => {}, removeItem: () => {} },
    sessionStorage: { getItem: () => null, setItem: () => {} },
    document: { addEventListener() {}, querySelector: () => null, getElementById: () => null, body: { appendChild() {} }, createElement: () => ({ style: {}, setAttribute() {}, appendChild() {} }) },
    window: { addEventListener() {}, location: { href: '/x' } },
    fetch: async () => ({ ok: true, status: 200, headers: { get: () => 'application/json' }, json: async () => ({}) }),
    setTimeout, clearTimeout, setInterval, clearInterval, CSS: { escape: (s) => s }, console,
  };
  const names = Object.keys(sandbox);
  // eslint-disable-next-line no-new-func
  const fn = new Function(...names, src + '\n;return { API };');
  const { API } = fn(...names.map(n => sandbox[n]));
  API.request = (method, url, data) => { calls.push({ method, url, data }); return Promise.resolve({ ok: true }); };
  return { API, calls };
}

function run(src) {
  const fails = [];
  let env;
  try { env = load(src); } catch (e) { return [`โหลด api.js ไม่ได้: ${e.message}`]; }
  const o = env.API.c('cid-1');
  for (const m of ['get', 'post', 'put', 'del'])
    if (typeof o[m] !== 'function') fails.push(`API.c(cid).${m} ไม่มี (หน้าจะได้ "api.${m} is not a function")`);
  if (typeof o.dashboard !== 'function') fails.push('เมธอดเฉพาะเดิม (dashboard) หาย');
  if (!fails.length) {
    o.get('/api/companies/cid-1/x');
    o.post('/api/companies/cid-1/y', { a: 1 });
    const g = env.calls[0], p = env.calls[1];
    if (!g || g.method !== 'GET' || g.url !== '/api/companies/cid-1/x') fails.push('get ไม่ส่งต่อ URL เดิมเป็น GET');
    if (!p || p.method !== 'POST' || !p.data || p.data.a !== 1) fails.push('post ไม่ส่งต่อข้อมูลเดิม');
  }
  return fails;
}

let bad = 0;
const ok = run(SRC);
if (ok.length) { bad++; console.log('❌ api.js ปัจจุบัน:\n  ' + ok.join('\n  ')); }

// negative: ถอดการเติม .get ⇒ ต้องล้ม
const broken = SRC.replace(/if \(!o\.get\) o\.get = [^\n]*\n/, '');
if (broken === SRC) { bad++; console.log('❌ negative test: หาบรรทัดเติม .get ในซอร์สไม่เจอ (sim ต้องอัปเดต)'); }
else if (!run(broken).length) { bad++; console.log('❌ negative test: ถอด .get แล้ว sim ยังผ่าน = ไม่มีด่าน'); }

// สแกนหน้าเว็บ: ตัวแปรที่มาจาก Layout.api()/API.c(...) ที่เรียกเมธอดทั่วไป — ต้องเป็นชุดที่ API.c มี
const allowed = new Set(['get', 'post', 'put', 'del']);
const files = [];
for (const dir of ['pages', 'js', 'admin']) {
  const d = path.join(ROOT, dir);
  if (!fs.existsSync(d)) continue;
  for (const f of fs.readdirSync(d)) if (/\.(html|js)$/.test(f)) files.push(path.join(d, f));
}
let scanned = 0;
for (const f of files) {
  const t = fs.readFileSync(f, 'utf8');
  const vars = new Set();
  for (const m of t.matchAll(/\b(?:const|let|var)\s+(\w+)\s*=\s*(?:Layout\.api\(\)|API\.c\()/g)) vars.add(m[1]);
  for (const v of vars) {
    for (const m of t.matchAll(new RegExp(`\\b${v}\\.(patch|delete|request)\\(`, 'g'))) {
      bad++; console.log(`❌ ${path.relative(ROOT, f)}: ${v}.${m[1]}( — API.c(cid) ไม่มีเมธอดนี้ (ใช้ ${[...allowed].join('/')} หรือเมธอดเฉพาะ)`);
    }
    scanned++;
  }
}

if (bad) { console.log(`❌ api_company_generic_methods_sim: ${bad} ข้อ`); process.exit(1); }
console.log(`✅ api_company_generic_methods_sim: API.c(cid) มี get/post/put/del · negative test ล้มตามคาด · สแกนตัวแปร ${scanned} ตัว`);
