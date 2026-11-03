// รอบ 230 (erp-review/2026-11-02/CMS_SETTINGS_UX_AUDIT.md §6 ค้างข้อ 1–4) — ล็อก "ค่าตั้งที่ย้ายเข้าศูนย์ตั้งค่าเว็บแล้ว มีที่แก้ที่เดียว" + "บันทึกการ์ดหนึ่งไม่ทับค่าของการ์ดอื่น" + wizard 6 ขั้น
// ด้วย **ซอร์สจริง** ของหน้า/สคริปต์
// กติกาที่ล็อก:
//   (ก) ที่เดิมไม่มีฟอร์มแก้ค่าที่ย้ายแล้ว (ห้ามสองฟอร์มแก้ค่าเดียวกัน — CMS_UX_REDESIGN §8): cms-edit (ติดต่อ · PDPA/นโยบาย · ค่าการจองระดับเว็บ) ·
//       cms-orders (modal สินค้าตามสั่ง) · cms-bookings (ฟอร์มเทอมคลาส) · cms-booking-providers (ช่องแก้กติกาค่าตอบแทน) · และ saveSeo ไม่ส่งช่องที่ย้ายแล้ว
//   (ข) ค่าการจองระดับเว็บ: แต่ละการ์ดส่งเฉพาะช่องของตัวเอง (null/ไม่ส่ง = เซิร์ฟเวอร์คงค่าเดิม) · สวิตช์ผู้ให้บริการส่งเฉพาะเมื่อเจ้าของกดจริง
//   (ค) สินค้าตามสั่ง: การ์ดชำระ/การ์ดวันผลิต ส่งเฉพาะช่องของตัวเอง
//   (ง) wizard: 6 ขั้น · ทุกแบบเว็บอยู่ในการ์ดธุรกิจหนึ่งใบ (หรือ "อื่น ๆ") · ทำต่อด้วย ?setup= · ศูนย์ตั้งค่าเว็บโหลดตัวแก้ทั้ง 4 ไฟล์และมีกลุ่ม mto/classes
// negative tests: ใส่บั๊กกลับเข้าซอร์สจริง ⇒ ชุดเดียวกันต้องล้ม
'use strict';
const fs = require('fs');
const path = require('path');
const vm = require('vm');

const WWW = path.join(__dirname, '..', 'Accounting', 'wwwroot');
const read = (p) => fs.readFileSync(path.join(WWW, p), 'utf8');
const SRC = {
  edit: read('pages/cms-edit.html'), orders: read('pages/cms-orders.html'), bookings: read('pages/cms-bookings.html'),
  providers: read('pages/cms-booking-providers.html'), hub: read('pages/cms-site-settings.html'), sites: read('pages/cms-sites.html'),
  bsa: read('js/booking-site-settings-admin.js'), mto: read('js/cms-mto-settings-admin.js'),
  info: read('js/cms-site-info-admin.js'), cls: read('js/class-term-admin.js'),
};

function fakeDom() {
  const els = {};
  const get = (id) => (els[id] = els[id] || { id, value: '', checked: false, innerHTML: '', textContent: '', style: {}, dataset: {},
    insertAdjacentHTML() {}, addEventListener() {}, querySelectorAll: () => [], querySelector: () => null });
  return { els, get };
}
function loadModule(src, globalName) {
  const dom = fakeDom(); const bodies = [];
  const api = {
    cmsUpdateBookingSiteSettings: async (sid, b) => { bodies.push(b); return { data: null }; },
    cmsUpdateCommerceConfig: async (sid, b) => { bodies.push(b); return { data: null }; },
  };
  const ctx = { console, Date, Number, String, Math, Object, Array, Set, Map, JSON, encodeURIComponent, Promise,
    CSS: { escape: (s) => s },
    document: { getElementById: dom.get, querySelector: () => null, querySelectorAll: () => [], body: { insertAdjacentHTML() {} } },
    Layout: { esc: (v) => String(v == null ? '' : v), api: () => api, toast() {}, jsArg: (v) => v, getCompanyId: () => 'c' } };
  ctx.window = ctx;
  vm.createContext(ctx);
  try { vm.runInContext(src, ctx); } catch (e) { return null; }
  const M = ctx[globalName];
  return M ? { M, dom, bodies } : null;
}

