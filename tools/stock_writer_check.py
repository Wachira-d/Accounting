#!/usr/bin/env python3
"""ห้ามเขียนสต็อกนอก `IStockLedger` — บังคับกติกา "หนึ่งความจริงของสต็อก"

ที่มา (POS_MULTI_BRANCH_ANALYSIS.md §2.2 — บั๊กจริง)
    เดิมมีสองความจริงที่ไม่คุยกัน:
      • `Product.CurrentStock`  เขียนโดย 9 ไฟล์ (POS/เอกสาร/นำเข้า/ผลิต/ฝากขาย/…)
      • `WarehouseStock`        เขียนโดย `WarehouseService` ตัวเดียว (ใบโอนคลัง)
    โอนวัตถุดิบจากครัวกลางไปสาขา → `WarehouseStock` ขยับ แต่ยอดที่ POS ตัดตอนขาย
    คือ `CurrentStock` ซึ่งไม่ขยับ ⇒ ตัวเลขสองชุดที่ไม่มีวันตรงกัน และไม่มีใครรู้ว่า
    อันไหนคือของจริง.  ยังพบอีกว่า **เครื่องหมายของ `StockMovement.Quantity` ไม่ตรงกัน**
    ระหว่างผู้เขียน (บางที่ OUT เก็บเป็นบวก บางที่เป็นลบ) ⇒ รายงานที่ SUM จาก
    movement ตรงบางที่ผิดบางที่.

กติกาที่ checker นี้บังคับ
    การเขียนสต็อกทุกชนิดต้องผ่าน `IStockLedger.MoveAsync` — ห้ามมี
      • `_db.StockMovements.Add(...)`
      • `<x>.CurrentStock +=` / `-=` / `=`
      • `<x>.Quantity +=` บนแถว `WarehouseStock`  (จับผ่านชื่อตัวแปรที่มาจาก
        `WarehouseStocks` — ดูฟังก์ชัน `warehouse_stock_vars`)
    นอกไฟล์ที่ได้รับยกเว้น (ตัว ledger เอง + migration ที่ซ่อมข้อมูลเก่า)

ทำไมเป็น checker ได้ (ต่างจากเคส `_db.Users` ที่จงใจไม่เขียน checker)
    นี่คือ **รูปทรงของโค้ด** ไม่ใช่ taint — "มีการเขียนฟิลด์นี้ไหม" ตอบได้จากตัวบท
    ไม่ต้องรู้ว่าค่ามาจากไหน จึงไม่มี false positive ประเภทที่ทำให้ checker พัง

negative test: `python3 tools/stock_writer_check.py --self-test`
"""
import re
import sys
from pathlib import Path

ROOT = Path(__file__).resolve().parent.parent
SRC = ROOT / "Accounting"

# ── ไฟล์ที่ได้รับยกเว้น ──────────────────────────────────────────────
# ledger เองคือที่เดียวที่เขียนได้ · migration ซ่อมข้อมูลเก่าเป็น SQL ล้วน
ALLOWED = {
    "Services/Implementations/Inventory/StockLedger.cs",
    "Data/DatabaseMigrationHelper.cs",
}

# ทั้ง `_db.StockMovements.Add(...)` และ `_db.Set<StockMovement>().Add(...)` — ทรงหลัง
# หลุดรุ่นแรกจริง (StockTransferController เขียน movement ตรงมาตลอด · ERP_REVIEW E-04)
RE_MOVEMENT_ADD = re.compile(r"\b(?:StockMovements|Set\s*<\s*StockMovement\s*>\s*\(\s*\))\s*\.\s*(?:Add|AddRange)\b")
RE_CURRENT_STOCK_WRITE = re.compile(r"\.CurrentStock\s*(\+=|-=|=(?!=))")
# `WarehouseStock` row mutation — จับจากชื่อฟิลด์ที่มีเฉพาะบนแถวนั้น
RE_WS_QTY_WRITE = re.compile(r"\.(AvailableQuantity|ReservedQuantity)\s*(\+=|-=|=(?!=))")


