# ผลตรวจ e-Tax Invoice by Email (2026-11-06)

> คำขอเจ้าของ: "เคสที่ส่งใบกำกับภาษีผ่านอีเมล ตรวจว่าออกเป็น e-Tax และส่งแบบ e-Tax by Email ถูกต้องครบถ้วน
> เมื่อบริษัทตั้งค่า e-Tax by Email ครบ และตั้ง SMTP ของตัวเองครบ"
> ขอบเขต: เฟส 1 อ่านอย่างเดียว · ไม่ได้แก้โค้ด · ไม่ได้คอมไพล์ (env ไม่มี .NET SDK)

## คำตัดสินสำหรับกรณีของเจ้าของ

**ยังไม่ถูกต้อง** แม้บริษัทจะตั้งค่า e-Tax by Email และ SMTP ของตัวเองครบแล้วก็ตาม

- มีทางส่งแบบ e-Tax by Email อยู่ทางเดียว คือ `DocumentEmailService.SendEtaxByEmailAsync` ซึ่งผู้ใช้ต้องกดเองที่หน้า e-Tax หรือติ๊ก "ส่งแบบ e-Tax" ในหน้าต่างส่งอีเมล
  ทางนี้ส่งจาก SMTP ของบริษัท ใส่ CC `csemail@etax.teda.th` และแนบ PDF/A-3 ที่ฝัง XML แล้ว **แต่รูปแบบอีเมลไม่ตรงคู่มือกรมสรรพากร**
  หัวเรื่องเป็น `{เลขภาษี}.{เลขเอกสาร}` ซึ่งไม่ตรงรูปแบบ `[วันที่พ.ศ.][INV][เลขที่]` ตามคู่มือ และแนบ 2 ไฟล์ (XML + PDF) ทั้งที่คู่มือกำหนด 1 ไฟล์ต่ออีเมล
  ผลคือ ETDA มีแนวโน้มจะไม่ประทับเวลาให้
- ทางส่งอัตโนมัติทุกทางไม่ได้ส่งเป็น e-Tax by Email เลย ได้แก่ ส่งหลังรับชำระ (รอบ 241) · คิวตั้งเวลา/ตอนอนุมัติ · รอบเอกสารประจำ · ค่าตั้ง "ส่งอัตโนมัติเมื่ออนุมัติ"
  ทุกทางนี้ส่งเป็นอีเมลเอกสารธรรมดา ไม่มี CC ถึง ETDA และไม่ใช้หัวเรื่องตามกฎ แต่ไฟล์แนบเป็น PDF/A-3 ที่ดูเหมือน e-Tax
  ลูกค้าจึงได้ใบที่ไม่เคยถูกประทับเวลา
- ระบบไม่เคยรับหรือเก็บใบที่ ETDA ประทับเวลาแล้วส่งกลับมา ไม่มีช่อง `EtdaTimestampToken` และสถานะของแถว e-Tax ไม่ขยับหลังส่ง
  แต่ส่วนอื่นของระบบถือว่า "ส่งพร้อม CC ได้สำเร็จ" เท่ากับ "ถึงกรมสรรพากรแล้ว" ซึ่งเป็นสถานะที่ระบบประทับขึ้นเอง
- ถ้า SMTP ของบริษัทยังไม่ได้ตั้งหรือยังทดสอบไม่ผ่าน ทาง e-Tax **จะไม่ปฏิเสธการส่ง** แต่จะถอยไปใช้ SMTP ของแพลตฟอร์ม
  โดยใส่ From เป็นอีเมลที่บริษัทลงทะเบียนไว้ ซึ่งเป็นการปลอมโดเมน และไม่ได้ส่งจากอีเมลที่ลงทะเบียนจริง

## กฎทางการ (ค้นผ่าน WebSearch · เปิดเว็บราชการตรงไม่ได้)

ทุกโดเมน `rd.go.th` · `etax.teda.th` · `getinvoice.net` ถูกปิดใน network ของ session นี้ (ENOTFOUND)
เนื้อหาด้านล่างจึงมาจากข้อความย่อของผลค้นหาที่ชี้ไปยังเอกสารต้นทาง ก่อนลงมือแก้ควรให้คนเปิดคู่มือ RD ฉบับปัจจุบันยืนยันอีกครั้ง

