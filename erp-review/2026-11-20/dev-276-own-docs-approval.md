# รอบ 276 — ผู้ใช้ "เห็นเฉพาะเอกสารที่ตัวเองสร้าง" + "สร้าง/แก้ได้เฉพาะร่าง" + ขออนุมัติ + แจ้งผู้อนุมัติ

ความต้องการของเจ้าของ (ข้อความเดิม): "ตั้งค่าการจำกัดสิทธิ์ในระบบให้ เห็นเฉพาะเอกสารที่ตัวเองสร้างเท่านั้นด้วยได้มั้ย และจำกัดแค่ให้เป็นสถานะร่างเท่านั้น
และกดแจ้งเพื่อให้บัญชีหรือคนดูแลหลักเข้ามากดอนุมัติได้ การแจ้งถ้าส่งไลน์เข้ากรุ๊ปหรือส่วนตัวได้ก็ทำด้วย หรืออีเมล์ หรือ แจ้งผ่านในระบบเอง"

## สถานะปัจจุบัน (สั้น — อ่านตรงนี้ก่อน)

| ส่วน | สถานะ |
| --- | --- |
| ค่าตั้ง 2 ตัวต่อผู้ใช้ + ต่อบทบาท (`CompanyUser`/`CompanyRole` · `OwnDocumentsOnly` · `DraftDocumentsOnly`) | ✅ ดู §6 sha |
| ตัวตัดสินเดียว `Helpers/DocumentRestrictionPolicy` (ค่าที่มีผล · ทางเข้าที่อนุญาต · ข้อความ) | ✅ |
| ด่านทุกคำขอ `Filters/DocumentRestrictionFilter` (global · หลัง TenantGuard) + ตัวกรองแถว EF ของ `Documents` | ✅ |
| ด่านชั้น service: `ApproveDocumentAsync` · `VoidDocumentAsync` (ทุกทางเข้า รวม LINE/มือถือ/งานอัตโนมัติในคำขอของผู้ถูกจำกัด) | ✅ |
| ขออนุมัติ / ถอนคำขอ / ตีกลับพร้อมเหตุ / อนุมัติ ⇒ แจ้งกลับผู้สร้าง (`DocumentApprovalRequests` + `NotificationEngine`) | ✅ |
| UI: หน้าทีม (ตั้งค่า) · หน้าบทบาท · หน้าเอกสาร (ป้าย + ปุ่ม) · คิวรออนุมัติ · เมนู | ✅ |
| เลื่อน: OCR สำหรับผู้ถูกจำกัด · ความเห็น (comments) POST · แชต AI · ค้นหาทั่วระบบ | ⏸ §5 |

---

## 1. ผลวิเคราะห์ระบบเดิม (ก่อนเขียนโค้ด — ห้ามสร้างของซ้ำ)

### 1.1 สิทธิ์/บทบาท
- `CompanyUser` (ผู้ใช้ ↔ บริษัท) มี `Role` (`UserRole` Owner/Accountant/Staff/Auditor/Viewer/ExternalAccountant/PlatformSupport/SystemAdmin) + `CompanyRoleId` (บทบาทกำหนดเอง) + `AllowedBranchIds`.
- ด่านคีย์สิทธิ์ตัวเดียว `PermissionService.HasPermissionAsync` (Owner/SystemAdmin/Accountant ผ่านชุดมาตรฐาน `Helpers/BuiltInRolePermissions`) ·
  เอกสารใช้ `Helpers/DocumentPermissionHelper` (Create/Approve/Void แยกฝั่งรายรับ/รายจ่าย) · `DocumentController.DenyDocAsync` เป็นด่านเดียวของ controller.
- **`PermissionKeys.DocumentViewAll` ("ดูเอกสารทุกคน · bypass row-level filter") มีอยู่แต่ไม่มีผู้อ่านเลย** (grep = 0 ผู้อ่าน) — ไม่มี row-level filter ใดในระบบ.
  ⇒ ค่าตั้งใหม่ต้องเป็นตัวตัดสินจริง ไม่ใช่คีย์ที่สามที่ไม่มีใครอ่าน (คำตัดสิน 276-1).
