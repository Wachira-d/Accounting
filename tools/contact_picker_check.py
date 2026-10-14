#!/usr/bin/env python3
"""ด่าน static: ทุกช่องที่ผู้ใช้เลือกผู้ติดต่อ ต้องใช้ตัวเลือกกลาง `Layout.contactPicker` ตัวเดียว

ที่มา (คำสั่งเจ้าของ 2026-10-14): "ข้อมูลผู้ติดต่อ ที่อยู่ในส่วนอื่นๆ เช่น หน้าหัก ณ ที่จ่าย ทำให้มีปุ่มเพิ่มได้แบบหน้าออกเอกสารเลย
รวมถึงหน้าอื่นๆ … ให้เป็นแนวทางเดียวกันทั้งระบบ" — ตรวจแล้วพบ 4 กลไกปนกัน: autocomplete กลาง (ไม่มีปุ่มสร้าง ยกเว้นหน้าเอกสาร
ที่เขียนปุ่ม/โค้ดสร้างเองอีกชุด) · `<select>` ที่โหลด `pageSize=500` (เซิร์ฟเวอร์ตัดที่ 200 ⇒ ผู้ติดต่อลำดับหลังหาไม่เจอ ·
ไม่กรองที่ปิดใช้งาน · `catch {}` เงียบ) · ช่องพิมพ์ GUID เอง · ช่องพิมพ์ชื่ออิสระที่ API รับ ContactId ได้แต่ไม่เคยส่ง
และบั๊ก echo กลับ: wht.html เรียก `setText?.()` ที่ไม่มีอยู่จริง + id ช่องผิด ⇒ เปิดแก้แล้วผู้ถูกหักว่าง

กติกา (0 จุดบนเรพหลังแก้ · R3 เป็น ratchet ต่อไฟล์):
  R1 ห้ามชื่อเก่า `contactAutocomplete(` ทุกไฟล์ใน wwwroot (ชื่อเดียว = contactPicker)
  R2 ห้าม `<select id="…">` ที่ id สื่อถึงผู้ติดต่อ (contact/customer/supplier/vendor/payee/consign/partner) ใน pages/*.html
     และ js/*.js — ยกเว้นรายการ ALLOW_SELECT (พร้อมเหตุผล) และ id ที่ลงท้าย Type (เลือก "ประเภท" ไม่ใช่ผู้ติดต่อ)
  R3 การอ่านรายชื่อผู้ติดต่อตรง (`getContacts(` / `document/contacts?`) ในหน้า เกินจำนวนใน ALLOW_LIST_READ ⇒ ฟ้อง
     (ของเดิมที่เหลือเป็นแคช/ตารางชื่อเพื่อแสดงผล หรือหน้าพิเศษ — ช่องเลือกใหม่ต้องใช้ contactPicker)
  R4 ทุก `Layout.contactPicker('<id>', '<id>'…)` ที่ id เป็นสตริงตรง ต้องมี element id นั้นในไฟล์เดียวกัน
     (หรือ .html คู่ของ *-logic.js) — กันคลาส "id ผิดแล้วเงียบ" (fContactInput)
  R5 ตัวเลือกกลางใน js/layout.js ต้องต่อสายครบ: ปุ่มสร้าง (`cp-create-btn` + `_canCreateContact(`) · หน้าต่างสร้างด่วนเรียก
     `_contactQuickPayload(` → `api.createContact(` → `_contactCreateOutcome(` · แถวผลค้นหา escape ชื่อ (`Layout.esc(c.name)`) ·
     ตัวรับ "เปิดฟอร์มเต็ม" (`_ensureContactCreatedListener(`)

ใช้: python3 tools/contact_picker_check.py             # ตรวจเรพ
     python3 tools/contact_picker_check.py --self-test # negative test: ฉีดบั๊กแต่ละชนิดลงสำเนาไฟล์จริงแล้วต้องฟ้อง
"""
import re
import sys
from pathlib import Path

ROOT = Path(__file__).resolve().parent.parent
WWW = ROOT / "Accounting" / "wwwroot"
LAYOUT = WWW / "js" / "layout.js"

