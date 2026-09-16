#!/usr/bin/env python3
# -*- coding: utf-8 -*-
"""ตรวจว่า sha ที่เอกสารอ้างถึง อยู่บน branch จริงหรือไม่

ที่มา (รอบ 167): `DOCUMENT_FLOW.md` รอบ 163/164 จด `commit dbaa778` / `commit 0c80a7b`
ซึ่งเป็น sha **ก่อน amend** — commit ถูกแก้ทีหลัง (ซ่อม build ที่พัง / เติมไฟล์ที่ลืม)
sha เดิมจึงกลายเป็น dangling object: `git cat-file -t` ยังตอบว่า "commit" (จึงดูเหมือนถูก)
แต่ `git merge-base --is-ancestor` ตอบว่าไม่อยู่ในประวัติ และของจริงจะหายหลัง `git gc`
⇒ คนที่ตามรอยว่า "พฤติกรรมนี้เปลี่ยนที่คอมมิตไหน" หาไม่เจอ

กติกา: รูปแบบที่ตรวจคือ `commit <sha>` และ `✅ <sha>` (สองรูปที่เรพนี้ใช้จริง) —
sha ที่เอ่ยลอย ๆ ในเนื้อความ (เช่นตัวอย่างในบทเรียนที่จงใจอ้างถึง sha ที่ตายแล้ว)
ไม่ถูกตรวจ เพราะไม่ได้ทำหน้าที่ "ชี้ไปยังคอมมิตที่แก้เรื่องนี้"

placeholder ที่ยังไม่ใส่ sha (`<pending>` / `<new-sha>` / `<sha>`) ข้ามให้ —
มันบอกตัวเองอยู่แล้วว่ายังไม่เสร็จ
"""
import os
import re
import subprocess
import sys

ROOT = os.path.dirname(os.path.dirname(os.path.abspath(__file__)))
PATTERN = re.compile(r'(?:commit|✅)\s+`?([0-9a-f]{7,40})`?')
SKIP_DIRS = {'.git', 'node_modules', 'bin', 'obj'}


def git(*args):
    return subprocess.run(['git', '-C', ROOT] + list(args),
                          capture_output=True, text=True)


def main():
    if git('rev-parse', '--git-dir').returncode != 0:
        print('ไม่ใช่ git repo — ข้าม')
        return 0

    md_files = []
    for base, dirs, files in os.walk(ROOT):
        dirs[:] = [d for d in dirs if d not in SKIP_DIRS]
        for f in files:
            if f.endswith('.md'):
                md_files.append(os.path.join(base, f))

    problems = []
    cache = {}
    for path in sorted(md_files):
        try:
            with open(path, encoding='utf-8') as fh:
                lines = fh.readlines()
        except (OSError, UnicodeDecodeError):
            continue
        for no, line in enumerate(lines, 1):
            for sha in PATTERN.findall(line):
                if sha not in cache:
                    exists = git('cat-file', '-t', sha).stdout.strip() == 'commit'
                    on_branch = exists and git(
                        'merge-base', '--is-ancestor', sha, 'HEAD').returncode == 0
                    cache[sha] = (exists, on_branch)
                exists, on_branch = cache[sha]
                if on_branch:
                    continue
                why = ('object ไม่มีในเรพนี้เลย' if not exists
                       else 'มี object แต่ **ไม่อยู่ในประวัติของ HEAD** (น่าจะถูก amend/rebase)')
                rel = os.path.relpath(path, ROOT)
                problems.append((rel, no, sha, why))

    if not problems:
        print('doc_commit_sha_check: ผ่าน — sha ที่เอกสารอ้างถึงอยู่บน branch ครบทุกตัว')
        return 0

    print('doc_commit_sha_check: พบ sha ที่เอกสารอ้างแต่ตามรอยไม่ได้\n')
    for rel, no, sha, why in problems:
        print('  %s:%d  %s — %s' % (rel, no, sha, why))
    print('\nแก้: หา sha จริงด้วย `git log --oneline` แล้วแทนที่ '
          '(sha ที่จดไว้ก่อน commit จะตายทันทีที่ amend)')
    return 1


if __name__ == '__main__':
    sys.exit(main())
