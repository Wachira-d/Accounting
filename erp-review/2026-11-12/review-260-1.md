# ฝ่ายค้าน รอบ 260-1 — สลิปจากแชต LINE OA ⇒ ผูกเอกสาร

ขอบเขต: `91d7fdde` + `32c5a818` (แก้ผล CI db-test) บน `claude/booking-service-features-sdemob` · อ่านโค้ดอย่างเดียว · ยังไม่ได้คอมไพล์/รันในเครื่องนี้

สรุป: **0 HIGH · 5 MED · 9 LOW** — ตัดสินเงินถูกทาง (ไม่ประทับรับเงิน · ทุกทางเข้าเดิน `SlipEvidenceService.SubmitAsync` · ทุก query กรองบริษัท · HMAC + ตรวจแหล่งแชต + CAS ถูก) ·
ช่องโหว่หลักอยู่ที่ "สลิปหายเงียบ" (ซ้ำเท็จ · แถวค้าง Submitting) และ PDPA retention

---

## MED

### M1 — "สลิปซ้ำ" ด้วยเลขอ้างอิงจาก OCR ⇒ ตอบลูกค้าว่าส่งแล้ว แล้วทิ้งเงียบ (ไม่มีแถว · ไม่มีคิวพนักงาน)
- `Services/Implementations/LineChatSlipService.cs:113-114` + `Helpers/OcrSlipText.cs:73-79` (`ExtractRef` = ชุด `[A-Z0-9]{10,25}` ที่ยาวสุด)
- สถานการณ์: สลิปจ่ายบิล (Bill Payment) พิมพ์ Biller ID 15 หลัก/Ref1 ของลูกค้าเหมือนกันทุกใบ · Tesseract แทรกช่องว่างในเลขรายการจริง
  ⇒ ชุดยาวสุดคือ Biller ID/Ref1 ที่คงที่ ⇒ สลิปใบแรกเก็บ `OcrRef`/`SlipBankRef` = ค่าคงที่ ⇒ **สลิปทุกใบถัดไปของลูกค้า/บริษัทนั้นได้ "สลิปนี้ส่งเข้ามาแล้ว ไม่ต้องส่งซ้ำ"**
  และไม่มีอะไรเกิดขึ้นเลย (เงินที่ลูกค้าบอกว่าโอนแล้วหายจากระบบ — ตรงกับปัญหาที่รอบนี้ตั้งใจแก้) · ทางไปต่อของลูกค้า = ไม่มี (ถูกบอกว่าไม่ต้องส่ง)
- ด่านเท็จนี้กระทบพอร์ทัล/หน้าชำระด้วย เพราะ ref จาก OCR ถูกเขียนลง `PaymentIntent.SlipBankRef` (ผ่าน `SubmitAsync`) แล้วถูกใช้ใน `DuplicateProblem` ของทุกช่องทาง
- แก้น้อยสุด: ซ้ำด้วย **แฮชไฟล์** = ตอบซ้ำได้ตามเดิม · ซ้ำด้วย **ref อย่างเดียว** ⇒ ห้ามทิ้ง: บันทึกแถว `Unmatched` เหตุใหม่ `DuplicateRef` (พนักงานเห็นรูปเทียบกับรายการเดิม)
  หรืออย่างน้อยนับเป็นซ้ำเฉพาะเมื่อยอดเท่ากันด้วย · เทสต์ทิศตรงข้าม: สองสลิป ref เท่ากันยอดต่างกัน ⇒ ใบที่สองยังเข้าคิว

### M2 — แถวค้างสถานะ `Submitting` ตลอดกาล เมื่อประตูแจ้งโอนล้มด้วย exception ชนิดอื่น ⇒ หายจากคิว + บล็อกการส่งใหม่
- `LineChatSlipService.cs:228` (`catch … when BusinessRuleException or KeyNotFoundException or ArgumentException` เท่านั้น) · จุดจอง `:290` (postback) · `:373` (พนักงานผูก) · `:292` `ReadAllBytesAsync` อยู่หลังจอง
- สถานการณ์: ลูกค้ากดเลือกใบ ⇒ `ClaimAsync` ตั้ง `Submitting` ในฐาน ⇒ S3/ดิสก์ล้ม (`IOException`/`HttpRequestException`) หรือ `DbUpdateException` ใน `SubmitAsync`
  ⇒ exception หลุดถึง webhook (จับแล้ว log · ตอบ 200 · ลูกค้าไม่ได้คำตอบ) ⇒ แถวอยู่ `Submitting`: ไม่อยู่ในคิว (`ListOpenAsync`/Autopilot อ่านแค่ `Unmatched`) ·
  ผูก/ปิดไม่ได้ (`Assignable` ไม่มี `Submitting`) · แต่ยังอยู่ใน `Statuses.Live` ⇒ ส่งรูปเดิมซ้ำได้ "ส่งเข้ามาแล้ว" — **ล้มเงียบ (F2 ข้อ 7)**
  · `AssignAsync` เคสเดียวกัน: พนักงานได้ 500 และแถวหายจากคิว (การคืนสถานะที่ `:382` วิ่งเฉพาะเมื่อ `ok == false` ไม่ใช่เมื่อ throw)
