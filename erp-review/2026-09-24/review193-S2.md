# ฝ่ายค้านรอบ 193: security ทีม S2 (fd880c2): ด่านไฟล์แนบกลาง `IAttachmentAccessGate`

> ตรวจแบบอ่านอย่างเดียว เครื่องนี้ไม่มี .NET SDK จึงไม่ได้คอมไพล์ ข้อสรุปทั้งหมดได้จากการอ่านซอร์สที่ HEAD `d2f8b2e` (ซึ่งมี fd880c2 แล้ว)
> ลองถอดด่านใน checker เฉพาะในสำเนาที่ scratchpad ไม่ได้แตะไฟล์ในเรพ
> ลำดับ: CONFIRMED, PLAUSIBLE, ตรวจแล้วไม่มีปัญหา, แล้วจึงตารางเทียบ U2 กับ S2 (คำถามข้อ 1)

---

## CONFIRMED

### S2-C1 (P0): `POST ocr/scan/{fileAttachmentId}` ทำให้ไฟล์แนบ**ทุกชนิด**ในบริษัทกลายเป็น "สแกนที่ยังไม่ผูก" จึงหลุดทุกด่านของ U2 และ S2
- **ต้นทาง:** `OcrController.cs:222-239` มีแค่ `[Authorize]` ระดับคลาส แล้วเรียก `OcrService.ScanAsync(companyId, fileAttachmentId)` ทันที
- **service ไม่ดูชนิดไฟล์:** `OcrService.cs:148-150` ค้นไฟล์ด้วย `Id + CompanyId` อย่างเดียว ไม่ดู `EntityType` แล้วสร้าง `OcrScanResult` ใหม่ที่ `FileAttachmentId` ชี้ไฟล์นั้น (`:184-187`)
- **ผล 1 (อ่านเนื้อหา):** response คือ `OcrResultResponse` ซึ่งมี `RawTextContent`, ชื่อผู้ขาย, เลขผู้เสียภาษี และยอด (`OcrService.cs:8914-8923` ใน `MapToResponse`)
  - ข้อความของสลิปเงินเดือน (`PayrollRun`), ไฟล์แนบของเอกสารลับ, ใบเสร็จในใบเบิกของคนอื่น, สลิปมัดจำของแขก (`LodgingReservation`) และไฟล์ 50 ทวิ ในถังก่อนบันทึก จึงหลุดออกมาใน response ของคำขอนี้เอง
- **ผล 2 (ได้ไฟล์ต้นฉบับ):** สแกนใหม่นี้เดินด่าน S2 แล้วผ่าน
  - `AttachmentPermissionScope.ScanFileOwner` คืน `null` เพราะชนิดไฟล์ไม่ใช่ `"Document"` และสแกนใหม่ไม่มี `CreatedDocumentId`
  - ด่านจึงตกไปใช้คีย์อ่านของ OCR ซึ่งว่าง (`AttachmentAccessGate.cs:180-185`) แล้วผ่าน
  - `GET ocr/{newScanId}/image` (`OcrController.cs:1395-1427`) จึงคืน**ไบต์ของไฟล์ต้นฉบับ**
  - สรุป: ด่านความลับ Payroll (PDPA ม.26), คีย์อ่านของ Lodging/SiteOrder, ผู้ตรวจใบเบิก และ "ถังก่อนบันทึกเปิดได้เฉพาะผู้อัปโหลด" ของ S2 ถูกข้ามทั้งหมด
- **ผล 3 (ลบหลักฐาน):** ต่อด้วย `DELETE ocr/{newScanId}` (`OcrController.cs:1254`) ไปถึง `DeleteScanAsync` (`OcrService.cs:8652-8659`)
  - ขั้นนี้**ลบไฟล์จริงบนดิสก์และลบแถว `FileAttachments`** ของไฟล์เหยื่อ โดยไม่ดูชนิดและไม่เดิน `AttachmentRetention`
  - สรุป: สมาชิกคนไหนก็ลบสลิปเงินเดือน, ใบเสร็จนำส่ง หรือไฟล์แนบของเอกสารที่อนุมัติแล้วได้ ขัด พ.ร.บ.การบัญชี ม.10 / §87/3
