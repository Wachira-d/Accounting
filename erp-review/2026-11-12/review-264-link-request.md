# ฝ่ายค้าน รอบ 264 — คำขอเชื่อมคู่ค้าถึงปลายทางจริง (050e98d1 · 1133b0ea)

ขอบเขต: `Helpers/PartnerLinkRequestRules.cs` · `PartnerLinkRequestMigration.cs` · `CrossTenantWorkflowService(.LinkRequests).cs` ·
`CrossTenantWorkflowController.cs` · `PartnerCompanyMatch.InviteTargets` · `NotificationEvents`/`NotificationAudiencePolicy`/`NotificationEngine` (CompanyAdmin) ·
`AutopilotJob` ขั้น PartnerLinkRequests · `partners.html` · `layout.js` กระดิ่ง · คำตัดสิน 264-1-1…8
อ่านโค้ดอย่างเดียว (ไม่ได้คอมไพล์/รัน) · ทุกข้อด้านล่างตามถึงบรรทัดจริงแล้ว

สรุป: **0 HIGH · 4 MED · 8 LOW**

---

## MED

### M1 — เช็กการมีอยู่ของเลขภาษีได้ผ่านการชน unique index เมื่อส่งพร้อมกัน (anti-enumeration 264-1-2 รั่ว)
`Services/Implementations/CrossTenantWorkflowService.cs:91-117,160-163` · `Data/AccountingDbContext.cs:2345` (`HasIndex(CompanyAId, CompanyBId).IsUnique()`)

- เส้นที่ "มีบริษัทปลายทาง" ทำ `FirstOrDefaultAsync(existing)` → `Add(new TradingPartnership)` → `SaveChangesAsync`; เส้นที่ "ไม่มี" ไม่ insert แถวคู่เลย
- A ยิง `POST invite` ถึงเลข T พร้อมกัน 3–5 ครั้ง (แท็บ/สคริปต์): ถ้า T มีอยู่ ทั้งหมดเห็น `existing == null` แล้ว insert ⇒ ตัวที่สองขึ้นไปได้ `DbUpdateException` (unique) ⇒ **500** และ**ไม่ถูกเติมเวลา** (`Task.Delay` อยู่หลัง SaveChanges ไม่ได้อยู่ใน `finally`) · ถ้า T ไม่มี ⇒ ได้ 202 ทุกครั้ง ⇒ แยก "มี/ไม่มี" ได้ด้วยรหัสสถานะและเวลา
- ต้นทุนผู้โจมตี: ~3–5 จาก 20 ครั้ง/วัน ต่อเลข · บริษัทหลายรายรวมกันไล่ได้เรื่อย ๆ (และ B ได้แจ้งเตือนแค่ฉบับเดียว — แทบไม่รู้ตัว)
- แก้ขั้นต่ำ: (1) ใน `InvitePartnerAsync` จับ `DbUpdateException` ที่เป็น unique violation ของคู่ ⇒ ถือเป็น `Unchanged` (detach แถวแล้ว Save ส่วนที่เหลือ) หรือ serialize ด้วย `pg_advisory_xact_lock` ต่อคู่ก่อนอ่าน `existing` · (2) ย้ายการเติมเวลาไป `try/finally` ให้ทุกทาง (รวม exception) ใช้เวลาเท่ากัน · เทสต์ Db: ส่งพร้อมกัน 2 ครั้งถึงบริษัทที่มีอยู่ ⇒ ทั้งคู่ 202

### M2 — ชื่อบริษัทผู้ขอ = ข้อความอิสระที่ผู้ขอคุม ไปถึงอีเมล (SMTP ของ B เอง) + LINE OA กลาง ⇒ ช่องฟิชชิงที่ 264-1-4 บอกว่าปิดแล้ว
`Helpers/PartnerLinkRequestRules.cs:90-97` (`NoticeMessage` — `Clip(requesterName, 120)`) · `NotificationEngine.cs` `DispatchEmailAsync`/`DispatchLineAsync`

