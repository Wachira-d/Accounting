# ฝ่ายค้านรอบ 270 — ee79eb2b · 5b296525 · 5369183d

ขอบเขต: คำตัดสิน 270-1-1…5 · DOCUMENT_FLOW §2.12g-270 · เทสต์ N6 (FK) · SUB09 flaky
อ่านจาก `origin/claude/booking-service-features-sdemob` (FETCH_HEAD) · อ่านอย่างเดียว · ยังไม่ได้คอมไพล์/รันในเครื่องนี้ (ไม่มี .NET SDK)
ที่พบ: HIGH 1 · MED 4 · LOW 7 · ตรวจแล้วไม่ใช่บั๊ก 6

---

## HIGH

### H1 — แผนคืนมัดจำนับเงินที่เข้า "หลังออกใบมัดจำแล้ว" เป็นเงินของใบมัดจำ ⇒ Cr บัญชีหัวใบเกินยอดที่บัญชีนั้นรับจริง
- `DocumentService.BookingDepositRefund.cs:22-27` โหลด **ทุกแถว** `SiteBookingPayments` ของการจองที่ `!IsRefund && Completed` ไม่แยกว่าแถวไหนเป็นงวดมัดจำ
- `:40` แถวที่สร้างล่าสุด (`rows.Count - 1`) ถูกถือว่าเป็น "งวดสุดท้าย = ผังหัวใบ" (`headerGl`)
- แต่เส้นรับเงิน (`CmsBookingService.Payment.cs:65`) รับเงินได้ถึง **ยอดรวมทั้งการจอง** (`ValidateAmount(amount, GrandTotal, PaidAmount)`) ไม่ใช่แค่ถึงมัดจำ ·
  เมื่อใบมัดจำอนุมัติแล้ว เงินที่เข้าเพิ่มลงแค่บนการจอง (`:247` `AlreadyIssuedNote` — "บันทึกบนการจองอย่างเดียว") **ไม่มี JE ลงใบมัดจำ**
- ตัวย้ายเงินรอบ 268 (`MoveEarlierInstalmentsCoreAsync`) ใช้ `finalPaymentId` (งวดที่ทำให้มัดจำครบ) แต่ตัววางแผนคืนเงินรอบ 270 ใช้ "แถวที่สร้างล่าสุด" — สองฝั่งไม่ได้นิยาม "งวดสุดท้าย" เหมือนกัน
- `PlanRefund` (`BookingDepositInstalmentChannel.cs:94`) วนจากงวดล่าสุดก่อน ⇒ ไปหยิบจากแถวนี้ก่อน
- **เกิดอย่างไร**: มัดจำ 1,000 = งวด 1 500 เข้าธนาคาร B (มี JE `BKDEP-` ย้ายไป B แล้ว) + งวด 2 500 เข้าธนาคาร A (ผังหัวใบ) ⇒ ใบมัดจำอนุมัติ ·
  ลูกค้าโอนค่าบริการที่เหลืออีก 3,000 เข้าธนาคาร C (ลงบนการจองอย่างเดียว) ⇒ ยกเลิก ⇒ คืนมัดจำเต็ม 1,000 โดยไม่เลือกบัญชี ⇒ `pools = [B 500, A 500, A(ผิด) 3,000]`
  ⇒ ได้ขาเดียว **Cr A 1,000** ทั้งที่ A รับจากใบนี้แค่ 500 · ยอด B 500 ค้างอยู่ในบัญชี (สมุดธนาคาร A ติดลบ 500 เทียบกับรายการเดินบัญชี · B เกิน 500) —
  เป็น defect class เดียวกับ 268 L7 ที่รอบนี้ตั้งใจปิด และไม่มี 409 ให้เห็น (ระบบ "รู้" บัญชีทุกส่วน)
