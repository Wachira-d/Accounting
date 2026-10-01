#!/usr/bin/env python3
# -*- coding: utf-8 -*-
"""ocr_helper_test_check.py — ตัวตัดสินของไปป์ไลน์ OCR ทุกตัวต้องมีเทสต์ล็อกไว้

ที่มา (ถดถอยจริง — ผู้ใช้รายงาน 2026-09-10 "ก่อนหน้านี้เคยทำงานได้ถูกต้องมากกว่านี้"):
คอมมิต 4fd8dd6 เพิ่มด่านให้ตัวค้นสามค่า (AmountTriple) เลิกทับค่าที่ engine อ่านมา —
ถูกต้องในตัวมันเอง — แต่ก่อนหน้านั้นการ "ทับ" นั่นเองที่บังเอิญ**ซ่อม**ป้าย SubTotal/Total
ที่ engine สลับให้ใบลักกี้เวย์อยู่โดยไม่มีใครรู้. พอด่านมา ป้ายสลับก็โผล่ ⇒ เอกสารที่สร้าง
ยอดผิด (239 รวม VAT กลายเป็นก่อน VAT). วันถัดมา 416f095 เพิ่มตัวสแกนคำ "มัดจำ" ทั้งหน้า
⇒ แถวฟอร์ม "หักเงินมัดจำ 0.00" ทำให้ใบซื้อธรรมดาติด [DEPOSIT-BUY] — defect class เดียวกับ
"แถวยอด 0 ไม่ใช่หลักฐาน" ที่แก้ไปแล้วในตัวอ่านประเภทเงินได้อีกตัวหนึ่ง

ทั้งสองเคสมีจุดร่วม: **ตรรกะที่ตัดสินตัวเลข/ธงบนสแกน ถูกแก้โดยไม่มีเทสต์ที่ใช้กระดาษ
จริงล็อกพฤติกรรมเดิมไว้** — จึงไม่มีอะไรฟ้องตอนที่การแก้ทำให้ใบที่เคยถูกกลับมาผิด.
กลไกที่กัน: ตัวตัดสินของ OCR ทุกตัวอยู่ใน `Accounting/Helpers/Ocr*.cs` (pure) และ**ต้องมี**
ไฟล์เทสต์ที่อ้างถึงคลาสนั้นใน `Accounting.Tests/` — เพิ่ม helper ใหม่แล้วไม่มีเทสต์ =
ยังไม่ผ่าน (กฎเหล็ก #4 G: control ที่ไม่มีเทสต์ยืนยัน = ไม่มี control)

ทำไม checker เดิมจับไม่ได้: ทุกตัวอ่านโครงสร้างโค้ดใน `Accounting/` — ไม่มีตัวไหนถาม
ว่า "ของชิ้นนี้มีเทสต์ไหม" ซึ่งต้องโยงสองโปรเจกต์เข้าหากัน

รันเปล่า ๆ = ตรวจทั้งเรพ · `--self-test` = ทดสอบตัว checker เอง

ส่วนที่สอง (รอบ 201 ทีม OC · A-OC4 · ratchet): static method ใน `OcrService.cs` — ตัวตัดสินที่ยังฝังในไฟล์ service
คือที่ที่ถดถอยเกิดโดยไม่มีอะไรฟ้อง (CLAUDE.md §H) · ชื่อที่มีอยู่แล้วอยู่ใน `tools/ocr_service_static_baseline.txt` ·
static ตัวใหม่ = ฟ้อง (ย้ายไป Helpers/Ocr*.cs พร้อมเทสต์) · แถวใน baseline ที่ไม่มีในไฟล์แล้ว = ฟ้อง (ตัดแถวทิ้ง — baseline ห้ามค้างของตาย)
"""
import os
import re
import sys

ROOT = os.path.dirname(os.path.dirname(os.path.abspath(__file__)))
HELPERS = os.path.join(ROOT, "Accounting", "Helpers")
TESTS = os.path.join(ROOT, "Accounting.Tests")

# ชนิดระดับบนสุดที่ "ตัดสิน" ได้ — record/enum ที่เป็นแค่ข้อมูลไม่ต้องมีเทสต์ของตัวเอง
CLASS_RE = re.compile(r"^\s*public\s+(?:static\s+|sealed\s+|partial\s+)*class\s+([A-Za-z_][A-Za-z0-9_]*)", re.M)
IDENT = r"(?<![A-Za-z0-9_]){}(?![A-Za-z0-9_])"


