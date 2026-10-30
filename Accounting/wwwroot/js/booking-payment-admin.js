/* รอบ 218 (คำตัดสินข้อ 177 · erp-review/2026-10-30/BOOKING_PAYMENT_DESIGN.md) — การชำระล่วงหน้าของการจองนัดหมาย (ฝั่งร้าน)
 * - cms-edit: หมวด "💳 การชำระเงินของการจอง" (#bkSecPayment) + ฟอร์มบริการ (#bsPayWrap)
 * - cms-bookings: กล่องตรวจสลิปในรายละเอียดการจอง
 * ค่าทุกตัว/ตัวเลือก/ข้อความตัดสินที่เซิร์ฟเวอร์ (Helpers/BookingPaymentPolicy · BookingPaymentChannels) — ไฟล์นี้แสดง/ส่งค่าอย่างเดียว ·
 * แยกไฟล์เพื่อลดการชนกับทีมอื่นที่แก้หน้า cms-edit/cms-bookings */
(function () {
  const esc = (v) => (window.Layout && Layout.esc ? Layout.esc(v) : String(v == null ? '' : v));
  const jsArg = (v) => (window.Layout && Layout.jsArg ? Layout.jsArg(v) : String(v == null ? '' : v));
  const $ = (id) => document.getElementById(id);
  const num = (id) => { const el = $(id); if (!el || el.value === '') return null; const n = Number(el.value); return Number.isFinite(n) ? n : null; };
  const txt = (id) => { const el = $(id); const t = el ? el.value.trim() : ''; return t === '' ? null : t; };
  const base = (siteId) => `/api/companies/${Layout.getCompanyId()}/cms/sites/${siteId}/booking`;

  window.BookingPayAdmin = {
    _s: null,
    _siteId: null,

    // ═══════════ cms-edit: ค่าตั้งเว็บ ═══════════
    async loadSettings(siteId) {
      this._siteId = siteId;
      const box = $('bkSecPayment'); if (!box) return;
      try {
        const d = (await API.get(`${base(siteId)}/payment-settings`)).data || {};
        this._s = d;
        const opts = Array.isArray(d.modeOptions) ? d.modeOptions : [];
        $('bpayMode').innerHTML = opts.map(o => `<option value="${esc(o.value)}" ${o.value === (d.mode || '') ? 'selected' : ''}>${esc(o.label)}</option>`).join('');
        const set = (id, v) => { const el = $(id); if (el) el.value = (v === null || v === undefined) ? '' : v; };
        set('bpayFixed', d.depositFixedAmount); set('bpayPercent', d.depositPercent);
        set('bpayMin', d.depositMinAmount); set('bpayMax', d.depositMaxAmount);
        set('bpayDeadline', d.deadlineMinutes); set('bpayNote', d.paymentNote);
        $('bpayGateway').checked = d.onlineGatewayEnabled === true;
        $('bpayTransfer').checked = d.bankTransferEnabled !== false;
        set('bpayBankName', d.bankName); set('bpayBankAcct', d.bankAccountNumber); set('bpayBankAcctName', d.bankAccountName);
        set('bpayBankBranch', d.bankBranch); set('bpayPromptPay', d.promptPayId); set('bpayQrUrl', d.promptPayQrUrl);
        this._qrPreview();
        $('bpayGatewayHint').textContent = d.companyGatewayConfigured
          ? `ระบบรับชำระเงินของบริษัท: ${d.companyGatewayName || 'ตั้งค่าแล้ว'} — ลูกค้าจ่ายแล้วยืนยันการจองอัตโนมัติ`
          : 'บริษัทยังไม่ได้ตั้งค่าระบบรับชำระเงินออนไลน์ (ตั้งค่า → รับชำระเงินออนไลน์) — เปิดช่องนี้ไม่ได้จนกว่าจะตั้ง';
        $('bpayGateway').disabled = !d.companyGatewayConfigured && !d.onlineGatewayEnabled;
        this._renderProblem(d.problem);
        this._renderServices(d.services || []);
        this.onModeChange();
        this._optsLoaded = opts.length > 0;
      } catch (e) {
        this._optsLoaded = false;
        box.querySelector('#bpayProblem').innerHTML = `<div class="bs-hint" style="color:#b91c1c">โหลดค่าตั้งการชำระเงินไม่สำเร็จ: ${esc(e.message)}</div>`;
      }
    },
    onModeChange() {
      const m = ($('bpayMode') || {}).value || '';
      const deposit = m === 'DepositRequired' || m === 'DepositOptional';
      const required = m === 'DepositRequired' || m === 'FullRequired';
      $('bpayDepositRow').style.display = deposit ? '' : 'none';
      $('bpayDeadlineRow').style.display = required ? '' : 'none';
      const opt = ((this._s && this._s.modeOptions) || []).find(o => o.value === m);
      $('bpayModeHelp').textContent = opt ? opt.description : '';
    },
    _renderProblem(p) {
      const el = $('bpayProblem'); if (!el) return;
      el.innerHTML = p ? `<div style="background:#fef2f2;border:1px solid #fca5a5;color:#991b1b;border-radius:8px;padding:10px 12px;font-size:13px;margin-bottom:8px">⚠️ ${esc(p)}</div>` : '';
    },
    _renderServices(rows) {
      const el = $('bpayServices'); if (!el) return;
      if (!rows.length) { el.innerHTML = '<div class="bs-hint">ยังไม่มีบริการ</div>'; return; }
      el.innerHTML = `<table class="table" style="font-size:12px"><thead><tr><th>บริการ</th><th>ผลที่มีจริง</th><th></th></tr></thead><tbody>${rows.map(r => `
        <tr><td>${esc(r.name)}<div class="bs-hint">${esc(Layout.enumLabel ? Layout.enumLabel('bookingType', r.bookingType) : r.bookingType)}</div></td>
          <td style="${r.problem && !r.collects ? 'color:#b45309' : ''}">${esc(r.summary)}</td>
          <td>${window.Page && typeof Page.openEditBookingService === 'function' ? `<button type="button" class="btn btn-secondary btn-sm" onclick="Page.openEditBookingService('${jsArg(r.id)}')">แก้บริการ</button>` : ''}</td></tr>`).join('')}</tbody></table>`;
    },
    async uploadQr(input) {
      const f = input && input.files && input.files[0]; if (!f) return;
      const api = Layout.api(); if (!api || !this._siteId) return;
      try {
        const fd = new FormData(); fd.append('file', f);
        const res = await api.cmsUploadMedia(this._siteId, fd);
        const url = res && res.data && res.data.publicUrl;
        if (!url) throw new Error('เซิร์ฟเวอร์ไม่ส่ง URL กลับ');
        $('bpayQrUrl').value = url;
        this._qrPreview();
        Layout.toast('อัปโหลดรูป QR แล้ว — กด “บันทึกการชำระเงิน” เพื่อใช้งาน', 'info');
      } catch (e) { Layout.toast('อัปโหลดรูป QR ไม่สำเร็จ: ' + e.message, 'error'); }
      input.value = '';
    },
    clearQr() { $('bpayQrUrl').value = ''; this._qrPreview(); },
    _qrPreview() {
      const url = ($('bpayQrUrl') || {}).value || '';
      const el = $('bpayQrPreview'); if (!el) return;
      el.innerHTML = url ? `<img src="${esc(url)}" alt="QR โอนเงิน" style="max-width:160px;max-height:160px;border:1px solid var(--gray-200);border-radius:8px">` : '<span class="bs-hint">ยังไม่มีรูป QR</span>';
    },
    async save() {
      if (!this._siteId) return;
      const body = {
        mode: ($('bpayMode') || {}).value || '',
        depositFixedAmount: num('bpayFixed'), depositPercent: num('bpayPercent'),
        depositMinAmount: num('bpayMin'), depositMaxAmount: num('bpayMax'),
        deadlineMinutes: num('bpayDeadline'),
        onlineGatewayEnabled: $('bpayGateway').checked, bankTransferEnabled: $('bpayTransfer').checked,
        paymentNote: ($('bpayNote') || {}).value || null,
        updateTransfer: true,
        bankName: txt('bpayBankName'), bankAccountNumber: txt('bpayBankAcct'), bankAccountName: txt('bpayBankAcctName'),
        bankBranch: txt('bpayBankBranch'), promptPayId: txt('bpayPromptPay'), promptPayQrUrl: txt('bpayQrUrl'),
      };
      try {
        let res;
        try { res = await API.put(`${base(this._siteId)}/payment-settings`, body); }
        catch (e1) {
          // ฝ่ายค้านรอบ 218 (B1): ล้างช่องบัญชีรับโอนที่ใช้ร่วมกับคำสั่งซื้อ — เซิร์ฟเวอร์ตอบ 409 พร้อมรายชื่อช่อง ⇒ ถามก่อน แล้วส่งซ้ำพร้อมยืนยัน
          if (e1 && e1.status === 409 && /ยืนยันการล้าง/.test(e1.message || '') && confirm(e1.message)) {
            body.confirmClearTransfer = true;
            res = await API.put(`${base(this._siteId)}/payment-settings`, body);
          } else throw e1;
        }
        Layout.toast(res.message || 'บันทึกแล้ว', 'success');
        await this.loadSettings(this._siteId);
        if (window.Page && typeof Page.loadBookingServices === 'function') await Page.loadBookingServices();
      } catch (e) {
        // ด่านเซิร์ฟเวอร์ (โหมดบังคับไม่มีช่องทาง ฯลฯ) — แสดงค้างบนหมวด ไม่ใช่แค่ toast
        this._renderProblem(e.message);
        Layout.toast(e.message, 'error', 10000);
      }
    },

    // ═══════════ cms-edit: ฟอร์มบริการ (ตามค่าเว็บ/กำหนดเอง) ═══════════
    hydrateService(s) {
      const wrap = $('bsPayWrap'); if (!wrap) return;
      const opts = ((this._s && this._s.modeOptions) || []).filter(o => o.value !== '');
      if (!opts.length) { wrap.style.display = 'none'; return; }   // ไม่มีตัวเลือกจาก server ⇒ ไม่ส่ง (null = คงค่าเดิม)
      wrap.style.display = '';
      const cur = s && s.paymentMode ? s.paymentMode : 'Inherit';
      $('bsPayMode').innerHTML = `<option value="Inherit" ${cur === 'Inherit' ? 'selected' : ''}>ตามค่าเว็บ</option>`
        + opts.map(o => `<option value="${esc(o.value)}" ${o.value === cur ? 'selected' : ''}>กำหนดเอง: ${esc(o.label)}</option>`).join('');
      const set = (id, v) => { const el = $(id); if (el) el.value = (v === null || v === undefined) ? '' : v; };
      set('bsPayMin', s ? s.depositMinAmount : null); set('bsPayMax', s ? s.depositMaxAmount : null);
      set('bsPayDeadline', s ? s.paymentDeadlineMinutes : null);
      $('bsPayHint').textContent = s && s.paymentSummary ? 'ผลที่มีจริง: ' + s.paymentSummary : '';
      $('bsPayProblem').textContent = s && s.paymentProblem ? '⚠️ ' + s.paymentProblem : '';
    },
    servicePayload() {
      const wrap = $('bsPayWrap');
      if (!wrap || wrap.style.display === 'none') return {};
      return { paymentMode: $('bsPayMode').value || 'Inherit', depositMinAmount: num('bsPayMin'), depositMaxAmount: num('bsPayMax'), paymentDeadlineMinutes: num('bsPayDeadline') };
    },

    // ═══════════ cms-bookings: ตรวจสลิป ═══════════
    slipBlock(b) {
      const p = b && b.payment; if (!p) return '';
      const head = p.statusLabel ? `<div style="margin-top:6px;font-size:12px"><strong>การชำระล่วงหน้า:</strong> ${esc(p.statusLabel)}</div>` : '';
      // ฝ่ายค้านรอบ 218 (S2): การจองยกเลิก/ไม่มา ⇒ ไม่มีปุ่มตรวจสลิป (เซิร์ฟเวอร์ปฏิเสธด้วย)
      if (!p.slipPending || b.status === 'Cancelled' || b.status === 'NoShow') return head;
      const upBkk = p.slipUploadedAt ? new Date(new Date(p.slipUploadedAt).getTime() + 7 * 3600 * 1000).toISOString().slice(0, 10) : '';
      return `${head}<div style="margin-top:6px;border:1px solid #fcd34d;background:#fffbeb;border-radius:8px;padding:8px 10px;font-size:12px">
          🧾 ลูกค้าแนบสลิป${p.slipUploadedAt ? ' เมื่อ ' + esc(new Date(p.slipUploadedAt).toLocaleString('th-TH', { timeZone: 'Asia/Bangkok' })) : ''} — ตรวจยอดในบัญชีธนาคารก่อนยืนยัน
          <div style="margin-top:6px"><label style="font-size:12px">วันที่โอนตามสลิป (= วันที่ใบรับมัดจำ §78/1)
            <input type="date" id="bkSlipTransferDate" class="form-input" style="width:auto;display:inline-block" value="${esc(upBkk)}"></label></div>
          <div style="display:flex;gap:6px;flex-wrap:wrap;margin-top:6px">
            ${p.slipFileUrl ? `<button type="button" class="btn btn-sm btn-outline" onclick="BookingPayAdmin.viewSlip('${jsArg(p.slipFileUrl)}')">ดูสลิป</button>` : ''}
            <button type="button" class="btn btn-primary btn-sm" onclick="BookingPayAdmin.verify('${jsArg(b.id)}')">ยืนยันสลิป (บันทึกรับเงิน)</button>
            <button type="button" class="btn btn-sm" style="border:1px solid #dc2626;color:#dc2626" onclick="BookingPayAdmin.reject('${jsArg(b.id)}')">ปฏิเสธสลิป</button>
          </div>
          <div id="bkSlipView"></div>
        </div>`;
    },
    async viewSlip(url) {
      const box = $('bkSlipView'); if (!box) return;
      box.innerHTML = '<div class="bs-hint">กำลังโหลดสลิป…</div>';
      try {
        const f = await API.fetchFile(url, 'slip');
        if (!f) { box.innerHTML = ''; return; }
        const u = URL.createObjectURL(f.blob);
        box.innerHTML = (f.blob.type || '').startsWith('image/')
          ? `<img src="${esc(u)}" alt="สลิปโอนเงิน" style="max-width:100%;max-height:420px;border:1px solid var(--gray-200);border-radius:8px;margin-top:8px">`
          : `<a href="${esc(u)}" download="${esc(f.name || 'slip')}" class="btn btn-sm btn-outline" style="margin-top:8px">ดาวน์โหลดสลิป</a>`;
      } catch (e) { box.innerHTML = `<div style="color:#b91c1c">โหลดสลิปไม่สำเร็จ: ${esc(e.message)}</div>`; }
    },
    _sid() { return window.Page && typeof Page._sid === 'function' ? Page._sid() : this._siteId; },
    async _after(id, res, okMsg) {
      ((res && res.data && res.data.erpNotices) || []).forEach(n => Layout.toast(n, 'warning', 15000));
      Layout.toast(res && res.message ? res.message : okMsg, 'success');
      if (window.Page && typeof Page.viewBooking === 'function') await Page.viewBooking(id);
      if (window.Page && typeof Page.load === 'function') await Page.load();
    },
    async verify(id) {
      const transferDate = (($('bkSlipTransferDate') || {}).value || '') || null;
      if (!confirm(`ยืนยันว่าเงินเข้าบัญชีแล้วตามสลิป?\n\nระบบจะบันทึกรับเงิน · ยืนยันการจอง · ออกใบรับมัดจำลงวันที่โอน ${transferDate || '(ตามเวลาที่แนบสลิป)'} (§78/1)`)) return;
      const note = prompt('บันทึกถึงผู้สอบบัญชี (ถ้ามี — เช่น ตรวจยอดในแอปธนาคารแล้ว)', '') ;
      if (note === null) return;
      try {
        const res = await API.post(`${base(this._sid())}/bookings/${encodeURIComponent(id)}/slip/verify`, { note: note || null, transferDate });
        await this._after(id, res, 'ยืนยันสลิปแล้ว');
      } catch (e) { Layout.toast('ยืนยันสลิปไม่สำเร็จ: ' + e.message, 'error', 10000); }
    },
    async reject(id) {
      const reason = prompt('เหตุผลที่สลิปไม่ผ่าน (ลูกค้าจะเห็นข้อความนี้)', 'ยอดเงินไม่ตรง / ยังไม่พบเงินเข้าบัญชี');
      if (reason === null) return;
      if (!reason.trim()) { Layout.toast('ต้องระบุเหตุผล', 'error'); return; }
      try {
        const res = await API.post(`${base(this._sid())}/bookings/${encodeURIComponent(id)}/slip/reject`, { reason });
        await this._after(id, res, 'ปฏิเสธสลิปแล้ว');
      } catch (e) { Layout.toast('ปฏิเสธสลิปไม่สำเร็จ: ' + e.message, 'error', 10000); }
    },
  };
})();
