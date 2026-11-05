# LICENSE_CASE_MATRIX — ทุกเคสการจัดการแพ็กเกจ/license + "สิ่งที่ลูกค้าจะได้ก่อนจ่าย" (รอบ 233 · 2026-11-03)

> เจ้าของ (คำต่อคำ): "ควรจะแสดงสิ่งที่ลูกค้าจะได้ถ้าจ่ายเงินนี้ไหม เช่น ต่ออายุไปอีก 1 ปีจากวันหมดอายุเดิม จะเปลี่ยนวันหมดอายุเป็นวันที่เท่าไหร่ ·
> ถ้าเลือกแพ็คต่ำกว่าต้องขึ้นแจ้งว่าชำระไม่ได้เพราะอะไร · ถ้าก่อนหน้านี้จ่ายเป็น standard มา 1 ปี ใช้ไป 3 เดือน และมากดแพ็กเกจ pro ราคาต้องคำนวณให้ถูกต้อง"
>
> คำตัดสิน: `erp-review/2026-09-29/DECISIONS.md` แถว **233-1 … 233-8** · ตัวตัดสินเดียว `Accounting/Helpers/PlanPurchasePreview.cs` ·
> เทสต์ `Accounting.Tests/PlanPurchasePreviewRound233Tests.cs` (pure 30 Fact + 1 Theory 5 แถว) + Db `PlatformBillingPhase0DbTests.Round233_*` (6) + `Accounting.Tests/Db/PublicCheckoutRound233DbTests.cs` (4)
> **ยังไม่ได้คอมไพล์ในเครื่องนี้** (ไม่มี .NET SDK) — CI บน `claude/**` คือ compiler ตัวแรก

## 0. สรุป

| | จำนวน |
| --- | --- |
| สถานะ × การกระทำ ในเมทริกซ์ | 14 × 10 = **140 ช่อง** (รวมช่องที่ "ไม่เกี่ยว/ทำไม่ได้โดยนิยาม") |
| ช่องที่พฤติกรรมเดิมถูกต้องแล้ว (ยอด+วันที่+ปฏิเสธ) แต่**ไม่บอกลูกค้า** (ไม่มีวันที่/เหตุ) | 96 — แก้ด้วย preview ตัวเดียว (E-1) |
| ช่องที่ผิดจริง (ยอด/วันที่/ข้อความ/ข้อมูลที่ประทับ) | 31 ช่อง ใน 9 ข้อบกพร่อง E-1…E-9 + ฝ่ายค้าน 233 อีก 9 ข้อ (E-10…E-18) — **แก้แล้วทั้งหมด** |
| ค้าง (ใหญ่เกินรอบ/ต้องให้เจ้าของเลือก) | 0 ข้อ — K-1 ทำแล้วรอบ 237 (เจ้าของเลือก "รองรับทั้ง 2 ทาง เลือกได้" · 237-1…237-7) — K-3 และ K-6 ทำแล้วตามคำตัดสินผู้ประสานงาน (E-15/E-16) · K-5 ทำแล้วรอบ 235 (235-X1) · K-2 K-4 K-7 K-8 ทำแล้วรอบ 235 (235-1…235-4) |

### ตัวอย่างของเจ้าของ (ล็อกด้วยเทสต์ `เจ้าของ_Starterรายปี_ใช้3เดือน_อัปเกรดPro_…`)

ราคาแม่แบบจริง (`Data/SeedPlanTemplates.cs`): Starter (= "standard") รายปี **฿6,018** · Professional (Pro) รายปี **฿15,198**
จ่าย Starter 01/01/2569 ⇒ ใช้ได้ถึง **01/01/2570** · วันนี้ 01/04/2569 (ใช้ไป 3 เดือน) ⇒ เหลือ **275/365 วัน**

| | แบบ ก KeepExpiry (**ค่าที่เลือกไว้ให้** — ข้อ 178 · 233-2 · 237-1) | แบบ ข RestartCycle (รอบ 237 · ลูกค้าเลือกได้ตามค่าตั้ง `PlatformUpgradeModeOffer`) |
| --- | --- | --- |
| ค่า Pro | 15,198 × 275/365 = **฿11,450.55** (ส่วนที่เหลือของรอบ) | ฿15,198.00 (เต็มปี) |
| เครดิต Starter ที่จ่ายจริง | 6,018 × 275/365 = **−฿4,534.11** | −฿4,534.11 |
| **ยอดชำระ** | **฿6,916.44** | ฿10,663.89 |
| ใช้ได้ถึง | **01/01/2570 (คงเดิม)** | 01/04/2570 |
| ส่วนต่างของสองแบบ | — | +฿3,747.45 = ค่า Pro 90 วัน (15,198 × 90/365) ⇒ มูลค่าต่อวันเท่ากัน |

