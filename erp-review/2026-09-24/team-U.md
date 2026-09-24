# ทีม U — UX: คิวรอตรวจ (OCR) · ตัวแสดง "กำลังทำงาน" กลาง · แนบไฟล์ทีหลังบนเอกสารที่อนุมัติแล้ว

รอบ 190 (2026-09-24) · รับข้อ **1, 4, 7** ของเจ้าของ · worktree `agent-a3c623e3843eda915`
⚠️ **ยังไม่ได้คอมไพล์** (เครื่องนี้ไม่มี .NET SDK) — CI คือคอมไพเลอร์ตัวแรก

---

## ข้อ 1 — Review Queue (`/pages/review-queue.html`)

### มีไว้ทำอะไรจริง (ไล่จากโค้ด)
- หน้า → `GET /api/companies/{cid}/ocr/review-queue` (`OcrController.ReviewQueue`) → `ActiveLearningRanker.RankAsync`
- คิว = สแกนที่ **อ่านเสร็จแล้ว (Completed) ภายใน 30 วัน** และ **ยังไม่มีผลลัพธ์ทางบัญชี** (ยังไม่สร้างเอกสาร)
  หรือมีบรรทัดที่อาจเป็นสินทรัพย์ถาวรรอตัดสิน — เรียงให้ใบที่ "คนแก้แล้วระบบได้เรียนมากสุด" ขึ้นก่อน
  (ความไม่มั่นใจ × ผู้ขายใหม่ × ความสด × ธงเสริม) · กดแถว → `document-scan.html?reviewScan=<id>` → เปิด modal ตรวจของสแกนนั้น
  (`openReview` ดึงด้วย id ตรง ๆ ไม่ต้องอยู่ในลิสต์หน้าแรก — ใช้ได้จริง)
- ต่อสายเข้าเมนูแล้วตั้งแต่ T4-14 (`layout.js` เมนู `ocr-review-queue` ใต้ "เครื่องมือ: AI · OCR")

### สาเหตุที่พบ (file:line ของ HEAD `75292cf`)
| # | อาการ | สาเหตุ |
|---|---|---|
| U1-a | **แถบเมนูด้านข้างหาย** | หน้าไม่มี `<div id="pageContent">` (ใช้ `<div id="layoutRoot">` + `.page-container` — `review-queue.html:76`) ⇒ `Layout.render()` เจอ `if (!pageContent) { console.warn; return; }` (`layout.js` ~1571) แล้ว**ไม่สร้าง sidebar/header เลย** · กวาดทั้งเรพ: หน้าอื่นที่ไม่มี `pageContent` มี 8 หน้า (admin-help · dashboard · delivery-sign · design-system · line-bind · mobile-receipt · quick-sale · quotation-accept) ส่วนใหญ่ตั้งใจเป็นหน้าเดี่ยว — **ไม่ได้แตะ** |
| U1-b | เมนูไม่ไฮไลต์ + ด่านแพ็กเกจไม่ตรวจ | `Layout.init('review-queue')` (`:112`) แต่ id ในเมนูคือ `ocr-review-queue` ⇒ `_enforcePageAccess` หาเมนูไม่เจอ = ข้ามด่าน `AI_Features` |
| U1-c | **หน้าว่าง บอกว่า "ระบบมั่นใจกับทุก scan"** | ข้อความ hardcode (`:133`) ไม่เคยตรวจอะไรเลย — คิวว่างจริงส่วนใหญ่เพราะ (ก) สแกนถูกสร้างเป็นเอกสารแล้ว (ข) เก่ากว่า 30 วัน (ค) อ่านไม่สำเร็จ — ไม่ใช่เพราะระบบมั่นใจ · endpoint ไม่ส่งตัวเลขอะไรให้หน้าอธิบายได้ |
| U1-d | **แก้เสร็จแล้วไม่หายจากคิว** (เส้นบันทึกเป็น JE) | `ActiveLearningRanker.cs:69` กรอง `CreatedDocumentId == null` อย่างเดียว — `OcrService.CreateJournalEntryFromScanAsync` (`:7122`) ตั้ง `CreatedJournalEntryId` แต่ไม่ตั้ง `CreatedDocumentId` ⇒ สแกนที่ลงบัญชีแล้วค้างในคิว 30 วัน |
| U1-e | ข้อความหน้าเป็นอังกฤษ | หัว "Review Queue" · เหตุผล `uncertain (40%)` / `new vendor` / `stale` / `asset alert` สร้างใน ranker (`:113-117`) · ป้าย `💼 Asset` |
| U1-f | ธง "ลายมือ" ไม่มีผล | `Intelligence.cs:410-413` เขียนว่าลายมือ "docks the grade so the review queue surfaces it first" แต่ ranker ไม่อ่าน `HasHandwriting` (มี ≠ ถูกเรียก) |
| U1-g | หน้าใช้ `fetch()` ดิบ | `:123` ข้าม `API.request` ⇒ ไม่มี 401/403 handling กลาง และจะไม่ได้ตัวแสดงข้อ 4 |

