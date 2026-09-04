#!/usr/bin/env python3
"""ค่าที่ฝังใน JS string literal ภายใน onclick="" ต้องหนีแบบ JS ไม่ใช่แบบ HTML

ที่มา (บั๊กจริง): `fixed-assets.html` เขียน

    onclick="Page.deleteAsset('${a.id}','${Layout.esc(a.name||'')}')"

`Layout.esc` เป็น **HTML escape** (ทำผ่าน textContent→innerHTML) จึงหนีแค่ `& < >`
**ไม่หนี `'` และขึ้นบรรทัดใหม่** ⇒ ชื่อสินทรัพย์ที่ OCR อ่านมาจากใบกำกับ (มี
อัญประกาศเดี่ยว/ขึ้นบรรทัดใหม่ได้) ปิด JS string กลางคัน แล้วทั้งหน้าตายด้วย
`Invalid or unexpected token` — ผู้ใช้รายงานว่า "กดลบรายการดราฟไม่ได้"

กติกาที่ตรวจ (shape-based ไม่ใช่ taint จึงไม่มี false positive โดยนิยาม):
    ห้ามมี `Layout.esc(` / `esc(` อยู่ใน **JS string literal** ภายใน `onclick="..."`
    — ไม่ว่าค่าที่ส่งจะเป็น id ที่ปลอดภัยวันนี้หรือไม่ก็ตาม เพราะวันหนึ่งจะมีคน
    เปลี่ยนไปส่งชื่อ/คำอธิบายแทน แล้วไม่มีอะไรฟ้อง

    ที่ถูกคือ `Layout.jsArg(...)` (หนีระดับ JS ก่อน แล้วค่อยหนีระดับ HTML attribute)
    หรือดีกว่านั้นคือ **ไม่ส่งข้อความอิสระผ่าน onclick เลย** — ส่ง id/ดัชนีแล้วไป
    หยิบค่าจากข้อมูลที่โหลดไว้ (`Page.rows[i]`)

หมายเหตุ: `Layout.esc(...)` ที่อยู่ **นอก** string literal (เช่นในเนื้อ HTML ปกติ
`<td>${Layout.esc(x)}</td>`) ถูกต้องแล้ว — ตัวตรวจต้องไม่ไปฟ้องพวกนั้น
"""
import re
import sys
from pathlib import Path

ROOT = Path(__file__).resolve().parent.parent
WWWROOT = ROOT / "Accounting" / "wwwroot"

ATTR = re.compile(r'onclick="([^"]*)"')
# `'${ ... esc( ... ' — esc ที่โผล่หลังอัญประกาศเดี่ยวเปิด string
IN_JS_STRING = re.compile(r"'[^']*\$\{[^}]*?\b(?:Layout\.)?esc\(")


def scan(path: Path):
    hits = []
    text = path.read_text(encoding="utf-8", errors="replace")
    for lineno, line in enumerate(text.splitlines(), 1):
        for m in ATTR.finditer(line):
            if IN_JS_STRING.search(m.group(1)):
                hits.append((lineno, m.group(0)[:150]))
    return hits


def main() -> int:
    targets = sys.argv[1:] or [str(WWWROOT)]
    files = []
    for t in targets:
        p = Path(t)
        files.extend(sorted(p.rglob("*.html")) if p.is_dir() else [p])

    total = 0
    for f in files:
        for lineno, snippet in scan(f):
            total += 1
            rel = f.relative_to(ROOT) if str(f).startswith(str(ROOT)) else f
            print(f"{rel}:{lineno}: `esc()` อยู่ใน JS string ของ onclick "
                  f"— HTML escape ไม่หนี ' และ \\n ⇒ ชื่อที่มีอักขระพวกนี้ทำให้ทั้งหน้าตาย")
            print(f"    => {snippet}")
            print("    → ใช้ Layout.jsArg(...) หรือส่ง id แล้วไปหยิบค่าจากข้อมูลที่โหลดไว้")

    print(f"\nตรวจ {len(files)} ไฟล์ html · esc() ใน JS string ของ onclick {total} จุด")
    return 1 if total else 0


if __name__ == "__main__":
    raise SystemExit(main())
