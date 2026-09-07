#!/usr/bin/env python3
"""ฟิลด์ข้อความที่ถูกใช้เป็น "ที่เก็บธงของด่าน" ถูกเขียนทับด้วย `=` แทนการต่อท้าย

═══ ที่มา (บั๊กจริง · ผลตรวจ OCR 2026-09-06 · แก้ในรอบ 146) ═══
`OcrScanResult.ProcessingNotes` เป็นที่สะสมของ **ทุกขั้นในไปป์ไลน์**
([Field Confidence] · [Buyer] · [Reasoning] · [Deposit-Buy]) และที่สำคัญที่สุด
เป็นที่เก็บธง **[VAT-CLAIM]** ซึ่งด่านบันทึก JE อ่านด้วย
`(result.ProcessingNotes ?? "").Contains("[VAT-CLAIM]")` เพื่อบล็อกการเคลม
ภาษีซื้อต้องห้าม §82/5

ตอนตั้งธง `IsDuplicate` โค้ดเขียน

    scanResult.ProcessingNotes = $"Possible duplicate: ...";   // ← `=` ไม่ใช่ `+=`

⇒ **ทับทั้งก้อน** ⇒ สแกนที่ถูกตีว่าซ้ำเสียธง [VAT-CLAIM] ไป ⇒ ใบภาษีซื้อ
ต้องห้ามที่บังเอิญซ้ำ **ผ่านด่าน §82/5 ไปได้** โดยไม่มี error ให้เห็นเลย

═══ ทำไมกติกาถึงแคบขนาดนี้ ═══
"ฟิลด์ชื่อ Notes ถูกเขียนทับ" อย่างเดียวฟ้องผิดเพียบ — `StockCountLine.Notes`
· `PdpaProcessingActivity.Notes` · `TaxReport.Notes` เป็นช่องที่ **ผู้ใช้กรอก
เอง** การเขียนทับคือพฤติกรรมที่ถูกต้อง (รันจริงได้ 6 จุด ซึ่งถูกทั้งหมด)
และการแยกว่า `doc.Notes` กับ `line.Notes` คนละตารางกันต้อง resolve type จริง
ซึ่งเป็นกำแพงเดิมที่ทำให้ checker สองตัวก่อนหน้าถูกทิ้ง (ดู CLAUDE.md:
`.HasValue` · `nullable_unwrap_check`)

จึงใช้ **คุณสมบัติของตัวฟิลด์เอง** เป็นตัวคัดแทนชนิด — ฟิลด์จะถูกเฝ้าก็ต่อเมื่อ
เป็นจริงทั้งสองข้อ:
  (1) ถูก**ต่อท้าย**ด้วยสำนวน `X.F = (Y.F ?? "") + …` อย่างน้อย MIN_APPENDS ครั้ง
      ⇒ พิสูจน์ว่ามันเป็น "ที่สะสม" ไม่ใช่ช่องค่าเดียว
  (2) ถูก**อ่านเป็นธง** ด้วย `.Contains("[…")` ที่ไหนสักแห่ง
      ⇒ พิสูจน์ว่ามีด่านที่พึ่งเนื้อหาในนั้นจริง — ธงที่หายไม่มี error ให้เห็น
ทั้งเรพตอนนี้มีฟิลด์เดียวที่ผ่านสองข้อนี้ (`ProcessingNotes`) ซึ่งตรงกับบั๊กจริง
พอดี · ถ้าวันหน้ามีฟิลด์ใหม่ถูกใช้แบบเดียวกัน มันจะถูกเฝ้าเองอัตโนมัติ

การเขียนทับที่ **อ้างถึงตัวเองใน RHS** (`x.F = x.F + …` · `x.F = Rebuild(x.F)`)
ไม่ถูกฟ้อง เพราะนั่นคือการต่อท้าย/ประกอบใหม่จากของเดิม ไม่ใช่การลบทิ้ง
"""
import os
import re
import sys

ROOT = os.path.dirname(os.path.dirname(os.path.abspath(__file__)))
SRC = os.path.join(ROOT, "Accounting")

MIN_APPENDS = 3

# X.F = (Y.F ?? "") + …   ← สำนวน "ต่อท้ายข้อความ" ของเรพนี้
APPEND = re.compile(r'(\w+)\.(\w+)\s*=\s*\(\s*[\w.]+\s*\?\?\s*""\s*\)\s*\+')
# …F …Contains("[…    ← อ่านเป็นธง
FLAG_READ = re.compile(r'\.(\w+)\s*(?:\?\?\s*""\s*\))?\s*\.?\s*\)?\s*\.Contains\("\[')
# X.F = <อะไรก็ได้ที่ไม่มี .F อยู่ใน RHS> ;
PLAIN = re.compile(r'^\s*(\w+)\.(\w+)\s*=\s*(?!.*\.\2\b)([^=].*?);\s*$')


def main() -> int:
    files = []
    for dirpath, dirnames, filenames in os.walk(SRC):
        dirnames[:] = [d for d in dirnames if d not in ("bin", "obj")]
        for fn in filenames:
            if fn.endswith(".cs"):
                files.append(os.path.join(dirpath, fn))

    texts = {}
    for path in files:
        with open(path, encoding="utf-8", errors="replace") as fh:
            texts[path] = fh.read()

    appended = {}
    flagged = set()
    for text in texts.values():
        for m in APPEND.finditer(text):
            appended[m.group(2)] = appended.get(m.group(2), 0) + 1
        for m in FLAG_READ.finditer(text):
            flagged.add(m.group(1))

    watched = {f for f, n in appended.items() if n >= MIN_APPENDS} & flagged

    problems = []
    for path, text in texts.items():
        for idx, line in enumerate(text.split("\n"), 1):
            m = PLAIN.match(line)
            if m and m.group(2) in watched:
                rel = os.path.relpath(path, ROOT)
                problems.append((rel, idx, m.group(1), m.group(2), line.strip()))

    for rel, ln, var, field, src in problems:
        print(f"{rel}:{ln}: `{var}.{field}` ถูกเขียนทับด้วย `=` "
              f"ทั้งที่เป็นที่สะสมและมีด่านอ่านธงจากมัน")
        print(f"    {src[:120]}")
        print(f"    → ต่อท้ายเสมอ: {var}.{field} = ({var}.{field} ?? \"\") + \"\\n…\";")

    print()
    label = ", ".join(sorted(watched)) or "(ไม่มี)"
    print(f"ตรวจ {len(files)} ไฟล์ .cs · ฟิลด์ที่เฝ้า: {label} · "
          f"การเขียนทับที่ลบธงของด่าน {len(problems)} จุด")
    return 1 if problems else 0


if __name__ == "__main__":
    sys.exit(main())
