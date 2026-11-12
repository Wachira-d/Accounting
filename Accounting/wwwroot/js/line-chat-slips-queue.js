// รอบ 260 (คำตัดสิน 260-1-1) — คิว "สลิปจากแชต LINE ที่ยังไม่ผูกเอกสาร" บนหน้ารายการรับชำระออนไลน์ (คิวเดียวกับสลิปพอร์ทัล)
// กติกา: server computes · page displays — เหตุ/ยอด/ใบที่เสนอ/ป้ายมาจากเซิร์ฟเวอร์ (LineChatSlipService.ListOpenAsync) · หน้าเว็บไม่ตัดสินอะไรเอง
// ผูกแล้ว = รายการรอยืนยันในตารางด้านล่าง (ยังไม่ใช่การรับชำระ — ยืนยันที่แถวนั้นตามเดิม)
const LineChatSlipQueue = {
  items: [],
  previews: {},

  base() { return `/api/companies/${Layout.getCompanyId()}/line-chat-slips`; },

  async load() {
    const box = document.getElementById('lineChatSlips');
    if (!box || !Layout.getCompanyId()) return;
    try {
      const j = await API.get(this.base());
      if (j && j.success === false) throw new Error(j.message || 'โหลดไม่สำเร็จ');
      this.items = (j && j.data && j.data.items) || [];
      this.render();
    } catch (e) {
      // ไม่มีสิทธิ์ดู/โหลดไม่ได้ = บอกตรง ๆ (ไม่ซ่อนเหมือนไม่มีรายการ)
      box.innerHTML = `<div class="card banner warn">โหลดสลิปจากแชต LINE ไม่สำเร็จ: ${Layout.esc(e.message || '')}</div>`;
    }
  },

  render() {
    const box = document.getElementById('lineChatSlips');
    if (!box) return;
    if (!this.items.length) { box.innerHTML = ''; return; }
    box.innerHTML = `<div class="card" style="margin-bottom:16px">
      <div style="font-weight:700;margin-bottom:4px">💬 สลิปจากแชต LINE ที่ยังไม่ผูกเอกสาร (${this.items.length})</div>
      <div class="hint" style="margin-bottom:8px">ดูรูปสลิป แล้วเลือกเอกสารที่ลูกค้าชำระ — ระบบสร้างรายการรอยืนยันในตารางด้านล่าง ·
        ถ้าเป็นเงินที่ยังไม่รู้ว่าจ่ายใบไหน ให้รอรายการเดินบัญชีเข้า แล้วบันทึกเป็น "เงินรอจัดสรร" ที่หน้าธนาคาร จากนั้นปิดรายการนี้</div>
      <div class="scroll-x"><table class="tbl"><thead><tr>
        <th>รับเมื่อ</th><th>จาก</th><th>เหตุ</th><th class="num">ยอดที่อ่านได้</th><th>เลือกเอกสาร</th><th></th>
      </tr></thead><tbody>${this.items.map(r => this.row(r)).join('')}</tbody></table></div></div>`;
  },

  row(r) {
    const opts = (r.options || []).map(o =>
      `<option value="${Layout.esc(o.documentId)}">${o.amountMatches ? '✓ ' : ''}${Layout.esc(o.documentNumber)} · ต้องโอน ${Layout.money(o.netDue)}${Number(o.grossDue) > Number(o.netDue) ? ' · ก่อนหัก ณ ที่จ่าย ' + Layout.money(o.grossDue) : ''}</option>`).join('');
    return `<tr>
      <td>${Layout.date(r.receivedAt)}</td>
      <td>${Layout.esc(r.contactName || 'ไม่ทราบผู้ติดต่อ')}<div style="color:#94a3b8;font-size:11px">${Layout.esc(r.sourceLabel || '')}</div></td>
      <td>${Layout.esc(r.reasonLabel || '')}${r.reasonDetail ? `<div style="color:#b45309;font-size:11px">${Layout.esc(r.reasonDetail)}</div>` : ''}</td>
      <td class="num">${r.amount != null ? Layout.money(r.amount) : '—'}${r.amountsText && r.amount == null ? `<div style="color:#94a3b8;font-size:11px">${Layout.esc(r.amountsText)}</div>` : ''}
        ${r.slipRef ? `<div style="font-family:ui-monospace,monospace;font-size:10px;color:#64748b">${Layout.esc(r.slipRef)}</div>` : ''}</td>
      <td style="min-width:220px">
        <select class="form-input" id="lcs-doc-${Layout.esc(r.id)}">${opts}<option value="">พิมพ์เลขที่เอกสารเอง…</option></select>
        <input class="form-input" id="lcs-num-${Layout.esc(r.id)}" placeholder="เลขที่เอกสาร (ถ้าไม่อยู่ในรายการ)" style="margin-top:4px">
        <input class="form-input" id="lcs-amt-${Layout.esc(r.id)}" placeholder="ยอด (ว่าง = ยอดที่อ่านได้/ยอดสุทธิของใบ)" inputmode="decimal" style="margin-top:4px">
      </td>
      <td style="white-space:nowrap">
        <button class="btn btn-sm btn-secondary" onclick="LineChatSlipQueue.preview('${Layout.esc(r.id)}')">ดูสลิป</button>
        <button class="btn btn-sm btn-primary" onclick="LineChatSlipQueue.assign('${Layout.esc(r.id)}')">ผูกเอกสาร</button>
        <button class="btn btn-sm btn-secondary" onclick="LineChatSlipQueue.dismiss('${Layout.esc(r.id)}')">ปิดรายการ</button>
        <div id="lcs-prev-${Layout.esc(r.id)}"></div>
      </td></tr>`;
  },

  async preview(id) {
    const holder = document.getElementById('lcs-prev-' + id);
    if (!holder) return;
    try {
      if (!this.previews[id]) {
        const f = await API.fetchFile(`${this.base()}/${id}/file`, 'slip');
        if (!f) return;
        this.previews[id] = URL.createObjectURL(f.blob);
      }
      holder.innerHTML = `<a href="${this.previews[id]}" target="_blank" rel="noopener"><img src="${this.previews[id]}" alt="สลิป" style="max-width:220px;max-height:320px;margin-top:6px;border:1px solid #e2e8f0;border-radius:6px"></a>`;
    } catch (e) { Layout.toast('เปิดสลิปไม่สำเร็จ: ' + (e.message || ''), 'error'); }
  },

  async assign(id) {
    const sel = document.getElementById('lcs-doc-' + id);
    const num = (document.getElementById('lcs-num-' + id) || {}).value || '';
    const amtRaw = ((document.getElementById('lcs-amt-' + id) || {}).value || '').replace(/,/g, '').trim();
    const body = { documentId: sel && sel.value ? sel.value : null, documentNumber: sel && sel.value ? null : num.trim(), amount: amtRaw ? Number(amtRaw) : null };
    if (!body.documentId && !body.documentNumber) { Layout.toast('เลือกเอกสาร หรือพิมพ์เลขที่เอกสาร', 'error'); return; }
    try {
      const j = await API.post(`${this.base()}/${id}/assign`, body);
      if (!j || !j.success) throw new Error((j && j.message) || 'ผูกไม่สำเร็จ');
      Layout.toast(j.message || 'ผูกแล้ว');
      await this.load();
      if (typeof Page !== 'undefined' && Page.load) await Page.load();
    } catch (e) { Layout.toast(e.message || 'ผูกไม่สำเร็จ', 'error'); }
  },

  async dismiss(id) {
    const reason = prompt('เหตุที่ปิดรายการ (เช่น ไม่ใช่สลิป · บันทึกเป็นเงินรอจัดสรรจากรายการเดินบัญชีแล้ว)');
    if (reason == null) return;
    if (!reason.trim()) { Layout.toast('กรุณาระบุเหตุ', 'error'); return; }
    try {
      const j = await API.post(`${this.base()}/${id}/dismiss`, { reason: reason.trim() });
      if (!j || !j.success) throw new Error((j && j.message) || 'ปิดไม่สำเร็จ');
      Layout.toast(j.message || 'ปิดรายการแล้ว');
      await this.load();
    } catch (e) { Layout.toast(e.message || 'ปิดไม่สำเร็จ', 'error'); }
  },
};
