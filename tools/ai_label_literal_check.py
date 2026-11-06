#!/usr/bin/env python3
"""ratchet ป้าย "🤖" ตายตัวในหน้าเว็บ (checklist กฎเหล็ก #1: "🤖 AI แนะนำ" เฉพาะตอนเรียก AI จริง ไม่งั้น "⚙️ ระบบแนะนำ")

ที่มา (รอบ 250 WP-03 · ผลตรวจ D §1.5/§2.9 + ฝ่ายค้าน M3): หน้า recurring/fixed-assets/contacts/organization/petty-cash ติด 🤖 กับคำแนะนำทุกครั้ง
ทั้งที่ endpoint ตอบจากความจำ/ประวัติของบริษัท (ไม่เรียก provider) — ป้ายที่ไม่ซื่อสัตย์ · ตัวตัดสินป้ายเดียวคือ `Layout.suggestLabel(resp)` (ดู `usedAi` ของเซิร์ฟเวอร์)

กติกา: นับ "🤖" ต่อไฟล์ใน `Accounting/wwwroot/**` (.html/.js · ไม่นับคอมเมนต์ `//` · `/* */` · `<!-- -->`) เทียบ `tools/ai_label_literal_baseline.txt`
— ลดได้ ห้ามเพิ่ม · ไฟล์ที่ไม่อยู่ใน baseline = 0 · `js/layout.js` (ตัวตัดสิน suggestLabel) และ `js/ai-suggestion.js` (ป้ายตาม usedAi) ได้รับยกเว้นโดยชื่อ
`--self-test`: ฉีด "🤖 " ลงสำเนา contacts.html แล้วต้องฟ้อง · คอมเมนต์ที่มี 🤖 ไม่ถูกนับ · `--write-baseline` เขียน baseline จากของจริง
"""
import os
import re
import sys

ROOT = os.path.dirname(os.path.dirname(os.path.abspath(__file__)))
WWW = os.path.join(ROOT, "Accounting", "wwwroot")
BASELINE = os.path.join(ROOT, "tools", "ai_label_literal_baseline.txt")
EXEMPT = {os.path.join("js", "layout.js"), os.path.join("js", "ai-suggestion.js")}


def strip_comments(t):
    t = re.sub(r"<!--.*?-->", "", t, flags=re.S)
    t = re.sub(r"/\*.*?\*/", "", t, flags=re.S)
    t = re.sub(r"(?<![:\\'\"])//[^\n]*", "", t)
    return t


def count(t):
    return strip_comments(t).count("🤖")


def scan():
    res = {}
    for dp, dn, fn in os.walk(WWW):
        dn[:] = [d for d in dn if d not in ("node_modules", "lib", "vendor")]
        for f in fn:
            if not f.endswith((".html", ".js")):
                continue
            rel = os.path.relpath(os.path.join(dp, f), WWW)
            if rel in EXEMPT:
                continue
            c = count(open(os.path.join(dp, f), encoding="utf-8", errors="ignore").read())
            if c:
                res[rel] = c
    return res


def load():
    b = {}
    if os.path.exists(BASELINE):
        for line in open(BASELINE, encoding="utf-8"):
            line = line.strip()
            if line and not line.startswith("#"):
                k, v = line.rsplit(" ", 1)
                b[k] = int(v)
    return b


def self_test():
    fails = []
    if count("// 🤖 คอมเมนต์\n/* 🤖 */\n<!-- 🤖 -->\nx = 1;") != 0:
        fails.append("คอมเมนต์ที่มี 🤖 ต้องไม่ถูกนับ")
    if count("hint.textContent = '🤖 ' + d.reasoning;") != 1:
        fails.append("ป้าย 🤖 ในโค้ดต้องถูกนับ")
    p = os.path.join(WWW, "pages", "contacts.html")
    t = open(p, encoding="utf-8").read()
    inj = t.replace("${Layout.suggestLabel(j.data)}: ", "🤖 ", 1)
    if inj == t:
        fails.append("หาจุดฉีดใน contacts.html ไม่เจอ (แก้ self-test)")
    elif count(inj) <= count(t):
        fails.append("ฉีด 🤖 ลงสำเนา contacts.html แล้วไม่ถูกนับเพิ่ม")
    if fails:
        print("❌ self-test ai_label_literal_check:\n  " + "\n  ".join(fails)); return 1
    print("✅ self-test ai_label_literal_check ผ่าน (นับ 🤖 ในโค้ด · ไม่นับคอมเมนต์ · ฉีดลง contacts.html ถูกจับ)")
    return 0


if __name__ == "__main__":
    if "--self-test" in sys.argv:
        sys.exit(self_test())
    cur = scan()
    if "--write-baseline" in sys.argv:
        with open(BASELINE, "w", encoding="utf-8") as fh:
            fh.write("# ratchet ของ tools/ai_label_literal_check.py (รอบ 250 WP-03 ฝ่ายค้าน M3) — ป้าย 🤖 ตายตัวต่อไฟล์ (นอกคอมเมนต์) · ลดลงเท่านั้น\n")
            fh.write("# ป้ายคำแนะนำใหม่ต้องใช้ Layout.suggestLabel(resp) (usedAi จากเซิร์ฟเวอร์) — ห้ามเพิ่มแถว/ตัวเลขเพื่อให้เขียว\n")
            for k in sorted(cur):
                fh.write(f"{k} {cur[k]}\n")
        print("เขียน baseline", len(cur), "ไฟล์"); sys.exit(0)
    base = load()
    bad = False
    for k, v in sorted(cur.items()):
        if v > base.get(k, 0):
            bad = True
            print(f"❌ {k}: 🤖 ตายตัว {v} จุด (baseline {base.get(k, 0)}) — ใช้ Layout.suggestLabel(resp) ตาม usedAi ของเซิร์ฟเวอร์")
    for k, v in sorted(base.items()):
        if cur.get(k, 0) < v:
            print(f"ℹ️ {k}: เหลือ {cur.get(k, 0)} < baseline {v} — ลดตัวเลขใน ai_label_literal_baseline.txt (ratchet)")
    if bad:
        sys.exit(1)
    print(f"✅ ai_label_literal_check: ป้าย 🤖 ตายตัวไม่เกิน baseline ({sum(cur.values())} จุดคงค้าง)")
    sys.exit(self_test())
