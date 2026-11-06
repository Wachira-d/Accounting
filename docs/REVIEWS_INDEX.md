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

## 📥 IMPORT_ETL_REVIEW (รอบ 205 · 2026-10-14) — ศูนย์นำเข้าข้อมูล

`erp-review/2026-10-14/IMPORT_ETL_REVIEW.md` — inventory ทางนำเข้าเดิม 14 เส้น + แบบ pipeline เดียว (Extract→Transform→Stage→Review→Commit→Learn→Undo) ·
helper owner: `Helpers/ImportTabularReader` · `ImportValueNormalizer` · `ImportHeaderDetector` · `ImportColumnMapper` · คำตัดสินข้อ 151 ·
ก่อนแตะการนำเข้าใด ๆ อ่าน §2 ของไฟล์นั้น — ห้ามเขียนตัวแยกไฟล์/ตัวแปลงวันที่-ตัวเลข/ตัวจับคู่คอลัมน์สำเนาที่สอง

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

## 📐 docs/OCR_TOTALS_MATRIX.md — เมทริกซ์ยอดบรรทัด ↔ หัวใบ ทุกรูปแบบเอกสาร (ทีม T 2026-10-26)

สถานะปัจจุบันของตัวตัดสินยอด OCR (`OcrLineReconciler` เคส A–G · `OcrEtaxLineNormalizer`) ต่อช่อง {ราคาบรรทัด} × {ส่วนลด} × {VAT} × {XML}: ตัวตัดสิน · เทสต์ที่ล็อก · ช่องว่างที่เหลือ ·
เส้นจริงของใบ SP-IN2026-10-00933 เป็นตัวเลขทุกขั้น · ระดับคำเตือนบนหน้าตรวจ · ข้อเสนอรอเจ้าของ (ส่วนลดท้ายบิลลง `BillDiscountAmount`) — แก้ไฟล์เมื่อโค้ดเปลี่ยน ไม่ append

## 📋 docs/BACKLOG_REGISTER_2026-10-26.md — ทะเบียนงานคงค้างรอบ 205 + คำตัดสินที่เสนอ + แพ็กงาน (ทีม W 2026-10-26)

คำสั่งเจ้าของ "ตั้งทีมวิเคราะห์งานคงค้าง และดำเนินการตัดสินใจ และพัฒนาให้เสร็จสิ้นทั้งหมด" — รวบรวมงานค้าง 66 รายการ (R-01…R-66) จากเอกสารทุกทีมรอบ 205
(BOOKING_ENGINE · BOOKING_REVENUE_AUDIT · PER_LINE_WHT · POS_MTO · COURSE_CREDIT · CMS_FEATURE_ROADMAP · OCR_TOTALS_MATRIX · LODGING §12 · INTEGRATION_RESYNC §12 · รายงานทีม G/H/I/J/M/O) ·
ทุกข้อเปิดไฟล์ยืนยัน (§7) · ตัดสินเอง 56 ข้อตาม DECISION_DOCTRINE + คำตัดสิน 116–158 + หลักที่เจ้าของย้ำ (ตั้งค่าได้ · ของเดิมที่ดีอย่าแก้ให้แย่ · ข้อมูลครบถูกต้องที่สุด · ไม่ปล่อยผ่าน) ·
**แพ็กงาน W1–W10** (ขอบเขตไฟล์ไม่ทับกัน · brief พร้อมใช้: ไฟล์ · คำตัดสิน · เกณฑ์เสร็จ · เทสต์ทิศตรงข้าม) · **5 คำถามเจ้าของ Q1–Q5** (§4 เลือกตอบ) · ตั้งใจไม่ทำ 16 (§5) · checklist manual test เจ้าของ 14 ข้อ (§6) ·
ทีมพัฒนาที่ปิดแพ็กให้ติ๊ก `✅ <sha>` ในไฟล์นี้ ไม่ลบแถว

## 📙 docs/JE_POSTING_AUDIT.md — เมทริกซ์ความถูกต้องของ JE อัตโนมัติทุกเส้น (ทีม V 2026-10-26)