**ว่างเพราะ query/field ไม่ตรงไหม?** — เปิด DTO แล้ว: ชื่อฟิลด์ที่ JS อ่าน (`scanId` `vendorName` `originalFileName` `processedAt`
`priorityScore` `hasPotentialFixedAsset` `reasonHint` `quality.{letter,score,color}`) **ตรงกับ anonymous object ของ endpoint ทุกตัว**
(camelCase) · ความมั่นใจเก็บ 0–1 (`document-scan.html:2174` คูณ 100) สูตรจึงถูกหน่วย ⇒ ว่าง = ไม่มีของที่เข้าเงื่อนไขจริง ไม่ใช่ field ผิด
แต่ผู้ใช้ไม่มีทางรู้ (U1-c)

### สิ่งที่แก้ (เพิ่มวิธีคิด ไม่รื้อ)
- **`Helpers/OcrReviewQueuePriority.cs` (ใหม่)** — ย้ายสูตรออกมาเป็น pure helper · ใบที่ไม่มีธงใหม่ได้คะแนน**เท่าสูตรเดิมทุกประการ**
  (ล็อกด้วยเทสต์) · เพิ่ม: ลายมือ ×1.3 (ดันขึ้น) · ผู้ใช้แก้แล้ว ×0.5 (บทเรียนเก็บแล้ว เหลือแค่สร้างเอกสาร — ยังอยู่ในคิว) ·
  เหตุผลเป็นไทยทั้งหมด ("ระบบไม่มั่นใจ 40%" · "ผู้ขายใหม่" · "ยังจับคู่ผู้ติดต่อไม่ได้" · "อาจเป็นสินทรัพย์ถาวร — รอตัดสิน" ·
  "มีลายมือเขียน" · "อาจซ้ำกับสแกนเดิม" · "แก้ข้อมูลแล้ว — รอสร้างเอกสาร" · "สแกนเกิน 7 วันแล้ว")
- `ActiveLearningRanker` — เรียก helper · ตัดสแกนที่มี `CreatedJournalEntryId` ออกจากคิว (U1-d) · เพิ่ม `SummarizeAsync` นับ
  สแกนใน 30 วัน / สร้างเอกสารแล้ว / ในนั้นยังเป็นร่าง / บันทึกเป็น JE / อ่านไม่สำเร็จ / กำลังอ่าน / ค้างเกิน 30 วัน / อยู่ในคิว
  (เงื่อนไข "อยู่ในคิว" ตรงกับ `RankAsync` ตัวต่อตัว)
- `OcrController.ReviewQueue` — คำตอบเปลี่ยนจาก array เป็น `{ items, summary }` (ผู้เรียกมีหน้าเดียว · หน้ารับได้ทั้งสองรูป)
- `review-queue.html` — โครงมาตรฐาน `#pageContent` + `page-header` (sidebar กลับมา) · `Layout.init('ocr-review-queue')` ·
  ข้อความไทยทั้งหน้า + วิธีใช้ ("แก้ → สร้างเอกสาร → ออกจากคิวเอง") · `API.get` แทน `fetch` · **คิวว่างแสดงเหตุผลจากตัวเลขจริง**
  พร้อมลิงก์ไปที่ที่ทำต่อได้ (ร่างรออนุมัติ → หน้าเอกสารฝั่งรายจ่าย · อ่านไม่สำเร็จ/ค้างเกิน 30 วัน → หน้าสแกน) ·
  summary `undefined` ⇒ บอกว่า "ไม่มีข้อมูลสรุป" (ไม่แสดง 0) · แถวเป็น `<a href>` (เปิดแท็บใหม่ได้) · สีเกรดกรองเป็น `#hex` ก่อนใส่ style

