# -*- coding: utf-8 -*-
"""ตรวจวงกลม DI แบบเดียวกับที่ ASP.NET ทำตอน start (ValidateOnBuild)

รันจาก root ของ repo:  python3 tools/di_cycle_check.py

ทำไมต้องมี: วงกลมใน DI ไม่ใช่ compile error — โค้ดคอมไพล์ผ่านหมด แล้วไป
ระเบิดตอน start ด้วย stack trace ยาวเป็นหน้า ("A circular dependency was
detected") ซึ่งอ่านหาต้นตอยาก. สคริปต์นี้จำลองการตรวจแบบเดียวกัน: อ่าน
registration จาก Program.cs → หา ctor ของ implementation แต่ละตัว → สร้าง
กราฟ → หา cycle แล้วพิมพ์เส้นทางที่วนออกมาให้ตรง ๆ

วิธีแก้เมื่อเจอ: ตัวใดตัวหนึ่งในวงต้องเลิก inject อีกตัวผ่าน ctor แล้วไป
resolve ตอนเรียกใช้แทน (inject IServiceProvider) — ดูตัวอย่างที่
ApprovalService และ PlatformBillingDocumentIssuer
"""
import re, os, sys
from collections import defaultdict

prog = open('Accounting/Program.cs', encoding='utf-8').read()
# AddScoped<IFoo, Foo>() / AddSingleton<IFoo, Foo>() / AddTransient<...>
reg = {}
for m in re.finditer(r'Add(?:Scoped|Singleton|Transient)<\s*([\w\.]+)\s*,\s*([\w\.]+)\s*>', prog):
    reg[m.group(1).split('.')[-1]] = m.group(2).split('.')[-1]
# AddScoped<Foo>() — self-registered
for m in re.finditer(r'Add(?:Scoped|Singleton|Transient)<\s*([\w\.]+)\s*>\s*\(\s*\)', prog):
    t = m.group(1).split('.')[-1]
    reg.setdefault(t, t)

# หา ctor ของแต่ละ implementation
impl_files = {}
for root, _, files in os.walk('Accounting'):
    for fn in files:
        if fn.endswith('.cs'):
            p = os.path.join(root, fn)
            try: s = open(p, encoding='utf-8').read()
            except OSError: continue
            for m in re.finditer(r'public\s+(?:sealed\s+|partial\s+)?class\s+(\w+)', s):
                impl_files.setdefault(m.group(1), []).append((p, s))

def ctor_deps(cls):
    deps = []
    for p, s in impl_files.get(cls, []):
        for m in re.finditer(rf'public\s+{cls}\s*\(([^)]*)\)', s):
            body = re.sub(r'//[^\n]*', '', m.group(1))
            for part in body.split(','):
                part = part.strip()
                if not part: continue
                t = part.split()[0].split('<')[0].rstrip('?').split('.')[-1]
                deps.append(t)
    return deps

graph = defaultdict(set)
for svc, impl in reg.items():
    for d in ctor_deps(impl):
        if d in reg:            # นับเฉพาะ service ที่ register ไว้
            graph[svc].add(d)

# หา cycle (DFS)
cycles, state, stack = [], {}, []
def dfs(n):
    state[n] = 1; stack.append(n)
    for m in sorted(graph.get(n, ())):
        if state.get(m) == 1:
            cycles.append(stack[stack.index(m):] + [m])
        elif state.get(m, 0) == 0:
            dfs(m)
    stack.pop(); state[n] = 2

for n in sorted(reg): 
    if state.get(n, 0) == 0: dfs(n)

print(f'service ที่ register: {len(reg)} · เส้นพึ่งพา: {sum(len(v) for v in graph.values())}')
if cycles:
    seen = set()
    for c in cycles:
        key = tuple(sorted(set(c)))
        if key in seen: continue
        seen.add(key)
        print('❌ วงกลม: ' + ' → '.join(c))
    sys.exit(1)
print('✅ ไม่มีวงกลมใน DI graph')
