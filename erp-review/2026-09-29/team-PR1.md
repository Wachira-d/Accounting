# รอบ 200 ทีม PR1 — ➕/🗑 พนักงานในรอบเงินเดือนที่คำนวณ/นำเข้าแล้ว

**โจทย์ผู้ใช้**: หน้ารอบเงินเดือน (`payroll.html` → `viewRun`) ของรอบที่ **นำเข้าจากระบบนอก** (`ImportPayrollRunAsync` · `IsExternalImport=true` ·
คำนวณใหม่ไม่ได้ตาม `PayrollRunEditPolicy.CanRecalculate`) — "ถ้าต้องการสร้างหรือเพิ่มรายชื่อจากหน้านี้ต้องทำยังไง". ก่อนรอบนี้มีแค่ ✏️ แก้ยอด
(`UpdatePayrollDetailAsync`) กับแหล่งจ่าย (`SetEmployeePaymentAccountAsync`) — **ไม่มีทางเพิ่ม/เอาออกเลย** ทางเดียวคือนำเข้าใหม่ทั้งรอบ (ซึ่งชน
`ExternalRunRef` เดิม) · รอบที่สร้างในระบบก็มีแค่ "คำนวณใหม่ทั้งรอบ" ซึ่งทับยอดที่แก้มือของทุกคน

**สถานะ**: โค้ด + เทสต์ + checker + เอกสารครบ · **ยังไม่ได้คอมไพล์/รันเทสต์ในเครื่องนี้ (ไม่มี .NET SDK) — CI เป็นตัวแรก**

## สิ่งที่ทำ

| # | สิ่งที่ทำ | ที่ |
| --- | --- | --- |
| 1 | ตัวตั้งเดียว "พนักงานอยู่ในงวด" — `InPeriod(start,end)` คืน `Expression<Func<Employee,bool>>` (EF + `.Compile()`) · `Reason(emp,start,end)` ข้อความไทยพร้อมทางไปต่อ (เริ่มงานหลังงวด · พ้นสภาพก่อนงวด · ปิดใช้งานไม่มีวันพ้นสภาพ) · tenant/IsDeleted อยู่ใน Where ของผู้เรียก | `Accounting/Helpers/PayrollEmployeeEligibility.cs` |
| 2 | `CalculatePayrollAsync` เรียกตัวตั้งนี้แทนเงื่อนไข inline (D-S2 ลาออกกลางเดือนคงเดิม) | `PayrollService.cs:1874-1876` |
| 3 | ตัวเติมยอดรายคนตัวเดียว `PayrollDetailAmounts.Apply` (ย้ายจาก `UpdatePayrollDetailAsync` คำต่อคำ — ข้อความ error เดิมทุกตัว) + `RecomputeRunTotals` (กรอง `!IsDeleted` · `EmployeeCount` · `TotalWorkersCompensation`) · แก้ยอดเรียกตัวนี้ | `Accounting/Helpers/PayrollDetailAmounts.cs` · `PayrollService.cs:1466-1472` |
| 4 | `GetAddableEmployeesAsync` — ผ่าน eligibility + ยังไม่อยู่ในรอบ · เงินเดือนคืนเฉพาะ `includeSalary` | `PayrollService.cs:1493` |
| 5 | `AddPayrollDetailAsync` — ธุรกรรม + `FOR UPDATE` (tenant) → อ่านรอบใต้ล็อก → `CanEditAmounts` + หลักฐาน (`PAYROLL-EDIT-LOCKED`) → พนักงานของบริษัท → ซ้ำ 409 `PAYROLL-DETAIL-DUPLICATE` → `Reason` (`PAYROLL-EMPLOYEE-NOT-IN-PERIOD`) → แหล่งจ่าย → `Apply` → รายได้ 0 ปฏิเสธ → กองทุนเงินทดแทน (กติกาเดียวกับเส้นคำนวณ) → `RecomputeRunTotals` → `AddChainedAuditLog` → save/commit · ภาษี/ฐาน ปกส./เหตุผล ≥ 5 ตัวอักษร **บังคับ** · สถานะรอบคงเดิม | `PayrollService.cs:1532` |
| 6 | `RemovePayrollDetailAsync` — ด่านเดียวกัน · soft-delete · ห้ามเหลือ 0 คน (`PAYROLL-DETAIL-LAST`) · ห้ามเอาออกเมื่อเวลาทำงานถูกปันเข้าโครงการด้วยรอบนี้ (`PAYROLL-DETAIL-ALLOCATED`) · audit OldValues/NewValues | `PayrollService.cs:1680` |
| 7 | ผังแหล่งจ่ายรายคน — แยกเป็น `IsValidNetPaymentAccountAsync` ตัวเดียว ใช้ทั้ง "แก้แหล่งจ่าย" และ ➕ (เข้มกว่าเส้น import: ต้องเป็น 111x/1133/2123 ระดับ 4+) | `PayrollService.cs:1484` |
| 8 | Controller: `GET runs/{id}/addable-employees` (`CheckPayrollAccessAsync`) · `POST runs/{id}/employees` · `DELETE runs/{id}/employees/{employeeId}?reason=` (`RequirePayrollWriteAsync(..., PayrollRun)`) · interface 3 เมธอด | `PayrollController.cs:471-517` · `IPayrollService.cs` |
| 9 | DTO `AddPayrollDetailRequest` (ตัวเลข `decimal?` · `Reason` `string?`) · `PayrollAddableEmployeeDto` · `PayrollRunResponse` +`PeriodStart/PeriodEnd` | `Models/DTOs/Payroll/PayrollDtos.cs` |
| 10 | หน้าเว็บ: ปุ่ม "➕ เพิ่มพนักงานเข้ารอบนี้" (disabled + `title`=เหตุผลล็อก เมื่อแก้ไม่ได้) · 🗑 เอาออกรายแถว (prompt เหตุผล) · โมดัลรายคน**ตัวเดียว**กับ ✏️ (`_edInputsHtml` · สุทธิสด `_edRecalc` · ปกส. `_edSyncSso`) · dropdown จากเซิร์ฟเวอร์ + 🔄 โหลดใหม่ + ลิงก์สร้างพนักงาน · ค่าเริ่มต้น = เงินเดือนรายเดือน / ฐาน ปกส. = เงินเดือนเมื่ออยู่ใน ม.33 (ช่องที่ผู้ใช้พิมพ์ไม่ถูกทับ) · ภาษีไม่เติม · ว่าง = ตัดคีย์ | `payroll.html:1673-1735` · `:1838-2030` · `api.js` |
| 11 | `employees.html?new=1` เปิดฟอร์มสร้างพนักงานทันที (ล้าง query หลังเปิด) | `employees.html:337-343` |

