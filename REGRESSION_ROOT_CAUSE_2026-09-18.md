# REGRESSION_ROOT_CAUSE_2026-09-18 — ทำไมของที่เคยดีกลับแย่ลง และจะทำให้ระบบ "ดีขึ้นเรื่อย ๆ" ได้อย่างไร

> โจทย์จากเจ้าของโปรเจกต์ (รอบ 169): *"ตั้งทีมไล่ตรวจสอบปัญหาที่แจ้งไปว่าเกิดจากอะไร และยิ่งส่วนที่เคยทำได้ดี
> ทำไมพัฒนาไปแล้วกลับทำให้แย่ลง … การดึง แสดง เชื่อมโยงข้อมูลกันผิด · การมี logic ทำงานสิ่งเดียวกันซ้อนกัน
> และจะป้องกันให้การพัฒนาในอนาคตไม่ผิดพลาด ระบบต้องพัฒนาดีขึ้นเรื่อยได้ยังไง"*

branch `claude/erp-system-review-team-660mev` · ตรวจที่ HEAD `7448ecb` (145 คอมมิต, 2026-09-03 → 09-16) ·
ทีม 4 ด้านอ่านอย่างเดียว แล้ว main agent **เปิดไฟล์ยืนยันทุกข้อที่นำมาใช้** (§6 คือรายการที่รายงานมาแล้ว
**ไม่จริง** — จดไว้ตามกติกา "ผลตรวจผิดได้ทั้งสองทาง")

| ทีม | โจทย์ | วิธี |
| --- | --- | --- |
| A โบราณคดีการถดถอย | ไล่ `git log -S`/`git show` ทั้ง 145 คอมมิต หา "คอมมิตที่ทำพัง → คอมมิตที่ซ่อม" | 33 กรณี · ระบุ sha คู่ · ระยะห่าง · ใครจับ |
| B ล่า logic ซ้อน | การตัดสินเรื่องเดียวกันถูกเขียนไว้กี่ที่ · helper กลางมีไหม · ใครยังไม่เรียก | grep/สคริปต์ 10 หมวด ระบุ file:line |
| C ไล่สายข้อมูล | ค่าเดียวกัน ดึงมาแสดงคนละหน้า ตรงกันไหม · ใครเป็น "ตัวตั้ง" | 8 ค่า ไล่จาก UI → endpoint → service → ตาราง |
| D สถาปนิกกระบวนการ | ทำไมกลไกกันถดถอยที่มีอยู่ไม่ทำงาน · ต้องเพิ่มชั้นไหน | วัดจริง: CI · SDK · checker · เทสต์ · doc · dead code |

---

## §1 คำตอบสั้นสำหรับ 5 คำถามของเจ้าของ

1. **"ปัญหาที่แจ้งไปเกิดจากอะไร"** — ใน 33 กรณีถดถอยที่พิสูจน์ได้ด้วย sha คู่ ต้นเหตุ 3 อันดับแรกครอบ 30/33:
   (ก) **คอมไพล์ไม่ได้ในสภาพแวดล้อมที่เขียนโค้ด** → อ้างสมาชิก/ชนิดโดยไม่เปิดคำประกาศ (13 กรณี — ผู้ใช้เป็น compiler
   ของโปรเจกต์: build error ถึงเครื่องผู้ใช้ 8 ครั้ง / 14 คอมมิต "แก้ CSxxxx" ใน 15 วัน)
   (ข) **แก้เส้นเดียว/ฝั่งเดียว/renderer เดียว/ทางเข้าเดียวจาก N** โดยไม่ grep call site (11 กรณี)
   (ค) **ไม่มี baseline จริงก่อนแก้ · ไม่มีเทสต์ล็อกทิศตรงข้าม** (6 กรณี)
2. **"ทำไมส่วนที่เคยดีกลับแย่ลง"** — เพราะทุกการแก้ใน 15 วันถูกส่งออก **โดยไม่มีตัวใดพิสูจน์ว่า "ของเดิมยังถูก"**:
   เทสต์ 1,670 ตัวจับได้ **0 กรณี** (ไม่เคยรันในเซสชันพัฒนา — ไม่มี SDK · CI ปิดบน `claude/**`) · checker 37 ตัวจับได้
   1 กรณี (3%) · **ผู้ใช้จับ 39% · ทีมตรวจรอบถัดไป 30% · ตรวจย้อนเอง 24%** ⇒ การตรวจทั้งหมดเป็น post-hoc
   และ **เกิดหลัง push แล้วเสมอ**. ที่หนักกว่า: **20/33 กรณี defect class ถูกจดใน CLAUDE.md ไว้แล้วก่อนเกิด** —
   การจดไม่ลดการเกิดซ้ำ เพราะสิ่งที่ต้องกันคือ "ชนิดจริงของตัวแปร" (compiler) และ "grep ให้ครบ" (วินัย) ซึ่ง
   ทั้งคู่ไม่ใช่ด่านอัตโนมัติ
3. **"ดึง/แสดง/เชื่อมโยงข้อมูลผิด"** — ไล่ 8 ค่า: ตรง 4 · ไม่ตรง 3 · ตรงแต่เปราะ 1 (§4) ตัวหนักสุดคือ **ยอด WHT
   ค้างนำส่ง ภ.ง.ด.3/53** ที่หน้านำส่ง/ปฏิทินนับจาก `Documents` แต่รายงาน/ไฟล์ยื่น/แดชบอร์ดนับจาก `WithholdingTaxCerts`
   ⇒ JE นำส่ง ≠ ไฟล์ที่ยื่น · และ **สถานะ "ยื่นแล้ว" เก็บ 4 ที่ไม่ sync** (ผู้ใช้ต้องกด 3 ที่ให้งวดหนึ่ง "จบ")
4. **"logic ทำงานสิ่งเดียวกันซ้อนกัน"** — 10 หมวด (§3) helper กลาง**มีอยู่แล้วเกือบทุกหมวด** แต่ถูกเรียกน้อยกว่า
   สำเนามือทุกหมวด (`DocumentStatusRules` 21 จุด vs สำเนา 172 · `ArApScope` 5 ไฟล์ vs ~45 ชุด · ตัดสินนิติบุคคล
   6 กติกาที่**ต่างกันจริง** · `Math.Round` เงิน 237 จุดไม่มี `MidpointRounding`) — และ 2 helper ยัง "ไม่ครบพอให้เรียก
   ตรง ๆ" จึงบังคับให้คนเขียนต่อท้ายเอง
5. **"จะป้องกันและทำให้ดีขึ้นเรื่อย ๆ ได้อย่างไร"** — root cause ระดับกระบวนการ 5 ข้อ (§7): CI ที่มีอยู่ถูกปิดตรงจุดเดียว
   ที่มันจะทำงาน · "ไม่มี SDK" เป็น proxy policy ไม่ใช่กฎธรรมชาติ · ความรู้ถูกจดเป็น log ไม่ใช่กฎที่บังคับได้ · เทสต์ล็อกแต่
   helper และไม่เคยรัน · "มี ≠ ถูกเรียก" ไม่มีเครื่องวัด. **รอบนี้ทำ 4 อย่างที่ทำได้ทันทีโดยไม่ต้องรอเจ้าของ** (§5)
   และ **2 อย่างที่ต้องให้เจ้าของตัดสิน** (§7.3) ซึ่งเป็นตัวที่ให้ผลมากสุด

---

## §2 ทีม A — โบราณคดีการถดถอย (33 กรณี)

### 2.1 ตัวเลขที่พิสูจน์ได้

| หัวข้อ | ค่า |
| --- | --- |
| คอมมิตบน branch | 145 (root `0750e6a` squash 1,152 ไฟล์ ⇒ ทุกอย่างเกิดใน 15 วัน ~10 คอมมิต/วัน) |
| คอมมิตที่หัวเรื่องหลักคือ **ซ่อมของที่ตัวเองทำพัง** | **16/145 (11%)** — 8 ตัวเป็น build-break ล้วน |
| กรณีถดถอยที่ยืนยัน sha คู่ได้ | **33** = build ล้มทั้ง solution 13 + พฤติกรรมถดถอย/แก้ครึ่งเดียว 20 |
| ระยะที่บั๊กอยู่ในระบบ | มัธยฐาน ~8 คอมมิต (วันเดียว–3 วัน) · นานสุด 54 คอมมิต (RG-01 AmountTriple, 4 วัน) และ 50 (`docType.Value`) — ทั้งคู่ผู้ใช้จับ |
| วันที่พังใส่ตัวเองมากสุด | **09-16: 9 กรณีในวันเดียว** จากคอมมิตต่อเนื่อง 12 ตัว (รอบ "ตรวจแล้วแก้ แก้แล้วตรวจ" ที่หมุนเร็วสุด) |

