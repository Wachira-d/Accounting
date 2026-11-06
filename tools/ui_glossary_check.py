#!/usr/bin/env python3
"""ratchet ศัพท์นักพัฒนา/อังกฤษที่ห้ามถึงผู้ใช้ (glossary · docs/ui-glossary.md) — ต่อไฟล์ใน `Accounting/wwwroot/**/*.html`

═══ ที่มา (รอบ 252 WP-13 M · audit 249 D §11 M) ═══
ทีม D พบหน้า notifications/email-schedule/account-subscription/portal/contacts พูดกับผู้ใช้ด้วยคำอย่าง "fallback ไป System/Email",
"Default-deny", "Sys / Mail", "Trigger *", "Session หมดอายุ — login ใหม่", "L2", "workspace", "invite", "Portal" — ผู้ใช้ทั่วไป (เจ้าของร้าน/นักบัญชี)
อ่านไม่ออก · แก้หน้าเดียวแล้วคำเดิมกลับมาที่หน้าใหม่ได้เงียบ ๆ ⇒ ratchet ต่อไฟล์ (ห้ามเพิ่ม · ลดแล้วให้ลด baseline)

กติกา: นับคำต้องห้าม (TERMS) ใน "ข้อความที่ผู้ใช้เห็น" เท่านั้น —
  • markup นอก <script>/<style> (ไม่นับคอมเมนต์ <!-- -->): ข้อความระหว่างแท็ก + ค่า title/placeholder/aria-label/alt
  • ใน <script>: ข้อความในสตริง '…' "…" `…` (ไม่นับคอมเมนต์ // /* */ · ไม่นับนิพจน์ ${…} ใน template) — ชื่อตัวแปร/เมธอด (`fallbackName`, `.push(`) ไม่ถูกนับ
คำที่ใช้ได้แทน: ดู docs/ui-glossary.md · คำในวงเล็บต่อท้ายคำไทยเพื่ออ้างอิง (เช่น "ส่งสำเนาลับ (BCC)") ไม่อยู่ในรายการต้องห้าม

ใช้: python3 tools/ui_glossary_check.py [--self-test] [--write-baseline]
"""
import os
import re
import sys

ROOT = os.path.dirname(os.path.dirname(os.path.abspath(__file__)))
WWW = os.path.join(ROOT, "Accounting", "wwwroot")
BASELINE = os.path.join(ROOT, "tools", "ui_glossary_baseline.txt")

TERMS = [
    ("fallback", re.compile(r"\bfall-?back\b", re.I)),
    ("push", re.compile(r"\bpush\b", re.I)),
    ("Trigger", re.compile(r"\bTrigger\b")),
    ("Default-deny", re.compile(r"\bDefault-deny\b", re.I)),
    ("Sys", re.compile(r"\bSys\b")),
    ("Mail", re.compile(r"(?<![-\w])Mail\b")),
    ("L2", re.compile(r"\bL2\b")),
    ("workspace", re.compile(r"\bworkspace\b", re.I)),
    ("invite", re.compile(r"\binvite\b", re.I)),
    ("Portal", re.compile(r"\bPortal\b")),
    ("Session", re.compile(r"\bSession\b")),
]

SCRIPT_RE = re.compile(r"(<script\b[^>]*>)(.*?)(</script>)", re.S | re.I)
STYLE_RE = re.compile(r"<style\b[^>]*>.*?</style>", re.S | re.I)
ATTR_RE = re.compile(r"""\b(?:title|placeholder|aria-label|alt)\s*=\s*("[^"]*"|'[^']*')""", re.I)


def _markup_text(html):
    html = re.sub(r"<!--.*?-->", " ", html, flags=re.S)
    html = STYLE_RE.sub(" ", html)
    attrs = " ".join(m.group(1)[1:-1] for m in ATTR_RE.finditer(html))
    text = re.sub(r"<[^>]+>", " ", html)
    return text + " " + attrs


def _js_strings(js):
    """ข้อความในสตริงของ JS (ข้ามคอมเมนต์ · ตัด ${…} ออกจาก template) — ตัวแยกแบบเดินทีละตัวอักษร"""
    out = []
    i, n = 0, len(js)
    while i < n:
        c = js[i]
        if c == "/" and i + 1 < n and js[i + 1] == "/":
            j = js.find("\n", i)
            i = n if j < 0 else j
            continue
        if c == "/" and i + 1 < n and js[i + 1] == "*":
            j = js.find("*/", i + 2)
            i = n if j < 0 else j + 2
            continue
        if c in "'\"`":
            q = c
            i += 1
            buf = []
            depth = 0
            while i < n:
                d = js[i]
                if d == "\\":
                    i += 2
                    continue
                if q == "`" and depth == 0 and js.startswith("${", i):
                    depth = 1
                    i += 2
                    continue
                if depth:
                    if d == "{":
                        depth += 1
                    elif d == "}":
                        depth -= 1
                    i += 1
                    continue
                if d == q:
                    break
                if q != "`" and d == "\n":
                    break
                buf.append(d)
                i += 1
            out.append("".join(buf))
            i += 1
            continue
        i += 1
    return " ".join(out)


