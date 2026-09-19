#!/usr/bin/env python3
# -*- coding: utf-8 -*-
"""public static method ใน `Accounting/Helpers/` ที่ไม่มีผู้เรียกนอกไฟล์ตัวเอง — ratchet กับ baseline

ที่มา (รอบ 169 — REGRESSION_ROOT_CAUSE_2026-09-18.md): CLAUDE.md เรียก "ของที่สร้างไว้แล้ว
ไม่ได้ถูกเรียกใช้" ว่าเป็น defect class ที่ใหญ่ที่สุดของเรพ (`CanShareMoneyAggregates` k=3 ·
`PdpaSignupConsent` ที่ doc-comment เรียกตัวเองว่า "แหล่งความจริงเดียว" · `SsoWageBase` ที่
comment บอกว่า "ด่านนำส่งจะบล็อกให้" ฯลฯ) แต่ **ไม่มีเครื่องมือสักตัวที่วัดมัน** — ทุกครั้งที่จับได้
คือจับด้วยตา + grep มือ. วันที่เขียน checker นี้เรพมี public static method ใน Helpers ~400 ตัว
และ 55 ตัวไม่มีผู้เรียกนอกไฟล์ตัวเอง (2 ไฟล์ตายทั้งไฟล์)

กติกา (ratchet — ไม่ใช่กวาดล้าง):
  * รายชื่อที่ "ตายอยู่แล้ววันนี้" อยู่ใน `tools/dead_helper_baseline.txt` — checker **ไม่ล้ม**
    กับของเดิม (55 ตัวต้องถูกตัดสินทีละตัวว่า "ต่อสาย หรือ ลบ" เป็นงานแยก ห้ามเดาแทนเจ้าของ)
  * ล้มเฉพาะเมื่อมี method ตายตัว**ใหม่**ที่ไม่อยู่ใน baseline = คอมมิตนี้เพิ่ง "สร้างของแล้วไม่ต่อสาย"
    (หรือถอดผู้เรียกตัวสุดท้ายออกโดยไม่ลบ helper)
  * baseline ที่**กลับมามีชีวิต** จะถูกรายงานให้ลบออกจาก baseline (ไม่ล้ม — แต่ควรเก็บกวาดในคอมมิต
    เดียวกัน เพื่อให้ ratchet เดินได้ทางเดียว) · `--write-baseline` เขียนทับ baseline ด้วยสภาพปัจจุบัน
  * "ผู้เรียก" = ไฟล์ .cs ใต้ `Accounting/` (ไม่รวมเทสต์ — เทสต์ที่อ้างถึงแต่ไม่มีโค้ดจริงเรียก ก็ยัง
    เป็น dead helper: เทสต์ผ่านทุกวันบนของที่ระบบไม่เคยเดิน) นับ**นอกคอมเมนต์** เพราะ doc-comment
    ที่เขียนว่า "ดู `Foo.Bar`" ไม่ใช่การเรียก (บทเรียน `CanShareMoneyAggregates` ที่ doc-comment
    อ้างว่าด่านอยู่ที่ X แต่ไม่มีใครเรียก)
  * over-approximate ฝั่ง "มีชีวิต" ได้ (ชื่อสั้นอย่าง `For`/`Split` แมตช์ที่อื่นได้ = ถือว่ามีชีวิต)
    เพราะทิศนั้นแค่พลาด dead helper ตัวหนึ่ง · ฝั่ง "ตาย" ต้องแม่น — checker ที่ฟ้องผิด = checker ที่พัง

ใช้: python3 tools/dead_helper_check.py [--write-baseline] [--self-test] [--all]
  --all  พิมพ์รายการตายทั้งหมด (รวม baseline) พร้อมป้ายว่ามีเทสต์อ้างถึงไหม — ใช้ตอนไล่ตัดสิน "ต่อสาย/ลบ"
"""
import os
import re
import sys
import tempfile

ROOT = os.path.dirname(os.path.dirname(os.path.abspath(__file__)))
BASELINE = os.path.join(ROOT, 'tools', 'dead_helper_baseline.txt')

