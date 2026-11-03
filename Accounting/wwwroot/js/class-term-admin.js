/* รอบ 230 (erp-review/2026-11-02/CMS_SETTINGS_UX_AUDIT.md §6 ค้างข้อ 4 · §2 แถว cms-bookings) — "คลาส & เทอม" (ตารางคลาสประจำ) ย้ายจากหน้าทำงานการจอง เข้าศูนย์ "ตั้งค่าเว็บ"
 * เดิม: เทอม (ค่าตั้งของสิ่งที่ขาย) อยู่ในหน้าทำงาน cms-bookings + "สร้างรอบ" ถามจำนวนสัปดาห์ด้วย prompt()
 * ตอนนี้: ตาราง/ฟอร์มเทอมอยู่ที่นี่ที่เดียว · บันทึกเทอมใหม่แล้วสร้างรอบล่วงหน้าทันที (ค่าเริ่มต้น 8 สัปดาห์ · ตั้งเป็น 0 = ยังไม่สร้าง) ·
 *   ปุ่มสร้างรอบต่อแถวมีช่องจำนวนสัปดาห์ในแถว (ไม่ใช้ prompt) · หน้าทำงานการจองเหลือปฏิทินสัปดาห์ + roster/เช็คอิน (งานประจำวัน)
 * ทุกข้อตัดสินที่เซิร์ฟเวอร์ (ClassScheduleGenerator · ตรวจเทอม) — ไฟล์นี้ส่งค่า/แสดงผลอย่างเดียว
 * ใช้: cms-site-settings กลุ่ม "คลาส & เทอม" (CmsSiteSettingsExt.classes) */
