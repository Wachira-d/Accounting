/* CmsSiteContext — "งานของเว็บไหน" ตัวเดียวของหน้าจัดการรายเว็บ (คำสั่งซื้อ · จองคิว · Lead)
 *
 * ที่มา: เมนูซ้าย "ขายออนไลน์ & Portal" เคยมี คำสั่งซื้อ/จองคิว/ที่พัก/ตั้งค่าที่พัก/Lead ปนกับ "เว็บไซต์ของฉัน"
 * ผู้ใช้ที่มีหลายเว็บต้องเดาว่ากำลังดูของเว็บไหน ⇒ ย้ายทางเข้าไปไว้ "ใต้การ์ดเว็บ" (cms-sites.html) ที่เปิดหน้าด้วย
 * `?siteId=` และทุกหน้าแสดง breadcrumb "เว็บไซต์ของฉัน › <เว็บ> › <งาน>" จากตัวนี้ตัวเดียว
 * (เดิมแต่ละหน้าอ่าน ?siteId= เองแล้วเชื่อค่าใน URL ตรง ๆ — siteId ของบริษัทอื่น/เว็บที่ลบไปแล้ว = select ว่าง + ตารางว่างเงียบ)
 *
 * ตัวเลือก "ทุกไซต์" (`?siteId=all`) — ภาพรวมรวมทุกเว็บ ดึงผ่าน endpoint ระดับบริษัท `cms/overview/{orders|bookings|leads}` ที่แบ่งหน้าจริง
 * (R-45 · เดิมวนยิงรายเว็บ 20 แถว/เว็บ ฝั่ง client) · เว็บที่ผู้ใช้ไม่มีบทบาทถูกตัดฝั่งเซิร์ฟเวอร์ (Helpers/CmsSiteScope — กติกาเดียวกับ
 * [RequireSiteRole] รายเว็บ) · ทุกแถวติด siteId/siteName จากเซิร์ฟเวอร์ — ปุ่ม action ต้องยิงไปเว็บเจ้าของแถว (actSiteId)
 *
 * ปุ่มโมดูลต่อเว็บ (MODULE_LINKS) อ่านจาก `site.modules` ที่เซิร์ฟเวอร์คำนวณ (CmsModuleResolver) — หน้าเว็บแค่วาด */