---

## ข้อ 4 — ตัวแสดง "กำลังทำงาน" กลาง (`wwwroot/js/api.js`)

### สาเหตุ
ทั้งเว็บไม่มีตัวแสดงสถานะกลาง — `API.request` (`api.js:115` เดิม) ยิง `fetch` แล้วรอเงียบ ๆ · แต่ละหน้าต่างคนต่างทำ (บางหน้า
`btn.disabled=true; textContent='กำลังบันทึก…'` · ส่วนใหญ่ไม่ทำ) ⇒ ปุ่มกดซ้ำได้ + ผู้ใช้ไม่รู้ว่าระบบทำงานอยู่

### กลไก (ที่เดียว — ทุกหน้าที่ใช้ `API.*` ได้อัตโนมัติ 122/128 หน้า)
`const ApiBusy` ใน `api.js` + `API.request` ห่อ `_requestCore` ด้วย `begin` / `finally end`:
1. คำขอค้าง ≥ **400ms** → แถบบนสุด (เส้นวิ่ง 3px + ป้ายกลางจอ `role=status aria-live`) บอกว่ากำลังทำอะไร
   (อ่านเอกสาร OCR · อนุมัติ · อนุมัติเป็นชุด · ยกเลิก · ประมวลผลเป็นชุด · สร้างไฟล์ · อัปโหลดไฟล์แนบ · บันทึก · ลบ · โหลดข้อมูล)
   — คำขอสั้นกว่านั้น **ไม่มีอะไรโผล่** (ไม่กระพริบ) · คำสั่งเขียนสำคัญกว่าการโหลดตอนเลือกป้าย
2. คำขอซ้อน → ซ่อนเมื่อหมดทุกตัว · ซ่อนหลัง 1 tick เพื่อไม่ดับ-ติดระหว่างคำสั่งที่ต่อกันใน await เดียว (บันทึก → อนุมัติ)
3. คำสั่งเขียน (POST/PUT/PATCH/DELETE) ที่มาจาก**การคลิกปุ่ม** (capture listener จำปุ่มล่าสุด ≤15 วินาที — เผื่อ `confirm()`;
   กด Enter/พิมพ์ = ล้างปุ่มที่จำไว้) → กันคลิกซ้ำ**ทันที**แบบมองไม่เห็น (`data-api-busy` + กลืนคลิกใน capture) · เกิน 400ms
   → disable + `aria-busy` + "⟳ กำลังดำเนินการ…" (ล็อกความกว้างปุ่มกันเลย์เอาต์กระโดด) · จบ → คืนข้อความ/สถานะเดิม
   · **ปุ่มที่หน้าคุมเอง (disable ไว้ก่อนยิง) ไม่แตะ** · หน้าเขียนข้อความผลลัพธ์ลงปุ่มเอง ("✓ ส่งแล้ว") **ข้อความของหน้าชนะ**
4. เกิน 8 วินาที → "…ยังทำงานอยู่ (N วินาที) — กรุณาอย่าปิดหรือเปลี่ยนหน้า" (นับทุกวินาที) · ระหว่างคำสั่งที่ผู้ใช้กดค้าง
   ≥400ms `beforeunload` ให้เบราว์เซอร์ถามก่อนปิด/เปลี่ยนหน้า (ตรงกับ "ผู้ใช้จะไปกดเมนูอื่นต่อ")
5. ล้ม / HTTP 4xx-5xx / 401 redirect → ซ่อนเสมอ (`finally`)
6. งานเบื้องหลัง → `API.quietly(() => API.get(...))` · ใช้กับ badge แจ้งเตือนใน `layout.js` แล้ว (ผู้ใช้ไม่ได้สั่ง)
- ป้ายใช้ `textContent` (ไม่มีทาง inject HTML) · สไตล์ฉีดครั้งเดียวจาก JS (`style-src 'unsafe-inline'` อนุญาตอยู่แล้ว)

