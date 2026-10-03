# ดัชนีเอกสารตรวจ/คำตัดสิน (ย้ายจาก CLAUDE.md 2026-10-03 — เนื้อหาคงเดิมทุกตัวอักษร)

> CLAUDE.md อ้างไฟล์นี้ · กติกาการดูแล DOCUMENT_FLOW/ACCOUNT_STRUCTURE ยังเป็น hard requirement เท่าเดิม

## 📘 DOCUMENT_FLOW.md — เอกสารอ้างอิง flow ล่าสุด

`DOCUMENT_FLOW.md` (root ของ repo) คือ **single source of truth** ของ flow
เอกสารทุกประเภทในระบบ — ตั้งแต่ทางเข้า (สร้าง/OCR/integration/convert/recurring)
→ lifecycle (Draft → Approved → Sent → Paid/Voided) → ทางออก (PDF, e-Tax XML,
รายงานภาษี). ใช้เป็น reference เวลาแก้/เพิ่ม feature ที่เกี่ยวกับเอกสาร

### กฎการดูแล (hard requirement)

> **ทุก PR/commit ที่เปลี่ยน flow ต้องอัปเดต `DOCUMENT_FLOW.md` ในคอมมิตเดียวกัน**
> — ห้ามแยก commit, ห้ามขึ้น TODO ไว้ทำทีหลัง. ถ้าไฟล์นี้ drift จากโค้ดจริง
> = ทุกคน (รวม AI agent) จะตัดสินใจผิดจาก doc ที่ไม่ตรงความจริง

**ต้องอัปเดตเมื่อแก้สิ่งต่อไปนี้** (ไม่ครบก็ใส่เพิ่มได้):
1. เพิ่ม/ลด `DocumentType` enum value
2. เปลี่ยน `DocumentStatus` หรือ transition (เพิ่ม state ใหม่, เปลี่ยน guard)
3. แก้ `ApproveDocumentAsync` (ลำดับขั้น, เพิ่ม/ลด validation, JE/stock/asset)
4. แก้ JE posting per type (`AutoPostToJournalAsync`)
5. แก้ `ApplyStockMovementsAsync` (เปลี่ยน DocumentType ที่กระทบ stock)
6. แก้ tax point logic (`TaxPointResolver`)
7. แก้ §86/4 / §82/3 / §82/5 / §65 ตรี gate
8. แก้ undue VAT reclassification (11640 ↔ 11610)
9. แก้ deposit lifecycle (Realize/Refund/Apply)
10. เพิ่ม/แก้ entry point ใหม่ (OCR, integration, convert pair, recurring)
11. เพิ่ม/แก้ออก channel (PDF template, e-Tax type, รายงานภาษีใหม่)
12. เพิ่ม/แก้ AI feature (`AiFeatureKey`) — ต้องเพิ่มในตาราง distillation
13. แก้ retention period / PDPA gate

### Workflow ที่ AI agent ต้องทำ

ก่อน commit ที่กระทบ flow:
- [ ] อ่าน `DOCUMENT_FLOW.md` ก่อน — ให้รู้ behavior ปัจจุบัน
- [ ] แก้โค้ด + อัปเดต section ที่เกี่ยวข้องใน `DOCUMENT_FLOW.md`
  (แก้ file:line, แก้ตาราง, แก้ลำดับขั้นถ้าจำเป็น)
- [ ] อัปเดตบรรทัดท้ายไฟล์: `Last verified against codebase: YYYY-MM-DD —
  commit <new-sha>` (รอใส่ sha จริงหลัง commit ก็ได้)