- อีกทาง: งวดที่ทำให้มัดจำครบ (index n−2) ถ้าผูกผังต่างจากหัวใบ (ช่องทางของใบตั้งตามงวดนี้ ⇒ ปกติเท่ากัน แต่ถ้าพนักงานแก้ผังหัวใบก่อนอนุมัติจะไม่เท่า) และไม่มี `BKDEP-` ⇒ ได้ `null` ⇒ 409 ทั้งที่ไม่ควร
- **แก้**: ให้ pools จบที่งวดที่ทำให้มัดจำครบ (ใช้กติกาเดียวกับ `BookingDepositInstalmentChannel.For` / `finalPaymentId` · ทางที่ดีคือแยกเป็น helper `DepositInstalments(rows, depositAmount)` ที่สองฝั่งเรียกร่วมกัน) ·
  หรือตัดยอดสะสมของ pools ให้ไม่เกิน `doc.TotalAmount` (ตัดจากงวดที่สร้างหลังสุด) ·
  เทสต์ทิศตรงข้าม: "เงินเข้าเพิ่มหลังออกใบมัดจำ ⇒ ขาคืนไม่เกินยอดที่แต่ละบัญชีรับจากใบนี้" (ยังไม่มีใน `Round270DbTests.cs:110/138`)

---

## MED

### M1 — คิว `linechat.slip-linked` ทิ้งผล Retry ทิ้งตอนส่งจริง ⇒ ข้อความที่เลื่อนพ้นชั่วโมงเงียบหายเพราะ 5xx/429/timeout ครั้งเดียว
- `LineChatSlipService.cs:702` `var (notice, _) = await NotifyChatLinkedAsync(...)` — ทิ้ง `Retryable` · `:713` คืน `notice` (ไม่ใช่ null) ⇒
  `DeferredNotifyDispatchJob.cs:~128` เห็นเป็น `skipNote` ⇒ ประทับ `DoneAt` + Note **ไม่มี backoff** (`AfterFailure` ทำงานเฉพาะตอน `error != null`)
- เส้นที่เลื่อนพ้นชั่วโมงเงียบ (`NoticeAfterAssignAsync:650-652`) ไม่ได้ส่งก่อนเข้าคิว ⇒ ครั้งที่ dispatch คือ **ครั้งแรกจริง** ⇒ LINE ตอบ 500/429 ครั้งเดียวก็เลิก
- รอบ 270 ทำให้ `Classify` แยก Retry/Failed ได้ถูกแล้ว (270-1-3) แต่เส้นคิวไม่ได้ใช้ผลนั้นเลย ⇒ "ลองซ้ำเฉพาะ Retry" ตามที่ DOCUMENT_FLOW §2.12g-270 เขียนไว้ เป็นจริงแค่ในเส้นส่งทันที
- **เกิดอย่างไร**: สลิปเข้า 23:10 ⇒ ผูกตอน 23:30 (ชั่วโมงเงียบ) ⇒ คิว 08:00 ⇒ LINE 503 ⇒ ปิดแถวพร้อม "แจ้งไม่สำเร็จ" ⇒ ลูกค้าไม่เคยได้ข้อความ ·
  พนักงานเห็นได้แค่ใน Note ของคิว (ไม่ใช่ "ล้มดัง" ตาม F2 ข้อ 7)
- **แก้**: ให้ `NotifyChatLinkedAsync` บอก verdict ออกมาให้ `DispatchLinkedNoticeAsync` · ถ้า Retry ให้ `throw` (หรือคืนค่าที่ job ตีความว่าเป็น `error`) ⇒ ได้ backoff ผ่าน `DeferredNotifyPolicy.AfterFailure` ·
  เก็บกติกา "ส่งซ้ำครั้งเดียว" ไว้เฉพาะแถวที่เข้าคิวมาจาก `FailedWillRetry` (แยกด้วย `Attempts`)

### M2 — `[VAT-ZERO-UNREAD]` หลุดเพราะเครื่องหมายอัตราศูนย์ไปจับชื่อบริษัท/ที่อยู่
- `OcrZeroVatTaxInvoiceGuard.cs:31` `ส่งออก|export|…` จับได้ทุกที่บนกระดาษ ไม่ใช่เฉพาะบรรทัด VAT/อัตรา ·
  ชื่อผู้ขายแบบ "… Import-Export Co., Ltd." / "… อิมพอร์ต เอ็กซ์ปอร์ต" และที่อยู่ "เขตอุตสาหกรรมส่งออก" / "นิคมฯ เพื่อการส่งออก" พบบ่อย