def ocr_helper_classes(helpers_dir=HELPERS):
    """คืน list ของ (ไฟล์, ชื่อคลาส) สำหรับทุก public class ใน Helpers/Ocr*.cs"""
    out = []
    if not os.path.isdir(helpers_dir):
        return out
    for f in sorted(os.listdir(helpers_dir)):
        if not (f.startswith("Ocr") and f.endswith(".cs")):
            continue
        txt = open(os.path.join(helpers_dir, f), encoding="utf-8").read()
        for m in CLASS_RE.finditer(txt):
            out.append((f, m.group(1)))
    return out


def referenced_in_tests(cls, tests_dir=TESTS):
    pat = re.compile(IDENT.format(re.escape(cls)))
    if not os.path.isdir(tests_dir):
        return False
    for f in os.listdir(tests_dir):
        if not f.endswith(".cs"):
            continue
        try:
            txt = open(os.path.join(tests_dir, f), encoding="utf-8").read()
        except (OSError, UnicodeDecodeError):
            continue
        if pat.search(txt):
            return True
    return False


def collect(helpers_dir=HELPERS, tests_dir=TESTS):
    """คืน list ของ (ไฟล์, คลาส) ที่ไม่มีเทสต์ไหนอ้างถึง"""
    return [(f, c) for f, c in ocr_helper_classes(helpers_dir) if not referenced_in_tests(c, tests_dir)]


OCR_SERVICE = os.path.join(ROOT, "Accounting", "Services", "Implementations", "OcrService.cs")
STATIC_BASELINE = os.path.join(ROOT, "tools", "ocr_service_static_baseline.txt")
# เมธอด static (ไม่นับ field `static readonly` · ชนิดซ้อน) — ชนิดคืนเป็น tuple หลายบรรทัดได้
STATIC_METHOD_RE = re.compile(
    r"^[ \t]*(?:private|internal|public|protected)\s+static\s+(?!readonly\b|class\b|partial\b|extern\b)(?:async\s+)?"
    r"(\([^)]*\)|[\w.]+(?:<[^;(){}]*?>)?(?:\[\])?\??)\s+(\w+)\s*(?:<[^>]*>)?\s*\(", re.M)


def static_methods(text):
    return sorted({m.group(2) for m in STATIC_METHOD_RE.finditer(text)})


def load_baseline(path=STATIC_BASELINE):
    if not os.path.isfile(path):
        return set()
    out = set()
    for line in open(path, encoding="utf-8"):
        line = line.strip()
        if line and not line.startswith("#"):
            out.add(line)
    return out


def static_ratchet(text, baseline):
    """คืน (ตัวใหม่ที่ไม่อยู่ใน baseline, แถว baseline ที่ไม่มีในไฟล์แล้ว)"""
    found = set(static_methods(text))
    return sorted(found - baseline), sorted(baseline - found)


def ratchet_self_test(verbose=False):
    """negative test ของ ratchet บนไฟล์จริง — รันทุกครั้งที่รัน checker (ไม่ต้องจำไปรัน --self-test เอง)"""
    ok = True
    if not os.path.isfile(OCR_SERVICE):
        return ok
    real = open(OCR_SERVICE, encoding="utf-8").read()
    base = load_baseline()
    injected = real + "\nstatic class __X\n{\n    private static decimal? InferSomethingNew(string? rawText) => null;\n}\n"
    new, _ = static_ratchet(injected, base)
    if "InferSomethingNew" not in new:
        print("❌ self-test: static method ใหม่ใน OcrService.cs ไม่ถูกฟ้อง"); ok = False
    elif verbose:
        print("✅ self-test: static method ใหม่ใน OcrService.cs ถูกฟ้อง (ratchet)")
    multi = "    private static (string? A,\n        string? B)\n        Pair(string x)\n    { return (x, x); }\n"
    if "Pair" not in static_methods(multi):
        print("❌ self-test: ชนิดคืนเป็น tuple หลายบรรทัดไม่ถูกนับ"); ok = False
    if static_methods("    private static readonly Regex R = new(\"x\");\n"):
        print("❌ self-test: field static readonly ถูกนับเป็นเมธอด"); ok = False
    _, stale = static_ratchet(real, base | {"ThisMethodWasMovedOut"})
    if "ThisMethodWasMovedOut" not in stale:
        print("❌ self-test: แถว baseline ที่ไม่มีในไฟล์แล้วไม่ถูกฟ้อง"); ok = False
    elif verbose:
        print("✅ self-test: แถว baseline ค้าง (ย้ายออกแล้วไม่ตัดแถว) ถูกฟ้อง")
    return ok


