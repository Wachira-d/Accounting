# ผลฝ่ายค้าน — รอบ 258 แพ็ก "สิทธิ์ + คิวส่ง + หน้าแจ้งเตือนซ้ำ"

- คอมมิตที่ตรวจ: `24b3680e` `54c83725` `38e7503f` `b5724a3c` `e159d198` `5b1adc42` `f2730790` `533382cd` (อ่านที่ `FETCH_HEAD` = `c573a951`)
- วิธีตรวจ: อ่านโค้ดตรงจุด (ไม่ได้คอมไพล์/รันเทสต์ในเครื่องนี้ — ไม่มี .NET SDK) · ไม่แก้โค้ด/ไม่แตะ git
- คำตัดสินที่อ้าง: `258-2-1` … `258-2-9` ใน `erp-review/2026-09-29/DECISIONS.md`

## สรุป

| ระดับ | จำนวน | หมายเหตุ |
| --- | --- | --- |
| HIGH | 1 | ของเดิม (ไม่ใช่ถดถอย) แต่อยู่ในเส้นที่แพ็กนี้ประกาศว่าปิดแล้ว (258-2-3) · แก้บรรทัดเดียว |
| MED | 3 | 2 ข้อเป็นผลของแพ็กนี้โดยตรง (R-M1 · R-M2) · 1 ข้อเป็นช่องที่การออกแบบ 258-2-4 เหลือไว้ (R-M3) |
| LOW | 10 | |

**คำตัดสิน: รวมได้ พร้อมงานตาม (merge with follow-ups)** — ไม่มี HIGH ที่แพ็กนี้สร้างขึ้นใหม่ · แนะนำปิด R-H1 (บรรทัดเดียว) และ R-M1 (เพิ่มคีย์เดียว) ก่อนรวม เพราะทั้งคู่ทำให้คำอ้างของแพ็ก ("ผูกได้ทางเดียวที่มีด่านครบ" · "ผู้สร้างออกแล้วตกไปหาเจ้าของ") ไม่จริง

---

## HIGH

### R-H1 — API key ออกรหัสผูก LINE ในนามเจ้าของคีย์ได้ ⇒ ผู้ถือคีย์ผูก LINE ตัวเองเป็น "เจ้าของ" (ของเดิม · ไม่ใช่ถดถอย)

- หลักฐาน: `Accounting/Controllers/LineWebhookController.cs` คลาส `LineBotBindController` (`[Authorize]` อย่างเดียว · route `api/line-bot`) เมธอด `IssueBindCode` — ไม่มี `RejectApiKey`/`OwnerActionGuard` · `Accounting/Middleware/ApiKeyMiddleware.cs:182-197` สร้างตัวตน = `apiKey.CreatedByUserId` และไม่จำกัด path · `Accounting/Filters/ApiKeyScopeFilter.cs` ตรวจแค่ scope เขียน (POST) · `Accounting/Middleware/TenantGuardFilter.cs:30-35` route ไม่มี `companyId` ⇒ ข้าม
- สถานการณ์: ผู้รับเหมา/ระบบภายนอกที่ถือคีย์ `acc_` (CanWrite) ยิง `POST /api/line-bot/issue-bind-code` ⇒ ได้รหัส 6 หลักของ**ผู้ออกคีย์ (เจ้าของ)** ⇒ ส่ง "ผูก 123456" จาก LINE ของตัวเอง ⇒ `LineBotService.HandleMessageAsync` (บรรทัด ~176-215) ตั้ง `User.LineUserId` ⇒ บอทกลางทำงานในนามเจ้าของ (สร้างเอกสาร · อนุมัติ · ดูยอด) **ทุกบริษัทที่เจ้าของเป็นสมาชิก** ไม่ใช่แค่บริษัทของคีย์ · ไม่ผูกกับ IP allowlist/rate limit/scope ของคีย์ · เพิกถอนคีย์แล้วการผูกยังอยู่ · ตัวบรรเทาที่มี = อีเมลแจ้งเจ้าของ + audit (ตรวจพบภายหลัง ไม่ได้กัน)
- ทำไมนับในรอบนี้: 258-2-3 ถอดทางพิมพ์ userId ดิบเพราะ "ข้ามด่านของทางผูกด้วยรหัส" และ 258-2-1 ห้ามคีย์ตั้งกฎ "เพราะคีย์ถือตัวตนเจ้าของ" — เหตุผลเดียวกันใช้กับจุดนี้ตรง ๆ และแพ็กนี้แก้เมธอดนี้อยู่แล้ว (A-M9) แต่ไม่ใส่ด่าน
- แก้ขั้นต่ำ: `[Accounting.Filters.RejectApiKey("ออกรหัสผูก LINE")]` บน `IssueBindCode` (และ `ClearMyLineBinding` ใน `NotificationConfigController` เพื่อความสมมาตร) + กติกาใน `tools/required_call_site_check.py` + เทสต์ controller แบบเดียวกับ `ตารางส่ง_คีย์API_…`

