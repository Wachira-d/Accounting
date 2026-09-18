#!/usr/bin/env python3
# -*- coding: utf-8 -*-
"""ตารางกำหนดยื่นแบบภาษีต้องมีที่เดียว — `Helpers/TaxFilingDeadline`

ที่มา (2026-09-16): เรพมีตารางกำหนดยื่น **3 ชุด** ที่ต่างคนต่างเขียน —
`StatutoryRemittanceService.DueDates` · `TaxCalendarService` ·
`TaxComplianceChecker.DeadlineFor` — และชุดที่สามเหมา ภ.พ.36 ไปรวมกับ ภ.พ.30
⇒ ได้วันที่ 23 แทนวันที่ 15 (§83/6 = ภายใน 7 วันนับแต่วันสิ้นเดือน + ขยาย
e-Filing 8 วัน) = เตือนช้ากว่ากำหนดจริง 8 วัน · อีกสองชุดก็ต่างกันเรื่องการ
เลื่อนวันหยุด (0 / เสาร์-อาทิตย์) และ ภ.ง.ด.54 หายไปจากปฏิทินทั้งแบบ

ทำไมเขียน checker ตัวนี้ได้ (ต่างจาก "ยอดถูกคำนวณสองที่" ที่เขียนไม่ได้):
กติกานี้เป็น **รูปทรงของโค้ดล้วน** — ไม่ต้อง resolve ชนิดตัวแปรเลย ลายเซ็นของ
"กำหนดยื่นตามกฎหมายไทย" คือ *วันที่คงที่ของเดือนถัดจากงวด* ซึ่งเขียนได้แค่
สองรูป: `new DateTime(<x>.Year, <x>.Month, <เลข>)` หรือ `<x>.AddDays(<เลข>)`
โดย `<x>` มาจากนิพจน์ที่มี `AddMonths(1)` ในเมธอดเดียวกัน

ฟ้องเมื่อเมธอดเข้าเงื่อนไข **ครบทั้งสามข้อ**:
  1. ชื่อเมธอดสื่อถึงกำหนดเวลา (Due… / Deadline… / …DueDate… / …Deadline…)
     และคืน DateTime / DateTime? / tuple ของ DateTime
  2. ข้างในมี "เดือนถัดจากงวด" (`AddMonths(1)`) **และ** วันที่คงที่จากตัวนั้น
  3. ไฟล์ไม่ใช่เจ้าของตารางกลาง (Helpers/TaxFilingDeadline.cs)

เงื่อนไขข้อ 2 คือตัวที่กันการฟ้องผิด: เมธอดคำนวณวันอื่น ๆ ในเรพ
(`CalculateNextRunDate` ของ recurring · `IsDue` ที่คืน bool · `BalanceDue`/
`AmountDue` ที่คืน decimal) ตกเกณฑ์ไปเองโดยไม่ต้องมี allow-list

⚠️ เส้นทางที่สอง (เพิ่ม 2026-09-18) — **จับจากรูปทรงบอดี้โดยไม่พึ่งชื่อเมธอด**:
รุ่นแรกล่ามตัวเองไว้กับ `NAME_RE` ⇒ `ComplianceService.InitializeFilingCalendarAsync`
(คืน `Task` ชื่อไม่มีคำว่า Due/Deadline) ถือตารางกำหนดยื่น**ชุดที่ 4** ทั้งชุด —
`AddMonths(1).AddDays(14)` · `AddDays(6)` · ไม่เลื่อนวันหยุด · ไม่มี e-Filing —
แล้ว checker รายงาน "0 จุด" มาตลอด = ด่านที่รายงานเขียวทั้งที่มีบั๊กอยู่ตรงหน้า
(บทเรียน CLAUDE.md: "checker ที่เป็น allow-list ต้องถามว่าไฟล์ที่เพิ่งแตะอยู่ในลิสต์ไหม"
— ที่นี่ allow-list คือ *ชื่อเมธอด*). ลายเซ็นที่แข็งกว่าชื่อคือ **บอดี้เขียนค่าลง
`DueDate`/`EFilingDueDate` หรือเอ่ยชื่อแบบยื่น** ร่วมกับ "เดือนถัดจากงวด + วันคงที่"
— ตัวคำนวณวันอื่น ๆ (recurring · ครบกำหนดชำระ) ไม่เอ่ยคำเหล่านั้น จึงไม่ฟ้องผิด
"""
import re
import sys
from pathlib import Path

