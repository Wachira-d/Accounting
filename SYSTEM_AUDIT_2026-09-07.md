# SYSTEM_AUDIT_2026-09-07.md — ผลตรวจรอบ "ทีมตรวจทุกด้าน" (A–G)

โจทย์จากเจ้าของโปรเจกต์: *"ดำเนินการต่อทั้งหมดให้เสร็จสิ้น พร้อมตั้งทีมตรวจสอบทุกด้าน"*
ตั้งทีม 7 ด้าน แต่ละทีมเปิดไฟล์อ่านจริงและต้องแนบ file:line + วิธี reproduce

| ทีม | ขอบเขต |
| --- | --- |
| A | ความปลอดภัย/ตัวตน (SSO · JWT · tenant guard · integration key · rate limit) |
| B | e-Tax / ETDA (XML · ลายเซ็น · การนำส่ง · retention) |
| C | เอกสาร/JE/ภาษี (deposit · CN-DN · undue VAT · งวดปิด · §86/4) |
| D | หัก ณ ที่จ่าย (ตารางอัตรา · 50 ทวิ · ภ.ง.ด.1/2/3/53/54) |
| E | LINE bot + สินทรัพย์ถาวร (ค่าเสื่อม · จำหน่าย · ทบทวนอายุ) |
| F | frontend drift / silent no-op (DOM id · deep link · สำเนามือฝั่ง JS) |
| G | multi-instance / data integrity (JobLock · storage · N+1 · schema drift) |

## กติกาการใช้ไฟล์นี้

ใช้ชุดเดียวกับ `SYSTEM_REVIEW_2026-09.md` / `ERP_REVIEW_2026-09-05.md` ทุกข้อ:
- **verify ก่อนเชื่อ** — ทุกข้อต้องเปิดไฟล์ยืนยันเองก่อนลงมือ · เจอว่ารายงานผิด
  ให้บันทึกว่าผิด ไม่ใช่ข้ามเงียบ
- แก้เสร็จ **ติ๊ก `✅ <sha>` หน้า ID** ไม่ลบแถว
- §9 = ตรวจแล้วไม่ใช่บั๊ก ห้ามรายงานซ้ำ

---

## §1 — ปิดแล้วในรอบนี้

| ID | ทีม | เรื่อง | สถานะ |
| --- | --- | --- | --- |
| B-01 | B | "โหมดออฟไลน์" ประทับว่าส่งสรรพากรแล้วทั้งที่ไม่เคยส่ง + ล็อกเอกสารถาวร | ✅ (รอบนี้) |
| B-02 | B | `EtaxController` มีแค่ `[Authorize]` ระดับคลาส — 10 write endpoint ไม่มีด่านสิทธิ์ | ✅ (รอบนี้) |
| B-03 | B | เลข `ETAX-…` ออกด้วย `CountAsync()+1` — ไม่มีล็อก · นับจากจำนวนแถว · ไม่เห็น change tracker | ✅ (รอบนี้) |
| B-04 | B | `EtaxController.cs` ไม่เคยอยู่ใน `WATCHED` ของ `write_permission_gate_check` | ✅ (รอบนี้) |

## §2 — backlog เรียงลำดับ (ยังไม่ปิด)

### P0

