// ล็อกพฤติกรรม "ตัวแสดงกำลังทำงานกลาง" ด้วย **โค้ดจริง** (`ApiBusy` + `API.request`
// ใน wwwroot/js/api.js) ไม่ใช่โค้ดที่เขียนซ้ำในเทสต์
//
// ที่มา: เจ้าของรายงาน รอบ 190 ข้อ 4 — "หลังกดปุ่มคำสั่ง ระบบนิ่งเหมือนค้าง ไม่รู้ว่าทำงานอยู่
// ผู้ใช้จะไปกดเมนูอื่นต่อ"
//
// ทิศที่ต้องล็อกพร้อมกัน (F2 ข้อ 8 — เข้มขึ้นต้องมีทิศตรงข้าม):
//   ✓ นานเกิน 400ms → แสดง · จบ → ซ่อน · ล้ม → ซ่อน · ซ้อนกัน → ซ่อนเมื่อหมดทุกตัว
//   ✓ คำขอสั้น → **ไม่กระพริบเลย** (ทั้งแถบและปุ่ม)
//   ✓ ปุ่มที่กด: กันคลิกซ้ำ · disable + "กำลังดำเนินการ…" เมื่อนาน · คืนสภาพเมื่อจบ
//   ✓ ปุ่มที่หน้าคุมเอง / ข้อความที่หน้าเขียนทับ → ห้ามแตะ
//
// และ **negative test ของตัว sim เอง** (F2 ข้อ 6): ใส่บั๊กกลับเข้าไปในซอร์ส (ลบ finally ·
// ตั้งหน่วงเป็น 0) แล้วชุดเดียวกันต้องล้ม — sim ที่ผ่านทั้งตอนถูกและตอนผิด = ไม่มีด่าน
'use strict';
const fs = require('fs');
const path = require('path');

const SRC = fs.readFileSync(path.join(__dirname, '..', 'Accounting', 'wwwroot', 'js', 'api.js'), 'utf8');

// ───────── นาฬิกาปลอม ─────────
function makeClock() {
  let now = 1_000_000;
  let seq = 0;
  const timers = new Map();
  const add = (fn, ms, repeat) => {
    const id = ++seq;
    timers.set(id, { fn, at: now + Math.max(0, ms || 0), every: repeat ? Math.max(1, ms || 0) : 0 });
    return id;
  };
  const flush = () => new Promise(r => setImmediate(r));
  return {
    get now() { return now; },
    setTimeout: (fn, ms) => add(fn, ms, false),
    clearTimeout: (id) => { timers.delete(id); },
    setInterval: (fn, ms) => add(fn, ms, true),
    clearInterval: (id) => { timers.delete(id); },
    async advance(ms) {
      const target = now + ms;
      await flush();
      for (;;) {
        let nextId = null, next = null;
        for (const [id, t] of timers) if (t.at <= target && (!next || t.at < next.at || (t.at === next.at && id < nextId))) { next = t; nextId = id; }
        if (!next) break;
        now = next.at;
        if (next.every) next.at = now + next.every; else timers.delete(nextId);
        next.fn();
        await flush();
      }
      now = target;
      await flush();
    },
  };
}

// ───────── DOM จิ๋ว ─────────
class FakeEl {
  constructor(tag) {
    this.tag = tag; this.attrs = {}; this.children = []; this.style = {};
    this.disabled = false; this.isConnected = true; this.offsetWidth = 96;
    this._html = ''; this._q = {}; this.textContent = '';
    this.classHistory = [];
    const set = new Set();
    this.classList = {
      add: (c) => { set.add(c); this.classHistory.push('+' + c); },
      remove: (c) => { set.delete(c); },
      contains: (c) => set.has(c),
    };
  }
  get innerHTML() { return this._html; }
  set innerHTML(v) { this._html = String(v); }
  setAttribute(k, v) { this.attrs[k] = String(v); }
  getAttribute(k) { return k in this.attrs ? this.attrs[k] : null; }
  removeAttribute(k) { delete this.attrs[k]; }
  hasAttribute(k) { return k in this.attrs; }
  appendChild(c) { this.children.push(c); return c; }
  querySelector(sel) { return this._q[sel] || (this._q[sel] = new FakeEl('span')); }
  closest(sel) { return (this.tag === 'button' || (this.tag === 'a' && this.attrs.class === 'btn')) ? this : (this.parent ? this.parent.closest(sel) : null); }
}

