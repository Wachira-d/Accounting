# ฝ่ายค้าน รอบ 264 แพ็ก P2 — อ่านบัญชีจากสลิป (QR + ผู้โอน/ผู้รับ)

ขอบเขต: `e6caad8c` (+ `66696a8f` docs) · อ่านโค้ดบน `claude/booking-service-features-sdemob` @ `eb62bb4a` · read-only (ไม่ได้คอมไพล์/รัน)
คำตัดสิน 264-4-1…8 · DOCUMENT_FLOW §2.12g-264-P2 · docs/lessons/ocr-pipeline.md

## สรุป

| # | ระดับ | เรื่อง | ไฟล์ |
|---|---|---|---|
| H1 | HIGH | เลขอ้างอิงรายการ**จริงที่ไม่ได้ redact** ของสลิป K+ อยู่ใน doc-comment — ประกอบ QR payload ของจริงกลับได้ทั้งดวง | `Accounting/Helpers/ThaiSlipQr.cs:11` |
| M1 | MED | ชั้น `SlipUnique` = `AllowsAuto=true, 0.90` ⇒ ท่อ**บันทึกจ่ายเอง**ในบัญชีผู้ซื้อจาก OCR 3–4 หลักของรูปในบริษัทผู้ขาย — ครั้งแรกที่ทางนี้ถูกป้อนจริง | `Helpers/PartnerPaySourceAccount.cs:44` · `PartnerPayablePipeline.cs:186-239,400-414` |
| M2 | MED | `ClassifyMask` รับโทเค็นที่**ไม่ได้ปิดบัง** (เลขบัญชีเต็ม / เลขบัตรเต็ม) เป็น BankAccount/Card ⇒ เก็บลง `SlipReadJson` + ส่งข้ามบริษัท — ขัด 264-4-5/7 "เลขที่ธนาคารปิดมาแล้ว" | `Helpers/OcrSlipText.cs:76-98,231-235` · `PartnerPaymentNoticeService.cs:173` |
| M3 | MED | ตัวถอด QR ของ production (ZXing.Net) **ไม่เคยรันกับรูปสลิปจริง** — ยืนยันด้วย zxing-cpp/OpenCV เท่านั้น เทสต์ใช้ QR สังเคราะห์ 8 px/โมดูล · ไม่ลอง inverted · คืน QR แรกที่เจอ (ไม่ใช่ QR สลิปที่ parse ได้) | `Services/Implementations/SlipQrImageDecoder.cs:201-259` · `Accounting.Tests/ThaiSlipQrTests.cs:80-93` |
| L1 | LOW | `NotOurs` กล่าวหา "อาจเป็นสลิปปลอม" จาก OCR อ่านเลขท้ายเพี้ยน 1 หลัก / อ่านตัวปิดเป็นเลข ⇒ RequiresChoice (400 ถ้าไม่เลือก) | `Helpers/SlipAccountMatch.cs:131-155` · `SlipReceivingAccount.cs:136-138` |
| L2 | LOW | PDPA เก็บรักษา: purge 90 วันลบรูปแต่คง `SlipReadJson` (ธนาคาร+เลขท้ายของผู้โอนที่ไม่รู้ตัว) ไว้ตลอดไป · DSR erase ไม่ครอบ `LineChatSlips` | `LineChatSlipService.cs:680-711` |
| L3 | LOW | PDPA ข้ามบริษัท: ผู้โอนที่ไม่ใช่ B (กรรมการ/บุคคลที่สาม) ⇒ ธนาคาร+เลขท้ายของเขาถูกเก็บถาวรบน `PartnerPaymentNotices` ของ B ทั้งที่ผลจับคู่ = NotOurs | `PartnerPaymentNoticeService.cs:164-173,204-212` |
| L4 | LOW | ถอด QR วิ่ง**แม้ไม่มี tessdata** · ไม่มีเพดานพิกเซลก่อน decode · sync บนเธรด request · TryHarder+AutoRotate ×2 ขนาด สำหรับสลิปที่ไม่มี QR | `TesseractSlipImageReader.cs:33-36` · `SlipQrImageDecoder.cs:206-223` |
| L5 | LOW | กันซ้ำด้วยเลขอ้างอิง: ref จาก QR ≠ ref จาก OCR ของสำเนาเดียวกันที่ครอปตัด QR / แถวก่อนรอบนี้ ⇒ สัญญาณ DuplicateRef หลุด | `OcrSlipText.cs` Parse · `LineChatSlipService.cs:137-139,284-291` |
| L6 | LOW | corpus ยังมี Ref1 ของบิล · เลขท้ายบัตร · MID/TID ร้าน ที่ไม่ได้แทน | `Accounting.Tests/SlipCorpusRound264.cs` |
| L7 | LOW | ผู้รับบนสลิป Unique แต่บัญชีนั้นใช้รับโอนไม่ได้ ⇒ ตกลงบันไดเดิม อาจแนะนำ "บัญชีรับโอนบัญชีเดียว" ซึ่งเป็น**อีกบัญชี** ทั้งที่สลิปบอกบัญชีที่เงินเข้า | `SlipReceivingAccount.cs:132-135,150-154` |