### หน้าที่ใช้ `fetch()` ดิบ (ไม่ได้ตัวแสดงนี้ — **ไม่ได้แก้รอบนี้** ตามโจทย์)
58 ไฟล์มี `fetch(` นอก `api.js` · ส่วนใหญ่เป็นดาวน์โหลด blob (PDF/XML/ไฟล์แนบ) ที่ต้องใช้ fetch เอง · จุดที่ควรย้ายมา `API.*`
เรียงตามจำนวน: `documents.html` 29 · `sme-config.html` 19 · `cash-management.html` 16 · `payroll.html` 11 · `settings.html` 9 ·
`pos.html`/`pos-reservations.html`/`pos-floorplan.html`/`email-schedule.html` 6 · `products`/`contacts`/`account-subscription` 5 ·
`document-scan.html` 4 (รวม upload) · อีก ~45 ไฟล์ 1–4 จุด · (`review-queue.html` ย้ายแล้วในรอบนี้)
**ข้อเสนอ**: เพิ่ม `API.blob(url)` ที่เดินผ่าน `ApiBusy` แล้วไล่แทนเป็นรอบแยก

---

## ข้อ 7 — แนบไฟล์ทีหลังบนเอกสารที่อนุมัติแล้ว

### สาเหตุ (ยืนยันจากโค้ด — ไม่ใช่ด่านสถานะ ไม่ใช่ endpoint ปฏิเสธ)
`documents.html:9549` (HEAD) — `document.getElementById('detailContent').innerHTML += extraSections;`
`innerHTML +=` แปลง DOM ทั้ง modal เป็นข้อความแล้ว parse ใหม่ ⇒ **listener ทุกตัวที่ `renderAttachmentSection` ผูกไว้ด้วย
`addEventListener` (`:11200` click · dragover · drop · change · toggle) หายเงียบ** — กล่องยังอยู่ หน้าตาเหมือนเดิม แต่คลิก/ลากไม่มีผล
- ทำไม**เฉพาะใบที่อนุมัติแล้ว**: `extraSections` ไม่ว่างเมื่อใบมี **JE** (อนุมัติแล้ว) หรือการชำระ หรือบรรทัดผังสินทรัพย์ ·
  ใบร่างไม่มีสิ่งเหล่านี้ ⇒ ไม่ถึงบรรทัดนี้ ⇒ ใช้ได้ปกติ · และเป็น race: `getAttachments` (1 คำขอ) เสร็จก่อน
  `Promise.all([payments, journals])` เกือบทุกครั้ง ⇒ ผูก listener แล้วโดนล้าง
- ผลข้างเคียงเดียวกัน: รูปย่อไฟล์แนบไม่โหลดตอนกาง (listener `toggle` หาย) — ปุ่มดู/ดาวน์โหลด/ลบรอดเพราะเป็น `onclick` attribute
- ฝั่งเซิร์ฟเวอร์: `FileAttachmentController.Upload` **ไม่ดูสถานะเอกสารเลย** (อนุญาตทุกสถานะอยู่แล้ว) · เปิดโค้ดแล้ว**ไม่พบเหตุผล
  ทางกฎหมายที่ต้องห้ามแนบหลังอนุมัติ** — การแนบหลักฐานไม่แตะเลขที่/ยอด/JE (§86/4 "ห้ามแก้ไขย้อนหลัง" ครอบเนื้อใบ ไม่ใช่หลักฐานประกอบ)

