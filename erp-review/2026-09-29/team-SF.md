# รอบ 200 · ทีม SF — แก้ผลฝ่ายค้าน settlement (งานทีม P2 · T · V2)

ขอบเขต: `review200-P2.md` (X-1..X-10) · `review200-T.md` (T-1..T-8) · `review200-V2.md` (V2-C1..C4 · V2-P1..P4) · คำตัดสิน `DECISIONS.md` ข้อ **25 · 26 · 27**
(+ 10 · 15 · 20 ที่เป็นบริบท) · worktree reset ไปที่ `b350edff` ก่อนเริ่ม (tree สะอาด) · คอมมิตโค้ด **c7bad3f5** (sha เติมในคอมมิตตามหลัง) ·
**ยังไม่ได้คอมไพล์** (ไม่มี .NET SDK ในเครื่อง — รบกวน rebuild / อ่านผล CI) · **ไม่แตะ X-2 / `ConfirmedRangeUtc`** (ทีม GF)

## สรุปรายการ

| ID | สถานะ | ที่แก้ (file) | เทสต์ (`Accounting.Tests/SettlementReview200SfTests.cs`) |
|---|---|---|---|
| **X-1** (P1 · ข้อ 26) | ✅ | ด่านลงบัญชี `SettlementPostingService.BuildGateAsync` → `GatewayBatchIntentRules.PostingIssue(ModeMismatch(…))` บล็อก `SettlementPlanIssueCode.GatewayModeMismatch` (37) · นำเข้าไฟล์ `SettlementImportService.ImportFileAsync` · บันทึกค่าตั้ง gateway `PaymentSettingsController.Save` → `ConfigChangeRefusal` (ตรวจเฉพาะเมื่อโหมด VAT/WHT เปลี่ยน · 400 พร้อมชื่อช่องทาง — ปิด B-3 ของทีม P2) · แก้ถ้อยคำ `team-P2.md` §5 "ต่างเป็นสตางค์" (ผิดสำหรับคู่ None↔ThaiVat7 = ต่างเต็ม 7/107) | `X1_ด่านลงบัญชี_โหมดขัด_บล็อก…` · `X1_บันทึกค่าตั้งgateway_…` |
| **X-3** (P2 · ข้อ 26) | ✅ | `Helpers/GatewayBatchIntentRules.ModeMismatch(…, companyVatRegistered)` — "ตรงกัน" = สองเส้นให้ผลภาษีเท่ากัน: ช่องทาง ภ.พ.36 **ไม่ตรงเสมอ** (config ไม่มีโหมดนี้ · ทางไปต่อ = ช่องทางไม่ผูก config + นำเข้าไฟล์) · ไม่หัก ↔ "หักเองแล้วได้คืน" (W2) ไม่ตรง · ไม่หัก ↔ ตัวแทนหัก (W1) ตรง (ไม่มีขา JE/50 ทวิ/ยอดยื่นฝั่งเรา = เท่าเส้นเดิม) · บริษัทไม่จด VAT ⇒ คู่ VAT ไทยทุกคู่ตรง (VAT เป็นค่าใช้จ่ายทั้งก้อนทั้งสองเส้น) · ผู้เรียก 5 จุดส่งสถานะ VAT จาก `CompanyVatStatus.IsRegisteredAsync` | `X3_โหมดต้องให้ผลภาษีเท่ากันทั้งสองเส้น` (Theory 10 — สองทิศ) · `X3_ForeignPp36_…` · แก้ Theory เดิม `SettlementGatewayPhase2Tests` (ย้าย None↔ภ.พ.36 เป็นไม่ตรง · เพิ่ม None↔W2) |
| **X-4** (P3) | ✅ | `SettlementBatchMath.BuildFeeLines`: WHT ต่อ "บรรทัดใบ" (= บรรทัดบน 50 ทวิ) คิดจากฐานก่อน VAT รวมครั้งเดียว · สูตรตัวเดียว `SettlementFeeTax.WhtOnBase` (`Compute` เรียกตัวเดียวกัน — checker ห้ามสำเนา) · VAT ยังต่อรายการ · ก้อนเดียว/ไม่ระบุ VAT ตัวเลขเท่าเดิม | `X4_ค่าธรรมเนียม3_65คูณ100…10_55ไม่ใช่11_00` · `X4_ทิศตรงข้าม_…` |
| **X-5** (P3) | ✅ | `SettlementImportService.PersistAsync`: เติมรอบโอน gateway (PayoutRef ซ้ำ) ด้วยวันเงินเข้าอื่น ⇒ 400 `SETTLEMENT-GATEWAY-PAYOUT-DATE` (จุดตัดยอดคืนต้องเป็นวันของรอบ) · เส้นไฟล์คงคำเตือนเดิม | checker (ไม่มีเทสต์ DbContext) · TEST_PLAN SSF-06 |
| X-6 / X-7 | 📋 | ล็อก gateway ครอบเส้นไฟล์/จับคู่มือ/rematch — แตะ `SettlementImportService.Lines` หลายเส้น (ไฟล์ทีม T/I) · ตาข่ายที่การลงบัญชี (`IntentSettledElsewhere`) กันเงินผิดแล้ว ผลที่เหลือ = รอบค้าง/ไม่ลงตัวที่มองเห็น | — |
| X-8 | 📋 | `PeriodTo` ว่าง = ไม่มีขอบบน — อยู่ใน `LoadIntentRowsAsync` ที่ทีม GF กำลังแก้ช่วงวันที่ (X-2) ⇒ ทำพร้อมกันที่นั่นเพื่อไม่ conflict | — |
| ✅ <pending> รอบ 201 ทีม GW X-9 (A-GW5 แสดงแยก — ไม่รวมเข้า 11630 (คนละผังพัก)) | 📋 | หน้าเคลม VAT ค่าธรรมเนียม gateway ไม่รวม VAT ของ intent ที่ batch เป็นเจ้าของ — คู่ B-1 (ไฟล์ทีม G) | — |
| **X-10** (P3) | ✅ | `SettlementChannelService.SaveAsync` ตรวจโหมดเฉพาะช่องทางใหม่/การผูกหรือโหมดเปลี่ยน (`GatewayBatchIntentRules.ChannelModeTouched`) — ช่องทางเดิมที่โหมดขัดแก้ชื่อ/ปิดใช้งานได้ (ด่านนำเข้า/ลงบัญชีตรวจซ้ำทุกครั้ง) · ไม่จด VAT คู่ None↔ThaiVat7 ผ่าน | `X10_…` · Theory X3 แถวไม่จด VAT |
| **T-1** (P2 · ข้อ 27) | ✅ | `Helpers/SettlementPosting.cs`: `SettlementReceiptWht.Decide` (เกณฑ์งวดสุดท้ายเดียวกับ `CreatePaymentAsync`) · `SettlementReceiptTarget.DocumentWht` · ด่าน: ใบมี WHT รับบางส่วน ⇒ `ReceiptDocumentNotPayable` + ทางไปต่อ (รับชำระที่หน้าเอกสารแล้วผูกบรรทัด) · เงินเข้าเต็มยอดก่อนหัก ⇒ ทางไปต่อบอกให้ถอด WHT · `SettlementDocumentBuilder.ReceiptPayment(…, SettlementReceiptWhtKind)` — ครบ ⇒ `WithholdingTaxAmount = null` (งวดสุดท้ายเดิม ⇒ Dr 11910 · ลูกหนี้ GL ปิด) · ไม่มี WHT ⇒ 0 · ตัดสินไม่ได้ ⇒ ล้มดัง · `EnsureReceiptAsync` ตัดสินจากข้อเท็จจริงสด | `T1_ตัวตัดสิน_…` · `T1_รอบโอนจ่ายสุทธิครบ_…` · `T1_ด่าน_ใบมีWHTรับบางส่วน…_ใบไม่มีWHTรับบางส่วนไม่ถูกแตะ` · `T1_เงินเข้าเต็มยอดก่อนหัก…` · แก้ `SettlementPostingTests.รับชำระใบขาย_…` |
| **T-2** (P2 · ข้อ 15) | ✅ | `SettlementSummarySupplement.SplitDuplicates` (ใน `SettlementPostingChecks.cs`) — ทุกบรรทัดของใบสรุปวันนั้นเนื้อหาตรงรอบที่ออกใบแรก (ข้อเท็จจริง `SettlementContentOverlap.ForBatchAsync` ตัวเดียวกับผู้นำเข้า — ย้ายมาคำนวณครั้งเดียวก่อนด่าน) ⇒ `SummarySaleDuplicate` บล็อก · ตรงบางบรรทัด/ตรงรอบอื่น ⇒ ยังเป็นใบเพิ่มเติม (คำเตือนรายบรรทัดเดิมอยู่) | `T2_…` สองทิศ |
| **T-3** (P2) | ✅ | `SettlementWalletContinuity.PickPrevious` + `SettlementWalletCandidate` — รอบวันเดียวกันที่ปลายรอบ = ต้นรอบนี้ก่อน · fallback ลำดับนำเข้าโดยข้ามรอบที่ต้นรอบ = ปลายรอบนี้ (มาหลังตามยอด) · `WalletContinuityAsync` โหลดรอบวันเดียวกันทั้งหมด + รอบล่าสุดของวันก่อน · ข้อความ Gap บอกกรณีวันเดียวกัน | `T3_payoutวันเดียวกันนำเข้าสลับลำดับ_…` · `T3_ทิศตรงข้าม_…` |
| **T-4** (P3) | ✅ | `SettlementWalletContinuityKind.PreviousHadNoBalances` (รอบก่อน 0/0) ⇒ เตือน `WalletContinuityUnknown` ไม่บล็อก | `T4_…` (รอบก่อนมียอดจริงยังบล็อก) |
| T-5 (P3) | 📋 | ข้อความ "ต่อจากใบ X" อยู่บนเอกสารที่ออกเลขแล้ว (แก้ย้อนไม่ได้) · ยอด/ภาษีถูกทุกกรณี · แก้จริงต้องมีเส้นยกเลิก-ออกแทนของใบสรุป (ทีม V1) | — |
| **T-6** (P3) | ✅ | ทางไปต่อ `SummarySaleFirstNotIssued` + "ลบร่าง {เลข} / ยกเลิกรอบโอน {ref}" | `T6_…` |
| **T-7** (P3 · ข้อ 20) | ✅ | หลักฐาน "ปรับปรุงแล้ว" = JE อื่นที่โพสต์แล้ว/ไม่ถูกกลับรายการซึ่งช่อง **อ้างอิง** = เลข JE นี้ตรงตัว (`LegacyMoneyLegAudit.AdjustedBy` · ไม่ fuzzy · ไม่ค้นคำอธิบาย · ไม่อ้างตัวเอง) · `JournalAnomalyService.ScanAsync` ข้าม · ข้อความ Fix บอกวิธี (ช่อง "อ้างอิง" หน้าสมุดรายวัน) | `T7_…` |
| **T-8** (P3) | ✅ | `SettlementBatchMath.FeeCutoff` เรียง (ปี, เดือน) ก่อนจัดรูป | `T8_…` |
| **V2-C1** (P2 · ข้อ 25) | ✅ | `Helpers/DocumentDeliveryEvidence.DeliveredAsync` — หลักฐาน = `DocumentEmailLog` ส่งสำเร็จ (อีเมลปกติ + e-Tax by email เขียนแถวชนิดเดียวกันพร้อม `DocumentId`) · `OrphanChildrenAsync` เลิกใช้ `DocumentStatus.Sent` (checker ห้าม) | `V2C1_หลักฐานการส่ง_นับเฉพาะส่งสำเร็จ` (Theory 4) |
| **V2-C2** (P3) | ✅ | `SettlementOrphanItem.AckEffective` / `AckStatusLabel` — "✅ รับรู้แล้ว" เฉพาะกองยกเลิกไม่ได้จริง · อื่น ๆ "⚠️ การรับรู้เดิมไม่มีผล" · settlements.html แสดงป้ายจากเซิร์ฟเวอร์ | `V2C2_…` |
| **V2-C3** (P3) | ✅ | `ChangeTracker.Clear()` ต้น lambda ของ `AcknowledgeOrphanCoreAsync` และธุรกรรมท้ายของ `UnpostCoreAsync` — ตรวจทีละเมธอด: ของที่โหลดก่อน lambda (triage · `loaded` · `payoutJe` · `docFacts`) เป็น AsNoTracking ใช้อ่านอย่างเดียว · ขั้นยกเลิกเอกสาร/การรับชำระ/ถอนจับคู่ commit ธุรกรรมของตัวเอง (`DocumentService.cs` VoidDocumentAsync/VoidPaymentAsync) · แถวที่เขียนโหลด/สร้างใหม่หลัง Clear | checker `before` (Clear ก่อน BeginTransaction) |
| **V2-C4** (P3) | ✅ | `OrphanArtifactsAsync` กรองรอบตายใน SQL (`parts.Contains(p.Notes.Substring(len(หัวป้าย), 32))` เมื่อป้ายอยู่ต้น Notes · ป้ายไม่อยู่ต้น Notes ยังดึงมาให้ตัวอ่านป้ายตัดสิน ⇒ ผลเท่าเดิมทุกแถว) | checker |
| **V2-P1** (P3) | ✅ | คำขอรับรู้ส่ง `batchId` ของรอบที่ดูอยู่ (`OrphanAckRequest.BatchId` · api.js · settlements.html) → service ตรวจช่องทางเดียวกัน + tenant → audit `checkedBatchId/checkedPayoutRef` · `SettlementOrphanTriage.AckCovers` + `SettlementOrphanCurrentBatch`: รอบที่ใช้เลขรอบโอนเดียวกับรอบเจ้าของที่ยกเลิก **และ**นำเข้าหลังการรับรู้ ⇒ การรับรู้เดิมไม่ครอบ (บล็อก + ปุ่มรับรู้ใหม่ · ไม่ใช่ตอบซ้ำ) | `V2P1_…` สองทิศ |
| V2-P2 (P3) | ✅ บางส่วน | ตารางของกำพร้า (ผู้รับรู้/ปุ่ม) ไม่หายเมื่อกดลงบัญชีแล้วได้ 409 · 📋 รายงานระดับช่องทาง/หน้าเอกสารที่เห็นยอดค้างผังพักของใบกำพร้าที่รับรู้แล้ว (ต้องมี endpoint/หน้าใหม่ — คำถามค้าง 5 ของทีม V2) | — |
| V2-P3 / V2-P4 | 📋 | P3 ผูกการรับรู้กับ hash ของเหตุ (ต้องคอลัมน์เพิ่ม · ความเสี่ยงต่ำ) · P4 ตามชั้นหลาน (คำถามค้าง 2 ทีม V2) | — |
| (ของแถม) checker ทีม W | ✅ | แถว `BuildGateAsync` ของทีม W ต้องการ literal `c.CountryCode` ที่หายไปหลัง merge ทีม T (โหลดผู้ติดต่อทั้งแถว — `counterparty?.CountryCode`) ⇒ `required_call_site_check` ล้มที่ HEAD `b350edff` · แก้ literal ให้ตรงโค้ด (ตรรกะเดิม) | — |

