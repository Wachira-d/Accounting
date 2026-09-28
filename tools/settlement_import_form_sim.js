// ล็อกตัวอ่านฟอร์ม "นำเข้ารอบโอน" ของ settlements.html ด้วย **โค้ดจริงของหน้า** (รอบ 198 เฟส 1 ทีม D)
//
// สิ่งที่ล็อก (ทั้งสองทิศ · F2 ข้อ 8):
//   readHeader — ช่องตัวเลขว่าง = ตัดคีย์ทิ้ง (ไม่ส่ง null เข้า decimal ⇒ body ถูกโยนทิ้งทั้งก้อน) · 0 ที่พิมพ์เองต้องไม่หาย ·
//                ยอดโอน (netPayout) ที่กรอกต้องไปถึง payload
//   readMap    — แบบกว้าง: คอลัมน์ที่ติ๊กเป็นยอดเงินอยู่ใน amountColumns · คอลัมน์ที่ใช้เป็นช่องอื่นไม่ถูกนับว่า "ไม่ใช้" ·
//                คอลัมน์ที่ไม่ได้ใช้เลย = ignore (ผู้ใช้เห็นแล้วเลือกไม่ใช้) · แบบยาว: ไม่มี amountColumns ค้าง
//   matchBody  — (review198-D D-01) ผู้สมัครที่เซิร์ฟเวอร์ติดธง selectable=false ไม่ถูกส่ง · รายการรับชำระส่งเป็น paymentIntentId
//                (เดิมส่งเป็น documentId ⇒ 404 "ไม่พบเอกสารขาย" ทุกครั้ง) · เอกสารส่งเป็น documentId
//   negative test ในตัว — ใส่บั๊กกลับ (ส่ง null · ลืม ignore · intent เป็น documentId · ไม่ดู selectable) ในหน่วยความจำ แล้ว sim ต้องจับได้
'use strict';
const fs = require('fs');
const path = require('path');

const ROOT = path.join(__dirname, '..');
const pageSrc = fs.readFileSync(path.join(ROOT, 'Accounting', 'wwwroot', 'pages', 'settlements.html'), 'utf8');
const layoutSrc = fs.readFileSync(path.join(ROOT, 'Accounting', 'wwwroot', 'js', 'layout.js'), 'utf8');

function extractMethod(src, name) {
  const m = src.match(new RegExp('\\n      ' + name + '\\(\\) \\{[\\s\\S]*?\\n      \\},'));
  return m ? m[0] : null;
}
const numOrNullSrc = (layoutSrc.match(/\n  numOrNull\(elId\) \{[\s\S]*?\n  \},/) || [])[0];

function buildPage(readHeaderSrc, readMapSrc) {
  // eslint-disable-next-line no-new-func
  const numOrNull = new Function('document', 'return ({' + numOrNullSrc + '}).numOrNull;');
  // eslint-disable-next-line no-new-func
  return (document) => {
    const Layout = { numOrNull: numOrNull(document) };
    // eslint-disable-next-line no-new-func
    const make = new Function('document', 'Layout', 'return ({' + readHeaderSrc + readMapSrc + '});');
    return make(document, Layout);
  };
}

/** DOM ปลอม: ช่องตาม id · select ของช่องจับคู่คอลัมน์ ([data-map-field]) · checkbox/select ของคอลัมน์ยอด ([data-amt-*="i"]) */
function fakeDom(byId, mapFields, amt) {
  return {
    getElementById: (id) => byId[id] || null,
    querySelectorAll: (sel) => (sel === '[data-map-field]'
      ? mapFields.map(([field, value]) => ({ value, dataset: { mapField: field } })) : []),
    querySelector: (sel) => {
      const m = sel.match(/^\[data-amt-(idx|type|neg|vat)="(\d+)"\]$/);
      if (!m) return null;
      const row = amt[Number(m[2])] || {};
      if (m[1] === 'type') return { value: row.type || '' };
      return { checked: !!row[m[1]] };
    },
  };
}

