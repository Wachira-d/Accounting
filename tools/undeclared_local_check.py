#!/usr/bin/env python3
"""ตรวจ **ตัวแปรที่ถูกใช้เป็นอาร์กิวเมนต์แต่ไม่เคยประกาศ** ในเมธอดนั้น → CS0103

ที่มา (เคสจริง): ตอนเพิ่มพารามิเตอร์ `issuer` ให้ `ComposeFooter` ผมไปเติม
อาร์กิวเมนต์ที่จุดเรียกใน `RenderDocumentPdfNative` ว่า
`ComposeFooter(col, doc, template, accent, L, issuer)` โดยเข้าใจว่าเมธอดนั้นมี
ตัวแปร `issuer` อยู่แล้ว — **แต่มันอยู่คนละเมธอด** (`ComposeHeaderAndTitle`
คำนวณเองข้างใน) ⇒ CS0103 "The name 'issuer' does not exist in the current
context" ล้มทั้ง solution (Accounting.Tests พังตามด้วย CS0006)

ทำไม checker 14 ตัวเดิมมองไม่เห็น: ทุกตัวอ่านโครงสร้างระดับสูงกว่า (DI graph,
ชนิดอาร์กิวเมนต์, string literal, ชื่อ token) — ไม่มีตัวไหนถามว่า "ชื่อนี้ประกาศ
ไว้ที่ไหน". `arg_type_check` ดูชนิด แต่ต้องมีตัวแปรอยู่จริงก่อนถึงจะดูชนิดได้

กติกา (ตั้งใจแคบเพื่อไม่ให้ฟ้องผิด):
  ฟ้องเฉพาะ identifier ที่
    · ปรากฏเป็น **อาร์กิวเมนต์เดี่ยว ๆ** ในการเรียกเมธอด (ไม่ใช่ `a.b`, ไม่ใช่นิพจน์)
    · เป็น camelCase (ขึ้นต้นตัวพิมพ์เล็ก) — ตัดชื่อชนิด/ค่าคงที่ออก
    · **ไม่ใช่** พารามิเตอร์ของเมธอดนั้น
    · **ไม่ใช่** ตัวแปรที่ประกาศที่ไหนก็ได้ในเมธอดนั้น (var/ชนิด/out/foreach/
      using/catch/pattern/lambda param/deconstruction/query range variable)
    · **ไม่ใช่** field/property/const ของคลาส (รวมคลาส partial ทั้งไฟล์)
  ที่เหลือ (this.x, ชื่อชนิด, enum, namespace) ไม่แตะ
"""
import re
import sys
from pathlib import Path

ROOT = Path(__file__).resolve().parent.parent

# ── ตัดคอมเมนต์ + string ให้เหลือแต่โค้ด (คงจำนวนบรรทัด) ────────────────
def strip_noise(text: str) -> str:
    out = []
    i, n = 0, len(text)
    while i < n:
        c = text[i]
        nxt = text[i + 1] if i + 1 < n else ""
        if c == "/" and nxt == "/":
            j = text.find("\n", i)
            i = n if j < 0 else j
            continue
        if c == "/" and nxt == "*":
            j = text.find("*/", i + 2)
            j = n if j < 0 else j + 2
            out.append("\n" * text.count("\n", i, j))
            i = j
            continue
        if c == '"':
            # raw string """ … """
            if text.startswith('"""', i):
                q = 3
                while i + q < n and text[i + q] == '"':
                    q += 1
                close = '"' * q
                j = text.find(close, i + q)
                j = n if j < 0 else j + q
                out.append("\n" * text.count("\n", i, j))
                i = j
                continue
            verbatim = i > 0 and text[i - 1] == "@"
            j = i + 1
            while j < n:
                if verbatim:
                    if text[j] == '"':
                        if j + 1 < n and text[j + 1] == '"':
                            j += 2
                            continue
                        j += 1
                        break
                else:
                    if text[j] == "\\":
                        j += 2
                        continue
                    if text[j] == '"' or text[j] == "\n":
                        j += 1
                        break
                j += 1
            out.append("\n" * text.count("\n", i, j))
            i = j
            continue
        if c == "'":
            j = i + 1
            while j < n:
                if text[j] == "\\":
                    j += 2
                    continue
                if text[j] == "'":
                    j += 1
                    break
                j += 1
            i = j
            continue
        out.append(c)
        i += 1
    return "".join(out)


