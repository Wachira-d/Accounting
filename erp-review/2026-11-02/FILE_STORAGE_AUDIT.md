# FILE_STORAGE_AUDIT — ที่เก็บไฟล์กลาง (Local | S3-compatible) · ผู้เขียน/ผู้อ่านไฟล์ทุกจุด

> คำสั่งเจ้าของ (2026-11-02): ตรวจทุกจุดที่เขียน/อ่านไฟล์บนดิสก์ของเครื่อง → ผ่าน abstraction ตัวเดียว
> (CLAUDE.md กฎเหล็ก #4 D "ไฟล์ห้ามเขียน local disk ตรง — ผ่าน abstraction กลาง") · ผู้ให้บริการ LocalDisk (dev/ค่าเริ่มต้น)
> และ S3-compatible (AWS S3 · Cloudflare R2 · MinIO · DigitalOcean Spaces) · ค่าที่เก็บไว้เดิมต้องเปิดได้ · มีเครื่องมือย้ายไฟล์ที่แอดมินกดเอง ·
> ห้ามลบไฟล์บนดิสก์อัตโนมัติ · checker กันจุดใหม่ · "ตัดสินใจเอง"
>
> สถานะ: ✅ ผ่านที่เก็บกลางแล้ว · ⚙️ ยกเว้นโดยตั้งใจ (ไม่ใช่ไฟล์ของผู้ใช้) · ⚠️ ข้อจำกัดที่รู้ · ❌ ยังเปิด
> **ยังไม่ได้คอมไพล์ในเครื่องนี้** (ไม่มี .NET SDK — CI คือ compiler ตัวแรก)

## 0. คำตอบสั้น

- **มี abstraction เดียวแล้ว**: `Services/Storage/IFileStorage` — `LocalDiskFileStorage` (พฤติกรรมเดิมทุกไบต์) · `S3FileStorage`
  (SigV4 เขียนเอง `Helpers/S3SigV4` — ไม่เพิ่ม NuGet · ล็อกด้วยเวกเตอร์ทางการของ AWS) · เลือกด้วย `FileStorage:Provider`
- **ค่าที่ persist ไม่เปลี่ยน** (ไม่มี data migration): ทุกตารางยังเก็บพาธเดิม (`FileAttachment.StoragePath` · `LogoPath` · `PdfFilePath` …) ·
  object key **คำนวณ**จากพาธด้วย `Helpers/FileStorageKey` ตัวเดียว (ฝั่งเขียน · ฝั่งอ่าน · เครื่องมือย้าย ได้ key เดียวกัน) —
  `attachments/…` (= `FileStorage:BasePath`) · `web/…` (= wwwroot) · `app/…` (= content root เช่น `storage/etax`) · รากยาวสุดชนะ
- **ผู้แตะดิสก์ตรงเหลือ 32 จุด / 11 ไฟล์** (จาก 138 จุด / 35 ไฟล์ ที่ HEAD ก่อนรอบนี้ — นับด้วยกติกาเดียวกันของ checker รุ่นใหม่) — ทุกจุดที่เหลือคือไฟล์โปรแกรมที่มากับ deploy หรือการคำนวณพาธ (§2) ·
  ratchet `tools/local_disk_write_check.py` ขยายจาก "นับแค่ `GetCurrentDirectory()`" (ไม่เห็นผู้เขียนจริงส่วนใหญ่) เป็น
  `File.*` · `new FileStream` · `new FileInfo/DirectoryInfo` · `Directory.*` · `PhysicalFile(` · นับในรูอินเทอร์โพเลต `$"{…}"` ด้วย ·
  ยกเว้นเฉพาะ 4 ไฟล์ OWNER (`LocalDiskFileStorage` · `FileStorageResults` · `FileStorageLocator` · `FileStorageDefaults`) ไม่ใช่ทั้งโฟลเดอร์ + self-test
- **production fail-fast**: `Provider=S3` แต่ bucket/คีย์ว่างหรือเป็น placeholder ⇒ แอปไม่ start (`Helpers/FileStorageConfigPolicy`) ·
  นอก production = เตือนดังแล้วใช้ Local
- **ช่วงเปลี่ยนผ่านไม่มีไฟล์หาย**: โหมด S3 อ่าน object ไม่เจอ (404 เท่านั้น) ⇒ ถอยอ่านดิสก์ที่ตำแหน่งเดิม (`LocalReadFallback`) ·
  5xx/403/เครือข่าย ⇒ ล้มดัง ไม่แกล้งว่าไฟล์หาย
- **เครื่องมือย้าย**: `POST api/admin/file-storage/migrate?dryRun=true|false&limit=N` (SystemAdmin) — คัดลอก → อ่านกลับเทียบ SHA-256 ·
  รันซ้ำได้ (เทียบขนาด + hash ใน metadata ก่อนอ่านไฟล์) · ครบ limit แล้วไม่ hash ไฟล์ที่เหลือ (`unchecked`) · ไฟล์ใดล้มบันทึกแล้วไปต่อ ·
  ปลายทางที่ระบบเขียนเองแต่ไบต์ต่าง = `remoteNewer` (ฉบับหลังสลับเป็น S3 — ไม่ใช่ล้ม) · ปลายทางที่เขียนนอกระบบ + ไบต์ต่าง ⇒ ล้ม ไม่เขียนทับ ·
  ไม่ส่งไฟล์ความลับ (.pfx/.p12/.pem/.key) · **ไม่ลบไฟล์บนดิสก์** · `JobLock` (`AdvisoryLockKey.FileStorageMigrate`) กันสองเครื่อง ·
  ตรวจทุกคอลัมน์พาธในฐาน (`FileAttachments` รวม soft-delete · `CompanySettings.LogoPath/StampPath` · `DocumentBrands.LogoPath` · `EtaxInvoices.PdfFilePath/XmlFilePath` ·
  `SiteMedia` รุ่นก่อน R-43) · `complete`/ข้อความ "ครบแล้ว" เฉพาะเมื่อไม่เหลืองาน ไม่ล้ม และไม่มีพาธนอกราก (ไฟล์+ฐาน)
- **คลาสที่สร้างนอก DI** (เทสต์ · static helper ของตัวเรนเดอร์ PDF) ใช้ `FileStorageDefaults.Local` (ดิสก์ค่าเริ่มต้น = พฤติกรรมเดิม) — ไม่มีเส้นใดที่
  "ไม่มีที่เก็บ ⇒ เงียบ/โยน" (โลโก้หายจาก PDF · สลิปล้ม)

## 1. ตารางผู้เขียน/ผู้อ่าน → สถานะ

| # | จุด | ไฟล์ | เดิม | สถานะ |
| --- | --- | --- | --- | --- |
| 1 | ไฟล์แนบ (อัปโหลด/ไบต์/อ่าน/ลบ) | `Services/Implementations/FileAttachmentService.cs` | `File.Move`/`WriteAllBytes`/`ReadAllBytes`/`Delete` ใต้ `FileStorage:BasePath` | ✅ `IFileStorage` · BasePath อ่านที่ `FileStorageLocator` ที่เดียว · staging ย้าย = อ่าน→เขียน→ลบผ่านที่เก็บ |
| 2 | อัปโหลด/ดาวน์โหลดไฟล์แนบ | `Controllers/FileAttachmentController.cs` | `new FileStream` · `PhysicalFile` · `GetCurrentDirectory()` ×3 | ✅ เขียนผ่านที่เก็บ · เสิร์ฟ `FileStorageResults.FileResultAsync` (Local = PhysicalFile เดิม) |
| 3 | ย่อรูป+เก็บ (โลโก้ · ตราประทับ · โลโก้แบรนด์ · รูปสินค้า+thumb · รูปแอดมิน · รูปที่พัก · สลิปค่าบริการ/ส่วนเสริม/พอร์ทัล/ที่พัก · รูปไฟล์แนบ) | `Services/Implementations/ImageProcessingService.cs` | `Directory.CreateDirectory` + `File.WriteAllBytesAsync` | ✅ จุดเดียวครอบผู้เรียก 11 จุด · `IImageProcessingService.Storage` ให้ผู้เรียกอ่าน/ลบด้วยที่เก็บเดียวกัน |
| 4 | สแกน OCR เว็บ + ดูรูปสแกน | `Controllers/OcrController.cs` | `WriteAllBytesAsync` `wwwroot/uploads/ocr` · `PhysicalFile` | ✅ |
| 5 | สแกน OCR API v1 | `Controllers/V1/OcrV1Controller.cs` | `File.Create` | ✅ |
| 6 | ไฟล์แนบจาก integration (base64) | `Controllers/IntegrationController.cs` | `WriteAllBytesAsync` | ✅ |
| 7 | รูปจาก LINE → OCR | `Services/Implementations/LineBotService.cs` | `WriteAllBytesAsync` | ✅ |
| 8 | อ่านไฟล์ต้นฉบับสแกน (hash ซ้ำ · XML e-Tax ใน PDF · text layer · Azure · Tesseract · local) + ลบสแกน | `Services/Implementations/OcrService.cs` | `File.Exists/ReadAllBytesAsync/Delete` ×9 | ✅ `ReadSourceBytesAsync` (ไม่พบ ⇒ `FileNotFoundException` สัญญาเดิม) |
| 9 | กวาดไฟล์สแกนกำพร้า | `Services/Implementations/Ocr/OcrSelfCorrectionService.cs` | `File.Delete` | ✅ |
| 10 | คัดลอกไฟล์แนบตอนแปลงเอกสาร | `Services/Implementations/DocumentService.cs` (`CascadeAttachmentsAsync`) | `File.OpenRead/Create` ใน `Task.Run` | ✅ ที่เก็บ singleton (ไม่ผูก DbContext ของคำขอ) |
| 11 | PDF ใบเสร็จ/ใบแจ้งหนี้ค่าบริการที่เก็บไว้ | `Services/Implementations/SaasBillingDocumentService.cs` | `GetCurrentDirectory()` ×2 + `ReadAllBytesAsync` | ✅ `IFileAttachmentService.ReadBytesAsync` (ไม่พบ ⇒ สร้างใหม่ ตามเดิม) |
| 12 | PDF/A-3 + XML e-Tax | `Services/Implementations/EtaxInvoiceService.PdfA3.cs` · อ่านใน `DocumentEmailService.cs` | `WriteAllBytes/WriteAllText` `storage/etax` | ✅ XML คงไบต์เดิม (UTF-8 + BOM) |
| 13 | ลบโลโก้/ตราประทับเดิม | `Services/Implementations/SettingsService.cs` | `File.Exists/Delete` ×4 | ✅ |
| 14 | ลบโลโก้แบรนด์เดิม | `Controllers/DocumentBrandController.cs` | `File.Delete` | ✅ |
| 15 | ลบรูปสินค้า+thumb | `Services/Implementations/ProductService.cs` | `File.Delete` ตาม URL ที่ผู้เรียกส่ง | ✅ + **แข็งขึ้น**: ลบเฉพาะ URL ที่อยู่ในแกลเลอรีของสินค้านี้ · ใต้โฟลเดอร์บริษัทนี้ · ไม่มี `..` (เดิม URL ใดก็ลบได้ — ข้ามบริษัท/ออกนอกโฟลเดอร์) |
| 16 | ลบสื่อ CMS รุ่นก่อน R-43 | `Services/Implementations/CmsContentService.cs` | `File.Delete` ×3 | ✅ (สื่อใหม่ผ่าน `IFileAttachmentService` ตั้งแต่ R-43) |
| 17 | สื่อศูนย์ช่วยเหลือ (วิดีโอ/PDF ≤ 300 MB) | `Controllers/HelpCenterController.cs` | `File.Create` | ✅ · ⚠️ โหมด S3 บัฟเฟอร์ทั้งไฟล์ในหน่วยความจำ (SigV4 เซ็น hash ของเนื้อไฟล์ · จองบัฟเฟอร์ตามขนาด ไม่คัดลอกซ้ำ · PUT timeout 10 นาที) |
| 18 | สลิปจากพอร์ทัลลูกค้า | `Services/Implementations/PortalService.cs` | `Directory.CreateDirectory` + `File.Create` | ✅ |
| 19 | สลิปการจองที่พัก (เขียน · ตรวจมีจริง · เสิร์ฟ 3 endpoint) | `Lodging/LodgingService.Operations.cs` · `.Lifecycle.cs` · `LodgingController` · `LodgingPublicController` | `File.Create` · `File.Exists` · `PhysicalFile` ×3 | ✅ ด่านพาธ `LodgingSlipPath.Resolve` เดิม รับ `fileExists` จากที่เก็บ |
| 20 | สลิปค่าบริการ/ส่วนเสริม + หนังสือรับรอง 50 ทวิ (ลบเมื่อสร้างรายการล้ม · อ่าน OCR · เสิร์ฟ 4 endpoint) | `SubscriptionSlipUpload.cs` · `BillingCenterController` · `AdminPlatformBillingController` ×2 · `MeteringAdminController` | `File.Exists/Delete/ReadAllBytes` · `PhysicalFile` ×4 | ✅ `StoredSlip.Storage` + `SubscriptionSlipUpload.ServeAsync` · `OpenStored` ไม่ตรวจ "มีจริง" แล้ว (ตรวจตอนเสิร์ฟ) |
| 21 | ฝังโลโก้/ตราประทับใน PDF (QuestPDF sync) | `Services/Implementations/PdfGenerationService.cs` | `File.Exists/ReadAllBytes` | ✅ `IFileStorage.ReadAllBytes` (sync) |
| 22 | เสิร์ฟ `/uploads/**` สาธารณะ (โลโก้ · รูปสินค้า · สื่อ CMS · help-media · รูปที่พัก) | `Program.cs` static files | `PhysicalFileProvider` | ✅ โหมด S3: `FileStorageUploadsMiddleware` หลัง static handler · allow-list `publicUploadPrefixes` เดิมทำงานก่อนเสมอ · กัน `..`/backslash · ชนิดไฟล์ไม่รู้จักไม่เสิร์ฟ · Range/206 + If-None-Match/304 ส่งต่อถึง S3 · ETag/Last-Modified |
| 23 | ส่งออก Excel/CSV/PDF ทั้งหมด (MiniExcel `SaveAs` · QuestPDF) | หลายไฟล์ | `MemoryStream` | ⚙️ ไม่แตะดิสก์อยู่แล้ว |

## 2. จุดที่เหลือใน baseline (32 จุด / 11 ไฟล์) — ยกเว้นโดยตั้งใจ

| ไฟล์ | จุด | เหตุผล | สถานะ |
| --- | --- | --- | --- |
| `Program.cs` | 11 | สร้างโฟลเดอร์ `uploads` ตอน start (โหมด Local) · โมเดล ONNX (`models/`) · คีย์ DataProtection | ⚙️ · ❌ **คีย์ DataProtection อยู่บนดิสก์ของเครื่อง** — หลาย instance ต้องใช้ volume ร่วมหรือย้ายไป DB (งานแยก — ไม่ใช่ไฟล์ของผู้ใช้) |
| `Controllers/EtaxController.cs` · `Services/Implementations/EtaxInvoiceService.cs` | 3 + 1 | ใบรับรองดิจิทัล e-Tax (.pfx) ที่ผู้ดูแลวางเอง — เป็น**ความลับ** ไม่ใช่ไฟล์อัปโหลด | ⚙️ · ❌ ควรย้ายไป secret store (งานแยก) |
| `Services/Implementations/KnowledgeBaseService.cs` | 4 | อ่านเอกสาร `.md` ของเรพเป็นฐานความรู้แชตบอต (มากับ deploy) | ⚙️ |
| `Services/Ai/Embedding/WordPieceTokenizer.cs` · `OnnxSentenceEmbeddingService.cs` | 3 + 1 | `vocab.txt`/โมเดล ONNX (มากับ deploy · `FileInfo` ตรวจขนาด) | ⚙️ |
| `Services/Implementations/Ocr/EmbeddedTesseractOcrService.cs` | 2 | `wwwroot/tessdata` (มากับ deploy) | ⚙️ |
| `Services/Implementations/PdfGenerationService.PdfA3.cs` | 2 | ฟอนต์ไทยใน `Fonts/` (มากับ deploy) | ⚙️ |
| `Services/Implementations/SettingsService.cs` · `Controllers/DocumentBrandController.cs` · `Lodging/LodgingService.Operations.cs` | 2 + 1 + 2 | `GetCurrentDirectory()` ใช้**คำนวณพาธ** wwwroot เท่านั้น — การเขียน/อ่าน/ลบผ่านที่เก็บแล้ว | ⚙️ (ลดได้ด้วย `Locator.WebRoot` เมื่อแตะไฟล์นั้นครั้งหน้า) |

## 3. การตัดสินใจ (เจ้าของสั่ง "ตัดสินใจเอง")

1. **ต่อยอด ไม่สร้างคู่** — ไม่มี `IFileStorage` เดิม · `IFileAttachmentService` (ที่เก็บกลางระดับ "แถวไฟล์แนบ") คงเดิมแต่อยู่บน `IFileStorage` ·
   ผู้เรียกที่เก็บไฟล์ไม่ใช่ไฟล์แนบ (โลโก้/สลิป/e-Tax) ใช้ `IFileStorage` ตรง
2. **key คำนวณจากพาธ ไม่ persist key** — ไม่มีช่วงที่แถวเก่าอ่านไม่ได้ · ไม่มี migration ข้อมูล · เงื่อนไข: พาธในคอนเทนเนอร์ต้องคงที่
   (`/app` · `FileStorage:BasePath` เดิม) — ย้ายแล้ว เครื่องมือย้ายรายงาน `dbRowsUnmappable`
3. **SigV4 เขียนเอง ไม่ใช้ AWSSDK.S3** — ใช้ 4 คำสั่ง · ไม่ลาก dependency (แนว MiniExcel) · ล็อกสูตรด้วยเวกเตอร์ทางการ AWS ·
   path-style ค่าเริ่มต้น (MinIO บังคับ · R2/Spaces/AWS รองรับ)
4. **PUT แนบ `x-amz-meta-sha256`** — เครื่องมือย้ายข้ามไฟล์ที่ย้ายแล้วด้วย HEAD ไม่ต้องดาวน์โหลด · ตรวจหลังคัดลอกด้วย GET จริง (ไม่เชื่อ ETag — MD5 ไม่ใช่ทุกเจ้า)
5. **อ่านถอยไปดิสก์เฉพาะ 404** (`LocalReadFallback` เปิดเป็นค่าเริ่มต้น) — deploy S3 ได้ก่อนย้าย · 5xx ⇒ ล้มดัง (F2 ข้อ 7)
6. **ลบ = ลบทั้ง object และสำเนาบนดิสก์** — ผู้ใช้สั่งลบแล้วต้องไม่เหลือ (ระยะเก็บ 5 ปีตัดสินโดยผู้เรียกเดิม: `AttachmentRetention` · `keepPhysicalFile`)
7. **เครื่องมือย้ายไม่ลบดิสก์** — หลักฐานบัญชีอยู่ใต้ ม.10 / §87/3 · เจ้าของถอด volume เองหลังตรวจรายงาน
8. **ไม่ใส่ S3 key ใน `SecretReprotectJob` registry** — registry นั้นสำหรับ "คอลัมน์ในฐานที่เข้ารหัสด้วย SecretProtector" ·
   คีย์ S3 เป็น env/config (แนวเดียวกับ `JWT_SECRET`/`ENCRYPTION_KEY`) ⇒ ด่าน fail-fast ใน `FileStorageRegistration` แทน
9. **เสิร์ฟไฟล์สาธารณะผ่านแอป** (ไม่ใช่ URL ตรงของ bucket) — bucket เป็น private ทั้งหมด · allow-list `publicUploadPrefixes` เดิมคุมสิทธิ์ตัวเดียว

## 4. ข้อจำกัดที่รู้ (⚠️)

- โหมด S3 บัฟเฟอร์ไฟล์ทั้งก้อนในหน่วยความจำตอนเขียน (help-media สูงสุด 300 MB) — ถ้าวิดีโอใหญ่บ่อย พิจารณา multipart upload (งานแยก)
- ตัวเรนเดอร์ PDF และแกลเลอรีสลิปที่พักเรียกที่เก็บแบบ sync (HEAD/GET ต่อรูป) ในโหมด S3
- AWS: คีย์ต้องมี `s3:ListBucket` มิฉะนั้น object ที่ไม่มีตอบ 403 (ไม่ใช่ 404) ⇒ ระบบล้มดังแทน "ไม่พบไฟล์" · ตรวจได้ที่ `GET api/admin/file-storage/status` ช่อง `probe`
- ยังไม่มีการ probe bucket ตอน start (ข้อเสนอฝ่ายค้าน N5) — ใช้ช่อง `probe` บนหน้า status แทน (ไม่เพิ่ม hosted service ที่ต้องผ่าน JobLock)
- ลำดับ "allow-list ก่อน middleware" ใน Program.cs ล็อกด้วยการอ่านโค้ด/doc เท่านั้น (เทสต์ middleware ครอบตัวกันพาธ/ชนิดไฟล์/Range — ไม่ครอบลำดับ pipeline)
- `Provider=Local` ยังรันหลาย instance ไม่ได้ (เว้นแต่ volume ร่วม) — เป้าหมายของ S3 คือแก้ข้อนี้
- คีย์ DataProtection + ใบรับรอง e-Tax ยังอยู่บนดิสก์ (§2 · งานแยก)

## 5. ขั้นตอน deploy ของเจ้าของ

1. **สร้าง bucket แบบ private** (ไม่เปิด public access) — R2: สร้าง API token "Object Read & Write" เฉพาะ bucket ·
   AWS: IAM policy `s3:GetObject` `s3:PutObject` `s3:DeleteObject` `s3:ListBucket` เฉพาะ bucket · MinIO/Spaces: access key ของ bucket
   · เปิด versioning หรือสำรองข้อมูล (หลักฐานบัญชีเก็บ 5 ปี — **ห้ามตั้ง lifecycle ลบอัตโนมัติ**)
2. **ตั้ง env** (ห้ามใส่คีย์ลงไฟล์ appsettings):

   | env | ตัวอย่าง |
   | --- | --- |
   | `FileStorage__Provider` | `S3` |
   | `FileStorage__S3__Endpoint` | R2 `https://<accountid>.r2.cloudflarestorage.com` · MinIO `http://minio:9000` · Spaces `https://sgp1.digitaloceanspaces.com` · AWS เว้นว่าง |
   | `FileStorage__S3__Region` | R2 `auto` · MinIO `us-east-1` · Spaces `sgp1` · AWS `ap-southeast-1` |
   | `FileStorage__S3__Bucket` | `nextacc-files` |
   | `FileStorage__S3__AccessKeyId` / `FileStorage__S3__SecretAccessKey` | คีย์จริง (placeholder/ว่าง ⇒ แอปไม่ start) |
   | `FileStorage__S3__KeyPrefix` (ไม่บังคับ) | `prod` — แยก staging/production ใน bucket เดียว |
   | `FileStorage__S3__LocalReadFallback` | `true` จนย้ายครบ |
   | `FileStorage__BasePath` | **คงค่าเดิม** (พาธเดิมคือสูตร key ของไฟล์แนบทุกแถว) |

3. **deploy โดยยังเมานต์ volume `uploads`/`wwwroot/uploads`/`storage` เดิมไว้** — ไฟล์ใหม่เข้า bucket · ไฟล์เก่าอ่านจากดิสก์
4. ล็อกอิน SystemAdmin → `GET /api/admin/file-storage/status` (ดู provider/ปลายทาง/โฟลเดอร์ · `probe` ต้องเป็น "ok") →
   `POST /api/admin/file-storage/migrate?dryRun=true` (นับ) → `POST …/migrate?dryRun=false&limit=200` ซ้ำจน **`complete = true`**
   (= `remaining = 0` · `unchecked = 0` · `failed = 0` · `unmappable = 0` · `dbRowsUnmappable = 0`) · `remoteNewer` > 0 ไม่ใช่ปัญหา (ฉบับใหม่อยู่ใน bucket แล้ว)
5. staging ก่อน: ตั้ง `LocalReadFallback=false` แล้วเปิดไฟล์แนบ/โลโก้/สลิป/สแกน/e-Tax ของจริงหลายบริษัท (TEST_PLAN FS-8) → แล้วค่อยทำ production
6. สำรอง volume เดิมเก็บไว้ (snapshot) ก่อนถอด — เครื่องมือไม่ลบไฟล์บนดิสก์ให้

## 6. กันถดถอย

- `tools/local_disk_write_check.py` (ratchet + `--self-test` อยู่ใน `check_all.sh`) — จุดใหม่ที่แตะดิสก์ตรงนอก `Services/Storage/` ⇒ ล้ม
- เทสต์: `FileStorageKeyTests` · `S3SigV4Tests` · `FileStorageConfigPolicyTests` · `FileStorageProviderTests` · `Db/ProductImageRemoveDbTests` (TEST_PLAN §FS-1..FS-10)

## 7. ฝ่ายค้าน (ผู้ประสานงาน 2026-11-02) — แก้แล้วในรอบเดียวกัน

B1 `_storage` อยู่ผิดคลาส (`ExternalIntegrationController` เป็นผู้ใช้จริง) · B2 สลิปที่พัก/พอร์ทัลโยนเมื่อสร้างนอก DI (Db tests) → ctor `IFileStorage?` + `FileStorageDefaults.Local` ·
B3 โลโก้ PDF หายเมื่อ static storage ว่าง → `FileStorageDefaults.Local` · S1 หยุด hash หลังครบ limit + เทียบขนาด/metadata ก่อน + จับ IO ต่อไฟล์ ·
S2 `remoteNewer` · S3 `complete` + ตรวจคอลัมน์พาธทุกตาราง · S4 Range/ETag/ชนิดไม่รู้จัก · S5 บัฟเฟอร์/timeout · S6 ปิด secret ใน `ToString` ·
S7 เทสต์ middleware + `RemoveImageAsync` สองทิศ · N1 ยกเว้นรายไฟล์ + รูอินเทอร์โพเลต/FileInfo · N4 `AdvisoryLockKey.FileStorageMigrate` · N7 ข้ามไฟล์ความลับ
