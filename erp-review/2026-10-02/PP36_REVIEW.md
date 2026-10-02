# ผลตรวจ ภ.พ.36 บริการต่างประเทศ (§83/6) — 2026-10-02

ที่มา: ผู้ใช้ส่งภาพใบสำคัญจ่าย PV-20260901-0001 (Booking.com B.V. ค่าคอมมิชชั่น 5,908 · ติ๊กบริการต่างประเทศ) ถามว่า "ภาษีซื้อขาหนึ่ง ภาษีขายขาหนึ่ง ลงผิดไหม"
แล้วสั่ง "ตรวจการลง JE และทั้งกระบวนการตั้งแต่ต้นจนจบ". ทีมตรวจ 3 ทีม (T ภาษี/บัญชี · C โค้ด · E วงจรทั้งเส้น) · main agent เปิดไฟล์ยืนยัน P0 ด้วยตัวเอง (✅).
คำตัดสินที่ใช้: `erp-review/2026-09-29/DECISIONS.md` ข้อ 129–136.

## คำตอบต่อคำถามผู้ใช้
ทรง JE ถูกตามกฎหมาย: Dr ค่าใช้จ่าย 5,908 · Dr 11640 ภาษีซื้อยังไม่ถึงกำหนด 413.56 / Cr 21912 VAT ค้างนำส่ง ภ.พ.36 413.56 · Cr ธนาคาร 5,908.
"ภาษีขาย" ในชื่อผัง 21912 เป็นชื่อที่ชวนเข้าใจผิด (คำตัดสิน 132) · ที่ผิดจริงคือ **JE นี้ไม่เคยถูกบันทึก** (P0-1/P0-2) และวงจรก่อน/หลังมีช่องโหว่ (E-*).

