# ทีม G — ความปลอดภัย / tenant isolation / สิทธิ์ (ส่วนที่ SYSTEM_REVIEW §10 ระบุว่ายังไม่ได้ตรวจ)

> HEAD ตรวจ: a054340 · วิธี: อ่านโค้ด + grep call site เท่านั้น (ไม่มี .NET SDK) · เขียน append ทีละ finding
> ขอบเขต: 1 SignatureApproval ภายนอก · 2 PayslipPublic · 3 Impersonation · 4 CMS anonymous · 5 LineBot · 6 /api/v1 · 7 allow-list ของ write_permission_gate_check · 8 file access

## สรุป 5 บรรทัด
1. **ไม่พบ IDOR/รั่วข้ามบริษัทแบบตรง ๆ ใน 8 พื้นที่ที่ §10 กังวล** — external approve เป็น POST ใต้ TenantGuard · token สาธารณะ (สลิป/ใบเสนอราคา/ที่พัก) เป็น crypto 128-256 bit มีอายุ/เพิกถอน · CMS anonymous ส่ง companyId+siteId ครบ + sanitizer · /api/v1 มี scope ทุก endpoint
2. **ปัญหาหลักคือ "สิทธิ์" ไม่ใช่ "ตัวตน"**: เส้นอนุมัติทางอ้อม 3 ทาง (ลายเซ็น G-01 · มือถือ G-05 · LINE text G-03) ไม่มี `Document.Approve` gate ⇒ ด่านที่ใส่ใน DocumentController รอบก่อนอ้อมได้ทันที และ LINE postback ใช้สำเนา role (G-04)
3. **รหัสผูก LINE 6 หลักค้นข้ามทุกบริษัทโดยไม่มี attempt limit** (G-02) = ทางยึดบัญชี/รับสลิปคนอื่นที่ราคาถูกที่สุดในระบบ — ยิ่งผู้เช่ามาก ยิ่งชนง่าย
4. **allow-list ของ checker เฝ้าแค่ 3 ไฟล์** — อีก ≥ 20 controller ที่ลง JE/ภาษี/สต๊อก (50 ทวิ · เช็ค · สินทรัพย์ · เงินทดรอง · recurring · import) มี permRefs = 0 (G-07) ซ้ำรอยบทเรียน Metering ทุกประการ
5. เชิงโครงสร้าง: ลูกค้าหน้าร้าน CMS ได้ JWT ชนิดเดียวกับผู้ใช้ระบบ (G-06) และ impersonation บันทึก PII log เป็น Owner ไม่ใช่แอดมิน (G-08) — ยังไม่ระเบิดวันนี้ แต่เป็นระเบิดเวลา 2 ลูกที่ CLAUDE.md เคยจดบทเรียนไว้แล้ว

**ลำดับที่แนะนำ**: G-02 (S, ปิดทันที) → G-01+G-05+G-03 (คอมมิตเดียว: helper `CanApproveAsync` ทุกช่องทาง + เพิ่ม 3 ไฟล์เข้า WATCHED) → G-07 (เพิ่ม 20 ไฟล์เข้า WATCHED แล้วไล่ตามที่ checker ฟ้อง) → G-04/G-06/G-08/G-09

## Findings (เรียง P0→P3 — ลำดับตอนเขียน = ลำดับที่พบ; จัดเรียงใหม่ในสรุปท้ายไฟล์)

### G-01 [P1][S] เส้นอนุมัติผ่านลายเซ็น (`/approvals`, `/external`) ไม่มีด่านสิทธิ์ `Document.Approve` — สมาชิกคนไหนก็ตั้งขั้นอนุมัติเอง/อนุมัติแทน/ยิง "อนุมัติจากลูกค้า" ได้ และข้ามขั้นภายในที่ยัง Pending
- ไฟล์: `Accounting/Controllers/SignatureApprovalController.cs:53-56,63-67,83-90,113-116,124-131` · `Accounting/Services/Implementations/SignatureApprovalService.cs:96-108,196,321-399`
- โค้ด:
  - controller ทั้ง 3 คลาสมีแค่ `[Authorize]` ระดับคลาส — `grep -n "RequirePermission\|PermissionKeys\|HasPermission" SignatureApprovalController.cs SignatureApprovalService.cs` = **0 จุด** ขณะที่ `DocumentController.cs:61-75` มี `DenyDocAsync(... DocPerm.Approve ...)` ครอบทางเว็บปกติ
  - `SetupApprovalAsync :101-105` `foreach (var e in existing) e.IsDeleted = true;` แล้วสร้างขั้นใหม่ตาม `request.Steps` (ผู้เรียกกำหนด `ApproverUserId` เองได้)
  - `ApproveAsync :196` `if (approval.ApproverUserId.HasValue && approval.ApproverUserId.Value.ToString() != userId) throw` — ขั้นที่ตั้งเป็น role (ApproverUserId=null) ใครก็อนุมัติได้
  - `ExternalApproveQuotationAsync :331` ตรวจแค่ `doc.Status == Approved` → :397-398 `await _docService.ApproveDocumentAsync(companyId, documentId, $"external:{request.ApproverName}", acknowledgeWarnings: true)` **โดยไม่ตรวจว่ามีขั้นภายใน (Manager/Finance) ที่ยัง Pending** ต่างจาก `ApproveAsync :200-204` ที่เช็ค `previousPending` และ `CheckAllApprovedAndProcessAsync :456` ที่เช็ค `All(Approved)`
