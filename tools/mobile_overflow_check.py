#!/usr/bin/env python3
"""ด่าน static กัน "หน้าจอมือถือถูกตัดขอบ เลื่อนไม่ได้" กลับมาอีก

ที่มา (2026-10-09 · ผู้ใช้ Android ~412px): "หน้า ocr บนมือถือเห็นปุ่มเมนูไม่ครบ เลื่อนไม่ได้ ตรวจสอบหน้าอื่นๆด้วย"
ทำซ้ำด้วย Playwright + Chromium (390/412px · mock /api/**) พบต้นเหตุร่วมเป็นกลุ่ม:
  1. `.page-content` / `.main-content` ตั้ง `overflow-x:hidden` ⇒ ของที่กว้างกว่าจอถูก **ตัดทิ้ง** และไม่มีทางเลื่อนไปดู
     (การ์ดสแกน OCR กว้าง 732px บนจอ 412px — ปุ่ม "ข้อมูลดิบ/แกะใหม่/ลบทั้งคู่" หายทั้งแถว)
  2. grid `repeat(auto-fill, minmax(360px,1fr))` — คอลัมน์ขั้นต่ำกว้างกว่าเนื้อที่จอมือถือ (360px phone ≈ 336px ใช้ได้)
  3. ตาราง `<table>` ที่ไม่อยู่ในกล่องที่เลื่อนข้างได้ (.table-container ฯลฯ)
  4. หน้าไม่มี `<meta name="viewport">` ⇒ เบราว์เซอร์มือถือเรนเดอร์กว้าง 980px (ตัวหนังสือจิ๋ว เห็น sidebar)
  5. `min-width` ตายตัวบนกล่องระดับหน้า (body/html/.page-content/.main-content) ⇒ บังคับความกว้างเดสก์ท็อป

กติกาที่ตรวจ (ทุกข้อ 0 จุดบนเรพหลังแก้ — ไม่มี baseline):
  R1 ทุก .html ที่มี <head> ต้องมี viewport `width=device-width` และห้าม `width=<ตัวเลข>`
  R2 css/style.css ต้องมีตาข่ายมือถือ: ภายใน `@media (max-width: 768px)` มี `.page-content { overflow-x: auto }`
  R3 ห้ามหน้าใด (.html) ตั้ง overflow hidden/clip ให้ .page-content/#pageContent/.main-content/body/html
  R4 ห้าม min-width ≥ 400px บน body/html/.app-layout/.main-content/.page-content/#pageContent
  R5 ห้าม `minmax(<N>px, …)` ที่ N ≥ 300 โดยไม่ห่อด้วย `min(100%, Npx)` (ทุก .html/.css/.js)
  R6 `<table>` ใน markup คงที่ของ pages/*.html ต้องมีบรรพบุรุษที่เลื่อนข้างได้
     (.table-container · .table-responsive · .card [มือถือ overflow-x:auto] · .modal · inline overflow auto/scroll ·
      คลาสที่ <style> ของหน้านั้นประกาศ overflow auto/scroll) — ตารางที่สร้างใน JS template ตรวจไม่ได้แบบ static
      (ตาข่าย R2 รับช่วงต่อ: ล้นแล้ว "เลื่อนได้" ไม่ใช่ "หาย")

ใช้: python3 tools/mobile_overflow_check.py            # ตรวจเรพ
     python3 tools/mobile_overflow_check.py --self-test # negative test: ฉีดบั๊กแต่ละชนิดลงสำเนาไฟล์จริงแล้วต้องฟ้อง
"""
import re
import sys
from html.parser import HTMLParser
from pathlib import Path

ROOT = Path(__file__).resolve().parent.parent
WWW = ROOT / "Accounting" / "wwwroot"
SKIP_PARTS = {"lib", "vendor", "node_modules", "spec"}

CSS_COMMENT = re.compile(r"/\*.*?\*/", re.S)
HTML_COMMENT = re.compile(r"<!--.*?-->", re.S)
STYLE_BLOCK = re.compile(r"<style[^>]*>(.*?)</style>", re.S | re.I)
VIEWPORT = re.compile(r"<meta\s+[^>]*name\s*=\s*[\"']viewport[\"'][^>]*>", re.I)
PAGE_BOX = r"(?:\.page-content|#pageContent|\.main-content|\.app-layout|\bbody|\bhtml)"
# rule ทั้งก้อน: selector { body }
RULE = re.compile(r"([^{}]+)\{([^{}]*)\}")
MINMAX = re.compile(r"minmax\(\s*(\d+)px")
VOID = {"area", "base", "br", "col", "embed", "hr", "img", "input", "link", "meta", "source", "track", "wbr"}


