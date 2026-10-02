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
| P0-1 ✅ | `JournalPostingGuard` กฎ JE-NO-COUNTERPART เทียบขาเครดิตที่ไม่ใช่ภาษี (ธนาคาร 5,908) กับ `TotalAmount` 6,321.56 และนับ 21912 เป็นบัญชีภาษีขาย ⇒ **JE §83/6 ถูกปฏิเสธทุกใบ** (PV ตรง · PI/Expense · ใบค่าธรรมเนียมรอบโอน OTA) · ตัวสแกน `JournalAnomalyService:128-135` ใช้กฎเดียวกัน | `Services/JournalPostingGuard.cs:47,134-143` · โยนที่ `DocumentService.cs:~16878` | ✅ 8da202b8 ทีม F1 (ธง `DocFacts.IsForeignService` · `SplitCredit` · `JE-PP36-UNFLAGGED`) |
| P0-2 ✅ | อนุมัติล้มแต่ค่าที่แก้ค้างใน DbContext (เลขเอกสาร · Status=Paid · InputVatPostedAsUndue · TaxPointDate) ถูกบันทึกโดย SaveChanges ที่ตามมา ⇒ **ใบ "อนุมัติแล้ว" ไม่มี JE** · `ApproveDocumentAsync` rollback ธุรกรรมแต่ไม่ถอย tracked entity (`:6503-6507`) · ผู้เรียกที่กลืน error แล้ว save ต่อ: `CreateDocumentAsync` auto-approve PV เงินสด (`:1648` LogInformation "staying Draft" ไม่จริง) → `AuditMiddleware:106` · `ApprovalService.TryFinalizeApprovedEntityAsync :452-480` (Console.Error) · `DocumentController.BulkApprove :1525` · `OcrController :455/467` · `RecurringTransactionService :528` · `SettlementPostingService :381` · (LINE `LineBotService:364/830` ต้องยืนยัน) · บั๊กคลาสเดียวกับรอบ 194 ที่ `CmsBookingService:585-600` แก้ด้วย `TrackedChangeRevert` เฉพาะผู้เรียกตัวเดียว | `DocumentService.cs:1626-1652, 6503-6507` · `Middleware/AuditMiddleware.cs:106-107` · `RevertTrackedChangesSinceAsync :4138` | ✅ 8da202b8 ทีม F1 (ถอยในตัวอนุมัติ · ผู้เรียกล้มดัง 8 จุด · LINE ยืนยันแล้ว + แก้) |
| P0-3 | integration `CreatePaymentVoucherJournalAsync` คืน null + LogWarning ขณะที่ใบถูกสร้าง Approved ⇒ ใบไม่มี JE เงียบ (บั๊กทั่วไป) · API v1/integration ไม่มีช่อง `IsForeignService` | `IntegrationService.cs:3998,4018,4082,4089` | ✅ 8da202b8 ทีม F1 (ธุรกรรมเดียว · โยน `INTEGRATION-PV-NO-JE` · ช่อง `IsForeignService` ใน API ยังไม่มี) |
| E-1 ✅ cc840773 | ยอดที่ต้องจ่าย/ค้าง/จ่ายแล้วของใบต่างประเทศรวม VAT ประเมินเอง (`TotalAmount`/`BalanceDue`/`PaidAmount` = 6,321.56) ขณะ GL ตั้งเจ้าหนี้/จ่ายแค่ 5,908 ⇒ ยอดค้างปลอม 413.56 ในอายุหนี้ หรือเจ้าหนี้ติดเดบิต + เงินออกเกิน · PDF สอง renderer พิมพ์ "ยอดรวมสุทธิ 6,321.56" + ตัวอักษร · จับคู่ธนาคารเสนอ `TotalAmount` | `DocumentService.cs:1592,1599,1604` · `CreatePaymentAsync ~12390` · `AgingReportService.cs:70,112` · `BankService.Reconciliation.cs:877,890` · `BankService.MatchResolution.cs:158` · `PdfGenerationService.cs:2223-2251` · `DocumentRenderer.cs:923-952` (ฝัง "ภาษีมูลค่าเพิ่ม 7%" ตรง — drift กับ `L.TotalVat`) | ทีม F2 (คำตัดสิน 131) |
| E-1b ✅ cc840773 | แปลงใบซื้อ→ใบสำคัญจ่ายไม่ส่งธง `IsForeignService` + เส้น PV ที่ปิดหนี้ใบต้นทางไม่รู้จัก §83/6 (Dr เจ้าหนี้ 6,321.56) · คำเตือน `RD-83/6-UNFLAGGED` สั่งให้ติ๊กธงบนใบลูก ⇒ ภ.พ.36 นับซ้ำ · `ReclassifyForeignServiceAsync` ไม่กันใบที่มีใบต้นทาง | `ConvertCoreAsync :10974-11050` · `:16517-16594` · `:18966-18979` · `:7303-7417` | ทีม F2 |

