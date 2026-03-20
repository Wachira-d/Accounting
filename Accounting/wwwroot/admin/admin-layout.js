// Admin Panel Layout
const AdminLayout = {
  currentPage: '',
  navItems: [
    { section: 'ภาพรวม' },
    { id: 'dashboard', label: 'แดชบอร์ด', icon: '📊', href: '/admin/index.html' },
    { section: 'จัดการลูกค้า' },
    { id: 'customers', label: 'ลูกค้า/บริษัท', icon: '🏢', href: '/admin/customers.html' },
    { id: 'users', label: 'ผู้ใช้งาน', icon: '👥', href: '/admin/users.html' },
    { section: 'แพ็กเกจ' },
    { id: 'plans', label: 'จัดการแพ็กเกจ', icon: '💎', href: '/admin/plans.html' },
    { section: 'การเงิน' },
    { id: 'payments', label: 'ตรวจสอบการชำระ', icon: '💳', href: '/admin/payments.html' },
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
        <span class="admin-logo">AcctPlatform</span>
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
      <button class="admin-menu-btn" onclick="document.querySelector('.admin-sidebar').classList.toggle('open')">☰</button>
      <h1 class="admin-page-title">${document.title.split(' - ')[0]}</h1>
      <div style="flex:1"></div>
      <a href="/" target="_blank" class="admin-link-site">เปิดเว็บไซต์หลัก ↗</a>
    `;

    // Main
    const main = document.createElement('main');
    main.className = 'admin-main';
    main.innerHTML = pageContent.innerHTML;

    wrapper.appendChild(sidebar);
    const rightSide = document.createElement('div');
    rightSide.className = 'admin-right';
    rightSide.appendChild(header);
    rightSide.appendChild(main);
    wrapper.appendChild(rightSide);

    pageContent.innerHTML = '';
    pageContent.appendChild(wrapper);
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
