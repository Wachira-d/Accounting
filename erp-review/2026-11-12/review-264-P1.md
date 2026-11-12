# ฝ่ายค้าน รอบ 264 P1 — ความยินยอม "งานอัตโนมัติกับคู่ค้า"

ขอบเขต: 3d6cdb70 · b3a42aa3 · 14d8f706 · 42ddc03e · 6e7b36ff (บน `claude/booking-service-features-sdemob`) · อ่านโค้ดอย่างเดียว ไม่ได้คอมไพล์/รัน

สรุป: HIGH 1 · MED 4 · LOW 8 · ตรวจแล้วไม่ใช่บั๊ก 10 ข้อ (ท้ายไฟล์)

---

## HIGH

### H1 — ระงับแล้วเชื่อมกลับ ความยินยอมเดิมกลับมาใช้ได้เอง (`StaleAfterSuspend` ไม่ทำงานบนเส้นจริง)
- `Helpers/PartnerConsent.cs:243` ตรวจ `partnership.SuspendedAt is DateTime s && s >= row.GrantedAt`
- แต่ทางเดียวที่แถวที่ถูกระงับกลับมาเชื่อมได้คือเชิญใหม่ `CrossTenantWorkflowService.cs:117-128` ซึ่งตั้ง `existing.SuspendedAt = null` (และ Status → Pending) จากนั้นยอมรับที่ `:207-208` (Status = Accepted, `AcceptedAt` = ตอนนี้)
- สิ่งที่เกิด: B ให้ `AutoRecordPayment` (เพดาน 100,000) → B ระงับคู่ (ความยินยอมยังไม่ถูกถอน) → A เชิญใหม่หลังพัก 7 วัน → B กดยอมรับการเชื่อม (คิดว่าแค่เชื่อมเอกสาร) → `SuspendedAt == null` ⇒ ข้ามด่าน ⇒ `CheckAsync` คืน `Ok` ⇒ พอ P3 ต่อสาย ระบบจะบันทึกจ่ายเองด้วยความยินยอมที่ให้ก่อนการระงับ ขัดกับข้อความยินยอมที่เก็บ hash ไว้ ("ต้องให้ความยินยอมใหม่หลังเชื่อมกลับ") และคำตัดสิน 264-2-2
- ซ้ำเติม: ตอนแถวเป็น Pending หน้า `ListAsync` (`PartnerConsentService.cs:98`) กรองเหลือแค่ Accepted/Suspended ⇒ B ไม่เห็นแถวนั้นและถอนไม่ได้ในช่วงนั้น
- เทสต์ไม่จับ เพราะ `PartnerConsentRound264DbTests.cs:176-177` จำลองการเชื่อมกลับด้วย `ExecuteUpdate(Status = Accepted)` ซึ่งยังเหลือ `SuspendedAt` ไว้ ไม่ได้เดินเส้น reinvite+accept จริง
- แก้ขั้นต่ำ: ใน `Allows` เพิ่มเงื่อนไขว่าแถวต้องให้หลังการยอมรับครั้งล่าสุด: `if (partnership.AcceptedAt is DateTime acc && row.GrantedAt < acc) → StaleAfterSuspend` (การให้บังคับว่าคู่ต้อง Accepted อยู่แล้ว ดังนั้นแถวที่ถูกต้องมี `GrantedAt ≥ AcceptedAt` เสมอ) และเก็บเงื่อนไข SuspendedAt เดิมไว้ด้วย · ทางที่ดีกว่าคือถอนแถวที่ยังใช้อยู่ทั้งหมดใน `SuspendPartnershipAsync` (ธุรกรรมเดียวกัน · audit `replaced=false, reason="ระงับคู่"`) · เทสต์ Db ต้องเดิน `SuspendPartnershipAsync` → `InvitePartnerAsync` → `AcceptPartnershipAsync` จริง

---

## MED