- ด่านข้ามบริษัท `Middleware/TenantGuardFilter` (global action filter · route `companyId`) — จุดต่อสายที่เหมาะสำหรับด่านใหม่ (ทำงานหลังมัน).
- เมนู: `GET api/company/{id}/roles/my-permissions` → `layout.js hasMenuAccess` (`PermissionDeniedMenuIds` ซ่อนเมนู).

### 1.2 workflow อนุมัติเดิม — `ApprovalService`
- มีอยู่แล้วครบ: `ApprovalRule` (ขั้น + ผู้อนุมัติระบุตัว + ตัวกรองยอด/ชนิด/โครงการ) → `SubmitForApprovalAsync` ⇒ ใบ `Draft → WaitingApproval` + แจ้ง `approval.required`
  ถึงผู้อนุมัติขั้นแรก (ผ่าน `INotificationEngine`) · `SubmitActionAsync` (SoD ผู้ขอห้ามอนุมัติเอง · ผู้กดต้องเป็นผู้อนุมัติของขั้น · ขั้นสุดท้ายต้องมีสิทธิ์อนุมัติเอกสาร ·
  ตีกลับ ⇒ ใบกลับ Draft + แจ้งผู้ขอ · อนุมัติครบ ⇒ `ApproveDocumentAsync` + แจ้งผู้ขอ).
- **ช่องว่าง**: ไม่มีกฎที่ตรง ⇒ โยน "ไม่พบกฎการอนุมัติที่เหมาะสม" — หน้าเว็บบอก "กด อนุมัติ ได้เลย" ⇒ **ผู้ที่อนุมัติเองไม่ได้ไม่มีทางขออนุมัติเลย**
  (บริษัทส่วนใหญ่ไม่ตั้งกฎ). รอบนี้เติมเส้น "ขออนุมัติพื้นฐาน" เฉพาะกรณีนี้ — กฎที่ตรงยังเดินเส้นเดิมทุกประการ (คำตัดสิน 276-4).
- ใบ `WaitingApproval` แก้ไม่ได้อยู่แล้ว (`UpdateDocumentAsync` รับเฉพาะ Draft/Rejected) · `Rejected` แก้ได้เหมือนร่าง · `ApproveDocumentAsync` รับ Draft/WaitingApproval.

### 1.3 ช่องทางแจ้งเตือนเดิม — ใช้ทั้งหมด ไม่ยิง API ตรง
- `NotificationEngine.DispatchAsync(companyId, eventKey, ctx)` = ตัวส่งกลางตัวเดียว: ตาราง `NotificationSettings` ต่อบริษัท×คีย์×บทบาทผู้รับ
  (ช่อง กระดิ่ง `EnableSystem` · อีเมล `EnableEmail` (SMTP ของบริษัท) · LINE ส่วนตัว `EnableLine` (`User.LineUserId` ที่ผูกแล้ว) · กลุ่ม LINE ของบริษัท `EnableLineGroup`
  (`LineNotifyService.TryPushCompanyGroupAsync` — OA ของบริษัทเท่านั้น · รอบ 262/272 OA กลางไม่ประกาศเรื่องธุรกิจ)) + การปิดรายคน `NotificationPreferences` ·
  โควตาข้อความอยู่ในตัวส่งของช่อง · ค่าตั้งต้นเมื่อบริษัทยังไม่ตั้ง = `NotificationAudiencePolicy.DefaultMatrix`.
- หน้าตั้งค่า (`NotificationConfigService` / หน้าแจ้งเตือน) แสดงคีย์จาก `NotificationEvents.Catalog` + บทบาทจาก `NotificationRecipientRoles.All` ⇒
  คีย์ใหม่ขึ้นบนหน้าตั้งค่าอัตโนมัติ — เจ้าของเลือกช่องเองได้ (ข้อ "ส่งไลน์เข้ากรุ๊ปหรือส่วนตัว หรืออีเมล หรือในระบบ").

