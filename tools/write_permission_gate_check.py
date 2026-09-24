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
    # เพิ่มรอบ 184 — ทุก write ที่นี่แตะเงินสดในลิ้นชักและสต็อกจริง (คืนเงิน ·
    # ยกเลิกบิล · แก้ราคา · ปิดกะ) แต่มีแค่ [Authorize] ระดับคลาสมาตลอด
    # ⇒ แคชเชียร์คนไหนก็คืนเงิน/ปิดกะแทนกันได้ และ checker มองไม่เห็นเพราะ
    # ไฟล์นี้ไม่เคยอยู่ในลิสต์ (บทเรียนซ้ำรอบที่ 5 ของ "allow-list ครบไหม ≠ ผ่านไหม")
    "Accounting/Controllers/PosController.cs",
    # เพิ่มรอบ 184 — มีด่านครบอยู่แล้วทุกจุด แต่ไม่เคยถูกเฝ้า ⇒ ใครถอดด่านออก
    # พรุ่งนี้ก็ไม่มีอะไรฟ้อง (ratchet: ใส่ตอนที่ยังเขียว = ล็อกไว้ไม่ให้ถอยหลัง)
    "Accounting/Controllers/LodgingController.cs",
    # เพิ่มรอบ 184 — ทางเข้า "นำเข้าไฟล์" เขียน JE · การชำระเงิน · สินทรัพย์ · ยอดยกมา ·
    # ทะเบียนพนักงาน ได้เป็นพันแถวในคำสั่งเดียว และ /export โหลด CitizenId + เลขบัญชี +
    # เงินเดือนทั้งบริษัท แต่มีแค่ [Authorize] ระดับคลาสมาตลอด
    # ("allow-list ครบไหม ≠ ผ่านไหม" — รอบที่ 6 ของบทเรียนเดียวกัน)
    "Accounting/Controllers/ImportExportController.cs",
    # เพิ่มรอบ 190 — แนบ/ลบไฟล์หลักฐานของเอกสาร (หลักฐานประกอบรายการบัญชีที่ต้องเก็บ 5 ปี
    # ตาม พ.ร.บ.การบัญชี ม.10) มีแค่ [Authorize] ระดับคลาสมาตลอด ⇒ สมาชิกคนไหนก็ลบหลักฐาน
    # ของใบที่อนุมัติแล้วได้ · รอบ 193: ครอบทุกชนิด (DenyAttachmentAsync — แนบ/ลบ/ดู/ดาวน์โหลด)
    "Accounting/Controllers/FileAttachmentController.cs",
]

# ตัวบ่งชี้ว่า action นี้ผ่านด่านสิทธิ์บางอย่างแล้ว
GATE_MARKERS = (
    "DenyDocAsync",
    # ด่านไฟล์แนบทุกชนิด (รอบ 193 ข้อ 29) — ตาราง "ชนิด → คีย์ของโมดูล" อยู่ที่ Helpers/AttachmentPermissionScope ·
    # ชนิดที่ไม่รู้จัก = ห้ามเขียน
    "DenyAttachmentAsync",
    "DenyKeyAsync",
    # ด่านของเส้นนำเข้า/ส่งออกไฟล์ (รอบ 184) — เป็น **เมธอด** ไม่ใช่ attribute เพราะ
    # คีย์ที่ต้องใช้ขึ้นกับ `entityType` ใน body ซึ่ง attribute คงที่มองไม่เห็น
    # (ทะเบียนพนักงานต้องเข้มกว่าผู้ติดต่อ) · เรียก `HasPermissionAsync` ภายใน
    "DenyAsync",
    "DocumentPermissionHelper",
    "RequirePayrollWriteAsync",
    "RequireBillingAsync",
    "RequireEtaxAsync",
    "RequireAssetAsync",
    "RequireAnyAsync",
    "HasPermissionAsync",
    "UserRole.Owner",          # ด่าน Owner-only (purge)
    "IsInRole(\"SystemAdmin\")",
    # ด่านแบบ attribute ที่บังคับ "คีย์สิทธิ์" (ไม่ใช่แค่ล็อกอิน) — `Filters/
    # RequirePermissionAttribute` · เพิ่มรอบ 184 พร้อม PosController
    # ⚠️ ถ้าไม่มีบรรทัดนี้ `LodgingController` (มีด่านครบ 41 จุด) จะถูกฟ้องผิดทันที
    # ที่ใครเพิ่มมันเข้า WATCHED — checker ที่ฟ้องผิด = checker ที่พัง (F2 ข้อ 6)
    "RequirePermission(",
)