- **เงื่อนไข:** ต้องรู้ attachment id ซึ่งเป็น threat model เดียวกับที่ U2 ใช้อธิบายด่านดาวน์โหลด ("เดิมสมาชิกทุกคนโหลดสลิปเงินเดือนได้ถ้ารู้ id")
- **ทำไม checker ไม่เห็น:** `attachment_gate_check.discover()` ค้นเฉพาะ field ชนิด `IFileAttachmentService` หรือ `.FileAttachments` ใน controller ส่วนเส้นนี้ผ่าน `IOcrService`
  - ข้อความในคอมมิต F3-7 ที่ว่า "ทางเข้าที่แตะไฟล์แนบแต่ไม่เรียก gate = 0" จึง**ไม่จริง**
- **แนวแก้:**
  - `ScanAsync` รับเฉพาะไฟล์ `EntityType == "OcrScan"` (ไฟล์ที่เพิ่งอัปโหลดเพื่อสแกน) หรือเรียก `gate.DenyAttachmentAsync(..., Read, attachmentId)` ก่อนสแกน
  - `DeleteScanAsync` ลบไฟล์จริงเฉพาะเมื่อไฟล์ยังเป็นชนิด `OcrScan` และเดิน retention
  - เพิ่ม `Scan`, `Retry` และ `Delete` เข้า TARGETS

### S2-C2 (P0): `POST ocr/{scanId}/link-document/{documentId}` ย้ายไฟล์ของเอกสารใบหนึ่งไปเป็นของอีกใบได้โดยไม่มีด่าน
- **จุดที่ไม่มีด่าน:**
  - `OcrController.cs:442-449` ไม่มีด่านใด
  - `LinkScanToExistingDocumentAsync` → `RelinkScanFileToDocumentAsync` (`OcrService.cs:8307-8316`, `:8325-8337`) ตั้ง `EntityType="Document", EntityId=<ใบปลายทาง>` ให้ไฟล์โดย**ไม่ดูว่าไฟล์เป็นของใครอยู่** และเขียนทับ `CreatedDocumentId`
- **ขั้นการโจมตี:**
  1. ผู้ใช้ที่ไม่เห็นฝั่งรายจ่ายหรือชั้นความลับของใบ A หา scanId ของใบ A ได้จาก `GET documents/{A}/linked-scan` (`DocumentController.cs:294-308`) ซึ่งไม่มีด่านฝั่ง/ความลับเลย และต้องรู้แค่ docId
  2. ยิง link-document ไปยังใบ B ที่ตัวเองเห็น
  3. ดาวน์โหลดไฟล์ผ่าน `GET attachments/{id}/download` ได้ เพราะด่านเอกสารตัดสินด้วยใบ B
  4. ใบ A สูญหลักฐานต้นฉบับไปจาก UI
- **ผลต่อด่าน S2:** ด่านของ S2 ตามไฟล์ไปหาเจ้าของ "ปัจจุบัน" (`ScanFileOwner` ข้อ 1) จึง**ไม่มีทางกันได้** เพราะผู้โจมตีเปลี่ยนเจ้าของได้เอง
- **ตัวขยายผล:** fallback แบบ relink-on-read ใน `FileAttachmentService.GetByEntityAsync` (`:144-173`) ย้ายไฟล์ใดก็ได้ที่ `scan.FileAttachmentId` ชี้ ไปเป็นของ `scan.CreatedDocumentId` โดยไม่ดูชนิดเดิมเช่นกัน
  - ถ้ารวมกับ S2-C1 (สแกนไฟล์เหยื่อแล้วกด create-document) ไฟล์ชนิดใดก็ถูกดึงเข้าเอกสารของผู้โจมตีได้
- **แนวแก้:**
  - link-document ต้องผ่าน `DenyScanAsync(scan, Write)` และสิทธิ์สร้างหรืออนุมัติใบปลายทาง
  - relink ทั้งสองจุดทำเฉพาะเมื่อไฟล์ยังเป็น `EntityType == "OcrScan"`
  - `linked-scan` ต้องผ่านด่านอ่านเอกสาร