- [ ] ใส่ทั้ง 2 ไฟล์ใน commit เดียวกัน
- [ ] **ตรวจว่า sha ที่จดไว้อยู่บน branch จริง** — `git merge-base --is-ancestor <sha> HEAD`
  ก่อน push ทุกครั้ง. sha ที่เขียนลง doc *ก่อน* commit จะกลายเป็น **dangling ทันทีที่
  amend/rebase** (แก้ commit message · เพิ่มไฟล์ที่ลืม · ซ่อม build) ⇒ doc ชี้ไปยัง object
  ที่ `git show` ยังเปิดได้วันนี้แต่ **ไม่อยู่ในประวัติของ branch** และจะหายจริงหลัง `gc`
  ⇒ คนที่ตามรอยว่า "พฤติกรรมนี้เปลี่ยนที่คอมมิตไหน" จะหาไม่เจอ
  _(ที่มา: รอบ 163/164 จด `dbaa778`/`0c80a7b` ไว้ ซึ่งเป็น sha ก่อน amend — ของจริงคือ
  `ff635b0`/`0393d2f`; `git cat-file -t` ตอบว่า "commit" ทั้งคู่จึงดูเหมือนถูก
  — **`cat-file` พิสูจน์ว่า object มีอยู่ ไม่ได้พิสูจน์ว่าอยู่บน branch**)_
  _(**และวิธีเติม sha ก็สำคัญ**: `git commit --amend` หลังเติม sha ลง doc **เปลี่ยน sha
  ที่เพิ่งเขียนไปเสมอ** ⇒ วนไม่รู้จบ. ให้เติม sha ใน **คอมมิตตามหลังอีกใบ** (หรือปล่อย
  `<pending>` ไว้แล้วตามเก็บ) — จับได้เพราะ `doc_commit_sha_check` ฟ้องทันทีหลัง amend
  ในรอบที่เพิ่งเขียน checker ตัวนี้เอง)_

### Anti-pattern — ห้ามทำ

```
❌ "เดี๋ยวค่อยอัปเดต doc ทีหลัง" → doc drift → คนถัดมา (รวม AI) อ่าน doc
   แล้วทำผิดเพราะ doc ไม่ตรงโค้ด
❌ commit แยกระหว่างโค้ดกับ doc → ระหว่าง 2 commit นี้ branch อยู่ใน
   inconsistent state
❌ อัปเดตแค่ตาราง ไม่อัปเดต file:line → ลิงก์ใน "Quick reference" จะตาย
   หลัง refactor
```

### ถ้าพบ doc กับโค้ดไม่ตรง

แปลว่า **doc ผิด** (โค้ดเป็น ground truth). ให้แก้ doc ทันทีในคอมมิต
เดียวกับงานที่กำลังทำ — ห้ามรอ

## 📗 ACCOUNT_STRUCTURE.md — โครงสร้างลูกค้า/กลุ่มบริษัท/สาขา/บิลลิ่ง/API

`ACCOUNT_STRUCTURE.md` (root) คือ single source of truth ของชั้น
**BillingAccount → Company → Branch**, ผลิตภัณฑ์ Connected (`/api/v1`),
`UsageEvent`/pricing, portal `/connect` — ใช้กฎการดูแล**ชุดเดียวกับ
DOCUMENT_FLOW.md ทุกข้อ**: แตะ entity/พฤติกรรมที่ไฟล์นั้นครอบ (Company,
Branch, AccountSubscription, Subscription, ExternalIntegration/ApiClient,
billing, quota resolution) → อัปเดตไฟล์ + ป้ายสถานะ (✅/🔨/📋) + บรรทัด
`Last verified` ในคอมมิตเดียวกัน. ไฟล์นี้แยกส่วน "มีจริง" กับ "ออกแบบไว้"
ชัดเจน — ห้ามปล่อยให้ 📋 ที่สร้างเสร็จแล้วยังติดป้ายเดิม


## 📙 ERP_REVIEW_2026-09-05.md — ผลตรวจรอบ "ทีม ERP" (A–F) + แผนสู่ ERP

`ERP_REVIEW_2026-09-05.md` (root) คือผลตรวจรอบที่สอง โจทย์จากเจ้าของโปรเจกต์: "ทีมที่ครอบ
ทุกมุม — ความต่อเนื่อง/ถูกต้อง/ครบถ้วนของข้อมูล · ประเภทเอกสาร จุดแสดงผล จุดให้เลือก ตรงกัน
ไหม · ระบบเดิมต้องถูกก่อนค่อยขยายเป็น ERP". รายงานเต็มของแต่ละทีมอยู่ใน
`erp-review/2026-09-05/report-*.md` + `VERIFY-main.md` (สิ่งที่ main agent เปิดไฟล์ยืนยันเอง)
- **กติกาเดิมทุกข้อของ SYSTEM_REVIEW ใช้กับไฟล์นี้** (verify ก่อนเชื่อ · ติ๊ก `✅ <sha>` ไม่ลบแถว ·
  §"ตรวจแล้วไม่ใช่บั๊ก" ห้ามรายงานซ้ำ)