## P1/P2
| ID | ปัญหา | หลักฐาน | ทีม |
|---|---|---|---|
| ✅ 873f9ad4 E-2 | เดือนเคลมภาษีซื้อ ภ.พ.36 สองกติกา: `RecognizePp36InputVatAsync` ใช้ `SupplierTaxInvoiceDate ?? PaymentDate` (+ หน้าเว็บติด "(แนะนำ)") ขัด `TaxService.ClaimBasisDate :3320` ⇒ คำตัดสิน 129: วันใบเสร็จ RD · JE รับรู้ลงวันนั้น · ห้ามเคลมเข้างวดที่ยื่น ภ.พ.30 แล้ว · อ้างมาตรา §77/2 → §82/4 + ใบเสร็จ RD | `StatutoryRemittanceService.cs:1250-1383 (1312-1313)` · `tax-remittance.html:706-712` · DOCUMENT_FLOW §6.2g | F3 |
| ✅ 873f9ad4 E-3 | ยอดค้าง/นำส่ง/รับรู้/รายงาน ภ.พ.36 คัดจากธง+สถานะเอกสาร ไม่ดูว่ามี JE (Cr 21912 / Dr 11640) จริง · รับรู้ย้าย `VatAmount` ทั้งก้อน (บริษัทไม่จด VAT · บรรทัดภาษีซื้อต้องห้าม) | `StatutoryRemittanceService.cs:316-341, 1288-1300` · `TaxService.GeneratePp36Report :1564-1640` | F3 |
| ✅ 873f9ad4 E-4 | สกุลเงินต่างประเทศ: ยอดค้าง/รายงาน/รับรู้ ใช้ยอดสกุลเอกสาร ไม่แปลงบาท (หน้า undue-vat แก้แล้วด้วย `ToGlAmount` `DocumentService.cs:5567-5571`) | `TaxService.cs:1617-1630` | F3 |
| ✅ 873f9ad4 E-5 | ใบที่อนุมัติหลังนำส่งงวดนั้น: นำส่งเพิ่มไม่ได้ (ด่านกันซ้ำ `:992-998`) แต่ถูกรับรู้ไปเคลม ⇒ คำตัดสิน 133 | `StatutoryRemittanceService.cs:992-998` | F3 |
| ✅ 873f9ad4 E-6 | ยกเลิก/ปลดธงหลังนำส่ง/รับรู้ไม่ถูกบล็อก (ด่านดู `FilingLockedAt` แต่รายงาน ภ.พ.36 ที่สร้างตอนนำส่งเป็นร่าง) · JE รับรู้ไม่มี `SourceDocumentId` ไม่ถูกกลับ ⇒ คำตัดสิน 134 · ยอดค้างติดลบ `continue` เงียบ `:334` | `DocumentService.cs:8095-8160, 8577` | F3 |
| E-7 ✅ cc840773 | ใบลดหนี้ฝั่งซื้อของใบต่างประเทศ: Dr เจ้าหนี้ยอดรวม VAT · ไม่กลับ 21912 · รายงาน/ยอดค้างไม่หักใบลดหนี้ | `:15528-15541, 15612-15652` | F2 |
| ✅ 873f9ad4 E-8 | ชุดชนิดเอกสารที่นับ ภ.พ.36 ต่างกันสามเส้น (ยอดค้าง/ปฏิทิน `:635`/รายงาน) · ฝั่งสร้างตั้งธงบนใบขาย/CIL ได้ | `:1497, 2632` | F3 (ชุดชนิดตัวเดียว) |
| ✅ 873f9ad4 E-9 | ภ.พ.36 ช้าไม่มีเงินเพิ่ม ⇒ คำตัดสิน 135 | `StatutoryRemittanceService.cs:1021-1023` | F3 |
| ✅ 873f9ad4 E-10 | กระทบยอดภาษีกับ GL: เดือนนำส่งฟ้องผลต่าง −413.56 ปลอม · ไม่มีบรรทัด 11640 · ไม่มีสาเหตุ "ใบอนุมัติแล้วไม่มี JE" | `TaxGlReconciliationService.cs:95-100, 143-160` | F3 |
| ✅ 873f9ad4 T-2 | ชื่อผัง 21912 ⇒ คำตัดสิน 132 (migration เฉพาะชื่อเดิมทุกตัวอักษร · ข้อความสำรอง `PdfGenerationService.cs:835` · error `DocumentService.cs:15915,16648`) | `ChartOfAccountTemplates.cs:153` | F3 |
| T-4b ✅ cc840773 | PV/ใบซื้อผู้รับต่างประเทศไม่หัก WHT + ไม่จำแนกเงินได้ ⇒ เงียบ (รอบโอนตั้ง 40(2) 15%) ⇒ คำตัดสิน 130 เตือน | `DocumentService.cs:19081-19093` · `SettlementWhtIncomeType` · `SettlementFeeTax.cs:121-125` | F2 |
| ✅ 873f9ad4 T-3d / C-P2 | ป้ายรายการ "ภ.พ.36 รอรับรู้"/"เคลม ภ.พ.30" ตัดสินใน JS จากธง ไม่ดู GL · หลังรับรู้ใบต่างประเทศไม่มีป้าย · "เคลม ภ.พ.30" ขึ้นกับใบ WaitingApproval | `documents.html:7530-7548` | F3 (server computes) |
| E-12 ✅ cc840773 | §65 ตรี (11)(18) เตือนทุกผู้ขายต่างประเทศที่ไม่มีเลขภาษีไทย · (19) ไม่เคยทำงาน + จะบวกกลับยอดรวม VAT | `Section65TerValidator.cs:96-113,157` | F2 |
| ✅ 873f9ad4 E-13 | บรรทัดใบซื้อต่างประเทศใน ภ.พ.30 ขึ้นเหตุผล "[รอใบกำกับ §82/3]" ผิด | `TaxService.cs:851-859` | F3 |
| C-P2 ✅ cc840773 | `DescribeMissingJournalAsync` ไม่แยกกรณี JE หักล้างเป็นศูนย์ · QuestPDF ฝัง "ภาษีมูลค่าเพิ่ม 7%" | `PdfGenerationService.cs:531-547` · `DocumentRenderer.cs:924` | F2 |

