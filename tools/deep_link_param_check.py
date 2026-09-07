#!/usr/bin/env python3
# -*- coding: utf-8 -*-
"""ตรวจ "ชื่อ query param ที่ลิงก์ส่งไป" กับ "ชื่อที่หน้าปลายทางอ่านจริง"

═══ ที่มา (ผลตรวจทีม F · SYSTEM_AUDIT_2026-09-07.md F-03) ═══
พบลิงก์ **9 จุดใน 6 หน้า** ที่ส่งชื่อพารามิเตอร์ที่หน้าปลายทางไม่เคยอ่าน — เช่น
`documents.html?id=` (ปลายทางอ่าน `openDoc`) · `fixed-assets.html?assetId=`
(อ่าน `id`) · `etax.html?status=` · `projects.html?id=` · `wht.html?documentId=`
· `admin/users.html?userId=` ที่ **ไม่มี `URLSearchParams` เลยสักบรรทัด**

อาการ: กด "เปิดดูเอกสาร" หลังขายเสร็จ → ได้ลิสต์เอกสารหน้า 1 ไม่ใช่ใบที่เพิ่งออก
— เป็น silent no-op ที่ไม่มี error ให้ debug เพราะ "หน้าเปิดได้ปกติ"

`dead_link_check.py` ตรวจแค่ว่า **ไฟล์** ปลายทางมีอยู่ ไม่ได้ดู query string
จึงต้องมีตัวนี้แยก (รูปแบบเดียวกับ `upload_route_check` / `csp_external_ref_check`
ที่โยงสองที่คนละไฟล์เข้าหากัน)

กติกา: ทุกชื่อ param ที่ถูกส่งไปยังหน้า `.html` ในเรพ ต้องมี `params.get('<ชื่อ>')`
(หรือ `.get("<ชื่อ>")`) อยู่ในไฟล์ปลายทาง — ไม่งั้นค่าที่ส่งไปหายเงียบ
"""
import os
import re
import sys

ROOT = os.path.dirname(os.path.dirname(os.path.abspath(__file__)))
WWW = os.path.join(ROOT, "Accounting", "wwwroot")

# ลิงก์ที่มี query string ชี้ไปหน้า .html ในเรพ
LINK = re.compile(r'["\'`](/[A-Za-z0-9_\-./]*?\.html)\?([^"\'`\s>]+)')
# ฝั่งอ่าน: params.get('x') · qs.get("x") · searchParams.get(`x`)
READ = re.compile(r'\.get\(\s*[\'"`]([A-Za-z0-9_]+)[\'"`]\s*\)')

# param ที่เป็น marker เฉย ๆ (ไม่ต้องมีตัวอ่าน) — ต้องระบุชื่อไว้ให้เป็นการ
# ตัดสินใจที่ตั้งใจ ไม่ใช่ยกเว้นอัตโนมัติ
IGNORED = {
    ("register.html", "sso"),   # marker ให้หน้า register รู้ว่ามาจาก SSO — ตั๋วจริงอยู่ใน sessionStorage
}


def strip_comments(text):
    """ตัดคอมเมนต์ // และ /* */ โดย**คงจำนวนบรรทัด** และไม่แตะข้อความในสตริง
    (บทเรียนจาก csp_external_ref_check: `//` ของ `https://` อยู่ในสตริง)"""
    out = []
    i, n = 0, len(text)
    quote = None
    while i < n:
        c = text[i]
        if quote:
            out.append(c)
            if c == "\\" and i + 1 < n:
                out.append(text[i + 1]); i += 2; continue
            if c == quote:
                quote = None
            i += 1
            continue
        if c in "'\"`":
            quote = c; out.append(c); i += 1; continue
        if c == "/" and i + 1 < n and text[i + 1] == "/":
            while i < n and text[i] != "\n":
                out.append(" "); i += 1
            continue
        if c == "/" and i + 1 < n and text[i + 1] == "*":
            while i < n and not (text[i] == "*" and i + 1 < n and text[i + 1] == "/"):
                out.append("\n" if text[i] == "\n" else " "); i += 1
            out.append("  "); i += 2
            continue
        out.append(c); i += 1
    return "".join(out)


def main():
    pages = {}   # path สัมพัทธ์ (เช่น "pages/documents.html") → ชุดชื่อ param ที่อ่าน
    sources = []  # (ไฟล์ต้นทาง, บรรทัด, หน้าเป้าหมาย, ชื่อ param)

    for dirpath, _dirs, files in os.walk(WWW):
        for fn in files:
            if not fn.endswith((".html", ".js")):
                continue
            path = os.path.join(dirpath, fn)
            try:
                raw = open(path, encoding="utf-8").read()
            except Exception:
                continue
            clean = strip_comments(raw)
            rel = os.path.relpath(path, WWW).replace(os.sep, "/")
            if fn.endswith(".html"):
                pages[rel] = set(READ.findall(clean))
            for m in LINK.finditer(clean):
                target = m.group(1).lstrip("/")
                qs = m.group(2)
                line = clean.count("\n", 0, m.start()) + 1
                for pair in re.split(r"[&;]", qs):
                    name = pair.split("=")[0].strip()
                    # ข้าม template placeholder (`${...}`) และชื่อว่าง
                    if not name or "$" in name or "{" in name:
                        continue
                    sources.append((rel, line, target, name))

    problems = []
    for src, line, target, name in sources:
        if target not in pages:
            continue          # ไฟล์ปลายทางไม่มี = งานของ dead_link_check
        if (os.path.basename(target), name) in IGNORED:
            continue
        if name not in pages[target]:
            problems.append((src, line, target, name))

    seen = set()
    for src, line, target, name in problems:
        key = (src, target, name)
        if key in seen:
            continue
        seen.add(key)
        print(f"{src}:{line}: ส่ง `?{name}=` ไป /{target} "
              f"แต่หน้านั้นไม่เคยอ่านชื่อนี้ → ค่าหายเงียบ (ตกมาที่ลิสต์เปล่า)")
        print(f"    → เพิ่ม `params.get('{name}')` ที่ /{target} "
              f"หรือเปลี่ยนชื่อ param ให้ตรงกับที่ปลายทางอ่านอยู่แล้ว")

    print()
    print(f"ตรวจ {len(pages)} หน้า · ลิงก์ที่มี query {len(sources)} จุด · "
          f"ชื่อ param ที่ปลายทางไม่อ่าน {len(seen)} จุด")
    return 1 if seen else 0


if __name__ == "__main__":
    sys.exit(main())
