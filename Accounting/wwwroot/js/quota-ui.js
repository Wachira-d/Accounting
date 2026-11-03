/**
 * quota-ui.js — รอบ 229 (QUOTA_ADDON_SUBSCRIPTION_AUDIT §D.3) — ตัวแสดงโควตา + หน้าต่างซื้อโควตาเสริม + รายการต่ออายุ ตัวเดียวของทุกหน้า
 *
 * กติกา (F2 ข้อ 5 — server computes · page displays):
 *  • ตัวเลขทุกตัว (ใช้ไป · เพดาน · ระดับเตือน · ราคา · ยอดคิดตามวัน · ข้อความ) มาจากเซิร์ฟเวอร์ — ไฟล์นี้ไม่มีสูตร/เกณฑ์ "ไม่จำกัด" ของตัวเอง
 *  • โหลดไม่ได้ = "โหลดไม่ได้" ไม่ใช่ "ไม่จำกัด" (D-4 · team.html เดิมตกเป็น 999)
 *  • ซื้อ = ส่งยอดที่เห็นกลับไปเทียบ (ไม่ตรง ⇒ 409 วาดข้อเสนอใหม่ · ยังไม่คิดเงิน) + Idempotency-Key ต่อหน้าต่างที่เปิด
 *  • ชำระออนไลน์ผ่าน PayWidget (ช่องทางของ NextAcc · intent ใต้บริษัทผู้ให้บริการ) หรือโอน + แนบสลิป · ติ๊กยอมรับนโยบายบังคับ (ด่านจริงที่เซิร์ฟเวอร์)
 *
 * ใช้:  QuotaUI.renderSummary(el, { only: ['cms.sites'] })   ·   QuotaUI.openBuy('cms.sites', { onDone })
 *       QuotaUI.renderPurchases(el)   ·   QuotaUI.renderRenewal(el)   ·   หน้าโหลดด้วย #buy=<resource> ⇒ เปิดหน้าต่างซื้อเอง
 */
