#!/usr/bin/env node
// ═══ ล็อกพฤติกรรม: "ขายด่วน" (quick-sale.html) อนุมัติแล้วต้องบอกผลจริง — ห้ามขึ้น "บันทึกสำเร็จ" ทั้งที่ใบยังเป็นร่าง ═══
//
// รัน: node tools/quick_sale_approve_sim.js
// (เทียบรุ่นก่อนแก้: QS_PAGE=/path/to/old/quick-sale.html node tools/quick_sale_approve_sim.js — รุ่นก่อนไม่มี _approve ⇒ ล้มตั้งแต่ขั้นดึงเมธอด)
//
// ที่มา (รอบ 198 ทีม S4 · ค้างจากทีม W รอบ 199): submit() เรียก approveDocument แล้ว `catch (_) {}` กลืนทุก error ⇒ ใบที่มีคำเตือน
// (เซิร์ฟเวอร์ตอบ 422 รอคนรับทราบ) / ไม่มีสิทธิ์อนุมัติ (403) / ด่านบังคับ ค้างเป็นร่าง แต่หน้าขึ้น "✅ บันทึกสำเร็จ"
//
// ใช้ **โค้ดจริง** จาก quick-sale.html (ดึง _approve · _approvalOutcome · _draftNotice · _esc) ล็อกสองทิศ (F2 ข้อ 8):
//   (a) ไม่มีคำเตือน ⇒ อนุมัติในครั้งเดียว · ส่ง acknowledgeWarnings:false
//   (b) มีคำเตือน ⇒ ไม่อนุมัติ · ไม่ส่ง true (หน้านี้ไม่มีหน้าต่างให้คนรับทราบ) · แบนเนอร์บอก "ร่าง" + รายการคำเตือน (หนี HTML) + ลิงก์ไปหน้าเอกสาร
//   (c) error อื่น ⇒ ข้อความของเซิร์ฟเวอร์ในแบนเนอร์ (ไม่กลืน)
//   (d) submit() ต้องไม่กลืน error ของการอนุมัติอีก (`catch (_)` รอบ approveDocument)
//   (e) review198-S4 S4-2: api.js คืน { success:false } โดย**ไม่ throw** (429 ถูกจำกัดอัตรา · 403 ขณะรายการบริษัทยังโหลดไม่เสร็จ) ⇒ ต้องไม่นับเป็นอนุมัติ
//       และแบนเนอร์บอกข้อความนั้น (รูปคำตอบเดียวกับ api.js — ไม่ใช่ error object)
// และ negative test ในตัว: ใส่บั๊กกลับทีละแบบแล้วชุดเดียวกันต้องล้ม
'use strict';
const fs = require('fs');
const path = require('path');

const PAGE = process.env.QS_PAGE || path.join(__dirname, '..', 'Accounting', 'wwwroot', 'pages', 'quick-sale.html');
const REAL_SRC = fs.readFileSync(PAGE, 'utf8');
const METHODS = ['_approve', '_approvalOutcome', '_draftNotice', '_esc'];

function extract(src, name) {
  const re = new RegExp('\\n      (async )?' + name + '\\(');
  const m = re.exec(src);
  if (!m) return null;
  const start = m.index + 1;
  // เมธอดบรรทัดเดียว (เช่น _esc) จบที่ท้ายบรรทัด · หลายบรรทัดจบที่ "      }," ตัวแรก
  const lineEnd = src.indexOf('\n', start);
  const firstLine = src.slice(start, lineEnd);
  if (/\}\s*,?\s*$/.test(firstLine) && (firstLine.match(/\{/g) || []).length === (firstLine.match(/\}/g) || []).length)
    return firstLine.replace(/,\s*$/, '');
  const end = src.indexOf('\n      },', start);
  if (end < 0) return null;
  return src.slice(start, end + '\n      }'.length);
}

function makeQs(src, api) {
  const parts = METHODS.map(n => {
    const t = extract(src, n);
    if (!t) throw new Error('หาเมธอด ' + n + ' ใน quick-sale.html ไม่เจอ — ถูกลบ/เปลี่ยนชื่อ?');
    return t;
  });
  // eslint-disable-next-line no-new-func
  return new Function('API', 'return ({\n' + parts.join(',\n') + '\n});')({ c: () => api });
}

const WARN = 'ราคาต่ำกว่าต้นทุน <script>x</script>';
function makeApi(mode, log) {
  return {
    async approveDocument(id, body) {
      const ack = !!(body && body.acknowledgeWarnings);
      log.push('approve:' + (ack ? 'ack' : 'noack'));
      if (mode === 'forbidden') { const e = new Error('ผู้ใช้นี้ไม่มีสิทธิ์อนุมัติใบกำกับภาษี'); e.status = 403; throw e; }
      // รูปเดียวกับ api.js: 429 และ 403-ช่วงโหลดบริษัท คืนค่าแทนการ throw
      if (mode === 'ratelimited') return { success: false, data: null, message: 'กรุณารอสักครู่' };
      if (mode === 'notready') return { success: false, data: null, message: 'company not ready' };
      if (mode === 'warnings' && !ack) {
        const e = new Error('เอกสารมีจุดที่ต้องตรวจก่อนยืนยันการอนุมัติ');
        e.status = 422; e.body = { data: { warnings: [WARN], aiHints: null } };
        throw e;
      }
      return { data: { id, status: 'Approved' } };
    },
  };
}

