"""ตัวอ่าน "วันที่เอกสาร" แบบกติกา (rule-based) ของ python OCR service — รอบ 193 · คำตัดสินเจ้าของข้อ 27

ที่มา (team-L รอบ 190 ตัวอ่าน #2): ``rule_based_extraction`` เดิมรู้จักแต่ปี 4 หลัก และหยิบวันที่ **ตัวแรกของหน้า**
⇒ ใบ Wine Pro ``Date: 18/09/26`` ไม่ได้วันที่ · ใบที่มี "Due Date" / "ครบกำหนด" อยู่บนสุดได้วันครบกำหนดเป็นวันที่เอกสาร.
ตัวนี้ยกหลักเดียวกับ ``Accounting/Helpers/OcrDateReader.cs`` แบบย่อ (ฝั่ง C# ยังตรวจซ้ำทุกใบ — ที่นี่แค่ไม่ให้แย่กว่า):

1. **ป้ายชนะตำแหน่ง** — วันที่ที่มีป้าย วันที่/Date/ลงวันที่ ชนะวันที่ลอย ๆ · วันที่ที่มีป้ายชนิดอื่น
   (ครบกำหนด/Due/พิมพ์/หมดอายุ/ส่งของ) ไม่มีสิทธิ์เป็นวันที่เอกสาร
2. **แบบไทยก่อน** — วัน/เดือน/ปี · ถ้าเป็นวันที่ไม่ได้ค่อยลอง เดือน/วัน/ปี
3. **ปี** — 4 หลัก พ.ศ. (≥ 2400) ลบ 543 · 2 หลัก: ≥ 60 = พ.ศ. ย่อ (69 → 2026) · < 60 = ค.ศ. ย่อ (26 → 2026)
   (ตรงกับ ``ThaiDate.NormalizeYear`` ฝั่ง C#)

ไม่มี dependency นอก stdlib — ทดสอบได้ด้วย ``python3 -m doctest ocr-service/app/date_reader.py -v``

>>> read_document_date("Receipt / Tax Invoice\\nDate: 18/09/26 6:35 PM\\nTotal 3,593.00")
'2026-09-18'
>>> read_document_date("ใบกำกับภาษี วันที่ DATE 12/09/26")
'2026-09-12'
>>> read_document_date("Due Date: 30/09/2026\\nInvoice Date: 01/09/2026")
'2026-09-01'
>>> read_document_date("ครบกำหนด 12 ต.ค. 69\\nวันที่ 12 ก.ย. 69")
'2026-09-12'
>>> read_document_date("วันที่ 15/08/2569")
'2026-08-15'
>>> read_document_date("ใบเสร็จ 05/08/2026")
'2026-08-05'
>>> read_document_date("Invoice Date: 12/31/2026")
'2026-12-31'
>>> read_document_date("Due Date: 30/09/2026") is None
True
>>> read_document_date("") is None
True
"""
from __future__ import annotations

import re
from datetime import date

# ตัวคั่นระหว่างตัวเลขใช้ [ \t] เท่านั้น (ไม่ข้ามบรรทัด) · [0-9] ไม่ใช่ \d (กันเลขไทย)
_NUMERIC = re.compile(
    r"(?<![0-9/.\-])([0-9]{1,2})[ \t]*([/.\-])[ \t]*([0-9]{1,2})[ \t]*\2[ \t]*([0-9]{4}|[0-9]{2})(?![0-9/.\-])")
_THAI_MONTH = re.compile(
    r"(?<![0-9])([0-9]{1,2})[ \t]*"
    r"(ม\.?ค\.?|ก\.?พ\.?|มี\.?ค\.?|เม\.?ย\.?|พ\.?ค\.?|มิ\.?ย\.?|ก\.?ค\.?|ส\.?ค\.?|ก\.?ย\.?|ต\.?ค\.?|พ\.?ย\.?|ธ\.?ค\.?)"
    r"[ \t]*([0-9]{4}|[0-9]{2})(?![0-9])")
