# BACKLOG_REGISTER_2026-10-26 — ทะเบียนงานคงค้างรอบ 205 (2026-10-22 → 26) + คำตัดสินที่เสนอ + แพ็กงานทีมพัฒนา

> ทีม W (คำสั่งเจ้าของ 2026-10-26 "ตั้งทีมวิเคราะห์งานคงค้าง และดำเนินการตัดสินใจ และพัฒนาให้เสร็จสิ้นทั้งหมด") ·
> ฐาน: `claude/booking-service-features-sdemob` @ `492e8b74` · ทุกข้อ**เปิดไฟล์ยืนยันแล้ว** (ไม่ใช่อ่านจาก CHANGELOG อย่างเดียว — จุดที่ grep แล้วไม่พบจดไว้ว่า "ไม่พบ") ·
> หลักที่ใช้ตัดสิน: `DECISION_DOCTRINE.md` §1–§3 (G1–G7 · ทิศความเสียหาย · R1 ห้ามประทับสถานะปลายทางเอง) · คำตัดสินเจ้าของ 116–158 (`erp-review/2026-09-29/DECISIONS.md`) ·
> หลักที่เจ้าของย้ำ: **(ก) "เปิดให้ตั้งค่าได้"** (ข้อ 150/155/156/157) · **(ข) "ของเดิมที่ดีอย่าแก้ให้แย่"** (ทีม R/T) · **(ค) "ข้อมูลครบถูกต้องที่สุด"** (ข้อ 158) · **(ง) "ระบบต้องไม่ปล่อยผ่าน"** (ทีม S — ด่าน `INT-DEPOSIT-OVERDRAW`) ·
> ไฟล์นี้เป็น "สถานะปัจจุบัน" (F4 ข้อ 5) — ทีมพัฒนาที่ปิดแพ็กให้ติ๊ก `✅ <sha>` ในคอลัมน์สถานะ ไม่ลบแถว

## 0. สรุป 30 วินาที

| | จำนวน |
| --- | --- |
| รายการคงค้างที่รวบรวม (R-01…R-66) | **66** |
| ตรวจแล้ว **ปิดได้เลย** (แก้แล้ว / ไม่ใช่บั๊ก) | 4 (R-55 R-57 R-64 R-65) + 1 ย้ายไป manual (R-66) |
| **ตัดสินเองได้** จากหลักข้างบน (จัดเข้าแพ็ก W1–W10 = 40 · "ตั้งใจไม่ทำรอบนี้" = 16) | 56 |
| **ต้องถามเจ้าของ/นักบัญชี** (คำถามเลือกตอบ §4) | 5 คำถาม (Q1–Q5) → ผูกกับ R-33 · R-23/R-24 · R-53 · R-41(B1) · R-08 (เฉพาะค่าเริ่มต้น — งานไม่รอ) |
| แพ็กงานทีมพัฒนา (ขอบเขตไฟล์ไม่ทับกัน · ทำขนานได้) | **10** (§3) |
| ตั้งใจไม่ทำรอบนี้ (มีเหตุผล) | 16 (§5) |
| ต้อง manual test โดยเจ้าของ | 14 ข้อ (§6) |

ขนาด: S = ≤ ½ วัน · M = 1–2 วัน · L = > 2 วัน / ต้องฝ่ายค้าน

---

## 1. ทะเบียนรายการ (ต้นทาง · ผลกระทบ · ขนาด · ขึ้นกับ · คำตัดสินที่เสนอ · แพ็ก)

ผลกระทบ: 💰 เงิน/บัญชี · 🧾 ภาษี/กฎหมาย · 👤 ผู้ใช้เห็น · ⚙️ เทคนิค/วินัย · 🔐 สิทธิ์/ความปลอดภัย

### 1.1 เส้นเงิน CMS + โควตา (`docs/BOOKING_REVENUE_AUDIT.md` ทีม E)