### 1.4 ข้อมูลผู้สร้างบนเอกสาร
- `Document.CreatedBy` (string จาก `BaseEntity`) — ทางเว็บ/แปลง/คัดลอก = `userId.ToString()` · ทาง LINE บางเส้น = อีเมลผู้ใช้ · ทางระบบ = ป้าย (`system:*`, `api:v1`, `storefront-*`).
  ⇒ "ของตัวเอง" = `CreatedBy == userId` **หรือ** `CreatedBy == email ของผู้ใช้` (ตัวกรองเดียว · คำตัดสิน 276-3).
- ไม่มีคอลัมน์ "ผู้ขออนุมัติ" — เพิ่มตารางแยก `DocumentApprovalRequests` (ประวัติครบ · ไม่แตะ `DocumentResponse` ที่ทีมอื่นแก้อยู่).

### 1.5 ทางเข้าที่อ่าน/เขียนเอกสาร (ต้องเดินด่านเดียวกัน)
| ทางเข้า | ที่อยู่ | ผลรอบนี้ |
| --- | --- | --- |
| รายการ/ค้นหา/ส่งออกรายการ/periods/chain/revisions/duplicate-check | `DocumentController` (110 endpoint) | ตัวกรองแถว EF ของ `Documents` (ทุก query ผ่าน `_db.Documents`) + รายการอนุญาต |
| อ่านใบเดียว/PDF/แปลง/แปลงบางส่วน/คัดลอก/ไฟล์แนบ/ผูกใบต้นทาง | `DocumentController` · `DocumentCloneController` · `FileAttachmentController` | ด่านกลางตรวจ id ใน route (`documentId`/`invoiceId`/`sourceId`/`entityId`) ⇒ **404** ถ้าไม่ใช่ของตัวเอง |
| เอกสารลูกที่อ้างใบของคนอื่น (`RelatedDocumentId` ฯลฯ ในฟอร์ม) | `CreateDocumentAsync`/`UpdateDocumentAsync` | ตัวกรองแถว EF ⇒ ใบต้นทางของคนอื่น "ไม่พบเอกสาร" |
| รายงาน/แดชบอร์ด/GL/สมุดรายวัน/ธนาคาร/ภาษี/สินทรัพย์/เงินรอจัดสรร/AR-AP | ~200 controller | **ปลอดภัยไว้ก่อน: ไม่เห็น** (ผู้ใช้ "เห็นเฉพาะของตัวเอง" เข้าได้เฉพาะรายการอนุญาต) |
| ค้นหาทั่วระบบ `search/quick` · แชต AI · สำนักงานบัญชี `accountant/*` | `SearchController` · `TenantAssistantController` · `AccountantController` | ไม่อยู่ในรายการอนุญาต ⇒ 403 พร้อมเหตุ |
| มือถือ `api/mobile/companies/{id}/*` | `MobileController` | มี `companyId` ⇒ ด่านเดียวกัน (dashboard/approve ไม่อยู่ในรายการ) |
| LINE (บอทอนุมัติ/สร้างแล้วอนุมัติ) | `LineBotService` → `ApproveDocumentAsync(…, user.Email)` | ด่านชั้น service ตาม "ผู้กระทำ" (Guid หรืออีเมลสมาชิก) |
| อนุมัติอัตโนมัติในคำขอของผู้ถูกจำกัด (ใบสำคัญจ่ายเงินสดตอนสร้าง ฯลฯ) | `CreateDocumentAsync` → `ApproveDocumentAsync(… "system")` | ด่านชั้น service ตาม "คำขอนี้" (ค่าบน DbContext ที่ตัวกรองตั้ง) |
| งานเบื้องหลัง (รายการประจำ · งานกลางคืน · webhook) | ไม่มีคำขอของผู้ถูกจำกัด | ไม่ถูกจำกัด (ไม่ใช่การกระทำของผู้ใช้คนนั้น) |

## 2. ออกแบบ (ตัดสินเองตามหลักใน brief)

