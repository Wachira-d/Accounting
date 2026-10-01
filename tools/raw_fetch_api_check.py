#!/usr/bin/env python3
# -*- coding: utf-8 -*-
"""raw_fetch_api_check.py — หน้าแอป (`wwwroot/pages/*.html`) ห้ามยิง `fetch('/api/…')` ดิบเพิ่ม (ratchet)

ที่มา (รอบ 201 ทีม OC · A-OC3 · team-K2 C-07 ส่วนที่เหลือ)
--------------------------------------------------------
`document-scan.html` ยิง `fetch('/api/…/ai-feedback/latest')` ดิบแล้ว `r.json()` ทันที ⇒ 401 (token หมดอายุ) ·
403 (ไม่มีสิทธิ์ — ข้อความของเซิร์ฟเวอร์บอกวิธีขอสิทธิ์) · หน้า HTML ของ proxy กลายเป็น "Unexpected token <" ที่ไม่บอกอะไร
และไม่มีตัวแสดง "กำลังทำงาน" กลาง · `API.request` (api.js) จัดการทั้งหมดนั้นอยู่แล้วที่เดียว — fetch ดิบคือสำเนาที่ไม่ครบ.
รอบ 200 ทีม K2 ย้ายสองปุ่มไป `API.post` แต่ไม่มี checker ⇒ fetch ดิบตัวใหม่เกิดได้เงียบ ๆ

กติกา: นับ `fetch(` ที่อาร์กิวเมนต์แรกเป็น literal ขึ้นต้น `/api/` (หรือ `${…}/api/`) ต่อไฟล์ใน `Accounting/wwwroot/pages/*.html`
เทียบ `tools/raw_fetch_api_baseline.txt` (ของเดิมก่อนกติกา — ดาวน์โหลดไฟล์/หน้าที่ต้องการ blob มีเหตุผลของมัน แต่ต้องตัดสินทีละจุด):
  • มากกว่า baseline = ฟ้อง (ใช้ `API.get/post/put/del` หรือ helper ใน api.js)
  • น้อยกว่า baseline = แจ้ง (ℹ️ ไม่ล้ม) ให้ลดตัวเลขใน baseline — ไม่ล้มเพราะหลายทีมแก้หน้าพร้อมกัน (ทีมที่ย้าย fetch ออกไม่ควรทำให้
    checker ของอีกทีมแดง) · ช่องโหว่ที่ยอมรับ: โควตาที่ค้างให้ fetch ดิบตัวใหม่ในไฟล์เดียวกันใช้ได้จนกว่าจะลดตัวเลข
ไม่ตรวจ `wwwroot/*.html` ระดับบน (หน้าเข้าสู่ระบบ/portal สาธารณะ — ไม่มี token ของ API.request) · ไม่ตรวจ fetch ที่ส่งตัวแปร (มองไม่เห็น URL)

negative test รันทุกครั้ง: ใส่ fetch ดิบเข้าข้อความหน้าแล้วต้องฟ้อง · fetch ไปไฟล์ static (`/js/…`) ต้องไม่ฟ้อง
"""
import os
import re
import sys

ROOT = os.path.dirname(os.path.dirname(os.path.abspath(__file__)))
PAGES = os.path.join(ROOT, "Accounting", "wwwroot", "pages")
BASELINE = os.path.join(ROOT, "tools", "raw_fetch_api_baseline.txt")
RAW_FETCH = re.compile(r"""\bfetch\(\s*[`'"](?:\$\{[^}]*\})?/api/""")


def count(text):
    return len(RAW_FETCH.findall(text))


def load_baseline(path=BASELINE):
    out = {}
    if not os.path.isfile(path):
        return out
    for line in open(path, encoding="utf-8"):
        line = line.strip()
        if not line or line.startswith("#"):
            continue
        name, _, n = line.rpartition(" ")
        out[name.strip()] = int(n)
    return out


def scan(pages_dir=PAGES):
    res = {}
    if not os.path.isdir(pages_dir):
        return res
    for f in sorted(os.listdir(pages_dir)):
        if f.endswith(".html"):
            n = count(open(os.path.join(pages_dir, f), encoding="utf-8").read())
            if n:
                res[f] = n
    return res


def judge(found, baseline, notes=None):
    errs = []
    for f in sorted(set(found) | set(baseline)):
        have, allowed = found.get(f, 0), baseline.get(f, 0)
        if have > allowed:
            errs.append(f"Accounting/wwwroot/pages/{f}: fetch('/api/…') ดิบ {have} จุด (baseline {allowed}) — ใช้ API.get/post/put/del ใน api.js "
                        "(จัดการ 401/403/ไม่ใช่ JSON/ตัวแสดงกำลังทำงานที่เดียว) · ห้ามเพิ่มตัวเลขใน baseline เพื่อให้เขียว")
        elif have < allowed and notes is not None:
            notes.append(f"tools/raw_fetch_api_baseline.txt: {f} เหลือ {have} จุด (baseline {allowed}) — ลดตัวเลขใน baseline ให้ตรง (ratchet ลดลงเท่านั้น)")
    return errs


def self_test():
    fails = []
    if count("const r = await fetch(`/api/companies/${cid}/x`);") != 1:
        fails.append("fetch(`/api/…`) ไม่ถูกนับ")
    if count("const r = await fetch(`${base}/api/companies/x`);") != 1:
        fails.append("fetch(`${base}/api/…`) ไม่ถูกนับ")
    if count("fetch('/js/translations.json'); API.get('/api/x');") != 0:
        fails.append("fetch ไปไฟล์ static หรือ API.get ถูกนับผิด")
    real = scan()
    base = load_baseline()
    victim = "document-scan.html"
    injected = dict(real)
    injected[victim] = real.get(victim, 0) + 1
    if not judge(injected, base):
        fails.append(f"ใส่ fetch ดิบเพิ่มใน {victim} แล้วไม่ฟ้อง")
    return fails


def main():
    st = self_test()
    notes = []
    errs = judge(scan(), load_baseline(), notes)
    for n in notes:
        print("ℹ️  " + n)
    for e in errs + [f"self-test: {x}" for x in st]:
        print("❌ " + e)
    if errs or st:
        return 1
    total = sum(scan().values())
    print(f"raw_fetch_api_check: ผ่าน (fetch ดิบคงค้างตาม baseline {total} จุด · negative test ผ่าน)")
    return 0


if __name__ == "__main__":
    sys.exit(main())