ฐานเครดิต = **ยอดที่จ่ายจริง** ของรอบนั้น (หลังคูปอง/ราคาพิเศษ/แอดมินแก้ยอด — `PlatformProration.OldPriceBasis` รอบ 231 S2) · ยอดทุกตัว "รวม VAT" ตามค่าตั้ง
`PlatformPriceIncludesVat` (เงินที่รับรวม VAT เสมอ — ใบกำกับถอด 7/107) · กล่องสรุปแตกเป็น "ค่า Pro ส่วนที่เหลือ − เครดิต" โดยผลรวม = ยอดที่คิดจริงพอดี

## 1. ตัวตัดสินเดียว (สิ่งที่สร้างรอบนี้)

* `PlanPurchasePreview.PaidPeriod(...)` — **ตัวคำนวณวันที่ตัวเดียว** ของ preview (วันชำระ = วันนี้) และตอนอนุมัติชำระทั้ง 3 เส้นใน
  `SubscriptionService.ReviewPaymentLockedAsync` (เปลี่ยนรอบ `:3055` · ส่วนต่าง `:3126` · ซื้อ/ต่ออายุ/ดาวน์เกรด `:3184`) — วันชำระตอนอนุมัติ = `ApprovedPaymentDate`
  (ตัวเดียวกับวันที่บนใบเสร็จ) ⇒ "วันที่ที่ preview บอก = วันที่ที่ประทับ" ถ้าจ่ายวันนี้ (Db test `Round233_trial_same_plan_purchase_starts_at_payment_and_matches_preview`)
* `PlanPurchasePreview.ForPurchase/ForScheduled/ForSame/ForAnonymous/Refused` → record `Result` (Kind · Allowed · RefusalReason · NextStep(+Url) · แพ็ก/วันหมดอายุเดิม → ใหม่ ·
  ListPrice · Credit + สูตร · CouponDiscount · AmountDue · VatIncluded · Headline · DateLine · Lines · Notes) — **server computes · page displays**
* ทางเข้า: `SubscriptionService.QuotePlanChangeAsync` (hub `pages/billing.html` · `pages/subscription.html`) · `PublicCheckoutService.QuoteAsync`
  (`/checkout.html` · `/pricing.html`) · `PublicCheckoutService.CreateOrderAsync` (ข้อความปฏิเสธเดียวกัน) · `OwnedCompaniesAsync` (ป้าย "ต่ำกว่าแพ็กปัจจุบัน")
* ล็อก: `tools/required_call_site_check.py` กติกา `_R233_WHY` 8 ข้อ (ทุกทางเข้าเรียก preview · ห้าม `.AddMonths(`/`PeriodStart(` ในทางเข้า preview ·
  ห้าม `baseDate.AddMonths(`/`payment.PaymentDate.AddMonths(`/`sub.EndDate = keepEnd` ในตัวอนุมัติ)

## 2. เมทริกซ์

ทางเข้า: **P** = หน้าซื้อสาธารณะ (`/checkout.html` → `PublicCheckoutService`) · **H** = hub ผู้เช่า (`billing.html`/`subscription.html` → `QuotePlanChangeAsync` →
`ConvertTrialAsync`/`ChangeSubscriptionAsync`) · **A** = แอดมิน (`RecordManualPaymentAsync`/อนุมัติสลิป/เปลี่ยนแพ็กเกจ) · **C** = ตัดบัตรอัตโนมัติ (`PlatformSavedCardService` → ใบต่ออายุ) ·
**R** = ใบต่ออายุ (`SubmitInvoicePaymentAsync`) — ทุกทางที่ "จ่ายเงิน" จบที่ `ReviewPaymentLockedAsync` ⇒ `PaidPeriod` ตัวเดียว

รหัสช่อง (ความหมายเต็มในตาราง §3): **N** ซื้อใหม่ · **RN** ต่ออายุต่อท้าย · **RS** ต่ออายุเริ่มวันชำระ · **UP** ส่วนต่าง คงวันหมดอายุ · **UF** อัปเกรดเต็มรอบเริ่มวันชำระ ·
**CC** รอบยาวขึ้นหักเครดิต · **SC** ตั้งเวลา (ไม่จ่าย มีผลวันครบรอบ) · **X:เหตุ** ปฏิเสธ · **–** ไม่เกี่ยว