# R2 — <select> ผู้ติดต่อที่ตั้งใจคงไว้ (ไฟล์, id) : เหตุผล
ALLOW_SELECT = {
    ("arap-analysis.html", "contactSelect"): "รายชื่อเฉพาะผู้ที่มียอดค้างจากผลวิเคราะห์ AR/AP (ไม่ใช่ทะเบียน) — ตัวกรองรายงาน",
    ("intercompany.html", "fPartner"): "บริษัทในกลุ่ม (Company) ไม่ใช่ผู้ติดต่อ",
    ("mobile-receipt.html", "customer"): "หน้ามือถือแบบ standalone ไม่โหลด layout.js — ตามงานแยก",
}

# R3 — จำนวนการอ่านรายชื่อผู้ติดต่อตรงที่ยอมให้ต่อไฟล์ : เหตุผล (ratchet — ลดได้ ห้ามเพิ่ม)
ALLOW_LIST_READ = {
    "contacts.html": (1, "หน้าทะเบียนผู้ติดต่อเอง"),
    "documents.html": (2, "แคชสำรองหาข้อมูลจาก id + หาสาขาอื่นของเลขภาษีเดียวกัน (ไม่ใช่ช่องเลือก)"),
    "cheques.html": (1, "ตารางชื่อผู้รับในรายการเช็คค้าง (แสดงผล)"),
    "consignment.html": (1, "ตารางชื่อคู่ค้าในรายการฝากขาย (แสดงผล)"),
    "document-scan.html": (1, "จับคู่ผู้ขายจาก OCR (กฎเหล็ก #3 — มีทางสร้างจากข้อมูลสแกนของตัวเอง)"),
    "pos.html": (1, "ค้นสมาชิก POS (ชื่อลูกค้าอิสระ + แต้มสะสม)"),
    "pos-reservations.html": (1, "ค้นลูกค้าจองโต๊ะ (ชื่อ/เบอร์อิสระ)"),
    "quick-sale.html": (1, "หน้าขายด่วน standalone ไม่โหลด layout.js"),
    "mobile-receipt.html": (1, "หน้ามือถือ standalone ไม่โหลด layout.js"),
}

CONTACTISH = re.compile(r"contact|customer|supplier|vendor|payee|consign|partner", re.I)
SELECT_ID = re.compile(r"<select\b[^>]*\bid\s*=\s*[\"']([^\"'$]+)[\"']", re.I)
LIST_READ = re.compile(r"getContacts\s*\(|document/contacts\?")
PICKER_CALL = re.compile(r"Layout\.contactPicker\(\s*'([^']+)'\s*,\s*'([^']+)'")
OLD_NAME = re.compile(r"\bcontactAutocomplete\s*\(")


def _strip_comments(text, suffix):
    if suffix == ".html":
        text = re.sub(r"<!--.*?-->", "", text, flags=re.S)
    # คอมเมนต์ JS แบบอนุรักษ์: บรรทัดที่ขึ้นต้นด้วย // หรือ * (JSDoc) และ /* … */ ที่จบในบรรทัดเดียว
    # (ห้ามใช้ /\*.*?\*/ ข้ามบรรทัด — รุ่นแรกกิน `/*` ในสตริง/regex แล้วกลืนโค้ดจริงไปครึ่งไฟล์ ⇒ นับขาด)
    text = re.sub(r"(?m)^\s*(?://|\*|/\*).*$", "", text)
    text = re.sub(r"/\*[^\n]*?\*/", "", text)
    text = re.sub(r"(?<=[;,{}()\s])//\s[^\n`'\"]*$", "", text, flags=re.M)
    return text


def _files():
    out = []
    for p in sorted((WWW / "pages").glob("*.html")) + sorted((WWW / "pages").glob("*.js")) + sorted((WWW / "js").glob("*.js")):
        out.append(p)
    return out


def _func_body(src, name):
    """เนื้อเมธอดระดับบนของ Layout (เยื้อง 2 ช่อง · ปิดด้วย '\\n  },')"""
    m = re.search(r"\n  (?:async )?" + re.escape(name) + r"\(", src)
    if not m:
        return None
    end = src.find("\n  },", m.end())
    return src[m.end(): end if end > 0 else len(src)]