## ซ่อมข้อมูลเดิม
✅ 8da202b8 (ทีม F1 · `GET/POST missing-journal[/repair]` · `Helpers/MissingJournalRepair`) ใบที่อนุมัติแล้วแต่ไม่มี JE (รวม PV-20260901-0001): หลัง P0-1/P0-2 ขึ้น ⇒ เครื่องมือ "ลงบัญชีให้ใบที่อนุมัติแล้วแต่ไม่มี JE" (ทีม F1) ผ่าน AutoPost ตัวเดียว · งวดปิด/นำส่งแล้ว ⇒ ปฏิเสธพร้อมทางไปต่อ ·
ตัวสแกน DOC-NO-JE (`JournalAnomalyService.cs:145-170`) คำแนะนำ "ยกเลิกแล้วอนุมัติใหม่" ต้องชี้เครื่องมือนี้แทน ·
ใบต่างประเทศที่ BalanceDue รวม VAT ⇒ migration ตามคำตัดสิน 131 (ทีม F2) ✅ cc840773 (`Helpers/ForeignServicePayeeBalanceMigration` — แถวสูตรเดิมเท่านั้น · ใบที่จ่ายเกินยอดจ่ายผู้รับเงินแล้วไม่แตะ + log ให้คนซ่อม) · **ระหว่างนี้ผู้ใช้อย่ากดนำส่ง/รับรู้ ภ.พ.36 ของ PV-20260901-0001**

