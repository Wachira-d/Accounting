# รอบ 200 — ทีม T: เวลา/ภาษีของรอบโอน settlement

> ขอบเขต (BRIEF): R-A6 · R-A7 · R-A8 · R-A11 · R-A12 · R-B13 · C-11 · C-12 · C-13 · C-15–C-20 · O-2 (C-9 · DECISIONS ข้อ 15) · O-3 (C-6 · ข้อ 16) ·
> E2-9 · E2-11 · ข้อ 20 (รายงาน JE เก่าลงผิดหมวด 112) · ฐาน = `5c1fe028` (branch `claude/erp-system-review-team-660mev`) ·
> **ยังไม่ได้คอมไพล์** (เครื่องนี้ไม่มี .NET SDK) — ความเสี่ยงคอมไพล์อยู่ท้ายไฟล์

## สรุป

| ID | สถานะ | แก้ที่ / หลักฐาน | เทสต์ |
|---|---|---|---|
| R-A6 | ✅ 84d47dda (ตรวจที่ HEAD แล้ว) | `SettlementBatchMath.cs` กลุ่มใบสรุปด้วย `ThaiDate.CalendarDateUtc(l.TxnDate ?? batch.PayoutDate)` · ผู้ลงบัญชีใช้ `ThaiDate.CalendarDateUtc` กับวันรอบโอน/วันนี้ | `SettlementPostingTests.RA6_…` (มีอยู่แล้ว) · `C16_…` ล็อกขอบตี 1 เวลาไทยของค่าธรรมเนียมเพิ่ม |
| R-A7 | ✅ 84d47dda (ตรวจแล้ว) | `SettlementPostingGate.Evaluate`: `TaxPeriodFiled` (บล็อก · `TaxFilingLockPolicy.DeclaredOrFiledStatuses` — ตัวตัดสินเดือนยื่นที่มีอยู่) · `SummarySaleLate` (เตือน + เหตุผล §87 · นับ จ.–ศ. ไม่หักวันหยุด ⇒ เตือนเร็วกว่าจริง = ทิศปลอดภัย · ระบบยังไม่มีปฏิทินวันหยุดราชการ) · รายได้ซ้ำระดับออเดอร์ | `SettlementPostingTests.RA7_…` |
| R-A8 | ✅ 84d47dda (ตรวจแล้ว) | `SettlementBatchMath.cs` ตรวจทั้ง `batch.Currency` และ `channel.Currency` | มีอยู่แล้ว |
| R-A11 | ✅ 84d47dda + เทสต์รอบนี้ | builder ส่ง VAT ของแผนตรง (`VatAmountOverride`) · ผู้ลงบัญชีตรวจยอดหลังสร้าง (`SETTLEMENT-POST-AMOUNT`) · **เพิ่ม round-trip**: ใบค่าธรรมเนียม/ใบสรุปจาก builder → `DocumentService.PreviewTotals` (สูตรตัวเดียวกับ `CreateDocumentAsync`) = ยอดของแผน ทุกยอด 0.01–300.00 | `SettlementRound200TimeTaxTests.RA11_…` |
| R-A12 | ✅ รอบนี้ | `Helpers/SettlementPostingChecks.cs` `SettlementWalletContinuity.Judge` · service `WalletContinuityAsync` (รอบก่อนหน้าช่องทางเดียวกัน ไม่นับยกเลิก/ลบ เรียงวันเงินเข้า→เวลานำเข้า) · ต่าง ⇒ บล็อก `WalletContinuityGap` (ผลต่าง + ทางไปต่อ) · รอบแรก ⇒ เตือน `WalletContinuityUnknown` ("ไม่รู้" ≠ ต่อเนื่อง · DOCTRINE §1) | `RA12_…` |
| R-B13 | ✅ รอบนี้ | `SettlementCrossBatchReceipts.PendingElsewhere` (กติกาเดียวกับแผน: องค์ประกอบขาย · ไม่อ้างเงินในผังพัก · ไม่ใช่ AmountMismatch · รวมต่อใบต่อรอบ > 0 · รอบที่รับชำระใบนั้นแล้วไม่นับซ้ำ) · service `PendingReceiptsElsewhereAsync` (ทุกช่องทาง · Imported/Classified/Matched) · ด่าน `ReceiptDocumentNotPayable` ข้อความ+ทางไปต่อเฉพาะ | `RB13_…` ×2 (ใบ 1,000 สองรอบ ⇒ บล็อก · ผ่อน 600+400 ⇒ ผ่าน) |
| C-9 / O-2 | ✅ รอบนี้ (ข้อ 15) | `SettlementSummarySupplement.Judge` · `DuplicateSalesAsync` แยก: ใบสรุปวันเดียวกันของรอบที่**ยังมีผล** ⇒ ใบสรุปเพิ่มเติม (เตือน `SummarySaleSupplementary`) · ใบแรกยังไม่ออกเลข ⇒ บล็อก `SummarySaleFirstNotIssued` · ของรอบที่**ยกเลิกแล้ว** ⇒ ยังบล็อก `SummarySaleDuplicate` · builder `SummaryDocument(..., supplementOf)` บรรทัด "ยอดขายเพิ่มเติม … ต่อจากใบ X" + หมายเหตุ · วันที่/`DeliveryDate`/ยอด/VAT เท่าเดิม | `ข้อ15_…` ×2 |
| C-6 / O-3 | ✅ รอบนี้ (ข้อ 16) | คงบล็อก `SummaryTaxInvoiceNotAllowed` · ทางไปต่อ `SettlementPostingGate.SummaryRetailNextStep` (ติ๊ก "ประกอบกิจการขายปลีก" ที่ ตั้งค่า → ข้อมูลบริษัท · **ไม่ต้องใช้ ภ.พ.06** · หรือใบเต็มรูปรายออเดอร์ · ระบบไม่ติ๊กแทน) | `ข้อ16_…` · `SettlementReview198FixTests.C6_…` ยังผ่าน (ข้อความยังมี "ประกอบกิจการขายปลีก") |
| C-11 | ✅ รอบนี้ | `BuildGateAsync` หาแบบ ภ.ง.ด. ด้วย `WithholdingTaxCertService.ResolveWhtFormType(counterparty, WithholdingTax53)` (ตัวเดียวกับ `CreateAsync` ของ 50 ทวิ) → `FiledWhtPeriods` ของแบบนั้น + `FiledPp36Periods` · ด่าน: ข้อความใช้ชื่อแบบจริง (`WhtUnissuedCertGate.FormLabel`) · ใบค่าธรรมเนียม ภ.พ.36 ในเดือนที่ยื่นแล้ว ⇒ `TaxPeriodFiled` | `C11_…` ×2 |
| C-12 | ✅ รอบนี้ | อ่าน `CreatePaymentAsync` แล้ว: `WithholdingTaxAmount` null ⇒ คิดตามสัดส่วน และ**งวดสุดท้ายหยิบ WHT ที่เหลือทั้งก้อน** ⇒ `ReceiptPayment` ส่ง `0m` ชัด | `SettlementPostingTests.รับชำระใบขาย_…` (เดิม assert null → 0) |
| C-13 | ✅ รอบนี้ | `WalkInCustomerContact.GetOrCreateAsync`: ตรวจ → `pg_advisory_xact_lock(AdvisoryLockKey.For(co, WalkInContact, ""))` → ตรวจซ้ำ → สร้าง (ไม่มีธุรกรรมผู้เรียก = เปิดธุรกรรมสั้นเอง) · แถวซ้ำเดิม = หยิบแถวเก่าสุดแบบกำหนดได้ (ไม่รวมอัตโนมัติ — สอดคล้องข้อ 19 K-5) | required_call_site (ต้อง DB — ไม่มีเทสต์ DbContext) |
| C-15 | ✅ รอบนี้ | `SettlementStock.StanceOf` (ผ่าน `InventoryIndustry` ตัวเดียว — ผลตรวจเขียน `BusinessType` แต่แกนที่ถูกคือ `IndustryType` ตามที่ `InventoryIndustry` บันทึกไว้) · เตือน `SummarySaleNoStock` (ถือสต็อก / ยังไม่ระบุ — ทางไปต่อต่างกัน) | `C15_…` |
| C-16 | ✅ เตือน + ❓ คำถามค้าง | `SettlementBatchMath` (private `FeeCutoff`) เตือน `FeeCutoffCrossesMonth` ยอด+เดือน (ปฏิทินไทย) — ไม่แยกใบตามเดือนเอง (ดูคำถามค้าง 1) | `C16_…` |
| C-17 | ✅ รอบนี้ | `Plan` ข้อ 5: คืนเงินของออเดอร์ที่อยู่ในใบสรุปรอบนี้ ⇒ `RefundUnmatched` ทางไปต่อที่ทำได้จริง (ออกเอกสารขายของออเดอร์เอง → จับคู่ขาย+คืน → รับชำระ + ใบลดหนี้) | `C17_…` |
| C-18 | 📋 | ดูแผนข้างล่าง | — |
| C-19 | 📋 ➡️ V2 | orphan/unpost = ขอบเขตทีม V2 · ทางไปต่อวันนี้มีแล้ว (ยกเลิกทีละใบได้ตั้งแต่ S3/S4) | — |
| C-20 | ✅ บางส่วน | `_db.ChangeTracker.Clear()` ต้น lambda ของ `CommitPostedAsync` · `MatchCoreAsync` · `ResolveChargebackCoreAsync` · **ค้าง `UnpostCoreAsync`** (ไฟล์เดียวกันแต่เป็นเส้นของทีม V2 — ห้ามแตะตาม BRIEF) · วันนี้ไม่มี `EnableRetryOnFailure` (latent) | required_call_site ×3 |
| E2-9 | 📋 | ดูแผนข้างล่าง | — |
| E2-11 | 📋 | ดูเหตุผลข้างล่าง | — |
| ข้อ 20 (I-1/P-1) | ✅ รอบนี้ | ยังไม่มีรายงานที่นักบัญชีเปิดได้ (มีแต่ SQL ที่ต้องแทน `:company_id` เอง) ⇒ `Helpers/LegacyMoneyLegAudit.Classify` + `JournalAnomalyService` ข้อ 4 ⇒ ขึ้นการ์ด 🩺 หน้า "เครื่องมือนักบัญชี" ต่องวดที่เลือก (อ่านอย่างเดียว · JE ที่กลับรายการแล้วไม่ฟ้อง · ทางไปต่อ "ให้นักบัญชีตรวจ — ไม่แก้อัตโนมัติ") · **พบบั๊กใน SQL เดิม**: `JournalType IN (2,3)` (2 = สมุดซื้อ) ⇒ แก้เป็น `(3,4)` | `ข้อ20_…` |

