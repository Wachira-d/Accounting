// ล็อกสัญญา "ฟอร์มกฎการอนุมัติ ↔ API" ด้วย **โค้ดจริง** ของ approval.html + DTO จริง
// (Accounting/Models/DTOs/Approval/ApprovalDtos.cs) — รอบ 200 ทีม RF (R200-X3)
//
// ที่มา: หน้า approval.html ส่ง `description: null` และไม่มี `projectId` ตอนแก้กฎ · UpdateRuleAsync เขียนทับสองช่องนั้นตรง ๆ
// ⇒ กฎที่จำกัดโครงการ (สร้างผ่าน API/หน้าอื่น) กลายเป็น "ทุกโครงการ" ทันทีที่เปิดแก้แล้วกดบันทึก — คำอธิบายหายด้วย
// (กฎเหล็ก #4 A ข้อ 2 "เก็บแล้วต้อง echo กลับ" + "ห้าม silent no-op")
//
// สัญญาที่ล็อก (รันเมธอดจริงของ Page บน DOM จำลอง):
//   1. เปิดแก้กฎที่มีคำอธิบาย + โครงการ แล้วบันทึกโดยไม่แตะ ⇒ payload ส่งคำอธิบายเดิม + projectId เดิม (ไม่ล้าง)
//   2. โครงการเดิมที่ไม่อยู่ในรายการโครงการ (ปิดไปแล้ว/เกินหน้า) ⇒ ยังถูกส่งกลับ ไม่ตกเป็น "ทุกโครงการ"
//   3. เลือก "ทุกโครงการ" ตอนแก้กฎที่เคยจำกัดโครงการ ⇒ ส่ง clearProjectId: true (Guid? รับ "" ไม่ได้)
//   4. ล้างคำอธิบายตอนแก้ ⇒ ส่ง "" (= ล้าง ตามกติกาเรพ) · ฟอร์มใหม่หลังเปิดแก้ ⇒ ไม่ค้างค่าเดิม (description null · projectId null)
//   5. ทุกคีย์ใน payload อยู่ใน CreateApprovalRuleRequest (ไม่งั้นเซิร์ฟเวอร์ทิ้งเงียบ)
// baseline (F2 ข้อ 6 — รันกับ `git show 4a9ebd5a:Accounting/wwwroot/pages/approval.html` ตอนเขียน): ล้ม 5 ข้อ (สัญญา 1–4)
// (description = null · ไม่มีคีย์ projectId) · negative test ในตัว: ใส่บั๊กกลับเข้าซอร์สจริง 3 แบบแล้วต้องจับได้ทุกตัว
'use strict';
const fs = require('fs');
const path = require('path');
const vm = require('vm');

const ROOT = path.join(__dirname, '..');
const read = (p) => fs.readFileSync(path.join(ROOT, p), 'utf8');

function pageScript(html) {
  const blocks = [...html.matchAll(/<script>([\s\S]*?)<\/script>/g)].map(m => m[1]);
  const s = blocks.find(b => b.includes('const Page ='));
  if (!s) throw new Error('ไม่พบ const Page ใน approval.html');
  return s.replace(/\bconst Page =/, 'globalThis.Page =');
}

function dtoKeys(cs, name) {
  const i = cs.indexOf(`public record ${name}(`);
  const end = cs.indexOf(');', i);
  const body = cs.slice(i, end).split('\n').map(l => l.replace(/\/\/\/?.*$/, '')).join('\n');
  const out = new Set();
  const re = /\b(?:string|bool|int|decimal|Guid|DateTime|DocumentType|List<[^>]+>)\??\s+([A-Z]\w*)\s*(?:=|,|$)/gm;
  let m;
  while ((m = re.exec(body))) out.add(m[1][0].toLowerCase() + m[1].slice(1));
  return out;
}

