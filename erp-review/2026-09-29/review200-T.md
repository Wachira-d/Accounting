# รอบ 200 — ฝ่ายค้านของทีม T (เวลา/ภาษีของรอบโอน settlement)

> ตรวจ diff `5c1fe028..worktree-agent-abdad47d63899e57b` (คอมมิตโค้ด `80908ecb`) แบบอ่านอย่างเดียว · ไฟล์อ่านจาก `git show <branch>:<path>` / `git archive` ลง scratchpad
> (ไม่แตะ working tree หลัก) · checker ที่รันบนสำเนา branch: `advisory_lock_key_check` · `tuple_name_merge_check` · `record_arg_check` · `nullable_arg_check` ·
> `using_check` · `undeclared_local_check` · `accessibility_check` · `namespace_shadow_check` · `dead_helper_check` · `flag_field_overwrite_check` · `gl_code_check` ·
> `string_quote_close_check` · `comment_line_break_check` · `verbatim_string_check` · `identifier_space_check` — **ผ่านทั้งหมด (0 จุด)** · ไม่มี .NET SDK = ยังไม่ได้คอมไพล์
> เลขบรรทัดด้านล่าง = ไฟล์บน branch `worktree-agent-abdad47d63899e57b`

## สรุป

| # | เรื่อง | ผล | P |
|---|---|---|---|
| T-1 | C-12 `WithholdingTaxAmount: 0m` — ใบที่ตั้ง WHT ลูกค้าไว้ รับชำระผ่านรอบโอนจนครบยอดสุทธิ ⇒ ลูกหนี้ใน GL ค้างเท่ายอด WHT ถาวรขณะใบขึ้น "ชำระแล้ว" (เกณฑ์เงินสด = ค่าเริ่มต้น) | **CONFIRMED** | P2 |
| T-2 | ข้อ 15 — ด่านกันไฟล์ซ้ำ (ไม่มีเลขออเดอร์/เลขรายการ) เหลือแค่คำเตือนตอน**นำเข้า** ที่ไม่ไหลมาถึงด่าน**ลงบัญชี** ⇒ ใบสรุปเพิ่มเติมที่ซ้ำรายได้/ภาษีขายผ่านได้ด้วยคำเตือนทั่วไปคำเดียว | PLAUSIBLE | P2 |
| T-3 | R-A12 — รอบโอนที่ `PayoutDate` วันเดียวกัน เรียงด้วยเวลานำเข้า ⇒ นำเข้าสลับลำดับ = บล็อก `WalletContinuityGap` ทั้งสองรอบโดยที่ยอดถูก · ข้อความทางไปต่อไม่พูดถึงลำดับ | PLAUSIBLE | P2 |
| T-4 | R-A12 — ช่องทางที่รอบเก่ากรอก wallet 0/0 (ไฟล์เก่าไม่มียอด) แล้วเริ่มกรอกยอดจริง ⇒ บล็อกถาวร · ทางไปต่อที่ข้อความบอกใช้ได้ (นำเข้าใหม่ + บรรทัด "ปรับปรุงอื่น") แต่ไม่บอกเคสนี้ตรง ๆ | PLAUSIBLE | P3 |
| T-5 | ข้อ 15 — ใบแรกถูกยกเลิก/รอบแรกถูกยกเลิกการลงบัญชีภายหลัง ⇒ ใบเพิ่มเติมยังพิมพ์ "ต่อจากใบ X" ที่ Voided (เฉพาะข้อความ · ยอด/ภาษีถูก) | PLAUSIBLE | P3 |
| T-6 | ข้อ 15 — ใบแรกเป็นร่างค้างของรอบที่ลงบัญชีต่อไม่ได้ (เช่นติดด่านใหม่) ⇒ ทางไปต่อ "ลงรอบนั้นให้เสร็จ" เป็นทางตัน · ไม่บอก "ลบร่าง/ยกเลิกรอบนั้น" | PLAUSIBLE | P3 |
| T-7 | ข้อ 20 — ทำตาม Fix (JE ปรับปรุงในงวดปัจจุบัน) แล้วคำเตือนไม่มีวันหาย (ไม่ข้าม JE ที่ถูกปรับปรุงแล้ว) | CONFIRMED | P3 |
| T-8 | C-16 — รายชื่อเดือนเรียงแบบข้อความ `"MM/YYYY"` ⇒ ข้ามปีเรียงผิด (12/2568 หลัง 01/2569) | CONFIRMED | P3 (ข้อความ) |
| — | C-20 · C-13 · C-11 · R-B13 · ข้อ 20 enum/tenant/อ่านอย่างเดียว · ข้อ 15 เลข/race/tenant · คอมไพล์ 6 จุด | NOT-A-BUG | — |

