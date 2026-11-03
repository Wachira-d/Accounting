/* รอบ 230 (erp-review/2026-11-02/CMS_SETTINGS_UX_AUDIT.md §6 ค้างข้อ 3 · แพ็ก F §1 ข้อ 6/11) — "ข้อมูลร้าน" และ "กฎหมาย / นโยบาย" ย้ายจากแท็บ SEO ของ cms-edit เข้าศูนย์ "ตั้งค่าเว็บ"
 * - ข้อมูลร้าน (CmsSiteSettingsExt.general): เบอร์/อีเมล/LINE/Facebook/Instagram ที่แสดงบนเว็บ (ว่าง = ใช้ของบริษัท) + รายการโทเคนที่ใช้ในเนื้อหา
 * - กฎหมาย / นโยบาย (CmsSiteSettingsExt.legal): ช่องยินยอม PDPA · แบนเนอร์คุกกี้ · นโยบายความเป็นส่วนตัว/ข้อกำหนด/คืนเงิน (แม่แบบ · เขียนเอง · ลิงก์ของร้าน) ·
 *   ค่าตั้งนโยบายคืนเงิน · ข้อความช่องติ๊กยอมรับนโยบาย
 * - บันทึกผ่าน PUT …/cms/sites/{id} เฉพาะช่องของการ์ดนั้น (null/ไม่ส่ง = เซิร์ฟเวอร์คงค่าเดิม · "" = ล้าง) — SEO/สาขา-คลังที่ยังอยู่ใน cms-edit ไม่ถูกแตะ
 * - โหมดนโยบาย/คำเตือน/ตัวอย่าง HTML ตัดสิน+encode ที่เซิร์ฟเวอร์ (Helpers/CmsLegalPages · LegalMarkdown) — ไฟล์นี้แสดง/ส่งค่าอย่างเดียว */
