// ===== Shared Layout Component =====
// Provides sidebar navigation + header for all app pages

const Layout = {
  currentPage: '',
  user: null,
  companies: [],
  currentCompany: null,
  _initialized: false,

  init(pageName) {
    // Prevent double-initialization (loadCompanies calls Page.init which calls Layout.init again)
    if (this._initialized && this.currentPage === pageName) return true;

    this.currentPage = pageName;
    this._initialized = true;
    this.user = JSON.parse(localStorage.getItem('user') || 'null');
    this.currentCompany = JSON.parse(localStorage.getItem('currentCompany') || 'null');
    if (!localStorage.getItem('token')) { window.location.href = '/login.html'; return false; }
    this.render();
    this.bindEvents();
    this.loadNotificationCount();
    this.initServiceWorker();
    this.initSignalR();
    return true;
  },

  // PWA Service Worker
  initServiceWorker() {
    if ('serviceWorker' in navigator) {
      navigator.serviceWorker.register('/sw.js').catch(() => {});
    }
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
        this.toast(notification.title || notification.message || 'การแจ้งเตือนใหม่', 'info');
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
    { id: 'dashboard', label: 'แดชบอร์ด', icon: '📊', href: '/app.html' },

    { section: 'รายรับ' },
    { id: 'documents', label: 'ใบเสนอราคา/แจ้งหนี้', icon: '📄', href: '/pages/documents.html' },
    { id: 'revenue-recognition', label: 'รับรู้รายได้', icon: '📈', href: '/pages/revenue-recognition.html' },
    { id: 'recurring', label: 'รายการประจำ', icon: '🔄', href: '/pages/recurring.html' },

    { section: 'รายจ่าย' },
    { id: 'expense', label: 'บันทึกค่าใช้จ่าย', icon: '🧾', href: '/pages/expense.html' },
    { id: 'payments', label: 'การชำระเงิน', icon: '💳', href: '/pages/payments.html' },

    { section: 'ผู้ติดต่อ' },
    { id: 'contacts', label: 'ลูกค้า/ผู้จำหน่าย', icon: '👥', href: '/pages/contacts.html' },
    { id: 'freelance', label: 'Freelancer/ผู้รับจ้าง', icon: '👤', href: '/pages/freelance.html' },

    { section: 'สินค้า/บริการ' },
    { id: 'products', label: 'สินค้าและบริการ', icon: '📦', href: '/pages/products.html' },
    { id: 'warehouse', label: 'คลังสินค้า', icon: '🏭', href: '/pages/warehouse.html' },

    { section: 'POS ขายหน้าร้าน' },
    { id: 'pos', label: 'หน้าขาย POS', icon: '🖥️', href: '/pages/pos.html' },
    { id: 'pos-packages', label: 'แพ็คเกจบริการ', icon: '💆', href: '/pages/pos-packages.html' },
    { id: 'pos-modifiers', label: 'ตัวเลือกสินค้า', icon: '🔧', href: '/pages/pos-modifiers.html' },
    { id: 'pos-reports', label: 'รายงาน POS', icon: '📊', href: '/pages/pos-reports.html' },

    { section: 'การเงิน' },
    { id: 'bank', label: 'บัญชีธนาคาร', icon: '🏦', href: '/pages/bank.html' },
    { id: 'loans', label: 'สินเชื่อ/เงินกู้', icon: '💰', href: '/pages/loans.html' },
    { id: 'multi-currency', label: 'สกุลเงินต่างประเทศ', icon: '💱', href: '/pages/multi-currency.html' },

    { section: 'บัญชี' },
    { id: 'accounts', label: 'ผังบัญชี', icon: '📋', href: '/pages/accounts.html' },
    { id: 'journals', label: 'สมุดรายวัน', icon: '📝', href: '/pages/journals.html' },
    { id: 'fiscal', label: 'งวดบัญชี', icon: '📅', href: '/pages/fiscal.html' },
    { id: 'fixed-assets', label: 'สินทรัพย์ถาวร', icon: '🏢', href: '/pages/fixed-assets.html' },

    { section: 'ภาษี' },
    { id: 'tax', label: 'รายงานภาษี (ภ.พ.30)', icon: '🏛️', href: '/pages/tax.html' },
    { id: 'wht', label: 'หัก ณ ที่จ่าย (ภ.ง.ด.)', icon: '📜', href: '/pages/wht.html' },
    { id: 'tax-calendar', label: 'ปฏิทินภาษี', icon: '📆', href: '/pages/tax-calendar.html' },
    { id: 'etax', label: 'e-Tax Invoice', icon: '🧾', href: '/pages/etax.html' },

    { section: 'เงินเดือน' },
    { id: 'payroll', label: 'ระบบเงินเดือน', icon: '💵', href: '/pages/payroll.html' },
    { id: 'commission', label: 'คอมมิชชัน', icon: '💸', href: '/pages/commission.html' },

    { section: 'รายงาน' },
    { id: 'reports', label: 'รายงานการเงิน', icon: '📈', href: '/pages/reports.html' },
    { id: 'budget', label: 'งบประมาณ', icon: '🎯', href: '/pages/budget.html' },
    { id: 'aging', label: 'อายุลูกหนี้/เจ้าหนี้', icon: '⏳', href: '/pages/aging.html' },
    { id: 'fpa', label: 'วิเคราะห์การเงิน', icon: '📉', href: '/pages/fpa.html' },

    { section: 'โครงการ/องค์กร' },
    { id: 'projects', label: 'โครงการ', icon: '📐', href: '/pages/projects.html' },
    { id: 'time-billing', label: 'บันทึกเวลา', icon: '⏱️', href: '/pages/time-billing.html' },
    { id: 'dimensions', label: 'สาขาและมิติ', icon: '🏬', href: '/pages/dimensions.html' },
    { id: 'intercompany', label: 'ระหว่างบริษัท', icon: '🔗', href: '/pages/intercompany.html' },
    { id: 'consolidation', label: 'งบการเงินรวม', icon: '📑', href: '/pages/consolidation.html' },

    { section: 'คลังเอกสาร' },
    { id: 'import-export', label: 'นำเข้า/ส่งออก', icon: '📥', href: '/pages/import-export.html' },
    { id: 'customer-portal', label: 'Portal ลูกค้า', icon: '🌐', href: '/pages/customer-portal.html' },
    { id: 'ai-tools', label: 'AI อัจฉริยะ', icon: '🤖', href: '/pages/ai-tools.html' },

    { section: 'ตั้งค่า' },
    { id: 'settings', label: 'ตั้งค่าบริษัท', icon: '⚙️', href: '/pages/settings.html' },
    { id: 'approval', label: 'การอนุมัติ', icon: '✅', href: '/pages/approval.html' },
    { id: 'webhooks', label: 'Webhooks & API', icon: '🔌', href: '/pages/webhooks.html' },
    { id: 'subscription', label: 'แพ็กเกจ', icon: '💎', href: '/pages/subscription.html' },
    { id: 'usage', label: 'สถานะการใช้งาน', icon: '📊', href: '/pages/usage.html' },
    { id: 'audit', label: 'บันทึกกิจกรรม', icon: '🔍', href: '/pages/audit.html' },
  ],

  render() {
    // Create sidebar
    const sidebar = document.createElement('aside');
    sidebar.className = 'sidebar';
    sidebar.id = 'sidebar';
    sidebar.innerHTML = `
      <div class="sidebar-header">
        <div class="sidebar-logo"><span>Nexaacc</span></div>
      </div>
      <div style="padding:12px 16px;border-bottom:1px solid var(--gray-800)">
        <select id="companySelect" class="form-select" style="background:var(--gray-800);color:#fff;border-color:var(--gray-700);font-size:13px;padding:8px 10px">
          <option value="">-- เลือกบริษัท --</option>
        </select>
      </div>
      <nav class="sidebar-nav">
        ${this.navItems.map(item => {
          if (item.section) return `<div class="nav-section">${item.section}</div>`;
          const active = item.id === this.currentPage ? ' active' : '';
          return `<a href="${item.href}" class="nav-item${active}"><span class="icon">${item.icon}</span>${item.label}</a>`;
        }).join('')}
      </nav>
      <div class="sidebar-footer">
        <a href="#" class="nav-item" onclick="Layout.logout();return false"><span class="icon">🚪</span>ออกจากระบบ</a>
      </div>
    `;

    // Create header
    const header = document.createElement('header');
    header.className = 'app-header';
    header.innerHTML = `
      <div class="header-left">
        <button class="mobile-toggle" onclick="document.getElementById('sidebar').classList.toggle('open')">☰</button>
        <h1 class="header-title" id="headerTitle"></h1>
      </div>
      <div class="header-right">
        <button class="header-icon-btn" onclick="Layout.toggleNotifications()" title="การแจ้งเตือน">
          🔔<span class="badge-dot hidden" id="notifDot"></span>
        </button>
        <div class="dropdown">
          <div class="header-user" onclick="this.nextElementSibling.classList.toggle('show')">
            <div class="header-avatar">${(this.user?.fullName || 'U').charAt(0)}</div>
            <span class="text-sm font-medium">${this.user?.fullName || 'ผู้ใช้'}</span>
          </div>
          <div class="dropdown-menu" id="userDropdown">
            <a class="dropdown-item" href="/pages/settings.html">⚙️ ตั้งค่า</a>
            <div class="dropdown-divider"></div>
            <a class="dropdown-item" href="#" onclick="Layout.logout();return false">🚪 ออกจากระบบ</a>
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

    const appLayout = document.createElement('div');
    appLayout.className = 'app-layout';
    appLayout.appendChild(sidebar);
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
    np.innerHTML = `<div class="modal" style="max-width:420px"><div class="modal-header"><h3 class="modal-title">การแจ้งเตือน</h3><button class="modal-close" onclick="Layout.closeNotifications()">&times;</button></div><div class="modal-body" id="notifList" style="max-height:400px;overflow-y:auto"><p class="text-gray-500 text-sm text-center" style="padding:20px">ไม่มีการแจ้งเตือน</p></div></div>`;
    document.body.appendChild(np);

    this.loadCompanies();
  },

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
        select.innerHTML = '<option value="">ยังไม่มีบริษัท</option>';
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
        // Always refresh localStorage with full company data from API (includes isSetupComplete etc.)
        const fresh = companies.find(c => c.id === this.currentCompany.id);
        if (fresh) {
          this.currentCompany = fresh;
          localStorage.setItem('currentCompany', JSON.stringify(fresh));
        }
      }

      // Show setup reminder on dashboard if setup not complete (no forced redirect)
      const selected = companies.find(c => c.id === (this.currentCompany?.id || companies[0].id));
      if (selected && !selected.isSetupComplete) {
        const path = window.location.pathname;
        if (path === '/app.html' || path === '/') {
          this.showSetupReminder();
        }
      }

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
    banner.innerHTML = `
      <div>
        <strong style="font-size:1rem">⚙️ ตั้งค่าบริษัทให้เสร็จสมบูรณ์</strong>
        <p style="margin:4px 0 0;font-size:0.875rem;opacity:0.9">กรอกข้อมูลบริษัทเพื่อออกเอกสารภาษีและรายงานได้ถูกต้อง</p>
      </div>
      <a href="/pages/settings.html?setup=1" class="btn" style="background:rgba(255,255,255,0.2);color:#fff;border:1px solid rgba(255,255,255,0.3);white-space:nowrap">ตั้งค่าเลย</a>`;
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
        <h2 style="margin-bottom:4px">ยินดีต้อนรับสู่ Nexaacc!</h2>
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
      if (!this._setupData.name) { this.toast('กรุณากรอกชื่อบริษัท', 'error'); return; }
      if (!this._setupData.taxId) { this.toast('กรุณากรอกเลขผู้เสียภาษี', 'error'); return; }
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
    if (!d.name) { this.toast('กรุณากรอกชื่อบริษัท', 'error'); return; }
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
      this.toast('สร้างบริษัทสำเร็จ!');
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
              <div class="text-sm font-medium">${(n.title||'').replace(/</g,'&lt;').replace(/>/g,'&gt;')}</div>
              <div class="text-xs text-gray-500" style="margin-top:2px">${(n.message||'').replace(/</g,'&lt;').replace(/>/g,'&gt;')}</div>
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

  logout() {
    localStorage.removeItem('token');
    localStorage.removeItem('user');
    localStorage.removeItem('currentCompany');
    window.location.href = '/login.html';
  },

  setTitle(title) {
    document.getElementById('headerTitle').textContent = title;
    document.title = title + ' - Nexaacc';
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
    t.innerHTML = `${type === 'success' ? '✅' : type === 'error' ? '❌' : 'ℹ️'} ${msg}`;
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
    const map = {
      'Draft': ['badge-gray', 'ร่าง'], 'Posted': ['badge-success', 'ผ่านรายการ'], 'Voided': ['badge-danger', 'ยกเลิก'],
      'Active': ['badge-success', 'ใช้งาน'], 'Inactive': ['badge-gray', 'ปิดใช้งาน'],
      'Open': ['badge-success', 'เปิด'], 'Closed': ['badge-gray', 'ปิด'], 'Locked': ['badge-danger', 'ล็อค'],
      'Approved': ['badge-success', 'อนุมัติ'], 'Rejected': ['badge-danger', 'ปฏิเสธ'], 'Pending': ['badge-warning', 'รออนุมัติ'],
      'WaitingApproval': ['badge-warning', 'รออนุมัติ'],
      'Sent': ['badge-info', 'ส่งแล้ว'], 'Paid': ['badge-success', 'ชำระแล้ว'], 'PartiallyPaid': ['badge-warning', 'ชำระบางส่วน'],
      'Overdue': ['badge-danger', 'เกินกำหนด'],
      'Trial': ['badge-warning', 'ทดลอง'], 'Expired': ['badge-danger', 'หมดอายุ'],
      'Submitted': ['badge-info', 'ส่งแล้ว'], 'Filed': ['badge-success', 'ยื่นแล้ว'],
      'Disposed': ['badge-gray', 'จำหน่าย'], 'FullyDepreciated': ['badge-warning', 'หมดค่าเสื่อม'],
      'Cancelled': ['badge-danger', 'ยกเลิก'], 'Completed': ['badge-success', 'เสร็จสิ้น'],
      'InProgress': ['badge-info', 'กำลังดำเนินการ'], 'Running': ['badge-info', 'กำลังประมวลผล'],
      'Matched': ['badge-success', 'จับคู่แล้ว'], 'Unmatched': ['badge-warning', 'ยังไม่จับคู่'],
      'Reconciled': ['badge-success', 'กระทบยอดแล้ว'], 'Processing': ['badge-info', 'กำลังประมวลผล'],
    };
    const [cls, label] = map[status] || ['badge-gray', status];
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

  docTypeLabel(type) {
    const map = {
      Quotation: 'ใบเสนอราคา', Invoice: 'ใบแจ้งหนี้', Receipt: 'ใบเสร็จรับเงิน',
      TaxInvoice: 'ใบกำกับภาษี', DebitNote: 'ใบเพิ่มหนี้', CreditNote: 'ใบลดหนี้',
      PurchaseOrder: 'ใบสั่งซื้อ', PurchaseInvoice: 'ใบรับสินค้า', Expense: 'ค่าใช้จ่าย',
      DeliveryNote: 'ใบส่งของ', BillingNote: 'ใบวางบิล'
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
    this.toast('ส่งออก CSV สำเร็จ', 'success');
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
    this.toast('ส่งออก Excel สำเร็จ', 'success');
  },

  // Print specific element
  printElement(selector, title = 'Nexaacc') {
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
};
