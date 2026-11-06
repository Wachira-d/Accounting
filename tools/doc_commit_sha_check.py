#!/usr/bin/env python3
# -*- coding: utf-8 -*-
"""ตรวจว่า sha ที่เอกสารอ้างถึง อยู่บน branch จริงหรือไม่ + placeholder "เติมในคอมมิตตามหลัง" ที่ค้างเกินคอมมิตตามหลัง

ที่มา (รอบ 167): `DOCUMENT_FLOW.md` รอบ 163/164 จด `commit dbaa778` / `commit 0c80a7b`
ซึ่งเป็น sha **ก่อน amend** — commit ถูกแก้ทีหลัง (ซ่อม build ที่พัง / เติมไฟล์ที่ลืม)
sha เดิมจึงกลายเป็น dangling object: `git cat-file -t` ยังตอบว่า "commit" (จึงดูเหมือนถูก)
แต่ `git merge-base --is-ancestor` ตอบว่าไม่อยู่ในประวัติ และของจริงจะหายหลัง `git gc`
⇒ คนที่ตามรอยว่า "พฤติกรรมนี้เปลี่ยนที่คอมมิตไหน" หาไม่เจอ

กติกา: รูปแบบที่ตรวจคือ `commit <sha>` และ `✅ <sha>` (สองรูปที่เรพนี้ใช้จริง) —
sha ที่เอ่ยลอย ๆ ในเนื้อความ (เช่นตัวอย่างในบทเรียนที่จงใจอ้างถึง sha ที่ตายแล้ว)
ไม่ถูกตรวจ เพราะไม่ได้ทำหน้าที่ "ชี้ไปยังคอมมิตที่แก้เรื่องนี้"

รอบ 250 (AUDIT_249 WP-01 ข้อ 4–5):
  (ก) clone ตื้น (`git rev-parse --is-shallow-repository` = true) ⇒ `git fetch --unshallow` ก่อน · fetch ไม่ได้ ⇒ sha ที่หา object ไม่เจอ
      / ตัดสินบรรพบุรุษไม่ได้ ไม่นับเป็นรายการแดง (พิมพ์ "clone ตื้น — เชื่อผล CI" · CI checkout ด้วย fetch-depth 0)
      ที่มา: เครื่อง agent เป็น clone ตื้น 446 คอมมิต ⇒ ฟ้องผิด 3–4 sha ที่มีจริงบน GitHub
  (ข) placeholder `commit <เติมในคอมมิตตามหลัง>` / `sha เติมในคอมมิตตามหลัง` ใน DOCUMENT_FLOW · CHANGELOG · ACCOUNT_STRUCTURE · TEST_PLAN
      ที่บรรทัดนั้น (git blame) อายุ > 1 คอมมิตนับจาก HEAD = **fail** — "คอมมิตตามหลัง" คือคอมมิตถัดไปเท่านั้น
      (ที่มา: เติม sha ไม่ครบ 2 จาก 10 · ค้าง 102 บรรทัด) · ของค้างก่อนกติกานี้อยู่ใน baseline ratchet
      `tools/doc_commit_sha_placeholder_baseline.txt` (เติม sha แล้ว ⇒ แถวนั้นหมดความหมาย — ลบออกได้ ไม่ฟ้อง)
  (ค) ✅ ตามด้วยเลขประจำตัวผู้เสียภาษี 13 หลัก (checksum mod 11 ถูก) ไม่ใช่ sha — ไม่ตรวจ (sha ย่อ 13 ตัวที่เป็นเลขฐานสิบล้วน
      *และ* checksum ถูกบังเอิญมีโอกาส ~0.02% ของ ~0.2% — sha ย่อจริงในเรพนี้ยาว 7–8 ตัว)
  `--self-test` = negative test ในเรพชั่วคราว (CLAUDE.md F2 ข้อ 6)

placeholder ที่ยังไม่ใส่ sha (`<pending>` / `<new-sha>` / `<sha>`) ข้ามให้ —
มันบอกตัวเองอยู่แล้วว่ายังไม่เสร็จ
"""
import hashlib
import os
import re
import shutil
import subprocess
import sys
import tempfile