- ทำไมพัง: (1) สมาชิกที่ไม่มี `Document.Approve` เรียก `POST /api/companies/{cid}/external/quotations/{id}/approve` พร้อม `SignatureData` อะไรก็ได้ + `ApproverName` → (2) service สร้างขั้น "Customer" ให้เองแล้วตั้ง Approved ทันที → (3) เรียก `ApproveDocumentAsync` ตรง ⇒ ใบเสนอราคาได้เลข/สถานะ Approved โดยข้ามทั้งด่านสิทธิ์ของเว็บและขั้นอนุมัติภายในที่ผู้จัดการยังไม่กด · `AutoConvert=true` แถมสร้าง Invoice ให้ด้วย (`:409-421`) · ทางที่ 2: `POST /approvals/setup` ลบขั้นเดิมทั้งหมดแล้วตั้งตัวเองเป็นผู้อนุมัติขั้นเดียว → กด `approve` → `CheckAllApprovedAndProcessAsync` อนุมัติเอกสารทุกชนิด (ไม่จำกัด Quotation) พร้อม `acknowledgeWarnings: true` ข้าม warning dialog ทั้งหมด
- ผลกระทบ: ด่าน `Document.Approve`/`Document.Revenue.Approve` ที่เพิ่งใส่ใน DocumentController (รอบ write_permission_gate) เป็นโมฆะ — มีทางอ้อม 2 ทางที่ใช้ JWT ของสมาชิกธรรมดา · ลายเซ็น "ผู้ซื้อ" ที่แนบเป็นข้อมูลที่ผู้เรียกพิมพ์เอง (ไม่ได้มาจากลูกค้า) ถูกบันทึกเป็น `DocumentSignature SignerRole="ผู้ซื้อ"` และพิมพ์ลงเอกสาร = หลักฐานปลอม · Invoice ที่สร้างอัตโนมัติเข้า AR/ภ.พ.30
- defect class (CLAUDE.md): "`[Authorize]` ระดับคลาส = ล็อกอินอยู่ไหม ไม่ใช่มีสิทธิ์ทำสิ่งนี้ไหม" + "ด่านที่ครอบแค่ทางเดียว คือด่านที่ไม่มี" + allow-list ของ `write_permission_gate_check` ไม่ครอบไฟล์นี้ (ดู G-07)
- ทางแก้ที่เสนอ: (ก) `ExternalApprovalController`/`DocumentApprovalController.Approve|Reject|SetupApproval` เรียก `DocumentPermissionHelper.CanApproveAsync` (setup อย่างน้อย Create) ก่อน (ข) `ExternalApproveQuotationAsync` ต้องเช็ค pending ขั้นอื่นแบบเดียวกับ `ApproveAsync` หรือเดินผ่าน `CheckAllApprovedAndProcessAsync` เท่านั้น (ค) เพิ่ม `SignatureApprovalController.cs` เข้า allow-list ของ checker
- ความมั่นใจ: สูง (อ่านโค้ดครบทั้ง 3 คลาส; ยังไม่ได้ยิงจริง) — ต้องเช็คต่อ: `DocumentPermissionHelper.CanApproveAsync` คืน true ให้ Owner/Admin เสมอหรือไม่ (ถ้าบริษัทส่วนใหญ่มีสมาชิกเป็น Owner หมด ผลกระทบจริงลด)

### G-02 [P1][S] รหัสผูก LINE 6 หลัก (ทั้ง "ผูก" บัญชีผู้ใช้ และ "สลิป" พนักงาน) ค้นข้ามทุกบริษัท ไม่มีตัวนับความพยายาม — เดาได้จาก LINE ใครก็ได้ที่ add OA ⇒ ยึดบัญชี/รับสลิปของคนอื่น
- ไฟล์: `Accounting/Services/Implementations/LineBotService.cs:53-54,96-104` · `Accounting/Services/Implementations/PayslipLineDeliveryService.cs:53,80-97` · `Accounting/Controllers/LineWebhookController.cs:29-60`
- โค้ด:
  - `LineBotService.cs:100-103` `_db.LineBindCodes.Include(c => c.User).Where(c => c.Code == code && c.UsedAt == null && c.ExpiresAt > DateTime.UtcNow).FirstOrDefaultAsync()` → `:107 row.User.LineUserId = lineUserId;`
  - `PayslipLineDeliveryService.cs:84-86` `.Where(c => c.Code == code && c.UsedAt == null && c.ExpiresAt > DateTime.UtcNow).OrderByDescending(c => c.CreatedAt)` → `:92 row.Employee.LineId = lineUserId;`
  - `LineBotService.cs:53` `RandomNumberGenerator.GetInt32(0, 1_000_000).ToString("D6")` · อายุ 10 นาที (ผู้ใช้) / **24 ชม.** (`BindCodeValidHours = 24` สลิป)
  - ไม่มี `Attempts`/lockout ต่อ `lineUserId` ทั้งสองเส้น; `LineWebhookController.Receive` วนทุก event ในคำขอเดียว (`:44 foreach (var ev in events...)`) และ `RateLimitMiddleware` นับต่อ IP ของ **เซิร์ฟเวอร์ LINE** ไม่ใช่ต่อผู้ส่ง