IDENT = r"[A-Za-z_][A-Za-z0-9_]*"
# หัวเมธอด: `... ชื่อ(พารามิเตอร์) {`  (ตัดตัวที่เป็น if/for/while/... ออก)
# `var (a, b) = …` · `typeof(T)` · `nameof(x)` — ไม่ใช่การเรียกเมธอด
NOT_A_CALL = {"var", "typeof", "nameof", "sizeof", "new", "checked", "unchecked",
              "stackalloc", "default", "await", "throw",
              # pattern combinator: `is not (A or B)` ไม่ใช่การเรียกเมธอด
              "not", "and", "or", "is"}

KEYWORDS_STMT = {
    "if", "for", "foreach", "while", "switch", "catch", "using", "lock",
    "fixed", "return", "yield", "do", "else", "try", "finally", "when",
}
METHOD_HEAD = re.compile(
    r"^[ \t]*(?:\[[^\]]*\][ \t]*)*"
    r"(?:(?:public|private|protected|internal|static|async|override|virtual|sealed|new|partial|extern|unsafe)[ \t]+)+"
    rf"[^;=(){{}}]*?\b({IDENT})[ \t]*(<[^()]*>)?[ \t]*\(",
    re.M,
)

DECL_PATTERNS = [
    # var x = …  /  Type x = …  /  Type<T> x = …
    rf"\b(?:var|[A-Za-z_][\w\.<>,\[\]\?]*)\s+({IDENT})\s*(?:=[^=]|;|,)",
    rf"\bout\s+(?:var|[A-Za-z_][\w\.<>,\[\]\?]*)\s+({IDENT})\b",
    rf"\bforeach\s*\(\s*(?:var|[A-Za-z_][\w\.<>,\[\]\?]*)\s+({IDENT})\b",
    rf"\bcatch\s*\([^)]*\s+({IDENT})\s*\)",
    rf"\bis\s+[A-Za-z_][\w\.<>,\[\]\?]*\s+({IDENT})\b",          # pattern: is Foo f
    # รอบ 193: `x is not decimal t` (ผูก t ในทางที่เงื่อนไขเป็นเท็จ — C# 9) — แถวบนจับ "not" เป็นชนิดแล้วได้ "decimal"
    # เป็นชื่อตัวแปร ⇒ เคยฟ้องผิด `t` ใน Helpers/OcrSettlementProposal ทั้งที่คอมไพล์ผ่าน
    rf"\bis\s+not\s+[A-Za-z_][\w\.<>,\[\]\?]*\s+({IDENT})\b",   # pattern: is not Foo f
    rf"\bis\s*\{{[^{{}}]*\}}\s*({IDENT})\b",                       # property pattern: is { } f
    # ตัวแปรที่ผูกใน **subpattern ซ้อน** ของ recursive pattern:
    #   `x is { Matched: true, Status: { } s }`  ·  `x is { Inner: Foo f }`
    # ต่างจากบรรทัดบนตรงที่ไม่มี `is` นำหน้าติด ๆ (มันอยู่ลึกเข้าไปในวงเล็บปีกกา)
    # — เคยฟ้องผิดกับโค้ดที่ถูกต้อง (OcrScanComplianceEvaluator)
    rf"\b{IDENT}\s*:\s*\{{[^{{}}]*\}}\s*({IDENT})\b",              # nested: Status: { } s
    rf"\b{IDENT}\s*:\s*[A-Za-z_][\w\.<>,\[\]\?]*\s+({IDENT})\s*[,}}]",  # nested: Status: string s
    rf"\bfrom\s+({IDENT})\s+in\b",                                # LINQ range var
    rf"\blet\s+({IDENT})\s*=",
]
DECL_RES = [re.compile(p) for p in DECL_PATTERNS]
# **over-approximate โดยตั้งใจ**: ชื่อที่ถูก "กำหนดค่า" ที่ไหนก็ตามในเมธอด ถือว่า
# ประกาศแล้ว — ครอบ multi-declarator (`decimal a = 0, b = 0;`), tuple, pattern
# แปลก ๆ ที่ regex ไล่ไม่หมด. เราสนใจเฉพาะ CS0103 = "ไม่มีอยู่เลย" การเผื่อฝั่งนี้
# จึงไม่ทำให้พลาดบั๊กจริง (ตัวที่พลาดจะไม่ถูกกำหนดค่าที่ไหนเลยอยู่แล้ว)
ASSIGNED = re.compile(rf"(?<![\w.])({IDENT})\s*=(?![=>])")
# lambda: `x =>` และ `(x, y) =>`
LAMBDA1 = re.compile(rf"(?<![\w.])({IDENT})\s*=>")
LAMBDA_N = re.compile(rf"\(([^()]*)\)\s*=>")
# deconstruction / tuple: `var (a, b) =`
DECONSTRUCT = re.compile(r"\bvar\s*\(([^()]*)\)\s*=")
# `foreach (var (key, title, content) in …)` — deconstruction ใน foreach
FOREACH_TUPLE = re.compile(r"\bforeach\s*\(\s*var\s*\(([^()]*)\)\s*in\b")