ROOT = os.path.dirname(os.path.dirname(os.path.abspath(__file__)))
PATTERN = re.compile(r'(?:commit|✅)\s+`?([0-9a-f]{7,40})`?')
SKIP_DIRS = {'.git', 'node_modules', 'bin', 'obj', ".claude"}
PLACEHOLDER_FILES = ('DOCUMENT_FLOW.md', 'CHANGELOG.md', 'ACCOUNT_STRUCTURE.md', 'TEST_PLAN.md')
# `commit <เติมในคอมมิตตามหลัง>` · `commit (เติม…)` · `commit: เติม…` · `sha เติม…` — ไม่นับที่อยู่ใน backtick (ข้อความที่ "พูดถึง" placeholder)
PLACEHOLDER = re.compile(r'(?<!`)(?:commit|sha)\s*:?\s*[<(]?เติมในคอมมิตตามหลัง')
BASELINE_NAME = os.path.join('tools', 'doc_commit_sha_placeholder_baseline.txt')
MAX_PLACEHOLDER_AGE = 1


def git(root, *args, timeout=None):
    return subprocess.run(['git', '-C', root] + list(args), capture_output=True, text=True, timeout=timeout)


def is_thai_tax_id(s):
    """เลขผู้เสียภาษี 13 หลัก + checksum mod 11 (สูตรเดียวกับ Helpers/ThaiTaxId)"""
    if len(s) != 13 or not s.isdigit():
        return False
    total = sum(int(s[i]) * (13 - i) for i in range(12))
    return (11 - total % 11) % 10 == int(s[12])


def ensure_history(root, allow_fetch=True):
    """คืน True = ประวัติครบ (ตัดสินได้) · False = clone ตื้นและ unshallow ไม่ได้"""
    r = git(root, 'rev-parse', '--is-shallow-repository')
    if r.stdout.strip() != 'true':
        return True
    if allow_fetch and os.environ.get('DOC_SHA_NO_FETCH') != '1':
        try:
            f = git(root, 'fetch', '--unshallow', '--quiet', timeout=300)
            if f.returncode == 0 and git(root, 'rev-parse', '--is-shallow-repository').stdout.strip() != 'true':
                return True
        except subprocess.TimeoutExpired:
            pass
    return False


def line_key(rel, text):
    return rel + '\t' + hashlib.sha1(text.strip().encode('utf-8')).hexdigest()[:16]


def load_baseline(root):
    keys = set()
    p = os.path.join(root, BASELINE_NAME)
    if os.path.exists(p):
        for raw in open(p, encoding='utf-8'):
            raw = raw.rstrip('\n')
            if not raw or raw.startswith('#'):
                continue
            parts = raw.split('\t')
            if len(parts) >= 2:
                keys.add(parts[0] + '\t' + parts[1])
    return keys


def blame_commits(root, rel):
    """เลขบรรทัด → sha ของคอมมิตที่เขียนบรรทัดนั้น (0000… = ยังไม่ commit)"""
    r = git(root, 'blame', '--porcelain', '--', rel)
    out = {}
    if r.returncode != 0:
        return out
    cur = None
    for ln in r.stdout.split('\n'):
        m = re.match(r'^([0-9a-f]{40}) \d+ (\d+)', ln)
        if m:
            cur = (m.group(1), int(m.group(2)))
            out[cur[1]] = cur[0]
    return out