- ครบ 9 ทีม (F บางส่วน — ควรรันซ้ำ) · แก้แล้ว P0 6 + P1 15 ในคอมมิตชุดรอบ 135 · ที่เหลือเป็น backlog
  เรียงลำดับใน §1/§9 ของไฟล์นั้น · brief สำหรับรอบถัดไป: `erp-review/2026-09-05/BRIEF.md`
- ราก 9 ข้อที่ต้องซ่อมก่อนขยายเป็น ERP อยู่ใน §6 — **ห้ามเพิ่มโมดูลใหม่ทับรากที่ยังไม่ซ่อม**
  (โดยเฉพาะ: ชั้น posting เดียว · DocumentTypeRegistry/DocumentStatusRules · สิทธิ์ที่ server ·
  enum→UI จากแหล่งเดียว · Sales Order)
- helper ใหม่ที่ทุกเส้นต้องใช้แทนสำเนามือ: `Helpers/DocumentStatusRules` (แทน `== Approved`) ·
  `Helpers/StockMovementSign` (เครื่องหมาย StockMovement) · `PayrollRunEditPolicy.CanVoid` ·
  `Helpers/CashSaleStockRules` (ใบเสร็จ standalone ↔ สต๊อก/COGS ตาม `CashSaleStockPolicy`) ·
  `Helpers/ArApScope` (ชุดชนิดลูกหนี้/เจ้าหนี้ — ใบวางบิล**ไม่ใช่**ลูกหนี้) ·
  `Helpers/TipAccountResolver` (บัญชีทิป POS/TipPayout — ห้าม 216xx) ·
  ทุกทางเข้าอนุมัติเอกสาร (เว็บ/กฎ/ลายเซ็น/มือถือ/LINE) ต้องผ่าน `DocumentPermissionHelper.CanApproveAsync`
- helper กลางจากรอบ 193 (ทุกเส้นต้องใช้ ห้ามเขียนสำเนา): `Helpers/DepositPolicyResolver` (โหมดมัดจำ 3 แบบ) · `Helpers/ContactTaxBranchKey`
  (คีย์ผู้ติดต่อ = เลขภาษี+สาขา · `SoftScope` · `AdoptTaxId(..., ContactMatchKind)`) · `IIssuedDocumentHooks.RunAsync` (e-Tax หลังออกเอกสาร
  ทุกทางเข้า — หลัง commit) · `Helpers/CompanyVatStatus` + `CompanySettingsFactory` (ธง VAT · stopgap รอเจ้าของตัดสินต้นทาง) ·
  `Helpers/InputVatVehicleRule` (§82/5(6)) · `Helpers/OwnerActionGuard` + `[RejectApiKey]`/`[RequireOwner]` (งานระดับเจ้าของห้ามคีย์ API) ·
  `IAttachmentAccessGate` (ด่านไฟล์แนบ/สแกนตัวเดียว) · `Helpers/DocumentSignedContent` (ลายเซ็นลูกค้าผูก hash เนื้อหา) ·
  `AuditHashChain.Seal/Analyze` (hash chain canonical ตัวเดียว)

## 📕 SYSTEM_REVIEW_2026-09.md — ลิสต์งานจากการตรวจทั้งระบบ (8 ทีม)

`SYSTEM_REVIEW_2026-09.md` (root) คือผลตรวจทั้งระบบโดยทีมผู้เชี่ยวชาญ 8 ด้าน
(เอกสาร · onboarding/nav · บัญชี/ภาษี · payroll · OCR/AI · security/arch ·
frontend · โมดูลรอง) **180 ข้อ (P0 25 · P1 61 · P2 65 · P3 29)** พร้อม file:line
ทุกข้อ และ P0/P1 ผ่านการ verify ซ้ำโดย main agent — ใช้เป็น backlog หลัก

**สถานะ ณ 2026-09-04: ติ๊กแล้ว 84 แถว · P0 เหลือ 0 · P1 31 · P2 43 · P3 20**
สิ่งที่เหลือ**ไม่ใช่บั๊กที่แก้ได้ในที่เดียว**อีกแล้ว แบ่งเป็น 3 กอง — ต้องเลือก
อย่างตั้งใจ ไม่ใช่ไล่ทำตามลำดับ ID:
1. **ฟีเจอร์ใหม่** (ภ.ง.ด.50 export · ค่าเผื่อหนี้ TFRS บทที่ 9 · ไฟล์โอนเงินเดือน
   เข้าธนาคาร · หน้าเก็บ ปกส./PVD รายคน · ปฏิทินวันหยุดราชการ) — งานหลายวัน/ชิ้น