- ทำไมพัง: (1) รหัสเป็นตัวเลข 6 หลัก = 10^6 ค่า ค้นด้วย `Code == code` **ไม่มี CompanyId/OA** ⇒ ทุกรหัสที่ค้างอยู่ทั้งแพลตฟอร์มเป็นเป้าพร้อมกัน (โอกาสต่อครั้ง = N_outstanding / 10^6) → (2) ผู้โจมตี add OA แล้วส่ง "สลิป 000001", "สลิป 000002", … (สคริปต์ LINE client หรือหลายบัญชี) ระบบตอบ ❌/✅ ทันที = oracle → (3) ชนรหัสของพนักงานคนใดในบริษัทใดก็ได้ ⇒ `Employee.LineId` = LINE ของผู้โจมตี ⇒ ได้ลิงก์สลิป (เงินเดือน/เลขบัตร mask/ชื่อ) ทุกงวดถัดไปแบบเงียบ (HR ไม่มีทางรู้ว่า LINE ปลายทางไม่ใช่พนักงาน) · เส้น "ผูก" อายุสั้น 10 นาทีแต่ผลหนักกว่า: ได้ตัวตน `User` เต็ม → บันทึกค่าใช้จ่าย/ส่งรูป OCR สร้างเอกสาร/กดอนุมัติ (ดู G-03/G-04) ในบริษัทเหยื่อ
- ผลกระทบ: PDPA ม.26/ม.37 (ข้อมูลเงินเดือนรั่วข้ามบริษัท) · account takeover ผ่านช่องทางที่ไม่มีรหัสผ่าน · ยิ่งแพลตฟอร์มมีผู้เช่ามาก โอกาสชนยิ่งสูง (ตรงข้ามกับสัญชาตญาณ "6 หลักก็พอ" ที่ถูกเมื่อผูกต่อบริษัท)
- defect class (CLAUDE.md): "checksum ผ่าน ≠ เป็นเลขนั้นจริง" ญาติ — ค่าที่ตรงกัน ≠ พิสูจน์ตัวตน · "ด่านที่เขียนไว้ครึ่งเดียว" (มี expiry แต่ไม่มี attempt limit) · ตัวคูณจาก multi-tenant (คีย์ไม่ scope)
- ทางแก้ที่เสนอ: ตัวนับผิดต่อ `lineUserId` ใน DB (pattern `ChatRateLimiter`) เช่น 5 ครั้ง/ชม. แล้วเงียบ · รหัสยาวขึ้น (8-10 หลัก หรือ alnum) · scope ด้วย OA/ช่องทาง (ถ้าใช้ channel เดียวทั้งแพลตฟอร์ม ให้ผูกรหัสกับ `CompanyId` ที่แสดงบนหน้าจอให้พิมพ์ด้วย เช่น "สลิป ABC-123456") · ลดอายุรหัสสลิปจาก 24 ชม.
- ความมั่นใจ: สูงเชิงตรรกะ / กลางเชิงปฏิบัติ — ต้องเช็คต่อ: LINE Messaging API มี throttle ฝั่ง inbound webhook ต่อผู้ใช้หรือไม่ (ประเมินว่าไม่มีในระดับที่กันได้) และจำนวนรหัสค้างจริงบน production

### G-03 [P1][S] LINE "บันทึก {ร้าน} {จำนวน}" สร้าง Expense แล้ว **อนุมัติอัตโนมัติโดยไม่มีด่านสิทธิ์ใด ๆ** + `catch {}` กลืน error แล้วตอบ ✅ พร้อม "เลขที่เอกสาร" ที่อาจเป็น DRAFT
- ไฟล์: `Accounting/Services/Implementations/LineBotService.cs:230-263`
- โค้ด: `:248 var doc = await _docService.CreateDocumentAsync(companyId, new CreateDocumentRequest(DocumentType: Expense, …), createdBy: user.Email);` → `:262 try { await _docService.ApproveDocumentAsync(companyId, doc.Id, user.Email); } catch { /* show success even if approve hiccups */ }` → `:263 return $"✅ บันทึกแล้ว {vendor} {amount.Value:N2} ฿\nเลขที่เอกสาร: {doc.DocumentNumber}";`
- ทำไมพัง: (1) เส้นนี้ตรวจแค่ `UserLoginPolicy` + สมาชิกบริษัท (`:120-135`) — ไม่มี role/permission check เลย ต่างจาก `HandlePostbackAsync :596-600` ในไฟล์เดียวกันที่บล็อก Staff/Viewer/Auditor → (2) Viewer ที่ผูก LINE ไว้ (หรือผู้โจมตีจาก G-02) พิมพ์ "จ่าย xxx 99999" ⇒ Expense ถูก **Approve ทันที** = ออกเลข + JE ค่าใช้จ่าย/เจ้าหนี้ + `Contact` ผู้ขายใหม่ถูกสร้าง (`:241-243`) → (3) ถ้า Approve ล้ม (งวดปิด/§65ตรี/ผังบัญชีไม่มี) `catch {}` กลืน แล้วข้อความยังบอก "✅ บันทึกแล้ว … เลขที่เอกสาร: DRAFT-…" ผู้ใช้เข้าใจว่าลงบัญชีแล้ว
- ผลกระทบ: JE/ภาษีจากผู้ใช้ที่ระบบตั้งใจไม่ให้อนุมัติ · ค่าใช้จ่ายปลอมเข้า ภ.ง.ด.50 · ร่างค้างเงียบเมื่อ approve ล้ม
- defect class (CLAUDE.md): "ด่านที่ครอบแค่ทางเดียว" (postback มีด่าน · text ไม่มี — ไฟล์เดียวกัน) · "ห้าม `catch {}` กลืน error ใน payment/stock/JE path" · "ข้อความตอบกลับต้องบอกสถานะจริง"
- ทางแก้ที่เสนอ: แยก "บันทึกร่าง" (ต้อง Create) กับ "อนุมัติ" (ต้อง Approve — ใช้ helper เดียวกับ G-04) · ถ้า approve ล้มให้ตอบ "บันทึกร่างแล้ว ยังไม่ลงบัญชี: {เหตุผล}" + ปุ่มอนุมัติ
- ความมั่นใจ: สูง

