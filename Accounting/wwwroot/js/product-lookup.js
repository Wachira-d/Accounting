// ===== Product Lookup Component =====
// Typeahead for the document line-item editor.
// User types in the description field → list of matching products appears.
// On select, auto-fills unit price + VAT rate + product code, and tags
// the row with data-product-id / data-product-code so save() can submit
// the linkage back to the server.

const ProductLookup = {
  _debounceTimer: null,
  _cache: new Map(),            // q → results (keeps consecutive keystrokes snappy)
  _cacheMaxAge: 30_000,         // ms

  /**
   * Attach typeahead to a description input inside a document-line row.
   *
   * @param {HTMLInputElement} inputEl   The description input.
   * @param {(row: HTMLElement, product: object) => void} onSelect
   *        Called when the user picks a product. The row passed is the
   *        nearest <tr>; the product is the API response object
   *        (Code, Name, SellingPrice, CostPrice, VatRate, Unit, ...).
   */
  attach(inputEl, onSelect) {
    if (!inputEl || inputEl.dataset.productLookupAttached === '1') return;
    inputEl.dataset.productLookupAttached = '1';

    const wrapper = document.createElement('div');
    wrapper.style.cssText = 'position:relative';
    inputEl.parentNode.insertBefore(wrapper, inputEl);
    wrapper.appendChild(inputEl);

    const dropdown = document.createElement('div');
    dropdown.className = 'product-dropdown';
    dropdown.style.cssText = 'display:none;position:absolute;top:100%;left:0;z-index:1100;' +
      'min-width:380px;max-width:520px;background:#fff;border:1px solid #e2e8f0;border-radius:8px;' +
      'box-shadow:0 6px 16px rgba(0,0,0,0.12);max-height:320px;overflow-y:auto;margin-top:2px';
    wrapper.appendChild(dropdown);

    const hide = () => { dropdown.style.display = 'none'; };
    document.addEventListener('click', (e) => { if (!wrapper.contains(e.target)) hide(); });

    inputEl.addEventListener('input', () => {
      clearTimeout(this._debounceTimer);
      const q = inputEl.value.trim();
      if (q.length < 2) { hide(); return; }
      this._debounceTimer = setTimeout(() => this._search(q, inputEl, dropdown, onSelect), 250);
    });

    inputEl.addEventListener('focus', () => {
      if (inputEl.value.trim().length >= 2 && dropdown.children.length > 0) {
        dropdown.style.display = 'block';
      }
    });

    // Keyboard nav — arrows + Enter pick an item without leaving the keyboard.
    inputEl.addEventListener('keydown', (e) => {
      if (dropdown.style.display === 'none') return;
      const items = [...dropdown.querySelectorAll('.product-item')];
      if (!items.length) return;
      const current = dropdown.querySelector('.product-item.active');
      let idx = current ? items.indexOf(current) : -1;
      if (e.key === 'ArrowDown') { e.preventDefault(); idx = Math.min(items.length - 1, idx + 1); }
      else if (e.key === 'ArrowUp') { e.preventDefault(); idx = Math.max(0, idx - 1); }
      else if (e.key === 'Enter' && idx >= 0) { e.preventDefault(); items[idx].click(); return; }
      else if (e.key === 'Escape') { hide(); return; }
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
        // Server-side search across Code / Name / SKU / Barcode — ProductService
        // already handles the filter when ?search=... is provided.
        const res = await api.getProducts(`?search=${encodeURIComponent(q)}&pageSize=10`);
        items = (res.data?.items) || res.data || [];
        this._cache.set(q, { t: now, items });
      } catch (e) {
        dropdown.innerHTML = '<div style="padding:12px;text-align:center;color:#ef4444;font-size:13px">ค้นหาสินค้าไม่สำเร็จ</div>';
        return;
      }
    }

    if (!items || items.length === 0) {
      dropdown.innerHTML = '<div style="padding:10px 14px;color:#64748b;font-size:13px">' +
        'ไม่พบสินค้าตรงกับ "' + this._esc(q) + '" — กรอกข้อความเองได้เลย ' +
        '<a href="/pages/products.html" target="_blank" style="color:#2563eb">หรือเพิ่มสินค้าใหม่ →</a>' +
        '</div>';
      dropdown.style.display = 'block';
      return;
    }

    dropdown.innerHTML = items.map((p, i) => {
      const stockHint = p.trackStock
        ? `<span style="color:${p.currentStock > 0 ? '#059669' : '#dc2626'};font-size:11px">คงเหลือ ${p.currentStock ?? 0} ${this._esc(p.unit || '')}</span>`
        : '';
      const price = (p.sellingPrice ?? 0).toLocaleString('th-TH', { minimumFractionDigits: 2, maximumFractionDigits: 2 });
      return `
        <div class="product-item" data-index="${i}" style="padding:8px 14px;cursor:pointer;border-bottom:1px solid #f1f5f9"
             onmouseenter="this.style.background='#f0f9ff'" onmouseleave="this.classList.contains('active')?this.style.background='#eff6ff':this.style.background='transparent'">
          <div style="display:flex;justify-content:space-between;align-items:start;gap:12px">
            <div style="flex:1;min-width:0">
              <div style="font-size:13px;font-weight:600;color:#1e293b;white-space:nowrap;overflow:hidden;text-overflow:ellipsis">
                ${this._esc(p.name)}
              </div>
              <div style="font-size:11px;color:#64748b;margin-top:1px">
                ${this._esc(p.code)}${p.unit ? ' · ' + this._esc(p.unit) : ''}${p.barcode ? ' · ' + this._esc(p.barcode) : ''}
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

  /**
   * Default fill behavior — copies product fields into the row's data-f
   * inputs. Callers that want different behavior (e.g. preserve a quantity
   * the user already typed) can pass their own onSelect to attach().
   */
  fillRow(row, product, opts = {}) {
    if (!row || !product) return;
    const set = (field, val) => {
      const el = row.querySelector(`[data-f="${field}"]`);
      if (el != null && val != null && val !== '') el.value = val;
    };
    set('desc', product.name);
    // Preserve qty/price the user has already edited if opts.overwrite is false.
    if (opts.overwriteQty !== false) set('qty', 1);
    if (opts.overwritePrice !== false) {
      // Pick selling vs cost based on the current document side. The Page
      // hint comes from documents.html — it sets data-doc-side on the table.
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
    // Tag the row so save() can persist the linkage.
    row.dataset.productId = product.id || '';
    row.dataset.productCode = product.code || '';

    // Show a small chip under the description that the row is linked.
    let chip = row.querySelector('.product-chip');
    if (!chip) {
      chip = document.createElement('div');
      chip.className = 'product-chip';
      chip.style.cssText = 'font-size:10px;color:#0369a1;margin-top:2px;display:flex;align-items:center;gap:4px';
      const descCell = row.querySelector('[data-f="desc"]')?.closest('td');
      if (descCell) descCell.appendChild(chip);
    }
    chip.innerHTML = `📦 ${this._esc(product.code)} ` +
      `<a href="javascript:void(0)" style="color:#94a3b8;text-decoration:none" title="ยกเลิกการผูกสินค้า">✕</a>`;
    chip.querySelector('a').addEventListener('click', () => {
      row.dataset.productId = '';
      row.dataset.productCode = '';
      chip.remove();
    });

    // Recompute totals after the price/qty changes.
    if (typeof Page !== 'undefined' && Page.calcSum) Page.calcSum();
  },

  _esc(str) {
    if (str == null) return '';
    return String(str).replace(/</g, '&lt;').replace(/>/g, '&gt;').replace(/"/g, '&quot;');
  }
};
