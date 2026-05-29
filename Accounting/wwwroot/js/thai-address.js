// Thai address autocomplete widget — enhances a tambon / amphur / province /
// postal input group with cascading datalist suggestions, reverse postal
// lookup, alias normalization, and a no-match inline warning.
//
// Backend API already serves data at /api/gov/address (provinces, districts,
// subdistricts, postal). We cache in-memory so a typical form fills in 3–5
// fetches total regardless of how many times the user opens / closes the
// modal. The widget is idempotent — calling enhance() on the same inputs is
// a no-op (tracked via WeakSet).
//
// Usage:
//   ThaiAddress.enhance({
//     subDistrict: 'fSubDistrict',
//     district:    'fDistrict',
//     province:    'fProvince',
//     postalCode:  'fPostalCode',
//   });
//
// Public extras (useful after a free-text paste-parse):
//   ThaiAddress.refresh(ids)              — re-run cascade given current values
//   ThaiAddress.normalize(text, kind)     — collapse aliases (e.g. "กทม" → "กรุงเทพมหานคร")

const ThaiAddress = (() => {
  const BASE = '/api/gov/address';
  const cache = {
    provinces: null,
    districts: {},          // provinceCode -> []
    subdistricts: {},       // districtCode -> []
    postal: {},             // postalCode   -> []
  };
  const wired = new WeakSet();

  // Common province aliases. Most provinces resolve cleanly by Thai name; the
  // capital is the one that arrives in five different forms depending on
  // who's typing.
  const PROVINCE_ALIASES = {
    'กรุงเทพ':      'กรุงเทพมหานคร',
    'กรุงเทพฯ':     'กรุงเทพมหานคร',
    'กรุงเทพมหานคร': 'กรุงเทพมหานคร',
    'กทม':         'กรุงเทพมหานคร',
    'กทม.':        'กรุงเทพมหานคร',
    'bkk':         'กรุงเทพมหานคร',
    'bangkok':     'กรุงเทพมหานคร',
  };

  async function _fetch(path) {
    const res = await fetch(BASE + path);
    if (!res.ok) {
      // Log so missing-data symptoms ("ไม่ขึ้นอะไรเลย") are debuggable from
      // the browser console instead of failing silently.
      console.warn('[ThaiAddress] ' + BASE + path + ' → HTTP ' + res.status);
      throw new Error('address api ' + res.status);
    }
    const j = await res.json();
    return j.data || j;
  }

  async function getProvinces() {
    if (!cache.provinces) cache.provinces = await _fetch('/provinces');
    return cache.provinces;
  }
  async function getDistricts(provCode) {
    if (!cache.districts[provCode]) cache.districts[provCode] = await _fetch('/districts/' + provCode);
    return cache.districts[provCode];
  }
  async function getSubDistricts(distCode) {
    if (!cache.subdistricts[distCode]) cache.subdistricts[distCode] = await _fetch('/subdistricts/' + distCode);
    return cache.subdistricts[distCode];
  }
  async function getByPostal(code) {
    if (!cache.postal[code]) cache.postal[code] = await _fetch('/postal/' + code);
    return cache.postal[code];
  }

  // Normalize a province alias to its canonical name. Falls through unchanged
  // when no alias matches — non-Bangkok provinces almost always have a single
  // canonical form so the dictionary stays small.
  function normalize(text, kind) {
    if (!text) return text;
    const trimmed = String(text).trim();
    if (kind === 'province') {
      const key = trimmed.toLowerCase();
      return PROVINCE_ALIASES[trimmed] || PROVINCE_ALIASES[key] || trimmed;
    }
    return trimmed;
  }

  function _ensureList(input, listId) {
    if (input.getAttribute('list') === listId) return;
    let dl = document.getElementById(listId);
    if (!dl) {
      dl = document.createElement('datalist');
      dl.id = listId;
      document.body.appendChild(dl);
    }
    input.setAttribute('list', listId);
    // NOT setting autocomplete="off" — Chrome can suppress datalist
    // suggestions on inputs with that attribute. The browser's own address
    // autocomplete is harmless on a datalist-backed field; it just doesn't
    // fight for the same dropdown space.
  }

  function _fillList(listId, names) {
    const dl = document.getElementById(listId);
    if (!dl) return;
    const seen = new Set();
    dl.innerHTML = names
      .filter(n => { if (seen.has(n)) return false; seen.add(n); return true; })
      .map(n => `<option value="${String(n).replace(/"/g, '&quot;')}">`)
      .join('');
  }

  function _findByThai(list, value) {
    if (!value) return null;
    const v = String(value).trim();
    return list.find(x => (x.nameTh || x.subDistrictNameTh) === v) || null;
  }

  // Inline status note attached just below the postal field — shown when a
  // 5-digit code has zero matches (typo) or when normalization rewrote a
  // province alias. Less intrusive than a toast.
  function _setNote(input, message, level) {
    const id = '_thAddrNote_' + input.id;
    let el = document.getElementById(id);
    if (!message) { if (el) el.remove(); return; }
    if (!el) {
      el = document.createElement('div');
      el.id = id;
      el.style.cssText = 'font-size:11px;margin-top:4px;line-height:1.4';
      input.insertAdjacentElement('afterend', el);
    }
    el.style.color = level === 'warn' ? '#b45309' : level === 'ok' ? '#047857' : '#475569';
    el.textContent = message;
  }

  function _showPostalPicker(code, results, els) {
    let modal = document.getElementById('thAddrPicker');
    if (!modal) {
      modal = document.createElement('div');
      modal.id = 'thAddrPicker';
      modal.style.cssText = 'position:fixed;inset:0;background:rgba(15,23,42,.55);display:none;align-items:center;justify-content:center;z-index:99999;padding:16px';
      document.body.appendChild(modal);
    }
    // If the user already typed a matching district, narrow the picker — they
    // probably want one of those. Fall back to the full list otherwise so
    // they still get to see every option.
    const userDist = els.dist.value && els.dist.value.trim();
    const narrowed = userDist
      ? results.filter(r => r.districtNameTh === userDist)
      : results;
    const shown = narrowed.length ? narrowed : results;
    modal.innerHTML = `
      <div style="background:#fff;border-radius:14px;max-width:480px;width:100%;padding:22px;box-shadow:0 20px 50px rgba(0,0,0,.25);font-family:inherit">
        <h4 style="margin:0 0 6px;font-size:16px;font-weight:700;color:#0f172a">เลือกที่อยู่สำหรับรหัสไปรษณีย์ ${code}</h4>
        <p style="margin:0 0 14px;font-size:13px;color:#64748b">มี ${results.length} ตำบลใช้รหัสนี้${shown.length !== results.length ? ` — กรองตามอำเภอ "${userDist}" แล้ว` : ''} — เลือกตัวเลือกที่ตรง</p>
        <div style="max-height:340px;overflow-y:auto;display:flex;flex-direction:column;gap:6px">
          ${shown.map((r, i) => `
            <button type="button" data-i="${i}" style="text-align:left;border:1px solid #e2e8f0;background:#f8fafc;padding:10px 12px;border-radius:8px;cursor:pointer;font-size:13px;color:#0f172a">
              <strong>${r.subDistrictNameTh}</strong> · ${r.districtNameTh} · ${r.provinceNameTh}
            </button>`).join('')}
        </div>
        <div style="text-align:right;margin-top:14px">
          <button type="button" id="thAddrPickerCancel" style="background:none;border:1px solid #cbd5e1;color:#475569;padding:6px 14px;border-radius:8px;cursor:pointer">ยกเลิก</button>
        </div>
      </div>`;
    modal.style.display = 'flex';
    modal.querySelectorAll('button[data-i]').forEach(btn => {
      btn.onclick = () => {
        const r = shown[parseInt(btn.dataset.i, 10)];
        els.sub.value = r.subDistrictNameTh;
        els.dist.value = r.districtNameTh;
        els.prov.value = r.provinceNameTh;
        modal.style.display = 'none';
        // Pre-load district list for this province + subdistrict list for this
        // district so the user's next edit hits a warm cache and sees
        // suggestions immediately.
        getDistricts(r.provinceCode).then(() => getSubDistricts(r.districtCode)).catch(() => {});
      };
    });
    document.getElementById('thAddrPickerCancel').onclick = () => { modal.style.display = 'none'; };
  }

  // Update the district + subdistrict datalists to match the currently-typed
  // province. Returns the resolved province object (or null if no match).
  async function _refreshDistricts(prov, dist, sub) {
    try {
      const provs = await getProvinces();
      const p = _findByThai(provs, prov.value);
      if (!p) { _fillList('thAddrDistList', []); _fillList('thAddrSubList', []); return null; }
      const dists = await getDistricts(p.code);
      _fillList('thAddrDistList', dists.map(d => d.nameTh));
      // If the typed district no longer belongs to this province, drop it +
      // subdistrict + reset the subdistrict datalist. Without this users can
      // end up with a Phuket province + Bangkok district saved.
      if (dist.value && !_findByThai(dists, dist.value)) {
        dist.value = '';
        sub.value = '';
        _fillList('thAddrSubList', []);
      }
      return { p, dists };
    } catch { return null; }
  }

  async function _refreshSubs(prov, dist, sub) {
    try {
      const r = await _refreshDistricts(prov, dist, sub);
      if (!r) return null;
      const d = _findByThai(r.dists, dist.value);
      if (!d) { _fillList('thAddrSubList', []); return null; }
      const subs = await getSubDistricts(d.code);
      _fillList('thAddrSubList', subs.map(s => s.nameTh));
      // Same containment check at the subdistrict level.
      if (sub.value && !_findByThai(subs, sub.value)) sub.value = '';
      return { d, subs };
    } catch { return null; }
  }

  // Public: re-run the cascade against current input values. Use after a
  // free-text address paste-parse to warm datalists with the right options.
  async function refresh(ids) {
    const sub  = document.getElementById(ids.subDistrict);
    const dist = document.getElementById(ids.district);
    const prov = document.getElementById(ids.province);
    if (!sub || !dist || !prov) return;
    await _refreshSubs(prov, dist, sub);
  }

  // Public: when the postal box is filled (DBD lookup, paste-parse, manual
  // type) but subDistrict / district / province aren't, look up the postal
  // and autofill. Single match → fill silently; multi → picker. Use this
  // after any programmatic fill that bypasses the user's `input` event
  // (assigning `el.value = ...` doesn't fire input listeners). Returns the
  // results so callers can know if a picker was shown.
  async function autoFillFromPostal(ids) {
    const sub  = document.getElementById(ids.subDistrict);
    const dist = document.getElementById(ids.district);
    const prov = document.getElementById(ids.province);
    const zip  = document.getElementById(ids.postalCode);
    if (!sub || !dist || !prov || !zip) return null;
    const code = (zip.value || '').replace(/\D/g, '').slice(0, 5);
    if (code.length !== 5) return null;
    // Skip if everything is already filled — don't fight the user.
    if (sub.value && dist.value && prov.value) return null;
    try {
      const results = await getByPostal(code);
      if (!results || !results.length) return null;
      if (results.length === 1) {
        const r = results[0];
        if (!sub.value)  sub.value  = r.subDistrictNameTh;
        if (!dist.value) dist.value = r.districtNameTh;
        if (!prov.value) prov.value = r.provinceNameTh;
        await _refreshSubs(prov, dist, sub);
        return results;
      }
      // Multi-match — let the picker do the work. Reuses the same modal the
      // input listener uses. Pre-fills province if it's the only common one
      // (saves a click when all matches share a province).
      const uniqueProvs = [...new Set(results.map(r => r.provinceNameTh))];
      if (uniqueProvs.length === 1 && !prov.value) prov.value = uniqueProvs[0];
      _showPostalPicker(code, results, { sub, dist, prov });
      return results;
    } catch { return null; }
  }

  function enhance(ids) {
    const sub  = document.getElementById(ids.subDistrict);
    const dist = document.getElementById(ids.district);
    const prov = document.getElementById(ids.province);
    const zip  = document.getElementById(ids.postalCode);
    if (!sub || !dist || !prov || !zip) return;
    if (wired.has(sub) && wired.has(dist) && wired.has(prov) && wired.has(zip)) return;
    [sub, dist, prov, zip].forEach(el => wired.add(el));

    _ensureList(prov, 'thAddrProvList');
    _ensureList(dist, 'thAddrDistList');
    _ensureList(sub,  'thAddrSubList');

    // Province list is small (~77) — load once and keep. Log failures so
    // empty-dropdown symptoms are diagnosable from DevTools.
    getProvinces().then(provs => {
      _fillList('thAddrProvList', provs.map(p => p.nameTh));
      if (prov.value) _refreshSubs(prov, dist, sub).catch(() => {});
    }).catch(err => console.warn('[ThaiAddress] province load failed', err));

    // Province handlers — normalize aliases on blur, refresh cascade on
    // commit. We don't auto-clear district when province is wiped completely
    // because the user might just be retyping; the cascade re-check on the
    // next blur catches mismatches.
    const onProvCommit = async () => {
      const before = prov.value;
      const after = normalize(before, 'province');
      if (before !== after) {
        prov.value = after;
        _setNote(prov, `จังหวัด "${before}" ถูกปรับเป็น "${after}"`, 'ok');
        setTimeout(() => _setNote(prov, '', ''), 2500);
      } else {
        _setNote(prov, '', '');
      }
      await _refreshSubs(prov, dist, sub);
    };
    prov.addEventListener('change', onProvCommit);
    prov.addEventListener('blur', onProvCommit);

    // District handlers.
    const onDistCommit = async () => {
      await _refreshSubs(prov, dist, sub);
    };
    dist.addEventListener('change', onDistCommit);
    dist.addEventListener('blur', onDistCommit);

    // Subdistrict handlers — autofill postal on commit, always (even if a
    // postal is already there). Subdistrict is the most specific field; if
    // the user just changed it, the previous postal almost certainly belongs
    // to the old subdistrict.
    const onSubCommit = async () => {
      try {
        const r = await _refreshSubs(prov, dist, sub);
        if (!r) return;
        const s = _findByThai(r.subs, sub.value);
        if (s && s.postalCode && zip.value !== s.postalCode) {
          zip.value = s.postalCode;
          _setNote(zip, '', '');
        }
      } catch {}
    };
    sub.addEventListener('change', onSubCommit);
    sub.addEventListener('blur', onSubCommit);

    // Reverse postal lookup. Debounced so typing the code one digit at a time
    // doesn't fire five requests.
    let zipTimer;
    zip.addEventListener('input', () => {
      const v = zip.value.replace(/\D/g, '').slice(0, 5);
      if (zip.value !== v) zip.value = v;
      _setNote(zip, '', '');
      if (v.length !== 5) return;
      clearTimeout(zipTimer);
      zipTimer = setTimeout(async () => {
        try {
          const results = await getByPostal(v);
          if (!results.length) {
            _setNote(zip, `ไม่พบที่อยู่สำหรับรหัส ${v} — โปรดตรวจตัวเลขอีกครั้ง`, 'warn');
            return;
          }
          if (results.length === 1) {
            const r = results[0];
            if (!sub.value)  sub.value  = r.subDistrictNameTh;
            if (!dist.value) dist.value = r.districtNameTh;
            if (!prov.value) prov.value = r.provinceNameTh;
            // Prime cascading datalists so further typing has hints.
            await _refreshSubs(prov, dist, sub);
            _setNote(zip, `เติม ${r.subDistrictNameTh} · ${r.districtNameTh} · ${r.provinceNameTh} อัตโนมัติ`, 'ok');
            setTimeout(() => _setNote(zip, '', ''), 2500);
          } else {
            _showPostalPicker(v, results, { sub, dist, prov });
          }
        } catch {
          // Network blip — stay silent rather than nag, user can retry.
        }
      }, 250);
    });

    // When the user clears the postal box explicitly, drop the inline note.
    zip.addEventListener('blur', () => {
      if (!zip.value) _setNote(zip, '', '');
    });
  }

  return { enhance, refresh, normalize, autoFillFromPostal, getProvinces, getDistricts, getSubDistricts, getByPostal };
})();