### สิ่งที่แก้
1. `innerHTML +=` → `insertAdjacentHTML('beforeend', …)` (ไม่ re-parse ของเดิม) — แก้ที่ราก
2. กล่องไฟล์แนบผูก handler เป็น **attribute** (`onclick`/`ondragover`/`ondragleave`/`ondrop`/`onchange`/`ontoggle`) ผ่านเมธอด
   `Page.attPick/attDragOver/attDragLeave/attDrop/attPicked/attToggled` — attribute รอดการ re-parse ⇒ ถ้าวันหนึ่งมี `innerHTML +=`
   กลับมาอีก กล่องนี้ก็ยังใช้ได้ · เพิ่ม `role=button tabindex=0` + Enter/Space เปิดตัวเลือกไฟล์ · ลากสิ่งที่ไม่ใช่ไฟล์มาวาง →
   toast บอก (ไม่เงียบ) · ข้อความใต้กล่อง: "แนบเพิ่มได้ทุกสถานะเอกสาร (ไม่แก้เลขที่ ยอดเงิน หรือรายการบัญชี)"
3. **ด่านสิทธิ์ + tenant** (`FileAttachmentController.DenyDocAsync`): `entityType = "Document"` → เอกสารต้องมีอยู่ในบริษัทนี้ (ไม่งั้น 404)
   + ผู้ใช้ต้องมีสิทธิ์**สร้างหรืออนุมัติ**เอกสารประเภทนั้น (`DocumentPermissionHelper`) ไม่งั้น 403 ข้อความไทย · ใช้ทั้ง Upload และ
   **Delete** (เดิมสมาชิกคนไหนก็ลบหลักฐานของใบที่อนุมัติแล้วได้) · ไม่ดูสถานะเอกสาร · เพิ่ม controller เข้า
   `tools/write_permission_gate_check.py` WATCHED (negative test: ถอดด่านใน Delete แล้ว checker ฟ้อง `Delete` บรรทัด 159)
4. **magic bytes**: `UploadFileType.SniffAttachment(head, clientFileName)` (ใหม่) — ไบต์ตัดสิน · ชื่อไฟล์ใช้แยกชนิดย่อยเฉพาะเมื่อไบต์
   ยืนยันตระกูลแล้ว (ZIP→docx/xlsx/zip · OLE2→doc/xls) · csv/txt ต้องไม่มีไบต์ 0 และไม่ขึ้นต้นด้วย `<` · ไม่รับ ICO/OLE อื่น ·
   controller เซฟด้วยนามสกุลจากผลตรวจ + เก็บ Content-Type ที่ระบบสรุปเอง (เดิมเก็บค่าที่ client บอกแล้วตอบกลับตอนดาวน์โหลด) ·
   ชื่อที่ผู้ใช้เห็น = ชื่อเดิม + นามสกุลจริง · ไม่ผ่าน → `UnsupportedUploadException` (400 ข้อความไทยบอกชนิดที่รับ) ·
   `Sniff()` ของเส้น CMS/โลโก้**ไม่ถูกแตะ** (ยังไม่รับ ZIP/ข้อความ — ล็อกด้วยเทสต์) · `FileAttachmentService` รับ `.tif/.tiff` เพิ่ม
   (TIFF ที่ decoder อ่านไม่ได้จะถูกเซฟดิบเป็น .tif — เดิมจะถูก service ปฏิเสธทีหลังทิ้งไฟล์ค้างบนดิสก์)

---