## เส้นอ่าน PayrollDetails หลัง soft-delete (ตรวจแล้ว)

`PayrollDetail` มี `HasQueryFilter(!IsDeleted)` (`AccountingDbContext.cs:2300`) · `grep` ทั้งเรพ: **ไม่มี** raw SQL ที่อ่าน `"PayrollDetails"` (มีแค่ `ALTER TABLE`
ใน `DatabaseMigrationHelper`) · `IgnoreQueryFilters` ทั้ง 4 ไฟล์ที่แตะ payroll (`PdfGenerationService` JE · `TaxService` Documents · `PayrollService` Employee restore ·
`StatutoryRemittanceService` Contacts) **ไม่ใช่** `PayrollDetails` ⇒ ภ.ง.ด.1 · สปส.1-10 · 50 ทวิ · สลิป · JE ตอนจ่าย (`Include(r => r.Details)`) · ปันต้นทุน ·
`TaxFilingExportService` · `PayslipLineDeliveryService` ไม่เห็นแถวที่เอาออก · `CalculatePayrollAsync` ลบจริงเฉพาะแถวที่ยังไม่ลบ (แถว soft-delete ค้างไว้เป็นประวัติ) ·
ไม่มี unique index บน `(PayrollRunId, EmployeeId)` ⇒ เอาออกแล้วเพิ่มคนเดิมกลับได้

## เทสต์ + ด่าน

- `Accounting.Tests/PayrollEmployeeEligibilityTests.cs` (8 Fact · สองทิศ + ตาราง 60 กรณีเทียบ `Reason` กับ expression)
- `Accounting.Tests/PayrollDetailAmountsTests.cs` (9 Fact · ปกส. เพดาน 750 / ฐาน 15,000 / ฐาน < 1,650 / ฐาน 0 · ไม่ระบุภาษี ⇒ ปฏิเสธ · สุทธิติดลบ ⇒ ปฏิเสธ · D-D1 · แก้แค่รายการหัก · ยอดรวมไม่นับแถวลบ)
- `tools/required_call_site_check.py` บล็อก "รอบ 200 ทีม PR1" 8 แถว: `CalculatePayrollAsync` ต้องเรียก `InPeriod(` + forbid สำเนาเงื่อนไขเดิม · `GetAddableEmployeesAsync` ใช้ eligibility ·
  `UpdatePayrollDetailAsync` ใช้ `Apply`/`RecomputeRunTotals` + forbid สูตรเดิม · `Add/RemovePayrollDetailAsync` ต้องมี `FOR UPDATE` (+tenant) · `CanEditAmounts(…editEvidence)` ก่อน `SaveChangesAsync` ·
  `if (!canEditAmt) throw` · `AddChainedAuditLog` ก่อน save · ห้าม `AuditLogs.Add(`/`PayrollRunLockEvidence.None` · Add: ภาษี/ฐานบังคับ (`if (!req.X.HasValue) throw`) · Remove: soft-delete + ด่านคนสุดท้าย ·
  controller 3 endpoint: ด่านสิทธิ์ก่อนเรียก service · negative test ด้วยมือ (ดูหัวข้อถัดไป)