# อาร์กิวเมนต์เดี่ยว: `(a, b)` / `, name)` — เอาเฉพาะที่เป็น identifier ล้วน
CALL = re.compile(rf"(?<![\w.])({IDENT})\s*\(([^()]*)\)")


def split_args(s: str):
    depth = 0
    cur = ""
    for ch in s:
        if ch in "<[":
            depth += 1
        elif ch in ">]":
            depth = max(0, depth - 1)
        if ch == "," and depth == 0:
            yield cur
            cur = ""
        else:
            cur += ch
    yield cur


def method_bodies(src: str):
    """หา "บล็อกที่มีพารามิเตอร์" ทุกตัวในไฟล์ — เมธอด · constructor · local function

    ไม่ใช้ regex จับหัวเมธอด (พังกับ return type ที่เป็น tuple `(bool Ok, string R)`
    ซึ่งมีวงเล็บอยู่ในตัวเอง — รุ่นแรกจับผิดตัวแล้วฟ้องพารามิเตอร์ของเมธอดอื่น 40+ จุด)
    แต่ scan หา `(` ที่ปิดแล้วตามด้วย `{` และมี identifier อยู่ข้างหน้าแทน
    คืน (ชื่อ, บรรทัด, ตัวเมธอด, พารามิเตอร์, offset ของ `{`)
    """
    n = len(src)
    i = 0
    # local function ที่ซ้อนอยู่ในเมธอด **ห้าม yield แยก** — ตัวมันมองเห็นตัวแปร
    # ของเมธอดแม่ (closure) ถ้าแยกออกมาตรวจจะฟ้องตัวแปรที่ capture มาทุกตัว
    # (รุ่นก่อนหน้าฟ้อง `win`/`memoLc` ใน BulkBankAiMatchService ด้วยเหตุนี้)
    scanned_until = -1
    while i < n:
        if src[i] != "(":
            i += 1
            continue
        # หา ) ที่คู่กัน
        depth, j = 0, i
        while j < n:
            if src[j] == "(":
                depth += 1
            elif src[j] == ")":
                depth -= 1
                if depth == 0:
                    break
            j += 1
        if j >= n:
            break
        # ข้าม where-clause ของ generic ก่อนถึง {
        k = j + 1
        while k < n and (src[k].isspace() or src[k] == "\n"):
            k += 1
        # `where T : class` ระหว่าง ) กับ {
        if src.startswith("where", k):
            b = src.find("{", k)
            g = src.find(";", k)
            if b < 0 or (0 <= g < b):
                i += 1
                continue
            k = b
        if k >= n or src[k] != "{":
            i += 1
            continue
        if k < scanned_until:      # อยู่ในเมธอดที่ตรวจไปแล้ว = local function
            i += 1
            continue
        # identifier ก่อน ( (ข้ามช่องว่าง) — ต้องไม่ใช่คีย์เวิร์ดคำสั่ง
        p2 = i - 1
        while p2 >= 0 and src[p2].isspace():
            p2 -= 1
        e2 = p2
        while p2 >= 0 and (src[p2].isalnum() or src[p2] == "_"):
            p2 -= 1
        name = src[p2 + 1:e2 + 1]
        if not name or name in KEYWORDS_STMT or name in NOT_A_CALL:
            i += 1
            continue
        # `new Foo(...) { … }` = object initializer ไม่ใช่เมธอด
        q = p2
        while q >= 0 and src[q].isspace():
            q -= 1
        if src[max(0, q - 2):q + 1] == "new":
            i += 1
            continue
        params = src[i + 1:j]
        depth, e = 0, k
        while e < n:
            if src[e] == "{":
                depth += 1
            elif src[e] == "}":
                depth -= 1
                if depth == 0:
                    break
            e += 1
        scanned_until = e
        yield name, src.count("\n", 0, i) + 1, src[k:e + 1], params, k
        i = j + 1


