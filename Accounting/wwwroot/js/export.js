// Reusable export module: CSV / Excel / PDF
// Dependencies loaded lazily from CDN:
//   - SheetJS (xlsx) for Excel
//   - PDF: uses native browser print-to-PDF (renders Thai correctly; no fragile font setup)

window.ExportUtil = (function () {
  const CDN = {
    xlsx: 'https://cdn.jsdelivr.net/npm/xlsx@0.18.5/dist/xlsx.full.min.js'
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
  // Browser-native print-to-PDF approach. Generates an HTML page in a new window
  // with Google Fonts' Noto Sans Thai, then triggers window.print(). The user picks
  // "Save as PDF" in the print dialog to download. This is the only reliable way
  // to get correctly-rendered Thai glyphs without bundling a font into jsPDF
  // (the previous CDN-based Sarabun font path failed silently and produced garbled
  // boxes when the font URL or registration was unavailable).
  async function exportPdf(rows, columns, filename, opts = {}) {
    const escapeHtml = (s) => String(s ?? '')
      .replace(/&/g, '&amp;').replace(/</g, '&lt;').replace(/>/g, '&gt;')
      .replace(/"/g, '&quot;').replace(/'/g, '&#39;');

    const headerCells = columns.map(c =>
      `<th style="text-align:${c.align || 'left'}">${escapeHtml(c.label)}</th>`).join('');

    const bodyRows = rows.map(r => '<tr>' + columns.map(c => {
      const v = c.key.split('.').reduce((acc, k) => acc?.[k], r);
      const val = c.format ? c.format(v, r) : (v == null ? '' : v);
      return `<td style="text-align:${c.align || 'left'}">${escapeHtml(val)}</td>`;
    }).join('') + '</tr>').join('');

    const totalsBlock = opts.totals
      ? '<div class="totals">' + Object.entries(opts.totals).map(
          ([label, value]) => `<div><strong>${escapeHtml(label)}:</strong> ${escapeHtml(value)}</div>`
        ).join('') + '</div>'
      : '';

    const orientation = opts.landscape ? 'landscape' : 'portrait';
    const safeFn = safeFilename(filename) + '.pdf';
    const titleText = opts.title || filename || '';
    const subtitleText = opts.subtitle || '';

    const html = `<!DOCTYPE html>
<html lang="th">
<head>
<meta charset="UTF-8">
<title>${escapeHtml(safeFn)}</title>
<link href="https://fonts.googleapis.com/css2?family=Noto+Sans+Thai:wght@300;400;500;600;700&display=swap" rel="stylesheet">
<style>
  @page { size: A4 ${orientation}; margin: 12mm 10mm; }
  * { box-sizing: border-box; }
  body { margin: 0; padding: 16px 20px; font-family: 'Noto Sans Thai', 'Sarabun', sans-serif; color: #111; font-size: 11px; }
  h1 { margin: 0 0 4px 0; font-size: 16px; font-weight: 700; }
  .subtitle { margin: 0 0 12px 0; color: #555; font-size: 11px; }
  .meta { color: #888; font-size: 10px; margin-bottom: 8px; }
  table { width: 100%; border-collapse: collapse; font-size: 10px; }
  thead th { background: #34495e; color: #fff; padding: 6px 8px; font-weight: 600; border: 1px solid #2c3e50; }
  tbody td { padding: 5px 8px; border: 1px solid #ddd; word-break: break-word; }
  tbody tr:nth-child(even) td { background: #f5f5f5; }
  .totals { margin-top: 16px; font-size: 11px; line-height: 1.6; }
  .totals div { display: inline-block; margin-right: 24px; }
  .print-btn { position: fixed; top: 12px; right: 12px; z-index: 9999; padding: 10px 16px; background: #4F46E5; color: #fff; border: none; border-radius: 6px; cursor: pointer; font-family: inherit; font-size: 13px; box-shadow: 0 2px 8px rgba(0,0,0,0.15); }
  .print-btn:hover { background: #4338ca; }
  @media print { .print-btn { display: none; } body { padding: 0; } }
</style>
</head>
<body>
  <button class="print-btn" onclick="window.print()">📥 บันทึกเป็น PDF / พิมพ์</button>
  ${titleText ? `<h1>${escapeHtml(titleText)}</h1>` : ''}
  ${subtitleText ? `<div class="subtitle">${escapeHtml(subtitleText)}</div>` : ''}
  <div class="meta">สร้างเมื่อ ${new Date().toLocaleString('th-TH')}</div>
  <table>
    <thead><tr>${headerCells}</tr></thead>
    <tbody>${bodyRows}</tbody>
  </table>
  ${totalsBlock}
  <script>
    // Wait for the Thai font to actually load before triggering print, otherwise the
    // PDF bakes in a fallback font and Thai text will look misaligned in some viewers.
    (async () => {
      try { if (document.fonts && document.fonts.ready) await document.fonts.ready; } catch (e) {}
      // Auto-trigger so the user goes straight to the OS print dialog and chooses "Save as PDF".
      setTimeout(() => { try { window.print(); } catch (e) {} }, 300);
    })();
  <\/script>
</body>
</html>`;

    const win = window.open('', '_blank', 'width=1024,height=768');
    if (!win) {
      throw new Error('เบราว์เซอร์ปิดกั้น popup — โปรดอนุญาต popup สำหรับเว็บไซต์นี้แล้วลองอีกครั้ง');
    }
    win.document.open();
    win.document.write(html);
    win.document.close();
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