ROOT = Path(__file__).resolve().parent.parent
SRC = ROOT / "Accounting"
OWNER = "TaxFilingDeadline.cs"          # เจ้าของตารางกลาง — ตัวเดียว ไม่ใช่ allow-list ที่โตได้

# ชื่อเมธอดที่สื่อถึง "กำหนดเวลา"
NAME_RE = re.compile(r"^(Due\w*|Deadline\w*|\w*DueDates?|\w*Deadlines?|\w*DueDate\w*|\w*DeadlineFor\w*)$")
# คืนค่าเป็นวันที่ (เดี่ยว/nullable/tuple)
RET_RE = re.compile(r"(DateTime\??|\(\s*DateTime[^)]*\))\s*$")

HDR_RE = re.compile(
    r"^[ \t]*(?:public|private|internal|protected)[ \t]+(?:static[ \t]+)?"
    r"(?P<ret>[\w<>,.\[\]? \t()]+?)[ \t]+(?P<name>\w+)[ \t]*\(",
    re.M)

NEXT_MONTH_RE = re.compile(r"AddMonths\s*\(\s*1\s*\)")
# ⚠️ อาร์กิวเมนต์ตัวที่สามต้องรับ **ตัวแปร** ด้วย ไม่ใช่เฉพาะเลขคงที่ —
# รูปจริงที่เคยอยู่ในเรพห่อไว้ใน local function: `DateTime D(int day) =>
# new DateTime(next.Year, next.Month, day);` ⇒ รุ่นแรกที่บังคับ `\d+` **จับไม่ได้**
# ทั้งที่เป็นบั๊กที่ checker นี้เขียนมาเพื่อจับโดยตรง (negative test จับได้)
# ⚠️ วันที่ 1 ไม่ใช่กำหนดยื่น — `new DateTime(x.Year, x.Month, 1)` คือ "ต้นงวด/
# ขอบเดือน" ที่รายงาน aging/ภ.พ.30 ใช้กันทั่วเรพ ⇒ ต้องคัดออก ไม่งั้นฟ้องผิด
# (รุ่นแรกของเส้นทางที่สองฟ้อง GenerateVatReport กับ ArApAnalysisService ด้วยสาเหตุนี้)
FIXED_DAY_RE = [
    re.compile(r"new\s+DateTime\s*\(\s*\w+\.Year\s*,\s*\w+\.Month\s*,\s*(?!1\s*\))[\w.]+\s*\)"),
    # `AddDays(0|1)` คือคณิตขอบงวด (สิ้นเดือน ±1) — กำหนดยื่นไทยเลื่อนจากต้นเดือน
    # ถัดไป 6/14/22 วัน หรือ +8 e-Filing ไม่มี 0/1 ⇒ ตัดออกกันฟ้องผิด
    re.compile(r"\bAddDays\s*\(\s*(?![01]\s*\))\d+\s*\)"),
    # `new DateTime(year, month, 1).AddMonths(1).AddDays(14)` — รูปที่ ComplianceService ใช้
    re.compile(r"new\s+DateTime\s*\(\s*\w+\s*,\s*\w+\s*,\s*\d+\s*\)\s*\.AddMonths\s*\(\s*1\s*\)"),
]
# ลายเซ็นว่าบอดี้นี้กำลัง "ตั้งกำหนดยื่นแบบราชการ" — ใช้แทนชื่อเมธอด
FILING_MARKER_RE = re.compile(
    # `DueDate =` คือ "กำหนดค่า" — ต้องไม่จับ `DueDate ==` (เปรียบเทียบใน aging)
    r"\bE?FilingDueDate\b|\bDueDate\s*=(?!=)|\bFormCode\b|\bFilingType\b"
    r"|ภ\.พ\.|ภ\.ง\.ด\.|สปส\.|\bPP30\b|\bPND\d|\bSPS\d|\bSSO1-10\b")


