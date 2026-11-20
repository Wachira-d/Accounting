// รอบ 277 (งาน A) — ล็อก "ไม่มีสิทธิ์แดชบอร์ด ⇒ ไม่เห็นเมนู และไม่ถูกส่งไปแดชบอร์ด" ด้วย **โค้ดจริง** จาก layout.js
//
// ที่มา: เจ้าของรายงาน "เกิดข้อผิดพลาด: ไม่มีสิทธิ์ “ดูแดชบอร์ดภาพรวม” (Reports.Dashboard) … ถ้าสิทธิ์ดูไม่ได้ก็ซ่อนเมนูนั้นไม่ให้เห็นไปเลย" (2026-11-20) ·
// กติกา: เซิร์ฟเวอร์ส่ง `permissionDeniedMenuIds` (ตาราง Helpers/MenuPermissionMap — แดชบอร์ด = Reports.Dashboard) · `hasMenuAccess` ตัด ·
// `landingUrl` = แดชบอร์ดถ้ามีสิทธิ์ ไม่งั้นเมนูแรกที่เข้าได้ ไม่มีเลย/ยังไม่ได้รับสิทธิ์ = /pages/no-access.html
//
// เมธอดตัดจากซอร์สจริง (hasMenuAccess · landingUrl · dashboardUrl · _navLockState · billingHub) — navItems/ที่ซ่อนเป็นข้อมูลทดสอบ ·
// negative test: ถอดการตรวจสิทธิ์แดชบอร์ดใน landingUrl ⇒ ชุดเดียวกันต้องล้ม
'use strict';
const fs = require('fs');
const path = require('path');

const SRC = fs.readFileSync(path.join(__dirname, '..', 'Accounting', 'wwwroot', 'js', 'layout.js'), 'utf8');

function extractBlock(src, re, head) {
  const m = re.exec(src);
  if (!m) return null;
  let i = m.index + m[0].length, depth = 1;
  for (; i < src.length && depth > 0; i++) {
    const ch = src[i];
    if (ch === '{') depth++;
    else if (ch === '}') depth--;
  }
  return head(m) + src.slice(m.index + m[0].length, i);
}
const method = (src, name) => extractBlock(src, new RegExp('\\n\\s*' + name + '\\s*\\(([^)]*)\\)\\s*\\{'), m => `${name}(${m[1]}) {`);
const prop = (src, name) => extractBlock(src, new RegExp('\\n\\s*' + name + '\\s*:\\s*\\{'), () => `${name}: {`);

function build(src) {
  const parts = ['hasMenuAccess', 'landingUrl', 'dashboardUrl', '_navLockState'].map(n => method(src, n));
  if (parts.some(p => !p)) return null;
  const hub = prop(src, 'billingHub');
  // eslint-disable-next-line no-new-func
  return new Function('localStorage', `return { myPermissions: null, subscription: null, hidden: [], ${hub ? hub + ',' : ''}
    getHiddenMenuItems() { return this.hidden; },
    hasFeature() { return true; },
    navItems: [
      { id: 'dashboard', href: '/app.html' },
      { id: 'getting-started', href: '/pages/getting-started.html' },
      { id: 'documents', href: '/pages/documents.html?side=revenue' },
      { id: 'payment-intents', href: '/pages/payment-intents.html' },
      { id: 'contacts', href: '/pages/contacts.html' },
    ],
    ${parts.join(',\n')} };`)({ getItem: () => 'advanced' });
}

