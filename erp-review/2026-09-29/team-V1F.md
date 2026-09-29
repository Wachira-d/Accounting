# รอบ 200 · ทีม V1F — แก้ผลฝ่ายค้านทีม V1 (ยกเลิกและออกใบแทน · เช็คเด้ง/e-Tax)

> แหล่ง: `review200-V1.md` (ฝ่ายค้าน · ตารางสถานะอยู่ท้ายไฟล์นั้น) · คำตัดสิน DECISIONS ข้อ 9, 11 · CLAUDE.md กฎเหล็ก #2 A/C/F ·
> **ยังไม่ได้คอมไพล์ในเครื่องนี้ (ไม่มี .NET SDK) — รบกวน rebuild + `dotnet test` ฝั่ง CI/ผู้ใช้**
>
> worktree ถูกสร้างจาก `69fd88e8` (master เก่า) ⇒ `reset --hard origin/claude/erp-system-review-team-660mev` (`4a9ebd5a`) ก่อนเริ่ม ·
> คัดลอก `review200-V1.md` จาก working tree หลักเข้ามาที่ path เดียวกัน

## ตารางรายการ

| ข้อ | ระดับ | สถานะ | ที่แก้ (file:method) | เทสต์ |
|---|---|---|---|---|
| **V1-R1** ด่านรายงานล็อก/งวดปิดของออกใบแทนดูแค่ใบขาย | P1 | ✅ | `Helpers/SettlementPaidReissue.cs` `Decide` (ลูปใบเสร็จ: `FilingLocked` · `ClosedPeriodName`) · `ReissueReceiptFact` +2 ช่อง (ไม่มีค่าเริ่มต้น) · `DocumentService.Reissue.cs` `EvaluateSettlementPaidReissueAsync` (`IsDocumentFilingLockedAsync(r.Id)` · `ClosedPeriodNameAsync(r.DocumentDate)`) · `ClosedPeriodNameAsync` (ตัวเดียวของใบขาย+ใบเสร็จ) | `R200_V1F_R1_ใบเสร็จอัตโนมัติอยู่ในรายงานภาษีที่ล็อกแล้ว…` · `…งวดบัญชีของวันที่ใบเสร็จปิดแล้ว…` · `…ทิศตรงข้าม…ออกใบแทนได้เหมือนเดิม` |
| **V1-R2** เช็คเด้งถอยภาษีขายของใบเสร็จที่ติดธงเงียบ ๆ | P1 | ✅ | `Helpers/DocumentVoidPreconditions.cs` `ShouldUndoOutputVatReclass` · `FlaggedReceiptKeepsTaxPoint` · `DocumentService.cs` `ReversePaymentInternalAsync` (ข้าม `TryUndoUndueOutputVatReclassAsync` เมื่อใบเสร็จถือ VAT ติดธง หรือใบเสร็จถือ VAT อื่นยังมีผล — `LiveVatReceiptExistsAsync`) · ผลตามที่ต้องแก้คู่กัน: `Helpers/SettlementReceiptPolicy.CarriesTaxInvoiceRole(…, liveVatReceiptExists)` ที่ `CreatePaymentAsync` + `IssueReceiptForPaymentAsync` (รับชำระใหม่หลังเช็คเด้งไม่ออกใบกำกับใบที่สอง = ไม่นับ VAT ซ้ำ) | `R200_V1F_R2_เช็คเด้ง_ใบเสร็จถือVAT70ติดธง_ห้ามถอยภาษีขาย…` · `…ทิศตรงข้าม…พฤติกรรมเดิม` · `…รับชำระใหม่หลังเช็คเด้ง…ใบรับเปล่า…` · `SettlementReceiptPolicyTests` (ปรับ 4) |
| **V1-R3** ธง "ต้องยกเลิกทาง e-Tax" ไม่มีทางปิด | P2 | ✅ | `DocumentVoidPreconditions.EtaxCancellationResolution` (pure · `EtaxCancellationEvidence`) · `DocumentService.Reissue.cs` `ResolveEtaxCancellationAsync` · `DocumentController.ResolveEtaxCancellation` `POST {id}/etax-cancellation` (สิทธิ์ยกเลิก) · `IDocumentService` · `api.js resolveEtaxCancellation` · ปุ่มบนแถบธง `documents.html` | `R200_V1F_R3_ปิดธงได้เมื่อมีหลักฐาน…` · `R200_V1F_R3_ไม่มีหลักฐาน_ปฏิเสธ…` (Theory 5) |
| **V1-R4** e-Tax ใบขาย Submitted ถูกพลิก Voided | P2 | ✅ | `SettlementPaidReissueFacts.DocumentEtax` (แทน `EtaxAccepted`) · `Decide` `REISSUE-ETAX-SUBMITTED` · ขั้นยกเลิก e-Tax ใบเดิมกรอง `EtaxReachedRdStatuses` (forbid สูตรเดิม `e.Status != EtaxStatus.Accepted`) | `R200_V1F_R4_eTaxใบขายส่งแล้วยังไม่รู้ผล_บล็อก…` · theory เดิม `"etax"` ปรับ |
| **V1-R5** integration ยกเลิกใบที่ถูกแทน ⇒ 200 โกหก | P2 | ✅ | `SettlementPaidReissue.IntegrationVoid` + `IntegrationVoidKind` · `IntegrationService.VoidDocumentByExternalRefAsync` (ตามสาย `ReplacedByDocumentId` ≤10 ชั้น · `INTEGRATION-VOID-REPLACED` ผ่าน `HandleSyncError` = success:false · ค้น ExternalRef: ใบที่ยังมีผลก่อน แล้วใหม่สุด) | `R200_V1F_R5_…บอกเลขใบแทน` |
| **V1-R6** ใบแทนข้าม SoD/วงเงิน (นโยบาย ตัดสินแล้ว) | P2 | ✅ | `Helpers/ApprovalControlPolicy.cs` (ใหม่): `NeedsSignatureFlow` · `SelfApprovalBlocked` (ใช้แทน inline ใน `ApproveDocumentAsync` แล้ว) · `ForReissue` · คำขอบนใบเดิม `Documents.ReissueRequestedAt/By/Json` (`DatabaseMigrationHelper` ADD COLUMN · echo `DocumentResponse.ReissueRequestedAt/By/Reason`) · `CancelReissueRequestAsync` + `DELETE {id}/reissue-settlement-paid/request` · `ReissueSettlementPaidRequest.ConfirmPendingRequest` · แถบ "รอยืนยัน" + ปุ่มยืนยัน/ยกเลิกคำขอ | `R200_V1F_R6_SoDเปิด…` · `…วงเงินเซ็นหลายขั้น…` · `…ตัวตัดสินเดียวกับApproveDocumentAsync…` · `…คำขอที่บันทึกไว้อ่านกลับได้ครบ…` |
| **V1-R7** `CopyScalars` พาหลักฐานของใบเดิม | P3 | ✅ | allowlist `DocumentCarriedFields`/`DocumentNotCarriedFields`/`LineCarriedFields`/`LineNotCarriedFields` (`nameof` — คอมไพเลอร์จับชื่อผิด) · `CopyDocumentForReissue`/`CopyLineForReissue` · ลบ `CopyScalars` · สถานะใบใหม่ `ReplacementStatus` | `R200_V1F_R7_ทุกช่องค่า…ถูกจัดกลุ่ม…ครบ_ไม่ซ้ำ` (ช่องใหม่ใน entity ⇒ เทสต์ล้มพร้อมชื่อ) · `…ลายเซ็นรับของ_ผลตรวจRD…ไม่ตามไป…` · เทสต์เดิม `โคลน…` ปรับ |
| **V1-R8** `ForbiddenChanges` ตรวจผลสูตรตัวเอง | P3 | ✅ | `RequestLineIssues` (ด่านของคำขอผู้ใช้ — บรรทัดที่ไม่มี/คำบรรยายว่าง = 400 `REISSUE-LINE-INVALID`) · `ForbiddenChanges` เปลี่ยนบทบาทเป็นตาข่ายของตัวคัดลอก allowlist (ตอนนี้มีความหมาย — ช่องเงินหล่น = ฟ้อง) · `StripReplacementNote` + ภาพย่อเทียบหมายเหตุของผู้ใช้ (ไม่รวมบรรทัดที่ระบบต่อ) | `R200_V1F_R8_คำบรรยาย…` · `…ใบแทนของใบแทน_บรรทัดอ้างใบเดิมมีบรรทัดเดียว…` |
| **V1-R9** ลิงก์ที่ไม่ถูกย้าย | P3 | ✅ | `RepointDocumentLinksAsync(companyId, old, neo, lineMap)`: `PaymentIntents` (SourceKind=Document · SourceId · ReceiptDocumentId ของใบเดิม) · ใบเสร็จเดิม→ใหม่ในลูปใบเสร็จ · `ProjectCostEntries.DocumentLineId` รายบรรทัด · JE ชี้ใบใหม่ + คำบรรยาย `JournalDescriptionAfterMove` (`Reference` ห้ามแตะ — forbid `j.Reference =`) | `R200_V1F_R9_JEที่ย้ายไปใบแทน…` |
| **V1-R10** กติกาผู้ซื้อ §86/4 สำเนาที่สอง | P3 | ✅ | `Services/Implementations/Tax/TaxInvoiceCompletenessChecker.cs` `MustEnforceBuyerFields` + `BuyerBlockingFields` — ใช้ทั้ง `ApproveDocumentAsync` (แทน inline `rd864Types`/บล็อก) และออกใบแทน · `BuyerDeclinedTaxInvoice` ตามไปเฉพาะผู้ซื้อคนเดิม | `R200_V1F_R10_…` |
| **V1-P1** e-Tax by Email | P2 | ✅ | `DocumentVoidPreconditions.EffectiveEtax` (internal) + `EffectiveEtaxAsync` (ตัวโหลดเดียว: แถว e-Tax + `DocumentEmailLog` IsEtaxByEmail·IncludedRdTimestamp·Sent ⇒ Accepted) ใช้ใน `DecideAutoReceiptOnPaymentVoidAsync` · `EvaluateSettlementPaidReissueAsync` · `SettlementPostingService.LoadUnpostFactsAsync` | `R200_V1F_P1_…` |
| **V1-P2** throw กลางลูปยกเลิกการลงบัญชี | P3 | ✅ | `AutoReceiptOnPaymentVoid(SettlementUnpost)` = ติดธง (ด่านก่อนเริ่มยังปฏิเสธตามเดิม) · `UnpostCoreAsync` เก็บ `PaymentVoidResult.EtaxCancellationFlag` แล้วบอกในผลลัพธ์ (ห้ามทิ้งผล — ล็อกด้วย must) | `VoidReissueR200Tests.R200_V1_ยกเลิกการลงบัญชีรอบโอน_…ติดธงไม่throwกลางลูป` (แทนเทสต์ "ปฏิเสธ" เดิม) |
| **V1-P3** "ประกาศว่ายื่นแต่ยังไม่ล็อก" · `claimedElsewhere` | P3 | 📋 | ต้องให้เจ้าของตัดสิน (Q4 ของทีม V1) · `claimedElsewhere`/แถวใบ Voided ในรายงานภาษีขาย = พฤติกรรมเดิมของ `TaxService` นอกขอบเขต | — |
| **V1-P4** retry หลัง commit | P3 | ✅ | `ReissueSettlementPaidDocumentAsync`: lambda คืน `(Neo, Receipts, Message)` · hooks + `GetDocumentAsync` นอก execution strategy | ล็อกลำดับใน `required_call_site_check` |