def method_bodies(text):
    """คืน (ชื่อ, return type, บอดี้, เลขบรรทัดของหัวเมธอด) — จับปีกกาแบบนับคู่"""
    for m in HDR_RE.finditer(text):
        # หา ')' ที่ปิดวงเล็บพารามิเตอร์ แล้วตามด้วย '{'
        i, depth = m.end() - 1, 0
        while i < len(text):
            if text[i] == '(':
                depth += 1
            elif text[i] == ')':
                depth -= 1
                if depth == 0:
                    break
            i += 1
        j = i + 1
        while j < len(text) and text[j] in " \t\r\n":
            j += 1
        if j < len(text) - 1 and text[j] == '=' and text[j + 1] == '>':
            # expression-bodied — บอดี้คือทุกอย่างจนถึง ';' ตัวแรกที่อยู่นอกวงเล็บ
            k, d = j + 2, 0
            while k < len(text):
                c = text[k]
                if c in "([{":
                    d += 1
                elif c in ")]}":
                    d -= 1
                elif c == ';' and d <= 0:
                    break
                k += 1
            yield (m.group("name"), m.group("ret").strip(), text[j:k],
                   text.count("\n", 0, m.start()) + 1)
            continue
        if j >= len(text) or text[j] != '{':
            continue                       # ลายเซ็นอย่างเดียว (interface/abstract)
        depth, k = 0, j
        while k < len(text):
            if text[k] == '{':
                depth += 1
            elif text[k] == '}':
                depth -= 1
                if depth == 0:
                    break
            k += 1
        yield (m.group("name"), m.group("ret").strip(),
               text[j:k + 1], text.count("\n", 0, m.start()) + 1)


_COMMENT_RE = re.compile(r"//[^\n]*|/\*.*?\*/", re.S)


def strip_comments(text):
    """ลบคอมเมนต์ C# แต่ **คงจำนวนบรรทัด** (แทนด้วยขึ้นบรรทัดใหม่เท่าเดิม)
    ไม่งั้นเลขบรรทัดที่ฟ้องเพี้ยนทั้งไฟล์ — และถ้าไม่ตัด หมายเหตุที่อธิบายบั๊กเก่า
    ("เดิมเขียน AddMonths(1).AddDays(14)") จะถูกนับเป็นโค้ดจริง ⇒ checker ฟ้อง
    เอกสารของตัวเอง (บทเรียนซ้ำของ localstorage_key_check / css_var_check)
    ⚠️ ไม่ tokenize สตริง — `//` ใน URL ในสตริงจะกินท้ายบรรทัด ซึ่งยอมรับได้ที่นี่
    เพราะสิ่งที่ค้นไม่อยู่ในสตริง และการกินเกินทำให้ฟ้อง**น้อยลง** ไม่ใช่มากขึ้น"""
    return _COMMENT_RE.sub(lambda m: "\n" * m.group(0).count("\n"), text)


def scan_file(path):
    text = strip_comments(path.read_text(encoding="utf-8", errors="replace"))
    hits = []
    for name, ret, body, line in method_bodies(text):
        if not NEXT_MONTH_RE.search(body):
            continue
        if not any(r.search(body) for r in FIXED_DAY_RE):
            continue
        # เส้นทางที่ 1: ชื่อ+ชนิดคืนค่าสื่อถึงกำหนดเวลา (รูปเดิม)
        by_name = NAME_RE.match(name) and RET_RE.search(ret)
        # เส้นทางที่ 2: บอดี้ประกาศตัวเองว่าเป็นตารางกำหนดยื่น (ไม่พึ่งชื่อ)
        by_body = FILING_MARKER_RE.search(body)
        if not (by_name or by_body):
            continue
        hits.append((line, name, ret))
    return hits


def main():
    argv = [a for a in sys.argv[1:] if not a.startswith("-")]
    targets = [Path(a) for a in argv] if argv else sorted(SRC.rglob("*.cs"))
    found = 0
    for f in targets:
        if not f.exists() or "/obj/" in str(f) or "/bin/" in str(f):
            continue
        if f.name == OWNER:
            continue
        for line, name, ret in scan_file(f):
            found += 1
            rel = f.relative_to(ROOT) if str(f).startswith(str(ROOT)) else f
            print(f"{rel}:{line}: เมธอด `{name}` คืน {ret} และคำนวณวันที่คงที่ของ"
                  f" 'เดือนถัดจากงวด' เอง\n"
                  f"    → ตารางกำหนดยื่นต้องมีที่เดียว: Helpers/TaxFilingDeadline.For()"
                  f" (เดิมมี 3 ชุด และชุดหนึ่งให้ ภ.พ.36 = วันที่ 23 แทน 15)")
    print(f"\nตรวจ {len([t for t in targets if t.exists()])} ไฟล์ ·"
          f" ตารางกำหนดยื่นที่เขียนซ้ำ {found} จุด")
    return 1 if found else 0


if __name__ == "__main__":
    sys.exit(main())