---

## CONFIRMED

### T-1 (P2) C-12 — ส่ง WHT = 0 ชัด ทำให้ลูกหนี้ใน GL ค้างเงียบเมื่อใบตั้ง WHT ลูกค้าไว้

- จุดแก้: `Accounting/Helpers/SettlementPosting.cs:692` `ReceiptPayment(...) => new(..., WithholdingTaxAmount: 0m)`
- ผลใน `DocumentService.CreatePaymentAsync`:
  - `doc.TotalAmount = SubTotal + VAT − WHT` (`DocumentService.cs:1592`) ⇒ `BalanceDue` เป็นยอด**สุทธิหลัง WHT**
  - ส่ง `0m` ⇒ ข้ามสาขา "งวดสุดท้ายหยิบ WHT ที่เหลือ" (`DocumentService.cs:12198–12214`) ⇒ `payment.WithholdingTaxAmount = 0`
  - `doc.PaidAmount += request.Amount` (`:12288`) ⇒ รับครบยอดสุทธิ = ใบ **Paid**
  - เกณฑ์เงินสด (ค่าเริ่มต้น `?? WhtRecognitionBasis.Cash`): JE ตอนอนุมัติตั้ง AR = **ยอดก่อนหัก WHT** (`:14790` `TotalAmount + WithholdingTaxAmount`) · JE รับชำระล้าง AR เพียง `thbAmount` (`:17041` — `postPerPaymentWht` เป็นเท็จเมื่อ WHT = 0) ⇒ **AR ใน GL ค้างเท่ายอด WHT ถาวร · 11910 (ภาษีถูกหัก) ไม่เคยถูกบันทึก** ขณะที่ AR รายตัว = 0
- ฉากที่เกิด: ใบกำกับ/ใบแจ้งหนี้ที่ติ๊ก WHT ลูกค้า (เช่น 3% งานบริการ) ถูกจับคู่กับบรรทัดขายของรอบโอน และยอดที่จับคู่รวมแล้ว**เท่ายอดสุทธิ** (ผู้ซื้อจ่ายสุทธิผ่านช่องทางนั้น หรือผู้ใช้จับคู่หลายรอบจนครบยอดสุทธิ) — ด่านของแผนมีแค่ `BalanceDue + 0.005 < r.Amount` (`SettlementPosting.cs:417`) จึงผ่าน
- เทียบของเดิม (`null`): งวดสุดท้ายหยิบ WHT ที่เหลือ ⇒ Dr 11910 · AR ล้างทั้งก้อน — ตรงกับกรณีที่เงินเข้าจริงเป็นยอดสุทธิ
- เหตุผลของทีม ("แพลตฟอร์มโอนยอดเต็ม ไม่ได้หักแทนผู้ซื้อ") ถูกเฉพาะเคสยอดเต็ม — แต่เคสยอดเต็มถูกด่าน `BalanceDue` บล็อกอยู่แล้ว (ยอดเต็ม > ยอดสุทธิ) ⇒
  ผลจริงของการเปลี่ยนคือเคส**ยอดสุทธิ**ซึ่งเปลี่ยนจากถูกเป็นผิดเงียบ (ทิศตรงข้ามที่เทสต์ไม่ได้ล็อก — `SettlementPostingTests.รับชำระใบขาย_…` เปลี่ยน assert null→0 เท่านั้น)
