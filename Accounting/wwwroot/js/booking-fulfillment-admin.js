/* รอบ 226 — จัดส่ง / รับเองของการจอง (ฝั่งร้าน)
 * - ตั้งค่าเว็บ › การชำระเงิน (cms-site-settings · ต่อท้ายหมวดผ่าน BookingFulfillAdmin.mount): เปิดจัดส่ง/รับเอง · ค่าตั้งต้น · ที่อยู่/เวลารับเอง · ยอดขั้นต่ำจัดส่ง
 *   ค่าจัดส่งใช้ "โซน/อัตราค่าส่ง" + ค่าส่งเหมาจ่าย/ส่งฟรีเมื่อครบของร้าน (ตั้งค่าร้านค้า) — ตัวคิดเดียวกับคำสั่งซื้อ (Helpers/ShopDeliveryFee)
 * - cms-edit ฟอร์มบริการ: ตามค่าเว็บ / กำหนดเอง (#bsFulfillWrap สร้างต่อจาก #bsPayWrap)
 * ค่า/ข้อความตัดสินที่เซิร์ฟเวอร์ (Helpers/BookingFulfillment) — ไฟล์นี้แสดง/ส่งค่าอย่างเดียว */
(function () {
  const esc = (v) => (window.Layout && Layout.esc ? Layout.esc(v) : String(v == null ? '' : v));
  const $ = (id) => document.getElementById(id);
  const base = (siteId) => `/api/companies/${Layout.getCompanyId()}/cms/sites/${siteId}/booking`;

  window.BookingFulfillAdmin = {
    _siteId: null,

    // ═══════════ ตั้งค่าเว็บ ═══════════
    async mount(el, ctx) {
      this._siteId = ctx && ctx.siteId;
      if (!el || !this._siteId) return;
      let card = $('bkSecFulfill');
      if (!card) {
        card = document.createElement('div');
        card.className = 'sg-card';
        card.id = 'bkSecFulfill';
        const first = el.firstElementChild;
        if (first) first.insertAdjacentElement('afterend', card); else el.appendChild(card);
      }
      card.innerHTML = `<h3 style="font-size:16px;font-weight:600;margin:0 0 6px">🚚 จัดส่ง / รับเองของการจอง</h3>
        <div class="bs-hint" style="margin-bottom:8px">หลังเลือกเวลา ลูกค้าเลือก “จัดส่ง” (กรอกที่อยู่ ระบบคิดค่าส่งให้) หรือ “รับเองที่ร้าน” · ปิดทั้งคู่ = ไม่มีขั้นนี้ (การจองนัดหมายทั่วไป) ·
          บริการแต่ละตัวตั้งทับได้ที่ฟอร์มบริการ · ค่าจัดส่งแยกบรรทัดในใบยืนยัน/เอกสาร · มัดจำ (%) คิดจากค่าบริการ — ชำระเต็มจำนวนรวมค่าจัดส่ง</div>
        <div id="bfulProblem"></div>
        <div class="form-row">
          <div class="form-group"><label style="font-size:13px"><input type="checkbox" id="bfulDelivery"> เปิด “จัดส่ง”</label></div>
          <div class="form-group"><label style="font-size:13px"><input type="checkbox" id="bfulPickup"> เปิด “รับเองที่ร้าน”</label></div>
          <div class="form-group"><label class="form-label">เลือกไว้ก่อน</label>
            <select class="form-input" id="bfulDefault"><option value="">ให้ลูกค้าเลือกเอง</option><option value="delivery">จัดส่ง</option><option value="pickup">รับเองที่ร้าน</option></select></div>
        </div>
        <div class="form-row">
          <div class="form-group"><label class="form-label">ที่อยู่ / เวลารับเอง (แสดงลูกค้า)</label>
            <textarea class="form-input" id="bfulPickupNote" rows="2" maxlength="1000" placeholder="เช่น รับที่ร้านสาขาสยาม ชั้น 1 · 10:00–18:00 น."></textarea></div>
          <div class="form-group"><label class="form-label">ยอดค่าบริการขั้นต่ำสำหรับจัดส่ง (บาท · ว่าง = ไม่กำหนด)</label>
            <input type="number" class="form-input" id="bfulMin" min="0" step="0.01"></div>
        </div>
        <div class="bs-hint" id="bfulFeeSource"></div>
        <div id="bfulServices" style="overflow-x:auto;margin-top:6px"></div>
        <button type="button" class="btn btn-primary btn-sm" style="margin-top:8px" onclick="BookingFulfillAdmin.save()">บันทึกการจัดส่ง/รับเอง</button>`;
      await this.load();
    },
    async load() {
      try {
        const d = (await API.get(`${base(this._siteId)}/fulfillment-settings`)).data || {};
        $('bfulDelivery').checked = d.deliveryEnabled === true;
        $('bfulPickup').checked = d.pickupEnabled === true;
        $('bfulDefault').value = d.defaultMethod || '';
        $('bfulPickupNote').value = d.pickupNote || '';
        $('bfulMin').value = d.deliveryMinAmount == null ? '' : d.deliveryMinAmount;
        const id = encodeURIComponent(this._siteId);
        $('bfulFeeSource').innerHTML = `${esc(d.feeSourceSummary || '')} · <a href="/pages/cms-store-settings.html?siteId=${id}&tab=shipping" style="color:var(--primary)">โซน/อัตราค่าส่งของร้าน →</a>`;
        const rows = d.services || [];
        $('bfulServices').innerHTML = rows.length ? `<table class="table" style="font-size:12px"><thead><tr><th>บริการ</th><th>ผลที่มีจริง</th></tr></thead><tbody>${rows.map(r =>
          `<tr><td>${esc(r.name)}</td><td>${esc(r.summary)}</td></tr>`).join('')}</tbody></table>` : '';
        $('bfulProblem').innerHTML = '';
      } catch (e) {
        $('bfulProblem').innerHTML = `<div class="bs-hint" style="color:#b91c1c">โหลดค่าตั้งจัดส่ง/รับเองไม่สำเร็จ: ${esc(e.message)}</div>`;
      }
    },
    async save() {
      const min = $('bfulMin').value === '' ? null : Number($('bfulMin').value);
      const body = {
        deliveryEnabled: $('bfulDelivery').checked, pickupEnabled: $('bfulPickup').checked,
        defaultMethod: $('bfulDefault').value || null, pickupNote: $('bfulPickupNote').value.trim() || null,
        deliveryMinAmount: Number.isFinite(min) ? min : null,
      };
      try {
        const res = await API.put(`${base(this._siteId)}/fulfillment-settings`, body);
        Layout.toast(res.message || 'บันทึกแล้ว', 'success');
        await this.load();
      } catch (e) {
        $('bfulProblem').innerHTML = `<div style="background:#fef2f2;border:1px solid #fca5a5;color:#991b1b;border-radius:8px;padding:10px 12px;font-size:13px;margin-bottom:8px">⚠️ ${esc(e.message)}</div>`;
        Layout.toast(e.message, 'error', 10000);
      }
    },

    // ═══════════ cms-edit: ฟอร์มบริการ (ตามค่าเว็บ / กำหนดเอง) ═══════════
    _ensureServiceMarkup() {
      let wrap = $('bsFulfillWrap');
      if (wrap) return wrap;
      const anchor = $('bsPayWrap');
      if (!anchor) return null;
      wrap = document.createElement('div');
      wrap.id = 'bsFulfillWrap';
      wrap.innerHTML = `<div class="form-group" style="margin-top:10px"><label class="form-label">🚚 จัดส่ง / รับเองของบริการนี้</label>
          <select class="form-input" id="bsFulMode" onchange="BookingFulfillAdmin.onServiceModeChange()">
            <option value="Inherit">ตามค่าเว็บ</option><option value="Custom">กำหนดเอง</option></select>
          <div id="bsFulCustom" style="display:none;margin-top:6px">
            <label style="font-size:13px;margin-right:12px"><input type="checkbox" id="bsFulDelivery"> จัดส่ง</label>
            <label style="font-size:13px;margin-right:12px"><input type="checkbox" id="bsFulPickup"> รับเองที่ร้าน</label>
            <select class="form-input" id="bsFulDefault" style="width:auto;display:inline-block"><option value="">ให้ลูกค้าเลือกเอง</option><option value="delivery">เลือกจัดส่งไว้ก่อน</option><option value="pickup">เลือกรับเองไว้ก่อน</option></select>
            <div class="bs-hint">ปิดทั้งคู่ = บริการนี้ไม่มีขั้นจัดส่ง/รับเอง แม้เว็บเปิดไว้</div>
          </div>
          <div class="bs-hint" id="bsFulHint"></div></div>`;
      anchor.insertAdjacentElement('afterend', wrap);
      return wrap;
    },
    hydrateService(s) {
      if (!this._ensureServiceMarkup()) return;
      $('bsFulMode').value = s && s.fulfillmentMode === 'Custom' ? 'Custom' : 'Inherit';
      $('bsFulDelivery').checked = !!(s && s.fulfillmentDeliveryEnabled);
      $('bsFulPickup').checked = !!(s && s.fulfillmentPickupEnabled);
      $('bsFulDefault').value = (s && s.fulfillmentDefault) || '';
      $('bsFulHint').textContent = s && s.fulfillmentSummary ? 'ผลที่มีจริง: ' + s.fulfillmentSummary : '';
      this.onServiceModeChange();
    },
    onServiceModeChange() {
      const c = $('bsFulCustom'); if (c) c.style.display = ($('bsFulMode') || {}).value === 'Custom' ? '' : 'none';
    },
    servicePayload() {
      if (!$('bsFulfillWrap')) return {};
      const mode = $('bsFulMode').value === 'Custom' ? 'Custom' : 'Inherit';
      return mode === 'Inherit' ? { fulfillmentMode: 'Inherit' } : {
        fulfillmentMode: 'Custom', fulfillmentDeliveryEnabled: $('bsFulDelivery').checked, fulfillmentPickupEnabled: $('bsFulPickup').checked,
        fulfillmentDefault: $('bsFulDefault').value || null,
      };
    },
  };
})();