| สถานะปัจจุบัน ↓ / การกระทำ → | ต่ออายุแพ็กเดิมรอบเดิม | ต่ออายุก่อนหมด | อัปเกรด | ดาวน์เกรด | รอบยาวขึ้น | รอบสั้นลง | อัปเกรด+รอบ | คูปอง | ยกเลิก | กลับมาซื้อหลังยกเลิก |
| --- | --- | --- | --- | --- | --- | --- | --- | --- | --- | --- |
| 1 ทดลองใช้ (Trial) | N (เริ่มวันชำระ · E-4) | – | N | N (แพ็กใดก็ได้) | N | N | N | ✓ คูปอง/โปรโมชันแปลงทดลอง | ทันที (`SubscriptionCancelPolicy`) | N |
| 2 ทดลองหมดอายุ (Expired+Kind Trial) | N | – | N | N | N | N | N | ✓ | – | N |
| 3 จ่ายแล้ว Active | RN (P/R/C) · H="Same" ชี้ใบต่ออายุ | RN | UP (D≥1 · ราคาเดิม>0) ไม่งั้น UF | SC (H) · X:ต่ำกว่า (P) | CC | SC (H) · X:รอบสั้น (P) | CC (ยาวขึ้น) / UP (รอบสั้นกว่าคงรอบเดิม) | ✓ อัปเกรด/CC · X:ต่ออายุ (P · 233-5) | สิ้นรอบ (`CancelAtPeriodEnd`) | ใช้ต่อ (Resume) หรือจ่าย ⇒ ล้างการยกเลิก |
| 4 ใกล้หมดอายุ (Active, ≤ lead days) | RN | RN | UP/UF | SC/X | CC | SC/X | CC/UP | เหมือน 3 | สิ้นรอบ | เหมือน 3 |
| 5 หมดอายุในช่วงผ่อนผัน (PastDue) | RS (นับจากวันชำระ) | – | N | N (ซื้อแพ็กถูกกว่าได้) | N | N | N | ✓ | ทันที | N |
| 6 เกินผ่อนผัน/ระงับ (Suspended/Expired) | RS · ระงับโดยแอดมิน/dunning คงสถานะ (`StatusAfterPayment`) | – | N | N | N | N | N | ✓ | ทันที | N |
| 7 ฟรีถาวร (Free Edition) | – (ไม่ขาย) | – | N เริ่มวันชำระ (ไม่ +100 ปี) | – | N | – | N | ✓ | – | N |
| 8 ExpiryNeedsReview (แผนจ่ายเงิน EndDate ค่าแทน) | RS (**E-2**) | – | UF (ไม่คิดส่วนต่างจากค่าแทน · E-2) | SC (มีผลทันที) | CC→UF | SC | UF | ✓ | – | – |
| 9 ขอยกเลิกสิ้นรอบ | RN + ล้างการยกเลิก (บอกใน Notes) | RN | UP/UF + ล้าง | X (P) · H: ยกเลิกแล้วล้าง scheduled | CC + ล้าง | X/SC | CC/UP | เหมือน 3 | – | Resume ฟรี (ก่อนสิ้นรอบ) |
| 10 มีรายการรอชำระ/รอตรวจสลิป | resume รายการเดิม (`CanResume` · ไม่มีสลิป/ไม่พัก/ไม่ผูกใบ) · มีสลิป ⇒ สร้างใหม่ไม่ได้ซ้อน (ด่าน PlatformDoublePayment) | เหมือนซ้าย | ส่วนต่างใบเก่าถูกแทน (`SupersedePendingPaymentsAsync`) | SC | ใบเก่าถูกแทน | SC | เหมือนกัน | คูปองเดิมต้องตรง | – | – |
| 11 ใบต่ออายุออกแล้ว | X:ชำระจากใบ (P · พาไปแท็บใบแจ้งหนี้) · R | X | UP + ใบเดิมถูกแทน/คง (`RenewalInvoiceClaims`) | X:ใบออกแล้ว (H `ScheduleBlockedByIssuedRenewal`) | CC + ใบเดิมถูกแทน | X | CC/UP | – | สิ้นรอบ (ใบคงตัวชี้) | – |
| 12 ราคาพิเศษ (PriceOverride) | RN ราคาพิเศษ (`AppliesToRenewals`) | RN | UP ฐานเครดิต = ยอดจ่ายจริง | SC | CC | SC | CC | ✓ ซ้อนบนราคาพิเศษ | – | – |
| 13 license หลายบริษัท (AccountSubscription) | RN บริษัท + cascade แพลนบัญชีเฉพาะแพ็กตรง (`MayCascadeToAccountPlan`) | RN | UP/UF (ไม่ cascade) | SC | CC | SC | CC | ✓ | – | – (K-3) |
| 14 โหมดทดสอบ (intent `IsTestMode`) | จ่ายได้ ไม่เปิดสิทธิ์ ไม่ออกใบ (`TestModePaymentPolicy`) — preview เหมือนจริง + ป้าย "โหมดทดสอบ" | ← | ← | ← | ← | ← | ← | ← | ← | ← |
| 15 License lapsed (บริษัทผูก License ที่หมดอายุ/ระงับ/ยกเลิก · รอบ 238 · คำตัดสิน 236-3) | ผู้ถือ: RN License (แพ็กตรง · ไม่ใช่ระงับ/ยกเลิก) · คนอื่น X:ผูก License (ทางไปต่อ = ผู้ถือต่ออายุ หรือถอด) | ← | X | X | X | X | X | – | – | X |

