#!/usr/bin/env python3
"""`"` เดี่ยวใน string ธรรมดา — ปิดสตริงกลางคำ ⇒ CS1002/CS1525 ล้มทั้ง solution

ที่มา (บั๊กจริง · รอบ 126 — **ผมเขียนเองในคอมมิต 802f149**):
`DocumentService.RequireOpenFiscalPeriodAsync` เขียนข้อความบอกทางแก้ว่า

    $"ลง{what}เข้างวด "{period.Name}" ไม่ได้ — งวดนี้ปิดแล้ว "
    + "ทางแก้: เปิดงวดที่หน้า "งวดบัญชี" แล้วทำรายการใหม่ "

`"` รอบ `{period.Name}` และรอบ `งวดบัญชี` เป็นอัญประกาศ **ASCII** ⇒ ปิดสตริง
ตรงนั้นทันที ⇒ ตัวถัดไป (`{` / ตัวอักษรไทย) กลายเป็น token ที่วางผิดที่ ⇒
CS1002 "; expected" ลามทั้งไฟล์ แล้ว `Accounting.Tests` พังตามด้วย CS0006

CLAUDE.md §F มีบทเรียนนี้อยู่แล้ว (เคส `$@"..."` ของ `BuildCss`) แต่
`verbatim_string_check.py` ตรวจเฉพาะ **verbatim/raw** string — สตริงธรรมดา
`"..."` / `$"..."` ไม่เคยมีใครดู. ในสตริงแบบนั้น `"` จริงต้องเขียน `\"` หรือ
เลี่ยงไปใช้อัญประกาศไทย `“ ”` (ทางที่เรพนี้ใช้กับข้อความถึงผู้ใช้)

กติกาที่ตรวจ: ตัวปิดของสตริงธรรมดาต้องตามด้วยตัวอักษรที่ "จบสตริงได้จริง"
(ช่องว่าง `)` `,` `;` `.` `+` `]` `}` `:` `?` `=` ...) — ถ้าตามด้วย
**ตัวอักษร/ตัวเลข/`{`** แปลว่ามันไม่ใช่ตัวปิด แต่เป็น `"` ที่ลืมหนี
"""
import re
import sys
from pathlib import Path

ROOT = Path(__file__).resolve().parent.parent
SRC = ROOT / "Accounting"

# ตัวอักษรที่ตามหลัง `"` แล้วแปลว่า "ตัวปิดจริง" — ทุกอย่างที่เป็น operator/
# ตัวคั่น/จบบรรทัด. ที่เหลือ (ตัวอักษรทุกภาษา · ตัวเลข · `{`) = ยังอยู่กลางคำ
#   `}` ต้องอยู่ในลิสต์ (สตริงจบท้าย interpolation hole `{x ?? ""}` และท้าย
#   collection initializer) แต่ `{` **ห้าม** — `"` ที่ตามด้วย `{` คือลายเซ็นของบั๊กนี้
CLOSER_OK = set(" \t\r\n)]},;.:+?=!&|<>*/%^~-#@[")

# ⚠️ ห้าม slice `text[i:]` ในลูป — ไฟล์ใหญ่ (DocumentService.cs ~1 MB) จะกลายเป็น
# O(n²) จนรันไม่จบ (เจอตอนรันครั้งแรก: timeout 120 วิ) · ใช้ match(text, i) แทน
VERBATIM_OPEN = re.compile(r'(?:@\$?|\$@)"')