## P0 (ยืนยันแล้ว)
| ID | ปัญหา | หลักฐาน | สถานะ |
|---|---|---|---|
| P0-1 ✅ | `JournalPostingGuard` กฎ JE-NO-COUNTERPART เทียบขาเครดิตที่ไม่ใช่ภาษี (ธนาคาร 5,908) กับ `TotalAmount` 6,321.56 และนับ 21912 เป็นบัญชีภาษีขาย ⇒ **JE §83/6 ถูกปฏิเสธทุกใบ** (PV ตรง · PI/Expense · ใบค่าธรรมเนียมรอบโอน OTA) · ตัวสแกน `JournalAnomalyService:128-135` ใช้กฎเดียวกัน | `Services/JournalPostingGuard.cs:47,134-143` · โยนที่ `DocumentService.cs:~16878` | ทีม F1 |
| P0-2 ✅ | อนุมัติล้มแต่ค่าที่แก้ค้างใน DbContext (เลขเอกสาร · Status=Paid · InputVatPostedAsUndue · TaxPointDate) ถูกบันทึกโดย SaveChanges ที่ตามมา ⇒ **ใบ "อนุมัติแล้ว" ไม่มี JE** · `ApproveDocumentAsync` rollback ธุรกรรมแต่ไม่ถอย tracked entity (`:6503-6507`) · ผู้เรียกที่กลืน error แล้ว save ต่อ: `CreateDocumentAsync` auto-approve PV เงินสด (`:1648` LogInformation "staying Draft" ไม่จริง) → `AuditMiddleware:106` · `ApprovalService.TryFinalizeApprovedEntityAsync :452-480` (Console.Error) · `DocumentController.BulkApprove :1525` · `OcrController :455/467` · `RecurringTransactionService :528` · `SettlementPostingService :381` · (LINE `LineBotService:364/830` ต้องยืนยัน) · บั๊กคลาสเดียวกับรอบ 194 ที่ `CmsBookingService:585-600` แก้ด้วย `TrackedChangeRevert` เฉพาะผู้เรียกตัวเดียว | `DocumentService.cs:1626-1652, 6503-6507` · `Middleware/AuditMiddleware.cs:106-107` · `RevertTrackedChangesSinceAsync :4138` | ทีม F1 |
| P0-3 | integration `CreatePaymentVoucherJournalAsync` คืน null + LogWarning ขณะที่ใบถูกสร้าง Approved ⇒ ใบไม่มี JE เงียบ (บั๊กทั่วไป) · API v1/integration ไม่มีช่อง `IsForeignService` | `IntegrationService.cs:3998,4018,4082,4089` | ทีม F1 |
| E-1 ✅ <pending> | ยอดที่ต้องจ่าย/ค้าง/จ่ายแล้วของใบต่างประเทศรวม VAT ประเมินเอง (`TotalAmount`/`BalanceDue`/`PaidAmount` = 6,321.56) ขณะ GL ตั้งเจ้าหนี้/จ่ายแค่ 5,908 ⇒ ยอดค้างปลอม 413.56 ในอายุหนี้ หรือเจ้าหนี้ติดเดบิต + เงินออกเกิน · PDF สอง renderer พิมพ์ "ยอดรวมสุทธิ 6,321.56" + ตัวอักษร · จับคู่ธนาคารเสนอ `TotalAmount` | `DocumentService.cs:1592,1599,1604` · `CreatePaymentAsync ~12390` · `AgingReportService.cs:70,112` · `BankService.Reconciliation.cs:877,890` · `BankService.MatchResolution.cs:158` · `PdfGenerationService.cs:2223-2251` · `DocumentRenderer.cs:923-952` (ฝัง "ภาษีมูลค่าเพิ่ม 7%" ตรง — drift กับ `L.TotalVat`) | ทีม F2 (คำตัดสิน 131) |
| E-1b ✅ <pending> | แปลงใบซื้อ→ใบสำคัญจ่ายไม่ส่งธง `IsForeignService` + เส้น PV ที่ปิดหนี้ใบต้นทางไม่รู้จัก §83/6 (Dr เจ้าหนี้ 6,321.56) · คำเตือน `RD-83/6-UNFLAGGED` สั่งให้ติ๊กธงบนใบลูก ⇒ ภ.พ.36 นับซ้ำ · `ReclassifyForeignServiceAsync` ไม่กันใบที่มีใบต้นทาง | `ConvertCoreAsync :10974-11050` · `:16517-16594` · `:18966-18979` · `:7303-7417` | ทีม F2 |

