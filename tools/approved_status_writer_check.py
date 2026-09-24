#!/usr/bin/env python3
# -*- coding: utf-8 -*-
"""เอกสารที่ "ออกโดยไม่ผ่าน ApproveDocumentAsync" แต่ไม่เรียกผลข้างเคียงหลังออกเอกสาร — ratchet กับ baseline

═══ ที่มา (รอบ 193 · ผลตรวจ S-02 `erp-review/2026-09-24/audit-settings.md` + ฝ่ายค้าน C-3) ═══
ค่าตั้งที่มีผล "ตอนออกเอกสาร" (e-Tax อัตโนมัติ `EtaxEnabled`/`EtaxAutoSign`) เคยอยู่ใน
`ApproveDocumentAsync` เท่านั้น แต่ POS (ใบกำกับเต็มรูป) และ Integration (TIV/CN/DN) สร้างเอกสารเป็น
`Approved` ตรง ๆ ⇒ ใบจากสองทางนี้ไม่เคยถูกออก e-Tax. แก้ด้วย `IIssuedDocumentHooks.RunAsync` จุดเดียว.
รอบแรกของ checker นี้ **มองไม่เห็น** ใบเสร็จ settlement ที่เกิดเป็น `Paid` และทำหน้าที่ใบกำกับ ณ วันรับเงิน
(§78/1 · `DocumentService.CreateSettlementReceiptAsync`) เพราะจับแค่ `Approved` และข้าม DocumentService ทั้งไฟล์
(ฝ่ายค้าน C-3) ⇒ รอบนี้ขยายขอบเขตโดยแยก "เกิดมาออกแล้ว" ออกจาก "เปลี่ยนสถานะตามการชำระ"

═══ กติกา — สองรูปที่นับเป็น "การออกเอกสาร" ═══
  (ก) **สร้างเอกสารใหม่ที่เกิดมาในสถานะออกแล้ว**: ใน `new Document { … }` ช่อง `Status = <expr>` (ข้ามบรรทัดได้)
      ฟ้องเมื่อ expr อ้างสถานะที่ไม่ใช่ Draft/WaitingApproval/Rejected **หรือ** ไม่อ้างสถานะใดเลย
      (มาจากตัวแปร = "ไม่รู้" ⇒ นับว่าออก — ห้ามตกเป็นผ่าน)
  (ข) **ประทับอนุมัติบนเอกสารเดิม** `x.Status = DocumentStatus.Approved;` (ค่าตรงตัว)
      — ไม่นับ `x.Status = paid ? … : DocumentStatus.Approved` (คำนวณสถานะคืนเมื่อยกเลิก/ลดยอดชำระ = ไม่ใช่การออกใหม่)
  ผ่านเมื่อ: เมธอดที่ครอบเรียก `…IssuedHooks.RunAsync(` **หรือ** เมธอดนั้นเป็นตัวช่วยที่ไม่ save เอง และ **ทุก**
  call site ในไฟล์เดียวกันอยู่ในเมธอดที่เรียก hook (เช่น `CreateSettlementReceiptAsync` ← ผู้เรียก 2 ตัวหลัง commit)
  จุดที่ตั้งใจยกเว้นอยู่ใน `tools/approved_status_writer_baseline.txt` (`ไฟล์:เมธอด = จำนวน`) — ห้ามเพิ่มแถวเพื่อให้เขียว

ใช้: python3 tools/approved_status_writer_check.py [--all] [--self-test] [--write-baseline]

═══ negative test (F2 ข้อ 6) ═══ `--self-test`: ลืม hook · Paid ข้ามบรรทัด · สถานะจากตัวแปร · ตัวช่วยที่ผู้เรียกหนึ่งตัวลืม hook
= ต้องฟ้อง · เรียก hook แล้ว · ตัวช่วยที่ผู้เรียกครบ · `==`/คอมเมนต์ · คำนวณสถานะคืนแบบ ternary บนเอกสารเดิม · ร่าง = ต้องไม่ฟ้อง
"""
import os
import re
import sys
import tempfile

