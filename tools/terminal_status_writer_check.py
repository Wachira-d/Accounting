#!/usr/bin/env python3
# -*- coding: utf-8 -*-
"""สถานะ "ปลายทาง" ที่ถูกประทับนอกเจ้าของกติกา — ratchet กับ baseline

═══ ที่มา (DECISION_AUDIT_2026-09-18.md §2 ราก R1) ═══
ผลตรวจ 8 ทีมพบว่า **ต้นเหตุร่วมอันดับหนึ่ง**ของทั้งระบบคือ "สถานะปลายทางที่ระบบประทับเอง
โดยไม่มีของจริงยืนยัน" — พบใน 8 จุดคนละโดเมน:

  · `TaxReportStatus.Filed`      ← กดปุ่มอย่างเดียว แล้ว**ล็อกเอกสาร/JE ทั้งงวด**
  · `"Filed"` (ComplianceFiling) ← คอมเมนต์เขียนเอง "Simulate submission" + แต่งเลขอ้างอิงจาก GUID
  · `ReconciliationStatus.Matched` ← ประทับ "จับคู่แล้ว" โดยไม่เก็บว่าจับกับอะไร
  · `DocumentStatus.Approved`    ← POS/Integration/Mobile สร้างเอกสาร Approved ตรง ๆ ข้ามด่านสิทธิ์/โควตา/§86/4
  · `ReservationStatus.NoShow`   ← night audit ประทับเอง ไม่ผ่านเส้นยกเลิก ⇒ มัดจำค้าง
  · `StockDeducted = true`       ← CMS ตั้งธง "ตัดสต็อกแล้ว" **โดยไม่ตัด** แล้วตอนยกเลิกบวกคืน ⇒ สต็อกบวม

หลักการที่ถูกละเมิด: `CLAUDE.md` กฎเหล็ก #4 F2 ข้อ 3 — *"ค่าที่แต่งขึ้น / สถานะปลายทางที่ระบบ
ประทับเอง อันตรายกว่าการไม่ตอบ · สถานะ 'ระบบภายนอกรับแล้ว' ตั้งได้เฉพาะเมื่อภายนอกตอบกลับจริง"*
และ `DECISION_DOCTRINE.md` §1 G5 (ทิศปลอดภัย = ทิศที่ความเสียหาย**มองเห็น**)

═══ ทำไมต้องเป็น checker ไม่ใช่บทเรียนที่จดไว้ ═══
`REGRESSION_ROOT_CAUSE_2026-09-18.md` พิสูจน์แล้วว่า **20 จาก 33 กรณีถดถอย เป็น defect class
ที่ถูกจดไว้ใน CLAUDE.md อยู่แล้วก่อนเกิด** ⇒ การจดไม่ใช่ด่าน · R1 โผล่ซ้ำใน 6 โดเมนพร้อมกัน
แปลว่ามันจะโผล่ในโดเมนที่ 7 แน่ถ้าไม่มีอะไรฟ้อง

═══ ขอบเขตแคบโดยตั้งใจ (F4 ข้อ 3: ห้าม checker ที่ต้องรู้ taint ทั้งเรพ) ═══
ตรวจ**เฉพาะการกำหนดค่า** (`X.Status = Enum.Value`) ของสถานะปลายทางในตารางข้างล่าง
ไม่ตรวจการ**อ่าน**/เปรียบเทียบ (`== Approved` เป็นเรื่องของ `DocumentStatusRules` คนละด่าน)
ไม่พยายามรู้ว่าโค้ดเดินมาจากไหน — แค่ถามว่า "ไฟล์ที่ประทับ เป็นเจ้าของกติกาหรือเปล่า"

กติกา ratchet (เหมือน `dead_helper_check`):
  * จุดที่ประทับอยู่แล้ววันนี้อยู่ใน `tools/terminal_status_writer_baseline.txt` — **ไม่ล้ม**
    (แต่ละจุดต้องถูกตัดสินทีละตัวว่า "ต่อสายเข้าเจ้าของกติกา" หรือ "ยอมรับพร้อมเหตุผล")
  * ล้มเฉพาะจุด**ใหม่** = คอมมิตนี้เพิ่งเพิ่มการประทับสถานะปลายทางในที่ที่ไม่ใช่เจ้าของ
  * จุดที่หายไปจาก baseline แล้ว → รายงานให้ลบออกจาก baseline (ratchet เดินทางเดียว)
  * `--write-baseline` เขียนทับด้วยสภาพปัจจุบัน

ใช้: python3 tools/terminal_status_writer_check.py [--write-baseline] [--self-test] [--all]

═══ negative test (บังคับตาม F2 ข้อ 6) ═══
`--self-test` สร้างไฟล์ชั่วคราวที่มีการประทับนอกเจ้าของ แล้วต้องจับได้ · และไฟล์ที่ประทับ
**ในเจ้าของ** ต้องไม่ถูกฟ้อง · และการ**อ่าน**สถานะต้องไม่ถูกฟ้อง
"""
import os
import re
import sys
import tempfile

