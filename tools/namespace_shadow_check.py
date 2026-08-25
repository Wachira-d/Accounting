#!/usr/bin/env python3
"""ตรวจ `Xxx.Type` ที่ผูกไป namespace ผิดชั้น — CS0234 ล้มทั้ง solution.

ที่มา (ของจริง เพิ่งเกิด): `AuthService.cs` อยู่ใน namespace
`Accounting.Services.Implementations` แล้วเขียน `Helpers.PdpaPolicy` โดยคิดว่า
C# จะไล่ขึ้นไปเจอ `Accounting.Helpers` — แต่เรพนี้มี
**`Accounting.Services.Helpers`** อยู่ด้วย (BankCsvParser/BankExcelParser)
C# จึงหยุดที่ชั้นใกล้ที่สุดที่ "ชื่อตรง" แล้วฟ้อง CS0234 ว่าไม่มี `PdpaPolicy`
ใน `Accounting.Services.Helpers` — ทั้งที่ไฟล์มี `using Accounting.Helpers;`
อยู่แล้ว (using ไม่ช่วย เพราะการอ้างแบบมีจุดนำหน้าใช้กติกา "ชั้นใกล้ชนะ")

`tools/using_check.py` จับไม่ได้เพราะ using **ครบอยู่แล้ว** ปัญหาคือชื่อถูก
ผูกไปผิดที่ ไม่ใช่หาไม่เจอ

กติกา: ไฟล์ใน namespace `A.B.C` เขียน `Seg.Type` — ผู้สมัครคือ
`A.B.C.Seg`, `A.B.Seg`, `A.Seg`, `Seg` โดย**ชั้นใกล้ที่สุดชนะ** ฟ้องเมื่อ
namespace ที่ชนะ **ไม่มี** ชนิดชื่อนั้น แต่ผู้สมัครชั้นนอกกว่า**มี** = ตรงกับ
เคส CS0234 เป๊ะ (ถ้าชั้นใกล้มีชนิดนั้นจริง โค้ดก็ทำงานถูก — ไม่ฟ้อง)
"""
import re
import sys
from pathlib import Path

ROOT = Path(__file__).resolve().parent.parent
NS_DECL = re.compile(r"^\s*namespace\s+([A-Za-z_][\w.]*)", re.M)
# ชนิดที่ประกาศในไฟล์ — ใช้สร้างแผนที่ namespace → ชื่อชนิดที่มีอยู่จริง
TYPE_DECL = re.compile(
    r"^\s*(?:\[[^\]]*\]\s*)*(?:public|internal|private|protected)?\s*"
    r"(?:static\s+|sealed\s+|abstract\s+|partial\s+|readonly\s+|ref\s+|file\s+)*"
    r"(?:class|struct|interface|enum|record)\s+(?:class\s+|struct\s+)?([A-Za-z_]\w*)", re.M)
# `Seg.Type` ที่ Seg ขึ้นต้นด้วยตัวใหญ่ และตามด้วยชนิด/สมาชิกที่ขึ้นต้นตัวใหญ่
QUALIFIED = re.compile(r"(?<![\w.])([A-Z][A-Za-z0-9_]*)\.([A-Z][A-Za-z0-9_]*)")


def build_index(files):
    """คืน (เซ็ต namespace ทั้งหมด, แผนที่ namespace → ชื่อชนิดที่ประกาศไว้)"""
    ns, types = set(), {}
    for f in files:
        text = f.read_text(encoding="utf-8", errors="replace")
        m = NS_DECL.search(text)
        if not m:
            continue
        full = m.group(1)
        parts = full.split(".")
        # เก็บทุก prefix — `A.B.C` ทำให้ `A`, `A.B`, `A.B.C` มีอยู่จริงหมด
        for i in range(1, len(parts) + 1):
            ns.add(".".join(parts[:i]))
        types.setdefault(full, set()).update(TYPE_DECL.findall(text))
    return ns, types


def strip_noise(text: str) -> str:
    """ตัดคอมเมนต์ + string literal ออก (คงจำนวนบรรทัดไว้ให้เลขบรรทัดตรง)"""
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
        elif c in "\"'":
            j, quote = i + 1, c
            if c == '"' and text.startswith('"""', i):       # raw string
                j = text.find('"""', i + 3)
                j = n if j < 0 else j + 3
                out.append("\n" * text.count("\n", i, j))
                i = j
                continue
            while j < n and text[j] != quote:
                j += 2 if text[j] == "\\" else 1
            out.append("\n" * text.count("\n", i, min(j + 1, n)))
            i = min(j + 1, n)
        else:
            out.append(c)
            i += 1
    return "".join(out)


def check_file(path: Path, namespaces: set, types: dict):
    text = path.read_text(encoding="utf-8", errors="replace")
    m = NS_DECL.search(text)
    if not m:
        return []
    own = m.group(1).split(".")
    body = strip_noise(text)

    problems = []
    for line_no, line in enumerate(body.splitlines(), 1):
        stripped = line.strip()
        if stripped.startswith(("using ", "namespace ")):
            continue
        for seg, member in QUALIFIED.findall(line):
            # ผู้สมัคร: ไล่จากชั้นในสุดออกมาถึงระดับบนสุด
            candidates = [".".join(own[:i] + [seg]) for i in range(len(own), 0, -1)]
            candidates.append(seg)
            hits = [c for c in candidates if c in namespaces]
            if len(hits) < 2:
                continue
            winner = hits[0]                      # ชั้นใกล้ที่สุดชนะเสมอ
            if member in types.get(winner, ()):
                continue                          # ชั้นที่ชนะมีชนิดนี้จริง = โค้ดถูก
            owners = [c for c in hits[1:] if member in types.get(c, ())]
            if owners:                            # ชั้นนอกมี แต่ชั้นที่ชนะไม่มี = CS0234
                problems.append((line_no, stripped, seg, member, winner, owners))
    return problems


def main():
    files = sorted(
        p for p in ROOT.rglob("*.cs")
        if not any(part in {"bin", "obj", ".git"} for part in p.parts)
    )
    namespaces, types = build_index(files)

    targets = [Path(a) for a in sys.argv[1:]] or files
    total = 0
    for path in targets:
        for line_no, text, seg, member, winner, owners in check_file(path, namespaces, types):
            total += 1
            rel = path.relative_to(ROOT) if ROOT in path.parents else path
            print(f"{rel}:{line_no}: `{seg}.{member}` ผูกไป {winner} (ชั้นใกล้ชนะ) "
                  f"ซึ่งไม่มีชนิดนี้ — ตัวจริงอยู่ที่ {', '.join(owners)}\n    {text}")
            print(f"    → เขียนชื่อเต็ม {owners[0]}.{member} หรือใช้ชื่อเปล่าผ่าน using")

    print(f"\nตรวจ {len(targets)} ไฟล์ .cs · ชื่อที่ผูกไป namespace ผิดชั้น {total} จุด")
    return 1 if total else 0


if __name__ == "__main__":
    sys.exit(main())
