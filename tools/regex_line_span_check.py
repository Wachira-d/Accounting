#!/usr/bin/env python3
# -*- coding: utf-8 -*-
"""regex ที่ใช้ \\s เป็น "ตัวคั่นระหว่างกลุ่มตัวเลข" — กลืนขึ้นบรรทัดใหม่เงียบ ๆ

═══ ที่มา: บั๊กจริง PI-20260820-0005 ═══
pattern หาเลขผู้เสียภาษี 13 หลักเขียนไว้ว่า

    (\\d{1}[-\\s]?\\d{4}[-\\s]?\\d{5}[-\\s]?\\d{2}[-\\s]?\\d{1})

`\\s` **ครอบ `\\n`** ⇒ ตัวเลขท้ายบรรทัดถูกต่อกับตัวเลขต้นบรรทัดถัดไปกลายเป็น
"เลข 13 หลัก" ที่ไม่มีใครพิมพ์ลงกระดาษ:

    "...สกรู 10  12.00   120.00\\n8859991966695 พุกพลาสติก..."
                          └────────┬────────┘
                            "0" + "\\n" + "885999196669"  →  0885999196669

ในใบกำกับร้านวัสดุที่มีทั้งบาร์โค้ดและยอดเงินทุกบรรทัด เลขปลอมพวกนี้เกิดขึ้น
เป็นสิบตัวต่อหน้า และผ่าน mod-11 ไทยได้ ~1 ใน 10 ⇒ กลายเป็น "เลขผู้เสียภาษี
ผู้ซื้อ" แล้วระบบเตือนว่า "อาจอัพโหลดผิดบริษัท" ทั้งที่กระดาษถูกต้องทุกอย่าง

pattern เดียวกันนี้ถูกคัดลอกไปวางไว้ **5 ไฟล์** ตอนเจอครั้งแรกจึงแก้ได้ที่เดียว
เหลืออีก 4 ที่พังเงียบ ๆ (defect class "แก้ตัวเดียว เหลือที่เหลือ" ใน CLAUDE.md)
— checker ตัวนี้คือกลไกที่กันไม่ให้เกิดตัวที่สาม

═══ กติกา ═══
ฟ้องเมื่อเจอ character class ที่มี `\\s` และ **ติดกับ atom ตัวเลข** (`\\d`,
`[0-9]`) โดยมี quantifier แบบ "ไม่บังคับ/สั้น" (`?`, `*`, `{0,n}`) — นั่นคือ
รูปแบบ "ตัวคั่น" ซึ่งไม่มีวันตั้งใจให้ข้ามบรรทัด

**ไม่**ฟ้อง `\\s*` / `\\s+` ที่อยู่ระหว่างคำ (เช่น `Account\\s*No\\.?`) เพราะ
การข้ามบรรทัดตรงนั้นมักตั้งใจจริง — จำกัดเฉพาะกรณีที่ทั้งสองข้างเป็นตัวเลข

ทางแก้: ใช้ `[- \\t]?` แทน `[-\\s]?` (หรือดึง pattern มาจากตัวกลาง เช่น
`Accounting.Helpers.ThaiTaxId.Pattern`)
"""
import os
import re
import sys

ROOT = os.path.dirname(os.path.dirname(os.path.abspath(__file__)))
SCAN_DIRS = ["Accounting", "Accounting.Tests"]

# atom ที่นับว่าเป็น "ตัวเลข" ในสายตา regex
DIGIT_ATOM = r"(?:\\d|\[0-9\])(?:\{\d+(?:,\d*)?\})?"
# character class ที่มี \s อยู่ข้างใน + quantifier แบบตัวคั่น
SEP_CLASS = r"\[[^\]\n]*\\s[^\]\n]*\](?:\?|\*|\{0,\d+\})"

# ตัวเลข → ตัวคั่น  หรือ  ตัวคั่น → ตัวเลข (พอตัวใดตัวหนึ่งก็ถือว่าเป็นตัวคั่นเลข)
PATTERNS = [
    re.compile(DIGIT_ATOM + r"\s*" + SEP_CLASS),
    re.compile(SEP_CLASS + r"\s*" + DIGIT_ATOM),
]

# บรรทัดที่จงใจยกเว้น เขียนคอมเมนต์ต่อท้ายว่า regex-line-span-ok
ALLOW_MARK = "regex-line-span-ok"


def scan_file(path):
    hits = []
    try:
        with open(path, encoding="utf-8") as f:
            lines = f.readlines()
    except (UnicodeDecodeError, OSError):
        return hits
    for i, line in enumerate(lines, 1):
        if ALLOW_MARK in line:
            continue
        for pat in PATTERNS:
            m = pat.search(line)
            if m:
                hits.append((i, line.rstrip(), m.group(0)))
                break
    return hits


def main():
    findings = []
    for d in SCAN_DIRS:
        base = os.path.join(ROOT, d)
        if not os.path.isdir(base):
            continue
        for dirpath, dirnames, filenames in os.walk(base):
            dirnames[:] = [x for x in dirnames if x not in ("bin", "obj", "node_modules")]
            for fn in filenames:
                if not fn.endswith(".cs"):
                    continue
                p = os.path.join(dirpath, fn)
                for line_no, text, frag in scan_file(p):
                    findings.append((os.path.relpath(p, ROOT), line_no, text, frag))

    if not findings:
        print("regex_line_span_check: OK — ไม่มี regex ที่ใช้ \\s เป็นตัวคั่นระหว่างตัวเลข")
        return 0

    print("regex_line_span_check: พบ regex ที่ตัวคั่นระหว่างตัวเลขครอบ \\n")
    print("  `\\s` ครอบ `\\n` ⇒ เลขท้ายบรรทัดถูกต่อกับเลขต้นบรรทัดถัดไป")
    print("  เป็นเลขที่ไม่มีอยู่จริงบนกระดาษ (บั๊กจริง PI-20260820-0005)")
    print("  แก้: ใช้ [- \\t]? แทน [-\\s]? · หรือดึงจากตัวกลาง ThaiTaxId.Pattern")
    print(f"  ยกเว้นรายบรรทัดด้วยคอมเมนต์ // {ALLOW_MARK} (ต้องอธิบายเหตุผล)\n")
    for path, line_no, text, frag in findings:
        print(f"  {path}:{line_no}")
        print(f"      {text.strip()[:150]}")
        print(f"      ↑ ตัวคั่นที่กลืนบรรทัด: {frag}")
    print(f"\nรวม {len(findings)} จุด")
    return 1


if __name__ == "__main__":
    sys.exit(main())
