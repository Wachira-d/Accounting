#!/usr/bin/env python3
# -*- coding: utf-8 -*-
"""ทุกจุดในหน้าเว็บที่บันทึก "คำตอบที่ผู้ใช้เลือก" ต้องประกาศ `source` ด้วย

ที่มา (บั๊กจริง · ผลตรวจรอบ 181 · DECISION_AUDIT §3 D7-3)
--------------------------------------------------------
รอบ 178 ตั้งด่านในคลังคำตอบที่เรียนทันที (`GenericFeedbackDistillationModel`) ว่า
จะเสิร์ฟคำตอบจาก `AiSuggestionMemory` ได้ก็ต่อเมื่อ `ExplicitAcceptCount >= 1` —
เหตุผลถูกต้อง: ผู้ใช้ที่กด "ยอมรับและอนุมัติต่อ" เป็นนิสัยสร้างคำยืนยันแบบ Implicit
ได้วันละหลายสิบแถวโดยไม่เคยมองค่าที่ระบบเติมให้ ⇒ ถ้านับรวม คลังจะเอียงตามนิสัยการกด

แต่ `/ai-feedback/record` รับ `source` เป็น **ฟิลด์ที่ไม่ส่งก็ได้** และค่าที่ไม่ส่ง =
`Implicit` (ค่าที่ปลอดภัยที่สุด — ถูกแล้ว) ⇒ หน้าเว็บ 6 จุดที่ผูกกับ event `change`
(= ผู้ใช้เปลี่ยนค่าเองกับมือ = Explicit จริง ๆ) ลืมส่ง `source` ⇒ `ExplicitAcceptCount`
ค้างที่ 0 ตลอดกาล ⇒ **คลังทันทีของ ManualJeAccount · ProductCategory ·
GlAccountSlot(recurring) · AssetCategory · BankStatementMatch · OcrFullReview
ตายเงียบทั้งหมด** — ไม่มี error, ไม่มี log, หน้าเว็บทำงานปกติทุกประการ
สิ่งที่ผู้ใช้สอนไม่เคยถูกหยิบมาใช้ตอบคำถามเดิม

ทำไมต้องเป็น checker ไม่ใช่แค่แก้ 6 จุดนั้น
-------------------------------------------
จุดที่บันทึก feedback มี 18 จุดใน 10 ไฟล์ และเพิ่มขึ้นทุกครั้งที่มี suggestion ใหม่.
"แก้ตัวเดียว เหลือที่เหลือ" คือ defect class ที่เรพนี้เจอซ้ำที่สุด และจุดที่เพิ่มใหม่
วันหลังจะไม่มีอะไรฟ้องเลย (JavaScript ที่ถูกไวยากรณ์ทุกประการ · เซิร์ฟเวอร์ตอบ 200 ·
ผลเสียโผล่อีกหลายเดือนถัดมาในรูป "ระบบไม่เคยฉลาดขึ้น")

กติกา
-----
1. object literal ใดที่มีทั้ง `feedbackId` และ `acceptedAi` (= body ของการบันทึก
   คำตอบ) **ต้อง** มีคีย์ `source` ด้วย
2. `api.aiFeedbackRecord(...)` ต้องส่งครบ **4 อาร์กิวเมนต์**
   (feedbackId, chosenAnswer, acceptedAi, source) — ดูลายเซ็นใน `js/api.js`

⚠️ checker ตัวนี้ตรวจแค่ว่า "**ประกาศ**ป้ายหรือยัง" ไม่ได้ตรวจว่าป้ายถูกไหม —
ป้ายที่ถูกคือป้ายที่ตรงความจริง: ยิงจาก event `change` = `'Explicit'` ·
ยิงตอนกดบันทึก/อนุมัติโดยไม่รู้ว่าผู้ใช้แตะไหม = `'Implicit'` ·
คลิกเดียวยืนยันหลายรายการ = `'BulkApprove'`. **ห้ามติด Explicit เหมาให้ผ่าน checker**
(ป้ายที่ทำให้ตัวเลขสวยแต่ไม่จริง ทำให้คลังเอียงกว่าเดิม — บทเรียนรอบ 178)

negative test (บังคับรันเมื่อแก้ checker ตัวนี้)
-----------------------------------------------
  python3 tools/ai_feedback_source_check.py --self-test
      → ใส่บั๊กกลับในหน่วยความจำ (snippet ที่ไม่มี source / เรียก 3 อาร์กิวเมนต์)
        แล้วต้องถูกจับได้ · snippet ที่ถูกต้องต้องไม่ถูกฟ้อง

  ทดสอบกับเรพจริง (ยืนยันว่าเดินไฟล์จริงถึง):
      sed -i "s/    source: 'Explicit',\\n//" ... # หรือแก้มือ: ลบบรรทัด
      grep -n "source: 'Explicit'," Accounting/wwwroot/pages/journals.html   # จำเลขบรรทัดไว้
      → ลบบรรทัดนั้นออก 1 บรรทัด แล้วรัน `python3 tools/ai_feedback_source_check.py`
      → ต้องได้ exit 1 พร้อมชื่อไฟล์ journals.html · แล้ว `git checkout` คืน
"""
import os
import re
import sys

