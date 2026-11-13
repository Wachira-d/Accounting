// รอบ 264 P4 (team-264-a2b-design §6.1/§6.2 · คำตัดสิน 264-5-11/12) — ปุ่ม "จ่ายจากระบบของเรา" + "แจ้งโอนให้ผู้ขาย" บนใบซื้อที่มาจากคู่ค้า NextAcc
// กติกา: server computes · page displays — ตัวเลือก/ยอด/เหตุ/ป้ายจาก PartnerOutboundPaymentService.OptionsAsync · หน้าเว็บไม่ตัดสินอะไรเอง
// ผลของทั้งสองปุ่มไม่ใช่ "ชำระแล้ว" ฝั่งผู้ขาย — ผู้ขายรับเงินเมื่อผู้ให้บริการ/ผู้ขายยืนยันจริง (การแจ้งที่หน้าคู่ค้า)
const PartnerOutboundPay = {
  docId: null,
  opts: null,

  base() { return `/api/companies/${Layout.getCompanyId()}/partner-outbound-payments/purchases/${this.docId}`; },

  close() { const m = document.getElementById('popModal'); if (m) m.remove(); },

  shell(inner) {
    this.close();
    const m = document.createElement('div');
    m.id = 'popModal';
    m.style.cssText = 'position:fixed;inset:0;background:rgba(15,23,42,.45);z-index:1000;display:flex;align-items:flex-start;justify-content:center;overflow:auto;padding:24px 12px';
    m.innerHTML = `<div style="background:#fff;border-radius:12px;max-width:560px;width:100%;padding:16px;box-shadow:0 10px 30px rgba(0,0,0,.2)">
      <div style="display:flex;justify-content:space-between;align-items:center;margin-bottom:8px"><b>💸 จ่าย/แจ้งโอนใบซื้อจากคู่ค้า</b>
      <button type="button" class="btn btn-sm btn-secondary" data-pop-close="1">ปิด</button></div><div id="popBody">${inner}</div></div>`;
    m.addEventListener('click', (e) => { if (e.target === m || (e.target.dataset && e.target.dataset.popClose)) this.close(); });
    document.body.appendChild(m);
  },

  async open(docId) {
    this.docId = docId;
    this.shell('<div class="hint">กำลังโหลด…</div>');
    try {
      const j = await API.get(this.base());
      if (!j || j.success === false) throw new Error((j && j.message) || 'โหลดไม่สำเร็จ');
      this.opts = j.data;
      this.render();
    } catch (e) {
      document.getElementById('popBody').innerHTML = `<div class="banner warn">${Layout.esc(e.message || 'โหลดไม่สำเร็จ')}</div>`;
    }
  },

  options(list, selected) {
    return (list || []).map(o => `<option value="${Layout.esc(o.value)}"${o.value === selected ? ' selected' : ''}>${Layout.esc(o.label)}</option>`).join('');
  },

  render() {
    const o = this.opts;
    const body = document.getElementById('popBody');
    if (!body) return;
    const plans = (o.plans || []).map(p => `<li>${Layout.esc(p.kindLabel)} · ${Layout.money(p.amount)} · ${Layout.esc(p.accountLabel)} · ${Layout.esc(p.stateLabel)}`
      + `${p.slipCopyNote ? `<div style="color:#64748b;font-size:11px">${Layout.esc(p.slipCopyNote)}</div>` : ''}</li>`).join('');
    const head = `<div class="hint" style="margin-bottom:8px">${Layout.esc(o.sellerName || 'ผู้ขาย')} · ${Layout.esc(o.sourceDocumentNumber || '')} · ยอดค้างของเรา ${Layout.money(o.balanceDue)}</div>`;
    if (!o.eligible) {
      body.innerHTML = head + `<div class="banner warn">${Layout.esc(o.problem || '')}</div>` + (plans ? `<ul style="font-size:12px">${plans}</ul>` : '');
      return;
    }
    const acc = `<option value="">— เลือกบัญชีที่เงินออกจริง —</option>${this.options(o.accounts, '')}`;
    const gateway = o.gatewayOffered
      ? `<div class="card" style="margin-bottom:10px"><b>จ่ายจากระบบของเรา</b> <span class="hint">(${Layout.money(o.gatewayAmount)} ผ่านช่องทางของผู้ขาย)</span>
          <select class="form-input" id="popGwAcc" style="margin-top:6px">${acc}</select>
          <select class="form-input" id="popGwMethod" style="margin-top:6px">${this.options(o.gatewayMethods, '')}</select>
          <div class="hint" style="margin-top:4px">${Layout.esc(o.gatewayCaveat || '')}</div>
          ${o.gatewayAfterNote ? `<div class="hint" style="margin-top:2px">${Layout.esc(o.gatewayAfterNote)}</div>` : ''}
          <button type="button" class="btn btn-primary btn-sm" id="popGwGo" style="margin-top:6px">เปิดรายการชำระ</button><div id="popGwOut"></div></div>`
      : `<div class="card" style="margin-bottom:10px"><b>จ่ายจากระบบของเรา</b><div class="hint">${Layout.esc(o.gatewayProblem || '')}</div></div>`;
    const transfer = `<div class="card"><b>แจ้งโอนให้ผู้ขาย</b> <span class="hint">(โอนเองแล้ว แนบสลิปของเรา)</span>
        ${o.transferProblem ? `<div class="banner warn" style="margin-top:6px">${Layout.esc(o.transferProblem)}</div>` : ''}
        <select class="form-input" id="popTrAcc" style="margin-top:6px">${acc}</select>
        <select class="form-input" id="popTrMethod" style="margin-top:6px">${this.options(o.transferMethods, 'BankTransfer')}</select>
        <input class="form-input" id="popTrAmt" inputmode="decimal" style="margin-top:6px" placeholder="ยอดที่โอนจริง" value="${o.transferAmount != null ? Layout.esc(String(o.transferAmount)) : ''}">
        ${o.transferWht ? `<div class="hint">ภาษีหัก ณ ที่จ่ายของใบ ${Layout.money(o.transferWht)} (ตามกติกาของเรา)</div>` : ''}
        <input class="form-input" id="popTrDate" type="date" style="margin-top:6px">
        <input class="form-input" id="popTrFile" type="file" accept="image/jpeg,image/png,image/webp,application/pdf" style="margin-top:6px">
        <button type="button" class="btn btn-primary btn-sm" id="popTrGo" style="margin-top:6px">${Layout.esc(o.transferButtonLabel || 'บันทึกจ่าย')}</button><div id="popTrOut"></div></div>`;
    body.innerHTML = head + gateway + transfer + (plans ? `<div style="margin-top:10px;font-weight:600">ที่ทำไปแล้ว</div><ul style="font-size:12px">${plans}</ul>` : '');
    const gw = document.getElementById('popGwGo');
    if (gw) gw.addEventListener('click', () => this.startGateway());
    document.getElementById('popTrGo').addEventListener('click', () => this.transfer());
  },

  async startGateway() {
    const out = document.getElementById('popGwOut');
    const bank = document.getElementById('popGwAcc').value;
    if (!bank) { out.innerHTML = '<div class="banner warn">เลือกบัญชีที่เงินออกจริง — ระบบไม่เดาบัญชีให้</div>'; return; }
    const btn = document.getElementById('popGwGo');
    if (btn.disabled) return;
    btn.disabled = true;
    try {
      const j = await API.post(this.base() + '/gateway', { bankAccountId: bank, method: document.getElementById('popGwMethod').value, returnUrl: '/pages/documents.html' });
      if (!j || j.success === false) throw new Error((j && j.message) || 'ไม่สำเร็จ');
      const r = j.data;
      // ฝ่ายค้าน L8: รูป QR เฉพาะ data:image/…;base64 หรือ https (แบบ pay-link.html — เซิร์ฟเวอร์กรองแล้วอีกชั้น)
      const qrOk = r.qrPayload && /^(data:image\/(png|jpe?g|gif|webp);base64,|https:\/\/)/i.test(String(r.qrPayload));
      const qr = qrOk ? `<img src="${Layout.esc(r.qrPayload)}" alt="QR สำหรับชำระเงิน" style="max-width:220px;display:block;margin:6px auto">` : '';
      const link = r.authorizeUrl && /^https:\/\//i.test(String(r.authorizeUrl))
        ? `<a class="btn btn-sm btn-secondary" href="${Layout.esc(r.authorizeUrl)}" target="_blank" rel="noopener">ไปหน้าชำระของผู้ให้บริการ</a>` : '';
      out.innerHTML = `<div class="banner info" style="margin-top:6px">${Layout.esc(j.message || '')}</div>${qr}${link}`;
    } catch (e) { out.innerHTML = `<div class="banner warn">${Layout.esc(e.message || 'ไม่สำเร็จ')}</div>`; }
    finally { btn.disabled = false; }
  },

  async transfer() {
    const out = document.getElementById('popTrOut');
    const btn = document.getElementById('popTrGo');
    // ฝ่ายค้าน H1: ปิดปุ่มระหว่างส่ง (เซิร์ฟเวอร์กันซ้ำด้วยกุญแจ + ล็อกอยู่แล้ว — นี่กันกดรัวบนหน้า)
    if (btn.disabled) return;
    btn.disabled = true;
    const file = document.getElementById('popTrFile').files[0];
    const fd = new FormData();
    if (file) fd.append('file', file);
    fd.append('bankAccountId', document.getElementById('popTrAcc').value);
    fd.append('method', document.getElementById('popTrMethod').value);
    fd.append('amount', String(document.getElementById('popTrAmt').value || '').replace(/,/g, '').trim());
    fd.append('transferredAt', document.getElementById('popTrDate').value);
    try {
      const j = await API.upload(this.base() + '/transfer', fd);
      if (!j || j.success === false) throw new Error((j && j.message) || 'ไม่สำเร็จ');
      const r = j.data;
      const msg = `<div class="banner info" style="margin-top:6px">${Layout.esc(j.message || '')}</div>`
        + (r.warning ? `<div class="banner warn">${Layout.esc(r.warning)}</div>` : '');
      // สำเร็จแล้วโหลดตัวเลือกใหม่ (ยอดค้าง/ที่ทำไปแล้ว) — ไม่ปล่อยฟอร์มเดิมให้กดซ้ำ
      await this.open(this.docId);
      const box = document.getElementById('popTrOut');
      if (box) box.innerHTML = msg;
      else { const body = document.getElementById('popBody'); if (body) body.insertAdjacentHTML('afterbegin', msg); }
    } catch (e) {
      out.innerHTML = `<div class="banner warn">${Layout.esc(e.message || 'ไม่สำเร็จ')}</div>`;
      btn.disabled = false;
    }
  },
};

// ปุ่มบนแถวเอกสาร (documents.html) — ผูกด้วย data-pop-doc (ไม่ต่อ id เข้าสตริง onclick) · capture: ก่อนตัวเปิดแถว
document.addEventListener('click', (e) => {
  const a = e.target && e.target.closest ? e.target.closest('[data-pop-doc]') : null;
  if (!a) return;
  e.preventDefault();
  e.stopPropagation();
  PartnerOutboundPay.open(a.getAttribute('data-pop-doc'));
}, true);
