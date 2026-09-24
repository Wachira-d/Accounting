#!/usr/bin/env python3
# -*- coding: utf-8 -*-
"""ล็อก "จุดเรียก" ของด่านเงิน/ภาษี/สต็อกใน service — ด่านที่มีแต่ไม่ถูกเรียก = ไม่มีด่าน

ที่มา (รอบ 193 · ฝ่ายค้าน M2)
----------------------------
เทสต์ของรอบ 193 ล็อกแค่ **helper** (pure) ไม่ได้ล็อกว่า service ยังเรียก helper นั้นอยู่ ⇒
ถอดการแก้ออกจาก service แล้วเทสต์ยังเขียวทั้งชุด:
  • `SsoUnpaidLeaveWageTests` ประกอบสูตรเองในไฟล์เทสต์ — `PayrollService` กลับไปใช้
    `GrossWage(proratedBaseSalary, …)` (ไม่หักลา) เทสต์ก็ไม่รู้
  • ด่าน D-01 (50 ทวิรายเดือนใช้ `EmployeeTaxIdentity.Resolve`) · `CalculatePayrollAsync` ใช้
    `PayrollRunEditPolicy.CanRecalculate` · POS "ตัดสต็อกก่อน JE" — ไม่มีอะไรฟ้องถ้าถูกถอด
เรพนี้ไม่มีเทสต์ระดับ service (ไม่มี InMemory/SQLite DbContext ใน Accounting.Tests) จึงล็อกด้วย
**รูปทรงของโค้ด** ในเมธอดที่ระบุ: ต้องมีการเรียก X · X ต้องมาก่อน Y · ห้ามประกอบสูตรเองซ้ำ (Z)

ทำไมเป็น checker ได้ (ไม่ขัด F4 ข้อ 1/3): ไม่ต้องรู้ชนิด ไม่ต้องรู้ taint · scope แคบเป็นรายเมธอด
ที่ระบุชื่อ (OWNER file) · ค้นบนโค้ดที่ตัดคอมเมนต์/สตริงแล้ว ⇒ คอมเมนต์ที่ "พูดถึง" การเรียกไม่นับ

กติกาเพิ่มใหม่: เพิ่มแถวใน RULES · negative test รันทุกครั้งที่รัน checker (ไม่ต้องจำ --self-test):
แต่ละกติกาถูกป้อนเมธอดจริงที่ "ถอดการแก้" (ลบ/สลับ/คอมเมนต์การเรียก) แล้วต้องฟ้อง
"""
import re
import sys
from pathlib import Path

ROOT = Path(__file__).resolve().parent.parent
SRC = ROOT / "Accounting"

PAYROLL = "Services/Implementations/PayrollService.cs"
POS = "Services/Implementations/PosService.Orders.cs"
BOT = "Services/Implementations/BotExchangeRateService.cs"
COMMISSION = "Services/Implementations/CommissionService.cs"

