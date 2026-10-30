/* รอบ 218 (คำตัดสินข้อ 177 · erp-review/2026-10-30/BOOKING_PAYMENT_DESIGN.md) — การชำระล่วงหน้าของการจองบนหน้าร้าน (ฝั่งลูกค้า)
 * - ก่อนกดจอง: POST booking/payment-preview ⇒ ยอดที่ต้องจ่าย · บังคับไหม · กำหนดเวลา · ช่องทาง · นโยบายยกเลิก (มัดจำไม่บังคับ = ติ๊กเลือกจ่าย)
 * - หลังจอง / หน้า my-booking: กล่องชำระจาก booking.payment (ชำระออนไลน์ · เลขบัญชี · QR พร้อมเพย์ที่มียอด · แนบสลิป · สถานะ)
 * ค่าทุกตัวมาจากเซิร์ฟเวอร์ (Helpers/BookingPaymentPolicy) — ไฟล์นี้แสดงอย่างเดียว · ต่อเข้ากับ Store ผ่าน StoreBookingPay.install(Store)
 * (ไฟล์แยกจาก storefront.html เพื่อลดการชนกับทีมอื่น) */
(function () {
  const methods = {
    // ── ก่อนกดจอง ──
    async _bkPayPreview() {
      const form = document.getElementById('sfBookForm');
      if (!form) return;
      let box = document.getElementById('sfBookPayPreview');
      if (!box) {
        box = document.createElement('div');
        box.id = 'sfBookPayPreview';
        const btn = document.getElementById('sfBookSubmit');
        if (btn) btn.insertAdjacentElement('beforebegin', box); else form.appendChild(box);
      }
      const sidEl = form.querySelector('input[name="serviceId"]');
      const serviceId = sidEl ? sidEl.value : '';
      this._bkPay = null;
      box.innerHTML = '';
      if (!serviceId) return;
      try {
        const r = await fetch(this._url('/booking/payment-preview'), {
          method: 'POST', headers: { 'Content-Type': 'application/json' },
          body: JSON.stringify({ bookingServiceId: serviceId, units: this._bkUnits() })
        });
        if (!r.ok) return;
        const p = (await r.json()).data;
        this._bkPay = p;
        if (!p) return;
        const policy = p.cancellationPolicy ? `<div style="font-size:12px;color:var(--text-secondary);margin-top:6px">นโยบายยกเลิก: ${this.esc(p.cancellationPolicy)}</div>` : '';
        const note = p.paymentNote ? `<div style="font-size:12px;color:var(--text-secondary);margin-top:4px">${this.esc(p.paymentNote)}</div>` : '';
        if (!p.collects) { box.innerHTML = policy ? `<div style="margin:4px 0">${policy}</div>` : ''; return; }
        const channels = [p.gatewayAvailable ? 'ชำระออนไลน์' : null, p.transferAvailable ? 'โอน/สแกน QR แล้วแนบสลิป' : null].filter(Boolean).join(' หรือ ');
        const pick = p.optional
          ? `<label style="display:flex;gap:6px;align-items:center;font-weight:600;margin-top:6px"><input type="checkbox" id="sfBookPayNow"> จ่ายมัดจำ ฿${this._money(p.amountDue)} ตอนนี้ (ไม่บังคับ)</label>`
          : '';
        box.innerHTML = `<div style="border:1px solid ${p.required ? '#F59E0B' : '#A7F3D0'};background:${p.required ? '#FFFBEB' : '#ECFDF5'};border-radius:10px;padding:12px 14px;font-size:14px">
            <div style="font-weight:600">💳 ${this.esc(p.summary || p.modeLabel || '')}</div>
            <div style="font-size:12px;color:var(--text-secondary);margin-top:4px">ค่าบริการรวม ฿${this._money(p.totalAmount)}${channels ? ' · ช่องทาง: ' + this.esc(channels) : ''}</div>
            ${pick}${policy}${note}
          </div>`;
      } catch (_) { box.innerHTML = ''; }
    },
    /** ค่าที่ส่งเพิ่มตอนจอง (เรียกจาก _submitBooking) — มัดจำไม่บังคับ: ลูกค้าเลือกจ่ายไหม */
    _bkPayExtras() {
      const cb = document.getElementById('sfBookPayNow');
      return cb ? { payDepositNow: cb.checked } : {};
    },

    // ── หลังจอง / หน้า my-booking ──
    async _renderBookingPayBox(booking, boxId) {
      const box = document.getElementById(boxId || 'sfBookingPayBox');
      if (!box) return true;
      const p = booking && booking.payment;
      const token = booking && booking.publicToken;
      if (!p || !token) return false;   // คำตอบรูปเดิม ⇒ ให้ตัวเดิมแสดง
      const due = Number(p.amountDue || 0);
      if (!(due > 0) && !p.slipPending && !p.statusLabel) { box.innerHTML = ''; return true; }
      const parts = [];
      parts.push(`<div style="font-weight:600;font-size:15px">💳 ${this.esc(p.statusLabel || 'การชำระเงิน')}</div>`);
      if (p.summary) parts.push(`<div style="font-size:13px;margin-top:4px">${this.esc(p.summary)}</div>`);
      if (p.slipRejectedReason && !p.slipPending)
        parts.push(`<div style="margin-top:8px;background:#FEF2F2;border:1px solid #FCA5A5;color:#991B1B;border-radius:8px;padding:8px 10px;font-size:13px">สลิปก่อนหน้าไม่ผ่าน: ${this.esc(p.slipRejectedReason)} — แนบสลิปใหม่หรือชำระออนไลน์</div>`);
      if (p.slipPending)
        parts.push(`<div style="margin-top:8px;font-size:13px">⏳ แนบสลิปแล้ว — รอร้านตรวจสอบ · <a href="${this.esc(this._url('/booking/bookings/by-token/' + encodeURIComponent(token) + '/slip'))}" target="_blank" rel="noopener">ดูสลิปที่ส่ง</a></div>`);
      if (p.gatewayAvailable) {
        const m = await this._payMethods();
        if (m && m.online && (m.methods || []).length)
          parts.push(`<div style="margin-top:10px"><div style="font-size:13px;font-weight:600">ชำระออนไลน์ ฿${this._money(due)} — ยืนยันการจองอัตโนมัติ${m.isTestMode ? ' <span style="color:#92400e">(โหมดทดสอบ)</span>' : ''}</div>
            <div style="display:flex;gap:8px;flex-wrap:wrap;margin-top:6px">${(m.methods || []).map(x => `<button class="btn btn-primary" onclick="Store.bookingPayOnline('${this.jsArg(token)}', '${this.jsArg(x)}')">${this.esc(this._payLabel(x))}</button>`).join('')}</div></div>`);
      }
      const t = p.transfer;
      if (p.transferAvailable && t) {
        const qr = p.promptPayQrAvailable ? this._promptPayQrSrc(due) : (t.qrImageUrl || null);
        parts.push(`<div style="margin-top:10px;display:grid;gap:10px">
            ${qr ? `<div style="text-align:center;background:#F9FAFB;padding:14px;border-radius:8px">
              <div style="font-size:13px;color:var(--text-secondary);margin-bottom:6px">${p.promptPayQrAvailable ? 'สแกน QR พร้อมเพย์ (ยอด ฿' + this._money(due) + ')' : 'สแกน QR โอนเงิน — กรอกยอด ฿' + this._money(due)}</div>
              <img src="${this.esc(qr)}" alt="QR โอนเงิน" style="width:220px;max-width:100%;height:auto" onerror="this.style.display='none'">
              ${t.promptPayId ? `<div style="font-size:12px;color:var(--text-secondary);margin-top:6px">พร้อมเพย์: <strong>${this.esc(t.promptPayId)}</strong></div>` : ''}
            </div>` : ''}
            ${t.accountNumber ? `<div style="background:#F9FAFB;padding:12px 14px;border-radius:8px;font-size:13px">
              <div style="font-weight:600;margin-bottom:4px">โอนเข้าบัญชีธนาคาร ยอด ฿${this._money(due)}</div>
              <div>ธนาคาร: <strong>${this.esc(t.bankName || '-')}</strong>${t.branch ? ' · สาขา ' + this.esc(t.branch) : ''}</div>
              <div>เลขบัญชี: <strong style="font-family:monospace">${this.esc(t.accountNumber)}</strong></div>
              <div>ชื่อบัญชี: <strong>${this.esc(t.accountName || '-')}</strong></div>
            </div>` : ''}
          </div>`);
      }
      if (p.canUploadSlip)
        parts.push(`<form onsubmit="Store.bookingUploadSlip(event, '${this.jsArg(token)}')" style="display:flex;gap:8px;align-items:center;flex-wrap:wrap;margin-top:10px">
            <input type="file" name="file" accept="image/*,.pdf" required style="flex:1;min-width:200px">
            <button type="submit" class="btn btn-primary">📎 ส่งสลิป</button>
            <div class="sf-slip-status" style="font-size:13px;width:100%"></div>
          </form>`);
      if (p.paymentNote) parts.push(`<div style="font-size:12px;color:var(--text-secondary);margin-top:8px">${this.esc(p.paymentNote)}</div>`);
      if (due > 0 && !p.gatewayAvailable && !p.transferAvailable && !p.slipPending)
        parts.push(`<div style="font-size:13px;margin-top:8px;color:#78350F">ร้านยังไม่ได้ตั้งช่องทางชำระออนไลน์/โอน — ติดต่อร้านเพื่อชำระเงิน (เลขที่การจอง ${this.esc(booking.bookingNumber || '')})</div>`);
      box.innerHTML = `<div style="margin-top:14px;border:2px solid ${due > 0 && !p.slipPending ? '#F59E0B' : '#16a34a'};border-radius:12px;padding:14px 18px;text-align:left">${parts.join('')}</div>`;
      return true;
    },
    // รอบ 217 รวม: ส่งครั้งเดียว (SubmitOnce — ล็อกฟอร์ม + Idempotency-Key ต่อฟอร์ม · ลองใหม่ใช้คีย์เดิม) · ด่านจริงที่เซิร์ฟเวอร์ (สลิปค้าง = ปฏิเสธ)
    async bookingUploadSlip(ev, token) {
      ev.preventDefault();
      const form = ev.target;
      if (window.SubmitOnce && !SubmitOnce.claim(form)) return;
      const status = form.querySelector('.sf-slip-status');
      if (status) status.textContent = 'กำลังส่งสลิป…';
      try {
        const url = this._url('/booking/bookings/by-token/' + encodeURIComponent(token) + '/slip');
        const init = { method: 'POST', body: new FormData(form) };
        const r = window.SubmitOnce ? await SubmitOnce.fetch(url, init, form) : await fetch(url, init);
        const j = await r.json().catch(() => null);
        if (!r.ok) throw new Error((j && j.message) || 'ส่งสลิปไม่สำเร็จ');
        if (window.SubmitOnce) SubmitOnce.reset(form);   // สลิปใบถัดไป = คำขอใหม่
        await this.renderBookingByToken(token, (j && j.message) || 'ส่งสลิปแล้ว — ร้านจะตรวจสอบและยืนยันการจอง');
      } catch (e) {
        if (status) status.textContent = e.message;
      } finally {
        if (window.SubmitOnce) SubmitOnce.release(form);
      }
    },
  };

  window.StoreBookingPay = {
    /** ต่อเข้ากับ Store (เรียกก่อน Store.init) — ห่อ _pickSlot ให้แสดงยอดก่อนกดจอง · แทนกล่องจ่ายเดิมเมื่อคำตอบมี booking.payment */
    install(store) {
      if (!store || store._bkPayInstalled) return;
      Object.assign(store, methods);
      const pick = store._pickSlot;
      if (typeof pick === 'function')
        store._pickSlot = function () { const r = pick.apply(this, arguments); this._bkPayPreview(); return r; };
      const legacy = store._renderBookingPayOnline;
      store._renderBookingPayOnline = async function (booking, boxId) {
        if (await this._renderBookingPayBox(booking, boxId)) return;
        if (typeof legacy === 'function') return legacy.call(this, booking, boxId);
      };
      store._bkPayInstalled = true;
    },
  };
})();
