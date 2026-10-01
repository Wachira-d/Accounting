# รอบ 200 · ทีม RF — แก้ผลฝ่ายค้านทีม R (`review200-R.md`)

> ขอบเขต: R200-X1 … X8 ทุกข้อ (CONFIRMED 4 + PLAUSIBLE 4 — PLAUSIBLE ทุกข้อ verify แล้วว่าจริงก่อนแก้) · worktree ของทีม (ไม่ push) ·
> **ยังไม่ได้คอมไพล์/รันเทสต์** (ไม่มี .NET SDK) — CI เป็นตัวแรก · checker ที่รัน = ท้ายไฟล์

## 1. ตารางรายการ

| ID | ระดับ | สถานะ | แก้ที่ (file:จุด) | เทสต์ / ด่าน |
|---|---|---|---|---|
| **R200-X1** | P1 security | ✅ | `Helpers/DocumentTemplateStyle.cs` (ใหม่ — ตัวตรวจค่าหน้าตาตัวเดียว) · `PdfGenerationService.cs` `BuildDocumentHtml` หัวเอกสาร `WebUtility.HtmlEncode(ComputeDocumentTitle(…))` · `BuildCss`/`BuildLayoutCss` (internal static · ทุกค่าผ่านตัวตรวจ) · `SanitizeHex` → `DocumentTemplateStyle.Hex` · `NormalizeFont` รายการอนุญาต · `refLabel` ประมาณการ JE หนี · `PdfGenerationService.DocumentRenderer.cs` `ResolvePageSize` + ขนาดเนื้อความ · `DocumentTemplateController.cs` `StyleRejection` (400 ไทย) + `[RequirePermission(CompanySettings.Edit)]` 5 เส้น | `TeamRFRound200Tests.X1_*` (7) · `write_permission_gate_check` เฝ้า `DocumentTemplateController` (+ `READ_ONLY_POSTS_IN_FILE` สำหรับ generate/preview) · `required_call_site_check` 9 แถว + ชนิดกติกาใหม่ `forbid_lit` |
| **R200-X2** | P2 กฎเหล็ก #1 | ✅ | `Helpers/AnomalyExplainStudent.cs` (ใหม่ — `ReadPrompt` อ่าน `anomaly.amount`/`mad_z_score`/`typical_range` · `vendor_history_12mo` ทั้งแบบชุดยอดและแบบสรุป · `recent_12mo` · `local_model.pick`; `Decide` ลำดับหลักฐาน z → ช่วงประวัติ → กติการะดับ → NeedReview) · `AnomalyExplanationDistillationModel.PredictAsync` ตอบค่าในชุด + `StructuredJson` · `AiSuggestionController.ExplainAnomaly` ใช้คำอธิบายนักเรียนเมื่อ `!UsedAi && FromLocalModel` | `X2_นักเรียนอ่าน_payload_*` · `X2_KillSwitch_*` (Theory 2 รูป payload — ผ่าน `PredictAsync` จริง → `ShouldPersist(false,true,…)` → `ReadStructured`) · `X2_ColdStart_*` · `X2_ทิศตรงข้าม_*` |
| **R200-X8** | P3 | ✅ | `Helpers/AnomalyExplainVerdict.cs` `Coerce` (ตรงชุด / ต่างแค่ `_ - ช่องว่าง` ⇒ ค่าในชุด · อื่น ๆ ⇒ NeedReview · ว่าง ⇒ ไม่บันทึก) + `IsRecognized` (ความมั่นใจว่างเมื่อถูกแปลง) · controller เก็บเสมอเมื่อมีผู้ตอบ ⇒ ไม่ยิงครูซ้ำ | `X8_*` (Theory 5) · `AnomalyExplainVerdictTests.คำตอบว่าง_ห้ามเขียน` (ปรับจากเดิมที่ล็อก "นอกชุด = ทิ้ง") |
| **R200-X3** | P2 | ✅ | `ApprovalRuleValidation.PatchDescription/PatchProjectId` · `ApprovalService.UpdateRuleAsync` · `ValidateRuleAsync` ตรวจโครงการเป็นของบริษัท · `CreateApprovalRuleRequest(+ClearProjectId=false)` · `approval.html` ช่องคำอธิบาย + dropdown โครงการ (โครงการเดิมที่ไม่อยู่ในรายการยังถูกเลือกไว้) · hydrate/reset/payload | `X3_*` (สองทิศ) · `tools/approval_rule_form_sim.js` (รันเมธอดจริงของ `Page` บน DOM จำลอง · 5 สัญญา · baseline `git show 4a9ebd5a:…approval.html` ล้ม 5 ข้อ · negative test ในตัว 3/3) |
| **R200-X4** | P3 | ✅ | `DepositKindDocumentRules.ShouldClearStalePaymentDate` = `paymentDate != null && !DocumentStatusRules.IsIssued(status)` · `DocumentService.IssueForfeitTaxInvoiceAsync` | `X4_*` (Draft/WaitingApproval/Rejected ⇒ ล้าง · Approved/Paid/ไม่มีวันรับเงิน ⇒ ไม่แตะ) · call-site forbid `draft.Status == DocumentStatus.Draft` |
| **R200-X5** | P3 | ✅ | `OwnerActionGuard.NotOwner` (403 · `OWNER-ONLY`) — ใช้ทั้ง `RolePermissionService.EnsureOwnerAccessAsync` **และ** `CompanyService.EnsureOwnerAccessAsync` (คลาสเดียวกัน 17 ผู้เรียก: api-keys · webhook · ปิดงวด/ปิดปี · สมาชิก ฯลฯ — ทุกตัวเด้งออกจากระบบแบบเดียวกัน) · `roles.html` ปุ่ม "สร้าง Role เริ่มต้น" ขึ้นเฉพาะหลังผ่านด่านบทบาทจาก server (`ensureMyRole`) | `X5_*` · call-site 2 แถว (forbid `new UnauthorizedAccessException(`) · `owner_action_wiring_check` ยังเขียว (EnsureNotApiKey คงเป็นคำสั่งแรก) |
| **R200-X6** | P3 | ✅ | `SsoInsuredNumber.ForDisplay` (Resolve แล้วค่อย `PiiMask`) · `PayrollService.GenerateSsoReportAsync` | `X6_*` (สองทิศ) · call-site ปรับแถว G2-05 (call_args `includePii`) |
| **R200-X7** | P3 | ✅ | `Math.Round` ไม่ระบุ midpoint ในโมดูลเงินเดือน **11 จุด** (ฝ่ายค้านชี้ 4): `PayrollService` 1215 · 2755 · 2911 · 2940 · 3442 · `PayrollController` 748 · 764 · 834 · `SsoRateSchedule.GetMaxContribution` · `TaxFilingExportService` 585 · 586 (Excel สปส.) · แก้ข้อความ D-08 ใน `team-R.md` | checker ใหม่ `tools/payroll_rounding_check.py` (20 ไฟล์ · negative test ในตัว) — ตอนนี้ 0 จุด |