## เทสต์ / sim ที่เพิ่ม
| ไฟล์ | ล็อกอะไร |
|---|---|
| `tools/api_busy_indicator_sim.js` (ใหม่ · check_all กวาดรันเอง) | รัน `ApiBusy` + `API.request` **จริง** จาก api.js ด้วยนาฬิกาปลอม 14 ชุด / 37 ข้อ: สั้น=ไม่โผล่ · นาน=โผล่ที่ 401ms หายเมื่อจบ · ล้มเครือข่าย/500/401=หาย · ซ้อน=หายเมื่อหมด · ปุ่ม: กันคลิกซ้ำ · disable+ข้อความหลัง 400ms · คืนสภาพ · สั้น=ไม่กระพริบ · หน้าคุมเอง=ไม่แตะ · ข้อความหน้าชนะ · คำสั่งต่อกันไม่มีช่องกดซ้ำและแถบไม่กระพริบ · OCR นาน=บอกเวลา · quietly · beforeunload สองทิศ · **negative test ในตัว**: ใส่บั๊กกลับ 4 แบบ (ลบ `end` ใน finally · หน่วง 0 · ไม่คืนข้อความปุ่ม · ไม่กันคลิกซ้ำ) แล้ว sim ต้องล้มทุกแบบ ✓ |
| `Accounting.Tests/OcrReviewQueuePriorityTests.cs` (ใหม่ · 7 Fact + 1 Theory/4) | ครึ่งที่ 1: ใบไม่มีธงใหม่ = สูตรเดิม (0.80 · 0.75 · 0.375 · 0) + asset ×1.3 + ไม่จับคู่ = ผู้ขายใหม่ · ครึ่งที่ 2: ลายมือดันขึ้น · แก้แล้วเลื่อนลงแต่ยังอยู่ · เหตุผลไทยไม่มีอังกฤษเดิม · ใบที่มั่นใจ+ผู้ขายคุ้นไม่มีเหตุผลรก · confidence ผิดหน่วยถูกบีบ |
| `Accounting.Tests/UploadFileTypeAttachmentTests.cs` (ใหม่ · 13 Fact + 4 Theory/13) | ผ่าน: PDF · PNG · xlsx/docx/zip · xls/doc · CSV+BOM · RAR · 7z · ปฏิเสธ: HTML/SVG/XML ในชื่อ .csv/.txt · ข้อความในชื่อ .pdf · csv ในชื่อ .html · ไบนารีในชื่อ .txt · OLE อื่น · ICO · ว่าง · ZIP ชื่อ .exe → เก็บ .zip · เส้นเดิม `Sniff` ไม่เปลี่ยน · MIME ต่อนามสกุล |

`bash tools/check_all.sh`: checker 43 ✅ · simulation 4 ✅ · node/brace/U+FFFD 14 ไฟล์ ✅ ·
❌ **`test_inventory --check` เท่านั้น** — เพราะห้ามแตะ `TEST_PLAN.md` ในรอบนี้ (ดูข้างล่าง) · ไม่มี dotnet = ยังไม่คอมไพล์

## ไฟล์ที่แตะ
`Accounting/Helpers/OcrReviewQueuePriority.cs` (ใหม่) · `Accounting/Helpers/UploadFileType.cs` ·
`Accounting/Services/Implementations/Ocr/ActiveLearningRanker.cs` · `Accounting/Controllers/OcrController.cs` ·
`Accounting/Controllers/FileAttachmentController.cs` · `Accounting/Services/Implementations/FileAttachmentService.cs` ·
`Accounting/wwwroot/js/api.js` · `Accounting/wwwroot/js/layout.js` · `Accounting/wwwroot/pages/review-queue.html` ·
`Accounting/wwwroot/pages/documents.html` · `tools/api_busy_indicator_sim.js` (ใหม่) · `tools/write_permission_gate_check.py` ·
`Accounting.Tests/OcrReviewQueuePriorityTests.cs` (ใหม่) · `Accounting.Tests/UploadFileTypeAttachmentTests.cs` (ใหม่)

## doc ที่ main agent ต้องขยับตอน merge (ทีมห้ามแตะ)
- **TEST_PLAN.md §0** — วางแถว `python3 tools/test_inventory.py --row` ทับ (ณ worktree นี้: **260 ไฟล์ · 2,113 Fact + 325 Theory (1,469 InlineData)** —
  ถ้าทีมอื่นเพิ่มเทสต์ด้วย ให้รัน `--row` ใหม่หลัง merge) · เพิ่มเคส: RQ-01 เมนูข้างขึ้นในหน้าคิว · RQ-02 คิวว่างบอกเหตุผลเป็นตัวเลข ·
  RQ-03 สแกนที่บันทึกเป็น JE หายจากคิว · BUSY-01..05 (สั้นไม่กระพริบ · นานโชว์ · ปุ่มกันซ้ำ · ล้มไม่ค้าง · ปิดหน้าระหว่างอนุมัติถูกถาม) ·
  ATT-07 แนบไฟล์บนใบที่อนุมัติแล้ว (คลิก + ลาก) · ATT-08 HTML ชื่อ .csv ถูกปฏิเสธ · ATT-09 สมาชิกไม่มีสิทธิ์เอกสาร → 403 แนบ/ลบ