### 2.1 ค่าตั้ง
- `CompanyUser.OwnDocumentsOnly` · `CompanyUser.DraftDocumentsOnly` · `CompanyRole.OwnDocumentsOnly` · `CompanyRole.DraftDocumentsOnly` (bool · ค่าตั้งต้น false = พฤติกรรมเดิม).
- ค่าที่มีผล (ตัวตัดสินเดียว `DocumentRestrictionPolicy.Effective`): ผู้ใช้ **หรือ** บทบาท · เจ้าของ/SystemAdmin ไม่ถูกจำกัดเสมอ (กันล็อกบริษัท) ·
  **"เห็นเฉพาะของตัวเอง" ⇒ บังคับ "เฉพาะร่าง" ด้วย** (คำตัดสิน 276-2 — การอนุมัติต้องเห็นทั้งบริษัท: เลขเอกสาร gap-free · ผลรวมใบลดหนี้ ≤ ใบเดิม · ใบซ้ำ · สต็อก ·
  ถ้าผู้ใช้ที่เห็นแค่ของตัวเองอนุมัติได้ ตัวตรวจเหล่านี้จะนับเฉพาะใบของเขา ⇒ ผิดกฎหมายเงียบ ๆ). "เฉพาะร่าง" อย่างเดียวใช้ได้ (เห็นทุกใบ อนุมัติไม่ได้).
- ผู้ตั้งได้: เจ้าของ (ชุดเดียวกับเปลี่ยนบทบาทสมาชิก `EnsureOwnerAccessAsync`) · ตั้งบนเจ้าของไม่ได้ · ตั้งบนตัวเองไม่ได้ · audit `AddChainedAuditLog` ทุกครั้ง ·
  บทบาทกำหนดเอง: ผู้มี `Roles.Manage` (ด่านเดิมของหน้าบทบาท).

### 2.2 ด่าน (บังคับที่เซิร์ฟเวอร์ทุกทาง)
1. **ตัวกรองแถว EF** บน `Documents`: `DocumentOwnerScopeUserId == null || CreatedBy == id || CreatedBy == email` — ค่าบน `AccountingDbContext` (scoped ต่อคำขอ)
   ตั้งโดย `DocumentRestrictionFilter` เฉพาะผู้ "เห็นเฉพาะของตัวเอง" · งานเบื้องหลัง/ผู้ใช้อื่น = null = ไม่มีผล.
2. **`DocumentRestrictionFilter`** (global action filter หลัง `TenantGuardFilter`): ค่าที่มีผลของผู้ใช้ในบริษัทของ route →
   - "เห็นเฉพาะของตัวเอง": คำขอทุกชนิดต้องอยู่ในรายการอนุญาต (`DocumentRestrictionPolicy.OwnScopeRoutes`) มิฉะนั้น 403 + เหตุ ·
     id เอกสารใน route ต้องเป็นของตัวเอง มิฉะนั้น **404** (ไม่บอกว่ามีใบนั้นอยู่) · ไฟล์แนบ/ความเห็นต้องเป็นของเอกสารที่มองเห็น.
   - "เฉพาะร่าง": คำขอเขียน (POST/PUT/PATCH/DELETE) ต้องอยู่ในรายการเขียนที่อนุญาต (`DraftWriteRoutes`) · แก้/ลบใบได้เฉพาะ Draft/Rejected.
3. **ชั้น service** `DocumentRestrictionGuard.EnsureMayFinalizeAsync` ใน `ApproveDocumentAsync` + `VoidDocumentAsync` — ผู้กระทำ (Guid/อีเมลสมาชิก) หรือคำขอนี้ถูกจำกัด ⇒ `BusinessRuleException` 403.
   ล็อกจุดเรียกด้วย `tools/required_call_site_check.py` (`_R276_`).

