#!/usr/bin/env python3
"""
ตรวจ "เลขผังบัญชีที่ hardcode ไม่ตรงความหมายในผังมาตรฐาน" — ที่มา (บั๊กจริง):
พรีวิว GL ใน PdfGenerationService ใช้ ByCode("21510", "ภาษีหัก ณ ที่จ่ายค้างจ่าย")
แต่ผังมาตรฐาน (ChartOfAccountTemplates) 21510 คือ "เงินมัดจำรับล่วงหน้าค่าห้องพัก"
⇒ พรีวิวโชว์ WHT เข้าบัญชีเงินมัดจำ ผู้ใช้เข้าใจว่าระบบลงบัญชีผิด (JE จริงถูก)

หลักการ: สแกนทุก call รูป `FindAccountAsync(companyId, "CODE")` /
`ByCode("CODE", "ชื่อ fallback")` / `ByCodeChain(new[]{"C1","C2"}, "ชื่อ")` ทั้งเรพ
แล้วเทียบ CODE กับชื่อบัญชีจริงใน ChartOfAccountTemplates.cs:
  • ByCode/ByCodeChain (มีชื่อ fallback แนบมา): ชื่อ fallback กับชื่อ template
    ต้องมี "สาระร่วม" (common substring ไทย ≥ 4 ตัวอักษร ทางใดทางหนึ่ง)
    — ไม่มีเลย = คนละความหมาย = บั๊กคลาสเดียวกับ 21510
  • code ที่ไม่อยู่ใน template (ผังเฉพาะ tenant) → ข้าม (ตัดสินไม่ได้)

ใช้: python3 tools/gl_code_check.py [ไฟล์ ...]   (ไม่ระบุ = Services ทั้งหมด)
exit 1 เมื่อพบปัญหา
"""
import re
import sys
from pathlib import Path

ROOT = Path(__file__).resolve().parent.parent
TEMPLATE = ROOT / 'Accounting/Services/ChartOfAccountTemplates.cs'

# new("21510", "เงินมัดจำรับล่วงหน้าค่าห้องพัก", "Room Deposit...", ...)
TEMPLATE_ROW = re.compile(r'new\(\s*"(\d{3,6})"\s*,\s*"([^"]+)"')
# ByCode("21510", "ภาษีหัก ณ ที่จ่ายค้างจ่าย")
BYCODE = re.compile(r'ByCode\(\s*"(\d{3,6})"\s*,\s*"([^"]+)"')
# ByCodeChain(new[] { "21917", "21916" }, "ภาษีหัก...")  → ตรวจทุก code ใน chain
BYCHAIN = re.compile(r'ByCodeChain\(\s*[^,]*?\{([^}]*)\}\s*,\s*"([^"]+)"', re.S)
CODE_IN_CHAIN = re.compile(r'"(\d{3,6})"')


def load_template():
    src = TEMPLATE.read_text(encoding='utf-8')
    names = {}
    for code, name in TEMPLATE_ROW.findall(src):
        names.setdefault(code, name)   # ตัวแรกชนะ (common ก่อน industry)
    return names


def common_substring_len(a: str, b: str) -> int:
    """ความยาว common substring ที่ยาวที่สุด (ตัดช่องว่าง/จุด/ขีด)"""
    clean = lambda s: re.sub(r'[\s.\-–—()]+', '', s)
    a, b = clean(a), clean(b)
    if not a or not b:
        return 0
    best = 0
    for i in range(len(a)):
        for j in range(i + best + 1, len(a) + 1):
            if a[i:j] in b:
                best = j - i
            else:
                break
    return best


def check_file(f: Path, template: dict):
    src = f.read_text(encoding='utf-8', errors='replace')
    problems = []
    pairs = list(BYCODE.findall(src))
    for chain_codes, name in BYCHAIN.findall(src):
        for code in CODE_IN_CHAIN.findall(chain_codes):
            pairs.append((code, name))
    for code, fallback in pairs:
        tname = template.get(code)
        if not tname:
            continue                    # ไม่อยู่ในผังมาตรฐาน → ตัดสินไม่ได้
        if common_substring_len(fallback, tname) >= 4:
            continue                    # ความหมายสอดคล้อง
        line = next((i for i, l in enumerate(src.splitlines(), 1)
                     if f'"{code}"' in l and (fallback[:8] in l or 'ByCodeChain' in l)), 0)
        problems.append((line, code, fallback, tname))
    return problems


def main():
    template = load_template()
    targets = [Path(a) for a in sys.argv[1:]]
    if not targets:
        targets = [ROOT / 'Accounting/Services']
    scan = []
    for t in targets:
        t = t if t.is_absolute() else (ROOT / t)
        if t.is_file():
            scan.append(t)
        elif t.is_dir():
            scan += sorted(t.rglob('*.cs'))

    bad = 0
    for f in scan:
        for line, code, fallback, tname in check_file(f, template):
            bad += 1
            print(f'{f.relative_to(ROOT)}:{line}: เลขผัง {code} ใช้เป็น "{fallback}" '
                  f'แต่ผังมาตรฐานคือ "{tname}" — คนละความหมาย (บั๊กคลาส 21510-WHT)')

    print(f'\nตรวจ {len(scan)} ไฟล์ · ผังมาตรฐาน {len(template)} บัญชี · พบปัญหา {bad} จุด')
    return 1 if bad else 0


if __name__ == '__main__':
    sys.exit(main())
