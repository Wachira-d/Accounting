#!/usr/bin/env python3
# -*- coding: utf-8 -*-
"""ห้ามแตะดิสก์ตรงเพิ่ม (เขียน/อ่าน/ลบ/เสิร์ฟไฟล์ของผู้ใช้) นอกที่เก็บไฟล์กลาง — ratchet (ทีม W4 · R-43 → ขยายโดยทีม FS 2026-11-02)

ที่มา (กฎเหล็ก #4 D "ไฟล์ห้ามเขียน local disk ตรง — ผ่าน abstraction กลาง")
    รอบ W4: `CmsContentService`/`CmsCommerceService` เขียน `uploads/**` ด้วย `Path.Combine(Directory.GetCurrentDirectory(), …)`
    ⇒ ไฟล์กระจายหลายโฟลเดอร์ · ไม่มีด่าน · scale หลาย instance ไม่ได้ — checker รุ่นแรกนับแค่ `GetCurrentDirectory()`
    รอบ FS (2026-11-02): มีที่เก็บกลางจริงแล้ว (`Services/Storage/IFileStorage` · Local | S3-compatible) และพบว่าตัวนับรุ่นแรก
    **ไม่เห็นผู้เขียนจริงส่วนใหญ่** — `File.WriteAllBytesAsync`/`new FileStream`/`PhysicalFile` ที่ใช้ `env.WebRootPath` ไม่ผ่าน
    `GetCurrentDirectory()` เลย (ImageProcessingService · EtaxInvoiceService.PdfA3 · ตัวเสิร์ฟสลิป 7 จุด) ⇒ ขยายรูปที่นับ

กติกา (ratchet — ไม่ใช่กวาดล้าง)
    * นับใน `Accounting/**/*.cs` (ยกเว้น bin/obj · ยกเว้น OWNER `Services/Storage/` = ที่เก็บกลางเอง) หลังตัดคอมเมนต์/สตริง/char literal:
      `GetCurrentDirectory()` · `File.<WriteAll*|AppendAll*|ReadAll*|Create|CreateText|Open|OpenRead|OpenWrite|OpenText|Delete|Move|Copy|Exists|Replace>(` ·
      `new FileStream(` · `Directory.<CreateDirectory|Delete|Move|Exists|GetFiles|EnumerateFiles>(` · `PhysicalFile(`
    * ไฟล์ที่มีอยู่แล้ว + จำนวนจุด อยู่ใน `tools/local_disk_write_baseline.txt` — ไฟล์ใหม่ที่มี หรือไฟล์เดิมที่จำนวนเพิ่ม ⇒ ล้ม
    * ไฟล์ที่จำนวนลด ⇒ รายงานให้ลด baseline (ไม่ล้ม · ratchet เดินทางเดียว) · `--write-baseline` เขียนทับ
    * จุดที่เหลือใน baseline = ไฟล์โปรแกรมที่มากับ deploy (ฟอนต์ · tessdata · โมเดล ONNX · เอกสารฐานความรู้ · ใบรับรอง e-Tax ที่ผู้ดูแลวางเอง ·
      คีย์ DataProtection) — ไม่ใช่ไฟล์ของผู้ใช้ · รายการพร้อมเหตุผลอยู่ใน erp-review/2026-11-02/FILE_STORAGE_AUDIT.md

แทนด้วย: `IFileStorage.WriteAsync/ReadAllBytesAsync/OpenReadAsync/ExistsAsync/DeleteAsync` · เสิร์ฟ `FileStorageResults.FileResultAsync` ·
ไฟล์แนบ `IFileAttachmentService` · รูป `IImageProcessingService.ProcessAndSaveAsync`

negative test: `python3 tools/local_disk_write_check.py --self-test`
"""
import os
import re
import sys
import tempfile

ROOT = os.path.dirname(os.path.dirname(os.path.abspath(__file__)))
SRC = os.path.join(ROOT, 'Accounting')
BASELINE = os.path.join(ROOT, 'tools', 'local_disk_write_baseline.txt')
# OWNER — ไฟล์ของที่เก็บกลางที่แตะดิสก์ได้ (ยกเว้นรายไฟล์ ไม่ใช่ทั้งโฟลเดอร์ — ไฟล์อื่นใน Services/Storage ยังถูกนับ · ฝ่ายค้าน N1)
OWNER_FILES = (
    'Services/Storage/LocalDiskFileStorage.cs',
    'Services/Storage/FileStorageResults.cs',
    'Services/Storage/FileStorageLocator.cs',
    'Services/Storage/FileStorageDefaults.cs',
)
SKIP_DIRS = {'bin', 'obj', 'wwwroot'}
PATTERNS = [
    re.compile(r'\bGetCurrentDirectory\s*\(\s*\)'),
    re.compile(r'\bFile\s*\.\s*(?:WriteAll\w*|AppendAll\w*|ReadAll\w*|Create|CreateText|Open|OpenRead|OpenWrite|OpenText|Delete|Move|Copy|Exists|Replace)\s*\('),
    re.compile(r'\bnew\s+(?:System\.IO\.)?FileStream\s*\('),
    re.compile(r'\bDirectory\s*\.\s*(?:CreateDirectory|Delete|Move|Exists|GetFiles|EnumerateFiles)\s*\('),
    re.compile(r'\bPhysicalFile\s*\('),
    re.compile(r'\bnew\s+(?:System\.IO\.)?(?:FileInfo|DirectoryInfo)\s*\('),
]