## ฝ่ายค้านบนงานทีม F1 (main agent ยืนยัน P1-A/P1-B) — ทีม F1 รอบสอง
| ID | ปัญหา | สถานะ |
|---|---|---|
| P1-A | เครื่องมือซ่อมลงแค่ AutoPost แต่ข้ามผลข้างเคียงหลังอนุมัติ (ปรับใบต้นทาง · undue VAT ขาย · 50 ทวิ · สต็อก · supersede · ทะเบียนสินทรัพย์) | ✅ 5e797b83 ทางปลอดภัย: `MissingJournalRepair.SideEffectsOf` ⇒ ปฏิเสธใบที่มีใบต้นทาง · WHT · บรรทัดสินค้า · ผัง 12xxx · ใบแทน · โครงการ · มัดจำ พร้อมทางไปต่อ · ใบเดี่ยว (PV-20260901-0001) ยังซ่อมได้ · ข้อเสนอ: แยก "ผลข้างเคียงหลัง AutoPost" เป็นเมธอดกลางที่อนุมัติ+ซ่อมเรียกร่วม (รอบหน้า — ต้องทำให้ทุกขั้น idempotent ก่อน) |
| P1-B | ข้อความล้มมีแค่สองสถานะ — ใบยกเลิก/ปฏิเสธได้ "อนุมัติและลงบัญชีแล้ว" + ถูกเขียนหมายเหตุ · BulkApprove นับผิด | ✅ 5e797b83 `AutoApproveFailureKind` (ร่าง · มีผล · มีผลแต่ไม่มี JE · ปิด) · ไม่เขียนหมายเหตุใบปิด · BulkApprove ตรวจสถานะก่อนเรียก + นับเฉพาะที่อนุมัติจริงรอบนี้ · FinalizeError ตัวเดียวกัน |
| P2-1 | `ReapplyPending` คืนทั้งแถวจาก snapshot ⇒ ทับค่าที่คนอื่นแก้ในฐาน | ✅ 5e797b83 เก็บ/คืนเฉพาะช่อง IsModified (Added = ทุกช่องไม่ใช่คีย์) · เทสต์ออฟไลน์ + Db |
| P2-3 | `RecordAutoApproveFailureAsync` โยนได้จากใน catch ของผู้เรียก | ✅ 5e797b83 try/catch ภายใน · ข้อความสำรอง + LogError |
| P2-4 | integration expense/CIL ทิ้งใบ Approved ไม่มี JE · CMS · แพลตฟอร์ม เงียบ | ✅ 5e797b83 expense + CIL: `RunAtomicCreateAsync` + `PostMappingJournalOrThrowAsync` (ธุรกรรมเดียว · `INTEGRATION-NO-JE`) · CMS `ConfirmPaymentAsync` + แพลตฟอร์ม 3 เมธอด ⇒ หมายเหตุบนเอกสาร · **ไม่ทำ**: invoice/CN/DN (ใบขายที่ออกถึงลูกค้าแล้ว — rollback ⇒ ภาษีขายหายจาก ภ.พ.30 · คงเส้น `PostMappingJournalAsync` เดิมที่ดัง 3 ที่ · ต้องเจ้าของตัดสิน) |
| P2-5 | ตัวสแกน DOC-NO-JE คนละเงื่อนไขกับเครื่องมือซ่อม · ปุ่มตาม ruleCode | ✅ 5e797b83 `ExpectsLiveJournal` + JE หลักที่มีผล + ชุดชนิดจาก `DocumentJournalExpectation.PostingTypes` (ลบสำเนา `JePostingTypes`) · accountant.html ปุ่มจาก `canRepair` ของเซิร์ฟเวอร์ |
| P2-6 | อนุมัติสำเร็จภายหลัง หมายเหตุล้มค้าง | ✅ 5e797b83 `AutoApproveFailure.MarkResolved` ต่อท้าย "✅ แก้แล้ว" ในธุรกรรมอนุมัติ |
| P2-7 | PV ที่ชำระใบต้นทางเจ้าของ ภ.พ.36 ⇒ Cr 21912 ต้องเป็น 0 | ❌ ไม่ได้ทำ — ต้องใช้ `ForeignServiceVat.PayeeAmount(doc, sourceOwnsPp36)` ของทีม F2 แต่ merge branch ของ F2 เข้า worktree ถูกปฏิเสธโดยระบบสิทธิ์ ⇒ ทำหลัง F2 เข้า branch หลัก |
| P2-8 | `ReclassifyForeignServiceAsync` ไม่ล็อกแถว ⇒ JE คู่ | ✅ 5e797b83 FOR UPDATE + ตัดสินซ้ำใต้ล็อก |

