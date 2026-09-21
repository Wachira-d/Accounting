#!/usr/bin/env python3
"""ตรวจ CS1061: controller เรียกเมธอดที่ **ไม่มีใน interface ของ service นั้น**

ที่มา (ของจริง 2026-09-21): เพิ่ม `ReclassifyForeignServiceAsync` ลง
`DocumentService` + endpoint ใน `DocumentController` แล้ว **ลืมประกาศใน
`IDocumentService`** ⇒ controller ถือตัวแปรเป็น interface จึงคอมไพล์ไม่ผ่าน
(`CS1061`) และลาก `Accounting.Tests` ล้มตามด้วย `CS0006` (Accounting.dll
ไม่ถูกสร้าง) — env นี้ไม่มี .NET SDK ⇒ เสียรอบ CI ไปหนึ่งรอบเต็มเพื่อรู้เรื่องนี้

คู่สมมาตรที่หายไปหนึ่งขา: impl + endpoint ครบ แต่ interface ขาด — ตรงกับ F2 ข้อ 1
("คู่สมมาตรต้องอ่านอีกฝั่งทันที") ซึ่งเป็นต้นเหตุอันดับต้น ๆ ของการถดถอยในเรพนี้

═══ ขอบเขตแคบโดยตั้งใจ (F4 ข้อ 1/3) ═══
ไม่พยายาม resolve ชนิดใด ๆ — จับเฉพาะรูปแบบตรงตัวที่ไม่ต้องรู้ type:
  • อ่าน **ชนิดที่ประกาศจริง** ของฟิลด์ (`private readonly IXxx _yyy;`) —
    **ห้ามเดาชื่อ interface จากชื่อฟิลด์** (รอบแรกเดาแบบนั้นแล้วฟ้องผิด 2 จุด:
    `_quotaService` ชนิดจริงคือ `ICmsQuotaService` · `_emailService` คือ
    `IDocumentEmailService`)
  • หาไฟล์ `Services/Interfaces/<ชนิด>.cs` — **ไม่มีไฟล์ = ข้าม**
    (service ที่ inject เป็นคลาสตรง ๆ หรือ interface อยู่ที่อื่น ไม่มีปัญหานี้)
  • ฟ้องเมื่อชื่อเมธอดนั้น **ไม่ปรากฏเลยแม้แต่ครั้งเดียว** ในไฟล์ interface

เงื่อนไข "ไม่ปรากฏเลย" หลวมโดยตั้งใจ — ชื่อที่โผล่ในคอมเมนต์ก็นับว่าผ่าน เพื่อให้
false positive เป็นศูนย์ (checker ที่ฟ้องผิด = checker ที่พัง — F2 ข้อ 6)
"""
import re
import sys
from pathlib import Path

ROOT = Path(__file__).resolve().parent.parent
CALL = re.compile(r"(?P<field>_[A-Za-z0-9_]+)\.(?P<method>[A-Z][A-Za-z0-9_]*)\s*\(")
# `private readonly IDocumentService _documentService;` — ชนิดที่ประกาศจริงเท่านั้น
FIELD_DECL = re.compile(
    r"\breadonly\s+(?P<type>I[A-Z][A-Za-z0-9_]*)\s+(?P<field>_[A-Za-z0-9_]+)\s*;")


def interface_path(type_name: str) -> Path | None:
    """`IDocumentService` → `Services/Interfaces/IDocumentService.cs` (ถ้ามี)"""
    p = ROOT / "Accounting" / "Services" / "Interfaces" / f"{type_name}.cs"
    return p if p.is_file() else None


def strip_comments(text: str) -> str:
    """ตัด `//` และ `/* */` — คอมเมนต์ที่ยกตัวอย่างโค้ดไม่ใช่การเรียกจริง"""
    text = re.sub(r"/\*.*?\*/", "", text, flags=re.S)
    return "\n".join(re.sub(r"//.*$", "", ln) for ln in text.splitlines())


