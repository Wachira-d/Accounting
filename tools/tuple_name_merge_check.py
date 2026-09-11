#!/usr/bin/env python3
# -*- coding: utf-8 -*-
"""tuple_name_merge_check.py — ternary ที่สองสาขาเป็น tuple ชื่อไม่ตรงกัน = ชื่อหายเงียบ

ที่มา (ของจริง — หลุดไปถึง build ฝั่งผู้ใช้ 2026-09-11):
    var receiptDocs = payIds.Count == 0
        ? new List<(Guid Id, string Number, Guid? PayId)>()
        : (await ...).Select(r => (r.Id, r.DocumentNumber, r.SettlementPaymentId)).ToList();
    ...
    receiptDocs.Where(r => r.PayId.HasValue)      // ← CS1061

สาขาแรกประกาศชื่อ `Id/Number/PayId` · สาขาที่สอง**อนุมาน**ชื่อจาก member access ได้
`Id/DocumentNumber/SettlementPaymentId` ⇒ C# หา common type ของสองสาขาแล้ว **ทิ้งชื่อ
ตำแหน่งที่ไม่ตรงกัน** ได้ `(Guid Id, string, Guid?)` — บรรทัดที่ประกาศ**ไม่ error เลย**
error ไปโผล่ที่จุดใช้ชื่อซึ่งอยู่ห่างออกไป จึงอ่านแล้วงงว่าทำไมชื่อที่พิมพ์ไว้ชัด ๆ หายไป

ทำไม checker เดิมจับไม่ได้: ทุกตัวอ่านโครงสร้างระดับอื่น (using · ชนิดอาร์กิวเมนต์ ·
string literal) ไม่มีตัวไหนเทียบ "ชื่อที่ประกาศ" กับ "ชื่อที่อนุมานได้" ของ tuple —
และ**ไม่ต้อง resolve type** เพราะกติกาอนุมานชื่อ tuple เป็นเรื่อง syntax ล้วน
(ชื่อ = ตัวระบุตัวสุดท้ายของ member access) จึงเขียนได้แม่นโดยไม่ชน "กำแพง type
resolution" ที่ทำให้ checker สองตัวก่อนหน้านี้ถูกทิ้ง

ขอบเขต: เฉพาะรูป `cond ? new List<( ... )>() : ....Select(x => ( ... )).ToList();`
ซึ่งเป็นรูปที่เรพนี้ใช้จริงทั้ง 3 จุด — แคบไว้ดีกว่าฟ้องผิด

รันเปล่า ๆ = ตรวจทั้งเรพ · `--self-test` = ทดสอบตัว checker เอง
"""
import os
import re
import sys

ROOT = os.path.dirname(os.path.dirname(os.path.abspath(__file__)))
IDENT = r"[A-Za-z_][A-Za-z0-9_]*"


def strip_noise(text):
    """ตัดคอมเมนต์ + string literal (คงจำนวนบรรทัดไว้ให้เลขบรรทัดตรง)"""
    out, i, n = [], 0, len(text)
    while i < n:
        c = text[i]
        if c == "/" and i + 1 < n and text[i + 1] == "/":
            j = text.find("\n", i)
            i = n if j < 0 else j
        elif c == "/" and i + 1 < n and text[i + 1] == "*":
            j = text.find("*/", i + 2)
            j = n if j < 0 else j + 2
            out.append("\n" * text.count("\n", i, j))
            i = j
        elif c == '"':
            if text.startswith('"""', i):
                j = text.find('"""', i + 3)
                j = n if j < 0 else j + 3
            else:
                j = i + 1
                while j < n and text[j] != '"':
                    j += 2 if text[j] == "\\" else 1
                j = min(j + 1, n)
            out.append("\n" * text.count("\n", i, j))
            i = j
        else:
            out.append(c)
            i += 1
    return "".join(out)


def split_top(s, opens="(<[", closes=")>]"):
    """แยกด้วย ',' ที่ระดับนอกสุด (ไม่แตะที่อยู่ใน <> () [])"""
    parts, depth, cur = [], 0, []
    for ch in s:
        if ch in opens:
            depth += 1
        elif ch in closes:
            depth -= 1
        if ch == "," and depth == 0:
            parts.append("".join(cur)); cur = []
        else:
            cur.append(ch)
    parts.append("".join(cur))
    return [p.strip() for p in parts if p.strip()]


def declared_names(inner):
    """`Guid Id, string Number, Guid? PayId` → ['Id','Number','PayId']
    ตัวที่ไม่ได้ตั้งชื่อ (มีแต่ชนิด) → None"""
    names = []
    for el in split_top(inner):
        toks = re.findall(IDENT, el.replace("?", " "))
        # "Guid Id" → 2 token · "string" → 1 token (ไม่มีชื่อ)
        # ชนิด generic "List<int> X" ยุบ <> ก่อนนับ
        flat = re.sub(r"<[^<>]*>", "", el)
        toks = re.findall(IDENT, flat.replace("?", " "))
        names.append(toks[-1] if len(toks) >= 2 else None)
    return names


def inferred_names(inner):
    """`r.Id, r.DocumentNumber, Foo(x)` → ['Id','DocumentNumber',None]
    รองรับชื่อที่เขียนชัด `Name: expr`"""
    names = []
    for el in split_top(inner):
        m = re.match(rf"^({IDENT})\s*:(?!:)", el)
        if m:
            names.append(m.group(1)); continue
        # member access ล้วน ๆ เท่านั้นที่อนุมานชื่อได้ (ห้ามมีตัวดำเนินการ/วงเล็บ)
        if re.fullmatch(rf"{IDENT}(?:\.{IDENT})+", el):
            names.append(el.split(".")[-1])
        elif re.fullmatch(IDENT, el):
            names.append(el)
        else:
            names.append(None)
    return names


