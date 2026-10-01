# รอบ 200 — ฝ่ายค้านรอบสาม: งานทีม V1H (merge 82eddbe6 = abf0892f + 6c412a07)

ตรวจที่ HEAD 09c8556a · read-only · ไม่มี .NET SDK (CI 82eddbe6 เขียว build + static)

**ความเสี่ยงคอมไพล์: ไม่พบ** — ลายเซ็นที่เปลี่ยนทุกตัวแก้ผู้เรียกครบ (`IEtaxInvoiceService.VoidAsync` · `ReverseMultiDocPaymentInternalAsync` tuple · `EtaxInvoiceResponse` +3 · `EtaxReissueReviewReport` +1 · `EtaxCancellationClaim` ค่าตั้งต้น) ·
`FlaggedReceiptKeepsTaxPoint` → internal (มี InternalsVisibleTo) · ไม่มีนิยามซ้ำหลัง merge · ไล่ assert ของ `VoidReissueR200HTests` ด้วยมือตรงทุกตัว

| ID | P | ที่ | สถานการณ์ล้ม | สถานะ |
|---|---|---|---|---|
| V1H-O1 | P2 | `DocumentService.cs` ~9906 · `DocumentService.Reissue.cs` ~745 | ทาง (ค) ล้างธงถาวร — ยกเลิกการชำระใหม่/เช็คเด้งภายหลัง ⇒ ใบกำกับเดิมระบุรับเงินแต่เงินหาย ไม่มีธง | ส่งทีม V1I |
| V1H-O2 | P2 | `EtaxInvoiceService.cs` ~755 + `EtaxVoidPolicy.Decide` | ตัดสินจากสถานะแถว ไม่ใช่ `EffectiveEtaxAsync` ⇒ แถวที่ส่งทางอีเมลประทับเวลากรมฯ แล้วยกเลิกได้ไม่ต้องมีหลักฐาน + audit เท็จ | ส่งทีม V1I |
| V1H-O3 | P2 | `DocumentService.cs` ~9450 `VoidPaymentAsync` | ไม่ล็อกแถวเอกสาร ⇒ race กับการรับชำระใหม่ ⇒ ถอยภาษีขายทั้งที่มีการชำระที่มีผล + lost update | ส่งทีม V1I |
| V1H-O4 | P3 | `Reissue.cs` ~711 vs `DocumentController.cs` ~1129 | ทาง ค ต้องสิทธิ์อนุมัติเพิ่ม ต่างจาก ก/ข | **ตัดสิน: คงตามโค้ด** — แก้ถ้อยคำข้อ 64 |
| V1H-O5 | P3 | `EtaxInvoiceService.cs` ~760 | ไฟล์เก่าก่อนส่ง e-Tax ผ่านเป็นหลักฐานยกเลิก | ส่งทีม V1I |
| V1H-O6 | P3 | `EtaxController.cs` ~195 | เรียกด่านไฟล์แนบแม้ไม่ส่งไฟล์ ⇒ ผู้ใช้บางคนยกเลิกแถว Generated/Signed ไม่ได้ | ส่งทีม V1I |
| V1H-O7 | P3 | `DocumentVoidPreconditions.cs` ~164 | ข้อความธง Submitted แนะนำทาง (ค) ที่ปฏิเสธ Submitted | ส่งทีม V1I |

## F3 ข้อ 11
1. ทางเข้า: ยกเลิกรับชำระหน้าเอกสาร · เช็คเด้ง (`ChequeService`) · ถอยรอบโอน settlement ⇒ `VoidPaymentAsync` ตัวเดียว · ใช้ตัวถอยภาษีตัวเดียวทั้งใบเดียว/หลายใบ ·
   `VoidDocumentAsync` ของเอกสารที่ชำระด้วยการจัดสรรหลายใบในเดือนปิด ⇒ 409 (เท่าเส้นใบเดียว — ไม่ใช่บั๊ก) · settlement ไม่สร้างการชำระหลายใบ ⇒ ไม่ชนทีม SG
2. ถอยซ้ำ/ผิดเดือน: ไม่พบ (ถอยเฉพาะ PaidAmount ≤ 0.005 และ OutputVatDueAt มีค่า แล้วล้าง · กรอง ReversedByEntryId · ลงวันที่ของ JE ย้ายภาษีเอง) — ช่องเดียวคือ race O3
3. สถานะปลายทาง: ป้าย "ยกเลิกในระบบนี้" ถูก ยกเว้น O2 · ทาง (ค) ไม่คืนธง (O1) · รายงานข้อ 53 อ่านอย่างเดียว tenant ครบ

## ตรวจแล้วไม่ใช่ปัญหา
`LivePaymentCoverageAsync` สูตรเดียวกับ PaidAmount ไม่นับการชำระหลายใบซ้ำ · ใบเสร็จธรรมดาติดธงไม่ได้ (ออก e-Tax ไม่ได้) · `ReceiptHoldsTaxPointFor` ใบเสร็จรอบโอนอ้างใบเดิมเสมอ ⇒ เส้นใบเดียวเหมือนเดิม