- ความเสี่ยงเข้าถึงได้: แคบ (ใบที่มี WHT + จับคู่กับรอบโอน) จึงให้ P2 ไม่ใช่ P1 — แต่เป็นเงิน/GL ที่ไม่ตรงกับบัญชีย่อยแบบไม่มีใครเห็น
- ทางแก้ที่เสนอ: อย่าตัดสินด้วยการส่ง 0 เงียบ ⇒ เพิ่มข้อเท็จจริง `WithholdingTaxAmount` ของใบลง `SettlementReceiptTarget` แล้ว**บล็อก/ถาม**เมื่อใบที่จะรับชำระมี WHT > 0
  ("ช่องทางนี้ไม่หัก ณ ที่จ่าย — ถ้าผู้ซื้อหักจริงให้ส่ง WHT ตามใบ · ถ้าไม่หัก ให้แก้ใบถอด WHT") · เทสต์ทิศตรงข้าม: ใบ 1,070 WHT 30 รับ 1,040 ผ่านรอบโอน ⇒ ต้องไม่ Paid เงียบโดย AR ค้าง 30

### T-7 (P3) ข้อ 20 — คำเตือน JE เก่าไม่หายหลังทำตาม Fix

- `JournalAnomalyService.cs:203` ข้ามเฉพาะ JE ที่ถูก**กลับรายการ** (`ReversedByEntryId`) · ข้อความ Fix (`LegacyMoneyLegAudit.cs` ค่าคงที่ `Fix`) บอกให้ "บันทึก JE ปรับปรุงในงวดปัจจุบัน … อ้างเลข JE นี้"
  ⇒ นักบัญชีทำตามแล้ว สแกนงวดนั้นอีกกี่ครั้งก็ยังขึ้น (ไม่มีกลไกผูก JE ปรับปรุงกับ JE เก่า/รับทราบ) = เตือนที่ฟ้องใบที่จัดการแล้ว (F2 ข้อ 8 "คำเตือนที่ฟ้องใบถูกทุกใบ = ปิดด่านโดยไม่ตั้งใจ")
- เสนอ: ข้าม JE ที่มี JE อื่น `Reference`/ข้อความอ้างเลขนี้ หรือเปลี่ยน Fix เป็น "กลับรายการแล้วลงใหม่" ให้ตรงกับที่ตัวสแกนรู้จัก

### T-8 (P3) C-16 — เรียงเดือนแบบข้อความ

- `SettlementBatchMath.cs:566` `.Select(d => $"{d:MM}/{d.Year + 543}")…OrderBy(s => s, StringComparer.Ordinal)` ⇒ `"01/2569"` มาก่อน `"12/2568"` · ข้อความเตือนเท่านั้น (ไม่กระทบยอด) · เรียงด้วย `(Year, Month)` ก่อนจัดรูป

---

## PLAUSIBLE

### T-2 (P2) ข้อ 15 — ด่านกันรายได้ซ้ำระดับไฟล์เหลือแค่คำเตือนตอนนำเข้า

- ก่อนข้อ 15 `SummarySaleDuplicate` (บล็อก) คือด่านเดียวที่กัน "ไฟล์เดียวกันนำเข้าด้วยเลขรอบโอนอื่น และไม่มีเลขออเดอร์/เลขรายการ" (ด่านระดับออเดอร์ `DuplicateSalesAsync` ส่วน (2)(3) ต้องมี `ExternalOrderId`)
- หลังข้อ 15: ใบสรุปวันเดียวกันของรอบที่ยังมีผล ⇒ `SummarySaleSupplementary` **ไม่บล็อก** (`SettlementPosting.cs:500`) ข้อความทั่วไป "ตรวจว่าออเดอร์ในรอบนี้ไม่ใช่ชุดเดียวกับใบแรก"
- ตัวกันที่ทีมอ้าง (`SettlementImportService.ContentOverlapElsewhereAsync` `:313/:629`) เป็นคำเตือน**ตอนนำเข้า** (`warnings.Add`) ไม่ถูกเก็บลงแถวและไม่ถูกคำนวณซ้ำที่ `BuildGateAsync` ⇒
  ผู้กดลงบัญชี (อีกคนเมื่อเปิด SoD) ไม่เห็นหลักฐานว่า "เนื้อหาตรงทุกช่องกับรอบ X" · ผลเมื่อพลาด = รายได้ + ภาษีขาย ภ.พ.30 ของวันนั้นซ้ำ (ใบเลขใหม่ที่ออกแล้วแก้ย้อนไม่ได้ ต้องออกใบลดหนี้)
