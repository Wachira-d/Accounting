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

═══ กติกาที่สอง — hook ต้องอยู่ **หลัง** commit (ฝ่ายค้านรอบสอง R2-C12) ═══
hook ออก e-Tax เปิดธุรกรรม/ล็อกแถวเอกสาร (`FOR UPDATE`) ของตัวเอง ⇒ เรียกขณะธุรกรรมของผู้เรียกยังเปิดอยู่ =
(1) อ่านเอกสารที่ยังไม่ commit ไม่เห็น/เห็นไม่ครบ (2) รอล็อกแถวที่ธุรกรรมของตัวเองถืออยู่ (deadlock/timeout) และ
(3) ถ้าธุรกรรมของผู้เรียก rollback ทีหลัง e-Tax ที่ส่งออกไปแล้วเรียกคืนไม่ได้. รอบแรกของ checker ตรวจแค่ "มีการเรียก"
⇒ ย้าย hook ไปก่อน `CommitAsync` แล้วยังเขียว. กติกา: ในเมธอดที่ครอบการเรียก hook ถ้ามี `BeginTransactionAsync(`
ก่อนหน้า hook ต้องมี `CommitAsync(` **หลัง** `BeginTransactionAsync` ตัวล่าสุดนั้น และ**ก่อน** hook — ไม่มี baseline (ต้องเป็น 0)
ขอบเขต: ธุรกรรมที่ผู้เรียกเปิดไว้ในเมธอดอื่นมองไม่เห็น (ต้อง type/flow analysis — F4 ข้อ 1)

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


BEGIN_TX_RE = re.compile(r"\bBeginTransactionAsync\s*\(")
COMMIT_RE = re.compile(r"\bCommitAsync\s*\(")


def hooks_before_commit():
    """[(ไฟล์:เมธอด, บรรทัด)] ของการเรียก hook ที่ธุรกรรมในเมธอดเดียวกันยังไม่ commit (R2-C12)"""
    out = []
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
            hooks = [m.start() for m in HOOK_RE.finditer(body)]
            if not hooks:
                continue
            ms = methods(body)
            for h in hooks:
                om = outer_method(ms, h)
                if om is None:
                    continue
                # ธุรกรรมที่ "ยังครอบ hook อยู่" = บล็อกของ BeginTransaction ยังไม่ปิดก่อนถึง hook
                begins = [m.start() for m in BEGIN_TX_RE.finditer(body, om[1], h) if encloses(body, m.start(), h)]
                if not begins:
                    continue
                # commit ที่นับ = อยู่บนเส้นทางเดียวกับ hook (บล็อกของมันยังไม่ปิดก่อนถึง hook) — commit ใน
                # `if (…) { await tx.CommitAsync(); return; }` ของทางออกก่อนกำหนด **ไม่นับ** (ไม่ใช่เส้นที่ hook อยู่)
                if any(on_path(body, m.start(), h) for m in COMMIT_RE.finditer(body, begins[-1], h)):
                    continue
                out.append((f"{rel}:{om[0]}", body.count("\n", 0, h) + 1))
    return out


def encloses(body: str, pos: int, target: int) -> bool:
    """บล็อก `{…}` ที่ครอบ pos ยังไม่ปิดก่อนถึง target (ไม่มี `}` ที่ทำให้ระดับต่ำกว่าระดับของ pos)"""
    depth = 0
    for ch in body[pos:target]:
        if ch == "{":
            depth += 1
        elif ch == "}":
            depth -= 1
            if depth < 0:
                return False
    return True


CONDITIONAL_HEADS = ("if", "else", "while", "for", "foreach", "switch", "catch", "case", "default")


def _open_brace_before(body: str, pos: int) -> int:
    """ตำแหน่ง `{` ของบล็อกที่ครอบ pos (สแกนย้อน)"""
    depth = 0
    for j in range(pos - 1, -1, -1):
        ch = body[j]
        if ch == "}":
            depth += 1
        elif ch == "{":
            if depth == 0:
                return j
            depth -= 1
    return -1


def _block_is_conditional(body: str, brace: int) -> bool:
    """บล็อกที่เปิดด้วย `{` ตรงนี้ "อาจไม่ถูกรัน" ไหม — if/else/loop/catch/switch = ใช่ · try/using/lock/lambda/บล็อกเปล่า = ไม่"""
    head = body[:brace].rstrip()
    if re.search(r"\b(try|else|default)\s*$", head):
        return not head.endswith("try")
    if head.endswith(")"):
        depth, j = 0, len(head) - 1
        while j >= 0:
            if head[j] == ")":
                depth += 1
            elif head[j] == "(":
                depth -= 1
                if depth == 0:
                    break
            j -= 1
        kw = re.search(r"(\w+)\s*$", head[:j]) if j > 0 else None
        return bool(kw and kw.group(1) in CONDITIONAL_HEADS)
    return bool(re.search(r"\bcase\b[^;{}]*:\s*$", head))