### G-04 [P2][S] LINE postback อนุมัติใช้ **สำเนามือของกติกาสิทธิ์** (`membership.Role is Owner/Accountant/…`) แทน `DocumentPermissionHelper.CanApproveAsync` ⇒ ไม่ตรงกับเว็บ/มือถือ
- ไฟล์: `Accounting/Services/Implementations/LineBotService.cs:594-600` เทียบ `Accounting/Controllers/DocumentController.cs:61-75` (`DocumentPermissionHelper.CanApproveAsync`)
- โค้ด: `if (membership.Role is not (UserRole.Owner or UserRole.Accountant or UserRole.ExternalAccountant or UserRole.SystemAdmin)) return "❌ สิทธิ์ของคุณอนุมัติเอกสารไม่ได้ …"`
- ทำไมพัง: (1) เว็บตัดสินจาก permission key (`Document.Approve` / `Document.Purchase.Approve`, custom role strict mode) → (2) LINE ตัดสินจาก enum role → (3) Staff ที่แอดมินให้ `Document.Approve` ผ่าน custom role อนุมัติในเว็บได้แต่ LINE ปฏิเสธ (feature พัง) · Accountant ที่ถูกถอด `Document.Purchase.Approve` ยังอนุมัติผ่าน LINE ได้ (ด่านรั่ว) · ไม่แยกทิศซื้อ/ขาย
- ผลกระทบ: สิทธิ์ที่แอดมินตั้งไม่มีผลกับช่องทาง LINE ทั้งสองทิศ
- defect class (CLAUDE.md): "สำเนามือฝั่ง … ที่ตามหลังอยู่ไม่กี่ธง อันตรายกว่าสำเนาที่ผิดชัด ๆ" · "ด่านต้องรวมเป็นเมธอดเดียว"
- ทางแก้ที่เสนอ: เรียก `DocumentPermissionHelper.CanApproveAsync(_permissions, doc.CompanyId, user.Id, doc.DocumentType)` แทน
- ความมั่นใจ: สูง (ต้องเช็ค: `IPermissionService` inject ได้ใน LineBotService โดยไม่เกิด DI cycle — รัน `tools/di_cycle_check.py` ตอนแก้)

### G-05 [P1][S] อนุมัติผ่านมือถือ (`MobileApiService.QuickApproveAsync`) — B-02 แก้ให้เดิน `ApproveDocumentAsync` แล้ว แต่ **ยังไม่มีด่านสิทธิ์และไม่ตรวจว่าผู้กดเป็นผู้อนุมัติของขั้นนั้น**
- ไฟล์: `Accounting/Controllers/MobileController.cs` (permRefs = 0, `[Authorize]` ระดับคลาส, route `companies/{companyId:guid}/approve`) · `Accounting/Services/Implementations/MobileApiService.cs:330-392`
- โค้ด: `:347-353 var approvalRequest = await _db.ApprovalRequests…FirstOrDefaultAsync(ar => ar.CompanyId == companyId && ar.EntityId == entityId && … OverallStatus == Pending)` → `:366-369 new ApprovalAction { StepOrder = approvalRequest.CurrentStep, ApproverUserId = userId, Status = newStatus }` → `:389 approvalRequest.OverallStatus = Approved` → `ApproveDocumentAsync` — ไม่มี `HasPermissionAsync`/`CanApproveAsync`/เทียบ `ApprovalStep.ApproverUserId|Role` กับ `userId` เลยในเมธอด (grep คำว่า Permission/Role/CanApprove ในช่วง :330-392 = 0)
- ทำไมพัง: (1) สมาชิกใดก็ได้ (Viewer) รู้ `entityId` ของเอกสารที่รออนุมัติ (GET `/mobile/companies/{cid}/pending` คืนให้อยู่แล้ว) → (2) POST approve → ระบบบันทึกว่า "ผู้กด = ผู้อนุมัติของขั้นปัจจุบัน" แล้วเลื่อนขั้น → (3) ครบขั้น ⇒ `ApproveDocumentAsync` ออกเลข/JE จริง
- ผลกระทบ: workflow อนุมัติหลายขั้น (ApprovalRequest/ApprovalStep) ไร้ความหมายผ่านช่องมือถือ — ญาติตรงของ G-01 คนละ workflow engine (SignatureApproval vs ApprovalRequest)
- defect class (CLAUDE.md): "ด่านที่ครอบแค่ทางเดียว" · `[Authorize]` ≠ สิทธิ์ · "แก้ตัวเดียว เหลือที่เหลือ" (B-02 แก้ Status writer แต่ไม่ได้ถามว่าใครกดได้)
- ทางแก้ที่เสนอ: ก่อน `ApprovalAction` ตรวจ `ApprovalStep` ของ `CurrentStep` ว่า `ApproverUserId == userId` หรือ role ตรง และเรียก `DocumentPermissionHelper.CanApproveAsync` เมื่อ EntityType=Document · เพิ่ม `MobileController.cs` เข้า WATCHED
- ความมั่นใจ: สูง (ต้องเช็คต่อ: `ApprovalRequests` ถูกสร้างจากที่ไหน — ถ้าไม่มีใครสร้าง เส้นนี้อาจไม่มีวันเจอ Pending และผลกระทบจริงเป็น 0 — grep `ApprovalRequests.Add` ยังไม่ได้ทำ)