## ไฟล์ที่แตะ

- โค้ด: `Helpers/GatewayBatchIntentRules.cs` · `Helpers/SettlementBatchMath.cs` (enum 37 · BuildFeeLines · FeeCutoff) · `Helpers/SettlementFeeTax.cs` · `Helpers/SettlementPosting.cs` ·
  `Helpers/SettlementPostingChecks.cs` · `Helpers/SettlementPostingGuards.cs` · `Helpers/LegacyMoneyLegAudit.cs` · `Helpers/DocumentDeliveryEvidence.cs` (ใหม่) ·
  `Services/Settlement/SettlementPostingService.cs` · `SettlementImportService.cs` · `SettlementImportService.Gateway.cs` (1 บรรทัด — ส่งสถานะ VAT · ไม่แตะช่วงวันที่) ·
  `SettlementChannelService.cs` · `Services/Implementations/JournalAnomalyService.cs` · `Controllers/PaymentSettingsController.cs` · `Controllers/SettlementController.cs` ·
  `wwwroot/js/api.js` · `wwwroot/pages/settlements.html`
- นอกขอบเขตไฟล์ที่ brief แบ่ง (แตะเล็กที่สุด): `PaymentSettingsController.Save` (ทีม G — ด่านโหมดตามข้อ 26 เท่านั้น) · `JournalAnomalyService` (ทีม T ข้อ 20) · `SettlementImportService.Gateway.cs` (P2/GF — บรรทัดเดียว)
- เทสต์: `SettlementReview200SfTests.cs` (ใหม่ · 20 Fact + 2 Theory/14 InlineData) · `SettlementGatewayPhase2Tests.cs` · `SettlementPostingTests.cs`
- checker: `tools/required_call_site_check.py` +17 แถว (บล็อก "รอบ 200 ทีม SF") + แก้แถว OrphanChildrenAsync (ห้าม `DocumentStatus.Sent`) + literal แถวทีม W
- doc: DOCUMENT_FLOW §2.10 (ตั้งค่าช่องทาง · ประกอบจาก PaymentIntent · ด่านผู้ลงบัญชี · ด่านเพิ่มทีม T · ขั้น 3 รับชำระ) + Last verified · TEST_PLAN §0 + SSF-01..15 + SPP2-04 · CHANGELOG ·
  `team-P2.md` (ถ้อยคำ §5 + "สูตรเดียว") · ตารางสถานะท้าย `review200-P2/T/V2.md`

