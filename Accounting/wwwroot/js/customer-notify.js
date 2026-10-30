// รอบ 212 แพ็ก A (คำตัดสินข้อ 171) — ส่วนกลางของหน้า ตั้งค่า / ตั้งค่าร้านค้า / การใช้งาน / แพ็กเกจ / ส่วนเสริม
//   CustomerNotify.mountRules(el, { companyId, siteId })  ตาราง ประเภทข้อความ × LINE/อีเมล + ถอยไปอีเมลของระบบ (เฉพาะระดับบริษัท)
//   CustomerNotify.mountQuota(el, { companyId })          การ์ดโควตาข้อความผ่านช่องกลาง + ปุ่มซื้อแพ็ก
// เซิร์ฟเวอร์ส่งแคตตาล็อก · ป้าย · ค่าที่ resolve แล้ว · ข้อความเตือน (F2 #5) — ไฟล์นี้แสดงอย่างเดียว ไม่มีสำเนากติกา
(function () {
  'use strict';

  function esc(s) {
    return String(s == null ? '' : s).replace(/[&<>"']/g, function (c) {
      return { '&': '&amp;', '<': '&lt;', '>': '&gt;', '"': '&quot;', "'": '&#39;' }[c];
    });
  }

  function triVal(v) { return v === true ? 'true' : v === false ? 'false' : ''; }
  function parseTri(v) { return v === 'true' ? true : v === 'false' ? false : null; }

  function cellSelect(key, channel, own, eff, layer, disabled) {
    if (disabled) return '<td class="cn-cell" style="color:#94a3b8;font-size:12px">— (อีเมลเท่านั้น)</td>';
    var cur = triVal(own);
    return '<td class="cn-cell">'
      + '<select class="form-input cn-sel" data-key="' + esc(key) + '" data-ch="' + channel + '" style="min-width:120px;padding:4px 6px">'
      + '<option value=""' + (cur === '' ? ' selected' : '') + '>ตามค่าที่สืบทอด</option>'
      + '<option value="true"' + (cur === 'true' ? ' selected' : '') + '>ส่ง</option>'
      + '<option value="false"' + (cur === 'false' ? ' selected' : '') + '>ไม่ส่ง</option>'
      + '</select>'
      + '<div style="font-size:11px;color:' + (eff ? '#15803d' : '#b91c1c') + ';margin-top:2px">'
      + (eff ? '✓ ส่ง' : '✗ ไม่ส่ง') + ' · ' + esc(layer) + '</div></td>';
  }

  function row(r, isAll) {
    var hint = r.ownerSwitch
      ? '<div style="font-size:11px;color:#64748b">⚙️ ' + esc(r.ownerSwitch)
        + (r.ownerSwitchUrl ? ' · <a href="' + esc(r.ownerSwitchUrl) + '" style="color:#2563eb">ไปตั้งค่า</a>' : '') + '</div>'
      : '';
    return '<tr' + (isAll ? ' style="background:#f8fafc;font-weight:600"' : '') + '>'
      + '<td style="padding:6px 8px">' + esc(r.label) + (r.group ? ' <span style="font-size:11px;color:#94a3b8">(' + esc(r.group) + ')</span>' : '') + hint + '</td>'
      + cellSelect(r.key, 'line', r.lineEnabled, r.effectiveLine, r.lineLayer, !r.lineCapable)
      + cellSelect(r.key, 'email', r.emailEnabled, r.effectiveEmail, r.emailLayer, false)
      + '</tr>';
  }

  function renderRules(el, data, opts) {
    var siteMode = data.scope === 'site';
    var html = '<div style="font-size:12px;color:#64748b;margin-bottom:8px">' + esc(data.note) + '</div>';
    if (siteMode) html += '<div style="font-size:12px;color:#92400e;background:#fffbeb;border:1px solid #fde68a;border-radius:6px;padding:6px 10px;margin-bottom:8px">'
      + 'ค่าของเว็บ "' + esc(data.siteName) + '" ทับค่าของบริษัท · "ตามค่าที่สืบทอด" = ใช้ค่าของบริษัท</div>';
    html += '<div style="overflow-x:auto"><table style="width:100%;border-collapse:collapse;font-size:13px">'
      + '<thead><tr style="text-align:left;border-bottom:1px solid #e2e8f0"><th style="padding:6px 8px">ประเภทข้อความ</th><th>LINE</th><th>อีเมล</th></tr></thead><tbody>'
      + row(data.all, true)
      + data.events.map(function (e) { return row(e, false); }).join('')
      + '</tbody></table></div>'
      + '<div style="display:flex;gap:8px;flex-wrap:wrap;margin-top:10px">'
      + '<button type="button" class="btn btn-primary cn-save">บันทึกการแจ้งเตือนลูกค้า</button>'
      + '<button type="button" class="btn btn-outline cn-all">ส่งทั้งหมด (ล้างค่าที่ตั้งเฉพาะ)</button>'
      + '</div>';
    if (!siteMode && data.fallback) {
      var f = data.fallback;
      html += '<div class="form-group" style="margin-top:16px">'
        + '<label class="form-label">อีเมลถึงลูกค้าเมื่อผู้ส่งอีเมลของร้านล้ม / ยังไม่ตั้ง</label>'
        + '<select class="form-input cn-fallback">'
        + f.options.map(function (o) {
          return '<option value="' + esc(o.value) + '"' + (o.value === f.effective ? ' selected' : '') + '>' + esc(o.label) + '</option>';
        }).join('')
        + '</select>'
        + '<div style="font-size:11px;color:#64748b;margin-top:4px">ผู้ส่งอีเมลของร้าน: '
        + (f.tenantEmailConfigured ? 'ตั้งและทดสอบผ่านแล้ว' : 'ยังไม่ได้ตั้ง/ทดสอบ (ตั้งค่า → อีเมล)') + '</div></div>';
    }
    el.innerHTML = html;

    el.querySelector('.cn-all').addEventListener('click', function () {
      el.querySelectorAll('.cn-sel').forEach(function (s) {
        s.value = s.getAttribute('data-key') === '*' ? 'true' : '';
      });
    });
    el.querySelector('.cn-save').addEventListener('click', async function () {
      var byKey = {};
      el.querySelectorAll('.cn-sel').forEach(function (s) {
        var k = s.getAttribute('data-key');
        byKey[k] = byKey[k] || { eventKey: k, lineEnabled: null, emailEnabled: null };
        if (s.getAttribute('data-ch') === 'line') byKey[k].lineEnabled = parseTri(s.value);
        else byKey[k].emailEnabled = parseTri(s.value);
      });
      try {
        var res = await API.put('/api/companies/' + opts.companyId + '/customer-notify/rules',
          { siteId: opts.siteId || null, rules: Object.keys(byKey).map(function (k) { return byKey[k]; }) });
        if (res && res.data) renderRules(el, res.data, opts);
        if (window.Layout && Layout.toast) Layout.toast((res && res.message) || 'บันทึกแล้ว', 'success');
      } catch (e) {
        if (window.Layout && Layout.toast) Layout.toast(e.message || 'บันทึกไม่สำเร็จ', 'error');
      }
    });
    var fb = el.querySelector('.cn-fallback');
    if (fb) fb.addEventListener('change', async function () {
      try {
        var res = await API.put('/api/companies/' + opts.companyId + '/customer-notify/fallback', { value: fb.value });
        if (res && res.data) renderRules(el, res.data, opts);
        if (window.Layout && Layout.toast) Layout.toast((res && res.message) || 'บันทึกแล้ว', 'success');
      } catch (e) {
        if (window.Layout && Layout.toast) Layout.toast(e.message || 'บันทึกไม่สำเร็จ', 'error');
      }
    });
  }

  async function mountRules(el, opts) {
    if (!el || !opts || !opts.companyId) return;
    el.innerHTML = '<div style="color:#94a3b8;font-size:13px">กำลังโหลด…</div>';
    try {
      var q = opts.siteId ? '?siteId=' + encodeURIComponent(opts.siteId) : '';
      var res = await API.get('/api/companies/' + opts.companyId + '/customer-notify/rules' + q);
      renderRules(el, res.data, opts);
    } catch (e) {
      el.innerHTML = '<div style="color:#b91c1c;font-size:13px">โหลดค่าตั้งการแจ้งเตือนลูกค้าไม่สำเร็จ: ' + esc(e.message) + '</div>';
    }
  }

  function renderQuota(el, q, opts) {
    var limitText = q.unlimited ? 'ไม่จำกัด' : Number(q.effectiveLimit).toLocaleString('th-TH');
    var pct = q.unlimited || !q.effectiveLimit ? 0 : Math.min(100, Math.round(q.used * 100 / q.effectiveLimit));
    var color = q.warnLevel >= 2 ? '#dc2626' : q.warnLevel === 1 ? '#d97706' : '#16a34a';
    var html = '<div style="font-weight:600;margin-bottom:4px">💬 ข้อความแจ้งเตือนผ่านช่องกลาง (เดือน ' + esc(q.period) + ')</div>'
      + '<div style="font-size:13px">ใช้ไป <b>' + Number(q.used).toLocaleString('th-TH') + '</b> / ' + limitText + ' ข้อความ'
      + (q.bonusRemaining > 0 ? ' · โบนัสคงเหลือ ' + Number(q.bonusRemaining).toLocaleString('th-TH')
        + (q.bonusExpiresAt ? ' (ใช้ได้ถึง ' + new Date(q.bonusExpiresAt).toLocaleDateString('th-TH') + ')' : '') : '')
      + '</div>';
    if (!q.unlimited) html += '<div style="height:6px;background:#e2e8f0;border-radius:3px;margin:6px 0"><div style="height:6px;border-radius:3px;width:' + pct + '%;background:' + color + '"></div></div>';
    if (q.message) html += '<div style="font-size:12px;color:' + color + '">' + esc(q.message) + '</div>';
    html += '<div style="font-size:11px;color:#64748b;margin-top:4px">' + esc(q.meteredNote) + '</div>';
    if (q.packPrice != null) {
      html += '<button type="button" class="btn btn-outline cn-topup" style="margin-top:8px">ซื้อแพ็กเพิ่ม '
        + Number(q.packSize).toLocaleString('th-TH') + ' ข้อความ · ' + Number(q.packPrice).toLocaleString('th-TH', { minimumFractionDigits: 2 })
        + ' บาท (ใช้ได้ ' + q.packValidDays + ' วัน)</button>';
    }
    el.innerHTML = html;
    var btn = el.querySelector('.cn-topup');
    if (btn) btn.addEventListener('click', async function () {
      if (!confirm('ซื้อแพ็กข้อความเพิ่ม ' + q.packSize + ' ข้อความ? ค่าใช้จ่ายจะเข้าบิลรอบถัดไป')) return;
      try {
        var res = await API.post('/api/companies/' + opts.companyId + '/metering/notify-quota/topup', { packs: 1 });
        if (res && res.data) renderQuota(el, res.data, opts);
        if (window.Layout && Layout.toast) Layout.toast((res && res.message) || 'เพิ่มข้อความแล้ว', 'success');
      } catch (e) {
        if (window.Layout && Layout.toast) Layout.toast(e.message || 'ซื้อแพ็กไม่สำเร็จ', 'error');
      }
    });
  }

  async function mountQuota(el, opts) {
    if (!el || !opts || !opts.companyId) return;
    try {
      var res = await API.get('/api/companies/' + opts.companyId + '/metering/notify-quota');
      if (!res || !res.data) { el.style.display = 'none'; return; }
      renderQuota(el, res.data, opts);
    } catch (e) {
      el.innerHTML = '<div style="color:#b91c1c;font-size:13px">โหลดโควตาข้อความไม่สำเร็จ: ' + esc(e.message) + '</div>';
    }
  }

  window.CustomerNotify = { mountRules: mountRules, mountQuota: mountQuota };
})();