ตรวจตัวประกอบ JE จริงทุกเส้น (AutoPost ขาย/ซื้อ/CN-DN/รับ-จ่าย/PV/CIL/GRN · การชำระ/allocation · POS · settlement/gateway · ค่าเสื่อม · ปิดปี ·
recurring · payroll · integration · course credit · ที่พัก) หลังเคส PV-202610-0040 (บีบฐาน 500.93→402.93) · 52 เคส · ถูก 43 · ❌ 2 แก้แล้ว
(**V-DN-01** ใบเพิ่มหนี้ของใบที่ชำระครบลง Dr เงินสดตอนอนุมัติแล้วซ้ำตอนรับชำระ · **V-CN-03** ขาเงิน CN/DN ลง "111" ตายตัว) ·
⚠ 5 · ❓ 2 (V-PAY-05 ย้าย 21913 ทั้งก้อนเมื่อรับบางส่วน · V-CN-04 CN ของใบชำระครบ = คืนเงินทันทีเสมอ) · §3 SQL ตรวจข้อมูลเก่า (อ่านอย่างเดียว) ·
ตัวตัดสินใหม่ `AdjustmentNoteAccount.CounterLegIsMoney` · เทสต์ `JePostingAuditTests` · ยังไม่ผ่านฝ่ายค้าน

## 🗂 erp-review 2026-10-01 … 2026-11-06 — ดัชนีรายไฟล์ (เติมรอบ 250 WP-05 · 1 บรรทัด/ไฟล์ = ใช้ตัดสินเรื่องอะไร)

> ปิดแถวใดให้ติ๊ก `✅ <sha>` ในไฟล์ต้นทาง ไม่ลบแถว · ไฟล์ "สถานะปัจจุบัน" ยังเป็น DOCUMENT_FLOW / ACCOUNT_STRUCTURE / TEST_PLAN — ไฟล์ข้างล่างคือหลักฐาน ณ วันที่ตรวจ

