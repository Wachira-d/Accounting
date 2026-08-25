/**
 * ตัวเติมข้อมูล "ผู้ควบคุมข้อมูลส่วนบุคคล" + เวอร์ชันนโยบาย บนหน้าเอกสาร
 * ทางกฎหมาย (terms.html / privacy.html) — **resolver กลางตัวเดียว**
 *
 * ทำไมต้องรวมไว้ที่เดียว: สองหน้านี้ต้องแสดงชื่อ/ที่อยู่/เลขผู้เสียภาษี/อีเมล
 * ของผู้ให้บริการชุดเดียวกันเป๊ะ ถ้าต่างหน้าต่างเขียนโค้ดเติมเอง วันหนึ่งจะ
 * drift (defect class "สอง renderer ห้าม drift" ใน CLAUDE.md กฎเหล็ก #4 A)
 *
 * แหล่งข้อมูล: GET /api/legal/policy ← SiteSettings.PlatformSeller* ที่แอดมิน
 * กรอกไว้ที่ /admin/site-settings.html แท็บ "ใบเสร็จค่าบริการ"
 */
window.LegalPolicy = {
  data: null,

  /** พ.ศ. + ชื่อเดือนไทย — แบบยื่น/เอกสารทางการใช้ พ.ศ. (กฎโปรเจกต์) */
  thaiDate(iso) {
    if (!iso) return '-';
    const months = ['มกราคม','กุมภาพันธ์','มีนาคม','เมษายน','พฤษภาคม','มิถุนายน',
                    'กรกฎาคม','สิงหาคม','กันยายน','ตุลาคม','พฤศจิกายน','ธันวาคม'];
    const [y, m, d] = iso.split('-').map(Number);
    if (!y || !m || !d) return iso;
    return `${d} ${months[m - 1]} ${y + 543}`;
  },

  esc(s) {
    return String(s ?? '').replace(/[&<>"']/g, c =>
      ({ '&': '&amp;', '<': '&lt;', '>': '&gt;', '"': '&quot;', "'": '&#39;' }[c]));
  },

  /** เติมทุกจุดบนหน้า: [data-legal="version|effectiveDate|controllerName|…"] */
  async load() {
    let p = null;
    try {
      const r = await fetch('/api/legal/policy');
      const j = await r.json();
      p = j.data || j;
    } catch (e) { /* ออฟไลน์/เซิร์ฟเวอร์ล่ม — ยังต้องอ่านเนื้อนโยบายได้ */ }
    this.data = p;

    const put = (key, text) => document.querySelectorAll(`[data-legal="${key}"]`)
      .forEach(el => el.textContent = text);

    put('version', p?.version || '-');
    put('effectiveDate', this.thaiDate(p?.effectiveDate));
    put('controllerName', p?.controllerName || 'ผู้ให้บริการ (ยังไม่ได้ตั้งค่า)');
    put('controllerTaxId', p?.controllerTaxId || '-');
    put('controllerAddress', p?.controllerAddress || '-');
    put('controllerPhone', p?.controllerPhone || '-');
    put('privacyEmail', p?.privacyContactEmail || '-');

    // อีเมลติดต่อ — ทำเป็นลิงก์ mailto ให้กดได้จริง
    document.querySelectorAll('[data-legal-mailto]').forEach(el => {
      if (p?.privacyContactEmail) {
        el.innerHTML = `<a href="mailto:${this.esc(p.privacyContactEmail)}">${this.esc(p.privacyContactEmail)}</a>`;
      } else {
        el.textContent = 'ยังไม่ได้ตั้งค่าอีเมลติดต่อ';
      }
    });

    // ⚠️ แอดมินยังไม่ได้กรอกตัวตนผู้ควบคุมข้อมูล = นโยบายฉบับนี้ยัง "ไม่แจ้ง
    // ผู้ควบคุมข้อมูล" ตาม ม.23(1) — ต้องเห็นชัด ไม่ใช่ปล่อยขีดกลางเงียบ ๆ
    const warn = document.getElementById('legalWarn');
    if (warn) warn.style.display = p && p.controllerConfigured ? 'none' : 'block';
    return p;
  },
};