## ฝ่ายค้านบน merge F2+F3 (6a1baa8a) — ทีม F3 รอบสอง
| ID | ปัญหา | สถานะ |
|---|---|---|
| P1-2 | ใบลด/เพิ่มหนี้ของใบเจ้าของขยับ 21912/11640 ด้วย JE ของตัวเอง แต่ `Pp36Ledger` อ่านเฉพาะ JE ใบเจ้าของ ⇒ นำส่ง/รับรู้/ภ.พ.30 เพี้ยนเท่า VAT ของใบลดหนี้ | ✅ 15a29829 รวม JE ใบลด/เพิ่มหนี้เข้าใบเจ้าของ · ฐานรายงานปรับ · บล็อกยกเลิกใบลดหนี้หลังนำส่ง |
| P1-3 | เครื่องมือซ่อมตัดสินระดับงวด ⇒ ใบที่ไม่เคยถูกนับซ่อมไม่ได้ | ✅ 15a29829 ต่อใบ (`RemittedStatusAsync` + `OwnsPp36`) |
| P2-1 | backfill ใช้วันสร้างใบ ⇒ ใบร่างที่อนุมัติหลังนำส่งถูกผูก · รับรู้รายการที่จ่ายขาดได้ | ✅ 15a29829 เวลาสร้าง JE หลัก · `Pp36RemittanceBackfill` + เทสต์ · บล็อกรับรู้รายการจ่ายขาด |
| P2-2 | วันที่ใบเสร็จตกไปวันจ่าย · วันเคลมไม่มีเพดาน §82/3 | ✅ 15a29829 บังคับวันที่ · `EvaluateClaimPeriod` |
| P2-3 | ภ.พ.30 ใช้ยอดสกุลเอกสาร | ✅ 15a29829 `DocumentFx.ToBaht` ทุกบรรทัด ภ.พ.30 |
| P2-6 | ใบซื้อเครดิตจ่ายเดือนถัดไป งวดผิด (คำตัดสินข้อ 137) | ✅ 15a29829 `StampFirstPaymentAsync` 5 ทางเข้า · เตือนจ่ายข้ามงวด |

## ตรวจแล้วถูกต้อง (ห้ามรายงานซ้ำ)
ทรง JE §83/6 ใน AutoPost + พรีวิวใช้ `ForeignServiceVat.SplitCredit` ตัวเดียว · 11640 บังคับ · tax point = วันจ่าย · ตารางกำหนดยื่นตัวเดียว (7 / 15 ต.ค.) ·
JE นำส่ง Dr 21912/Cr ธนาคาร · ไม่ประทับ Filed เอง · 21912 ไม่ถูกนับเป็นภาษีขายใน ภ.พ.30/กระทบยอด · งาน §82/3 6 เดือนไม่แตะใบต่างประเทศ · ยกเลิกก่อนนำส่งกลับครบ ·
หน้า 2 ของ PDF ที่หัวซ้ำ = ตั้งใจ · ปุ่มอนุมัติเดี่ยว `/approve` และเส้นลายเซ็นไม่บันทึกค่าค้าง

