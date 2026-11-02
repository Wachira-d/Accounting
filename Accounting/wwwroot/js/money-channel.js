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
 *
 *   2026-11-02 (PETTY_CASH_AUDIT ส่วน A): เดิมแจ้งแค่ "โหลดรายการบัญชีไม่สำเร็จ — รีเฟรช" โดยไม่บอกว่า<b>คำขอไหน</b>ล้มและเซิร์ฟเวอร์ตอบอะไร ⇒
 *   ผู้ใช้รีเฟรชซ้ำไม่หาย และทีมแก้ไม่ได้เพราะไม่รู้สาเหตุ · ตอนนี้แยกสองคำขอ (บัญชีธนาคาร / ผังเงินสด-ช่องทางอื่น) ·
 *   ตัวที่สำเร็จยังแสดงตามปกติ · ตัวที่ล้มบอกชื่อ + HTTP status + ข้อความเซิร์ฟเวอร์ในตัวเลือกและ toast ·
 *   คำตอบ success:false (เช่น 429/บริษัทยังไม่พร้อม) นับเป็น "ล้ม" ไม่ใช่ "ไม่มีบัญชี" (เดิมแสดง "ยังไม่มีบัญชีธนาคาร" ทั้งที่ยังโหลดไม่ได้)
 */
(function () {
  function esc(v) { return (window.Layout && Layout.esc) ? Layout.esc(v == null ? '' : String(v)) : String(v == null ? '' : v); }

  window.MoneyChannel = {
    /**
     * เติม <select> ด้วยบัญชีธนาคารที่เปิดใช้งาน (ผูกผังแล้ว = เลือกได้ · ยังไม่ผูก = แสดงแต่เลือกไม่ได้ พร้อมบอกที่แก้)
     * + ผังเงินสด/ช่องทางอื่นจาก `accounts/payment-channels` (เลือกได้เมื่อ includeAccounts)
     * @returns {Promise<{banks:any[], accounts:any[], failed:boolean, errors:{source:string,status:?number,message:string}[]}>}
     */
    async fill(sel, opts) {
      opts = opts || {};
      const placeholder = opts.placeholder || '— เลือกบัญชี —';
      const includeAccounts = opts.includeAccounts !== false;
      if (!sel) return { banks: [], accounts: [], failed: true };
      sel.innerHTML = '<option value="">กำลังโหลดรายการบัญชี…</option>';
      const api = window.Layout && Layout.api ? Layout.api() : null;
      let banks = [], accounts = [];
      const errors = [];   // [{ source, status, message }] — คำขอไหนล้ม (ผู้เรียก/หน้าจอบอกผู้ใช้ได้ตรงตัว)
      if (!api) errors.push({ source: 'บริษัท', status: null, message: 'ยังไม่ได้เลือกบริษัท' });
      const load = async (source, call) => {
        try {
          const r = await call();
          if (r && r.success === false) { errors.push({ source, status: null, message: r.message || 'เซิร์ฟเวอร์ไม่ส่งรายการกลับมา' }); return []; }
          const data = r && r.data;
          if (data != null && !Array.isArray(data)) { errors.push({ source, status: null, message: 'รูปแบบข้อมูลที่ได้ไม่ใช่รายการ' }); return []; }
          return data || [];
        } catch (e) {
          errors.push({ source, status: (e && e.status) || null, message: (e && e.message) || String(e) });
          if (window.console) console.error('MoneyChannel.fill: โหลด' + source + 'ไม่สำเร็จ', e);
          return [];
        }
      };
      if (api) {
        // สองคำขอไม่พึ่งกัน — ยิงพร้อมกัน · ตัวหนึ่งล้มไม่ลากอีกตัว
        const [b, a] = await Promise.all([
          load('รายการบัญชีธนาคาร', () => api.getBankAccounts()),
          includeAccounts ? load('ผังเงินสด/ช่องทางอื่น', () => api.getPaymentChannels()) : Promise.resolve([]),
        ]);
        banks = b; accounts = a;
      }
      const failed = errors.length > 0;
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
        const describe = (x) => `${x.source}${x.status ? ' (HTTP ' + x.status + ')' : ''}: ${x.message}`;
        errors.forEach(x => parts.push(`<option value="" disabled>โหลด${esc(describe(x))}</option>`));
        if (window.Layout && Layout.toast) {
          Layout.toast('โหลดตัวเลือก “บัญชี” ไม่ครบ — ' + errors.map(describe).join(' · ')
            + ' · ถ้ารีเฟรชแล้วยังเป็น แจ้งผู้ดูแลพร้อมข้อความนี้', 'error', 10000);
        }
      } else if (!active.length && !(includeAccounts && accounts.length)) {
        parts.push('<option value="" disabled>ยังไม่มีบัญชีธนาคาร — เพิ่มที่หน้า “บัญชีธนาคาร”</option>');
      }
      sel.innerHTML = parts.join('');
      return { banks: active, accounts, failed, errors };
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