---

## H1 — เลขอ้างอิงจริงหลุดใน doc-comment (HIGH)

`Accounting/Helpers/ThaiSlipQr.cs:11`:
```
///          02 LL "<เลขที่รายการจริง — ลบแล้ว>" ] ← เลขอ้างอิงรายการของธนาคาร ...
```
`016` + วันที่ในปี + เวลา + อักษร/เลขของธนาคาร = เลขที่รายการของสลิป K+ `KplusSameBank` (ค่าจริงลบออกจากไฟล์นี้ — รอบ 264 P2 ชุดแก้ H1)
("23:27 น." · ใน corpus แทนเป็น `016790246801ERTY0209`) ไม่มีแพตเทิร์นแทนค่า `790246` ที่ทุกค่าที่ redact แล้วมี ·
`git log -S` ⇒ เข้ามาใน `e6caad8c` (branch push แล้ว)

**ผลเสีย:** เอกสารเดียวกันบอกโครงสร้างครบ (`00`/`000001`/`01`=`004`/`02`=ref/`51`=TH/CRC-16 ในโค้ดเดียวกัน) ⇒ ใครก็ประกอบ payload
`<payload QR ตรวจสลิปของจริง — ลบแล้ว>` + `ThaiSlipQr.Crc16` = **QR ตรวจสลิปของจริง** ส่งให้บริการตรวจสลิปของธนาคาร/ผู้ให้บริการ
แล้วได้ชื่อผู้โอน-ผู้รับ + ยอด — สิ่งที่ corpus ระบุว่ากันไว้แล้ว ("แทนกลาง…กันการนำไปตรวจกับธนาคาร") ถูกข้ามด้วยบรรทัดเดียว (PDPA ม.27 เปิดเผยโดยไม่มีฐาน)

**แก้ขั้นต่ำ:** แทนด้วยค่าที่ redact แล้วจาก corpus (`016790246801ERT90209`) หรือ `"<ref>"` + คอมมิตแก้ + ถามเจ้าของว่าจะ rewrite history ของ branch ไหม
(ก่อน merge main) · ราคาถูกกว่าคือให้ `tools/` มี checker เล็ก ๆ: ทุกสตริงที่ match `^0\d{2}\d{3}\d{6}[A-Z]{3}\d{5}$` หรือ prefix `00\d\d000600000101` ในไฟล์ที่ไม่ใช่ corpus ⇒ ฟ้อง

---

## M1 — สลิป OCR ⇒ บันทึกจ่ายอัตโนมัติในบัญชีผู้ซื้อ (MED · ต้องให้เจ้าของตัดสิน)

ห่วงโซ่ (ตรวจแล้วทุกข้อ):
1. `PartnerPaySourceAccount.Precedence` แถว `SlipUnique` = `AllowsAuto: true, 0.90m` (`:44`) ≥ `AutoThreshold 0.85`
2. ก่อนรอบนี้ `SlipMatch: null` เสมอ ⇒ ทางนี้ไม่เคยเกิด · รอบนี้ `SlipMatchAsync` ป้อนจริง (`PartnerPayablePipeline.cs:400`)
3. `PartnerAutoRecordPolicy.Decide` ⇒ `if (!f.Source.AllowsAuto) …; return Auto` (`PartnerAutoRecordPolicy.cs:124-125`) ⇒ ท่อเรียก `CreatePaymentAsync(... OverrideBankAccountId: bank ...)` ไม่มีคนกด (`PartnerPayablePipeline.cs:221-225`)
4. ความยินยอมมี `DefaultPayBankAccountId` = บัญชีเดียวกับสลิป ⇒ ไม่อยู่ใน losers ⇒ ยัง auto — คือ**คำตัดสิน 268-1-1 ที่ปิด auto ของชั้นนี้ไว้ ("ไม่ใช่หลักฐานว่าเงินออกจากบัญชีนั้นจริง — จนกว่า P4") ถูกเปิดกลับ**ด้วยหลักฐานที่อ่อนกว่า: Tesseract อ่าน 3–4 หลักจากรูปที่อยู่**อีกบริษัท** (A)
5. `PartnerPaySourceSlipRound264Tests.cs:60` ล็อก `AllowsAuto == true` ⇒ ตั้งใจ แต่ 264-4-7 เขียนว่า "กติกาขัดกันของ 264-3-1 คงเดิม" ไม่ได้บอกว่าเปิด auto จริงครั้งแรก

