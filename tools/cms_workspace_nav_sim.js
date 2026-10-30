// ล็อก "พื้นที่ทำงานเว็บไซต์" (รอบ 215 เฟส 1 · erp-review/2026-10-30/CMS_UX_REDESIGN.md) ด้วย **โค้ดจริง** ของ layout.js + cms-site-context.js
//
// ที่มา: เจ้าของงงว่าเมนูซ้าย "คำสั่งซื้อจากเว็บ / ตั้งค่าร้านค้า / การจองคิวจากเว็บ / ตั้งค่าที่พัก …" เป็นของเว็บไหน — ชื่อไม่บอกเว็บ
// และโผล่ตามโมดูลรวมทั้งบริษัท · การ์ด "ภาพรวมทุกไซต์" เปิดการจองของเว็บแรก / ที่พักไม่มี siteId
// กติกาที่ล็อก:
//  (ก) หน้ารายเว็บ 7 หน้า (workspaceTab) ไม่วาดบนแถบซ้ายแล้ว (คลาส nav-subhidden — ช่องค้นหายังเจอ) · มีทางเข้า "พื้นที่ทำงานเว็บไซต์" แทน
//  (ข) role ที่ได้แค่ "lodging" ⇒ ทางเข้าพาไป lodging.html ตรง (ไม่มีกุญแจสิทธิ์ใหม่ · ไม่มีใครหาหน้าไม่เจอ) · role ที่ไม่มีงานเว็บเลย ⇒ ไม่มีทางเข้า
//  (ค) แท็บของเว็บสปา (modules=[bookings]) ไม่มี Front desk/ตั้งค่าร้าน · แท็บกรองตามสิทธิ์เมนูเดิม
//  (ง) ลิงก์ภาพรวมทุกไซต์เป็น ?siteId=all จริงทุกตัว (การจองด้วย) · ไม่มีลิงก์ที่พักแบบ "ทุกไซต์" ปลอม
//  (จ) ชื่อหมวดมาจากเซิร์ฟเวอร์ (cmsNavLabel) · เว็บล่าสุดที่ไม่ใช้งานของหน้า ⇒ ไม่ถูกเลือก (กันตารางว่าง) · ?siteId= ที่ไม่มีที่พัก ⇒ null (ไม่ตกไปแห่งแรก)
// negative tests: ถอดแต่ละด่านออกจากซอร์สจริงแล้วชุดเดียวกันต้องล้ม
'use strict';
const fs = require('fs');
const path = require('path');
const vm = require('vm');

const ROOT = path.join(__dirname, '..', 'Accounting', 'wwwroot', 'js');
const LAYOUT = fs.readFileSync(path.join(ROOT, 'layout.js'), 'utf8');
const CTX = fs.readFileSync(path.join(ROOT, 'cms-site-context.js'), 'utf8');

function storage() {
  const m = new Map();
  return { getItem: k => (m.has(k) ? m.get(k) : null), setItem: (k, v) => m.set(k, String(v)), removeItem: k => m.delete(k) };
}

/** โหลด layout.js ทั้งไฟล์ใน vm (DOM ปลอมขั้นต่ำ) → คืน Layout + nav ปลอมที่จับ innerHTML */
function loadLayout(src) {
  const nav = { innerHTML: '' };
  const document = {
    querySelector: (q) => (q === '.sidebar-nav' ? nav : null),
    querySelectorAll: () => [],
    getElementById: () => null,
    addEventListener: () => {},
    createElement: () => ({ style: {}, appendChild() {}, setAttribute() {} }),
  };
  const ctx = { console, localStorage: storage(), document, navigator: {}, setTimeout, clearTimeout, requestAnimationFrame: () => {}, location: { pathname: '/', search: '' } };
  ctx.window = ctx;
  vm.createContext(ctx);
  try { vm.runInContext(src + '\n;globalThis.__L = Layout;', ctx); } catch (e) { return { err: e.message }; }
  return { L: ctx.__L, nav, ctx };
}

