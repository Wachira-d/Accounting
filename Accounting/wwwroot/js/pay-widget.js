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
 *  5. **เลขบัตรไม่เคยออกจากเบราว์เซอร์ไปเซิร์ฟเวอร์ของเรา** (PCI-DSS SAQ-A · รอบ 228 PLATFORM_OMISE_AUDIT §5.1) —
 *     ช่องกรอกบัตรไม่มี name/ไม่อยู่ใน <form> · ค่าในช่องส่งให้สคริปต์ของผู้ให้บริการ (URL + คีย์สาธารณะมาจากเซิร์ฟเวอร์ —
 *     `PaymentClientSetup`) แลกเป็นโทเคน แล้วส่ง **เฉพาะโทเคน** (`cardToken`) · ล้างช่องทันทีหลังแลก
 *  6. **กลับจากหน้าธนาคาร (3-D Secure · แบงก์กิ้ง · วอลเล็ท)** — ก่อนพาออกไป จดรายการไว้ใน sessionStorage + ใส่ `payReturn=1`
 *     ใน URL กลับ · โหลดหน้าเดิมกลับมา ⇒ `PayWidget.resume()` (รันเองตอนโหลด) ถามสถานะจากเซิร์ฟเวอร์ (ไม่เชื่อ query)
 *
 * ═══ สองโหมด ═══
 *  A. **โหมดในระบบ** (ผู้ใช้ล็อกอิน) — ส่ง sourceKind/sourceId/amount เอง ยิงไป
 *     `/pay/intents` ซึ่งเป็น [Authorize]
 *  B. **โหมดลูกค้าปลายทาง** (ไม่ล็อกอิน) — ส่ง `publicBase` ที่เป็นเส้นทางซึ่ง
 *     **มี token/orderId ของผู้เรียกอยู่ในตัว URL แล้ว** ยิงไป `/public-pay/...`
 *     ⇒ ฝั่งนี้ **ไม่ส่ง sourceId และไม่ส่ง amount** เลย: เซิร์ฟเวอร์ resolve เอง
 *     จากแถวจริง (ถ้าให้หน้าเว็บกำหนดยอดได้ ใครก็จ่าย 1 บาทแล้วได้ของ)
 *
 * การใช้:
 *   PayWidget.open({
 *     companyId, sourceKind: 'SiteOrder', sourceId, amount,   // โหมด A
 *     publicBase: `/api/companies/${cid}/public-pay/lodging/${siteId}/${token}`, // โหมด B
 *     method: 'PromptPay', description, customerEmail,
 *     methods: [{ code, label }],      // (ไม่ใส่ method) ให้ผู้จ่ายเลือกวิธีในหน้าต่างนี้ — ป้ายจากเซิร์ฟเวอร์
 *     card: { providerCode, scriptUrl, publicKey },   // ไม่ส่ง = widget ถาม cardSetupUrl (ค่าตั้งต้น public-pay/methods ของบริษัท)
 *     onPaid: (intent) => { ... },     // จ่ายสำเร็จ
 *     onClosed: () => { ... },         // ผู้ใช้ปิดหน้าต่างเอง
 *   });
 */