- 264-1-4 ตัดข้อความเชิญอิสระออกจากการแจ้ง "เพราะเป็นช่องฟิชชิง" แต่ `Company.Name` ของผู้ขอก็เป็นข้อความอิสระ 120 ตัวอักษรที่ใครสมัครบริษัทก็ตั้งได้ (ไม่มีฟิลด์ยืนยันทะเบียนบน `Company`) — เช่น `"กรมสรรพากร แจ้งด่วน: บัญชีถูกระงับ โทร 08x-xxx ภายในวันนี้"` ⇒ ไปถึงเจ้าของ/ผู้ดูแลของบริษัทเหยื่อทางอีเมลที่**ส่งจาก SMTP ของบริษัทเหยื่อเอง** (ดูเป็นอีเมลภายใน) และ LINE ของ NextAcc · ผู้ขอบริษัทเดียวส่งได้ 20 เลข/วัน, บริษัทหลายรายรวมกันได้ 10 ฉบับ/วัน/เหยื่อ
- กระดิ่ง/อีเมล encode HTML ถูกต้องแล้ว (ไม่ใช่ XSS) — ปัญหาคือเนื้อหา
- แก้ขั้นต่ำ: ในช่องนอกแอป (อีเมล/LINE) อย่าใส่ชื่อ — ใส่เฉพาะเลขภาษี + สาขาของผู้ขอ (ระบบคำนวณ) แล้วให้อ่านชื่อบนการ์ด (มีป้าย "ระบบไม่ได้ยืนยัน") · หรือตัดชื่อเหลือ ~60 ตัว + ลบตัวเลขยาว/URL/เบอร์โทรด้วยตัวกรองกลาง และใส่คำว่า "(ชื่อที่ผู้ขอตั้งเอง — ระบบไม่ได้ยืนยัน)" ในข้อความ

### M3 — เพดาน 10/24 ชม. ถูกใช้ "ปิดเสียง" คำขอจริงได้ถาวร (SkipCapped ไม่เคยแจ้งอีก)
`CrossTenantWorkflowService.LinkRequests.cs:204-218` · `PartnerLinkRequestRules.cs:116-122`

- แถวที่ได้ `SkipCapped` ถูกประทับ `InviteNotifiedAt = now`, `InviteNoticeDueAt = null` ⇒ ไม่มีวันถูกแจ้งอีก แม้วันถัดไปเพดานว่างแล้ว
- ผู้โจมตีมี 10 บริษัท (สมัครฟรี) ยิงคำขอถึง B ตอนต้นวัน ⇒ คำขอของคู่ค้าจริงที่มาหลังจากนั้นทั้งวันไม่ถูกแจ้งเลย (อยู่แค่ในตารางหน้าคู่ค้า ซึ่งคือสภาพบั๊กเดิมที่รอบนี้ตั้งใจแก้ "B หาไม่เจอ") · ทำซ้ำทุกวันได้ (ปฏิเสธแล้วพัก 7 วันต่อคู่ แต่ผู้โจมตีหมุนบริษัทได้)
- แก้ขั้นต่ำ: SkipCapped ⇒ เลื่อน `InviteNoticeDueAt` ไปเวลาที่แถวเก่าสุดใน 24 ชม. หลุดหน้าต่าง (ไม่ประทับ NotifiedAt) จนกว่าจะหมดอายุ · หรือรวมเป็น "สรุปคำขอ N รายการ" ฉบับเดียวต่อวันเมื่อเกินเพดาน · เทสต์: เกินเพดานวันนี้ ⇒ พรุ่งนี้ได้แจ้ง

### M4 — ปฏิเสธ (reject) ไม่มีด่านสถานะ/ฝั่ง และไม่ audit — ใช้ตัดคู่ที่ "เชื่อมแล้ว" ได้โดยไม่มีหลักฐานการถอนความยินยอม
`CrossTenantWorkflowService.cs:262-278` (`RejectPartnershipAsync`) · controller `Reject`

- ไม่ตรวจ `Status` (รับทั้ง Accepted/Suspended/Pending) และไม่ตรวจว่าเป็นผู้ขอหรือผู้รับ · ไม่มี `AddChainedAuditLog` ทั้งที่ accept เขียน audit เป็น "หลักฐานความยินยอม PDPA ม.19" (264-1-6) — การถอนความยินยอม/ตัดการเชื่อมต้องมีหลักฐานคู่กัน
- ตัวอย่าง: ผู้ใช้ที่มีสิทธิ์ตั้งค่าบริษัทเรียก `POST trading-partners/{id}/reject` กับคู่ที่ Accepted ⇒ คู่กลายเป็น Rejected เงียบ ๆ (ข้ามเส้น `Suspend` ที่มี audit + ข้อความ) · ผู้ขอที่ได้ id จาก `EntityId` ของแจ้งเตือนตอบรับก็ "ปฏิเสธ" คู่ของตัวเองได้ · UI ซ่อนปุ่ม แต่ API ไม่ซ่อน
- แก้ขั้นต่ำ: reject ได้เฉพาะ `Status == Pending && InvitedByCompanyId != rejectingCompanyId` (อื่น ๆ ⇒ 409 พร้อมทางไปต่อ "ใช้ ระงับการเชื่อม") + เขียน audit `TradingPartnerLinkRejected` (RuleCode/LegalReference เดียวกัน · เหตุผลเก็บฝั่งผู้ปฏิเสธเท่านั้น) · เทสต์ทิศตรงข้าม: reject คู่ Accepted ⇒ 409