**สถานการณ์:** B มี 2 บัญชีกสิกร (••0621 กับ ••0821) · Tesseract อ่าน `X0621` เป็น `X0821` (ตัวเลขห้าม fuzzy แต่ OCR ไม่ได้ fuzzy-free) ⇒ Unique ผิดบัญชี ⇒ JE เครดิตธนาคารผิดบัญชีใน B โดย "⚙️ ระบบบันทึกเอง" · ไม่มี QR ยืนยันธนาคาร (KBIZ ไม่มี QR) · แถวแจ้งเก็บแค่ bank+mask ไม่มีที่มา (QR/ข้อความ) ให้ฝั่ง B แยกได้
ยังไม่มีชั้นที่ 1 (BankLine, P4) จับผิด

**แก้ขั้นต่ำ (เลือกหนึ่ง):** (ก) `SlipUnique AllowsAuto=false` (preselect อย่างเดียว — สอดคล้อง 268-1-1) จนกว่า P4 · หรือ (ข) auto เฉพาะเมื่อธนาคารผู้โอนยืนยันด้วย QR
(`Roles==Qr` หรือ `BankFromQr`/ข้อความตรง QR) **และ** หลักที่เห็น ≥ 4 — ต้องส่ง flag ที่มาไปบน `PartnerPaymentNotices` · บันทึกเป็นคำตัดสินใน DECISIONS

---

## M2 — เลขที่ไม่ได้ปิดบังถูกเก็บ/ส่งข้ามบริษัท (MED · PDPA ม.22/ม.27 + PCI)

`ClassifyMask` (`OcrSlipText.cs:76-98`):
- `masked` ตรวจเฉพาะกลุ่มแรก**เมื่อ**มีตัวปิด — โทเค็นที่ไม่มี `x` เลย เช่น `123-4-56789-0` ผ่าน shape 3-1-5-1 + `^x*[0-9]*$` ⇒ `BankAccount` พร้อม 10 หลักเต็ม
- Card (`:90`): กลุ่มแรก 4 ตัวเลขก็พอ ⇒ `4417-7012-3456-1154` (PAN เต็มจาก OCR ภาพบิลบัตร/สลิปที่พิมพ์เลขเต็ม) ⇒ `Card` เก็บ PAN เต็ม
- `ToStoredJson` (`:234`) เก็บ `AccountMask` ทุกชนิด รวม Card (ทั้งที่ Card = NotAnAccount ไม่ใช้จับคู่) ⇒ text คอลัมน์ไม่เข้ารหัส
- ผู้โอนชนิด BankAccount ⇒ `PartnerPaymentNotices.SlipPayerAccountMask` (32 ตัว พอใส่เลขเต็ม) ข้ามไปบริษัท B · ป้าย `PartyLabel` ใน `Reason` ก็แสดงเลขเต็ม (`"••1234567890"`)

ไม่มีเทสต์ทิศนี้ (`OcrSlipTextPartiesTests.cs:214-226` มีแต่โทเค็นที่ปิดแล้ว) · สลิปบางธนาคาร/ภาพหน้าบัญชี/สมุดบัญชีที่ลูกค้าส่งผิดในแชตแสดงเลขเต็มจริง

**แก้ขั้นต่ำ:** BankAccount ต้องมี `x` อย่างน้อย 1 ตัว **หรือ** ตัดให้เก็บ/ส่งได้ไม่เกิน 4 หลักที่เห็นขวาสุด (ตัวที่เหลือเป็น `x`) ที่ชั้น `ToStoredJson` + `SlipPayerForNotice` ·
Card ⇒ ไม่เก็บ mask (หรือเก็บแค่ 4 ท้าย) · เพิ่มเทสต์ `123-4-56789-0` / `4417-7012-3456-1154` ⇒ ไม่ถูกเก็บเต็ม