function runScenarios(factory) {
  const failures = [];
  const check = (name, cond, got) => { if (!cond) failures.push(`${name} — ได้ ${JSON.stringify(got)}`); };

  // ── readHeader ──
  {
    const dom = fakeDom({
      hPayoutRef: { value: '' }, hPayoutDate: { value: '2026-09-20' }, hFrom: { value: '' }, hTo: { value: '' },
      hBank: { value: '' }, hNote: { value: ' ' },
      hNetPayout: { value: '948.48' }, hOpening: { value: '' }, hClosing: { value: '0' },
    }, [], []);
    const page = factory(dom);
    page.imp = { channelId: 'ch-1' };
    const h = page.readHeader();
    check('ยอดโอนที่กรอกไปถึง payload', h.netPayout === 948.48, h);
    check('ยอด wallet ต้นรอบว่าง = ตัดคีย์ (ไม่ใช่ null)', !('openingWalletBalance' in h), h);
    check('0 ที่พิมพ์เองต้องไม่หาย', h.closingWalletBalance === 0, h);
    check('ข้อความว่างไม่ส่ง', !('payoutRef' in h) && !('note' in h) && !('bankAccountId' in h), h);
    check('ช่องทางติดไปด้วย', h.channelId === 'ch-1', h);
  }
  {
    const dom = fakeDom({ hPayoutDate: { value: '2026-09-20' }, hNetPayout: { value: '' }, hOpening: { value: '' }, hClosing: { value: '' } }, [], []);
    const page = factory(dom);
    page.imp = { channelId: 'ch-1' };
    const h = page.readHeader();
    check('ยอดโอนว่าง = ไม่มีคีย์ (หน้าเว็บเตือนก่อนส่ง · ไม่ส่ง null)', !('netPayout' in h), h);
    check('ไม่มีค่า null ใน payload เลย', Object.values(h).every(v => v !== null), h);
  }

  // ── readMap แบบกว้าง ──
  {
    const headers = ['Order ID', 'Order Date', 'Buyer Name', 'Product Price', 'Commission Fee', 'Phone'];
    const dom = fakeDom({ mLayout: { value: 'Wide' }, mDateOrder: { value: 'DayMonthYear' } },
      [['orderId', 'Order ID'], ['date', 'Order Date'], ['description', '']],
      [{}, {}, {}, { idx: true }, { idx: true, type: 'Commission', neg: true }, {}]);
    const page = factory(dom);
    page.imp = { inspection: { headers } };
    const map = page.readMap();
    check('layout = Wide', map.layout === 'Wide', map);
    check('ช่องที่เลือกถูกเก็บ · ช่องว่างไม่ส่ง', map.orderId === 'Order ID' && map.date === 'Order Date' && !('description' in map), map);
    check('คอลัมน์ยอดที่ติ๊กครบ 2', (map.amountColumns || []).length === 2, map.amountColumns);
    const comm = (map.amountColumns || []).find(a => a.header === 'Commission Fee') || {};
    check('ประเภท/กลับเครื่องหมายของคอลัมน์ติดไป', comm.type === 'Commission' && comm.negate === true && comm.vatExclusive === false, comm);
    check('คอลัมน์ที่ไม่ใช้ = ignore (ชื่อผู้ซื้อ · เบอร์)', JSON.stringify(map.ignore) === JSON.stringify(['Buyer Name', 'Phone']), map.ignore);
    check('คอลัมน์ที่ใช้แล้วไม่อยู่ใน ignore', !(map.ignore || []).includes('Order ID') && !(map.ignore || []).includes('Product Price'), map.ignore);
  }
  // ── readMap แบบยาว ──
  {
    const headers = ['Type', 'Amount', 'Ref'];
    const dom = fakeDom({ mLayout: { value: 'Long' }, mDateOrder: { value: 'Auto' }, mNegate: { checked: true }, mVatExcl: { checked: false } },
      [['type', 'Type'], ['amount', 'Amount'], ['amountIn', '']], [{ idx: true }, { idx: true }, {}]);
    const page = factory(dom);
    page.imp = { inspection: { headers } };
    const map = page.readMap();
    check('แบบยาวไม่มีคอลัมน์ยอดหลายคอลัมน์ค้าง (Validate ของ service จะตีกลับ)', Array.isArray(map.amountColumns) && map.amountColumns.length === 0, map);
    check('ตัวเลือกกลับเครื่องหมายติดไป', map.negate === true && map.vatExclusive === false, map);
    check('Ref ที่ไม่ใช้ = ignore', JSON.stringify(map.ignore) === JSON.stringify(['Ref']), map.ignore);
  }
  return failures;
}

const readHeaderSrc = extractMethod(pageSrc, 'readHeader');
const readMapSrc = extractMethod(pageSrc, 'readMap');
if (!readHeaderSrc || !readMapSrc || !numOrNullSrc) {
  console.log('❌ หา readHeader/readMap ใน settlements.html หรือ numOrNull ใน layout.js ไม่เจอ — ถูกลบ/เปลี่ยนชื่อ?');
  process.exit(1);
}

let fail = 0;
const real = runScenarios(buildPage(readHeaderSrc, readMapSrc));
if (real.length) { console.log('❌ settlement_import_form_sim: โค้ดจริงผิด'); real.forEach(f => console.log('  ✗ ' + f)); fail = 1; }