### S2-C3 (P1): ด่านอ่านของ `GET ocr/{scanId}` ถูกข้ามได้ด้วย endpoint เขียนที่คืนผลอ่านเต็มใบ
ด่าน `DenyScanAsync` อยู่แค่ใน GetResult และ GetImage แต่ endpoint ข้างล่างนี้คืน `OcrResultResponse` เต็ม (รวม `RawTextContent`) หรือเนื้อหาบรรทัดของสแกนใดก็ได้ในบริษัท รวมสแกนที่ S2 ซ่อนไว้

| endpoint | บรรทัด | ข้อจำกัด | สิ่งที่รั่ว |
|---|---|---|---|
| `POST ocr/{scanId}/match-contact/{contactId}` | :599 → `OcrService.cs:8015-8048` | ไม่มีด่านสถานะ | ผลอ่านเต็มใบ และ**แก้** `MatchedContactId` ของสแกนที่ผูกเอกสารลับแล้วด้วย |
| `POST ocr/{scanId}/retry` | :250-290 | ได้ 1 ครั้งต่อใบตาม `OcrMaxRetriesPerScan` | ผลอ่านเต็มของสแกนใหม่ ซึ่งด่านจะซ่อนแถวนั้นในภายหลังได้ถูกต้อง แต่ response ของ retry หลุดไปแล้ว |
| `GET ocr/{scanId}/line-preview` | :491 | ทีมรู้แล้ว | บรรทัด คำอธิบาย และยอด |
| `GET ocr/{scanId}/stock-preview` | :659 | ทีมรู้แล้ว | บรรทัดพร้อมสินค้าที่จับคู่ |
| `GET ocr/{scanId}/open-pos` | :501 | ทีมรู้แล้ว | PO เปิดของผู้ขายรายนั้น พร้อมบรรทัดและยอด |
| `GET ocr/{scanId}/predecessor-candidates` | :527 | ทีมรู้แล้ว | เลขที่ ยอด และยอดค้างของเอกสารที่ "น่าจะเป็นใบต้นทาง" (ดู S2-P4) |

- endpoint เขียนของสแกนที่เหลือ (`lines-project`, `line-project`, `line-fields`, `modify-line`, `correct`, `register-asset`, `import-stock`, `reject-match`, `link-po`/`link-predecessor`) ก็มีแค่ `[Authorize]` ทั้งหมด
  - `link-po`/`link-predecessor`/`unlink` โยน error เมื่อสแกนผูกเอกสารแล้ว (`OcrService.cs:9643`, `:9880`) จึงไม่รั่วผลของใบที่ผูกแล้ว
  - แต่ endpoint ที่เหลือแก้ข้อมูลสแกนของใบที่ผู้ใช้มองไม่เห็นได้
- ข้อนี้ไม่ใช่บั๊กที่ S2 สร้าง แต่ทำให้คำอ้างใน `r193-S2.md` ("ไฟล์ชุดเดียวกัน…OCR 4 เส้น…เดินด่านเดียวแล้ว" และรายการ backlog ข้อ 2 ที่มีแค่ 4 เส้น) **ไม่ครบ**
- **แนวแก้:** ใส่ `DenyScanAsync(Read)` ให้เส้นอ่าน และ `DenyScanAsync(Write)` ให้เส้นเขียนทุกเส้นที่รับ `{scanId}` แล้วเพิ่มเข้า TARGETS

### S2-C4 (P2): `attachment_gate_check.py` ค้นทางเข้าใหม่ไม่เจอในรูปแบบที่มีอยู่จริง และยอมรับด่านที่เรียกด้วยอาร์กิวเมนต์ผิด
ลองแก้ในสำเนาที่ scratchpad (ไม่แตะเรพ):

| mutation | ผล | ประเมิน |
|---|---|---|
| ถอดประโยคด่านจาก `UploadReceipt` | ❌ ฟ้อง | ✓ |
| ย้ายด่านไปหลัง `UploadBytesAsync` | ❌ ฟ้อง (sink ก่อนด่าน) | ✓ |
| `if (deny is { } d) { Console.WriteLine(d.Message); }` ใช้ผลแต่ไม่ return | ✅ **เขียว** | false negative |
| เรียก `DenyAttachmentAsync(..., "Document", Guid.Empty, AttachmentAccess.Read, …)` แทนชนิด/ทิศที่ถูก | ✅ **เขียว** | false negative ทั้งที่ในทางปฏิบัติด่านนี้ผ่านเสมอ (`DenyDocAsync` doc==null + Read ⇒ null) |
| ห่อด่านไว้ใน `if (file.Length < 0) { … }` | ✅ **เขียว** | false negative |
| controller ใหม่ที่ใช้ field แบบ `IFileAttachmentService? _x` | ไม่ถูกค้นเจอ | regex `IFileAttachmentService\s+(\w+)` ไม่รับ `?` |
| controller ใหม่ที่อ่านไฟล์ผ่าน `_db.Set<FileAttachment>()` | ไม่ถูกค้นเจอ | ค้นแค่ `.FileAttachments` |
| controller ใหม่ที่เรียก `IOcrService.ScanAsync(companyId, attachmentId)` | ไม่ถูกค้นเจอ | ต้นเหตุที่ S2-C1 หลุด |

