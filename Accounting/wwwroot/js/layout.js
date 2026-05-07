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
  _initialized: false,

  esc(str) {
    if (str == null) return '';
    const d = document.createElement('div');
    d.textContent = String(str);
    return d.innerHTML;
  },

  init(pageName) {
    // Prevent double-initialization (loadCompanies calls Page.init which calls Layout.init again)
    if (this._initialized && this.currentPage === pageName) return true;

    this.currentPage = pageName;
    this._initialized = true;
    this.user = JSON.parse(localStorage.getItem('user') || 'null');
    this.currentCompany = JSON.parse(localStorage.getItem('currentCompany') || 'null');
    // Restore cached subscription so menu renders correctly on first paint
    try {
      const cached = JSON.parse(localStorage.getItem('subscription') || 'null');
      if (cached) {
        this.subscription = cached;
        this.features = cached.enabledFeatureNames || [];
        this.subscriptionStatus = cached.status;
      }
    } catch {}
    if (!localStorage.getItem('token')) { window.location.href = '/login.html'; return false; }
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
  },

  _refreshNavMenu() {
    const nav = document.querySelector('.sidebar-nav');
    if (!nav) return;
    const hidden = this.getHiddenMenuItems();
    // Filter out hidden items, then drop sections that have no remaining items underneath
    const items = this.navItems.filter(item => !item.id || !hidden.includes(item.id));
    const visible = [];
    for (let i = 0; i < items.length; i++) {
      const it = items[i];
      if (it.section) {
        // include the section header only if the next non-section item exists in this run
        let hasFollowing = false;
        for (let j = i + 1; j < items.length; j++) {
          if (items[j].section) break;
          hasFollowing = true; break;
        }
        if (hasFollowing) visible.push(it);
      } else {
        visible.push(it);
      }
    }
    nav.innerHTML = visible.map(item => this._renderNavItem(item)).join('');
    this._highlightActiveSection();
  },

  // ===== Per-user menu visibility (stored in localStorage, scoped by company) =====
  _menuKey() {
    const cid = (this.subscription && this.subscription.companyId) ||
                (this.user && this.user.companyId) || 'default';
    return `nextacc_hiddenMenu_${cid}`;
  },
  getHiddenMenuItems() {
    try { return JSON.parse(localStorage.getItem(this._menuKey()) || '[]'); }
    catch { return []; }
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
    if (locked) {
      return `<a href="/pages/subscription.html" class="nav-item nav-item-locked${active}" data-nav-id="${item.id}" title="${this._t('layout.upgradeLocked', 'Upgrade required').replace('{label}', label)}" style="opacity:0.5"><span class="icon">${item.icon}</span>${label}<span style="margin-left:auto;font-size:11px">🔒</span></a>`;
    }
    return `<a href="${item.href}" class="nav-item${active}" data-nav-id="${item.id}"><span class="icon">${item.icon}</span>${label}</a>`;
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
      // Dynamically load SignalR client if not already loaded
      const script = document.createElement('script');
      script.src = 'https://cdnjs.cloudflare.com/ajax/libs/microsoft-signalr/8.0.0/signalr.min.js';
      script.onload = () => this.connectSignalR();
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

  // Navigation organized following PEAK Account structure
  navItems: [
    { section: 'หลัก' },
    { id: 'dashboard', label: 'แดชบอร์ด', icon: '📊', href: '/app.html', feature: 'Dashboard', _i18nKey: 'nav.dashboard' },

    { section: 'รายรับ' },
    { id: 'documents', label: 'ขายสินค้า/บริการ', icon: '📄', href: '/pages/documents.html?side=revenue', feature: 'DocumentEngine', _i18nKey: 'nav.documents' },
    { id: 'revenue-recognition', label: 'รับรู้รายได้', icon: '📈', href: '/pages/revenue-recognition.html', feature: 'RevenueRecognition', _i18nKey: 'nav.revenueRecognition' },

    { section: 'รายจ่าย' },
    { id: 'purchases', label: 'ซื้อสินค้า', icon: '🛒', href: '/pages/purchases.html', feature: 'DocumentEngine', _i18nKey: 'nav.purchases' },
    { id: 'expense', label: 'บันทึกค่าใช้จ่าย', icon: '🧾', href: '/pages/expense.html', feature: 'ExpenseManagement', _i18nKey: 'nav.expense' },
    { id: 'expense-docs', label: 'เอกสารฝั่งจ่าย', icon: '📋', href: '/pages/documents.html?side=expense', feature: 'DocumentEngine', _i18nKey: 'nav.expenseDocs' },
    { id: 'payments', label: 'ชำระเงิน/รวมจ่าย', icon: '💳', href: '/pages/payments.html', feature: 'DocumentEngine', _i18nKey: 'nav.payments' },

    { section: 'รายการอัตโนมัติ' },
    { id: 'recurring', label: 'รายการประจำ', icon: '🔄', href: '/pages/recurring.html', feature: 'RecurringTransactions', _i18nKey: 'nav.recurring' },

    { section: 'ผู้ติดต่อ' },
    { id: 'contacts', label: 'ลูกค้า/ผู้จำหน่าย', icon: '👥', href: '/pages/contacts.html', feature: 'DocumentEngine', _i18nKey: 'nav.contacts' },
    { section: 'สินค้า/บริการ' },
    { id: 'products', label: 'สินค้าและบริการ', icon: '📦', href: '/pages/products.html', feature: 'Inventory', _i18nKey: 'nav.products' },
    { id: 'warehouse', label: 'คลังสินค้า', icon: '🏭', href: '/pages/warehouse.html', feature: 'WarehouseManagement', _i18nKey: 'nav.warehouse' },
    { id: 'inventory-reports', label: 'รายงานสินค้าคงเหลือ', icon: '📊', href: '/pages/inventory-reports.html', feature: 'Inventory', _i18nKey: 'nav.inventoryReports' },
    { id: 'supplies', label: 'วัสดุสิ้นเปลือง', icon: '🧹', href: '/pages/supplies.html', feature: 'Inventory', _i18nKey: 'nav.supplies' },

    { section: 'POS ขายหน้าร้าน' },
    { id: 'pos', label: 'หน้าขาย POS', icon: '🖥️', href: '/pages/pos.html', feature: 'DocumentEngine', _i18nKey: 'nav.pos' },
    { id: 'pos-packages', label: 'แพ็คเกจบริการ', icon: '💆', href: '/pages/pos-packages.html', feature: 'DocumentEngine', _i18nKey: 'nav.posPackages' },
    { id: 'pos-modifiers', label: 'ตัวเลือกสินค้า', icon: '🔧', href: '/pages/pos-modifiers.html', feature: 'DocumentEngine', _i18nKey: 'nav.posModifiers' },
    { id: 'pos-reports', label: 'รายงาน POS', icon: '📊', href: '/pages/pos-reports.html', feature: 'DocumentEngine', _i18nKey: 'nav.posReports' },

    { section: 'การเงิน' },
    { id: 'bank', label: 'บัญชีธนาคาร', icon: '🏦', href: '/pages/bank.html', feature: 'BankReconciliation', _i18nKey: 'nav.bank' },
    { id: 'loans', label: 'สินเชื่อ/เงินกู้', icon: '💰', href: '/pages/loans.html', feature: 'LoanManagement', _i18nKey: 'nav.loans' },
    { id: 'multi-currency', label: 'สกุลเงินต่างประเทศ', icon: '💱', href: '/pages/multi-currency.html', feature: 'MultiCurrency', _i18nKey: 'nav.multiCurrency' },

    { section: 'บัญชี' },
    { id: 'accounts', label: 'ผังบัญชี', icon: '📋', href: '/pages/accounts.html', feature: 'BasicAccounting', _i18nKey: 'nav.accounts' },
    { id: 'journals', label: 'สมุดรายวัน', icon: '📝', href: '/pages/journals.html', feature: 'BasicAccounting', _i18nKey: 'nav.journals' },
    { id: 'general-ledger', label: 'บัญชีแยกประเภท', icon: '📒', href: '/pages/general-ledger.html', feature: 'BasicAccounting', _i18nKey: 'nav.generalLedger' },
    { id: 'fiscal', label: 'งวดบัญชี', icon: '📅', href: '/pages/fiscal.html', feature: 'BasicAccounting', _i18nKey: 'nav.fiscal' },
    { id: 'fixed-assets', label: 'สินทรัพย์ถาวร', icon: '🏢', href: '/pages/fixed-assets.html', feature: 'FixedAssets', _i18nKey: 'nav.fixedAssets' },
    { id: 'financial-mgmt', label: 'บริหารการเงิน', icon: '💰', href: '/pages/financial-mgmt.html', feature: 'AdvancedReporting', _i18nKey: 'nav.financialMgmt' },

    { section: 'ภาษี' },
    { id: 'tax', label: 'รายงานภาษี (ภ.พ.30)', icon: '🏛️', href: '/pages/tax.html', feature: 'TaxManagement', _i18nKey: 'nav.tax' },
    { id: 'wht', label: 'หัก ณ ที่จ่าย (ภ.ง.ด.)', icon: '📜', href: '/pages/wht.html', feature: 'TaxManagement', _i18nKey: 'nav.wht' },
    { id: 'tax-calendar', label: 'ปฏิทินภาษี', icon: '📆', href: '/pages/tax-calendar.html', feature: 'TaxManagement', _i18nKey: 'nav.taxCalendar' },
    { id: 'etax', label: 'e-Tax Invoice', icon: '🧾', href: '/pages/etax.html', feature: 'EtaxInvoice', _i18nKey: 'nav.etax' },
    { id: 'tax-export', label: 'Export ยื่นภาษี/ประกันสังคม', icon: '📤', href: '/pages/tax-export.html', feature: 'TaxManagement', _i18nKey: 'nav.taxExport' },

    { section: 'เงินเดือน' },
    { id: 'payroll', label: 'ระบบเงินเดือน', icon: '💵', href: '/pages/payroll.html', feature: 'Payroll', _i18nKey: 'nav.payroll' },
    { id: 'commission', label: 'คอมมิชชัน', icon: '💸', href: '/pages/commission.html', feature: 'Commission', _i18nKey: 'nav.commission' },

    { section: 'รายงาน' },
    { id: 'executive-reports', label: 'รายงานผู้บริหาร', icon: '👔', href: '/pages/executive-reports.html', feature: 'AdvancedReporting', _i18nKey: 'nav.executiveReports' },
    { id: 'reports', label: 'รายงานการเงิน', icon: '📈', href: '/pages/reports.html', feature: 'BasicAccounting', _i18nKey: 'nav.reports' },
    { id: 'budget', label: 'งบประมาณ', icon: '🎯', href: '/pages/budget.html', feature: 'BudgetManagement', _i18nKey: 'nav.budget' },
    { id: 'aging', label: 'อายุลูกหนี้/เจ้าหนี้', icon: '⏳', href: '/pages/aging.html', feature: 'AgingReport', _i18nKey: 'nav.aging' },
    { id: 'arap-analysis', label: 'วิเคราะห์ AR/AP', icon: '🔍', href: '/pages/arap-analysis.html', feature: 'AdvancedReporting', _i18nKey: 'nav.arapAnalysis' },
    { id: 'fpa', label: 'วิเคราะห์การเงิน', icon: '📉', href: '/pages/fpa.html', feature: 'FPA', _i18nKey: 'nav.fpa' },

    { section: 'โครงการ/องค์กร' },
    { id: 'projects', label: 'โครงการ', icon: '📐', href: '/pages/projects.html', feature: 'ProjectAccounting', _i18nKey: 'nav.projects' },
    { id: 'time-billing', label: 'บันทึกเวลา', icon: '⏱️', href: '/pages/time-billing.html', feature: 'TimeBilling', _i18nKey: 'nav.timeBilling' },
    { id: 'dimensions', label: 'สาขาและมิติ', icon: '🏬', href: '/pages/dimensions.html', feature: 'CostCenter', _i18nKey: 'nav.dimensions' },
    { id: 'intercompany', label: 'ระหว่างบริษัท', icon: '🔗', href: '/pages/intercompany.html', feature: 'MultiCompany', _i18nKey: 'nav.intercompany' },
    { id: 'consolidation', label: 'งบการเงินรวม', icon: '📑', href: '/pages/consolidation.html', feature: 'Consolidation', _i18nKey: 'nav.consolidation' },

    { section: 'คลังเอกสาร' },
    { id: 'import-export', label: 'นำเข้า/ส่งออก', icon: '📥', href: '/pages/import-export.html', feature: 'BulkImport', _i18nKey: 'nav.importExport' },
    { id: 'customer-portal', label: 'Portal ลูกค้า', icon: '🌐', href: '/pages/customer-portal.html', feature: 'CustomerPortal', _i18nKey: 'nav.customerPortal' },
    { id: 'ai-tools', label: 'AI อัจฉริยะ', icon: '🤖', href: '/pages/ai-tools.html', feature: 'AI_Features', _i18nKey: 'nav.aiTools' },
    { id: 'document-scan', label: 'สแกนเอกสาร', icon: '📸', href: '/pages/document-scan.html', feature: 'AI_Features', _i18nKey: 'nav.documentScan' },

    { section: 'ตั้งค่า' },
    { id: 'team', label: 'จัดการทีม', icon: '👥', href: '/pages/team.html', feature: 'MultiUser', _i18nKey: 'nav.team' },
    { id: 'settings', label: 'ตั้งค่าบริษัท', icon: '⚙️', href: '/pages/settings.html', _i18nKey: 'nav.settings' },
    { id: 'approval', label: 'การอนุมัติ', icon: '✅', href: '/pages/approval.html', feature: 'ApprovalWorkflow', _i18nKey: 'nav.approval' },
    { id: 'signatures', label: 'ลายเซ็นและอนุมัติ', icon: '✍️', href: '/pages/signatures.html', feature: 'ApprovalWorkflow', _i18nKey: 'nav.signatures' },
    { id: 'integrations', label: 'เชื่อมต่อระบบ', icon: '🔗', href: '/pages/integrations.html', feature: 'APIAccess', _i18nKey: 'nav.integrations' },
    { id: 'api-developer', label: 'API Developer', icon: '📘', href: '/pages/api-developer.html', feature: 'APIAccess', _i18nKey: 'nav.apiDeveloper' },
    { id: 'webhooks', label: 'Webhooks & API', icon: '🔌', href: '/pages/webhooks.html', feature: 'Webhook', _i18nKey: 'nav.webhooks' },
    { id: 'subscription', label: 'แพ็กเกจ', icon: '💎', href: '/pages/subscription.html', _i18nKey: 'nav.subscription' },
    { id: 'usage', label: 'สถานะการใช้งาน', icon: '📊', href: '/pages/usage.html', _i18nKey: 'nav.usage' },
    { id: 'audit', label: 'บันทึกกิจกรรม', icon: '🔍', href: '/pages/audit.html', feature: 'AuditLog', _i18nKey: 'nav.audit' },
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
        ${this.navItems.map(item => this._renderNavItem(item)).join('')}
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
            <a class="dropdown-item" href="/pages/settings.html">⚙️ ${this.esc(tSettings)}</a>
            <div class="dropdown-divider"></div>
            <a class="dropdown-item" href="#" onclick="Layout.logout();return false">🚪 ${this.esc(tLogout)}</a>
          </div>
        </div>
      </div>
    `;

    // Wrap page content
    const pageContent = document.getElementById('pageContent');
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

  showCompanySetupPrompt() {
    this.setupStep = 1;
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
      formContent = `
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
  openModal(id) { document.getElementById(id).classList.add('active'); },
  closeModal(id) { document.getElementById(id).classList.remove('active'); },

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

  date(d) {
    if (!d) return '-';
    const dt = new Date(d);
    if (isNaN(dt.getTime())) return '-';
    return dt.toLocaleDateString('th-TH', { year: 'numeric', month: 'short', day: 'numeric' });
  },

  dateInput(d) {
    if (!d) return '';
    const dt = new Date(d);
    if (isNaN(dt.getTime())) return '';
    return dt.toISOString().split('T')[0];
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
  _expenseDocTypes: ['PurchaseRequisition','PurchaseOrder','PurchaseInvoice','Expense','PaymentVoucher'],

  docTypeLabel(type) {
    const map = {
      Quotation: 'ใบเสนอราคา', Invoice: 'ใบแจ้งหนี้', Receipt: 'ใบเสร็จรับเงิน',
      TaxInvoice: 'ใบกำกับภาษี', DebitNote: 'ใบเพิ่มหนี้', CreditNote: 'ใบลดหนี้',
      DeliveryNote: 'ใบส่งของ', BillingNote: 'ใบวางบิล', ReceiptVoucher: 'ใบสำคัญรับ',
      PurchaseRequisition: 'ใบขอซื้อ', PurchaseOrder: 'ใบสั่งซื้อ',
      PurchaseInvoice: 'ใบแจ้งหนี้ซื้อ', Expense: 'ค่าใช้จ่าย', PaymentVoucher: 'ใบสำคัญจ่าย'
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

  contactAutocomplete(inputId, hiddenId, { placeholder = 'พิมพ์ชื่อหรือเลขผู้เสียภาษี...', onSelect } = {}) {
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
    let contacts = [], debounce = null;
    const search = async (q) => {
      const api = Layout.api(); if (!api) return;
      try {
        const res = await api.getContacts('?pageSize=20&search=' + encodeURIComponent(q));
        contacts = res.data?.items || res.data || [];
      } catch { contacts = []; }
      if (!contacts.length) { dropdown.style.display = 'none'; return; }
      dropdown.innerHTML = contacts.map(c => `<div class="ac-item" data-id="${c.id}" style="padding:8px 12px;cursor:pointer;border-bottom:1px solid var(--gray-100);font-size:13px">
        <div class="font-medium">${Layout.esc(c.name)}</div>
        <div class="text-xs text-gray-500">${Layout.esc(c.taxId || '')} ${c.isCustomer ? '(ลูกค้า)' : ''} ${c.isSupplier ? '(ผู้ขาย)' : ''}</div>
      </div>`).join('');
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
