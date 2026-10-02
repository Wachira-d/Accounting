#!/usr/bin/env node
// ═══ ล็อกพฤติกรรม: หน้าจองสาธารณะ (storefront.html) แบ่งแขกลงห้องแล้ว "ผลรวม = จำนวนจริง" ═══
//
// รัน: node tools/lodging_guest_split_sim.js
// (เทียบรุ่นก่อนแก้: SF_PAGE=/path/to/old/storefront.html node tools/lodging_guest_split_sim.js — รุ่นก่อนไม่มี _splitGuests ⇒ ล้ม)
//
// ที่มา (รอบ 189 report-F F-06 · แก้รอบ 200 ทีม R): _quoteBody ใส่ `Math.ceil(adults / rooms)` ทุกห้อง ⇒ ผู้ใหญ่ 3 · 2 ห้อง = คิด 4 คน
// (ค่าแขกเกิน/PerPerson เกิน 1 คนเต็ม) · เด็กทุกคนลงห้องแรก ⇒ ติดเพดานเด็กต่อห้องทั้งที่รวมกันรับได้
//
// ใช้โค้ดจริงจากหน้า (ดึง _splitGuests + _quoteBody) · สองทิศ (F2 ข้อ 8):
//   (a) ผลรวมผู้ใหญ่/เด็กที่ส่ง = ที่แขกกรอก · ต่างกันไม่เกิน 1 ต่อห้อง
//   (b) 1 ห้อง = ส่งค่าเดิมตรง ๆ (ใบที่ถูกอยู่แล้วไม่ถูกแตะ) · ผู้ใหญ่น้อยกว่าห้อง = ห้องละ 1 (พฤติกรรมเดิม)
//   (c) รอบ 202 ทีม LW (คำตัดสินข้อ 123): แขกตั้งผู้เข้าพัก/คนเสริมรายห้องเอง ⇒ ค่าที่แขกตั้งชนะค่าแบ่งอัตโนมัติ (ส่งตรงตามที่ตั้ง
//       รวม extraBeds) · ห้องที่ไม่ได้แตะยังได้ค่าแบ่ง · เพิ่มห้องทีหลังไม่ล้างค่าที่แขกตั้งไว้
// negative test: ใส่สูตรเดิม (ceil + เด็กห้องแรก) กลับ ⇒ ชุดเดียวกันต้องล้ม
'use strict';
const fs = require('fs');
const path = require('path');

const PAGE = process.env.SF_PAGE || path.join(__dirname, '..', 'Accounting', 'wwwroot', 'storefront.html');
const REAL_SRC = fs.readFileSync(PAGE, 'utf8');
const METHODS = ['_splitGuests', '_roomKeys', '_syncRoomGuests', '_quoteBody'];

function extract(src, name) {
  const re = new RegExp('\\n      ' + name + '\\(');
  const m = re.exec(src);
  if (!m) return null;
  const start = m.index + 1;
  const end = src.indexOf('\n      },', start);
  if (end < 0) return null;
  return src.slice(start, end + '\n      }'.length);
}

function makeStore(src) {
  const parts = METHODS.map(n => {
    const t = extract(src, n);
    if (!t) throw new Error('หาเมธอด ' + n + ' ใน storefront.html ไม่เจอ — ถูกลบ/เปลี่ยนชื่อ?');
    return t;
  });
  // eslint-disable-next-line no-new-func
  return new Function('return ({\n' + parts.join(',\n') + '\n});')();
}

function body(store, sel, adults, children) {
  store._lgState = { sel, adults, children, infants: 0, roomGuests: {}, guestTouched: {}, extras: {}, checkIn: '2026-10-01', checkOut: '2026-10-03', ratePlanId: null };
  return store._quoteBody();
}