- ไม่พบการฟ้องผิดบนเรพจริง (รันที่ HEAD แล้วเขียว และ negative test ในตัวผ่าน)
- ข้อจำกัดข้างบนเป็นธรรมชาติของ checker ข้อความ (CLAUDE.md F4 ข้อ 1/3) ไม่ควรขยายให้ทำ type resolution
  - แต่ควร (1) ให้ discover ค้น `.Set<FileAttachment>` และ `IFileAttachmentService?`
  - (2) เพิ่ม `IOcrService` sink ที่รับ `fileAttachmentId`/`scanId` (`ScanAsync`, `DeleteScanAsync`, `LinkScanToExistingDocumentAsync`, `MatchContactAsync`, `Preview*`) เข้าเกณฑ์ discover
  - (3) บังคับว่าการใช้ผลต้องเป็น `if (deny …) return`

---

## PLAUSIBLE

**S2-P1: ใบเบิกช่วง `Submitted`: ถอดหลักฐานหลังผ่านด่าน §65 ทวิ ได้**
- ด่าน "ไม่มีใบเสร็จต้องแนบหลักฐาน" ตรวจเฉพาะตอน Submit (`ExpenseClaimService.cs:247-256`)
- `ExpenseClaimEvidencePolicy.OwnerMayChange(Submitted) = true` ผู้ยื่นจึงถอดไฟล์ได้หลังส่ง และทั้งผู้อนุมัติเว็บและมือถือ (`MobileApiService.cs:518+`) ไม่ตรวจซ้ำ
- ผลคือใบ no-receipt ถูกอนุมัติโดยไม่มีหลักฐานแม้แต่ไฟล์เดียวได้ รวมทั้งเกิด TOCTOU (สลับไฟล์ระหว่างที่ผู้อนุมัติกำลังดู)
- ข้อนี้เป็นการเลือกออกแบบของทีม (อนุญาต Submitted เพื่อให้เพิ่มหลักฐานได้) ข้อเสนอคือ Submitted ให้ **เพิ่ม** ได้แต่ **ถอด** ไม่ได้ หรือให้ Approve ตรวจจำนวนไฟล์ซ้ำ

**S2-P2: ผู้ยื่นที่ถือ `Expense.Approve` เอง** (Owner/Accountant ที่เบิกเอง) ยังแก้หลักฐานหลังอนุมัติได้โดยไม่ต้องให้เหตุผล ข้อนี้คือ backlog ข้อ 1 ของทีม จดไว้เพื่อให้รู้ว่าช่องนี้รวมกรณี "อนุมัติเอง แก้เอง" ด้วย

**S2-P3: สแกนที่ลงเป็น JE ตรง ๆ ไม่ใช้ด่านความลับของ JE**
- `ScanFileOwner` ดูแค่ `Document`/`CreatedDocumentId` ไม่ดู `CreatedJournalEntryId`
- ไฟล์ของสแกนที่กด "บันทึก JE" ยังเป็นชนิด `OcrScan` (ไม่ relink) ถ้า JE นั้นถูกตั้ง `Sensitivity` ภายหลัง รูปและผลอ่านจึงยังเปิดได้ระดับสมาชิก
- ขณะที่ไฟล์แนบชนิด `JournalEntry` ผ่านด่านความลับ (`AttachmentAccessGate.cs:115-127`) ตรงนี้คือสองประตูของข้อมูลชุดเดียวกัน แบบเดียวกับ C3 เดิม

