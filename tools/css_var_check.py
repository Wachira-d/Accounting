#!/usr/bin/env python3
"""ตรวจการอ้าง CSS custom property ที่ **ไม่เคยประกาศ** — ปุ่มล่องหน/สีหาย เงียบ ๆ

ที่มา (เคสจริง): ปุ่ม "🗑 ลบทั้งหมด" ในหน้ารายละเอียดเอกสารตั้ง inline style
ชี้ไปโทเคนสีแดงที่ไม่มีอยู่ในไฟล์ CSS เลย — สเปก CSS ระบุว่าการอ้างตัวแปรที่
ไม่มี = "invalid at computed-value time" ⇒ property นั้นกลายเป็น **initial
value** (background: transparent) **ไม่ใช่** ตกไปใช้ค่าที่ประกาศไว้ก่อนหน้า
คลาสของปุ่มตั้ง color:#fff ไว้ ⇒ ตัวหนังสือขาวบนพื้นใส บนพื้นหลัง modal สีขาว
= **ปุ่มลบถาวรที่มองไม่เห็น แต่ยังคลิกโดนได้** ผู้ใช้เห็นเป็นแค่ช่องว่าง

ทำไม checker เดิมมองไม่เห็น: เป็น CSS ที่ถูกไวยากรณ์ทุกประการ · `node --check`
ดูแต่ JS · ตัวอื่นอ่านโครงสร้าง C#/DOM ไม่มีตัวไหน resolve โทเคนข้ามไฟล์

กติกา: เก็บชื่อที่ประกาศ (`--x:`) จากทุกไฟล์ .css/.html ใน wwwroot แล้วฟ้อง
การอ้างที่ชื่อไม่อยู่ในชุดนั้น **และไม่มี fallback** (`var(--x, ค่าเผื่อ)`
ถือว่าปลอดภัย — ผู้เขียนตั้งใจกันไว้แล้ว)
"""
import re
import sys
from pathlib import Path

ROOT = Path(__file__).resolve().parent.parent
WWW = ROOT / "Accounting" / "wwwroot"

DECL = re.compile(r"(--[A-Za-z0-9_-]+)\s*:")
# จับชื่อ + ตัวถัดไป: ')' = ไม่มี fallback · ',' = มี fallback
USE = re.compile(r"var\(\s*(--[A-Za-z0-9_-]+)\s*(\)|,)")
# ตัดคอมเมนต์ CSS/HTML ก่อนสแกน — ไม่งั้นหมายเหตุที่อธิบายบั๊กนี้ถูกนับเป็น
# การอ้างจริง (checker ฟ้องเอกสารของตัวเอง — บทเรียนจาก localstorage_key_check)
COMMENT = re.compile(r"/\*.*?\*/|<!--.*?-->", re.S)


def strip_comments(text: str) -> str:
    """ตัดคอมเมนต์แต่คงจำนวนบรรทัด — เลขบรรทัดที่ฟ้องต้องตรงกับไฟล์จริง"""
    return COMMENT.sub(lambda m: "\n" * m.group(0).count("\n"), text)


def main():
    files = sorted(
        p for p in list(WWW.rglob("*.css")) + list(WWW.rglob("*.html"))
        if not any(part in {"lib", "vendor", "node_modules"} for part in p.parts)
    )

    declared = set()
    bodies = {}
    for f in files:
        text = strip_comments(f.read_text(encoding="utf-8", errors="replace"))
        bodies[f] = text
        declared |= set(DECL.findall(text))

    problems = []
    for f, text in bodies.items():
        for lineno, line in enumerate(text.split("\n"), 1):
            for name, term in USE.findall(line):
                if term == ")" and name not in declared:
                    problems.append((f, lineno, name, line.strip()[:110]))

    for f, lineno, name, text in problems:
        rel = f.relative_to(ROOT)
        print(f"{rel}:{lineno}: อ้าง {name} ที่ไม่เคยประกาศ (จะกลายเป็น initial value)\n    {text}")
        print(f"    → ประกาศใน :root ของ css/style.css หรือใส่ค่าเผื่อ var({name}, #xxx)")

    print(f"\nตรวจ {len(files)} ไฟล์ css/html · ตัวแปรที่อ้างแล้วไม่มีจริง {len(problems)} จุด")
    return 1 if problems else 0


if __name__ == "__main__":
    sys.exit(main())
