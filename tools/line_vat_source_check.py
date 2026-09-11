#!/usr/bin/env python3
# -*- coding: utf-8 -*-
"""ทุกการ "เขียนอัตรา VAT ลงบรรทัดเอกสาร" ต้องผ่าน Layout.setLineVat

ที่มา (บั๊กจริง · ผู้ใช้รายงาน 2026-09-11)
-----------------------------------------
บิลเขียนมือของผู้ขายที่ไม่จด VAT (ยอด 3,500 · ช่อง VAT ว่าง · รวม 3,500) สแกน
อ่านถูกทุกช่อง ฟอร์มเติม 0% ให้ถูกแล้ว — แต่ `documents.html` ผูก
`descInput.addEventListener('blur', () => this.suggestVatType(row))` ซึ่งถาม
endpoint heuristic ว่า "รายการชื่อแบบนี้ปกติคิด VAT เท่าไร" แล้ว **เขียนทับทันที**
เพราะด่านเดียวที่มีคือ `vatSel.dataset.userTouched === '1'` ซึ่งตัวเติมจาก OCR
ไม่เคยตั้งให้ ⇒ ยอดสุทธิเด้งจาก 3,500 เป็น 3,745 (VAT 245 ที่ไม่มีบนกระดาษ)
⇒ ภาษีซื้อผีเข้า ภ.พ.30 · ยอดเจ้าหนี้/ยอดจ่ายเกินจริง — เงียบสนิทเพราะตัวเลข
"ดูสมเหตุสมผล" และผู้ใช้เพิ่งเห็นหน้าสแกนบอกว่าไม่มี VAT มาก่อนหน้านั้นเอง

ทำไมต้องเป็น checker ไม่ใช่แค่แก้จุดที่ผู้ใช้เจอ
------------------------------------------------
ช่อง VAT ของบรรทัดมีคนเขียนอยู่ 8 จุดใน 3 ไฟล์ (OCR handoff · เปิดแก้เอกสารเดิม ·
ใบลดหนี้ · ใบเสร็จตัดลูกหนี้ · ค่าตั้งต้นตามชนิดเอกสาร · คลังสินค้า · §83/6 ·
ตัวแนะนำ) — "แก้ตัวเดียว เหลือที่เหลือ" คือ defect class ที่เรพนี้เจอซ้ำที่สุด
และจุดที่เพิ่มใหม่วันหลังจะไม่มีอะไรฟ้องเลย (JS ที่ถูกไวยากรณ์ทุกประการ)

กติกา: ห้ามเขียน `.value = …` ลงช่องที่ได้จาก `[data-f="vat"]` ตรง ๆ —
ต้องเรียก `Layout.setLineVat(sel, rate, src)` ที่ประกาศ **ที่มา** ของค่า
เพื่อให้ลำดับความน่าเชื่อ (user > doc/product > ocr > doctype > ai) ตัดสินแทน
"ใครมาก่อน/มาหลัง"

ข้อยกเว้นเดียว: ตัว `setLineVat` เองใน `js/layout.js` (นิยามของกติกา) และ
fallback บรรทัดเดียวใน `product-lookup.js` สำหรับหน้าที่ไม่ได้โหลด layout.js

self-test: python3 tools/line_vat_source_check.py --self-test
"""
import re, sys, pathlib

ROOT = pathlib.Path(__file__).resolve().parent.parent
WWW = ROOT / "Accounting" / "wwwroot"

VAT_SELECTOR = re.compile(r"""\[data-f=["']vat["']\]""")
# 1) เขียนตรง: row.querySelector('[data-f="vat"]').value = …
DIRECT = re.compile(r"""\[data-f=["']vat["']\]["']\s*\)\s*(?:\?\.)?\.value\s*=(?!=)""")
# 2) เก็บใส่ตัวแปรก่อน: const vatSel = …[data-f="vat"]… ;  …  vatSel.value = …
CAPTURE = re.compile(
    r"""(?:const|let|var)\s+([A-Za-z_$][\w$]*)\s*=\s*[^;\n]*\[data-f=["']vat["']\]""")
# 3) วนลูปช่อง VAT: querySelectorAll('… [data-f="vat"]').forEach(sel => { sel.value = … })
FOREACH = re.compile(
    r"""\[data-f=["']vat["']\][^;\n]*?\.forEach\(\s*\(?\s*([A-Za-z_$][\w$]*)""")
# การมอบให้ตัวตัดสินกลาง
ALLOWED_CALL = re.compile(r"Layout\.setLineVat\s*\(")

ALLOW_FILES = {
    # นิยามของกติกาเอง
    "js/layout.js",
}
# บรรทัด fallback ที่ตั้งใจ (หน้าที่ไม่ได้โหลด layout.js)
ALLOW_LINE_MARK = "Layout.setLineVat"


