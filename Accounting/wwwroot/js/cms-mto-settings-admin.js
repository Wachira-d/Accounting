/* รอบ 230 (erp-review/2026-11-02/CMS_SETTINGS_UX_AUDIT.md §6 ค้างข้อ 1 · แพ็ก D) — "สินค้าตามสั่ง / พรีออเดอร์" ย้ายจาก modal หน้าคำสั่งซื้อ เข้าศูนย์ "ตั้งค่าเว็บ"
 * ที่เดียวของ: ลูกค้าต้องจ่ายก่อนไหม (ชำระเต็ม/มัดจำ % หรือบาท) · ริบมัดจำเมื่อยกเลิก · วันปิดประจำสัปดาห์ · รับออเดอร์ของวันถัดไปถึงกี่โมง · ข้ามวันหยุดบริษัท ·
 *   (ขั้นสูง ผู้ทำบัญชี) ใบกำกับภาษีเมื่อรับมัดจำ · VAT ค่าส่งเมื่อสินค้าในบิลหลายอัตรา
 * หน้าคำสั่งซื้อเหลือปุ่มลิงก์มาที่นี่ (ห้ามสองฟอร์มแก้ค่าเดียวกัน) · มารับเอง/ค่าส่งเหมาจ่าย/ส่งฟรี อยู่กลุ่ม "การจัดส่ง / รับเอง"
 * GET/PUT …/commerce/config (ตรวจที่เซิร์ฟเวอร์ · ตัวเลือก+คำอธิบายผลภาษีมาจากเซิร์ฟเวอร์ทั้งหมด — PaymentTermMode · DepositInvoicing · ShippingVatAllocation) ·
 * ส่งเฉพาะช่องของกลุ่มนี้ (ช่องอื่นของร้านไม่ส่ง = เซิร์ฟเวอร์คงค่าเดิม) */