---

## M3 — ตัวถอด QR ของจริงยังไม่ถูกพิสูจน์บนกระดาษจริง (MED)

- DECISIONS 264-4-1 / ThaiSlipQr doc: "ถอดด้วย ZXing + OpenCV ในเครื่อง" — คือ zxing-cpp/OpenCV ไม่ใช่ ZXing.Net 0.16.9 ที่ ship
- เทสต์เดียวของ `SlipQrImageDecoder` (`ThaiSlipQrTests.cs:80-93`) = QRCoder PNG `GetGraphic(8)` (8 px/โมดูล, quiet zone เต็ม, ขาวดำล้วน) — ไม่ใช่ mini-QR ~120–200 px บนพื้นลวดลาย JPEG
- `DecodeBitmap` (`:252-256`): ไม่ตั้ง `TryInverted` · `Decode` คืน QR **แรก** ที่เจอ — สลิปที่มี QR อื่น (โฆษณา/QR ร้าน) ⇒ ได้ QR ผิด ⇒ `ThaiSlipQr.Parse` = null ⇒ เสียทั้ง ref และบทบาท (fallback ปลอดภัย แต่ฟีเจอร์ไม่ทำงานเงียบ ๆ)
- ผลคือ ทุกข้อในคำตัดสิน 264-4-8 ที่เปลี่ยนเพราะ QR ("กรุงไทย/SCB/K+ ref ผิด → จาก QR", "LooksLikeSlip false → true") **พิสูจน์แค่ตัว parse** ไม่ใช่ pipeline จริง

**แก้ขั้นต่ำ:** รัน `SlipQrImageDecoder.TryDecode` (.NET) กับรูปจริง 6 ใบในเครื่อง (ไม่ commit รูป) แล้วจดผลใน lessons/DECISIONS 264-4-8 ·
ใช้ `DecodeMultiple` แล้วเลือกผลแรกที่ `ThaiSlipQr.Parse != null` · `TryInverted = true` · ถ้ารูปจริงถอดไม่ได้ที่ 2400 ให้ลอง crop ครึ่งล่าง (QR ตรวจสลิปอยู่ล่างเสมอใน 6 ใบ) ก่อนย่อครึ่ง

---

## L1 — `NotOurs` จาก OCR เพี้ยน (LOW)

`SlipAccountMatch.Match` ⇒ ธนาคารตรง + หลักที่เห็น ≥ 3 แต่ไม่มีบัญชีที่ตรงทุกหลัก ⇒ `NotOurs` ⇒ `SlipPayeeNotOursReason` "อาจเป็นสลิปปลอม" + `RequiresChoice` (ไม่เลือก = 400)
- OCR อ่านเลขเพี้ยน 1 หลัก (`8`↔`3`, `1`↔`7`) บนบัญชีของเราเอง ⇒ ข้อกล่าวหาผิดต่อลูกค้า + งานเพิ่มทุกใบ
- OCR อ่านตัวปิดตัวสุดท้ายก่อนเลขเป็นตัวเลข (`xx319` → `x2319`: ผ่าน `^x*[0-9]*$`) ⇒ 5 หลักที่เห็น หนึ่งหลักถูกแต่ง ⇒ NotOurs · ตัว docstring เองบอกว่าบัญชีจริง "เห็น 4 หลัก" ทุกใบ

**แก้ขั้นต่ำ (ไม่ใช่ fuzzy match — แค่ลดการกล่าวหา):** ถ้ามีบัญชีของเราที่ธนาคารตรงและหลักที่เห็นต่าง ≤ 1 ตำแหน่ง **หรือ** หลักที่เห็น > 4 ⇒ `Unreadable` (ไม่ใช่ NotOurs) · ใช้กับฝั่ง B ด้วย (ลด `OfferDirectorAdvance` ผิด) · เทสต์ทิศตรงข้าม: เพี้ยน 2 หลัก/ธนาคารอื่น ยังเป็น NotOurs

## L2 — ข้อมูลผลอ่านค้างหลังลบรูป (LOW)