**S2-P4: `predecessor-candidates` คืนเอกสารโดยไม่กรองฝั่งหรือชั้นความลับของผู้เรียก**
- service ไม่รับ userId (`OcrController.cs:527-530`) จึงคืน `DocumentNumber/TotalAmount/BalanceDue` ของใบที่ผู้ใช้เปิดในหน้าเอกสารไม่ได้
- ยังไม่ได้ไล่ว่าตัวหาใบต้นทางกรอง `Sensitivity` ในคิวรีหรือไม่

**S2-P5: รายการสแกนคง `TotalCount` แต่ตัดแถว**
- ผู้ใช้อนุมานจำนวนสแกนของใบลับได้ (pageSize ลบด้วยจำนวนแถวที่ได้)
- ใช้ pattern เดียวกับ `DocumentController.GetDocuments` ถือว่ารับได้ และ controller ไม่ส่ง `search` เข้า `GetResultsAsync` จึงไม่มี oracle จากการค้นหา
- `SummarizeAsync` ของ review-queue ก็นับรวมใบที่ซ่อนเช่นกัน
- ผลข้างเคียงด้าน UX: ถ้าตั้งสิทธิ์แยกฝั่งไว้ แท็บ "สร้างแล้ว" อาจเป็นหน้าว่างทั้งหน้าแต่ยังมีปุ่มหน้าถัดไป

**S2-P6: ถังก่อนบันทึก: ไม่มีใครเปิดหรือจัดการไฟล์ที่ไม่มีรายการชี้ได้**
- ผู้อัปโหลดที่ออกจากบริษัทแล้วถูก `TenantAccessMiddleware` ตัด ส่วน Owner ก็เปิดไม่ได้ (`UnsavedFileVisible` = ผู้อัปโหลดเท่านั้น และ `GET` ทั้งถังคืน 403 เสมอ)
- ไม่ใช่ช่องรั่ว (ทิศปลอดภัย) แต่ไม่มีเครื่องมือเก็บกวาดหรือตรวจ
- ในทางกลับกัน **การลบ**ไฟล์ในถังใช้แค่ `Tax.File` (`AttachmentAccessGate.cs:151-154` เป็นด่านเขียนที่ไม่ดูผู้อัปโหลด) ผู้มี Tax.File จึงถอดไฟล์ที่คนอื่นเพิ่งแนบก่อนกดบันทึกได้ ผลเสียหายต่ำ

**S2-P7: `AdoptUnsavedAttachmentAsync` ไม่ตรวจว่าผู้บันทึกคือผู้อัปโหลด**
- `WhtCreditController` ไม่มีด่านเขียน (backlog 4 ของทีม)
- ผู้ที่รู้ id ของไฟล์ในถังของคนอื่นจึงผูกไฟล์นั้นเข้ารายการของตัวเองได้ แล้วไฟล์กลายเป็นระดับสมาชิก (`KeyGated` Read ว่าง)
- ต้องเดา GUID และปิดทางเห็นถังแล้ว ความเสี่ยงจึงต่ำ

**S2-P8: ทิศเขียนของไฟล์ `OcrScan` ที่ผูกเอกสารแล้วเปลี่ยนเกณฑ์**
- เดิมใช้ "มีคีย์สร้างเอกสารตัวใดก็ได้" ตอนนี้ใช้ "สร้างหรืออนุมัติเอกสารชนิดนั้น" (`DenyDocAsync` Write)
- ผลคือผู้ที่มีแค่คีย์อนุมัติลบได้เพิ่มขึ้น และผู้ที่มีคีย์สร้างของอีกฝั่งลบไม่ได้แล้ว
- เกิดเฉพาะเคส relink พลาด (ไฟล์ยังเป็น `OcrScan` แต่สแกนชี้เอกสารที่ยังอยู่) ไม่ใช่ช่องรั่ว แต่ควรจดไว้ใน DOCUMENT_FLOW ว่าเป็นพฤติกรรมที่ตั้งใจ

---

## ตรวจแล้วไม่มีปัญหา