function makeDom() {
  const els = new Map();
  const el = (id) => {
    if (!els.has(id)) {
      const e = { id, _value: undefined, innerHTML: '', textContent: '', style: {} };
      Object.defineProperty(e, 'value', {
        get() {
          if (this._value !== undefined) return this._value;
          const sel = /<option value="([^"]*)"[^>]*\sselected>/.exec(this.innerHTML);
          if (sel) return sel[1];
          const first = /<option value="([^"]*)"/.exec(this.innerHTML);
          return first ? first[1] : '';
        },
        set(v) { this._value = String(v); },
      });
      els.set(id, e);
    }
    return els.get(id);
  };
  const document = {
    getElementById: el,
    querySelectorAll(sel) {
      if (sel.includes('data-step-approver')) {
        const html = el('ruleStepList').innerHTML;
        return html.split('data-step-approver').slice(1).map(chunk => {
          const m = /<option value="([^"]*)" selected>/.exec(chunk.split('</select>')[0]);
          return { value: m ? m[1] : '', options: [] };
        });
      }
      return [];
    },
  };
  return { document, el, els };
}

async function runScenario(html, cs) {
  const fails = [];
  const { document, el } = makeDom();
  const sent = [];
  const api = {
    getMembers: async () => ({ data: [{ userId: 'u1', fullName: 'ผู้อนุมัติ', email: 'a@x' }] }),
    getProjects: async () => ({ data: { items: [{ id: 'p-1', code: 'P1', name: 'โครงการหนึ่ง' }] } }),
    updateApprovalRule: async (id, data) => { sent.push({ op: 'update', id, data }); },
    createApprovalRule: async (data) => { sent.push({ op: 'create', data }); },
    getApprovalRules: async () => ({ data: [] }),
  };
  const Layout = {
    init: () => false, api: () => api, esc: (s) => String(s ?? ''), toast() {}, openModal() {}, closeModal() {},
    docTypeOptions: (sel) => (sel ? `<option value="${sel}" selected>${sel}</option>` : ''),
    numOrNull: (id) => { const v = el(id).value; return v === '' || v == null ? null : Number(v); },
    docTypeLabel: (x) => x, money: (x) => String(x), date: (x) => String(x), statusBadge: (x) => String(x),
  };
  const ctx = { globalThis: {}, document, Layout, localStorage: { getItem: () => null }, confirm: () => true, console };
  ctx.globalThis = ctx;
  vm.createContext(ctx);
  vm.runInContext(pageScript(html), ctx);
  const Page = ctx.Page;
  const tick = () => new Promise(r => setTimeout(r, 0));
  const check = (cond, msg) => { if (!cond) fails.push(msg); };

  Page._rules = [{
    id: 'r1', name: 'กฎโครงการ', description: 'คำอธิบายเดิม', projectId: 'p-9', documentType: 'Invoice',
    minAmount: 0, maxAmount: null, isActive: true, steps: [{ stepOrder: 1, approverUserId: 'u1' }],
  }];

  // 1+2 — เปิดแก้แล้วบันทึกโดยไม่แตะ (p-9 ไม่อยู่ในรายการโครงการ)
  Page.editRule('r1'); for (let i = 0; i < 5; i++) await tick();
  await Page.saveRule();
  const s1 = sent.pop();
  check(s1 && s1.op === 'update', 'สัญญา 1: บันทึกตอนแก้ต้องเรียก updateApprovalRule');
  const d1 = s1 ? s1.data : {};
  check(d1.description === 'คำอธิบายเดิม', `สัญญา 1: description ต้องเป็นค่าเดิม (ได้ ${JSON.stringify(d1.description)})`);
  check('projectId' in d1, 'สัญญา 1: payload ต้องมีคีย์ projectId');
  check(d1.projectId === 'p-9', `สัญญา 2: projectId เดิมที่ไม่อยู่ในรายการต้องถูกส่งกลับ (ได้ ${JSON.stringify(d1.projectId)})`);
  check(!d1.clearProjectId, 'สัญญา 1: ไม่ได้แตะโครงการ ⇒ ห้ามส่ง clearProjectId');

  // 3 — เลือกทุกโครงการ
  Page.editRule('r1'); for (let i = 0; i < 5; i++) await tick();
  el('ruleProject').value = '';
  await Page.saveRule();
  const d3 = (sent.pop() || {}).data || {};
  check(d3.projectId == null && d3.clearProjectId === true, `สัญญา 3: เลือกทุกโครงการ ⇒ clearProjectId true (ได้ ${JSON.stringify(d3)})`);

  // 4a — ล้างคำอธิบายตอนแก้
  Page.editRule('r1'); for (let i = 0; i < 5; i++) await tick();
  el('ruleDescription').value = '';
  await Page.saveRule();
  const d4 = (sent.pop() || {}).data || {};
  check(d4.description === '', `สัญญา 4: ล้างคำอธิบายตอนแก้ ⇒ ส่ง "" (ได้ ${JSON.stringify(d4.description)})`);

  // 4b — ฟอร์มใหม่หลังเปิดแก้ ต้องไม่ค้างค่า
  Page.editRule('r1'); for (let i = 0; i < 5; i++) await tick();
  Page.openCreateRule(); for (let i = 0; i < 5; i++) await tick();
  el('ruleName').value = 'กฎใหม่';
  await Page._renderSteps([{ approverUserId: 'u1' }]);
  await Page.saveRule();
  const s5 = sent.pop() || {};
  const d5 = s5.data || {};
  check(s5.op === 'create', 'สัญญา 4: ฟอร์มใหม่ต้องเรียก createApprovalRule');
  check(d5.description == null && d5.projectId == null && !d5.clearProjectId,
    `สัญญา 4: ฟอร์มใหม่ห้ามค้างคำอธิบาย/โครงการของกฎก่อน (ได้ ${JSON.stringify(d5)})`);

  // 5 — คีย์ ⊆ DTO
  const keys = dtoKeys(cs, 'CreateApprovalRuleRequest');
  for (const d of [d1, d3, d4, d5])
    for (const k of Object.keys(d)) check(keys.has(k), `สัญญา 5: คีย์ ${k} ไม่มีใน CreateApprovalRuleRequest (เซิร์ฟเวอร์ทิ้งเงียบ)`);
  return fails;
}