## ผลทีม F2 (รอบ PP36)
- predicate ที่ตกลงกับทีม F3: `ForeignServiceVat.OwnsPp36(Document)` / `OwnsPp36(type, isForeignService, vat, hasRelatedDocument)` + รูป EF `ForeignServiceVat.OwnsPp36Query` — ใบสำคัญจ่ายที่ปิดหนี้ใบต้นทางไม่ใช่เจ้าของ (ใบลูกสืบทอดธงแล้ว ⇒ นับจากธงตรง ๆ = ซ้ำ)
- ยอดจ่ายผู้รับเงิน: `ForeignServiceVat.PayeeAmount` / `PayeeAmountQuery` · ป้าย PDF `DocumentLabels.TotalVatSelfAssessedPp36` / `TotalPayeeAmount`
- ค้าง: ด่าน JE-NO-COUNTERPART (ทีม F1) ต้องเทียบขาเงิน/เจ้าหนี้กับ `PayeeAmount(doc, sourceOwnsPp36)` (PV ปิดหนี้ใบต่างประเทศ Cr ธนาคาร 5,908 ≠ TotalAmount 6,321.56) · E-7 ด่านนำส่งใช้ชั้นงวด (TODO F3: ต่อใบ)

## ฝ่ายค้าน F2+F3 (merge 6a1baa8a) — ส่วนทีม F2
| ID | ปัญหา | แก้ | สถานะ |
|---|---|---|---|
| P1-1 | ธงบน PV ที่อ้างใบต้นทางไม่ตรง "ใบต้นทางเป็นเจ้าของ ภ.พ.36": JE ใช้ใบต้นทาง แต่ด่าน JE ใช้ธงใบลูก · สร้างสืบทอดเฉพาะ false · แก้ร่างติ๊ก/ปลดได้อิสระ ⇒ (ก) PV ร่างเก่าโดนตีตกข้อความผิด (ข) ปลดธงล้ม (ค) PV จ่ายใบไทย 10,700 ติ๊กแล้วจ่าย 10,000 ค้าง 700 เงียบ | `ForeignServiceVat.LinkedVoucherFlag` (ไม่ระบุ ⇒ ตั้งตามใบต้นทาง + หมายเหตุ · ขัด ⇒ ปฏิเสธ `RD-83/6-LINKED-FLAG`) ทั้งสร้าง/แก้ · `DocFacts.SourceOwnsPp36` + `VatNotPaidToPayee` ในด่าน (AutoPost + ตัวสแกน) · migration PV ยังไม่อนุมัติสองทิศ · เทสต์ ก/ข/ค | ✅ 5985acef |
| P1-2 | DN/CN ฝั่งซื้อของใบเจ้าของ ภ.พ.36 `BalanceDue` 1,070 ขณะ JE เจ้าหนี้ 1,000 | `PayeeAmount(doc, ownsSource)` ตอนสร้าง/แก้ (`VatNotPaidToPayee` ครอบ DebitNote) · migration ใบยังไม่อนุมัติ · (Pp36Ledger รวม CN/DN = ทีม F3) | ✅ 5985acef |
| P2-4 | documents.html ติ๊ก checkbox ที่ซ่อนกลับเป็น true หลัง onDocTypeChange ⇒ ใบร่างเก่าชนิดขาย/CIL บันทึกไม่ได้ | `_hydrateForeignServiceFlag` ติ๊กเฉพาะเมื่อช่องแสดง · ชนิดที่ติ๊กไม่ได้ ⇒ ถอด + แจ้ง · `tools/foreign_flag_hydrate_sim.js` (โค้ดจริง สองทิศ · รุ่นเก่าล้ม) | ✅ 5985acef |
| P2-5 | PV เก่าธง false ที่จ่ายใบเจ้าของ ภ.พ.36: `PaidAmount` ตอนอนุมัติ + PDF | อนุมัติ: ส่งค่าเจ้าของใบต้นทาง (resolver เดียวกับ JE) ✅ · PDF: **ตั้งใจไม่ทำ** — ใบที่ยังไม่อนุมัติได้ธงจาก migration · ใบที่อนุมัติก่อนรอบนี้ GL จ่ายเต็มยอดจริง ⇒ พิมพ์เต็มยอดตรง GL (ส่งค่าใบต้นทางเข้า renderer จะพิมพ์ยอดที่ขัด GL) | ✅ 5985acef (PDF ตั้งใจไม่ทำ) |