(function () {
  const esc = (v) => (window.Layout && Layout.esc ? Layout.esc(v) : String(v == null ? '' : v).replace(/[&<>"']/g, c => ({ '&': '&amp;', '<': '&lt;', '>': '&gt;', '"': '&quot;', "'": '&#39;' }[c])));
  const $ = (id) => document.getElementById(id);
  const opt = (list, v) => (list || []).map(m => `<option value="${esc(m.value)}"${m.value === v ? ' selected' : ''}>${esc(m.label)}</option>`).join('');
  // ชื่อวันจากปฏิทินของเบราว์เซอร์ (2026-11-01 = วันอาทิตย์ · ค่า 0–6 ตรงกับ MtoLeadTime.ParseClosedWeekdays)
  const dayShort = (d) => new Date(Date.UTC(2026, 10, 1 + d)).toLocaleDateString('th-TH', { weekday: 'short', timeZone: 'UTC' });

  const M = {
    _siteId: null, _c: null,
    async mount(el, ctx) {
      this._siteId = ctx.siteId;
      try { this._c = (await Layout.api().cmsGetCommerceConfig(ctx.siteId)).data || {}; }
      catch (e) { el.innerHTML = `<div class="sg-card" style="color:#b91c1c">โหลดค่าสินค้าตามสั่งไม่สำเร็จ: ${esc(e.message)}</div>`; return; }
      const c = this._c; const id = encodeURIComponent(ctx.siteId || '');
      const badge = typeof ctx.scopeBadge === 'function' ? ctx.scopeBadge('site') : '';
      const closed = new Set(String(c.mtoClosedWeekdays || '').split(',').filter(Boolean));
      el.innerHTML = `<div class="sg-card" id="sgMtoPay"><h3>🎂 ลูกค้าต้องจ่ายก่อนไหม (สินค้าตามสั่ง) ${badge}</h3>
          <div class="sg-hint" style="margin-bottom:8px">ใช้กับคำสั่งซื้อที่มีสินค้าตามสั่ง/พรีออเดอร์ · มีผลกับออเดอร์ที่สั่งหลังบันทึก — ออเดอร์ที่ค้างอยู่ใช้วิธีที่ตรึงไว้ตอนสั่ง</div>
          <div class="form-group"><label class="form-label" for="sgMtoTerm">เงื่อนไขชำระ</label><select class="form-input" id="sgMtoTerm" onchange="CmsMtoAdmin.onTerm()">${opt(c.paymentTermModes, c.mtoPaymentTermMode)}</select></div>
          <div class="form-row">
            <div class="form-group" id="sgMtoPctWrap"><label class="form-label" for="sgMtoPct">มัดจำ (%)</label><input type="number" class="form-input" id="sgMtoPct" min="1" max="99" step="0.01" value="${esc(c.mtoDepositPercent ?? '')}"></div>
            <div class="form-group" id="sgMtoFixedWrap"><label class="form-label" for="sgMtoFixed">มัดจำ (บาท)</label><input type="number" class="form-input" id="sgMtoFixed" min="1" step="0.01" value="${esc(c.mtoDepositFixedAmount ?? '')}"></div>
          </div>
          <div class="form-group"><label class="form-label" for="sgMtoForfeit">ยกเลิกออเดอร์หลังจ่ายมัดจำ — ริบมัดจำคงเหลือ (%)</label>
            <input type="number" class="form-input" id="sgMtoForfeit" min="0" max="100" step="0.01" placeholder="ว่าง = ไม่ริบเอง (รอพนักงานตัดสินที่หน้าเงินมัดจำ)" value="${esc(c.depositForfeitOnCancelPercent ?? '')}">
            <div class="sg-hint">ตั้งแล้วระบบริบส่วนนี้เข้ารายได้ค่าธรรมเนียมอัตโนมัติตอนยกเลิก (ไม่รวมเงินประกัน) · ส่วนที่เหลือรอคืน — ระบบไม่คืนเงินเอง</div></div>
          <details style="margin-bottom:10px"><summary style="cursor:pointer;font-size:13px">ขั้นสูง (สำหรับผู้ทำบัญชี) — ค่าเริ่มต้นถูกต้องตามกฎหมายแล้ว</summary>
            <div class="form-group" style="margin-top:8px"><label class="form-label" for="sgMtoDepInv">ใบกำกับภาษีเมื่อลูกค้าจ่ายมัดจำ</label>
              <select class="form-input" id="sgMtoDepInv" onchange="CmsMtoAdmin.onHelp('sgMtoDepInv','depositInvoicingModes')">${opt(c.depositInvoicingModes, c.mtoDepositInvoicing)}</select>
              <div class="sg-hint" id="sgMtoDepInvHelp" style="white-space:pre-wrap"></div></div>
            <div class="form-group"><label class="form-label" for="sgMtoShipVat">VAT ค่าจัดส่งเมื่อสินค้าในบิลหลายอัตรา</label>
              <select class="form-input" id="sgMtoShipVat" onchange="CmsMtoAdmin.onHelp('sgMtoShipVat','shippingVatAllocationOptions')">${opt(c.shippingVatAllocationOptions, c.shippingVatAllocation)}</select>
              <div class="sg-hint" id="sgMtoShipVatHelp" style="white-space:pre-wrap"></div></div>
          </details>
          <button type="button" class="btn btn-primary btn-sm" onclick="CmsMtoAdmin.savePay()">บันทึกการชำระของสินค้าตามสั่ง</button></div>
        <div class="sg-card" id="sgMtoDays"><h3>📆 วันผลิต / วันรับของ</h3>
          <div class="form-group"><span class="form-label">วันปิดร้านประจำสัปดาห์ (ไม่นับเป็นวันผลิต/รับของ)</span>
            <div style="display:flex;gap:10px;flex-wrap:wrap;font-size:13px">${[1, 2, 3, 4, 5, 6, 0].map(d => `<label style="display:flex;gap:4px;align-items:center"><input type="checkbox" class="sg-mto-dow" value="${d}"${closed.has(String(d)) ? ' checked' : ''}> ${esc(dayShort(d))}</label>`).join('')}</div></div>
          <div class="form-row">
            <div class="form-group"><label class="form-label" for="sgMtoCutoff">รับออเดอร์ของวันถัดไปถึงกี่โมง (0–23 · ว่าง = ไม่ตัดรอบ)</label><input type="number" class="form-input" id="sgMtoCutoff" min="0" max="23" value="${esc(c.mtoCutoffHour ?? '')}"></div>
            <div class="form-group" style="display:flex;align-items:flex-end"><label style="display:flex;gap:8px;align-items:center;font-size:13px"><input type="checkbox" id="sgMtoHolidays"${c.mtoSkipCompanyHolidays !== false ? ' checked' : ''}> ข้ามวันหยุดบริษัท (หน้า "วันหยุด")</label></div>
          </div>
          <div class="sg-hint" style="margin-bottom:8px">วันหยุด/วันพิเศษที่ "ปิดทั้งวัน" ของเว็บ (ตั้งที่ <a href="/pages/cms-site-settings.html?siteId=${id}&group=booking" style="color:var(--primary)">การจอง / บริการ › เวลาเปิดร้าน & วันหยุด</a>) ใช้กับวันรับของด้วย — ตั้งที่เดียว</div>
          <button type="button" class="btn btn-primary btn-sm" onclick="CmsMtoAdmin.saveDays()">บันทึกวันผลิต/รับของ</button></div>
        <div class="sg-card"><div class="sg-link"><div><div style="font-weight:500">มารับเองที่ร้าน · ค่าส่งเหมาจ่าย · ส่งฟรี</div><div class="sg-hint">แก้ที่: ตั้งค่าเว็บ › การจัดส่ง / รับเอง</div></div>
          <a class="btn btn-sm btn-secondary" href="/pages/cms-site-settings.html?siteId=${id}&group=shipping">ไปที่นั่น →</a></div></div>`;
      this.onTerm(); this.onHelp('sgMtoDepInv', 'depositInvoicingModes'); this.onHelp('sgMtoShipVat', 'shippingVatAllocationOptions');
      if (window.CmsDirtyGuard) { CmsDirtyGuard.watch($('sgMtoPay')); CmsDirtyGuard.watch($('sgMtoDays')); }
    },
    onTerm() {
      const t = ($('sgMtoTerm') || {}).value;
      if ($('sgMtoPctWrap')) $('sgMtoPctWrap').style.display = t === 'deposit_percent' ? '' : 'none';
      if ($('sgMtoFixedWrap')) $('sgMtoFixedWrap').style.display = t === 'deposit_fixed' ? '' : 'none';
    },
    onHelp(selId, listKey) {
      const v = ($(selId) || {}).value; const m = ((this._c || {})[listKey] || []).find(x => x.value === v);
      const h = $(selId + 'Help'); if (h) h.textContent = (m && m.description) || '';
    },
    async _put(body, cardId) {
      try {
        await Layout.api().cmsUpdateCommerceConfig(this._siteId, body);
        Layout.toast('บันทึกแล้ว', 'success');
        if (window.CmsDirtyGuard) CmsDirtyGuard.reset($(cardId));
      } catch (e) { Layout.toast(e.message, 'error', 10000); }
    },
    async savePay() {
      const n = (id) => { const v = $(id).value; if (v === '') return null; const x = Number(v); return Number.isFinite(x) ? x : null; };
      const forfeit = n('sgMtoForfeit');
      await this._put({
        mtoPaymentTermMode: $('sgMtoTerm').value, mtoDepositPercent: n('sgMtoPct'), mtoDepositFixedAmount: n('sgMtoFixed'),
        mtoDepositInvoicing: $('sgMtoDepInv').value || null, shippingVatAllocation: $('sgMtoShipVat').value || null,
        depositForfeitOnCancelPercent: forfeit, clearDepositForfeitOnCancelPercent: forfeit == null,
      }, 'sgMtoPay');
    },
    async saveDays() {
      const raw = $('sgMtoCutoff').value; const cutoff = raw === '' ? null : parseInt(raw, 10);
      await this._put({
        mtoClosedWeekdays: Array.from(document.querySelectorAll('.sg-mto-dow')).filter(x => x.checked).map(x => x.value).join(','),
        mtoCutoffHour: Number.isFinite(cutoff) ? cutoff : null, clearMtoCutoffHour: !Number.isFinite(cutoff),
        mtoSkipCompanyHolidays: $('sgMtoHolidays').checked,
      }, 'sgMtoDays');
    },
  };
  window.CmsMtoAdmin = M;
  window.CmsSiteSettingsExt = window.CmsSiteSettingsExt || {};
  window.CmsSiteSettingsExt.mto = (el, ctx) => M.mount(el, ctx);
})();
