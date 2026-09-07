#!/usr/bin/env python3
"""ด่านสิทธิ์ของ endpoint ที่ "เขียน" ข้อมูล — checker ตัวที่ 26

═══ ที่มา (บั๊กจริง · ผลตรวจ A-D2 / D-A1 / D-A2) ═══
`[Authorize]` ระดับคลาสตอบแค่ "ล็อกอินอยู่ไหม" ไม่ได้ตอบ "มีสิทธิ์ทำสิ่งนี้ไหม"
⇒ ใน `DocumentController` มี 20 endpoint ที่เขียนข้อมูล (PUT · DELETE · convert ·
payments · write-off · contacts) **ไม่เช็คสิทธิ์เลย** ขณะที่ create/approve/void
เช็คครบ · ใน `PayrollController` หนักกว่า: **สร้างรอบ · อนุมัติ · จ่าย · นำส่ง ปกส. ·
ตั้งค่าอัตราภาษี** ไม่มีด่านอะไรเลย ⇒ สมาชิกคนไหนของบริษัทก็กดจ่ายเงินเดือนจริงได้

รูปแบบเดียวกับ `admin_menu_gate_check` — เทียบ "สิ่งที่ action ทำ" (verb ของ route)
กับ "ด่านที่มันเรียก" ซึ่งเป็นความสัมพันธ์ที่คอมไพเลอร์ไม่มีทางเห็น

═══ กติกา ═══
action ที่ผูกกับ [HttpPost] / [HttpPut] / [HttpDelete] / [HttpPatch] ในคอนโทรลเลอร์
ที่เฝ้าอยู่ ต้องมีอย่างน้อย 1 ใน `GATE_MARKERS` อยู่ในตัวเมธอด — เว้น action ที่อยู่ใน
`READ_ONLY_POSTS` (POST ที่รับ body มาคำนวณแล้วคืนค่า ไม่เขียนอะไร)

**ทำไมเป็น allow-list ต่อคอนโทรลเลอร์ ไม่ใช่ทั้งเรพ**: คอนโทรลเลอร์สาธารณะ
(`PublicQuotationController` · webhook · portal ของแขก) ตั้งใจไม่มีด่านผู้ใช้ —
กวาดทั้งเรพจะฟ้องผิดเป็นสิบจุด และ "checker ที่ฟ้องผิด = checker ที่พังแล้ว"
เพิ่มคอนโทรลเลอร์ใหม่เข้า `WATCHED` เมื่อมันแตะเงิน/ภาษี/ข้อมูลพนักงาน
"""
import os
import re
import sys

ROOT = os.path.dirname(os.path.dirname(os.path.abspath(__file__)))

WATCHED = [
    "Accounting/Controllers/DocumentController.cs",
    "Accounting/Controllers/PayrollController.cs",
    # เพิ่มรอบ 126d — ทุก write ที่นี่ "ก่อค่าใช้จ่ายให้บริษัท" (เปิด add-on รายเดือน ·
    # ซื้อโควตา · ส่งหลักฐานชำระเงิน) แต่มีแค่ [Authorize] ระดับคลาสมาตลอด
    # ⇒ สมาชิกคนไหนก็กดแทนบริษัทได้ · checker ไม่เคยมองเพราะไม่อยู่ในลิสต์นี้
    "Accounting/Controllers/MeteringController.cs",
    # เพิ่มรอบ 147 — ทุก write ที่นี่ "ยื่นเอกสารต่อกรมสรรพากรแทนบริษัท"
    # (ออก/ลงนาม/นำส่ง/ยกเลิก e-Tax · ส่งอีเมลที่มี timestamp ของ ETDA) แต่มีแค่
    # [Authorize] ระดับคลาส ⇒ สมาชิกคนไหนก็ยื่นแทนบริษัทได้ · checker รายงานเขียว
    # ตลอดเพราะไฟล์นี้ไม่เคยอยู่ในลิสต์ (บทเรียน "allow-list ครบไหม ≠ ผ่านไหม")
    "Accounting/Controllers/EtaxController.cs",
    # เพิ่มรอบ 148 — ทุก write ที่นี่ขยับ GL จริง (โพสต์ JE ค่าเสื่อม · จำหน่าย
    # ลง JE กำไร/ขาดทุน · ตัดจำหน่าย · ตีราคาใหม่) แต่มีแค่ [Authorize] ระดับคลาส
    # และ **ไม่มีคีย์สิทธิ์สินทรัพย์อยู่ใน PermissionKeys เลย** จนถึงรอบนี้
    "Accounting/Controllers/FixedAssetController.cs",
]