> **แถว 15 (รอบ 238):** License **ยังเป็นตัวคุม ไม่ถอยไปใช้แถวบริษัทอัตโนมัติ** · ระยะผ่อนผันมีจริง: หลังวันหมดอายุ ≤ `GracePeriodDays` ⇒ License = **PastDue เขียนได้เต็ม**
> (`PlanIdentityResolver.LicenseStatusAfter` · เดิม Expired ทันที = ผ่อนผัน 0 วัน) · พ้นผ่อนผัน ⇒ Expired = **อ่านอย่างเดียว** · License ระงับ/ยกเลิก ⇒ ด่านเห็นเป็นหมดอายุ
> (`PlanIdentityResolver.GateStatus` — อ่าน/ส่งออกได้ เขียน 402 · เดิม 403 ทุกคำขอ) · ไฟล์ยื่นแบบ e-Filing + POST ส่งออก ผ่านด่านหมดอายุเสมอ (`SubscriptionGatePolicy.IsComplianceOutputPath` · ไม่ผ่านด่านบริษัทถูกระงับ) ·
> งานเตือนไม่เขียนแถวบริษัท (เลิก `CascadeExpiredFromLicense` · แถวที่ถูก cascade แล้วคืนสถานะด้วย `LicenseLapseMigration`) · แจ้งเจ้าของบริษัทใต้ License (T-7 · วันหมดอายุ — บิต 16/32) ·
> **ถอด** (เจ้าของบริษัท/ผู้ถือ License · `GET detach-preview` บอก "หลังถอดจะใช้ {แพ็กเกจ} ถึง {วันที่} / ซื้อได้ทันที") ⇒ แถวบริษัทกลับมามีผลตามที่เก็บไว้ (Pro ที่จ่ายไว้ยังไม่หมด = ใช้ต่อ · หมดแล้ว = ซื้อได้ทันที = แถว 2/6) ·
> ผูกบริษัทที่จ่ายเองเข้า License ⇒ เวลาที่จ่ายไว้ **เดินต่อ ไม่คืนเงิน** + คำเตือนก่อนผูก (คำตัดสิน 236-9 ค่าตั้งต้น รอเจ้าของยืนยัน) · Db `LicenseLapseRound238DbTests` · pure `LicenseLapseRound238Tests`
> **ฝ่ายค้านรอบ 238 (236-10):** ถอดแถวที่เลยวันหมดอายุ ⇒ Expired ทันที (ไม่ใช่ Active ที่งานเดินถึง Suspended) · License ระงับ ⇒ เขียน 402 ทุกโหมด (รวม partner) ·
> ผูกในช่วงผ่อนผัน = เตือน · ผูกอัตโนมัติเฉพาะ License ที่ยังไม่หมดอายุ · ผู้ไม่เกี่ยวข้องเรียก `effective`/พรีวิว/ถอด = 404

## 3. ความหมายของช่อง (ยอด · วันที่ · เครดิต · เอกสาร)

| รหัส | ยอดที่ต้องจ่าย (สูตร) | วันเริ่ม → วันหมดอายุใหม่ | เครดิต | เอกสาร | โค้ด |
| --- | --- | --- | --- | --- | --- |
| **N** | ราคาพิเศษ ?? แม่แบบ → โปรโมชันแปลงทดลอง % → คูปอง | วันชำระ → วันชำระ + รอบ (ทดลองที่เหลือสิ้นสุด · 233-7) | – | ใบเสร็จ/ใบกำกับ (คูปอง = ส่วนลดก่อน VAT) | `QuotePlanChangeCoreAsync` "New" · `ConvertTrialAsync` · `PaidPeriod` |
| **RN** | ราคาต่ออายุ (`AmountDueAsync(currentPlan)`) | วันหมดอายุเดิม → +รอบ (ไม่เสียวันที่เหลือ · 233-1) | – | ใบแจ้งหนี้ต่ออายุ (R/C) หรือใบเสร็จ | `PeriodStart` samePlan · `PublicCheckoutService.RenewalPreviewAsync` |
| **RS** | ราคาต่ออายุ | วันชำระ → +รอบ (หมดแล้ว · 233-1) | – | ใบเสร็จ | `PeriodStart` (EndDate ≤ วันชำระ) |
| **UP** | round((ใหม่ − เดิมจ่ายจริง) × D/C, 2) | วันชำระ → **วันหมดอายุเดิม** | แสดง = เดิม × D/C (สูตร + วัน) | ใบเสร็จ "ค่าส่วนต่างอัปเกรด … (D/C วัน)" | `PlatformProration.Upgrade` · `PaidPeriod(isProration)` |
| **UF** | ราคาเต็มรอบ | วันชำระ → +รอบ (วันที่เหลือไม่ถูกหัก — บอกใน Notes) | – | ใบเสร็จ | `PeriodStart` Upgrade |
| **CC** | max(0, ราคารอบใหม่ − เดิม × D/C) | วันชำระ → +รอบใหม่ | เดิม × D/C | **บรรทัด = ค่ารอบใหม่เต็ม + ส่วนลดเครดิตก่อน VAT** (233-4 · E-8) | `PlatformProration.CycleChange` · `PaidPeriod(isCycleChange)` |
| **SC** | 0 | มีผลวันครบรอบ (ฟรีถาวร/หมดแล้ว = ทันที) | ไม่คืน | – | `ScheduledChangeEffectiveAt` · `ForScheduled` |
| **X** | – | – | – | – | `PublicCheckoutPolicy.DecideOrder` → `PlanPurchasePreview.Refused` (เหตุ + วันที่ + ทางไปต่อ) |