function loadCtx(src, layoutStub) {
  const listeners = {};
  const ctx = { console, window: {}, location: { href: '' },
    document: { getElementById: () => null, addEventListener: (t, f) => { (listeners[t] = listeners[t] || []).push(f); } } };
  ctx.__listeners = listeners;
  ctx.window.Layout = layoutStub;
  ctx.Layout = layoutStub;
  vm.createContext(ctx);
  try { vm.runInContext(src, ctx); } catch (e) { return null; }
  const C = ctx.window.CmsSiteContext;
  if (C) { C.__ctx = ctx; }
  return C;
}

function navHtml(L, nav, opts) {
  L.currentCompany = { id: 'c1' };
  L.currentPage = opts.page || 'dashboard';
  L.myPermissions = opts.perms;
  L._cmsModules = new Set(opts.modules);
  L._cmsNavLabel = opts.navLabel || undefined;
  L.subscription = null;
  L.features = [];
  L.ctxLocal = null;
  nav.innerHTML = '';
  L._refreshNavMenu();
  return nav.innerHTML;
}

function anchor(html, navId) {
  const re = new RegExp(`<a [^>]*data-nav-id="${navId}"[^>]*>`);
  const m = re.exec(html);
  return m ? m[0] : null;
}

const WS = ['cms-orders', 'cms-store-settings', 'cms-bookings', 'cms-booking-providers', 'lodging', 'lodging-settings', 'cms-leads'];

