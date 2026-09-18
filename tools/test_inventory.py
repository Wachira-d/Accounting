#!/usr/bin/env python3
# -*- coding: utf-8 -*-
"""นับเทสต์จริงใน Accounting.Tests เพื่อให้ TEST_PLAN.md §0 สร้างจาก source ไม่ใช่พิมพ์มือ

ที่มา (รอบ 169): TEST_PLAN.md §0 เขียนว่า "~150 เคส / 19 ไฟล์" ค้างมาจนความจริงคือ 187 ไฟล์ /
1,600+ เคส (ผิด 10 เท่า) — doc ที่ประกาศตัวว่าเป็น single source of truth แล้วผิดขนาดนี้ สอนคนอ่านให้
ไม่เชื่อ doc ทั้งชุด. กติกาเดียวกับ `MENU_SECTIONS`/`complianceIssues`: ตัวเลขที่มี source ให้สร้างจาก
source แล้วให้ doc แสดง

ใช้: python3 tools/test_inventory.py          # สรุปตัวเลข
     python3 tools/test_inventory.py --row    # พิมพ์ข้อความสำหรับวางในช่อง "เทสต์ที่มี" ของ TEST_PLAN §0
     python3 tools/test_inventory.py --check  # exit 1 ถ้าตัวเลขใน TEST_PLAN §0 ไม่ตรงกับความจริง
"""
import datetime
import os
import re
import sys

ROOT = os.path.dirname(os.path.dirname(os.path.abspath(__file__)))
TDIR = os.path.join(ROOT, 'Accounting.Tests')


def inventory():
    files = facts = theories = inline = db = 0
    for base, dirs, names in os.walk(TDIR):
        dirs[:] = [d for d in dirs if d not in ('bin', 'obj')]
        for f in names:
            if not f.endswith('.cs'):
                continue
            files += 1
            with open(os.path.join(base, f), encoding='utf-8', errors='ignore') as fh:
                t = fh.read()
            facts += len(re.findall(r'\[Fact\b', t))
            theories += len(re.findall(r'\[Theory\b', t))
            inline += len(re.findall(r'\[InlineData\b', t))
            code = re.sub(r'/\*.*?\*/', '', t, flags=re.S)
            code = re.sub(r'(?<!:)//[^\n]*', '', code)   # คอมเมนต์ที่เอ่ยถึง DbContext ไม่ใช่การใช้
            if re.search(r'\bDbContext\b|WebApplicationFactory', code):
                db += 1
    return files, facts, theories, inline, db


def row(inv, today=None):
    files, facts, theories, inline, db = inv
    today = today or datetime.date.today().isoformat()
    return (f'**{files} ไฟล์ · {facts:,} `[Fact]` + {theories:,} `[Theory]` ({inline:,} `InlineData`)** '
            f'ณ {today} — {"pure-logic ทั้งหมด (0 ไฟล์แตะ `DbContext`)" if db == 0 else f"{db} ไฟล์แตะ `DbContext`"}')


def check(inv):
    files, facts, theories, inline, db = inv
    path = os.path.join(ROOT, 'TEST_PLAN.md')
    with open(path, encoding='utf-8') as fh:
        for line in fh:
            if line.startswith('| เทสต์ที่มี |'):
                m = re.search(r'\*\*(\d+) ไฟล์ · ([\d,]+) `\[Fact\]` \+ ([\d,]+) `\[Theory\]` \(([\d,]+) `InlineData`\)\*\*', line)
                if not m:
                    print('❌ TEST_PLAN §0 ช่อง "เทสต์ที่มี" ไม่อยู่ในรูปที่สคริปต์นี้สร้าง — วางผล --row ลงไป')
                    return 1
                got = tuple(int(x.replace(',', '')) for x in m.groups())
                if got != (files, facts, theories, inline):
                    print(f'❌ TEST_PLAN §0 บอก {got} แต่ของจริง {(files, facts, theories, inline)} — รัน --row แล้ววางทับ')
                    return 1
                print('✅ TEST_PLAN §0 ตรงกับ Accounting.Tests')
                return 0
    print('❌ ไม่พบแถว "เทสต์ที่มี" ใน TEST_PLAN §0')
    return 1


if __name__ == '__main__':
    inv = inventory()
    if '--row' in sys.argv:
        print(row(inv))
    elif '--check' in sys.argv:
        sys.exit(check(inv))
    else:
        files, facts, theories, inline, db = inv
        print(f'ไฟล์เทสต์ {files} · [Fact] {facts} · [Theory] {theories} · [InlineData] {inline} · แตะ DbContext {db}')