def match_close(text, i, open_ch, close_ch):
    """i ชี้ที่ตัวเปิด — คืน index ของตัวปิดที่คู่กัน (หรือ -1)"""
    depth = 0
    while i < len(text):
        if text[i] == open_ch:
            depth += 1
        elif text[i] == close_ch:
            depth -= 1
            if depth == 0:
                return i
        i += 1
    return -1


TERNARY = re.compile(r"\?\s*new\s+List<\(")
SELECT_TUPLE = re.compile(rf"\.Select\(\s*{IDENT}\s*=>\s*\(")


def check_text(text):
    """คืน list ของ (บรรทัด, declared, inferred)"""
    body = strip_noise(text)
    problems = []
    for m in TERNARY.finditer(body):
        lt = body.index("<", m.start())
        gt = match_close(body, lt, "<", ">")
        if gt < 0:
            continue
        inner = body[lt + 1:gt].strip()
        if not (inner.startswith("(") and inner.endswith(")")):
            continue                       # ไม่ใช่ List<tuple> — ข้าม
        decl = declared_names(inner[1:-1])
        if not any(decl):
            continue                       # ไม่ได้ตั้งชื่อเลย = ไม่มีอะไรให้หาย
        # สาขา else = ตั้งแต่ ':' ที่คู่กัน จนจบคำสั่ง
        j, depth = gt, 0
        colon = -1
        while j < len(body):
            ch = body[j]
            if ch in "([{":
                depth += 1
            elif ch in ")]}":
                depth -= 1
            elif ch == ":" and depth == 0 and body[j:j + 2] != "::":
                colon = j; break
            elif ch == ";" and depth == 0:
                break
            j += 1
        if colon < 0:
            continue
        end = body.find(";", colon)
        else_branch = body[colon + 1:end if end > 0 else len(body)]
        sels = list(SELECT_TUPLE.finditer(else_branch))
        if not sels:
            continue
        s2 = sels[-1]
        op = else_branch.index("(", s2.end() - 1)
        cp = match_close(else_branch, op, "(", ")")
        if cp < 0:
            continue
        inf = inferred_names(else_branch[op + 1:cp])
        if len(inf) != len(decl) or any(d != i2 for d, i2 in zip(decl, inf)):
            problems.append((body.count("\n", 0, m.start()) + 1, decl, inf))
    return problems


def scan(root=None):
    root = root or ROOT
    found, total = [], 0
    for dirpath, dirnames, filenames in os.walk(root):
        dirnames[:] = [d for d in dirnames if d not in {"bin", "obj", ".git", "node_modules"}]
        for f in filenames:
            if not f.endswith(".cs"):
                continue
            total += 1
            path = os.path.join(dirpath, f)
            try:
                txt = open(path, encoding="utf-8", errors="replace").read()
            except OSError:
                continue
            for line, decl, inf in check_text(txt):
                found.append((os.path.relpath(path, root), line, decl, inf))
    return total, found


def self_test():
    bug = """class X { void M() {
        var receiptDocs = payIds.Count == 0
            ? new List<(Guid Id, string Number, Guid? PayId)>()
            : rows.Select(r => (r.Id, r.DocumentNumber, r.SettlementPaymentId)).ToList();
    } }"""
    good = """class X { void M() {
        var lines = depAcctIds.Count == 0
            ? new List<(Guid AccountId, decimal Net, bool HasDoc)>()
            : rows.Select(x => (x.AccountId, x.Net, x.HasDoc)).ToList();
        var named = ids.Count == 0
            ? new List<(Guid Id, string Number)>()
            : rows.Select(r => (Id: r.Id, Number: r.DocumentNumber)).ToList();
        var plain = ids.Count == 0
            ? new List<Guid>()
            : rows.Select(r => r.Id).ToList();
    } }"""
    comment = '''class X { void M() {
        // ? new List<(Guid Id, string Number)>() : rows.Select(r => (r.A, r.B)).ToList();
        var ok = 1;
    } }'''
    ok = True
    b = check_text(bug)
    if len(b) != 1 or b[0][1] != ["Id", "Number", "PayId"]:
        print("❌ self-test: บั๊กจริงไม่ถูกฟ้อง", b); ok = False
    else:
        print("✅ self-test: ternary ที่ชื่อ tuple ไม่ตรงกัน ถูกฟ้อง")
    g = check_text(good)
    if g:
        print("❌ self-test: ฟ้องผิดบนรูปที่ถูกต้อง", g); ok = False
    else:
        print("✅ self-test: ชื่อตรงกัน / ตั้งชื่อชัด / ไม่ใช่ tuple → ไม่ฟ้อง")
    c = check_text(comment)
    if c:
        print("❌ self-test: ฟ้องข้อความในคอมเมนต์", c); ok = False
    else:
        print("✅ self-test: คอมเมนต์ไม่ถูกนับ")
    return 0 if ok else 1


def main():
    if "--self-test" in sys.argv:
        return self_test()
    total, found = scan()
    print()
    for rel, line, decl, inf in found:
        print(f"{rel}:{line}: ternary คร่อม tuple ที่ชื่อไม่ตรงกัน — C# จะทิ้งชื่อที่ไม่ตรง")
        print(f"    ประกาศไว้ : {decl}")
        print(f"    อนุมานได้ : {inf}")
        print("    → ประกาศชนิดไว้ตรง ๆ แล้วเติมทีหลัง (เลี่ยง ternary) หรือตั้งชื่อในสาขา else ให้ตรงกัน")
    print(f"\nตรวจ {total} ไฟล์ .cs · ternary ที่ทำให้ชื่อ tuple หาย {len(found)} จุด")
    return 1 if found else 0


if __name__ == "__main__":
    sys.exit(main())