def check_file(path: Path, cache: dict):
    problems = []
    body = strip_comments(path.read_text(encoding="utf-8", errors="replace"))
    fields = {m.group("field"): m.group("type") for m in FIELD_DECL.finditer(body)}
    if not fields:
        return problems
    for line_no, line in enumerate(body.splitlines(), 1):
        for m in CALL.finditer(line):
            field, method = m.group("field"), m.group("method")
            type_name = fields.get(field)
            if type_name is None:
                continue
            svc = type_name
            iface = interface_path(type_name)
            if iface is None:
                continue
            if iface not in cache:
                cache[iface] = iface.read_text(encoding="utf-8", errors="replace")
            if method not in cache[iface]:
                problems.append((line_no, line.strip(), svc, method, iface))
    return problems


SELF_TEST_IFACE = """namespace Accounting.Services.Interfaces;
public interface IFakeService
{
    Task<int> ExistingMethodAsync(Guid id);
}
"""
SELF_TEST_CTRL = """namespace Accounting.Controllers;
public class FakeController
{
    private readonly IFakeService _fakeService;
    private readonly ISomethingElse _other;
    public async Task A() { await _fakeService.ExistingMethodAsync(Guid.Empty); }
    public async Task B() { await _fakeService.MissingMethodAsync(Guid.Empty); }
    // await _fakeService.AlsoMissingButCommentedAsync(x);  ← คอมเมนต์ ต้องไม่ฟ้อง
    public async Task C() { await _other.NotOurProblemAsync(); }  // ไม่มีไฟล์ interface = ข้าม
}
"""


def self_test() -> int:
    """negative test 4 ทิศ — ด่านที่ไม่มีเทสต์ทิศตรงข้าม = ไม่มีด่าน (F2 ข้อ 6)"""
    import tempfile
    ok = True
    with tempfile.TemporaryDirectory() as d:
        root = Path(d)
        (root / "Accounting" / "Services" / "Interfaces").mkdir(parents=True)
        (root / "Accounting" / "Controllers").mkdir(parents=True)
        (root / "Accounting" / "Services" / "Interfaces" / "IFakeService.cs").write_text(
            SELF_TEST_IFACE, encoding="utf-8")
        ctrl = root / "Accounting" / "Controllers" / "FakeController.cs"
        ctrl.write_text(SELF_TEST_CTRL, encoding="utf-8")

        global ROOT
        real_root, ROOT = ROOT, root
        try:
            found = check_file(ctrl, {})
        finally:
            ROOT = real_root

        names = {p[3] for p in found}
        if "MissingMethodAsync" not in names:
            print("❌ self-test: ไม่จับเมธอดที่ขาดจาก interface"); ok = False
        if "ExistingMethodAsync" in names:
            print("❌ self-test: ฟ้องเมธอดที่มีอยู่จริง (false positive)"); ok = False
        if "AlsoMissingButCommentedAsync" in names:
            print("❌ self-test: ฟ้องบรรทัดที่เป็นคอมเมนต์"); ok = False
        if "NotOurProblemAsync" in names:
            print("❌ self-test: ฟ้อง service ที่ไม่มีไฟล์ interface ในโฟลเดอร์นี้"); ok = False
    print("✅ self-test ผ่าน 4 ทิศ" if ok else "❌ self-test ไม่ผ่าน")
    return 0 if ok else 1


def main():
    if "--self-test" in sys.argv:
        return self_test()

    targets = [Path(a) for a in sys.argv[1:] if not a.startswith("--")]
    if not targets:
        targets = sorted((ROOT / "Accounting" / "Controllers").rglob("*.cs"))

    cache, total = {}, 0
    for path in targets:
        if "Controllers" not in path.parts:
            continue
        for line_no, text, svc, method, iface in check_file(path, cache):
            total += 1
            rel = path.relative_to(ROOT) if ROOT in path.parents else path
            print(f"{rel}:{line_no}: `{svc}.{method}(...)` — ไม่มี `{method}` ใน "
                  f"{iface.relative_to(ROOT)}\n    {text}")
            print(f"    → ประกาศเมธอดนี้ใน interface ด้วย (impl + endpoint ครบแต่ interface ขาด "
                  f"= CS1061 + CS0006 ลากเทสต์ล้มตาม)")

    print(f"\nตรวจ controller {len(targets)} ไฟล์ · เมธอดที่ไม่มีใน interface {total} จุด")
    return 1 if total else 0


if __name__ == "__main__":
    sys.exit(main())
