#!/usr/bin/env python3
# -*- coding: utf-8 -*-
"""attachment_gate_check.py — ทุก action ที่อ่าน/เขียนไฟล์แนบต้อง **เรียกด่านกลางจริงในตัวเมธอด**

═══ ที่มา (ฝ่ายค้านรอบ 193 · P6) ═══
เทสต์ของด่านไฟล์แนบ (U2) ทดสอบแค่ตาราง `AttachmentPermissionScope` (pure) — ถอดการเรียกด่านออกจาก
controller แล้วเทสต์ยังเขียวทุกตัว · repo นี้ไม่มี WebApplicationFactory / DB ในเทสต์ จึงยิง endpoint จริงไม่ได้
และ `write_permission_gate_check` นับ "มีคำนี้อยู่ในช่วงข้อความของ action" ⇒ มองไม่เห็นกรณีที่พบจริงในรอบนี้:
`StatutoryRemittanceController.UploadReceipt` ไม่มีด่านเลย แต่ช่วงข้อความของมันลากไปถึงเมธอด private
`ResolveUserIdAsync` ข้างล่างที่มีคำว่า `UserRole.Owner` ⇒ checker ตัวนั้นเขียว (false negative)

═══ กติกา (ต่อ action ใน TARGETS) ═══
1. หาเมธอดด้วยการ parse (ตัดคอมเมนต์/สตริงออกก่อน) ⇒ ชื่อด่านในคอมเมนต์หรือในสตริง **ไม่นับ**
2. ต้อง `await` ด่านที่กำหนด **ในตัวเมธอดนั้นเอง** และเก็บผลไว้ในตัวแปร
3. ตัวแปรนั้นต้องถูกใช้ตัดสินจริง (`if (deny …)` / `hidden.Contains(…)`) — เรียกแล้วทิ้งผล = ไม่มีด่าน
4. การเรียกด่านต้องมา **ก่อน** จุดที่แตะไฟล์/ข้อมูล (sink) ตัวแรกในเมธอด
5. ไม่พบเมธอด = ฟ้อง (เปลี่ยนชื่อ action แล้ว checker ห้ามเขียวเงียบ)

═══ ค้นหาทางเข้าใหม่ (allow-list ครบไหม ≠ ผ่านไหม) ═══
ทุกเมธอดใน `Accounting/Controllers/**` ที่เรียก service ไฟล์แนบ (field ชนิด `IFileAttachmentService`) หรือแตะ
`.FileAttachments` ต้องอยู่ใน TARGETS หรือ EXEMPT (พร้อมเหตุผล) — ทางเข้าใหม่ที่ไม่มีใครตัดสินจะถูกฟ้อง ·
ข้าม `.claude` `bin` `obj`

รันเปล่า ๆ = ตรวจเรพ **และ** negative test (ถอดการเรียกด่านจากไฟล์จริงทีละ target แล้วต้องฟ้อง) ·
`--self-test` = negative test อย่างเดียว
"""
import os
import re
import sys

ROOT = os.path.dirname(os.path.dirname(os.path.abspath(__file__)))
CONTROLLERS = os.path.join(ROOT, "Accounting", "Controllers")
SKIP_DIRS = {".claude", "bin", "obj", ".git", "node_modules"}

DENY_USE = r"\bif\s*\(\s*{v}\b"                   # if (deny is { } d) / if (deny != null)
HIDDEN_USE = r"\b{v}\s*\.\s*Contains\s*\("        # hidden.Contains(x.Id)