def scan(overrides=None):
    overrides = overrides or {}
    read = lambda p: overrides.get(p, p.read_text(encoding="utf-8", errors="ignore"))
    findings = []
    texts = {p: read(p) for p in _files()}
    for p, extra in overrides.items():
        if p not in texts:
            texts[p] = extra
    for p, raw in texts.items():
        name = p.name
        code = _strip_comments(raw, p.suffix)
        for m in OLD_NAME.finditer(code):
            findings.append(("R1", name, "เรียก contactAutocomplete( — ชื่อเก่าถูกยุบเป็น Layout.contactPicker(inputId, hiddenId, opts)"))
        if p.parent.name in ("pages", "js") and p != LAYOUT:
            for m in SELECT_ID.finditer(code):
                sid = m.group(1)
                if not CONTACTISH.search(sid) or sid.endswith("Type"):
                    continue
                if (name, sid) in ALLOW_SELECT:
                    continue
                findings.append(("R2", name, f'<select id="{sid}"> เลือกผู้ติดต่อเอง — ใช้ <input type="hidden"> + <input type="text"> + Layout.contactPicker'))
        if p.parent.name == "pages":
            n = len(LIST_READ.findall(code))
            allowed = ALLOW_LIST_READ.get(name, (0, ""))[0]
            if n > allowed:
                findings.append(("R3", name, f"อ่านรายชื่อผู้ติดต่อตรง {n} จุด (ยอม {allowed}) — ช่องเลือกผู้ติดต่อใช้ Layout.contactPicker · ถ้าเป็นแคชแสดงผลจริงให้เพิ่มใน ALLOW_LIST_READ พร้อมเหตุผล"))
        if p.parent.name == "pages":
            pair = raw
            if p.suffix == ".js" and p.name.endswith("-logic.js"):
                html = p.with_name(p.name[: -len("-logic.js")] + ".html")
                pair = raw + (texts.get(html) or (html.read_text(encoding="utf-8", errors="ignore") if html.exists() else ""))
            for m in PICKER_CALL.finditer(code):
                for el_id in (m.group(1), m.group(2)):
                    if not re.search(r"\bid\s*=\s*[\"']" + re.escape(el_id) + r"[\"']", pair):
                        findings.append(("R4", name, f"Layout.contactPicker อ้าง id '{el_id}' ที่ไม่มีในหน้า — ช่องจะไม่ทำงาน (คลาส fContactInput)"))
    lay = texts.get(LAYOUT) or read(LAYOUT)
    picker = _func_body(lay, "contactPicker") or ""
    save = _func_body(lay, "_saveContactQuickCreate") or ""
    need = [
        (picker, "cp-create-btn", "contactPicker ไม่ใส่ปุ่ม '+ สร้างใหม่' (cp-create-btn) ให้หน้า"),
        (picker, "_canCreateContact(", "contactPicker ไม่ตรวจสิทธิ์สร้างผู้ติดต่อก่อนแสดงปุ่ม"),
        (picker, "_openContactQuickCreate(", "contactPicker ไม่เปิดหน้าต่างสร้างด่วน"),
        (picker, "Layout.esc(c.name)", "แถวผลค้นหาไม่ escape ชื่อผู้ติดต่อ (XSS)"),
        (picker, "_ensureContactCreatedListener(", "contactPicker ไม่ติดตั้งตัวรับ 'เปิดฟอร์มเต็ม'"),
        (save, "_contactQuickPayload(", "บันทึกด่วนไม่ผ่านตัวประกอบ payload กลาง"),
        (save, "createContact(", "บันทึกด่วนไม่เรียก POST document/contacts (ด่านเซิร์ฟเวอร์ตัวเดียว)"),
        (save, "_contactCreateOutcome(", "บันทึกด่วนไม่ตรวจผลจากเซิร์ฟเวอร์ (รายเดิมที่ปิดใช้งานจะถูกเลือก)"),
    ]
    for body, token, msg in need:
        if token not in body:
            findings.append(("R5", "layout.js", msg))
    if "createContact(" in save and "_contactCreateOutcome(" in save and save.index("_contactCreateOutcome(") < save.index("createContact("):
        findings.append(("R5", "layout.js", "ตรวจผลก่อนเรียกสร้าง — ลำดับผิด"))
    return findings


