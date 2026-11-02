/* รอบ 218 (คำตัดสินข้อ 177 · erp-review/2026-10-30/BOOKING_PAYMENT_DESIGN.md) — การชำระล่วงหน้าของการจองนัดหมาย (ฝั่งร้าน)
 * - ตั้งค่าเว็บ › การชำระเงิน (cms-site-settings · window.CmsSiteSettingsExt.payment — รอบ 215 เฟส 2): ฟอร์ม "💳 การชำระเงินของการจอง"
 *   (#bkSecPayment · ที่เดียว — cms-edit เหลือกล่อง "ย้ายแล้ว" #bkSecPaymentMoved)
 * - cms-edit: ฟอร์มบริการ (#bsPayWrap · ตามค่าเว็บ/กำหนดเอง)
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
      const moved = $('bkSecPaymentMovedLink');   // cms-edit: กล่อง "ย้ายแล้ว" ชี้ไปเว็บเดียวกัน
      if (moved && siteId) moved.href = `/pages/cms-site-settings.html?siteId=${encodeURIComponent(siteId)}&group=payment`;
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
        $('bpayGatewayHint').textContent = d.companyGatewayConfigured
          ? `ระบบรับชำระเงินของบริษัท: ${d.companyGatewayName || 'ตั้งค่าแล้ว'} — ลูกค้าจ่ายแล้วยืนยันการจองอัตโนมัติ`
          : 'บริษัทยังไม่ได้ตั้งค่าระบบรับชำระเงินออนไลน์ (ตั้งค่า → รับชำระเงินออนไลน์) — เปิดช่องนี้ไม่ได้จนกว่าจะตั้ง';
        $('bpayGateway').disabled = !d.companyGatewayConfigured && !d.onlineGatewayEnabled;
        // รอบ 226 (คำตัดสิน 226-2): ช่องว่าง = ค่าตั้งต้นของเซิร์ฟเวอร์ (โหมดบังคับ)
        const dl = $('bpayDeadline'); if (dl) dl.placeholder = `ว่าง = ค่าตั้งต้น ${Number(d.defaultDeadlineMinutes || 1440)} นาที (${Math.round(Number(d.defaultDeadlineMinutes || 1440) / 60)} ชม.) · 0 = ไม่กำหนด`;
        const dn = $('bpayDeadlineNote'); if (dn) dn.textContent = d.deadlineNote || '';
        // รอบ 228 (แพ็ก F · §1 ข้อ 8): "ต้องชำระภายใน" เป็น ตัวเลข + หน่วย (นาที/ชั่วโมง/วัน) — ช่องเดิมยังเก็บนาที
        if (window.CmsDuration) { CmsDuration.attach('bpayDeadline', 'min'); CmsDuration.sync('bpayDeadline'); }   // ฝ่ายค้านรอบ 226 ข้อ 3: เว็บเดิมที่ไม่เคยกรอก ⇒ คงเป็น 0 + แนะนำ
        this._renderProblem(d.problem, d.channelWarnings);
        this._renderUncollectable(d);
        this._renderServices(d.services || []);
        this.onModeChange();
        this._optsLoaded = opts.length > 0;
        if (window.CmsDirtyGuard) CmsDirtyGuard.reset(box.closest('.sg-card') || box);   // รอบ 228 แพ็ก B: ค่าบนจอ = ค่าที่บันทึก
      } catch (e) {
        this._optsLoaded = false;
        box.querySelector('#bpayProblem').innerHTML = `<div class="bs-hint" style="color:#b91c1c">โหลดค่าตั้งการชำระเงินไม่สำเร็จ: ${esc(e.message)}</div>`;
      }
    },
    /** ฝ่ายค้านรอบ 228 S-5(a): หลังบันทึกบัญชีรับโอน — อัปเดตคำเตือน/กล่องแดง/ตารางผล โดยไม่แตะช่องที่ผู้ใช้อาจกำลังแก้ */
    async refreshWarnings(siteId) {
      try {
        const d = (await API.get(`${base(siteId)}/payment-settings`)).data || {};
        this._s = Object.assign({}, this._s || {}, { channelWarnings: d.channelWarnings, services: d.services, uncollectableProblem: d.uncollectableProblem, problem: d.problem });
        this._renderProblem(d.problem, d.channelWarnings);
        this._renderUncollectable(d);
        this._renderServices(d.services || []);
      } catch (_) { /* คำเตือนเดิมคงไว้ — ครั้งหน้าโหลดใหม่ */ }
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
    // รอบ 223: คำเตือนช่องทาง (เซิร์ฟเวอร์ตัดสิน — BookingPaymentChannels.Warnings) แสดงค้างใต้ปัญหาหลัก — ติ๊กแล้วแต่ลูกค้าไม่เห็น ต้องไม่เงียบ
    _renderProblem(p, warnings) {
      const el = $('bpayProblem'); if (!el) return;
      const ws = Array.isArray(warnings) ? warnings : (this._s && Array.isArray(this._s.channelWarnings) ? this._s.channelWarnings : []);
      el.innerHTML = (p ? `<div style="background:#fef2f2;border:1px solid #fca5a5;color:#991b1b;border-radius:8px;padding:10px 12px;font-size:13px;margin-bottom:8px">⚠️ ${esc(p)}</div>` : '')
        + ws.map(w => `<div style="background:#fffbeb;border:1px solid #fcd34d;color:#78350f;border-radius:8px;padding:8px 12px;font-size:13px;margin-bottom:6px">⚠️ ${esc(w)}</div>`).join('');
    },
    // รอบ 226 (คำตัดสิน 226-1): โหมดบังคับ + บริการที่เก็บเงินล่วงหน้าไม่ได้ ⇒ กล่องแดง (บันทึกไม่ผ่านจนแก้) + ปุ่มแก้ในคลิกเดียวต่อบริการ/ทั้งหมด
    _renderUncollectable(d) {
      const el = $('bpayUncollectable'); if (!el) return;
      const rows = (d.services || []).filter(r => r.requiredButUncollectable);
      if (!d.uncollectableProblem || !rows.length) { el.innerHTML = ''; return; }
      const ids = rows.map(r => r.id);
      const convIds = rows.filter(r => r.bookingType !== 'PrePayment').map(r => r.id);
      el.innerHTML = `<div style="background:#fef2f2;border:1px solid #fca5a5;color:#991b1b;border-radius:8px;padding:10px 12px;font-size:13px;margin-bottom:8px">
          ⛔ ${esc(d.uncollectableProblem)}
          <table class="table" style="font-size:12px;margin-top:6px;background:#fff"><tbody>${rows.map(r => `<tr><td>${esc(r.name)}<div class="bs-hint">${esc(Layout.enumLabel ? Layout.enumLabel('bookingType', r.bookingType) : r.bookingType)}</div></td>
            <td style="white-space:nowrap">${r.bookingType !== 'PrePayment' ? `<button type="button" class="btn btn-primary btn-sm" onclick="BookingPayAdmin.fixServices('convert', ['${jsArg(r.id)}'])">เปลี่ยนเป็นชำระมัดจำล่วงหน้า</button>` : `<span class="bs-hint">${esc(r.problem || '')}</span>`}
              <button type="button" class="btn btn-secondary btn-sm" onclick="BookingPayAdmin.fixServices('exempt', ['${jsArg(r.id)}'])">ยกเว้น (ไม่รับชำระล่วงหน้า)</button></td></tr>`).join('')}</tbody></table>
          ${rows.length > 1 ? `<div style="display:flex;gap:6px;flex-wrap:wrap;margin-top:6px">
            ${convIds.length ? `<button type="button" class="btn btn-primary btn-sm" onclick='BookingPayAdmin.fixServices("convert", ${esc(JSON.stringify(convIds))})'>เปลี่ยนชนิดทั้งหมด (${convIds.length})</button>` : ''}
            <button type="button" class="btn btn-secondary btn-sm" onclick='BookingPayAdmin.fixServices("exempt", ${esc(JSON.stringify(ids))})'>ยกเว้นทั้งหมด (${rows.length})</button></div>` : ''}
        </div>`;
    },
    async fixServices(action, ids) {
      if (!this._siteId || !Array.isArray(ids) || !ids.length) return;
      const msg = action === 'convert'
        ? 'เปลี่ยนชนิดบริการเป็น “ชำระมัดจำล่วงหน้า (ใบรับมัดจำ)”?\n\nมีผลกับการจองใหม่เท่านั้น — การจองที่มีอยู่ใช้ชนิดเดิมที่ตรึงไว้ตอนจอง · ลูกค้าใหม่ต้องชำระตามโหมดของเว็บก่อนการจองจะยืนยัน · ใบรับมัดจำลงวันที่รับเงิน (§78/1)'
        : 'ยกเว้นบริการนี้ — ไม่รับชำระล่วงหน้า?\n\nการจองบริการนี้จะยืนยันตามค่า “ยืนยันอัตโนมัติ” ของบริการ (ไม่มีขั้นชำระ)';
      if (!confirm(msg)) return;
      try {
        const res = await API.post(`${base(this._siteId)}/payment-settings/fix-services`, { action, serviceIds: ids });
        Layout.toast(res.message || 'แก้บริการแล้ว', 'success');
        await this.loadSettings(this._siteId);
        if (window.Page && typeof Page.loadBookingServices === 'function') await Page.loadBookingServices();
      } catch (e) { Layout.toast(e.message, 'error', 10000); }
    },
    // รอบ 228 (CMS_SETTINGS_UX_AUDIT แพ็ก D · §1 ข้อ 9): ตารางรวม "มัดจำ & การยกเลิก ของทุกงาน" — ข้อความจากเซิร์ฟเวอร์ · แถวบอกที่แก้ (ไม่มีช่องแก้ซ้ำ)
    async loadOverview(siteId) {
      const el = $('sgDepositOverview'); if (!el || !siteId) return;
      try {
        const rows = (await API.get(`${base(siteId)}/deposit-overview`)).data || [];
        el.innerHTML = rows.length ? `<div class="sg-card"><h3 style="font-size:16px;font-weight:600;margin:0 0 6px">📋 มัดจำ & การยกเลิก ของทุกงาน</h3>
          <div class="bs-hint" style="margin-bottom:6px">ภาพรวมอ่านอย่างเดียว — ตรวจว่านโยบายบนเว็บตรงกับที่ระบบเก็บจริง · แก้ตามคอลัมน์ "แก้ที่"</div>
          <div style="overflow-x:auto"><table class="table" style="font-size:12px"><thead><tr><th>งาน</th><th>ชื่อ</th><th>มัดจำ/ชำระก่อน</th><th>ยกเลิก</th><th>แก้ที่</th></tr></thead><tbody>${rows.map(r =>
            `<tr><td>${esc(r.work)}</td><td>${esc(r.name)}</td><td>${esc(r.deposit)}${r.warning ? `<div style="color:#b45309">⚠️ ${esc(r.warning)}</div>` : ''}</td><td>${esc(r.cancellation)}</td><td class="bs-hint">${esc(r.where)}</td></tr>`).join('')}</tbody></table></div></div>` : '';
      } catch (_) { el.innerHTML = ''; }
    },
    _renderServices(rows) {
      const el = $('bpayServices'); if (!el) return;
      if (!rows.length) { el.innerHTML = '<div class="bs-hint">ยังไม่มีบริการ</div>'; return; }
      el.innerHTML = `<table class="table" style="font-size:12px"><thead><tr><th>บริการ</th><th>ผลที่มีจริง</th><th></th></tr></thead><tbody>${rows.map(r => `
        <tr><td>${esc(r.name)}<div class="bs-hint">${esc(Layout.enumLabel ? Layout.enumLabel('bookingType', r.bookingType) : r.bookingType)}</div></td>
          <td style="${r.problem && !r.collects ? 'color:#b45309' : ''}">${esc(r.summary)}</td>
          <td>${window.Page && typeof Page.openEditBookingService === 'function' ? `<button type="button" class="btn btn-secondary btn-sm" onclick="Page.openEditBookingService('${jsArg(r.id)}')">แก้บริการ</button>` : ''}</td></tr>`).join('')}</tbody></table>`;
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
        // รอบ 228 (CMS_SETTINGS_UX_AUDIT แพ็ก A): บัญชีรับโอนบันทึกที่การ์ดของมันเอง (js/site-transfer-account.js) — หมวดนี้ไม่แตะแถวบัญชี
        updateTransfer: false,
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
    // รอบ 223 (BOOKING_PAYMENT_AUDIT ข้อ 4): หน้าทำงานของพนักงานเห็นการชำระครบ — โหมด · ที่มาของยอด · ยอดค้าง · กำหนดชำระ · ขั้นของลูกค้า ·
    // สลิป (รอตรวจ/ยืนยันแล้ว — เปิดดูผ่านลิงก์ที่ต้องล็อกอิน + สิทธิ์ CmsOrderManage) · ค่าทุกตัวจากเซิร์ฟเวอร์
    slipBlock(b) {
      const p = b && b.payment; if (!p) return '';
      const rows = [
        p.statusLabel ? ['การชำระล่วงหน้า', p.statusLabel] : null,
        p.mode ? ['โหมด', p.modeLabel] : null,
        p.amountBasis ? ['ที่มาของยอด', p.amountBasis] : null,
        Number(p.amountDue) > 0 ? ['ยอดค้างชำระล่วงหน้า', '฿' + Number(p.amountDue).toLocaleString('th-TH', { minimumFractionDigits: 2, maximumFractionDigits: 2 })] : null,
        p.holdUntil ? ['ต้องชำระภายใน', new Date(p.holdUntil).toLocaleString('th-TH', { timeZone: 'Asia/Bangkok' }) + ' (ไม่ชำระ ⇒ ระบบยกเลิกอัตโนมัติ)'] : null,
        p.headline && p.step && p.step !== 'None' ? ['ลูกค้าเห็น', p.headline] : null,
        p.slipRejectedReason && !p.slipPending ? ['สลิปล่าสุดไม่ผ่าน', p.slipRejectedReason] : null,
        p.shopPolicyWarning ? ['⚠️ นโยบายชำระ', p.shopPolicyWarning] : null,   // รอบ 226: โหมดบังคับแต่บริการเก็บเงินไม่ได้
      ].filter(Boolean);
      const head = rows.length ? `<div style="margin-top:6px;font-size:12px;border:1px solid var(--gray-200);border-radius:8px;padding:6px 10px">${rows.map(r =>
        `<div style="display:flex;justify-content:space-between;gap:8px"><span style="color:var(--gray-500)">${esc(r[0])}</span><span style="text-align:right">${esc(r[1])}</span></div>`).join('')}</div>` : '';
      const fileBtn = p.slipFileUrl ? `<button type="button" class="btn btn-sm btn-outline" onclick="BookingPayAdmin.viewSlip('${jsArg(p.slipFileUrl)}')">ดูสลิป</button>` : '';
      // ฝ่ายค้านรอบ 218 (S2): การจองยกเลิก/ไม่มา ⇒ ไม่มีปุ่มตรวจสลิป (เซิร์ฟเวอร์ปฏิเสธด้วย)
      if (!p.slipPending || b.status === 'Cancelled' || b.status === 'NoShow')
        return head + (p.slipOnFile && fileBtn ? `<div style="margin-top:6px;font-size:12px;display:flex;gap:6px;align-items:center;flex-wrap:wrap">🧾 สลิปในแฟ้มของการจอง ${fileBtn}</div><div id="bkSlipView"></div>` : '');
      const upBkk = p.slipUploadedAt ? new Date(new Date(p.slipUploadedAt).getTime() + 7 * 3600 * 1000).toISOString().slice(0, 10) : '';
      return `${head}<div style="margin-top:6px;border:1px solid #fcd34d;background:#fffbeb;border-radius:8px;padding:8px 10px;font-size:12px">
          🧾 ลูกค้าแนบสลิป${p.slipUploadedAt ? ' เมื่อ ' + esc(new Date(p.slipUploadedAt).toLocaleString('th-TH', { timeZone: 'Asia/Bangkok' })) : ''} — ตรวจยอดในบัญชีธนาคารก่อนยืนยัน
          <div style="margin-top:6px"><label style="font-size:12px">วันที่โอนตามสลิป (= วันที่ใบรับมัดจำ §78/1)
            <input type="date" id="bkSlipTransferDate" class="form-input" style="width:auto;display:inline-block" value="${esc(upBkk)}"></label></div>
          <div style="display:flex;gap:6px;flex-wrap:wrap;margin-top:6px">
            ${fileBtn}
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
  // ═══════════ ตั้งค่าเว็บ › การชำระเงิน (รอบ 215 เฟส 2 hook) ═══════════
  // ฟอร์มค่าตั้งการจองอยู่ที่นี่ที่เดียว · กลุ่มนี้ถูกแทนทั้งกลุ่ม ⇒ แสดงลิงก์วิธีชำระของคำสั่งซื้อ/ที่พักต่อท้าย (ไม่ให้หาย)
  const STYLE_ID = 'bookingPayAdminStyle';
  function ensureStyle() {
    if ($(STYLE_ID)) return;
    const st = document.createElement('style'); st.id = STYLE_ID;
    st.textContent = '.bs-sec{font-size:13px;font-weight:700;color:var(--gray-700);margin:16px 0 8px;padding-top:10px;border-top:1px solid var(--gray-200)}'
      + '.bs-hint{font-size:11px;color:var(--gray-500);font-weight:400}';
    document.head.appendChild(st);
  }
  function settingsMarkup() {
    return `
<div id="bkSecPayment" style="border:1px solid var(--gray-200);border-radius:10px;padding:14px 16px;margin:16px 0">
        <h3 style="font-size:16px;font-weight:600;margin:0 0 6px">💳 การชำระเงินของการจอง (มัดจำ / จ่ายเต็มก่อน)</h3>
        <div class="bs-hint" style="margin-bottom:8px">ลูกค้าเห็นยอดที่ต้องจ่าย + กำหนดเวลา + วิธีชำระ <b>ก่อนกดจอง</b> · หลังจองโอน/สแกน QR แล้วแนบสลิป (ร้านตรวจที่หน้า "การจอง") หรือจ่ายออนไลน์ ·
          เก็บเงินล่วงหน้าได้เฉพาะบริการชนิด "ชำระมัดจำล่วงหน้า (ใบรับมัดจำ)" — ใบรับมัดจำลงวันที่รับเงิน (§78/1) · บริการแต่ละตัวตั้งทับได้ที่ฟอร์มบริการ</div>
        <div id="bpayProblem"></div>
        <div id="bpayUncollectable"></div>
        <div class="form-row">
          <div class="form-group"><label class="form-label">โหมดของเว็บ</label><select class="form-input" id="bpayMode" onchange="BookingPayAdmin.onModeChange()"></select>
            <div class="bs-hint" id="bpayModeHelp"></div></div>
          <div class="form-group" id="bpayDeadlineRow"><label class="form-label">ถ้าไม่จ่ายภายใน… หลังจอง ระบบยกเลิกให้อัตโนมัติ (ว่าง = ค่าตั้งต้น 24 ชม. · 0 = ไม่กำหนด)</label>
            <input type="number" class="form-input" id="bpayDeadline" min="0" max="10080" placeholder="ว่าง = ค่าตั้งต้น 1440 นาที (24 ชม.)">
            <div class="bs-hint" id="bpayDeadlineNote"></div>
            <div class="bs-hint">ไม่ชำระ/ไม่แนบสลิปในเวลา ⇒ ระบบยกเลิกการจองและปล่อยรอบให้คนอื่น (ไม่เกินเวลานัด) · แนบสลิปแล้วไม่ถูกยกเลิกระหว่างรอร้านตรวจ</div></div>
        </div>
        <div class="form-row-3" id="bpayDepositRow">
          <div class="form-group"><label class="form-label">มัดจำ (บาท)</label><input type="number" class="form-input" id="bpayFixed" min="0" step="0.01" placeholder="ใช้แทน %"></div>
          <div class="form-group"><label class="form-label">หรือ มัดจำ (% ของค่าบริการ)</label><input type="number" class="form-input" id="bpayPercent" min="0" max="100" step="0.01"></div>
          <div class="form-group"><label class="form-label">ขั้นต่ำ / สูงสุด (บาท)</label>
            <div style="display:flex;gap:6px"><input type="number" class="form-input" id="bpayMin" min="0" step="0.01" placeholder="ขั้นต่ำ"><input type="number" class="form-input" id="bpayMax" min="0" step="0.01" placeholder="สูงสุด"></div></div>
        </div>
        <h4 class="bs-sec">ช่องทางรับเงิน</h4>
        <div class="form-group"><label style="font-size:13px"><input type="checkbox" id="bpayGateway"> ชำระออนไลน์ (บัตร/QR ผ่านระบบรับชำระเงิน — ยืนยันการจองอัตโนมัติ)</label>
          <div class="bs-hint" id="bpayGatewayHint"></div></div>
        <div class="form-group"><label style="font-size:13px"><input type="checkbox" id="bpayTransfer" checked> โอนเงิน / สแกน QR แล้วแนบสลิป (ร้านตรวจสลิป)</label>
          <div class="bs-hint">เลขบัญชี · พร้อมเพย์ · รูป QR ตั้งที่การ์ด <b>“💰 บัญชีรับโอน / พร้อมเพย์”</b> ด้านบน (ที่เดียว — ใช้ร่วมกับคำสั่งซื้อและที่พัก · รอบ 228)</div></div>
        <div class="form-group"><label class="form-label">ข้อความถึงลูกค้า (เงื่อนไขการชำระ/คืนเงิน — แสดงในกล่องชำระเงิน)</label>
          <textarea class="form-input" id="bpayNote" rows="2" maxlength="1000" placeholder="เช่น มัดจำไม่คืนเมื่อยกเลิกภายใน 24 ชม. ก่อนนัด · โอนแล้วร้านยืนยันภายใน 1 ชม."></textarea>
          <div class="bs-hint">ค่าธรรมเนียมยกเลิก/คืนมัดจำตามจริงคิดจาก "ยกเลิกฟรีก่อนเวลานัด" + "% ค่าธรรมเนียม" ของแต่ละบริการ (ริบไม่เกินมัดจำ)</div></div>
        <h4 class="bs-sec">ผลกับแต่ละบริการ</h4>
        <div id="bpayServices" style="overflow-x:auto"><div class="bs-hint">กำลังโหลด…</div></div>
        <button type="button" class="btn btn-primary btn-sm" style="margin-top:8px" onclick="BookingPayAdmin.save()">บันทึกการชำระเงิน</button>
      </div>`;
  }
  function otherLinks(ctx) {
    const mods = (ctx.site && Array.isArray(ctx.site.modules)) ? ctx.site.modules : [];
    const id = encodeURIComponent(ctx.siteId || '');
    const rows = [
      mods.includes('orders') && ['วิธีชำระของร้าน (โอน/ออนไลน์/ปลายทาง)', `/pages/cms-store-settings.html?siteId=${id}&tab=payment`, 'ตั้งค่าร้านค้า › วิธีชำระ'],
      mods.includes('lodging') && ['มัดจำ & การยืนยันของที่พัก', `/pages/lodging-settings.html?siteId=${id}`, 'ตั้งค่าที่พัก › มัดจำ & การยืนยัน'],
      ['ระบบรับชำระเงินออนไลน์ของบริษัท (บัตร/QR)', '/pages/payment-settings.html', 'รับชำระเงินออนไลน์ (ระดับบริษัท)'],
    ].filter(Boolean);
    return `<div class="sg-card"><h3>การชำระเงินส่วนอื่น</h3>${rows.map(r => `<div class="sg-link"><div><div style="font-weight:500">${esc(r[0])}</div>
      <div class="sg-hint">แก้ที่: ${esc(r[2])}</div></div><a class="btn btn-sm btn-secondary" href="${esc(r[1])}">ไปที่นั่น →</a></div>`).join('')}</div>`;
  }
  window.CmsSiteSettingsExt = window.CmsSiteSettingsExt || {};
  window.CmsSiteSettingsExt.payment = async (el, ctx) => {
    ensureStyle();
    const mods = (ctx.site && Array.isArray(ctx.site.modules)) ? ctx.site.modules : [];
    const badge = typeof ctx.scopeBadge === 'function' ? ctx.scopeBadge('site') : '';
    // รอบ 228 (CMS_SETTINGS_UX_AUDIT แพ็ก A): การ์ด "บัญชีรับโอน / พร้อมเพย์" แสดงทุกเว็บ (คำสั่งซื้อ · การจอง · ที่พัก ใช้ร่วม) — อยู่บนสุดของหมวด
    el.innerHTML = '<div id="sgTransferCard"></div><div id="sgDepositOverview"></div>' + (mods.includes('bookings')
      ? `<div class="sg-card"><div class="sg-hint" style="margin-bottom:6px">${badge}</div>${settingsMarkup()}</div>`
      : '')
      + otherLinks(ctx);
    if (window.SiteTransferCard) await window.SiteTransferCard.mount($('sgTransferCard'), ctx);
    if (mods.includes('bookings')) await window.BookingPayAdmin.loadSettings(ctx.siteId);
    await window.BookingPayAdmin.loadOverview(ctx.siteId);   // รอบ 228 แพ็ก D: มัดจำ & การยกเลิก ของทุกงาน (อ่านอย่างเดียว)
    // รอบ 228 (แพ็ก C): จัดส่ง/รับเองของการจองย้ายไปกลุ่ม "การจัดส่ง / รับเอง" ของตัวเอง (CmsSiteSettingsExt.shipping ใน booking-fulfillment-admin.js)
  };
})();