def strip_comments_and_strings(text: str) -> str:
    """ตัดคอมเมนต์ + string/char literal (คงจำนวนบรรทัด) — ชื่อ API ที่เอ่ยในคอมเมนต์/ข้อความไม่ใช่การเรียก ·
    รองรับ verbatim (@"…" / $@"…" — `""` = อัญประกาศ, backslash ไม่ใช่ escape) และ raw string (\"\"\"…\"\"\")"""
    out = []
    i, n = 0, len(text)
    while i < n:
        c = text[i]
        if text.startswith('//', i):
            j = text.find('\n', i)
            i = n if j < 0 else j
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
            verbatim = i > 0 and (text[i - 1] == '@' or (text[i - 1] == '$' and i > 1 and text[i - 2] == '@'))
            interp = i > 0 and (text[i - 1] == '$' or (text[i - 1] == '@' and i > 1 and text[i - 2] == '$'))
            j = i + 1
            holes = []
            while j < n:
                if interp and text[j] == '{':
                    if j + 1 < n and text[j + 1] == '{':
                        j += 2
                        continue
                    # รูอินเทอร์โพเลต {expr} = โค้ดจริง — นับด้วย (ฝ่ายค้าน N1: $"{File.Delete(p)}" เคยหลุด)
                    depth, k = 1, j + 1
                    while k < n and depth:
                        if text[k] == '{':
                            depth += 1
                        elif text[k] == '}':
                            depth -= 1
                        k += 1
                    holes.append(text[j + 1:k - 1])
                    j = k
                    continue
                if verbatim:
                    if text[j] == '"':
                        if j + 1 < n and text[j + 1] == '"':
                            j += 2
                            continue
                        break
                else:
                    if text[j] == '\\':
                        j += 2
                        continue
                    if text[j] == '"' or text[j] == '\n':
                        break
                j += 1
            seg = text[i:j + 1]
            out.append('\n' * seg.count('\n'))
            for h in holes:
                out.append(' ' + strip_comments_and_strings(h).replace('\n', ' ') + ' ')
            i = j + 1
            continue
        if c == "'":
            # char literal: 'x' · '\n' · '\u0041' — อัญประกาศคู่ใน '"' ต้องไม่เปิดสตริง
            m = re.match(r"'(?:\\.[0-9A-Fa-f]{0,7}|[^'\\\n])'", text[i:i + 12])
            if m:
                i += m.end()
                continue
        out.append(c)
        i += 1
    return ''.join(out)


def count_text(code: str) -> int:
    code = strip_comments_and_strings(code)
    return sum(len(p.findall(code)) for p in PATTERNS)


def scan(src=SRC):
    counts = {}
    for dirpath, dirs, files in os.walk(src):
        dirs[:] = [x for x in dirs if x not in SKIP_DIRS]
        for fn in files:
            if not fn.endswith('.cs'):
                continue
            p = os.path.join(dirpath, fn)
            rel = os.path.relpath(p, src).replace(os.sep, '/')
            if rel in OWNER_FILES:
                continue
            with open(p, encoding='utf-8', errors='replace') as fh:
                k = count_text(fh.read())
            if k:
                counts[rel] = k
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
        fh.write('# local_disk_write_check — ไฟล์:จำนวนจุดแตะดิสก์ตรง (GetCurrentDirectory · File.* · FileStream · Directory.* · PhysicalFile)\n')
        fh.write('# ratchet: ลดได้ เพิ่มไม่ได้ · เหตุผลของจุดที่เหลือ: erp-review/2026-11-02/FILE_STORAGE_AUDIT.md\n')
        for rel in sorted(counts):
            fh.write(f'{rel}:{counts[rel]}\n')


def compare(counts, base):
    """คืน (problems, improvements)"""
    problems, improvements = [], []
    for rel, k in sorted(counts.items()):
        b = base.get(rel)
        if b is None:
            problems.append(f'{rel}: ใหม่ {k} จุด — ใช้ IFileStorage / IFileAttachmentService / FileStorageResults แทน (CLAUDE.md กฎเหล็ก #4 D)')
        elif k > b:
            problems.append(f'{rel}: เพิ่มจาก {b} → {k} จุด — ใช้ IFileStorage แทนการแตะดิสก์ตรง')
        elif k < b:
            improvements.append(f'{rel}: ลดจาก {b} → {k} — อัปเดต baseline ด้วย --write-baseline')
    for rel, b in sorted(base.items()):
        if rel not in counts:
            improvements.append(f'{rel}: เหลือ 0 (baseline {b}) — ลบออกจาก baseline ด้วย --write-baseline')
    return problems, improvements