def self_test():
    base = scan()
    if base:
        print(f"self-test ล้ม: เรพจริงต้องสะอาดก่อน (พบ {len(base)} จุด)")
        for f in base[:10]:
            print("  ", f)
        return 1
    wht = WWW / "pages" / "wht.html"
    doc = WWW / "pages" / "documents.html"
    fin = WWW / "pages" / "financial-mgmt-logic.js"
    lay = LAYOUT
    wt, dt, ft, lt = (p.read_text(encoding="utf-8") for p in (wht, doc, fin, lay))
    call = "Layout.contactPicker('fContact', 'fContactId'"
    assert call in wt, "self-test: wht.html ไม่มีบรรทัดเรียก picker แล้ว (แก้ self-test)"
    assert "_contactCreateOutcome(c, this._cpqKnownIds)" in lt, "self-test: ข้อความใน layout.js เปลี่ยน (แก้ self-test)"
    assert "Layout.esc(c.name)" in lt
    fake = WWW / "pages" / "zz-selftest-new.html"
    cases = [
        ("เรียกชื่อเก่า", "R1", {wht: wt.replace(call, "Layout.contactAutocomplete('fContact', 'fContactId'")}),
        ("select ผู้ติดต่อใหม่", "R2", {fake: '<select class="form-select" id="fSupplierPick"></select>'}),
        ("select ใน JS template", "R2", {fake: "<script>el.innerHTML = `<select id=\"payeeSel\">${o}</select>`;</script>"}),
        ("อ่านรายชื่อเพิ่ม (หน้าใหม่)", "R3", {fake: "<script>api.getContacts('?pageSize=500').then(r => fill(r));</script>"}),
        ("อ่านรายชื่อเกินโควตา", "R3", {doc: dt + "\n<script>API.get(`/api/companies/${cid}/document/contacts?pageSize=500`);</script>"}),
        ("id ผิด", "R4", {wht: wt.replace(call, "Layout.contactPicker('fContactInput', 'fContactId'")}),
        ("id ผิดใน -logic.js", "R4", {fin: ft.replace("Layout.contactPicker('depContact'", "Layout.contactPicker('depContactX'")}),
        ("ถอดปุ่มสร้าง", "R5", {lay: lt.replace("cp-create-btn", "cp-x-btn")}),
        ("ไม่ตรวจผลหลังสร้าง", "R5", {lay: lt.replace("const out = this._contactCreateOutcome(c, this._cpqKnownIds);", "const out = { kind: 'created' };")}),
        ("ไม่ escape ชื่อ", "R5", {lay: lt.replace("${Layout.esc(c.name)}</div>", "${c.name}</div>")}),
    ]
    ok_cases = [
        ("คอมเมนต์ที่เอ่ยชื่อเก่า", {wht: wt.replace(call, "/* เดิม contactAutocomplete( */ " + call)}),
        ("select ประเภทผู้ติดต่อ", {fake: '<select id="fContactType"></select>'}),
        ("หน้าใหม่ที่ใช้ picker ถูก", {fake: '<input type="hidden" id="pId"><input id="pName"><script>Layout.contactPicker(\'pName\', \'pId\', { side: \'customer\' });</script>'}),
    ]
    bad = 0
    for name, code, ov in cases:
        got = [f for f in scan(ov) if f[0] == code]
        if not got:
            print(f"self-test ล้ม: '{name}' ต้องถูกฟ้องด้วย {code} แต่ไม่ถูกฟ้อง")
            bad = 1
    for name, ov in ok_cases:
        got = scan(ov)
        if got:
            print(f"self-test ล้ม: '{name}' ต้องไม่ถูกฟ้อง แต่ฟ้อง: {got[0]}")
            bad = 1
    if not bad:
        print(f"self-test: ผ่าน ({len(cases)} บั๊กที่ฉีดถูกจับครบ · {len(ok_cases)} เคสถูกต้องไม่ถูกฟ้อง)")
    return bad


def main():
    if "--self-test" in sys.argv:
        return self_test()
    f = scan()
    if f:
        print(f"❌ contact_picker_check: {len(f)} จุด")
        for code, name, msg in f:
            print(f"  [{code}] {name}: {msg}")
        return 1
    print("✅ contact_picker_check: ทุกช่องเลือกผู้ติดต่อใช้ Layout.contactPicker")
    return 0


if __name__ == "__main__":
    sys.exit(main())
