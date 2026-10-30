/* CmsSiteContext — "งานของเว็บไหน" ตัวเดียวของหน้าจัดการรายเว็บ (คำสั่งซื้อ · จองคิว · Lead · ที่พัก · ตั้งค่า)
 *
 * ที่มา: เมนูซ้าย "ขายออนไลน์ & Portal" เคยมี คำสั่งซื้อ/จองคิว/ที่พัก/ตั้งค่าที่พัก/Lead ปนกับ "เว็บไซต์ของฉัน"
 * ผู้ใช้ที่มีหลายเว็บต้องเดาว่ากำลังดูของเว็บไหน ⇒ ย้ายทางเข้าไปไว้ "ใต้การ์ดเว็บ" (cms-sites.html) ที่เปิดหน้าด้วย
 * `?siteId=` (เดิมแต่ละหน้าอ่าน ?siteId= เองแล้วเชื่อค่าใน URL ตรง ๆ — siteId ของบริษัทอื่น/เว็บที่ลบไปแล้ว = select ว่าง + ตารางว่างเงียบ)
 *
 * รอบ 215 (erp-review/2026-10-30/CMS_UX_REDESIGN.md §4 · เฟส 1): `renderCrumb` วาด **หัวพื้นที่ทำงาน** แทน breadcrumb เดิม —
 *   ตัวสลับเว็บ (ชื่อ · ประเภทธุรกิจ · สถานะ · URL) + ป้ายขอบเขต (🌐 เฉพาะเว็บนี้ / 🗂 ทุกไซต์) + แท็บงานของเว็บนั้น
 *   ตาม `site.modules` ที่เซิร์ฟเวอร์คำนวณ (CmsModuleResolver) และสิทธิ์เมนู (Layout.hasMenuAccess — id เมนูเดิมเป็นกุญแจ) ·
 *   หน้าที่มี select เว็บของตัวเอง (`#siteSelector`) ⇒ ตัวสลับบนหัวสั่ง select เดิม (หน้าโหลดข้อมูลตามเส้นเดิม) แล้วซ่อน select เดิม
 *   ⇒ มีตัวเลือกเว็บตัวเดียวบนจอ · หน้าที่ไม่มี (ที่พัก) ⇒ เปลี่ยน URL `?siteId=` · จำเว็บ/แท็บล่าสุดผ่าน Layout.setCmsLastSite
 *
 * ตัวเลือก "ทุกไซต์" (`?siteId=all`) — ภาพรวมรวมทุกเว็บ ดึงผ่าน endpoint ระดับบริษัท `cms/overview/{orders|bookings|leads}` ที่แบ่งหน้าจริง
 * (R-45) · เว็บที่ผู้ใช้ไม่มีบทบาทถูกตัดฝั่งเซิร์ฟเวอร์ (Helpers/CmsSiteScope) · ทุกแถวติด siteId/siteName จากเซิร์ฟเวอร์ —
 * ปุ่ม action ต้องยิงไปเว็บเจ้าของแถว (actSiteId) · หน้าที่รองรับ = ALL_PAGES (ตัวเดียวที่ cms-sites ใช้สร้างลิงก์ภาพรวมด้วย)
 *
 * ปุ่ม/แท็บโมดูลต่อเว็บ (MODULE_LINKS) อ่านจาก `site.modules` ที่เซิร์ฟเวอร์คำนวณ — หน้าเว็บแค่วาด */
