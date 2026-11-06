#!/usr/bin/env python3
# -*- coding: utf-8 -*-
"""`<table>` กว้าง (≥ 5 คอลัมน์) ใน `.modal-body` ต้องอยู่ใน `.table-container` (เลื่อนแนวนอนได้บนมือถือ) — ratchet ต่อไฟล์

═══ ที่มา (รอบ 250 WP-04 ข้อ 13 · AUDIT_249 ทีม D รูปแบบ F) ═══
ตารางหลายคอลัมน์ในหน้าต่าง (modal) ที่ไม่ห่อ `.table-container` ⇒ บนมือถือคอลัมน์ขวาหลุดขอบ กดปุ่มท้ายแถวไม่ได้ (ญาติของ `mobile_overflow_check`
ที่ตรวจตารางในหน้าหลัก) · ของเดิมมีอยู่ ⇒ ratchet ต่อไฟล์: ห้ามเพิ่ม · ลดแล้วให้ลด baseline

กติกา: `<table>` ที่มีบรรพบุรุษ class `modal-body` และแถวแรก (`<tr>` แรกใน thead/tbody) มีเซลล์ (th/td · นับ colspan) ≥ 5 ต้องมีบรรพบุรุษ class
`table-container` ที่อยู่ "ใน" modal-body นั้น · ตรวจ markup ของ .html (เนื้อใน `<script>` ไม่ถูกตีความ)

ใช้: python3 tools/modal_table_container_check.py [--self-test] [--write-baseline]
"""
import json
import os
import sys
from html.parser import HTMLParser

ROOT = os.path.dirname(os.path.dirname(os.path.abspath(__file__)))
WWW = os.path.join(ROOT, "Accounting", "wwwroot")
BASELINE = os.path.join(ROOT, "tools", "modal_table_container_baseline.json")
VOID = {"area", "base", "br", "col", "embed", "hr", "img", "input", "link", "meta", "source", "track", "wbr"}
MIN_COLUMNS = 5


class _Scan(HTMLParser):
    def __init__(self):
        super().__init__(convert_charrefs=True)
        self.stack = []        # (tag, classes)
        self.tables = []       # [in_modal, wrapped, first_row_cells, row_done]
        self.cur = []          # ตารางที่เปิดอยู่ (ซ้อนได้)
        self.row_open = []     # ต่อตาราง: กำลังอยู่ใน tr แรกไหม

    def _classes_above(self):
        return [set(c) for _t, c in self.stack]

    def handle_starttag(self, tag, attrs):
        a = dict(attrs)
        if tag == "table":
            above = self._classes_above()
            modal_idx = max((i for i, c in enumerate(above) if "modal-body" in c), default=-1)
            wrapped = modal_idx >= 0 and any("table-container" in c for c in above[modal_idx + 1:])
            t = [modal_idx >= 0, wrapped, 0, False]
            self.tables.append(t)
            self.cur.append(t)
            self.row_open.append(False)
        elif self.cur:
            t = self.cur[-1]
            if tag == "tr" and not t[3]:
                self.row_open[-1] = True
            elif tag in ("th", "td") and self.row_open[-1]:
                try:
                    t[2] += max(1, int(a.get("colspan") or 1))
                except ValueError:
                    t[2] += 1
        if tag not in VOID:
            self.stack.append((tag, (a.get("class") or "").split()))

    def handle_endtag(self, tag):
        if self.cur:
            if tag == "tr" and self.row_open[-1]:
                self.row_open[-1] = False
                self.cur[-1][3] = True
            elif tag == "table":
                self.cur.pop()
                self.row_open.pop()
        for i in range(len(self.stack) - 1, -1, -1):
            if self.stack[i][0] == tag:
                del self.stack[i:]
                break


def violations(html):
    """จำนวนตารางกว้างใน modal-body ที่ไม่ห่อ table-container"""
    p = _Scan()
    p.feed(html)   # ฝ่ายค้าน WP-04 ต่อยอด: parse ล้ม = ฟ้อง (ผู้เรียกจด) — ห้ามกลืนเป็น "0 จุด"
    p.close()
    return sum(1 for in_modal, wrapped, cols, _d in p.tables if in_modal and not wrapped and cols >= MIN_COLUMNS)