ข้อความปฏิเสธ (ตัวอย่างจริงจากเทสต์): "บริษัทนี้ใช้ Enterprise ถึง 01/03/2570 — Professional ต่ำกว่าแพ็กเกจปัจจุบัน จึงชำระที่หน้านี้ไม่ได้ · ลดเป็น Professional ได้ที่หน้าแพ็กเกจในระบบ
มีผล 01/03/2570 (ไม่ต้องชำระตอนนี้ · ไม่คืนเงินส่วนต่าง)" → ลิงก์ `/pages/billing.html#tab=plan` — แสดง**ในกล่องสรุป (กล่องแดง) และใต้ปุ่ม** · ปุ่มเป็น "ชำระไม่ได้" (ปิด)

## 4. ผิด/ขาดที่พบ (E) — แก้แล้วทั้งหมดในรอบนี้

| # | ช่องที่กระทบ | ก่อนแก้ (อ่านจริง) | แก้ | เทสต์ (reproduce + ทิศตรงข้าม) |
| --- | --- | --- | --- | --- |
| E-1 | ทุกช่องที่จ่ายเงิน (96) | กล่องสรุปบอก "ชำระ Enterprise 12 เดือน — เริ่มรอบใหม่จากวันที่ชำระ" ไม่มีวันที่ · ต่ออายุบอก "ต่อจากวันหมดอายุเดิม" ไม่บอกวัน (`PublicCheckoutService.QuoteAsync` เดิม) | `PlanPurchasePreview` + `checkout.html`/`billing.html`/`subscription.html`/`pricing.html` แสดง "ใช้ได้ถึง เดิม → ใหม่" | `ต่ออายุก่อนหมด_…` `ต่ออายุหลังหมด_…` `ยังไม่ล็อกอิน_…` |
| E-2 | 8×(ต่ออายุ/อัปเกรด) | EndDate ค่าแทน (+100 ปี) ⇒ `PeriodStart` คืนปี 2126 ⇒ `SubscriptionExtendedTo`/ประวัติ/แจ้งเตือน "ต่ออายุถึง 2670" แล้ว `ApplyTemplate` ปิดรอบทับเป็นวันนี้+รอบ (ข้อมูลสองค่าขัดกัน) · ส่วนต่างคิดจากรอบค่าแทน | `PeriodStart`: ค่าแทน ⇒ วันชำระ · `PlatformProration.Upgrade` ค่าแทน ⇒ ไม่ใช่ส่วนต่าง · อนุมัติปลด `ExpiryNeedsReview` | `วันหมดอายุค่าแทน100ปี_…` · Db `Round233_renewal_on_open_ended_paid_plan_…` |
| E-3 | 3/4 จากแพ็กราคา 0 ที่ Active | `PlatformProration.Upgrade(0, ใหม่)` Applies ⇒ จ่าย ใหม่ × D/C แล้ว "คงวันหมดอายุเดิม" (ได้แพ็กจ่ายเงินแค่ถึงวันหมดทดลอง) | ราคาเดิม ≤ 0 ⇒ ไม่ใช่ส่วนต่าง (จ่ายเต็มรอบ เริ่มวันชำระ) | `แพ็กเดิมราคา0_…` (+ ทิศตรงข้าม 590→1,490 ยังคิดส่วนต่าง) |
| E-4 | 1×(ซื้อแพ็กเดียวกับที่ทดลอง/ถูกกว่า) | ทดลอง Pro → ซื้อ Pro = ต่อท้ายวันทดลอง · ทดลอง Pro → ซื้อ Enterprise = เริ่มวันนี้ (ไม่สม่ำเสมอ · หน้าชำระบอก "เริ่มรอบใหม่จากวันที่ชำระ") | 233-7: ทดลอง ⇒ เริ่มวันชำระเสมอ | `ทดลองใช้แพ็กเดียวกัน_…` · Db `Round233_trial_same_plan_…` |
| E-5 | ทุกช่องที่วันที่ตก 29 ก.พ. | `QuotaAddOnPricing.ThaiDateText` ใช้ `AddYears(543)` ⇒ 29/02/2028 แสดง "28/02/2571" (ค.ศ. 2571 ไม่อธิกสุรทิน) บนใบเสร็จส่วนต่าง/โควตาเสริม/preview | บวกเลขปีตรง ๆ | `วันที่ไทย_29กพ_…` |
| E-6 | 3/4/9/11 × ดาวน์เกรด/รอบสั้น/คูปองต่ออายุ/ใบออกแล้ว (P) | `QuoteAsync` throw ⇒ ข้อความ error ลอยในคอลัมน์ตะกร้า ไม่มีแพ็กปัจจุบัน/วันที่/ลิงก์ | ใบเสนอ `Preview.Allowed=false` + เหตุ + วันมีผล + ลิงก์ · กล่องแดง + ใต้ปุ่ม · `CreateOrderAsync` ข้อความเดียวกัน | `ดาวน์เกรด_ปฏิเสธ_…` `รอบสั้นลง_…` `ต่ออายุพร้อมคูปอง_…` `ใบต่ออายุออกแล้ว_…` `เหตุอื่น_…` + ทิศตรงข้าม `ทิศตรงข้าม_อัปเกรด_ไม่ถูกปฏิเสธ` |
| E-7 | ทุกช่องที่อนุมัติจากสลิปภายหลัง | เส้นซื้อ/ต่ออายุเริ่มนับจาก "เวลาที่แอดมินกดอนุมัติ" (`now`) แต่เส้นเปลี่ยนรอบนับจาก `PaymentDate` — สองนิยาม "วันชำระ" | ทุกเส้นใช้ `ApprovedPaymentDate` (= วันที่บนใบเสร็จ · tax point) ผ่าน `PaidPeriod` · ล็อก `call_args` (`PaidPeriod(changeKind` ต้องรับ `paidAt` — ใส่ `now` กลับ ⇒ ฟ้อง) | Db `Round233_S1_backdated_slip_period_starts_at_payment_date_not_approval` (สลิปย้อน 5 วัน · แพ็กหมดไป 10 วัน ⇒ วันรับเงิน + 1 เดือน) — แก้การอ้างเทสต์เดิมที่ผิด (`Mid_cycle_upgrade_…` ใช้วันรับเงิน = now ตรวจความต่างไม่ได้) |
| E-8 | CC | บรรทัดใบกำกับ = ยอดสุทธิหลังหักเครดิต เครดิตอยู่แค่ในคำอธิบาย | `InvoiceDiscountGross` ⇒ บรรทัด = ค่ารอบใหม่เต็ม + ส่วนลดเครดิตก่อน VAT (§86/4) | `ใบกำกับ_เครดิตเปลี่ยนรอบเป็นส่วนลดก่อนVAT_…` |
| E-9 | 3/4 × ดาวน์เกรด (pricing) | การ์ดแพ็กที่ถูกกว่าใส่ตะกร้าได้ แล้วไปรู้ตอน checkout | `/me` คืน `lowerPlans` ต่อบริษัทที่ผู้ใช้เป็นเจ้าของ (ตัดสินที่เซิร์ฟเวอร์ — เงื่อนไขเดียวกับ `DecideOrder`) · การ์ดติดป้าย | กติกา `OwnedCompaniesAsync` ใน required_call_site_check |
| E-10 (B1) | 7×(ซื้อ) ฟรีถาวร → แพ็กจ่ายเงิน | `ApplyTemplate` เห็นธงฟรีถาวร ⇒ `CloseOpenEnded(UtcNow)` ทับ EndDate ที่ตั้งจากวันรับเงิน ⇒ subscription ≠ `SubscriptionExtendedTo`/ใบเสร็จ | `ApplyTemplate(..., paidPeriodEndUtc)` ปิดรอบด้วยวันหมดอายุของรอบที่จ่าย (เส้นซื้อ + เปลี่ยนรอบ) | Db `Round233_B1_permanent_free_purchase_end_date_matches_payment_record` |
| E-11 (B2) | 5/6 × สลิปลงวันที่ย้อนหลังมาก | วันรับเงิน + รอบ ≤ เวลาอนุมัติ ⇒ จ่ายแล้วได้รอบที่หมดไปแล้ว | `PaidPeriod(approvedAtUtc)`: รอบที่คิดจบก่อนอนุมัติ ⇒ นับจากเวลาอนุมัติ + ประวัติ `PaidPeriodStartedAtApproval` "วันรับเงินย้อนหลังเกินรอบ — นับจากวันอนุมัติ" · ทิศตรงข้าม 233-1b | `B2_วันรับเงินย้อนหลัง_…` · `B2_ทิศตรงข้าม_โอนก่อนหมดอายุ_…` |
| E-12 (S2) | UP/CC ของลูกค้าที่หัก ณ ที่จ่าย | ฐานเครดิต = เงินที่เข้า (หลังหัก 3%) ⇒ ตัวอย่างเจ้าของคิด ฿7,043.56 | `LastApprovedCycleAmountAsync` = `Amount + WithholdingTaxAmount` ⇒ ฿6,916.44 | `S2_ฐานเครดิต_ก่อนหักณที่จ่าย_…` · Db `Round233_S2_S3_…` |
| E-13 (S3) | UP/CC หลังรอบที่ได้ฟรี (คูปอง 100%/ยกเว้น) | รายการ Approved ยอด 0 ถูกกรองทิ้ง ⇒ ฐาน = ราคาแม่แบบ = เครดิตเงินที่ไม่เคยจ่าย | Approved ยอด 0 ⇒ เครดิต 0 · ราคาแม่แบบเฉพาะไม่มีรายการ Approved เลย (`OldPriceBasis` null vs 0) | `S3_…` · Db `Round233_S2_S3_…` · แก้ `PlatformPriceOverrideRound231Tests` แถว 0 |
| E-14 (N7) | 8 × ดาวน์เกรด | ค่าแทน +100 ปี ⇒ ตั้งเวลาเปลี่ยนมีผลปี 2126 · ดาวน์เกรดที่จ่ายเงินถูกเลื่อนไป "วันชำระ" (อดีต) | `ScheduledChangeEffectiveAt`/`DeferToCycleEnd`: ค่าแทน = ไม่มีวันครบรอบ ⇒ มีผลทันที ไม่ตั้งเวลา | `N7_…` |
| E-15 (S5 · เดิม K-6) | 10 × resume ส่วนต่างเก่า | ยอดคิดจากวันที่เหลือ ณ วันสร้าง | `PublicCheckoutPolicy.MustRequote`: ยอดใบเสนอวันนี้ ≠ ใบเดิม และยังไม่เริ่มชำระออนไลน์ ⇒ ออกรายการใหม่ผ่านเส้นเดิม (ยกเลิกใบเก่าด้วย `SupersedePendingPaymentsAsync` — ไม่ซ้อน) · มี intent แล้ว ⇒ ใช้ใบเดิม | `S5_resume…` · Db `Resume_stale_proration_requotes_and_cancels_old_without_duplicates` |
| E-16 (S5 · เดิม K-3) | 13 | แพลนบัญชี (ตัวคุมสิทธิ์ของบริษัทที่ผูก — `GetEffectivePlanAsync`) นับจากเวลาอนุมัติเอง ⇒ คนละวันกับ subscription | cascade ใช้ `PaidPeriod` + `paidAt` + `approvedAtUtc` เดียวกัน | Db `Round233_K3_account_plan_cascade_uses_same_paid_period_as_subscription` |
| E-17 (N2/N3) | CC เครดิตเกินค่ารอบ · ทุกช่อง (ป้าย VAT) | preview แสดง "ค่ารอบใหม่ = ฿เครดิต" · ป้าย "รวม VAT" ใช้แค่ธงจด VAT (ตัวออกใบตรวจเลขผู้เสียภาษีด้วย) | `Money.NewCyclePrice` + `Allowed=false` (`CreditCoversAllRefusal` = ข้อความเดียวกับ `RequestPlanPaymentAsync`) · `PlanPurchasePreview.IssuesTaxInvoice` ตัวเดียวทั้ง preview และตัวออกใบ 4 จุด | `N2_…` `N3_…` |
| E-18 (N5/N6) | pricing · ทดลองใช้ | ป้ายต่ำกว่าแพ็กประกอบข้อความในหน้าเว็บ · ข้อความทดลองไม่บอกว่าวันที่เหลือไปไหน | `lowerNote` จากเซิร์ฟเวอร์ (`LowerPlanNote` มีวันมีผล) · "วันทดลองที่เหลือ N วันไม่ถูกนำไปต่อท้าย" | `N5_…` · `ทดลองใช้แพ็กเดียวกัน_…` |

