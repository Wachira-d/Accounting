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

═══ หลังฝ่ายค้าน (review193-V-C3) ═══
กติกา 1 ปิดจุดบอด: list ที่ await แล้ว · `string.Equals(c.TaxId, x)` · ยกเว้นเฉพาะเมื่อสาขาถูก**เทียบ**/ป้อนตัวตัดสินที่ดูสาขา
(ไม่ใช่แค่มีคำว่า BranchCode) · navigation `x.Contact.TaxId` นับเฉพาะ x ที่เป็นพารามิเตอร์ lambda (ไม่ฟ้อง `doc.Contact.TaxId == company.TaxId`)
กติกา 2 (key `ไฟล์#soft`): หลัง `ContactTaxBranchKey.FindAsync` ในเมธอดเดียวกัน การจับผู้ติดต่อด้วย Name/Email/Phone บนแหล่ง
ผู้ติดต่อตรง ๆ ต้องผ่าน `ContactTaxBranchKey.SoftScope` (เลขใหม่ ⇒ เฉพาะแถวที่ยังไม่มีเลข · เลขมีแล้วคนละสาขา ⇒ ห้าม)
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
# navigation `x.Contact.TaxId ==` นับเฉพาะเมื่อ x เป็นพารามิเตอร์ของ lambda ในประโยคเดียวกัน (= query) —
# `doc.Contact.TaxId == company.TaxId` (ตรวจ "ออกใบให้ตัวเอง" บนออบเจกต์) ไม่ใช่การหาผู้ติดต่อ (ฝ่ายค้าน: เคยฟ้องผิด)
NAV_CMP = r"\b{p}\s*\.\s*(?:Payee)?Contact\s*\.\s*TaxId\s*(?:==|\.Equals\()"
LAMBDA_PARAM = re.compile(r"\b(\w+)\s*=>")
TAX_CMP = re.compile(
    r"\.TaxId\s*==\s*(?!\s*null\b)(?!\s*\"\")"              # c.TaxId == x
    r"|(?<![=!<>])==\s*[\w.()?!\[\]]*\.TaxId\b"              # x == c.TaxId
    r"|\.Contains\(\s*\w+\s*\.\s*TaxId\s*!?\s*\)"            # ids.Contains(c.TaxId)
    r"|\.TaxId\s*\.\s*Equals\("                              # c.TaxId.Equals(x)
    r"|\w\(\s*\w+\s*\.\s*TaxId\s*\)\s*==(?!\s*null\b)"       # Normalize(c.TaxId) == x
    r"|\bstring\s*\.\s*Equals\s*\([^;]*?\.TaxId\b"            # string.Equals(c.TaxId, x) (ฝ่ายค้าน: จุดบอด)
)
# ยกเว้นเฉพาะเมื่อสาขาถูก "เทียบ" ในประโยคเดียวกัน หรือผ่านตัวจับคู่กลาง — แค่มีคำว่า BranchCode (เช่น .Select(new { c.BranchCode }))
# ไม่นับ (ฝ่ายค้าน: เคยเป็นจุดบอด)
EXEMPT = re.compile(
    # ฝ่ายค้านรอบสอง R2-C10: ยกเว้นเฉพาะนิพจน์ที่เรียกตัวจับคู่กลาง "จริง" — ไม่ใช่แค่มีคำ ContactTaxBranchKey (HasTaxId ในหัว if ก็เคยนับ)
    r"\bContactTaxBranchKey\s*\.\s*(?:FindAsync|Pick|PickContact|LoadByTaxIdsAsync|AllBranchIdsAsync|SoftScope)\b"
    r"|\bContactKeyCandidate\b|\bOcrVendorBranchContact\b"   # ป้อนตัวตัดสินที่ดูสาขา
    # สาขาถูก "เทียบกับค่า" — `c.BranchCode != null` ไม่ใช่การเทียบสาขา (R2 probe C5)
    r"|\.BranchCode\s*(?:==|!=)(?!\s*null\b)"
    r"|\.BranchCode\s*\.\s*Equals\("
    r"|(?<!null)\s(?:==|!=)\s*[\w.()?!]*\.BranchCode\b"
    r"|\w\(\s*\w+\s*\.\s*BranchCode\s*\)\s*(?:==|!=)")