# (ไฟล์, เมธอด, must[], before[(a, b)], forbid[], เหตุผล)
RULES = [
    (PAYROLL, "CalculatePayrollAsync",
     ["LoadRecalculateLockEvidenceAsync(", "PayrollRunEditPolicy.CanRecalculate(", "SsoWageBase.ForPeriod("],
     [("PayrollRunEditPolicy.CanRecalculate(", "RemoveRange(run.Details)")],
     ["SsoWageBase.GrossWage(", "SsoWageBase.PeriodBase(", "SsoWageBase.Clamp(", "SsoWageBase.SalaryPaidThisPeriod("],
     "#35 คำนวณใหม่ต้องผ่านตัวตัดสินเดียว (พร้อมหลักฐานยื่น/นำส่ง/ปันต้นทุน) ก่อนลบแถวเดิม · "
     "ฐาน ปกส. ต้องประกอบที่ SsoWageBase.ForPeriod ตัวเดียว (ตัวที่เทสต์ D-02 เรียก)"),
    (PAYROLL, "MapToPayrollRunResponse",
     ["PayrollRunEditPolicy.CanRecalculate("], [], [],
     "ปุ่มคำนวณใหม่บนจอต้องตัดสินด้วยตัวเดียวกับด่าน"),
    (PAYROLL, "LoadRecalculateLockEvidenceAsync",
     ["ComplianceFilings", "TaxReports", "EFilingExports", "StatutoryRemittances", "EmployeeProjectTimes",
      "SsoSettledAt", "PayrollRunLockEvidence.From("], [], [],
     "หลักฐาน 'ยื่น/นำส่ง/ปันต้นทุนแล้ว' ต้องครบทุกแหล่ง"),
    (PAYROLL, "IssueMonthlyPnd1CertsAsync",
     ["EmployeeTaxIdentity.Resolve(emp.TaxId, emp.CitizenId)", "payeeTaxId == null"],
     [("EmployeeTaxIdentity.Resolve(", "payeeTaxId == null")],
     ["string.IsNullOrWhiteSpace(emp.TaxId)", "string.IsNullOrEmpty(emp.TaxId)", "emp.TaxId == null"],
     "D-01 ด่าน 50 ทวิรายเดือนต้องใช้ resolver กลาง (TaxId → เลขบัตร) ไม่ใช่ emp.TaxId เดี่ยว ๆ"),
    (POS, "CompleteOrderAsync",
     ["DeductSaleStockAsync(", "CreateSalesJournalEntryAsync("],
     [("DeductSaleStockAsync(", "CreateSalesJournalEntryAsync(")], [],
     "E-01 ตัดสต็อกก่อน JE — COGS ของ JE มาจากต้นทุนที่ ledger ตัดจริง"),
    (POS, "SyncOfflineOrderAsync",
     ["DeductSaleStockAsync(", "CreateSalesJournalEntryAsync("],
     [("DeductSaleStockAsync(", "CreateSalesJournalEntryAsync(")], [],
     "E-01 เส้นออฟไลน์ต้องตัดสต็อกก่อน JE เหมือนเส้นออนไลน์"),
    (POS, "VoidOrderAsync",
     ["PosVoidSaleJournal.Decide(", "LoadSaleUnitCostsAsync("],
     [("PosVoidSaleJournal.Decide(", "ReverseJournalEntryAsync(")], [],
     "ยกเลิกบิลต้องตัดสิน JE ขายที่อาจถูกกลับด้วยมือแล้ว (ห้ามกลับซ้ำ) · คืนวัตถุดิบด้วยต้นทุน ณ วันขาย"),
    (POS, "RefundOrderAsync",
     ["LoadSaleUnitCostsAsync(", "PosCogsBooking.RefundCogs("], [], [],
     "คืนเงินเมนูสูตร: วัตถุดิบกลับด้วยต้นทุน ณ วันขาย ให้ตรงกับ COGS ที่ JE กลับ"),
    (POS, "ApplyRecipeConsumptionAsync",
     ["PosCogsBooking.RecipeCost(", "UnitCostOverride: restockCost"], [], [],
     "COGS สูตรนับเฉพาะวัตถุดิบ TrackStock · ขาคืนใช้ต้นทุน ณ วันขาย"),
    (BOT, "SyncRatesToCompanyAsync",
     ["CurrencyRateSync.Decide("], [], [],
     "sync ธปท. ต้องเขียนทับแถวอัตรา 0 ที่ค้าง (ไม่ข้ามเพราะ 'มีแถวแล้ว')"),
    (COMMISSION, "UpdatePlanAsync",
     ["CommissionPlanRules.IsDeactivateOnly(", "CommissionPlanRules.Validate("],
     [("CommissionPlanRules.IsDeactivateOnly(", "CommissionPlanRules.Validate(")], [],
     "ปิดใช้งานแผนเก่าต้องไม่ถูกบังคับให้แก้อัตรา"),
]


