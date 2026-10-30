/* รอบ 212 C — ส่วน "สรุปรายวันทาง LINE" ในแท็บการแจ้งเตือนของ settings.html
 * เซิร์ฟเวอร์ส่งรายการหัวข้อ/ป้าย/บทบาท/สถานะผูก LINE มาทั้งหมด (Helpers/StaffDailySummaryPlan) — ไฟล์นี้แสดงผลอย่างเดียว ไม่มีสำเนากติกา
 * ทุกค่าที่มาจากผู้ใช้ผ่าน Layout.esc ก่อนต่อเข้า HTML · ปุ่มส่ง id ผ่าน data-* (ไม่มีข้อความอิสระใน onclick) */
(function () {
  const esc = (s) => Layout.esc(s);

  const S = {
    data: null,
    editing: null, // null = ไม่ได้แก้ · {} = ชุดใหม่ · config = แก้ชุดเดิม

    async call(path, opts) {
      const cid = Layout.getCompanyId();
      const r = await fetch(`/api/companies/${cid}/notifications/daily-summary${path || ''}`, Object.assign({
        headers: { 'Authorization': 'Bearer ' + API.token, 'Content-Type': 'application/json' }
      }, opts || {}));
      let j = {};
      try { j = await r.json(); } catch (_) { j = {}; }
      if (r.status === 403) throw new Error(j.message || 'ต้องมีสิทธิ์ "ตั้งค่าบริษัท" จึงจะจัดการสรุปรายวันได้');
      if (!r.ok) throw new Error(j.message || ('HTTP ' + r.status));
      return j;
    },

    async load() {
      const body = document.getElementById('staffSummaryBody');
      if (!body) return;
      body.innerHTML = '<div class="text-gray-400" style="padding:16px">กำลังโหลด…</div>';
      try {
        const j = await this.call('');
        this.data = j.data;
        this.editing = null;
        this.render();
      } catch (e) {
        body.innerHTML = '<div style="color:#dc2626;padding:12px">' + esc(e.message) + '</div>';
      }
    },

    sectionLabel(key) {
      const s = (this.data.sections || []).find(x => x.key === key);
      return s ? s.label : key;
    },

    memberChip(m) {
      const bind = m.lineBound
        ? '<span style="color:#16a34a;font-size:11px">ผูก LINE แล้ว</span>'
        : '<span style="color:#dc2626;font-size:11px">ยังไม่ผูก LINE</span>';
      return '<span style="display:inline-block;border:1px solid #e2e8f0;border-radius:12px;padding:2px 8px;margin:2px;font-size:12px">'
        + esc(m.name) + ' · ' + esc(m.roleLabel) + ' · ' + bind + '</span>';
    },

    render() {
      const body = document.getElementById('staffSummaryBody');
      const d = this.data;
      if (!body || !d) return;
      let html = '';
      const configs = d.configs || [];
      if (configs.length === 0 && !this.editing)
        html += '<div class="text-gray-400" style="padding:8px 0">ยังไม่มีชุดสรุป — กด "เพิ่มชุดสรุป" เพื่อเริ่ม</div>';
      configs.forEach(c => {
        if (this.editing && this.editing.id === c.id) { html += this.formHtml(c); return; }
        const unbound = (c.recipients || []).filter(m => !m.lineBound).length;
        html += '<div style="border:1px solid #e2e8f0;border-radius:8px;padding:12px;margin-bottom:12px">'
          + '<div style="display:flex;justify-content:space-between;gap:8px;flex-wrap:wrap">'
          + '<div><strong>' + esc(c.siteName || 'ทุกเว็บที่ผู้รับเห็น') + '</strong> · ส่งเวลา ' + esc(c.sendAt) + ' น. '
          + (c.enabled ? '<span style="color:#16a34a">● เปิด</span>' : '<span style="color:#94a3b8">● ปิด</span>') + '</div>'
          + '<div style="display:flex;gap:6px;flex-wrap:wrap">'
          + '<button class="btn btn-outline btn-sm" data-sds-act="edit" data-sds-id="' + esc(c.id) + '">แก้ไข</button>'
          + '<button class="btn btn-outline btn-sm" data-sds-act="test" data-sds-id="' + esc(c.id) + '">ส่งทดสอบตอนนี้ (ถึงฉัน)</button>'
          + '<button class="btn btn-outline btn-sm" style="color:#dc2626" data-sds-act="delete" data-sds-id="' + esc(c.id) + '">ลบ</button>'
          + '</div></div>'
          + '<div style="font-size:12px;color:#475569;margin-top:6px">หัวข้อ: ' + (c.sections || []).map(k => esc(this.sectionLabel(k))).join(' · ') + '</div>'
          + '<div style="margin-top:6px">' + ((c.recipients || []).map(m => this.memberChip(m)).join('') || '<span style="color:#dc2626;font-size:12px">ไม่มีผู้รับ</span>') + '</div>'
          + (unbound > 0 ? '<div style="font-size:12px;color:#b45309;margin-top:6px">⚠️ ' + unbound + ' คนยังไม่ผูก LINE — จะถูกข้าม · ' + esc(d.bindHowTo)
              + ' <a href="' + esc(d.bindPage) + '" target="_blank" rel="noopener">เปิดหน้าผูก LINE</a></div>' : '')
          + (c.lastRunNote ? '<div style="font-size:12px;color:#64748b;margin-top:6px">รอบล่าสุด: ' + esc(c.lastRunNote) + '</div>' : '')
          + '</div>';
      });
      if (this.editing && !this.editing.id) html += this.formHtml(null);
      if (!this.editing)
        html += '<button class="btn btn-outline" data-sds-act="new">+ เพิ่มชุดสรุป</button>';
      html += this.historyHtml();
      body.innerHTML = html;
    },

    formHtml(c) {
      const d = this.data;
      const cur = c || { enabled: true, sendAt: d.defaultSendAt, siteId: null, sections: (d.sections || []).map(s => s.key), recipientUserIds: [], recipientRoles: ['Owner'] };
      const siteOpts = '<option value="">ทุกเว็บที่ผู้รับเห็น</option>' + (d.sites || []).map(s =>
        '<option value="' + esc(s.key) + '"' + (cur.siteId === s.key ? ' selected' : '') + '>' + esc(s.label) + '</option>').join('');
      const secs = (d.sections || []).map(s =>
        '<label style="display:block;font-size:13px;margin:4px 0"><input type="checkbox" data-sds-sec="' + esc(s.key) + '"'
        + ((cur.sections || []).includes(s.key) ? ' checked' : '') + '> ' + esc(s.label)
        + ' <span class="text-gray-400" style="font-size:11px">— ' + esc(s.hint) + '</span></label>').join('');
      const roles = (d.roles || []).map(r =>
        '<label style="display:inline-block;font-size:13px;margin:4px 12px 4px 0"><input type="checkbox" data-sds-role="' + esc(r.key) + '"'
        + ((cur.recipientRoles || []).includes(r.key) ? ' checked' : '') + '> ' + esc(r.label) + '</label>').join('');
      const users = (d.members || []).map(m =>
        '<label style="display:block;font-size:13px;margin:3px 0"><input type="checkbox" data-sds-user="' + esc(m.userId) + '"'
        + ((cur.recipientUserIds || []).includes(m.userId) ? ' checked' : '') + '> ' + esc(m.name) + ' <span class="text-gray-400" style="font-size:11px">(' + esc(m.roleLabel) + ')</span> '
        + (m.lineBound ? '<span style="color:#16a34a;font-size:11px">ผูก LINE แล้ว</span>' : '<span style="color:#dc2626;font-size:11px">ยังไม่ผูก LINE</span>')
        + '</label>').join('');
      return '<div id="sdsForm" style="border:2px solid #4F46E5;border-radius:8px;padding:14px;margin-bottom:12px">'
        + '<div style="display:flex;gap:16px;flex-wrap:wrap;align-items:flex-end">'
        + '<label class="form-checkbox" style="font-size:13px"><input type="checkbox" id="sdsEnabled"' + (cur.enabled ? ' checked' : '') + '> เปิดใช้</label>'
        + '<div><label class="form-label">เวลาส่ง (เวลาไทย)</label><input type="time" step="300" class="form-input" id="sdsSendAt" value="' + esc(cur.sendAt) + '" style="max-width:140px"></div>'
        + '<div><label class="form-label">เว็บไซต์</label><select class="form-select" id="sdsSite" style="max-width:260px">' + siteOpts + '</select></div>'
        + '</div>'
        + '<div style="margin-top:12px"><div class="form-label">หัวข้อในสรุป</div>' + secs
        + '<div class="text-gray-400" style="font-size:11px">ผู้รับเห็นเฉพาะหัวข้อ/เว็บที่ตัวเองมีสิทธิ์ — ไม่มีสิทธิ์ = ไม่มีหัวข้อนั้นในข้อความของเขา</div></div>'
        + '<div style="margin-top:12px"><div class="form-label">ผู้รับตามบทบาท</div>' + roles + '</div>'
        + '<div style="margin-top:8px"><div class="form-label">ผู้รับรายคน</div><div style="max-height:220px;overflow:auto;border:1px solid #e2e8f0;border-radius:6px;padding:6px 10px">' + (users || '<span class="text-gray-400">ไม่มีสมาชิก</span>') + '</div>'
        + '<div style="font-size:12px;color:#b45309;margin-top:6px">' + esc(d.bindHowTo) + ' <a href="' + esc(d.bindPage) + '" target="_blank" rel="noopener">เปิดหน้าผูก LINE</a></div></div>'
        + '<div style="display:flex;gap:8px;margin-top:14px">'
        + '<button class="btn btn-primary" data-sds-act="save">บันทึก</button>'
        + '<button class="btn btn-outline" data-sds-act="cancel">ยกเลิก</button>'
        + '</div></div>';
    },

    historyHtml() {
      const rows = this.data.history || [];
      if (rows.length === 0) return '';
      return '<h4 style="font-size:14px;font-weight:600;margin:18px 0 8px">ประวัติการส่ง 7 วัน</h4>'
        + '<div style="overflow-x:auto"><table class="table" style="font-size:12px;min-width:560px"><thead><tr>'
        + '<th style="text-align:left">วันที่</th><th style="text-align:left">ผู้รับ</th><th style="text-align:left">ผล</th><th style="text-align:left">เหตุ</th></tr></thead><tbody>'
        + rows.map(h => '<tr><td>' + esc(h.date) + '</td><td>' + esc(h.name) + '</td><td>' + esc(h.statusLabel) + '</td><td>' + esc(h.error || '') + '</td></tr>').join('')
        + '</tbody></table></div>';
    },

    collect() {
      const form = document.getElementById('sdsForm');
      const pick = (attr) => Array.from(form.querySelectorAll('input[' + attr + ']')).filter(x => x.checked).map(x => x.getAttribute(attr));
      return {
        enabled: document.getElementById('sdsEnabled').checked,
        sendAt: document.getElementById('sdsSendAt').value,
        siteId: document.getElementById('sdsSite').value || null,
        sections: pick('data-sds-sec'),
        recipientRoles: pick('data-sds-role'),
        recipientUserIds: pick('data-sds-user'),
      };
    },

    async onClick(ev) {
      const btn = ev.target.closest('[data-sds-act]');
      if (!btn) return;
      const act = btn.getAttribute('data-sds-act');
      const id = btn.getAttribute('data-sds-id');
      try {
        if (act === 'new') { this.editing = {}; this.render(); return; }
        if (act === 'cancel') { this.editing = null; this.render(); return; }
        if (act === 'edit') { this.editing = (this.data.configs || []).find(c => c.id === id) || null; this.render(); return; }
        if (act === 'save') {
          const payload = this.collect();
          const editId = this.editing && this.editing.id;
          const j = await this.call(editId ? '/' + editId : '', { method: editId ? 'PUT' : 'POST', body: JSON.stringify(payload) });
          Layout.toast(j.message || 'บันทึกแล้ว');
          await this.load();
          return;
        }
        if (act === 'delete') {
          if (!confirm('ลบชุดสรุปนี้?')) return;
          const j = await this.call('/' + id, { method: 'DELETE' });
          Layout.toast(j.message || 'ลบแล้ว');
          await this.load();
          return;
        }
        if (act === 'test') {
          btn.disabled = true;
          const j = await this.call('/' + id + '/test', { method: 'POST' });
          Layout.toast(j.message || '', j.success ? undefined : 'error');
          btn.disabled = false;
        }
      } catch (e) {
        btn.disabled = false;
        Layout.toast(e.message, 'error');
      }
    },

    bind() {
      const wrap = document.getElementById('staffSummaryBody');
      if (!wrap || wrap.dataset.sdsBound) return;
      wrap.dataset.sdsBound = '1';
      wrap.addEventListener('click', (ev) => this.onClick(ev));
    },
  };

  window.StaffDailySummary = {
    load() { S.bind(); return S.load(); },
  };
})();