ROOT = os.path.dirname(os.path.dirname(os.path.abspath(__file__)))
SRC = os.path.join(ROOT, "Accounting")
BASELINE = os.path.join(ROOT, "tools", "approved_status_writer_baseline.txt")

NOT_ISSUED = {"Draft", "WaitingApproval", "Rejected"}
NEW_DOC_RE = re.compile(r"\bnew\s+(?:global::)?(?:Accounting\.)?(?:Models\.Entities\.)?Document\s*(?:\(\s*\))?\s*\{")
APPROVE_ASSIGN_RE = re.compile(
    r"\.\s*Status\s*=(?!=)\s*(?:Accounting\.)?(?:Models\.Enums\.)?DocumentStatus\s*\.\s*Approved\s*;")
STATUS_MEMBER_RE = re.compile(r"(?<![\w.])Status\s*=(?!=)")
HOOK_RE = re.compile(r"[iI]ssuedHooks\s*[!]?\s*\.\s*RunAsync\s*\(")
METHOD_RE = re.compile(
    r"^[ \t]*(?:\[[^\]]*\][ \t]*)*(?:(?:public|private|internal|protected|static|async|override|virtual|sealed|new|partial)\s+)+"
    r"[\w<>\[\]?,.\s()]+?\s+(\w+)\s*(?:<[^>()]*>)?\s*\([^;{]*?\)\s*(?:where[^{]*)?\{",
    re.M | re.S)
KEYWORDS = ("if", "for", "foreach", "while", "switch", "catch", "using", "lock", "return")


def strip(text: str) -> str:
    """ตัดคอมเมนต์ + เนื้อสตริงเป็นช่องว่าง (คงจำนวนตัวอักษร/บรรทัด) — ปีกกาในสตริงไม่ทำให้นับเมธอดผิด"""
    out = []
    i, n = 0, len(text)

    def blank(s):
        return re.sub(r"[^\n]", " ", s)

    while i < n:
        c = text[i]
        nx = text[i + 1] if i + 1 < n else ""
        if c == "/" and nx == "/":
            j = text.find("\n", i)
            j = n if j < 0 else j
            out.append(blank(text[i:j])); i = j; continue
        if c == "/" and nx == "*":
            j = text.find("*/", i + 2)
            j = n if j < 0 else j + 2
            out.append(blank(text[i:j])); i = j; continue
        m = re.match(r'(\$@|@\$|@)"', text[i:i + 3])
        if m:
            j = i + len(m.group(0))
            while j < n:
                if text[j] == '"':
                    if j + 1 < n and text[j + 1] == '"':
                        j += 2; continue
                    break
                j += 1
            out.append(blank(text[i:j + 1])); i = j + 1; continue
        if c == '"' or (c == "$" and nx == '"'):
            j = i + (2 if c == "$" else 1)
            while j < n and text[j] != '"' and text[j] != "\n":
                j += 2 if text[j] == "\\" else 1
            out.append(blank(text[i:j + 1])); i = j + 1; continue
        if c == "'":
            m2 = re.match(r"'(?:\\.|[^'\\\n]){1,8}'", text[i:])
            if m2:
                out.append(blank(m2.group(0))); i += len(m2.group(0)); continue
        out.append(c); i += 1
    return "".join(out)


def close_brace(body: str, start: int) -> int:
    """ตำแหน่งปีกกาปิดที่คู่กับ `{` ที่ start"""
    depth, j = 0, start
    while j < len(body):
        if body[j] == "{":
            depth += 1
        elif body[j] == "}":
            depth -= 1
            if depth == 0:
                return j
        j += 1
    return len(body) - 1


def methods(body: str):
    """[(ชื่อ, start, end)] ของเมธอดทุกตัว"""
    res = []
    for m in METHOD_RE.finditer(body):
        if m.group(1) in KEYWORDS:
            continue
        start = m.end() - 1
        res.append((m.group(1), start, close_brace(body, start)))
    return res


