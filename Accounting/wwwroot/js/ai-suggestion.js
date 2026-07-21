// Reusable AI suggestion popover. Used by document/payment/CN/bank forms.
// Pattern:
//   AiSuggestion.show({
//     anchorEl: <DOM element to anchor the popover to>,
//     fetch:   async () => { return await API.... }   // returns suggestion
//     applyAnswer: (primary) => { ... }                // called when user accepts AI pick
//     answerToLabel: (id) => "Account 5402"            // optional, for showing primary nicely
//     allowDismiss: true                                // show "ใช้ของเดิม" button
//   });
//
// AI response shape (from server):
//   { primary, confidence, alternatives[], risks[], complianceFlags[],
//     reasoning, suggestedActions[], feedbackId, usedAi }
//
// Safety: AI down (usedAi=false) → popover still renders with a clear
// "AI ไม่พร้อม — แสดง local pick เท่านั้น" badge. Caller's onApply still
// works because primary fell back to localPick on the server side.
window.AiSuggestion = (function () {
  let _popover = null;
  let _outsideHandler = null;

  function close() {
    if (_popover) { _popover.remove(); _popover = null; }
    if (_outsideHandler) { document.removeEventListener('click', _outsideHandler, true); _outsideHandler = null; }
  }

  function esc(s) {
    if (s == null) return '';
    return String(s).replace(/[&<>"']/g, c => ({ '&': '&amp;', '<': '&lt;', '>': '&gt;', '"': '&quot;', "'": '&#39;' }[c]));
  }

  async function show(opts) {
    close();
    const { anchorEl, fetch, applyAnswer, answerToLabel, allowDismiss = true } = opts;
    if (!anchorEl) return;

    // Build placeholder + position immediately so user knows it's loading.
    const pop = document.createElement('div');
    pop.className = 'ai-suggestion-popover';
    pop.style.cssText = 'position:absolute;z-index:9999;background:#fff;border:1px solid #cbd5e1;border-radius:10px;box-shadow:0 8px 28px rgba(15,23,42,.18);padding:14px;min-width:320px;max-width:480px;font-family:inherit;font-size:13px';
    pop.innerHTML = '<div style="color:#64748b">🤖 กำลังถาม AI...</div>';
    document.body.appendChild(pop);
    _popover = pop;

    // Position below anchor.
    const rect = anchorEl.getBoundingClientRect();
    pop.style.top = (rect.bottom + window.scrollY + 4) + 'px';
    pop.style.left = (rect.left + window.scrollX) + 'px';

    // Close on outside click — defer attach so the click that opened
    // us doesn't immediately close.
    setTimeout(() => {
      _outsideHandler = (e) => { if (_popover && !_popover.contains(e.target) && e.target !== anchorEl) close(); };
      document.addEventListener('click', _outsideHandler, true);
    }, 10);

    let resp;
    try {
      const raw = await fetch();
      resp = raw && raw.data ? raw.data : raw;
    } catch (e) {
      pop.innerHTML = '<div style="color:#dc2626">ขอความเห็น AI ไม่สำเร็จ: ' + esc(e.message || e) + '</div>';
      return;
    }
    if (!resp) { pop.innerHTML = '<div style="color:#64748b">AI ไม่ตอบ</div>'; return; }

    const primary = resp.primary;
    const conf = resp.confidence != null ? Math.round(resp.confidence * 100) : null;
    const usedAi = !!resp.usedAi;
    const primaryLabel = answerToLabel ? (answerToLabel(primary) || primary) : primary;
    const altRows = (resp.alternatives || []).slice(0, 5).map(a => {
      const label = answerToLabel ? (answerToLabel(a) || a) : a;
      return `<button type="button" class="ai-alt-btn" data-val="${esc(a)}" style="display:block;width:100%;text-align:left;padding:6px 10px;margin:3px 0;background:#f1f5f9;border:1px solid #e2e8f0;border-radius:6px;cursor:pointer;font-size:12px;font-family:inherit">${esc(label)}</button>`;
    }).join('');
    const risks = (resp.risks || []).filter(Boolean);
    const compliance = (resp.complianceFlags || []).filter(Boolean);
    const actions = (resp.suggestedActions || []).filter(Boolean);

    pop.innerHTML = `
      <div style="display:flex;justify-content:space-between;align-items:start;margin-bottom:8px">
        <div style="display:flex;align-items:center;gap:6px">
          <span style="font-weight:700">🤖 AI แนะนำ</span>
          ${usedAi
            ? (conf != null ? `<span style="background:#dcfce7;color:#16a34a;padding:2px 8px;border-radius:9999px;font-size:11px;font-weight:600">${conf}%</span>` : '')
            : '<span style="background:#fef3c7;color:#92400e;padding:2px 8px;border-radius:9999px;font-size:11px">AI ไม่พร้อม — ใช้ local pick</span>'}
        </div>
        <button type="button" class="ai-pop-close" style="background:none;border:none;color:#94a3b8;cursor:pointer;font-size:18px;line-height:1;padding:0">×</button>
      </div>
      ${primary
        ? `<div style="background:#f0fdf4;border:1px solid #bbf7d0;border-radius:6px;padding:10px;margin-bottom:8px">
              <div style="font-weight:600;color:#16a34a;margin-bottom:6px">${esc(primaryLabel)}</div>
              ${resp.reasoning ? `<div style="font-size:11px;color:#475569;line-height:1.5">${esc(resp.reasoning)}</div>` : ''}
              <button type="button" class="ai-accept-btn" style="margin-top:8px;background:#16a34a;color:#fff;border:none;padding:6px 14px;border-radius:6px;cursor:pointer;font-size:12px;font-weight:600;font-family:inherit">✓ ใช้คำแนะนำนี้</button>
              ${allowDismiss ? '<button type="button" class="ai-dismiss-btn" style="margin-top:8px;margin-left:6px;background:transparent;color:#64748b;border:1px solid #cbd5e1;padding:6px 14px;border-radius:6px;cursor:pointer;font-size:12px;font-family:inherit">ใช้ของเดิม</button>' : ''}
            </div>`
        : '<div style="color:#94a3b8">AI ไม่สามารถแนะนำได้ — โปรดเลือกเอง</div>'}
      ${altRows ? `<div style="margin-bottom:6px"><div style="font-size:11px;color:#64748b;margin-bottom:4px">หรือ:</div>${altRows}</div>` : ''}
      ${actions.length ? `<div style="font-size:11px;color:#334155;margin-top:6px"><b>แนะนำเพิ่มเติม:</b> <ul style="margin:2px 0 0 18px;padding:0">${actions.map(a => `<li>${esc(a)}</li>`).join('')}</ul></div>` : ''}
      ${risks.length ? `<div style="font-size:11px;color:#dc2626;margin-top:6px"><b>ความเสี่ยง:</b> ${risks.map(esc).join('; ')}</div>` : ''}
      ${compliance.length ? `<div style="font-size:11px;color:#7c3aed;margin-top:4px"><b>กฎภาษีไทย:</b> ${compliance.map(esc).join('; ')}</div>` : ''}
    `;

    pop.querySelector('.ai-pop-close').onclick = close;
    pop.querySelectorAll('.ai-alt-btn').forEach(btn => {
      btn.onclick = async () => {
        const val = btn.getAttribute('data-val');
        await _recordAndApply(resp.feedbackId, val, false, applyAnswer, val);
        close();
      };
    });
    const acceptBtn = pop.querySelector('.ai-accept-btn');
    if (acceptBtn) acceptBtn.onclick = async () => {
      await _recordAndApply(resp.feedbackId, primary, true, applyAnswer, primary);
      close();
    };
    const dismissBtn = pop.querySelector('.ai-dismiss-btn');
    if (dismissBtn) dismissBtn.onclick = async () => {
      // Record a "did not accept" feedback so the local model learns
      // that AI's pick was rejected in this context. ChosenAnswer is
      // sent as "__USER_KEPT_EXISTING__" — the trainer can choose to
      // demote AI's pick on this signal.
      if (resp.feedbackId) {
        try { await API.post(window.AiSuggestion._companyScopedFeedbackUrl(), { feedbackId: resp.feedbackId, chosenAnswer: '__USER_KEPT_EXISTING__', acceptedAi: false }); } catch (e) {}
      }
      close();
    };
  }

  async function _recordAndApply(feedbackId, chosen, accepted, applyFn, applyValue) {
    if (feedbackId) {
      try {
        await API.post(window.AiSuggestion._companyScopedFeedbackUrl(), {
          feedbackId, chosenAnswer: chosen || '', acceptedAi: accepted,
        });
      } catch (e) { /* fire-and-forget */ }
    }
    if (applyFn) {
      try { applyFn(applyValue); } catch (e) { console.warn('AI applyAnswer threw', e); }
    }
  }

  return {
    show, close,
    // Caller can override if their layout exposes companyId differently.
    _companyScopedFeedbackUrl: () => {
      const cid = (window.Layout && Layout.getCompanyId) ? Layout.getCompanyId() : null;
      return cid ? `/api/companies/${cid}/ai-feedback/record` : '/api/companies/00000000-0000-0000-0000-000000000000/ai-feedback/record';
    },
  };
})();
