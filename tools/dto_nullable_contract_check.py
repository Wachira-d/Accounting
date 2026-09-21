#!/usr/bin/env python3
"""ตรวจ "สัญญาโกหก": DTO ประกาศ string **ไม่ nullable** แต่โค้ดฝั่ง service ปฏิบัติกับมัน
ราวกับว่าเว้นว่างได้ ⇒ ASP.NET ตีกลับเป็นอังกฤษก่อนโค้ดเราได้ทำงาน

ที่มา (ผู้ใช้รายงาน 2026-09-21): กด "💾 บันทึกการตั้งค่า" ที่หน้าตั้งค่าที่พัก แล้วได้
    One or more validation errors occurred. — Code: The Code field is required.
ทั้งที่ช่อง "รหัส" บนฟอร์ม**ไม่มีดอกจัน ไม่มี required** และ `LodgingService` ก็เขียนไว้
ชัดว่า `if (string.IsNullOrWhiteSpace(p.Code)) p.Code = DeriveCode(p.Name);`
— เจตนาคือ "ไม่บังคับ" ทุกชั้น ยกเว้น**ชั้นเดียว**: `public string Code { get; set; } = "";`

เพราะโปรเจกต์เปิด <Nullable>enable</Nullable> ⇒ ASP.NET Core ใส่ `[Required]`
**โดยปริยาย** ให้ property ชนิด reference ที่ไม่มี `?` ทุกตัว (implicit required for
non-nullable reference types) ด่านนี้ทำงาน**ก่อน** service ⇒ ผลสองอย่างพร้อมกัน:
  1. ผู้ใช้เห็นข้อความอังกฤษที่อ้างชื่อ property C# ซึ่งไม่ตรงกับป้ายใด ๆ บนจอ
  2. `BusinessRuleException` ภาษาไทยที่เขียนไว้อย่างดี (เช่น "กรุณาระบุหมายเลขห้อง")
     **ไม่เคยถูกเรียก** ในเคส null — ตรงกับ F2 ข้อ 2 "มี ≠ ถูกเรียก"

═══ ขอบเขตแคบโดยตั้งใจ (F4 ข้อ 1/3) ═══
ไม่ resolve ชนิดใด ๆ — อ่าน **ชนิดที่ประกาศจริง** จากลายเซ็นเมธอด
(`SaveUnitAsync(Guid companyId, LodgingUnitDto dto, ...)` ⇒ `dto` คือ `LodgingUnitDto`)
แล้วจึงตัดสิน เหมือนที่ `service_interface_check.py` อ่านชนิดฟิลด์จากที่ประกาศ
**ห้ามเดาชนิดจากชื่อตัวแปร** — เดาแล้วฟ้องผิด (checker ที่ฟ้องผิด = checker ที่พัง)

ไม่ฟ้องอะไรบ้าง (โดยตั้งใจ):
  • property ที่ประกาศ `string?` อยู่แล้ว — ถูกแล้ว
  • DTO ที่ไม่มีใครรับเป็นพารามิเตอร์เมธอด (response-only) — ไม่มีทางโดนด่าน binding
  • `<param>.<Prop>` ที่ใช้แบบปกติ (ไม่มี `?.` / `??` / IsNullOrWhiteSpace)
"""
import re
import sys
from pathlib import Path

ROOT = Path(__file__).resolve().parent.parent

# `public string Code { get; set; }` (ไม่มี `?` หลัง string) — จับทั้งที่มีและไม่มี initializer
CLASS_PROP = re.compile(
    r"public\s+string\s+(?P<name>[A-Z][A-Za-z0-9_]*)\s*\{\s*get;\s*(set|init);\s*\}")
# `public class LodgingPropertyDto` / `public record CreateDocumentRequest(`
TYPE_DECL = re.compile(
    r"public\s+(?:sealed\s+|abstract\s+|partial\s+)*(?:class|record(?:\s+struct)?)\s+"
    r"(?P<name>[A-Z][A-Za-z0-9_]*)")
# พารามิเตอร์ของเมธอด: `LodgingUnitDto dto` (ชนิดต้องลงท้าย Dto/Request)
PARAM = re.compile(r"\b(?P<type>[A-Z][A-Za-z0-9_]*(?:Dto|Request))\s+(?P<var>[a-z][A-Za-z0-9_]*)\b")
# ลายเซ็นเมธอด — บรรทัดที่มี modifier + วงเล็บเปิด และไม่ใช่การเรียกใช้
SIGNATURE = re.compile(r"^\s*(?:public|private|protected|internal)\s[^;=]*\(")


