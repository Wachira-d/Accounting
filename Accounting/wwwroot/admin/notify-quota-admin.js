// รอบ 213 (คำตัดสินข้อ 172) — เพดานข้อความช่องกลาง "เฉพาะบริษัท" + ยอดใช้/โบนัส (อ่านอย่างเดียว) บนหน้าแอดมิน
// ใช้ที่ /admin/customers.html (รายละเอียดบริษัท) และ /admin/account-subscriptions.html (บริษัทที่ผูก)
// เซิร์ฟเวอร์ประกอบบรรทัดสรุปเอง (NotifyQuotaPolicy.AdminSummary · GET /api/admin/companies/{id}/notify-quota) — ที่นี่แสดง + ส่งค่าที่แอดมินพิมพ์เท่านั้น
// ข้อความจากเซิร์ฟเวอร์ผ่าน AdminLayout.esc ทุกครั้ง · ปุ่มผูกด้วย addEventListener (ไม่ต่อสตริง onclick)
const NotifyQuotaAdmin = {
  esc(v) { return AdminLayout.esc(v == null ? '' : String(v)); },

  async mount(el, companyId) {
    if (!el || !companyId) return;
    el.innerHTML = '<span style="color:#94a3b8;font-size:12px">กำลังโหลด…</span>';
    try {
      const r = await AdminAPI.get(`/companies/${encodeURIComponent(companyId)}/notify-quota`);
      this.render(el, companyId, r.data);
    } catch (e) {
      el.innerHTML = `<span style="color:#dc2626;font-size:12px">โหลดโควตาข้อความไม่สำเร็จ: ${this.esc(e.message)}</span>`;
    }
  },

  render(el, companyId, d) {
    const cur = d.maxSharedMessagesPerMonth;
    el.innerHTML = `<div style="font-size:12px;color:#334155">💬 ${this.esc(d.summary)}</div>
      <button type="button" class="admin-btn-sm nqa-edit" style="margin-top:4px">ตั้งเพดานข้อความเฉพาะบริษัท</button>`;
    el.querySelector('.nqa-edit').addEventListener('click', () => this.edit(el, companyId, cur));
  },

  async edit(el, companyId, cur) {
    const raw = prompt('เพดานข้อความผ่านช่องกลาง/เดือน ของบริษัทนี้\n'
      + '• เว้นว่าง = ตามแพ็กเกจ\n• -1 = ไม่จำกัด\n• ตัวเลข = เพดาน (ตัวนับเดือนนี้ไม่ถูกรีเซ็ต)', cur == null ? '' : String(cur));
    if (raw === null) return;
    const text = raw.trim();
    const body = text === '' ? { clear: true } : { maxSharedMessagesPerMonth: Number(text) };
    if (text !== '' && !Number.isInteger(body.maxSharedMessagesPerMonth)) { alert('กรุณาใส่จำนวนเต็ม'); return; }
    const notes = prompt('หมายเหตุ (บันทึกในประวัติ — ไม่บังคับ)', '');
    if (notes) body.notes = notes;
    try {
      const r = await AdminAPI.put(`/companies/${encodeURIComponent(companyId)}/notify-quota`, body);
      this.render(el, companyId, r.data);
      if (AdminLayout.toast) AdminLayout.toast(r.message || 'บันทึกแล้ว', 'success');
    } catch (e) {
      alert(e.message || 'บันทึกไม่สำเร็จ');
    }
  },
};
