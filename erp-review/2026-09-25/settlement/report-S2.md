# ทีม S2 (รอบ 198) — ระบบวันนี้รองรับ settlement แค่ไหน · สถาปัตยกรรม · แผนเฟส

> subagent เขียนไฟล์ไม่ได้ — main agent บันทึกจากรายงานที่ส่งกลับ · อ่านโค้ดที่ HEAD aa04d91c · รูปแบบไฟล์/API ของ Omise/Shopee/Lazada/Agoda มาจากความรู้ทั่วไป ยังไม่ยืนยันกับของจริง
> **main agent ยืนยันเองแล้ว**: G-8 (`PaymentGatewayController` มีแค่ `[Authorize]` ระดับคลาส · refund เรียก provider คืนเงินจริง) · I-1 (`IntegrationService.cs:2676-2689` prefix "112" + `StartsWith` ไม่เรียง · หาไม่เจอ `return null`)

## 0. คำตอบสั้น
1. gateway ของเราเอง (Omise ผ่าน `PaymentIntent`) มีโครงแล้วแต่ลงบัญชีไม่ครบ — คืนเงินไม่มี JE · คืนบางส่วนไม่เข้า settlement · ค่าธรรมเนียมรวม VAT ลงค่าใช้จ่ายทั้งก้อน · WHT ลง JE แต่ไม่ออก 50 ทวิ · ไม่ตรวจงวดปิด · ข้อความบล็อกบอกทางแก้ที่ไม่มี · refund/settlement ไม่มีด่านสิทธิ์
2. marketplace / OTA ยังไม่มีอะไรรองรับ — มีแค่เครื่องมือให้ประกอบเอง (`Payment.FeeAmount` · `PaymentSettlementAdjustment` ฝั่งซื้อ · สร้าง JE จากรายการธนาคาร) ลงบัญชีคนละแบบ · ไม่มีนำเข้า settlement · ไม่มีบัญชีพักต่อช่องทาง
3. ทางเข้าเก่าลงเงินบัตร/QR ผิดผังวันนี้: Integration ลงหมวด "เงินลงทุนชั่วคราว" · POS ที่ไม่ผูกบัญชี QR ลงผังเดียวกัน บัตรลง 113xx ตัวไหนก็ได้
4. เสนอชั้นเดียว **Settlement Batch** (1 รอบโอน = 1 batch · บรรทัดตามประเภท): ค่าธรรมเนียมเป็น**เอกสารซื้อ** (ภ.พ.30/36 · 50 ทวิ · §65 ตรี เดินเส้นเดิม) · เงินเข้าธนาคารจริงเป็นตัวตั้ง ผูก `BankTransaction` · `JournalEntryBuilder` ผู้เรียกจริงรายแรก + ด่านงวด (ราก ERP_REVIEW §6 ข้อ 1)
5. เฟส 0 + เฟส 1 (CSV จับคู่คอลัมน์เอง) พอลงบัญชี marketplace/gateway ได้ถูกครบ · adapter เฉพาะเจ้าต้องรอไฟล์จริง