def on_path(body: str, pos: int, target: int) -> bool:
    """คำสั่งที่ pos อยู่บนเส้นทางที่ต้องผ่านก่อนถึง target ไหม — ออกจากบล็อก try/using/lambda ได้
    (ยังเป็นเส้นหลัก) แต่ออกจากบล็อกเงื่อนไข (if/else/catch/loop) = ไม่ใช่ (เช่น commit ในทางออกก่อนกำหนด)"""
    depth, j = 0, pos
    while j < target:
        ch = body[j]
        if ch == "{":
            depth += 1
        elif ch == "}":
            depth -= 1
            if depth < 0:
                opener = _open_brace_before(body, pos)
                if opener < 0 or _block_is_conditional(body, opener):
                    return False
                pos, depth = opener, 0   # ขึ้นไปบล็อกแม่ แล้วเดินต่อ
        j += 1
    return True


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
    # R2-C12 — hook ก่อน commit ต้องฟ้อง · หลัง commit / ไม่มีธุรกรรม / ธุรกรรมที่สองหลัง commit แรก ต้องไม่ฟ้อง
    "EarlyHookService.cs": (
        "class EarlyHook {\n"
        "  public async Task Pay(Guid c) {\n"
        "    await using var tx = await _db.Database.BeginTransactionAsync();\n"
        "    var r = new Document { Status = DocumentStatus.Paid };\n"
        "    await _db.SaveChangesAsync();\n"
        "    await _issuedHooks.RunAsync(c, r);\n"
        "    await tx.CommitAsync();\n"
        "  }\n"
        "  public async Task EarlyReturn(Guid c, Document d, bool dup) {\n"
        "    await using var tx = await _db.Database.BeginTransactionAsync();\n"
        "    if (dup) { await tx.CommitAsync(); return; }\n"
        "    await _issuedHooks.RunAsync(c, d);\n"
        "    await tx.CommitAsync();\n"
        "  }\n"
        "  public async Task Twice(Guid c, Document d) {\n"
        "    var t1 = await _db.Database.BeginTransactionAsync(); await t1.CommitAsync();\n"
        "    var t2 = await _db.Database.BeginTransactionAsync();\n"
        "    await _issuedHooks.RunAsync(c, d);\n"
        "    await t2.CommitAsync();\n"
        "  }\n"
        "}\n"),
    "LateHookService.cs": (
        "class LateHook {\n"
        "  public async Task Pay(Guid c) {\n"
        "    await using var tx = await _db.Database.BeginTransactionAsync();\n"
        "    var r = new Document { Status = DocumentStatus.Paid };\n"
        "    await _db.SaveChangesAsync();\n"
        "    await tx.CommitAsync();\n"
        "    await _issuedHooks.RunAsync(c, r);\n"
        "  }\n"
        "  public async Task NoTx(Guid c, Document d) { await _issuedHooks.RunAsync(c, d); }\n"
        "  public async Task Nested(Guid c, Document d) {\n"
        "    await using var tx = await _db.Database.BeginTransactionAsync();\n"
        "    try { await _db.SaveChangesAsync(); await tx.CommitAsync(); } catch { await tx.RollbackAsync(); throw; }\n"
        "    if (d != null) { await _issuedHooks.RunAsync(c, d); }\n"
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
            early = hooks_before_commit()
        finally:
            SRC = saved
    keys = set(hits.keys())
    must = ["BadService.cs:Make", "PaidService.cs:Receipt", "PaidService.cs:FromVar",
            "PaidService.cs:Stamp", "HelperMissService.cs:Build2"]
    for k in must:
        if f"Services/Implementations/{k}" not in keys:
            print(f"❌ self-test: ไม่จับ {k}", sorted(keys)); ok = False
    for bad in ("GoodService", "ReaderService", "HelperService.cs", "LateHookService", "EarlyHookService"):
        if any(bad in k for k in keys):
            print(f"❌ self-test: ฟ้องผิด {bad}", sorted(keys)); ok = False
    early_keys = sorted(k for k, _ in early)
    for k in ("EarlyHookService.cs:Pay", "EarlyHookService.cs:Twice", "EarlyHookService.cs:EarlyReturn"):
        if f"Services/Implementations/{k}" not in early_keys:
            print(f"❌ self-test (hook ก่อน commit): ไม่จับ {k}", early_keys); ok = False
    if any("LateHookService" in k for k in early_keys):
        print("❌ self-test (hook ก่อน commit): ฟ้องผิด LateHookService", early_keys); ok = False
    if ok:
        print("✅ self-test ผ่าน — จับ: ลืม hook · Paid ข้ามบรรทัด · สถานะจากตัวแปร · ประทับตรง · ตัวช่วยที่ผู้เรียกลืม · "
              "hook ก่อน commit (รวมธุรกรรมที่สอง) · "
              "ไม่ฟ้อง: เรียก hook แล้ว · ตัวช่วยที่ผู้เรียกครบ · ร่าง · การอ่าน/คอมเมนต์ · คำนวณสถานะคืน · "
              "hook หลัง commit · ไม่มีธุรกรรม")
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
    early = hooks_before_commit()
    if early:
        print(f"❌ เรียก IssuedDocumentHooks ขณะธุรกรรมในเมธอดเดียวกันยังไม่ commit {len(early)} จุด (R2-C12):")
        for k, ln in early:
            print(f"   {k} (บรรทัด {ln})")
        print("   → ย้าย `_issuedHooks.RunAsync` ไปหลัง `CommitAsync` — hook เปิดธุรกรรม/ล็อกแถวของตัวเอง "
              "และ e-Tax ที่ส่งออกไปแล้วเรียกคืนไม่ได้ถ้าธุรกรรมของผู้เรียก rollback")
        return 1
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