## 2. รายละเอียดที่ต้องรู้

### X1 — ไล่ `PdfGenerationService*` ทั้งไฟล์ (ผลกวาด)
สแกนทุก `$"…{x}…"` ที่มี `<` ในทุก partial (`.cs` · `.DocumentRenderer` · `.HtmlRenderer` · `.PdfA3*` · `.Payslip` · `.TaxReport` · `.WhtCert`):
- **ดิบที่แก้**: หัวเอกสาร (`:1991` เดิม) · ทุกค่าใน `BuildCss`/`BuildLayoutCss` · `refLabel` ของ JE ที่เป็นประมาณการ (ค่าจากระบบ แต่หนีกันไว้)
- **ปลอดภัยอยู่แล้ว**: ชื่อ/ที่อยู่/สาขา/โทร/อีเมล/เว็บ/หัวกล่องคู่ค้า/หมายเหตุ/เงื่อนไข/ท้ายกระดาษ/ลายเซ็น/50 ทวิ/ใบเสร็จ = `HtmlEncode` · ป้ายทั้งหมดจาก `DocumentLabels`
  หรือค่าคงที่ · ตัวเลข `:N2` · `LayoutStyle` ผ่าน `SanitizeLayout` (รายการปิด) · `CopyLabelPosition` เทียบค่าคงที่ · โลโก้/ตรา/ลายเซ็น `HtmlImageSource` · XMP ของ PDF/A-3 `XmlEscape` ·
  `.Payslip/.TaxReport/.WhtCert/.HtmlRenderer` เป็น QuestPDF (ไม่ใช่ HTML)
