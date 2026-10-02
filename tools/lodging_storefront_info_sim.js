#!/usr/bin/env node
// ═══ ล็อกพฤติกรรม: หน้าเว็บที่พัก (storefront.html) — ข้อมูลห้องสด · 404 ≠ ขัดข้อง · ค่าค้นหาข้ามหน้า ═══
//
// รัน: node tools/lodging_storefront_info_sim.js
//
// ที่มา (รอบ 202 ทีม LW · LODGING_REVIEW W-02/W-04/W-07 · คำตัดสินข้อ 117):
//   W-04 `lodgingInfo()` เดิมแปลงทุกความล้มเหลว (5xx/เน็ตหลุด) เป็น null = "เว็บนี้ไม่มีที่พัก" ⇒ ระบบจองห้องล่มแล้วหน้าเว็บตกไป
//        การ์ดนัดหมาย ฿0 เงียบ ๆ และจำผลนั้นไว้ทั้งหน้า
//   W-02 บล็อก LodgingRooms ต้องแสดงราคาเริ่มต้นจากเซิร์ฟเวอร์ + "ราคาจริงตามวันที่เลือก" · ไม่ผูกที่พัก ⇒ "ยังไม่เปิดจองออนไลน์" ไม่มีราคา
//   W-07 ค้นหาจากหน้าแรกแล้วไปหน้าจอง ค่าวัน/จำนวนแขกหาย
//
// ใช้โค้ดจริงจากหน้า (ดึงเมธอดออกมารันกับ fetch จำลอง) · สองทิศ (F2 ข้อ 8):
//   (a) 404 ⇒ null และจำไว้ (ไม่ยิงซ้ำ) · 500/เน็ตหลุด ⇒ โยน Error และ "ไม่จำ" (กดใหม่ได้) · 200 ⇒ data
//   (b) การ์ดห้อง: หนีอักขระ HTML ทุกช่อง · มี "ราคาจริงตามวันที่เลือก" · ราคา = baseRate ที่เซิร์ฟเวอร์ส่ง · ไม่ผูก ⇒ ไม่มี "฿"
//   (c) ค่าค้นหา → URL → อ่านกลับได้ค่าเดิม · ค่าผิดรูปถูกทิ้ง (ไม่ใส่ state)
// negative test: ใส่ lodgingInfo รุ่นเดิม (catch ⇒ null) กลับ ⇒ (a) ต้องล้ม
'use strict';
const fs = require('fs');
const path = require('path');

const PAGE = process.env.SF_PAGE || path.join(__dirname, '..', 'Accounting', 'wwwroot', 'storefront.html');
const REAL_SRC = fs.readFileSync(PAGE, 'utf8');
const METHODS = ['esc', '_money', '_pageUrl', 'lodgingInfo', '_lodgingClosedHtml', '_lodgingRoomsHtml', '_lgStateFromQuery', '_pageUrlQuery'];

function extract(src, name) {
  const re = new RegExp('\\n      (?:async )?' + name + '\\(');
  const m = re.exec(src);
  if (!m) return null;
  const start = m.index + 1;
  // เมธอดบรรทัดเดียว (esc/_pageUrl) จบที่ " },\n" บรรทัดเดียวกัน
  const lineEnd = src.indexOf('\n', start);
  const line = src.slice(start, lineEnd);
  if (/\},\s*$/.test(line)) return line.replace(/,\s*$/, '');
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