---

## MED

### R-M1 — ผู้สร้างรายการประจำออกจากบริษัทแล้ว ⇒ แจ้งเตือน "รอบล้ม/ค้างร่าง" ไม่ถึงใครเลย (คำอ้าง "ตกไปหาเจ้าของ" ไม่จริง)

- หลักฐาน: `Accounting/Helpers/RecurringStaffNotice.cs:9-10,18-19` (คืน null = "ตามตารางแจ้งเตือน หรือค่าตั้งต้นของเหตุการณ์: เจ้าของบริษัท") · `Accounting/Helpers/NotificationAudiencePolicy.cs:156-182` — `recurring.run_failed` ไม่อยู่ใน `BellOnlyKeys` (มีแต่ `RecurringUpcomingRun`) / `EmailDefaultKeys` / `MigratedBroadcastKeys` ⇒ `DefaultMatrix` คืนว่าง · `Accounting/Services/Implementations/NotificationEngine.cs:~75,97` — `savedRows == 0` + ไม่มีผู้รับระบุตัว ⇒ `settings.Count == 0` ⇒ `return`
- สถานการณ์: พนักงานที่ตั้งรายการประจำลาออก (กรณีเดียวกับที่ทำให้เกิด `RECURRING-APPROVER-GONE`) · บริษัทไม่เคยตั้งแถวของคีย์ `recurring.run_failed` (ค่าตั้งต้นของเกือบทุกบริษัท) ⇒ ทุกรอบค้างร่าง ⇒ `NotifyStuckDraftAsync` และ `RecordRunFailureAsync` เรียก `DispatchAsync` แล้วไม่มีใครได้รับ · ก่อนแพ็กนี้อดีตพนักงานได้รับ (ผิดคน) — ตอนนี้ไม่มีคนได้รับ = B-M1 ("แจ้งทันที") ไม่เกิดในกรณีที่มันถูกเขียนมาเพื่อ
- เทสต์ที่มี (`RecurringNoticeRound258Tests` "ทิศตรงข้าม_ผู้สร้างออกจากบริษัทแล้ว…") ยืนยันแค่ว่า helper คืน null — ผ่านทั้งที่ไม่มีใครถูกแจ้ง
- แก้ขั้นต่ำ: เพิ่ม `NotificationEvents.RecurringRunFailed` ใน `EmailDefaultKeys` (กระดิ่ง + อีเมลถึงเจ้าของ) + Db test: ผู้สร้างไม่ใช่สมาชิก · ไม่มีแถวตั้งค่า ⇒ เจ้าของได้กระดิ่ง 1 รายการ

### R-M2 — กฎ "เกินกำหนด" ที่ส่งซ้ำทุก 2 หรือ 3 วัน: ย้อนเก็บครั้งแรก + ครั้งซ้ำของวันนี้ = ลูกค้าได้ทวงสองฉบับพร้อมกัน

