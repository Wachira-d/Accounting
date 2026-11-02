/* รอบ 228 (erp-review/2026-11-01/BOOKING_PAYMENT_AUDIT.md §9) — หน้า "ดู/จัดการการจองของฉัน" (my-booking/{token}) ส่วนที่ลูกค้าทำเองได้เพิ่ม
 * - มัดจำไม่บังคับที่ไม่ได้เลือกตอนจอง ⇒ ปุ่ม "ชำระมัดจำตอนนี้" (booking.payment.payLater*) → POST …/deposit-later → กล่องชำระเดิม (ออนไลน์/โอน/QR/สลิป)
 * - เปลี่ยนวิธีรับ/ที่อยู่ก่อนกำหนดของร้าน (booking.canChangeFulfillmentOnline) → POST …/fulfillment {previewOnly} ⇒ ค่าส่ง/ผลต่างจากเซิร์ฟเวอร์ → ยืนยัน
 * ค่า/ยอด/เหตุที่ปิดทุกตัวจากเซิร์ฟเวอร์ (Helpers/BookingDepositLater · Helpers/BookingFulfillmentChange · ตัวคิดค่าส่ง Helpers/ShopDeliveryFee) — ไฟล์นี้แสดง/ส่งค่าอย่างเดียว
 * ต่อเข้ากับ Store ผ่าน StoreBookingManage.install(Store) · storefront.html เรียก Store._bkManageRender(b, token) หลังวาดหน้า my-booking */
