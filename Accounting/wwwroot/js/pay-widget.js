/**
 * pay-widget.js — หน้าจ่ายเงินที่ **ทุกทางเข้าใช้ร่วมกัน**
 * (PAYMENT_GATEWAY_DESIGN.md §4.6)
 *
 * ═══ ทำไมต้องมีตัวเดียว ═══
 * ระบบมี 5 ทางเข้าที่ต้องรับเงิน (เว็บขายของ · portal ลูกค้า · มัดจำที่พัก ·
 * ค่าบริการ SaaS · POS) — ถ้าแต่ละทางเขียนหน้าจ่ายเอง จะได้สำเนา 5 ชุดที่ drift
 * แน่นอน แล้ววันที่เปลี่ยนผู้ให้บริการต้องไล่แก้ 5 ที่ (defect class เดียวกับ
 * "สำเนามือฝั่ง JS" ที่เรพนี้เจอซ้ำที่สุด)
 *
 * ═══ กติกาที่ไฟล์นี้รักษา ═══
 *  1. **ไม่มีชื่อผู้ให้บริการ** — ทุกอย่างผ่าน `/pay/intents`
 *  2. **ไม่เชื่อ query string ตอนกลับจากหน้าธนาคาร** — ถามสถานะจากเซิร์ฟเวอร์เสมอ
 *     (ใครก็เติม `?status=success` เองได้)
 *  3. **poll แบบถอยห่าง** 2s → 5s → 10s แล้วหยุดเมื่อจบ — ถี่กว่านี้ไม่ช่วยอะไร
 *     (ลูกค้าใช้เวลาสแกนจ่ายเป็นนาที) และเปลืองทั้งเซิร์ฟเวอร์เราและโควตา API
 *  4. **ป้ายโหมดทดสอบต้องเห็นบนหน้าจ่ายของลูกค้า** ไม่ใช่แค่หน้าตั้งค่าของร้าน —
 *     ไม่งั้นร้านทดลองแล้วเข้าใจว่าเก็บเงินได้จริง
 *
 * การใช้:
 *   PayWidget.open({
 *     companyId, sourceKind: 'SiteOrder', sourceId, amount,
 *     method: 'PromptPay', description, customerEmail,
 *     onPaid: (intent) => { ... },     // จ่ายสำเร็จ
 *     onClosed: () => { ... },         // ผู้ใช้ปิดหน้าต่างเอง
 *   });
 */
