#!/usr/bin/env python3
# -*- coding: utf-8 -*-
"""ตรวจคีย์ localStorage/sessionStorage ที่ "อ่าน แต่ไม่มีใครเขียน" ในเรพนี้

ที่มา (บั๊กจริง 3 จุด พบพร้อมกันรอบเดียว):
  • pages/etax.html อ่าน 'companyId' → null เสมอ → เด้งไป settings.html ทุกครั้ง
    ⇒ หน้า e-Tax Invoice เข้าไม่ได้เลยสักครั้งตั้งแต่เขียนมา
  • mobile-expense.html อ่านคีย์เดียวกัน ⇒ ขึ้น "ต้อง login + เลือกบริษัทก่อน"
    ตลอด ส่งเบิกไม่ได้
  • pages/signatures-logic.js อ่าน 'selectedCompanyId' ⇒ แท็บรออนุมัติว่าง และ
    ปุ่มอนุมัติ/ปฏิเสธ `return` เงียบ ๆ กดแล้วไม่มีอะไรเกิดขึ้น

ทั้งสามเป็น string ที่ถูกต้องทางไวยากรณ์ทุกประการ — `node --check` และ checker
ฝั่ง C# ทุกตัวมองไม่เห็น ต้องเทียบ "ฝั่งอ่าน" กับ "ฝั่งเขียน" ทั้งเรพเท่านั้น
(คีย์ของแอปหลักคือ 'currentCompany' ผ่าน Layout.getCompanyId())

ข้อจำกัดโดยตั้งใจ: ฝั่ง "อ่าน" ตรวจเฉพาะคีย์ที่เป็น string literal ตรง ๆ ส่วน
ฝั่ง "เขียน" นับ prefix ของคีย์ประกอบด้วย (`setItem('tour:' + pageKey, …)`
ครอบการอ่าน `'tour:quick-sale'`) — ไม่งั้นฟ้องผิดทุกคีย์ที่ตั้งชื่อแบบมี prefix
"""
import os
import re
import sys

ROOT = os.path.join(os.path.dirname(os.path.dirname(os.path.abspath(__file__))),
                    'Accounting', 'wwwroot')

# คีย์ที่ "ไม่มีใครเขียนในเรพ" ได้โดยชอบธรรม — ต้องมีเหตุผลกำกับเสมอ
ALLOW = {
    # (ยังไม่มี — ถ้าจะเพิ่ม ใส่เหตุผลว่าใครเป็นคนเขียนคีย์นี้)
}

READ = re.compile(r"""(?:local|session)Storage\.getItem\(\s*(['"])([A-Za-z0-9_.:-]+)\1\s*\)""")
WRITE = re.compile(r"""(?:local|session)Storage\.setItem\(\s*(['"])([A-Za-z0-9_.:-]+)\1\s*,""")
# `localStorage.key = v` / `localStorage['key'] = v` ก็นับเป็นการเขียน
WRITE_PROP = re.compile(r"""(?:local|session)Storage\[\s*(['"])([A-Za-z0-9_.:-]+)\1\s*\]\s*=""")
# เขียนด้วยคีย์ที่ประกอบจากตัวแปร แต่ขึ้นต้นด้วย literal — เช่น
# `setItem('tour:' + pageKey, '1')` ⇒ ครอบคลุมการอ่าน 'tour:quick-sale'
# (ไม่นับเป็น "ไม่มีใครเขียน" ไม่งั้นฟ้องผิดทุกคีย์ที่ตั้งชื่อแบบมี prefix)
WRITE_PREFIX = re.compile(r"""(?:local|session)Storage\.setItem\(\s*(['"])([A-Za-z0-9_.:-]*)\1\s*\+""")


BLOCK_COMMENT = re.compile(r"/\*.*?\*/", re.S)
# `//` ที่ไม่ได้ตามหลัง `:` (กัน https://) และไม่ใช่ `<!--`
LINE_COMMENT = re.compile(r"(?<!:)//[^\n]*")
HTML_COMMENT = re.compile(r"<!--.*?-->", re.S)


def strip_comments(text):
    """ตัดคอมเมนต์ก่อนสแกน — ไม่งั้น "หมายเหตุอธิบายบั๊กเก่า" ที่พิมพ์ชื่อคีย์
    เดิมไว้ จะถูกนับเป็นการอ่านจริง แล้ว checker ฟ้องตัวเอกสารของตัวเอง
    (เจอตอน negative test รอบแรก: คอมเมนต์ที่เขียนอธิบายบั๊ก 'companyId'
    ทำให้ checker ฟ้องทั้งที่โค้ดแก้ไปแล้ว)"""
    keep_lines = lambda m: '\n' * m.group(0).count('\n')
    text = HTML_COMMENT.sub(keep_lines, text)
    text = BLOCK_COMMENT.sub(keep_lines, text)
    return LINE_COMMENT.sub('', text)