- **เกิดอย่างไร**: ผู้ขายชื่อ "สยาม อิมพอร์ต-เอ็กซ์ปอร์ต จำกัด / SIAM IMPORT EXPORT CO.,LTD." ออกใบกำกับ 7% ⇒ engine อ่านแถว VAT ไม่ได้ ⇒ บรรทัด 0% ⇒
  `HasZeroRatedMarker` = true (คำว่า "EXPORT" ในหัวใบ) ⇒ ไม่ติดแท็ก ⇒ อนุมัติเองได้ ⇒ ถ้าเป็นฝั่งขาย ใบนี้เข้า **ช่อง 2 ภ.พ.30** ผ่าน
  `OutputVatOwnership.SalesBoxOwner:159` (`Type == TaxInvoice` ⇒ นับ 0% ทุกกรณี) · ถ้าเป็นฝั่งซื้อ ภาษีซื้อหายทั้งก้อน
- **แก้**: ให้ "ส่งออก/export" นับเฉพาะเมื่ออยู่บรรทัด/หน้าต่างเดียวกับคำว่า VAT/ภาษี/อัตรา/0% (หรือ "ใบขนสินค้าขาออก" / "export entry") · ตัด "import-export / อิมพอร์ต" ทิ้ง ·
  เทสต์จากกระดาษจริงตาม §H: "ชื่อผู้ขายมี EXPORT + VAT 7% อ่านไม่ได้ ⇒ ยังติดแท็ก" (ทิศตรงข้ามกับใบส่งออกจริงที่มีอยู่แล้ว)

### M3 — ใบที่อนุมัติไปแล้วก่อนรอบ 270 ไม่มีตัวตรวจย้อนหลัง (F2 ข้อ 9)
- ข้อ 10 ในข้อความคอมมิตตั้งใจไม่แตะเอกสาร OCR ที่อนุมัติแล้ว (ถูกต้อง — ห้ามแก้รายงานที่ยื่นแล้วเงียบ ๆ) แต่ก็ไม่มี**รายงาน/คำเตือน**
  ⇒ ใบกำกับ OCR ทั้งใบ 0% ที่ลงช่อง 2 ไปแล้วไม่มีใครเห็น
- **เกิดอย่างไร**: เดือนที่ยังไม่ยื่น ภ.พ.30 มีใบกำกับ OCR ที่อ่าน VAT ไม่ได้ ซึ่งอนุมัติเองก่อน deploy ⇒ ยอดในช่อง 2 สูงเกินจริง ⇒ ยื่นแบบโดยไม่มีคำเตือน
- **แก้**: เพิ่มบรรทัดเตือน (อ่านอย่างเดียว) ในตัวสรุปช่องขาย / Autopilot สำหรับใบกำกับที่ `OcrScanId != null` และ VAT 0 ทั้งใบ และ `Check(...)` ของกระดาษเดิมคืนแท็ก — ใช้ตัวตัดสินเดียวกัน

### M4 — SUB09 flaky: ด่าน idempotency ปล่อยให้สองคำขอทำงานจริงทั้งคู่ (ไม่ใช่บั๊กของเทสต์อย่างเดียว)
- หลักฐาน CI run 43487318055 (job 132099077202): `BusinessRuleException: คุณจองรอบนี้ไว้แล้วเมื่อสักครู่` จาก `CmsBookingService.DuplicateBookingResultAsync:1216`
  ← `CreateBookingAsync` ← lambda `SubmitOnceRound217DbTests.cs:318` ← `IdempotencyMiddleware` · เทสต์ใช้ 916 ms ≈ hold 800 ms ครั้งเดียว
  ⇒ **ทั้งสองคำขอผ่านมิดเดิลแวร์เข้า `_next`** พร้อมกัน แล้วถูกเรียงลำดับด้วยล็อกไซต์ ⇒ ตัวที่สองชน `DuplicateSubmitGuard` (ชั้นที่สอง) แล้วโยนออกมา (เทสต์ไม่มี ExceptionMiddleware)