2. **re-design ที่ผลตรวจระบุเองว่าให้ประเมินแยก** (ยุบสองแดชบอร์ด · ชั้นสต็อก
   ทางเข้าเดียว · component layer · migration lock + `CONCURRENTLY`)
3. **ของที่ต้องตัดสินใจว่า "ต่อสาย หรือ ลบ"** (E-commerce 460 บรรทัดที่ไม่มี UI ·
   Open Banking ที่คอมเมนต์เขียนว่า "simulate" · `api.js` 101 เมธอดที่หน้าไม่เรียก)
   — **ห้ามเดาแทนเจ้าของโปรเจกต์** สองทางเลือกให้ผลต่างกันคนละเรื่อง

### กติกาการใช้

- **อ่าน §1 (20 ข้อแรก) + §2 (ต้นเหตุร่วม) + §3 (ลำดับ sprint) ก่อนลงมือ** —
  หลายข้อมีรากเดียวกัน แก้ที่รากหนึ่งครั้งปิดได้หลายข้อ
- **§9 คือรายการที่ตรวจแล้วไม่ใช่บั๊ก** — ห้ามรายงานซ้ำ ห้ามแก้
- ทุกข้อที่แก้ต้องปฏิบัติตามกฎเหล็ก #4 ครบ (reproduce → pure class + เทสต์ →
  checker negative test → sync DOCUMENT_FLOW/ACCOUNT_STRUCTURE/TEST_PLAN ในคอมมิตเดียว)
- เมื่อแก้ข้อใดเสร็จ ให้**ติ๊กในไฟล์นั้น** (เติม `✅ <sha>` หน้า ID) ไม่ลบแถว —
  เพื่อให้รอบถัดไปรู้ว่าอะไรปิดแล้ว ปิดที่คอมมิตไหน
- §10 คือส่วนที่ยังไม่ได้ตรวจ เรียงตามความเสี่ยง — ทีมตรวจรอบถัดไปเริ่มจากตรงนั้น

## 📓 DECISION_DOCTRINE.md — ตรวจอะไรก่อน · เมื่อไรถาม AI · เอาคำตอบกลับมาเรียนยังไง

`DECISION_DOCTRINE.md` (root) คือ **กติกากลางของ "ขั้นตอนการตัดสินใจ"** ทั้งระบบ — กลั่นจากโค้ดจริง
โดยทีม 3 ด้าน (ลำดับชั้นหลักฐาน · เกณฑ์โยนให้ AI · วงจรเรียนรู้) รอบ 177 · **อ่านก่อนเขียนตัวตัดสิน
(`Helpers/*Evidence.cs` · `*Guard.cs` · `*Policy.cs`) หรือก่อนเพิ่มจุดที่เรียก AI ใหม่ทุกครั้ง**
- **§1 ลำดับชั้นหลักฐาน (G1–G7)** — เรียงด้วย "ระยะห่างจากของจริง" ไม่ใช่ confidence ที่ผู้เสนอแต่งเอง ·
  "ไม่รู้" ต้องเป็นค่าใน enum · **เงื่อนไขที่เป็นเท็จเพราะไม่มีข้อมูล ห้ามตกเป็น "ผ่าน"** ·
  ทิศปลอดภัย = ทิศที่ความเสียหาย**มองเห็นและแก้ทัน** ไม่ใช่ทิศที่เงียบ
- **§2 ถาม AI ได้เมื่อครบ 4 ข้อพร้อมกัน** + ชั้นการรับคำตอบ 3 ชั้น + **เกณฑ์ต่างกันตามทิศของ
  ความเสียหาย** (silence-gate ≥0.70 + ต้องทิ้งร่องรอย · warn-gate ไม่มีขั้นต่ำ · write-gate ≥0.70
  + candidate set) · **ห้ามใช้ `HasModelAnswer` เดี่ยว ๆ** (tier-2 majority ไม่ดูอินพุตเลย)
- **§3 วงจรเรียนรู้** — คำตอบจริงอยู่ที่ "อนุมัติ" · เก็บสองทิศ · **กันคลังเอียง 5 ข้อ** ·
  ตัวชี้วัดที่แยก "local โตจริง" ออกจาก "ระบบเงียบลง"