## P1/P2
| ID | ปัญหา | หลักฐาน | ทีม |
|---|---|---|---|
| E-2 | เดือนเคลมภาษีซื้อ ภ.พ.36 สองกติกา: `RecognizePp36InputVatAsync` ใช้ `SupplierTaxInvoiceDate ?? PaymentDate` (+ หน้าเว็บติด "(แนะนำ)") ขัด `TaxService.ClaimBasisDate :3320` ⇒ คำตัดสิน 129: วันใบเสร็จ RD · JE รับรู้ลงวันนั้น · ห้ามเคลมเข้างวดที่ยื่น ภ.พ.30 แล้ว · อ้างมาตรา §77/2 → §82/4 + ใบเสร็จ RD | `StatutoryRemittanceService.cs:1250-1383 (1312-1313)` · `tax-remittance.html:706-712` · DOCUMENT_FLOW §6.2g | F3 |
| E-3 | ยอดค้าง/นำส่ง/รับรู้/รายงาน ภ.พ.36 คัดจากธง+สถานะเอกสาร ไม่ดูว่ามี JE (Cr 21912 / Dr 11640) จริง · รับรู้ย้าย `VatAmount` ทั้งก้อน (บริษัทไม่จด VAT · บรรทัดภาษีซื้อต้องห้าม) | `StatutoryRemittanceService.cs:316-341, 1288-1300` · `TaxService.GeneratePp36Report :1564-1640` | F3 |
| E-4 | สกุลเงินต่างประเทศ: ยอดค้าง/รายงาน/รับรู้ ใช้ยอดสกุลเอกสาร ไม่แปลงบาท (หน้า undue-vat แก้แล้วด้วย `ToGlAmount` `DocumentService.cs:5567-5571`) | `TaxService.cs:1617-1630` | F3 |
| E-5 | ใบที่อนุมัติหลังนำส่งงวดนั้น: นำส่งเพิ่มไม่ได้ (ด่านกันซ้ำ `:992-998`) แต่ถูกรับรู้ไปเคลม ⇒ คำตัดสิน 133 | `StatutoryRemittanceService.cs:992-998` | F3 |
| E-6 | ยกเลิก/ปลดธงหลังนำส่ง/รับรู้ไม่ถูกบล็อก (ด่านดู `FilingLockedAt` แต่รายงาน ภ.พ.36 ที่สร้างตอนนำส่งเป็นร่าง) · JE รับรู้ไม่มี `SourceDocumentId` ไม่ถูกกลับ ⇒ คำตัดสิน 134 · ยอดค้างติดลบ `continue` เงียบ `:334` | `DocumentService.cs:8095-8160, 8577` | F3 |
| E-7 ✅ <pending> | ใบลดหนี้ฝั่งซื้อของใบต่างประเทศ: Dr เจ้าหนี้ยอดรวม VAT · ไม่กลับ 21912 · รายงาน/ยอดค้างไม่หักใบลดหนี้ | `:15528-15541, 15612-15652` | F2 |
| E-8 | ชุดชนิดเอกสารที่นับ ภ.พ.36 ต่างกันสามเส้น (ยอดค้าง/ปฏิทิน `:635`/รายงาน) · ฝั่งสร้างตั้งธงบนใบขาย/CIL ได้ | `:1497, 2632` | F3 (ชุดชนิดตัวเดียว) |
| E-9 | ภ.พ.36 ช้าไม่มีเงินเพิ่ม ⇒ คำตัดสิน 135 | `StatutoryRemittanceService.cs:1021-1023` | F3 |
| E-10 | กระทบยอดภาษีกับ GL: เดือนนำส่งฟ้องผลต่าง −413.56 ปลอม · ไม่มีบรรทัด 11640 · ไม่มีสาเหตุ "ใบอนุมัติแล้วไม่มี JE" | `TaxGlReconciliationService.cs:95-100, 143-160` | F3 |
| T-2 | ชื่อผัง 21912 ⇒ คำตัดสิน 132 (migration เฉพาะชื่อเดิมทุกตัวอักษร · ข้อความสำรอง `PdfGenerationService.cs:835` · error `DocumentService.cs:15915,16648`) | `ChartOfAccountTemplates.cs:153` | F3 |
| T-4b ✅ <pending> | PV/ใบซื้อผู้รับต่างประเทศไม่หัก WHT + ไม่จำแนกเงินได้ ⇒ เงียบ (รอบโอนตั้ง 40(2) 15%) ⇒ คำตัดสิน 130 เตือน | `DocumentService.cs:19081-19093` · `SettlementWhtIncomeType` · `SettlementFeeTax.cs:121-125` | F2 |
| T-3d / C-P2 | ป้ายรายการ "ภ.พ.36 รอรับรู้"/"เคลม ภ.พ.30" ตัดสินใน JS จากธง ไม่ดู GL · หลังรับรู้ใบต่างประเทศไม่มีป้าย · "เคลม ภ.พ.30" ขึ้นกับใบ WaitingApproval | `documents.html:7530-7548` | F3 (server computes) |
| E-12 ✅ <pending> | §65 ตรี (11)(18) เตือนทุกผู้ขายต่างประเทศที่ไม่มีเลขภาษีไทย · (19) ไม่เคยทำงาน + จะบวกกลับยอดรวม VAT | `Section65TerValidator.cs:96-113,157` | F2 |
| E-13 | บรรทัดใบซื้อต่างประเทศใน ภ.พ.30 ขึ้นเหตุผล "[รอใบกำกับ §82/3]" ผิด | `TaxService.cs:851-859` | F3 |
| C-P2 ✅ <pending> | `DescribeMissingJournalAsync` ไม่แยกกรณี JE หักล้างเป็นศูนย์ · QuestPDF ฝัง "ภาษีมูลค่าเพิ่ม 7%" | `PdfGenerationService.cs:531-547` · `DocumentRenderer.cs:924` | F2 |