- เส้น One ใน `HandleImageAsync:133-140`: exception หลัง `WriteAsync:119` ⇒ แถวไม่ถูกบันทึกเลย · ไฟล์กำพร้าใน `line-chat-slips` · ไม่มีคิวพนักงาน
- แก้น้อยสุด: `try/finally` รอบส่วนหลัง Claim — ไม่สำเร็จด้วยเหตุใดก็ตาม ⇒ `ToStaffAsync(SubmitFailed, "ระบบขัดข้อง")` (postback/รูป) หรือคืนสถานะเดิม (Assign) แล้ว rethrow ·
  สำรอง: คิว/Autopilot แสดง `Submitting` ที่ `UpdatedAt` เก่ากว่า N นาที และให้ `Assignable` รวมแถวนั้น

### M3 — รูปที่ไม่ใช่สลิปแต่มียอดเดียว (ภาพหน้าชำระ/QR พร้อมเพย์ที่ฝังยอด) ⇒ "ได้รับสลิปแล้ว" + สร้างรายการรอยืนยันอัตโนมัติ
- `Helpers/OcrSlipText.cs:69` (กฎ (ข): ทั้งภาพมียอดไม่ใช่ศูนย์ยอดเดียว ⇒ "มั่นใจ") + `Helpers/LineChatSlipRule.cs:122` (มั่นใจ + ตรงใบเดียว ⇒ `Outcome.One`)
- สถานการณ์: ลูกค้าแคปหน้าจอ `pay-document.html`/QR ที่ร้านส่งให้ (แสดง "ยอดที่ต้องชำระ 1,070.00") ส่งกลับมาถามในแชต ⇒ ยอดเดียว = Net ของใบ ⇒ `SubmitAsync` สร้าง
  PaymentIntent รอยืนยันทั้งที่ยังไม่โอน + ตอบ "ได้รับสลิปแล้ว รอตรวจเงินเข้า" (ลูกค้าเข้าใจว่าจ่ายแล้ว) · ผลต่อ: `PendingProblem` บล็อกสลิปจริงที่ตามมาทุกช่องทาง
  (pendingSum + ยอด > ยอดค้าง ⇒ สลิปจริงไป `SubmitFailed`) จนพนักงานปฏิเสธรายการปลอม
- แก้น้อยสุด: `Outcome.One` ต้องมี "สัญญาณว่าเป็นสลิป" เพิ่ม (มีเลขอ้างอิง `Ref != null` หรือคำว่า สำเร็จ/successful/รหัสอ้างอิง) — ไม่มี ⇒ `Many` (ลูกค้ากดยืนยัน 1 ปุ่ม ตามคำตัดสิน 260-1-2 "ยอดไม่มั่นใจ ⇒ ให้ลูกค้ากดยืนยัน")
  · เทสต์ทิศตรงข้าม: ข้อความ "ยอดที่ต้องชำระ 1,070.00" ล้วน ⇒ ไม่ใช่ One

### M4 — PDPA: รูปจากแชต (รวมรูปส่วนตัวของคนแปลกหน้า) เก็บถาวร ไม่มี retention/purge · เก็บสองสำเนา
- `LineChatSlipService.cs:118-119` (เขียนทุกรูปก่อนตัดสิน) · `Helpers/Round260LineSlipMigration.cs` (ไม่มี `RetainUntil`) · ไม่มีผู้ลบไฟล์/แถว (`grep LineChatSlips` = service + Autopilot อ่านอย่างเดียว) ·
  `DismissAsync:396-417` ปิดแถวแต่ไม่ลบไฟล์