def self_test():
    with tempfile.TemporaryDirectory() as tmp:
        svc = os.path.join(tmp, 'Services', 'Implementations')
        owner = os.path.join(tmp, 'Services', 'Storage')
        ctl = os.path.join(tmp, 'Controllers')
        for d in (svc, owner, ctl):
            os.makedirs(d)
        # ผู้แตะดิสก์จริง 6 จุด (GetCurrentDirectory · File.WriteAllBytesAsync · FileStream · Directory.CreateDirectory · File.Exists · File.Delete) + คอมเมนต์/สตริง/char literal/verbatim ที่เอ่ยถึง (ต้องไม่นับ)
        with open(os.path.join(svc, 'Bad.cs'), 'w', encoding='utf-8') as fh:
            fh.write('// Directory.GetCurrentDirectory() · File.WriteAllBytesAsync(x) ในคอมเมนต์\n'
                     'var s = "Path.Combine(Directory.GetCurrentDirectory(), x)";\n'
                     'var q = \'"\'; var v = @"C:\\dir\\"; var w = @"say ""File.Delete(p)"" ok";\n'
                     'var p = Path.Combine(Directory.GetCurrentDirectory(), "uploads");\n'
                     'await System.IO.File.WriteAllBytesAsync(p, bytes);\n'
                     'using var fs = new FileStream(p, FileMode.Create);\n'
                     'Directory.CreateDirectory(dir);\n'
                     'if (File.Exists(p)) File.Delete(p);\n')
        with open(os.path.join(ctl, 'Serve.cs'), 'w', encoding='utf-8') as fh:
            fh.write('return PhysicalFile(path, "image/png");\nreturn File(bytes, "image/png");\nvar n = file.OpenReadStream();\n')
        with open(os.path.join(svc, 'Ok.cs'), 'w', encoding='utf-8') as fh:
            fh.write('await _storage.WriteAsync(path, bytes, "image/png", ct);\nvar b = await _files.UploadBytesAsync(cid, "SiteMedia", sid, name, ct, content, uid);\n'
                     'return await _storage.FileResultAsync(p, "image/png") ?? NotFound();\n')
        # OWNER ถูกยกเว้นรายไฟล์ · ไฟล์อื่นในโฟลเดอร์เดียวกันยังถูกนับ · รูอินเทอร์โพเลต/FileInfo ถูกนับ
        with open(os.path.join(owner, 'LocalDiskFileStorage.cs'), 'w', encoding='utf-8') as fh:
            fh.write('await File.WriteAllBytesAsync(full, content, ct);\nDirectory.CreateDirectory(d);\n')
        with open(os.path.join(owner, 'Sneaky.cs'), 'w', encoding='utf-8') as fh:
            fh.write('var m = $"ok {File.Delete(p)} {{File.Delete(q)}}";\nvar len = new FileInfo(p).Length;\nvar t = $"{x}";\n')
        counts = scan(tmp)
        assert counts == {'Services/Implementations/Bad.cs': 6, 'Controllers/Serve.cs': 1, 'Services/Storage/Sneaky.cs': 2}, counts
        # ratchet: ไฟล์ใหม่ ⇒ ล้ม · ตรง baseline ⇒ ผ่าน · เพิ่ม ⇒ ล้ม · ลด ⇒ แค่รายงาน
        counts.pop('Services/Storage/Sneaky.cs')
        p, _ = compare(counts, {})
        assert len(p) == 2, 'ไฟล์ใหม่ต้องถูกจับ'
        p, imp = compare(counts, dict(counts))
        assert not p and not imp
        p, _ = compare(counts, {'Services/Implementations/Bad.cs': 5, 'Controllers/Serve.cs': 1})
        assert p, 'จำนวนเพิ่มต้องถูกจับ'
        p, imp = compare(counts, {'Services/Implementations/Bad.cs': 9, 'Controllers/Serve.cs': 1})
        assert not p and imp
    # baseline จริงต้องไม่มีไฟล์ใน OWNER (ไม่งั้นแปลว่า scan ไม่ได้ยกเว้นจริง)
    assert not any(k in OWNER_FILES for k in read_baseline()), 'baseline มีไฟล์ OWNER'
    print('self-test ผ่าน (จับ GetCurrentDirectory/File.*/FileStream/Directory.*/PhysicalFile · ไม่นับคอมเมนต์/สตริง/char/verbatim · ยกเว้นเฉพาะไฟล์ OWNER · นับรูอินเทอร์โพเลต/FileInfo)')
    return 0


def main(argv):
    if '--self-test' in argv:
        return self_test()
    counts = scan()
    if '--write-baseline' in argv:
        write_baseline(counts)
        print(f'เขียน baseline {len(counts)} ไฟล์ · {sum(counts.values())} จุด')
        return 0
    base = read_baseline()
    problems, improvements = compare(counts, base)
    for m in problems:
        print('❌ ' + m)
    for m in improvements:
        print('ℹ️  ' + m)
    total = sum(counts.values())
    print(f'local_disk_write_check: แตะดิสก์ตรง {total} จุด / {len(counts)} ไฟล์ (baseline {sum(base.values())})')
    return 1 if problems else 0


if __name__ == '__main__':
    sys.exit(main(sys.argv[1:]))