**2026-10-01 (รอบ 201 · 8 ทีมขนาน)** — `BRIEF.md` ขอบเขต/ทีม · `BACKLOG.md` ทะเบียนงานค้างหลังรอบ 200 · `team-AI.md` AI/ธนาคาร (+ แก้ผลฝ่ายค้าน X-1..X-10) ·
`team-DV.md` ยกเลิก/ใบแทน/e-Tax · `team-GW.md` gateway/integration (GWO-1..7) · `team-IN.md` สต็อก/สินทรัพย์/ค่าตั้ง/ที่พัก (ตาข่าย C-5) ·
`team-OC.md` OCR · `team-PL.md` platform/audit hash chain/advisory lock · `team-PR2.md` เงินเดือน (ฝ่ายค้านรอบสอง) · `team-ST.md` settlement · `team-TX.md` ภาษี/ด่านอนุมัติ/มัดจำ
**2026-10-02 (รอบ 202)** — `LODGING_REVIEW.md` ที่พัก 3 ทีม (คำตัดสิน 116–127) · `PP36_REVIEW.md` ภ.พ.36 บริการต่างประเทศ §83/6 (ฐานรวมภาษีที่ออกแทน — คำตัดสินข้อ 40)
**2026-10-16** — `UNIT_TEST_TRIAGE.md` เทสต์ที่เน่าเพราะไม่มีด่านรัน (ต้นเรื่องของ WP-01 รอบ 250)
**2026-10-29 (รอบ 209 · CMS จำลองธุรกิจจริง)** — `README.md` ภาพรวม · `PLAN.md` แผนจากทีม A–E · `BUSINESS_SIMULATIONS.md` 5 ธุรกิจเดินจากโค้ด · `MTO_ORDER_REVIEW.md` สินค้าตามสั่ง ·
`SHIPPING_REVIEW.md` จัดส่ง/รับเอง · `CLASS_BOOKING_REVIEW.md` จองคลาส · `SETTINGS_INTEGRATION_MAP.md` แผนที่ค่าตั้ง CMS ↔ ERP
**2026-10-30 (รอบ 211–219)** — `BOOKING_PAYMENT_DESIGN.md` รับชำระล่วงหน้าการจอง · `CMS_UX_REDESIGN.md` พื้นที่ทำงานรายเว็บ (รอบ 215) · `LINE_CUSTOMER_NOTIFY_DESIGN.md` แจ้งลูกค้าปลายทางทาง LINE (ข้อ 170) ·
`NOTIFY_PREFS_QUOTA_DAILY_SUMMARY_DESIGN.md` ค่าตั้งแจ้งเตือน/โควตาช่องกลาง · `PLATFORM_BILLING_AUDIT.md` เงินผู้เช่าเข้า NextAcc (G-16 ปิดโดยรอบ 231) · `PLATFORM_GATEWAY_REFUND_POLICY_DESIGN.md` Omise ของ NextAcc + นโยบายคืนเงิน · `mockups/`
- `erp-review/2026-11-01/BOOKING_PAYMENT_AUDIT.md` — มัดจำการจองทุกค่าตั้ง × ทุกช่องทาง (ค่าส่ง POS ปิดแล้ว — ติ๊กรอบ 250)
- `erp-review/2026-11-01/PETTY_CASH_AUDIT.md` — เงินสดย่อยเปิด→ปิดกองทุน (รอบ 221–228)
- `erp-review/2026-11-01/TAX_PENALTY_AUDIT.md` — เงินเพิ่ม/เบี้ยปรับ/ค่าปรับ (ค่า "เสนอ" + tooltip — N5)
- `erp-review/2026-11-02/CMS_SETTINGS_UX_AUDIT.md` — UX ตั้งค่า CMS ทั้งหมด (รอบ 230 wizard)
- `erp-review/2026-11-02/FILE_STORAGE_AUDIT.md` — ที่เก็บไฟล์กลาง Local/S3 · ผู้เขียน/ผู้อ่านไฟล์ (DataProtection อยู่ DB แล้ว — ติ๊กรอบ 250)
- `erp-review/2026-11-02/PLATFORM_OMISE_AUDIT.md` — Omise ฝั่งแอดมินแพลตฟอร์ม เงิน→เอกสาร→บัญชี (รอบ 227)
- `erp-review/2026-11-02/QUOTA_ADDON_SUBSCRIPTION_AUDIT.md` — โควตา · ส่วนเสริม · ต่ออายุ
- `erp-review/2026-11-03/LICENSE_CASE_MATRIX.md` — ทุกเคสแพ็กเกจ/License + สิ่งที่ลูกค้าได้
- `erp-review/2026-11-03/LICENSE_USAGE_OVERAGE_AUDIT.md` — ส่วนเกินโควตา · ราคาพิเศษ · ชนิดบัญชี (รอบ 231 — บรรทัดสถานะ ⏳ แก้เป็น ✅ รอบ 250)
- `erp-review/2026-11-03/OMISE_READINESS.md` — ความพร้อมส่ง Omise ตรวจ + รายการ go-live 10 ข้อ (N11) · webhook ขาออกไม่ใช้ proxy (Y8)
- `erp-review/2026-11-03/legal-en/` — ร่างคำแปลนโยบาย 6 ฉบับ (ประวัติ — ตัวจริง `LegalDocumentDefaults.En.cs`)
- `erp-review/2026-11-06/ASSET_MULTI_DOCUMENT_DESIGN.md` — สินทรัพย์ 1 ชิ้น ↔ หลายใบ (รอบ 249 · Q1–Q5 → คำตัดสิน 250-24…27)
- `erp-review/2026-11-06/AUTOPILOT_BILLING_DESIGN.md` — รายการประจำ→ส่ง→รับเงิน→ออกใบ→คู่ค้า (รอบ 248–254 · คู่ค้าข้ามผู้เช่า §6 = ตัวตั้ง)
- `erp-review/2026-11-06/AUTO_SEND_WHT_DESIGN.md` — ส่งเอกสารอัตโนมัติ + รับ 50 ทวิ เข้า (คำตัดสินเจ้าของ §11 → DECISIONS 243-AS · §1.1/G-8 ชี้ `TradingPartnership` รอบ 250)
- `erp-review/2026-11-06/ETAX_BY_EMAIL_AUDIT.md` — e-Tax by Email (รอบ 244 · รหัสหัวเรื่อง — N1)
- `erp-review/2026-11-06/EWHT_EVIDENCE_RESEARCH.md` — หลักฐาน e-WHT (N4 — แนบเป็น 50 ทวิ ธรรมดา · ไม่เสนอ 1%)
- `erp-review/2026-11-06/LINE_MULTICHANNEL_DESIGN.md` — ส่งเอกสาร/แจ้งเตือนหลายช่องทาง L1–L6 (§10 คำตอบเจ้าของ)
- `erp-review/2026-11-06/LINE_OA_TERMS_CHECK.md` — ข้อกำหนด LINE OA ส่งในนามผู้เช่า (C1–C10 · C9 อยู่ในร่างข้อกำหนด Y6 แล้ว)
- `erp-review/2026-11-06/NOTIFY_PRICING_ANALYSIS.md` — ราคา/โควตาข้อความ LINE-อีเมล (N7 · N9)
- `erp-review/2026-11-06/RETENTION_LAW_CHECK.md` — เลขมาตรา พ.ร.บ.การบัญชี (ม.14 เก็บ · ม.10 รอบบัญชี · ม.19 ผู้ทำบัญชี · ETA ม.12) — ลงมือรอบ 250 WP-06
- `erp-review/2026-11-06/AUDIT_249_FOR_OPUS.md` — **ใบสั่งงานรวมรอบ 250** (WP-01…13 · คำตัดสิน §2 41 ข้อ + §3.3 19 ข้อ → DECISIONS 250-*)
- `erp-review/2026-11-06/AUDIT_249_COVERAGE.md` — ตรวจความครอบคลุมใบสั่งงาน (~93% · ดึงบั๊กคู่ค้า P-1/2/3/6 เข้า WP-02b)
- `erp-review/2026-11-06/audit-249/A_BACKLOG_DECISIONS.md` — งานค้าง/คำตัดสินค้าง 233–249 (95 แถว)
- `erp-review/2026-11-06/audit-249/B_E2E_BILLING_PROCESS.md` — กระบวนการรายการประจำต้นจนจบ (F-1…F-9)
- `erp-review/2026-11-06/audit-249/C_TAX_MONEY_CORRECTNESS.md` — เงิน/ภาษีรอบ 241–249 (F1…F9 · ลงบัญชีผิดเงียบ = 0)
- `erp-review/2026-11-06/audit-249/D_UX_UI.md` — UX/UI 71 ข้อ + 13 รูปแบบ (ร้ายแรง 10 ข้อ → WP-03)
- `erp-review/2026-11-06/audit-249/E_QUALITY_TESTS_DOCS.md` — CI/เทสต์/เอกสาร (H-1 เทสต์ pure ไม่เคยรัน · M-1…M-6)

**`docs/BACKLOG_REGISTER_2026-10-26.md` — 24 แถวยังไม่มี ✅ (ตรวจกับโค้ด 2026-11-06 รอบ 250):** ไม่มีแถวใดปิดในโค้ดแล้วโดยไม่ได้ติ๊ก ·
**R-21** (JE ขา 2191x ใบต่างสกุล `PerLineWht` vs `ToBaht`) ยังเปิดจริง — W7 วิเคราะห์ไม่ merge (`0a74e86b`) · **R-37** golden/replay harness รอผล job เทสต์ pure ที่เปิดใน WP-01 (`010f39b0`) ·
**R-22 · R-28 · R-29 · R-30 · R-35 · R-36 · R-42 · R-48** (+ R-52 OCR สลิป · R-13 · R-16 บางส่วน) = **ตั้งใจไม่ทำ/รอข้อมูลภายนอก** ตามตาราง §ไม่ทำ ของไฟล์นั้น ·
**R-66 + M-01…M-14** = ชุดทดสอบด้วยมือ (ต้องคนกด — agent ปิดเองไม่ได้)
