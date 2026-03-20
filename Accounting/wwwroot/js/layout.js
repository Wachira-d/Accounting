// ===== Shared Layout Component =====
// Provides sidebar navigation + header for all app pages

const Layout = {
  currentPage: '',
  user: null,
  companies: [],
  currentCompany: null,

  init(pageName) {
    this.currentPage = pageName;
    this.user = JSON.parse(localStorage.getItem('user') || 'null');
    this.currentCompany = JSON.parse(localStorage.getItem('currentCompany') || 'null');
    if (!localStorage.getItem('token')) { window.location.href = '/login.html'; return false; }
    this.render();
    this.bindEvents();
    this.loadNotificationCount();
    return true;
  },

  navItems: [
    { section: 'หลัก' },
    { id: 'dashboard', label: 'แดชบอร์ด', icon: '📊', href: '/app.html' },
    { section: 'บัญชี' },
    { id: 'accounts', label: 'ผังบัญชี', icon: '📋', href: '/pages/accounts.html' },
    { id: 'journals', label: 'สมุดรายวัน', icon: '📝', href: '/pages/journals.html' },
    { id: 'fiscal', label: 'งวดบัญชี', icon: '📅', href: '/pages/fiscal.html' },
    { section: 'เอกสาร' },
    { id: 'documents', label: 'เอกสารทั้งหมด', icon: '📄', href: '/pages/documents.html' },
    { id: 'contacts', label: 'ผู้ติดต่อ', icon: '👥', href: '/pages/contacts.html' },
    { id: 'payments', label: 'การชำระเงิน', icon: '💳', href: '/pages/payments.html' },
    { section: 'สินค้าและบริการ' },
    { id: 'products', label: 'สินค้า/บริการ', icon: '📦', href: '/pages/products.html' },
    { id: 'warehouse', label: 'คลังสินค้า', icon: '🏭', href: '/pages/warehouse.html' },
    { section: 'การเงิน' },
    { id: 'bank', label: 'บัญชีธนาคาร', icon: '🏦', href: '/pages/bank.html' },
    { id: 'expense', label: 'เบิกค่าใช้จ่าย', icon: '🧾', href: '/pages/expense.html' },
    { id: 'loans', label: 'สินเชื่อ', icon: '💰', href: '/pages/loans.html' },
    { section: 'ภาษี' },
    { id: 'tax', label: 'รายงานภาษี', icon: '🏛️', href: '/pages/tax.html' },
    { id: 'wht', label: 'หนังสือรับรองหัก ณ ที่จ่าย', icon: '📜', href: '/pages/wht.html' },
    { section: 'รายงาน' },
    { id: 'reports', label: 'รายงานการเงิน', icon: '📈', href: '/pages/reports.html' },
    { id: 'budget', label: 'งบประมาณ', icon: '🎯', href: '/pages/budget.html' },
    { id: 'aging', label: 'รายงานอายุลูกหนี้', icon: '⏳', href: '/pages/aging.html' },
    { section: 'สินทรัพย์' },
    { id: 'fixed-assets', label: 'สินทรัพย์ถาวร', icon: '🏢', href: '/pages/fixed-assets.html' },
    { section: 'โครงการ' },
    { id: 'projects', label: 'โครงการ', icon: '📐', href: '/pages/projects.html' },
    { section: 'ระบบ' },
    { id: 'approval', label: 'อนุมัติ', icon: '✅', href: '/pages/approval.html' },
    { id: 'settings', label: 'ตั้งค่า', icon: '⚙️', href: '/pages/settings.html' },
    { id: 'audit', label: 'บันทึกกิจกรรม', icon: '🔍', href: '/pages/audit.html' },
  ],

  render() {
    // Create sidebar
    const sidebar = document.createElement('aside');
    sidebar.className = 'sidebar';
    sidebar.id = 'sidebar';
    sidebar.innerHTML = `
      <div class="sidebar-header">
        <div class="sidebar-logo"><span>AcctPlatform</span></div>
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
      const companies = res.data?.items || res.data || [];
      const select = document.getElementById('companySelect');
      companies.forEach(c => {
        const opt = document.createElement('option');
        opt.value = c.id;
        opt.textContent = c.name;
        if (this.currentCompany?.id === c.id) opt.selected = true;
        select.appendChild(opt);
      });
      if (!this.currentCompany && companies.length > 0) {
        this.currentCompany = companies[0];
        localStorage.setItem('currentCompany', JSON.stringify(companies[0]));
        select.value = companies[0].id;
      }
    } catch (e) { console.warn('Could not load companies:', e); }
  },

  bindEvents() {
    document.addEventListener('change', e => {
      if (e.target.id === 'companySelect') {
        const id = e.target.value;
        if (id) {
          this.currentCompany = { id };
          localStorage.setItem('currentCompany', JSON.stringify({ id }));
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
    if (!cid) { Layout.toast('กรุณาเลือกบริษัทก่อน', 'error'); return null; }
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
              <div class="text-sm font-medium">${n.title}</div>
              <div class="text-xs text-gray-500" style="margin-top:2px">${n.message}</div>
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
    document.title = title + ' - AcctPlatform';
  },

  // Toast notifications
  toast(msg, type = 'success') {
    const container = document.getElementById('toastContainer');
    const t = document.createElement('div');
    t.className = `toast toast-${type}`;
    t.innerHTML = `${type === 'success' ? '✅' : type === 'error' ? '❌' : 'ℹ️'} ${msg}`;
    container.appendChild(t);
    setTimeout(() => { t.style.opacity = '0'; setTimeout(() => t.remove(), 300); }, 3500);
  },

  // Modal helpers
  openModal(id) { document.getElementById(id).classList.add('active'); },
  closeModal(id) { document.getElementById(id).classList.remove('active'); },

  // Format helpers
  money(n) {
    if (n == null) return '0.00';
    return Number(n).toLocaleString('th-TH', { minimumFractionDigits: 2, maximumFractionDigits: 2 });
  },

  date(d) {
    if (!d) return '-';
    return new Date(d).toLocaleDateString('th-TH', { year: 'numeric', month: 'short', day: 'numeric' });
  },

  dateInput(d) {
    if (!d) return '';
    return new Date(d).toISOString().split('T')[0];
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
};