| # | กฎ | ความมั่นใจ | แหล่ง |
|---|---|---|---|
| R1 | ใช้ได้เฉพาะผู้ประกอบการจด VAT ที่รายได้ไม่เกิน 30 ล้านบาท/ปี และต้องยื่นขออนุมัติต่ออธิบดีกรมสรรพากร (ก.อ.01) | สูง | etax.rd.go.th (01_Overview) · smemove · peakaccount |
| R2 | ใช้ได้ **ระบบเดียว** คือ by Email **หรือ** e-Tax Invoice & e-Receipt (H2H/ผู้ให้บริการ) อย่างใดอย่างหนึ่ง | สูง | คู่มือ RD by Email · leceipt · aplusme |
| R3 | ตั้งแต่ 1 ก.ค. 2561 ไฟล์ต้องเป็น **PDF/A-3** (ETDA workshop: PDF/A-3U + ฝัง XML) | สูง | คู่มือ RD manual-etax-invoiceby-email-110161.pdf · etax.teda.th/ETDA_PDFA3_Workshop.pdf |
| R4 | ส่ง **จากอีเมลที่ลงทะเบียนกับกรมสรรพากร** · To = ผู้ซื้อ (**ระบุได้ 1 อีเมล**) · CC = `csemail@etax.teda.th` | สูง | คู่มือ RD (ข้อความย่อจากผลค้น) |
| R5 | **1 อีเมล = ใบกำกับ 1 ฉบับ = ไฟล์แนบ 1 ไฟล์** · ไม่เกิน **3 MB** · ห้ามใส่รหัสผ่าน/เข้ารหัส | สูง | คู่มือ RD |
| R6 | หัวเรื่องต้องเรียงตามรูปแบบที่กำหนด ใช้วงเล็บเหลี่ยมติดกันไม่เว้นวรรค · วันที่ `ddMMyyyy` เป็น **พ.ศ.** · ใบกำกับ `[18012560][INV][เลขที่]` · ใบเพิ่มหนี้ `[วันที่][DBN][เลขใบเพิ่มหนี้][เลขใบเดิม]` · ใบลดหนี้ `[วันที่][CDN หรือ CRN][เลขใบลดหนี้][เลขใบเดิม]` · ยกเลิก `[วันที่][CIV][เลขใบเดิม]` · ใบแทน `[วันที่][INV][เลขใหม่][เลขใบเดิมที่ยกเลิก]` | กลาง-สูงสำหรับรูปแบบ · **กลาง** สำหรับรหัส CDN/CRN (แหล่งไม่ตรงกัน) และรหัสใบรับ (มีแหล่งที่ใช้ TIV/RCT แต่ยังหาตัวอย่างหัวเรื่อง [RCT] ไม่พบ) | คู่มือ RD · RD seminar 180820/190819/190820 · getinvoice · peakaccount |
| R7 | ETDA ประทับเวลาแล้วส่งไฟล์กลับให้ผู้ขายและผู้ซื้อ · ผู้ซื้อตรวจได้ที่ validation.etax.teda.th | สูง | getinvoice · คู่มือ RD |
| R8 | สภาพแวดล้อมทดสอบใช้ `csemail@uat.etax.teda.th` และต้องลงทะเบียนทดสอบกับ eservice@etda.or.th | กลาง | etax.teda.th eTax_Email_instruction_to_register_email_atTest_env_v1.0.pdf |
| R9 | ใช้ลายมือชื่อดิจิทัลของผู้ขายไหม — แหล่งขัดกัน (aplusme บอกไม่ต้องใช้ cert เพราะ ETDA ประทับเวลาแทน · bangkokaccounting บอกต้องมี) | ต่ำ | — |
| R10 | ผู้ให้บริการ/แพลตฟอร์มส่งแทนจากอีเมลของตัวเอง — ไม่พบกฎที่อนุญาต · R4 กำหนดให้ส่งจากอีเมลที่ลงทะเบียน ⇒ การส่งในนามจาก SMTP ของแพลตฟอร์ม **ถือว่าใช้ไม่ได้** | กลาง (อนุมานจาก R4) | — |