function suite(src, quiet) {
  let fail = 0;
  const check = (name, cond) => { if (!quiet) console.log(`  ${cond ? '✓' : '✗'} ${name}`); if (!cond) fail++; };
  const L = build(src);
  if (!L) { if (!quiet) console.log('  ✗ หาเมธอดใน layout.js ไม่เจอ'); return 99; }

  // ผู้ใช้ไม่ผูกบทบาท ("*") ที่ไม่มี Reports.Dashboard — กรณีที่เจ้าของรายงาน
  L.myPermissions = { isOwnerOrAdmin: false, allowedMenuIds: ['*'], permissionDeniedMenuIds: ['dashboard', 'payment-intents'] };
  check('ไม่มีสิทธิ์แดชบอร์ด: ไม่เห็นเมนูแดชบอร์ด', L.hasMenuAccess('dashboard') === false);
  check('ไม่มีสิทธิ์แดชบอร์ด: หน้าแรก = เมนูแรกที่เข้าได้ (ข้ามคู่มือเริ่มต้น) ไม่ใช่แดชบอร์ด', L.landingUrl() === '/pages/documents.html?side=revenue');
  L.hidden = ['documents'];
  check('เมนูที่เจ้าของซ่อน ไม่ใช่หน้าแรก', L.landingUrl() === '/pages/contacts.html');
  L.hidden = [];
  // บทบาทกำหนดเองที่ติ๊กเมนูแดชบอร์ดแต่ไม่ได้ติ๊กสิทธิ์ Reports.Dashboard
  L.myPermissions = { isOwnerOrAdmin: false, allowedMenuIds: ['dashboard', 'contacts'], permissionDeniedMenuIds: ['dashboard'] };
  check('บทบาทติ๊กเมนูแดชบอร์ดแต่ไม่มีสิทธิ์: ซ่อน', L.hasMenuAccess('dashboard') === false);
  check('บทบาทติ๊กเมนูแดชบอร์ดแต่ไม่มีสิทธิ์: หน้าแรก = ผู้ติดต่อ', L.landingUrl() === '/pages/contacts.html');
  // ไม่มีเมนูไหนเข้าได้เลย
  L.myPermissions = { isOwnerOrAdmin: false, allowedMenuIds: [], permissionDeniedMenuIds: ['dashboard'] };
  check('ไม่มีเมนูที่เข้าได้: หน้าต้อนรับ "ยังไม่ได้รับสิทธิ์"', L.landingUrl() === '/pages/no-access.html');
  // ตอบรับคำเชิญแบบยังไม่กำหนดบทบาท
  L.myPermissions = { isOwnerOrAdmin: false, allowedMenuIds: [], accessPending: true };
  check('ยังไม่ได้รับสิทธิ์: ไม่เห็นเมนูใด', L.hasMenuAccess('documents') === false && L.hasMenuAccess('my-signature') === false);
  check('ยังไม่ได้รับสิทธิ์: หน้าแรก = หน้าต้อนรับ', L.landingUrl() === '/pages/no-access.html');
  check('หน้าต้อนรับเข้าได้เสมอ', L.hasMenuAccess('no-access') === true);
  // ทิศตรงข้าม
  L.myPermissions = { isOwnerOrAdmin: false, allowedMenuIds: ['*'], permissionDeniedMenuIds: null };
  check('ทิศตรงข้าม: มีสิทธิ์แดชบอร์ด = เห็นเมนู + หน้าแรกคือแดชบอร์ด', L.hasMenuAccess('dashboard') === true && L.landingUrl() === '/app.html');
  L.myPermissions = { isOwnerOrAdmin: true, allowedMenuIds: [], permissionDeniedMenuIds: null };
  check('ทิศตรงข้าม: เจ้าของ = แดชบอร์ด', L.landingUrl() === '/app.html');
  L.myPermissions = null;
  check('ทิศตรงข้าม: ยังไม่รู้สิทธิ์ = ไม่ซ่อน (ไม่กระพริบ) + แดชบอร์ด (หน้าแดชบอร์ดตรวจซ้ำเอง)', L.hasMenuAccess('dashboard') === true && L.landingUrl() === '/app.html');
  return fail;
}

console.log('menu_landing_sim — โค้ดจริงจาก layout.js');
const fails = suite(SRC, false);
const mutated = SRC.replace("if (this.hasMenuAccess('dashboard')) return this.dashboardUrl();", 'return this.dashboardUrl();');
const negFails = mutated === SRC ? 0 : suite(mutated, true);
if (mutated === SRC) console.log('  ✗ negative test: หาบรรทัดตรวจสิทธิ์แดชบอร์ดใน landingUrl ไม่เจอ (แก้ sim)');
else console.log(`  ${negFails > 0 ? '✓' : '✗'} negative test: ถอดการตรวจสิทธิ์แดชบอร์ดแล้วชุดเดียวกันล้ม ${negFails} ข้อ`);
if (fails || mutated === SRC || negFails === 0) { console.log('❌ menu_landing_sim ล้ม'); process.exit(1); }
console.log('✅ menu_landing_sim ผ่านทุกทิศ');