| ID | ทีม | เรื่อง | file:line |
| --- | --- | --- | --- |
| ✅ A-01 | A | รหัสผูก LINE 6 หลักค้นแบบ global + ไม่มีตัวนับความพยายาม ⇒ brute-force ยึดบัญชี | `LineBotService.cs:95-116` |
| ✅ A-02 | A | `SiteCustomer` JWT ใช้ key/issuer/audience ชุดเดียวกับผู้ใช้ ERP | `CmsCustomerService.cs:166` · `JwtHelper.cs:10` |
| ✅ D-01 | D | ตารางอัตรา WHT ชุดที่ 3 ใน `TaxService` (40(1)=3% คงที่ · คีย์ `"5"`/`"6"` ชนกับ `ThaiWhtRateTable`) | `TaxService.cs:19-33` |
| ✅ E-01 | E | `CalculateDepreciationAsync` ไม่มี switch-to-straight-line ⇒ DecliningBalance ไม่มีวันจบ + ตัวเลขต่างจากตารางที่ผู้ใช้เห็น 35% | `FixedAssetService.cs:654-668` |
| ✅ F-01 | F | ปิดบิลออฟไลน์ใน POS อ่าน DOM id ที่ไม่มีในหน้า ⇒ TypeError เงียบ โหมดออฟไลน์ตายทั้งก้อน | `pos.html:2252-2284` |
| ✅ F-02 | F | ตารางแมป 50 ทวิ ฝั่ง JS ตกรหัส `8ad`/`8tr` ที่ระบบเองสร้าง ⇒ ช่องประเภทว่างแต่ยอดรวมเต็ม | `wht.html:403-414` |
| ✅ G-01 | G | background job 4 ตัวไม่มี JobLock (2 ตัวส่งอีเมลถึงลูกค้าจริง) | `ScheduledReportDispatcher.cs:55` · `AbandonedCartService.cs:63` · `OcrMlBackgroundService.cs:85` · `BackgroundJobService.cs:94` |
| G-02 | G | ไฟล์อัปโหลดเขียน local disk ตรง 13 จุด ไม่มี storage abstraction | ทั้งเรพ (ดูรายงาน G) |

### P1

