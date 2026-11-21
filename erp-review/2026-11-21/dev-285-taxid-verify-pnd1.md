# รอบ 285 — ยืนยันเลขผู้เสียภาษีของบริษัท + ภ.ง.ด.1 ช่องที่ 5 (คำถามค้างของเจ้าของ 2 ข้อ)

ที่มา: 280-10 (`erp-review/2026-11-20/dev-280-partner-request-channels.md` §8) · 275-E-7 (`erp-review/2026-11-20/dev-275-E.md`) ·
เจ้าของสั่งให้ทีมตัดสินและทำให้เสร็จ · คำตัดสิน 285-1…285-7 ใน `erp-review/2026-09-29/DECISIONS.md`

## 1. วิเคราะห์ระบบเดิม (ยืนยันกับโค้ดจริง)

| เรื่อง | พบ |
| --- | --- |
| ขั้นยืนยันเลขภาษีของบริษัท | **ไม่มี** — `CompanyService.UpdateAsync` ตั้ง `TaxId` อะไรก็ได้ · รอบ 280 แก้ด้วย `TaxIdSetAt` (เปิดเฉพาะคำขอหลังตั้งเลข) |
| กลไกตรวจทะเบียนที่มีแล้ว | `IDbdLookupService` (`DbdLookupService`: RD VAT SOAP `vatserviceRD3` → DBD openapi → CKAN → datawarehouse · `VerifyTinAsync` RD TIN check · `GetBranchAsync`) · `DbdIdentityGuard` (OCR: ทะเบียนชนะเมื่อกุญแจไม่ขัด · `Normalize` ตัดคำนำหน้า) · `EtaxPartyTaxIdGuard` |
| ทะเบียนพิสูจน์ความเป็นเจ้าของได้ไหม | **ไม่ได้** — ชื่อ/เลขในทะเบียนเป็นข้อมูลสาธารณะ ผู้แอบอ้างพิมพ์ชื่อให้ตรงได้ ⇒ ใช้เป็นด่านตัดของที่ผิดชัดเท่านั้น |
| ที่อื่นที่เชื่อ `Company.TaxId` | จับคู่คู่ค้า (`PartnerCompanyMatch` · invite/route/notify) — ทุกเส้นเอกสาร/เงินข้ามบริษัทต้องมีคู่ค้า **Accepted** (ยินยอมสองฝั่ง) · e-Tax ใช้ใบรับรอง CA ที่ RD ตรวจเอง |

## 2. สิ่งที่ทำ

- ตัวตัดสินเดียว `Accounting/Helpers/CompanyTaxIdVerification.cs` (IsVerified · IncomingRequestCutoff · CompareRegistryName ตรงตัว · SubmitProblem · ApproveProblem · ReasonProblem · ป้าย)
- `Company.TaxIdVerifiedAt/TaxIdVerifiedFor/TaxIdVerificationRequestId` + ตาราง `CompanyTaxIdVerificationRequests` (`Round285Migration`)
- `CompanyTaxIdVerificationService`: ยื่น (ไฟล์ PDF/รูปจากไบต์ · SHA-256 · ไฟล์แนบชนิด `CompanyTaxIdEvidence` · เทียบทะเบียน · audit) · อนุมัติ (ใต้ `FOR UPDATE` แถวบริษัท · เลขในคำขอต้องตรงเลขปัจจุบัน) · ปฏิเสธ/ถอน (ต้องมีเหตุ) · ผู้ดูแลเปิดไฟล์ (ตรวจ SHA-256)
- `CompanyTaxIdVerificationController` (`api/companies/{id}/tax-id-verification` · ยื่น = `CompanySettings.Edit` · ปฏิเสธ API key) · `AdminCompanyTaxIdVerificationController` (`api/admin/tax-id-verifications` · SystemAdmin)
- `StampTaxIdSetAt` ⇒ เปลี่ยนเลขทุกทางเข้า = หลุดสถานะ · `MaterializeIncomingLinkRequestsAsync` ⇒ ยืนยันแล้วเปิดคำขอที่ส่งก่อนสมัคร · การ์ดคำขอฝั่งผู้รับแสดงสถานะเลขของผู้ขอ
- หน้าจอ: `settings.html` กล่องใต้ช่องเลขภาษี (สถานะ/เหตุ/ฟอร์มยื่น — ป้ายจากเซิร์ฟเวอร์) · `/admin/taxid-verifications.html` (คิว · เปิดไฟล์ · อนุมัติ/ปฏิเสธ/ถอน · ปุ่มอนุมัติปิดพร้อมเหตุเมื่อ `ApproveProblem`) · `partners.html` ป้ายบนการ์ด
- ตาราง `AttachmentPermissionScope` +1 ชนิด (ระบบแนบ · อ่าน/ลบ = ตั้งค่าบริษัท)

