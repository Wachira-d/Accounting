#!/usr/bin/env python3
# -*- coding: utf-8 -*-
"""ค่าที่แทรกเข้า **attribute ของ HTML** ต้องผ่านตัวหนีเสมอ

═══ ที่มา (ผลตรวจ G-01/F-01 · ยืนยันด้วยการรันจริง) ═══
`Layout.esc` เดิมทำผ่าน `textContent → innerHTML` ซึ่งตาม HTML serialization spec
หนีแค่ `& < >` — **ไม่หนี `"` และ `'`** ⇒ ทุกจุดที่เขียน

    title="${Layout.esc(v)}"

ค่าที่มี `"` จะ **แตกออกจาก attribute** แล้วผู้โจมตีเติม handler ของตัวเองต่อได้:

    v = 'x" onmouseover="alert(1)'
    → title="x" onmouseover="alert(1)"

ค่าพวกนี้มาจากชื่อผู้ติดต่อ · ชื่อสินค้า · หมายเหตุ · ผลอ่าน OCR — ทั้งหมดเป็นสิ่งที่
ผู้ใช้/คู่ค้า/กระดาษคุมได้ · และ JWT อยู่ใน localStorage ⇒ XSS = ขโมย token

ตัว `esc()` ถูกแก้ให้หนี 5 ตัวแล้ว (แก้ที่รากทีเดียว 61 จุดหายพร้อมกัน) — checker นี้
มีไว้กัน **ตัวถัดไป**: จุดที่แทรกค่าเข้า attribute โดย**ไม่ผ่านตัวหนีเลย**

═══ สิ่งที่ฟ้อง ═══
`<tag attr="${EXPR}">` ที่ EXPR ไม่ผ่าน `esc(` / `jsArg(` / ตัวจัดรูปที่ปลอดภัยอยู่แล้ว

═══ สิ่งที่ไม่ฟ้อง (ปลอดภัยอยู่แล้ว) ═══
* ผ่าน `Layout.esc(...)` / `esc(...)` / `Layout.jsArg(...)` / `escapeHtml(...)`
* ค่าที่เป็นตัวเลขล้วนโดยโครงสร้าง: `Number(...)` · `parseInt/parseFloat` ·
  `.length` · `Layout.money(...)` · `Layout.date(...)` · ตัวแปรนับ/ดัชนี (`i`, `idx`)
* ค่าคงที่/ตัวเลือกที่โค้ดเราเขียนเอง: ternary ที่ทั้งสองข้างเป็น string literal
* `style="...${...}"` ที่เป็นสี/ขนาดจากค่าคงที่ของเราเอง (ไม่มาจากข้อมูลผู้ใช้)

⚠️ บทเรียนตอนเขียน (ทำไม regex ต้องระวัง):
รุ่นแรกจับ `"[^"]*\$\{` ตรง ๆ แล้วไปโดน **template literal ซ้อน** —
`` `<a href="${x}">${`${y}`}</a>` `` และ attribute ที่มีหลาย `${}` ในตัวเดียว
จึงต้องดึง "ค่าใน attribute ทั้งก้อน" ออกมาก่อน แล้วค่อยหา `${...}` ทีละอันแบบ
นับวงเล็บปีกกา (nested `${ a ? `${b}` : c }` มีจริงในเรพนี้)
"""
import re
import sys
from pathlib import Path

ROOT = Path(__file__).resolve().parent.parent
SCAN_DIRS = ["Accounting/wwwroot"]

# attribute ที่ค่าไปอยู่ในบริบทที่ยิงสคริปต์ได้ถ้าแตกออกมา
RISKY_ATTRS = (
    "title|value|placeholder|alt|href|src|label|content|"
    "aria-label|data-[a-z0-9-]+|id|class|name|for"
)
ATTR_RE = re.compile(rf'\b({RISKY_ATTRS})\s*=\s*"([^"]*)"', re.IGNORECASE)

# ตัวหนีที่ยอมรับ (รวมตัวหนีประจำหน้าอย่าง `this._esc(`)
SAFE_CALLS = re.compile(
    r"\b(Layout\.esc|Layout\.jsArg|escapeHtml|_esc|esc|jsArg|encodeURIComponent)\s*\(")

# ═══ ทำไมต้องแคบขนาดนี้ ═══
# รุ่นแรกฟ้อง "ทุกค่าที่แทรกเข้า attribute โดยไม่ผ่านตัวหนี" = **669 จุด** ซึ่ง
# เกือบทั้งหมดเป็นค่าที่โค้ดเราสร้างเอง (`id="${row.id}"` · `class="${cls}"` ·
# `href="${this._pageUrl(...)}"`) — ไม่มีทางมี `"` ปน · checker ที่ฟ้องผิด
# ระดับนั้นคือ checker ที่พังแล้ว (CLAUDE.md) เพราะไม่มีใครอ่านผลอีก
#
# ความเสี่ยงจริงคือ **ข้อความอิสระที่คนนอกคุมได้** — ชื่อผู้ติดต่อ · ชื่อสินค้า ·
# หมายเหตุ · ผลอ่าน OCR · ชื่อบริษัทที่ผู้เช่าตั้งเอง (โผล่ในหน้าแอดมิน) ·
# ซึ่งเป็น **taint ไม่ใช่รูปทรงของโค้ด** · จับได้ใกล้เคียงที่สุดด้วย "ชื่อฟิลด์"
# ที่โดยธรรมชาติเก็บข้อความอิสระ — ยอมพลาดบางจุดดีกว่าฟ้องผิด 600 จุด
FREE_TEXT_FIELD = re.compile(
    r"\.(name|title|description|desc|note|notes|remark|remarks|address|"
    r"subject|message|comment|comments|label|reason|fullName|companyName|"
    r"contactName|productName|customerName|vendorName|supplierName|"
    r"displayName|bankName|accountName)\b", re.IGNORECASE)