(function () {
  const ALL = 'all';
  const esc = (s) => (window.Layout && Layout.esc) ? Layout.esc(s) : String(s == null ? '' : s)
    .replace(/[&<>"']/g, c => ({ '&': '&amp;', '<': '&lt;', '>': '&gt;', '"': '&quot;', "'": '&#39;' }[c]));
  const enc = encodeURIComponent;

  // key = โมดูลจาก CmsModuleResolver · ปุ่มเรียงตามลำดับนี้บนการ์ดเว็บ
  const MODULE_LINKS = {
    orders:   [
      { page: 'cms-orders',         label: '🛒 คำสั่งซื้อ',     href: (id) => `/pages/cms-orders.html?siteId=${enc(id)}` },
      { page: 'cms-store-settings', label: '⚙️ ตั้งค่าร้านค้า', href: (id) => `/pages/cms-store-settings.html?siteId=${enc(id)}` },
    ],
    bookings: [{ page: 'cms-bookings', label: '📅 การจองคิว', href: (id) => `/pages/cms-bookings.html?siteId=${enc(id)}` }],
    lodging:  [
      { page: 'lodging',          label: '🏨 Front desk',   href: (id) => `/pages/lodging.html?siteId=${enc(id)}` },
      { page: 'lodging-settings', label: '🛏️ ตั้งค่าที่พัก', href: (id) => `/pages/lodging-settings.html?siteId=${enc(id)}` },
    ],
    leads:    [{ page: 'cms-leads',    label: '📨 คำขอ / Lead', href: (id) => `/pages/cms-leads.html?siteId=${enc(id)}` }],
  };
  const MODULE_ORDER = ['orders', 'bookings', 'lodging', 'leads'];

  const CmsSiteContext = {
    ALL,
    MODULE_LINKS,

    /** ปุ่มงานของเว็บนี้ตามโมดูลที่ใช้จริง → [{module, page, label, href}] */
    moduleLinks(site) {
      const mods = Array.isArray(site && site.modules) ? site.modules : [];
      return MODULE_ORDER.filter(m => mods.includes(m))
        .flatMap(m => MODULE_LINKS[m].map(l => ({ module: m, page: l.page, label: l.label, href: l.href(site.id) })));
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
     *  ตรวจกับรายการเว็บของบริษัทจริง · ไม่มี/ไม่รู้จัก ⇒ เว็บแรกที่ใช้โมดูลนี้ (บอกผู้ใช้เมื่อ URL ผิด) */
    pick(sites, raw, opts) {
      const o = opts || {};
      if (raw === ALL && o.allowAll && sites.length > 1) return ALL; // เว็บเดียว = ไม่มีตัวเลือกทุกไซต์ (fillSelect) ⇒ ตกไปเว็บนั้น
      if (raw && sites.some(s => s.id === raw)) return raw;
      if (raw && raw !== ALL && window.Layout) Layout.toast('ไม่พบเว็บไซต์ตามลิงก์ (อาจถูกลบหรือไม่ใช่ของบริษัทนี้) — แสดงเว็บแรกแทน', 'info', 6000);
      const withMod = o.module ? this.sitesWith(sites, o.module) : [];
      return (withMod[0] || sites[0] || {}).id || null;
    },

    /** เติม dropdown เว็บ — เว็บที่ใช้โมดูลนี้ขึ้นก่อน · "ทุกไซต์" อยู่บนสุดเมื่อหน้ารองรับ */
    fillSelect(sel, sites, opts) {
      const o = opts || {};
      if (typeof sel === 'string') sel = document.getElementById(sel);
      if (!sel) return;
      const has = (s) => !o.module || (Array.isArray(s.modules) && s.modules.includes(o.module));
      const sorted = sites.slice().sort((a, b) => (has(b) ? 1 : 0) - (has(a) ? 1 : 0));
      const all = o.allowAll && sites.length > 1 ? `<option value="${ALL}">🌐 ทุกไซต์ (ภาพรวม)</option>` : '';
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

    /** breadcrumb "เว็บไซต์ของฉัน › <เว็บ|ทุกไซต์> › <งาน>" + ปุ่มงานอื่นของเว็บเดียวกัน */
    renderCrumb(el, sites, siteId, section, currentPage) {
      if (typeof el === 'string') el = document.getElementById(el);
      if (!el) return;
      const site = siteId && siteId !== ALL ? sites.find(s => s.id === siteId) : null;
      const sep = '<span style="color:var(--gray-400);margin:0 6px">›</span>';
      const mid = site
        ? `<a href="/pages/cms-edit.html?siteId=${enc(site.id)}" style="color:var(--primary);font-weight:600" title="แก้ไขเว็บไซต์นี้">${esc(site.name)}</a>`
        : `<span style="font-weight:600">${siteId === ALL ? 'ทุกไซต์' : '—'}</span>`;
      const siblings = site
        ? this.moduleLinks(site).filter(l => l.page !== currentPage)
            .map(l => `<a class="btn btn-sm btn-ghost" href="${esc(l.href)}" style="font-size:12px">${esc(l.label)}</a>`).join('')
        : '';
      el.innerHTML = `<div style="display:flex;justify-content:space-between;align-items:center;gap:8px;flex-wrap:wrap;font-size:13px;margin-bottom:10px">
          <nav aria-label="breadcrumb"><a href="/pages/cms-sites.html" style="color:var(--primary)">🌐 เว็บไซต์ของฉัน</a>${sep}${mid}${sep}<span style="color:var(--gray-600)">${esc(section)}</span></nav>
          <div style="display:flex;gap:4px;flex-wrap:wrap">${siblings}</div>
        </div>`;
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

  window.CmsSiteContext = CmsSiteContext;
})();