# public static <return type> Name( — ยกเว้น operator / property / constructor (ไม่มี return type)
METHOD_RE = re.compile(
    r'public\s+static\s+(?:async\s+|extern\s+|unsafe\s+|partial\s+)*'
    r'(?!class\b|struct\b|record\b|enum\b|interface\b|readonly\b|const\b|event\b|implicit\b|explicit\b|operator\b)'
    r'[\w<>\[\]?,.\s()]+?\s+(\w+)\s*(?:<[^>()]*>)?\s*\(')
SKIP_DIRS = {'bin', 'obj', 'node_modules', '.git'}


def strip_comments(text):
    """ตัด // และ /* */ ออก โดยคงจำนวนบรรทัด — ไม่แตะ `//` ที่ตามหลัง `:` (URL ในสตริง)"""
    text = re.sub(r'/\*.*?\*/', lambda m: re.sub(r'[^\n]', ' ', m.group(0)), text, flags=re.S)
    return re.sub(r'(?<!:)//[^\n]*', '', text)


def collect(root):
    helpers_dir = os.path.join(root, 'Accounting', 'Helpers')
    if not os.path.isdir(helpers_dir):
        return {}, {}
    sources = {}
    for base, dirs, files in os.walk(os.path.join(root, 'Accounting')):
        dirs[:] = [d for d in dirs if d not in SKIP_DIRS]
        for f in files:
            if f.endswith('.cs'):
                p = os.path.join(base, f)
                try:
                    with open(p, encoding='utf-8', errors='ignore') as fh:
                        sources[p] = strip_comments(fh.read())
                except OSError:
                    pass
    tests = ''
    tdir = os.path.join(root, 'Accounting.Tests')
    if os.path.isdir(tdir):
        for base, dirs, files in os.walk(tdir):
            dirs[:] = [d for d in dirs if d not in SKIP_DIRS]
            for f in files:
                if f.endswith('.cs'):
                    try:
                        with open(os.path.join(base, f), encoding='utf-8', errors='ignore') as fh:
                            tests += fh.read()
                    except OSError:
                        pass
    methods = {}   # (file, name) -> True
    for p, text in sources.items():
        if not p.startswith(helpers_dir + os.sep):
            continue
        for name in set(METHOD_RE.findall(text)):
            if name in ('Main',):
                continue
            methods[(os.path.basename(p), name)] = p
    dead = {}
    internal_only = set()
    for (fname, name), p in methods.items():
        pat = re.compile(r'\b' + re.escape(name) + r'\b')
        alive = any(pat.search(t) for q, t in sources.items() if q != p)
        if not alive:
            dead[(fname, name)] = bool(re.search(r'\b' + re.escape(name) + r'\b', tests))
            # ★ รอบ 183 — แยกสองอาการที่เดิมกองรวมกัน:
            #   · "ไม่มีใครเรียกเลย"        = ของที่สร้างแล้วลืมต่อสาย (ปัญหาจริง)
            #   · "เรียกในไฟล์ตัวเองเท่านั้น" = ควรเป็น private (ระเบียบ ไม่ใช่ช่องโหว่)
            # ทีมตัดสินใจฝ่ายข้อมูลวัดแล้วพบว่า ~53% ของ baseline เป็นอาการที่สอง
            # ⇒ baseline ที่ปนสองอาการทำให้คนเลิกอ่าน = ratchet ที่ไม่มีใครเชื่อ
            # (F2 ข้อ 6: checker ที่ฟ้องผิด = checker ที่พัง)
            own = sources.get(p, '')
            if len(pat.findall(own)) > 1:      # นิยาม + อย่างน้อยหนึ่งการใช้งาน
                internal_only.add((fname, name))
    return methods, dead, internal_only


def read_baseline(path):
    out = set()
    if not os.path.exists(path):
        return out
    with open(path, encoding='utf-8') as fh:
        for line in fh:
            line = line.split('#', 1)[0].strip()
            if line:
                parts = line.split()
                if len(parts) >= 2:
                    out.add((parts[0], parts[1]))
    return out


