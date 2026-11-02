/* รอบ 228 (erp-review/2026-11-02/CMS_SETTINGS_UX_AUDIT.md แพ็ก A · §1 ข้อ 1–2) — การ์ด "💰 บัญชีรับโอน / พร้อมเพย์" ของทุกเว็บ
 * - ที่เดียวของเลขบัญชี · ธนาคาร (dropdown จากเซิร์ฟเวอร์ — ค่าเดิมนอกรายการคงไว้) · สาขา · พร้อมเพย์ · รูป QR ที่ลูกค้าเห็น
 *   ใช้ร่วม คำสั่งซื้อ · การจอง · ที่พัก (เดิมเว็บที่พักไม่มีที่ตั้ง · modal หน้าคำสั่งซื้อล้างรูป QR เงียบ ๆ)
 * - GET/PUT …/cms/sites/{id}/booking/transfer-account · ล้างช่องที่มีค่า ⇒ เซิร์ฟเวอร์ 409 ถามยืนยัน
 * ค่าตรวจ/ป้ายทั้งหมดจากเซิร์ฟเวอร์ (Helpers/SiteTransferAccount · BookingPaymentChannels) — ไฟล์นี้แสดง/ส่งค่าอย่างเดียว */
(function () {
  const esc = (v) => (window.Layout && Layout.esc ? Layout.esc(v) : String(v == null ? '' : v));
  const $ = (id) => document.getElementById(id);
  const txt = (id) => { const el = $(id); const t = el ? el.value.trim() : ''; return t === '' ? null : t; };
  const url = (siteId) => `/api/companies/${Layout.getCompanyId()}/cms/sites/${siteId}/booking/transfer-account`;
  const OTHER = '__other__';

  window.SiteTransferCard = {
    _siteId: null,
    async mount(el, ctx) {
      this._siteId = ctx && ctx.siteId;
      if (!el || !this._siteId) return;
      const badge = ctx && typeof ctx.scopeBadge === 'function' ? ctx.scopeBadge('site') : '';
      el.innerHTML = `<div class="sg-card" id="stfCard"><h3 style="font-size:16px;font-weight:600;margin:0 0 6px">💰 บัญชีรับโอน / พร้อมเพย์ ${badge}</h3>
        <div class="sg-hint" style="margin-bottom:8px">เลขบัญชีและ QR ที่ลูกค้าเห็นเพื่อโอนแล้วแนบสลิป — <b>ที่เดียวของเว็บนี้</b> ใช้ร่วมคำสั่งซื้อ การจอง และที่พัก <span id="stfUsedBy"></span></div>
        <div id="stfProblem"></div>
        <div class="form-row-3">
          <div class="form-group"><label class="form-label">ธนาคาร</label>
            <select class="form-input" id="stfBank" onchange="SiteTransferCard.onBankChange()"></select>
            <input type="text" class="form-input" id="stfBankOther" maxlength="128" placeholder="พิมพ์ชื่อธนาคาร" style="display:none;margin-top:6px"></div>
          <div class="form-group"><label class="form-label">เลขบัญชี</label><input type="text" class="form-input" id="stfAcct" maxlength="30" inputmode="numeric" placeholder="xxx-x-xxxxx-x"></div>
          <div class="form-group"><label class="form-label">ชื่อบัญชี</label><input type="text" class="form-input" id="stfAcctName" maxlength="200"></div>
        </div>
        <div class="form-row-3">
          <div class="form-group"><label class="form-label">สาขา (ถ้ามี)</label><input type="text" class="form-input" id="stfBranch" maxlength="128"></div>
          <div class="form-group"><label class="form-label">พร้อมเพย์ (เบอร์มือถือ / เลขผู้เสียภาษี)</label><input type="text" class="form-input" id="stfPromptPay" maxlength="20" inputmode="numeric">
            <div class="sg-hint">ใส่แล้วระบบสร้าง QR ที่มียอดเงินให้ลูกค้าอัตโนมัติ</div></div>
          <div class="form-group"><label class="form-label">หรือ แนบรูป QR โอนเงิน</label>
            <input type="hidden" id="stfQrUrl"><div id="stfQrPreview" style="margin-bottom:6px"></div>
            <input type="file" accept="image/*" onchange="SiteTransferCard.uploadQr(this)">
            <button type="button" class="btn btn-secondary btn-sm" style="margin-top:4px" onclick="SiteTransferCard.clearQr()">ลบรูป QR</button></div>
        </div>
        <button type="button" class="btn btn-primary btn-sm" onclick="SiteTransferCard.save()">บันทึกบัญชีรับโอน</button></div>`;
      await this.load();
      if (window.CmsDirtyGuard) CmsDirtyGuard.watch($('stfCard'));
    },
    async load() {
      try {
        const d = (await API.get(url(this._siteId))).data || {};
        const opts = Array.isArray(d.bankNameOptions) ? d.bankNameOptions : [];
        const cur = d.bankName || '';
        $('stfBank').innerHTML = '<option value="">— เลือก —</option>' + opts.map(o => `<option value="${esc(o)}">${esc(o)}</option>`).join('')
          + `<option value="${OTHER}">อื่น ๆ (พิมพ์เอง)</option>`;
        $('stfBank').value = cur;   // ค่าเดิมนอกรายการ: เซิร์ฟเวอร์ใส่ไว้ในรายการแล้ว (ไม่หายเงียบ)
        $('stfBankOther').value = ''; this.onBankChange();
        const set = (id, v) => { const el = $(id); if (el) el.value = v == null ? '' : v; };
        set('stfAcct', d.bankAccountNumber); set('stfAcctName', d.bankAccountName); set('stfBranch', d.bankBranch);
        set('stfPromptPay', d.promptPayId); set('stfQrUrl', d.promptPayQrUrl);
        this._qrPreview();
        $('stfUsedBy').textContent = (d.usedBy || []).length ? `(งานของเว็บนี้: ${(d.usedBy || []).join(' · ')})` : '';
        $('stfProblem').innerHTML = d.payable ? ''
          : '<div style="background:#fffbeb;border:1px solid #fcd34d;color:#78350f;border-radius:8px;padding:8px 12px;font-size:13px;margin-bottom:6px">⚠️ ยังไม่มีเลขบัญชี/พร้อมเพย์/รูป QR — ลูกค้าที่ต้องโอนจะเห็น “ติดต่อร้านเพื่อขอเลขบัญชี”</div>';
        if (window.CmsDirtyGuard) CmsDirtyGuard.reset($('stfCard'));
      } catch (e) {
        $('stfProblem').innerHTML = `<div class="sg-hint" style="color:#b91c1c">โหลดบัญชีรับโอนไม่สำเร็จ: ${esc(e.message)}</div>`;
      }
    },
    onBankChange() { const other = $('stfBank').value === OTHER; $('stfBankOther').style.display = other ? '' : 'none'; },
    async uploadQr(input) {
      const f = input && input.files && input.files[0]; if (!f) return;
      const api = Layout.api(); if (!api || !this._siteId) return;
      try {
        const fd = new FormData(); fd.append('file', f);
        const res = await api.cmsUploadMedia(this._siteId, fd);
        const u = res && res.data && res.data.publicUrl;
        if (!u) throw new Error('เซิร์ฟเวอร์ไม่ส่ง URL กลับ');
        $('stfQrUrl').value = u; this._qrPreview();
        if (window.CmsDirtyGuard) CmsDirtyGuard.markDirty($('stfCard'));
        Layout.toast('อัปโหลดรูป QR แล้ว — กด “บันทึกบัญชีรับโอน” เพื่อใช้งาน', 'info');
      } catch (e) { Layout.toast('อัปโหลดรูป QR ไม่สำเร็จ: ' + e.message, 'error'); }
      input.value = '';
    },
    clearQr() { $('stfQrUrl').value = ''; this._qrPreview(); if (window.CmsDirtyGuard) CmsDirtyGuard.markDirty($('stfCard')); },
    _qrPreview() {
      const u = ($('stfQrUrl') || {}).value || ''; const el = $('stfQrPreview'); if (!el) return;
      el.innerHTML = u ? `<img src="${esc(u)}" alt="QR โอนเงิน" style="max-width:160px;max-height:160px;border:1px solid var(--gray-200);border-radius:8px">` : '<span class="sg-hint">ยังไม่มีรูป QR</span>';
    },
    async save() {
      const bank = $('stfBank').value === OTHER ? txt('stfBankOther') : ($('stfBank').value || null);
      const body = { bankName: bank, bankAccountNumber: txt('stfAcct'), bankAccountName: txt('stfAcctName'), bankBranch: txt('stfBranch'),
        promptPayId: txt('stfPromptPay'), promptPayQrUrl: txt('stfQrUrl') };
      try {
        let res;
        try { res = await API.put(url(this._siteId), body); }
        catch (e1) {
          if (e1 && e1.status === 409 && /ยืนยันการล้าง/.test(e1.message || '') && confirm(e1.message)) {
            body.confirmClear = true; res = await API.put(url(this._siteId), body);
          } else throw e1;
        }
        Layout.toast(res.message || 'บันทึกแล้ว', 'success');
        await this.load();
        // ฝ่ายค้านรอบ 228 S-5(a): การ์ดการชำระของการจองอาจมีค่าที่ยังไม่บันทึก ⇒ ไม่โหลดทับ — อัปเดตเฉพาะคำเตือน/ตารางผล
        if (window.BookingPayAdmin && this._siteId && $('bkSecPayment')) await window.BookingPayAdmin.refreshWarnings(this._siteId);
      } catch (e) {
        $('stfProblem').innerHTML = `<div style="background:#fef2f2;border:1px solid #fca5a5;color:#991b1b;border-radius:8px;padding:10px 12px;font-size:13px;margin-bottom:8px">⚠️ ${esc(e.message)}</div>`;
        Layout.toast(e.message, 'error', 10000);
      }
    },
  };
})();