def placeholder_problems(root, full_history):
    """(ข) — รายการ (rel, no, อายุ, ข้อความ) ที่ค้างเกินกติกาและไม่อยู่ใน baseline"""
    if not full_history:
        return [], 0
    baseline = load_baseline(root)
    problems, grandfathered = [], 0
    age_cache = {}
    for rel in PLACEHOLDER_FILES:
        path = os.path.join(root, rel)
        if not os.path.exists(path):
            continue
        lines = open(path, encoding='utf-8').read().split('\n')
        hits = [(i + 1, t) for i, t in enumerate(lines) if PLACEHOLDER.search(t)]
        if not hits:
            continue
        blame = blame_commits(root, rel)
        for no, text in hits:
            sha = blame.get(no)
            if sha is None or set(sha) == {'0'}:
                continue   # ยังไม่ commit = อายุ 0
            if sha not in age_cache:
                c = git(root, 'rev-list', '--count', sha + '..HEAD')
                age_cache[sha] = int(c.stdout.strip() or 0) if c.returncode == 0 else 0
            age = age_cache[sha]
            if age <= MAX_PLACEHOLDER_AGE:
                continue
            if line_key(rel, text) in baseline:
                grandfathered += 1
                continue
            problems.append((rel, no, age, text.strip()[:120]))
    return problems, grandfathered


def check(root, allow_fetch=True, out=print):
    if git(root, 'rev-parse', '--git-dir').returncode != 0:
        out('ไม่ใช่ git repo — ข้าม')
        return 0
    full_history = ensure_history(root, allow_fetch)

    md_files = []
    for base, dirs, files in os.walk(root):
        dirs[:] = [d for d in dirs if d not in SKIP_DIRS]
        for f in files:
            if f.endswith('.md'):
                md_files.append(os.path.join(base, f))

    problems, unknown = [], []
    cache = {}
    for path in sorted(md_files):
        try:
            with open(path, encoding='utf-8') as fh:
                lines = fh.readlines()
        except (OSError, UnicodeDecodeError):
            continue
        for no, line in enumerate(lines, 1):
            for sha in PATTERN.findall(line):
                if is_thai_tax_id(sha):
                    continue   # (ค) ✅ เลขผู้เสียภาษี ไม่ใช่ sha
                if sha not in cache:
                    exists = git(root, 'cat-file', '-t', sha).stdout.strip() == 'commit'
                    on_branch = exists and git(root, 'merge-base', '--is-ancestor', sha, 'HEAD').returncode == 0
                    cache[sha] = (exists, on_branch)
                exists, on_branch = cache[sha]
                if on_branch:
                    continue
                rel = os.path.relpath(path, root)
                if not full_history:
                    unknown.append((rel, no, sha))   # (ก) clone ตื้น — ตัดสินไม่ได้ ไม่ใช่รายการแดง
                    continue
                why = ('object ไม่มีในเรพนี้เลย' if not exists
                       else 'มี object แต่ **ไม่อยู่ในประวัติของ HEAD** (น่าจะถูก amend/rebase)')
                problems.append((rel, no, sha, why))

    stale, grandfathered = placeholder_problems(root, full_history)

    if unknown:
        out('doc_commit_sha_check: clone ตื้น — เชื่อผล CI (unshallow ไม่ได้ · sha %d ตัวตรวจไม่ได้ในเครื่องนี้ · '
            'กติกาอายุ placeholder ข้าม)' % len(unknown))
    elif not full_history:
        out('doc_commit_sha_check: clone ตื้น — เชื่อผล CI (กติกาอายุ placeholder ข้าม)')
    if not problems and not stale:
        out('doc_commit_sha_check: ผ่าน — sha ที่เอกสารอ้างถึงอยู่บน branch ครบทุกตัว'
            + (' · placeholder ค้างก่อนกติกา %d บรรทัด (baseline)' % grandfathered if grandfathered else ''))
        return 0

    if problems:
        out('doc_commit_sha_check: พบ sha ที่เอกสารอ้างแต่ตามรอยไม่ได้\n')
        for rel, no, sha, why in problems:
            out('  %s:%d  %s — %s' % (rel, no, sha, why))
        out('\nแก้: หา sha จริงด้วย `git log --oneline` แล้วแทนที่ '
            '(sha ที่จดไว้ก่อน commit จะตายทันทีที่ amend)')
    if stale:
        out('\ndoc_commit_sha_check: placeholder "เติมในคอมมิตตามหลัง" ค้างเกิน %d คอมมิต\n' % MAX_PLACEHOLDER_AGE)
        for rel, no, age, text in stale:
            out('  %s:%d  (อายุ %d คอมมิต) %s' % (rel, no, age, text))
        out('\nแก้: เติม sha จริงของคอมมิตที่บรรทัดนั้นอ้าง (`git log -S"<คำในบรรทัด>"`) ในคอมมิตนี้ — ห้าม amend')
    return 1


