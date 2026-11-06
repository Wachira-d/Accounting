#!/usr/bin/env python3
# -*- coding: utf-8 -*-
"""label ใน `.form-group` ต้องผูกกับช่องจริง (`for` ชี้ id ที่มีในไฟล์ หรือ label ห่อช่องกรอกไว้เอง) — ratchet ต่อไฟล์

═══ ที่มา (รอบ 250 WP-04 ข้อ 13 · AUDIT_249 ทีม D รูปแบบ D) ═══
ทีม D พบฟอร์มจำนวนมากที่ `<label>` ไม่ผูกกับช่อง (ไม่มี `for` / `for` ชี้ id ที่ไม่มี — เปลี่ยน id ช่องแล้วลืม label) ⇒ กดที่ป้ายแล้วไม่โฟกัสช่อง ·
โปรแกรมอ่านหน้าจออ่านช่องไม่มีชื่อ · ทดสอบอัตโนมัติหาช่องจากป้ายไม่ได้ · ของเดิมมีมาก (หลายพันจุด) ⇒ **ratchet ต่อไฟล์**: ห้ามเพิ่ม · ลดแล้วให้ลด baseline

กติกา: `<label>` ที่อยู่ภายใต้ element ที่มี class `form-group` ต้อง (ก) มี `for="X"` และมี `id="X"` ในไฟล์เดียวกัน หรือ (ข) ห่อ `<input>/<select>/<textarea>` ไว้ข้างใน ·
`for` ที่เป็น template (`${…}`) ไม่ตัดสิน (สร้างตอนรัน) · ตรวจเฉพาะ markup ของ .html (เนื้อใน `<script>` ไม่ถูกตีความเป็นแท็ก)

ใช้: python3 tools/form_label_for_check.py [--self-test] [--write-baseline]
"""
import json
import os
import re
import sys
from html.parser import HTMLParser

ROOT = os.path.dirname(os.path.dirname(os.path.abspath(__file__)))
WWW = os.path.join(ROOT, "Accounting", "wwwroot")
BASELINE = os.path.join(ROOT, "tools", "form_label_for_baseline.json")
VOID = {"area", "base", "br", "col", "embed", "hr", "img", "input", "link", "meta", "source", "track", "wbr"}
CONTROLS = {"input", "select", "textarea"}
ID_RE = re.compile(r"""\bid\s*=\s*["']([^"'$]+)["']""")


class _Scan(HTMLParser):
    def __init__(self):
        super().__init__(convert_charrefs=True)
        self.stack = []          # (tag, is_form_group)
        self.labels = []         # [for_value | None, wraps_control, in_form_group]
        self.open_label = None

    def handle_starttag(self, tag, attrs):
        a = dict(attrs)
        if tag == "label":
            in_fg = any(fg for _t, fg in self.stack)
            self.open_label = [a.get("for"), False, in_fg]
            self.labels.append(self.open_label)
        elif tag in CONTROLS and self.open_label is not None:
            self.open_label[1] = True
        if tag not in VOID:
            cls = (a.get("class") or "").split()
            self.stack.append((tag, "form-group" in cls))

    def handle_startendtag(self, tag, attrs):
        if tag in CONTROLS and self.open_label is not None:
            self.open_label[1] = True

    def handle_endtag(self, tag):
        if tag == "label":
            self.open_label = None
        for i in range(len(self.stack) - 1, -1, -1):
            if self.stack[i][0] == tag:
                del self.stack[i:]
                break


def violations(html):
    """[(for_value|None)] ของ label ใน .form-group ที่ไม่ผูกกับช่อง"""
    p = _Scan()
    try:
        p.feed(html)
    except Exception:
        return []
    ids = set(ID_RE.findall(html))
    out = []
    for for_value, wraps, in_fg in p.labels:
        if not in_fg or wraps:
            continue
        if for_value is None or not for_value.strip():
            out.append(None)
        elif "${" in for_value or "{{" in for_value:
            continue
        elif for_value not in ids:
            out.append(for_value)
    return out


def scan():
    counts = {}
    for base, dirs, files in os.walk(WWW):
        dirs[:] = [d for d in dirs if d not in ("lib", "vendor", "node_modules")]
        for f in files:
            if not f.endswith(".html"):
                continue
            path = os.path.join(base, f)
            rel = os.path.relpath(path, ROOT).replace(os.sep, "/")
            n = len(violations(open(path, encoding="utf-8", errors="ignore").read()))
            if n:
                counts[rel] = n
    return counts


def self_test():
    fails = []
    ok = ('<div class="form-group"><label for="a">ชื่อ</label><input id="a"></div>'
          '<div class="form-group"><label>อีเมล <input id="b"></label></div>'
          '<label>นอก form-group ไม่ตรวจ</label>'
          '<div class="form-group"><label for="${x}">template</label></div>')
    if violations(ok):
        fails.append(f"markup ถูกต้องถูกฟ้อง: {violations(ok)}")
    if violations('<div class="form-group"><label>ไม่มี for</label><input id="c"></div>') != [None]:
        fails.append("label ไม่มี for และไม่ห่อช่อง ไม่ถูกฟ้อง")
    if violations('<div class="row form-group"><div><label for="old">id เก่า</label><input id="new"></div></div>') != ["old"]:
        fails.append("for ชี้ id ที่ไม่มี (ซ้อนลึก) ไม่ถูกฟ้อง")
    if violations('<div class="form-group"><script>const t = `<label>${x}</label>`;</script></div>'):
        fails.append("label ใน script ถูกนับ")
    for f in fails:
        print("❌ self-test form_label_for_check: " + f)
    if not fails:
        print("form_label_for_check --self-test: ผ่าน")
    return 1 if fails else 0


def main(argv):
    if "--self-test" in argv:
        return self_test()
    if self_test():
        return 1
    counts = scan()
    if "--write-baseline" in argv:
        with open(BASELINE, "w", encoding="utf-8") as fh:
            json.dump(dict(sorted(counts.items())), fh, ensure_ascii=False, indent=1)
        print(f"เขียน baseline {len(counts)} ไฟล์ · {sum(counts.values())} จุด")
        return 0
    base = json.load(open(BASELINE, encoding="utf-8")) if os.path.exists(BASELINE) else {}
    bad = [(f, n, base.get(f, 0)) for f, n in sorted(counts.items()) if n > base.get(f, 0)]
    for f, n, b in bad:
        print(f"❌ {f}: label ใน .form-group ที่ไม่ผูกช่อง {n} จุด (baseline {b}) — ใส่ for=\"<id ช่อง>\" ที่มีจริง หรือห่อช่องไว้ใน label")
    better = [(f, b, counts.get(f, 0)) for f, b in sorted(base.items()) if counts.get(f, 0) < b]
    for f, b, n in better:
        print(f"ℹ️  {f}: ลดจาก {b} เหลือ {n} — ลด baseline (python3 tools/form_label_for_check.py --write-baseline)")
    print(f"form_label_for_check: {sum(counts.values())} จุดใน {len(counts)} ไฟล์ · เกิน baseline {len(bad)} ไฟล์")
    return 1 if bad else 0


if __name__ == "__main__":
    sys.exit(main(sys.argv[1:]))