- ไล่ดูทุกทางที่ไปถึง `_next` ในคำขอสองตัวที่ห่างกันไม่ถึง 1 วินาที:
  - `Reclaim`: เป็นไปไม่ได้ — ต้องมีอายุ > 120 วินาที (`IdempotencyKeyPolicy.cs:170`)
  - "แถวถูกลบแล้วแทรกใหม่": เป็นไปไม่ได้ — ตัวแรกสำเร็จ (`ShouldStore` เป็นจริงกับ `{success:true}`) และไม่มีเทสต์อื่นแตะ `IdempotencyRecords`
    (`ChatRetentionPurgeJob` ลบเฉพาะแถวอายุ > 24 ชม.)
  - `inserted==1` สองครั้ง: ต้องไม่มี unique index — เป็นไปไม่ได้ เพราะ `EnsureIdempotencyTableAsync` รันก่อน และมีคลาสนี้คลาสเดียวที่แตะตาราง
  - **ที่เหลือคือ fail-open** — `IdempotencyMiddleware.cs:94-97` INSERT โยน error ชั่วคราวอะไรก็ได้ (connection pool เต็ม / 53300 too many clients /
    timeout ใต้ Db test หลายคลาสขนานบน `max_connections=100` — แต่ละ `PostAsync` เปิด 2 context) ⇒ `LogWarning` แล้ว `await _next` ⇒ คำขอที่สองทำงานจริง
  - หมายเหตุ: stack ชี้ `IdempotencyMiddleware.cs:64` (ทางผ่านเมื่อ `key==null` หรือเป็น credential path) ซึ่ง input ของเทสต์ไม่มีทางเข้าได้ ⇒
    เลขบรรทัดของ async frame ใน Release ไม่แม่น · ใช้ log ของ PostgreSQL ช่วงนาทีนั้นยืนยันไม่ได้ (หน้าต่าง tail ไม่ครอบ 18:58:45)
- **ผลต่อ production**: ระหว่างฐานรับไม่ไหว การกดจองซ้ำของแขกได้ 409 "จองไว้แล้ว" จากชั้นที่สอง แทนที่จะได้ผลเดิม (replay) — ปลอดภัยเชิงข้อมูลเพราะได้ใบเดียว
  แต่เส้นที่ไม่มีด่านซ้ำเชิงความหมาย (ทุก POST อื่นที่ใช้ Idempotency-Key) **บันทึกซ้ำได้จริง** ⇒ fail-open ควรดังและแคบลง
- **แพตช์ที่เสนอ**:
  ```csharp
  // IdempotencyMiddleware — ลองใหม่หนึ่งครั้งก่อน fail-open + บอกผู้เรียก (ไม่เงียบ)
  catch (Exception ex) when (IsTransient(ex) && !retried) { retried = true; await Task.Delay(50); goto insert; }
  catch (Exception ex)
  {
      _logger.LogError(ex, "Idempotency store ใช้ไม่ได้ — ปล่อยผ่าน key={Key}", key);   // Warning → Error
      ctx.Response.Headers[IdempotencyKeyPolicy.StatusHeader] = "unavailable";
      await _next(ctx); return;
  }
  ```
  ฝั่งเทสต์: ส่ง `ListLogger` เข้า `PostAsync` แทน `NullLogger` แล้วให้เทสต์ล้มพร้อมข้อความ "fail-open: <exception>" เมื่อมี warning (รู้สาเหตุจริงในรอบหน้า) ·
  ห่อ `_next` ของเทสต์ด้วย try/catch `BusinessRuleException { StatusCode: 409 }` ⇒ เขียน 409 ลง response แบบ ExceptionMiddleware (production ทำแบบนี้อยู่แล้ว) —
  Assert ยังจับได้ว่าเกินหนึ่งใบ · ห้ามแก้แค่ "ลองรันใหม่"

---

## LOW

- **L1** `BookingDepositRefund.cs:29/44`: `ResolvePostingAccountAsync` โยน `BusinessRuleException` (400 `DocumentMoneyChannel.RuleCode` ไม่ใช่ 409) เมื่อบัญชีธนาคารของหัวใบ/งวดก่อนถูกปิดหรือถอดผังหลังรับเงิน ⇒
  คืนเงินไม่ได้แม้ยอดที่คืนอยู่ในบัญชีที่ยังใช้ได้ · หน้าเว็บไม่ focus ช่อง "คืนจากบัญชี" (เช็คแค่ `RefundChoiceCode`) · ผู้ใช้ยังเลือกบัญชีเองได้ (ข้อความ hint บอกไว้) ⇒ LOW ·
  แก้: จับไว้แล้วถือเป็น `AccountId = null` (ไม่รู้ ⇒ 409 แบบเดียวกัน)