def strip_css_comments(t):
    return CSS_COMMENT.sub(lambda m: "\n" * m.group(0).count("\n"), t)


def line_of(text, idx):
    return text.count("\n", 0, idx) + 1


def files(exts):
    for p in sorted(WWW.rglob("*")):
        if p.suffix in exts and not any(part in SKIP_PARTS for part in p.relative_to(WWW).parts):
            yield p


def media_blocks(css, max_width):
    """คืน (start,end) ของเนื้อใน `@media (max-width: <max_width>px) { … }` ทุกก้อน (นับปีกกาจับคู่)"""
    out = []
    for m in re.finditer(r"@media[^{]*max-width\s*:\s*%dpx[^{]*\{" % max_width, css):
        depth, i = 1, m.end()
        while i < len(css) and depth:
            if css[i] == "{":
                depth += 1
            elif css[i] == "}":
                depth -= 1
            i += 1
        out.append((m.end(), i - 1))
    return out


# ---------------------------------------------------------------- rules
def check_viewport(path, text):
    probs = []
    if not re.search(r"<head[\s>]", text, re.I):
        return probs
    tags = VIEWPORT.findall(HTML_COMMENT.sub("", text))
    if not tags:
        probs.append((1, "R1 ไม่มี <meta name=\"viewport\" content=\"width=device-width, initial-scale=1.0\"> — มือถือจะเรนเดอร์กว้างแบบเดสก์ท็อป"))
    for t in tags:
        c = re.search(r"content\s*=\s*[\"']([^\"']*)", t, re.I)
        content = c.group(1) if c else ""
        if "width=device-width" not in content.replace(" ", ""):
            probs.append((1, f"R1 viewport ไม่มี width=device-width: {content!r}"))
        if re.search(r"(?<![-\w])width\s*=\s*\d", content):
            probs.append((1, f"R1 viewport ตั้งความกว้างตายตัว (บังคับเดสก์ท็อป): {content!r}"))
    return probs


def check_mobile_net(css):
    for s, e in media_blocks(css, 768):
        for m in RULE.finditer(css[s:e]):
            sels = [x.strip() for x in m.group(1).split(",")]
            if ".page-content" in sels and re.search(r"overflow-x\s*:\s*auto", m.group(2)):
                return []
    return [(1, "R2 ตาข่ายมือถือหาย: ต้องมี `.page-content { overflow-x: auto }` ใน @media (max-width: 768px) — "
                "ไม่งั้น .page-content{overflow-x:hidden} ตัดของที่ล้นทิ้งแบบเลื่อนไม่ได้")]


def check_container_rules(css, in_html):
    """R3 (เฉพาะ .html) + R4 (ทุกไฟล์)"""
    probs = []
    for m in RULE.finditer(css):
        sel, body = m.group(1), m.group(2)
        sel_last = sel.strip().split(",")
        hits_box = any(re.search(PAGE_BOX + r"\s*$", s.strip()) for s in sel_last)
        if not hits_box:
            continue
        ln = line_of(css, m.start(2))
        if in_html and re.search(r"overflow(?:-x)?\s*:\s*(hidden|clip)", body):
            probs.append((ln, f"R3 ตั้ง overflow hidden/clip ให้กล่องระดับหน้า ({sel.strip()[:60]}) — ของที่ล้นจะถูกตัดและเลื่อนไม่ได้บนมือถือ"))
        mw = re.search(r"(?<![-\w])min-width\s*:\s*(\d+)px", body)
        if mw and int(mw.group(1)) >= 400:
            probs.append((ln, f"R4 min-width {mw.group(1)}px บนกล่องระดับหน้า ({sel.strip()[:60]}) — บังคับความกว้างเดสก์ท็อปบนมือถือ"))
    return probs


def check_minmax(text):
    probs = []
    for m in MINMAX.finditer(text):
        n = int(m.group(1))
        if n >= 300:
            probs.append((line_of(text, m.start()), f"R5 minmax({n}px, …) กว้างกว่าเนื้อที่จอมือถือ — ใช้ minmax(min(100%, {n}px), 1fr)"))
    return probs