- **§4 backlog ที่ยืนยันแล้ว** (V1 `OcrFieldArbiter` ไม่ได้ตัดสินแต่หน้าจอบอกว่าตัดสิน · GAP-1 สูตร
  จับคู่ธนาคารสองชุดที่ให้อันดับต่างกัน · kill-switch ที่ไม่ผ่าน ฯลฯ) · **§5 = ที่ตรวจแล้วไม่จริง
  ห้ามรายงานซ้ำ** · **§6 = 4 ข้อที่ต้องให้เจ้าของตัดสิน**

## 📗 DECISION_AUDIT_2026-09-18.md — ตรวจ "กระบวนการตัดสินใจ" ทั้งระบบ (8 ทีม) + แผนรอบพัฒนาถัดไป

`DECISION_AUDIT_2026-09-18.md` (root) คือผลตรวจรอบ 181 โดยทีม 8 ด้าน (เอกสาร/ด่านอนุมัติ · ยื่นภาษี · OCR · ธนาคาร/
จ่ายเงิน · สต็อก/สินทรัพย์ · เงินเดือน · สถาปัตยกรรม AI · POS/CMS/ที่พัก/ทางเข้าภายนอก) ตามโจทย์เจ้าของ "ตัดสินถูก
หลักการไหม · ลำดับขั้นถูกไหม · ครอบคลุมไหม · เรียก AI + เทรนให้ดีขึ้นเองครบไหม" — **main agent เปิดไฟล์ยืนยันทุกข้อ
P0/P1 ก่อนเขียน** (✅ ในตาราง) · กติกาเดียวกับ SYSTEM_REVIEW/ERP_REVIEW/DOCTRINE ทุกข้อ
- **§1 คำตอบ 5 ข้อ** · **§2 ต้นเหตุร่วม 7 แบบ (R1–R7)** — สถานะปลายทางประทับเอง · "ไม่รู้"→ค่าแต่ง · ตารางกฎหมาย
  สำเนาที่สอง · สองด่านในเมธอดเดียว · ทางเข้าอื่นไม่เดินด่าน · ลูปเรียนรู้ไม่ปิด/สอนตัวเอง · ไม่มีเทสต์ที่ด่านเงิน
- **§3 ผลตรวจรายทีม** (P0 ที่ยืนยันแล้ว: ด่าน WHT เก่ายังรันหลังด่านใหม่ · CIT 2 ตาราง ภ.ง.ด.50 ใช้ขั้น SME ทุกบริษัท ·
  Filed จากปุ่ม · OCR serialize บรรทัดก่อน Product master · VendorIntel สวมรอยกระดาษเรื่อง WHT · BankFeed Matched ไม่มีคู่ ·
  FIFO เศษ `Max(taken,1)` · ด่านสต็อกติดลบถูกข้ามด้วย override · POS ใบกำกับไม่หักส่วนลด · V1 เลข 13 หลัก = นิติบุคคล ·
  Integration VAT 7 ไม่ดู `IsVatRegistered` · sentinel `__USER_KEPT_EXISTING__` ไหลเข้าตัวแนะนำ GL · กุญแจคลังเขียน≠อ่าน
  เมื่อนักเรียนตอบ · JS 6 จุดไม่ส่ง `source`) · **§5 = ที่ตรวจแล้วไม่จริง/บรรทัดคลาด ห้ามรายงานซ้ำ** · **§7 = ไม่ใช่ปัญหา ห้ามแก้**
- **§6 แผนรอบถัดไป**: §6.0 เทสต์ก่อนแตะ (`ApprovalWarningGolden` · `BankMatchGolden` · FIFO/CIT/POS/Payroll · checker
  `terminal_status_writer`) → §6.1 P0 22 ข้อทำได้เลย → §6.2 P1 รายโดเมน → §6.3 P2 โครงสร้าง → **§6.4 = 9 ข้อที่เจ้าของ
  ต้องตัดสินก่อน ห้ามเดาแทน** (ฐาน ปกส. รวมเบี้ยเลี้ยง · `BuyerDeclinedTaxInvoice` ประทับเอง · ผลข้างเคียงถอดด่าน WHT เก่า ·
  VendorIntel auto-fill WHT · "ประกาศว่ายื่น" ต้องมีเลขรับไหม · ตาราง DTA · ขอบเขตฝากขาย/POC/LCNRV · วันเริ่มค่าเสื่อม ·
  ค้างจากรอบ 180)