## 3. สถานะทีละข้อ

| ข้อ | สถานะ | หมายเหตุ |
| --- | --- | --- |
| (1) ยืนยันเลขผู้เสียภาษีของบริษัท | ✅ แก้แล้ว ae317b36 | หลักฐาน = เอกสาร + คนอนุมัติ (285-1) · ไม่ใช้อีเมลโดเมน · ไม่ fuzzy |
| (1) เห็นคำขอที่ส่งก่อนสมัคร | ✅ | เฉพาะบริษัทที่ยืนยันแล้ว (285-3) · ยังไม่ยืนยัน = เดิม |
| (1) เปลี่ยน TaxId ⇒ หลุดสถานะ | ✅ | ทุกทางเข้า SaveChanges + ชั้นที่สองใน `IsVerified` |
| (1) ใช้กับเส้นคู่ค้า | ✅ บางส่วน | ป้ายบนการ์ดคำขอ · **ไม่**เปลี่ยนตัวตัดสินกรณีเลขซ้ำ (`PartnerCompanyMatch`) — เปลี่ยนเส้นทางเอกสารเงียบ ๆ เสี่ยงกว่า (285-5) |
| (1) ใช้กับ 50 ทวิ ที่รับ / e-Tax | ไม่ทำ (ตั้งใจ) | 50 ทวิ/เอกสารข้ามบริษัทไหลผ่านคู่ค้า Accepted เท่านั้น · e-Tax ตัวตนผูกใบรับรอง CA ที่ RD ตรวจ (285-5) |
| (2) ภ.ง.ด.1 ช่อง 5 | คงพฤติกรรมเดิม | ไม่ได้คำตอบชัดจากแหล่งทางการ — ดู §4 |

## 4. ภ.ง.ด.1 ช่องที่ 5 — ค้นที่ไหนแล้วได้อะไร (285-7 · เข้าถึง 2026-11-21)

1. WebSearch (ไทย) "โครงสร้างข้อมูล ภ.ง.ด.1 ยื่นผ่านอินเทอร์เน็ต เลขประจำตัวผู้เสียภาษีอากร ผู้มีเงินได้ ไม่มีเลข" → ได้แต่หน้าเมนู rd.go.th (เช่น `rd.go.th/region/01/pnd1-electronics.html`) ไม่มีสเปกฟิลด์
2. WebSearch "… คนต่างด้าว ไม่มีเลข กรอก 0000000000000 ใบแนบ" → ตัวอย่างใบแนบจากบุคคลที่สาม (businessplus.co.th) — **ไม่ใช่แหล่งทางการ** ไม่ยืนยันข้อตกลงเติม 0
3. WebSearch "ข้อกำหนดโครงสร้างข้อมูล ใบแนบ ภ.ง.ด.1 … ไฟล์ txt" → พบคู่มือทางการ `https://rd.go.th/fileadmin/user_upload/WHT/swc1.5.4/swc-ui_manual_1.5.4_V2.pdf`
   ที่ดัชนีมีภาคผนวก "รูปแบบข้อมูล ภ.ง.ด.1 ภ.ง.ด.1ก และ ภ.ง.ด.1ก พิเศษ (Version 1.0)" — **WebFetch ล้ม: โฮสต์ rd.go.th / www.rd.go.th ไม่อยู่ใน allow-list ของเครือข่าย session นี้**
