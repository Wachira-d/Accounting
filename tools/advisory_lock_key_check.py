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

รอบ 250 (AUDIT_249 WP-01 ข้อ 8 · E M-3): `pg_advisory(_xact)_lock(hashtext(...))` ใน SQL ตรง — hashtext คืน int4 (32 บิต)
ไม่ผ่าน `AdvisoryLockKey.For` (64 บิต · มี scope) ⇒ ชนคีย์ของทรัพยากรอื่นได้ และหลุดจากตัวตั้งเดียวของคีย์ ⇒ ฟ้อง ·
จุดเดิมที่รู้อยู่ (`Helpers/PaymentQrMigration.cs` — แก้โค้ดใน WP-04) อยู่ใน HASHTEXT_BASELINE (ratchet: เพิ่มไม่ได้ · แก้แล้วลบแถว)
`--self-test` = negative test (ไฟล์สังเคราะห์ต้องถูกฟ้อง · AdvisoryLockKey.For/คอมเมนต์/hashtext นอกการล็อกไม่ฟ้อง)
"""
import os
import re
import sys

ROOT = os.path.dirname(os.path.dirname(os.path.abspath(__file__)))
SRC = os.path.join(ROOT, "Accounting")

LOCK_CALL = re.compile(r'pg_advisory(?:_xact)?_lock\(\{0\}\)"\s*,\s*([A-Za-z_]\w*)\s*\)')
BAD_RHS = re.compile(r'HashCode\.Combine|\.GetHashCode\s*\(')
# SQL ตรง: pg_advisory_lock / pg_advisory_xact_lock / pg_try_advisory(_xact)_lock ที่คีย์คือ hashtext(...)
HASHTEXT_IN_LOCK = re.compile(r'pg_(?:try_)?advisory(?:_xact)?_lock(?:_shared)?\s*\(\s*hashtext\s*\(', re.I)
# ratchet: ไฟล์ → จำนวนจุดที่รู้อยู่ก่อนกติกา (แก้แล้วลดตัวเลข/ลบแถว — เกินตัวเลข = ฟ้อง)
HASHTEXT_BASELINE = {
    "Accounting/Helpers/PaymentQrMigration.cs": 1,   # WP-04 แก้เป็น AdvisoryLockKey.For
}


def hashtext_hits(text):
    """จำนวนการเรียกล็อกที่คีย์เป็น hashtext( — ข้ามบรรทัดคอมเมนต์"""
    n = 0
    for line in text.split("\n"):
        stripped = line.lstrip()
        if stripped.startswith("//") or stripped.startswith("*"):
            continue
        n += len(HASHTEXT_IN_LOCK.findall(line))
    return n


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
    hashtext_problems = []
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
            rel = os.path.relpath(path, ROOT).replace(os.sep, "/")
            n = hashtext_hits("\n".join(lines))
            if n > HASHTEXT_BASELINE.get(rel, 0):
                hashtext_problems.append((rel, n, HASHTEXT_BASELINE.get(rel, 0)))

    for rel, ln, var in problems:
        print(f"{rel}:{ln}: `{var}` คำนวณจาก HashCode.Combine/GetHashCode "
              f"ซึ่ง**สุ่มต่อ process** → advisory lock กันข้าม instance ไม่ได้")
        print("    → ใช้ Helpers/AdvisoryLockKey.For(companyId, scope, part) แทน")

    for rel, n, base in hashtext_problems:
        print(f"{rel}: pg_advisory_*lock(hashtext(...)) {n} จุด (baseline {base}) — hashtext เป็น int4 32 บิต "
              f"ไม่ผ่านตัวตั้งเดียวของคีย์ ⇒ ชนคีย์ทรัพยากรอื่นได้")
        print("    → ส่งคีย์จาก Helpers/AdvisoryLockKey.For(companyId, scope, part) เป็นพารามิเตอร์ (pg_advisory_xact_lock({0}))")

    print()
    print(f"ตรวจ {scanned} ไฟล์ .cs · คีย์ advisory lock ที่ไม่ deterministic "
          f"{len(problems)} จุด · hashtext ในล็อกเกิน baseline {len(hashtext_problems)} ไฟล์")
    return 1 if problems or hashtext_problems else 0


def self_test():
    fails = []
    bad = 'await db.Database.ExecuteSqlInterpolatedAsync($"SELECT pg_advisory_xact_lock(hashtext({k}))", ct);'
    bad2 = 'cmd.CommandText = "SELECT pg_try_advisory_lock( hashtext(@k) )";'
    ok = 'await db.Database.ExecuteSqlRawAsync("SELECT pg_advisory_xact_lock({0})", new object[] { AdvisoryLockKey.For(cid, "x", "y") }, ct);'
    comment = '// เดิม: SELECT pg_advisory_xact_lock(hashtext(...)) — ห้าม'
    if hashtext_hits(bad) != 1:
        fails.append("pg_advisory_xact_lock(hashtext( ต้องถูกนับ")
    if hashtext_hits(bad2) != 1:
        fails.append("pg_try_advisory_lock( hashtext( ต้องถูกนับ")
    if hashtext_hits(ok) != 0:
        fails.append("AdvisoryLockKey.For ต้องไม่ถูกนับ")
    if hashtext_hits(comment) != 0:
        fails.append("คอมเมนต์ต้องไม่ถูกนับ")
    if hashtext_hits("SELECT hashtext('a')") != 0:
        fails.append("hashtext นอกการล็อกไม่ใช่เรื่องของกติกานี้")
    # จุดใน baseline ต้องยังนับได้จากไฟล์จริง (กันกติกาตายเงียบเมื่อรูปโค้ดเปลี่ยน — แก้แล้วให้ลบแถว baseline)
    for rel, n in HASHTEXT_BASELINE.items():
        path = os.path.join(ROOT, rel)
        if os.path.exists(path):
            got = hashtext_hits(open(path, encoding="utf-8").read())
            if got > n:
                fails.append(f"{rel} เกิน baseline ({got} > {n})")
    if fails:
        print("advisory_lock_key_check --self-test: ล้ม")
        for f in fails:
            print("  - " + f)
        return 1
    print("advisory_lock_key_check --self-test: ผ่าน")
    return 0


if __name__ == "__main__":
    sys.exit(self_test() if "--self-test" in sys.argv else main())
