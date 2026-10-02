#!/usr/bin/env node
// ═══ ล็อกพฤติกรรม: หน้าจองสาธารณะ (storefront.html) แบ่งแขกลงห้อง + ตัวเลือกผู้เข้าพักรายห้อง ═══
//
// รัน: node tools/lodging_guest_split_sim.js
// (เทียบรุ่นก่อนแก้: SF_PAGE=/path/to/old/storefront.html node tools/lodging_guest_split_sim.js)
//
// ที่มา (รอบ 189 report-F F-06 · แก้รอบ 200 ทีม R): _quoteBody ใส่ `Math.ceil(adults / rooms)` ทุกห้อง ⇒ ผู้ใหญ่ 3 · 2 ห้อง = คิด 4 คน
// · เด็กทุกคนลงห้องแรก ⇒ ติดเพดานเด็กต่อห้องทั้งที่รวมกันรับได้
// รอบ 202 ทีม LW (คำตัดสินข้อ 123/124 · ฝ่ายค้าน P1-1/P1-2): แบบจำลองเดียวกับเซิร์ฟเวอร์ `Helpers/LodgingOccupancy` —
// adults = ผู้ใหญ่บนเตียงปกติ (≤ maxAdults) · extraBeds = คนเสริม **คนละคน** (ไม่ซ้อนนับ) · ทารกระดับการจอง (อยู่ใน quote ด้วย)
//
// ใช้โค้ดจริงจากหน้า (ดึง _splitGuests · _roomKeys · _syncRoomGuests · _quoteBody · _roomGuestPanel) · สองทิศ (F2 ข้อ 8):
//   (a) ผลรวมคน (ผู้ใหญ่ + คนเสริม) / เด็ก ที่ส่ง = ที่แขกกรอก · ต่างกันไม่เกิน 1 ต่อห้อง (ห้องที่ไม่มีเพดานใน fixture)
//   (b) 1 ห้อง = ส่งค่าเดิมตรง ๆ (ไม่เกินเพดาน = ไม่ย้ายเป็นคนเสริม) · ผู้ใหญ่น้อยกว่าห้อง = ห้องละ 1
//   (c) แขกตั้งเองรายห้อง ⇒ ค่าที่แขกตั้งชนะค่าแบ่งอัตโนมัติ · เพิ่มห้องทีหลังไม่ล้างค่าที่ตั้งไว้
//   (d) ผู้ใหญ่เกิน maxAdults ⇒ ย้ายเป็นคนเสริม ≤ maxExtraBeds (เฉพาะห้องที่เปิดเตียงเสริม) · ที่ยังเกินคงไว้ (เซิร์ฟเวอร์ตอบข้อความ)
//   (e) แผงผู้เข้าพัก: ผู้ใหญ่ 1..maxAdults (ไม่บวกคนเสริม) · ช่องคนเสริมแยก 0..maxExtraBeds เฉพาะห้องที่เปิด · ป้ายราคาจากเซิร์ฟเวอร์ (หนีอักขระ)
//   (f) quote ส่ง infants
// negative test: สูตรเดิม (ceil + เด็กห้องแรก) · sync รุ่นแรกของรอบ 202 (ไม่ย้ายคนเสริม) · แผงรุ่นแรก (ผู้ใหญ่ = max + คนเสริม) ⇒ ต้องล้มทุกตัว
'use strict';
const fs = require('fs');
const path = require('path');

const PAGE = process.env.SF_PAGE || path.join(__dirname, '..', 'Accounting', 'wwwroot', 'storefront.html');
const REAL_SRC = fs.readFileSync(PAGE, 'utf8');
const METHODS = ['esc', 'jsArg', '_splitGuests', '_roomKeys', '_syncRoomGuests', '_quoteBody', '_roomGuestPanel'];

function extract(src, name) {
  const re = new RegExp('\\n      (?:async )?' + name + '\\(');
  const m = re.exec(src);
  if (!m) return null;
  const start = m.index + 1;
  const line = src.slice(start, src.indexOf('\n', start));
  if (/\},\s*$/.test(line)) return line.replace(/,\s*$/, '');   // เมธอดบรรทัดเดียว (esc/jsArg)
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
  const s = new Function('return ({\n' + parts.join(',\n') + '\n});')();
  // fixture ตามค่าที่ /lodging/info ส่ง (projection สาธารณะ) — A: ผู้ใหญ่ 2 + คนเสริม 1 · B: ผู้ใหญ่ 3 ไม่รับคนเสริม · C: ไม่มีเพดาน (ค่าสูง)
  s._lodging = { infantMaxAge: 2, roomTypes: [
    { id: 'A', name: 'Deluxe', maxAdults: 2, allowExtraBed: true, maxExtraBeds: 1, extraBedSummary: 'เตียงเสริมสูงสุด 1 คน · ฿<b>500</b>/คน/คืน' },
    { id: 'B', name: 'Family', maxAdults: 3, allowExtraBed: false, maxExtraBeds: 0 },
    { id: 'C', name: 'Villa', maxAdults: 20, allowExtraBed: false, maxExtraBeds: 0 },
  ] };
  return s;
}

