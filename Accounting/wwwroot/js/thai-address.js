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

  // Kick off the province fetch as soon as the script loads — by the time any
  // modal opens, the list is usually warm. Failed requests are cached as null
  // (well, undefined) so they retry on next demand instead of staying stuck.
  getProvinces().catch(err => {
    console.warn('[ThaiAddress] eager province load failed — will retry on demand', err);
    cache.provinces = null;
  });

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
  // Custom suggestion popover — independent of HTML5 datalist. Some browsers
  // suppress the datalist dropdown when the value field is empty / typed
  // value doesn't match an option, and Chrome's datalist also has quirks
  // around dynamic option loading. The popover is a flat <div> we position
  // under the input + drive ourselves; works the same in every browser.
  //
  // Behavior: shows on focus + input. Filters by substring (case-insensitive,
  // Thai-tolerant). Click an option → input.value updates, change event
  // fires (so cascades + autofill react). Blur closes the popover after a
  // tiny delay to let click land first.
  function _attachSuggestionPopover(input, getOptionsAsync) {
    const popId = '_thAddrPop_' + input.id;
    if (document.getElementById(popId)) return;
    const pop = document.createElement('div');
    pop.id = popId;
    pop.style.cssText = 'position:absolute;background:#fff;border:1px solid #cbd5e1;border-radius:8px;box-shadow:0 8px 24px rgba(0,0,0,.18);max-height:260px;overflow-y:auto;z-index:99998;display:none;font-size:13px;min-width:180px';
    document.body.appendChild(pop);

    let cached = null;
    let lastFetchAt = 0;
    // Track WHY we last opened — focus = show all options (user is browsing),
    // input = show filtered (user is searching). Without this, focusing a
    // field that already has "เมืองชลบุรี" auto-filled would show only that
    // one match in the dropdown, blocking the user from picking a different
    // district like "ศรีราชา" without first clearing the field.
    let openMode = 'focus';

    function positionPopover() {
      const r = input.getBoundingClientRect();
      pop.style.left = (window.scrollX + r.left) + 'px';
      pop.style.top  = (window.scrollY + r.bottom + 2) + 'px';
      pop.style.width = Math.max(r.width, 180) + 'px';
    }

    function render(items) {
      if (!items || !items.length) { pop.style.display = 'none'; return; }
      pop.innerHTML = items.slice(0, 200).map(o =>
        `<div class="thAddrOpt" data-v="${String(o).replace(/"/g, '&quot;')}" style="padding:8px 12px;cursor:pointer;color:#0f172a">${o}</div>`
      ).join('');
      pop.querySelectorAll('.thAddrOpt').forEach(el => {
        el.addEventListener('mouseenter', () => el.style.background = '#f1f5f9');
        el.addEventListener('mouseleave', () => el.style.background = '');
        // mousedown preventDefault keeps the input from blurring before the
        // click handler fires — without it, blur closes the popover first
        // and the click never lands.
        el.addEventListener('mousedown', e => e.preventDefault());
        el.addEventListener('click', () => {
          input.value = el.dataset.v;
          pop.style.display = 'none';
          // Fire change so the cascade refreshes the next datalist down +
          // postal autofill triggers when user picks a tambon.
          input.dispatchEvent(new Event('change', { bubbles: true }));
        });
      });
      positionPopover();
      pop.style.display = 'block';
    }

    async function showFiltered() {
      // Re-fetch every 60s so an out-of-date province cache (e.g. user
      // opened the form, never closed it, gov-data deploy happened) self-
      // heals on the next focus.
      if (!cached || Date.now() - lastFetchAt > 60_000) {
        try { cached = await getOptionsAsync(); lastFetchAt = Date.now(); }
        catch { cached = []; }
      }
      const q = (input.value || '').trim().toLowerCase();
      // On `focus` we always show every option — the user just clicked into
      // the field, presumably to pick something. On `input` we filter so
      // typing narrows the list. Without the openMode split, focusing a
      // field that already had a value would filter the dropdown to ~1 row
      // and trap the user.
      const filtered = (openMode === 'input' && q)
        ? cached.filter(o => String(o).toLowerCase().includes(q))
        : cached;
      render(filtered);
    }

    // Public so the enhance() cascade can invalidate this popover's cache
    // when its upstream input changes (e.g. province change → district
    // cache stale). Stored on the input element via dataset so we can
    // find it from outside the closure.
    input.__thAddrInvalidate = () => { cached = null; lastFetchAt = 0; };

    input.addEventListener('focus', () => { openMode = 'focus'; showFiltered(); });
    input.addEventListener('input', () => { openMode = 'input'; showFiltered(); });
    input.addEventListener('blur', () => {
      // Slight delay so the option's click handler runs first.
      setTimeout(() => { pop.style.display = 'none'; }, 180);
    });
    // Reposition on viewport changes so a long page-scroll while the
    // popover is open doesn't leave it stranded.
    window.addEventListener('resize', () => {
      if (pop.style.display === 'block') positionPopover();
    });
  }

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
  //
  // Does NOT clear dist.value when the typed value doesn't match — the user
  // may still be mid-typing and the partial doesn't have to match a list
  // entry yet. Cascade clearing (when province genuinely changes to a new
  // province) is handled by _onProvinceChanged below.
  async function _refreshDistricts(prov, dist, sub) {
    try {
      const provs = await getProvinces();
      const p = _findByThai(provs, prov.value);
      if (!p) { _fillList('thAddrDistList', []); _fillList('thAddrSubList', []); return null; }
      const dists = await getDistricts(p.code);
      _fillList('thAddrDistList', dists.map(d => d.nameTh));
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
      return { d, subs };
    } catch { return null; }
  }

  // Cascade-clear runs ONLY when the parent field commits a new value. Drops
  // child values that no longer belong to the new parent + invalidates the
  // child popover caches so the next focus refetches with the right scope.
  // Without the cache invalidation a user who:
  //   1) picked ชลบุรี → focused district → cache fetched 11 districts
  //   2) changed province to กรุงเทพมหานคร
  //   3) focused district again
  // ...would still see the stale ชลบุรี district list for up to 60s.
  async function _onProvinceChanged(prov, dist, sub) {
    if (dist.__thAddrInvalidate) dist.__thAddrInvalidate();
    if (sub.__thAddrInvalidate) sub.__thAddrInvalidate();
    if (!prov.value) {
      _fillList('thAddrDistList', []);
      _fillList('thAddrSubList', []);
      return;
    }
    try {
      const provs = await getProvinces();
      const p = _findByThai(provs, prov.value);
      if (!p) return;
      const dists = await getDistricts(p.code);
      _fillList('thAddrDistList', dists.map(d => d.nameTh));
      // Drop district / subdistrict only when the existing value would now
      // be cross-province nonsense. Free-text values that have no exact
      // match are preserved (the user knows what they typed).
      if (dist.value && _findByThai(provs, prov.value) && !_findByThai(dists, dist.value)) {
        dist.value = '';
        sub.value = '';
        _fillList('thAddrSubList', []);
      }
    } catch {}
  }

  async function _onDistrictChanged(prov, dist, sub) {
    if (sub.__thAddrInvalidate) sub.__thAddrInvalidate();
    if (!prov.value || !dist.value) { _fillList('thAddrSubList', []); return; }
    try {
      const provs = await getProvinces();
      const p = _findByThai(provs, prov.value);
      if (!p) return;
      const dists = await getDistricts(p.code);
      const d = _findByThai(dists, dist.value);
      if (!d) { _fillList('thAddrSubList', []); return; }
      const subs = await getSubDistricts(d.code);
      _fillList('thAddrSubList', subs.map(s => s.nameTh));
      if (sub.value && !_findByThai(subs, sub.value)) sub.value = '';
    } catch {}
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

      // Pre-fill anything the results unambiguously agree on, regardless of
      // how many tambon rows we got back. Example: 20110 has multiple tambons
      // under ศรีราชา but every one of them shares province=ชลบุรี and
      // district=ศรีราชา — so fill both silently and ask the user only for
      // the tambon. Without this two of three fields stayed blank because
      // the picker exited silently on multi-match.
      const uniqueProvs = [...new Set(results.map(r => r.provinceNameTh))];
      const uniqueDists = [...new Set(results.map(r => r.districtNameTh))];
      const uniqueSubs  = [...new Set(results.map(r => r.subDistrictNameTh))];
      if (!prov.value && uniqueProvs.length === 1) prov.value = uniqueProvs[0];
      if (!dist.value && uniqueDists.length === 1) dist.value = uniqueDists[0];
      if (!sub.value  && uniqueSubs.length  === 1) sub.value  = uniqueSubs[0];

      // Are we done? If only the tambon is still ambiguous, show a picker
      // narrowed to whatever the user (or our pre-fill) settled on. The
      // picker's existing district-filter logic handles the narrowing.
      if (!sub.value) {
        const filtered = dist.value
          ? results.filter(r => r.districtNameTh === dist.value)
          : results;
        if (filtered.length === 1) {
          sub.value = filtered[0].subDistrictNameTh;
        } else {
          _showPostalPicker(code, results, { sub, dist, prov });
        }
      }
      // Refresh datalists so manual edits get the right cascade options.
      await _refreshSubs(prov, dist, sub);
      return results;
    } catch (e) {
      console.warn('[ThaiAddress] postal autofill failed', e);
      return null;
    }
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

    // Belt-and-suspenders: attach a custom suggestion popover to each input.
    // The HTML5 datalist above is the keyboard-friendly default; the popover
    // is the always-works fallback that fires on focus and shows up even
    // when datalist is silently empty (browser quirks, slow API, etc.).
    _attachSuggestionPopover(prov, async () => {
      const provs = await getProvinces();
      return provs.map(p => p.nameTh);
    });
    _attachSuggestionPopover(dist, async () => {
      if (!prov.value) return [];
      const provs = await getProvinces();
      const p = _findByThai(provs, prov.value);
      if (!p) return [];
      const dists = await getDistricts(p.code);
      return dists.map(d => d.nameTh);
    });
    _attachSuggestionPopover(sub, async () => {
      if (!prov.value || !dist.value) return [];
      const provs = await getProvinces();
      const p = _findByThai(provs, prov.value);
      if (!p) return [];
      const dists = await getDistricts(p.code);
      const d = _findByThai(dists, dist.value);
      if (!d) return [];
      const subs = await getSubDistricts(d.code);
      return subs.map(s => s.nameTh);
    });

    // Helper: did the datalist actually get populated? We use this on focus
    // to retry a fetch if the original load failed (network blip, cold
    // cache, request still in flight when the user clicks). Without this
    // retry the user sees the dropdown arrow but an empty list and there's
    // no recovery path short of refreshing the page.
    const _listIsEmpty = (id) => {
      const dl = document.getElementById(id);
      return !dl || dl.children.length === 0;
    };

    const _ensureProvOptions = async () => {
      if (!_listIsEmpty('thAddrProvList')) return true;
      try {
        const provs = await getProvinces();
        _fillList('thAddrProvList', provs.map(p => p.nameTh));
        _setNote(prov, '', '');
        return true;
      } catch (err) {
        console.warn('[ThaiAddress] province retry failed', err);
        _setNote(prov, 'โหลดรายชื่อจังหวัดไม่สำเร็จ — โปรดลองรีเฟรชหน้า', 'warn');
        return false;
      }
    };

    // Eager attempt — usually completes long before the user clicks.
    _ensureProvOptions().then(ok => {
      if (ok && prov.value) _refreshSubs(prov, dist, sub).catch(() => {});
    });

    // Retry-on-focus handlers — when the user clicks any of the three
    // cascading inputs, verify the relevant datalist is populated and
    // refetch if not. This is the safety net for "dropdown arrow appears
    // but the list is empty" symptoms.
    prov.addEventListener('focus', _ensureProvOptions);
    dist.addEventListener('focus', async () => {
      await _ensureProvOptions();
      if (prov.value) await _refreshDistricts(prov, dist, sub);
    });
    sub.addEventListener('focus', async () => {
      await _ensureProvOptions();
      if (prov.value && dist.value) await _refreshSubs(prov, dist, sub);
    });

    // Province handlers — normalize aliases on blur, refresh cascade on
    // commit. _onProvinceChanged invalidates the district + subdistrict
    // popover caches so the next focus on those fields refetches with the
    // new province scope; without that invalidation the user would see
    // the previous province's districts for up to 60s.
    //
    // We only run the cascade clear on `change` (an explicit commit from
    // the popover click or datalist pick) — NOT on `blur`, because losing
    // focus mid-typing-a-real-name would erase a partial that should be
    // preserved.
    let lastProvCommitted = prov.value;
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
      if (prov.value !== lastProvCommitted) {
        lastProvCommitted = prov.value;
        await _onProvinceChanged(prov, dist, sub);
      } else {
        await _refreshDistricts(prov, dist, sub);
      }
    };
    prov.addEventListener('change', onProvCommit);
    prov.addEventListener('blur', onProvCommit);

    // District handlers — same pattern. Cascade-clear sub only on actual
    // commit changes, refresh datalist on blur.
    let lastDistCommitted = dist.value;
    const onDistCommit = async () => {
      if (dist.value !== lastDistCommitted) {
        lastDistCommitted = dist.value;
        await _onDistrictChanged(prov, dist, sub);
      } else {
        await _refreshSubs(prov, dist, sub);
      }
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