// ── negative test: ใส่บั๊กกลับแล้วต้องจับได้ (sim ที่จับบั๊กไม่ได้ = ไม่มีด่าน · F2 ข้อ 6) ──
const mutants = [
  ['ส่ง null เมื่อช่องตัวเลขว่าง', readHeaderSrc.replace('if (n !== null) h[key] = n;', 'h[key] = n;'), readMapSrc],
  ['ลืมใส่คอลัมน์ที่ไม่ใช้ลง ignore', readHeaderSrc, readMapSrc.replace('map.ignore = headers.filter(h => !used.has(h));', 'map.ignore = [];')],
  ['คอลัมน์ยอดไม่ถูกนับว่าใช้แล้ว', readHeaderSrc, readMapSrc.replace('            used.add(h);\n', '')],
];
for (const [name, h, m] of mutants) {
  if (h === readHeaderSrc && m === readMapSrc) { console.log(`❌ negative test "${name}": ใส่บั๊กไม่ติด (โค้ดหน้าเปลี่ยน — แก้ sim)`); fail = 1; continue; }
  if (runScenarios(buildPage(h, m)).length === 0) { console.log(`❌ negative test "${name}": sim ไม่จับ`); fail = 1; }
}

// ── matchBody (review198-D D-01): ผู้สมัครที่เซิร์ฟเวอร์บอกว่าเลือกไม่ได้ต้องไม่ถูกส่ง · รายการรับชำระ = paymentIntentId · เอกสาร = documentId ──
const matchBodySrc = (pageSrc.match(/\n      matchBody\(l, pick\) \{[\s\S]*?\n      \},/) || [])[0];
function runMatchScenarios(src) {
  const failures = [];
  const check = (name, cond, got) => { if (!cond) failures.push(`${name} — ได้ ${JSON.stringify(got)}`); };
  // eslint-disable-next-line no-new-func
  const page = new Function('return ({' + src + '});')();
  const line = { matchCandidates: [
    { kind: 'Document', id: 'doc-1', selectable: true },
    { kind: 'PaymentIntent', id: 'pi-1', selectable: true },
    { kind: 'PaymentIntent', id: 'pi-2', selectable: false, selectReason: 'ยอดไม่ตรง' },
    { kind: 'Reservation', id: 'rs-1', selectable: false },
  ] };
  const d = page.matchBody(line, '0');
  check('เอกสาร = documentId', d && d.documentId === 'doc-1' && !('paymentIntentId' in d), d);
  const i = page.matchBody(line, '1');
  check('รายการรับชำระ = paymentIntentId (ไม่ส่งเป็น documentId ⇒ เดิม 404 เสมอ)', i && i.paymentIntentId === 'pi-1' && !('documentId' in i), i);
  check('ผู้สมัครที่เลือกไม่ได้ ⇒ ไม่ส่ง', page.matchBody(line, '2') === null, page.matchBody(line, '2'));
  check('การจองที่พัก ⇒ ไม่ส่ง', page.matchBody(line, '3') === null, page.matchBody(line, '3'));
  check('ใบขายสรุปรายวัน', JSON.stringify(page.matchBody(line, 'summary')) === JSON.stringify({ useDailySummary: true }), page.matchBody(line, 'summary'));
  check('ดัชนีเกิน ⇒ ไม่ส่ง', page.matchBody(line, '9') === null, page.matchBody(line, '9'));
  return failures;
}
let matchMutants = 0;
if (!matchBodySrc) { console.log('❌ หา matchBody ใน settlements.html ไม่เจอ — ถูกลบ/เปลี่ยนชื่อ?'); fail = 1; }
else {
  const mr = runMatchScenarios(matchBodySrc);
  if (mr.length) { console.log('❌ settlement_import_form_sim: matchBody ผิด'); mr.forEach(f => console.log('  ✗ ' + f)); fail = 1; }
  const mm = [
    ['ส่ง id ของรายการรับชำระเป็น documentId (บั๊ก D-01 เดิม)', matchBodySrc.replace("m.kind === 'PaymentIntent'", 'false')],
    ['ไม่ดูธง selectable ของเซิร์ฟเวอร์', matchBodySrc.replace('|| m.selectable !== true', '')],
  ];
  for (const [name, src] of mm) {
    matchMutants++;
    if (src === matchBodySrc) { console.log(`❌ negative test "${name}": ใส่บั๊กไม่ติด (โค้ดหน้าเปลี่ยน — แก้ sim)`); fail = 1; continue; }
    if (runMatchScenarios(src).length === 0) { console.log(`❌ negative test "${name}": sim ไม่จับ`); fail = 1; }
  }
}

if (!fail) console.log(`✅ settlement_import_form_sim: ตัวอ่านฟอร์มนำเข้ารอบโอน + คำขอตัดสินการจับคู่ถูกทั้งสองทิศ · negative test ${mutants.length + matchMutants} ตัวจับได้ครบ`);
process.exit(fail);