- เป็นทิศที่เจ้าของเลือก (ข้อ 15) — จึงไม่ใช่บั๊ก แต่ **"คำเตือนตอนนำเข้า" ไม่ใช่ด่านที่ผู้ตัดสินเห็น** · เสนอ: ที่ `DuplicateSalesAsync` เมื่อเป็นใบเพิ่มเติม ให้เทียบ `SettlementTxnKey.ContentKey`
  ของบรรทัดใบสรุปรอบนี้กับบรรทัดของรอบที่ออกใบแรก — ตรงกัน ⇒ ยกเป็นบล็อก `SummarySaleDuplicate` พร้อมเลขรอบ (ไม่ตรง ⇒ ใบเพิ่มเติมตามข้อ 15)

### T-3 (P2) R-A12 — รอบโอนวันเดียวกันเรียงด้วยเวลานำเข้า

- `SettlementPostingService.cs:594–595` `b.PayoutDate < batch.PayoutDate || (== && b.CreatedAt < batch.CreatedAt)` · `PayoutDate` ถูกตัดเป็นวันตามปฏิทินตอนนำเข้า (`SettlementImportService.cs:366` `ThaiDate.CalendarDateUtc`)
- ฉาก: payout สองรอบวันเดียวกัน (P1 ต้นรอบ 0 → ปลาย 100 · P2 ต้นรอบ 100 → ปลาย 50) ผู้ใช้นำเข้า P2 ก่อน P1 ⇒ P1 เทียบกับ P2 (ปลาย 50 ≠ ต้น 0) = Gap · P2 เทียบกับรอบเมื่อวาน = Gap ⇒ **บล็อกทั้งคู่ทั้งที่ยอดถูก**
- ทางไปต่อจริงคือยกเลิก+นำเข้าใหม่ให้ลำดับถูก (ถ้าลงบัญชีไปแล้วต้องยกเลิกการลงบัญชีก่อน) แต่ข้อความ `WalletContinuityGap` (`SettlementPosting.cs:523–530`) บอกแค่ "รอบที่ขาด/ยอดต้นรอบผิด/รายการนอกรอบ"
- เสนอ: ภายในวันเดียวกันให้ตัวตัดสินหา "รอบที่ปลายรอบ = ต้นรอบนี้" ก่อน (ลำดับนำเข้าเป็นแค่ fallback) หรืออย่างน้อยเพิ่ม `PeriodTo`/`PayoutRef` เป็นตัวเรียง + ข้อความบอกกรณีนำเข้าสลับลำดับ

### T-4 (P3) R-A12 — ช่องทางที่ยอด wallet เพิ่งเริ่มกรอก

- `SettlementWalletContinuity.Judge` (`SettlementPostingChecks.cs`) เทียบกับรอบก่อนที่ปลายรอบ = 0 เพราะไฟล์รุ่นเก่าไม่มียอด ⇒ ต้นรอบจริง ≠ 0 = Gap (บล็อก)
- มีทางไปต่อ (นำเข้าใหม่ ต้นรอบ = 0 + บรรทัด "ปรับปรุงอื่น" ที่ผู้ใช้เลือกผัง — ได้ผลบัญชีที่ถูก: ยกยอด wallet เข้าผังพัก) จึงไม่ใช่ทางตัน · แต่ข้อความไม่พูดถึงเคส "รอบก่อน ๆ ไม่มียอด wallet"
  และบังคับให้เก็บ `OpeningWalletBalance` ที่ไม่ใช่ยอดจริง · เสนอ: รอบก่อนที่ต้น = ปลาย = 0 ทั้งคู่ ⇒ ถือเป็น "ไม่รู้" (`WalletContinuityUnknown` เตือน) ไม่ใช่ Gap

### T-5 (P3) ข้อ 15 — อ้างใบแรกที่ถูกยกเลิกภายหลัง