PARSE_ERRORS = []


def scan():
    counts = {}
    for base, dirs, files in os.walk(WWW):
        dirs[:] = [d for d in dirs if d not in ("lib", "vendor", "node_modules")]
        for f in files:
            if not f.endswith(".html"):
                continue
            path = os.path.join(base, f)
            rel = os.path.relpath(path, ROOT).replace(os.sep, "/")
            try:
                n = violations(open(path, encoding="utf-8", errors="ignore").read())
            except Exception as e:   # อ่าน/parse ไม่ได้ = ตรวจไม่ได้ ⇒ ล้มดัง ไม่ใช่ผ่าน
                PARSE_ERRORS.append(f"{rel}: {type(e).__name__}: {e}")
                continue
            if n:
                counts[rel] = n
    return counts


def _row(n):
    return "<thead><tr>" + "".join(f"<th>c{i}</th>" for i in range(n)) + "</tr></thead>"


def self_test():
    fails = []
    wide = f"<table>{_row(5)}<tbody><tr><td>1</td></tr></tbody></table>"
    if violations(f'<div class="modal-body"><div class="table-container">{wide}</div></div>') != 0:
        fails.append("ตารางกว้างที่ห่อ table-container ถูกฟ้อง")
    if violations(f'<div class="modal-body"><table>{_row(4)}</table></div>') != 0:
        fails.append("ตาราง 4 คอลัมน์ถูกฟ้อง")
    if violations(f'<div class="page"><table>{_row(8)}</table></div>') != 0:
        fails.append("ตารางนอก modal ถูกฟ้อง (งานของ mobile_overflow_check)")
    if violations(f'<div class="modal-body"><p>x</p>{wide}</div>') != 1:
        fails.append("ตารางกว้างใน modal-body ที่ไม่ห่อ ไม่ถูกฟ้อง")
    if violations('<div class="modal-body"><table><tr><th colspan="3">a</th><th colspan="2">b</th></tr></table></div>') != 1:
        fails.append("colspan ไม่ถูกนับ")
    if violations(f'<div class="table-container"><div class="modal-body">{wide}</div></div>') != 1:
        fails.append("table-container นอก modal-body ถูกนับว่าห่อ")
    if violations(f'<div class="modal-body"><script>const t = `{wide}`;</script></div>') != 0:
        fails.append("ตารางใน script ถูกนับ")
    try:
        violations(None)   # อินพุตที่ parse ไม่ได้ต้องโยน (ไม่ใช่คืน 0 จุดเงียบ)
        fails.append("อินพุตที่ parse ไม่ได้ถูกกลืนเป็นผ่าน")
    except Exception:
        pass
    for f in fails:
        print("❌ self-test modal_table_container_check: " + f)
    if not fails:
        print("modal_table_container_check --self-test: ผ่าน")
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
    if PARSE_ERRORS:
        for e in PARSE_ERRORS:
            print(f"❌ parse ไม่ได้ (ตรวจไม่ได้ = ไม่ผ่าน): {e}")
        return 1
    base = json.load(open(BASELINE, encoding="utf-8")) if os.path.exists(BASELINE) else {}
    bad = [(f, n, base.get(f, 0)) for f, n in sorted(counts.items()) if n > base.get(f, 0)]
    for f, n, b in bad:
        print(f"❌ {f}: ตาราง ≥ {MIN_COLUMNS} คอลัมน์ใน .modal-body ที่ไม่ห่อ .table-container {n} จุด (baseline {b})")
    for f, b in sorted(base.items()):
        if counts.get(f, 0) < b:
            print(f"ℹ️  {f}: ลดจาก {b} เหลือ {counts.get(f, 0)} — ลด baseline (--write-baseline)")
    print(f"modal_table_container_check: {sum(counts.values())} จุดใน {len(counts)} ไฟล์ · เกิน baseline {len(bad)} ไฟล์")
    return 1 if bad else 0


if __name__ == "__main__":
    sys.exit(main(sys.argv[1:]))