| ID | ทีม | เรื่อง | file:line |
| --- | --- | --- | --- |
| ✅ A-03 | A | `TenantGuardFilter` ถูกข้ามด้วย header `X-Integration-Key` ค่าอะไรก็ได้ | `TenantGuardFilter.cs:47-54` |
| A-04 | A | webhook fan-out ไปทุก config ของผู้เช่า | — |
| A-05 | A | `/api/integration/*` ใช้ BCrypt ทุก request ไม่มีแคช/rate limit | — |
| B-05 | B | ลายเซ็นเป็น XMLDSig ไม่ใช่ XAdES-BES; `DigitalSignature` เก็บ hash ไม่ใช่ `SignatureValue` | `EtaxInvoiceService` |
| B-06 | B | ไม่มี cron นำส่งภายในวันที่ 15 · `retry-failed` ไม่มี UI เรียก | — |
| B-07 | B | `ThaiAdminCodes` แต่งรหัสอำเภอ/ตำบลแล้ว default เป็น กทม. | `ThaiAdminCodes.cs` |
| B-08 | B | ส่งยอดสกุลต่างประเทศให้ RD โดยไม่แปลง | — |
| B-09 | B | สร้าง e-Tax อัตโนมัติล้มแล้วกลืนด้วย `LogWarning` | `DocumentService.cs:5470-5478` |
| C-01 | C | `RefundDepositAsync` JE ไม่บาลานซ์เมื่อไม่มีผัง 21913 | `DocumentService.cs:3704-3745` |
| C-02 | C | CN/DN ฝั่งซื้อไม่ดู `IsVatClaimable` | `DocumentService.cs:13566-13606` |
| C-03 | C | `TryReclassifyUndueOutputVatAsync` ใช้ `ResolveFiscalPeriodAsync` (ไม่เช็คงวดปิด) | `DocumentService.cs:12472` |
| C-04 | C | `CnDnPurchaseSideOverride` อ่านใน JE แต่ไม่อ่านใน `ApplyStockMovementsAsync` | — |
| C-05 | C | ใบเสร็จตัดชำระ/ใบลดหนี้คืนเงิน ออกนอก `ApproveDocumentAsync` ⇒ ข้ามด่าน §86/4 · `IssuerBranchCode` · `RetentionUntil` · `TaxPointDate` | — |
| ✅ D-02 | D | dropdown อัตรา/ประเภทพิมพ์มือใน `recurring.html` + `admin/ocr-config.html` (ผิดกฎหมาย 4 จุด) | `recurring.html:91-110` |
| D-03 | D | `8ad`/`8tr` หายจาก renderer 50 ทวิ ทั้ง 3 ตัว | — |
| D-04 | D | Col7 ประเภทเงินได้ต่างกันระหว่างสองเส้น export ภ.ง.ด. | — |
| D-05 | D | ภ.ง.ด.1 e-Filing คืนไฟล์เปล่าแล้วประทับว่า export แล้ว | — |
| D-06 | D | ภ.ง.ด.2 ไม่มีช่องผู้รับเงินและไม่มี enum `WithholdingTax2` | — |
| D-07 | D | `DefaultRate = t.JuristicRate ?? t.IndividualRate` ไม่ดูชนิดผู้รับ | `ThaiWhtRateTable` call site |
| ✅ E-02 | E | เส้นรูปใน LINE สร้างเอกสารโดยไม่ผ่าน `CanCreateAsync` (ด่านอยู่ที่ controller เว็บเท่านั้น) | `LineBotService.cs:480` |
| ✅ E-03 | E | การ์ด "อนุมัติเลย" ใน LINE ไม่อ่าน `ComplianceIssues` ทั้งที่ค่ามาถึงแล้ว | `LineBotService.cs:543-576` |
| ✅ E-04 | E | `FixedAssetController` ไม่มีคีย์สิทธิ์เลย + endpoint มือแข่งกับ cron ได้ | `FixedAssetController.cs:116` |
| E-05 | E | `DisposeAsync`/`WriteOffAsync` ไม่คิดค่าเสื่อมถึงวันขาย + silent no-op เมื่อไม่มีผังบัญชี | `FixedAssetService.cs:381-486` |
| E-06 | E | `AdjustUsefulLifeAsync` ทบทวนอายุแบบย้อนหลัง (ผิด TFRS บทที่ 10) + ไม่มี `UsefulLifeReviewedAt` | `FixedAssetService.cs:586-608` |
| F-03 | F | deep-link 9 จุด/6 หน้า ใช้ชื่อ query param ที่ปลายทางไม่อ่าน | ดูรายงาน F |
| F-04 | F | `_revenueDocTypes`/`_expenseDocTypes` ยัด CN/DN/DeliveryNote เป็น revenue ทั้งที่เป็น `BothSides` | `layout.js:2528-2533` |
| ✅ F-05 | F | ปุ่มลบบริษัทในคอนโซลแอดมินพิมพ์ `AdminApi` (ของจริง `AdminAPI`) | `admin/customers.html:466` |
| ✅ G-03 | G | `AssociationRuleMiner` ลบทั้งตารางแล้วเขียนใหม่ ไม่มีล็อก ไม่มี transaction | `AssociationRuleMiner.cs:181-199` |
| ✅ G-04 | G | `PaymentIntentReconcileJob` commit ปล่อยล็อกก่อนงานจริงเริ่ม | `PaymentIntentReconcileJob.cs:71-95` |
| G-05 | G | N+1 ยืนยันแล้ว 3 จุด | `OcrController.cs:823` · `CrossTenantKnowledgeAggregator.cs:150` · `EmailScheduleService.cs:286` |

### P2