# (ไฟล์, เมธอด, regex ของการเรียกด่าน, regex ของการใช้ผล, sinks — ต้องมาหลังด่าน)
TARGETS = [
    # ── FileAttachmentController: ทุก action ผ่าน wrapper ที่ส่งต่อไป IAttachmentAccessGate ──
    ("Accounting/Controllers/FileAttachmentController.cs", "Upload",
     r"DenyAttachmentAsync\s*\(", DENY_USE,
     [r"\bReadHeadAsync\s*\(", r"\bProcessAndSaveAsync\s*\(", r"\bFileStream\b", r"\.UploadAsync\s*\("]),
    ("Accounting/Controllers/FileAttachmentController.cs", "GetByEntity",
     r"DenyAttachmentAsync\s*\(", DENY_USE, [r"\.GetByEntityAsync\s*\("]),
    ("Accounting/Controllers/FileAttachmentController.cs", "Delete",
     r"DenyAttachmentAsync\s*\(", DENY_USE, [r"\.DeleteAsync\s*\("]),
    ("Accounting/Controllers/FileAttachmentController.cs", "Download",
     r"DenyAttachmentAsync\s*\(", DENY_USE, [r"\bPhysicalFile\s*\(", r"File\.Exists\s*\("]),
    # ── ใบเสร็จนำส่ง (C2) ──
    ("Accounting/Controllers/StatutoryRemittanceController.cs", "UploadReceipt",
     r"\.DenyAttachmentAsync\s*\(", DENY_USE,
     [r"\.CopyToAsync\s*\(", r"\.UploadBytesAsync\s*\(", r"\.AttachReceiptAsync\s*\("]),
    # ── รูป/ผลอ่าน/รายการสแกน (C3) ──
    ("Accounting/Controllers/OcrController.cs", "GetImage",
     r"\.DenyScanAsync\s*\(", DENY_USE, [r"\.FileAttachments\b", r"\bPhysicalFile\s*\("]),
    ("Accounting/Controllers/OcrController.cs", "GetResult",
     r"\.DenyScanAsync\s*\(", DENY_USE, [r"\.GetResultAsync\s*\("]),
    ("Accounting/Controllers/OcrController.cs", "GetResults",
     r"\.HiddenScanIdsAsync\s*\(", HIDDEN_USE, [r"\bOk\s*\("]),
    # คิวรอตรวจรวมสแกนที่ผูกเอกสารแล้วแต่ติดธงสินทรัพย์ ⇒ ชื่อผู้ขาย/ชื่อไฟล์ต้องผ่านด่านเดียวกัน
    ("Accounting/Controllers/OcrController.cs", "ReviewQueue",
     r"\.HiddenScanIdsAsync\s*\(", HIDDEN_USE, [r"ScanQualityGrader\s*\.", r"\bOk\s*\("]),
]

# ด่านชั้นใน — wrapper/ตัวตัดสินต้อง "ส่งต่อจริง" (ไม่ใช่ return null) · (ไฟล์, เมธอด, regex ที่ต้องเรียก)
DELEGATES = [
    # wrapper ใน controller ต้องส่งต่อไป service จริง
    ("Accounting/Controllers/FileAttachmentController.cs", "DenyAttachmentAsync",
     [r"_gate\s*\.\s*DenyAttachmentAsync\s*\("]),
    # ใบเบิก (P1): เจ้าของใบแนบ/ถอดได้ตามสถานะใบเท่านั้น · สแกนที่ผูกเอกสาร (C3): ใช้ด่านเอกสาร
    ("Accounting/Services/Implementations/AttachmentAccessGate.cs", "DenyAttachmentAsync",
     [r"ExpenseClaimEvidencePolicy\s*\.\s*OwnerMayChange\s*\(", r"\bDenyScanCoreAsync\s*\(",
      r"AttachmentPermissionScope\s*\.\s*IsUnsavedBucket\s*\(", r"AttachmentPermissionScope\s*\.\s*UnsavedFileVisible\s*\("]),
    ("Accounting/Services/Implementations/AttachmentAccessGate.cs", "DenyScanCoreAsync",
     [r"AttachmentPermissionScope\s*\.\s*ScanFileOwner\s*\(", r"\bDenyDocAsync\s*\("]),
    ("Accounting/Services/Implementations/AttachmentAccessGate.cs", "HiddenScanIdsAsync",
     [r"AttachmentPermissionScope\s*\.\s*ScanFileOwner\s*\(", r"AttachmentPermissionScope\s*\.\s*DocumentReadable\s*\("]),
]