def strip_noise(text: str) -> str:
    """ตัดคอมเมนต์และเนื้อในสตริง — ตัวอย่างโค้ดในคอมเมนต์ไม่ใช่โค้ดจริง"""
    text = re.sub(r"/\*.*?\*/", "", text, flags=re.S)
    out = []
    for ln in text.splitlines():
        ln = re.sub(r"//.*$", "", ln)
        ln = re.sub(r'@"(?:[^"]|"")*"', '""', ln)
        ln = re.sub(r'"(?:\\.|[^"\\])*"', '""', ln)
        out.append(ln)
    return "\n".join(out)


def dto_nonnullable_strings(dto_root: Path) -> dict:
    """{ชื่อชนิด: {ชื่อ property ที่เป็น string ไม่ nullable}}"""
    result = {}
    for f in sorted(dto_root.rglob("*.cs")):
        body = strip_noise(f.read_text(encoding="utf-8", errors="replace"))
        current = None
        lines = body.splitlines()
        for idx, ln in enumerate(lines):
            m = TYPE_DECL.search(ln)
            if m:
                current = m.group("name")
                result.setdefault(current, set())
                # positional record: `record Foo(string Bar, string? Baz)`
                # ⚠️ ลายเซ็นข้ามบรรทัดได้และ**ส่วนใหญ่ข้าม** — เวอร์ชันแรก (รอบ 187)
                # อ่านแค่ส่วนที่เหลือของบรรทัดเดียวกัน ⇒ `CreateProjectRequest`
                # (พารามิเตอร์อยู่บรรทัดที่ 2-3) คืน None ⇒ checker **เขียวปลอม**
                # และบั๊ก P0 จริง 6 ตัวซ่อนอยู่ในจุดบอดนี้ทั้งหมด
                # (ทีมตรวจรอบ 189 A14 — "checker ที่ครอบไม่ถึง = ไม่มีด่าน" F2 ข้อ 6)
                head = ln[m.end():]
                if "(" in head:
                    depth, buf, k = 0, [], idx
                    while k < len(lines):
                        seg = lines[k] if k > idx else head
                        for ch in seg:
                            if ch == "(":
                                depth += 1
                                if depth == 1:
                                    continue
                            elif ch == ")":
                                depth -= 1
                                if depth == 0:
                                    break
                            if depth >= 1:
                                buf.append(ch)
                        if depth == 0 and buf:
                            break
                        k += 1
                    head = "".join(buf)
                for pm in re.finditer(r"(?<![?\w])string\s+([A-Z][A-Za-z0-9_]*)", head):
                    result[current].add(pm.group(1))
                continue
            if current is None:
                continue
            pm = CLASS_PROP.search(ln)
            if pm:
                result[current].add(pm.group("name"))
    return {k: v for k, v in result.items() if v}


def method_bodies(body: str):
    """คืน (บรรทัดเริ่ม, ข้อความลายเซ็น, เนื้อเมธอด) ทีละเมธอด ด้วยการนับวงเล็บปีกกา"""
    lines = body.splitlines()
    i = 0
    while i < len(lines):
        if not SIGNATURE.match(lines[i]):
            i += 1
            continue
        sig_start = i
        sig = lines[i]
        # ลายเซ็นข้ามบรรทัดได้ — ไล่จนเจอ `{` หรือ `;`/`=>`
        j = i
        while j < len(lines) and "{" not in lines[j] and ";" not in lines[j]:
            j += 1
            if j < len(lines):
                sig += " " + lines[j]
        if j >= len(lines) or "{" not in lines[j]:
            i = j + 1
            continue
        depth, k, buf = 0, j, []
        started = False
        while k < len(lines):
            buf.append(lines[k])
            for ch in lines[k]:
                if ch == "{":
                    depth += 1
                    started = True
                elif ch == "}":
                    depth -= 1
            if started and depth <= 0:
                break
            k += 1
        # ฐานบรรทัดต้องเป็นบรรทัดแรกของ `buf` (= lines[j]) ไม่ใช่บรรทัดลายเซ็น —
        # ลายเซ็นข้ามบรรทัดได้ ถ้าใช้ sig_start เลขบรรทัดที่ฟ้องจะคลาดไปเท่ากับ
        # ความยาวลายเซ็น แล้วคนเปิดไปเจอบรรทัดที่ไม่เกี่ยวข้อง
        yield j + 1, sig, "\n".join(buf)
        i = k + 1


