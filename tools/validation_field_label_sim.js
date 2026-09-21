// ล็อกพฤติกรรม "ข้อความ validation ต้องชี้ช่องที่ผู้ใช้เห็น" ด้วย **โค้ดจริง**
// (`describeFieldErrors` ใน wwwroot/js/api.js) ไม่ใช่โค้ดที่เขียนซ้ำในเทสต์
//
// ที่มา: ผู้ใช้รายงาน 2026-09-21 — กด "💾 บันทึกการตั้งค่า" ที่หน้าตั้งค่าที่พัก
// แล้วได้ toast "One or more validation errors occurred. — Code: The Code field
// is required." · คำร้องตรงตัว: "ไม่มีบอกว่า Require อันไหน หรือ ขาดอะไร อันไหน"
//
// สองทิศที่ต้องล็อกพร้อมกัน (F2 ข้อ 8):
//   ✓ ช่องที่**มีอยู่บนหน้า** → ต้องได้ป้ายไทยจริง + ชื่อส่วน + ไฮไลต์ + เปิดแท็บให้
//   ✓ ช่องที่**ไม่มีบนหน้า**  → ต้องบอกตรง ๆ ว่าไม่มี (G3) ห้ามสั่งให้ไปกรอกของที่มองไม่เห็น
'use strict';
const fs = require('fs');
const path = require('path');

// ───────── DOM จิ๋ว: รองรับเฉพาะ selector ที่ describeFieldErrors ใช้จริง ─────────
class El {
  constructor(tag, attrs = {}, children = []) {
    this.tag = tag; this.attrs = attrs; this.children = children;
    this.classList = new Set((attrs.class || '').split(' ').filter(Boolean));
    this.parent = null; this.focused = false; this.scrolled = false;
    children.forEach(c => { c.parent = this; });
    this.classList = {
      _s: this.classList,
      add: (c) => this.classList._s.add(c),
      remove: (c) => this.classList._s.delete(c),
      contains: (c) => this.classList._s.has(c),
      has: (c) => this.classList._s.has(c),
    };
  }
  get textContent() {
    return this.attrs.text != null ? this.attrs.text
      : this.children.map(c => c.textContent).join('');
  }
  get id() { return this.attrs.id || ''; }
  setAttribute(k, v) { this.attrs[k] = v; }
  removeAttribute(k) { delete this.attrs[k]; }
  focus() { this.focused = true; }
  scrollIntoView() { this.scrolled = true; }
  _match(sel) {
    for (const part of sel.split(',').map(s => s.trim())) {
      if (part.startsWith('.') && this.classList.contains(part.slice(1))) return true;
      if (part.startsWith('[name=')) {
        const want = part.slice(6, -1).replace(/^"|"$/g, '');
        if (this.attrs.name === want) return true;
      }
      if (/^[a-z0-9]+$/i.test(part) && this.tag === part) return true;
    }
    return false;
  }
  closest(sel) { let n = this; while (n) { if (n._match(sel)) return n; n = n.parent; } return null; }
  contains(el) { let n = el; while (n) { if (n === this) return true; n = n.parent; } return false; }
  *walk() { yield this; for (const c of this.children) yield* c.walk(); }
  querySelector(sel) { for (const n of this.walk()) if (n !== this && n._match(sel)) return n; return null; }
  querySelectorAll(sel) { const out = []; for (const n of this.walk()) if (n._match(sel)) out.push(n); return out; }
}

// โครงเดียวกับ wwwroot/pages/lodging-settings.html (card > h4 + .fld > label + input)
function buildPage() {
  const codeInput = new El('input', { class: 'form-input', name: 'code' });
  const nameInput = new El('input', { class: 'form-input', name: 'name', required: '' });
  const card = new El('div', { class: 'card' }, [
    new El('h4', { text: 'ข้อมูลที่พัก' }),
    new El('div', { class: 'fld' }, [new El('label', { text: 'ชื่อที่พัก *' }), nameInput]),
    new El('div', { class: 'fld' }, [
      new El('label', { text: 'รหัส (ใช้ในเลขจอง RES-XXXX-…)' }), codeInput]),
  ]);
  const panel = new El('div', { id: 'tabGeneral' }, [card]);
  const root = new El('body', {}, [panel]);
  return { root, codeInput, nameInput, panel };
}