## ออกแบบที่ตัดสินเอง (ทิศมองเห็นและย้อนได้ — ให้เจ้าของทบทวน)
1. **R6 เลือก "บันทึกคำขอรอคนที่สอง" ไม่ใช่ "ร่างใบแทน"** — ร่างใบแทน (Draft ที่ `ReplacesDocumentId` ชี้ใบเดิม) จะโผล่ในรายการเอกสาร และถ้ามีคนกด "อนุมัติ" ปกติ
   จะลง JE/สต็อกซ้ำ (ต้องเพิ่มด่านใน `ApproveDocumentAsync` อีกชั้น) ⇒ เก็บคำขอบนใบเดิม 3 คอลัมน์ ระหว่างรอไม่มีอะไรถูกแตะ · วงเงินเซ็นหลายขั้นใช้
   "คนที่สอง" แทน flow ลายเซ็นหลายขั้น (flow ลายเซ็นผูกกับ `ApproveDocumentAsync` ของเอกสาร Draft — ใบแทนไม่ผ่านเส้นนั้น) → คำถามค้าง Q1
2. **R2 ใช้ "ใบเสร็จถือ VAT ที่ยังมีผล" ทุกใบ ไม่ใช่เฉพาะใบที่ติดธงในคำขอนี้** — ใบเสร็จแปลง (convert) ที่ถือ VAT ก็เป็นใบกำกับที่ออกแล้วเช่นกัน ⇒ ถอยภาษีขาย
   ขณะใบกำกับยังมีผล = ภาษีขายหายจากแบบ (ทิศเงียบ) · ทิศนี้ทำให้ VAT **ค้างรายงาน**จนกว่าจะยกเลิกใบเสร็จนั้น (ทิศมองเห็นได้)
