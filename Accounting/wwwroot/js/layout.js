// ===== Shared Layout Component =====
// Provides sidebar navigation + header for all app pages

const Layout = {
  currentPage: '',
  user: null,
  companies: [],
  currentCompany: null,
  subscription: null,
  features: [],          // array of enabled feature names
  subscriptionStatus: null,
  myPermissions: null,   // { roleName, isOwnerOrAdmin, allowedMenuIds }
  _initialized: false,

  esc(str) {
    if (str == null) return '';
    const d = document.createElement('div');
    d.textContent = String(str);
    return d.innerHTML;
  },

  // Format a Date as YYYY-MM-DD using LOCAL components.
  // Required because `.toISOString()` converts to UTC and shifts the calendar
  // date by the user's timezone offset (UTC+7 → previous-day rollover for any
  // local-midnight date), which leaks prior-month entries into "this month"
  // reports and date-range filters.
  toDateInput(d) {
    const x = (d instanceof Date) ? d : new Date(d);
    if (isNaN(x.getTime())) return '';
    const y = x.getFullYear();
    const m = String(x.getMonth() + 1).padStart(2, '0');
    const day = String(x.getDate()).padStart(2, '0');
    return `${y}-${m}-${day}`;
  },

  init(pageName) {
    // Prevent double-initialization (loadCompanies calls Page.init which calls Layout.init again)
    if (this._initialized && this.currentPage === pageName) return true;

    this.currentPage = pageName;
    this._initialized = true;
    this._installGlobalErrorHandler();
    // Lazy-load UX convenience scripts (Cmd+K palette + helpers). โหลด
    // ครั้งเดียวจาก layout.js → ทุกหน้าได้ feature เหมือนกันโดยไม่ต้องเพิ่ม
    // <script> ในทุก HTML.
    if (!document.querySelector('script[src="/js/command-palette.js"]')) {
      const s1 = document.createElement('script'); s1.src = '/js/command-palette.js'; s1.defer = true; document.head.appendChild(s1);
      const s2 = document.createElement('script'); s2.src = '/js/ux-helpers.js'; s2.defer = true; document.head.appendChild(s2);
      // Smart inline validators — tax-id checksum / period close / VAT mismatch
      const s3 = document.createElement('script'); s3.src = '/js/smart-hooks.js'; s3.defer = true; document.head.appendChild(s3);
    }
    // Reflect ui-mode on <body> so pages can hide advanced-only sections via CSS.
    const uiMode = localStorage.getItem('uiMode') || 'simple';
    document.body.classList.toggle('ui-mode-simple', uiMode === 'simple');
    // Mount help icons once layout is rendered + after DOM mutations from pages.
    setTimeout(() => this._mountHelpIcons(), 200);
    document.addEventListener('DOMContentLoaded', () => setTimeout(() => this._mountHelpIcons(), 200));
    this.user = JSON.parse(localStorage.getItem('user') || 'null');
    this.currentCompany = JSON.parse(localStorage.getItem('currentCompany') || 'null');
    // Deep-link override — when an external system sends the user to a URL
    // with ?company=X (typically from the DeepLinkRewriter on the server),
    // honor that immediately so the page that follows queries the right
    // tenant. Without this the cached currentCompany would mask the redirect.
    try {
      const qCompany = new URLSearchParams(location.search).get('company');
      if (qCompany && /^[0-9a-f-]{36}$/i.test(qCompany) && qCompany !== this.currentCompany?.id) {
        // Stash a minimal company object until loadCompanies() repopulates the
        // full record from the API. id-only is enough for getCompanyId().
        this.currentCompany = { id: qCompany };
        localStorage.setItem('currentCompany', JSON.stringify({ id: qCompany }));
      }
    } catch { /* malformed URL — fall through to cached company */ }
    // Restore cached subscription so menu renders correctly on first paint
    try {
      const cached = JSON.parse(localStorage.getItem('subscription') || 'null');
      if (cached) {
        this.subscription = cached;
        this.features = cached.enabledFeatureNames || [];
        this.subscriptionStatus = cached.status;
      }
    } catch {}
    try {
      const cachedPerms = JSON.parse(localStorage.getItem('myPermissions') || 'null');
      if (cachedPerms) this.myPermissions = cachedPerms;
    } catch {}
    if (!localStorage.getItem('token')) {
      // Preserve the deep-link target so the user lands on the right page
      // after login — without this we'd send them to / and forget the
      // /{cid}/journals/{id} they came from.
      try {
        const here = location.pathname + location.search + location.hash;
        if (here && here !== '/' && !here.startsWith('/login') && !here.startsWith('/register')) {
          sessionStorage.setItem('returnTo', here);
        }
      } catch {}
      window.location.href = '/login.html';
      return false;
    }
    if (typeof I18n !== 'undefined') I18n.init();
    this.render();
    this.bindEvents();
    this.loadNotificationCount();
    this.initServiceWorker();
    this.initSignalR();
    this.applySiteBranding();
    // Page-level feature check — redirect to subscription if locked
    this._enforcePageAccess();
    // Show banner if user has a legacy weak password
    this._showPasswordWeakBannerIfNeeded();
    return true;
  },

  _showPasswordWeakBannerIfNeeded() {
    let notice;
    try { notice = JSON.parse(localStorage.getItem('passwordWeakNotice') || 'null'); } catch { return; }
    if (!notice) return;
    // If grace period expired, force redirect to change-password
    if (notice.forced) {
      window.location.href = '/change-password.html?required=1';
      return;
    }
    if (sessionStorage.getItem('passwordWeakNoticeDismissed') === '1') return;

    const banner = document.createElement('div');
    banner.id = 'passwordWeakBanner';
    banner.style.cssText = 'position:sticky;top:0;left:0;right:0;z-index:9999;background:linear-gradient(135deg,#fbbf24,#f59e0b);color:#78350f;padding:10px 16px;font-size:14px;display:flex;align-items:center;gap:12px;box-shadow:0 2px 8px rgba(0,0,0,.15)';
    const days = notice.daysRemaining ?? 0;
    const weakLabel = this._t('layout.weakPassword', 'รหัสผ่านไม่ปลอดภัย');
    const changeWithinTpl = this._t('layout.changeWithinDays', 'กรุณาเปลี่ยนภายใน {days} วัน', { days });
    const changeNow = this._t('layout.changeNow', 'เปลี่ยนเลย');
    const closeLabel = this._t('common.close', 'ปิด');
    banner.innerHTML = `
      <span style="font-size:18px">🔒</span>
      <span style="flex:1">
        <b>${this.esc(weakLabel)}</b> — ${this.esc(changeWithinTpl)}
      </span>
      <a href="/change-password.html" style="background:#78350f;color:#fff;padding:6px 14px;border-radius:6px;text-decoration:none;font-size:13px;font-weight:600;white-space:nowrap">${this.esc(changeNow)}</a>
      <button onclick="Layout._dismissPasswordWeakBanner()" style="background:transparent;border:none;color:#78350f;font-size:20px;cursor:pointer;padding:0 4px" aria-label="${this.esc(closeLabel)}">×</button>
    `;
    document.body.insertBefore(banner, document.body.firstChild);
  },

  _dismissPasswordWeakBanner() {
    sessionStorage.setItem('passwordWeakNoticeDismissed', '1');
    const el = document.getElementById('passwordWeakBanner');
    if (el) el.remove();
  },

  // ===== Feature & Subscription Helpers =====
  hasFeature(name) {
    if (!name) return true;
    // Without subscription data, allow access (graceful fallback)
    if (!this.subscription) return true;
    return this.features.includes(name);
  },

  // Throws by redirecting to subscription page; returns true if has access
  requireFeature(name, opts = {}) {
    if (this.hasFeature(name)) return true;
    if (opts.silent) return false;
    const label = opts.label || name;
    this.toast(this._t('layout.upgradeNeeded', `ฟีเจอร์ "${label}" ไม่อยู่ในแพ็กเกจของคุณ — โปรดอัพเกรด`, { label }), 'error');
    setTimeout(() => { window.location.href = '/pages/subscription.html'; }, 1200);
    return false;
  },

  isSubscriptionActive() {
    const s = this.subscriptionStatus;
    return s === 'Trial' || s === 'Active';
  },

  async loadSubscription() {
    if (!this.currentCompany?.id) return;
    try {
      const res = await API.get(`/api/subscription/${this.currentCompany.id}`);
      if (res?.success && res.data) {
        this.subscription = res.data;
        this.features = res.data.enabledFeatureNames || [];
        this.subscriptionStatus = res.data.status;
        try { localStorage.setItem('subscription', JSON.stringify(res.data)); } catch {}
        // Re-render menu with updated feature list
        this._refreshNavMenu();
        this._enforcePageAccess();
      }
    } catch (e) { /* trial/no subscription — keep features empty */ }
    await this.loadMyPermissions();
  },

  async loadVatRegistration() {
    if (!this.currentCompany?.id) return;
    // ค่า cache กัน flicker + ลด request; refresh เงียบ ๆ ทุกครั้งที่โหลดบริษัท
    try {
      const cached = localStorage.getItem('vatReg:' + this.currentCompany.id);
      if (cached !== null) this._vatRegistered = cached === 'true';
    } catch {}
    try {
      const res = await API.get(`/api/companies/${this.currentCompany.id}/settings`);
      if (res?.success && res.data) {
        this._vatRegistered = res.data.vatRegistered !== false;
        try { localStorage.setItem('vatReg:' + this.currentCompany.id, String(this._vatRegistered)); } catch {}
        this._refreshNavMenu();
      }
    } catch { /* keep default (show) */ }
  },

  async loadMyPermissions() {
    if (!this.currentCompany?.id) return;
    this.loadVatRegistration();
    try {
      const res = await API.get(`/api/company/${this.currentCompany.id}/roles/my-permissions`);
      if (res?.success && res.data) {
        this.myPermissions = res.data;
        try { localStorage.setItem('myPermissions', JSON.stringify(res.data)); } catch {}
        this._refreshNavMenu();
        this._enforceRoleAccess();
      }
    } catch (e) { /* no permissions data — allow all */ }
  },

  hasMenuAccess(menuId) {
    // Permissions not loaded yet → don't flicker-hide; allow then re-render.
    if (!this.myPermissions) return true;
    if (this.myPermissions.isOwnerOrAdmin) return true;
    const allowed = this.myPermissions.allowedMenuIds || [];
    // "*" = backend sentinel = "no custom CompanyRole assigned, show all"
    // (preserves legacy access for plain Employee / Manager UserRoles).
    if (allowed.includes('*')) return true;
    // STRICT mode: explicit role with empty grant list → hide everything.
    // Admin must tick menus on the role page to expose them.
    return allowed.includes(menuId);
  },

  _enforceRoleAccess() {
    if (!this.myPermissions || !this.currentPage) return;
    if (this.myPermissions.isOwnerOrAdmin) return;
    const allowed = this.myPermissions.allowedMenuIds || [];
    if (allowed.includes('*')) return;
    if (!this.hasMenuAccess(this.currentPage)) {
      this.toast('คุณไม่มีสิทธิ์เข้าถึงหน้านี้ — กำลังพาไปหน้าแดชบอร์ด', 'error');
      setTimeout(() => { window.location.href = '/app.html'; }, 1500);
    }
  },

  _refreshNavMenu() {
    const nav = document.querySelector('.sidebar-nav');
    if (!nav) return;
    const hidden = this.getHiddenMenuItems();
    const isAdminUser = this.myPermissions?.isOwnerOrAdmin === true;

    // Simple mode collapses the 14-section, 64-item sidebar to a hand-picked
    // shortlist so non-technical users aren't drowned in options. The full
    // navItems are still available via the "ดูเมนูทั้งหมด" link injected at
    // the bottom (which flips uiMode → advanced and reloads).
    const uiMode = localStorage.getItem('uiMode') || 'simple';
    const SIMPLE_ALLOWED = new Set([
      'dashboard',          // หน้าหลัก (Simple Mode home overrides via custom link)
      'getting-started',    // คู่มือเริ่มต้น — สำคัญที่สุดสำหรับผู้ใช้ใหม่
      'documents',          // ขาย
      'recurring',          // invoice รายเดือนอัตโนมัติ — use case หลักของ SME
      'expense',            // จ่าย
      'pos',                // หน้าขาย POS
      'bank',               // ธนาคาร
      'contacts',           // ลูกค้า/ผู้จำหน่าย
      'products',           // สินค้า
      'tax',                // ภพ.30
      'tax-calendar',       // ปฏิทินภาษี
      'reports',            // งบการเงิน
      'aging',              // ค้างรับ-ค้างจ่าย (linked from Simple Mode home)
      'document-scan',      // OCR ถ่ายรูปบิล
      'settings',           // ตั้งค่าบริษัท
    ]);
    // ⚠️ จับคู่ด้วย "ชื่อหมวด" — เปลี่ยนชื่อหมวดใน navItems ต้องอัปเดตชุดนี้ด้วย
    const SIMPLE_SECTIONS = new Set([
      'ขาย / รายรับ', 'ซื้อ / รายจ่าย', 'POS หน้าร้าน',
      'เงิน & ธนาคาร', 'ลูกค้า & สินค้า',
      'ภาษี & e-Filing', 'รายงาน & วิเคราะห์', 'ตั้งค่า & ผู้ใช้',
      'เครื่องมือ: AI · OCR · นำเข้าข้อมูล',  // hosts document-scan (OCR) จาก home strip
    ]);
    const items = this.navItems.filter(item => {
      // section headers + non-item entries pass through; the render loop's
      // flush() then drops sections that end up empty after item filtering.
      if (item.section) return uiMode !== 'simple' || SIMPLE_SECTIONS.has(item.section);
      const visible =
        (!item.id || !hidden.includes(item.id))
        && (!item.id || this.hasMenuAccess(item.id))
        && (!item.adminOnly || isAdminUser);
      if (!visible) return false;
      // เมนูเฉพาะบริษัทจด VAT (ภ.พ.30 / ภาษีซื้อรอ / ภ.พ.30 ย้อนหลัง) — ซ่อน
      // เมื่อบริษัทไม่จด VAT (ไม่มีภาระยื่น). default true → ไม่กระทบถ้ายังไม่โหลด
      if (item.vatOnly && this._vatRegistered === false) return false;
      if (uiMode === 'simple' && item.id && !SIMPLE_ALLOWED.has(item.id)) return false;
      return true;
    });

    // Walk the list: top-level items (no section ancestor) render directly;
    // section markers open a collapsible <details> that wraps every
    // subsequent item until the next section marker.
    const collapsedState = this._loadCollapsedSections();
    const html = [];
    html.push(this._renderNavSearch());

    // Simple-mode shortcuts at the top: หน้าหลัก + the two express flows.
    // Renders as plain nav-items so the existing CSS / active-state styling
    // applies without bespoke selectors.
    if (uiMode === 'simple') {
      html.push(`<a class="nav-item" href="/simple.html"><span class="icon">🏠</span><span class="label">หน้าหลัก (โหมดง่าย)</span></a>`);
      html.push(`<a class="nav-item" href="/pages/quick-sale.html"><span class="icon">⚡</span><span class="label">ขายเร็ว</span></a>`);
      // quick-expense was removed 2026 — it bypassed VAT controls,
      // vendor linkage, and approval workflow. Field-bookkeeping
      // routes through expense.html (มี/ไม่มี ใบเสร็จ §65 ทวิ) or
      // documents.html → ค่าใช้จ่าย which both enforce proper accounting.
      html.push(`<div style="height:1px;background:#e2e8f0;margin:10px 12px;"></div>`);
    }

    let inGroup = false;
    let groupItems = [];
    let currentSection = null;
    const flush = () => {
      if (!inGroup) return;
      if (groupItems.length === 0) { inGroup = false; return; }
      // Auto-expand only the group containing the active page.
      // All other groups default to COLLAPSED (the user-collapsed-by-default
      // model — keeps the sidebar compact on first visit).  An explicit
      // localStorage entry of `false` re-opens a group that's not active.
      const containsActive = groupItems.some(it => it.id === this.currentPage);
      const explicit = collapsedState[currentSection.section];
      const isCollapsed = containsActive ? false : (explicit === false ? false : true);
      const sectionKey = this._sectionI18nKey(currentSection.section);
      const label = sectionKey ? this._t(sectionKey, currentSection.section) : currentSection.section;
      const icon = currentSection.icon || '📁';
      const desc = currentSection.description ? ` title="${this._esc(currentSection.description)}"` : '';
      html.push(`<details class="nav-group" data-section="${currentSection.section}"${isCollapsed ? '' : ' open'}>
        <summary class="nav-group-header"${desc}>
          <span class="icon">${icon}</span>
          <span class="label">${this._esc(label)}</span>
          <span class="count">${groupItems.length}</span>
        </summary>
        <div class="nav-group-items">${groupItems.map(it => this._renderNavItem(it)).join('')}</div>
      </details>`);
      groupItems = [];
      inGroup = false;
    };

    for (const it of items) {
      if (it.section) {
        flush();
        currentSection = it;
        inGroup = true;
      } else if (inGroup) {
        groupItems.push(it);
      } else {
        // top-level item before the first section
        html.push(this._renderNavItem(it));
      }
    }
    flush();

    // Mode-switch footer — gives users a visible way out of either mode.
    if (uiMode === 'simple') {
      html.push(`
        <div style="margin:18px 12px 8px;padding-top:14px;border-top:1px solid #e2e8f0;">
          <a class="nav-item" href="#" onclick="localStorage.setItem('uiMode','advanced'); window.location.reload(); return false;"
             title="แสดงเมนูครบ 64 รายการของระบบบัญชี">
            <span class="icon">⚙️</span><span class="label">ดูเมนูทั้งหมด (มืออาชีพ)</span>
          </a>
        </div>`);
    } else {
      html.push(`
        <div style="margin:18px 12px 8px;padding-top:14px;border-top:1px solid #e2e8f0;">
          <a class="nav-item" href="#" onclick="localStorage.setItem('uiMode','simple'); window.location.href='/simple.html'; return false;"
             title="ซ่อนเมนูซับซ้อน ใช้งานแบบง่าย ๆ">
            <span class="icon">😊</span><span class="label">โหมดง่าย</span>
          </a>
        </div>`);
    }

    nav.innerHTML = html.join('');
    this._wireNavSearch();
    this._wireGroupPersistence();
    this._highlightActiveSection();
  },

  _renderNavSearch() {
    return `<div class="nav-search-wrap"><input type="text" id="navSearchBox"
        class="nav-search" placeholder="🔍 ค้นหาเมนู..." autocomplete="off"
        oninput="Layout._filterNav(this.value)"></div>`;
  },

  _filterNav(query) {
    const q = (query || '').trim().toLowerCase();
    const groups = document.querySelectorAll('.sidebar-nav .nav-group');
    const flatItems = document.querySelectorAll('.sidebar-nav > .nav-item');
    if (!q) {
      groups.forEach(g => { g.style.display = ''; g.querySelectorAll('.nav-item').forEach(a => a.style.display = ''); });
      flatItems.forEach(a => a.style.display = '');
      return;
    }
    flatItems.forEach(a => {
      const t = (a.textContent + ' ' + (a.title || '')).toLowerCase();
      a.style.display = t.includes(q) ? '' : 'none';
    });
    groups.forEach(g => {
      const matches = Array.from(g.querySelectorAll('.nav-item')).filter(a => {
        const t = (a.textContent + ' ' + (a.title || '')).toLowerCase();
        const match = t.includes(q);
        a.style.display = match ? '' : 'none';
        return match;
      });
      if (matches.length > 0) {
        g.style.display = '';
        g.open = true; // force-expand groups with hits
      } else {
        g.style.display = 'none';
      }
    });
  },

  _wireNavSearch() {
    // (no-op currently — input already wires via inline oninput)
  },

  _wireGroupPersistence() {
    document.querySelectorAll('.sidebar-nav .nav-group').forEach(g => {
      g.addEventListener('toggle', () => {
        const state = this._loadCollapsedSections();
        const section = g.dataset.section;
        // Record both states explicitly:
        //   false = "user opened" — keep it open on reload even if not active
        //   true  = "user collapsed" — keep it collapsed
        // (Default behaviour when entry is undefined: collapsed unless active.)
        state[section] = !g.open;
        try { localStorage.setItem('nav.collapsedSections', JSON.stringify(state)); } catch (_) {}
      });
    });
  },

  _loadCollapsedSections() {
    try { return JSON.parse(localStorage.getItem('nav.collapsedSections') || '{}'); }
    catch (_) { return {}; }
  },

  // ===== Global search (header) =====
  _globalSearchTimer: null,
  _onGlobalSearch(q) {
    clearTimeout(this._globalSearchTimer);
    if (!q || q.trim().length < 2) { this._hideGlobalSearch(); return; }
    this._globalSearchTimer = setTimeout(() => this._runGlobalSearch(q.trim()), 250);
  },
  async _runGlobalSearch(query) {
    const cid = this.getCompanyId();
    if (!cid) return;
    const dropdown = document.getElementById('globalSearchDropdown');
    dropdown.style.display = 'block';
    dropdown.innerHTML = '<div style="padding:14px;text-align:center;color:#94a3b8;font-size:13px">กำลังค้นหา...</div>';
    try {
      const res = await API.get(`/api/companies/${cid}/accountant/search?q=${encodeURIComponent(query)}`);
      const d = res.data;
      if (!d.totalHits) {
        dropdown.innerHTML = '<div style="padding:14px;text-align:center;color:#94a3b8;font-size:13px">ไม่พบผลลัพธ์</div>';
        return;
      }
      let html = '';
      if (d.documents.length) {
        html += `<div style="padding:6px 14px;background:#f8fafc;font-size:11px;font-weight:600;color:#475569">📄 เอกสาร (${d.documents.length})</div>`;
        html += d.documents.map(doc => `<a href="/pages/documents.html?id=${doc.id}" style="display:grid;grid-template-columns:1fr auto;gap:8px;padding:8px 14px;border-bottom:1px solid #f1f5f9;text-decoration:none;color:inherit;font-size:13px">
          <div><strong>${this.esc(doc.documentNumber)}</strong> · ${this.esc(doc.documentType)}<br>
            <span style="font-size:11px;color:#64748b">${this.esc(doc.contactName || '-')} · ${this.date(doc.date)}</span></div>
          <div style="text-align:right;font-variant-numeric:tabular-nums">${this.money(doc.totalAmount)}<br>
            <span style="font-size:10px;color:#94a3b8">${this.esc(doc.status)}</span></div>
        </a>`).join('');
      }
      if (d.journalEntries.length) {
        html += `<div style="padding:6px 14px;background:#f8fafc;font-size:11px;font-weight:600;color:#475569">📝 สมุดรายวัน (${d.journalEntries.length})</div>`;
        html += d.journalEntries.map(j => `<a href="/pages/journals.html?entryId=${j.id}" style="display:grid;grid-template-columns:1fr auto;gap:8px;padding:8px 14px;border-bottom:1px solid #f1f5f9;text-decoration:none;color:inherit;font-size:13px">
          <div><strong>${this.esc(j.entryNumber)}</strong> · ${this.esc(j.journalType)}<br>
            <span style="font-size:11px;color:#64748b">${this.esc(j.description || '-')} · ${this.date(j.entryDate)}</span></div>
          <div style="text-align:right;font-variant-numeric:tabular-nums">${this.money(j.totalDebit)}<br>
            <span style="font-size:10px;color:#94a3b8">${this.esc(j.status)}</span></div>
        </a>`).join('');
      }
      if (d.contacts.length) {
        html += `<div style="padding:6px 14px;background:#f8fafc;font-size:11px;font-weight:600;color:#475569">👥 ผู้ติดต่อ (${d.contacts.length})</div>`;
        html += d.contacts.map(c => `<a href="/pages/contacts.html?id=${c.id}" style="display:grid;grid-template-columns:1fr auto;gap:8px;padding:8px 14px;border-bottom:1px solid #f1f5f9;text-decoration:none;color:inherit;font-size:13px">
          <div><strong>${this.esc(c.name)}</strong><br>
            <span style="font-size:11px;color:#64748b">${this.esc(c.taxId || '-')} · ${this.esc(c.contactType)}</span></div>
          <div></div>
        </a>`).join('');
      }
      dropdown.innerHTML = html;
    } catch (e) {
      dropdown.innerHTML = `<div style="padding:14px;color:#dc2626;font-size:13px">${this.esc(e.message)}</div>`;
    }
  },
  _showGlobalSearchResults() {
    const d = document.getElementById('globalSearchDropdown');
    if (d && d.innerHTML.trim()) d.style.display = 'block';
  },
  _hideGlobalSearch() {
    const d = document.getElementById('globalSearchDropdown');
    if (d) d.style.display = 'none';
  },

  // ===== Per-user menu visibility (stored in localStorage, scoped by company) =====
  _menuKey() {
    const cid = (this.subscription && this.subscription.companyId) ||
                (this.user && this.user.companyId) || 'default';
    return `nextacc_hiddenMenu_${cid}`;
  },
  getHiddenMenuItems() {
    // Union of two sources:
    //   1) Per-user localStorage list — controlled by the user via the
    //      sidebar's "ซ่อน" toggle. Personal preference.
    //   2) Server-loaded Owner company-wide hide list (in
    //      this.myPermissions.ownerHiddenMenuIds) — the Owner has
    //      hidden these for EVERYONE in the company via
    //      /pages/settings-features.html.
    let local = [];
    try { local = JSON.parse(localStorage.getItem(this._menuKey()) || '[]'); }
    catch { local = []; }
    const ownerHidden = this.myPermissions?.ownerHiddenMenuIds || [];
    if (!ownerHidden.length) return local;
    return Array.from(new Set([...local, ...ownerHidden]));
  },
  setHiddenMenuItems(ids) {
    localStorage.setItem(this._menuKey(), JSON.stringify(ids || []));
    this._refreshNavMenu();
  },
  toggleMenuItem(id, hidden) {
    const set = new Set(this.getHiddenMenuItems());
    if (hidden) set.add(id); else set.delete(id);
    this.setHiddenMenuItems(Array.from(set));
  },

  _enforcePageAccess() {
    if (!this.subscription || !this.currentPage) return;
    const item = this.navItems.find(n => n.id === this.currentPage);
    if (!item || !item.feature) return;
    if (!this.hasFeature(item.feature)) {
      this.toast(this._t('layout.upgradeRedirect', `ฟีเจอร์ "${item.label}" ไม่อยู่ในแพ็กเกจของคุณ — กำลังพาไปหน้าแพ็กเกจ`, { label: item.label }), 'error');
      setTimeout(() => { window.location.href = '/pages/subscription.html'; }, 1500);
    }
  },

  _t(key, fallback, vars) {
    if (typeof I18n === 'undefined') return fallback;
    const val = I18n.t(key, vars);
    return (val && val !== key) ? val : fallback;
  },

  _renderNavItem(item) {
    if (item.section) {
      const sectionKey = this._sectionI18nKey(item.section);
      const label = sectionKey ? this._t(sectionKey, item.section) : item.section;
      return `<div class="nav-section" data-section="${item.section}">${label}</div>`;
    }
    const active = item.id === this.currentPage ? ' active' : '';
    const label = item._i18nKey ? this._t(item._i18nKey, item.label) : item.label;
    const locked = item.feature && this.subscription && !this.hasFeature(item.feature);
    // Tooltip: the description hover-text. Combine with the lock message
    // when the feature isn't available in the current plan.
    const tooltip = locked
      ? this._t('layout.upgradeLocked', 'Upgrade required').replace('{label}', label) + ' — ' + (item.description || '')
      : (item.description || label);
    const tt = ` title="${this._esc(tooltip)}"`;
    if (locked) {
      return `<a href="/pages/subscription.html" class="nav-item nav-item-locked${active}" data-nav-id="${item.id}"${tt} style="opacity:0.5"><span class="icon">${item.icon}</span>${label}<span style="margin-left:auto;font-size:11px">🔒</span></a>`;
    }
    return `<a href="${item.href}" class="nav-item${active}" data-nav-id="${item.id}"${tt}><span class="icon">${item.icon}</span>${label}</a>`;
  },

  _esc(s) {
    return String(s == null ? '' : s).replace(/[&<>"']/g, c => ({'&':'&amp;','<':'&lt;','>':'&gt;','"':'&quot;',"'":'&#39;'}[c]));
  },

  _sectionI18nKey(section) {
    const map = {
      'หลัก': 'nav.sections.main', 'รายรับ': 'nav.sections.revenue',
      'รายจ่าย': 'nav.sections.expense', 'รายการอัตโนมัติ': 'nav.sections.auto',
      'ผู้ติดต่อ': 'nav.sections.contacts', 'สินค้า/บริการ': 'nav.sections.products',
      'POS ขายหน้าร้าน': 'nav.sections.pos', 'การเงิน': 'nav.sections.finance',
      'บัญชี': 'nav.sections.accounting', 'ภาษี': 'nav.sections.tax',
      'เงินเดือน': 'nav.sections.payroll', 'รายงาน': 'nav.sections.reports',
      'โครงการ/องค์กร': 'nav.sections.projects', 'คลังเอกสาร': 'nav.sections.documents',
      'ตั้งค่า': 'nav.sections.settings',
    };
    return map[section] || null;
  },

  // PWA Service Worker — auto-update when new version deployed
  initServiceWorker() {
    if (!('serviceWorker' in navigator)) return;
    const hadController = !!navigator.serviceWorker.controller;
    navigator.serviceWorker.register('/sw.js').then(reg => {
      // Check for updates every 60s so new deploys appear quickly
      setInterval(() => reg.update().catch(() => {}), 60000);

      reg.addEventListener('updatefound', () => {
        const newWorker = reg.installing;
        if (!newWorker) return;
        newWorker.addEventListener('statechange', () => {
          if (newWorker.state === 'installed' && navigator.serviceWorker.controller) {
            newWorker.postMessage('SKIP_WAITING');
          }
        });
      });

      let refreshing = false;
      navigator.serviceWorker.addEventListener('controllerchange', () => {
        if (refreshing || !hadController) return;
        refreshing = true;
        if (this.toast) this.toast(this._t('layout.systemUpdate', 'ระบบอัปเดตเวอร์ชันใหม่ กำลังโหลด...'), 'info');
        setTimeout(() => window.location.reload(), 500);
      });
    }).catch(() => {});
  },

  // SignalR real-time notifications
  signalRConnection: null,
  initSignalR() {
    if (typeof signalR === 'undefined') {
      // Dynamically load SignalR client if not already loaded.
      // jsdelivr is on the CSP allow-list (cdnjs.cloudflare.com is not),
      // so loading from cdnjs is silently blocked with a console error.
      // Switched to the jsdelivr mirror — same package, same version.
      const script = document.createElement('script');
      script.src = 'https://cdn.jsdelivr.net/npm/@microsoft/signalr@8.0.0/dist/browser/signalr.min.js';
      script.onload = () => this.connectSignalR();
      script.onerror = () => console.warn('[Layout] SignalR client failed to load — real-time notifications disabled');
      document.head.appendChild(script);
    } else {
      this.connectSignalR();
    }
  },

  connectSignalR() {
    const token = localStorage.getItem('token');
    if (!token || typeof signalR === 'undefined') return;
    try {
      this.signalRConnection = new signalR.HubConnectionBuilder()
        .withUrl('/hubs/notifications', { accessTokenFactory: () => token })
        .withAutomaticReconnect()
        .build();

      this.signalRConnection.on('ReceiveNotification', (notification) => {
        this.toast(notification.title || notification.message || this._t('layout.newNotification', 'การแจ้งเตือนใหม่'), 'info');
        this.loadNotificationCount();
      });

      this.signalRConnection.on('RefreshData', () => {
        if (typeof Page !== 'undefined' && Page.load) Page.load();
      });

      this.signalRConnection.start().then(() => {
        const cid = this.getCompanyId();
        if (cid) this.signalRConnection.invoke('JoinCompanyGroup', cid).catch(() => {});
      }).catch(() => {});
    } catch (e) { /* SignalR optional */ }
  },

  // Navigation v2 — grouped + collapsible + with descriptions for tooltips.
  // Structure: top-level items appear first (always visible).  Subsequent
  // `section:` markers open collapsible groups; every item in a group also
  // carries a `description` shown in the hover tooltip.  Active section
  // auto-expands; collapsed state persists in localStorage.
  navItems: [
    { id: 'dashboard', label: 'แดชบอร์ด', icon: '📊', href: '/app.html', feature: 'Dashboard', _i18nKey: 'nav.dashboard',
      description: 'ภาพรวมธุรกิจ — ยอดขาย รายจ่าย ลูกหนี้ เจ้าหนี้ กำไร เปรียบเทียบรายเดือน' },
    { id: 'getting-started', label: 'เริ่มต้นใช้งาน', icon: '🚀', href: '/pages/getting-started.html',
      description: 'คู่มือตั้งค่า 5 ขั้นแรก — ข้อมูลบริษัท → ผังบัญชี → ลูกค้า/สินค้า → เอกสารแรก → ภาษี' },
    { id: 'accountant-workspace', label: 'สำนักงานบัญชี (ทุก client)', icon: '🗂️', href: '/pages/accountant-workspace.html',
      description: 'งานคงค้างรวมทุกบริษัทที่ดูแล · ร่างค้างอนุมัติ · ธนาคารรอ match · สถานะ ภ.พ.30 · ลูกหนี้เกินกำหนด' },
    { id: 'accountant', label: 'เครื่องมือนักบัญชี', icon: '🧮', href: '/pages/accountant.html', feature: 'BasicAccounting', _i18nKey: 'nav.accountant',
      description: 'Pre-close checklist + Sub-Ledger ↔ GL reconciliation + Document completeness — ตรวจสุขภาพระบบรายเดือน' },

    { section: 'ขาย / รายรับ', icon: '📤', description: 'ใบเสนอราคา → ใบแจ้งหนี้/ใบกำกับ → ใบเสร็จ · มัดจำ · รายการเกิดซ้ำ' },
    { id: 'documents', label: 'ขายสินค้า/บริการ', icon: '📄', href: '/pages/documents.html?side=revenue', feature: 'DocumentEngine', _i18nKey: 'nav.documents',
      description: 'ใบเสนอราคา · ใบแจ้งหนี้ · ใบกำกับภาษี · ใบเสร็จ · ใบลดหนี้/เพิ่มหนี้' },
    { id: 'recurring', label: 'รายการเกิดซ้ำ / ส่งอัตโนมัติ', icon: '🔁', href: '/pages/recurring.html', feature: 'DocumentEngine',
      description: 'ตั้ง invoice/ค่าใช้จ่ายรายเดือน · อนุมัติ + ส่งอีเมลอัตโนมัติ · ตัวแปร <<month>> <<year>>' },
    { id: 'deposits', label: 'เงินมัดจำ/รับล่วงหน้า', icon: '🤝', href: '/pages/deposits.html', feature: 'DocumentEngine',
      description: 'มัดจำ/รับล่วงหน้าคงค้าง — ภาษีขายถึงกำหนดทันที (§78) รายได้รอรับรู้ (217xx) จนส่งมอบ · รับรู้รายได้บางส่วนได้' },
    { id: 'mobile-receipt', label: 'รับเงินสดด่วน (มือถือ)', icon: '📱', href: '/pages/mobile-receipt.html', feature: 'AdvancedReporting',
      description: 'หน้าจอ mobile-first สำหรับร้านค้า — numpad + วิธีรับเงิน + พิมพ์ใบเสร็จ 1 click' },
    { id: 'revenue-recognition', label: 'รับรู้รายได้', icon: '📈', href: '/pages/revenue-recognition.html', feature: 'RevenueRecognition', _i18nKey: 'nav.revenueRecognition',
      description: 'ASC 606 / TFRS 15 — รับรู้รายได้ตาม performance obligation' },

    { section: 'ซื้อ / รายจ่าย', icon: '📥', description: 'ใบขอซื้อ → สั่งซื้อ → รับสินค้า → ใบแจ้งหนี้ซื้อ → จ่ายเงิน' },
    { id: 'purchases', label: 'ซื้อสินค้า', icon: '🛒', href: '/pages/purchases.html', feature: 'DocumentEngine', _i18nKey: 'nav.purchases',
      description: 'PR → PO → ใบรับสินค้า (GRN) → ใบกำกับภาษีซื้อ — partial fulfilment support' },
    { id: 'expense-docs', label: 'เอกสารฝั่งซื้อทั้งหมด', icon: '📋', href: '/pages/documents.html?side=expense', feature: 'DocumentEngine', _i18nKey: 'nav.expenseDocs',
      description: 'ใบสำคัญจ่าย · ใบเสร็จรับเงินจากผู้ขาย · ใบลดหนี้/เพิ่มหนี้ฝั่งซื้อ' },
    { id: 'payments', label: 'ชำระเงิน / รวมจ่าย', icon: '💳', href: '/pages/payments.html', feature: 'DocumentEngine', _i18nKey: 'nav.payments',
      description: 'บันทึกการรับ-จ่ายเงิน · จ่ายชำระหลายบิลในใบเดียว · cheque payment' },

    { section: 'เงิน & ธนาคาร', icon: '🏦', description: 'บัญชีธนาคาร · กระทบยอด · เช็ค · เงินสดย่อย · เงินกู้ · FX' },
    { id: 'bank', label: 'บัญชีธนาคาร', icon: '🏦', href: '/pages/bank.html', feature: 'BankReconciliation', _i18nKey: 'nav.bank',
      description: 'นำเข้า statement · M:N reconciliation · AI auto-match จากประวัติ · กลุ่ม net-off' },
    { id: 'cash-management', label: 'จัดการเงินสด & เช็คล่วงหน้า', icon: '🏦', href: '/pages/cash-management.html', feature: 'AdvancedReporting',
      description: 'เช็คล่วงหน้า · เบิก-เคลียร์เงินสด · จ่ายรวม vendor · ใบแจ้งยอดลูกหนี้ — รวมในที่เดียว' },
    { id: 'cheques', label: 'จัดการเช็ค', icon: '✍️', href: '/pages/cheques.html', feature: 'DocumentEngine',
      description: 'เปิดเล่มเช็ค · ออกเช็ค · บันทึกเช็คคืน · ติดตามเช็คคงค้าง — Issued / Cleared / Bounced / Voided' },

    // ───── 👥 พนักงาน (Self-Service) ─────
    // Self-service flows the EMPLOYEE initiates (not the accountant).
    // Distinct from HR/เงินเดือน below which is manager-side. Grouped
    // here so the employee menu is one section away — they don't have
    // to hunt through accounting submenus to file a claim. The "ไม่มี
    // ใบเสร็จ" entry deep-links into expense.html?noReceipt=1 which
    // toggles the §65 ทวิ form mode (reason + optional witness).
    { id: 'petty-cash', label: 'เงินสดย่อย', icon: '🪙', href: '/pages/petty-cash.html', feature: 'BasicAccounting',
      description: 'เปิดเงินสดย่อยตามคน · disbursement · top-up · ตรวจนับ — imprest system' },
    { id: 'cash-forecast', label: 'คาดการณ์กระแสเงินสด', icon: '💰', href: '/pages/cash-forecast.html', feature: 'AdvancedReporting',
      description: 'รายวัน 30/60/90 วัน · เงินเข้า-ออก · ติดลบเมื่อไหร่ · risk alerts · ลูกหนี้/เจ้าหนี้ top' },
    { id: 'loans', label: 'สินเชื่อ / เงินกู้', icon: '💰', href: '/pages/loans.html', feature: 'LoanManagement', _i18nKey: 'nav.loans',
      description: 'จัดการเงินกู้ — ผ่อนต้น+ดอกเบี้ย · ตารางผ่อน · สรุปดอกจ่าย' },
    { id: 'multi-currency', label: 'สกุลเงินต่างประเทศ', icon: '💱', href: '/pages/multi-currency.html', feature: 'MultiCurrency', _i18nKey: 'nav.multiCurrency',
      description: 'อัตราแลกเปลี่ยน · กำไร/ขาดทุนจากอัตราแลกเปลี่ยน · period-end revaluation' },
    { id: 'fx-reval', label: 'FX Revaluation', icon: '💱', href: '/pages/fx-reval.html', feature: 'MultiCurrency',
      description: 'Period-end revalue AR/AP FCY → post JE กำไร/ขาดทุน · idempotent ต่องวด' },

    { section: 'ลูกค้า & สินค้า', icon: '👥', description: 'ข้อมูลหลัก: ลูกค้า ผู้จำหน่าย สินค้า คลัง งานผลิต ฝากขาย' },
    { id: 'contacts', label: 'ลูกค้า / ผู้จำหน่าย', icon: '👥', href: '/pages/contacts.html', feature: 'DocumentEngine', _i18nKey: 'nav.contacts',
      description: 'ฐานข้อมูลลูกค้า / ผู้จำหน่าย — ดึง DBD อัตโนมัติจากเลขผู้เสียภาษี' },
    { id: 'products', label: 'สินค้าและบริการ', icon: '📦', href: '/pages/products.html', feature: 'Inventory', _i18nKey: 'nav.products',
      description: 'สินค้า · บริการ · ราคา · ตัวเลือก · barcode · ภาษีต่อรายการ' },
    { id: 'product-aliases', label: 'ชื่อแทนสินค้า (OCR)', icon: '🏷️', href: '/pages/product-aliases.html', feature: 'Inventory',
      description: 'ชื่อที่ vendor แต่ละเจ้าใช้เรียกสินค้าเรา → OCR/นำเข้า จับคู่สินค้าอัตโนมัติ' },
    { id: 'warehouse', label: 'คลังสินค้า', icon: '🏭', href: '/pages/warehouse.html', feature: 'WarehouseManagement', _i18nKey: 'nav.warehouse',
      description: 'หลายคลัง · โอนระหว่างคลัง · ตรวจนับ · ต้นทุน FIFO/Weighted' },
    { id: 'inventory-reports', label: 'รายงานสินค้าคงเหลือ', icon: '📊', href: '/pages/inventory-reports.html', feature: 'Inventory', _i18nKey: 'nav.inventoryReports',
      description: 'Stock card · Aging stock · Movement · Valuation' },
    { id: 'supplies', label: 'วัสดุสิ้นเปลือง', icon: '🧹', href: '/pages/supplies.html', feature: 'Inventory', _i18nKey: 'nav.supplies',
      description: 'ของใช้ในออฟฟิศ — เบิกตามต้องการ ไม่ตัดสต็อกขาย' },
    { id: 'production', label: 'งานผลิต & BOM', icon: '🏭', href: '/pages/production.html', feature: 'Inventory',
      description: 'Bill of Materials · production order · backflush components → finished goods at WAC' },
    { id: 'consignment', label: 'สินค้าฝากขาย / รับฝาก', icon: '📦', href: '/pages/consignment.html', feature: 'Inventory',
      description: 'Inbound (ของ vendor วางที่เรา จ่ายเมื่อใช้) · Outbound (ของเราอยู่ที่ลูกค้า รับรู้รายได้เมื่อขาย)' },

    { section: 'ภาษี & e-Filing', icon: '🏛️', description: 'ภ.พ.30 · ภงด.1/3/53/54 · e-Tax · ปฏิทินภาษี · Export ยื่นสรรพากร' },
    { id: 'tax', label: 'รายงานภาษี ภพ.30', icon: '🏛️', href: '/pages/tax.html', feature: 'TaxManagement', _i18nKey: 'nav.tax', vatOnly: true,
      description: 'VAT รายเดือน · Defer Input VAT ≤6 เดือน · Filing Lock · Reject & Reverse · Export pipe-delimited RD' },
    { id: 'vat-history', label: 'ภ.พ.30 ย้อนหลัง', icon: '🗄️', href: '/pages/vat-history.html', feature: 'TaxManagement', vatOnly: true,
      description: 'ประวัติรายงาน ภ.พ.30 ทุกเดือนที่ผ่านมา · เปิดดู / พิมพ์ซ้ำ / ตรวจสถานะยื่น' },
    { id: 'undue-vat', label: 'ภาษีซื้อยังไม่ถึงกำหนด', icon: '⏳', href: '/pages/undue-vat.html', feature: 'TaxManagement', vatOnly: true,
      description: 'เอกสารที่ภาษีซื้อพักไว้ 11640 รอใบกำกับครบ §86/4 — เตือนก่อนหมดสิทธิเคลม 6 เดือน (§82/3) · เติมข้อมูลแล้วย้ายเข้า ภ.พ.30' },
    { id: 'wht', label: 'หัก ณ ที่จ่าย (ภงด.)', icon: '📜', href: '/pages/wht.html', feature: 'TaxManagement', _i18nKey: 'nav.wht',
      description: 'ภงด.1/3/53/54 · สร้างหนังสือรับรองหัก ณ ที่จ่าย · Export ยื่นออนไลน์' },
    { id: 'tax-remittance', label: 'นำส่งภาษี / ประกันสังคม', icon: '💸', href: '/pages/tax-remittance.html', feature: 'TaxManagement',
      description: 'ยอดรอนำส่งรวม — สปส.1-10 · ภงด.1/3/53 · ภพ.30 · กำหนดชำระ/เลยกำหนด · ทำจ่าย (ลง JE) · แนบใบเสร็จ' },
    { id: 'tax-calendar', label: 'ปฏิทินภาษี', icon: '📆', href: '/pages/tax-calendar.html', feature: 'TaxManagement', _i18nKey: 'nav.taxCalendar',
      description: 'กำหนดการยื่นภาษี · alert ก่อนถึงวัน due · ติดตามสถานะการยื่น' },
    { id: 'etax', label: 'e-Tax Invoice', icon: '🧾', href: '/pages/etax.html', feature: 'EtaxInvoice', _i18nKey: 'nav.etax',
      description: 'ใบกำกับภาษีอิเล็กทรอนิกส์ — PDF/A-3 + XML ฝัง · ส่งกรมสรรพากร' },
    { id: 'tax-export', label: 'Export ยื่นภาษี / SSO', icon: '📤', href: '/pages/tax-export.html', feature: 'TaxManagement', _i18nKey: 'nav.taxExport',
      description: 'ไฟล์ TXT ตามรูปแบบกรมสรรพากร + ประกันสังคม · ภงด.91 รายปี · RD ACK tracking' },
    { id: 'stamp-duty', label: 'อากรแสตมป์', icon: '🏷️', href: '/pages/stamp-duty.html', feature: 'TaxManagement',
      description: 'ตามประมวลรัษฎากร §103-105 — สัญญาเช่า · กู้ยืม · รับเหมา · มอบอำนาจ · เช็คต่างประเทศ' },

    { section: 'บัญชี', icon: '📚', description: 'ผังบัญชี · สมุดรายวัน · แยกประเภท · งวด/ปิดปี · สินทรัพย์ · รายการปรับปรุง' },
    { id: 'accounts', label: 'ผังบัญชี', icon: '📋', href: '/pages/accounts.html', feature: 'BasicAccounting', _i18nKey: 'nav.accounts',
      description: 'Chart of Accounts ไม่จำกัดระดับ · seed มาตรฐานไทย' },
    { id: 'journals', label: 'สมุดรายวัน', icon: '📝', href: '/pages/journals.html', feature: 'BasicAccounting', _i18nKey: 'nav.journals',
      description: 'JV/SV/UV/RV/PV — สร้าง แก้ไข กลับรายการ (เลือกวันที่ได้) ลบทุกสถานะ' },
    { id: 'general-ledger', label: 'บัญชีแยกประเภท', icon: '📒', href: '/pages/general-ledger.html', feature: 'BasicAccounting', _i18nKey: 'nav.generalLedger',
      description: 'GL พร้อม Opening/Debit/Credit/Ending · drill-down ทุกบัญชีลงไปถึงเอกสาร' },
    { id: 'fiscal', label: 'งวดบัญชี & ปิดสิ้นปี', icon: '📅', href: '/pages/fiscal.html', feature: 'BasicAccounting', _i18nKey: 'nav.fiscal',
      description: 'งวดบัญชี · Soft Close · Year-End Close (auto JE โอน P&L ไป RE)' },
    { id: 'fixed-assets', label: 'ทะเบียนสินทรัพย์', icon: '🏢', href: '/pages/fixed-assets.html', feature: 'FixedAssets', _i18nKey: 'nav.fixedAssets',
      description: 'ทะเบียนสินทรัพย์ · คำนวณค่าเสื่อมราคาอัตโนมัติ · จำหน่าย' },
    { id: 'financial-mgmt', label: 'บริหารการเงิน', icon: '💰', href: '/pages/financial-mgmt.html', feature: 'AdvancedReporting', _i18nKey: 'nav.financialMgmt',
      description: 'Cash flow forecast · กระแสเงินสดล่วงหน้า · liquidity analysis' },

    { section: 'รายงาน & วิเคราะห์', icon: '📊', description: 'งบการเงิน · รายงานผู้บริหาร · aging · งบประมาณ · FP&A' },
    { id: 'reports', label: 'งบการเงิน', icon: '📈', href: '/pages/reports.html', feature: 'BasicAccounting', _i18nKey: 'nav.reports',
      description: 'งบทดลอง · งบดุล · งบกำไรขาดทุน · กระแสเงินสด · งบแสดงการเปลี่ยนแปลงส่วนของเจ้าของ' },
    { id: 'executive-reports', label: 'รายงานผู้บริหาร', icon: '👔', href: '/pages/executive-reports.html', feature: 'AdvancedReporting', _i18nKey: 'nav.executiveReports',
      description: 'KPI สรุปสำหรับผู้บริหาร · trend · benchmark · highlights' },
    { id: 'aging', label: 'อายุลูกหนี้ / เจ้าหนี้', icon: '⏳', href: '/pages/aging.html', feature: 'AgingReport', _i18nKey: 'nav.aging',
      description: 'แยกตามอายุ 30/60/90/120 วัน · alert ค้างชำระ · auto-refresh ทุก 6 ชม.' },
    { id: 'arap-analysis', label: 'วิเคราะห์ AR / AP', icon: '🔍', href: '/pages/arap-analysis.html', feature: 'AdvancedReporting', _i18nKey: 'nav.arapAnalysis',
      description: 'DSO · DPO · cycle time · ลูกค้า top-N · เจ้าหนี้ top-N' },
    { id: 'risk', label: 'ความเสี่ยงลูกค้า / ผู้ขาย', icon: '🎯', href: '/pages/risk.html', feature: 'AdvancedReporting',
      description: 'คะแนนความเสี่ยงผู้ติดต่อ — ลูกค้าจ่ายช้า · vendor void สูง · price volatility' },
    { id: 'budget', label: 'งบประมาณ', icon: '🎯', href: '/pages/budget.html', feature: 'BudgetManagement', _i18nKey: 'nav.budget',
      description: 'ตั้งงบประมาณรายเดือน · เปรียบเทียบ actual vs budget · variance' },
    { id: 'cost-report', label: 'Fix-Variable Cost', icon: '📊', href: '/pages/cost-report.html', feature: 'AdvancedReporting',
      description: 'รายงานต้นทุนคงที่ vs ผันแปร รายเดือนและรายโครงการ' },
    { id: 'fpa', label: 'วิเคราะห์การเงิน (FP&A)', icon: '📉', href: '/pages/fpa.html', feature: 'FPA', _i18nKey: 'nav.fpa',
      description: 'Financial Planning & Analysis · ratio · DuPont · forecast' },

    { section: 'POS หน้าร้าน', icon: '🏪', description: 'ระบบหน้าขาย · แพ็คเกจบริการ · ตัวเลือกสินค้า · รายงานกะ' },
    { id: 'pos', label: 'หน้าขาย POS', icon: '🖥️', href: '/pages/pos.html', feature: 'DocumentEngine', _i18nKey: 'nav.pos',
      description: 'หน้าขายแบบ Touch — รับชำระเงิน พิมพ์ใบเสร็จด่วน' },
    { id: 'pos-packages', label: 'แพ็คเกจบริการ', icon: '💆', href: '/pages/pos-packages.html', feature: 'DocumentEngine', _i18nKey: 'nav.posPackages',
      description: 'ตั้งค่าแพ็คเกจคอร์ส/บริการ — สำหรับสปา คลินิก ฟิตเนส' },
    { id: 'pos-modifiers', label: 'ตัวเลือกสินค้า', icon: '🔧', href: '/pages/pos-modifiers.html', feature: 'DocumentEngine', _i18nKey: 'nav.posModifiers',
      description: 'ไซส์ · สี · รสชาติ · เพิ่ม-ลด ingredient ต่อจาน' },
    { id: 'pos-reports', label: 'รายงาน POS', icon: '📊', href: '/pages/pos-reports.html', feature: 'DocumentEngine', _i18nKey: 'nav.posReports',
      description: 'สรุปยอดขาย · X-Report · Z-Report · ยอดต่อพนักงาน' },

    { section: 'ขายออนไลน์ & Portal', icon: '🌐', description: 'เว็บไซต์ · คำสั่งซื้อ/จองจากเว็บ · lead · portal ลูกค้า/vendor' },
    { id: 'cms-sites', label: 'เว็บไซต์ของฉัน (CMS)', icon: '🌐', href: '/pages/cms-sites.html', feature: 'CmsWebsiteBuilder', _i18nKey: 'nav.cmsSites',
      description: 'สร้างเว็บไซต์ multi-site · e-commerce · booking · เชื่อม ERP อัตโนมัติ' },
    { id: 'cms-orders', label: 'คำสั่งซื้อจากเว็บ', icon: '🛒', href: '/pages/cms-orders.html', feature: 'CmsWebsiteBuilder', _i18nKey: 'nav.cmsOrders',
      description: 'จัดการ order ที่ลูกค้าสั่งผ่านร้านค้าออนไลน์ — ยืนยัน · จัดส่ง · ติดตาม' },
    { id: 'cms-bookings', label: 'การจองจากเว็บ', icon: '📅', href: '/pages/cms-bookings.html', feature: 'CmsWebsiteBuilder', _i18nKey: 'nav.cmsBookings',
      description: 'จัดการการจอง — ร้านอาหาร · สปา · คลินิก · โรงแรม · ยืนยัน-ยกเลิก-No show' },
    { id: 'cms-leads', label: 'คำขอ / Lead', icon: '📨', href: '/pages/cms-leads.html', feature: 'CmsWebsiteBuilder', _i18nKey: 'nav.cmsLeads',
      description: 'RFQ · นัดดูทรัพย์ · นัด demo · สมัครเรียน · ขอใบเสนอราคา — sales funnel ครบ' },
    { id: 'customer-portal', label: 'Portal ลูกค้า', icon: '🏪', href: '/pages/customer-portal.html', feature: 'CustomerPortal', _i18nKey: 'nav.customerPortal',
      description: 'ให้ลูกค้าเข้าดูใบแจ้งหนี้ · ชำระเงิน · ดาวน์โหลดเอกสาร' },
    { id: 'vendor-portal-admin', label: 'Vendor Portal — ออก token', icon: '🤝', href: '/pages/vendor-portal-admin.html', feature: 'MultiUser',
      description: 'AP ออก magic-link ให้ vendor เข้าดู PO + invoice + status + อัปโหลด invoice ใหม่ — ไม่ต้องมี user' },

    { section: 'HR & เงินเดือน', icon: '👤', description: 'พนักงาน · เงินเดือน/สปส. · ลา · เงินทดรอง · คอมมิชชัน' },
    { id: 'organization', label: 'โครงสร้างองค์กร', icon: '🏢', href: '/pages/organization.html', feature: 'Payroll', _i18nKey: 'nav.organization',
      description: 'แผนก · ตำแหน่ง · ผู้บังคับบัญชา · org chart · routing การอนุมัติ' },
    { id: 'employees', label: 'พนักงาน (HR Master)', icon: '👥', href: '/pages/employees.html', feature: 'Payroll',
      description: 'สร้าง · จัดการรายละเอียด · ตั้งฐานเงินเดือน/วัน/ชั่วโมง · sync 2 ทางกับ HRIS ภายนอก' },
    { id: 'payroll', label: 'ระบบเงินเดือน & ลา', icon: '💵', href: '/pages/payroll.html', feature: 'Payroll', _i18nKey: 'nav.payroll',
      description: 'พนักงาน · รอบจ่าย · ภงด.1 · ประกันสังคม · กองทุน · ลาหยุด' },
    { id: 'leave-types', label: 'ตั้งค่าประเภทลา + วันหยุด', icon: '📅', href: '/pages/leave-types.html', feature: 'Payroll',
      description: 'HR Admin · กำหนดประเภทการลา · โควต้า · ปฏิทินวันหยุดประจำปี' },
    { id: 'leave-calendar', label: 'ปฏิทินการลา (HR view)', icon: '🗓️', href: '/pages/leave-calendar.html', feature: 'Payroll',
      description: 'ดูทุกคนลาช่วงไหน + วันหยุดประจำปี · วางแผนกำลังคน' },
    { id: 'salary-advance', label: 'เงินทดรองจ่ายพนักงาน', icon: '💰', href: '/pages/salary-advance.html', feature: 'Payroll', _i18nKey: 'nav.salaryAdvance',
      description: 'เงินยืม-เคลียร์ · workflow อนุมัติ · หักจากเงินเดือนอัตโนมัติ' },
    { id: 'commission', label: 'คอมมิชชัน', icon: '💸', href: '/pages/commission.html', feature: 'Commission', _i18nKey: 'nav.commission',
      description: 'กฎคอมมิชชันต่อพนักงาน · คำนวณจากยอดขาย · เข้ารวมเงินเดือน' },
    { id: 'project-time', label: 'เวลาทำงาน-โครงการ', icon: '🕒', href: '/pages/project-time.html', feature: 'ProjectAccounting',
      description: 'บันทึก/sync ชั่วโมงทำงานของพนักงานต่อโครงการ · ใช้กระจาย labour cost ลงโปรเจค' },

    { section: 'พนักงาน (Self-Service)', icon: '🙋', description: 'สิ่งที่พนักงานทำเอง: เบิกค่าใช้จ่าย · ขอลา · ดูสลิป' },
    { id: 'expense', label: 'เบิกค่าใช้จ่าย (มีใบเสร็จ)', icon: '🧾', href: '/pages/expense.html', feature: 'ExpenseManagement', _i18nKey: 'nav.expense',
      description: 'พนักงานออกเงินก่อน → ส่ง manager อนุมัติ → บริษัทคืนเงิน' },
    { id: 'expense-no-receipt', label: 'เบิกค่าใช้จ่าย (ไม่มีใบเสร็จ)', icon: '📝', href: '/pages/expense.html?noReceipt=1', feature: 'ExpenseManagement',
      description: '§65 ทวิ — กรณี vendor ออกใบเสร็จไม่ได้ (ตลาดสด · taxi · ใบเสร็จหาย) → อนุมัติแล้วระบบสร้างใบรับรองแทนใบเสร็จให้อัตโนมัติ' },
    { id: 'leave-my', label: 'ขอลา / ดูสิทธิ์ลา', icon: '🏖️', href: '/pages/leave.html', feature: 'Payroll',
      description: 'ดูโควต้าลาคงเหลือ · ขอลาใหม่ · ดูประวัติของฉัน · รองรับครึ่งวัน' },
    { id: 'mobile-expense', label: 'เบิกค่าใช้จ่าย (มือถือ)', icon: '📱', href: '/mobile-expense.html', feature: 'ExpenseManagement',
      description: 'หน้าเบิกค่าใช้จ่ายแบบ mobile — ถ่ายรูปใบเสร็จ + กรอกยอด + ส่งจากภาคสนาม' },
    // quick-expense was removed 2026 — bypassed VAT controls, vendor
    // linkage, approval workflow; created data that failed audit. All
    // field expenses now go through expense.html (มี/ไม่มี ใบเสร็จ)
    // which enforces §65 ทวิ when no receipt + creates a proper
    // ExpenseClaim with HR audit trail.

    { section: 'โครงการ & กลุ่มบริษัท', icon: '🏗️', description: 'โครงการ · บันทึกเวลา · cost center · ระหว่างบริษัท · งบรวม' },
    { id: 'projects', label: 'โครงการ', icon: '📐', href: '/pages/projects.html', feature: 'ProjectAccounting', _i18nKey: 'nav.projects',
      description: 'job cost · งบประมาณต่อโครงการ · ติดตามรายได้/ค่าใช้จ่าย' },
    { id: 'time-billing', label: 'บันทึกเวลา', icon: '⏱️', href: '/pages/time-billing.html', feature: 'TimeBilling', _i18nKey: 'nav.timeBilling',
      description: 'timesheet · บิลตามชั่วโมง · ติดตามกำไรต่อโปรเจค' },
    { id: 'dimensions', label: 'สาขา & มิติ (Cost Center)', icon: '🏬', href: '/pages/dimensions.html', feature: 'CostCenter', _i18nKey: 'nav.dimensions',
      description: 'cost center หลายมิติ · กระจาย JE ตามแผนก/สาขา · รายงานต่อมิติ' },
    { id: 'intercompany', label: 'ระหว่างบริษัท', icon: '🔗', href: '/pages/intercompany.html', feature: 'MultiCompany', _i18nKey: 'nav.intercompany',
      description: 'ธุรกรรมข้ามบริษัทในเครือ · auto-mirror · eliminate ตอนรวมงบ' },
    { id: 'consolidation', label: 'งบการเงินรวม', icon: '📑', href: '/pages/consolidation.html', feature: 'Consolidation', _i18nKey: 'nav.consolidation',
      description: 'รวมงบทุกบริษัทในเครือ · FX translation · NCI · elimination entries' },

    { section: 'เครื่องมือ: AI · OCR · นำเข้าข้อมูล', icon: '🤖', description: 'สแกนเอกสาร · AI ช่วยงาน · นำเข้า/ส่งออก · ย้ายจากระบบเดิม' },
    { id: 'document-scan', label: 'สแกนเอกสาร (OCR)', icon: '📸', href: '/pages/document-scan.html', feature: 'AI_Features', _i18nKey: 'nav.documentScan',
      description: 'สแกนใบเสร็จ-ใบกำกับด้วยกล้อง · Azure DI + Tesseract · RD compliance check' },
    { id: 'ai-tools', label: 'AI อัจฉริยะ', icon: '🤖', href: '/pages/ai-tools.html', feature: 'AI_Features', _i18nKey: 'nav.aiTools',
      description: 'auto-categorize · anomaly · cash-flow forecast · vendor canon · GL suggestion' },
    { id: 'import-export', label: 'นำเข้า/ส่งออกข้อมูล', icon: '📥', href: '/pages/import-export.html', feature: 'BulkImport', _i18nKey: 'nav.importExport',
      description: 'นำเข้า Excel ทีละ batch · ส่งออกข้อมูลเป็น CSV/Excel · backup' },
    { id: 'migrate-competitor', label: 'ย้ายจาก Express/PEAK/FlowAccount', icon: '🔁', href: '/pages/migrate-competitor.html', feature: 'BulkImport',
      description: 'sniff รูปแบบไฟล์ + preview + dry-run import — ลูกค้าจากระบบบัญชีอื่นย้ายมาง่าย' },
    { id: 'migration-wizard', label: 'นำเข้าข้อมูลเดิม', icon: '🔄', href: '/pages/migration-wizard.html', feature: 'BasicAccounting',
      description: 'Wizard 3 ขั้น: Upload legacy COA → Map → Validate → Commit opening balances' },
    { id: 'import-conflicts', label: 'แก้ข้อมูลซ้ำจากการนำเข้า', icon: '🔀', href: '/pages/import-conflicts.html', feature: 'AdvancedReporting',
      description: 'เมื่อ import เจอข้อมูลที่ซ้ำกับในระบบ — เลือก side-by-side ว่าจะใช้ของเดิมหรือใหม่ ต่อแถว/ทั้งหมด' },

    { section: 'ตั้งค่า & ผู้ใช้', icon: '⚙️', description: 'บริษัท · ทีม/สิทธิ์ · อนุมัติ · เทมเพลต · แจ้งเตือน · PDPA' },
    { id: 'settings', label: 'ตั้งค่าบริษัท', icon: '⚙️', href: '/pages/settings.html', _i18nKey: 'nav.settings',
      description: 'ข้อมูลบริษัท · logo · เลขผู้เสียภาษี · default บัญชี · เลขเอกสาร · SMTP' },
    // หมายเหตุ: design-system.html เป็นคู่มือ design token/component สำหรับ
    // นักพัฒนา — ไม่ใช่ฟีเจอร์สำหรับผู้ใช้ระบบบัญชี จึงถอดออกจากเมนู (ยังเปิด
    // ตรงผ่าน URL /pages/design-system.html ได้สำหรับทีมพัฒนา).
    { id: 'sme-config', label: 'ตั้งค่าขั้นสูง (อนุมัติ·สต๊อก·กะ)', icon: '⚙️', href: '/pages/sme-config.html', feature: 'AdvancedReporting',
      description: 'Approval workflow · Schedule reports · ส่วนลดเงินสด · Stock transfer · Sample data — รวมไว้ที่เดียว' },
    { id: 'email-schedule', label: 'ส่งอีเมลอัตโนมัติ', icon: '📧', href: '/pages/email-schedule.html',
      description: 'ตั้งกฎส่งใบกำกับ/สลิป/ใบ 50ทวิ ตามวันที่กำหนด · เตือนใกล้/เกินกำหนดชำระ · ดูคิว' },
    { id: 'notifications', label: 'การแจ้งเตือน (Notification Engine)', icon: '🔔', href: '/pages/notifications.html', _i18nKey: 'nav.notifications',
      description: 'Matrix ตั้งค่าแจ้งเตือนต่อ event/role · System · Email · LINE · per-user preferences' },
    { id: 'team', label: 'จัดการทีม', icon: '👥', href: '/pages/team.html', feature: 'MultiUser', _i18nKey: 'nav.team',
      description: 'เชิญสมาชิก · กำหนด role ต่อคน · เปิด/ปิดสิทธิ์' },
    { id: 'roles', label: 'จัดการ Role / สิทธิ์', icon: '🔐', href: '/pages/roles.html', feature: 'MultiUser', _i18nKey: 'nav.roles',
      description: 'สร้าง role · template (POS Cashier / Inventory Clerk / etc.) · ติ๊ก perm:* keys' },
    { id: 'approval', label: 'กฎอนุมัติตามวงเงิน (Workflow)', icon: '✅', href: '/pages/approval.html', feature: 'ApprovalWorkflow', _i18nKey: 'nav.approval',
      description: 'ตั้งกฎ: เอกสารประเภท/วงเงินไหน ต้องผ่านใครอนุมัติก่อน — ปุ่มอนุมัติปกติจะถูกล็อคจนกว่าจะผ่านครบ · ต่างจาก "ลายเซ็น": อันนี้คุมสิทธิ์ อันนั้นเก็บลายเซ็นบน PDF' },
    { id: 'signatures', label: 'ลายเซ็น & ส่งเซ็นอนุมัติ', icon: '✍️', href: '/pages/signatures.html', feature: 'ApprovalWorkflow', _i18nKey: 'nav.signatures',
      description: 'จัดการภาพลายเซ็นของฉัน + ส่งเอกสารให้เซ็นทีละคน (ภายใน/ลูกค้าออนไลน์) — ลายเซ็นประทับลง PDF; เซ็นครบระบบอนุมัติ+ลงบัญชีให้อัตโนมัติ' },
    { id: 'sensitivity', label: 'สิทธิ์ดูเอกสารลับ', icon: '🔒', href: '/pages/sensitivity.html', adminOnly: true,
      description: 'กำหนดใครเห็นเอกสารกลุ่มอ่อนไหว (payroll / ผู้บริหาร) — sensitivity gate' },
    { id: 'document-templates', label: 'เทมเพลตเอกสาร PDF', icon: '🎨', href: '/pages/document-templates.html', feature: 'DocumentEngine',
      description: 'ปรับ logo · สี · font · header · footer · watermark · ลายเซ็น — preview สด · per-document-type' },
    { id: 'settings-features', label: 'ฟีเจอร์ & เมนู (Owner)', icon: '🧩', href: '/pages/settings-features.html', adminOnly: true,
      description: 'เจ้าของกิจการเลือกเปิด/ปิดฟีเจอร์ + ซ่อนเมนูที่ไม่ใช้ ใช้ได้ทุกคนในบริษัท' },
    { id: 'pdpa', label: 'PDPA — สิทธิ์เจ้าของข้อมูล', icon: '🛡️', href: '/pages/pdpa.html', adminOnly: true,
      description: 'พ.ร.บ.คุ้มครองข้อมูลส่วนบุคคล §32 — รับคำขอ Access/Erasure/Rectification + ติดตาม DPO + erasure impact' },
    { id: 'cleanup', label: 'เริ่มต้นใหม่ (ล้างข้อมูลทดลอง)', icon: '🧨', href: '/pages/cleanup.html', adminOnly: true,
      description: 'ลบข้อมูลทดลองทั้งชุดก่อนขึ้นใช้งานจริง — ระวัง: ย้อนกลับไม่ได้' },

    { section: 'Developer / API', icon: '🔧', description: 'เชื่อมต่อระบบภายนอก · API key · webhook' },
    { id: 'integrations', label: 'เชื่อมต่อระบบ', icon: '🔗', href: '/pages/integrations.html', feature: 'APIAccess', _i18nKey: 'nav.integrations',
      description: 'API key สำหรับ external system · mapping บัญชี · sync log' },
    { id: 'api-developer', label: 'API Documentation', icon: '📘', href: '/pages/api-developer.html', feature: 'APIAccess', _i18nKey: 'nav.apiDeveloper',
      description: 'เอกสาร REST API สำหรับ developer · endpoint list · sample payload' },
    { id: 'webhooks', label: 'Webhooks', icon: '🔌', href: '/pages/webhooks.html', feature: 'Webhook', _i18nKey: 'nav.webhooks',
      description: 'ส่ง event ออกไประบบอื่นเมื่อมีการเปลี่ยนแปลง · subscribe/unsubscribe' },

    { section: 'บัญชีผู้ใช้', icon: '💎', description: 'License · แพ็กเกจ · การใช้งาน · audit log' },
    { id: 'account-subscription', label: 'License ของฉัน', icon: '🎫', href: '/pages/account-subscription.html',
      description: 'แพ็กเกจหลัก (User-level) ครอบหลายบริษัทใต้ License เดียว — แนะนำสำหรับเจ้าของหลายบริษัท / นักบัญชีดูแลหลายลูกค้า' },
    { id: 'subscription', label: 'แพ็กเกจของบริษัทนี้', icon: '💎', href: '/pages/subscription.html', _i18nKey: 'nav.subscription',
      description: 'แพ็กเกจระดับบริษัท (Company-level) — ใช้เมื่อต้องการแยกบิลแยกใบกำกับ' },
    { id: 'usage', label: 'สถานะการใช้งาน', icon: '📊', href: '/pages/usage.html', _i18nKey: 'nav.usage',
      description: 'การใช้งานเทียบกับ limit · จำนวนเอกสาร · ผู้ใช้ · storage' },
    { id: 'audit', label: 'บันทึกกิจกรรม (Audit)', icon: '🔍', href: '/pages/audit.html', feature: 'AuditLog', _i18nKey: 'nav.audit',
      description: 'ประวัติทุก action ในระบบ · ใคร · เมื่อไหร่ · IP · เปลี่ยนอะไร' },
  ],

  render() {
    // Create sidebar
    const sidebar = document.createElement('aside');
    sidebar.className = 'sidebar';
    sidebar.id = 'sidebar';
    const tSelectCo = this._t('nav.selectCompany', '-- เลือกบริษัท --');
    const tLogout = this._t('nav.logout', 'ออกจากระบบ');
    sidebar.innerHTML = `
      <div class="sidebar-header">
        <div class="sidebar-logo"><span>Next Acc</span></div>
      </div>
      <div style="padding:12px 16px;border-bottom:1px solid var(--gray-800)">
        <select id="companySelect" class="form-select" style="background:var(--gray-800);color:#fff;border-color:var(--gray-700);font-size:13px;padding:8px 10px">
          <option value="">${this.esc(tSelectCo)}</option>
        </select>
      </div>
      <nav class="sidebar-nav">
        ${this.navItems.filter(item => (!item.id || this.hasMenuAccess(item.id)) && (!item.adminOnly || this.myPermissions?.isOwnerOrAdmin === true)).map(item => this._renderNavItem(item)).join('')}
      </nav>
      <div class="sidebar-footer">
        <a href="#" class="nav-item" onclick="Layout.logout();return false"><span class="icon">🚪</span>${this.esc(tLogout)}</a>
      </div>
    `;

    // Create header
    const header = document.createElement('header');
    header.className = 'app-header';
    const tNotif = this._t('layout.notifications', 'การแจ้งเตือน');
    const tUser = this._t('nav.user', 'ผู้ใช้');
    const tSettings = this._t('layout.settings', 'ตั้งค่า');
    header.innerHTML = `
      <div class="header-left">
        <button class="mobile-toggle" onclick="Layout.toggleSidebar()">☰</button>
        <h1 class="header-title" id="headerTitle"></h1>
      </div>
      <div class="header-center" style="flex:1;max-width:480px;margin:0 16px;position:relative">
        <input type="text" id="globalSearchInput" placeholder="🔍 ค้นหา หรือกด Ctrl+K สำหรับคำสั่ง"
          autocomplete="off" oninput="Layout._onGlobalSearch(this.value)"
          onblur="setTimeout(()=>Layout._hideGlobalSearch(),200)"
          onfocus="if(this.value.length>=2)Layout._showGlobalSearchResults()"
          style="width:100%;padding:8px 56px 8px 12px;border:1px solid #e5e7eb;border-radius:8px;font-size:13px;background:#f9fafb"
          title="พิมพ์เลขเอกสาร / เลข JE / ชื่อลูกค้า / จำนวนเงิน เพื่อค้นหาทั่วระบบ — หรือกด Ctrl+K (⌘K) เปิด command palette ข้ามไปหน้าใดก็ได้">
        <kbd style="position:absolute;right:10px;top:50%;transform:translateY(-50%);font-size:10px;color:#94a3b8;background:#fff;border:1px solid #e5e7eb;border-radius:4px;padding:1px 5px;pointer-events:none;font-family:inherit">Ctrl+K</kbd>
        <div id="globalSearchDropdown" style="display:none;position:absolute;top:100%;left:0;right:0;margin-top:4px;background:#fff;border:1px solid #e5e7eb;border-radius:8px;box-shadow:0 8px 24px rgba(0,0,0,.12);max-height:480px;overflow-y:auto;z-index:1100"></div>
      </div>
      <div class="header-right">
        <div id="appLangSwitcher" style="margin-right:8px"></div>
        <button class="header-icon-btn" onclick="Layout.toggleNotifications()" title="${this.esc(tNotif)}">
          🔔<span class="badge-dot hidden" id="notifDot"></span>
        </button>
        <div class="dropdown">
          <div class="header-user" onclick="this.nextElementSibling.classList.toggle('show')">
            <div class="header-avatar">${this.esc((this.user?.fullName || 'U').charAt(0))}</div>
            <span class="text-sm font-medium">${this.esc(this.user?.fullName || tUser)}</span>
          </div>
          <div class="dropdown-menu" id="userDropdown">
            <a class="dropdown-item" href="/pages/account-subscription.html">🎫 License ของฉัน</a>
            <a class="dropdown-item" href="/pages/settings.html">⚙️ ${this.esc(tSettings)}</a>
            <div class="dropdown-divider"></div>
            <a class="dropdown-item" href="#" onclick="Layout.logout();return false">🚪 ${this.esc(tLogout)}</a>
          </div>
        </div>
      </div>
    `;

    // Wrap page content
    const pageContent = document.getElementById('pageContent');
    if (!pageContent) {
      // Page missed migration to the standard `<div id="pageContent">`
      // wrapper. Log + bail instead of throwing on appendChild(null),
      // which used to leave the user with no sidebar AND no rendered
      // page content (body.innerHTML was wiped after the throw).
      console.warn('[Layout] missing <div id="pageContent"> — skipping render. Page:', this.currentPage);
      return;
    }
    const mainContent = document.createElement('div');
    mainContent.className = 'main-content';
    mainContent.appendChild(header);
    mainContent.appendChild(pageContent);

    // Sidebar overlay for mobile
    const overlay = document.createElement('div');
    overlay.className = 'sidebar-overlay';
    overlay.id = 'sidebarOverlay';
    overlay.onclick = () => Layout.toggleSidebar();

    const appLayout = document.createElement('div');
    appLayout.className = 'app-layout';
    appLayout.appendChild(sidebar);
    appLayout.appendChild(overlay);
    appLayout.appendChild(mainContent);

    document.body.innerHTML = '';
    document.body.appendChild(appLayout);

    // Toast container
    const tc = document.createElement('div');
    tc.className = 'toast-container';
    tc.id = 'toastContainer';
    document.body.appendChild(tc);

    // Floating Action Button (FAB) ถูกถอดออกตามคำขอเจ้าของโปรเจกต์ —
    // ปุ่ม "＋ Quick" ลอยมุมขวาล่างบังเนื้อหา/ปุ่มในหน้า. ทางลัดสร้างรายการ
    // ยังเข้าถึงได้จาก sidebar + mobile bottom-nav ด้านล่าง. (_toggleFab()
    // null-safe อยู่แล้วเมื่อไม่มี #fabMenu)
    {
      // Mobile bottom-nav for Simple Mode — provides thumb-reachable nav on
      // phones where the sidebar is hidden behind the hamburger. Only renders
      // when uiMode=simple so power users keep their full sidebar UX.
      if ((localStorage.getItem('uiMode') || 'simple') === 'simple') {
        const bn = document.createElement('nav');
        bn.id = 'mobileBottomNav';
        bn.innerHTML = `
          <a href="/simple.html"><span>🏠</span><span>หน้าหลัก</span></a>
          <a href="/pages/quick-sale.html"><span>💰</span><span>ขาย</span></a>
          <a href="/pages/expense.html"><span>🧾</span><span>เบิก</span></a>
          <a href="/pages/bank.html"><span>🏦</span><span>เงิน</span></a>
          <a href="/pages/tax-calendar.html"><span>🏛️</span><span>ภาษี</span></a>`;
        document.body.appendChild(bn);
      }
    }

    // Notification panel
    const np = document.createElement('div');
    np.className = 'modal-overlay';
    np.id = 'notifPanel';
    const tnTitle = this._t('layout.notifications', 'การแจ้งเตือน');
    const tnEmpty = this._t('layout.noNotifications', 'ไม่มีการแจ้งเตือน');
    np.innerHTML = `<div class="modal" style="max-width:420px"><div class="modal-header"><h3 class="modal-title">${this.esc(tnTitle)}</h3><button class="modal-close" onclick="Layout.closeNotifications()">&times;</button></div><div class="modal-body" id="notifList" style="max-height:400px;overflow-y:auto"><p class="text-gray-500 text-sm text-center" style="padding:20px">${this.esc(tnEmpty)}</p></div></div>`;
    document.body.appendChild(np);

    if (typeof I18n !== 'undefined') {
      I18n.renderSwitcher('appLangSwitcher');
      I18n.apply();
    }
    this._highlightActiveSection();
    this.loadCompanies();
  },

  _highlightActiveSection() {
    const activeItem = document.querySelector('.nav-item.active');
    if (!activeItem) return;
    let el = activeItem.previousElementSibling;
    while (el && !el.classList.contains('nav-section')) el = el.previousElementSibling;
    if (el) el.classList.add('section-active');
    requestAnimationFrame(() => {
      activeItem.scrollIntoView({ block: 'center', behavior: 'instant' });
    });
  },

  _companiesLoaded: false,

  async loadCompanies() {
    try {
      const res = await API.get('/api/company');

      // Guard: if API failed (e.g. network error), don't disrupt current state
      if (!res || !res.success) {
        // Still try to load page data with cached company
        if (this.currentCompany?.id) {
          if (typeof Dashboard !== 'undefined' && Dashboard.load) Dashboard.load();
          else if (typeof Page !== 'undefined' && Page.load) Page.load();
        }
        return;
      }

      const companies = res.data?.items || res.data || [];
      const select = document.getElementById('companySelect');

      if (companies.length === 0) {
        // No companies — show create prompt
        select.innerHTML = `<option value="">${this.esc(this._t('nav.noCompany', 'ยังไม่มีบริษัท'))}</option>`;
        this.showCompanySetupPrompt();
        return;
      }

      // Store companies list for later use (e.g. company select change handler)
      this.companies = companies;

      // Clear default "-- เลือกบริษัท --" and populate with actual companies
      select.innerHTML = '';
      companies.forEach(c => {
        const opt = document.createElement('option');
        opt.value = c.id;
        opt.textContent = c.name;
        if (this.currentCompany?.id === c.id) opt.selected = true;
        select.appendChild(opt);
      });
      // "+ สร้างบริษัทใหม่" sentinel — triggers the setup overlay so users
      // who already have one company can spin up another from the same login.
      const sep = document.createElement('option');
      sep.disabled = true; sep.textContent = '──────────';
      select.appendChild(sep);
      const newOpt = document.createElement('option');
      newOpt.value = '__create_new__';
      newOpt.textContent = '+ สร้างบริษัทใหม่';
      select.appendChild(newOpt);

      // Auto-select if only 1 company or no company selected
      if (!this.currentCompany || !companies.find(c => c.id === this.currentCompany.id)) {
        this.currentCompany = companies[0];
        localStorage.setItem('currentCompany', JSON.stringify(companies[0]));
        select.value = companies[0].id;
      } else {
        // Refresh localStorage with full company data from API
        const fresh = companies.find(c => c.id === this.currentCompany.id);
        if (fresh) {
          // isSetupComplete is sticky: once true locally, keep true even if API returns false
          if (this.currentCompany.isSetupComplete && !fresh.isSetupComplete) {
            fresh.isSetupComplete = true;
          }
          this.currentCompany = fresh;
          localStorage.setItem('currentCompany', JSON.stringify(fresh));
        }
      }

      // Show setup reminder on dashboard if setup not complete (no forced redirect)
      if (this.currentCompany && !this.currentCompany.isSetupComplete) {
        const path = window.location.pathname;
        if (path === '/app.html' || path === '/') {
          this.showSetupReminder();
        }
      }

      this._companiesLoaded = true;

      // Load subscription/features for the selected company (no await - menu refreshes when ready)
      this.loadSubscription();

      // Load page data with selected company (use load() not init() to avoid re-init loop)
      if (typeof Dashboard !== 'undefined' && Dashboard.load) Dashboard.load();
      else if (typeof Page !== 'undefined' && Page.load) Page.load();
      else if (typeof Page !== 'undefined' && Page.init) Page.init();
    } catch (e) { console.warn('Could not load companies:', e); }
  },

  showSetupReminder() {
    const pageContent = document.getElementById('pageContent');
    if (!pageContent) return;
    // Don't add duplicate
    if (document.getElementById('setupReminder')) return;
    const banner = document.createElement('div');
    banner.id = 'setupReminder';
    banner.style.cssText = 'background:linear-gradient(135deg,#4F46E5,#4338ca);color:#fff;padding:16px 24px;border-radius:12px;margin-bottom:20px;display:flex;align-items:center;justify-content:space-between;gap:16px';
    const tsTitle = this._t('layout.setupComplete', 'ตั้งค่าบริษัทให้เสร็จสมบูรณ์');
    const tsDesc = this._t('layout.setupCompleteDesc', 'กรอกข้อมูลบริษัทเพื่อออกเอกสารภาษีและรายงานได้ถูกต้อง');
    const tsBtn = this._t('layout.setupNow', 'ตั้งค่าเลย');
    banner.innerHTML = `
      <div>
        <strong style="font-size:1rem">⚙️ ${this.esc(tsTitle)}</strong>
        <p style="margin:4px 0 0;font-size:0.875rem;opacity:0.9">${this.esc(tsDesc)}</p>
      </div>
      <a href="/pages/settings.html?setup=1" class="btn" style="background:rgba(255,255,255,0.2);color:#fff;border:1px solid rgba(255,255,255,0.3);white-space:nowrap">${this.esc(tsBtn)}</a>`;
    pageContent.insertBefore(banner, pageContent.firstChild);
  },

  setupStep: 1,

  showCompanySetupPrompt(opts) {
    this.setupStep = 1;
    this._setupData = {};
    this._setupForNewCompany = !!(opts && opts.forNewCompany);
    this.renderSetupStep();
  },

  renderSetupStep() {
    const pageContent = document.getElementById('pageContent');
    if (!pageContent) return;

    const steps = [
      { num: 1, label: 'ข้อมูลกิจการ' },
      { num: 2, label: 'ที่อยู่' },
      { num: 3, label: 'ภาษีและบัญชี' },
    ];

    const stepBar = `<div style="display:flex;justify-content:center;gap:8px;margin-bottom:32px">
      ${steps.map(s => `<div style="display:flex;align-items:center;gap:6px">
        <div style="width:28px;height:28px;border-radius:50%;display:flex;align-items:center;justify-content:center;font-size:13px;font-weight:600;
          ${this.setupStep >= s.num ? 'background:var(--primary);color:#fff' : 'background:var(--gray-200);color:var(--gray-500)'}">
          ${this.setupStep > s.num ? '&#10003;' : s.num}</div>
        <span style="font-size:13px;color:${this.setupStep >= s.num ? 'var(--gray-900)' : 'var(--gray-400)'}">${s.label}</span>
        ${s.num < 3 ? '<span style="color:var(--gray-300);margin:0 4px">—</span>' : ''}
      </div>`).join('')}
    </div>`;

    let formContent = '';
    if (this.setupStep === 1) {
      // For users creating their 2nd+ company, hint that the new company will
      // automatically use their existing License (Account Plan) so they don't
      // worry about paying twice. First-time users see the normal hint only.
      const licenseHint = this._setupForNewCompany
        ? `<div style="padding:10px 14px;background:#ecfeff;border-radius:8px;border:1px solid #67e8f9;margin-bottom:12px;font-size:13px;color:#0c4a6e">
            🎫 <strong>License ของคุณจะคุ้มครองบริษัทใหม่อัตโนมัติ</strong> — ถ้ายังเหลือ slot จะถูกผูกเข้า Plan ส่วนตัวให้ทันที (ไม่ต้องจ่ายเพิ่ม) ·
            <a href="/pages/account-subscription.html" target="_blank" style="color:#0ea5e9;text-decoration:underline">ดูสิทธิ์ของฉัน</a>
          </div>`
        : '';
      formContent = `
        ${licenseHint}
        <div style="padding:10px 14px;background:#eff6ff;border-radius:8px;border:1px solid #bfdbfe;margin-bottom:16px;font-size:13px;color:#1e40af">
          พิมพ์ชื่อบริษัทเพื่อค้นหาจาก DBD หรือใส่เลขผู้เสียภาษี 13 หลักเพื่อดึงข้อมูลอัตโนมัติ
        </div>
        <div class="form-group">
          <label class="form-label">ชื่อบริษัท / กิจการ <span style="color:red">*</span></label>
          <input type="text" id="setupCompanyName" class="form-input" placeholder="พิมพ์ชื่อบริษัทเพื่อค้นหา เช่น มังกร" autocomplete="off">
        </div>
        <div class="form-group">
          <label class="form-label">ชื่อบริษัท (EN)</label>
          <input type="text" id="setupCompanyNameEn" class="form-input" placeholder="Company Name in English">
        </div>
        <div class="form-row">
          <div class="form-group">
            <label class="form-label">ประเภทธุรกิจ <span style="color:red">*</span></label>
            <select id="setupBizType" class="form-select">
              <option value="JuristicPerson">บริษัทจำกัด</option>
              <option value="Partnership">ห้างหุ้นส่วน</option>
              <option value="Individual">บุคคลธรรมดา</option>
              <option value="PublicCompany">บริษัทมหาชน</option>
              <option value="Foundation">มูลนิธิ</option>
              <option value="Association">สมาคม</option>
              <option value="Other">อื่นๆ</option>
            </select>
          </div>
          <div class="form-group">
            <label class="form-label">เลขทะเบียนนิติบุคคล (DBD)</label>
            <input type="text" id="setupJuristicId" class="form-input" placeholder="เลขทะเบียนนิติบุคคล" maxlength="13">
          </div>
        </div>
        <div class="form-row">
          <div class="form-group" style="flex:2">
            <label class="form-label">เลขผู้เสียภาษี 13 หลัก <span style="color:red">*</span></label>
            <div style="display:flex;gap:8px">
              <input type="text" id="setupTaxId" class="form-input" placeholder="เลขประจำตัวผู้เสียภาษี" maxlength="13" style="flex:1">
              <button class="btn btn-secondary" id="setupDbdBtn" style="white-space:nowrap">ดึงข้อมูล</button>
            </div>
          </div>
          <div class="form-group">
            <label class="form-label">รหัสสาขา</label>
            <input type="text" id="setupBranch" class="form-input" placeholder="00000 (สำนักงานใหญ่)" value="00000">
          </div>
        </div>
        <div style="display:flex;justify-content:flex-end;margin-top:16px">
          <button class="btn btn-primary btn-lg" onclick="Layout.nextSetupStep()">ถัดไป &rarr;</button>
        </div>`;
    } else if (this.setupStep === 2) {
      formContent = `
        <div class="form-group">
          <label class="form-label">ที่อยู่ (ตามใบทะเบียน)</label>
          <textarea class="form-textarea" id="setupAddress" rows="2" placeholder="เลขที่ ซอย ถนน"></textarea>
        </div>
        <div class="form-row">
          <div class="form-group"><label class="form-label">แขวง/ตำบล</label><input type="text" id="setupSubDistrict" class="form-input"></div>
          <div class="form-group"><label class="form-label">เขต/อำเภอ</label><input type="text" id="setupDistrict" class="form-input"></div>
        </div>
        <div class="form-row">
          <div class="form-group"><label class="form-label">จังหวัด</label><input type="text" id="setupProvince" class="form-input"></div>
          <div class="form-group"><label class="form-label">รหัสไปรษณีย์</label><input type="text" id="setupPostalCode" class="form-input" maxlength="5"></div>
        </div>
        <div class="form-row">
          <div class="form-group"><label class="form-label">โทรศัพท์</label><input type="text" id="setupPhone" class="form-input" placeholder="02-xxx-xxxx"></div>
          <div class="form-group"><label class="form-label">อีเมลบริษัท</label><input type="email" id="setupEmail" class="form-input" placeholder="info@company.co.th"></div>
        </div>
        <div style="display:flex;justify-content:space-between;margin-top:16px">
          <button class="btn btn-secondary btn-lg" onclick="Layout.prevSetupStep()">&larr; ย้อนกลับ</button>
          <button class="btn btn-primary btn-lg" onclick="Layout.nextSetupStep()">ถัดไป &rarr;</button>
        </div>`;
    } else if (this.setupStep === 3) {
      formContent = `
        <div style="padding:16px;background:#f0fdf4;border-radius:8px;border:1px solid #bbf7d0;margin-bottom:20px">
          <h4 style="margin:0 0 8px;font-size:14px;color:#166534">การตั้งค่าภาษีและบัญชีตามกฎหมายไทย</h4>
          <p style="margin:0;font-size:13px;color:#15803d">ข้อมูลนี้จำเป็นสำหรับการออกเอกสารภาษีและรายงานที่ถูกต้องตามกฎหมาย</p>
        </div>
        <div class="form-row">
          <div class="form-group" style="flex:1">
            <label class="form-checkbox" style="padding:12px;background:#f8fafc;border-radius:8px;border:1px solid #e2e8f0">
              <input type="checkbox" id="setupVatRegistered">
              <span>จดทะเบียนภาษีมูลค่าเพิ่ม (VAT)</span>
            </label>
          </div>
          <div class="form-group" style="flex:1">
            <label class="form-label">อัตรา VAT (%)</label>
            <input type="number" id="setupVatRate" class="form-input" value="7" step="0.01">
          </div>
        </div>
        <div class="form-row">
          <div class="form-group" style="flex:1">
            <label class="form-checkbox" style="padding:12px;background:#f8fafc;border-radius:8px;border:1px solid #e2e8f0">
              <input type="checkbox" id="setupWhtRegistered" checked>
              <span>หักภาษี ณ ที่จ่าย (ภ.ง.ด.3/53)</span>
            </label>
          </div>
          <div class="form-group" style="flex:1">
            <label class="form-checkbox" style="padding:12px;background:#f8fafc;border-radius:8px;border:1px solid #e2e8f0">
              <input type="checkbox" id="setupSocialSecurity">
              <span>จดทะเบียนประกันสังคม</span>
            </label>
          </div>
        </div>
        <div class="form-group">
          <label class="form-label">เดือนเริ่มต้นรอบบัญชี (พ.ร.บ.การบัญชี)</label>
          <select id="setupFiscalMonth" class="form-select">
            <option value="1">มกราคม (ม.ค. - ธ.ค.)</option><option value="2">กุมภาพันธ์</option><option value="3">มีนาคม</option>
            <option value="4">เมษายน (เม.ย. - มี.ค.)</option><option value="5">พฤษภาคม</option><option value="6">มิถุนายน</option>
            <option value="7">กรกฎาคม (ก.ค. - มิ.ย.)</option><option value="8">สิงหาคม</option><option value="9">กันยายน</option>
            <option value="10">ตุลาคม (ต.ค. - ก.ย.)</option><option value="11">พฤศจิกายน</option><option value="12">ธันวาคม</option>
          </select>
          <p style="font-size:12px;color:var(--gray-500);margin-top:4px">* นิติบุคคลส่วนใหญ่ใช้รอบ ม.ค. - ธ.ค. ตาม พ.ร.บ.การบัญชี พ.ศ. 2543</p>
        </div>
        <div style="display:flex;justify-content:space-between;margin-top:20px">
          <button class="btn btn-secondary btn-lg" onclick="Layout.prevSetupStep()">&larr; ย้อนกลับ</button>
          <button class="btn btn-primary btn-lg" onclick="Layout.createFirstCompany()" id="setupBtn">สร้างบริษัทและเริ่มต้นใช้งาน</button>
        </div>`;
    }

    pageContent.innerHTML = `
      <div style="max-width:600px;margin:40px auto;text-align:center">
        <div style="font-size:48px;margin-bottom:12px">🏢</div>
        <h2 style="margin-bottom:4px">ยินดีต้อนรับสู่ Next Acc!</h2>
        <p style="color:var(--gray-500);margin-bottom:24px">กรอกข้อมูลกิจการเพื่อเริ่มต้นใช้งานระบบบัญชี</p>
        ${stepBar}
        <div class="card" style="text-align:left;padding:24px">
          ${formContent}
        </div>
      </div>`;

    // Attach DBD lookup after DOM update
    if (this.setupStep === 1) {
      setTimeout(() => {
        if (typeof DbdLookup === 'undefined') return;
        // Autocomplete on company name
        DbdLookup.attachNameSearch(document.getElementById('setupCompanyName'), (result) => {
          this._setupData.name = result.nameTh;
          this._setupData.nameEn = result.nameEn || '';
          this._setupData.taxId = result.juristicId || '';
          this._setupData.juristicId = result.juristicId || '';
          document.getElementById('setupCompanyName').value = result.nameTh;
          document.getElementById('setupCompanyNameEn').value = result.nameEn || '';
          document.getElementById('setupTaxId').value = result.juristicId || '';
          document.getElementById('setupJuristicId').value = result.juristicId || '';
          if (result.address) { this._setupData.address = result.address; }
          this.toast('เลือก ' + result.nameTh + ' แล้ว');
        });
        // Tax ID lookup button
        DbdLookup.attachTaxIdLookup(
          document.getElementById('setupTaxId'),
          (result) => {
            this._setupData.name = result.nameTh;
            this._setupData.nameEn = result.nameEn || '';
            this._setupData.juristicId = result.juristicId || '';
            document.getElementById('setupCompanyName').value = result.nameTh;
            document.getElementById('setupCompanyNameEn').value = result.nameEn || '';
            document.getElementById('setupJuristicId').value = result.juristicId || '';
            if (result.address) { this._setupData.address = result.address; }
          },
          document.getElementById('setupDbdBtn')
        );
        this.restoreStepData();
      }, 0);
    } else {
      setTimeout(() => this.restoreStepData(), 0);
    }
  },

  // Store partial data between steps
  _setupData: {},

  nextSetupStep() {
    this.saveCurrentStepData();
    if (this.setupStep === 1) {
      if (!this._setupData.name) { this.toast(this._t('layout.enterCompanyName', 'กรุณากรอกชื่อบริษัท'), 'error'); return; }
      if (!this._setupData.taxId) { this.toast(this._t('layout.enterTaxId', 'กรุณากรอกเลขผู้เสียภาษี'), 'error'); return; }
    }
    this.setupStep++;
    this.renderSetupStep();
  },

  prevSetupStep() {
    this.saveCurrentStepData();
    this.setupStep--;
    this.renderSetupStep();
    this.restoreStepData();
  },

  saveCurrentStepData() {
    const d = this._setupData;
    if (this.setupStep === 1) {
      d.name = document.getElementById('setupCompanyName')?.value?.trim() || '';
      d.nameEn = document.getElementById('setupCompanyNameEn')?.value?.trim() || '';
      d.businessType = document.getElementById('setupBizType')?.value || 'JuristicPerson';
      d.juristicId = document.getElementById('setupJuristicId')?.value?.trim() || '';
      d.taxId = document.getElementById('setupTaxId')?.value?.trim() || '';
      d.branchCode = document.getElementById('setupBranch')?.value?.trim() || '00000';
    } else if (this.setupStep === 2) {
      d.address = document.getElementById('setupAddress')?.value?.trim() || '';
      d.subDistrict = document.getElementById('setupSubDistrict')?.value?.trim() || '';
      d.district = document.getElementById('setupDistrict')?.value?.trim() || '';
      d.province = document.getElementById('setupProvince')?.value?.trim() || '';
      d.postalCode = document.getElementById('setupPostalCode')?.value?.trim() || '';
      d.phone = document.getElementById('setupPhone')?.value?.trim() || '';
      d.email = document.getElementById('setupEmail')?.value?.trim() || '';
    } else if (this.setupStep === 3) {
      d.isVatRegistered = document.getElementById('setupVatRegistered')?.checked || false;
      d.vatRate = parseFloat(document.getElementById('setupVatRate')?.value) || 7;
      d.isWhtRegistered = document.getElementById('setupWhtRegistered')?.checked || false;
      d.isSocialSecurityRegistered = document.getElementById('setupSocialSecurity')?.checked || false;
      d.fiscalYearStartMonth = parseInt(document.getElementById('setupFiscalMonth')?.value) || 1;
    }
  },

  restoreStepData() {
    const d = this._setupData;
    setTimeout(() => {
      if (this.setupStep === 1) {
        if (d.name) document.getElementById('setupCompanyName').value = d.name;
        if (d.nameEn) document.getElementById('setupCompanyNameEn').value = d.nameEn;
        if (d.businessType) document.getElementById('setupBizType').value = d.businessType;
        if (d.juristicId) document.getElementById('setupJuristicId').value = d.juristicId;
        if (d.taxId) document.getElementById('setupTaxId').value = d.taxId;
        if (d.branchCode) document.getElementById('setupBranch').value = d.branchCode;
      } else if (this.setupStep === 2) {
        if (d.address) document.getElementById('setupAddress').value = d.address;
        if (d.subDistrict) document.getElementById('setupSubDistrict').value = d.subDistrict;
        if (d.district) document.getElementById('setupDistrict').value = d.district;
        if (d.province) document.getElementById('setupProvince').value = d.province;
        if (d.postalCode) document.getElementById('setupPostalCode').value = d.postalCode;
        if (d.phone) document.getElementById('setupPhone').value = d.phone;
        if (d.email) document.getElementById('setupEmail').value = d.email;
      }
    }, 0);
  },

  async createFirstCompany() {
    this.saveCurrentStepData();
    const d = this._setupData;
    if (!d.name) { this.toast(this._t('layout.enterCompanyName', 'กรุณากรอกชื่อบริษัท'), 'error'); return; }
    const btn = document.getElementById('setupBtn');
    btn.disabled = true; btn.textContent = 'กำลังสร้าง...';
    try {
      const res = await API.createCompany({
        name: d.name,
        nameEn: d.nameEn || null,
        taxId: d.taxId || '-',
        branchCode: d.branchCode || '00000',
        businessType: d.businessType || 'JuristicPerson',
        juristicId: d.juristicId || null,
        isVatRegistered: d.isVatRegistered || false,
        vatRate: d.vatRate || 7,
        isWhtRegistered: d.isWhtRegistered !== false,
        isSocialSecurityRegistered: d.isSocialSecurityRegistered || false,
        address: d.address || null,
        subDistrict: d.subDistrict || null,
        district: d.district || null,
        province: d.province || null,
        postalCode: d.postalCode || null,
        phone: d.phone || null,
        email: d.email || null,
        fiscalYearStartMonth: d.fiscalYearStartMonth || 1,
      });
      const company = res.data;
      localStorage.setItem('currentCompany', JSON.stringify(company));
      this._setupData = {};
      this.toast(this._t('layout.companyCreated', 'สร้างบริษัทสำเร็จ!'));
      // Redirect to settings page for additional setup
      setTimeout(() => window.location.href = '/pages/settings.html?setup=1', 500);
    } catch (e) {
      this.toast(e.message, 'error');
      btn.disabled = false; btn.textContent = 'สร้างบริษัทและเริ่มต้นใช้งาน';
    }
  },

  bindEvents() {
    document.addEventListener('change', e => {
      if (e.target.id === 'companySelect') {
        const id = e.target.value;
        if (id === '__create_new__') {
          // Reset to the current selection visually, then launch the setup
          // overlay. Reusing the existing first-time wizard means the create
          // flow stays consistent — same field validation, same chart-of-
          // accounts seeding, same trial subscription auto-attach.
          if (this.currentCompany?.id) e.target.value = this.currentCompany.id;
          this.showCompanySetupPrompt({ forNewCompany: true });
          return;
        }
        if (id) {
          // Keep full company object if available, fallback to { id }
          const full = this.companies?.find(c => c.id === id) || { id };
          this.currentCompany = full;
          localStorage.setItem('currentCompany', JSON.stringify(full));
          window.location.reload();
        }
      }
    });
    document.addEventListener('click', e => {
      if (!e.target.closest('.dropdown')) {
        document.querySelectorAll('.dropdown-menu').forEach(d => d.classList.remove('show'));
      }
    });
  },

  getCompanyId() {
    return this.currentCompany?.id || '';
  },

  // Ensures currentCompany.myRole is populated. Falls back to fetching from
  // /api/company/{id} when stale localStorage lacks the field. Returns the role string.
  async ensureMyRole() {
    if (this.currentCompany?.myRole) return this.currentCompany.myRole;
    const id = this.getCompanyId();
    if (!id) return null;
    try {
      const res = await API.get(`/api/company/${id}`);
      const data = res?.data;
      if (data?.myRole) {
        this.currentCompany = { ...this.currentCompany, ...data };
        localStorage.setItem('currentCompany', JSON.stringify(this.currentCompany));
        return data.myRole;
      }
    } catch {}
    return null;
  },

  api() {
    const cid = this.getCompanyId();
    if (!cid) { return null; }
    return API.c(cid);
  },

  async loadNotificationCount() {
    try {
      const res = await API.get('/api/notification/count');
      const count = res.data?.unread || 0;
      const dot = document.getElementById('notifDot');
      if (dot) dot.classList.toggle('hidden', count === 0);
    } catch (e) { /* ignore */ }
  },

  toggleSidebar() {
    const sb = document.getElementById('sidebar');
    const ov = document.getElementById('sidebarOverlay');
    if (sb) sb.classList.toggle('open');
    if (ov) ov.classList.toggle('open');
  },

  async toggleNotifications() {
    const panel = document.getElementById('notifPanel');
    panel.classList.toggle('active');
    if (panel.classList.contains('active')) {
      const list = document.getElementById('notifList');
      list.innerHTML = '<div style="text-align:center;padding:20px"><div class="spinner" style="margin:0 auto"></div></div>';
      try {
        const res = await API.get('/api/notification?page=1&pageSize=20');
        const items = res.data?.items || res.data || [];
        if (items.length === 0) {
          list.innerHTML = '<p class="text-gray-500 text-sm text-center" style="padding:20px">ไม่มีการแจ้งเตือน</p>';
        } else {
          list.innerHTML = items.map(n => `
            <div style="padding:12px 0;border-bottom:1px solid var(--gray-100);${n.isRead ? '' : 'background:#F5F3FF;margin:0 -24px;padding:12px 24px'}">
              <div class="text-sm font-medium">${Layout.esc(n.title)}</div>
              <div class="text-xs text-gray-500" style="margin-top:2px">${Layout.esc(n.message)}</div>
              <div class="text-xs text-gray-400" style="margin-top:4px">${new Date(n.createdAt).toLocaleString('th-TH')}</div>
            </div>
          `).join('');
        }
      } catch (e) {
        list.innerHTML = '<p class="text-danger text-sm text-center" style="padding:20px">โหลดข้อมูลไม่สำเร็จ</p>';
      }
    }
  },

  closeNotifications() {
    document.getElementById('notifPanel').classList.remove('active');
  },

  applySiteBranding() {
    fetch('/api/site/landing').then(r => r.json()).then(json => {
      const d = json.data;
      if (!d) return;
      this._siteName = d.siteName || 'Next Acc';
      if (d.siteLogoUrl) {
        const logo = document.querySelector('.sidebar-logo');
        if (logo) logo.innerHTML = `<img src="${d.siteLogoUrl}" alt="${this._siteName}" style="height:28px;object-fit:contain">`;
      } else if (d.siteName) {
        const logo = document.querySelector('.sidebar-logo span');
        if (logo) logo.textContent = d.siteName;
      }
      if (d.primaryColor) document.documentElement.style.setProperty('--primary', d.primaryColor);
      if (d.faviconUrl) {
        let link = document.querySelector("link[rel~='icon']");
        if (!link) { link = document.createElement('link'); link.rel = 'icon'; document.head.appendChild(link); }
        link.href = d.faviconUrl;
      }
    }).catch(() => {});
  },

  logout() {
    localStorage.removeItem('token');
    localStorage.removeItem('user');
    localStorage.removeItem('currentCompany');
    window.location.href = '/login.html';
  },

  setTitle(title) {
    document.getElementById('headerTitle').textContent = title;
    document.title = title + ' - ' + (this._siteName || 'Next Acc');
  },

  // Toast notifications (with deduplication - max 3 visible, no duplicate messages)
  _activeToasts: new Map(),
  // Help tooltip: any element with `data-help="…"` gets a small ⓘ icon
  // injected next to it; clicking the icon shows a bubble with the help
  // text. Mounted once at init; auto-rescanned when pages dynamically
  // re-render content (call Layout._mountHelpIcons() to rescan).
  _mountHelpIcons() {
    document.querySelectorAll('[data-help]:not([data-help-mounted])').forEach(el => {
      el.setAttribute('data-help-mounted', '1');
      const help = el.getAttribute('data-help');
      if (!help) return;
      const ic = document.createElement('button');
      ic.type = 'button';
      ic.className = 'help-icon';
      ic.setAttribute('aria-label', 'ดูคำอธิบาย');
      ic.textContent = 'ⓘ';
      ic.onclick = (e) => { e.preventDefault(); e.stopPropagation(); this._showHelpBubble(ic, help); };
      el.appendChild(ic);
    });
  },
  _showHelpBubble(anchor, text) {
    const old = document.getElementById('helpBubble');
    if (old) old.remove();
    const b = document.createElement('div');
    b.id = 'helpBubble';
    b.innerHTML = `<div class="hb-text">${this.esc(text)}</div><button class="hb-close" onclick="this.parentElement.remove()">×</button>`;
    document.body.appendChild(b);
    const r = anchor.getBoundingClientRect();
    // Position below the icon; flip up if it would clip the viewport bottom.
    const w = b.offsetWidth, h = b.offsetHeight;
    let top = r.bottom + 8, left = Math.max(8, Math.min(window.innerWidth - w - 8, r.left));
    if (top + h > window.innerHeight - 8) top = Math.max(8, r.top - h - 8);
    b.style.top = top + 'px'; b.style.left = left + 'px';
    // Auto-close on outside click
    setTimeout(() => {
      const off = (ev) => { if (!b.contains(ev.target)) { b.remove(); document.removeEventListener('click', off); } };
      document.addEventListener('click', off);
    }, 0);
  },

  // Guided tour runner. Any page can opt-in by marking elements with
  //   <element data-tour-step="1" data-tour-text="..."></element>
  // and (optionally) data-tour-position="top|bottom|left|right".
  // Call Layout.startTour() from page code or auto-trigger on first visit:
  //   if (!localStorage.getItem('tour:'+pageName)) Layout.startTour(pageName);
  startTour(pageKey) {
    const steps = Array.from(document.querySelectorAll('[data-tour-step]'))
      .map(el => ({ el, n: parseInt(el.getAttribute('data-tour-step') || '0', 10),
                    text: el.getAttribute('data-tour-text') || '',
                    pos: el.getAttribute('data-tour-position') || 'bottom' }))
      .filter(s => s.n > 0 && s.text)
      .sort((a, b) => a.n - b.n);
    if (steps.length === 0) return;
    let i = 0;
    const overlay = document.createElement('div');
    overlay.id = 'tourOverlay';
    document.body.appendChild(overlay);
    const renderStep = () => {
      const s = steps[i];
      const r = s.el.getBoundingClientRect();
      // Highlight ring
      overlay.innerHTML = `
        <div class="tour-mask" style="top:${r.top - 6}px;left:${r.left - 6}px;width:${r.width + 12}px;height:${r.height + 12}px;"></div>
        <div class="tour-pop tour-pos-${s.pos}" style="top:${r.bottom + 12}px;left:${Math.max(8, r.left)}px;">
          <div class="tour-text">${this.esc(s.text)}</div>
          <div class="tour-actions">
            <span class="tour-progress">${i + 1} / ${steps.length}</span>
            <div style="flex:1;"></div>
            <button class="tour-skip" type="button">ข้าม</button>
            ${i < steps.length - 1
              ? '<button class="tour-next" type="button">ถัดไป →</button>'
              : '<button class="tour-done" type="button">เสร็จสิ้น ✓</button>'}
          </div>
        </div>`;
      overlay.querySelector('.tour-skip').onclick = () => { overlay.remove(); if (pageKey) localStorage.setItem('tour:' + pageKey, '1'); };
      const next = overlay.querySelector('.tour-next');
      if (next) next.onclick = () => { i++; s.el.scrollIntoView({ block: 'center', behavior: 'smooth' }); setTimeout(renderStep, 250); };
      const done = overlay.querySelector('.tour-done');
      if (done) done.onclick = () => { overlay.remove(); if (pageKey) localStorage.setItem('tour:' + pageKey, '1'); };
    };
    steps[0].el.scrollIntoView({ block: 'center', behavior: 'smooth' });
    setTimeout(renderStep, 300);
  },

  // One-liner page opt-in: auto-runs the tour on first visit and injects a
  // floating "📘 สอนใช้หน้านี้" replay button. Pages just call
  //   Layout.enableTour('document-scan')
  // after DOM ready and add data-tour-step / data-tour-text on key elements.
  enableTour(pageKey, opts) {
    if (!pageKey) return;
    const o = opts || {};
    const hasSteps = document.querySelector('[data-tour-step]');
    if (!hasSteps) return;
    // Replay button
    if (!document.getElementById('tourReplayBtn')) {
      const btn = document.createElement('button');
      btn.id = 'tourReplayBtn';
      btn.type = 'button';
      btn.className = 'tour-replay-btn';
      btn.title = 'สอนใช้หน้านี้อีกครั้ง';
      btn.innerHTML = '📘 สอนใช้หน้านี้';
      btn.onclick = () => this.startTour(pageKey);
      document.body.appendChild(btn);
    }
    // Auto-trigger on first visit
    const key = 'tour:' + pageKey;
    if (!localStorage.getItem(key)) {
      const delay = o.delay || 600;
      setTimeout(() => this.startTour(pageKey), delay);
    }
  },

  _toggleFab() {
    const menu = document.getElementById('fabMenu');
    if (!menu) return;
    menu.style.display = menu.style.display === 'none' ? 'flex' : 'none';
  },

  // Catch any Promise rejection that bubbles up without being handled —
  // typically an API call where the caller forgot to .catch. Showing a
  // toast beats the previous "silent failure or raw alert()" experience.
  _installGlobalErrorHandler() {
    if (this._errHandlerInstalled) return;
    window.addEventListener('unhandledrejection', (e) => {
      const r = e.reason;
      if (!r) return;
      // Ignore aborted fetches / cancelled requests
      if (r.name === 'AbortError') return;
      const raw = (r.message || String(r));
      // Suppress the generic backend 500 toast when we don't have a useful
      // hint — repeatedly slamming the user with "เกิดข้อผิดพลาดภายในระบบ"
      // on every page load (when one of the many background init calls
      // fails) is worse than silent. We still log to console + ErrorLogs
      // server-side, and a click on the silent-error icon in the header
      // surfaces the last error for debugging.
      const isGenericBackend500 = /เกิดข้อผิดพลาดภายในระบบ/.test(raw) && (r.status === 500 || /HTTP 500/.test(raw));
      if (isGenericBackend500) {
        this._lastSilentError = { at: new Date().toISOString(), reason: r, message: raw };
        console.error('[unhandled · 500 suppressed]', r);
        try { this._showSilentErrorBadge(); } catch {}
        return;
      }
      const friendly = /non-JSON|HTTP 5\d\d|<!doctype/i.test(raw)
        ? 'เซิร์ฟเวอร์มีปัญหาชั่วคราว กรุณาลองใหม่อีกครั้ง หรือรีเฟรชหน้านี้'
        : raw.length > 200 ? raw.slice(0, 200) + '…' : raw;
      if (this.toast) this.toast(friendly, 'error');
      console.error('[unhandled]', r);
    });
    window.addEventListener('error', (e) => {
      // Only catch script errors that escape — DOM/resource errors stay silent.
      if (!e.error) return;
      if (this.toast) this.toast('เกิดข้อผิดพลาดในหน้า: ' + (e.error.message || e.message || ''), 'error');
      console.error('[window.error]', e.error);
    });
    this._errHandlerInstalled = true;
  },

  _showSilentErrorBadge() {
    if (document.getElementById('silentErrorBadge')) return;
    const badge = document.createElement('div');
    badge.id = 'silentErrorBadge';
    badge.style.cssText = 'position:fixed;bottom:16px;right:16px;background:#fbbf24;color:#78350f;padding:8px 14px;border-radius:20px;font-size:12px;font-weight:600;box-shadow:0 4px 12px rgba(0,0,0,.15);cursor:pointer;z-index:9998;display:flex;align-items:center;gap:6px';
    badge.innerHTML = '⚠️ มี API บางตัวล้มเหลว (คลิกเพื่อดูรายละเอียด)';
    badge.onclick = () => {
      const e = this._lastSilentError;
      if (!e) return;
      const txt = `เวลา: ${e.at}\nข้อความ: ${e.message}\n\n${e.reason?.body ? 'Body:\n' + JSON.stringify(e.reason.body, null, 2) : ''}`;
      alert(txt);
    };
    document.body.appendChild(badge);
  },

  toast(msg, type = 'success') {
    const container = document.getElementById('toastContainer');
    if (!container) return;

    // Deduplicate: skip if same message is already showing
    const key = `${type}:${msg}`;
    if (this._activeToasts.has(key)) return;

    // Limit max visible toasts to 3
    const existing = container.querySelectorAll('.toast');
    if (existing.length >= 3) {
      existing[0].remove();
      for (const [k, el] of this._activeToasts) {
        if (!document.contains(el)) this._activeToasts.delete(k);
      }
    }

    const t = document.createElement('div');
    t.className = `toast toast-${type}`;
    t.innerHTML = `${type === 'success' ? '✅' : type === 'error' ? '❌' : 'ℹ️'} ${this.esc(msg)}`;
    container.appendChild(t);
    this._activeToasts.set(key, t);
    setTimeout(() => {
      t.style.opacity = '0';
      setTimeout(() => { t.remove(); this._activeToasts.delete(key); }, 300);
    }, 3500);
  },

  // Modal helpers
  openModal(id) {
    document.getElementById(id).classList.add('active');
    this._ensureModalKeyboardWired();
  },
  closeModal(id) { document.getElementById(id).classList.remove('active'); },

  /** Wire keyboard shortcuts สำหรับทุก modal ในระบบ (one-time global listener):
   *  • Escape → ปิด modal บนสุดที่กำลังเปิด
   *  • Enter ในช่อง input/select (ไม่ใช่ textarea, ไม่กด Shift+Enter) → trigger
   *    ปุ่ม primary (.btn-primary คนสุดท้ายใน modal-footer ของ modal นั้น)
   *  ทำให้ทุก form ในระบบใช้ keyboard ปกติได้ทันที ไม่ต้องเดินสาย onkeydown
   *  ทีละ input — เปิด throughput ของผู้ใช้บน desktop ดีขึ้นมาก. */
  _ensureModalKeyboardWired() {
    if (this._modalKbWired) return;
    this._modalKbWired = true;
    document.addEventListener('keydown', e => {
      const open = [...document.querySelectorAll('.modal-overlay.active')];
      if (!open.length) return;
      const top = open[open.length - 1];   // topmost = nested-friendly
      if (e.key === 'Escape') {
        e.preventDefault();
        top.classList.remove('active');
        return;
      }
      if (e.key === 'Enter' && !e.shiftKey && !e.isComposing) {
        const t = e.target;
        if (!t || !top.contains(t)) return;
        const tag = (t.tagName || '').toUpperCase();
        // textarea / contenteditable / button = ปล่อยทำงานตามปกติ
        if (tag === 'TEXTAREA' || tag === 'BUTTON' || t.isContentEditable) return;
        // ปุ่ม primary ตัวสุดท้ายใน footer (CTA หลัก) — กดให้
        const cta = top.querySelector('.modal-footer .btn-primary');
        if (cta && !cta.disabled) { e.preventDefault(); cta.click(); }
      }
    });
  },

  // ===== Entity Timeline Modal (Phase N) =====
  // Reusable audit-history modal that any detail page can pop open via
  //   Layout.showEntityTimeline('Document', docId, 'INV-2026-0001')
  // Loads /accountant/timeline/{type}/{id} and renders a vertical
  // timeline with action / user / timestamp / diff-of-changed-fields.
  // Keeps every detail page free of audit-log boilerplate.
  async showEntityTimeline(entityType, entityId, label) {
    const cid = this.getCompanyId();
    if (!cid) { this.toast('กรุณาเลือกบริษัทก่อน', 'warning'); return; }
    let wrap = document.getElementById('entityTimelineModal');
    if (!wrap) {
      wrap = document.createElement('div');
      wrap.id = 'entityTimelineModal';
      wrap.className = 'modal-overlay';
      wrap.innerHTML = `
        <div class="modal" style="max-width:720px">
          <div class="modal-header">
            <h3 class="modal-title" id="etTitle">📜 ประวัติการเปลี่ยนแปลง</h3>
            <button class="modal-close" onclick="Layout.closeModal('entityTimelineModal')">&times;</button>
          </div>
          <div class="modal-body" id="etBody" style="max-height:60vh;overflow-y:auto"></div>
          <div class="modal-footer">
            <button class="btn btn-secondary" onclick="Layout.closeModal('entityTimelineModal')">ปิด</button>
          </div>
        </div>`;
      document.body.appendChild(wrap);
    }
    wrap.querySelector('#etTitle').textContent = '📜 ประวัติการเปลี่ยนแปลง' + (label ? ' — ' + label : '');
    const body = wrap.querySelector('#etBody');
    body.innerHTML = '<div style="text-align:center;padding:24px;color:#6b7280">กำลังโหลด...</div>';
    wrap.classList.add('active');
    try {
      const res = await API.get(`/api/companies/${cid}/accountant/timeline/${encodeURIComponent(entityType)}/${encodeURIComponent(entityId)}`);
      const items = res?.data || [];
      if (!items.length) {
        body.innerHTML = '<div style="text-align:center;padding:32px;color:#6b7280">ไม่พบประวัติการเปลี่ยนแปลง</div>';
        return;
      }
      const actionMap = { Create: ['สร้าง','#10b981','✨'], Update: ['แก้ไข','#3b82f6','✏️'],
        Delete: ['ลบ','#dc2626','🗑️'], View: ['ดู','#6b7280','👁️'], Login: ['เข้าระบบ','#6366f1','🔑'],
        Logout: ['ออกจากระบบ','#6b7280','🚪'], Approve: ['อนุมัติ','#10b981','✅'],
        Reject: ['ปฏิเสธ','#dc2626','⛔'], Post: ['ลงบัญชี','#10b981','📒'],
        Void: ['ยกเลิก','#dc2626','🚫'], Reverse: ['กลับรายการ','#f59e0b','↩️'] };
      body.innerHTML = items.map(it => {
        const ai = actionMap[it.action] || [it.action,'#6b7280','•'];
        const ts = new Date(it.timestamp);
        const tsStr = ts.toLocaleString('th-TH', { dateStyle:'medium', timeStyle:'short' });
        const diff = this._renderAuditDiff(it.oldValues, it.newValues, it.action);
        return `<div style="border-left:3px solid ${ai[1]};padding:10px 14px;margin-bottom:10px;background:#f9fafb;border-radius:0 6px 6px 0">
          <div style="display:flex;justify-content:space-between;gap:10px;align-items:flex-start;flex-wrap:wrap">
            <div style="font-weight:600;color:${ai[1]}">${ai[2]} ${ai[0]}</div>
            <div style="color:#6b7280;font-size:12px">${tsStr}</div>
          </div>
          <div style="color:#374151;font-size:13px;margin-top:4px">${this.esc(it.userEmail || '(ระบบ)')}</div>
          ${diff}
        </div>`;
      }).join('');
    } catch (e) {
      body.innerHTML = `<div style="color:#dc2626;padding:16px">โหลดประวัติไม่สำเร็จ: ${this.esc(e?.message || String(e))}</div>`;
    }
  },

  // Render a compact diff of changed fields between OldValues / NewValues
  // JSON blobs. Hides housekeeping fields (timestamps, audit cols) and
  // truncates long values so the timeline stays scannable.
  _renderAuditDiff(oldJson, newJson, action) {
    const skip = new Set(['UpdatedAt','CreatedAt','UpdatedBy','CreatedBy','RowVersion','ConcurrencyStamp']);
    const trunc = v => { const s = v == null ? '∅' : String(v); return s.length > 80 ? s.slice(0,80) + '…' : s; };
    let oldV = {}, newV = {};
    try { if (oldJson) oldV = JSON.parse(oldJson); } catch {}
    try { if (newJson) newV = JSON.parse(newJson); } catch {}
    if (action === 'Create' && newV && typeof newV === 'object') {
      const keys = Object.keys(newV).filter(k => !skip.has(k) && newV[k] != null && newV[k] !== '').slice(0, 6);
      if (!keys.length) return '';
      return `<div style="margin-top:6px;font-size:12px;color:#4b5563">
        ${keys.map(k => `<div><b>${this.esc(k)}:</b> ${this.esc(trunc(newV[k]))}</div>`).join('')}
      </div>`;
    }
    if (action === 'Update') {
      const keys = new Set([...Object.keys(oldV || {}), ...Object.keys(newV || {})]);
      const rows = [];
      for (const k of keys) {
        if (skip.has(k)) continue;
        const o = oldV?.[k], n = newV?.[k];
        if (JSON.stringify(o) === JSON.stringify(n)) continue;
        rows.push(`<div><b>${this.esc(k)}:</b> <span style="color:#dc2626;text-decoration:line-through">${this.esc(trunc(o))}</span> → <span style="color:#059669">${this.esc(trunc(n))}</span></div>`);
      }
      if (!rows.length) return '';
      return `<div style="margin-top:6px;font-size:12px;color:#4b5563">${rows.slice(0,8).join('')}${rows.length>8?`<div style="color:#9ca3af">…และอีก ${rows.length-8} รายการ</div>`:''}</div>`;
    }
    return '';
  },

  // Danger confirm: requires solving math problem OR typing "confirm"
  // Usage: await Layout.confirmDanger({ title, message, mode: 'math'|'type'|'doubleCheck', confirmText }) → boolean
  // doubleCheck mode: first solves math, then types confirmText
  confirmDanger(opts = {}) {
    return new Promise(resolve => {
      const title = opts.title || 'ยืนยันการดำเนินการ';
      const message = opts.message || 'การดำเนินการนี้ไม่สามารถย้อนกลับได้';
      const mode = opts.mode || 'math';
      const confirmText = opts.confirmText || 'confirm';
      const a = Math.floor(Math.random() * 9) + 2;
      const b = Math.floor(Math.random() * 9) + 2;

      let step = 1;
      let expected, promptHtml;
      if (mode === 'doubleCheck') {
        expected = String(a + b);
        promptHtml = `<b>ขั้นที่ 1/2</b> — กรุณาคำนวณ: <b>${a} + ${b} = ?</b>`;
      } else if (mode === 'math') {
        expected = String(a + b);
        promptHtml = `เพื่อยืนยัน กรุณาคำนวณ: <b>${a} + ${b} = ?</b>`;
      } else {
        expected = confirmText;
        promptHtml = `เพื่อยืนยัน กรุณาพิมพ์ <b>${confirmText}</b>`;
      }

      let wrap = document.getElementById('dangerConfirmModal');
      if (!wrap) {
        wrap = document.createElement('div');
        wrap.id = 'dangerConfirmModal';
        wrap.className = 'modal-overlay';
        wrap.innerHTML = `
          <div class="modal" style="max-width:440px">
            <div class="modal-header" style="border-bottom:2px solid var(--danger,#dc2626)">
              <h3 class="modal-title" id="dcTitle" style="color:var(--danger,#dc2626)">⚠️ ยืนยัน</h3>
              <button class="modal-close" id="dcClose">&times;</button>
            </div>
            <div class="modal-body">
              <div id="dcMessage" style="margin-bottom:12px;line-height:1.5"></div>
              <div id="dcPrompt" style="margin-bottom:8px;font-size:14px"></div>
              <input type="text" class="form-input" id="dcInput" autocomplete="off" placeholder="คำตอบ..." style="font-size:16px">
              <div id="dcError" style="color:var(--danger,#dc2626);font-size:13px;margin-top:6px;min-height:18px"></div>
            </div>
            <div class="modal-footer">
              <button class="btn btn-secondary" id="dcCancel">ยกเลิก</button>
              <button class="btn btn-danger" id="dcOk">ยืนยัน</button>
            </div>
          </div>`;
        document.body.appendChild(wrap);
      }
      wrap.querySelector('#dcTitle').textContent = '⚠️ ' + title;
      wrap.querySelector('#dcMessage').innerHTML = message;
      wrap.querySelector('#dcPrompt').innerHTML = promptHtml;
      const input = wrap.querySelector('#dcInput');
      const errDiv = wrap.querySelector('#dcError');
      input.value = '';
      errDiv.textContent = '';
      wrap.classList.add('active');
      setTimeout(() => input.focus(), 50);

      const cleanup = (val) => {
        wrap.classList.remove('active');
        wrap.querySelector('#dcOk').onclick = null;
        wrap.querySelector('#dcCancel').onclick = null;
        wrap.querySelector('#dcClose').onclick = null;
        input.onkeydown = null;
        resolve(val);
      };
      const tryConfirm = () => {
        if (input.value.trim() === expected) {
          if (mode === 'doubleCheck' && step === 1) {
            step = 2;
            expected = confirmText;
            wrap.querySelector('#dcPrompt').innerHTML = `<b>ขั้นที่ 2/2</b> — กรุณาพิมพ์เลขที่เอกสาร: <b>${Layout.esc(confirmText)}</b>`;
            input.value = '';
            input.placeholder = 'พิมพ์เลขที่เอกสาร...';
            errDiv.textContent = '';
            input.focus();
          } else {
            cleanup(true);
          }
        } else {
          errDiv.textContent = 'คำตอบไม่ถูกต้อง กรุณาลองใหม่';
          input.select();
        }
      };
      wrap.querySelector('#dcOk').onclick = tryConfirm;
      wrap.querySelector('#dcCancel').onclick = () => cleanup(false);
      wrap.querySelector('#dcClose').onclick = () => cleanup(false);
      input.onkeydown = e => {
        if (e.key === 'Enter') { e.preventDefault(); tryConfirm(); }
        if (e.key === 'Escape') cleanup(false);
      };
    });
  },

  // Format helpers
  money(n) {
    if (n == null || n === '') return '0.00';
    const num = Number(n);
    if (isNaN(num)) return '0.00';
    return num.toLocaleString('th-TH', { minimumFractionDigits: 2, maximumFractionDigits: 2 });
  },

  // Parse a server timestamp safely. ASP.NET Core's JSON serializer drops
  // the 'Z' suffix on DateTime values with Kind=Unspecified (typical for
  // timestamptz columns round-tripped through EF Core 8), so the bare
  // "2026-05-25T04:22:20" string would be parsed by JS as local-naive
  // and the moment would shift by the local UTC offset. This helper
  // assumes naive ISO strings are UTC.
  asUtc(s) {
    if (!s) return new Date(NaN);
    if (s instanceof Date) return s;
    const str = String(s);
    return /[zZ]$|[+-]\d{2}:?\d{2}$/.test(str) ? new Date(str) : new Date(str + 'Z');
  },

  date(d) {
    const dt = this.asUtc(d);
    if (isNaN(dt.getTime())) return '-';
    return dt.toLocaleDateString('th-TH', { year: 'numeric', month: 'short', day: 'numeric' });
  },

  /// <summary>Format a server timestamp in the browser's local timezone.
  /// Use this for any UI that shows "เมื่อไหร่" — payment time, sync time,
  /// session open, audit log, etc. — so the user sees their wall-clock time.</summary>
  dateTime(d) {
    const dt = this.asUtc(d);
    if (isNaN(dt.getTime())) return '-';
    return dt.toLocaleString('th-TH');
  },

  time(d) {
    const dt = this.asUtc(d);
    if (isNaN(dt.getTime())) return '-';
    return dt.toLocaleTimeString('th-TH');
  },

  /// <summary>Format วันที่สำหรับ <input type="date" value="yyyy-MM-dd"> โดย
  /// ใช้วัน "ตามปฏิทินไทย" (Asia/Bangkok) ตรงกับที่ Layout.date() แสดง.
  /// เดิมใช้ toISOString().split[0] → UTC date → form edit ของเอกสาร DocumentDate
  /// 02/06 (stored as UTC 02/06 00:00) จะกลายเป็น "2026-06-01" → ผู้ใช้บันทึก
  /// แล้ว shift ผิด 1 วัน. ใช้ asUtc + ดึง Y/M/D ใน BKK locale แทน.</summary>
  dateInput(d) {
    if (!d) return '';
    const dt = this.asUtc(d);
    if (isNaN(dt.getTime())) return '';
    // toLocaleDateString('sv-SE', {timeZone: 'Asia/Bangkok'}) → "yyyy-MM-dd"
    // sv-SE locale ใช้ ISO format ตรง ๆ ไม่ต้อง parse string
    return dt.toLocaleDateString('sv-SE', { timeZone: 'Asia/Bangkok' });
  },

  statusBadge(status) {
    const styleMap = {
      'Draft': 'badge-gray', 'Posted': 'badge-success', 'Voided': 'badge-danger',
      'Active': 'badge-success', 'Inactive': 'badge-gray',
      'Open': 'badge-success', 'Closed': 'badge-gray', 'Locked': 'badge-danger',
      'Approved': 'badge-success', 'Rejected': 'badge-danger', 'Pending': 'badge-warning',
      'WaitingApproval': 'badge-warning',
      'Sent': 'badge-info', 'Paid': 'badge-success', 'PartiallyPaid': 'badge-warning',
      'Overdue': 'badge-danger',
      'Trial': 'badge-warning', 'Expired': 'badge-danger',
      'Submitted': 'badge-info', 'Filed': 'badge-success',
      'Disposed': 'badge-gray', 'FullyDepreciated': 'badge-warning',
      'Cancelled': 'badge-danger', 'Completed': 'badge-success',
      'InProgress': 'badge-info', 'Running': 'badge-info',
      'Matched': 'badge-success', 'Unmatched': 'badge-warning',
      'Reconciled': 'badge-success', 'Processing': 'badge-info',
      'Reversed': 'badge-info',
    };
    const cls = styleMap[status] || 'badge-gray';
    const label = this._t('status.' + status, status);
    return `<span class="badge ${cls}">${label}</span>`;
  },

  // Pagination
  renderPagination(page, totalPages, onPageChange) {
    if (totalPages <= 1) return '';
    let html = '<div class="pagination">';
    html += `<button ${page <= 1 ? 'disabled' : ''} onclick="${onPageChange}(${page - 1})">‹</button>`;
    for (let i = 1; i <= totalPages; i++) {
      if (i === 1 || i === totalPages || (i >= page - 2 && i <= page + 2)) {
        html += `<button class="${i === page ? 'active' : ''}" onclick="${onPageChange}(${i})">${i}</button>`;
      } else if (i === page - 3 || i === page + 3) {
        html += '<button disabled>...</button>';
      }
    }
    html += `<button ${page >= totalPages ? 'disabled' : ''} onclick="${onPageChange}(${page + 1})">›</button>`;
    html += '</div>';
    return html;
  },

  // Account type labels
  accountTypeLabel(type) {
    const map = { Asset: 'สินทรัพย์', Liability: 'หนี้สิน', Equity: 'ส่วนของเจ้าของ', Revenue: 'รายได้', Expense: 'ค่าใช้จ่าย' };
    return map[type] || type;
  },

  // Document type labels & categorization
  _revenueDocTypes: ['Quotation','Invoice','TaxInvoice','Receipt','DeliveryNote','BillingNote','DebitNote','CreditNote','ReceiptVoucher'],
  _expenseDocTypes: ['PurchaseRequisition','PurchaseOrder','PurchaseInvoice','Expense','PaymentVoucher','CertificateInLieu'],

  docTypeLabel(type) {
    const map = {
      Quotation: 'ใบเสนอราคา', Invoice: 'ใบแจ้งหนี้', Receipt: 'ใบเสร็จรับเงิน',
      TaxInvoice: 'ใบกำกับภาษี', DebitNote: 'ใบเพิ่มหนี้', CreditNote: 'ใบลดหนี้',
      DeliveryNote: 'ใบส่งของ', BillingNote: 'ใบวางบิล', ReceiptVoucher: 'ใบสำคัญรับ',
      PurchaseRequisition: 'ใบขอซื้อ', PurchaseOrder: 'ใบสั่งซื้อ',
      GoodsReceiptNote: 'ใบรับสินค้า',
      PurchaseInvoice: 'ใบแจ้งหนี้ซื้อ', Expense: 'ใบบันทึกค่าใช้จ่าย', PaymentVoucher: 'ใบสำคัญจ่าย',
      CertificateInLieu: 'ใบรับรองแทนใบเสร็จรับเงิน'
    };
    return map[type] || type;
  },

  // Export table to CSV
  exportTableCSV(tableEl, filename = 'export.csv') {
    if (typeof tableEl === 'string') tableEl = document.querySelector(tableEl);
    if (!tableEl) return;
    const rows = [...tableEl.querySelectorAll('tr')];
    const csv = rows.map(row =>
      [...row.querySelectorAll('th, td')].map(cell => {
        let text = cell.textContent.trim().replace(/"/g, '""');
        return `"${text}"`;
      }).join(',')
    ).join('\n');
    const bom = '\uFEFF';
    const blob = new Blob([bom + csv], { type: 'text/csv;charset=utf-8;' });
    const link = document.createElement('a');
    link.href = URL.createObjectURL(blob);
    link.download = filename;
    link.click();
    URL.revokeObjectURL(link.href);
    this.toast(this._t('common.csvSuccess', 'ส่งออก CSV สำเร็จ'), 'success');
  },

  // Export table to Excel (simple HTML table format)
  exportTableExcel(tableEl, filename = 'export.xlsx') {
    if (typeof tableEl === 'string') tableEl = document.querySelector(tableEl);
    if (!tableEl) return;
    const html = `<html xmlns:o="urn:schemas-microsoft-com:office:office" xmlns:x="urn:schemas-microsoft-com:office:excel">
      <head><meta charset="UTF-8"><!--[if gte mso 9]><xml><x:ExcelWorkbook><x:ExcelWorksheets><x:ExcelWorksheet>
      <x:Name>Sheet1</x:Name><x:WorksheetOptions><x:DisplayGridlines/></x:WorksheetOptions>
      </x:ExcelWorksheet></x:ExcelWorksheets></x:ExcelWorkbook></xml><![endif]--></head>
      <body><table>${tableEl.innerHTML}</table></body></html>`;
    const blob = new Blob([html], { type: 'application/vnd.ms-excel' });
    const link = document.createElement('a');
    link.href = URL.createObjectURL(blob);
    link.download = filename;
    link.click();
    URL.revokeObjectURL(link.href);
    this.toast(this._t('common.excelSuccess', 'ส่งออก Excel สำเร็จ'), 'success');
  },

  // Print specific element
  printElement(selector, title = 'Next Acc') {
    const el = typeof selector === 'string' ? document.querySelector(selector) : selector;
    if (!el) return;
    const win = window.open('', '_blank');
    win.document.write(`<!DOCTYPE html><html><head><title>${title}</title>
      <link href="https://fonts.googleapis.com/css2?family=Noto+Sans+Thai:wght@300;400;500;600;700&display=swap" rel="stylesheet">
      <link rel="stylesheet" href="/css/style.css">
      <style>body{padding:20px;font-family:'Noto Sans Thai',sans-serif} .no-print{display:none}</style>
      </head><body>${el.outerHTML}</body></html>`);
    win.document.close();
    win.onload = () => { win.print(); win.close(); };
  },

  contactAutocomplete(inputId, hiddenId, { placeholder = 'พิมพ์ชื่อหรือเลขผู้เสียภาษี...', onSelect, onCreateNew } = {}) {
    const input = document.getElementById(inputId);
    const hidden = document.getElementById(hiddenId);
    if (!input || !hidden) return;
    input.setAttribute('autocomplete', 'off');
    input.setAttribute('placeholder', placeholder);
    let dropdown = input.parentElement.querySelector('.ac-dropdown');
    if (!dropdown) {
      dropdown = document.createElement('div');
      dropdown.className = 'ac-dropdown';
      dropdown.style.cssText = 'position:absolute;top:100%;left:0;right:0;z-index:999;background:#fff;border:1px solid var(--gray-200);border-radius:8px;max-height:220px;overflow-y:auto;box-shadow:0 4px 12px rgba(0,0,0,.12);display:none';
      input.parentElement.style.position = 'relative';
      input.parentElement.appendChild(dropdown);
    }
    let contacts = [], debounce = null, autoFiredFor = null;
    const search = async (q) => {
      const api = Layout.api(); if (!api) return;
      try {
        const res = await api.getContacts('?pageSize=20&search=' + encodeURIComponent(q));
        // ตัดผู้ติดต่อที่ปิดใช้งานแล้ว (IsActive=false) ออกจากตัวเลือก —
        // ยังโชว์ในหน้ารายชื่อผู้ติดต่อ (badge "ปิด") แต่ห้ามเลือกมาออกเอกสารใหม่
        contacts = (res.data?.items || res.data || []).filter(c => c.isActive !== false);
      } catch { contacts = []; }
      // Auto-create on a COMPLETE Tax ID with no existing match — the user
      // asked for "type the full tax id and it just creates it". Fires once
      // per distinct id; quickCreateContact dedups + notifies.
      if (onCreateNew && /^\d{13}$/.test(q)
          && !contacts.some(c => (c.taxId || '') === q)
          && autoFiredFor !== q) {
        autoFiredFor = q;
        dropdown.style.display = 'none';
        onCreateNew(q);
        return;
      }
      // When a create-new handler is supplied, always keep the dropdown open
      // (even with zero matches) so the user can add the contact inline.
      if (!contacts.length && !onCreateNew) { dropdown.style.display = 'none'; return; }
      const itemsHtml = contacts.map(c => `<div class="ac-item" data-id="${c.id}" style="padding:8px 12px;cursor:pointer;border-bottom:1px solid var(--gray-100);font-size:13px">
        <div class="font-medium">${Layout.esc(c.name)}</div>
        <div class="text-xs text-gray-500">${Layout.esc(c.taxId || '')} ${c.isCustomer ? '(ลูกค้า)' : ''} ${c.isSupplier ? '(ผู้ขาย)' : ''}</div>
      </div>`).join('');
      // Footer "create new" affordance. A 13-digit query is treated as a Tax
      // ID → offer DBD-assisted auto-create; otherwise create by name.
      let createHtml = '';
      if (onCreateNew) {
        const isTaxId = /^\d{13}$/.test(q);
        const label = isTaxId
          ? `➕ สร้างผู้ติดต่อจากเลขภาษี <b>${Layout.esc(q)}</b> (ดึงชื่อจาก DBD)`
          : `➕ สร้างผู้ติดต่อใหม่ “${Layout.esc(q)}”`;
        createHtml = `<div class="ac-create" style="padding:9px 12px;cursor:pointer;font-size:13px;color:var(--primary);background:#f8fafc;border-top:1px solid var(--gray-200);font-weight:500">${label}</div>`;
      }
      dropdown.innerHTML = itemsHtml + createHtml;
      dropdown.style.display = 'block';
      dropdown.querySelectorAll('.ac-item').forEach(item => {
        item.onmousedown = (e) => {
          e.preventDefault();
          const id = item.dataset.id;
          const c = contacts.find(x => x.id === id);
          hidden.value = id;
          input.value = c ? c.name : '';
          dropdown.style.display = 'none';
          if (onSelect) onSelect(c);
        };
        item.onmouseenter = () => item.style.background = 'var(--gray-50)';
        item.onmouseleave = () => item.style.background = '';
      });
      const createEl = dropdown.querySelector('.ac-create');
      if (createEl) createEl.onmousedown = (e) => {
        e.preventDefault();
        dropdown.style.display = 'none';
        onCreateNew(q);
      };
    };
    input.addEventListener('input', () => {
      hidden.value = '';
      clearTimeout(debounce);
      const q = input.value.trim();
      if (q.length < 1) { dropdown.style.display = 'none'; return; }
      debounce = setTimeout(() => search(q), 250);
    });
    input.addEventListener('focus', () => { if (input.value.trim().length >= 1) search(input.value.trim()); });
    input.addEventListener('blur', () => { setTimeout(() => dropdown.style.display = 'none', 200); });
    return {
      setValue(id, name) { hidden.value = id || ''; input.value = name || ''; },
      getValue() { return hidden.value; },
      clear() { hidden.value = ''; input.value = ''; }
    };
  },

  // ===== Form Validation Utilities =====
  validateTaxId(taxId) {
    if (!taxId) return true;
    const digits = taxId.replace(/\D/g, '');
    if (digits.length !== 13) return false;
    let sum = 0;
    for (let i = 0; i < 12; i++) sum += parseInt(digits[i]) * (13 - i);
    const check = (11 - (sum % 11)) % 10;
    return check === parseInt(digits[12]);
  },

  validatePhone(phone) {
    if (!phone) return true;
    return /^0[0-9]{8,9}$/.test(phone.replace(/[\s-]/g, ''));
  },

  validatePostalCode(code) {
    if (!code) return true;
    return /^[0-9]{5}$/.test(code.trim());
  },

  validateForm(rules) {
    for (const { field, value, label, checks } of rules) {
      for (const check of checks) {
        if (check === 'required' && !value?.trim()) {
          this.toast(`กรุณากรอก${label}`, 'error');
          document.getElementById(field)?.focus();
          return false;
        }
        if (check === 'taxId' && !this.validateTaxId(value)) {
          this.toast(`${label}ไม่ถูกต้อง (ต้องเป็นเลข 13 หลักตามรูปแบบกรมสรรพากร)`, 'error');
          document.getElementById(field)?.focus();
          return false;
        }
        if (check === 'phone' && !this.validatePhone(value)) {
          this.toast(`${label}ไม่ถูกต้อง (รูปแบบ: 0XXXXXXXXX)`, 'error');
          document.getElementById(field)?.focus();
          return false;
        }
        if (check === 'postalCode' && !this.validatePostalCode(value)) {
          this.toast(`${label}ต้องเป็นเลข 5 หลัก`, 'error');
          document.getElementById(field)?.focus();
          return false;
        }
      }
    }
    return true;
  },
};
