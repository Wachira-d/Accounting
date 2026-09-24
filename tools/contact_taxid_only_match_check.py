#!/usr/bin/env python3
# -*- coding: utf-8 -*-
"""ห้ามหา/จับคู่ "ผู้ติดต่อ" ด้วยเลขผู้เสียภาษีอย่างเดียว — ต้องมีรหัสสาขาในนิพจน์เดียวกัน

═══ ที่มา (รอบ 193 · คำตัดสินเจ้าของข้อ 20 · ทีม O2 → ฝ่ายค้าน → ทีม C3) ═══
ตามประกาศอธิบดีฯ ฉบับที่ 199 หนึ่งเลขผู้เสียภาษีมีได้หลายสถานประกอบการ และระบบเก็บผู้ติดต่อ
1 แถว = 1 (เลขภาษี, สาขา). ทีม O2 ทำตัวจับคู่กลาง `Helpers/ContactTaxBranchKey` แล้วต่อเข้า 9 ทางเข้า
แต่ฝ่ายค้านพบอีกหลายทางเข้า (CMS · ข้ามบริษัท · ใบแจ้งหนี้ค่าบริการแพลตฟอร์ม · นำเข้า) ที่ยังเขียน
`c.TaxId == x` เอง ⇒ หยิบแถวไหนก็ได้ของเลขนั้น ⇒ ใบกำกับของผู้ซื้อสาขา 8 ออกในนามสำนักงานใหญ่
(§86/4 สาขาผิด) — defect class "แก้ตัวเดียว เหลือที่เหลือ" (CLAUDE.md F2 ข้อ 1)

═══ กติกา (ขอบเขตแคบโดยตั้งใจ — F4 ข้อ 3) ═══
ฟ้อง **ประโยค** (ตัดด้วย `;`) ที่ครบทั้งสองข้อ:
  (ก) แหล่งเป็นผู้ติดต่อ — `.Contacts` · `Set<Contact>()` · `Entries<Contact>()` · ตัวแปร IQueryable ที่
      ประกาศจาก `.Contacts` ในไฟล์เดียวกัน (ไม่มี `await` = ยังเป็น query) · หรือ navigation `.Contact.TaxId`
  (ข) เทียบเลขภาษี — `.TaxId ==` · `== x.TaxId` · `.Contains(c.TaxId)` · `.TaxId.Equals(` · `Normalize(c.TaxId) ==`
      (ไม่นับ `== null` / `== ""` ซึ่งเป็นการตรวจว่าว่าง · ไม่นับ `VendorTaxId`/`BuyerTaxId` ที่ไม่ใช่ผู้ติดต่อ)
และประโยคนั้น **ไม่มี** `BranchCode` หรือ `ContactTaxBranchKey`.
ไม่ตรวจ `Helpers/ContactTaxBranchKey.cs` (เจ้าของกติกา) · ข้าม `bin` `obj` `.claude` `node_modules`

จุดที่**ตั้งใจ**ใช้เลขภาษีอย่างเดียว (รวมทุกสาขาของนิติบุคคลเดียว เช่น ประวัติ WHT สะสม · รายการผู้สมัคร
ให้ผู้ใช้เลือก) ต้องมีคอมเมนต์เหตุผลในโค้ด แล้วอยู่ใน `tools/contact_taxid_only_match_baseline.txt`
ratchet เดินทางเดียว (เหมือน `terminal_status_writer_check`): ล้มเฉพาะเมื่อจำนวนต่อไฟล์**เพิ่ม** ·
ห้ามเพิ่มแถว/ตัวเลขใน baseline เพื่อให้เขียว — ย้ายไปใช้ `ContactTaxBranchKey` แทน

ใช้: python3 tools/contact_taxid_only_match_check.py [--all] [--self-test] [--write-baseline]
negative test: --self-test (ใส่บั๊กกลับต้องจับได้ · รูปที่ถูกต้องไม่ถูกฟ้อง · โฟลเดอร์ที่ข้ามไม่ถูกสแกน)
"""
import os
import re
import sys
import tempfile