function makeEnv() {
  const clock = makeClock();
  const listeners = { click: [], keydown: [] };
  const winListeners = { beforeunload: [] };
  const body = new FakeEl('body');
  const head = new FakeEl('head');
  const byId = {};
  const document = {
    body, head,
    addEventListener: (t, fn) => { (listeners[t] = listeners[t] || []).push(fn); },
    createElement: (tag) => new FakeEl(tag),
    getElementById: (id) => byId[id] || null,
    querySelector: () => null, querySelectorAll: () => [],
  };
  const origAppend = body.appendChild.bind(body);
  body.appendChild = (c) => { if (c.id) byId[c.id] = c; return origAppend(c); };
  head.appendChild = (c) => { if (c.id) byId[c.id] = c; return c; };
  const window = { addEventListener: (t, fn) => { (winListeners[t] = winListeners[t] || []).push(fn); }, location: { href: '/x' } };

  // fetch ปลอม: คิวคำตอบที่ผู้ทดสอบกำหนด (หน่วงเท่าไร · ตอบอะไร)
  const plan = [];
  const fetch = () => {
    const p = plan.shift() || { ms: 10, status: 200 };
    return new Promise((resolve, reject) => {
      clock.setTimeout(() => {
        if (p.networkError) return reject(new TypeError('Failed to fetch'));
        resolve({
          status: p.status, ok: p.status >= 200 && p.status < 300,
          headers: { get: () => 'application/json' },
          json: async () => (p.status >= 200 && p.status < 300 ? { success: true, data: p.data ?? null } : { success: false, message: 'ล้ม' }),
          text: async () => '',
        });
      }, p.ms);
    });
  };
  return { clock, listeners, winListeners, body, byId, document, window, fetch, plan };
}

function load(src) {
  const env = makeEnv();
  const sandbox = {
    localStorage: { getItem: () => null, setItem: () => {}, removeItem: () => {} },
    sessionStorage: { getItem: () => null, setItem: () => {} },
    document: env.document, window: env.window, fetch: env.fetch,
    setTimeout: env.clock.setTimeout, clearTimeout: env.clock.clearTimeout,
    setInterval: env.clock.setInterval, clearInterval: env.clock.clearInterval,
    CSS: { escape: (s) => s }, console,
  };
  const names = Object.keys(sandbox);
  // eslint-disable-next-line no-new-func
  const fn = new Function(...names, src + '\n;return { API, ApiBusy };');
  const { API, ApiBusy } = fn(...names.map(n => sandbox[n]));
  ApiBusy._now = () => env.clock.now;   // นาฬิกาเดียวกับ timer ปลอม
  return { env, API, ApiBusy };
}

// คลิกแบบเบราว์เซอร์: capture listener ของ document ก่อน แล้วค่อย handler ของปุ่ม
function click(env, el, handler) {
  let stopped = false;
  const ev = { target: el, preventDefault() {}, stopImmediatePropagation() { stopped = true; } };
  for (const l of env.listeners.click) { l(ev); if (stopped) break; }
  if (!stopped && handler) return handler();
  return null;
}

const bar = (env) => env.byId.apiBusyBar;
const barShown = (env) => !!bar(env) && bar(env).classList.contains('show');
const barEverShown = (env) => !!bar(env) && bar(env).classHistory.includes('+show');
const barText = (env) => bar(env) ? bar(env).querySelector('.api-busy-text').textContent : '';