(function () {
  const ALL = 'all';
  const esc = (s) => (window.Layout && Layout.esc) ? Layout.esc(s) : String(s == null ? '' : s)
    .replace(/[&<>"']/g, c => ({ '&': '&amp;', '<': '&lt;', '>': '&gt;', '"': '&quot;', "'": '&#39;' }[c]));
  const enc = encodeURIComponent;

  // key = โมดูลจาก CmsModuleResolver · kind: work = งานเข้า · report = รายงาน · settings = ตั้งค่า (เรียงแท็บตาม KIND_ORDER)
  const MODULE_LINKS = {
    orders:   [
      { page: 'cms-orders',         kind: 'work',     label: '🛒 คำสั่งซื้อ',        href: (id) => `/pages/cms-orders.html?siteId=${enc(id)}` },
      { page: 'cms-store-settings', kind: 'settings', label: '⚙️ ตั้งค่าร้านค้า',    href: (id) => `/pages/cms-store-settings.html?siteId=${enc(id)}` },
    ],
    bookings: [
      { page: 'cms-bookings',          kind: 'work',   label: '📅 การจอง',             href: (id) => `/pages/cms-bookings.html?siteId=${enc(id)}` },
      { page: 'cms-booking-providers', kind: 'report', label: '📊 สรุปผู้ให้บริการ',   href: (id) => `/pages/cms-booking-providers.html?siteId=${enc(id)}` },
    ],
    lodging:  [
      { page: 'lodging',          kind: 'work',     label: '🏨 Front desk',   href: (id) => `/pages/lodging.html?siteId=${enc(id)}` },
      { page: 'lodging-settings', kind: 'settings', label: '🛏️ ตั้งค่าที่พัก', href: (id) => `/pages/lodging-settings.html?siteId=${enc(id)}` },
    ],
    leads:    [{ page: 'cms-leads', kind: 'work', label: '📨 คำขอ / Lead', href: (id) => `/pages/cms-leads.html?siteId=${enc(id)}` }],
  };
  const MODULE_ORDER = ['orders', 'bookings', 'lodging', 'leads'];
  const KIND_ORDER = ['work', 'report', 'settings', 'content'];
  // แท็บ "หน้าเว็บ" (cms-edit) — ทุกเว็บมี · สิทธิ์ = เมนู cms-sites (cms-edit เรียก Layout.init('cms-sites'))
  const CONTENT_LINK = { module: null, page: 'cms-edit', menuId: 'cms-sites', kind: 'content', label: '✏️ หน้าเว็บ & เนื้อหา', href: (id) => `/pages/cms-edit.html?siteId=${enc(id)}` };
  // รอบ 215 เฟส 2: ศูนย์ "ตั้งค่าเว็บ" (cms-site-settings) — ทุกเว็บมี · สิทธิ์ = เมนู cms-sites (หน้าเรียก Layout.init('cms-sites')) ·
  // ผู้ที่เห็นศูนย์นี้ไม่ต้องเห็นแท็บ "ตั้งค่าร้านค้า"/"ตั้งค่าที่พัก" ซ้ำ (เข้าจากกลุ่มในศูนย์ได้) · ผู้ที่มีสิทธิ์แค่หน้าเดิม ⇒ ยังเห็นแท็บเดิม
  const SETTINGS_LINK = { module: null, page: 'cms-site-settings', menuId: 'cms-sites', kind: 'settings', label: '⚙️ ตั้งค่าเว็บ', href: (id) => `/pages/cms-site-settings.html?siteId=${enc(id)}` };
  const HUB_COVERS = ['cms-store-settings', 'lodging-settings'];
  // หน้าที่มีโหมด "ทุกไซต์" (?siteId=all ผ่าน cms/overview/*) — cms-sites ใช้ชุดเดียวกันสร้างลิงก์ภาพรวม
  const ALL_PAGES = { 'cms-orders': 'orders', 'cms-bookings': 'bookings', 'cms-leads': 'leads' };
  const ALL_LINKS = {
    'cms-orders':   { label: '🛒 คำสั่งซื้อ', href: '/pages/cms-orders.html?siteId=all' },
    'cms-bookings': { label: '📅 การจอง',     href: '/pages/cms-bookings.html?siteId=all' },
    'cms-leads':    { label: '📨 คำขอ / Lead', href: '/pages/cms-leads.html?siteId=all' },
  };

  const canSee = (menuId) => !(window.Layout && typeof Layout.hasMenuAccess === 'function') || Layout.hasMenuAccess(menuId);

  const CmsSiteContext = {
    ALL,
    MODULE_LINKS,
    ALL_PAGES,

    /** ปุ่มงานของเว็บนี้ตามโมดูลที่ใช้จริง → [{module, page, kind, label, href}] (ไม่กรองสิทธิ์ — การ์ด/ตัวนับใช้) */
    moduleLinks(site) {
      const mods = Array.isArray(site && site.modules) ? site.modules : [];
      return MODULE_ORDER.filter(m => mods.includes(m))
        .flatMap(m => MODULE_LINKS[m].map(l => ({ module: m, page: l.page, kind: l.kind, label: l.label, href: l.href(site.id) })));
    },

    /** แท็บของพื้นที่ทำงานเว็บนี้ — โมดูลจากเซิร์ฟเวอร์ + หน้าเว็บ · กรองด้วยสิทธิ์เมนูเดิม · เรียง งานเข้า → รายงาน → ตั้งค่า → หน้าเว็บ */
    workspaceTabs(site) {
      if (!site) return [];
      const hub = canSee(SETTINGS_LINK.menuId);
      const links = this.moduleLinks(site).filter(l => !(hub && HUB_COVERS.includes(l.page)))
        .concat([{ module: null, page: SETTINGS_LINK.page, menuId: SETTINGS_LINK.menuId, kind: 'settings', label: SETTINGS_LINK.label, href: SETTINGS_LINK.href(site.id) }])
        .concat([{ module: null, page: CONTENT_LINK.page, menuId: CONTENT_LINK.menuId, kind: 'content', label: CONTENT_LINK.label, href: CONTENT_LINK.href(site.id) }]);
      return links.filter(l => canSee(l.menuId || l.page))
        .sort((a, b) => KIND_ORDER.indexOf(a.kind) - KIND_ORDER.indexOf(b.kind));
    },

    /** โมดูลของหน้า (จาก MODULE_LINKS) · null = หน้าที่ทุกเว็บมี (cms-edit) */
    pageModule(page) {
      const m = MODULE_ORDER.find(k => MODULE_LINKS[k].some(l => l.page === page));
      return m || null;
    },

    /** ที่พักที่หน้า Front desk/ตั้งค่าที่พักควรเปิด: ?propertyId= (ของบริษัทนี้) > ที่พักของ ?siteId= > ที่พักของเว็บล่าสุด > แห่งแรก ·
     *  มี ?siteId= แต่เว็บนั้นยังไม่มีที่พัก ⇒ null (ห้ามตกไปที่พักแห่งอื่นเงียบ ๆ — หน้าแสดงทางไปสร้าง) */
    pickProperty(props, rawPropertyId, rawSiteId, sites) {
      const list = props || [];
      if (rawPropertyId && list.some(p => p.id === rawPropertyId)) return rawPropertyId;
      // ฝ่ายค้านรอบ 215 S2: ?siteId= ที่ไม่อยู่ในรายการเว็บที่ผู้ใช้เห็น (ลบแล้ว/ค้างจากเว็บล่าสุด/ไม่มีสิทธิ์) = ไม่มี ⇒ ตกไปลำดับถัดไป
      // (เดิม ⇒ null ⇒ ทางตันถาวร) · รายการเว็บโหลดไม่ได้ (ว่าง) ⇒ เชื่อ siteId ตามเดิม
      const known = !rawSiteId || !Array.isArray(sites) || !sites.length || sites.some(s => s.id === rawSiteId);
      if (rawSiteId && known) { const b = list.find(p => p.siteId === rawSiteId); return b ? b.id : null; }
      this._dropStaleLast(sites);
      const last = (window.Layout && typeof Layout.cmsLastSite === 'function') ? Layout.cmsLastSite() : null;
      const bound = last ? list.find(p => p.siteId === last.siteId) : null;
      return (bound || list[0] || {}).id || null;
    },

    /** หัวพื้นที่ทำงานของหน้าที่พัก (เลือกด้วยที่พัก ไม่ใช่เว็บ) — เว็บ = เว็บที่ที่พักผูกอยู่ · ที่พักไม่ผูกเว็บ ⇒ บอกตรง ๆ ·
     *  ตัวเลือกที่พักในหน้า (#propSelector) ซ่อนเมื่อทุกที่พักผูกเว็บแล้ว (ตัวสลับเว็บพอ) · มีที่พักไม่ผูก ⇒ คงไว้ */
    renderPropertyHeader(el, sites, prop, props, section, currentPage, fallbackSiteId, beforeLeave) {
      sites = sites || [];
      const boundSite = prop && prop.siteId && sites.some(s => s.id === prop.siteId) ? prop.siteId : null;
      const siteId = boundSite || (!prop && fallbackSiteId && sites.some(s => s.id === fallbackSiteId) ? fallbackSiteId : null);
      const note = prop && !prop.siteId
        ? `ที่พัก "${prop.name || ''}" ยังไม่ผูกกับเว็บไซต์ — งานของที่พักนี้ไม่ขึ้นบนเว็บใด (ผูกได้ที่ ตั้งค่าที่พัก › ข้อมูลที่พัก › เว็บไซต์ที่ผูก)`
        : null;
      this.renderCrumb(el, sites, siteId, section, currentPage, { unboundNote: note, beforeLeave });
      const ps = document.getElementById('propSelector');
      const allBound = (props || []).length > 0 && props.every(p => p.siteId && sites.some(s => s.id === p.siteId));
      if (ps) ps.style.display = allBound ? 'none' : '';
    },

    /** ลิงก์ภาพรวม "ทุกไซต์" ที่เป็นทุกไซต์จริง (?siteId=all) ตามโมดูลที่มีอย่างน้อย 1 เว็บ + สิทธิ์ — ไม่มีทุกไซต์ของที่พัก (เฟส 3) */
    allLinks(sites) {
      const mods = new Set((sites || []).flatMap(s => Array.isArray(s.modules) ? s.modules : []));
      return Object.keys(ALL_PAGES).filter(p => mods.has(ALL_PAGES[p]) && canSee(p))
        .map(p => ({ page: p, module: ALL_PAGES[p], label: ALL_LINKS[p].label, href: ALL_LINKS[p].href }));
    },

    /** เว็บที่ใช้โมดูลนี้ — ใช้กับภาพรวม "ทุกไซต์" (ไม่ดึงเว็บองค์กรที่ไม่มีวันมีออเดอร์) */
    sitesWith(sites, module) {
      return (sites || []).filter(s => Array.isArray(s.modules) && s.modules.includes(module));
    },

    async loadSites() {
      const api = Layout.api(); if (!api) return [];
      const res = await api.cmsListSites('?pageSize=100');
      return res?.data?.items || res?.data || [];
    },

    /** เลือกเว็บจากค่า `?siteId=` ที่หน้าอ่านมาเอง (`raw` — หน้าปลายทางต้องอ่านชื่อ param เองให้ deep_link_param_check เห็น)
     *  ตรวจกับรายการเว็บของบริษัทจริง · ไม่มี siteId ⇒ เว็บล่าสุดที่ใช้ (ถ้าใช้โมดูลนี้) ⇒ เว็บแรกที่ใช้โมดูลนี้ (บอกผู้ใช้เมื่อ URL ผิด) */
    pick(sites, raw, opts) {
      const o = opts || {};
      if (raw === ALL && o.allowAll && sites.length > 1) return ALL; // เว็บเดียว = ไม่มีตัวเลือกทุกไซต์ (fillSelect) ⇒ ตกไปเว็บนั้น
      if (raw && sites.some(s => s.id === raw)) return raw;
      if (raw && raw !== ALL && window.Layout) Layout.toast('ไม่พบเว็บไซต์ตามลิงก์ (อาจถูกลบหรือไม่ใช่ของบริษัทนี้) — แสดงเว็บแรกแทน', 'info', 6000);
      const withMod = o.module ? this.sitesWith(sites, o.module) : sites;
      // รอบ 215: จำเว็บล่าสุด (ความสะดวก — URL ยังเป็นความจริง) · เว็บล่าสุดที่ไม่ใช้งานนี้ ⇒ ไม่เลือก (กันตารางว่าง)
      const last = (!raw && window.Layout && typeof Layout.cmsLastSite === 'function') ? Layout.cmsLastSite() : null;
      if (last && withMod.some(s => s.id === last.siteId)) return last.siteId;
      this._dropStaleLast(sites);
      return (withMod[0] || sites[0] || {}).id || null;
    },

    /** ฝ่ายค้านรอบ 215 S2: เว็บล่าสุดที่จำไว้ไม่อยู่ในรายการเว็บที่ผู้ใช้เห็นแล้ว (ถูกลบ/ถูกถอดสิทธิ์) ⇒ ล้าง
     *  (ทางเข้า "พื้นที่ทำงานเว็บไซต์" จะไม่พาไปเว็บที่ไม่มีซ้ำ ๆ) · รายการว่าง = ยังโหลดไม่ได้ ⇒ ไม่ตัดสิน */
    _dropStaleLast(sites) {
      if (!window.Layout || typeof Layout.cmsLastSite !== 'function' || !Array.isArray(sites) || !sites.length) return;
      const last = Layout.cmsLastSite();
      if (last && !sites.some(s => s.id === last.siteId) && typeof Layout.clearCmsLastSite === 'function') Layout.clearCmsLastSite();
    },

    /** เติม dropdown เว็บ — เว็บที่ใช้โมดูลนี้ขึ้นก่อน · "ทุกไซต์" อยู่บนสุดเมื่อหน้ารองรับ */
    fillSelect(sel, sites, opts) {
      const o = opts || {};
      if (typeof sel === 'string') sel = document.getElementById(sel);
      if (!sel) return;
      const has = (s) => !o.module || (Array.isArray(s.modules) && s.modules.includes(o.module));
      const sorted = sites.slice().sort((a, b) => (has(b) ? 1 : 0) - (has(a) ? 1 : 0));
      const all = o.allowAll && sites.length > 1 ? `<option value="${ALL}">🗂 ทุกไซต์ (ภาพรวม)</option>` : '';
      sel.innerHTML = all + sorted.map(s => `<option value="${esc(s.id)}">${esc(s.name)}${s.subdomain ? ` (${esc(s.subdomain)})` : ''}${has(s) ? '' : ' — ยังไม่ใช้งานนี้'}</option>`).join('');
      if (o.value) sel.value = o.value;
    },

    /** จด ?siteId= ลง URL (bookmark/รีเฟรชแล้วกลับมาเว็บเดิม) โดยคงพารามิเตอร์อื่นไว้ */
    setUrl(siteId) {
      try {
        const u = new URL(location.href);
        if (siteId) u.searchParams.set('siteId', siteId); else u.searchParams.delete('siteId');
        history.replaceState(null, '', u.pathname + u.search + u.hash);
      } catch (_) { /* เบราว์เซอร์เก่า — ไม่กระทบการทำงาน */ }
    },

    /** ป้ายขอบเขต 2 แบบเท่านั้น (ข้อความ + ไอคอน — ไม่ใช้สีอย่างเดียว) · kind: 'site' | 'all' | 'company' */
    scopeBadge(kind, siteName) {
      const st = 'display:inline-flex;align-items:center;gap:4px;font-size:12px;border-radius:9999px;padding:2px 10px;border:1px solid;white-space:nowrap';
      if (kind === 'company') return `<span class="cms-scope cms-scope-company" style="${st};border-color:var(--gray-300);background:var(--gray-50);color:var(--gray-700)">🏢 ใช้กับทุกไซต์ (ระดับบริษัท)</span>`;
      if (kind === 'all') return `<span class="cms-scope cms-scope-all" style="${st};border-color:#c4b5fd;background:#f5f3ff;color:#5b21b6">🗂 กำลังดู: ทุกไซต์</span>`;
      return `<span class="cms-scope cms-scope-site" style="${st};border-color:#bfdbfe;background:#eff6ff;color:#1e40af">🌐 เฉพาะเว็บนี้${siteName ? ': ' + esc(siteName) : ''}</span>`;
    },

    /** หัวพื้นที่ทำงาน (รอบ 215) — breadcrumb + ตัวสลับเว็บ + ป้ายขอบเขต + แท็บงานของเว็บ · ชื่อเดิม renderCrumb คงไว้
     *  (ทุกหน้าที่เรียกอยู่ได้ของใหม่โดยไม่ต้องแก้) · siteId = null ⇒ ยังไม่มีเว็บ/ที่พักไม่ผูกเว็บ (`opts.unboundNote`) */
    renderCrumb(el, sites, siteId, section, currentPage, opts) {
      if (typeof el === 'string') el = document.getElementById(el);
      if (!el) return;
      const o = opts || {};
      sites = sites || [];
      const isAll = siteId === ALL;
      const site = siteId && !isAll ? sites.find(s => s.id === siteId) : null;
      if (site && window.Layout && typeof Layout.setCmsLastSite === 'function') Layout.setCmsLastSite(site.id, currentPage);
      const sep = '<span aria-hidden="true" style="color:var(--gray-400);margin:0 6px">›</span>';
      const lbl = (kind, v) => (window.Layout && Layout.enumLabel) ? Layout.enumLabel(kind, v, '') : '';

      // ฝ่ายค้านรอบ 215 S1: ตัวสลับแสดงเฉพาะเว็บที่ผู้ใช้มีแท็บให้ทำงาน (+ เว็บปัจจุบัน) — ไม่พาไปหน้าที่ไม่มีสิทธิ์
      this._last = { el, sites, siteId, section, currentPage, opts: o };
      const usable = sites.filter(s => (site && s.id === site.id) || this.workspaceTabs(s).length > 0);
      const allowAll = Object.prototype.hasOwnProperty.call(ALL_PAGES, currentPage) && sites.length > 1;
      const options = (allowAll ? `<option value="${ALL}"${isAll ? ' selected' : ''}>🗂 ทุกไซต์ (ภาพรวม)</option>` : '')
        + (!site && !isAll ? `<option value="" selected>— เลือกเว็บไซต์ —</option>` : '')
        + usable.map(s => `<option value="${esc(s.id)}"${site && s.id === site.id ? ' selected' : ''}>${esc(s.name)}${s.industryType ? ' · ' + esc(lbl('siteIndustry', s.industryType)) : ''}</option>`).join('');

      const published = site && (site.status === 'Published' || site.status === 1);
      const meta = site ? [
        site.industryType ? `<span style="font-size:12px;border:1px solid #ddd6fe;background:#f5f3ff;color:#5b21b6;border-radius:9999px;padding:2px 10px">${esc(lbl('siteIndustry', site.industryType))}</span>` : '',
        `<span style="font-size:12px;color:${published ? '#15803d' : '#b45309'}">${published ? '●' : '○'} ${esc(lbl('siteStatus', site.status) || (published ? 'เผยแพร่แล้ว' : 'ฉบับร่าง'))}</span>`,
        site.subdomain ? `<a href="/site/${enc(site.subdomain)}${published ? '' : '/home?preview=1'}" target="_blank" rel="noopener" style="font-size:12px;color:var(--primary)" title="${published ? 'เปิดหน้าเว็บ' : 'ดูตัวอย่าง (ยังไม่เผยแพร่)'}">/site/${esc(site.subdomain)} ↗</a>` : '',
      ].join('') : '';
      const scope = isAll ? this.scopeBadge('all') : site ? this.scopeBadge('site', site.name) : '';

      const tabs = isAll ? this.allLinks(sites) : this.workspaceTabs(site);
      const tabHtml = tabs.map(t => {
        // ฝ่ายค้านเฟส 2: หน้าตั้งค่าร้าน/ที่พักที่ศูนย์ตั้งค่าเว็บครอบไว้ ⇒ แท็บ "ตั้งค่าเว็บ" เป็นแท็บปัจจุบัน
        const cur = t.page === currentPage || (t.page === SETTINGS_LINK.page && HUB_COVERS.includes(currentPage));
        return `<a href="${esc(t.href)}"${cur ? ' aria-current="page"' : ''} style="white-space:nowrap;padding:8px 12px;font-size:13px;border-bottom:2px solid ${cur ? 'var(--primary)' : 'transparent'};color:${cur ? 'var(--primary)' : 'var(--gray-600)'};font-weight:${cur ? '600' : '400'};text-decoration:none">${esc(t.label)}</a>`;
      }).join('');

      el.innerHTML = `<div class="cms-ws" style="margin-bottom:12px">
          <nav aria-label="breadcrumb" style="font-size:12px;color:var(--gray-500);margin-bottom:6px">
            <a href="/pages/cms-sites.html" style="color:var(--primary)">🗂 เว็บไซต์ทั้งหมด</a>${sep}<span style="font-weight:600;color:var(--gray-700)">${site ? esc(site.name) : isAll ? 'ทุกไซต์' : '—'}</span>${sep}<span>${esc(section)}</span>
          </nav>
          <div style="display:flex;align-items:center;gap:10px;flex-wrap:wrap;background:#fff;border:1px solid var(--gray-200);border-radius:10px;padding:10px 12px">
            <label for="cmsWsSite" style="font-size:12px;color:var(--gray-500)">เว็บไซต์</label>
            <select id="cmsWsSite" class="form-input" style="min-width:200px;max-width:100%;width:auto;font-weight:600">${options}</select>
            ${scope}${meta}
          </div>
          ${o.unboundNote ? `<div role="note" style="font-size:12px;color:#92400e;background:#fffbeb;border:1px solid #fde68a;border-radius:8px;padding:6px 10px;margin-top:6px">${esc(o.unboundNote)}</div>` : ''}
          ${tabHtml ? `<nav aria-label="งานของเว็บ" style="display:flex;gap:2px;overflow-x:auto;border-bottom:1px solid var(--gray-200);margin-top:8px;-webkit-overflow-scrolling:touch">${tabHtml}</nav>` : ''}
          <div aria-live="polite" style="position:absolute;width:1px;height:1px;overflow:hidden;clip:rect(0 0 0 0)">${site ? 'กำลังดูเว็บ ' + esc(site.name) : isAll ? 'กำลังดูทุกไซต์' : ''}</div>
        </div>`;

      // ตัวเลือกเว็บตัวเดียวบนจอ: หน้าที่มี #siteSelector ⇒ สั่ง select เดิม (เส้นโหลดเดิมของหน้า) แล้วซ่อน · ไม่มี ⇒ เปลี่ยน URL
      const pageSel = document.getElementById('siteSelector');
      if (pageSel) pageSel.style.display = 'none';
      const ws = el.querySelector('#cmsWsSite');
      if (ws) ws.addEventListener('change', () => {
        const v = ws.value;
        if (!v) return;
        // N11: หน้าที่มีฟอร์มค้างแก้ (เช่น ตั้งค่าที่พัก) ถามก่อนทิ้ง · ไม่ยืนยัน ⇒ กลับไปเว็บเดิม
        if (typeof o.beforeLeave === 'function' && !o.beforeLeave()) { ws.value = site ? site.id : (isAll ? ALL : ''); return; }
        // เว็บปลายทางไม่มีงานของหน้านี้ (เช่น จาก Front desk ไปเว็บสปา) ⇒ ไปแท็บแรกของเว็บนั้น ไม่ใช่หน้าว่าง
        const target = sites.find(s => s.id === v);
        const mod = this.pageModule(currentPage);
        if (target && mod && !(Array.isArray(target.modules) && target.modules.includes(mod))) {
          // workspaceTabs กรองสิทธิ์แล้ว (หน้าเว็บ = สิทธิ์ cms-sites) ⇒ ไม่มีแท็บ = ไม่มีที่ให้ไป — ห้ามพาไปหน้าที่จะถูกปฏิเสธ
          const first = this.workspaceTabs(target)[0];
          if (first) { location.href = first.href; return; }
          if (window.Layout) Layout.toast('คุณไม่มีสิทธิ์ทำงานบนเว็บนี้', 'error');
          ws.value = site ? site.id : (isAll ? ALL : '');
          return;
        }
        if (pageSel && Array.from(pageSel.options).some(op => op.value === v)) {
          pageSel.value = v;
          pageSel.dispatchEvent(new Event('change'));
          return;
        }
        if (typeof o.onSelect === 'function') { o.onSelect(v); return; }
        location.href = `/pages/${currentPage}.html?siteId=${enc(v)}`;
      });
    },

    /** แถบใต้ตัวกรองเมื่อดูแบบทุกไซต์ — ตัวเลขทั้งหมดมาจาก PagedResponse ของเซิร์ฟเวอร์ (totalCount/page/totalPages) · หน้าแค่วาดปุ่มเลื่อนหน้า
     *  info = { totalCount, page, totalPages, pageSize, goPage: 'Page.goPage' } · null = ซ่อน */
    allNote(el, info) {
      if (typeof el === 'string') el = document.getElementById(el);
      if (!el) return;
      if (!info) { el.style.display = 'none'; el.innerHTML = ''; return; }
      const page = Number(info.page) || 1;
      const totalPages = Math.max(1, Number(info.totalPages) || 1);
      const go = String(info.goPage || 'Page.goPage').replace(/[^A-Za-z0-9_.]/g, '');
      const btn = (n, label, disabled) => `<button type="button" class="btn btn-sm btn-ghost" style="font-size:12px" ${disabled ? 'disabled' : ''} onclick="${go}(${n})">${label}</button>`;
      el.style.display = '';
      el.innerHTML = `<div style="display:flex;justify-content:space-between;align-items:center;gap:8px;flex-wrap:wrap;font-size:12px;color:var(--gray-600);background:var(--gray-50);border:1px solid var(--gray-200);border-radius:8px;padding:8px 12px;margin-bottom:12px">
          <div>ภาพรวมทุกเว็บไซต์ที่คุณมีสิทธิ์ · ทั้งหมด ${Number(info.totalCount || 0).toLocaleString('th-TH')} รายการ · หน้า ${page}/${totalPages} — เลือกเว็บไซต์ด้านบนเพื่อดูเฉพาะเว็บ</div>
          <div style="display:flex;gap:4px">${btn(page - 1, '‹ ก่อนหน้า', page <= 1)}${btn(page + 1, 'ถัดไป ›', page >= totalPages)}</div>
        </div>`;
    },
  };

  // ฝ่ายค้านรอบ 215 S4: หัวที่วาดก่อนสิทธิ์โหลด (hasMenuAccess = true ตอนยังไม่รู้) ⇒ วาดใหม่เมื่อ Layout ได้สิทธิ์
  CmsSiteContext.rerender = function () {
    const l = this._last;
    if (!l || !l.el || !l.el.isConnected) return;
    this.renderCrumb(l.el, l.sites, l.siteId, l.section, l.currentPage, l.opts);
  };
  try { document.addEventListener('layout:permissions', () => CmsSiteContext.rerender()); } catch (_) { /* ไม่มี DOM (sim) */ }

  window.CmsSiteContext = CmsSiteContext;
})();