ROOT = os.path.dirname(os.path.dirname(os.path.abspath(__file__)))
SRC = os.path.join(ROOT, "Accounting")
BASELINE = os.path.join(ROOT, "tools", "contact_taxid_only_match_baseline.txt")
SKIP_DIRS = {"bin", "obj", ".claude", "node_modules", ".git", "wwwroot"}
OWNERS = {"Helpers/ContactTaxBranchKey.cs"}

CONTACT_SRC = re.compile(
    r"\.Contacts\b|\bSet\s*<\s*(?:Accounting\.)?(?:Models\.Entities\.)?Contact\s*>\s*\(|"
    r"\bEntries\s*<\s*(?:Accounting\.)?(?:Models\.Entities\.)?Contact\s*>\s*\(")
NAV_CMP = re.compile(r"\.(?:Payee)?Contact\s*\.\s*TaxId\s*(?:==|\.Equals\()")
TAX_CMP = re.compile(
    r"\.TaxId\s*==\s*(?!\s*null\b)(?!\s*\"\")"              # c.TaxId == x
    r"|(?<![=!<>])==\s*[\w.()?!\[\]]*\.TaxId\b"              # x == c.TaxId
    r"|\.Contains\(\s*\w+\s*\.\s*TaxId\s*!?\s*\)"            # ids.Contains(c.TaxId)
    r"|\.TaxId\s*\.\s*Equals\("                              # c.TaxId.Equals(x)
    r"|\w\(\s*\w+\s*\.\s*TaxId\s*\)\s*==(?!\s*null\b)"           # Normalize(c.TaxId) == x
)
EXEMPT = re.compile(r"\bBranchCode\b|\bContactTaxBranchKey\b")
QUERY_VAR = re.compile(r"\b(\w+)\s*=\s*(?:\(\s*IQueryable<[^>]*>\s*\))?[^;=]*?(?:\.Contacts\b|Set\s*<\s*(?:Models\.Entities\.)?Contact\s*>\s*\()")

_BLOCK_COMMENT = re.compile(r"/\*.*?\*/", re.S)


def strip_comments(text):
    """ตัดคอมเมนต์ (คงจำนวนบรรทัด) — คอมเมนต์ที่ *พูดถึง* `c.TaxId == x` ไม่ใช่ query"""
    text = _BLOCK_COMMENT.sub(lambda m: re.sub(r"[^\n]", " ", m.group(0)), text)
    out = []
    for line in text.split("\n"):
        idx = line.find("//")
        while idx >= 0 and line[:idx].count('"') % 2 == 1:   # // อยู่ในสตริง (เช่น "https://")
            idx = line.find("//", idx + 2)
        out.append(line[:idx] if idx >= 0 else line)
    return "\n".join(out)


def scan_text(body):
    """คืนเลขบรรทัดของประโยคที่ฟ้อง"""
    tainted = set()
    for stmt in body.split(";"):
        if "await" in stmt:
            continue
        for m in QUERY_VAR.finditer(stmt):
            tainted.add(m.group(1))
    hits = []
    pos = 0
    for stmt in body.split(";"):
        start = pos
        pos += len(stmt) + 1
        if EXEMPT.search(stmt):
            continue
        is_contact = bool(CONTACT_SRC.search(stmt)) or any(
            re.search(r"\b" + re.escape(v) + r"\s*\.\s*(?:Where|Any|All|First|Single|Count|Select|OrderBy)", stmt)
            for v in tainted)
        m = None
        if is_contact:
            m = TAX_CMP.search(stmt)
        if m is None:
            m = NAV_CMP.search(stmt)
        if m is None:
            continue
        hits.append(body.count("\n", 0, start + m.start()) + 1)
    return hits


def scan(src=None):
    src = src or SRC
    out = []
    for dirpath, dirnames, filenames in os.walk(src):
        dirnames[:] = [d for d in dirnames if d not in SKIP_DIRS]
        for fn in filenames:
            if not fn.endswith(".cs"):
                continue
            full = os.path.join(dirpath, fn)
            rel = os.path.relpath(full, src).replace(os.sep, "/")
            if rel in OWNERS:
                continue
            try:
                raw = open(full, encoding="utf-8", errors="replace").read()
            except OSError:
                continue
            lines = scan_text(strip_comments(raw))
            if lines:
                out.append((rel, lines))
    out.sort()
    return out


