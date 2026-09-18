#!/usr/bin/env python3
"""ตรวจ "ช่องว่างในชื่อ method/ชื่อคลาส" — CS1001/CS1003 ล้มทั้ง solution.

ที่มา: โปรเจกต์นี้ตั้งชื่อเทสต์เป็น**ภาษาไทย** (`AuditHashChainTests` ฯลฯ) ซึ่ง
อ่านง่ายมาก แต่ทำให้พลาดง่ายเป็นพิเศษ — ภาษาไทยเขียนติดกันไม่มีช่องว่าง ยกเว้น
ตอนแทรกศัพท์อังกฤษ เช่น

    public void แก้ field ใดภายหลัง_ตรวจต้องไม่ผ่าน(string field)
                    ^^^^^^^ ช่องว่าง = จบชื่อ identifier ตรงนี้

คอมไพเลอร์อ่านเป็น "return type = แก้", "ชื่อ = field" แล้วเจอ token เกิน ⇒
CS1001 "Identifier expected" / CS1003 ลามทั้งไฟล์ และเพราะ `Accounting.Tests`
อ้าง `Accounting.dll` ⇒ ล้มทั้ง solution (defect class เดิมในกฎเหล็ก #4 F)

ไม่มี checker ตัวไหนใน 11 ตัวเดิมมองเห็น: ทุกตัวอ่านโครงสร้างโค้ดระดับสูงกว่า
(DI graph, argument type, string literal) ไม่มีตัวไหนตรวจ "ชื่อ" ระดับ token

วิธีตรวจ: หาบรรทัดที่เป็นการ**ประกาศ** method/คลาส แล้วนับ token ก่อนวงเล็บเปิด
หลังตัดตัวขยาย (public/static/async/…) ออก — ต้องเหลือ [return type][ชื่อ] = 2
token พอดี (คลาส/record/enum = 1) มากกว่านั้นแปลว่ามีช่องว่างอยู่ในชื่อ

ทรงที่สอง (รอบ 170c — CI run 162 จับได้): **ตัวอักษรที่ไม่ใช่ตัวอักษร/ตัวเลข/_
ในชื่อ** เช่น `เพดานไม่เกิน_§49_วรรคท้าย()` — `§` ไม่ใช่ identifier char ⇒ CS1056
"Unexpected character" + CS1013 + CS1002 ล้ม `Accounting.Tests`. ชื่อไทยทำให้พลาด
ง่ายอีกครั้ง เพราะสายตาอ่าน "§49" เป็นคำเดียวกับข้อความรอบ ๆ. ตรวจเฉพาะ **token ชื่อ**
(ตัวสุดท้ายก่อน `(` / ชื่อชนิด) หลังตัด generic `<…>` ทิ้ง — อนุญาต `\w` `.` (explicit
interface impl) `@` (verbatim identifier) `~` (destructor)
"""
import re
import sys
import unicodedata
from pathlib import Path

ROOT = Path(__file__).resolve().parent.parent

MODIFIERS = {
    "public", "private", "protected", "internal", "static", "async", "override",
    "virtual", "sealed", "abstract", "extern", "new", "partial", "unsafe",
    "readonly", "const", "ref", "required", "file",
}
TYPE_KEYWORDS = {"class", "struct", "interface", "record", "enum"}

# บรรทัดที่ "อาจ" เป็นการประกาศ method: ขึ้นต้นด้วยตัวขยายการเข้าถึง แล้วมี (
DECL = re.compile(r"^\s*(?:public|private|protected|internal)\s+(?P<body>.+)$")


def collapse_generics(s: str) -> str:
    """ยุบช่องว่างใน <...> และ [...] — `Dictionary<string, int>` เป็น token เดียว"""
    out, depth = [], 0
    for ch in s:
        if ch in "<[":
            depth += 1
        elif ch in ">]":
            depth = max(0, depth - 1)
        if ch.isspace() and depth > 0:
            continue
        out.append(ch)
    return "".join(out)


# ตัวอักษรที่ใช้ในชื่อได้ตามสเปก C# (identifier-part-character): หมวด Unicode
# L* (ตัวอักษร) · Nl · Nd (ตัวเลข) · Mn/Mc (**สระ/วรรณยุกต์ไทยอยู่หมวดนี้** —
# `str.isalnum()`/`\w` ตอบว่าไม่ใช่ตัวอักษร ⇒ ใช้ไม่ได้ ฟ้องผิดทุกชื่อไทย) · Pc (_) · Cf
# บวกที่อนุญาตเป็นพิเศษ: `.` ของ explicit interface impl (`IFoo.Bar(`) · `@` verbatim
# identifier · `~` destructor
IDENT_CATEGORIES = {"Lu", "Ll", "Lt", "Lm", "Lo", "Nl", "Nd", "Mn", "Mc", "Pc", "Cf"}