function body(store, sel, adults, children, infants = 0) {
  store._lgState = { sel, adults, children, infants, roomGuests: {}, guestTouched: {}, extras: {}, checkIn: '2026-10-01', checkOut: '2026-10-03', ratePlanId: null };
  return store._quoteBody();
}

function selectRange(html, field) {
  const m = new RegExp("lodgingSetGuest\\('[^']*', '" + field + "', this\\.value\\)\">([\\s\\S]*?)</select>").exec(html);
  if (!m) return null;
  const vals = [...m[1].matchAll(/value="(\d+)"/g)].map(x => +x[1]);
  return [Math.min(...vals), Math.max(...vals)];
}

function run(src) {
  const fails = [];
  let store;
  try { store = makeStore(src); } catch (e) { return [e.message]; }
  // (a) ห้องไม่มีเพดาน (C) — ผลรวมตรง · แบ่งเท่า
  const cases = [
    { sel: { C: 2 }, adults: 3, children: 0 },
    { sel: { C: 2 }, adults: 5, children: 2 },
    { sel: { C: 3 }, adults: 7, children: 3 },
    { sel: { C: 3 }, adults: 3, children: 1 },
  ];
  for (const c of cases) {
    const b = body(store, c.sel, c.adults, c.children);
    const sa = b.rooms.reduce((s, r) => s + r.adults + r.extraBeds, 0);
    const sc = b.rooms.reduce((s, r) => s + r.children, 0);
    if (sa !== c.adults) fails.push(`(a) ผู้ใหญ่ ${c.adults} คน ${b.rooms.length} ห้อง ⇒ ส่ง ${sa} คน`);
    if (sc !== c.children) fails.push(`(a) เด็ก ${c.children} คน ${b.rooms.length} ห้อง ⇒ ส่ง ${sc} คน`);
    const ad = b.rooms.map(r => r.adults), ch = b.rooms.map(r => r.children);
    if (Math.max(...ad) - Math.min(...ad) > 1 || Math.max(...ch) - Math.min(...ch) > 1)
      fails.push(`(a) แบ่งไม่เท่ากัน ${JSON.stringify(ad)} / ${JSON.stringify(ch)}`);
  }
  // (b) ไม่เกินเพดาน = ไม่แตะ
  const one = body(store, { A: 1 }, 2, 1);
  if (one.rooms.length !== 1 || one.rooms[0].adults !== 2 || one.rooms[0].children !== 1 || one.rooms[0].extraBeds !== 0)
    fails.push('(b) 1 ห้องไม่เกินเพดานต้องส่งค่าเดิม: ' + JSON.stringify(one.rooms));
  const few = body(store, { C: 3 }, 1, 0);
  if (few.rooms.some(r => r.adults !== 1)) fails.push('(b) ผู้ใหญ่น้อยกว่าห้อง ต้องห้องละ 1: ' + JSON.stringify(few.rooms));
  // (c) แขกตั้งเองรายห้อง
  body(store, { C: 2 }, 4, 0);
  const st = store._lgState;
  st.roomGuests['C:1'] = { adults: 3, children: 1, extraBeds: 1 }; st.guestTouched['C:1'] = true;
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
  // (d) เกินเพดาน ⇒ คนเสริม (แยกจากผู้ใหญ่)
  const over = body(store, { A: 1 }, 3, 0);
  if (over.rooms[0].adults !== 2 || over.rooms[0].extraBeds !== 1)
    fails.push('(d) ผู้ใหญ่ 3 ห้อง max 2 + คนเสริม 1 ⇒ ต้องเป็นผู้ใหญ่ 2 + คนเสริม 1: ' + JSON.stringify(over.rooms));
  const over2 = body(store, { A: 1 }, 4, 0);
  if (over2.rooms[0].adults !== 3 || over2.rooms[0].extraBeds !== 1)
    fails.push('(d) เกินทั้งเพดานและคนเสริม ⇒ คนเสริมเต็ม 1 ที่เหลือคงในผู้ใหญ่ (เซิร์ฟเวอร์ตอบข้อความ): ' + JSON.stringify(over2.rooms));
  const noXb = body(store, { B: 1 }, 4, 0);
  if (noXb.rooms[0].adults !== 4 || noXb.rooms[0].extraBeds !== 0)
    fails.push('(d) ห้องที่ไม่รับคนเสริมต้องไม่ถูกใส่คนเสริม: ' + JSON.stringify(noXb.rooms));
  // (e) แผงผู้เข้าพัก
  body(store, { A: 1, B: 1 }, 3, 0); store._syncRoomGuests();
  const panel = store._roomGuestPanel(store._lodging);
  const rowA = panel.split('ห้องที่ 2')[0], rowB = panel.split('ห้องที่ 2')[1] || '';
  const aA = selectRange(rowA, 'adults'), xA = selectRange(rowA, 'extraBeds'), aB = selectRange(rowB, 'adults');
  if (!aA || aA[1] !== 2) fails.push('(e) ห้อง A ผู้ใหญ่ต้อง 1..2 (ไม่บวกคนเสริม) ได้ ' + JSON.stringify(aA));
  if (!xA || xA[0] !== 0 || xA[1] !== 1) fails.push('(e) ห้อง A ต้องมีช่องคนเสริม 0..1 ได้ ' + JSON.stringify(xA));
  if (selectRange(rowB, 'extraBeds')) fails.push('(e) ห้อง B (ไม่รับคนเสริม) ต้องไม่มีช่องคนเสริม');
  if (!aB || aB[1] !== 3) fails.push('(e) ห้อง B ผู้ใหญ่ต้อง 1..3 ได้ ' + JSON.stringify(aB));
  if (panel.includes('<b>500</b>') || !panel.includes('&lt;b&gt;500')) fails.push('(e) ป้ายราคาคนเสริมจากเซิร์ฟเวอร์ต้องหนีอักขระ');
  // (f) quote ส่ง infants
  if (body(store, { A: 1 }, 2, 0, 2).infants !== 2) fails.push('(f) quote ต้องส่ง infants (ยอดผู้เข้าพักรวมของเซิร์ฟเวอร์รวมทารก)');
  return fails;
}

