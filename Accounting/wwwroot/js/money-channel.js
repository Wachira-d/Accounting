/*
 * ช่องทางเงิน — ตัวเลือก "จ่าย/รับเงินจากบัญชีไหน" ตัวกลางของหน้าที่เคลื่อนเงิน (เงินสดย่อย · สินเชื่อ · ใบเบิก · เงินทดรอง)
 *
 * ที่มา (ทีม F1 · 2026-10-14 · PAYMENT_CHANNEL_AUDIT ส่วนที่ 1 ข้อ 1/19 · ส่วนที่ 2 ข้อ 1/2/8):
 *   หน้าเงินสดย่อยส่ง BankAccount.Id ไปในช่องที่เซิร์ฟเวอร์ใช้เป็นผังบัญชี ⇒ FK ล้ม · หน้าอื่นไม่มีตัวเลือกเลย ⇒ เงินโอนถูกลงเป็นเงินสด
 *   ตัวนี้แยก "ชนิดของรหัส" ไว้ในค่า option เสมอ: `bank:<BankAccounts.Id>` / `account:<ChartOfAccounts.Id>` แล้ว `read()` คืนเป็นสองช่อง
 *   (bankAccountId / accountId) ⇒ หน้าไม่มีทางส่งรหัสผิดตาราง · เซิร์ฟเวอร์เป็นคนแปลงธนาคาร → ผังที่ผูก (LinkedAccountId) และตรวจ
 *   บริษัท/สถานะ/การผูกเอง (Helpers/PickedAccountGuard) — ที่นี่แค่แสดง
 *
 *   โหลดไม่สำเร็จ = แจ้งดัง (toast + ตัวเลือกบอกเหตุ) ไม่ใช่ปล่อยรายการว่างเงียบ ๆ (ส่วนที่ 1 ข้อ 16)
 */
(function () {
  function esc(v) { return (window.Layout && Layout.esc) ? Layout.esc(v == null ? '' : String(v)) : String(v == null ? '' : v); }

  window.MoneyChannel = {
    /**
     * เติม <select> ด้วยบัญชีธนาคารที่เปิดใช้งาน (ผูกผังแล้ว = เลือกได้ · ยังไม่ผูก = แสดงแต่เลือกไม่ได้ พร้อมบอกที่แก้)
     * + ผังเงินสด/ช่องทางอื่นจาก `accounts/payment-channels` (เลือกได้เมื่อ includeAccounts)
     * @returns {Promise<{banks:any[], accounts:any[], failed:boolean}>}
     */
    async fill(sel, opts) {
      opts = opts || {};
      const placeholder = opts.placeholder || '— เลือกบัญชี —';
      const includeAccounts = opts.includeAccounts !== false;
      if (!sel) return { banks: [], accounts: [], failed: true };
      sel.innerHTML = '<option value="">กำลังโหลดรายการบัญชี…</option>';
      const api = window.Layout && Layout.api ? Layout.api() : null;
      let banks = [], accounts = [], failed = !api;
      if (api) {
        try { const r = await api.getBankAccounts(); banks = (r && r.data) || []; } catch (e) { failed = true; }
        if (includeAccounts) {
          try { const r = await api.getPaymentChannels(); accounts = (r && r.data) || []; } catch (e) { failed = true; }
        }
      }
      const active = banks.filter(b => b && b.isActive !== false);
      const parts = [`<option value="">${esc(placeholder)}</option>`];
      if (active.length) {
        parts.push('<optgroup label="บัญชีธนาคาร">');
        active.forEach(b => {
          const label = `${b.bankName || ''} ${b.accountNumber || ''} — ${b.accountName || ''}`.trim();
          if (b.linkedAccountId) {
            parts.push(`<option value="bank:${esc(b.id)}" data-gl="${esc(b.linkedAccountId)}">${esc(label)}${b.linkedAccountCode ? ' · ผัง ' + esc(b.linkedAccountCode) : ''}</option>`);
          } else {
            parts.push(`<option value="" disabled>${esc(label)} (ยังไม่ผูกผังบัญชี — ผูกที่หน้า “บัญชีธนาคาร”)</option>`);
          }
        });
        parts.push('</optgroup>');
      }
      if (includeAccounts && accounts.length) {
        parts.push('<optgroup label="เงินสด / ช่องทางอื่น">');
        accounts.forEach(a => parts.push(`<option value="account:${esc(a.id)}" data-gl="${esc(a.id)}">${esc(a.accountCode)} ${esc(a.accountName)}</option>`));
        parts.push('</optgroup>');
      }
      if (failed) {
        parts.push('<option value="" disabled>โหลดรายการบัญชีไม่สำเร็จ — รีเฟรชหน้าแล้วลองใหม่</option>');
        if (window.Layout && Layout.toast) Layout.toast('โหลดรายการบัญชีธนาคาร/เงินสดไม่สำเร็จ — รีเฟรชหน้าแล้วลองใหม่', 'error');
      } else if (!active.length && !(includeAccounts && accounts.length)) {
        parts.push('<option value="" disabled>ยังไม่มีบัญชีธนาคาร — เพิ่มที่หน้า “บัญชีธนาคาร”</option>');
      }
      sel.innerHTML = parts.join('');
      return { banks: active, accounts, failed };
    },

    /** ค่าที่เลือก → { bankAccountId, accountId } (อย่างใดอย่างหนึ่ง หรือ null ทั้งคู่) */
    read(sel) {
      const v = sel ? (sel.value || '') : '';
      if (v.indexOf('bank:') === 0) return { bankAccountId: v.slice(5), accountId: null };
      if (v.indexOf('account:') === 0) return { bankAccountId: null, accountId: v.slice(8) };
      return { bankAccountId: null, accountId: null };
    },

    /** เลือกตัวเลือกตามผังบัญชีที่เก็บไว้ (เช่น `Loan.MoneyAccountId`) — ธนาคารที่ผูกผังนั้นชนะ · ไม่พบ = คืน false (ผู้เรียกบอกผู้ใช้) */
    selectByGl(sel, glId) {
      if (!sel || !glId) return false;
      const opts = Array.from(sel.options);
      const hit = opts.find(o => o.value.indexOf('bank:') === 0 && o.dataset.gl === glId)
        || opts.find(o => o.value === 'account:' + glId);
      if (hit) { sel.value = hit.value; return true; }
      return false;
    },
  };
})();
