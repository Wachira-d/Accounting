#!/usr/bin/env python3
"""ห้ามต่อค่าที่ไม่ผ่านตัวหนีเข้า HTML template (`${...}` ใน template literal ที่มีแท็ก) — CLAUDE.md #4 C · XSS ⇒ ขโมย JWT ใน localStorage

ที่มา (รอบ 250 WP-03 · ผลตรวจ D §2.1 · §3.7 · §6.5 · §8.5 · §9.5 + ฝ่ายค้าน M1): รุ่นแรกเป็น allowlist ชื่อฟิลด์ (`name`, `code` …)
⇒ `${c.phone}` ใน contacts.html หลุด · รุ่นนี้เป็น **denylist**: ทุก `${expr}` ในบริบท HTML ฟ้อง เว้นแต่ expr "ปลอดภัยโดยรูป":
  • เรียกตัวหนี/ตัวจัดรูปที่คืนข้อความปลอดภัย: Layout.esc( · esc( · escapeHtml( · Layout.jsArg( · jsArg( · Layout.money( · money( ·
    Layout.date( · dateThai( · Layout.statusBadge( · encodeURIComponent( · Number( · parseInt( · parseFloat( · Math.* · Layout.renderPagination( ·
    และ helper ของหน้าที่ประกอบ HTML ที่หนีภายในแล้ว (รายการ SAFE_CALLS — เพิ่มได้เมื่ออ่านตัว helper แล้ว)
  • ตัวเลขล้วน / `.length` / `.toFixed(` / `.join(...)` ของ `.map(` ที่ตัวลูกเป็น template (ตัวลูกถูกตรวจแยกตามบริบท)
  • นิพจน์เงื่อนไข (`a ? X : Y` · `a && X` · `a || X`) ที่ "ใบ" ทุกใบปลอดภัย (template/สตริงลิเทอรัล/ตัวเลข/เรียกตัวหนี) — เงื่อนไขไม่ถูกแสดงจึงไม่ตรวจ
  `String(x)` **ไม่**ถือว่าปลอดภัย · bare `${x}` ฟ้อง (ตัวแปรที่เป็น HTML ประกอบแล้วต้องใส่ `/* html */` ต่อท้ายชื่อ: `${rows /* html */}` — ผู้เขียนยืนยันว่าตัวแปรนั้นหนีแล้ว)
  หมายเหตุ `esc-ok` บนบรรทัด ไม่ยอมรับอีกแล้ว (ฝ่ายค้าน M1: บรรทัดที่มี `.title =` ถูกข้ามทั้งบรรทัด) — ใช้ `/* html */` หรือ `/* text */` (ค่าไป textContent/title) ต่อ expr แทน

บริบท HTML = template literal ที่ข้อความลิเทอรัลมี `<` หรืออยู่ซ้อนใน template ที่เป็น HTML · template ล้วนของ URL/ข้อความ (ไม่มี `<`) ไม่ตรวจ
ratchet: `BASELINE` ต่อไฟล์ — หน้าที่ WP-03 แตะ = 0 · documents.html (14k บรรทัด · จุดเดิมจำนวนมาก) = ตัวเลขปัจจุบัน ลดได้ ห้ามเพิ่ม
`--self-test`: รูปดิบต้องถูกฟ้อง (รวม `${c.phone}` · bare · `String(x)` · method call) · รูปที่หนีแล้วไม่ถูกฟ้อง · ฉีดลงสำเนาไฟล์จริงแล้วต้องถูกจับ
"""
import os
import re
import sys