## 5. ค้าง (K) — พร้อมเหตุผล

| # | เรื่อง | ทำไมไม่ทำรอบนี้ | ทางต่อ |
| --- | --- | --- | --- |
| ~~K-1~~ | แบบ ข (เริ่มรอบใหม่หักเครดิต) | **ทำแล้วรอบ 237 (คำตัดสินเจ้าของ 2026-11-04 "ทำให้ระบบรองรับทั้ง 2 ทาง สามารถเลือกได้" · DECISIONS 237-1…237-7)** — ตัวตัดสินเดียว `PlatformProration.UpgradeByMode` (แบบ ข = `CycleChange` รอบเท่าเดิม `sameCycleUpgrade`) · ค่าตั้ง `SiteSettings.PlatformUpgradeModeOffer` (CustomerChooses ค่าตั้งต้น เลือกแบบ ก ไว้ให้ / KeepExpiryOnly / RestartCycleOnly — หน้า `/admin/site-settings.html`) · แบบที่เลือกประทับ `SubscriptionPayment.UpgradeMode` · อนุมัติใช้แบบที่ประทับ (`ApprovalRouteOf` — ไม่อ่านค่าตั้ง) · เครดิต ≥ ราคาเต็ม ⇒ ปฏิเสธแบบ ข (237-3) · License ขยับตามแบบ ข (237-7) · ตัวเลข §0 ล็อกด้วย `UpgradeModeRound237Tests` · Db `UpgradeModeRound237DbTests` · DOCUMENT_FLOW §2.12e | – |
| ~~K-2~~ | คูปองตอนต่ออายุ | **ทำแล้วรอบ 235 (คำตัดสิน 235-1 แทน 233-5)** — ต่ออายุสาธารณะผ่าน `RequestRenewalPaymentAsync` → `RequestPlanPaymentAsync` (จองใต้ล็อก · ลดก่อน VAT · นับเมื่อเงินเข้า) · `PlatformCoupon.PurchaseKinds` New/Renewal/Both (ค่าตั้งต้น New — แอดมินเปิดต่ออายุเอง · ราคาพิเศษต่ออายุไม่ลดซ้อน 235-1b) | – |
| ~~K-3~~ | แพลนบัญชี | **ทำแล้ว = E-16** (หน้า License ยังไม่มี preview — รวมกับ K-7) | – |
| ~~K-4~~ | `AddMonths` บนเวลา UTC | **ทำแล้วรอบ 235 (235-2)** — `PlatformPlanPricing.AddCycleMonths` (ปฏิทินไทย) ใน PaidPeriod/Proration/CloseOpenEnded · มีผลรอบใหม่ (ไม่ migration) | – |
| ~~K-5~~ | `AddYears(543)` อีก 18 จุดใน `AccountingService`/`IntegrationService` | **ทำแล้ว รอบ 235 (DECISIONS 235-X1)** — ทั้ง 18 จุดเป็นการแปลงข้อมูลเข้า/ซ่อมข้อมูล ⇒ ตัวตัดสินกลาง `ThaiDate.TryNormalizeEra`/`NormalizeEra` (+ `BankCsvParser` 2 จุด) · 29 ก.พ. ไม่แต่ง · เทสต์ `BuddhistEraNormalizeRound235Tests` | – |
| ~~K-6~~ | resume ส่วนต่างเก่า | **ทำแล้ว = E-15** | – |
| ~~K-7~~ | แอดมินไม่เห็น preview | **ทำแล้วรอบ 235 (235-3)** — `record/preview` + `approval-preview` ⇒ `PlanPurchasePreview.ForPurchase`/`ForInvoiceOnly` ตัวเดียว · `payments.html` + `customers.html` · ภาษีหักที่ระบบเสนอ + ตัวตรวจเดียว (235-6) | – |
| ~~K-8~~ | อีเมลยืนยันคำสั่งซื้อไม่มี DateLine | **ทำแล้วรอบ 235 (235-4)** — `PlatformPurchaseEmailText` (คำสั่งซื้อ + ใบเสร็จ: แพ็ก·รอบ·ใช้ได้ถึง พ.ศ.·ยอด·ภาษีหัก) | – |

