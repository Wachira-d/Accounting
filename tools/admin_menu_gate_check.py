#!/usr/bin/env python3
"""ตรวจว่า **เมนูที่พาไปหน้าซึ่งเรียก `/api/admin/...` ถูก gate ด้วย `platformAdmin`**

ที่มา (เคสจริง): เมนู "รายงานการใช้งาน AI" และ "การใช้งานรายบริษัท" ซึ่งแสดงข้อมูล
**ข้ามบริษัท** ถูกติดธง `adminOnly: true` — แต่ `adminOnly` ใน layout.js ถูกเช็คกับ
`myPermissions.isOwnerOrAdmin` ซึ่งแปลว่า "เจ้าของ/แอดมิน **ของบริษัทนี้**"
= **ลูกค้าทุกรายที่เปิดบริษัทเอง** ⇒ ลูกค้าเห็นเมนูของแพลตฟอร์มโผล่ในแถบซ้ายตัวเอง

ข้อมูลไม่ได้รั่ว เพราะ controller ฝั่ง `/api/admin/*` บังคับ
`[Authorize(Roles = "SystemAdmin")]` อยู่แล้ว (กดแล้วได้ 403) — แต่เป็นการเปิดเผย
หน้าจอภายในให้ลูกค้าเห็น และอยู่ห่างจากการรั่วจริงแค่ "ลืมใส่ attribute หนึ่งบรรทัด"

ทำไม checker ตัวอื่นมองไม่เห็น: ทุกตัวอ่านโค้ดฝั่งเดียว — ตัวนี้ต้องโยงสามชั้น
(รายการเมนูใน layout.js → ไฟล์ HTML ปลายทาง → endpoint ที่หน้านั้นเรียก)

กติกา:
  หน้าใดเรียก `/api/admin/` → เมนูที่ชี้ไปหน้านั้นต้องมี `platformAdmin: true`
  (`adminOnly: true` อย่างเดียว = ไม่ผ่าน)
  และ controller ที่รองรับ `/api/admin/*` ต้องมี `Authorize(Roles = "SystemAdmin")`
"""
import re
import sys
from pathlib import Path

ROOT = Path(__file__).resolve().parent.parent
WWW = ROOT / "Accounting" / "wwwroot"
LAYOUT = WWW / "js" / "layout.js"
CONTROLLERS = ROOT / "Accounting" / "Controllers"

# { id: '…', … href: '/pages/x.html' … } — อ่านทีละรายการในบรรทัดเดียว/หลายบรรทัด
NAV_ITEM = re.compile(r"\{\s*id:\s*'([^']+)'[^}]*?href:\s*'([^']+)'[^}]*?\}", re.S)
ADMIN_API = re.compile(r"/api/admin/")


def strip_comments(text: str) -> str:
    """ตัดคอมเมนต์แต่คงจำนวนบรรทัด — ไม่งั้นหมายเหตุที่อธิบายบั๊กนี้ถูกนับเป็นโค้ดจริง
    (บทเรียนซ้ำจาก localstorage_key_check / css_var_check ที่ checker ฟ้องเอกสารตัวเอง)"""
    out = []
    i, n = 0, len(text)
    while i < n:
        if text[i] == "/" and i + 1 < n and text[i + 1] == "/":
            j = text.find("\n", i)
            i = n if j < 0 else j
            continue
        if text[i] == "/" and i + 1 < n and text[i + 1] == "*":
            j = text.find("*/", i + 2)
            j = n if j < 0 else j + 2
            out.append("\n" * text.count("\n", i, j))
            i = j
            continue
        out.append(text[i])
        i += 1
    return "".join(out)


def main():
    problems = []

    layout_raw = LAYOUT.read_text(encoding="utf-8", errors="replace")
    layout = strip_comments(layout_raw)

    for m in NAV_ITEM.finditer(layout):
        item_id, href = m.group(1), m.group(2)
        block = m.group(0)
        page = href.split("?")[0].split("#")[0].lstrip("/")
        target = WWW / page
        if not target.exists() or target.suffix != ".html":
            continue
        body = strip_comments(target.read_text(encoding="utf-8", errors="replace"))
        if not ADMIN_API.search(body):
            continue
        if "platformAdmin: true" not in block:
            line = layout.count("\n", 0, m.start()) + 1
            problems.append((
                LAYOUT.relative_to(ROOT), line,
                f"เมนู '{item_id}' → {href} เรียก /api/admin/ แต่ไม่ได้ติดธง platformAdmin",
                "adminOnly ไม่พอ — มันแปลว่า 'เจ้าของบริษัทนี้' = ลูกค้าทุกราย "
                "ต้องใส่ `platformAdmin: true`"))

    # ด่านฝั่ง server: controller ที่รับ /api/admin/* ต้องบังคับ role SystemAdmin
    for f in sorted(CONTROLLERS.rglob("*.cs")):
        src = f.read_text(encoding="utf-8", errors="replace")
        routes = re.findall(r'\[Route\("([^"]*api/admin[^"]*)"\)\]', src, re.I)
        if not routes:
            continue
        if 'Roles = "SystemAdmin"' not in src and 'Roles="SystemAdmin"' not in src:
            line = src[:src.index(routes[0])].count("\n") + 1
            problems.append((
                f.relative_to(ROOT), line,
                f"controller รับเส้นทาง {routes[0]} แต่ไม่มี [Authorize(Roles = \"SystemAdmin\")]",
                "endpoint ใต้ /api/admin คืนข้อมูลข้ามบริษัท — ต้องบังคับแอดมินแพลตฟอร์ม"))

    for rel, line, msg, hint in problems:
        print(f"{rel}:{line}: {msg}\n    → {hint}")

    print(f"\nตรวจเมนู + controller · เมนูแอดมินที่ gate ไม่ถูก {len(problems)} จุด")
    return 1 if problems else 0


if __name__ == "__main__":
    sys.exit(main())