# ── ตัดคอมเมนต์/สตริงโดยคงตำแหน่ง (เพื่อ index ของ before[] และเลขบรรทัด) ────────────────
def mask(text: str) -> str:
    out = list(text)
    n = len(text)

    def blank(a, b):
        for k in range(a, min(b, n)):
            if out[k] != "\n":
                out[k] = " "

    def skip_string(i):
        """i ชี้ที่ prefix ($/@) หรือ " — คืน index หลังปิดสตริง (รองรับรูอินเทอร์โพเลตที่มีสตริงซ้อน)"""
        j = i
        interp = verb = False
        while j < n and text[j] in "$@":
            interp |= text[j] == "$"
            verb |= text[j] == "@"
            j += 1
        if text.startswith('"""', j):  # raw string — มองเป็นก้อนทึบ
            end = text.find('"""', j + 3)
            return n if end < 0 else end + 3
        j += 1  # ข้าม "
        while j < n:
            c = text[j]
            if verb and c == '"' and j + 1 < n and text[j + 1] == '"':
                j += 2; continue
            if not verb and c == "\\":
                j += 2; continue
            if c == '"':
                return j + 1
            if interp and c == "{":
                if j + 1 < n and text[j + 1] == "{":
                    j += 2; continue
                j = skip_code_block(j + 1)  # คืน index หลัง } ที่ปิดรู
                continue
            if not verb and c == "\n":
                return j
            j += 1
        return n

    def skip_code_block(j):
        depth = 0
        while j < n:
            c = text[j]
            if c in "$@\"" and (c == '"' or (j + 1 < n and text[j + 1] in '"$@')):
                j = skip_string(j); continue
            if c == "'":
                j = skip_char(j); continue
            if c == "{":
                depth += 1
            elif c == "}":
                if depth == 0:
                    return j + 1
                depth -= 1
            j += 1
        return n

    def skip_char(j):
        k = j + 1
        if k < n and text[k] == "\\":
            k += 2
        else:
            k += 1
        while k < n and text[k] != "'" and text[k] != "\n" and k - j < 12:
            k += 1
        return k + 1

    i = 0
    while i < n:
        c = text[i]
        if text.startswith("//", i):
            e = text.find("\n", i)
            e = n if e < 0 else e
            blank(i, e); i = e; continue
        if text.startswith("/*", i):
            e = text.find("*/", i + 2)
            e = n if e < 0 else e + 2
            blank(i, e); i = e; continue
        if c == '"' or (c in "$@" and i + 1 < n and text[i + 1] in '"$@'):
            e = skip_string(i)
            blank(i, e); i = e; continue
        if c == "'":
            e = skip_char(i)
            blank(i, e); i = e; continue
        i += 1
    return "".join(out)


RE_DECL = r"^[ \t]*(?:public|private|internal|protected)\b[^;\n=]*?\b{name}\s*(?:<[^>\n]*>)?\s*\("


def method_body(masked: str, name: str):
    """คืน (start, end) ของบอดี้ { … } ของเมธอดชื่อนี้ (ตัวแรก) หรือ None"""
    m = re.search(RE_DECL.format(name=re.escape(name)), masked, flags=re.M)
    if not m:
        return None
    # หาวงเล็บปิดของพารามิเตอร์ แล้วปีกกาแรก
    j = m.end() - 1
    depth = 0
    while j < len(masked):
        if masked[j] == "(":
            depth += 1
        elif masked[j] == ")":
            depth -= 1
            if depth == 0:
                break
        j += 1
    k = masked.find("{", j)
    arrow = masked.find("=>", j)
    if k < 0 or (0 <= arrow < k):
        return None
    depth = 0
    for e in range(k, len(masked)):
        if masked[e] == "{":
            depth += 1
        elif masked[e] == "}":
            depth -= 1
            if depth == 0:
                return (k, e + 1)
    return None


def check_rule(text: str, rule):
    rel, meth, must, before, forbid, why = rule
    masked = mask(text)
    span = method_body(masked, meth)
    if span is None:
        return [f"{rel}: ไม่พบเมธอด {meth} (ย้าย/เปลี่ยนชื่อ? ต้องแก้ RULES ให้ตรง — ห้ามปล่อยกติกาที่ไม่ตรวจอะไร)"]
    body = masked[span[0]:span[1]]
    line0 = masked.count("\n", 0, span[0]) + 1
    errs = []
    for p in must:
        if p not in body:
            errs.append(f"{rel}:{line0} {meth} ไม่เรียก `{p}` — {why}")
    for a, b in before:
        ia, ib = body.find(a), body.find(b)
        if ia >= 0 and ib >= 0 and ia > ib:
            errs.append(f"{rel}:{line0 + body.count(chr(10), 0, ib)} {meth}: `{a}` ต้องมาก่อน `{b}` — {why}")
    for p in forbid:
        idx = body.find(p)
        if idx >= 0:
            errs.append(f"{rel}:{line0 + body.count(chr(10), 0, idx)} {meth} ประกอบเองด้วย `{p}` — {why}")
    return errs


def _code_positions(raw: str, pat: str):
    """ตำแหน่งของ pat ที่อยู่ในโค้ดจริง (ไม่ใช่คอมเมนต์/สตริง) ของ raw"""
    m = mask(raw)
    out, k = [], m.find(pat)
    while k >= 0:
        out.append(k)
        k = m.find(pat, k + 1)
    return out


def _replace_code(raw: str, pat: str, repl: str, first_only: bool = False) -> str:
    pos = _code_positions(raw, pat)
    if first_only:
        pos = pos[:1]
    for k in reversed(pos):
        raw = raw[:k] + repl + raw[k + len(pat):]
    return raw


