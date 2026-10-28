#!/usr/bin/env python3
# -*- coding: utf-8 -*-
"""ห้ามประกอบ path เขียนไฟล์จาก `Directory.GetCurrentDirectory()` เพิ่มใน Services/Controllers — ratchet (ทีม W4 · R-43)

ที่มา (กฎเหล็ก #4 D "ไฟล์ห้ามเขียน local disk ตรง — ผ่าน abstraction กลาง")
    `CmsContentService.UploadMediaAsync` เขียน `uploads/cms/{companyId}/{siteId}` และ
    `CmsCommerceService.RecordPaymentSlipAsync` เขียน `wwwroot/uploads/order-slips` ด้วย
    `Path.Combine(Directory.GetCurrentDirectory(), …)` ขณะที่ `IFileAttachmentService` (ที่เก็บไฟล์กลาง) มีอยู่แล้ว
    ⇒ ไฟล์กระจายหลายโฟลเดอร์ · ไม่มีแถว FileAttachment (ไม่มีด่านสิทธิ์/ไม่มี audit) · scale หลาย instance ไม่ได้
    รอบ W4 ย้ายสองเส้นนี้ไป IFileAttachmentService แล้ว — checker นี้กันไม่ให้มีจุดใหม่ (ตัวที่สาม)

กติกา (ratchet — ไม่ใช่กวาดล้าง)
    * นับ `GetCurrentDirectory()` ใน `Accounting/Services/**` และ `Accounting/Controllers/**` (ตัดคอมเมนต์/สตริงก่อน)
    * ไฟล์ที่มีอยู่แล้ว + จำนวนจุด อยู่ใน `tools/local_disk_write_baseline.txt` — ไฟล์ใหม่ที่มี หรือไฟล์เดิมที่จำนวนเพิ่ม ⇒ ล้ม
    * ไฟล์ที่จำนวนลด ⇒ รายงานให้ลด baseline (ไม่ล้ม · ratchet เดินทางเดียว) · `--write-baseline` เขียนทับ
    * `FileAttachmentService.cs` คือ abstraction กลางเอง (BasePath fallback) — อยู่ใน baseline ตามจำนวนจริง

negative test: `python3 tools/local_disk_write_check.py --self-test`
"""
import os
import re
import sys
import tempfile

ROOT = os.path.dirname(os.path.dirname(os.path.abspath(__file__)))
SRC = os.path.join(ROOT, 'Accounting')
BASELINE = os.path.join(ROOT, 'tools', 'local_disk_write_baseline.txt')
SCAN_DIRS = ('Services', 'Controllers')
PATTERN = re.compile(r'\bGetCurrentDirectory\s*\(\s*\)')


def strip_comments_and_strings(text: str) -> str:
    """ตัดคอมเมนต์ + string literal (คงจำนวนบรรทัด) — `GetCurrentDirectory` ที่เอ่ยในคอมเมนต์ไม่ใช่การเรียก"""
    out = []
    i, n = 0, len(text)
    while i < n:
        c = text[i]
        if text.startswith('//', i):
            j = text.find('\n', i)
            if j < 0:
                j = n
            i = j
            continue
        if text.startswith('/*', i):
            j = text.find('*/', i + 2)
            j = n if j < 0 else j + 2
            out.append('\n' * text[i:j].count('\n'))
            i = j
            continue
        if text.startswith('"""', i):
            j = text.find('"""', i + 3)
            j = n if j < 0 else j + 3
            out.append('\n' * text[i:j].count('\n'))
            i = j
            continue
        if c == '"':
            j = i + 1
            while j < n and text[j] != '"':
                if text[j] == '\\':
                    j += 1
                j += 1
            seg = text[i:j + 1]
            out.append('\n' * seg.count('\n'))
            i = j + 1
            continue
        out.append(c)
        i += 1
    return ''.join(out)


def scan(src=SRC):
    counts = {}
    for d in SCAN_DIRS:
        base = os.path.join(src, d)
        for dirpath, dirs, files in os.walk(base):
            dirs[:] = [x for x in dirs if x not in ('bin', 'obj')]
            for fn in files:
                if not fn.endswith('.cs'):
                    continue
                p = os.path.join(dirpath, fn)
                with open(p, encoding='utf-8', errors='replace') as fh:
                    code = strip_comments_and_strings(fh.read())
                k = len(PATTERN.findall(code))
                if k:
                    counts[os.path.relpath(p, src).replace(os.sep, '/')] = k
    return counts