(function (global) {
  'use strict';

  const POLL_STEPS_MS = [2000, 2000, 3000, 5000, 5000, 10000];
  const MAX_POLL_MS = 15 * 60 * 1000;   // QR ส่วนใหญ่หมดอายุก่อนถึงตรงนี้อยู่แล้ว
  const RETURN_PARAM = 'payReturn';
  const PENDING_KEY = 'payWidget.pending';
  const PENDING_MAX_AGE_MS = 2 * 60 * 60 * 1000;   // กลับจากหน้าธนาคารเกิน 2 ชม. = ไม่ตรวจต่ออัตโนมัติ (สถานะจริงยังอยู่ที่หน้าเดิม)

  // รอบ 228: ตัวแปลงเลขบัตรเป็นโทเคน **ต่อผู้ให้บริการ** (ตารางเดียว · คีย์ = providerCode ที่เซิร์ฟเวอร์ส่งมา) — สคริปต์/คีย์มาจากเซิร์ฟเวอร์เสมอ
  const TOKENIZERS = {
    omise(setup, c) {
      return new Promise((resolve, reject) => {
        const sdk = global.Omise;
        if (!sdk || typeof sdk.createToken !== 'function') { reject(new Error('โหลดตัวรับบัตรของผู้ให้บริการไม่สำเร็จ — ลองใหม่อีกครั้ง')); return; }
        sdk.setPublicKey(setup.publicKey);
        sdk.createToken('card', {
          name: c.name, number: c.number, expiration_month: c.month, expiration_year: c.year, security_code: c.cvc,
        }, (status, resp) => {
          if (status === 200 && resp && resp.id) resolve(resp.id);
          else reject(new Error((resp && resp.message) ? 'ข้อมูลบัตรไม่ถูกต้อง: ' + resp.message : 'แปลงข้อมูลบัตรไม่สำเร็จ'));
        });
      });
    },
  };

  function storage() { try { return global.sessionStorage; } catch { return null; } }
  function loadScript(src) {
    return new Promise((resolve, reject) => {
      const found = Array.from(document.scripts).find(x => x.src === src);
      if (found && found.dataset.loaded === '1') { resolve(); return; }
      const el = found || document.createElement('script');
      el.addEventListener('load', () => { el.dataset.loaded = '1'; resolve(); });
      el.addEventListener('error', () => reject(new Error('โหลดตัวรับบัตรไม่สำเร็จ (เครือข่าย/ตัวบล็อกโฆษณา) — ลองใหม่ หรือเลือกพร้อมเพย์')));
      if (!found) { el.src = src; el.async = true; document.head.appendChild(el); }
    });
  }

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
      this._opts = opts = Object.assign({}, opts || {});
      this._mount();

      // รอบ 228: ไม่ได้กำหนดวิธี + มีหลายวิธี ⇒ ให้ผู้จ่ายเลือกที่นี่ (ป้ายจากเซิร์ฟเวอร์)
      const choices = (opts.methods || []).map(m => typeof m === 'string' ? { code: m, label: m } : m).filter(m => m && m.code);
      if (!opts.method && choices.length > 1) { this._renderChooser(choices); return; }
      if (!opts.method && choices.length === 1) opts.method = choices[0].code;

      // รอบ 228: บัตร ⇒ ช่องกรอกบัตร → โทเคนในเบราว์เซอร์ก่อน (เลขบัตรไม่ส่งมาที่เซิร์ฟเวอร์ของเรา)
      if (opts.method === 'Card' && !opts.cardToken) { await this._renderCardForm(); return; }
      await this._start();
    },

    _choose(code) { this._opts.method = code; this.open(this._opts); },

    _renderChooser(choices) {
      this._setBody(`
        <div style="padding:20px">
          <div style="font-size:15px;font-weight:700;color:#0f172a;margin-bottom:12px">เลือกวิธีชำระเงิน</div>
          <div style="display:grid;gap:8px">
            ${choices.map((m, i) => `<button data-pw-method="${i}" style="text-align:left;background:#f8fafc;border:1px solid #e2e8f0;border-radius:10px;padding:12px 14px;font-size:14px;cursor:pointer">${esc(m.label || m.code)}</button>`).join('')}
          </div>
          <button onclick="PayWidget.close()" style="margin-top:14px;background:#f1f5f9;border:1px solid #e2e8f0;border-radius:8px;padding:8px 16px;font-size:13px;cursor:pointer">ยกเลิก</button>
        </div>`);
      const box = document.getElementById('payWidgetBox');
      (box ? box.querySelectorAll('[data-pw-method]') : []).forEach(b => b.addEventListener('click', () => this._choose(choices[Number(b.dataset.pwMethod)].code)));
    },

    async _cardSetup() {
      const o = this._opts;
      if (o.card && o.card.publicKey && o.card.scriptUrl) return o.card;
      // ค่าตั้งต้นเฉพาะเส้นของร้าน (public-pay) — เส้นค่าบริการ NextAcc (platform-pay) ต้องส่ง card ของช่องทาง NextAcc มาเอง (ห้ามหยิบคีย์ของร้าน)
      const shopPath = !o.publicBase || String(o.publicBase).indexOf('/public-pay/') >= 0;
      const url = o.cardSetupUrl || (o.companyId && shopPath ? `/api/companies/${o.companyId}/public-pay/methods` : null);
      if (!url) return null;
      try {
        const res = await fetch(url, { headers: this._authHeader() });
        const json = await res.json();
        const d = json && json.data;
        return d && d.card && d.card.publicKey && d.card.scriptUrl ? d.card : null;
      } catch { return null; }
    },

    /** รอบ 230: ข้อเสนอ "บันทึกบัตรเพื่อตัดอัตโนมัติ" (ข้อความยินยอม + ฉบับจากเซิร์ฟเวอร์) — เฉพาะเส้นค่าบริการ NextAcc (platform-pay) · null = ไม่เสนอ */
    async _savedCardOffer() {
      const o = this._opts;
      if (o.savedCardOffer !== undefined) return o.savedCardOffer;
      if (!o.publicBase || String(o.publicBase).indexOf('/platform-pay/') < 0 || !o.companyId) return null;
      try {
        const res = await fetch(`/api/companies/${o.companyId}/platform-pay/saved-card-offer`, { headers: this._authHeader() });
        const json = await res.json();
        const d = json && json.data;
        return d && d.text && d.version ? d : null;
      } catch { return null; }
    },

    async _renderCardForm() {
      this._setBody('<div style="padding:28px;text-align:center;color:#64748b">กำลังเตรียมช่องกรอกบัตร…</div>');
      const setup = await this._cardSetup();
      if (!setup || !TOKENIZERS[setup.providerCode]) {
        this._renderError('ช่องทางนี้ยังรับบัตรไม่ได้ (ยังไม่ได้ตั้งคีย์สาธารณะของผู้ให้บริการ) — เลือกวิธีอื่น เช่น พร้อมเพย์');
        return;
      }
      this._cardSetupValue = setup;
      const offer = this._offerValue = await this._savedCardOffer();
      const inp = 'width:100%;box-sizing:border-box;border:1px solid #cbd5e1;border-radius:8px;padding:9px 10px;font-size:14px';
      const yearNow = new Date().getFullYear();
      this._setBody(`
        ${setup.isTestMode ? '<div style="background:#fef3c7;color:#92400e;padding:8px 14px;font-size:12px;font-weight:700;text-align:center">⚠️ โหมดทดสอบ — ใช้บัตรทดสอบเท่านั้น เงินไม่เข้าจริง</div>' : ''}
        <div style="padding:20px">
          <div style="font-size:15px;font-weight:700;color:#0f172a">ชำระด้วยบัตรเครดิต/เดบิต</div>
          ${this._opts.amount ? `<div style="font-size:13px;color:#64748b;margin-top:2px">ยอด ฿${money(this._opts.amount)}</div>` : ''}
          <div style="display:grid;gap:10px;margin-top:12px">
            <label style="font-size:12px;color:#475569">ชื่อบนบัตร<input id="pwCardName" autocomplete="cc-name" style="${inp}"></label>
            <label style="font-size:12px;color:#475569">หมายเลขบัตร<input id="pwCardNumber" autocomplete="cc-number" inputmode="numeric" maxlength="23" style="${inp}"></label>
            <div style="display:grid;grid-template-columns:1fr 1fr 1fr;gap:8px">
              <label style="font-size:12px;color:#475569">เดือน<input id="pwCardMonth" autocomplete="cc-exp-month" inputmode="numeric" maxlength="2" placeholder="MM" style="${inp}"></label>
              <label style="font-size:12px;color:#475569">ปี (ค.ศ.)<input id="pwCardYear" autocomplete="cc-exp-year" inputmode="numeric" maxlength="4" placeholder="${yearNow}" style="${inp}"></label>
              <label style="font-size:12px;color:#475569">CVV<input id="pwCardCvc" autocomplete="cc-csc" inputmode="numeric" maxlength="4" type="password" style="${inp}"></label>
            </div>
          </div>
          ${offer ? `<label style="display:flex;gap:8px;align-items:flex-start;font-size:12px;color:#334155;margin-top:12px;line-height:1.6">
            <input type="checkbox" id="pwSaveCard" style="margin-top:3px">
            <span><b>บันทึกบัตรเพื่อตัดอัตโนมัติ</b> (ไม่บังคับ)<br>${esc(offer.text)}</span></label>
            ${offer.whtWarning ? `<label style="display:flex;gap:8px;align-items:flex-start;font-size:12px;color:#92400e;margin-top:8px;line-height:1.6">
            <input type="checkbox" id="pwSaveCardWht" style="margin-top:3px"><span>${esc(offer.whtWarning)}</span></label>` : ''}` : ''}
          <div id="pwCardError" style="color:#b91c1c;font-size:12px;margin-top:8px"></div>
          <div style="font-size:11px;color:#64748b;margin-top:8px;line-height:1.6">ข้อมูลบัตรส่งตรงถึงผู้ให้บริการรับชำระเงินเพื่อแปลงเป็นรหัสใช้ครั้งเดียว — ระบบของเราไม่เห็นและไม่เก็บเลขบัตร · ธนาคารอาจพาไปยืนยันตัวตน (3-D Secure) แล้วกลับมาหน้านี้เอง</div>
          <div style="display:flex;gap:8px;margin-top:14px">
            <button id="pwCardPay" style="flex:1;background:#16a34a;color:#fff;border:0;border-radius:8px;padding:10px 16px;font-size:14px;font-weight:600;cursor:pointer">ชำระเงิน</button>
            <button onclick="PayWidget.close()" style="background:#f1f5f9;border:1px solid #e2e8f0;border-radius:8px;padding:10px 16px;font-size:13px;cursor:pointer">ยกเลิก</button>
          </div>
        </div>`);
      const btn = document.getElementById('pwCardPay');
      if (btn) btn.addEventListener('click', () => this._submitCard());
    },

    async _submitCard() {
      const val = id => { const el = document.getElementById(id); return el ? String(el.value || '').trim() : ''; };
      const errBox = document.getElementById('pwCardError');
      const btn = document.getElementById('pwCardPay');
      const card = {
        name: val('pwCardName'), number: val('pwCardNumber').replace(/[\s-]/g, ''),
        month: val('pwCardMonth'), year: val('pwCardYear'), cvc: val('pwCardCvc'),
      };
      if (!card.name || !/^\d{12,19}$/.test(card.number) || !/^\d{1,2}$/.test(card.month) || !/^\d{2,4}$/.test(card.year) || !/^\d{3,4}$/.test(card.cvc)) {
        if (errBox) errBox.textContent = 'กรอกข้อมูลบัตรให้ครบ (ชื่อ · เลขบัตร · เดือน/ปีหมดอายุ · CVV)';
        return;
      }
      if (card.year.length === 2) card.year = '20' + card.year;
      // รอบ 230: ติ๊กบันทึกบัตร = ยินยอมตามข้อความ (ฉบับที่แสดงส่งกลับให้เซิร์ฟเวอร์ตรวจ) · ไม่ติ๊ก = ล้างค่าที่อาจค้างจากครั้งก่อน
      const saveBox = document.getElementById('pwSaveCard');
      const extra = Object.assign({}, this._opts.extraBody || {});
      delete extra.saveCard; delete extra.saveCardConsent; delete extra.saveCardConsentVersionShown; delete extra.saveCardWhtAcknowledged;
      if (saveBox && saveBox.checked && this._offerValue) {
        // ฝ่ายค้าน S1: นิติบุคคลต้องติ๊กรับทราบภาษีหัก ณ ที่จ่ายแยก (เซิร์ฟเวอร์ตรวจซ้ำ)
        const whtBox = document.getElementById('pwSaveCardWht');
        if (this._offerValue.whtWarning && !(whtBox && whtBox.checked)) {
          if (errBox) errBox.textContent = 'ติ๊กรับทราบเรื่องภาษีหัก ณ ที่จ่ายก่อน หรือเอาเครื่องหมายบันทึกบัตรออก';
          return;
        }
        extra.saveCard = true; extra.saveCardConsent = true; extra.saveCardConsentVersionShown = this._offerValue.version;
        extra.saveCardWhtAcknowledged = !!(whtBox && whtBox.checked);
      }
      this._opts.extraBody = extra;
      if (btn) { btn.disabled = true; btn.textContent = 'กำลังตรวจบัตร…'; }
      const setup = this._cardSetupValue;
      try {
        await loadScript(setup.scriptUrl);
        const token = await TOKENIZERS[setup.providerCode](setup, card);
        // ล้างช่องบัตรทันที — ค่าไม่ค้างใน DOM หลังได้โทเคน
        ['pwCardNumber', 'pwCardCvc', 'pwCardMonth', 'pwCardYear', 'pwCardName'].forEach(id => { const el = document.getElementById(id); if (el) el.value = ''; });
        this._opts.cardToken = token;
        await this._start();
      } catch (e) {
        if (errBox) errBox.textContent = e.message || 'แปลงข้อมูลบัตรไม่สำเร็จ';
        if (btn) { btn.disabled = false; btn.textContent = 'ชำระเงิน'; }
      }
    },

    /** URL กลับหลังหน้าธนาคาร = หน้าเดิม + payReturn=1 (ไม่มีข้อมูลสถานะใน URL — สถานะถามเซิร์ฟเวอร์เสมอ) */
    _returnUrl() {
      if (this._opts.returnUrl) return this._opts.returnUrl;
      try {
        const u = new URL(location.href);
        u.hash = '';
        u.searchParams.set(RETURN_PARAM, '1');
        return u.toString();
      } catch { return location.origin + location.pathname; }
    },

    async _start() {
      const opts = this._opts;
      this._setBody('<div style="padding:28px;text-align:center;color:#64748b">กำลังเตรียมการชำระเงิน…</div>');

      try {
        const pub = !!opts.publicBase;
        const url = pub
          ? `${opts.publicBase}/intents`
          : `/api/companies/${opts.companyId}/pay/intents`;
        // โหมดสาธารณะส่ง **เฉพาะวิธีจ่าย** — ตัวตนอยู่ใน URL แล้ว และยอดมาจาก
        // เซิร์ฟเวอร์เท่านั้น (ห้ามให้หน้าเว็บกำหนดจำนวนเงินที่ตัวเองจะจ่าย)
        // รอบ 219 ส่วน B: extraBody = ช่องเพิ่มของทางเข้านั้น (เช่น ติ๊กยอมรับนโยบาย policyAccepted/policyVersionShown) — ไม่รับยอดเงิน
        const body = pub
          ? Object.assign({}, opts.extraBody || {}, {
              method: opts.method || 'PromptPay',
              returnUrl: this._returnUrl(),
              cardToken: opts.cardToken || null,
            })
          : {
              sourceKind: opts.sourceKind, sourceId: opts.sourceId,
              amount: opts.amount, method: opts.method || 'PromptPay',
              description: opts.description || null,
              customerEmail: opts.customerEmail || null,
              customerPhone: opts.customerPhone || null,
              returnUrl: this._returnUrl(),
              cardToken: opts.cardToken || null,
              siteId: opts.siteId || null, contactId: opts.contactId || null,
            };
        // รอบ 217 (ข้อ 175): คีย์กันซ้ำต่อการเปิด widget หนึ่งครั้ง (js/submit-once.js ถ้าหน้าโหลดไว้) — เน็ตหลุด/กดซ้ำ = เริ่มจ่ายครั้งเดียว
        const attempt = this._attempt = {};
        const init = {
          method: 'POST',
          headers: Object.assign({ 'Content-Type': 'application/json' }, this._authHeader()),
          body: JSON.stringify(body),
        };
        const res = global.SubmitOnce ? await global.SubmitOnce.fetch(url, init, attempt) : await fetch(url, init);
        const json = await res.json();
        if (!res.ok || !json.success) throw new Error(json.message || 'เริ่มการชำระเงินไม่สำเร็จ');

        const intent = json.data;
        this._intentId = intent.id;

        // 3-D Secure ของบัตร: ต้องพาไปหน้าธนาคาร แล้วกลับมาตรวจสถานะจากเซิร์ฟเวอร์
        // (ห้ามเชื่อ query string ตอนกลับ)
        // รอบ 228: จดรายการไว้ก่อนออก (sessionStorage) ⇒ กลับมาแล้ว resume() ถามสถานะต่อ — ไม่ได้จด (โหมดส่วนตัว) = หน้าเดิมยังแสดงสถานะจริงจากเซิร์ฟเวอร์
        // รอบ 232: เซิร์ฟเวอร์ปิดรายการตั้งแต่สร้าง (charge ล้ม/ไม่มี QR/โหมดไม่ตรง) ⇒ บอกเหตุผลทันที ไม่รอ poll รอบแรก 2 วิ
        if (intent.status === 'Failed' || intent.status === 'Expired') {
          this._renderError(intent.failureMessage || 'เริ่มการชำระเงินไม่สำเร็จ — ลองใหม่อีกครั้ง', intent);
          return;
        }
        // ฝ่ายค้าน 232 B1: บัตรที่ไม่ต้อง 3-D Secure สำเร็จทันที (ไม่มี QR/ลิงก์) ⇒ หน้าสำเร็จ — ห้ามตกไปตาข่าย "ไม่มี QR/ลิงก์" (ลูกค้ากดจ่ายซ้ำ = ตัดบัตรสองครั้ง)
        if (intent.status === 'Succeeded') {
          this._stopped = true;
          this._renderDone(intent);
          if (typeof this._opts.onPaid === 'function') this._opts.onPaid(intent);
          return;
        }
        // รอบ 232: **QR ชนะลิงก์** — เซิร์ฟเวอร์ (GatewayChargeNextStep) ไม่ส่งทั้งคู่แล้ว แต่หน้านี้ยึดลำดับเดียวกันเผื่อรายการเก่าที่เก็บทั้งคู่ไว้ก่อนรอบ 232
        // (ที่มา: พร้อมเพย์มีทั้ง QR + authorize_uri ⇒ เดิมพาออกไปหน้า offsite แล้วถูกส่งกลับหน้าเดิมทันที "เด้งกลับหน้าเดิม" ไม่เห็น QR)
        if (!intent.qrPayload && intent.authorizeUrl && /^https:\/\//i.test(String(intent.authorizeUrl))) {
          this._rememberPending(intent);
          global.location.href = intent.authorizeUrl;
          return;
        }
        // บัตรรอผลธนาคาร (ไม่มีลิงก์ 3DS) ⇒ ไปหน้ารอผลตามปกติ — ตัวตัดสินอยู่ฝั่งเซิร์ฟเวอร์ (GatewayChargeNextStep.NeedsPayerStep) · ตาข่ายนี้ดูเฉพาะวิธีที่ต้องมี QR/ลิงก์
        const methodNow = intent.methodKind || intent.method || opts.method || 'PromptPay';
        if (!intent.qrPayload && methodNow !== 'ManualSlip' && methodNow !== 'Card') {
          // ไม่มีทั้ง QR และลิงก์ทั้งที่รายการยังรอ — ล้มดังแทนหน้าว่าง "กำลังรอการชำระเงิน…" (เซิร์ฟเวอร์ควรปิดรายการแล้ว — นี่คือตาข่ายชั้นที่สอง)
          this._renderError('ผู้ให้บริการไม่ส่ง QR/ลิงก์หน้าชำระเงินกลับมา — ปิดหน้านี้แล้วลองใหม่อีกครั้ง', intent);
          return;
        }

        this._renderPending(intent);
        this._poll(0, Date.now());
      } catch (e) {
        // ฝ่ายค้าน N2: โทเคนบัตรใช้ได้ครั้งเดียว — เริ่มไม่สำเร็จแล้วห้ามส่งโทเคนเดิมซ้ำ (เปิดใหม่ = กรอกบัตรใหม่)
        this._opts.cardToken = null;
        this._renderError(e.message);
      }
    },

    _statusUrl() {
      return this._opts.publicBase
        ? `${this._opts.publicBase}/intents/${this._intentId}`
        : `/api/companies/${this._opts.companyId}/pay/intents/${this._intentId}/status`;
    },

    _rememberPending(intent) {
      const st = storage();
      if (!st) return;
      try {
        st.setItem(PENDING_KEY, JSON.stringify({
          intentId: intent.id, publicBase: this._opts.publicBase || null, companyId: this._opts.companyId || null,
          customAuth: typeof this._opts.authHeader === 'function', hash: location.hash || '', at: Date.now(),
        }));
      } catch { /* เก็บไม่ได้ = กลับมาแล้วไม่เปิดหน้าต่างตรวจผลเอง (สถานะยังถูกต้องที่เซิร์ฟเวอร์) */ }
    },

    /** กลับจากหน้าธนาคาร (3-D Secure/แบงก์กิ้ง/วอลเล็ท) — เปิดหน้าต่าง "กำลังตรวจผล" แล้วถามสถานะจากเซิร์ฟเวอร์ (ไม่เชื่อ query) ·
     *  หน้าที่ใช้ authHeader ของตัวเองเรียก resume({ authHeader }) เอง · ปิดหน้าต่าง = โหลดหน้าใหม่ให้เห็นสถานะล่าสุด */
    resume(extra) {
      let marked = false;
      try { marked = new URL(location.href).searchParams.get(RETURN_PARAM) === '1'; } catch { marked = false; }
      if (!marked) return false;
      const st = storage();
      let rec = null;
      try { rec = st ? JSON.parse(st.getItem(PENDING_KEY) || 'null') : null; } catch { rec = null; }
      // ฝ่ายค้าน N1: หน้าที่ใช้ตัวสร้าง header ของตัวเอง ⇒ รอให้หน้านั้นเรียก resume({ authHeader }) เอง — ห้ามล้างเครื่องหมาย/รายการก่อน
      if (rec && rec.customAuth && !(extra && typeof extra.authHeader === 'function')) return false;
      // ล้างเครื่องหมายใน URL (กดรีเฟรชแล้วไม่ตรวจซ้ำ) + คืน hash เดิมของหน้า
      try {
        const u = new URL(location.href);
        u.searchParams.delete(RETURN_PARAM);
        if (rec && rec.hash) u.hash = rec.hash;
        history.replaceState(null, '', u.toString());
      } catch { /* เปลี่ยน URL ไม่ได้ไม่เป็นไร */ }
      if (!rec || !rec.intentId || (Date.now() - Number(rec.at || 0)) > PENDING_MAX_AGE_MS) return false;
      try { if (st) st.removeItem(PENDING_KEY); } catch { /* ignore */ }
      this._stopped = false;
      this._opts = Object.assign({ publicBase: rec.publicBase, companyId: rec.companyId,
        onClosed: () => { try { location.reload(); } catch { /* ignore */ } } }, extra || {});
      this._intentId = rec.intentId;
      this._mount();
      this._setBody('<div style="padding:28px;text-align:center;color:#64748b">กลับจากหน้าธนาคารแล้ว — กำลังตรวจผลการชำระเงินกับระบบ…</div>');
      this._poll(0, Date.now());
      return true;
    },

    _authHeader() {
      // รอบ 219 ส่วน B: หน้าแอดมิน (โทเคน admin_token) ส่งตัวสร้าง header ของตัวเองมา
      if (this._opts && typeof this._opts.authHeader === 'function') return this._opts.authHeader() || {};
      // หน้า storefront ของลูกค้าไม่มี token — โหมดสาธารณะจึงเดินผ่าน `/public-pay/...`
      // (คอมเมนต์เดิมตรงนี้เขียนว่า "endpoint ฝั่งนั้นเปิดสาธารณะอยู่แล้ว" ซึ่ง
      //  **ไม่จริง** — `/pay/*` เป็น [Authorize] ทั้งคลาส ⇒ widget นี้ใช้กับแขก
      //  ไม่ได้เลยตั้งแต่วันแรก และไม่มีหน้าไหนเรียกมันด้วย · แก้แล้วในรอบ 126)
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
      // รอบ 232 (232-Omise-2 · S3): ป้าย/หมายเหตุโหมดทดสอบมาจากเซิร์ฟเวอร์ (statusLabel/testNote) — หน้านี้ไม่มีข้อความของตัวเอง
      const testNote = intent.testNote
        ? `<div style="background:#fef3c7;color:#92400e;border-radius:8px;padding:8px 12px;font-size:12px;font-weight:700;margin-top:10px">⚠️ ${esc(intent.testNote)}</div>`
        : '';
      this._setBody(`
        <div style="padding:28px;text-align:center">
          <div style="font-size:40px">✅</div>
          <div style="font-size:17px;font-weight:800;color:#166534;margin:8px 0 4px">${esc(intent.statusLabel || 'ชำระเงินสำเร็จ')}</div>
          <div style="font-size:13px;color:#64748b">฿${money(intent.amount)}</div>
          ${testNote}
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
          const res = await fetch(this._statusUrl(), { headers: this._authHeader() });
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

  // รอบ 228: กลับจากหน้าธนาคาร ⇒ ตรวจผลต่อเองทุกหน้าที่โหลด widget (หน้าร้าน/ศูนย์บิล/แพ็กเกจ/ส่วนเสริม ไม่ต้องเรียกเอง)
  function autoResume() { try { PayWidget.resume(); } catch { /* ไม่ให้หน้าเดิมพัง */ } }
  if (document.readyState === 'loading') document.addEventListener('DOMContentLoaded', autoResume);
  else autoResume();
})(window);