| # | รายการ | ต้นทาง | ผล | ขนาด | ขึ้นกับ | คำตัดสินที่เสนอ (เหตุผล) | แพ็ก |
| --- | --- | --- | --- | --- | --- | --- | --- |
| R-01 | **G-2** ปุ่ม "ส่งต่อใหม่" (re-dispatch) ให้ handler ที่ล้มหลังเงินเข้า — ตรวจแล้ว `PaymentIntentService.DispatchSucceededAsync` ยิงครั้งเดียวตอน Succeeded · ไม่มี endpoint re-dispatch ใด (grep `redispatch|ส่งต่อใหม่` = 0) | BOOKING_REVENUE_AUDIT §4 | 💰👤 | S | handler ทุกตัว idempotent แล้ว (L-1/S-1) | **ทำ** — `POST payment-gateway/intents/{id}/redispatch` (สิทธิ์พนักงานการเงิน · `RejectApiKey`) เรียก `DispatchSucceededAsync` เดิมตัวเดียว (ไม่เขียนสำเนา) · เงื่อนไข: intent = Succeeded และยังมี event "⚠️ ยังไม่ได้บันทึก" · audit ผู้กด · ผลลัพธ์ล้มดัง 3 ที่ (F2 #7) · ปุ่มในหน้ารายการรับชำระออนไลน์ เฉพาะแถวที่ `PaymentPendingAction` บอกว่าค้าง (server computes) | W1 |
| R-02 | **L-2** คืนเงินผ่าน gateway สำเร็จแต่ยังไม่ออกใบลดหนี้ ⇒ ไม่มีป้ายในคิว "ต้องคืนเงินแขก" | §1 | 💰🧾 | S | — | **ทำ** — หลัง `GatewayRefundService` สำเร็จของ intent ที่ผูกการจอง/ออเดอร์ ⇒ ประทับธง `RefundedAtGatewayAt` + แสดง "คืนที่ gateway แล้ว · ยังไม่ออกใบลดหนี้" · **ไม่ออกใบลดหนี้เอง** (R1 — หลักฐานคืนมีแล้วแต่ "ยืนยันคืนเงินแล้ว" เป็นการตัดสินของคน · ทางไปต่อ = ปุ่มเดิม) | W1 |
| R-03 | **Q-2** โควตา: ใบมัดจำใบที่ 2+ ของ `BookingNumber` เดียวกัน · เงินประกัน `RefundableSecurity` · ใบกำกับยอดริบ · ใบแทน (`ReissueFinalDocumentAsync`) ถูกนับเพิ่ม ทั้งที่ LICENSING §4 = "1 การเข้าพัก = 2 หน่วย" / "นับเฉพาะเอกสารที่มีรายได้" | §5 | 💰👤 | M | — | **ทำ** — เพิ่มข้อเท็จจริงเข้า `DocumentQuotaPolicy.Classify` (มีใบมัดจำของการจองนี้แล้ว · ลักษณะมัดจำ · อ้างใบมัดจำ · `isReplacement`) ⇒ `NotCounted` · ตัวตัดสินเดิมตัวเดียว ไม่เพิ่มสำเนา · เทสต์ทิศตรงข้าม: ใบมัดจำใบแรก/ใบกำกับเช็คเอาต์ยังนับเท่าเดิม · **ไม่คืนโควตาย้อนหลัง** (ตัวนับไม่ลด — LICENSING §13.1) | W1 |
| R-04 | **Q-4** นับโควตาตอน `CreateDocumentAsync` (รวมใบร่าง) ⇒ การจอง PrePayment ที่ไม่เคยจ่าย (ใบร่างหลัง B-1) และ Appointment กินโควตา | §5 | 💰👤 | L | R-03 | **ทำเป็น 2 ขั้น** — ขั้น 1 (รอบนี้): ใบร่างที่สร้างจากเส้นอัตโนมัติ (`OriginModule` ∈ CmsBooking/SiteOrder) = `NotCounted` จนอนุมัติ → นับตอนอนุมัติ (ตรงหลัก "เลขเอกสารออกตอน Approve") · ขั้น 2: ย้ายทุกชนิดไปนับตอนอนุมัติ **เฉพาะเมื่อ** รายงานเทียบก่อน-หลังทั้งระบบ (`tools/` สคริปต์อ่าน DB อย่างเดียว) ไม่ทำให้บริษัทใดนับ*เพิ่ม* · ไม่แตะตัวนับย้อนหลัง | W1 |
| R-05 | **Q-6** `CurrentMonthDocuments++` อ่าน-แก้-เขียนบน entity ⇒ นับขาดเมื่อสองใบชนกัน | §5 | 💰 | S | — | **ทำ** — `UPDATE … SET n = n + 1 RETURNING` ในธุรกรรมเดียวกับการสร้างใบ · ล็อกลำดับ: นับหลังใบ `SaveChanges` สำเร็จในธุรกรรมเดียว (ใบล้ม = ไม่นับ คงเดิม) · Db test `Category=Db` สองคำขอพร้อมกัน ⇒ +2 | W1 |
| R-06 | **S-2** ปิดปุ่มจ่ายออนไลน์ของออเดอร์ขณะมีสลิปรอตรวจ (แบบที่พัก) — ตอนนี้กันซ้ำที่ `ConfirmPaymentAsync` แล้ว แต่ลูกค้ายังกดจ่ายซ้ำได้ (เงินเข้า 2 ทาง → ต้องคืน) | §2 | 💰👤 | S | — | **ทำ** — `ResolveOrderAsync` คืนสถานะ "สลิปรอตรวจ" ⇒ หน้าเว็บซ่อนปุ่ม + ข้อความ "ร้านกำลังตรวจสลิป" · เปิดให้จ่ายได้เมื่อสลิปถูกปฏิเสธ · ด่านฝั่งเซิร์ฟเวอร์ (ไม่ใช่แค่ซ่อนปุ่ม) ปฏิเสธสร้าง intent ใหม่ `SITE-ORDER-SLIP-PENDING` | W2 |
| R-07 | **S-3** ยกเลิกออเดอร์ที่ชำระแล้ว ⇒ void ใบกำกับที่มีการชำระผูก ⇒ ถูกบล็อก ⇒ `[ERP-VOID-FAILED]` ค้างทำมือ | §2 | 💰🧾 | M | R-02 | **ทำ** — ยกเลิกออเดอร์ที่ชำระแล้ว ⇒ **เสนอ**ใบลดหนี้ §86/10 (เหตุ "ยกเลิกการขาย" · อ้างใบเดิม · ร่าง) + ลิงก์ refund gateway · **ไม่อนุมัติใบลดหนี้เอง** (R1 — เงินยังไม่คืน) · ตัวตัดสิน pure `Helpers/SiteOrderCancelPlan` (ใบยังไม่มีการชำระ ⇒ void เดิม · มีการชำระ ⇒ CN ร่าง) · เทสต์ทิศตรงข้าม: ออเดอร์ที่ยังไม่จ่ายยัง void ได้เหมือนเดิม | W2 |
| R-08 | **B-4** ชำระมัดจำออนไลน์ของการจองนัดหมาย (CMS booking) — ไม่มี `PaymentSourceKind.SiteBooking`/resolver/handler/หน้าแขก ⇒ โหมด PrePayment ได้แค่ใบร่างค้าง | §3 · BOOKING_ENGINE B5 | 💰🧾👤 | L | PAYMENT_GATEWAY_DESIGN · B-1 (ทำแล้ว) | **ทำ** — ครบ 4 ชิ้น: `PaymentSourceKind.SiteBooking` · `PublicPaymentResolver` (token การจอง · ยอดจาก server เท่านั้น) · `Services/Payments/Handlers/SiteBookingPaymentHandler` ตั้ง `PaidAmount` + `PaymentSourceKind`/เลขอ้างอิง `GatewayPaymentRecord` แล้วเรียก `SyncBookingToErpAsync` (ด่าน `CmsBookingErpPolicy.AutoApprove` อนุมัติเองเมื่อครบ) · hold รอบ N นาที (`BookingSlotEngine` รับสถานะ Pending+หมดอายุ) · ซ้ำ = เลขอ้างอิงเดิม · เทสต์ทิศตรงข้าม: PrePayment ที่ไม่จ่ายยังเป็นร่าง · Instant/Guaranteed ไม่เปลี่ยน | W2 |
| R-09 | ส่วนที่เหลือของ PrePayment (ราคาเต็ม − มัดจำ) ไม่มีเอกสาร/รายได้/VAT (ค้างรอบ 194 P1-3) | §3 | 💰🧾 | M | R-08 | **ทำหลัง R-08** — เมื่อ "ให้บริการเสร็จ" (`DecideOnComplete`) ⇒ ใบกำกับ/ใบเสร็จส่วนที่เหลือหักฐานมัดจำ (`DepositBaseDeducted` · ตัวเดียวกับเช็คเอาต์ที่พัก/MTO ข้อ 156) · tax point = วันใช้บริการ §78/1 · ไม่ออกถ้าใบมัดจำยังร่าง (ข้อความ "อนุมัติใบมัดจำก่อน" เดิม) | W2 |
| R-10 | **B2** ลูกค้ายกเลิก/เลื่อนนัดเอง + คิดค่าธรรมเนียมตามนโยบาย (`FreeCancellationHours`/`CancellationFeePercent` ตอนนี้แสดงเฉย ๆ) | BOOKING_ENGINE §3 B2 | 💰👤 | M | R-08 (มัดจำ) | **ทำ** — ลิงก์โทเคน (แบบ `booking-manage.html`) · ตัวตัดสิน pure `Helpers/BookingCancellationFee` เพดาน = มัดจำที่รับจริง (ข้อ 126 เดียวกับที่พัก) · ไม่มีมัดจำ ⇒ บันทึกยอดค้างใน CRM ไม่ออกใบเอง (R1) · เลื่อนนัดผ่าน `BookingSlotEngine.Decide` ตัวเดียว ใต้ล็อกเดิม | W2 |
| R-11 | **B4** no-show fee — กด "ลูกค้าไม่มา" ⇒ ริบมัดจำตามนโยบาย | §3 B4 | 💰🧾 | S | R-10 | **ทำ** — ใช้ `RealizeDepositAsync(ForfeitAs)` เดิมของศูนย์มัดจำ (ไม่มี posting ใหม่) · ริบ ≤ มัดจำ (ข้อ 126) · ไม่มีมัดจำ ⇒ CRM เท่านั้น | W2 |
| R-12 | **B3** reminder ก่อนนัด (อีเมล/LINE/SMS 24 ชม. + 2 ชม.) — คอลัมน์ `ReminderSent/ReminderSentAt` มีแต่**ไม่มีผู้เขียน** (grep Services = 0) | §3 B3 | 👤⚙️ | M | — | **ทำ** — `BackgroundService` ผ่าน `Helpers/JobLock` (F4 #6) + watermark ต่อบริษัท · เทมเพลตต่อเว็บตั้งค่าได้ (หลัก ก) · ประทับ `ReminderSentAt` **หลังส่งถึงจริง**เท่านั้น (R1 · F2 #3) · ส่งล้มไม่ล้มงาน + ร่องรอยต่อแถว | W3 |
| R-13 | **B6x** คิวสำรอง: แจ้งลูกค้าอัตโนมัติเมื่อได้คิว · ถือรอบ N นาทีให้ตอบรับ · คิวสำรองข้ามรอบ | §3 B6 | 👤 | M | R-12 | **ทำเฉพาะ "แจ้งเมื่อได้คิว"** บนช่องส่งของ R-12 · ถือรอบ N นาที = ตั้งค่าได้ต่อไซต์ (`SiteBookingSettings.WaitlistHoldMinutes` · 0 = ไม่ถือ) · คิวสำรองข้ามรอบ → §5 (ไม่ทำรอบนี้) | W3 |
| R-14 | **B13** เวลาทำการระดับเว็บ (ตั้งครั้งเดียวใช้ทุกบริการ) | §3 B13 | 👤 | S | — | **ทำ** — `SiteBookingSettings.BookingHoursJson` เป็นค่าตั้งต้น · บริการ override (`UseSiteDefaults` มีแล้ว) · resolver กลางตัวเดียว (`BookingSlotEngine.RulesOf`) ห้าม `??` เอง (กฎ #4 A) · เทสต์: บริการที่ตั้งเวลาเองไม่ถูกแตะ | W3 |
| R-15 | **B14** Db test การจองพร้อมกัน (BKE-10/BKE-16 ⏳) | §3 B14 · TEST_PLAN BKE | ⚙️ | S | job db-test | **ทำ** — `Db/CmsBookingDoubleBookingDbTests` (แบบ `LodgingDoubleBookingDbTests`) · N คำขอรอบสุดท้าย ⇒ 1 ใบ + 409 `CMS-BOOKING-FULL` · ยกเลิก ⇒ คิวสำรองใบแรกเลื่อนขึ้น | W3 |
| R-16 | **B12** ปฏิทินมุมมองพนักงาน day/week + ลากเลื่อนนัด · **POS §4 #7** หน้าจัดการการจอง CMS ไม่มีช่องเลือกช่าง | §3 B12 · POS_MTO §4 #7 | 👤 | M | B1x (ทำแล้วบางส่วน) | **ทำเฉพาะ**: ช่องเลือกผู้ให้บริการ (`AssignedStaffId`) ในโมดัลการจอง `cms-bookings.html` ผ่าน `BookingSlotEngine.PickResource` ตัวเดียว (ห้ามข้ามด่าน) · มุมมองปฏิทิน/ลากเลื่อน → §5 | W3 |

### 1.2 หัก ณ ที่จ่ายตามรายการ + FX (`docs/PER_LINE_WHT_DESIGN.md` §8 ทีม F)

| # | รายการ | ต้นทาง | ผล | ขนาด | ขึ้นกับ | คำตัดสินที่เสนอ | แพ็ก |
| --- | --- | --- | --- | --- | --- | --- | --- |
| R-17 | **B-1** OCR DTO ต่อบรรทัด (`OcrLineItemDto.WithholdingTaxRate/IncomeTypeCode`) ให้หน้ารีวิวสแกนเห็นแผนก่อนสร้าง | §8 B-1 | 🧾👤 | M | `PerLineWhtEnabled` | **ทำ** — ค่าจาก `OcrLineWhtPlan` ตัวเดิม (ไม่คำนวณใหม่ในหน้า) · คอลัมน์ WHT ใน `document-scan.html` เฉพาะเมื่อค่าตั้งเปิด · ทุกช่องที่ผู้ใช้แก้ → `RecordUserChoiceAsync` (กฎเหล็ก #3 ข้อ 5) · ขอบเขตแตะ `document-scan.html` เฉพาะคอลัมน์ WHT (ไม่ชน W8) | W7 |
| R-18 | **B-4** ใบลดหนี้ต่างสกุลที่ปรับ 50 ทวิ (`WhtCertAmountLine` · `WhtCreditNoteRemainder`) ยังคิดสกุลเอกสาร | §8 B-4 | 🧾💰 | S | — | **ทำ** — ใช้ `PerLineWht.ForDocument` ของใบลดหนี้ (ตัวตั้งเดียว) · เทสต์: ใบบาทไม่ขยับแม้แต่สตางค์ (ทิศตรงข้าม) | W7 |
| R-19 | **B-5** ใบเบิกพนักงาน `ExpenseClaim` — ประเภทเงินได้ต่อบรรทัด (ตอนนี้มีแค่อัตรา) | §8 B-5 | 🧾 | S | — | **ทำ** — คอลัมน์ `IncomeTypeCode` + migration · อ่านด้วย `PerLineWht` เดียวกัน · ไม่บังคับ (ว่าง = ตามหัวใบ — เหมือน `DocumentLine`) | W7 |
| R-20 | **B-6** ข้อเสนอหักต่อบรรทัดเมื่อหัวใบ**ไม่**มีหัก (กระดาษไม่พิมพ์ยอดหัก) | §8 B-6 | 🧾 | M | `OcrWhtThresholdBase` · `OcrMinorityWhtPart` | **ทำเป็น "ข้อเสนอ" เท่านั้น** (warn-gate — ไม่มีเกณฑ์ขั้นต่ำ · DOCTRINE 2.3) · ระบบ**ไม่หักให้เอง** (คำตัดสินเดิม) · ผ่านเกณฑ์ 1,000 · ป้าย "⚙️ ระบบแนะนำ" · เทสต์ทิศตรงข้าม: ใบที่หัวมีหักแล้วไม่ถูกเสนอซ้ำ | W7 |
| R-21 | **B-2** JE ขา 2191x ของใบต่างสกุลใช้ `PerLineWht` (ฐานบาท × อัตรา) แทน `ToBaht(WHT สกุลเอกสาร)` — ต้องแก้ขาเจ้าหนี้/FX squeeze ใน `AutoPostToJournalAsync` พร้อมกัน | §8 B-2 | 💰🧾 | L | R-18 | **ทำท้ายแพ็ก + ฝ่ายค้านบังคับ** — 50 ทวิ กับ GL ต้องเท่ากันถึงสตางค์ (ตอนนี้ TieOut ต่างระดับเศษ) · golden `ApprovalWarningGolden`/JE ใบต่างสกุลก่อน-หลัง · ใบบาททุกใบ = 0 เปลี่ยน · ถ้าฝ่ายค้านไม่ผ่าน → ถอยเป็น backlog รอบถัดไป (ไม่ merge ครึ่ง) | W7 |
| R-22 | **B-7** ตาราง DTA (`DtaTreatyRates`) ว่างโดยตั้งใจ ⇒ ม.70 เต็มอัตราเสมอ | §8 B-7 · CLAUDE.md E | 🧾 | — | ตัวบททางการ | **ตั้งใจไม่ทำ** (รอข้อมูลภายนอก — ข้อ 150 "รอข้อมูลภายนอก") | §5 |
| R-23 | **B-3** อัตรา ณ วันจ่ายสำหรับฐาน WHT 50 ทวิ รายงวด (`Payment.ExchangeRate`) + ผลต่าง FX ลง `FxGainLossAccountId` | §8 B-3 | 🧾💰 | M | นักบัญชี | **ถามเจ้าของ/นักบัญชี (Q2)** — เปลี่ยนความหมายฐานภาษีของใบที่ออกแล้ว ยังไม่มีหลักจากเจ้าของ | §4 |
| R-24 | **B-8** อัตรา ธปท. ที่ใช้กับ VAT (อัตราซื้อถัวเฉลี่ย วันทำการก่อน tax point) vs mid-rate ที่ `IBotExchangeRateService` คืน | §8 B-8 | 🧾 | M | นักบัญชี | **ถาม (Q2 ข้อเดียวกัน)** | §4 |

### 1.3 POS + สินค้าตามสั่ง (`docs/POS_MTO_DESIGN.md` §3.6/§4 ทีม K/Q)

| # | รายการ | ต้นทาง | ผล | ขนาด | ขึ้นกับ | คำตัดสินที่เสนอ | แพ็ก |
| --- | --- | --- | --- | --- | --- | --- | --- |
| R-25 | **§4 #8** ล้างรูปตัวอย่างของตะกร้าที่ไม่ได้สั่ง (`EntityId = cartId` ค้างตลอดไป) | §4 #8 · ฝ่ายค้าน K ข้อ 4 | ⚙️ | S | `IFileAttachmentService` | **ทำ** — งานเบื้องหลังผ่าน `Helpers/JobLock` · ลบไฟล์ `EntityType=SiteOrder` ที่ `EntityId` ไม่อยู่ใน `SiteOrders` และ `CreatedAt` เกิน `N` วัน (ค่าตั้งแพลตฟอร์ม · ค่าเริ่มต้น 14) · ลบผ่าน `IFileAttachmentService` ตัวเดียว (ห้าม `File.Delete` เอง) · ทิศตรงข้าม: รูปของตะกร้าที่กลายเป็นออเดอร์ไม่ถูกลบ | W4 |
| R-26 | **§4 #5** `ServicePackage.RevenueAccountId` ไม่มีผู้อ่าน (ค่าตั้งมีช่องแต่ไม่มีผล — F2 #2) | §4 #5 | 💰 | S | `PosRevenueSplit` | **ทำ** — ตรึงลง `PosOrderItem.RevenueAccountId` ตอนเพิ่มบรรทัดแพ็กเกจ (ออเดอร์เดิมไม่เปลี่ยน · JE ใบที่ปิดแล้วไม่แตะ) · เหตุผล: "เก็บแล้วต้องมีผล" คือกฎเหล็ก #4 F2 #2 — ผู้ที่ตั้งค่าไว้ตั้งใจให้มีผล · บอกบนหน้าตั้งค่าว่ามีผลกับบิลใหม่เท่านั้น | W6 |
| R-27 | **§4 #6** `StaffCommissionSummaries` ไม่มีผู้เขียน ⇒ แท็บค่าคอมใน `pos-reports.html` ว่างเสมอ | §4 #6 | 👤 | S | `PosStaffSales` | **ทำ** — แท็บอ่านจาก `GET pos/reports/staff-sales` (ฐานใหม่ของทีม K) · ตาราง/entity เดิมคง schema (ไม่ลบ) แต่หน้าไม่อ่านอีก · HTTP 200 ของว่าง = โกหก (F2 #7) ⇒ ต้องแก้ไม่ใช่ปล่อย | W6 |
| R-28 | **§4 #1** รายการพิเศษ "ยกเว้น VAT" รายบรรทัดบน POS — ต้องแก้ 3 ชั้น (ยอดบิล · `PosTaxInvoiceLines` · รายงานภาษีขายแยกคอลัมน์) | §4 #1 | 🧾 | L | — | **ไม่ทำรอบนี้** — ทำครึ่ง = ใบกำกับยอดไม่ตรง · หน้าจอบอกตรง ๆ ว่ายังไม่รองรับ (ไม่มีช่องที่ไม่มีผล) ⇒ ไม่ผิดกฎหมาย (คิด 7% = ทิศเสียเกิน G5) | §5 |
| R-29 | **§4 #2** หักมัดจำของการจองในบิล POS (ตอนนี้บล็อก + ทางไปต่อ) | §4 #2 | 💰 | L | API ศูนย์มัดจำ | **ไม่ทำรอบนี้** — มีด่าน+ทางไปต่อแล้ว (ข้อ 150 "บล็อกได้เมื่อมีทางไปต่อ") · ต่อสายเมื่อศูนย์มัดจำมี API "ตัดมัดจำเข้าใบขาย POS" ตัวเดียว | §5 |
| R-30 | **§4 #3** ค่าส่งตามระยะทาง · **#4** ตัวเลือกสินค้าบน POS · **#9** ใบสั่งผลิต+แจ้งพร้อมรับ | §4 | 👤 | M–L | — | **ไม่ทำรอบนี้** (ฟีเจอร์ใหม่ · ไม่มีผู้ใช้รายงาน · ไม่แตะเงิน/กฎหมาย) | §5 |
| R-31 | **§3.6 ข้อ 7 ยังไม่ทำ**: ยกเลิกหลังมัดจำ ไม่คืน/ริบอัตโนมัติ · ใบมัดจำของบิลหลายอัตรา VAT | §3.6 | 💰🧾 | — | — | **ตั้งใจไม่ทำ** — ข้อ 156 ตัดสินแล้ว: ไม่ยกเลิก/ริบเอง (R1 — ต้องมีหลักฐานคืน/เงื่อนไขริบจากคน) · บิลหลายอัตรา = แบบเดิม + หมายเหตุ | §5 |
| R-32 | **§3.6 ข้อ 8** VAT ค่าส่งบิลปนแบ่งตามสัดส่วน (ตอนนี้ 7% = อัตราสูงสุดในบิล) | §3.6 ข้อ 8 | 🧾 | S | นักบัญชี | **ตั้งใจไม่ทำ** — 7% คือทิศปลอดภัย (G5 เสียเกินแก้ได้ · เสียขาดถูกประเมิน) · เปลี่ยนเมื่อนักบัญชียืนยันเท่านั้น (ไม่ตั้งเป็นคำถามเจ้าของ — ไม่ใช่เรื่องนโยบาย) | §5 |

### 1.4 OCR ยอดรวม / e-Tax (`docs/OCR_TOTALS_MATRIX.md` ทีม R/T)

| # | รายการ | ต้นทาง | ผล | ขนาด | ขึ้นกับ | คำตัดสินที่เสนอ | แพ็ก |
| --- | --- | --- | --- | --- | --- | --- | --- |
| R-33 | **§4** ส่วนลดท้ายบิล (เคส C/E/F/G) ถูกกระจายลงบรรทัด ⇒ เอกสาร "ตรงตัวเลข แต่หน้าตาต่างกระดาษ" (576 ต่อบรรทัด vs 538.32 ท้ายบิล) — ทางเลือก `BillDiscountAmount` คำนวณแล้ว ยอดเท่าเดิมทุกสตางค์ | §4 | 🧾👤 | L | §H ก่อน-หลังทั้งชุด | **ถามเจ้าของ (Q1)** — เปลี่ยนรูปการบันทึกของ**ทุกใบ**ที่มีส่วนลดท้ายบิล (ของเดิมที่ตัดสินถูกจะ "เปลี่ยนหน้าตา") ขัดกันระหว่างหลัก (ข) กับ (ค) · เสนอ**ตัวเลือก ข** (เฉพาะใบที่พิสูจน์ส่วนลดท้ายบิลจากหัวใบ/XML — e-Tax · `OcrBillDiscount` พบแถวส่วนลดบนกระดาษ) | §4 → W8 |
| R-34 | **§5** ใบ SP-IN2026-10-00933 ยังไม่เข้า `OcrPaperSamples` (golden คู่สัญญาต้องรัน dotnet) | §5 | ⚙️ | S | CI/SDK | **ทำ** — ตามขั้น 4 ข้อใน §5 · ทำบน branch แล้วอ่าน golden snapshot จาก CI log (`get_job_logs`) มาวาง — ห้ามเขียนจากความจำ (F2 #6) | W8 |
| R-35 | **#10** รูป F/G + หลายอัตรา + ไม่มีตาราง ภ.พ. ⇒ Ambiguous `[Σ-GAP]` ให้คนตัดสิน | §3 #10 | 🧾 | — | กระดาษจริง | **ตั้งใจไม่ทำ** — ถูกต้องตาม G3 "ไม่รู้ = บอกว่าไม่รู้" · ไม่เดาส่วนลดข้ามอัตรา | §5 |
| R-36 | **#15** XML รวม VAT ต่อบรรทัด + AllowanceTotal รวม VAT ท้ายบิล ⇒ `[ETAX-HEADER-GAP]` (สังเคราะห์ · ไม่มีใบจริง) | §3 #15 | 🧾 | — | กระดาษจริง | **ตั้งใจไม่ทำ** — ไม่เพิ่มกติกาจากใบสังเคราะห์ (§H "เทสต์จากกระดาษจริง") · เก็บเป็นช่องว่างที่รู้ตัว | §5 |
| R-37 | เคส G `DiscountInclVatOnExclusivePrices` ล็อกด้วยเทสต์สังเคราะห์ · replay/golden harness ยังรอ CI | §3 #4 · §baseline | ⚙️ | S | CI | **ทำ** — ติดตาม CI รอบแรกหลัง merge: `OcrReplayLineGoldenTests`/`EtaxGoldenCorpusTests`/`OcrVatCodeLinesTests` ต้องเขียว · ถ้าแดง = เคส G เปลี่ยนใบที่เดิมถูก ⇒ ถอดเคส G (หลัก ข) ไม่ใช่แก้ golden | W8 |

### 1.5 คอร์ส / เครดิต (`docs/COURSE_CREDIT_DESIGN.md` §5 ทีม L)

| # | รายการ | ต้นทาง | ผล | ขนาด | ขึ้นกับ | คำตัดสินที่เสนอ | แพ็ก |
| --- | --- | --- | --- | --- | --- | --- | --- |
| R-38 | **B3** คืนเงิน**บางส่วน** (ลดเครดิตบางส่วนแล้วใช้ต่อ) + ปุ่มออกใบลดหนี้จากหน้าบัตร — ตอนนี้ `CreditPassDocumentRule` ให้ CN เฉพาะยอด = ส่วนที่ยังไม่รับรู้ทั้งก้อน แล้วปิดบัตร | §5 B3 | 💰🧾 | M | `CourseCreditMath` | **ทำ** — กติกาใหม่ใน `CreditPassDocumentRule` (ตัวเดียว): CN ยอด `x` ≤ `Deferred` ⇒ ลดเครดิต `floor(x ÷ ราคาต่อเครดิตตามสัญญา)` และ **เครดิตคงเหลือหลังลดต้องไม่ต่ำกว่าที่ใช้+จองไว้** · บัตรยัง Active · แถว Adjust ในสมุด · ปุ่มออก CN ร่างจากหน้าบัตร (ไม่อนุมัติเอง) · ทิศตรงข้าม: CN เต็มส่วนที่เหลือยังปิดบัตรเหมือนเดิม · แตะเงิน ⇒ ฝ่ายค้าน | W6 |
| R-39 | **B4** ต่อสายเครื่องจองของทีม B เข้า `ICreditBookingLedger` (ห้ามแก้ slot logic) | §5 B4 · BOOKING_ENGINE §2.1c | 💰👤 | M | ทีม B merge แล้ว | **ทำ** — ตอนสร้างการจองที่เลือก "ใช้บัตร" ⇒ `Reserve` ในธุรกรรมเดียวกับ `Decide` · ใช้บริการเสร็จ ⇒ `Consume` · ยกเลิก ⇒ `Release` · ห้ามแตะ `BookingSlotEngine` · เทสต์: การจองไม่ใช้บัตรไม่แตะสมุด | W6 |
| R-40 | **B6** `RevenueRecognitionService` หาผังรายได้รับล่วงหน้าด้วย `21810` → `StartsWith("218")` = กลุ่ม "ค่าใช้จ่ายบุคลากรค้างจ่าย" ของผังมาตรฐาน (ตรวจแล้ว `RevenueRecognitionService.cs:341-343`) | §5 B6 | 💰 | S | — | **ทำ** — resolver ผังรายได้รับล่วงหน้าตัวเดียว (`Helpers/DeferredRevenueAccount` — 21713/217xx จาก `DepositKind`/ค่าตั้ง) · ไม่มี fallback ด้วย prefix ที่ความหมายผิด · ไม่รู้ = บอกไม่รู้ (ล้มดัง) ไม่ใช่ลงผังผิด (G3) · ตรวจ JE เดิมที่ลง 218xx ⇒ รายงานตรวจข้อมูล (อ่านอย่างเดียว) | W6 |
| R-41 | **B1** ขายคอร์สผ่าน POS (บรรทัดคอร์ส Cr 21713 แทน 41000 + `OpenPass`) · **B2** ขายผ่านร้านออนไลน์ | §5 B1/B2 | 💰🧾 | L | R-40 | **ทำ B2 ก่อน** (จุดต่อ `ActivateFromDocumentAsync` มีแล้ว · ใบขาย `SiteOrderPaymentHandler` ลง 21713 เมื่อสินค้าเป็นคอร์ส) · **B1 POS = ถามเจ้าของ (Q4)** ว่าจะขายคอร์สที่ POS ไหม (แตะ JE ขาย/คืนของ POS ทั้งคู่ — ถ้าไม่มีความต้องการจริง ไม่ควรแตะของเดิม) | W6 / §4 |
| R-42 | **B5** ขยายอายุ/โอนเครดิต/แพ็กเกจหลายบริการแยกจำนวน · **B8** portal สมาชิกดูยอด · **B7** integration test DB จริง | §5 | 👤 | M–L | PDPA | **ไม่ทำรอบนี้** (ยังไม่มีผู้ขอ · B8 ต้องผ่าน PDPA/สิทธิ์ portal · B7 env ไม่มี Testcontainers — ใช้ job db-test เมื่อเขียน) | §5 |

### 1.6 CMS เว็บไซต์ (`docs/CMS_FEATURE_ROADMAP.md` §6 ทีม C + รายงานทีม)

| # | รายการ | ต้นทาง | ผล | ขนาด | ขึ้นกับ | คำตัดสินที่เสนอ | แพ็ก |
| --- | --- | --- | --- | --- | --- | --- | --- |
| R-43 | **ไฟล์ CMS ยังเขียน disk ตรง** — ตรวจแล้ว `CmsContentService.cs:550` (`uploads/cms/{companyId}/{siteId}`) และ `CmsCommerceService.cs:1995-2012` (`wwwroot/uploads/order-slips`) ใช้ `Directory.GetCurrentDirectory()` ขณะที่ `IFileAttachmentService` (ที่เก็บไฟล์กลาง · ทีม K ใช้แล้วกับรูปตัวอย่าง) มีอยู่ | รายงานทีม C · กฎเหล็ก #4 D "ไฟล์ห้ามเขียน local disk ตรง" | ⚙️🔐 | M | `IFileAttachmentService` | **ทำ** — ย้ายสื่อ CMS + สลิปออเดอร์ไป `IFileAttachmentService` (`EntityType=SiteMedia/SiteOrder`) · URL สาธารณะผ่าน endpoint เสิร์ฟของมัน (ตรวจเว็บ+บริษัท) · **ไฟล์เดิม**: migration อ่านตำแหน่งเก่าได้ต่อ (`PublicUrl` resolver รองรับทั้งสองรูป) ไม่ย้ายไฟล์เงียบ ๆ (F2 #9) · `tools/` checker ratchet "Path.Combine(GetCurrentDirectory" ใน Services ⇒ 0 จุดใหม่ | W4 |
| R-44 | **OG/meta ด้วย JS** — `storefront.html:505-508` ตั้ง `og:image`/description หลัง fetch ⇒ LINE/Facebook crawler ไม่รัน JS ⇒ แชร์ลิงก์ร้านไม่มีรูป/ชื่อ (roadmap §6 #1 "เสียยอดขายตรง ๆ") | roadmap §6 #1 · รายงานทีม C | 👤 | M | `CmsRenderingService` | **ทำ** — SSR `<head>` (title/description/og:*/canonical/JSON-LD) ที่เส้นเสิร์ฟ storefront ต่อหน้า (เซิร์ฟเวอร์เรนเดอร์จาก `SitePage` เดิม · HtmlEncode ทุกช่อง) · JS เดิมคงไว้สำหรับเปลี่ยนหน้าแบบ SPA · เทสต์: หน้าที่ไม่มี og image ไม่ใส่แท็กว่าง | W4 |
| R-45 | **"ทุกไซต์" หน้าจองคิว + endpoint ข้ามเว็บฝั่งเซิร์ฟเวอร์** — ตรวจแล้ว `cms-orders.html`/`cms-leads.html` ทำโหมดทุกไซต์**ฝั่ง client** (ยิงต่อเว็บ `PER_SITE_ALL=20`) · `cms-bookings.html` ไม่มีโหมดนี้เลย (grep `allSites` = 0) | รายงานทีม C | 👤⚙️ | M | `CmsSiteContext` | **ทำ** — endpoint ระดับบริษัท `GET companies/{cid}/cms/bookings?siteId=all` (+ orders/leads) กรอง `CompanyId` + เว็บที่ผู้ใช้มีบทบาท (`RequireSiteRole` ต่อแถว) · แบ่งหน้าจริง (ไม่ใช่ 20/เว็บ) · หน้าทั้งสามใช้ endpoint เดียวแทนวนยิง · ปุ่ม action ยังยิงไปเว็บเจ้าของแถว (`actSiteId` เดิม) | W4 |
| R-46 | **§6 #2** ตัวเรนเดอร์บล็อกตัวเดียว (ย้าย Hero/Image/FAQ/Testimonials ไปแบบ `CmsGalleryBlock`) — ปิด defect class "สองตัวเรนเดอร์ drift" | roadmap §6 #2 | ⚙️🔐 | M | — | **ทำ** — ทีละชนิดบล็อก · golden HTML ก่อน-หลังต่อบล็อก (ของเดิมเท่าเดิม · escape ครบ) | W4 |
| R-47 | **§6 #3** JSON-LD FAQ/Product/LocalBusiness อัตโนมัติ | roadmap §6 #3 | 👤 | S | R-44 | **ทำพ่วง R-44** (เซิร์ฟเวอร์ประกอบจากข้อมูลบริษัท/บล็อก · ไม่มีสำเนาใน JS) | W4 |
| R-48 | **§6 #4–#8** Blog · Popup/แชทลอย · อีเวนต์/Before-After/Team · A/B · ประวัติเวอร์ชันหน้า | roadmap §6 | 👤 | M–L | — | **ไม่ทำรอบนี้** (ฟีเจอร์ใหม่ไม่แตะเงิน/กฎหมาย — รอผู้ใช้ขอ · ประวัติเวอร์ชันหน้า (#8) ค่อยทำเมื่อมี `SitePage.Version` ผู้เขียน) | §5 |

### 1.7 ที่พัก — หน้าเคาน์เตอร์ (`LODGING_BOOKING_AUDIT.md` §12 ทีม A · ข้อ 157 ทีม P)

| # | รายการ | ต้นทาง | ผล | ขนาด | ขึ้นกับ | คำตัดสินที่เสนอ | แพ็ก |
| --- | --- | --- | --- | --- | --- | --- | --- |
| R-49 | **ส่วนลดตอนเช็คเอาต์** — folio บล็อกราคาติดลบ · เช็คเอาต์ไม่ส่ง `BillDiscountAmount` (ตรวจแล้ว `LodgingService.Lifecycle.cs:1466` ส่งค่าจาก `create.BillDiscountAmount ?? 0` — มีช่องรับแต่หน้าไม่ส่ง) · ปัญหาจริง = ส่วนลดท้ายบิลปนฐานหักมัดจำ (`PreviewTotals(..., 0m, deducted)`) | §12.2 "ส่วนลด" | 💰🧾👤 | M | `LodgingDepositSettlement.PlanCheckout` | **ทำ — ตัดสินได้จากเลขคณิต ไม่ต้องถาม**: ส่วนลดเป็นส่วนของ*ราคา* · มัดจำเป็น*การชำระ* ⇒ ฐานภาษีใบเช็คเอาต์ = (ราคาเข้าพักหลังส่วนลด) − ฐานมัดจำที่ออกใบกำกับแล้ว · ถ้าติดลบ ⇒ ใบลดหนี้อ้างใบมัดจำ §86/10 เหตุ "ปรับยอด" (ตัวเดียวกับข้อ 157 "ยอดลด = ใบลดหนี้อ้างใบเดิม") · ช่องส่วนลด (จำนวน/%) ในโมดัลเช็คเอาต์ + เหตุผล (audit) · เซิร์ฟเวอร์คิด (`PlanCheckout` รับ `billDiscount`) หน้าแสดง · เทสต์ทิศตรงข้าม: ไม่ใส่ส่วนลด = ตัวเลขเดิมทุกสตางค์ทุกโหมด `TaxInvoiceTiming` · แตะภาษี ⇒ ฝ่ายค้าน | W5 |
| R-50 | ใบรับเงิน**ใบที่ 2+** ของการจอง ไม่มีลิงก์ในโมดัล (ลิงก์เฉพาะ `depositDocumentId` ใบแรก) | §12.2 "ใบเสร็จ/ใบกำกับ" | 👤 | S | — | **ทำ** — รายการเอกสารทั้งหมดของ `BookingNumber` (`LoadDepositSnapshotsAsync` อ่านตาม BookingNumber อยู่แล้ว) ในการ์ดการเงิน · server computes | W5 |
| R-51 | เพิ่ม folio **ก่อนเช็คอิน** (เตียงเสริมแจ้งล่วงหน้า) — เซิร์ฟเวอร์รับแต่ปุ่มขึ้นเฉพาะ CheckedIn เพราะ folio ค้างบนใบที่ถูกยกเลิกทีหลังยังไม่มีกติกา | §12.2 | 💰👤 | S | — | **ทำ** — เปิดปุ่มเมื่อ Confirmed + กติกาตัวเดียว `Helpers/LodgingFolioOnCancel`: ยกเลิก/ไม่มา ⇒ folio ที่ยังไม่ออกใบ = ถูกล้าง (audit) · folio ที่ออกใบแล้ว = อยู่ในใบลดหนี้ตามข้อ 157 · ทิศตรงข้าม: folio ของแขก CheckedIn ไม่ถูกแตะ | W5 |
| R-52 | **ยอดสลิป**ต่อใบ — แขกไม่กรอกยอด · ไม่มีตัวอ่านสลิป ⇒ กรอบแสดง "ยอดที่ต้องโอน" ให้เทียบตา | §12.1 | 👤 | S / L | — | **ทำเฉพาะช่อง "ยอดที่โอน" ให้แขกกรอกตอนส่งสลิป** (ไม่บังคับ · ไม่ใช่หลักฐาน · แสดงคู่ยอดเซิร์ฟเวอร์ · ไม่ประทับว่า "ตรง" เอง — G3) · ตัวอ่านสลิป OCR/QR = **ไม่ทำรอบนี้** (ต้องมี `ILocalDistillationModel` + กฎเหล็ก #1 ครบ 4 ขั้น) | W5 / §5 |
| R-53 | ใบกำกับ**อย่างย่อ** §86/6 สำหรับ walk-in ที่พัก — ไม่มีในเส้นที่พัก (ใบเต็มรูปแทนได้ตามกฎหมาย) | §12.2 | 🧾👤 | M | `AbbreviatedTaxInvoiceRule` · `IsRetailApproved` | **ถามเจ้าของ (Q3)** — ไม่ใช่ช่องโหว่ทางกฎหมาย เป็นความต้องการใช้งาน (ต้องมีอนุมัติขายปลีก) | §4 |
| R-54 | **ค่าปรับยกเลิกไม่ย้ายผัง** (ข้อ 157 "ตัดสินแทนเจ้าของ (แก้ได้)": ค่าปรับคงอยู่ในใบเดิม ผังรายได้ของใบนั้น) | ข้อ 157 ทีม P | 💰🧾 | S | — | **ตั้งใจไม่ทำ (คงตามข้อ 157)** — มัดจำที่ริบเพราะยกเลิกบริการโรงแรม = ค่าบริการที่ต้องเสีย VAT เหมือนเดิม (ไม่ใช่ค่าปรับที่พ้น VAT) ⇒ ผังรายได้เดิมถูกตามภาษี · โหมดเดิมใช้ `RealizeDepositAsync(ForfeitAs: PriceOrFee)` อยู่แล้ว · ถ้าเจ้าของต้องการ "ผังรายได้ค่าปรับ" แยกเพื่อการนำเสนอ ให้เปิดเป็นค่าตั้งผังในรอบถัดไป — **ไม่ตั้งเป็นคำถามรอบนี้** (ไม่กระทบภาษี) | §5 |

### 1.8 Integration (`INTEGRATION_RESYNC.md` §12 ทีม S)

| # | รายการ | ต้นทาง | ผล | ขนาด | ขึ้นกับ | คำตัดสินที่เสนอ | แพ็ก |
| --- | --- | --- | --- | --- | --- | --- | --- |
| R-55 | `/daily-summary` เดินด่าน `INT-DEPOSIT-OVERDRAW` · ล็อกทิศกลับ (`DEP-DRAWN-REMOVE` 409 บน reverse/void) | §12 | 💰 | — | — | **ตรวจแล้ว — ทำแล้ว** (จดใน §12 ตารางแถว 7–8 · ทีม S แก้ฝ่ายค้าน) · เหลือ Db test ทิศกลับใน job db-test (เพิ่มใน W10) | ✅ / W10 |
| R-56 | **"อ้างอิงรูปแบบอื่น"** — มัดจำที่ลงด้วยเลขอ้างอิงไม่ใช่ `RES-…`/"การจอง #…" ⇒ ด่านไม่เห็นเครดิต ⇒ ปล่อยผ่าน (ตั้งใจ) | §12 | 💰 | S | — | **ทำเป็นค่าตั้ง** (หลัก ก) — `IntegrationConfig.BookingRefPatternsJson` ต่อคีย์ integration (regex ที่ validate · ค่าเริ่มต้น = 2 รูปเดิม) · `BookingDepositLedger` อ่าน pattern จากค่าตั้ง (ตัวเดียว) · ไม่รู้จักยังปล่อยผ่าน (G3) + รายงานตรวจข้อมูล "JE ที่ตัดผังมัดจำแต่ไม่อ้างการจอง" (อ่านอย่างเดียว) · เทสต์: pattern เดิม 0 เปลี่ยน | W10 |
| R-57 | JE ที่ลงไปแล้วก่อนด่าน (เช่น JV-INT-202609-0009) | §12 | 💰 | — | — | **ตรวจแล้ว — รายงานอ่านอย่างเดียว + ลิงก์กลับรายการมีแล้ว** · ไม่กลับให้เอง (R1) — ปิด | ✅ |

### 1.9 รายการจากรายงานทีม (G · H · I · J · M · O)

| # | รายการ | ต้นทาง | ผล | ขนาด | ขึ้นกับ | คำตัดสินที่เสนอ | แพ็ก |
| --- | --- | --- | --- | --- | --- | --- | --- |
| R-58 | **LINE ไม่ประทับ `OriginModule`** — ตรวจแล้ว `LineBotService.cs:340` สร้าง `CreateDocumentRequest` ไม่มี `OriginModule` (มีแค่ `Reference: "LINE-bot"`) ⇒ `DocumentCreationChannel` ตอบ "ไม่ทราบ" | รายงานทีม G · `DocumentCreationChannel.cs:26,52` | 👤⚙️ | S | — | **ทำ** — ประทับ `OriginModule = "LineBot"` (ค่าคงที่ใน `DocumentCreationChannelRule`) · **migration** backfill แถวเดิม `Reference == "LINE-bot" AND OriginModule IS NULL` (F2 #9 — ของเสียถูกเก็บแล้ว) · `DocumentCreationChannel` อ่านค่าใหม่ · ทิศตรงข้าม: ใบที่ Reference อื่นไม่ถูกแตะ | W9 ✅ (ทำแล้ว 2026-10-26 · sha ในคอมมิตตามหลัง) |
| R-59 | **feedback เก่าของ `DocumentTypeClassification` ไม่ migrate** — ทีม H เปลี่ยน prompt จาก "ชนิดกระดาษ" เป็น "ชนิดปลายทาง" แต่แถว `AiFeedback` เดิมมี AiAnswer เป็นชนิดกระดาษ ⇒ `GenericFeedbackDistillationModel` เรียนคำตอบคนละความหมาย (ตรวจแล้วไม่พบ migration ใน `DatabaseMigrationHelper`/`AiFeedbackRecorder`) | รายงานทีม H · `WorkflowPrompts.cs:271-272` | ⚙️ (คุณภาพนักเรียน) | M | — | **ทำ** — migration ครั้งเดียว: แถว feature นี้ก่อนวันที่ deploy ทีม H ⇒ แปลง AiAnswer/UserChosen ด้วย `DocumentSide` เมื่อแปลงได้ทางเดียว (เช่น TaxInvoice+ฝั่งซื้อ → Expense/PurchaseInvoice ตามที่ผู้ใช้เลือกจริง) · แปลงไม่ได้ ⇒ ประทับ `ExcludedFromTraining=true` + เหตุผล (ไม่ลบ · ไม่เดา — G3) · `LoadFromFeedbackAsync` ข้ามแถว Excluded · เทสต์ทิศตรงข้าม: แถวหลังวันนั้นไม่ถูกแตะ | W9 ✅ (ทำแล้ว 2026-10-26 · sha ในคอมมิตตามหลัง) |
| R-60 | **audit log กฎตราประทับ** — ตรวจแล้ว `DocumentStampRulesController.Update` บันทึก `DocumentStampRulesJson` ตรง ไม่มี `AddChainedAuditLog` (ค่าตั้งที่เปลี่ยนหน้าตาเอกสารกฎหมาย 50 ทวิ/ใบกำกับ) | รายงานทีม J | 🔐🧾 | S | — | **ทำ** — `AddChainedAuditLog(before/after JSON · reason)` ในธุรกรรมเดียวกับ Save (กฎ M append-only) · ผู้แก้/เวลา แสดงบนหน้าตั้งค่า | W9 ✅ (ทำแล้ว 2026-10-26 · sha ในคอมมิตตามหลัง) |
| R-61 | **ตัวเลือกผู้ลงนามตามสิทธิ์** — `BuildAsync` ส่งสมาชิกทุกคน + ทุกบทบาท (เว้น SystemAdmin) เป็นตัวเลือกผู้ลงนาม · การตั้งกฎใช้สิทธิ์ `CompanySettingsEdit` | รายงานทีม J | 🔐 | S | R-60 | **ตัดสิน: ไม่กรองตัวเลือก (หลัก ก — ใครลงนามเป็นเรื่องตั้งค่าของบริษัท)** แต่ (1) แสดงบทบาท+สถานะ active ข้างชื่อ (2) ผู้ลงนามที่ถูกถอดจากบริษัท ⇒ renderer ไม่วาดลายเซ็น + หมายเหตุ "ผู้ลงนามไม่อยู่ในบริษัทแล้ว" (ไม่แต่งลายเซ็นของคนที่ออกไป — G3) (3) audit จาก R-60 | W9 ✅ (ทำแล้ว 2026-10-26 · sha ในคอมมิตตามหลัง) |
| R-62 | **Admin OCR นับ "หน้า" vs "ครั้ง"** — ตรวจแล้ว `AdminCompanyUsageController.Audit.cs:239,365` นับ `OcrScanResults` (ครั้ง) ขณะเพดาน/มิเตอร์ (`:410,419` · `SubscriptionService.CurrentMonthOcrPages`) เป็น**หน้า** ⇒ เทียบคนละหน่วยในแถวเดียว | รายงานทีม G | 👤 | S | — | **ทำ** — แยกคอลัมน์ "สแกน (ครั้ง)" กับ "หน้า (มิเตอร์)" ป้ายชัด · ไม่แปลงครั้ง→หน้าเอง (ไม่รู้จำนวนหน้าย้อนหลัง = บอกไม่รู้) | W9 ✅ (ทำแล้ว 2026-10-26 · sha ในคอมมิตตามหลัง) |
| R-63 | **Db test สิทธิ์ของ endpoint ทีม I** (`without-owner/repair` · `assign-owner` · `LastOwnerGuard` ใต้ `pg_advisory_xact_lock`) ยังไม่มีใน job db-test | รายงานทีม I | 🔐⚙️ | S | job db-test | **ทำ** — `Db/CompanyOwnershipDbTests`: ซ่อมพร้อมกัน 2 คำขอ ⇒ Owner คนเดียว · ถอด Owner คนสุดท้าย ⇒ ปฏิเสธ · แอดมินที่ไม่ใช่ผู้สร้างไม่อยู่ในตัวเลือก (ทิศตรงข้าม: ผู้สร้าง active ตั้งเองได้) | W9 ✅ (ทำแล้ว 2026-10-26 · sha ในคอมมิตตามหลัง) |
| R-64 | **ภ.ง.ด.50 ตารางเดิมไม่มีตัวทบทวน** + **2 จุด DocumentService/Gateway** (รายงานทีม O) | รายงานทีม O | 🧾 | — | — | **ตรวจแล้ว — แก้แล้ว**: `TaxController.cs:63 GET tax/cit-recompute-review` มีแล้ว · grep `JournalStatus.Posted` ที่ไม่มี `Reversed`/`LedgerStatuses` ใน `DocumentService*.cs` + `Services/Payments/**` = **0 จุด** (ทีม O หลังฝ่ายค้านแก้ `LoadFeeVatAgingAsync` + มัดจำ ×3) — ปิด | ✅ |
| R-65 | **SmartSafety / Executive quarter ใช้ปีปฏิทิน** (ทีม M พบ) | รายงานทีม M | 👤 | — | — | **ตรวจแล้ว**: Executive trends ใช้ `PnlComparison.TrailingPeriods` ตามรอบบัญชีแล้ว (`ExecutiveReportService.Trends.cs:51-63` · ทีม O) · `SmartSafetyController` ไม่มีโค้ดไตรมาส/`AddMonths(-3)` เลย (grep = 0) ⇒ รายการ "SmartSafety quarter" **ไม่ใช่บั๊ก** (ที่ทีม O แก้คือ "ไม่นับใบปิดบัญชี") — ปิด · ห้ามรายงานซ้ำ | ✅ |
| R-66 | TEST_PLAN **BKE-11** (cms-edit เปิดแก้บริการแล้วบันทึกโดยไม่แตะ ⇒ ค่าคงเดิม) · **BKE-12** (`?siteId=` ล็อกไซต์ · siteId ต่างบริษัท ⇒ "ไม่พบเว็บไซต์") — ⏳ manual | TEST_PLAN BKE | 👤 | — | — | **ไม่ใช่งานพัฒนา** — ย้ายไป checklist เจ้าของ §6 | §6 |

---

## 2. ลำดับความสำคัญ (ทำไมแพ็กไหนก่อน)

1. **เงินเข้าแล้วแต่ไม่มีเอกสาร/ทางไปต่อ** (R-01 R-02 R-06 R-07 R-08) — เจ้าของเสียเงิน/ภาษีจริงวันนี้
2. **ค่าตั้งที่มีช่องแต่ไม่มีผล · HTTP 200 ของว่าง** (R-26 R-27 R-12 `ReminderSentAt` ไม่มีผู้เขียน) — F2 #2/#7 ตรง ๆ
3. **ผังผิดความหมาย** (R-40 218xx) — JE ลงผิดกลุ่มเงียบ ๆ
4. **วินัยที่เคยเป็นบั๊ก** (R-43 disk ตรง · R-46 สองตัวเรนเดอร์ · R-58/R-59 ของเสียที่ persist แล้ว)
5. ฟีเจอร์ที่ผู้ใช้ขอชัด (R-44 OG · R-45 ทุกไซต์ · R-49 ส่วนลด · R-38 คืนบางส่วน)

---

## 3. แพ็กงานทีมพัฒนา (brief พร้อมใช้ · ขอบเขตไฟล์ไม่ทับกัน)

กติการ่วมทุกแพ็ก: อ่าน `CLAUDE.md` + `common.md` · `python3 tools/callers.py <Symbol>` ก่อนแตะ · ตัวตัดสิน = pure helper ใน `Helpers/` + เทสต์ในคอมมิตเดียว · ล็อกจุดเรียกด่านเงิน/ภาษี/สิทธิ์ใน `tools/required_call_site_check.py` · `bash tools/check_all.sh` ก่อน commit · แตะเงิน/ภาษี ⇒ ฝ่ายค้าน 1 รอบ (3 คำถาม) · doc ขยับในคอมมิตเดียว (DOCUMENT_FLOW/TEST_PLAN/CHANGELOG + ติ๊ก `✅ <sha>` ในไฟล์นี้) · ไฟล์ร่วม (`Program.cs` · `DatabaseMigrationHelper.cs` · `AllEnums.cs` · `app.html`) แก้เป็นก้อนแยกเล็กที่สุด

### W1 — Gateway & โควตา (R-01 R-02 R-03 R-04 R-05)
- **ไฟล์**: `Services/Payments/PaymentIntentService.cs` · `GatewayRefundService.cs` · `Controllers/PaymentGatewayController.cs` · `Helpers/DocumentQuotaPolicy.cs` · `Services/Implementations/SubscriptionService.cs` (ตัวนับ) · หน้ารายการรับชำระออนไลน์ · `Accounting.Tests/Db/*Quota*DbTests.cs`
- **คำตัดสินที่ใช้**: ข้อ 127 (ไม่คืนเงินเอง) · LICENSING §4/§13.1 · R1 · F2 #7
- **เกณฑ์เสร็จ**: re-dispatch endpoint + ปุ่ม (server บอกว่าค้าง) · ธงคืนที่ gateway · `Classify` รับข้อเท็จจริงใหม่ 4 ตัว · ตัวนับ atomic · ใบร่างจากเส้นอัตโนมัติไม่นับจนอนุมัติ · สคริปต์เทียบก่อน-หลังโควตาทุกบริษัท (อ่านอย่างเดียว) แนบผลในคอมมิต
- **เทสต์ทิศตรงข้าม**: ใบมัดจำใบแรก + ใบกำกับเช็คเอาต์ยังนับ 2 · void ไม่คืนโควตา · re-dispatch บน intent ที่ handler สำเร็จแล้ว = no-op พร้อมข้อความ (ไม่บันทึกซ้ำ) · 2 คำขอพร้อมกัน ⇒ +2

### W2 — เส้นเงินการจองนัดหมาย + ยกเลิกออเดอร์ (R-06 R-07 R-08 R-09 R-10 R-11)
- **ไฟล์**: `Services/Implementations/CmsBookingService.cs` (เส้นเงิน/ยกเลิก) · `Services/Payments/Handlers/SiteBookingPaymentHandler.cs` (ใหม่) · `Services/Payments/PublicPaymentResolver.cs` · `Helpers/CmsBookingErpPolicy.cs` · `Helpers/BookingCancellationFee.cs` (ใหม่) · `Helpers/SiteOrderCancelPlan.cs` (ใหม่) · `CmsCommerceService.cs` เฉพาะ `ResolveOrderAsync`/เส้นยกเลิก · storefront หน้าจอง/จัดการการจอง · `AllEnums.cs` (+`PaymentSourceKind.SiteBooking` ก้อนเดียว)
- **ขอบเขตห้ามแตะ**: `BookingSlotEngine` (ของทีม B — เรียก `Decide` เท่านั้น) · `PaymentIntentService` (ของ W1)
- **คำตัดสินที่ใช้**: ข้อ 126 (เพดานค่าปรับ = มัดจำ) · 127 · 155 · 156 (หักฐานมัดจำ) · R1 · §78/1
- **เกณฑ์เสร็จ**: แขกจ่ายมัดจำออนไลน์ ⇒ ใบมัดจำอนุมัติเอง (วันที่ = วันรับเงิน) · ไม่จ่าย ⇒ ร่างค้างเหมือนเดิม · เสร็จบริการ ⇒ ใบส่วนที่เหลือหักมัดจำ · ยกเลิกเอง/ไม่มา ⇒ ค่าธรรมเนียม ≤ มัดจำ · ยกเลิกออเดอร์ที่จ่ายแล้ว ⇒ CN ร่าง + ลิงก์ refund · สลิปรอตรวจ ⇒ intent ใหม่ถูกปฏิเสธ
- **เทสต์ทิศตรงข้าม**: Instant/Guaranteed ไม่เปลี่ยน (`BookingInvoiceTimingPolicyTests` เดิมเขียว) · webhook ซ้ำ = เลขอ้างอิงเดิม ไม่ออกใบซ้ำ · ออเดอร์ที่ยังไม่จ่าย void เหมือนเดิม · มัดจำ 0 ⇒ ไม่มีเอกสาร (`NoDepositNote` เดิม) · **ฝ่ายค้านบังคับ**

### W3 — เครื่องจอง: งานเบื้องหลัง + ตั้งค่า + Db test (R-12 R-13 R-14 R-15 R-16)
- **ไฟล์**: `Services/Jobs/BookingReminderJob.cs` (ใหม่ · `Helpers/JobLock`) · `Helpers/BookingReminderPlan.cs` (ใหม่ pure) · `Models/Entities/CmsBookingResource.cs` (`SiteBookingSettings` คอลัมน์ใหม่) · `Helpers/BookingEngineMigration.cs` · `BookingSlotEngine.RulesOf` (เฉพาะอ่านเวลาทำการระดับเว็บ) · `wwwroot/pages/cms-bookings.html` (ช่องผู้ให้บริการในโมดัล) · `cms-edit.html` (ค่าตั้งใหม่) · `Accounting.Tests/Db/CmsBookingDoubleBookingDbTests.cs`
- **ขอบเขตห้ามแตะ**: เส้นเงิน/ยกเลิกใน `CmsBookingService` (W2) — ต่อสาย "แจ้งเมื่อได้คิว" ผ่าน event/เมธอดใหม่ไฟล์แยก `CmsBookingService.Notify.cs`
- **เกณฑ์เสร็จ**: reminder ส่งจริงแล้วค่อยประทับ · ถือรอบคิวสำรองตั้งค่าได้ · เวลาทำการระดับเว็บเป็นค่าตั้งต้น · เลือกผู้ให้บริการผ่าน `PickResource` · Db test เขียวใน job db-test
- **เทสต์ทิศตรงข้าม**: บริการที่ตั้งเวลาเองไม่เปลี่ยน · ส่งล้ม ⇒ `ReminderSentAt` ยัง null + ร่องรอย · การจองที่ไม่เลือกคน ⇒ ระบบจัดให้ deterministic เดิม

### W4 — CMS แพลตฟอร์ม (R-25 R-43 R-44 R-45 R-46 R-47)
- **ไฟล์**: `Services/Implementations/CmsContentService.cs` (สื่อ) · `CmsCommerceService.cs` เฉพาะ `SaveSlip*` (สลิป → `IFileAttachmentService`) · `CmsRenderingService.cs` + เส้นเสิร์ฟ storefront (SSR head) · `Helpers/Cms*Block.cs` (ตัวเรนเดอร์) · `Controllers/CmsSiteController.cs` (endpoint ทุกไซต์) · `wwwroot/js/cms-site-context.js` · `cms-orders.html`/`cms-leads.html`/`cms-bookings.html` (เฉพาะโหมดทุกไซต์ — ประสานกับ W3 เรื่องโมดัล) · `Services/Jobs/CartImageCleanupJob.cs` (ใหม่) · `tools/local_disk_write_check.py` (ใหม่ ratchet)
- **คำตัดสินที่ใช้**: กฎเหล็ก #4 C (HtmlEncode) · D (ห้ามเขียน disk ตรง · JobLock) · A (สองตัวเรนเดอร์ห้าม drift) · F2 #9 (ไฟล์เดิมอ่านได้ต่อ)
- **เกณฑ์เสร็จ**: ไม่มี `Path.Combine(Directory.GetCurrentDirectory` ใหม่ใน Services (checker = 0) · แชร์ลิงก์หน้า storefront ใน LINE ได้รูป/ชื่อ (ตรวจด้วย `curl` ไม่รัน JS เห็นแท็ก) · หน้าทั้งสามโหมดทุกไซต์ใช้ endpoint เดียว + แบ่งหน้า · รูปตะกร้าเก่าถูกลบตามอายุ
- **เทสต์ทิศตรงข้าม**: URL สื่อเดิมยังเปิดได้ · บล็อกที่ย้ายตัวเรนเดอร์ golden HTML เท่าเดิม · รูปของออเดอร์จริงไม่ถูกลบ · ผู้ใช้ที่ไม่มีบทบาทบนเว็บ B ไม่เห็นแถวของ B ในโหมดทุกไซต์

### W5 — ที่พัก หน้าเคาน์เตอร์ (R-49 R-50 R-51 R-52)
- **ไฟล์**: `Services/Implementations/Lodging/LodgingService.Lifecycle.cs` (เช็คเอาต์/folio) · `Helpers/LodgingDepositSettlement.cs` (`PlanCheckout` รับส่วนลด) · `Helpers/LodgingCheckoutDraft.cs` · `Helpers/LodgingFolioOnCancel.cs` (ใหม่) · `Controllers/LodgingController.cs` · `wwwroot/pages/lodging.html` · หน้าส่งสลิปแขก (ช่องยอดที่โอน) · `Helpers/LodgingSlipGallery.cs`
- **ขอบเขตห้ามแตะ**: `LodgingTaxInvoiceTimingPolicy` (ข้อ 157 ของทีม P — เรียกเท่านั้น) · เส้นเงิน `ConfirmAsync`
- **คำตัดสินที่ใช้**: ข้อ 126 · 157 (ยอดลด = ใบลดหนี้อ้างใบเดิม) · §86/10 · G3 (ยอดสลิปที่แขกกรอกไม่ใช่หลักฐาน)
- **เกณฑ์เสร็จ**: ส่วนลดเช็คเอาต์คิดบนราคาก่อนหักมัดจำ · ติดลบ ⇒ CN อ้างใบมัดจำ · ลิงก์ทุกใบของการจอง · folio ก่อนเช็คอินได้ + ล้างตามกติกาเมื่อยกเลิก · กรอบสลิปแสดงยอดที่แขกกรอกคู่ยอดเซิร์ฟเวอร์
- **เทสต์ทิศตรงข้าม**: ไม่ใส่ส่วนลด ⇒ `PlanCheckout` ตัวเลขเดิมทุกสตางค์ ทั้ง 2 โหมด `TaxInvoiceTiming` × ประเภทมัดจำทุกแบบ · folio ของ CheckedIn ไม่ถูกล้าง · **ฝ่ายค้านบังคับ**

### W6 — คอร์ส/เครดิต + POS ค่าตั้งที่ไม่มีผล (R-26 R-27 R-38 R-39 R-40 R-41 เฉพาะ B2)
- **ไฟล์**: `Helpers/CreditPassDocumentRule.cs` · `Helpers/CourseCreditMath.cs` · `Services/Implementations/CourseCredit/*` · `Helpers/DeferredRevenueAccount.cs` (ใหม่) · `Services/Implementations/RevenueRecognitionService.cs` (เฉพาะ resolver ผัง) · `Services/Payments/Handlers/SiteOrderPaymentHandler.cs` (บรรทัดคอร์ส → 21713 + `ActivateFromDocumentAsync`) · `Services/Implementations/PosService.cs` เฉพาะจุดเพิ่มบรรทัดแพ็กเกจ (ตรึง `RevenueAccountId`) · `wwwroot/pages/pos-reports.html` (แท็บค่าคอม) · การจองที่เลือกใช้บัตร (`CmsBookingService.Credits.cs` ไฟล์ใหม่ — เรียก `ICreditBookingLedger` เท่านั้น)
- **ขอบเขตห้ามแตะ**: `BookingSlotEngine` · JE ขาย/คืนของ POS (B1 รอ Q4)
- **คำตัดสินที่ใช้**: F2 #2 (ค่าตั้งต้องมีผล) · #7 (200 ของว่าง = โกหก) · G3 (ไม่รู้ผัง = ล้มดัง) · TFRS NPAEs บทที่ 6
- **เกณฑ์เสร็จ**: CN บางส่วน ⇒ เครดิตลดตามสัดส่วน บัตรยัง Active · จอง/ใช้/ยกเลิก ⇒ Reserve/Consume/Release · ไม่มี fallback `StartsWith("218")` · แท็บค่าคอมมีข้อมูลจาก `staff-sales` · บิลใหม่ของแพ็กเกจลงผังที่ตั้ง · สินค้าคอร์สบนร้านออนไลน์เปิดบัตรหลังชำระ
- **เทสต์ทิศตรงข้าม**: CN เต็มส่วนที่เหลือยังปิดบัตร · CN เกิน Deferred/ทำให้เครดิต < ใช้แล้ว ⇒ บล็อกพร้อมยอดที่ออกได้ · บิล POS เดิม/JE ที่ปิดแล้วไม่เปลี่ยน · การจองไม่ใช้บัตรไม่แตะสมุด · **ฝ่ายค้าน R-38**

### W7 — หัก ณ ที่จ่ายตามรายการ + ใบต่างสกุล (R-17 R-18 R-19 R-20 R-21)
- **ไฟล์**: `Helpers/PerLineWht.cs` · `Helpers/OcrLineWhtPlan.cs` · `Helpers/WhtCertAmountLine.cs` · `Helpers/WhtCreditNoteRemainder.cs` · `Models/DTOs/Ocr/*` (`OcrLineItemDto`) · `wwwroot/pages/document-scan.html` **เฉพาะคอลัมน์ WHT** · `ExpenseClaim` entity/service + migration · `DocumentService.AutoPostToJournalAsync` (R-21 ท้ายแพ็ก)
- **ขอบเขตห้ามแตะ**: `OcrLineReconciler`/`OcrEtax*`/`OcrBillDiscount` (W8)
- **คำตัดสินที่ใช้**: PER_LINE_WHT §3 · §8b (ใบบาทไม่ขยับ) · DOCTRINE 2.3 warn-gate · คำตัดสินเดิม "ระบบไม่หักให้เอง"
- **เกณฑ์เสร็จ**: หน้ารีวิวสแกนเห็นแผน WHT ต่อบรรทัดเมื่อค่าตั้งเปิด + round-trip ต่อช่อง · CN ต่างสกุลใช้ `ForDocument` · ExpenseClaim มีประเภทเงินได้ต่อบรรทัด · ข้อเสนอหักเมื่อหัวไม่มี (warn) · R-21: 50 ทวิ = GL ถึงสตางค์ (ฝ่ายค้านผ่านเท่านั้นจึง merge)
- **เทสต์ทิศตรงข้าม**: ใบบาททุกใบ 0 เปลี่ยน (ทั้งสองฝั่ง) · ค่าตั้งปิด ⇒ หน้ารีวิวไม่มีคอลัมน์และ DTO ไม่มีแผน · ใบที่หัวมีหักแล้วไม่ถูกเสนอซ้ำ

### W8 — OCR ยอดรวม/e-Tax ปิดรอบ (R-34 R-37 + R-33 เมื่อ Q1 ตอบ)
- **ไฟล์**: `Accounting.Tests/Ocr/OcrPaperSamples.cs` · `OcrReplay*GoldenTests.cs` · `EtaxGoldenCorpusTests.cs` · (ถ้า Q1 = ก/ข) `OcrService.BuildScanLinesAsync`/`CreateDocumentFromScanCoreAsync` · `Helpers/OcrBillDiscount.cs` · `DocumentService.AllocateBillDiscount` (เรียก ไม่คัดลอกสูตร) · `docs/OCR_TOTALS_MATRIX.md`
- **คำตัดสินที่ใช้**: หลัก ข (ของเดิมที่ดีอย่าแก้ให้แย่) · §H (รันชุดกระดาษจริงก่อน-หลัง อธิบายทุกใบที่เปลี่ยน) · F2 #6 (golden จาก CI ไม่ใช่ความจำ) · ข้อ 158
- **เกณฑ์เสร็จ**: ใบ 00933 อยู่ใน Corpus + golden · `tools/ocr_golden_corpus_check.py` เขียว · CI harness เขียว · R-33 (ถ้าทำ): ยอดภาษีทุกใบเท่าเดิมทุกสตางค์ + ตารางก่อน-หลังระดับบรรทัดทุกใบที่มีส่วนลดแนบในคอมมิต
- **เทสต์ทิศตรงข้าม**: ใบที่ไม่มีส่วนลดท้ายบิล (เคส A/B · ส่วนลดต่อบรรทัดจริง) ไม่ถูกแตะแม้แต่ช่องเดียว

### W9 — แพลตฟอร์ม/แอดมิน/AI hygiene (R-58 R-59 R-60 R-61 R-62 R-63)
- **ไฟล์**: `Services/Implementations/LineBotService.cs` · `Helpers/DocumentCreationChannel*.cs` · `Data/DatabaseMigrationHelper.cs` (2 ก้อน: backfill LINE · ExcludedFromTraining) · `Services/Ai/AiFeedbackRecorder.cs`/`GenericFeedbackDistillationModel.LoadFromFeedbackAsync` · `Controllers/DocumentStampRulesController.cs` · `PdfGenerationService.Stamp.cs` + QuestPDF renderer (ผู้ลงนามที่ออกแล้ว — ทั้งสอง renderer คอมมิตเดียว) · `Controllers/AdminCompanyUsageController.Audit.cs` + หน้าแอดมิน · `Accounting.Tests/Db/CompanyOwnershipDbTests.cs`
- **คำตัดสินที่ใช้**: F2 #9 (persist แล้วต้องมี migration) · G3 (ไม่เดา) · กฎ M audit append-only · กฎเหล็ก #4 A (สอง renderer)
- **เกณฑ์เสร็จ**: ใบจาก LINE ขึ้นช่องทาง "LINE" ทั้งใบใหม่และใบเดิม · นักเรียน DocType ไม่เรียนแถวความหมายเก่า · แก้กฎตราแล้วมีแถว audit · ผู้ลงนามที่ออกแล้วไม่ถูกวาด · แอดมินเห็น "ครั้ง" กับ "หน้า" แยก · Db test สิทธิ์เขียว
- **เทสต์ทิศตรงข้าม**: ใบ Reference อื่น OriginModule ไม่ถูก backfill · feedback หลังวัน deploy ไม่ถูก Excluded · ผู้ลงนาม active วาดเหมือนเดิม

### W10 — Integration: pattern อ้างอิงตั้งค่าได้ + Db test ทิศกลับ (R-55 R-56)
- **ไฟล์**: `Helpers/BookingDepositLedger.cs` (อ่าน pattern) · `Helpers/BookingRefPattern.cs` (ใหม่ pure · validate regex) · `IntegrationConfig` entity + migration · หน้าตั้งค่า integration · รายงานตรวจข้อมูล (หมวดใหม่ "JE ตัดผังมัดจำไม่อ้างการจอง") · `Accounting.Tests/Db/BookingDepositDrawdownDbTests.cs`
- **คำตัดสินที่ใช้**: หลัก ก (ตั้งค่าได้) · ง (ไม่ปล่อยผ่านเมื่อรู้) · G3 (ไม่รู้ = ปล่อยผ่าน + รายงาน)
- **เกณฑ์เสร็จ**: pattern เพิ่มได้ต่อคีย์ · regex ผิด ⇒ ปฏิเสธพร้อมข้อความ · ด่านเห็นมัดจำที่ลงด้วยรูปใหม่ · Db test: ตัดเกิน 422 · reverse ที่ถูกตัดแล้ว 409 · พร้อมกัน 2 คำขอผ่านได้ไม่เกินยอด
- **เทสต์ทิศตรงข้าม**: pattern เริ่มต้น 2 รูปเดิม ⇒ ผลของ `BookingDepositDrawdownTests` ทุกตัวเท่าเดิม · JE ไม่อ้างการจอง ⇒ 200 เหมือนเดิม

---

## 4. ต้องถามเจ้าของ/นักบัญชี (5 คำถาม — เลือกตอบ)

| Q | คำถาม | ตัวเลือก | ข้อเสนอของทีม W | ปลดล็อก |
| --- | --- | --- | --- | --- |
| **Q1** | ใบสแกนที่มี**ส่วนลดท้ายบิล** ให้บันทึก "หน้าตาตรงกระดาษ" (บรรทัดราคาเต็ม + ส่วนลดท้ายบิล `BillDiscountAmount` — ยอดภาษีเท่าเดิมทุกสตางค์) หรือคงแบบเดิม (กระจายส่วนลดลงบรรทัด)? | ก) ตรงกระดาษทุกเคส C/E/F/G · **ข) เฉพาะใบที่พิสูจน์ส่วนลดท้ายบิลได้จากหัวใบ/XML (e-Tax · แถวส่วนลดบนกระดาษ)** · ค) คงเดิม | **ข** — ได้หลัก (ค) "ข้อมูลครบถูกต้องที่สุด" โดยไม่แตะใบที่เดิมตัดสินถูกจากบรรทัด (หลัก ข) | R-33 → W8 |
| **Q2** | 50 ทวิ/ฐาน WHT ของ**ใบต่างสกุล**ที่จ่ายเป็นงวด ใช้อัตราแลกเปลี่ยน**วันจ่าย** (ป.132/2548) หรืออัตราวันใบ (เดิม)? และอัตรา ธปท. ที่ใช้กับ VAT ใช้ "อัตราซื้อถัวเฉลี่ยวันทำการก่อน tax point" แทน mid-rate ไหม? | ก) วันจ่าย + อัตราซื้อถัวเฉลี่ย (ตามแนวสรรพากร — เปลี่ยนตัวเลขใบใหม่) · ข) คงเดิมทั้งคู่ · ค) ให้นักบัญชีของบริษัทตั้งค่าเลือกได้ต่อบริษัท | **ค** (หลัก ก) โดยค่าเริ่มต้น = เดิม · ใบที่ออกแล้วไม่แก้ย้อนหลัง (B-10 รายงาน) | R-23 R-24 → W7 ขั้นถัดไป |
| **Q3** | ที่พักต้องการออก**ใบกำกับภาษีอย่างย่อ** (§86/6) ให้แขก walk-in ไหม? (ต้องมีสถานะอนุมัติขายปลีก `IsRetailApproved` · ใบเต็มรูปใช้แทนได้ตามกฎหมายอยู่แล้ว) | ก) ต้องการ — ตั้งค่าต่อที่พัก · ข) ไม่ต้องการ | ข (ไม่มีผู้ขอ · ไม่ใช่ช่องโหว่กฎหมาย) | R-53 |
| **Q4** | จะ**ขายคอร์ส/บัตรเครดิตที่ POS** ไหม? (ต้องแก้ JE ขาย+คืนของ POS ให้ Cr 21713 — แตะของเดิมที่ทำงานดี) | ก) ขาย — ทำ B1 · ข) ขายที่หน้า "คอร์ส & เครดิต"/ร้านออนไลน์เท่านั้น (B2) | **ข** (หลัก ข) | R-41 B1 |
| **Q5** | โมดูลจองนัดหมาย: ตัวเลือกจ่ายมัดจำออนไลน์ (R-08) ให้เปิดเป็น**ค่าเริ่มต้น**สำหรับบริการ PrePayment ทุกไซต์ หรือต้องให้แต่ละไซต์เปิดเอง? | ก) เปิดทุกไซต์ที่ตั้ง gateway แล้ว · **ข) ปิดเป็นค่าเริ่มต้น ไซต์เปิดเอง** | **ข** (ไซต์ที่ยังรับสลิป/โอนเองไม่เปลี่ยนพฤติกรรม — หลัก ข) · ไม่บล็อก W2 (ทำปุ่มตั้งค่าไว้ · รอแค่ค่าเริ่มต้น) | R-08 (ค่าเริ่มต้นเท่านั้น) |

