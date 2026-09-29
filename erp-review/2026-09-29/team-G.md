# รอบ 200 — ทีม G: payment gateway ส่วนที่ยังไม่เคยถูกตรวจ

> ขอบเขต (BRIEF): `GatewaySettlementService` G-2/G-3/G-4 · `PaymentGatewayController` G-8 ทุก endpoint · หน้าเว็บ gateway · E-3 · E-4 · E2-12 ที่ค้าง ·
> DECISIONS ข้อ 18 (`MinVerifyWait` · `Omise-Version`) · มุมมอง security + นักบัญชี + วิศวกร payment · base `5c1fe028` · คอมมิตแก้ `e004a2cc`
> **ยังไม่ได้คอมไพล์** (ไม่มี .NET SDK ในเครื่องนี้) — CI บน `claude/**` คือ compiler ตัวแรก

## สรุป

| ID | สถานะ | P | เรื่อง | แก้ที่ | เทสต์ |
|---|---|---|---|---|---|
| G-R18 | ✅ | P1 | adapter **ไม่ปักรุ่น API** ⇒ ชื่อช่องขึ้นกับรุ่นตั้งต้นของบัญชีผู้ขาย · รุ่น 2017-11-02 ใช้ `refunded` (เป็นยอด) ส่วน adapter อ่าน `refunded_amount` (ชื่อของรุ่น 2019-05-29 — ยืนยันจากคู่มืออัปเกรดของผู้ให้บริการ) ⇒ ยอดคืนสะสม = null เงียบ ⇒ "ตรวจผลการคืนเงิน" = ProviderSilent ตลอด | `OmisePaymentProvider.ApiVersion = "2019-05-29"` + `ApiVersionHeader` ส่งใน `Client()` ทุกคำขอ (ค่าคงที่ตัวเดียว) | `R18_ทุกคำขอส่งหัวรุ่นAPIที่adapterอ่านชื่อช่อง` (fake handler จับหัวจริงของคำขอ 2 เส้น) |
| G-R18b | ✅ ตัดสินแล้ว | — | `MinVerifyWait` | คงเดิม 10 นาที (DECISIONS ข้อ 18) — ไม่แตะโค้ด | เทสต์ E2-2 เดิม |
| G2-1 | ✅ CONFIRMED | P1 | **ขอบช่วง "วันที่รับเงิน" เลื่อน 7 ชั่วโมง** — `BuildPlanAsync` เทียบ `ConfirmedAt` (เวลา UTC จริง) กับ `ThaiDate.CalendarDateUtc(วันที่)` ซึ่งเป็นป้ายวันไทยที่ 00:00 **UTC** ⇒ รับเงิน 00:00–07:00 ของวันแรกหลุด · 00:00–07:00 ของวันถัดจากวันสุดท้ายถูกนับ ⇒ รอบโอน "ยอดไม่ตรง" ทั้งที่ค่าธรรมเนียมถูก (ผู้ใช้ถูกชี้ไปแก้ค่าธรรมเนียมผิดจุด) · รายงานกระทบยอด (`.Date` ของเวลา UTC) คลาสเดียวกัน | `GatewaySettlementMath.ConfirmedRangeUtc` (เที่ยงคืนเวลาไทย · สูตรเดียวกับ `RefundCutoffUtc`) ใช้ทั้ง `BuildPlanAsync` และ `GET pay/reconciliation` | `RANGE_*` สองทิศ |
| G2-2 | ✅ PLAUSIBLE | P2 | ค่าธรรมเนียมจากผู้ให้บริการ**ทับ** `FeeActual` ทุกครั้งที่สถานะเปลี่ยน (`ApplyChargeToEntity`) ⇒ ค่าที่ผู้ใช้แก้พร้อมเหตุผล + hash chain (G-6) หรือค่าที่อยู่ในใบสำคัญรอบโอนแล้ว ถูกทับเงียบ | `PaymentIntentPolicy.ShouldTakeProviderFee` (รับเฉพาะยังไม่มีค่า หรือรายการยังเปิด · ไม่เคยทับเมื่อบันทึกรอบ/อยู่ใน batch) | `FEE_*` สองทิศ |
| G2-3 | NOT-A-BUG | — | สูตร `Contribution`/`Plan`: ไล่ 11340 ครบ 4 เคส (สำเร็จ · คืนบางส่วนก่อนรอบ · คืนหลังวันเงินเข้า · คืนหลังรอบก่อน) ⇒ 11340 กลับเป็น 0 ทุกเคส · Dr = Cr ทุกโหมด · gross ≤ 0 / fee > gross ⇒ `NegativeNet` บล็อก | — | เทสต์เดิม `GatewaySettlementMathTests` |
| G3-1 | NOT-A-BUG + หมายเหตุ | — | VAT ค่าธรรมเนียม 3 โหมดถูก (ปัดรายรายการ · บริษัทไม่จด VAT ไม่มีขา 11630) · **หมายเหตุ**: รุ่น 2019-05-29 ช่อง `fee` = ก่อน VAT (มี `fee_vat` แยก) ⇒ โหมดที่ตรงกับผู้ให้บริการนี้คือ `AddedOnTop` · ค่าเริ่มต้น `None` บนบริษัทจด VAT มีคำเตือน R-E6 แล้ว · อ่าน `fee_vat` = G-7 (📋 รอ sandbox) | — | — |
| G4-1 | NOT-A-BUG | — | WHT ค่าธรรมเนียม: ฐานก่อน VAT × 3/97 · แผนที่มี WHT ถูกบล็อก `WhtCertificateRequired` เสมอ (ไม่มี 21917 ที่ไม่มี 50 ทวิ) | — | เทสต์เดิม |
| G8-1 | ✅ CONFIRMED | P1 | `GET pay/intents/{id}/status` มีแค่ `[Authorize]` — `live=true` (ค่าเริ่มต้น) ถามผู้ให้บริการแล้ว **`ApplyChargeAsync` เปลี่ยนสถานะ + ส่งต่อให้ต้นทางออกใบเสร็จ/ตัดหนี้** ได้โดยสมาชิกคนไหนก็ได้ (รวมบทบาทดูอย่างเดียว) ของทุกโมดูล | สิทธิ์เริ่มรับชำระของต้นทาง **หรือ** `Bank.View` (`PaymentGatewayPermissionScope.StatusKeysFor` · ตรวจก่อน `RefreshAsync`) | `G8_*` + `required_call_site` (ลำดับ) |
| G8-2 | ✅ CONFIRMED | P2 | `GET pay/intents` · `intents/{id}/events` · `reconciliation` · `settlements/pending` ไม่มีด่านสิทธิ์ (ข้อมูลระดับบัญชีธนาคาร + เหตุผล/หลักฐานคืนเงิน) — endpoint พี่น้อง `settlements/preview`/`fee-vat` ใช้ `Bank.View` อยู่แล้ว | `[RequirePermission(Bank.View)]` (`ViewPayments` · `PreviewSettlement`) | `G8_*` |
| G8-3 | ✅ PLAUSIBLE | P2 | `confirm-manually` บนช่องทางที่ผู้ให้บริการถือเงินไว้ก่อน แต่รายการ**ไม่มี charge ที่ผู้ให้บริการ** (สร้างไม่สำเร็จ · `ProviderRef` ว่าง) ⇒ Dr 11340 ด้วยเงินที่ผู้ให้บริการไม่มีวันโอนมา (หน้าเว็บซ่อนปุ่มแต่ API รับ) | `PaymentIntentPolicy.ManualConfirmBlockReason` + ทางไปต่อ "บันทึกรับชำระที่ต้นทางโดยตรง" | `MC_*` สองทิศ |
| G8-4 | ✅ PLAUSIBLE | P2 | webhook สาธารณะ: `id` จาก body ถูกต่อเข้า `/events/{id}` ของคำขอที่แนบ **secret key ของทุกบริษัท** โดยไม่ตรวจรูป (`../charges/…` · `?…`) | `OmisePaymentProvider.IsWellFormedEventId` (`evnt_[A-Za-z0-9_]+` · ไม่ผ่าน = ไม่ยิงออกเลย) | `WH_*` (รวม "ไม่ยิงคำขอ" ด้วย fake handler) |
| G8-5 | 📋 | P2 | webhook: 1 POST นิรนาม = N คำขอออกไปผู้ให้บริการ (N = config ที่เปิดใช้ทุกบริษัท) · เพดานรวม 600/นาที/IP ⇒ ยิงเลข event รูปถูกแต่ปลอมได้ = ใช้โควตาผู้ให้บริการของทุกร้าน | เสนอ: token ลับต่อ config ใน URL webhook (`/api/pay/webhooks/{provider}/{token}` → ลองเฉพาะ config นั้น) — เปลี่ยนขั้นตั้งค่าของผู้ใช้ ⇒ ต้องออกแบบ migration ของ URL เดิม (ห้ามทำครึ่งทาง) | — |
| G8-6 | NOT-A-BUG | — | endpoint เขียนที่เหลือ: `POST intents` (สิทธิ์ต้นทาง) · `refund`/`verify` (`Bank.PaymentInit` + `[RejectApiKey]`) · `resolve-manually` (`[RequireOwner]` + `[RejectApiKey]`) · `PUT fee`/`POST settlements`/`fee-vat/claim` (`Journal.Manage` + `[RejectApiKey]`) · `preview` (`Bank.View` อ่านอย่างเดียว) · tenant: `TenantAccessMiddleware` + ทุก query `CompanyId` · webhook ownership R-E1 · บัตร: `ChargeRequest` มีแค่ token (`payment_provider_boundary_check` เขียว) | — | checker เดิม |
| PG-1 | ✅ CONFIRMED | P2 | `payment-intents.html` ประวัติรายการอ่าน `e.fromStatus/e.toStatus` แต่ `GET events` ส่ง `from/to` ⇒ **ทุกแถว "— →" ว่าง** — หน้าที่มีไว้ตอบ "ลูกค้าบอกว่าจ่ายแล้วแต่ระบบไม่รู้" ตอบไม่ได้ | อ่าน `from/to` | — (JS) |
| PG-2 | ✅ PLAUSIBLE | P3 | `payment-settlements.html` แก้ช่องหลังดูตัวอย่างแล้วปุ่มบันทึกยังเปิด ⇒ บันทึกชุดที่ไม่ได้เห็นรายการบัญชี (เซิร์ฟเวอร์ยังตรวจยอดซ้ำ — ไม่ผิดเงิน แต่ผิดสัญญา "ต้องเห็น JE ก่อน") | `bindPreviewInvalidation` ปิดปุ่ม + ข้อความ | — (JS) |
| PG-3 | ✅ CONFIRMED | P1 (security) | `pos.html`: ชื่อเครื่องขายต่อเข้า `<option>` ดิบ · ชื่อสินค้าในตะกร้า `${c.name}` ดิบ ⇒ stored XSS ใส่แคชเชียร์/เจ้าของ (JWT ใน localStorage) | `Layout.esc` | — (JS · `html_attr_escape_check` ไม่ครอบข้อความใน element) |
| PG-4 | NOT-A-BUG | — | หน้า gateway 3 หน้า + `pay-widget.js`: ทุกช่องที่ผู้ใช้/ผู้ให้บริการคุมได้ผ่าน `Layout.esc`/`esc` · onclick ใช้ `Layout.jsArg`/GUID · สถานะ enum ส่งเป็นชื่อ | — | — |
| PG-5 | 📋 | P3 | ป้ายสถานะ/ที่มาแสดงชื่อ enum อังกฤษ (`Succeeded` · `SiteOrder`) · เงื่อนไขปุ่มคืนเงิน + เกณฑ์ "ค้าง 30 นาที" เป็นสำเนาใน JS (หลักการ 5) | ให้เซิร์ฟเวอร์ส่ง `statusLabel`/`canRefund`/`isStuck` | — |
| PG-6 | 📋 | P3 | `ReturnUrl` จากทางเข้าสาธารณะส่งต่อเป็น `return_uri` ของผู้ให้บริการโดยไม่จำกัดโดเมน (open redirect หลัง 3-D Secure — ผู้โจมตีต้องถือ orderId/token เอง ⇒ ความเสี่ยงต่ำ) | จำกัดให้เป็นโดเมนของไซต์ | — |
| E2-12a | ✅ | P3 | ใบสำคัญ "ตรวจผล"/"บันทึกผลด้วยมือ" ลงวันนี้ ทั้งที่เงินออกวันพยายามคืน (คำถามเจ้าของข้อ 4) | `GatewayRefundMath.PastRefundBooking`: งวดของวันเงินออกเปิด ⇒ ลงวันนั้น · ปิด ⇒ วันนี้ + หมายเหตุวันจริงบนใบสำคัญ (ทิศมองเห็น/ย้อนได้) · ตัวตัดสินเดียวของสองเส้น | `BOOK_*` สองทิศ |
| E2-12b | ✅ | P3 | ClaimDate ในอนาคตรับได้ | `GatewayFeeVatClaim.FutureClaimDateMessage` (service เรียกก่อนลงบัญชี · หน้าเว็บ `max=วันนี้`) | `VAT_วันที่เคลมในอนาคต…` |
| E2-12c | ✅ | P3 | สาขาผู้ออกใบว่าง ⇒ `00000` เงียบ | บล็อก "กรอกตามใบ" (หน้าเว็บตั้ง 00000 ไว้ให้แล้ว) · เทสต์เดิม `RE3_รับใบกำกับ_ผ่าน…` ที่ล็อกพฤติกรรมเดิมถูกกลับทิศ | `VAT_สาขาว่าง…` สองทิศ |
| E2-12d | ✅ | P3 | กระทบยอดแถวที่บันทึกรอบแล้วใช้โหมด VAT ค่าธรรมเนียม**วันนี้** ⇒ เปลี่ยนโหมดทีหลัง = รอบเก่าไม่สมดุล | `PaymentIntent.SettledFeeDeducted` (บันทึกตอนบันทึกรอบ) + migration เติมย้อนหลัง**เฉพาะที่พิสูจน์ได้** (ส่วนต่างเท่าค่าธรรมเนียม หรือ ค่าธรรมเนียม+VAT) · ไม่ตรง = NULL = สูตรเดิม | `REC_*` สองทิศ |
| E2-12e | 📋 | P3 | สถานะคืนแล้วแต่ `RefundedAmount = 0` (แถวเก่า/E-1) ค้าง −ค่าธรรมเนียมใน "ยังไม่ถึงรอบโอน" ตลอดไป | ต้องมีคนตรวจยอดคืนรายรายการจากแดชบอร์ด — ไม่มีข้อมูลให้ migration | — |
| E2-12f | 📋 | P3 | ส่วนต่างปัดเศษรายใบกำกับค้าง 11630 ไม่มีเครื่องมือตัดจำหน่าย · เคลมลงวันก่อนใบสำคัญรอบโอนที่พัก VAT ทำ 11630 ติดลบระหว่างเดือน (ยอดรวมไม่ติดลบ) | เครื่องมือปรับปรุง 11630 + audit | — |
| E-3 | ✅ CONFIRMED (หนักกว่าที่รายงาน) | P1 | นอกจาก "ปิดบิลล้มโดยไม่มีคำเตือนล่วงหน้า" แล้ว **ข้อความล้มดังชี้ไป "ตั้งค่าเครื่อง → บัญชีธนาคาร" ซึ่งไม่มีช่องบนหน้าจอเลย** (`BankAccountId` มีแค่ใน API) ⇒ ทางไปต่อเป็นทางตัน · pinned id ไม่ถูกตรวจ tenant/active | ช่อง "บัญชีธนาคารรับเงิน" ในโมดัลตั้งค่าเครื่อง (hydrate/reset/payload ตาม checklist B) · `TerminalResponse.MoneyAccountWarning` ← `MoneyAccountFallback.TerminalBankWarning` (กติกาเดียวกับ `PickBank` ตอนปิดบิล) · ป้ายแดงข้างป้ายสาขา (คลิก = เปิดตั้งค่า) · `ValidateTerminalMoneyAccountsAsync` (แก้ไขตรวจเฉพาะค่าที่เปลี่ยน) | `E3_*` สองทิศ |
| E-3i | 📋 | P2 | ฝั่ง Integration ไม่มี "เครื่อง" ให้ปัก — ทางไปต่อคือระบบต้นทางส่ง `bankAccountName` · หน้าตั้งค่า integration ยังไม่มีคำเตือนล่วงหน้า | เพิ่มคำเตือนจาก `PickBank` ที่หน้า integration/สถานะ sync | — |
| E-4 | ✅ | P3 | onSkip **ถึงผู้ใช้จริง 3 ทาง** (หมายเหตุ `[ยังไม่ลงบัญชี]` บนเอกสาร · sync log `PartialSuccess` · ข้อความตอบคู่ค้า `JeSkipSuffix`) + แดชบอร์ด "เอกสารอนุมัติ N ใบยังไม่ลงบัญชี" · แต่ข้อความรวม "ไม่พบผังลูกหนี้/รายได้" ไม่บอกว่าผังไหน | `TradeReceivableAccount.MissingMessage` (11310 + ช่อง "ถ้าเป็นลูกหนี้ จะบันทึกบัญชี" บนผู้ติดต่อ) · แยกข้อความรายได้ | `E4_*` |
| E-4b | 📋 | P2 | resync แบบ in-place ที่สร้างบรรทัดไม่ได้ ⇒ ตกไปทาง reversal: **กลับ JE เดิมก่อน** แล้วสร้างใหม่ไม่ได้ (เหตุเดียวกัน) ⇒ เอกสารไม่มี JE (ดัง แต่ทำลาย JE ที่ถูกอยู่) · ข้อความ "สั่งลงบัญชีใหม่จากหน้าเอกสาร" — ยังไม่พบปุ่มนั้น | ตรวจสร้างบรรทัดได้ก่อนกลับ JE เดิม · ยืนยัน/สร้างทาง re-post | — |