def strip_comments_and_strings(text: str) -> str:
    """ตัดคอมเมนต์และ string literal ออก **โดยคงจำนวนบรรทัดไว้**

    ต้องคงบรรทัดไว้ ไม่งั้นเลขบรรทัดที่ฟ้องเพี้ยนทั้งไฟล์ (บทเรียนซ้ำจาก
    `localstorage_key_check` และ `css_var_check`) · และต้องแยกสถานะ
    ในสตริง/นอกสตริง ไม่งั้นจะไปกิน `//` ของ `https://` ที่อยู่ในสตริงเอง
    (บทเรียนจาก `csp_external_ref_check`)
    """
    out = []
    i, n = 0, len(text)
    while i < n:
        c = text[i]
        nxt = text[i + 1] if i + 1 < n else ""
        if c == "/" and nxt == "/":
            while i < n and text[i] != "\n":
                i += 1
            continue
        if c == "/" and nxt == "*":
            i += 2
            while i < n - 1 and not (text[i] == "*" and text[i + 1] == "/"):
                if text[i] == "\n":
                    out.append("\n")
                i += 1
            i = min(i + 2, n)
            continue
        if c == '"':
            # raw string """...""" / verbatim @"..." / ปกติ "..."
            if text.startswith('"""', i):
                i += 3
                while i < n and not text.startswith('"""', i):
                    if text[i] == "\n":
                        out.append("\n")
                    i += 1
                i = min(i + 3, n)
                continue
            verbatim = i > 0 and text[i - 1] == "@"
            i += 1
            while i < n:
                if text[i] == "\n":
                    out.append("\n")
                    i += 1
                    continue
                if verbatim:
                    if text[i] == '"':
                        if i + 1 < n and text[i + 1] == '"':
                            i += 2
                            continue
                        i += 1
                        break
                else:
                    if text[i] == "\\":
                        i += 2
                        continue
                    if text[i] == '"':
                        i += 1
                        break
                i += 1
            continue
        if c == "'":
            i += 1
            while i < n and text[i] != "'":
                i += 2 if text[i] == "\\" else 1
            i += 1
            continue
        out.append(c)
        i += 1
    return "".join(out)


def scan_text(rel: str, text: str):
    findings = []
    code = strip_comments_and_strings(text)
    for lineno, line in enumerate(code.splitlines(), start=1):
        if RE_MOVEMENT_ADD.search(line):
            findings.append((rel, lineno, "StockMovements.Add / Set<StockMovement>().Add — ต้องเรียก IStockLedger.MoveAsync แทน"))
        if RE_CURRENT_STOCK_WRITE.search(line):
            findings.append((rel, lineno, "เขียน Product.CurrentStock เอง — ต้องให้ ledger เป็นคนปรับ"))
        if RE_WS_QTY_WRITE.search(line):
            findings.append((rel, lineno, "เขียนยอดในแถว WarehouseStock เอง — ต้องเรียก IStockLedger.MoveAsync"))
    return findings


def scan_repo():
    findings = []
    for path in sorted(SRC.rglob("*.cs")):
        rel = path.relative_to(SRC).as_posix()
        if rel in ALLOWED:
            continue
        findings.extend(scan_text(rel, path.read_text(encoding="utf-8", errors="replace")))
    return findings


GOOD_SAMPLE = '''
// ตัวอย่างที่ถูก: เรียกผ่าน ledger + พูดถึงชื่อฟิลด์ในคอมเมนต์/สตริงเฉย ๆ
public class Ok
{
    // เดิมเขียน product.CurrentStock -= qty; ตรงนี้ — ย้ายไป ledger แล้ว
    const string Note = "product.CurrentStock = 0";
    public async Task Run()
    {
        await _stock.MoveAsync(new StockMoveRequest(cid, pid, -qty, "OUT"));
        var onHand = product.CurrentStock;      // อ่านอย่างเดียว = ได้
        if (product.CurrentStock == qty) { }    // เทียบ == ไม่ใช่กำหนดค่า
    }
}
'''

BAD_SAMPLE = '''
public class Bad
{
    public void Run()
    {
        product.CurrentStock -= qty;
        _db.StockMovements.Add(new StockMovement { Quantity = qty });
        _db.Set<StockMovement>().Add(new StockMovement { Quantity = qty });
        ws.AvailableQuantity += qty;
    }
}
'''


def self_test() -> int:
    good = scan_text("Good.cs", GOOD_SAMPLE)
    bad = scan_text("Bad.cs", BAD_SAMPLE)
    ok = True
    if good:
        ok = False
        print("❌ negative test ล้ม: ฟ้องโค้ดที่ถูกต้อง")
        for f in good:
            print("   ", f)
    if len(bad) != 4:
        ok = False
        print(f"❌ negative test ล้ม: ควรจับบั๊กได้ 4 จุด แต่จับได้ {len(bad)}")
        for f in bad:
            print("   ", f)
    print("✅ negative test ผ่าน" if ok else "")
    return 0 if ok else 1


def main() -> int:
    if "--self-test" in sys.argv:
        return self_test()
    findings = scan_repo()
    if not findings:
        print("✅ stock_writer_check: ไม่มีใครเขียนสต็อกนอก IStockLedger")
        return 0
    print(f"❌ stock_writer_check: พบการเขียนสต็อกนอก IStockLedger {len(findings)} จุด")
    for rel, lineno, msg in findings:
        print(f"  Accounting/{rel}:{lineno}  {msg}")
    return 1


if __name__ == "__main__":
    sys.exit(main())
