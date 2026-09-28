#!/usr/bin/env python3
# -*- coding: utf-8 -*-
"""ทุกค่าของ `SettlementLineType` ต้องมีกติกาแถวเดียวใน `Helpers/SettlementLineTypeRules.cs` (รอบ 198 เฟส 1 ทีม A)

ที่มา: ประเภทบรรทัด settlement กำหนด "ลงบัญชีทางไหน · เครื่องหมาย · ผัง · VAT/WHT · ต้องจับคู่ใบขายไหม" — ถ้ามีคนเพิ่มค่า enum
(เช่นประเภทใหม่ของ Lazada) แต่ลืมแถวในตาราง `For()` จะคืนกติกาของ Unclassified ⇒ บรรทัดประเภทใหม่ลงบัญชีไม่ได้ทั้งที่ผู้ใช้
เลือกแล้ว (ทิศปลอดภัย แต่เงียบว่าทำไม) · และถ้ามีคนเขียน `switch` ของ SettlementLineType เองที่อื่น = ตารางที่สอง (F2 ข้อ 4)

กติกา:
  1. ทุกชื่อใน `enum SettlementLineType` (Models/Enums/SettlementEnums.cs) มี `new SettlementLineTypeRule(SettlementLineType.<ชื่อ>`
     ใน Helpers/SettlementLineTypeRules.cs **ครั้งเดียว** · แถวที่อ้างชื่อที่ไม่มีใน enum = ผิด
  2. `case SettlementLineType.X` / `SettlementLineType.X =>` (switch) นอกไฟล์เจ้าของ = ผิด (ถามตาราง ห้ามตัดสินเอง)

ใช้: python3 tools/settlement_line_type_rules_check.py [--self-test]
negative test ในตัวรันทุกครั้ง: ถอดแถว / ซ้ำแถว / เพิ่ม switch นอกเจ้าของ บนสำเนาในหน่วยความจำ แล้วต้องฟ้อง
"""
import os
import re
import sys

ROOT = os.path.dirname(os.path.dirname(os.path.abspath(__file__)))
SRC = os.path.join(ROOT, 'Accounting')
ENUM_FILE = os.path.join(SRC, 'Models', 'Enums', 'SettlementEnums.cs')
RULES_FILE = os.path.join(SRC, 'Helpers', 'SettlementLineTypeRules.cs')
OWNERS = {os.path.normpath(RULES_FILE)}

ENUM_BLOCK = re.compile(r'enum\s+SettlementLineType\s*\{(.*?)\}', re.S)
ENUM_MEMBER = re.compile(r'^\s*([A-Z]\w*)\s*=\s*\d+\s*,', re.M)
RULE_ROW = re.compile(r'new\s+SettlementLineTypeRule\(\s*SettlementLineType\.(\w+)')
SWITCH_ARM = re.compile(r'(?:\bcase\s+SettlementLineType\.\w+|SettlementLineType\.\w+\s*=>)')


def strip_comments(text):
    text = re.sub(r'/\*.*?\*/', lambda m: '\n' * m.group(0).count('\n'), text, flags=re.S)
    return re.sub(r'(?<!:)//[^\n]*', '', text)


def enum_members(enum_src):
    m = ENUM_BLOCK.search(strip_comments(enum_src))
    return ENUM_MEMBER.findall(m.group(1)) if m else []


def check(enum_src, rules_src, other_files):
    problems = []
    members = enum_members(enum_src)
    if not members:
        problems.append('หา enum SettlementLineType ไม่เจอ')
    rows = RULE_ROW.findall(strip_comments(rules_src))
    for name in members:
        n = rows.count(name)
        if n == 0:
            problems.append(f'SettlementLineType.{name} ไม่มีกติกาใน SettlementLineTypeRules.All')
        elif n > 1:
            problems.append(f'SettlementLineType.{name} มีกติกา {n} แถว (ต้องแถวเดียว)')
    for name in sorted(set(rows) - set(members)):
        problems.append(f'กติกาอ้าง SettlementLineType.{name} ที่ไม่มีใน enum')
    for path, text in other_files:
        for i, line in enumerate(strip_comments(text).splitlines(), 1):
            if SWITCH_ARM.search(line):
                problems.append(f'{os.path.relpath(path, ROOT)}:{i}: ตัดสินตาม SettlementLineType เอง — ถามจาก '
                                f'SettlementLineTypeRules.For(type) แทน')
    return problems


def repo_files():
    out = []
    for d, dirs, files in os.walk(SRC):
        dirs[:] = [x for x in dirs if x not in ('bin', 'obj', 'wwwroot', 'node_modules')]
        for f in files:
            if f.endswith('.cs'):
                p = os.path.normpath(os.path.join(d, f))
                if p in OWNERS:
                    continue
                with open(p, encoding='utf-8', errors='replace') as fh:
                    t = fh.read()
                if 'SettlementLineType.' in t:
                    out.append((p, t))
    return out


def self_test(enum_src, rules_src):
    fails = []
    members = enum_members(enum_src)
    if len(members) < 2:
        return ['self-test: enum สั้นผิดปกติ']
    victim = members[1]
    removed = re.sub(r'new\s+SettlementLineTypeRule\(\s*SettlementLineType\.' + victim + r'\b', 'new X(', rules_src, count=1)
    if not check(enum_src, removed, []):
        fails.append(f'self-test: ถอดแถว {victim} แล้วไม่ฟ้อง')
    dup = rules_src + f'\n new SettlementLineTypeRule(SettlementLineType.{victim}, "x")'
    if not check(enum_src, dup, []):
        fails.append(f'self-test: ซ้ำแถว {victim} แล้วไม่ฟ้อง')
    fake = [('Accounting/Services/Fake.cs', f'switch (t) {{ case SettlementLineType.{victim}: break; }}')]
    if not check(enum_src, rules_src, fake):
        fails.append('self-test: switch นอกเจ้าของแล้วไม่ฟ้อง')
    arm = [('Accounting/Services/Fake2.cs', f'var x = t switch {{ SettlementLineType.{victim} => 1, _ => 0 }};')]
    if not check(enum_src, rules_src, arm):
        fails.append('self-test: switch expression นอกเจ้าของแล้วไม่ฟ้อง')
    ok = [('Accounting/Services/Fake3.cs', f'if (l.LineType == SettlementLineType.{victim}) {{ }}')]
    if check(enum_src, rules_src, ok):
        fails.append('self-test: การเปรียบเทียบธรรมดาถูกฟ้องผิด (checker ที่ฟ้องผิด = checker ที่พัง)')
    return fails


def main():
    with open(ENUM_FILE, encoding='utf-8') as f:
        enum_src = f.read()
    with open(RULES_FILE, encoding='utf-8') as f:
        rules_src = f.read()
    fails = self_test(enum_src, rules_src)
    if '--self-test' in sys.argv:
        print('\n'.join(fails) if fails else 'self-test: ผ่าน')
        return 1 if fails else 0
    problems = fails + check(enum_src, rules_src, repo_files())
    if problems:
        print('❌ settlement_line_type_rules_check:')
        for p in problems:
            print('  - ' + p)
        return 1
    print(f'✅ SettlementLineType {len(enum_members(enum_src))} ค่า มีกติกาครบในตารางเดียว')
    return 0


if __name__ == '__main__':
    sys.exit(main())
