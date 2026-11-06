# AUDIT_249_FOR_OPUS — ใบสั่งงานรวมจากผลตรวจ 5 ทีม (รอบ 241–249 · HEAD `5a13c6e9`)

> **วิธีใช้เอกสารนี้:** นี่คือเอกสาร*ฉบับเดียว*ที่ agent ผู้ลงมือ (Opus) ต้องอ่านก่อนเริ่มงานรอบ 250 เป็นต้นไป — รวม finding ทั้งหมดจาก
> `erp-review/2026-11-06/audit-249/{A,B,C,D,E}` แล้ว dedupe · ตัดสินให้แล้วทุกข้อที่ทีมตัดสินได้ (§2) · เหลือคำถามเจ้าของเฉพาะเรื่องกฎหมาย/เงิน/สัญญา/ข้อเท็จจริงที่มีแต่เจ้าของรู้ (§3 — ทุกข้อมี "ค่าตั้งต้นถ้าไม่ตอบ" ⇒ **ไม่ต้องรอคำตอบก่อนลงมือ**) ·
> งานเรียงเป็นชุด WP-01… (§4) แต่ละชุดระบุ `file:line` ครบจากรายงานต้นทาง ทำตามลำดับ · **ห้ามทำ WP-02 ขึ้นไปก่อน WP-01 เขียว** (เทสต์ pure ไม่เคยรัน — ทุกคำว่า "มีเทสต์ทิศตรงข้าม" ใน 9 รอบที่ผ่านมายังเป็นแค่โค้ดที่คอมไพล์ผ่าน) ·
> ปิดแถวใดให้ติ๊ก `✅ <sha>` ในรายงานต้นทาง ไม่ลบแถว (ธรรมเนียม erp-review) · doc กับโค้ดขัดกัน = doc ผิด แก้ทันทีในคอมมิตเดียวกัน (CLAUDE.md F2 ข้อ 10)

## 0. สถานะ ณ HEAD `5a13c6e9` (2026-11-06)

| ข้อเท็จจริง | หลักฐาน | ความหมายต่อการลงมือ |
| --- | --- | --- |
| PR #29 (`claude/booking-service-features-sdemob` → base `claude/erp-system-review-team-660mev`) **merged** = `5a13c6e9` | `git log -1` | HEAD ที่ทุก WP อ้างอิง · branch งานถัดไปแตกจากนี่ |
| CI ของ `fe9036f7` (run 42193745876): build ✅ · Db test (`Category=Db`) ✅ · static checks ✅ (12m54s) | A-01 · C §0 · E §0 | CS1501 `CostSourceVoidBlockAsync` แก้แล้ว — ไม่ต้องรายงานซ้ำ |
| **job `dotnet test` (pure-function) ถูก skip ทุก push บน `claude/**`** (`.github/workflows/ci.yml:79-83` `if: github.event_name != 'push' \|\| startsWith(github.ref,'refs/heads/main') …`) และ `pull_request` trigger จำกัด `branches: [main, master]` (`:6-7`) แต่ PR #19–#29 ทุกตัว base ≠ main ⇒ **event ไม่เคยยิง** · run แบบ `pull_request` ล่าสุด = 2026-07-21 (`d674dcf6`) | E H-1 | **เทสต์ pure ~235 ตัว / ~3,400 บรรทัดของรอบ 241–249 (และทุกรอบตั้งแต่ ก.ค.) ไม่เคยรัน** — มีแต่คอมไพล์ ⇒ WP-01 ต้องทำก่อนทุกอย่าง และต้องเผื่อเวลาแก้เทสต์เก่าที่ล้มสะสม |
| `doc_commit_sha_check.py` ไม่อยู่ใน `tools/check_all.sh` (`grep -c` = 0) ทั้งที่ CLAUDE.md F3 ข้อ 4 บอกว่าอยู่ · รันเดี่ยว exit 1 | E H-2 · A A-02 | placeholder sha ค้าง (DOCUMENT_FLOW 8 บรรทัด · CHANGELOG 12 · ACCOUNT_STRUCTURE 8) ไม่มีใครเห็น — ตาราง sha ครบอยู่ WP-05 |
| clone ในเครื่องนี้เป็น shallow (`git rev-parse --is-shallow-repository` = true · 446 คอมมิต) ⇒ checker sha ฟ้องผิด 3–4 sha ที่มีจริงบน GitHub (825f5c0e · 0a829110? · 2560776c · a4dfa177) | A-02 (ฟ้องผิด) **ขัดกับ** E H-2 (บอกว่า 0a829110 เป็น sha ตาย) | **ยึด E สำหรับ 0a829110** (E ตรวจ blame แล้วพบว่าคัดลอกมาจาก `review193-r2-sec-tax.md:54/62` ซึ่งตายอยู่แล้ว) · ยึด A สำหรับที่เหลือ (ตรวจผ่าน `gh api`) ⇒ WP-01 ให้ checker รู้จัก shallow clone + fetch --unshallow ก่อนตัดสิน |
| ไม่มี .NET SDK ในเครื่อง agent | ทุกรายงาน | ทุกรายงานผู้ใช้ต้องบอก "ยังไม่ได้คอมไพล์ในเครื่องนี้" · อ่านผล CI ผ่าน `bash tools/ci_wait.sh` (background) |
| คอมมิตเฉพาะ `**.md`/`erp-review/**` ไม่รัน CI (`paths-ignore`) | ci.yml หัวไฟล์ | WP-05/06 ส่วน doc ล้วน = "ไม่มีรอบ CI ให้รอ" ห้ามรายงานว่าเขียว |

## 1. ตัวเลขสรุป

| ทีม | ขอบเขต | จำนวน finding | แยกความรุนแรง |
| --- | --- | --- | --- |
| A Backlog/Decisions | งานค้าง+คำตัดสินค้างรอบ 233–249 (~~88~~ **95** แถว A-01…K-03 — แก้ตาม AUDIT_249_COVERAGE ข้อ 1) | 95 | **P0** 1 (A-01 — ปิดแล้ว: fe9036f7 เขียว) · **P1** 28 · **P2** 49 · **P3** 16 · ปิดระหว่างตรวจ 1 (K-03) |
| B E2E billing | รายการประจำ→ส่ง→รับเงิน→ออกใบ→แจ้งเตือน | 9 (F-1…F-9) | สูง 1 (F-3) · กลาง 2 (F-1 · F-2) · กลาง-ต่ำ 1 (F-4) · ต่ำ 5 (F-5…F-9) |
| C Tax/Money | สินทรัพย์หลายใบ · WHT ลิงก์ชำระ · e-Tax by Email · แจ้งเตือน · นโยบายอนุมัติ · ปัดเศษ | 9 (F1…F9) | Medium 1 (F1) · Low-Med 2 (F2 · F3) · Low 5 (F4…F8) · Info 1 (F9) · ❌ ลงบัญชีผิดเงียบ = 0 |
| D UX/UI | 10 กลุ่มหน้าที่แก้ใน 3 วัน + cross-cutting | 71 ข้อ + 13 รูปแบบ (A–M) | ร้ายแรง (XSS/ข้อมูลหาย/privacy/silent no-op) 10 ข้อ (= "ทำก่อน 10 ข้อ" D §12) · ที่เหลือ S/M |
| E Quality/Tests/Docs | CI · เทสต์สองทิศ · checker · เอกสาร · multi-instance | 13 (H-1 H-2 · M-1…M-6 · L-1…L-5) | H 2 · M 6 · L 5 |
| **รวม (หลัง dedupe)** | | **~150 แถวงาน → 12 WP** | ถาม**เจ้าของ** 19 ข้อ (§3: ตอบใช่/ไม่ได้ทันที 8 · ต้องหาข้อมูลภายนอก 11) · **ตัดสินให้แล้ว** 41 ข้อ (§2) |

## 2. คำตัดสินที่ทีมตัดสินให้แล้ว (Opus ทำตามนี้ ไม่ต้องถาม)

| # | ประเด็น | คำตัดสิน | เหตุผล 1 ประโยค | ที่มา |
| --- | --- | --- | --- | --- |
| 1 | job `dotnet test` pure บน `claude/**` | **เปิดรันทุก push** (ตัด `if:` ออก) · เทสต์เก่าที่ล้มสะสม = แก้ให้เขียวในรอบเดียวกัน ไม่ `[Skip]` | เทสต์ที่ไม่มี runner = ไม่มีเทสต์ (F2 ข้อ 2) | E H-1 |
| 2 | `doc_commit_sha_check` | ต่อเข้า `check_all.sh` + รู้จัก shallow clone (พิมพ์ "clone ตื้น — fetch --unshallow แล้วตรวจซ้ำ" ไม่ใช่รายการแดง) + นับ placeholder `เติมในคอมมิตตามหลัง` ที่อายุ > 1 คอมมิตเป็น fail | checker ที่ไม่มีใครเรียก = ไม่มี · ฟ้องผิดบน clone ตื้นเกือบทำให้แก้ sha ที่ถูกอยู่แล้ว | E H-2 · M-1 · A-02 |
| 3 | sha `0a829110` (RETENTION_LAW_CHECK.md:190) | ⚠️ **แก้ 2026-11-06 (WP-05): sha มีจริง** — หลัง fetch --unshallow = คอมมิตรอบ 193 ทีม S2 ที่ปิด R2-C1…C4 ⇒ ไม่ต้องแทน (A ถูก · E ผิดเพราะ clone ตื้น) · เดิม: ~~**sha ตาย** — หา sha จริงของ R2-C4 รอบ 193 ด้วย `git log -S"R2-C4" -- erp-review/2026-09-24/` แล้วแทนทั้ง RETENTION และ `review193-r2-sec-tax.md:54/62`~~ | A ว่าพบบน GitHub แต่ E ตรวจ blame พบว่าคัดลอกจากที่ที่ตายอยู่แล้ว — E ตรวจลึกกว่า | E H-2 (ยึด E) |
| 4 | สองทางแก้ F-3 (post-payment ไม่ enqueue) | **ย้าย `EnqueueAsync` + `SyncWhtCreditReceivedAsync` เข้าไปหลัง commit ใน `CreatePaymentAsync` เอง** (1:1 · `source` จากพารามิเตอร์) ไม่ใช่ไล่เติมทีละทางเข้า · `CreateMultiDocPaymentAsync` ยกเว้นโดยตั้งใจ (`SettlementReceiptPolicy`) · CMS/Lodging/PlatformBilling ส่ง `source` ที่ policy ตัดสิน "ไม่ออกใบเสร็จซ้ำ" (มีเส้นออกใบเสร็จของตัวเอง) | ทุกทางเข้าที่แตะข้อมูลชุดเดียวกันต้องเดินด่านเดียว (F3 ข้อ 8) | B F-3 · A B-21/D-02/D-13 |
| 5 | `ResolveLinePostedGlAmountAsync` | ใช้ `PurchaseLineGlAmount.Posted(isCIL, …, payerBorneShare)` · เพิ่ม CIL ใน `purchaseSide` · CIL ที่ `RelatedDocumentId` เป็น PI/Expense คืน 0 เหมือน PV · แยก helper "ส่วนออกแทนต่อบรรทัด" จาก `LoadCostLineFactsAsync` ให้ทั้งสองเส้นเรียก | สองสูตรสำหรับบรรทัดเดียว = defect class "สอง renderer" | C F1 · A C-06 |
| 6 | สูตร VAT ต้องห้ามรวมต้นทุนใน AutoPost CN/DN (`DocumentService.cs:18822-18824`) | แทนด้วย `PurchaseLineGlAmount.Standalone` + กติกา forbid literal `Amount + docLine.VatAmount` ใน DocumentService | ตัวตั้งตัวเดียว | A C-07 |
| 7 | ลำดับล็อก void/purge สินทรัพย์ (TOCTOU) | **ถือคีย์ `AssetCostSource` เสมอ**เมื่อ `AssetCostSourceRules.KindFor(type) != null` ก่อน `FOR UPDATE` แถวใบ — ไม่ตรวจ `AnyAsync` ก่อน | ต้นทุน = advisory lock 1 ครั้งต่อ void ของใบซื้อ · ปิดเส้นที่ "มีเทสต์ไม่ได้" | C F2 · E L-3 |
| 8 | `EmailEnqueueFailed` dead code | `OnRecurringDocumentCreatedAsync` คืน `EnqueueOutcome {Enqueued, Skipped(reason), Failed(reason)}` · ผู้เรียกตั้งธงจากผล · ไม่โยนต่อ | ล้มดัง 3 ที่ — `LogWarning` ไม่ใช่การดัง (F2 ข้อ 7) | B F-1 |
| 9 | ลิงก์/QR เงียบเมื่อไม่ตั้ง `AppBaseUrl` | เพิ่ม Fact `SystemBaseUrlConfigured` ใน `AutopilotReadiness` (รหัส `GATEWAY` Warn + ปุ่มไปหน้าแอดมิน) · `ForScheduledEmailAsync` คืน refusal ให้ `EnqueueDocumentAsync` ใส่ `ErrorMessage` บนแถวคิว · การ์ดเรื่องค้างแอดมิน (`PlatformOps`) เตือนเมื่อว่าง | ด่านที่รายงานผลที่ไม่เกิดจริง (F2 ข้อ 3) | B F-2 · A H-04 |
| 10 | อีเมลซ้ำ 2 กฎ (`DocumentApproved` + `RecurringInvoiceCreated`/`AutoSendEmail`) | ใน `OnRecurringDocumentCreatedAsync` **ข้าม**กฎเสมือน/กฎ `RecurringInvoiceCreated` เมื่อมีกฎ `DocumentApproved` ที่ครอบชนิดนี้และใบอนุมัติแล้ว (ทิศเดียว) + แผงความพร้อมเตือน "มีกฎทั้งสองแบบ" | กฎ `DocumentApproved` เป็นของเดิมที่ใบคีย์มือพึ่งอยู่ — ห้ามแตะฝั่งนั้น | B F-4 |
| 11 | `NotificationEngine.DispatchAsync` คีย์ที่ระบุ `RecipientUserId` | สลับลำดับ: `savedRows.Count > 0 && settings.Count == 0` ⇒ return ก่อนสาขา `RecipientUserId` | คลาสเดียวกับบั๊ก (3) รอบ 249 ที่แก้เฉพาะเส้นปกติ | B F-8 |
| 12 | `IsCashInflowDocAsync` กับ CreditNote | ย้ายเป็น `Helpers/CashFlowDirection.Of(type, relatedType, cnDnSide)` ผ่าน `AdjustmentNoteAccount.ResolveSide` ตั้งแต่ตอนนี้ + เทสต์ตารางครบทุก `DocumentType` | วันนี้ไปไม่ถึง (CN ชำระไม่ได้) แต่ใช้ร่วมกับตัวปรับยอดธนาคาร — กับดักอนาคต | C F8 · A K-02 |
| 13 | ใบที่ตั้ง WHT ให้ผู้ซื้อบุคคลธรรมดา | ยก `WhtMismatchNote` เป็นคำเตือนก่อน**อนุมัติใบ** (ชุด WHT-PAYEE) ไม่ใช่ตอนออกลิงก์ | ต้นเหตุอยู่ตอนสร้างใบ | C F5 |
| 14 | `EtdaTimestampToken` ตัดที่ 500 ตัว | ปฏิเสธพร้อมบอกเหตุ (ไม่ตัดเงียบ) — ไฟล์หลักฐานยังเป็นหลัก | ค่าที่ตัดเงียบ = หลักฐานขาด | C F4 |
| 15 | QR พร้อมเพย์บนพอร์ทัล/pay-link ผ่าน `api.qrserver.com` | เซิร์ฟเวอร์คืน data-URI จาก `PaymentQrService`/`PromptPayService` ตัวเดียวกับรอบ 246 · ลบ TLV builder ใน JS · checker ห้าม `qrserver.com` ทั้ง `wwwroot/` | PDPA/ความลับทางการค้า + server computes | A G-01 · D §9.1 · §10.4 |
| 16 | ลิงก์ชำระของ INV ที่มี TaxInvoice อ้างแล้ว (246-QR-10 N6) | `DocumentPayLinkPolicy.Allowed` **ปฏิเสธ** INV ที่มี TaxInvoice ออกแล้วไม่ยกเลิก + ข้อความ "ชำระที่ใบกำกับ {เลข}" · ตัดสินสด (`LinkUsable`) รวมเงื่อนไขนี้ ⇒ ลิงก์เดิมเพิกถอนในตัว · TaxInvoice ถูกยกเลิก ⇒ ออกได้ (ทิศตรงข้าม) | รับชำระซ้ำ/ใบเสร็จซ้อน §86/4 | A G-02/G-03 |
| 17 | นโยบายอนุมัติข้ามคำเตือน Q3 (248) | **คงตามที่ทำ**: ต่อรายการ + ต่อผู้ติดต่อ · ไม่มีระดับบริษัท · ค่าตั้งต้นปิด | กันปล่อยทั้งบริษัทโดยไม่ตั้งใจ — เจ้าของแค่รับทราบ (§3 ข้อ Y1) | A B-01 |
| 18 | ทางเข้าอนุมัติแบบไม่มีคน (LINE/OCR/ใบเบิก/API/workflow) ไม่ได้สิทธิ์จากนโยบาย | ตั้งใจ · มีเทสต์ · จดเป็น decision 248 | — | A B-22 |
| 19 | 236-3 License หมดอายุ ⇒ บริษัทยังอยู่ใต้ License (อ่านอย่างเดียว · มีปุ่มถอดเอง) · 236-9 ผูกบริษัทจ่ายเองเข้า License: เวลาที่จ่ายไว้เดินต่อ ไม่คืน (เตือนก่อนผูก) | **คงตามที่ทำทั้งสองข้อ** | ง่าย · ไม่สร้างเครดิตที่ต้องติดตาม — เจ้าของแค่รับทราบ (§3 Y2/Y3) | A J-01 · J-02 |
| 20 | สองธงทดสอบ e-Tax (`EtaxTestMode` vs `EtaxByEmailUseUat`) | คงสองธง · แก้ป้าย "โหมดทดสอบ (e-Tax ระบบเต็ม)" / "ทดสอบ e-Tax by Email (UAT)" + hint | ระบบแยกกันจริง | A F-02 |
| 21 | `RevenueCeiling = 30_000_000m` ไม่มีผู้อ่าน | **ลบค่าคงที่** (dead helper) — คำเตือนที่ใช้จริงคือ `RevenueWarning` ที่มีอยู่แล้ว · ไม่สร้างด่านบล็อก (สิทธิจริงมาจาก ก.อ.01) · เพิ่มช่อง "วันที่/เลขอนุมัติ ก.อ.01" เป็น backlog | มี ≠ ถูกเรียก · ค่าในระบบตัดสินสิทธิ RD ไม่ได้ | A F-05 · C §3 |
| 22 | e-Tax `DocumentTypeCode` | โค้ด (`EtaxDocumentTypeMap` 388/80/81/T02/T03) ถูก · CLAUDE.md §F "T01–T04" ผิด ⇒ แก้ CLAUDE.md ให้ชี้ helper · T01 ใบรับเงินล่วงหน้า = backlog | doc ≠ โค้ด = doc ผิด | C F9 |
| 23 | คอลัมน์ตาย `EtaxByEmailEmbedXml/EtaxByEmailAutoSendOnApprove` + `EtaxMode.Both` | จดใน DOCUMENT_FLOW ว่าตั้งใจ · DROP COLUMN ในรอบเก็บกวาด schema | ไม่มีผลเงิน | A F-07 |
| 24 | ค่าเสื่อมงวดแรก = เดือนถัดจากพร้อมใช้ (ASSET Q3) | **คงเดิม** (ไม่ถดถอย) — ถ้าเจ้าของต้องการรายวัน/เดือนเดียวกันค่อยเพิ่มค่าตั้งบริษัท + migration แผนที่ยังไม่โพสต์ | ธรรมเนียมทั้งระบบ · เป็นนโยบายไม่ใช่บั๊ก | A C-03 · C F3 |
| 25 | ดอกเบี้ยระหว่างก่อสร้าง (ASSET Q2) | ค่าตั้งต้น = **ไม่รวมเป็นต้นทุน** (ลงค่าใช้จ่าย) จนกว่าผู้ทำบัญชีตอบ | ทางที่ง่ายและถูกเมื่อไม่มีนโยบาย | A C-02 |
| 26 | วันพร้อมใช้ของสินทรัพย์ใบเดียว (ASSET Q5) | **ทำ**: ช่อง "วันที่พร้อมใช้งาน" แก้ได้จนกว่ามีค่าเสื่อมลงบัญชี · ว่าง = วันที่เอกสาร | พ.ร.ฎ.145 ค่าเสื่อมเริ่มก่อนพร้อมใช้ = หักเกินปีแรก | A C-05 |
| 27 | ราคาตามสัญญาแก้หลังสร้าง (ASSET Q1) | เพิ่มช่องใน modal แก้ไข + `PATCH …/contract-price` (สิทธิ์ `Asset.Manage` · audit) · คง**คำเตือน** ไม่บล็อก | ใส่ผิดแล้วแก้ไม่ได้ = ฟ้องตลอด | A C-01 |
| 28 | ริบมัดจำ (ASSET Q4) · GRN-link · ปลดแถวที่โอนแล้ว | P3 — จด "ตั้งใจ" ใน DOCUMENT_FLOW · ทำเมื่อผู้ใช้ขอ | มีทางไปต่อทุกข้อ | A C-04 · C-08 |
| 29 | `AutoSendDisabled` ต่อผู้ติดต่อ | **ไม่สร้าง `AutoSendSettingsResolver` ตัวที่สอง** — เพิ่มเป็นชั้น "ผู้ติดต่อ" ใน `AutopilotSettingsResolver` · แก้ AUTO_SEND §9 ให้ชี้ตัวเดียว | ตัวตั้งตัวเดียว | A D-03 |
| 30 | แผนคู่ค้าข้ามผู้เช่า: AUTO_SEND เฟส 246 (`TenantPartnerLink`/`WhtCertExchangeOutbox`) vs AUTOPILOT §6 (`TradingPartnership`) vs ของจริง `CrossTenantWorkflow.cs:22 TradingPartnership` | **ต่อยอดจาก `TradingPartnership` ที่มีอยู่** · แก้ AUTO_SEND §1.1 (เพิ่มแถว CrossTenantWorkflow) + G-8 + §7.1 + §9 เฟส 246 ให้ชี้ AUTOPILOT §6 และระบุ "ต่อจากของเดิม" | ห้ามสร้างสำเนาที่สอง (F2 ข้อ 4 · F4 ข้อ 7) — E พบว่า doc ผิดข้อเท็จจริง | E M-6 · A D-09 |
| 31 | AI ตรวจของจริงใบ 50 ทวิ (AUTO_SEND §11 ข้อ 1–2) | `AiFeatureKey.WhtCertAuthenticity` + `GenericFeedbackDistillationModel` · ด่านเชิงกำหนดก่อน (checksum · คู่สัญญา · ยอด · ปีภาษี) · kill-switch ⇒ local ตอบ "ไม่มั่นใจ" = ผู้ใช้กดยืนยัน ไม่ auto | กฎเหล็ก #1 ครบ 4 ขั้น | A D-05 |
| 32 | e-WHT เสนอ 1% อัตโนมัติ (Q-E6) | **ไม่เสนอ** จนกว่ากฎกระทรวงยืนยัน | ค่าที่แต่งขึ้นอันตรายกว่าไม่ตอบ | A D-08 |
| 33 | LINE L2/L3 ระหว่างรอหนังสือจาก LINE | ทำผ่าน **OA ของบริษัทเท่านั้น** · OA กลางส่งเฉพาะแบบ A (1:1 ธุรกรรม) ที่มีอยู่ · `LineFallbackToEmail` ค่าตั้งต้น **true** + `Channels` flags (ตามเจ้าของ Q7) · `X-Line-Retry-Key` ทุก push ใน `LineNotifyService.PushCoreAsync` · เงื่อนไข C2/C3/C5 ตั้งแต่แรก · ไม่ตัดโควตาผู้เช่าด้วย reply | ไม่เสี่ยง OA กลางถูกระงับ | A E-02 · E-03 · E-08 · E-09 · E-12 · E-13 · E-10 |
| 34 | โควตาแยกช่อง (L4) | ค่าตั้งต้นตาม NOTIFY §5 (LINE 100/300/1,000/5,000 · อีเมล 300/1,500/6,000/−1) · ปิดขาย `notify.topup` ก้อนรวมเมื่อสองแพ็กใหม่เปิด · **แต่ตั้งเพดานอีเมลได้หลังเจ้าของรัน SQL NOTIFY §4** เท่านั้น (§3 N5) | ลูกค้าเดิมสะดุด | A E-04 · E-14 |
| 35 | หน่วยนับกลุ่ม LINE `max(1, memberCount − 1)` | ทดสอบจริง 10 นาทีบน OA ทดสอบก่อน L3/L4 แล้วบันทึกใน NOTIFY §5 | น่าจะนับต่ำไป | A E-11 |
| 36 | `PartnerDiscoverable` Q1 · `BankReceiptAutoSettle` Q4 · ใบเสนอราคาแปลง Q5 · อีเมลสถานะ Q7 · ส่งออกนักบัญชี Q9 | ตามข้อเสนอทีมทั้งหมด: ไม่เปิดเผย · ปิด+จำลอง 30 วัน · `Off` · ล้ม=ทันที สำเร็จ=digest 08:40 · Excel ทั่วไปก่อน | ไม่มีความเสี่ยงเงิน/กฎหมาย | A B-02 B-04 B-05 B-07 B-09 |
| 37 | คำเตือนวงเงินเครดิต Q6 | **ทำ** เป็นคำเตือน `[CREDIT-LIMIT]` ใน `AutoApprovePolicy.Catalogue` (ปล่อยได้ด้วยนโยบาย) + ทิศตรงข้าม | ลูกค้าเกินวงเงินยังออกใบประจำเงียบ | A B-06 |
| 38 | `DocumentPurgeDbTests.ProductionFkShapeAsync` ALTER TABLE บนฐานร่วม | ย้ายไป `DbTestDatabase` static init (ฐานเทสต์ = รูป production เสมอ) | flaky เชิงโครงสร้าง | E M-4 |
| 39 | `PaymentQrMigration` คีย์ `hashtext(markerKey)` | เปลี่ยนเป็น `AdvisoryLockKey.For(OneShotDataFix, MarkerKey)` + ย้าย `CREATE TABLE OneShotDataFixes` ไป `DatabaseMigrationHelper` กลาง + สอน `advisory_lock_key_check` ฟ้อง `hashtext(` | สูตรคีย์สองสำเนา | E M-3 · §7 |
| 40 | ข้อความคอมมิต `369bc403` ผิดหัวเรื่อง | **ไม่ amend/rewrite** — `git notes add 369bc403` + บทเรียน `docs/lessons/process-docs.md` | push แล้ว | E M-2 · A K-01 |
| 41 | RETENTION 150 จุด (I-01) | **ลงมือได้เลย** ตามถ้อยคำฝ่ายค้าน GO ทั้ง A–D (WP-06) ไม่ต้องรอเจ้าของเปิด PDF (I-02) — ถ้าผลต่างค่อยแก้ตาม · audit row เก่าห้าม migrate · doc ประวัติไม่แก้ย้อนหลัง | ฝ่ายค้านล้มคำอ้างหลักไม่ได้ | A I-01/I-02 · RETENTION §ฝ่ายค้าน |