3. **R5 `Reference` ตามไปใบแทน** — ใบแทนคือการขายเดียวกัน · การรับชำระ/ออกใบของคู่ค้าที่ค้นด้วยเลขอ้างอิงกรอง Voided อยู่แล้ว ⇒ เจอใบแทน ·
   สั่งยกเลิกด้วยเลขอ้างอิง ⇒ เลือกใบที่ยังมีผล (= ใบแทน) ⇒ ยกเลิกใบแทนผ่านด่านปกติ (ด่านรอบโอนตอบ 409 ให้คู่ค้าเห็น) · สั่งด้วย DocumentId ของใบเดิม ⇒ 409 พร้อมเลขใบแทน
4. **P2 ตาข่ายชั้นสองเปลี่ยนจาก "ปฏิเสธ" เป็น "ติดธง"** — ด่านก่อนเริ่ม (`SettlementUnpostGate`) ยังปฏิเสธใบที่ส่งแล้วตามเดิม · ถึงตาข่ายได้เฉพาะ race
5. **R3 ใบเสร็จที่ยกเลิกผ่านการปิดธงถูก soft-delete** (เส้นเดียวกับใบเสร็จที่ยกเลิกคู่การชำระ `ApplyAutoReceiptOnPaymentVoidAsync`) ⇒ endpoint คืนใบต้นทาง

