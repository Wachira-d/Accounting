// Admin Panel Layout
const AdminLayout = {
  currentPage: '',
  navItems: [
    { section: 'ภาพรวม' },
    { id: 'dashboard', label: 'แดชบอร์ด', icon: '📊', href: '/admin/index.html' },
    { section: 'จัดการลูกค้า' },
    { id: 'customers', label: 'ลูกค้า/บริษัท', icon: '🏢', href: '/admin/customers.html' },
    { id: 'users', label: 'ผู้ใช้งาน', icon: '👥', href: '/admin/users.html' },
    { id: 'account-subs', label: '🎫 Account Plans (User License)', icon: '🎫', href: '/admin/account-subscriptions.html' },
    { section: 'แพ็กเกจ' },
    { id: 'plans', label: 'จัดการแพ็กเกจ', icon: '💎', href: '/admin/plans.html' },
    { section: 'การเงิน' },
    { id: 'payments', label: 'ตรวจสอบการชำระ', icon: '💳', href: '/admin/payments.html' },
    { section: 'เชื่อมต่อระบบ' },
    { id: 'integrations', label: 'Integration', icon: '🔗', href: '/admin/integrations.html' },
    { section: 'ตั้งค่าระบบบัญชี' },
    { id: 'coa-template', label: 'ผังบัญชีต้นแบบ', icon: '📒', href: '/admin/coa-template.html' },
    { section: 'ตั้งค่า' },
    { id: 'site-settings', label: 'ตั้งค่าเว็บไซต์', icon: '⚙️', href: '/admin/site-settings.html' },
    { id: 'system-email', label: 'อีเมลระบบ (SMTP)', icon: '📧', href: '/admin/system-email.html' },
    { id: 'ocr-config', label: 'ตั้งค่า OCR / Azure DI', icon: '🔍', href: '/admin/ocr-config.html' },
    { id: 'ai-config', label: 'AI Augmentation (DeepSeek)', icon: '🤖', href: '/admin/ai-config.html' },
    { section: 'ตรวจสอบระบบ' },
    { id: 'audit-log', label: 'บันทึกกิจกรรม (Audit)', icon: '📜', href: '/admin/audit-log.html' },
    { id: 'error-log', label: 'บันทึกข้อผิดพลาด', icon: '🐞', href: '/admin/error-log.html' },
    { id: 'background-jobs', label: 'งานเบื้องหลัง (Jobs)', icon: '🛠️', href: '/admin/background-jobs.html' },
  ],

  init(pageName) {
    this.currentPage = pageName;
    const token = localStorage.getItem('admin_token');
    const user = JSON.parse(localStorage.getItem('admin_user') || 'null');
    if (!token || !user || !user.isSystemAdmin) {
      window.location.href = '/admin/login.html';
      return;
    }
    AdminAPI.token = token;
    this.render(user);
  },

  render(user) {
    const pageContent = document.getElementById('pageContent');
    if (!pageContent) return;

    const wrapper = document.createElement('div');
    wrapper.className = 'admin-layout';

    // Sidebar
    const sidebar = document.createElement('aside');
    sidebar.className = 'admin-sidebar';
    sidebar.innerHTML = `
      <div class="admin-sidebar-header">
        <span class="admin-logo">Next Acc</span>
        <span class="admin-badge">ADMIN</span>
      </div>
      <nav class="admin-nav">
        ${this.navItems.map(item => {
          if (item.section) return `<div class="admin-nav-section">${item.section}</div>`;
          const active = item.id === this.currentPage ? ' active' : '';
          return `<a href="${item.href}" class="admin-nav-item${active}"><span class="icon">${item.icon}</span>${item.label}</a>`;
        }).join('')}
      </nav>
      <div class="admin-sidebar-footer">
        <div class="admin-user-info">
          <div style="font-weight:600;font-size:13px">${user.fullName}</div>
          <div style="font-size:11px;color:#94a3b8">${user.email}</div>
        </div>
        <button onclick="AdminLayout.logout()" class="admin-logout-btn">ออกจากระบบ</button>
      </div>
    `;

    // Header
    const header = document.createElement('header');
    header.className = 'admin-header';
    header.innerHTML = `
      <button class="admin-menu-btn" onclick="AdminLayout.toggleSidebar()">☰</button>
      <h1 class="admin-page-title">${document.title.split(' - ')[0]}</h1>
      <div style="flex:1"></div>
      <a href="/" target="_blank" class="admin-link-site">เปิดเว็บไซต์หลัก ↗</a>
    `;

    // Main
    const main = document.createElement('main');
    main.className = 'admin-main';
    main.innerHTML = pageContent.innerHTML;

    // Overlay for mobile sidebar
    const overlay = document.createElement('div');
    overlay.className = 'admin-sidebar-overlay';
    overlay.onclick = () => this.toggleSidebar();

    wrapper.appendChild(sidebar);
    wrapper.appendChild(overlay);
    const rightSide = document.createElement('div');
    rightSide.className = 'admin-right';
    rightSide.appendChild(header);
    rightSide.appendChild(main);
    wrapper.appendChild(rightSide);

    pageContent.innerHTML = '';
    pageContent.appendChild(wrapper);
  },

  toggleSidebar() {
    const sb = document.querySelector('.admin-sidebar');
    const ov = document.querySelector('.admin-sidebar-overlay');
    if (sb) sb.classList.toggle('open');
    if (ov) ov.classList.toggle('open');
  },

  logout() {
    localStorage.removeItem('admin_token');
    localStorage.removeItem('admin_user');
    window.location.href = '/admin/login.html';
  },

  // Helpers
  money(n) {
    if (n == null) return '฿0';
    return '฿' + Number(n).toLocaleString('th-TH', { minimumFractionDigits: 0, maximumFractionDigits: 2 });
  },
  date(d) {
    if (!d) return '-';
    return new Date(d).toLocaleDateString('th-TH', { year: 'numeric', month: 'short', day: 'numeric' });
  },
  datetime(d) {
    if (!d) return '-';
    return new Date(d).toLocaleDateString('th-TH', { year: 'numeric', month: 'short', day: 'numeric', hour: '2-digit', minute: '2-digit' });
  },
  statusBadge(status, type = 'default') {
    const colors = {
      Active: 'bg-green', Trial: 'bg-blue', Expired: 'bg-red', Cancelled: 'bg-gray',
      PastDue: 'bg-orange', Suspended: 'bg-red', Pending: 'bg-yellow', UnderReview: 'bg-blue',
      Approved: 'bg-green', Rejected: 'bg-red', Inactive: 'bg-gray', PendingVerification: 'bg-yellow'
    };
    const c = colors[status] || 'bg-gray';
    return `<span class="admin-badge-sm ${c}">${status}</span>`;
  },
  planBadge(plan) {
    const colors = { FreeTrial: 'bg-gray', Basic: 'bg-blue', Pro: 'bg-purple', Enterprise: 'bg-gold' };
    const labels = { FreeTrial: 'ทดลองใช้', Basic: 'Starter', Pro: 'Professional', Enterprise: 'Enterprise' };
    return `<span class="admin-badge-sm ${colors[plan] || 'bg-gray'}">${labels[plan] || plan}</span>`;
  },

  toast(msg, type = 'success') {
    const el = document.createElement('div');
    el.className = `admin-toast ${type}`;
    el.textContent = msg;
    document.body.appendChild(el);
    setTimeout(() => el.classList.add('show'), 10);
    setTimeout(() => { el.classList.remove('show'); setTimeout(() => el.remove(), 300); }, 3000);
  }
};