### 2.3 ขออนุมัติ (ปุ่มเดียวบนใบ)
- `POST document/{id}/approval-request` (ผู้สร้าง · ใบ Draft/Rejected): **กฎอนุมัติที่ตรง ⇒ ส่งเข้า workflow เดิม** (`ApprovalService.SubmitForApprovalAsync` — ผู้อนุมัติระบุตัว) ·
  ไม่มีกฎ + ผู้ขออนุมัติเองได้ ⇒ 409 "ไม่เข้ากฎ — กดอนุมัติได้เลย" (พฤติกรรมเดิม) · ไม่มีกฎ + ผู้ขออนุมัติเองไม่ได้ ⇒ **เส้นพื้นฐาน**:
  ใบ → `WaitingApproval` · แถว `DocumentApprovalRequests` (Pending) · แจ้ง `document.approval_requested` ถึงบทบาท `DocumentApprover`
  (เจ้าของ + นักบัญชี + ผู้มีสิทธิ์อนุมัติเอกสาร · ไม่รวมผู้ถูกจำกัด) ตามตารางแจ้งเตือนของบริษัท.
- ผู้อนุมัติ: ปุ่ม "อนุมัติ" (endpoint เดิม `/approve` → `ApproveDocumentAsync` — เลขออกตอนนี้ตามกฎ gap-free เดิม) หรือ "ตีกลับ" (`/approval-request/return` + เหตุผลบังคับ) ⇒ ใบ `Rejected` ·
  ผู้ขอห้ามอนุมัติ/ตีกลับใบที่ตัวเองขอ (SoD).
- ผู้สร้าง: **แก้ระหว่างรออนุมัติไม่ได้** (คงกติกาเดิม — ผู้อนุมัติต้องเห็นฉบับเดียวกับที่ถูกขอ) · ถอนคำขอได้ (`/approval-request/withdraw` ⇒ Draft) แล้วแก้/ขอใหม่ (คำตัดสิน 276-5).
- อนุมัติ/ตีกลับ ⇒ แจ้งผู้สร้าง (`document.approval_granted` / `document.approval_returned` · ผู้รับระบุตัว) · audit ทุกขั้นผ่าน `AddChainedAuditLog`.
- ข้อความแจ้ง: ชนิดเอกสาร · ยอดรวม · ชื่อผู้ขอ · ลิงก์เปิดใบ — **ไม่มีชื่อลูกค้า/เลขผู้เสียภาษี** (ข้อความเดียวกันถึงกลุ่ม LINE · คำตัดสิน 276-6).

### 2.4 UX
- หน้า "ทีม" (`team.html`): ต่อสมาชิก มีกล่อง "จำกัดเอกสาร" 2 ติ๊ก (ติ๊กแรกติ๊กที่สองให้อัตโนมัติ + ล็อกพร้อมเหตุ) · ป้ายบนแถวสมาชิก.
- หน้า "บทบาท" (`roles.html`): 2 ติ๊กเดียวกันต่อบทบาท.
- หน้าเอกสาร: แถบบนสุด "บัญชีของคุณเห็นเฉพาะเอกสารที่คุณสร้าง · สร้าง/แก้ได้เฉพาะร่าง — กด 'ส่งขออนุมัติ' ให้ผู้ดูแลอนุมัติ" (ข้อความจากเซิร์ฟเวอร์) ·
  ปุ่ม "อนุมัติ/ยกเลิก/แปลง/รับชำระ" ของผู้ถูกจำกัดซ่อน · กล่องสถานะคำขอ (รอ · ตีกลับพร้อมเหตุ · ถอน) จาก `GET document/{id}/approval-request` (เซิร์ฟเวอร์ตัดสิน `canApprove/canReturn/canWithdraw/canRequest`).
- คิว "เอกสารรออนุมัติ" บนหน้า approval (`GET document-approval-queue`) สำหรับผู้อนุมัติ.
- เมนู: ผู้ "เห็นเฉพาะของตัวเอง" เห็นเฉพาะเมนูในรายการอนุญาต (`DocumentRestrictionPolicy.OwnScopeMenuIds`) — เซิร์ฟเวอร์ส่งใน my-permissions.

## 3. คำตัดสิน (บันทึกใน `erp-review/2026-09-29/DECISIONS.md` §รอบ 276)
276-1 ถึง 276-8 — ดูไฟล์ DECISIONS.

