#!/usr/bin/env python3
# -*- coding: utf-8 -*-
"""แถว audit ที่เขียนด้วย `AuditLogs.Add(...)` ตรง ๆ แทน `AddChainedAuditLog(...)` — ratchet ต่อไฟล์

═══ ที่มา (รอบ 201 ทีม PL · A-PL4 · r193-W "9 จุด" → รอบ 201 นับได้ 19 + ที่พัก) ═══
ก่อนรอบ 201 `AuditLogs.Add(row)` ตรง ๆ ข้ามการประทับ hash ⇒ `RowHash = null` อยู่นอก chain และตัวตรวจมองไม่เห็น
(กฎเหล็ก #2 M "audit log append-only + hash chain") · รอบ 201 (A-PL1) ย้ายการประทับไปที่ `SaveChanges` ภายใต้ล็อกของบริษัท
⇒ แถวที่ Add ตรงถูกประทับด้วยเส้นเดียวกันแล้ว (กันพลาด) **แต่** การ Add ตรงยังซ่อนเจตนา: คนอ่านโค้ดแยกไม่ออกว่าแถวนี้ "ตั้งใจเข้า chain"
และถ้ามีคนเพิ่ม DbContext ตัวที่สอง/เส้นเขียน raw ในอนาคต แถวพวกนี้คือแถวแรกที่หลุด ⇒ ทางเข้าเดียว = `AddChainedAuditLog`

กติกา ratchet (แบบ `terminal_status_writer_check` / `dead_helper_check`):
  * นับต่อไฟล์ใน `Accounting/**/*.cs` (นอกคอมเมนต์/สตริง) · ยกเว้น `Data/AccountingDbContext.cs` (ตัว `AddChainedAuditLog` เอง)
  * baseline `tools/audit_direct_add_baseline.txt` = จุดของทีมอื่นที่ยังไม่ย้าย (DocumentService ช่วง DV · Payroll · OCR · Auth ·
    LINE · ที่พัก) — **ห้ามเพิ่มแถว/ห้ามเพิ่มจำนวน** · ล้มเมื่อไฟล์ใดมีจุดมากกว่า baseline · จุดที่ลดลงให้ลด baseline ตาม
  * `--write-baseline` เขียนทับด้วยสภาพปัจจุบัน (ใช้เฉพาะตอนลดจำนวน)
  * `--self-test` / ทุกครั้งที่รันปกติ: negative test ในตัว (ฉีดการ Add ตรงลงไฟล์จริงที่ baseline = 0 แล้วต้องฟ้อง ·
    คอมเมนต์/สตริง/AddChainedAuditLog ต้องไม่ฟ้อง)

ใช้: python3 tools/audit_direct_add_check.py [--write-baseline] [--self-test]
"""
import os
import re
import sys

ROOT = os.path.dirname(os.path.dirname(os.path.abspath(__file__)))
SRC = os.path.join(ROOT, "Accounting")
BASELINE = os.path.join(ROOT, "tools", "audit_direct_add_baseline.txt")
EXEMPT = {"Data/AccountingDbContext.cs"}

DIRECT = re.compile(r"(?:\bAuditLogs|\bSet\s*<\s*(?:Models\.Entities\.)?AuditLog\s*>\s*\(\s*\))\s*\.\s*(?:Add|AddRange|AddAsync|AddRangeAsync)\s*\(")


def strip_code(text: str) -> str:
    """ตัดคอมเมนต์ // /* */ และเนื้อสตริง (ธรรมดา/verbatim/interpolated แบบหยาบ) — คงจำนวนบรรทัด"""
    out, i, n = [], 0, len(text)
    while i < n:
        c = text[i]
        if text.startswith("//", i):
            j = text.find("\n", i)
            j = n if j < 0 else j
            i = j
            continue
        if text.startswith("/*", i):
            j = text.find("*/", i + 2)
            j = n if j < 0 else j + 2
            out.append("\n" * text.count("\n", i, j))
            i = j
            continue
        if text.startswith('"""', i):
            j = text.find('"""', i + 3)
            j = n if j < 0 else j + 3
            out.append('""' + "\n" * text.count("\n", i, j))
            i = j
            continue
        if c == '"' or (c in "@$" and i + 1 < n and text[i + 1] in '"@$'):
            k = i
            verbatim = False
            while k < n and text[k] in "@$":
                verbatim = verbatim or text[k] == "@"
                k += 1
            if k >= n or text[k] != '"':
                out.append(c)
                i += 1
                continue
            k += 1
            while k < n:
                if verbatim and text.startswith('""', k):
                    k += 2
                    continue
                if not verbatim and text[k] == "\\":
                    k += 2
                    continue
                if text[k] == '"':
                    k += 1
                    break
                k += 1
            out.append('""' + "\n" * text.count("\n", i, k))
            i = k
            continue
        out.append(c)
        i += 1
    return "".join(out)


def count_direct(text: str) -> list:
    code = strip_code(text)
    return [code.count("\n", 0, m.start()) + 1 for m in DIRECT.finditer(code)]


