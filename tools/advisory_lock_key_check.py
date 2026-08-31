#!/usr/bin/env python3
# -*- coding: utf-8 -*-
"""ตรวจว่าคีย์ของ pg_advisory_xact_lock มาจากฟังก์ชันที่ deterministic

═══ ที่มา ═══
`HashCode.Combine(...)` และ `string.GetHashCode()` ใน .NET **สุ่ม seed ใหม่ทุก
process** (Marvin hash) ⇒ instance A กับ instance B ได้คีย์คนละค่าสำหรับ
ทรัพยากรเดียวกัน ⇒ advisory lock **ไม่กันกันเลย** ทั้งที่โค้ดอ่านแล้วเหมือนได้
ป้องกันไว้แล้ว. อาการที่ผู้ใช้เจอ: เลขเอกสาร/เลข JE ซ้ำ · ยอดสต็อกหายตอนปรับ
พร้อมกัน · รายการธนาคารถูกจับคู่สองครั้ง — ทั้งหมดเกิดเฉพาะตอนมีหลาย instance
จึงไม่โผล่ตอนทดสอบเครื่องเดียว

เคยแก้ไปแล้ว 1 จุด (JournalEntryBuilder) แล้วเหลืออีก 6 จุด = defect class
"แก้ตัวเดียว เหลือที่เหลือ" → checker นี้กันตัวที่ 8

กติกา: คีย์ทุกตัวต้องมาจาก `Helpers/AdvisoryLockKey.For(...)`
"""
import os
import re
import sys

ROOT = os.path.dirname(os.path.dirname(os.path.abspath(__file__)))
SRC = os.path.join(ROOT, "Accounting")

LOCK_CALL = re.compile(r'pg_advisory(?:_xact)?_lock\(\{0\}\)"\s*,\s*([A-Za-z_]\w*)\s*\)')
BAD_RHS = re.compile(r'HashCode\.Combine|\.GetHashCode\s*\(')


def find_assignment(lines, upto_idx, name):
    """ไล่ขึ้นไปหาบรรทัดที่กำหนดค่าให้ตัวแปรนี้ (ในระยะที่สมเหตุสมผล)"""
    pat = re.compile(rf'(?:\bvar\s+|\blong\s+|\bint\s+)?\b{re.escape(name)}\s*=(?!=)')
    for i in range(upto_idx, max(-1, upto_idx - 40), -1):
        if pat.search(lines[i]):
            # ต่อบรรทัดถัดไปด้วย เผื่อการกำหนดค่าถูกตัดขึ้นบรรทัดใหม่
            return i, " ".join(lines[i:i + 3])
    return None, None


def main():
    problems = []
    scanned = 0
    for dirpath, _dirs, files in os.walk(SRC):
        for fn in files:
            if not fn.endswith(".cs"):
                continue
            path = os.path.join(dirpath, fn)
            scanned += 1
            try:
                lines = open(path, encoding="utf-8").read().split("\n")
            except Exception:
                continue
            for idx, line in enumerate(lines):
                m = LOCK_CALL.search(line)
                if not m:
                    continue
                var = m.group(1)
                aidx, rhs = find_assignment(lines, idx, var)
                if rhs is None:
                    continue  # หาไม่เจอ = ไม่ฟันธง (ห้ามฟ้องผิด)
                if BAD_RHS.search(rhs):
                    rel = os.path.relpath(path, ROOT)
                    problems.append((rel, aidx + 1, var))

    for rel, ln, var in problems:
        print(f"{rel}:{ln}: `{var}` คำนวณจาก HashCode.Combine/GetHashCode "
              f"ซึ่ง**สุ่มต่อ process** → advisory lock กันข้าม instance ไม่ได้")
        print("    → ใช้ Helpers/AdvisoryLockKey.For(companyId, scope, part) แทน")

    print()
    print(f"ตรวจ {scanned} ไฟล์ .cs · คีย์ advisory lock ที่ไม่ deterministic "
          f"{len(problems)} จุด")
    return 1 if problems else 0


if __name__ == "__main__":
    sys.exit(main())