## 4. ตรวจแล้วไม่ใช่บั๊ก / ไม่ทำ
- `PermissionKeys.DocumentViewAll` ไม่มีผู้อ่าน — ไม่ต่อสายเป็นตัวกรอง (ความหมายกลับด้าน: ถ้าต่อสายตอนนี้ ผู้ใช้เดิมทุกคนที่ไม่มีคีย์นี้จะเห็นแค่ของตัวเองทันที = เปลี่ยนพฤติกรรมเดิม) ·
  คงไว้เป็นคีย์ในหน้าบทบาทตามเดิม (ลบไม่ได้ — บทบาทที่บันทึกไว้อ้างอยู่).

## 5. เลื่อน (พร้อมเหตุ)
- **OCR** สำหรับผู้ถูกจำกัด (43 endpoint · มีนำเข้าสต็อก/อนุมัติรวด/รายการสแกนของทุกคน) — v1 ปิด (403 พร้อมเหตุ) · ต้องมีตัวกรองแถวของ `OcrScanResults` ก่อน.
- **ความเห็น (POST comments)** — v1 อ่านได้อย่างเดียว (ร่างคำขอมีช่องหมายเหตุแทน).
- **แชต AI / ค้นหาทั่วระบบ** — v1 ปิดสำหรับ "เห็นเฉพาะของตัวเอง" (ต้องตัดเครื่องมือของแชตที่อ่าน GL ก่อน).
- บริการที่เปิด scope ใหม่ระหว่างคำขอ (`IServiceScopeFactory`) ไม่ได้ตัวกรองแถว — อยู่นอกรายการอนุญาตทั้งหมด (ตรวจแล้ว 0 จุดในรายการอนุญาต).

## 6. ผล

| ส่วน | sha |
| --- | --- |
| โค้ดหลัก (ค่าตั้ง · ตัวกรองแถว · filter · guard · ขออนุมัติ · แจ้งเตือน · UI) | 69740aef |
| เทสต์ · กติกา `_R276_` · เอกสาร · แก้ตามด่าน check_all · ด่านสิทธิ์ชั้น controller | 4456de7a |

- ตรวจในเครื่อง: build ✅ · เทสต์ที่เกี่ยวข้อง 53 ตัว (pure + Db `accounting_test_r276`) ✅ · Db ที่แตะ Documents (DuplicateSale/ApproveRevert/Purge/Link) 26 ตัว ✅ ·
  `check_all --no-dotnet` ข้อที่เป็นของรอบนี้ผ่านทั้งหมด (ข้อที่ยังแดงเป็นของแพ็กอื่น: `TaxSlipDeskRule.AgencyTypeProblem` dead helper · `RateLimitMiddleware` using · placeholder sha 275-A)
- ตอบ F3 ข้อ 7–12: (7) รูปแบบเดิม "ไม่มีตัวกรองแถว" — ทางอ่านเอกสารทั้งหมดผ่าน `_db.Documents` ได้ตัวกรองเดียว · `IgnoreQueryFilters` บน Documents 68 จุดอยู่นอกรายการอนุญาตทั้งหมด (void/purge/เลขเอกสาร/งานระบบ) ·
  (8) เว็บ · มือถือ · LINE · workflow · bulk-approve · อนุมัติอัตโนมัติ เดินด่าน service เดียว · CSV/API key ไม่ใช่ผู้ใช้ (ไม่จำกัด) · งานเบื้องหลังไม่จำกัด ·
  (9) ผู้ถูกกัน: "ส่งขออนุมัติ" + ข้อความบอกเหตุทุก 403 · เทสต์ทิศตรงข้าม `ผู้ไม่ถูกจำกัด_*` · (10) ค่าตั้งต้น false — ไม่มีข้อมูลเก่าผิด · migration Round276 ·
  (11) ฝ่ายค้าน: ยังไม่ได้ส่ง (ทีมเดียว) — เจ้าของ/ผู้ประสานงานควรส่ง diff ให้ฝ่ายค้านหนึ่งรอบ · (12) DOCUMENT_FLOW §3.2-276 · ACCOUNT_STRUCTURE (`CompanyUser`) · TEST_PLAN P276 · CHANGELOG · DECISIONS 276-1…8