## รายละเอียดที่ควรรู้

- **R-A12 เลือกบล็อก ไม่ใช่เตือน**: ยอดต้นรอบไม่ต่อเนื่อง = ผังพักคลาดจาก wallet จริงถาวร (หลักการข้อ 1 ของ DECISIONS รอบ 200 — ความจริงของเงินห้ามถูกบิด) ·
  ทางไปต่อ: นำเข้ารอบที่ขาด / ยกเลิกแล้วนำเข้าใหม่ด้วยยอดต้นรอบที่ถูก (หัวรอบโอนแก้ได้แค่บัญชีธนาคาร) · รายการนอกรอบโอนจริง ⇒ นำเข้าใหม่โดยตั้งต้นรอบ = ปลายรอบก่อน + บรรทัด "ปรับปรุงอื่น" ·
  ช่องทางที่ไม่เคยกรอกยอด wallet (0/0 ทุกรอบ) ไม่ถูกแตะ · รอบแรกของช่องทางเตือนครั้งเดียว
- **R-B13 ทั้งสองรอบถูกบล็อก** เมื่อรวมกันเกินยอดค้าง (ระบบไม่เดาว่ารอบไหนถูก) — ข้อความบอกเลขรอบที่ชนทั้งสองฝั่ง
- **ข้อ 15 ความเสี่ยงที่เหลือ**: ไฟล์ที่ไม่มีเลขออเดอร์และไม่มีเลขรายการ + นำเข้าเนื้อหาเดียวกันด้วยเลขรอบโอนอื่น ⇒ ใบสรุปเพิ่มเติมอาจซ้ำรายได้ — ด่านที่เหลือคือ
  ตัวกันซ้ำตอนนำเข้า (`SettlementTxnKey` · เตือนเนื้อหาตรงรอบอื่น `ContentOverlapElsewhereAsync`) + คำเตือน `SummarySaleSupplementary` ที่ผู้กดลงบัญชีเห็น ·
  ใบสรุปของรอบโอนที่ยกเลิกแล้วยังบล็อก (ไม่ถือเป็น "ใบแรก") — ถ้าทีม V2 เพิ่ม "รับรู้ของกำพร้า" (ข้อ 10) ให้ตัดสินว่าใบกำพร้าที่รับรู้แล้วนับเป็นใบแรกของวันได้ไหม (คำถามค้าง 3)