## 3. คำถามที่เหลือให้เจ้าของ (ส่งเป็นชุดเดียวในสรุปรอบ 250 · ทุกข้อมีค่าตั้งต้น ⇒ ไม่บล็อกงาน)

### 3.1 ตอบ "ใช่/ไม่" ได้ทันที (8 ข้อ)

| # | คำถาม | ค่าตั้งต้นถ้าไม่ตอบ (ทีมเลือกแล้ว) | บล็อกอะไร | ที่มา |
| --- | --- | --- | --- | --- |
| Y1 | นโยบายอนุมัติข้ามคำเตือน: ตั้งได้ต่อรายการ+ต่อผู้ติดต่อ **ไม่มีระดับบริษัท** ค่าตั้งต้นปิด — ใช่ไหม | ใช่ (คงตามโค้ด 248) | ไม่บล็อก | A B-01 (AUTOPILOT Q3) |
| Y2 | 236-3: License หมดอายุ ⇒ บริษัทที่ผูกยังอยู่ใต้ License (อ่านอย่างเดียว · ถอดเองได้) — ใช่ไหม | ใช่ (คงตามรอบ 238) | ไม่บล็อก | A J-01 |
| Y3 | 236-9: ผูกบริษัทที่จ่ายเองเข้า License ⇒ เวลาที่จ่ายไว้เดินต่อ ไม่คืน/ไม่เป็นเครดิต (เตือนก่อนผูก) — ใช่ไหม (ถ้าอยากคืน = เครดิตผ่าน `PlatformProration.RemainingDays` งาน M) | ใช่ | ไม่บล็อก | A J-02 |
| Y4 | รับทราบ: e-Tax มี**สองธงทดสอบ**แยกกัน (ระบบเต็ม vs by Email UAT) — ป้ายจะถูกแก้ให้ชัด | รับทราบ | ไม่บล็อก | A F-02 |
| Y5 | คู่ค้า NextAcc เป็นฟีเจอร์**ทุกแพ็กเกจ** + เพดานจำนวนคู่ต่อแพ็กเกจ — ใช่ไหม (ถ้าไม่ บอกแพ็กเกจ/เพดาน) | ทุกแพ็กเกจ ไม่มีเพดาน | รอบ 251 AUTOPILOT (`PlanTemplate`) | A B-10 (Q10) |
| Y6 | เผยแพร่ข้อกำหนด**ฉบับใหม่**ที่มีข้อ "การส่งอีเมลในนามท่าน" (`LegalDocumentDefaults.cs:220` + `.En.cs:159`) ใน `/admin/legal.html` (กด "ใส่คำแปลตั้งต้น" แล้วเผยแพร่) — ทำได้เลยไหม | ยังไม่เผยแพร่ ⇒ ระบบเพิ่มแบนเนอร์ "ฉบับตั้งต้นมีข้อใหม่ที่ฉบับเผยแพร่ยังไม่มี" (WP-03) · รวมกับฉบับที่แก้เลขมาตรา (WP-06) ให้เผยแพร่ครั้งเดียว | PDPA ม.40 (ลูกค้าปัจจุบันยังไม่ยอมรับข้อนี้ขณะที่ระบบส่งในนามบริษัทอยู่แล้ว) | A H-01 |
| Y7 | ops: ตั้ง `SiteSettings.AppBaseUrl` ใน `/admin/site-settings.html` แล้วหรือยัง (1 นาที) | ยังไม่ตั้ง ⇒ LINE/อีเมล/QR ไม่มีลิงก์ (ตั้งใจ) + การ์ดเตือนแอดมิน (WP-02) | ลิงก์ชำระ/QR ทุกฉบับ | A H-04 · B F-2 |
| Y8 | ops: โฮสต์โปรดักชันมี **egress ตรง** (ไม่บังคับ proxy) ใช่ไหม — `OutboundUrlGuard.cs:105 UseProxy=false` | ใช่ (จดใน OMISE_READINESS §3/deploy notes "webhook ขาออกไม่ใช้ proxy ระบบ") · ถ้าต้องผ่าน proxy = งานใหม่ resolve+pin IP ผ่าน proxy | webhook/callback/ลิงก์เซ็น | A H-03 |

### 3.2 ต้องหาข้อมูลภายนอก / ส่งต่อผู้ทำบัญชี-ทนาย-ops (11 ข้อ)

| # | คำถาม | ส่งต่อใคร | ค่าตั้งต้นถ้าไม่ตอบ | บล็อกอะไร | ที่มา |
| --- | --- | --- | --- | --- | --- |
| N1 | **รหัสหัวเรื่อง e-Tax by Email**: ใบลดหนี้ = `CDN` หรือ `CRN` · ใบเสร็จ/ใบกำกับใช้ `RCT` ไหม — เทียบคู่มือ "e-Tax Invoice by Email" ฉบับล่าสุด (`Helpers/EtaxByEmailFormat` · `EtaxDocumentTypeMap`) **หรือ** อนุญาตโดเมน `www.rd.go.th`, `etax.teda.th` ใน network ของ session ให้ agent ตรวจซ้ำ | ผู้ทำบัญชี / ops (network) | คงรหัสปัจจุบัน (ออกจากผลค้นหา · merge แล้ว) + tooltip "ยังไม่เทียบคู่มือฉบับจริง" | **ภาษี**: รหัสผิด ⇒ ETDA ไม่ประทับเวลาทั้งชุด | A F-01 |
| N2 | **ตัวบท พ.ร.บ.การบัญชี 2543**: เปิด PDF จาก dbd.go.th ยืนยัน ม.10/ม.14/ม.19 ด้วยตา 2 นาที **หรือ** อนุญาตโดเมน `www.dbd.go.th`, `www.rd.go.th`, `ratchakitcha.soc.go.th`, `krisdika.go.th` | เจ้าของ / ops | ทำ WP-06 ตามฝ่ายค้าน GO ไปก่อน · ผลต่างค่อยแก้ | ไม่บล็อก | A I-02 |
| N3 | **ภาษีซื้อผ่านแพลตฟอร์ม (L-2)**: ใบกำกับที่ส่งผ่าน NextAcc↔NextAcc ใช้ลงภาษีซื้อได้เมื่อใด (§82/5(1)) + ข้อแลกเปลี่ยนข้ามผู้เช่าใน DPA (PDPA ม.40) | ผู้ทำบัญชี + ฝ่ายกฎหมาย | รอบ 252 `BillAutoPost` ส่งใบซื้อเป็น**ร่าง** + `PaperRequired` (ไม่ลงภาษีซื้อเอง) · ค่าตั้งต้นปิด · เพดานยอดต่อคู่ | รอบ 252 AUTOPILOT | A B-11 · B-03 |
| N4 | **e-WHT หลักฐาน**: Q-E1 รูปแบบหลักฐานที่เจ้าพนักงานรับ · Q-E2 ปุ่มส่งออกบน epay.rd.go.th · Q-E5 คำบ่งชี้บน statement แต่ละธนาคาร (ขอภาพหน้าจอ 1 ชุด) · Q-E3 เลขกฎกระทรวง 2569 ลด 1% · Q-E4 อัตรา ม.ค.–ก.ย. 2569 | ผู้ทำบัญชี / ลูกค้าจริง / ราชกิจจาฯ | ผู้ใช้แนบ e-WHT เป็น "ใบ 50 ทวิ" ธรรมดา · **ไม่เสนอ 1% อัตโนมัติ** | WP-10 ส่วน e-WHT (E1/E7/E8) | A D-08 |
| N5 | **เบี้ยปรับ/เงินเพิ่ม**: (4) เลขระเบียบกรมสรรพากรตารางลดเบี้ยปรับ ภ.พ.30 2/5/10/20% (5) ภ.พ.36 ใช้ตารางเดียวกันไหม (6) กติกาเสียสิทธิ์ขยาย e-Filing (2) อัตราเงินเพิ่มกองทุนเงินทดแทน 3%/เดือน | ผู้ทำบัญชี | คงเป็นค่า "เสนอ" + tooltip "ยังไม่ยืนยันเลขระเบียบ" | ไม่บล็อก (ระบบไม่ลงเอง) | A I-04 |
| N6 | **ดอกเบี้ยระหว่างก่อสร้าง** รวมเป็นต้นทุนสินทรัพย์ไหม (TFRS NPAEs บทที่ 10 — นโยบายบัญชี) | ผู้ทำบัญชี | ไม่รวม (ลงค่าใช้จ่าย) | ไม่บล็อก | A C-02 |
| N7 | **ราคาแพ็ก**: LINE 1,000 ข้อความ ฿290 (ทีม) หรือ ฿390 (design) · อีเมล 5,000 ฿190 (ถ้าผู้ให้บริการแพงกว่า ฿0.02/ฉบับ ⇒ 2,000/฿190) · **ผู้ให้บริการอีเมลโปรดักชันคือเจ้าไหน** (ตั้งที่ `admin/system-email.html` ไม่อยู่ใน repo) · เปิด OA Manager ดูแผนจริง @nextacc | เจ้าของ | LINE ฿290 · อีเมล ฿190/5,000 | WP-11 L4 | A E-07 |
| N8 | **หนังสือถึง LINE ประเทศไทย** (ร่างใน LINE_OA_TERMS_CHECK §7 ข้อ 2) + ทนายเปิดตัวบทเต็ม 3 ฉบับยืนยัน T1–T7/G1–G3/A1–A3 — ส่ง/จ้างไหม · ตรวจประกาศ "reply ฟรี 25,000/เดือน/OA ตั้งแต่ 1 ก.พ. 2570" ใน OA Manager/อีเมลจาก LINE | เจ้าของ / ทนาย | L2/L3 ใช้ OA บริษัทเท่านั้น · OA กลางเฉพาะแบบ A · เพิ่มตัวนับ reply/เดือนใน `NextAccOaQuotaWatch` | L3 ผ่าน OA กลาง | A E-08 · E-10 |
| N9 | **SQL NOTIFY §4** บนโปรดักชัน (P95 อีเมล/เดือนจาก `DocumentEmailLogs` — 5 แถว) | ops | ยังไม่ตั้ง `MaxSharedEmailsPerMonth` · บริษัทที่เกิน ⇒ ทับรายบริษัท `ceil(peak×1.2)` | WP-11 L4 ส่วนอีเมล | A E-14 |
| N10 | **ถ้อยคำข้อยกเว้น** "ติดต่อตามธุรกรรม/สัญญาที่มีอยู่ก่อน" ตามประกาศ ก.ดิจิทัลฯ (พ.ร.บ.คอมพิวเตอร์ ม.11) ก่อนอ้างต่อลูกค้า | ทนาย 1 คำถาม | **ห้าม**ใส่ข้ออ้างมาตราในอีเมลถึงลูกค้า (คง Reply-To + ช่องทางบอกไม่รับ) | ไม่บล็อก | A H-02 |
| N11 | **สถานะ go-live Omise 10 ข้อ** (OMISE_READINESS §3): `AppBaseUrl` · คีย์ test→live · สวิตช์หน้าซื้อสาธารณะ · แพ็กเกจ IsActive/ราคา · เผยแพร่นโยบาย 3 ฉบับ+EN · ข้อมูลติดต่อ · อีเมลระบบ · ทดลองครบลูป · ส่ง URL ให้ Omise · `PaymentLiveGate` + รายชื่อ**ธนาคาร/feed ที่ลูกค้าใช้จริง** (1 บรรทัด) | เจ้าของ (ค่าอยู่ในฐานโปรดักชัน) | ระบบ: ตรวจว่าการ์ดเรื่องค้างแอดมินครอบ 10 ข้อ (WP-02) · ธนาคารไม่รู้ ⇒ นำเข้า statement รายวัน | รับเงินจริง · รอบ 250 AUTOPILOT | A J-04 · B-08 |

### 3.3 คำตัดสินทั้ง 19 ข้อ (เจ้าของมอบให้ตัดสินเอง 2026-11-06 · Opus ทำตามนี้ · เจ้าของแย้งได้ทีหลัง — ทุกข้อออกแบบให้กลับทางได้)

หลักที่ใช้ตัดสิน: (1) ห้ามประทับสถานะ/ค่าทางกฎหมายที่ยังไม่ยืนยัน (F2 ข้อ 3) · (2) สิ่งที่ระบบทำแทนเจ้าของไม่ได้ (เผยแพร่ข้อกำหนด · ส่งหนังสือ · ตั้งค่าโปรดักชัน)
⇒ ระบบทำทุกอย่างจนเหลือ "กดปุ่มเดียว" + แจ้งเตือนดังที่หน้าแอดมิน · (3) ค่าตั้งต้นเลือกทางที่ปลอดภัยต่อเงิน/ภาษีของลูกค้า

| # | คำตัดสิน | เหตุผล | งานที่เกิด (WP) |
| --- | --- | --- | --- |
| Y1 | **ใช่** — นโยบายอนุมัติข้ามคำเตือน ต่อรายการ + ต่อผู้ติดต่อ (ต้องรับรองรายการ) · ไม่มีระดับบริษัท · ค่าตั้งต้นปิด | ระดับบริษัทเท่ากับปิดด่านทั้งบริษัทในคลิกเดียว — เสี่ยงเกินประโยชน์ | ไม่มี (คงโค้ด 248) |
| Y2 | **ใช่** — License หมดอายุ ⇒ บริษัทยังผูก (อ่านอย่างเดียว · ถอดเองได้) | ไม่ทำให้ข้อมูลลูกค้าหลุดจาก License เงียบ ๆ | ไม่มี |
| Y3 | **ใช่** — ผูกบริษัทที่จ่ายเองเข้า License ⇒ เวลาที่จ่ายไว้เดินต่อ ไม่คืน/ไม่เป็นเครดิต · **ต้องเตือนก่อนกดผูก** พร้อมจำนวนวันที่เหลือ (ตรวจว่ามีแล้ว · ถ้าไม่มี เพิ่มใน WP-13) | คืนเครดิตต้องมีบัญชีเครดิตแพลตฟอร์ม (งาน M) ไม่คุ้มก่อนมีผู้ขอจริง | WP-13 (ตรวจคำเตือน) |
| Y4 | **คงสองธง** + แก้ป้ายตามคำตัดสินข้อ 20 | ระบบเต็มกับ by Email ทดสอบคนละปลายทาง | WP-03 (ป้าย) |
| Y5 | **ทุกแพ็กเกจ · ไม่มีเพดาน** ในรอบ 251 · เตรียมช่อง `PlanTemplate.MaxTradingPartners` (null = ไม่จำกัด) ให้แอดมินตั้งภายหลังได้โดยไม่ deploy | การเชื่อมคู่ค้าคือแรงดึงให้ลูกค้าของลูกค้ามาใช้ (network effect) — จำกัดตอนนี้ทำร้ายการเติบโต | WP-12 (251) |
| Y6 | **ระบบเตรียมฉบับใหม่ครบในร่างเดียว** (ข้อ "ส่งอีเมลในนามท่าน" + ข้อแลกเปลี่ยนข้ามผู้เช่า (N3) + เลขมาตราที่แก้ใน WP-06) · แบนเนอร์แอดมิน "ฉบับตั้งต้นมีข้อใหม่ที่ฉบับเผยแพร่ยังไม่มี" + ปุ่มไปหน้าเผยแพร่ · **การกดเผยแพร่เป็นของเจ้าของ** (ระบบประทับการยอมรับแทนลูกค้าไม่ได้) | PDPA ม.40 · ข้อกำหนดต้องมาจากเจ้าของบริการ | WP-03 (แบนเนอร์) · WP-06 (รวมร่าง) |
| Y7 | **ถือว่ายังไม่ตั้ง** จนระบบตรวจเห็นเอง: เพิ่มการ์ด "URL ของระบบยังไม่ได้ตั้ง — ลิงก์ชำระ/QR/ลิงก์ใน LINE-อีเมลจะไม่ถูกแนบ" บนแดชบอร์ดแอดมิน + Fact `GATEWAY` ในแผงความพร้อม (คำตัดสินข้อ 9) · ไม่เดาโดเมน | ค่าที่แต่งขึ้น (โดเมนผิด) อันตรายกว่าไม่ใส่ลิงก์ — เคยเกิดแล้ว (app.nextacc.com) | WP-02 |
| Y8 | **ถือว่ามี egress ตรง** (คง `UseProxy=false`) + เพิ่มปุ่ม "ทดสอบการเชื่อมต่อขาออก" ในหน้าแอดมินที่ยิง HEAD ไป `https://api.omise.co` ผ่าน `OutboundUrlGuard` แล้วบอกผล — ถ้าล้ม การ์ดแอดมินแดงพร้อมคำอธิบาย | ตรวจได้จริงด้วยเครื่องเอง ไม่ต้องรอคำตอบ | WP-02 (ปุ่ม + การ์ด) |
| N1 | **คงรหัสปัจจุบัน** + ย้ายตาราง "ชนิดเอกสาร → รหัสหัวเรื่อง" ไปเป็นค่าตั้งของแพลตฟอร์มที่แอดมินแก้ได้ (ค่าตั้งต้น = ค่าปัจจุบัน) + tooltip "ยังไม่เทียบคู่มือฉบับจริง" ในหน้าแอดมิน · agent ลอง WebFetch คู่มือ ETDA ใน WP-03 ถ้า network อนุญาต | ถ้ารหัสผิดแก้ได้ใน 1 นาทีโดยไม่ deploy · ไม่ต้องรอผู้ทำบัญชี | WP-03 |
| N2 | **ทำ WP-06 ตามฝ่ายค้าน GO ทันที** · agent ลอง WebFetch ตัวบทจาก krisdika/dbd ถ้าได้ ให้เทียบแล้วจดผล | ฝ่ายค้านยืนยันแล้ว · ผลต่าง (ถ้ามี) แก้เพิ่มได้ | WP-06 |
| N3 | **ใบที่ส่งผ่านแพลตฟอร์มเข้าเป็นใบซื้อ "ร่าง" เสมอ** · `InputVatEvidence`: ต้นทางเป็น e-Tax ที่ลงลายมือชื่อ + มีประทับเวลา ETDA ⇒ `EtaxVerified` (ลงภาษีซื้อได้) · ต้นทางเป็น e-Tax ยังไม่ประทับ ⇒ `AwaitingTimestamp` (ห้ามลง) · อื่น ๆ ⇒ `PaperRequired` (ห้ามลงจนกว่าผู้ใช้ยืนยันว่าได้ต้นฉบับ) · ข้อแลกเปลี่ยนข้ามผู้เช่าเข้าร่างข้อกำหนด Y6 | §82/5(1)+§86/4 ต้องมีต้นฉบับใบกำกับ — ไฟล์ e-Tax ที่ลงลายมือชื่อคือต้นฉบับ (ขมธอ.3-2560) ส่วน PDF ที่แพลตฟอร์มเรนเดอร์ไม่ใช่ | WP-12 (252) |
| N4 | **e-WHT = แนบเป็นหลักฐาน 50 ทวิ ธรรมดา** · ไม่เสนออัตรา 1% อัตโนมัติ · ช่อง "เลขอ้างอิง e-WHT" (ถ้ามี) เก็บเป็นข้อความ | ไม่รู้รูปแบบที่เจ้าพนักงานรับ ⇒ ไม่แต่ง | WP-10 |
| N5 | **คงเป็นค่า "เสนอ"** + tooltip "ยังไม่ยืนยันเลขระเบียบ" · ระบบไม่ลงบัญชีเบี้ยปรับเอง | ผู้ใช้ตัดสินตอนยื่นจริง | WP-13 (tooltip ถ้ายังไม่มี) |
| N6 | **ไม่รวมดอกเบี้ยเป็นต้นทุน** (ลงค่าใช้จ่าย) · ทางไปต่อ: ผู้ใช้ผูกบรรทัด JE ดอกเบี้ยเข้าสินทรัพย์ระหว่างจัดหาด้วยมือได้ถ้าบัญชีอยู่ในผัง 12280 (ไม่ต้องสร้างฟีเจอร์ใหม่) | ไม่มีนโยบายบัญชีที่ยืนยัน ⇒ ทางอนุรักษ์ · ไม่ปิดทาง | ไม่มี (จดใน DOCUMENT_FLOW) |
| N7 | **LINE ฿290/1,000 ข้อความ · อีเมล ฿190/5,000** เป็นค่าตั้งต้นของแพ็กในหน้าแอดมิน (แก้ได้โดยไม่ deploy) · ผู้ให้บริการอีเมล = ค่าใน `admin/system-email.html` (ระบบอ่านต้นทุน/ฉบับจากค่าตั้งนั้น ถ้าไม่มีให้เพิ่มช่อง `CostPerEmailThb`) | ราคาเป็นการตัดสินทางธุรกิจที่เปลี่ยนได้ — ระบบต้องไม่ล็อก | WP-11 (L4) |
| N8 | **L2/L3 ผ่าน OA ของบริษัทเท่านั้น** · OA กลางส่งเข้า "กลุ่ม" ได้เมื่อแอดมินเปิดสวิตช์ `CentralOaGroupDeliveryEnabled` (ค่าตั้งต้น **ปิด** — เปิดหลังได้หนังสือยืนยันจาก LINE) · ห้ามส่งเข้ากลุ่มกลางของแอดมินเด็ดขาด (§10 Q1b) · เพิ่มตัวนับ reply/เดือนใน `NextAccOaQuotaWatch` | ทำตามคำตอบเจ้าของ §10 Q1 ได้ทันทีที่ได้หนังสือ โดยไม่ deploy | WP-11 |
| N9 | **ไม่ตั้งเพดานรวม** `MaxSharedEmailsPerMonth` · ทับรายบริษัทได้ | ยังไม่มีข้อมูล P95 | WP-11 |
| N10 | **ห้ามใส่ข้ออ้างมาตราในอีเมลถึงลูกค้า** · คง Reply-To ของบริษัท + ลิงก์ "ไม่ต้องการรับอีเมลนี้" | อ้างกฎหมายผิดเสี่ยงกว่าไม่อ้าง | ไม่มี (ตรวจว่าไม่มีข้อความอ้าง) |
| N11 | **ระบบทำ "รายการตรวจ go-live 10 ข้อ" เป็นการ์ดบนแดชบอร์ดแอดมิน** (แต่ละข้อตรวจจากข้อมูลจริง ✅/❌ + ปุ่มไปแก้) · ธนาคาร/feed ไม่รู้ ⇒ ค่าตั้งต้นเส้นโอน = นำเข้า statement + จับคู่เสนอ (ไม่ปิดหนี้เอง) | เจ้าของเห็นสิ่งที่เหลือในหน้าเดียว | WP-02 |