## การเปลี่ยนพฤติกรรมที่ผู้ใช้เห็น (F2 ข้อ 8 — ถูกกันแล้วทำอะไรต่อ)

- **เข้มขึ้น**: (1) รอบโอนของช่องทางที่ผูก config ซึ่งโหมดขัด ⇒ ลงบัญชี/นำเข้าไฟล์ไม่ได้ — ทางไปต่อ: แก้โหมดสองที่ให้ตรง (ประกอบจาก intent ⇒ ยกเลิกรอบแล้วประกอบใหม่) ·
  (2) config None ↔ ช่องทาง ภ.พ.36 / W2 ที่เคยผ่าน ⇒ ไม่ผ่าน — ทางไปต่อ: ภ.พ.36 ใช้ช่องทางไม่ผูก config + นำเข้าไฟล์ · W2 แก้ให้ตรง ·
  (3) เปลี่ยนโหมดที่หน้าตั้งค่า gateway ขณะช่องทางผูกขัด ⇒ 400 — ทางไปต่อ: แก้ช่องทางก่อน · (4) ใบที่มี WHT ลูกค้ารับบางส่วนผ่านรอบโอน ⇒ บล็อก — ทางไปต่อ:
  รับชำระที่หน้าเอกสารแล้วผูกบรรทัด · (5) ใบสรุปเพิ่มเติมที่ซ้ำทุกบรรทัด ⇒ บล็อก — ยกเลิกรอบที่ซ้ำ · (6) เติมรอบ gateway ด้วยวันอื่น ⇒ 400 — ระบุวันให้ตรง ·
  (7) การรับรู้เดิมไม่ครอบรอบที่นำเข้าซ้ำเลขเดิม ⇒ บล็อก — รับรู้ใหม่ได้