const has = (src, re) => (re instanceof RegExp ? re.test(src) : src.includes(re));

async function scenario(S) {
  const fails = [];
  const ok = (name, cond) => { if (!cond) fails.push(name); };

  // (ก) ที่เดิมไม่มีฟอร์มแก้ค่าที่ย้ายแล้ว
  ['id="sContactPhone"', 'name="lgMode_privacy"', 'id="sRequireConsent"', 'id="lgAcceptText"', 'id="bssDuration"', 'id="ssWeekly"',
    'id="bssFfPhone"', 'name="bssCompletionDoc"', 'id="bssProvidersEnabled"', 'id="bssStrategy"', 'id="bssReminderEnabled"', 'id="bssRequireMember"']
    .forEach(id => ok(`(ก) cms-edit ไม่มีช่อง ${id} แล้ว`, !has(S.edit, id)));
  const saveSeo = (S.edit.match(/async saveSeo\(\) \{[\s\S]*?\n {6}\},/) || [''])[0];
  ok('(ก) cms-edit saveSeo มีอยู่', saveSeo.length > 0);
  ['contactPhone', 'lineId', 'requireDataConsent', 'cookieConsentEnabled', 'dataConsentText', 'legalPayload', 'privacyPolicyUrl']
    .forEach(k => ok(`(ก) saveSeo ไม่ส่ง ${k} (ย้ายไปศูนย์ตั้งค่าเว็บ)`, !saveSeo.includes(k)));
  ['id="mtoModal"', 'id="mtoTerm"', 'id="mtoClosedDays"', 'saveMto('].forEach(id => ok(`(ก) cms-orders ไม่มี ${id} แล้ว`, !has(S.orders, id)));
  ok('(ก) cms-orders มีลิงก์ไปกลุ่ม mto', has(S.orders, /cms-site-settings\.html\?[^"']*group=mto/));
  ['id="clsTermModal"', 'id="ctService"', 'clsSaveTerm(', "prompt('สร้างรอบ"].forEach(id => ok(`(ก) cms-bookings ไม่มี ${id} แล้ว`, !has(S.bookings, id)));
  ['class="form-input pay-mode"', 'savePay(', 'PAY_MODES'].forEach(id => ok(`(ก) cms-booking-providers ไม่มี ${id} แล้ว`, !has(S.providers, id)));
  ok('(ก) ตัวแก้ใหม่มีช่องติดต่อ/นโยบาย', has(S.info, 'sgInfoPhone') && has(S.info, 'sgLgMode_'));
  ok('(ก) ค่าตอบแทน: ป้ายกติกาจากเซิร์ฟเวอร์ (ไม่มีตารางในหน้า)', has(S.bsa, 'st.modes') && !has(S.bsa, "'PerClass', 'ต่อคลาส"));

  // (ข) ค่าการจองระดับเว็บ — ส่งเฉพาะช่องของการ์ด
  const b = loadModule(S.bsa, 'BookingSiteAdmin');
  ok('(ข) โหลด BookingSiteAdmin ได้', !!b);
  if (b) {
    const { M, dom, bodies } = b;
    M._siteId = 's1'; M._d = {}; M._sched = { weekly: [{ day: 1, start: '09:00', end: '18:00', blocked: false, capacity: null }], exceptions: [] };
    dom.get('bsaDuration').value = '45'; dom.get('bsaMinAdv').value = '2'; dom.get('bsaMaxAdv').value = '14'; dom.get('bsaBufBefore').value = ''; dom.get('bsaBufAfter').value = '5';
    await M.saveRules();
    const r = bodies[0] || {};
    ok('(ข) การรับจอง: ส่งเฉพาะ 5 ช่องของตัวเอง', Object.keys(r).sort().join(',') === 'bufferAfterMinutes,bufferBeforeMinutes,durationMinutes,maxAdvanceDays,minAdvanceHours');
    ok('(ข) การรับจอง: ช่องว่าง = null (คงค่าเดิม ไม่ใช่ 0/ค่าตั้งต้น)', r.bufferBeforeMinutes === null && r.durationMinutes === 45);
    await M.saveHours();
    ok('(ข) เวลาเปิดร้าน: ส่งแค่ schedule', Object.keys(bodies[1] || {}).join(',') === 'schedule' && (bodies[1].schedule.weekly[0] || {}).dayOfWeek === 'Monday');
    dom.get('bsaRemindOn').checked = true; dom.get('bsaRemindHours').value = ''; dom.get('bsaHold').value = '30'; dom.get('bsaRemindTpl').value = '';
    await M.saveRemind();
    ok('(ข) เตือน: ไม่ส่งช่องของการ์ดอื่น', !('schedule' in (bodies[2] || {})) && !('durationMinutes' in (bodies[2] || {})) && bodies[2].reminderHoursBefore === '24,2');
    dom.get('bsaProvOn').checked = true; dom.get('bsaStrategy').value = 'LeastLoadedDay';
    M._provDirty = false;
    await M.saveProviders();
    ok('(ข) สวิตช์ผู้ให้บริการไม่ถูกประทับเมื่อเจ้าของไม่ได้กด', !('providerAllocationEnabled' in (bodies[3] || {})) && bodies[3].providerRecommendStrategy === 'LeastLoadedDay');
    M._provDirty = true;
    await M.saveProviders();
    ok('(ข) ทิศตรงข้าม: กดสวิตช์แล้ว ⇒ ส่งค่า', (bodies[4] || {}).providerAllocationEnabled === true);
  }

  // (ค) สินค้าตามสั่ง
  const m = loadModule(S.mto, 'CmsMtoAdmin');
  ok('(ค) โหลด CmsMtoAdmin ได้', !!m);
  if (m) {
    const { M, dom, bodies } = m;
    M._siteId = 's1'; M._c = {};
    dom.get('sgMtoCutoff').value = ''; dom.get('sgMtoHolidays').checked = true;
    await M.saveDays();
    const d = bodies[0] || {};
    ok('(ค) วันผลิต: ไม่ส่งเงื่อนไขชำระ/มัดจำ', !('mtoPaymentTermMode' in d) && !('mtoDepositPercent' in d) && d.clearMtoCutoffHour === true && d.mtoCutoffHour === null);
    dom.get('sgMtoTerm').value = 'deposit_percent'; dom.get('sgMtoPct').value = '30'; dom.get('sgMtoFixed').value = ''; dom.get('sgMtoForfeit').value = '';
    dom.get('sgMtoDepInv').value = ''; dom.get('sgMtoShipVat').value = '';
    await M.savePay();
    const p = bodies[1] || {};
    ok('(ค) การชำระ: ไม่ส่งวันปิด/ตัดรอบ', !('mtoClosedWeekdays' in p) && !('mtoCutoffHour' in p) && p.mtoDepositPercent === 30 && p.clearDepositForfeitOnCancelPercent === true);
  }

  // (ง) wizard + ศูนย์ตั้งค่าเว็บ
  ['wzStep1', 'wzStep2', 'wzStep3', 'wzStep4', 'wzStep5', 'wzStep6'].forEach(id => ok(`(ง) wizard มี ${id}`, has(S.sites, `id="${id}"`)));
  ok('(ง) ไม่มี modal 2 ขั้นเดิม', !has(S.sites, 'id="step1"') && !has(S.sites, 'backToTemplates'));
  ok('(ง) ทำต่อได้ด้วย ?setup=', has(S.sites, "qs.get('setup')") && has(S.sites, 'resumeWizard('));
  const tplKeys = ((S.sites.match(/const TEMPLATES = \{([\s\S]*?)\n {4}\};/) || [])[1] || '').match(/^ {6}([a-z]+): \{/gm) || [];
  ok('(ง) อ่านรายการแบบเว็บได้', tplKeys.length >= 10);
  ok('(ง) การ์ด "อื่น ๆ" แสดงทุกแบบเว็บ (tpls: null)', /key: 'other'[^\n]*tpls: null/.test(S.sites));
  ok('(ง) ขั้น 4/5 ใช้ตัวแก้เดียวกับศูนย์ (ไม่มีสำเนาฟอร์ม)', has(S.sites, 'SiteTransferCard.mount(') && has(S.sites, 'BookingSiteAdmin.mountBasics(') && has(S.sites, 'SiteShippingAdmin.mount('));
  ok('(ง) ขั้น 6 = แผงความพร้อมจากเซิร์ฟเวอร์', has(S.sites, 'CmsSetupStatus.render('));
  ['cms-site-info-admin.js', 'cms-mto-settings-admin.js', 'booking-site-settings-admin.js', 'class-term-admin.js']
    .forEach(f => ok(`(ง) ศูนย์ตั้งค่าเว็บโหลด ${f}`, has(S.hub, `/js/${f}`)));
  ok("(ง) ศูนย์มีกลุ่ม mto/classes", has(S.hub, "key: 'mto'") && has(S.hub, "key: 'classes'"));
  ok('(ง) กลุ่มการจองต่อท้ายด้วย BookingSiteAdmin.mount', has(S.hub, 'BookingSiteAdmin.mount('));
  // ฝ่ายค้านรอบ 230
  const create = (S.sites.match(/async createSite\(\) \{[\s\S]*?\n {6}\},/) || [''])[0];
  const iFresh = create.indexOf('cmsGetSite('), iSet = create.indexOf('this.wz.site = site');
  ok('(S1) wizard โหลดเว็บใหม่หลังใส่แม่แบบ ก่อนตั้ง wz.site (ขั้น 5 เห็นโมดูลจริง)', iFresh > 0 && iSet > iFresh);
  ok('(S2) หน้าการจองตัดสินลิงก์/ตัวแก้เทอมใหม่เมื่อสิทธิ์มาถึง', /layout:permissions[^\n]*clsTermsHome\(\)/.test(S.bookings));
  ok('(S2) รายงานผู้ให้บริการตัดสินการ์ดกติกาใหม่เมื่อสิทธิ์มาถึง', /layout:permissions[^\n]*payRulesHome\(\)/.test(S.providers));
  ok('(S3) กลุ่ม mto แสดงเมื่อมีสินค้า/เข้าด้วย ?group=mto', /productCount[^\n]*_wantMto[^\n]*key: 'mto'/.test(S.hub));
  ok('(S3) กลุ่มที่ขอไม่มี ⇒ บอกผู้ใช้', S.hub.includes('กลุ่มนี้ไม่มีในเว็บนี้'));
  ok('(N1) การวาดซ้อนกันไม่ต่อการ์ดการจองซ้ำ', S.hub.includes('_renderToken') && S.bsa.includes('isCurrent()'));
  ok('(ง) ตัวแก้ลงทะเบียนกลุ่ม', has(S.info, 'CmsSiteSettingsExt.general') && has(S.info, 'CmsSiteSettingsExt.legal') && has(S.mto, 'CmsSiteSettingsExt.mto') && has(S.cls, 'CmsSiteSettingsExt.classes'));
  return fails;
}

(async () => {
  let failed = 0;
  const base = await scenario(SRC);
  if (base.length) { failed++; console.log('❌ ซอร์สจริงไม่ผ่าน:\n  - ' + base.join('\n  - ')); }
  else console.log('✅ ซอร์สจริง: ค่าที่ย้ายมีที่แก้ที่เดียว · บันทึกรายการ์ดไม่ทับกัน · wizard 6 ขั้น');

  // negative tests — ใส่บั๊กกลับแล้วต้องล้ม
  const neg = [
    ['ช่องติดต่อกลับมาใน cms-edit', Object.assign({}, SRC, { edit: SRC.edit.replace('<div class="sg-moved"', '<input id="sContactPhone"><div class="sg-moved"') })],
    ['saveSeo ส่ง contactPhone อีก', Object.assign({}, SRC, { edit: SRC.edit.replace("captchaSiteKey: document.getElementById('sCaptchaSiteKey').value || null,", "captchaSiteKey: document.getElementById('sCaptchaSiteKey').value || null, contactPhone: '',") })],
    ['การ์ดการรับจองส่ง schedule ด้วย', Object.assign({}, SRC, { bsa: SRC.bsa.replace("maxAdvanceDays: num('bsaMaxAdv', null) }", "maxAdvanceDays: num('bsaMaxAdv', null), schedule: {} }") })],
    ['ช่องว่างกลายเป็น 0', Object.assign({}, SRC, { bsa: SRC.bsa.replace("bufferBeforeMinutes: num('bsaBufBefore', null)", "bufferBeforeMinutes: num('bsaBufBefore', 0)") })],
    ['สวิตช์ผู้ให้บริการส่งเสมอ', Object.assign({}, SRC, { bsa: SRC.bsa.replace('if (this._provDirty) body.providerAllocationEnabled', 'body.providerAllocationEnabled') })],
    ['การ์ดวันผลิตส่งเงื่อนไขชำระ', Object.assign({}, SRC, { mto: SRC.mto.replace('mtoSkipCompanyHolidays: $(\'sgMtoHolidays\').checked,', "mtoSkipCompanyHolidays: $('sgMtoHolidays').checked, mtoPaymentTermMode: 'full',") })],
    ['modal สั่งทำกลับมา', Object.assign({}, SRC, { orders: SRC.orders.replace('<div class="grid grid-4', '<div id="mtoModal"></div><div class="grid grid-4') })],
    ['prompt() สร้างรอบกลับมา', Object.assign({}, SRC, { bookings: SRC.bookings + "<script>prompt('สร้างรอบล่วงหน้า')</script>" })],
    ['wizard ขาดขั้น 6', Object.assign({}, SRC, { sites: SRC.sites.replace('id="wzStep6"', 'id="wzStepX"') })],
    ['S1 ไม่โหลดเว็บใหม่หลังใส่แม่แบบ', Object.assign({}, SRC, { sites: SRC.sites.replace('await api.cmsGetSite(site.id)', 'await Promise.resolve(null)') })],
    ['S2 ไม่ฟังสิทธิ์ที่มาถึงในหน้าการจอง', Object.assign({}, SRC, { bookings: SRC.bookings.replace(/document\.addEventListener\('layout:permissions'[^\n]*clsTermsHome\(\); \}\);/, '') })],
    ['S3 กลุ่ม mto กลับไปผูกคำสั่งซื้ออย่างเดียว', Object.assign({}, SRC, { hub: SRC.hub.replace("(mods.includes('orders') || Number(s.productCount || 0) > 0 || this._wantMto) && { key: 'mto'", "mods.includes('orders') && { key: 'mto'") })],
    ['ศูนย์ไม่โหลดตัวแก้สั่งทำ', Object.assign({}, SRC, { hub: SRC.hub.replace('/js/cms-mto-settings-admin.js', '/js/x.js') })],
  ];
  for (const [name, src] of neg) {
    const f = await scenario(src);
    if (!f.length) { failed++; console.log(`❌ negative "${name}" — ชุดตรวจไม่จับ`); }
    else console.log(`✅ negative "${name}" ถูกจับ (${f[0]})`);
  }
  if (failed) { console.log(`❌ cms_settings_hub_sim: ${failed} ชุดล้ม`); process.exit(1); }
  console.log('✅ cms_settings_hub_sim ผ่านทั้งหมด');
})();