- **TEST_PLAN.md ADM-U08 ไม่ตรงโค้ด** (พบระหว่างตรวจ — ไม่ได้แก้): เขียนว่า "อัปโหลดไฟล์แนบเมื่อเกินเพดานพื้นที่ต้องถูกบล็อก"
  แต่ `CanFitStorageAsync` มีผู้เรียกแค่ `CmsContentService.cs:574` — **เส้นไฟล์แนบเอกสารไม่เคยเช็คเพดาน** (มี ≠ ถูกเรียก)
- **DOCUMENT_FLOW.md** — ส่วนไฟล์แนบ/ทางออก: "แนบ/ลบไฟล์หลักฐานได้ทุกสถานะ · ต้องมีสิทธิ์สร้างหรืออนุมัติประเภทเอกสารนั้น · ชนิดไฟล์ตัดสินจากไบต์ (`UploadFileType.SniffAttachment`)"
- **OCR_PIPELINE_REVIEW_2026-09-06.md T4-14** — เติมสถานะ: หน้าได้ layout มาตรฐาน · เหตุผลไทย · สรุปเหตุคิวว่าง · ตัด JE-only ออกจากคิว
- **CHANGELOG.md** — รอบ 190 ทีม U (สามข้อด้านบน)
- `docs/lessons/` (frontend) — defect class ใหม่: **"`innerHTML +=` บน container ที่ลูกผูก listener แบบ async = ปุ่มตายเงียบ"**
  (ใน 24 จุดของ `innerHTML +=` ทั้ง wwwroot มีจุดนี้จุดเดียวที่อันตราย — ที่เหลือเติม `<option>` หรือข้อความ · ไม่ได้ทำ checker
  เพราะกติกาที่แยก 23 จุดที่ปลอดภัยออกต้องรู้ว่า container มีลูกที่ผูก listener ไหม = ฟ้องผิดแน่ (F2 ข้อ 6))

## คำถามเจ้าของ (ห้ามเดาแทน)
1. **ลบไฟล์แนบของเอกสารที่อนุมัติแล้ว** — วันนี้ลบได้ (ตอนนี้ต้องมีสิทธิ์สร้าง/อนุมัติ) และ `FileAttachmentService.DeleteAsync` **ลบไฟล์จริงออกจากดิสก์**
   ทั้งที่หลักฐานประกอบรายการบัญชีต้องเก็บ 5 ปี (พ.ร.บ.การบัญชี ม.10) — เลือก: (ก) ห้ามลบเมื่อเอกสารไม่ใช่ร่าง (ข) ลบได้แต่เก็บไฟล์จริง
   (soft-delete อย่างเดียว + ป้าย "ถูกถอด") (ค) คงเดิม
2. **ด่านสิทธิ์ของไฟล์แนบชนิดอื่น** (Contact · Payment · JournalEntry · FixedAsset · Expense · ExpenseClaim · Product · Project · PayrollRun)
   — รอบนี้ครอบเฉพาะ Document · ExpenseClaim ต้องให้พนักงานแนบของตัวเองได้ · PayrollRun ควรต้อง `Payroll.Run` ไหม — ต้องการตารางคีย์ต่อชนิด
3. **เพดานพื้นที่** — ให้ไฟล์แนบเอกสารเช็ค `CanFitStorageAsync` ไหม (ถ้าเช็ค: ผู้ใช้ที่เกินเพดานจะแนบหลักฐานไม่ได้ — ต้องมีทางไปต่อ)
4. **คิวรอตรวจควรรวม "เอกสารร่างที่สร้างจากสแกนแต่ยังไม่อนุมัติ" ไหม** — วันนี้ออกจากคิวทันทีที่สร้างร่าง (หน้าบอกจำนวนร่างและลิงก์ไปหน้าเอกสาร)
   · ถ้ารวม ต้องตัดสินว่ากดแถวแล้วไปหน้าไหน (modal สแกน หรือ ฟอร์มเอกสาร)
5. **ช่วง 30 วัน** ของคิว — สแกนเก่ากว่านั้นที่ยังไม่ได้สร้างเอกสารหายจากคิว (ตอนนี้หน้าบอกจำนวนแล้ว) — ขยาย/ตัดตามรอบปิดงวดแทนไหม