ROOT = os.path.dirname(os.path.dirname(os.path.abspath(__file__)))
WWW = os.path.join(ROOT, 'Accounting', 'wwwroot')

# ไฟล์ที่เป็น "นิยามของกติกา" ไม่ใช่จุดเรียก — ตัวห่อใน api.js ส่ง source ต่อให้อยู่แล้ว
SKIP_REL = {os.path.join('js', 'api.js')}


def blank_strings_and_comments(t: str) -> str:
    """แทนเนื้อในสตริง/คอมเมนต์ด้วยช่องว่าง โดย**คงตำแหน่งและจำนวนบรรทัด**

    ต้องทำก่อนนับปีกกา ไม่งั้น `{` ใน template string (`${cid}`) หรือใน
    ข้อความไทยจะทำให้หา object literal ผิดก้อน"""
    out = list(t)
    i, n = 0, len(t)
    while i < n:
        c = t[i]
        if c in '"\'`':
            j = i + 1
            while j < n:
                if t[j] == '\\':
                    j += 2
                    continue
                if t[j] == c:
                    break
                j += 1
            for k in range(i + 1, min(j, n)):
                if out[k] != '\n':
                    out[k] = ' '
            i = j + 1
            continue
        if c == '/' and i + 1 < n and t[i + 1] == '/':
            j = t.find('\n', i)
            j = n if j < 0 else j
            for k in range(i, j):
                out[k] = ' '
            i = j
            continue
        if c == '/' and i + 1 < n and t[i + 1] == '*':
            j = t.find('*/', i + 2)
            j = n if j < 0 else j + 2
            for k in range(i, j):
                if out[k] != '\n':
                    out[k] = ' '
            i = j
            continue
        i += 1
    return ''.join(out)


SCRIPT_RE = re.compile(r'<script\b[^>]*>(.*?)</script>', re.S | re.I)


def scripts_only(t: str) -> str:
    """สำหรับ .html — คงเฉพาะเนื้อใน <script> (ส่วนอื่นเป็นช่องว่าง) เพื่อไม่ให้
    เครื่องหมาย ' ในข้อความ HTML ทำให้ตัวอ่านสตริงเพี้ยนทั้งไฟล์"""
    out = [' ' if ch != '\n' else '\n' for ch in t]
    for m in SCRIPT_RE.finditer(t):
        for k in range(m.start(1), m.end(1)):
            out[k] = t[k]
    return ''.join(out)


def enclosing_object(t: str, idx: int):
    """ก้อน {...} ที่ห่อ index นี้อยู่ — คืน (start, end) หรือ None"""
    depth, i = 0, idx
    while i >= 0:
        c = t[i]
        if c == '}':
            depth += 1
        elif c == '{':
            if depth == 0:
                break
            depth -= 1
        i -= 1
    if i < 0:
        return None
    depth, j = 0, i
    while j < len(t):
        if t[j] == '{':
            depth += 1
        elif t[j] == '}':
            depth -= 1
            if depth == 0:
                return (i, j + 1)
        j += 1
    return (i, len(t))


def top_level_arg_count(t: str, open_paren: int):
    """จำนวนอาร์กิวเมนต์ของ call ที่เปิดวงเล็บที่ open_paren (t = ข้อความที่ blank แล้ว)"""
    depth, args, seen = 0, 1, False
    j = open_paren
    while j < len(t):
        c = t[j]
        if c in '([{':
            depth += 1
        elif c in ')]}':
            depth -= 1
            if depth == 0:
                return args if seen else 0
        elif c == ',' and depth == 1:
            args += 1
        elif not c.isspace() and depth == 1:
            seen = True
        j += 1
    return args


KEY_FEEDBACK = re.compile(r'\bfeedbackId\b')
KEY_ACCEPTED = re.compile(r'\bacceptedAi\b')
KEY_SOURCE = re.compile(r'\bsource\s*:')
CALL_WRAPPER = re.compile(r'\baiFeedbackRecord\s*\(')