def tolerates_blank(method: str, after: int) -> bool:
    """`IsNullOrWhiteSpace(dto.X)` แล้วยังไงต่อ — **ตรงนี้คือหัวใจของ checker**

    สองทรงที่หน้าตาเหมือนกันแต่คนละเรื่องโดยสิ้นเชิง:

      (ก) `if (IsNullOrWhiteSpace(dto.X)) throw new BusinessRuleException("กรุณา…");`
          ⇒ service ก็ถือว่า **บังคับ** — สองชั้นเห็นตรงกัน ไม่ใช่สัญญาที่ขัดกัน
          (ต่างกันแค่ "ใครเป็นคนพูด" ซึ่งตอนนี้ `ValidationErrorText` ทำให้ทั้งสอง
           ทางเป็นไทยที่ระบุช่องได้แล้ว) → **ไม่ฟ้อง** มิฉะนั้น checker จะแดงวันแรก
          10 จุดแล้วต้องมี baseline ทั้งที่ไม่มีบั๊ก (F2 ข้อ 6 "checker ที่ฟ้องผิด
          = checker ที่พัง")

      (ข) `if (IsNullOrWhiteSpace(dto.X)) dto.X = Derive(...);` หรือ
          `x.Code = IsNullOrWhiteSpace(dto.Code) ? Derive(...) : dto.Code.Trim();`
          ⇒ service ถือว่า **ไม่บังคับ** (เว้นว่างได้ เดี๋ยวเติมให้) แต่ DTO ประกาศ
          ว่าบังคับ ⇒ ผู้ใช้โดนตีกลับก่อนถึงตัวเติมเสมอ = **บั๊กที่ผู้ใช้รายงาน**
          → ฟ้อง

    ตัดสินจากข้อความถัดไปไม่เกิน 2 บรรทัด — `throw` = (ก) นอกนั้น = (ข)
    """
    tail = method[after:after + 400]
    head = "\n".join(tail.split("\n")[:3])
    # ปฏิเสธคำขอได้สองทรงในเรพนี้ — `throw new BusinessRuleException(...)` (service)
    # และ `return BadRequest(new ApiResponse<...>(false, null, "กรุณา…"))` (controller)
    # ทั้งคู่แปลว่า "ชั้นนี้ก็ถือว่าบังคับ" เหมือนกัน — ถ้าไม่นับทรงที่สอง checker จะ
    # ฟ้อง 3 controller ที่ไม่มีบั๊ก (ตรวจจริงแล้ว 2026-09-21: TestEmailRequest ×2 ·
    # ApplyJournalDepositRequest) = checker ที่ฟ้องผิด
    rejects = re.search(r"\bthrow\b|return\s+(BadRequest|Conflict|UnprocessableEntity|Problem|ValidationProblem)\s*\(",
                        head)
    return rejects is None


def check_file(path: Path, props: dict):
    problems = []
    body = strip_noise(path.read_text(encoding="utf-8", errors="replace"))
    for line_no, sig, method in method_bodies(body):
        params = {m.group("var"): m.group("type")
                  for m in PARAM.finditer(sig) if m.group("type") in props}
        if not params:
            continue
        for var, dto_type in params.items():
            v = re.escape(var)
            patterns = [
                (rf"IsNullOrWhiteSpace\(\s*{v}\.([A-Z][A-Za-z0-9_]*)\s*\)", "IsNullOrWhiteSpace"),
                (rf"IsNullOrEmpty\(\s*{v}\.([A-Z][A-Za-z0-9_]*)\s*\)", "IsNullOrEmpty"),
                (rf"\b{v}\.([A-Z][A-Za-z0-9_]*)\?\.", "?."),
                (rf"\b{v}\.([A-Z][A-Za-z0-9_]*)\s*\?\?", "??"),
            ]
            for pat, how in patterns:
                for m in re.finditer(pat, method):
                    prop = m.group(1)
                    if prop not in props[dto_type]:
                        continue
                    # ใช้เกณฑ์เดียวกันกับ **ทุกรูปแบบ** — `?.`/`??` ก็เป็นการ "รับ null
                    # ไว้ก่อนเพื่อจะปฏิเสธด้วยข้อความที่ดีกว่า" ได้เหมือนกัน
                    # (เช่น `var p = request.Provider?.Trim() ?? ""; if (!IsSupported(p)) throw …`)
                    # เวอร์ชันแรกฟ้อง `?.`/`??` แบบไม่มีเงื่อนไข ⇒ ฟ้องผิด 15 จุด
                    # รวม `LoginRequest.Email` ที่ฟอร์มบังคับอยู่แล้ว (ทีมตรวจรอบ 189)
                    if not tolerates_blank(method, m.end()):
                        continue   # ชั้นนี้ก็ปฏิเสธเอง — สองชั้นเห็นตรงกัน ไม่ใช่บั๊ก
                    off = method[:m.start()].count("\n")
                    problems.append((line_no + off, dto_type, var, prop, how))
    # ยุบซ้ำ (property เดียวอาจโดนหลายรูปแบบในเมธอดเดียว)
    seen, uniq = set(), []
    for p in problems:
        key = (p[1], p[3])
        if key in seen:
            continue
        seen.add(key)
        uniq.append(p)
    return uniq