- **ข้อ 20**: ตัวจับใช้รูปข้อความที่โค้ดเก่าเขียนเอง (JE "รับชำระ …"/"จ่ายชำระ …" + บรรทัด "รับชำระ (…)"/"ตัดลูกหนี้ …" · POS "รับเงิน … POS #…") + หมวดผัง —
  ขาลูกหนี้ 113xx ≠ 11310 อาจเป็นผังที่ปักบนลูกค้าโดยตั้งใจ ⇒ Warning + ข้อความบอกว่า "ถ้าตั้งใจ ไม่ต้องทำอะไร"

## 📋 backlog พร้อมแผน

- **C-18 ป้ายการรับชำระปลอมได้** — แผน: (1) `Payment.SettlementBatchId uuid NULL` + index (`DatabaseMigrationHelper` ADD COLUMN IF NOT EXISTS) (2) `ReceiptPayment` ตั้งคอลัมน์ (ต้องเพิ่มช่องใน `CreatePaymentRequest` หรือให้ผู้ลงบัญชีประทับหลังสร้างในธุรกรรมเดียว)
  (3) backfill จาก Notes `[SETTLEMENT:{N}]` ของแถวเดิม (4) ผู้อ่านป้าย 6 จุดเปลี่ยนไปอ่านคอลัมน์ — `SettlementPaymentsAsync` · `OrphanArtifactsAsync` · `PendingReceiptsElsewhereAsync` (รอบนี้) ·
  `SettlementImportService.PostingArtifactsAsync` · `DocumentService.VoidPaymentAsync` (`SettlementArtifactGuard.BatchIdFromPaymentNotes`) · ยกเลิกการลงบัญชี (5) ด่านบันทึกรับชำระทั่วไปห้ามตั้งคอลัมน์นี้เอง ·
  ข้ามขอบเขตทีม V1/V2 (Void* · orphan/unpost) ⇒ ทำเป็นงานเดียวหลัง merge รอบ 200