- สถานการณ์: ผู้ใช้ LINE ที่ไม่ได้ผูก (1:1 ทุกคน — คำตัดสิน 260-1-5) ส่งรูปบุคคล/บัตรประชาชน ⇒ เก็บถาวรใน storage ของบริษัท · พนักงาน "ปิดรายการ (ไม่ใช่สลิป)" แล้วรูปยังอยู่ ·
  สลิปที่ผูกสำเร็จมีสำเนาที่สองใน `uploads/document-slips` (ไฟล์แนบเอกสาร) แต่สำเนาใน `line-chat-slips` ไม่มีเหตุผลทางกฎหมายต้องเก็บ · การเปิดดูไฟล์ (`LineChatSlipController.SlipFile`) ไม่ลง `PiiAccessLog`
- แก้น้อยสุด: Dismissed/Submitted/Assigned ⇒ ลบไฟล์ใน `line-chat-slips` (Submitted/Assigned มีสำเนาที่ไฟล์แนบเอกสารแล้ว) · แถว Unmatched/Dismissed ที่ไม่ผูกเอกสารมีอายุ (เช่น 90 วัน) + job purge ผ่าน `JobLock` ·
  ลง RoPA (`ProcessingActivity`) สำหรับ "รูปจากแชต LINE"

### M5 — คิวพนักงาน: พิมพ์เลขที่เอกสารเองแต่ไม่ได้เปลี่ยน select ⇒ ผูกกับใบแรกในรายการเงียบ ๆ
- `wwwroot/js/line-chat-slips-queue.js:47` (ตัวเลือก "พิมพ์เลขที่เอกสารเอง…" อยู่**ท้าย** ⇒ ค่าเริ่มต้นของ select = ใบแรก) + `:76` (`documentId` ชนะ `documentNumber` เสมอ)
- สถานการณ์: แถวมีใบที่เสนอ (INV-001) พนักงานพิมพ์ `INV-007` ในช่องเลขที่ แล้วกด "ผูกเอกสาร" ⇒ ส่ง `documentId` ของ INV-001 · ช่องที่พิมพ์ถูกทิ้ง ⇒ รายการรอยืนยันผิดใบ
  (กฎเหล็ก #4 A "ห้าม silent no-op") · `:76` ด้วย: ยอดพิมพ์ผิดรูป ("1.500,00", "฿500") ⇒ `Number()` = NaN ⇒ JSON `null` ⇒ เซิร์ฟเวอร์ใช้ยอด OCR/ยอดสุทธิแทนเงียบ ๆ
- แก้น้อยสุด: ใส่ตัวเลือกว่าง "— เลือกเอกสาร —" เป็นค่าแรก หรือ: ช่องเลขที่มีค่า ⇒ ใช้เลขที่ (หรือ error ถ้าขัดกับ select) · ยอดที่ `isNaN` ⇒ toast error ไม่ส่ง

---

## LOW

- **L1 — ไม่มีกันซ้ำระดับเหตุการณ์ (webhookEventId / LineMessageId)** — `LineChatSlipService.cs:111` กันซ้ำด้วยแฮชเฉพาะแถวที่บันทึกแล้ว · `Round260LineSlipMigration.cs:44-45` ไม่มี index บน `LineMessageId`.
  webhook ทำ download + Tesseract แบบ synchronous ⇒ LINE timeout แล้วส่งซ้ำ (`deliveryContext.isRedelivery`) ระหว่างรอบแรกยังอ่านอยู่ ⇒ ผ่านด่านทั้งคู่ ⇒ Unmatched สองแถว/แจ้งทีมสองครั้ง
  (เส้น One ปลอดภัยเพราะ `SubmitAsync` กันซ้ำใต้ล็อก ⇒ แถวที่สองเป็น `SubmitFailed`). แก้: unique index `(CompanyId, LineMessageId)` + ตรวจก่อนดาวน์โหลด (ประหยัด OCR ด้วย).
- **L2 — ผู้ใช้ที่ไม่ได้ผูกสแปมคิว+แจ้งทีม** — `:102` กรองเฉพาะกลุ่ม · 1:1 ไม่ผูกได้ 10 รูป/วัน/บัญชี (≤10 MB ต่อรูป) · ทุกแถว `ToStaffAsync:243-250` dispatch แจ้งทีม ⇒ หลายบัญชี = แจ้งเตือนท่วม.
  ยอมรับตามคำตัดสิน 260-1-5 แต่แนะนำ: เหตุ `Unbound` ไม่ dispatch รายแถว (รวมใน Autopilot) และ/หรือเพดานต่อบริษัทต่อวันของแถว Unbound.
- **L3 — กลุ่มกินโควตาก่อนตรวจการผูก** — `CustomerLineWebhookController.cs:66` เรียก `ConsumeMediaAsync` (เขียนตารางนับ) ก่อน `HandleImageAsync:102` ตรวจว่ากลุ่มผูก/แสดงยอด ⇒ ทุกรูปในทุกกลุ่มที่ OA อยู่ = เขียน DB หนึ่งครั้ง.
  ผลกระทบแค่ภาระ DB (ตัวนับต่อกลุ่ม ไม่กินโควตากลุ่มอื่น). แก้: ตรวจการผูกกลุ่มก่อน (query อ่านอย่างเดียว) แล้วค่อย consume. postback ไม่มีเพดานเลย และลายเซ็นผิดทุกครั้ง `LogWarning` (`:266`) = log spam ได้.
- **L4 — ใบที่มีสลิปรอตรวจเต็มยอดยังเป็นผู้สมัคร** — `CandidatesAsync:189-206` ไม่หัก `pendingSum` ⇒ สองใบยอดเท่ากัน ใบ A มีสลิปพอร์ทัลรอตรวจแล้ว ⇒ `Many` แทน `One`(ใบ B) และถ้าลูกค้าเลือก A ⇒ `SubmitFailed`.
  ไม่เกิดการแจ้งซ้ำ (ด่าน `PendingProblem` ใน `SubmitAsync` กันไว้). แก้: ตัดใบที่ `pendingSum + net > balance` ออกจากผู้สมัคร.
- **L5 — `Take(60)` ก่อนกรองชนิดเอกสาร** — `:195` เอา 60 ใบเก่าสุดทุกชนิด (รวมใบซื้อถ้าผู้ติดต่อเป็นทั้งผู้ซื้อ/ผู้ขาย) แล้วจึงตัดด้วย `Refusal` ⇒ ลูกค้าที่มีใบค้างเยอะ ใบใหม่ไม่ถูกพิจารณา ⇒ `NoMatch` ผิด.
  และ `ListOpenAsync:344` เรียก `DueAmountsForDocumentAsync` ต่อใบต่อผู้ติดต่อ (≤100×60 ครั้งต่อการโหลดหน้า · `OpenCountAsync`/`ListOpenAsync` เขียน `ExpireChoicesAsync` ใน GET). แก้: กรองชนิดขายใน SQL ก่อน `Take`.
- **L6 — `AssignAsync` ผูกใบของผู้ติดต่ออื่นได้โดยไม่เตือน + ค้นด้วยเลขที่แบบ FirstOrDefault** — `:364-368` (`contactId: null` ที่ `:378`) · แถวจากแชตของผู้ติดต่อ A ผูกกับใบของ B ได้ (พนักงานมีสิทธิ์ — ยอมรับ) แต่ไม่มีคำเตือน/ไม่ลง audit ว่าข้ามผู้ติดต่อ ·
  เลขที่ซ้ำข้ามชนิด/สาขา/ปี ⇒ `FirstOrDefault` ไม่มี order ⇒ อาจได้ใบผิด หรือได้ใบที่ `Refusal` แล้วโยนทั้งที่มีใบที่ถูก. แก้: กรองฝั่งขาย + มากกว่า 1 ใบ ⇒ 409 ให้เลือกจากรายการ · audit `crossContact=true`.
- **L7 — §H: ไม่มีเทสต์ทิศตรงข้ามของผู้เรียกเดิม `SlipOcrAssistService`** — พฤติกรรมเปลี่ยน 3 จุดสำหรับ badge สลิปค่าบริการ: `1500.00` ไม่เป็น 500 แล้ว (ถูก) · ทิ้ง 0.00 · `SlipOcrDate` มีค่าจากรูป "12 พ.ย. 69" (เดิม null) ·
  ยอดที่ติดตัวอักษร/จุดด้านหน้า (lookbehind `(?<![\d.,])`) ไม่ถูกจับแล้ว. ไม่มีเทสต์ที่เรียก `SlipOcrAssistService`/ข้อความสลิปค่าบริการจริงก่อน-หลัง (`grep SlipOcrAssist Accounting.Tests` = 0). แก้: เทสต์ "ใบที่ถูกอยู่แล้วไม่ถูกแตะ" 2–3 ข้อความจริง.
- **L8 — วันที่ "12/11/69" อ่านเป็น ค.ศ. 2069** — `OcrSlipText.cs:88` (`y < 100 ⇒ 2000+y`) ในขณะที่ `:96` ตีความ "69" แบบไทยเป็น พ.ศ. — สองเส้นในไฟล์เดียวไม่สอดคล้อง (ค้างจากตัวเดิม แต่ตอนนี้แสดงในคิวพนักงาน).
  แก้: สองหลัก > (ปี ค.ศ. ปัจจุบัน % 100) + 10 ⇒ พ.ศ.
- **L9 — ปิดสวิตช์แล้วปุ่มเดิมยังกดได้ / ไฟล์ช่องทางพนักงานติดป้ายลูกค้า** — `HandlePostbackAsync:259` ไม่สนใจ `enabled` (ปุ่มที่ออกไปแล้วใช้ได้ถึง 24 ชม. — อาจตั้งใจ ควรจดในคำตัดสิน) ·
  `AssignAsync` ส่ง `SlipEvidenceService.ChannelLineChat` ⇒ `FileAttachment.CreatedBy = "line-chat-customer"` แม้พนักงานผูก (ผู้ผูกอยู่แค่ใน `LineChatSlip.ResolvedBy`/audit).

---

## ตรวจแล้วไม่ใช่บั๊ก (อย่ารายงานซ้ำ)
- **HMAC/ตัวตนแชต**: `KeyFrom` = HMAC(channelSecret, label) · ลงนาม บริษัท+`LineIdHash(destination, sourceId)`+สลิป+ใบ · ตรวจ `row.SourceKey == sourceKey` + `FixedTimeEquals` + ใบต้องอยู่ใน `CandidatesJson` + ผู้ติดต่อยังผูก ณ ตอนกด + CAS `AwaitingChoice→Submitting` ⇒ ปลอม/ข้ามแชต/กดซ้ำ ไม่ได้
- **postback หลังใบชำระแล้ว**: CAS ใช้ครั้งเดียว · `SubmitAsync` reload ใบใต้ล็อกแล้ว `UploadProblem` ⇒ ใบชำระครบ/ยอดเกินยอดค้าง ⇒ `SubmitFailed` ในคิว (ไม่สร้างรายการ)
- **WHT**: `AmountsFor` ให้ Net = ยอดค้าง (หลังหักแล้ว) · Gross = ยอดค้าง + WHT ⇒ การแก้ใน `32c5a818` ถูก (โอนยอดเต็ม ⇒ `GrossBeforeWht` ไม่ผูกเอง ไม่แจ้งเกินยอดค้าง)
- **tenant isolation**: ทุก query ใน `LineChatSlipService`/`DueAmountsForDocumentAsync`/Autopilot มี `CompanyId` · ใบของผู้ติดต่ออื่นถูกตัดทั้งใน SQL และใน `Decide` (`:112`)
- **ที่เก็บไฟล์**: ผ่าน `IFileStorage` (ไม่เขียนดิสก์ตรง) · `/uploads/line-chat-slips` และ `/uploads/document-slips` ไม่อยู่ใน allow-list static (`Program.cs:1170-1189`) ⇒ ไม่เสิร์ฟตรง · เปิดผ่าน endpoint ที่ `RequirePermission(ViewPayments)` + `no-store`
- **ชนิดไฟล์**: sniff จากไบต์ (`DetectPublicType`) · ไม่รับ SVG/HTML · `messageId` ต้องเป็นตัวเลขล้วน ≤ 32 ก่อนต่อ URL
- **XSS คิว**: ทุก field ผ่าน `Layout.esc` (ชื่อผู้ติดต่อ · เหตุ · ref · ยอดที่อ่านได้ · เลขที่เอกสาร)
- **สิทธิ์**: ดู = `ViewPayments` · ผูก/ปิด = `ConfirmManually` + `RejectApiKey` · ระบบไม่ประทับรับเงิน/ไม่เรียกตัวยืนยัน (F2 ข้อ 3)
- **ยอดในกลุ่ม**: กลุ่มต้องผูก + ทุกปลายทางเปิดแสดงยอด (`:79`, `:102`) · 1:1 ที่ไม่แสดงยอด ข้อความไม่มียอด (`AmountPart`)
- **กฎเหล็ก #1**: ไม่มีจุดเรียก AI ใหม่ (Tesseract ในเครื่อง) ⇒ ไม่ต้องมี distillation