(function () {
  const esc = (v) => (window.Layout && Layout.esc ? Layout.esc(v) : String(v == null ? '' : v).replace(/[&<>"']/g, c => ({ '&': '&amp;', '<': '&lt;', '>': '&gt;', '"': '&quot;', "'": '&#39;' }[c])));
  const $ = (id) => document.getElementById(id);
  const KINDS = ['privacy', 'terms', 'refund'];
  const FIELD = {
    // รอบ 232 (232-Checkout-1): mdEn = ฉบับภาษาอังกฤษของข้อความที่ร้านเขียน (ฉบับเดียวกัน · ว่าง = ลูกค้าที่กด English เห็นไทย + ประกาศ)
    privacy: { url: 'privacyPolicyUrl', md: 'privacyPolicyMarkdown', mdEn: 'privacyPolicyMarkdownEn', mode: 'privacyLegalMode', title: '🔒 นโยบายความเป็นส่วนตัว' },
    terms: { url: 'termsOfServiceUrl', md: 'termsOfServiceMarkdown', mdEn: 'termsOfServiceMarkdownEn', mode: 'termsLegalMode', title: '📄 ข้อกำหนดการใช้บริการ' },
    refund: { url: 'refundPolicyUrl', md: 'refundPolicyMarkdown', mdEn: 'refundPolicyMarkdownEn', mode: 'refundLegalMode', title: '↩️ นโยบายการยกเลิกและคืนเงิน' },
  };
  const published = (st) => st === 'Published' || st === 1;
  const reset = (id) => { if (window.CmsDirtyGuard && $(id)) CmsDirtyGuard.reset($(id)); };

  const I = {
    _siteId: null, site: null,
    async _loadSite(siteId) {
      this._siteId = siteId;
      this.site = (await Layout.api().cmsGetSite(siteId)).data || {};
    },
    async _update(body, cardId, okMsg) {
      try {
        const saved = await Layout.api().cmsUpdateSite(this._siteId, body);
        if (saved && saved.data) this.site = saved.data;   // echo ค่าที่เซิร์ฟเวอร์เก็บจริง (โหมดนโยบายตัดสินที่เซิร์ฟเวอร์)
        Layout.toast(okMsg || 'บันทึกแล้ว', 'success');
        reset(cardId);
        return true;
      } catch (e) { Layout.toast(e.message, 'error', 10000); return false; }
    },

    // ═════ ข้อมูลร้าน ═════
    async mountGeneral(el, ctx) {
      await this._loadSite(ctx.siteId);
      const s = this.site; const id = encodeURIComponent(ctx.siteId || '');
      const badge = typeof ctx.scopeBadge === 'function' ? ctx.scopeBadge('site') : '';
      const inp = (fid, label, v, ph, type) => `<div class="form-group"><label class="form-label" for="${fid}">${label}</label><input type="${type || 'text'}" class="form-input" id="${fid}" value="${esc(v || '')}" placeholder="${esc(ph)}"></div>`;
      el.innerHTML = `<div class="sg-card" id="sgInfoCard"><h3>📞 ข้อมูลติดต่อที่แสดงบนเว็บ ${badge}</h3>
          <div class="sg-hint" style="margin-bottom:8px">เว้นว่าง = ใช้ข้อมูลของบริษัทจาก ตั้งค่าบริษัท · หน้า "ติดต่อเรา" และแม่แบบนโยบายดึงค่าพวกนี้ให้เอง — แก้ที่นี่ที่เดียว ทุกหน้าเปลี่ยนตามทันที</div>
          <div class="form-row">${inp('sgInfoPhone', 'เบอร์โทรติดต่อ', s.contactPhone, 'ใช้ของบริษัท')}${inp('sgInfoEmail', 'อีเมลติดต่อ', s.contactEmail, 'ใช้ของบริษัท', 'email')}</div>
          <div class="form-row">${inp('sgInfoLine', 'LINE ให้ลูกค้าทักแชต (แสดงบนเว็บ)', s.lineId, '@yourshop')}${inp('sgInfoFb', 'เพจ Facebook', s.facebookUrl, 'https://facebook.com/...')}</div>
          <div class="form-row">${inp('sgInfoIg', 'Instagram', s.instagramUrl, 'https://instagram.com/...')}<div></div></div>
          <details style="margin-bottom:10px"><summary style="cursor:pointer;font-size:12px;color:var(--gray-600)">ข้อความแทรกที่ใช้ได้ในเนื้อหาหน้าเว็บ (เช่น เบอร์ร้าน · ชื่อร้าน)</summary>
            <div id="sgInfoTokens" class="sg-hint" style="line-height:1.9;margin-top:6px">กำลังโหลด…</div></details>
          <button type="button" class="btn btn-primary btn-sm" onclick="CmsSiteInfoAdmin.saveContact()">บันทึกข้อมูลติดต่อ</button></div>
        <div class="sg-card"><h3>ข้อมูลร้านส่วนอื่น</h3>
          <div class="sg-link"><div><div style="font-weight:500">ชื่อเว็บ · ลิงก์เว็บของร้าน · โดเมน</div><div class="sg-hint">แก้ที่: หน้าเว็บ › โดเมน</div></div><a class="btn btn-sm btn-secondary" href="/pages/cms-edit.html?siteId=${id}&tab=domain">ไปที่นั่น →</a></div>
          <div class="sg-link"><div><div style="font-weight:500">ประเภทธุรกิจ / เติมแบบเว็บสำเร็จรูป</div><div class="sg-hint">แก้ที่: เว็บไซต์ทั้งหมด › การ์ดเว็บ › ⋯</div></div><a class="btn btn-sm btn-secondary" href="/pages/cms-sites.html">ไปที่นั่น →</a></div>
          <div class="sg-link"><div><div style="font-weight:500">สาขาที่ออกเอกสาร · คลังที่ตัดสต็อก (ผู้ทำบัญชี)</div><div class="sg-hint">แก้ที่: หน้าเว็บ › SEO · เครื่องมือวัดผล · สาขา/คลัง</div></div><a class="btn btn-sm btn-secondary" href="/pages/cms-edit.html?siteId=${id}&tab=seo">ไปที่นั่น →</a></div>
        </div>`;
      if (window.CmsDirtyGuard) CmsDirtyGuard.watch($('sgInfoCard'));
      try {
        const rows = (await API.get('/api/cms/content-tokens')).data || [];
        $('sgInfoTokens').innerHTML = rows.map(t => `<div><code>{{${esc(t.token)}}}</code> — ${esc(t.label)}</div>`).join('') || 'ไม่มีข้อมูล';
      } catch (_) { $('sgInfoTokens').textContent = 'โหลดรายการไม่สำเร็จ'; }
    },
    async saveContact() {
      // ส่งสตริงเสมอ ("" = ล้าง ⇒ กลับไปใช้ของบริษัท · null จะถูกอ่านว่า "ไม่ได้ส่ง" แล้วค่าเดิมค้าง)
      await this._update({ contactPhone: $('sgInfoPhone').value, contactEmail: $('sgInfoEmail').value, lineId: $('sgInfoLine').value,
        facebookUrl: $('sgInfoFb').value, instagramUrl: $('sgInfoIg').value }, 'sgInfoCard', 'บันทึกข้อมูลติดต่อแล้ว');
    },

    // ═════ กฎหมาย / นโยบาย ═════
    async mountLegal(el, ctx) {
      await this._loadSite(ctx.siteId);
      const badge = typeof ctx.scopeBadge === 'function' ? ctx.scopeBadge('site') : '';
      const box = (k) => `<div class="sg-card" id="sgLg_${k}"><h3>${FIELD[k].title}</h3>
          <div style="display:flex;gap:14px;flex-wrap:wrap;font-size:13px;margin-bottom:8px">
            ${[['default', 'ใช้แม่แบบของระบบ'], ['custom', 'เขียนเอง / ปรับแม่แบบ'], ['external', 'ใช้ลิงก์ของร้านเอง']].map(([v, l]) =>
              `<label style="display:flex;gap:6px;align-items:center"><input type="radio" name="sgLgMode_${k}" value="${v}" onchange="CmsSiteInfoAdmin.modeChanged('${k}')"> ${l}</label>`).join('')}
          </div>
          <div id="sgLgUrlWrap_${k}" style="display:none;margin-bottom:8px"><input type="text" class="form-input" id="sgLgUrl_${k}" maxlength="500" oninput="CmsSiteInfoAdmin.modeChanged('${k}', true)" placeholder="https://... หรือ /หน้าที่ร้านทำไว้"></div>
          <div id="sgLgMdWrap_${k}" style="display:none;margin-bottom:8px">
            <textarea class="form-input" id="sgLgMd_${k}" rows="12" spellcheck="false" style="font-family:ui-monospace,Menlo,Consolas,monospace;font-size:12px"></textarea>
            <div class="sg-hint">## หัวข้อ · - รายการ · **ตัวหนา** · [ข้อความ](https://...) · ข้อความแทรก เช่น {{company.name}} {{company.phone}} (ดูรายการที่ ข้อมูลร้าน) ·
              ${k === 'refund' ? 'เฉพาะนโยบายคืนเงิน: {{shop.depositForfeit}} {{shop.returnWindowDays}} {{shop.refundProcessingDays}} (ตัวเลขจากค่าตั้งด้านล่าง) · ' : ''}ไม่รับ HTML</div>
            <details style="margin-top:8px"><summary style="cursor:pointer;font-size:13px">🌐 ฉบับภาษาอังกฤษ (English version) — ไม่บังคับ</summary>
              <textarea class="form-input" id="sgLgMdEn_${k}" rows="10" spellcheck="false" style="font-family:ui-monospace,Menlo,Consolas,monospace;font-size:12px;margin-top:6px" placeholder="## 1. Heading&#10;Paragraph..."></textarea>
              <div class="sg-hint">ลูกค้ากด English ที่หน้านโยบาย (?lang=en) · เว้นว่าง = ยังไม่มีฉบับอังกฤษ ลูกค้าเห็นฉบับไทยพร้อมประกาศ "English version not available yet" · ฉบับไทยเป็นฉบับที่มีผล</div>
              <div class="sg-hint" style="color:var(--amber-700,#b45309)">⚠️ ข้อความอังกฤษช่องนี้ใช้เฉพาะเมื่อเลือก "เขียนเอง / ปรับแม่แบบ" — เลือก "ใช้แม่แบบของระบบ" แล้วบันทึก ⇒ ข้อความอังกฤษของร้านถูกล้าง และลูกค้าที่กด English เห็นคำแปลอังกฤษของแม่แบบระบบ (ฉบับไทยเป็นฉบับที่มีผล) · เขียนไทยเองแต่เว้นช่องนี้ว่าง ⇒ ลูกค้าเห็นไทย + ประกาศ (ระบบไม่เอาคำแปลแม่แบบมาคู่กับข้อความที่ร้านแก้) · "ใช้ลิงก์ของร้านเอง" ⇒ ลูกค้าไปหน้าของร้าน ไม่เห็นข้อความนี้</div>
            </details>
          </div>
          <div style="display:flex;gap:6px;align-items:center;flex-wrap:wrap;font-size:12px;background:var(--gray-50);border-radius:8px;padding:8px 10px">
            <span>🔗 ลิงก์ที่ลูกค้าเห็น:</span><code id="sgLgPub_${k}" style="flex:1;min-width:200px;word-break:break-all">-</code>
            <button type="button" class="btn btn-secondary btn-sm" onclick="CmsSiteInfoAdmin.copy('${k}')">คัดลอก</button>
            <a class="btn btn-secondary btn-sm" id="sgLgOpen_${k}" href="#" target="_blank" rel="noopener">เปิด</a>
            <button type="button" class="btn btn-secondary btn-sm" onclick="CmsSiteInfoAdmin.preview('${k}')">ดูตัวอย่าง</button>
          </div>
          <div id="sgLgWarn_${k}" style="font-size:12px;color:#92400e"></div>
          <div id="sgLgPreview_${k}" style="display:none;max-height:420px;overflow:auto;border:1px solid var(--gray-200);border-radius:8px;padding:12px 16px;margin-top:8px;font-size:13px;line-height:1.8"></div>
          <div style="margin-top:8px"><button type="button" class="btn btn-primary btn-sm" onclick="CmsSiteInfoAdmin.saveLegal('${k}')">บันทึก${FIELD[k].title.replace(/^\S+\s/, '')}</button></div></div>`;
      el.innerHTML = `<div class="sg-card" id="sgLgConsent"><h3>🔒 ข้อมูลส่วนบุคคล (PDPA) ${badge}</h3>
          <div class="sg-hint" style="margin-bottom:8px">ร้านเป็นเจ้าของข้อมูลลูกค้า (ผู้ควบคุมข้อมูลส่วนบุคคล) — ระบบช่วยแสดงนโยบายก่อนเก็บข้อมูลและเก็บหลักฐานความยินยอมให้ (PDPA ม.23) ·
            ลิงก์นโยบายแสดงท้ายเว็บทุกหน้า · บนช่องยินยอมของฟอร์มสั่งซื้อ/จอง/ติดต่อ · หน้าสมัครสมาชิก · แบนเนอร์คุกกี้</div>
          <div id="sgLgWarnings"></div>
          <label style="font-size:13px;display:flex;gap:6px;align-items:center"><input type="checkbox" id="sgLgRequire"> บังคับติ๊กยินยอมก่อนส่งฟอร์ม (ตรวจที่เซิร์ฟเวอร์)</label>
          <label style="font-size:13px;display:flex;gap:6px;align-items:center;margin-top:6px"><input type="checkbox" id="sgLgCookie"> แสดงแบนเนอร์คุกกี้ (สคริปต์วัดผลโหลดหลังลูกค้ากดยอมรับ)</label>
          <div class="form-group" style="margin-top:8px"><label class="form-label" for="sgLgConsentText">ข้อความยินยอม (เว้นว่าง = ข้อความมาตรฐาน)</label>
            <textarea class="form-input" id="sgLgConsentText" rows="2" placeholder="ข้าพเจ้ายินยอมให้ร้านเก็บและใช้ข้อมูลส่วนบุคคลเพื่อดำเนินการตามคำสั่งซื้อ/การจอง ตามนโยบายความเป็นส่วนตัว"></textarea></div>
          <button type="button" class="btn btn-primary btn-sm" onclick="CmsSiteInfoAdmin.saveConsent()">บันทึกการยินยอม</button></div>
        ${KINDS.map(box).join('')}
        <div class="sg-card" id="sgLgRefund"><h3>⚙️ ค่าตั้งนโยบายคืนเงิน และช่องติ๊กยอมรับนโยบาย</h3>
          <div class="form-row">
            <div class="form-group"><label class="form-label" for="sgLgReturnDays">แจ้งคืนสินค้าได้ภายใน (วัน)</label><input type="number" min="0" max="365" class="form-input" id="sgLgReturnDays" placeholder="7"></div>
            <div class="form-group"><label class="form-label" for="sgLgRefundDays">คืนเงินภายใน (วันหลังอนุมัติ)</label><input type="number" min="0" max="365" class="form-input" id="sgLgRefundDays" placeholder="14"></div>
          </div>
          <div class="sg-hint" style="margin:-4px 0 8px">เว้นว่าง/0 = ค่าเริ่มต้น · การริบมัดจำเมื่อยกเลิกตั้งที่ "การชำระเงิน" / "สินค้าตามสั่ง" (แม่แบบอ่านค่านั้นเอง)</div>
          <div class="form-group"><label class="form-label" for="sgLgAccept">ข้อความช่องติ๊ก "ยอมรับนโยบาย" บนหน้าสั่งซื้อ/จอง (เว้นว่าง = ข้อความมาตรฐาน)</label>
            <input type="text" class="form-input" id="sgLgAccept" maxlength="500" placeholder="ข้าพเจ้าได้อ่านและยอมรับข้อกำหนดการใช้บริการ และนโยบายการยกเลิกและคืนเงินของร้าน">
            <div class="sg-hint">ลูกค้าต้องติ๊กทุกครั้งก่อนยืนยัน (ระบบเก็บฉบับที่ลูกค้ายอมรับไว้เป็นหลักฐาน) — พนักงานบันทึกแทนลูกค้าไม่ต้องติ๊ก</div></div>
          <button type="button" class="btn btn-primary btn-sm" onclick="CmsSiteInfoAdmin.saveRefund()">บันทึก</button></div>`;
      this.hydrateLegal();
      if (window.CmsDirtyGuard) ['sgLgConsent', 'sgLg_privacy', 'sgLg_terms', 'sgLg_refund', 'sgLgRefund'].forEach(i => CmsDirtyGuard.watch($(i)));
    },
    hydrateLegal(only) {
      const s = this.site;
      const warns = s.legalWarnings || [];   // ฝ่ายค้านรอบ 214: ตัวตนร้านที่ขาด · ลิงก์เดิมที่ใช้ไม่ได้ (ไม่ลบเงียบ)
      $('sgLgWarnings').innerHTML = warns.length ? `<div style="background:#fffbeb;border:1px solid #fcd34d;color:#92400e;border-radius:8px;padding:8px 12px;font-size:12px;line-height:1.7;margin-bottom:10px">${warns.map(w => '⚠️ ' + esc(w)).join('<br>')}</div>` : '';
      if (!only || only === 'consent') {
        $('sgLgRequire').checked = !!s.requireDataConsent;
        $('sgLgCookie').checked = s.cookieConsentEnabled !== false;
        $('sgLgConsentText').value = s.dataConsentText || '';
      }
      for (const k of KINDS) {
        if (only && only !== k) continue;
        const f = FIELD[k];
        const r = document.querySelector(`input[name="sgLgMode_${k}"][value="${s[f.mode] || 'default'}"]`); if (r) r.checked = true;
        $('sgLgUrl_' + k).value = s[f.url] || '';
        $('sgLgMd_' + k).value = s[f.md] || '';
        $('sgLgMdEn_' + k).value = s[f.mdEn] || '';   // รอบ 232: hydrate ฉบับอังกฤษ (เก็บแล้วต้อง echo กลับ)
        $('sgLgPreview_' + k).style.display = 'none';
        $('sgLgWarn_' + k).textContent = '';
        this.modeChanged(k, true);
      }
      if (!only || only === 'refund-settings') {
        $('sgLgReturnDays').value = s.refundReturnWindowDays ?? '';
        $('sgLgRefundDays').value = s.refundProcessingDays ?? '';
        $('sgLgAccept').value = s.policyAcceptanceText || '';
      }
    },
    _mode(k) { const el = document.querySelector(`input[name="sgLgMode_${k}"]:checked`); return el ? el.value : 'default'; },
    _internalUrl(k) { return `${location.origin}/site/${encodeURIComponent(this.site.subdomain || '')}/legal/${k}`; },
    async modeChanged(k, fromHydrate) {
      const mode = this._mode(k);
      $('sgLgUrlWrap_' + k).style.display = mode === 'external' ? '' : 'none';
      $('sgLgMdWrap_' + k).style.display = mode === 'custom' ? '' : 'none';
      const ext = $('sgLgUrl_' + k).value.trim();
      const shown = mode === 'external' && ext ? (ext.startsWith('/') ? `${location.origin}/site/${encodeURIComponent(this.site.subdomain || '')}${ext}` : ext) : this._internalUrl(k);
      $('sgLgPub_' + k).textContent = shown;
      const open = $('sgLgOpen_' + k);
      open.href = shown;
      // เว็บยังไม่เผยแพร่ ⇒ หน้าสาธารณะยังเปิดไม่ได้ — แสดงตัวอย่างในหน้านี้แทน (ไม่พาไปหน้า 404)
      open.onclick = (mode !== 'external' && !published(this.site.status))
        ? (ev) => { ev.preventDefault(); Layout.toast('เว็บยังไม่เปิดให้ลูกค้าเห็น — แสดงตัวอย่างในหน้านี้แทน', 'info'); this.preview(k); } : null;
      const md = $('sgLgMd_' + k);
      if (!fromHydrate && mode === 'custom' && !md.value.trim()) {   // เลือก "เขียนเอง" ครั้งแรก ⇒ ตั้งต้นจากแม่แบบ
        try { const res = await Layout.api().cmsLegalPreview(this._siteId, k, ''); md.value = (res.data && res.data.markdown) || ''; }
        catch (e) { Layout.toast('โหลดแม่แบบไม่สำเร็จ: ' + e.message, 'error'); }
      }
    },
    async copy(k) {
      const url = $('sgLgPub_' + k).textContent;
      try { await navigator.clipboard.writeText(url); Layout.toast('คัดลอกลิงก์แล้ว', 'success'); } catch (_) { prompt('คัดลอกลิงก์นี้', url); }
    },
    async preview(k) {
      const mode = this._mode(k);
      const box = $('sgLgPreview_' + k);
      try {
        const res = await Layout.api().cmsLegalPreview(this._siteId, k, mode === 'custom' ? $('sgLgMd_' + k).value : '');
        box.innerHTML = (res.data && res.data.html) || '';   // HTML แปลง + encode ที่เซิร์ฟเวอร์ (ตัวเดียวกับหน้าสาธารณะ)
        box.style.display = '';
        $('sgLgWarn_' + k).innerHTML = ((res.data && res.data.warnings) || []).map(x => '⚠️ ' + esc(x)).join('<br>');
        if (mode === 'external') Layout.toast('ร้านเลือกใช้ลิงก์ของร้านเอง — ตัวอย่างนี้คือแม่แบบสำรองของระบบ', 'info');
      } catch (e) { Layout.toast('ดูตัวอย่างไม่สำเร็จ: ' + e.message, 'error'); }
    },
    async saveConsent() {
      if (await this._update({ requireDataConsent: $('sgLgRequire').checked, cookieConsentEnabled: $('sgLgCookie').checked, dataConsentText: $('sgLgConsentText').value },
        'sgLgConsent', 'บันทึกการยินยอมแล้ว')) this.hydrateLegal('consent');
    },
    async saveLegal(k) {
      const f = FIELD[k]; const mode = this._mode(k);
      const body = {};
      body[f.url] = mode === 'external' ? $('sgLgUrl_' + k).value.trim() : '';
      body[f.md] = mode === 'default' ? '' : $('sgLgMd_' + k).value;   // ลิงก์ของร้าน: เก็บข้อความที่เขียนไว้ต่อ (สลับกลับมาไม่หาย) · แม่แบบ: ล้าง
      body[f.mdEn] = mode === 'default' ? '' : $('sgLgMdEn_' + k).value;   // รอบ 232: ฉบับอังกฤษตามฉบับไทย (แม่แบบ = ไม่มีฉบับอังกฤษของร้าน · รอบ 232 ต่อ: หน้าร้านใช้คำแปลแม่แบบของระบบแทน — CmsLegalPages.EffectiveMarkdownEn)
      if (await this._update(body, 'sgLg_' + k, 'บันทึกนโยบายแล้ว')) this.hydrateLegal(k);
    },
    async saveRefund() {
      if (await this._update({ refundReturnWindowDays: Number($('sgLgReturnDays').value || 0), refundProcessingDays: Number($('sgLgRefundDays').value || 0),
        policyAcceptanceText: $('sgLgAccept').value.trim() }, 'sgLgRefund', 'บันทึกแล้ว')) this.hydrateLegal('refund-settings');
    },
  };
  window.CmsSiteInfoAdmin = I;
  window.CmsSiteSettingsExt = window.CmsSiteSettingsExt || {};
  window.CmsSiteSettingsExt.general = (el, ctx) => I.mountGeneral(el, ctx);
  window.CmsSiteSettingsExt.legal = (el, ctx) => I.mountLegal(el, ctx);
})();