- **L2** `deposits.html:~373` / `deposit-center.html:~589` `moneyAccountId: …dataset.gl || null` — บัญชีธนาคารที่ไม่มีผัง GL ไม่มี `data-gl` ⇒ ส่ง null ⇒ เซิร์ฟเวอร์ใช้แผนเดิม ⇒ ได้ 409 ซ้ำ ·
  ผู้ใช้เลือกแล้วไม่มีผล (ห้าม silent no-op) — ควร disable option หรือบอกเหตุ
- **L3** `BookingDepositRefund.cs:49-57` + `PlanRefund:108`: JE คืนมัดจำที่ถูก reverse ภายหลังถูกกรองออกจาก `refunded` แต่ `doc.DepositRefundedAmount` ไม่ลด ⇒
  `unattributed` ไปหักจาก pool ⇒ อาจได้ 409 ทั้งที่รู้บัญชี (ฝั่งปลอดภัย ไม่ลงผิด)
- **L4** `LineChatSlipService.cs:697-699`: ด่าน stale ไม่ตรวจสถานะเอกสาร — ใบที่ถูก void/ลบหลังเข้าคิวยังได้ "ผูกสลิปกับ {เลขใบ} แล้ว" · เพิ่ม `!IsDeleted && Status != Voided`
  (ป้อนเข้า `LinkedNoticeStaleReason` เป็นข้อเท็จจริงอีกตัว)
- **L5** `LinkedNoticeStaleReason` กรณีกุญแจไม่ตรง: `AssignAsync:575` รับเฉพาะแถวที่ `InQueue` ⇒ สลิปที่ Assigned แล้วผูกใหม่ไม่ได้ในวันนี้ ⇒ ด่านนี้เป็น defense-in-depth ·
  ถ้าเปิด "ผูกใหม่" ในอนาคต `QueueLinkedNoticeAsync:662` จะไม่เข้าคิวรอบสอง (unique ที่ Kind+EntityId) และ retry key = `row.Id` ⇒ LINE ตอบ 409 ⇒ `Classify` ประทับ Sent ทั้งที่ข้อความใหม่ไม่ได้ส่ง ⇒
  ต้องเปลี่ยน retry key ให้ผูกกับ `LinkKey` ก่อนเปิดฟีเจอร์นั้น
- **L6** `AutopilotService.cs:231-232`: `NewValues.Contains(...)` = `LIKE '%…%'` สแกน AuditLogs ของบริษัททุกครั้งที่โหลด Autopilot + `Take(200)` ก่อนกรองสถานะ ⇒
  บริษัทที่ใช้ตัวช่วยใบซ้ำ > 200 ครั้ง ใบร่างเก่าหลุดจากคิวเงียบ ๆ · ควรกรอง `Action`/`EntityType` + ดัชนี หรือกลับไปเริ่มจาก Documents (CreditNote ร่าง + `RelatedDocumentId`) แล้วค่อย join audit
- **L7** `DuplicateSaleService.AuditAsync:174-201` เป็น best-effort (กลืน error แล้วคืน note) ⇒ ถ้าเขียน audit ไม่สำเร็จ ใบร่างจะไม่เคยขึ้นคิว 270-1-4 · และหัวเรื่องจะโชว์ `DRAFT-{guid}`
  (ร่างยังไม่มีเลขตาม §86/4) — ใช้ "ใบลดหนี้ร่าง (อ้าง {Invoice})" เมื่อเลขยังเป็น DRAFT

---

## ตรวจแล้วไม่ใช่บั๊ก (ไม่ต้องรายงานซ้ำ)

1. **JE คืนมัดจำบาลานซ์** — Dr ฐาน + Dr VAT = `request.Amount` (`DepositReversalMath.RefundSplit`) · Cr = Σ legs ซึ่งถูกบังคับให้ = `request.Amount`
   (`DocumentService.cs:5951`) · VAT > 0 แต่ไม่มีผัง ⇒ throw ก่อนเขียน · บาลานซ์ทุกเส้น
