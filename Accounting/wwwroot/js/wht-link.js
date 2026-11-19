/**
 * รอบ 271 — โมดัล "ผูก / เปลี่ยน 50 ทวิ ที่ผูก" (ใช้ร่วม documents.html + wht.html)
 *
 * หน้านี้ **แสดงอย่างเดียว**: ผู้สมัคร · ผลตรวจ (✅/⚠️/⛔) · ตารางเทียบ · ข้อความใบเดิมที่จะถูกยกเลิก · กล่องแดงเดือนที่ยื่นแล้ว
 * มาจากเซิร์ฟเวอร์ทั้งหมด (Helpers/WhtCertLinkPlan · WhtCertVoidGuard) — ไม่มีสำเนากติกาใน JS (หลักการ #5)
 * ทางเข้าผูกมีทางเดียว: POST document/{pv}/wht-link (โมดัลกลับทิศจากหน้า 50 ทวิ ก็เรียกตัวนี้)
 * ทุกค่าที่ต่อเข้า HTML ผ่าน Layout.esc (กฎ #4 C)
 */
const WhtLink = {
  _ensure() {
    let wrap = document.getElementById('whtLinkModal');
    if (wrap) return wrap;
    wrap = document.createElement('div');
    wrap.id = 'whtLinkModal';
    wrap.className = 'modal-overlay';
    wrap.innerHTML = '<div class="modal" style="max-width:760px"><div class="modal-header"><h3 class="modal-title" id="wlTitle">ผูก 50 ทวิ</h3>'
      + '<button class="modal-close" type="button" data-wl-close>&times;</button></div>'
      + '<div class="modal-body"><div id="wlBody" style="font-size:13px;line-height:1.6"></div></div>'
      + '<div class="modal-footer" id="wlFooter" style="flex-wrap:wrap;gap:6px"></div></div>';
    document.body.appendChild(wrap);
    wrap.querySelector('[data-wl-close]').onclick = () => Layout.closeModal('whtLinkModal');
    return wrap;
  },

  _resultBadge(verdict, label) {
    const color = verdict === 'Ok' ? '#166534' : verdict === 'OkWithWarnings' ? '#92400e' : '#991b1b';
    return `<span style="color:${color}">${Layout.esc(label || '')}</span>`;
  },

  _redBox(text) {
    return `<div style="padding:10px 12px;background:#FEF2F2;border:1px solid #FCA5A5;border-radius:8px;color:#991B1B">⛔ ${Layout.esc(text || '')}</div>`;
  },

  /** ตารางผู้สมัคร (ใช้ทั้งโมดัลและฟอร์มใบสำคัญจ่าย) — ใบ ⛔ แสดงแต่เลือกไม่ได้ + เหตุผล */
  candidatesTableHtml(cands, radioName, selectedId) {
    if (!cands || !cands.length) return '';
    return `<div class="table-container"><table class="table" style="font-size:12px"><thead><tr><th></th><th>เลขที่</th><th>เดือน</th>`
      + `<th class="text-right">เงินได้</th><th class="text-right">ภาษี</th><th>ผลตรวจ</th></tr></thead><tbody>`
      + cands.map(c => {
        const blocked = c.verdict === 'Blocked';
        const checked = !blocked && c.certId === selectedId ? ' checked' : '';
        return `<tr${blocked ? ' style="opacity:.75"' : ''}>`
          + `<td>${blocked ? '–' : `<input type="radio" name="${Layout.esc(radioName)}" value="${Layout.esc(c.certId)}"${checked}>`}</td>`
          + `<td class="font-medium">${Layout.esc(c.certificateNumber)} <span style="color:#64748b">(${Layout.esc(c.statusLabel)})</span></td>`
          + `<td>${Layout.esc(String(c.taxMonth).padStart(2, '0'))}/${Layout.esc(String(Number(c.taxYear) + 543))}</td>`
          + `<td class="text-right">${Layout.money(c.totalIncomeAmount)}</td><td class="text-right">${Layout.money(c.totalTaxAmount)}</td>`
          + `<td>${this._resultBadge(c.verdict, c.resultLabel)}</td></tr>`;
      }).join('') + '</tbody></table></div>'
      + '<div class="text-xs" style="color:#64748b;margin-top:4px">⚙️ ระบบแนะนำ — เรียงตามผู้ถูกหัก · ยอดภาษี · เดือน (กติกาตายตัว ไม่ใช้ AI)</div>';
  },

  /**
   * เปิดจากฝั่งใบสำคัญจ่าย (รายละเอียดหลังอนุมัติ) — opts: { paymentId, presetCertId, onDone }
   */
  async openForDocument(docId, opts = {}) {
    const api = Layout.api(); if (!api) return;
    this._ensure();
    this._ctx = { docId, paymentId: opts.paymentId || null, onDone: opts.onDone || null };
    const body = document.getElementById('wlBody');
    const footer = document.getElementById('wlFooter');
    document.getElementById('wlTitle').textContent = 'ผูก / เปลี่ยน 50 ทวิ ของใบสำคัญจ่าย';
    body.innerHTML = 'กำลังโหลด...';
    footer.innerHTML = '<button class="btn btn-secondary" type="button" data-wl-cancel>ปิด</button>';
    footer.querySelector('[data-wl-cancel]').onclick = () => Layout.closeModal('whtLinkModal');
    Layout.openModal('whtLinkModal');
    let r;
    try { r = (await api.getDocWhtLinkCandidates(docId, this._ctx.paymentId)).data; }
    catch (e) { body.innerHTML = this._redBox(e.message); return; }
    this._ctx.current = r.currentCertNumber || null;
    if (r.filedBlock) {
      // 271-0-2: เดือนที่ยื่นแล้ว — ไม่แสดงตารางผู้สมัครเลย ไม่มีปุ่มรับทราบ
      body.innerHTML = this._redBox(r.filedBlock)
        + '<div style="margin-top:8px"><a href="/pages/tax.html">เปิดหน้ารายงานภาษี</a></div>';
      return;
    }
    const pv = r.pv || {};
    let html = `<div style="margin-bottom:8px">การจ่าย: <b>${Layout.esc(pv.documentNumber || '')}</b> · ผู้ถูกหัก ${Layout.esc(pv.payeeName || '')}`
      + ` · ${Layout.esc(pv.formLabel || '')} · ภาษี <b>${Layout.money(pv.taxAmount)}</b>`
      + (r.currentCertNumber ? ` · ใบปัจจุบัน <b>${Layout.esc(r.currentCertNumber)}</b>` : ' · ยังไม่มีใบ') + '</div>';
    if (!r.candidates || !r.candidates.length) {
      body.innerHTML = html + `<div style="padding:8px 10px;background:#FFFBEB;border:1px solid #FCD34D;border-radius:8px;color:#92400E">${Layout.esc(r.hiddenReason || 'ไม่พบใบที่ผูกได้')}</div>`;
      return;
    }
    html += '<div class="form-label">1) เลือกใบที่ออกไว้แล้ว</div>' + this.candidatesTableHtml(r.candidates, 'wlCand', opts.presetCertId)
      + '<div id="wlCheck" style="margin-top:12px"></div>';
    body.innerHTML = html;
    body.querySelectorAll('input[name="wlCand"]').forEach(rb => { rb.onchange = () => this._check(rb.value); });
    const preset = opts.presetCertId && body.querySelector(`input[name="wlCand"][value="${CSS.escape(opts.presetCertId)}"]`);
    if (preset) this._check(opts.presetCertId);
  },

  async _check(certId) {
    const api = Layout.api(); if (!api) return;
    const box = document.getElementById('wlCheck');
    const footer = document.getElementById('wlFooter');
    box.innerHTML = 'กำลังตรวจ...';
    let c;
    try { c = (await api.checkDocWhtLink(this._ctx.docId, { certId, paymentId: this._ctx.paymentId })).data; }
    catch (e) { box.innerHTML = this._redBox(e.message); return; }
    const mark = m => m === 'Ok' ? '✅' : m === 'Warning' ? '⚠️' : '⛔';
    let html = '<div class="form-label">2) ตรวจเทียบ</div><div class="table-container"><table class="table" style="font-size:12px"><thead><tr>'
      + `<th></th><th>ใบสำคัญจ่าย ${Layout.esc(c.pv?.documentNumber || '')}</th><th>ใบที่จะผูก ${Layout.esc(c.candidate?.certificateNumber || '')}</th><th></th></tr></thead><tbody>`
      + (c.rows || []).map(row => `<tr><td>${Layout.esc(row.label)}</td><td>${Layout.esc(row.pvValue)}</td><td>${Layout.esc(row.certValue)}</td><td>${mark(row.result)}</td></tr>`).join('')
      + '</tbody></table></div>';
    const blocks = c.candidate?.blocks || [];
    const warns = c.candidate?.warnings || [];
    if (blocks.length) html += blocks.map(b => this._redBox(b.message)).join('');
    if (warns.length) html += '<div style="margin-top:8px">' + warns.map(w =>
      `<label style="display:flex;gap:8px;align-items:flex-start;margin-top:4px"><input type="checkbox" class="wl-ack" value="${Layout.esc(w.code)}" style="margin-top:3px">`
      + `<span>รับทราบ: ${Layout.esc(w.message)}</span></label>`).join('') + '</div>';
    if (c.unlinkedNotice)
      html += `<div class="form-label" style="margin-top:10px">3) ใบเดิมที่จะหลุดการผูก</div><div style="padding:8px 10px;background:#FFF7ED;border:1px solid #FDBA74;border-radius:8px">${Layout.esc(c.unlinkedNotice)}</div>`;
    html += `<div class="form-group" style="margin-top:8px"><label class="form-label" for="wlReason">เหตุผล${c.currentCertId ? ' *' : ''}</label>`
      + '<input type="text" class="form-input" id="wlReason" maxlength="300" placeholder="เช่น ใบนี้ออกด้วยมือไว้แล้ว ระบบออกซ้ำ"></div>';
    box.innerHTML = html;
    footer.innerHTML = '<button class="btn btn-secondary" type="button" data-wl-cancel>ปิด</button>'
      + (c.canLink ? `<button class="btn btn-primary" type="button" data-wl-go>${c.currentCertId ? 'ยืนยันเปลี่ยนการผูก' : 'ยืนยันผูก'}</button>` : '');
    footer.querySelector('[data-wl-cancel]').onclick = () => Layout.closeModal('whtLinkModal');
    const go = footer.querySelector('[data-wl-go]');
    if (go) go.onclick = () => this._submit(c);
  },

  async _submit(c) {
    const api = Layout.api(); if (!api) return;
    const acks = [...document.querySelectorAll('#wlCheck .wl-ack')].filter(x => x.checked).map(x => x.value);
    const need = (c.candidate?.warnings || []).map(w => w.code).filter(code => !acks.includes(code));
    if (need.length) { Layout.toast('ติ๊ก "รับทราบ" ทุกข้อก่อนยืนยัน', 'error'); return; }
    const reason = (document.getElementById('wlReason')?.value || '').trim();
    if (c.currentCertId && !reason) { Layout.toast('ระบุเหตุผลที่เปลี่ยนการผูก', 'error'); return; }
    if (c.currentCertId) {
      const ok = await Layout.confirmDanger({
        title: 'ยืนยันเปลี่ยน 50 ทวิ ที่ผูก',
        message: `ผูก ${c.candidate.certificateNumber} และยกเลิก ${c.currentCertNumber} (เลขคงอยู่ ประทับยกเลิก)`,
        mode: 'math', confirmText: 'ยืนยันเปลี่ยนการผูก',
      });
      if (!ok) return;
    }
    try {
      const res = await api.linkDocWhtCert(this._ctx.docId, {
        certId: c.candidate.certId, paymentId: this._ctx.paymentId, reason, acknowledgeWarnings: acks,
        expectedCurrentCertId: c.currentCertId || null,   // ฝ่ายค้าน M6 — ใบที่เห็นบนจอ (เปลี่ยนแล้ว = 409)
      });
      Layout.closeModal('whtLinkModal');
      Layout.toast(res.message || (res.data && res.data.message) || 'ผูกแล้ว', 'success', 7000);
      if (typeof this._ctx.onDone === 'function') await this._ctx.onDone();
    } catch (e) {
      Layout.toast(e.message, 'error', 12000);
    }
  },

  /** เปิดจากฝั่งใบ 50 ทวิ (หน้า 50 ทวิ) — เลือกใบสำคัญจ่าย แล้วเดินโมดัลฝั่งใบสำคัญจ่ายตัวเดียวกัน (ทางเข้าผูกทางเดียว) */
  async openForCert(certId, certNumber, onDone) {
    const api = Layout.api(); if (!api) return;
    this._ensure();
    const body = document.getElementById('wlBody');
    const footer = document.getElementById('wlFooter');
    document.getElementById('wlTitle').textContent = 'ผูก ' + (certNumber || '50 ทวิ') + ' กับใบสำคัญจ่าย';
    body.innerHTML = 'กำลังโหลด...';
    footer.innerHTML = '<button class="btn btn-secondary" type="button" data-wl-cancel>ปิด</button>';
    footer.querySelector('[data-wl-cancel]').onclick = () => Layout.closeModal('whtLinkModal');
    Layout.openModal('whtLinkModal');
    let list;
    try { list = (await api.getWhtPvCandidates(certId)).data || []; }
    catch (e) { body.innerHTML = this._redBox(e.message); return; }
    if (!list.length) {
      body.innerHTML = '<div style="padding:8px 10px;background:#FFFBEB;border:1px solid #FCD34D;border-radius:8px;color:#92400E">'
        + 'ไม่พบใบสำคัญจ่ายของผู้ถูกหักรายนี้ (ช่วง ±3 เดือน) ที่ยังไม่มีใบ หรือมีใบที่ระบบออกเอง — ถ้าการจ่ายนี้ไม่มีใบสำคัญจ่ายในระบบ ให้กด "ไม่มีใบสำคัญจ่าย" ที่แถวของใบ</div>';
      return;
    }
    body.innerHTML = '<div class="table-container"><table class="table" style="font-size:12px"><thead><tr><th>เอกสาร</th><th>วันที่</th>'
      + '<th class="text-right">ภาษีหัก</th><th>ใบปัจจุบัน</th><th>ผลตรวจ</th><th></th></tr></thead><tbody>'
      + list.map((p, i) => `<tr><td>${Layout.esc(p.documentTypeLabel)} <b>${Layout.esc(p.documentNumber)}</b></td>`
        + `<td>${Layout.esc(Layout.date(p.documentDate))}</td>`
        + `<td class="text-right">${Layout.money(p.withholdingTaxAmount)}</td><td>${Layout.esc(p.currentCertNumber || '— ยังไม่มีใบ')}</td>`
        + `<td>${this._resultBadge(p.verdict, p.resultLabel)}</td>`
        + `<td>${p.verdict === 'Blocked' ? '' : `<button class="btn btn-sm btn-primary" type="button" data-wl-pick="${i}">เลือก</button>`}</td></tr>`).join('')
      + '</tbody></table></div>';
    body.querySelectorAll('[data-wl-pick]').forEach(b => {
      b.onclick = () => this.openForDocument(list[Number(b.dataset.wlPick)].documentId, { presetCertId: certId, onDone });
    });
  },
};
window.WhtLink = WhtLink;