- หลักฐาน: `Accounting/Helpers/ScheduleCatchUp.cs` `DueRuleCatchesUp` กันเฉพาะ `repeatEveryDays == 1` · `Accounting/Services/Implementations/EmailScheduleService.cs` `ScanDayAsync` — รอบย้อนเก็บ (`catchUp`) ใช้กุญแจ `overdue-{Offset}` · รอบวันนี้ (`repeating`) ใช้ `OverdueReminderSchedule.SendToday` + กุญแจ `overdue-{Offset}-{yyyyMMdd}` · ทั้งสองแถวใช้ `sendUtc = ScheduledUtc(realToday, SendAtHour)` เดียวกัน
- สถานการณ์: กฎ Offset 3 · ส่งซ้ำทุก 2 วัน · ระบบไม่ได้สแกน 2 วันเต็ม (ลายน้ำ = วันนี้−3) · ใบ X ครบ "ครั้งแรก" ในวันที่พลาด d = วันนี้−2 ⇒ ย้อนเก็บ d เข้าคิวครั้งแรก · สแกนวันนี้: `overdueDays = Offset + 2` ⇒ `past % 2 == 0` ⇒ ครั้งซ้ำที่ 1 เข้าคิวด้วย (เมื่อกฎเปิดส่งซ้ำก่อนวัน d) ⇒ อีเมล/LINE ทวงสองฉบับในชั่วโมงเดียวกัน · N = 3 เกิดเมื่อพลาด 3 วัน (`MaxDays = 3` รวมวันนี้−3)
- เทสต์ที่ขาด: `QueueRecoveryRound258Tests` "เกินกำหนด_ย้อนได้_ยกเว้นกฎที่ทวงซ้ำทุกวัน" ไม่มีเคส N = 2/3
- แก้ขั้นต่ำ: ใน `DueRuleCatchesUp` สาขา Overdue — `repeatEveryDays > 1 && (todayBkk - day).Days % repeatEveryDays == 0` ⇒ ไม่ย้อน **หรือ** (ดีกว่า เพราะครั้งซ้ำมีเงื่อนไข `RepeatActivatedAt`) ให้รอบวันนี้ข้ามใบที่ "วันเตือนครั้งแรก" อยู่ในวันที่ย้อนเก็บของรอบเดียวกัน + เทสต์สองทิศ

### R-M3 — ตัวกู้แถวค้างแยก "ยังไม่เคยลองส่ง" กับ "กำลังส่งแล้วตาย" ไม่ได้ และ heartbeat ไม่มีเจ้าของ

- หลักฐาน: `EmailScheduleService.ProcessPendingQueueAsync` — จอง 50 แถวด้วย `UpdatedAt = now` แล้ว heartbeat ทีละแถว "ก่อนส่ง" ด้วยเงื่อนไข `Status == Sending` อย่างเดียว · `RecoverStaleSendingAsync` ใช้ `(UpdatedAt ?? CreatedAt) < cutoff` · `RetryFailedAsync` อ่านแล้วเขียน (ไม่ใช่คำสั่งเดียวแบบมีเงื่อนไข)
- (ก) deploy/ปิดระบบกลางแบตช์ (กรณีปกติ ไม่ต้องมีหลายเครื่อง): แถวที่ **ยังไม่ถึงคิว** (สูงสุด 49 แถว) ถูกประทับ `Failed` พร้อมข้อความ "ไม่ทราบว่าอีเมลออกไปแล้วหรือยัง: ตรวจกับผู้รับก่อน" ทั้งที่ระบบรู้ได้ว่าไม่เคยส่ง ⇒ ใบกำกับของงวดค้างจนกว่าคนจะกด "ลองใหม่" ทีละแถว (ไม่มีปุ่มรวม) และข้อความชี้สาเหตุที่ไม่ได้ตรวจจริง (หลักการ F2 ข้อ 7)
- (ข) หลายเครื่อง + แบตช์ช้ากว่า 30 นาที (SMTP ค้าง 100 วิ × 20 แถว): เครื่อง B กู้แถวที่เครื่อง A ยังไม่ถึง ⇒ `Failed` · A ข้ามแถวนั้น (ถูก — ไม่ส่งซ้ำ) แต่ผลเดียวกับ (ก)
- (ค) ส่งซ้ำจริง (ช่องแคบ): แถว R ของ A ถูก B กู้ ⇒ คนกด "ลองใหม่" ⇒ `Pending` ⇒ B จอง R (`Sending`) และเริ่มส่ง ⇒ A มาถึง R: heartbeat เจอ `Status == Sending` (ของ B) ⇒ ผ่าน ⇒ ส่งอีกฉบับ
- แก้ขั้นต่ำ: คอลัมน์ `SendStartedAt` (heartbeat เขียน · ตัวจองล้าง) — ตัวกู้: `SendStartedAt IS NULL` ⇒ คืน `Pending` (ปลอดภัย ไม่เคยเรียกผู้ให้บริการ) · มีค่า ⇒ `Failed` ตามคำตัดสินเดิม · heartbeat เพิ่มเงื่อนไขเจ้าของ (`UpdatedAt == ค่าที่ตัวเองจองไว้` ตัดเป็นไมโครวินาที หรือคอลัมน์ `ClaimToken`) · เทสต์ Db: แบตช์ 3 แถวตายหลังแถวแรก ⇒ 2 แถวหลังกลับเป็น `Pending`

