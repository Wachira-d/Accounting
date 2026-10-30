/**
 * ตัวแสดงเอกสารทางกฎหมายของแพลตฟอร์ม (terms.html / privacy.html) — **resolver กลางตัวเดียว**
 *
 * รอบ 214 (คำตัดสินข้อ 173): เนื้อความทั้งฉบับมาจากเซิร์ฟเวอร์ (GET /api/legal/documents/{kind}) —
 * ฉบับที่ผู้ดูแลระบบเผยแพร่ที่ /admin/legal.html หรือฉบับตั้งต้นของระบบ · HTML ถูกแปลงและ encode ที่เซิร์ฟเวอร์แล้ว
 * (Helpers/LegalMarkdown — ข้อความทุกตัว HtmlEncode · ลิงก์รับเฉพาะ http(s)/mailto/tel/path ภายใน) หน้านี้แค่วาง
 * ชื่อ/ที่อยู่/อีเมลผู้ควบคุมข้อมูลถูกแทนในเนื้อความที่เซิร์ฟเวอร์แล้ว (SiteSettings.PlatformSeller* — ตัวตั้งเดียว)
 *
 * ?version=1.0 = ดูฉบับเก่า (ประวัติ) — แสดงป้าย "ฉบับเก่า" + ลิงก์กลับฉบับปัจจุบัน
 */
window.LegalPolicy = {
  data: null,

  /** พ.ศ. + ชื่อเดือนไทย — แบบยื่น/เอกสารทางการใช้ พ.ศ. (กฎโปรเจกต์) */
  thaiDate(iso) {
    if (!iso) return '-';
    const months = ['มกราคม','กุมภาพันธ์','มีนาคม','เมษายน','พฤษภาคม','มิถุนายน',
                    'กรกฎาคม','สิงหาคม','กันยายน','ตุลาคม','พฤศจิกายน','ธันวาคม'];
    const [y, m, d] = String(iso).split('-').map(Number);
    if (!y || !m || !d) return iso;
    return `${d} ${months[m - 1]} ${y + 543}`;
  },

  esc(s) {
    return String(s ?? '').replace(/[&<>"']/g, c =>
      ({ '&': '&amp;', '<': '&lt;', '>': '&gt;', '"': '&quot;', "'": '&#39;' }[c]));
  },

  /** โหลดเอกสาร kind = 'privacy' | 'terms' แล้ววางลง #legalBody + เติมหัว/ท้ายเอกสาร */
  async loadDocument(kind) {
    const params = new URLSearchParams(location.search);
    const version = params.get('version');
    const body = document.getElementById('legalBody');
    let d = null;
    try {
      const url = `/api/legal/documents/${encodeURIComponent(kind)}` + (version ? `?version=${encodeURIComponent(version)}` : '');
      const r = await fetch(url);
      const j = await r.json().catch(() => null);
      if (!r.ok) throw new Error((j && j.message) || 'โหลดเอกสารไม่สำเร็จ');
      d = j.data || j;
    } catch (e) {
      // ล้มดัง (F2 ข้อ 7): บอกตรง ๆ ว่าโหลดไม่ได้ — ไม่ปล่อยหน้าว่างที่ดูเหมือนเอกสารไม่มีเนื้อหา
      if (body) body.innerHTML = `<div class="legal-warn">โหลดเนื้อความไม่สำเร็จ (${this.esc(e.message)}) — กรุณาลองใหม่อีกครั้ง</div>`;
      return null;
    }
    this.data = d;

    const put = (key, text) => document.querySelectorAll(`[data-legal="${key}"]`).forEach(el => { el.textContent = text; });
    put('version', d.version || '-');
    put('effectiveDate', this.thaiDate(d.effectiveDate));
    // เซิร์ฟเวอร์แปลง + encode แล้ว (LegalMarkdown) — วางเป็น HTML ได้ ข้อความผู้ใช้ไม่มีทางเป็นแท็ก
    if (body) body.innerHTML = d.html || '<p>-</p>';

    const warn = document.getElementById('legalWarn');
    if (warn) warn.style.display = d.controllerConfigured ? 'none' : 'block';

    const notice = document.getElementById('legalNotice');
    if (notice) {
      if (d.isUpcoming) {
        // ฝ่ายค้านรอบ 214 S2: ฉบับที่ประกาศล่วงหน้า ≠ ฉบับเก่า
        notice.innerHTML = `ท่านกำลังดู<b>ฉบับที่จะมีผลตั้งแต่ ${this.esc(this.thaiDate(d.effectiveDate))}</b> (ฉบับที่ ${this.esc(d.version)}) — <a href="${this.esc(location.pathname)}">ดูฉบับที่ใช้อยู่ปัจจุบัน</a>`;
        notice.style.display = 'block';
      } else if (d.isArchived) {
        notice.innerHTML = `ท่านกำลังดู<b>ฉบับเก่า</b> (ฉบับที่ ${this.esc(d.version)}) — <a href="${this.esc(location.pathname)}">ดูฉบับปัจจุบัน</a>`;
        notice.style.display = 'block';
      } else if (d.upcoming) {
        notice.innerHTML = `📣 ประกาศ: ฉบับที่ ${this.esc(d.upcoming.version)} จะมีผลตั้งแต่ <b>${this.esc(this.thaiDate(d.upcoming.effectiveDate))}</b>`
          + (d.upcoming.changeSummary ? ` — ${this.esc(d.upcoming.changeSummary)}` : '')
          + ` · <a href="?version=${encodeURIComponent(d.upcoming.version)}">อ่านฉบับใหม่</a>`;
        notice.style.display = 'block';
      } else {
        notice.style.display = 'none';
      }
    }
    this.loadHistory(kind, d.version);
    return d;
  },

  /** ประวัติฉบับ (สาธารณะ) — ซ่อนเมื่อมีฉบับเดียว */
  async loadHistory(kind, shownVersion) {
    const box = document.getElementById('legalHistory');
    if (!box) return;
    try {
      const r = await fetch(`/api/legal/documents/${encodeURIComponent(kind)}/history`);
      const j = await r.json();
      const rows = (j && j.data) || [];
      if (rows.length < 2) { box.style.display = 'none'; return; }
      box.innerHTML = '<b>ประวัติฉบับ:</b> ' + rows.map(h => {
        const label = `ฉบับที่ ${this.esc(h.version)} (มีผล ${this.esc(this.thaiDate(h.effectiveDate))})`
          + (h.isCurrent ? ' · ปัจจุบัน' : h.isUpcoming ? ' · ประกาศล่วงหน้า' : '');
        return h.version === shownVersion ? `<span>${label}</span>` : `<a href="?version=${encodeURIComponent(h.version)}">${label}</a>`;
      }).join(' · ');
      box.style.display = 'block';
    } catch (_) { box.style.display = 'none'; /* ประวัติเป็นส่วนเสริม — เนื้อความหลักแสดงแล้ว */ }
  },
};