(function (global) {
  'use strict';

  const POLL_STEPS_MS = [2000, 2000, 3000, 5000, 5000, 10000];
  const MAX_POLL_MS = 15 * 60 * 1000;   // QR ส่วนใหญ่หมดอายุก่อนถึงตรงนี้อยู่แล้ว

  function esc(v) {
    return String(v == null ? '' : v)
      .replace(/&/g, '&amp;').replace(/</g, '&lt;').replace(/>/g, '&gt;')
      .replace(/"/g, '&quot;').replace(/'/g, '&#39;');
  }
  function money(n) {
    return Number(n || 0).toLocaleString('en-US', { minimumFractionDigits: 2, maximumFractionDigits: 2 });
  }

  const PayWidget = {
    _timer: null,
    _stopped: false,

    async open(opts) {
      this._stopped = false;
      this._opts = opts || {};
      this._mount();
      this._setBody('<div style="padding:28px;text-align:center;color:#64748b">กำลังเตรียมการชำระเงิน…</div>');

      try {
        const res = await fetch(`/api/companies/${opts.companyId}/pay/intents`, {
          method: 'POST',
          headers: Object.assign({ 'Content-Type': 'application/json' }, this._authHeader()),
          body: JSON.stringify({
            sourceKind: opts.sourceKind, sourceId: opts.sourceId,
            amount: opts.amount, method: opts.method || 'PromptPay',
            description: opts.description || null,
            customerEmail: opts.customerEmail || null,
            customerPhone: opts.customerPhone || null,
            returnUrl: opts.returnUrl || (location.origin + location.pathname),
            cardToken: opts.cardToken || null,
            siteId: opts.siteId || null, contactId: opts.contactId || null,
          }),
        });
        const json = await res.json();
        if (!res.ok || !json.success) throw new Error(json.message || 'เริ่มการชำระเงินไม่สำเร็จ');

        const intent = json.data;
        this._intentId = intent.id;

        // 3-D Secure ของบัตร: ต้องพาไปหน้าธนาคาร แล้วกลับมาตรวจสถานะจากเซิร์ฟเวอร์
        // (ห้ามเชื่อ query string ตอนกลับ)
        if (intent.authorizeUrl) { global.location.href = intent.authorizeUrl; return; }

        this._renderPending(intent);
        this._poll(0, Date.now());
      } catch (e) {
        this._renderError(e.message);
      }
    },

    _authHeader() {
      // หน้า storefront ของลูกค้าไม่มี token — endpoint ฝั่งนั้นเปิดสาธารณะอยู่แล้ว
      const t = (() => { try { return localStorage.getItem('token'); } catch { return null; } })();
      return t ? { Authorization: 'Bearer ' + t } : {};
    },

    _mount() {
      let el = document.getElementById('payWidgetOverlay');
      if (el) { el.style.display = 'flex'; return; }
      el = document.createElement('div');
      el.id = 'payWidgetOverlay';
      el.style.cssText = 'position:fixed;inset:0;background:rgba(15,23,42,.55);z-index:9999;'
        + 'display:flex;align-items:center;justify-content:center;padding:16px';
      el.innerHTML = '<div id="payWidgetBox" style="background:#fff;border-radius:14px;max-width:420px;'
        + 'width:100%;box-shadow:0 20px 50px rgba(0,0,0,.25);overflow:hidden"></div>';
      el.addEventListener('click', (ev) => { if (ev.target === el) PayWidget.close(); });
      document.body.appendChild(el);
    },

    _setBody(html) {
      const box = document.getElementById('payWidgetBox');
      if (box) box.innerHTML = html;
    },

    _testBadge(intent) {
      // ป้ายนี้ต้องอยู่บนหน้าจ่ายของ **ลูกค้า** ไม่ใช่แค่หน้าตั้งค่าของร้าน
      return intent.isTestMode
        ? '<div style="background:#fef3c7;color:#92400e;padding:8px 14px;font-size:12px;font-weight:700;'
          + 'text-align:center">⚠️ โหมดทดสอบ — เงินไม่เข้าจริง</div>'
        : '';
    },

    _renderPending(intent) {
      const qr = intent.qrPayload
        ? `<img src="${esc(intent.qrPayload)}" alt="QR สำหรับชำระเงิน"
             style="width:230px;height:230px;object-fit:contain;border:1px solid #e2e8f0;border-radius:10px">`
        : '<div style="color:#64748b;font-size:13px">กำลังรอการชำระเงิน…</div>';
      const expires = intent.qrExpiresAt
        ? `<div id="payWidgetCountdown" style="font-size:12px;color:#64748b;margin-top:6px"></div>` : '';

      this._setBody(`
        ${this._testBadge(intent)}
        <div style="padding:20px;text-align:center">
          <div style="font-size:13px;color:#64748b">ยอดที่ต้องชำระ</div>
          <div style="font-size:26px;font-weight:800;color:#0f172a;margin:2px 0 14px">฿${money(intent.amount)}</div>
          ${qr}
          ${expires}
          <div style="font-size:12px;color:#64748b;margin-top:12px;line-height:1.7">
            สแกนด้วยแอปธนาคาร แล้ว<b>รอสักครู่</b> — ระบบจะอัปเดตให้เองเมื่อได้รับเงิน
            <br>ไม่ต้องกดปุ่มอะไรเพิ่ม และ<b>ห้ามปิดหน้านี้จนกว่าจะขึ้นว่าสำเร็จ</b>
          </div>
          <button onclick="PayWidget.close()"
            style="margin-top:14px;background:#f1f5f9;border:1px solid #e2e8f0;border-radius:8px;
                   padding:8px 16px;font-size:13px;cursor:pointer">ยกเลิก</button>
        </div>`);
      this._startCountdown(intent.qrExpiresAt);
    },

    _startCountdown(expiresAt) {
      if (!expiresAt) return;
      const el = () => document.getElementById('payWidgetCountdown');
      const end = new Date(expiresAt).getTime();
      const tick = () => {
        const box = el();
        if (!box || this._stopped) return;
        const left = Math.max(0, end - Date.now());
        const m = Math.floor(left / 60000), s = Math.floor((left % 60000) / 1000);
        box.textContent = left > 0
          ? `QR หมดอายุใน ${m}:${String(s).padStart(2, '0')} นาที`
          : 'QR หมดอายุแล้ว — ปิดหน้านี้แล้วกดชำระเงินใหม่';
        if (left > 0) setTimeout(tick, 1000);
      };
      tick();
    },

    _renderDone(intent) {
      this._setBody(`
        <div style="padding:28px;text-align:center">
          <div style="font-size:40px">✅</div>
          <div style="font-size:17px;font-weight:800;color:#166534;margin:8px 0 4px">ชำระเงินสำเร็จ</div>
          <div style="font-size:13px;color:#64748b">฿${money(intent.amount)}</div>
          <button onclick="PayWidget.close()"
            style="margin-top:16px;background:#16a34a;color:#fff;border:0;border-radius:8px;
                   padding:9px 20px;font-size:14px;font-weight:600;cursor:pointer">เสร็จสิ้น</button>
        </div>`);
    },

    _renderError(message, intent) {
      this._setBody(`
        ${intent ? this._testBadge(intent) : ''}
        <div style="padding:26px;text-align:center">
          <div style="font-size:36px">⚠️</div>
          <div style="font-size:15px;font-weight:700;color:#b91c1c;margin:8px 0 6px">ยังชำระเงินไม่สำเร็จ</div>
          <div style="font-size:13px;color:#64748b;line-height:1.7">${esc(message || '')}</div>
          <button onclick="PayWidget.close()"
            style="margin-top:16px;background:#f1f5f9;border:1px solid #e2e8f0;border-radius:8px;
                   padding:8px 18px;font-size:13px;cursor:pointer">ปิด</button>
        </div>`);
    },

    async _poll(step, startedAt) {
      if (this._stopped) return;
      if (Date.now() - startedAt > MAX_POLL_MS) {
        this._renderError('หมดเวลารอการชำระเงิน — ปิดหน้านี้แล้วลองใหม่อีกครั้ง');
        return;
      }
      const wait = POLL_STEPS_MS[Math.min(step, POLL_STEPS_MS.length - 1)];
      this._timer = setTimeout(async () => {
        if (this._stopped) return;
        try {
          const res = await fetch(
            `/api/companies/${this._opts.companyId}/pay/intents/${this._intentId}/status`,
            { headers: this._authHeader() });
          const json = await res.json();
          const intent = json.data;
          if (intent && intent.status === 'Succeeded') {
            this._stopped = true;
            this._renderDone(intent);
            if (typeof this._opts.onPaid === 'function') this._opts.onPaid(intent);
            return;
          }
          if (intent && (intent.status === 'Failed' || intent.status === 'Expired')) {
            this._stopped = true;
            this._renderError(
              intent.failureMessage
              || (intent.status === 'Expired' ? 'QR หมดอายุก่อนได้รับการชำระเงิน' : 'การชำระเงินไม่สำเร็จ'),
              intent);
            return;
          }
        } catch {
          // เน็ตสะดุดชั่วคราวไม่ใช่เหตุให้เลิกรอ — รอบถัดไปลองใหม่
        }
        this._poll(step + 1, startedAt);
      }, wait);
    },

    close() {
      this._stopped = true;
      if (this._timer) { clearTimeout(this._timer); this._timer = null; }
      const el = document.getElementById('payWidgetOverlay');
      if (el) el.style.display = 'none';
      if (typeof this._opts?.onClosed === 'function') this._opts.onClosed();
    },
  };

  global.PayWidget = PayWidget;
})(window);
