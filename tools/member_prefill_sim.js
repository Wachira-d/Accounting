#!/usr/bin/env node
// ═══ ล็อกพฤติกรรม: ตัวเติมฟอร์มจากโปรไฟล์สมาชิก (storefront.html · รอบ 216 · คำตัดสินข้อ 174) ═══
//
// รัน: node tools/member_prefill_sim.js
// (เทียบรุ่นอื่น: SF_PAGE=/path/to/storefront.html node tools/member_prefill_sim.js)
//
// ใช้โค้ดจริงจากหน้า (_memberPrefill · _memberNorm · _memberSaveRefresh · _memberSaveValue · esc · _profileUrl) บน DOM จำลอง ·
// กติกา "ห้ามใครมาก่อนชนะ" (CLAUDE.md กฎ #4 A) — /portal/me เป็น async ผลต้องไม่ขึ้นกับว่าตอบก่อนหรือหลังผู้ใช้เริ่มพิมพ์:
//   (a) ช่องว่าง ⇒ เติมจากโปรไฟล์ + ติด dataset.autoSrc='member'
//   (b) ผู้ใช้พิมพ์ก่อน /portal/me ตอบ ⇒ ค่าผู้ใช้ชนะ (ไม่ถูกทับ) · ช่องที่มีค่าอยู่แล้วแต่ระบบไม่ได้เติม ⇒ ไม่ทับ
//   (c) ค่าตั้งต้นของระบบ (data-auto-src="default" เช่นสาขา 00000) ⇒ โปรไฟล์ (เฉพาะกว่า) ชนะ
//   (d) ช่องที่ spec บอก "เทียบอย่างเดียว" (x[4] === false) ⇒ ไม่เติม
//   (e) ช่อง "บันทึกเป็นข้อมูลของฉัน": ซ่อนเมื่อไม่มีอะไรต่าง · ติ๊กไว้ก่อนเฉพาะเมื่อทุกช่องที่ต่างเป็นช่องที่โปรไฟล์ยังว่าง ·
//       แก้ค่าที่มีอยู่แล้ว ⇒ แสดงแต่ไม่ติ๊ก · ผู้ใช้กดเองแล้ว ⇒ ไม่เปลี่ยนสถานะให้ · ซ่อนอยู่ ⇒ ไม่ขอบันทึก
//   (f) ไม่ได้ล็อกอิน ⇒ ไม่แตะฟอร์ม
// negative test: ถอดด่าน userTouched / ถอดด่าน "มีค่าอยู่แล้ว" / ติ๊กเสมอ ⇒ ต้องล้มทุกตัว
'use strict';
const fs = require('fs');
const path = require('path');

const PAGE = process.env.SF_PAGE || path.join(__dirname, '..', 'Accounting', 'wwwroot', 'storefront.html');
const REAL_SRC = fs.readFileSync(PAGE, 'utf8');
const METHODS = ['esc', '_profileUrl', '_memberPrefill', '_memberNorm', '_memberSaveRefresh', '_memberSaveValue'];

function extract(src, name) {
  const re = new RegExp('\\n      (?:async )?' + name + '\\(');
  const m = re.exec(src);
  if (!m) return null;
  const start = m.index + 1;
  const line = src.slice(start, src.indexOf('\n', start));
  if (/\},\s*$/.test(line)) return line.replace(/,\s*$/, '');   // เมธอดบรรทัดเดียว
  const end = src.indexOf('\n      },', start);
  if (end < 0) return null;
  return src.slice(start, end + '\n      }'.length);
}

