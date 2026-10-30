// รอบ 212 แพ็ก A (คำตัดสินข้อ 171) — ส่วนกลางของหน้า ตั้งค่า / ตั้งค่าร้านค้า / การใช้งาน / แพ็กเกจ / ส่วนเสริม
//   CustomerNotify.mountRules(el, { companyId, siteId })  ตาราง ประเภทข้อความ × LINE/อีเมล + ถอยไปอีเมลของระบบ (เฉพาะระดับบริษัท)
//   CustomerNotify.mountQuota(el, { companyId })          การ์ดโควตาข้อความผ่านช่องกลาง + ปุ่มซื้อแพ็ก + จำนวนข้อความค้างส่ง (รอบ 213)
//   CustomerNotify.mountPending(el, { companyId, onChange }) รอบ 213: ข้อความที่ยังไม่ได้ส่งเพราะโควตาเต็ม + ส่งซ้ำ (ไม่ส่งเองโดยไม่กด)
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
    var html = '<div style="font-size:12px;color:#64748b;margin-bottom:8px">' + esc(data.note)
      + ' · <a href="' + PENDING_HREF + '" style="color:#2563eb">ข้อความที่ค้างส่งเพราะโควตาเต็ม / ส่งซ้ำ</a></div>';
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

  var PENDING_HREF = '/pages/addons.html#notifyPending';

  // รอบ 213: บรรทัด "มีข้อความค้างส่ง N รายการ" ใต้การ์ดโควตา — หลังซื้อแพ็กถามว่าจะส่งตอนนี้ไหม (พาไปหน้าจัดการ · ไม่ส่งเองโดยไม่กด)
  async function renderPendingLine(el, opts, afterTopUp) {
    var box = el.querySelector('.cn-pending-line');
    if (!box) return;
    try {
      var res = await API.get('/api/companies/' + opts.companyId + '/customer-notify/pending');
      var d = res && res.data;
      if (!d || !d.resendableCount) { box.innerHTML = ''; return; }
      var href = opts.pendingHref || PENDING_HREF;
      box.innerHTML = '<div style="margin-top:8px;font-size:13px;padding:8px 10px;border-radius:8px;'
        + (afterTopUp ? 'background:#ecfdf5;border:1px solid #a7f3d0;color:#065f46' : 'background:#fffbeb;border:1px solid #fde68a;color:#92400e') + '">'
        + (afterTopUp ? '✅ เติมโควตาแล้ว · ' : '⏳ ')
        + 'มีข้อความถึงลูกค้าค้างส่ง <b>' + Number(d.resendableCount).toLocaleString('th-TH') + '</b> รายการ'
        + (afterTopUp ? ' — ส่งตอนนี้ไหม? ' : ' (ค้างเพราะโควตาเต็ม) ')
        + '<a href="' + esc(href) + '" style="color:#2563eb;font-weight:600">' + (afterTopUp ? 'ไปเลือกส่งซ้ำ' : 'จัดการ / ส่งซ้ำ') + '</a></div>';
      if (afterTopUp && typeof opts.onPendingPrompt === 'function') opts.onPendingPrompt(d);
    } catch (e) {
      // ไม่มีสิทธิ์ค่าตั้งการแจ้งเตือน (403) ⇒ ไม่ใช่เรื่องของผู้ใช้คนนี้ ไม่แสดงบรรทัด · ล้มอย่างอื่น ⇒ บอกเหตุ ไม่เงียบ
      box.innerHTML = e && e.status === 403 ? ''
        : '<div style="font-size:11px;color:#94a3b8;margin-top:6px">ตรวจข้อความค้างส่งไม่ได้: ' + esc(e.message) + '</div>';
    }
  }

  function renderQuota(el, q, opts, afterTopUp) {
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
    html += '<div class="cn-pending-line"></div>';
    el.innerHTML = html;
    renderPendingLine(el, opts, !!afterTopUp);
    var btn = el.querySelector('.cn-topup');
    if (btn) btn.addEventListener('click', async function () {
      if (!confirm('ซื้อแพ็กข้อความเพิ่ม ' + Number(q.packSize).toLocaleString('th-TH') + ' ข้อความ ราคา '
        + Number(q.packPrice).toLocaleString('th-TH', { minimumFractionDigits: 2 }) + ' บาท? ค่าใช้จ่ายจะเข้าบิลรอบถัดไป')) return;
      btn.disabled = true;
      try {
        // ฝ่ายค้านรอบ 213 S4: ส่งข้อเสนอที่เห็นไปด้วย — เซิร์ฟเวอร์เทียบใต้ล็อก ไม่ตรง ⇒ 409 (ยังไม่คิดเงิน)
        var res = await API.post('/api/companies/' + opts.companyId + '/metering/notify-quota/topup',
          { packs: 1, expectedPackSize: q.packSize, expectedPackPrice: q.packPrice });
        if (res && res.data) renderQuota(el, res.data, opts, true);
        if (window.Layout && Layout.toast) Layout.toast((res && res.message) || 'เพิ่มข้อความแล้ว', 'success');
        // ฝ่ายค้านรอบ 213 (f): ข้อความค้างส่งบนหน้าเดียวกันต้องเห็นโควตาใหม่
        if (typeof opts.onTopUp === 'function') opts.onTopUp(res && res.data);
      } catch (e) {
        btn.disabled = false;
        if (e && e.status === 409 && e.body && e.body.data) {
          // ขนาด/ราคาเปลี่ยน ⇒ วาดข้อเสนอใหม่ให้เห็นก่อน แล้วให้ผู้ใช้กดยืนยันอีกครั้งเอง
          renderQuota(el, e.body.data, opts, false);
          if (window.Layout && Layout.toast) Layout.toast(e.message || 'ขนาด/ราคาแพ็กเปลี่ยน — กรุณาตรวจแล้วกดซื้ออีกครั้ง', 'warning');
          return;
        }
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

  // ═════════ รอบ 213: ข้อความที่ยังไม่ได้ส่ง (ค้างเพราะโควตาเต็ม) ═════════
  // เซิร์ฟเวอร์ตัดสินว่าแถวไหนส่งซ้ำได้ + เหตุ + ป้ายทุกตัว (CustomerNotifyResend.Evaluate) — ที่นี่แสดง/กรองการแสดงผล/ส่งรหัสที่เลือกเท่านั้น
  function fmtDate(v) {
    try { return new Date(v).toLocaleString('th-TH', { dateStyle: 'medium', timeStyle: 'short' }); } catch (e) { return String(v || ''); }
  }

  function renderPending(el, d, opts, state) {
    state = state || { onlyResendable: true, eventKey: '' };
    var items = d.items || [];
    var events = [];
    items.forEach(function (i) { if (!events.some(function (e) { return e.key === i.eventKey; })) events.push({ key: i.eventKey, label: i.eventLabel }); });
    var shown = items.filter(function (i) {
      return (!state.onlyResendable || i.resendable) && (!state.eventKey || i.eventKey === state.eventKey);
    });
    var q = d.quota;
    var html = '<div style="font-weight:600;margin-bottom:4px">📭 ข้อความถึงลูกค้าที่ยังไม่ได้ส่ง (ค้างเพราะโควตาเต็ม)</div>'
      + '<div style="font-size:12px;color:#64748b;margin-bottom:8px">ทั้งหมด ' + d.total + ' รายการ · ส่งซ้ำได้ <b>' + d.resendableCount + '</b> รายการ'
      + (q ? ' · โควตาเหลือ ' + (q.unlimited ? 'ไม่จำกัด' : Number(q.remaining).toLocaleString('th-TH') + ' ข้อความ') : '')
      + ' · ระบบประกอบข้อความใหม่จากสถานะล่าสุดของการจอง/คำสั่งซื้อ (ไม่ส่งเองจนกว่าจะกด)</div>';
    if (d.hint) html += '<div style="font-size:12px;color:#92400e;background:#fffbeb;border:1px solid #fde68a;border-radius:6px;padding:6px 10px;margin-bottom:8px">'
      + esc(d.hint) + ' · <a href="/pages/addons.html#notifyQuotaWrap" style="color:#2563eb">ซื้อแพ็กข้อความ</a></div>';
    if (!items.length) {
      el.innerHTML = html + '<div style="font-size:13px;color:#94a3b8">ไม่มีข้อความค้างส่ง</div>';
      return;
    }
    html += '<div style="display:flex;gap:8px;flex-wrap:wrap;align-items:center;margin-bottom:8px">'
      + '<select class="form-input cn-p-event" style="max-width:240px;padding:4px 6px" aria-label="กรองประเภทข้อความ"><option value="">ทุกประเภท</option>'
      + events.map(function (e) { return '<option value="' + esc(e.key) + '"' + (e.key === state.eventKey ? ' selected' : '') + '>' + esc(e.label) + '</option>'; }).join('')
      + '</select>'
      + '<label style="font-size:13px;display:flex;gap:4px;align-items:center"><input type="checkbox" class="cn-p-only"' + (state.onlyResendable ? ' checked' : '') + '> เฉพาะที่ส่งซ้ำได้</label>'
      + '<button type="button" class="btn btn-primary cn-p-send-sel">ส่งซ้ำที่เลือก</button>'
      + '<button type="button" class="btn btn-outline cn-p-send-all"' + (d.resendableCount ? '' : ' disabled') + '>ส่งซ้ำทั้งหมดที่ส่งได้ (' + d.resendableCount + ')</button>'
      + '</div>'
      + '<div style="overflow-x:auto"><table style="width:100%;border-collapse:collapse;font-size:13px">'
      + '<thead><tr style="text-align:left;border-bottom:1px solid #e2e8f0"><th style="padding:6px 8px;width:28px"><input type="checkbox" class="cn-p-all" aria-label="เลือกทั้งหมด"></th>'
      + '<th>ประเภท</th><th>รายการ</th><th>เมื่อ</th><th>สถานะ</th></tr></thead><tbody>'
      + (shown.length ? shown.map(function (i) {
        return '<tr style="border-bottom:1px solid #f1f5f9">'
          + '<td style="padding:6px 8px">' + (i.resendable ? '<input type="checkbox" class="cn-p-chk" data-id="' + esc(i.logId) + '" aria-label="เลือก">' : '') + '</td>'
          + '<td>' + esc(i.eventLabel) + '</td>'
          + '<td>' + esc(i.targetLabel) + '</td>'
          + '<td style="white-space:nowrap">' + esc(fmtDate(i.createdAt)) + '</td>'
          + '<td style="font-size:12px;color:' + (i.resendable ? '#15803d' : '#b91c1c') + '">' + (i.resendable ? '✓ ' : '✗ ') + esc(i.verdictLabel)
          + (i.resendCount ? '<div style="color:#64748b">กดส่งซ้ำแล้ว ' + i.resendCount + ' ครั้ง</div>' : '') + '</td></tr>';
      }).join('') : '<tr><td colspan="5" style="padding:10px;color:#94a3b8">ไม่มีรายการตามตัวกรอง</td></tr>')
      + '</tbody></table></div><div class="cn-p-result" style="margin-top:8px"></div>';
    el.innerHTML = html;

    el.querySelector('.cn-p-event').addEventListener('change', function (ev) { state.eventKey = ev.target.value; renderPending(el, d, opts, state); });
    el.querySelector('.cn-p-only').addEventListener('change', function (ev) { state.onlyResendable = ev.target.checked; renderPending(el, d, opts, state); });
    var all = el.querySelector('.cn-p-all');
    if (all) all.addEventListener('change', function () {
      el.querySelectorAll('.cn-p-chk').forEach(function (c) { c.checked = all.checked; });
    });

    async function send(ids, btn) {
      var buttons = el.querySelectorAll('.cn-p-send-sel, .cn-p-send-all');
      buttons.forEach(function (b) { b.disabled = true; });
      var original = btn.textContent;
      btn.textContent = 'กำลังส่ง…';
      try {
        var res = await API.post('/api/companies/' + opts.companyId + '/customer-notify/pending/resend', { logIds: ids });
        var r = res && res.data;
        await mountPending(el, opts, state);
        var box = el.querySelector('.cn-p-result');
        // ฝ่ายค้านรอบ 213 S5: แสดงรายการที่ไม่สำเร็จ/ข้าม/กำลังส่ง พร้อมเหตุจากเซิร์ฟเวอร์ (แถวที่ล้มยังอยู่ในรายการให้ลองใหม่)
        var labels = {}; (d.items || []).forEach(function (i) { labels[i.logId] = i.eventLabel + ' · ' + i.targetLabel; });
        var problems = (r && r.items || []).filter(function (x) { return x.outcome !== 'sent'; });
        if (box && r) box.innerHTML = '<div style="font-size:13px;padding:8px 10px;border-radius:8px;'
          + (r.stoppedForQuota || r.failed ? 'background:#fef2f2;border:1px solid #fecaca;color:#991b1b' : 'background:#ecfdf5;border:1px solid #a7f3d0;color:#065f46') + '">'
          + esc(r.message) + (r.topUpUrl ? ' · <a href="' + esc(r.topUpUrl) + '" style="color:#2563eb;font-weight:600">ซื้อแพ็กข้อความ</a>' : '')
          + (problems.length ? '<ul style="margin:6px 0 0 18px;font-size:12px">' + problems.map(function (x) {
              return '<li>' + esc(labels[x.logId] || x.logId) + ' — ' + esc(x.detail || x.outcome) + '</li>';
            }).join('') + '</ul>' : '')
          + '</div>';
        if (typeof opts.onChange === 'function') opts.onChange(r);
      } catch (e) {
        buttons.forEach(function (b) { b.disabled = false; });
        btn.textContent = original;   // ฝ่ายค้านรอบ 213 (f): ปุ่มกลับเป็นข้อความเดิม
        if (window.Layout && Layout.toast) Layout.toast(e.message || 'ส่งซ้ำไม่สำเร็จ', 'error');
      }
    }
    var sel = el.querySelector('.cn-p-send-sel');
    sel.addEventListener('click', function () {
      var ids = [];
      el.querySelectorAll('.cn-p-chk:checked').forEach(function (c) { ids.push(c.getAttribute('data-id')); });
      if (!ids.length) { if (window.Layout && Layout.toast) Layout.toast('เลือกรายการที่จะส่งซ้ำก่อน', 'warning'); return; }
      if (!confirm('ส่งซ้ำ ' + ids.length + ' รายการ? จะใช้โควตาข้อความตามปกติ')) return;
      send(ids, sel);
    });
    var allBtn = el.querySelector('.cn-p-send-all');
    allBtn.addEventListener('click', function () {
      if (!confirm('ส่งซ้ำทุกรายการที่ส่งได้ (' + d.resendableCount + ' รายการ)? จะใช้โควตาข้อความตามปกติ และหยุดเมื่อโควตาหมด')) return;
      send([], allBtn);
    });
  }

  async function mountPending(el, opts, state) {
    if (!el || !opts || !opts.companyId) return;
    try {
      var res = await API.get('/api/companies/' + opts.companyId + '/customer-notify/pending');
      renderPending(el, (res && res.data) || { items: [], total: 0, resendableCount: 0 }, opts, state);
    } catch (e) {
      el.innerHTML = '<div style="color:#b91c1c;font-size:13px">โหลดข้อความค้างส่งไม่สำเร็จ: ' + esc(e.message) + '</div>';
    }
  }

  window.CustomerNotify = { mountRules: mountRules, mountQuota: mountQuota, mountPending: mountPending };
})();
