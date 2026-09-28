#!/usr/bin/env python3
# -*- coding: utf-8 -*-
"""ความรู้เฉพาะแพลตฟอร์มของ settlement ต้องอยู่ใต้ `Accounting/Services/Settlement/Adapters/**` เท่านั้น (รอบ 198 เฟส 1 ทีม B)

═══ ที่มา (report-S2 §3 "Adapter" · §5 ความเสี่ยง "แพลตฟอร์มเปลี่ยนรูปไฟล์") ═══
ชั้นนำเข้า/จัดประเภท/จับคู่ (service) และตัวตัดสิน pure (Helpers/Settlement*) ต้องทำงานกับ "แถวที่อ่านแล้ว" เท่านั้น — ถ้าชื่อเจ้า
(Shopee · Lazada · Omise …) หรือการอ่านไฟล์ (MiniExcel · แยก CSV) หลุดเข้ามา:
  · แพลตฟอร์มเปลี่ยนรูปไฟล์ ⇒ ต้องไล่แก้หลายชั้น (defect class "แก้ตัวเดียว เหลือที่เหลือ")
  · กติกาที่ควรเป็นกลาง (ลำดับจัดประเภท · ด่านคำตอบ AI · การจับคู่) กลายเป็น if ตามชื่อเจ้า ที่เทสต์ไม่ครอบ
  · ป้าย seed สองชุด (ใน adapter + ใน service) ⇒ ตอบไม่ตรงกัน

กติกา (ตรวจโค้ดหลังตัดคอมเมนต์ · สตริงยังนับ — ชื่อเจ้าในสตริงคือความรู้เฉพาะเจ้า):
  1. ไฟล์ใน `Services/Settlement/**` (ยกเว้น `Adapters/`) · `Helpers/Settlement*.cs` · `Services/Ai/Prompts/SettlementLineClassifyPrompt.cs`
     ห้ามมีชื่อแพลตฟอร์ม (PLATFORM_TOKENS)
  2. ไฟล์กลุ่มเดียวกันห้ามอ่านไฟล์เอง (`MiniExcel.` · `ReadCsv(` · `CodePagesEncodingProvider`) — ต้องผ่าน adapter
  3. ตาราง seed ป้าย (`class SettlementLabelSeed`) ต้องอยู่ใน Adapters ที่เดียว

ใช้: python3 tools/settlement_adapter_boundary_check.py [--self-test]
negative test ในตัวรันทุกครั้ง: ใส่ชื่อเจ้า/ตัวอ่านไฟล์ลงสำเนาของไฟล์จริงในหน่วยความจำแล้วต้องฟ้อง · ชื่อเจ้าในคอมเมนต์/ใน Adapters ต้องไม่ถูกฟ้อง
"""
import os
import re
import sys

ROOT = os.path.dirname(os.path.dirname(os.path.abspath(__file__)))
SRC = os.path.join(ROOT, 'Accounting')
ADAPTERS = os.path.normpath(os.path.join(SRC, 'Services', 'Settlement', 'Adapters'))
SERVICE_DIR = os.path.normpath(os.path.join(SRC, 'Services', 'Settlement'))
HELPERS = os.path.normpath(os.path.join(SRC, 'Helpers'))
EXTRA = [os.path.normpath(os.path.join(SRC, 'Services', 'Ai', 'Prompts', 'SettlementLineClassifyPrompt.cs'))]

PLATFORM_TOKENS = [
    'shopee', 'lazada', 'tiktok', 'omise', '2c2p', 'gbprimepay', 'gb prime pay', 'stripe', 'paypal', 'agoda',
    'booking.com', 'expedia', 'trip.com', 'traveloka', 'line myshop', 'grabfood', 'lineman', 'line man', 'robinhood',
]
PLATFORM_RE = re.compile(r'(?<![a-z0-9])(?:' + '|'.join(re.escape(t) for t in PLATFORM_TOKENS) + r')(?![a-z0-9])', re.I)
FILE_READ_RE = re.compile(r'\bMiniExcel\s*\.|\bReadCsv\s*\(|\bCodePagesEncodingProvider\b')
SEED_CLASS_RE = re.compile(r'\bclass\s+SettlementLabelSeed\b')


def strip_comments(text):
    """ตัด /* */ และ // (คงสตริงไว้ — ชื่อเจ้าในสตริงนับ) · ไม่ตัด // ที่อยู่ในสตริง"""
    out, i, n = [], 0, len(text)
    in_str = None
    while i < n:
        c = text[i]
        if in_str:
            out.append(c)
            if in_str == '"' and c == '\\' and i + 1 < n:
                out.append(text[i + 1]); i += 2; continue
            if c == in_str:
                in_str = None
            i += 1; continue
        if c == '"':
            in_str = '"'; out.append(c); i += 1; continue
        if text.startswith('//', i):
            j = text.find('\n', i)
            i = n if j < 0 else j
            continue
        if text.startswith('/*', i):
            j = text.find('*/', i + 2)
            seg = text[i:(n if j < 0 else j + 2)]
            out.append('\n' * seg.count('\n'))
            i = n if j < 0 else j + 2
            continue
        out.append(c); i += 1
    return ''.join(out)