## 5b. ช่วงผ่อนผันระหว่างรอตรวจสลิป (233-1b · ฝ่ายค้าน S4 — ยืนยันจากโค้ด)

สลิปโอนก่อนหมดอายุแต่ตรวจหลังหมด ⇒ ต่อจาก EndDate เดิม (`PaidPeriod` ไม่มีช่องว่าง/ไม่ซ้อน ตรงบัญชี) · ระหว่างรอตรวจ: `ProcessExpiredSubscriptionsAsync` ตั้ง **PastDue** เมื่อเลย EndDate
แล้ว **Suspended** หลัง `Subscription.DeactivationDaysAfterExpiry` (ค่าตั้งต้น 14 วัน) **เฉพาะเมื่อแอดมินเปิด "ระงับอัตโนมัติ"** (`PlatformBillingSettings.AutoSuspendEnabled`) ·
ด่านเขียน (`SubscriptionGatePolicy` — PlanExpired หลังพ้นผ่อนผัน) บังคับจริงเมื่อโหมด Enforce เท่านั้น (ค่าตั้งต้น Shadow = บันทึกไม่บล็อก) ⇒ **มีผ่อนผันคุมสิทธิ์** (14 วัน) แต่ขึ้นกับสวิตช์แอดมินสองตัว —
ถ้าแอดมินตรวจสลิปช้ากว่า 14 วันและเปิดระงับอัตโนมัติ ลูกค้าถูกระงับระหว่างรอ (จ่ายแล้วปลด: `StatusAfterPayment` เปิดคืนเมื่อระงับเพราะหมดอายุ)