# `[Authorize(Roles = "…")]` / `[Authorize(Policy = "…")]` **บน action** ก็เป็นด่าน
# — ต่างจาก `[Authorize]` เปล่า ๆ ซึ่งตอบแค่ "ล็อกอินอยู่ไหม" (บทเรียนใน CLAUDE.md).
# ไม่นับ attribute ระดับ**คลาส** เพราะ scan() อ่านเฉพาะช่วงของ action อยู่แล้ว
ATTR_GATE_RE = re.compile(r'\[Authorize\s*\(\s*(Roles|Policy)\s*=')

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

    # ── attribute ที่วาง**เหนือ** [Http…] เป็นของ action ตัวล่าง ไม่ใช่ตัวบน ──
    # C# เขียนได้ทั้งสองแบบ: `[RequirePermission] [HttpPost]` และ `[HttpPost]
    # [RequirePermission]` · ถ้านับช่วงจาก [Http…] ลงไปอย่างเดียว attribute แบบแรก
    # จะถูกนับให้ action **ก่อนหน้า** ⇒ action ที่ไม่มีด่านจะเขียว และ action ที่มี
    # ด่านจะถูกฟ้อง — ผิดทั้งสองทิศพร้อมกัน (F2 ข้อ 6 "checker ที่ฟ้องผิด = checker ที่พัง")
    def attr_start(i):
        j = i
        while j > 0 and lines[j - 1].lstrip().startswith("["):
            j -= 1
        return j

    starts = [attr_start(i) for i, _, _ in acts]

    bad = []
    for idx, (i, verb, route) in enumerate(acts):
        if verb == "Get":
            continue
        end = starts[idx + 1] if idx + 1 < len(acts) else len(lines)
        body = "\n".join(lines[starts[idx]:end])
        nm = NAME_RE.search(body)
        name = nm.group(1) if nm else "?"
        if name in READ_ONLY_POSTS:
            continue
        if any(k in body for k in GATE_MARKERS) or ATTR_GATE_RE.search(body):
            continue
        bad.append((i + 1, verb, route, name))
    return bad


SELF_TEST_SRC = """
public class FakeController : ControllerBase
{
    [HttpGet("x")]
    public async Task<IActionResult> ReadThing() => Ok();

    [HttpPost("a")]
    [RequirePermission(PermissionKeys.PosCashier)]
    public async Task<IActionResult> AttrBelowHttp() => Ok();

    [RequirePermission(PermissionKeys.PosRefund)]
    [HttpPost("b")]
    public async Task<IActionResult> AttrAboveHttp() => Ok();

    [HttpPost("c")]
    public async Task<IActionResult> NoGateAtAll() => Ok();

    [HttpPut("d")]
    public async Task<IActionResult> GateInBody()
    {
        await DenyKeyAsync("x");
        return Ok();
    }
}
"""


def self_test():
    """ใส่บั๊กกลับแล้วต้องจับได้ — 3 ทิศ:
    1. endpoint ที่ไม่มีด่านเลย ต้องถูกฟ้อง
    2. endpoint ที่มี [RequirePermission] **ใต้** [Http…] ต้องไม่ถูกฟ้อง
    3. endpoint ที่มี [RequirePermission] **เหนือ** [Http…] ต้องไม่ถูกฟ้อง
       (และต้องไม่ทำให้ action ก่อนหน้าเขียวไปด้วย)
    """
    import tempfile
    ok = True
    with tempfile.NamedTemporaryFile("w", suffix=".cs", delete=False, encoding="utf-8") as f:
        f.write(SELF_TEST_SRC)
        tmp = f.name
    try:
        found = {name for _, _, _, name in scan(tmp)}
    finally:
        os.unlink(tmp)

    expect_bad = {"NoGateAtAll"}
    expect_ok = {"AttrBelowHttp", "AttrAboveHttp", "GateInBody", "ReadThing"}
    missing = expect_bad - found
    wrong = found & expect_ok
    if missing:
        print(f"❌ self-test: ควรฟ้องแต่ไม่ฟ้อง {sorted(missing)}")
        ok = False
    if wrong:
        print(f"❌ self-test: ฟ้องผิด (endpoint ที่มีด่านแล้ว) {sorted(wrong)}")
        ok = False
    if ok:
        print("✅ self-test ผ่าน 3 ทิศ (ไม่มีด่าน=ฟ้อง · attr ใต้/เหนือ [Http…]=ไม่ฟ้อง)")
    return 0 if ok else 1


def main():
    if "--self-test" in sys.argv:
        return self_test()
    targets = [a for a in sys.argv[1:] if not a.startswith("--")] or WATCHED
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