// ───────── ชุดทดสอบ (คืนจำนวนที่ล้ม) ─────────
async function suite(src, quiet) {
  let fail = 0;
  const check = (name, cond, extra = '') => {
    if (!quiet) console.log(`  ${cond ? '✓' : '✗'} ${name}${!cond && extra ? ' — ' + extra : ''}`);
    if (!cond) fail++;
  };
  const say = (t) => { if (!quiet) console.log(t); };

  {
    say('1. คำขอสั้น (<400ms) — ต้องไม่กระพริบ');
    const { env, API } = load(src);
    env.plan.push({ ms: 250, status: 200 });
    const p = API.get('/api/x');
    await env.clock.advance(300); await p;
    await env.clock.advance(1000);
    check('แถบไม่เคยโผล่', !barEverShown(env));
  }
  {
    say('2. คำขอนาน — โผล่หลัง 400ms · หายเมื่อจบ · ข้อความบอกว่ากำลังทำอะไร');
    const { env, API } = load(src);
    env.plan.push({ ms: 1500, status: 200 });
    const p = API.get('/api/reports/pl');
    await env.clock.advance(399);
    check('ยังไม่โผล่ที่ 399ms', !barShown(env));
    await env.clock.advance(2);
    check('โผล่ที่ 401ms', barShown(env));
    check('ข้อความ "กำลังโหลดข้อมูล…"', barText(env) === 'กำลังโหลดข้อมูล…', barText(env));
    await env.clock.advance(1200); await p;
    check('หายเมื่อคำขอจบ', !barShown(env));
  }
  {
    say('3. คำขอล้ม (เครือข่าย) — ต้องหายเสมอ ไม่ค้าง');
    const { env, API } = load(src);
    env.plan.push({ ms: 900, networkError: true });
    const p = API.post('/api/companies/c/document', {});
    let threw = false; p.catch(() => { threw = true; });
    await env.clock.advance(500);
    check('โผล่ระหว่างรอ', barShown(env));
    await env.clock.advance(600);
    check('ผู้เรียกยังได้ error เหมือนเดิม', threw);
    check('หายหลังล้ม', !barShown(env));
  }
  {
    say('3b. เซิร์ฟเวอร์ตอบ 500 — หายเช่นกัน');
    const { env, API } = load(src);
    env.plan.push({ ms: 700, status: 500 });
    const p = API.put('/api/x', {}); p.catch(() => {});
    await env.clock.advance(800);
    check('หายหลัง HTTP 500', !barShown(env) && barEverShown(env));
  }
  {
    say('4. คำขอซ้อน — ซ่อนเมื่อหมดทุกตัว');
    const { env, API } = load(src);
    env.plan.push({ ms: 600, status: 200 }, { ms: 1100, status: 200 });
    const a = API.get('/api/a');
    await env.clock.advance(100);
    const b = API.get('/api/b');
    await env.clock.advance(400);   // t=500
    check('โผล่ขณะมีสองคำขอ', barShown(env));
    await env.clock.advance(200);   // t=700: a จบแล้ว b ยังอยู่
    await a;
    check('ยังโผล่เพราะ b ยังไม่จบ', barShown(env));
    await env.clock.advance(600); await b;   // t=1300
    check('หายเมื่อหมดทุกตัว', !barShown(env));
  }
  {
    say('5. ปุ่มคำสั่ง — กันกดซ้ำ · disable + "กำลังดำเนินการ…" · คืนสภาพ');
    const { env, API } = load(src);
    const btn = new FakeEl('button'); btn.innerHTML = '💾 บันทึก';
    let calls = 0;
    const handler = () => { calls++; return API.post('/api/companies/c/document', {}); };
    env.plan.push({ ms: 1000, status: 200 });
    // ต้องมี listener ติดตั้งก่อนคลิกแรก — ในหน้าจริงคำขอ GET ตอนโหลดหน้าติดตั้งให้
    env.plan.unshift({ ms: 1, status: 200 }); await Promise.all([API.get('/api/boot'), env.clock.advance(5)]);
    const p = click(env, btn, handler);
    await env.clock.advance(100);
    check('หน้าตาปุ่มยังไม่เปลี่ยนก่อน 400ms (ไม่กระพริบ)', btn.innerHTML === '💾 บันทึก' && !btn.disabled);
    click(env, btn, handler);
    check('คลิกซ้ำระหว่างรอถูกกลืน (ยิงคำสั่งครั้งเดียว)', calls === 1, `calls=${calls}`);
    await env.clock.advance(350);
    check('เกิน 400ms: ปุ่ม disable', btn.disabled === true);
    check('เกิน 400ms: ขึ้น "กำลังดำเนินการ…"', btn.innerHTML.includes('กำลังดำเนินการ…'), btn.innerHTML);
    check('แถบบนบอก "กำลังบันทึก…"', barText(env) === 'กำลังบันทึก…', barText(env));
    await env.clock.advance(600); await p;
    await env.clock.advance(1);
    check('จบแล้วปุ่มกลับมาเหมือนเดิม', btn.innerHTML === '💾 บันทึก' && btn.disabled === false, btn.innerHTML);
    check('จบแล้วกดได้อีก', !btn.hasAttribute('data-api-busy'));
  }
  {
    say('6. ปุ่มคำสั่งสั้น — ปุ่มไม่กระพริบเลย');
    const { env, API } = load(src);
    env.plan.push({ ms: 1, status: 200 }); await Promise.all([API.get('/api/boot'), env.clock.advance(5)]);
    const btn = new FakeEl('button'); btn.innerHTML = 'ลบ';
    env.plan.push({ ms: 150, status: 200 });
    const p = click(env, btn, () => API.del('/api/x/1'));
    let everDisabled = false;
    for (let i = 0; i < 20; i++) { await env.clock.advance(10); everDisabled = everDisabled || btn.disabled; }
    await p; await env.clock.advance(1);
    check('ไม่เคยถูก disable', !everDisabled);
    check('ข้อความไม่เคยเปลี่ยน', btn.innerHTML === 'ลบ');
    check('ปล่อยธงกันคลิกซ้ำแล้ว', !btn.hasAttribute('data-api-busy'));
  }
  {
    say('7. ปุ่มที่หน้าคุมเอง (disable ไว้ก่อนยิง) — ห้ามแตะ');
    const { env, API } = load(src);
    env.plan.push({ ms: 1, status: 200 }); await Promise.all([API.get('/api/boot'), env.clock.advance(5)]);
    const btn = new FakeEl('button'); btn.innerHTML = 'บันทึก';
    env.plan.push({ ms: 900, status: 200 });
    const p = click(env, btn, async () => {
      btn.disabled = true; btn.innerHTML = 'กำลังบันทึก...';
      try { await API.post('/api/x', {}); } finally { btn.disabled = false; btn.innerHTML = 'บันทึก'; }
    });
    await env.clock.advance(1000); await p; await env.clock.advance(1);
    check('หน้าคืนสภาพเองได้ ไม่ถูกเขียนทับกลับเป็นข้อความรอ', btn.innerHTML === 'บันทึก' && btn.disabled === false, btn.innerHTML);
  }
  {
    say('8. หน้าเขียนข้อความผลลัพธ์ลงปุ่มเอง — ข้อความของหน้าต้องชนะ');
    const { env, API } = load(src);
    env.plan.push({ ms: 1, status: 200 }); await Promise.all([API.get('/api/boot'), env.clock.advance(5)]);
    const btn = new FakeEl('button'); btn.innerHTML = 'ส่ง';
    env.plan.push({ ms: 800, status: 200 });
    const p = click(env, btn, async () => { await API.post('/api/send', {}); btn.innerHTML = '✓ ส่งแล้ว'; });
    await env.clock.advance(900); await p; await env.clock.advance(1);
    check('ไม่เขียนทับ "✓ ส่งแล้ว" ด้วยข้อความเดิม', btn.innerHTML === '✓ ส่งแล้ว', btn.innerHTML);
    check('ปุ่มไม่ค้าง disable', btn.disabled === false);
  }
  {
    say('9. คำสั่งต่อกันในคลิกเดียว (บันทึก → อนุมัติ) — ไม่มีช่องให้กดซ้ำระหว่างสองคำสั่ง');
    const { env, API } = load(src);
    env.plan.push({ ms: 1, status: 200 }); await Promise.all([API.get('/api/boot'), env.clock.advance(5)]);
    const btn = new FakeEl('button'); btn.innerHTML = 'บันทึกและอนุมัติ';
    env.plan.push({ ms: 500, status: 200 }, { ms: 500, status: 200 });
    let freeInBetween = false;
    const p = click(env, btn, async () => {
      await API.post('/api/doc', {});
      freeInBetween = !btn.hasAttribute('data-api-busy');
      await API.post('/api/doc/1/approve', {});
    });
    await env.clock.advance(700);
    check('ระหว่างคำสั่งที่สอง ปุ่มยังล็อก', btn.hasAttribute('data-api-busy'));
    check('แถบไม่ดับแล้วติดใหม่ระหว่างสองคำสั่ง (ไม่กระพริบ)', barShown(env) && bar(env).classHistory.filter(c => c === '+show').length === 1);
    check('แถบบอก "กำลังอนุมัติ…"', barText(env) === 'กำลังอนุมัติ…', barText(env));
    await env.clock.advance(400); await p; await env.clock.advance(1);
    check('ไม่มีจังหวะปล่อยปุ่มระหว่างสองคำสั่ง', !freeInBetween);
    check('จบแล้วคืนสภาพ', btn.innerHTML === 'บันทึกและอนุมัติ' && !btn.disabled);
  }
  {
    say('10. งานนานมาก (OCR) — บอกว่ากำลังอ่านเอกสาร + เวลาที่ผ่านไป');
    const { env, API } = load(src);
    env.plan.push({ ms: 12000, status: 200 });
    const fd = {};
    const p = API.upload('/api/companies/c/ocr/upload', fd);
    await env.clock.advance(500);
    check('ข้อความ OCR', barText(env).startsWith('กำลังอ่านเอกสารด้วย OCR'), barText(env));
    await env.clock.advance(8600);
    check('เกิน 8 วินาที: บอกว่ายังทำงาน + อย่าเปลี่ยนหน้า', /ยังทำงานอยู่ \([89] วินาที\).*อย่าปิดหรือเปลี่ยนหน้า/.test(barText(env)), barText(env));
    await env.clock.advance(3000); await p;
    check('จบแล้วหาย', !barShown(env));
  }
  {
    say('11. งานเบื้องหลัง (API.quietly) — ไม่ขึ้นแถบ');
    const { env, API } = load(src);
    env.plan.push({ ms: 2000, status: 200 });
    const p = API.quietly(() => API.get('/api/notification/count'));
    await env.clock.advance(2100); await p;
    check('ไม่เคยโผล่', !barEverShown(env));
  }
  {
    say('12. HTTP 401 (redirect ไปหน้า login) — ไม่ค้าง');
    const { env, API } = load(src);
    env.plan.push({ ms: 600, status: 401 });
    const p = API.get('/api/x');
    await env.clock.advance(700); await p;
    check('หายหลัง redirect', !barShown(env));
  }
  {
    say('13. ปิดหน้าระหว่างคำสั่งที่ผู้ใช้สั่ง — เบราว์เซอร์ต้องถามก่อน · ไม่ถามเมื่อไม่มีงานค้าง');
    const { env, API } = load(src);
    env.plan.push({ ms: 1, status: 200 }); await Promise.all([API.get('/api/boot'), env.clock.advance(5)]);
    const btn = new FakeEl('button'); btn.innerHTML = 'อนุมัติ';
    env.plan.push({ ms: 2000, status: 200 });
    const p = click(env, btn, () => API.post('/api/doc/1/approve', {}));
    await env.clock.advance(600);
    const ev = { prevented: false, preventDefault() { this.prevented = true; } };
    env.winListeners.beforeunload.forEach(l => l(ev));
    check('ระหว่างรอ: ถามก่อนออก', ev.prevented);
    await env.clock.advance(1500); await p; await env.clock.advance(1);
    const ev2 = { prevented: false, preventDefault() { this.prevented = true; } };
    env.winListeners.beforeunload.forEach(l => l(ev2));
    check('จบแล้ว: ไม่ถาม', !ev2.prevented);
  }
  return fail;
}