(function () {
  const box = () => document.getElementById('sfBookingManageBox');
  const card = (inner, color) => `<div style="margin-top:14px;border:1px solid ${color || '#D1D5DB'};border-radius:12px;padding:14px 18px;text-align:left">${inner}</div>`;

  const methods = {
    _bkManageRender(b, token) {
      const el = box();
      if (!el || !b) return;
      this._bkManage = { b, token, quote: null };
      const parts = [];
      const p = b.payment || {};
      if (p.payLaterShow) {
        parts.push(card(`<div style="font-weight:600">💳 มัดจำ (ไม่บังคับ)</div>
            <div style="font-size:13px;margin-top:4px">${this.esc(p.payLaterNote || '')}</div>
            ${p.canPayDepositLater ? `<button class="btn btn-primary" style="margin-top:10px" onclick="Store.bookingDepositLater(this)">ชำระมัดจำ ฿${this._money(p.payLaterAmount)} ตอนนี้</button>` : ''}`,
          '#A7F3D0'));
      }
      if (b.canChangeFulfillmentOnline) {
        parts.push(card(`<div style="font-weight:600">🚚 เปลี่ยนวิธีรับ / ที่อยู่จัดส่ง</div>
            <div style="font-size:13px;color:var(--text-secondary);margin-top:4px">ตอนนี้: ${this.esc(b.fulfillmentSummary || b.fulfillmentLabel || '-')}</div>
            ${b.fulfillmentChangeNote ? `<div style="font-size:12px;color:var(--text-secondary);margin-top:2px">${this.esc(b.fulfillmentChangeNote)}</div>` : ''}
            <div id="sfFulChangeForm"></div>
            <button class="btn" id="sfFulChangeOpen" style="margin-top:10px" onclick="Store.bookingFulfillOpen()">เปลี่ยนวิธีรับ / ที่อยู่</button>`));
      } else if (b.fulfillmentChangeNote && b.fulfillmentMethod) {
        parts.push(`<div style="margin-top:12px;font-size:13px;color:var(--text-secondary)">🚚 ${this.esc(b.fulfillmentChangeNote)}</div>`);
      }
      el.innerHTML = parts.join('');
    },

    async bookingDepositLater(btn) {
      const m = this._bkManage; if (!m) return;
      return SubmitOnce.run(btn, async () => {
        try {
          const r = await SubmitOnce.fetch(this._url('/booking/bookings/by-token/' + encodeURIComponent(m.token) + '/deposit-later'), { method: 'POST' }, btn);
          const j = await r.json().catch(() => null);
          if (!r.ok) throw new Error((j && j.message) || 'ตั้งยอดมัดจำไม่สำเร็จ');
          await this.renderBookingByToken(m.token, (j && j.message) || 'ตั้งยอดมัดจำแล้ว — ชำระได้ด้านล่าง');
        } catch (e) { alert(e.message); return false; }
      }, { busyText: 'กำลังตั้งยอด…' });
    },

    /** เปิดฟอร์ม — ถามเซิร์ฟเวอร์ด้วยค่าปัจจุบันก่อน (วิธีที่เปิด · อัตราที่เลือกได้) */
    async bookingFulfillOpen() {
      const m = this._bkManage; if (!m) return;
      const b = m.b;
      const btn = document.getElementById('sfFulChangeOpen'); if (btn) btn.style.display = 'none';
      m.values = { fulfillmentMethod: b.fulfillmentMethod, deliveryAddress: b.deliveryAddress, deliveryProvince: b.deliveryProvince,
        deliveryPostalCode: b.deliveryPostalCode, deliveryRateId: null };
      await this._bkFulPreview();
    },

    _bkFulRead() {
      const m = this._bkManage; const f = document.getElementById('sfFulChangeForm');
      if (!m || !f) return;
      const v = (n) => { const e = f.querySelector(`[name="${n}"]`); const t = e ? String(e.value || '').trim() : ''; return t === '' ? null : t; };
      const picked = f.querySelector('input[name="fcMethod"]:checked');
      m.values = {
        fulfillmentMethod: picked ? picked.value : m.values.fulfillmentMethod,
        deliveryAddress: v('fcAddress'), deliveryProvince: v('fcProvince'), deliveryPostalCode: v('fcPostal'), deliveryRateId: v('fcRate'),
      };
    },

    async _bkFulPreview() {
      const m = this._bkManage; if (!m) return;
      try {
        const r = await fetch(this._url('/booking/bookings/by-token/' + encodeURIComponent(m.token) + '/fulfillment'), {
          method: 'POST', headers: { 'Content-Type': 'application/json' }, body: JSON.stringify(Object.assign({}, m.values, { previewOnly: true })) });
        const j = await r.json().catch(() => null);
        if (!r.ok) throw new Error((j && j.message) || 'คิดค่าจัดส่งไม่สำเร็จ');
        m.quote = j.data;
        this._bkFulDraw();
      } catch (e) {
        const f = document.getElementById('sfFulChangeForm');
        if (f) f.innerHTML = `<div style="color:#DC2626;font-size:13px;margin-top:8px">${this.esc(e.message)}</div>`;
      }
    },
    _bkFulPreviewSoon() { clearTimeout(this._bkFulTimer); this._bkFulTimer = setTimeout(() => { this._bkFulRead(); this._bkFulPreview(); }, 450); },

    _bkFulDraw() {
      const m = this._bkManage; const f = document.getElementById('sfFulChangeForm');
      if (!m || !f) return;
      const res = m.quote || {}; const q = res.quote || {}; const v = m.values || {};
      const method = q.method || v.fulfillmentMethod;
      const radio = (val, label) => `<label style="display:flex;gap:6px;align-items:center;font-weight:600">
          <input type="radio" name="fcMethod" value="${val}" ${method === val ? 'checked' : ''} onchange="Store._bkFulRead();Store._bkFulPreview()"> ${label}</label>`;
      const input = 'width:100%;padding:10px;border:1px solid #D1D5DB;border-radius:6px';
      const opts = Array.isArray(q.rateOptions) ? q.rateOptions : [];
      f.innerHTML = `<div style="display:grid;gap:8px;margin-top:10px">
          ${q.deliveryEnabled && q.pickupEnabled ? `<div style="display:flex;gap:16px;flex-wrap:wrap">${radio('delivery', 'จัดส่ง')}${radio('pickup', 'รับเองที่ร้าน')}</div>` : ''}
          ${method === 'delivery' ? `<div><label>ที่อยู่จัดส่ง</label><textarea name="fcAddress" rows="2" maxlength="1000" style="${input}" oninput="Store._bkFulPreviewSoon()">${this.esc(v.deliveryAddress || '')}</textarea></div>
            <div style="display:flex;gap:8px;flex-wrap:wrap">
              <div style="flex:2;min-width:140px"><label>จังหวัด</label><input name="fcProvince" maxlength="100" style="${input}" value="${this.esc(v.deliveryProvince || '')}" oninput="Store._bkFulPreviewSoon()"></div>
              <div style="flex:1;min-width:110px"><label>รหัสไปรษณีย์</label><input name="fcPostal" maxlength="5" inputmode="numeric" style="${input}" value="${this.esc(v.deliveryPostalCode || '')}" oninput="Store._bkFulPreviewSoon()"></div>
            </div>
            ${opts.length > 1 ? `<div><label>วิธีจัดส่ง</label><select name="fcRate" style="${input}" onchange="Store._bkFulRead();Store._bkFulPreview()">
              <option value="">— เลือกวิธีจัดส่ง —</option>
              ${opts.map(o => `<option value="${this.esc(o.rateId)}" ${o.rateId === q.selectedRateId ? 'selected' : ''}>${this.esc(o.name)}${o.zoneName ? ' · ' + this.esc(o.zoneName) : ''} — ${o.isFree ? 'ส่งฟรี' : '฿' + this._money(o.fee)}</option>`).join('')}
            </select></div>` : ''}` : ''}
          ${method === 'pickup' && q.pickupNote ? `<div style="font-size:13px;color:var(--text-secondary)">📍 ${this.esc(q.pickupNote)}</div>` : ''}
          ${res.problem ? `<div style="color:#B45309;font-size:13px">⚠️ ${this.esc(res.problem)}</div>` : ''}
          ${!res.problem && res.effect ? `<div style="font-size:13px;background:#F9FAFB;border-radius:8px;padding:8px 10px">${this.esc(res.effect)}</div>` : ''}
          <div style="display:flex;gap:8px;flex-wrap:wrap">
            <button class="btn btn-primary" ${res.allowed ? '' : 'disabled'} onclick="Store.bookingFulfillApply(this)">ยืนยันเปลี่ยนวิธีรับ</button>
            <button class="btn" onclick="Store.renderBookingByToken('${this.jsArg(m.token)}')">ยกเลิก</button>
          </div></div>`;
    },

    async bookingFulfillApply(btn) {
      const m = this._bkManage; if (!m) return;
      this._bkFulRead();
      return SubmitOnce.run(btn, async () => {
        try {
          const r = await SubmitOnce.fetch(this._url('/booking/bookings/by-token/' + encodeURIComponent(m.token) + '/fulfillment'), {
            method: 'POST', headers: { 'Content-Type': 'application/json' },
            // ฝ่ายค้านรอบ 228 S6: ส่งยอดที่ลูกค้าเห็นในตัวอย่าง — เซิร์ฟเวอร์ไม่บันทึกถ้ายอดเปลี่ยน (409)
            body: JSON.stringify(Object.assign({}, m.values, { previewOnly: false,
              expectedNewFee: m.quote ? m.quote.newFee : null, expectedDepositAmount: m.quote ? m.quote.newDepositAmount : null })) }, btn);
          const j = await r.json().catch(() => null);
          if (r.status === 409) { await this._bkFulPreview(); throw new Error((j && j.message) || 'ยอดเปลี่ยน — ตรวจอีกครั้ง'); }
          if (!r.ok) throw new Error((j && j.message) || 'เปลี่ยนวิธีรับไม่สำเร็จ');
          await this.renderBookingByToken(m.token, ((j && j.message) || 'เปลี่ยนวิธีรับแล้ว') + (j && j.data && j.data.effect ? ' — ' + j.data.effect : ''));
        } catch (e) { alert(e.message); return false; }
      }, { busyText: 'กำลังบันทึก…' });
    },
  };

  window.StoreBookingManage = {
    install(store) {
      if (!store || store._bkManageInstalled) return;
      Object.assign(store, methods);
      store._bkManageInstalled = true;
    },
  };
})();