async function run(src) {
  const fails = [];
  let S;
  try { S = makeStore(src); } catch (e) { return [e.message]; }
  S._ctx = { pathBased: true, basePath: '/site/b4' };
  S._lurl = (p) => '/lodging' + p;
  S.data = { site: { contactPhone: '02-111-2222', lineId: '@hotel<x>', contactEmail: '' } };

  // (a) 404 / 500 / network / 200
  let calls = 0;
  global.fetch = async () => { calls++; return { status: 404, ok: false, json: async () => ({ message: 'ไม่มี' }) }; };
  S._lodging = undefined;
  if ((await S.lodgingInfo()) !== null) fails.push('(a) 404 ต้องคืน null');
  await S.lodgingInfo();
  if (calls !== 1) fails.push(`(a) 404 ต้องจำผลไว้ (ยิง ${calls} ครั้ง)`);

  for (const mode of ['500', 'net']) {
    S._lodging = undefined; calls = 0;
    global.fetch = mode === '500'
      ? async () => { calls++; return { status: 500, ok: false, json: async () => ({ message: 'ฐานข้อมูลล่ม' }) }; }
      : async () => { calls++; throw new TypeError('Failed to fetch'); };
    let threw = null;
    try { await S.lodgingInfo(); } catch (e) { threw = e; }
    if (!threw) fails.push(`(a) ${mode} ต้องโยน Error ไม่ใช่คืน null (= "ไม่มีที่พัก" เงียบ ๆ)`);
    else if (mode === '500' && !String(threw.message).includes('ฐานข้อมูลล่ม')) fails.push('(a) 500 ต้องส่งข้อความของเซิร์ฟเวอร์ต่อ: ' + threw.message);
    if (S._lodging !== undefined) fails.push(`(a) ${mode} ห้ามจำผล (ต้องลองใหม่ได้)`);
  }
  S._lodging = undefined;
  global.fetch = async () => ({ status: 200, ok: true, json: async () => ({ data: { name: 'X', roomTypes: [] } }) });
  const ok = await S.lodgingInfo();
  if (!ok || ok.name !== 'X') fails.push('(a) 200 ต้องคืน data');

  // (b) การ์ดห้อง
  const html = S._lodgingRoomsHtml({ onlineBookingEnabled: true, roomTypes: [
    { id: '1', name: '<script>alert(1)</script>', images: ['x" onerror="y'], sizeSqm: 25, bedType: '"><b>', maxOccupancy: 3, baseRate: 1234.5, description: '<i>d</i>' }] });
  if (html.includes('<script>') || html.includes('"><b>') || html.includes('<i>d</i>') || html.includes('x" onerror'))
    fails.push('(b) การ์ดห้องต้องหนีอักขระ HTML ทุกช่อง');
  if (!html.includes('ราคาจริงตามวันที่เลือก')) fails.push('(b) ต้องบอกว่าราคาจริงตามวันที่เลือก');
  if (!html.includes('1,234.50')) fails.push('(b) ราคาเริ่มต้นต้องเป็น baseRate ที่เซิร์ฟเวอร์ส่ง');
  if (!html.includes('href="/site/b4/booking"')) fails.push('(b) ปุ่มต้องไปหน้าจอง');
  const closed = S._lodgingClosedHtml();
  if (!closed.includes('ยังไม่เปิดจองออนไลน์') || closed.includes('฿')) fails.push('(b) ไม่ผูกที่พัก ⇒ "ยังไม่เปิดจองออนไลน์" ไม่มีราคา');
  if (closed.includes('@hotel<x>') || !closed.includes('02-111-2222')) fails.push('(b) ช่องทางติดต่อต้องแสดงและหนีอักขระ');
  if (!S._lodgingRoomsHtml({ roomTypes: [] }).includes('ยังไม่มีประเภทห้อง')) fails.push('(b) ไม่มีห้องเปิดขายต้องบอก');

  // (c) ค่าค้นหาข้ามหน้า
  const url = S._pageUrlQuery('booking', { checkIn: '2026-11-01', checkOut: '2026-11-03', adults: 3, children: 1, infants: 1, rooms: 2 });
  if (!url.startsWith('/site/b4/booking?')) fails.push('(c) URL หน้าจองผิด: ' + url);
  global.location = { search: url.slice(url.indexOf('?')) };
  S._lgState = { checkIn: '', checkOut: '', adults: 2, children: 0, infants: 0, rooms: 1 };
  S._lgStateFromQuery();
  const st = S._lgState;
  if (st.checkIn !== '2026-11-01' || st.checkOut !== '2026-11-03' || st.adults !== 3 || st.children !== 1 || st.infants !== 1 || st.rooms !== 2)
    fails.push('(c) อ่านค่ากลับไม่ตรง: ' + JSON.stringify(st));
  global.location = { search: '?checkIn=<x>&checkOut=2026-11-03&adults=abc' };
  S._lgState = { checkIn: '', checkOut: '', adults: 2, children: 0, infants: 0, rooms: 1 };
  S._lgStateFromQuery();
  if (S._lgState.checkIn !== '' || S._lgState.adults !== 2) fails.push('(c) ค่าผิดรูปต้องถูกทิ้ง: ' + JSON.stringify(S._lgState));
  return fails;
}

(async () => {
  const real = await run(REAL_SRC);
  const OLD = `      async lodgingInfo() {
        if (this._lodging !== undefined) return this._lodging;
        try {
          const r = await fetch(this._lurl('/info'));
          this._lodging = r.ok ? ((await r.json()).data || null) : null;
        } catch (_) { this._lodging = null; }
        return this._lodging;
      },`;
  const mutated = REAL_SRC.replace(/\n      async lodgingInfo\(\) \{[\s\S]*?\n      \},/, '\n' + OLD);
  const neg = mutated === REAL_SRC ? ['แทนรุ่นเดิมไม่ได้'] : await run(mutated);
  if (real.length) {
    console.log('❌ lodging_storefront_info_sim: หน้าเว็บที่พักผิดพฤติกรรม');
    real.forEach(f => console.log('   ' + f));
    process.exit(1);
  }
  if (!neg.length) {
    console.log('❌ lodging_storefront_info_sim: negative test ไม่ล้ม — sim จับ lodgingInfo รุ่นเดิมไม่ได้');
    process.exit(1);
  }
  console.log(`✅ lodging_storefront_info_sim: 404≠ขัดข้อง · การ์ดห้องหนีอักขระ+ราคาจากเซิร์ฟเวอร์ · ค่าค้นหาข้ามหน้า · รุ่นเดิมถูกจับ (${neg.length} ข้อ)`);
})();
