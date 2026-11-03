/* รอบ 226 — ขั้น "จัดส่ง / รับเอง" ของการจองบนหน้าร้าน (ฝั่งลูกค้า · เช่น เบเกอรี่สั่งทำ)
 * - วาดจาก payment-preview.fulfillment ของเซิร์ฟเวอร์ (Helpers/BookingFulfillment + ตัวคิดค่าส่งของร้าน Helpers/ShopDeliveryFee) — ไฟล์นี้แสดง/ส่งค่าอย่างเดียว
 * - ค่าที่ส่งกลับ: วิธีรับ · ที่อยู่ · จังหวัด · รหัสไปรษณีย์ · อัตราที่เลือก — ไม่มีช่องยอดค่าส่ง (เซิร์ฟเวอร์คิดใหม่ทุกครั้งตอนจอง)
 * - สมาชิกที่ล็อกอิน: เติมที่อยู่จัดส่งจากโปรไฟล์ (รอบ 216) เฉพาะช่องที่ว่าง/ระบบเติมเอง — ค่าที่ผู้ใช้พิมพ์ชนะเสมอ (dataset.userTouched)
 * ต่อเข้ากับ Store ผ่าน StoreBookingFulfill.install(Store) — เรียกจาก storefront-booking-pay.js (_bkPayPreview) */