SELF_TEST_DTO = """namespace Accounting.Models.DTOs;
public class FakeThingDto
{
    public string Name { get; set; } = "";
    public string Code { get; set; } = "";
    public string? Nickname { get; set; }
}
"""
SELF_TEST_SVC = """namespace Accounting.Services;
public class FakeService
{
    public async Task SaveAsync(Guid companyId, FakeThingDto dto, string userId)
    {
        if (string.IsNullOrWhiteSpace(dto.Code)) dto.Code = "AUTO";   // ← ต้องฟ้อง
        var nick = dto.Nickname?.Trim();                              // ← nullable แล้ว ไม่ฟ้อง
        if (string.IsNullOrWhiteSpace(dto.Name))                      // ← ด่านเราเองว่าบังคับ
            throw new BusinessRuleException("กรุณาระบุชื่อ");          //    สองชั้นตรงกัน ไม่ฟ้อง
        // if (string.IsNullOrWhiteSpace(dto.Name)) { }               ← คอมเมนต์ ต้องไม่ฟ้อง
        await Task.CompletedTask;
    }
    public IActionResult Reject(FakeThingDto dto)
    {
        if (string.IsNullOrWhiteSpace(dto.Name))                      // ← controller ปฏิเสธเอง
            return BadRequest("กรุณาระบุชื่อ");                        //    สองชั้นตรงกัน ไม่ฟ้อง
        return Ok();
    }
    public void Unrelated(string s) { var x = s?.Trim(); }            // ไม่ใช่ DTO ไม่ฟ้อง
}
"""


def self_test() -> int:
    """negative test 4 ทิศ — ด่านที่ไม่มีเทสต์ทิศตรงข้าม = ไม่มีด่าน (F2 ข้อ 6)"""
    import tempfile
    ok = True
    with tempfile.TemporaryDirectory() as d:
        root = Path(d)
        dto_dir = root / "DTOs"
        dto_dir.mkdir(parents=True)
        (dto_dir / "FakeDtos.cs").write_text(SELF_TEST_DTO, encoding="utf-8")
        svc = root / "FakeService.cs"
        svc.write_text(SELF_TEST_SVC, encoding="utf-8")

        props = dto_nonnullable_strings(dto_dir)
        if props.get("FakeThingDto") != {"Name", "Code"}:
            print(f"❌ self-test: อ่าน property ผิด — ได้ {props.get('FakeThingDto')}")
            ok = False
        hits = {p[3] for p in check_file(svc, props)}
        if "Code" not in hits:
            print("❌ self-test: ไม่จับ property ที่ไม่ nullable แต่ถูกเช็ค IsNullOrWhiteSpace")
            ok = False
        if "Nickname" in hits:
            print("❌ self-test: ฟ้อง property ที่ประกาศ string? ไว้แล้ว (false positive)")
            ok = False
        if "Name" in hits:
            print("❌ self-test: ฟ้อง property ที่ service ก็โยน throw (สองชั้นเห็นตรงกัน)")
            ok = False
    print("✅ self-test ผ่าน 5 ทิศ (จับ · nullable แล้ว · throw · return BadRequest · คอมเมนต์)"
          if ok else "❌ self-test ไม่ผ่าน")
    return 0 if ok else 1


def main():
    if "--self-test" in sys.argv:
        return self_test()

    props = dto_nonnullable_strings(ROOT / "Accounting" / "Models" / "DTOs")
    targets = [Path(a) for a in sys.argv[1:] if not a.startswith("--")]
    if not targets:
        targets = sorted((ROOT / "Accounting" / "Services").rglob("*.cs")) \
            + sorted((ROOT / "Accounting" / "Controllers").rglob("*.cs"))

    total = 0
    for path in targets:
        if not path.is_file() or path.suffix != ".cs":
            continue
        for line_no, dto_type, var, prop, how in check_file(path, props):
            total += 1
            rel = path.relative_to(ROOT) if ROOT in path.parents else path
            print(f"{rel}:{line_no}: `{var}.{prop}` ใช้แบบ \"{how}\" (ยอมรับค่าว่าง) "
                  f"แต่ {dto_type}.{prop} ประกาศเป็น `string` ไม่ใช่ `string?`")
            print(f"    → ASP.NET ใส่ [Required] โดยปริยาย ⇒ ตีกลับเป็นอังกฤษก่อนถึงบรรทัดนี้ "
                  f"· แก้เป็น `public string? {prop} {{ get; set; }}` ถ้าเว้นว่างได้จริง")

    print(f"\nตรวจ {len(targets)} ไฟล์ · DTO {len(props)} ชนิด · สัญญาที่ขัดกัน {total} จุด")
    return 1 if total else 0


if __name__ == "__main__":
    sys.exit(main())