def class_members(src: str):
    """field/property/const ระดับคลาส — ทั้งไฟล์ (partial อยู่คนละไฟล์เราไม่ตาม
    แต่ชื่อสมาชิกมักขึ้นต้นด้วย _ หรือพิมพ์ใหญ่ จึงถูกตัดด้วยกฎ camelCase อยู่แล้ว)"""
    names = set()
    for m in re.finditer(
            rf"^[ \t]*(?:(?:public|private|protected|internal|static|readonly|const|volatile|required|new)[ \t]+)+"
            rf"[A-Za-z_][\w\.<>,\[\]\?]*\s+({IDENT})\s*(?:=|;|\{{|=>)", src, re.M):
        names.add(m.group(1))
    return names


def declared_in(body: str, params: str):
    names = set()
    for p in split_args(params):
        p = p.strip()
        if not p:
            continue
        p = re.sub(r"=.*$", "", p).strip()          # default value
        toks = re.findall(IDENT, p)
        if toks:
            names.add(toks[-1])
    for rx in DECL_RES:
        for m in rx.finditer(body):
            names.add(m.group(1))
    for m in LAMBDA1.finditer(body):
        names.add(m.group(1))
    for m in LAMBDA_N.finditer(body):
        for p in split_args(m.group(1)):
            toks = re.findall(IDENT, p)
            if toks:
                names.add(toks[-1])
    for m in DECONSTRUCT.finditer(body):
        names.update(re.findall(IDENT, m.group(1)))
    for m in FOREACH_TUPLE.finditer(body):
        names.update(re.findall(IDENT, m.group(1)))
    for m in ASSIGNED.finditer(body):
        names.add(m.group(1))
    # local function ที่ประกาศในตัวเมธอด — ใช้ตัว scan เดียวกับเมธอด เพื่อให้
    # พารามิเตอร์ชนิด tuple `(decimal Inc, decimal Tax) m` ถูกอ่านถูกด้วย
    for _n, _l, _b, nested_params, _k in method_bodies(body):
        for p in split_args(nested_params):
            p = re.sub(r"=.*$", "", p).strip()
            toks = re.findall(IDENT, p)
            if toks:
                names.add(toks[-1])
    for m in LAMBDA_N.finditer(body):
        for p in split_args(m.group(1)):
            toks = re.findall(IDENT, p)
            if toks:
                names.add(toks[-1])
    return names


def main():
    files = sorted(p for p in ROOT.rglob("*.cs")
                   if not any(part in {"obj", "bin", ".claude"} for part in p.parts))
    problems = []
    for f in files:
        raw = f.read_text(encoding="utf-8", errors="replace")
        src = strip_noise(raw)
        members = class_members(src)
        for name, headline, body, params, body_off in method_bodies(src):
            declared = declared_in(body, params)
            for cm in CALL.finditer(body):
                callee, argstr = cm.group(1), cm.group(2)
                if callee in NOT_A_CALL or callee in KEYWORDS_STMT or not argstr.strip():
                    continue
                for arg in split_args(argstr):
                    a = arg.strip()
                    # เอาเฉพาะ `name` และ `label: name`
                    m2 = re.fullmatch(rf"(?:{IDENT}\s*:\s*)?({IDENT})", a)
                    if not m2:
                        continue
                    v = m2.group(1)
                    if not v[0].islower():          # ชื่อชนิด/ค่าคงที่ — ไม่แตะ
                        continue
                    if v in declared or v in members:
                        continue
                    if v in {"value", "this", "base", "true", "false", "null",
                             "args", "sender", "e", "ex", "nameof", "default"}:
                        continue
                    line = src.count("\n", 0, body_off + cm.start()) + 1
                    problems.append((f, line, v, callee))

    for f, line, v, callee in problems:
        rel = f.relative_to(ROOT)
        print(f"{rel}:{line}: ส่ง `{v}` เข้า {callee}(...) แต่ไม่เคยประกาศในเมธอดนี้ → CS0103")
        print("    → ประกาศตัวแปรในเมธอดนี้ หรือรับเป็นพารามิเตอร์เพิ่ม")

    print(f"\nตรวจ {len(files)} ไฟล์ .cs · ชื่อที่ใช้แต่ไม่เคยประกาศ {len(problems)} จุด")
    return 1 if problems else 0


if __name__ == "__main__":
    sys.exit(main())