(async () => {
  console.log('ชุดหลัก — โค้ดจริงใน api.js');
  const fail = await suite(SRC, false);

  // negative test ของ sim: ใส่บั๊กกลับ แล้วชุดเดียวกันต้องล้ม
  console.log('\nnegative test — ใส่บั๊กกลับเข้าไปแล้ว sim ต้องจับได้');
  const mutants = [
    ['ลบ ApiBusy.end ใน finally (ล้มแล้วค้าง)', SRC.replace('ApiBusy.end(busyTok);', '')],
    ['หน่วงเป็น 0 (กระพริบทุกคำขอ)', SRC.replace('DELAY_MS: 400,', 'DELAY_MS: 0,')],
    ['ไม่คืนข้อความปุ่ม', SRC.replace('if (el.innerHTML === st.busyHtml) el.innerHTML = st.prevHtml;', '')],
    ['ไม่กันคลิกซ้ำ', SRC.replace("el.setAttribute('data-api-busy', '1');", '')],
  ];
  let mutFail = 0;
  for (const [name, src] of mutants) {
    if (src === SRC) { console.log(`  ✗ ${name} — หาจุดที่จะใส่บั๊กไม่เจอ (ซอร์สเปลี่ยน? แก้ sim ให้ตรง)`); mutFail++; continue; }
    const f = await suite(src, true);
    console.log(`  ${f > 0 ? '✓' : '✗'} ${name} → sim ล้ม ${f} ข้อ`);
    if (f === 0) mutFail++;
  }

  const total = fail + mutFail;
  console.log(total === 0 ? '\n✅ api_busy_indicator_sim ผ่านทุกทิศ'
                          : `\n❌ api_busy_indicator_sim ล้ม ${total} ข้อ`);
  process.exit(total === 0 ? 0 : 1);
})();