---

## LOW

| # | เรื่อง | หลักฐาน | แก้ขั้นต่ำ |
| --- | --- | --- | --- |
| R-L1 | ด่านเซิร์ฟเวอร์ของ `create-document?approve=true` อ่อนกว่าธง `canAutoApprove` — ลายมือ / รายการที่อาจเป็นสินทรัพย์ / ไม่มีผู้ขาย-ยอดรวม กันแค่ด้วยการซ่อนช่องติ๊ก ⇒ ผู้เรียกตรง (คีย์/สคริปต์) ยังอนุมัติรวดได้ (258-2-8 ตัดสินไว้แล้วว่าคงพฤติกรรม — จดเป็นความเสี่ยงคงค้าง) | `OcrController.cs:466-472` ใช้ `Evaluate` 4 ช่อง · `OcrPostingReadiness.EvaluateUnseen` ใช้เฉพาะ `OcrService.cs:10674` | คำขอจากคีย์ (`OwnerActionGuard.IsApiKeyRequest`) ใช้ `EvaluateUnseen` กับ `scanBefore` |
| R-L2 | ปุ่ม "สร้าง + อนุมัติ" ในหน้าตรวจยังกดได้กับใบที่เซิร์ฟเวอร์จะไม่อนุมัติ (ข้อผิดพลาดตามสรรพากร/เกรด D) ⇒ ได้ Draft + เหตุหลังกด — ทิศตรงข้ามของกฎเหล็ก #3 (ไม่เงียบ แต่ผู้ใช้เสียหนึ่งรอบ) | `document-scan.html:3792` | ปิดปุ่ม + แสดง `autoApproveBlockedReason` จาก DTO (เซิร์ฟเวอร์ตัดสิน) |
| R-L3 | `NotificationSettingMerge` ตัดสินต่อแถว แต่เครื่องแจ้งเตือนอ่านต่อเหตุการณ์ (`Any`) — ผู้เรียกส่ง `false` บางแถว + ไม่ส่งแถวอื่น (หรือมีแถวบทบาทเก่าที่ไม่อยู่ในแคตตาล็อก) ⇒ กลุ่มยังประกาศ ขณะที่หน้าแสดง "ปิด" (หน้าคำนวณจากบทบาทในแคตตาล็อกเท่านั้น) | `NotificationConfigService.cs:74,88` · `NotificationEngine.cs:103` · `notifications.html:163` | แถวใดของเหตุการณ์ส่งค่าชัดเจน ⇒ เขียนค่านั้นลงทุกแถวที่เก็บไว้ของเหตุการณ์ |
| R-L4 | GET `rules` / `queue` ยังเปิดให้สมาชิกทุกคน: เห็น `BccEmails` · เนื้อเทมเพลต · `ToEmail` + หัวเรื่องของคิว (รวมสลิปเงินเดือน/50 ทวิ) | `EmailScheduleController.cs` `GetRules` · `GetQueue` | ซ่อน `BccEmails`/`ToEmail` (mask) เมื่อไม่มี `CompanySettings.Edit` หรือใส่ด่านอ่าน |
| R-L5 | `canRetry`/`canCancel` ไม่รวมสิทธิ์ ⇒ ผู้ไม่มี `CompanySettings.Edit` เห็นปุ่มแล้วได้ 403 (มีข้อความ ไม่เงียบ) · หน้ากฎไม่ล็อกปุ่มแก้/ลบให้นักบัญชีที่เคยจัดการกฎได้ | `EmailScheduleController.cs` `GetQueue` · `email-schedule.html` | ส่งธง `canManage` จากเซิร์ฟเวอร์แล้วล็อกปุ่ม + บอกเหตุ |
| R-L6 | webhook 1:1: ล้มหลังเกิดผลข้างเคียง (เอกสารสร้างแล้ว แล้วขั้นตอบโยน) ⇒ ถอนประทับ ⇒ Redelivery (อัตโนมัติถ้าเปิดไว้ใน console และคำขอแรกหมดเวลา) สร้างใบที่สอง | `LineWebhookController.cs` `HandleOneToOneEventAsync` catch | ถอนประทับเฉพาะเมื่อยังไม่เข้า handler (ย้าย `ReplyAsync` ออกนอก try ที่ถอนประทับ) |
| R-L7 | คนแปลกหน้ายิงรัว: ไม่เกิด push (ถูก) แต่ทุก event ยังเขียน `LineWebhookEventsSeen` + ตัวนับ ก่อนถึงเพดาน — ตารางโตไม่จำกัดถ้าไม่มีงานล้าง | `LineWebhookController.cs` · `ContactLineGroupService.cs:32-40` | ยืนยันว่ามีงานล้างแถวเก่า (ไม่พบในแพ็กนี้) |
| R-L8 | `LastCatchUpNote` ไม่ถูกล้าง ⇒ บรรทัด "ส่งย้อนหลัง" ค้างใต้กฎตลอดไป · ใบแจ้งยอดข้ามเดือน (ตั้ง 26-28 + หยุดข้ามสิ้นเดือน) ยังหาย (จดไว้ใน 258-2-5 แล้ว) | `EmailScheduleService.StampScanDayAsync` · `ScheduleCatchUp.Monthly` | ล้างบันทึกเมื่อพ้น N วัน · ข้ามเดือน = งานตามที่จดไว้ |
| R-L9 | หัวเรื่องแจ้งค้างร่างแสดง `(DRAFT-{guid})` — ใบร่างยังไม่มีเลขจริง | `RecurringStaffNotice.StuckDraftTitle` · `RecurringTransactionService.cs` (ส่ง `result.DocumentNumber`) | ไม่ส่งเลขเมื่อขึ้นต้น `DRAFT-` |
| R-L10 | audit ตอน "ผูก" ยังใช้ `_db.AuditLogs.Add` (ไม่เข้าลูกโซ่) ขณะที่ตอน "ถอด" ที่เพิ่มรอบนี้ใช้ `AddChainedAuditLog` — คู่สมมาตรแก้ฝั่งเดียว (ของเดิม) | `LineBotService.cs:~198` | เปลี่ยนเป็น `AddChainedAuditLog` |

