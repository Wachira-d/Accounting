/**
 * รอบ 219 ส่วน B (คำตัดสินข้อ 179) — ช่องติ๊ก "ยอมรับข้อกำหนด + นโยบายการยกเลิกและคืนเงิน" ก่อนชำระเงิน (ตัวเดียวของทุกหน้า)
 *
 * ข้อความ · ฉบับ · ลิงก์ มาจากเซิร์ฟเวอร์เสมอ (แพลตฟอร์ม: GET /api/companies/{cid}/platform-pay → policy ·
 * ร้าน: ข้อมูลเว็บ storefront → policyAcceptanceText/policyVersion/termsLink/refundLink) — หน้านี้ไม่มีสำเนากติกา
 * ด่านจริงอยู่ที่เซิร์ฟเวอร์ (Helpers/PurchasePolicyAcceptance) — ไม่ติ๊ก = 400 ข้อความไทย · ตัวตรวจฝั่งหน้าเว็บมีไว้บอกเร็วเท่านั้น
 *
 * ใช้:  el.innerHTML = PolicyAccept.html({ text, version, termsUrl, refundUrl })
 *       const v = PolicyAccept.value(el)   // { policyAccepted, policyVersionShown }
 *       PolicyAccept.require(el)           // โยน Error ถ้ายังไม่ติ๊ก
 */
(function (global) {
  const esc = s => String(s == null ? '' : s).replace(/[&<>"']/g, c => ({ '&': '&amp;', '<': '&lt;', '>': '&gt;', '"': '&quot;', "'": '&#39;' }[c]));
  const safeUrl = u => (typeof u === 'string' && /^(https?:\/\/|\/)/i.test(u.trim())) ? u.trim() : null;
  const MISSING = 'กรุณาติ๊กยอมรับข้อกำหนดและนโยบายการยกเลิก/คืนเงินก่อนดำเนินการต่อ';

  const PolicyAccept = {
    html(p) {
      if (!p || !p.text) return '';
      const terms = safeUrl(p.termsUrl), refund = safeUrl(p.refundUrl);
      const links = [
        terms ? `<a href="${esc(terms)}" target="_blank" rel="noopener">ข้อกำหนด</a>` : '',
        refund ? `<a href="${esc(refund)}" target="_blank" rel="noopener">นโยบายการยกเลิกและคืนเงิน</a>` : '',
      ].filter(Boolean).join(' · ');
      return `<label class="policy-accept" data-policy-version="${esc(p.version || '')}"
          style="display:flex;gap:8px;align-items:flex-start;font-size:13px;line-height:1.55;margin:8px 0;text-align:left">
        <input type="checkbox" name="policyAccepted" value="1" required style="margin-top:3px;width:auto;flex:none">
        <span>${esc(p.text)}${links ? ` <span style="white-space:nowrap">(${links})</span>` : ''} <b style="color:#DC2626">*</b></span>
      </label>`;
    },
    _box(root) {
      const el = typeof root === 'string' ? document.getElementById(root) : root;
      return el ? (el.matches && el.matches('.policy-accept') ? el : el.querySelector('.policy-accept')) : null;
    },
    value(root) {
      const box = this._box(root);
      if (!box) return { policyAccepted: false, policyVersionShown: null };
      const cb = box.querySelector('input[name="policyAccepted"]');
      return { policyAccepted: !!(cb && cb.checked), policyVersionShown: box.getAttribute('data-policy-version') || null };
    },
    require(root) {
      const v = this.value(root);
      if (!v.policyAccepted) throw new Error(MISSING);
      return v;
    },
  };
  global.PolicyAccept = PolicyAccept;
})(window);