- **ผ่อนลง**: ช่องทางเดิมที่โหมดขัดแก้ชื่อ/ปิดได้ · บริษัทไม่จด VAT คู่ VAT ไทยผ่าน · payout วันเดียวกันนำเข้าสลับลำดับไม่บล็อก · รอบก่อน 0/0 ⇒ เตือน · JE ที่ปรับปรุงแล้วเลิกเตือน ·
  ใบลดหนี้ที่อีเมลให้ลูกค้าแล้ว ⇒ รับรู้ได้ (ไม่ถูกพาไปยกเลิก)

## F3 ข้อ 7–12

7. `ModeMismatch` ผู้เรียก 5 จุด (ประกอบ · นำเข้าไฟล์ · บันทึกช่องทาง · ลงบัญชี · ผ่าน `ConfigChangeRefusal` ที่ค่าตั้ง gateway) + เทสต์ 2 ไฟล์ — ลายเซ็นเดิม (4 อาร์กิวเมนต์) เหลือ **0** ·
   `DocumentStatus.Sent` เป็น "ส่งลูกค้าแล้ว" ในเส้นกำพร้า เหลือ **0** · `WithholdingTaxAmount: 0m` ในเส้นรับชำระรอบโอน เหลือ **0** (checker ห้าม) · `ReceiptPayment(` ผู้เรียก 1 + เทสต์ 2
