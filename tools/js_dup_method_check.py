#!/usr/bin/env python3
# -*- coding: utf-8 -*-
"""ตรวจ "method/key ชื่อซ้ำใน object literal เดียวกัน" ในไฟล์ JS/HTML ของ wwwroot

ที่มา (บั๊กจริง): documents.html มี `toggleVatClaim(el)` (ไอคอนเคลม VAT บน
บรรทัดในฟอร์ม) และ `async toggleVatClaim(id, claim, el)` (ติ๊กเคลมหลังอนุมัติ
ในหน้า detail) อยู่ใน `const Page = {...}` เดียวกัน — JavaScript อนุญาต key
ซ้ำใน object literal โดย **ตัวหลังทับตัวแรกเงียบ ๆ** ⇒ กดไอคอนบนฟอร์มไปเรียก
เวอร์ชันหลังด้วย claim=undefined → เด้ง confirm "เลิกเคลมภาษีซื้อใบนี้?"
กลางฟอร์ม + ยิง API ด้วย DOM element แทน id. `node --check` มองไม่เห็น
(ไวยากรณ์ถูกทุกประการ) ต้องไล่นับ key ต่อ object เอง

Heuristic (ตามสไตล์โค้ดในเรพ — ทุกหน้าใช้ pattern เดียวกัน):
  • หา `const <Name> = {` / `window.<Name> = {` ที่ต้น statement
  • นับ brace depth จริงทีละบรรทัด (ตัด string/template ก่อนนับ) — เก็บ key
    เฉพาะที่ depth == 1 ของ object นั้น (key ชั้นแรกเท่านั้น) จนกว่า depth = 0
  • key = `name(...)` (รวม async) หรือ `name: ...` — get/set ข้าม
  • ชื่อซ้ำใน object เดียวกัน = ฟ้อง
บทเรียนจาก negative test รุ่นแรก: เทียบด้วย indent อย่างเดียวฟ้องผิดใน object
ซ้อน (translations.js ใช้ indent ชั้นในเท่าชั้นนอก) — ต้องนับ depth จริงเท่านั้น
"""
import os
import re
import sys

ROOT = os.path.join(os.path.dirname(os.path.dirname(os.path.abspath(__file__))),
                    'Accounting', 'wwwroot')

OBJ_OPEN = re.compile(r'^(\s*)(?:const|let|var)\s+([A-Za-z_$][\w$]*)\s*=\s*\{\s*$|'
                      r'^(\s*)window\.([A-Za-z_$][\w$]*)\s*=\s*\{\s*$')
KEY = re.compile(r'^(\s*)(?:async\s+)?([A-Za-z_$][\w$]*)\s*(?:\(|:)')
SKIP_NAMES = {'get', 'set', 'if', 'for', 'while', 'switch', 'return', 'function', 'catch'}