ไม่ตั้งเป็นคำถาม (ตัดสินแทนได้จากกฎหมาย/หลักเดิม — เจ้าของแก้ได้ภายหลัง): R-49 ส่วนลดเช็คเอาต์ (เลขคณิต + ข้อ 157) · R-54 ผังค่าปรับ (ข้อ 157 + VAT ของมัดจำที่ริบ) · R-32 VAT ค่าส่งบิลปน (ทิศปลอดภัย G5 · รอนักบัญชีไม่ใช่เจ้าของ) · R-26 ผังแพ็กเกจมีผล (F2 #2)

---

## 5. ตั้งใจไม่ทำรอบนี้ (16 ข้อ · เหตุผล)

| # | รายการ | เหตุผล | เมื่อไรค่อยทำ |
| --- | --- | --- | --- |
| R-22 | ตาราง DTA ว่าง | รอตัวบททางการ (ข้อ 150 "รอข้อมูลภายนอก") · ม.70 เต็ม = ทิศปลอดภัย | เมื่อนักบัญชียืนยันทีละประเทศ |
| R-28 | POS ยกเว้น VAT รายบรรทัด | ต้องแก้ 3 ชั้นพร้อมกัน · ทำครึ่ง = ใบกำกับผิด · 7% = เสียเกินไม่ผิดกฎหมาย | เมื่อมีร้านที่ขายของยกเว้นปนจริง |
| R-29 | หักมัดจำการจองในบิล POS | มีด่าน+ทางไปต่อแล้ว (ข้อ 150) | เมื่อศูนย์มัดจำมี API ตัวเดียว |
| R-30 | ค่าส่งตามระยะทาง · ตัวเลือกบน POS · ใบสั่งผลิต | ฟีเจอร์ใหม่ไม่แตะเงิน/กฎหมาย · ไม่มีผู้ขอ | ตามคำขอ |
| R-31 | MTO: คืน/ริบมัดจำอัตโนมัติ · ใบมัดจำบิลหลายอัตรา | ข้อ 156 ตัดสินแล้ว (R1 — ต้องมีหลักฐานจากคน) | — |
| R-32 | VAT ค่าส่งแบ่งตามสัดส่วน | 7% = ทิศปลอดภัย · รอนักบัญชี | เมื่อนักบัญชียืนยัน |
| R-35 | OCR #10 F/G + หลายอัตราไม่มีตาราง | "ไม่รู้ = บอกว่าไม่รู้" ถูกต้องตาม G3 | ไม่ทำ |
| R-36 | OCR #15 XML รวม VAT + allowance รวม VAT | ไม่เพิ่มกติกาจากใบสังเคราะห์ (§H) | เมื่อมีกระดาษจริง |
| R-42 | คอร์ส B5/B7/B8 | ไม่มีผู้ขอ · PDPA portal · ไม่มี Testcontainers | ตามคำขอ |
| R-48 | CMS Blog/Popup/อีเวนต์/A/B/เวอร์ชันหน้า | ฟีเจอร์ใหม่ไม่แตะเงิน/กฎหมาย | ตามคำขอ |
| R-52 (ส่วน OCR สลิป) | ตัวอ่านสลิป OCR/QR | ต้องผ่าน distillation loop ครบ 4 ขั้น + student model — งานแยกรอบ | รอบ OCR ถัดไป |
| R-54 | ย้ายผังค่าปรับยกเลิกที่พัก | ข้อ 157 + VAT ของมัดจำที่ริบ | เปิดเป็นค่าตั้งผังเมื่อเจ้าของขอ |
| R-13 (ส่วนคิวข้ามรอบ) | คิวสำรอง "เวลาไหนก็ได้วันนี้" | เปลี่ยน slot logic ของทีม B · ไม่มีผู้ขอ | ตามคำขอ |
| R-16 (ส่วนปฏิทิน) | ปฏิทิน day/week + ลากเลื่อน | UI ใหญ่ · ไม่แตะเงิน | รอบถัดไป |
| BOOKING_ENGINE B1x เหลือ/B8/B9/B10/B11/B15 | รูป/ราคาต่อผู้ให้บริการ · หลายวัน · price rules · calendar sync · intake form (PDPA ม.26) · ข้ามเที่ยงคืน | ฟีเจอร์ใหม่ · B11 ต้องเข้ารหัส+RoPA ก่อน | รอบถัดไป ตามคำขอ |
| ข้อ 122 captcha | ต้องตั้ง key กับเจ้าของ | — | เมื่อเจ้าของให้ key |

---

## 6. Checklist manual test สำหรับเจ้าของ (รอบ 205 — ของที่เครื่องทดสอบไม่ได้)

| # | ทดสอบ | คาดหวัง | ที่มา |
| --- | --- | --- | --- |
| M-01 | `cms-edit.html` เปิดแก้บริการจองคิวแล้วกดบันทึกโดยไม่แตะอะไร | ชนิดการจอง/มัดจำ/buffer/ตารางเวลา คงเดิม | TEST_PLAN BKE-11 |
| M-02 | เปิด `cms-bookings.html?siteId=<ของเว็บ A>` · แล้วลอง siteId ของบริษัทอื่น | ล็อกเฉพาะเว็บ A · ของบริษัทอื่น ⇒ "ไม่พบเว็บไซต์" + แสดงเว็บแรก | BKE-12 |
| M-03 | หน้าเคาน์เตอร์ที่พัก: การจองที่แขกส่งสลิป 2 ใบ (ใบแรกถูกปฏิเสธ) — เปิดโมดัล | เห็นทั้ง 2 ใบ + ป้าย "ถูกปฏิเสธแล้ว + เหตุผล" · กดรูปขยาย · ปุ่มเปิดแท็บใหม่ไม่ถูกบล็อกบน Safari/มือถือ | LODGING §12.1 |
| M-04 | ดาวน์โหลด/พิมพ์/ดูบนเว็บ 50 ทวิ ของบริษัทที่ตั้งตราประทับ | ตราขึ้นครบ 3 ทาง · ลายน้ำยกเลิกบนใบที่ยกเลิก | ทีม J |
| M-05 | ตั้งค่า Document Stamp Rules เลือกผู้ลงนามต่อสถานะ แล้วเปิด PDF + หน้าพิมพ์ | ลายเซ็นชุดเดียวกันทั้งสองทาง (ไม่ใช่ของผู้กดพิมพ์) | ทีม J |
| M-06 | แชร์ลิงก์หน้า storefront ลง LINE | (ก่อน W4) ไม่มีรูป/ชื่อ = ยืนยันอาการ · (หลัง W4) มีรูป/ชื่อ | R-44 |
| M-07 | ร้านออนไลน์โหมด `deposit_tax_invoice`: สั่ง → มัดจำ → กด "ส่งมอบแล้ว" | ใบมัดจำ (VAT ทันที) → ใบขายส่วนที่เหลือหักมัดจำ → ตัดสต็อกครั้งเดียว · ยืนยันซ้ำไม่ออกซ้ำ | ข้อ 156 ทีม Q |
| M-08 | ที่พักโหมด `TaxInvoiceAtBooking`: ยืนยันการจอง → รับเงิน → เช็คเอาต์มีค่า minibar | ใบกำกับเต็มยอดตอนยืนยัน · ใบรับเงินไม่มี VAT · เช็คเอาต์ออกเฉพาะ minibar | ข้อ 157 ทีม P |
| M-09 | ที่พักโหมดเดิม (ค่าเริ่มต้น) ทำการจองครบวงจร | ตัวเลขทุกใบเท่าก่อน deploy (เทียบกับใบจริงก่อนหน้า) | หลัก ข |
| M-10 | ระบบต้นทาง (TakeTime) ส่ง JE ตัดมัดจำของการจองที่ใบมัดจำถูกยกเลิกแล้ว | 422 `INT-DEPOSIT-OVERDRAW` + ข้อความไทยบอกยอด/ใบที่ถูกยกเลิก · ไม่มี JE เกิด | ทีม S |
| M-11 | สแกนใบ e-Tax SP-IN2026-10-00933 ใหม่ | 1,340.19 · 93.81 · 1,434.00 · กล่อง**น้ำเงิน** "ระบบตรวจพบและปรับให้แล้ว" (ไม่ใช่เหลือง) · เลขผู้ซื้อเติมจากหน้า PDF `[ETAX-PAGE-FILL]` ไม่หยุดอนุมัติ | ทีม R/T · ข้อ 158 |
| M-12 | หน้าแอดมิน AI: เปิดรายการ call วันเก่าที่เคย "Schema mismatch" | ข้อความสาเหตุจริง (ขาด token/finish_reason) + คำตอบดิบย่อปิดบัง PII | ทีม H |
| M-13 | ค้นหา `RES-149669` ในหน้าเอกสาร + Ctrl+K | เจอใบเสร็จมัดจำ | 2026-10-26 |
| M-14 | จองนัดหมายหน้าเว็บโหมด PrePayment (ยังไม่มีเส้นจ่าย) | การจองสำเร็จ · เอกสารเป็น**ร่าง** ไม่มีเลข/JE · หมายเหตุ "รับเงินแล้วเปิดเอกสาร" | ทีม E B-1 |

---

## 7. บันทึกการตรวจ (จุดที่ grep แล้ว "ไม่พบ" — เปิดไฟล์ยืนยันแล้ว)

- `LineBotService.cs:336-350` ไม่มี `OriginModule` ใน `CreateDocumentRequest` ✓ (R-58)
- `DocumentStampRulesController.cs:45-66` ไม่มี audit ✓ · ตัวเลือกผู้ลงนาม = สมาชิกทุกคน ✓ (R-60/61)
- `CmsContentService.cs:550` · `CmsCommerceService.cs:1993-2012` เขียน disk ตรง ✓ · `IFileAttachmentService` มีอยู่ (`Services/Interfaces/IFileAttachmentService.cs`) ✓ (R-43)
- `storefront.html:505-508` meta ด้วย JS ✓ (R-44) · `cms-bookings.html` grep `allSites` = 0 · `cms-orders.html:221` ยิงต่อเว็บฝั่ง client ✓ (R-45)
- `AdminCompanyUsageController.Audit.cs:239,365` นับครั้ง · `:410,419` เพดานเป็นหน้า ✓ (R-62)
- `TaxController.cs:63` `cit-recompute-review` มี ✓ · `JournalStatus.Posted` ไม่มี `Reversed` ใน `DocumentService*.cs`/`Services/Payments/**` = 0 ✓ (R-64)
- `ExecutiveReportService.Trends.cs:51-63` ใช้รอบบัญชี ✓ · `SmartSafetyController.cs` ไม่มีโค้ดไตรมาส ✓ (R-65)
- `RevenueRecognitionService.cs:341-343` `21810`/`StartsWith("218")` ✓ (R-40)
- `PaymentIntentService.cs:357` dispatch ครั้งเดียว · ไม่มี endpoint re-dispatch ✓ (R-01)
- `ReminderSentAt` ไม่มีผู้เขียนใน `Services/` ✓ (R-12)
- `LodgingService.Lifecycle.cs:1466` รับ `BillDiscountAmount` แต่หน้าไม่ส่ง ✓ (R-49)
- CHANGELOG 2026-10-22..26: "ยังไม่ผ่านฝ่ายค้าน" ของทีม Q/P/S/T ทุกทีมมีแถว "แก้ผลฝ่ายค้าน" ตามหลังแล้ว (T = `492e8b74`) ⇒ ไม่มีงานค้างฝ่ายค้าน

_Last verified against codebase: 2026-10-26 (ทีม W · commit 492e8b74 เป็นฐาน)_