ROOT = os.path.dirname(os.path.dirname(os.path.abspath(__file__)))
W = os.path.join("Accounting", "wwwroot")
FILES = [
    os.path.join(W, "pages", "fixed-assets.html"),
    os.path.join(W, "pages", "recurring.html"),
    os.path.join(W, "pages", "contacts.html"),
    os.path.join(W, "pages", "email-schedule.html"),
    os.path.join(W, "pages", "notifications.html"),
    os.path.join(W, "portal.html"),
    os.path.join(W, "pay-link.html"),
    os.path.join(W, "pages", "documents.html"),
    # รอบ 252 WP-12A — หน้า Autopilot ใหม่ (ข้อความจากเซิร์ฟเวอร์/OCR/รายการเดินบัญชีทุกช่อง) · ratchet 0 ตั้งแต่ไฟล์เกิด
    os.path.join(W, "pages", "autopilot.html"),
]
BASELINE_FILE = os.path.join(ROOT, "tools", "html_interpolation_escape_baseline.txt")

SAFE_CALLS = [
    r"Layout\.esc", r"esc", r"this\.escapeHtml", r"escapeHtml", r"Layout\.jsArg", r"jsArg", r"Layout\.money", r"money",
    r"Layout\.date", r"Layout\.dateTime", r"dateThai", r"Layout\.statusBadge", r"encodeURIComponent", r"Number", r"parseInt", r"parseFloat",
    r"Math\.\w+", r"Layout\.renderPagination", r"Layout\.fmtNum", r"Layout\.number", r"AdminLayout\.esc",
]
SAFE_CALL_RE = re.compile(r"^(?:" + "|".join(SAFE_CALLS) + r")\s*\(")
NUM_RE = re.compile(r"^-?\d+(\.\d+)?$")
PRAGMA_RE = re.compile(r"/\*\s*(html|text)\b[^*]*\*/")


def scan_templates(src):
    """คืน list ของ (บรรทัด, expr, html_context) ทุก ${} ใน template literal — เดินอักขระแยกสถานะสตริง/คอมเมนต์ · ซ้อนได้"""
    out = []
    n = len(src)

    def line_of(pos):
        return src.count("\n", 0, pos) + 1

    def skip_string(i, q):
        i += 1
        while i < n and src[i] != q:
            if src[i] == "\\":
                i += 2; continue
            if src[i] == "\n" and q != "`":
                return i
            i += 1
        return i + 1

    def regex_allowed(i):
        # regex literal เมื่อตัวก่อนหน้า (ไม่นับช่องว่าง) เป็นตัวดำเนินการ/วงเล็บเปิด/ต้นบรรทัด หรือคำว่า return
        k = i - 1
        while k >= 0 and src[k] in " \t\r\n":
            k -= 1
        if k < 0:
            return True
        if src[k] in "(,=:[!&|?{};+-*%<>~^":
            return True
        return src[max(0, k - 5):k + 1].endswith("return")

    def skip_regex(i):
        i += 1
        in_class = False
        while i < n and src[i] != "\n":
            ch = src[i]
            if ch == "\\":
                i += 2; continue
            if ch == "[":
                in_class = True
            elif ch == "]":
                in_class = False
            elif ch == "/" and not in_class:
                return i + 1
            i += 1
        return i

    def parse_template(i, parent_html):
        """i ชี้ที่ ` เปิด · คืน index หลัง ` ปิด"""
        i += 1
        lits, exprs = [], []
        buf = []
        while i < n and src[i] != "`":
            c = src[i]
            if c == "\\":
                buf.append(src[i:i + 2]); i += 2; continue
            if c == "$" and i + 1 < n and src[i + 1] == "{":
                lits.append("".join(buf)); buf = []
                start = i + 2
                j, nested = parse_expr(start)
                exprs.append((line_of(i), src[start:j - 1], nested, start))
                i = j; continue
            buf.append(c); i += 1
        lits.append("".join(buf))
        html = parent_html or any("<" in l for l in lits)
        for ln, e, nested, start in exprs:
            out.append((ln, e, html))
            for (np, nend) in nested:
                parse_template(np, html)
        return i + 1

    def parse_expr(i):
        """i ชี้หลัง ${ · คืน (index หลัง } ปิด, [ตำแหน่ง template ซ้อน])"""
        depth = 1
        nested = []
        while i < n and depth > 0:
            c = src[i]
            if c in "'\"":
                i = skip_string(i, c); continue
            if c == "`":
                start = i
                # ข้าม template ซ้อนแบบหยาบ (ตรวจจริงใน parse_template รอบถัดไป)
                k = i + 1; d2 = 0
                while k < n:
                    if src[k] == "\\":
                        k += 2; continue
                    if src[k] == "$" and k + 1 < n and src[k + 1] == "{":
                        d2 += 1; k += 2; continue
                    if src[k] == "}" and d2 > 0:
                        d2 -= 1; k += 1; continue
                    if src[k] == "`" and d2 == 0:
                        break
                    k += 1
                nested.append((start, k + 1))
                i = k + 1; continue
            if c == "{":
                depth += 1
            elif c == "}":
                depth -= 1
            i += 1
        return i, nested

    i = 0
    while i < n:
        c = src[i]
        if src.startswith("//", i) and (i == 0 or src[i - 1] != ":"):
            nl = src.find("\n", i); i = n if nl < 0 else nl; continue
        if src.startswith("/*", i):
            e = src.find("*/", i + 2); i = n if e < 0 else e + 2; continue
        if c in "'\"":
            i = skip_string(i, c); continue
        if c == "`":
            i = parse_template(i, False); continue
        if c == "/" and regex_allowed(i):
            i = skip_regex(i); continue
        i += 1
    return out