// ── DOM จำลองขั้นต่ำ ──
class El {
  constructor(tag, attrs) {
    this.tagName = tag; this.dataset = {}; this.style = {}; this.children = []; this.parent = null; this.listeners = {};
    this.value = ''; this.checked = false; this.type = ''; this.name = ''; this.className = ''; this.id = '';
    Object.assign(this, attrs || {});
    this._html = '';
  }
  set innerHTML(h) {
    this._html = h;
    if (/<input type="checkbox" name="saveToProfile"/.test(h)) {
      const cb = new El('input', { type: 'checkbox', name: 'saveToProfile' });
      this.children = [cb]; cb.parent = this;
    }
  }
  get innerHTML() { return this._html; }
  get firstChild() { return this.children[0] || null; }
  get isConnected() { return true; }
  insertBefore(n, ref) {
    n.parent = this;
    const i = ref ? this.children.indexOf(ref) : -1;
    if (i < 0) this.children.push(n); else this.children.splice(i, 0, n);
  }
  addEventListener(t, fn) { (this.listeners[t] = this.listeners[t] || []).push(fn); }
  all() { return this.children.flatMap(c => [c, ...c.all()]); }
  querySelector(sel) {
    for (const part of sel.split(',').map(x => x.trim())) {
      let m;
      const hit = this.all().find(c =>
        ((m = /^\[name="([^"]+)"\]$/.exec(part)) && c.name === m[1])
        || ((m = /^\.([\w-]+)$/.exec(part)) && c.className === m[1])
        || ((m = /^#([\w-]+)$/.exec(part)) && c.id === m[1])
        || (part === 'button[type="submit"]' && c.tagName === 'button' && c.type === 'submit')
        || (part === 'input' && c.tagName === 'input'));
      if (hit) return hit;
    }
    return null;
  }
  fire(type, target) { for (const fn of this.listeners[type] || []) fn({ target }); }
}
global.document = { createElement: (t) => new El(t) };

function makeForm(fields) {
  const f = new El('form');
  for (const [name, value, extra] of fields) {
    const i = new El('input', Object.assign({ name, value: value || '', type: 'text' }, extra || {}));
    f.insertBefore(i, null);
  }
  f.insertBefore(new El('button', { type: 'submit' }), null);
  return f;
}

function makeStore(src, profile, opts) {
  const parts = METHODS.map(n => {
    const t = extract(src, n);
    if (!t) throw new Error('หาเมธอด ' + n + ' ใน storefront.html ไม่เจอ — ถูกลบ/เปลี่ยนชื่อ?');
    return t;
  });
  // eslint-disable-next-line no-new-func
  const s = new Function('return ({\n' + parts.join(',\n') + '\n});')();
  s._pageUrl = (slug) => '/s/' + slug;
  s._custSession = () => (opts && opts.loggedOut ? null : { token: 't' });
  let release;
  const gate = new Promise(r => { release = r; });
  s._custMeLoad = async () => { if (opts && opts.late) await gate; s._custMe = profile; return profile; };
  s._release = () => release();
  return s;
}

const PROFILE = { fullName: 'สมชาย ใจดี', phone: '0812345678', email: 'a@shop.test', branchCode: '00001', taxId: '0105556000009' };
const SPEC = me => [['customerName', me.fullName, 'text', true], ['customerPhone', me.phone, 'phone', true],
  ['customerEmail', me.email, 'text', false], ['billingBranchCode', me.branchCode, 'text', false], ['guestTaxId', me.taxId, 'digits', true, false]];

async function scenario(src) {
  const fails = [];
  const ok = (cond, msg) => { if (!cond) fails.push(msg); };

  // (a)+(b)+(c)+(d): ผู้ใช้พิมพ์เบอร์ก่อน /portal/me ตอบ · อีเมลมีค่าค้างจากการวาดใหม่ · สาขาเป็นค่าตั้งต้น
  {
    const s = makeStore(src, PROFILE, { late: true });
    const f = makeForm([['customerName'], ['customerPhone'], ['customerEmail', 'keep@x.test'], ['billingBranchCode', '00000', { dataset: { autoSrc: 'default' } }], ['guestTaxId']]);
    f.querySelector('[name="billingBranchCode"]').dataset = { autoSrc: 'default' };
    const p = s._memberPrefill(f, SPEC);
    const phone = f.querySelector('[name="customerPhone"]');
    phone.value = '0899999999'; f.fire('input', phone);   // ผู้ใช้พิมพ์ระหว่างรอ
    s._release(); await p;
    ok(f.querySelector('[name="customerName"]').value === 'สมชาย ใจดี', '(a) ช่องชื่อว่างต้องถูกเติมจากโปรไฟล์');
    ok(f.querySelector('[name="customerName"]').dataset.autoSrc === 'member', '(a) ช่องที่เติมต้องติด autoSrc=member');
    ok(phone.value === '0899999999', '(b) ค่าที่ผู้ใช้พิมพ์ก่อน /portal/me ตอบต้องชนะ — ถูกทับเป็น ' + phone.value);
    ok(f.querySelector('[name="customerEmail"]').value === 'keep@x.test', '(b) ช่องที่มีค่าอยู่แล้ว (ระบบไม่ได้เติม) ต้องไม่ถูกทับ');
    ok(f.querySelector('[name="billingBranchCode"]').value === '00001', '(c) ค่าตั้งต้น (autoSrc=default) ต้องแพ้โปรไฟล์');
    ok(f.querySelector('[name="guestTaxId"]').value === '', '(d) ช่อง "เทียบอย่างเดียว" ต้องไม่ถูกเติม');
    // (e) เบอร์ต่างจากโปรไฟล์ที่มีค่าอยู่แล้ว ⇒ แสดง แต่ไม่ติ๊กให้
    const box = f.querySelector('.sf-save-profile');
    ok(box && box.style.display !== 'none', '(e) พิมพ์เบอร์ต่างจากโปรไฟล์ ⇒ ต้องแสดงช่อง "บันทึกเป็นข้อมูลของฉัน"');
    ok(box && !box.querySelector('input').checked, '(e) แก้ค่าที่โปรไฟล์มีอยู่แล้ว ⇒ ห้ามติ๊กไว้ก่อน');
    ok(s._memberSaveValue(f) === false, '(e) ไม่ติ๊ก ⇒ ไม่ขอบันทึก');
    // (b) ซ้ำ: เรียกเติมอีกรอบ (เช่นวาดใหม่) ต้องไม่ทับค่าที่ผู้ใช้แตะ
    await s._memberPrefill(f, SPEC);
    ok(phone.value === '0899999999', '(b) เติมรอบสองต้องไม่ทับช่องที่ผู้ใช้แตะแล้ว');
    // ผู้ใช้ลบชื่อที่ระบบเติมทิ้ง (ตั้งใจเว้นว่าง) แล้วฟอร์มถูกเติมอีกรอบ ⇒ ต้องคงว่าง
    const name = f.querySelector('[name="customerName"]');
    name.value = ''; f.fire('input', name);
    await s._memberPrefill(f, SPEC);
    ok(name.value === '', '(b) ช่องที่ผู้ใช้ลบค่าที่ระบบเติมทิ้ง ⇒ เติมรอบใหม่ต้องไม่ใส่กลับ');
  }

  // (e) โปรไฟล์ไม่มีเบอร์ ⇒ ผู้ใช้พิมพ์เบอร์ ⇒ ติ๊กไว้ก่อน · ผู้ใช้เอาติ๊กออกแล้วพิมพ์ต่อ ⇒ ไม่ติ๊กกลับให้
  {
    const s = makeStore(src, Object.assign({}, PROFILE, { phone: null }));
    const f = makeForm([['customerName'], ['customerPhone']]);
    await s._memberPrefill(f, SPEC);
    ok(!f.querySelector('.sf-save-profile') || f.querySelector('.sf-save-profile').style.display === 'none', '(e) ยังไม่มีอะไรต่าง ⇒ ซ่อนช่องบันทึก');
    const phone = f.querySelector('[name="customerPhone"]');
    phone.value = '081-111-2222'; f.fire('input', phone);
    const cb = f.querySelector('.sf-save-profile').querySelector('input');
    ok(cb.checked === true, '(e) เติมช่องที่โปรไฟล์ว่าง ⇒ ติ๊กไว้ก่อน');
    ok(s._memberSaveValue(f) === true, '(e) ติ๊กอยู่และแสดงอยู่ ⇒ ขอบันทึก');
    cb.checked = false; f.fire('change', cb);
    phone.value = '081-111-3333'; f.fire('input', phone);
    ok(cb.checked === false, '(e) ผู้ใช้เอาติ๊กออกเองแล้ว ⇒ ห้ามติ๊กกลับให้');
    // เบอร์ที่ต่างแค่รูปแบบ (ขีด/+66) = ไม่ต่าง
    const s2 = makeStore(src, PROFILE);
    const f2 = makeForm([['customerName'], ['customerPhone', '+66 81 234 5678']]);
    await s2._memberPrefill(f2, SPEC);
    const box2 = f2.querySelector('.sf-save-profile');
    ok(!box2 || box2.style.display === 'none', '(e) เบอร์เดียวกันต่างรูปแบบ ⇒ ไม่นับว่าต่าง');
  }

  // (f) ไม่ได้ล็อกอิน ⇒ ไม่แตะ
  {
    const s = makeStore(src, PROFILE, { loggedOut: true });
    const f = makeForm([['customerName']]);
    await s._memberPrefill(f, SPEC);
    ok(f.querySelector('[name="customerName"]').value === '' && !f.querySelector('.sf-member-hint'), '(f) ไม่ได้ล็อกอิน ⇒ ต้องไม่เติม/ไม่แสดงป้าย');
  }
  return fails;
}

(async () => {
  const real = await scenario(REAL_SRC);
  if (real.length) {
    console.error('❌ member_prefill_sim: โค้ดจริงผิดกติกา');
    for (const f of real) console.error('   - ' + f);
    process.exit(1);
  }
  // negative test — ใส่บั๊กกลับแล้วต้องจับได้ (F2 ข้อ 6)
  const mutants = [
    ['ถอดด่าน userTouched', REAL_SRC.replace("if (el.dataset.userTouched === '1') continue;", '')],
    ['ถอดด่าน "มีค่าอยู่แล้วที่ระบบไม่ได้เติม"', REAL_SRC.replace('if (el.value && !el.dataset.autoSrc) continue;', '')],
    ['ติ๊กบันทึกเสมอ', REAL_SRC.replace("cb.checked = fillsEmpty && !changesExisting;", 'cb.checked = true;')],
    ['เติมช่อง "เทียบอย่างเดียว"', REAL_SRC.replace('.filter(x => x[4] !== false)', '')],
  ];
  let bad = 0;
  for (const [name, src] of mutants) {
    if (src === REAL_SRC) { console.error('❌ negative test "' + name + '": หาโค้ดที่จะกลายพันธุ์ไม่เจอ — หน้าเปลี่ยนแล้ว แก้ sim'); bad++; continue; }
    const r = await scenario(src);
    if (!r.length) { console.error('❌ negative test "' + name + '": บั๊กกลับมาแล้ว sim ไม่จับ'); bad++; }
  }
  if (bad) process.exit(1);
  console.log('member_prefill_sim: ผ่าน (กติกาเติมฟอร์มจากโปรไฟล์ + negative test ' + mutants.length + ' ตัว)');
})().catch(e => { console.error('❌ member_prefill_sim: ' + (e && e.stack || e)); process.exit(1); });
