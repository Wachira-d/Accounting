/* รอบ 217 (คำตัดสินข้อ 175) — "กดส่งครั้งเดียว" ตัวเดียวของทุกฟอร์มสร้างรายการ (หน้าร้าน · จองโต๊ะ · ติดต่อ · จ่ายเงิน · หลังบ้าน)
 *
 * ที่มา: เจ้าของรายงาน "กดจองเบิ้ลสองที บันทึกการจองซ้ำได้" + "กดยืนยันจองครั้งแรกไม่มีอะไรเกิดขึ้น นิ่งไปพักนึง ไม่ขึ้นว่าโหลด"
 *   1. ปุ่มถูกล็อก + ขึ้น "กำลังบันทึก…" ทันทีที่กด (กดซ้ำ/Enter ซ้ำระหว่างรอ = ไม่ส่งซ้ำ) · ล้ม ⇒ ปลดล็อกให้ลองใหม่
 *   2. คีย์ Idempotency-Key สุ่มครั้งเดียวต่อ "ฟอร์มที่เปิดอยู่" (WeakMap ตาม element — เปิดฟอร์มใหม่ = คีย์ใหม่) ·
 *      ลองใหม่หลังเน็ตหลุดใช้คีย์เดิม ⇒ เซิร์ฟเวอร์คืนผลเดิม ไม่สร้างซ้ำ (Helpers/IdempotencyKeyPolicy · IdempotencyMiddleware)
 *   3. เซิร์ฟเวอร์ตอบ 409 + Idempotency-Status: in-flight ⇒ รอแล้วยิงคีย์เดิมซ้ำ (ได้ผลเดิม) · mismatch ⇒ หมุนคีย์ใหม่ + บอกผู้ใช้ตรวจรายการก่อน
 * หน้าเว็บไม่ตัดสินว่า "ซ้ำ" เอง — ด่านจริงอยู่ที่เซิร์ฟเวอร์ (คีย์ + DuplicateSubmitGuard) */
(function (global) {
  'use strict';
  const keys = new WeakMap();
  const HEADER = 'Idempotency-Key';
  const STATUS = 'Idempotency-Status';
  const MAX_INFLIGHT_RETRIES = 5;

  function newKey() {
    if (global.crypto && global.crypto.randomUUID) return global.crypto.randomUUID();
    // ห้าม Math.random — คีย์ต้องเดาไม่ได้ (ฝ่ายค้านรอบ 209)
    return Array.from(global.crypto.getRandomValues(new Uint8Array(16)), b => b.toString(16).padStart(2, '0')).join('');
  }

  const SubmitOnce = {
    /** คีย์ของฟอร์ม/โมดัลนี้ (สร้างครั้งแรกที่ขอ · ใช้ซ้ำตอนลองใหม่) — holder = element ของฟอร์ม/โมดัล หรือ object ใดก็ได้ */
    key(holder) {
      if (!holder) return newKey();
      let k = keys.get(holder);
      if (!k) { k = newKey(); keys.set(holder, k); }
      return k;
    },
    /** เริ่มรอบใหม่ (หลังสำเร็จ/ผู้ใช้ตั้งใจส่งรายการใหม่) */
    reset(holder) { if (holder) keys.delete(holder); },

    /** fetch พร้อม Idempotency-Key ของ holder · in-flight ⇒ รอ Retry-After แล้วยิงคีย์เดิม · mismatch ⇒ หมุนคีย์ (ครั้งหน้าเป็นคำขอใหม่) */
    async fetch(url, opts, holder) {
      const o = Object.assign({}, opts || {});
      o.headers = Object.assign({}, o.headers || {});
      o.headers[HEADER] = SubmitOnce.key(holder);
      for (let i = 0; ; i++) {
        const r = await fetch(url, o);
        const st = r.headers.get(STATUS);
        if (r.status === 409 && st === 'in-flight' && i < MAX_INFLIGHT_RETRIES) {
          const wait = Math.min(5, Math.max(1, parseInt(r.headers.get('Retry-After') || '2', 10) || 2));
          await new Promise(res => setTimeout(res, wait * 1000));
          continue;
        }
        if (r.status === 409 && st === 'mismatch') SubmitOnce.reset(holder);
        return r;
      }
    },

    /** ล็อกปุ่มระหว่างทำงาน — กดซ้ำระหว่างรอ = ไม่ทำอะไร · คืนผลของ fn · ล้ม/โยน ⇒ ปลดล็อก (ผู้เรียกจัดการข้อความ)
     *  opts.busyText = ข้อความระหว่างรอ · opts.keepLocked = true ⇒ สำเร็จแล้วไม่ปลดล็อก (เช่นหน้าเปลี่ยนไปหน้าสำเร็จ) */
    async run(btn, fn, opts) {
      const o = opts || {};
      if (btn && btn.dataset.soBusy === '1') return undefined;
      const label = btn ? btn.innerHTML : null;
      if (btn) {
        btn.dataset.soBusy = '1';
        btn.disabled = true;
        btn.setAttribute('aria-busy', 'true');
        btn.textContent = o.busyText || 'กำลังบันทึก…';
      }
      let ok = false;
      try {
        const out = await fn();
        ok = out !== false;
        return out;
      } finally {
        if (btn && (!ok || !o.keepLocked) && btn.isConnected) {
          btn.innerHTML = label;
          btn.disabled = false;
          btn.removeAttribute('aria-busy');
          delete btn.dataset.soBusy;
        } else if (btn) {
          delete btn.dataset.soBusy;
        }
      }
    },

    /** กัน Enter ส่งฟอร์มซ้ำระหว่างรอ — ฟอร์มที่ submit ผ่าน onsubmit: ใส่ไว้ต้น handler แล้ว return ถ้าได้ false */
    claim(form) {
      if (!form) return true;
      if (form.dataset.soBusy === '1') return false;
      form.dataset.soBusy = '1';
      return true;
    },
    release(form) { if (form) delete form.dataset.soBusy; },
  };

  global.SubmitOnce = SubmitOnce;
})(window);