def self_test() -> list:
    """negative test ด้วยโค้ดจริง: ถอดการแก้ทีละแบบแล้วต้องฟ้อง · ของจริงต้องไม่ฟ้อง"""
    fails = []
    cache = {}
    for rule in RULES:
        rel, meth, must, before, forbid, _ = rule
        text = cache.setdefault(rel, (SRC / rel).read_text(encoding="utf-8"))
        span = method_body(mask(text), meth)
        if span is None:
            continue  # รายงานในรอบจริงอยู่แล้ว
        head, body_raw, tail = text[:span[0]], text[span[0]:span[1]], text[span[1]:]
        for p in must:
            # (1) ลบการเรียกที่บังคับ (ทุกจุดที่เป็นโค้ด) → ต้องฟ้อง
            # ตัวแทนต้องไม่มี p เป็น substring (ไม่งั้น "Removed_X" ยังมี "X" อยู่ = ด่านผ่านเอง)
            removed = _replace_code(body_raw, p, "REMOVED_CALL_SITE")
            if not any(f"`{p}`" in e for e in check_rule(head + removed + tail, rule)):
                fails.append(f"self-test: ลบ `{p}` จาก {meth} แล้ว checker ไม่ฟ้อง")
            # (2) เหลือไว้แค่ในคอมเมนต์ → ต้องฟ้อง (คอมเมนต์ที่พูดถึงการเรียกไม่ใช่การเรียก)
            commented = removed.replace("{", "{ // " + p + "\n", 1)
            if not any(f"`{p}`" in e for e in check_rule(head + commented + tail, rule)):
                fails.append(f"self-test: `{p}` ที่อยู่ในคอมเมนต์ถูกนับเป็นการเรียกใน {meth}")
        # (3) สลับลำดับ → ต้องฟ้อง
        for a, b in before:
            tok = "SWAP_TOKEN_(" 
            swapped = _replace_code(body_raw, a, tok, first_only=True)
            swapped = _replace_code(swapped, b, a, first_only=True)
            swapped = swapped.replace(tok, b, 1)
            if not any("ต้องมาก่อน" in e for e in check_rule(head + swapped + tail, rule)):
                fails.append(f"self-test: สลับ `{a}` ↔ `{b}` ใน {meth} แล้ว checker ไม่ฟ้อง")
        # (4) ประกอบสูตรเองซ้ำ → ต้องฟ้อง
        for p in forbid:
            injected = body_raw.replace("{", "{ var __x = " + p + "1m);\n", 1)
            if not any("ประกอบเอง" in e for e in check_rule(head + injected + tail, rule)):
                fails.append(f"self-test: ใส่ `{p}` ใน {meth} แล้ว checker ไม่ฟ้อง")
    # (5) ตัวตัดสตริง: การเรียกที่อยู่ในสตริง/สตริงอินเทอร์โพเลตซ้อน ต้องไม่นับ · ปีกกาในสตริงต้องไม่ทำให้บอดี้ขาด
    sample = (
        'class A {\n'
        '    private async Task Foo(int x)\n'
        '    {\n'
        '        var s = $"{(x > 0 ? "}" : "{")} Bar.Call(";\n'
        '        var t = @"}}"" Bar.Call(";\n'
        '        Baz.Real();\n'
        '    }\n'
        '    private void After() { Bar.Call(1); }\n'
        '}\n')
    rule = ("x.cs", "Foo", ["Baz.Real(", "Bar.Call("], [], [], "t")
    errs = check_rule(sample, rule)
    if not (len(errs) == 1 and "Bar.Call(" in errs[0]):
        fails.append(f"self-test: ตัวตัดสตริงผิด — คาด 1 ข้อ (Bar.Call ในสตริง/เมธอดอื่นไม่นับ) ได้ {errs}")
    return fails


def main() -> int:
    errs = []
    for rule in RULES:
        path = SRC / rule[0]
        if not path.exists():
            errs.append(f"{rule[0]}: ไม่พบไฟล์")
            continue
        errs += check_rule(path.read_text(encoding="utf-8"), rule)
    st = self_test()
    for e in errs:
        print("❌ " + e)
    for e in st:
        print("❌ " + e)
    if errs or st:
        return 1
    if "--self-test" in sys.argv:
        print("self-test: ผ่าน")
    print(f"required_call_site_check: {len(RULES)} กติกา ผ่าน (+ negative test ในตัว)")
    return 0


if __name__ == "__main__":
    sys.exit(main())