def outer_method(ms, pos):
    encl = sorted([mm for mm in ms if mm[1] <= pos <= mm[2]], key=lambda mm: mm[1])
    return encl[0] if encl else None


def status_expr(body: str, i: int, end: int) -> str:
    """อ่าน expr หลัง `Status =` ใน initializer จนถึง `,`/`}` ระดับเดียวกัน (ข้ามบรรทัดได้)"""
    depth, j = 0, i
    while j < end:
        ch = body[j]
        if ch in "([{":
            depth += 1
        elif ch in ")]}":
            if depth == 0:
                break
            depth -= 1
        elif ch == "," and depth == 0:
            break
        j += 1
    return body[i:j]


def issued_expr(expr: str) -> bool:
    """expr นี้ทำให้เอกสาร "เกิดมาออกแล้ว" ได้ไหม — ไม่อ้างสถานะใดเลย (ตัวแปร) = ได้ ("ไม่รู้" ห้ามเป็นผ่าน)"""
    names = re.findall(r"DocumentStatus\s*\.\s*(\w+)", expr)
    if not names:
        return True
    return any(nm not in NOT_ISSUED for nm in names)


def stamps(body: str):
    """ตำแหน่งการ "ออกเอกสาร" ทั้งสองรูป"""
    out = []
    for m in NEW_DOC_RE.finditer(body):
        start = m.end() - 1
        end = close_brace(body, start)
        # ช่องของ initializer ชั้นนอกสุดเท่านั้น (ไม่ใช่ Lines = { new DocumentLine { Status… } })
        depth, j = 0, start + 1
        while j < end:
            ch = body[j]
            if ch in "([{":
                depth += 1
            elif ch in ")]}":
                depth -= 1
            elif depth == 0:
                sm = STATUS_MEMBER_RE.match(body, j)
                if sm:
                    expr = status_expr(body, sm.end(), end)
                    if issued_expr(expr):
                        out.append(j)
                    j = sm.end() + len(expr)
                    continue
            j += 1
    out.extend(m.start() for m in APPROVE_ASSIGN_RE.finditer(body))
    return sorted(out)


def hooked_via_callers(body: str, ms, method_name: str) -> bool:
    """ตัวช่วยที่ไม่ save เอง — ผ่านเมื่อ call site ทุกจุดในไฟล์อยู่ในเมธอดที่เรียก hook (อย่างน้อย 1 จุด)"""
    calls = [m.start() for m in re.finditer(r"(?<![\w.])" + re.escape(method_name) + r"\s*\(", body)]
    sites = []
    for c in calls:
        om = outer_method(ms, c)
        # signature ของตัวเองอยู่ก่อน `{` ของเมธอด ⇒ ไม่มีเมธอดครอบ = ไม่ใช่ call site
        if om is None:
            continue
        sites.append(om)
    return bool(sites) and all(om[0] != method_name and HOOK_RE.search(body, om[1], om[2] + 1) for om in sites)


def scan():
    hits = {}
    for dirpath, dirnames, filenames in os.walk(SRC):
        dirnames[:] = [d for d in dirnames if d not in ("bin", "obj", "node_modules", "wwwroot")]
        for fn in filenames:
            if not fn.endswith(".cs"):
                continue
            full = os.path.join(dirpath, fn)
            rel = os.path.relpath(full, SRC).replace(os.sep, "/")
            try:
                raw = open(full, encoding="utf-8", errors="replace").read()
            except OSError:
                continue
            body = strip(raw)
            ss = stamps(body)
            if not ss:
                continue
            ms = methods(body)
            for s in ss:
                om = outer_method(ms, s) or ("<นอกเมธอด>", 0, len(body) - 1)
                if HOOK_RE.search(body, om[1], om[2] + 1):
                    continue
                if om[0] != "<นอกเมธอด>" and hooked_via_callers(body, ms, om[0]):
                    continue
                hits.setdefault(f"{rel}:{om[0]}", []).append(body.count("\n", 0, s) + 1)
    return hits