- checker ที่รันผ่าน: `service_interface` · `write_permission_gate` · `dto_nullable_contract` · `blank_number_null` · `deep_link_param` · `html_attr_escape` · `onclick_js_string` ·
  `enum_number_compare` · `record_arg` · `nullable_arg` · `using` · `undeclared_local` · `arg_type` · `string_quote_close` · `comment_line_break` · `tuple_name_merge` · `dead_helper` ·
  `identifier_space` · `namespace_shadow` · `accessibility` · `verbatim_string` · `escape_helper` · `js_dup_method` · `settings_reader` · `payroll_rounding` · `test_inventory --check` ·
  `node --check` (payroll.html · employees.html · api.js) · sims `employee_form_contract` · `blank_number_form` · `validation_field_label` · `api_busy_indicator` · brace/U+FFFD

## ความเสี่ยงคอมไพล์ (ยังไม่ได้คอมไพล์)

- `PayrollDetailAmounts.Apply(..., (decimal MaxBase, …) sso)` รับ tuple ชื่อเดียวกับผลของ `GetSsoParamsAsync` (internal) — ชื่อตรงกันทุกช่อง
- `GetAddableEmployeesAsync`: `anon ?? throw` บนชนิดนิรนาม · `!inRun.Contains(e.Id)` (List<Guid>) ใน EF · expression ที่ปิด `DateTime` ของรอบ (parameterize ได้)
- controller `actorUserId == Guid.Empty ? (Guid?)null : actorUserId`
- `settings?.WorkersCompensationEnabled == true && … settings.WorkersCompensationRatePercent` (nullable flow แบบเดียวกับเส้นคำนวณ)

## คำถามค้าง (ให้เจ้าของ/main agent ตัดสิน)

1. **เพิ่มเข้ารอบ Approved แล้วคงสถานะ Approved** (ตามสเปก — เหมือน ✏️ แก้ยอด) ⇒ ผู้อนุมัติไม่เคยเห็นคนที่ถูกเพิ่ม · ควรดีดกลับ Calculated (แบบคำนวณใหม่หลังอนุมัติ) ไหม?
2. **ปุ่ม "ยกเลิกรอบ" ไม่มีบนหน้าเว็บ** (มีแค่ `POST runs/{id}/void`) — ข้อความ `PAYROLL-DETAIL-LAST` จึงบอกตรง ๆ ว่าทำได้ผ่าน API เท่านั้น · ควรเพิ่มปุ่มไหม?
3. **กองทุนเงินทดแทนของแถวที่เพิ่ม** คิดจาก "ฐาน ปกส. ที่ผู้ใช้ประกาศ" (เส้นคำนวณใช้ค่าจ้างตามกฎหมายของงวด) — ✏️ แก้ยอดเดิม**ไม่**คิดเงินทดแทนใหม่เมื่อฐานเปลี่ยน (ไม่แตะในรอบนี้)
4. ผังแหล่งจ่ายของ ➕ ใช้ด่านเดียวกับ "แก้แหล่งจ่าย" (111x/1133/2123 ระดับ 4+) ไม่ใช่ด่านหลวมของเส้น import (สเปกเขียนว่า "แบบเดียวกับ import") — เลือกตัวเข้มเพราะเป็นช่องเดียวกันบนแถวเดียวกัน
5. ฟอร์มสร้างพนักงาน (`employees.html`) ตั้งวันเริ่มงาน = วันนี้ ⇒ สร้างคนให้รอบงวดก่อนจะไม่อยู่ในรายชื่อจนกว่าจะแก้วันเริ่มงาน (โมดัลบอกช่วงงวดและทางไปต่อแล้ว)
6. ภาษีหัก ณ ที่จ่ายของแถวที่เพิ่มกรอกมือทั้งหมด (ระบบไม่คิด — ตามหลักเดียวกับ ✏️) · ถ้าต้องการให้ระบบเสนอยอดต้องมีตัวคิดภาษีรายคนที่ใช้บริบททั้งปีตัวเดียวกับ `CalculatePayrollAsync` (ยังไม่มี)
