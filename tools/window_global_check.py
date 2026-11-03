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
# รอบ 232: object ของหน้า billing ที่ถูกอ้างข้ามสคริปต์/onclick (`window.UsageTab` ฯลฯ) — เจ้าของคือหน้านั้นเอง
GLOBALS = {"Layout": "js/layout.js", "Bill": "pages/billing.html", "HubTabs": "pages/billing.html",
           "PlanTab": "pages/billing.html", "UsageTab": "pages/billing.html"}

# ── รอบ 232 กติกาที่สอง: const/let/class ระดับสคริปต์ที่ประกาศใน <script> บล็อกหลัง แต่ถูกเรียกจาก <script> บล็อกก่อนหน้า ──
# ที่มา (บั๊กจริง): billing.html — IIFE ในสคริปต์ที่ 2 เรียก `UsageTab.refresh()` แต่ `const UsageTab = {…}` อยู่สคริปต์ที่ 3
# ⇒ "UsageTab is not defined" (สคริปต์ที่ 3 ยังไม่รัน) ⇒ 3 การ์ดค้าง "กำลังโหลด..." · ตรวจเฉพาะการอ้างแบบ `Name.` / `Name(` ที่อยู่นอกคอมเมนต์
_DECL = re.compile(r"^\s*(?:const|let|class)\s+([A-Z][A-Za-z0-9_]*)\b", re.M)
_SCRIPT = re.compile(r"<script(?![^>]*\bsrc=)[^>]*>(.*?)</script>", re.S)


def _strip_comments(js):
    js = re.sub(r"/\*.*?\*/", "", js, flags=re.S)
    return re.sub(r"(^|[^:\\])//[^\n]*", r"\1", js)


def _eager_uses(js, use):
    """ตำแหน่งการอ้างที่ **รันทันทีตอนโหลดสคริปต์** เท่านั้น (ฝ่ายค้าน S4): ระดับบนสุด (depth 0) หรือใน body ตรงของ IIFE ระดับบนสุด
    (depth 1 ของ statement ที่เป็น `(…)()` / `(async () => {…})()`) — ไม่นับใน function/method/callback ซึ่งรันทีหลังเมื่อทุกบล็อกโหลดแล้ว"""
    # แบ่ง statement ระดับบนสุดด้วย depth ของ {} () [] (สตริง/เทมเพลตถูกถอดไปก่อนแบบหยาบ — พอสำหรับการนับวงเล็บ)
    js2 = re.sub(r"`(?:\\.|[^`\\])*`|'(?:\\.|[^'\\\n])*'|\"(?:\\.|[^\"\\\n])*\"", lambda m: " " * len(m.group(0)), js)
    depth = 0; start = 0; stmts = []
    for i, ch in enumerate(js2):
        if ch in "({[": depth += 1
        elif ch in ")}]": depth = max(0, depth - 1)
        if depth == 0 and (ch == ";" or ch == "\n"):
            seg = js2[start:i + 1]
            if seg.strip(): stmts.append((start, seg))
            start = i + 1
    if js2[start:].strip(): stmts.append((start, js2[start:]))
    iife = re.compile(r"^\s*(?:\(\s*)?(?:async\s*)?(?:function\b|\([^)]*\)\s*=>|[A-Za-z_$][\w$]*\s*=>)[\s\S]*\)\s*\(\s*\)\s*;?\s*$")
    found = []
    for off, seg in stmts:
        is_iife = bool(iife.match(seg))
        d = 0
        for m in use.finditer(seg):
            d = 0
            for ch in seg[:m.start()]:
                if ch in "({[": d += 1
                elif ch in ")}]": d = max(0, d - 1)
            if d == 0 or (is_iife and d <= 2):
                found.append(off + m.start())
    return found


def check_script_order(html):
    """คืนรายการ (ชื่อ, บล็อกที่ประกาศ, บล็อกที่เรียกก่อน) — บล็อกนับจาก 1 เฉพาะ <script> inline · นับเฉพาะการอ้างที่รันทันที (_eager_uses)"""
    blocks = [_strip_comments(b) for b in _SCRIPT.findall(html)]
    decl = {}
    for i, b in enumerate(blocks, 1):
        for name in _DECL.findall(b):
            decl.setdefault(name, i)
    errs = []
    for name, at in decl.items():
        use = re.compile(r"(?<![A-Za-z0-9_.$])" + re.escape(name) + r"\s*[.(]")
        for i in range(1, at):
            if _eager_uses(blocks[i - 1], use):
                errs.append((name, at, i))
                break
    return errs


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
        # กติกาที่สอง: เรียกก่อนประกาศข้ามบล็อก = ฟ้อง · ประกาศก่อนเรียก / เรียกในคอมเมนต์ / ชื่อเป็นส่วนของชื่ออื่น = ไม่ฟ้อง
        bad2 = check_script_order("<script>(async()=>{ UsageTab.refresh(); })();</script><script>const UsageTab = { refresh(){} };</script>")
        good2 = check_script_order("<script>const UsageTab = {};</script><script>UsageTab.refresh();</script>"
                                   "<script>// Foo.bar()\nconst Foo = {};</script><script>MyFoo.x(); const MyFoo = 1;</script>")
        # ฝ่ายค้าน S4: การอ้างใน function/method/callback (รันทีหลัง) ไม่ฟ้อง · ระดับบนสุด/IIFE ฟ้อง
        deferred = check_script_order("<script>function go(){ Later.run(); }\nconst Obj = { m(){ Later.run(); } };\n"
                                      "document.addEventListener('x', () => Later.run());\nsetTimeout(function(){ Later.run(); }, 0);</script>"
                                      "<script>const Later = { run(){} };</script>")
        eager = check_script_order("<script>Bill.init();</script><script>const Bill = {};</script>")
        eager_iife = check_script_order("<script>(async () => { for (let i = 0; i < 3; i++) { await x(); } Late.go(); })();</script><script>const Late = {};</script>")
        assert not deferred and eager == [("Bill", 2, 1)] and eager_iife == [("Late", 2, 1)], f"self-test S4 ล้ม: {deferred} {eager} {eager_iife}"
        ext = check_script_order('<script src="/js/x.js"></script><script>const A = {};</script><script>A.go();</script>')
        assert bad2 == [("UsageTab", 2, 1)] and not good2 and not ext, f"self-test กติกาลำดับสคริปต์ล้ม: {bad2} {good2} {ext}"
        print("✅ window_global_check self-test")
        return 0
    referenced = set()
    order_errs = []
    for dp, _, fs in os.walk(ROOT):
        for f in fs:
            if f.endswith((".js", ".html")):
                path = os.path.join(dp, f)
                t = open(path, encoding="utf-8", errors="ignore").read()
                for name in GLOBALS:
                    if "window." + name in t:
                        referenced.add(name)
                if f.endswith(".html"):
                    for name, at, before in check_script_order(t):
                        order_errs.append(f"❌ {os.path.relpath(path, ROOT)}: `{name}` ประกาศใน <script> บล็อกที่ {at} แต่ถูกเรียกจากบล็อกที่ {before} — "
                                          f"บล็อกนั้นรันก่อน ⇒ \"{name} is not defined\" (ย้ายประกาศขึ้นก่อน หรือเรียกผ่าน window.{name} หลังผูก)")
    owners = {n: open(os.path.join(ROOT, p), encoding="utf-8").read() for n, p in GLOBALS.items()}
    errs = check(owners, referenced) + order_errs
    for e in errs:
        print(e)
    return 1 if errs else 0


if __name__ == "__main__":
    sys.exit(main())
