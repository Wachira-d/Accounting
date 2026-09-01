#!/usr/bin/env python3
"""ตรวจว่า <script src="https://..."> / <link rel=stylesheet href="https://...">
ในหน้าเว็บ อยู่ใน allow-list ของ Content-Security-Policy หรือยัง

ที่มา (บั๊กจริง): หน้า login โหลด `https://accounts.google.com/gsi/client` แต่ CSP
ใน `Middleware/SecurityMiddleware.cs` มี script-src แค่ 'self' + fonts.googleapis.com
+ cdn.jsdelivr.net ⇒ **เบราว์เซอร์บล็อกสคริปต์เงียบ ๆ** (ขึ้นเฉพาะใน console)
⇒ `window.google` ไม่เคยมี ⇒ ปุ่ม Google ขึ้น "โหลดบริการ Google ไม่สำเร็จ —
ปิดตัวบล็อกโฆษณาแล้วลองใหม่" ทุกครั้ง ทั้งที่ผู้ใช้ไม่มีตัวบล็อกโฆษณาเลย
(ข้อความโทษผิดตัว → ไล่ต้นเหตุไม่เจอ). Facebook SDK โดนแบบเดียวกัน

ทำไม checker ตัวอื่นมองไม่เห็น: ทั้งสองฝั่งถูกต้องตามไวยากรณ์ทุกประการ —
`node --check` ดูแต่ JS, checker ฝั่ง C# อ่านโครงสร้างโค้ด ไม่มีตัวไหนโยง
"หน้าเว็บโหลดอะไร" เข้ากับ "เซิร์ฟเวอร์อนุญาตอะไร" ซึ่งอยู่คนละไฟล์คนละภาษา
(รูปแบบเดียวกับ upload_route_check: ฝั่งเขียนถูก แต่ฝั่งที่ยอมให้เสิร์ฟไม่รู้เรื่อง)
"""
import os
import re
import sys

ROOT = os.path.dirname(os.path.dirname(os.path.abspath(__file__)))
WWWROOT = os.path.join(ROOT, "Accounting", "wwwroot")
CSP_FILE = os.path.join(ROOT, "Accounting", "Middleware", "SecurityMiddleware.cs")


def load_csp():
    """ดึงสตริง CSP ออกจาก headers.Append("Content-Security-Policy", "..." + "...");
    ต่อ string literal ทุกก้อนเข้าด้วยกัน

    ⚠️ ต้องเดินทีละตัวอักษร **แยกสถานะในสตริง/นอกสตริง** — รุ่นแรกตัดคอมเมนต์ด้วย
    regex `//[^\\n]*` ก่อน ซึ่งไป**กิน `//` ของ `https://`** ที่อยู่ในสตริงเอง
    ⇒ ได้ CSP เพี้ยนจนฟ้องผิด 300+ จุด (fonts.googleapis.com ที่อนุญาตอยู่แล้ว)
    """
    src = open(CSP_FILE, encoding="utf-8").read()
    i = src.find('"Content-Security-Policy"')
    if i < 0:
        return None
    p = i + len('"Content-Security-Policy"')
    out, n = [], len(src)
    while p < n:
        ch = src[p]
        if ch == '"':                       # string literal → เก็บเนื้อใน
            p += 1
            buf = []
            while p < n and src[p] != '"':
                if src[p] == "\\" and p + 1 < n:
                    buf.append(src[p + 1]); p += 2; continue
                buf.append(src[p]); p += 1
            out.append("".join(buf)); p += 1
            continue
        if src.startswith("//", p):          # คอมเมนต์ (นอกสตริงเท่านั้น)
            nl = src.find("\n", p)
            p = n if nl < 0 else nl + 1
            continue
        if src.startswith(");", p):
            break
        p += 1
    return "".join(out)


def parse_directives(csp):
    out = {}
    for part in csp.split(";"):
        toks = part.split()
        if toks:
            out[toks[0]] = toks[1:]
    return out


def allowed(directive_values, origin, fallback):
    """origin เช่น https://accounts.google.com — ผ่านเมื่อถูกระบุตรง ๆ หรือมี
    wildcard scheme (https:) ให้ · ไม่มี directive เลย → ตกไปใช้ default-src"""
    vals = directive_values if directive_values is not None else fallback
    if vals is None:
        return False
    for v in vals:
        if v == origin or v == "https:" or v == "*":
            return True
        if v.startswith(origin.rstrip("/") + "/"):   # อนุญาตเป็นพาธย่อย
            return True
    return False


COMMENT = re.compile(r"<!--.*?-->", re.S)
SCRIPT = re.compile(r"""<script\b[^>]*\bsrc\s*=\s*["'](https?://[^"']+)["']""", re.I)
LINK = re.compile(r"""<link\b[^>]*\bhref\s*=\s*["'](https?://[^"']+)["'][^>]*>""", re.I)


def origin_of(url):
    m = re.match(r"(https?://[^/?#]+)", url)
    return m.group(1) if m else url


def main():
    csp = load_csp()
    if not csp:
        print("❌ หา Content-Security-Policy ใน SecurityMiddleware.cs ไม่เจอ")
        return 1
    d = parse_directives(csp)
    default = d.get("default-src")
    bad = []

    for base, _dirs, files in os.walk(WWWROOT):
        for fn in files:
            if not fn.endswith(".html"):
                continue
            path = os.path.join(base, fn)
            rel = os.path.relpath(path, WWWROOT)
            text = COMMENT.sub("", open(path, encoding="utf-8", errors="replace").read())
            for m in SCRIPT.finditer(text):
                o = origin_of(m.group(1))
                if not allowed(d.get("script-src"), o, default):
                    line = text[:m.start()].count("\n") + 1
                    bad.append((rel, line, "script-src", o))
            for m in LINK.finditer(text):
                tag = m.group(0)
                if "stylesheet" not in tag.lower():
                    continue   # preconnect/dns-prefetch/icon ไม่ถูก CSP บล็อก
                o = origin_of(m.group(1))
                if not allowed(d.get("style-src"), o, default):
                    line = text[:m.start()].count("\n") + 1
                    bad.append((rel, line, "style-src", o))

    for rel, line, directive, o in sorted(bad):
        print(f"❌ {rel}:{line} โหลด {o} แต่ {directive} ของ CSP ไม่อนุญาต "
              f"⇒ เบราว์เซอร์บล็อกเงียบ (หน้าเว็บจะเห็นแค่ว่าไลบรารีไม่มา)")
    print(f"ตรวจ wwwroot เทียบ CSP · ทรัพยากรภายนอกที่ถูกบล็อก {len(bad)} จุด")
    return 1 if bad else 0


if __name__ == "__main__":
    sys.exit(main())