async function scenarios(src) {
  const fails = [];
  const link = '/pages/documents.html?id=d-1';
  {
    const log = [];
    const qs = makeQs(src, makeApi('clean', log));
    const r = await qs._approve('c-1', 'd-1');
    if (!r.approved) fails.push('(a) ไม่มีคำเตือนแต่ไม่อนุมัติ');
    if (log.join(',') !== 'approve:noack') fails.push('(a) ต้องส่ง acknowledgeWarnings:false ครั้งเดียว — ได้ ' + log.join(','));
  }
  {
    const log = [];
    const qs = makeQs(src, makeApi('warnings', log));
    const r = await qs._approve('c-1', 'd-1');
    if (r.approved) fails.push('(b) มีคำเตือนแต่ถือว่าอนุมัติแล้ว');
    if (log.includes('approve:ack')) fails.push('(b) รับทราบคำเตือนแทนคน (ส่ง acknowledgeWarnings:true)');
    const html = r.approved ? '' : qs._draftNotice(r, link);
    if (!/ร่าง/.test(html)) fails.push('(b) แบนเนอร์ไม่บอกว่าเป็นร่าง');
    if (!html.includes(link)) fails.push('(b) ไม่มีลิงก์ไปหน้าเอกสาร');
    if (html.includes('<script>')) fails.push('(b) ข้อความคำเตือนไม่ถูกหนี HTML');
    if (!html.includes('ราคาต่ำกว่าต้นทุน')) fails.push('(b) ไม่แสดงรายการคำเตือน');
  }
  {
    const log = [];
    const qs = makeQs(src, makeApi('forbidden', log));
    const r = await qs._approve('c-1', 'd-1');
    if (r.approved) fails.push('(c) 403 แต่ถือว่าอนุมัติแล้ว');
    const html = r.approved ? '' : qs._draftNotice(r, link);
    if (!html.includes('ไม่มีสิทธิ์อนุมัติ')) fails.push('(c) ไม่แสดงข้อความของเซิร์ฟเวอร์');
  }
  for (const mode of ['ratelimited', 'notready']) {
    const log = [];
    const qs = makeQs(src, makeApi(mode, log));
    const r = await qs._approve('c-1', 'd-1');
    if (r.approved) fails.push('(e) ' + mode + ': api.js คืน success:false แต่ถือว่าอนุมัติแล้ว (ขึ้น ✅ ทั้งที่ใบยังเป็นร่าง)');
    const html = r.approved ? '' : qs._draftNotice(r, link);
    if (!r.approved && !/ร่าง/.test(html)) fails.push('(e) ' + mode + ': แบนเนอร์ไม่บอกว่าเป็นร่าง');
    if (mode === 'ratelimited' && !r.approved && !html.includes('กรุณารอสักครู่')) fails.push('(e) ratelimited: ไม่แสดงข้อความจาก api.js');
  }
  {
    const m = /\n      async submit\(\)[\s\S]*?\n      \},/.exec(src);
    if (!m) fails.push('(d) หา submit() ไม่เจอ');
    else {
      if (/approveDocument\([^)]*\)\s*;?\s*\}\s*catch\s*\(\s*_?\s*\)/.test(m[0])) fails.push('(d) submit() ยังกลืน error ของ approveDocument');
      if (!/this\._approve\(/.test(m[0])) fails.push('(d) submit() ไม่เรียก _approve');
      if (!/_draftNotice\(/.test(m[0])) fails.push('(d) submit() ไม่แสดงแบนเนอร์ร่างเมื่ออนุมัติไม่สำเร็จ');
    }
  }
  return fails;
}

(async () => {
  const real = await scenarios(REAL_SRC);
  if (real.length) {
    console.error('❌ quick_sale_approve_sim — โค้ดจริงไม่ผ่าน:\n  ' + real.join('\n  '));
    process.exit(1);
  }
  // negative test: ใส่บั๊กกลับทีละแบบ ⇒ ต้องล้ม
  const mutants = [
    ['ส่ง acknowledgeWarnings:true ตั้งแต่แรก', s => s.replace('{ acknowledgeWarnings: false }', '{ acknowledgeWarnings: true }')],
    ['กลืน error แล้วนับเป็นอนุมัติ', s => s.replace('return this._approvalOutcome(e);', 'return { approved: true };')],
    ['ไม่หนี HTML ของคำเตือน', s => s.replace('`<li>${this._esc(w)}</li>`', '`<li>${w}</li>`')],
    ['ไม่แสดงข้อความของเซิร์ฟเวอร์', s => s.replace("(e && e.message) || 'อนุมัติไม่สำเร็จ'", "'อนุมัติไม่สำเร็จ'")],
    ['ไม่ตรวจ success:false ของ api.js (S4-2)', s => s.replace('if (res && res.success === false)', 'if (false)')],
    ['submit กลับไปกลืน error', s => s.replace('const approval = await this._approve(co.id, docId);',
      'try { await API.c(co.id).approveDocument(docId); } catch (_) { } const approval = { approved: true };')],
  ];
  const survived = [];
  for (const [name, mutate] of mutants) {
    const src = mutate(REAL_SRC);
    if (src === REAL_SRC) { survived.push(name + ' (แทนที่ไม่ได้ — ปรับ mutant ให้ตรงโค้ดปัจจุบัน)'); continue; }
    let fails;
    try { fails = await scenarios(src); } catch (e) { fails = ['throw: ' + e.message]; }
    if (!fails.length) survived.push(name);
  }
  if (survived.length) {
    console.error('❌ quick_sale_approve_sim — negative test ไม่ล้ม (ด่านไม่มีฟัน):\n  ' + survived.join('\n  '));
    process.exit(1);
  }
  console.log(`✅ quick_sale_approve_sim — 5 ชุดเหตุการณ์ผ่าน · negative test ${mutants.length} แบบล้มครบ`);
})();