### M1 — TOCTOU: ถอนระหว่าง `CheckAsync` กับการเขียนของท่อ ยังเขียนได้
- `PartnerConsentService.cs:65-80` อ่านแบบ `AsNoTracking` ไม่ล็อกอะไร · `WithdrawAsync` (`:274-283`) ก็ไม่ล็อก
- สิ่งที่เกิด (เมื่อ P3 ต่อสาย): ท่อเรียก `CheckAsync` ได้ Ok → ผู้ใช้กดถอน (commit) → ท่อบันทึกจ่าย/อนุมัติ ⇒ งานเกิดหลังถอน ขัดกับข้อความ "ถอนแล้วมีผลทันทีกับงานที่ยังไม่ได้ทำ"
- แก้: ให้ API ตอนใช้มีรูปแบบที่ต้องเรียก**ในธุรกรรมของท่อ**: `SELECT … FROM "PartnerAutomationConsents" WHERE "Id"=@id AND "WithdrawnAt" IS NULL FOR SHARE` ก่อนเขียน (ฝั่งถอน `UPDATE` รอโดยอัตโนมัติ) หรือ advisory xact lock ต่อ (บริษัท, คู่) ใช้ร่วมกันทั้ง Withdraw และท่อ · ล็อกจุดเรียกใน R264-P1 ให้ P3 ต้องเรียกแบบนั้น

### M2 — `CheckAsync` ส่ง `DefaultPayBankAccountId` ที่ปิดใช้งาน/ลบไปแล้วกลับไปให้ท่อ
- `PartnerConsent.cs:318` คืน `row.DefaultPayBankAccountId` เมื่อ Ok · `CheckAsync` ไม่ตรวจว่าบัญชียังเป็นของบริษัท ยัง `IsActive` และไม่ถูกลบ (ตรวจแค่ตอนให้ที่ `ValidateAsync:196`) · หน้า (`:141`) เขียนว่า "บัญชีที่ปิดใช้งานแล้ว" แต่คำตัดสินยังเป็น "ใช้อยู่"
- สิ่งที่เกิด: บัญชีถูกปิดหลังให้ความยินยอม → P3 ประกาศจ่ายจากบัญชีที่ปิดแล้ว (ระบบหยิบบัญชีให้เองโดยที่ไม่มีหลักฐานว่ายังใช้ได้จริง)
- แก้: ใน `CheckAsync` ตรวจบัญชีซ้ำ (`CompanyId`, `IsActive`, `!IsDeleted`) · ใช้ไม่ได้ ⇒ คืน `DefaultPayBankAccountId = null` พร้อมโค้ด/เหตุ (เช่น `PayAccountInactive`) ให้ท่อตกไปใช้หลักฐานเท่านั้น หรือไม่ผ่านไปเลย

### M3 — ความหมายของ `amount` ในการเทียบเพดานไม่ได้กำหนดไว้ (สกุลเงิน · ก่อน/หลัง VAT/WHT · ค่าติดลบ)
- `PartnerConsent.cs:228-251` / `CheckAsync(…, decimal? amount)` ไม่มีเอกสารหรือการตรวจว่ายอดนี้คืออะไร · ข้อความยินยอมพูดถึง "ยอด … บาท"
- สิ่งที่เกิด: P3 ส่งยอดสกุลต่างประเทศ (USD 3,000 ≈ 105,000 บาท) หรือยอดสุทธิหลังหัก WHT 3% เข้ามา จะผ่านเพดาน 100,000 บาทที่ไม่ควรผ่าน · ยอดติดลบหรือ 0 ผ่านเสมอ
- แก้: เปลี่ยนพารามิเตอร์เป็นยอดรวมเงินบาทของใบ (`GrandTotal` เป็นบาท รวม VAT ก่อนหัก WHT · ใบสกุลต่างประเทศใช้ยอดบาทตามอัตราของใบ) ระบุใน doc-comment + DOCUMENT_FLOW · `amount < 0` ⇒ `AmountUnknown` · เพิ่มกติกา R264-P1 ห้ามส่ง `NetPayable`/ยอดสกุลต่างประเทศ