def write_baseline(path, dead):
    with open(path, 'w', encoding='utf-8') as fh:
        fh.write('# dead_helper_check baseline — public static method ใน Helpers ที่ยังไม่มีผู้เรียก ณ วันเขียน\n')
        fh.write('# ทุกแถวต้องถูกตัดสิน "ต่อสาย หรือ ลบ" แล้วตัดออก — ห้ามเพิ่มแถวใหม่เพื่อให้ checker เขียว\n')
        for (f, n) in sorted(dead):
            fh.write(f'{f} {n}\n')


def run(root, baseline_path, show_all=False):
    methods, dead, internal_only = collect(root)
    baseline = read_baseline(baseline_path)
    new = sorted(k for k in dead if k not in baseline)
    revived = sorted(k for k in baseline if k not in dead)
    orphan = {k for k in dead if k not in internal_only}
    print(f'public static method ใน Helpers: {len(methods)} · ไม่มีผู้เรียกนอกไฟล์: {len(dead)} '
          f'(baseline {len(baseline)})')
    print(f'   ├─ ไม่มีใครเรียกเลย (ของที่ลืมต่อสาย)        : {len(orphan)}')
    print(f'   └─ เรียกในไฟล์ตัวเองเท่านั้น (ควรเป็น private): {len(internal_only)}')
    if show_all:
        for (f, n) in sorted(dead):
            tag = 'มีเทสต์' if dead[(f, n)] else 'ไม่มีเทสต์'
            kind = 'INTERNAL' if (f, n) in internal_only else 'ORPHAN'
            print(f'   [{kind:8}] {f} {n} [{tag}]{"" if (f, n) in baseline else "  ← ใหม่"}')
    for (f, n) in revived:
        print(f'ℹ️  กลับมามีผู้เรียกแล้ว — ตัดออกจาก baseline ได้: {f} {n}')
    if new:
        print(f'❌ dead helper ใหม่ {len(new)} ตัว (สร้างแล้วไม่ต่อสาย หรือถอดผู้เรียกตัวสุดท้ายโดยไม่ลบ):')
        for (f, n) in new:
            tag = 'มีเทสต์แต่ระบบไม่เคยเดิน' if dead[(f, n)] else 'ไม่มีทั้งผู้เรียกและเทสต์'
            kind = ('เรียกในไฟล์ตัวเองเท่านั้น → ทำเป็น private'
                    if (f, n) in internal_only else 'ไม่มีใครเรียกเลย → ต่อสาย หรือ ลบ')
            print(f'   Accounting/Helpers/{f}: {n}  [{tag}] — {kind}')
        print('   → ต่อสายเข้าเส้นที่ควรเรียก หรือลบทิ้ง — ห้ามเติมลง baseline เพื่อให้ผ่าน')
        return 1
    print('✅ ไม่มี dead helper ใหม่')
    return 0


