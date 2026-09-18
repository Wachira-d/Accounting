#!/usr/bin/env python3
# -*- coding: utf-8 -*-
"""พิมพ์ทุก call site ของสัญลักษณ์ (คลาส/เมธอด/พร็อพเพอร์ตี้) ทั้ง .cs/.html/.js — ใช้ก่อนแก้ทุกครั้ง

ที่มา (รอบ 169): กติกา "grep call site ก่อนแก้ / มี ≠ ถูกเรียก / แก้ตัวเดียว เหลือที่เหลือ" ปรากฏ
ใน CLAUDE.md หลายสิบครั้ง แต่ทุกครั้งต้องประกอบคำสั่ง grep เอง แล้วมักลืมกรองคอมเมนต์
(doc-comment ที่บอกว่า "ดู `Foo.Bar`" ถูกนับเป็นผู้เรียก — เคส `CanShareMoneyAggregates`) หรือ
ลืมฝั่ง JS/HTML (เคส `Layout.docHeaderLabel` · `MENU_SECTIONS`). ตัวนี้ทำให้เป็นคำสั่งเดียว
และแยกให้เห็นทันทีว่า: นิยามอยู่ไหน · โค้ดจริงเรียกกี่จุด · เทสต์อ้างกี่จุด · คอมเมนต์เอ่ยถึงกี่จุด

ใช้: python3 tools/callers.py <Symbol> [<Symbol2> ...] [--with-comments]
     python3 tools/callers.py Foo.Bar        # จำกัดเฉพาะ `Foo.Bar` (สัญลักษณ์มีจุด = ต้องเจอทั้งสาย)
ผลลัพธ์: exit 0 เมื่อทุกสัญลักษณ์มีผู้เรียกในโค้ดจริง ≥ 1 · exit 1 เมื่อตัวใดตัวหนึ่งมีแต่นิยาม/เทสต์/คอมเมนต์
"""
import os
import re
import sys

ROOT = os.path.dirname(os.path.dirname(os.path.abspath(__file__)))
SKIP_DIRS = {'bin', 'obj', 'node_modules', '.git', '__pycache__'}
DEF_RE = (r'\b(class|struct|record|interface|enum)\s+{0}\b|'
                    r'\b{0}\s*(?:<[^>()]*>)?\s*\([^)]*\)\s*(?:=>|\{{|where|$)|'
                    r'\b{0}\s*[:=]\s*(?:function|async|\()|'
                    r'^\s*(?:async\s+)?{0}\s*\([^)]*\)\s*\{{')


def iter_files():
    for top in ('Accounting', 'Accounting.Tests'):
        for base, dirs, files in os.walk(os.path.join(ROOT, top)):
            dirs[:] = [d for d in dirs if d not in SKIP_DIRS]
            for f in files:
                if f.endswith(('.cs', '.html', '.js')):
                    yield os.path.join(base, f)


def strip_comment(line, ext):
    # คงส่วนที่อยู่ก่อนคอมเมนต์ · ไม่ตัด `//` ที่ตามหลัง `:` (URL)
    if ext == '.cs' or ext == '.js':
        line = re.sub(r'(?<!:)//.*$', '', line)
    return re.sub(r'/\*.*?\*/', '', line)


def is_comment_only(line, ext):
    s = line.strip()
    if ext in ('.cs', '.js'):
        return s.startswith('//') or s.startswith('*') or s.startswith('/*')
    return s.startswith('<!--')


def scan(symbol):
    # ไม่กัน `.` นำหน้า — `Accounting.Helpers.Foo.Bar` ก็คือการเรียก Foo.Bar
    pat = re.compile(r'(?<!\w)' + re.escape(symbol) + r'(?!\w)')
    parts = symbol.split('.')
    def_re = re.compile(DEF_RE.format(re.escape(parts[-1])), re.M)
    # สัญลักษณ์มีจุด (Class.Member): นิยามของ Member อยู่ในไฟล์ที่ประกาศ Class — ค้นด้วยชื่อสมาชิกเปล่า
    member_pat = re.compile(r'(?<!\w)' + re.escape(parts[-1]) + r'(?!\w)') if len(parts) > 1 else None
    owner_re = re.compile(r'\b(class|struct|record|interface|enum)\s+' + re.escape(parts[-2]) + r'\b') if len(parts) > 1 else None
    hits = {'def': [], 'code': [], 'test': [], 'comment': []}
    for path in iter_files():
        ext = os.path.splitext(path)[1]
        rel = os.path.relpath(path, ROOT)
        try:
            with open(path, encoding='utf-8', errors='ignore') as fh:
                lines = fh.readlines()
        except OSError:
            continue
        in_block = False
        if member_pat is not None and ext == '.cs' and owner_re.search(''.join(lines)):
            for no, raw in enumerate(lines, 1):
                line = strip_comment(raw.rstrip('\n'), ext)
                if member_pat.search(line) and def_re.search(line):
                    hits['def'].append((rel, no, raw.strip()))
        for no, raw in enumerate(lines, 1):
            line = raw.rstrip('\n')
            if in_block:
                if '*/' in line:
                    in_block = False
                    line = line.split('*/', 1)[1]
                else:
                    if pat.search(line):
                        hits['comment'].append((rel, no, line.strip()))
                    continue
            if not pat.search(line):
                if '/*' in line and '*/' not in line:
                    in_block = True
                continue
            if is_comment_only(line, ext) or not pat.search(strip_comment(line, ext)):
                hits['comment'].append((rel, no, line.strip()))
            elif def_re.search(strip_comment(line, ext)) and rel.startswith('Accounting' + os.sep) and ext == '.cs' \
                    and not rel.startswith('Accounting.Tests'):
                hits['def'].append((rel, no, line.strip()))
            elif rel.startswith('Accounting.Tests'):
                hits['test'].append((rel, no, line.strip()))
            else:
                hits['code'].append((rel, no, line.strip()))
            if '/*' in line and '*/' not in line:
                in_block = True
    return hits


def main(argv):
    show_comments = '--with-comments' in argv
    symbols = [a for a in argv if not a.startswith('--')]
    if not symbols:
        print(__doc__)
        return 2
    rc = 0
    for sym in symbols:
        h = scan(sym)
        print(f'== {sym}: นิยาม {len(h["def"])} · โค้ดจริงเรียก {len(h["code"])} · เทสต์ {len(h["test"])} · คอมเมนต์ {len(h["comment"])}')
        for label, key in (('นิยาม', 'def'), ('ผู้เรียก (โค้ดจริง)', 'code'), ('เทสต์', 'test')):
            if h[key]:
                print(f'  -- {label}')
                for rel, no, text in h[key][:200]:
                    print(f'  {rel}:{no}: {text[:160]}')
        if show_comments and h['comment']:
            print('  -- คอมเมนต์ (ไม่ใช่การเรียก)')
            for rel, no, text in h['comment'][:100]:
                print(f'  {rel}:{no}: {text[:160]}')
        if not h['code']:
            print(f'  ⚠️  {sym} ไม่มีผู้เรียกในโค้ดจริง — "มี ≠ ถูกเรียก": ต่อสาย หรือ ลบ')
            rc = 1
    return rc


if __name__ == '__main__':
    sys.exit(main(sys.argv[1:]))
