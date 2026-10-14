// Shared UX convenience helpers — เรียกใช้จากทุกหน้า:
//   - DateRangePresets — preset buttons (เดือนนี้ / เดือนก่อน / 30 วัน / Q / YTD)
//   - AutoSave — บันทึก form draft localStorage + restore ตอนกลับมา
//   - AmountInWords — convert ตัวเลขเป็น "หนึ่งหมื่นห้าพันบาทถ้วน" inline
//   - SetupChecklist — widget แสดง onboarding steps ที่ทำเสร็จ
//   - SavedViews — บันทึก filter combination ที่ใช้บ่อย (per-page namespace)

(function() {
  'use strict';
  if (window.UxHelpers) return;
  const UX = {};

  // ============================================================
  // #2 — Date Range Presets
  // ============================================================
  UX.DateRangePresets = {
    /// <param name="onSelect">callback(fromIso, toIso) เมื่อ user คลิก preset</param>
    mount(containerEl, onSelect) {
      const today = new Date();
      const presets = [
        ['วันนี้', this.today()],
        ['7 วันล่าสุด', this.last(7)],
        ['30 วันล่าสุด', this.last(30)],
        ['เดือนนี้', this.thisMonth()],
        ['เดือนก่อน', this.lastMonth()],
        ['ไตรมาสนี้', this.thisQuarter()],
        ['YTD', this.yearToDate()],
        ['ปีที่แล้ว', this.lastYear()],
      ];
      containerEl.innerHTML = presets.map(([label, [from, to]]) =>
        `<button class="btn btn-sm btn-outline" data-from="${this.iso(from)}" data-to="${this.iso(to)}" style="margin:2px;font-size:11px;padding:4px 8px">${label}</button>`
      ).join('');
      containerEl.querySelectorAll('button').forEach(btn => {
        btn.addEventListener('click', () => onSelect(btn.dataset.from, btn.dataset.to));
      });
    },
    iso(d) { return d.toISOString().slice(0, 10); },
    today() { const t = new Date(); return [t, t]; },
    last(days) { const t = new Date(); const f = new Date(t); f.setDate(f.getDate() - days); return [f, t]; },
    thisMonth() { const t = new Date(); const f = new Date(t.getFullYear(), t.getMonth(), 1); return [f, t]; },
    lastMonth() {
      const t = new Date();
      const f = new Date(t.getFullYear(), t.getMonth() - 1, 1);
      const e = new Date(t.getFullYear(), t.getMonth(), 0);
      return [f, e];
    },
    thisQuarter() {
      const t = new Date(); const q = Math.floor(t.getMonth() / 3);
      const f = new Date(t.getFullYear(), q * 3, 1);
      return [f, t];
    },
    yearToDate() {
      const t = new Date(); const f = new Date(t.getFullYear(), 0, 1);
      return [f, t];
    },
    lastYear() {
      const t = new Date();
      const f = new Date(t.getFullYear() - 1, 0, 1);
      const e = new Date(t.getFullYear() - 1, 11, 31);
      return [f, e];
    }
  };

  // ============================================================
  // #5 — Auto-Save Form Draft
  // ใช้: UX.AutoSave.attach('newInvoiceForm', { restore: true, ttlMinutes: 60 })
  // ============================================================
  UX.AutoSave = {
    _timers: {},
    attach(formId, opts = {}) {
      const form = document.getElementById(formId);
      if (!form) return;
      const key = `autosave.${formId}`;
      const ttl = (opts.ttlMinutes || 120) * 60000;

      // Restore on load
      if (opts.restore !== false) {
        try {
          const stored = JSON.parse(localStorage.getItem(key) || 'null');
          if (stored && stored.savedAt && (Date.now() - stored.savedAt < ttl)) {
            if (confirm(`พบ draft ที่บันทึกค้างไว้เมื่อ ${this._timeAgo(stored.savedAt)} — โหลดต่อจากเดิมไหม?`)) {
              for (const [k, v] of Object.entries(stored.data || {})) {
                const el = form.querySelector(`[name="${k}"], [data-f="${k}"], #${k}`);
                if (el) {
                  if (el.type === 'checkbox') el.checked = v;
                  else el.value = v;
                }
              }
            }
          }
        } catch {}
      }

      // Debounced save on input
      const save = () => {
        const data = {};
        form.querySelectorAll('input[name], select[name], textarea[name], [data-f]').forEach(el => {
          const name = el.name || el.dataset.f;
          if (!name) return;
          data[name] = el.type === 'checkbox' ? el.checked : el.value;
        });
        localStorage.setItem(key, JSON.stringify({ savedAt: Date.now(), data }));
      };
      form.addEventListener('input', () => {
        clearTimeout(this._timers[formId]);
        this._timers[formId] = setTimeout(save, 500);
      });
    },
    clear(formId) { localStorage.removeItem(`autosave.${formId}`); },
    _timeAgo(ts) {
      const m = Math.floor((Date.now() - ts) / 60000);
      if (m < 1) return 'ตอนนี้';
      if (m < 60) return `${m} นาทีก่อน`;
      const h = Math.floor(m / 60);
      if (h < 24) return `${h} ชม.ก่อน`;
      return `${Math.floor(h / 24)} วันก่อน`;
    }
  };

  // ============================================================
  // #10 — Amount-in-words inline display (Thai baht)
  // ============================================================
  // ⚠️ สำเนาของตัวตั้ง C# `Helpers/ThaiBahtText.Format` — คงไว้เพราะเป็นพรีวิวสดทุกครั้งที่กดแป้น (ยอดฟอร์มก่อนบันทึก ·
  // ใบเสร็จมือถือ · ช่อง data-amount-words) · ตัวพิมพ์จริง (PDF/HTML/50 ทวิ) มาจากเซิร์ฟเวอร์เสมอ ·
  // ต้องตรงกับ C# ทุกตัวอักษร — ล็อกด้วย tools/thai_baht_text_sim.js ซึ่งอ่านตารางเคสจาก Accounting.Tests/ThaiBahtTextTests.cs
  // (แก้ที่นี่ = แก้ C# + ตารางเทสต์ในคอมมิตเดียวกัน) · 2026-10-14: เดิม 101 → "หนึ่งร้อยหนึ่ง" · 1,000,001 → "หนึ่งล้านหนึ่ง"
  // ไม่ตรงกระดาษ ("…เอ็ด") และยอดติดลบขึ้น "ลบ…" ขณะที่เซิร์ฟเวอร์อ่านขนาด
  UX.AmountInWords = {
    convert(amount) {
      if (amount === null || amount === undefined || isNaN(amount)) return '';
      // ปัดเป็นสตางค์แบบ AwayFromZero (ขนาดก่อน แล้วค่อยปัด) — toPrecision(15) ตัดเศษทศนิยมฐานสองทิ้ง (1.005×100 = 100.4999…)
      const cents = Math.round(Number((Math.abs(Number(amount)) * 100).toPrecision(15)));
      const baht = Math.floor(cents / 100);
      const satang = cents % 100;
      if (baht === 0 && satang === 0) return 'ศูนย์บาทถ้วน';
      const text = (baht === 0 ? 'ศูนย์' : this._int(baht, false)) + 'บาท';
      return text + (satang === 0 ? 'ถ้วน' : this._int(satang, false) + 'สตางค์');
    },
    attach(inputEl, displayEl) {
      const update = () => {
        const v = parseFloat(inputEl.value);
        displayEl.textContent = isNaN(v) ? '' : '(' + this.convert(v) + ')';
      };
      inputEl.addEventListener('input', update);
      update();
    },
    _digits: ['', 'หนึ่ง', 'สอง', 'สาม', 'สี่', 'ห้า', 'หก', 'เจ็ด', 'แปด', 'เก้า'],
    _places: ['', 'สิบ', 'ร้อย', 'พัน', 'หมื่น', 'แสน'],
    // จำนวนเต็มบวก — วนกลุ่ม "ล้าน" ซ้ำ (hasHigher = มีหลักสูงกว่าที่ไม่เป็นศูนย์ ⇒ หน่วย 1 อ่าน "เอ็ด")
    _int(n, hasHigher) {
      if (n >= 1000000) {
        const high = Math.floor(n / 1000000), low = n % 1000000;
        return this._int(high, hasHigher) + 'ล้าน' + (low === 0 ? '' : this._group(low, true));
      }
      return this._group(n, hasHigher);
    },
    // กลุ่มเดียว 0..999,999
    _group(v, hasHigher) {
      let result = '';
      for (let pos = 5, div = 100000; pos >= 0; pos--, div /= 10) {
        const d = Math.floor(v / div) % 10;
        if (d === 0) continue;
        if (pos === 1) result += (d === 1 ? '' : d === 2 ? 'ยี่' : this._digits[d]) + 'สิบ';
        else if (pos === 0 && d === 1 && (hasHigher || v > 9)) result += 'เอ็ด';
        else result += this._digits[d] + this._places[pos];
      }
      return result;
    }
  };

  // ============================================================
  // #20 — Setup Checklist Widget
  // Frontend mounts → fetch /api/companies/{cid}/setup-status → render
  // ============================================================
  UX.SetupChecklist = {
    async fetch(companyId) {
      try {
        const r = await fetch(`/api/companies/${companyId}/setup-status`, {
          headers: { 'Authorization': `Bearer ${localStorage.getItem('token')}` }
        });
        const j = await r.json();
        return j.data;
      } catch { return null; }
    },
    render(targetEl, status) {
      if (!status) return;
      const items = [
        { key: 'companyInfo', label: 'ตั้งค่าข้อมูลบริษัท (ชื่อ + เลขผู้เสียภาษี)', href: '/pages/settings.html' },
        { key: 'chartOfAccounts', label: 'นำเข้าผังบัญชี', href: '/pages/accounts.html' },
        { key: 'firstContact', label: 'เพิ่มลูกค้า/vendor คนแรก', href: '/pages/contacts.html' },
        { key: 'bankAccount', label: 'เชื่อมบัญชีธนาคาร', href: '/pages/bank.html' },
        // ไฟล์จริงเป็นพหูพจน์ document-templateS.html — เดิมสะกดเอกพจน์
        // ⇒ ข้อนี้ในเช็กลิสต์เริ่มต้นใช้งานคลิกแล้วตาย (จับด้วย tools/dead_link_check.py)
        { key: 'documentTemplate', label: 'ตั้งค่าเทมเพลตเอกสาร (โลโก้ + สี)', href: '/pages/document-templates.html' },
        { key: 'firstEmployee', label: 'เพิ่มพนักงานคนแรก (ถ้ามี payroll)', href: '/pages/payroll.html' },
        { key: 'firstDocument', label: 'สร้างเอกสารใบแรก', href: '/pages/documents.html' },
      ];
      const done = items.filter(i => status[i.key]).length;
      const pct = Math.round(done / items.length * 100);
      const html = items.map(i => `
        <div style="display:flex;align-items:center;gap:8px;padding:6px 0">
          <span style="font-size:16px">${status[i.key] ? '✅' : '⬜'}</span>
          <a href="${i.href}" style="flex:1;color:${status[i.key] ? '#9ca3af' : '#1f2937'};text-decoration:${status[i.key] ? 'line-through' : 'none'};font-size:13px">${i.label}</a>
        </div>`).join('');
      targetEl.innerHTML = `
        <div class="card" style="padding:14px">
          <div style="display:flex;justify-content:space-between;align-items:center;margin-bottom:8px">
            <h3 style="margin:0;font-size:14px">🚀 Setup Checklist</h3>
            <span style="font-size:12px;color:#6b7280">${done}/${items.length} (${pct}%)</span>
          </div>
          <div style="height:4px;background:#e5e7eb;border-radius:2px;margin-bottom:10px"><div style="width:${pct}%;height:100%;background:#10b981;border-radius:2px;transition:width 0.3s"></div></div>
          ${html}
        </div>`;
    }
  };

  // ============================================================
  // #14 — Saved Views (per-page namespace)
  // ใช้: UX.SavedViews.save('aging', 'top-overdue', { filter1, filter2 })
  //      UX.SavedViews.list('aging') → [{name, data}, ...]
  // ============================================================
  UX.SavedViews = {
    _key(ns) { return `savedViews.${ns}`; },
    list(ns) {
      try { return JSON.parse(localStorage.getItem(this._key(ns)) || '[]'); }
      catch { return []; }
    },
    save(ns, name, data) {
      const all = this.list(ns).filter(v => v.name !== name);
      all.push({ name, data, savedAt: Date.now() });
      localStorage.setItem(this._key(ns), JSON.stringify(all));
    },
    load(ns, name) {
      return this.list(ns).find(v => v.name === name)?.data;
    },
    remove(ns, name) {
      const all = this.list(ns).filter(v => v.name !== name);
      localStorage.setItem(this._key(ns), JSON.stringify(all));
    },
    mount(ns, containerEl, onLoad) {
      const render = () => {
        const views = this.list(ns);
        containerEl.innerHTML = views.length === 0
          ? `<button class="btn btn-sm btn-outline" onclick="UxHelpers.SavedViews.promptSave('${ns}', window._currentFilter || {}); UxHelpers.SavedViews._refresh()">+ บันทึก view</button>`
          : views.map(v => `
              <span class="pill pill-info" style="margin:2px;cursor:pointer;padding:4px 10px;display:inline-flex;align-items:center;gap:4px"
                onclick="(${onLoad.toString()})(${JSON.stringify(v.data).replace(/"/g, '&quot;')})">
                ${v.name}
                <span style="margin-left:4px;color:#6b7280;cursor:pointer" onclick="event.stopPropagation();if(confirm('ลบ \\'${v.name}\\'?')){UxHelpers.SavedViews.remove('${ns}','${v.name}');UxHelpers.SavedViews._refresh()}">×</span>
              </span>`).join('') +
            `<button class="btn btn-sm btn-outline" onclick="UxHelpers.SavedViews.promptSave('${ns}', window._currentFilter || {}); UxHelpers.SavedViews._refresh()">+</button>`;
      };
      this._refresh = render;
      render();
    },
    promptSave(ns, currentFilter) {
      const name = prompt('ตั้งชื่อ view (เช่น "Top 10 ลูกหนี้เกิน 90 วัน"):');
      if (!name) return;
      this.save(ns, name.trim(), currentFilter);
    }
  };

  window.UxHelpers = UX;
})();