# ตัวแปรที่มาจากผู้ติดต่อ: ทั้ง IQueryable (ยังไม่ await) และ list ที่ await ToListAsync แล้ว (ฝ่ายค้าน: จุดบอด)
QUERY_VAR = re.compile(r"\b(\w+)\s*=\s*(?:\(\s*IQueryable<[^>]*>\s*\))?[^;=]*?(?:\.Contacts\b|Set\s*<\s*(?:Models\.Entities\.)?Contact\s*>\s*\()")
TAINT_USE = r"\b{v}\s*\.\s*(?:Where|Any|All|First|FirstOrDefault|Single|SingleOrDefault|Count|Select|OrderBy|ToDictionary|GroupBy)\b"

# ── กติกา 2 (ฝ่ายค้าน C-6): หลังคีย์เลขภาษี (ContactTaxBranchKey.FindAsync) ในเมธอดเดียวกัน ห้ามถอยไปจับผู้ติดต่อด้วย
# ชื่อ/อีเมล/เบอร์ บนแหล่งผู้ติดต่อตรง ๆ — ต้องจับบนชุด ContactTaxBranchKey.SoftScope(...) เท่านั้น
SOFT_CMP = re.compile(
    r"\.(?:Name|NameEn|ContactPerson|Email|Phone)\b(?:\s*\.\s*(?:Trim|ToLower|ToUpper|ToLowerInvariant|ToUpperInvariant)\s*\(\s*\))*\s*=="
    r"|\.(?:Name|NameEn|ContactPerson|Email|Phone)\s*\.\s*(?:Contains|StartsWith|Equals)\s*\(")
# ผู้สมัครเทียบชื่อแบบ fuzzy (CounterpartyNameMatcher) โหลดจากแหล่งผู้ติดต่อตรง ๆ (R2 probe C3)
NAME_CANDIDATES = re.compile(r"Select\s*\(\s*\w+\s*=>\s*new\s*\{[^}]*\.Name(?:En)?\b")
METHOD_DECL = re.compile(r"^[ \t]*(?:public|private|internal|protected)\b[^;\n]*\(", re.M)

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


def _statements(body):
    pos = 0
    for stmt in body.split(";"):
        yield pos, stmt
        pos += len(stmt) + 1


def scan_text(body):
    """กติกา 1 — คืนเลขบรรทัดของประโยคที่หา/จับคู่ผู้ติดต่อด้วยเลขภาษีโดยไม่มีสาขา"""
    tainted = set()
    for _, stmt in _statements(body):
        for m in QUERY_VAR.finditer(stmt):
            tainted.add(m.group(1))
    hits = []
    for start, stmt in _statements(body):
        # R2-C10: ตัดหัวบล็อกที่ติดมากับประโยคแรก (`if (ContactTaxBranchKey.HasTaxId(x)) {`) — เริ่มพิจารณาที่บรรทัดของแหล่งผู้ติดต่อ
        src_hits = [m.start() for m in [CONTACT_SRC.search(stmt)] if m] + [
            m.start() for v in tainted for m in [re.search(TAINT_USE.format(v=re.escape(v)), stmt)] if m]
        if src_hits:
            cut = stmt.rfind("\n", 0, min(src_hits)) + 1
            start, stmt = start + cut, stmt[cut:]
        if EXEMPT.search(stmt):
            continue
        is_contact = bool(src_hits)
        m = TAX_CMP.search(stmt) if is_contact else None
        if m is None:
            for p in set(LAMBDA_PARAM.findall(stmt)):
                m = re.search(NAV_CMP.format(p=re.escape(p)), stmt)
                if m:
                    break
        if m is None:
            continue
        hits.append(body.count("\n", 0, start + m.start()) + 1)
    return hits