2. **409 ไม่ลงอะไร** — `throw` ที่ `:5945-5947` อยู่ก่อน `GetNextJournalEntryNumberAsync` / `JournalEntries.Add` / `SaveChanges` และอยู่ใน `refundTx` ที่ยังไม่ commit ⇒ dispose = rollback ·
   middleware ส่ง `data.ruleCode` (`ExceptionMiddleware.cs:214-217`) ⇒ หน้าเว็บอ่าน `e.body.data.ruleCode` ได้
3. **ทางเข้าคืนมัดจำ** — ผู้เรียก `RefundDepositAsync` มี 3 จุด: `DocumentController.cs:850` (deposits/deposit-center) + ที่พัก `LodgingService.Lifecycle.cs:2126/2394`
   (ส่ง `MoneyAccountId` ⇒ ขาเดียวตามเดิม · ใบที่พักไม่ผูก `SiteBooking`) · CMS ไม่ได้คืนเองแต่ส่งไปหน้า "เงินมัดจำ" (`CmsBookingService.Payment.cs:510`) ·
   `FinancialManagementService.RefundDepositAsync` เป็นระบบเงินมัดจำคนละตาราง (`DepositTransaction`) ไม่ใช่คลาสนี้
4. **ลำดับ "งวดล่าสุดก่อน" ตอนคืนบางส่วน** — คงที่และไม่ขึ้นกับลำดับการเรียก · คืนครั้งที่สองหักยอดที่คืนไปแล้วของผังเดิมก่อน (`PlanRefund:97-106`) ⇒ ไม่คืนจากบัญชีเดียวเกินยอดของ pool
   (ยกเว้นกรณีที่ pool ผิดตั้งแต่ต้น — H1)
5. **`LinePushOutcome.Classify`** — 429 ที่มีคำว่า "monthly limit" ⇒ Failed (ถูก — โควตาเดือนรอไม่ได้) · 429 อื่น / 5xx / `TransportError`
   (`PushCoreAsync` catch-all ⇒ `TransportError:true` รวม timeout) ⇒ Retry · ไม่มี token/ปลายทางว่าง ⇒ status null ⇒ Failed ถาวร (ถูก) · 409 ⇒ Sent เพราะ retry key = `row.Id`
   ไม่มีการจัดผิดที่ทำให้ข้อความหาย **ในตัวจำแนก** — ที่หายอยู่ที่คิวทิ้งผล (M1)
6. **ตัด FK ของตารางทดลอง (5b296525)** — deadlock ใน log คือ `DROP TABLE r245_cols_*` (AccessExclusive บน Contacts/Companies เพื่อถอด FK trigger) ชนกับ `INSERT INTO Contacts`
   ของเทสต์อื่น · production: `ContactNotifyEndpointMigration` เป็น `CREATE TABLE IF NOT EXISTS` (no-op หลังบูตครั้งแรก · ไม่ DROP) และรันใต้ `pg_advisory_xact_lock`
   (`DatabaseMigrationHelper.cs:71-82`) ⇒ ไม่ได้บังบั๊ก deadlock ของโค้ดหลัก · เทสต์ยังตรวจรายชื่อคอลัมน์ได้ตามเดิม (FK ตรวจที่ฐานจากโมเดล) — ยอมรับได้
   (`Assert.DoesNotContain("REFERENCES")` จะล้มดังถ้า regex ไม่จับ FK รูปใหม่ ⇒ ไม่เงียบ)
7. **OCR ทางเข้า** — แท็กเขียนใน `BuildScanLinesAsync` (ผู้เรียก 3 จุด: `OcrService.cs:7382/8575/9144` — สร้าง/พรีวิว/ดึงซ้ำ) แล้วถูกหยุดผ่าน `OcrPostingReadiness.Evaluate*`
   ซึ่งผู้อนุมัติเองทุกเส้นใช้ (`OcrController.cs:466` อัปโหลด/มือถือ · `LineBotService.cs:697/874` LINE · `OcrService.cs:10756/10761` คิว/อีเมล) ⇒ ครบทุกทางเข้า ·
   ใบส่งออกที่พิมพ์ "VAT 0%" / "ส่งออก" / "ม.80/1" ผ่าน · บรรทัดยกเว้น (−1) ไม่ติด