def load_baseline():
    out = {}
    if not os.path.exists(BASELINE):
        return out
    for line in open(BASELINE, encoding="utf-8"):
        line = line.strip()
        if not line or line.startswith("#"):
            continue
        key, _, rest = line.partition(" = ")
        cnt = rest.split("#", 1)[0].strip()
        try:
            out[key.strip()] = int(cnt)
        except ValueError:
            out[key.strip()] = 1
    return out


def write_baseline(hits):
    with open(BASELINE, "w", encoding="utf-8") as f:
        f.write("# จุดออกเอกสารนอก ApproveDocumentAsync ที่ตั้งใจ **ไม่**เรียก IssuedDocumentHooks\n")
        f.write("# ห้ามเพิ่มแถวเพื่อให้ checker เขียว — ตัดออกได้อย่างเดียว · ทุกแถวต้องมีเหตุผลหลัง #\n")
        for k, lines in sorted(hits.items()):
            f.write(f"{k} = {len(lines)}  # TODO เหตุผล\n")


SELF_TEST_FILES = {
    "BadService.cs": (
        "class Bad {\n"
        "  public async Task Make(Guid c) {\n"
        "    var doc = new Document { Status = DocumentStatus.Approved, Notes = \"{ }\" };\n"
        "    await _db.SaveChangesAsync();\n"
        "  }\n"
        "  public async Task Other(Guid c, Document doc) { await _issuedHooks.RunAsync(c, doc); }\n"
        "}\n"),
    "PaidService.cs": (
        "class Paid {\n"
        "  public async Task Receipt(bool ok) {\n"
        "    var r = new Models.Entities.Document {\n"
        "      DocumentType = DocumentType.Receipt,\n"
        "      Status = ok\n"
        "          ? DocumentStatus.Paid\n"
        "          : DocumentStatus.Draft,\n"
        "    };\n"
        "  }\n"
        "  public void FromVar(DocumentStatus st) { var d = new Document { Status = st }; }\n"
        "  public void Stamp(Document d) { d.Status = DocumentStatus.Approved; }\n"
        "}\n"),
    "HelperService.cs": (
        "class Helper {\n"
        "  private Document Build(bool ok) {\n"
        "    return new Document { Status = ok ? DocumentStatus.Paid : DocumentStatus.Draft };\n"
        "  }\n"
        "  public async Task A() { var d = Build(true); await _db.SaveChangesAsync(); await _issuedHooks.RunAsync(c, d); }\n"
        "  public async Task B() { var d = Build(true); await _db.SaveChangesAsync(); if (d != null) await _issuedHooks.RunAsync(c, d); }\n"
        "}\n"),
    "HelperMissService.cs": (
        "class HelperMiss {\n"
        "  private Document Build2(bool ok) {\n"
        "    return new Document { Status = ok ? DocumentStatus.Paid : DocumentStatus.Draft };\n"
        "  }\n"
        "  public async Task A() { var d = Build2(true); await _issuedHooks.RunAsync(c, d); }\n"
        "  public async Task B() { var d = Build2(true); await _db.SaveChangesAsync(); }\n"
        "}\n"),
    "GoodService.cs": (
        "class Good {\n"
        "  public async Task Make(Guid c, bool auto) {\n"
        "    var doc = new Document { Status = auto ? DocumentStatus.Approved : DocumentStatus.Draft };\n"
        "    if (auto) { await _issuedHooks.RunAsync(c, doc); }\n"
        "  }\n"
        "  public void Draft() { var d = new Document { Status = DocumentStatus.Draft, Lines = { new DocumentLine { } } }; }\n"
        "  public void NoStatus() { var d = new Document { DocumentType = DocumentType.Quotation }; }\n"
        "}\n"),
    "ReaderService.cs": (
        "class Reader {\n"
        "  // doc.Status = DocumentStatus.Approved;  <- คอมเมนต์ ไม่ใช่การประทับ\n"
        "  public bool M(Document d) => d.Status == DocumentStatus.Approved;\n"
        "  public void Revert(Document inv) {\n"
        "    inv.Status = inv.PaidAmount <= 0.01m ? DocumentStatus.Approved\n"
        "        : DocumentStatus.PartiallyPaid;\n"
        "  }\n"
        "}\n"),
}