### M4 — คำขอพร้อมกัน (แทนที่ซ้อนแทนที่ / ถอนซ้อนแทนที่ / ถอนสองครั้ง) ได้ 500 แทน 409
- `GrantAsync` (`:230-235`) และ `WithdrawAsync` (`:276-281`) อ่านแถวที่ยังใช้อยู่แบบไม่ล็อกแล้วค่อย UPDATE · ธุรกรรมที่สองรอล็อกแถว และหลังอันแรก commit `OLD.WithdrawnAt` ไม่ใช่ null แล้ว ⇒ trigger `RAISE EXCEPTION` (SqlState `P0001`) · `catch` ที่ `:260` จับแค่ `23505` ⇒ DbUpdateException หลุดไปถึง middleware ⇒ 500 (ข้อความ trigger อาจถูก echo ออกไป)
- ไม่มีข้อมูลเสีย: unique บางส่วนกันไม่ให้เกิดแถวที่ใช้อยู่ 2 แถว และการถอน+แทรกอยู่ในธุรกรรมเดียวกัน จึงไม่เหลือ 0 แถวโดยไม่ตั้งใจ · แต่กรณีถอนแข่งกับแทนที่ ผู้ใช้กดถอนแล้วได้ข้อผิดพลาด ในขณะที่แถวใหม่จากการแทนที่ยังใช้อยู่ ⇒ ผู้ใช้ต้องรู้เองว่าต้องถอนอีกครั้ง
- แก้: อ่านแถวด้วย `FOR UPDATE` (ทั้ง Grant-prev และ Withdraw) แล้วตรวจ `WithdrawnAt` ซ้ำ ⇒ 409 "มีการเปลี่ยนพร้อมกัน — รีเฟรช" · หรือจับ `P0001` ที่มาจาก trigger นี้แล้วแปลงเป็น 409 · เพิ่มเทสต์ Db ที่ถอนพร้อมกันสองครั้ง

---

## LOW

