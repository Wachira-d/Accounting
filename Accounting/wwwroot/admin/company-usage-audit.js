// แท็บ "ตรวจสอบการใช้งาน" ในโมดัลบริษัท (/admin/customers.html)
// ข้อมูลทั้งหมดคำนวณที่เซิร์ฟเวอร์ (GET /api/admin/company-usage/{id}/audit) — หน้านี้แค่จัดวาง:
// ป้ายประเภท/สถานะ/ช่องทางมาจากเซิร์ฟเวอร์ (ห้ามมีตารางป้ายชุดที่สองในนี้) · ค่าที่เซิร์ฟเวอร์ไม่รู้ (null) = "ไม่มีข้อมูล" ไม่ใช่ 0
// ทุกข้อความจากเซิร์ฟเวอร์ผ่าน AdminLayout.esc ก่อนต่อเข้า HTML
const CompanyUsageAudit = {
  state: null,

  mount(rootId, companyId) {
    const root = document.getElementById(rootId);
    if (!root) return;
    if (this.state && this.state.companyId === companyId && this.state.rootId === rootId && root.dataset.loaded === '1') return;
    this.state = { rootId, companyId, data: null, preset: '12', from: '', to: '', pivotDim: 'type', pivotStatus: '', chartMetric: 'docs' };
    root.dataset.loaded = '1';
    root.innerHTML = this.controlsHtml() + '<div id="cuaBody"><p class="cua-muted" style="padding:20px;text-align:center">กำลังโหลด…</p></div>';
    this.load();
  },

  // ───────── helpers ─────────
  esc(v) { return AdminLayout.esc(v == null ? '' : String(v)); },
  n(v) { return v == null ? '–' : Number(v).toLocaleString('th-TH'); },
  money(v) { return v == null ? '–' : AdminLayout.money(v); },
  usd(v) { return v == null ? '–' : '$' + Number(v).toLocaleString('en-US', { minimumFractionDigits: 2, maximumFractionDigits: 4 }); },
  bytes(b) {
    if (b == null) return 'ไม่มีข้อมูล';
    if (b < 1024) return b + ' B';
    if (b < 1048576) return (b / 1024).toFixed(1) + ' KB';
    if (b < 1073741824) return (b / 1048576).toFixed(1) + ' MB';
    return (b / 1073741824).toFixed(2) + ' GB';
  },
  monthLabel(mk) {
    const [y, m] = String(mk).split('-').map(Number);
    if (!y || !m) return mk;
    return new Date(y, m - 1, 1).toLocaleDateString('th-TH', { month: 'short', year: '2-digit' });
  },
  ymNow(offset = 0) {
    const d = new Date(); d.setDate(1); d.setMonth(d.getMonth() + offset);
    return `${d.getFullYear()}-${String(d.getMonth() + 1).padStart(2, '0')}`;
  },
  // ค่าใช้ ÷ เพดาน → ป้ายสี (เพดาน ≥ 99999 = ไม่จำกัด)
  pctCell(used, max) {
    if (used == null) return '<span class="cua-muted">ไม่มีข้อมูล</span>';
    if (max == null || max <= 0 || max >= 99999) return `${this.n(used)}`;
    const p = Math.round(used / max * 100);
    const cls = p >= 100 ? 'cua-over' : p >= 80 ? 'cua-warn' : '';
    return `<span class="${cls}">${this.n(used)}</span><span class="cua-muted"> / ${this.n(max)} (${p}%)</span>`;
  },
  limitBar(label, used, max, isBytes) {
    if (used == null) {
      return `<div class="cua-lbar"><span class="cua-lbl">${this.esc(label)}</span><div class="cua-track"><div class="cua-fill" style="width:0"></div></div><span class="cua-lval cua-muted">ไม่มีข้อมูล</span></div>`;
    }
    const unlimited = max == null || max <= 0 || max >= 99999 && !isBytes;
    const pct = unlimited ? 0 : Math.min(used / max * 100, 100);
    const cls = pct >= 100 ? 'full' : pct >= 80 ? 'warn' : '';
    const val = isBytes
      ? `${this.bytes(used)} / ${max ? this.bytes(max) : '∞'}`
      : `${this.n(used)} / ${unlimited ? '∞' : this.n(max)}`;
    return `<div class="cua-lbar"><span class="cua-lbl">${this.esc(label)}</span><div class="cua-track"><div class="cua-fill ${cls}" style="width:${pct}%"></div></div><span class="cua-lval">${val}</span></div>`;
  },
  channelLabel(code) {
    const c = (this.state.data?.channels || []).find(x => x.channel === code);
    return c ? c.label : code;
  },
  statusLabel(code) {
    const s = (this.state.data?.statuses || []).find(x => x.status === code);
    return s ? s.label : code;
  },

  // ───────── controls ─────────
  controlsHtml() {
    return `
      <style>
        .cua-muted{color:#94a3b8}
        .cua-ctrl{display:flex;gap:8px;flex-wrap:wrap;align-items:flex-end;margin-bottom:12px}
        .cua-ctrl label{display:block;font-size:11px;font-weight:600;color:#64748b;margin-bottom:2px}
        .cua-ctrl select,.cua-ctrl input{padding:6px 8px;border:1px solid #d1d5db;border-radius:6px;font-size:13px}
        .cua-kpis{display:grid;grid-template-columns:repeat(auto-fit,minmax(150px,1fr));gap:10px;margin-bottom:14px}
        .cua-kpi{background:#f8fafc;border:1px solid #e2e8f0;border-radius:8px;padding:10px 12px}
        .cua-kpi .l{font-size:11px;color:#64748b;font-weight:600}
        .cua-kpi .v{font-size:19px;font-weight:800;color:#0f172a;margin-top:2px;line-height:1.2;font-variant-numeric:tabular-nums}
        .cua-kpi .s{font-size:11px;color:#94a3b8;margin-top:2px}
        .cua-sec{border:1px solid #e2e8f0;border-radius:8px;padding:12px 14px;margin-bottom:14px}
        .cua-sec h4{margin:0 0 10px;font-size:14px;color:#1e293b;display:flex;justify-content:space-between;align-items:center;gap:8px;flex-wrap:wrap}
        .cua-tbl{width:100%;border-collapse:collapse;font-size:12px}
        .cua-tbl th{background:#f8fafc;text-align:left;padding:6px 8px;border-bottom:2px solid #e2e8f0;white-space:nowrap;color:#334155;font-weight:700}
        .cua-tbl td{padding:6px 8px;border-bottom:1px solid #f1f5f9;white-space:nowrap}
        .cua-tbl .num{text-align:right;font-variant-numeric:tabular-nums}
        .cua-tbl tfoot td{font-weight:700;background:#f8fafc}
        .cua-tbl td.click{cursor:pointer;color:#4f46e5}
        .cua-tbl td.click:hover{background:#eef2ff}
        .cua-scroll{overflow-x:auto;-webkit-overflow-scrolling:touch}
        .cua-over{color:#dc2626;font-weight:700}
        .cua-warn{color:#d97706;font-weight:700}
        .cua-lbar{display:flex;align-items:center;gap:8px;margin-bottom:6px;font-size:12px}
        .cua-lbl{min-width:130px;color:#64748b}
        .cua-track{flex:1;height:6px;background:#e2e8f0;border-radius:3px;overflow:hidden}
        .cua-fill{height:100%;background:#4f46e5;border-radius:3px}
        .cua-fill.warn{background:#f59e0b}.cua-fill.full{background:#ef4444}
        .cua-lval{min-width:120px;text-align:right;font-weight:600}
        .cua-chart{display:flex;align-items:flex-end;gap:4px;height:180px;position:relative;padding-top:8px;border-bottom:1px solid #cbd5e1}
        .cua-col{flex:1;display:flex;flex-direction:column;justify-content:flex-end;align-items:stretch;min-width:14px;height:100%;cursor:pointer}
        .cua-col:hover{opacity:.8}
        .cua-seg{width:100%}
        .cua-xl{display:flex;gap:4px;font-size:10px;color:#64748b;margin-top:4px}
        .cua-xl span{flex:1;text-align:center;min-width:14px;overflow:hidden;white-space:nowrap}
        .cua-limit{position:absolute;left:0;right:0;border-top:2px dashed #ef4444;pointer-events:none}
        .cua-limit span{position:absolute;right:0;top:-16px;font-size:10px;color:#ef4444;background:#fff;padding:0 3px}
        .cua-legend{display:flex;gap:12px;flex-wrap:wrap;font-size:11px;color:#475569;margin-top:6px}
        .cua-legend i{display:inline-block;width:10px;height:10px;border-radius:2px;margin-right:4px;vertical-align:-1px}
        .cua-note{font-size:11px;color:#64748b;line-height:1.6;margin:0;padding-left:16px}
        @media(max-width:768px){.cua-lbl{min-width:90px}.cua-lval{min-width:90px}}
      </style>
      <div class="cua-ctrl">
        <div><label>ช่วงเวลา</label>
          <select id="cuaPreset" onchange="CompanyUsageAudit.onPreset(this.value)">
            <option value="3">3 เดือนล่าสุด</option>
            <option value="6">6 เดือนล่าสุด</option>
            <option value="12" selected>12 เดือนล่าสุด</option>
            <option value="24">24 เดือนล่าสุด</option>
            <option value="custom">กำหนดเอง</option>
          </select></div>
        <div id="cuaCustom" style="display:none;gap:8px">
          <div style="display:inline-block"><label>ตั้งแต่เดือน</label><input type="month" id="cuaFrom"></div>
          <div style="display:inline-block"><label>ถึงเดือน</label><input type="month" id="cuaTo"></div>
        </div>
        <button class="admin-btn admin-btn-primary admin-btn-sm" onclick="CompanyUsageAudit.load()">แสดง</button>
        <div style="flex:1"></div>
        <button class="admin-btn admin-btn-outline admin-btn-sm" onclick="CompanyUsageAudit.exportCsv('docs')">⬇ CSV เอกสาร</button>
        <button class="admin-btn admin-btn-outline admin-btn-sm" onclick="CompanyUsageAudit.exportCsv('monthly')">⬇ CSV รายเดือน</button>
      </div>`;
  },

  onPreset(v) {
    this.state.preset = v;
    const box = document.getElementById('cuaCustom');
    if (box) box.style.display = v === 'custom' ? 'inline-flex' : 'none';
    if (v === 'custom') {
      const f = document.getElementById('cuaFrom'), t = document.getElementById('cuaTo');
      if (f && !f.value) f.value = this.ymNow(-11);
      if (t && !t.value) t.value = this.ymNow(0);
    } else {
      this.load();
    }
  },

  async load() {
    const st = this.state;
    if (!st) return;
    let from = '', to = '';
    if (st.preset === 'custom') {
      from = document.getElementById('cuaFrom')?.value || '';
      to = document.getElementById('cuaTo')?.value || '';
      if (!from || !to) { AdminLayout.toast('กรุณาเลือกเดือนเริ่มและเดือนสิ้นสุด', 'error'); return; }
    } else {
      const n = parseInt(st.preset, 10) || 12;
      from = this.ymNow(-(n - 1)); to = this.ymNow(0);
    }
    const body = document.getElementById('cuaBody');
    if (body) body.innerHTML = '<p class="cua-muted" style="padding:20px;text-align:center">กำลังโหลด…</p>';
    try {
      const res = await AdminAPI.companyUsageAudit(st.companyId, from, to);
      st.data = res.data;
      this.render();
    } catch (e) {
      if (body) body.innerHTML = `<p style="padding:20px;text-align:center;color:#dc2626">โหลดไม่สำเร็จ: ${this.esc(e.message)}</p>`;
    }
  },

  // ───────── render ─────────
  render() {
    const d = this.state.data;
    const body = document.getElementById('cuaBody');
    if (!d || !body) return;
    const sum = (arr, k) => arr.reduce((a, x) => a + (Number(x[k]) || 0), 0);
    const m = d.monthly || [];
    const L = d.limits, C = d.current || {};
    const last = m[m.length - 1] || {};

    const kpis = `
      <div class="cua-kpis">
        <div class="cua-kpi"><div class="l">เอกสารที่สร้าง (ทั้งช่วง)</div><div class="v">${this.n(sum(m, 'documents'))}</div>
          <div class="s">อนุมัติ ${this.n(sum(m, 'approved'))} · ร่าง ${this.n(sum(m, 'draft'))} · ยกเลิก ${this.n(sum(m, 'voided'))}</div></div>
        <div class="cua-kpi"><div class="l">มูลค่าใบที่อนุมัติ</div><div class="v">${this.money(sum(m, 'approvedAmount'))}</div><div class="s">ไม่รวมร่าง/รอ/ยกเลิก/ปฏิเสธ</div></div>
        <div class="cua-kpi"><div class="l">นับโควตาเอกสาร (ประมาณ)</div><div class="v">${this.n(sum(m, 'documentsQuotaCounted'))}</div>
          <div class="s">เฉลี่ย ${this.n(Math.round(sum(m, 'documentsQuotaCounted') / Math.max(m.length, 1)))}/เดือน${L ? ' · เพดาน ' + this.n(L.maxDocumentsPerMonth) : ''}</div></div>
        <div class="cua-kpi"><div class="l">สแกน OCR (ครั้ง)</div><div class="v">${this.n(sum(m, 'ocrScans'))}</div><div class="s">สำเร็จ ${this.n(sum(m, 'ocrCompleted'))} ครั้ง · หน้า (มิเตอร์) ดูที่ "ใช้จริงเดือนนี้"</div></div>
        <div class="cua-kpi"><div class="l">เรียก AI (ยิงถึง provider)</div><div class="v">${this.n(sum(m, 'aiCallsPaid'))}</div>
          <div class="s">ทั้งหมด ${this.n(sum(m, 'aiCallsTotal'))} · ${this.usd(sum(m, 'aiCostUsd'))}</div></div>
        <div class="cua-kpi"><div class="l">สมุดรายวัน</div><div class="v">${this.n(sum(m, 'journalEntries'))}</div><div class="s">อีเมล ${this.n(sum(m, 'emailsSent'))} · e-Tax ${this.n(sum(m, 'etaxIssued'))}</div></div>
        <div class="cua-kpi"><div class="l">ผู้ใช้ปัจจุบัน</div><div class="v">${this.n(C.users)}</div><div class="s">${L ? 'เพดาน ' + this.n(L.maxUsers) : 'ไม่มีข้อมูลเพดาน'}</div></div>
        <div class="cua-kpi"><div class="l">ค่ามิเตอร์ส่วนเสริม</div><div class="v">${this.money(sum(m, 'meteredCharged'))}</div><div class="s">ทั้งช่วง (หลังหักโควตาฟรี)</div></div>
      </div>`;

    const current = `
      <div class="cua-sec">
        <h4><span>ใช้จริงเดือนนี้ เทียบเพดานแพ็กเกจ ${L ? '<span class="cua-muted" style="font-weight:400">(' + this.esc(L.planName) + (L.fromAccountPlan ? ' · เพดานรวมทุกบริษัทใต้ License' : '') + ')</span>' : ''}</span></h4>
        ${L ? '' : '<div class="cua-muted" style="font-size:12px;margin-bottom:6px">อ่านแพ็กเกจของบริษัทนี้ไม่ได้ — แสดงเฉพาะตัวเลขใช้จริง</div>'}
        ${this.limitBar('เอกสาร/เดือน', C.documentsThisMonth, L?.maxDocumentsPerMonth)}
        ${this.limitBar('สมุดรายวัน/เดือน', C.journalEntriesThisMonth, L?.maxJournalEntriesPerMonth)}
        ${this.limitBar('OCR หน้า/เดือน', C.ocrPagesThisMonth, L?.maxOcrPagesPerMonth)}
        ${this.limitBar('พื้นที่จัดเก็บ', C.storageUsed, L?.maxStorageBytes, true)}
        ${this.limitBar('ผู้ใช้', C.users, L?.maxUsers)}
      </div>`;

    body.innerHTML = kpis + current
      + `<div class="cua-sec"><h4><span>กราฟรายเดือน</span>
           <select id="cuaChartMetric" onchange="CompanyUsageAudit.state.chartMetric=this.value;CompanyUsageAudit.renderChart()" style="font-size:12px;padding:4px 6px;border:1px solid #d1d5db;border-radius:6px">
             <option value="docs">เอกสาร (แยกสถานะ)</option>
             <option value="quota">เอกสารที่นับโควตา เทียบเพดาน</option>
             <option value="je">สมุดรายวัน เทียบเพดาน</option>
             <option value="ocr">สแกน OCR (ครั้ง — ไม่เทียบเพดานหน้า)</option>
             <option value="ai">เรียก AI (local / provider)</option>
           </select></h4><div id="cuaChart"></div>
           <div class="cua-muted" style="font-size:11px;margin-top:4px">คลิกแท่งเพื่อดูรายการเอกสารของเดือนนั้น</div></div>`
      + `<div class="cua-sec"><h4><span>ตารางเอกสาร เดือน × <select id="cuaPivotDim" onchange="CompanyUsageAudit.state.pivotDim=this.value;CompanyUsageAudit.renderPivot()" style="font-size:12px;padding:3px 6px;border:1px solid #d1d5db;border-radius:6px">
             <option value="type">ประเภทเอกสาร</option><option value="channel">ช่องทางสร้าง</option><option value="status">สถานะ</option></select></span>
           <select id="cuaPivotStatus" onchange="CompanyUsageAudit.state.pivotStatus=this.value;CompanyUsageAudit.renderPivot()" style="font-size:12px;padding:3px 6px;border:1px solid #d1d5db;border-radius:6px">
             <option value="">ทุกสถานะ</option>${(d.statuses || []).map(s => `<option value="${this.esc(s.status)}">${this.esc(s.label)}</option>`).join('')}</select></h4>
           <div id="cuaPivot" class="cua-scroll"></div></div>`
      + `<div class="cua-sec"><h4><span>ประเภทเอกสาร × ช่องทางสร้าง (ทั้งช่วง)</span></h4><div id="cuaMatrix" class="cua-scroll"></div>
           <div id="cuaChannelNotes" style="margin-top:8px"></div></div>`
      + `<div class="cua-sec"><h4><span>โควตาและมิเตอร์รายเดือน</span></h4><div id="cuaMonthly" class="cua-scroll"></div></div>`
      + `<div id="cuaExtras"></div>`
      + `<div class="cua-sec" id="cuaDrillSec" style="display:none"><h4><span id="cuaDrillTitle">รายการเอกสาร</span>
           <button class="admin-btn admin-btn-outline admin-btn-sm" onclick="document.getElementById('cuaDrillSec').style.display='none'">ปิด</button></h4>
           <div id="cuaDrill" class="cua-scroll"></div></div>`
      + `<div class="cua-sec"><h4><span>หมายเหตุการนับ</span></h4><ul class="cua-note">${(d.notes || []).map(t => `<li>${this.esc(t)}</li>`).join('')}</ul></div>`;

    const sel = document.getElementById('cuaChartMetric'); if (sel) sel.value = this.state.chartMetric;
    const pd = document.getElementById('cuaPivotDim'); if (pd) pd.value = this.state.pivotDim;
    const ps = document.getElementById('cuaPivotStatus'); if (ps) ps.value = this.state.pivotStatus;
    this.renderChart();
    this.renderPivot();
    this.renderMatrix();
    this.renderMonthly();
    this.renderExtras();
  },

  renderChart() {
    const d = this.state.data, el = document.getElementById('cuaChart');
    if (!d || !el) return;
    const m = d.monthly || [], L = d.limits;
    const metric = this.state.chartMetric;
    // ชุดแท่ง: [{key,label,color}] + เพดาน (ถ้ามี)
    let series, limit = null;
    if (metric === 'docs') series = [
      { key: 'approved', label: this.statusLabel('Approved'), color: '#4f46e5' },
      { key: 'pending', label: this.statusLabel('Pending'), color: '#0ea5e9' },
      { key: 'draft', label: this.statusLabel('Draft'), color: '#cbd5e1' },
      { key: 'voided', label: this.statusLabel('Voided'), color: '#f87171' },
      { key: 'rejected', label: this.statusLabel('Rejected'), color: '#fbbf24' }];
    else if (metric === 'quota') { series = [{ key: 'documentsQuotaCounted', label: 'นับโควตา (ประมาณ)', color: '#4f46e5' }]; limit = L?.maxDocumentsPerMonth; }
    else if (metric === 'je') { series = [{ key: 'journalEntries', label: 'สมุดรายวัน', color: '#0d9488' }]; limit = L?.maxJournalEntriesPerMonth; }
    // W9 R-62: ครั้ง ≠ หน้า — ไม่วาดเส้นเพดานหน้าทับกราฟครั้ง
    else if (metric === 'ocr') { series = [{ key: 'ocrScans', label: 'สแกน (ครั้ง)', color: '#7c3aed' }]; limit = null; }
    else series = [
      { key: 'aiLocal', label: 'ตอบในระบบ (ไม่เสียเงิน)', color: '#94a3b8' },
      { key: 'aiCallsPaid', label: 'ยิงถึง provider', color: '#ea580c' }];
    const rows = m.map(r => ({ ...r, aiLocal: Math.max((r.aiCallsTotal || 0) - (r.aiCallsPaid || 0), 0) }));
    if (limit != null && (limit <= 0 || limit >= 99999)) limit = null;
    const totals = rows.map(r => series.reduce((a, s) => a + (Number(r[s.key]) || 0), 0));
    const max = Math.max(1, ...totals, limit || 0);
    const cols = rows.map((r, i) => {
      const segs = series.slice().reverse().map(s => {
        const v = Number(r[s.key]) || 0;
        return v > 0 ? `<div class="cua-seg" style="height:${v / max * 100}%;background:${s.color}" title="${this.esc(s.label)}: ${this.n(v)}"></div>` : '';
      }).join('');
      return `<div class="cua-col" title="${this.esc(this.monthLabel(r.month))}: ${this.n(totals[i])}" onclick="CompanyUsageAudit.drill({month:'${this.esc(r.month)}'})">${segs}</div>`;
    }).join('');
    const limitLine = limit != null ? `<div class="cua-limit" style="bottom:${limit / max * 100}%"><span>เพดาน ${this.n(limit)}</span></div>` : '';
    el.innerHTML = `<div class="cua-chart">${limitLine}${cols}</div>
      <div class="cua-xl">${rows.map(r => `<span>${this.esc(this.monthLabel(r.month))}</span>`).join('')}</div>
      <div class="cua-legend">${series.map(s => `<span><i style="background:${s.color}"></i>${this.esc(s.label)}</span>`).join('')}
        ${limit != null ? '<span><i style="background:#ef4444"></i>เพดานแพ็กเกจปัจจุบัน</span>' : ''}</div>`;
  },

  // แถวของ pivot ตามมิติที่เลือก
  dimOf(cell, dim) {
    if (dim === 'channel') return { key: cell.channel, label: this.channelLabel(cell.channel) };
    if (dim === 'status') return { key: cell.status, label: cell.statusLabel };
    return { key: cell.type, label: cell.typeLabel };
  },

  renderPivot() {
    const d = this.state.data, el = document.getElementById('cuaPivot');
    if (!d || !el) return;
    const dim = this.state.pivotDim, stf = this.state.pivotStatus;
    const cells = (d.docCells || []).filter(c => !stf || c.status === stf);
    const rows = new Map();
    cells.forEach(c => {
      const k = this.dimOf(c, dim);
      if (!rows.has(k.key)) rows.set(k.key, { label: k.label, byMonth: {}, total: 0 });
      const r = rows.get(k.key);
      r.byMonth[c.month] = (r.byMonth[c.month] || 0) + c.count;
      r.total += c.count;
    });
    if (!rows.size) { el.innerHTML = '<p class="cua-muted" style="text-align:center;padding:12px">ไม่มีเอกสารในช่วงนี้</p>'; return; }
    const list = [...rows.entries()].sort((a, b) => b[1].total - a[1].total);
    const months = d.months || [];
    const colTot = months.map(mk => list.reduce((a, [, r]) => a + (r.byMonth[mk] || 0), 0));
    const dimParam = dim === 'channel' ? 'channel' : dim === 'status' ? 'status' : 'type';
    const drillArg = (key, mk) => {
      const f = { [dimParam]: key };
      if (mk) f.month = mk;
      if (stf && dimParam !== 'status') f.status = stf;
      return this.esc(JSON.stringify(f));
    };
    el.innerHTML = `<table class="cua-tbl"><thead><tr><th>${dim === 'channel' ? 'ช่องทาง' : dim === 'status' ? 'สถานะ' : 'ประเภท'}</th>
        ${months.map(mk => `<th class="num">${this.esc(this.monthLabel(mk))}</th>`).join('')}<th class="num">รวม</th></tr></thead>
      <tbody>${list.map(([key, r]) => `<tr><td>${this.esc(r.label)}</td>
        ${months.map(mk => r.byMonth[mk]
          ? `<td class="num click" onclick='CompanyUsageAudit.drill(${drillArg(key, mk)})'>${this.n(r.byMonth[mk])}</td>`
          : '<td class="num cua-muted">·</td>').join('')}
        <td class="num click" onclick='CompanyUsageAudit.drill(${drillArg(key, null)})'><strong>${this.n(r.total)}</strong></td></tr>`).join('')}</tbody>
      <tfoot><tr><td>รวม</td>${colTot.map(v => `<td class="num">${this.n(v)}</td>`).join('')}<td class="num">${this.n(colTot.reduce((a, b) => a + b, 0))}</td></tr></tfoot></table>`;
  },

  renderMatrix() {
    const d = this.state.data, el = document.getElementById('cuaMatrix');
    if (!d || !el) return;
    const cells = d.docCells || [];
    const types = new Map(), chans = new Map();
    cells.forEach(c => {
      if (!types.has(c.type)) types.set(c.type, { label: c.typeLabel, total: 0, by: {} });
      const t = types.get(c.type);
      t.total += c.count; t.by[c.channel] = (t.by[c.channel] || 0) + c.count;
      chans.set(c.channel, (chans.get(c.channel) || 0) + c.count);
    });
    if (!types.size) { el.innerHTML = '<p class="cua-muted" style="text-align:center;padding:12px">ไม่มีเอกสารในช่วงนี้</p>'; document.getElementById('cuaChannelNotes').innerHTML = ''; return; }
    const chanList = [...chans.entries()].sort((a, b) => b[1] - a[1]).map(([k]) => k);
    const typeList = [...types.entries()].sort((a, b) => b[1].total - a[1].total);
    const arg = (f) => this.esc(JSON.stringify(f));
    el.innerHTML = `<table class="cua-tbl"><thead><tr><th>ประเภท</th>${chanList.map(ch => `<th class="num">${this.esc(this.channelLabel(ch))}</th>`).join('')}<th class="num">รวม</th></tr></thead>
      <tbody>${typeList.map(([type, t]) => `<tr><td>${this.esc(t.label)}</td>
        ${chanList.map(ch => t.by[ch] ? `<td class="num click" onclick='CompanyUsageAudit.drill(${arg({ type, channel: ch })})'>${this.n(t.by[ch])}</td>` : '<td class="num cua-muted">·</td>').join('')}
        <td class="num"><strong>${this.n(t.total)}</strong></td></tr>`).join('')}</tbody>
      <tfoot><tr><td>รวม</td>${chanList.map(ch => `<td class="num">${this.n(chans.get(ch))}</td>`).join('')}<td class="num">${this.n(cells.reduce((a, c) => a + c.count, 0))}</td></tr></tfoot></table>`;
    const notes = (d.channels || []).filter(c => c.caveat && chans.has(c.channel));
    document.getElementById('cuaChannelNotes').innerHTML = notes.length
      ? `<ul class="cua-note">${notes.map(c => `<li><strong>${this.esc(c.label)}</strong> — ${this.esc(c.caveat)}</li>`).join('')}</ul>` : '';
  },

  renderMonthly() {
    const d = this.state.data, el = document.getElementById('cuaMonthly');
    if (!d || !el) return;
    const L = d.limits;
    el.innerHTML = `<table class="cua-tbl"><thead><tr>
        <th>เดือน</th><th class="num">เอกสาร</th><th class="num">นับโควตา</th><th class="num">ร่าง</th><th class="num">อนุมัติ</th><th class="num">ยกเลิก</th>
        <th class="num">สมุดรายวัน</th><th class="num">OCR สแกน (ครั้ง)</th><th class="num">OCR หน้า (มิเตอร์)</th><th class="num">AI ทั้งหมด</th><th class="num">AI provider</th><th class="num">ค่า AI</th>
        <th class="num">อีเมล</th><th class="num">e-Tax</th><th class="num">ไฟล์ใหม่</th><th class="num">พื้นที่สะสม</th><th class="num">ผู้ใช้สะสม</th><th class="num">ค่ามิเตอร์</th></tr></thead>
      <tbody>${(d.monthly || []).map(r => `<tr>
        <td class="click" onclick="CompanyUsageAudit.drill({month:'${this.esc(r.month)}'})">${this.esc(this.monthLabel(r.month))}</td>
        <td class="num">${this.n(r.documents)}</td>
        <td class="num">${this.pctCell(r.documentsQuotaCounted, L?.maxDocumentsPerMonth)}</td>
        <td class="num">${this.n(r.draft)}</td><td class="num">${this.n(r.approved)}</td><td class="num">${this.n(r.voided)}</td>
        <td class="num">${this.pctCell(r.journalEntries, L?.maxJournalEntriesPerMonth)}</td>
        <td class="num">${this.n(r.ocrScans)}</td>
        <td class="num">${r.ocrPagesMetered == null ? '<span class="cua-muted" title="ไม่มีประวัติรายเดือน — มิเตอร์หน้ามีเฉพาะเดือนปัจจุบัน">—</span>' : this.pctCell(r.ocrPagesMetered, L?.maxOcrPagesPerMonth)}</td>
        <td class="num">${this.n(r.aiCallsTotal)}</td><td class="num">${this.n(r.aiCallsPaid)}</td><td class="num">${this.usd(r.aiCostUsd)}</td>
        <td class="num">${this.n(r.emailsSent)}</td><td class="num">${this.n(r.etaxIssued)}</td>
        <td class="num">${this.n(r.filesAdded)} · ${this.bytes(r.storageAddedBytes)}</td>
        <td class="num">${this.bytes(r.storageCumulativeBytes)}</td>
        <td class="num">${this.pctCell(r.usersCumulative, L?.maxUsers)}</td>
        <td class="num">${this.money(r.meteredCharged)}</td></tr>`).join('')}</tbody></table>`;
  },

  renderExtras() {
    const d = this.state.data, el = document.getElementById('cuaExtras');
    if (!d || !el) return;
    let html = '';
    const met = d.metered || [];
    if (met.length) {
      html += `<div class="cua-sec"><h4><span>มิเตอร์ส่วนเสริม/ค่าใช้จ่ายตามการใช้งาน</span></h4><div class="cua-scroll"><table class="cua-tbl">
        <thead><tr><th>เดือน</th><th>รายการ</th><th class="num">จำนวน</th><th class="num">ในโควตาฟรี</th><th class="num">คิดเงิน</th></tr></thead>
        <tbody>${met.map(x => `<tr><td>${this.esc(this.monthLabel(x.month))}</td><td>${this.esc(x.featureName)} <span class="cua-muted">${this.esc(x.featureCode)}</span></td>
          <td class="num">${this.n(x.quantity)} ${this.esc(x.unitLabel || '')}</td><td class="num">${this.n(x.freeQuantity)}</td><td class="num">${this.money(x.charged)}</td></tr>`).join('')}</tbody></table></div></div>`;
    }
    const ai = d.aiByFeature || [];
    if (ai.length) {
      html += `<div class="cua-sec"><h4><span>AI แยกฟีเจอร์ (ทั้งช่วง · สูงสุด 20)</span></h4><div class="cua-scroll"><table class="cua-tbl">
        <thead><tr><th>ฟีเจอร์</th><th class="num">เรียกทั้งหมด</th><th class="num">ยิงถึง provider</th><th class="num">ค่าใช้จ่าย</th></tr></thead>
        <tbody>${ai.map(x => `<tr><td>${this.esc(x.featureKey)}</td><td class="num">${this.n(x.callsTotal)}</td><td class="num">${this.n(x.callsAi)}</td><td class="num">${this.usd(x.costUsd)}</td></tr>`).join('')}</tbody></table></div></div>`;
    }
    el.innerHTML = html;
  },

  // ───────── drill-down ─────────
  async drill(filter) {
    const st = this.state, d = st.data;
    const sec = document.getElementById('cuaDrillSec'), el = document.getElementById('cuaDrill'), title = document.getElementById('cuaDrillTitle');
    if (!sec || !el || !d) return;
    const f = { ...filter };
    if (!f.month) { f.fromMonth = d.fromMonth; f.toMonth = d.toMonth; }
    const parts = [];
    if (f.month) parts.push(this.monthLabel(f.month)); else parts.push(`${this.monthLabel(d.fromMonth)} – ${this.monthLabel(d.toMonth)}`);
    if (f.type) { const c = (d.docCells || []).find(x => x.type === f.type); parts.push(c ? c.typeLabel : f.type); }
    if (f.status) parts.push(this.statusLabel(f.status));
    if (f.channel) parts.push(this.channelLabel(f.channel));
    title.textContent = 'รายการเอกสารล่าสุด · ' + parts.join(' · ');
    sec.style.display = '';
    el.innerHTML = '<p class="cua-muted" style="padding:12px;text-align:center">กำลังโหลด…</p>';
    sec.scrollIntoView({ behavior: 'smooth', block: 'nearest' });
    try {
      const res = await AdminAPI.companyUsageAuditDocuments(st.companyId, { ...f, take: 100 });
      const r = res.data || {};
      const items = r.items || [];
      if (!items.length) { el.innerHTML = '<p class="cua-muted" style="padding:12px;text-align:center">ไม่พบเอกสาร</p>'; return; }
      el.innerHTML = `<table class="cua-tbl"><thead><tr><th>เลขที่</th><th>ประเภท</th><th>สถานะ</th><th>ช่องทาง</th><th>วันที่เอกสาร</th><th>บันทึกเมื่อ</th><th class="num">ยอดรวม</th></tr></thead>
        <tbody>${items.map(x => `<tr><td>${this.esc(String(x.documentNumber || '').startsWith('DRAFT-') ? '(ร่าง — ยังไม่มีเลข)' : x.documentNumber)}</td>
          <td>${this.esc(x.typeLabel)}</td><td>${this.esc(x.statusLabel)}</td><td>${this.esc(x.channelLabel)}</td>
          <td>${AdminLayout.date(x.documentDate)}</td><td>${AdminLayout.datetime(x.createdAt)}</td><td class="num">${this.money(x.totalAmount)}</td></tr>`).join('')}</tbody></table>
        <div class="cua-muted" style="font-size:11px;margin-top:6px">แสดง ${this.n(items.length)} ใบล่าสุด${r.truncated ? ` · กรองช่องทางจาก ${this.n(r.scanned)} ใบล่าสุดเท่านั้น (ใบเก่ากว่านี้ไม่ได้ตรวจ — แคบช่วงเดือนเพื่อดูครบ)` : ''}</div>`;
    } catch (e) {
      el.innerHTML = `<p style="padding:12px;text-align:center;color:#dc2626">โหลดไม่สำเร็จ: ${this.esc(e.message)}</p>`;
    }
  },

  // ───────── CSV ─────────
  exportCsv(kind) {
    const d = this.state?.data;
    if (!d) { AdminLayout.toast('ยังไม่มีข้อมูล', 'error'); return; }
    const q = v => {
      const s = v == null ? '' : String(v);
      // กันสูตร Excel (CSV injection) + escape เครื่องหมายคำพูด
      const safe = /^[=+\-@\t\r]/.test(s) && isNaN(Number(s)) ? "'" + s : s;
      return '"' + safe.replace(/"/g, '""') + '"';
    };
    let header, rows;
    if (kind === 'docs') {
      header = ['เดือน', 'ประเภท', 'รหัสประเภท', 'สถานะ', 'ช่องทาง', 'จำนวน', 'มูลค่า(อนุมัติแล้ว)', 'นับโควตา'];
      rows = (d.docCells || []).map(c => [c.month, c.typeLabel, c.type, c.statusLabel, this.channelLabel(c.channel), c.count, c.amount, c.quotaCount]);
    } else {
      header = ['เดือน', 'เอกสาร', 'นับโควตา', 'ร่าง', 'รออนุมัติ', 'อนุมัติ', 'ยกเลิก', 'ถูกปฏิเสธ', 'มูลค่าอนุมัติ', 'สมุดรายวัน', 'OCR สแกน (ครั้ง)', 'OCR สำเร็จ (ครั้ง)', 'OCR หน้า (มิเตอร์ · เดือนปัจจุบันเท่านั้น)',
        'AI ทั้งหมด', 'AI provider', 'ค่า AI (USD)', 'อีเมล', 'e-Tax', 'ไฟล์ใหม่', 'ไบต์ใหม่', 'ไบต์สะสม', 'ผู้ใช้สะสม', 'ค่ามิเตอร์ (บาท)'];
      rows = (d.monthly || []).map(r => [r.month, r.documents, r.documentsQuotaCounted, r.draft, r.pending, r.approved, r.voided, r.rejected, r.approvedAmount,
        r.journalEntries, r.ocrScans, r.ocrCompleted, r.ocrPagesMetered ?? '', r.aiCallsTotal, r.aiCallsPaid, r.aiCostUsd, r.emailsSent, r.etaxIssued,
        r.filesAdded, r.storageAddedBytes, r.storageCumulativeBytes, r.usersCumulative, r.meteredCharged]);
    }
    const csv = '﻿' + [header, ...rows].map(r => r.map(q).join(',')).join('\r\n');
    const blob = new Blob([csv], { type: 'text/csv;charset=utf-8' });
    const url = URL.createObjectURL(blob);
    const a = document.createElement('a');
    const safeName = String(d.company?.name || 'company').replace(/[\\/:*?"<>|\s]+/g, '_').slice(0, 60);
    a.href = url; a.download = `usage-${kind}-${safeName}-${d.fromMonth}_${d.toMonth}.csv`;
    document.body.appendChild(a); a.click(); a.remove(); URL.revokeObjectURL(url);
  },
};