### สิ่งที่เจ้าของต้องทำหลัง deploy
1. หน้า "ทีม" → ปุ่ม 🔒 จำกัดเอกสาร ของพนักงานที่ต้องการ (หรือหน้า "บทบาท" → ช่อง "จำกัดเอกสาร")
2. หน้า "การแจ้งเตือน" → เหตุการณ์ "พนักงานส่งเอกสารร่างขออนุมัติ" → เลือกช่อง (กระดิ่ง/อีเมล/LINE ส่วนตัว/กลุ่ม LINE) — ค่าตั้งต้นเปิดทุกช่องถึง "ผู้อนุมัติเอกสาร (เจ้าของ/บัญชี)"
3. ผู้อนุมัติที่ต้องการแจ้ง LINE ส่วนตัวต้องผูก LINE กับบัญชีผู้ใช้แล้ว · กลุ่ม LINE ใช้ OA ของบริษัท (ตั้งค่า LINE) · อีเมลใช้ SMTP ของบริษัท

## 7. ชุดแก้ฝ่ายค้าน 276 (HIGH 2 · MED 4 · LOW 6) — คำตัดสิน 276-9…16 · commit 8a2514f3 (โค้ด) + 9ca871bb (กติกา·เทสต์·เอกสาร)

| ข้อ | ผล | วิธีแก้ |
| --- | --- | --- |
| หลัก | ✅ | ด่านผูก **ผู้ใช้ + บริษัทที่ทำงานอยู่**: route มี {companyId} ⇒ บริษัทนั้น · ไม่มี ⇒ header `X-Company-Id` สำหรับตัวกรองแถว + ค่าจำกัดที่เข้มที่สุดของทุกบริษัทที่เป็นสมาชิกสำหรับรายการอนุญาต (`DocumentRestrictionGuard.StrictestAsync`) · ข้ามเฉพาะ `[AllowAnonymous]`/API key |
| HIGH-1 LINE | ✅ | `LineBotService` ตั้งขอบเขตทุกเหตุการณ์ (ข้อความ/รูป/ปุ่ม) · `DocumentRestrictionPolicy.LineCommandDenied`: ดูยอด (เห็นเฉพาะของตัวเอง) · ถาม (ทั้งสองแบบ) · ปุ่มอนุมัติ ⇒ ตอบเหตุ · "บันทึก"/ส่งรูป ⇒ ร่างของผู้ส่ง (ไม่อนุมัติอัตโนมัติ · ไม่ส่งการ์ดปุ่มอนุมัติ) |
| HIGH-2 | ✅ | `api/accountant-workspace` (และทุกทางเข้าไม่มี companyId ที่ไม่อยู่ในรายการ) 403 — ผลไล่ด้านล่าง |
| MED-3 | ✅ | `NotificationEngine` ตัดผู้รับ "เห็นเฉพาะของตัวเอง" ทั้งเส้นบทบาทและผู้รับระบุตัว ยกเว้นเรื่องของใบที่เขาสร้าง (`MayReceiveAsync`) · `api/notification` + ตัวนับกรองแถวเก่าด้วยกติกาเดียวกัน |
| MED-4 | ✅ | ทางเขียนของผู้เห็นเฉพาะของตัวเอง: ตรวจ id ทุกตัวแล้ว **ยกตัวกรองแถว** ⇒ ด่านระดับบริษัทใน service (ยอดที่แปลงไปแล้ว · ผลรวมใบลดหนี้ · ใบซ้ำ · สต็อก) เห็นทั้งบริษัท (ธงเฉพาะร่าง/ผู้สร้างคงอยู่) · `CheckDuplicateAsync` ตรวจทั้งบริษัท คืนแค่ผลตัดสินของใบที่มองไม่เห็น (`RedactDuplicate`) · ตอนอนุมัติ: ใบที่ผู้ถูกจำกัดสร้างแล้วยอดรายได้สะสมเกิน ⇒ **บล็อก** `R276-OVER-CONVERT` (ใบทั่วไปยังเป็นคำเตือนตามข้อ 138) · Db test แปลงซ้ำใบที่คนอื่นแปลงแล้ว ⇒ ถูกกัน |
| MED-5 | ✅ | id ใน body (`*DocumentId` · `SourceLineId` ลึก 2 ชั้น รวมบรรทัด) ต้องเป็นของที่มองเห็น ⇒ ไม่ใช่ 404 (`DocumentRestrictionFilter.BodyReferences`) |
| MED-6 | ✅ | `PUT document/contacts/{id}` ออกจากรายการ ⇒ 403 + เหตุ · สร้างผู้ติดต่อใหม่ได้ |
| LOW-7 | ✅ | `EnsureNotSelfApprovalAsync` + `OnDocumentApprovedAsync` แปลงผู้กระทำอีเมล→id (`ActorUserIdAsync`) |
| LOW-8 | ✅ | `GET …/approval-request` ตรวจฝั่งเอกสาร + ชั้นความลับ (ด่านเดียวกับหน้าเอกสาร) ⇒ 404 |
| LOW-9 | ✅ | `NotificationContext.PrivateNote` ต่อเฉพาะช่องส่วนตัว — หมายเหตุผู้ขอไม่เข้ากลุ่ม LINE |
| LOW-10 | ✅ | `Document.CreatedByUserId` ประทับที่ `SaveChanges` (id ใน CreatedBy · อีเมลสมาชิกคนเดียว · ผู้ใช้ของคำขอที่ถูกจำกัดเมื่อเป็นป้ายระบบ) · ตัวกรองแถวใช้ Guid เป็นหลัก (ข้อความเป็นทางสำรองของแถวที่ยังว่าง) · Round276Migration เติมแถวเก่าที่จับคู่ได้แน่นอน |
| LOW-11 | ✅ | `DocumentRestrictionRouteInventoryTests` ไล่ทุก route ของแอป: แถวตรง action · route ข้อมูลรวมไม่หลุด · ชื่อพารามิเตอร์ที่ตัวกรองตรวจได้ · สัดส่วนที่อนุญาต |
| LOW-12 | ✅ | เปิด OCR สร้างร่างบนเว็บ (upload · scan · ดู/แก้/สร้างเอกสารจากสแกน · ลบ) เฉพาะสแกน/ไฟล์ที่ผู้ใช้อัปโหลดเอง (`RouteCheck.OwnScan`) · นำเข้าสต็อก/สินทรัพย์/JE/คิวตรวจ/ตั้งค่า ปิด · LINE ทางเดียวกัน |