`PurgeExpiredAsync` (`LineChatSlipService.cs:680-711`) ตั้ง `FileDeletedAt` + Dismissed แต่ไม่ล้าง `SlipReadJson` (ไม่ล้าง `OcrRef/OcrAmount*` ด้วย — เดิม) ⇒ แถวที่ไม่ได้ผูกเอกสาร (ไม่มีฐานเก็บตาม พ.ร.บ.บัญชี) เก็บธนาคาร+เลขท้ายผู้โอน/ผู้รับตลอดไป ·
RoPA `LineChatSlipRule.RopaRetention` พูดถึงรูปเท่านั้น · DSR (`PdpaService*`) ไม่มี `LineChatSlips`
**แก้:** ตอน purge แถวที่ไม่ใช่ Submitted/Assigned ⇒ `SlipReadJson = null` (Submitted คัดลอกไป intent แล้ว) + เพิ่มในข้อความ RoPA

## L3 — ข้อมูลผู้โอนที่ไม่ใช่ B ถูกเก็บที่ B (LOW)

`NotifyCoreAsync` คัดลอก bank+mask ทุกครั้งที่ผู้โอนเป็นชนิดบัญชี — ไม่รู้ว่าเป็นของ B หรือไม่ · กรณีกรรมการ/ญาติ/บุคคลที่สามจ่ายแทน ⇒ ฝั่ง B ได้ `NotOurs` (ใช้แค่เปิดตัวเลือกกรรมการจ่ายแทน) แต่ mask ของบุคคลนั้นค้างบนแถวแจ้งของ B ถาวร (ไม่มีผู้ล้าง) · ไม่ผูก consent ใด (264-4-7 เลือกไว้)
**แก้:** หลัง `SlipMatchAsync` ครั้งแรกฝั่ง B ได้ `NotOurs/Unreadable` ⇒ ล้าง `SlipPayerAccountMask` เหลือ kind (เพิ่มคอลัมน์ผลเช่น `SlipPayerMatch`) · หรือส่งแค่ 4 หลักขวา (M2) ซึ่งลดความเสี่ยงส่วนใหญ่อยู่แล้ว

## L4 — ต้นทุน/ความทนทานของการถอด QR (LOW)

- `TesseractSlipImageReader.cs:33-36`: เดิมไม่มี tessdata ⇒ คืน Empty ทันที · ตอนนี้ decode เต็มรูปทุกครั้ง (ทางเข้าใหม่ของการ decode)
- `SkiaImageOps.DecodeUpright` decode เต็มก่อนเช็กขนาด — PNG 10 MB บีบอัดสูงขนาด 30k×30k ⇒ จอง native ~3.6 GB (มี `PaymentQrImageValidator.MaxPixels = 12M` อยู่แล้วแต่ไม่ได้ใช้) · Tesseract path มีความเสี่ยงเดิม แต่ตอนนี้ decode สองครั้งต่อรูป
- เรียก sync ใน `ReadAsync` (ไม่ `Task.Run`, ไม่รับ `ct`) · สลิปไม่มี QR (ttb/KBIZ/K+ บัตร) จ่ายเต็ม TryHarder+AutoRotate ที่ 2400 แล้วซ้ำที่ครึ่ง
**แก้:** เช็ก `codec.Info.Width*Height ≤ PaymentQrImageValidator.MaxPixels` ก่อน `SKBitmap.Decode` · ห่อ `Task.Run` + ct · ข้าม AutoRotate (สลิปตั้งตรงหลัง EXIF แล้ว)

## L5 — กันซ้ำด้วย ref เปลี่ยนแหล่ง (LOW)

ref ของแถว LINE ตอนนี้ = QR (ถ้าถอดได้) ไม่งั้น OCR · สำเนาเดียวกันที่ส่งซ้ำแบบครอป QR ออก (sha ต่าง) ⇒ ref จาก OCR (กรุงไทย: `79024680135` ครึ่งเดียว) ≠ ref QR ของใบแรก ⇒ `DuplicateRef` ไม่ติด (ก่อนรอบนี้ OCR ทั้งคู่ — มีโอกาสตรงกัน) · แถวก่อน deploy เก็บ ref OCR ทั้งหมด
เป็นสัญญาณให้คนดูเท่านั้น (M1 รอบ 260) ⇒ LOW · **แก้:** เทียบซ้ำทั้ง `QrRef` (จาก `SlipReadJson`) และ ref จาก OCR ของข้อความเสมอ (เก็บสองค่า)