### G-06 [P2][M] ลูกค้าหน้าร้าน CMS ล็อกอินแล้วได้ **JWT ชนิดเดียวกับผู้ใช้ระบบ** (`JwtHelper.GenerateToken(customer.Id, …)`) — ผ่าน `[Authorize]` ของทุก controller ที่ไม่มี `{companyId}` ใน route · และ endpoint นี้ **ไม่มีหน้าเว็บไหนเรียก**
- ไฟล์: `Accounting/Services/Implementations/CmsCustomerService.cs:166` · `Accounting/Controllers/CmsCustomerController.cs:70-86` · `Accounting/Helpers/JwtHelper.cs:20-28` · `Accounting/Middleware/TenantAccessMiddleware.cs:130-148`
- โค้ด: `var token = JwtHelper.GenerateToken(customer.Id, customer.Email, customer.FullName ?? "", _config);` — claim ชุดเดียวกับ `AuthService.cs:1566` (NameIdentifier/Email/Name/Jti) ไม่มี claim แยกชนิด · `TenantAccessMiddleware` ปล่อยผ่านเมื่อ route ไม่มี companyId (`:130-135` → `:66-70 if (companyId == null) { await _next; return; }`) · `grep -rn "cms/sites/.*portal/login" wwwroot` = 0 (portal.html เรียก `/api/portal/login` ของ PortalController คนละตัว)
- ทำไมพัง: (1) ใครก็สมัคร/ล็อกอินเป็นลูกค้าหน้าร้านของเว็บใดก็ได้ (anonymous) → (2) ได้ Bearer ที่ `[Authorize]` ยอมรับทั้งระบบ → (3) เรียก controller ที่ไม่มี companyId: `CompanyController` (POST สร้างบริษัท — วันนี้ล้มด้วย FK `CompanyUser.UserId→Users` Restrict ⇒ 500) · `DbdLookupController` (ยิง API กรมพัฒน์ผ่านเราไม่จำกัด) · `GovernmentServiceController` 20 ep · `NotificationController`/`MobileController`/`SubscriptionController`/`AccountSubscriptionController` (คีย์ด้วย userId ที่ไม่มีใน Users → ส่วนใหญ่ได้ค่าว่าง/500) — วันนี้ยังไม่พบเส้นที่ให้ข้อมูลผู้เช่าออกมา แต่ทุก endpoint ใหม่ที่ไม่มี companyId และเชื่อ NameIdentifier จะเปิดให้คนแปลกหน้าทันที
- ผลกระทบ: ขอบเขตความเชื่อถือของ `[Authorize]` ทั้งระบบเปลี่ยนจาก "สมาชิกที่ผ่าน KYC ของเรา" เป็น "ใครก็ได้บนอินเทอร์เน็ต" โดยไม่มีใครรู้ · ฟีเจอร์ล็อกอินลูกค้า CMS เองยังไม่ได้ต่อสาย (dead) — ความเสี่ยงเกิดขึ้นทั้งที่ยังไม่มีใครใช้
- defect class (CLAUDE.md): "**token คนละวัตถุประสงค์ต้องใช้กุญแจคนละดอก**" (บทเรียน SSO ticket) · "ของที่สร้างไว้แล้วไม่ได้ถูกเรียกใช้" · "entity ที่ไม่ใช่ tenant entity คือจุดที่ global guard ช่วยไม่ได้"
- ทางแก้ที่เสนอ: ออก token ด้วย secret+purpose แยก (`|storefront-customer`) และ scheme/policy แยก ให้ `[Authorize]` เริ่มต้นไม่รับ · หรือถ้ายังไม่ใช้ ให้ปิด endpoint login/register ไปก่อน (ตัดสินใจ "ต่อสาย หรือ ลบ")
- ความมั่นใจ: สูงในกลไก / กลางในผลกระทบปัจจุบัน — ต้องเช็คต่อ: ไล่ 11 controller ที่ไม่มี companyId (ตารางในสรุปท้าย) ทีละเมธอดว่ามีตัวไหน "สร้าง" แถวที่อ้าง userId โดยไม่มี FK (เช่น UserSignature, MobileDevice, NotificationPreference) → ข้อมูลขยะ + เส้นทางเข้าในอนาคต

### G-07 [P1][M] allow-list ของ `tools/write_permission_gate_check.py` เฝ้าแค่ 3 ไฟล์ — controller ที่แตะเงิน/ภาษี/HR/สต๊อกอีก ≥ 20 ไฟล์ **ไม่มี RequirePermission/HasPermission แม้แต่จุดเดียว** (ตรวจแล้วรายไฟล์)
- ไฟล์: `tools/write_permission_gate_check.py:30-37` `WATCHED = [DocumentController, PayrollController, MeteringController]`
- ตารางที่นับจริง (regex `\[Http(Post|Put|Patch|Delete)` vs `RequirePermission|HasPermissionAsync|Deny\w*Async|Can(Approve|Create|Void)Async|PermissionKeys\.`):

| Controller | WRITE ep | permRefs | ตัวอย่าง write ที่ลง JE/ภาษี/เงิน | สถานะเดิม |
| --- | --- | --- | --- | --- |
| WithholdingTaxCertController | 9 | 0 | `{certId}/issue` `{certId}/void` `DELETE {certId}` `bulk-generate` — 50 ทวิ = เอกสารภาษี | **ใหม่** |
| ChequeController + PostDatedCheckController | 5+5 | 0 | `{chequeId}/clear` `{chequeId}/bounce` → JE ธนาคาร | **ใหม่** |
| FixedAssetController | 9 | 0 | `dispose` `writeoff` `revalue` `depreciate` `DELETE` → JE ค่าเสื่อม/จำหน่าย | **ใหม่** |
| CashAdvanceController · LoanController | 5+4 | 0 | `approve` `disburse` `clear` · `payments` | **ใหม่** |
| RecurringController | 6 | 0 | `run-now` สร้างเอกสารทันที | **ใหม่** |
| ImportExportController | 8 | 0 | `import` `smart-import/confirm` (JE/เอกสารเป็นชุด) | **ใหม่** |
| WarehouseController · StockTransferController · ProductionConsignmentController | 7+4+8 | 0 | ปรับ/โอนสต๊อก | ใหม่ (E-04 แก้ writer แต่ไม่ได้ใส่ด่าน) |
| WhtCreditController | 6 | 0 | เครดิตภาษีถูกหัก → CIT | **ใหม่** |
| BankController · BankFeedController | 18+5 | 0 | จับคู่/ตัดรายการ/JE ธนาคาร | ซ้ำ SYSTEM_REVIEW §7 "Bank 33" (ยังเปิด) |
| PosController (+FloorPlan/Reservation) | 37+7+5 | 0 | ขาย/คืน/เปิด-ปิดกะ | ซ้ำ §7 "Pos 50" (ยังเปิด) |
| ProductController | 17 | 0 | สินค้า/สต๊อก | ซ้ำ E-07 |
| MobileController | 4 | 0 | approve (G-05) | **ใหม่** |
| PaymentGatewayController · PaymentSettingsController | 6+3 | 0 | ตั้งค่า/คีย์ gateway · intent | **ใหม่** |
| BudgetController · ProjectController | 3+12 | 0 | งบ/โครงการ | ใหม่ (P3) |
| SignatureApprovalController (3 คลาส) | 5 | 0 | อนุมัติ/ตั้งขั้น (G-01) | **ใหม่** |
| ที่มีด่านแล้ว (เทียบ): LeaveController 24 · ExpenseClaim 4 · SalaryAdvance 4 · Metering 11 · Subscription 5 · Lodging 41 | | | | |

