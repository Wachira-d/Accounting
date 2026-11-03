// รอบ 232 (แก้หลังผู้ใช้รายงาน): หน้า "โควตาเสริม · ราคาส่วนเกิน" และ "ส่วนเสริมและราคา" อยู่ใน hub แพ็กเกจ & บิลลิ่ง ของแอดมิน
// แต่เดิมเป็นหน้า /pages/* ที่ใช้เปลือกผู้เช่า (js/layout.js + js/api.js อ่าน localStorage 'token') — แอดมินที่ล็อกอินพอร์ทัลแอดมิน
// มีแค่ 'admin_token' ⇒ กดแท็บแล้วถูกพาไปหน้า login ของลูกค้าปกติ · ย้ายสองหน้ามาอยู่ /admin/ ใต้เปลือกแอดมินแล้ว
// ไฟล์นี้ (โหลดหลัง admin-api.js + admin-layout.js) ให้โค้ดหน้าเดิมที่เรียก API.* / Layout.* ทำงานบนเปลือกแอดมิน
// โดยไม่ต้องเขียนหน้าใหม่ทั้งหน้า — ทุกคำขอไปทาง AdminAPI (admin_token · 401 ⇒ /admin/login.html) · ใช้เฉพาะหน้าที่ย้ายมาเท่านั้น
'use strict';
(function (global) {
  if (typeof AdminAPI === 'undefined' || typeof AdminLayout === 'undefined') {
    console.error('[admin-tenant-compat] ต้องโหลดหลัง admin-api.js และ admin-layout.js');
    return;
  }

  const API = {
    get: (p) => AdminAPI.get(p),
    post: (p, b) => AdminAPI.post(p, b),
    put: (p, b) => AdminAPI.put(p, b),
    delete: (p) => AdminAPI.del(p),
    getPlans: () => AdminAPI.get('/api/subscription/plans'),   // [AllowAnonymous] — ใช้ได้กับโทเคนแอดมิน
    /** คืน { blob, name } แบบเดียวกับ js/api.js (หน้าเดิมเปิด blob เอง) */
    async fetchFile(url, fallbackName) {
      const token = localStorage.getItem('admin_token');
      const res = await fetch(url, { headers: token ? { Authorization: `Bearer ${token}` } : {}, cache: 'no-store' });
      if (res.status === 401) { global.location.href = '/admin/login.html'; throw new Error('Unauthorized'); }
      if (!res.ok) {
        let msg = `ดาวน์โหลดไม่สำเร็จ (HTTP ${res.status})`;
        try { const j = await res.json(); if (j && j.message) msg = j.message; } catch (_) { /* ไม่ใช่ JSON */ }
        throw new Error(msg);
      }
      return { blob: await res.blob(), name: fallbackName || 'download' };
    },
  };

  const Layout = {
    init: (pageId) => AdminLayout.init(pageId),
    /** AdminLayout.init บังคับ SystemAdmin แล้ว (ไม่ใช่ ⇒ /admin/login.html) — ตรงนี้คืน true เสมอ */
    requirePlatformAdmin: async () => true,
    setTitle(title) {
      document.title = `${title} - Admin Next Acc`;
      const h = document.querySelector('.admin-page-title');
      if (h) h.textContent = title;
    },
    esc: (s) => AdminLayout.esc(s),
    jsArg: (v) => AdminLayout.jsArg(v),
    date: (d) => AdminLayout.date(d),
    toast: (msg, type) => AdminLayout.toast(msg, type),
    openModal: (id) => { const el = document.getElementById(id); if (el) el.classList.add('active'); },
    closeModal: (id) => { const el = document.getElementById(id); if (el) el.classList.remove('active'); },
  };

  global.API = API;
  global.Layout = Layout;
})(window);