ROOT = os.path.dirname(os.path.dirname(os.path.abspath(__file__)))
SRC = os.path.join(ROOT, "Accounting")
BASELINE = os.path.join(ROOT, "tools", "terminal_status_writer_baseline.txt")

# ── ตารางสถานะปลายทาง: รูปแบบที่ประทับ → ไฟล์ที่เป็น "เจ้าของกติกา" ──
# key   = ป้ายอ่านง่ายที่โผล่ในข้อความฟ้อง
# regex = รูปแบบการ **กำหนดค่า** เท่านั้น (ซ้าย `=` ต้องเป็น property ไม่ใช่ `==`)
# owners= path (relative ต่อ Accounting/) ที่ได้รับอนุญาตให้ประทับ — เจ้าของกติกาของสถานะนั้น
WATCH = [
    {
        "label": "TaxReportStatus.Filed",
        "regex": r"\.\s*Status\s*=\s*TaxReportStatus\s*\.\s*Filed\b",
        "owners": ["Services/Implementations/TaxService.cs"],
        "why": "ล็อกเอกสาร/JE ทั้งงวด — ตั้งได้เฉพาะเส้นที่ตรวจว่ามีหลักฐานการยื่นจริง",
    },
    {
        # จับทั้ง ComplianceFiling และ CorporateIncomeTax — คนละ entity แต่ defect class เดียวกัน
        # (คำว่า "Filed" แปลว่า "ยื่นต่อกรมสรรพากรแล้ว" แต่โค้ดตั้งมันตอน "ลง JE เสร็จ")
        "label": 'Status = "Filed" (สตริง)',
        "regex": r"\.\s*Status\s*=\s*\"Filed\"",
        "owners": ["Services/Implementations/ComplianceService.cs"],
        "why": "สถานะ 'ยื่นแล้ว' แบบสตริง — ตั้งได้เฉพาะเมื่อมีเลขรับจริง ห้ามแต่งจาก GUID/ห้ามตั้งตอนลง JE",
    },
    {
        # owners ว่างโดยตั้งใจ: **ปลายทางที่ตั้งใจ**คือให้มีตัวตัดสินจับคู่ตัวเดียว
        # (`Helpers/BankMatchArbiter`) เป็นคนเดียวที่ประทับ — จุดที่ประทับอยู่วันนี้ 4 จุด
        # ใช้สูตรคนละชุดและให้อันดับต่างกัน (GAP-1 ค้างมาหลายรอบ) จึงอยู่ใน baseline
        # ทั้งหมด แล้วตัดออกทีละจุดเมื่อต่อสายเข้าตัวตัดสินกลางแล้ว
        "label": "ReconciliationStatus.Matched",
        "regex": r"ReconciliationStatus\s*\.\s*Matched\s*;",
        "owners": [],
        "why": "ประทับ 'จับคู่แล้ว' ต้องเก็บว่าจับกับอะไร + ผ่านด่านยอด + ผ่านตัวตัดสินจับคู่ตัวเดียว",
    },
    {
        "label": "DocumentStatus.Approved (ประทับตรง)",
        "regex": r"\.\s*Status\s*=\s*(?:Models\.Enums\.)?DocumentStatus\s*\.\s*Approved\b",
        "owners": ["Services/Implementations/DocumentService.cs"],
        "why": "อนุมัติเอกสารต้องผ่าน ApproveDocumentAsync (ด่านสิทธิ์ · โควตา · §86/4 · JE · สต็อก)",
    },
    {
        "label": "StockDeducted = true",
        "regex": r"\.\s*StockDeducted\s*=\s*true\b",
        "owners": [],  # ไม่มีใครควรตั้งธงนี้โดยไม่ผ่าน IStockLedger — ทุกจุดต้องอยู่ใน baseline หรือถูกแก้
        "why": "ธง 'ตัดสต็อกแล้ว' ต้องตั้งหลังตัดจริงผ่าน IStockLedger เท่านั้น",
    },
]