def load_baseline():
    base = {}
    if not os.path.exists(BASELINE):
        return base
    for line in open(BASELINE, encoding="utf-8"):
        line = line.strip()
        if not line or line.startswith("#"):
            continue
        key, _, cnt = line.rpartition(" = ")
        try:
            base[key] = int(cnt)
        except ValueError:
            base[line] = 1
    return base


def write_baseline(hits):
    with open(BASELINE, "w", encoding="utf-8") as f:
        f.write("# จุดที่ query ผู้ติดต่อด้วยเลขผู้เสียภาษีอย่างเดียว (ไม่มีสาขา) ที่ **ตั้งใจ** — ทุกจุดต้องมีคอมเมนต์เหตุผลในโค้ด\n")
        f.write("# (รวมทุกสาขาของนิติบุคคลเดียว / รายการผู้สมัครให้คนเลือก / ไฟล์ของทีมอื่นที่ยังไม่ได้ย้าย — ดู erp-review/2026-09-24/r193-C3.md)\n")
        f.write("# ห้ามเพิ่มแถว/ตัวเลขเพื่อให้ checker เขียว — ratchet เดินทางเดียว: ย้ายไป Helpers/ContactTaxBranchKey แล้วลดตัวเลขลง\n")
        for rel, lines in hits:
            f.write(f"{rel} = {len(lines)}\n")


def self_test():
    ok = True
    cases = {
        # (relpath, content, ต้องถูกฟ้องไหม)
        "Services/Bad.cs": ("class A { async Task M() { var c = await _db.Contacts\n"
                            "  .FirstOrDefaultAsync(c => c.CompanyId == cid && c.TaxId == customer.TaxId); } }\n", True),
        "Services/BadReversed.cs": ("class A { void M() { var q = _db.Set<Contact>().Where(c => taxId == c.TaxId); } }\n", True),
        "Services/BadContains.cs": ("class A { void M() { var r = _db.Contacts.Where(c => ids.Contains(c.TaxId!)).ToList(); } }\n", True),
        "Services/BadTainted.cs": ("class A { async Task M() { var payeeQuery = _db.Contacts.AsNoTracking();\n"
                                   "  payeeQuery = payeeQuery.Where(c => c.TaxId == t);\n"
                                   "  var ids = await payeeQuery.Select(c => c.Id).ToListAsync(); } }\n", True),
        "Services/BadNormalize.cs": ("class A { async Task M() { var id = (await _db.Contacts.Select(c => new { c.Id, c.TaxId }).ToListAsync())\n"
                                     "  .Where(c => DocumentService.NormalizeTaxDigits(c.TaxId) == d).Select(c => c.Id).FirstOrDefault(); } }\n", True),
        "Services/BadNav.cs": ("class A { void M() { var r = _db.Documents.Where(d => d.Contact.TaxId == t); } }\n", True),
        "Services/OkBranch.cs": ("class A { void M() { var r = _db.Contacts.Where(c => c.TaxId == t && c.BranchCode == b); } }\n", False),
        "Services/OkKey.cs": ("class A { async Task M() { var k = await ContactTaxBranchKey.FindAsync(_db.Contacts, cid, t, b); } }\n", False),
        "Services/OkNullCheck.cs": ("class A { void M() { var r = _db.Contacts.Where(c => c.TaxId == null || c.TaxId == \"\"); } }\n", False),
        "Services/OkCompany.cs": ("class A { void M() { var r = _db.Companies.Where(c => c.TaxId == t); } }\n", False),
        "Services/OkVendor.cs": ("class A { void M() { var r = _db.VendorIntel.Where(v => v.VendorTaxId == t); } }\n", False),
        "Services/OkComment.cs": ("class A {\n  // _db.Contacts.Where(c => c.TaxId == t)\n"
                                 "  /* _db.Contacts.Where(c => c.TaxId == t); */ void M() { } }\n", False),
        "Helpers/ContactTaxBranchKey.cs": ("class K { void M() { var r = scope.Contacts.Where(c => c.TaxId == raw); } }\n", False),
        "bin/Gen.cs": ("class A { void M() { var r = _db.Contacts.Where(c => c.TaxId == t); } }\n", False),
        "obj/Gen.cs": ("class A { void M() { var r = _db.Contacts.Where(c => c.TaxId == t); } }\n", False),
        ".claude/worktrees/x/Accounting/Svc.cs": ("class A { void M() { var r = _db.Contacts.Where(c => c.TaxId == t); } }\n", False),
    }
    with tempfile.TemporaryDirectory() as tmp:
        for rel, (content, _) in cases.items():
            p = os.path.join(tmp, rel)
            os.makedirs(os.path.dirname(p), exist_ok=True)
            open(p, "w", encoding="utf-8").write(content)
        found = {rel for rel, _ in scan(tmp)}
    for rel, (_, expect) in cases.items():
        got = rel in found
        if got != expect:
            ok = False
            print(f"❌ self-test: {rel} — คาด {'ฟ้อง' if expect else 'ไม่ฟ้อง'} แต่{'ฟ้อง' if got else 'ไม่ฟ้อง'}")
    if ok:
        print("✅ self-test ผ่าน — จับ TaxId-only 6 รูปได้ · ไม่ฟ้องรูปที่มีสาขา/ตัวจับคู่กลาง/null-check/Company/Vendor/คอมเมนต์ · ข้าม bin obj .claude")
    return 0 if ok else 1