## 1. แผนที่สิ่งที่มีอยู่
| ชิ้น | ที่ | ผู้เรียก | ข้อจำกัด |
|---|---|---|---|
| `IPaymentProvider` Omise/ManualSlip | `IPaymentProvider.cs:128` · `OmisePaymentProvider.cs:157-158` | ✅ | อ่าน `fee` อย่างเดียว · ไม่มี `ListSettlementsAsync` |
| `PaymentIntent`/`PaymentProviderConfig` | `Models/Entities/Payments.cs:52-59,70-110` | ✅ | `FeeEstimated` (:104) ไม่มีใครเขียน · `ExpectedFeePercentByMethodJson` (:55) ไม่มีใครอ่าน · ไม่มี `RefundedAmount`/สถานะ Dispute |
| `IGatewayAccountResolver` | `GatewayAccountResolver.cs:51-93` | ✅ 10 จุด | ดี ใช้ต่อได้ |
| handler 6 ชนิด | `Services/Payments/Handlers/*` | ✅ | ส่ง `moneyInAccountId` เข้า `OverridePaymentAccountId` |
| `GatewaySettlementService`/`Math` | `GatewaySettlementService.cs:64-278` · `GatewaySettlementMath.cs:96-150` | ✅ Math 13 เทสต์ · Service 0 | เลือกรายการตาม `ConfirmedAt` ไม่ใช่รอบโอนจริง |
| `GatewayReconciliation` | `PaymentGatewayController.cs:113-138` | ✅ 7 เทสต์ | รู้แค่คืนเต็มยอด |
| หน้าเว็บ | `payment-intents/settlements/settings.html` | ✅ | ไม่มีช่องแก้ค่าธรรมเนียมรายรายการ |
| `Payment.FeeAmount` (ขาย) | `Payment.cs:86-93` · `DocumentService.cs:16893-16908` · `documents.html:11456` | ✅ | ผังสำรอง `53200` ไม่มีในผัง ⇒ ตกผังแรกที่ชื่อมี "ค่าธรรมเนียม" ไม่เรียง · หน้าไม่ส่ง `feeAccountId` · ไม่แยก VAT · ไม่มี WHT |
| `Payment.GatewayFeeAmount` | `Payment.cs:22` | ❌ ไม่มีผู้อ่าน/เขียน | ซ้ำความหมาย |
| `PaymentSettlementAdjustment` | `DocumentService.cs:12041-12109` | ✅ | ฝั่งซื้อเท่านั้น (:12053 throw ฝั่งขาย) |
| BankFeed + จับคู่ | `BankService.cs:734-860` · `Bank/BankFlowClassifier.cs:22-59` | ✅ | รู้จัก memo shopee/lazada/omise/stripe = Aggregator · agoda/booking/expedia = OtaSettlement แต่ไม่แปลงเป็น settlement |
| `BankFeeDictionary` | `Bank/BankFeeDictionary.cs` | ✅ | ตั้งชื่อส่วนต่าง ไม่สร้าง JE |
| Import | `ImportExportService.cs:2390-2436` | ✅ | 15 ชนิด ไม่มี settlement · มี `AiFeatureKey.ImportColumnMatch=26` |
| `ECommerceService` | `ECommerceService.cs:17-20,96-170` | ⚠️ ไม่มีหน้าเว็บ | ดึงออเดอร์ไม่ลงลายเซ็น · Draft TaxInvoice ต่อออเดอร์ · ไม่มีค่าธรรมเนียม — ต่อสาย/ลบ รอเจ้าของ |
| Integration ขาเข้า | `IntegrationService.cs:1185-1300 · 2676-2689` | ✅ | ไม่มีช่องค่าธรรมเนียม · I-1 |
| POS | `PosService.Orders.cs:1656-1670` (gateway→11340 ✅) · `:2237-2256` (EDC/QR) | ✅ | EDC ไม่มีบัญชีพัก/MDR · P-1 |
| ที่พัก OTA | `AllEnums.cs:2278-2286` "Ota (บันทึกมือ)" · `Lodging.cs:345-346` | ⚠️ ป้ายชื่อ | ไม่มีคู่ค้า/ค่าคอม/โมเดล · ผัง 41140/52150 ไม่มีผู้อ้าง |
| ภ.พ.36/ภ.ง.ด.54 | `StatutoryRemittanceService.cs:39-45,306-325` · `ForeignServiceEvidence.cs` | ✅ | เกิดจากเอกสารซื้อ `IsForeignService` เท่านั้น |
| ชั้น posting | `Journal/JournalEntryBuilder.cs:53` | ❌ ไม่มีผู้เรียก | ด่านงวด X-9 อยู่แค่ `AccountingService` |
| กระทบบัญชีย่อย | `SubLedgerReconciliationService.cs:53-82` | ✅ | นับ 11340 เป็นลูกหนี้การค้า ⇒ ส่วนต่างปลอม (S-1) |

