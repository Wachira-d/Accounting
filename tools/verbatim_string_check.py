#!/usr/bin/env python3
# -*- coding: utf-8 -*-
"""verbatim_string_check.py — จับ `"` เดี่ยวที่หลุดเข้าไปใน verbatim string

ที่มาของ checker ตัวนี้ (บั๊กจริง — หลุดถึงผู้ใช้):
`BuildCss` ใน PdfGenerationService.cs เป็น `$@"...CSS ยาวหลายร้อยบรรทัด..."`
เขียนคอมเมนต์ CSS ว่า

    /* ── กติกาการแบ่งหน้า (ผู้ใช้ขอ: "ให้ย้ายไปทั้งส่วน...") ── */

เครื่องหมาย `"` ตัวแรก **ปิด string ทันที** ⇒ CSS ที่เหลือถูกคอมไพเลอร์อ่าน
เป็นโค้ด C# ⇒ error 300+ บรรทัด (CS1010 Newline in constant · CS1056
Unexpected character '—' · CS1040 preprocessor เพราะ `#` ในโค้ดสี ฯลฯ) โดยที่
error **ตัวแรก** ชี้ไปบรรทัดที่ผิดจริง แต่ที่เหลือชี้มั่วทั้งไฟล์

checker เดิมทั้ง 7 ตัวมองไม่เห็นเลยเพราะ:
  · awk brace-balance นับ `{`/`}` — คอมเมนต์นี้ไม่มีวงเล็บปีกกา
  · using/record/arg/accessibility checker อ่านเฉพาะ "โครงสร้างโค้ด"
    ไม่มีตัวไหน tokenize string literal

กฎภาษา C# ที่ตรวจ:
  · `@"..."` / `$@"..."` / `@$"..."` — ปิดด้วย `"` ตัวเดียว, ใส่ `"` จริงต้อง
    เขียน `""`
  · raw string (สาม quote ติดกัน) — quote เดี่ยวใส่ได้ตามสบาย แต่มีกฎของตัวเอง:
    ถ้าเนื้อหากินหลายบรรทัด ตัวเปิดต้องตามด้วยขึ้นบรรทัดใหม่ทันที
    (ห้ามมีเนื้อหาบรรทัดเดียวกับตัวเปิด) และตัวปิดต้องอยู่บรรทัดของตัวเอง
    ผิดกฎนี้ = CS8997 Unterminated raw string literal + error ลามทั้งไฟล์
    (บั๊กจริง: เขียน SQL migration หลายบรรทัดต่อท้ายตัวเปิดเลย ⇒ 300+ errors
     และตัว checker เองก็เพิ่งพลาดกฎเดียวกันในภาษา Python ตอนเขียนหมายเหตุนี้)
  · `"..."` ธรรมดา — `\"` เอาอยู่แล้ว และปิดในบรรทัดเดียว จึงไม่ใช่ปัญหา

ตรรกะ: หา verbatim string ที่เปิดแล้วกินหลายบรรทัด → บรรทัดที่ทำให้มัน "ปิด"
ถ้าหลังจุดปิดยังมีเนื้อหาที่ดูเหมือน "ข้อความต่อ" (ไม่ใช่ `;` `,` `)` `+` ที่
เป็นการปิด expression ตามปกติ) แปลว่า `"` ตัวนั้นน่าจะเป็นการปิดโดยไม่ตั้งใจ

รันเปล่า ๆ = ตรวจทั้งเรพ · `--self-test` = ทดสอบตัว checker เอง
"""
import os
import re
import sys

ROOT = os.path.dirname(os.path.dirname(os.path.abspath(__file__)))
SKIP_DIRS = {"bin", "obj", ".git", "node_modules", ".vs"}

# หลังปิด verbatim string ตามปกติจะเจอสิ่งเหล่านี้ (จบ expression/ต่อ string)
OK_AFTER = re.compile(r'^\s*(?:[;,)\]}]|\+|\.\w|\?\?|==|!=|\)\s*[;,.])')


# ตัวอักษรที่ "อาจ" เป็นจุดเริ่มของสิ่งที่ต้องตรวจ — ตัวอื่นข้ามทีเดียวหลายตัว
# ด้วย regex search (C loop) แทนที่จะเดินทีละตัวใน Python (ไฟล์ในเรพนี้ใหญ่หลัก
# แสนตัวอักษร: เดินทีละตัว + regex ทุกตำแหน่ง = สแกนทั้งเรพเกิน 2 นาที)
_INTERESTING = re.compile(r"""["'@$]|/[/*]""")

# หัวสตริงทุกแบบของ C#: (คำนำหน้า)(รั้ว quote)
_STR_OPEN = re.compile(r'(\$@|@\$|\$|@)?("{3,}|")')