8. ทางเข้าที่แตะโหมดภาษีค่าธรรมเนียม: ประกอบจาก intent ✅ · นำเข้าไฟล์ ✅ · บันทึกช่องทาง ✅ · บันทึกค่าตั้ง gateway ✅ · ลงบัญชี ✅ · (เส้นรอบโอน gateway เดิมใช้ config อย่างเดียว — ไม่ต้องเทียบ) ·
   ทางเข้ารับชำระใบจากรอบโอน = `EnsureReceiptAsync` ทางเดียว · ทางเข้ารับรู้ของกำพร้า = endpoint เดียว
9. ดู "การเปลี่ยนพฤติกรรม" · เทสต์ทิศตรงข้ามอยู่ในทุกเทสต์ที่ชื่อมี "ทิศตรงข้าม"/"ไม่ถูกแตะ"
10. ค่าที่ persist: รอบโอนที่**ลงบัญชีไปแล้ว**ด้วยโหมดขัด — ไม่แก้อัตโนมัติ (ข้อ 20 · SQL คัดกรองใน `team-P2.md` §5 ยังใช้ได้) · การรับชำระรอบโอนที่ลงไปแล้วด้วย WHT 0 ของใบที่ตั้ง WHT
   (ลูกหนี้ GL ค้าง) — ไม่มี migration อัตโนมัติ (JE ในงวดที่อาจปิดแล้ว) · query คัดกรองอ่านอย่างเดียวให้นักบัญชี:
   ```sql
   SELECT p."CompanyId", d."DocumentNumber", d."WithholdingTaxAmount", p."PaymentNumber", p."Amount", p."WithholdingTaxAmount" AS paid_wht
   FROM "Payments" p JOIN "Documents" d ON d."Id" = p."DocumentId" AND d."CompanyId" = p."CompanyId"
   WHERE p."IsDeleted" = false AND p."Notes" LIKE '[SETTLEMENT:%' AND d."WithholdingTaxAmount" > 0 AND p."WithholdingTaxAmount" = 0;
   ```