## 2. ช่องโหว่
### 2.1 เส้น gateway (แก้ก่อนต่อยอด)
- **G-1 P0** คืนเงินไม่มี JE (`PaymentGatewayController.cs:221-236` · `PaymentIntentService.cs:316-317`) ⇒ 11340 พอง · ใบลดหนี้ทีหลังทำลูกหนี้ติดลบ
- **G-2 P0** คืนบางส่วนไม่เข้า settlement (กรอง `Succeeded` :201 · ไม่มี `RefundedAmount`) ⇒ บล็อกถาวร
- **G-3 P1** ค่าธรรมเนียมรวม VAT ลงค่าใช้จ่าย (`GatewaySettlementMath.cs:107-141`) ⇒ เสียภาษีซื้อ · OCR ใบค่าธรรมเนียมเป็น PI = นับซ้ำ
- **G-4 P1** ฐาน WHT รวม VAT (:111-113) + ไม่ออก 50 ทวิ ⇒ 21917 ไม่เคยถูกยื่น
- **G-5 P1** ไม่ตรวจงวดปิด (`GatewaySettlementService.cs:110-112` · `new JournalEntry`)
- **G-6 P1** ข้อความบล็อกบอกให้แก้ค่าธรรมเนียมแต่ไม่มีที่แก้ (`FeeEstimated`=0 เสมอ) — ผิด F2 ข้อ 8
- **G-7 P1** อ่าน `fee` อย่างเดียว (ยืนยันกับ docs Omise)
- **G-8 P0** refund / confirm-manually / settlements ไม่มีด่านสิทธิ์ (`:22,156,189,328`) · ไม่อยู่ใน WATCHED (`write_permission_gate_check.py:30-70`)
- **G-9 P2** doc ไม่ตรง (`DOCUMENT_FLOW.md:1083` · `PAYMENT_GATEWAY_DESIGN.md` §0 ขัด §7.1)
### 2.2 ต่อกรณี
gateway ของเรา ⚠️ · gateway ที่ลูกค้าเปิดเองนอกระบบ ❌ (ไม่มีบัญชีพัก · Dr ธนาคารยอดสุทธิทีละใบ) · marketplace ❌ · OTA merchant/VCC ❌ · OTA agency ⚠️ (PI + `IsForeignService` มือ) ·
คืนเงิน ⚠️/❌ · chargeback ❌ · reserve ❌ · ยอดติดลบ ❌ (`GatewaySettlementMath.cs:117-121` บล็อก) · FX ⚠️ (settlement THB อย่างเดียว) · OCR ใบค่าธรรมเนียม อ่านได้/ผูกไม่ได้ ·
กระทบยอดเงินเข้า ⚠️ (settlement JE ไม่ผูกบรรทัดธนาคาร · create-je ซ้ำได้) · tax point marketplace/OTA ยังไม่มีกติกา
### 2.3 บั๊กทางเข้าเก่า (ใหม่)
- **I-1** `IntegrationService.cs:2679-2689` banktransfer/promptpay/creditcard ⇒ "112" = **เงินลงทุนชั่วคราว** (`ChartOfAccountTemplates.cs:31-32`) · ลูกหนี้ `StartsWith("113")` ไม่เรียง · หาไม่เจอ `return null` เงียบ (ใบตัดชำระแต่ไม่มี JE) · ต้อง migration ซ่อม JE เก่า
- **P-1** `PosService.Orders.cs:2237-2256` ผังสำรอง 1011/1012/1131 ไม่มี ⇒ QR/e-Wallet → 11200 · บัตร → 113xx ตัวไหนก็ได้
- **F-1** `DocumentService.cs:16901-16904` ผังสำรองค่าธรรมเนียมสุ่ม · **S-1** `SubLedgerReconciliationService.cs:53-82`