- ใบแรกถูกเลือกตอนสร้างเฉพาะใบที่ไม่ Voided (`SettlementPostingService.cs:719–736` · `SettlementPostingChecks.cs:98`) ⇒ ตอนสร้างถูก · แต่หลังจากนั้นถ้ายกเลิกการลงบัญชีรอบแรก (ใบแรก Voided)
  ใบเพิ่มเติมยังพิมพ์ "ต่อจากใบ X" · และถ้ารอบแรกลงใหม่ ใบใหม่ของรอบแรกจะกลายเป็น "ใบเพิ่มเติมของ" ใบรอบสอง ⇒ อ้างกันไขว้ · ไม่กระทบยอด/ภาษี (แต่ละใบเป็นใบกำกับของตัวเอง)
- ลงบัญชีค้างครึ่งทางแล้วทำต่อ: ใบที่สร้างไว้ถูก adopt (`CreateOrAdoptAsync`) ข้อความเดิมคงอยู่แม้ `SupplementOf` รอบใหม่ว่างแล้ว · ข้อความเท่านั้น

### T-6 (P3) ข้อ 15 — ใบแรกเป็นร่างของรอบที่ทำต่อไม่ได้

- `SummarySaleFirstNotIssued` ทางไปต่อ = "ลงบัญชีรอบ {FirstPayoutRef} ให้เสร็จก่อน" (`SettlementPosting.cs:494–498`) · ถ้ารอบนั้นติดด่านบล็อกอื่น (เช่น `WalletContinuityGap` ใหม่ของรอบนี้เอง ·
  งวดปิด) ผู้ใช้ไม่มีทางอื่นในข้อความ — ควรบอกด้วยว่า "หรือลบร่าง {เลข DRAFT} / ยกเลิกรอบโอนนั้น (ร่างจะกลายเป็นของกำพร้าที่ระบบบอกให้ลบ)"

---

## NOT-A-BUG (ตรวจแล้ว)

### ข้อ 15 เลข / ใบแรก / race / tenant / รายงานภาษีขาย
- เลขที่: ใบเพิ่มเติมเกิดผ่าน `CreateDocumentAsync` (ร่าง) → `ApproveIfDraftAsync` ⇒ เลข gap-free ออกตอนอนุมัติ เส้นเดียวกับใบสรุปเดิม
- ใบแรกถูกใบ: `same` กรอง `CompanyId` + ป้าย `system:settlement:{batch}:sum-yyyyMMdd` (วันเดียวกัน) · `channelBatches` กรอง `CompanyId` + `ChannelId` (ช่องทางเดียวกัน) · รอบตัวเองถูกตัด (`b.Id != batch.Id`) ·
  ใบ Voided/ลบ ไม่นับ · ใบของรอบที่ยกเลิก/ลบแล้วยังบล็อก `SummarySaleDuplicate` (`:731`)
- race: `PostAsync` ถือ `JobLock.RunExclusiveAsync(SettlementChannelLock.Part(channelId))` (session advisory lock คีย์คงที่) ตลอด `PostCoreAsync` รวม `BuildGateAsync` ⇒ สองรอบช่องทางเดียวกันลงพร้อมกันไม่ได้ ⇒ ไม่มี "ใบแรกสองใบ"
  (ใบแรกยังเป็นร่าง ⇒ บล็อก `SummarySaleFirstNotIssued`)
- รายงานภาษีขาย: ใบเพิ่มเติมเป็นใบกำกับ/ใบเสร็จแยกใบ ยอด/VAT/วันที่/`DeliveryDate` = แผนของรอบนี้ (`SummaryDocument` เปลี่ยนแค่ข้อความ) ⇒ นับครบไม่ซ้ำ (ความเสี่ยงซ้ำจากไฟล์ซ้ำ = T-2)
- `supplementOf.ToDictionary(s => s.Day, …)`: 1 แถวต่อวันของ `plan.SummarySales` ⇒ ไม่มีคีย์ซ้ำ

### R-A12 รอบก่อนหน้า / tenant
- กรอง `CompanyId` + `ChannelId` + ไม่ลบ + ไม่ Voided · เรียงวันเงินเข้าก่อนเวลานำเข้า (ข้อจำกัดวันเดียวกัน = T-3) · รอบแรก = เตือน "ไม่รู้" ไม่ใช่ผ่าน (DOCTRINE §1 ✓) ·
  ช่องทางที่ 0/0 ทุกรอบ = Continuous (ไม่แตะ)

