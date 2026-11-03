/* รอบ 230 (erp-review/2026-11-02/CMS_SETTINGS_UX_AUDIT.md §6 ค้างข้อ (4) · แพ็ก F) — ค่าการจอง "ระดับเว็บ" ย้ายจาก cms-edit › บริการจอง เข้าศูนย์ "ตั้งค่าเว็บ › การจอง / บริการ"
 * ที่เดียวของ: การจัดสรรผู้ให้บริการ (สวิตช์ + วิธีเลือกคนให้อัตโนมัติ — อยู่ข้าง "ลูกค้าเลือกผู้ให้บริการได้ไหม") · เวลาเปิดร้าน & วันหยุดของเว็บ ·
 *   การรับจอง (ระยะเวลา/เวลาเตรียม/เวลาเก็บล้าง/จองล่วงหน้า) · เตือนก่อนนัด & คิวสำรอง · สมาชิก · ข้อมูลที่ลูกค้ากรอก · เอกสาร (ขั้นสูง ผู้ทำบัญชี) ·
 *   ค่าตอบแทนผู้ให้บริการ (กติกา — ย้ายจากท้ายรายงาน cms-booking-providers · สิทธิ์เดิมของเซิร์ฟเวอร์)
 * - GET/PUT …/booking/settings (null = คงค่าเดิม ⇒ แต่ละการ์ดส่งเฉพาะช่องของตัวเอง — บันทึกการ์ดหนึ่งไม่ทับค่าค้างของการ์ดอื่น)
 * - ตัวเลือก/ป้าย/ตาราง/ข้อความทั้งหมดมาจากเซิร์ฟเวอร์ (BookingProviderAllocation · BookingFormFields · BookingDocumentFlow · BookingInvoiceTimingPolicy ·
 *   BookingReminderPlan · BookingAccessRule · InstructorPayRule) — ไฟล์นี้แสดง/ส่งค่าอย่างเดียว
 * - ใช้ที่: cms-site-settings (กลุ่ม booking — Page เรียก BookingSiteAdmin.mount ต่อท้าย) · cms-sites wizard ขั้น 5 (BookingSiteAdmin.mountBasics) */