# ───────────────────────── self-test (negative test · F2 ข้อ 6) ─────────────────────────

def _sh(cwd, *args):
    r = subprocess.run(['git', '-C', cwd] + list(args), capture_output=True, text=True,
                       env=dict(os.environ, GIT_AUTHOR_NAME='t', GIT_AUTHOR_EMAIL='t@t', GIT_COMMITTER_NAME='t',
                                GIT_COMMITTER_EMAIL='t@t'))
    if r.returncode != 0:
        raise RuntimeError('git %s: %s' % (' '.join(args), r.stderr))
    return r.stdout.strip()


def _commit(repo, rel, text, msg):
    with open(os.path.join(repo, rel), 'a', encoding='utf-8') as fh:
        fh.write(text)
    _sh(repo, 'add', '-A')
    _sh(repo, 'commit', '-q', '-m', msg)
    return _sh(repo, 'rev-parse', 'HEAD')


def self_test():
    fails = []
    tmp = tempfile.mkdtemp(prefix='docsha-')
    try:
        repo = os.path.join(tmp, 'r')
        os.makedirs(repo)
        _sh(repo, 'init', '-q')
        first = _commit(repo, 'README.md', 'x\n', 'c1')
        # placeholder เก่า (อายุจะเป็น 3) — ต้องฟ้อง
        _commit(repo, 'DOCUMENT_FLOW.md', '_Last verified: 2026-11-06 — commit <เติมในคอมมิตตามหลัง>_\n', 'c2')
        _commit(repo, 'other.txt', 'a\n', 'c3')
        _commit(repo, 'other.txt', 'b\n', 'c4')
        # placeholder ใหม่ในคอมมิตล่าสุด (อายุ 0) + การพูดถึงใน backtick — ห้ามฟ้อง
        _commit(repo, 'CHANGELOG.md', '- รอบ X — sha เติมในคอมมิตตามหลัง\n- กติกา `commit <เติมในคอมมิตตามหลัง>`\n', 'c5')
        stale, _ = placeholder_problems(repo, True)
        names = [(r, a) for r, _, a, _ in stale]
        if names != [('DOCUMENT_FLOW.md', 3)]:
            fails.append('placeholder อายุ 3 ต้องถูกฟ้องตัวเดียว (CHANGELOG อายุ 0/backtick ไม่ฟ้อง) — ได้ %r' % names)
        # baseline ratchet: ใส่แถวเดิมใน baseline ⇒ ไม่ฟ้อง
        os.makedirs(os.path.join(repo, 'tools'), exist_ok=True)
        text = open(os.path.join(repo, 'DOCUMENT_FLOW.md'), encoding='utf-8').read().split('\n')[0]
        with open(os.path.join(repo, BASELINE_NAME), 'w', encoding='utf-8') as fh:
            fh.write(line_key('DOCUMENT_FLOW.md', text) + '\tตัวอย่าง\n')
        if placeholder_problems(repo, True)[0]:
            fails.append('แถวที่อยู่ใน baseline ต้องไม่ถูกฟ้อง')
        os.remove(os.path.join(repo, BASELINE_NAME))

        # sha: จริง ⇒ ผ่าน · ปลอม ⇒ ฟ้อง · เลขผู้เสียภาษี (checksum ถูก) หลัง ✅ ⇒ ไม่ใช่ sha · 13 หลัก checksum ผิด ⇒ ยังตรวจเป็น sha
        _commit(repo, 'NOTES.md', 'แก้แล้ว commit %s\n' % first[:8], 'c6')
        logs = []
        if check(repo, allow_fetch=False, out=logs.append) != 1:
            fails.append('ต้องล้มเพราะ placeholder เก่า (ยังไม่ใส่ baseline)')
        # ล้างตัวเก่าให้เหลือเฉพาะกติกา sha
        with open(os.path.join(repo, 'DOCUMENT_FLOW.md'), 'w', encoding='utf-8') as fh:
            fh.write('_Last verified: 2026-11-06 — commit %s_\n' % first[:8])
        with open(os.path.join(repo, 'CHANGELOG.md'), 'w', encoding='utf-8') as fh:
            fh.write('- รอบ X — commit %s\n' % first[:8])
        _sh(repo, 'add', '-A')
        _sh(repo, 'commit', '-q', '-m', 'c7')
        logs = []
        if check(repo, allow_fetch=False, out=logs.append) != 0:
            fails.append('sha จริงต้องผ่าน — %s' % ' | '.join(logs))
        _commit(repo, 'NOTES.md', '✅ 0105556000009 ผ่าน (TaxId)\n', 'c8')
        logs = []
        if check(repo, allow_fetch=False, out=logs.append) != 0:
            fails.append('เลขผู้เสียภาษี 13 หลักหลัง ✅ ต้องไม่ถูกตรวจเป็น sha — %s' % ' | '.join(logs))
        _commit(repo, 'NOTES.md', '✅ 0105556000000 checksum ผิด\n', 'c9')
        logs = []
        if check(repo, allow_fetch=False, out=logs.append) != 1:
            fails.append('13 หลักที่ checksum ผิดต้องยังถูกตรวจเป็น sha (ไม่หลวมกติกา)')
        _sh(repo, 'reset', '-q', '--hard', 'HEAD~1')
        _commit(repo, 'NOTES.md', 'commit deadbeef12\n', 'c10')
        logs = []
        if check(repo, allow_fetch=False, out=logs.append) != 1 or not any('deadbeef12' in l for l in logs):
            fails.append('sha ปลอมต้องถูกฟ้อง')
        _sh(repo, 'reset', '-q', '--hard', 'HEAD~1')

        # (ก) clone ตื้นที่ unshallow ไม่ได้ ⇒ sha ที่หาไม่เจอไม่ใช่รายการแดง + บอกว่า "clone ตื้น"
        shallow = os.path.join(tmp, 's')
        subprocess.run(['git', 'clone', '-q', '--depth', '1', 'file://' + repo, shallow], capture_output=True, check=True)
        logs = []
        if check(shallow, allow_fetch=False, out=logs.append) != 0 or not any('clone ตื้น' in l for l in logs):
            fails.append('clone ตื้นที่ fetch ไม่ได้ต้องไม่ล้ม และต้องบอก "clone ตื้น — เชื่อผล CI" — %s' % ' | '.join(logs))
        # ทิศตรงข้าม: unshallow ได้ ⇒ ตัดสินตามจริง (sha จริงผ่าน)
        logs = []
        if check(shallow, allow_fetch=True, out=logs.append) != 0 or any('clone ตื้น' in l for l in logs):
            fails.append('clone ตื้นที่ unshallow ได้ต้องตรวจเต็มและผ่าน — %s' % ' | '.join(logs))
    finally:
        shutil.rmtree(tmp, ignore_errors=True)

    if fails:
        print('doc_commit_sha_check --self-test: ล้ม')
        for f in fails:
            print('  - ' + f)
        return 1
    print('doc_commit_sha_check --self-test: ผ่าน')
    return 0


if __name__ == '__main__':
    if '--self-test' in sys.argv:
        sys.exit(self_test())
    sys.exit(check(ROOT))