def blank_strings(src):
    """แทนเนื้อ string/template/คอมเมนต์/regex ด้วยช่องว่าง (คงจำนวนบรรทัด) —
    เหลือแต่ "โค้ดจริง" ให้นับ brace depth ได้แม่น. ต้องเป็น tokenizer ตัวอักษร
    จริง ไม่ใช่ regex ต่อบรรทัด เพราะ template literal ของเรพนี้ยาวข้ามบรรทัด
    (innerHTML = `...${code}...`) — regex ต่อบรรทัดทำ depth เพี้ยนจนพลาดของจริง.
    `${...}` ใน template คือโค้ดจริง — คงไว้ให้ brace หักล้างกันเอง.
    **regex literal ต้อง parse ด้วย** — เจอจริง: `replace(/\'/g, ...)` มี quote
    ในตัว regex ถ้าไม่รู้จักจะเปิด string ค้างแล้วกลืนโค้ดที่เหลือทั้งไฟล์
    (ตัดสิน regex-vs-หาร จาก token ก่อนหน้าแบบเดียวกับ minifier ทั่วไป)"""
    out = []
    mode = ['code']            # code | sq | dq | tmpl | lc | bc
    interp = []                # brace depth ของแต่ละชั้น ${...}
    tail = ''                  # โค้ดล่าสุดใน code mode (ใช้ตัดสิน regex literal)
    i, n = 0, len(src)

    def prev_token_allows_regex():
        s = tail.rstrip()
        if not s:
            return True
        ch = s[-1]
        if ch in '(,=:[!&|?{};+-*%~^<>':
            return True
        m = re.search(r'([A-Za-z_$][\w$]*)$', s)
        return bool(m and m.group(1) in (
            'return', 'typeof', 'case', 'in', 'of', 'new', 'do',
            'else', 'void', 'delete', 'instanceof'))

    while i < n:
        c = src[i]
        nxt = src[i + 1] if i + 1 < n else ''
        cur = mode[-1]
        if cur == 'code':
            if c == '/' and nxt == '/':
                mode.append('lc'); out.append('  '); i += 2; continue
            if c == '/' and nxt == '*':
                mode.append('bc'); out.append('  '); i += 2; continue
            if c == '/' and prev_token_allows_regex():
                # regex literal — กลืนจน / ปิด ( [...] ข้างในไม่จบด้วย / )
                j = i + 1
                in_class = False
                while j < n:
                    cc = src[j]
                    if cc == '\\':
                        j += 2; continue
                    if cc == '\n':
                        break                     # regex ไม่ข้ามบรรทัด — self-heal
                    if in_class:
                        if cc == ']':
                            in_class = False
                    elif cc == '[':
                        in_class = True
                    elif cc == '/':
                        break
                    j += 1
                out.append('/'); out.append(' ' * max(0, j - i - 1))
                if j < n and src[j] == '/':
                    out.append('/'); j += 1
                    while j < n and src[j].isalpha():
                        out.append(' '); j += 1
                tail += '/x/'
                i = j
                continue
            if c == "'":
                mode.append('sq')
            elif c == '"':
                mode.append('dq')
            elif c == '`':
                mode.append('tmpl')
            elif c == '}' and interp and interp[-1] == 0:
                interp.pop(); mode.pop()          # ปิด ${...} → กลับเข้า template
                out.append(c); i += 1; tail = ''
                continue
            elif c == '{' and interp:
                interp[-1] += 1
            elif c == '}' and interp:
                interp[-1] -= 1
            out.append(c)
            tail = (tail + c)[-24:]
            if c == '\n':
                tail = ''
        elif cur in ('sq', 'dq'):
            if c == '\\':
                out.append('  '); i += 2; continue
            if (cur == 'sq' and c == "'") or (cur == 'dq' and c == '"') or c == '\n':
                mode.pop(); out.append(c)
                tail += "'x'"
            else:
                out.append('\n' if c == '\n' else ' ')
        elif cur == 'tmpl':
            if c == '\\':
                out.append('  '); i += 2; continue
            if c == '`':
                mode.pop(); out.append(c)
                tail += '`x`'
            elif c == '$' and nxt == '{':
                interp.append(0); mode.append('code')
                out.append('${'); i += 2; tail = ''
                continue
            else:
                out.append('\n' if c == '\n' else ' ')
        elif cur == 'lc':
            if c == '\n':
                mode.pop(); out.append(c)
            else:
                out.append(' ')
        elif cur == 'bc':
            if c == '*' and nxt == '/':
                mode.pop(); out.append('  '); i += 2; continue
            out.append('\n' if c == '\n' else ' ')
        i += 1
    return ''.join(out)


def scan_script(lines, rel, problems):
    i = 0
    n = len(lines)
    while i < n:
        m = OBJ_OPEN.match(lines[i])
        if not m:
            i += 1
            continue
        obj_name = m.group(2) or m.group(4)
        depth = 1
        seen = {}
        j = i + 1
        while j < n and depth > 0:
            if depth == 1:
                km = KEY.match(lines[j])
                if km and km.group(2) not in SKIP_NAMES:
                    name = km.group(2)
                    if name in seen:
                        problems.append((rel, seen[name] + 1, j + 1, obj_name, name))
                    else:
                        seen[name] = j
            depth += lines[j].count('{') - lines[j].count('}')
            j += 1
        i = j


def main():
    problems = []
    for base, dirs, files in os.walk(ROOT):
        dirs[:] = [d for d in dirs if d not in ('lib', 'vendor', 'node_modules')]
        for fn in files:
            if not fn.endswith(('.js', '.html')):
                continue
            path = os.path.join(base, fn)
            rel = os.path.relpath(path, ROOT)
            try:
                text = open(path, encoding='utf-8', errors='replace').read()
            except OSError:
                continue
            blocks = ([text] if fn.endswith('.js')
                      else re.findall(r'<script(?![^>]*\bsrc=)[^>]*>(.*?)</script>', text, re.S))
            for blk in blocks:
                # เลขบรรทัดต้องอ้างไฟล์จริง — หา offset ของ block ใน text
                off = text.find(blk)
                start_line = text.count('\n', 0, off) if off >= 0 else 0
                cleaned = blank_strings(blk)
                lines = cleaned.split('\n')
                before = len(problems)
                scan_script(lines, rel, problems)
                for k in range(before, len(problems)):
                    r, a, b, o, nm = problems[k]
                    problems[k] = (r, a + start_line, b + start_line, o, nm)

    for rel, first, second, obj, name in problems:
        print(f"❌ {rel}: `{name}` ซ้ำใน object `{obj}` — บรรทัด {first} ถูกทับโดยบรรทัด {second} เงียบ ๆ")
    print()
    print(f"ตรวจ wwwroot · method/key ซ้ำใน object เดียวกัน {len(problems)} จุด")
    return 1 if problems else 0


if __name__ == '__main__':
    sys.exit(main())