---

## LOW

### L1 — ส่งซ้ำก่อนคู่หมดอายุ ⇒ แถว "คำขอที่เราส่ง" ใหม่โชว์ "รอตอบ" อีก 30 วัน แต่ B ยอมรับไม่ได้หลังวันหมดอายุของคู่เดิม
`CrossTenantWorkflowService.cs:120-137` (Unchanged ไม่ขยับ `InvitedAt`) · `LinkRequests.cs:292-311`
A ส่ง R1 วันที่ 0, R2 วันที่ 25 ⇒ คู่หมดอายุวันที่ 30 (B เห็น "คำขอหมดอายุ" + 409) แต่ R2 ของ A ยังเป็น "ส่งแล้ว — รอคู่ค้าตอบรับ" ถึงวันที่ 55 — คำขอที่ A คิดว่าสดตายไปแล้ว 25 วัน · ไม่รั่วสมาชิกภาพ (ตัวเลขฝั่ง A คิดจากแถวของ A) แต่เป็นคลาส "ค่าที่แต่งขึ้น" · แก้: Unchanged ที่ยัง Pending ⇒ ต่ออายุ `InvitedAt = now` (ไม่แจ้งซ้ำ) ให้ตรงกับแถวคำขอใหม่

### L2 — ยอมรับแล้วประทับ "เชื่อมแล้ว" ให้คำขอไม่ระบุสาขาที่ไม่ได้ส่งถึงผู้ยอมรับ
`CrossTenantWorkflowService.cs:228-235` (`r.PartnerBranchCode == null || …`)
เลข T มีสำนักงานใหญ่ H และสาขา B(00001) เป็นคนละบริษัท: R1 (T, ไม่ระบุสาขา) ⇒ `InviteTargets` ส่งถึง H เท่านั้น · R2 (T, 00001) ⇒ B · B ยอมรับ ⇒ R1 ก็ถูกประทับ Accepted (`AcceptedPartnershipId` = คู่ของ B) ⇒ A เข้าใจว่า H ตอบรับ · แก้: เงื่อนไขสาขาว่างต้องเป็น "ผู้ยอมรับคือคนที่ `InviteTargets(candidates, null)` จะเลือก" หรือประทับเฉพาะ `r.Id == linkRequestId` + แถวที่สาขาตรงกันเป๊ะ

### L3 — คำขอถึงคู่ที่เชื่อมแล้ว/ที่ B เชิญเรามาก่อน ⇒ "รอตอบ" แล้วกลายเป็น "หมดอายุ — คู่ค้าไม่ได้ตอบรับ" ทั้งที่เชื่อมกันอยู่
`CrossTenantWorkflowService.cs:137` (Unchanged:Accepted / Pending ที่ B เชิญ) + `PartnerLinkRequestRules.OutgoingLabel` · ข้อความหลอก (A รู้อยู่แล้วว่าเชื่อม ⇒ ไม่รั่ว) · แก้: ตอนเขียนแถวคำขอ ถ้าคู่ Accepted อยู่แล้ว ⇒ ประทับ AcceptedAt ทันทีได้ (A เห็นคู่ในตารางอยู่แล้ว จึงไม่รั่ว) · กรณี Pending ที่ B เชิญ ⇒ ปล่อยไว้ได้ แต่ป้ายควรชี้ "มีคำขอจากคู่ค้ารอเรายอมรับในตาราง"