**ผลต่อแผน:** ไม่มีคำถามใดบล็อกงานพัฒนาอีก · สิ่งที่ยังต้องเป็นมือเจ้าของมี 3 อย่าง (ระบบเตรียมให้เหลือกดเดียว): กดเผยแพร่ข้อกำหนดฉบับใหม่ (Y6) · ตั้ง URL ระบบ/คีย์ live (Y7/N11) · ส่งหนังสือถึง LINE แล้วเปิดสวิตช์ (N8)

## 4. ชุดงานสำหรับ Opus (ทำตามลำดับ · ทุก WP ปิดด้วย `bash tools/check_all.sh` + CI เขียว + ตอบ F3 ข้อ 7–12 ในข้อความคอมมิต)

> สัญลักษณ์: 🖥 = ต้องแก้ฝั่งเซิร์ฟเวอร์ · **R** = กติกา `tools/required_call_site_check.py` ที่ต้องเพิ่ม · **T±** = เทสต์ทิศบวก/ทิศตรงข้าม · ฝ่ายค้าน = ส่ง diff ให้ subagent ฝ่ายค้าน 1 รอบ (3 คำถาม: ทางเข้าอื่น? ทิศตรงข้าม? สถานะปลายทางประทับเองไหม?) ก่อน push

---

### WP-01 · เปิดรันเทสต์ pure ใน CI + ซ่อม checker ที่หลุดสาย (ทำก่อนทุกอย่าง · effort ½–1 วัน รวมแก้เทสต์สะสม)

**เป้าหมาย:** ทุกเทสต์ใน `Accounting.Tests` ถูกรันจริงทุก push บน `claude/**` · `check_all.sh` เรียก checker ครบตามที่ CLAUDE.md อ้าง · checker ไม่ฟ้องผิดบน clone ตื้น

| # | รายการแก้ | ที่ | ที่มา |
| --- | --- | --- | --- |
| 1 | ตัด `if:` ของ job `test` (ชื่อ "dotnet test") ออก (หรือ `if: always()` ไม่ใช่ — ต้องยัง `needs: build`) · **ห้าม**แก้ `paths-ignore`/`concurrency` | `.github/workflows/ci.yml:79-83` | E H-1 |
| 2 | push แล้วรอ `bash tools/ci_wait.sh` (background) → job test แดง = `get_job_logs` → แก้เทสต์เก่าที่ล้มสะสมตั้งแต่ ก.ค. (คาดว่ามี) **ให้เขียวจริง ห้าม `[Skip]`/`[Fact(Skip=…)]`** · ถ้าเทสต์ล้มเพราะด่านเปลี่ยนโดยตั้งใจหลังเขียนเทสต์ ⇒ แก้เทสต์พร้อมอ้างคอมมิตที่เปลี่ยนด่าน · ถ้าล้มเพราะโค้ดผิด ⇒ แก้โค้ด (= บั๊กจริงที่ซ่อนอยู่ 4 เดือน — รายงานเจ้าของแยกข้อ) | — | E H-1 |
| 3 | ต่อ `python3 tools/doc_commit_sha_check.py` เข้า `tools/check_all.sh` (ตำแหน่งเดียวกับ checker doc อื่น) | `tools/check_all.sh` (`grep -c doc_commit_sha` = 0) | E H-2 |
| 4 | `doc_commit_sha_check.py`: (ก) ตรวจ `git rev-parse --is-shallow-repository` — ถ้า true ให้ `git fetch --unshallow` (หรือ `--deepen`) ก่อน · ถ้า fetch ไม่ได้ พิมพ์ "clone ตื้น — เชื่อผล CI" และ exit 0 สำหรับ sha ที่ `git cat-file -e` ไม่เจอ (ไม่ใช่รายการแดง) · (ข) นับ placeholder `commit <เติมในคอมมิตตามหลัง>` ใน DOCUMENT_FLOW/CHANGELOG/ACCOUNT_STRUCTURE/TEST_PLAN เป็น **fail** เมื่อบรรทัดนั้น (`git blame`) อายุ > 1 คอมมิตนับจาก HEAD · (ค) baseline 2 จุด pre-existing ถ้าหา sha จริงไม่ได้: `erp-review/2026-10-14/PAYMENT_CHANNEL_AUDIT.md:375,377` (`2560776c` · `c382580d`) | `tools/doc_commit_sha_check.py` | A A-02 · E H-2 · M-1 |
| 5 | negative test ของ checker (ข้อ 4ข): ใส่ placeholder ปลอมในไฟล์ชั่วคราวที่ blame เก่า ⇒ ต้องฟ้อง · ใส่ sha ที่มีจริง ⇒ ไม่ฟ้อง | self-test ใน checker เดียวกัน | F2 ข้อ 6 |
| 6 | ลบ baseline ค้าง 4 แถวที่ด่านมีแล้ว: `ScheduledReportController.SendNow` (:462) · `StampDutyController.MarkPaid` (:479) · `SubscriptionController.SubmitPayment` (:486) · `WithholdingTaxCertController.Void` (:534) | `tools/write_permission_gate_baseline.txt` | E M-5 |
| 7 | ตัด 4 แถวจาก `dead_helper` baseline ที่กลับมามีผู้เรียก: `PndTextFileFormat.DetailRow/SplitName` · `Pp30SalesClassifier.ShouldReport` · `ThaiPitCalculator.ExpenseDeduction` | baseline ของ `tools/dead_helper_check.py` | E §5 |
| 8 | สอน `advisory_lock_key_check` ฟ้อง `hashtext(` ภายใน `pg_advisory_xact_lock(` (+ negative test) — ตัวที่ฟ้องคือ `Helpers/PaymentQrMigration.cs:68` (แก้โค้ดใน WP-04) | `tools/advisory_lock_key_check.py` | E M-3 |
| 9 | `namespace_shadow_check` สแกน 64,707 ไฟล์ (กวาด `bin/obj`) ⇒ exclude `bin/`, `obj/`, `node_modules/` | `tools/namespace_shadow_check.py` | E §5 |
| 10 | แก้ CLAUDE.md 2 ข้อความให้ตรงความจริง**หลัง**ข้อ 1–3 เสร็จ: §F3 ข้อ 4 "doc sha อยู่บน branch (doc_commit_sha_check)" (คงได้เพราะต่อสายแล้ว) · §วิธีทำงาน "CI บน `claude/**` คือ compiler ตัวแรก" → "compiler + runner ของเทสต์ pure ตัวแรก" · และ §F3 ข้อ 6 ระบุ 3 job (static/build/test/db-test) ที่ต้องเขียวครบ | `CLAUDE.md` | E §6.3 |

**เทสต์:** ไม่มีเทสต์ใหม่ — แต่ "ทุกเทสต์ที่มีอยู่ต้องรันและเขียว" คือ definition of done · บันทึกจำนวนเทสต์ที่รัน (`Passed: N`) ลง CHANGELOG รอบ 250
**เอกสาร:** CLAUDE.md (ข้อ 10) · `docs/lessons/checker-writing.md` §"รายการ checker ทั้งหมด" เติม `doc_commit_sha_check` + `advisory_lock_key_check` และระบุว่าอยู่ใน `check_all.sh` แล้ว · CHANGELOG รอบ 250 (ประวัติ)
**Definition of done:** CI ของคอมมิตสุดท้ายใน WP นี้ **4 job เขียว** (static · build · **test** · db-test) · `python3 tools/doc_commit_sha_check.py` exit 0 ในเครื่อง (หลัง unshallow) และใน CI · ข้อความคอมมิตตอบ "เทสต์ pure รัน N ตัว ผ่าน N" เป็นตัวเลข

---

### WP-02 · บั๊กเงิน/ความถูกต้อง P0–P1 (B + C + A G/H) · effort 2–3 วัน · **ฝ่ายค้าน 1 รอบ**

> **สถานะ:** ✅ a40c91e5 (โค้ด+เทสต์+กติกา R250+doc · ยังไม่ได้คอมไพล์ในเครื่องนี้ — รอ CI) · ไม่ได้ทำ: Db `Void_while_linking_holds_key_first` · `SettleYearEnd_locks_in_id_order` (ล็อกด้วยกติกา R250 แทน) · `R248_MED5_*` (WP-07) · ฝ่ายค้าน = ทบทวนเอง (ไม่มีเครื่องมือ subagent)

**เป้าหมาย:** ปิดทุกจุดที่ "ค่าตั้งเป็น silent no-op" หรือ "สองสูตรสำหรับเรื่องเดียว" ในเส้นเงิน — ทุกทางเข้ารับชำระเดินงานหลังรับชำระ · GL เดียวต่อบรรทัด · ลิงก์ชำระไม่ออกให้ INV ที่ย้ายลูกหนี้แล้ว · ล้มดังทุกจุดที่เคยเงียบ

#### 2.1 งานหลังรับชำระครบทุกทางเข้า (B F-3 · A B-21/D-02/D-13 · คำตัดสิน §2 #4)

| # | รายการแก้ | ที่ |
| --- | --- | --- |
| 1 | ย้าย `_postPay.EnqueueAsync(...)` จากผู้เรียก 2 จุด (`DocumentController.cs:1719-1721` · `DocumentPaymentHandler.cs:110-111`) เข้าไป**หลัง commit** ใน `DocumentService.CreatePaymentAsync` (บริเวณ `:15375-15386` ที่ dispatch แจ้งเตือนอยู่แล้ว) · เพิ่มพารามิเตอร์ `PaymentEntrySource source` (enum: Manual · Gateway · Settlement · Import · Cms · Lodging · PlatformBilling) | `Services/Implementations/DocumentService.cs` · `Controllers/DocumentController.cs:1716-1721` · `Services/Payments/Handlers/DocumentPaymentHandler.cs:97-111` |
| 2 | ให้ 8 ผู้เรียกที่เหลือส่ง `source`: `Services/Settlement/SettlementPostingService.cs:313` (Settlement) · `Services/Import/Targets/PaymentImportTarget.cs:184` (Import) · `PlatformBillingDocumentIssuer.cs:320` · `CmsCommerceService.cs:1684` · `CmsBookingService.Workflow.cs:425` · `LodgingService.Lifecycle.cs:1778` · `LodgingService.TaxTiming.cs:421` · `DocumentController.cs:1715` `CreateMultiDocPaymentAsync` (ตั้งใจไม่ enqueue — ส่ง `source: MultiDoc` ให้ policy คืน "ไม่ทำ" พร้อมเหตุ `SettlementReceiptPolicy`) | ตามบรรทัด |
| 3 | `PostPaymentAutomationPolicy.Decide` รับ `source` — Cms/Lodging/PlatformBilling ⇒ `Skipped("มีเส้นออกใบเสร็จของตัวเอง")` (เขียนแถว run สถานะ Skipped ให้เห็น ไม่ใช่ไม่มีแถว) · Settlement/Import ⇒ เดินเต็ม | `Helpers/PostPaymentAutomationPolicy.cs` |
| 4 | `SyncWhtCreditReceivedAsync` ต้องถูกเรียก**ก่อน** `EnqueueAsync` ในแกนเดียวกัน (ไม่ใช่ต่อทางเข้า) — ตรวจว่าเส้น Settlement/Import เรียกอยู่แล้วหรือไม่ · ถ้าไม่ ⇒ ย้ายเข้าแกน | `DocumentService.CreatePaymentAsync` | A D-13 |
| 5 | **R241 ขยาย**: `CreatePaymentAsync` must `SyncWhtCreditReceivedAsync(` แล้ว `_postPay.EnqueueAsync(` (ลำดับ) · forbid `EnqueueAsync(` ใน `DocumentController`/`DocumentPaymentHandler` (ย้ายแล้ว) · ทุกไฟล์ใน 2 ข้อต้องเรียก `CreatePaymentAsync(` พร้อม `source:` (regex) | `tools/required_call_site_check.py` (R241 เดิมอยู่ `:14452-14545` · `:14465-14469`) |
| 6 | **B-13**: `PostPaymentAutomationService.SettingsForAsync(companyId, ruleId)` (`:52-63`) อ่านชั้น รายการประจำ › ผู้ติดต่อ ผ่าน `AutopilotSettingsResolver` (ผู้อ่านจุดที่ 4) — ไม่คำนวณเอง · ค่าตั้ง `PostPayEmailReceipt/PostPayIncludePartial` บน `RecurringTransaction`/`Contact` ถ้ายังไม่มีคอลัมน์ ⇒ เพิ่ม (`DatabaseMigrationHelper ADD COLUMN IF NOT EXISTS` · echo ใน DTO · hydrate ใน `recurring.html`) · **R248 เพิ่ม**: `SettingsForAsync` must `AutopilotSettingsResolver.Resolve(` | `Services/.../PostPaymentAutomationService.cs:52-63` · `Helpers/AutopilotSettingsResolver.cs` |

**T±:** `DocumentAutoSendRound241DbTests` เพิ่ม — `นำเข้ารับชำระCSV_มีแถวงานหลังรับชำระ` (Import ⇒ 1 แถว · ใบเสร็จออก) · `Settlement_มีแถวงาน` · `CMS_มีแถวSkippedพร้อมเหตุ_ไม่ออกใบเสร็จซ้ำ` (ทิศตรงข้าม) · `MultiDoc_ไม่สร้างแถว` (ทิศตรงข้าม) · `R248_ค่าตั้งต่อรายการปิด_ไม่ออกใบเสร็จแม้บริษัทเปิด` + ทิศตรงข้าม Inherit · `RecurringAutopilotRound248DbTests.R248_MED5_*` (B-19: enqueue ด้วยรายการ `TransferOnly` ⇒ อีเมลที่ส่งไม่มีลิงก์บัตร · Inherit ⇒ มี)

#### 2.2 GL สูตรเดียวต่อบรรทัด (C F1 · A C-06/C-07 · §2 #5–#6)

| # | รายการแก้ | ที่ |
| --- | --- | --- |
| 1 | `ResolveLinePostedGlAmountAsync`: `purchaseSide` (`:17323-17324`) เพิ่ม `CertificateInLieu` · CIL ที่ `RelatedDocumentId` ชี้ PI/Expense (route Settlement) คืน 0 เหมือน PV (`:17328` ตรวจเฉพาะ PV) · `:17336` `Standalone` → `PurchaseLineGlAmount.Posted(isCertificateInLieu, isVatClaimable, amount, vat, payerBorneShare)` | `DocumentService.cs:17320-17336` (ผู้เรียกเดียว = `ReclassifyLineAccountAsync :8413`) |
| 2 | แยก helper `WhtGrossUp.PayerBorneShareForLine(...)` (หรือชื่อใกล้เคียง) จาก `FixedAssetService.CostSources.cs LoadCostLineFactsAsync :109-121` ให้ทั้ง `LoadCostLineFactsAsync` และข้อ 1 เรียกตัวเดียว | `Helpers/WhtGrossUp.cs` · `FixedAssetService.CostSources.cs:109-121` |
| 3 | AutoPost CN/DN inline `!IsVatClaimable ? Amount + VatAmount : Amount` → `PurchaseLineGlAmount.Standalone(...)` | `DocumentService.cs:18822-18824` (เทียบ `:18829` ที่ถูกแล้ว) |
| 4 | **R เพิ่ม**: `ResolveLinePostedGlAmountAsync` must `PurchaseLineGlAmount.Posted(` · forbid `PurchaseLineGlAmount.Standalone(` ในเมธอดนั้น · forbid regex `Amount \+ \w+\.VatAmount` ใน `DocumentService.cs` (allowlist จุดที่ผ่าน helper) · `LoadCostLineFactsAsync` + `ResolveLinePostedGlAmountAsync` must เรียก helper ข้อ 2 | `required_call_site_check.py` (R249FA `:15219-15228`) |

**T±:** Db `AssetMultiDocumentDbTests` เพิ่ม `Reclassify_CIL_with_VAT_leaves_old_account_net_zero` (CIL 10,000+700 ลง 54xxx → แก้ผังไป 12260 ⇒ 54xxx สุทธิ 0 · 12260 = 10,700) · `Reclassify_payer_borne_wht_line_moves_gross_up` (10,000 ออกแทน 3% ⇒ ย้าย 10,309.28) · ทิศตรงข้าม `Reclassify_plain_PI_line_unchanged` (ใบธรรมดายอดเดิม) · pure `PurchaseLineGlAmountTests` ใบรับรองแทนทั้งสองเส้น (`Standalone` vs `Posted`) ได้ตัวเลขเดียวเมื่อ share=0 และต่างเมื่อ CIL

#### 2.3 ลิงก์ชำระ INV ที่มี TaxInvoice แล้ว (A G-02/G-03 · §2 #16)

| # | รายการแก้ | ที่ |
| --- | --- | --- |
| 1 | `DocumentPayLinkPolicy.Allowed`/`IssueRefusal`: INV ที่มี TaxInvoice อ้าง (`RelatedDocumentId`/conversion) สถานะ ≠ Voided ⇒ refuse `PAYLINK-241-TAXINV` ข้อความ "ชำระที่ใบกำกับ {เลข}" · `LinkUsable`/`UnavailableReason` (ตัดสินสด) รวมเงื่อนไขเดียวกัน ⇒ ลิงก์เดิมใช้ไม่ได้ทันที | `Helpers/DocumentPayLinkPolicy.cs` · ผู้เรียก `DocumentPayLinkService.cs:106,108,329` · `PublicViewAsync :385-390` |
| 2 | `DocumentPayLinkService.PublicViewAsync` โหลด fact "มีใบกำกับอ้างแล้ว" ส่งเข้า policy (ไม่ตัดสินใน service) | `DocumentPayLinkService.cs:366-390` |
| 3 | `PostPaymentAutomationPolicy.Decide` ⇒ INV ที่มี TaxInvoice แล้ว `Blocked("ออกใบเสร็จจากใบกำกับ")` — กันใบเสร็จซ้อน | `Helpers/PostPaymentAutomationPolicy.cs` |
| 4 | **R241 เพิ่ม**: `PublicViewAsync` must ส่ง fact ใบกำกับเข้า `LinkUsable(` · `StartAsync` เช่นกัน | `required_call_site_check.py` |

**T±:** `DocumentAutoSendRound241Tests`: `INVที่มีใบกำกับแล้ว_ไม่ออกลิงก์_บอกเลขใบกำกับ` · `ลิงก์เดิมของINV_ใช้ไม่ได้หลังออกใบกำกับ` · ทิศตรงข้าม `INVที่ใบกำกับถูกยกเลิก_ออกลิงก์ได้` · `INVไม่มีใบกำกับ_เดิม` · Db 1 ตัวเส้น `PublicViewAsync`

#### 2.4 ล้มดัง + ซ้ำ + ลำดับ (B F-1/F-2/F-4/F-8 · C F2/F4/F5/F8 · E L-2/L-3 · A H-04)