## L6 — corpus redaction ยังเหลือ (LOW)

`SlipCorpusRound264.cs`: `ScbBillRefs` "หมายเลขร้านค้า1 : <ค่าจริง — แทนแล้ว>" (Ref1 ของบิลมักเป็นเลขลูกค้าของผู้จ่ายที่ผู้รับชำระ) ไม่มีแพตเทิร์นแทน · เลขท้ายบัตร `…671 4`, `…1154` · MID/TID ร้าน · เลขท้ายบัญชีจริงทุกใบ (คงไว้โดยเจตนา — ยอมรับได้เมื่อไม่มีชื่อ)
ไม่พบชื่อคน/บริษัท/รูปใน diff · Biller ID เป็นเลขสาธารณะ
**แก้:** แทนกลาง Ref1 ด้วย `790246…` แบบเดียวกับตัวอื่น

## L7 — Unique แต่ใช้ไม่ได้ ⇒ แนะนำอีกบัญชี (LOW)

`Suggest` (`SlipReceivingAccount.cs:132-135`): Unique + `!usable.Contains(sid)` ⇒ ตกลงบันไดเดิม ⇒ `SoleTransferAccount` อาจแนะนำบัญชีรับโอนเดียว**ที่ไม่ใช่**บัญชีบนสลิป — ขัดเจตนา 264-4-6 "ไม่แนะนำบัญชีอื่นแทน" ในกรณีหลักฐานชี้บัญชีอื่นชัด
**แก้:** Unique แต่ใช้ไม่ได้ ⇒ `RequiresChoice` + เหตุ "บัญชีบนสลิปใช้รับโอนไม่ได้ (ปิดใช้/ไม่ผูกผัง)"

---

## ตรวจแล้วไม่ใช่บั๊ก (ไม่ต้องรายงานซ้ำ)

- **QR/ref ไม่ทำให้ยืนยันเงินอัตโนมัติ** — `TrySubmitAsync` ส่ง `bankRef: null` (`LineChatSlipService.cs:333`) · `BankReceiptSettleService` จับคู่ด้วย `SlipBankRef` ที่ลูกค้าพิมพ์เท่านั้น (`:184`) · `SlipPayee` เป็นค่าแนะนำ ยังต้องกดยืนยัน · LINE auto-submit ยังต้องมี `Amount` (QR ไม่มียอด) · QR ที่ลอกจากสลิปเก่ากลับทำให้ `DuplicateRef` ติด (ดีขึ้น)
- **Tenant isolation** — query ใหม่ทุกตัวมี CompanyId: `SlipMatchAsync` (`b.CompanyId == companyId`) · `SlipPayerForNoticeAsync` (CompanyId ผู้ขาย) · คัดลอกไป intent (`i.CompanyId == companyId`) · `LoadAccountsAsync` เดิม
- **Kill-switch** — ไม่มีจุดเรียก AI ใหม่ (`UsedAi => false`) · ไม่ต้อง AiFeatureKey
- **SlipReadJson ไม่มีชื่อ/Memo** — `StoredParty(Kind, Bank, Qr, Mask, Biller)` เท่านั้น · Memo อยู่ใน `Reading` หน่วยความจำ · อ่านกลับทิ้ง mask ที่จัดชนิดไม่ตรง (ยกเว้นประเด็น M2 ที่ "ไม่ได้ปิด" จัดชนิดผ่าน)
- **ผู้เรียกเดิม (ratchet §H)** — `SlipOcrAssistService` เรียก `Parse(text)` ไม่มี QR ⇒ ref เดิม · ผลต่างมีแค่ (ก) วันที่อังกฤษ (ลองหลังสองรูปเดิม — ใบที่เคยได้วันที่ไม่ถูกแตะ) (ข) `NormalizeThai` ในคำนำยอด (แทนเฉพาะ `ํา`→`ำ` ⇒ ทิศ null→ยอดตามคำนำ; ใบที่มียอดเดียวได้ค่าเดิม) · ไม่พบใบที่เคยถูกแล้วผิด
- **BE/CE** — อังกฤษ 2 หลัก ⇒ `TwoDigitYear` · 4 หลัก > 2400 ⇒ −543 · ไทยคงเดิม
- **ป้าย "อาก"/"อจาก"/"ถึง"/"to"** — เทียบ**ทั้งโทเค็น**หลัง `StripLead` (ไม่ใช่ substring) + ต้องมีป้ายผู้โอนก่อนป้ายผู้รับ ⇒ "อากร"/"ถึงวันที่" ไม่ติด · ไม่พบเส้นทางป้ายเท็จที่ให้ผล Unique
- **mask ถูก OCR ตัด/เลื่อน** — shape 3-1-5-1 / 3-6-1 + เทียบตำแหน่งจากขวา ⇒ ตัวหาย/เกินเปลี่ยนความยาวกลุ่ม ⇒ Unknown (ไม่เกิด Unique จากการเลื่อนตำแหน่ง) · เกณฑ์ 3 หลักเกิดได้เฉพาะเมื่อ OCR อ่านเลขเป็น x ซึ่งไม่สร้าง Unique ผิด (แค่ลดหลักฐาน)
- **`SameBank` ยุบมาใช้ตาราง** — เฉพาะเมื่อทั้งสองชื่ออยู่ในตาราง ⇒ หลวมขึ้นสำหรับชื่อพ้อง (TMB/ttb/ธนชาต) ไม่พบกรณีที่เดิมเท่ากันแล้วตอนนี้ต่าง