- ทำไมพัง: checker เป็น allow-list โดยตั้งใจ (คอมเมนต์ :20-22 บอกให้เพิ่มเมื่อ "แตะเงิน/ภาษี/ข้อมูลพนักงาน") แต่รอบที่ผ่านมาเพิ่มแค่ Metering — ไฟล์ข้างบนทุกไฟล์ผ่าน checker "เขียว" เพราะไม่ถูกมอง (บทเรียน CLAUDE.md เรื่อง MeteringController ซ้ำรอยอีก 20 ไฟล์)
- ผลกระทบ: สมาชิก Viewer/Staff ออก/ยกเลิก 50 ทวิ · เคลียร์/เด้งเช็ค · จำหน่ายสินทรัพย์ · รันเอกสารประจำ · นำเข้าเป็นชุด · จ่ายเงินทดรอง ได้ทั้งหมด = ด่าน `Document.*` ที่ใส่ไว้ใน DocumentController ถูกอ้อมได้จากอย่างน้อย 8 โมดูลข้างเคียงที่ลง JE เอง
- defect class (CLAUDE.md): "checker ที่เป็น allow-list ต้องถามทุกครั้งว่าไฟล์ที่เพิ่งแตะอยู่ในลิสต์ไหม" · `[Authorize]` ≠ สิทธิ์
- ทางแก้ที่เสนอ: (1) เพิ่มทั้ง 20 ไฟล์เข้า `WATCHED` (checker จะฟ้องทันที = backlog ที่มีหลักฐาน) (2) นโยบายจาก SYSTEM_REVIEW Re-design #7: controller ที่แตะเงิน/ภาษี/HR ต้องมี `RequirePermission` ระดับคลาส default แล้ว opt-out รายเมธอด (3) map คีย์ที่มีอยู่แล้ว: `Document.Approve` สำหรับ JE-posting writes · `Pii.View` สำหรับ 50 ทวิ
- ความมั่นใจ: สูง (นับจากไฟล์จริง; ยังไม่ได้ยืนยันว่าทุก write ลง JE — ระบุเฉพาะที่ชื่อ route บอกชัด)

### G-08 [P3][S] Impersonation: `imp_by` ไม่มีใครอ่าน ⇒ `PdpaPiiAccessLog`/AuditLog ระหว่าง session บันทึกเป็น **ผู้ใช้เป้าหมาย (Owner)** ไม่ใช่แอดมิน · และด่าน read-only ดูแค่ HTTP verb
- ไฟล์: `Accounting/Helpers/JwtHelper.cs:61-62` (`imp`, `imp_by`) · `Accounting/Middleware/ImpersonationReadonlyMiddleware.cs:20-27` · `grep -rn "imp_by" Accounting --include=*.cs` = เฉพาะ JwtHelper
- ทำไมพัง: token ใช้ `NameIdentifier = target.Id` (`AdminController.cs:847-848`) → ทุก log ระหว่าง session (PiiAccessLog ตอนดูเลขบัตร/เงินเดือน, AuditMiddleware) ระบุ actor = Owner ของลูกค้า · หลักฐานเดียวว่าเป็นแอดมินคือ AuditLog ตอน mint (`:851-860`) ⇒ ตอบผู้สอบบัญชี/PDPC ไม่ได้ว่า "ระหว่าง 15 นาทีนั้น แอดมินเปิดดูอะไร" · ด่านบล็อก POST/PUT/PATCH/DELETE — GET ที่เขียนข้อมูล (สแกนพบ `LeaveController.cs:122` `admin/types` และ `CmsCommerceConfigController.cs:28` GetConfig สร้าง default row) และ SignalR hub method (`JoinCompanyGroup` เป็น WebSocket ไม่ใช่ POST) หลุดด่าน — ทั้งสองไม่อันตราย แต่พิสูจน์ว่าด่าน "ทุก write" คือ "ทุก verb write"
- ที่ทำดีแล้ว: SystemAdmin only · config gate · 15 นาที · ไม่ใส่ role SystemAdmin ลง token · audit ตอนเริ่ม · target เป็น user จริงในบริษัท ⇒ TenantAccessMiddleware กันข้ามบริษัทได้ (แต่ถ้า Owner เป็นสมาชิกหลายบริษัท token เข้าได้ทุกบริษัทของเขา ไม่ใช่แค่ที่ audit ระบุ)
- ทางแก้ที่เสนอ: `AuditMiddleware`/`PdpaPiiAccessLog` อ่าน `imp_by` แล้วบันทึกเป็น actor จริง + ธง `viaImpersonation` · จำกัด token ด้วย claim `imp_company` แล้วให้ TenantAccessMiddleware เทียบ
- ความมั่นใจ: สูง