def in_scope(path):
    p = os.path.normpath(path)
    if p.startswith(ADAPTERS + os.sep):
        return False
    if p.startswith(SERVICE_DIR + os.sep):
        return True
    if os.path.dirname(p) == HELPERS and os.path.basename(p).startswith('Settlement'):
        return True
    return p in EXTRA


def check_file(path, text):
    problems = []
    code = strip_comments(text)
    rel = os.path.relpath(path, ROOT)
    if SEED_CLASS_RE.search(code) and not os.path.normpath(path).startswith(ADAPTERS + os.sep):
        problems.append(f'{rel}: ตาราง seed ป้าย (SettlementLabelSeed) ต้องอยู่ใน Services/Settlement/Adapters ที่เดียว')
    if not in_scope(path):
        return problems
    for i, line in enumerate(code.splitlines(), 1):
        m = PLATFORM_RE.search(line)
        if m:
            problems.append(f'{rel}:{i}: ชื่อแพลตฟอร์ม "{m.group(0)}" นอก Services/Settlement/Adapters — ย้ายความรู้เฉพาะเจ้าไป adapter/seed')
        m2 = FILE_READ_RE.search(line)
        if m2:
            problems.append(f'{rel}:{i}: อ่านไฟล์เอง ("{m2.group(0).strip()}") นอก adapter — ใช้ ISettlementReportAdapter')
    return problems


def repo_files():
    for d, dirs, files in os.walk(SRC):
        dirs[:] = [x for x in dirs if x not in ('bin', 'obj', 'wwwroot', 'node_modules')]
        for f in files:
            if f.endswith('.cs'):
                p = os.path.join(d, f)
                if in_scope(p) or 'SettlementLabelSeed' in f or f.startswith('Settlement'):
                    with open(p, encoding='utf-8', errors='replace') as fh:
                        yield p, fh.read()


def self_test():
    fails = []
    svc = os.path.join(SERVICE_DIR, 'SettlementImportService.cs')
    real = open(svc, encoding='utf-8').read() if os.path.exists(svc) else 'class X { }'
    if check_file(svc, real + '\nclass Z { string s = "Shopee ads fee"; }'):
        pass
    else:
        fails.append('self-test: ชื่อเจ้าในสตริงของไฟล์ service จริงไม่ถูกฟ้อง')
    if not check_file(svc, real + '\nclass Z { void F() { var r = MiniExcel.Query(s); } }'):
        fails.append('self-test: MiniExcel ในไฟล์ service ไม่ถูกฟ้อง')
    helper = os.path.join(HELPERS, 'SettlementFake.cs')
    if not check_file(helper, 'class A { bool F(string l) => l.Contains("lazada"); }'):
        fails.append('self-test: ชื่อเจ้าใน Helpers/Settlement*.cs ไม่ถูกฟ้อง')
    if check_file(svc, real + '\n// Shopee และ Omise ในคอมเมนต์\n/* Lazada */'):
        fails.append('self-test: ชื่อเจ้าในคอมเมนต์ถูกฟ้อง (checker ที่ฟ้องผิด = checker ที่พัง)')
    adapter = os.path.join(ADAPTERS, 'ShopeeAdapter.cs')
    if check_file(adapter, 'class A { string s = "Shopee"; void F() { MiniExcel.Query(x); } }'):
        fails.append('self-test: ชื่อเจ้า/ตัวอ่านไฟล์ใน Adapters ถูกฟ้อง (ต้องอนุญาต)')
    other = os.path.join(SRC, 'Services', 'Payments', 'Providers', 'OmisePaymentProvider.cs')
    if check_file(other, 'class A { string s = "omise"; }'):
        fails.append('self-test: ไฟล์นอกขอบเขต settlement ถูกฟ้อง')
    if not check_file(svc, 'class SettlementLabelSeed { }'):
        fails.append('self-test: ตาราง seed นอก Adapters ไม่ถูกฟ้อง')
    if check_file(svc, 'class A { string u = "http://x"; bool b = s == "booking"; }'):
        fails.append('self-test: คำว่า booking เดี่ยว ๆ (ไม่ใช่ booking.com) ถูกฟ้อง')
    return fails


def main():
    fails = self_test()
    if '--self-test' in sys.argv:
        print('\n'.join(fails) if fails else 'self-test: ผ่าน')
        return 1 if fails else 0
    problems = list(fails)
    scanned = 0
    for p, t in repo_files():
        scanned += 1
        problems += check_file(p, t)
    if problems:
        print('❌ settlement_adapter_boundary_check:')
        for p in problems:
            print('  - ' + p)
        return 1
    print(f'✅ ความรู้เฉพาะแพลตฟอร์มของ settlement อยู่ใน Adapters เท่านั้น (ตรวจ {scanned} ไฟล์)')
    return 0


if __name__ == '__main__':
    sys.exit(main())
