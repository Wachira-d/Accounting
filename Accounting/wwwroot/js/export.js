// Reusable export module: CSV / Excel / PDF
// Dependencies loaded lazily from CDN:
//   - SheetJS (xlsx) for Excel
//   - jsPDF + jspdf-autotable for PDF

window.ExportUtil = (function () {
  const CDN = {
    xlsx: 'https://cdn.jsdelivr.net/npm/xlsx@0.18.5/dist/xlsx.full.min.js',
    jspdf: 'https://cdn.jsdelivr.net/npm/jspdf@2.5.1/dist/jspdf.umd.min.js',
    autotable: 'https://cdn.jsdelivr.net/npm/jspdf-autotable@3.8.2/dist/jspdf.plugin.autotable.min.js',
    thaiFont: 'https://cdn.jsdelivr.net/gh/phamfoo/Sarabun-jsPDF@master/Sarabun-Regular-normal.js'
  };
  const loaded = {};

  function loadScript(url) {
    if (loaded[url]) return loaded[url];
    loaded[url] = new Promise((resolve, reject) => {
      const s = document.createElement('script');
      s.src = url; s.onload = resolve; s.onerror = reject;
      document.head.appendChild(s);
    });
    return loaded[url];
  }

  function safeFilename(name) {
    return (name || 'export').replace(/[^a-zA-Z0-9ก-๙_\-]/g, '_') + '_' +
      new Date().toISOString().slice(0, 10);
  }

  // Convert rows (array of objects) + columns definition to 2D array
  // columns: [{ key: 'name', label: 'ชื่อ', format?: (v, row) => string }]
  function rowsToMatrix(rows, columns) {
    const header = columns.map(c => c.label);
    const body = rows.map(r => columns.map(c => {
      const v = c.key.split('.').reduce((acc, k) => acc?.[k], r);
      return c.format ? c.format(v, r) : (v == null ? '' : v);
    }));
    return [header, ...body];
  }

  // ========== CSV ==========
  function exportCsv(rows, columns, filename) {
    const matrix = rowsToMatrix(rows, columns);
    const csv = matrix.map(row =>
      row.map(cell => {
        const s = String(cell ?? '');
        return /[",\n\r]/.test(s) ? `"${s.replace(/"/g, '""')}"` : s;
      }).join(',')
    ).join('\r\n');
    // BOM for Excel Thai support
    const blob = new Blob(['﻿' + csv], { type: 'text/csv;charset=utf-8;' });
    downloadBlob(blob, safeFilename(filename) + '.csv');
  }

  // ========== Excel (XLSX) ==========
  async function exportExcel(rows, columns, filename, sheetName = 'Sheet1') {
    await loadScript(CDN.xlsx);
    const matrix = rowsToMatrix(rows, columns);
    const wb = XLSX.utils.book_new();
    const ws = XLSX.utils.aoa_to_sheet(matrix);
    // Auto column widths
    ws['!cols'] = columns.map(c => ({ wch: Math.max(10, (c.label || '').length + 2, c.width || 0) }));
    XLSX.utils.book_append_sheet(wb, ws, sheetName.substring(0, 31));
    XLSX.writeFile(wb, safeFilename(filename) + '.xlsx');
  }

  // ========== PDF ==========
  async function exportPdf(rows, columns, filename, opts = {}) {
    await loadScript(CDN.jspdf);
    await loadScript(CDN.autotable);
    // Thai font (optional — fallback to default if fails)
    try { await loadScript(CDN.thaiFont); } catch { }

    const { jsPDF } = window.jspdf;
    const doc = new jsPDF({ orientation: opts.landscape ? 'landscape' : 'portrait', unit: 'mm', format: 'a4' });
    try { doc.setFont('Sarabun-Regular'); } catch { }

    if (opts.title) {
      doc.setFontSize(14);
      doc.text(opts.title, 14, 15);
    }
    if (opts.subtitle) {
      doc.setFontSize(10);
      doc.text(opts.subtitle, 14, 22);
    }

    const head = [columns.map(c => c.label)];
    const body = rows.map(r => columns.map(c => {
      const v = c.key.split('.').reduce((acc, k) => acc?.[k], r);
      const val = c.format ? c.format(v, r) : (v == null ? '' : v);
      return String(val);
    }));

    doc.autoTable({
      head, body,
      startY: opts.subtitle ? 26 : (opts.title ? 20 : 14),
      styles: { font: 'Sarabun-Regular', fontSize: 8, cellPadding: 1.5 },
      headStyles: { fillColor: [52, 73, 94], textColor: 255 },
      alternateRowStyles: { fillColor: [245, 245, 245] },
      didParseCell: (data) => {
        const col = columns[data.column.index];
        if (col?.align) data.cell.styles.halign = col.align;
      }
    });

    if (opts.totals) {
      const finalY = doc.lastAutoTable.finalY || 20;
      doc.setFontSize(10);
      let y = finalY + 8;
      Object.entries(opts.totals).forEach(([label, value]) => {
        doc.text(`${label}: ${value}`, 14, y);
        y += 6;
      });
    }

    doc.save(safeFilename(filename) + '.pdf');
  }

  function downloadBlob(blob, filename) {
    const url = URL.createObjectURL(blob);
    const a = document.createElement('a');
    a.href = url; a.download = filename;
    document.body.appendChild(a); a.click();
    document.body.removeChild(a);
    setTimeout(() => URL.revokeObjectURL(url), 1000);
  }

  // Unified export: shows dropdown menu
  // opts = { rows, columns, filename, title, landscape, totals, sheetName }
  async function exportData(format, opts) {
    const { rows, columns, filename } = opts;
    if (!rows || !rows.length) {
      if (window.Layout?.toast) Layout.toast('ไม่มีข้อมูลให้ Export', 'error');
      else alert('ไม่มีข้อมูลให้ Export');
      return;
    }
    try {
      if (format === 'csv') exportCsv(rows, columns, filename);
      else if (format === 'xlsx') await exportExcel(rows, columns, filename, opts.sheetName);
      else if (format === 'pdf') await exportPdf(rows, columns, filename, opts);
      else throw new Error('รูปแบบไม่รองรับ: ' + format);
      if (window.Layout?.toast) Layout.toast('Export สำเร็จ');
    } catch (e) {
      console.error(e);
      if (window.Layout?.toast) Layout.toast('Export ไม่สำเร็จ: ' + (e.message || e), 'error');
    }
  }

  // Render an export button group (CSV / Excel / PDF)
  // targetEl = element to attach, handler = (format) => void
  function renderButtons(handler, size = 'sm') {
    const s = size === 'sm' ? 'btn-sm' : '';
    return `
      <div class="dropdown" style="display:inline-block;position:relative">
        <button class="btn btn-secondary ${s}" onclick="ExportUtil._toggleMenu(this)">
          📥 Export ▾
        </button>
        <div class="export-menu" style="display:none;position:absolute;right:0;top:100%;background:var(--bg-primary,#fff);border:1px solid var(--border,#ddd);border-radius:6px;box-shadow:0 4px 12px rgba(0,0,0,0.12);z-index:100;min-width:140px">
          <button class="export-item" onclick="(${handler})('xlsx');ExportUtil._closeMenus()" style="display:block;width:100%;text-align:left;padding:8px 12px;border:none;background:transparent;cursor:pointer">📊 Excel (.xlsx)</button>
          <button class="export-item" onclick="(${handler})('csv');ExportUtil._closeMenus()" style="display:block;width:100%;text-align:left;padding:8px 12px;border:none;background:transparent;cursor:pointer">📄 CSV</button>
          <button class="export-item" onclick="(${handler})('pdf');ExportUtil._closeMenus()" style="display:block;width:100%;text-align:left;padding:8px 12px;border:none;background:transparent;cursor:pointer">📑 PDF</button>
        </div>
      </div>`;
  }

  function _toggleMenu(btn) {
    const menu = btn.nextElementSibling;
    const isOpen = menu.style.display === 'block';
    _closeMenus();
    if (!isOpen) menu.style.display = 'block';
  }
  function _closeMenus() {
    document.querySelectorAll('.export-menu').forEach(m => m.style.display = 'none');
  }
  document.addEventListener('click', e => {
    if (!e.target.closest('.dropdown')) _closeMenus();
  });

  // Hover effect for menu items
  const style = document.createElement('style');
  style.textContent = `.export-item:hover { background: var(--bg-secondary, #f5f5f5) !important; }`;
  document.head.appendChild(style);

  return { exportData, exportCsv, exportExcel, exportPdf, renderButtons, _toggleMenu, _closeMenus };
})();