def strip_comments_keep_lines(text: str) -> str:
    """ตัดคอมเมนต์ // และ /* */ โดย **คงจำนวนบรรทัด** และต้องแยกสถานะในสตริง
    ไม่งั้นจะไปกิน `//` ของ https:// (บทเรียนเดิมของ csp_external_ref_check)"""
    out, i, n = [], 0, len(text)
    while i < n:
        c = text[i]
        if c in ("'", '"', "`"):
            q = c
            out.append(c); i += 1
            while i < n:
                if text[i] == "\\":
                    out.append(text[i:i+2]); i += 2; continue
                out.append(text[i])
                if text[i] == q:
                    i += 1; break
                i += 1
            continue
        if c == "/" and i + 1 < n and text[i+1] == "/":
            while i < n and text[i] != "\n":
                out.append(" "); i += 1
            continue
        if c == "/" and i + 1 < n and text[i+1] == "*":
            while i < n and not (text[i] == "*" and i + 1 < n and text[i+1] == "/"):
                out.append("\n" if text[i] == "\n" else " "); i += 1
            out.append("  "); i += 2
            continue
        out.append(c); i += 1
    return "".join(out)


def scan_text(text: str, rel: str):
    hits = []
    clean = strip_comments_keep_lines(text)
    lines = clean.split("\n")
    for idx, line in enumerate(lines, 1):
        if DIRECT.search(line) and not ALLOWED_CALL.search(line):
            hits.append((idx, line.strip()))
    # รูปที่เก็บใส่ตัวแปรก่อน (ประกาศตัวแปร หรือพารามิเตอร์ของ forEach)
    # — มองในขอบเขต 25 บรรทัดถัดไป (พอสำหรับทุกจุดในเรพ)
    for m in list(CAPTURE.finditer(clean)) + list(FOREACH.finditer(clean)):
        var = m.group(1)
        start_line = clean.count("\n", 0, m.start()) + 1
        assign = re.compile(r"\b" + re.escape(var) + r"\s*\.value\s*=(?!=)")
        for off in range(0, 25):
            li = start_line - 1 + off
            if li >= len(lines):
                break
            if assign.search(lines[li]) and not ALLOWED_CALL.search(lines[li]):
                hits.append((li + 1, lines[li].strip()))
    return sorted(set(hits))


def run(paths=None):
    problems = []
    files = paths or sorted(
        list(WWW.rglob("*.html")) + list(WWW.rglob("*.js")))
    for f in files:
        rel = str(f.relative_to(WWW)) if paths is None else str(f)
        if rel.replace("\\", "/") in ALLOW_FILES:
            continue
        try:
            text = f.read_text(encoding="utf-8")
        except Exception:
            continue
        if not VAT_SELECTOR.search(text):
            continue
        for ln, src in scan_text(text, rel):
            problems.append(f"{rel}:{ln}: เขียนอัตรา VAT ลงบรรทัดตรง ๆ — ต้องผ่าน "
                            f"Layout.setLineVat(sel, rate, src)\n      {src[:120]}")
    return problems


def self_test():
    bad = """
      fill(l) {
        const vatSel = row.querySelector('[data-f="vat"]');
        vatSel.value = String(l.vatRate);
      }
    """
    good = """
      fill(l) {
        const vatSel = row.querySelector('[data-f="vat"]');
        Layout.setLineVat(vatSel, l.vatRate, 'doc');
        const rate = parseFloat(vatSel.value) || 0;
      }
    """
    direct_bad = """      row.querySelector('[data-f="vat"]').value = 7;"""
    foreach_bad = """
        document.querySelectorAll('#linesBody [data-f="vat"]').forEach(sel => {
          if (parseFloat(sel.value) === 0) sel.value = '7';
        });
    """
    ok = True
    for name, src, want in (("bad", bad, 1), ("good", good, 0), ("direct", direct_bad, 1),
                            ("forEach", foreach_bad, 1)):
        got = len(scan_text(src, name))
        print(f"  self-test {name}: ฟ้อง {got} จุด (ต้องได้ {want})",
              "✅" if got == want else "❌")
        ok &= got == want
    return ok


if __name__ == "__main__":
    if "--self-test" in sys.argv:
        sys.exit(0 if self_test() else 1)
    probs = run()
    if probs:
        print("❌ พบการเขียนอัตรา VAT ของบรรทัดที่ไม่ผ่านตัวตัดสินกลาง:")
        for p in probs:
            print("  " + p)
        sys.exit(1)
    print("✅ ทุกจุดที่เขียนอัตรา VAT ของบรรทัดผ่าน Layout.setLineVat")