def strip_generic(name: str) -> str:
    """`Foo<T>` → `Foo` · `Dictionary<string,int>` → `Dictionary` (ชื่อจริงอยู่ก่อน <)"""
    i = name.find("<")
    return name[:i] if i >= 0 else name


def bad_name_char(name: str):
    """คืนตัวอักษรตัวแรกที่ใช้ในชื่อไม่ได้ หรือ None"""
    core = strip_generic(name)
    for i, ch in enumerate(core):
        if ch in ".@" or (ch == "~" and i == 0):
            continue
        if unicodedata.category(ch) not in IDENT_CATEGORIES:
            return ch
    return None


def check_file(path: Path):
    problems = []
    for lineno, raw in enumerate(path.read_text(encoding="utf-8", errors="replace").splitlines(), 1):
        line = raw.split("//")[0]
        m = DECL.match(line)
        if not m:
            continue
        body = collapse_generics(m.group("body"))

        if " operator " in body:
            continue                       # `operator ==` — ตัดที่ = ไม่ได้

        # สนใจเฉพาะ "ส่วนหัว" ก่อนตัวใดตัวหนึ่งใน ( = {  แล้วแต่ตัวไหนมาก่อน:
        #   (  = เริ่มพารามิเตอร์          →  `void Foo(`
        #   =  = เริ่มค่าเริ่มต้นของ field  →  `Guid Acc = Guid.NewGuid();`
        #   {  = เริ่มบอดี้/ตัวสมาชิก enum  →  `enum Nature { A, B }`
        # ถ้าไม่ตัด `=` ทิ้งก่อน วงเล็บใน `Guid.NewGuid()` จะถูกนับเป็นพารามิเตอร์
        cuts = [i for i in (body.find("("), body.find("="), body.find("{")) if i >= 0]
        paren = body.find("(")
        head = body[:min(cuts)] if cuts else body
        # method จริงต้องมี `(` มาก่อน `=`/`{` เท่านั้น
        is_method = paren >= 0 and paren == min(cuts)
        tokens = [t for t in head.split() if t]
        if not tokens:
            continue

        # ตัดตัวขยายออกให้หมด
        while tokens and tokens[0] in MODIFIERS:
            tokens.pop(0)
        if not tokens:
            continue

        if tokens[0] in TYPE_KEYWORDS:
            # ประกาศชนิด: `class Foo`, `record Bar(…)`, `enum E` → เหลือชื่อ 1 token
            # `: BaseX` (สืบทอด) ตัดทิ้งก่อน
            name_tokens = head.split(":")[0].split()
            while name_tokens and name_tokens[0] in MODIFIERS:
                name_tokens.pop(0)
            name_tokens = name_tokens[1:]
            # `record struct Foo` / `record class Foo` — คำนำหน้าสองคำ
            if name_tokens and name_tokens[0] in {"struct", "class"}:
                name_tokens = name_tokens[1:]
            if len(name_tokens) > 1:
                problems.append((lineno, raw.strip(), "ชื่อชนิดมีช่องว่าง"))
            elif name_tokens:
                bad = bad_name_char(name_tokens[0].split("(")[0])
                if bad is not None:
                    problems.append((lineno, raw.strip(), f"ชื่อชนิดมีตัวอักษรที่ใช้ไม่ได้ '{bad}' (CS1056)"))
            continue

        if not is_method:
            continue                       # property/field/expression-bodied
        if not head.strip():
            continue                       # tuple return type `(int a, string b) Foo(`

        # ถึงตรงนี้ = ประกาศ method/ctor: ต้องเหลือ [return type][ชื่อ] หรือ [ชื่อ ctor]
        if len(tokens) > 2:
            problems.append((lineno, raw.strip(), "ชื่อ method มีช่องว่าง"))
            continue
        bad = bad_name_char(tokens[-1])
        if bad is not None:
            problems.append((lineno, raw.strip(), f"ชื่อ method มีตัวอักษรที่ใช้ไม่ได้ '{bad}' (CS1056)"))
    return problems


def main():
    targets = sorted(
        p for p in ROOT.rglob("*.cs")
        if not any(part in {"bin", "obj", ".git"} for part in p.parts)
    )
    if len(sys.argv) > 1:                  # โหมดทดสอบ: ระบุไฟล์เอง
        targets = [Path(a) for a in sys.argv[1:]]

    total = 0
    for path in targets:
        for lineno, text, why in check_file(path):
            total += 1
            rel = path.relative_to(ROOT) if ROOT in path.parents else path
            print(f"{rel}:{lineno}: {why}\n    {text}")

    print(f"\nตรวจ {len(targets)} ไฟล์ .cs · ชื่อที่มีช่องว่าง/ตัวอักษรต้องห้าม {total} จุด")
    return 1 if total else 0


if __name__ == "__main__":
    sys.exit(main())