(function () {
  const methods = {
    _bkFulfillBox() {
      const form = document.getElementById('sfBookForm');
      if (!form) return null;
      let box = document.getElementById('sfBookFulfillBox');
      if (!box) {
        box = document.createElement('div');
        box.id = 'sfBookFulfillBox';
        const anchor = document.getElementById('sfBookPayPreview') || document.getElementById('sfBookSubmit');
        if (anchor) anchor.insertAdjacentElement('beforebegin', box); else form.appendChild(box);
      }
      return box;
    },

    /** วาด/อัปเดตกล่องจากคำตอบ payment-preview · ช่องที่ผู้ใช้พิมพ์ไม่ถูกวาดใหม่ (วาดโครงครั้งเดียวต่อบริการ แล้วอัปเดตเฉพาะส่วนที่เซิร์ฟเวอร์คิด) */
    _bkFulfillRender(p) {
      const f = p && p.fulfillment;
      const existing = document.getElementById('sfBookFulfillBox');
      if (!f || !f.enabled) { if (existing) existing.remove(); return; }
      const box = this._bkFulfillBox();
      if (!box) return;
      const sid = (document.querySelector('#sfBookForm input[name="serviceId"]') || {}).value || '';
      if (box.dataset.serviceId !== sid) {
        box.dataset.serviceId = sid;
        const both = f.deliveryEnabled && f.pickupEnabled;
        const radio = (v, label) => `<label style="display:flex;gap:6px;align-items:center;font-weight:600">
            <input type="radio" name="fulfillmentMethod" value="${v}" ${f.method === v ? 'checked' : ''} onchange="Store._bkFulfillChanged()"> ${label}</label>`;
        box.innerHTML = `<div style="border:1px solid #D1D5DB;border-radius:10px;padding:12px 14px;display:grid;gap:8px">
            <div style="font-weight:600">🚚 รับสินค้า/บริการอย่างไร</div>
            ${both ? `<div style="display:flex;gap:16px;flex-wrap:wrap">${radio('delivery', 'จัดส่ง')}${radio('pickup', 'รับเองที่ร้าน')}</div>`
              : `<input type="hidden" name="fulfillmentMethod" value="${f.deliveryEnabled ? 'delivery' : 'pickup'}"><div style="font-size:13px">${f.deliveryEnabled ? 'จัดส่งถึงที่อยู่ของคุณ' : 'รับเองที่ร้าน'}</div>`}
            <div id="sfBookDeliveryFields" style="display:none;gap:8px">
              <div><label>ที่อยู่จัดส่ง <span style="color:#DC2626">*</span></label><textarea name="deliveryAddress" rows="2" oninput="this.dataset.userTouched='1';delete this.dataset.autoSrc" maxlength="1000" placeholder="บ้านเลขที่ ถนน ตำบล/แขวง อำเภอ/เขต" style="width:100%;padding:10px;border:1px solid #D1D5DB;border-radius:6px"></textarea></div>
              <div style="display:flex;gap:8px;flex-wrap:wrap">
                <div style="flex:2;min-width:140px"><label>จังหวัด <span style="color:#DC2626">*</span></label><input name="deliveryProvince" maxlength="100" oninput="this.dataset.userTouched='1';delete this.dataset.autoSrc;Store._bkFulfillChangedSoon()" style="width:100%;padding:10px;border:1px solid #D1D5DB;border-radius:6px"></div>
                <div style="flex:1;min-width:110px"><label>รหัสไปรษณีย์ <span style="color:#DC2626">*</span></label><input name="deliveryPostalCode" maxlength="5" inputmode="numeric" oninput="this.dataset.userTouched='1';delete this.dataset.autoSrc;Store._bkFulfillChangedSoon()" style="width:100%;padding:10px;border:1px solid #D1D5DB;border-radius:6px"></div>
              </div>
              <div id="sfBookDeliveryRates"></div>
            </div>
            <div id="sfBookPickupNote" style="display:none;font-size:13px;color:var(--text-secondary)"></div>
            <div id="sfBookFulfillFee" style="font-size:13px"></div>
          </div>`;
        this._bkFulfillPrefill();
      }
      const method = this._bkFulfillMethod() || f.method || null;
      const del = document.getElementById('sfBookDeliveryFields');
      if (del) del.style.display = method === 'delivery' ? 'grid' : 'none';
      const pn = document.getElementById('sfBookPickupNote');
      if (pn) {
        pn.style.display = method === 'pickup' ? '' : 'none';
        pn.textContent = method === 'pickup' ? '📍 ' + (f.pickupNote || 'รับที่ร้าน — ร้านจะแจ้งรายละเอียดการรับ') : '';
      }
      const rates = document.getElementById('sfBookDeliveryRates');
      if (rates) {
        const opts = Array.isArray(f.rateOptions) ? f.rateOptions : [];
        rates.innerHTML = method === 'delivery' && opts.length > 1
          ? `<label>วิธีจัดส่ง</label><select name="deliveryRateId" onchange="Store._bkFulfillChanged()" style="width:100%;padding:10px;border:1px solid #D1D5DB;border-radius:6px">
              <option value="">— เลือกวิธีจัดส่ง —</option>
              ${opts.map(o => `<option value="${this.esc(o.rateId)}" ${o.rateId === f.selectedRateId ? 'selected' : ''}>${this.esc(o.name)}${o.zoneName ? ' · ' + this.esc(o.zoneName) : ''} — ${o.isFree ? 'ส่งฟรี' : '฿' + this._money(o.fee)}</option>`).join('')}
            </select>` : '';
      }
      const fee = document.getElementById('sfBookFulfillFee');
      if (fee) {
        fee.innerHTML = f.problem
          ? `<div style="color:#B45309">⚠️ ${this.esc(f.problem)}</div>`
          : method === 'delivery'
            ? `ค่าจัดส่ง: <strong>${Number(f.deliveryFee) > 0 ? '฿' + this._money(f.deliveryFee) : 'ส่งฟรี'}</strong> · ยอดรวม ฿${this._money(p.grandTotal)}`
            : '';
      }
    },

    _bkFulfillMethod() {
      const box = document.getElementById('sfBookFulfillBox');
      if (!box) return null;
      const r = box.querySelector('input[name="fulfillmentMethod"]:checked') || box.querySelector('input[type="hidden"][name="fulfillmentMethod"]');
      return r ? r.value : null;
    },

    _bkFulfillChanged() {
      if (typeof this._bkPayPreview === 'function') this._bkPayPreview();
    },
    _bkFulfillChangedSoon() {
      clearTimeout(this._bkFulfillTimer);
      this._bkFulfillTimer = setTimeout(() => this._bkFulfillChanged(), 450);
    },

    /** ค่าที่ส่งไปกับ payment-preview และตอนกดจอง — ไม่มีขั้นนี้ ⇒ {} */
    _bkFulfillExtras() {
      const box = document.getElementById('sfBookFulfillBox');
      if (!box) return {};
      const v = (n) => { const el = box.querySelector(`[name="${n}"]`); const t = el ? String(el.value || '').trim() : ''; return t === '' ? null : t; };
      const method = this._bkFulfillMethod();
      const out = { fulfillmentMethod: method };
      if (method === 'delivery') {
        out.deliveryAddress = v('deliveryAddress');
        out.deliveryProvince = v('deliveryProvince');
        out.deliveryPostalCode = v('deliveryPostalCode');
        out.deliveryRateId = v('deliveryRateId');
      }
      return out;
    },

    /** สมาชิก: เติมที่อยู่จัดส่งจากโปรไฟล์ (ช่องว่าง/ช่องที่ระบบเติมเท่านั้น — ผู้ใช้พิมพ์แล้วไม่ทับ) แล้วคิดค่าส่งใหม่ */
    async _bkFulfillPrefill() {
      if (typeof this._custMeLoad !== 'function') return;
      const me = await this._custMeLoad();
      const box = document.getElementById('sfBookFulfillBox');
      if (!me || !box || !box.isConnected) return;
      const sa = me.shippingAddress || {};
      let filled = false;
      for (const [name, val] of [['deliveryAddress', me.shippingAddressText], ['deliveryProvince', sa.province], ['deliveryPostalCode', sa.postalCode]]) {
        const el = box.querySelector(`[name="${name}"]`);
        if (!el || val == null || val === '' || el.dataset.userTouched === '1') continue;
        if (el.value && !el.dataset.autoSrc) continue;
        if (el.value !== String(val)) filled = true;
        el.value = String(val); el.dataset.autoSrc = 'member';
      }
      if (filled && this._bkFulfillMethod() === 'delivery') this._bkFulfillChanged();
    },
  };

  window.StoreBookingFulfill = {
    install(store) {
      if (!store || store._bkFulfillInstalled) return;
      Object.assign(store, methods);
      store._bkFulfillInstalled = true;
    },
  };
})();