_BLOCK_COMMENT = re.compile(r"/\*.*?\*/", re.S)


def strip_comments(text: str) -> str:
    """ตัดคอมเมนต์ออกก่อนสแกน — doc-comment ที่ *พูดถึง* สถานะไม่ใช่การประทับ
    (บทเรียนเดียวกับ dead_helper_check: การอ้างถึงในคอมเมนต์ไม่ใช่ call site)
    แทนที่ด้วยช่องว่างจำนวนเท่าเดิมเพื่อให้เลขบรรทัดไม่ขยับ"""
    text = _BLOCK_COMMENT.sub(lambda m: re.sub(r"[^\n]", " ", m.group(0)), text)
    out = []
    for line in text.split("\n"):
        idx = line.find("//")
        if idx >= 0:
            # ไม่ตัดเมื่อ // อยู่ในสตริง (พอเพียงสำหรับกรณีที่เจอจริง: "http://")
            if not re.search(r'"[^"]*//', line[:idx + 2]):
                line = line[:idx]
        out.append(line)
    return "\n".join(out)


def scan():
    """คืน list ของ (key, ข้อความอธิบาย, [เลขบรรทัด]) — **key ไม่มีเลขบรรทัด**

    เจตนา: baseline ต้องทนการขยับบรรทัด (ทุกคอมมิตที่แก้ไฟล์เดียวกันทำให้เลขเลื่อน
    ⇒ baseline ที่อิงเลขบรรทัดจะฟ้อง "ของใหม่" ทั้งที่ไม่มีอะไรใหม่ = checker ที่ฟ้องผิด
    ซึ่ง F2 ข้อ 6 บอกว่า "checker ที่ฟ้องผิด = checker ที่พัง")
    จึงเก็บเป็น `ไฟล์:ป้าย` แล้วเทียบ **จำนวน** — เพิ่มจุดในไฟล์เดิมก็ยังจับได้"""
    hits = []
    for dirpath, dirnames, filenames in os.walk(SRC):
        dirnames[:] = [d for d in dirnames if d not in ("bin", "obj", "node_modules")]
        for fn in filenames:
            if not fn.endswith(".cs"):
                continue
            full = os.path.join(dirpath, fn)
            rel = os.path.relpath(full, SRC).replace(os.sep, "/")
            try:
                raw = open(full, encoding="utf-8", errors="replace").read()
            except OSError:
                continue
            body = strip_comments(raw)
            for w in WATCH:
                if rel in w["owners"]:
                    continue
                lines = [body.count("\n", 0, m.start()) + 1
                         for m in re.finditer(w["regex"], body)]
                if lines:
                    hits.append((f"{rel}:{w['label']}", w["why"], lines))
    hits.sort()
    return hits


def load_baseline():
    """คืน dict {key: จำนวนที่ยอมรับไว้}"""
    out = {}
    if not os.path.exists(BASELINE):
        return out
    for line in open(BASELINE, encoding="utf-8"):
        line = line.strip()
        if not line or line.startswith("#"):
            continue
        key, _, cnt = line.rpartition(" = ")
        try:
            out[key] = int(cnt)
        except ValueError:
            out[line] = 1
    return out


def write_baseline(hits):
    with open(BASELINE, "w", encoding="utf-8") as f:
        f.write("# จุดที่ประทับ 'สถานะปลายทาง' นอกเจ้าของกติกา ณ วันที่สร้าง baseline\n")
        f.write("# แต่ละแถวต้องถูกตัดสินทีละตัว: ต่อสายเข้าเจ้าของกติกา หรือ ยอมรับพร้อมเหตุผลในโค้ด\n")
        f.write("# ห้ามเพิ่มแถวเพื่อให้ checker เขียว — ratchet เดินทางเดียว (ตัดออกได้ เพิ่มไม่ได้)\n")
        for k, _, lines in hits:
            f.write(f"{k} = {len(lines)}\n")