### G-09 [P3][S] token สาธารณะเก็บ plaintext + unique index (สลิปเงินเดือน · ใบเสนอราคา · ใบส่งของ · ที่พัก) — DB รั่ว = เปิดสลิปทุกคนได้ 7 วัน
- ไฟล์: `Accounting/Models/Entities/PayslipDelivery.cs:36` `public string Token` · `DatabaseMigrationHelper.cs:4858` `IX_PayslipShareTokens_Token` · `Document.QuotationAcceptToken/DeliverySignToken` · `LodgingService.Reservations.cs:411`
- เหตุผล: ญาติของ SYSTEM_REVIEW F-15 (refresh token plaintext) — `VendorPortalTokens.TokenHash` ทำถูกอยู่แล้วในเรพ ใช้ pattern เดียวกัน (SHA-256 ค้น)
- ความมั่นใจ: สูง · ความรุนแรงต่ำเพราะอายุสั้น (7/30 วัน) และเป็น defence-in-depth

## ตรวจแล้วไม่ใช่บั๊ก (บอกว่าตรวจอะไร เพื่อทีมอื่นไม่เสียเวลาซ้ำ)
- **ExternalApproveQuotationAsync ไม่ใช่ "GET เปลี่ยนสถานะ"** — เป็น `POST` ใต้ `[Authorize]` + route `{companyId}` (TenantGuard ครอบ) และเดิน `ApproveDocumentAsync` แล้ว (ไม่ตั้ง Status ตรง) · ไม่มี token ภายนอกเลย (ชื่อ "External" = ระบบภายนอกที่ถือ JWT ของสมาชิก) — ปัญหาจริงคือสิทธิ์ (G-01)
- **PublicQuotationController / delivery e-sign**: token 32 ไบต์ crypto (`NewToken :205`) · ตรวจรูปแบบ hex 32-80 · หมดอายุ 30 วัน (`DocumentController.cs:135`) · ตัด Draft/Voided · idempotent (ยอมรับซ้ำคืนของเดิม) · เปลี่ยนสถานะด้วย POST เท่านั้น · ล้าง token เมื่อเอกสารเปลี่ยน (`DocumentService.cs:2065`) — ผ่าน
- **PayslipPublicController**: token 32 ไบต์ base64url · 7 วัน · เพิกถอนได้ (ออกใหม่ revoke เก่า `:135-138`) · 404 เงียบ · PDPA log ทุกครั้ง · เลขบัตร mask ผ่าน `PiiMask.CitizenId` ในสายสร้างสลิป (`PayrollService.cs:3983`) · tier rate limit เป็น anonymous 600/นาที/IP (ที่ SYSTEM_REVIEW §7 กังวล) **ไม่เป็นปัญหา**กับ token 256-bit — ปัญหาอยู่ที่รหัสผูก 6 หลัก (G-02) ไม่ใช่ตัว token
- **LINE webhook**: ตรวจ `X-Line-Signature` HMAC-SHA256 + `FixedTimeEquals` ทุกคำขอ (`LineBotService.cs:60-70`) · postback approve มี tenant guard (สมาชิกบริษัทเจ้าของเอกสาร `:590-592`) + `UserLoginPolicy` (`:585`) + เดิน `ApproveDocumentAsync` (ไม่ใช่ B-02 class) · รูป OCR เข้าบริษัทที่ผู้ใช้เลือก/บริษัทเดียว (`:330-347`) ไม่เดา
- **/api/v1 (PublicApiControllerBase)**: ทั้ง 10 endpoint ใน 4 controller เรียก `ResolveCallerAsync(scope, feature)` ครบ (grep นับ = จำนวน endpoint) · scope wildcard ถูก · คีย์ไม่มี Scopes ถูกกัน · แคช verify เก็บเฉพาะผลผ่าน + TTL 5 นาที + Revoked/Expired ตรวจ DB ทุกครั้ง · `ApiKeyScopeFilter` (F-05) มีจริงและอ่าน `IsApiKeyAuth` · TenantAccessMiddleware บล็อก API key ข้าม companyId (`:52-64`) — ที่เหลือ (IP fail-open F-17 · rate limit static F-10) เป็นของเดิม
- **CMS storefront anonymous**: ทุก endpoint ที่สุ่ม (storefront 10 · commerce 13 · ext 9 · booking 5 · customer 3 · content 4 · site 2 · lead 1) ส่ง `companyId + siteId` เข้า service และ `GetPageBySlugAsync` กรอง `Status == Published` (`CmsContentService.cs:192`) · RichText/Html block ผ่าน `CmsHtmlSanitizer` allowlist (`CmsRenderingService.cs:384-392, 473-478`) · `CmsRbacMiddleware` มี call site จริง (`[RequireSiteRole]` 47 จุด) ตรวจ `SiteStaffAccesses` และปล่อย Owner · cart/order/slip ที่ anonymous ใช้ GUID เป็น capability (ยอมรับได้ ไม่ใช่ IDOR แบบเดา) · `CmsWebhookController` ตรวจ `X-Webhook-Signature` ต่อ gateway ของ site
- **cms-edit.html**: innerHTML 65 จุด · ที่ไม่ผ่าน esc มี 3 จุด แต่ค่าเป็น `site.subdomain` (slug) และ `BLOCK_TYPES` คงที่ — ไม่ใช่ข้อมูลอิสระ
- **File access**: `/uploads/ocr`, `/uploads/attachments`, `/uploads/lodging-slips` ไม่อยู่ใน `publicUploadPrefixes` (`Program.cs:934-955`) และเสิร์ฟผ่าน endpoint ที่ scope `companyId` (`FileAttachmentController.cs:96-116 GetByIdAsync(companyId, attachmentId)`) · ชื่อไฟล์ที่เขียนลงดิสก์ทุกจุดที่ grep เป็น `Guid.NewGuid()` (OcrController:140 · OcrV1:104 · ImageProcessingService:194 · LineBot:430) · `templates/{entityType}/download` ไม่ประกอบ path (เลือกจาก switch) — ไม่พบ path traversal
- **Impersonation** ส่วนที่ทำถูก — ดู G-08
- **LodgingPublicController** token 128-bit hex scope `companyId+siteId` ทุกเมธอด (`:75-127`) — ผ่าน (สลิปย้ายมาเสิร์ฟผ่าน endpoint แล้วตาม b63dfd8)

