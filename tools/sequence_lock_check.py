#!/usr/bin/env python3
"""ออกเลขรันเองด้วย OrderByDescending(<เลข>) — ไม่มีล็อกและเรียงแบบข้อความ

ที่มา (บั๊กจริง · ผลตรวจ F-08):
มีจุดที่ออกเลขเองราว 25 จุดกระจายทั่ว service ทุกจุดเขียนรูปเดียวกัน

    var last = await _db.Payments
        .Where(p => p.PaymentNumber.StartsWith(pattern))
        .OrderByDescending(p => p.PaymentNumber)      // ← เรียงแบบ **ข้อความ**
        .Select(p => p.PaymentNumber).FirstOrDefaultAsync();
    var next = 1;
    if (last != null && int.TryParse(last[pattern.Length..], out var n)) next = n + 1;

พลาดพร้อมกัน 3 อย่าง:
  1. **ไม่มี advisory lock** — สอง request/สอง instance อ่าน max ได้เลขเดียวกัน
  2. **เรียงแบบ lexicographic** — "…9999" > "…10000" ⇒ ทะลุหลักพันแล้ววนกลับ
     ไปทับเลขเดิม (เงียบสนิท เพราะเลขที่ได้ "ดูปกติ")
  3. ไม่นับแถวที่ยัง Add ค้างใน change tracker

กติกาที่ตรวจ (shape-based): ห้าม `OrderByDescending(x => x.<ชื่อที่ลงท้ายด้วย
Number/Code/No>)` นอกโฟลเดอร์ Helpers — ให้เดินผ่านตัวออกเลขกลางแทน
(`Helpers/SequenceNumber` · `Helpers/DocumentNumberGenerator` ·
`Helpers/AssetCodeGenerator` · `JournalEntryBuilder.NextJournalNumberAsync`)

ไม่ฟ้อง: การเรียงเพื่อ**แสดงผล** ที่ตามด้วย ToListAsync/Skip/Take (ตารางที่
ผู้ใช้เรียงตามเลขเอกสาร) — ตัวชี้วัดคือมี `FirstOrDefaultAsync`/`FirstAsync`
ตามมาภายในไม่กี่บรรทัด ซึ่งเป็นลายเซ็นของ "หา max เพื่อ +1"
"""
import re
import sys
from pathlib import Path

ROOT = Path(__file__).resolve().parent.parent
SRC = ROOT / "Accounting"

# ชื่อคอลัมน์ที่เป็น "เลขรัน" — ลงท้ายด้วย Number / Code / No
# `(?!\s*[.\w])` = ต้องจบที่ตัวคอลัมน์จริง ๆ — กัน `a.AccountCode.Length`
# ซึ่งเป็นการ**ค้นหา**ผังบัญชีที่ prefix ยาวสุด ไม่ใช่การออกเลข (เจอตอน
# negative test บนเรพจริง)
ORDER_DESC = re.compile(
    r"\.OrderByDescending\(\s*(\w+)\s*=>\s*\1\.(?P<col>\w*(?:Number|Code|No))(?!\s*[.\w])")
TAKE_FIRST = re.compile(r"\.First(OrDefault)?Async\(")
# โฟลเดอร์ที่เป็น "ตัวออกเลขกลาง" — ที่นั่นตั้งใจทำแบบนี้พร้อมล็อก
ALLOWED_DIRS = ("Helpers", )
ALLOWED_FILES = ("JournalEntryBuilder.cs", )
# คอลัมน์ที่ลงท้ายตรงเงื่อนไขแต่ไม่ใช่เลขรัน
FALSE_FRIENDS = {"PhoneNo", "TaxIdNo", "CountryCode", "CurrencyCode", "LanguageCode"}


def scan(path: Path):
    text = path.read_text(encoding="utf-8", errors="replace")
    lines = text.splitlines()
    hits = []
    for i, line in enumerate(lines):
        m = ORDER_DESC.search(line)
        if not m:
            continue
        col = m.group("col")
        if col in FALSE_FRIENDS:
            continue
        # ลายเซ็นของ "หา max เพื่อ +1" = จบ **คำสั่งเดียวกัน** ด้วย FirstOrDefaultAsync
        # ⚠️ ห้ามดูแค่ "บรรทัดใกล้ ๆ" — เมธอดถัดไปที่มี FirstOrDefaultAsync จะทำให้
        # การเรียงเพื่อ**แสดงผล** (…Skip().Take().ToListAsync()) ถูกฟ้องผิด
        # (เจอตอน negative test — checker ที่ฟ้องผิด = checker ที่พังแล้ว)
        stmt = []
        for j in range(i, min(i + 12, len(lines))):
            stmt.append(lines[j])
            if ";" in lines[j]:
                break
        if not TAKE_FIRST.search("\n".join(stmt)):
            continue
        hits.append((i + 1, col, line.strip()[:110]))
    return hits


def main() -> int:
    targets = sys.argv[1:] or [str(SRC)]
    files = []
    for t in targets:
        p = Path(t)
        files.extend(sorted(p.rglob("*.cs")) if p.is_dir() else [p])

    total = 0
    for f in files:
        parts = f.parts
        if any(d in parts for d in ALLOWED_DIRS) or f.name in ALLOWED_FILES:
            continue
        for lineno, col, snippet in scan(f):
            total += 1
            rel = f.relative_to(ROOT) if str(f).startswith(str(ROOT)) else f
            print(f"{rel}:{lineno}: ออกเลข '{col}' เองด้วยการเรียงแบบข้อความ "
                  f"— ไม่มีล็อก และ \"9999\" ชนะ \"10000\"")
            print(f"    => {snippet}")
            print("    → ใช้ Accounting.Helpers.SequenceNumber.NextAsync "
                  "(หรือ DocumentNumberGenerator / AssetCodeGenerator / "
                  "JournalEntryBuilder.NextJournalNumberAsync)")

    print(f"\nตรวจ {len(files)} ไฟล์ .cs · จุดที่ออกเลขเอง {total} จุด")
    return 1 if total else 0


if __name__ == "__main__":
    raise SystemExit(main())
