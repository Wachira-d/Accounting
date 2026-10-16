# UNIT_TEST_TRIAGE — unit test ที่เน่าเพราะไม่มีด่านไหนรัน (2026-10-16 · ทีม Tb)

**สถานะปัจจุบัน:** base `5a58139e` ล้ม **64 / 9,959** (`dotnet test Accounting.Tests -c Release`) → branch `fix-unit-tests2` ล้ม **0**
(ตัวเลขสุดท้ายอยู่ในข้อความรายงาน/คอมมิตปิดรอบ) · ไม่มีเทสต์ใดถูก skip/ลบ · ทุกเทสต์ที่แก้ฝั่งเทสต์ อ้างคอมมิต/คำตัดสินที่เปลี่ยนกติกา

**ทำไมเน่า:** `tools/check_all.sh` เขียน `dotnet test … | tail -20 || fail=1` — exit code ของ pipeline คือของ `tail` (0 เสมอ) ⇒
พิมพ์ "ผ่านทั้งหมด" ทั้งที่ล้ม 64 · CI รันเฉพาะ `Category=Db` + build ⇒ unit test ไม่มีผู้รันเลย · แก้แล้วใน `64fcf74d`
(เก็บ exit code ของ dotnet เอง + พิมพ์รายชื่อที่ล้ม)

ชนิด: **(a)** บั๊กโค้ดจริง — แก้ที่ owner + เทสต์ทิศตรงข้าม · **(b)** เทสต์ค้างหลังเปลี่ยนกติกาโดยเจตนา/เทสต์ผิดตั้งแต่เขียน · **(c)** สภาพแวดล้อม/ไม่ deterministic

## (a) บั๊กโค้ดจริง — สำคัญสุดสำหรับเจ้าของ