# เส้นทาง/URL ในสตริง (`/line-invite` · `/api/x/push`) ไม่ใช่ข้อความถึงผู้ใช้ — ตัดทิ้งก่อนนับ (ฝ่ายค้าน WP-13: เดิมฟ้องผิด contacts.html)
PATH_RE = re.compile(r"(?:https?:)?[\w.\-]*(?:/[\w.\-{}$:?=&]+)+/?")


def visible_text(html):
    scripts = []

    def keep(m):
        scripts.append(m.group(2))
        return " "
    markup = SCRIPT_RE.sub(keep, html)
    # สตริงใน JS มักมี HTML ข้างใน ⇒ ส่งผ่านตัวแยก markup อีกชั้น (ตัดแท็ก/คอมเมนต์ · เก็บ title/placeholder)
    return PATH_RE.sub(" ", _markup_text(markup) + " " + " ".join(_markup_text(_js_strings(s)) for s in scripts))


def count(html):
    t = visible_text(html)
    return sum(len(rx.findall(t)) for _, rx in TERMS)


def details(html):
    t = visible_text(html)
    return {name: len(rx.findall(t)) for name, rx in TERMS if rx.findall(t)}


def scan():
    res = {}
    for dp, dn, fn in os.walk(WWW):
        dn[:] = [d for d in dn if d not in ("node_modules", "lib", "vendor")]
        for f in fn:
            if not f.endswith(".html"):
                continue
            rel = os.path.relpath(os.path.join(dp, f), WWW)
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
    base = '<div><p>สวัสดี</p><script>const fallbackName = 1; arr.push(x); // fallback ในคอมเมนต์\n</script></div>'
    if count(base) != 0:
        fails.append(f"ชื่อตัวแปร/เมธอด/คอมเมนต์ถูกนับ ({count(base)}) — ต้องเป็น 0")
    cases = [
        '<p>ระบบจะ fallback ไปใช้ SMTP</p>',
        '<input placeholder="Trigger ที่ใช้">',
        "<script>el.innerHTML = 'หมด Session — login ใหม่';</script>",
        '<script>x = `<b>Default-deny:</b> ${a.push(1)}`;</script>',
        '<th>Sys / Mail / LINE</th>',
    ]
    for c in cases:
        if count(base + c) <= 0:
            fails.append(f"ไม่จับ: {c}")
    if count('<script>x = `${ "fallback" }`;</script>') != 0:
        fails.append("นิพจน์ ${…} ใน template ถูกนับ")
    if count("<script>API.post(this._u('/line-invite'), {}); fetch('/api/x/push');</script>") != 0:
        fails.append("เส้นทาง URL ในสตริงถูกนับ (/line-invite · /api/x/push)")
    if count('<!-- fallback --><p>ส่งสำเนาลับ (BCC)</p>') != 0:
        fails.append("คอมเมนต์ HTML / คำที่อนุญาตถูกนับ")
    # ฉีดลงไฟล์จริงต้องเพิ่ม
    real = os.path.join(WWW, "pages", "notifications.html")
    if os.path.exists(real):
        t = open(real, encoding="utf-8").read()
        if count(t.replace("</body>", "<p>ระบบจะ fallback ไป Sys</p></body>", 1)) <= count(t):
            fails.append("ฉีดคำต้องห้ามลง notifications.html แล้วไม่ถูกจับ")
    return fails


def main():
    if "--self-test" in sys.argv:
        f = self_test()
        for x in f:
            print("❌", x)
        print("✅ self-test ui_glossary_check ผ่าน" if not f else "❌ self-test ล้ม")
        return 1 if f else 0
    found = scan()
    if "--write-baseline" in sys.argv:
        with open(BASELINE, "w", encoding="utf-8") as fh:
            fh.write("# ratchet ของ tools/ui_glossary_check.py (รอบ 252 WP-13 M) — ศัพท์นักพัฒนา/อังกฤษที่ผู้ใช้เห็น ต่อไฟล์ · ลดลงเท่านั้น\n")
            fh.write("# คำแทนดู docs/ui-glossary.md — ห้ามเพิ่มแถว/ตัวเลขเพื่อให้ checker เขียว\n")
            for k in sorted(found):
                fh.write(f"{k} {found[k]}\n")
        print(f"เขียน baseline {len(found)} ไฟล์ · {sum(found.values())} จุด")
        return 0
    f = self_test()
    if f:
        for x in f:
            print("❌", x)
        return 1
    base = load()
    bad = []
    for k, v in sorted(found.items()):
        b = base.get(k, 0)
        if v > b:
            p = os.path.join(WWW, k)
            bad.append(f"{k}: {v} > baseline {b} · {details(open(p, encoding='utf-8', errors='ignore').read())}")
        elif v < b:
            print(f"ℹ️  {k}: ลดจาก {b} เหลือ {v} — ลด baseline (--write-baseline)")
    for k, b in sorted(base.items()):
        if k not in found and b > 0:
            print(f"ℹ️  {k}: เหลือ 0 (baseline {b}) — ลด baseline (--write-baseline)")
    for x in bad:
        print("❌ ศัพท์ต้องห้ามเพิ่ม —", x, "(คำแทน: docs/ui-glossary.md)")
    print(f"ui_glossary_check: {sum(found.values())} จุดใน {len(found)} ไฟล์ · เกิน baseline {len(bad)} ไฟล์")
    return 1 if bad else 0


if __name__ == "__main__":
    sys.exit(main())
