#!/usr/bin/env python3
"""ห้ามตัดสิน "ผังนี้ลงรายการได้ไหม" ด้วยระดับผัง (Level < 4 / Level >= 4) — รอบ 206

ที่มา (บั๊กจริง · PAYMENT_CHANNEL_AUDIT ADV2-P1-1 + "ยังค้าง" รอบ 2)
    กติกา "บัญชีคุม ลงรายการตรงไม่ได้" มีเจ้าของคือ `Helpers/PostingAccountRule` (บัญชีคุม = **มีบัญชีย่อย**) แต่มีเจ้าของที่สอง
    (`PickedAccountGuard` — แก้แล้ว 2560776c) ที่สาม (`DocumentMoneyChannel.MinPostingLevel`) และตัวหาผังตั้งต้น ~46 จุดที่กรอง
    `a.Level >= 4` เอง ⇒ ผังของบริษัทที่ลงรายการได้ระดับ 2–3 ใช้เป็นแหล่งเงินไม่ได้ · ผัง 11122 ที่มีผังเงินฝากย่อย
    ผ่านทางหนึ่งแต่ถูกบล็อกอีกทาง · หน้าเว็บกรอง `level >= 4` ซ้ำอีกชุด

กติกาที่บังคับ
    โค้ด (ไม่นับคอมเมนต์) ใน Accounting/**/*.cs และ wwwroot/**/*.js|html ห้ามมี
      • `.Level < 4` · `.Level >= 4` · `.Level > 3` · `.Level <= 3` (และรูป `.level` ของ JS)
      • `MinPostingLevel`
    ใช้แทน: `PostingAccountRule.Check` (pure) · `PostingAccountGuard.LoadPostingFactsAsync`/`EnsurePostingAccountAsync` (ตัวดึง) ·
    `PostingAccountGuard.WhereNotControlAccount(db)` (ตัวกรอง query ของตัวหาผังตั้งต้น) · หน้าเว็บอ่าน `hasChildAccounts` จากเซิร์ฟเวอร์

negative test: รันทุกครั้ง (ฉีดบรรทัดเก่าลงสำเนาไฟล์จริงแล้วต้องฟ้อง · คอมเมนต์ต้องไม่ฟ้อง) · `--self-test` = เฉพาะ negative test
"""
import re
import sys
from pathlib import Path

ROOT = Path(__file__).resolve().parent.parent
SRC = ROOT / "Accounting"

RE_CS = re.compile(r"\.Level\s*(?:<\s*4|>=\s*4|>\s*3|<=\s*3)(?![0-9])|\bMinPostingLevel\b")
RE_JS = re.compile(r"\.level\s*(?:<\s*4|>=\s*4|>\s*3|<=\s*3)(?![0-9])")


def code_part(line: str) -> str:
    """ตัดคอมเมนต์บรรทัด (// · /// · * ของ block) — ตัด // ท้ายบรรทัดเมื่ออยู่นอกสตริง (นับเครื่องหมายคำพูด)"""
    s = line.strip()
    if s.startswith("//") or s.startswith("*") or s.startswith("/*") or s.startswith("<!--"):
        return ""
    out, quote = [], None
    i = 0
    while i < len(line):
        c = line[i]
        if quote:
            if c == "\\":
                out.append(line[i:i + 2]); i += 2; continue
            if c == quote:
                quote = None
        elif c in "\"'`":
            quote = c
        elif c == "/" and line[i:i + 2] == "//":
            break
        out.append(c)
        i += 1
    return "".join(out)


def scan_text(text: str, is_cs: bool) -> list:
    rx = RE_CS if is_cs else RE_JS
    hits = []
    for n, line in enumerate(text.split("\n"), 1):
        if rx.search(code_part(line)):
            hits.append((n, line.strip()))
    return hits


def scan_repo() -> list:
    found = []
    for p in SRC.rglob("*"):
        if not p.is_file() or "/bin/" in str(p) or "/obj/" in str(p):
            continue
        if p.suffix == ".cs":
            is_cs = True
        elif p.suffix in (".js", ".html") and "wwwroot" in p.parts:
            is_cs = False
        else:
            continue
        if is_cs is False and (p.name.endswith(".min.js") or "/lib/" in str(p)):
            continue
        for n, line in scan_text(p.read_text(encoding="utf-8", errors="replace"), is_cs):
            found.append(f"{p.relative_to(ROOT)}:{n}: {line[:160]}")
    return found


def self_test() -> list:
    """คืนรายการความล้มเหลวของ negative test (ว่าง = ผ่าน)"""
    errs = []
    dmc = (SRC / "Helpers" / "DocumentMoneyChannel.cs").read_text(encoding="utf-8")
    injected = dmc.replace("public static class DocumentMoneyChannel\n{",
                           "public static class DocumentMoneyChannel\n{\n    public const int MinPostingLevel = 4;\n"
                           "    static bool Old(int Level) => x.Level < 4;", 1)
    if injected == dmc:
        errs.append("หาจุดฉีดใน DocumentMoneyChannel.cs ไม่เจอ")
    elif len(scan_text(injected, True)) < 2:
        errs.append("ฉีด MinPostingLevel + x.Level < 4 กลับลง DocumentMoneyChannel แล้วไม่ฟ้อง")
    cases_cs = [
        ("var a = await _db.ChartOfAccounts.FirstOrDefaultAsync(a => a.AccountCode == c && a.Level >= 4);", True),
        ("        && x.Level > 3 && x.IsActive)", True),
        ("if (coa.Level <= 3) throw new X();", True),
        ("// Level >= 4 = บัญชี postable เท่านั้น", False),
        ("/// <summary>ระดับ (Level &lt; 4)</summary>", False),
        ("var s = \"a.Level >= 4\"; // ในสตริง", True),   # สตริงในโค้ด C# — ยังนับ (ไม่มีเหตุผลเขียนแบบนี้)
        ("var d = a.Level >= 40;", False),
        ("x.Level == 1", False),
    ]
    for line, want in cases_cs:
        got = bool(scan_text(line, True))
        if got != want:
            errs.append(f"C#: {line!r} — คาด {'ฟ้อง' if want else 'ไม่ฟ้อง'}")
    cases_js = [
        ("this.accounts.filter(a => a.accountCode.startsWith(prefix) && a.level >= 4)", True),
        ("const isCat = a.level <= 2;", False),
        ("// เดิม level >= 4 ข้ามผังระดับ 2–3", False),
    ]
    for line, want in cases_js:
        got = bool(scan_text(line, False))
        if got != want:
            errs.append(f"JS: {line!r} — คาด {'ฟ้อง' if want else 'ไม่ฟ้อง'}")
    return errs


def main() -> int:
    errs = self_test()
    if errs:
        print("❌ posting_level_check negative test ล้ม — checker ไม่มีด่าน:")
        for e in errs:
            print("  ", e)
        return 1
    if "--self-test" in sys.argv:
        print("✓ posting_level_check negative test ผ่าน")
        return 0
    found = scan_repo()
    if found:
        print("❌ ตัดสิน \"ลงรายการได้\" ด้วยระดับผัง — ใช้ PostingAccountRule / PostingAccountGuard.WhereNotControlAccount / hasChildAccounts แทน:")
        for f in found:
            print("  ", f)
        return 1
    print("✓ posting_level_check: ไม่มีการตัดสินบัญชีคุมด้วยระดับผัง (negative test ผ่าน)")
    return 0


if __name__ == "__main__":
    sys.exit(main())