function layoutSuite(src, quiet) {
  let fail = 0;
  const check = (name, cond, extra = '') => { if (!quiet) console.log(`  ${cond ? '✓' : '✗'} ${name}${!cond && extra ? ' — ' + extra : ''}`); if (!cond) fail++; };
  const r = loadLayout(src);
  if (!r.L) { if (!quiet) console.log('  ✗ โหลด layout.js ใน vm ไม่ได้: ' + r.err); return 99; }
  const { L, nav, ctx } = r;
  ctx.localStorage.setItem('uiMode', 'advanced');
  const owner = { isOwnerOrAdmin: true, allowedMenuIds: ['*'] };

  let html = navHtml(L, nav, { perms: owner, modules: ['orders', 'bookings', 'lodging', 'leads'], navLabel: 'เว็บไซต์ & การจอง' });
  const entry = anchor(html, 'cms-workspace');
  check('(ก) มีทางเข้า "พื้นที่ทำงานเว็บไซต์"', !!entry && html.includes('พื้นที่ทำงานเว็บไซต์'));
  const hiddenAll = WS.every(id => { const a = anchor(html, id); return a && /nav-subhidden/.test(a); });
  check('(ก) หน้ารายเว็บทั้ง 7 ไม่วาดบนแถบซ้าย (พับไว้ ค้นหาเจอ)', hiddenAll, WS.filter(id => !/nav-subhidden/.test(anchor(html, id) || '')).join(','));
  // สภาพในภาพหน้าจอของเจ้าของ: อยู่หน้า "เว็บไซต์ของฉัน" แล้วเมนูลูกกางทั้งหมด — ต้องพับอยู่
  const onSites = navHtml(L, nav, { perms: owner, modules: ['orders', 'bookings', 'lodging', 'leads'], page: 'cms-sites' });
  check('(ก) อยู่หน้าเว็บไซต์ทั้งหมด: หน้ารายเว็บยังพับ (ไม่กาง 7 รายการแบบเดิม)', WS.every(id => /nav-subhidden/.test(anchor(onSites, id) || '')));
  check('(ก) "เว็บไซต์ทั้งหมด" ยังแสดงปกติ', !!anchor(html, 'cms-sites') && !/nav-subhidden/.test(anchor(html, 'cms-sites')));
  check('(จ) ชื่อหมวดมาจากเซิร์ฟเวอร์', html.includes('>เว็บไซต์ &amp; การจอง<') || html.includes('>เว็บไซต์ & การจอง<'));
  html = navHtml(L, nav, { perms: owner, modules: ['orders', 'leads'], navLabel: 'เว็บไซต์ & ร้านออนไลน์' });
  check('(จ) บริษัทร้านค้าอย่างเดียว ⇒ ชื่อหมวด "เว็บไซต์ & ร้านออนไลน์"', html.includes('ร้านออนไลน์'));
  check('(ก) Portal ลูกค้าไม่อยู่หมวดเว็บแล้ว', (() => {
    const sec = html.indexOf('data-section="เว็บไซต์ & การจอง"'); const a = html.indexOf('data-nav-id="customer-portal"');
    return sec >= 0 && a >= 0 && a < sec;
  })());

  // (ข) role ที่ได้แค่ Front desk
  html = navHtml(L, nav, { perms: { isOwnerOrAdmin: false, allowedMenuIds: ['lodging'] }, modules: ['lodging'] });
  const e2 = anchor(html, 'cms-workspace');
  check('(ข) role ที่ได้แค่ lodging ⇒ ทางเข้าพาไป lodging.html ตรง', !!e2 && /href="\/pages\/lodging\.html/.test(e2), e2 || 'ไม่มีทางเข้า');
  html = navHtml(L, nav, { perms: { isOwnerOrAdmin: false, allowedMenuIds: ['documents'] }, modules: ['lodging'] });
  check('(ข) role ที่ไม่มีงานเว็บเลย ⇒ ไม่มีทางเข้า (ไม่ใช่กุญแจสิทธิ์ใหม่ที่ทุกคนเห็น)', !anchor(html, 'cms-workspace'));
  // เว็บ/แท็บล่าสุด
  ctx.localStorage.setItem('cmsLastSite:c1:anon', JSON.stringify({ siteId: 's-spa', page: 'cms-bookings' }));
  html = navHtml(L, nav, { perms: owner, modules: ['bookings', 'leads'] });
  check('ทางเข้า = แท็บล่าสุดของเว็บล่าสุด', /href="\/pages\/cms-bookings\.html\?siteId=s-spa"/.test(anchor(html, 'cms-workspace') || ''), anchor(html, 'cms-workspace'));
  html = navHtml(L, nav, { perms: owner, modules: ['bookings', 'leads'], page: 'cms-bookings' });
  check('อยู่หน้างานเว็บ ⇒ ทางเข้าเป็น active', /class="nav-item active"/.test(anchor(html, 'cms-workspace') || ''));
  ctx.localStorage.setItem('cmsLastSite:c1:anon', JSON.stringify({ siteId: 's-x', page: 'lodging' }));
  html = navHtml(L, nav, { perms: owner, modules: ['bookings'] });
  check('(N1) เว็บล่าสุดแยกต่อผู้ใช้', (() => { L.user = { id: 'u2' }; const v = L.cmsLastSite(); L.user = null; return v === null; })());
  check('(N4) ไม่มี data-nav-id="undefined"', !/data-nav-id="undefined"/.test(html));
  check('แท็บล่าสุดที่ผู้ใช้ไม่เห็นแล้ว ⇒ ไม่ใช้ (ไปแท็บงานแรกที่เห็น)', /href="\/pages\/cms-bookings\.html"/.test(anchor(html, 'cms-workspace') || ''), anchor(html, 'cms-workspace'));
  return fail;
}

/** S4: Layout ประกาศ 'layout:permissions' หลังโหลดสิทธิ์ (หัวพื้นที่ทำงานวาดใหม่ตามนั้น) */
async function permEventSuite(src, quiet) {
  let fail = 0;
  const check = (name, cond) => { if (!quiet) console.log(`  ${cond ? '✓' : '✗'} ${name}`); if (!cond) fail++; };
  const r = loadLayout(src);
  if (!r.L) return 99;
  const fired = [];
  r.ctx.document.dispatchEvent = (e) => fired.push(e && e.type);
  r.ctx.CustomEvent = function (type) { this.type = type; };
  r.ctx.API = { get: async () => ({ success: true, data: { isOwnerOrAdmin: false, allowedMenuIds: ['lodging'] } }) };
  vm.runInContext('globalThis.API = API; globalThis.CustomEvent = CustomEvent;', r.ctx);
  r.L.currentCompany = { id: 'c1' }; r.L.loadVatRegistration = () => {};
  await r.L.loadMyPermissions();
  check('(S4) โหลดสิทธิ์แล้วประกาศ layout:permissions', fired.includes('layout:permissions'));
  return fail;
}

const SITES = [
  { id: 's-spa', name: 'test-spa', modules: ['bookings', 'leads'], industryType: 'Beauty', status: 'Published', subdomain: 'test-spa' },
  { id: 's-hotel', name: 'b1', modules: ['lodging'], industryType: 'Hotel', status: 'Published', subdomain: 'b1' },
  { id: 's-shop', name: 'b3', modules: ['orders', 'bookings'], industryType: 'Retail', status: 'Draft', subdomain: 'b3' },
];

function ctxSuite(src, quiet) {
  let fail = 0;
  const check = (name, cond, extra = '') => { if (!quiet) console.log(`  ${cond ? '✓' : '✗'} ${name}${!cond && extra ? ' — ' + extra : ''}`); if (!cond) fail++; };
  let allowed = null; let last = null; let cleared = 0; const toasts = [];
  const stub = {
    hasMenuAccess: (id) => !allowed || allowed.includes(id),
    cmsLastSite: () => last, setCmsLastSite() {}, clearCmsLastSite() { cleared++; last = null; }, toast(m) { toasts.push(m); },
    esc: s => String(s), enumLabel: (k, v) => String(v),
  };
  const C = loadCtx(src, stub);
  if (!C) { if (!quiet) console.log('  ✗ โหลด cms-site-context.js ไม่ได้'); return 99; }
  const pages = (site) => C.workspaceTabs(site).map(t => t.page);
  const spa = pages(SITES[0]);
  check('(ค) เว็บสปา: มีการจอง + สรุปผู้ให้บริการ + Lead + หน้าเว็บ', ['cms-bookings', 'cms-booking-providers', 'cms-leads', 'cms-edit'].every(p => spa.includes(p)), spa.join(','));
  check('(ค) เว็บสปา: ไม่มี Front desk / ตั้งค่าที่พัก / คำสั่งซื้อ / ตั้งค่าร้าน', !['lodging', 'lodging-settings', 'cms-orders', 'cms-store-settings'].some(p => spa.includes(p)), spa.join(','));
  check('(ค) แท็บเรียง งานเข้า → รายงาน → ตั้งค่า → หน้าเว็บ', spa[spa.length - 1] === 'cms-edit' && spa.indexOf('cms-bookings') < spa.indexOf('cms-booking-providers'));
  check('(ค) ทุกแท็บพก siteId ของเว็บนั้น', C.workspaceTabs(SITES[1]).every(t => t.href.includes('siteId=s-hotel')));
  allowed = ['lodging'];
  const fd = pages(SITES[1]);
  check('(ค) role Front desk: เว็บโรงแรมเห็นแค่ Front desk (ไม่เห็นตั้งค่าที่พัก/หน้าเว็บ)', fd.length === 1 && fd[0] === 'lodging', fd.join(','));
  allowed = null;
  const all = C.allLinks(SITES).map(l => l.href);
  check('(ง) ภาพรวมทุกไซต์เป็น ?siteId=all จริงทุกลิงก์ (รวมการจอง)', all.length > 0 && all.every(h => h.endsWith('?siteId=all')) && all.some(h => h.includes('cms-bookings')), all.join(','));
  check('(ง) ไม่มีลิงก์ที่พักแบบ "ทุกไซต์" (ยังไม่มีโหมดรวม)', !all.some(h => h.includes('lodging')));
  check('(ง) โมดูลที่ไม่มีเว็บใดใช้ ⇒ ไม่มีลิงก์ภาพรวม', !C.allLinks([SITES[1]]).length);
  last = { siteId: 's-hotel', page: 'lodging' };
  check('(จ) เว็บล่าสุดที่ไม่ใช้งานของหน้า ⇒ ไม่ถูกเลือก', C.pick(SITES, null, { module: 'bookings' }) === 's-spa');
  last = { siteId: 's-shop', page: 'cms-orders' };
  check('(จ) เว็บล่าสุดที่ใช้งานของหน้า ⇒ ถูกเลือก', C.pick(SITES, null, { module: 'bookings' }) === 's-shop');
  check('(จ) ?siteId= ในลิงก์ชนะเว็บล่าสุดเสมอ', C.pick(SITES, 's-spa', { module: 'bookings' }) === 's-spa');
  last = null;
  const props = [{ id: 'p1', siteId: 's-hotel' }, { id: 'p2', siteId: null }];
  check('(จ) ?siteId= ที่ไม่มีที่พัก ⇒ null (ไม่ตกไปแห่งแรก)', C.pickProperty(props, null, 's-spa') === null);
  check('(จ) ?siteId= ที่มีที่พัก ⇒ ที่พักนั้น', C.pickProperty(props, null, 's-hotel') === 'p1');
  check('(จ) ?propertyId= ชนะ', C.pickProperty(props, 'p2', 's-hotel') === 'p2');
  last = { siteId: 's-hotel', page: 'lodging' };
  // ── ฝ่ายค้านรอบ 215 ──
  last = null;
  check('(S2) ?siteId= ที่ไม่อยู่ในรายการเว็บ (ค้าง/ลบ) ⇒ ตกไปลำดับถัดไป ไม่ใช่ทางตัน', C.pickProperty(props, null, 's-gone', SITES) === 'p1');
  check('(S2) ทิศตรงข้าม: ?siteId= ที่อยู่ในรายการแต่ไม่มีที่พัก ⇒ null (บอกให้สร้าง)', C.pickProperty(props, null, 's-spa', SITES) === null);
  last = { siteId: 's-gone', page: 'lodging' }; cleared = 0;
  C.pickProperty(props, null, null, SITES);
  check('(S2) เว็บล่าสุดที่ไม่อยู่ในรายการ ⇒ ล้าง', cleared === 1);
  last = { siteId: 's-gone', page: 'cms-bookings' }; cleared = 0;
  C.pick(SITES, null, { module: 'bookings' });
  check('(S2) pick: เว็บล่าสุดค้าง ⇒ ล้าง', cleared === 1);
  last = { siteId: 's-hotel', page: 'lodging' }; cleared = 0;
  C.pick(SITES, null, { module: 'bookings' });
  check('(S2) ทิศตรงข้าม: เว็บล่าสุดที่ยังอยู่แต่ไม่ใช้งานนี้ ⇒ ไม่ล้าง', cleared === 0);
  // S1: ตัวสลับเว็บ — ผู้ใช้ Front desk
  allowed = ['lodging'];
  let wsHandler = null; const wsEl = { value: 's-hotel', addEventListener: (t, f) => { wsHandler = f; } };
  const el = { innerHTML: '', isConnected: true, querySelector: (q) => (q === '#cmsWsSite' ? wsEl : null) };
  C.renderCrumb(el, SITES, 's-hotel', 'Front desk', 'lodging');
  check('(S1) ตัวสลับของผู้ใช้ Front desk ไม่มีชื่อเว็บสปา/ร้าน', el.innerHTML.includes('value="s-hotel"') && !el.innerHTML.includes('value="s-spa"') && !el.innerHTML.includes('value="s-shop"'));
  wsEl.value = 's-spa'; C.__ctx.location.href = ''; toasts.length = 0;
  if (wsHandler) wsHandler();
  check('(S1) เลือกเว็บที่ไม่มีแท็บ ⇒ ไม่พาไปหน้าเว็บ (ถูกปฏิเสธ) + บอก + คืนค่า', C.__ctx.location.href === '' && toasts.length === 1 && wsEl.value === 's-hotel');
  allowed = null;
  const el2 = { innerHTML: '', isConnected: true, querySelector: () => null };
  C.renderCrumb(el2, SITES, 's-hotel', 'Front desk', 'lodging');
  check('(S1) ทิศตรงข้าม: เจ้าของเห็นทุกเว็บในตัวสลับ', ['s-spa', 's-hotel', 's-shop'].every(id => el2.innerHTML.includes(`value="${id}"`)));
  // S4: วาดใหม่เมื่อสิทธิ์มาถึง
  check('(S4) ก่อนรู้สิทธิ์: แท็บตั้งค่าเว็บแสดง', el2.innerHTML.includes('cms-site-settings.html'));
  allowed = ['lodging'];
  (C.__ctx.__listeners['layout:permissions'] || []).forEach(f => f());
  check('(S4) สิทธิ์มาถึง ⇒ หัววาดใหม่ ไม่มีแท็บที่ไม่มีสิทธิ์', !el2.innerHTML.includes('cms-site-settings.html') && el2.innerHTML.includes('lodging.html'));
  // ── เฟส 2: ศูนย์ "ตั้งค่าเว็บ" ──
  allowed = null;
  const hubTabs = pages(SITES[2]);
  check('(P2) ผู้เห็นศูนย์ตั้งค่า: มีแท็บ "ตั้งค่าเว็บ" และไม่มีแท็บตั้งค่าร้าน/ที่พักซ้ำ', hubTabs.includes('cms-site-settings') && !hubTabs.includes('cms-store-settings'), hubTabs.join(','));
  allowed = ['cms-orders', 'cms-store-settings'];
  const storeOnly = pages(SITES[2]);
  check('(P2) ทิศตรงข้าม: ผู้มีสิทธิ์แค่ตั้งค่าร้าน ยังเห็นแท็บตั้งค่าร้านเดิม (ไม่หาย)', storeOnly.includes('cms-store-settings') && !storeOnly.includes('cms-site-settings'), storeOnly.join(','));
  allowed = null;
  last = { siteId: 's-hotel', page: 'lodging' };
  check('(จ) ไม่มีพารามิเตอร์ ⇒ ที่พักของเว็บล่าสุด', C.pickProperty([{ id: 'p0', siteId: null }].concat(props), null, null) === 'p1');
  return fail;
}

function negative(label, src, mutate, suite) {
  const broken = mutate(src);
  if (broken === src) { console.log(`✗ negative test (${label}): หาจุดที่จะถอดในซอร์สไม่เจอ — sim ไม่มีด่าน`); return false; }
  const n = suite(broken, true);
  console.log(n > 0 ? `✓ negative test (${label}): ล้ม ${n === 99 ? 'ทั้งชุด' : n + ' ข้อ'}` : `✗ negative test (${label}): ถอดแล้วยังผ่าน — sim ไม่มีด่าน`);
  return n > 0;
}

console.log('layout.js — แถบซ้าย: ทางเข้าพื้นที่ทำงานเว็บ (รอบ 215)');
const f1 = layoutSuite(LAYOUT, false);
console.log('cms-site-context.js — แท็บ/ภาพรวม/เลือกเว็บ (รอบ 215)');
const f2 = ctxSuite(CTX, false);
const negs = [
  negative('ถอดการพับแท็บรายเว็บ', LAYOUT, s => s.replace('(it.workspaceTab && wsEntryShown)', '(false)'), layoutSuite),
  negative('ทางเข้าโผล่โดยไม่ดูว่าเห็นแท็บไหม', LAYOUT, s => s.replace('if (wsTabs.length === 0) items.splice(wsEntryIdx, 1);', 'if (false) items.splice(wsEntryIdx, 1);'), layoutSuite),
  negative('ชื่อหมวดไม่อ่านจากเซิร์ฟเวอร์', LAYOUT, s => s.replace('(currentSection.cmsNavLabel && this._cmsNavLabel)', '(false)'), layoutSuite),
  negative('แท็บไม่กรองสิทธิ์', CTX, s => s.replace('return links.filter(l => canSee(l.menuId || l.page))', 'return links'), ctxSuite),
  negative('แท็บไม่ดูโมดูลของเว็บ', CTX, s => s.replace('return MODULE_ORDER.filter(m => mods.includes(m))', 'return MODULE_ORDER'), ctxSuite),
  negative('ภาพรวมการจองกลับไปเปิดเว็บแรก', CTX, s => s.replace("'cms-bookings': { label: '📅 การจอง',     href: '/pages/cms-bookings.html?siteId=all' }", "'cms-bookings': { label: '📅 การจอง', href: '/pages/cms-bookings.html' }"), ctxSuite),
  negative('?siteId= ที่ไม่มีที่พักตกไปแห่งแรก', CTX, s => s.replace('return b ? b.id : null; }', 'if (b) return b.id; }'), ctxSuite),
];
(async () => {
console.log('layout.js — ประกาศเมื่อสิทธิ์โหลดเสร็จ (ฝ่ายค้าน S4)');
const f3 = await permEventSuite(LAYOUT, false);
const brokenPerm = LAYOUT.replace("document.dispatchEvent(new CustomEvent('layout:permissions'))", 'void 0');
const negPerm = brokenPerm !== LAYOUT && (await permEventSuite(brokenPerm, true)) > 0;
console.log(negPerm ? '✓ negative test (ไม่ประกาศสิทธิ์): ล้ม' : '✗ negative test (ไม่ประกาศสิทธิ์): ยังผ่าน — sim ไม่มีด่าน');
negs.push(negPerm);
negs.push(negative('ตัวสลับแสดงทุกเว็บ', CTX, s => s.replace('const usable = sites.filter(s => (site && s.id === site.id) || this.workspaceTabs(s).length > 0);', 'const usable = sites;'), ctxSuite));
negs.push(negative('ไม่มีแท็บแล้วพาไปหน้าเว็บ', CTX, s => s.replace("if (first) { location.href = first.href; return; }", "location.href = first ? first.href : CONTENT_LINK.href(target.id); return;"), ctxSuite));
negs.push(negative('?siteId= ค้างเป็นทางตัน', CTX, s => s.replace('const known = !rawSiteId', 'const known = true || !rawSiteId'), ctxSuite));
negs.push(negative('ไม่ล้างเว็บล่าสุดที่ค้าง', CTX, s => s.replace("if (last && !sites.some(s => s.id === last.siteId) && typeof Layout.clearCmsLastSite === 'function') Layout.clearCmsLastSite();", ''), ctxSuite));
negs.push(negative('ซ่อนแท็บตั้งค่าร้านแม้ไม่มีสิทธิ์ศูนย์', CTX, s => s.replace('.filter(l => !(hub && HUB_COVERS.includes(l.page)))', '.filter(l => !HUB_COVERS.includes(l.page))'), ctxSuite));
negs.push(negative('ไม่วาดใหม่เมื่อสิทธิ์มาถึง', CTX, s => s.replace("document.addEventListener('layout:permissions', () => CmsSiteContext.rerender());", ''), ctxSuite));
const ok = f1 === 0 && f2 === 0 && f3 === 0 && negs.every(Boolean);
console.log(ok ? 'ผ่าน' : `ล้ม (${f1 + f2} ข้อ · negative ${negs.filter(x => !x).length})`);
process.exit(ok ? 0 : 1);
})();