def self_test():
    """negative test — ใส่บั๊กกลับแล้วต้องจับได้"""
    global SRC
    ok = True
    with tempfile.TemporaryDirectory() as tmp:
        os.makedirs(os.path.join(tmp, "Services", "Implementations"), exist_ok=True)

        bad = os.path.join(tmp, "Services", "Implementations", "SomeOtherService.cs")
        open(bad, "w", encoding="utf-8").write(
            "class X {\n"
            "  void M(Doc d) {\n"
            "    d.Status = DocumentStatus.Approved;\n"
            "  }\n"
            "}\n"
        )

        owner = os.path.join(tmp, "Services", "Implementations", "DocumentService.cs")
        open(owner, "w", encoding="utf-8").write(
            "class Y {\n"
            "  void Approve(Doc d) { d.Status = DocumentStatus.Approved; }\n"
            "}\n"
        )

        reader = os.path.join(tmp, "Services", "Implementations", "ReaderService.cs")
        open(reader, "w", encoding="utf-8").write(
            "class Z {\n"
            "  // d.Status = DocumentStatus.Approved;  <- ในคอมเมนต์ ไม่ใช่การประทับ\n"
            "  bool M(Doc d) => d.Status == DocumentStatus.Approved;\n"
            "}\n"
        )

        saved, SRC = SRC, tmp
        try:
            keys = {k for k, _, _ in scan()}
        finally:
            SRC = saved

        if not any("SomeOtherService.cs" in k for k in keys):
            print("❌ self-test: ไม่จับการประทับนอกเจ้าของกติกา"); ok = False
        if any("DocumentService.cs" in k for k in keys):
            print("❌ self-test: ฟ้องเจ้าของกติกาเอง (false positive)"); ok = False
        if any("ReaderService.cs" in k for k in keys):
            print("❌ self-test: ฟ้องการอ่าน/คอมเมนต์ (false positive)"); ok = False
    if ok:
        print("✅ self-test ผ่าน — จับของใหม่ได้ · ไม่ฟ้องเจ้าของกติกา · ไม่ฟ้องการอ่าน/คอมเมนต์")
    return 0 if ok else 1


def main():
    args = sys.argv[1:]
    if "--self-test" in args:
        return self_test()

    hits = scan()
    if "--write-baseline" in args:
        write_baseline(hits)
        print(f"เขียน baseline แล้ว: {sum(len(l) for _, _, l in hits)} จุด "
              f"ใน {len(hits)} ไฟล์×ป้าย")
        return 0

    base = load_baseline()
    cur = {k: len(lines) for k, _, lines in hits}
    total = sum(cur.values())
    new = [(k, w, lines) for k, w, lines in hits if len(lines) > base.get(k, 0)]
    gone = sorted(k for k in base if base[k] > cur.get(k, 0))

    if "--all" in args:
        for k, w, lines in hits:
            mark = "ใหม่" if len(lines) > base.get(k, 0) else "baseline"
            print(f"  [{mark}] {k} (บรรทัด {', '.join(map(str, lines))})\n         → {w}")

    print(f"จุดประทับสถานะปลายทางนอกเจ้าของกติกา: {total} "
          f"(baseline {sum(base.values())})")
    if gone:
        print(f"ℹ️  {len(gone)} รายการใน baseline ลดลงแล้ว — ปรับ baseline ในคอมมิตเดียวกัน:")
        for g in gone[:10]:
            print(f"   - {g} (baseline {base[g]} → ปัจจุบัน {cur.get(g, 0)})")

    if new:
        print(f"❌ จุดประทับ**ใหม่** {len(new)} รายการ (สถานะปลายทางที่ไม่มีของจริงยืนยัน — ราก R1):")
        for k, w, lines in new:
            print(f"   {k} (บรรทัด {', '.join(map(str, lines))}) "
                  f"— baseline {base.get(k, 0)} → ปัจจุบัน {len(lines)}")
            print(f"     → {w}")
        print("   → ต่อสายเข้าเจ้าของกติกาของสถานะนั้น หรือพิสูจน์ว่ามีของจริงยืนยันก่อนประทับ")
        print("   → ห้ามเติมลง baseline เพื่อให้ผ่าน")
        return 1

    print("✅ ไม่มีจุดประทับสถานะปลายทางใหม่")
    return 0


if __name__ == "__main__":
    sys.exit(main())
