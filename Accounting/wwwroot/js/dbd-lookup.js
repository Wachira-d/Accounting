// ===== DBD Lookup Component =====
// ดึงข้อมูลนิติบุคคลจากกรมพัฒนาธุรกิจการค้า (DBD)
// ใช้ได้ทั้ง autocomplete จากชื่อ และ auto-fill จากเลขทะเบียน

const DbdLookup = {
  _debounceTimer: null,
  _activeDropdown: null,

  // ===== Autocomplete: ค้นหาจากชื่อบริษัท =====
  // inputEl: input element ที่ user พิมพ์ชื่อ
  // onSelect: callback(result) เมื่อ user เลือกรายการ
  // options.enabledCheck: () => bool — return false to skip DBD entirely
  //                       (e.g., when contact type is not juristic). Without
  //                       this, a personal contact's name search hits DBD,
  //                       gets "ไม่พบข้อมูล", and confuses the user.
  attachNameSearch(inputEl, onSelect, options = {}) {
    if (!inputEl) return;
    const enabledCheck = options.enabledCheck || (() => true);

    const wrapper = document.createElement('div');
    wrapper.style.cssText = 'position:relative';
    inputEl.parentNode.insertBefore(wrapper, inputEl);
    wrapper.appendChild(inputEl);

    const dropdown = document.createElement('div');
    dropdown.className = 'dbd-dropdown';
    dropdown.style.cssText = 'display:none;position:absolute;top:100%;left:0;right:0;z-index:1000;' +
      'background:#fff;border:1px solid #e2e8f0;border-radius:8px;box-shadow:0 4px 12px rgba(0,0,0,0.1);' +
      'max-height:280px;overflow-y:auto;margin-top:2px';
    wrapper.appendChild(dropdown);

    inputEl.addEventListener('input', () => {
      clearTimeout(this._debounceTimer);
      // Skip DBD lookup when the caller says we shouldn't (non-juristic).
      if (!enabledCheck()) { dropdown.style.display = 'none'; return; }
      const q = inputEl.value.trim();
      if (q.length < 2) { dropdown.style.display = 'none'; return; }

      this._debounceTimer = setTimeout(async () => {
        dropdown.innerHTML = '<div style="padding:12px;text-align:center;color:#94a3b8;font-size:13px">กำลังค้นหา...</div>';
        dropdown.style.display = 'block';

        try {
          const res = await API.get(`/api/dbd/search?q=${encodeURIComponent(q)}&limit=8`);
          const items = res.data || [];

          if (items.length === 0) {
            dropdown.innerHTML = '<div style="padding:12px;text-align:center;color:#94a3b8;font-size:13px">ไม่พบข้อมูล</div>';
            return;
          }

          dropdown.innerHTML = items.map((item, i) => `
            <div class="dbd-item" data-index="${i}" style="padding:10px 14px;cursor:pointer;border-bottom:1px solid #f1f5f9;transition:background 0.15s"
                 onmouseenter="this.style.background='#f0f9ff'" onmouseleave="this.style.background='transparent'">
              <div style="font-size:14px;font-weight:500;color:#1e293b">${this._esc(item.nameTh)}</div>
              ${item.nameEn ? `<div style="font-size:12px;color:#64748b">${this._esc(item.nameEn)}</div>` : ''}
              <div style="font-size:11px;color:#94a3b8;margin-top:2px">
                ${this._esc(item.juristicId)} | ${this._esc(item.juristicType || '')} | ${this._esc(item.status || '')}
              </div>
            </div>
          `).join('');

          dropdown.querySelectorAll('.dbd-item').forEach(el => {
            el.addEventListener('click', () => {
              const idx = parseInt(el.dataset.index);
              dropdown.style.display = 'none';
              if (onSelect) onSelect(items[idx]);
            });
          });

        } catch (e) {
          dropdown.innerHTML = '<div style="padding:12px;text-align:center;color:#ef4444;font-size:13px">เกิดข้อผิดพลาดในการค้นหา</div>';
        }
      }, 350);
    });

    // Close dropdown when clicking outside
    document.addEventListener('click', (e) => {
      if (!wrapper.contains(e.target)) dropdown.style.display = 'none';
    });

    inputEl.addEventListener('focus', () => {
      if (dropdown.children.length > 0 && inputEl.value.trim().length >= 2) {
        dropdown.style.display = 'block';
      }
    });
  },

  // ===== Auto-fill: ดึงข้อมูลจากเลขทะเบียน 13 หลัก =====
  // inputEl: input element ที่ user กรอกเลขทะเบียน
  // onResult: callback(result) เมื่อดึงข้อมูลสำเร็จ
  // btnEl: (optional) ปุ่มดึงข้อมูล ถ้าไม่ระบุจะ auto-fetch เมื่อกรอกครบ 13 หลัก
  attachTaxIdLookup(inputEl, onResult, btnEl, options = {}) {
    if (!inputEl) return;
    const enabledCheck = options.enabledCheck || (() => true);

    const doLookup = async () => {
      if (!enabledCheck()) return;     // non-juristic → skip silently
      const taxId = inputEl.value.replace(/[^0-9]/g, '');
      if (taxId.length !== 13) {
        if (typeof Layout !== 'undefined') Layout.toast('กรุณากรอกเลขผู้เสียภาษี 13 หลัก', 'error');
        return;
      }

      // Show loading state
      if (btnEl) { btnEl.disabled = true; btnEl.textContent = 'กำลังค้นหา...'; }

      try {
        const res = await API.get(`/api/dbd/juristic/${encodeURIComponent(taxId)}`);
        if (res.data) {
          const d = res.data;
          const hasName = d.nameTh || d.nameEn;
          const hasAny = hasName || d.address;
          if (onResult) onResult(d);
          if (typeof Layout !== 'undefined') {
            if (hasName) {
              Layout.toast('ดึงข้อมูลจาก DBD สำเร็จ — ' + (d.nameTh || d.nameEn));
            } else if (hasAny) {
              Layout.toast('ดึงข้อมูลบางส่วนได้ — กรุณากรอกชื่อบริษัทเพิ่มเติม', 'info');
            } else {
              Layout.toast('พบเลขทะเบียนในระบบ แต่ DBD ไม่ได้ส่งรายละเอียดกลับมา กรุณากรอกข้อมูลเอง', 'info');
            }
          }
        } else {
          if (typeof Layout !== 'undefined') Layout.toast('ไม่พบข้อมูลนิติบุคคล', 'error');
        }
      } catch (e) {
        // Try TIN verification as fallback
        try {
          const tinRes = await API.get(`/api/dbd/verify-tin/${encodeURIComponent(taxId)}`);
          if (tinRes.data?.isExist) {
            if (typeof Layout !== 'undefined') Layout.toast('เลขผู้เสียภาษีถูกต้อง แต่ไม่พบข้อมูลนิติบุคคลในระบบ DBD', 'info');
          } else {
            if (typeof Layout !== 'undefined') Layout.toast('ไม่พบเลขผู้เสียภาษีในระบบ', 'error');
          }
        } catch (e2) {
          if (typeof Layout !== 'undefined') Layout.toast('ไม่สามารถเชื่อมต่อระบบ DBD ได้', 'error');
        }
      } finally {
        if (btnEl) { btnEl.disabled = false; btnEl.textContent = 'ดึงข้อมูล'; }
      }
    };

    let lastAutoLookupId = '';
    // Auto-fetch when 13 digits entered (works even if a button is provided)
    inputEl.addEventListener('input', () => {
      const val = inputEl.value.replace(/[^0-9]/g, '');
      if (val.length === 13 && val !== lastAutoLookupId) {
        lastAutoLookupId = val;
        setTimeout(doLookup, 300);
      } else if (val.length < 13) {
        lastAutoLookupId = '';
      }
    });

    // Trigger on blur/change too (for paste / programmatic fills)
    inputEl.addEventListener('change', () => {
      const val = inputEl.value.replace(/[^0-9]/g, '');
      if (val.length === 13 && val !== lastAutoLookupId) {
        lastAutoLookupId = val;
        doLookup();
      }
    });

    // Button click
    if (btnEl) {
      btnEl.addEventListener('click', (e) => { e.preventDefault(); lastAutoLookupId = ''; doLookup(); });
    }
  },

  // ===== Helper: fill form fields from DBD result =====
  fillCompanyForm(result, fieldMap) {
    // fieldMap: { nameTh: 'inputId', nameEn: 'inputId', ... }
    if (!result) return;

    const setVal = (id, val) => {
      const el = document.getElementById(id);
      if (el && val) el.value = val;
    };

    if (fieldMap.nameTh) setVal(fieldMap.nameTh, result.nameTh);
    if (fieldMap.nameEn) setVal(fieldMap.nameEn, result.nameEn);
    if (fieldMap.taxId) setVal(fieldMap.taxId, result.juristicId);
    if (fieldMap.juristicId) setVal(fieldMap.juristicId, result.juristicId);
    if (fieldMap.address) setVal(fieldMap.address, result.address);

    // Parse address to sub-components if possible
    if (result.address && fieldMap.addressParts) {
      this._parseThaiAddress(result.address, fieldMap.addressParts);
    }
  },

  // Try to parse Thai address into components. We also surface หมู่ที่ (Moo)
  // when the field is mapped, so callers that pre-include `moo` get it
  // filled directly from the DBD address.
  _parseThaiAddress(addr, fieldIds) {
    if (!addr) return;
    // Thai address patterns: ตำบล/แขวง, อำเภอ/เขต, จังหวัด, รหัสไปรษณีย์, หมู่ที่.
    // Allow optional whitespace after the prefix — DBD often returns
    // "ตำบล สุรศักดิ์" (with space) rather than "ตำบลสุรศักดิ์", and the
    // previous regex silently skipped those cases. The regex is intentionally
    // simple; the postal-code reverse-lookup invoked by the caller is the
    // bulletproof source of canonical tambon/amphur/province names.
    const tumbonMatch = addr.match(/(?:แขวง|ตำบล|ต\.)\s*([^\s,]+)/);
    const amphurMatch = addr.match(/(?:เขต|อำเภอ|อ\.)\s*([^\s,]+)/);
    const provinceMatch = addr.match(/(?:จังหวัด|จ\.)\s*([^\s,\d]+)/);
    const postalMatch = addr.match(/(\d{5})(?!\d)/);
    // Matches "หมู่ที่ 5" / "หมู่ 5" / "ม.5" and Thai-numeral variants.
    // Group 1 is the digit run; we strip Thai numerals downstream.
    const mooMatch = addr.match(/(?:หมู่ที่|หมู่|ม\.)\s*([0-9๐-๙]+)/);

    if (tumbonMatch && fieldIds.subDistrict) {
      const el = document.getElementById(fieldIds.subDistrict);
      if (el) el.value = tumbonMatch[1].trim();
    }
    if (amphurMatch && fieldIds.district) {
      const el = document.getElementById(fieldIds.district);
      if (el) el.value = amphurMatch[1].trim();
    }
    if (provinceMatch && fieldIds.province) {
      const el = document.getElementById(fieldIds.province);
      if (el) el.value = provinceMatch[1].trim();
    }
    if (postalMatch && fieldIds.postalCode) {
      const el = document.getElementById(fieldIds.postalCode);
      if (el) el.value = postalMatch[1];
    }
    if (mooMatch && fieldIds.moo) {
      const el = document.getElementById(fieldIds.moo);
      // Convert Thai numerals (๐-๙) → ASCII digits so the saved value is
      // consistently "5" not "๕".
      if (el) el.value = mooMatch[1].replace(/[๐-๙]/g, d => String.fromCharCode(0x30 + d.charCodeAt(0) - 0x0E50));
    }
  },

  _esc(str) {
    if (!str) return '';
    return str.replace(/</g, '&lt;').replace(/>/g, '&gt;').replace(/"/g, '&quot;');
  }
};