def read_baseline(path=BASELINE):
    base = {}
    if not os.path.exists(path):
        return base
    with open(path, encoding='utf-8') as fh:
        for line in fh:
            line = line.strip()
            if not line or line.startswith('#'):
                continue
            rel, _, k = line.rpartition(':')
            base[rel] = int(k)
    return base


def write_baseline(counts, path=BASELINE):
    with open(path, 'w', encoding='utf-8') as fh:
        fh.write('# local_disk_write_check — ไฟล์:จำนวน GetCurrentDirectory() ที่มีอยู่ก่อน (ratchet · ลดได้ เพิ่มไม่ได้)\n')
        for rel in sorted(counts):
            fh.write(f'{rel}:{counts[rel]}\n')


def compare(counts, base):
    """คืน (problems, improvements)"""
    problems, improvements = [], []
    for rel, k in sorted(counts.items()):
        b = base.get(rel)
        if b is None:
            problems.append(f'{rel}: ใหม่ {k} จุด — เขียนไฟล์ผ่าน IFileAttachmentService แทน (CLAUDE.md กฎเหล็ก #4 D)')
        elif k > b:
            problems.append(f'{rel}: เพิ่มจาก {b} → {k} จุด')
        elif k < b:
            improvements.append(f'{rel}: ลดจาก {b} → {k} — อัปเดต baseline ด้วย --write-baseline')
    for rel, b in sorted(base.items()):
        if rel not in counts:
            improvements.append(f'{rel}: เหลือ 0 (baseline {b}) — ลบออกจาก baseline ด้วย --write-baseline')
    return problems, improvements


def self_test():
    with tempfile.TemporaryDirectory() as tmp:
        svc = os.path.join(tmp, 'Services', 'Implementations')
        os.makedirs(svc)
        os.makedirs(os.path.join(tmp, 'Controllers'))
        # ไฟล์ที่เรียกจริง 1 จุด + คอมเมนต์/สตริงที่เอ่ยถึง (ต้องไม่นับ)
        with open(os.path.join(svc, 'Bad.cs'), 'w', encoding='utf-8') as fh:
            fh.write('// Directory.GetCurrentDirectory() ในคอมเมนต์\n'
                     'var s = "Path.Combine(Directory.GetCurrentDirectory(), x)";\n'
                     'var p = Path.Combine(Directory.GetCurrentDirectory(), "uploads");\n')
        with open(os.path.join(svc, 'Ok.cs'), 'w', encoding='utf-8') as fh:
            fh.write('var bytes = await _files.UploadBytesAsync(cid, "SiteMedia", sid, name, ct, content, uid);\n')
        counts = scan(tmp)
        assert counts == {'Services/Implementations/Bad.cs': 1}, counts
        # ratchet: ไฟล์ใหม่ ⇒ ล้ม · ตรง baseline ⇒ ผ่าน · เพิ่ม ⇒ ล้ม · ลด ⇒ แค่รายงาน
        p, _ = compare(counts, {})
        assert p, 'ไฟล์ใหม่ต้องถูกจับ'
        p, imp = compare(counts, {'Services/Implementations/Bad.cs': 1})
        assert not p and not imp
        p, _ = compare(counts, {'Services/Implementations/Bad.cs': 0})
        assert p, 'จำนวนเพิ่มต้องถูกจับ'
        p, imp = compare(counts, {'Services/Implementations/Bad.cs': 3})
        assert not p and imp
    print('self-test ผ่าน (negative test จับไฟล์ใหม่/จำนวนเพิ่ม · ไม่นับคอมเมนต์/สตริง)')
    return 0


def main(argv):
    if '--self-test' in argv:
        return self_test()
    counts = scan()
    if '--write-baseline' in argv:
        write_baseline(counts)
        print(f'เขียน baseline {len(counts)} ไฟล์')
        return 0
    base = read_baseline()
    problems, improvements = compare(counts, base)
    for m in problems:
        print('❌ ' + m)
    for m in improvements:
        print('ℹ️  ' + m)
    total = sum(counts.values())
    print(f'local_disk_write_check: GetCurrentDirectory() ใน Services/Controllers {total} จุด / {len(counts)} ไฟล์ (baseline {sum(base.values())})')
    return 1 if problems else 0


if __name__ == '__main__':
    sys.exit(main(sys.argv[1:]))