(async () => {
  const html = read('Accounting/wwwroot/pages/approval.html');
  const cs = read('Accounting/Models/DTOs/Approval/ApprovalDtos.cs');
  const fails = await runScenario(html, cs);
  if (fails.length) {
    console.log('❌ approval_rule_form_sim:\n  - ' + fails.join('\n  - '));
    process.exit(1);
  }

  // negative test — ใส่บั๊กกลับเข้าไปในซอร์สจริง แล้วต้องถูกจับได้ทุกตัว
  const mutants = [
    ['ส่ง description: null เหมือนก่อนแก้', html.replace('description: id ? desc : (desc || null),', 'description: null,')],
    ['ตัดคีย์ projectId ออกจาก payload', html.replace(/\n\s*projectId,\n/, '\n')],
    ['ไม่ hydrate โครงการตอนเปิดแก้', html.replace("this._fillProjects(r.projectId || '');", "this._fillProjects('');")],
  ];
  let caught = 0;
  for (const [name, src] of mutants) {
    if (src === html) { console.log(`❌ negative test "${name}": หาจุดกลายพันธุ์ไม่เจอ (ซอร์สเปลี่ยนรูป — ปรับ sim)`); process.exit(1); }
    const f = await runScenario(src, cs);
    if (f.length) caught++;
    else console.log(`❌ negative test "${name}": sim ไม่จับ`);
  }
  if (caught !== mutants.length) process.exit(1);
  console.log(`✅ approval_rule_form_sim: สัญญา 5 ข้อผ่าน · negative test ${caught}/${mutants.length} ถูกจับ`);
})().catch(e => { console.log('❌ approval_rule_form_sim ล้ม: ' + (e && e.stack || e)); process.exit(1); });