11. ฝ่ายค้าน: งานนี้คือการแก้ผลฝ่ายค้านรอบแรก — **ยังไม่ได้ส่ง diff ของทีม SF ให้ฝ่ายค้านรอบสอง** (agent นี้ไม่มีเครื่องมือเรียก subagent) ⇒ ขอ main agent ส่ง
12. DOCUMENT_FLOW ✅ · TEST_PLAN ✅ · CHANGELOG ✅ · ACCOUNT_STRUCTURE ไม่แตะ (ไม่เปลี่ยนโครงสร้าง entity/บิลลิ่ง)

## checker ที่รัน (ใน worktree นี้)

ผ่านทั้งหมด: `required_call_site_check` (ชุด settlement 110 กติกา 0 ปัญหา · ตัวเต็ม + self-test — ดูผลท้ายรายงาน) · `record_arg_check` · `nullable_arg_check` · `using_check` ·
`tuple_name_merge_check` · `undeclared_local_check` (**หมายเหตุ**: checker ตัดทุกพาธที่มี `.claude` ⇒ ใน worktree ตรวจ 0 ไฟล์ — รันสำเนาที่กรองพาธแบบ relative แล้ว 1,525 ไฟล์ 0 จุด) ·
`arg_type_check` · `gl_code_check` · `flag_field_overwrite_check` · `html_attr_escape_check` · `string_quote_close_check` · `comment_line_break_check` · `identifier_space_check` ·
`accessibility_check` · `verbatim_string_check` · `namespace_shadow_check` · `dead_helper_check` · `enum_number_compare_check` · `onclick_js_string_check` · `test_inventory --check` ·
`node --check` (settlements.html · api.js) · awk brace (ทุก .cs ที่แก้) · U+FFFD · **ไม่ได้รัน** `check_all.sh` เต็ม (เครื่องโหลดหนัก)

## ความเสี่ยงคอมไพล์ (ไม่มี SDK)

