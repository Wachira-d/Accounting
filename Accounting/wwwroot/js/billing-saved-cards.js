/**
 * รอบ 230 (PLATFORM_OMISE_AUDIT §5.2) — การ์ด "บัตรที่บันทึกไว้ · ตัดบัตรอัตโนมัติ" ในศูนย์บิล (/pages/billing.html)
 *
 * ทุกค่าที่แสดง (ป้ายบัตร · บัตรใช้ได้ไหม/เพราะอะไร · ตัดอัตโนมัติได้ไหม/เพราะอะไร · ข้อความยินยอม + ฉบับ · ผลการตัดล่าสุด)
 * มาจาก GET …/billing-center/saved-cards — หน้านี้แสดงอย่างเดียว ไม่ตัดสินเอง (F2 ข้อ 5)
 * ไม่มีช่องกรอกบัตรที่นี่: บันทึกบัตรใหม่ทำตอนชำระด้วยบัตร (ติ๊ก "บันทึกบัตรเพื่อตัดอัตโนมัติ" ในหน้าต่างชำระ) — เลขบัตรไม่ผ่านเซิร์ฟเวอร์เรา
 */
(function (global) {
  const SavedCards = {
    data: null,
    cid() { return Layout.getCompanyId(); },
    base() { return `/api/subscription/${this.cid()}/billing-center/saved-cards`; },
    box() { return document.getElementById('savedCards'); },

    async load() {
      const box = this.box();
      if (!box) return;
      try {
        const res = await API.get(this.base());
        this.data = res.data;
        this.render();
      } catch (e) {
        box.innerHTML = `<div class="note bad">${Layout.esc(e.message || 'โหลดบัตรที่บันทึกไว้ไม่สำเร็จ')}</div>`;
      }
    },

    render() {
      const d = this.data, e = Layout.esc, box = this.box();
      if (!d || !box) return;
      const cards = d.cards || [];
      const cardRows = cards.length === 0
        ? '<div class="muted">ยังไม่มีบัตรที่บันทึกไว้ — ชำระใบแจ้งหนี้ด้วยบัตรแล้วติ๊ก "บันทึกบัตรเพื่อตัดอัตโนมัติ"</div>'
        : `<div class="table-container"><table class="table"><thead><tr><th>บัตร</th><th>สถานะ</th><th></th></tr></thead><tbody>
            ${cards.map(c => `<tr>
              <td class="font-medium">${e(c.label)} ${c.isDefault ? '<span class="pill Paid">ตั้งต้น</span>' : ''} ${c.isTestMode ? '<span class="pill DueToday">ทดสอบ</span>' : ''}</td>
              <td>${c.problem ? `<span class="muted">${e(c.problem)}</span>` : '<span class="pill Paid">ใช้ตัดอัตโนมัติได้</span>'}</td>
              <td style="white-space:nowrap">
                ${c.isDefault ? '' : `<button class="btn btn-secondary btn-sm" data-sc-default="${Layout.esc(c.id)}">ตั้งเป็นบัตรตั้งต้น</button>`}
                <button class="btn btn-secondary btn-sm" data-sc-remove="${Layout.esc(c.id)}" data-sc-label="${Layout.esc(c.label)}">ลบ</button>
              </td></tr>`).join('')}
          </tbody></table></div>`;

      const attempts = d.recentAttempts || [];
      const attemptRows = attempts.length === 0 ? '' : `<div style="margin-top:12px"><div class="muted" style="margin-bottom:4px">การตัดอัตโนมัติล่าสุด</div>
          <div class="table-container"><table class="table"><thead><tr><th>วันที่</th><th>ใบแจ้งหนี้</th><th>ครั้งที่</th><th class="text-right">ยอด</th><th>ผล</th></tr></thead><tbody>
          ${attempts.map(a => `<tr><td>${Layout.date(a.attemptedAt)}</td><td>${e(a.documentNumber || '-')}</td><td>${e(a.attemptNo)}</td>
            <td class="text-right">฿${Layout.money(a.amount)}</td><td>${e(a.outcomeLabel)}${a.detail ? `<div class="muted">${e(a.detail)}</div>` : ''}</td></tr>`).join('')}
          </tbody></table></div></div>`;

      const status = d.autoChargeBlockReason
        ? `<div class="note warn">ตัดบัตรอัตโนมัติ: ไม่ทำงาน — ${e(d.autoChargeBlockReason)}</div>`
        : '<div class="note">ตัดบัตรอัตโนมัติ: <b>ทำงานอยู่</b> — ระบบตัดบัตรตั้งต้นเมื่อใบแจ้งหนี้ถึงกำหนด (ลองใหม่ตามรอบทวงหนี้ถ้าไม่สำเร็จ)</div>';
      // ฝ่ายค้านรอบ 230: ยินยอม (ฉบับที่แสดง) + นิติบุคคลรับทราบภาษีหัก ณ ที่จ่ายแยก (S1) · เปิดได้เมื่อมีบัตรยืนยันแล้ว (S3) · ปิด = เสนอลบบัตรด้วย (S6)
      const consentBoxes = `<label style="display:flex;gap:8px;align-items:flex-start;font-size:12px;color:#334155;line-height:1.6;margin-top:8px">
             <input type="checkbox" id="scConsent" style="margin-top:3px"><span>${e(d.consentText)}</span></label>
           ${d.whtWarning ? `<label style="display:flex;gap:8px;align-items:flex-start;font-size:12px;color:#92400e;line-height:1.6;margin-top:6px">
             <input type="checkbox" id="scWht" style="margin-top:3px"><span>${e(d.whtWarning)}</span></label>` : ''}`;
      const hasVerified = cards.some(c => c.verified);
      const toggle = d.mandateEnabled
        ? `<label style="display:flex;gap:6px;align-items:center;font-size:12px;color:#334155"><input type="checkbox" id="scRemoveCards" checked> ลบบัตรที่บันทึกไว้ด้วยเมื่อปิด</label>
           <button class="btn btn-secondary btn-sm" data-sc-off="1">ปิดตัดบัตรอัตโนมัติ</button>`
        : (d.available && hasVerified
          ? `${consentBoxes}<button class="btn btn-primary btn-sm" style="margin-top:6px" data-sc-on="1">เปิดตัดบัตรอัตโนมัติ</button>`
          : (d.pendingActivation ? '<div class="muted">รอการชำระครั้งแรกด้วยบัตรที่บันทึกสำเร็จ — ระบบเปิดตัดอัตโนมัติให้เอง (บัตรที่ชำระไม่ผ่าน: ลบแล้วบันทึกใหม่ตอนชำระครั้งถัดไป)</div>' : ''));
      const reEnable = d.mandateEnabled && d.available && hasVerified && (d.needsReconsent || (d.whtWarning && !d.whtAcknowledged))
        ? `${consentBoxes}<button class="btn btn-primary btn-sm" style="margin-top:6px" data-sc-on="1">ยืนยันความยินยอมฉบับปัจจุบัน</button>` : '';

      box.innerHTML = `${d.available ? '' : `<div class="note warn" style="margin-bottom:8px">${e(d.unavailableReason || '')}</div>`}
        ${status}
        <div style="margin-top:10px">${cardRows}</div>
        <div style="margin-top:10px;display:flex;flex-direction:column;align-items:flex-start;gap:4px">${toggle}${reEnable}</div>
        ${attemptRows}
        <div class="muted" style="margin-top:8px">NextAcc ไม่เก็บเลขบัตร — เก็บเฉพาะเลขอ้างอิงของผู้ให้บริการรับชำระเงิน ยี่ห้อ 4 ตัวท้าย และเดือน/ปีหมดอายุ</div>`;
    },

    async act(fn, okMsg) {
      try {
        const res = await fn();
        this.data = res.data;
        this.render();
        Layout.toast(res.message || okMsg, 'success');
      } catch (err) { Layout.toast(err.message || 'ทำรายการไม่สำเร็จ', 'error'); }
    },

    onClick(ev) {
      const t = ev.target.closest('button');
      if (!t) return;
      if (t.dataset.scDefault) this.act(() => API.post(`${this.base()}/${encodeURIComponent(t.dataset.scDefault)}/default`, {}), 'ตั้งเป็นบัตรตั้งต้นแล้ว');
      else if (t.dataset.scRemove) {
        if (!confirm(`ลบบัตร ${t.dataset.scLabel}? ระบบจะไม่ใช้บัตรนี้ตัดอัตโนมัติอีก (ลบบัตรใบสุดท้าย = ปิดตัดอัตโนมัติ)`)) return;
        this.act(() => API.del(`${this.base()}/${encodeURIComponent(t.dataset.scRemove)}`), 'ลบบัตรแล้ว');
      } else if (t.dataset.scOff) {
        const rm = document.getElementById('scRemoveCards');
        const removeCards = !!(rm && rm.checked);
        if (!confirm('ปิดตัดบัตรอัตโนมัติ? ใบแจ้งหนี้ถัดไปต้องชำระเองที่ศูนย์บิล' + (removeCards ? ' · บัตรที่บันทึกไว้จะถูกลบ' : ''))) return;
        this.act(() => API.post(`${this.base()}/auto-charge`, { enabled: false, removeCards }), 'ปิดตัดบัตรอัตโนมัติแล้ว');
      } else if (t.dataset.scOn) {
        const box = document.getElementById('scConsent');
        if (!box || !box.checked) { Layout.toast('กรุณาติ๊กยินยอมก่อนเปิดตัดบัตรอัตโนมัติ', 'error'); return; }
        const wht = document.getElementById('scWht');
        if (this.data && this.data.whtWarning && !(wht && wht.checked)) { Layout.toast('กรุณาติ๊กรับทราบเรื่องภาษีหัก ณ ที่จ่ายก่อน', 'error'); return; }
        this.act(() => API.post(`${this.base()}/auto-charge`,
          { enabled: true, consentAccepted: true, consentVersionShown: this.data ? this.data.consentVersion : null,
            whtAcknowledged: !!(wht && wht.checked) }), 'เปิดตัดบัตรอัตโนมัติแล้ว');
      }
    },

    async init() {
      const box = this.box();
      if (!box) return;
      box.addEventListener('click', (ev) => this.onClick(ev));
      // รอ layout ตั้งบริษัทปัจจุบันก่อน (แบบเดียวกับการ์ดอื่นของศูนย์บิล)
      for (let i = 0; i < 50 && !(global.Layout && Layout.getCompanyId && Layout.getCompanyId()); i++) await new Promise(r => setTimeout(r, 100));
      this.load();
    },
  };
  global.SavedCards = SavedCards;
  if (document.readyState === 'loading') document.addEventListener('DOMContentLoaded', () => SavedCards.init());
  else SavedCards.init();
})(window);