def _skip_trivia(text, i):
    """ถ้า i ชี้ที่คอมเมนต์/char literal → คืนตำแหน่งถัดจากนั้น ไม่ใช่ → คืน None"""
    n = len(text)
    if text.startswith("//", i):
        e = text.find("\n", i)
        return n if e < 0 else e + 1
    if text.startswith("/*", i):
        e = text.find("*/", i + 2)
        return n if e < 0 else e + 2
    if text[i] == "'":
        j = i + 1
        while j < n:
            if text[j] == "\\":
                j += 2
                continue
            if text[j] == "'":
                return j + 1
            if text[j] == "\n":
                return j
            j += 1
        return n
    return None


def _skip_hole(text, i):
    """i ชี้ที่ '{' ของ interpolation hole → คืนตำแหน่งถัดจาก '}' ที่คู่กัน

    ในรูนี้เป็น "โค้ด C# ปกติ" ⇒ สตริงซ้อน/คอมเมนต์/วงเล็บปีกกาซ้อนได้หมด
    (นี่คือเหตุผลที่ `$@"... {(x ? $"<b>{y}</b>" : "")} ..."` ถูกต้องตามภาษา
    และ checker รุ่นแรกฟ้องผิด 5 จุด)"""
    n = len(text)
    depth, j = 0, i
    while j < n:
        ch = text[j]
        nxt = _skip_trivia(text, j)
        if nxt is not None:
            j = nxt
            continue
        m = _STR_OPEN.match(text, j)
        if m and m.group(2):
            j, _ = _skip_string(text, j)
            continue
        if ch == "{":
            depth += 1
        elif ch == "}":
            depth -= 1
            if depth == 0:
                return j + 1
        j += 1
    return n


def _skip_string(text, i):
    """i ชี้ที่หัวสตริง → คืน (ตำแหน่งถัดจากสตริง, ข้อมูลสำหรับ heuristic)

    ข้อมูล = (kind, start, close_pos) โดย kind ∈ {raw, verbatim, plain}"""
    n = len(text)
    m = _STR_OPEN.match(text, i)
    prefix, fence = m.group(1) or "", m.group(2)
    interpolated = "$" in prefix
    verbatim = "@" in prefix

    # raw string (สาม quote ขึ้นไป) — `"` เดี่ยวใส่ได้ แต่กฎหลายบรรทัดเข้ม:
    # เปิดแล้วถ้าเนื้อหาข้ามบรรทัด ตัวเปิดต้องตามด้วย newline ทันที
    if len(fence) >= 3:
        body_start = i + len(prefix) + len(fence)
        e = text.find(fence, body_start)
        end = n if e < 0 else e + len(fence)
        if e >= 0:
            body = text[body_start:e]
            if "\n" in body:
                # หลายบรรทัด → หลังตัวเปิดต้องมีแต่ช่องว่างจนจบบรรทัด
                first_nl = body.find("\n")
                if body[:first_nl].strip():
                    return end, ("raw-multiline", i, body_start)
        return end, None

    j = i + len(prefix) + 1
    while j < n:
        ch = text[j]
        if ch == '"':
            if verbatim and j + 1 < n and text[j + 1] == '"':
                j += 2                      # "" = quote จริงใน verbatim
                continue
            return j + 1, ("verbatim" if verbatim else "plain", i, j)
        if not verbatim and ch == "\\":
            j += 2                          # escape ใน string ธรรมดา
            continue
        if not verbatim and ch == "\n":
            return j, None                  # string ธรรมดาข้ามบรรทัดไม่ได้
        if interpolated and ch == "{":
            if j + 1 < n and text[j + 1] == "{":
                j += 2                      # {{ = ปีกกาจริง
                continue
            j = _skip_hole(text, j)
            continue
        if interpolated and ch == "}" and j + 1 < n and text[j + 1] == "}":
            j += 2
            continue
        j += 1
    return n, None


def scan_text(text):
    """คืน list ของ (line_no, col, snippet) ที่สงสัยว่า quote ปิด string ก่อนเวลา"""
    problems = []
    i, n = 0, len(text)

    def line_of(pos):
        return text.count("\n", 0, pos) + 1

    while i < n:
        m_next = _INTERESTING.search(text, i)
        if not m_next:
            break
        i = m_next.start()

        nxt = _skip_trivia(text, i)
        if nxt is not None:
            i = nxt
            continue

        m = _STR_OPEN.match(text, i)
        if not (m and m.group(2)):
            i += 1                          # '@' / '$' ที่ไม่ได้นำหน้าสตริง
            continue

        end, info = _skip_string(text, i)
        if info and info[0] == "raw-multiline":
            problems.append((line_of(info[1]), 0,
                'raw string หลายบรรทัด: หลัง \"\"\" ที่เปิด ต้องขึ้นบรรทัดใหม่ทันที '
                '(ห้ามมีเนื้อหาบรรทัดเดียวกับตัวเปิด) — CS8997'))
        elif info and info[0] == "verbatim":
            _, start, close = info
            multiline = "\n" in text[start:close]
            nl = text.find("\n", close)
            rest = text[close + 1: n if nl < 0 else nl]
            if multiline and rest.strip() and not OK_AFTER.match(rest):
                problems.append((line_of(close),
                                 close - (text.rfind("\n", 0, close) + 1),
                                 rest.strip()[:70]))
        i = max(end, i + 1)
    return problems