def scan(text: str):
    """คืน [(บรรทัด, ตัวอย่าง)] ของ `"` ที่น่าจะลืมหนี"""
    hits = []
    n = len(text)
    # state: บรรทัดปัจจุบัน · ตัวนับ interpolation hole ที่กำลังเปิดอยู่
    state = {"line": 1}

    def nl(a, b):
        state["line"] += text.count("\n", a, b)

    def skip_string(i, interpolated, verbatim):
        """ข้ามสตริง 1 ก้อน (ตัวชี้อยู่หลัง `"` เปิดแล้ว) คืนตำแหน่งหลังตัวปิด

        ⚠️ ต้องรู้จัก **interpolation hole** `{...}` — ข้างในเป็น **โค้ด** ที่มี
        สตริงของตัวเองได้ (`{x ?? ""}` · `{id.ToString("N")}`) ถ้าไม่แยกจะฟ้องผิด
        เป็นร้อยจุด (รุ่นแรกฟ้อง 382 จุดบนเรพที่ไม่มีบั๊ก — ซ้ำรอยบทเรียนของ
        `verbatim_string_check.py` ที่ต้อง track hole เหมือนกัน)
        """
        start_line = state["line"]
        while i < n:
            ch = text[i]
            if ch == "\n":
                state["line"] += 1
                if not verbatim:
                    return i + 1  # สตริงธรรมดาข้ามบรรทัดไม่ได้ — ปล่อยให้ compiler ว่า
                i += 1
                continue
            if not verbatim and ch == "\\":
                i += 2
                continue
            if interpolated and ch == "{":
                if i + 1 < n and text[i + 1] == "{":
                    i += 2
                    continue
                i = skip_hole(i + 1)
                continue
            if ch == '"':
                if verbatim and i + 1 < n and text[i + 1] == '"':
                    i += 2
                    continue
                nxt = text[i + 1] if i + 1 < n else "\n"
                if not verbatim and nxt not in CLOSER_OK:
                    snippet = text[max(0, i - 45):i + 20].replace("\n", "\u23ce")
                    hits.append((start_line, snippet))
                return i + 1
            i += 1
        return i

    def skip_hole(i):
        """ข้ามเนื้อใน `{...}` ของ interpolated string — คืนตำแหน่งหลัง `}`"""
        depth = 1
        while i < n and depth > 0:
            ch = text[i]
            if ch == "\n":
                state["line"] += 1
                i += 1
                continue
            if ch == "'":
                i = skip_char(i + 1)
                continue
            m = VERBATIM_OPEN.match(text, i)
            if m:
                i = skip_string(m.end(), "$" in m.group(0), True)
                continue
            if ch == '"':
                i = skip_string(i + 1, False, False)
                continue
            if ch == "$" and i + 1 < n and text[i + 1] == '"':
                i = skip_string(i + 2, True, False)
                continue
            if ch == "{":
                depth += 1
            elif ch == "}":
                depth -= 1
            i += 1
        return i

    def skip_char(i):
        while i < n and text[i] != "'":
            i += 2 if text[i] == "\\" else 1
        return i + 1

    i = 0
    while i < n:
        c = text[i]
        if c == "\n":
            state["line"] += 1
            i += 1
            continue
        if c == "/" and i + 1 < n and text[i + 1] == "/":
            while i < n and text[i] != "\n":
                i += 1
            continue
        if c == "/" and i + 1 < n and text[i + 1] == "*":
            j = text.find("*/", i + 2)
            j = n if j < 0 else j + 2
            nl(i, j)
            i = j
            continue
        if c == "'":
            i = skip_char(i + 1)
            continue
        # raw string """...""" — ข้ามทั้งก้อน (verbatim_string_check ดูแลแล้ว)
        if text.startswith('"""', i) or text.startswith('$"""', i) or text.startswith('$$"""', i):
            j = text.index('"""', i) + 3
            close = text.find('"""', j)
            if close < 0:
                break
            nl(i, close)
            i = close + 3
            while i < n and text[i] == '"':
                i += 1
            continue
        m = VERBATIM_OPEN.match(text, i)
        if m:
            i = skip_string(m.end(), "$" in m.group(0), True)
            continue
        if c == '"':
            i = skip_string(i + 1, False, False)
            continue
        if c == "$" and i + 1 < n and text[i + 1] == '"':
            i = skip_string(i + 2, True, False)
            continue
        i += 1
    return hits


def main() -> int:
    targets = sys.argv[1:] or [str(SRC)]
    files = []
    for t in targets:
        p = Path(t)
        files.extend(sorted(p.rglob("*.cs")) if p.is_dir() else [p])

    total = 0
    for f in files:
        for lineno, snippet in scan(f.read_text(encoding="utf-8", errors="replace")):
            total += 1
            rel = f.relative_to(ROOT) if str(f).startswith(str(ROOT)) else f
            print(f"{rel}:{lineno}: `\"` ในสตริงธรรมดาที่ยังไม่หนี — ปิดสตริงกลางคำ")
            print(f"    => …{snippet}…")
            print('    → ใช้ `\\"` หรืออัญประกาศไทย “ ” ในข้อความถึงผู้ใช้')

    print(f"\nตรวจ {len(files)} ไฟล์ .cs · จุดที่ `\"` ปิดสตริงกลางคำ {total} จุด")
    return 1 if total else 0


if __name__ == "__main__":
    raise SystemExit(main())
