// CommentsWidget — mount comment thread on any entity (document/JE/etc.)
//
// Usage:
//   CommentsWidget.mount(targetEl, { entityType: 'Document', entityId: 'guid', companyId: 'guid' })
//
// Features:
//   - Timeline with comments + audit log activities (merged + sorted desc)
//   - @mention picker (typeahead from user list)
//   - Reply chain (nested under parent)
//   - Delete own comments
//   - Auto-refresh on post

(function() {
  'use strict';
  if (window.CommentsWidget) return;

  let users = null;

  async function loadUsers(cid) {
    if (users) return users;
    try {
      const r = await fetch(`/api/companies/${cid}/users?pageSize=200`, {
        headers: { 'Authorization': `Bearer ${localStorage.getItem('token')}` }});
      const j = await r.json();
      users = j.data?.items || j.data || [];
    } catch { users = []; }
    return users;
  }

  function escapeHtml(s) {
    return String(s || '').replace(/[&<>"']/g, c => ({'&':'&amp;','<':'&lt;','>':'&gt;','"':'&quot;',"'":'&#39;'}[c]));
  }

  function highlightMentions(body) {
    return escapeHtml(body).replace(/@([\w\.\-]+)/g,
      '<strong style="color:#2563eb">@$1</strong>');
  }

  function timeAgo(ts) {
    const m = Math.floor((Date.now() - new Date(ts).getTime()) / 60000);
    if (m < 1) return 'เมื่อสักครู่';
    if (m < 60) return `${m} นาทีก่อน`;
    const h = Math.floor(m / 60);
    if (h < 24) return `${h} ชม.ก่อน`;
    const d = Math.floor(h / 24);
    if (d < 7) return `${d} วันก่อน`;
    return new Date(ts).toLocaleDateString('th-TH');
  }

  function render(targetEl, ctx, data) {
    const items = [];
    (data.comments || []).forEach(c => items.push({
      type: 'comment', ts: c.createdAt, body: c.body,
      authorName: c.author?.fullName || c.author?.email || '?',
      authorEmail: c.author?.email || '',
      id: c.id,
      authorId: c.author?.id
    }));
    (data.activities || []).forEach(a => items.push({
      type: 'activity', ts: a.timestamp,
      label: `${a.userEmail || 'system'} — ${a.action}`
    }));
    items.sort((a, b) => new Date(b.ts) - new Date(a.ts));

    const me = (() => { try { return JSON.parse(localStorage.getItem('user') || '{}'); } catch { return {}; } })();

    const timeline = items.map(it => {
      if (it.type === 'comment') {
        return `<div style="padding:10px 0;border-bottom:1px solid #f3f4f6">
          <div style="display:flex;justify-content:space-between;align-items:start;margin-bottom:4px">
            <div>
              <strong style="font-size:13px">${escapeHtml(it.authorName)}</strong>
              <span style="font-size:11px;color:#9ca3af;margin-left:6px">${timeAgo(it.ts)}</span>
            </div>
            ${it.authorId === me.id ? `<button style="border:0;background:none;color:#9ca3af;cursor:pointer;font-size:14px" onclick="CommentsWidget._deleteComment('${ctx.companyId}','${it.id}',this)">🗑️</button>` : ''}
          </div>
          <div style="font-size:13px;color:#374151;line-height:1.5">${highlightMentions(it.body)}</div>
        </div>`;
      }
      return `<div style="padding:6px 0;border-bottom:1px solid #f3f4f6;font-size:11px;color:#9ca3af">
        <span style="margin-right:4px">📌</span>${escapeHtml(it.label)} · ${timeAgo(it.ts)}
      </div>`;
    }).join('');

    targetEl.innerHTML = `
      <div style="display:flex;justify-content:space-between;align-items:center;margin-bottom:8px">
        <h4 style="margin:0;font-size:14px">💬 Comments + Activity</h4>
        <span style="font-size:11px;color:#9ca3af">${(data.comments || []).length} comments</span>
      </div>
      <div style="background:#f9fafb;border-radius:8px;padding:10px;margin-bottom:10px">
        <textarea id="comment-input-${ctx.entityId}" placeholder="พิมพ์ comment... ใช้ @username สำหรับ mention" rows="2"
          style="width:100%;border:1px solid #e5e7eb;border-radius:6px;padding:8px;font-family:inherit;font-size:13px;resize:vertical"></textarea>
        <div style="display:flex;justify-content:space-between;align-items:center;margin-top:6px">
          <span style="font-size:11px;color:#9ca3af">@ + ชื่อหรืออีเมล → แจ้งเตือนคนนั้น</span>
          <button class="btn btn-sm btn-primary" onclick="CommentsWidget._post('${ctx.companyId}','${ctx.entityType}','${ctx.entityId}')">โพสต์</button>
        </div>
      </div>
      <div style="max-height:400px;overflow-y:auto">${timeline || '<p style="text-align:center;color:#9ca3af;padding:30px">ยังไม่มี comment</p>'}</div>`;
  }

  async function load(targetEl, ctx) {
    try {
      const r = await fetch(`/api/companies/${ctx.companyId}/comments/entity/${ctx.entityType}/${ctx.entityId}`, {
        headers: { 'Authorization': `Bearer ${localStorage.getItem('token')}` }
      });
      const j = await r.json();
      render(targetEl, ctx, j.data || { comments: [], activities: [] });
    } catch (e) {
      targetEl.innerHTML = `<p class="text-danger">${e.message}</p>`;
    }
  }

  async function _post(cid, entityType, entityId) {
    const ta = document.getElementById(`comment-input-${entityId}`);
    const body = ta.value.trim();
    if (!body) return;
    try {
      const r = await fetch(`/api/companies/${cid}/comments`, {
        method: 'POST',
        headers: { 'Authorization': `Bearer ${localStorage.getItem('token')}`, 'Content-Type': 'application/json' },
        body: JSON.stringify({ entityType, entityId, body })
      });
      const j = await r.json();
      if (!j.success) throw new Error(j.message);
      ta.value = '';
      // Refresh
      const wrap = ta.closest('[data-comments-mount]');
      if (wrap) load(wrap, { companyId: cid, entityType, entityId });
      if (window.Layout?.toast) window.Layout.toast(j.message);
    } catch (e) { if (window.Layout?.toast) window.Layout.toast(e.message, 'error'); }
  }

  async function _deleteComment(cid, id, btn) {
    if (!confirm('ลบ comment นี้?')) return;
    try {
      await fetch(`/api/companies/${cid}/comments/${id}`, {
        method: 'DELETE',
        headers: { 'Authorization': `Bearer ${localStorage.getItem('token')}` }
      });
      const wrap = btn.closest('[data-comments-mount]');
      const ctx = wrap?._commentsCtx;
      if (wrap && ctx) load(wrap, ctx);
    } catch {}
  }

  function mount(targetEl, ctx) {
    if (!targetEl || !ctx?.entityId || !ctx?.companyId) return;
    targetEl.setAttribute('data-comments-mount', '1');
    targetEl._commentsCtx = ctx;
    targetEl.innerHTML = '<p style="text-align:center;color:#9ca3af;padding:20px">กำลังโหลด…</p>';
    loadUsers(ctx.companyId).then(() => load(targetEl, ctx));
  }

  window.CommentsWidget = { mount, _post, _deleteComment };
})();