def split_top(expr, ops):
    """แยกนิพจน์ตามตัวดำเนินการระดับบนสุด (นอกวงเล็บ/สตริง/template)"""
    parts, buf, depth, i, n = [], [], 0, 0, len(expr)
    while i < n:
        c = expr[i]
        if c in "'\"`":
            q = c; j = i + 1
            d2 = 0
            while j < n:
                if expr[j] == "\\":
                    j += 2; continue
                if q == "`" and expr[j] == "$" and j + 1 < n and expr[j + 1] == "{":
                    d2 += 1; j += 2; continue
                if q == "`" and expr[j] == "}" and d2 > 0:
                    d2 -= 1; j += 1; continue
                if expr[j] == q and d2 == 0:
                    break
                j += 1
            buf.append(expr[i:j + 1]); i = j + 1; continue
        if c in "([{":
            depth += 1
        elif c in ")]}":
            depth -= 1
        if depth == 0:
            hit = next((op for op in ops if expr.startswith(op, i)), None)
            if hit and not (hit == "?" and expr.startswith("?.", i)) and not (hit == "?" and expr.startswith("??", i)):
                parts.append("".join(buf)); buf = []; parts.append(hit); i += len(hit); continue
        buf.append(c); i += 1
    parts.append("".join(buf))
    return parts


def balanced_call(e):
    """e = NAME(...) ทั้งก้อน (วงเล็บปิดตัวสุดท้ายคือของ NAME)"""
    m = SAFE_CALL_RE.match(e)
    if not m:
        return False
    depth = 0
    for idx in range(m.end() - 1, len(e)):
        ch = e[idx]
        if ch == "(":
            depth += 1
        elif ch == ")":
            depth -= 1
            if depth == 0:
                return idx == len(e) - 1
    return False


def leaf_safe(e):
    e = e.strip()
    while e.startswith("(") and e.endswith(")") and balanced_parens(e[1:-1]):
        e = e[1:-1].strip()
    if not e:
        return True
    if PRAGMA_RE.search(e):
        return True
    if e[0] in "'\"`":
        return True          # สตริง/template ลิเทอรัล (template ซ้อนถูกตรวจแยก)
    if NUM_RE.match(e):
        return True
    if balanced_call(e):
        return True
    if re.search(r"\.length$", e) or re.search(r"\.toFixed\([^()]*\)$", e):
        return True
    if re.search(r"\.map\(", e) and re.search(r"\.join\(\s*(['\"`]).*?\1\s*\)$", e, re.S):
        return True          # ตัวลูกเป็น template — ตรวจแยกตามบริบท
    return False