- **E2-9 จุดตัดคืนเงินของรอบโอน gateway** — ยังจริงที่ HEAD (`GatewaySettlementMath.RefundCutoffUtc` + ข้อความ NetMismatch บอกให้ตรวจวันเงินเข้า) · ทางที่ถูก = ใช้ข้อมูล transfer ของผู้ให้บริการ
  ซึ่งคือเฟส 2 ที่ทีม P2 ทำ (DECISIONS รอบ 200 ข้อ 12: gateway เข้ารอบโอน settlement ตัวเดียวกับ marketplace — บรรทัดคืนเงินมาจากข้อมูลจริง ไม่ใช่จุดตัดเดา) ·
  ไม่แตะ `GatewaySettlementMath` เพื่อไม่ชนทีม G/P2 · เมื่อ P2 merge แล้ว: ปิดเส้นบันทึกรอบโอน gateway เดิมสำหรับช่องทางที่ผูกรอบโอน settlement แล้ว
- **E2-11 ข้อมูลเก่าของ `RefundDeductedAfterSettlement`** — ยังจริง แต่เหตุที่ไม่ทำยังจริง: สูตรเติมย้อนหลังต้องรู้โหมด VAT ค่าธรรมเนียม **ณ วันบันทึกรอบ** ซึ่งไม่ได้เก็บ ⇒ เติมด้วยโหมดวันนี้ = ค่าที่แต่งขึ้น (F2 ข้อ 3) ·
  ประชากรเกือบศูนย์ (โค้ดรอบ 198 เดียวกัน) · แผน: เก็บโหมดค่าธรรมเนียมลงแถว intent ตอนบันทึกรอบ (E2-12 "Settled-row fee mode") ก่อน แล้วค่อยเติมเฉพาะแถวที่มีโหมด

## คำถามค้าง (เลือกทิศที่มองเห็นและย้อนได้ไว้แล้ว)