### 2.2 ใครจับ

| ผู้จับ | กรณี | สัดส่วน |
| --- | --- | --- |
| ผู้ใช้/เจ้าของ (ส่ง build error 8 · รายงานอาการ 5) | 13 | **39%** |
| ทีมตรวจรอบถัดไป (subagent ที่ตั้งเอง) | 10 | 30% |
| ตรวจย้อนเอง | 8 | 24% |
| checker | 1 (`doc_commit_sha_check` จับ `7448ecb`) | 3% |
| **เทสต์ xUnit** | **0** | **0%** |

### 2.3 ความถี่ต้นเหตุเชิงกระบวนการ (กรณีเดียวนับได้หลายเหตุ)

| # | ต้นเหตุ | จำนวน | ตัวอย่าง |
| --- | --- | --- | --- |
| 1 | คอมไพล์ไม่ได้ + อ้างสมาชิก/ชนิดโดยไม่เปิดคำประกาศ ("สมาชิกบนชนิดที่คิดว่าถืออยู่" 7 ครั้ง — ครั้งที่ 4 ของคลาสเดียวกันคือ `d.IsSubjectToSocialSecurity` บน `PayrollDetail`) | **13** | `802f149`→`99f7cdf`/`53ae749` · `4cbb52a`→`d242368` · `d4a7c77`→`3470198` |
| 2 | แก้เส้นเดียว/ฝั่งเดียว/renderer เดียว/ทางเข้าเดียวจาก N โดยไม่ grep sibling | **11** | 50 ทวิ 3 renderer แก้ใน 3 คอมมิต (`b710167`→`85cab37`→`d242368`) · ด่าน approve อ้อมได้ 3 ทาง · `settledByPv` ยุบไม่ครบ |
| 3 | ไม่มี baseline จริง / ไม่มีเทสต์ทิศตรงข้าม / simulation ไม่ใช่โค้ดเดิม | **6** | RG-01 ถอด heuristic ที่บังเอิญซ่อมป้ายสลับ · "นายสมชาย" (baseline sim ไม่ใช่ `git show <sha>:<file>`) · ไฟล์ยื่นว่าง HTTP 200 |
| 4 | checker ให้ "เขียวเทียม" (allow-list ไม่ครอบไฟล์ / ครอบแค่รูปย่อย) | 4 | `verbatim_string_check` ข้ามสตริงธรรมดา · `write_permission_gate` ไม่มี Metering · `namespace_shadow` ดูแค่สาย 2 ตอน |
| 5 | สำเนามือของกติกา server ใน JS / ตัดสินจาก field เดียว | 2 | `ReceiptIssuePolicy` ฝั่ง JS · การ์ดสแกนฟันธงเหตุผลรุ่นเก่า |
| 6 | เชื่อ doc/กฎ/คอมเมนต์ที่ตัวเองเขียนผิด | 2 | CLAUDE.md บอก "ใช้ชื่อเต็ม `Accounting.Helpers.X` ได้" → CS0234 · fallback VAT header-only เชื่อคอมเมนต์รอบก่อน |
| 7–8 | self-review ครึ่งเดียว · ลำดับ doc/amend | 2 | เห็น 1 ใน 2 error ในคอมมิตเดียว · sha dangling หลัง amend (ทำซ้ำในคอมมิตถัดไปทันที) |

### 2.4 บทเรียนที่จดใน CLAUDE.md แล้ว **ยังเกิดซ้ำ** (≥ 20/33)