class TableScan(HTMLParser):
    """ตารางที่ (ก) ≥4 คอลัมน์ (ข) ไม่มีบรรพบุรุษที่เลื่อนข้างได้ (ค) ไม่ถูกซ่อนบนมือถือด้วย class ที่ display:none
    (เช่น deposit-center: การ์ดบนมือถือ · ตารางเฉพาะจอ ≥920px) — ตาราง 2 คอลัมน์แบบ key/value ไม่ล้นจอ ไม่ฟ้อง"""
    def __init__(self, scroll_classes, hidden_classes):
        super().__init__(convert_charrefs=True)
        self.stack = []
        self.scroll_classes = scroll_classes
        self.hidden_classes = hidden_classes
        self.tables = []   # [line, flagged_candidate, cols, rows_seen]
        self.bad = []

    def _scrolls(self, attrs):
        cls = set((attrs.get("class") or "").split())
        if cls & ({"table-container", "table-responsive", "card", "modal"} | self.scroll_classes):
            return True
        style = (attrs.get("style") or "").replace(" ", "")
        return bool(re.search(r"overflow(-x)?:(auto|scroll)", style))

    def handle_starttag(self, tag, attrs):
        a = dict(attrs)
        if tag == "table":
            cls = set((a.get("class") or "").split())
            cand = not any(s for _, s in self.stack) and not (cls & self.hidden_classes)
            self.tables.append([self.getpos()[0], cand, 0, 0])
        elif tag == "tr" and self.tables:
            self.tables[-1][3] += 1
        elif tag in ("th", "td") and self.tables and self.tables[-1][3] <= 1:
            self.tables[-1][2] += int(a.get("colspan") or 1) if str(a.get("colspan") or "1").isdigit() else 1
        if tag not in VOID:
            self.stack.append((tag, self._scrolls(a)))

    def handle_endtag(self, tag):
        if tag == "table" and self.tables:
            line, cand, cols, _ = self.tables.pop()
            if cand and cols >= 4:
                self.bad.append(line)
        for i in range(len(self.stack) - 1, -1, -1):
            if self.stack[i][0] == tag:
                del self.stack[i:]
                break


def classes_with(page_css, prop_re):
    out = set()
    for m in RULE.finditer(page_css):
        if re.search(prop_re, m.group(2)):
            for s in m.group(1).split(","):
                last = s.strip().split()[-1] if s.strip() else ""
                mm = re.fullmatch(r"(?:[a-z]+)?\.([\w-]+)", last)
                if mm:
                    out.add(mm.group(1))
    return out


def check_tables(text):
    css = strip_css_comments("\n".join(STYLE_BLOCK.findall(text)))
    p = TableScan(classes_with(css, r"overflow(?:-x)?\s*:\s*(auto|scroll)"), classes_with(css, r"(?<![-\w])display\s*:\s*none"))
    p.feed(HTML_COMMENT.sub(lambda m: "\n" * m.group(0).count("\n"), text))
    return [(ln, "R6 <table> ไม่อยู่ในกล่องที่เลื่อนข้างได้ — ห่อด้วย <div class=\"table-container\">") for ln in p.bad]


# ---------------------------------------------------------------- driver
def scan(overrides=None):
    """overrides: {Path: text} — ใช้แทนเนื้อไฟล์จริง (self-test)"""
    overrides = overrides or {}
    read = lambda p: overrides.get(p) if p in overrides else p.read_text(encoding="utf-8", errors="replace")
    probs = []
    style = WWW / "css" / "style.css"
    for p in files({".css"}):
        css = strip_css_comments(read(p))
        probs += [(p, *x) for x in check_container_rules(css, in_html=False)]
        probs += [(p, *x) for x in check_minmax(css)]
        if p == style:
            probs += [(p, *x) for x in check_mobile_net(css)]
    for p in files({".html"}):
        text = read(p)
        probs += [(p, *x) for x in check_viewport(p, text)]
        css = strip_css_comments("\n".join(STYLE_BLOCK.findall(HTML_COMMENT.sub("", text))))
        probs += [(p, *x) for x in check_container_rules(css, in_html=True)]
        # ไม่ตัด /* */ ทั้งไฟล์ — HTML มี accept="image/*" ฯลฯ ทำให้ regex คอมเมนต์กินข้ามหลายร้อยบรรทัด (พลาด site-settings)
        probs += [(p, *x) for x in check_minmax(HTML_COMMENT.sub(lambda m: "\n" * m.group(0).count("\n"), text))]
        if p.parent.name == "pages":
            probs += [(p, *x) for x in check_tables(text)]
    for p in files({".js"}):
        probs += [(p, *x) for x in check_minmax(read(p))]
    return probs