## 6. Self-review (F3 ข้อ 11)

1. **ทางเข้าอื่น?** P/H/A/C/R ทุกทางที่จ่ายเงินจบที่ `ReviewPaymentLockedAsync` ⇒ `PaidPeriod` ตัวเดียว (ล็อก) · preview: P + H (`billing.html`, `subscription.html`) + แอดมิน/อีเมล (รอบ 235 — K-7/K-8 ปิดแล้ว)
2. **ทิศตรงข้าม?** ทุกด่านที่เข้มขึ้นมีเทสต์ "ที่ถูกอยู่แล้วไม่ถูกแตะ": Active ต่ออายุยังต่อท้าย · วันหมดอายุจริง 2 ปียังต่อท้าย · ราคาเดิม > 0 ยังคิดส่วนต่าง · อัปเกรดไม่ถูกปฏิเสธ · ไม่ใช่เปลี่ยนรอบ ⇒ ส่วนลดบนใบ = คูปองอย่างเดียว · วันที่ปกติไม่เปลี่ยน
3. **สถานะปลายทางประทับเอง?** ไม่มีสถานะใหม่ — preview อ่านอย่างเดียว · `ExpiryNeedsReview=false` ประทับเฉพาะเมื่อเงินเข้าแล้วได้รอบจริง (วันหมดอายุไม่ใช่ค่าแทน)