### L4 — แจ้ง "คู่ค้าตอบรับแล้ว" ยืมคีย์ `autopilot.partner_exchange` (ความหมาย "ยกเลิก 50 ทวิ") — บริษัทที่ปิดคีย์นั้นไม่รู้ว่าใครรับคำขอ
`CrossTenantWorkflowService.cs:248-259` · `NotificationEvents.cs` (ป้ายของคีย์ = "คู่ค้า NextAcc ยกเลิกหนังสือรับรอง 50 ทวิ…")
กลไกกันบริษัทเลขซ้ำของ 264-1-3 ("ผู้ขอตรวจชื่อ/เลข/สาขาผู้ยอมรับได้") พึ่งการแจ้งนี้ แต่ผู้ใช้ปิดคีย์ 50 ทวิ ⇒ ปิดการตรวจนี้ไปด้วยโดยไม่รู้ตัว · ค่าตั้งต้นของคีย์นั้นยังประกาศเข้ากลุ่ม LINE ของบริษัท (EmailDefaultKeys) ต่างจากที่ 264-1-4 ตั้งใจกันไว้ · เข้าข่าย §4E "ห้าม reuse ผิดความหมาย" · แก้: คีย์ใหม่ `partner.link_accepted` (Owner + CompanyAdmin · ไม่เข้ากลุ่ม LINE)

### L5 — บริษัทที่ตั้งเลขภาษีของคนอื่นได้รับคำขอที่ตั้งใจถึงบริษัทจริง และการ "ตรวจชื่อ/เลข/สาขา" กันไม่ได้ถ้าลอกชื่อมาด้วย
`PartnerCompanyMatch.InviteTargets` · `PartnerLinkRequestRules.AcceptedNoticeMessage`
`Company` ไม่มีฟิลด์ยืนยันเลขภาษี/ทะเบียน · C ที่ลอกทั้งชื่อ+เลข+สาขาของ B ได้คำขอของ A (รู้ว่า A อยากค้ากับ B) และถ้ายอมรับ ข้อความถึง A เหมือน B ทุกตัวอักษร · กรณี C เป็นผู้ถือเลขรายเดียว (B ไม่ได้ใช้ NextAcc) มีอยู่แล้วก่อนรอบนี้ (C เปิด PartnerDiscoverable เองได้) — รอบนี้เพิ่มเฉพาะกรณีกำกวม (เดิมทิ้ง) · แก้ระยะสั้น: ข้อความตอบรับถึง A บอกตรง ๆ ว่า "ระบบไม่ได้ยืนยันว่าบริษัทนี้เป็นเจ้าของเลขภาษีจริง" + ค่าตั้งงานอัตโนมัติต่อคู่ (P1 consent) ต้องให้ A ยืนยันคู่เองก่อนเปิด · ระยะยาว: ยืนยันเลขภาษีของบริษัท (DBD/RD) แล้วแสดงป้าย "ยืนยันแล้ว"

### L6 — เลขภาษีผู้ขอส่งเต็มทางอีเมล/LINE (ผู้ขอที่เป็นบุคคลธรรมดา = เลขบัตรประชาชน)
`PartnerLinkRequestRules.cs:93` · การ์ด `GetIncomingLinkRequestAsync:283` · กฎเหล็ก #2 J (mask เลข 13 หลัก)
ผู้ขอเปิดเผยเองจึงมีฐานทางกฎหมาย แต่ช่องนอกแอปเก็บถาวรในกล่องจดหมาย/แชท · แก้: ช่องอีเมล/LINE แสดงแบบ mask (`1-XXXX-XXXXX-XX-3`) · เต็มเฉพาะบนการ์ดในแอป