---

## ผลแก้ (ทีม P2 · คำตัดสิน 264-4-9…12)

| # | ผล |
|---|---|
| H1 | ✅ ค่าจริงในไฟล์นี้ (บรรทัด ref/payload/Ref1) ถูกลบ · `tools/slip_secret_check.py` + `tools/slip_secret_hashes.txt` (แฮชเท่านั้น) + negative test · **ประวัติคอมมิต e6caad8c ยังมีค่าจริง — rewrite history = เจ้าของตัดสิน** |
| M1 | ✅ `SlipUnique` preselect เท่านั้น (`AllowsAuto=false` · `SlipUniqueNotAutoReason`) — 264-4-9 |
| M2 | ✅ ต้องมีตัวปิด · ≤ 4 หลักขวาสุด · บัตรไม่เก็บเลข · เทสต์สองทิศ `OcrSlipTextPartiesTests.ฝ่ายค้าน_M2_…` + `ClassifyMask` — 264-4-10 |
| M3 | ✅ `DecodeMultiple` + `TryInverted` + เลือกดวงที่ parse ได้ · รัน ZXing.Net กับรูปจริง 10 ใบในเครื่อง: 6/6 ใบที่มี QR ถอด+parse ผ่าน (006×2 · 014×3 · 004×1) · 4 ใบไม่มี QR = null — 264-4-11 |
| L1 | ✅ ต่างหนึ่งหลักจากบัญชีธนาคารเดียวกัน ⇒ Unreadable · ทิศตรงข้าม (สองหลัก/ธนาคารอื่น) ยัง NotOurs |
| L2 | ✅ purge ล้าง `SlipReadJson` + RoPA · DSR ของ `LineChatSlips` ไม่แตะในรอบนี้ (ผลอ่านไม่มีชื่อ ⇒ ระบุตัวเจ้าของข้อมูลจากคำขอ DSR ไม่ได้) |
| L3 | ⏸ ไม่แก้แยก — เหตุอยู่ใน 264-4-12 (หลัง M2 เหลือธนาคาร + ≤ 4 หลักที่ปิดแล้ว · ตัวล้างต้องมีผู้เขียนบนเส้นอ่าน ⇒ ทำพร้อม P4) |
| L4 | ✅ เพดานพิกเซลก่อนถอด · `Task.Run` + ct · ไม่ AutoRotate · ⏸ ยังถอดเมื่อไม่มี tessdata (ตั้งใจ — QR ให้ ref/ธนาคารโดยไม่พึ่ง OCR) |
| L5 | ✅ `Reading.TextRef` · กันซ้ำเทียบทั้ง ref จาก QR และจากข้อความ · ⏸ ไม่เพิ่มคอลัมน์เก็บสองค่า (แถวใหม่เก็บ ref จาก QR · สำเนาครอปที่มาทีหลังเทียบค่าจากข้อความของตัวเองกับ ref เดิมได้เฉพาะแถวก่อนรอบนี้) |
| L6 | ✅ แทน Ref1 · เลขท้ายบัตร · MID/TID |
| L7 | ✅ ต้องเลือกเอง + `SlipPayeeUnusableReason` · ทิศตรงข้าม: ไม่มีผลอ่าน ⇒ บัญชีเดียวตามเดิม |