## ซ้ำกับ SYSTEM_REVIEW / ERP_REVIEW (ID เดิม + ยังเปิดอยู่จริงไหม)
- **B-03** `SignatureApprovalService.cs:294 approval.Document.Status = DocumentStatus.Rejected` — **ยังเปิด** (เห็นในโค้ด HEAD) และเป็น Document.Status writer นอก `IDocumentService` (ราก §2 ของ ERP_REVIEW) — ควรเด้ง Draft แบบ MobileApiService
- **B-02** mobile approve — ปิดแล้ว (Status writer) แต่ด่านสิทธิ์ยังไม่มี → G-05
- **E-07/E-08** ProductController/SmeOperations ไม่มีด่าน — ยังเปิด (ProductController permRefs=0) · อยู่ในตาราง G-07
- **SYSTEM_REVIEW §7 "Bank 33 · Pos 50 ยังสมาชิกคนไหนก็ทำได้"** — ยังเปิด (Bank 18 write/0 perm · Pos 37/0) · **§7 PayslipPublicController tier** — ตรวจแล้วไม่เป็นปัญหา (ดูข้างบน) ควรถอด
- **F-13** ลายเซ็น — ปิดแล้ว (เห็นคอมเมนต์ ★ F-13 ที่ `:211-220`)
- **F-15** refresh token plaintext — ยังเปิด; G-09 เป็นญาติ
- **F-17** ApiKey IP fail-open — ยังเปิด (`ApiKeyMiddleware.cs:124-131` `clientIp != null &&`)
- **F-03** upload html/svg เป็น stored XSS บน origin เดียว — เห็นว่า `ImageProcessingService` มี `SaveRawAsync` อยู่ ยังไม่ได้ยืนยันว่าจำกัดนามสกุลแล้ว (ไม่ได้อ่านลึก)

## ยังไม่ได้อ่าน
- `ApprovalRequests.Add` call site (ตัดสินผลกระทบจริงของ G-05) · `ApprovalService` ตัวที่ ERP_REVIEW เรียกว่า "bounce"
- `CmsHtmlSanitizer` เนื้อใน (allowlist ครอบ `style`/`href=javascript:`/svg ไหม) · `theme.CustomCss` ฉีดดิบที่ `CmsRenderingService.cs:296-299` (CSS injection — exfil ผ่าน `url()` จำกัด)
- storefront.html ฝั่ง client `renderBlock(b)` เป็น renderer ตัวที่ 2 ของบล็อก (drift class) — ไม่ได้เทียบทีละบล็อก
- 11 controller ที่ไม่มี `{companyId}` (AccountSubscription · AccountantWorkspace · Company · DbdLookup · GovernmentService · HelpCenter · Me · Mobile · Notification · Subscription · LineWebhook) ทีละเมธอดกับ shopper-JWT (G-06)
- `VendorPortalController` (`api/portal/summary|invoice` token-hash) · `PublicChatController` · `PublicReservationController` · `ContactInquiryController` (anti-spam) · `DeepLinkController`
- ไม่ได้ยิงจริง — ทุกข้อเป็นการอ่านโค้ด

## ข้อเสนอเชิง ERP (สิ่งที่ขาดถ้าจะเป็น ERP ครบ — แยกจากบั๊ก)
1. **Approval engine ตัวเดียว** — วันนี้มี 3 ตัวที่ตัดสิน "ใครอนุมัติได้" คนละกติกา: `DocumentPermissionHelper` (เว็บ) · `SignatureApprovalService/DocumentApproval` (ลายเซ็น) · `ApprovalRequest/ApprovalStep` (มือถือ) + role list ใน LINE — ERP ต้องมี policy engine เดียว (matrix: ชนิดเอกสาร × วงเงิน × ขั้น × ผู้อนุมัติ) ที่ทุกช่องทาง (เว็บ/มือถือ/LINE/API/e-sign) เรียกผ่านเมธอดเดียว และ `Document.Status` เขียนได้ที่เดียว
2. **Authorization ระดับแพลตฟอร์ม**: ย้ายจาก "จำใส่ RequirePermission ทีละเมธอด" เป็น policy-by-default (controller/verb → permission key) + checker เป็น deny-list (ทุก controller ถูกมอง ยกเว้นที่ประกาศ `[PublicSurface]`) — allow-list พิสูจน์แล้วว่าตามไม่ทัน (G-07)
3. **Identity แยกชนิด**: ผู้ใช้ระบบ · ลูกค้าหน้าร้าน · ลูกค้า ERP portal · ผู้ขาย portal · พนักงาน (สลิป) · แอดมินแพลตฟอร์ม · API key · impersonation — แต่ละชนิดต้องมี scheme/claim `typ` และ policy ของตัวเอง (G-06, G-08) ไม่ใช่ JWT ก้อนเดียว
4. **Verification code service กลาง** (6 หลัก + attempt counter + scope + TTL) ใช้ร่วม LINE bind/สลิป/SSO/OTP — แทนการเขียน `RandomNumberGenerator.GetInt32(0,1_000_000)` ซ้ำ 2 ที่ (G-02)
5. **Origin แยกสำหรับเนื้อหาผู้เช่า** (storefront/uploads) — SYSTEM_REVIEW Re-design #3 ยังเป็นเรื่องใหญ่สุดเชิงโครงสร้าง แม้ sanitizer จะครอบวันนี้
