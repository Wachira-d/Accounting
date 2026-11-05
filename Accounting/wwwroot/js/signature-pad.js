/*
 * ช่องวาดลายเซ็นของหน้าสาธารณะ — ตัวเดียวของทุกหน้า (รอบ 239 · quotation-accept.html · delivery-sign.html)
 *
 * - pointer events (เมาส์ · นิ้ว · ปากกา) + fallback touch/mouse สำหรับเบราว์เซอร์เก่า · ปุ่มล้างเรียก pad.clear()
 * - ใช้ได้ที่ความกว้างมือถือ (canvas ยืดตามกล่อง · touch-action:none กันหน้าเลื่อนระหว่างเซ็น)
 * - ส่งออก PNG พื้นโปร่ง ย่อให้กว้างไม่เกิน maxWidth (ค่าตั้งต้น 800px) — เซิร์ฟเวอร์ตรวจซ้ำทุกครั้งด้วย
 *   Helpers/SignatureImageValidator (ขนาด ≤ 200KB · PNG จริง · ขนาดภาพ · ไม่ว่าง) — ฝั่งนี้ไม่ใช่ด่าน แค่ช่วยไม่ให้ส่งของที่จะถูกปฏิเสธ
 *
 * ใช้: const pad = SignaturePad.create(canvasEl, { onChange: (hasInk) => … });
 *      pad.isEmpty() · pad.clear() · pad.toPngDataUrl()  (ว่าง ⇒ null)
 */
(function () {
  'use strict';

  function create(canvas, opts) {
    opts = opts || {};
    var ctx = canvas.getContext('2d');
    var last = null;
    var strokes = 0;
    var segments = 0;

    function style() {
      ctx.lineWidth = opts.lineWidth || 2.4;
      ctx.lineCap = 'round';
      ctx.lineJoin = 'round';
      ctx.strokeStyle = opts.color || '#1e293b';
      ctx.fillStyle = opts.color || '#1e293b';
    }

    function resize() {
      var ratio = Math.max(window.devicePixelRatio || 1, 1);
      var w = canvas.offsetWidth;
      var h = canvas.offsetHeight;
      if (!w || !h) return;
      canvas.width = Math.round(w * ratio);
      canvas.height = Math.round(h * ratio);
      ctx.setTransform(ratio, 0, 0, ratio, 0, 0);
      style();
    }

    function point(e) {
      var r = canvas.getBoundingClientRect();
      var p = e.touches && e.touches.length ? e.touches[0] : e;
      return { x: p.clientX - r.left, y: p.clientY - r.top };
    }

    function changed() {
      if (typeof opts.onChange === 'function') opts.onChange(!api.isEmpty());
    }

    function start(e) {
      if (e.button != null && e.button > 0) return;
      e.preventDefault();
      if (e.pointerId != null && canvas.setPointerCapture) {
        try { canvas.setPointerCapture(e.pointerId); } catch (_) { /* บางเบราว์เซอร์ไม่รองรับ capture — วาดต่อได้ */ }
      }
      last = point(e);
      strokes++;
    }

    function move(e) {
      if (!last) return;
      e.preventDefault();
      var p = point(e);
      ctx.beginPath();
      ctx.moveTo(last.x, last.y);
      ctx.lineTo(p.x, p.y);
      ctx.stroke();
      last = p;
      segments++;
      if (segments === 1 || segments === 4) changed();
    }

    function end() { last = null; }

    canvas.style.touchAction = 'none';
    if (window.PointerEvent) {
      canvas.addEventListener('pointerdown', start);
      canvas.addEventListener('pointermove', move);
      canvas.addEventListener('pointerup', end);
      canvas.addEventListener('pointercancel', end);
    } else {
      canvas.addEventListener('mousedown', start);
      canvas.addEventListener('mousemove', move);
      window.addEventListener('mouseup', end);
      canvas.addEventListener('touchstart', start, { passive: false });
      canvas.addEventListener('touchmove', move, { passive: false });
      canvas.addEventListener('touchend', end);
    }

    resize();
    // หมุนจอ/ปรับขนาดก่อนเริ่มเซ็น ⇒ คำนวณขนาดใหม่ (เซ็นแล้วไม่ resize — จะล้างลายเซ็นทิ้ง)
    window.addEventListener('resize', function () { if (api.isEmpty()) resize(); });

    var api = {
      /** ว่าง = ยังไม่มีเส้นพอเป็นลายเซ็น (แตะจุดเดียว/ลากสั้นมากไม่นับ) */
      isEmpty: function () { return strokes === 0 || segments < 4; },
      clear: function () {
        ctx.save();
        ctx.setTransform(1, 0, 0, 1, 0, 0);
        ctx.clearRect(0, 0, canvas.width, canvas.height);
        ctx.restore();
        strokes = 0;
        segments = 0;
        last = null;
        changed();
      },
      /** data:image/png;base64,… ย่อกว้างไม่เกิน maxWidth · ว่าง ⇒ null */
      toPngDataUrl: function () {
        if (api.isEmpty()) return null;
        var maxW = opts.maxWidth || 800;
        var scale = Math.min(1, maxW / canvas.width);
        var out = document.createElement('canvas');
        out.width = Math.max(1, Math.round(canvas.width * scale));
        out.height = Math.max(1, Math.round(canvas.height * scale));
        out.getContext('2d').drawImage(canvas, 0, 0, out.width, out.height);
        return out.toDataURL('image/png');
      },
    };
    return api;
  }

  window.SignaturePad = { create: create };
})();
