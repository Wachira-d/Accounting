#!/usr/bin/env python3
# -*- coding: utf-8 -*-
"""`Math.Round` ที่ไม่ระบุ `MidpointRounding` ในไฟล์ที่ผลิตบรรทัดเอกสารจากสแกน OCR — scope แคบ (รอบ 200 ทีม K2 · ผลตรวจรอบ 189 C-10)

ที่มา: กฎเหล็ก #4 E บังคับ `MidpointRounding.AwayFromZero` เสมอ (ค่าเริ่มต้นของ .NET = banker's rounding) · ในเมธอดเดียวกันเคยแก้ยอด WHT
**หัวใบ** แล้ว (T5-N6) แต่ตัวกระจาย WHT **รายบรรทัด** ห่างไป 650 บรรทัดในไฟล์เดียวกันยังไม่ได้แก้ ⇒ 50 ทวิ รายประเภทเงินได้ต่างจากคำนวณมือ
0.01 บาทในเคส midpoint ("แก้ตัวเดียว เหลือที่เหลือ") · ทั้งเรพมี ~266 จุด — ไม่กวาดทั้งเรพ (F4 ข้อ 3: scope แคบ) · ล็อกเฉพาะไฟล์ในลิสต์

negative test รันทุกครั้ง: ถอด `, MidpointRounding.AwayFromZero` ออกจากการเรียกแรกในไฟล์จริงแล้วต้องฟ้อง · รูปที่ถูก (หลายบรรทัด ·
วงเล็บซ้อน) ต้องไม่ฟ้อง
"""
import re
import sys
from pathlib import Path

ROOT = Path(__file__).resolve().parent.parent
FILES = [
    "Accounting/Services/Implementations/OcrService.cs",
    "Accounting/Services/Implementations/Ocr/SmartFieldExtractor.cs",
]


def strip_comments(src: str) -> str:
    src = re.sub(r"/\*.*?\*/", lambda m: re.sub(r"[^\n]", " ", m.group(0)), src, flags=re.S)
    return re.sub(r"(?<![:\"])//[^\n]*", "", src)


def violations(src: str):
    code = strip_comments(src)
    out = []
    for m in re.finditer(r"\bMath\.Round\(", code):
        i, depth = m.end(), 1
        while depth and i < len(code):
            c = code[i]
            if c == "(":
                depth += 1
            elif c == ")":
                depth -= 1
            i += 1
        call = code[m.start():i]
        if "MidpointRounding" not in call:
            out.append((code.count("\n", 0, m.start()) + 1, " ".join(call.split())[:120]))
    return out


def self_test() -> bool:
    ok = True
    good = "var a = Math.Round(x * (y + 1m), 2,\n    MidpointRounding.AwayFromZero);\n// Math.Round(z, 2) ในคอมเมนต์ไม่นับ\n"
    if violations(good):
        print("self-test ล้ม: รูปที่ถูก (หลายบรรทัด/วงเล็บซ้อน/คอมเมนต์) ถูกฟ้อง")
        ok = False
    real = (ROOT / FILES[0]).read_text(encoding="utf-8")
    mutated = real.replace(", 2, MidpointRounding.AwayFromZero)", ", 2)", 1)
    if mutated == real or len(violations(mutated)) <= len(violations(real)):
        print("self-test ล้ม: ถอด MidpointRounding ออกจากไฟล์จริงแล้วไม่ถูกฟ้อง")
        ok = False
    return ok


def main() -> int:
    if not self_test():
        return 2
    bad = 0
    for rel in FILES:
        p = ROOT / rel
        if not p.exists():
            print(f"ไม่พบไฟล์ {rel} (ย้าย/เปลี่ยนชื่อ? แก้ลิสต์ FILES)")
            return 2
        for line, call in violations(p.read_text(encoding="utf-8")):
            print(f"{rel}:{line}: Math.Round ไม่ระบุ MidpointRounding — {call}")
            bad += 1
    if bad:
        print(f"\n❌ {bad} จุด — เติม `, MidpointRounding.AwayFromZero` (กฎเหล็ก #4 E)")
        return 1
    print(f"✅ ocr_round_midpoint_check: {len(FILES)} ไฟล์ ไม่มี Math.Round ที่ขาด MidpointRounding (+ negative test ผ่าน)")
    return 0


if __name__ == "__main__":
    sys.exit(main())