## F3 ข้อ 7–12
7. รูปแบบเดิม: `SettlementPaidReissue.CopyScalars` เหลือ **0** จุด (ลบ) · `e.Status != EtaxStatus.Accepted` ในเส้นออกใบแทน **0** · inline SoD/วงเงินใน `ApproveDocumentAsync` **0**
   (`ApprovalControlPolicy`) · inline ผู้ซื้อ §86/4 (`rd864Types`/`MissingBuyerFields` ใน approve+reissue) **0** · ตัวโหลด e-Tax ของการยกเลิกที่ไม่เห็น e-Tax by Email **0** จาก 3 เส้น
   (void payment · reissue · unpost) — `VoidDocumentAsync` (ยกเลิกเอกสารปกติ) ยังดูแค่ `Accepted` และพลิก Submitted เป็น Voided ⇒ คำถามค้าง Q2 ·
   `SettlementPostingGate.SodSelfApproval` (ของ V2 — "ผู้สร้างไม่รู้ = บล็อก" ต่างจาก approve) ไม่ยุบ ⇒ Q5
8. ทางเข้าอื่น: ยกเลิกการชำระ (หน้าเอกสาร · เช็คเด้ง · ยกเลิกการลงบัญชี) เดินตัวตัดสินเดียว · ออกใบแทน = endpoint เดียว (ปุ่ม/ยืนยัน) · integration void (R5) ·
   ออกใบเสร็จ (รับชำระ · ออกย้อนหลัง) ใช้ `CarriesTaxInvoiceRole` ตัวเดียว · มือถือ/LINE ไม่มีเส้นออกใบแทน/ปิดธง