---

## สรุป
HIGH 1 (แผนคืนมัดจำรวมเงินที่เข้าหลังออกใบ ⇒ Cr บัญชีหัวใบเกินจริง) ต้องแก้ก่อนปิดรอบ · M1 (คิวทิ้งผล Retry) กับ M2 (regex ส่งออกกว้างเกิน ⇒ ใบ 7% เข้าช่อง 2) ควรแก้ในรอบเดียวกัน ·
SUB09 = fail-open ของ `IdempotencyMiddleware` ใต้ฐานรับไม่ไหว (ไม่ใช่ race ของรอบ 270) — เสนอแพตช์ให้ลองใหม่ + ล้มดังทั้งโค้ดหลักและเทสต์

---

## การตอบของทีม (ชุดแก้ · คำตัดสิน 270-2-1…7)

| ข้อ | สถานะ | อยู่ที่ |
| --- | --- | --- |
| H1 แผนคืนนับเงินที่เข้าหลังออกใบ | ✅ แก้ — กติกาเดียว `DepositRows` (ตัวย้ายเงิน + ตัววางแผนคืน) + `CapToDeposit` · hook หลังอนุมัติก็ไม่ใช้แถวล่าสุดแล้ว | Db `C270_2_1_มัดจำ500Bบวก500A_*` · pure `H1_270_*` |
| M1 คิวทิ้งผล Retry | ✅ แก้ — `DeferredNotifyRetryException` ⇒ backoff ของคิวเดิม · ปิดเฉพาะส่งแล้ว/ถาวร | Db `C270_2_2_*` · pure `M1_270_*` |
| M2 regex ส่งออกกว้าง | ✅ แก้ — อ่านทีละบรรทัด รับเฉพาะเครื่องหมายชัด · ตัด import-export/เขตส่งออก | pure `M2_270_*` |
| M3 ใบที่อนุมัติก่อน 270 | ✅ แก้ — `GET tax/ocr-zero-vat-review` + แถบหน้า ภาษี (อ่านอย่างเดียว · ทางแก้จากเซิร์ฟเวอร์) | Db `C270_2_5_*` |
| M4 SUB09 / fail-open | ✅ แก้ — ลองซ้ำ · LogError · header `unavailable` · เส้นเงิน/เอกสาร 503 · เทสต์เก็บ log | `IdempotencyStoreUnavailableTests` · Db `SUB09_*` |
| L1 บัญชีถอดผัง ⇒ 400 | ✅ แก้ — ไม่รู้ ⇒ 409 ให้เลือก | Db `C270_2_1_ทิศตรงข้าม_*` |
| L2 ตัวเลือกไม่มี data-gl | ⏸ ตรวจแล้วไม่ใช่บั๊ก — บัญชีที่ไม่มีผังเป็น `<option value="" disabled>` ใน `MoneyChannel.fill` (เลือกไม่ได้) ⇒ ตัวเลือกที่เลือกได้มี `data-gl` ทุกตัว | 270-2-7 |
| L3 JE คืนที่ถูกกลับรายการ | ✅ แก้ — ยอดคืนแล้วจากขาที่ยังมีผล | `BookingDepositRefundPlanAsync` |
| L4 ใบยกเลิกหลังเข้าคิว | ✅ แก้ — `documentLive` | pure `L5_270_*` |
| L5 retry key / แถวคิวเดิม | ✅ แก้ — retry key ผูกกับ LinkKey · แถวคิวเดิมใช้กับการผูกใหม่ | pure `L5_270_*` · Db `C268_1_4_*` |
| L6 Take(200) ก่อนกรอง | ✅ แก้ — เริ่มจากใบลดหนี้ที่ค้าง · audit ตามช่วงเวลา | Db `C270_1_4_*` |
| L7 audit best-effort · DRAFT- ในหัวเรื่อง | ✅ แก้ — ทางสำรองจากหมายเหตุ · หัวเรื่อง "อ้าง {ใบแจ้งหนี้}" | Db `C270_1_4_*` · pure `L7_270_*` |
