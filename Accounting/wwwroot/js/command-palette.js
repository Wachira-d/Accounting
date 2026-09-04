// #1 — Global Cmd+K command palette
// ค้นทุกอย่างจาก keyboard: เอกสาร, ลูกค้า, รายงาน, settings.
// Trigger: Cmd+K / Ctrl+K / "/" บน input ที่ไม่ได้ focus
//
// Modeled after Linear/Notion/GitHub command palette:
//   - keyboard-driven (Tab/Arrow/Enter)
//   - fuzzy-ish substring match
//   - groups: หน้าจอ / actions / search documents/contacts
//   - recently-used pinned at top
(function() {
  'use strict';
  if (window.CmdPalette) return;     // singleton

  const RECENTS_KEY = 'cmdPalette.recents';
  const MAX_RECENTS = 8;

  // Static commands — สามารถ extend ได้จากภายนอกผ่าน window.CmdPalette.register()
  const staticCommands = [
    { id: 'nav-dashboard', label: 'ไปหน้าหลัก (Dashboard)', icon: '🏠', keys: 'dashboard หน้าหลัก home', action: () => location.href = ((localStorage.getItem('uiMode') || 'simple') === 'advanced' ? '/app.html' : '/simple.html') },
    { id: 'nav-aging', label: 'รายงานอายุลูกหนี้/เจ้าหนี้', icon: '⏳', keys: 'aging ลูกหนี้ เจ้าหนี้', action: () => location.href = '/pages/aging.html' },
    { id: 'nav-cash', label: 'จัดการเงินสด (PDC + Forecast + Bulk Pay)', icon: '🏦', keys: 'cash pdc forecast เช็ค เงินสด', action: () => location.href = '/pages/cash-management.html' },
    { id: 'nav-mobile', label: 'รับเงินสดด่วน (Mobile)', icon: '📱', keys: 'mobile receipt รับเงิน', action: () => location.href = '/pages/mobile-receipt.html' },
    { id: 'nav-docs', label: 'เอกสารทั้งหมด', icon: '📄', keys: 'documents เอกสาร invoice receipt', action: () => location.href = '/pages/documents.html' },
    { id: 'nav-payroll', label: 'เงินเดือน + ภ.ง.ด.1', icon: '👥', keys: 'payroll salary เงินเดือน', action: () => location.href = '/pages/payroll.html' },
    { id: 'nav-bank', label: 'กระทบยอดธนาคาร', icon: '🏛️', keys: 'bank reconcile กระทบยอด', action: () => location.href = '/pages/bank.html' },
    { id: 'nav-tax', label: 'ภาษี & รายงานสรรพากร', icon: '🧾', keys: 'tax ภาษี ภพ.30 ภงด', action: () => location.href = '/pages/tax.html' },
    { id: 'nav-reports', label: 'รายงานการเงิน', icon: '📊', keys: 'reports งบ balance sheet pnl กำไร', action: () => location.href = '/pages/reports.html' },
    { id: 'act-new-invoice', label: '+ สร้างใบแจ้งหนี้ใหม่', icon: '📝', keys: 'new invoice ใหม่ ใบแจ้งหนี้', action: () => location.href = '/pages/documents.html?openCreate=revenue&type=Invoice' },
    { id: 'act-new-expense', label: '+ บันทึกค่าใช้จ่าย', icon: '💸', keys: 'new expense ค่าใช้จ่าย', action: () => location.href = '/pages/documents.html?openCreate=expense&type=Expense' },
    { id: 'act-new-pv', label: '+ สร้างใบสำคัญจ่าย', icon: '💵', keys: 'new pv payment voucher จ่าย', action: () => location.href = '/pages/documents.html?openCreate=expense&type=PaymentVoucher' },
  ];

  function getRecents() {
    try { return JSON.parse(localStorage.getItem(RECENTS_KEY) || '[]'); } catch { return []; }
  }
  function pushRecent(cmdId) {
    let r = getRecents().filter(x => x !== cmdId);
    r.unshift(cmdId);
    r = r.slice(0, MAX_RECENTS);
    localStorage.setItem(RECENTS_KEY, JSON.stringify(r));
  }

  function build() {
    const overlay = document.createElement('div');
    overlay.id = 'cmd-palette-overlay';
    overlay.style.cssText = 'display:none;position:fixed;inset:0;background:rgba(0,0,0,0.4);z-index:9999;align-items:flex-start;justify-content:center;padding-top:80px;backdrop-filter:blur(4px);';
    overlay.innerHTML = `
      <div id="cmd-palette-box" style="background:#fff;border-radius:12px;width:90%;max-width:580px;box-shadow:0 20px 60px rgba(0,0,0,0.3);overflow:hidden;font-family:inherit">
        <div style="padding:14px 16px;border-bottom:1px solid #e5e7eb;display:flex;align-items:center;gap:10px">
          <span style="font-size:18px">🔎</span>
          <input id="cmd-palette-input" placeholder="ค้นหา หน้าจอ / เอกสาร / การกระทำ... (Esc ปิด)" autocomplete="off"
            style="flex:1;border:0;outline:none;font-size:16px;font-family:inherit">
          <kbd style="background:#f3f4f6;padding:3px 7px;border-radius:4px;font-size:11px;color:#6b7280">Esc</kbd>
        </div>
        <div id="cmd-palette-results" style="max-height:50vh;overflow-y:auto;padding:8px"></div>
        <div style="padding:8px 16px;font-size:11px;color:#9ca3af;background:#f9fafb;border-top:1px solid #e5e7eb">
          ↑↓ เลือก · Enter เปิด · Esc ปิด · พิมพ์ <kbd>/</kbd> หรือ <kbd>⌘K</kbd> เปิดใหม่
        </div>
      </div>`;
    document.body.appendChild(overlay);
    return overlay;
  }

  let overlay, inputEl, resultsEl;
  let allCommands = [...staticCommands];
  let selectedIdx = 0;
  let visibleCommands = [];

  function open() {
    if (!overlay) {
      overlay = build();
      inputEl = overlay.querySelector('#cmd-palette-input');
      resultsEl = overlay.querySelector('#cmd-palette-results');
      inputEl.addEventListener('input', render);
      inputEl.addEventListener('keydown', onKey);
      overlay.addEventListener('click', e => { if (e.target === overlay) close(); });
    }
    overlay.style.display = 'flex';
    inputEl.value = '';
    selectedIdx = 0;
    render();
    setTimeout(() => inputEl.focus(), 50);
  }
  function close() {
    if (overlay) overlay.style.display = 'none';
  }

  function score(q, cmd) {
    if (!q) return 1;
    q = q.toLowerCase();
    const hay = (cmd.label + ' ' + (cmd.keys || '')).toLowerCase();
    if (hay.startsWith(q)) return 100;
    if (hay.includes(q)) return 50;
    // Token loose match
    const tokens = q.split(/\s+/);
    let hits = 0;
    for (const t of tokens) if (hay.includes(t)) hits++;
    return hits === tokens.length ? 25 : 0;
  }

  let dynamicCommands = [];
  let dynSearchTimer = null;

  async function fetchDynamic(q) {
    if (!q || q.length < 2) { dynamicCommands = []; return; }
    try {
      const cid = (() => { try { return JSON.parse(localStorage.getItem('currentCompany'))?.id; } catch { return null; } })();
      if (!cid) return;
      const r = await fetch(`/api/companies/${cid}/search/quick?q=${encodeURIComponent(q)}&perKindLimit=5`, {
        headers: { 'Authorization': `Bearer ${localStorage.getItem('token')}` }
      });
      const j = await r.json();
      // QuickSearchHit { Kind, Id, Title, Subtitle, Detail, DeepLink }
      const hits = Array.isArray(j.data) ? j.data : [];
      dynamicCommands = hits.map(h => ({
        id: 'dyn-' + h.kind + '-' + h.id,
        icon: h.kind === 'Document' ? '📄' :
              h.kind === 'Contact' ? '👤' :
              h.kind === 'Product' ? '📦' :
              h.kind === 'JournalEntry' ? '📒' : '•',
        label: h.title + (h.subtitle ? ' — ' + h.subtitle : ''),
        keys: q,
        action: () => { if (h.deepLink) location.href = h.deepLink; }
      }));
      render();
    } catch {}
  }

  function render() {
    const q = inputEl.value.trim();
    // Debounce dynamic search
    clearTimeout(dynSearchTimer);
    dynSearchTimer = setTimeout(() => fetchDynamic(q), 250);
    let filtered;
    if (!q) {
      dynamicCommands = [];
      const recents = getRecents();
      const recentCmds = recents.map(id => allCommands.find(c => c.id === id)).filter(Boolean);
      const others = allCommands.filter(c => !recents.includes(c.id));
      filtered = [
        ...(recentCmds.length ? [{ groupHeader: 'ใช้บ่อย' }, ...recentCmds] : []),
        { groupHeader: 'ทั้งหมด' },
        ...others.slice(0, 10)
      ];
    } else {
      const staticMatches = allCommands.map(c => ({ c, s: score(q, c) }))
        .filter(x => x.s > 0)
        .sort((a, b) => b.s - a.s)
        .slice(0, 8)
        .map(x => x.c);
      filtered = [];
      if (staticMatches.length > 0) {
        filtered.push({ groupHeader: 'หน้าจอ + คำสั่ง' });
        filtered.push(...staticMatches);
      }
      if (dynamicCommands.length > 0) {
        filtered.push({ groupHeader: 'ค้นพบในระบบ (เอกสาร / ลูกค้า / JE)' });
        filtered.push(...dynamicCommands.slice(0, 10));
      }
      if (filtered.length === 0) filtered = [{ empty: true }];
    }
    visibleCommands = filtered.filter(f => !f.groupHeader && !f.empty);
    if (selectedIdx >= visibleCommands.length) selectedIdx = Math.max(0, visibleCommands.length - 1);

    let html = '';
    let cmdI = 0;
    for (const item of filtered) {
      if (item.groupHeader) {
        html += `<div style="padding:6px 10px;font-size:11px;color:#9ca3af;text-transform:uppercase;letter-spacing:0.5px;font-weight:600">${item.groupHeader}</div>`;
        continue;
      }
      if (item.empty) {
        html += `<div style="padding:20px;text-align:center;color:#9ca3af">ไม่พบรายการ — ลองเปลี่ยน keyword</div>`;
        continue;
      }
      const sel = cmdI === selectedIdx;
      html += `<div data-cmd-i="${cmdI}" class="cmd-row" style="display:flex;align-items:center;gap:12px;padding:10px 12px;border-radius:8px;cursor:pointer;background:${sel ? '#eff6ff' : 'transparent'}">
        <span style="font-size:18px">${item.icon || '•'}</span>
        <span style="flex:1;font-size:14px;color:#111">${item.label}</span>
        ${sel ? '<kbd style="background:#dbeafe;padding:2px 6px;border-radius:4px;font-size:10px;color:#1e40af">↵</kbd>' : ''}
      </div>`;
      cmdI++;
    }
    resultsEl.innerHTML = html;
    resultsEl.querySelectorAll('.cmd-row').forEach(el => {
      el.addEventListener('click', () => execute(parseInt(el.dataset.cmdI)));
      el.addEventListener('mouseenter', () => {
        selectedIdx = parseInt(el.dataset.cmdI);
        render();
      });
    });
  }

  function execute(idx) {
    const cmd = visibleCommands[idx];
    if (!cmd) return;
    pushRecent(cmd.id);
    close();
    try { cmd.action(); } catch (e) { console.error(e); }
  }

  function onKey(e) {
    if (e.key === 'Escape') { close(); return; }
    if (e.key === 'ArrowDown') {
      e.preventDefault();
      selectedIdx = Math.min(visibleCommands.length - 1, selectedIdx + 1);
      render();
    } else if (e.key === 'ArrowUp') {
      e.preventDefault();
      selectedIdx = Math.max(0, selectedIdx - 1);
      render();
    } else if (e.key === 'Enter') {
      e.preventDefault();
      execute(selectedIdx);
    }
  }

  // Global hotkey: Cmd+K / Ctrl+K / "/" (when not focused on text input)
  document.addEventListener('keydown', e => {
    if ((e.metaKey || e.ctrlKey) && e.key === 'k') {
      e.preventDefault();
      open();
    } else if (e.key === '/' && !['INPUT', 'TEXTAREA', 'SELECT'].includes(document.activeElement?.tagName)) {
      e.preventDefault();
      open();
    }
  });

  window.CmdPalette = {
    open, close,
    register(cmds) {
      if (!Array.isArray(cmds)) cmds = [cmds];
      for (const c of cmds) if (c.id && c.label && c.action) allCommands.push(c);
    }
  };
})();