_THAI_MONTHS = {
    "มค": 1, "กพ": 2, "มีค": 3, "เมย": 4, "พค": 5, "มิย": 6,
    "กค": 7, "สค": 8, "กย": 9, "ตค": 10, "พย": 11, "ธค": 12,
}
# ป้ายชนิดอื่นตรวจก่อนเสมอ ("Due Date" มีคำว่า Date อยู่ข้างใน)
_OTHER_LABELS = ("ครบกำหนด", "กำหนดชำระ", "กำหนดส่ง", "due", "print", "พิมพ์", "expir", "exp.", "exp:",
                 "หมดอายุ", "valid", "ใช้ได้ถึง", "ถึงวันที่", "delivery", "วันส่ง", "ส่งของ",
                 "orderdate", "podate", "เริ่ม", "สิ้นสุด", "period")
_DOC_LABELS = ("วันที่", "ลงวันที่", "date", "วันออก", "issued")
_LOOK_BEHIND = 40


def _normalize_year(y: int) -> int:
    if y >= 2400:
        return y - 543
    if y >= 100:
        return y
    return 2500 + y - 543 if y >= 60 else 2000 + y


def _valid(y: int, m: int, d: int) -> date | None:
    try:
        return date(y, m, d) if 1900 <= y <= 2400 else None
    except ValueError:
        return None


def _label(text: str, pos: int, prev_end_same_line: int) -> int:
    """0 = ไม่มีป้าย · 1 = ป้ายวันที่เอกสาร · 2 = ป้ายวันที่ชนิดอื่น (มองย้อนในบรรทัดเดียวกันเท่านั้น)"""
    line_start = text.rfind("\n", 0, pos) + 1
    start = max(line_start, prev_end_same_line, pos - _LOOK_BEHIND)
    p = re.sub(r"\s+", "", text[start:pos]).lower()
    other_end = max((p.rfind(l) + len(l) for l in _OTHER_LABELS if l in p), default=-1)
    doc_hits = [(p.rfind(l), p.rfind(l) + len(l)) for l in _DOC_LABELS if l in p]
    if not doc_hits:
        return 2 if other_end >= 0 else 0
    doc_start, doc_end = max(doc_hits, key=lambda h: h[1])
    if other_end >= doc_end:
        return 2
    # ป้ายชนิดอื่นที่จบชิดหน้าป้ายวันที่ (≤ 2 ตัว: "due date") = ป้ายเดียวกัน
    for l in _OTHER_LABELS:
        i = p.rfind(l)
        if i >= 0 and i + len(l) <= doc_start and doc_start - (i + len(l)) <= 2:
            return 2
    return 1


def read_document_date(text: str | None) -> str | None:
    """วันที่เอกสาร (``YYYY-MM-DD``) หรือ ``None`` เมื่อไม่มีวันที่ที่มีสิทธิ์เป็นวันที่เอกสาร"""
    if not text:
        return None
    found: list[tuple[int, int, date]] = []   # (position, label, date)
    for m in _NUMERIC.finditer(text):
        a, b, y = int(m.group(1)), int(m.group(3)), _normalize_year(int(m.group(4)))
        d = _valid(y, b, a) or _valid(y, a, b)   # แบบไทยก่อน
        if d:
            found.append((m.start(), m.end(), d))
    for m in _THAI_MONTH.finditer(text):
        mo = _THAI_MONTHS.get(m.group(2).replace(".", ""))
        d = _valid(_normalize_year(int(m.group(3))), mo, int(m.group(1))) if mo else None
        if d:
            found.append((m.start(), m.end(), d))
    if not found:
        return None

    candidates = []
    prev_end, prev_line = 0, -1
    for start, end, d in sorted(found):
        line_start = text.rfind("\n", 0, start) + 1
        label = _label(text, start, prev_end if line_start == prev_line else 0)
        candidates.append((label, start, d))
        prev_end, prev_line = end, line_start

    # ป้ายวันที่เอกสาร > ไม่มีป้าย · บนก่อนล่าง · ป้ายชนิดอื่นไม่มีสิทธิ์
    ranked = [c for c in candidates if c[0] != 2]
    if not ranked:
        return None
    best = sorted(ranked, key=lambda c: (0 if c[0] == 1 else 1, c[1]))[0]
    return best[2].isoformat()