### R-B13 ข้ามรอบ
- นับเฉพาะรอบ `Imported/Classified/Matched` (enum จริง: 0/1/2 · Posted 3 · BankMatched 4 · Voided 9) + `!b.IsDeleted && !l.IsDeleted` ⇒ ไม่นับรอบที่ยกเลิก/ลบ ·
  รอบที่ลงบัญชีแล้วไม่นับ (ยอดค้างลดแล้ว) · รอบที่ลงค้างครึ่งทางแล้วรับชำระใบนั้น (ป้าย `PaymentMarker`) ไม่นับซ้ำ · tenant: `l.CompanyId`, `b.CompanyId`, `p.CompanyId` ครบ ·
  ทุกช่องทางของบริษัท (ถูก — ใบขายเป็นของบริษัท) · บล็อกทั้งสองรอบพร้อมข้อความเลขรอบ = มีทางไปต่อ

### C-11 แบบ ภ.ง.ด. ตรงกับที่ 50 ทวิ ออกจริง
- ผู้ออก: `SettlementDocumentBuilder.WhtCertificate` ส่ง `TaxType.WithholdingTax53` → `WithholdingTaxCertService.CreateAsync` เรียก `ResolveWhtFormType(payee, request.TaxFormType)` **ไม่ส่งธง foreign** (`:106`)
- ด่าน: `ResolveWhtFormType(counterparty, TaxType.WithholdingTax53)` ไม่ส่งธง foreign เหมือนกัน (`SettlementPostingService.cs:481–482`) ⇒ นิติบุคคล 53 · บุคคลธรรมดา 3 · `CountryCode` ต่างประเทศ 54 — ตรงกันทุกกรณี
- ผู้ติดต่อที่ถูกลบ: ด่านได้ null ⇒ 53 · แต่ `CounterpartyFound=false` บล็อกการลงบัญชีอยู่แล้ว ⇒ ไม่เกิดใบ
- `internal static` เรียกจาก assembly เดียวกันได้ · เทสต์เข้าถึงผ่าน `InternalsVisibleTo Include="Accounting.Tests"` (`Accounting.csproj:82`)
- ภ.พ.36 เดือน = วันเงินเข้า (= วันที่ใบค่าธรรมเนียม) ⇒ `FiledPp36Periods` เทียบถูกเดือน · ด่านยกเลิกการลงบัญชีใช้รายงานทุกชนิดอยู่แล้ว (ไม่มีคู่สมมาตรตกหล่น)

### C-13 `WalkInCustomerContact`
- คีย์ `AdvisoryLockKey.For(companyId, "walk-in-contact", "")` = FNV-1a คงที่ข้ามเครื่อง (`advisory_lock_key_check` 0 จุด) · ตรวจ → ล็อก → ตรวจซ้ำ → สร้าง ถูกลำดับ
- `await using var tx = ownTx ? await db.Database.BeginTransactionAsync(ct) : null;` — ชนิดของ conditional = `IDbContextTransaction` (อีกข้างเป็น null literal) · `await using` รับ null ได้ ⇒ คอมไพล์ผ่าน
- ผู้เรียกทั้งสอง (`SettlementPostingService.EnsureSummaryDocumentAsync` · `IntegrationService.ProcessInvoiceAsync:805`) ไม่มีธุรกรรมค้าง ⇒ เส้น ownTx (ล็อกถือสั้น ไม่ซ้อนล็อกอื่น ⇒ ไม่เปิด deadlock) ·
  ยังไม่มี `EnableRetryOnFailure` ⇒ ธุรกรรมที่ผู้ใช้เปิดเองนอก execution strategy ใช้ได้ (ถ้าเปิด retry วันหน้าต้องห่อด้วย strategy — latent เหมือนที่ทีมจดไว้)