def balanced_parens(s):
    d = 0
    for ch in s:
        if ch == "(":
            d += 1
        elif ch == ")":
            d -= 1
            if d < 0:
                return False
    return d == 0


def expr_safe(e):
    e = e.strip()
    while e.startswith("(") and e.endswith(")") and balanced_parens(e[1:-1]):
        e = e[1:-1].strip()
    if leaf_safe(e):
        return True
    parts = split_top(e, ["?", ":"])
    if len(parts) > 1 and "?" in parts:
        # cond ? A : B (ซ้อนต่อท้ายได้: a ? b : c ? d : e) — ตรวจเฉพาะผลลัพธ์ · เงื่อนไขไม่ถูกแสดง
        k = parts.index("?")
        depth, m = 0, None
        for idx in range(k + 1, len(parts)):
            if parts[idx] == "?":
                depth += 1
            elif parts[idx] == ":":
                if depth == 0:
                    m = idx; break
                depth -= 1
        if m is None:
            return False
        true_br = "".join(parts[k + 1:m])
        false_br = "".join(parts[m + 1:])
        return expr_safe(true_br) and expr_safe(false_br)
    parts = split_top(e, ["||", "&&", "??"])
    if len(parts) > 1:
        ops = [p for p in parts if p in ("||", "&&", "??")]
        vals = [p for p in parts if p not in ("||", "&&", "??")]
        # a && X — แสดง X (a เป็นเงื่อนไข) · a || X / a ?? X — แสดงได้ทั้งสองข้าง
        if all(o == "&&" for o in ops):
            return expr_safe(vals[-1])
        return all(expr_safe(v) for v in vals)
    return False


def scan_text(text):
    hits = []
    for ln, e, html in scan_templates(text):
        if html and not expr_safe(e):
            hits.append((ln, "${" + e.strip()[:80] + "}"))
    return hits


def load_baseline():
    base = {}
    if os.path.exists(BASELINE_FILE):
        for line in open(BASELINE_FILE, encoding="utf-8"):
            line = line.strip()
            if not line or line.startswith("#"):
                continue
            f, c = line.rsplit(" ", 1)
            base[f] = int(c)
    return base


def scan_repo(update=False):
    base = load_baseline()
    bad, report, counts = False, [], {}
    for rel in FILES:
        path = os.path.join(ROOT, rel)
        if not os.path.exists(path):
            report.append(f"⚠️ ไม่พบไฟล์ {rel} (ย้ายแล้ว? แก้ FILES)"); bad = True; continue
        hits = scan_text(open(path, encoding="utf-8").read())
        counts[rel] = len(hits)
        allowed = base.get(os.path.basename(rel), 0)
        if len(hits) > allowed:
            bad = True
            report.append(f"❌ {rel}: {len(hits)} จุด (baseline {allowed}) — ห่อด้วย Layout.esc()/esc()/escapeHtml() ก่อนต่อเข้า HTML")
            for no, frag in hits[:25]:
                report.append(f"   :{no}  {frag}")
        elif len(hits) < allowed:
            report.append(f"ℹ️ {rel}: {len(hits)} < baseline {allowed} — ลดตัวเลขใน html_interpolation_escape_baseline.txt (ratchet)")
    return bad, report, counts


SELF_BAD = """
a.innerHTML = `<td>${a.name}</td><td>${c.phone || '-'}</td>`;
b.innerHTML = `<div>${value}</div>`;
c.innerHTML = `<b>${String(x.label)}</b>`;
d.innerHTML = `<i>${x.name.trim()}</i>`;
e.innerHTML = `<p>${ok ? `<b>${r.documentType}</b>` : ''}</p>`;
"""
SELF_OK = """
a.innerHTML = `<td>${Layout.esc(a.name)}</td><td>${Layout.esc(c.phone || '-')}</td>`;
u = `/api/companies/${cid}/x/${id}`;
b.innerHTML = `<div>${rows /* html */}</div><span>${n}</span>`.replace('x','y');
c.innerHTML = `<ul>${list.map(x => `<li>${esc(x)}</li>`).join('')}</ul>`;
d.innerHTML = `<p>${flag ? '<b>ใช่</b>' : ''}${Layout.money(t)} ${items.length} ${(v || 0).toFixed(2)}</p>`;
e.innerHTML = `<p>${ok && `<b>${Layout.esc(z)}</b>`}</p>`;
"""