def scan_soft(body):
    """กติกา 2 — คืนเลขบรรทัดของการถอยไปจับชื่อ/อีเมล/เบอร์บนแหล่งผู้ติดต่อตรง ๆ หลัง FindAsync ในเมธอดเดียวกัน"""
    hits = []
    decls = [m.start() for m in METHOD_DECL.finditer(body)] + [len(body)]
    for fm in re.finditer(r"ContactTaxBranchKey\s*\.\s*FindAsync\s*\(", body):
        end = next((d for d in decls if d > fm.start()), len(body))
        window = body[fm.end():end]
        fuzzy = "CounterpartyNameMatcher" in window
        for start, stmt in _statements(window):
            if "SoftScope" in stmt or not CONTACT_SRC.search(stmt):
                continue
            # ผู้สมัครแบบ fuzzy — ไม่นับ query ที่ดึงแถวเดียวด้วย Id (เช่นอ่านชื่อของแถวที่รู้ตัวแล้ว)
            m = SOFT_CMP.search(stmt) or (NAME_CANDIDATES.search(stmt)
                                          if fuzzy and not re.search(r"\.Id\s*==", stmt) else None)
            if m:
                hits.append(body.count("\n", 0, fm.end() + start + m.start()) + 1)
    return sorted(set(hits))


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
            body = strip_comments(raw)
            lines = scan_text(body)
            if lines:
                out.append((rel, lines))
            soft = scan_soft(body)
            if soft:
                out.append((rel + "#soft", soft))
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
        # ── จุดบอดที่ฝ่ายค้านพบ (รอบ 193) ──
        "Services/BadMaterialized.cs": ("class A { async Task M() { var all = await _db.Contacts.Where(c => c.CompanyId == cid).ToListAsync();\n"
                                        "  var hit = all.FirstOrDefault(c => c.TaxId == t); } }\n", True),
        "Services/BadStringEquals.cs": ("class A { void M() { var r = _db.Contacts.FirstOrDefault(c => string.Equals(c.TaxId, t)); } }\n", True),
        "Services/BadBranchOnlySelected.cs": ("class A { void M() { var r = _db.Contacts.Where(c => c.TaxId == t).Select(c => new { c.Id, c.BranchCode }).ToList(); } }\n", True),
        "Services/OkSelfInvoiceCheck.cs": ("class A { bool M(Document doc, Company company) { return doc.Contact.TaxId == company.TaxId; } }\n", False),
        "Services/OkBranchDecider.cs": ("class A { void M() { var r = _db.Contacts.Where(c => c.TaxId != null).ToList()\n"
                                        "  .Where(c => Norm(c.TaxId) == d).Select(c => new OcrVendorBranchContact.Candidate(c.Id, c.Name, c.BranchCode)).ToList(); } }\n", False),
        # ── กติกา 2: ถอยไปชื่อ/อีเมล/เบอร์หลัง FindAsync ต้องผ่าน SoftScope ──
        "Services/SoftBad.cs": ("class A {\n  private async Task M() {\n"
                                "    var k = await ContactTaxBranchKey.FindAsync(_db.Contacts, cid, t, b);\n"
                                "    if (k.ContactId == null && !k.TaxIdExists) c = await _db.Contacts.FirstOrDefaultAsync(x => x.CompanyId == cid && x.Email == email);\n"
                                "  }\n}\n", True),
        "Services/SoftBadName.cs": ("class A {\n  private async Task M() {\n"
                                    "    var k = await ContactTaxBranchKey.FindAsync(_db.Set<Contact>(), cid, t, null);\n"
                                    "    c = await _db.Set<Contact>().FirstOrDefaultAsync(x => x.Name.ToLower() == n.ToLower());\n"
                                    "  }\n}\n", True),
        "Services/SoftOk.cs": ("class A {\n  private async Task M() {\n"
                               "    var k = await ContactTaxBranchKey.FindAsync(_db.Contacts, cid, t, b);\n"
                               "    var soft = ContactTaxBranchKey.SoftScope(_db.Contacts, cid, t, k);\n"
                               "    if (soft != null) c = await soft.FirstOrDefaultAsync(x => x.Email == email);\n"
                               "  }\n"
                               "  private async Task Other() { var x = await _db.Contacts.FirstOrDefaultAsync(c => c.Name == n); }\n}\n", False),
        # ── ฝ่ายค้านรอบสอง (probe C2–C5) ──
        "Services/BadInsideHasTaxIdBlock.cs": ("class A {\n  private async Task<Contact?> M() {\n"
                                               "    if (ContactTaxBranchKey.HasTaxId(partner.TaxId))\n    {\n"
                                               "      return await _db.Contacts.FirstOrDefaultAsync(c => c.CompanyId == o && c.TaxId == partner.TaxId);\n"
                                               "    }\n    return null;\n  }\n}\n", True),
        "Services/BadBranchNotNull.cs": ("class A { void M() { var r = _db.Contacts.Where(c => c.TaxId == t && c.BranchCode != null).ToList(); } }\n", True),
        "Services/SoftBadNameEn.cs": ("class A {\n  private async Task M() {\n"
                                      "    var k = await ContactTaxBranchKey.FindAsync(_db.Contacts, cid, t, b);\n"
                                      "    c = await _db.Contacts.FirstOrDefaultAsync(x => x.NameEn == n);\n  }\n}\n", True),
        "Services/SoftBadFuzzy.cs": ("class A {\n  private async Task M() {\n"
                                     "    var k = await ContactTaxBranchKey.FindAsync(_db.Contacts, cid, t, b);\n"
                                     "    var cands = await _db.Contacts.Where(c => c.CompanyId == cid).Select(c => new { c.Id, c.Name }).ToListAsync();\n"
                                     "    var best = CounterpartyNameMatcher.Best(n, cands, c => c.Name);\n  }\n}\n", True),
        "Services/OkBranchCompared.cs": ("class A { void M() { var r = _db.Contacts.Where(c => c.TaxId == t && c.BranchCode == b).ToList(); } }\n", False),
        "Services/OkFindInIf.cs": ("class A {\n  private async Task M() {\n"
                                   "    if (ContactTaxBranchKey.HasTaxId(t))\n    {\n"
                                   "      var key = await ContactTaxBranchKey.FindAsync(_db.Contacts, o, t, b);\n    }\n  }\n}\n", False),
        "Services/SoftOkFuzzy.cs": ("class A {\n  private async Task M() {\n"
                                    "    var k = await ContactTaxBranchKey.FindAsync(_db.Contacts, cid, t, b);\n"
                                    "    var soft = ContactTaxBranchKey.SoftScope(_db.Contacts, cid, t, k);\n"
                                    "    var cands = await soft.Select(c => new { c.Id, c.Name }).ToListAsync();\n"
                                    "    var best = CounterpartyNameMatcher.Best(n, cands, c => c.Name);\n  }\n}\n", False),
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
        found = {rel.split("#")[0] for rel, _ in scan(tmp)}
    for rel, (_, expect) in cases.items():
        got = rel in found
        if got != expect:
            ok = False
            print(f"❌ self-test: {rel} — คาด {'ฟ้อง' if expect else 'ไม่ฟ้อง'} แต่{'ฟ้อง' if got else 'ไม่ฟ้อง'}")
    if ok:
        print("✅ self-test ผ่าน — จับ TaxId-only 11 รูป + soft match นอก SoftScope 4 รูป · ไม่ฟ้องรูปที่ถูก 13 รูป (สาขา/ตัวจับคู่กลาง/ตัวตัดสินสาขา/null-check/Company/Vendor/ออกใบให้ตัวเอง/คอมเมนต์/SoftScope/เมธอดอื่น/FindAsync ในบล็อก if/fuzzy บน SoftScope) · ข้าม bin obj .claude")
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
        print("   → ใช้ Helpers/ContactTaxBranchKey.FindAsync/Pick (เลขภาษี + สาขา) แทน `c.TaxId == x` · ตั้งใจรวมทุกสาขา = AllBranchIdsAsync")
        print("   → แถว #soft: หลัง FindAsync ห้ามจับชื่อ/อีเมล/เบอร์บน _db.Contacts ตรง ๆ — ใช้ ContactTaxBranchKey.SoftScope(...)")
        print("   → ถ้าตั้งใจรวมทุกสาขาจริง ให้ใส่ BranchCode ในนิพจน์ไม่ได้ — ปรึกษาก่อน ห้ามเติม baseline เพื่อให้ผ่าน")
        return 1
    print("✅ ไม่มี query ผู้ติดต่อด้วยเลขภาษีอย่างเดียวจุดใหม่")
    return 0


if __name__ == "__main__":
    sys.exit(main())