(function () {
  const esc = (v) => (window.Layout && Layout.esc ? Layout.esc(v) : String(v == null ? '' : v).replace(/[&<>"']/g, c => ({ '&': '&amp;', '<': '&lt;', '>': '&gt;', '"': '&quot;', "'": '&#39;' }[c])));
  const $ = (id) => document.getElementById(id);
  const num = (id, fallback) => { const el = $(id); const raw = el ? el.value : ''; if (raw === '') return fallback; const n = Number(raw); return Number.isFinite(n) ? n : fallback; };
  const DAY_KEYS = ['Sunday', 'Monday', 'Tuesday', 'Wednesday', 'Thursday', 'Friday', 'Saturday'];
  // ชื่อวันจากปฏิทินของเบราว์เซอร์ (2026-11-01 = วันอาทิตย์) — ไม่เก็บตารางชื่อวันซ้ำในไฟล์
  const dayName = (d) => new Date(Date.UTC(2026, 10, 1 + d)).toLocaleDateString('th-TH', { weekday: 'long', timeZone: 'UTC' });
  const card = (key, title, body, hint) => `<div class="sg-card" id="bsa_${key}"><h3>${title}</h3>${hint ? `<div class="sg-hint" style="margin-bottom:8px">${hint}</div>` : ''}<div id="bsa_${key}_body">${body}</div></div>`;
  const watch = (key) => { if (window.CmsDirtyGuard && $('bsa_' + key)) CmsDirtyGuard.watch($('bsa_' + key)); };
  const clean = (key) => { if (window.CmsDirtyGuard && $('bsa_' + key)) CmsDirtyGuard.reset($('bsa_' + key)); };
  const touched = (key) => { if (window.CmsDirtyGuard && $('bsa_' + key)) CmsDirtyGuard.markDirty($('bsa_' + key)); };
  const dayIndex = (d) => typeof d === 'number' ? d : Math.max(0, DAY_KEYS.indexOf(d));

  const A = {
    _siteId: null, _ctx: null, _d: null, _services: [], _sched: { weekly: [], exceptions: [] }, _cf: [], _cfLoaded: false, _provDirty: false,

    async _load() {
      const api = Layout.api();
      this._d = (await api.cmsGetBookingSiteSettings(this._siteId)).data || {};
      try { this._services = (await api.cmsListAllBookingServices(this._siteId)).data || []; } catch (_) { this._services = []; }
    },

    /** ศูนย์ตั้งค่าเว็บ › การจอง / บริการ — ต่อท้ายการ์ด "ลูกค้าเลือกผู้ให้บริการได้ไหม" ของหน้า */
    async mount(el, ctx, isCurrent) {
      if (!el) return;
      this._siteId = ctx.siteId; this._ctx = ctx;
      try { await this._load(); }
      catch (e) { if (typeof isCurrent === 'function' && !isCurrent()) return; el.insertAdjacentHTML('beforeend', `<div class="sg-card" style="color:#b91c1c">โหลดค่าการจองของเว็บไม่สำเร็จ: ${esc(e.message)}</div>`); return; }
      // ฝ่ายค้านรอบ 230 N1: ระหว่างโหลดมีการวาดกลุ่มใหม่ (สลับกลุ่ม/สิทธิ์มาถึง) ⇒ ตัวเก่าไม่ต่อการ์ดซ้ำ
      if (typeof isCurrent === 'function' && !isCurrent()) return;
      if (el.querySelector && el.querySelector('#bsa_providers')) return;
      const mods = (ctx.site && Array.isArray(ctx.site.modules)) ? ctx.site.modules : [];
      el.insertAdjacentHTML('beforeend', [
        card('providers', '👥 ช่าง / พนักงาน / ห้องที่ให้บริการ', this.providersHtml(), 'เปิดแล้ว: ลูกค้าเลือกคนได้ตามค่าด้านบน · หน้าทำงานเลือกคนที่ว่างตอนเริ่มงาน · มีรายงานงานต่อคน — รายชื่อผู้ให้บริการแก้ที่ หน้าเว็บ › บริการจอง › ผู้ให้บริการ'),
        card('hours', '🕘 เวลาเปิดร้าน & วันหยุด', this.hoursHtml(mods), 'ใช้กับทุกบริการที่ไม่ได้ตั้งเวลาเอง · วันหยุด/วันปิดของเว็บใช้กับวันรับของสินค้าตามสั่งด้วย (ชุดเดียวต่อเว็บ)'),
        card('rules', '📅 การรับจอง (ค่าเริ่มต้นของเว็บ)', this.rulesHtml(), 'บริการที่ติ๊ก "เหมือนทั้งเว็บ" ใช้ค่าเหล่านี้ · บริการอื่นตั้งเฉพาะบริการนั้น'),
        card('remind', '🔔 เตือนก่อนนัด & คิวสำรอง', this.remindHtml(), 'ส่งทางอีเมลหรือ LINE · ระบบประทับ "ส่งแล้ว" เฉพาะเมื่อส่งถึงจริง · ส่งไม่ถึงเห็นเหตุในหน้าการจอง'),
        card('member', '👤 สมาชิก', this.memberHtml(), ''),
        card('form', '📝 ข้อมูลที่ลูกค้าต้องกรอกตอนจอง', this.formHtml(), 'ชื่อ-นามสกุลบังคับเสมอ · ซ่อนทั้งเบอร์และอีเมลพร้อมกันไม่ได้ · พนักงานจองแทนลูกค้าไม่ผ่านด่านนี้'),
        card('docs', '🧾 เอกสารที่ระบบออกให้ (ขั้นสูง — สำหรับผู้ทำบัญชี)', this.docsHtml(), 'ค่าเริ่มต้นถูกต้องตามกฎหมายอยู่แล้ว — เจ้าของร้านไม่ต้องแตะ'),
        card('pay', '💰 ค่าตอบแทนผู้ให้บริการ (กติกา)', '<div class="sg-hint">กำลังโหลด…</div>', 'ผูกผู้ให้บริการกับพนักงาน + กติกา (ต่อคลาส · ต่อหัว · % รายได้ก่อน VAT) — ยอดที่คิดได้ดูที่ รายงาน "สรุปผู้ให้บริการ" · ใช้เป็นฐานค่าคอมมิชชัน "ค่าตอบแทนผู้สอน"'),
      ].join(''));
      this.afterRender();
      ['providers', 'hours', 'rules', 'remind', 'member', 'form', 'docs', 'pay'].forEach(watch);
      await this.loadPay();
    },

    /** wizard สร้างเว็บ ขั้น 5 (เว็บจอง): เวลาเปิดร้าน + การรับจอง — ตัวแก้/ตัวบันทึกชุดเดียวกับศูนย์ตั้งค่าเว็บ */
    async mountBasics(el, ctx) {
      if (!el) return;
      this._siteId = ctx.siteId; this._ctx = ctx;
      try { await this._load(); }
      catch (e) { el.innerHTML = `<div class="sg-card" style="color:#b91c1c">โหลดค่าการจองไม่สำเร็จ: ${esc(e.message)}</div>`; return; }
      el.innerHTML = card('hours', '🕘 เวลาเปิดร้าน & วันหยุด', this.hoursHtml([]), 'ตั้งครั้งเดียวใช้กับทุกบริการ — แก้ภายหลังได้ที่ ตั้งค่าเว็บ › การจอง / บริการ')
        + card('rules', '📅 การรับจอง', this.rulesHtml(), '');
      this.afterRender(); watch('hours'); watch('rules');
    },

    /** รายงานผู้ให้บริการ: ผู้ที่เข้าศูนย์ตั้งค่าเว็บไม่ได้ (ไม่มีเมนู cms-sites) แต่มีสิทธิ์ HR — การ์ดกติกาค่าตอบแทนตัวเดียวกัน (ทางไปต่อ ไม่ใช่สำเนา) */
    async mountPay(el, ctx) {
      if (!el) return;
      this._siteId = ctx.siteId;
      el.innerHTML = card('pay', '💰 กติกาค่าตอบแทนผู้ให้บริการ', '<div class="sg-hint">กำลังโหลด…</div>', 'ปกติแก้ที่ ตั้งค่าเว็บ › การจอง / บริการ — แสดงที่นี่เพราะบัญชีของคุณเข้าหน้านั้นไม่ได้');
      watch('pay');
      await this.loadPay();
    },

    afterRender() {
      this.schedRender();
      this.cfRender();
      this.strategyHint();
      if (window.CmsDuration) { CmsDuration.attach('bsaHold', 'min'); CmsDuration.sync('bsaHold'); }
    },

    // ── ผู้ให้บริการ (สวิตช์ + วิธีเลือกคนให้อัตโนมัติ) ──
    providersHtml() {
      const d = this._d;
      const opts = (d.providerStrategyOptions || []).map(o => `<option value="${esc(o.value)}"${o.value === (d.providerRecommendStrategy || 'LeastLoadedDay') ? ' selected' : ''}>${esc(o.label)}</option>`).join('');
      return `<label style="font-size:14px;font-weight:600;display:flex;gap:8px;align-items:center"><input type="checkbox" id="bsaProvOn"${d.providerAllocationEnabled === true ? ' checked' : ''} onchange="BookingSiteAdmin._provDirty=true">
          ใช้ช่าง/พนักงาน/ห้องที่ให้บริการ (นับคิวต่อคน)</label>
        <div class="sg-hint" style="margin:2px 0 8px">ปิด = ไม่ต้องระบุคน (นับแค่จำนวนคิวของบริการ) · ค่ารายบริการยังเก็บไว้ เปิดกลับได้ค่าเดิม · ผู้ให้บริการที่ใช้งาน ${Number(d.activeProviderCount || 0)} คน</div>
        ${opts ? `<div class="form-group"><label class="form-label" for="bsaStrategy">วิธีเลือกคนให้อัตโนมัติ</label>
          <select class="form-input" id="bsaStrategy" onchange="BookingSiteAdmin.strategyHint()">${opts}</select><div class="sg-hint" id="bsaStrategyHint"></div></div>` : ''}
        <div style="display:flex;gap:8px;flex-wrap:wrap"><button type="button" class="btn btn-primary btn-sm" onclick="BookingSiteAdmin.saveProviders()">บันทึก</button>
          <a class="btn btn-secondary btn-sm" href="/pages/cms-edit.html?siteId=${encodeURIComponent(this._siteId)}&tab=booking#bkSecProviders">รายชื่อผู้ให้บริการ →</a></div>`;
    },
    strategyHint() {
      const v = ($('bsaStrategy') || {}).value; const el = $('bsaStrategyHint'); if (!el) return;
      const o = ((this._d || {}).providerStrategyOptions || []).find(x => x.value === v);
      el.textContent = o ? o.description : '';
    },
    async saveProviders() {
      const body = {};
      // ส่งสวิตช์เฉพาะเมื่อเจ้าของกดจริง (ฝ่ายค้านรอบ 215 S1 — ค่าที่อนุมานได้ห้ามถูกประทับเป็นค่าที่ตั้งเอง)
      if (this._provDirty) body.providerAllocationEnabled = $('bsaProvOn').checked;
      if ($('bsaStrategy')) body.providerRecommendStrategy = $('bsaStrategy').value;
      await this._save('providers', body, () => { this._provDirty = false; $('bsa_providers_body').innerHTML = this.providersHtml(); this.strategyHint(); });
    },

    // ── เวลาเปิดร้าน & วันหยุด (ตัวแก้ตาราง — รูปเดียวกับ BookingScheduleDto) ──
    hoursHtml(mods) {
      const sch = this._d.schedule || {};
      this._sched = {
        weekly: (sch.weekly || []).map(w => ({ day: dayIndex(w.dayOfWeek), start: w.start, end: w.end, blocked: !!w.isBlocked, capacity: w.capacity ?? null })),
        exceptions: (sch.exceptions || []).map(x => ({ date: x.date, kind: x.closed ? ((x.start || x.end) ? 'partial' : 'closed') : 'open', start: x.start || '', end: x.end || '' })),
      };
      const mto = (mods || []).includes('orders')
        ? `<div class="sg-hint" style="margin-top:8px">ร้านมีสินค้าตามสั่งด้วย: วันปิดประจำสัปดาห์ของการผลิต/รับของ · ตัดรอบ ตั้งที่ <a href="/pages/cms-site-settings.html?siteId=${encodeURIComponent(this._siteId)}&group=mto" style="color:var(--primary)">สินค้าตามสั่ง / พรีออเดอร์ →</a></div>` : '';
      return `<div id="bsaWeekly"></div>
        <button type="button" class="btn btn-secondary btn-sm" style="margin:6px 0" onclick="BookingSiteAdmin.copyMonday()">ใช้เวลาของวันจันทร์กับทุกวันที่เปิด</button>
        <div style="font-weight:600;font-size:14px;margin-top:10px">วันหยุด / วันพิเศษของเว็บ</div>
        <div id="bsaExceptions"></div>
        <button type="button" class="btn btn-secondary btn-sm" onclick="BookingSiteAdmin.addException()">+ เพิ่มวันหยุด/วันพิเศษ</button>
        ${mto}
        <div style="margin-top:10px"><button type="button" class="btn btn-primary btn-sm" onclick="BookingSiteAdmin.saveHours()">บันทึกเวลาเปิดร้าน</button></div>`;
    },
    schedRender() {
      const w = $('bsaWeekly'); const x = $('bsaExceptions'); if (!w || !x) return;
      const st = this._sched; const ref = 'BookingSiteAdmin._sched';
      w.innerHTML = [1, 2, 3, 4, 5, 6, 0].map(d => {
        const rows = st.weekly.map((r, i) => ({ r, i })).filter(o => o.r.day === d);
        return `<div style="display:flex;gap:8px;align-items:flex-start;padding:4px 0;border-top:1px solid var(--gray-100);flex-wrap:wrap">
          <div style="width:84px;font-size:13px;font-weight:500">${esc(dayName(d))}</div>
          <div style="flex:1;display:flex;flex-direction:column;gap:4px;min-width:220px">
            ${rows.length ? rows.map(({ r, i }) => `<div style="display:flex;gap:4px;align-items:center;flex-wrap:wrap">
              <input type="time" class="form-input" style="width:auto" value="${esc(r.start)}" onchange="${ref}.weekly[${i}].start=this.value">
              <span>–</span><input type="time" class="form-input" style="width:auto" value="${esc(r.end === '24:00' ? '23:59' : r.end)}" onchange="${ref}.weekly[${i}].end=this.value">
              <select class="form-input" style="width:auto" onchange="${ref}.weekly[${i}].blocked=this.value==='1'"><option value="0"${r.blocked ? '' : ' selected'}>เปิด</option><option value="1"${r.blocked ? ' selected' : ''}>พัก/ปิด</option></select>
              <button type="button" class="btn btn-sm btn-secondary" onclick="BookingSiteAdmin.removeWeekly(${i})" title="ลบช่วง">✕</button></div>`).join('')
              : '<span class="sg-hint">ปิด (ไม่รับจอง)</span>'}
          </div>
          <button type="button" class="btn btn-sm btn-secondary" onclick="BookingSiteAdmin.addWeekly(${d})">+ ช่วง</button></div>`;
      }).join('');
      x.innerHTML = st.exceptions.length ? st.exceptions.map((e, i) => `<div style="display:flex;gap:4px;align-items:center;flex-wrap:wrap;margin:4px 0">
          <input type="date" class="form-input" style="width:auto" value="${esc(e.date)}" onchange="${ref}.exceptions[${i}].date=this.value">
          <select class="form-input" style="width:auto" onchange="${ref}.exceptions[${i}].kind=this.value;BookingSiteAdmin.schedRender()">
            <option value="closed"${e.kind === 'closed' ? ' selected' : ''}>ปิดทั้งวัน</option><option value="partial"${e.kind === 'partial' ? ' selected' : ''}>ปิดบางช่วง</option>
            <option value="open"${e.kind === 'open' ? ' selected' : ''}>เปิดเวลาพิเศษ (แทนเวลาปกติ)</option></select>
          ${e.kind === 'closed' ? '' : `<input type="time" class="form-input" style="width:auto" value="${esc(e.start)}" onchange="${ref}.exceptions[${i}].start=this.value"><span>–</span>
            <input type="time" class="form-input" style="width:auto" value="${esc(e.end)}" onchange="${ref}.exceptions[${i}].end=this.value">`}
          <button type="button" class="btn btn-sm btn-secondary" onclick="BookingSiteAdmin.removeException(${i})">✕</button></div>`).join('')
        : '<div class="sg-hint" style="margin-bottom:6px">ยังไม่มีวันหยุด/วันพิเศษ</div>';
    },
    addWeekly(d) {
      const last = this._sched.weekly.filter(w => w.day === d && !w.blocked).pop();
      this._sched.weekly.push(last ? { day: d, start: last.end === '24:00' ? '23:00' : last.end, end: '23:59', blocked: false, capacity: null }
        : { day: d, start: '09:00', end: '18:00', blocked: false, capacity: null });
      touched('hours'); this.schedRender();
    },
    removeWeekly(i) { this._sched.weekly.splice(i, 1); touched('hours'); this.schedRender(); },
    copyMonday() {
      const st = this._sched; const mon = st.weekly.filter(w => w.day === 1);
      if (!mon.length) { Layout.toast('ยังไม่ได้ตั้งเวลาของวันจันทร์', 'error'); return; }
      const open = new Set(st.weekly.filter(w => !w.blocked).map(w => w.day)); open.add(1);
      st.weekly = st.weekly.filter(w => !open.has(w.day) || w.day === 1);
      open.forEach(d => { if (d !== 1) mon.forEach(w => st.weekly.push({ ...w, day: d })); });
      touched('hours'); this.schedRender();
    },
    addException() { this._sched.exceptions.push({ date: new Date().toISOString().slice(0, 10), kind: 'closed', start: '', end: '' }); touched('hours'); this.schedRender(); },
    removeException(i) { this._sched.exceptions.splice(i, 1); touched('hours'); this.schedRender(); },
    schedPayload() {
      const st = this._sched;
      return {
        weekly: st.weekly.map(w => ({ dayOfWeek: DAY_KEYS[w.day], start: w.start, end: w.end, isBlocked: !!w.blocked, capacity: w.capacity })),
        exceptions: st.exceptions.map(x => x.kind === 'closed' ? { date: x.date, closed: true, start: null, end: null }
          : { date: x.date, closed: x.kind === 'partial', start: x.start || null, end: x.end || null }),
      };
    },
    async saveHours() { await this._save('hours', { schedule: this.schedPayload() }, null); },

    // ── การรับจอง ──
    rulesHtml() {
      const d = this._d;
      return `<div class="form-row-3">
          <div class="form-group"><label class="form-label" for="bsaDuration">ระยะเวลาต่อครั้ง (นาที)</label><input type="number" class="form-input" id="bsaDuration" min="5" max="1440" value="${esc(d.durationMinutes ?? 60)}"></div>
          <div class="form-group"><label class="form-label" for="bsaBufBefore">เวลาเตรียมก่อนนัด (นาที)</label><input type="number" class="form-input" id="bsaBufBefore" min="0" max="720" value="${esc(d.bufferBeforeMinutes ?? 0)}"></div>
          <div class="form-group"><label class="form-label" for="bsaBufAfter">เวลาเก็บล้างหลังนัด (นาที)</label><input type="number" class="form-input" id="bsaBufAfter" min="0" max="720" value="${esc(d.bufferAfterMinutes ?? 0)}"></div>
        </div>
        <div class="form-row-3">
          <div class="form-group"><label class="form-label" for="bsaMinAdv">ต้องจองล่วงหน้าอย่างน้อย (ชั่วโมง)</label><input type="number" class="form-input" id="bsaMinAdv" min="0" max="8760" value="${esc(d.minAdvanceHours ?? 1)}"></div>
          <div class="form-group"><label class="form-label" for="bsaMaxAdv">จองล่วงหน้าได้ไม่เกิน (วัน)</label><input type="number" class="form-input" id="bsaMaxAdv" min="0" max="730" value="${esc(d.maxAdvanceDays ?? 30)}"></div>
          <div class="form-group" style="align-self:end"><button type="button" class="btn btn-primary btn-sm" onclick="BookingSiteAdmin.saveRules()">บันทึกการรับจอง</button></div>
        </div>`;
    },
    async saveRules() {
      await this._save('rules', { durationMinutes: num('bsaDuration', null), bufferBeforeMinutes: num('bsaBufBefore', null), bufferAfterMinutes: num('bsaBufAfter', null),
        minAdvanceHours: num('bsaMinAdv', null), maxAdvanceDays: num('bsaMaxAdv', null) }, null);
    },

    // ── เตือนก่อนนัด & คิวสำรอง ──
    remindHtml() {
      const d = this._d;
      const ph = Array.isArray(d.reminderPlaceholders) ? d.reminderPlaceholders : [];
      return `<div class="form-row-3">
          <div class="form-group" style="align-self:end"><label style="font-size:13px"><input type="checkbox" id="bsaRemindOn"${d.reminderEnabled !== false ? ' checked' : ''}> เตือนลูกค้าก่อนนัด</label></div>
          <div class="form-group"><label class="form-label" for="bsaRemindHours">เตือนก่อนนัดกี่ชั่วโมง (คั่นด้วยจุลภาค)</label><input type="text" class="form-input" id="bsaRemindHours" placeholder="24,2" value="${esc(d.reminderHoursBefore || '24,2')}"></div>
          <div class="form-group"><label class="form-label" for="bsaHold">คิวสำรองได้ที่แล้ว กันที่ให้ยืนยันภายใน (0 = ไม่กัน)</label><input type="number" class="form-input" id="bsaHold" min="0" max="10080" value="${esc(d.waitlistHoldMinutes ?? 0)}"></div>
        </div>
        <div class="form-group"><label class="form-label" for="bsaRemindTpl">ข้อความเตือน (ว่าง = ข้อความเริ่มต้นของระบบ)</label>
          <textarea class="form-input" id="bsaRemindTpl" rows="4" placeholder="${esc(d.reminderDefaultTemplate || '')}">${esc(d.reminderTemplate || '')}</textarea>
          <div class="sg-hint">${ph.length ? 'ตัวแทนที่ใช้ได้: ' + esc(ph.join(' ')) : ''}</div></div>
        <div class="sg-hint" style="margin-bottom:8px">กันที่หมดเวลาแล้วยังไม่ยืนยัน ⇒ ระบบยกเลิกและเลื่อนคิวถัดไป</div>
        <button type="button" class="btn btn-primary btn-sm" onclick="BookingSiteAdmin.saveRemind()">บันทึกการเตือน</button>`;
    },
    async saveRemind() {
      await this._save('remind', { reminderEnabled: $('bsaRemindOn').checked, reminderHoursBefore: $('bsaRemindHours').value.trim() || '24,2',
        waitlistHoldMinutes: num('bsaHold', 0), reminderTemplate: $('bsaRemindTpl').value }, null);
    },

    // ── สมาชิก ──
    memberHtml() {
      const d = this._d;
      if (typeof d.requireMemberToBook !== 'boolean') return '<div class="sg-hint">โหลดค่าสมาชิกไม่ได้ — ลองรีเฟรชหน้า</div>';
      return `<label style="font-size:13px"><input type="checkbox" id="bsaMemberOnly"${d.requireMemberToBook ? ' checked' : ''}> ต้องเข้าสู่ระบบสมาชิกก่อนจอง (ทุกบริการที่ไม่ได้ตั้งเอง)</label>
        <div class="sg-hint" style="margin:4px 0 8px">ปิด (ค่าเริ่มต้น) = จองได้โดยไม่ต้องสมัคร · แต่ละบริการตั้งแยกได้ที่ฟอร์มบริการ · พนักงานจองแทนลูกค้าได้เสมอ · แขกกลับมาดูการจองด้วยเลขที่ + เบอร์/อีเมล</div>
        <button type="button" class="btn btn-primary btn-sm" onclick="BookingSiteAdmin.saveMember()">บันทึก</button>`;
    },
    async saveMember() { if ($('bsaMemberOnly')) await this._save('member', { requireMemberToBook: $('bsaMemberOnly').checked }, null); },

    // ── ข้อมูลที่ลูกค้ากรอก (+ ช่องเพิ่มเอง — ตรวจจริงที่เซิร์ฟเวอร์ BookingFormFields.ValidateConfig) ──
    formHtml() {
      const d = this._d;
      const opts = d.formFieldOptions || [];
      this._cfTypes = (d.customFieldTypeOptions || []).map(o => [o.value, o.label]);
      this._cfReq = opts.map(o => [o.value, o.label]);
      this._cf = ((d.formFields || {}).customFields || []).map(f => Object.assign({}, f, { options: f.options || [], serviceIds: f.serviceIds || [] }));
      this._cfLoaded = this._cfTypes.length > 0;
      if (!opts.length) return '<div class="sg-hint">โหลดตัวเลือกไม่สำเร็จ — ลองรีเฟรชหน้า</div>';
      const ff = d.formFields || {};
      const sel = (id, v) => `<select class="form-input" id="${id}">${opts.map(o => `<option value="${esc(o.value)}"${o.value === v ? ' selected' : ''}>${esc(o.label)}</option>`).join('')}</select>`;
      return `<div class="form-row-3">
          <div class="form-group"><label class="form-label" for="bsaFfPhone">เบอร์โทร</label>${sel('bsaFfPhone', ff.phone || 'Required')}</div>
          <div class="form-group"><label class="form-label" for="bsaFfEmail">อีเมล</label>${sel('bsaFfEmail', ff.email || 'Optional')}</div>
          <div class="form-group"><label class="form-label" for="bsaFfNotes">หมายเหตุ / รายละเอียดเพิ่มเติม</label>${sel('bsaFfNotes', ff.notes || 'Optional')}</div>
        </div>
        <div style="font-weight:600;font-size:14px">➕ ช่องที่เพิ่มเอง <span class="sg-hint">(ไม่เกิน 10 ช่อง · เลือกได้ว่าใช้กับบริการไหน)</span></div>
        <div id="bsaCustomFields" style="margin-top:8px"></div>
        <div style="display:flex;gap:8px;flex-wrap:wrap"><button type="button" class="btn btn-secondary btn-sm" onclick="BookingSiteAdmin.cfAdd()">+ เพิ่มช่อง</button>
          <button type="button" class="btn btn-primary btn-sm" onclick="BookingSiteAdmin.saveForm()">บันทึกฟอร์มจอง</button></div>`;
    },
    cfRender() {
      const wrap = $('bsaCustomFields'); if (!wrap) return;
      if (!this._cfLoaded) { wrap.innerHTML = '<div class="sg-hint">โหลดค่าตั้งช่องเพิ่มเองไม่สำเร็จ — ลองรีเฟรชหน้า</div>'; return; }
      const sel = (list, v) => list.map(([val, lbl]) => `<option value="${esc(val)}"${val === v ? ' selected' : ''}>${esc(lbl)}</option>`).join('');
      const svcs = this._services || [];
      wrap.innerHTML = this._cf.map((f, i) => {
        const known = (f.serviceIds || []).filter(id => svcs.some(s => s.id === id));
        const stale = (f.serviceIds || []).length > 0 && known.length === 0;
        const scope = (f.serviceIds || []).length ? `${known.length} บริการ` : 'ทุกบริการ';
        return `<div style="border:1px solid var(--gray-200);border-radius:8px;padding:10px;margin-bottom:8px">
          <div class="form-row-3">
            <div class="form-group"><label class="form-label">ชื่อช่องที่ลูกค้าเห็น</label><input class="form-input" maxlength="100" value="${esc(f.label || '')}" placeholder="เช่น ความยาวผม" onchange="BookingSiteAdmin.cf(${i},'label',this.value)"></div>
            <div class="form-group"><label class="form-label">ชนิด</label><select class="form-input" onchange="BookingSiteAdmin.cf(${i},'type',this.value)">${sel(this._cfTypes, f.type || 'text')}</select></div>
            <div class="form-group"><label class="form-label">บังคับกรอก</label><select class="form-input" onchange="BookingSiteAdmin.cf(${i},'requirement',this.value)">${sel(this._cfReq, f.requirement || 'Optional')}</select></div>
          </div>
          ${f.type === 'select' ? `<div class="form-group"><label class="form-label">ตัวเลือก (คั่นด้วย ,)</label><input class="form-input" value="${esc((f.options || []).join(', '))}" placeholder="เช่น สั้น, กลาง, ยาว" onchange="BookingSiteAdmin.cf(${i},'options',this.value)"></div>` : ''}
          <div class="form-row-3">
            <div class="form-group"><label class="form-label">รหัสช่อง (a-z 0-9 _)</label><input class="form-input" maxlength="30" value="${esc(f.key || '')}" onchange="BookingSiteAdmin.cf(${i},'key',this.value)"></div>
            <div class="form-group"><label class="form-label">ยาวสุด (ตัวอักษร · ว่าง = 500)</label><input class="form-input" type="number" min="1" max="500" value="${f.maxLength == null ? '' : Number(f.maxLength)}" onchange="BookingSiteAdmin.cf(${i},'maxLength',this.value)"></div>
            <div class="form-group"><label class="form-label">ใช้กับบริการ</label><details><summary id="bsaCfScope${i}" style="cursor:pointer">${esc(scope)}${stale ? ' <span style="color:#DC2626">· บริการที่เลือกถูกลบแล้ว — เลือกใหม่</span>' : ''}</summary>
              <div class="sg-hint">ไม่ติ๊กเลย = ทุกบริการ</div>
              ${svcs.map(s => `<label style="display:flex;gap:6px;align-items:center;font-size:13px"><input type="checkbox"${(f.serviceIds || []).includes(s.id) ? ' checked' : ''} onchange="BookingSiteAdmin.cfSvc(${i},'${Layout.jsArg(s.id)}',this.checked)"> ${esc(s.name || '')}</label>`).join('') || '<div class="sg-hint">ยังไม่มีบริการ</div>'}
            </details></div>
          </div>
          <button type="button" class="btn btn-sm btn-danger" onclick="BookingSiteAdmin.cfRemove(${i})">🗑 ลบช่องนี้</button></div>`;
      }).join('') || '<div class="sg-hint" style="margin-bottom:8px">ยังไม่มีช่องที่เพิ่มเอง</div>';
    },
    cf(i, k, v) {
      const f = this._cf[i]; if (!f) return;
      if (k === 'options') f.options = String(v || '').split(',').map(x => x.trim()).filter(Boolean);
      else if (k === 'maxLength') f.maxLength = v === '' ? null : Number(v);
      else f[k] = v;
      if (k === 'type') this.cfRender();
    },
    cfSvc(i, id, on) {
      const f = this._cf[i]; if (!f) return;
      const svcs = (this._services || []).map(s => s.id);
      const cur = (f.serviceIds || []).filter(x => svcs.includes(x) && x !== id);
      f.serviceIds = on ? cur.concat([id]) : cur;
      const sum = $('bsaCfScope' + i); if (sum) sum.textContent = f.serviceIds.length ? `${f.serviceIds.length} บริการ` : 'ทุกบริการ';
    },
    cfAdd() {
      if (!this._cfLoaded) return;
      let n = this._cf.length + 1; while (this._cf.some(f => f.key === 'field' + n)) n++;
      this._cf.push({ key: 'field' + n, label: '', type: 'text', options: [], requirement: 'Optional', maxLength: null, serviceIds: [] });
      touched('form'); this.cfRender();
    },
    cfRemove(i) { this._cf.splice(i, 1); touched('form'); this.cfRender(); },
    async saveForm() {
      if (!$('bsaFfPhone')) return;
      const formFields = { phone: $('bsaFfPhone').value, email: $('bsaFfEmail').value, notes: $('bsaFfNotes').value };
      // โหลดรายการเดิมได้เท่านั้นจึงส่ง (ไม่ส่ง = เซิร์ฟเวอร์คงรายการเดิม · [] = ลบทั้งหมด)
      if (this._cfLoaded) formFields.customFields = this._cf.map(f => Object.assign({}, f, { serviceIds: (f.serviceIds || []).length ? f.serviceIds : null }));
      await this._save('form', { formFields }, () => { $('bsa_form_body').innerHTML = this.formHtml(); this.cfRender(); });
    },

    // ── เอกสาร (ขั้นสูง) ──
    docsHtml() {
      const d = this._d;
      const cur = d.completionDocument || 'Auto';
      const timing = d.guaranteedInvoiceTiming || 'TaxInvoiceAtBooking';
      const radios = (name, list, v, sub) => list.map(o => `<label style="display:flex;gap:8px;align-items:flex-start;margin:6px 0;cursor:pointer">
          <input type="radio" name="${name}" value="${esc(o.value)}"${o.value === v ? ' checked' : ''} style="margin-top:3px"><span><strong>${esc(o.label)}</strong><br><span class="sg-hint">${esc(o[sub])}</span></span></label>`).join('');
      return `<details><summary style="cursor:pointer;font-size:13px">เปิดดู / แก้ (สำหรับผู้ทำบัญชี)</summary>
        <div class="form-label" style="margin-top:8px">ตอนให้บริการเสร็จ (บริการแบบนัดหมาย)</div>
        ${radios('bsaCompletionDoc', d.completionDocumentOptions || [], cur, 'description') || '<div class="sg-hint">โหลดตัวเลือกไม่สำเร็จ</div>'}
        <div class="form-label" style="margin-top:8px">จังหวะออกใบกำกับภาษี — บริการแบบรับประกันที่ (Guaranteed)</div>
        ${radios('bsaInvoiceTiming', d.guaranteedInvoiceTimingOptions || [], timing, 'taxEffect') || '<div class="sg-hint">โหลดตัวเลือกไม่สำเร็จ</div>'}
        <div style="overflow-x:auto;margin-top:8px"><table class="table" style="font-size:12px"><thead><tr><th>ชนิดบริการ</th><th>ตอนจอง</th><th>ตอนรับเงิน</th><th>ตอนให้บริการเสร็จ</th><th>ตอนยกเลิก</th></tr></thead>
          <tbody>${(d.documentFlow || []).map(r => `<tr><td><strong>${esc(r.bookingTypeLabel)}</strong></td><td>${esc(r.atBooking)}</td><td>${esc(r.onPayment)}</td><td>${esc(r.onComplete)}</td><td>${esc(r.onCancel)}</td></tr>`).join('')}</tbody></table></div>
        <div class="sg-hint">ตารางสรุปตามค่าตั้งปัจจุบัน + สถานะจด VAT ของบริษัท · มีผลกับการจองใหม่เท่านั้น</div>
        <button type="button" class="btn btn-primary btn-sm" style="margin-top:6px" onclick="BookingSiteAdmin.saveDocs()">บันทึกค่าตั้งเอกสาร</button></details>`;
    },
    async saveDocs() {
      const body = {};
      const cd = document.querySelector('input[name="bsaCompletionDoc"]:checked'); if (cd) body.completionDocument = cd.value;
      const t = document.querySelector('input[name="bsaInvoiceTiming"]:checked'); if (t) body.guaranteedInvoiceTiming = t.value;
      await this._save('docs', body, () => { $('bsa_docs_body').innerHTML = this.docsHtml(); });
    },

    // ── ค่าตอบแทนผู้ให้บริการ (กติกา · GET/PUT …/booking/resource-pay — สิทธิ์ตรวจที่เซิร์ฟเวอร์) ──
    payBase() { return `/api/companies/${Layout.getCompanyId()}/cms/sites/${encodeURIComponent(this._siteId)}/booking`; },
    async loadPay() {
      const el = $('bsa_pay_body'); if (!el) return;
      let st;
      try { st = (await API.get(`${this.payBase()}/resource-pay`)).data || { resources: [], employees: [], modes: [] }; }
      catch (e) { el.innerHTML = `<div class="sg-hint">ดู/แก้ค่าตอบแทนไม่ได้: ${esc(e.message)} (ต้องมีสิทธิ์ดูเงินเดือน)</div>`; return; }
      const modes = Array.isArray(st.modes) && st.modes.length ? st.modes.map(m => [m.value, m.label]) : null;
      if (!modes) { el.innerHTML = '<div class="sg-hint">โหลดตัวเลือกกติกาไม่สำเร็จ — ลองรีเฟรชหน้า</div>'; return; }
      const emps = st.employees || [];
      const empOpts = (sel) => '<option value="">— ไม่ผูก —</option>' + emps.map(e => `<option value="${esc(e.id)}"${e.id === sel ? ' selected' : ''}>${esc(e.name)}</option>`).join('')
        + (sel && !emps.some(e => e.id === sel) ? `<option value="${esc(sel)}" selected>(พนักงานที่ไม่ใช้งาน)</option>` : '');
      const modeOpts = (sel) => modes.map(([v, l]) => `<option value="${esc(v)}"${v === sel ? ' selected' : ''}>${esc(l)}</option>`).join('');
      const rows = (st.resources || []).map(r => `<tr data-rid="${esc(r.resourceId)}">
          <td><strong>${esc(r.name)}</strong>${r.isActive ? '' : ' <span class="sg-hint">(ปิด)</span>'}</td>
          <td><select class="form-input pay-emp" aria-label="พนักงาน">${empOpts(r.employeeId)}</select></td>
          <td><select class="form-input pay-mode" aria-label="กติกา">${modeOpts(r.mode)}</select></td>
          <td><input type="number" class="form-input pay-rate" min="0" step="0.01" value="${esc(Number(r.rate))}" style="width:100px" aria-label="อัตรา"></td>
          <td><input type="number" class="form-input pay-min" min="0" step="0.01" value="${esc(Number(r.minimumPerClass))}" style="width:100px" aria-label="ขั้นต่ำต่อคลาส (ต่อหัวเท่านั้น)"></td>
          <td><button type="button" class="btn btn-sm btn-secondary" onclick="BookingSiteAdmin.savePay('${Layout.jsArg(r.resourceId)}')">บันทึก</button></td></tr>`).join('')
        || '<tr><td colspan="6" class="sg-hint" style="padding:12px">ยังไม่มีผู้ให้บริการ</td></tr>';
      el.innerHTML = `<div style="overflow-x:auto"><table class="table" style="font-size:13px"><thead><tr><th>ผู้ให้บริการ</th><th>พนักงาน (HR)</th><th>กติกา</th><th>อัตรา</th><th>ขั้นต่ำ/คลาส</th><th></th></tr></thead><tbody>${rows}</tbody></table></div>`;
      clean('pay');
    },
    async savePay(rid) {
      const tr = document.querySelector(`#bsa_pay_body tr[data-rid="${CSS.escape(rid)}"]`); if (!tr) return;
      const body = { employeeId: tr.querySelector('.pay-emp').value || null, mode: tr.querySelector('.pay-mode').value,
        rate: Number(tr.querySelector('.pay-rate').value || 0), minimumPerClass: Number(tr.querySelector('.pay-min').value || 0) };
      try { const r = await API.put(`${this.payBase()}/resource-pay/${encodeURIComponent(rid)}`, body); Layout.toast(r.message || 'บันทึกแล้ว', 'success'); await this.loadPay(); }
      catch (e) { Layout.toast(e.message, 'error'); }
    },

    /** บันทึกเฉพาะช่องของการ์ดนี้ (null/ไม่ส่ง = เซิร์ฟเวอร์คงค่าเดิม) · สำเร็จ ⇒ ล้างค่าค้างเฉพาะการ์ดนี้ (การ์ดอื่นที่ค้างไม่ถูกวาดทับ) */
    async _save(key, body, after) {
      try {
        const res = await Layout.api().cmsUpdateBookingSiteSettings(this._siteId, body);
        if (res && res.data) this._d = res.data;
        Layout.toast('บันทึกแล้ว', 'success');
        if (typeof after === 'function') after();
        clean(key);
      } catch (e) { Layout.toast(e.message, 'error', 10000); }
    },
  };
  window.BookingSiteAdmin = A;
})();
