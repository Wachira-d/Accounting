#!/usr/bin/env python3
# -*- coding: utf-8 -*-
"""ห้ามเขียนตัวกรอง "เลขผู้เสียภาษีเหลือแต่ตัวเลข" เอง — ต้องเรียก ThaiTaxId.Normalize ตัวเดียว

═══ ที่มา (ฝ่ายค้าน W1c P2-6 · ทีม W1d · 2026-10-16) ═══
b195bc96 ทำให้ `Helpers/ThaiTaxId.Normalize` แปลงเลขไทย ๐–๙ เป็น ASCII (เดิมคงไว้ ⇒ checksum คิด `d[i] - '0'` จากรหัสอักขระไทย
ไม่มีวันผ่าน · ช่อง 5 ของไฟล์ ภ.ง.ด.1 เป็นเลขไทย) แต่สำเนา inline `new string(x.TaxId.Where(char.IsDigit).ToArray())` ราว 40 จุด
ยังคงเลขไทยไว้ ⇒ สองรูปของเลขเดียวกันในระบบเดียว (ตัวตัดซ้ำผู้ติดต่อ · คีย์ตัวเรียนรู้ · ตัวเทียบ "เลขเราบนกระดาษ" ที่เทียบ
ค่า Normalize กับค่า inline = ไม่มีวันเท่ากันเมื่อกระดาษพิมพ์เลขไทย) — defect class "แก้ตัวตั้ง เหลือสำเนา" (F2 ข้อ 1/4)

═══ กติกา (ขอบเขตแคบโดยตั้งใจ — F4 ข้อ 3: ตรวจระดับบรรทัด ไม่ตาม taint) ═══
ฟ้องบรรทัด (หลังตัดคอมเมนต์) ที่มี **ตัวกรองตัวเลขแบบ inline** —
  `.Where(char.IsDigit)` · `.Where(c => char.IsDigit(c))` · `Regex.Replace(x, @"\\D", "")` · `Regex.Replace(x, @"[^0-9]", "")`
**และ** บรรทัดเดียวกันพูดถึงเลขผู้เสียภาษี/เลขบัตร — คำ `TaxId` · `taxId` · `Tid`/`tid` · `CitizenId` · `citizen` · `companyTax` (ไม่สนตัวพิมพ์)
ไม่ตรวจ `Helpers/ThaiTaxId.cs` (เจ้าของกติกา) · ข้าม `bin` `obj` `.claude` `node_modules` `wwwroot`
ตัวกรองตัวเลขของเบอร์โทร/เลขบัญชี/รหัสสลิป/สาขา (บรรทัดไม่มีคำข้างบน) ไม่ถูกฟ้อง — ไม่ใช่เลขผู้เสียภาษี
จุดที่บรรทัดไม่มีคำเหล่านี้แต่เป็นเลขผู้เสียภาษี (เช่น `m.Value` ของ regex เลข 13 หลัก) แก้ด้วยมือแล้วในทีม W1d — ตัวตรวจนี้กันเฉพาะรูปที่เห็นได้ในบรรทัด

ใช้: python3 tools/taxid_digits_inline_check.py [--self-test]
negative test: --self-test (ใส่สำเนา inline กลับต้องจับได้ · ตัวกรองเบอร์โทร/บัญชีไม่ถูกฟ้อง · คอมเมนต์ไม่ถูกฟ้อง · ไฟล์เจ้าของไม่ถูกสแกน)
"""
import os
import re
import sys
import tempfile

ROOT = os.path.dirname(os.path.dirname(os.path.abspath(__file__)))
SRC = os.path.join(ROOT, "Accounting")
SKIP_DIRS = {"bin", "obj", ".claude", "node_modules", ".git", "wwwroot"}
OWNERS = {"Helpers/ThaiTaxId.cs"}

INLINE_DIGITS = re.compile(
    r"\.Where\s*\(\s*char\s*\.\s*IsDigit\s*\)"
    r"|\.Where\s*\(\s*(\w+)\s*=>\s*char\s*\.\s*IsDigit\s*\(\s*\1\s*\)\s*\)"
    r"|Regex\s*\.\s*Replace\s*\([^;]*?@?\"(?:\\\\D|\\D|\[\^0-9\])\"\s*,\s*\"\"\s*\)")
TAX_WORD = re.compile(r"taxid|\btid\b|tid\b|citizen|companytax", re.IGNORECASE)