# นิพจน์ที่ปลอดภัยโดยโครงสร้าง (ไม่ใช่ข้อความอิสระจากผู้ใช้)
#
# ⚠️ บทเรียนจาก negative test (รุ่นแรกพลาดของจริง): กติกา "ตัวแปรนับ/ดัชนี"
# เดิมไม่ผูกท้าย ⇒ `row.name` ถูกมองว่าปลอดภัยเพราะ **ขึ้นต้น** ด้วย `row`
# ทั้งที่นั่นคือเคสอันตรายที่สุดที่ checker นี้มีไว้จับ · ตัวแปรพวกนี้ปลอดภัย
# ก็ต่อเมื่อ **ทั้งนิพจน์เป็นตัวมันเอง** ไม่ใช่เป็นคำนำหน้าของ property chain
SAFE_SHAPE = re.compile(
    r"^\s*(?:"
    r"Number\(|parseInt\(|parseFloat\(|"
    r"Layout\.(?:money|date|dateTime|dateInput|toDateInput|num)\(|"
    r"[A-Za-z_$][\w$]*\.length\s*$|"
    r"[0-9]+\s*$|"
    r"(?:i|j|k|idx|index|n|no|seq|row|col)\s*$"
    r")")

# ค่าที่เป็นตัวเลือกของโค้ดเราเอง: ternary ที่ผลลัพธ์เป็น string literal ทั้งสองข้าง
TERNARY_LITERALS = re.compile(
    r"^[^?]*\?\s*'[^']*'\s*:\s*'[^']*'\s*$|^[^?]*\?\s*\"[^\"]*\"\s*:\s*\"[^\"]*\"\s*$")


def interpolations(value: str):
    """ดึง `${...}` ออกจากค่าของ attribute — นับปีกกาเพื่อรองรับ template ซ้อน"""
    out, i, n = [], 0, len(value)
    while i < n - 1:
        if value[i] == "$" and value[i + 1] == "{":
            depth, j = 1, i + 2
            while j < n and depth:
                if value[j] == "{":
                    depth += 1
                elif value[j] == "}":
                    depth -= 1
                j += 1
            out.append(value[i + 2:j - 1])
            i = j
        else:
            i += 1
    return out


def strip_comments(src: str) -> str:
    """ตัดคอมเมนต์ **โดยคงจำนวนบรรทัด** และรู้จักสถานะในสตริง

    (บทเรียนซ้ำจาก csp_external_ref_check: การตัด `//` แบบไม่ดูสถานะจะไปกิน
    `//` ของ `https://` ที่อยู่ในสตริงเอง แล้วฟ้องผิดเป็นร้อยจุด)
    """
    out, i, n = [], 0, len(src)
    while i < n:
        c = src[i]
        if c in "\"'`":
            q = c
            out.append(c)
            i += 1
            while i < n:
                if src[i] == "\\":
                    out.append(src[i:i + 2]); i += 2; continue
                out.append(src[i])
                if src[i] == q:
                    i += 1; break
                i += 1
            continue
        if src.startswith("//", i):
            while i < n and src[i] != "\n":
                out.append(" "); i += 1
            continue
        if src.startswith("/*", i):
            while i < n and not src.startswith("*/", i):
                out.append("\n" if src[i] == "\n" else " "); i += 1
            out.append("  "); i += 2
            continue
        out.append(c); i += 1
    return "".join(out)


def check_text(text: str):
    """คืน [(บรรทัด, attr, expr)] ของจุดที่ต้องฟ้อง"""
    hits = []
    cleaned = strip_comments(text)
    for m in ATTR_RE.finditer(cleaned):
        attr, value = m.group(1), m.group(2)
        for expr in interpolations(value):
            if SAFE_CALLS.search(expr):
                continue
            if SAFE_SHAPE.match(expr):
                continue
            if TERNARY_LITERALS.match(expr.strip()):
                continue
            # ฟ้องเฉพาะที่อ้างฟิลด์ข้อความอิสระ (ดูเหตุผลที่ FREE_TEXT_FIELD)
            if not FREE_TEXT_FIELD.search(expr):
                continue
            line = cleaned.count("\n", 0, m.start()) + 1
            hits.append((line, attr, expr.strip()[:90]))
    return hits


def main() -> int:
    files, problems = 0, []
    for d in SCAN_DIRS:
        for path in sorted((ROOT / d).rglob("*.html")):
            files += 1
            try:
                text = path.read_text(encoding="utf-8", errors="ignore")
            except OSError:
                continue
            for line, attr, expr in check_text(text):
                problems.append((path.relative_to(ROOT), line, attr, expr))

    for rel, line, attr, expr in problems:
        print(f"{rel}:{line}: `{attr}=\"${{{expr}}}\"` ไม่ผ่านตัวหนี — "
              f"ค่าที่มี \" จะแตกออกจาก attribute")
        print(f"    → ห่อด้วย Layout.esc(...) (หรือ jsArg ถ้าเป็น JS string ใน onclick)")

    print(f"\nตรวจ {files} ไฟล์ html · ค่าใน attribute ที่ไม่ผ่านตัวหนี "
          f"{len(problems)} จุด")
    return 1 if problems else 0


if __name__ == "__main__":
    sys.exit(main())
