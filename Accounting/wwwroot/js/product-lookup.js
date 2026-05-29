// ===== Product Lookup Component =====
// Typeahead for the document line-item editor. On select, fills qty/price/VAT
// and tags the row with data-product-code so save() persists the linkage.

const ProductLookup = {
  _debounceTimer: null,
  _cache: new Map(),
  _cacheMaxAge: 30_000,
  _cacheMaxEntries: 50,
  _wrappers: new Set(),

  attach(inputEl, onSelect) {
    if (!inputEl || inputEl.dataset.productLookupAttached === '1') return;
    inputEl.dataset.productLookupAttached = '1';

    // Dropdown lives on document.body so the form modal's overflow:auto
    // can't clip it. Position is computed from inputEl.getBoundingClientRect()
    // — same as a tooltip / picker. Without this the dropdown rendered
    // correctly but was clipped by the modal, so the user saw nothing.
    const dropdown = document.createElement('div');
    dropdown.className = 'product-dropdown';
    dropdown.style.cssText = 'display:none;position:absolute;z-index:99999;' +
      'min-width:380px;max-width:520px;background:#fff;border:1px solid #e2e8f0;border-radius:8px;' +
      'box-shadow:0 8px 24px rgba(0,0,0,0.18);max-height:320px;overflow-y:auto';
    document.body.appendChild(dropdown);
    this._wrappers.add({ wrapper: inputEl, dropdown });

    const positionDropdown = () => {
      const r = inputEl.getBoundingClientRect();
      dropdown.style.left = (window.scrollX + r.left) + 'px';
      dropdown.style.top  = (window.scrollY + r.bottom + 2) + 'px';
      dropdown.style.width = Math.max(r.width, 380) + 'px';
    };

    inputEl.addEventListener('input', () => {
      clearTimeout(this._debounceTimer);
      const q = inputEl.value.trim();
      if (q.length < 2) { dropdown.style.display = 'none'; return; }
      this._debounceTimer = setTimeout(() => {
        positionDropdown();
        this._search(q, inputEl, dropdown, onSelect);
      }, 250);
    });

    inputEl.addEventListener('focus', () => {
      if (inputEl.value.trim().length >= 2 && dropdown.children.length > 0) {
        positionDropdown();
        dropdown.style.display = 'block';
      }
    });

    // Reposition when the user scrolls the modal or resizes the window —
    // otherwise a long ingredient list could leave the dropdown stranded.
    const reposition = () => { if (dropdown.style.display === 'block') positionDropdown(); };
    window.addEventListener('scroll', reposition, true);
    window.addEventListener('resize', reposition);

    inputEl.addEventListener('keydown', (e) => {
      if (dropdown.style.display === 'none') return;
      const items = [...dropdown.querySelectorAll('.product-item')];
      if (!items.length) return;
      const current = dropdown.querySelector('.product-item.active');
      let idx = current ? items.indexOf(current) : -1;
      if (e.key === 'ArrowDown') { e.preventDefault(); idx = Math.min(items.length - 1, idx + 1); }
      else if (e.key === 'ArrowUp') { e.preventDefault(); idx = Math.max(0, idx - 1); }
      else if (e.key === 'Enter' && idx >= 0) { e.preventDefault(); items[idx].click(); return; }
      else if (e.key === 'Escape') { dropdown.style.display = 'none'; return; }
      else return;
      items.forEach(el => el.classList.remove('active'));
      if (items[idx]) {
        items[idx].classList.add('active');
        items[idx].style.background = '#eff6ff';
        items[idx].scrollIntoView({ block: 'nearest' });
      }
    });
  },

  async _search(q, inputEl, dropdown, onSelect) {
    const now = Date.now();
    let items = null;
    const cached = this._cache.get(q);
    if (cached && (now - cached.t) < this._cacheMaxAge) {
      items = cached.items;
    } else {
      dropdown.innerHTML = '<div style="padding:12px;text-align:center;color:#94a3b8;font-size:13px">กำลังค้นหาสินค้า…</div>';
      dropdown.style.display = 'block';
      try {
        const api = Layout.api(); if (!api) return;
        const res = await api.getProducts(`?search=${encodeURIComponent(q)}&pageSize=10`);
        items = (res.data?.items) || res.data || [];
        // Cap cache — drop the oldest entry once we exceed the budget.
        if (this._cache.size >= this._cacheMaxEntries) {
          this._cache.delete(this._cache.keys().next().value);
        }
        this._cache.set(q, { t: now, items });
      } catch (e) {
        dropdown.innerHTML = '<div style="padding:12px;text-align:center;color:#ef4444;font-size:13px">ค้นหาสินค้าไม่สำเร็จ</div>';
        return;
      }
    }

    if (!items || items.length === 0) {
      dropdown.innerHTML = '<div style="padding:10px 14px;color:#64748b;font-size:13px">' +
        'ไม่พบสินค้าตรงกับ "' + Layout.esc(q) + '" — กรอกข้อความเองได้เลย ' +
        '<a href="/pages/products.html" target="_blank" style="color:#2563eb">หรือเพิ่มสินค้าใหม่ →</a>' +
        '</div>';
      dropdown.style.display = 'block';
      return;
    }

    dropdown.innerHTML = items.map((p, i) => {
      const stockHint = p.trackStock
        ? `<span style="color:${p.currentStock > 0 ? '#059669' : '#dc2626'};font-size:11px">คงเหลือ ${p.currentStock ?? 0} ${Layout.esc(p.unit || '')}</span>`
        : '';
      const price = (p.sellingPrice ?? 0).toLocaleString('th-TH', { minimumFractionDigits: 2, maximumFractionDigits: 2 });
      return `
        <div class="product-item" data-index="${i}" style="padding:8px 14px;cursor:pointer;border-bottom:1px solid #f1f5f9"
             onmouseenter="this.style.background='#f0f9ff'" onmouseleave="this.classList.contains('active')?this.style.background='#eff6ff':this.style.background='transparent'">
          <div style="display:flex;justify-content:space-between;align-items:start;gap:12px">
            <div style="flex:1;min-width:0">
              <div style="font-size:13px;font-weight:600;color:#1e293b;white-space:nowrap;overflow:hidden;text-overflow:ellipsis">
                ${Layout.esc(p.name)}
              </div>
              <div style="font-size:11px;color:#64748b;margin-top:1px">
                ${Layout.esc(p.code)}${p.unit ? ' · ' + Layout.esc(p.unit) : ''}${p.barcode ? ' · ' + Layout.esc(p.barcode) : ''}
              </div>
            </div>
            <div style="text-align:right;flex-shrink:0">
              <div style="font-size:13px;font-weight:600;color:#0f172a">${price}</div>
              ${stockHint}
            </div>
          </div>
        </div>`;
    }).join('');
    dropdown.style.display = 'block';

    dropdown.querySelectorAll('.product-item').forEach(el => {
      el.addEventListener('click', () => {
        const idx = parseInt(el.dataset.index);
        const p = items[idx];
        dropdown.style.display = 'none';
        const row = inputEl.closest('tr');
        if (row && onSelect) onSelect(row, p);
      });
    });
  },

  fillRow(row, product, opts = {}) {
    if (!row || !product) return;
    const set = (field, val) => {
      const el = row.querySelector(`[data-f="${field}"]`);
      if (el != null && val != null && val !== '') el.value = val;
    };
    set('desc', product.name);
    if (opts.overwriteQty !== false) set('qty', 1);
    if (opts.overwritePrice !== false) {
      const side = row.closest('table')?.dataset.docSide || 'revenue';
      const price = side === 'expense'
        ? (product.costPrice ?? product.sellingPrice ?? 0)
        : (product.sellingPrice ?? product.costPrice ?? 0);
      set('price', price);
    }
    if (opts.overwriteVat !== false && product.vatRate != null) {
      const vatSel = row.querySelector('[data-f="vat"]');
      if (vatSel) vatSel.value = String(product.vatRate);
    }
    row.dataset.productId = product.id || '';
    row.dataset.productCode = product.code || '';
    this.createChip(row, product.code || '');
    if (typeof Page !== 'undefined' && Page.calcSum) Page.calcSum();
  },

  // Render (or refresh) the product-linkage chip under the description cell.
  // Used both on initial pick (from fillRow) and when re-opening a saved
  // document (documents.html openEdit) — single source of truth for the
  // chip's appearance and unlink behavior.
  createChip(row, code) {
    if (!row) return;
    let chip = row.querySelector('.product-chip');
    if (!chip) {
      chip = document.createElement('div');
      chip.className = 'product-chip';
      chip.style.cssText = 'font-size:10px;color:#0369a1;margin-top:2px;display:flex;align-items:center;gap:4px';
      const descCell = row.querySelector('[data-f="desc"]')?.closest('td');
      if (!descCell) return;
      descCell.appendChild(chip);
    }
    chip.innerHTML = `📦 ${Layout.esc(code)} ` +
      `<a href="javascript:void(0)" style="color:#94a3b8;text-decoration:none" title="ยกเลิกการผูกสินค้า">✕</a>`;
    chip.querySelector('a').addEventListener('click', () => {
      row.dataset.productId = '';
      row.dataset.productCode = '';
      chip.remove();
    });
  },
};

// Single document-level click listener — hides any open ProductLookup
// dropdown when the user clicks outside its input or dropdown. Registered
// once at module load so re-attaching to new rows doesn't accumulate
// listeners.
document.addEventListener('click', (e) => {
  for (const entry of [...ProductLookup._wrappers]) {
    if (!entry.wrapper.isConnected) { ProductLookup._wrappers.delete(entry); continue; }
    const insideInput = entry.wrapper.contains(e.target);
    const insideDropdown = entry.dropdown.contains(e.target);
    if (!insideInput && !insideDropdown) entry.dropdown.style.display = 'none';
  }
});
