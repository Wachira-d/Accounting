// รอบ 231 (คำตัดสินข้อ 231-8 · LICENSE_USAGE_OVERAGE_AUDIT §C.3) — การ์ด "ราคาพิเศษ" ของแอดมิน (รายบริษัท / ราย License)
// ใช้ร่วม 2 หน้า: /admin/customers.html (บริษัท) · /admin/account-subscriptions.html (License) — mount(el, { companyId } | { accountSubscriptionId })
// ทุกตัวเลข (ราคาตั้ง · ราคาที่มีผลต่อรอบ · ป้าย live) มาจาก GET /api/admin/price-overrides — หน้าเว็บแสดงอย่างเดียว (F2 ข้อ 5)
window.PriceOverrideAdmin = {
  async mount(el, scope) {
    if (!el || !scope) return;
    el.innerHTML = '<div style="color:#94a3b8;font-size:12px">กำลังโหลดราคาพิเศษ...</div>';
    const q = scope.companyId ? 'companyId=' + encodeURIComponent(scope.companyId) : 'accountSubscriptionId=' + encodeURIComponent(scope.accountSubscriptionId);
    let data;
    try {
      const r = await AdminAPI.get('/price-overrides?' + q);
      if (!r.success) throw new Error(r.message || 'โหลดไม่สำเร็จ');
      data = r.data;
    } catch (e) { el.innerHTML = '<div style="color:#dc2626;font-size:12px">⚠ ' + AdminLayout.esc(e.message) + '</div>'; return; }
    el.innerHTML = this.html(data, scope);
    this.wire(el, data, scope);
  },

  money(v) { return Number(v || 0).toLocaleString('th-TH', { minimumFractionDigits: 2, maximumFractionDigits: 2 }); },
  d(v) { return v ? new Date(v).toLocaleDateString('th-TH') : '-'; },
  modeLabel(m) { return ({ PriceOfPlan: 'ราคาของแพ็กเกจอื่น', FixedPerCycle: 'กำหนดราคาเอง/รอบ', PercentOff: 'ลด %' })[m] || m; },
  valueText(o) {
    if (o.mode === 'PriceOfPlan') return 'จ่ายราคา ' + AdminLayout.esc(o.referencePlanName || '-');
    if (o.mode === 'PercentOff') return 'ลด ' + Number(o.percentOff || 0) + '%';
    const parts = [];
    if (o.fixedMonthly != null) parts.push('เดือน ฿' + this.money(o.fixedMonthly));
    if (o.fixedQuarterly != null) parts.push('3 เดือน ฿' + this.money(o.fixedQuarterly));
    if (o.fixedSemiAnnual != null) parts.push('6 เดือน ฿' + this.money(o.fixedSemiAnnual));
    if (o.fixedAnnual != null) parts.push('ปี ฿' + this.money(o.fixedAnnual));
    return parts.join(' · ') || '-';
  },

  html(data, scope) {
    const e = AdminLayout.esc;
    const rows = (data.items || []);
    const live = rows.filter(o => o.live);
    const preview = (data.preview || []);
    const planOpts = (data.plans || []).filter(p => !p.isPermanentFree).map(p => `<option value="${e(p.id)}">${e(p.name)} (${e(p.plan)})</option>`).join('');
    return `
      <div style="display:flex;justify-content:space-between;align-items:center;gap:8px;flex-wrap:wrap">
        <div style="font-weight:700">★ ราคาพิเศษ${scope.accountSubscriptionId ? ' (License — ใช้กับทุกบริษัทใต้ License ที่ไม่มีราคาพิเศษของตัวเอง)' : ' (บริษัทนี้ — ชนะราคาพิเศษของ License)'}</div>
        <button type="button" class="admin-btn admin-btn-primary admin-btn-sm" data-po="new">+ ตั้งราคาพิเศษ</button>
      </div>
      <div style="font-size:12px;color:#64748b;margin:4px 0 8px">ใช้กับ ใบต่ออายุ · ส่วนต่างอัปเกรด (ทั้งสองฝั่ง) · ดูราคา · คำขอแพ็กเกจ · อนุมัติอัตโนมัติ — คูปองลดซ้อนจากราคาพิเศษ · รายการ/ใบที่ออกแล้วไม่ถูกแก้</div>
      ${live.length ? `<table class="admin-table" style="width:100%;font-size:12px"><thead><tr><th>แพ็กเกจ</th><th>โหมด</th><th>ค่า</th><th>ช่วงมีผล</th><th>ใช้กับ</th><th>เหตุผล</th><th></th></tr></thead><tbody>
        ${live.map(o => `<tr>
          <td><b>${e(o.planName || '-')}</b></td><td>${e(this.modeLabel(o.mode))}</td><td>${this.valueText(o)}</td>
          <td>${this.d(o.effectiveFromUtc)} → ${o.effectiveToUtc ? this.d(o.effectiveToUtc) : 'ตลอดไป'}</td>
          <td>${o.appliesToRenewals ? 'ต่ออายุ' : ''}${o.appliesToRenewals && o.appliesToUpgrades ? ' · ' : ''}${o.appliesToUpgrades ? 'อัปเกรด' : ''}</td>
          <td style="color:#475569">${e(o.reason || '')}<div style="color:#94a3b8">โดย ${e(o.approvedBy || '-')}</div></td>
          <td><button type="button" class="admin-btn admin-btn-outline admin-btn-sm" data-po="end" data-id="${e(o.id)}">ปิด</button></td>
        </tr>`).join('')}</tbody></table>`
        : '<div style="padding:8px;color:#94a3b8;font-size:12px">ไม่มีราคาพิเศษที่มีผล — คิดราคาตั้งของแพ็กเกจ</div>'}
      <details style="margin-top:8px"><summary style="cursor:pointer;font-size:12px;color:#475569">ราคาที่มีผลต่อรอบ (ตัวอย่างสด — ตัวตัดสินเดียวกับใบต่ออายุ)</summary>
        <table class="admin-table" style="width:100%;font-size:12px;margin-top:6px"><thead><tr><th>แพ็กเกจ</th>${['เดือน', '3 เดือน', '6 เดือน', 'ปี'].map(l => `<th>${l}</th>`).join('')}</tr></thead><tbody>
          ${preview.map(p => `<tr><td><b>${e(p.planName)}</b></td>${p.cycles.map(c => `<td>${c.applied
            ? `<span style="color:#166534;font-weight:700">฿${this.money(c.price)}</span> <span style="text-decoration:line-through;color:#94a3b8">฿${this.money(c.listPrice)}</span>`
            : `฿${this.money(c.listPrice)}`}</td>`).join('')}</tr>`).join('')}
        </tbody></table></details>
      ${rows.length > live.length ? `<details style="margin-top:6px"><summary style="cursor:pointer;font-size:12px;color:#94a3b8">ประวัติที่ปิด/หมดช่วงแล้ว (${rows.length - live.length})</summary>
        ${rows.filter(o => !o.live).map(o => `<div style="font-size:12px;color:#64748b;padding:3px 0">${e(o.planName || '-')} · ${e(this.modeLabel(o.mode))} · ${this.valueText(o)} · ${this.d(o.effectiveFromUtc)} → ${o.effectiveToUtc ? this.d(o.effectiveToUtc) : '∞'}${o.endedAtUtc ? ` · ปิด ${this.d(o.endedAtUtc)} โดย ${e(o.endedBy || '-')}: ${e(o.endReason || '')}` : ''}</div>`).join('')}
      </details>` : ''}
      <div data-po="form" style="display:none;margin-top:10px;padding:10px;border:1px solid #e2e8f0;border-radius:8px;background:#f8fafc">
        <div style="display:grid;grid-template-columns:repeat(auto-fit,minmax(180px,1fr));gap:8px">
          <label style="font-size:12px">แพ็กเกจที่ได้สิทธิ์<select class="admin-input" data-po-f="plan">${planOpts}</select></label>
          <label style="font-size:12px">โหมด<select class="admin-input" data-po-f="mode">
            <option value="PriceOfPlan">จ่ายราคาของแพ็กเกจอื่น</option><option value="PercentOff">ลด % จากราคาตั้ง</option><option value="FixedPerCycle">กำหนดราคาเอง/รอบ</option></select></label>
          <label style="font-size:12px" data-po-m="PriceOfPlan">แพ็กเกจอ้างอิง (จ่ายราคานี้)<select class="admin-input" data-po-f="ref">${planOpts}</select></label>
          <label style="font-size:12px;display:none" data-po-m="PercentOff">ลด %<input class="admin-input" type="number" min="0.01" max="100" step="0.01" data-po-f="pct"></label>
          <label style="font-size:12px;display:none" data-po-m="FixedPerCycle">รายเดือน<input class="admin-input" type="number" min="0" step="0.01" data-po-f="fm" placeholder="ว่าง = แม่แบบ"></label>
          <label style="font-size:12px;display:none" data-po-m="FixedPerCycle">ราย 3 เดือน<input class="admin-input" type="number" min="0" step="0.01" data-po-f="fq" placeholder="ว่าง = แม่แบบ"></label>
          <label style="font-size:12px;display:none" data-po-m="FixedPerCycle">ราย 6 เดือน<input class="admin-input" type="number" min="0" step="0.01" data-po-f="fs" placeholder="ว่าง = แม่แบบ"></label>
          <label style="font-size:12px;display:none" data-po-m="FixedPerCycle">รายปี<input class="admin-input" type="number" min="0" step="0.01" data-po-f="fa" placeholder="ว่าง = แม่แบบ"></label>
          <label style="font-size:12px">มีผลตั้งแต่<input class="admin-input" type="date" data-po-f="from"></label>
          <label style="font-size:12px">ถึง (ว่าง = ตลอดไป)<input class="admin-input" type="date" data-po-f="to"></label>
        </div>
        <div style="display:flex;gap:14px;margin-top:6px;font-size:12px">
          <label><input type="checkbox" data-po-f="ren" checked> ใช้กับใบต่ออายุ</label>
          <label><input type="checkbox" data-po-f="upg" checked> ใช้กับอัปเกรด/ส่วนต่าง</label>
        </div>
        <input class="admin-input" data-po-f="reason" maxlength="500" placeholder="เหตุผล (บังคับ · เก็บใน audit)" style="margin-top:6px">
        <div style="display:flex;gap:6px;margin-top:8px">
          <button type="button" class="admin-btn admin-btn-success admin-btn-sm" data-po="save">บันทึก</button>
          <button type="button" class="admin-btn admin-btn-outline admin-btn-sm" data-po="cancel">ยกเลิก</button>
        </div>
      </div>`;
  },

  wire(el, data, scope) {
    const form = el.querySelector('[data-po="form"]');
    const f = k => el.querySelector(`[data-po-f="${k}"]`);
    const showMode = () => {
      const m = f('mode').value;
      el.querySelectorAll('[data-po-m]').forEach(l => { l.style.display = l.dataset.poM === m ? '' : 'none'; });
    };
    el.querySelector('[data-po="new"]').addEventListener('click', () => { form.style.display = ''; showMode(); });
    el.querySelector('[data-po="cancel"]').addEventListener('click', () => { form.style.display = 'none'; });
    f('mode').addEventListener('change', showMode);
    el.querySelector('[data-po="save"]').addEventListener('click', async () => {
      const num = k => { const v = f(k).value; return v === '' ? null : Number(v); };
      const body = {
        companyId: scope.companyId || null, accountSubscriptionId: scope.accountSubscriptionId || null,
        planTemplateId: f('plan').value, mode: f('mode').value,
        referencePlanTemplateId: f('mode').value === 'PriceOfPlan' ? f('ref').value : null,
        percentOff: num('pct'), fixedMonthly: num('fm'), fixedQuarterly: num('fq'), fixedSemiAnnual: num('fs'), fixedAnnual: num('fa'),
        effectiveFromUtc: f('from').value ? f('from').value + 'T00:00:00+07:00' : null,
        effectiveToUtc: f('to').value ? f('to').value + 'T00:00:00+07:00' : null,
        appliesToRenewals: f('ren').checked, appliesToUpgrades: f('upg').checked,
        reason: f('reason').value.trim(),
      };
      if (body.reason.length < 3) { alert('ต้องระบุเหตุผล'); return; }
      try {
        const r = await AdminAPI.post('/price-overrides', body);
        if (!r.success) throw new Error(r.message);
        alert('✓ ' + r.message);
        this.mount(el, scope);
      } catch (e) { alert(e.message); }
    });
    el.querySelectorAll('[data-po="end"]').forEach(b => b.addEventListener('click', async () => {
      const reason = prompt('เหตุผลที่ปิดราคาพิเศษ (บังคับ · เก็บใน audit)', '') || '';
      if (reason.trim().length < 3) { alert('ต้องระบุเหตุผล'); return; }
      try {
        const r = await AdminAPI.post('/price-overrides/' + b.dataset.id + '/end', { reason });
        if (!r.success) throw new Error(r.message);
        alert('✓ ' + r.message);
        this.mount(el, scope);
      } catch (e) { alert(e.message); }
    }));
  },
};