# ตัวบ่งชี้ว่า action นี้ผ่านด่านสิทธิ์บางอย่างแล้ว
GATE_MARKERS = (
    "DenyDocAsync",
    "DenyKeyAsync",
    "DocumentPermissionHelper",
    "RequirePayrollWriteAsync",
    "RequireBillingAsync",
    "RequireEtaxAsync",
    "RequireAssetAsync",
    "RequireAnyAsync",
    "HasPermissionAsync",
    "UserRole.Owner",          # ด่าน Owner-only (purge)
    "IsInRole(\"SystemAdmin\")",
)

# POST ที่ "อ่านอย่างเดียว" — รับ body มาคำนวณแล้วคืนค่า ไม่แตะฐานข้อมูล
# (ต้องระบุชื่อเมธอดตรง ๆ เพื่อให้การเพิ่มรายการเป็นการตัดสินใจที่ตั้งใจ)
READ_ONLY_POSTS = {
    # ภารกิจแลกโควตา — ผู้ใช้ทำงานสั้น ๆ แลกโควตา**ฟรี** ไม่ก่อหนี้ให้บริษัท
    # จึงตั้งใจไม่ผูกกับ BillingManage (ไม่งั้นฟีเจอร์นี้ตายสำหรับสมาชิกทั่วไป
    # ซึ่งเป็นกลุ่มเป้าหมายของมันพอดี) — เป็นการตัดสินใจ ไม่ใช่การลืม
    "ClaimReward",
    "SuggestPvAccounting",   # ถาม AI แนะนำผังบัญชี — ไม่เขียนเอกสาร
    "ParseAddress",          # แปลงที่อยู่ free-text → structured
}

ACTION_RE = re.compile(r'^\s*\[Http(Get|Post|Put|Delete|Patch)(\("([^"]*)"\))?\]')
NAME_RE = re.compile(r'>+\s+(\w+)\s*\(')


def scan(path):
    """คืนลิสต์ (line, verb, route, name) ของ action ที่เขียนข้อมูลแต่ไม่มีด่าน"""
    with open(path, encoding="utf-8") as f:
        lines = f.read().split("\n")

    acts = []
    for i, line in enumerate(lines):
        m = ACTION_RE.match(line)
        if m:
            acts.append((i, m.group(1), m.group(3) or ""))

    bad = []
    for idx, (i, verb, route) in enumerate(acts):
        if verb == "Get":
            continue
        end = acts[idx + 1][0] if idx + 1 < len(acts) else len(lines)
        body = "\n".join(lines[i:end])
        nm = NAME_RE.search(body)
        name = nm.group(1) if nm else "?"
        if name in READ_ONLY_POSTS:
            continue
        if any(k in body for k in GATE_MARKERS):
            continue
        bad.append((i + 1, verb, route, name))
    return bad


def main():
    targets = sys.argv[1:] or WATCHED
    total = 0
    for rel in targets:
        path = rel if os.path.isabs(rel) else os.path.join(ROOT, rel)
        if not os.path.exists(path):
            print(f"⚠️  ไม่พบไฟล์: {rel}")
            continue
        for line, verb, route, name in scan(path):
            total += 1
            print(f"{rel}:{line}: [{verb}] {name} ({route or '/'}) "
                  f"— endpoint ที่เขียนข้อมูลแต่ไม่มีด่านสิทธิ์")

    if total:
        print()
        print(f"❌ พบ {total} จุด — action ที่เขียนข้อมูลต้องผ่านด่านสิทธิ์")
        print("   เอกสาร: DenyDocAsync(...) / DenyKeyAsync(...)")
        print("   เงินเดือน: RequirePayrollWriteAsync(...) / RequireAnyAsync(...)")
        print("   ถ้า POST นี้อ่านอย่างเดียวจริง ให้เพิ่มชื่อเมธอดใน READ_ONLY_POSTS")
        return 1
    print("✅ ทุก endpoint ที่เขียนข้อมูลในคอนโทรลเลอร์ที่เฝ้าอยู่ ผ่านด่านสิทธิ์แล้ว")
    return 0


if __name__ == "__main__":
    sys.exit(main())