def scan() -> dict:
    found = {}
    for dirpath, _, files in os.walk(SRC):
        for fn in files:
            if not fn.endswith(".cs"):
                continue
            path = os.path.join(dirpath, fn)
            rel = os.path.relpath(path, SRC).replace(os.sep, "/")
            if rel in EXEMPT or "/bin/" in "/" + rel or "/obj/" in "/" + rel:
                continue
            with open(path, encoding="utf-8") as f:
                lines = count_direct(f.read())
            if lines:
                found[rel] = lines
    return found


def load_baseline() -> dict:
    base = {}
    if not os.path.exists(BASELINE):
        return base
    with open(BASELINE, encoding="utf-8") as f:
        for raw in f:
            line = raw.strip()
            if not line or line.startswith("#"):
                continue
            rel, cnt = line.rsplit("\t", 1)
            base[rel] = int(cnt)
    return base


def judge(found: dict, base: dict):
    errs, shrink = [], []
    for rel, lines in sorted(found.items()):
        allowed = base.get(rel, 0)
        if len(lines) > allowed:
            errs.append(f"Accounting/{rel}: AuditLogs.Add ตรง {len(lines)} จุด (baseline {allowed}) บรรทัด {lines} — "
                        "ใช้ _db.AddChainedAuditLog(row) (A-PL4 · ทางเข้าเดียวของแถว audit)")
        elif len(lines) < allowed:
            shrink.append(f"{rel}: เหลือ {len(lines)} จาก baseline {allowed} — ลด baseline (ratchet เดินทางเดียว)")
    for rel, allowed in sorted(base.items()):
        if rel not in found and allowed > 0:
            shrink.append(f"{rel}: ไม่เหลือจุด Add ตรงแล้ว — ลบแถวออกจาก baseline")
    return errs, shrink


def self_test(found: dict, base: dict) -> list:
    fails = []
    samples_bad = ["_db.AuditLogs.Add(new AuditLog { });", "db.AuditLogs.AddRange(rows);",
                   "await _db.AuditLogs.AddAsync(row);", "_db.Set<AuditLog>().Add(row);"]
    samples_ok = ["// _db.AuditLogs.Add(row);", 'var s = "_db.AuditLogs.Add(";', "_db.AddChainedAuditLog(row);",
                  "/* AuditLogs.Add( */ var x = 1;", "var n = await _db.AuditLogs.CountAsync();",
                  'var v = @"AuditLogs.Add(""x"")";']
    for s in samples_bad:
        if not count_direct("class X { void F() { " + s + " } }"):
            fails.append(f"self-test: `{s}` ไม่ถูกนับ")
    for s in samples_ok:
        if count_direct("class X { void F() { " + s + " } }"):
            fails.append(f"self-test: `{s}` ถูกนับผิด (ฟ้องผิด = checker พัง · F2 ข้อ 6)")
    # ฉีดลงไฟล์จริงที่ baseline = 0 แล้วต้องฟ้อง
    target = "Controllers/AdminController.cs"
    path = os.path.join(SRC, target)
    if os.path.exists(path) and base.get(target, 0) == 0:
        with open(path, encoding="utf-8") as f:
            text = f.read()
        injected = dict(found)
        injected[target] = count_direct(text + "\nclass __Inj { void F() { _db.AuditLogs.Add(new AuditLog()); } }\n")
        errs, _ = judge(injected, base)
        if not any(target in e for e in errs):
            fails.append(f"self-test: ฉีด AuditLogs.Add ลง {target} แล้วไม่ฟ้อง")
    else:
        fails.append(f"self-test: ไฟล์เป้า {target} หายหรือมีใน baseline — เปลี่ยนเป้าของ negative test")
    return fails


# ── PL-X7 (รอบ 201 · คำตัดสินข้อ 104): แถว audit ในธุรกรรมของผู้เรียกถูก "เก็บ" ผูกกับ TransactionId แล้วประทับตอน commit ⇒ execution strategy ที่
#    retry (EnableRetryOnFailure) จะเล่น SaveChanges ซ้ำในธุรกรรมใหม่โดยแถวเดิมผูกธุรกรรมเก่า (ทิ้ง) — เปิดได้เฉพาะเมื่อห่อทุกธุรกรรมด้วย
#    Database.CreateExecutionStrategy().ExecuteAsync(...) ซึ่งเรพนี้ยังไม่ทำ ⇒ ห้ามเปิดเงียบ ๆ
RETRY_RX = re.compile(r"\bEnableRetryOnFailure\s*\(")


def retry_errors(texts: dict) -> list:
    return [f"Accounting/{rel}: เปิด EnableRetryOnFailure — ต้องห่อธุรกรรมด้วย execution strategy ก่อน (แถว audit ประทับตอน commit ผูกกับธุรกรรม · PL-X7)"
            for rel, t in texts.items() if RETRY_RX.search(strip_code(t))]