| บทเรียน | จดเมื่อ | เกิดซ้ำ |
| --- | --- | --- |
| "`.HasValue`/`.Value` — เปิดคำประกาศชนิดที่ถืออยู่จริง" | ≤ 09-04 | **3 ครั้ง** (09-08 · 09-08 · 09-16) — ไฟล์เองยอมรับว่า checker เขียนไม่ได้ |
| "แก้ตัวเดียว เหลือที่เหลือ" | root | **8 กรณี** — คลาสที่ซ้ำมากที่สุด |
| "สอง renderer ห้าม drift" (กฎข้อแรกของ #4 A) | root | 3 renderer · 3 คอมมิต |
| "ด่านที่ครอบแค่ทางเดียว คือด่านที่ไม่มี" | ≤ 09-04 | 3 กรณี (ใส่ด่านทางละคอมมิต) |
| "ตัวคั่นสตริงห้ามโผล่ในสตริงชนิดเดียวกัน" | root | 2 (สตริงธรรมดา · `$$"""`) — checker ครอบแค่รูปที่เคยพัง |
| "sha ที่จดก่อน commit ตายเมื่อ amend" | `9e4d7ed` | **คอมมิตถัดไปทันที** (`7448ecb`) |

⇒ กฎที่ "ไม่กันอะไร" ชัดที่สุดคือกลุ่มที่ต้อง (ก) รู้ชนิดจริง = compiler (ข) grep ให้ครบ = วินัย — CLAUDE.md #4 มี
142 bullet (F อย่างเดียว 130) ซึ่งส่วนใหญ่เป็น**คำอธิบายบั๊ก** ไม่ใช่ด่าน

### 2.5 ขอบของ static checker (37 ตัว)

| ประเภท | กรณี |
| --- | --- |
| checker ควรจับได้แต่ครอบไม่ถึงรูป/ไฟล์ | 5 |
| จับได้ในหลักการแต่ต้อง checker ใหม่ (syntax ล้วน) | 2 (tuple ternary — เขียนแล้ว · CS0411 — ต้อง resolve overload ไม่เขียน) |
| **จับไม่ได้โดยธรรมชาติ — ต้อง compile/รู้ type** | **8** (ทิ้ง `nullable_unwrap`/`.HasValue` checker ไปแล้วเพราะฟ้องผิด) |
| **จับไม่ได้ — พฤติกรรม/ตัวเลข (ต้อง test/simulation/baseline)** | **12** |
| จับไม่ได้ — taint / call-graph ข้ามชั้น | 6 |

**26/33 (79%) อยู่นอกขอบเขต static checker** — เพิ่ม checker อีกกี่ตัวก็ครอบได้แค่ 5–7 กรณีที่เป็น "รูปทรงโค้ด";
กลุ่มใหญ่ต้องการ **compiler** (8) และ **regression suite ที่รันจริง** (12)

---

## §3 ทีม B — logic เรื่องเดียวกันเขียนซ้อนกัน (10 หมวด)

| # | หมวด | สำเนามือ | helper กลาง (ผู้เรียก) | ความเสี่ยงถ้า drift | checker ได้? |
| --- | --- | --- | --- | --- | --- |
| 1 | **ตัวกรอง tenant** | 63 filtered/aggregate + 108 Id-only lookup บน 32 ตารางหลักที่ไม่มี `CompanyId` ในคำสั่ง | **ไม่มี** — `HasQueryFilter` 203 ตัวใน `AccountingDbContext` **กรองแค่ `IsDeleted` ทั้งหมด** (CLAUDE.md ข้อ M เคยเขียนว่า "บังคับด้วย global query filter" — **ไม่จริง** แก้ doc แล้วรอบนี้) | ข้ามบริษัทได้ทันทีที่ id หลุดจาก request — ไม่มีตาข่ายชั้น DbContext · จุดที่ต้องดู: `ProjectAccountingService.cs:261` · `AccountingService.cs:2453,2467` | ❌ taint — ทางที่กันได้จริงคือ global filter ผูก `ICurrentTenant` (โครงสร้าง) |
| 2 | **ชุดสถานะเอกสาร** | **172 บรรทัด** นิยาม "ชุด" เอง — 7 ตระกูลที่**ต่างกันจริง**: `!= Voided && != Draft` ~45 จุด (ยังนับ `WaitingApproval`/`Rejected` ที่ถือ `DRAFT-{guid}`) · `!= Voided && != Rejected` 14 (นับ Draft) · AR เปิด 16 · posted 4 · `Approved‖Paid` เท่านั้น (ตัวเรียนรู้ AI มองไม่เห็น Sent/PartiallyPaid) 7 · แก้ได้ 7 | `DocumentStatusRules` (21 จุด/7 ไฟล์) — **ไม่มี `Effective[]`/`OpenReceivable[]` array** ⇒ แม้ผู้ใช้ helper ก็ต้องเขียน `!NotIssued.Contains(s) && s != Voided` ซ้ำ 11 ครั้งใน `TaxService.cs` | รายงาน/แดชบอร์ด/ตัวเรียนรู้นับใบที่ยังไม่ออกเลข | ✅ regex "คำสั่งเดียวมี `DocumentStatus.X` ≥2 เชื่อม `&&/‖/or/,`" นอก Helpers |
| 3 | **ชนิดเอกสารฝั่งซื้อ/ขาย** | `PurchaseInvoice+Expense` 45 จุด · `Invoice+TaxInvoice` ~70 จุด · ชุด AP ต่างกันเอง 3 แบบ | `ArApScope` (5 ไฟล์) · `WhtRemitScope` (2) · `DocumentSide` (7) | **ขัด ArApScope โดยตรง** (นับ BillingNote เป็นลูกหนี้ = นับซ้ำ): `CashForecastController:77` · `ArApAnalysisService:18` · `AdvancedArApService:86,130` (ตัดสิน**วงเงินเครดิต**) · `FxRevaluationService:126` (revalue BillingNote ที่ไม่มี JE) ⇒ AR บนแดชบอร์ด ≠ aging ≠ GL 11310 ≠ วงเงิน | ✅ |
| 4 | **ตัดสินบุคคล/นิติบุคคล** | **6 ชุดกติกาต่างกัน** + inline 6: `ThaiTaxId:69` = `'0'` · `WhtPayeeKind:46` = `0`/1-8/9 · **`SmartFieldExtractor:733,756` = `'0'‖'8'‖'9'`** (เลือกผัง 21916/21917) · `ContactsV1Controller:128` และ `IntegrationService:1488` = **แค่ `Length==13`** (บัตรประชาชน = นิติบุคคล) · JS `contacts.html:725` เทียบเลข `ct === 2` | `WhtPayeeKind.Detect/IsJuristic` (5) · `ThaiTaxId.IsJuristic` | ผังหัก 21916↔21917 สลับ ⇒ ภ.ง.ด.3/53 ยื่นผิดแบบ · ContactType ผิดตั้งแต่ทางเข้า API | ⚠️ จับได้เฉพาะรูป `Length == 13 && StartsWith('0')` — ความต่างเชิงกติกาต้องยุบเป็น `ThaiTaxId.IsJuristic` + เทสต์ล็อกหลักแรก |
| 5 | **กำหนดยื่นภาษี** | สำเนาตารางที่ 4 `ComplianceService.InitializeFilingCalendarAsync` (ไม่เลื่อนวันหยุด ไม่มี e-Filing) · `PayrollService:880` สปส.6-09 · JS `payroll.html:864,1007` วันที่ 15 | `TaxFilingDeadline` (3 ไฟล์) — **checker เดิมรายงาน 0** เพราะจับแค่ชื่อเมธอด `*Due*/*Deadline*` | ปฏิทิน compliance บอกกำหนดคนละวันกับหน้านำส่ง (ซ้ำรอย ภ.พ.36 23→15) | ✅ **แก้แล้วรอบนี้** — ยุบ ComplianceService/สปส.6-09 + checker จับที่ body shape (§5) |
| 6 | **ผังบัญชี hardcode** | `"21911"` 33 · `"21913"` 33 · `"11610"` 17 · `"21917"` 16 · `"21916"` 15 · `"11640"` 13 — `DocumentService.cs` คนเดียว 63 · **ลำดับ fallback 21916↔21917 เขียน 5 ที่ด้วยเงื่อนไขคนละตัว** | **ไม่มี** — resolver เป็น `private` ใน `DocumentService` และ `IntegrationService.ResolveVatAccountAsync` เป็นสำเนาที่สอง | JE จริง ≠ พรีวิว ≠ integration/OCR/gateway เลือกผังคนละใบ (ซ้ำรอย ภ.พ.36 พรีวิว) | ✅ literal 5 หลักนอกไฟล์ resolver (ขยาย `gl_code_check` ให้มีโหมด "อยู่นอก OWNER") |
| 7 | **สูตร VAT** | inclusive เขียนเอง 8 จุด C# (`*7m/107m` · `*100m/107m` · `/1.07m` · `/(1+rate)` ไม่มี AwayFromZero) · บวกเพิ่ม 5 จุด (`*0.07m` = สูตรที่พิสูจน์แล้วว่าตกกึ่งกลาง) · JS `quick-sale.html:253,375` `gross/1.07` | `OutputVatRate.VatOn` (ทางบวกเพิ่ม) · **ไม่มี helper 7/107 ใน C#** · JS `Layout.vatFromInclusive` (3) | ยอด VAT ใบเสร็จ SaaS/quick-sale ต่างระบบหลัก ฿0.01 ในเคส `×0.07` | ✅ literal `1.07`/`0.07`/`7m / 107m` นอก Helpers |
| 8 | **พ.ศ./ค.ศ.** | ทิศ input 3 เกณฑ์ (`> 2400` 16 จุด · `> 2500` 2 · `>= 2400 && <= 2700` 1 · JS 3) · ทิศ display `+543` + format เอง ~50 จุด (`TaxFilingExportService` 17 · `PdfGenerationService*` 8) | `ThaiDate.NormalizeYear` · JS `Layout.normalizeThaiYear` (**เรียกแค่ 2 จุด** จาก 45) | display 50 จุดเสี่ยง th-TH double-convert (บทเรียนที่มีแล้ว) | ✅ `[<>]=? *2[45]00` และ `[+-] 543` นอก `ThaiDate`/`layout.js` |
| 9 | **ป้าย enum ฝั่ง JS** | สำเนามือ 17 ชุด/15 ไฟล์ — `DocumentStatus` **ไม่มีใน ENUM_LABELS** และป้ายไม่ตรงกัน (`translations.js` `Paid:'ชำระแล้ว'` vs `documents.html` `Paid:'จ่ายครบ'`) | `Layout.ENUM_LABELS` + `enumLabel()` (6 ไฟล์) | ป้ายผิด/ไม่ตรงหน้าต่อหน้า (ไม่กระทบเงิน) | ✅ object literal คีย์ PascalCase ตรงชื่อ enum ใน `AllEnums.cs` นอก `layout.js`/`translations.js` |
| 10 | **`Math.Round` เงิน** | 465 จุด · **237 ไม่มี `MidpointRounding` และไม่ใช้ alias `R`** · ที่เป็นเงินจริง: `OcrService` UnitPrice/Amount 6 จุด · `PdfGenerationService:2006,2013` (ยอดที่พิมพ์) · `TaxFilingExportService:580` (คู่ค่าจ้าง/สมทบ) · `PosService.Orders:1748` (ปัดบาทถ้วน banker's) | ไม่มี `MoneyRound` — ทุกจุดพิมพ์เอง | ยอดพิมพ์ ≠ GL ฿0.01 · ไฟล์ สปส. คู่ตีกลับ | ⚠️ จับได้แน่ แต่ ~60% เป็น %/confidence ⇒ ต้องจำกัดที่ argument มีคำ amount/vat/total/price/wht/salary/sso |

**ข้อสังเกตข้ามหมวด**: helper ที่มีอยู่ถูกเรียก**น้อยกว่าสำเนามือทุกหมวด** · helper 2 ตัวไม่ครบพอให้เรียกตรง ๆ
(`DocumentStatusRules` ไม่มี array ที่ EF แปลได้ · ไม่มี C# helper 7/107) ⇒ "มี helper" ยังไม่พอ ต้อง "helper
ที่เรียกได้ในประโยคเดียว" + checker ที่ฟ้องเมื่อไม่เรียก (สูตรที่พิสูจน์แล้ว 3 ตัว: `filing_deadline_single_source` ·
`sequence_lock` · `line_vat_source` = **ลายเซ็นของการคำนวณซ้ำ + ไฟล์ OWNER ตัวเดียว ไม่ใช่ allow-list**)

---

## §4 ทีม C — ค่าเดียวกัน ดึงมาแสดงคนละที่ (8 ค่า)

| # | ค่า | สถานะ | รายละเอียดที่ยืนยันแล้ว | ตัวตั้งที่ควรเป็น |
| --- | --- | --- | --- | --- |
| 1 | VAT สุทธิของงวด | ✅ ตรง | ทุกหน้าอ่าน `TaxReport.NetVat` · จุดเปราะ: 3 ผู้อ่านมีกติกา dedup ต่างกันเมื่อมีหลายแถว/เดือน (`OrderByDescending(UpdatedAt)` · `grp.First()` ไม่เรียง · `OrderByDescending(Filed)`) — วันนี้ dup-guard กันไว้ | `TaxReport.NetVat` (+ helper `TaxReportPicker.Latest` เป็นการป้องกัน) |
| 2 | **WHT ค้างนำส่ง ภ.ง.ด.3/53** | ❌ **ไม่ตรง** | หน้านำส่ง/ปฏิทิน/JE นำส่ง: `Documents.WithholdingTaxAmount` คีย์ `PaymentDate ?? DocumentDate` · รายงาน/ไฟล์ยื่น/e-Filing/แดชบอร์ด: `WithholdingTaxCerts` Issued/Printed คีย์ `TaxYear/TaxMonth` · **สูตรที่ 3** `PayrollService.GeneratePnd3Async` (ไม่กรอง payer-side · ไม่แยก 3/53 · ไม่ตัด PV) เปิดเป็น endpoint แต่ **ไม่มี UI เรียก** · เคสจริง 3 ทรง: เอกสารมี WHT แต่ยังไม่ออก 50 ทวิ → นำส่งมียอด ไฟล์ = 0 · cert เลือก TaxMonth ≠ เดือนจ่าย · PaymentDate คนละเดือนกับ DocumentDate | `WithholdingTaxCerts` Issued/Printed (สิ่งที่ยื่นจริงตาม ท.ป.4/2528) — `StatutoryRemittanceService` ต้องย้ายไปอ่าน certs และแสดงเอกสารที่ยังไม่ออก 50 ทวิ เป็น **แถวเตือน** · **ขึ้นกับการตัดสินใจ "50 ทวิ auto-issue" ที่ค้างอยู่** |
| 3 | ประกันสังคมของงวด | ⚠️ ตัวเลขตรง ขอบเขตต่าง | จอ: ทุกรอบ ≠ Voided (มีป้าย `InFilingFile`/`ExcludedNote` แล้ว) · ไฟล์: Approved+Paid (+`EnsureFilableRunsAsync`) · นำส่ง: Paid เท่านั้น (+`PendingReason`) — เหลือ `PreCloseChecklistService` นับ `WithholdingTax1` ใน `TaxReports` ซึ่งไม่มีวันมี และลิงก์ผิดหน้า | `PayrollRun.TotalSocialSecurity*` ใน `PayrollRunFilingScope.FilingStatuses` · **PreClose แก้แล้วรอบนี้** |
| 4 | ยอดค้างชำระเอกสาร | ✅ ผู้อ่าน / ❌ ผู้เขียน 1 จุด | ผู้อ่านทุกตัวใช้ `BalanceDue` · `ArApAnalysisService:92,105` อ่าน `PaidAmount` ปน · **`IntegrationService:1205` CN ผ่าน API ลด `BalanceDue` โดยไม่แตะ `PaidAmount`** ⇒ รับชำระบางส่วนครั้งถัดไปคำนวณ `Balance = Total − Paid` ทับ → **ยอด CN ที่หักแล้วเด้งกลับเป็นยอดค้าง** | `BalanceDue = TotalAmount − PaidAmount` ผู้เขียนขยับคู่ · **แก้แล้วรอบนี้** · ระยะยาว helper `DocumentBalance.Apply` + checker ฟ้อง `.BalanceDue =` นอก helper (31 จุด) |
| 5 | สต็อกคงเหลือ | ✅ ตรงแล้ว | `CurrentStock = Σ WarehouseStock` ผ่าน `IStockLedger` ตัวเดียว (`stock_writer_check` 0 จุด) · **CLAUDE.md "สองความจริง" ล้าสมัย — แก้ doc แล้ว** · `ReconcileProductTotalsAsync` มีแต่ interface + comment **ไม่มีใครเรียก** | `WarehouseStock` (ledger) — ต่อสาย reconcile เข้า job |
| 6 | **สถานะ "ยื่นแล้ว/นำส่งแล้ว"** | ❌ **4 ที่ไม่ sync** | `TaxReport.Filed` (tax.html) · `StatutoryRemittance` (นำส่ง) · `PayrollRun.SsoSettledAt` (payroll — **ไม่สร้างแถว remittance**) · `TaxCalendarEvent.Status` (**ผู้ใช้กรอกมือเท่านั้น** ไม่อ่าน 3 ตัวแรก) ⇒ ผู้ใช้ต้องกด 3 ที่ และ 2 ปฏิทินบอกคนละสถานะเสมอ | เหตุการณ์จริง 2 อย่าง: "ยื่นแบบ" = `TaxReport.FiledDate` · "จ่ายเงิน" = `StatutoryRemittance.PayDate` — `TaxCalendarEvent` ควร **derived/read-only** |
| 7 | คำนำหน้าผู้ติดต่อ | ⚠️ | ไฟล์ ภ.ง.ด.3 ทั้งสองปุ่มใช้ `Contact.TitleTh` แต่ e-Filing อ่าน **snapshot** `TaxReportLine.TaxPayerTitle` · **50 ทวิ PDF ไม่พิมพ์คำนำหน้าเลย** | `Contact.TitleTh` + `Name` · **50 ทวิ แก้แล้วรอบนี้** (`ThaiTitleHelper.WithTitle`) · snapshot staleness = backlog |
| 8 | หัวเอกสาร | ✅ ตัวเดียว | list/detail/PDF/e-Tax ใช้ `ResolveDocumentTitle` · JS `Layout.docHeaderLabel` fallback 3 ธงยังอยู่ (ใช้จริงจุดเดียว: พรีวิว "ถ้าจ่ายครบหัวจะเป็น…") | `DocumentResponse.DocumentTitle` · พรีวิวควรขอเซิร์ฟเวอร์แล้วลบ fallback |

---

## §5 สิ่งที่แก้ในรอบ 169 (ทำได้ทันทีโดยไม่ต้องรอเจ้าของตัดสิน)

### 5.1 โค้ด

| # | เรื่อง | ไฟล์ | ทำไม |
| --- | --- | --- | --- |
| 1 | **ยุบตารางกำหนดยื่นชุดที่ 4** — `InitializeFilingCalendarAsync` เลิกพิมพ์ ภ.พ.30/ภ.ง.ด.1/3/53/สปส.1-10 เอง 4 ลูป → ตารางเดียว `monthlyForms` วนเรียก `TaxFilingDeadline.For` | `Services/Implementations/ComplianceService.cs` | ปฏิทิน compliance เคยบอกกำหนดคนละวันกับหน้านำส่ง (ไม่เลื่อนวันหยุด · ภ.พ.36 23) |
| 2 | สปส.6-09 ผ่าน `TaxFilingDeadline` (`"SsoSps609" => 15` · `HasEFilingExtension` = false สำหรับ `Sso*`) | `Helpers/TaxFilingDeadline.cs` · `Services/Implementations/PayrollService.cs` | `new DateTime(…,15).AddMonths(1)` ไม่เลื่อนวันหยุด |
| 3 | **CN ผ่าน API ขยับคู่ `PaidAmount`/`BalanceDue`** (`apply = min(CN, max(0, BalanceDue))`) แล้วเดินสูตรเดียวกับ `DocumentService:8719` | `Services/Implementations/IntegrationService.cs` | ทีม C #4 — ยอด CN เด้งกลับหลังรับชำระบางส่วน |
| 4 | 50 ทวิ PDF พิมพ์คำนำหน้าบุคคลธรรมดาจาก `Contact.TitleTh` — `ThaiTitleHelper.WithTitle` (ไม่ต่อซ้ำถ้าชื่อมีอยู่แล้ว · ไม่ต่อให้นิติบุคคล) | `Helpers/ThaiTitleHelper.cs` · `PdfGenerationService.WhtCert.cs` · `Accounting.Tests/ThaiTitleHelperTests.cs` (+7 เคส) | ทีม C #7 — ใบรับรองพิมพ์ "สมชาย ใจดี" ขณะไฟล์ ภ.ง.ด.3 ประกาศ "นาย" |
| 5 | PreClose ข้อ WHT นับเฉพาะ `WithholdingTax3/53` + ลิงก์ `/pages/tax.html` | `Services/Implementations/PreCloseChecklistService.cs` | เดิมนับ `WithholdingTax1` ที่ `TaxService` throw ไม่ให้สร้าง และลิงก์ไปทะเบียน 50 ทวิ — เตือนผิดที่ตลอด |

### 5.2 เครื่องมือ (ชั้นที่ทีม D ระบุว่า "ไม่มีสักตัว")

| เครื่องมือ | ทำอะไร | negative test |
| --- | --- | --- |
| `tools/check_all.sh` | รัน checker ทุกตัว + `node --check` ทุก `<script>` ใน .html ที่แก้ (แตกเป็นไฟล์แล้วใช้ parser จริง) + brace balance .cs ที่แก้ + U+FFFD + `test_inventory --check` + `dotnet build/test` ถ้ามี SDK — exit code เดียว | รันบน working tree รอบนี้ผ่าน; ล้มตัวไหนชื่อไฟล์บอก |
| `tools/callers.py <Symbol>` | นิยาม · ผู้เรียกในโค้ดจริง · เทสต์ · คอมเมนต์ — แยกกัน · exit 1 เมื่อ "มีแต่นิยาม/เทสต์/คอมเมนต์" · รองรับ `Class.Member` และชื่อเต็ม `Accounting.Helpers.X.Y` | `SsoFilingScope` (7 ผู้เรียก) vs `PayrollRunFilingScope.CanRemit` (0) vs `SsoRateSchedule.RangesOverlap` (เทสต์ 2 · โค้ด 0 — และ doc-comment ใน `Payroll.cs:493` บอกว่า "เป็นตัวตรวจ") |
| `tools/dead_helper_check.py` | public static method ใน `Helpers/` ที่ไม่มีผู้เรียกนอกไฟล์ (นอกคอมเมนต์ · เทสต์ไม่นับ) — **ratchet กับ `tools/dead_helper_baseline.txt` (58 แถว)**: ล้มเฉพาะตัวใหม่ · baseline ที่กลับมามีชีวิตรายงานให้ตัดออก · `--all` ไล่ตัดสิน "ต่อสาย/ลบ" | `--self-test` 7 ข้อ: orphan ฟ้อง · มีผู้เรียกไม่ฟ้อง · **เทสต์อย่างเดียวฟ้อง** · **คอมเมนต์/URL ไม่นับเป็นผู้เรียก** · generic/async จับได้ · operator/property ไม่นับ · baseline ratchet ทั้งสองทิศ — จับ 3 ตัวที่สคริปต์รอบก่อนพลาดเพราะนับ doc-comment (`SanitizeHtmlBlock` · `ResolvePrefixAsync` · `RangesOverlap`) |
| `tools/test_inventory.py` | นับไฟล์/`[Fact]`/`[Theory]`/`InlineData`/แตะ DbContext จริง (นอกคอมเมนต์) · `--row` สร้างข้อความสำหรับ TEST_PLAN §0 · `--check` ล้มเมื่อ doc ไม่ตรง | TEST_PLAN §0 เคยผิด 10 เท่า (~150/19 vs 1,670/187) — ตอนนี้สร้างจาก source |
| `tools/filing_deadline_single_source_check.py` (ขยาย) | จับที่ **body shape** (`AddMonths(1)` + วันคงที่ + marker ของแบบยื่น) นอกไฟล์ OWNER แทนการพึ่งชื่อเมธอด · ตัดคอมเมนต์ก่อนสแกน · ไม่ฟ้องวันที่ 1 / `AddDays(1)` (ขอบงวด) | 4 ทิศ: เรพ 0 · synthetic 1 · `ComplianceService` ก่อนแก้ 1 · `PayrollService` ก่อนแก้ 1 (รุ่นเดิมรายงาน 0 ทั้งที่มีสำเนา = **เขียวเทียม**) |

### 5.3 เอกสารที่ผิดจากโค้ด (แก้ doc)

- CLAUDE.md ข้อ M "tenant isolation บังคับด้วย global query filter" → **ไม่มี filter ตัวไหนกรอง `CompanyId`** (203 ตัวกรอง `IsDeleted`)
- CLAUDE.md §POS "`CurrentStock` กับ `WarehouseStock` เป็นสองความจริง" → ยุบผ่าน `IStockLedger` แล้ว; `ReconcileProductTotalsAsync` ไม่มีผู้เรียก
- TEST_PLAN §0 ตัวเลขเทสต์ → สร้างจาก `test_inventory.py`

---

## §6 สิ่งที่รายงานมาแล้ว **ไม่จริง / เกินจริง** (บันทึกตามกติกา — ห้ามข้ามเงียบ)

| ทีม | คำอ้าง | ความจริงเมื่อเปิดไฟล์ |
| --- | --- | --- |
| D | "CLAUDE.md F-list ยังมี `nullable_unwrap_check.py` ทั้งที่ลบไปแล้ว" | **ไม่จริง** — ปรากฏเฉพาะในบทเรียนที่จดว่า "เขียนแล้วต้องทิ้ง" (บรรทัด ~1630) ไม่ได้อยู่ในลิสต์คำสั่ง F |
| D | "0 ไฟล์เทสต์แตะ `DbContext`" | จริงเชิงการใช้ — grep ดิบได้ 4 ไฟล์ แต่ทั้งหมดเป็นคอมเมนต์ (`test_inventory.py` จึงตัดคอมเมนต์ก่อนนับ) |
| D | "55 dead method" | เมื่อนับ**นอกคอมเมนต์**ได้ **58** — สคริปต์เดิมนับ doc-comment ที่เอ่ยชื่อเป็นผู้เรียก (พลาด `SanitizeHtmlBlock` · `ResolvePrefixAsync` · `RangesOverlap`) |
| B | "`quick-sale.html` มีสูตร 7/107 ที่ CLAUDE.md บอกว่าไม่มี" | literal `7/107` ไม่มีจริง (ตามที่ CLAUDE.md จด) แต่ `/1.07` คือสูตรเดียวกัน — และรอบก่อนพิสูจน์ด้วย sweep 2 ล้านค่าแล้วว่า `x/1.07` **ไม่ตกจุดกึ่งกลาง** ⇒ ไม่ใช่บั๊กปัดเศษ; ที่เป็นบั๊กจริงคือ `*0.07` และ unitPrice ไม่ปัดก่อนลง numeric(18,2) |
| A | "RG-05 ผ่อนด่านเมื่อรูปเป็น canonical" (รอบ 168) | ทางแก้ที่เสนอจะพา "นายช่างการไฟฟ้า" พังกลับ — ใช้ `Contact.TitleTh` ที่ยืนยันแล้วแทน (บันทึกไว้ใน CLAUDE.md แล้ว) |

---

## §7 ทีม D — ทำไมกลไกที่มีอยู่ไม่ทำให้ระบบดีขึ้นเอง

### 7.1 ข้อเท็จจริงที่วัดได้

| หัวข้อ | ค่า |
| --- | --- |
| CI | `.github/workflows/ci.yml` ครบ (DI-cycle → nullable → restore → build → test) แต่ `on.push.branches: [main, master]` — **ปิดบน `claude/**` โดยเจ้าของสั่ง** (คอมเมนต์ในไฟล์: กันอีเมลแจ้ง fail ทุกคอมมิต) ⇒ **ไม่เคยรันกับคอมมิตไหนใน 145 ตัว** |
| .NET SDK ในเซสชัน | ไม่มี · installer ถูก **proxy policy ปฏิเสธ** (`builds.dotnet.microsoft.com` / `download.visualstudio.microsoft.com` → CONNECT 403) · `api.nuget.org` ผ่าน ⇒ เป็นการตั้งค่า environment ไม่ใช่ข้อจำกัดทางเทคนิค |
| checker | 37 → **38** ตัว รวม ~34 วินาที · `--self-test` 9 → 10 ตัว · ไม่มี git hook · ไม่มี runner (→ `check_all.sh`) |
| เทสต์ | 187 ไฟล์ · 1,435 Fact + 235 Theory · **0 แตะ DbContext** (pure-function ทั้งหมด) · 14 ไฟล์ range-sweep · code-commit ที่แถมเทสต์ 83/123 (67%) — **แต่ไม่เคยรันในเซสชันพัฒนา** |
| Helpers | 135 ไฟล์ · 405 public static · **58 ไม่มีผู้เรียกนอกไฟล์** · 2 ไฟล์ตายทั้งไฟล์ (`PdpaSignupConsent.cs` — doc-comment เรียกตัวเองว่า "แหล่งความจริงเดียว" · `ContactHydration.cs`) |
| เอกสาร root | 3.5 MB — TEST_PLAN 1.03 MB · DOCUMENT_FLOW 0.80 MB ("รอบ 1xx" 51 บล็อก = changelog) · CLAUDE.md 448 KB/2,866 บรรทัด (#4 = 68% · โต 1,511→2,866 ใน 13 วัน · "บทเรียนซ้อน" ×54) |
| ไฟล์ยักษ์ | `DocumentService.cs` 16,566 บรรทัด (เมธอดยาวสุด ≈ 1,829) · `OcrService.cs` 8,829 · `documents.html` 12,061 |
| self-check runtime | `PreCloseChecklist` · `TaxGlReconciliation` · `SubLedgerReconciliation` · `JournalAnomaly` — **pull-only ทั้งหมด** · scheduled มีแค่ `AuditChainVerifyJob` (7 วัน) |

### 7.2 Root cause 5 ข้อ

| RC | สาระ | หลักฐาน |
| --- | --- | --- |
| **RC-1** | ด่านที่มีอยู่ถูกปิดตรงจุดเดียวที่มันจะทำงาน — ปัญหาคือ**การแจ้งเตือน** แต่แก้ที่**การตรวจ** (ปิดด่านแทนปิดเสียง) | ci.yml ครบแต่ trigger ไม่ครอบ branch งาน ⇒ compiler ตัวแรกที่โค้ดเจอ = เครื่องผู้ใช้ |
| **RC-2** | "ไม่มี SDK" ถูกยอมรับเป็นกฎธรรมชาติ แล้วทั้งกระบวนการงอตัวรอบมัน | checker ~15 ตัวเป็น "compiler เลียนแบบ" · 2 ตัวถูกทิ้งเพราะชนกำแพง type resolution · 21% ของคอมมิตแตะ `tools/` · แต่ข้อจำกัดจริงคือ proxy policy |
| **RC-3** | ความรู้ถูกจดเป็น log เหตุการณ์ ไม่ใช่กฎที่บังคับได้ | defect class เดียวกันจด 9 ครั้งยังเกิด · 20/33 จดก่อนเกิด · TEST_PLAN §0 ผิด 10× สอนคนอ่านให้ไม่เชื่อ doc ทั้งชุด |
| **RC-4** | เทสต์ล็อกแต่ helper — ชั้น service ที่บั๊กเกิดจริงทดสอบไม่ได้โดยโครงสร้าง และเทสต์ที่มีไม่เคยรัน | 0 DbContext · เมธอด 1,829 บรรทัด · "negative test" 23 ครั้งใน CLAUDE.md = simulation python เฉพาะกิจ ไม่ใช่ suite ที่สะสม |
| **RC-5** | "มี ≠ ถูกเรียก" เป็น defect class ใหญ่สุด แต่ไม่มีเครื่องวัด | 58 dead method / 2 dead file · self-check เป็น pull-only · ทุกครั้งที่จับได้คือตา + grep มือ → **แก้แล้วรอบนี้** (`dead_helper_check` · `callers.py`) |

### 7.3 สิ่งที่ต้องให้เจ้าของตัดสิน (impact สูงสุด · effort ต่ำสุด — agent ทำเองไม่ได้)

| # | ข้อเสนอ | ผล | สิ่งที่ต้องทำ |
| --- | --- | --- | --- |
| **O-1** | เปิด host ของ .NET ใน proxy policy ของ environment (`builds.dotnet.microsoft.com` · `download.visualstudio.microsoft.com` · `dot.net`) **หรือ** ใช้ image ที่มี SDK 8 | agent คอมไพล์เอง ~1 นาที · `dotnet test` 1,670 เคสรันได้ทุกคอมมิต · build break หยุดถึงเครื่องผู้ใช้ · checker "compiler เลียนแบบ" 15 ตัวกลายเป็นส่วนเกิน (ไม่ต้องลบ) | ตั้งค่า environment ฝั่งเจ้าของ |
| **O-2** | เปิด `claude/**` กลับใน workflow **แต่แก้ที่ "เสียง" ไม่ใช่ปิด "ด่าน"**: `paths-ignore: ['**.md', 'erp-review/**']` (ตัดคอมมิต doc ล้วน ~39%) + `concurrency: cancel-in-progress` (คอมมิตถี่ไม่กองกัน) + แยก job `build` (ทุก push) / `test` (PR/main/dispatch) + เจ้าของปิด GitHub → Settings → Notifications → Actions failure email · **agent อ่านผลเองผ่าน MCP `actions_list`/`get_job_logs` หลัง push** | error ไม่ต้องเดินทางถึงเครื่องผู้ใช้ · ทำได้ทันทีโดยไม่ต้องรอ O-1 | อนุมัติ → agent เขียน `.github/workflows/build.yml` ตามร่าง §9 |

### 7.4 ชั้นที่เหลือ (agent ทำได้ — เรียงตามคุ้มค่า)

| ชั้น | ข้อเสนอ | สถานะ |
| --- | --- | --- |
| Test gate | `check_all.sh` + `test_inventory.py` | ✅ รอบนี้ |
| Test gate | **Golden set กระดาษจริง** — รวมเลขจากใบจริงที่กระจายใน 10 ไฟล์เทสต์เป็น `Accounting.Tests/Golden/*.json` + เทสต์ 1 ตัว loop ทุกไฟล์ ⇒ กติกา H "รันชุดกระดาษจริงก่อน/หลัง" ทำได้จริง | backlog (กลาง) |
| Test gate | Testcontainers PostgreSQL สำหรับ 3 เส้นเงินหลัก (`ApproveDocumentAsync` JE Dr=Cr + เลข gap-free · `AutoPostToJournalAsync` ต่อชนิด · `CreateDocumentFromScanAsync` Σ บรรทัด = หัว) — รันเฉพาะ CI job `test` | backlog (สูง — ทำหลัง O-1/O-2) |
| Single-source | `dead_helper_check` ratchet · `callers.py` | ✅ รอบนี้ |
| Single-source | ตัดสิน 58 dead method / 2 dead file ทีละตัว "ต่อสาย หรือ ลบ" — ตัวที่อันตรายทันที: `PayrollRunFilingScope.CanRemit/CountsTowardFiling` · `ArApScope.IsPayable` · `SsoIdentityPolicy.ProvidesEmailVerification` · `DocumentQuotaPolicy.MustAlwaysIssue` · `SsoRateSchedule.RangesOverlap` (doc-comment บอกว่าเป็นตัวตรวจ) | backlog — **ห้ามเดาแทนเจ้าของ** ว่าฟีเจอร์ไหนตั้งใจทิ้ง |
| Single-source | checker ตามสูตร "ลายเซ็นการคำนวณซ้ำ + OWNER file": (1) VAT literal `1.07`/`0.07`/`7m/107m` นอก Helpers (2) `± 543`/`> 2[45]00` นอก `ThaiDate`/`layout.js` (3) ชุด `DocumentStatus.X` ≥2 นอก `DocumentStatusRules` (4) ผัง 5 หลักนอก resolver — **ทุกตัวต้องมี negative test และ 0 FP ก่อน ship** | backlog (ต่ำ/ตัว) |
| Single-source | เติม helper ให้ "เรียกได้ในประโยคเดียว": `DocumentStatusRules.Effective[]`/`OpenReceivable[]` (array ให้ EF แปล) · `Vat.FromInclusive(x, rate)` C# พร้อม AwayFromZero · `MoneyRound(x)` | backlog (ต่ำ) — ต้องทำ**ก่อน**เขียน checker ข้อบน ไม่งั้นบังคับให้ใช้ของที่ยังไม่มี |
| Review | ทำ "ฝ่ายค้าน" เป็นขั้นตอนถาวรสำหรับทุกคอมมิตที่แตะเงิน/ภาษี/สิทธิ์ — subagent รับ diff + 3 คำถาม (ทางเข้าอื่นที่แตะข้อมูลชุดเดียวกัน? ทิศตรงข้ามยังทำงาน? สถานะปลายทางถูกประทับโดยไม่มีของจริง?) | นโยบาย — ใช้ตั้งแต่รอบหน้า |
| Review | ยุบ CLAUDE.md #4 จาก 142 bullet เป็น **หลักการ 10 ข้อ** (§8) + ย้ายบทเรียนดิบไป `docs/lessons/` · แยก "สถานะปัจจุบัน" ออกจาก "ประวัติรอบ" ใน DOCUMENT_FLOW/TEST_PLAN (→ `CHANGELOG.md`) | **ต้องให้เจ้าของเห็นด้วยก่อน** — เป็นการเปลี่ยนวิธีทำงานที่เจ้าของกำหนดไว้ (กฎ "ห้ามลบแถว/append ในคอมมิตเดียว") |
| Change-impact | `tools/signature_change_check.py` — จาก `git diff` หา `public … Name(` ใน Helpers ที่จำนวน/ชนิดพารามิเตอร์เปลี่ยน แล้วนับว่า call site ทุกไฟล์ถูกแตะในคอมมิตเดียวกันไหม (ต้องการแค่ชื่อเมธอด ไม่ชนกำแพง type) | backlog (ต่ำ) |
| Change-impact | แตก `DocumentService.cs` ตาม ERP_REVIEW §6 "ชั้น posting เดียว" — เมธอด 1,829 บรรทัดทำให้ "อ่านทั้งเมธอดก่อนแก้" เป็นไปไม่ได้ทางกายภาพ | backlog (สูง — หลังมี compile gate) |
| Observability | `DailySelfAuditJob` (pattern `AuditChainVerifyJob` + `JobLock`) เรียก 4 service ตรวจสอบ**ที่มีอยู่แล้ว** ต่อทุกบริษัท → `SelfAuditFinding` + แบนเนอร์แดชบอร์ด — pull → push โดยไม่ต้องเขียน invariant ใหม่ | backlog (ต่ำ — 1 job + 1 ตาราง + 1 แบนเนอร์) |
| Observability | invariant 10 ข้อสำหรับ self-audit (เรียงตามความเสียหายที่เคยเกิดจริง): ① Approved doc ใน `DocumentJournalExpectation` ไม่มี JE Posted ② JE `TotalDebit≠TotalCredit` หรือ Σlines≠Total ③ เลขซ้ำ/ช่องว่างใน series ต่อ `Company+Branch+Year` ④ `TaxReport.NetVat` งวดยื่นแล้ว ≠ GL 21911/11610 ⑤ Σ ไฟล์ยื่น ≠ Σ Paid runs (ขยาย `EnsureFilableRunsAsync` ให้เทียบ**ตัวเลข**) ⑥ สินทรัพย์ `NBV<Salvage`/ครบอายุแต่ยังโพสต์ ⑦ `CurrentStock` ≠ Σ movement (ต่อสาย `ReconcileProductTotalsAsync`) ⑧ Voided แต่ JE ไม่ Reversed ⑨ e-Tax `Submitted` ที่ `SubmissionId LIKE 'OFFLINE-%'` ⑩ สแกนค้าง `Processing` > 1 ชม. | backlog (กลาง — ①②④⑩ มีโค้ดอยู่แล้ว) |
| Observability | ตัวเลขที่จะส่งออก/นำส่งต้องมี "คู่ตรวจ" ก่อนปุ่มกด — ขยาย pattern `InFilingFile`+`ExcludedNote` ไปทุกไฟล์ยื่น: preview ส่ง `{fileTotal, screenTotal, delta, excluded[]}` แล้ว UI ปิดปุ่มเมื่อ `delta≠0` · HTTP 200 กับ payload ว่างต้องเป็น 409/422 ที่มี `nextAction` | backlog (ต่ำ/ไฟล์) |

---

## §8 หลักการ 10 ข้อ (กลุ่มจาก 142 bullet — ใช้แทนการอ่านทั้งหมดก่อนเริ่มงาน)

1. **แก้ที่หนึ่ง grep ทั้งเรพ** — `python3 tools/callers.py <Symbol>` ก่อนแตะ · ตอบเป็นตัวเลขในคอมมิต ("รูปแบบเดิมเหลือ 0 จุด")
2. **มี ≠ ถูกเรียก** — helper/ด่าน/doc-comment ที่ไม่มี call site = ไม่มี (`dead_helper_check` ratchet)
3. **ค่าที่แต่งขึ้น / สถานะปลายทางที่ระบบประทับเอง อันตรายกว่าการไม่ตอบ** — ไม่รู้ = บอกว่าไม่รู้ แล้วให้ชั้นถัดไปตัดสิน
4. **ตัวตั้งตัวเดียว** — กติกา/ตาราง/สูตรอยู่ใน `Helpers/` OWNER file เดียว · helper ต้อง "เรียกได้ในประโยคเดียว"
5. **Server computes · page displays** — JS ห้ามมีสำเนากติกา/ป้าย/ตาราง
6. **ด่านต้องมี negative test มิฉะนั้นไม่มีด่าน** — checker/เทสต์/ด่าน runtime ทุกตัว ใส่บั๊กกลับแล้วต้องจับได้ · **checker ที่ฟ้องผิด = checker ที่พัง**
7. **ล้มดัง 3 ที่** (ตัวข้อมูล · สถานะงาน · คำตอบผู้เรียก) — `LogWarning`/`Debug.WriteLine` ไม่ใช่การดัง · HTTP 200 กับของว่างคือการโกหก
8. **เข้มขึ้นต้องมีเทสต์ทิศตรงข้าม + ทางไปต่อของผู้ใช้** — บีบตัวกรอง/เพิ่ม throw แล้วต้องถามว่า "เคสที่ถูกตัดออกเห็นอะไร"
9. **Persist แล้วต้องมี migration** — แก้โค้ดอย่างเดียวไม่พอเมื่อของเสียถูกเก็บไว้แล้ว
10. **Doc ตามโค้ดในคอมมิตเดียว · sha เติมในคอมมิตตามหลัง ห้าม amend** — โค้ดเป็น ground truth; doc ผิด = แก้ doc ทันที

### Checklist ก่อน push (12 ข้อ — ครึ่งแรกเครื่องทำ)

```
ก. เครื่องทำ — bash tools/check_all.sh (< 1 นาที)
 1. checker ทุกตัวผ่าน (ล้มตัวไหน = แก้ ไม่ใช่เลี่ยง)
 2. node --check ทุก <script> ใน .html ที่แก้ · brace balance ทุก .cs ที่แก้ · U+FFFD
 3. ไม่มี dead helper ใหม่ (dead_helper_check) · TEST_PLAN §0 ตรงเทสต์จริง (test_inventory --check)
 4. doc sha อยู่บน branch (doc_commit_sha_check) — เติม sha ในคอมมิตตามหลัง ห้าม amend
 5. ถ้ามี dotnet: build + test ผ่าน
 6. หลัง push: อ่านผล Actions ผ่าน MCP actions_list → แดง = แก้ก่อนรายงานผู้ใช้ (เมื่อ O-2 เปิด)

ข. คนทำ — ตอบเป็นข้อความในคำอธิบายคอมมิต (ตอบไม่ได้ = ยังไม่ push)
 7. "แก้ที่นี่ แล้ว callers.py/grep รูปแบบเดิมทั้งเรพได้กี่จุด" → ตัวเลข (0 ก็เขียนว่า 0)
 8. "ทางเข้าอื่นที่แตะข้อมูลชุดเดียวกัน (มือ/CSV/API/LINE/job/มือถือ) เดินด่านเดียวกันไหม"
 9. "ถ้าเพิ่ม throw/เข้มขึ้น — ผู้ใช้ที่ถูกกันทำอะไรได้แทน + เทสต์ทิศตรงข้ามชื่ออะไร"
10. "ค่าที่ persist ไว้ก่อนแก้ ยังผิดอยู่ไหม → มี migration ไหม"
11. "แตะเงิน/ภาษี/สิทธิ์ → ส่ง diff ให้ subagent ฝ่ายค้าน 1 รอบแล้ว" (3 คำถามใน §7.4)
12. "DOCUMENT_FLOW/ACCOUNT_STRUCTURE/TEST_PLAN ต้องขยับไหม" — เพิ่ม/ลด throw หรือเปลี่ยนสูตรค่าที่ persist = ใช่
```

### สิ่งที่**ไม่ควรทำ** (มีหลักฐานว่าเสียเวลาแล้ว)

1. เขียน checker ที่ต้อง type resolution อีก (`.HasValue` · `nullable_unwrap` ถูกทิ้งแล้ว · `d.IsSubjectToSocialSecurity` คลาสเดียวกัน) — คำตอบคือ compiler (O-1/O-2)
2. จดบทเรียน CSxxxx ลง CLAUDE.md เพิ่ม — 9 ครั้งไม่ลดการเกิด
3. checker ที่กวาดทั้งเรพด้วยกติกาที่ต้องรู้ taint (`_db.Users` 85 จุด) — ใช้ scope แคบ/OWNER file เท่านั้น
4. แก้ "เสียงรบกวน" ด้วยการปิด trigger — นั่นคือสิ่งที่ทำให้ CI กลายเป็นไฟล์ตกแต่ง
5. ปล่อย doc เป็น append-only log โดยไม่มี "สถานะปัจจุบัน" ที่สั้นพอจะผิดแล้วเห็น
6. `BackgroundService` ใหม่โดยไม่ผ่าน `JobLock` (23 ตัว 18 ใช้ล็อก — ค่าเสื่อม ×2 เคยเกิด)
7. เพิ่มฟีเจอร์ทับ `DocumentService.cs` ต่อจนกว่าจะแตก posting layer

---

## §9 ร่าง `.github/workflows/build.yml` (รอเจ้าของอนุมัติ O-2 — **ยังไม่ได้สร้างไฟล์** เพราะเป็นการกลับคำสั่งเดิมของเจ้าของ)

```yaml
name: Build
# เปิดบน branch งานอีกครั้ง — แก้ที่ "เสียง" ไม่ใช่ปิด "ด่าน":
#   paths-ignore **.md → คอมมิตเอกสารล้วนไม่รัน · concurrency → คอมมิตถี่ยกเลิกรอบเก่า
#   job build (ทุก push) แยกจาก test (PR/main/dispatch) · agent อ่านผลผ่าน MCP หลัง push
#   เจ้าของปิดอีเมล: GitHub → Settings → Notifications → Actions → uncheck failed workflows
on:
  push:
    branches: [ main, master, 'claude/**' ]
    paths-ignore: [ '**.md', 'erp-review/**', 'scripts/**' ]
  pull_request:
    branches: [ main, master ]
  workflow_dispatch:
concurrency:
  group: build-${{ github.ref }}
  cancel-in-progress: true
permissions: { contents: read }
env: { DOTNET_NOLOGO: true, DOTNET_CLI_TELEMETRY_OPTOUT: true }
jobs:
  static-checks:
    runs-on: ubuntu-latest
    steps:
      - uses: actions/checkout@v4
      - uses: actions/setup-python@v5
        with: { python-version: '3.12' }
      - uses: actions/setup-node@v4
        with: { node-version: '20' }
      - run: bash tools/check_all.sh --all
  build:
    runs-on: ubuntu-latest
    steps:
      - uses: actions/checkout@v4
      - uses: actions/setup-dotnet@v4
        with: { dotnet-version: '8.0.x' }
      - uses: actions/cache@v4
        with: { path: ~/.nuget/packages, key: "nuget-${{ runner.os }}-${{ hashFiles('**/*.csproj') }}" }
      - run: dotnet restore Accounting.sln
      - run: dotnet build Accounting.sln -c Release --no-restore 2>&1 | tee build.log
      - if: failure()
        run: grep -E 'error CS[0-9]{4}' build.log | sort -u | head -40
  test:
    needs: build
    if: github.event_name != 'push' || startsWith(github.ref, 'refs/heads/main') || startsWith(github.ref, 'refs/heads/master')
    runs-on: ubuntu-latest
    steps:
      - uses: actions/checkout@v4
      - uses: actions/setup-dotnet@v4
        with: { dotnet-version: '8.0.x' }
      - run: dotnet restore Accounting.sln
      - run: dotnet build Accounting.sln -c Release --no-restore
      - run: dotnet test Accounting.sln -c Release --no-build
```
(เทสต์เป็น pure-function ทั้งหมด — ถ้าอยากให้ `test` รันทุก push บน `claude/**` ด้วย ลบ `if:` ได้ ไม่ต้องมี PostgreSQL)

---

## §10 Backlog ที่เหลือจากรอบนี้ (เรียงตามความเสียหาย · ระบุว่าใครต้องตัดสิน)

| # | เรื่อง | ขนาด | ต้องตัดสินก่อน? |
| --- | --- | --- | --- |
| 1 | `StatutoryRemittanceService` (pending + calendar) ย้ายไปอ่าน `WithholdingTaxCerts` ให้ตรงไฟล์ยื่น + แถวเตือน "ยังไม่ออก 50 ทวิ" · ลบ/redirect `PayrollService.GeneratePnd3Async` (สูตรที่ 3 ไม่มี UI) | M | **ใช่ — นโยบาย 50 ทวิ auto-issue** (ถ้า auto-issue ทุกใบ สองแหล่งจะเท่ากันเอง) |
| 2 | สถานะ "ยื่นแล้ว" 4 ที่ → `TaxCalendarEvent` derived จาก `TaxReport.FiledDate` + `StatutoryRemittance` · `SettleSocialSecurityAsync` สร้างแถว remittance | M | ไม่ |
| 3 | ตัดสิน 58 dead helper / 2 dead file (§7.4) | S/ตัว | **ใช่** — ฟีเจอร์ไหนตั้งใจทิ้ง |
| 4 | ชุดสถานะ/ชนิดเอกสาร 172+45 จุด → helper (เติม `Effective[]` ก่อน) + checker | L | ไม่ |
| 5 | ตัดสินนิติบุคคล 6 กติกา → `ThaiTaxId.IsJuristic` ตัวเดียว + เทสต์ล็อกหลักแรก (SmartFieldExtractor `'8'‖'9'` เปลี่ยนผัง 21916/21917) | M | ไม่ (แต่กระทบผังจริง — ต้องมี simulation ใบจริงก่อน/หลัง) |
| 6 | ผัง hardcode 63 จุดใน `DocumentService` → resolver public + `IntegrationService.ResolveVatAccountAsync` ยุบ | L | ไม่ |
| 7 | `Math.Round` เงินจริง ~30 จุด (OCR UnitPrice · PDF ยอดพิมพ์ · สปส. คู่ค่าจ้าง · POS ปัดบาทถ้วน) | S | ไม่ — แต่ต้องพิสูจน์ก่อนว่าสูตรตกกึ่งกลางได้จริง (บทเรียน `VatRoundingMode`) |
| 8 | พ.ศ. display 50 จุด → `ThaiDate.ToThaiDisplayString` · input 3 เกณฑ์ → `NormalizeYear` | M | ไม่ |
| 9 | `DocumentStatus` เข้า `ENUM_LABELS` + ยุบสำเนา 17 ชุด | S | ไม่ |
| 10 | e-Filing `TaxPayerTitle` snapshot → hydrate สด หรือเตือนเมื่อ ≠ ปัจจุบัน · JS `docHeaderLabel` fallback → endpoint title-preview | S | ไม่ |
| 11 | `ReconcileProductTotalsAsync` ต่อสายเข้า job · `PreCloseChecklist` เพิ่มข้อ ⑤⑦ | S | ไม่ |
| 12 | ภ.พ.36 pending filter/แกนวันที่ (§83/6 = วันจ่าย) · quick-sale unitPrice ปัด + approve error ที่ถูกกลืน · §65 ตรี(6) surcharge add-back จาก GL · `payroll.html` วันที่ 15 hardcode | S–M | ไม่ |
| — | **การตัดสินใจที่ค้างจากรอบก่อน**: เพดาน ปกส. 17,500 (2569) · 50 ทวิ auto-issue · หน่วยราชการ/มูลนิธิ หัก ณ ที่จ่ายอย่างไร | — | **ใช่ทั้งสาม** |

---

_ยังไม่ได้คอมไพล์ในสภาพแวดล้อมนี้ (ไม่มี .NET SDK — ดู RC-2) — รบกวน rebuild ฝั่งผู้ใช้ · เอกสารนี้อ้างโค้ด ณ
`7448ecb` + การแก้ของรอบ 169 (sha ในคอมมิตตามหลัง)_