### C-20 `ChangeTracker.Clear()` ต้นแลมบ์ดา — ตรวจทีละเมธอด
- ผู้เรียกของ `PostAsync`/`MatchBankTransactionAsync`/`ResolveChargebackAsync` มีแต่ `SettlementController` (`:340/:365/:447`) — ไม่มี entity ของผู้เรียกค้างใน tracker ที่ Clear จะปลดทิ้ง (ต่างจากบั๊กที่พักรอบ 194 ที่มาของ `TrackedChangeRevert`)
- `CommitPostedAsync`: ของที่โหลดก่อนแลมบ์ดา = `gate.Loaded` (`LoadAsync` ทั้งหมด `AsNoTracking`) · `ExistingDocs`/`SettlementPayments` (`AsNoTracking`) — ใช้อ่านอย่างเดียว ·
  ของที่ถูกเขียน (`batch`, `SettlementLines`, `PaymentIntents`, JE จาก `JournalEntryBuilder`, audit) โหลด/สร้าง**หลัง** Clear ทั้งหมด · ขั้นก่อนหน้า (`EnsureFee/Summary/Receipt`) `SaveChanges` เองทุกขั้น ⇒ ไม่มีของค้างที่ต้องพึ่ง SaveChanges ตัวท้าย
- `MatchCoreAsync`: `batch` โหลดหลัง Clear · `_bank.ReconcileAsync` โหลด `BankTransaction` เองแล้ว `SaveChanges` เอง (ไม่ Clear/ไม่เปิดธุรกรรมซ้อน) · `batch` ถูกแก้หลังนั้นและ SaveChanges ท้ายแลมบ์ดา ⇒ เขียนจริง
- `ResolveChargebackCoreAsync`: `line`/`loaded` `AsNoTracking` ใช้แค่อ่าน · JE/audit สร้างหลัง Clear
- `AddChainedAuditLog` หา tip จาก DB + แถว Added ที่ค้าง ⇒ Clear ไม่ทำ chain แตก
- ค้าง (ทีมจดไว้แล้ว): `UnpostCoreAsync` ยังไม่มี Clear — เป็นเส้นทีม V2

### ข้อ 20 `JournalAnomalyService` ข้อ 4
- อ่านอย่างเดียวจริง: ใช้ `journals` ชุดเดียวกับข้อ 1 (`AsNoTracking` · `CompanyId == companyId` · `Posted` · ช่วงวันที่) · เพิ่มแค่ `Anomaly` ในลิสต์ · ไม่มี SaveChanges
- C# ไม่ใช้ค่าตัวเลข `JournalType` · SQL แก้ `(2,3)` → `(3,4)` ถูกตาม enum จริง (`AllEnums.cs:366–373`: Purchase=2 · CashReceipts=3 · CashPayments=4) และตรงกับโค้ดเก่า
  `IntegrationService.CreatePaymentJournalAsync` ที่ตั้ง `CashReceipts/CashPayments` (ยืนยันที่ `5c1fe028` `IntegrationService.cs:2813`)
- ไม่ชนเส้นปัจจุบัน: JE รับชำระของ `DocumentService` ใช้คำอธิบาย "ชำระเงิน …" (ไม่ใช่ "รับชำระ ") · เงินปัจจุบันผ่าน `MoneyAccountFallback` (1112x/11340/11113) ไม่ใช่ 112 ·
  หน้าเว็บหนีข้อความด้วย `Layout.esc` (`accountant.html:166–169`) ⇒ ชื่อผังที่ผู้ใช้ตั้งไม่เปิด XSS

### คอมไพล์ (ข้อ 6)
- `HashSet<(Guid BatchId, Guid DocumentId)>` → `IReadOnlySet<(Guid BatchId, Guid DocumentId)>`: `HashSet<T>` implement `IReadOnlySet<T>` (.NET 5+ · โปรเจกต์ net8.0) ✓
- pattern variable ใน `SettlementPostingGate.Evaluate`: `filedPp36` (ระดับเมธอด) · `w`/`prev` (ในบล็อก `if (f.Wallet is { } w)`) — ไม่ชนชื่ออื่น · `next` ในลูปไม่ชนพารามิเตอร์ `next` ของ local function `Add` (คนละ scope ไม่ซ้อนกัน) ✓
- `ToDictionary(b => b.Id.ToString("N"), b => (b.PayoutRef, Dead: …))` — ชื่อ `PayoutRef` อนุมานจาก member access ✓ · `Select(g => (g.Key.DocumentId, Ref: …, Amount: …))` เช่นกัน ✓
- record ที่เพิ่ม optional ท้าย (`SettlementReceiptTarget` · `SettlementPostingFacts`): ทุกจุดสร้าง (service 2 จุด · เทสต์ 6 ไฟล์) อยู่ใน LINQ-to-objects/โค้ดธรรมดา — ไม่มีใน EF expression (CS0854) ✓ ·
  `SettlementWalletPrevious` ถูกสร้างใน EF `Select` แต่ไม่มีพารามิเตอร์ optional ✓