def self_test():
    real = lambda rel: (WWW / rel, (WWW / rel).read_text(encoding="utf-8"))
    cases = []
    p, t = real("pages/document-scan.html")
    cases.append(("R1 ลบ viewport", {p: VIEWPORT.sub("", t, count=1)}, "R1"))
    cases.append(("R1 viewport width=1024", {p: VIEWPORT.sub('<meta name="viewport" content="width=1024">', t, count=1)}, "R1"))
    cases.append(("R5 คืน minmax(360px) ของ scan-grid", {p: t.replace("minmax(min(100%, 360px), 1fr)", "minmax(360px, 1fr)", 1)}, "R5"))
    cases.append(("R3 page-content hidden ในหน้า", {p: t.replace("</style>", ".page-content{overflow-x:hidden}</style>", 1)}, "R3"))
    # ไฟล์ยาวที่มี accept="image/*" — เคยทำให้ตัวตัดคอมเมนต์กินข้ามบรรทัดจนพลาด inline style (ล็อกไว้)
    ss, sst = real("admin/site-settings.html")
    assert "minmax(min(100%, 300px),1fr)" in sst, "self-test: site-settings.html เปลี่ยน (แก้ self-test)"
    cases.append(("R5 inline minmax(300px) ใน admin/site-settings.html", {ss: sst.replace("minmax(min(100%, 300px),1fr)", "minmax(300px,1fr)", 1)}, "R5"))
    s, st = real("css/style.css")
    net = re.search(r"\.page-content \{ overflow-x: auto;[^}]*\}", st)
    assert net, "self-test: หา .page-content { overflow-x: auto … } ใน style.css ไม่เจอ (ข้อความเปลี่ยน — แก้ self-test)"
    cases.append(("R2 ถอดตาข่ายมือถือ", {s: st.replace(net.group(0), "", 1)}, "R2"))
    cases.append(("R4 body min-width 1200px", {s: st + "\nbody { min-width: 1200px; }\n"}, "R4"))
    lv, lt = real("pages/leave.html")
    unwrapped = re.sub(r'<div class="table-container">\s*(<table class="table">.*?</table>)\s*</div>', r"\1", lt, count=1, flags=re.S)
    assert unwrapped != lt, "self-test: leave.html ไม่มีตารางที่ห่อ table-container แล้ว (แก้ self-test)"
    cases.append(("R6 แกะ table-container ออกจาก leave.html", {lv: unwrapped}, "R6"))
    # ทิศตรงข้าม: ของที่ถูกต้องต้องไม่ถูกฟ้อง
    ok_cases = [
        ("min(100%, 360px) ไม่ฟ้อง", {p: t.replace("</style>", ".x{grid-template-columns:repeat(auto-fill,minmax(min(100%, 420px),1fr))}</style>", 1)}),
        ("ตารางใน .card ไม่ฟ้อง", {lv: unwrapped.replace('<table class="table">', '<div class="card"><table class="table">', 1).replace("</table>", "</table></div>", 1)}),
        ("min-width 200px บน .page-content ไม่ฟ้อง", {s: st + "\n.page-content { min-width: 200px; }\n"}),
    ]
    fail = 0
    base = scan()
    if base:
        print(f"self-test ล้ม: เรพจริงต้องสะอาดก่อน (พบ {len(base)} จุด)")
        return 1
    for name, ov, code in cases:
        got = [x for x in scan(ov) if x[2].startswith(code)]
        if not got:
            print(f"self-test ล้ม: '{name}' ต้องถูกฟ้องด้วย {code} แต่ไม่ถูกฟ้อง")
            fail = 1
    for name, ov in ok_cases:
        got = scan(ov)
        if got:
            print(f"self-test ล้ม: '{name}' ต้องไม่ถูกฟ้อง แต่ฟ้อง: {got[0][2]}")
            fail = 1
    if not fail:
        print(f"self-test: ผ่าน ({len(cases)} บั๊กที่ฉีดถูกจับครบ · {len(ok_cases)} เคสถูกต้องไม่ถูกฟ้อง)")
    return fail


def main():
    if "--self-test" in sys.argv:
        return self_test()
    probs = scan()
    for p, ln, msg in probs:
        print(f"{p.relative_to(ROOT)}:{ln}: {msg}")
    if probs:
        print(f"\n❌ mobile_overflow_check: {len(probs)} จุด — หน้าจอมือถือจะถูกตัดขอบ/เลื่อนไม่ได้ (ดูหัวไฟล์นี้)")
        return 1
    print("✅ mobile_overflow_check: viewport · ตาข่าย .page-content · minmax · ตาราง — สะอาด")
    return 0


if __name__ == "__main__":
    sys.exit(main())