**ข้อ 2: OcrController 4 action**
- **ลำดับ:** `GetImage` เรียก `DenyScanAsync` ก่อนแตะ `FileAttachments` และก่อน `PhysicalFile` (`OcrController.cs:1398-1401`) ส่วน `GetResult` เรียกก่อน `GetResultAsync` ส่วน `GetResults` และ `ReviewQueue` ตัดแถวก่อนสร้าง response
- **ด่านที่ใช้:** ใบที่ผูกเอกสาร (ไฟล์ย้ายเป็น Document แล้ว หรือ `CreatedDocumentId` ยังมีชีวิต) ใช้ `DenyDocAsync` (ฝั่ง + ชั้นความลับ) ใบที่ยังไม่ผูกใช้คีย์ OCR ถูกต้องตามที่ออกแบบ
- **ความสอดคล้อง:** `DenyScanCoreAsync` กับ `HiddenScanIdsAsync` ตัดสินตรงกันทุกกรณี (เอกสารถูกลบ = ไม่มีด่าน · `CanViewAsync` ≡ `GetVisibleKindsAsync.Contains` ตาม `SensitivityService.cs:30-62`)
- **N+1:** ไม่มีแบบต่อแถว
  - `HiddenScanIdsAsync` คิวรี 3 ครั้งต่อหน้า บวก `VisibleDirectionsAsync` (2 คีย์) และ `GetVisibleKindsAsync` (4 kind × ≤3 คิวรี)
  - `GetImage`/`GetResult` คิวรีซ้ำสแกนและไฟล์หลังด่าน 1-2 ครั้ง (คงที่ ไม่ใช่ N+1)
- **ความถูกต้องของโค้ด:** ใช้ `PagedResponse` ตามลำดับพารามิเตอร์ `(Items, TotalCount, Page, PageSize, TotalPages)` ถูกต้อง และ `RankAsync` คืน `List<RankedScan>` จึง assign ผลของ `.ToList()` ได้

**ข้อ 3: `UploadReceipt`**
- ด่าน `DenyAttachmentAsync("StatutoryRemittance", Write)` มาก่อน `CopyToAsync`/`UploadBytesAsync` (`StatutoryRemittanceController.cs:133-150`)
- รายการต้องมีอยู่ในบริษัทนี้และยังไม่ถูกลบ (`AttachmentAccessGate.cs:255-256`) แล้วจึงตรวจ `Tax.File`
- ไบต์ตัดสินชนิดผ่าน `SniffAttachment` ชื่อที่เก็บใช้นามสกุลจากผลตรวจ Content-Type มาจาก `kind`
  - นามสกุลทุกตัวที่ sniff คืนได้อยู่ใน `AllowedExtensions` ของ `UploadBytesAsync` จึงไม่ตก `InvalidOperationException`
- `RequestSizeLimit(25MB)` เท่าเพดานของ service
- tenant: route companyId ผ่าน `TenantAccessMiddleware` บวกกรอง `CompanyId` ทั้งใน gate และ `AttachReceiptAsync`

**ข้อ 4: ใบเบิก (P1)**
- ล็อกหลัง `Approved/Paid/Rejected/Voided` สถานะที่ไม่รู้จักถือว่าล็อก
- ผู้ถือ `Expense.Approve`/`HR.Admin` ยังแนบได้ ส่วนการดูของผู้ยื่นไม่ถูกล็อก
- **ทางเข้าอื่น:** ไม่มีเลย
  - grep ทั้งเรพพบว่าแถว `EntityType="ExpenseClaim"` ถูกสร้างเฉพาะผ่าน `FileAttachmentController.Upload`
  - `ExpenseClaimService` แค่นับไฟล์ `MobileApiService` แค่อนุมัติ/ปฏิเสธ ส่วน LINE แนบเฉพาะ `OcrScan` และ Integration แนบเฉพาะ `Document`
  - entity `ExpenseClaim` ไม่มีช่อง URL/ใบเสร็จแยก

**ข้อ 5: ถัง WhtCredit และ migration**
- **Migration** (`DatabaseMigrationHelper.cs:5643-5653`)
  - idempotent: หลังรันแล้ว `EntityId` ไม่ว่าง จึงไม่ match ซ้ำ
  - กรอง tenant ด้วย `w."CompanyId" = f."CompanyId"` และอยู่หลัง `CREATE TABLE "WhtCreditsReceived"` (:5600) ในลิสต์เดียวกัน