// ───────── โหลด API จากไฟล์จริง ─────────
function loadApi(root) {
  const src = fs.readFileSync(path.join(__dirname, '..', 'Accounting', 'wwwroot', 'js', 'api.js'), 'utf8');
  const sandbox = {
    localStorage: { getItem: () => null, setItem: () => {}, removeItem: () => {} },
    document: {
      querySelector: (s) => root.querySelector(s),
      querySelectorAll: (s) => root.querySelectorAll(s),
    },
    CSS: { escape: (s) => String(s).replace(/([^\w-])/g, '\\$1') },
    fetch: () => { throw new Error('sim ไม่ควรยิง network'); },
    window: {}, console,
  };
  const names = Object.keys(sandbox);
  // eslint-disable-next-line no-new-func
  const fn = new Function(...names, src + '\n;return API;');
  return fn(...names.map(n => sandbox[n]));
}

// ───────── ตรวจ ─────────
let fail = 0;
function check(name, cond, extra = '') {
  if (cond) { console.log(`  ✓ ${name}`); }
  else { console.log(`  ✗ ${name}${extra ? ' — ' + extra : ''}`); fail++; }
}

{
  console.log('ทิศที่ 1 — ช่องมีอยู่บนหน้า: ต้องได้ป้ายไทย + ส่วน + ไฮไลต์');
  const page = buildPage();
  const api = loadApi(page.root);
  let revealed = null;
  globalThis.Page = { revealField: (n) => { revealed = n; } };
  const msg = api.describeFieldErrors(['code'], ['ต้องมีค่า — เว้นว่างไม่ได้']);

  check('ข้อความมีป้ายไทยที่ผู้ใช้เห็นจริง', msg.includes('รหัส (ใช้ในเลขจอง RES-XXXX-…)'), msg);
  check('ข้อความบอกส่วนที่ช่องอยู่', msg.includes('ข้อมูลที่พัก'), msg);
  check('ไม่มีชื่อ property C# หลุดออกไป', !msg.includes('Code:') && !msg.includes('field is required'), msg);
  check('ช่องถูกไฮไลต์ (has-error)', page.codeInput.classList.contains('has-error'));
  check('ตั้ง aria-invalid ให้ screen reader', page.codeInput.attrs['aria-invalid'] === 'true');
  check('โฟกัส + เลื่อนจอไปที่ช่อง', page.codeInput.focused && page.codeInput.scrolled);
  check('เรียก hook ให้หน้าเปิดแท็บที่ซ่อนอยู่', revealed === 'code');
}

{
  console.log('ทิศที่ 2 — ช่องไม่มีบนหน้า: ต้องบอกว่าไม่มี ไม่ใช่สั่งให้ไปกรอก');
  const page = buildPage();
  const api = loadApi(page.root);
  globalThis.Page = undefined;
  const msg = api.describeFieldErrors(['ghostField'], ['ต้องมีค่า — เว้นว่างไม่ได้']);
  check('บอกตรง ๆ ว่าไม่มีช่องนี้บนหน้านี้', msg.includes('ไม่มีช่องนี้บนหน้านี้'), msg);
  check('บอกทางไปต่อ (แจ้งผู้ดูแล)', msg.includes('แจ้งผู้ดูแลระบบ'), msg);
  check('ไม่พังเมื่อหน้าไม่มี hook revealField', true);
}

{
  console.log('ทิศที่ 3 — ไฮไลต์รอบก่อนต้องถูกล้าง (ไม่งั้นผู้ใช้ไล่ผิดช่อง)');
  const page = buildPage();
  const api = loadApi(page.root);
  api.describeFieldErrors(['code'], ['x']);
  api.describeFieldErrors(['name'], ['y']);
  check('ช่องที่แก้แล้วไม่แดงค้าง', !page.codeInput.classList.contains('has-error'));
  check('ช่องใหม่แดงแทน', page.nameInput.classList.contains('has-error'));
}

{
  console.log('ทิศที่ 4 — ไม่มีช่องส่งมาเลย: ต้องคืนค่าว่างให้ผู้เรียกใช้ข้อความเดิม');
  const page = buildPage();
  const api = loadApi(page.root);
  check('fields ว่าง → คืน ""', api.describeFieldErrors([], []) === '');
  check('fields ไม่ใช่อาร์เรย์ → คืน ""', api.describeFieldErrors(undefined, undefined) === '');
}

console.log(fail === 0 ? '\n✅ validation_field_label_sim ผ่านทุกทิศ'
                       : `\n❌ validation_field_label_sim ล้ม ${fail} ข้อ`);
process.exit(fail === 0 ? 0 : 1);