- `ModeMismatch` เพิ่มพารามิเตอร์บังคับ `bool companyVatRegistered` — ผู้เรียกทุกตัวแก้แล้ว (grep ทั้งเรพ) · `ReceiptPayment` เพิ่ม `SettlementReceiptWhtKind wht` บังคับ — ผู้เรียก 1 + เทสต์ 2
- `ISettlementPostingService.AcknowledgeOrphanAsync` เพิ่ม `Guid? checkedBatchId` ก่อน `ct` — implementer เดียว + controller เดียว
- `SettlementChannelService`: `var prior = channelId is Guid priorId ? await …FirstOrDefaultAsync(ct) : null;` (ชนิด anonymous ↔ null literal) · `prior?.FeeVatMode` ได้ `SettlementFeeVatMode?`
- `PaymentSettingsController`: ส่ง `IEnumerable<(string DisplayName, …)>` เข้าพารามิเตอร์ `IEnumerable<(string ChannelName, …)>` — tuple ชนิดเดียวกัน (ชื่อไม่มีผลต่อชนิด)
- `SettlementBatchMath.BuildFeeLines`: deconstruct ternary `var (a, b, c) = cond ? WhtOnBase(…) : (Σ…, Σ…, Σ…)` — สองสาขาเป็น `(decimal, decimal, decimal)` · `tuple_name_merge_check` 0
- `ReceiptPayment`: `wht == FinalInstallment ? (decimal?)null : 0m`
- EF: `parts.Contains(p.Notes.Substring(markerHeadLength, 32))` + `StartsWith(markerHead)` (ตัวแปรท้องถิ่น) · `legacyNumbers.Contains(e.Reference.Trim())` · projection เข้า record ที่ไม่มีพารามิเตอร์ optional (`SettlementWalletCandidate` · `LegacyMoneyLegAudit.AdjustingEntry` — nested record ใน static class)
- `SettlementWalletPrevious` เพิ่มพารามิเตอร์ท้ายแบบ optional — ไม่มีผู้สร้างใน EF expression อีกแล้ว (เปลี่ยนเป็น `SettlementWalletCandidate`)
- pattern variables `stale`/`ack` ใน `SettlementOrphanTriage.Split` ชื่อไม่ชนกัน · `DocumentDeliveryEvidence.IsDeliveryEvidence` / `SettlementReceiptWht.UndecidableMessage` เป็น `internal` (เทสต์เข้าถึงผ่าน InternalsVisibleTo)

## คำถามค้าง (เลือกทิศที่มองเห็น/ย้อนได้ไว้แล้ว)

1. **ช่องทาง ภ.พ.36 ที่ผูก config gateway** — ข้อ 26 ทำให้ไม่ตรงเสมอ (config ไม่มีโหมดนี้) ⇒ gateway ต่างประเทศใช้ได้เฉพาะช่องทางไม่ผูก config + นำเข้าไฟล์ · ถ้าเจ้าของต้องการให้ประกอบจาก intent ได้
   ต้องเพิ่มค่า `GatewayFeeVatMode.ForeignPp36` ให้เส้นเดิมรู้จัก (ไฟล์ทีม G) แล้วเติมคู่ใน `VatProblem`
2. **รอบก่อน 0/0 = ไม่รู้** (T-4) — ถ้าช่องทางใดถอน wallet หมดทุกรอบจริง (ปลายรอบ 0 จริง) แล้วรอบถัดไปต้นรอบ ≠ 0 จะได้คำเตือนแทนการบล็อก · ทิศที่มองเห็น (เตือนพร้อมยอด)
3. **T-1 ใบที่มี WHT รับบางส่วน = บล็อกเสมอ** แม้ WHT ของใบถูกบันทึกครบแล้วจากงวดก่อน (ไม่ได้คำนวณ WHT คงเหลือซ้ำจาก DocumentService) — ทางไปต่อมี (รับชำระที่หน้าเอกสาร)
4. **ส่งผ่าน LINE** ไม่มีบันทึกการส่ง ⇒ นับเป็น "ส่งลูกค้าแล้ว" ไม่ได้ · ถ้าต้องการ ต้องให้ `DocumentLineDeliveryService` เขียนบันทึกก่อน (ไม่เดาจากการกดปุ่ม)