- **ทุกเส้นบันทึกย้ายไฟล์:** Create (:246), Update (:269) และ MarkReceived (:292)
  - เส้น OCR (`OcrService.cs:3846`) ใช้ไฟล์ชนิด `OcrScan` ไม่เกี่ยวกับถัง
  - `Id` ถูกตั้งใน `BaseEntity` ก่อน Add จึงผูกได้ตั้งแต่ Create
- **หน้าเว็บ:** `wht-credit.html:358` โหมดแก้ไขอัปโหลดตรงเข้ารายการ และไม่มีหน้าไหนเรียก `GET attachments/WhtCredit/0000…`

**ข้อ 6: `CompetitorImportController`**
- ทั้ง `preview` และ `import` (รวม dryRun) เรียก `Detect` แล้วเรียก `DenyAsync(ForImport(EntityKind))` ก่อนแตะฐาน
- controller มีแค่ 2 action นี้
- EntityKind ของ adapter ทั้ง 3 ตัว ("Contacts"/"Products"/"ChartOfAccounts") map ถึงคีย์ใน `ForImport`
- ไม่มี fake ของ `ICompetitorImportCoordinator` ในเทสต์ (เพิ่มเมธอดใน interface แล้วไม่พัง)

**ข้อ 8: API key / X-Acting-User**
- gate ใช้ `NameIdentifier` เหมือน `RequirePermission` ทุกที่ คีย์ `acc_` จึงได้สิทธิ์ของผู้สร้างคีย์
- คีย์ `int_` ที่ไม่ส่ง X-Acting-User จะได้ userId = IntegrationId ⇒ `CanViewAsync` false (ไม่มี user) และ VisibleDirections = All
  - ผลคือเห็นเอกสารทั่วไป แต่เอกสารลับหรือสลิปเงินเดือนไม่เห็น ซึ่งถูกต้อง
- gate **ไม่ได้เปิดช่องใหม่** ให้ API key ส่วนการสวมเจ้าของเป็นเรื่องของ C1 เดิม (ทีม W)
- ข้อสังเกต: S2-C1/C2 ใช้ได้กับคีย์ `acc_` ที่มี `CanWrite` ด้วย เพราะ OcrController ไม่มีด่านสิทธิ์

**ข้อ 9: ความเสี่ยงคอมไพล์ (อ่านด้วยตา): ไม่พบ**
- **DI:** `Program.cs:430` register `AddScoped<IAttachmentAccessGate, AttachmentAccessGate>` แล้ว และ `di_cycle_check` ผ่าน (193 service / 219 เส้น ไม่มีวง)
- **controller:** `[FromServices]` ใน action ใช้ได้ `FileAttachmentController` ไม่เหลือการใช้ `_permissions`/`_sensitivity` และไม่มีเทสต์ที่ `new` controller ตัวใดที่เปลี่ยน constructor
- **ชนิดและสมาชิก:**
  - `AttachmentAccess`/`AttachmentDenial` ย้ายไป `Accounting.Helpers` และทุกไฟล์ที่ใช้มี `using Accounting.Helpers`
  - `GetVisibleKindsAsync` คืน `HashSet<>` ส่งเข้า `IReadOnlySet<>` ได้ `DocumentVisibility` เป็น record struct ที่มี `Allows`
  - `StatutoryRemittance.IsDeleted` มีอยู่ (BaseEntity) `UnsupportedUploadException(string)` มีอยู่ และ ternary tuple `? found : null` target-typed ได้ใน C# 9+
  - enum `AttachmentOwnerKind.OcrScan` ไม่มี switch ที่ต้องครบทุกค่าที่อื่น
- **checker อื่น:** `write_permission_gate_check` และ `attachment_gate_check` เขียวที่ HEAD

---

## ตาราง: ข้อ 1: ด่าน U2 (f04c145, private ใน controller) เทียบกับ S2 (`AttachmentAccessGate`)