| # | รายการแก้ | ที่ | ที่มา |
| --- | --- | --- | --- |
| 1 | `OnRecurringDocumentCreatedAsync` คืน `EnqueueOutcome` (Enqueued/Skipped(เหตุ)/Failed(เหตุ)) แทน catch-all `LogWarning` (`:262`) · `RecurringTransactionService.cs:930-938` ตั้ง `EmailEnqueueFailed` จากผล · `RecurringRunOutcome.cs:184-185` กลับมาถึงได้ | `EmailScheduleService.cs:186-263` · `RecurringTransactionService.cs:930-955` | B F-1 |
| 2 | Fact `SystemBaseUrlConfigured` (จาก `SystemBaseUrl.IsConfigured`) ใน `AutopilotReadiness.Evaluate :168-186` ⇒ `GATEWAY` **Warn** "ยังไม่ได้ตั้ง URL ของระบบ — ลิงก์/QR จะไม่ถูกแนบ" + ปุ่มไป `/admin/site-settings.html` · `RecurringAutopilotService.cs:291-295` `payPlan` บอกตามจริง · `ForScheduledEmailAsync :279-284` คืน refusal → `EnqueueDocumentAsync` ใส่ `ErrorMessage` บนแถวคิว (ส่งได้แต่บอกว่าไม่มีลิงก์เพราะอะไร) · การ์ด "เรื่องค้าง" แอดมิน (`PlatformOps` รายวัน) +1 ข้อเมื่อ `AppBaseUrl` ว่าง/เป็น `ShippedDefault` | `Helpers/AutopilotReadiness.cs:168-186` · `DocumentPayLinkService.cs:246-248, :279-284` · `PayQrFactsAsync :317-319` · `NotificationEngine.cs:434-436` | B F-2 · A H-04 |
| 3 | `OnRecurringDocumentCreatedAsync` ข้ามกฎเสมือน/`RecurringInvoiceCreated` เมื่อมีกฎ `DocumentApproved` ครอบชนิดนี้และใบอนุมัติแล้ว · `AutopilotReadiness` Warn "มีกฎส่งทั้งสองแบบ" | `EmailScheduleService.cs:74, :216-260, :762` · `DocumentService.cs:7759` | B F-4 |
| 4 | `NotificationEngine.DispatchAsync :74-78` vs `:84-87` — สลับลำดับ (`savedRows.Count > 0 && settings.Count == 0` ⇒ return ก่อนสาขา `RecipientUserId`) | `Services/.../NotificationEngine.cs:60-100` | B F-8 |
| 5 | `Helpers/CashFlowDirection` แทน `IsCashInflowDocAsync` (`DocumentService.cs:14652-14667`) — รู้ฝั่ง CN/DN จาก `AdjustmentNoteAccount.ResolveSide` · ผู้เรียก 5 จุด (`:11906` · `:12182` · `:15331` · `:15790` · `:15374-15386`) เรียกตัวเดียว | `DocumentService.cs` · `Helpers/PaymentNotificationText.cs` | C F8 · A K-02 |
| 6 | `WhtMismatchNote` (บุคคลธรรมดา+ใบมี WHT) ยกเป็นคำเตือนก่อนอนุมัติในชุด WHT-PAYEE (`CollectApprovalWarningsAsync`) · คงไว้ที่ลิงก์ด้วย | `DocumentPayLinkPolicy.cs:127-146` · `DocumentService.cs:22199` บริเวณคำเตือน WHT | C F5 |
| 7 | `EtdaTimestampToken` > 500 ตัว ⇒ `BusinessRuleException` "token ยาวเกิน — แนบไฟล์หลักฐานแทน" ไม่ตัดเงียบ | `DocumentEmailService.cs:595` | C F4 |
| 8 | void/purge ถือคีย์ `AssetCostSource` เสมอเมื่อ `AssetCostSourceRules.KindFor(type) != null` ก่อน `FOR UPDATE` ใบ — ลบการตรวจ `AnyAsync` นอกล็อก (`LockCostSourcesIfLinkedAsync :198-204`) | `FixedAssetService.CostSources.cs:191-204, :350` · ผู้เรียก `DocumentService.cs:9912, :10964` | C F2 · E L-3 |
| 9 | `WhtCreditService.SettleYearEndAsync` `rows` `.OrderBy(w => w.Id)` ก่อนวน `ExpireCoreAsync` · เพิ่ม `AdvisoryLockKey.WhtCreditWriteOff` const แทน literal `"wht-credit-writeoff"` ใน `LockCreditAsync` | `WhtCreditService.cs:937-938` · `LockCreditAsync` | E L-2 · §7 |
| 10 | `OpeningBalanceReconciliation.cs:78` กรอง/ติดป้าย `IsUnderAcquisition` · ส่งออกทะเบียน `ImportExportService.cs:1639` ติดป้าย · `GetCostSourcesAsync` เพิ่ม problem row "ใบนี้มีทะเบียนเกิน GL อยู่ก่อน" (เคส #4) · หน้า/รายงานติดป้าย "วันที่ซื้อ = วันพร้อมใช้" สำหรับสินทรัพย์หลายใบ | ตามบรรทัด | C F6 |
| 11 | `CS8604 DocumentController.cs(1530,96)` `documentIds` nullable — ปิด contract | `DocumentController.cs:1530` | E L-5 |
| 12 | เทสต์ literal รหัส → ค่าคงที่: `RecurringAutopilotRound248DbTests.cs:365` `"BUYER-86/4"` · `AutopilotRound248Tests.cs:133` `"AR-OVERDUE"` → `ArOverdueApprovalWarning.Code` | ตามบรรทัด | E L-4 |

**T±:** `RecurringRunOutcomeTests`/`AutopilotRound248Tests`: fake `IEmailScheduleService` โยน ⇒ facts มี `EmailEnqueueFailed` (+ทิศตรงข้าม Skipped โดยตั้งใจ ⇒ ไม่ตั้งธง) · `ไม่ตั้งBaseUrl_readinessไม่ใช่Full_GATEWAYเป็นWarn` + ทิศตรงข้าม · Db `ทั้งสองกฎ_เข้าคิว1แถว` + ทิศตรงข้าม `กฎเดียว_เดิม` · Db `ปิดทุกช่อง_ระบุผู้รับ_ไม่ส่ง` + ทิศตรงข้าม `ยังไม่ตั้ง_ระบุผู้รับ_ส่งค่าตั้งต้น` · `CashFlowDirectionTests` ตารางครบทุก `DocumentType` × ฝั่ง CN/DN · Db handler บุคคลธรรมดา+ใบมี WHT (Payment.WHT = 0 · ใบปิดที่ 10,400 · มีคำเตือนตอนอนุมัติ) · Db `Void_while_linking_holds_key_first` (ล็อกเสมอ · ไม่ deadlock) · `SettleYearEnd_locks_in_id_order` (ยืนยันลำดับผ่าน log/assert รายการ)
**R:** R241 (ข้อ 2.1/2.3) · R248 (`SettingsForAsync`) · R249FA (helper share · forbid Standalone ใน Resolve) · R249 (`CashFlowDirection` ใน 5 ผู้เรียก · forbid `IsCashInflowDocAsync`) · ใหม่ R250-LOCK: `VoidDocumentAsync`/`PurgeOneDocumentCoreAsync` must `LockCostSourceWritersAsync(` ก่อน `FOR UPDATE`
**เอกสาร:** DOCUMENT_FLOW §2.12g (งานหลังรับชำระ "ทุกทางเข้า" + ตาราง source) · §2.12g ลิงก์ชำระ (เงื่อนไขใบกำกับ) · §6 สินทรัพย์ (ลำดับล็อก · GL helper เดียว) · §2.7 (EmailEnqueueFailed · 2 กฎ) · ACCOUNT_STRUCTURE (การ์ดเรื่องค้าง AppBaseUrl) · DECISIONS.md แถว 250-1…250-N (คำตัดสิน §2 #4–#16) · TEST_PLAN §0 · CHANGELOG
**Definition of done:** `grep -n "EnqueueAsync(" Accounting/` นอก `DocumentService.CreatePaymentAsync` = 0 (ตอบเป็นตัวเลขในคอมมิต) · `callers.py IsCashInflowDocAsync` = 0 · ทุก T± ข้างบนรันใน CI และเขียว · ฝ่ายค้านตอบ 3 คำถามแล้ว · ผู้ใช้ที่นำเข้า CSV รับชำระเห็นแถวงานบนหน้าเอกสาร

---

### WP-03 · ความปลอดภัย/UX ร้ายแรงจาก D (XSS · ข้อมูลหายเงียบ · privacy · silent no-op) · effort 2 วัน · **ฝ่ายค้าน 1 รอบ** (แตะ privacy/สิทธิ์)

**เป้าหมาย:** ปิด "ทำก่อน 10 ข้อ" ของ D §12 + G-01 + H-01 แบนเนอร์ — ทุกข้อเป็นคลาสบั๊กที่ CLAUDE.md #4 ห้ามเกิดซ้ำ

| # | รายการแก้ | ที่ (ไฟล์:บรรทัด) | ที่มา |
| --- | --- | --- | --- |
| 1 | **XSS fixed-assets.html** — `Layout.esc()` ทุกจุด: โครงการ `${p.code}…${p.name}` :518 · ผังบัญชี `${a.accountCode} - ${a.accountName}` :526, :533 · ตาราง `${a.assetCode}` :603 · `${a.name}` :606 · `${a.category}` :608 · รายละเอียด :823–829 (`assetCode`, `category`, `location`, `serialNumber`) · รายงาน :1196 (`assetCode`, `name`, `category`) | `Accounting/wwwroot/fixed-assets.html` | D §2.1 |
| 2 | XSS อื่น: documents.html ใบรับรอง `${d.certifierName}`, `${d.certifierPosition}`, `${d.witnessName}` :11186–11187 · contacts.html `${c.email \|\| '-'}` :563 · email-schedule.html `${r.documentType}` :239 · notifications.html `data-k="${key}"` :147–149, `data-pe="${ev.key}"` :204 · portal.html `src="${ppQr}"` :505 + `onsubmit=…'${esc(docNumber)}'` :515 ต้องเป็น `Layout.jsArg` | ตามบรรทัด | D §3.7 · §6.5 · §8.5 · §4.6 · §9.5 |
| 3 | **recurring.html ข้อมูลหาย**: เมื่อ `!this._apOpts` (โหลด `/recurring/autopilot/options` ล้ม) ส่ง `paymentMode: null`, `payLinkChannels: null` (= ไม่แก้) ไม่ใช่ `''`/`[]` · ล็อกขั้น ④ พร้อมเหตุผล · `pm.value = r.paymentMode` หา option ไม่เจอ (:301) ⇒ `_restoreSavedOption` | `recurring.html:282–288, :301, :1141–1142` · เซิร์ฟเวอร์ `RecurringTransactionService.cs:204–209` ("" = ล้าง · [] = ล้างช่องทาง — คงเดิม) | D §1.1 |
| 4 | **recurring.html นโยบายไม่บันทึกกับฟอร์ม**: จำ dirty ของ `#fPolicyRows` → หลัง create สำเร็จ (ได้ id) เรียก `savePolicy('recurring')` ต่อ · ตอนสร้างใหม่ disable ปุ่ม "บันทึกนโยบายของรายการนี้" + ข้อความใต้ปุ่ม · ปิดปุ่มบันทึกนโยบายเมื่อฟอร์ม dirty (`_buildPayload` ≠ ค่าที่โหลด) + "บันทึกรายการก่อน" (B F-6) | `recurring.html:373–386, :396–417, :408, :1030–1058` | D §1.2 · B F-6 |
| 5 | **fixed-assets AI ทับค่า hydrate**: `openEdit` (:696–728) ตั้ง `dataset.autoSrc='saved'` + `userTouched` · `suggestAssetCategory` (:467–500) ข้ามเติม `fLife`/`fMethod` เมื่อ `fId` มีค่า · `openCreate` (:636–660) reset flag | `fixed-assets.html:467–500, :696–728, :636–660` | D §2.2 |
| 6 | **QR บุคคลที่สาม → server data-URI** (G-01): `payment-options` ของพอร์ทัลส่ง `promptPayQrDataUri` จาก `PaymentQrService`/`PromptPayService` (เส้นเดียวกับ pay-document.html :56–61) · ลบ `buildPromptPayPayload` + TLV builder `portal.html:589–611` · `:491–494, :505` ใช้ data-URI (ตรวจ prefix `data:image/png;base64,` ก่อนใส่ `src` แบบ pay-document :57–61) · pay-link.html `:132–134` แสดงรูป QR แทน payload ดิบ 🖥 · **checker ใหม่** `tools/external_qr_check.py` (หรือเพิ่มใน `csp_external_ref_check`): forbid `qrserver.com`/`api.qrserver` ทั้ง `wwwroot/` + negative test | `portal.html:491–494, :505, :589–611` · `pay-link.html:132–134` · `Controllers/PortalController` (payment-options) · `tools/` | A G-01 · D §9.1 · §10.4 |
| 7 | **notifications.html**: โหลดเมทริกซ์ล้ม ⇒ `_catalog == null` ⇒ disable "💾 บันทึกตั้งค่าทั้งหมด" + ข้อความ/ปุ่มลองใหม่ใน `#matrixContainer` (ห้าม `saveMatrix` ส่ง `{settings: []}`) · dirty tracking + `beforeunload` + ป้าย "มีการเปลี่ยนแปลงที่ยังไม่บันทึก" · `loadLine` กลืน error (:240) → `Layout.panelError` | `notifications.html:47, :110–124, :159–179, :46–48, :101–107, :240` | D §4.1–4.2 |
| 8 | **settings.html e-Tax**: `saveEtaxConfig` สำเร็จ ⇒ toast + `loadEtaxConfig()` ใหม่ (คำนวณ `warnings` :2442–2447 ใหม่) · `loadEtaxConfig` `catch { /* graceful */ }` :2456 → panelError · "อีเมลผู้ส่งที่ลงทะเบียน *" :944 `checkValidity()` ก่อนส่ง · ลิงก์กระโดดไปแท็บอีเมล :957–960 · `loadSignRequestSettings`/`loadAutoSendSettings` ได้ 403 (:1395, :1433) ⇒ disable การ์ด + "เฉพาะเจ้าของบริษัท" · แก้ป้ายสองธงทดสอบ (§2 #20) | `settings.html:2460–2478, :2456, :944, :957–960, :1380–1448` | D §5.1–5.2, 5.5 · A F-02 |
| 9 | **email-schedule.html**: `sendAtHour: parseInt(...) \|\| 9` → `Number.isInteger` + ช่วง 0–23 · `dayOfMonth … \|\| 5` → 1–28 · `retry()`/`cancel()` อ่าน `j.success` (:390–408) · ย้าย `fetch` ดิบ 6 จุด (:209, :330, :347, :360, :393, :403) ไป `API.*` | `email-schedule.html:316, :318, :390–408` | D §8.1–8.2 |
| 10 | **contacts.html notify race**: ติ๊กหัวข้อ/ยอด = PUT ทันที (:1280–1289) ⇒ seq-guard แบบ `recurring.html:440` หรือ debounce 400ms รวม PUT เดียวต่อปลายทาง · `addNotifyEmail` `checkValidity()+reportValidity()` (:1270–1275) | `contacts.html:1259–1289` | D §6.1–6.2 |
| 11 | **documents.html**: `renderAutoSendSection` :13199–13203 · `renderPayQrNotice` :13184 `catch (_) { return; }` ⇒ "โหลดไม่สำเร็จ — ลองใหม่" แบบ `renderDocAssetSection :2718–2721` · ปุ่มสร้างลิงก์ clipboard ถูกปฏิเสธ (:13222–13223) ⇒ "สร้างลิงก์แล้ว — คัดลอกจากด้านบน" · `registerAssetFromLine` `created === 0` แสดง `notes` ในแผง (:2779–2786) · `_docHasAssetAccountLine` (:2690–2698) → DTO `hasAssetCandidateLines` 🖥 | `documents.html` ตามบรรทัด | D §3.1, 3.2, 3.4, 3.6 |
| 12 | **fixed-assets อื่น**: `assetAccountId` ว่าง = คงผังเดิม (:762) vs `depreciationExpenseAccountId`/`accumulated…` ว่าง = Guid.Empty (:763–764) ⇒ ล็อกช่องตอนแก้ไขพร้อมเหตุผล (แบบ `_lockValuationFields`) 🖥 · โหลดผัง/โครงการล้ม (:509 `console.warn` · :521 `/* ignore */`) → panelError · `pageSize=200` + กรอง client + ปิด pagination (:556–565, :621) ⇒ เซิร์ฟเวอร์กรอง `assetType` + แบ่งหน้า 🖥 · แผงเลือกบรรทัด (:943–965) พิมพ์ยอด ⇒ ติ๊ก checkbox ให้ + แถวสรุป "เลือก n บรรทัด · รวม X / ราคาตามสัญญา Y" | `fixed-assets.html:757–769, :502–522, :552–623, :943–965, :231` | D §2.3–2.5, 2.7 |
| 13 | **แบนเนอร์ /admin/legal.html** "ฉบับตั้งต้นมีข้อใหม่ที่ฉบับเผยแพร่ยังไม่มี" (diff หัวข้อระหว่าง `LegalDocumentDefaults` กับฉบับเผยแพร่) 🖥 — รอเจ้าของกดเผยแพร่ (§3 Y6) | `wwwroot/admin/legal.html` · `Helpers/LegalDocumentDefaults.cs:220` · `.En.cs:159` | A H-01 |
| 14 | ป้าย AI ซื่อสัตย์: `AiSuggestionController.cs:281–345` คืน `usedAi` (source `Learned`/`ContactHistory` ⇒ false) · helper `Layout.suggestLabel(resp)` ⇒ `usedAi ? '🤖 AI แนะนำ' : '⚙️ ระบบแนะนำ'` · ใช้ที่ `recurring.html:813–844, :150, :830` · `fixed-assets.html:135, :422–465, :462–464, :489` · ย้าย `fetch` ดิบ (`recurring :813` · `fixed-assets :437, :479`) ไป `API.*` | ตามบรรทัด | D §1.5 · §2.9 · รูปแบบ G |

**T±:** XSS — เพิ่มเคสใน checker `onclick_js_string_check`/ใหม่ `html_interpolation_escape_check` สำหรับ `${x.name}`/`${x.code}` ที่ไม่ผ่าน `esc(`/`Layout.esc(` ใน 7 ไฟล์ข้างบน (ratchet baseline = 0 หลังแก้) + negative test · QR — `PaymentQrRound246Tests` เพิ่ม `พอร์ทัล_payment-options_คืนdataUri_ไม่มีURLภายนอก` + Db ทิศตรงข้าม "บัญชีไม่มีพร้อมเพย์ ⇒ ไม่มี QR ไม่มี URL" · `node --check` ทุกไฟล์ที่แก้ · checker `raw_fetch_api_check` baseline ลด: email-schedule 6→0 · settings LINE 9→≤6 · recurring 2→0 · fixed-assets 2→0 (ตอบตัวเลขในคอมมิต)
**R:** `PortalController` payment-options must `PaymentQrService.` (หรือ `PromptPayService.`) · forbid `qrserver` ใน `wwwroot/**` (checker ข้อ 6)
**เอกสาร:** DOCUMENT_FLOW §2.12i (QR พอร์ทัล/pay-link ผ่านเซิร์ฟเวอร์) · §2.7 (นโยบายบันทึกกับฟอร์ม · payload null) · `docs/lessons/ux-frontend.md` (หรือหมวดที่ตรง) append: "QR บุคคลที่สามตกหล่นที่ portal หลังแก้ storefront รอบ 209 — checker ไม่จับ `<img src>`" · TEST_PLAN · CHANGELOG
**Definition of done:** `grep -rn "qrserver" Accounting/wwwroot` = 0 · checker escape ใหม่ baseline 0 · ฝ่ายค้านตอบ "ทางเข้าอื่นที่สร้าง QR (storefront/pay-document/pay-link/portal/กระดาษ) ใช้ตัวเดียวกันแล้ว" · เปิดหน้า fixed-assets ด้วยชื่อสินทรัพย์ `<img onerror=…>` ไม่รัน

---

### WP-04 · เทสต์/กติกาที่ขาด (E + C F7) · effort 1 วัน · ทำ**หลัง** WP-01 (ไม่งั้นไม่มีประโยชน์)

**เป้าหมาย:** ด่านทุกตัวของรอบ 241–249 มีเทสต์สองทิศผ่านเส้นจริง + กติกา call-site · งานย้ายข้อมูลตอนบูตมีเทสต์ · ฐานเทสต์ไม่ถูก ALTER ขณะขนาน

| # | รายการแก้/เพิ่ม | ที่ | ที่มา |
| --- | --- | --- | --- |
| 1 | **`PaymentQrMigration`** Db test 4 ทิศ (ลอก `ContactNotifyEndpointRound245DbTests` เคส R245g): ย้ายถูกบริษัท · ข้ามบริษัทที่มี 2 บัญชี · บูตซ้ำ no-op · บัญชีที่มีเลขอยู่แล้วไม่ถูกทับ · เปลี่ยนคีย์ `pg_advisory_xact_lock(hashtext({markerKey}))` (`:68`) → `AdvisoryLockKey.For(AdvisoryLockKey.OneShotDataFix, MarkerKey)` · ย้าย `CREATE TABLE OneShotDataFixes` จาก `ContactNotifyEndpointMigration.Statements()` ไป `DatabaseMigrationHelper` กลาง · **R เพิ่ม** R246-MIG: `MoveLegacyTemplatePromptPayAsync` must `AdvisoryLockKey.For(` + `Program.cs:2141` must เรียก | `Accounting/Helpers/PaymentQrMigration.cs:63-121` · `Program.cs:2141-2147` · `Accounting.Tests/Db/PaymentQrRound246DbTests.cs` | E M-3 · §7 |
| 2 | **`WhtWriteOffJournalLock.EnsureNotWriteOffEntriesAsync`** Db test: `ReverseJournalEntryAsync` บน JE ตัดสูญ ⇒ 409 `GL-WHT-WRITEOFF-ENTRY` · ทิศตรงข้าม JE ธรรมดากลับได้ | `Helpers/WhtWriteOffJournalLock.cs:36` · `WhtCreditWriteOffDbTests` | E L-1 |
| 3 | **`DocumentPurgeDbTests.ProductionFkShapeAsync`** (:89-112 `ALTER TABLE … DROP/ADD CONSTRAINT … ON DELETE RESTRICT`) ย้ายไป `DbTestDatabase` static init (ฐาน = รูป production เสมอ) · ลบออกจากคลาส | `Accounting.Tests/Db/DocumentPurgeDbTests.cs:89-112` · `DbTestDatabase.cs` | E M-4 |
| 4 | **ด่านสินทรัพย์ที่ไม่มี negative test ผ่านเส้นจริง** — Db test ละ 1 ตัว + กติกา: purge+สินทรัพย์หลายใบ (`DocumentService.cs:11191-11193` ⇒ `Purge_capitalized_source_is_blocked` · ทิศตรงข้าม held ปลดได้) · ย้ายฝั่ง CN/DN `:9151` · สลับบริการต่างประเทศ `:8890` · resync `IntegrationService.cs:3529` (`CostSourceDocumentChangeBlockAsync`) · OCR `OcrService.cs:10215` (ลงทะเบียนซ้ำจากสแกนที่ผูกแล้ว ⇒ 409) · reclassify `:8324` ผ่าน `DocumentService` · `EnsureNotAssetCostTransferAsync` ผ่าน `AccountingService.cs:1039, :1263` · **R249FA ขยาย** ครอบ 7 จุดเรียกนอก `FixedAssetService.CostSources.cs` (ตอนนี้ `:15219-15228` ครอบเฉพาะไฟล์ CostSources) | `AssetMultiDocumentDbTests` · `required_call_site_check.py` | C F7 · §1.10 |
| 5 | **8 ฟังก์ชันทิศตรงข้าม**: `AutoApprovePolicy.Releases` (เรียกตรง) · `EtaxByEmailFormat.UatWarning` (0 เทสต์) · `EtaxDeliveryRouter.Decide` neg "ไม่ใช่ e-Tax by Email ⇒ ส่งปกติ" · `PostPaymentAutomationPolicy.Effective`/`IdempotencyKey` neg · `DocumentPayLinkPolicy.Allowed`/`OnlineOnly` neg · `PaymentQrPolicy.Pick`/`PromptPayUsable` neg · `AssetCostSourceRules.PlaceInServiceProblem` neg (0 N) · `EtaxByEmailFormat.Stage` neg | ไฟล์เทสต์รอบนั้น ๆ (`AutopilotRound248Tests` · `EtaxByEmailRound244Tests` · `DocumentAutoSendRound241Tests` · `PaymentQrRound246Tests` · `AssetCostSourceRulesTests`) | E L-1 · §2 |
| 6 | **`BindingHash` สองทิศ**: hash เปลี่ยนเมื่อแก้ `unitPrice`/`accountId`/`contactId` · ไม่เปลี่ยนเมื่อแก้ `description` | `AutopilotRound248Tests` · `Helpers/AutoApprovePolicy.cs:149-183` | C §5.3 |
| 7 | **XML e-Tax** negative: `DocumentTypeCode` (388/80/81/T02/T03 ต่อชนิด) · `IssueDateTime` +07:00 (`FormatIso :1074-1078`) · BranchID 5 หลัก/ผู้ซื้อไม่ครบ ⇒ throw (`:265-268`) | `EtaxInvoiceService.cs` · เทสต์ใหม่ `EtaxXmlRound250Tests` | C F7 · §3 |
| 8 | **`PaymentNotificationRound249Tests`** ตารางครบทุก `DocumentType` (ย้ายไปทดสอบ `CashFlowDirection` จาก WP-02) | `Accounting.Tests/PaymentNotificationRound249Tests.cs` | A K-02 |
| 9 | **`AssetMultiDocumentDbTests:130`** `PostJe` fallback `UtcNow` เงียบเมื่อเอกสารไม่อยู่ใน tracker ⇒ throw ในเทสต์แทน · `:70` ของ `RecurringAutopilotRound248DbTests` `AgingDays=45` ค่าคงที่ — คำนวณจาก DueDate | ตามบรรทัด | E §3 |
| 10 | **`settings_reader_check` ratchet** เพิ่ม `RecurringTransaction` · `Contact` · `EmailScheduleRule` · `BankAccount` เข้า `ENTITIES` (`:44-75`) เฉพาะ property ค่าตั้ง (หน้า `recurring.html`/`email-schedule.html`/`bank.html`) · baseline ว่าง (ทุกตัวมีผู้อ่านแล้ว) | `tools/settings_reader_check.py:44-75` | B F-7 |
| 11 | **`deep_link_param_check`** ครอบ `#hash=`: ลิงก์ `<page>.html#<k>=` ต้องมี `location.hash` + ชื่อ `<k>` ในไฟล์ปลายทาง + negative test 1 เคส | `tools/deep_link_param_check.py:4-20` | A A-06 |
| 12 | **`contact_picker_check`** ตัดสินด้วยพฤติกรรม (มี `.Add/.Remove/SaveChanges/ExecuteUpdate` ในเส้น) ไม่ใช่ชื่อ id · เพิ่ม negative case ใน self-test | `tools/contact_picker_check.py` | E §5 · §6.4 ข้อ 3 |
| 13 | **checker UI ใหม่ 2 ตัว** (ratchet): label ใน `.form-group` ต้องมี `for` ชี้ id จริง · `<table` ≥ 5 คอลัมน์ใน `.modal-body` ต้องห่อ `.table-container` | `tools/` ใหม่ | D รูปแบบ D · F |

**เอกสาร:** TEST_PLAN §0 (ชื่อเทสต์ใหม่ทุกตัว · `test_inventory --check` ต้องผ่าน) · `docs/lessons/checker-writing.md` (checker ใหม่ 4 ตัว + defect class "checker ตัดสินด้วยชื่อ id") · CHANGELOG
**Definition of done:** E §2 matrix ทุกแถว P≥1 N≥1 กติกา≥1 (เขียนตารางใหม่ใน CHANGELOG) · `required_call_site_check` self-test ผ่าน (ถอดการเรียกแต่ละจุดแล้วฟ้อง) · job test + db-test เขียว

---

### WP-05 · เอกสาร/ประวัติ (doc-only 1–2 คอมมิต · effort ½ วัน · **ไม่มีรอบ CI ให้รอ** — `paths-ignore`)

**เป้าหมาย:** ทุก `commit <เติมในคอมมิตตามหลัง>` ถูกเติม · DECISIONS/REVIEWS_INDEX ตามโค้ด · doc ที่ผิดข้อเท็จจริงถูกแก้ · git notes · lessons 6 เรื่อง

#### 5.1 ตาราง sha ที่ต้องเติม (จาก E §6.2 + A A-03 · ยึด E เมื่อขัดกัน — E ตรวจ blame)

| ไฟล์:บรรทัด | รอบ | sha ที่ใส่ |
| --- | --- | --- |
| DOCUMENT_FLOW.md:7759 | 235 (ฝ่ายค้าน 235-X) | `1019338e` / `ae7255ad` / `ceeae223` (A-03) |
| DOCUMENT_FLOW.md:7760 | 236 | `1cea2ec6` |
| DOCUMENT_FLOW.md:7761 | 237 | `f1d66b15` / `c4c79995` |
| DOCUMENT_FLOW.md:7762 | 238 | `f09ca228` / `41b04e0a` / `a7ba4164` |
| DOCUMENT_FLOW.md:7764 | 240 | `61f6b0f6` |
| DOCUMENT_FLOW.md:7765 | 243a | `26ec41b4` (+ `db2a391e` CS8126) |
| DOCUMENT_FLOW.md:7766 | 242 OCR | `26ec41b4` |
| DOCUMENT_FLOW.md:7767 | 242 เอกสาร | `26ec41b4` |
| DOCUMENT_FLOW.md:7768 | 246 QR | `6a8e7ab3` (+ `1abfda1c` · `a16fad65`) |
| DOCUMENT_FLOW.md:7769 | 241 | `26ec41b4` |
| DOCUMENT_FLOW.md:7770 | 248 | `8304ce95` (+ `29d6730f` ฝ่ายค้าน · `ca10f623`) |
| DOCUMENT_FLOW.md:7771 | 249 แจ้งเตือน | `ecc6710f` (**ไม่ใช่** `369bc403` — M-2) |
| CHANGELOG.md:6387 | 240 ฝ่ายค้าน | `61f6b0f6` |
| CHANGELOG.md:6411 | 242 เอกสาร | `26ec41b4` |
| CHANGELOG.md:6423 | 241 | `26ec41b4` |
| CHANGELOG.md:6432 | 243a | `26ec41b4` |
| CHANGELOG.md:6450 | 244 | `6a8e7ab3` |
| CHANGELOG.md:6458 | 244 ฝ่ายค้าน | `6a8e7ab3` |
| CHANGELOG.md:6471 | 245 L1 | `6a8e7ab3` (+ `a16fad65` · `1abfda1c`) |
| CHANGELOG.md:6481 | 246 QR | `6a8e7ab3` |
| CHANGELOG.md:6492 | 247 | `ca8acbe8` (+ `b2e96b45`) |
| CHANGELOG.md:6507 | 248 | `8304ce95` |
| CHANGELOG.md:6521 | 249 แจ้งเตือน | `ecc6710f` |
| CHANGELOG.md:6527 | 248 ฝ่ายค้าน | `29d6730f` (+ `ca10f623`) — blame ชี้ `7cb6bd08` เพราะถูกย้าย hunk |
| CHANGELOG ส่วนรอบ 234–239 (A-03: 234 · 235 ×4 · 236 ×2 · 237 ×2 · 238 ×2 · 239 ×2) | | 234 → `a1e4d857` / ฝ่ายค้าน `181dbf12` · 235 → `1019338e`/`ae7255ad`/`ceeae223` · 236 → `1cea2ec6` · 237 → `f1d66b15`/`c4c79995` · 238+239 → `f09ca228`/`41b04e0a`/`a7ba4164` (ใช้ `grep -n "เติมในคอมมิตตามหลัง" CHANGELOG.md` หาบรรทัดจริง — ตอนเขียนไฟล์นี้ grep ทั้ง 4 ไฟล์ได้ **102 บรรทัด** รวมของเก่า ⇒ เติมให้หมดในคราวเดียว) |
| ACCOUNT_STRUCTURE.md:1420 | ทีม FS | `d6d83995` |
| ACCOUNT_STRUCTURE.md:1422 | 230 Omise | `ab876762` |
| ACCOUNT_STRUCTURE.md:1424 · 1478 | 232 Notify · Public checkout | `b4780a33` |
| ACCOUNT_STRUCTURE.md:1208 · 1211 · 1397 · 1398 | rev 46/49/50 (ต.ค.) | หา `git log -S` (เก่า — ปิดพร้อมกัน) |
| erp-review/2026-11-06/RETENTION_LAW_CHECK.md:190 + `erp-review/2026-09-24/review193-r2-sec-tax.md:54,62` | sha ตาย `0a829110` | `git log -S"R2-C4" -- erp-review/2026-09-24/` แล้วแทนทั้ง 2 ไฟล์ |
| erp-review/2026-10-14/PAYMENT_CHANNEL_AUDIT.md:375,377 | `2560776c` · `c382580d` | A ว่ามีบน GitHub (`gh api`) ⇒ หลัง `fetch --unshallow` ถ้ายังไม่เจอ = baseline ใน checker (WP-01 ข้อ 4ค) |

#### 5.2 รายการ doc อื่น

| # | รายการ | ที่ | ที่มา |
| --- | --- | --- | --- |
| 1 | **DECISIONS.md** เติมแถวรอบ 247 · 248 · 249 (ไม่มีเลย — แถวท้ายสุด = 246-QR-10): 247-1 (ผลรอบ `RecurringRunOutcome`) · 248-1…248-6 (Q3 ค่าตั้งต้น · `BindingHash` · `[RejectApiKey]` · MED-3 แหล่ง Unattended · B-22 ทางเข้าไม่มีคน · MED-5) · 249-N1…N3 (แยกคีย์ `payment_made` · migration คัดลอกค่าตั้ง · H-05 ปิดค่าตั้งต้นไม่ต้อง migrate) · 249-FA1…FA6 (สินทรัพย์ H1–H3/M1–M6 คัดจาก CHANGELOG:6483–6548) · **250-1…** = คำตัดสิน §2 ของไฟล์นี้ (41 ข้อ — ใส่เฉพาะที่มีผลเงิน/สิทธิ์/กฎหมาย) | `erp-review/2026-09-29/DECISIONS.md` | A A-04 |
| 2 | **REVIEWS_INDEX.md** §ใหม่ "2026-11-01…06": 19 ไฟล์ใน `erp-review/2026-11-01..06/` (รวม 9 ไฟล์ออกแบบ/วิจัย: ASSET_MULTI_DOCUMENT_DESIGN · AUTOPILOT_BILLING_DESIGN · AUTO_SEND_WHT_DESIGN · ETAX_BY_EMAIL_AUDIT · EWHT_EVIDENCE_RESEARCH · LINE_MULTICHANNEL_DESIGN · LINE_OA_TERMS_CHECK · NOTIFY_PRICING_ANALYSIS · RETENTION_LAW_CHECK) + `audit-249/A–E` + **ไฟล์นี้** · 1 บรรทัด/ไฟล์ว่าใช้ตัดสินเรื่องอะไร | `docs/REVIEWS_INDEX.md` (`grep "2026-11-0[1-6]"` = 0) | A A-05 |
| 3 | **AUTO_SEND_WHT_DESIGN.md**: §1.1 เพิ่มแถว `CrossTenantWorkflow.cs:22 TradingPartnership` · `:84` G-8 → "มี `TradingPartnership` + `CrossTenantWorkflowService/Controller` แต่ยังไม่ต่อกับการส่งเอกสาร/50 ทวิ" · §1.1/§7.1/§9 เฟส 246 → "ดู AUTOPILOT §6 รอบ 251–253 ต่อยอดจากของเดิม" · §9 เฟส 243 ข้อ 7 → `AutoSendDisabled` เป็นชั้นใน `AutopilotSettingsResolver` (ไม่สร้าง resolver ที่สอง) | `erp-review/2026-11-06/AUTO_SEND_WHT_DESIGN.md:27-49, :84` | E M-6 · A D-09 · D-03 |
| 4 | **ASSET_MULTI_DOCUMENT_DESIGN.md:131** "ยังไม่ push ตามคำสั่ง" → push แล้ว (c314266c…fe9036f7 · PR #29) · **AUTOPILOT §12 บรรทัด 3** "L1 ต่อสายในรอบ 249" → ยังไม่ต่อ (WP-08) · §7.3 ข้อ 2–3 จด "ตั้งใจ" (GRN-link · ปลดแถวที่โอนแล้วใช้ CN · กู้คืนไม่ผูกกลับ · แผงไม่แสดงบน CN ซื้อ) | ตามบรรทัด | A A-07 · C-08 |
| 5 | **CLAUDE.md** 2 ข้อความ (ทำใน WP-01 ข้อ 10 ถ้ายังไม่ทำ) + §F "`DocumentTypeCode (T01–T04)`" → "ตาม `Helpers/EtaxDocumentTypeMap` (388/80/81/T02/T03)" + §H DBD submission เติมที่มา "5 เดือน = เกณฑ์ห้างหุ้นส่วน/อื่น · บริษัท ~5 เดือนทางอ้อมจาก ป.พ.พ. ม.1197 (AGM ภายใน 4 เดือน)" (I-03 — ทำพร้อม WP-06) | `CLAUDE.md` §F · §H · §F3 · §วิธีทำงาน | C F9 · A I-03 · E §6.3 |
| 6 | **git notes** `git notes add 369bc403 -m "ข้อความคอมมิตนี้เป็นของ ecc6710f (แจ้งเตือนรับ/จ่าย) — เนื้อหาจริง = สินทรัพย์หลายใบ H1–H3/M1–M6 · ดู CHANGELOG:6281/6540"` · `git push origin refs/notes/*` | git | E M-2 · A K-01 |
| 7 | **docs/lessons/** append 6 เรื่อง: (process-docs.md) "ข้อความคอมมิตถูกทับจาก scratch ร่วม — ตั้งชื่อไฟล์ข้อความตาม sha/งาน · `git log -1 --stat` หลัง commit · แก้ด้วย git notes" · (process-docs.md) "สองทีม stage แยก hunk ในไฟล์เดียว (`7cb6bd08` api.js/CHANGELOG สลับที่) — ไฟล์ append-only ให้ทีมเดียวเขียน · `git diff --cached` ดูบริบทหลัง `add -p`" · (checker-writing.md) "checker ฟ้องผิด 2 ตัว/สัปดาห์ (`contact_picker_check` จับชื่อ id · `write_permission_gate` จับ verb) — ตัดสินด้วยพฤติกรรม · แก้ที่ negative case ของ self-test ไม่ใช่เปลี่ยน id หน้าเว็บหลบ (29d6730f เลี่ยง · ca10f623 ถูกทาง)" · (money-tax.md) "ด่านเทียบผลต่างมีเครื่องหมาย (`c314266c`) — นับ `max(0,after) − max(0,before)` ฝั่งเดียว · เทสต์ต้องมีเคสข้ามศูนย์ (−400k→+20k) และจากศูนย์ (0→+20k)" · (build-errors.md) "เปลี่ยน signature แล้วแก้ผู้เรียกก่อนนิยาม (`43cfdba1` CS1501) — `callers.py` หลังแก้ · default ของพารามิเตอร์ใหม่อยู่คอมมิตเดียวกับผู้เรียกตัวแรก" · (process-docs.md) "คอมมิต 'ตามหลัง' เติม sha ไม่ครบ (`f990b16e`/`43cfdba1` 2 จาก 10) — ใช้ `grep -n 'เติมในคอมมิตตามหลัง'` 4 ไฟล์เป็น checklist · checker นับ placeholder เป็น fail" · (+ checker-writing.md) "`doc_commit_sha_check` ฟ้องผิดบน shallow clone — ตรวจ `is-shallow-repository` ก่อน" | `docs/lessons/{process-docs,checker-writing,money-tax,build-errors}.md` | E §6.4 · A A-02 |
| 8 | DOCUMENT_FLOW จด "ตั้งใจ": คอลัมน์ตาย `EtaxByEmailEmbedXml/EtaxByEmailAutoSendOnApprove` (`DatabaseMigrationHelper.cs:1964,1967`) + `EtaxMode.Both=3` (`AllEnums.cs:1368`) คงค่าเก่าพร้อม `ModeWarning` · DROP ในรอบเก็บกวาด schema | `DOCUMENT_FLOW.md` §e-Tax | A F-07 |
| 9 | ACCOUNT_STRUCTURE §6.1/§6.2b ระบุเพดานรายบริษัท 2 กลไก (`Subscription.Max*` vs `CompanyQuotaOverride`) + ลำดับชนะ · `PLATFORM_BILLING_AUDIT.md` §2 G-16 ปิด | `ACCOUNT_STRUCTURE.md` | A J-07 |
| 10 | `OMISE_READINESS §3`/deploy notes: "webhook ขาออกไม่ใช้ proxy ระบบ (`OutboundUrlGuard.cs:105 UseProxy=false`)" | `erp-review/.../OMISE_READINESS.md` | A H-03 |

**Definition of done:** `grep -rn "เติมในคอมมิตตามหลัง" DOCUMENT_FLOW.md CHANGELOG.md ACCOUNT_STRUCTURE.md TEST_PLAN.md` = **0** (ตอบตัวเลขในคอมมิต) · `python3 tools/doc_commit_sha_check.py` exit 0 · `grep -c "2026-11-0" docs/REVIEWS_INDEX.md` ≥ 20 · DECISIONS.md มีแถว 247–250 · sha ของคอมมิต doc นี้เองเติมใน**คอมมิตตามหลัง** (ห้าม amend) · รายงานผู้ใช้ว่า "คอมมิต doc ล้วน ไม่มีรอบ CI"

---

### WP-06 · แก้เลขมาตรา RETENTION 150 จุด (A I-01/I-03 · RETENTION_LAW_CHECK §2 + ภาคผนวก + §ฝ่ายค้าน) · effort 1 วัน · **1 คอมมิต** · **ฝ่ายค้าน 1 รอบ** (แตะข้อความกฎหมายที่ลูกค้าเห็น)

**เป้าหมาย:** ทุกการอ้างมาตรา พ.ร.บ.การบัญชี 2543 / ETA ในโค้ด·UI·เทสต์·CLAUDE.md ถูกต้องตามตัวบท — รายการ 152 แถวอยู่ใน `erp-review/2026-11-06/RETENTION_LAW_CHECK.md` ภาคผนวก (คอลัมน์ ชุด A/B/C/D · ไฟล์:บรรทัด · ข้อความเดิม · ข้อความใหม่) ⇒ **เดินตามภาคผนวกแถวต่อแถว** (A=100 · A-bare=15 · B=13 · C=6 · D=16 = 150 + D-ok 1 + — 1)

**4 เงื่อนไขฝ่ายค้าน (บังคับ — ถ้อยคำต้องตรงนี้):**

| ชุด | จาก → เป็น | ถ้อยคำบังคับ / ห้าม |
| --- | --- | --- |
| **A** (100+15) ระยะเก็บเอกสาร | ม.10 → **ม.14** | "ม.14 — เก็บ ≥ 5 ปีนับแต่วันปิดบัญชี (อธิบดีสั่งให้เก็บเกินได้ไม่เกิน 7 ปี)" · **ห้าม**ใส่ "โดยความเห็นชอบของรัฐมนตรี" (ไม่ยืนยัน) · EN: Section 10 → Section 14 · แก้ assert `Accounting.Tests/PlatformPriceOverrideRound231Tests.cs:324` ("ม.14") ในคอมมิตเดียวกัน · `Helpers/TrialDataRetentionPolicy.cs:12,27,28,73` "ท.ป.4/2528 เก็บ 5 ปี" → "ม.14 (+ ป.รัษฎากร §87/3)" (ท.ป.4/2528 = อัตราหัก ณ ที่จ่าย ไม่ใช่ระยะเก็บ) |
| **B** (13) รอบบัญชี | ม.11 → **ม.10** | ผู้อนุญาตเปลี่ยนรอบ = "สารวัตรใหญ่บัญชี/สารวัตรบัญชี" หรือเขียนกลาง ๆ "ต้องได้รับอนุญาต" · **ห้าม "อธิบดี"** · **DBD submission ที่อ้าง ม.11 (ยื่นงบ 5 เดือน/1 เดือนหลัง AGM) ไม่แตะ** (ม.11 ถูกอยู่แล้ว) · แก้ assert `AdversarialExportRound5Tests.cs:51,91` + `Db/AdversarialExportRound5DbTests.cs:257` ("ม.10") · `tools/required_call_site_check.py:9119` |
| **C** (6) "ม.11 ทวิ" (มาตราไม่มีอยู่) | → **พ.ร.บ.ธุรกรรมทางอิเล็กทรอนิกส์ 2544 ม.12** (+ **ม.12/1** เมื่อพูดถึงไฟล์สแกน) + การบัญชี ม.14 | คงหมายเหตุ "hash chain เป็น control ภายใน — ETA ม.12 ต้องการ 'แสดงความถูกต้อง/ไม่เปลี่ยนแปลง' hash chain เป็นวิธีหนึ่ง ไม่ใช่ข้อบังคับรายมาตรา" |
| **D** (16) ผู้ทำบัญชี | ม.7 → **ม.19 (ม.7(6))** | `settings.html:880` = "ม.19 + ม.40" (ม.40 ความรับผิดกรรมการ · เลี่ยงคำว่า "รับผิดทางอาญาโดยอัตโนมัติ") · `settings.html:878` "ผู้ทำบัญชี (ม.7)" → ม.19 · D-ok `Helpers/DocumentIssuerIdentity.cs:72` คงไว้แต่เขียน "ม.7(4)" ให้ชัด |

**จุดที่ A ยืนยันว่ายังค้าง (ตรวจ ณ fe9036f7):** `CLAUDE.md:296-298` (ม.7/ม.10/ม.11 — §H ข้อ ม.7 → ม.19 · ม.10 → ม.14 · ม.11 รอบบัญชี → ม.10 · DBD submission คง ม.11 + เติมที่มา I-03) · `Helpers/TrialDataRetentionPolicy.cs:12,27,28,73` · `settings.html:562` (ม.10) · `:878` · `:880` · "ม.11 ทวิ" 6 จุด · `Helpers/LegalDocumentDefaults.cs` + `.En.cs` ที่แตะ = **เวอร์ชันใหม่** (ไม่แก้ฉบับที่ลูกค้ายอมรับแล้ว · เจ้าของเผยแพร่ครั้งเดียวพร้อม Y6)

**ข้อห้าม:** ห้าม migrate audit row เก่า (`LegalReference` ใน hash chain — กฎ M) · ไม่แก้ `erp-review/*`/CHANGELOG ย้อนหลัง · ไม่แตะ `erp-review/2026-11-06/RETENTION_LAW_CHECK.md` นอกจากติ๊ก ✅
**T±:** เทสต์ assert 4 จุดข้างบนแก้พร้อม · เพิ่ม `LegalReferenceTests` (pure): `RuleCode` ทุกตัวใน `Helpers/*Retention*`/`TrialDataRetentionPolicy` อ้าง "ม.14" ไม่อ้าง "ม.10"/"ท.ป.4" · ทิศตรงข้าม: `LegalReference` ที่อ้าง ม.11 (DBD submission) ไม่ถูกแตะ · checker ใหม่ `tools/legal_citation_check.py` (ratchet): forbid regex `พ\.ร\.บ\.การบัญชี.*ม\.10\b.*เก็บ` · `ม\.11 ทวิ` · `ท\.ป\.4/2528.*เก็บ` ใน `Accounting/**` + `CLAUDE.md` + negative test
**เอกสาร:** CHANGELOG รอบนี้ระบุ**ตามเงื่อนไขฝ่ายค้าน**: "ยืนยันจาก snippet หลายแหล่งรวมสำเนาราชการ (dip.go.th · dbd.go.th PDF · rd.go.th) — ยังไม่ได้เปิดอ่านตัวบทเต็ม (รอ §3 N2)" · RETENTION_LAW_CHECK ติ๊ก ✅ sha ทุกแถว · DOCUMENT_FLOW §6.3 retention (ข้อความมาตรา) · CLAUDE.md §H
**Definition of done:** `grep -rn "ม\.11 ทวิ" Accounting/ CLAUDE.md` = 0 · `grep -rn "ท.ป.4/2528" Accounting/Helpers/TrialDataRetentionPolicy.cs` = 0 · checker ใหม่ baseline 0 · CI 4 job เขียว · ฝ่ายค้านยืนยันถ้อยคำ 4 เงื่อนไขตรง · ถ้าเจ้าของตอบ N2 ว่าต่าง ⇒ แก้ตามในคอมมิตตามหลัง

---

### WP-07 · งานค้างรอบ 248 Autopilot (A B-12 · B-16 · B-17/F-06 · B-18 · B-19) · effort 1½ วัน

**เป้าหมาย:** แผงความพร้อม/ทดลองรันบอกความจริงครบ (e-Tax · QR · โควตา · งวด · หลังรับเงิน) · ตารางเห็นระดับ · ส่งทดสอบได้ · Db test เส้น enqueue→send

| # | รายการแก้ | ที่ | ที่มา |
| --- | --- | --- | --- |
| 1 | **คอลัมน์ "ระดับ Autopilot 🟢/🟡/🔴"** ในตารางรายการประจำ — คืน `AutopilotReadiness` ใน list DTO (เซิร์ฟเวอร์คำนวณ · ไม่คำนวณใน JS) | `RecurringTransactionService.MapToResponse :1070-1072` · `recurring.html` ตาราง (`grep "ระดับ Autopilot"` = 0) | A B-12 |
| 2 | **e-Tax ในทดลองรัน**: `RecurringAutopilotService.BuildAsync :110-305` เรียก `EtaxDeliveryRouter.Decide` + `EtaxByEmailSenderGate.Refusal` (อ่านอย่างเดียว) · รหัสใหม่ `ETAX-SENDER` (SMTP บริษัทยังไม่ทดสอบผ่าน) · `ETAX-RECIPIENT` (ผู้ติดต่อหลายอีเมล/ตารางมี CC/BCC ⇒ จะล้มดังตอนส่ง) ใน `AutopilotReadiness.Evaluate` · ข้อความในแท็บช่องทางแจ้งเตือนผู้ติดต่อเมื่อบริษัทเป็นโหมด ByEmail | `RecurringAutopilotService.cs` (grep `EtaxDeliveryRouter` = 0) · `Helpers/AutopilotReadiness.cs:90-190` · `contacts.html` แท็บ L1 | A B-17 · F-06 |
| 3 | **4 รหัสความพร้อมใหม่** จากผู้อ่านเดิม: `PAY-QR` (`PaymentQrPolicy.Pick` — บัญชีมี QR/พร้อมเพย์ไหม) · `QUOTA` (`NotifyQuotaPolicy` — โควตาอีเมล/LINE พอรอบหน้าไหม) · `PERIOD` (`ClosedPeriodRanges` — งวดรอบหน้าปิดแล้วไหม) · `POSTPAY` (`PostPaymentAutomationPolicy.Effective` — ค่าตั้งหลังรับเงินครบไหม) · รหัสที่มีแล้ว: APPROVE BUYER-TAX CONTACT GATEWAY OWNER-POLICY RECIPIENT SMTP TEMPLATE THB (+ ETAX-* ข้อ 2 · GATEWAY/BaseUrl จาก WP-02) | `Helpers/AutopilotReadiness.cs` · `RecurringAutopilotService.BuildAsync` | A B-18 |
| 4 | **`POST recurring/{id}/send-test`** — ส่งตัวอย่างอีเมลรอบถัดไปถึง**ผู้ใช้ปัจจุบันเท่านั้น** ผ่าน `OnBehalfEmailSender` · ไม่ตัดโควตา · ไม่บันทึก `DocumentEmailLog` เป็นการส่งจริง (ติดป้าย test) · `[RejectApiKey]` · สิทธิ์สร้างเอกสาร · ปุ่มใน wizard ขั้น "ส่ง" | `RecurringController.cs` · `recurring.html` (grep "ส่งทดสอบ" = 0) | A B-16 |
| 5 | **Db test MED-5**: `RecurringAutopilotRound248DbTests.R248_MED5_TransferOnly_รายการ_อีเมลที่ส่งไม่มีลิงก์บัตร` (enqueue ด้วยรายการ `TransferOnly` ขณะบริษัท Gateway ⇒ ตอนส่งคิว `RecheckDocumentAtSendAsync :839-867` ใช้ `PaymentMode/PayLinkChannels` ของรายการ) + ทิศตรงข้าม `Inherit ⇒ มีลิงก์` — (ซ้ำกับ WP-02 T± · ทำที่เดียว) | `Accounting.Tests/Db/RecurringAutopilotRound248DbTests.cs` | A B-19 |
| 6 | ทดลองรัน/readiness UX: ผล "ทดลองรัน" แยกกล่องจาก `#readinessBody` (:467–476 · :272 ทับใน 600ms) · เพิ่มบรรทัด (:928–931) ข้ามบรรทัดว่างในโหมดตรวจ (:438, :973) | `recurring.html` | D §1.8–1.9 |

**T±:** `AutopilotRound248Tests` เพิ่มต่อรหัส: ETAX-SENDER Fail/ผ่าน · ETAX-RECIPIENT Fail/ผ่าน · PAY-QR · QUOTA · PERIOD · POSTPAY (แต่ละตัวมี Fail + ทิศตรงข้าม Ok) · Db `send-test ไม่สร้าง DocumentEmailLog จริง ไม่ตัดโควตา ส่งถึงผู้ใช้เท่านั้น` + ทิศตรงข้าม "ผู้ใช้ไม่มีอีเมล ⇒ 409"
**R:** R248 เพิ่ม `BuildAsync` must `EtaxDeliveryRouter.Decide(` · `EtaxByEmailSenderGate.Refusal(` · `PaymentQrPolicy.Pick(` · `NotifyQuotaPolicy.` · `ClosedPeriodRanges.` · `PostPaymentAutomationPolicy.Effective(` · `SendTest` must `OnBehalfEmailSender` + forbid `DocumentEmailLogs.Add` (หรือ must ติดป้าย test) · `owner_action_wiring_check` R248-K4 `[RejectApiKey]` บน send-test
**เอกสาร:** DOCUMENT_FLOW §2.7 (รหัสความพร้อมครบชุด + send-test) · AUTOPILOT_BILLING_DESIGN §4.6/§12 ติ๊ก · CHANGELOG · TEST_PLAN
**Definition of done:** บริษัทโหมด ByEmail ที่ SMTP ยังไม่ทดสอบ ⇒ readiness ไม่ใช่ Full และทดลองรันบอก "จะล้มที่ด่านผู้ส่ง" · ตารางแสดงระดับ · ทุกรหัสมี T±

---

### WP-08 · ต่อสาย L1 + สินทรัพย์ P2 (A E-01 · C-01 · C-05 · D-03) · effort 2 วัน · **ฝ่ายค้าน 1 รอบ** (แตะผู้รับจริง/PDPA)

> **สถานะ (2026-11-06):** ✅ ลงมือแล้ว ข้อ 1–5 (commit <เติมในคอมมิตตามหลัง>) — Db `ContactRecipientWiringRound250DbTests` · `AssetEditsRound250DbTests` · R250L1/R250FA ·
> ขยาย 250-29 ให้ "ไม่ส่งอัตโนมัติ" ครอบทุกเส้นอัตโนมัติ (DECISIONS 250-WP08-2) · ทางเข้าที่ไม่ต่อสาย (ส่งเองจากหน้าเอกสาร · BookingNotifier) จดเป็น backlog ใน DOCUMENT_FLOW §2.12h · ยังไม่ได้คอมไพล์ในเครื่องนี้ · รอฝ่ายค้านจากผู้ประสานงาน

> **หมายเหตุลำดับ:** 246-QR-10 (block pay link เมื่อมีใบกำกับแล้ว) ถูก**ดึงขึ้นไป WP-02 §2.3** เพราะเป็นเงิน P1 (รับชำระซ้ำ) — ไม่ทำซ้ำที่นี่

**เป้าหมาย:** ค่าตั้ง "ช่องทางแจ้งเตือน" ของผู้ติดต่อ (L1 รอบ 245) มีผลต่อการส่งจริงทุกเส้น · ราคาสัญญา/วันพร้อมใช้แก้ได้

| # | รายการแก้ | ที่ | ที่มา |
| --- | --- | --- | --- |
| 1 | **`ContactRecipientResolver` เข้า 3 เส้นส่ง** (ตามแผน L1 · DECISIONS 245-L1-7): `EmailScheduleService.EnqueueDocumentAsync :758-760` (ยังอ่าน `Contact.Email`) · `PostPaymentAutomationService.cs:268` · `SignRequestService` — ถามเรื่อง `invoice`/`receipt`/`sign` ก่อนตก `Contact.Email` · ผู้ติดต่อที่ "ยกเลิกรับ" ⇒ ไม่ส่ง + บันทึกเหตุบนแถวคิว (ล้มดัง) · `RecurringEmailReadiness` (`Helpers/RecurringRunOutcome.cs:49-65` · CHANGELOG:6230-6231 ใช้ `Contact.Email` ชั่วคราว) อ่าน resolver ตัวเดียวกัน · แท็บ L1 `contacts.html` เอาคำเตือน "ยังไม่มีผล"/"(เมื่อเส้นส่งต่อสาย L2)" (:1250, :1304) ออก → เซิร์ฟเวอร์ส่งประโยคพร้อมแสดง | `EmailScheduleService.cs:755-800` · `PostPaymentAutomationService.cs:268` · `SignRequestService.cs` · `Helpers/RecurringRunOutcome.cs:49-65` · `contacts.html:1250, :1304` | A E-01 · B F-5 · D §6.3 |
| 2 | **`AutoSendDisabled`** = ชั้น "ผู้ติดต่อ" ใน `AutopilotSettingsResolver` (คอลัมน์ `Contact.AutoSendDisabled` · migration · echo · hydrate ใน `recurring.html` wizard + `contacts.html` แท็บ L1) · ผู้อ่าน: `OnRecurringDocumentCreatedAsync` + `PostPaymentAutomationService` | `Helpers/AutopilotSettingsResolver.cs:74-100` · `Models/Entities/Contact.cs` · `DatabaseMigrationHelper` | A D-03 |
| 3 | **ราคาตามสัญญาแก้หลังสร้าง**: ช่อง `srcContractPrice` ใน modal แก้ไข (ตอนนี้เฉพาะ create `:231, :886, :977` · อ่านอย่างเดียว `:1029`) + `PATCH fixed-assets/{id}/contract-price` (สิทธิ์ `Asset.Manage` · audit chain) · คง `ContractPriceWarning` (`FixedAssetService.CostSources.cs:859`) เป็นคำเตือน | `fixed-assets.html:231, :886, :977, :1029` · `FixedAssetController` · `FixedAssetService.CostSources.cs` | A C-01 |
| 4 | **วันพร้อมใช้ของสินทรัพย์ใบเดียว**: ช่อง "วันที่พร้อมใช้งาน" (`AvailableForUseDate`) บนทะเบียนที่สร้างจาก 1 บรรทัด (auto-register ตอนอนุมัติ `FixedAssetService.cs:1602-1617` + OCR) · แก้ได้จนกว่ามีค่าเสื่อมลงบัญชี · ว่าง = วันที่เอกสาร (ไม่ถดถอย) · ตัวตั้งต้นค่าเสื่อมใช้ `AvailableForUseDate ?? DocumentDate` ผ่าน helper เดียว (`DepreciationSchedule` — ไม่ inline) | `FixedAssetService.cs` · `Helpers/DepreciationSchedule.cs` · `fixed-assets.html` | A C-05 |
| 5 | fixed-assets `confirmPlaceInService` (:1050) ตรวจ `placeLife > 0` เมื่อวิธี ≠ None · วันที่ตั้งต้น `Date.now()+7h` (:1031) → `Layout.toDateInput(new Date())` | `fixed-assets.html:1031–1050` | D §2.12 |

**T±:** Db `ContactNotifyEndpointRound245DbTests` เพิ่ม `DueSoon_ส่งถึงอีเมลที่สองของผู้ติดต่อ` · `ยกเลิกรับ_ไม่ส่ง_บันทึกเหตุบนคิว` · ทิศตรงข้าม `ไม่ตั้งปลายทาง_ตก Contact.Email เดิม` · `PostPayment ใช้ resolver เรื่อง receipt` · `SignRequest ใช้ resolver เรื่อง sign` · `AutoSendDisabled_ผู้ติดต่อ_ไม่เข้าคิว` + ทิศตรงข้าม · `AssetMultiDocumentDbTests`: `Contract_price_patch_requires_Asset_Manage_and_audits` · `Single_line_asset_available_date_editable_until_depreciation_posted` + ทิศตรงข้าม `…_locked_after_first_depreciation` · `Empty_available_date_falls_back_to_document_date`
**R:** R245 เพิ่ม: `EnqueueDocumentAsync` · `PostPaymentAutomationService.SendReceiptAsync` · `SignRequestService.Send*` must `IContactRecipientResolver.ResolveAsync(` · **forbid `.Contact.Email`/`contact.Email` เป็นผู้รับ** ใน 3 ไฟล์ · `RecurringEmailReadiness` must resolver · R249FA เพิ่ม `PatchContractPriceAsync` must `AddChainedAuditLog(`
**เอกสาร:** DOCUMENT_FLOW §2.12h (L1 มีผลแล้ว — ลบประโยค "L2 ค่อยต่อ") · §6 สินทรัพย์ (ราคาสัญญาแก้ได้ · วันพร้อมใช้ใบเดียว) · AUTOPILOT §12 บรรทัด 3 แก้ · LINE_MULTICHANNEL_DESIGN §8 L1 ติ๊ก · ASSET design §6 Q1/Q5 ติ๊ก · DECISIONS 250-x · CHANGELOG · TEST_PLAN
**Definition of done:** `callers.py ContactRecipientResolver` แสดงผู้เรียกฝั่งส่ง ≥ 3 เส้น (ตอบตัวเลข) · `settings_reader_check` ไม่มี `ContactNotifyEndpoints.*` ใน baseline "ไม่มีผู้อ่าน" · ฝ่ายค้าน: "ทางเข้าอื่นที่ส่งถึงผู้ติดต่อ (LINE booking รอบ 211/212 · ทวงหนี้ · ใบเสนอราคา) เดินด่านเดียวกันหรือจดเป็น backlog"

---

### WP-09 · 50 ทวิ "เห็นก่อน–รับเข้าได้" (AUTO_SEND_WHT_DESIGN §9 เฟส 243 + 244a · A D-01 · D-04 · D-05 · D-11 · D-12) · effort 4–5 วัน · **ฝ่ายค้าน** · กฎเหล็ก #1 ครบ

> ตรวจ ณ fe9036f7: `WhtCertIntakeStatus` · `WhtCertIntakeService` · `WhtCertUploadLink` · `WhtCertAllocation` · `WhtCertReminderJob` · `WhtCreditableAmount` · `wht-upload.html` · `WhtCertPublicController` = **ไม่มีทั้งหมด** (grep 0) — เฟส 243–247 ยังไม่เริ่ม · สิ่งที่มี: ตัดสูญ 11910 (243a) · ตรวจแถวรอใบ (241) · `WhtCertRequestText` ในอีเมลใบเสร็จ · จุดต่อว่าง `ReceiptEmailSections` (`DocumentService.cs:16609, :20031`)

| # | รายการ | รายละเอียดจากแผน | ที่มา |
| --- | --- | --- | --- |
| 1 | `Helpers/WhtCertIntakeStatus` (pure · 10 สถานะ) + ตัวตัดสินเดียว · ชิป+ตัวกรองใน `documents.html` · การ์ดผู้ติดต่อ + แดชบอร์ด "50 ทวิ ที่ต้องตาม" · `GET auto-send/matrix` · **R243**: ห้ามเทียบ `WhtCreditStatus.Pending` ตรงใน mapper (must ผ่าน helper) | AUTO_SEND §9 เฟส 243 ข้อ 1–4 | A D-01 |
| 2 | รายงานปลายปี "50 ทวิ ที่ยังขาด" + Excel (MiniExcel) บน `wht-credit.html` (ตอนนี้มี KPI ตัดสูญเท่านั้น) + escalation §4.4 — ตัวตัดสินเดียวกับข้อ 1 | AUTO_SEND §4.5/§4.4 | A D-11 |
| 3 | **244a ประตูเดียว** `WhtCertIntakeService.SubmitAsync` · `Helpers/WhtCreditableAmount.Of` (ใบ 1 ใบครอบหลายใบแจ้งหนี้ / ผ่อน 3 งวดได้ใบงวดแรก ⇒ เครดิตตามใบจริง ไม่ใช่ทั้งแถว) · `WhtCertAllocation` · ย้าย `OcrService.EnsureWhtCreditFromCertAsync` (เส้นสแกนเดิม) มาเรียกประตูเดียวกัน · **R243**: ทุกจุดที่ตั้ง `WhtCreditReceived` must ผ่าน `SubmitAsync` | AUTO_SEND §9 เฟส 244 | A D-04 |
| 4 | **AI ตรวจของจริง** (§2 #31): `AiFeatureKey.WhtCertAuthenticity` ใน `Models/Enums/AllEnums.cs` · `GenericFeedbackDistillationModel(featureKey)` ใน `Program.cs` · เรียกผ่าน `IAiOrchestrator.AskAsync` เท่านั้น · ด่านเชิงกำหนดก่อน (checksum mod-11 · คู่สัญญาตรง · ยอด/ปีภาษีตรงแถวรอ) · เก็บ `WhtCertAuthenticityAiFeedbackId` + `UsedAi` · ผู้ใช้ยืนยัน/แก้ ⇒ `RecordUserChoiceAsync` · kill-switch ⇒ local "ไม่มั่นใจ" ⇒ ผู้ใช้กดยืนยัน (ไม่ auto) · ป้าย 🤖/⚙️ ซื่อสัตย์ | AUTO_SEND §11 ข้อ 1–2 · CLAUDE #1 | A D-05 |
| 5 | WHT บน**ส่วนเสริม** `CompanyFeature` (grep `WithholdingTax` ใน `CompanyFeature*.cs` = 0): คอลัมน์ `WithholdingTaxAmount` + `PlatformWhtSuggestion` ตัวเดิม แบบเดียวกับ `CompanyQuotaAddOns` รอบ 235 | DECISIONS 234-6 · CHANGELOG:6279-6282 | A D-12 |
| 6 | **R243 ข้อ D-13**: ทุกจุดที่เรียก `EnqueueAsync` ต้องมี `SyncWhtCreditReceivedAsync` ก่อน — ปิดแล้วใน WP-02 §2.1 ข้อ 4–5 (ตรวจซ้ำที่นี่ว่ากติกายังอยู่) | — | A D-13 |

**T±:** `WhtCertIntakeStatusTests` ตาราง 10 ค่า + ทิศตรงข้าม · `WhtCreditableAmountTests` (ใบรวม · ผ่อนงวด · ใบเกินยอด ⇒ ปฏิเสธ) · Db `WhtCertIntakeRound252DbTests`: Submit จากสแกน/มือ/API ได้แถวเดียวกัน · ซ้ำ = idempotent · AI kill-switch (ปิด provider ทุกตัว) ⇒ สถานะ "รอผู้ใช้ยืนยัน" ไม่ใช่ auto · ส่วนเสริมนิติบุคคลหัก 3% · บุคคลธรรมดาไม่หัก
**เอกสาร:** DOCUMENT_FLOW §ใหม่ "50 ทวิ ขาเข้า" · ACCOUNT_STRUCTURE §3.1e-2 (11910) · AUTO_SEND §9 ติ๊ก · DECISIONS · TEST_PLAN · CHANGELOG
**Definition of done:** ปิดปีแล้วรายงานบอกจำนวนใบที่ขาดเป็นตัวเลข · เครดิต ภ.ง.ด.50 นับจาก `WhtCreditableAmount` ไม่ใช่ทั้งแถว · `AskAsync` มี student ลงทะเบียน (feature parity) · ฝ่ายค้าน 3 คำถาม

---

### WP-10 · ลิงก์อัปโหลด + ทวงอัตโนมัติ + "ไม่หัก" บนหน้าชำระ + e-WHT ขั้นแรก (AUTO_SEND 244b + 245 · A D-04b · D-06 · D-07 · D-08 · B-06) · effort 4 วัน · **ฝ่ายค้าน** · ต้องมี WP-08 (resolver) + WP-09 ก่อน

| # | รายการ | รายละเอียด | ที่มา |
| --- | --- | --- | --- |
| 1 | **244b** `wht-upload.html` สาธารณะ + `WhtCertPublicController` (`[AllowAnonymous]` · token hash ค้น+เทียบเวลาคงที่แบบ `DocumentPayLinkService.PublicViewAsync :366-371` · rate limit แบบ `StartAsync :218-229` · magic bytes · hash ไฟล์ · ขนาด) · ลิงก์ในอีเมลใบเสร็จต่อที่ `ReceiptEmailSections` (`DocumentService.cs:16609, :20031`) · เข้า `SubmitAsync` ประตูเดียว (WP-09) | AUTO_SEND §9 เฟส 244b | A D-04 |
| 2 | **`WhtCertReminderJob`** ทวง 7/21/45 วัน + รายเดือน ≤ 6 (เจ้าของอนุมัติ §11 ข้อ 4) · `BackgroundService` ผ่าน `Helpers/JobLock` (F4 ข้อ 6) · watermark ต่อบริษัท (#4 D) · 1 อีเมล/ลูกค้า/รอบ · `OnBehalfEmailSender` + `ContactRecipientResolver` (เรื่อง `wht-cert`) · ไม่สร้างแถวรอใบเอง | AUTO_SEND เฟส 245 | A D-07 |
| 3 | **"ไม่หัก ณ ที่จ่าย" บนหน้าชำระ** (`pay-document.html` 📋): ตัวเลือก + คำเตือนนิติบุคคล + **บันทึกการยอมรับ** (ผู้กด·เวลา·IP·ข้อความ·version·hash) → `PaymentIntent` ยอดเต็ม (Gross) + ป้ายฝั่งผู้ขาย · วันนี้ลิงก์ล็อกยอดสุทธิ 244-7 | AUTO_SEND §11 ข้อ 3 · §6.3 | A D-06 |
| 4 | **e-WHT ขั้นแรก** E1+E7+E8 ตามลำดับ EWHT_EVIDENCE §4 (ช่องทางหลักฐาน `EvidenceKind` ใหม่ — **ไม่ใช่** `OcrTotalAnchor.EvidenceKind` คนละเรื่อง) · **ไม่เสนอ 1%** (§2 #32) · ทำเมื่อ §3 N4 ตอบ Q-E1 แล้ว · ระหว่างรอ = แนบเป็นใบ 50 ทวิ ธรรมดา | EWHT §4 · §7 | A D-08 |
| 5 | **คำเตือนวงเงินเครดิต** `[CREDIT-LIMIT]` ใน `AutoApprovePolicy.Catalogue` (`:58-62`) จาก `Contact.CreditLimit` (DocumentService grep = 0) — คำเตือน ไม่บล็อก · ปล่อยได้ด้วยนโยบาย · ทิศตรงข้าม ไม่ตั้ง CreditLimit ⇒ ไม่ฟ้อง | AUTOPILOT Q6 | A B-06 |

**T±:** Db `WhtCertPublicUploadDbTests` (token ปลอม = ไม่เปิดเผยอะไร · ไฟล์ไม่ใช่ PDF/รูป ⇒ ปฏิเสธ · เกิน rate ⇒ 429 · อัปโหลดสำเร็จ = แถว intake เดียว) · `WhtCertReminderJobDbTests` (วัน 7/21/45 · ไม่ซ้ำในรอบเดียว · หยุดเมื่อได้ใบ · JobLock กันรันซ้ำ) · `NoWhtAcceptanceDbTests` (นิติบุคคลเลือกไม่หัก ⇒ intent = Gross + แถว acceptance hash ตรง · บุคคลธรรมดาไม่เห็นตัวเลือก) · `AutopilotRound248Tests` CREDIT-LIMIT 2 ทิศ
**R:** R243/R244 ใหม่: `WhtCertPublicController` must `RateLimit` + `MagicBytes` + `SubmitAsync(` · `WhtCertReminderJob` must `JobLock.RunExclusiveAsync(` + `IContactRecipientResolver` · `PublicPaymentController.StartAsync` must อ่าน `NoWhtAcceptance` ก่อน `AmountsFor(`
**เอกสาร:** DOCUMENT_FLOW §50 ทวิ ขาเข้า (ลิงก์ · ทวง · ไม่หัก) · AUTO_SEND §9 ติ๊ก · EWHT §4 ติ๊ก · DECISIONS · CHANGELOG · TEST_PLAN

---

### WP-11 · LINE หลายช่องทาง L2 → L4 (OA ของบริษัทก่อน) (A E-02 · E-04 · E-09 · E-10 · E-11 · E-12 · E-13 · E-14 · B-15 · E-05/E-06 backlog) · effort 5+ วัน · **ฝ่ายค้าน** · ต้องมี WP-08 (L1 ต่อสาย) ก่อน

> ตรวจ ณ fe9036f7: `LineQueue` · `LineGroupEventHandler` · `LineGroupCommandParser` · `LineDocumentCard` · `ContactLinkInvite` · `X-Line-Retry-Key` · `NextAccOaQuotaWatch` · `notify.topup.line` · `MaxSharedLineMessagesPerMonth`/`MaxSharedEmailsPerMonth` = ไม่พบ · `ContactRecipientRule.cs:100,131` `lineDeliveryOpen=false` ล็อกไว้

| # | รายการ | รายละเอียด | ที่มา |
| --- | --- | --- | --- |
| 1 | **ทำได้ทันที (ไม่รอ E-08)**: `X-Line-Retry-Key` ทุก push ใน `LineNotifyService.PushCoreAsync` ตัวเดียว (key = hash(เหตุการณ์+ผู้รับ)) — ครอบของเดิมรอบ 211/212/232/249 · แม่แบบ `BookingNotifier` รอบ 211 เติม 2 บรรทัด: ป้าย "ผ่าน NextAcc" + ช่องทางยกเลิก (LINE_OA §5 · C2/C5) · เติม §ใหม่ใน LINE_MULTICHANNEL_DESIGN "เงื่อนไข C1–C10 → จุดในโค้ด" | NOTIFY §5 ข้อ 8 · LINE_OA §7 ข้อ 3 | A E-13 · E-09 |
| 2 | **L2** — คิว LINE (`LineQueue` แบบ `EmailScheduleService` · SKIP LOCKED · lease) · Flex การ์ดเอกสาร (`LineDocumentCard` ผ่าน `DocumentLabels`) · ลิงก์ดูเอกสาร (ผ่าน `NotificationLink.Absolute` — ไม่ตั้ง BaseUrl = ไม่ส่งลิงก์) · ติ๊กช่องทางต่อกฎ (`EmailScheduleRule.Channels` flags) · ส่ง 1:1 ผ่าน **OA ของบริษัทเท่านั้น** · `LineFallbackToEmail` ค่าตั้งต้น **true** (เจ้าของ Q7 · DECISIONS 245-L1-3) · ปลด `lineDeliveryOpen` ใน `ContactRecipientRule.cs:100,131` เฉพาะเส้นนี้ · ช่อง LINE ใน wizard ขั้น "ส่ง" (`recurring.html` — B-15) · ปุ่ม LINE ในแท็บผู้ติดต่อเลิก "เร็ว ๆ นี้" | LINE §8 L2 | A E-02 · E-12 · B-15 |
| 3 | **หน่วยนับกลุ่ม** — ทดสอบจริง 10 นาทีบน OA ทดสอบ (กลุ่ม 3 คน + บอท → push 1 → ดู `/quota/consumption` เพิ่ม 3 หรือ 4) ก่อน L3/L4 · บันทึกผลใน NOTIFY §5 ข้อ 10 · แก้สูตร `max(1, memberCount − 1)` ตามผล | LINE §7.4 | A E-11 |
| 4 | **L4 โควตาแยกช่อง** (หลัง §3 N7 ราคา + N9 SQL): `NotifyUsageCounter.Channel` · `PlanTemplate.MaxSharedLineMessagesPerMonth/MaxSharedEmailsPerMonth` (ค่าตั้งต้น LINE 100/300/1,000/5,000 · อีเมล 300/1,500/6,000/−1) · `notify.topup.line` (฿290/1,000) · `notify.topup.email` (฿190/5,000) · `HeldQuota` · อีเมลผ่าน SMTP NextAcc ตัดโควตารายเดือน (วันนี้มีแต่เพดานรายวัน `SignRequestEmail:PlatformDailyCapPerCompany`=200) · บริษัทที่เกิน P95 ⇒ ทับรายบริษัท `ceil(peak×1.2)` · ปิดขาย `notify.topup` ก้อนรวม ฿99/500 (โบนัสเดิมใช้ได้ทั้งสองช่องจนหมด) · ตัวนับ reply/เดือนของ OA กลางใน `NextAccOaQuotaWatch` (ไม่ตัดโควตาผู้เช่า) | LINE §8 L4 · NOTIFY §5 ข้อ 2–7, 9 | A E-04 · E-10 · E-14 |
| 5 | **L3 กลุ่ม + `#HeyNextAcc` + webhook** — ผ่าน OA บริษัทเองก่อน · OA กลาง**รอ §3 N8** (หนังสือจาก LINE + สวิตช์แอดมิน) | LINE §8 L3 · LINE_OA §7 | A E-03 · E-08 |
| 6 | **backlog**: L5 เชิญผู้ใช้ของผู้ติดต่อ + QR ผูก + พอร์ทัลไม่มีรหัสผ่าน — รวมกับ AUTOPILOT รอบ 254 (`PdpaConsentRecord` ตัวเดียว) · L6 ทวง 50 ทวิ ทาง LINE + ลบ `Contact.LineUserId` เมื่อ `settings_reader_check` ยืนยัน 0 ผู้อ่าน (ผู้อ่านปัจจุบัน `CustomerLineLink` บางจุด) | LINE §8 L5/L6 | A E-05 · E-06 |

**T±:** `LineQueueDbTests` (retry key คงที่ต่อเหตุการณ์ · timeout ส่งซ้ำไม่จ่ายสอง · fallback อีเมลเมื่อโควตา LINE เต็ม + ทิศตรงข้าม `LineFallbackToEmail=false` ⇒ Failed พร้อมเหตุ · OA กลางถูกปฏิเสธสำหรับผู้เช่า `NOTIFY-TENANT-NOT-PLATFORM`) · `LineDocumentCardTests` (HtmlEncode · ป้ายผู้ส่ง · ช่องทางยกเลิก — C2/C5 เป็น assert) · `NotifyQuotaPolicyTests` ต่อช่อง + ทิศตรงข้าม "โบนัสเดิมใช้ได้ทั้งสองช่อง"
**R:** `LineNotifyService.PushCoreAsync` must `X-Line-Retry-Key` · forbid `PushMessageAsync(` นอก `PushCoreAsync` · `LineQueue` worker must `JobLock` หรือ SKIP LOCKED · `NotifyUsageCounter` must `Channel`
**เอกสาร:** DOCUMENT_FLOW §2.12h (LINE ส่งจริง) · ACCOUNT_STRUCTURE §6 โควตา 2 ช่อง + ราคา · LINE design §8 ติ๊ก · NOTIFY §5 ผลทดสอบหน่วยนับ · DECISIONS · CHANGELOG · TEST_PLAN

---

### WP-12 · Autopilot รอบ 250–254 ตามแผน A (AUTOPILOT_BILLING_DESIGN §1.2 A-5…A-7 · §5–§8 · A B-04 · B-07 · B-08 · B-10 · B-14 · B-20 · B-02 · B-03/B-11) · effort 2–3 สัปดาห์ · ทำหลัง WP-02 (A-4 ปิดแล้ว) + WP-08

| ลำดับ | ชุด | เนื้อหา | ค่าตั้งต้น/เงื่อนไข | ที่มา |
| --- | --- | --- | --- | --- |
| 1 | **หน้า "เลขบัญชี + QR + แจ้งโอน"** (AUTOPILOT §5.6–5.7) + `SlipEvidenceService` — สำหรับ `TransferOnly` (วันนี้ไม่มีที่แจ้งสลิป) · ใช้ endpoint อัปโหลดสลิปเดียวกับ `portal.html:515–526` · เพิ่มบน `pay-document.html:83–87` + ปุ่ม "คัดลอกเลขบัญชี" | ไม่ประทับ "ชำระแล้ว" โดยไม่มีคน | A B-20 · D §10.5 |
| 2 | `RecurringTransaction.IntervalMonths` + `LeadDays` (ทุก N เดือน · สร้างล่วงหน้า N วัน) — migration · echo · hydrate · `AutopilotReadiness` รู้จัก | ราคาแปรผัน/มิเตอร์ = backlog 255 | A B-14 |
| 3 | **A-5** อีเมลสถานะถึงทีมเรา: ล้ม/ติด = ทันที · สำเร็จ = digest 08:40 (`NotificationEvents` E-01…E-15) | ตามทีม | A B-07 |
| 4 | **A-6** `autopilot.html` แดชบอร์ด · **A-7** `QuotationAcceptAutopilotJob` ค่าตั้งต้นบริษัท `Off` (ตั้งต่อใบ) | `Off` | A B-05 |
| 5 | **รอบ 250 AUTOPILOT**: ปิดหนี้จาก statement (`BankReceiptAutoSettle`) ค่าตั้งต้น**ปิด** + จำลอง 30 วันก่อนเปิด · ลำดับธนาคาร/feed ตาม §3 N11 (ไม่รู้ ⇒ นำเข้า statement รายวัน) | ปิด | A B-04 · B-08 |
| 6 | **รอบ 251** คู่ค้า NextAcc: ต่อยอด `TradingPartnership` (`CrossTenantWorkflow.cs:22`) — **ไม่สร้างใหม่** · `PartnerDiscoverable` ค่าตั้งต้นปิด (บอกแค่สถานะการเชื่อม) · ทุกแพ็กเกจไม่มีเพดาน (จนกว่า §3 Y5) · scope `documents:read` แยกใน partner API (A H-06) | ตามทีม | A B-02 · B-10 · E M-6 |
| 7 | **รอบ 252** `BillAutoPost` ฝั่งคู่ค้า: **รอ §3 N3** — ระหว่างรอส่งใบซื้อเป็น**ร่าง** + `PaperRequired` (ไม่ลงภาษีซื้อเอง) · ค่าตั้งต้นปิด · เพดานยอดต่อคู่ | ร่าง+PaperRequired | A B-03 · B-11 |
| 8 | **รอบ 253** แลกเปลี่ยน 50 ทวิ ผ่านคู่ค้า (ต้อง WP-09/10 ก่อน) · **รอบ 254** พอร์ทัลไม่มีรหัสผ่าน (ลิงก์วิเศษ/OTP) รวม LINE L5 · **รอบ 255** อีเมลขาเข้า (กล่องรับ ETDA stamp — A F-03) + ราคาแปรผัน + ส่งออกนักบัญชี (Excel ทั่วไปก่อน · PEAK/FlowAccount/Express เมื่อมีไฟล์ตัวอย่าง — B-09) | | A |

**กติกาเดียวกันทุกชุด:** ไม่มี AI call ใหม่โดยไม่มี student (WP-09 ข้อ 4 เป็นแม่แบบ) · ทุก BackgroundService ใหม่ผ่าน `JobLock` · ทุกค่าตั้งใหม่ต้องมีผู้อ่านเชิงธุรกิจ + อยู่ใน `settings_reader_check` ENTITIES (WP-04 ข้อ 10) · ทุกสถานะ "ภายนอกรับแล้ว" ตั้งได้เฉพาะเมื่อภายนอกตอบกลับจริง

---

### WP-13 · UX ย่อยที่เหลือจาก D (checklist · ทำเป็นรอบเก็บกวาดหลัง WP-03 · effort 3–4 วัน · ไม่ต้องฝ่ายค้าน ยกเว้นข้อ 🖥 ที่แตะสิทธิ์)

**13.1 Cross-cutting (ทำครั้งเดียวได้ผลหลายหน้า — D §11)**

- [ ] **A** `Layout.debouncedLoader(fn, 300)` + seq-guard → ใช้ที่ `recurring.html:18, :559–594` · `fixed-assets.html:53`
- [ ] **B** `Layout.openModal` focus `[autofocus], input, select, textarea, button` ตัวแรก + รองรับ `data-enter-target` (Enter = "ถัดไป" ใน wizard ไม่ใช่ `.btn-primary` ตัวสุดท้าย `layout.js:2744–2777, :2770–2777`)
- [ ] **C** `Layout.confirm()`/`Layout.promptText()` แบบ Promise ตัวเดียว แทน native: `recurring :1317` · `fixed-assets :662, :1014, :1171` · `notifications :253` · `documents :12371–12398, :13229` · `settings :2413` · `bank :2787` · `contacts :1292, :1297` · `email-schedule :344, :400` · `account-subscription :258–298` · หน้าสาธารณะ (`delivery-sign :99, :101, :120` · `pay-link :94, :100, :101, :106, :118, :119, :135, :140, :141`) ใช้กล่อง inline `role=alert` แทน `alert()`
- [ ] **D** checker label `for` (WP-04 ข้อ 13) + แก้ `recurring`/`fixed-assets`/`settings` ทั้งฟอร์ม · ปุ่มไอคอน `🤖`/`✕` ใส่ `aria-label` (`recurring :150` · `fixed-assets :135`)
- [ ] **E** `style.css:958–964` เพิ่ม selector `[style*="grid-template-columns:1fr 1fr"]`, `[style*="grid-template-columns: 1fr 1fr"]` (+ `1fr 1fr 1fr`) → `1fr !important` ที่ ≤ 768px — แก้ `settings :908, :941, :967, :985, :1010, :1021, :1032, :1059` · `fixed-assets :822` · `portal :351`
- [ ] **F** ตารางใน modal ห่อ `.table-container` (+ `min-width:640px`): `recurring :945–955` (วัดจริง 8 คอลัมน์ใน 311px ที่ 375px) · `fixed-assets :936, :1001` · `documents :13214` · checker (WP-04 ข้อ 13)
- [ ] **G** `Layout.suggestLabel(resp)` (ทำใน WP-03 ข้อ 14 — ตรวจว่าครบ `recurring :830` · `fixed-assets :135, :462–464, :489`)
- [ ] **H** `raw_fetch_api_check` baseline → 0: `email-schedule` (6) · `settings` LINE `:2115, :2185, :2203` (9) · `account-subscription :96, :113, :260, :278, :300` (5) · `contacts` (4) · `recurring` (2) · `fixed-assets` (2)
- [ ] **I** `Layout.panelError(host, e, retryFn)` แทน `catch (_) { return; }`: `documents :13184, :13203` · `settings :1395, :1433, :2456` · `notifications :240` · `fixed-assets :509, :521`
- [ ] **J** enum/สถานะอังกฤษดิบ → DTO `*Label` จากเซิร์ฟเวอร์ 🖥: `portal :353` (`d.status`) · `account-subscription :178` (`p.status` — subscription มี `statusLabel :170` แล้ว) · `email-schedule :239` (`r.documentType`) · `recurring :208` · `subscription.html :257–258, :282–283` (`statusMap/cycleMap/methodMap` สำเนา enum→ไทย ใน JS — ห้าม)
- [ ] **K** "ใครมาก่อนชนะ" (ทำใน WP-03 ข้อ 5, 10)
- [ ] **L** ตารางค้าง "กำลังโหลด..." → แถว error + ปุ่มลองใหม่ใน tbody: `recurring :593` · `fixed-assets :622`
- [ ] **M** glossary 1 หน้าใน `docs/` + checker คำต้องห้าม (fallback · push · Trigger · Default-deny · Sys/Mail · L2 · workspace · invite · Portal · Session): `notifications :26–44, :66, :135` · `email-schedule :34–48, :87, :92, :118, :139, :147, :239` · `account-subscription :60–73, :177` · `portal :149, :223, :270` · `contacts :1250` · `settings :2372, :2417` · ชื่อฟีเจอร์เดียว "ส่งอัตโนมัติ" (`recurring :186, :1185` · `email-schedule :16` · `settings :493`) · `recurring :208` "สร้างเป็นใบอนุมัติแล้วทันที"

**13.2 ต่อหน้า (ที่เหลือหลัง WP-03/WP-07)**

- [ ] recurring: required → `goStep()`+`focus()`+ป้าย "ขาด n ช่อง" บน chip (`:53–57, :234, :1036–1037`) · ว่าง "ยังไม่มีรายการประจำ" + ปุ่มสร้าง + แยก "ไม่พบตามตัวกรอง" (`:572`) · footer 5 ปุ่ม 2 แถวบนมือถือ (`:234`) · `openCreate()` ล้าง `#fExpenseCatHint`/`#fWhtRateHint`/`#fPayModeHint` (`:727–758`) · `fDocType` ค่าที่ไม่อยู่ใน list → `_restoreSavedOption` (`:1233`) · label `for` (`:62, :93, :94, :102, :107, :118…`) · placeholder ช่องผู้ติดต่อ `:97` · chip `aria-current` · `runNow` `confirm()` → `confirmDanger` (`:1317`)
- [ ] fixed-assets: "รหัสสินทรัพย์ *" (`:106`) เอา `*` ออก + placeholder "ว่าง = ออกให้อัตโนมัติ" (เซิร์ฟเวอร์ออก `FA-yyyyMM-####` `FixedAssetService.cs:467`) · บัญชีเครดิตขึ้น `*` เมื่อติ๊ก "บันทึกบัญชีตอนซื้อ" (`:149, :153`) · `save()` toast 3 แบบไม่ focus (`:745–754`) · คำนวณค่าเสื่อม `confirm()` (`:1171`) เฉพาะเดือนปัจจุบัน → modal + ตัวเลือกงวดย้อนหลัง 🖥 · ปลดเอกสาร `prompt()` (`:1014`) → modal · ว่าง "ไม่มีรายการ" (`:601`) บอกเหตุ+CTA · checkbox ไม่มี label (`:943`) · เหตุที่ผูกไม่ได้ซ่อนใน `title` (`documents.html:2736–2738`) → ข้อความตรง · แถวปุ่ม `:203` `flex-wrap`
- [ ] documents: **ลบถาวร 4 ชั้น** (`:12357–12410` confirm×2 + confirmDanger + prompt) → modal เดียว: radio "ลบเฉพาะใบนี้ / พร้อมเอกสารที่เกี่ยวข้อง (n ใบ)" + รายการจากแผน + ช่องเหตุผล (บังคับเมื่อ `needsReason`) + พิมพ์เลขที่ยืนยัน · ยกเลิกลิงก์ชำระ `confirm()` (`:13229`) → `confirmDanger`
- [ ] notifications: ศัพท์ (`:26–44, :135, :147–149, :66, :238–239` · ประโยค `:40` กับ `:43` ซ้ำ) · มือถือ 19 คอลัมน์ checkbox ป้าย 10px (`:10–18, :132–157`) → โหมด < 768px แสดงทีละบทบาท (select) · ผูก LINE ส่วนตัว (`:62–81` "admin webhook จะ log ID ให้") → ปุ่ม "ส่งรหัสผูกไปที่ LINE OA" ใช้กลไก `ContactLinkInvite` เดียวกับผู้ติดต่อ 🖥 (ทำพร้อม WP-11) · `clearLine` `confirm()` (`:253`)
- [ ] settings/bank: `testEmailConfig` `prompt()` (`:2413`) · ผลล้มเหลว "unknown" (`:2417`) · คำเตือน "fallback ไปใช้ SMTP ส่วนกลาง" (`:2372`) → ไทย · bank.html ตรวจขนาดไฟล์ 1 MB ฝั่ง client (`:385`) · `removeQr` `confirm()` (`:2787`)
- [ ] contacts: แท็บแสดงเฉพาะ `editId` (`:1187`) → ข้อความ "บันทึกก่อนแล้วตั้งช่องทางได้" ตอนสร้างใหม่ · `editNotifyLabel` `prompt()` (`:1292`) · `unlinkNotify` `confirm()` (`:1297`)
- [ ] quotation-accept: เติม "ชื่อผู้ยอมรับ" จาก `q.contactName` (DTO `:118` · ฟอร์ม `:88–89`) = คลิกเดียว · 4xx แสดง `j.message` เมื่อมี (`:104–110` · `PublicQuotationController.cs:75` · 429 `:171`) · ตารางยอด `:132–136` เพิ่ม `discountAmount`/`whtAmount` จาก DTO เมื่อ > 0 🖥
- [ ] delivery-sign: `alert()` 3 จุด (`:99, :101, :120`) · `user-scalable=no` (`:5`) เอาออก (WCAG 1.4.4) · `<label>` ช่องชื่อ/หมายเหตุ (`:82–83`) · `toLocaleString('th-TH')` ใส่ `timeZone:'Asia/Bangkok'` (`:61` — เทียบ `quotation-accept :69`)
- [ ] my-signature/signatures: ยุบ "ใช้ลายเซ็นที่วาดนี้" + "บันทึกลายเซ็น" เป็นปุ่มเดียว (`my-signature :50–51, :72`) · โค้ดวาดเอง (`:108–137`) → `signature-pad.js` · ลิงก์/คำอธิบายข้ามกับ `signatures.html` `#uploadModal` (`:15–22, :66–92` — ลายเซ็นส่วนตัว vs รายการหลายอัน)
- [ ] email-schedule: การ์ดกฎสรุปค่าทับ (แนบลิงก์/ช่องทาง/ส่งใบเสร็จ) — เซิร์ฟเวอร์ส่ง `summaryLine` 🖥 (`:236–251`) · เวลา `:383` timeZone · ศัพท์ "Trigger *"/"OffsetDays"/"BCC archive"/"เนื้อหา HTML"/enum trigger (`:34–48, :87, :92, :118, :139, :147`)
- [ ] portal: ภาษา (`:149` "Portal ลูกค้า" · `:223` "Statement" · `:270` "Session หมดอายุ — กรุณา login ใหม่") · ตาราง 8/5 คอลัมน์ ไม่มี overflow (`:41, :312, :400, :448`) · Modal ไม่มี Escape/คลิกนอก/focus (`:67–73, :463`) · toast `role="status"` (`:613–619`) · วันที่ statement `toISOString()` UTC (`:419–420`) → Asia/Bangkok
- [ ] account-subscription/subscription/pay-link/pay-document: glossary เดียว "แพ็กเกจ" (`account-subscription :60–73, :177` vs `subscription :31, :62`) · โหลดแพ็กเกจล้ม ไม่มีปุ่ม (`:239`) · `confirm()` 3 จุด (`:258, :277, :298`) · subscription `location.reload()` 6 จุด (`:336, :342, :491, :574, :640, :649`) → `await this.load()` · pay-link ฟอร์มสลิปเครื่องหมายบังคับ (`:75–88`)

**Definition of done (WP-13):** `raw_fetch_api_check` baseline ของ 6 ไฟล์ = 0 · checker label-for/table-container/glossary baseline 0 · Playwright 375px: `recurring.html` ตารางบรรทัดเพิ่มเติมไม่ถูกบีบ (D วัดจริง) · ไม่มี `alert(`/`confirm(`/`prompt(` ใน `wwwroot/*.html` ที่แก้ (grep = 0 ตอบตัวเลข)

---

## 5. ตรวจแล้วไม่ใช่บั๊ก (รวม 5 ทีม · dedupe · **ห้ามรายงานซ้ำ**)

| # | เรื่อง | ทำไมไม่ใช่บั๊ก | ที่มา |
| --- | --- | --- | --- |
| 1 | ลิงก์ชำระ "ลบ WHT ซ้ำ" (244-7) | `AmountsFor(balanceDue, wht, 0)` ⇒ Net = ยอดค้าง (สุทธิอยู่แล้ว) · Gross = Net + WHT (`DocumentPayLinkPolicy.cs:160-166`) · handler ส่ง WHT = `whtRemaining` เฉพาะเงินเข้า = ยอดค้าง (`:170-174`) · ทดลองรันสอดคล้อง (`RecurringAutopilotService.cs:178, :200`) · GL สมดุลทั้ง accrual/cash | B #1 · C §8.1 |
| 2 | `WhtForPayment` คืน null เมื่อยอดไม่ตรง | ⇒ `WhtPaymentSuggestion.Compute` แนะนำเอง (สัดส่วน · ไม่เกิน NetCap) ไม่ใช่ "ไม่บันทึก WHT" | C §8.2 |
| 3 | `EnsureLinkAsync` ส่ง `companyAttach: true` (`DocumentPayLinkService.cs:203`) | เมธอดนี้ตอบ "ช่องทางไหน" · "แนบไหม" ตัดสินที่ `ForScheduledEmailAsync :270-277` · ปุ่ม "สร้างลิงก์" บนหน้าเอกสารตั้งใจออกได้เสมอ | B #2 |
| 4 | `WithRecurringPayLayer` แก้แถว `EmailScheduleRule` | โหลด `AsNoTracking` (`:208`, `:844`) ⇒ ไม่บันทึกกลับ | B #3 |
| 5 | ใบจำลองทดลองรัน `TotalAmount = Sub+VAT` (`RecurringAutopilotService.cs:198`) | ใช้เฉพาะรวบรวมคำเตือน · ยอดในผลบอก "ประมาณการ" (`:296`) · ไม่ persist | B #4 |
| 6 | `PostPaymentAutomationService.EnqueueAsync` กลืน exception (`:91-96`) | เงิน commit แล้ว การชำระต้องไม่ล้ม · ไม่มีแถวงาน = ตรวจได้ที่หน้าเอกสาร (`StatusAsync.Runs`) — ต่างจาก F-1 ที่ไม่มีที่ให้ดู (WP-02 ย้ายเข้า `CreatePaymentAsync` แล้วยังคงกลืนหลัง commit) | B #5 |
| 7 | `OnBehalf` ของ e-Tax | `SendReceiptAsync :351` ส่ง `plan.OnBehalf && !etaxRoute` ⇒ เส้น e-Tax ไม่มี "ในนาม NextAcc" · ด่านผู้ส่งปฏิเสธแพลตฟอร์มอยู่แล้ว | B #6 |
| 8 | `DispatchAsync` ไม่มีใน `CreateMultiDocPaymentAsync` | เส้นกระจายหลายใบไม่เคยแจ้งเตือน (grep `:15700-16100` = 0) — ไม่ใช่ถดถอยของ 249 · ถ้าต้องการ = ฟีเจอร์ใหม่ | B #7 |
| 9 | QR หน้าชำระในไฟล์ e-Tax | ตัดทั้ง QuestPDF (`PdfGenerationService.cs:122`) และ `ResolvePaymentQrAsync(pdfA:true)` (`PaymentQr.cs:83`) · e-Tax by Email ใช้ `GenerateDocumentPdfAsync` เดียวกัน (`DocumentEmailService.cs:447-449`) | B #8 |
| 10 | นโยบายผู้ติดต่อไม่มีผลถ้ารายการไม่รับรอง `inherit` | ตั้งใจ (HIGH/MED-2 · `AutoApprovePolicy.Effective :140-145`) · Db test `:268` | B #9 · B ขั้น 1 |
| 11 | `ApiKey` ตั้งนโยบาย/ช่องทางไม่ได้ | `[RejectApiKey]` ครบ 3 endpoint (`RecurringController.cs:82, :105, :118`) · `owner_action_wiring_check` R248-K1..K3 | B #10 |
| 12 | ความพร้อม/ทดลองรันเป็น POST ที่ไม่เขียน | `write_permission_gate` allowlist + Db test `:106` นับแถว 5 ตาราง | B #11 · E #7 |
| 13 | LINE กลุ่มบริษัทรับแจ้งเตือน — ไม่ถอยไปกลุ่มกลาง | `DispatchCompanyGroupAsync :402-416` → `TryPushCompanyGroupAsync` (OA บริษัท) · `NotificationAudiencePolicy` ⇒ `Tenant` ⇒ `RouteLine` ปฏิเสธ `PlatformGroup` (`NOTIFY-TENANT-NOT-PLATFORM`) | B #12 · C §4.3 |
| 14 | `AutopilotSettingsResolver` ชั้น Inherit + ช่องทาง (`:83-84`) | ตั้งเฉพาะช่องทางโดยไม่ตัดสินเรื่องแนบ = ตั้งใจ (เทสต์ `AutopilotRound248Tests.cs:180-181`) | B #13 |
| 15 | `Document.AutoSendRuleId` ประทับโดยตารางแรก (`EmailScheduleService.cs:779-781`) · กฎเสมือนไม่ประทับ | ตรงกับ "หลังรับเงินต่อรายการ = ยังไม่ทำ" (ปิดใน WP-02 ข้อ 6) | B #14 |
| 16 | `OverRegistrationIncrease` ปล่อยเคส "ใบที่เกินอยู่ก่อน" (#4) | ตั้งใจ "ห้ามแย่ลง" ไม่ใช่ "ต้องถูกเป๊ะ" · เพิ่ม problem row ใน WP-02 ข้อ 10 | C §8.3 |
| 17 | `PlaceInServiceAsync :993` คำนวณเครื่องหมายจากแถวผูกแทน `SignFor` | แถวของใบเดียวกันเครื่องหมายเดียว ⇒ ผลเท่ากันเสมอ | C §8.4 |
| 18 | `ApplyPostCapitalizationCostAsync` `elapsed` นับจาก `PurchaseDate` (= วันพร้อมใช้) | `MonthIndexFor` ตัวเดียวกับตัวโพสต์ค่าเสื่อม ⇒ งวดที่ลงแล้วไม่ถูกแตะ (`EffectiveDepParams :861-873`) | C §8.5 |
| 19 | `NotificationEventSplit` `NOT EXISTS` ไม่กรอง `IsDeleted` ปลายทาง และไม่ดู `RecipientRole` | ตั้งใจไม่สร้างกลับเมื่อผู้ใช้เคยลบ / ไม่ทับค่าที่ตั้งเอง (doc comment) | C §8.6 · §4.2 |
| 20 | `RevenueWarning` e-Tax ≤ 30 ล้าน เป็นคำเตือนไม่บล็อก | สิทธิจริงจาก ก.อ.01 ที่ RD อนุมัติ (ข้อเสนอ E12) | C §8.7 |
| 21 | e-Tax `Receipt → T03` | ใบเสร็จที่ถือ VAT ถูก · ใบธรรมดา/ย่อถูกกันโดย `EtaxAutoIssueScope` | C §8.8 |
| 22 | `NeverList` มี `PERIOD-CLOSED`/`APPROVAL-LIMIT` ที่ไม่ใช่ warning | เพื่อแสดงในหน้า · เป็น exception ที่นโยบายไม่แตะอยู่แล้ว | C §8.9 |
| 23 | ปัดเศษ `NonClaimableVatIncluded` ตามสัดส่วน ±0.01 (`:590-591`) | ค่าแสดงผล/ตรวจ ไม่ใช่ค่าที่ลง GL (`Amount` ลง GL) | C §8.10 |
| 24 | `AssetGlBackingAsync` นับแถวผูกบนผัง 12280/12xxx เป็น "ทะเบียนรองรับ" ทั้งที่ยังไม่มี JE · `LinkExcessOverGlAsync` รวม JE `Reversed` · `ApplyPostCapitalizationCostAsync` ประทับ `CapitalizedAt` แม้ `legs.Count == 0` · `ReleaseCostSourcesForVoidAsync` ลด `PurchaseCost` ตรงโดยไม่ผ่าน `Adjust` · `PurchaseDate = AvailableForUseDate` (ยืม field แต่มี `AvailableForUseDate` แยก) · มัดจำก้อนเดียวแบ่งสองเครื่อง · `ContractPriceWarning` ไม่บล็อก | ทั้งหมดถูกตามออกแบบ §4 — ดูเหตุผลรายข้อ C §1.11 | C §1.11 |
| 25 | §65 ตรี ผ่านโดยไม่ต้องมีนโยบายสำหรับทางเข้าไม่มีคน (`PassesWithoutPerson :65-72`) | คำตัดสินข้อ 110 ก่อนรอบ 248 · ทิ้งร่องรอย `APPROVE-UNATTENDED-PASSED-S65` | C §5.1 |
| 26 | `BindingHash`: `productCode "007"` → `"7"` ชนกัน · ไม่รวม `description` · ตัดทศนิยม > 8 | ไม่กระทบยอด · จำนวนบรรทัดสะท้อนผ่าน `\|L` | C §5.2 |
| 27 | `BankAccount.cs:33` / `RecurringTransaction.cs:87,92` อ้าง `Accounting.Helpers.*` ใต้ `Accounting.Models.Entities` | `namespace_shadow_check` ยืนยันไม่ถูกบัง · `DocumentDtos.cs:6` alias global-safe | E #1 |
| 28 | `EmailScheduleWorker` เรียก `postPay.ProcessDueAsync` ไม่มี `JobLock` | `SKIP LOCKED` (`PostPaymentAutomationService.cs:110`) กันซ้ำข้ามเครื่อง · worker ไม่ใช่ของใหม่ (F4 ข้อ 6 ไม่ใช้) | E #2 · §7 |
| 29 | QuestPDF ไม่มี QR? | มี `PdfGenerationService.DocumentRenderer.cs:1056-1057` คู่กับ HTML `PaymentQr.cs` · `DocumentLabels payqr_*` ⇒ ไม่ drift · grep literal ไทยใน renderer = 0 | E #3 · C §3 |
| 30 | `NotificationEngine` ctor `IConfiguration? config = null` · `FakeNotify` ครบสมาชิก | DI resolve ได้เสมอ · interface มี `DispatchAsync` เดียว · CI build ✅ | E #4–5 |
| 31 | 4 Db test asset ล้มใน `7cb6bd08` · `write_permission_gate` ฟ้อง DryRun ×3 · CS1501 `:11191` | แก้แล้ว `c314266c` / `ca10f623` / `fe9036f7` — เหลือแค่บทเรียน (WP-05 ข้อ 7) | E #6–7, #11 · A K-03 |
| 32 | `PaymentQrPolicy.SourceProblem` / `AutopilotSettingsResolver.ValidateMode` throw ก่อน `KeyNotFound` | ปฏิเสธค่าที่ไม่รู้จักก่อนโหลดแถว = เจตนา (F2 ข้อ 3) | E #8 |
| 33 | `AssetMultiDocumentDbTests` TaxId เดียวกันทุกบริษัท | ไม่มี unique index บน `Companies.TaxId` · ความเปราะอนาคต ไม่ใช่บั๊ก | E #9 |
| 34 | `hashtext` ไม่ deterministic? | deterministic ต่อ cluster ⇒ ล็อกทำงานถูก · ปัญหาคือสำเนาสูตร (WP-04 ข้อ 1) | E #10 |
| 35 | กันกดซ้ำปุ่มเขียน · Escape ปิด modal | มีกลางแล้ว (`api.js:58–130 ApiBusy` · `layout.js:2756–2777`) — ยกเว้นหน้าที่ใช้ `fetch` ดิบ (รูปแบบ H) | D §12 ท้าย |
| 36 | `IsCashInflowDocAsync` คืน "ออก" ให้ CreditNote ฝั่งซื้อ | วันนี้ไปไม่ถึง (`PayableDocumentTypes :14588-14592` ไม่มี CN · คืนเงิน gateway เดินคนละเส้น) — ปิดกับดักอนาคตใน WP-02 ข้อ 5 | C §4.1 |
| 37 | `ReleaseCostSourcesForVoidAsync` / `ApplyPostCapitalizationCostAsync` ลำดับล็อก | ทุกเส้นเขียน: advisory key → Documents FOR UPDATE (เรียง Id) → FixedAssets — ยกเว้นช่อง TOCTOU ที่ WP-02 ข้อ 8 ปิด | C §1.7 |
| 38 | `ContactNotifyEndpoints.*` ไม่มีผู้อ่านเชิงธุรกิจ | ✅ ปิดแล้ว WP-08 (commit <เติมในคอมมิตตามหลัง>) — ผู้อ่าน = ตัวหาผู้รับของทุกเส้นส่ง · `settings_reader_check` ล็อก `TopicsJson` · คำเตือนบน UI ถูกลบ | B ขั้น 6 |
| 39 | `tools/dead_helper_check.py` รันในเครื่องไม่จบใน 120 วินาที | CI static job ของ fe9036f7 ผ่าน (12m54s) ⇒ dead helper/test_inventory/checker 57 ตัวเขียว | A K-03 |
| 40 | ข้อความคอมมิต `369bc403` ผิดหัวเรื่อง · `ecc6710f`/`369bc403` ซ้ำ | ไม่ amend — git notes (WP-05 ข้อ 6) | A K-01 · E M-2 |

## 6. ภาคผนวก

### 6.1 รายงานต้นทาง (อ่านเฉพาะเมื่อต้องการบริบทเพิ่ม — ทุก file:line ที่จำเป็นอยู่ในไฟล์นี้แล้ว)

| ทีม | ไฟล์ | ใช้เมื่อ |
| --- | --- | --- |
| A | `erp-review/2026-11-06/audit-249/A_BACKLOG_DECISIONS.md` | ต้องการแหล่ง/หลักฐานของแถวงานค้าง · ลำดับ 5 รอบที่ทีมแนะนำ · ID A-xx…K-xx |
| B | `erp-review/2026-11-06/audit-249/B_E2E_BILLING_PROCESS.md` | เส้นทาง+ผู้เรียกจริงของทุกขั้น billing (ขั้น 1–8) · ตารางผู้อ่านค่าตั้ง (ขั้น 6) |
| C | `erp-review/2026-11-06/audit-249/C_TAX_MONEY_CORRECTNESS.md` | ตัวอย่างตัวเลข (สินทรัพย์ §1.4 · WHT §2.1) · ตารางเครื่องหมาย §1.5 · e-Tax §3 |
| D | `erp-review/2026-11-06/audit-249/D_UX_UI.md` | บริบท UX ต่อหน้า (คำตัดสินต่อหน้า §0) |
| E | `erp-review/2026-11-06/audit-249/E_QUALITY_TESTS_DOCS.md` | matrix ด่าน↔เทสต์↔กติกา §2 · กับดัก Db test §3 · compile-risk §4 · checker §5 · multi-instance §7 |
| ออกแบบ/วิจัย | `ASSET_MULTI_DOCUMENT_DESIGN` · `AUTOPILOT_BILLING_DESIGN` · `AUTO_SEND_WHT_DESIGN` · `ETAX_BY_EMAIL_AUDIT` · `EWHT_EVIDENCE_RESEARCH` · `LINE_MULTICHANNEL_DESIGN` · `LINE_OA_TERMS_CHECK` · `NOTIFY_PRICING_ANALYSIS` · `RETENTION_LAW_CHECK` (ทั้งหมดใน `erp-review/2026-11-06/`) | WP-06 (ภาคผนวก RETENTION 152 แถว) · WP-09–12 (แผนรายเฟส) |
| คำตัดสินเดิม | `erp-review/2026-09-29/DECISIONS.md` (ถึง 246-QR-10) · `DECISION_DOCTRINE.md` · `docs/REVIEWS_INDEX.md` | ก่อนเขียนตัวตัดสิน/จุดเรียก AI ใหม่ |

### 6.2 วิธีตรวจรับทุก WP

1. **ก่อน commit:** `bash tools/check_all.sh` ผ่าน (ล้มตัวไหน = แก้ ไม่ใช่เลี่ยง) · `node --check` ทุก `<script>` ใน .html ที่แก้ · `python3 tools/callers.py <Symbol>` ก่อนแตะสัญลักษณ์ · `python3 tools/test_inventory.py --check` · `python3 tools/doc_commit_sha_check.py` (หลัง WP-01)
2. **ข้อความคอมมิต** ตอบ F3 ข้อ 7–12 เป็นตัวเลข/ชื่อ: จุดที่เหลือของรูปแบบเดิม (0 ก็เขียน 0) · ทางเข้าอื่นเดินด่านเดียวกันไหม · ผู้ใช้ที่ถูกกันทำอะไรได้แทน + ชื่อเทสต์ทิศตรงข้าม · ค่าที่ persist ก่อนแก้ยังผิดไหม → migration · ฝ่ายค้านแล้ว · DOCUMENT_FLOW/ACCOUNT_STRUCTURE/TEST_PLAN ขยับไหม · ลงท้ายด้วย `Co-Authored-By` + `Claude-Session` ตาม system-reminder
3. **หลัง push:** `bash tools/ci_wait.sh` (`run_in_background`) → แดง = `get_job_logs` แล้วแก้ก่อนรายงาน · **4 job ต้องเขียว** (static · build · test · db-test — หลัง WP-01) · doc-only commit ไม่มีรอบ CI ต้องบอกผู้ใช้
4. **ฝ่ายค้าน 1 รอบต่อ WP ที่แตะเงิน/ภาษี/สิทธิ์/PDPA** (WP-02 · 03 · 06 · 08 · 09 · 10 · 11): ส่ง diff ให้ subagent ตอบ 3 คำถาม — ทางเข้าอื่น? ทิศตรงข้าม? สถานะปลายทางประทับเองไหม? — ผลต้อง verify ก่อนเชื่อ และจดเมื่อผิด
5. **sha ของคอมมิตที่เปลี่ยน flow** เติมใน DOCUMENT_FLOW/CHANGELOG ใน**คอมมิตตามหลัง** (ห้าม amend) · ติ๊ก `✅ <sha>` ในรายงาน A–E และไฟล์นี้ (เพิ่มคอลัมน์สถานะท้ายตาราง WP เมื่อปิด)
6. **รายงานผู้ใช้** ทุกครั้งระบุ "ยังไม่ได้คอมไพล์ในเครื่องนี้ — ผล CI run <id> …" และตัวเลขเทสต์ที่รัน

### 6.3 ลำดับรอบที่แนะนำ (map WP → รอบ)

| รอบ | WP | หมายเหตุ |
| --- | --- | --- |
| 250 | WP-01 → WP-05 → WP-06 → WP-02 | เปิดเทสต์ก่อน · doc/sha · มาตรา · เงิน P1 — ส่งคำถาม §3 ทั้ง 19 ข้อเป็นชุดเดียวท้ายรอบ |
| 251 | WP-03 → WP-04 → WP-07 → WP-08 | ความปลอดภัย/UX ร้ายแรง · เทสต์ที่ขาด · ค้าง 248 · L1 ต่อสาย |
| 252 | WP-09 | 50 ทวิ เห็นก่อน–รับเข้าได้ (AUTO_SEND 243+244a) |
| 253 | WP-10 | ลิงก์อัปโหลด · ทวง · ไม่หัก · e-WHT (ถ้า N4 ตอบ) |
| 254 | WP-11 (L2 + L4 ขั้นแรก) | OA บริษัทก่อน · L3 OA กลางรอ N8 |
| 255+ | WP-12 (AUTOPILOT 250–255) · WP-13 ทยอยทุกรอบ | |

_สร้าง 2026-11-06 จากรายงาน A–E (ตรวจกับ HEAD `fe9036f7`/`43cfdba1` · merge `5a13c6e9`) · ไม่ได้คอมไพล์ในเครื่องนี้ · ข้อขัดแย้งระหว่างรายงานที่ตัดสินแล้ว: (1) `0a829110` ยึด E (sha ตาย) · (2) 246-QR-10 ดึงขึ้น WP-02 ไม่ใช่ WP-08 · (3) placeholder sha ยึดตาราง E §6.2 (blame) เหนือ A-03 (map รอบ) เมื่อต่างกัน · (4) A จัด C-06 เป็น P1 ขณะ C จัด F1 เป็น Medium — ยึด A (ทำใน WP-02) เพราะผลกระทบเงียบเมื่อย้ายผังค่าใช้จ่าย↔ค่าใช้จ่าย_