# ทางเข้าที่แตะไฟล์แนบแต่ตั้งใจไม่ผ่านด่านนี้ — ต้องมีเหตุผลทุกแถว (เพิ่มแถว = การตัดสินใจที่ตั้งใจ)
EXEMPT = {
    ("Accounting/Controllers/IntegrationController.cs", "AttachFilesAsync"):
        "แนบไฟล์ให้เอกสารที่คีย์ integration เพิ่งสร้างในคำขอเดียวกัน — ด่านคือคีย์ + scope (write_permission_gate_check)",
    ("Accounting/Controllers/OcrController.cs", "UploadAndScan"):
        "สร้างไฟล์ต้นฉบับของสแกนใหม่ (ยังไม่ผูกเอกสาร) · เส้นอัปโหลดอยู่กับทีม O1 รอบ 193 — backlog ใน r193-S2.md",
    ("Accounting/Controllers/V1/OcrV1Controller.cs", "SaveUploadAsync"):
        "API v1 — ผู้เรียก (Scan) ผ่าน ResolveCallerAsync(\"ocr:write\") ก่อน · ไฟล์เป็นของสแกนใหม่",
}


# ─────────────────────────── C# lexer (ตัดคอมเมนต์/สตริง คงตำแหน่ง) ───────────────────────────

def strip_code(src):
    """แทนเนื้อคอมเมนต์และสตริงด้วยช่องว่าง (คง newline) — ตำแหน่งตัวอักษรเท่าเดิม"""
    out = list(src)
    n = len(src)
    i = 0

    def blank(a, b):
        for k in range(a, min(b, n)):
            if out[k] != "\n":
                out[k] = " "

    def skip_plain_string(j):   # src[j] == '"'  → คืนตำแหน่งหลัง " ปิด
        j += 1
        while j < n and src[j] != '"' and src[j] != "\n":
            j += 2 if src[j] == "\\" else 1
        return j + 1

    def skip_char(j):
        j += 1
        while j < n and src[j] != "'" and src[j] != "\n":
            j += 2 if src[j] == "\\" else 1
        return j + 1

    while i < n:
        c = src[i]
        if src.startswith("//", i):
            j = src.find("\n", i)
            j = n if j < 0 else j
            blank(i, j); i = j; continue
        if src.startswith("/*", i):
            j = src.find("*/", i + 2)
            j = n if j < 0 else j + 2
            blank(i, j); i = j; continue
        # คำนำหน้าสตริง: $ @ $@ @$ (และ raw """)
        m = re.match(r'(\$@|@\$|\$|@)?("""|")', src[i:i + 5])
        if m and (i == 0 or not (src[i - 1].isalnum() or src[i - 1] == "_")):
            prefix, q = m.group(1) or "", m.group(2)
            start = i
            j = i + len(prefix)
            if q == '"""':
                k = src.find('"""', j + 3)
                k = n if k < 0 else k + 3
                blank(start, k); i = k; continue
            verbatim = "@" in prefix
            interp = "$" in prefix
            j += 1
            depth = 0
            while j < n:
                ch = src[j]
                if interp and ch == "{":
                    if src.startswith("{{", j) and depth == 0:
                        j += 2; continue
                    depth += 1; j += 1; continue
                if interp and ch == "}" and depth > 0:
                    depth -= 1; j += 1; continue
                if depth > 0 and ch == '"':
                    j = skip_plain_string(j); continue
                if depth > 0 and ch == "'":
                    j = skip_char(j); continue
                if not verbatim and ch == "\\":
                    j += 2; continue
                if ch == '"':
                    if verbatim and src.startswith('""', j):
                        j += 2; continue
                    j += 1; break
                if ch == "\n" and not verbatim:
                    break
                j += 1
            blank(start, j); i = j; continue
        if c == "'":
            j = skip_char(i)
            blank(i, j); i = j; continue
        i += 1
    return "".join(out)


DECL_LINE = re.compile(r"^[ \t]*(?:\[[^\]]*\][ \t]*)*(?:public|private|protected|internal)\b", re.M)
DECL_NAME = re.compile(r"[\w>\]\?)]\s+([A-Za-z_]\w*)\s*\(")


def _match_paren(s, i):
    """s[i] == '(' → ตำแหน่งหลัง ')' ที่คู่กัน"""
    depth = 0
    while i < len(s):
        if s[i] == "(":
            depth += 1
        elif s[i] == ")":
            depth -= 1
            if depth == 0:
                return i + 1
        i += 1
    return len(s)