แหล่ง:
- [คู่มือผู้ประกอบการ e-Tax Invoice by Email (RD)](https://www.rd.go.th/fileadmin/user_upload/eTax/manual-etax-invoiceby-email-110161.pdf)
- [RD seminar 190820](https://www.rd.go.th/publish/seminar/Seminar_190820.pdf) · [RD seminar 190819](https://www.rd.go.th/publish/seminar/Seminar_190819.pdf) · [RD 180820 doc](https://www.rd.go.th/publish/seminar/180820_eTaxInvoice-eReceipt_doc.pdf)
- [etax.rd.go.th Overview](https://etax.rd.go.th/etax_staticpage/app/emag/flipbook/01_Overview.pdf) · [ETDA e-Tax by Email](https://etax.teda.th/) · [ETDA PDF/A-3 workshop](https://etax.teda.th/etaxdocuments/ETDA_PDFA3_Workshop.pdf) · [ETDA test env](https://etax.teda.th/etaxdocuments/eTax_Email_instruction_to_register_email_atTest_env_v1.0.pdf)
- [getinvoice ขั้นตอน](https://www.getinvoice.net/how-to-etax-invoice-by-email/) · [PEAK ยกเลิก by email](https://www.peakaccount.com/blog/program-usage/manual/howto-cancel-etax-invoice-by-email) · [PEAK ความแตกต่าง](https://www.peakaccount.com/blog/tax/gen-tax/tax-difference-of-etax) · [leceipt](https://www.leceipt.com/blog/%E0%B8%A1%E0%B8%B5%E0%B8%A3%E0%B8%B2%E0%B8%A2%E0%B9%84%E0%B8%94%E0%B9%89%E0%B9%80%E0%B8%81%E0%B8%B4%E0%B8%99-30-%E0%B8%A5%E0%B9%89%E0%B8%B2%E0%B8%99%E0%B8%95%E0%B9%88%E0%B8%AD%E0%B8%9B%E0%B8%B5) · [aplusme](https://aplusme.org/blog/e-tax-invoice-registration-usage-guide.html) · [smemove](https://smemove.com/blog/e-tax-invoice-by-e-mail-%E0%B8%81%E0%B8%B1%E0%B8%9A-e-tax-invoice-e-receipt/)

## แผนที่ทางส่งอีเมลที่แตะใบกำกับภาษี (ชื่อไฟล์ใต้ `Accounting/`)

| ทางส่ง | จุดในโค้ด | เป็น e-Tax by Email ไหม | ผู้ส่ง |
|---|---|---|---|
| ส่งเอง: หน้า e-Tax → "📧 ส่งทางอีเมล" | `wwwroot/pages/etax.html:396,608-645` → `EtaxController.SendByEmail` `Controllers/EtaxController.cs:293-304` → `SendEtaxByEmailAsync` `Services/Implementations/DocumentEmailService.cs:176-333` | ใช่ | `GetSenderAsync` (:235) ⇒ SMTP บริษัท หรือ**ถอยไป SMTP แพลตฟอร์มเงียบ ๆ** |
| ส่งเอง: หน้าเอกสาร → "📧 ส่งอีเมล" + ติ๊ก "ส่งแบบ e-Tax" | `wwwroot/pages/documents.html:11381` (`preferEtax=false` = ไม่ติ๊กเป็นค่าตั้งต้น) · `:11922-11942` | ใช่ เมื่อติ๊ก | เหมือนบรรทัดบน |
| ส่งเอง: ฟอร์มสร้างเอกสาร "บันทึกและส่ง" + `fEmailAsEtax` | `wwwroot/pages/documents.html:10340-10360` | ใช่ เมื่อติ๊ก | เหมือนบรรทัดบน |
| ส่งเอง: หน้าเอกสาร ไม่ติ๊ก e-Tax / ส่งทวง | `SendDocumentEmailAsync` `DocumentEmailService.cs:42-174` | **ไม่ใช่** (แนบ PDF/A-3 ที่ฝัง XML ได้ แต่ไม่มี CC ETDA) | OnBehalf resolver (รอบ 240) |
| ส่งหลังรับชำระอัตโนมัติ (รอบ 241) | `Services/Implementations/PostPaymentAutomationService.cs:318-341` → `SendDocumentEmailAsync` | **ไม่ใช่** | OnBehalf |
| คิวตั้งเวลา / ตอนอนุมัติ / รอบเอกสารประจำ | `Services/Implementations/EmailScheduleService.cs:55,183,428-540,554-640` | **ไม่ใช่** | OnBehalf + ห่อเนื้อของแพลตฟอร์ม |
| ค่าตั้ง "ส่ง e-Tax by Email อัตโนมัติเมื่ออนุมัติ" | `Models/Entities/CompanySettings.cs:198` · บันทึก/คืนค่าที่ `EtaxController.cs:343,374` | — **ไม่มีโค้ดที่อ่านค่านี้** | — |
| ปุ่มส่ง e-Tax คลิกเดียว | `documents.html:11815` `sendEtaxEmailOneClick` | — **ไม่มีผู้เรียก** | — |

## ผลตรวจ (B = บล็อก/ผิดกฎหมาย · S = ร้ายแรง · N = ควรแก้)

| # | ระดับ | ข้อค้นพบ | หลักฐาน |
|---|---|---|---|
| E1 | **B** | หัวเรื่องเป็น `{SellerTaxId}.{EtaxRefNumber}` ซึ่งไม่ตรง R6 (`[วันที่พ.ศ.][INV][เลขที่]…`) · doc-comment อ้างว่า "RD-mandated" แต่ไม่มีแหล่งรองรับ · ระบบทับหัวเรื่องที่ผู้ใช้พิมพ์เสมอ จึงแก้มือไม่ได้ · ใช้รูปแบบเดียวกันทุกชนิดเอกสาร (ไม่มี DBN/CDN/CIV/ใบแทน) | `DocumentEmailService.cs:203-207,511-526` · UI ล็อกช่องตามรูปแบบเดิม `documents.html:3295-3305` · `etax.html` (หัวเรื่องที่ล็อก) |
| E2 | **B** | แนบ **2 ไฟล์** คือ `{ref}.xml` และ `{ref}.pdf` เป็นค่าตั้งต้น (`IncludeXml=true`) ทั้งที่ R5 กำหนด 1 ไฟล์ และ XML ฝังใน PDF/A-3 อยู่แล้ว · UI ทั้งสองหน้าส่ง `includeXml:true` | `Models/DTOs/Email/EmailDtos.cs:16-24` · `DocumentEmailService.cs:245-254` · `documents.html:10349,11941` · `etax.html:603` |
| E3 | **B** | ถ้า SMTP บริษัทยังไม่ได้ตั้ง/ยังทดสอบไม่ผ่าน ระบบ**ไม่ปฏิเสธ** · `GetSenderAsync` → `GetSenderForSettings` ถอยไป `GetGlobalFallbackSender` แต่ `From` = `EtaxByEmailSenderEmail` ⇒ ส่งผ่านเซิร์ฟเวอร์แพลตฟอร์มโดยปลอม From ของบริษัท (DMARC ล้ม และไม่ได้ส่งจากอีเมลที่ลงทะเบียนตาม R4) · ไม่ตรวจ `EmailConfigured` (ทดสอบผ่าน) และไม่ใช้ `OnBehalfEmailSender` | `DocumentEmailService.cs:235-243` · `Services/Implementations/Email/EmailSenderFactory.cs:29-34,46-48` |
| E4 | **S** | ไม่ตรวจว่า `EtaxByEmailSenderEmail` คือกล่องที่ SMTP/Graph ของบริษัทส่งออกจริง (`ResolveFromAddress`) · Graph ส่งจาก UPN ส่วน SMTP อาจปฏิเสธหรือเขียน From ทับ ⇒ ผู้ส่งจริงอาจไม่ใช่อีเมลที่ลงทะเบียน | `DocumentEmailService.cs:240` เทียบ `:483-491` |
| E5 | **S** | ทางอัตโนมัติ (หลังรับชำระ · คิว · ตอนอนุมัติ · ประจำ) ส่งใบกำกับภาษี/ใบเสร็จของบริษัทที่ใช้โหมด by Email เป็นอีเมลธรรมดา ไม่มี CC ETDA ⇒ ลูกค้าได้ PDF/A-3 ที่ไม่มีวันถูกประทับเวลา · ผู้ขายเข้าใจว่าส่ง e-Tax แล้ว | `PostPaymentAutomationService.cs:337-338` · `EmailScheduleService.cs:554-640` |
| E6 | **S** | `EtaxByEmailAutoSendOnApprove` ถูกเก็บ บันทึก และคืนค่าบนหน้าตั้งค่า แต่**ไม่มีโค้ดที่อ่าน** (ผิดหลัก F2#2 ค่าตั้งที่ไม่มีผล) · `EtaxByEmailEmbedXml` ก็เช่นกัน | `CompanySettings.cs:197-198` · `EtaxController.cs:342-343` · grep หาผู้อ่านได้ 0 จุด |
| E7 | **S** | ไม่มีการรับหรือเก็บใบที่ ETDA ประทับเวลาแล้ว (ไม่มี `EtdaTimestampToken` หรือไฟล์ที่ส่งกลับ) ซึ่งขัด CLAUDE.md §F · `EtaxInvoice.Status` ยังเป็น Generated/Signed หลังส่ง · `DocumentVoidPreconditions` ถือว่า `IncludedRdTimestamp && Sent` คือ "ประทับเวลาแล้ว/ถึงกรมสรรพากร" ⇒ เป็นสถานะปลายทางที่ระบบประทับขึ้นเอง (F2#3) · ไม่มีจอให้ผู้ใช้บันทึกหรืออัปโหลดใบที่ประทับเวลา | `Models/Entities/DocumentEmailLog.cs:33-34` · `Helpers/DocumentVoidPreconditions.cs:172-175` · `EtaxInvoiceService.cs` (ไม่มีจุดตั้งสถานะจากการส่งอีเมล) |
| E8 | **S** | `SendEtaxByEmailAsync` **ไม่ตรวจสถานะเอกสาร/แถว e-Tax** ⇒ ส่งใบที่ Voided หรือแถว e-Tax ที่ Voided/Error พร้อม CC ETDA ได้ (`SendDocumentEmailAsync` มีด่านนี้ที่ :52-61 แต่ทางนี้ไม่มี) | `DocumentEmailService.cs:176-199` |
| E9 | **S** | หน้าเอกสารไม่ตรวจ `res.success` · `EtaxController` คืน HTTP 200 พร้อม `success:false` เมื่อส่งล้ม และ `API.request` ไม่ throw ⇒ จอขึ้น "ส่ง e-Tax ทางอีเมลสำเร็จ (… สำเนาสรรพากร)" ทั้งที่ส่งไม่ผ่าน (หน้า etax.html ตรวจถูกต้องแล้ว) | `EtaxController.cs:300-303` · `documents.html:10348-10352,11941-11942` |
| E10 | **S** | To รับได้หลายอีเมล และยังรับ Cc/Bcc ของผู้ใช้ ⇒ ขัด R4 (To 1 อีเมล) · ถ้าผู้ใช้พิมพ์ csemail ใน Cc เองจะได้ CC ซ้ำ ไม่มี dedupe · ผู้ใช้เอาติ๊ก CC ETDA ออกได้ (`IncludeRdTimestamp=false`) โดยยังติดธง `IsEtaxByEmail=true` | `DocumentEmailService.cs:209-214,243-245` · `etax.html:617-623` |
| E11 | **S** | โหมดทดสอบ (`EtaxTestMode`) ไม่สลับไป `csemail@uat.etax.teda.th` ⇒ การทดสอบส่งเข้า ETDA production จริง | ไม่มีการอ้าง `EtaxTestMode` ในทางอีเมล |
| E12 | **S** | `EtaxMode.Both` ขัด R2 (ใช้ได้ระบบเดียว) · ไม่มีด่านรายได้ ≤ 30 ล้าน (R1) ทั้งตอนตั้งค่าและตอนส่ง | `Models/Enums/AllEnums.cs:1351-1357` · `EtaxController.UpdateConfig` :331-347 |
| E13 | N | ไม่ตรวจขนาดไฟล์ ≤ 3 MB (R5) ก่อนส่ง | `DocumentEmailService.cs:258-317` |
| E14 | N | อ้างว่าเป็น PDF/A-3**U** ด้วยการแก้ไบต์ `B→U` ใน XMP (`:342-352`) แต่ไม่มีเทสต์ที่ตรวจด้วย validator (veraPDF/ETDA) · ชื่อไฟล์ที่ฝังเป็น `ETDA-invoice.xml` ถูกต้อง (`:58-61`) | `PdfGenerationService.PdfA3.cs` |
| E15 | N | หน้า e-Tax แสดงปุ่มส่งทางอีเมลเฉพาะสถานะ Signed/Submitted/Accepted แต่ทางหน้าเอกสารสร้างแถวด้วย `signDigitally:false` (Generated) ⇒ แถวเดียวกันส่งซ้ำจากหน้า e-Tax ไม่ได้ · ฟอร์มสร้างเอกสารใช้ `signDigitally:true` ไม่ตรงกับ by Email (R9) | `etax.html:396` · `documents.html:10345,11934` |
| E16 | N | ไม่มีด่านกันส่งซ้ำ: ส่งใบเดิมซ้ำพร้อม CC ⇒ ETDA ประทับเวลาเลขเดิมหลายครั้ง · ไม่มีคำเตือน "ส่งแล้วเมื่อ…" | `DocumentEmailService.cs:176` |
| E17 | N | ไม่มีเทสต์เลยสำหรับ `SendEtaxByEmailAsync` และ `BuildEtaxByEmailSubject` (ค้นใน `Accounting.Tests` ได้ 0 ไฟล์) | — |
| OK1 | ✔ | ทาง e-Tax **ไม่ถูกห่อ** ด้วยหัว/ท้ายของแพลตฟอร์ม (รอบ 240/241) · หัวเรื่องไม่ผ่าน `SafeSubject` · เนื้ออีเมลมีหมายเหตุ ETDA (เนื้ออีเมลไม่ถูกบังคับรูปแบบในกฎ) | `DocumentEmailService.cs:176-333` ไม่เรียก `ResolveOnBehalfSenderAsync`/`Wrap*` |
| OK2 | ✔ | ใส่ CC `csemail@etax.teda.th` (ค่าตั้งต้น) · ผู้ใช้แก้ที่อยู่นี้ผ่าน API ไม่ได้ · ฝัง XML ชื่อ `ETDA-invoice.xml` · ใช้ไฟล์ PDF ที่เก็บไว้ก่อน แล้วค่อยสร้างใหม่ · ถ้าสร้าง PDF ไม่ได้จะล้มดัง | `DocumentEmailService.cs:211-214,256-317` · `EtaxController.cs:341` |
| OK3 | ✔ | การสร้างแถว e-Tax กันใบ Draft/Voided/WaitingApproval/Rejected และกันการสร้างซ้ำ | `EtaxInvoiceService.cs:108-121` |

## แผนแก้ (เสนอ · ยังไม่ได้ลงมือ)

1. **ตัวตัดสินกลาง `Helpers/EtaxByEmailFormat.cs` (pure)**
   - `Subject(docType, issueDate, number, refOriginalNo?, cancelledNo?)` คืน `[ddMMyyyy พ.ศ.][INV|DBN|CDN|CIV][…]` โดยใช้เวลา +07:00 และตัวแปลง พ.ศ. ตัวกลางที่มีอยู่
   - `AttachmentPolicy` บังคับไฟล์เดียว คือ `{ref}.pdf` (PDF/A-3 ที่ฝัง XML) และตรวจว่าไม่เกิน 3 MB
   - `TimestampAddress(testMode)` คืนที่อยู่ prod หรือ uat ตามโหมด
   - ให้คนยืนยันรหัสของใบลดหนี้ (CDN หรือ CRN) และใบรับ/ใบเสร็จ (RCT?) กับคู่มือ RD ฉบับปัจจุบันก่อน merge
   - เปลี่ยน `BuildEtaxByEmailSubject` ให้เรียกตัวนี้ และแก้ UI ที่ล็อกหัวเรื่องให้แสดงค่าจากเซิร์ฟเวอร์ ไม่สร้างหัวเรื่องเอง
2. **ด่านผู้ส่ง `EtaxByEmailSenderGate` (pure) ใน `SendEtaxByEmailAsync`**
   - ต้องผ่านครบ: `EmailConfigured && HasTenantSender && ResolveFromAddress(s)` ตรงกับ `EtaxByEmailSenderEmail` (ไม่สนตัวพิมพ์เล็ก/ใหญ่)
   - ไม่ผ่าน ⇒ throw `BusinessRuleException` รหัส `ETAX-EMAIL-SENDER-UNVERIFIED` พร้อมทางไปต่อ ("ตั้ง SMTP/ทดสอบที่ ตั้งค่า > อีเมล" หรือ "ส่งเป็นอีเมลธรรมดา") และ**ไม่**ถอยไปแพลตฟอร์ม
   - ใช้ `GetSenderForSettings` แทน `GetSenderAsync`
3. **ด่านสถานะ** — ห้ามส่งเมื่อเอกสารเป็น Voided/Draft/WaitingApproval/Rejected หรือแถว e-Tax เป็น Voided/Error · บังคับ To เป็น 1 อีเมล ไม่รับ Bcc · ถ้าผู้ใช้ใส่ csemail ใน Cc ให้ตัดซ้ำ · ถ้า `IncludeRdTimestamp=false` ต้องไม่ติดธง `IsEtaxByEmail` (เป็นอีเมลธรรมดา) · ถ้าเคยส่งแล้วต้องมีคำเตือนและให้ยืนยันก่อนส่งซ้ำ
4. **ทางเลือกช่องทางตัวเดียว `EtaxDeliveryRouter`** — ถ้าบริษัทอยู่โหมด ByEmail และเอกสารเป็นชนิดที่ออก e-Tax ได้ (TaxInvoice/Receipt-TaxInvoice/CN/DN · ไม่รวมใบกำกับอย่างย่อ) ให้ทุกทางส่งเอกสาร (`SendDocumentEmailAsync` · `PostPaymentAutomationService.SendReceiptAsync` · `EmailScheduleService` คิว/อนุมัติ/ประจำ) เลือกอย่างใดอย่างหนึ่ง:
   - ส่งต่อไป `SendEtaxByEmailAsync` (สร้างแถว e-Tax ก่อนถ้ายังไม่มี) หรือ
   - ถ้าผ่านด่านข้อ 2 ไม่ได้ ⇒ ล้มดังพร้อมสถานะ Blocked หรือแจ้งเตือน (ห้ามส่งอีเมลธรรมดาแทนเงียบ ๆ)
   - ต่อสาย `EtaxByEmailAutoSendOnApprove` เข้าทางอนุมัติ หรือลบค่าตั้งนี้ออก · ลบ `EtaxByEmailEmbedXml` หรือต่อสายให้มีผล · ล็อกจุดเรียกด้วย `tools/required_call_site_check.py`
5. **ใบที่ประทับเวลาแล้ว** — เพิ่ม `EtaxInvoice.ByEmailSentAt` · `EtdaTimestampedAt` · `EtdaTimestampFilePath` · `EtdaTimestampToken` (ผ่าน `DatabaseMigrationHelper`)
   - ระยะแรก: ให้ผู้ใช้อัปโหลดหรือส่งต่อใบที่ ETDA ส่งกลับ แล้วระบบตรวจกับ validation.etax.teda.th หรือลายเซ็นใน PDF
   - ระยะถัดไป: รับผ่านกล่องจดหมายขาเข้า
   - `DocumentVoidPreconditions.RdTimestampEmailLogs` ต้องแยก "ส่งพร้อม CC แล้ว (รอประทับเวลา)" ออกจาก "ประทับเวลาแล้ว" · ทั้งสองยังกันการยกเลิกเงียบได้ แต่ป้ายต้องบอกความจริง
   - เก็บถาวรตาม §87/3 / ม.10 (`RetentionUntil`)
6. **ตั้งค่า** — ตัด `Both` ออกจากตัวเลือก หรือเปลี่ยนเป็น "ใช้ได้ทีละระบบ" (มี migration ของค่าที่เก็บไว้) · เตือนเมื่อรายได้ปีล่าสุดเกิน 30 ล้าน (`CitRateTable.SmeAnnualRevenueCeiling` เป็นตัวตั้งคนละความหมาย ให้สร้างค่าคงที่ใหม่ใน helper ตัวเดียว)
7. **UI** — `documents.html` ตรวจ `res.success` ทั้งสองจุด · เปลี่ยนเป็น `includeXml:false` · บริษัทโหมด ByEmail ให้ติ๊ก e-Tax เป็นค่าตั้งต้นในหน้าต่างส่งอีเมล · ลบ `sendEtaxEmailOneClick` ที่ตายแล้ว · หน้า e-Tax เปิดปุ่มส่งให้สถานะ Generated ในโหมด ByEmail
8. **เทสต์ (รวมทิศตรงข้าม)**
   - `EtaxByEmailFormatTests`: หัวเรื่อง INV/DBN/CDN/CIV/ใบแทน · วันที่ พ.ศ. ใกล้เที่ยงคืน UTC (+07:00) · ไฟล์แนบเดียว · ไฟล์เกิน 3 MB ถูกปฏิเสธ · โหมดทดสอบได้ที่อยู่ uat
   - `EtaxByEmailSenderGateTests`: (ก) ตั้งครบ + ทดสอบผ่าน + ที่อยู่ตรง ⇒ ส่งจากบริษัท · (ข) **SMTP ยังไม่ทดสอบ ⇒ ปฏิเสธพร้อมคำแนะนำ และไม่เรียก `GetGlobalFallbackSender`** · (ค) ที่อยู่ไม่ตรง ⇒ ปฏิเสธ
   - `EtaxDeliveryRouterTests`: (ก) **บริษัทไม่ได้ใช้โหมด ByEmail ⇒ อีเมลธรรมดา ไม่มี CC ETDA หัวเรื่องปกติ (ทิศตรงข้าม)** · (ข) โหมด ByEmail + ใบกำกับ ⇒ ไปทาง e-Tax · (ค) โหมด ByEmail + ใบเสนอราคา ⇒ อีเมลธรรมดา · (ง) ใบกำกับอย่างย่อ ⇒ อีเมลธรรมดา
   - ทาง DB: ส่งใบ Voided ⇒ ปฏิเสธ · หลังรับชำระในโหมด ByEmail ⇒ มี CC ETDA หรือ Blocked (ห้ามมีอีเมลธรรมดา)
   - `required_call_site_check`: ทุกทางส่งเอกสารต้องเรียก `EtaxDeliveryRouter`
9. **เอกสาร** — อัปเดต `DOCUMENT_FLOW.md` (ช่องทางส่ง e-Tax) · `TEST_PLAN.md` · CLAUDE.md §F ระบุรูปแบบหัวเรื่องและกติกาไฟล์เดียว · แก้ doc-comment ที่ `DocumentEmailService.cs:511-520` ที่อ้างกฎที่ไม่มีอยู่จริง

## หมายเหตุวิธีตรวจ
- อ่านโค้ด ณ working tree ปัจจุบัน (ไม่ได้รัน ไม่ได้คอมไพล์)
- เปิดเว็บ rd.go.th / etax.teda.th / getinvoice.net ไม่ได้ (โดเมนไม่อยู่ใน allowlist ของ network) ⇒ กฎทั้งหมดมาจากข้อความย่อของผลค้นหา · ถ้าต้องการยืนยันจากต้นฉบับ ต้องเปิด `www.rd.go.th` และ `etax.teda.th` ใน network settings