---

## ตรวจแล้วไม่ใช่บั๊ก (ไม่ต้องรายงานซ้ำ)

- **`SettingsWriteGate.EvaluateAsync` เมื่อ `HttpContext`/บริการสิทธิ์เป็น null** ⇒ `allowed = false` ⇒ ปฏิเสธ (ปิดทางปลอดภัย) · คีย์ที่ถูกห้ามไม่ถามสิทธิ์
- **ทางเข้าอื่นของกฎตารางส่ง**: `UpsertRuleAsync`/`DeleteRuleAsync` มีผู้เรียกเดียว (`EmailScheduleController`) · ไม่มี `EmailScheduleRules.Add` นอก service · `RuleRequest` ไม่รับ `LastDayScanBkk`/`LastCatchUpNote` (ไม่มี mass-assignment)
- **คิวอีเมล retry/cancel รับ API key** = ตาม 258-2-1 (ส่งซ้ำไปผู้รับเดิม ไม่เปลี่ยนปลายทาง)
- **แถวที่ถูกกู้/ยกเลิก/ลองใหม่ ก่อนตัวส่งเดิมมาถึง** ⇒ heartbeat ไม่ตรง ⇒ ข้าม (ไม่ส่งซ้ำ) — ยกเว้นช่อง R-M3(ค)
- **สองเครื่องสแกนตามวันพร้อมกัน**: กุญแจกันซ้ำ + unique index ⇒ เครื่องหนึ่งล้มทั้ง transaction แล้วรอบถัดไปเห็นแถวเดิม · ไม่เกิดแถวซ้ำ (ไม่มี `JobLock` บน `EmailScheduleWorker` เป็นของเดิม — ไม่ได้เพิ่ม BackgroundService ใหม่)
- **`LastDayScanBkk` เป็น `timestamp`** + `Npgsql.EnableLegacyTimestampBehavior` ⇒ วันที่ไทย (Kind ไม่ระบุ) ไป-กลับตรงตัว · ขอบวัน (สแกน 23:30 แล้ว 00:30) ไม่เกิดวันพลาดปลอม · กฎใหม่/แก้ตาราง/เปิดกฎกลับ ⇒ ไม่ย้อน
- **ตัวจำกัดคนที่ยังไม่ผูก** ใช้ `ChatRateLimiter` (upsert ในฐาน — ไม่ใช่ static/IMemoryCache) · fail-open ไม่ทำให้เกิด push (คำตอบออกทาง reply token) · push ถอยเฉพาะ `Users.LineUserId` ที่ผูกแล้ว
- **OCR C-H2**: `GetResultAsync` → `AttachComplianceAsync` เติม `ComplianceIssues` (ไม่ null) + `Quality` ⇒ ใบสะอาดยังอนุมัติรวดได้ (กฎเหล็ก #3 ไม่ถดถอย) · `canAutoApprove` ต่อแถวเป็นการคำนวณ pure บนผลที่คำนวณอยู่แล้ว (ไม่มีคิวรีเพิ่ม) · ช่องระหว่างอ่าน `scanBefore` กับสร้างเอกสารแคบและทิศปลอดภัย
- **OCR โควตา**: `/scan/{id}` หัก-คืนด้วย `OcrQuotaRefundRule` ตัวเดียวกับ `/upload` · โยน exception = คืน · ปุ่ม "แกะใหม่" ของหน้าเว็บไม่ใช้เส้นนี้ (ไม่ถูกหักซ้ำ)
- **XSS**: `ai-tools.html` ผ่าน `Layout.esc` ทุกค่าจากกระดาษ · `line-bind.html` ใช้ `textContent` + ตรวจ scheme `https://line.me/` · `email-schedule.html` `lastCatchUpNote` ผ่าน `Layout.esc`
- **ของที่ถอด**: `setMyLineBinding` (api.js) · ตารางใน `settings.html` · `createDocFromOcr` — ไม่เหลือผู้เรียก/ลิงก์ค้าง
- **Tenant**: คิวรีใหม่กรอง `CompanyId` ครบ (`StampScanDayAsync` · `LineRowQueuedAsync` · `CancelPendingAsync` · `AutopilotService`) · `RecoverStaleSendingAsync` ข้ามบริษัทโดยตั้งใจ (งานเบื้องหลัง · แบบเดียวกับการจองคิว)
- **Migration**: `ADD COLUMN IF NOT EXISTS` ×2 — รันซ้ำได้ · **Db tests** ทั้งสองไฟล์ใหม่มี `[Trait("Category", "Db")]`
- **เทสต์ด่าน**: `SettingsWriteGateRound258Tests` มีเทสต์ระดับ controller (403 + บริการไม่ถูกเรียก) และกติกา call-site + REVIEWER_CASES (ถอดด่านแล้วฟ้อง) — ไม่ใช่เทสต์ที่เรียกแค่ helper

## เทสต์ที่ควรเพิ่มพร้อมงานตาม

1. คีย์ API → `issue-bind-code` = 403 (R-H1)
2. Db: ผู้สร้างไม่ใช่สมาชิก + ไม่มีแถวตั้งค่า ⇒ เจ้าของได้กระดิ่ง (R-M1)
3. pure + Db: กฎเกินกำหนดส่งซ้ำทุก 2 วัน · พลาด 2 วัน ⇒ ใบเดียวได้แถวคิวเดียว (R-M2)
4. Db: แบตช์ตายกลางทาง ⇒ แถวที่ยังไม่ถึงกลับเป็น `Pending` · แถวที่เริ่มส่งแล้วเป็น `Failed` (R-M3)