9. เข้มขึ้น + ทางไปต่อ: ใบเสร็จในรายงานล็อก ⇒ ปลดล็อก/Reject & Reverse/ใบลดหนี้ (ทิศตรงข้าม `…ทิศตรงข้าม…ออกใบแทนได้เหมือนเดิม`) · ใบขาย Submitted ⇒ ยกเลิก e-Tax ก่อน
   (ทิศตรงข้ามใน `R4`) · SoD/วงเงิน ⇒ บันทึกคำขอ + คนที่สองยืนยัน/ยกเลิกคำขอ (ทิศตรงข้าม `…ต่ำกว่าเกณฑ์ทำได้ทันที`) · integration ⇒ เลขใบแทน · ปิดธงไม่มีหลักฐาน ⇒ บอกหลักฐานที่ต้องใช้
10. ค่าที่ persist ก่อนแก้: ใบเสร็จที่ติดธงก่อนรอบนี้ซึ่งถูกถอยภาษีขายไปแล้ว (R2 เดิม) — **ไม่มี migration อัตโนมัติ** (ต้องให้นักบัญชีตรวจตาม DECISIONS ข้อ 20) ⇒ Q3 ·
    คอลัมน์ใหม่ `ReissueRequest*` default null = พฤติกรรมเดิม · ใบแทนที่ออกไปแล้วด้วย `CopyScalars` อาจพาลายเซ็นรับของ/ผลตรวจ RD มา — ไม่แก้ย้อนหลัง (ข้อมูลแสดงผล ไม่ใช่เงิน) ⇒ Q4
11. ฝ่ายค้าน: ทีมนี้คือรอบแก้ผลฝ่ายค้าน — **ขอ main agent ส่ง diff นี้ให้ฝ่ายค้านอีกรอบ** (โดยเฉพาะ R2 ผลตามที่ `CarriesTaxInvoiceRole` · R6 สคีมาคำขอ · R3 การยืนยันด้วยเลขอ้างอิงที่ระบบตรวจกับกรมสรรพากรไม่ได้)
12. DOCUMENT_FLOW §2.4c · §3.5 + บล็อก Last verified · TEST_PLAN §0 (`--row`) · CHANGELOG · ตารางสถานะท้าย `review200-V1.md`

## checker ที่รัน (เครื่องโหลดหนัก — รันเฉพาะที่เกี่ยว + check_all ในพื้นหลัง)
`required_call_site_check` ✅ (511 กติกา + negative test ในตัว) · `terminal_status_writer_check` ✅ (11 ≤ baseline 12 — แถวที่ลดคือ `BankV1Controller` ของทีมอื่น ไม่แตะ) ·
`approved_status_writer_check` ✅ · `record_arg_check` ✅ · `nullable_arg_check` ✅ · `using_check` ✅ · `write_permission_gate_check` ✅ · `owner_action_wiring_check` ✅ ·
`namespace_shadow_check` ✅ · `service_interface_check` ✅ · `tuple_name_merge_check` ✅ · `string_quote_close_check` ✅ · `comment_line_break_check` ✅ · `identifier_space_check` ✅ ·
`verbatim_string_check` ✅ · `dead_helper_check` ✅ (หลังทำ `EffectiveEtax`/`ScalarProperties` เป็น internal) · `undeclared_local_check` · `arg_type_check` · `accessibility_check` ·
`dto_nullable_contract_check` · `html_attr_escape_check` · `onclick_js_string_check` · `js_dup_method_check` · `enum_number_compare_check` ✅ · `test_inventory --check` ✅ ·
`doc_commit_sha_check` ✅ · awk brace = 0 ทุกไฟล์ .cs ที่แก้ · ผล `check_all.sh` อยู่ท้ายรายงานนี้