| test | class | root cause | breaking commit | fix | file |
| --- | --- | --- | --- | --- | --- |
| DocumentQuotaPolicyTests.เอกสารจากโมดูลที่พัก_ไม่นับโควตาเอกสาร… (TaxInvoice, Receipt) | a | ใบกำกับ/ใบเสร็จจากที่พักคืน `CountedMustAllow` ⇒ นับโควตาเอกสาร**และ** `lodging.stay` = คิดลูกค้าสองเด้ง (ขัด doc-comment + LODGING_LICENSING_PLAN §13) | 4ffcb92c | ที่พัก = `NotCounted` ทุกชนิด (ยังห้ามบล็อก) + ทิศตรงข้าม ใบขายทั่วไปยังนับ | Helpers/DocumentQuotaPolicy.cs |
| VatBackCalcGuardTests.กระดาษบอกไม่มีVAT_ยังไม่รู้รายการ_ไม่ถอด ×4 | a | บรรทัดว่างล้วนเข้ากิ่ง "ทุกรายการยกเว้น §81" (`All()` บนว่างล้วน = จริง) ⇒ ใบกำกับปกติที่ยังไม่ได้รายการถูกปฏิเสธการแยก 7/107 · เหตุผลผิดข้อ | 099e4702 (เทสต์ที่จับได้เพิ่มใน 8ab95947) | ต้องมีรายการอ่านได้ ≥ 1 + ทิศตรงข้าม | Helpers/VatBackCalcGuard.cs |
| ImportTabularReaderTests.ไบต์ที่ไม่ใช่ทั้ง_UTF8_และ_874_ล้มดัง · BankStatementParsingTests.ไฟล์ที่ไม่ใช่UTF8หรือTIS620_ล้มดัง | a | ตาราง 874 ของ .NET best-fit ไบต์ที่ TIS-620 ไม่กำหนดเป็น U+F8C1–F8C8/C1 แทนการ throw ⇒ ไฟล์ขยะถอด "สำเร็จ" เป็นอักขระเสีย | 71c8fdeb (ย้ายเข้า ImportTabularReader ใน 95c3031b) | ตรวจผลถอด `Is874Undefined` + ทิศตรงข้ามทุกไบต์ที่ 874 กำหนด | Helpers/ImportTabularReader.cs |
| SettlementReview200ReaderTests.RB7_เซลล์ตัวเลขxlsx_ตั้งแต่1e15… | a | "R" ของ .NET 8 พิมพ์เลขกลม ≥ 1e15 เป็นตัวเลขเต็ม ⇒ ด่าน IdLostPrecision มองไม่เห็น · กิ่ง double ไม่ถูกบังคับเลย | b7cd77eb | `Scientific()` บังคับ E+nn ทุกชนิดตัวเลข ≥ 1e15 + ทิศตรงข้าม 15 หลัก | Helpers/ImportTabularReader.cs |
| ThaiAddressParserTests.Bangkok_prefixed_address_still_parses | a | ตำบล/อำเภอ token ที่สองเขียนเป็น character class `[^\s,จังหวัด]+` (ห้ามตัวอักษรทีละตัว) ⇒ "คลองเตย กรุ" | ≤350a394d | negative lookahead ทั้งคำ + จังหวัดไม่มี จ. ที่ติดอำเภอใช้เป็นจังหวัด + ทิศตรงข้าม 3 เทสต์ | Services/Implementations/ThaiAddressParser.cs |
| AiPromptSanitizerTests.เลขบัญชีที่มีป้ายกำกับ…คงป้ายไว้ | a | `ToJsonString` ค่าเริ่มต้น escape ไทยเป็น `\u0E..` ⇒ prompt ยาวหลายเท่า (token) ทุก call ที่เปิดปิดบัง PII | 1d688c15 | `UnsafeRelaxedJsonEscaping` + ทิศตรงข้าม JSON ยังถูกรูป | Services/Ai/AiPromptSanitizer.cs |
| WhtCertUserDecisionTests.แบบที่ขอไม่ตรงผู้ถูกหัก… | a | เหตุผลถึงผู้ใช้มีคำ "payee"/"ContactType" + เว้นวรรคกลางวลีไทย | (รอบ 204) | ต้นทาง ResolveWhtFormType เป็นไทย + Notice ไม่เว้นวรรค | Services/Implementations/WithholdingTaxCertService.cs · Helpers/WhtCertUserDecision.cs |
| VoidReissueR200FTests.R7 · VoidReissueR200GTests.RV1F11 | a | `SourceLinkedAt/By` เพิ่มใน entity ไม่ได้จัดกลุ่มตามไป/ไม่ตามไป (เทสต์ partition ทำหน้าที่ถูก) | a13a995b | จัดเป็น "ไม่ตามไป" | Helpers/SettlementPaidReissue.cs |
| OcrScanPiiTests.ทุกช่องข้อความของแถวสแกน… | a | 5 ช่องใหม่ (ใบต้นทาง · OurRoleAiSuggested) ยังไม่ตัดสิน PII | 7ab17f4b · 7bac95b2 | NotPersonalDataFields (ไม่มีข้อมูลบุคคล) | Helpers/OcrScanPii.cs |
| FeatureCatalogTests.ทุกบิตเดี่ยวต้องมีป้ายไทย… | a | Webhook ป้ายไทย = ชื่อ enum | ≤350a394d | "เว็บฮุก (Webhook)" | Helpers/FeatureCatalog.cs |
| TeamRFRound200Tests.X1_BuildCss_ค่าโจมตีทุกช่อง… | a (เล็ก) | คอมเมนต์ CSS มี `<tr>` ⇒ ด่าน "ไม่มี `<` ใน style" ล้ม | ≤350a394d | แก้คอมเมนต์ (ไม่หย่อนเทสต์) | Services/Implementations/PdfGenerationService.cs |

## (b) เทสต์ค้าง/ผิดตั้งแต่เขียน — แก้เทสต์ (โค้ดถูก)