def retry_self_test() -> list:
    fails = []
    if not retry_errors({"Program.cs": "x.UseNpgsql(cs, o => o.EnableRetryOnFailure(3));"}):
        fails.append("self-test PL-X7: EnableRetryOnFailure ไม่ถูกฟ้อง")
    if retry_errors({"Program.cs": "// o.EnableRetryOnFailure(3)\nvar s = \"EnableRetryOnFailure(\";"}):
        fails.append("self-test PL-X7: คอมเมนต์/สตริงถูกฟ้องผิด")
    return fails


# ── ฝ่ายค้านรอบสาม P1-1 (รอบ 201 · ทีม PL): ธุรกรรม Serializable/RepeatableRead/Snapshot จับ snapshot ตั้งแต่คำสั่งแรก ⇒ ตัวประทับ audit ตอน commit
#    อ่านปลาย chain จาก snapshot เก่าแม้ได้ล็อกแล้ว ⇒ PrevHash ซ้ำกับธุรกรรมที่ commit ระหว่างนั้น = chain แตกกิ่ง · interceptor ล้มดังตอน commit
#    แต่ checker นี้กันตั้งแต่เขียนโค้ด: ใช้ ReadCommitted + `SELECT … FOR UPDATE` บนแถวที่ต้องกันแข่ง · baseline = 0 จุด (สองจุดเดิมแก้แล้ว)
ISOLATION_RX = re.compile(r"\bIsolationLevel\s*\.\s*(Serializable|RepeatableRead|Snapshot)\b")


def isolation_errors(texts: dict) -> list:
    out = []
    for rel, t in texts.items():
        code = strip_code(t)
        for m in ISOLATION_RX.finditer(code):
            out.append(f"Accounting/{rel}:{code.count(chr(10), 0, m.start()) + 1}: IsolationLevel.{m.group(1)} — ตัวประทับ audit ตอน commit อ่านปลาย chain "
                       "จาก snapshot เก่า (chain แตกกิ่ง · ฝ่ายค้านรอบสาม P1-1) ⇒ ใช้ ReadCommitted + SELECT … FOR UPDATE บนแถวที่ต้องกันแข่ง")
    return out


def isolation_self_test() -> list:
    fails = []
    if not isolation_errors({"X.cs": "await db.Database.BeginTransactionAsync(System.Data.IsolationLevel.Serializable);"}):
        fails.append("self-test P1-1: IsolationLevel.Serializable ไม่ถูกฟ้อง")
    if not isolation_errors({"X.cs": "BeginTransaction(IsolationLevel . RepeatableRead)"}):
        fails.append("self-test P1-1: RepeatableRead ไม่ถูกฟ้อง")
    if isolation_errors({"X.cs": "// IsolationLevel.Serializable\nvar s = \"IsolationLevel.Serializable\"; BeginTransaction(IsolationLevel.ReadCommitted);"}):
        fails.append("self-test P1-1: คอมเมนต์/สตริง/ReadCommitted ถูกฟ้องผิด")
    return fails


def all_sources() -> dict:
    texts = {}
    for dirpath, _, files in os.walk(SRC):
        for fn in files:
            if fn.endswith(".cs"):
                path = os.path.join(dirpath, fn)
                rel = os.path.relpath(path, SRC).replace(os.sep, "/")
                if "/bin/" in "/" + rel or "/obj/" in "/" + rel:
                    continue
                texts[rel] = open(path, encoding="utf-8").read()
    return texts


def main() -> int:
    iso = isolation_errors(all_sources()) + isolation_self_test()
    for e in iso:
        print("❌ " + e)
    retry_texts = {}
    for rel in ("Program.cs", "Data/AccountingDbContext.cs"):
        path = os.path.join(SRC, rel)
        if os.path.exists(path):
            retry_texts[rel] = open(path, encoding="utf-8").read()
    retry = retry_errors(retry_texts) + retry_self_test()
    for e in retry:
        print("❌ " + e)
    found = scan()
    if "--write-baseline" in sys.argv:
        with open(BASELINE, "w", encoding="utf-8") as f:
            f.write("# audit_direct_add_check baseline — ไฟล์<TAB>จำนวนจุด AuditLogs.Add ตรง (ห้ามเพิ่ม · ลดได้อย่างเดียว)\n")
            for rel, lines in sorted(found.items()):
                f.write(f"{rel}\t{len(lines)}\n")
        print(f"เขียน baseline {len(found)} ไฟล์ · {sum(len(v) for v in found.values())} จุด")
        return 0
    base = load_baseline()
    errs, shrink = judge(found, base)
    st = self_test(found, base)
    for e in errs + st:
        print("❌ " + e)
    for s in shrink:
        print("⚠️  " + s)
    if errs or st or retry or iso:
        return 1
    print(f"audit_direct_add_check: ผ่าน — {sum(len(v) for v in found.values())} จุดใน baseline {len(found)} ไฟล์ (ห้ามเพิ่ม)")
    return 0


if __name__ == "__main__":
    sys.exit(main())
