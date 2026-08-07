// Admin Panel Layout
const AdminLayout = {
  currentPage: '',
  navItems: [
    { section: 'ภาพรวม' },
    { id: 'dashboard', label: 'แดชบอร์ด', icon: '📊', href: '/admin/index.html' },
    { section: 'จัดการลูกค้า' },
    { id: 'customers', label: 'ลูกค้า/บริษัท', icon: '🏢', href: '/admin/customers.html' },
    { id: 'chats', label: 'แชทลูกค้า', icon: '💬', href: '/admin/chats.html' },
    { id: 'chat-kb', label: 'คลังความรู้ Chatbot', icon: '📚', href: '/admin/chat-kb.html' },
    { id: 'users', label: 'ผู้ใช้งาน', icon: '👥', href: '/admin/users.html' },
    { id: 'account-subs', label: '🎫 Account Plans (User License)', icon: '🎫', href: '/admin/account-subscriptions.html' },
    { section: 'แพ็กเกจ' },
    { id: 'plans', label: 'จัดการแพ็กเกจ', icon: '💎', href: '/admin/plans.html' },
    { section: 'การเงิน' },
    { id: 'revenue', label: 'รายรับ/ธุรกิจ', icon: '📈', href: '/admin/revenue.html' },
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
    { id: 'ai-models', label: 'นโยบายโมเดล AI ต่อฟีเจอร์', icon: '🧠', href: '/admin/ai-models.html' },
    { section: 'ตรวจสอบระบบ' },
    { id: 'audit-log', label: 'บันทึกกิจกรรม (Audit)', icon: '📜', href: '/admin/audit-log.html' },
    { id: 'error-log', label: 'บันทึกข้อผิดพลาด', icon: '🐞', href: '/admin/error-log.html' },
    { id: 'background-jobs', label: 'งานเบื้องหลัง (Jobs)', icon: '🛠️', href: '/admin/background-jobs.html' },
  ],

  /// เรนเดอร์ sidebar+topbar ครอบ #pageContent. pageName = id ใน navItems
  /// (ใช้ไฮไลต์เมนูที่กำลังเปิด) — ส่งมาผิด/ไม่ส่ง เมนูยังขึ้นครบ แค่ไม่ไฮไลต์
  init(pageName) {
    if (this._rendered) return true;          // กันเรียกซ้ำจากหลายที่ในหน้าเดียว
    this.currentPage = pageName || this.inferPageId();
    const token = localStorage.getItem('admin_token');
    const user = JSON.parse(localStorage.getItem('admin_user') || 'null');
    if (!token || !user || !user.isSystemAdmin) {
      window.location.href = '/admin/login.html';
      return false;
    }
    // AdminAPI มาจาก admin-api.js ซึ่งต้องถูกโหลดก่อนไฟล์นี้. ถ้าหน้าไหนลืม
    // ใส่ <script src="admin-api.js"> การอ้างตรง ๆ จะ throw ReferenceError
    // ตรงนี้ แล้ว render() ข้างล่างไม่ถูกเรียก = "เมนูหาย" แบบเงียบ ๆ
    // (เคยเกิดกับ ai-models.html) — เช็คก่อนแล้วเตือนดัง ๆ แทนการพังเงียบ
    if (typeof AdminAPI === 'undefined') {
      console.error('[AdminLayout] admin-api.js ยังไม่ถูกโหลด — ต้องใส่ก่อน admin-layout.js');
    } else {
      AdminAPI.token = token;
    }
    this.render(user);
    this._rendered = true;
    return true;
  },

  /// เดา id เมนูจาก URL — ใช้เป็นค่าสำรองให้ auto-init ด้านล่าง
  inferPageId() {
    const file = (location.pathname.split('/').pop() || '').replace(/\.html$/, '');
    if (!file || file === 'index') return 'dashboard';
    const hit = this.navItems.find(n => n.href && n.href.endsWith(`/${file}.html`));
    return hit ? hit.id : file;
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

// ─────────────────────────────────────────────────────────────────
//  Safety net — หน้าไหนลืมเรียก AdminLayout.init() เมนูจะขึ้นเองอยู่ดี
//
//  เคสจริง: ai-config.html ไม่เคยเรียก init() เลย ทั้งที่โหลดไฟล์นี้แล้ว
//  ผลคือหน้าโหลดข้อมูลได้ปกติทุกอย่าง มีแค่ "เมนูหาย" ซึ่งไม่มี error ให้
//  เห็นทั้งใน console และ Network — ผู้ใช้เจอก่อนเราเสมอ
//
//  แทนที่จะไล่แก้ทีละหน้าแล้วรอพลาดอีก ให้ไฟล์นี้ self-heal: หลัง DOM พร้อม
//  ถ้ายังไม่มีใครเรียก init และหน้ามี #pageContent (= ตั้งใจใช้ layout นี้)
//  ก็ init ให้เอง โดยเดา id เมนูจาก URL. หน้าใหม่ที่เขียนต่อจากนี้จึงมีเมนู
//  ครบตั้งแต่แรกแม้ผู้เขียนจะลืม
//
//  **ต้องยิงให้เร็วที่สุด ห้ามหน่วง** — render() ทำ
//  `main.innerHTML = pageContent.innerHTML` ซึ่ง re-parse HTML ใหม่ทั้งก้อน
//  element เดิมถูกทิ้ง property ที่ตั้งด้วย JS (เช่น checkbox.checked ที่
//  loadSettings เพิ่งเซ็ต) จะหายไปด้วยเพราะเป็น DOM property ไม่ใช่ attribute
//  ถ้ายิงช้ากว่าที่หน้าโหลดข้อมูลเสร็จ ค่าที่โหลดมาจะถูกล้าง
//
//  DOMContentLoaded ปลอดภัยเพราะ: inline script ท้ายหน้าที่เรียก Page.init()
//  เองจะรัน **ก่อน** event นี้อยู่แล้ว → หน้าที่เขียนถูกต้องชนะเสมอ ตัวนี้
//  ทำงานเฉพาะหน้าที่ลืมจริง ๆ และ _rendered guard กันเรนเดอร์ซ้ำอีกชั้น
function __adminLayoutAutoInit() {
  if (AdminLayout._rendered) return;
  if (!document.getElementById('pageContent')) return;   // หน้า login/standalone
  console.warn('[AdminLayout] หน้านี้ไม่ได้เรียก AdminLayout.init() — เรนเดอร์เมนูให้อัตโนมัติ '
    + `(เดา id = "${AdminLayout.inferPageId()}") กรุณาเพิ่มการเรียกให้ถูกต้องในหน้านั้น`);
  AdminLayout.init();
}
if (document.readyState === 'loading') {
  document.addEventListener('DOMContentLoaded', __adminLayoutAutoInit);
} else {
  __adminLayoutAutoInit();
}
