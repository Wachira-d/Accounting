// ═══════════════════════════════════════════════════════════════════════
//  NextAcc public chat widget — ปุ่มแชทลอยมุมขวาล่างของหน้าแรก
//  ถามฟีเจอร์/ข้อสงสัยกับบอท (RAG + DeepSeek ผ่าน orchestrator ฝั่ง server)
//  + ขอคุยกับเจ้าหน้าที่ (handoff → admin console ตอบ, widget polling รับ)
//  self-contained: สร้าง DOM/CSS เอง — แปะ <script src> บรรทัดเดียวใช้ได้เลย
// ═══════════════════════════════════════════════════════════════════════
(function () {
  const API = '/api/public/chat';
  const LS_TOKEN = 'nx_chat_session';
  const LS_CONV = 'nx_chat_conv';

  const state = {
    open: false,
    sending: false,
    conversationId: localStorage.getItem(LS_CONV) || null,
    sessionToken: localStorage.getItem(LS_TOKEN) || null,
    status: 'AiHandling',
    lastMsgAt: null,
    pollTimer: null,
  };

  // ── styles ──
  const css = `
  .nxc-fab{position:fixed;right:20px;bottom:20px;width:56px;height:56px;border-radius:50%;
    background:#1e40af;color:#fff;border:none;font-size:26px;cursor:pointer;z-index:9998;
    box-shadow:0 4px 14px rgba(30,64,175,.4);transition:transform .15s}
  .nxc-fab:hover{transform:scale(1.08)}
  .nxc-panel{position:fixed;right:20px;bottom:88px;width:360px;max-width:calc(100vw - 32px);
    height:520px;max-height:calc(100vh - 120px);background:#fff;border-radius:16px;z-index:9999;
    box-shadow:0 12px 40px rgba(0,0,0,.22);display:none;flex-direction:column;overflow:hidden;
    font-family:'Noto Sans Thai',system-ui,sans-serif}
  .nxc-panel.open{display:flex}
  .nxc-head{background:#1e40af;color:#fff;padding:14px 16px;display:flex;align-items:center;gap:10px}
  .nxc-head .t{flex:1;font-weight:700;font-size:15px}
  .nxc-head .s{font-size:11px;opacity:.85}
  .nxc-head button{background:none;border:none;color:#fff;font-size:20px;cursor:pointer}
  .nxc-body{flex:1;overflow-y:auto;padding:14px;background:#f8fafc}
  .nxc-msg{margin-bottom:10px;display:flex;flex-direction:column}
  .nxc-msg .b{max-width:85%;padding:9px 12px;border-radius:12px;font-size:13px;line-height:1.55;
    white-space:pre-wrap;word-break:break-word}
  .nxc-msg.user{align-items:flex-end}
  .nxc-msg.user .b{background:#1e40af;color:#fff;border-bottom-right-radius:4px}
  .nxc-msg.bot .b{background:#fff;border:1px solid #e2e8f0;border-bottom-left-radius:4px}
  .nxc-msg.agent .b{background:#ecfdf5;border:1px solid #86efac;border-bottom-left-radius:4px}
  .nxc-msg .tag{font-size:10px;color:#94a3b8;margin-top:3px;display:flex;gap:8px;align-items:center}
  .nxc-vote{cursor:pointer;opacity:.55;font-size:12px}
  .nxc-vote:hover,.nxc-vote.on{opacity:1}
  .nxc-foot{padding:10px;border-top:1px solid #e2e8f0;background:#fff}
  .nxc-row{display:flex;gap:8px}
  .nxc-in{flex:1;border:1px solid #cbd5e1;border-radius:10px;padding:9px 12px;font-size:13px;
    font-family:inherit;resize:none;max-height:80px}
  .nxc-send{background:#1e40af;color:#fff;border:none;border-radius:10px;padding:0 16px;
    cursor:pointer;font-size:15px}
  .nxc-send:disabled{opacity:.5;cursor:default}
  .nxc-note{font-size:10px;color:#94a3b8;margin-top:6px;text-align:center}
  .nxc-agentbtn{background:none;border:none;color:#1e40af;font-size:11px;cursor:pointer;
    text-decoration:underline;margin-top:4px}
  .nxc-typing{font-size:12px;color:#94a3b8;padding:4px 12px}
  `;

  function h(tag, cls, text) {
    const el = document.createElement(tag);
    if (cls) el.className = cls;
    if (text != null) el.textContent = text;
    return el;
  }

  function build() {
    const style = document.createElement('style');
    style.textContent = css;
    document.head.appendChild(style);

    const fab = h('button', 'nxc-fab', '💬');
    fab.title = 'สอบถามเกี่ยวกับระบบ';
    fab.onclick = toggle;

    const panel = h('div', 'nxc-panel');
    panel.id = 'nxcPanel';
    panel.innerHTML = `
      <div class="nxc-head">
        <span style="font-size:22px">🤖</span>
        <div style="flex:1">
          <div class="t">ผู้ช่วย NextAcc</div>
          <div class="s" id="nxcStatus">ถามเรื่องฟีเจอร์/การใช้งานได้เลย</div>
        </div>
        <button onclick="NxChat.toggle()" aria-label="ปิด">×</button>
      </div>
      <div class="nxc-body" id="nxcBody"></div>
      <div class="nxc-typing" id="nxcTyping" style="display:none">กำลังพิมพ์…</div>
      <div class="nxc-foot">
        <div class="nxc-row">
          <textarea class="nxc-in" id="nxcInput" rows="1" maxlength="1000"
            placeholder="พิมพ์คำถาม… (Enter เพื่อส่ง)"></textarea>
          <button class="nxc-send" id="nxcSend" aria-label="ส่ง">➤</button>
        </div>
        <div style="text-align:center">
          <button class="nxc-agentbtn" onclick="NxChat.requestAgent()">🙋 ติดต่อเจ้าหน้าที่</button>
          <span style="color:#cbd5e1">·</span>
          <button class="nxc-agentbtn" onclick="NxChat.rate()">⭐ ให้คะแนน</button>
        </div>
        <div class="nxc-note">บอทตอบจากข้อมูลระบบ — ข้อความถูกเก็บเพื่อปรับปรุงบริการ (ลบอัตโนมัติใน 90 วัน)</div>
      </div>`;
    document.body.appendChild(fab);
    document.body.appendChild(panel);

    const input = panel.querySelector('#nxcInput');
    input.addEventListener('keydown', e => {
      if (e.key === 'Enter' && !e.shiftKey) { e.preventDefault(); send(); }
    });
    panel.querySelector('#nxcSend').onclick = send;

    if (state.conversationId) welcome('ยินดีต้อนรับกลับครับ 👋 ถามต่อได้เลย');
    else welcome('สวัสดีครับ 👋 ผมช่วยตอบเรื่องฟีเจอร์ ราคา และการใช้งาน NextAcc ได้ครับ\nลองถามเช่น "สแกนบิลอัตโนมัติได้ไหม" หรือ "รองรับ ภ.พ.30 ไหม"');
  }

  function welcome(text) { addMsg('bot', text, { noTag: true }); }

  function toggle() {
    state.open = !state.open;
    document.getElementById('nxcPanel').classList.toggle('open', state.open);
    if (state.open && state.conversationId) startPolling();
    if (!state.open) stopPolling();
  }

  function addMsg(kind, text, opts = {}) {
    const body = document.getElementById('nxcBody');
    const wrap = h('div', 'nxc-msg ' + kind);
    const bubble = h('div', 'b', text);
    wrap.appendChild(bubble);
    if (!opts.noTag && kind !== 'user') {
      const tag = h('div', 'tag');
      tag.appendChild(h('span', '', opts.agent ? '👤 เจ้าหน้าที่'
        : (opts.usedAi ? '🤖 AI ตอบ' : '⚙️ ระบบตอบ')));
      if (opts.messageId && !opts.agent) {
        const up = h('span', 'nxc-vote', '👍');
        const dn = h('span', 'nxc-vote', '👎');
        up.onclick = () => vote(opts.messageId, 1, up, dn);
        dn.onclick = () => vote(opts.messageId, -1, dn, up);
        tag.appendChild(up); tag.appendChild(dn);
      }
      wrap.appendChild(tag);
    }
    body.appendChild(wrap);
    body.scrollTop = body.scrollHeight;
  }

  async function send() {
    const input = document.getElementById('nxcInput');
    const text = (input.value || '').trim();
    if (!text || state.sending) return;
    state.sending = true;
    document.getElementById('nxcSend').disabled = true;
    input.value = '';
    addMsg('user', text);
    document.getElementById('nxcTyping').style.display = '';
    try {
      const res = await fetch(API + '/ask', {
        method: 'POST', headers: { 'Content-Type': 'application/json' },
        body: JSON.stringify({ sessionToken: state.sessionToken, message: text }),
      });
      const j = await res.json().catch(() => null);
      const d = j && j.data;
      if (d) {
        if (d.sessionToken) { state.sessionToken = d.sessionToken; localStorage.setItem(LS_TOKEN, d.sessionToken); }
        if (d.conversationId && d.conversationId !== '00000000-0000-0000-0000-000000000000') {
          state.conversationId = d.conversationId; localStorage.setItem(LS_CONV, d.conversationId);
        }
        state.status = d.conversationStatus || state.status;
        addMsg(state.status === 'AgentHandling' ? 'agent' : 'bot', d.answer,
          { usedAi: d.usedAi, messageId: d.messageId });
        state.lastMsgAt = new Date().toISOString();
        updateStatusLine();
        if (state.status === 'WaitingAgent' || state.status === 'AgentHandling') startPolling();
      } else {
        addMsg('bot', (j && j.message) || 'ขออภัย ระบบขัดข้องชั่วคราว ลองใหม่อีกครั้งครับ', { noTag: true });
      }
    } catch (e) {
      addMsg('bot', 'เชื่อมต่อไม่ได้ — ตรวจอินเทอร์เน็ตแล้วลองใหม่ครับ', { noTag: true });
    } finally {
      state.sending = false;
      document.getElementById('nxcSend').disabled = false;
      document.getElementById('nxcTyping').style.display = 'none';
    }
  }

  async function vote(messageId, v, onEl, offEl) {
    onEl.classList.add('on'); offEl.classList.remove('on');
    try {
      await fetch(API + '/vote', {
        method: 'POST', headers: { 'Content-Type': 'application/json' },
        body: JSON.stringify({ messageId, sessionToken: state.sessionToken, vote: v }),
      });
    } catch (e) { /* best-effort */ }
  }

  async function requestAgent() {
    if (!state.conversationId) {
      addMsg('bot', 'พิมพ์คำถามสักข้อความก่อนนะครับ แล้วกดติดต่อเจ้าหน้าที่ได้เลย', { noTag: true });
      return;
    }
    const name = prompt('ชื่อของคุณ (สำหรับให้เจ้าหน้าที่ติดต่อกลับ):') || null;
    const email = name ? (prompt('อีเมลติดต่อกลับ (ไม่ใส่ก็ได้):') || null) : null;
    try {
      const res = await fetch(API + '/request-agent', {
        method: 'POST', headers: { 'Content-Type': 'application/json' },
        body: JSON.stringify({ conversationId: state.conversationId, sessionToken: state.sessionToken, name, email }),
      });
      const j = await res.json().catch(() => null);
      addMsg('bot', (j && j.message) || 'ส่งคำขอแล้ว — เจ้าหน้าที่จะเข้ามาตอบโดยเร็วครับ', { noTag: true });
      state.status = 'WaitingAgent';
      updateStatusLine();
      startPolling();
    } catch (e) { addMsg('bot', 'ส่งคำขอไม่สำเร็จ ลองใหม่ครับ', { noTag: true }); }
  }

  function updateStatusLine() {
    const el = document.getElementById('nxcStatus');
    el.textContent = state.status === 'WaitingAgent' ? '⏳ รอเจ้าหน้าที่เข้ามาตอบ'
      : state.status === 'AgentHandling' ? '🟢 เจ้าหน้าที่กำลังดูแลคุณ'
      : 'ถามเรื่องฟีเจอร์/การใช้งานได้เลย';
  }

  // ── polling ข้อความจากเจ้าหน้าที่ (เฉพาะตอนเปิด panel + อยู่โหมด agent) ──
  function startPolling() {
    if (state.pollTimer || !state.conversationId) return;
    state.pollTimer = setInterval(async () => {
      if (!state.open || !(state.status === 'WaitingAgent' || state.status === 'AgentHandling')) return;
      try {
        const after = state.lastMsgAt ? '&after=' + encodeURIComponent(state.lastMsgAt) : '';
        const res = await fetch(`${API}/${state.conversationId}/messages?sessionToken=${encodeURIComponent(state.sessionToken)}${after}`);
        const j = await res.json().catch(() => null);
        for (const m of (j && j.data) || []) {
          if (m.role === 'Agent') {
            addMsg('agent', m.content, { agent: true });
            state.status = 'AgentHandling';
            updateStatusLine();
          }
          state.lastMsgAt = m.createdAt;
        }
      } catch (e) { /* เงียบ — รอบหน้า */ }
    }, 5000);
  }
  function stopPolling() {
    if (state.pollTimer) { clearInterval(state.pollTimer); state.pollTimer = null; }
  }

  /// ให้คะแนน 1-5 — วัดคุณภาพบอท/เจ้าหน้าที่ (โชว์ในหน้า admin)
  async function rate() {
    if (!state.conversationId) {
      addMsg('bot', 'คุยกันสักหน่อยก่อนนะครับ แล้วค่อยให้คะแนนได้เลย 🙂', { noTag: true });
      return;
    }
    const raw = prompt('ให้คะแนนการช่วยเหลือครั้งนี้ (1 = แย่ ถึง 5 = ดีมาก):', '5');
    if (raw === null) return;
    const score = parseInt(raw, 10);
    if (!(score >= 1 && score <= 5)) { addMsg('bot', 'ใส่ตัวเลข 1-5 นะครับ', { noTag: true }); return; }
    try {
      const res = await fetch(API + '/rate', {
        method: 'POST', headers: { 'Content-Type': 'application/json' },
        body: JSON.stringify({ conversationId: state.conversationId, sessionToken: state.sessionToken, score }),
      });
      const j = await res.json().catch(() => null);
      addMsg('bot', (j && j.message) || 'ขอบคุณสำหรับคะแนนครับ 🙏', { noTag: true });
    } catch (e) { addMsg('bot', 'บันทึกคะแนนไม่สำเร็จครับ', { noTag: true }); }
  }

  window.NxChat = { toggle, requestAgent, rate };
  if (document.readyState === 'loading') document.addEventListener('DOMContentLoaded', build);
  else build();
})();