## ซ่อมข้อมูลเดิม
ใบที่อนุมัติแล้วแต่ไม่มี JE (รวม PV-20260901-0001): หลัง P0-1/P0-2 ขึ้น ⇒ เครื่องมือ "ลงบัญชีให้ใบที่อนุมัติแล้วแต่ไม่มี JE" (ทีม F1) ผ่าน AutoPost ตัวเดียว · งวดปิด/นำส่งแล้ว ⇒ ปฏิเสธพร้อมทางไปต่อ ·
ตัวสแกน DOC-NO-JE (`JournalAnomalyService.cs:145-170`) คำแนะนำ "ยกเลิกแล้วอนุมัติใหม่" ต้องชี้เครื่องมือนี้แทน ·
ใบต่างประเทศที่ BalanceDue รวม VAT ⇒ migration ตามคำตัดสิน 131 (ทีม F2) ✅ <pending> (`Helpers/ForeignServicePayeeBalanceMigration` — แถวสูตรเดิมเท่านั้น · ใบที่จ่ายเกินยอดจ่ายผู้รับเงินแล้วไม่แตะ + log ให้คนซ่อม) · **ระหว่างนี้ผู้ใช้อย่ากดนำส่ง/รับรู้ ภ.พ.36 ของ PV-20260901-0001**

## ตรวจแล้วถูกต้อง (ห้ามรายงานซ้ำ)
ทรง JE §83/6 ใน AutoPost + พรีวิวใช้ `ForeignServiceVat.SplitCredit` ตัวเดียว · 11640 บังคับ · tax point = วันจ่าย · ตารางกำหนดยื่นตัวเดียว (7 / 15 ต.ค.) ·
JE นำส่ง Dr 21912/Cr ธนาคาร · ไม่ประทับ Filed เอง · 21912 ไม่ถูกนับเป็นภาษีขายใน ภ.พ.30/กระทบยอด · งาน §82/3 6 เดือนไม่แตะใบต่างประเทศ · ยกเลิกก่อนนำส่งกลับครบ ·
หน้า 2 ของ PDF ที่หัวซ้ำ = ตั้งใจ · ปุ่มอนุมัติเดี่ยว `/approve` และเส้นลายเซ็นไม่บันทึกค่าค้าง

## ผลทีม F2 (รอบ PP36)
- predicate ที่ตกลงกับทีม F3: `ForeignServiceVat.OwnsPp36(Document)` / `OwnsPp36(type, isForeignService, vat, hasRelatedDocument)` + รูป EF `ForeignServiceVat.OwnsPp36Query` — ใบสำคัญจ่ายที่ปิดหนี้ใบต้นทางไม่ใช่เจ้าของ (ใบลูกสืบทอดธงแล้ว ⇒ นับจากธงตรง ๆ = ซ้ำ)
- ยอดจ่ายผู้รับเงิน: `ForeignServiceVat.PayeeAmount` / `PayeeAmountQuery` · ป้าย PDF `DocumentLabels.TotalVatSelfAssessedPp36` / `TotalPayeeAmount`
- ค้าง: ด่าน JE-NO-COUNTERPART (ทีม F1) ต้องเทียบขาเงิน/เจ้าหนี้กับ `PayeeAmount(doc, sourceOwnsPp36)` (PV ปิดหนี้ใบต่างประเทศ Cr ธนาคาร 5,908 ≠ TotalAmount 6,321.56) · E-7 ด่านนำส่งใช้ชั้นงวด (TODO F3: ต่อใบ)
