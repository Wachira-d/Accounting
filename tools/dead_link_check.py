#!/usr/bin/env python3
# -*- coding: utf-8 -*-
"""dead_link_check.py — ลิงก์ภายในที่ชี้ไปหน้าที่ไม่มีอยู่จริง

ที่มา (บั๊กจริง — ผู้ใช้เจอเอง):
ปุ่ม "เปิด →" ในหน้าสำนักงานบัญชี (accountant-workspace.html) พาไป
`/pages/dashboard.html` ซึ่ง **ไม่เคยมีไฟล์นั้นอยู่จริงเลยสักวันเดียว**
(ตรวจย้อน git history แล้ว) — ผู้ใช้เห็น ERR_INVALID_RESPONSE. พอไล่ดูทั้งเรพ
พบพาธเดียวกันอีก 3 จุด (ตอบรับคำเชิญ · Connect portal · ออกจาก POS) และเจอ
`/pages/document-template.html` (สะกดเอกพจน์ ไฟล์จริงเป็นพหูพจน์) ในเช็กลิสต์
เริ่มต้นใช้งานอีก 1 จุด

ทำไม checker เดิมจับไม่ได้: ทั้ง 8 ตัวตรวจฝั่ง C# หรือ syntax ของ JS
(`node --check`) — ลิงก์ที่ชี้ผิดเป็น **string ที่ถูกต้องทางไวยากรณ์ทุกประการ**
ไม่มีอะไรพังจนกว่าผู้ใช้จะกด. ไม่มี compiler ตัวไหนในโลกจับให้ ต้องเช็คกับ
ระบบไฟล์เท่านั้น

ตรวจ: ทุก string ที่หน้าตาเป็นพาธ absolute ลงท้าย .html ในไฟล์ .html/.js
ใต้ wwwroot → ต้องมีไฟล์อยู่จริง

รันเปล่า ๆ = ตรวจทั้งเรพ · `--self-test` = ทดสอบตัว checker เอง
"""
import os
import re
import sys

ROOT = os.path.dirname(os.path.dirname(os.path.abspath(__file__)))
WWWROOT = os.path.join(ROOT, "Accounting", "wwwroot")
SKIP_DIRS = {"lib", "node_modules", ".git"}

# พาธที่ขึ้นต้น '/' ลงท้าย .html ภายในเครื่องหมายคำพูด/วงเล็บ
LINK = re.compile(r"""["'`(]\s*(/[A-Za-z0-9_\-./]*\.html)""")


def collect(www=WWWROOT):
    """คืน dict: url → set(ไฟล์ที่อ้างถึง) เฉพาะ url ที่ไม่มีไฟล์จริง"""
    refs = {}
    for base, dirs, files in os.walk(www):
        dirs[:] = [d for d in dirs if d not in SKIP_DIRS]
        for f in files:
            if not f.endswith((".html", ".js")):
                continue
            path = os.path.join(base, f)
            try:
                txt = open(path, encoding="utf-8").read()
            except (OSError, UnicodeDecodeError):
                continue
            for m in LINK.finditer(txt):
                url = m.group(1)
                refs.setdefault(url, set()).add(os.path.relpath(path, www))
    return {u: s for u, s in refs.items()
            if not os.path.exists(os.path.join(www, u.lstrip("/")))}


def self_test():
    import shutil
    import tempfile
    tmp = tempfile.mkdtemp()
    try:
        os.makedirs(os.path.join(tmp, "pages"))
        open(os.path.join(tmp, "pages", "real.html"), "w").write("<p>ok</p>")
        open(os.path.join(tmp, "index.html"), "w", encoding="utf-8").write(
            """<a href="/pages/real.html">ok</a>
               <script>location.href = '/pages/ghost.html';</script>
               <a href="https://example.com/external.html">ข้างนอก ไม่ตรวจ</a>""")
        bad = collect(tmp)
        ok = True
        if "/pages/ghost.html" not in bad:
            print("❌ self-test: จับลิงก์ตายไม่ได้")
            ok = False
        else:
            print("✅ self-test: จับลิงก์ตายได้")
        if "/pages/real.html" in bad:
            print("❌ self-test: ฟ้องผิดบนลิงก์ที่มีไฟล์จริง")
            ok = False
        else:
            print("✅ self-test: ลิงก์ที่มีไฟล์จริง ไม่ฟ้อง")
        return 0 if ok else 1
    finally:
        shutil.rmtree(tmp, ignore_errors=True)


def main():
    if "--self-test" in sys.argv:
        return self_test()
    bad = collect()
    print()
    for url, srcs in sorted(bad.items()):
        print(f"{url} — ไม่มีไฟล์นี้ใน wwwroot")
        for s in sorted(srcs):
            print(f"    ← {s}")
    print(f"\nตรวจ wwwroot · ลิงก์ที่ชี้ไปหน้าที่ไม่มีอยู่จริง {len(bad)} จุด")
    return 1 if bad else 0


if __name__ == "__main__":
    sys.exit(main())