- `SettlementPostingKeys.PaymentMarkerHead` มีจริง (`SettlementPosting.cs:39` `public const string`) ✓ · `TaxType.VatPp36`/`WithholdingTax54` มีจริง ✓ · `Company.IndustryType` non-nullable → cast `(IndustryType?)` ✓
- `DocumentService.PreviewTotals` เป็น `internal static` — เทสต์เรียกได้ผ่าน InternalsVisibleTo ✓

---

## สถานะการแก้ (รอบ 200 ทีม SF · รายงาน `team-SF.md` · คำตัดสิน DECISIONS ข้อ 15, 20, 27)

| ID | สถานะ | ที่แก้ / เหตุผล |
|---|---|---|
| T-1 | ✅ c7bad3f5 | `Helpers/SettlementPosting.cs` `SettlementReceiptWht.Decide` (ไม่มี WHT ⇒ 0 · รับยอดสุทธิที่เหลือครบ ⇒ `null` = งวดสุดท้ายเดิมของ `CreatePaymentAsync` ⇒ 11910 + ลูกหนี้ปิด · รับบางส่วน ⇒ บล็อกพร้อมทางไปต่อ) · `SettlementReceiptTarget.DocumentWht` · `EnsureReceiptAsync` ตัดสินจากข้อเท็จจริงสด · builder ล้มดังเมื่อ Undecidable · เทสต์ `T1_…` 4 ตัว (สองทิศ) |
| T-2 | ✅ c7bad3f5 | `SettlementSummarySupplement.SplitDuplicates` — ทุกบรรทัดของใบสรุปวันนั้นเนื้อหาตรงรอบที่ออกใบแรก (`SettlementContentOverlap` ตัวเดียวกับผู้นำเข้า) ⇒ `SummarySaleDuplicate` · ตรงบางบรรทัด ⇒ ใบเพิ่มเติม · เทสต์ `T2_…` |
| T-3 | ✅ c7bad3f5 | `SettlementWalletContinuity.PickPrevious` — รอบวันเดียวกันที่ปลายรอบ = ต้นรอบนี้ก่อน · ข้ามรอบที่มาหลังตามยอด · ข้อความ Gap บอกกรณีวันเดียวกัน · เทสต์ `T3_…` สองทิศ |
| T-4 | ✅ c7bad3f5 | `SettlementWalletContinuityKind.PreviousHadNoBalances` ⇒ เตือน `WalletContinuityUnknown` ไม่บล็อก · เทสต์ `T4_…` (รอบก่อนมียอดจริงยังบล็อก) |
| T-5 | 📋 | ข้อความ "ต่อจากใบ X" อยู่บนเอกสารที่ออกเลขแล้ว (แก้ย้อนไม่ได้ §86/4) · ยอด/ภาษีถูก · ถ้าจะแก้ต้องมีเส้น "ยกเลิก-ออกแทน" ของใบสรุป (ทีม V1) — ไม่ทำครึ่งเดียว |
| T-6 | ✅ c7bad3f5 | ทางไปต่อ `SummarySaleFirstNotIssued` บอก "ลบร่าง {เลข} / ยกเลิกรอบโอน {ref}" · เทสต์ `T6_…` |
| T-7 | ✅ c7bad3f5 | `LegacyMoneyLegAudit.AdjustedBy` — หลักฐาน = JE อื่นที่โพสต์แล้ว/ไม่ถูกกลับรายการซึ่งช่อง "อ้างอิง" = เลข JE นี้ตรงตัว (ไม่ fuzzy · ไม่อ้างตัวเอง) · `JournalAnomalyService.ScanAsync` ข้าม · ข้อความ Fix บอกวิธี · เทสต์ `T7_…` |
| T-8 | ✅ c7bad3f5 | `SettlementBatchMath.FeeCutoff` เรียง (ปี, เดือน) · เทสต์ `T8_…` |