def methods(stripped):
    """คืน list ของ (name, body_start, body_end) — body = ช่วงหลัง signature ถึงปิดเมธอด"""
    res = []
    for dm in DECL_LINE.finditer(stripped):
        line_end = stripped.find("\n", dm.start())
        line_end = len(stripped) if line_end < 0 else line_end
        nm = DECL_NAME.search(stripped, dm.start(), line_end)
        if not nm:
            continue
        p = _match_paren(stripped, nm.end() - 1)
        rest = stripped[p:]
        ws = len(rest) - len(rest.lstrip())
        k = p + ws
        # ข้าม where-clause / base(...) ของ constructor
        while k < len(stripped) and stripped[k] not in "{;=":
            k += 1
        if k >= len(stripped):
            continue
        if stripped[k] == "{":
            depth = 0
            j = k
            while j < len(stripped):
                if stripped[j] == "{":
                    depth += 1
                elif stripped[j] == "}":
                    depth -= 1
                    if depth == 0:
                        break
                j += 1
            res.append((nm.group(1), k, j + 1))
        elif stripped.startswith("=>", k):
            depth = 0
            j = k + 2
            while j < len(stripped):
                ch = stripped[j]
                if ch in "({[":
                    depth += 1
                elif ch in ")}]":
                    depth -= 1
                elif ch == ";" and depth == 0:
                    break
                j += 1
            res.append((nm.group(1), k, j + 1))
    return res


def find_method(stripped, name):
    for nm, a, b in methods(stripped):
        if nm == name:
            return a, b
    return None


# ─────────────────────────── ตัวตรวจ ───────────────────────────

def _human(rx):
    """regex ของชื่อด่าน → ชื่อที่คนอ่าน (\\.DenyScanAsync\\s*\\( → .DenyScanAsync()"""
    return re.sub(r"\\s\*", "", rx).replace("\\.", ".").replace("\\(", "(").replace("\\b", "") + ")"


def check_target(text, name, gate_re, use_re, sinks):
    """คืนข้อความปัญหา (str) หรือ None"""
    s = strip_code(text)
    span = find_method(s, name)
    if span is None:
        return f"ไม่พบเมธอด {name} — เปลี่ยนชื่อแล้วให้แก้ TARGETS ด้วย (ห้ามเขียวเงียบ)"
    body = s[span[0]:span[1]]
    assign = re.search(r"(?:\bvar|[\w<>\?\[\],]+)\s+(\w+)\s*=\s*await\s+[\w\.\s]*?" + gate_re, body)
    if not assign:
        if re.search(gate_re, body):
            return f"{name}: เรียกด่านแต่ไม่ได้ await/เก็บผลไว้ตัดสิน"
        return f"{name}: ไม่เรียกด่านในตัวเมธอด ({_human(gate_re)})"
    var = assign.group(1)
    after = body[assign.end():]
    if not re.search(use_re.format(v=re.escape(var)), after):
        return f"{name}: เรียกด่านแล้วไม่ได้ใช้ผล `{var}` ตัดสิน (เรียกแล้วทิ้ง = ไม่มีด่าน)"
    first_sink = min((m.start() for r in sinks for m in [re.search(r, body)] if m), default=None)
    if first_sink is not None and first_sink < assign.start():
        return f"{name}: แตะไฟล์/ข้อมูลก่อนเรียกด่าน (sink ที่ตำแหน่งก่อนด่าน)"
    return None


def check_delegate(text, name, required):
    s = strip_code(text)
    span = find_method(s, name)
    if span is None:
        return f"ไม่พบเมธอด {name} (ด่านชั้นใน) — เปลี่ยนชื่อแล้วให้แก้ DELEGATES"
    body = s[span[0]:span[1]]
    miss = [r for r in required if not re.search(r, body)]
    if miss:
        return f"{name}: ไม่เรียก {', '.join(miss)} ในตัวเมธอด"
    return None


def attachment_touchers(text):
    """เมธอดในไฟล์ controller ที่แตะไฟล์แนบ (service ไฟล์แนบ / DbSet FileAttachments)"""
    s = strip_code(text)
    fields = set(re.findall(r"\bIFileAttachmentService\s+(\w+)\s*[;,=)]", s))
    pats = [r"\.FileAttachments\b"]
    for f in fields:
        pats.append(r"\b" + re.escape(f) + r"\s*\.\s*(UploadAsync|UploadBytesAsync|GetByEntityAsync|GetByIdAsync|DeleteAsync)\s*\(")
    out = []
    for nm, a, b in methods(s):
        if any(re.search(p, s[a:b]) for p in pats):
            out.append(nm)
    return out


