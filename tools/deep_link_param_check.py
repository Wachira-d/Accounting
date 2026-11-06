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

รอบ 250 WP-04 ข้อ 11 (AUDIT_249 A A-06): ลิงก์แบบ hash `<page>.html#<k>=` (เช่น `billing.html#tab=`) — ปลายทางต้องอ่าน `location.hash`
**และ** มีชื่อ `<k>` เป็นสตริง/รูปแบบในไฟล์ (`'k='` · `"k"` · `k=`) — เดิมตรวจแค่ query ⇒ ลิงก์ hash ที่ปลายทางไม่อ่าน = หน้าเปิดแท็บแรกเงียบ ๆ ·
`--self-test` = negative test (ปลายทางไม่อ่าน hash / ไม่มีชื่อ k ⇒ ฟ้อง · อ่านครบ ⇒ ไม่ฟ้อง · query เดิมยังตรวจ)
"""
import os
import re
import sys

ROOT = os.path.dirname(os.path.dirname(os.path.abspath(__file__)))
WWW = os.path.join(ROOT, "Accounting", "wwwroot")

# ลิงก์ที่มี query string ชี้ไปหน้า .html ในเรพ
# ฝ่ายค้าน WP-04 ต่อยอด: รวมลิงก์ relative (`b.html?x=` · `../pages/b.html?x=`) — resolve จากโฟลเดอร์ของไฟล์ต้นทาง
LINK = re.compile(r'["\'`]([A-Za-z0-9_\-./]*?\.html)\?([^"\'`\s>]+)')
# ลิงก์แบบ hash ที่มีชื่อคีย์: "/pages/billing.html#tab=..."
LINK_HASH = re.compile(r'["\'`]([A-Za-z0-9_\-./]*?\.html)(?:\?[^"\'`\s>#]*)?#([A-Za-z0-9_]+)=')   # รวม ?a=1#k=v
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


def resolve(src, path):
    """path ของลิงก์ ⇒ path สัมพัทธ์ใต้ wwwroot (ขึ้นต้น / = จากราก · อื่น = จากโฟลเดอร์ของไฟล์ต้นทาง)"""
    if path.startswith("/"):
        return path.lstrip("/")
    return os.path.normpath(os.path.join(os.path.dirname(src), path)).replace(os.sep, "/")


def analyse(files):
    """files: {path สัมพัทธ์ใต้ wwwroot: เนื้อไฟล์ดิบ} ⇒ (จำนวนหน้า, จำนวนลิงก์, [(src, line, target, name, kind)])"""
    pages = {}        # หน้า → ชุดชื่อ param ที่อ่าน
    page_text = {}    # หน้า → เนื้อที่ตัดคอมเมนต์แล้ว (ฝั่ง hash)
    sources = []      # (ไฟล์ต้นทาง, บรรทัด, หน้าเป้าหมาย, ชื่อ, ชนิด query/hash)
    for rel, raw in files.items():
        clean = strip_comments(raw)
        if rel.endswith(".html"):
            pages[rel] = set(READ.findall(clean))
            page_text[rel] = clean
        for m in LINK.finditer(clean):
            target = resolve(rel, m.group(1))
            line = clean.count("\n", 0, m.start()) + 1
            for pair in re.split(r"[&;]", m.group(2).split("#")[0]):
                name = pair.split("=")[0].strip()
                # ข้าม template placeholder (`${...}`) และชื่อว่าง
                if not name or "$" in name or "{" in name:
                    continue
                sources.append((rel, line, target, name, "query"))
        for m in LINK_HASH.finditer(clean):
            sources.append((rel, clean.count("\n", 0, m.start()) + 1, resolve(rel, m.group(1)), m.group(2), "hash"))

    problems = []
    for src, line, target, name, kind in sources:
        if target not in pages:
            continue          # ไฟล์ปลายทางไม่มี = งานของ dead_link_check
        if (os.path.basename(target), name) in IGNORED:
            continue
        if kind == "query":
            if name not in pages[target]:
                problems.append((src, line, target, name, kind))
        else:
            text = page_text[target]
            reads_hash = "location.hash" in text
            names_key = re.search(r"['\"`]" + re.escape(name) + r"(=|['\"`])", text) is not None
            if not (reads_hash and names_key):
                problems.append((src, line, target, name, kind))
    return len(pages), len(sources), problems


def load_files():
    files = {}
    for dirpath, _dirs, names in os.walk(WWW):
        for fn in names:
            if not fn.endswith((".html", ".js")):
                continue
            path = os.path.join(dirpath, fn)
            try:
                files[os.path.relpath(path, WWW).replace(os.sep, "/")] = open(path, encoding="utf-8").read()
            except Exception:
                continue
    return files


def self_test():
    reader = "<script>const h = location.hash || ''; const m = h.match(/tab=([a-z]+)/); const t = 'tab=';</script>"
    link = "<a href=\"/pages/b.html#tab=usage\">x</a><a href=\"/pages/q.html?id=1\">y</a>"
    qpage = "<script>const p = new URLSearchParams(location.search); p.get('id');</script>"
    fails = []
    _, _, ok = analyse({"pages/a.html": link, "pages/b.html": reader, "pages/q.html": qpage})
    if ok:
        fails.append(f"ปลายทางอ่าน hash + ชื่อ tab แล้วยังฟ้อง: {ok}")
    _, _, p1 = analyse({"pages/a.html": link, "pages/b.html": "<script>const t = 'tab=';</script>", "pages/q.html": qpage})
    if not any(k == "hash" for *_x, k in p1):
        fails.append("ปลายทางไม่อ่าน location.hash แล้วไม่ฟ้อง")
    _, _, p2 = analyse({"pages/a.html": link, "pages/b.html": "<script>const h = location.hash; const t = 'view=';</script>", "pages/q.html": qpage})
    if not any(k == "hash" for *_x, k in p2):
        fails.append("ปลายทางไม่มีชื่อคีย์ tab แล้วไม่ฟ้อง")
    _, _, p3 = analyse({"pages/a.html": link, "pages/b.html": reader, "pages/q.html": "<script>p.get('other');</script>"})
    if not any(k == "query" for *_x, k in p3):
        fails.append("query เดิม (ปลายทางไม่อ่าน id) แล้วไม่ฟ้อง — ถดถอย")
    # ฝ่ายค้าน WP-04 ต่อยอด: ?a=1#k=v และลิงก์ relative
    _, _, p5 = analyse({"pages/a.html": '<a href="/pages/b.html?x=1#tab=u">x</a>', "pages/b.html": "<script>p.get('x'); const t = 'view=';</script>"})
    if not any(k == "hash" for *_x, k in p5):
        fails.append("ลิงก์ ?a=1#k=v ที่ปลายทางไม่อ่าน hash ไม่ถูกฟ้อง")
    _, _, p6 = analyse({"pages/a.html": '<a href="q.html?id=1">x</a>', "pages/q.html": "<script>p.get('other');</script>"})
    if not any(t == "pages/q.html" for _s, _l, t, _n, _k in p6):
        fails.append("ลิงก์ relative (q.html?id=) ที่ปลายทางไม่อ่าน id ไม่ถูกฟ้อง")
    _, _, p7 = analyse({"pages/a.html": '<a href="q.html?id=1">x</a>', "pages/q.html": "<script>p.get('id');</script>"})
    if p7:
        fails.append(f"ลิงก์ relative ที่ปลายทางอ่านครบถูกฟ้อง: {p7}")
    _, _, p4 = analyse({"pages/a.html": "<!-- ไม่มีลิงก์ --><script>// '/pages/b.html#tab=x'\n</script>", "pages/b.html": "<p></p>"})
    if p4:
        fails.append("ลิงก์ในคอมเมนต์ถูกนับ")
    for f in fails:
        print("❌ self-test deep_link_param_check: " + f)
    if not fails:
        print("deep_link_param_check --self-test: ผ่าน")
    return 1 if fails else 0


def main():
    if "--self-test" in sys.argv:
        return self_test()
    if self_test():
        return 1
    n_pages, n_links, problems = analyse(load_files())
    seen = set()
    for src, line, target, name, kind in problems:
        key = (src, target, name, kind)
        if key in seen:
            continue
        seen.add(key)
        if kind == "query":
            print(f"{src}:{line}: ส่ง `?{name}=` ไป /{target} "
                  f"แต่หน้านั้นไม่เคยอ่านชื่อนี้ → ค่าหายเงียบ (ตกมาที่ลิสต์เปล่า)")
            print(f"    → เพิ่ม `params.get('{name}')` ที่ /{target} "
                  f"หรือเปลี่ยนชื่อ param ให้ตรงกับที่ปลายทางอ่านอยู่แล้ว")
        else:
            print(f"{src}:{line}: ส่ง `#{name}=` ไป /{target} แต่หน้านั้นไม่อ่าน location.hash/ไม่มีชื่อ `{name}` → เปิดแท็บแรกเงียบ ๆ")
            print(f"    → อ่าน `location.hash` ที่ /{target} แล้วแยก `{name}=` หรือเปลี่ยนลิงก์เป็น query ที่ปลายทางอ่าน")

    print()
    print(f"ตรวจ {n_pages} หน้า · ลิงก์ query/hash {n_links} จุด · "
          f"ชื่อที่ปลายทางไม่อ่าน {len(seen)} จุด")
    return 1 if seen else 0


if __name__ == "__main__":
    sys.exit(main())