## ความเสี่ยงคอมไพล์ที่ตรวจไม่ได้ (ไม่มี SDK)
1. `strategy.ExecuteAsync(async () => { … return (Neo: …, Receipts: …, Message: …); })` — overload `ExecuteAsync<TResult>(Func<Task<TResult>>)` · สองจุด return ชนิด tuple เดียวกัน
   (`Document?` ประกาศไว้) · `catch { rollback; throw; }` หลัง return ใน try
2. `for (…; hop is { Status: DocumentStatus.Voided, ReplacedByDocumentId: Guid nextId }; …)` ใช้ `nextId` ใน body — pattern variable ในเงื่อนไข for อยู่ใน scope ของ body (C# 7.3+)
3. `SettlementPaidReissueRequestCodec` (internal static · namespace `Accounting.Services.Implementations`) ถูกเรียกจาก `MapToResponse` ใน `DocumentService.cs` (partial เดียวกัน)
   และจากเทสต์ผ่าน `InternalsVisibleTo`
4. `System.Text.Json` deserialize positional record `ReissueSettlementPaidRequest` (มีพารามิเตอร์ default ท้าย) + `List<ReissueLineDescription>` — ใช้ `JsonSerializerDefaults.Web`
5. `ExecuteUpdateAsync(u => u.SetProperty(p => p.DocumentLineId, (Guid?)newLineId))` ในลูป `foreach (var (oldLineId, newLineId) in lineMap)` — deconstruct `KeyValuePair` (.NET Core 2.0+)
6. `DocumentResponse` เพิ่ม 3 พารามิเตอร์ท้าย (มีค่าเริ่มต้น) — `MapToResponse` ใช้ named args ต่อเนื่อง
7. `Assert.DoesNotContain("หมายเหตุ", IReadOnlyList<string>)` — resolve เป็น overload คอลเลกชัน (อาร์กิวเมนต์ที่สองไม่ใช่ string)

## คำถามค้าง (ให้เจ้าของทบทวน)
- **Q1** ใบแทนที่ยอดถึงเกณฑ์เซ็นหลายขั้น: ตอนนี้ = "คนที่สองยืนยัน" (ไม่ใช่ flow ลายเซ็นหลายขั้นเต็ม) — พอไหม หรือต้องผูก flow ลายเซ็นของเอกสาร
- **Q2** `VoidDocumentAsync` (ยกเลิกเอกสารปกติ) ยังพลิก e-Tax Submitted เป็น Voided และไม่เห็น e-Tax by Email — ใช้ `EffectiveEtaxAsync` + บล็อกแบบเดียวกันไหม (กระทบทุกการยกเลิก — ไม่แตะรอบนี้)
- **Q3** ใบเสร็จที่ติดธงก่อนรอบนี้ ภาษีขายถูกถอยไปแล้ว — ทำรายงานอ่านอย่างเดียวให้นักบัญชีตรวจ (แบบข้อ 20) ไหม
- **Q4** ใบแทนที่ออกด้วย `CopyScalars` ก่อนรอบนี้ (ลายเซ็นรับของ/ผลตรวจ RD ของใบเดิม) — ล้างย้อนหลังไหม
- **Q5** `SettlementPostingGate.SodSelfApproval` (ผู้สร้างไม่รู้ = บล็อก) กับ `ApprovalControlPolicy.SelfApprovalBlocked` (ผู้สร้างไม่รู้ = ไม่บล็อก) — กติกาต่างกันโดยตั้งใจ (รอบโอนนำเข้าจากไฟล์) หรือควรยุบ
- **Q6** ปิดธงด้วยเลขอ้างอิง: ระบบตรวจกับกรมสรรพากรไม่ได้ — ต้องแนบไฟล์หลักฐาน/ผู้อนุมัติคนที่สองด้วยไหม

## คอมมิต
- โค้ด + เอกสาร: `c6b4908a` · เติม sha: คอมมิตตามหลัง (ห้าม amend)