### ผลไล่ controller ที่ไม่มี {companyId} ใน route (HIGH-2)
| กลุ่ม | ผลสำหรับผู้ถูกจำกัด |
| --- | --- |
| `Admin*` · `AiAdmin` · `MeteringAdmin` · `AiUsageReport` | ปิด (ไม่อยู่ในรายการ · ต้องเป็นแอดมินแพลตฟอร์มอยู่แล้ว) |
| `AccountantWorkspace` (ภาพรวมข้ามบริษัท) | **ปิด** |
| `AccountSubscription` · `Subscription/payments` · `trial/start` · `VendorPortal` | ปิด (บิล/ข้อมูลบริษัท) |
| `Notification` GET/count/mark-read | เปิด — กรองแถวตามค่าจำกัด (MED-3) · `send` ปิด |
| `Auth/*` · `me/permissions` · `company` (GET) · `signatures` (ลายเซ็นของตัวเอง) · `mobile/devices` · `invitations` | เปิด (ข้อมูลของผู้ใช้เอง) |
| `gov/*` · `dbd/*` · `reference/*` · `subscription/plans|feature-catalog` · `integration/link-patterns` | เปิด GET (ข้อมูลอ้างอิงสาธารณะ) |
| `Public*` · `Legal` · `HelpCenter` · `ContactInquiry` · `Cms*` (resolve) | นิรนาม — ไม่ใช่ข้อมูลภายในบริษัท |