def discover(root=ROOT):
    found = set()
    base = os.path.join(root, "Accounting", "Controllers")
    for d, dirs, files in os.walk(base):
        dirs[:] = [x for x in dirs if x not in SKIP_DIRS]
        for f in files:
            if not f.endswith(".cs"):
                continue
            p = os.path.join(d, f)
            rel = os.path.relpath(p, root).replace(os.sep, "/")
            try:
                txt = open(p, encoding="utf-8").read()
            except (OSError, UnicodeDecodeError):
                continue
            for nm in attachment_touchers(txt):
                found.add((rel, nm))
    return found


def read(rel, root=ROOT):
    with open(os.path.join(root, rel), encoding="utf-8") as f:
        return f.read()


def run_checks(root=ROOT, overrides=None):
    """overrides: {rel: text} แทนเนื้อไฟล์ (ใช้ใน negative test)"""
    overrides = overrides or {}
    problems = []

    def text_of(rel):
        return overrides[rel] if rel in overrides else read(rel, root)

    for rel, name, gate, use, sinks in TARGETS:
        try:
            msg = check_target(text_of(rel), name, gate, use, sinks)
        except FileNotFoundError:
            msg = f"ไม่พบไฟล์ {rel}"
        if msg:
            problems.append(f"{rel}: {msg}")
    for rel, name, req in DELEGATES:
        try:
            msg = check_delegate(text_of(rel), name, req)
        except FileNotFoundError:
            msg = f"ไม่พบไฟล์ {rel}"
        if msg:
            problems.append(f"{rel}: {msg}")
    listed = {(r, n) for r, n, *_ in TARGETS} | set(EXEMPT)
    for rel, nm in sorted(discover(root)):
        if (rel, nm) not in listed:
            problems.append(f"{rel}: {nm} แตะไฟล์แนบแต่ไม่อยู่ใน TARGETS/EXEMPT — "
                            "ต้องเรียก IAttachmentAccessGate แล้วเพิ่มเข้า TARGETS (หรือ EXEMPT พร้อมเหตุผล)")
    return problems


# ─────────────────────────── negative test ───────────────────────────

def _remove_gate_statement(text, name, gate_re):
    """ถอดประโยคที่เรียกด่านออกจากเมธอด (ทั้งประโยคจนถึง ';')"""
    s = strip_code(text)
    span = find_method(s, name)
    if span is None:
        return None
    m = re.search(gate_re, s[span[0]:span[1]])
    if not m:
        return None
    pos = span[0] + m.start()
    st = s.rfind(";", span[0], pos)
    st = max(st, s.rfind("{", span[0], pos)) + 1
    en = s.find(";", pos) + 1
    return text[:st] + "\n" + text[en:]


SYNTH_OK = '''
public class FakeController : ControllerBase
{
    private readonly IFileAttachmentService _files;
    [HttpGet("{id}")]
    public async Task<IActionResult> Download(Guid companyId, Guid id, [FromServices] IAttachmentAccessGate gate)
    {
        var s = $"{(id == Guid.Empty ? "a{" : "b}")}";   // สตริงที่มีปีกกา/อัญประกาศซ้อน
        var deny = await gate.DenyAttachmentAsync(companyId, uid, "X", id, AttachmentAccess.Read, "ดู");
        if (deny is { } d) return StatusCode(d.Status, d.Message);
        var att = await _files.GetByIdAsync(companyId, id);
        return PhysicalFile(att.StoragePath, "x");
    }
}
'''


