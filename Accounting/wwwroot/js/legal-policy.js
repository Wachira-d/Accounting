/**
 * ตัวแสดงเอกสารทางกฎหมายของแพลตฟอร์ม (terms.html / privacy.html) — **resolver กลางตัวเดียว**
 *
 * รอบ 214 (คำตัดสินข้อ 173): เนื้อความทั้งฉบับมาจากเซิร์ฟเวอร์ (GET /api/legal/documents/{kind}) —
 * ฉบับที่ผู้ดูแลระบบเผยแพร่ที่ /admin/legal.html หรือฉบับตั้งต้นของระบบ · HTML ถูกแปลงและ encode ที่เซิร์ฟเวอร์แล้ว
 * (Helpers/LegalMarkdown — ข้อความทุกตัว HtmlEncode · ลิงก์รับเฉพาะ http(s)/mailto/tel/path ภายใน) หน้านี้แค่วาง
 * ชื่อ/ที่อยู่/อีเมลผู้ควบคุมข้อมูลถูกแทนในเนื้อความที่เซิร์ฟเวอร์แล้ว (SiteSettings.PlatformSeller* — ตัวตั้งเดียว)
 *
 * ?version=1.0 = ดูฉบับเก่า (ประวัติ) — แสดงป้าย "ฉบับเก่า" + ลิงก์กลับฉบับปัจจุบัน
 *
 * รอบ 232 (232-Checkout-1): **2 ภาษา** — `?lang=en` (จำใน localStorage `legal.lang`) · ฉบับ/วันมีผลเดียวกันทั้งสองภาษา (ภาษาเป็นแค่การแสดง) ·
 * ตัวตัดสินอยู่เซิร์ฟเวอร์ (Helpers/LegalDocumentLanguage): ขอ en แต่ยังไม่มี ⇒ ส่งไทยกลับมาพร้อม `languageNotice` — หน้านี้แสดงตามที่เซิร์ฟเวอร์บอก ไม่แต่ง ·
 * สวิตช์วาดลงที่ `[data-legal-lang]` (ถ้ามีในหน้า) · ค่าใน DTO: language · englishAvailable · languageNotice
 */
window.LegalPolicy = {
  data: null,
  LANG_KEY: 'legal.lang',

  /** ภาษาที่ผู้อ่านขอ: query ?lang ชนะ localStorage · ค่าอื่นนอกจาก en = th (เซิร์ฟเวอร์ normalize อีกชั้น) */
  requestedLang() {
    const q = new URLSearchParams(location.search).get('lang');
    if (q) return q === 'en' ? 'en' : 'th';
    try { return localStorage.getItem(this.LANG_KEY) === 'en' ? 'en' : 'th'; } catch (_) { return 'th'; }
  },

  /** กดสลับภาษา — จำไว้ + เขียน query (ลิงก์แชร์ได้ · กลับมาหน้านี้ภาษาเดิม) */
  setLang(lang) {
    try { localStorage.setItem(this.LANG_KEY, lang === 'en' ? 'en' : 'th'); } catch (_) { /* โหมดส่วนตัว — ใช้ query อย่างเดียว */ }
    const u = new URL(location.href);
    if (lang === 'en') u.searchParams.set('lang', 'en'); else u.searchParams.delete('lang');
    location.href = u.toString();
  },

  /** สวิตช์ TH/EN — ปุ่มของภาษาที่แสดงอยู่เป็นตัวหนา · ไม่มีฉบับอังกฤษก็ยังกดได้ (เซิร์ฟเวอร์ตอบไทย + ประกาศ) */
  renderLangSwitch(d) {
    const box = document.querySelector('[data-legal-lang]');
    if (!box) return;
    const cur = d.language === 'en' ? 'en' : 'th';
    const btn = (code, label) => `<button type="button" data-lang="${code}" style="border:1px solid var(--gray-300,#cbd5e1);background:${cur === code ? 'var(--primary,#4f46e5)' : '#fff'};color:${cur === code ? '#fff' : 'inherit'};border-radius:6px;padding:4px 10px;font-size:.85rem;cursor:pointer">${label}</button>`;
    box.innerHTML = `<span style="font-size:.85rem;color:var(--gray-500)">ภาษา / Language:</span> ${btn('th', 'ไทย')} ${btn('en', 'English')}`
      + (d.englishAvailable === false && cur === 'th' ? ' <span style="font-size:.8rem;color:var(--gray-500)">(English version not available yet)</span>' : '');
    box.querySelectorAll('button[data-lang]').forEach(b => b.addEventListener('click', () => this.setLang(b.dataset.lang)));
  },

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

  /** โหลดเอกสาร kind = 'privacy' | 'terms' | 'refund' (รอบ 219 ส่วน B) แล้ววางลง #legalBody + เติมหัว/ท้ายเอกสาร */
  async loadDocument(kind) {
    const params = new URLSearchParams(location.search);
    const version = params.get('version');
    const body = document.getElementById('legalBody');
    let d = null;
    try {
      const qs = new URLSearchParams();
      if (version) qs.set('version', version);
      if (this.requestedLang() === 'en') qs.set('lang', 'en');
      const url = `/api/legal/documents/${encodeURIComponent(kind)}` + (qs.toString() ? `?${qs}` : '');
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
    // รอบ 232: หัวเอกสาร/ชื่อแท็บตามภาษาที่แสดงจริง (เซิร์ฟเวอร์ส่ง title ตามภาษาแล้ว) + สวิตช์ + ประกาศ "ยังไม่มีฉบับอังกฤษ"
    document.querySelectorAll('[data-legal="title"]').forEach(el => { el.textContent = d.title || el.textContent; });
    if (d.title) document.title = `${d.title} | NextAcc`;
    document.documentElement.lang = d.language === 'en' ? 'en' : 'th';
    this.renderLangSwitch(d);
    const langNote = document.getElementById('legalLangNotice');
    if (langNote) {
      langNote.textContent = d.languageNotice || '';
      langNote.style.display = d.languageNotice ? 'block' : 'none';
    }
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
        const langQ = this.requestedLang() === 'en' ? '&lang=en' : '';
        return h.version === shownVersion ? `<span>${label}</span>` : `<a href="?version=${encodeURIComponent(h.version)}${langQ}">${label}</a>`;
      }).join(' · ');
      box.style.display = 'block';
    } catch (_) { box.style.display = 'none'; /* ประวัติเป็นส่วนเสริม — เนื้อความหลักแสดงแล้ว */ }
  },
};