## ความเสี่ยงคอมไพล์ที่เหลือ (ไม่มี SDK)
- `OmisePaymentProvider.IsWellFormedEventId` เป็น `internal static` (เทสต์เข้าถึงผ่าน `InternalsVisibleTo` ที่มีอยู่แล้ว) · pattern `eventId is { Length: > 5 and <= 100 }`
- `GatewayReconciliation`: `lifetime with { FeeDeducted = … }` บน `readonly record struct` (C# 10) · พารามิเตอร์ใหม่ `SettledFeeDeducted` ต่อท้ายพร้อมค่าเริ่มต้น (ผู้เรียกเดิมไม่ต้องแก้)
- `TerminalResponse` เพิ่ม `MoneyAccountWarning` ท้าย record พร้อมค่าเริ่มต้น · ผู้สร้างตัวเดียว (`MapTerminal`)
- controller `ConfirmManually` เพิ่ม `[FromServices] IEnumerable<IPaymentProvider>` · `IPaymentProvider.SettlesDirectlyToBank` เป็น default interface member (เรียกผ่านตัวแปรชนิด interface)
- เทสต์ใหม่สร้าง `OmisePaymentProvider` จริงด้วย `IHttpClientFactory` ปลอม + `NullLogger` (แบบเดียวกับ `LineSplitStudentTests`)

## คำถามค้าง (ทิศที่เลือกแล้ว = มองเห็น/ย้อนได้)
1. **G8-5 webhook token ต่อ config** — เปลี่ยน URL ที่ผู้ใช้ตั้งในแดชบอร์ดผู้ให้บริการ ⇒ ต้องรองรับ URL เดิมช่วงเปลี่ยนผ่าน (ไม่ทำในรอบนี้)
2. **ยืนยันใน sandbox** ว่าหัว `Omise-Version: 2019-05-29` ไม่เปลี่ยนช่องอื่นที่ adapter อ่าน (โดยเฉพาะ `fee` = ก่อน VAT ⇒ แนะนำโหมด `AddedOnTop` บนหน้าตั้งค่า) — ถ้าบัญชีใดเดิมใช้รุ่น 2017 อยู่ ค่าธรรมเนียมจาก charge หลัง deploy จะเป็นความหมายของรุ่นใหม่
3. E2-9 (จุดตัดคืนเงินวันเงินเข้า) — ยังรอเจ้าของเลือก override รายรายการ vs ข้อมูล transfer ของผู้ให้บริการ
4. สิทธิ์ `Bank.View` บนหน้ารายการรับชำระ — บทบาทที่กำหนดเองซึ่งเคยเปิดหน้านี้ได้ด้วยสิทธิ์เอกสาร/POS จะได้ 403 (ทางไปต่อ: เจ้าของเพิ่มสิทธิ์ "ดูบัญชีธนาคาร") · ถามสถานะของรายการตัวเองยังได้ด้วยสิทธิ์ต้นทาง

## ไฟล์ที่แตะ
`Controllers/PaymentGatewayController.cs` · `Services/Payments/{GatewaySettlementService,GatewayRefundService,PaymentIntentService}.cs` ·
`Services/Payments/Providers/OmisePaymentProvider.cs` · `Helpers/{GatewaySettlementMath,GatewayReconciliation,GatewayRefundMath,GatewayFeeVatClaim,PaymentIntentPolicy,PaymentGatewayPermissionScope,MoneyAccountFallback,TradeReceivableAccount}.cs` ·
`Models/Entities/Payments.cs` · `Models/DTOs/Pos/PosDtos.cs` · `Data/DatabaseMigrationHelper.cs` · `Services/Implementations/PosService.cs` ·
`Services/Implementations/IntegrationService.cs` (ข้อความ E-4 จุดเดียว — ไม่ใช่ไฟล์ของทีมอื่นในรอบนี้) · `wwwroot/pages/{payment-intents,payment-settlements,pos}.html` ·
`tools/required_call_site_check.py` (+15 แถว) · เทสต์ `GatewayTeamGRound200Tests` + ปรับ `GatewaySettlementReview198Tests` (กลับทิศเทสต์ที่ล็อกสาขาว่าง = 00000) ·
ไม่แตะ `Services/Settlement/*`
