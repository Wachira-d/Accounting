#!/usr/bin/env python3
"""รอบ 224 — สคริปต์ร่วมที่อ้าง `window.X` ต้องมีผู้ผูก `window.X = X` จริง

ที่มา: layout.js ประกาศ `const Layout = {…}` (lexical global ไม่ใช่ property ของ window) ⇒ `window.Layout && Layout.api`
ใน money-channel.js เป็น false ตลอด ⇒ dropdown "เติมจากบัญชี" ของเงินสดย่อยขึ้น "ยังไม่ได้เลือกบริษัท" ทั้งที่เลือกแล้ว
และ booking-payment-admin.js ตก fallback ไม่ escape HTML

ตรวจ: ทุกชื่อในตาราง GLOBALS ที่ถูกอ้างเป็น `window.<Name>` ใน wwwroot ต้องมีบรรทัด `window.<Name> = ` ในไฟล์ OWNER
negative test: python3 tools/window_global_check.py --self-test
"""
import os, re, sys

ROOT = os.path.join(os.path.dirname(os.path.abspath(__file__)), "..", "Accounting", "wwwroot")
GLOBALS = {"Layout": "js/layout.js"}


def check(owner_text_by_name, referenced):
    errs = []
    for name, owner in GLOBALS.items():
        if name not in referenced:
            continue
        if not re.search(r"^\s*window\." + name + r"\s*=", owner_text_by_name.get(name, ""), re.M):
            errs.append(f"❌ มีการอ้าง window.{name} แต่ {owner} ไม่ได้ผูก `window.{name} = {name}` — `const` ระดับสคริปต์ไม่อยู่บน window")
    return errs


def main():
    if "--self-test" in sys.argv:
        bad = check({"Layout": "const Layout = {};\n"}, {"Layout"})
        good = check({"Layout": "const Layout = {};\nwindow.Layout = Layout;\n"}, {"Layout"})
        assert bad and not good, "self-test ล้ม"
        print("✅ window_global_check self-test")
        return 0
    referenced = set()
    for dp, _, fs in os.walk(ROOT):
        for f in fs:
            if f.endswith((".js", ".html")):
                t = open(os.path.join(dp, f), encoding="utf-8", errors="ignore").read()
                for name in GLOBALS:
                    if "window." + name in t:
                        referenced.add(name)
    owners = {n: open(os.path.join(ROOT, p), encoding="utf-8").read() for n, p in GLOBALS.items()}
    errs = check(owners, referenced)
    for e in errs:
        print(e)
    return 1 if errs else 0


if __name__ == "__main__":
    sys.exit(main())
