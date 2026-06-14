// Smart inline validation hooks — auto-attaches to any input/element
// matching patterns. Wires Smart Safety endpoints to forms without
// requiring per-page integration.
//
// Triggers:
//   - [data-validate="tax-id"] OR id matching /tax|tax-id|TaxId/ — Thai mod-11 checksum
//   - [data-validate="vat-amount"] — flag mismatch vs subtotal × rate
//   - [data-check-period] on date inputs — warn ถ้าวันที่อยู่ในงวดปิด
//   - [data-amount-words] — แสดง "หนึ่งพันบาทถ้วน" ใต้ช่อง
//
// Re-mounts every 1s via MutationObserver แทน ListenForever — ครอบ
// dynamic forms (modal open / table add row).

(function() {
  'use strict';
  if (window.SmartHooks) return;

  const cid = () => {
    try { return JSON.parse(localStorage.getItem('currentCompany') || '{}').id; } catch { return null; }
  };

  // ============================================================
  // Thai Tax ID checksum (client-side — instant, no API call)
  // ============================================================
  function thaiTaxIdValid(taxId) {
    const clean = (taxId || '').replace(/\D/g, '');
    if (clean.length !== 13) return { valid: false, reason: `${clean.length}/13 หลัก` };
    const first = clean[0];
    if (first < '0' || first > '8') return { valid: false, reason: 'หลักแรกต้องเป็น 0-8' };
    let sum = 0;
    for (let i = 0; i < 12; i++) sum += parseInt(clean[i]) * (13 - i);
    const check = (11 - (sum % 11)) % 10;
    if (check !== parseInt(clean[12])) return { valid: false, reason: 'check digit ผิด' };
    return { valid: true };
  }

  function attachTaxId(el) {
    if (el._smartHookAttached) return;
    el._smartHookAttached = true;
    // สร้าง status icon ถัดจาก input
    const status = document.createElement('span');
    status.className = 'smart-tax-status';
    status.style.cssText = 'margin-left:6px;font-size:13px;display:inline-block;min-width:20px';
    el.parentNode?.insertBefore(status, el.nextSibling);
    const handler = () => {
      const v = el.value.trim();
      if (!v) { status.textContent = ''; el.style.borderColor = ''; return; }
      const r = thaiTaxIdValid(v);
      if (r.valid) {
        status.textContent = '✅';
        status.title = 'เลขผู้เสียภาษีถูกต้อง';
        el.style.borderColor = '#10b981';
      } else {
        status.textContent = '❌';
        status.title = r.reason;
        el.style.borderColor = '#ef4444';
      }
    };
    el.addEventListener('input', handler);
    el.addEventListener('blur', handler);
    if (el.value) handler();
  }

  // ============================================================
  // VAT validation — เช็คว่า amount = subtotal × rate
  // input ต้องมี data-vat-rate + data-vat-subtotal-id (ชี้ไป input subtotal)
  // ============================================================
  function attachVatCheck(el) {
    if (el._smartHookAttached) return;
    el._smartHookAttached = true;
    const status = document.createElement('span');
    status.className = 'smart-vat-status';
    status.style.cssText = 'display:block;font-size:11px;color:#dc2626;margin-top:2px';
    el.parentNode?.appendChild(status);
    const handler = () => {
      const sub = parseFloat(document.getElementById(el.dataset.vatSubtotalId)?.value) || 0;
      const rate = parseFloat(el.dataset.vatRate || '7');
      const vat = parseFloat(el.value) || 0;
      const expected = Math.round(sub * rate / 100 * 100) / 100;
      if (Math.abs(expected - vat) > 0.05 && sub > 0) {
        status.textContent = `⚠️ ควรเป็น ${expected.toLocaleString('en-US', {minimumFractionDigits: 2})} (${rate}% × ${sub.toLocaleString('en-US')})`;
      } else {
        status.textContent = '';
      }
    };
    el.addEventListener('input', handler);
  }

  // ============================================================
  // Period close check — ตรวจวันที่ของเอกสาร ผ่าน /smart/check-period-status
  // ============================================================
  let periodCache = {};
  async function attachPeriodCheck(el) {
    if (el._smartHookAttached) return;
    el._smartHookAttached = true;
    const status = document.createElement('span');
    status.style.cssText = 'display:block;font-size:11px;margin-top:2px';
    el.parentNode?.appendChild(status);
    const handler = async () => {
      const c = cid(); if (!c || !el.value) { status.textContent = ''; return; }
      const cacheKey = `${c}:${el.value}`;
      if (periodCache[cacheKey]) {
        renderResult(periodCache[cacheKey]);
        return;
      }
      try {
        const r = await fetch(`/api/companies/${c}/smart/check-period-status?documentDate=${el.value}`, {
          headers: { 'Authorization': `Bearer ${localStorage.getItem('token')}` }
        });
        const j = await r.json();
        periodCache[cacheKey] = j.data;
        renderResult(j.data);
      } catch { status.textContent = ''; }
    };
    function renderResult(d) {
      if (!d) { status.textContent = ''; return; }
      if (d.shouldBlock) {
        status.style.color = '#dc2626';
        status.textContent = `🚫 ${d.message}`;
      } else if (d.shouldWarn) {
        status.style.color = '#d97706';
        status.textContent = `⚠️ ${d.message}`;
      } else {
        status.textContent = '';
      }
    }
    el.addEventListener('change', handler);
    if (el.value) handler();
  }

  // ============================================================
  // Amount in words — แสดง "หนึ่งพันบาทถ้วน" ใต้ช่อง amount
  // ============================================================
  function amountInWordsTh(num) {
    if (window.UxHelpers?.AmountInWords) return window.UxHelpers.AmountInWords.convert(num);
    // Fallback: simple
    return '';
  }
  function attachAmountWords(el) {
    if (el._smartHookAttached) return;
    el._smartHookAttached = true;
    const status = document.createElement('div');
    status.style.cssText = 'font-size:11px;color:#6b7280;margin-top:2px;font-style:italic';
    el.parentNode?.appendChild(status);
    const handler = () => {
      const n = parseFloat(el.value);
      status.textContent = isNaN(n) || n === 0 ? '' : `(${amountInWordsTh(n)})`;
    };
    el.addEventListener('input', handler);
    if (el.value) handler();
  }

  // ============================================================
  // Auto-scan + attach
  // ============================================================
  function scan() {
    document.querySelectorAll('input[data-validate="tax-id"], input[id="fTaxId"], input[id="empIdCard"]').forEach(attachTaxId);
    document.querySelectorAll('input[data-validate="vat-amount"]').forEach(attachVatCheck);
    document.querySelectorAll('input[type="date"][data-check-period]').forEach(attachPeriodCheck);
    document.querySelectorAll('input[data-amount-words]').forEach(attachAmountWords);
  }

  // Run on load + MutationObserver for dynamic forms
  if (document.readyState === 'loading') {
    document.addEventListener('DOMContentLoaded', scan);
  } else {
    scan();
  }
  const observer = new MutationObserver(() => {
    clearTimeout(window._smartScanTimer);
    window._smartScanTimer = setTimeout(scan, 200);
  });
  observer.observe(document.body || document.documentElement, { childList: true, subtree: true });

  window.SmartHooks = { scan, thaiTaxIdValid };
})();