## 3. สถาปัตยกรรม
หลัก: หน่วยความจริง = รอบโอนของผู้ให้บริการ · ยอดเข้าธนาคารจริงเป็นตัวตั้ง (มีบรรทัด reserve/adjustment/FX ให้เลือกแทนบล็อกตัน) · **ค่าธรรมเนียม = เอกสารซื้อ 1 ใบ/batch/กลุ่มภาษี** จ่ายด้วยบัญชีพักผ่าน
`CreatePaymentAsync(OverridePaymentAccountId)` (ใบกำกับที่ OCR ทีหลังแนบเข้าใบของ batch — ปิดนับซ้ำ) · ขาขายใช้รับชำระเดิม · batch ผูก `BankTransactionId` · JE ผ่าน `JournalEntryBuilder` (Dr=Cr · ด่านงวด · AwayFromZero) ·
`GatewaySettlementService` → ตัวสร้าง batch จาก `PaymentIntent`
```
SettlementChannel: Kind{Gateway,Marketplace,Ota,CardAcquirer,Delivery,Other}·DisplayName·AdapterCode·ColumnMapJson·CounterpartyContactId(ContactTaxBranchKey)
  ·PaymentProviderConfigId?·ClearingAccountId(1134x ต่อช่องทาง)·ReserveAccountId?·DisputeAccountId?·FeeAccountMapJson·FeeVatMode{ThaiVat7,ForeignPp36,None}
  ·FeeWhtMode{None,Withhold3Percent}·RevenueModel{GrossWithFees,NetRate}·Currency·IsActive
SettlementBatch: ChannelId·PayoutRef(unique/channel)·PeriodFrom/To·PayoutDate·Currency·FxRate?·Opening/ClosingWalletBalance·NetPayout(ตัวตั้ง)
  ·Status{Imported,Classified,Matched,Posted,BankMatched,Voided}·SourceKind{CsvImport,PaymentIntents,Manual,Api}·SourceFileAttachmentId·BankAccountId·BankTransactionId?
  ·PayoutJournalEntryId?·FeeDocumentIdsJson
SettlementLine: BatchId·Seq·LineType·Description(ตัด PII)·ExternalOrderId·ExternalTxnId(unique/channel)·Amount(มีเครื่องหมาย)·VatAmount?·WhtAmount?
  ·MatchedDocumentId?/PaymentIntentId?/PaymentId?/ReservationId?·MatchStatus·ClassifiedBy{AdapterRule,Learned,Ai,User}·ClassifyAiFeedbackId?·ClassifyUsedAi
LineType: Sale·Refund·Chargeback·ChargebackReversal·Commission·PaymentFee·ShippingFeeCharged·ShippingSubsidy·SellerVoucher·PlatformVoucherSubsidy·AdsFee
  ·ServiceFee·WithdrawalFee·ReserveHold·ReserveRelease·TaxWithheldByPlatform·FxDifference·Adjustment(บังคับเหตุผล)·Unclassified(ห้ามลงบัญชี)
PaymentIntent + RefundedAmount · SettlementBatchId?
```
- Helpers: `SettlementLineTypeRules` (ตารางเดียว) · `SettlementBatchMath.Plan` (Σ บรรทัด = NetPayout + (Closing − Opening) ±0.01 · คืนแผน posting ครบ) · `SettlementFeeTax` (VAT ×100/107 · WHT ฐานก่อน VAT)
- Adapter: `ISettlementReportAdapter{Code; Detect; Parse}` ใน `Services/Settlement/Adapters/**` · `GenericColumnMapAdapter` (จำ `ColumnMapJson` · `ImportColumnMatch` ช่วย) · `PaymentIntentAdapter` · เฉพาะเจ้าเมื่อมีไฟล์จริง (golden fixture ตัด PII)
- AI (กฎเหล็ก #1) เฉพาะจำแนกประเภทบรรทัด: seed → `GenericFeedbackDistillationModel(AiFeatureKey.SettlementLineClassify)` → orchestrator · guard ในชุด enum · kill-switch ⇒ Unclassified · ห้าม AI สร้างยอด/จับคู่ยอด
- สิทธิ์ `settlement.import`/`settlement.post`/`payment.refund` + `[RejectApiKey]` · WATCHED · tenant · `AdvisoryLockKey` ต่อช่องทาง · idempotent `ExternalTxnId` · ตัด PII · ไฟล์ต้นฉบับผ่าน `IAttachmentAccessGate` · retention 5 ปี · `TaxFilingLockPolicy`
- รายงาน: บัญชีย่อยบัญชีพักต่อช่องทาง · แก้ S-1 · วิดเจ็ตค่าธรรมเนียม

## 4. แผนเฟส
- **เฟส 0 (แก้ด่วน)**: G-8 ด่านสิทธิ์ + WATCHED · G-1 JE คืนเงิน + `RefundedAmount` · G-2 · G-5 · G-6 endpoint แก้ `FeeActual` (เหตุผล+audit) · I-1 (`LinkedAccountId` ห้าม prefix 112 · หาไม่เจอล้มดัง · migration ซ่อม JE เก่ารออนุมัติ) · P-1 · F-1 · G-9
- **เฟส 1 (CSV · 4 ทีม)**: A แกนข้อมูล+คณิต (`Models/Entities/Settlement.cs` · `SettlementEnums.cs` · Helpers · migration บล็อกเดียว) · B นำเข้า+adapter (`Services/Settlement/Adapters/**` · `SettlementImportService` · boundary checker · distillation) ·
  C ลงบัญชี (`SettlementPostingService` · `JournalEntryBuilder` ด่านงวด · ห้าม `new JournalEntry` ใน `Services/Settlement/**`) · D หน้าจอ+controller (`SettlementController` · `settlements.html` · `settlement-channels.html` · api.js · PermissionKeys · WATCHED)
- เฟส 2 รวม gateway เข้า batch · `fee_vat` · เฟส 3 adapter เฉพาะเจ้า + OCR ใบค่าธรรมเนียมแนบ batch · เฟส 4 OTA · เฟส 5 chargeback/reserve/กระทบยอดบัญชีพัก · เฟส 6 ดึงออเดอร์ marketplace
- checker ใหม่: `settlement_adapter_boundary_check` · `settlement_line_type_rules_check` · required_call_site · WATCHED · `money_account_prefix_check` (แคบ: Integration/POS)

## 5. ให้เจ้าของตัดสิน (ข้อ 1,2,3,9 ก่อนเฟส 1)
1 gross/net (marketplace ค่าเริ่มต้น gross · OTA merchant net vs gross+ค่าคอม) · 2 ใบกำกับขายของออเดอร์ marketplace (เต็มรูป/อย่างย่อ ภ.พ.06/สรุปรายวัน · tax point) ·
3 บรรทัดขายที่จับคู่ใบขายไม่ได้: บล็อก หรือสร้างเอกสารสรุปอัตโนมัติ · 4 WHT ค่าธรรมเนียมค่าเริ่มต้น · 5 ค่าคอม OTA ต่างประเทศ ภ.พ.36 ทุกเจ้า · ภ.ง.ด.54 รอตาราง DTA ·
6 chargeback · 7 ผังพัก 11340+มิติ vs ผังย่อย (เสนอผังย่อย) · ผัง EDC · 8 Channel entity ใหม่ (เสนอ) · 9 ไฟล์ตัวอย่างจริง (ตัด PII) · 10 ชะตา `ECommerceService` + `GatewayFeeAmount` · 11 ขอบเขต migration ซ่อม JE I-1/P-1
ความเสี่ยง: นับซ้ำใบค่าธรรมเนียม vs OCR · แพลตฟอร์มเปลี่ยนรูปไฟล์ (Detect ล้มดัง) · ไฟล์ใหญ่ (stream + batch insert + ล็อก) · ลงครึ่งทาง (ธุรกรรมเดียว) · DocumentService บวม (เรียกผ่าน interface) · ด่านเข้มขึ้นเปลี่ยนตัวเลขผู้ใช้