def self_test():
    fails = []
    bad = scan_text(SELF_BAD)
    want = ["${a.name}", "${c.phone || '-'}", "${value}", "${String(x.label)}", "${x.name.trim()}", "${r.documentType}"]
    got = [b[1] for b in bad]
    for w in want:
        if w not in got:
            fails.append(f"ต้องถูกฟ้อง: {w} (ได้ {got})")
    ok = scan_text(SELF_OK)
    # `${n}` ใน SELF_OK เป็น bare — ต้องถูกฟ้องตามกติกา (ไม่มี pragma) ⇒ คาดว่ามี 1 จุดพอดี
    if [o[1] for o in ok] != ["${n}"]:
        fails.append(f"รูปที่หนีแล้วต้องไม่ถูกฟ้อง (ยกเว้น bare ${{n}}) แต่ได้ {[o[1] for o in ok]}")
    fa = os.path.join(ROOT, FILES[0])
    if os.path.exists(fa):
        t = open(fa, encoding="utf-8").read()
        injected = t.replace("${Layout.esc(a.name)}${a.isUnderAcquisition", "${a.name}${a.isUnderAcquisition", 1)
        if injected == t:
            fails.append("หาจุดฉีดใน fixed-assets.html ไม่เจอ (โค้ดเปลี่ยน — แก้ self-test)")
        elif len(scan_text(injected)) <= len(scan_text(t)):
            fails.append("ฉีด ${a.name} ดิบลงสำเนา fixed-assets.html แล้วไม่ถูกฟ้อง")
    ct = os.path.join(ROOT, FILES[2])
    if os.path.exists(ct):
        t = open(ct, encoding="utf-8").read()
        injected = t.replace("${Layout.esc(c.phone || '-')}", "${c.phone || '-'}", 1)
        if injected == t:
            fails.append("หาจุดฉีด c.phone ใน contacts.html ไม่เจอ (แก้ self-test)")
        elif len(scan_text(injected)) <= len(scan_text(t)):
            fails.append("ฉีด ${c.phone} ดิบลงสำเนา contacts.html แล้วไม่ถูกฟ้อง")
    if fails:
        print("❌ self-test html_interpolation_escape_check:\n  " + "\n  ".join(fails))
        return 1
    print("✅ self-test html_interpolation_escape_check ผ่าน (รูปดิบ 6 แบบถูกฟ้อง · รูปที่หนีแล้วไม่ถูกฟ้อง · ฉีดลง fixed-assets/contacts ถูกจับ)")
    return 0


if __name__ == "__main__":
    if "--self-test" in sys.argv:
        sys.exit(self_test())
    if "--counts" in sys.argv:
        _, _, counts = scan_repo()
        for f, c in counts.items():
            print(os.path.basename(f), c)
        sys.exit(0)
    if "--list" in sys.argv:
        f = sys.argv[sys.argv.index("--list") + 1]
        for no, frag in scan_text(open(f, encoding="utf-8").read()):
            print(f":{no}  {frag}")
        sys.exit(0)
    bad, report, _ = scan_repo()
    for r in report:
        print(r)
    if bad:
        sys.exit(1)
    print(f"✅ html_interpolation_escape_check: {len(FILES)} ไฟล์ ไม่มี ${{...}} ที่ไม่หนีในบริบท HTML เกิน baseline")
    sys.exit(self_test())