- **L1 — trigger ไม่กัน `IsDeleted` และ DELETE** (`PartnerConsentMigration.cs:396-416`): แถวที่ยังใช้อยู่ตั้ง `IsDeleted=true` ได้ ซึ่งทำให้ `GrantAsync` หา `prev` ไม่เจอ (query filter กรองออก) แต่ unique index ไม่สนใจ `IsDeleted` ⇒ ให้ความยินยอมขอบเขตนั้นใหม่ไม่ได้อีกเลย (409 ทุกครั้ง) และหน้าไม่แสดงแถวนั้น · ส่วน `BEFORE UPDATE` อย่างเดียวทำให้ลบประวัติแบบ hard delete ได้ (หลักฐานความยินยอม PDPA ม.19 ภาระพิสูจน์อยู่กับผู้ควบคุม) · วันนี้ไม่มีโค้ดส่วนไหนแตะตารางนี้ด้วยวิธีนี้ (ตรวจ PdpaService/CompanyService.DeleteCompanyAsync/AdminController.DeleteCompany/BulkCleanup แล้ว ทุกเส้นเป็น soft delete ของบริษัท หรือแตะเฉพาะตารางของตัวเอง) · แก้: ใส่ `"IsDeleted"` ในรายการช่องที่ห้ามเปลี่ยน + `BEFORE DELETE … RAISE` (ยกเว้น cascade ของบริษัทถ้าจำเป็น)
- **L2 — hash ไม่ผูกผู้ให้/เวลา และไม่มีกุญแจ** (`PartnerConsent.cs:137-146`): ไม่รวม `GrantedByUserId`, `GrantedAt`, `PartnerCompanyId` ⇒ สถานะ "หลักฐานตรง" (`HashOk`) ไม่ได้รับรองว่า "ใคร/เมื่อไร" (มีแต่ trigger ที่กันไว้) · SHA-256 ที่ไม่มีกุญแจกันการ INSERT ปลอมระดับฐานไม่ได้ · ขั้นยืนยันด้วย hash ฝั่ง client คำนวณเองได้ (ข้อความดึงได้จาก preview) จึงเป็นแค่การกันความคลาดเคลื่อนของ UX ไม่ใช่หลักฐานว่าผู้ใช้อ่านแล้ว · แก้: ถ้าจะเปลี่ยนรูป canonical ให้ขึ้นเป็น `PAC2` ที่รวม grantor+GrantedAt (ห้ามแก้ PAC1 เพราะแถวเดิมจะตรวจไม่ผ่าน) และระบุใน doc ว่าเป็น integrity check ไม่ใช่ tamper-proof
- **L3 — เวอร์ชันข้อความเก่ายังใช้ได้ตลอดไป**: `HashMatches` ยอมรับทุกเวอร์ชันที่ยังอยู่ใน `Texts` · ไม่มี `MinAcceptedTextVersion` ต่อขอบเขต ⇒ ถ้า v2 ขยายสิ่งที่ท่อทำ แถว v1 จะครอบพฤติกรรมใหม่โดยไม่ต้องยินยอมใหม่ · แก้: เพิ่ม `MinTextVersion` ใน `ScopeDef` + โค้ด `TextOutdated` ใน `Allows`
- **L4 — ขอบเขตที่ "ให้ล่วงหน้า" จะเริ่มมีผลเองเมื่อ P3 พลิก `ReaderWired`**: หน้าบอกผู้ให้ว่า "ยังไม่มีผล" (`EffectNote`) แต่ไม่มีขั้นแจ้งหรือยืนยันอีกครั้งตอนที่มีผลจริง · แก้: ตอนพลิกธงใน P3 ให้แจ้งกระดิ่งผู้ให้/บันทึก audit "เริ่มมีผล" หรือให้แถวที่ให้ก่อนวันต่อสายต้องยืนยันใหม่ (เทียบ `GrantedAt` กับค่าคงที่ `WiredSince` ใน ScopeDef)
- **L5 — เพดานของขอบเขตที่ไม่บังคับเพดานไม่ถูกบังคับเมื่อไม่รู้ยอด** (`PartnerConsent.cs:247-249`): `ReceiveSlipCopy` ตั้งเพดานได้ (`RequestProblem` ยอม) แต่ถ้าท่อส่ง `amount=null` ก็ผ่าน ⇒ เพดานที่ผู้ใช้ตั้งไว้ไม่มีผลเงียบ ๆ · แก้: `row.MaxAmount != null && amount == null ⇒ AmountUnknown` หรือห้ามตั้งเพดานบนขอบเขตที่ `!RequiresCap`
- **L6 — `notifyEmail` อยู่ใน audit chain แบบ append-only** (`PartnerConsent.cs:299` ผ่าน `CrossTenantWorkflowController.cs:266-305`): อีเมลบุคคลเข้าแถวที่ลบไม่ได้ ⇒ DSR erase ทำไม่ได้ (แถว auto-audit ของ ChangeTracker ก็มีค่านี้อยู่ก่อนแล้ว) · แก้: ใน snapshot บันทึกแค่ว่า "เปลี่ยน" หรือเก็บ hash/mask (`a***@x.com`) แทนค่าจริง
- **L7 — คู่ค้าถูกลบ (soft delete บริษัท) แต่การเชื่อมยัง Accepted**: `CheckAsync` ไม่ตรวจ `Companies.IsDeleted`/สถานะระงับของบริษัทคู่ค้า ⇒ ท่อ P3 อาจทำงานต่อจากเอกสารของบริษัทที่ถูกลบหรือระงับไปแล้ว · แก้: ใน `CheckAsync` โหลดสถานะบริษัทคู่ค้า ถ้าถูกลบหรือระงับ ⇒ `PartnerNotActive`
- **L8 — ปลีกย่อย**: (a) `ListAsync` `Take(2000)` ข้ามทุกคู่ (`:106`) ⇒ ถ้าประวัติเกินนั้น แถวที่ใช้อยู่ของคู่เก่าหลุดจากหน้า แสดงเป็น "ยังไม่ได้ให้" (ควรดึงแถวที่ใช้อยู่แยก แล้วจำกัดเฉพาะประวัติ) · (b) หน้ายืนยันขั้นที่ 1 (`partner-automation.html:148`) แสดงเพดานแต่ไม่แสดงบัญชีจ่ายตั้งต้น ทั้งที่ค่านี้ถูก hash ⇒ ผู้ใช้ไม่เห็นค่าที่กำลังยินยอม (ให้ preview คืนป้ายบัญชีจากเซิร์ฟเวอร์) · (c) `RequestFromPartnerAsync` ส่งแจ้งเตือนก่อนเขียน audit (`:330-353`) ⇒ ถ้า SaveChanges ล้ม คู่ค้าได้กระดิ่งแต่ไม่มีแถว audit · (d) ตัวจำกัด 3 ครั้ง/วันนับก่อนตรวจคำขอ (`Controller:95`) ⇒ คำขอที่ผิด (400/404) กินโควตาด้วย · (e) Db test fix 6e7b36ff กรองด้วย `NewValues.Contains("PartnerConsent")` ซึ่งเปราะ (ใช้ `"\"event\":\"PartnerConsent"` หรือ parse JSON แทน)