def self_test(root=ROOT):
    ok = True

    def expect(cond, msg):
        nonlocal ok
        if not cond:
            print("❌ self-test: " + msg)
            ok = False

    # 1. ถอดการเรียกด่านออกจากไฟล์จริงทีละ target ⇒ ต้องฟ้อง target นั้น
    for rel, name, gate, use, sinks in TARGETS:
        mutated = _remove_gate_statement(read(rel, root), name, gate)
        expect(mutated is not None, f"หาประโยคด่านใน {rel}:{name} ไม่เจอ (สร้าง mutation ไม่ได้)")
        if mutated is None:
            continue
        msg = check_target(mutated, name, gate, use, sinks)
        expect(msg is not None, f"ถอดด่านจาก {rel}:{name} แล้วไม่ฟ้อง")

    # 2. ชื่อด่านอยู่ในคอมเมนต์/สตริง ≠ เรียกจริง
    gate = r"\.DenyAttachmentAsync\s*\("
    base = SYNTH_OK
    expect(check_target(base, "Download", gate, DENY_USE, [r"\bPhysicalFile\s*\("]) is None,
           "ไฟล์ที่ถูกต้อง (มีสตริงปีกกาซ้อน) ถูกฟ้องผิด")
    in_comment = base.replace("        var deny = await gate.DenyAttachmentAsync",
                              "        var deny = (AttachmentDenial?)null; // await gate.DenyAttachmentAsync")
    expect(check_target(in_comment, "Download", gate, DENY_USE, [r"\bPhysicalFile\s*\("]) is not None,
           "ด่านที่อยู่ในคอมเมนต์ถูกนับว่าเรียกจริง")
    in_string = base.replace('var deny = await gate.DenyAttachmentAsync(companyId, uid, "X", id, AttachmentAccess.Read, "ดู");',
                             'var deny = "await gate.DenyAttachmentAsync(";')
    expect(check_target(in_string, "Download", gate, DENY_USE, [r"\bPhysicalFile\s*\("]) is not None,
           "ด่านที่อยู่ในสตริงถูกนับว่าเรียกจริง")
    # 3. เรียกแล้วทิ้งผล
    unused = base.replace("        if (deny is { } d) return StatusCode(d.Status, d.Message);\n", "")
    expect(check_target(unused, "Download", gate, DENY_USE, [r"\bPhysicalFile\s*\("]) is not None,
           "เรียกด่านแล้วไม่ใช้ผล ไม่ถูกฟ้อง")
    # 4. แตะไฟล์ก่อนเรียกด่าน
    late = base.replace("        var att = await _files.GetByIdAsync(companyId, id);\n", "").replace(
        "        var s = $", "        var att0 = PhysicalFile(\"p\", \"x\");\n        var s = $")
    expect(check_target(late, "Download", gate, DENY_USE, [r"\bPhysicalFile\s*\("]) is not None,
           "แตะไฟล์ก่อนเรียกด่าน ไม่ถูกฟ้อง")
    # 5. ทางเข้าใหม่ที่แตะไฟล์แนบต้องถูกค้นเจอ · เมธอดในคอมเมนต์ไม่นับ
    touch = attachment_touchers(SYNTH_OK + "\n// public Task X() { _files.GetByIdAsync(a, b); }\n")
    expect(touch == ["Download"], f"ค้นทางเข้าที่แตะไฟล์แนบผิด: {touch}")
    # 6. wrapper ที่ไม่ส่งต่อไป service ต้องถูกฟ้อง
    rel, name, req = DELEGATES[0]
    broken = read(rel, root).replace("=> _gate.DenyAttachmentAsync(", "=> NotTheGate(")
    expect(check_delegate(broken, name, req) is not None, "wrapper ที่ไม่ส่งต่อไป IAttachmentAccessGate ไม่ถูกฟ้อง")
    return ok


def main():
    if "--self-test" in sys.argv:
        ok = self_test()
        print("✅ self-test ผ่าน (ถอดด่านจากไฟล์จริงทุก target=ฟ้อง · คอมเมนต์/สตริง/ทิ้งผล/ด่านมาทีหลัง=ฟ้อง · "
              "ทางเข้าใหม่ถูกค้นเจอ)" if ok else "❌ self-test ล้ม")
        return 0 if ok else 1
    problems = run_checks()
    for p in problems:
        print("❌ " + p)
    ok = self_test()
    if problems or not ok:
        print(f"\n❌ attachment_gate_check: พบ {len(problems)} จุด"
              + ("" if ok else " + negative test ล้ม (checker จับบั๊กที่ใส่กลับไม่ได้ = ไม่มีด่าน)"))
        return 1
    print(f"✅ ทุกทางเข้าไฟล์แนบเรียกด่านกลางจริง ({len(TARGETS)} action · {len(DELEGATES)} ด่านชั้นใน · "
          f"ยกเว้นพร้อมเหตุผล {len(EXEMPT)}) · negative test ผ่าน")
    return 0


if __name__ == "__main__":
    sys.exit(main())