(function (global) {
  'use strict';
  const esc = s => String(s == null ? '' : s).replace(/[&<>"']/g, c => ({ '&': '&amp;', '<': '&lt;', '>': '&gt;', '"': '&quot;', "'": '&#39;' }[c]));
  const money = n => Number(n || 0).toLocaleString('th-TH', { minimumFractionDigits: 2, maximumFractionDigits: 2 });
  const thDate = d => d ? new Date(d).toLocaleDateString('th-TH', { year: 'numeric', month: 'short', day: 'numeric' }) : '';
  const toast = (m, t) => (global.Layout && Layout.toast) ? Layout.toast(m, t) : alert(m);
  const newKey = () => (global.crypto && crypto.randomUUID) ? crypto.randomUUID() : String(Date.now()) + Math.random().toString(16).slice(2);

  function loadScript(src) {
    return new Promise((resolve, reject) => {
      const found = Array.from(document.scripts).find(x => x.src && x.src.endsWith(src));
      if (found) { resolve(); return; }
      const el = document.createElement('script');
      el.src = src; el.onload = () => resolve(); el.onerror = () => reject(new Error('โหลดสคริปต์ไม่สำเร็จ: ' + src));
      document.head.appendChild(el);
    });
  }

  const QuotaUI = {
    cid() { return (global.Layout && Layout.getCompanyId && Layout.getCompanyId()) || ''; },

    async summary() {
      const r = await API.get(`/api/companies/${this.cid()}/quota/summary`);
      return (r && r.data && r.data.items) || [];
    },

    barsHtml(items, opts) {
      opts = opts || {};
      if (!items.length) return '<div class="muted" style="font-size:13px">ไม่มีรายการ</div>';
      return items.map(it => {
        const pct = Number(it.percent) || 0;   // รอบ 230: ความยาวหลอดคิดที่เซิร์ฟเวอร์ (QuotaLimitPolicy.Percent) — ไม่คิดเองในหน้าเว็บ
        const color = it.warnLevel === 2 ? '#dc2626' : it.warnLevel === 1 ? '#d97706' : '#16a34a';
        const buy = (opts.buy !== false && it.canBuy)
          ? `<button type="button" class="btn btn-secondary btn-sm" data-quota-buy="${esc(it.resource)}" style="margin-left:8px">➕ ซื้อเพิ่ม</button>` : '';
        const bar = (it.limit != null && !it.unlimited)
          ? `<div style="height:8px;background:#e5e7eb;border-radius:999px;overflow:hidden;margin-top:4px"><i style="display:block;height:100%;width:${pct}%;background:${color}"></i></div>` : '';
        return `<div class="quota-item" style="margin-bottom:12px">
          <div style="display:flex;justify-content:space-between;align-items:center;gap:8px;flex-wrap:wrap;font-size:13px">
            <span>${esc(it.label)}</span>
            <span><b>${esc(it.perSite ? it.limitText : it.usedText + ' / ' + it.limitText)}</b> ${it.perSite ? '' : esc(it.unit)}${buy}</span>
          </div>
          ${bar}
          ${it.note ? `<div class="muted" style="font-size:11px;margin-top:2px">${esc(it.note)}</div>` : ''}
        </div>`;
      }).join('');
    },

    /** หลอดโควตา — opts.only = รายชื่อทรัพยากรที่จะแสดง · opts.onBought = เรียกหลังซื้อสำเร็จ */
    async renderSummary(el, opts) {
      if (typeof el === 'string') el = document.getElementById(el);
      if (!el) return null;
      opts = opts || {};
      let items;
      try { items = await this.summary(); }
      catch (e) { el.innerHTML = `<div style="color:#b91c1c;font-size:13px">โหลดข้อมูลโควตาไม่ได้ — ลองรีเฟรชหน้านี้ (${esc(e.message || '')})</div>`; return null; }
      const shown = opts.only ? items.filter(i => opts.only.includes(i.resource)) : items;
      el.innerHTML = this.barsHtml(shown, opts);
      el.querySelectorAll('[data-quota-buy]').forEach(b => b.addEventListener('click', () =>
        this.openBuy(b.getAttribute('data-quota-buy'), { onDone: () => { this.renderSummary(el, opts); if (opts.onBought) opts.onBought(); } })));
      return shown;
    },

    // ───────── หน้าต่างซื้อ ─────────
    _overlay() {
      let ov = document.getElementById('quotaBuyOverlay');
      if (ov) return ov;
      ov = document.createElement('div');
      ov.id = 'quotaBuyOverlay';
      ov.style.cssText = 'position:fixed;inset:0;background:rgba(15,23,42,.45);z-index:2000;display:flex;align-items:flex-start;justify-content:center;overflow:auto;padding:24px 12px';
      ov.innerHTML = '<div id="quotaBuyBox" style="background:#fff;border-radius:14px;max-width:560px;width:100%;padding:18px 18px 14px;box-shadow:0 20px 50px rgba(0,0,0,.25)"></div>';
      ov.addEventListener('click', e => { if (e.target === ov) this.closeBuy(); });
      document.body.appendChild(ov);
      return ov;
    },
    closeBuy() { const ov = document.getElementById('quotaBuyOverlay'); if (ov) ov.remove(); this._buy = null; },

    async openBuy(resource, opts) {
      this._buy = { resource, units: 1, mode: null, key: newKey(), opts: opts || {}, offer: null, pay: null };
      this._overlay();
      document.getElementById('quotaBuyBox').innerHTML = '<div class="muted">กำลังโหลดราคา...</div>';
      try {
        await Promise.all([loadScript('/js/policy-accept.js'), loadScript('/js/pay-widget.js')]);
        const p = await API.get(`/api/companies/${this.cid()}/platform-pay`);
        this._buy.pay = (p && p.data) || {};
      } catch (e) { this._buy.pay = {}; }
      await this._loadOffer();
    },

    async _loadOffer(offersFromServer) {
      const b = this._buy; if (!b) return;
      let offers = offersFromServer;
      if (!offers) {
        try { const r = await API.get(`/api/companies/${this.cid()}/quota/offers?units=${b.units}`); offers = (r && r.data) || []; }
        catch (e) { document.getElementById('quotaBuyBox').innerHTML = this._frame('ซื้อโควตาเพิ่ม', `<div style="color:#b91c1c">${esc(e.message || 'โหลดราคาไม่สำเร็จ')}</div>`); return; }
      }
      b.offer = offers.find(o => o.resource === b.resource) || null;
      if (!b.offer) {
        document.getElementById('quotaBuyBox').innerHTML = this._frame('ซื้อโควตาเพิ่ม',
          '<div>ยังไม่เปิดขายโควตาเสริมของรายการนี้ — กรุณาติดต่อผู้ดูแลระบบ NextAcc หรืออัปเกรดแพ็กเกจ</div>');
        return;
      }
      const allowed = b.offer.modes.filter(m => m.allowed);
      if (!b.mode || !allowed.some(m => m.mode === b.mode)) b.mode = allowed.length ? allowed[0].mode : null;
      this._renderBuy();
    },

    _frame(title, body, foot) {
      return `<div style="display:flex;justify-content:space-between;align-items:center;margin-bottom:10px">
          <h3 style="margin:0;font-size:17px">${esc(title)}</h3>
          <button type="button" class="btn btn-secondary btn-sm" onclick="QuotaUI.closeBuy()">✕</button></div>
        <div style="font-size:14px;line-height:1.6">${body}</div>${foot ? `<div style="margin-top:12px">${foot}</div>` : ''}`;
    },

    _renderBuy() {
      const b = this._buy, o = b.offer;
      const max = o.remainingUnits != null ? o.remainingUnits : 100;
      const sel = o.modes.find(m => m.mode === b.mode);
      const modes = o.modes.map(m => `<label style="display:flex;gap:8px;align-items:flex-start;padding:8px 10px;border:1px solid ${m.mode === b.mode ? '#2563eb' : '#e5e7eb'};border-radius:10px;margin-bottom:6px;${m.allowed ? 'cursor:pointer' : 'opacity:.55'}">
          <input type="radio" name="quotaMode" value="${esc(m.mode)}" ${m.mode === b.mode ? 'checked' : ''} ${m.allowed ? '' : 'disabled'} style="margin-top:4px;width:auto">
          <span style="flex:1"><b>${esc(m.label)}</b> ${m.allowed ? `<span style="float:right;font-weight:700">฿${money(m.amount)}</span>` : ''}
            <div class="muted" style="font-size:12px">${esc(m.allowed ? m.note : m.denyReason)}</div></span></label>`).join('');
      const pay = b.pay || {};
      const methods = (pay.online && pay.methodOptions) ? pay.methodOptions : [];
      const vat = o.vatIncluded ? 'ราคารวม VAT แล้ว' : 'ราคายังไม่รวม VAT 7%';
      const body = `
        <div class="muted" style="font-size:12px;margin-bottom:8px">หน่วยละ ${esc(o.unitText)} · ฿${money(o.monthlyUnitPrice)}/เดือน · ฿${money(o.annualUnitPrice)}/ปี · ${esc(vat)}
          ${o.maxUnitsPerCompany != null ? ` · ซื้อได้สูงสุด ${o.maxUnitsPerCompany} หน่วย (ถืออยู่ ${o.heldUnits})` : ''}</div>
        <div style="display:flex;align-items:center;gap:8px;margin-bottom:10px"><b>1) จำนวน</b>
          <button type="button" class="btn btn-secondary btn-sm" data-q-step="-1" ${b.units <= 1 ? 'disabled' : ''}>−</button>
          <b style="min-width:28px;text-align:center">${b.units}</b>
          <button type="button" class="btn btn-secondary btn-sm" data-q-step="1" ${b.units >= max ? 'disabled' : ''}>+</button>
          <span class="muted" style="font-size:12px">${esc(o.unit)}</span></div>
        <div style="margin-bottom:6px"><b>2) ใช้แบบไหน</b></div>${modes}
        <div style="margin:10px 0 6px"><b>3) สรุป</b> — ${sel && sel.allowed ? `ยอดชำระ <b style="font-size:18px">฿${money(this._couponFor(sel) ? this._couponFor(sel).amountAfter : sel.amount)}</b> (${esc(vat)})
          ${this._couponFor(sel) ? `<div style="font-size:12px;color:#166534">${esc(this._couponFor(sel).label)} — ราคา ฿${money(this._couponFor(sel).listAmount)} ลด ฿${money(this._couponFor(sel).discount)}</div>` : ''}
          ${sel.nextMonthlyAmount ? `<div class="muted" style="font-size:12px">เดือนถัดไป ฿${money(sel.nextMonthlyAmount)}/เดือน เข้าใบแจ้งหนี้ค่าใช้งาน · ยกเลิกได้มีผลสิ้นเดือน</div>` : ''}` : 'เลือกวิธีที่ซื้อได้'}</div>
        ${sel && sel.allowed ? `<div style="display:flex;gap:6px;align-items:center;margin-bottom:8px;flex-wrap:wrap">
          <input id="quotaCoupon" class="form-input" maxlength="40" placeholder="โค้ดส่วนลด (ถ้ามี)" value="${esc(b.couponCode || '')}" style="max-width:200px;text-transform:uppercase">
          <button type="button" class="btn btn-secondary btn-sm" data-q-coupon="1">ใช้โค้ด</button>
          ${b.couponProblem ? `<span style="color:#b91c1c;font-size:12px">${esc(b.couponProblem)}</span>` : ''}</div>` : ''}
        <div id="quotaBuyPolicy">${global.PolicyAccept ? PolicyAccept.html(pay.policy) : ''}</div>
        <div class="muted" style="font-size:12px">ใช้ได้ทันทีเมื่อชำระสำเร็จ · ได้ใบกำกับภาษี/ใบเสร็จ · ค่าบริการไม่คืนเงิน (ตามนโยบาย) · ของที่สร้างไว้ไม่ถูกลบเมื่อโควตาหมด</div>`;
      const foot = (sel && sel.allowed)
        ? `<div style="display:flex;gap:8px;flex-wrap:wrap">${methods.map(m =>
            `<button type="button" class="btn btn-primary" data-q-pay="${esc(m.code)}">ชำระด้วย ${esc(m.label)}</button>`).join('')}
            <button type="button" class="btn btn-secondary" data-q-slip="1">โอนเงิน + แนบสลิป</button></div>
           ${!pay.online && pay.offlineReason ? `<div class="muted" style="font-size:12px;margin-top:6px">${esc(pay.offlineReason)}</div>` : ''}`
        : '';
      const box = document.getElementById('quotaBuyBox');
      box.innerHTML = this._frame('ซื้อ' + (o.label || 'โควตาเพิ่ม'), body, foot);
      box.querySelectorAll('[data-q-step]').forEach(x => x.addEventListener('click', () => {
        b.units = Math.max(1, Math.min(max, b.units + Number(x.getAttribute('data-q-step')))); this._loadOffer();
      }));
      box.querySelectorAll('input[name="quotaMode"]').forEach(x => x.addEventListener('change', () => { b.mode = x.value; this._renderBuy(); }));
      box.querySelectorAll('[data-q-pay]').forEach(x => x.addEventListener('click', () => this._checkout(x.getAttribute('data-q-pay'))));
      box.querySelectorAll('[data-q-slip]').forEach(x => x.addEventListener('click', () => this._checkout(null)));
      box.querySelectorAll('[data-q-coupon]').forEach(x => x.addEventListener('click', () => this._applyCoupon()));
    },

    /** รอบ 230: ผลคูปองที่เซิร์ฟเวอร์คิดไว้ของวิธี/จำนวนที่เลือกอยู่ (null = ไม่มี/ไม่ตรงกับที่เลือกแล้ว) */
    _couponFor(sel) {
      const c = this._buy && this._buy.coupon;
      return c && sel && c.mode === sel.mode && c.units === this._buy.units && c.ok ? c : null;
    },

    /** รอบ 230: ถามเซิร์ฟเวอร์ว่าคูปองใช้ได้ไหม/ลดเท่าไร (ไม่จอง — จองจริงตอนสร้างรายการ) */
    async _applyCoupon() {
      const b = this._buy; if (!b) return;
      const code = String((document.getElementById('quotaCoupon') || {}).value || '').trim();
      b.couponCode = code; b.coupon = null; b.couponProblem = null;
      if (code) {
        try {
          const r = await API.get(`/api/companies/${this.cid()}/quota/coupon-preview?resource=${encodeURIComponent(b.resource)}&mode=${encodeURIComponent(b.mode)}&units=${b.units}&code=${encodeURIComponent(code)}`);
          const d = (r && r.data) || {};
          if (d.ok) b.coupon = Object.assign({ mode: b.mode, units: b.units }, d);
          else b.couponProblem = d.problem || 'ใช้คูปองนี้ไม่ได้';
        } catch (e) { b.couponProblem = e.message || 'ตรวจคูปองไม่สำเร็จ'; }
      }
      this._renderBuy();
    },

    /** สร้างรายการ (ยอดที่เห็นไปเทียบ) แล้วจ่ายออนไลน์ / เปิดฟอร์มสลิป */
    async _checkout(method) {
      const b = this._buy; if (!b) return;
      let pol;
      try { pol = PolicyAccept.require('quotaBuyPolicy'); } catch (e) { toast(e.message, 'error'); return; }
      const sel = b.offer.modes.find(m => m.mode === b.mode);
      let row;
      try {
        const r = await API.request('POST', `/api/companies/${this.cid()}/quota/purchases`,
          { resource: b.resource, mode: b.mode, units: b.units, expectedPerUnit: sel.perUnit, expectedAmount: sel.amount,
            couponCode: this._couponFor(sel) ? b.couponCode : null },
          false, null, { 'Idempotency-Key': b.key });
        row = r && r.data;
      } catch (e) {
        if (e && e.status === 409 && e.body && e.body.data && e.body.data.offers) {
          toast(e.message || 'ราคาเปลี่ยน — กรุณาตรวจแล้วยืนยันอีกครั้ง', 'warning');
          b.key = newKey();
          await this._loadOffer(e.body.data.offers);
          return;
        }
        toast(e.message || 'สร้างรายการไม่สำเร็จ', 'error'); return;
      }
      if (!row) return;
      if (method) this._payOnline(row, method, pol);
      else this._slipForm(row, pol);
    },

    _payOnline(row, method, pol) {
      const b = this._buy || {}; const onDone = b.opts && b.opts.onDone;
      this.closeBuy();
      PayWidget.open({
        companyId: this.cid(),
        publicBase: `/api/companies/${this.cid()}/quota/purchases/${encodeURIComponent(row.id)}`,
        method, card: (b.pay && b.pay.card) || null,
        extraBody: { policyAccepted: pol.policyAccepted, policyVersionShown: pol.policyVersionShown },
        onPaid: () => { toast('ชำระสำเร็จ — ใช้โควตาเพิ่มได้ทันที', 'success'); if (onDone) onDone(); },
      });
    },

    _slipForm(row, pol) {
      const b = this._buy; const pay = (b && b.pay) || {};
      const bank = pay.bankTransferInstructions
        ? `<div style="white-space:pre-line;background:#f8fafc;border:1px solid #e2e8f0;border-radius:8px;padding:8px 10px;margin-bottom:8px"><b>โอนเงินค่าบริการให้ NextAcc</b>\n${esc(pay.bankTransferInstructions)}</div>`
        : `<div style="color:#b45309;margin-bottom:8px">${esc(pay.fallbackNote || 'กรุณาติดต่อผู้ดูแลระบบ NextAcc ก่อนโอนเงิน')}</div>`;
      document.getElementById('quotaBuyBox').innerHTML = this._frame('แนบสลิป — ยอด ฿' + money(row.amountDue), `${bank}
        <form id="quotaSlipForm" style="display:grid;gap:8px">
          <label>ไฟล์สลิป (รูปหรือ PDF)<input type="file" name="file" accept="image/*,application/pdf" required class="form-input"></label>
          <label>วันที่โอน (วันที่บนใบเสร็จ)<input type="date" name="transferDate" class="form-input"></label>
          <label>อ้างอิง/เวลาโอน (ถ้ามี)<input name="reference" maxlength="120" class="form-input"></label>
          <div class="muted" style="font-size:12px">ผู้ดูแลระบบตรวจแล้วเปิดใช้ให้ทันที (ระหว่างรอยังไม่เพิ่มโควตา) · ดูสถานะได้ที่ "การซื้อโควตาเสริม"</div>
        </form>`, `<button type="button" class="btn btn-primary" id="quotaSlipSubmit">ส่งสลิป</button>`);
      document.getElementById('quotaSlipSubmit').addEventListener('click', async () => {
        const form = document.getElementById('quotaSlipForm');
        if (!form.file.files.length) { toast('เลือกไฟล์สลิปก่อน', 'error'); return; }
        const fd = new FormData(form);
        fd.set('policyAccepted', pol.policyAccepted ? 'true' : 'false');
        if (pol.policyVersionShown) fd.set('policyVersionShown', pol.policyVersionShown);
        try {
          const r = await API.request('POST', `/api/companies/${this.cid()}/quota/purchases/${encodeURIComponent(row.id)}/slip`, fd, true);
          toast((r && r.message) || 'ส่งสลิปแล้ว', 'success');
          const onDone = b && b.opts && b.opts.onDone;
          this.closeBuy(); if (onDone) onDone();
        } catch (e) { toast(e.message || 'ส่งสลิปไม่สำเร็จ', 'error'); }
      });
    },

    // ───────── การซื้อของฉัน ─────────
    async renderPurchases(el) {
      if (typeof el === 'string') el = document.getElementById(el);
      if (!el) return;
      let rows;
      try { const r = await API.get(`/api/companies/${this.cid()}/quota/purchases`); rows = (r && r.data) || []; }
      catch (e) { el.innerHTML = `<div style="color:#b91c1c;font-size:13px">โหลดรายการไม่ได้ (${esc(e.message || '')})</div>`; return; }
      if (!rows.length) { el.innerHTML = '<div class="muted" style="font-size:13px">ยังไม่มีการซื้อโควตาเสริม</div>'; return; }
      el.innerHTML = `<div style="overflow-x:auto"><table class="table" style="width:100%;font-size:13px"><thead><tr>
          <th>รายการ</th><th>แบบ</th><th>ช่วงใช้ได้</th><th style="text-align:right">ยอด</th><th>สถานะ</th><th></th></tr></thead><tbody>
        ${rows.map(p => `<tr>
          <td>${esc(p.label)} × ${p.units}${p.unitSize > 1 ? ' (' + p.unitSize + ' ' + esc(p.unit) + '/หน่วย)' : ''}</td>
          <td>${esc(p.modeLabel)}${p.mode === 'Coterm' && p.status === 'Active' ? `<div class="muted" style="font-size:11px">${p.renewWithPlan ? 'ต่อพร้อมแพ็กเกจ' : 'ไม่ต่อรอบหน้า'}</div>` : ''}</td>
          <td>${thDate(p.periodStart)} – ${p.periodEnd ? thDate(p.periodEnd) : 'ทุกเดือนจนยกเลิก'}</td>
          <td style="text-align:right">฿${money(p.amountDue)}</td>
          <td>${esc(p.statusLabel)}${p.rejectedReason ? `<div style="color:#b91c1c;font-size:11px">${esc(p.rejectedReason)}</div>` : ''}
            ${p.receiptNumber ? `<div class="muted" style="font-size:11px">ใบเสร็จ ${esc(p.receiptNumber)}</div>` : ''}</td>
          <td style="white-space:nowrap">${p.canPay ? `<button type="button" class="btn btn-primary btn-sm" data-q-repay="${esc(p.id)}">ชำระ</button>` : ''}
            ${p.canCancel ? `<button type="button" class="btn btn-secondary btn-sm" data-q-cancel="${esc(p.id)}">ยกเลิก</button>` : ''}</td>
        </tr>`).join('')}</tbody></table></div>`;
      el.querySelectorAll('[data-q-cancel]').forEach(x => x.addEventListener('click', async () => {
        const p = rows.find(r => r.id === x.getAttribute('data-q-cancel'));
        const text = p && p.mode === 'RecurringMonthly' && p.status === 'Active'
          ? 'ยกเลิกแบบรายเดือน? ใช้ได้ถึงสิ้นเดือนนี้ ไม่คิดเดือนถัดไป (ไม่คืนเงินเดือนนี้)' : 'ยกเลิกรายการที่ยังไม่ได้ชำระนี้?';
        if (!confirm(text)) return;
        try { const r = await API.post(`/api/companies/${this.cid()}/quota/purchases/${encodeURIComponent(x.getAttribute('data-q-cancel'))}/cancel`, {}); toast((r && r.message) || 'บันทึกแล้ว', 'success'); this.renderPurchases(el); }
        catch (e) { toast(e.message, 'error'); }
      }));
      el.querySelectorAll('[data-q-repay]').forEach(x => x.addEventListener('click', async () => {
        const p = rows.find(r => r.id === x.getAttribute('data-q-repay'));
        await this._repay(p, () => this.renderPurchases(el));
      }));
    },

    /** ชำระรายการที่สร้างไว้แล้ว (รอชำระ/ถูกปฏิเสธ) — ยอดเดิมของรายการ (เซิร์ฟเวอร์) */
    async _repay(p, onDone) {
      this._buy = { resource: p.resource, opts: { onDone }, pay: null };
      this._overlay();
      try {
        await Promise.all([loadScript('/js/policy-accept.js'), loadScript('/js/pay-widget.js')]);
        const r = await API.get(`/api/companies/${this.cid()}/platform-pay`); this._buy.pay = (r && r.data) || {};
      } catch { this._buy.pay = {}; }
      const pay = this._buy.pay;
      const methods = (pay.online && pay.methodOptions) ? pay.methodOptions : [];
      const box = document.getElementById('quotaBuyBox');
      box.innerHTML = this._frame(`ชำระ ${p.label} × ${p.units} — ฿${money(p.amountDue)}`,
        `<div id="quotaBuyPolicy">${PolicyAccept.html(pay.policy)}</div>`,
        `<div style="display:flex;gap:8px;flex-wrap:wrap">${methods.map(m => `<button type="button" class="btn btn-primary" data-q-pay="${esc(m.code)}">ชำระด้วย ${esc(m.label)}</button>`).join('')}
         <button type="button" class="btn btn-secondary" data-q-slip="1">โอนเงิน + แนบสลิป</button></div>`);
      const pol = () => PolicyAccept.require('quotaBuyPolicy');
      box.querySelectorAll('[data-q-pay]').forEach(x => x.addEventListener('click', () => { try { this._payOnline(p, x.getAttribute('data-q-pay'), pol()); } catch (e) { toast(e.message, 'error'); } }));
      box.querySelectorAll('[data-q-slip]').forEach(x => x.addEventListener('click', () => { try { this._slipForm(p, pol()); } catch (e) { toast(e.message, 'error'); } }));
    },

    // ───────── ต่ออายุพร้อมแพ็กเกจ ─────────
    async renderRenewal(el) {
      if (typeof el === 'string') el = document.getElementById(el);
      if (!el) return;
      let d;
      try { const r = await API.get(`/api/companies/${this.cid()}/quota/renewal`); d = (r && r.data) || {}; }
      catch (e) { el.innerHTML = `<div style="color:#b91c1c;font-size:13px">โหลดรายการต่ออายุไม่ได้ (${esc(e.message || '')})</div>`; return; }
      const items = d.items || [];
      el.innerHTML = `<div class="muted" style="font-size:12px;margin-bottom:8px">${esc(d.note || '')}</div>
        ${items.length ? items.map(i => `<label style="display:flex;gap:8px;align-items:center;padding:6px 0;border-bottom:1px solid #f1f5f9;font-size:13px">
          <input type="checkbox" data-q-renew="${esc(i.id)}" ${i.renew ? 'checked' : ''} ${i.locked || !i.offerAvailable ? 'disabled' : ''} style="width:auto">
          <span style="flex:1">${esc(i.label)} — จำนวน
            <input type="number" min="1" value="${i.renewUnits}" data-q-units="${esc(i.id)}" ${i.locked || !i.offerAvailable ? 'disabled' : ''} style="width:64px;display:inline-block" class="form-input">
            ${i.offerAvailable ? `· หน่วยละ ฿${money(i.perUnit)}/รอบ = <b>฿${money(i.amount)}</b>` : '· <span style="color:#b45309">เลิกขายแล้ว — หมดพร้อมรอบนี้ (ข้อมูลไม่หาย)</span>'}
            ${i.locked ? '<span class="muted"> · อยู่ในใบแจ้งหนี้ที่ชำระ/แจ้งชำระแล้ว</span>' : i.inInvoice ? '<span class="muted"> · อยู่ในใบแจ้งหนี้ (แก้แล้วระบบออกใบใหม่ให้)</span>' : ''}</span></label>`).join('')
          : '<div class="muted" style="font-size:13px">ไม่มีโควตาเสริมแบบ "ถึงวันหมดอายุแพ็กเกจ" ที่จะต่อ</div>'}`;
      const save = async (id) => {
        const cb = el.querySelector(`[data-q-renew="${id}"]`), u = el.querySelector(`[data-q-units="${id}"]`);
        try {
          const r = await API.post(`/api/companies/${this.cid()}/quota/renewal/${encodeURIComponent(id)}`, { renew: cb.checked, units: Number(u.value) || null });
          toast((r && r.message) || 'บันทึกแล้ว', 'success'); this.renderRenewal(el);
        } catch (e) { toast(e.message, 'error'); this.renderRenewal(el); }
      };
      el.querySelectorAll('[data-q-renew]').forEach(x => x.addEventListener('change', () => save(x.getAttribute('data-q-renew'))));
      el.querySelectorAll('[data-q-units]').forEach(x => x.addEventListener('change', () => save(x.getAttribute('data-q-units'))));
    },

    /** หน้าโหลดด้วย #buy=<resource> (ลิงก์จากข้อความปฏิเสธของด่าน) ⇒ เปิดหน้าต่างซื้อ */
    initHash(onDone) {
      const m = /#buy=([a-z0-9.\-]+)/i.exec(location.hash || '');
      if (m) this.openBuy(m[1], { onDone });
    },

    /** ข้อผิดพลาดจาก API ที่เป็น "โควตาเต็ม" (ruleCode QUOTA-FULL:<resource>) ⇒ ถามว่าจะซื้อเพิ่มไหม · คืน true = จัดการแล้ว */
    handleFull(err, onDone) {
      const rc = err && err.body && err.body.data && err.body.data.ruleCode;
      if (!rc || String(rc).indexOf('QUOTA-FULL:') !== 0) return false;
      if (confirm((err.message || 'โควตาเต็ม') + '\n\nเปิดหน้าต่างซื้อเพิ่มตอนนี้ไหม?')) this.openBuy(String(rc).slice('QUOTA-FULL:'.length), { onDone });
      return true;
    },
  };
  global.QuotaUI = QuotaUI;
})(window);