function run(src) {
  const fails = [];
  let store;
  try { store = makeStore(src); } catch (e) { return [e.message]; }
  const cases = [
    { sel: { A: 2 }, adults: 3, children: 0 },
    { sel: { A: 2 }, adults: 5, children: 2 },
    { sel: { A: 1, B: 2 }, adults: 7, children: 3 },
    { sel: { A: 3 }, adults: 3, children: 1 },
  ];
  for (const c of cases) {
    const b = body(store, c.sel, c.adults, c.children);
    const sa = b.rooms.reduce((s, r) => s + r.adults, 0);
    const sc = b.rooms.reduce((s, r) => s + r.children, 0);
    if (sa !== c.adults) fails.push(`(a) ผู้ใหญ่ ${c.adults} คน ${b.rooms.length} ห้อง ⇒ ส่ง ${sa} คน`);
    if (sc !== c.children) fails.push(`(a) เด็ก ${c.children} คน ${b.rooms.length} ห้อง ⇒ ส่ง ${sc} คน`);
    const ad = b.rooms.map(r => r.adults), ch = b.rooms.map(r => r.children);
    if (Math.max(...ad) - Math.min(...ad) > 1 || Math.max(...ch) - Math.min(...ch) > 1)
      fails.push(`(a) แบ่งไม่เท่ากัน ${JSON.stringify(ad)} / ${JSON.stringify(ch)}`);
  }
  const one = body(store, { A: 1 }, 2, 1);
  if (one.rooms.length !== 1 || one.rooms[0].adults !== 2 || one.rooms[0].children !== 1)
    fails.push('(b) 1 ห้องต้องส่งค่าเดิม: ' + JSON.stringify(one.rooms));
  const few = body(store, { A: 3 }, 1, 0);
  if (few.rooms.some(r => r.adults !== 1)) fails.push('(b) ผู้ใหญ่น้อยกว่าห้อง ต้องห้องละ 1: ' + JSON.stringify(few.rooms));
  // (c) แขกตั้งเองรายห้อง
  body(store, { A: 2 }, 4, 0);
  const st = store._lgState;
  st.roomGuests['A:1'] = { adults: 3, children: 1, extraBeds: 1 }; st.guestTouched['A:1'] = true;
  const touched = store._quoteBody();
  const r1 = touched.rooms[1];
  if (!r1 || r1.adults !== 3 || r1.children !== 1 || r1.extraBeds !== 1)
    fails.push('(c) ค่าที่แขกตั้งรายห้องต้องถูกส่งตรง (รวมคนเสริม): ' + JSON.stringify(touched.rooms));
  if (touched.rooms[0].adults !== 2 || touched.rooms[0].extraBeds !== 0)
    fails.push('(c) ห้องที่ไม่ได้แตะต้องได้ค่าแบ่งเดิม: ' + JSON.stringify(touched.rooms));
  st.sel.B = 1; store._syncRoomGuests();
  const more = store._quoteBody();
  if (more.rooms.length !== 3 || more.rooms[1].adults !== 3 || more.rooms[1].extraBeds !== 1)
    fails.push('(c) เพิ่มห้องแล้วค่าที่แขกตั้งไว้ต้องคงอยู่: ' + JSON.stringify(more.rooms));
  return fails;
}

const real = run(REAL_SRC);
// negative test: สูตรเดิมก่อนแก้ต้องล้ม
const OLD_QUOTE = `      _quoteBody() {
        const st = this._lgState;
        const rooms = [];
        for (const [rtId, n] of Object.entries(st.sel)) for (let i = 0; i < n; i++) rooms.push({ roomTypeId: rtId, adults: Math.max(1, Math.ceil(st.adults / Math.max(1, Object.values(st.sel).reduce((a,b)=>a+b,0)))), children: i === 0 ? st.children : 0, extraBeds: 0 });
        return { rooms };
      },`;
const mutated = REAL_SRC.replace(/\n      _quoteBody\(\) \{[\s\S]*?\n      \},/, '\n' + OLD_QUOTE);
const neg = mutated === REAL_SRC ? ['แทนสูตรเดิมไม่ได้'] : run(mutated);

if (real.length) {
  console.log('❌ lodging_guest_split_sim: หน้าจองแบ่งแขกผิด');
  real.forEach(f => console.log('   ' + f));
  process.exit(1);
}
if (!neg.length) {
  console.log('❌ lodging_guest_split_sim: negative test ไม่ล้ม — sim จับสูตรเดิมไม่ได้');
  process.exit(1);
}
console.log(`✅ lodging_guest_split_sim: ผลรวมแขกตรงทุกเคส · 1 ห้องไม่ถูกแตะ · สูตรเดิมถูกจับ (${neg.length} ข้อ)`);