---

## ตรวจแล้วไม่ใช่บั๊ก (ห้ามรายงานซ้ำ)
1. **แข่งกันให้ครั้งแรกจนเกิด 2 แถวที่ใช้อยู่** — unique บางส่วน `WHERE WithdrawnAt IS NULL` ⇒ 23505 ⇒ 409 · การแทนที่ (ถอน+แทรก) อยู่ในธุรกรรมเดียวกัน ⇒ ไม่เกิด 0 แถวที่ใช้อยู่
2. **Tenant isolation** — ทุก query ผูก `CompanyId` หรือ partnership ที่บริษัทเป็นสมาชิก (`CheckAsync`, `ListAsync`, `ValidateAsync`, `WithdrawAsync`, `PayAccountOptionsAsync`) · ถอนแถวของบริษัทอื่น = 404
3. **ชื่อ/เลขภาษีคู่ค้าของคู่ที่ Pending/Rejected หลุด** — `ListAsync` คืนเฉพาะ Accepted/Suspended (เคยเชื่อมกันแล้ว) · `RequestFromPartnerAsync` ต้องเป็น Accepted
4. **ผู้ให้ถูกปิดผู้ใช้/ถอดจากบริษัท/ลดสิทธิ์** — `GrantProblemForAsync` ตรวจ `UserStatus.Active` + `HasPermissionAsync` ที่ผูก `CompanyUser` ของบริษัทนั้น ⇒ `GrantorLostPermission`
5. **AutoRecordPayment ต้องการแค่ PurchaseApprove** — ตรงกับด่านของ `CreatePayment` (`DocumentController.cs:1779` `DocPerm.Approve`)
6. **ReturnWhtCerts→Tax.File / PurchaseDrafts** — เป็น `CompanyWideToggle` ให้ต่อคู่ไม่ได้ (409) ⇒ การจับคู่สิทธิ์ยังไม่มีผล · `CompanyWideOff` อ่านด้วย `CompanyWideToggleOn` ตัวเดียว
7. **การปัดเศษใน hash** — `ValidateAsync` ปัด 2 ตำแหน่งแบบ AwayFromZero ก่อนทั้ง hash และ store · numeric(18,2) ปัดทางเดียวกัน · `Canonical` ปัดซ้ำได้โดยไม่เปลี่ยนค่า
8. **XSS** — `partner-automation.html` ส่งทุกค่าผ่าน `Layout.esc` · กระดิ่ง (`layout.js:2554`) escape `n.title` ที่มีชื่อบริษัทผู้ขอ · ลิงก์ใน partners.html ใช้ `encodeURIComponent`
9. **Audit chain** — ทุกการเขียนใช้ `AddChainedAuditLog` · `row.Id` มีค่าตั้งแต่สร้าง (`BaseEntity.Id = Guid.NewGuid()`) จึงอ้างใน audit ก่อน save ได้
10. **PDPA erase / soft-delete ทั่วไปชน trigger** — ไม่มีเส้นใดแก้ตารางนี้ (`UpdateTimestamps` แตะเฉพาะ entry ที่ Modified · การลบบริษัทเป็น soft delete ที่ไม่ cascade)