| test | class | root cause | breaking commit | fix | file |
| --- | --- | --- | --- | --- | --- |
| FilingCalendarRulesTests ×6 (Sso/Vat/Wht ×3/December) | b | งวด 07/2026 → 15 ส.ค. 2569 = เสาร์ · ระบบเลื่อนวันหยุดตาม ป.พ.พ. §193/8 (CLAUDE.md #2 E) | 0393d2f8 | งวดที่ทุกวันเป็นวันทำการ + เทสต์การเลื่อนแยก | Accounting.Tests/FilingCalendarRulesTests.cs |
| SsoLateFeeTests.ช้าเกินหนึ่งเดือนแม้วันเดียว… | b | นับจาก 15 มี.ค. ก่อนเลื่อน — ขัดกับเทสต์ "จ่าย 16 มี.ค. = ตรงกำหนด" ในไฟล์เดียวกัน · สูตรไม่เปลี่ยน | 0393d2f8 | 16 เม.ย. = 1 เดือน · 17 เม.ย. = 2 | SsoLateFeeTests.cs |
| CitRateTableTests ×3 | b | Q10: ทุน 0 = ไม่รู้ ⇒ ไม่ใช่ SME · InlineData int→double? ไม่เคยรัน | 30c2e02e | (1,0,true)+(0,0,false) · 0d/−1d | CitRateTableTests.cs |
| GatewaySettlementMathTests.ก่อนหัก_ลบภาษี… | b | บล็อกเฉพาะ wht > 0 · ค่าธรรมเนียม ≤ 0.16 ภาษีปัดเป็น 0 | ef3d97b5 | ล็อกจุดตัด 0.16/0.17 สองทิศ | GatewaySettlementMathTests.cs |
| InputVatClaimPeriodRulesTests ×2 | b | โค้ด "ต้องยื่นแบบเพิ่มเติม" ไม่เคยเปลี่ยน — เทสต์ค้นคำผิด | ≤350a394d | ค้นคำทางการ | InputVatClaimPeriodRulesTests.cs |
| BuyerTaxInvoiceFieldsTests ×2 | b | สาขาว่าง ≡ 00000 สนญ. ทั้งระบบ · ขาดเมื่อผิดรูป | ≤350a394d | สองทิศ | BuyerTaxInvoiceFieldsTests.cs |
| CashForecastTimingTests.ใบเดียวที่ช้ามาก… | b | มัธยฐานคู่ (3+4)/2 ปัด = 4 | ≤350a394d | 4 + กรณีคี่ | CashForecastTimingTests.cs |
| GlAccountTemplateSanityTests.Code_21510… | b | 21510 อยู่ชุดโรงแรม ไม่ใช่ผังกลาง | ≤350a394d | ตรวจชุดอุตสาหกรรมทุกชุด | GlAccountTemplateSanityTests.cs |
| InboundContactQualityTests.ต้นทางไม่ส่งชื่อมา… | b | แยก NoIncomingNameUnprovenKey | f23a89e2 | สองค่า | InboundContactQualityTests.cs |
| InventoryControlAccountTests.…วัสดุ118 | b | วัสดุ → 11520 | 4bfe9bfd | 11520 + มีในผังกลาง | InventoryControlAccountTests.cs |
| OcrDateReaderTests.ด่านอนุมัติ_วันที่… | b | เติมจากป้าย 0.90 ไม่ต้องยืนยัน | deda7c96 | ตัวเลขลอย + ทิศตรงข้าม | OcrDateReaderTests.cs |
| OcrFieldArbiterTests.ลำดับความน่าเชื่อ… | b | UserConfirmed บนสุด | 723f4ea1 | | OcrFieldArbiterTests.cs |
| PndTextFileFormatTests.Pipe_in_name… | b | Col12 คำนำหน้าชื่อ | a3b93f71 | 12 ช่อง | PndTextFileFormatTests.cs |
| AiUsageReportTests ×2 | b | สถานะที่ 8 LocalServed · 14.82×36.5 = 540.93 (คิดเลขผิด) | 32feb697 | + ล็อก StatusLabelTh จริง | AiUsageReportTests.cs |
| DepositKindNatureGuardTests.C1_ADVANCE… | b | Source = ที่มาของวิธี VAT ไม่ใช่ของประเภท | 219223e2 | + ทิศตรงข้าม | DepositKindNatureGuardTests.cs |
| DocumentConversionTests.Receipt_is_a_terminal_document | b | ใบเสร็จ → CN/DN ตาม §86/9–10 | ≤350a394d | Receipt_converts_only_to_credit_or_debit_note | DocumentConversionTests.cs |
| ForeignServicePayeeBalanceMigrationTests.ทุกคำสั่ง… | b | คำสั่ง (3)(4) ใบลูกยังไม่อนุมัติ ขอบเขตจากใบต้นทาง | 5985acef | ขอบเขตต่อคำสั่ง | ForeignServicePayeeAmountTests.cs |
| ImportHubSchemaTests.Migration_อยู่ในเส้นหลัก… · LodgingWebRound202Tests ×2 | b | สตริงย่อย "DELETE" ฟ้อง `ON DELETE CASCADE` / `"IsDeleted"` (ฟ้องผิดเสมอ) | 95c3031b · รอบ 202 | regex คำสั่ง + ทิศตรงข้าม DML จริงยังจับ | ImportHubSchemaTests.cs · LodgingWebRound202Tests.cs |
| OcrReview201OcTests.OCX9… | b | คอลัมน์อยู่ GetFullTextSearchStatements ตั้งแต่เพิ่ม | f01811b2 | อยู่ชุดบูต 1 ครั้ง | OcrReview201OcTests.cs |
| OcrApprovalGapWarningTests.คำเตือนVATไม่อยู่บนกระดาษ… | b | คำตัดสิน 198 ข้อ 6: API ปฏิเสธ | 33a127f2 | + ทิศตรงข้าม [Σ-GAP] ยังผ่าน | OcrApprovalGapWarningTests.cs |
| OcrFieldProvenanceTests.JSON_ต้องพกป้ายภาษาไทย… | b | ค้นไทยดิบใน JSON ที่ escape | ≤350a394d | ตรวจค่าหลัง parse | OcrFieldProvenanceTests.cs |
| OcrPayeeEvidenceTests · OcrReviewGuardTests | b | เลขทดสอบไม่ผ่าน mod-11 (คอมเมนต์อ้างว่าผ่าน) | 2428110f · bc32e60f | เลขที่ผ่านจริง + เลขเดิมยังถูกปฏิเสธ | OcrPayeeEvidenceTests.cs · OcrReviewGuardTests.cs |
| PosRefundMathTests.ลดเปอร์เซ็นต์_บวกคูปอง… | b | สูตรเดิมหักคูปองซ้ำด้วย (800/135 ไม่ใช่ 850/85) | ≤350a394d | ตัวเลขฐานเดิม | PosRefundMathTests.cs |
| RdComplianceOcrNoiseTests.เลขผู้ซื้อตรงกับบริษัท… | b | fixture ไม่มีสาขาที่สแกนอ่านได้ ⇒ กฎ BRANCH_CODE_PRESENT เตือนถูก | ≤350a394d | fixture + ทิศตรงข้าม | RdComplianceOcrNoiseTests.cs |
| SimulationRound6Tests.The_summary_header… | b | 300+150 = 450 ไม่ใช่ 900 (เทสต์จำลองล้วน ไม่เรียกโค้ดจริง) | ≤350a394d | 450 + ส่วนที่หาย 600 | SimulationRound6Tests.cs |
| SettlementReview198S5 S41 · SettlementRound200V2 V2 | b | ข้อความไทยครอบ by-Email | 3f4e1ea2 | "ถึงกรมสรรพากรแล้ว" | 2 ไฟล์ |
| SettlementReview200ReaderTests.RB9 | b | ค่าตั้งขัดหลักฐาน ⇒ ล้มดัง (I-10) | e1e9f900 | สองทิศ | SettlementReview200ReaderTests.cs |
| SettlementRound201StTests.X3 | b | ข้อความโค้ดกับเทสต์คนละถ้อยคำตั้งแต่เขียน | d0b19cf7 | ข้อความโค้ด | SettlementRound201StTests.cs |

## (c) สภาพแวดล้อม / ไม่ deterministic

| test | class | root cause | breaking commit | fix | file |
| --- | --- | --- | --- | --- | --- |
| PiiEncryptionRoundTripTests ×2 | c | .NET 8 โยน `AuthenticationTagMismatchException` (ลูกของ CryptographicException) | runtime | `ThrowsAny` (ยังบังคับถอดไม่ได้) | PiiEncryptionRoundTripTests.cs |
| SettlementGatewayPhase2Tests.บวกVATเพิ่ม… (flaky) | c | BuildRows เรียง ConfirmedAt แล้ว Id (Guid สุ่ม) | — | เรียงตามยอดก่อนเทียบ | SettlementGatewayPhase2Tests.cs |
| SettlementReview198FixTests.C1c | c | fixture สุ่ม BankAccountId แต่ลายนิ้วมือรวม bank= | 60db75ee | Guid คงที่ | SettlementReview198FixTests.cs |
| SettlementRound201StTests.ST1_ตัวฟังSaveChanges… | c | ฐานออฟไลน์: ตั้งแต่ audit ตอน commit SaveChanges เปิดธุรกรรมก่อนยิง SavingChanges | 9f226f15 | ล็อกเส้นหลัก (Tracked) · ตาข่าย SavingChanges → งานค้างข้างล่าง | SettlementRound201StTests.cs |

## ต้องให้เจ้าของตัดสิน / งานค้าง

1. **ยอดโควตาเอกสารที่นับเกินจากที่พักไปแล้ว** (ข้อ a แถวแรก) — ไม่ migrate: ไม่รู้ว่าเดือนไหนถูกคิด overage ไปแล้ว · ถ้ามีลูกค้าที่พักที่จ่าย overage ต้องตรวจย้อนจาก `UsageEvent`
2. **ExtractionVersion** — การแก้ VatBackCalcGuard เปลี่ยนผลแยก VAT เฉพาะใบที่บรรทัดว่างล้วน · ไม่ได้ bump รุ่น (ผลสแกนเก่าไม่เล่นซ้ำ) — ถ้าต้องการให้สแกนเดิมได้ผลใหม่ เจ้าของสั่ง bump
3. **ตาข่าย `SavingChanges` ของ `SettlementPaymentOwner.StampOnSave`** ไม่มีเทสต์ที่รันได้นอก PostgreSQL แล้ว — เสนอ Db test (Category=Db รันบน CI) ที่เพิ่มแถวก่อนเปิดขอบเขตแล้ว SaveChanges จริง
4. ข้อสังเกต (ไม่ได้แก้): เส้น "last resort" ใน `Program.cs` รันแค่ `ApplyMissingColumns` ไม่รัน `ApplyFullTextSearchIndexes` ซึ่งถือคอลัมน์ `OcrScanResults` ~389 คำสั่ง
5. `SimulationRound6Tests` / ส่วนหนึ่งของ `AiUsageReportTests` ทดสอบสูตรที่เทสต์เขียนเอง ไม่เรียกโค้ดจริง (F2 ข้อ 6) — เสนอผูกกับ `PndTextFileFormat`/`AiUsageReportService` หรือลบ

## กันไม่ให้เกิดซ้ำ (เสนอ — ไม่ได้ทำ เพราะห้ามแตะ ci.yml)

- **CI รัน unit test ทุก push บน `claude/**`**: เพิ่ม step `dotnet test --filter "Category!=Db"` ใน job build เดิม (build ทำอยู่แล้ว · ชุดนี้ ~2 นาที) — สาเหตุรากคือไม่มีผู้รันเลย
- ระหว่างนี้ `check_all.sh` (แก้แล้ว) ล้มดังเมื่อ dotnet test ล้ม · `/publish` เรียก check_all ⇒ เครื่องที่มี SDK จะเห็น
- checker ใหม่ (`tools/`): ห้ามรูป `dotnet … | tail` ที่ไม่มี `pipefail`/`PIPESTATUS` ในสคริปต์ทั้งเรพ (defect class: pipeline กลืน exit code)
- เทสต์ที่ใช้ `Guid.NewGuid()` ใน fixture ของลายนิ้วมือ/ลำดับ ⇒ ใช้ค่าคงที่ (2 ตัวในรอบนี้)