| ชนิด | อ่าน: U2 → S2 | เขียน (แนบ/ลบ): U2 → S2 | ทิศ |
|---|---|---|---|
| ไม่รู้จัก | ผ่าน → ผ่าน | 403 → 403 | เท่าเดิม |
| Document / Expense | ความลับ + ฝั่ง → เหมือนเดิม | ความลับ + สร้าง/อนุมัติ → เหมือนเดิม | เท่าเดิม |
| Payment | ตามเอกสารที่ถูกชำระ → เหมือนเดิม | → เหมือนเดิม (404 ถ้าไม่พบ) | เท่าเดิม |
| ExpenseClaim (ผู้ยื่น) | ผ่าน → ผ่าน | **ผ่านทุกสถานะ → ผ่านเฉพาะ Draft/Submitted** หลังจากนั้นต้องมีคีย์ผู้ตรวจ | เข้มขึ้น (ตั้งใจ · P1) |
| ExpenseClaim (คนอื่น) | คีย์ผู้ตรวจ → เหมือนเดิม | คีย์ผู้ตรวจ → เหมือนเดิม | เท่าเดิม |
| PayrollRun | ความลับ Payroll → เหมือนเดิม | + มีจริง + Payroll.Run → เหมือนเดิม | เท่าเดิม |
| JournalEntry | ความลับ JE → เหมือนเดิม | + มีจริง + Journal.Manage → เหมือนเดิม | เท่าเดิม |
| Contact / Product / Project / FixedAsset / Subscription* / Lodging / SiteOrder | คีย์อ่าน (ส่วนใหญ่ว่าง) → เหมือนเดิม | มีจริง + คีย์เขียน → เหมือนเดิม | เท่าเดิม |
| WhtCredit (มีรายการ) | ว่าง = สมาชิก → เหมือนเดิม | มีจริง + Tax.File → เหมือนเดิม | เท่าเดิม |
| WhtCredit (ถังว่าง) | **ทุกคน → รายการทั้งถัง 403 · ดาวน์โหลดเฉพาะผู้อัปโหลด** | Tax.File → Tax.File (ไม่ดูผู้อัปโหลด — S2-P6) | เข้มขึ้น (ตั้งใจ · P7) · Owner ก็เปิดไม่ได้ |
| StatutoryRemittance | ว่าง → ว่าง | **Tax.File (ไม่ตรวจว่ามีอยู่) → มีอยู่ + ไม่ถูกลบ + Tax.File** | เข้มขึ้น (ตั้งใจ · C2) · ลบใบเสร็จของรายการที่ถูกลบไม่ได้แล้ว (ทิศปลอดภัย) |
| OcrScan (ยังไม่ผูก / ไม่พบสแกน) | ผ่าน → ผ่าน | คีย์สร้างเอกสารใดก็ได้ → เหมือนเดิม | เท่าเดิม |
| OcrScan (ผูกเอกสารที่ยังอยู่) | **ผ่าน → ด่านเอกสาร (ฝั่ง + ความลับ)** | **คีย์สร้างใดก็ได้ → สร้าง/อนุมัติชนิดนั้น** | อ่านเข้มขึ้น (ตั้งใจ · C3) · เขียนเปลี่ยนเกณฑ์ (S2-P8) |

**สรุปข้อ 1:**
- ตรรกะของ U2 ย้ายมาครบ ทุกแถวที่ไม่ได้ตั้งใจแก้ให้ผลเท่าเดิม
- **ไม่มีชนิดไหนหลวมลงจากการย้ายเอง** (ยกเว้นทิศเขียนของ OcrScan ในเคส relink พลาด ตามข้อ S2-P8)
- ทิศเข้มทุกแถวเป็นแถวที่ตั้งใจ และไม่มีแถวที่ล็อก Staff/Accountant ออกจากไฟล์ที่เคยเปิดได้โดยไม่ตั้งใจ
  - ผู้ใช้ที่ยังไม่ได้ตั้งสิทธิ์แยกฝั่งได้ `DocumentVisibility.All` เหมือนลิสต์เอกสาร
  - คนที่ถูกกันเพิ่มคือผู้ที่ไม่เห็นฝั่ง/ชั้นของใบอยู่แล้ว, ผู้ยื่นใบเบิกหลังอนุมัติ และผู้ที่ไม่ใช่ผู้อัปโหลดไฟล์ในถัง WhtCredit
- **แต่ด่านใหม่ถูกข้ามได้ทั้งหมดผ่าน S2-C1/S2-C2/S2-C3** เพราะเส้น OCR อื่นยังเปลี่ยน "เจ้าของไฟล์" หรือคืนเนื้อหาของสแกนโดยไม่เดินด่าน
