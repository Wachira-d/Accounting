// #25 — In-app notification bell. Mount เป็น icon ใน header ทุกหน้า
// (ผ่าน layout.js). Poll ทุก 60s + show count badge + dropdown list.
(function() {
  'use strict';
  if (window.NotificationBell) return;

  let cid, intervalId;

  async function fetchCount() {
    if (!cid) return { count: 0 };
    try {
      const r = await fetch(`/api/companies/${cid}/notifications/count?unreadOnly=true`, {
        headers: { 'Authorization': `Bearer ${localStorage.getItem('token')}` }
      });
      const j = await r.json();
      return { count: j.data?.count ?? 0 };
    } catch { return { count: 0 }; }
  }

  async function fetchList() {
    if (!cid) return [];
    try {
      const r = await fetch(`/api/companies/${cid}/notifications?pageSize=10`, {
        headers: { 'Authorization': `Bearer ${localStorage.getItem('token')}` }
      });
      const j = await r.json();
      return j.data?.items || j.data || [];
    } catch { return []; }
  }

  function renderBadge(count) {
    const badge = document.getElementById('notif-bell-badge');
    if (!badge) return;
    if (count > 0) {
      badge.textContent = count > 99 ? '99+' : count;
      badge.style.display = 'inline-block';
    } else {
      badge.style.display = 'none';
    }
  }

  async function refresh() {
    const { count } = await fetchCount();
    renderBadge(count);
  }

  function openDropdown() {
    const existing = document.getElementById('notif-bell-dropdown');
    if (existing) { existing.remove(); return; }
    const dd = document.createElement('div');
    dd.id = 'notif-bell-dropdown';
    dd.style.cssText = 'position:absolute;top:42px;right:0;background:#fff;border-radius:8px;box-shadow:0 4px 16px rgba(0,0,0,0.15);width:340px;max-height:480px;overflow-y:auto;z-index:1000;border:1px solid #e5e7eb';
    dd.innerHTML = '<div style="padding:16px;text-align:center;color:#9ca3af">กำลังโหลด...</div>';
    document.getElementById('notif-bell-btn').appendChild(dd);
    fetchList().then(list => {
      if (list.length === 0) {
        dd.innerHTML = '<div style="padding:30px;text-align:center;color:#9ca3af;font-size:14px">ไม่มีการแจ้งเตือน</div>';
        return;
      }
      dd.innerHTML = `
        <div style="padding:10px 14px;border-bottom:1px solid #e5e7eb;display:flex;justify-content:space-between;align-items:center">
          <strong style="font-size:14px">การแจ้งเตือน</strong>
          <button style="border:0;background:none;color:#6b7280;font-size:12px;cursor:pointer" onclick="NotificationBell.markAllRead()">อ่านทั้งหมด</button>
        </div>` +
        list.map(n => `
          <div style="padding:10px 14px;border-bottom:1px solid #f3f4f6;${n.isRead ? '' : 'background:#eff6ff'}">
            <div style="font-size:13px;color:#111;margin-bottom:2px">${escapeHtml(n.title || '')}</div>
            <div style="font-size:12px;color:#6b7280;line-height:1.4">${escapeHtml(n.message || '')}</div>
            <div style="font-size:11px;color:#9ca3af;margin-top:4px">${new Date(n.createdAt).toLocaleString('th-TH')}</div>
          </div>`).join('');
    });

    // Close on outside click
    setTimeout(() => {
      document.addEventListener('click', function closer(e) {
        if (!dd.contains(e.target) && e.target.id !== 'notif-bell-btn') {
          dd.remove();
          document.removeEventListener('click', closer);
        }
      });
    }, 50);
  }

  async function markAllRead() {
    try {
      await fetch(`/api/companies/${cid}/notifications/mark-all-read`, {
        method: 'POST',
        headers: { 'Authorization': `Bearer ${localStorage.getItem('token')}` }
      });
      const dd = document.getElementById('notif-bell-dropdown');
      if (dd) dd.remove();
      refresh();
    } catch {}
  }

  function escapeHtml(s) {
    return String(s || '').replace(/[&<>"']/g, c => ({'&':'&amp;','<':'&lt;','>':'&gt;','"':'&quot;',"'":'&#39;'}[c]));
  }

  function mount(headerEl) {
    cid = (() => { try { return JSON.parse(localStorage.getItem('currentCompany'))?.id; } catch { return null; } })();
    if (!cid || !headerEl) return;
    if (document.getElementById('notif-bell-btn')) return;
    const btn = document.createElement('button');
    btn.id = 'notif-bell-btn';
    btn.title = 'การแจ้งเตือน';
    btn.style.cssText = 'position:relative;background:none;border:0;cursor:pointer;font-size:22px;padding:6px 10px;line-height:1';
    btn.innerHTML = `🔔<span id="notif-bell-badge" style="display:none;position:absolute;top:-2px;right:-2px;background:#ef4444;color:#fff;font-size:10px;font-weight:700;padding:1px 5px;border-radius:10px;line-height:1.3"></span>`;
    btn.addEventListener('click', e => { e.stopPropagation(); openDropdown(); });
    headerEl.appendChild(btn);
    refresh();
    intervalId = setInterval(refresh, 60000);
  }

  window.NotificationBell = { mount, refresh, markAllRead };
})();