- **ทางเข้าอื่นของหัวเอกสาร**: อีเมล (`DocumentEmailService.ComposeDefaultTemplate` หนีแล้ว) · อีเมลตั้งเวลา (`Render(..., htmlEncodeValues: true)`) · LINE (ข้อความล้วน)
- **ค่าเก่าที่เก็บไว้** (F3 ข้อ 10): ไม่ต้อง migration — ฝั่ง render แปลงค่าไม่ถูกรูปเป็นค่าเริ่มต้นของ entity ทุกครั้ง (ไม่ล้ม · ไม่มีแท็กหลุด) · แก้เทมเพลตเก่าแล้วบันทึก = ต้องแก้ช่องที่ผิดก่อน (400 บอกช่อง)
- **ทิศตรงข้าม**: สีจาก `<input type=color>` (`#rrggbb` ตัวเล็ก) ผ่าน · `#RGB` สั้นผ่าน (ขยายเป็น 6 หลัก — QuestPDF เดิมทิ้งค่านี้ ตอนนี้ใช้สีจริง = ตรงกับ HTML มากขึ้น) ·
  ฟอนต์ 4 ตัวของหน้าแก้เทมเพลต · ขนาดในช่วง input ของหน้า · หัวเอกสารภาษาไทยปกติไม่มีอักขระพิเศษ ⇒ หน้าตาเท่าเดิม (`X1_BuildCss_เทมเพลตปกติ_*`)
- **drift ที่พบแต่ไม่แตะ (คำถามค้าง Q1)**: QuestPDF ใช้ `brand.PrimaryColor` (แบรนด์เอกสาร) ทับสีเทมเพลต (`BuildBranding:3244`) แต่ HTML `BuildCss` ไม่รู้จักแบรนด์ ⇒ PDF จาก
  Chromium กับ PDF สำรอง (QuestPDF/e-Tax) สีหัวคนละสีเมื่อใบผูกแบรนด์ที่ตั้งสี · มีมาก่อนรอบนี้ ต้องตัดสินว่าแบรนด์ควรชนะใน HTML ด้วยไหม
- **สิทธิ์**: `CompanySettings.Edit` (คำอธิบายคีย์ครอบ "template" อยู่แล้ว · DECISIONS ข้อ 34 ~~เจ้าของ + Admin โดยปริยาย~~ → **แก้ถ้อยคำรอบ 200 ทีม Z (RF-3): เจ้าของโดยปริยาย (ระบบไม่มีบทบาท Admin ของบริษัท) · บทบาทอื่นให้สิทธิ์ `CompanySettings.Edit` ผ่านหน้าบทบาท**) · ผู้ไม่มีสิทธิ์ได้ 403 + `PermissionKeys.DeniedMessage` ·
  หน้าแก้เทมเพลตยังไม่ซ่อนปุ่มบันทึกตามสิทธิ์ (ได้ 403 toast — ทิศที่มองเห็น · Q2)