function mutate(src, name, replacement) {
  const re = new RegExp('\\n      ' + name + '\\([^)]*\\) \\{[\\s\\S]*?\\n      \\},');
  const out = src.replace(re, '\n' + replacement);
  return out === src ? null : out;
}

const real = run(REAL_SRC);
const NEGATIVES = {
  'สูตรเดิมก่อนรอบ 200': mutate(REAL_SRC, '_quoteBody', `      _quoteBody() {
        const st = this._lgState;
        const rooms = [];
        for (const [rtId, n] of Object.entries(st.sel)) for (let i = 0; i < n; i++) rooms.push({ roomTypeId: rtId, adults: Math.max(1, Math.ceil(st.adults / Math.max(1, Object.values(st.sel).reduce((a,b)=>a+b,0)))), children: i === 0 ? st.children : 0, extraBeds: 0 });
        return { rooms };
      },`),
  'sync ไม่ย้ายคนเสริม': mutate(REAL_SRC, '_syncRoomGuests', `      _syncRoomGuests() {
        const st = this._lgState; const keys = this._roomKeys();
        const adults = this._splitGuests(st.adults, keys.length, 1), kids = this._splitGuests(st.children, keys.length, 0);
        const next = {};
        keys.forEach((k, i) => {
          next[k.key] = st.guestTouched[k.key] && st.roomGuests[k.key] ? st.roomGuests[k.key] : { adults: adults[i], children: kids[i], extraBeds: 0 };
        });
        st.roomGuests = next;
      },`),
  'แผงผู้ใหญ่ = max + คนเสริม': REAL_SRC.includes('const maxA = Math.max(1, (rt.maxAdults | 0) || 1);   //')
    ? REAL_SRC.replace('const maxA = Math.max(1, (rt.maxAdults | 0) || 1);   //', 'const maxA = Math.max(1, (rt.maxAdults | 0) || 1) + maxXb;   //') : null,
};
const negFails = [];
for (const [name, src] of Object.entries(NEGATIVES)) {
  if (!src) { negFails.push(`แทน "${name}" ไม่ได้ (โค้ดขยับ — ปรับ negative test)`); continue; }
  if (!run(src).length) negFails.push(`negative "${name}" ไม่ล้ม — sim จับไม่ได้`);
}

if (real.length) {
  console.log('❌ lodging_guest_split_sim: หน้าจองแบ่งแขก/แผงผู้เข้าพักผิด');
  real.forEach(f => console.log('   ' + f));
  process.exit(1);
}
if (negFails.length) {
  console.log('❌ lodging_guest_split_sim: negative test');
  negFails.forEach(f => console.log('   ' + f));
  process.exit(1);
}
console.log(`✅ lodging_guest_split_sim: ผลรวมแขกตรง · คนเสริมแยกจากผู้ใหญ่ (LodgingOccupancy) · แผงผู้เข้าพักตามเพดาน · quote ส่งทารก · negative ${Object.keys(NEGATIVES).length} แบบถูกจับ`);
