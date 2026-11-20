#!/usr/bin/env python3
"""main_guard_tail_check — โค้ดระดับโมดูลที่อยู่หลัง `if __name__ == "__main__":` ไม่เคยถูกรันก่อน main()

ที่มา (รอบ 275 แพ็ก C · คอมมิต 234d5b3e · ตัวตรวจนี้ ea8b0253): กติกา `_R275C_` ใน tools/required_call_site_check.py ถูกต่อท้ายไฟล์ "หลัง" guard ⇒
`sys.exit(main())` จบโปรแกรมก่อนบรรทัด `RULES += [...]` จะถูกประเมิน ⇒ ด่าน 20+ กติกาไม่เคยตรวจอะไร แต่ตัวตรวจรายงาน "ผ่าน" ทุกครั้ง
(กฎเหล็ก #4 F2 ข้อ 6: ด่านที่ไม่ถูกรัน = ไม่มีด่าน)

กติกา (ทุกไฟล์ tools/**/*.py): มี guard ได้ไม่เกิน 1 จุด · ทุกบรรทัดหลัง guard ต้องเป็นบรรทัดว่าง/คอมเมนต์/บรรทัดที่เยื้อง (ตัวของ guard) ·
บรรทัดระดับโมดูลใด ๆ (`RULES += ...` · `def ...` · `X = ...`) หลัง guard = ฟ้อง ⇒ ย้าย guard ไปท้ายไฟล์
self-test ในตัว: ใส่บั๊กกลับ (บล็อกหลัง guard · guard สองจุด) ต้องฟ้อง · ไฟล์ที่ถูกต้องต้องไม่ฟ้อง
"""
import re
import sys
from pathlib import Path

ROOT = Path(__file__).resolve().parent
GUARD = re.compile(r"^if\s+__name__\s*==\s*['\"]__main__['\"]\s*:")


def errors_of(text: str, rel: str) -> list:
    lines = text.split("\n")
    idx = [i for i, l in enumerate(lines) if GUARD.match(l)]
    if not idx:
        return []
    errs = []
    if len(idx) > 1:
        errs.append(f"{rel}: มี `if __name__ == \"__main__\"` {len(idx)} จุด (บรรทัด {', '.join(str(i + 1) for i in idx)}) — ให้เหลือจุดเดียวท้ายไฟล์")
    in_string = None
    for n in range(idx[0] + 1, len(lines)):
        line = lines[n]
        if in_string:
            if in_string in line:
                in_string = None
            continue
        if not line.strip() or line.lstrip().startswith("#"):
            continue
        if line[0] in " \t":
            # docstring/สตริงหลายบรรทัดในตัว guard — ข้ามจนปิด (บรรทัดในนั้นอาจไม่เยื้อง)
            for q in ('"""', "'''"):
                if line.count(q) % 2 == 1:
                    in_string = q
            continue
        if GUARD.match(line):
            continue
        errs.append(f"{rel}:{n + 1}: โค้ดระดับโมดูลหลัง `if __name__ == \"__main__\"` (บรรทัด {idx[0] + 1}) ไม่ถูกรันก่อน main() — "
                    f"`{line.strip()[:60]}` · ย้าย guard ไปท้ายไฟล์")
        break
    return errs


def self_test() -> list:
    fails = []
    ok = 'RULES = []\nRULES += [1]\n\ndef main():\n    return 0\n\n\nif __name__ == "__main__":\n    # คอมเมนต์\n    sys.exit(main())\n'
    if errors_of(ok, "ok.py"):
        fails.append("self-test: ไฟล์ที่ guard อยู่ท้ายไฟล์ถูกฟ้อง")
    bad_tail = ok + '\n\nRULES += [2]\n'
    if not errors_of(bad_tail, "bad.py"):
        fails.append("self-test: `RULES += [...]` หลัง guard ไม่ฟ้อง (บั๊ก 234d5b3e)")
    bad_def = ok + 'def later():\n    pass\n'
    if not errors_of(bad_def, "bad.py"):
        fails.append("self-test: `def` หลัง guard ไม่ฟ้อง")
    two = ok + 'if __name__ == "__main__":\n    main()\n'
    if not errors_of(two, "two.py"):
        fails.append("self-test: guard สองจุดไม่ฟ้อง")
    doc = 'if __name__ == "__main__":\n    print("""\nไม่เยื้อง\n""")\n'
    if errors_of(doc, "doc.py"):
        fails.append("self-test: สตริงหลายบรรทัดในตัว guard ถูกฟ้องผิด")
    return fails


def main() -> int:
    errs = []
    files = sorted(p for p in ROOT.rglob("*.py") if "__pycache__" not in p.parts)
    for p in files:
        errs += errors_of(p.read_text(encoding="utf-8"), str(p.relative_to(ROOT.parent)))
    errs += self_test()
    for e in errs:
        print("❌ " + e)
    if errs:
        return 1
    print(f"main_guard_tail_check: {len(files)} ไฟล์ — ไม่มีโค้ดระดับโมดูลหลัง guard (+ self-test 5 เคส)")
    return 0


if __name__ == "__main__":
    sys.exit(main())