1. **C-16** ค่าธรรมเนียมข้ามเดือน: แยกใบค่าธรรมเนียม/50 ทวิ ตามเดือนของ `TxnDate` หรือยอมรับวันเงินเข้า (+ ผู้ทำบัญชีตั้งค้างจ่ายเอง)? — ตอนนี้ **เตือน** (ไม่เปลี่ยนตัวเลขที่ลง)
2. **R-A7** นับ 3 วันทำการยังไม่หักวันหยุดราชการ (ไม่มีปฏิทินวันหยุดในระบบ — ฟีเจอร์ใน SYSTEM_REVIEW กอง 1) — เตือนเร็วกว่าจริง ไม่ผิดทิศ
3. **ข้อ 15 × ข้อ 10** ใบสรุปกำพร้าที่ทีม V2 "รับรู้" แล้ว ควรนับเป็นใบแรกของวันสำหรับใบสรุปเพิ่มเติมไหม — ตอนนี้ยังบล็อกเป็นรายได้ซ้ำ (ทิศที่มองเห็น)

## เครื่องมือ

- `tools/required_call_site_check.py` +11 กติกา (BuildGateAsync · WalletContinuityAsync · PendingReceiptsElsewhereAsync · DuplicateSalesAsync · EnsureSummaryDocumentAsync ·
  CommitPostedAsync/MatchCoreAsync/ResolveChargebackCoreAsync (C-20) · `WalkInCustomerContact.GetOrCreateAsync` · `JournalAnomalyService.ScanAsync`) — รันเฉพาะกติกาของไฟล์ที่แตะ
  (36 กติกา + negative test ในตัว) ผ่าน · รันเต็มไฟล์ timeout 600 วินาทีเพราะเครื่องถูกทีมอื่นรัน checker เดียวกันพร้อมกัน ~8 process
- `bash tools/check_all.sh` (รันเต็ม ~40 นาทีเพราะเครื่องถูกแชร์): checker ทุกตัวผ่านรวม `required_call_site_check` · simulation 8 ตัว · brace/node/U+FFFD 21 ไฟล์ ·
  TEST_PLAN §0 ตรง — ล้มตัวเดียวคือ `flag_field_overwrite_check` ที่**เราก่อเอง** (`p.Notes.Contains("[SETTLEMENT:")` ทำให้ `Notes` ทุกตารางถูกนับเป็น "ที่เก็บธง"
  ⇒ ฟ้อง 6 จุดของตารางอื่น) ⇒ แก้เป็นค่าคงที่ `SettlementPostingKeys.PaymentMarkerHead` แล้วรัน checker นั้น + กติกา required_call_site ของไฟล์ที่แตะซ้ำ ผ่าน
- ฝ่ายค้าน (F3 ข้อ 11): agent นี้ไม่มีเครื่องมือเรียก subagent ⇒ ตรวจตัวเองด้วย 3 คำถาม (ทางเข้าอื่น: ผู้ลงบัญชีเป็นทางเข้าเดียวของใบสรุป/รับชำระจากรอบโอน ·
  `WalkInCustomerContact` ใช้ร่วมกับ integration — ล็อกครอบทั้งสอง · ทิศตรงข้าม: ทุกเทสต์มีครึ่ง "ไม่แตะของถูก" · สถานะปลายทาง: ไม่มีการประทับใหม่) — **ขอให้ main agent ส่ง diff ให้ฝ่ายค้านอีกรอบ**

## ความเสี่ยงคอมไพล์ (ไม่มี SDK)

- `WithholdingTaxCertService.ResolveWhtFormType` เป็น `internal static` — เรียกจาก `SettlementPostingService` (assembly เดียวกัน) ด้วยชื่อเต็ม `Accounting.Services.Implementations.WithholdingTaxCertService`
- `await using var tx = ownTx ? await db.Database.BeginTransactionAsync(ct) : null;` ใน `WalkInCustomerContact` (ชนิด `IDbContextTransaction?` จาก target-typed conditional)
- `IReadOnlySet<(Guid BatchId, Guid DocumentId)>` รับ `HashSet<(Guid, Guid)>` (ชื่อ tuple ต่างกัน = identity conversion)
- pattern variable ใน `if` ของ `SettlementPostingGate.Evaluate` (`filedPp36` · `w` · `prev`) รั่วสู่ block — ตรวจแล้วไม่ชนชื่ออื่นในเมธอด
- tuple ชื่ออนุมานใน `ToDictionary(..., b => (b.PayoutRef, Dead: …))` ของ `DuplicateSalesAsync` (ใช้ `other.PayoutRef`)
- `SettlementPostingFacts`/`SettlementReceiptTarget` เพิ่มพารามิเตอร์ optional ท้าย record — ผู้สร้างแบบ positional เดิม (เทสต์ 6 ไฟล์) ไม่ต้องแก้ · ไม่มีใครสร้าง record เหล่านี้ใน EF expression
