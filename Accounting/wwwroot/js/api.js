// ===== ตัวแสดง "กำลังทำงาน" กลาง — ทุกหน้าได้อัตโนมัติผ่าน API.request =====
//
// ที่มา (เจ้าของรายงาน รอบ 190 ข้อ 4): "หลังกดปุ่มคำสั่ง ระบบนิ่งเหมือนค้าง ไม่รู้ว่าทำงานอยู่
// ผู้ใช้จะไปกดเมนูอื่นต่อ" — ทั้งเว็บไม่มีตัวบอกสถานะกลางเลย แต่ละหน้าต่างคนต่างทำ (หรือไม่ทำ)
//
// กติกา (ล็อกด้วย tools/api_busy_indicator_sim.js ที่รันโค้ดไฟล์นี้จริง):
//   1. คำขอค้าง ≥ DELAY_MS (400ms) → แถบบนสุดของจอ + ข้อความว่ากำลังทำอะไร
//      คำขอที่จบก่อนนั้น → ไม่มีอะไรโผล่เลย (ไม่กระพริบ)
//   2. หลายคำขอซ้อนกัน → ซ่อนเมื่อ "หมดทุกตัว" เท่านั้น
//   3. คำสั่งเขียน (POST/PUT/PATCH/DELETE) ที่เกิดจากการกดปุ่ม → ปุ่มนั้นกดซ้ำไม่ได้ทันที
//      (ดักคลิกซ้ำแบบมองไม่เห็น) และเมื่อเกิน DELAY_MS ปุ่มถูก disable + ขึ้น "กำลังดำเนินการ…"
//      · ถ้าหน้าจัดการปุ่มเอง (disable ไว้ก่อนแล้ว) = ไม่แตะ · คืนสภาพเดิมเมื่อคำขอจบ (สำเร็จหรือล้ม)
//   4. นานเกิน LONG_MS → บอกเวลาที่ผ่านไป + "อย่าปิดหรือเปลี่ยนหน้า"
//   5. ล้ม/throw/redirect → ซ่อนเสมอ (finally ใน request)
//   6. งานเบื้องหลังที่ผู้ใช้ไม่ได้สั่ง (เช่นนับแจ้งเตือน) → ห่อด้วย API.quietly(() => …)
//
// ⚠️ หน้าที่เรียก fetch() ดิบข้าม API.request จะไม่ได้ตัวแสดงนี้ — ให้ย้ายมาใช้ API.get/post
const ApiBusy = {
  DELAY_MS: 400,
  LONG_MS: 8000,
  /** คลิกที่เกิดก่อนคำสั่งเขียนไม่เกินเท่านี้ ถือว่าเป็นปุ่มที่สั่งคำสั่งนั้น
   *  (เผื่อ confirm() ที่ผู้ใช้อ่านนาน) — คลิกถูก "ใช้แล้ว" เมื่อคำสั่งชุดนั้นจบ */
  CLICK_WINDOW_MS: 15000,
  BUTTON_SELECTOR: 'button, input[type="submit"], input[type="button"], a.btn, [role="button"]',
  BUSY_TEXT: 'กำลังดำเนินการ…',

  _active: new Set(),
  _btn: new Map(),
  _lastClick: null,
  _quiet: 0,
  _showTimer: null,
  _hideTimer: null,
  _tickTimer: null,
  _shown: false,
  _installed: false,
  _el: null,

  _now() { return Date.now(); },
  _hasDom() { return typeof document !== 'undefined' && !!document.body; },

  /** ข้อความบอกว่ากำลังทำอะไร — ดูจาก method + URL (ป้ายหน้าจอเท่านั้น ไม่ใช่กติกาธุรกิจ) */
  labelFor(method, url) {
    const m = String(method || 'GET').toUpperCase();
    const full = String(url || '').toLowerCase();
    const u = full.split('?')[0];
    if (/\/ocr\/(upload|scan|batch|bulk)|\/ocr\/[^/]+\/(retry|rescan)/.test(u)) return 'กำลังอ่านเอกสารด้วย OCR… อาจใช้เวลาถึง 1 นาที';
    if (/(bulk|batch)/.test(u) && /approve/.test(u)) return 'กำลังอนุมัติเอกสารเป็นชุด…';
    if (/\/approve(\/|$)/.test(u) || /[?&]approve=true/.test(full)) return 'กำลังอนุมัติ…';
    if (/\/(void|cancel)(\/|$)/.test(u)) return 'กำลังยกเลิกเอกสาร…';
    if (m === 'POST' && /\/attachments\//.test(u)) return 'กำลังอัปโหลดไฟล์แนบ…';
    if (/(bulk|batch|import)/.test(u)) return 'กำลังประมวลผลเป็นชุด…';
    if (/(pdf|export|download|xml)/.test(u)) return 'กำลังสร้างไฟล์…';
    if (m === 'GET') return 'กำลังโหลดข้อมูล…';
    if (m === 'DELETE') return 'กำลังลบ…';
    return 'กำลังบันทึก…';
  },

  /** ดักคลิก (capture) — จำปุ่มที่ผู้ใช้กดล่าสุด + กันกดซ้ำระหว่างคำสั่งยังไม่จบ */
  install() {
    if (this._installed || typeof document === 'undefined' || !document.addEventListener) return;
    this._installed = true;
    document.addEventListener('click', (e) => {
      const t = e.target && e.target.closest ? e.target.closest(this.BUTTON_SELECTOR) : null;
      if (t && t.hasAttribute && t.hasAttribute('data-api-busy')) {
        // ปุ่มนี้กำลังรอคำสั่งเดิม — กลืนคลิกซ้ำ (ไม่ให้ยิงคำสั่งเดียวกันสองรอบ)
        e.preventDefault();
        e.stopImmediatePropagation();
        return;
      }
      if (t && !(t.hasAttribute && t.hasAttribute('data-no-busy'))) this._lastClick = { el: t, at: this._now() };
    }, true);
    // กด Enter ส่งฟอร์ม/พิมพ์ต่อ = ไม่ใช่ปุ่มที่คลิกไว้ก่อนหน้า — ห้ามผูกคำสั่งถัดไปกับปุ่มเก่า
    document.addEventListener('keydown', () => { this._lastClick = null; }, true);
    // ปิด/เปลี่ยนหน้าระหว่างคำสั่งที่ผู้ใช้สั่งยังไม่จบ → ให้เบราว์เซอร์ถามก่อน
    if (typeof window !== 'undefined' && window.addEventListener) {
      window.addEventListener('beforeunload', (e) => {
        if (!this.hasPendingCommand()) return;
        e.preventDefault();
        e.returnValue = '';
      });
    }
  },

  /** มีคำสั่งเขียนที่ผู้ใช้กดปุ่มสั่ง ค้างเกิน DELAY_MS อยู่ไหม */
  hasPendingCommand() {
    const now = this._now();
    for (const t of this._active) if (t.btn && now - t.at >= this.DELAY_MS) return true;
    return false;
  },

  begin(method, url) {
    if (this._quiet > 0) return null;
    this.install();
    const m = String(method || 'GET').toUpperCase();
    const tok = { method: m, url, label: this.labelFor(m, url), at: this._now(), btn: null };
    this._active.add(tok);
    if (m !== 'GET') tok.btn = this._attachButton();
    if (this._hideTimer) { clearTimeout(this._hideTimer); this._hideTimer = null; }
    if (this._shown) this._render();
    else if (!this._showTimer) this._showTimer = setTimeout(() => { this._showTimer = null; this._show(); }, this.DELAY_MS);
    return tok;
  },

  end(tok) {
    if (!tok || !this._active.delete(tok)) return;   // เรียกซ้ำได้ไม่พัง
    if (tok.btn) this._detachButton(tok.btn);
    if (this._active.size === 0) {
      if (this._showTimer) { clearTimeout(this._showTimer); this._showTimer = null; }
      // ซ่อนหลังหนึ่ง tick — คำสั่งที่ยิงต่อกันใน await chain เดียว (บันทึก → อนุมัติ)
      // ต้องไม่ทำให้แถบดับแล้วติดใหม่ (กระพริบ) · ถ้ายังไม่เคยโชว์ก็ไม่มีอะไรให้ซ่อน
      if (this._shown && !this._hideTimer) {
        this._hideTimer = setTimeout(() => { this._hideTimer = null; if (this._active.size === 0) this._hide(); }, 0);
      }
    } else if (this._shown) this._render();
  },

  // ─── ปุ่ม ───
  _attachButton() {
    const lc = this._lastClick;
    if (!lc || !lc.el || this._now() - lc.at > this.CLICK_WINDOW_MS) return null;
    const el = lc.el;
    if (el.isConnected === false) return null;
    let st = this._btn.get(el);
    if (!st) {
      if (el.disabled) return null;   // หน้าจัดการสถานะปุ่มเองอยู่แล้ว — ไม่แย่ง
      st = { n: 0, applied: false, timer: null, release: null };
      this._btn.set(el, st);
      el.setAttribute('data-api-busy', '1');   // กันคลิกซ้ำทันที (ยังไม่เปลี่ยนหน้าตา = ไม่กระพริบ)
      st.timer = setTimeout(() => { st.timer = null; this._applyButton(el, st); }, this.DELAY_MS);
    }
    if (st.release) { clearTimeout(st.release); st.release = null; }
    st.n++;
    return el;
  },

  _applyButton(el, st) {
    if (st.n <= 0 || el.disabled) return;   // หน้าไป disable เองระหว่างรอ = ปล่อยให้หน้าคุม
    st.applied = true;
    st.prevHtml = el.innerHTML;
    st.prevMinWidth = el.style ? el.style.minWidth : '';
    if (el.style && el.offsetWidth) el.style.minWidth = el.offsetWidth + 'px';   // ปุ่มไม่หดจนเลย์เอาต์กระโดด
    el.disabled = true;
    el.setAttribute('aria-busy', 'true');
    el.setAttribute('aria-disabled', 'true');
    if (el.classList) el.classList.add('api-busy-btn');
    el.innerHTML = '<span class="api-busy-spin" aria-hidden="true"></span>' + this.BUSY_TEXT;
    st.busyHtml = el.innerHTML;   // อ่านกลับหลัง serialize — ใช้เทียบตอนคืนสภาพ
  },

  _detachButton(el) {
    const st = this._btn.get(el);
    if (!st) return;
    st.n--;
    if (st.n > 0) return;
    // เลื่อนไปหนึ่ง tick: handler ที่ยิงคำสั่งต่อกันใน await chain เดียว (บันทึก → อนุมัติ)
    // จะผูกกลับเข้าปุ่มเดิมก่อนปุ่มถูกปล่อย — ไม่มีช่องให้กดซ้ำระหว่างสองคำสั่ง
    st.release = setTimeout(() => this._releaseButton(el, st), 0);
  },

  _releaseButton(el, st) {
    st.release = null;
    if (st.n > 0) return;
    if (st.timer) { clearTimeout(st.timer); st.timer = null; }
    this._btn.delete(el);
    el.removeAttribute('data-api-busy');
    if (st.applied) {
      // คืนข้อความเดิมเฉพาะเมื่อหน้ายังไม่ได้เขียนทับเอง (หน้าที่ตั้งข้อความผลลัพธ์ต้องชนะ)
      if (el.innerHTML === st.busyHtml) el.innerHTML = st.prevHtml;
      el.disabled = false;
      el.removeAttribute('aria-busy');
      el.removeAttribute('aria-disabled');
      if (el.classList) el.classList.remove('api-busy-btn');
      if (el.style) el.style.minWidth = st.prevMinWidth || '';
    }
    if (this._lastClick && this._lastClick.el === el) this._lastClick = null;   // คลิกนี้ใช้แล้ว
  },

  // ─── แถบบนสุด ───
  _currentLabel() {
    let pick = null;
    for (const t of this._active) {
      // คำสั่งเขียนสำคัญกว่าการโหลด · ในกลุ่มเดียวกันเอาตัวที่เริ่มก่อน (ค้างนานสุด)
      if (!pick || (t.method !== 'GET' && pick.method === 'GET')) pick = t;
    }
    if (!pick) return '';
    const ms = this._now() - pick.at;
    return ms >= this.LONG_MS
      ? `${pick.label} ยังทำงานอยู่ (${Math.floor(ms / 1000)} วินาที) — กรุณาอย่าปิดหรือเปลี่ยนหน้า`
      : pick.label;
  },

  _ensureEl() {
    if (this._el || !this._hasDom()) return this._el;
    if (!document.getElementById('apiBusyStyle')) {
      const st = document.createElement('style');
      st.id = 'apiBusyStyle';
      st.textContent =
        '#apiBusyBar{position:fixed;top:0;left:0;right:0;z-index:100000;pointer-events:none;display:none}' +
        '#apiBusyBar.show{display:block}' +
        '#apiBusyBar .api-busy-track{height:3px;background:linear-gradient(90deg,transparent,#3b82f6,transparent);background-size:50% 100%;background-repeat:no-repeat;animation:apiBusySlide 1.1s linear infinite}' +
        '#apiBusyBar .api-busy-label{margin:6px auto 0;width:max-content;max-width:calc(100vw - 32px);background:#1e293b;color:#fff;font-size:13px;padding:6px 14px;border-radius:999px;box-shadow:0 4px 14px rgba(0,0,0,.2);display:flex;align-items:center;gap:8px}' +
        '.api-busy-spin{display:inline-block;width:12px;height:12px;border:2px solid currentColor;border-right-color:transparent;border-radius:50%;animation:apiBusySpin .7s linear infinite;vertical-align:-2px;margin-right:6px}' +
        '#apiBusyBar .api-busy-spin{margin-right:0}' +
        '.api-busy-btn{cursor:progress !important;opacity:.85}' +
        '@keyframes apiBusySlide{0%{background-position:-50% 0}100%{background-position:150% 0}}' +
        '@keyframes apiBusySpin{to{transform:rotate(360deg)}}';
      (document.head || document.body).appendChild(st);
    }
    const el = document.createElement('div');
    el.id = 'apiBusyBar';
    el.setAttribute('role', 'status');
    el.setAttribute('aria-live', 'polite');
    el.innerHTML = '<div class="api-busy-track"></div><div class="api-busy-label"><span class="api-busy-spin" aria-hidden="true"></span><span class="api-busy-text"></span></div>';
    document.body.appendChild(el);
    this._el = el;
    return el;
  },

  _render() {
    const el = this._ensureEl();
    if (!el) return;
    const txt = el.querySelector('.api-busy-text');
    if (txt) txt.textContent = this._currentLabel();   // textContent = ไม่มีทาง inject HTML
  },

  _show() {
    if (this._active.size === 0) return;
    this._shown = true;
    const el = this._ensureEl();
    if (el) { el.classList.add('show'); document.body.setAttribute('aria-busy', 'true'); }
    this._render();
    if (!this._tickTimer) this._tickTimer = setInterval(() => this._render(), 1000);
  },

  _hide() {
    this._shown = false;
    if (this._tickTimer) { clearInterval(this._tickTimer); this._tickTimer = null; }
    if (this._el) this._el.classList.remove('show');
    if (this._hasDom()) document.body.removeAttribute('aria-busy');
  },
};

// ===== API Client =====
const API = {
  baseUrl: '',
  token: null,

  init() {
    this.token = localStorage.getItem('token');
  },

  _t(key, fallback) {
    if (typeof I18n === 'undefined') return fallback;
    const v = I18n.t(key);
    return (v && v !== key) ? v : fallback;
  },

  // ═══ ข้อความ validation ต้องชี้ "ช่องไหน" ที่ผู้ใช้มองเห็น (ผู้ใช้รายงาน 2026-09-21) ═══
  // เดิมกดบันทึกที่หน้าตั้งค่าที่พักแล้วได้ toast:
  //   "One or more validation errors occurred. — Code: The Code field is required."
  // อังกฤษล้วน + ชื่อ property C# (`Code`) ที่ไม่ตรงกับป้ายใด ๆ บนจอ (ป้ายจริงคือ
  // "รหัส (ใช้ในเลขจอง RES-XXXX-…)") ⇒ ผู้ใช้หาไม่เจอว่าต้องแก้ตรงไหน
  //
  // เซิร์ฟเวอร์รู้แค่**ชื่อช่อง** (ส่งมาใน `data.fields` แบบ camelCase = ตรงกับ
  // `name="..."` บนฟอร์ม) · **ป้ายไทยอยู่ใน DOM ของหน้านี้อยู่แล้ว** ⇒ ฝั่งนี้แค่
  // ไปอ่านป้ายมาประกอบ ไม่ได้ถือสำเนากติกาใด ๆ (F2 ข้อ 5 "Server computes · page displays")
  //
  // ⚠️ กรณี "หาช่องไม่เจอบนหน้านี้" ต้องพูดตรง ๆ ว่าไม่เจอ — ห้ามสั่งให้ผู้ใช้ไป
  // กรอกของที่มองไม่เห็น (DECISION_DOCTRINE §1 G3 "ไม่รู้ ต้องบอกว่าไม่รู้")
  _fieldLabel(el) {
    if (!el) return '';
    const fld = el.closest('.fld');
    let lab = fld ? fld.querySelector('label') : null;
    if (!lab && el.id) lab = document.querySelector(`label[for="${CSS.escape(el.id)}"]`);
    if (!lab) lab = el.closest('label');
    const text = lab ? (lab.textContent || '').replace(/\s+/g, ' ').trim().replace(/\s*\*$/, '') : '';
    return text;
  },

  _fieldSection(el) {
    const card = el && el.closest ? el.closest('.card') : null;
    const h = card ? card.querySelector('h3, h4') : null;
    return h ? (h.textContent || '').replace(/\s+/g, ' ').trim() : '';
  },

  // หา input ของช่องชื่อนี้ — รองรับชื่อซ้อน ("lines[0].qty") โดยลองทั้งเส้นก่อน
  // แล้วค่อยถอยมาที่ส่วนท้าย (ฟอร์มส่วนใหญ่ตั้ง name เป็นชื่อช่องล้วน)
  // ⚠️ ทั้ง wwwroot มี input ที่ตั้ง `name=` แค่ 143 จาก 2,271 ตัว (กระจุกใน 10 ไฟล์)
  // — หน้าส่วนใหญ่ผูกช่องด้วย `id` แล้วอ่านด้วย `getElementById` ⇒ ถ้าหาด้วย
  // `[name=…]` อย่างเดียว จะหาไม่เจอเกือบทั้งระบบ (ทีมตรวจรอบ 189 A13/E-06)
  // ⇒ ลองทั้ง `name` · `data-field` · `id` ตามลำดับความชัดเจน
  _findFieldEl(name) {
    const tries = [name];
    const last = String(name).split('.').pop();
    if (last && last !== name) tries.push(last);
    for (const t of tries) {
      let esc = null;
      try { esc = CSS.escape(t); } catch (_) { continue; }
      for (const sel of [`[name="${esc}"]`, `[data-field="${esc}"]`, `#${esc}`]) {
        let el = null;
        try { el = document.querySelector(sel); } catch (_) { el = null; }
        if (el && 'value' in el) return el;
      }
    }
    return null;
  },

  /** สร้างข้อความไทยที่ระบุ "ป้ายที่ผู้ใช้เห็น" + ไฮไลต์ + พาไปที่ช่องแรกที่ผิด
   *  @returns {string} ข้อความสำหรับ toast (ว่าง = ประกอบไม่ได้ ให้ผู้เรียกใช้ข้อความเดิม) */
  describeFieldErrors(fields, messages) {
    if (!Array.isArray(fields) || fields.length === 0) return '';
    // ล้างไฮไลต์รอบก่อน มิฉะนั้นช่องที่แก้แล้วยังแดงค้าง (ทำให้ผู้ใช้ไล่ผิดช่อง)
    document.querySelectorAll('.has-error').forEach(el => {
      el.classList.remove('has-error');
      el.removeAttribute('aria-invalid');
    });

    const parts = [];
    let firstEl = null, firstName = '';
    fields.forEach((f, i) => {
      const why = (messages && messages[i]) ? String(messages[i]) : 'ค่าไม่ถูกต้อง';
      const el = this._findFieldEl(f);
      if (!el) {
        // ช่องนี้ไม่มีบนหน้าจอ — ฟอร์มส่งค่ามาเองโดยผู้ใช้ไม่เคยเห็น
        // หาไม่เจอ = **หาไม่เจอ** เท่านั้น — ห้ามสรุปสาเหตุ (ช่องอาจมีอยู่แต่ผูกด้วย
        // id/ชื่ออื่น หรืออยู่ในแท็บที่ยังไม่ถูกสร้าง) · ข้อความที่ระบุ "สาเหตุ"
        // ต้องตรวจสาเหตุนั้นจริง (F2 ข้อ 7) — เดิมเขียนว่า "ฟอร์มส่งค่ามาเอง"
        // ซึ่งผิดใน 94% ของหน้า (ทีมตรวจรอบ 189 A13)
        parts.push(`ช่อง "${f}" (ระบบไฮไลต์ให้อัตโนมัติไม่ได้ — กรุณามองหาช่องนี้บนหน้าจอ): ${why}`);
        return;
      }
      el.classList.add('has-error');
      el.setAttribute('aria-invalid', 'true');
      const label = this._fieldLabel(el) || f;
      const section = this._fieldSection(el);
      parts.push(`«${label}»${section ? ` (ในส่วน "${section}")` : ''}: ${why}`);
      if (!firstEl) { firstEl = el; firstName = f; }
    });

    // พาไปที่ช่องแรก — ถ้าหน้านั้นมีแท็บ/ส่วนที่ซ่อนอยู่ ให้หน้านั้นเปิดเองผ่าน hook
    // (ห้ามสั่ง unhide เอง — panel ที่ถูกปลดซ่อนมั่ว ๆ จะค้างทับแท็บอื่น ดู
    //  tools/tab_hidelist_check.py ที่เกิดจากบั๊กทรงนั้นโดยตรง)
    if (firstEl) {
      try {
        if (typeof Page !== 'undefined' && Page && typeof Page.revealField === 'function') {
          Page.revealField(firstName, firstEl);
        }
        firstEl.scrollIntoView({ behavior: 'smooth', block: 'center' });
        firstEl.focus({ preventScroll: true });
      } catch (_) { /* โฟกัสไม่ได้ไม่ใช่เหตุให้กลืนข้อความ */ }
    }

    const head = fields.length === 1 ? 'บันทึกไม่ได้ — ต้องแก้ 1 ช่อง' : `บันทึกไม่ได้ — ต้องแก้ ${fields.length} ช่อง`;
    return `${head}: ${parts.join(' · ')}`;
  },

  /** งานเบื้องหลังที่ผู้ใช้ไม่ได้สั่ง (badge แจ้งเตือน · poll) — ไม่ขึ้นตัวแสดง "กำลังทำงาน"
   *  ใช้: `API.quietly(() => API.get(url))` · ต้องเรียก API ภายใน fn แบบ synchronous
   *  (ตัวนับถูกอ่านตอนเริ่มคำขอ ก่อน await แรก) */
  quietly(fn) {
    ApiBusy._quiet++;
    try { return fn(); } finally { ApiBusy._quiet--; }
  },

  async request(method, url, data = null, isFormData = false, signal = null) {
    // ตัวแสดง "กำลังทำงาน" กลาง — เริ่มก่อน await แรก จบใน finally เสมอ (ล้ม/redirect ก็ซ่อน)
    const busyTok = ApiBusy.begin(method, url);
    try {
      return await this._requestCore(method, url, data, isFormData, signal);
    } finally {
      ApiBusy.end(busyTok);
    }
  },

  async _requestCore(method, url, data = null, isFormData = false, signal = null) {
    // Re-read token from localStorage on each request (handles token refresh by other tabs)
    this.token = localStorage.getItem('token');
    const headers = {};
    if (this.token) headers['Authorization'] = `Bearer ${this.token}`;
    if (!isFormData) headers['Content-Type'] = 'application/json';
    headers['Accept-Language'] = (typeof I18n !== 'undefined' && I18n.lang) ? I18n.lang : (localStorage.getItem('nextacc_lang') || 'th');

    // no-store: API JSON ต้องสดเสมอ — กัน browser/proxy/CDN cache GET response
    // (เคยเจอหน้าเงินมัดจำโชว์ 0 เพราะ document/deposits ถูก cache ตอนยังว่าง
    // ทั้งที่ backend มีข้อมูลแล้ว — endpoint ใหม่ที่ URL ไม่เคย cache กลับได้ข้อมูลสด)
    // cache-bust: no-store กันแค่ "ของใหม่" ไม่ล้างของที่ cache ไว้แล้ว (SW Cache
    // Storage / CDN object เก่า) → ใส่ timestamp ให้ URL ไม่ซ้ำทุกครั้ง = ทะลุทุก
    // cache layer ที่ key ด้วย URL (service worker match / browser / CDN) ถาวร
    if (method === 'GET') {
      url += (url.includes('?') ? '&' : '?') + '_t=' + Date.now();
    }
    const options = { method, headers, cache: 'no-store' };
    if (signal) options.signal = signal;   // AbortController support for long calls (bulk AI)
    if (data && !isFormData) options.body = JSON.stringify(data);
    if (data && isFormData) options.body = data;

    try {
      const res = await fetch(`${this.baseUrl}${url}`, options);
      if (res.status === 401) {
        // Auth API calls (login/register/sso) should throw error, not redirect
        const isAuthCall = url.startsWith('/api/auth/');
        if (!isAuthCall) {
          localStorage.removeItem('token');
          localStorage.removeItem('user');
          window.location.href = '/login.html';
          return;
        }
        const json = await res.json();
        throw new Error(json.message || this._t('api.invalidLogin', 'อีเมลหรือรหัสผ่านไม่ถูกต้อง'));
      }
      if (res.status === 403) {
        // During initial load, company may be stale — suppress and let loadCompanies() retry
        if (typeof Layout !== 'undefined' && !Layout._companiesLoaded) {
          console.warn('403 suppressed (companies not loaded yet):', url);
          return { success: false, data: null, message: 'company not ready' };
        }
        // Try to parse structured 403 (feature locked / subscription inactive)
        try {
          const json = await res.json();
          if (json.code === 'FEATURE_NOT_AVAILABLE' || json.code === 'SUBSCRIPTION_INACTIVE') {
            // Auto-redirect to subscription page on locked feature
            if (typeof Layout !== 'undefined' && Layout.toast) {
              Layout.toast(json.message || this._t('api.featureLocked', 'ฟีเจอร์นี้ไม่อยู่ในแพ็กเกจของคุณ'), 'error');
              setTimeout(() => { window.location.href = json.upgradeUrl || '/pages/subscription.html'; }, 1500);
            }
            const err = new Error(json.message || this._t('api.featureNotInPlan', 'ฟีเจอร์ไม่อยู่ในแพ็กเกจ'));
            err.code = json.code; err.feature = json.feature;
            throw err;
          }
          throw new Error(json.message || this._t('api.forbidden', 'คุณไม่มีสิทธิ์เข้าถึงข้อมูลนี้'));
        } catch (parseErr) {
          if (parseErr.code) throw parseErr;
          throw new Error(this._t('api.forbidden', 'คุณไม่มีสิทธิ์เข้าถึงข้อมูลนี้'));
        }
      }
      if (res.status === 429) {
        // Rate limited - silently skip, don't show error to user
        console.warn('Rate limited:', url);
        return { success: false, data: null, message: this._t('api.rateLimited', 'กรุณารอสักครู่') };
      }
      // HTTP 204 No Content — มาตรฐาน REST ของ DELETE ที่สำเร็จไม่มี body
      // (ASP.NET Core: return NoContent()). content-type ไม่ใช่ JSON เพราะ
      // ไม่มี body → ถือว่าสำเร็จ ไม่ต้อง parse
      if (res.status === 204) {
        return { success: true, data: null };
      }
      // Check content-type to avoid parsing HTML as JSON. Accept both the
      // standard "application/json" and ASP.NET Core's ProblemDetails variant
      // "application/problem+json" — both are valid JSON the JS side can read.
      const ct = res.headers.get('content-type') || '';
      const isJson = ct.includes('application/json') || ct.includes('+json');
      if (!isJson) {
        // Read the body as text so the error message can include what the
        // server actually said (often a useful single-line clue).
        let bodyText = '';
        try { bodyText = (await res.text()).slice(0, 300); } catch (_) {}
        const errMsg = `Server returned non-JSON (HTTP ${res.status}) for ${method} ${url}`;
        API._logError(method, url, res.status, errMsg + (bodyText ? ' · ' + bodyText : ''));
        throw new Error(errMsg + (bodyText ? '\n' + bodyText : '') + '. ' + this._t('api.serverNonJson', 'กรุณา restart server'));
      }
      const json = await res.json();
      if (!res.ok) {
        let msg = json.message || json.title || `Error ${res.status}`;
        // ซองของเราเอง (ApiResponse + ValidationErrorData): มีชื่อช่องมาด้วย ⇒
        // ประกอบข้อความจากป้ายไทยบนหน้าจอ + ไฮไลต์ช่องที่ผิด แทนที่จะโยนชื่อ
        // property C# ใส่หน้าผู้ใช้ (ดู describeFieldErrors)
        const vFields = json && json.data && Array.isArray(json.data.fields) ? json.data.fields : null;
        if (vFields && vFields.length) {
          const described = this.describeFieldErrors(vFields, json.data.messages);
          if (described) msg = described;
        } else if (json.errors) {
          // ProblemDetails ดิบของ ASP.NET (endpoint ที่ยังไม่ผ่าน factory ของเรา
          // เช่น 400 จาก middleware ชั้นนอก) — อย่างน้อยยังต่อท้ายให้เห็นว่าช่องไหน
          const details = Object.entries(json.errors).map(([k, v]) => `${k}: ${Array.isArray(v) ? v.join(', ') : v}`).join('; ');
          if (details) msg += ' — ' + details;
        }
        // Attach the response body + status to the thrown Error so callers
        // that care (e.g. approve-with-warnings flow) can inspect it. Most
        // catch sites just use err.message; the warning UI checks err.status
        // and err.body.data for the warnings list.
        const err = new Error(msg);
        err.status = res.status;
        err.body = json;
        throw err;
      }
      return json;
    } catch (err) {
      // TypeError or specific message strings indicate network failure (varies by browser/IAB)
      const msg = (err && err.message) || '';
      const isNetworkErr =
        err instanceof TypeError ||
        msg === 'Failed to fetch' ||
        /NetworkError|Network request failed|Load failed|connection|net::|Failed to load/i.test(msg);
      if (isNetworkErr) {
        const e = new Error(this._t('api.networkError', 'ไม่สามารถเชื่อมต่อเซิร์ฟเวอร์ได้ — กรุณาตรวจสอบการเชื่อมต่ออินเทอร์เน็ตและลองใหม่'));
        e.cause = err;
        throw e;
      }
      throw err;
    }
  },

  get(url, signal) { return this.request('GET', url, null, false, signal); },
  post(url, data, signal) { return this.request('POST', url, data, false, signal); },
  put(url, data, signal) { return this.request('PUT', url, data, false, signal); },
  del(url, signal) { return this.request('DELETE', url, null, false, signal); },
  // alias — 5 หน้า (employees/leave-types/project-time/roles) เรียก API.delete(...) ซึ่งไม่เคยมี
  // ⇒ TypeError ก่อนยิง request ⇒ ปุ่มลบตายเงียบตั้งแต่เขียนหน้า (ERP_REVIEW I-02)
  delete(url, signal) { return this.del(url, signal); },
  async upload(url, formData, signal) {
    const res = await this.request('POST', url, formData, true, signal);
    this.showStorageWarning(res);
    return res;
  },
  /** เพดานพื้นที่ของไฟล์แนบ = เตือน ไม่บล็อก (รอบ 193 ข้อ 30) — เซิร์ฟเวอร์บันทึกไฟล์แล้วและส่ง
   *  `data.storageWarning` มาเมื่อเกิน/ใกล้เต็มแพ็กเกจ · ข้อความมาจากเซิร์ฟเวอร์ (หน้าไม่คำนวณเอง)
   *  · หน้าที่ใช้ fetch ดิบเรียกตัวนี้กับ json ที่ได้เอง */
  showStorageWarning(res) {
    const w = res && res.data && typeof res.data.storageWarning === 'string' ? res.data.storageWarning : '';
    if (w && typeof Layout !== 'undefined' && Layout.toast) {
      try { Layout.toast(w, 'warning', 9000); } catch (_) {}
    }
  },
  _logError(method, url, status, msg) {
    try { fetch('/api/error-log/client', { method: 'POST', headers: { 'Content-Type': 'application/json' },
      body: JSON.stringify({ requestPath: url, httpMethod: method, statusCode: status, message: msg, source: 'Frontend' })
    }).catch(() => {}); } catch {}
  },

  // Auth
  login(email, password) { return this.post('/api/auth/login', { email, password }); },
  register(data) { return this.post('/api/auth/register', data); },
  // acceptedTerms/policyVersion = หลักฐานยินยอม PDPA ม.19 ส่งเฉพาะจากหน้าสมัคร
  // (หน้า login ไม่ส่ง → ผู้ใช้ใหม่ที่กด SSO ที่หน้า login จะถูก server ส่งกลับ
  // ไปหน้าสมัคร ซึ่งเป็นที่เดียวที่มีข้อความให้อ่านและช่องติ๊กให้ยินยอมจริง)
  // invitationToken = ผู้ถูกเชิญเข้าบริษัทที่เลือกสมัครด้วย SSO — เดิมไม่ส่งเลย
  // ⇒ คำเชิญไม่ถูกใช้ ผู้ใช้ไม่ได้เข้าบริษัทที่เชิญ ต้องให้แอดมินเชิญซ้ำ
  ssoLogin(provider, idToken, companyName, plan = null, acceptedTerms = false,
           policyVersion = null, invitationToken = null) {
    return this.post('/api/auth/sso', {
      provider, idToken, companyName, plan, acceptedTerms, policyVersion, invitationToken });
  },
  // หน้าสมัครที่มาด้วยตั๋ว SSO: "มีบัญชีอยู่แล้ว" — password ว่าง = เช็ค+ส่งลิงก์ยืนยัน ·
  // มี = ผูกทันที (คืน token) — ดู AuthService.SsoLinkExistingAsync
  ssoLinkExisting(ssoTicket, email, password = null) {
    return this.post('/api/auth/sso/link-existing', { ssoTicket, email, password });
  },
  changePassword(data) { return this.post('/api/auth/change-password', data); },

  // Company scoped
  c(companyId) {
    const base = `/api/companies/${companyId}`;
    return {
      // Dashboard
      dashboard: (q = '') => API.get(`${base}/dashboard${q}`),
      // Accounting
      getAccounts: (q = '') => API.get(`${base}/accounting/accounts${q}`),
      getPaymentChannels: () => API.get(`${base}/accounting/accounts/payment-channels`),
      createAccount: (d) => API.post(`${base}/accounting/accounts`, d),
      updateAccount: (id, d) => API.put(`${base}/accounting/accounts/${id}`, d),
      seedAccounts: (businessType, industryType) => {
        const params = [];
        if (businessType) params.push('businessType=' + businessType);
        if (industryType) params.push('industryType=' + industryType);
        return API.post(`${base}/accounting/accounts/seed${params.length ? '?' + params.join('&') : ''}`);
      },
      previewAccountTemplate: (businessType, industryType) => API.get(`${base}/accounting/accounts/template-preview?businessType=${businessType || 'JuristicPerson'}&industryType=${industryType || 'General'}`),
      getBusinessTypes: () => API.get(`${base}/accounting/business-types`),
      getJournals: (q = '') => API.get(`${base}/accounting/journals${q}`),
      getJournal: (id) => API.get(`${base}/accounting/journals/${id}`),
      createJournal: (d) => API.post(`${base}/accounting/journals`, d),
      updateJournal: (id, d) => API.put(`${base}/accounting/journals/${id}`, d),
      postJournal: (id) => API.post(`${base}/accounting/journals/${id}/post`),
      voidJournal: (id) => API.post(`${base}/accounting/journals/${id}/void`),
      deleteJournal: (id) => API.del(`${base}/accounting/journals/${id}`),
      reverseJournal: (id, data) => API.post(`${base}/accounting/journals/${id}/reverse`, data || {}),
      correctJournal: (id) => API.post(`${base}/accounting/journals/${id}/correct`),
      batchVoidJournals: (ids) => API.post(`${base}/accounting/journals/batch-void`, { entryIds: ids }),
      batchDeleteJournals: (ids) => API.post(`${base}/accounting/journals/batch-delete`, { entryIds: ids }),
      batchPostJournals: () => API.post(`${base}/accounting/journals/batch-post`),
      generalLedger: (q = '') => API.get(`${base}/accounting/reports/general-ledger${q}`),
      glDebug: (q = '') => API.get(`${base}/accounting/reports/gl-debug${q}`),
      rebuildLines: () => API.post(`${base}/accounting/reports/rebuild-lines`),
      repairDates: () => API.post(`${base}/accounting/reports/repair-dates`),
      trialBalance: (q = '') => API.get(`${base}/accounting/reports/trial-balance${q}`),
      balanceSheet: (q = '') => API.get(`${base}/accounting/reports/balance-sheet${q}`),
      profitLoss: (q = '') => API.get(`${base}/accounting/reports/profit-loss${q}`),
      cashFlow: (q = '') => API.get(`${base}/accounting/reports/cash-flow${q}`),
      getFiscalPeriods: () => API.get(`${base}/accounting/fiscal-periods`),
      createFiscalPeriod: (d) => API.post(`${base}/accounting/fiscal-periods`, d),
      ensureFiscalYear: (year) => API.post(`${base}/accounting/fiscal-periods/ensure-year?year=${year}`),
      closeFiscalPeriod: (id) => API.post(`${base}/accounting/fiscal-periods/${id}/close`),
      updateFiscalPeriod: (id, d, force = false) => API.put(`${base}/accounting/fiscal-periods/${id}${force ? '?force=true' : ''}`, d),
      deleteFiscalPeriod: (id) => API.del(`${base}/accounting/fiscal-periods/${id}`),
      // Documents
      getDocuments: (q = '') => API.get(`${base}/document${q}`),
      getDocumentPeriods: (q = '') => API.get(`${base}/document/periods${q}`),
      getDocument: (id) => API.get(`${base}/document/${id}`),
      getDocumentLinkedScan: (id) => API.get(`${base}/document/${id}/linked-scan`),
      createDocument: (d) => API.post(`${base}/document`, d),
      updateDocument: (id, d) => API.put(`${base}/document/${id}`, d),
      approveDocument: (id, body) => API.post(`${base}/document/${id}/approve`, body ?? {}),
      completeTaxInvoice: (id, body) => API.post(`${base}/document/${id}/complete-tax-invoice`, body),
      getDeposits: (status = '') => API.get(`${base}/document/deposits${status ? `?status=${status}` : ''}`),
      // Deposit Center (redesign): endpoint เดียวจบ — rows + KPI + GL tie-out + timestamp
      getDepositCenter: () => API.get(`${base}/document/deposit-center`),
      // จัดการบริษัท: ลบ (Owner + พิมพ์ชื่อยืนยัน) + ดูสิทธิ์สมาชิกรายคน
      deleteCompany: (confirmName) => API.del(`${base}?confirmName=${encodeURIComponent(confirmName)}`),
      getMemberPermissions: (userId) => API.get(`${base}/users/${userId}/permissions`),
      getDepositDiagnostics: () => API.get(`${base}/document/deposits/diagnostics`),
      realizeDeposit: (id, body) => API.post(`${base}/document/${id}/realize-deposit`, body),
      // รับรู้ภาษีขายรอเรียกเก็บของมัดจำ (21913 → 21911) โดยไม่แตะรายได้ —
      // ใช้เมื่อจุดรับผิด §78 เกิดก่อนส่งมอบ (ลูกค้าขอใบกำกับ ฯลฯ)
      recognizeDepositVat: (id, body) => API.post(`${base}/document/${id}/recognize-deposit-vat`, body ?? {}),
      refundDeposit: (id, body) => API.post(`${base}/document/${id}/refund-deposit`, body),
      applyDeposit: (invoiceId, body) => API.post(`${base}/document/${invoiceId}/apply-deposit`, body),
      searchJournalDeposits: (q) => API.get(`${base}/document/journal-deposits${q ? ('?q=' + encodeURIComponent(q)) : ''}`),
      applyJournalDeposit: (invoiceId, journalEntryNumber, applyDate) => API.post(`${base}/document/${invoiceId}/apply-journal-deposit`, { journalEntryNumber, applyDate: applyDate || null }),
      getContactDepositSummary: (contactId) => API.get(`${base}/document/contacts/${contactId}/deposit-summary`),
      getDocumentsByBooking: (bookingNumber) => API.get(`${base}/document/by-booking/${encodeURIComponent(bookingNumber)}`),
      getUndueInputVat: () => API.get(`${base}/document/undue-input-vat`),
      // รายงานผู้ติดต่อข้อมูลเสีย (อ่านอย่างเดียว · รอบ 193 ข้อ 19) — ที่อยู่ขึ้นต้น "/เลข" · สนญ. ที่อาจถูกที่อยู่สาขาทับ
      getContactHygiene: (registryLimit = 20, registryOffset = 0) => API.get(`${base}/contact-hygiene?registryLimit=${encodeURIComponent(registryLimit)}&registryOffset=${encodeURIComponent(registryOffset)}`),
      // ตรวจ §86/4 ของใบกำกับซื้อบนฟอร์ม ด้วยตัวตรวจเดียวกับตัวลงบัญชี (อ่านผู้ติดต่อจากฐาน)
      checkSupplierTaxInvoice: ({ contactId, branchCode, invoiceNumber, invoiceDate } = {}) => {
        const q = new URLSearchParams();
        if (contactId) q.set('contactId', contactId);
        if (branchCode) q.set('branchCode', branchCode);
        if (invoiceNumber) q.set('invoiceNumber', invoiceNumber);
        if (invoiceDate) q.set('invoiceDate', invoiceDate);
        return API.get(`${base}/document/supplier-tax-invoice-check?${q.toString()}`);
      },
      suggestPvAccounting: (body) => API.post(`${base}/document/ai-suggest-pv-accounting`, body),
      // reversalDate: วันที่ลงรายการกลับบัญชี (ว่าง = วันที่ของเอกสารเอง)
      voidDocument: (id, reversalDate) => API.post(
        `${base}/document/${id}/void${reversalDate ? `?reversalDate=${reversalDate}` : ''}`),
      // แก้ใบที่ยกเลิกไปแล้วตอนระบบยังใช้ "วันที่กด" — ย้าย JE กลับรายการเข้างวดที่ถูก
      // body.entries = [{journalEntryId, newDate}] กำหนดวันที่รายใบจากตารางในหน้าจอ
      redateVoidReversal: (id, newDate, body) => API.post(
        `${base}/document/${id}/redate-void-reversal${newDate ? `?newDate=${newDate}` : ''}`, body),
      // ดูก่อนย้าย — เอกสารใบเดียวมีตัวกลับได้หลายใบ ต้องเห็นว่าใบไหนย้ายไปวันไหน
      previewRedateVoidReversal: (id, newDate) => API.get(
        `${base}/document/${id}/redate-void-reversal/preview${newDate ? `?newDate=${newDate}` : ''}`),
      restoreDocument: (id) => API.post(`${base}/document/${id}/restore`),
      reclassifyLine: (id, body) => API.post(`${base}/document/${id}/reclassify-line`, body),
      reclassifyPaymentSource: (id, body) => API.post(`${base}/document/${id}/reclassify-payment-source`, body),
      // ตรวจ "ใบนี้เคยบันทึกไปแล้วหรือยัง" ก่อนสร้างจากสแกน (กันภาษีซื้อเบิ้ล)
      checkDuplicateDocument: (q) => API.get(`${base}/document/duplicate-check${q}`),
      // ตรวจสอบ/แก้ไขรายการบัญชี (JE) ของเอกสารที่อนุมัติแล้ว
      getDocumentJournalEntries: (id) => API.get(`${base}/document/${id}/journal-entries`),
      adjustDocumentJournalEntry: (id, jeId, body) => API.post(
        `${base}/document/${id}/journal-entries/${jeId}/adjust`, body),
      listAdjustingLines: (id) => API.get(`${base}/document/${id}/adjusting-lines`),
      saveAdjustingLines: (id, body) => API.put(`${base}/document/${id}/adjusting-lines`, body),
      deleteDocument: (id) => API.del(`${base}/document/${id}`),
      purgeDocument: (id, force = false, reason = null) =>
        API.del(`${base}/document/${id}/purge${force ? `?force=true&reason=${encodeURIComponent(reason || '')}` : ''}`),
      voidPayment: (paymentId) => API.post(`${base}/document/payments/${paymentId}/void`),
      // ออกใบเสร็จรับเงินให้การรับชำระที่บันทึกไปแล้ว (แถวที่มี JE แต่ไม่มีเอกสารคู่)
      issueReceiptForPayment: (paymentId) => API.post(`${base}/document/payments/${paymentId}/receipt`),
      convertDocument: (id, t) => API.post(`${base}/document/${id}/convert/${t}`),
      // ใบวางบิลรวมใบค้างชำระหลายใบ (ลูกค้ารายเดียว)
      getBillingOutstanding: (contactId) => API.get(`${base}/document/billing-note/outstanding?contactId=${contactId}`),
      createBillingNoteFromInvoices: (d) => API.post(`${base}/document/billing-note/from-invoices`, d),
      convertDocumentPartial: (id, t, body) => API.post(`${base}/document/${id}/convert-partial/${t}`, body),
      getDocumentFulfillment: (id) => API.get(`${base}/document/${id}/fulfillment`),
      getConversionTargets: (id) => API.get(`${base}/document/${id}/conversion-targets`),
      batchConvertDocuments: (ids, t) => API.post(`${base}/document/batch-convert/${t}`, { documentIds: ids }),
      createInvoiceFromObligation: (obligationId) => API.post(`${base}/document/from-obligation/${obligationId}`),
      writeOffBadDebt: (id, reason) => API.post(`${base}/document/${id}/write-off-bad-debt`, { reason }),
      // ออกใบกำกับภาษีเต็มรูป "แทน" ใบเสร็จ/ใบกำกับอย่างย่อ (§86/6 → §86/4)
      issueFullTaxInvoice: (id, reason) => API.post(`${base}/document/${id}/issue-full-tax-invoice`, { reason }),
      // Contacts
      getContacts: (q = '') => API.get(`${base}/document/contacts${q}`),
      createContact: (d) => API.post(`${base}/document/contacts`, d),
      getContactDuplicates: () => API.get(`${base}/document/contacts/duplicates`),
      mergeContacts: (d) => API.post(`${base}/document/contacts/merge`, d),
      updateContact: (id, d) => API.put(`${base}/document/contacts/${id}`, d),
      getContact: (id) => API.get(`${base}/document/contacts/${id}`),
      deleteContact: (id) => API.del(`${base}/document/contacts/${id}`),
      getContactSmartDefaults: (id) => API.get(`${base}/document/contacts/${id}/smart-defaults`),
      parseAddress: (text) => API.post(`${base}/document/contacts/parse-address`, { address: text }),
      // Payments
      getPayments: (q = '') => API.get(`${base}/document/payments${q}`),
      createPayment: (d) => API.post(`${base}/document/payments`, d),
      // Products
      getProducts: (q = '') => API.get(`${base}/product${q}`),
      getProduct: (id) => API.get(`${base}/product/${id}`),
      createProduct: (d) => API.post(`${base}/product`, d),
      updateProduct: (id, d) => API.put(`${base}/product/${id}`, d),
      deleteProduct: (id) => API.del(`${base}/product/${id}`),
      adjustStock: (d) => API.post(`${base}/product/stock/adjust`, d),
      getStockMovements: (productId) => API.get(`${base}/product/${productId}/stock/movements`),
      uploadProductImage: (productId, formData) => API.upload(`${base}/product/${productId}/images`, formData),
      deleteProductImage: (productId, url) => API.del(`${base}/product/${productId}/images?url=${encodeURIComponent(url)}`),
      getLowStock: () => API.get(`${base}/product/stock/low`),
      // Unit Conversions
      getUnitConversions: (productId) => API.get(`${base}/product/${productId}/unit-conversions`),
      createUnitConversion: (productId, d) => API.post(`${base}/product/${productId}/unit-conversions`, d),
      deleteUnitConversion: (id) => API.del(`${base}/product/unit-conversions/${id}`),
      convertUnit: (d) => API.post(`${base}/product/unit-conversions/convert`, d),
      // Product Categories
      getProductCategories: () => API.get(`${base}/product/categories`),
      createProductCategory: (d) => API.post(`${base}/product/categories`, d),
      deleteProductCategory: (id) => API.del(`${base}/product/categories/${id}`),
      // Stock Count
      getStockCounts: () => API.get(`${base}/product/stock-counts`),
      getStockCount: (id) => API.get(`${base}/product/stock-counts/${id}`),
      createStockCount: (d) => API.post(`${base}/product/stock-counts`, d),
      updateStockCountLines: (id, d) => API.put(`${base}/product/stock-counts/${id}/lines`, d),
      applyStockCount: (id) => API.post(`${base}/product/stock-counts/${id}/apply`),
      // Inventory Valuation & Reports
      getInventoryValuation: () => API.get(`${base}/product/inventory/valuation`),
      getStockBalance: (q = '') => API.get(`${base}/product/inventory/balance${q}`),
      getStockAging: () => API.get(`${base}/product/inventory/aging`),
      getMovementSummary: (q) => API.get(`${base}/product/inventory/movement-summary${q}`),
      createInventorySnapshot: (d) => API.post(`${base}/product/inventory/snapshots`, d),
      getInventorySnapshots: () => API.get(`${base}/product/inventory/snapshots`),
      getInventorySnapshotDetail: (id) => API.get(`${base}/product/inventory/snapshots/${id}`),
      // Supplies (วัสดุสิ้นเปลือง)
      useSupplies: (d) => API.post(`${base}/product/supplies/use`, d),
      getSuppliesUsageHistory: (productId) => API.get(`${base}/product/${productId}/supplies/usage`),
      getSuppliesUsageSummary: (q) => API.get(`${base}/product/supplies/usage-summary${q}`),
      getSuppliesBalance: (q = '') => API.get(`${base}/product/supplies/balance${q}`),
      // Financial Management
      createPrepaid: (d) => API.post(`${base}/financial/prepaid`, d),
      getPrepaids: () => API.get(`${base}/financial/prepaid`),
      getPrepaidDetail: (id) => API.get(`${base}/financial/prepaid/${id}`),
      processAmortization: (date) => API.post(`${base}/financial/prepaid/process?asOfDate=${date}`),
      // เงินมัดจำ/ประกันของโมดูล FinancialManagement — คนละตารางกับ "มัดจำจากเอกสาร" (document/deposits)
      // ชื่อเดิมซ้ำกับกลุ่มเอกสารด้านบน ⇒ ตัวหลังทับเงียบ ๆ: หน้า deposits.html/deposit-center.html
      // (มัดจำจากใบเสร็จ) ยิงไป /financial/deposits ผิดโมดูลมาตลอด — ต้องมีชื่อของตัวเอง
      createFinancialDeposit: (d) => API.post(`${base}/financial/deposits`, d),
      getFinancialDeposits: (dir = '') => API.get(`${base}/financial/deposits${dir ? '?direction=' + dir : ''}`),
      refundFinancialDeposit: (id, d) => API.post(`${base}/financial/deposits/${id}/refund`, d),
      createBadDebt: (d) => API.post(`${base}/financial/bad-debt`, d),
      getBadDebts: () => API.get(`${base}/financial/bad-debt`),
      postBadDebt: (id) => API.post(`${base}/financial/bad-debt/${id}/post`),
      createObsolescence: (d) => API.post(`${base}/financial/inventory-obsolescence`, d),
      getObsolescences: () => API.get(`${base}/financial/inventory-obsolescence`),
      postObsolescence: (id) => API.post(`${base}/financial/inventory-obsolescence/${id}/post`),
      createAccrued: (d) => API.post(`${base}/financial/accrued`, d),
      getAccrueds: () => API.get(`${base}/financial/accrued`),
      payAccrued: (id, d) => API.post(`${base}/financial/accrued/${id}/pay`, d),
      calculateCIT: (d) => API.post(`${base}/financial/cit`, d),
      getCITs: () => API.get(`${base}/financial/cit`),
      postCIT: (id) => API.post(`${base}/financial/cit/${id}/post`),
      createAppropriation: (d) => API.post(`${base}/financial/profit-appropriation`, d),
      getAppropriations: () => API.get(`${base}/financial/profit-appropriation`),
      approveAppropriation: (id) => API.post(`${base}/financial/profit-appropriation/${id}/approve`),
      createCapital: (d) => API.post(`${base}/financial/capital`, d),
      getCapitals: () => API.get(`${base}/financial/capital`),
      completeCapital: (id) => API.post(`${base}/financial/capital/${id}/complete`),
      createInvestment: (d) => API.post(`${base}/financial/investments`, d),
      getInvestments: () => API.get(`${base}/financial/investments`),
      sellInvestment: (id, d) => API.post(`${base}/financial/investments/${id}/sell`, d),
      // Bank
      getBankAccounts: () => API.get(`${base}/bank/accounts`),
      createBankAccount: (d) => API.post(`${base}/bank/accounts`, d),
      updateBankAccount: (id, d) => API.put(`${base}/bank/accounts/${id}`, d),
      getTransactions: (id, q = '') => API.get(`${base}/bank/accounts/${id}/transactions${q}`),
      createTransaction: (d) => API.post(`${base}/bank/transactions`, d),
      reconcile: (d) => API.post(`${base}/bank/reconcile`, d),
      autoMatch: (id) => API.post(`${base}/bank/accounts/${id}/auto-match`),
      getUnreconciled: (id) => API.get(`${base}/bank/accounts/${id}/unreconciled`),
      importBankStatement: (d) => API.post(`${base}/bank/import-statement`, d),
      aiSmartMatch: (id, d) => API.post(`${base}/bank/accounts/${id}/ai-match`, d || {}),
      getReconciliationSummary: (id) => API.get(`${base}/bank/accounts/${id}/reconciliation-summary`),
      batchReconcile: (d) => API.post(`${base}/bank/batch-reconcile`, d),
      unmatchTransaction: (d) => API.post(`${base}/bank/unmatch`, d),
      deleteBankTransaction: (txnId) => API.del(`${base}/bank/transactions/${txnId}`),
      getMatchCandidates: (txnId) => API.get(`${base}/bank/transactions/${txnId}/match-candidates`),
      createJeFromTxn: (txnId, d) => API.post(`${base}/bank/transactions/${txnId}/create-je`, d),
      getReconciliationDetail: (accountId) => API.get(`${base}/bank/accounts/${accountId}/reconciliation-detail`),
      getMatchInfo: (txnId) => API.get(`${base}/bank/transactions/${txnId}/match-info`),
      getMatchIssues: (accountId) => API.get(`${base}/bank/accounts/${accountId}/match-issues`),
      aiSuggestMatch: (txnId) => API.get(`${base}/bank/transactions/${txnId}/ai-suggest-match`),
      bulkDeleteBankTransactions: (d) => API.post(`${base}/bank/transactions/bulk-delete`, d),
      // Reconciliation Group (M:N + Net-off)
      createReconciliationGroup: (d) => API.post(`${base}/bank/reconciliation-groups`, d),
      getReconciliationGroup: (id) => API.get(`${base}/bank/reconciliation-groups/${id}`),
      listReconciliationGroups: (accId, q = '') => API.get(`${base}/bank/accounts/${accId}/reconciliation-groups${q}`),
      unreconcileGroup: (id) => API.del(`${base}/bank/reconciliation-groups/${id}`),
      getUnmatchedItems: (accId, q = '') => API.get(`${base}/bank/accounts/${accId}/unmatched-items${q}`),
      getLearnedSuggestions: (txnId) => API.get(`${base}/bank/transactions/${txnId}/learned-suggestions`),
      // Open Banking
      getConnections: () => API.get(`${base}/open-banking/connections`),
      createConnection: (d) => API.post(`${base}/open-banking/connections`, d),
      updateConnection: (id, d) => API.put(`${base}/open-banking/connections/${id}`, d),
      deleteConnection: (id) => API.del(`${base}/open-banking/connections/${id}`),
      syncConnection: (id, q = '') => API.post(`${base}/open-banking/connections/${id}/sync${q}`),
      getConnectionImports: (id) => API.get(`${base}/open-banking/connections/${id}/imports`),
      importBankFile: (accountId, format, base64, forceOverwrite = false) => API.post(`${base}/bank/import-statement`, { bankAccountId: accountId, fileFormat: format, base64Content: base64, forceOverwrite }),
      // Tax
      getTaxReports: (q = '') => API.get(`${base}/tax${q}`),
      getTaxReport: (id) => API.get(`${base}/tax/${id}`),
      autoRefreshTaxReports: (months = 2) => API.post(`${base}/tax/auto-refresh?months=${months}`),
      generateTaxReport: (d) => API.post(`${base}/tax/generate`, d),
      fileTaxReport: (id) => API.post(`${base}/tax/${id}/file`),
      updateTaxReport: (id, d) => API.put(`${base}/tax/${id}`, d),
      regenerateTaxReport: (id) => API.post(`${base}/tax/${id}/regenerate`),
      deleteTaxReport: (id) => API.del(`${base}/tax/${id}`),
      vatDebug: (year, month) => API.get(`${base}/tax/vat-debug?year=${year}&month=${month}`),
      getPullableDocuments: (reportId, q = '') => API.get(`${base}/tax/${reportId}/pullable-documents${q}`),
      pullDocumentIntoReport: (reportId, documentId) => API.post(`${base}/tax/${reportId}/pull-document`, { documentId }),
      // Tax Filing Export
      exportPnd1: (year, month) => `${base}/tax-filing-export/pnd1?year=${year}&month=${month}`,
      exportPnd3: (year, month) => `${base}/tax-filing-export/pnd3?year=${year}&month=${month}`,
      exportPnd53: (year, month) => `${base}/tax-filing-export/pnd53?year=${year}&month=${month}`,
      exportPnd1k: (year) => `${base}/tax-filing-export/pnd1k?year=${year}`,
      exportPp30: (year, month) => `${base}/tax-filing-export/pp30?year=${year}&month=${month}`,
      exportSso110: (year, month) => `${base}/tax-filing-export/sso110?year=${year}&month=${month}`,
      exportSso110Excel: (year, month) => `${base}/tax-filing-export/sso110-excel?year=${year}&month=${month}`,
      previewTaxExport: (formCode, year, month) => API.get(`${base}/tax-filing-export/preview/${formCode}?year=${year}&month=${month || 0}`),
      // WHT
      getWhtCerts: (q = '') => API.get(`${base}/withholding-tax-certs${q}`),
      getWhtCert: (id) => API.get(`${base}/withholding-tax-certs/${id}`),
      createWhtCert: (d) => API.post(`${base}/withholding-tax-certs`, d),
      updateWhtCert: (id, d) => API.put(`${base}/withholding-tax-certs/${id}`, d),
      issueWhtCert: (id) => API.post(`${base}/withholding-tax-certs/${id}/issue`),
      voidWhtCert: (id) => API.post(`${base}/withholding-tax-certs/${id}/void`),
      deleteWhtCert: (id) => API.del(`${base}/withholding-tax-certs/${id}`),
      getWhtByContact: (contactId, q = '') => API.get(`${base}/withholding-tax-certs/contacts/${contactId}${q}`),
      autoGenerateWht: (d) => API.post(`${base}/withholding-tax-certs/auto-generate`, d),
      getPendingWht: (q = '') => API.get(`${base}/withholding-tax-certs/pending${q}`),
      dismissPendingWht: (docId) => API.post(`${base}/withholding-tax-certs/pending/${docId}/dismiss`, {}),
      bulkGenerateWht: (d) => API.post(`${base}/withholding-tax-certs/bulk-generate`, d),
      // ===== CMS / Website Builder =====
      cmsListSites: (q = '') => API.get(`${base}/cms/sites${q}`),
      cmsGetSite: (id) => API.get(`${base}/cms/sites/${id}`),
      cmsCreateSite: (d) => API.post(`${base}/cms/sites`, d),
      cmsUpdateSite: (id, d) => API.put(`${base}/cms/sites/${id}`, d),
      cmsDeleteSite: (id) => API.del(`${base}/cms/sites/${id}`),
      cmsPublishSite: (id) => API.post(`${base}/cms/sites/${id}/publish`, {}),
      cmsApplySiteTemplate: (id, d) => API.post(`${base}/cms/sites/${id}/apply-template`, d),

      // CMS commerce — order/booking management for the site owner
      cmsListOrders: (siteId, q = '') => API.get(`${base}/cms/sites/${siteId}/commerce/orders${q}`),
      cmsGetOrder: (siteId, orderId) => API.get(`${base}/cms/sites/${siteId}/commerce/orders/${orderId}`),
      cmsUpdateOrderStatus: (siteId, orderId, d) => API.put(`${base}/cms/sites/${siteId}/commerce/orders/${orderId}/status`, d),
      cmsListBookings: (siteId, q = '') => API.get(`${base}/cms/sites/${siteId}/booking/bookings${q}`),
      cmsGetBooking: (siteId, id) => API.get(`${base}/cms/sites/${siteId}/booking/bookings/${id}`),
      cmsUpdateBookingStatus: (siteId, id, d) => API.put(`${base}/cms/sites/${siteId}/booking/bookings/${id}/status`, d),

      // Lodging — ธุรกิจที่พัก (ตั้งค่า + front desk) — ทุก endpoint ใต้ /lodging
      lodgingProperties: () => API.get(`${base}/lodging/properties`),
      lodgingGetProperty: (pid) => API.get(`${base}/lodging/properties/${pid}`),
      lodgingCreateProperty: (d) => API.post(`${base}/lodging/properties`, d),
      lodgingUpdateProperty: (pid, d) => API.put(`${base}/lodging/properties/${pid}`, d),
      lodgingRoomTypes: (pid) => API.get(`${base}/lodging/properties/${pid}/room-types`),
      lodgingSaveRoomType: (d) => API.post(`${base}/lodging/room-types`, d),
      lodgingDeleteRoomType: (id) => API.del(`${base}/lodging/room-types/${id}`),
      lodgingUnits: (pid) => API.get(`${base}/lodging/properties/${pid}/units`),
      lodgingSaveUnit: (d) => API.post(`${base}/lodging/units`, d),
      lodgingDeleteUnit: (id) => API.del(`${base}/lodging/units/${id}`),
      lodgingSetUnitStatus: (id, d) => API.put(`${base}/lodging/units/${id}/status`, d),
      lodgingRatePlans: (pid) => API.get(`${base}/lodging/properties/${pid}/rate-plans`),
      lodgingSaveRatePlan: (d) => API.post(`${base}/lodging/rate-plans`, d),
      lodgingDeleteRatePlan: (id) => API.del(`${base}/lodging/rate-plans/${id}`),
      lodgingSeasons: (pid) => API.get(`${base}/lodging/properties/${pid}/seasons`),
      lodgingSaveSeason: (d) => API.post(`${base}/lodging/seasons`, d),
      lodgingDeleteSeason: (id) => API.del(`${base}/lodging/seasons/${id}`),
      lodgingRateOverrides: (pid, q) => API.get(`${base}/lodging/properties/${pid}/rate-overrides${q}`),
      lodgingSaveRateOverrides: (d) => API.post(`${base}/lodging/rate-overrides`, d),
      lodgingPolicies: (pid) => API.get(`${base}/lodging/properties/${pid}/policies`),
      lodgingSavePolicy: (d) => API.post(`${base}/lodging/policies`, d),
      lodgingDeletePolicy: (id) => API.del(`${base}/lodging/policies/${id}`),
      lodgingExtras: (pid) => API.get(`${base}/lodging/properties/${pid}/extras`),
      lodgingSaveExtra: (d) => API.post(`${base}/lodging/extras`, d),
      lodgingExtrasNeedsReselect: () => API.get(`${base}/lodging/extras/needs-reselect`),
      lodgingDeleteExtra: (id) => API.del(`${base}/lodging/extras/${id}`),
      lodgingSearch: (pid, d) => API.post(`${base}/lodging/properties/${pid}/search`, d),
      lodgingQuote: (pid, d) => API.post(`${base}/lodging/properties/${pid}/quote`, d),
      lodgingCreateReservation: (pid, d) => API.post(`${base}/lodging/properties/${pid}/reservations`, d),
      lodgingReservations: (q = '') => API.get(`${base}/lodging/reservations${q}`),
      lodgingGetReservation: (id) => API.get(`${base}/lodging/reservations/${id}`),
      lodgingUpdateReservation: (id, d) => API.put(`${base}/lodging/reservations/${id}`, d),
      lodgingConfirm: (id, d) => API.post(`${base}/lodging/reservations/${id}/confirm`, d),
      lodgingRejectSlip: (id, d) => API.post(`${base}/lodging/reservations/${id}/reject-slip`, d),
      lodgingUploadImage: (formData) => API.upload(`${base}/lodging/images`, formData),
      lodgingAssign: (id, d) => API.post(`${base}/lodging/reservations/${id}/assign`, d),
      lodgingCheckIn: (id, d) => API.post(`${base}/lodging/reservations/${id}/check-in`, d),
      lodgingAddCharge: (id, d) => API.post(`${base}/lodging/reservations/${id}/charges`, d),
      lodgingCancelCharge: (id, chargeId) => API.del(`${base}/lodging/reservations/${id}/charges/${chargeId}`),
      lodgingCheckOut: (id, d) => API.post(`${base}/lodging/reservations/${id}/check-out`, d),
      lodgingCancel: (id, d) => API.post(`${base}/lodging/reservations/${id}/cancel`, d),
      lodgingNoShow: (id, d) => API.post(`${base}/lodging/reservations/${id}/no-show`, d),
      lodgingRefundPaid: (id, d) => API.post(`${base}/lodging/reservations/${id}/refund-paid`, d),
      lodgingReschedule: (id, d) => API.post(`${base}/lodging/reservations/${id}/reschedule`, d),
      lodgingTasks: (pid, q = '') => API.get(`${base}/lodging/properties/${pid}/housekeeping${q}`),
      lodgingCreateTask: (d) => API.post(`${base}/lodging/housekeeping`, d),
      lodgingUpdateTask: (id, d) => API.put(`${base}/lodging/housekeeping/${id}/status`, d),
      lodgingGuestRequests: (pid, q = '') => API.get(`${base}/lodging/properties/${pid}/guest-requests${q}`),
      lodgingResolveRequest: (id, d) => API.put(`${base}/lodging/guest-requests/${id}`, d),
      lodgingDashboard: (pid, q = '') => API.get(`${base}/lodging/properties/${pid}/dashboard${q}`),
      lodgingCalendar: (pid, q) => API.get(`${base}/lodging/properties/${pid}/calendar${q}`),
      cmsListGateways: (siteId) => API.get(`${base}/cms/sites/${siteId}/commerce/payment-gateways`),
      cmsCreateGateway: (siteId, d) => API.post(`${base}/cms/sites/${siteId}/commerce/payment-gateways`, d),
      cmsUpdateGateway: (siteId, id, d) => API.put(`${base}/cms/sites/${siteId}/commerce/payment-gateways/${id}`, d),
      cmsGetCommerceConfig: (siteId) => API.get(`${base}/cms/sites/${siteId}/commerce/config`),
      cmsUpdateCommerceConfig: (siteId, d) => API.put(`${base}/cms/sites/${siteId}/commerce/config`, d),
      cmsListLeads: (siteId, q = '') => API.get(`${base}/cms/sites/${siteId}/leads${q}`),
      cmsGetLead: (siteId, id) => API.get(`${base}/cms/sites/${siteId}/leads/${id}`),
      cmsUpdateLeadStatus: (siteId, id, d) => API.put(`${base}/cms/sites/${siteId}/leads/${id}/status`, d),
      cmsConvertLeadToQuotation: (siteId, id, d) => API.post(`${base}/cms/sites/${siteId}/leads/${id}/convert-to-quotation`, d),
      cmsListThemes: () => API.get(`${base}/cms/themes`),
      cmsGetTheme: (id) => API.get(`${base}/cms/themes/${id}`),
      cmsCreateTheme: (d) => API.post(`${base}/cms/themes`, d),
      cmsUpdateTheme: (id, d) => API.put(`${base}/cms/themes/${id}`, d),
      cmsDeleteTheme: (id) => API.del(`${base}/cms/themes/${id}`),
      // Pages + blocks (per site)
      cmsListPages: (siteId, q = '') => API.get(`${base}/cms/sites/${siteId}/content/pages${q}`),
      cmsGetPage: (siteId, pageId) => API.get(`${base}/cms/sites/${siteId}/content/pages/${pageId}`),
      cmsCreatePage: (siteId, d) => API.post(`${base}/cms/sites/${siteId}/content/pages`, d),
      cmsUpdatePage: (siteId, pageId, d) => API.put(`${base}/cms/sites/${siteId}/content/pages/${pageId}`, d),
      cmsDeletePage: (siteId, pageId) => API.del(`${base}/cms/sites/${siteId}/content/pages/${pageId}`),
      cmsPublishPage: (siteId, pageId) => API.post(`${base}/cms/sites/${siteId}/content/pages/${pageId}/publish`, {}),
      cmsAddBlock: (siteId, pageId, d) => API.post(`${base}/cms/sites/${siteId}/content/pages/${pageId}/blocks`, d),
      cmsUpdateBlock: (siteId, pageId, blockId, d) => API.put(`${base}/cms/sites/${siteId}/content/pages/${pageId}/blocks/${blockId}`, d),
      cmsDeleteBlock: (siteId, pageId, blockId) => API.del(`${base}/cms/sites/${siteId}/content/pages/${pageId}/blocks/${blockId}`),
      cmsReorderBlocks: (siteId, pageId, items) => API.post(`${base}/cms/sites/${siteId}/content/pages/${pageId}/blocks/reorder`, { items }),
      // Domains (per site)
      cmsListDomains: (siteId) => API.get(`${base}/cms/sites/${siteId}/domains`),
      cmsAddDomain: (siteId, d) => API.post(`${base}/cms/sites/${siteId}/domains`, d),
      cmsVerifyDomain: (siteId, domainId) => API.post(`${base}/cms/sites/${siteId}/domains/${domainId}/verify`, {}),
      cmsDeleteDomain: (siteId, domainId) => API.del(`${base}/cms/sites/${siteId}/domains/${domainId}`),
      // Media (per site)
      cmsListMedia: (siteId, q='') => API.get(`${base}/cms/sites/${siteId}/content/media${q}`),
      cmsUploadMedia: (siteId, formData) => API.upload(`${base}/cms/sites/${siteId}/content/media`, formData),
      cmsDeleteMedia: (siteId, mediaId) => API.del(`${base}/cms/sites/${siteId}/content/media/${mediaId}`),
      // Navigations (per site)
      cmsListNavigations: (siteId) => API.get(`${base}/cms/sites/${siteId}/content/navigations`),
      cmsCreateNavigation: (siteId, d) => API.post(`${base}/cms/sites/${siteId}/content/navigations`, d),
      cmsDeleteNavigation: (siteId, navId) => API.del(`${base}/cms/sites/${siteId}/content/navigations/${navId}`),
      cmsAddMenuItem: (siteId, navId, d) => API.post(`${base}/cms/sites/${siteId}/content/navigations/${navId}/items`, d),
      cmsDeleteMenuItem: (siteId, navId, itemId) => API.del(`${base}/cms/sites/${siteId}/content/navigations/${navId}/items/${itemId}`),
      // E-commerce (per site)
      cmsListProducts: (siteId, q='') => API.get(`${base}/cms/sites/${siteId}/commerce/products${q}`),
      cmsCreateProduct: (siteId, d) => API.post(`${base}/cms/sites/${siteId}/commerce/products`, d),
      cmsUpdateProduct: (siteId, id, d) => API.put(`${base}/cms/sites/${siteId}/commerce/products/${id}`, d),
      cmsDeleteProduct: (siteId, id) => API.del(`${base}/cms/sites/${siteId}/commerce/products/${id}`),
      // cmsListOrders/cmsGetOrder/cmsUpdateOrderStatus อยู่ในกลุ่ม "Orders" ด้านบน — ห้ามประกาศซ้ำ (key ซ้ำตัวหลังทับเงียบ)
      cmsSyncOrderToErp: (siteId, id) => API.post(`${base}/cms/sites/${siteId}/commerce/orders/${id}/sync-erp`, {}),
      cmsConfirmOrderPayment: (siteId, id, paymentId = null) => API.post(`${base}/cms/sites/${siteId}/commerce/orders/${id}/confirm-payment${paymentId ? `?paymentId=${paymentId}` : ''}`, {}),
      // Booking (per site)
      cmsListBookingServices: (siteId) => API.get(`${base}/cms/sites/${siteId}/booking/services`),
      cmsCreateBookingService: (siteId, d) => API.post(`${base}/cms/sites/${siteId}/booking/services`, d),
      cmsUpdateBookingService: (siteId, id, d) => API.put(`${base}/cms/sites/${siteId}/booking/services/${id}`, d),
      cmsDeleteBookingService: (siteId, id) => API.del(`${base}/cms/sites/${siteId}/booking/services/${id}`),
      // cmsListBookings/cmsGetBooking/cmsUpdateBookingStatus อยู่ในกลุ่มด้านบน — ห้ามประกาศซ้ำ
      // Customers (per site)
      cmsListCustomers: (siteId, q='') => API.get(`${base}/cms/sites/${siteId}/customers${q}`),
      cmsGetCustomer: (siteId, id) => API.get(`${base}/cms/sites/${siteId}/customers/${id}`),
      cmsCreateCustomer: (siteId, d) => API.post(`${base}/cms/sites/${siteId}/customers`, d),
      cmsUpdateCustomer: (siteId, id, d) => API.put(`${base}/cms/sites/${siteId}/customers/${id}`, d),
      cmsDeleteCustomer: (siteId, id) => API.del(`${base}/cms/sites/${siteId}/customers/${id}`),
      cmsLinkCustomerToErp: (siteId, id) => API.post(`${base}/cms/sites/${siteId}/customers/${id}/link-erp`, {}),
      // Forms (per site)
      cmsListForms: (siteId) => API.get(`${base}/cms/sites/${siteId}/forms`),
      cmsGetForm: (siteId, id) => API.get(`${base}/cms/sites/${siteId}/forms/${id}`),
      cmsCreateForm: (siteId, d) => API.post(`${base}/cms/sites/${siteId}/forms`, d),
      cmsDeleteForm: (siteId, id) => API.del(`${base}/cms/sites/${siteId}/forms/${id}`),
      cmsListFormSubmissions: (siteId, formId, q='') => API.get(`${base}/cms/sites/${siteId}/forms/${formId}/submissions${q}`),
      cmsUpdateSubmissionStatus: (siteId, formId, subId, d) => API.put(`${base}/cms/sites/${siteId}/forms/${formId}/submissions/${subId}/status`, d),
      // Locales (per site)
      cmsListLocales: (siteId) => API.get(`${base}/cms/sites/${siteId}/locales`),
      cmsAddLocale: (siteId, d) => API.post(`${base}/cms/sites/${siteId}/locales`, d),
      cmsDeleteLocale: (siteId, localeId) => API.del(`${base}/cms/sites/${siteId}/locales/${localeId}`),
      // Staff access (per site)
      cmsListStaffAccess: (siteId) => API.get(`${base}/cms/sites/${siteId}/staff-access`),
      cmsAddStaffAccess: (siteId, d) => API.post(`${base}/cms/sites/${siteId}/staff-access`, d),
      cmsDeleteStaffAccess: (siteId, accessId) => API.del(`${base}/cms/sites/${siteId}/staff-access/${accessId}`),
      // SEO redirects (per site)
      cmsListRedirects: (siteId) => API.get(`${base}/cms/sites/${siteId}/content/seo-redirects`),
      cmsAddRedirect: (siteId, d) => API.post(`${base}/cms/sites/${siteId}/content/seo-redirects`, d),
      cmsDeleteRedirect: (siteId, id) => API.del(`${base}/cms/sites/${siteId}/content/seo-redirects/${id}`),
      // Product variants
      cmsListVariants: (siteId, productId) => API.get(`${base}/cms/sites/${siteId}/commerce/products/${productId}/variants`),
      cmsCreateVariant: (siteId, productId, d) => API.post(`${base}/cms/sites/${siteId}/commerce/products/${productId}/variants`, d),
      cmsUpdateVariant: (siteId, productId, variantId, d) => API.put(`${base}/cms/sites/${siteId}/commerce/products/${productId}/variants/${variantId}`, d),
      cmsDeleteVariant: (siteId, productId, variantId) => API.del(`${base}/cms/sites/${siteId}/commerce/products/${productId}/variants/${variantId}`),
      // Product options
      cmsListOptions: (siteId, productId) => API.get(`${base}/cms/sites/${siteId}/commerce/products/${productId}/options`),
      cmsCreateOption: (siteId, productId, d) => API.post(`${base}/cms/sites/${siteId}/commerce/products/${productId}/options`, d),
      cmsDeleteOption: (siteId, productId, optionId) => API.del(`${base}/cms/sites/${siteId}/commerce/products/${productId}/options/${optionId}`),
      // Product reviews
      cmsListReviews: (siteId, productId, q='') => API.get(`${base}/cms/sites/${siteId}/commerce/products/${productId}/reviews${q}`),
      cmsGetReviewSummary: (siteId, productId) => API.get(`${base}/cms/sites/${siteId}/commerce/products/${productId}/reviews/summary`),
      cmsModerateReview: (siteId, reviewId, d) => API.put(`${base}/cms/sites/${siteId}/commerce/reviews/${reviewId}/moderate`, d),
      // Block templates
      cmsListBlockTemplates: () => API.get(`/api/cms/block-templates`),
      // Quotas
      cmsGetQuotas: (siteId) => API.get(`${base}/cms/sites/${siteId}/quotas`),
      // Page translations
      cmsUpsertPageTranslation: (siteId, pageId, d) => API.put(`${base}/cms/sites/${siteId}/content/pages/${pageId}/translations`, d),
      cmsDeletePageTranslation: (siteId, pageId, lang) => API.del(`${base}/cms/sites/${siteId}/content/pages/${pageId}/translations/${lang}`),
      // Server-side PDF generation. Endpoints return raw PDF bytes (not JSON), so we
      // expose URLs for the page to fetch as Blobs and trigger a download.
      generateDocPdfUrl: () => `${base}/document-templates/generate-pdf`,
      generateWhtPdfUrl: (certId) => `${base}/document-templates/withholding-tax/${certId}/pdf`,
      generateReceiptPdfUrl: (paymentId) => `${base}/document-templates/receipt/${paymentId}/pdf`,
      // Fixed Assets
      getAssets: (q = '') => API.get(`${base}/fixedasset${q}`),
      getAsset: (id) => API.get(`${base}/fixedasset/${id}`),
      // สินทรัพย์ที่ระบบสร้างอัตโนมัติจาก PV/PI และยังไม่ผ่านการ "ยืนยัน"
      // (NeedsReview=true) — UI ใช้เป็น badge เตือนผู้ใช้
      getAssetsNeedsReview: () => API.get(`${base}/fixedasset/needs-review`),
      getAssetsByDocument: (docId) => API.get(`${base}/fixedasset/by-document/${docId}`),
      // บรรทัดของเอกสาร vs ทะเบียน (เซิร์ฟเวอร์ตัดสินว่าบรรทัดไหนเข้าข่าย/มีทะเบียนแล้ว)
      getDocumentAssetLines: (docId) => API.get(`${base}/fixedasset/document-lines/${docId}`),
      // ขึ้นทะเบียนจากเอกสารที่โพสต์แล้ว — ทั้งใบ หรือเฉพาะบรรทัด
      registerAssetsFromDocument: (docId, lineId) => API.post(`${base}/fixedasset/from-document/${docId}${lineId ? `?lineId=${encodeURIComponent(lineId)}` : ''}`, {}),
      createAsset: (d) => API.post(`${base}/fixedasset`, d),
      updateAsset: (id, d) => API.put(`${base}/fixedasset/${id}`, d),
      deleteAsset: (id) => API.del(`${base}/fixedasset/${id}`),
      disposeAsset: (id, d) => API.post(`${base}/fixedasset/${id}/dispose`, d),
      writeOffAsset: (id, d) => API.post(`${base}/fixedasset/${id}/writeoff`, d),
      adjustAssetLife: (id, d) => API.put(`${base}/fixedasset/${id}/adjust-life`, d),
      getDepreciations: (id) => API.get(`${base}/fixedasset/${id}/depreciations`),
      runDepreciation: (d) => API.post(`${base}/fixedasset/depreciate`, d),
      getAssetCategories: () => API.get(`${base}/fixedasset/categories`),
      getAssetRegisterReport: () => API.get(`${base}/fixedasset/report/register`),
      getDepreciationSchedule: (id) => API.get(`${base}/fixedasset/${id}/report/depreciation-schedule`),
      importAssets: (d) => API.post(`${base}/fixedasset/import`, d),
      // Budget
      getBudgets: (q = '') => API.get(`${base}/budget${q}`),
      getBudget: (id) => API.get(`${base}/budget/${id}`),
      createBudget: (d) => API.post(`${base}/budget`, d),
      updateBudget: (id, d) => API.put(`${base}/budget/${id}`, d),
      deleteBudget: (id) => API.del(`${base}/budget/${id}`),
      getBudgetVsActual: (id) => API.get(`${base}/budget/${id}/vs-actual`),
      // Payroll
      getEmployees: (q = '') => API.get(`${base}/payroll/employees${q}`),
      createEmployee: (d) => API.post(`${base}/payroll/employees`, d),
      getPayrollRuns: (q = '') => API.get(`${base}/payroll/runs${q}`),
      createPayrollRun: (d) => API.post(`${base}/payroll/runs`, d),
      calculatePayroll: (id) => API.post(`${base}/payroll/runs/${id}/calculate`),
      approvePayroll: (id) => API.post(`${base}/payroll/runs/${id}/approve`),
      payPayroll: (id) => API.post(`${base}/payroll/runs/${id}/pay`),
      settleSso: (id, body) => API.post(`${base}/payroll/runs/${id}/settle-sso`, body),
      // กลับรายการนำส่ง สปส. (นำส่งผิดยอด/ผิดวัน) — ปลดล็อกให้แก้แล้วนำส่งใหม่
      reverseSso: (id, reason) => API.post(`${base}/payroll/runs/${id}/reverse-sso`, { reason }),
      // ── นำส่งภาษี/ประกันสังคมรวม (สปส.1-10 + ภงด.1/3/53 + ภพ.30) ──
      getRemittances: (monthsBack = 12) => API.get(`${base}/remittances?monthsBack=${monthsBack}`),
      // ปฏิทินนำส่ง "แบบ × เดือน" — ใช้บน dashboard (รวมงวดยอด 0 ที่ยังต้องยื่นแบบเปล่า)
      getFilingCalendar: (months = 12) => API.get(`${base}/remittances/calendar?months=${months}`),
      remitPreview: (type, year, month, payDate) => API.get(`${base}/remittances/preview?type=${encodeURIComponent(type)}&year=${year}&month=${month}${payDate ? '&payDate=' + encodeURIComponent(payDate) : ''}`),
      remit: (d) => API.post(`${base}/remittances`, d),
      uploadRemittanceReceipt: (id, formData) => API.upload(`${base}/remittances/${id}/receipt`, formData),
      // Expense Claims
      getExpenseClaims: (q = '') => API.get(`${base}/expense-claims${q}`),
      getExpenseClaim: (id) => API.get(`${base}/expense-claims/${id}`),
      createExpenseClaim: (d) => API.post(`${base}/expense-claims`, d),
      updateExpenseClaim: (id, d) => API.put(`${base}/expense-claims/${id}`, d),
      submitExpenseClaim: (id) => API.post(`${base}/expense-claims/${id}/submit`),
      approveExpenseClaim: (id, d) => API.post(`${base}/expense-claims/${id}/approve`, d),
      rejectExpenseClaim: (id, d) => API.post(`${base}/expense-claims/${id}/reject`, d),
      payExpenseClaim: (id) => API.post(`${base}/expense-claims/${id}/pay`),
      voidExpenseClaim: (id) => API.post(`${base}/expense-claims/${id}/void`),
      // Projects
      getProjects: (q = '') => API.get(`${base}/projects${q}`),
      getActiveProjects: () => API.get(`${base}/projects/active`),
      getProject: (id) => API.get(`${base}/projects/${id}`),
      getProjectMethods: () => API.get(`${base}/projects/methods`),
      createProject: (d) => API.post(`${base}/projects`, d),
      updateProject: (id, d) => API.put(`${base}/projects/${id}`, d),
      completeProject: (id) => API.post(`${base}/projects/${id}/complete`),
      deleteProject: (id) => API.del(`${base}/projects/${id}`),
      getProjectTasks: (id) => API.get(`${base}/projects/${id}/tasks`),
      createProjectTask: (id, d) => API.post(`${base}/projects/${id}/tasks`, d),
      updateProjectTask: (id, d) => API.put(`${base}/projects/tasks/${id}`, d),
      deleteProjectTask: (id) => API.del(`${base}/projects/tasks/${id}`),
      getProjectCosts: (id, q = '') => API.get(`${base}/projects/${id}/costs${q}`),
      addProjectCost: (id, d) => API.post(`${base}/projects/${id}/costs`, d),
      deleteProjectCost: (id) => API.del(`${base}/projects/costs/${id}`),
      getProjectProfit: (id) => API.get(`${base}/projects/${id}/profitability`),
      getProjectGlSummary: (id, q = '') => API.get(`${base}/projects/${id}/gl-summary${q}`),
      getProjectsSummary: () => API.get(`${base}/projects/summary`),
      // Loans
      getLoans: (q = '') => API.get(`${base}/loans${q}`),
      getLoan: (id) => API.get(`${base}/loans/${id}`),
      createLoan: (d) => API.post(`${base}/loans`, d),
      generateSchedule: (id) => API.post(`${base}/loans/${id}/generate-schedule`),
      getLoanSchedule: (id) => API.get(`${base}/loans/${id}/schedule`),
      makeLoanPayment: (id, d) => API.post(`${base}/loans/${id}/payments`, d),
      getLoanPayments: (id) => API.get(`${base}/loans/${id}/payments`),
      getLoanSummary: () => API.get(`${base}/loans/summary`),
      // ── รับชำระเงินผ่าน gateway (PAYMENT_GATEWAY_DESIGN.md) ──
      // ไม่มีชื่อผู้ให้บริการในไฟล์นี้ — ทุกอย่างผ่านชั้นกลาง
      getPaymentProviders: () => API.get(`${base}/payment-settings/providers`),
      getPaymentConfigs: () => API.get(`${base}/payment-settings`),
      savePaymentConfig: (d) => API.put(`${base}/payment-settings`, d),
      testPaymentConfig: (code) => API.post(`${base}/payment-settings/${code}/test`, {}),
      setPaymentMode: (code, mode) => API.post(`${base}/payment-settings/${code}/mode`, { mode }),
      createPaymentIntent: (d) => API.post(`${base}/pay/intents`, d),
      getPaymentIntentStatus: (id, live = true) => API.get(`${base}/pay/intents/${id}/status?live=${live}`),
      listPaymentIntents: (q = '') => API.get(`${base}/pay/intents${q}`),
      // ── กระทบยอด/บันทึกเงินโอนเข้า (settlement) ──
      // ยอดที่โอนเข้าจริงเป็น "ตัวตั้ง" — เซิร์ฟเวอร์เป็นคนตรวจและบล็อกเมื่อไม่ตรง
      getPendingSettlements: (code) =>
        API.get(`${base}/pay/settlements/pending?providerCode=${encodeURIComponent(code)}`),
      previewSettlement: (d) => API.post(`${base}/pay/settlements/preview`, d),
      recordSettlement: (d) => API.post(`${base}/pay/settlements`, d),
      getPaymentIntentEvents: (id) => API.get(`${base}/pay/intents/${id}/events`),
      // ยืนยันด้วยมือ (เห็นเงินเข้าบัญชีจริงแต่ระบบยังไม่รู้) — เดินผ่าน endpoint
      // เดียวกับ webhook ⇒ ต้นทางถูกดำเนินการต่อครบเหมือนกัน · บังคับเหตุผล
      confirmPaymentIntentManually: (id, reason) =>
        API.post(`${base}/pay/intents/${id}/confirm-manually`, { reason }),
      refundPaymentIntent: (id, amount, reason) =>
        API.post(`${base}/pay/intents/${id}/refund`, { amount, reason }),
      // สูตรวัตถุดิบต่อสินค้า (recipe) — มุมมองบนตาราง BOM เดียวกับใบสั่งผลิต
      getProductRecipe: (productId) => API.get(`${base}/mfg/products/${productId}/recipe`),
      saveProductRecipe: (productId, d) => API.put(`${base}/mfg/products/${productId}/recipe`, d),
      // Warehouse
      getWarehouses: () => API.get(`${base}/warehouses`),
      createWarehouse: (d) => API.post(`${base}/warehouses`, d),
      updateWarehouse: (id, d) => API.put(`${base}/warehouses/${id}`, d),
      getWarehouseStock: (id) => API.get(`${base}/warehouses/${id}/stock`),
      getTransfers: (q = '') => API.get(`${base}/warehouses/transfers${q}`),
      createTransfer: (d) => API.post(`${base}/warehouses/transfers`, d),
      shipTransfer: (id) => API.post(`${base}/warehouses/transfers/${id}/ship`),
      receiveTransfer: (id, d) => API.post(`${base}/warehouses/transfers/${id}/receive`, d),
      // Payroll - extended
      getEmployee: (id) => API.get(`${base}/payroll/employees/${id}`),
      updateEmployee: (id, d) => API.put(`${base}/payroll/employees/${id}`, d),
      terminateEmployee: (id, date) => API.post(`${base}/payroll/employees/${id}/terminate?endDate=${date}`),
      getPayrollRun: (id) => API.get(`${base}/payroll/runs/${id}`),
      getPayrollDetail: (runId, empId) => API.get(`${base}/payroll/runs/${runId}/employees/${empId}`),
      getPayslip: (runId, empId) => `${base}/payroll/runs/${runId}/employees/${empId}/payslip`,
      getPayslipDownload: (runId, empId) => `${base}/payroll/runs/${runId}/employees/${empId}/payslip?download=true`,
      // ส่งสลิปทาง LINE + ผูก LINE รายพนักงาน
      sendPayslipLine: (runId, empId) => API.post(`${base}/payroll/runs/${runId}/employees/${empId}/payslip/send-line`, {}),
      sendPayslipLineAll: (runId) => API.post(`${base}/payroll/runs/${runId}/payslip/send-line-all`, {}),
      issueEmployeeLineBindCode: (empId) => API.post(`${base}/payroll/employees/${empId}/line-bind-code`, {}),
      getEmployeeLineStatus: (empId) => API.get(`${base}/payroll/employees/${empId}/line-status`),
      setPayrollPaymentAccount: (runId, empId, accountCode) => API.put(`${base}/payroll/runs/${runId}/employees/${empId}/payment-account`, { accountCode }),
      updatePayrollDetail: (runId, empId, body) => API.put(`${base}/payroll/runs/${runId}/employees/${empId}/detail`, body),
      // กลับรายการจ่าย (Paid → Approved) เพื่อแก้ยอดย้อนหลังแล้วจ่ายใหม่
      reopenPayrollRun: (runId, reason) => API.post(`${base}/payroll/runs/${runId}/reopen`, { reason }),
      getPayrollItems: () => API.get(`${base}/payroll/items`),
      createPayrollItem: (d) => API.post(`${base}/payroll/items`, d),
      getLeaves: (q = '') => API.get(`${base}/payroll/leaves${q}`),
      getLeave: (id) => API.get(`${base}/payroll/leaves/${id}`),
      createLeave: (d) => API.post(`${base}/payroll/leaves`, d),
      approveLeave: (id) => API.post(`${base}/payroll/leaves/${id}/approve`),
      rejectLeave: (id, d) => API.post(`${base}/payroll/leaves/${id}/reject`, d),
      cancelLeave: (id) => API.post(`${base}/payroll/leaves/${id}/cancel`),
      getLeaveBalance: (employeeId, year) => API.get(`${base}/payroll/leaves/balance?employeeId=${employeeId}${year ? '&year=' + year : ''}`),
      getPnd1: (y, m) => API.get(`${base}/payroll/pnd1/${y}/${m}`),
      getSso: (y, m) => API.get(`${base}/payroll/sso/${y}/${m}`),
      // Salary Advance
      getSalaryAdvances: (q = '') => API.get(`${base}/salary-advances${q}`),
      getSalaryAdvance: (id) => API.get(`${base}/salary-advances/${id}`),
      createSalaryAdvance: (d) => API.post(`${base}/salary-advances`, d),
      updateSalaryAdvance: (id, d) => API.put(`${base}/salary-advances/${id}`, d),
      submitSalaryAdvance: (id) => API.post(`${base}/salary-advances/${id}/submit`),
      approveSalaryAdvance: (id, d) => API.post(`${base}/salary-advances/${id}/approve`, d || {}),
      rejectSalaryAdvance: (id, d) => API.post(`${base}/salary-advances/${id}/reject`, d),
      disburseSalaryAdvance: (id, d) => API.post(`${base}/salary-advances/${id}/disburse`, d || {}),
      voidSalaryAdvance: (id) => API.post(`${base}/salary-advances/${id}/void`),
      // Organisation structure
      getDepartments: (includeInactive = false) => API.get(`${base}/organization/departments?includeInactive=${includeInactive}`),
      getDepartment: (id) => API.get(`${base}/organization/departments/${id}`),
      createDepartment: (d) => API.post(`${base}/organization/departments`, d),
      updateDepartment: (id, d) => API.put(`${base}/organization/departments/${id}`, d),
      deleteDepartment: (id) => API.del(`${base}/organization/departments/${id}`),
      getPositions: (includeInactive = false) => API.get(`${base}/organization/positions?includeInactive=${includeInactive}`),
      getPosition: (id) => API.get(`${base}/organization/positions/${id}`),
      createPosition: (d) => API.post(`${base}/organization/positions`, d),
      updatePosition: (id, d) => API.put(`${base}/organization/positions/${id}`, d),
      deletePosition: (id) => API.del(`${base}/organization/positions/${id}`),
      getOrgChart: () => API.get(`${base}/organization/chart`),
      getDirectManager: (employeeId) => API.get(`${base}/organization/employees/${employeeId}/direct-manager`),
      getDirectManagerByUser: (userId) => API.get(`${base}/organization/users/${userId}/direct-manager`),
      // Notification engine config
      getNotificationCatalog: () => API.get(`${base}/notifications/config/catalog`),
      getNotificationSettings: () => API.get(`${base}/notifications/config/settings`),
      bulkUpsertNotificationSettings: (d) => API.put(`${base}/notifications/config/settings`, d),
      getMyNotificationPreferences: () => API.get(`${base}/notifications/config/preferences/me`),
      upsertMyNotificationPreferences: (d) => API.put(`${base}/notifications/config/preferences/me`, d),
      getMyLineBinding: () => API.get(`${base}/notifications/config/line-binding/me`),
      setMyLineBinding: (d) => API.put(`${base}/notifications/config/line-binding/me`, d),
      clearMyLineBinding: () => API.del(`${base}/notifications/config/line-binding/me`),
      // Tax Calendar
      getTaxEvents: (q = '') => API.get(`${base}/tax-calendar${q}`),
      getTaxEvent: (id) => API.get(`${base}/tax-calendar/${id}`),
      initTaxCalendar: (year) => API.post(`${base}/tax-calendar/initialize/${year}`),
      updateTaxEvent: (id, d) => API.put(`${base}/tax-calendar/${id}`, d),
      getUpcomingTax: (days = 30) => API.get(`${base}/tax-calendar/upcoming?daysAhead=${days}`),
      getOverdueTax: () => API.get(`${base}/tax-calendar/overdue`),
      // Recurring
      getRecurring: (q = '') => API.get(`${base}/recurring${q}`),
      getRecurringItem: (id) => API.get(`${base}/recurring/${id}`),
      createRecurring: (d) => API.post(`${base}/recurring`, d),
      updateRecurring: (id, d) => API.put(`${base}/recurring/${id}`, d),
      deleteRecurring: (id) => API.del(`${base}/recurring/${id}`),
      pauseRecurring: (id) => API.post(`${base}/recurring/${id}/pause`),
      resumeRecurring: (id) => API.post(`${base}/recurring/${id}/resume`),
      runRecurringNow: (id) => API.post(`${base}/recurring/${id}/run-now`),
      // Import/Export
      importData: (d) => API.post(`${base}/import-export/import`, d),
      validateImport: (d) => API.post(`${base}/import-export/validate`, d),
      getImportTemplate: (entity) => `${base}/import-export/templates/${entity}/download`,
      // Field metadata for the smart-import column-mapping dropdown
      getImportTemplateMeta: (entity) => API.get(`${base}/import-export/templates/${entity}`),
      exportData: (d) => API.post(`${base}/import-export/export`, d),
      getExportableEntities: () => API.get(`${base}/import-export/exportable-entities`),
      getImportableEntities: () => API.get(`${base}/import-export/importable-entities`),
      smartImportUpload: (d) => API.post(`${base}/import-export/smart-import/upload`, d),
      smartImportSession: (sid) => API.get(`${base}/import-export/smart-import/sessions/${sid}`),
      smartImportMapping: (d) => API.post(`${base}/import-export/smart-import/manual-mapping`, d),
      smartImportConfirm: (d) => API.post(`${base}/import-export/smart-import/confirm`, d),
      // Single DeepSeek call → type normalizations + fuzzy duplicates + per-row
      // quality flags + semantic validation + batch patterns. Called between
      // mapping save and confirm.
      smartImportAiReview: (sid) => API.post(`${base}/import-export/smart-import/sessions/${sid}/ai-review`),
      // Currency
      getCurrencies: () => API.get(`${base}/currency`),
      addCurrency: (d) => API.post(`${base}/currency`, d),
      updateCurrency: (id, d) => API.put(`${base}/currency/${id}`, d),
      getExchangeRates: (q = '') => API.get(`${base}/currency/rates${q}`),
      addExchangeRate: (d) => API.post(`${base}/currency/rates`, d),
      getLatestRate: (from, to) => API.get(`${base}/currency/rates/latest?from=${from}&to=${to}`),
      // Portal
      createPortalAccess: (d) => API.post(`${base}/portal/access`, d),
      getPortalAccess: () => API.get(`${base}/portal/access`),
      updatePortalAccess: (id, d) => API.put(`${base}/portal/access/${id}`, d),
      deactivatePortalAccess: (id) => API.post(`${base}/portal/access/${id}/deactivate`),
      // Dimensions & Branches
      getDimensions: () => API.get(`${base}/dimensions`),
      getDimension: (id) => API.get(`${base}/dimensions/${id}`),
      createDimension: (d) => API.post(`${base}/dimensions`, d),
      updateDimension: (id, d) => API.put(`${base}/dimensions/${id}`, d),
      deleteDimension: (id) => API.del(`${base}/dimensions/${id}`),
      getDimensionSummary: () => API.get(`${base}/dimensions/summary`),
      getDimensionPnl: (id) => API.get(`${base}/dimensions/${id}/pnl`),
      // includeInactive = true เฉพาะหน้าตั้งค่า (ต้องเห็นสาขาที่ปิดไว้เพื่อเปิดกลับ);
      // ตัวเลือกสาขาในหน้าอื่นเรียกแบบไม่ส่ง = ได้เฉพาะสาขาที่ใช้งานอยู่เหมือนเดิม
      getBranches: (includeInactive = false) => API.get(`${base}/dimensions/branches${includeInactive ? '?includeInactive=true' : ''}`),
      createBranch: (d) => API.post(`${base}/dimensions/branches`, d),
      updateBranch: (id, d) => API.put(`${base}/dimensions/branches/${id}`, d),
      deleteBranch: (id) => API.del(`${base}/dimensions/branches/${id}`),
      // Intercompany
      getIntercompanyTxns: (q = '') => API.get(`${base}/intercompany${q}`),
      getIntercompanyTxn: (id) => API.get(`${base}/intercompany/${id}`),
      createIntercompanyTxn: (d) => API.post(`${base}/intercompany`, d),
      confirmIntercompanyTxn: (id) => API.post(`${base}/intercompany/${id}/confirm`),
      voidIntercompanyTxn: (id) => API.post(`${base}/intercompany/${id}/void`),
      getIntercompanyBalances: () => API.get(`${base}/intercompany/balances`),
      // Consolidation — ConsolidationController อยู่ใต้ companies/{companyId}
      // (เดิมชี้ /api/consolidation/... เฉย ๆ → 404 ทั้งหน้า)
      getConsolidationGroups: () => API.get(`${base}/consolidation/groups`),
      getConsolidationGroup: (id) => API.get(`${base}/consolidation/groups/${id}`),
      createConsolidationGroup: (d) => API.post(`${base}/consolidation/groups`, d),
      addConsolidationMember: (gid, d) => API.post(`${base}/consolidation/groups/${gid}/members`, d),
      removeConsolidationMember: (gid, mid) => API.del(`${base}/consolidation/groups/${gid}/members/${mid}`),
      getConsolidatedBS: (gid) => API.get(`${base}/consolidation/groups/${gid}/balance-sheet`),
      getConsolidatedPnl: (gid) => API.get(`${base}/consolidation/groups/${gid}/pnl`),
      getEliminations: (gid) => API.get(`${base}/consolidation/groups/${gid}/eliminations`),
      // Commission
      getCommissionPlans: () => API.get(`${base}/commissions/plans`),
      getCommissionPlan: (id) => API.get(`${base}/commissions/plans/${id}`),
      getCommissionOptions: () => API.get(`${base}/commissions/options`),
      createCommissionPlan: (d) => API.post(`${base}/commissions/plans`, d),
      updateCommissionPlan: (id, d) => API.put(`${base}/commissions/plans/${id}`, d),
      assignCommissionPlan: (id, d) => API.post(`${base}/commissions/plans/${id}/assign`, d),
      calculateCommissions: (y, m) => API.post(`${base}/commissions/calculate/${y}/${m}`),
      getCommissionCalcs: (y, m) => API.get(`${base}/commissions/calculations/${y}/${m}`),
      approveCommissions: (y, m) => API.post(`${base}/commissions/approve/${y}/${m}`),
      // Revenue Recognition
      getRevenueContracts: (q = '') => API.get(`${base}/revenue-recognition/contracts${q}`),
      getRevenueContract: (id) => API.get(`${base}/revenue-recognition/contracts/${id}`),
      createRevenueContract: (d) => API.post(`${base}/revenue-recognition/contracts`, d),
      addObligation: (id, d) => API.post(`${base}/revenue-recognition/contracts/${id}/obligations`, d),
      updateObligationProgress: (id, d) => API.put(`${base}/revenue-recognition/obligations/${id}/progress`, d),
      generateRevenueSchedule: (id) => API.post(`${base}/revenue-recognition/contracts/${id}/generate-schedule`),
      recognizeRevenue: (id) => API.post(`${base}/revenue-recognition/schedules/${id}/recognize`),
      // asOfDate บังคับฝั่ง backend — ไม่ส่ง = default(DateTime) ปี 0001 → รายงานว่างเสมอ
      getDeferredRevenue: (asOfDate) => API.get(`${base}/revenue-recognition/deferred-revenue?asOfDate=${asOfDate || new Date().toISOString().slice(0, 10)}`),
      // Time & Billing
      getTimeEntries: (q = '') => API.get(`${base}/time-billing/entries${q}`),
      getTimeEntry: (id) => API.get(`${base}/time-billing/entries/${id}`),
      createTimeEntry: (d) => API.post(`${base}/time-billing/entries`, d),
      updateTimeEntry: (id, d) => API.put(`${base}/time-billing/entries/${id}`, d),
      submitTimeEntry: (id) => API.post(`${base}/time-billing/entries/${id}/submit`),
      approveTimeEntry: (id) => API.post(`${base}/time-billing/entries/${id}/approve`),
      getBillingRates: () => API.get(`${base}/time-billing/rates`),
      createBillingRate: (d) => API.post(`${base}/time-billing/rates`, d),
      generateTimeInvoice: (d) => API.post(`${base}/time-billing/generate-invoice`, d),
      getTimeSummary: (q = '') => API.get(`${base}/time-billing/summary${q}`),
      getUtilization: (q = '') => API.get(`${base}/time-billing/utilization${q}`),
      // AI
      aiCategorize: (entityType, entityId) => API.post(`${base}/ai/categorize/${entityType}/${entityId}`),
      aiBatchCategorize: (d) => API.post(`${base}/ai/categorize/batch`, d),
      aiAcceptCategory: (id) => API.post(`${base}/ai/categorize/${id}/accept`),
      getAiRules: () => API.get(`${base}/ai/rules`),
      createAiRule: (d) => API.post(`${base}/ai/rules`, d),
      aiLearnRules: () => API.post(`${base}/ai/rules/learn`),
      aiDetectAnomalies: (fromDate, toDate) => API.post(`${base}/ai/anomalies/detect?${fromDate ? 'fromDate='+fromDate+'&' : ''}${toDate ? 'toDate='+toDate : ''}`),
      getAnomalies: (q = '') => API.get(`${base}/ai/anomalies${q}`),
      resolveAnomaly: (id, notes = '') => API.post(`${base}/ai/anomalies/${id}/resolve?notes=${encodeURIComponent(notes)}`),
      createForecast: (d) => API.post(`${base}/ai/forecast`, d),
      getForecasts: () => API.get(`${base}/ai/forecasts`),
      // OCR
      // ⚠️ ต้องผ่าน API.request เท่านั้น — เดิมเมธอดนี้เป็น **จุดเดียวในไฟล์** ที่เขียน
      // fetch เองแล้วเรียก `r.json()` ดิบ ๆ โดยไม่ดู status/content-type ⇒ ทุก response
      // ที่ body ว่าง (401 จาก JWT · 413 ไฟล์ใหญ่เกิน · 502/504 จาก proxy) กลายเป็น
      // ข้อความ **"Failed to execute 'json' on 'Response': Unexpected end of JSON input"**
      // ซึ่งบอกผู้ใช้ไม่ได้เลยว่าเกิดอะไรและต้องทำอะไรต่อ (ผู้ใช้รายงาน 2026-09-18)
      // API.request จัดการครบอยู่แล้ว: 401 → พากลับหน้า login · non-JSON → บอก
      // status + เนื้อความจริง · !ok → ใช้ message ของเซิร์ฟเวอร์ · network → ข้อความไทย
      ocrUploadAndScan: (formData, preferredEngine) => {
        const qs = preferredEngine ? `?preferredEngine=${encodeURIComponent(preferredEngine)}` : '';
        return API.request('POST', `${base}/ocr/upload${qs}`, formData, true);
      },
      getOcrEngines: () => API.get(`${base}/ocr/engines`),

      // AI suggestion endpoints — called when the UI wants AI's
      // opinion on a decision (every endpoint returns a feedbackId
      // that should be posted back via aiFeedbackRecord after the
      // user makes their final choice, so the answer becomes a
      // training signal).
      aiSuggestPaymentVoucherAccount: (sourceInvoiceId, lineDescription, amount, currency, currentAccountCode) =>
        API.post(`${base}/ai/payment-voucher/suggest-account`,
          { sourceInvoiceId, lineDescription, amount, currency, currentAccountCode }),
      aiInferWhtCategory: (documentId, vendorName, vendorTaxId, vendorType, lineDescription, amount, currentCode) =>
        API.post(`${base}/ai/wht/infer-category`,
          { documentId, vendorName, vendorTaxId, vendorType, lineDescription, amount, currentCode }),
      aiClassifyCreditNoteReason: (creditNoteId, originalInvoiceId, currentReason) =>
        API.post(`${base}/ai/credit-note/classify-reason`,
          { creditNoteId, originalInvoiceId, currentReason }),
      aiSuggestBankMatch: (bankTransactionId, currentMatchedDocId) =>
        API.post(`${base}/ai/bank/suggest-match`,
          { bankTransactionId, currentMatchedDocId }),
      // `source` = คำยืนยันนี้ตั้งใจแค่ไหน: 'Explicit' เมื่อเรียกจาก event ที่ผู้ใช้
      // เปลี่ยนค่าเอง · 'BulkApprove' เมื่อคลิกเดียวยืนยันหลายรายการ · ไม่ส่ง = Implicit
      aiFeedbackRecord: (feedbackId, chosenAnswer, acceptedAi, source) =>
        API.post(`${base}/ai-feedback/record`,
          { feedbackId, chosenAnswer, acceptedAi, source }),
      aiExplainAnomaly: (anomalyId, force) =>
        API.post(`${base}/ai/anomalies/${anomalyId}/explain${force ? '?force=true' : ''}`),
      aiBatchSuggestPvAccounts: (sourceInvoiceId) =>
        API.post(`${base}/ai/payment-voucher/suggest-all-accounts`, { sourceInvoiceId }),
      // ⚠️ รับ **id ของไฟล์แนบ** ไม่ใช่ id ของสแกน — เคยมีหน้าเว็บส่ง scanId เข้ามา
      // แล้วได้ "File attachment not found." ทุกครั้ง · ถ้าต้องการ "อ่านไฟล์ใหม่"
      // จากรายการสแกนที่มีอยู่แล้ว ให้ใช้ ocrRetryScan (รับ scanId + ไม่ใช้โควตา)
      ocrScan: (fileId) => API.post(`${base}/ocr/scan/${fileId}`),
      ocrRetryScan: (scanId) => API.post(`${base}/ocr/${scanId}/retry`),
      getOcrResult: (id) => API.get(`${base}/ocr/${id}`),
      getOcrResults: () => API.get(`${base}/ocr`),
      // allowDuplicate = ผู้ใช้ยืนยันแล้วว่าเป็นคนละใบจริง แม้เลขที่จะซ้ำกับ
      // เอกสารที่มีอยู่ — เซิร์ฟเวอร์รับพารามิเตอร์นี้มาตั้งแต่ต้นแต่ไม่เคยมีใคร
      // ส่ง ⇒ ข้อความเตือนบอกให้ "กดยืนยันสร้างซ้ำ" โดยไม่มีปุ่มนั้นอยู่จริง
      // (ผลตรวจ T1-14 — ปฏิเสธแล้วต้องมีทางไปต่อ)
      ocrCreateDocument: (id, targetType, approve = false, allowDuplicate = false) => {
        const q = new URLSearchParams();
        if (targetType) q.set('targetType', targetType);
        if (approve) q.set('approve', 'true');
        if (allowDuplicate) q.set('allowDuplicate', 'true');
        const qs = q.toString();
        return API.post(`${base}/ocr/${id}/create-document${qs ? '?' + qs : ''}`);
      },
      // ผูกไฟล์ scan เข้ากับเอกสารที่สร้างผ่าน UI handoff (documents.html save)
      ocrLinkScanToDocument: (scanId, documentId) => API.post(`${base}/ocr/${scanId}/link-document/${documentId}`, {}),
      ocrStockPreview: (id) => API.get(`${base}/ocr/${id}/stock-preview`),
      ocrImportStock: (id, data) => API.post(`${base}/ocr/${id}/import-stock`, data),
      ocrRejectMatch: (id, data) => API.post(`${base}/ocr/${id}/reject-match`, data),
      ocrCorrect: (id, data) => API.post(`${base}/ocr/${id}/correct`, data),
      ocrMatchContact: (scanId, contactId) => API.post(`${base}/ocr/${scanId}/match-contact/${contactId}`),
      ocrDelete: (scanId, cascade = false, reason = null) => API.del(`${base}/ocr/${scanId}?cascade=${cascade ? 'true' : 'false'}${reason ? '&reason=' + encodeURIComponent(reason) : ''}`),
      // PO linkage — list, link, unlink
      // พรีวิวบรรทัดที่เซิร์ฟเวอร์สร้าง (ตัวเดียวกับปุ่มสร้างเอกสาร) — ปุ่ม “แก้ในฟอร์มก่อน” ใช้ตัวนี้
      ocrLinePreview: (scanId, targetType) => API.get(`${base}/ocr/${scanId}/line-preview${targetType ? `?targetType=${encodeURIComponent(targetType)}` : ''}`),
      ocrOpenPos: (scanId) => API.get(`${base}/ocr/${scanId}/open-pos`),
      ocrLinkPo: (scanId, data) => API.post(`${base}/ocr/${scanId}/link-po`, data),
      ocrUnlinkPo: (scanId) => API.del(`${base}/ocr/${scanId}/link-po`),
      ocrPredecessorCandidates: (scanId) => API.get(`${base}/ocr/${scanId}/predecessor-candidates`),
      ocrLinkPredecessor: (scanId, documentId) => API.post(`${base}/ocr/${scanId}/link-predecessor`, { documentId }),
      ocrUnlinkPredecessor: (scanId) => API.del(`${base}/ocr/${scanId}/link-predecessor`),
      // Webhooks
      getWebhooks: () => API.get(`${base}/webhooks`),
      createWebhook: (d) => API.post(`${base}/webhooks`, d),
      updateWebhook: (id, d) => API.put(`${base}/webhooks/${id}`, d),
      deleteWebhook: (id) => API.del(`${base}/webhooks/${id}`),
      testWebhook: (id) => API.post(`${base}/webhooks/${id}/test`),
      getWebhookDeliveries: (id) => API.get(`${base}/webhooks/${id}/deliveries`),
      retryDelivery: (id) => API.post(`${base}/webhooks/deliveries/${id}/retry`),
      getWebhookEventTypes: () => API.get(`${base}/webhooks/event-types`),
      // API Keys
      getApiKeys: () => API.get(`${base}/settings/api-keys`),
      createApiKey: (d) => API.post(`${base}/settings/api-keys`, d),
      revokeApiKey: (id) => API.del(`${base}/settings/api-keys/${id}`),
      // Attachments
      uploadAttachment: (entityType, entityId, formData) => API.upload(`${base}/attachments/${entityType}/${entityId}`, formData),
      getAttachments: (entityType, entityId) => API.get(`${base}/attachments/${entityType}/${entityId}`),
      deleteAttachment: (id) => API.del(`${base}/attachments/${id}`),
      // Authenticated download — bypasses static-file URL leak risk by streaming
      // through the API with JWT validation. Returns a blob URL caller can assign
      // to <a href> or window.open() for download/preview.
      downloadAttachmentUrl: (id) => `${base}/attachments/${id}/download`,
      // Approval
      getApprovalRules: () => API.get(`${base}/approval/rules`),
      createApprovalRule: (d) => API.post(`${base}/approval/rules`, d),
      updateApprovalRule: (id, d) => API.put(`${base}/approval/rules/${id}`, d),
      deleteApprovalRule: (id) => API.del(`${base}/approval/rules/${id}`),
      submitForApproval: (entityType, entityId) => API.post(`${base}/approval/submit?entityType=${entityType}&entityId=${entityId}`),
      getApprovalRequest: (id) => API.get(`${base}/approval/requests/${id}`),
      getPendingApprovals: () => API.get(`${base}/approval/pending`),
      submitAction: (id, d) => API.post(`${base}/approval/requests/${id}/action`, d),
      // Settings
      getSettings: () => API.get(`${base}/settings`),
      updateSettings: (d) => API.put(`${base}/settings`, d),
      uploadLogo: (formData) => API.upload(`${base}/settings/logo`, formData),
      deleteLogo: () => API.del(`${base}/settings/logo`),
      uploadStamp: (formData) => API.upload(`${base}/settings/stamp`, formData),
      deleteStamp: () => API.del(`${base}/settings/stamp`),
      getNumberSeries: () => API.get(`${base}/settings/number-series`),
      // ตั้งตัวย่อเลขที่เอกสารเอง — controller/service มีมาตลอดแต่ไม่เคยมี
      // ฝั่ง client ผูกไว้ ⇒ แท็บ "ลำดับเลขที่" เป็นตารางว่างที่แก้อะไรไม่ได้
      createNumberSeries: (d) => API.post(`${base}/settings/number-series`, d),
      updateNumberSeries: (id, d) => API.put(`${base}/settings/number-series/${id}`, d),
      // Email config
      getEmailConfig: () => API.get(`${base}/email-config`),
      updateEmailConfig: (d) => API.put(`${base}/email-config`, d),
      testEmailConfig: (d) => API.post(`${base}/email-config/test`, d),
      // eTax config + send
      getEtaxConfig: () => API.get(`${base}/etax/config`),
      updateEtaxConfig: (d) => API.put(`${base}/etax/config`, d),
      sendEtaxByEmail: (etaxId, d) => API.post(`${base}/etax/${etaxId}/send-email`, d),
      getEtaxEmailLogs: (etaxId) => API.get(`${base}/etax/${etaxId}/email-logs`),
      // Document email
      sendDocumentEmail: (documentId, d) => API.post(`${base}/document/${documentId}/send-email`, d),
      getDocumentEmailLogs: (documentId) => API.get(`${base}/document/${documentId}/email-logs`),
      // Aging
      getAgingReceivables: (q = '') => API.get(`${base}/aging/receivables${q}`),
      getAgingPayables: (q = '') => API.get(`${base}/aging/payables${q}`),
      getContactReceivables: (contactId, q = '') => API.get(`${base}/aging/contacts/${contactId}/receivables${q}`),
      getContactPayables: (contactId, q = '') => API.get(`${base}/aging/contacts/${contactId}/payables${q}`),
      // AR/AP Analysis
      getArApOverview: () => API.get(`${base}/arap-analysis/overview`),
      getArApContactDetail: (contactId, type = 'ar') => API.get(`${base}/arap-analysis/contacts/${contactId}?type=${type}`),
      getBadDebtAnalysis: () => API.get(`${base}/arap-analysis/bad-debt`),
      // Audit
      getAuditLogs: (q = '') => API.get(`${base}/audit/logs${q}`),
      getAuditSummary: (q = '') => API.get(`${base}/audit/summary${q}`),
      getEntityHistory: (entityType, entityId) => API.get(`${base}/audit/entity/${entityType}/${entityId}`),
      getUserActivity: (userId, limit = 100) => API.get(`${base}/audit/users/${userId}/activity?limit=${limit}`),
      // Notifications
      getNotifications: () => API.get('/api/notification'),
      getNotificationCount: () => API.get('/api/notification/count'),
      markRead: (ids) => API.post('/api/notification/mark-read', { notificationIds: ids }),
      markAllRead: () => API.post('/api/notification/mark-all-read'),
      // Dimension allocations
      addDimensionAllocation: (lineId, d) => API.post(`${base}/dimensions/journal-lines/${lineId}/allocations`, d),
      getDimensionAllocations: (lineId) => API.get(`${base}/dimensions/journal-lines/${lineId}/allocations`),
      // Product stock by warehouse
      getProductStock: (productId) => API.get(`${base}/warehouses/products/${productId}/stock`),
      // Fixed Asset Revaluation
      revalueAsset: (id, d) => API.post(`${base}/fixedasset/${id}/revalue`, d),
      // FPA - Financial Planning & Analysis
      getScenarios: () => API.get(`${base}/fpa/scenarios`),
      getScenario: (id) => API.get(`${base}/fpa/scenarios/${id}`),
      createScenario: (d) => API.post(`${base}/fpa/scenarios`, d),
      updateScenario: (id, d) => API.put(`${base}/fpa/scenarios/${id}`, d),
      deleteScenario: (id) => API.del(`${base}/fpa/scenarios/${id}`),
      addAssumption: (id, d) => API.post(`${base}/fpa/scenarios/${id}/assumptions`, d),
      removeAssumption: (id) => API.del(`${base}/fpa/assumptions/${id}`),
      calculateScenario: (id) => API.post(`${base}/fpa/scenarios/${id}/calculate`),
      compareScenarios: (d) => API.post(`${base}/fpa/scenarios/compare`, d),
      createKpi: (d) => API.post(`${base}/fpa/kpis`, d),
      getKpis: () => API.get(`${base}/fpa/kpis`),
      getKpiHistory: (id) => API.get(`${base}/fpa/kpis/${id}/history`),
      calculateKpiSnapshots: (y, m) => API.post(`${base}/fpa/kpis/snapshots/${y}/${m}`),
      getFpaFinancialRatios: (q = '') => API.get(`${base}/fpa/ratios${q}`),
      getBreakEven: (fy) => API.get(`${base}/fpa/break-even/${fy}`),
      // Custom Report Builder
      getCustomReports: () => API.get(`${base}/reports`),
      getCustomReport: (id) => API.get(`${base}/reports/${id}`),
      createCustomReport: (d) => API.post(`${base}/reports`, d),
      updateCustomReport: (id, d) => API.put(`${base}/reports/${id}`, d),
      deleteCustomReport: (id) => API.del(`${base}/reports/${id}`),
      duplicateCustomReport: (id) => API.post(`${base}/reports/${id}/duplicate`),
      executeCustomReport: (id, d) => API.post(`${base}/reports/${id}/execute`, d),
      getReportDataSources: () => API.get(`${base}/reports/data-sources`),
      getReportColumns: (ds) => API.get(`${base}/reports/data-sources/${ds}/columns`),
      // Compliance
      getComplianceFilings: (q = '') => API.get(`${base}/compliance/filings${q}`),
      createComplianceFiling: (d) => API.post(`${base}/compliance/filings`, d),
      submitComplianceFiling: (id) => API.post(`${base}/compliance/filings/${id}/submit`),
      // e-Tax extended
      etaxSignAndSubmit: (id) => API.post(`${base}/etax/${id}/sign-and-submit`),
      etaxQuickSubmit: (d) => API.post(`${base}/etax/quick-submit`, d),
      etaxGeneratePdf: (id) => API.post(`${base}/etax/${id}/generate-pdf`, {}),
      etaxDownloadPdfUrl: (id) => `${base}/etax/${id}/pdf`,
      etaxDownloadXmlUrl: (id) => `${base}/etax/${id}/xml`,
      // POS - Terminal
      getPosTerminals: () => API.get(`${base}/pos/terminals`),
      createPosTerminal: (d) => API.post(`${base}/pos/terminals`, d),
      updatePosTerminal: (id, d) => API.put(`${base}/pos/terminals/${id}`, d),
      // POS - Session
      getPosSessions: (q = '') => API.get(`${base}/pos/sessions${q}`),
      getPosSession: (id) => API.get(`${base}/pos/sessions/${id}`),
      openPosSession: (d) => API.post(`${base}/pos/sessions/open`, d),
      closePosSession: (id, d) => API.post(`${base}/pos/sessions/${id}/close`, d),
      // POS - Order
      getPosOrders: (q = '') => API.get(`${base}/pos/orders${q}`),
      getPosOrder: (id) => API.get(`${base}/pos/orders/${id}`),
      createPosOrder: (d) => API.post(`${base}/pos/orders`, d),
      updatePosOrder: (id, d) => API.put(`${base}/pos/orders/${id}`, d),
      updatePosOrderStatus: (id, d) => API.post(`${base}/pos/orders/${id}/status`, d),
      voidPosOrder: (id) => API.post(`${base}/pos/orders/${id}/void`),
      refundPosOrder: (id, d) => API.post(`${base}/pos/orders/${id}/refund`, d),
      issuePosTaxInvoice: (id, d) => API.post(`${base}/pos/orders/${id}/issue-tax-invoice`, d),
      syncPosOfflineOrder: (d) => API.post(`${base}/pos/orders/sync-offline`, d),
      completePosOrder: (id) => API.post(`${base}/pos/orders/${id}/complete`),
      // POS - Order Items
      addPosOrderItem: (orderId, d) => API.post(`${base}/pos/orders/${orderId}/items`, d),
      removePosOrderItem: (orderId, itemId) => API.del(`${base}/pos/orders/${orderId}/items/${itemId}`),
      updatePosOrderItemQty: (orderId, itemId, qty) => API.put(`${base}/pos/orders/${orderId}/items/${itemId}/qty`, { quantity: qty }),
      setPosItemDiscount: (orderId, itemId, body) => API.put(`${base}/pos/orders/${orderId}/items/${itemId}/discount`, body),
      setPosTip: (orderId, tipAmount) => API.put(`${base}/pos/orders/${orderId}/tip`, { tipAmount }),
      applyPosCoupon: (orderId, code) => API.post(`${base}/pos/orders/${orderId}/coupon`, { code }),
      splitPosOrder: (orderId, checks) => API.post(`${base}/pos/orders/${orderId}/split`, { checks }),
      updatePosItemStatus: (orderId, itemId, d) => API.post(`${base}/pos/orders/${orderId}/items/${itemId}/status`, d),
      // POS - Payment
      addPosPayment: (d) => API.post(`${base}/pos/payments`, d),
      // POS - Service Package
      getPosPackages: (q = '') => API.get(`${base}/pos/packages${q}`),
      getPosPackage: (id) => API.get(`${base}/pos/packages/${id}`),
      createPosPackage: (d) => API.post(`${base}/pos/packages`, d),
      updatePosPackage: (id, d) => API.put(`${base}/pos/packages/${id}`, d),
      deletePosPackage: (id) => API.del(`${base}/pos/packages/${id}`),
      // POS - Service Component
      addPosComponent: (pkgId, d) => API.post(`${base}/pos/packages/${pkgId}/components`, d),
      updatePosComponent: (pkgId, compId, d) => API.put(`${base}/pos/packages/${pkgId}/components/${compId}`, d),
      removePosComponent: (pkgId, compId) => API.del(`${base}/pos/packages/${pkgId}/components/${compId}`),
      // POS - Service Activity
      updatePosActivity: (actId, d) => API.put(`${base}/pos/activities/${actId}`, d),
      // POS - Modifier Group
      getPosModifierGroups: (q = '') => API.get(`${base}/pos/modifier-groups${q}`),
      createPosModifierGroup: (d) => API.post(`${base}/pos/modifier-groups`, d),
      updatePosModifierGroup: (id, d) => API.put(`${base}/pos/modifier-groups/${id}`, d),
      deletePosModifierGroup: (id) => API.del(`${base}/pos/modifier-groups/${id}`),
      // POS - Modifier Option
      addPosModifierOption: (groupId, d) => API.post(`${base}/pos/modifier-groups/${groupId}/options`, d),
      updatePosModifierOption: (groupId, optId, d) => API.put(`${base}/pos/modifier-groups/${groupId}/options/${optId}`, d),
      removePosModifierOption: (groupId, optId) => API.del(`${base}/pos/modifier-groups/${groupId}/options/${optId}`),
      // POS - Reports
      getPosDailySummary: (q = '') => API.get(`${base}/pos/daily-summary${q}`),
      // ยอดขายแยกรายสาขา (POS เฟส 5) — เซิร์ฟเวอร์รวมให้ หน้าเว็บแสดงอย่างเดียว
      getPosBranchSummary: (q = '') => API.get(`${base}/pos/reports/branches${q}`),
      getPosCommissionSummary: (q) => API.get(`${base}/pos/commission-summary${q}`),
      getPosCommissionDetail: (q) => API.get(`${base}/pos/commission-detail${q}`),
      // Integration
      getIntegrations: () => API.get(`${base}/integrations`),
      createIntegration: (d) => API.post(`${base}/integrations`, d),
      updateIntegration: (id, d) => API.put(`${base}/integrations/${id}`, d),
      deleteIntegration: (id) => API.del(`${base}/integrations/${id}`),
      regenerateIntegrationKey: (id) => API.post(`${base}/integrations/${id}/regenerate-key`),
      getIntegrationMappings: (id) => API.get(`${base}/integrations/${id}/mappings`),
      createIntegrationMapping: (id, d) => API.post(`${base}/integrations/${id}/mappings`, d),
      updateIntegrationMapping: (id, mid, d) => API.put(`${base}/integrations/${id}/mappings/${mid}`, d),
      deleteIntegrationMapping: (id, mid) => API.del(`${base}/integrations/${id}/mappings/${mid}`),
      getIntegrationMappingTemplates: () => API.get(`${base}/integrations/mapping-templates`),
      getIntegrationSyncLogs: (q = '') => API.get(`${base}/integrations/sync-logs${q}`),
      getIntegrationDashboard: () => API.get(`${base}/integrations/dashboard`),
      getIntegrationRevenueByCategory: (q = '') => API.get(`${base}/integrations/reports/revenue-by-category${q}`),
      getIntegrationRevenueBySource: (q = '') => API.get(`${base}/integrations/reports/revenue-by-source${q}`),
      getIntegrationDepositSummary: (q = '') => API.get(`${base}/integrations/reports/deposit-summary${q}`),
      getIntegrationDailyRevenue: (q = '') => API.get(`${base}/integrations/reports/daily-revenue${q}`),

      // E-Commerce
      getECommerceConnections: () => API.get(`${base}/ecommerce/connections`),
      connectECommerce: (d) => API.post(`${base}/ecommerce/connect`, d),
      syncECommerceOrders: (id, since) => API.post(`${base}/ecommerce/connections/${id}/sync${since ? '?since=' + since : ''}`),
      syncAllECommerce: () => API.post(`${base}/ecommerce/sync-all`),
      testECommerceConnection: (id) => API.post(`${base}/ecommerce/connections/${id}/test`),
      disconnectECommerce: (id) => API.del(`${base}/ecommerce/connections/${id}`),
      // Bank Feeds
      getBankFeedConnections: () => API.get(`${base}/bank-feeds/connections`),
      createBankFeedConnection: (d) => API.post(`${base}/bank-feeds/connections`, d),
      syncBankFeed: (id) => API.post(`${base}/bank-feeds/connections/${id}/sync`),
      syncAllBankFeeds: () => API.post(`${base}/bank-feeds/sync-all`),
      testBankFeedConnection: (id) => API.post(`${base}/bank-feeds/connections/${id}/test`),
      deleteBankFeedConnection: (id) => API.del(`${base}/bank-feeds/connections/${id}`),
      // Executive Reports
      getExecutiveSummary: (q = '') => API.get(`${base}/executive-reports/summary${q}`),
      getFinancialRatios: (q = '') => API.get(`${base}/executive-reports/ratios${q}`),
      getTrendAnalysis: (q = '') => API.get(`${base}/executive-reports/trends${q}`),
      getCustomerAnalytics: (q = '') => API.get(`${base}/executive-reports/customers${q}`),
      getSupplierAnalytics: (q = '') => API.get(`${base}/executive-reports/suppliers${q}`),
      getProductAnalytics: (q = '') => API.get(`${base}/executive-reports/products${q}`),
      getBudgetVariance: (q = '') => API.get(`${base}/executive-reports/budget-variance${q}`),
      getCashFlowForecast: (q = '') => API.get(`${base}/executive-reports/cash-flow-forecast${q}`),
      getBreakEvenAnalysis: (q = '') => API.get(`${base}/executive-reports/break-even${q}`),
      getSalesPerformance: (q = '') => API.get(`${base}/executive-reports/sales-performance${q}`),
      getProjectProfitability: (q = '') => API.get(`${base}/executive-reports/project-profitability${q}`),

      // Document Approvals (Signature-based)
      setupDocApproval: (d) => API.post(`${base}/approvals/setup`, d),
      getDocApprovals: (documentId) => API.get(`${base}/approvals/document/${documentId}`),
      getPendingDocApprovals: () => API.get(`${base}/approvals/pending`),
      approveDoc: (approvalId, d) => API.post(`${base}/approvals/${approvalId}/approve`, d),
      rejectDoc: (approvalId, d) => API.post(`${base}/approvals/${approvalId}/reject`, d),
      getDocSignatures: (documentId) => API.get(`${base}/approvals/document/${documentId}/signatures`),
      // External Approval
      externalApproveQuotation: (documentId, d) => API.post(`${base}/external/quotations/${documentId}/approve`, d),
      // Team / Members
      getMembers: () => API.get(`/api/company/${companyId}/users`),
      addMember: (email, role) => API.post(`/api/company/${companyId}/users`, { email, role }),
      updateMemberRole: (userId, role) => API.put(`/api/company/${companyId}/users/${userId}/role`, { role }),
      updateMemberName: (userId, fullName) => API.put(`/api/company/${companyId}/users/${userId}/name`, { fullName }),
      removeMember: (userId) => API.del(`/api/company/${companyId}/users/${userId}`),
      getUsageDetail: () => API.get(`/api/subscription/${companyId}/usage/detail`),
    };
  },

  // User Signatures (global, not company-scoped)
  getSignatures: () => API.get('/api/signatures'),
  getDefaultSignature: () => API.get('/api/signatures/default'),
  uploadSignature: (d) => API.post('/api/signatures', d),
  setDefaultSignature: (id) => API.post(`/api/signatures/${id}/set-default`),
  deleteSignature: (id) => API.del(`/api/signatures/${id}`),

  // Company management
  // User profile (incl. signature)
  getProfile: () => API.get('/api/auth/profile'),
  updateProfile: (data) => API.put('/api/auth/profile', data),
  // บัญชี Google/Facebook/LINE ที่ผูกกับผู้ใช้ (หน้าตั้งค่า → ความปลอดภัยบัญชี)
  getExternalLogins: () => API.get('/api/auth/external-logins'),
  removeExternalLogin: (id) => API.del(`/api/auth/external-logins/${id}`),
  // ผูกเพิ่มขณะล็อกอินอยู่แล้ว — ไม่ต้องมีอีเมลจาก provider (LINE ที่ยังไม่ได้
  // สิทธิ์ email ก็ผูกได้) · idToken = authorization code ที่เพิ่งได้จาก OAuth
  linkExternalLogin: (provider, idToken) =>
    API.post('/api/auth/external-logins/link', { provider, idToken }),

  getCompanies: () => API.get('/api/company'),
  createCompany: (d) => API.post('/api/company', d),
  getCompany: (id) => API.get(`/api/company/${id}`),
  updateCompany: (id, d) => API.put(`/api/company/${id}`, d),

  // Subscription
  getPlans: () => API.get('/api/subscription/plans'),
  getFeatureCatalog: () => API.get('/api/subscription/feature-catalog'),
  startTrial: (d) => API.post('/api/subscription/trial/start', d),
};

API.init();
