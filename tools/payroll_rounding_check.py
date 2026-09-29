#!/usr/bin/env python3
# -*- coding: utf-8 -*-
"""Math.Round ในโมดูลเงินเดือน/ประกันสังคม ต้องระบุ MidpointRounding — รอบ 200 ทีม RF (R200-X7)

═══ ที่มา ═══
ทีม R ปิด D-08 (เพดานสมทบ ปกส.) แล้วเขียนว่าเป็น "จุดสุดท้ายที่ยังเป็น banker's" — ฝ่ายค้านพบว่ายังเหลือ
(PayrollService expectedNet · อัตราภาษีบน 50 ทวิ · SsoRateSchedule.GetMaxContribution) และทีม RF กวาดเจออีก
รวม 11 จุด (controller ค่าตั้งอัตรา ปกส. · ไฟล์ Excel สปส.) ⇒ "จุดสุดท้าย" ที่ยืนยันด้วยตาไม่ใช่ด่าน
(CLAUDE.md กฎเหล็ก #4 E: `Math.Round` ต้องระบุ `MidpointRounding.AwayFromZero` เสมอ — default = banker's)

═══ กติกา ═══
ทุก `Math.Round(` ในไฟล์ของโมดูลเงินเดือน (FILES) ต้องมีคำ `MidpointRounding` อยู่ในวงเล็บของการเรียกนั้น
(นับวงเล็บ ข้ามบรรทัดได้) · คอมเมนต์ไม่นับ · ขอบเขตแคบรายไฟล์ (F4 ข้อ 3 — ไม่กวาดทั้งเรพ)
negative test ในตัวรันทุกครั้ง: โค้ดที่ลืม midpoint ต้องถูกฟ้อง · โค้ดที่ระบุแล้ว (รวมข้ามบรรทัด/ในคอมเมนต์) ต้องไม่ถูกฟ้อง
"""
import glob
import os
import re
import sys

ROOT = os.path.dirname(os.path.dirname(os.path.abspath(__file__)))
SRC = os.path.join(ROOT, "Accounting")

FILES = [
    "Services/Implementations/PayrollService*.cs",
    "Services/Implementations/Payroll/**/*.cs",
    "Controllers/PayrollController.cs",
    "Helpers/Sso*.cs",
    "Helpers/Payroll*.cs",
    "Helpers/ThaiPit*.cs",
    "Helpers/WorkersCompensation*.cs",
    "Helpers/Tip*.cs",
    "Services/Implementations/TaxFilingExportService.cs",
]


def strip_comments(text):
    """แทนคอมเมนต์ด้วยช่องว่างยาวเท่าเดิม (คงเลขบรรทัด/ตำแหน่ง)"""
    def blank(m):
        return re.sub(r"[^\n]", " ", m.group(0))
    text = re.sub(r"/\*.*?\*/", blank, text, flags=re.S)
    return re.sub(r"(?<![:\"])//[^\n]*", blank, text)


def violations(text):
    t = strip_comments(text)
    out = []
    for m in re.finditer(r"Math\.Round\s*\(", t):
        i, depth = m.end(), 1
        while depth and i < len(t):
            if t[i] == "(":
                depth += 1
            elif t[i] == ")":
                depth -= 1
            i += 1
        if "MidpointRounding" not in t[m.start():i]:
            out.append((t.count("\n", 0, m.start()) + 1, " ".join(t[m.start():i].split())[:100]))
    return out


def self_test():
    bad = "var x = Math.Round(a * b, 2);\n"
    good = ("var y = Math.Round(a\n    - b, 2, MidpointRounding.AwayFromZero);\n"
            "// Math.Round(x, 2) ในคอมเมนต์ไม่นับ\n")
    ok = len(violations(bad)) == 1 and len(violations(good)) == 0
    if not ok:
        print("❌ payroll_rounding_check: negative test ล้ม (ตัวตรวจจับ/ปล่อยผิด)")
    return ok


def main():
    if not self_test():
        return 1
    files = set()
    for pat in FILES:
        files.update(glob.glob(os.path.join(SRC, pat), recursive=True))
    bad = []
    for f in sorted(files):
        with open(f, encoding="utf-8") as fh:
            for ln, call in violations(fh.read()):
                bad.append(f"{os.path.relpath(f, ROOT)}:{ln}: {call}")
    if bad:
        print("❌ Math.Round ในโมดูลเงินเดือนที่ไม่ระบุ MidpointRounding (default = banker's rounding):")
        for b in bad:
            print("  " + b)
        print("   แก้: Math.Round(x, 2, MidpointRounding.AwayFromZero)")
        return 1
    print(f"✅ payroll_rounding_check: Math.Round ในโมดูลเงินเดือน {len(files)} ไฟล์ระบุ MidpointRounding ครบ")
    return 0


if __name__ == "__main__":
    sys.exit(main())
