#!/usr/bin/env python3
# -*- coding: utf-8 -*-
"""จุดประทับ `Status = DocumentStatus.Approved` นอก DocumentService ที่ไม่เรียก "ผลข้างเคียงหลังออกเอกสาร"
ในเมธอดเดียวกัน — ratchet กับ baseline

═══ ที่มา (รอบ 193 · ผลตรวจ S-02 `erp-review/2026-09-24/audit-settings.md`) ═══
ค่าตั้งที่มีผล "ตอนออกเอกสาร" (e-Tax อัตโนมัติ `EtaxEnabled`/`EtaxAutoSign`) เคยอยู่ใน
`ApproveDocumentAsync` เท่านั้น แต่ POS (ใบกำกับเต็มรูป) และ Integration (TIV/CN/DN) สร้างเอกสารเป็น
`Approved` ตรง ๆ ⇒ บริษัทที่เปิด e-Tax อัตโนมัติ ได้ e-Tax เฉพาะใบจากเว็บ ใบจาก POS/API ไม่เคยถูกออก
และไม่มีอะไรเตือน ⇒ ไม่ถูกนำส่งภายในวันที่ 15. แก้แล้วด้วย `IIssuedDocumentHooks.RunAsync` จุดเดียว —
checker นี้กันไม่ให้ทางเข้าที่ **สาม** เกิดโดยลืมเรียก (ราก R5 "ทางเข้าอื่นไม่เดินด่าน")

═══ กติกา ═══
  * จับ **การกำหนดค่า** `Status = … DocumentStatus.Approved …` (รวม object initializer และ ternary
    `Status = auto ? DocumentStatus.Approved : …`) — ไม่จับ `==`/`!=`/การอ่าน/คอมเมนต์
  * ไฟล์เจ้าของ (`Services/Implementations/DocumentService*.cs`) ไม่ตรวจ — `ApproveDocumentAsync` เรียก hook เอง
    (จุดประทับอื่นในไฟล์นั้น เช่น CN คืนมัดจำ อยู่ในความรับผิดชอบของเจ้าของไฟล์ — ดูรายงาน r193-V)
  * ผ่านเมื่อ **เมธอดเดียวกัน** มีการเรียก `…IssuedHooks.RunAsync(` / `issuedHooks.RunAsync(`
  * จุดที่ตั้งใจยกเว้นอยู่ใน `tools/approved_status_writer_baseline.txt` (คีย์ `ไฟล์:เมธอด = จำนวน` ทนเลขบรรทัดเลื่อน)
    — **ห้ามเพิ่มแถวเพื่อให้เขียว** ตัดออกได้อย่างเดียว (ratchet ทางเดียว)

ใช้: python3 tools/approved_status_writer_check.py [--all] [--self-test] [--write-baseline]

═══ negative test (F2 ข้อ 6) ═══
`--self-test`: ไฟล์ที่ประทับแล้วไม่เรียก hook ต้องถูกฟ้อง · ไฟล์ที่เรียก hook ในเมธอดเดียวกันไม่ถูกฟ้อง ·
การเปรียบเทียบ `==` และคอมเมนต์ไม่ถูกฟ้อง · ไฟล์เจ้าของไม่ถูกฟ้อง · hook ใน**เมธอดอื่น**ของไฟล์เดียวกันไม่นับ
"""
import os
import re
import sys
import tempfile

ROOT = os.path.dirname(os.path.dirname(os.path.abspath(__file__)))
SRC = os.path.join(ROOT, "Accounting")
BASELINE = os.path.join(ROOT, "tools", "approved_status_writer_baseline.txt")

OWNER_RE = re.compile(r"^Services/Implementations/DocumentService(\.[\w]+)?\.cs$")
STAMP_RE = re.compile(r"\bStatus\s*=(?!=)[^;,\n]*?\bDocumentStatus\s*\.\s*Approved\b")
HOOK_RE = re.compile(r"[iI]ssuedHooks\s*[!]?\s*\.\s*RunAsync\s*\(")
METHOD_RE = re.compile(
    r"^[ \t]*(?:\[[^\]]*\][ \t]*)*(?:(?:public|private|internal|protected|static|async|override|virtual|sealed|new|partial)\s+)+"
    r"[\w<>\[\]?,.\s()]+?\s+(\w+)\s*(?:<[^>()]*>)?\s*\([^;{]*?\)\s*(?:where[^{]*)?\{",
    re.M | re.S)


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
        # verbatim / interpolated-verbatim
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


def methods(body: str):
    """คืน [(ชื่อ, start, end)] ของเมธอดทุกตัว — end = ปีกกาปิดที่คู่กัน"""
    res = []
    for m in METHOD_RE.finditer(body):
        name = m.group(1)
        if name in ("if", "for", "foreach", "while", "switch", "catch", "using", "lock", "return"):
            continue
        start = m.end() - 1  # ตำแหน่ง '{'
        depth, j = 0, start
        while j < len(body):
            if body[j] == "{":
                depth += 1
            elif body[j] == "}":
                depth -= 1
                if depth == 0:
                    break
            j += 1
        res.append((name, start, j))
    return res