def main():
    args = sys.argv[1:]
    if "--self-test" in args:
        return self_test()
    hits = scan()
    if "--write-baseline" in args:
        write_baseline(hits)
        print(f"เขียน baseline แล้ว: {sum(len(l) for _, l in hits)} จุด ใน {len(hits)} ไฟล์")
        return 0
    base = load_baseline()
    cur = {rel: len(lines) for rel, lines in hits}
    new = [(rel, lines) for rel, lines in hits if len(lines) > base.get(rel, 0)]
    gone = sorted(k for k in base if base[k] > cur.get(k, 0))
    if "--all" in args:
        for rel, lines in hits:
            mark = "ใหม่" if len(lines) > base.get(rel, 0) else "baseline"
            print(f"  [{mark}] {rel} (บรรทัด {', '.join(map(str, lines))})")
    print(f"query ผู้ติดต่อด้วยเลขภาษีอย่างเดียว: {sum(cur.values())} (baseline {sum(base.values())})")
    if gone:
        print(f"ℹ️  {len(gone)} รายการใน baseline ลดลงแล้ว — ลดตัวเลขใน baseline ในคอมมิตเดียวกัน:")
        for g in gone:
            print(f"   - {g} (baseline {base[g]} → ปัจจุบัน {cur.get(g, 0)})")
    if new:
        print(f"❌ จุด**ใหม่** {len(new)} ไฟล์ — หา/จับคู่ผู้ติดต่อด้วยเลขภาษีโดยไม่มีสาขา (§86/4 · ประกาศอธิบดีฯ 199):")
        for rel, lines in new:
            print(f"   {rel} (บรรทัด {', '.join(map(str, lines))}) — baseline {base.get(rel, 0)} → ปัจจุบัน {len(lines)}")
        print("   → ใช้ Helpers/ContactTaxBranchKey.FindAsync/Pick (เลขภาษี + สาขา) แทน `c.TaxId == x`")
        print("   → ถ้าตั้งใจรวมทุกสาขาจริง ให้ใส่ BranchCode ในนิพจน์ไม่ได้ — ปรึกษาก่อน ห้ามเติม baseline เพื่อให้ผ่าน")
        return 1
    print("✅ ไม่มี query ผู้ติดต่อด้วยเลขภาษีอย่างเดียวจุดใหม่")
    return 0


if __name__ == "__main__":
    sys.exit(main())