def scope_of(rel_path):
    """แอปหลักกับ portal /connect เป็น **คนละ surface** (คนละการล็อกอิน คนละ
    หน้าจอ — ดู ACCOUNT_STRUCTURE.md) ⇒ คีย์ที่ /connect เขียนไว้ ไม่เคยมีอยู่
    ตอนผู้ใช้แอปหลักเปิดหน้า. เทียบ read/write แยก scope ไม่งั้นจะพลาดเคสจริง
    (`companyId` เขียนใน connect/ แต่ pages/etax.html อ่าน = null ตลอด)"""
    return 'connect' if rel_path.replace(os.sep, '/').startswith('connect/') else 'app'


def scan():
    reads = {}        # key -> [(file, line, scope)]
    writes = set()    # (scope, key) ที่มีการเขียนจริง
    prefixes = set()  # (scope, prefix) จากการเขียนด้วยคีย์ประกอบ ('tour:' + pageKey)
    for base, dirs, files in os.walk(ROOT):
        dirs[:] = [d for d in dirs if d not in ('lib', 'vendor', 'node_modules')]
        for fn in files:
            if not fn.endswith(('.js', '.html')):
                continue
            path = os.path.join(base, fn)
            try:
                text = open(path, encoding='utf-8', errors='replace').read()
            except OSError:
                continue
            rel = os.path.relpath(path, ROOT)
            sc = scope_of(rel)
            # สแกนจากฉบับตัดคอมเมนต์ (แทนคอมเมนต์ด้วยขึ้นบรรทัดเท่าเดิม
            # เลขบรรทัดที่ฟ้องจึงยังตรงกับไฟล์จริง)
            text = strip_comments(text)
            for m in WRITE.finditer(text):
                writes.add((sc, m.group(2)))
            for m in WRITE_PROP.finditer(text):
                writes.add((sc, m.group(2)))
            for m in WRITE_PREFIX.finditer(text):
                if m.group(2):
                    prefixes.add((sc, m.group(2)))
            for m in READ.finditer(text):
                key = m.group(2)
                line = text.count('\n', 0, m.start()) + 1  # บรรทัดตรงกับไฟล์จริง (strip ไม่ลบขึ้นบรรทัด)
                reads.setdefault(key, []).append((rel, line, sc))
    return reads, writes, prefixes


def main():
    reads, writes, prefixes = scan()
    problems = []
    for key, places in sorted(reads.items()):
        if key in ALLOW:
            continue
        # เขียนที่ไหนก็ได้ = ถือว่ามีคนเขียน ยกเว้นกรณีทิศทางเดียวที่อันตราย:
        # หน้าใน "แอปหลัก" อ่านคีย์ที่มีแต่ portal /connect เขียน — ผู้ใช้แอป
        # หลักส่วนใหญ่ไม่เคยเปิด /connect เลย คีย์จึงว่างตลอด (บั๊กจริงที่เจอ)
        # ทางกลับกัน connect อ่านคีย์ที่แอปหลักเขียนถือว่าปกติ (เข้ามาจากแอปหลัก)
        any_write = any(k == key for _, k in writes) \
            or any(key.startswith(pre) for _, pre in prefixes)
        app_write = ('app', key) in writes \
            or any(s == 'app' and key.startswith(pre) for s, pre in prefixes)
        bad = []
        for p in places:
            if not any_write:
                bad.append(p)
            elif p[2] == 'app' and not app_write:
                bad.append(p)
        if bad:
            problems.append((key, bad, any_write))

    for key, places, elsewhere in problems:
        why = ('ถูกเขียนเฉพาะใน portal /connect ซึ่งเป็นคนละ surface — '
               'ตอนผู้ใช้แอปหลักเปิดหน้า คีย์นี้ไม่มีค่า') if elsewhere \
              else 'ไม่มีที่ไหนเขียนเลยทั้งเรพ'
        print(f"❌ คีย์ '{key}' ถูกอ่าน {len(places)} จุด แต่{why}")
        for f, ln, _ in places:
            print(f"     {f}:{ln}")
    print()
    print(f"ตรวจ wwwroot · คีย์ที่อ่านได้ {len(reads)} ตัว · "
          f"อ่านแล้วไม่มีคนเขียนใน surface เดียวกัน {len(problems)} ตัว")
    return 1 if problems else 0


if __name__ == '__main__':
    sys.exit(main())