### L7 — ตัวกรอง "path ภายใน" ของกระดิ่งเป็นสำเนาฝั่ง JS ที่หลวมกว่าเซิร์ฟเวอร์
`wwwroot/js/layout.js:2551` เทียบ `Helpers/NotificationLink.cs:25`
JS ไม่กัน `\t`/`\n` (ตัวแยก URL ของเบราว์เซอร์ลบทิ้งก่อนแยก ⇒ `"/\t/evil.example"` กลายเป็น `//evil.example`) และไม่กัน `\` ที่อยู่กลางสตริง ซึ่งเซิร์ฟเวอร์กัน (`Contains('\\')`) · ตอนนี้ไม่มีผู้เขียน `ActionUrl` ที่ผู้ใช้คุมได้ (ตามที่หาเจอ) ⇒ เป็นการป้องกันเผื่อไว้ + ผิด F2 ข้อ 5 · แก้: API กระดิ่งคืน `href` ที่ผ่าน `NotificationLink` (หรือ `IsInternalPath` ตัวเดียว) แล้ว JS ใช้ตามนั้น

### L8 — ผู้รับ `CompanyAdmin` ไม่ตรงด่านสิทธิ์จริง + การแจ้งที่ล้มกลางทางทำให้ส่งซ้ำ + ไม่มีปุ่มบล็อกผู้ขอ
- `NotificationEngine.cs:246` ใช้ `ResolveByPermissionOrRoleAsync(…, UserRole.Owner)` แต่ด่าน `RequirePermission` ให้ผ่านทั้ง Owner **และ SystemAdmin** (`BuiltInRolePermissions.GrantsAll`) ⇒ คนที่กดยอมรับได้บางคนไม่ได้รับแจ้ง (ไม่รั่ว — ขาดผู้รับ) · แก้: ใช้ `BuiltInRolePermissions.Grants` เป็นตัวตัดสินเดียวกัน
- `DispatchSystemAsync` เซฟกระดิ่งทีละคน · คนที่ 2 ล้ม ⇒ ผล `Failed` ⇒ ลองใหม่ทุก 30 นาทีนาน 48 ชม. ⇒ คนแรกได้กระดิ่ง/อีเมล/LINE ซ้ำทุกครั้ง และ entity ที่ล้มยังค้างใน ChangeTracker ทำให้ `SaveChangesAsync` ใน `LinkRequests.cs:245` ล้มตาม ⇒ ทั้ง batch หยุด · แก้: ถือ "ส่งได้อย่างน้อยหนึ่งช่อง" เป็น Delivered (หรือ detach แถวที่ล้ม) และใช้ DbContext/scope แยกต่อแถว
- B ปฏิเสธได้แค่ "พัก 7 วัน" แล้วผู้ขอส่งใหม่ได้เรื่อย ๆ — ควรมีปุ่ม "ปฏิเสธและไม่รับคำขอจากบริษัทนี้อีก" (เก็บฝั่ง B · ผู้ขอยังเห็น "รอตอบ" ตาม 264-1-2)

---

## ตรวจแล้วไม่ใช่บั๊ก
- คำตอบ invite: 202 + `InviteAckMessage` คงที่ · audit ของผู้ขอรูปเดียว · rate-limit นับก่อนค้น (ไม่ขึ้นกับปลายทาง) · พักหลังปฏิเสธ 7 วันไม่ส่งสัญญาณถึง A (แถวคำขอของ A เกิดทุกครั้ง · สถานะคิดจากแถวของ A เท่านั้น) — ยกเว้นทางชน unique ใน M1
- Expired กับ Awaiting ฝั่ง A คิดจาก `PartnerLinkRequest.ExpiresAt` ของ A ⇒ ไม่ขึ้นกับว่ามี B
- `GetIncomingLinkRequestAsync` กรอง `CompanyAId/BId == companyId && InvitedByCompanyId != companyId` ⇒ ผู้ขอ/คนนอกได้ 404 ข้อความเดียว · สิทธิ์ `CompanySettings.Edit` ตรงกับปุ่มยอมรับ · การ์ด/ตาราง escape ทุกช่อง (`Layout.esc`)
- อีเมล: title/message ผ่าน `WebUtility.HtmlEncode` · ลิงก์ผ่าน `NotificationLink.Absolute` · LINE เป็น text
- `?company=` ที่ไม่ใช่สมาชิก: `loadCompanies` ถอยไปบริษัทแรก ⇒ การ์ดได้ 404 พร้อมทางไปต่อ (หรือ 403 ชั่วคราวถ้าโหลดก่อน) — ไม่รั่ว
- การเขียนข้ามผู้เช่าตอนยอมรับ (`ExecuteUpdate` บน `PartnerLinkRequests`) ล้อมด้วย `CompanyId == p.InvitedByCompanyId` และอยู่ในธุรกรรม/advisory lock เดียวกับการยอมรับ · แจ้งผู้ขอหลัง commit และไม่เกิดซ้ำเมื่อยอมรับแล้ว
- แถวคู่เดิมหลัง deploy: คอลัมน์ใหม่ NULL ⇒ ไม่แจ้งย้อนหลัง · Pending ที่อายุเกิน 30 วันจาก CreatedAt หมดอายุทันที (ตามคำตัดสิน 264-1-5/8)
- ไม่เข้ากลุ่ม LINE ของบริษัทปลายทาง (DefaultMatrix `EnableLineGroup: false`) · ผู้รับซ้ำ Owner/CompanyAdmin ถูกรวมด้วยคีย์ UserId
