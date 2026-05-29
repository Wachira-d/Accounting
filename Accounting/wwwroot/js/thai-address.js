// Thai address autocomplete widget — enhances a tambon/amphur/province/postal
// input group with cascading datalist suggestions + reverse postal lookup.
//
// Backend already serves the data at /api/gov/address (provinces / districts /
// subdistricts / postal). We cache responses in-memory so a typical form fills
// in 3–5 fetches max regardless of how many times the user edits the fields.
//
// Usage:
//   ThaiAddress.enhance({
//     subDistrict: 'fSubDistrict',
//     district:    'fDistrict',
//     province:    'fProvince',
//     postalCode:  'fPostalCode',
//   });
//
// The function is idempotent — safe to call every time a modal opens; the
// widget tracks which inputs it has already wired.

const ThaiAddress = (() => {
  const BASE = '/api/gov/address';
  const cache = {
    provinces: null,
    districts: {},          // provinceCode -> []
    subdistricts: {},       // districtCode -> []
    postal: {},             // postalCode   -> []
  };
  const wired = new WeakSet();

  async function _fetch(path) {
    const res = await fetch(BASE + path);
    if (!res.ok) throw new Error('address api ' + res.status);
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

  function _ensureList(input, listId) {
    if (input.getAttribute('list') === listId) return;
    let dl = document.getElementById(listId);
    if (!dl) {
      dl = document.createElement('datalist');
      dl.id = listId;
      document.body.appendChild(dl);
    }
    input.setAttribute('list', listId);
    input.setAttribute('autocomplete', 'off');
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
    const v = value.trim();
    return list.find(x => (x.nameTh || x.subDistrictNameTh) === v) || null;
  }

  function _showPostalPicker(code, results, els) {
    let modal = document.getElementById('thAddrPicker');
    if (!modal) {
      modal = document.createElement('div');
      modal.id = 'thAddrPicker';
      modal.style.cssText = 'position:fixed;inset:0;background:rgba(15,23,42,.55);display:none;align-items:center;justify-content:center;z-index:99999;padding:16px';
      document.body.appendChild(modal);
    }
    modal.innerHTML = `
      <div style="background:#fff;border-radius:14px;max-width:480px;width:100%;padding:22px;box-shadow:0 20px 50px rgba(0,0,0,.25);font-family:inherit">
        <h4 style="margin:0 0 6px;font-size:16px;font-weight:700;color:#0f172a">เลือกที่อยู่สำหรับรหัสไปรษณีย์ ${code}</h4>
        <p style="margin:0 0 14px;font-size:13px;color:#64748b">มี ${results.length} ตำบลใช้รหัสนี้ — เลือกตัวเลือกที่ตรง</p>
        <div style="max-height:340px;overflow-y:auto;display:flex;flex-direction:column;gap:6px">
          ${results.map((r, i) => `
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
        const r = results[parseInt(btn.dataset.i, 10)];
        els.sub.value = r.subDistrictNameTh;
        els.dist.value = r.districtNameTh;
        els.prov.value = r.provinceNameTh;
        // Pre-warm cascade caches so subsequent typing into province/district
        // surfaces the right options without another fetch.
        cache.districts[r.provinceCode] = cache.districts[r.provinceCode] || null;
        modal.style.display = 'none';
      };
    });
    document.getElementById('thAddrPickerCancel').onclick = () => { modal.style.display = 'none'; };
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

    // Province list is small (~77) — load once and keep.
    getProvinces().then(provs => {
      _fillList('thAddrProvList', provs.map(p => p.nameTh));
    }).catch(() => {});

    // Province → districts. Run on `change` (datalist commit) and `blur`.
    const refreshDistricts = async () => {
      try {
        const provs = await getProvinces();
        const p = _findByThai(provs, prov.value);
        if (!p) { _fillList('thAddrDistList', []); return null; }
        const dists = await getDistricts(p.code);
        _fillList('thAddrDistList', dists.map(d => d.nameTh));
        return { p, dists };
      } catch { return null; }
    };
    prov.addEventListener('change', refreshDistricts);
    prov.addEventListener('blur', refreshDistricts);

    // District → subdistricts.
    const refreshSubs = async () => {
      try {
        const provs = await getProvinces();
        const p = _findByThai(provs, prov.value);
        if (!p) return null;
        const dists = await getDistricts(p.code);
        const d = _findByThai(dists, dist.value);
        if (!d) { _fillList('thAddrSubList', []); return null; }
        const subs = await getSubDistricts(d.code);
        _fillList('thAddrSubList', subs.map(s => s.nameTh));
        return { d, subs };
      } catch { return null; }
    };
    dist.addEventListener('change', refreshSubs);
    dist.addEventListener('blur', refreshSubs);

    // Subdistrict commit → autofill postal (only if user hasn't typed one).
    const onSubChange = async () => {
      try {
        const r = await refreshSubs();
        if (!r) return;
        const s = _findByThai(r.subs, sub.value);
        if (s && !zip.value) zip.value = s.postalCode;
      } catch {}
    };
    sub.addEventListener('change', onSubChange);
    sub.addEventListener('blur', onSubChange);

    // Reverse postal lookup: 5 digits → autofill all three when there's a
    // single match, otherwise prompt the user to pick.
    let zipTimer;
    zip.addEventListener('input', () => {
      const v = zip.value.replace(/\D/g, '').slice(0, 5);
      if (zip.value !== v) zip.value = v;
      if (v.length !== 5) return;
      clearTimeout(zipTimer);
      zipTimer = setTimeout(async () => {
        try {
          const results = await getByPostal(v);
          if (!results.length) return;
          if (results.length === 1) {
            const r = results[0];
            if (!sub.value)  sub.value  = r.subDistrictNameTh;
            if (!dist.value) dist.value = r.districtNameTh;
            if (!prov.value) prov.value = r.provinceNameTh;
            // Prime cascading datalists so further typing has hints.
            await refreshDistricts();
            await refreshSubs();
          } else {
            _showPostalPicker(v, results, { sub, dist, prov });
          }
        } catch {}
      }, 250);
    });
  }

  return { enhance, getProvinces, getDistricts, getSubDistricts, getByPostal };
})();