- **ห้ามเพิ่มโมดูล/ฟีเจอร์ทับรากใน §2 ที่ยังไม่ซ่อม** — โดยเฉพาะ R1 (สถานะปลายทาง) และ R5 (ทางเข้าอื่น) เพราะทุกทางเข้า
  ใหม่จะสืบทอดช่องโหว่เดิม

## 📔 REGRESSION_ROOT_CAUSE_2026-09-18.md — ทำไมของที่เคยดีกลับแย่ลง + กลไกให้ระบบดีขึ้นเรื่อย ๆ

`REGRESSION_ROOT_CAUSE_2026-09-18.md` (root) คือผลตรวจรอบ 169 โดยทีม 4 ด้าน (โบราณคดีการถดถอย 33 กรณี ·
logic ซ้อน 10 หมวด · สายข้อมูล 8 ค่า · สถาปนิกกระบวนการ) ที่ main agent เปิดไฟล์ยืนยันทุกข้อ — **อ่าน §1 (คำตอบ 5 ข้อ)
+ §8 (หลักการ 10 ข้อ + checklist ก่อน push 12 ข้อ + ข้อห้าม 7 ข้อ — สำเนาอยู่ใน กฎเหล็ก #4 F2–F4) ก่อนเริ่มงานทุกรอบ** · บทเรียนดิบอยู่ `docs/lessons/`
- §6 = รายการที่ทีมรายงานมาแล้ว**ไม่จริง** — ห้ามรายงานซ้ำ · §7.3 = 2 เรื่องที่ต้องให้เจ้าของตัดสิน (เปิด host .NET ใน
  proxy policy · เปิด `claude/**` ใน workflow แบบแก้ "เสียง" ไม่ใช่ปิด "ด่าน") · §10 = backlog พร้อมป้ายว่าใครต้องตัดสิน
- เครื่องมือที่เกิดจากรอบนี้: `tools/check_all.sh` · `tools/callers.py` · `tools/dead_helper_check.py` (+ baseline) ·
  `tools/test_inventory.py` — กติกา ratchet: baseline **ห้ามเพิ่มแถว** เพื่อให้ checker เขียว มีแต่ตัดออกเมื่อต่อสาย/ลบแล้ว
- **คำตัดสินเจ้าของ (รอบ 170)**: (ก) **CI เปิดบน `claude/**` แล้ว** — หลัง push ต้องอ่านผล Actions ผ่าน MCP
  (`actions_list` → `get_job_logs`) แล้วแก้ก่อนรายงานผู้ใช้; job `test` รันเฉพาะ PR/main/dispatch (ข) เจ้าของจะเปิด host
  .NET ใน proxy policy — เมื่อ `command -v dotnet` เจอ `check_all.sh` จะ build/test ให้เอง (ค) **50 ทวิ ออกอัตโนมัติเป็น
  Issued ตอนจ่าย** ทุกทางเข้า — ยอดนำส่ง/ปฏิทิน/รายงาน/ไฟล์ยื่น/แดชบอร์ด อ่านจาก certs ผ่าน `Helpers/WhtCertFilingScope.Filed`
  ตัวเดียว · เอกสารหัก WHT ที่ไม่มี cert ออกจริง = ช่องโหว่ที่ต้องเตือน+บล็อกนำส่ง ห้ามนับเงียบ (ง) ยุบหมวด F เป็นหลักการ
  10 ข้อ + ย้ายบทเรียนดิบไป `docs/lessons/` (คอมมิตถัดไป)

## 📒 OCR_PIPELINE_REVIEW_2026-09-06.md — ไปป์ไลน์ OCR → เอกสาร (ทีมตรวจ 5 ด้าน)

`OCR_PIPELINE_REVIEW_2026-09-06.md` (root) คือผลตรวจ **เส้นทางตั้งแต่อัปโหลดจนได้
เอกสาร + JE** โดยทีม 5 ด้าน (สมองนักบัญชี · วิศวกรรมการสกัดข้อมูล · สถาปัตยกรรม
การเรียนรู้/AI · UX 1-click · คุณภาพ/ตัวชี้วัด) — โจทย์: "แค่อัพเอกสารไป ก็เหมือนมี
นักบัญชีที่เก่งที่สุดในโลกมาทำให้". รายงานดิบของแต่ละทีม:
`erp-review/2026-09-05/ocr-report-T1..T5.md` · โจทย์ที่ให้ทีม: `.../OCR-BRIEF.md`
- ใช้กติกาเดียวกับ SYSTEM_REVIEW/ERP_REVIEW ทุกข้อ (verify ก่อนเชื่อ · ติ๊ก `✅ <sha>`
  ไม่ลบแถว · §"ตรวจแล้วไม่ใช่บั๊ก" ห้ามรายงานซ้ำ)
- §2 = 16 ข้อที่แก้แล้ว · §3 = backlog เรียง P1/P2/P3 · §4 = สถาปัตยกรรมเป้าหมาย
  (Decision Record + Arbiter · student ต้องตอบได้ · ปิด loop ที่เอกสารที่อนุมัติ ·
  eval harness + KPI คู่) · §5 = แผน 5 เฟส · §6 = 3 คำถามที่ต้องให้เจ้าของตัดสิน
- helper ใหม่ที่ทุกเส้นต้องใช้: **`Helpers/OcrLineReconciler`** (กระทบยอด Σ บรรทัด ↔
  หัวใบ — ห้ามเขียนตรรกะ 4 เคสเองอีก) · `OcrAiAugmentationResult.HasModelAnswer`
  (ใช้แทน `UsedAi` ทุกจุดที่จะ **นำคำตอบไป apply**) ·
  **`Helpers/OcrPostingReadiness`** (ตัวตัดสิน "อนุมัติอัตโนมัติได้ไหม" ตัวเดียวของ
  ทุกช่องทาง — เว็บ/LINE/มือถือ ห้ามเขียนเกณฑ์เอง) · **`Helpers/OcrReviewGuard`**
  (กรองคำตอบ AI ก่อนแตะฟอร์ม — ยอดเงินรับเป็นชุดและต้องลงตัว) ·
  **`Helpers/OcrVendorKeyEvidence`** (หลักฐานว่า "เลขที่ใช้ค้นทะเบียนเป็นของผู้ขายจริง" —
  ป้ายกำกับ + ไม่ใช่เลขผู้ซื้อ/เลขเรา + ไม่ได้อยู่ในบล็อกผู้ซื้อ · ส่งผลให้
  `DbdIdentityGuard.Judge(..., keyProven:)` ซึ่งเป็น**ตัวตัดสินตัวเดียว**ของทั้งเส้น OCR
  และเส้น integration — สำเนา inline ใน `OcrService` ถูกถอดแล้ว) ·
  **`Helpers/InputVatAccountPolicy`** (ธงผังบัญชีปิดการเคลม §82/5 — ใช้ทั้งเส้นคีย์มือ
  และเส้น OCR) · **`Helpers/RawTextLineSplitter`** (แตกบรรทัดจากข้อความเมื่อไม่มีโมเดล
  — ต้องผ่าน `OcrLineSplitGuard` เสมอ) · **`Helpers/OcrTargetDocumentType`** (ชนิดเอกสาร
  ที่สแกนจะกลายเป็น — ด่านสิทธิ์กับเส้นสร้างเอกสารต้องใช้ตัวเดียวกัน **ห้ามคืน "ไม่รู้"**) ·
  **`Helpers/OcrPostedTruth`** (ช่องไหนของสแกนควร sync ให้ตรงเอกสารที่อนุมัติแล้ว —
  ห้ามลบค่าเดิมด้วยช่องว่าง/ศูนย์) · `AiResponse.FromLocalModel` (**ธงเดียวที่บอกว่า
  "นักเรียนตอบ"** — ห้ามเดาจาก `ProviderModel`/`Status` อีก) · ตารางอัตรา/ประเภทเงินได้ ม.40 อ่านจาก
  **`Helpers/ThaiWhtRateTable`** ตัวเดียว และหน้าเว็บสร้าง dropdown จาก
  `/api/reference/income-types` (ห้ามพิมพ์อัตราซ้ำใน JS)
- §2c = รอบ "เริ่มดำเนินการทั้งหมด" — ปิด backlog P1 อีก 13 ข้อใน 4 ชุด (ความทนทาน ·
  สมองนักบัญชี · การสกัดข้อมูล · UX) ดูตารางในไฟล์นั้น

### บทเรียนจากรอบ OCR — ย้ายไป `docs/lessons/ocr-pipeline.md`

บทเรียน defect class ของไปป์ไลน์ OCR ทั้งหมด (รวมที่เคยอยู่ท้ายไฟล์นี้) อยู่ที่ `docs/lessons/ocr-pipeline.md` —
กติกาเดียวกับ F5: append ที่นั่น · แตะ CLAUDE.md เฉพาะเมื่อเปลี่ยนหลักการ