def self_test():
    """negative test: helper ที่ไม่มีผู้เรียกต้องถูกฟ้อง · มีผู้เรียกต้องไม่ฟ้อง · อยู่ใน baseline ไม่ฟ้อง ·
    ผู้เรียกที่อยู่แค่ในคอมเมนต์ต้องไม่นับ · เทสต์อย่างเดียวไม่นับเป็นผู้เรียก"""
    fails = 0
    with tempfile.TemporaryDirectory() as tmp:
        hd = os.path.join(tmp, 'Accounting', 'Helpers')
        sd = os.path.join(tmp, 'Accounting', 'Services')
        td = os.path.join(tmp, 'Accounting.Tests')
        for d in (hd, sd, td):
            os.makedirs(d)
        with open(os.path.join(hd, 'Foo.cs'), 'w', encoding='utf-8') as fh:
            fh.write('public static class Foo {\n'
                     '  public static int Orphan(int x) => x;\n'
                     '  public static async Task<int> Wired(int x) => x;\n'
                     '  public static bool CommentOnly() => true;\n'
                     '  public static bool TestOnly() => true;\n'
                     '  public static Dictionary<string, int> Generic<T>(T v) => null;\n'
                     '  public static Foo operator +(Foo a, Foo b) => a;\n'
                     '  public static string Prop { get; } = "";\n'
                     '  private static int Hidden() => 1;\n'
                     '}\n')
        with open(os.path.join(sd, 'Svc.cs'), 'w', encoding='utf-8') as fh:
            fh.write('class Svc {\n'
                     '  // CommentOnly() ถูกเรียกที่นี่ — เป็นแค่คอมเมนต์\n'
                     '  /// <summary>ดู Foo.CommentOnly</summary>\n'
                     '  void Go() { var a = Foo.Wired(1); var u = "https://x/y"; var b = Foo.Generic(2); }\n'
                     '}\n')
        with open(os.path.join(td, 'FooTests.cs'), 'w', encoding='utf-8') as fh:
            fh.write('class FooTests { void T() { Foo.TestOnly(); Foo.Orphan(1); } }\n')
        methods, dead, internal_only = collect(tmp)
        names = {n for (_, n) in methods}
        for expect_in in ('Orphan', 'Wired', 'CommentOnly', 'TestOnly', 'Generic'):
            if expect_in not in names:
                print(f'self-test ล้ม: ไม่เจอ method {expect_in} ในสแกน'); fails += 1
        for expect_out in ('Hidden', 'Prop', 'operator'):
            if expect_out in names:
                print(f'self-test ล้ม: {expect_out} ไม่ควรถูกนับเป็น method'); fails += 1
        dead_names = {n for (_, n) in dead}
        if 'Orphan' not in dead_names:
            print('self-test ล้ม: Orphan ต้องถูกฟ้อง'); fails += 1
        if 'TestOnly' not in dead_names:
            print('self-test ล้ม: TestOnly (มีแต่เทสต์เรียก) ต้องถูกฟ้อง'); fails += 1
        if not dead.get(('Foo.cs', 'TestOnly'), False):
            print('self-test ล้ม: TestOnly ต้องติดป้ายว่ามีเทสต์'); fails += 1
        if 'CommentOnly' not in dead_names:
            print('self-test ล้ม: ผู้เรียกที่อยู่แค่ในคอมเมนต์ต้องไม่นับ'); fails += 1
        if 'Wired' in dead_names or 'Generic' in dead_names:
            print('self-test ล้ม: Wired/Generic มีผู้เรียกจริง (Generic อยู่หลัง URL บรรทัดเดียวกัน) ต้องไม่ฟ้อง'); fails += 1
        # baseline ratchet
        bl = os.path.join(tmp, 'baseline.txt')
        write_baseline(bl, {('Foo.cs', 'Orphan'): False, ('Foo.cs', 'Gone'): False})
        import io, contextlib
        buf = io.StringIO()
        with contextlib.redirect_stdout(buf):
            rc = run(tmp, bl)
        out = buf.getvalue()
        if rc != 1 or 'TestOnly' not in out or 'CommentOnly' not in out:
            print('self-test ล้ม: dead ใหม่นอก baseline ต้องทำให้ล้มพร้อมชื่อ'); fails += 1
        if 'Orphan' in out.split('❌')[-1]:
            print('self-test ล้ม: ตัวที่อยู่ใน baseline ต้องไม่ถูกฟ้องว่าใหม่'); fails += 1
        if 'Gone' not in out:
            print('self-test ล้ม: baseline ที่หายไปแล้วต้องถูกรายงานให้ตัดออก'); fails += 1
        write_baseline(bl, dead)
        buf = io.StringIO()
        with contextlib.redirect_stdout(buf):
            rc = run(tmp, bl)
        if rc != 0:
            print('self-test ล้ม: baseline ครบต้องผ่าน'); fails += 1
    print('self-test: ' + ('ผ่าน' if fails == 0 else f'ล้ม {fails} ข้อ'))
    return 1 if fails else 0


if __name__ == '__main__':
    if '--self-test' in sys.argv:
        sys.exit(self_test())
    if '--write-baseline' in sys.argv:
        _, dead, _ = collect(ROOT)
        write_baseline(BASELINE, dead)
        print(f'เขียน baseline {len(dead)} แถว → {os.path.relpath(BASELINE, ROOT)}')
        sys.exit(0)
    sys.exit(run(ROOT, BASELINE, show_all='--all' in sys.argv))