def self_test():
    import shutil
    import tempfile
    tmp = tempfile.mkdtemp()
    try:
        h = os.path.join(tmp, "Helpers"); t = os.path.join(tmp, "Tests")
        os.makedirs(h); os.makedirs(t)
        open(os.path.join(h, "OcrGoodThing.cs"), "w", encoding="utf-8").write(
            "namespace X;\npublic static class OcrGoodThing { public static int F() => 1; }\n"
            "public sealed record OcrGoodThingResult(int A);\n")
        open(os.path.join(h, "OcrOrphan.cs"), "w", encoding="utf-8").write(
            "namespace X;\npublic static class OcrOrphan { public static int F() => 1; }\n")
        open(os.path.join(h, "NotOcrHelper.cs"), "w", encoding="utf-8").write(
            "namespace X;\npublic static class NotOcrHelper { }\n")
        open(os.path.join(t, "OcrGoodThingTests.cs"), "w", encoding="utf-8").write(
            "public class OcrGoodThingTests { void T() { var x = OcrGoodThing.F(); } }\n")
        # ชื่อที่เป็นแค่ prefix ของอีกชื่อ ต้องไม่นับว่าอ้างถึง (OcrOrphan vs OcrOrphanage)
        open(os.path.join(t, "OtherTests.cs"), "w", encoding="utf-8").write(
            "public class OtherTests { void T() { var y = OcrOrphanage.G(); } }\n")
        bad = collect(h, t)
        names = {c for _, c in bad}
        ok = True
        if "OcrOrphan" not in names:
            print("❌ self-test: helper ที่ไม่มีเทสต์ไม่ถูกฟ้อง"); ok = False
        else:
            print("✅ self-test: helper ที่ไม่มีเทสต์ถูกฟ้อง (และชื่อที่เป็น prefix ไม่ถูกนับว่าอ้างถึง)")
        if "OcrGoodThing" in names:
            print("❌ self-test: ฟ้องผิดบน helper ที่มีเทสต์"); ok = False
        else:
            print("✅ self-test: helper ที่มีเทสต์ ไม่ฟ้อง")
        if "NotOcrHelper" in names:
            print("❌ self-test: ไฟล์ที่ไม่ใช่ Ocr* ถูกตรวจ"); ok = False
        else:
            print("✅ self-test: ตรวจเฉพาะ Helpers/Ocr*.cs")
        if not ratchet_self_test(verbose=True):
            ok = False
        return 0 if ok else 1
    finally:
        shutil.rmtree(tmp, ignore_errors=True)


def main():
    if "--self-test" in sys.argv:
        return self_test()
    total = len(ocr_helper_classes())
    bad = collect()
    print()
    for f, c in bad:
        print(f"Accounting/Helpers/{f}: `{c}` ไม่มีเทสต์ไหนใน Accounting.Tests อ้างถึง")
        print("    → เพิ่ม Accounting.Tests/<ชื่อคลาส>Tests.cs ที่ล็อกทั้งเคสที่ต้องทำงานและเคสที่ต้องเงียบ ด้วยเลข/ข้อความจากกระดาษจริง")
    print(f"\nตรวจตัวตัดสิน OCR {total} คลาส · ที่ไม่มีเทสต์ {len(bad)} คลาส")
    new, stale = [], []
    if os.path.isfile(OCR_SERVICE):
        new, stale = static_ratchet(open(OCR_SERVICE, encoding="utf-8").read(), load_baseline())
        for n in new:
            print(f"Accounting/Services/Implementations/OcrService.cs: static `{n}` ใหม่ — ตัวตัดสิน OCR ต้องเกิดใน Helpers/Ocr*.cs พร้อมเทสต์ "
                  "(ห้ามเพิ่มแถวใน tools/ocr_service_static_baseline.txt เพื่อให้เขียว)")
        for n in stale:
            print(f"tools/ocr_service_static_baseline.txt: `{n}` ไม่มีใน OcrService.cs แล้ว — ตัดแถวทิ้ง (ratchet ลดลงเท่านั้น)")
        print(f"static ใน OcrService.cs: ใหม่ {len(new)} · baseline ค้าง {len(stale)}")
    rt_ok = ratchet_self_test()
    return 1 if (bad or new or stale or not rt_ok) else 0


if __name__ == "__main__":
    sys.exit(main())