def scan_text(raw: str, is_html: bool):
    """คืน list ของ (line, reason)"""
    t = scripts_only(raw) if is_html else raw
    t = blank_strings_and_comments(t)
    problems = []

    # (1) object literal ที่เป็น body ของการบันทึกคำตอบ
    seen_blocks = set()
    for m in KEY_ACCEPTED.finditer(t):
        span = enclosing_object(t, m.start())
        if span is None or span in seen_blocks:
            continue
        seen_blocks.add(span)
        block = t[span[0]:span[1]]
        if not KEY_FEEDBACK.search(block):
            continue          # ไม่ใช่ body ของ /ai-feedback/record
        if KEY_SOURCE.search(block):
            continue
        problems.append((raw.count('\n', 0, m.start()) + 1,
                         'body ที่มี feedbackId + acceptedAi แต่ไม่ประกาศ source'))

    # (2) ตัวห่อ api.aiFeedbackRecord(feedbackId, chosenAnswer, acceptedAi, source)
    for m in CALL_WRAPPER.finditer(t):
        # นิยามของตัวห่อเอง (`aiFeedbackRecord: (a, b, c, d) =>`) ไม่ใช่จุดเรียก
        n_args = top_level_arg_count(t, m.end() - 1)
        if n_args < 4:
            problems.append((raw.count('\n', 0, m.start()) + 1,
                             f'aiFeedbackRecord() ส่ง {n_args} อาร์กิวเมนต์ '
                             '— ต้องมี source เป็นตัวที่ 4'))
    return problems


def run(root=WWW):
    bad = []
    for base, dirs, names in os.walk(root):
        dirs[:] = [d for d in dirs if d not in ('lib', 'node_modules')]
        for f in names:
            if not f.endswith(('.html', '.js')):
                continue
            full = os.path.join(base, f)
            rel = os.path.relpath(full, root)
            if rel in SKIP_REL:
                continue
            with open(full, encoding='utf-8', errors='ignore') as fh:
                raw = fh.read()
            if 'acceptedAi' not in raw and 'aiFeedbackRecord' not in raw:
                continue
            for line, why in scan_text(raw, f.endswith('.html')):
                bad.append((os.path.relpath(full, ROOT), line, why))
    return bad


BAD_SNIPPET = """
      sel.addEventListener('change', () => {
        fetch(`/api/companies/${cid}/ai-feedback/record`, {
          method: 'POST',
          body: JSON.stringify({ feedbackId: d.feedbackId, chosenAnswer: sel.value,
                                 acceptedAi: sel.value === d.accountId })
        });
      });
"""
BAD_WRAPPER = """
      jobs.push(api.aiFeedbackRecord(this._fid, chosen, chosen === aiSaid));
"""
GOOD_SNIPPET = """
      // source: 'Explicit' ในคอมเมนต์ไม่นับ — ต้องอยู่ในโค้ดจริง
      sel.addEventListener('change', () => {
        fetch(`/api/companies/${cid}/ai-feedback/record`, {
          method: 'POST',
          body: JSON.stringify({ feedbackId: d.feedbackId, chosenAnswer: sel.value,
                                 acceptedAi: sel.value === d.accountId, source: 'Explicit' })
        });
      });
      jobs.push(api.aiFeedbackRecord(this._fid, chosen, chosen === aiSaid, 'Implicit'));
"""


def self_test():
    fail = 0
    for name, text, want_hit in (('BAD_SNIPPET', BAD_SNIPPET, True),
                                 ('BAD_WRAPPER', BAD_WRAPPER, True),
                                 ('GOOD_SNIPPET', GOOD_SNIPPET, False)):
        hits = scan_text(text, is_html=False)
        if bool(hits) != want_hit:
            print(f'self-test ล้ม: {name} ควร{"ถูกฟ้อง" if want_hit else "ผ่าน"} '
                  f'แต่ได้ {hits}')
            fail = 1
    # คอมเมนต์ที่เขียนว่า source: ... ต้องไม่ช่วยให้ผ่าน (ครึ่ง "ด่านต้องฟ้องถูก")
    only_comment = BAD_SNIPPET.replace("method: 'POST',",
                                       "method: 'POST', // source: 'Explicit'")
    if not scan_text(only_comment, is_html=False):
        print('self-test ล้ม: source ที่อยู่ในคอมเมนต์ไม่ควรนับว่าประกาศแล้ว')
        fail = 1
    if not fail:
        print('self-test: ผ่าน')
    return fail


if __name__ == '__main__':
    if '--self-test' in sys.argv:
        sys.exit(self_test())
    problems = run()
    if problems:
        print('❌ จุดบันทึก feedback ที่ไม่ประกาศ UserChoiceSource '
              f'({len(problems)} จุด) — คลังจะนับเป็น Implicit '
              'แล้วด่าน ExplicitAcceptCount>=1 จะไม่มีวันเสิร์ฟคำตอบที่ผู้ใช้สอน:')
        for f, line, why in problems:
            print(f'   {f}:{line}: {why}')
        print("   → เติม source ให้ตรงความจริง: 'Explicit' (ผู้ใช้เปลี่ยนค่าเอง) · "
              "'BulkApprove' (คลิกเดียวหลายรายการ) · 'Implicit' (กดผ่านตอนบันทึก)")
        sys.exit(1)
    print('✅ ทุกจุดบันทึก feedback ประกาศ source แล้ว')