_BLOCK_COMMENT = re.compile(r"/\*.*?\*/", re.S)


def strip_comments(text):
    text = _BLOCK_COMMENT.sub(lambda m: re.sub(r"[^\n]", " ", m.group(0)), text)
    out = []
    for line in text.split("\n"):
        idx = line.find("//")
        while idx >= 0 and line[:idx].count('"') % 2 == 1:
            idx = line.find("//", idx + 2)
        out.append(line[:idx] if idx >= 0 else line)
    return "\n".join(out)


def scan_text(body):
    hits = []
    for no, line in enumerate(body.split("\n"), 1):
        if INLINE_DIGITS.search(line) and TAX_WORD.search(line):
            hits.append(no)
    return hits


def scan(src=None):
    src = src or SRC
    out = []
    for dirpath, dirnames, filenames in os.walk(src):
        dirnames[:] = [d for d in dirnames if d not in SKIP_DIRS]
        for fn in filenames:
            if not fn.endswith(".cs"):
                continue
            full = os.path.join(dirpath, fn)
            rel = os.path.relpath(full, src).replace(os.sep, "/")
            if rel in OWNERS:
                continue
            try:
                body = strip_comments(open(full, encoding="utf-8", errors="replace").read())
            except OSError:
                continue
            lines = scan_text(body)
            if lines:
                out.append((rel, lines))
    out.sort()
    return out


def self_test():
    bad = [
        'var d = new string(c.TaxId.Where(char.IsDigit).ToArray());',
        'var tid = new string((taxId ?? "").Where(char.IsDigit).ToArray());',
        'var x = new string(emp.CitizenId.Where(ch => char.IsDigit(ch)).ToArray());',
        'var dg = Regex.Replace(taxId ?? "", @"\\D", "").PadRight(13);',
        'var companyTax = new string((companyTaxId ?? "").Where(char.IsDigit).ToArray());',
    ]
    ok = [
        'var d = ThaiTaxId.Normalize(c.TaxId);',
        'var digits = new string(phone.Where(char.IsDigit).ToArray());',
        'var digits = new string((bankAccountNumber ?? "").Where(char.IsDigit).ToArray());',
        '// เดิม new string(c.TaxId.Where(char.IsDigit).ToArray()) — ห้าม',
        'var s = Regex.Replace(v, @"[-\\s+]", "");   // phone taxId-free',
    ]
    fail = 0
    for s in bad:
        if not scan_text(strip_comments(s)):
            print(f"self-test ล้ม: ต้องฟ้อง — {s}"); fail = 1
    for s in ok:
        if scan_text(strip_comments(s)):
            print(f"self-test ล้ม: ต้องไม่ฟ้อง — {s}"); fail = 1
    with tempfile.TemporaryDirectory() as d:
        os.makedirs(os.path.join(d, "Helpers"))
        os.makedirs(os.path.join(d, "bin"))
        open(os.path.join(d, "Helpers", "ThaiTaxId.cs"), "w", encoding="utf-8").write(bad[0])
        open(os.path.join(d, "bin", "X.cs"), "w", encoding="utf-8").write(bad[0])
        open(os.path.join(d, "Svc.cs"), "w", encoding="utf-8").write("\n" + bad[1])
        got = scan(d)
        if got != [("Svc.cs", [2])]:
            print(f"self-test ล้ม: สแกนโฟลเดอร์ได้ {got} (ต้องเป็น Svc.cs บรรทัด 2 เท่านั้น — ไม่สแกนเจ้าของ/bin)"); fail = 1
    print("✅ self-test taxid_digits_inline_check ผ่าน" if not fail else "❌ self-test ล้ม")
    return fail


def main():
    if "--self-test" in sys.argv:
        return self_test()
    hits = scan()
    if not hits:
        print("✅ taxid_digits_inline_check: ไม่มีสำเนาตัวกรองเลขผู้เสียภาษีแบบ inline (ใช้ ThaiTaxId.Normalize ตัวเดียว)")
        return 0
    print("❌ taxid_digits_inline_check: ตัวกรองเลขผู้เสียภาษีแบบ inline — ใช้ Accounting.Helpers.ThaiTaxId.Normalize (แปลงเลขไทยเป็น ASCII)")
    for rel, lines in hits:
        print(f"  Accounting/{rel}: บรรทัด {', '.join(map(str, lines))}")
    return 1


if __name__ == "__main__":
    sys.exit(main())