4. ผล: ไม่มีคำตอบชัด ⇒ **ไม่เดา** · `EmployeeTaxIdentity.ForFiling` / `Pnd1FileFormat` / `Pnd1FilingData.Build` ไม่แก้ (เติม 0 + ป้าย "ยังไม่ยืนยันกับสเปกกรมสรรพากร")
5. ทางไปต่อ: เจ้าของอนุญาตโฮสต์ `rd.go.th` + `www.rd.go.th` ในค่าเครือข่าย แล้วสั่งให้อ่านภาคผนวกของคู่มือ SWC-UI 1.5.4 · หรือถาม RD Call Center 1161 —
   ถ้าสเปกบังคับเลขที่กรมออกให้คนต่างชาติ ⇒ เปลี่ยน `ForFiling` ให้บล็อกพร้อมทาง "ให้พนักงานขอเลขประจำตัวผู้เสียภาษีจากกรม" (ไม่ใช่เติม 0)

## 5. ตรวจแล้วไม่ใช่บั๊ก

- ไม่มี

## 6. ทางเข้าที่แตะข้อมูลชุดเดียวกัน

- ตั้ง/เปลี่ยน `Company.TaxId`: `CompanyService.UpdateAsync` (ทางเดียวในหน้าเว็บ) + ทุก SaveChanges ⇒ `StampTaxIdSetAt` ล้างสถานะ — **ทางตั้ง `TaxIdVerifiedAt` นอก `ApproveAsync` เหลือ 0 จุด** (นอกเทสต์)
- เปิดคำขอเชื่อม: `MaterializeIncomingLinkRequestsAsync` (ทางเดียว · `POST …/requests/incoming/refresh`) · `OfferLinkRequestAsync` (ผู้ขอส่งแจ้งอีกครั้ง) ไม่เปลี่ยน
- ไม่เพิ่ม throw ในเส้นเดิม · ผู้ที่ยังไม่ยืนยันได้พฤติกรรมรอบ 280 เดิม (ทิศตรงข้าม: T285-01/T285-02)

## 7. สิ่งที่เจ้าของต้องทำหลัง deploy

1. ผู้ดูแลแพลตฟอร์มเปิด `/admin/taxid-verifications.html` เป็นระยะ (ยังไม่มีแจ้งเตือนเมื่อมีคำขอใหม่ — เลื่อน)
2. แจ้งลูกค้าว่ายื่นหลักฐานได้ที่หน้าตั้งค่าบริษัท (ไม่บังคับ — ไม่ยื่นก็ใช้งานเหมือนเดิม)
3. ถ้าต้องการปิดคำถาม 275-E-7: อนุญาตโฮสต์ rd.go.th ใน session หรือยืนยันกับนักบัญชี/RD 1161

## 8. เลื่อน

- แจ้งผู้ดูแลแพลตฟอร์มเมื่อมีคำขอใหม่ / แจ้งบริษัทเมื่ออนุมัติ-ปฏิเสธ (ตอนนี้ดูสถานะที่หน้าตั้งค่า)
- ใช้สถานะยืนยันตัดสินกรณีสองบริษัทเลขเดียวกันใน `PartnerCompanyMatch` (ต้องออกแบบกับเจ้าของ)

## 9. หลักฐาน

- เทสต์: pure `CompanyTaxIdVerificationRound285Tests` 13 เคส · Db `CompanyTaxIdVerificationRound285DbTests` 4 · รันในเครื่อง (PostgreSQL 16 · `accounting_test_r285`) ผ่าน 17/17 + `PartnerLinkNotifyRound280DbTests` ผ่าน
- กติกา `_R285_*` +8 · เคสฝ่ายค้าน R285-1/2
- คอมมิต: ae317b36 (งาน) · sha เติมในคอมมิตตามหลัง