(function () {
  const esc = (v) => (window.Layout && Layout.esc ? Layout.esc(v) : String(v == null ? '' : v).replace(/[&<>"']/g, c => ({ '&': '&amp;', '<': '&lt;', '>': '&gt;', '"': '&quot;', "'": '&#39;' }[c])));
  const $ = (id) => document.getElementById(id);
  const todayBkk = () => new Date(Date.now() + 7 * 3600 * 1000).toISOString().slice(0, 10);
  const dayShort = (d) => new Date(Date.UTC(2026, 10, 1 + d)).toLocaleDateString('th-TH', { weekday: 'short', timeZone: 'UTC' });   // 2026-11-01 = วันอาทิตย์

  const MODAL = `<div class="modal-overlay" id="ctaModal"><div class="modal" style="max-width:600px">
      <div class="modal-header"><h3 class="modal-title" id="ctaTitle">เทอมคลาส</h3><button class="modal-close" onclick="Layout.closeModal('ctaModal')">&times;</button></div>
      <div class="modal-body" id="ctaBody">
        <div class="form-group"><label class="form-label" for="ctaService">บริการ (คลาส)</label><select class="form-input" id="ctaService" onchange="ClassTermAdmin.fillInstructors()"></select></div>
        <div class="form-group"><label class="form-label" for="ctaName">ชื่อเทอม (ไม่บังคับ)</label><input type="text" class="form-input" id="ctaName" maxlength="200" placeholder="เช่น เทอมหนาว 2569"></div>
        <div class="form-group"><span class="form-label">วันในสัปดาห์</span><div id="ctaDays" style="display:flex;gap:10px;flex-wrap:wrap;font-size:13px"></div></div>
        <div class="form-row">
          <div class="form-group"><label class="form-label" for="ctaStart">เวลาเริ่มคลาส</label><input type="time" class="form-input" id="ctaStart" value="18:00"></div>
          <div class="form-group"><label class="form-label" for="ctaCapacity">ที่นั่งต่อรอบ (ว่าง = ความจุของบริการ)</label><input type="number" min="1" max="10000" class="form-input" id="ctaCapacity"></div>
        </div>
        <div class="form-row">
          <div class="form-group"><label class="form-label" for="ctaFrom">เริ่มเทอม</label><input type="date" class="form-input" id="ctaFrom"></div>
          <div class="form-group"><label class="form-label" for="ctaTo">สิ้นสุดเทอม (ว่าง = ไม่กำหนด)</label><input type="date" class="form-input" id="ctaTo"></div>
        </div>
        <div class="form-row">
          <div class="form-group"><label class="form-label" for="ctaInstructor">ผู้สอนประจำ</label><select class="form-input" id="ctaInstructor"></select></div>
          <div class="form-group"><label class="form-label" for="ctaStatus">สถานะ</label>
            <select class="form-input" id="ctaStatus"><option value="Active">ใช้งาน (สร้างรอบได้)</option><option value="Ended">สิ้นสุดแล้ว (หยุดสร้างรอบ)</option></select></div>
        </div>
        <label style="font-size:13px"><input type="checkbox" id="ctaSkipHolidays" checked> ข้ามวันหยุดบริษัท (หน้า "วันหยุด")</label>
        <div class="form-group" style="margin-top:8px"><label class="form-label" for="ctaNotes">หมายเหตุ</label><input type="text" class="form-input" id="ctaNotes"></div>
        <div class="form-group"><label class="form-label" for="ctaWeeks">หลังบันทึก สร้างคลาสล่วงหน้ากี่สัปดาห์ (0 = ยังไม่สร้าง · กดซ้ำได้ ไม่สร้างซ้ำ)</label>
          <input type="number" class="form-input" id="ctaWeeks" min="0" max="52" style="max-width:120px"></div>
        <div class="sg-hint">เวลาจบ = เวลาเริ่ม + ระยะเวลาของบริการ · ผู้สอนต้องเป็นผู้ให้บริการที่รับบริการนี้</div>
      </div>
      <div class="modal-footer"><button class="btn btn-secondary" onclick="Layout.closeModal('ctaModal')">ยกเลิก</button><button class="btn btn-primary" id="ctaSaveBtn" onclick="ClassTermAdmin.save()">บันทึกเทอม</button></div>
    </div></div>`;

  const T = {
    _siteId: null, _terms: [], _services: [], _resources: [], _editId: null,

    async mount(el, ctx) {
      this._siteId = ctx.siteId;
      if (!$('ctaModal')) document.body.insertAdjacentHTML('beforeend', MODAL);
      const id = encodeURIComponent(this._siteId || '');
      el.innerHTML = `<div class="sg-card"><h3>📚 คลาส & เทอม (ตารางคลาสประจำ) ${typeof ctx.scopeBadge === 'function' ? ctx.scopeBadge('site') : ''}</h3>
          <div class="sg-hint" style="margin-bottom:8px">บริการที่มีเทอม ⇒ หน้าเว็บเปิดจองเฉพาะคลาสในตาราง (เวลาเปิดรายสัปดาห์ของบริการนั้นไม่ใช้) · สร้างคลาสล่วงหน้าซ้ำได้ ไม่ซ้ำ และไม่ลบคลาสที่มีคนจอง ·
            แก้ครู/ที่นั่งของคลาสแต่ละครั้ง + รายชื่อคนเรียน/เช็คอิน ทำที่ <a href="/pages/cms-bookings.html?siteId=${id}" style="color:var(--primary)">หน้าการจอง › ตารางคลาส →</a> ·
            แพ็กเกจ/บัตรคลาสขายที่ <a href="/pages/course-credits.html" style="color:var(--primary)">คอร์ส & เครดิต →</a></div>
          <div style="display:flex;justify-content:flex-end;margin-bottom:8px"><button type="button" class="btn btn-primary btn-sm" onclick="ClassTermAdmin.open(null)">+ เทอมคลาสใหม่</button></div>
          <div style="overflow-x:auto"><table class="table" style="font-size:13px"><thead><tr><th>บริการ / เทอม</th><th>วัน · เวลา</th><th>ช่วงเทอม</th><th>ผู้สอน · ที่นั่ง</th><th>สถานะ</th><th>ดำเนินการ</th></tr></thead>
            <tbody id="ctaTerms"><tr><td colspan="6" class="sg-hint">กำลังโหลด…</td></tr></tbody></table></div></div>`;
      await this.reload(true);
    },

    async reload(withMeta) {
      const api = Layout.api(); if (!api) return;
      try {
        const jobs = [api.cmsClassTerms(this._siteId, '')];
        if (withMeta) jobs.push(api.cmsListAllBookingServices(this._siteId), api.cmsListBookingResources(this._siteId));
        const [tt, svcs, res] = await Promise.all(jobs);
        this._terms = tt.data || [];
        if (withMeta) { this._services = svcs.data || []; this._resources = (res.data || []).filter(r => r.isActive !== false); }
      } catch (e) { const b = $('ctaTerms'); if (b) b.innerHTML = `<tr><td colspan="6" style="color:#b91c1c">โหลดเทอมคลาสไม่สำเร็จ: ${esc(e.message)}</td></tr>`; return; }
      this.render();
    },

    render() {
      const body = $('ctaTerms'); if (!body) return;
      if (!this._terms.length) { body.innerHTML = '<tr><td colspan="6" class="sg-hint" style="padding:12px">ยังไม่มีเทอมคลาส — กด "+ เทอมคลาสใหม่"</td></tr>'; return; }
      body.innerHTML = this._terms.map(t => `<tr>
          <td><strong>${esc(t.serviceName)}</strong>${t.title ? `<div class="sg-hint">${esc(t.title)}</div>` : ''}</td>
          <td>${esc(t.weekdaysLabel)}<div style="font-size:11px">${esc(t.startTime)}–${esc(t.endTime)}</div></td>
          <td>${esc(Layout.date(t.startDate))} – ${t.endDate ? esc(Layout.date(t.endDate)) : 'ไม่กำหนด'}<div class="sg-hint">คลาสที่ยังไม่ถึง ${Number(t.upcomingSessions || 0)}${t.lastSessionDate ? ' · ถึง ' + esc(Layout.date(t.lastSessionDate)) : ''}</div></td>
          <td>${esc(t.instructorName || '—')}<div style="font-size:11px">${Number(t.effectiveCapacity || 0)} ที่/คลาส</div></td>
          <td>${esc(t.statusLabel)}</td>
          <td style="white-space:nowrap">
            <button type="button" class="btn btn-sm btn-secondary" onclick="ClassTermAdmin.open('${Layout.jsArg(t.id)}')">แก้ไข</button>
            <span style="display:inline-flex;gap:4px;align-items:center"><input type="number" class="form-input" min="1" max="52" value="8" style="width:64px" id="ctaW_${esc(t.id)}" aria-label="จำนวนสัปดาห์" data-no-dirty="1">
              <button type="button" class="btn btn-sm btn-primary" onclick="ClassTermAdmin.generate('${Layout.jsArg(t.id)}')">สร้างคลาสล่วงหน้า (สัปดาห์)</button></span>
            <button type="button" class="btn btn-sm btn-secondary" onclick="ClassTermAdmin.remove('${Layout.jsArg(t.id)}')">ลบ</button>
          </td></tr>`).join('');
    },

    open(id) {
      const t = id ? this._terms.find(x => x.id === id) : null;
      if (id && !t) return;
      this._editId = id || null;
      $('ctaTitle').textContent = t ? 'แก้เทอมคลาส' : 'เทอมคลาสใหม่';
      const svc = $('ctaService');
      svc.innerHTML = this._services.map(s => `<option value="${esc(s.id)}">${esc(s.name)}</option>`).join('');
      svc.value = t ? t.bookingServiceId : ((this._services[0] || {}).id || '');
      svc.disabled = !!t;   // ย้ายเทอมข้ามบริการไม่ได้ (เซิร์ฟเวอร์ปฏิเสธ) — ล็อกช่องพร้อมบอกเหตุ
      svc.title = t ? 'ย้ายเทอมไปบริการอื่นไม่ได้ — สร้างเทอมใหม่ที่บริการนั้น' : '';
      const picked = t ? t.weekdays : [];
      $('ctaDays').innerHTML = [1, 2, 3, 4, 5, 6, 0].map(n => `<label><input type="checkbox" class="cta-day" value="${n}"${picked.includes(n) ? ' checked' : ''}> ${esc(dayShort(n))}</label>`).join('');
      $('ctaName').value = t ? (t.title || '') : '';
      $('ctaStart').value = t ? t.startTime : '18:00';
      $('ctaCapacity').value = t && t.capacityOverride != null ? t.capacityOverride : '';
      $('ctaFrom').value = t ? t.startDate : todayBkk();
      $('ctaTo').value = t && t.endDate ? t.endDate : '';
      $('ctaStatus').value = t ? t.status : 'Active';
      $('ctaSkipHolidays').checked = t ? !!t.skipHolidays : true;
      $('ctaNotes').value = t ? (t.notes || '') : '';
      $('ctaWeeks').value = t ? '0' : '8';   // เทอมใหม่สร้างคลาสล่วงหน้าทันที 8 สัปดาห์ (audit §2) · แก้เทอมเดิมไม่สร้างเองถ้าไม่ขอ
      this.fillInstructors(t ? t.instructorResourceId : null);
      Layout.openModal('ctaModal');
    },

    fillInstructors(selected) {
      const svc = $('ctaService').value; const sel = $('ctaInstructor');
      const pool = this._resources.filter(r => Array.isArray(r.serviceIds) && r.serviceIds.includes(svc));
      sel.innerHTML = '<option value="">— ระบบจัดผู้ให้บริการตามปกติ —</option>' + pool.map(r => `<option value="${esc(r.id)}">${esc(r.name)}</option>`).join('');
      sel.value = selected && pool.some(r => r.id === selected) ? selected : '';
    },

    async save() {
      const api = Layout.api(); if (!api) return;
      const cap = $('ctaCapacity').value; const to = $('ctaTo').value;
      const body = {
        bookingServiceId: $('ctaService').value, title: $('ctaName').value.trim() || null,
        weekdays: Array.from(document.querySelectorAll('.cta-day')).filter(x => x.checked).map(x => Number(x.value)),
        startTime: $('ctaStart').value, startDate: ($('ctaFrom').value || todayBkk()) + 'T00:00:00', endDate: to ? to + 'T00:00:00' : null,
        instructorResourceId: $('ctaInstructor').value || null, capacityOverride: cap === '' ? null : Number(cap),
        skipHolidays: $('ctaSkipHolidays').checked, status: $('ctaStatus').value, notes: $('ctaNotes').value.trim() || null,
      };
      const weeks = Math.max(0, Math.min(52, Number($('ctaWeeks').value || 0) || 0));
      let termId = this._editId;
      // ฝ่ายค้านรอบ 230 N2: กดซ้ำระหว่างรอคำตอบ = สร้างเทอมซ้ำ ⇒ ปิดปุ่มจนเสร็จ
      const btn = $('ctaSaveBtn'); if (btn && btn.disabled) return; if (btn) btn.disabled = true;
      try {
        if (this._editId) await api.cmsUpdateClassTerm(this._siteId, this._editId, body);
        else { const r = await api.cmsCreateClassTerm(this._siteId, body); termId = (r && r.data && r.data.id) || null; }
      } catch (e) { Layout.toast('บันทึกเทอมไม่สำเร็จ: ' + e.message, 'error'); return; }
      finally { if (btn) btn.disabled = false; }
      Layout.closeModal('ctaModal');
      if (weeks > 0 && termId) await this.generate(termId, weeks);
      else { Layout.toast(weeks > 0 ? 'บันทึกเทอมแล้ว — กด "สร้างคลาสล่วงหน้า" ที่แถวเทอม' : 'บันทึกเทอมแล้ว', 'success'); await this.reload(false); }
    },

    async generate(id, weeksArg) {
      const api = Layout.api(); if (!api) return;
      const w = weeksArg || Number(($('ctaW_' + id) || {}).value || 0);
      if (!(w >= 1 && w <= 52)) { Layout.toast('จำนวนสัปดาห์ต้องอยู่ระหว่าง 1–52', 'error'); return; }
      try {
        const r = (await api.cmsGenerateClassSessions(this._siteId, id, { weeks: w })).data || {};
        Layout.toast(`สร้างคลาสใหม่ ${Number(r.created || 0)} ครั้ง · มีอยู่แล้ว ${Number(r.alreadyExisting || 0)} · ลบคลาสเก่าที่ไม่มีคนจอง ${Number(r.removed || 0)}`, 'success', 10000);
        (r.skipped || []).forEach(s => Layout.toast(`ข้าม ${Layout.date(s.date)} — ${s.reason}`, 'warning', 10000));
        (r.notices || []).forEach(n => Layout.toast(n, 'warning', 15000));
      } catch (e) { Layout.toast('สร้างคลาสไม่สำเร็จ: ' + e.message, 'error'); }
      await this.reload(false);
    },

    async remove(id) {
      const api = Layout.api(); if (!api) return;
      if (!confirm('ลบเทอมนี้? คลาสที่ยังไม่ถึงและไม่มีผู้จองจะถูกลบด้วย (มีผู้จอง = ต้องยกเลิกคลาสก่อน)')) return;
      try {
        const r = await api.cmsDeleteClassTerm(this._siteId, id);
        (r.data || []).forEach(n => Layout.toast(n, 'warning', 10000));
        Layout.toast('ลบเทอมแล้ว');
        await this.reload(false);
      } catch (e) { Layout.toast('ลบเทอมไม่สำเร็จ: ' + e.message, 'error'); }
    },
  };
  window.ClassTermAdmin = T;
  window.CmsSiteSettingsExt = window.CmsSiteSettingsExt || {};
  window.CmsSiteSettingsExt.classes = (el, ctx) => T.mount(el, ctx);
})();