### X2 — kill-switch ผ่านจริงยังไง
ปิด provider ⇒ `AiOrchestrator` → `FallbackToLocal(…, fromLocalModel: localPred != null)` · `PrimaryAnswer` = คำตอบนักเรียน (request ถูกแทนด้วย `localPred` ที่ต้นเมธอด) ·
`RawResponseJson` = `LocalRawJson` = `StructuredJson` ของนักเรียน ⇒ controller: `ShouldPersist(false, true, "LikelyError|…")` = true · คำอธิบาย = `ReadStructured(RawResponseJson)`
(ไม่ใช่ข้อความ routing) · บันทึก `AiReasoning` ⇒ เปิดหน้าใหม่ได้คำตอบแคช ป้าย `usedAi=false` (แถว feedback `AiPrimaryAnswer = null`)
- cold-start: tenant ใหม่/รายการไม่ใช่ยอดเงิน (amount 0) ⇒ กติการะดับของผู้เรียก (`Critical ⇒ LikelyError`) หรือ `NeedReview` — ตอบเสมอ
- ความมั่นใจ: z ห่างเกณฑ์ 3.5 มาก ⇒ ≥ 0.85 (Hybrid short-circuit) · ใกล้เกณฑ์/ช่วงประวัติ/กติกา ⇒ 0.50–0.75 (ครูถูกถามเมื่อเปิดอยู่ = student-first ตามกฎเหล็ก #1)
- ข้อสังเกต: เส้น ad-hoc `POST anomaly/explain` ไม่บันทึกลงรายการ (ตามออกแบบ) แต่ได้นักเรียนตัวเดียวกันแล้ว

### X3 — ทำไมต้องมี `clearProjectId`
`Guid?` รับ `""` ไม่ได้ (System.Text.Json โยน body ทั้งก้อน — defect class `blank_number_null_check`) ⇒ "ไม่มีคีย์/null = คงเดิม" ต้องคู่กับธงล้างแยก ·
ส่ง `projectId` + `clearProjectId` พร้อมกัน ⇒ ใช้ `projectId` (ทิศที่ไม่ขยายขอบเขตกฎ) · หน้าเว็บส่ง `clearProjectId: true` เฉพาะเมื่อกฎเดิมมีโครงการแล้วผู้ใช้เลือก "ทุกโครงการ"

### X5 — ทำไมแก้ `CompanyService` ด้วย
F2 ข้อ 1 (grep รูปแบบเดิมทั้งเรพ): `throw new UnauthorizedAccessException("ต้องเป็น Owner เท่านั้น")` มี 2 จุด (RolePermissionService · CompanyService) — คลาสเดียวกัน
(ผู้ไม่ใช่เจ้าของกดปุ่มระดับเจ้าของ ⇒ 401 ⇒ `api.js:399` ลบ token เด้งไป login) · ตอนนี้ 0 จุด · ไม่มีผู้เรียกที่ `catch (UnauthorizedAccessException)` จากด่านนี้
(3 จุดในเรพเป็นการเขียนไฟล์ uploads)

## 3. ตอบ F3 ข้อ 7–12
- **7 (grep)**: หัวเอกสารดิบเข้า HTML 0 จุด · `{t.X}` ดิบใน `BuildCss`/`BuildLayoutCss` 0 จุด (ล็อกด้วย `forbid_lit`) · `UnauthorizedAccessException("ต้องเป็น Owner` 0 จุด ·
  `Math.Round` ไม่ระบุ midpoint ในโมดูลเงินเดือน 0 จุด · `root.amount` ในนักเรียน 0 จุด · `rule.Description = request.Description` 0 จุด
- **8 (ทางเข้าอื่น)**: เทมเพลต — เส้นเขียนมีแค่ `DocumentTemplateService` (ผู้เรียก = controller นี้เท่านั้น · `callers.py`) · พรีวิวร่าง (`preview-html-draft`) ไม่บันทึกแต่ render ผ่านตัวตรวจเดียวกัน ·
  หัวเอกสารจาก `DocumentTitleOverridesJson` เข้าทาง `SettingsController` (มี `CompanySettings.Edit` อยู่แล้ว) และหนีตอน render · กฎอนุมัติ — `sme-config.html` สร้างอย่างเดียว (ไม่กระทบ)
- **9 (เข้มขึ้น)**: บันทึกเทมเพลตค่าผิด ⇒ 400 บอกช่อง (ทางไปต่อ: เลือกจากจานสี/รายการ) · ผู้ไม่มีสิทธิ์ ⇒ 403 บอกคีย์ · เลือกโครงการของบริษัทอื่น ⇒ 400 ·
  เทสต์ทิศตรงข้าม: `X1_ค่าปกติจากหน้าแก้เทมเพลต_บันทึกผ่าน` · `X1_BuildCss_เทมเพลตปกติ_*` · `X3_ทิศตรงข้าม_*` · `X4_ทิศตรงข้าม_*` · `X6_ทิศตรงข้าม_*` · `X8_ทิศตรงข้าม_*`
- **10 (ค่าที่ persist)**: สี/ฟอนต์เก่าที่ผิด ⇒ render เป็นค่าปลอดภัยทุกครั้ง (ไม่ต้อง migration) · กฎอนุมัติที่ถูกล้างโครงการไปแล้วจากหน้าเดิม **กู้ไม่ได้อัตโนมัติ** (ไม่มีประวัติค่าเดิม — Q3) ·
  ใบริบที่ค้างสถานะรออนุมัติจะถูกแก้ตอนริบซ้ำ (idempotent เดิม)
- **11 (ฝ่ายค้าน)**: ทีม RF คือรอบแก้ของฝ่ายค้านแล้ว — แนะนำส่ง diff นี้ให้ฝ่ายค้านอีกรอบ (security X1 + สิทธิ์ X5)
- **12 (doc)**: DOCUMENT_FLOW §6 PDF (bullet ใหม่) · มัดจำริบ (P4-1) · เงินเดือน (X6/X7) · ตาราง distillation (X2/X8) · บล็อก Last verified · TEST_PLAN §0 + R200-RF-01..11 · CHANGELOG

## 4. ความเสี่ยงคอมไพล์ (ไม่มี .NET SDK)
1. `DocumentTemplateStyle` — `hCount = c.HasValue ? (int)c.Value : null;` ใน `AnomalyExplainStudent` พึ่ง target-typed conditional (C# 9+ · net8 = C# 12 ⇒ ผ่าน)
2. `lo is > 0m` บน `decimal?` (relational pattern กับ Nullable — ผ่านตั้งแต่ C# 9)
3. `studentText?.Reasoning` บน `Nullable<ValueTuple<…>>` + `?? resp.SuggestedActions` (ชนิด `IReadOnlyList<string>` ทั้งสองฝั่ง)
4. `throw Accounting.Helpers.OwnerActionGuard.NotOwner(...)` (throw expression ของ method call คืน `BusinessRuleException`) · target-typed `new(...)` เลือก ctor `(string, string?, int)`
5. `BuildCss`/`BuildLayoutCss` เปลี่ยน `private` → `internal` (เทสต์เรียก · มี `InternalsVisibleTo`) · local function `Mm` จับ `inv` ที่ประกาศก่อน
6. `CreateApprovalRuleRequest` เพิ่มพารามิเตอร์ท้าย `bool ClearProjectId = false` — ไม่มีผู้สร้างแบบ positional ในเรพ (`record_arg_check` 0)
7. เทสต์ `new DocumentTemplate { … }` ใน namespace `Accounting.Tests` พร้อม `using Accounting.Models.Entities` (รูปเดียวกับ `DocumentChannelHeadingTests`)

## 5. คำถามค้าง
- ✅ 23d7a6de (รอบ 201 PL · A-PL6 `Helpers/DocumentBrandColor`) **Q1** แบรนด์เอกสารที่ตั้งสี: HTML ควรใช้สีแบรนด์แทนสีเทมเพลตเหมือน QuestPDF ไหม (drift เดิม — ไม่แตะรอบนี้)
- **Q2** หน้า `document-templates.html` ควรซ่อน/ล็อกปุ่มบันทึกสำหรับผู้ไม่มี `CompanySettings.Edit` ไหม (ตอนนี้ได้ 403 toast)
- **Q3** กฎอนุมัติที่ถูกล้างโครงการเงียบ ๆ จากหน้าเดิม (ช่วงทีม R → RF) — ไม่มีหลักฐานค่าเดิมให้กู้ · ถ้าต้องตามหา ต้องดู audit log ของ `ApprovalRules` (ถ้ามี)

## 6. checker ที่รัน (worktree นี้ · หลังแก้ · ผลรัน)
| checker | ผล |
|---|---|
| `required_call_site_check` | ✅ 515 กติกา ผ่าน (+ self-test กลายพันธุ์ทุกชนิดรวม `forbid_lit` ใหม่ · 14 เคสฝ่ายค้าน) |
| `write_permission_gate_check` | ✅ (เฝ้า `DocumentTemplateController` แล้ว · negative test มือ: ถอด `[RequirePermission]` ของ Duplicate ⇒ ฟ้อง 1 จุด) |
| `html_attr_escape_check` · `escape_helper_check` | ✅ 0 จุด · 0 จุด |
| `record_arg_check` · `nullable_arg_check` · `using_check` | ✅ 0 · 0 · 0 |
| `dead_helper_check` | ✅ ไม่มีตัวใหม่ (รอบแรกฟ้อง `DocumentTemplateStyle.KnownFont` ⇒ ทำเป็น private พร้อม `FontSize`) |
| `owner_action_wiring_check` | ✅ 99 จุด |
| `payroll_rounding_check` (ใหม่) | ✅ 20 ไฟล์ · 0 จุด |
| `string_quote_close_check` · `comment_line_break_check` · `identifier_space_check` · `verbatim_string_check` · `tuple_name_merge_check` · `namespace_shadow_check` · `accessibility_check` · `dto_nullable_contract_check` · `service_interface_check` · `arg_type_check` · `ai_feedback_source_check` · `localstorage_key_check` · `onclick_js_string_check` · `enum_number_compare_check` | ✅ ทั้งหมด 0 |
| `test_inventory --check` · `doc_commit_sha_check` | ✅ · ✅ |
| `node tools/approval_rule_form_sim.js` (ใหม่) | ✅ 5 สัญญา · negative test 3/3 · baseline `4a9ebd5a` ล้ม 5 ข้อ |
| `node --check` `<script>` ของ `approval.html` · `roles.html` | ✅ |
| brace balance ทุก `.cs` ที่แก้ | ✅ (raw = 0 ทุกไฟล์ต้นฉบับ · ตัวนับแบบตัดสตริงต่างเฉพาะไฟล์ที่มี `{`/`}` ในสตริง — ไม่ใช่ "ทั้งสองตัวฟ้อง" ตามกติกา check_all) |
| `bash tools/check_all.sh` ทั้งชุด | ไม่ได้รันทั้งชุด (เครื่องโหลดหนัก — checker 1 ตัวใช้ ~10 นาที) · รันรายตัวตามตารางนี้แทน |
| `dotnet build/test` | ❌ ไม่มี SDK — **ยังไม่ได้คอมไพล์** |