| ID | ทีม | เรื่อง |
| --- | --- | --- |
| A-06 | A | `CreateOrderAsync` ไม่เรียก `EnsureCartScopeAsync` |
| A-07 | A | login/register ของ portal ไม่อยู่ใน tier `auth:` |
| A-08 | A | chat rate limit คีย์ด้วย `X-Forwarded-For` ที่ปลอมได้ |
| B-10 | B | ไม่มี `EtdaTimestampToken` · `SendEtaxByEmailAsync` ไม่บังคับ `Status ≥ Signed` |
| C-06 | C | ป้ายไทยผิดใน `RequireOpenFiscalPeriodAsync(..., "รายการตัดหนี้สูญ")` `:11539` |
| C-07 | C | raw SQL `FOR UPDATE` ขาด `CompanyId` 3 จุด (`:8596` · `:3682` · `:14142`) |
| C-08 | C | ขาดปีกกา `:13952-13954` / `:13975-13977` |
| C-09 | C | `DOCUMENT_FLOW.md` เลขบรรทัดใน Quick-reference ล้าสมัยทั้งตาราง |
| D-08 | D | `H|`/`T|` + วันที่ พ.ศ. แบบขีดทับ ยังค้างใน ภ.ง.ด.1/1ก/2 |
| D-09 | D | `IssuedDate = DateTime.UtcNow` แทนวันจ่าย และไม่แปลงเป็นเวลากรุงเทพ |
| D-10 | D | เลขหนังสือรับรอง WHT ไม่ผ่าน `SequenceNumber` |
| ✅ E-07 | E | `Users.LineUserId` เป็น index ไม่ unique + `FirstOrDefaultAsync` ไม่มี `OrderBy` |
| ✅ F-06 | F | แปลง พ.ศ.→ค.ศ. ใน `admin/ocr-config.html` ให้ผลห่างจริง 500 ปี (ปีย่อ 2 หลัก) |
| G-06 | G | rate limit เป็น per-process ⇒ เพดานจริง = เพดาน × จำนวนเครื่อง |
| G-07 | G | `BulkCleanupController` สแกนทั้งบริษัทโดยไม่จำกัดช่วงเวลา |

---

## §9 — ตรวจแล้วไม่ใช่บั๊ก (ห้ามรายงานซ้ำ)

- **LINE webhook verify signature** — HMAC-SHA256 บน raw body + `FixedTimeEquals`,
  secret ว่าง = `return false` (fail closed) · `LineBotService.cs:63-73`
- **`UserLoginPolicy` บน LINE** — ครบทั้ง 3 ทางเข้า (`:128` · `:332` · `:647`)
- **`DocumentPermissionHelper.CanApproveAsync` บน postback** — มีแล้ว `:664`
- **ค่าเสื่อมสะสมเกินราคาทุน** — cap ที่ salvage แล้วทั้งสองเส้น
- **ที่ดินต้องเป็น `DepreciationMethod.None`** — เป็นด่านจริงที่ `:44-58`
- **`AdvisoryLockKey` call site ทั้ง 23 จุด** — ใช้ `For(...)` ครบ ไม่เหลือ `GetHashCode`
- **`EmailScheduleService.ProcessPendingQueueAsync`** — CTE + `FOR UPDATE SKIP LOCKED` + `RETURNING` ปลอดภัยข้าม instance
- **`RecurringTransactionService.ProcessDueRecurringTransactionsAsync`** — ล็อกรายแถวและเช็คผลจริง
- **DataProtection key ring** — ย้ายไป `DbXmlRepository` แล้ว
- **schema drift** — สแกน 326 entity เทียบ `DatabaseMigrationHelper` แล้วไม่พบ (มี negative test)
- **checksum เลขผู้เสียภาษีฝั่ง JS** — `layout.js:2794-2802` ตรงกับ `ThaiTaxId.IsValid`
- **`_buildReviewCorrection`** — ส่งครบ 24/24 ช่องของ `OcrCorrectionRequest`
- **`typeof this.xxx === 'function'` 10 จุด** — เมธอดมีจริงทุกตัว ไม่ใช่ dead branch
- **`AccountantWorkspaceController.cs:25` · `AiSuggestionController.cs:1496/2190`** —
  scanner ฟ้องว่าโหลดตารางไม่จำกัด แต่ verify แล้วเป็น projection/GroupBy ฝั่ง SQL ที่ scope แล้ว
- **`PosService.Orders.cs:1339`** — scanner นับเป็น N+1 ผิด (query อยู่นอก `foreach` ที่ไม่มีปีกกา)
- **เส้น OCR cached เสียโควตา** — รายงานรอบก่อนผิด: `OcrController` คืนโควตาเมื่อ `IsDuplicate` อยู่แล้ว

---

Last verified against codebase: 2026-09-07