def scan_file(path):
    try:
        text = open(path, encoding="utf-8").read()
    except (OSError, UnicodeDecodeError):
        return []
    return [(path, ln, col, snip) for ln, col, snip in scan_text(text)]


GOOD = '''class A {
    string Css() => $@"
        /* comment with no quotes */
        .x {{ color: red; }}
    ";
    string Ok() => @"line1
line2";
    string Raw() => """
        he said "hi" and it is fine
        """;
    string Plain() => "a \\" b";
    char C = '"';

    // สตริงซ้อนใน interpolation hole — ถูกต้องตามภาษา (checker รุ่นแรกฟ้องผิด
    // 5 จุดในเรพเพราะไม่ได้ track รู {...})
    string Hole(bool cond, string y) => $@"
        <p>{(cond ? $"<b>{y}</b>" : "")}</p>
        <img src='{y}' alt='logo'/>
    ";
    // ปีกกาจริงในสตริง interpolated ต้องเขียน {{ }} — ห้ามนับเป็นรู
    string Braces() => $@"
        .x {{ color: red; }}
        .y {{ content: '}}'; }}
    ";
}'''

BAD = '''class A {
    string Css() => $@"
        /* ผู้ใช้ขอ: "ย้ายทั้งส่วน" */
        .x {{ color: red; }}
    ";
}'''

# raw string หลายบรรทัดที่เปิดแล้วมีเนื้อหาต่อท้ายทันที = CS8997
BAD_RAW = '''class A {
    string Sql() => """UPDATE "T" SET "a" = 1
       WHERE "b" = 2;""";
}'''

# raw ถูกกฎ: บรรทัดเดียว (มีเนื้อหาต่อท้ายได้) และหลายบรรทัดที่ขึ้นบรรทัดใหม่
GOOD_RAW = '''class A {
    string One() => """UPDATE "T" SET "a" = 1 WHERE "b" = 2;""";
    string Many() => """
        UPDATE "T" SET "a" = 1
        WHERE "b" = 2;
        """;
}'''


def self_test():
    ok = True
    if scan_text(GOOD):
        print("❌ self-test: false positive บนโค้ดที่ถูกต้อง")
        for p in scan_text(GOOD):
            print("   ", p)
        ok = False
    else:
        print("✅ self-test: โค้ดที่ถูกต้อง ไม่ฟ้อง")
    bad = scan_text(BAD)
    if not bad:
        print("❌ self-test: จับบั๊กที่ตั้งใจให้จับไม่ได้ (negative test ไม่ผ่าน)")
        ok = False
    else:
        print(f"✅ self-test: จับบั๊กจริงได้ ({bad[0][2]!r})")

    if not scan_text(BAD_RAW):
        print("❌ self-test: raw string หลายบรรทัดผิดกฎ — จับไม่ได้")
        ok = False
    else:
        print("✅ self-test: จับ raw string หลายบรรทัดที่เปิดผิดกฎได้ (CS8997)")
    if scan_text(GOOD_RAW):
        print("❌ self-test: ฟ้องผิดบน raw string ที่ถูกกฎ")
        for p in scan_text(GOOD_RAW):
            print("   ", p)
        ok = False
    else:
        print("✅ self-test: raw string ที่ถูกกฎ ไม่ฟ้อง")
    return 0 if ok else 1


def main():
    if "--self-test" in sys.argv:
        return self_test()

    hits, count = [], 0
    for base, dirs, files in os.walk(ROOT):
        dirs[:] = [d for d in dirs if d not in SKIP_DIRS]
        for f in files:
            if f.endswith(".cs"):
                count += 1
                hits.extend(scan_file(os.path.join(base, f)))

    print()
    for path, ln, col, snip in hits:
        rel = os.path.relpath(path, ROOT)
        print(f'{rel}:{ln} — `"` ปิด verbatim string ก่อนเวลา แล้วมีโค้ดค้าง: {snip}')
        print('    แก้: ใช้ `""` (สอง quote) หรือเปลี่ยนเป็นอัญประกาศไทย “ ” ในคอมเมนต์')
    print(f"\nตรวจ {count} ไฟล์ · พบปัญหา {len(hits)} จุด")
    return 1 if hits else 0


if __name__ == "__main__":
    sys.exit(main())