def self_test():
    global SRC
    ok = True
    with tempfile.TemporaryDirectory() as tmp:
        d = os.path.join(tmp, "Services", "Implementations")
        os.makedirs(d)
        for name, text in SELF_TEST_FILES.items():
            open(os.path.join(d, name), "w", encoding="utf-8").write(text)
        saved, SRC = SRC, tmp
        try:
            hits = scan()
        finally:
            SRC = saved
    keys = set(hits.keys())
    must = ["BadService.cs:Make", "PaidService.cs:Receipt", "PaidService.cs:FromVar",
            "PaidService.cs:Stamp", "HelperMissService.cs:Build2"]
    for k in must:
        if f"Services/Implementations/{k}" not in keys:
            print(f"❌ self-test: ไม่จับ {k}", sorted(keys)); ok = False
    for bad in ("GoodService", "ReaderService", "HelperService.cs"):
        if any(bad in k for k in keys):
            print(f"❌ self-test: ฟ้องผิด {bad}", sorted(keys)); ok = False
    if ok:
        print("✅ self-test ผ่าน — จับ: ลืม hook · Paid ข้ามบรรทัด · สถานะจากตัวแปร · ประทับตรง · ตัวช่วยที่ผู้เรียกลืม · "
              "ไม่ฟ้อง: เรียก hook แล้ว · ตัวช่วยที่ผู้เรียกครบ · ร่าง · การอ่าน/คอมเมนต์ · คำนวณสถานะคืน")
    return 0 if ok else 1


def main():
    args = sys.argv[1:]
    if "--self-test" in args:
        return self_test()
    hits = scan()
    if "--write-baseline" in args:
        write_baseline(hits)
        print(f"เขียน baseline แล้ว: {sum(len(v) for v in hits.values())} จุด")
        return 0
    base = load_baseline()
    if "--all" in args:
        for k, lines in sorted(hits.items()):
            mark = "baseline" if len(lines) <= base.get(k, 0) else "ใหม่"
            print(f"  [{mark}] {k} (บรรทัด {', '.join(map(str, lines))})")
    new = {k: v for k, v in hits.items() if len(v) > base.get(k, 0)}
    gone = sorted(k for k in base if base[k] > len(hits.get(k, [])))
    print(f"จุดออกเอกสารนอก ApproveDocumentAsync ที่ไม่เรียก IssuedDocumentHooks: "
          f"{sum(len(v) for v in hits.values())} (baseline {sum(base.values())})")
    if gone:
        print(f"ℹ️  baseline ลดลง {len(gone)} รายการ — ตัดแถวออกจาก baseline ในคอมมิตเดียวกัน:")
        for g in gone:
            print(f"   - {g}")
    if new:
        print(f"❌ จุดออกเอกสาร**ใหม่** ที่ไม่เรียก IssuedDocumentHooks {len(new)} รายการ:")
        for k, lines in sorted(new.items()):
            print(f"   {k} (บรรทัด {', '.join(map(str, lines))}) — baseline {base.get(k, 0)} → ปัจจุบัน {len(lines)}")
        print("   → เรียก `_issuedHooks.RunAsync(companyId, doc)` หลัง commit ในเมธอดเดียวกัน (หรือในผู้เรียกทุกตัว)")
        print("   → หรืออนุมัติผ่าน ApproveDocumentAsync · ห้ามเติม baseline เพื่อให้ผ่าน")
        return 1
    print("✅ ทุกจุดออกเอกสารนอก ApproveDocumentAsync เรียกผลข้างเคียงหลังออกเอกสาร (หรืออยู่ใน baseline พร้อมเหตุผล)")
    return 0


if __name__ == "__main__":
    sys.exit(main())