def scan():
    hits = {}  # key -> [บรรทัด]
    for dirpath, dirnames, filenames in os.walk(SRC):
        dirnames[:] = [d for d in dirnames if d not in ("bin", "obj", "node_modules", "wwwroot")]
        for fn in filenames:
            if not fn.endswith(".cs"):
                continue
            full = os.path.join(dirpath, fn)
            rel = os.path.relpath(full, SRC).replace(os.sep, "/")
            if OWNER_RE.match(rel):
                continue
            try:
                raw = open(full, encoding="utf-8", errors="replace").read()
            except OSError:
                continue
            body = strip(raw)
            stamps = list(STAMP_RE.finditer(body))
            if not stamps:
                continue
            ms = methods(body)
            for s in stamps:
                encl = [mm for mm in ms if mm[1] <= s.start() <= mm[2]]
                # เมธอดในสุด (local function/lambda ซ้อน) = ตัวที่ start มากสุด
                encl.sort(key=lambda mm: mm[1])
                name, a, b = encl[-1] if encl else ("<นอกเมธอด>", 0, len(body))
                # hook ต้องอยู่ในเมธอดนอกสุดที่ครอบจุดประทับ (เมธอดจริง ไม่ใช่ lambda ย่อย)
                outer = encl[0] if encl else (name, a, b)
                if HOOK_RE.search(body, outer[1], outer[2] + 1):
                    continue
                key = f"{rel}:{outer[0]}"
                hits.setdefault(key, []).append(body.count("\n", 0, s.start()) + 1)
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
        f.write("# จุดประทับ DocumentStatus.Approved นอก DocumentService ที่ตั้งใจ **ไม่**เรียก IssuedDocumentHooks\n")
        f.write("# ห้ามเพิ่มแถวเพื่อให้ checker เขียว — ตัดออกได้อย่างเดียว · ทุกแถวต้องมีเหตุผลหลัง #\n")
        for k, lines in sorted(hits.items()):
            f.write(f"{k} = {len(lines)}  # TODO เหตุผล\n")


def self_test():
    global SRC
    ok = True
    with tempfile.TemporaryDirectory() as tmp:
        d = os.path.join(tmp, "Services", "Implementations")
        os.makedirs(d)
        open(os.path.join(d, "BadService.cs"), "w", encoding="utf-8").write(
            "class Bad {\n"
            "  public async Task Make(Guid c) {\n"
            "    var doc = new Document { Status = DocumentStatus.Approved, Notes = \"{ }\" };\n"
            "    await _db.SaveChangesAsync();\n"
            "  }\n"
            "  public async Task Other(Guid c, Document doc) { await _issuedHooks.RunAsync(c, doc); }\n"
            "}\n")
        open(os.path.join(d, "GoodService.cs"), "w", encoding="utf-8").write(
            "class Good {\n"
            "  public async Task Make(Guid c, bool auto) {\n"
            "    var doc = new Document { Status = auto ? DocumentStatus.Approved : DocumentStatus.Draft };\n"
            "    if (auto) { await _issuedHooks.RunAsync(c, doc); }\n"
            "  }\n"
            "}\n")
        open(os.path.join(d, "ReaderService.cs"), "w", encoding="utf-8").write(
            "class Reader {\n"
            "  // doc.Status = DocumentStatus.Approved;  <- คอมเมนต์ ไม่ใช่การประทับ\n"
            "  public bool M(Document d) => d.Status == DocumentStatus.Approved;\n"
            "  public bool N(Document d) { var ok = d.Status != DocumentStatus.Approved; return ok; }\n"
            "}\n")
        open(os.path.join(d, "DocumentService.cs"), "w", encoding="utf-8").write(
            "class DocumentService {\n"
            "  public void Approve(Document d) { d.Status = DocumentStatus.Approved; }\n"
            "}\n")
        saved, SRC = SRC, tmp
        try:
            keys = set(scan().keys())
        finally:
            SRC = saved
    if "Services/Implementations/BadService.cs:Make" not in keys:
        print("❌ self-test: ไม่จับการประทับที่ไม่มี hook (hook ในเมธอดอื่นต้องไม่นับ)", keys); ok = False
    if any("GoodService" in k for k in keys):
        print("❌ self-test: ฟ้องเมธอดที่เรียก hook แล้ว (false positive)"); ok = False
    if any("ReaderService" in k for k in keys):
        print("❌ self-test: ฟ้องการอ่าน/คอมเมนต์ (false positive)"); ok = False
    if any("DocumentService" in k for k in keys):
        print("❌ self-test: ฟ้องไฟล์เจ้าของ (false positive)"); ok = False
    if ok:
        print("✅ self-test ผ่าน — จับจุดที่ลืม hook · ไม่ฟ้องจุดที่เรียกแล้ว/การอ่าน/คอมเมนต์/เจ้าของ")
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
    print(f"จุดประทับ Approved นอก DocumentService ที่ไม่เรียก IssuedDocumentHooks: "
          f"{sum(len(v) for v in hits.values())} (baseline {sum(base.values())})")
    if gone:
        print(f"ℹ️  baseline ลดลง {len(gone)} รายการ — ตัดแถวออกจาก baseline ในคอมมิตเดียวกัน:")
        for g in gone:
            print(f"   - {g}")
    if new:
        print(f"❌ จุดประทับ Approved **ใหม่** ที่ไม่เรียก IssuedDocumentHooks {len(new)} รายการ:")
        for k, lines in sorted(new.items()):
            print(f"   {k} (บรรทัด {', '.join(map(str, lines))}) — baseline {base.get(k, 0)} → ปัจจุบัน {len(lines)}")
        print("   → เรียก `_issuedHooks.RunAsync(companyId, doc)` หลัง commit ในเมธอดเดียวกัน (e-Tax อัตโนมัติ ฯลฯ)")
        print("   → หรืออนุมัติผ่าน ApproveDocumentAsync · ห้ามเติม baseline เพื่อให้ผ่าน")
        return 1
    print("✅ ทุกจุดประทับ Approved นอกเจ้าของเรียกผลข้างเคียงหลังออกเอกสาร (หรืออยู่ใน baseline พร้อมเหตุผล)")
    return 0


if __name__ == "__main__":
    sys.exit(main())
