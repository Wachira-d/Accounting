#!/usr/bin/env python3
# -*- coding: utf-8 -*-
"""สร้างแถว `CompanySettings` นอก `Helpers/CompanySettingsFactory` — ต้องเป็น 0 จุด

═══ ที่มา (รอบ 193 · ผลตรวจ S-01 `erp-review/2026-09-24/audit-settings.md`) ═══
แถวค่าตั้งเคยถูกสร้างแบบ lazy จาก 6 ที่ด้วย `new CompanySettings { CompanyId = … }` ⇒ ได้
`VatRegistered = true` จากค่า default ของ entity เสมอ ไม่ว่าบริษัทจะจด VAT หรือไม่ ⇒ หน้าตั้งค่าโชว์ติ๊ก
"จด VAT" ให้บริษัทที่ไม่ได้จด · กดบันทึกแล้ว `SettingsService` sync ทับ `Company.IsVatRegistered = true`
⇒ ออกใบกำกับเก็บ VAT ได้ (§90/2). ตัวสร้างตัวเดียว (`CompanySettingsFactory`) seed ธง/อัตรา VAT จากบริษัท —
checker นี้กันจุดสร้างที่เจ็ดที่ลืมเรียกตัวนั้น (F2 ข้อ 4 ตัวตั้งตัวเดียว)

กติกา: ไฟล์ .cs ใต้ `Accounting/` (ไม่รวมเทสต์) ห้ามมี `new CompanySettings {/(` หรือ
`CompanySettings x = new(` นอก `Helpers/CompanySettingsFactory.cs` · ไม่นับคอมเมนต์/สตริง

ใช้: python3 tools/company_settings_factory_check.py [--self-test]
"""
import os
import re
import sys
import tempfile

ROOT = os.path.dirname(os.path.dirname(os.path.abspath(__file__)))
SRC = os.path.join(ROOT, "Accounting")
OWNER = "Helpers/CompanySettingsFactory.cs"

NEW_RE = re.compile(
    r"\bnew\s+(?:global::)?(?:Accounting\.)?(?:Models\.Entities\.)?CompanySettings\s*[({]"
    r"|\bCompanySettings\s+\w+\s*=\s*new\s*\(")


def strip(text: str) -> str:
    """ตัดคอมเมนต์ + สตริงธรรมดา (คงบรรทัด) — พอสำหรับรูปแบบที่ตรวจ"""
    text = re.sub(r"/\*.*?\*/", lambda m: re.sub(r"[^\n]", " ", m.group(0)), text, flags=re.S)
    text = re.sub(r'@"(?:[^"]|"")*"', lambda m: re.sub(r"[^\n]", " ", m.group(0)), text)
    text = re.sub(r'"(?:\\.|[^"\\\n])*"', '""', text)
    return re.sub(r"//[^\n]*", "", text)


def scan():
    hits = []
    for dirpath, dirnames, filenames in os.walk(SRC):
        dirnames[:] = [d for d in dirnames if d not in ("bin", "obj", "node_modules", "wwwroot")]
        for fn in filenames:
            if not fn.endswith(".cs"):
                continue
            full = os.path.join(dirpath, fn)
            rel = os.path.relpath(full, SRC).replace(os.sep, "/")
            if rel == OWNER:
                continue
            body = strip(open(full, encoding="utf-8", errors="replace").read())
            for m in NEW_RE.finditer(body):
                hits.append(f"{rel}:{body.count(chr(10), 0, m.start()) + 1}")
    return hits


def self_test():
    global SRC
    ok = True
    with tempfile.TemporaryDirectory() as tmp:
        os.makedirs(os.path.join(tmp, "Controllers"))
        os.makedirs(os.path.join(tmp, "Helpers"))
        open(os.path.join(tmp, "Controllers", "Bad.cs"), "w", encoding="utf-8").write(
            "class B { void M(){ var s = new CompanySettings { CompanyId = id }; "
            "CompanySettings t = new() { CompanyId = id }; var u = new Models.Entities.CompanySettings(); } }\n")
        open(os.path.join(tmp, "Controllers", "Ok.cs"), "w", encoding="utf-8").write(
            "class O { // new CompanySettings { } ในคอมเมนต์\n"
            "  string x = \"new CompanySettings {\"; void M(){ var s = CompanySettingsFactory.NewFor(c); "
            "var r = new CompanySettingsResponse(); } }\n")
        open(os.path.join(tmp, "Helpers", "CompanySettingsFactory.cs"), "w", encoding="utf-8").write(
            "static class F { static CompanySettings N() => new CompanySettings { }; }\n")
        saved, SRC = SRC, tmp
        try:
            hits = scan()
        finally:
            SRC = saved
    bad = [h for h in hits if h.startswith("Controllers/Bad.cs")]
    if len(bad) != 3:
        print(f"❌ self-test: ต้องจับได้ 3 จุดใน Bad.cs ได้ {len(bad)}", hits); ok = False
    if any(h.startswith("Controllers/Ok.cs") or "CompanySettingsFactory" in h for h in hits):
        print("❌ self-test: ฟ้องคอมเมนต์/สตริง/ชนิดชื่อคล้าย/ไฟล์เจ้าของ (false positive)", hits); ok = False
    if ok:
        print("✅ self-test ผ่าน — จับ new CompanySettings ทั้ง 3 รูป · ไม่ฟ้องคอมเมนต์/สตริง/เจ้าของ")
    return 0 if ok else 1


def main():
    if "--self-test" in sys.argv[1:]:
        return self_test()
    hits = scan()
    if hits:
        print(f"❌ สร้างแถว CompanySettings นอก {OWNER} {len(hits)} จุด:")
        for h in hits:
            print(f"   {h}")
        print("   → ใช้ CompanySettingsFactory.NewFor(company) / AddNewAsync(db, companyId) "
              "(seed สถานะ/อัตรา VAT จากบริษัท — ค่า default ของ entity คือ \"จด VAT\")")
        return 1
    print("✅ แถว CompanySettings ถูกสร้างผ่าน CompanySettingsFactory ที่เดียว")
    return 0


if __name__ == "__main__":
    sys.exit(main())
