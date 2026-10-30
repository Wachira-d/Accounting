# แจ้งเตือนลูกค้าปลายทางทาง LINE (รอบ 211 · คำตัดสินข้อ 170)

> คำขอเจ้าของ: การจองและทุกอย่างที่ส่งแจ้งเตือน/ยืนยัน ลูกค้าของร้านเลือกรับทาง LINE ได้ · ค่าเริ่มต้น = LINE OA ของ NextAcc ·
> ร้านตั้ง LINE OA ของตัวเอง หรือ SMTP ของตัวเองได้ · ระบบสมาชิกผูก LINE + ปุ่มเพิ่มเพื่อน · แขกไม่สมัครได้ปุ่มเดียวกันต่อการจอง/ออเดอร์ ·
> "วิเคราะห์และปรับให้เข้ากับระบบเดิม"
>
> สถานะ: **ทำแล้วในรอบนี้ (Phase B)** · ยังไม่ได้คอมไพล์ในเครื่องนี้ (ไม่มี .NET SDK) · ยังไม่ผ่านฝ่ายค้าน

## 1. สภาพเดิม (ตรวจจากโค้ดที่ 9a786f2e)

| เรื่อง | สถานะ | หลักฐาน |
| --- | --- | --- |
| ร้านตั้ง LINE Messaging API ของตัวเอง (token/secret เข้ารหัส · LineEnabled · Basic ID) | ✅ | `Models/Entities/CompanySettings.cs:230-245` · `Controllers/LineConfigController.cs` (PUT มีด่าน `CompanySettingsEdit`) · `pages/settings.html` แท็บ LINE |
| push ใช้ OA ของร้านเมื่อเปิด+มี token ไม่งั้น OA ของ NextAcc (config) | ✅ | `LineNotifyService.ResolveConfigAsync` |
| webhook รับเฉพาะ OA ของ NextAcc (secret จาก config) | ⚠️ | `LineWebhookController` + `LineBotService.VerifySignature` — OA ของร้านไม่มีทางส่ง webhook มาหาเรา |
| ผูก LINE ของผู้ใช้ ERP ("ผูก 123456") / พนักงานรับสลิป ("สลิป 123456") | ✅ | `LineBotService.HandleMessageAsync` · `PayslipLineDeliveryService` |
| สูตรตรวจลายเซ็น LINE มีที่เดียว | ✅ (แต่ฝังใน service) | `LineBotService.VerifySignature` — ต้องยกออกเป็น helper ก่อนมีทางเข้าที่สอง |
| ส่งข้อความถึงลูกค้า CMS: อีเมลก่อน → LINE | ⚠️ | `Services/Implementations/Cms/BookingNotifier.cs` — LINE มาจาก `SiteCustomer.AuthProvider=="line"` ซึ่ง**ไม่มีโค้ดไหนตั้งค่า** (storefront ไม่มี LINE Login) ⇒ LINE ไม่เคยถูกใช้ |
| อีเมลถึงลูกค้า CMS ใช้ SMTP ของร้าน | ❌ | `BookingNotifier` เรียก `IEmailService.TrySendAsync` (ของระบบเท่านั้น) · ร้านตั้ง SMTP/Graph/Gmail ได้แล้ว (`EmailConfigController` + `IEmailSenderFactory`) แต่ใช้กับเอกสารบัญชีเท่านั้น |
| สมาชิกเว็บ/แขก ผูก LINE · ปุ่มเพิ่มเพื่อน | ❌ | ไม่มีตาราง/endpoint/UI |
| พนักงานรับสลิปผ่าน OA ของร้าน | ⚠️ (พบระหว่างตรวจ) | ลิงก์เพิ่มเพื่อนใช้ Basic ID ของร้าน แต่ "สลิป 123456" ที่ส่งเข้า OA ร้านไม่มี webhook รับ — ไม่แก้รอบนี้ (ดู §7) |

## 2. แบบที่เลือก

**ผูกด้วย "เพิ่มเพื่อน + ส่งรหัส" ผ่าน Messaging API (ไม่ใช้ LINE Login)**

1. หน้าเว็บ (สมาชิก: `account` · แขก: หน้าจองสำเร็จ / `my-booking/{token}` / ออเดอร์สำเร็จ / `track-order/{token}`) ขอสถานะ → เซิร์ฟเวอร์ตัดสินทั้งหมด
2. ลูกค้ากด "รับแจ้งเตือนทาง LINE" → `POST …/start` ได้รหัสใช้ครั้งเดียว `LN-XXXXXXXX` (32 ตัวอักษร × 8 · 30 นาที · เก็บแค่ SHA-256)
   + ลิงก์ `https://line.me/R/ti/p/@id` (เพิ่มเพื่อน) + `https://line.me/R/oaMessage/@id/?ผูก LN-…` (เปิดแชตพร้อมข้อความ) + QR (สำหรับเปิดบนคอม)
3. ลูกค้าส่งข้อความ → webhook ของ OA นั้น (ลายเซ็นผ่าน) → ไถ่รหัส (นับโควตาต่อ LINE user fail-closed · update แบบมีเงื่อนไข `UsedAt IS NULL`) →
   บันทึก `CustomerLineLink` + `PdpaConsentRecord` + audit chain → ตอบ "เชื่อมต่อสำเร็จ"
4. ส่งแจ้งเตือน: `BookingNotifier` หา LINE ที่ผูกเอง (ระเบียน > สมาชิก > สมาชิกที่ผูกผู้ติดต่อ ERP) ที่ยัง**ใช้ได้กับ OA ปัจจุบัน** → ผูกแล้ว ⇒ LINE ก่อนแล้วอีเมล · ไม่ผูก ⇒ อีเมลก่อน (เดิม)

**ตัวตัดสินเดียว** (`Helpers/`): `CustomerLineLinkRule` (รหัส · รูปแบบข้อความ · hash · URL · ข้อความยินยอม/ตอบกลับ) ·
`CustomerNotifyChannel` (OA ไหน · ลิงก์ใช้ได้ไหม · ลำดับช่อง · อีเมลของร้าน/ระบบ) · `LineWebhookSignature` (HMAC ตัวเดียว — `LineBotService.VerifySignature` เรียกตัวนี้)

**ช่องทาง LINE ต่อบริษัท** = กติกาเดียวกับ push เดิม: `LineEnabled && มี token` ⇒ OA ของร้าน · ไม่งั้น OA ของ NextAcc (`Line:*`) ·
ผูกได้เมื่อ OA นั้นมี token + secret + Basic ID (`Line:BasicId` สำหรับ OA กลาง) — ไม่ครบ ⇒ ปุ่มไม่ขึ้นหน้าร้าน + หน้าตั้งค่าบอกเหตุ

**userId ของ LINE แยกตาม provider** ⇒ ลิงก์เก็บ `OaScope` (nextacc/company) + `OaDestination` (bot userId จากฟิลด์ `destination` ของ webhook) ·
ร้านสลับ OA ⇒ ลิงก์เก่า "ต้องผูกใหม่" (`LinkUsable=false` · หน้าร้านขึ้นข้อความ · หน้าตั้งค่านับ `StaleLinks`) — ไม่พยายาม push ข้าม OA

**webhook ต่อบริษัท** `POST /api/line/webhook/c/{companyId}` (`CustomerLineWebhookController` · AllowAnonymous): ไม่มี secret ⇒ 404 · ลายเซ็นผิด ⇒ 401 ·
จำ `destination` · `message` "ผูก LN-…" ⇒ ไถ่ใน scope company ของบริษัทนั้นเท่านั้น · `follow` ⇒ เปิดลิงก์ที่บล็อกไว้ + ตอบวิธีผูก (reply API ไม่กินโควตา) ·
`unfollow` ⇒ `BlockedAt` · ข้อความอื่นไม่ตอบ (แชตของร้าน) · ลายเซ็นผ่านแล้วตอบ 200 เสมอ

**OA ของ NextAcc**: `LineBotService.HandleMessageAsync` แยก "ผูก LN-…" **ก่อน** เส้น "ผูก 123456" ของ ERP (`IsLinkAttempt` ต้องมี `LN`) ·
`LineWebhookController` รับ `follow/unfollow` ⇒ เปิด/ปิดลิงก์ลูกค้า (เงียบ — ผู้ใช้ ERP ก็ follow OA นี้)

**อีเมล**: ร้านตั้ง + ทดสอบผ่าน (`CompanySettings.EmailConfigured` — ล้างทุกครั้งที่แก้ค่า) ⇒ `IEmailSenderFactory.GetSenderForSettings` + ผู้ส่งจาก
`DocumentEmailService.ResolveFromAddress` (ตัวเดียวกับเอกสาร) · ไม่งั้น `IEmailService` ของระบบ (เดิม) · ร้านตั้งแล้วส่งล้ม ⇒ ไม่สลับไปอีเมลระบบ ไปช่องถัดไป + ประทับเหตุ

## 3. ทางเลือกที่ไม่เลือก

| ทางเลือก | เหตุผลที่ไม่เลือก |
| --- | --- |
| LINE Login (OAuth) บนหน้าร้าน | userId ของ LINE Login ใช้ push ได้เฉพาะเมื่อ channel Login อยู่ provider เดียวกับ OA — ร้านที่ใช้ OA ของตัวเองต้องตั้ง Login channel เพิ่มอีกตัว · ต้องมี callback/redirect ต่อโดเมนร้าน · ไม่ได้ "เพิ่มเพื่อน" อัตโนมัติ (ยังต้องกดอีก) |
| LIFF | ต้องมี LIFF app ต่อ OA + ผูก Login channel — ภาระตั้งค่าของร้านสูงสุด |
| ให้ลูกค้ากรอก LINE ID ในฟอร์ม | LINE ID ≠ userId ที่ push ได้ · ส่งหาไม่ได้จริง · เท่ากับ "สถานะที่ระบบประทับเอง" (F2 #3) |
| webhook เดียวแล้วเดาบริษัทจาก `destination` | ต้องรู้ secret ก่อนตรวจลายเซ็น แต่ secret ขึ้นกับบริษัท ⇒ ต้องลองทุก secret (ช้า + เสี่ยง) · URL ต่อบริษัทตรวจได้ทันที |
| รหัสตัวเลข 6 หลักแบบ ERP | ชนกับ "ผูก 123456" (ERP ดึงเฉพาะตัวเลข) + พื้นที่เดาเล็ก — ใช้ `LN-` + 8 ตัว 32 แบบ แยกด้วยข้อความล้วน |
| ส่ง LINE ซ้ำทุกช่อง (LINE + อีเมลพร้อมกัน) | ไม่เปลี่ยนสัญญาเดิม "หยุดที่ช่องแรกที่สำเร็จ" ของ `IBookingNotifier` (งานเตือน/ได้คิวประทับหลังส่งถึง 1 ครั้ง) |

## 4. โครงข้อมูล

- `CustomerLineLinks` (TenantEntity): `SiteId` · `TargetKind` (member/booking/order) · `TargetId` · `LineUserId` · `OaScope` · `OaDestination` · `LinkedAt` ·
  `UnlinkedAt`/`UnlinkReason` (user/relinked/erase) · `BlockedAt` (unfollow) · `ConsentRecordId` — index (Company, Kind, Target) + (LineUserId)
- `CustomerLineLinkCodes`: `SiteId` · `TargetKind` · `TargetId` · `CodeHash` (unique) · `OaScope` · `ExpiresAt` · `UsedAt` · `LinkId`
- `CompanySettings.LineOaDestination` (ระบบประทับจาก webhook ที่ลายเซ็นผ่าน · ล้างเมื่อเปลี่ยน token)
- migration `DatabaseMigrationHelper.CmsRound211LineNotifyStatements` (CREATE TABLE/INDEX IF NOT EXISTS + ADD COLUMN) · ไม่มี UPDATE แถวเดิม
- config ใหม่ (ไม่บังคับ): `Line:ChannelSecret` (มีอยู่แล้วใน env) · `Line:BasicId` · `Line:BotUserId`

## 5. ทางเข้า (endpoint)

| ทางเข้า | สิทธิ์ | หมายเหตุ |
| --- | --- | --- |
| `GET/DELETE portal/me/line` · `POST portal/me/line/start` | โทเคนหน้าร้าน (`Me()`) | สมาชิก |
| `GET/DELETE storefront/line/{booking\|order}/{token}` · `POST …/start` | โทเคนสาธารณะของรายการ | แขก (เส้นเดียวกับหน้า my-booking/track-order) |
| `POST /api/line/webhook/c/{companyId}` | ลายเซ็น LINE ด้วย secret ของบริษัท | OA ของร้าน |
| `POST /api/line-webhook` (เดิม) | ลายเซ็น OA กลาง | + "ผูก LN-…" · follow/unfollow |
| `GET/PUT line-config` (เดิม) | `CompanySettingsEdit` (PUT) | + `customerChannel` (OA ที่ใช้ · ผูกได้ไหม · webhook URL · จำนวนลิงก์) · Basic ID ผิดรูปแบบ ⇒ 400 |

โควตา (`IChatRateLimiter` · DB · fail-closed): ออกรหัสต่อ IP 10/นาที·60/วัน + ต่อเป้าหมาย 5/นาที·20/วัน · ไถ่รหัสต่อ LINE user 3/นาที·10/วัน · ดูสถานะแขกต่อ IP 30/นาที

## 6. ความปลอดภัย · PDPA

- ไม่มี static dict — รหัส/ลิงก์/โควตาอยู่ใน DB (หลาย instance) · ใช้ครั้งเดียวด้วย `ExecuteUpdate … WHERE UsedAt IS NULL` (นับแถว = 1)
- รหัสเก็บเป็น SHA-256 · LINE userId ไม่ลง log เต็ม (`CustomerLineLinkRule.Mask`) · token/secret ของ OA เข้ารหัสด้วย `ISecretProtector` (เดิม) · ไม่มี fallback key
- ความยินยอม ม.19: ข้อความยินยอมแสดงข้างปุ่ม (เซิร์ฟเวอร์ประกอบ) · ไถ่รหัสสำเร็จ = การกระทำยืนยัน ⇒ `PdpaConsentRecord` purpose `cms-line-notify:messaging`
  (เวอร์ชัน = hash ข้อความ · ผ่าน `CmsDataConsent.Stage` ตัวเดิม) · ยกเลิกการเชื่อมต่อ ⇒ ถอนความยินยอมแถวนั้น
- DSR (`PdpaService.Cms`): ขอดูข้อมูลแสดง `lineLinks` ใต้สมาชิก/ออเดอร์/การจอง · ขอลบ ⇒ ปิดลิงก์ + ล้าง `LineUserId` (`CmsPdpaErasure.EraseLineLink` · ไม่มี legal hold — ไม่ใช่เอกสารบัญชี)
- OA กลางเป็น OA เดียวทุกบริษัท ⇒ การไถ่รหัสบน OA กลางค้นด้วย hash (unique) โดยไม่มี `CompanyId` ในเงื่อนไข (เหมือน `LineBindCodes` เดิม) — ทุก query อื่นกรองบริษัท

## 7. คำตัดสินที่ขอเจ้าของ

1. **ลำดับช่อง**: ผูก LINE แล้ว ⇒ LINE ก่อนแล้วอีเมล (หยุดที่ช่องแรกที่สำเร็จ) — หรืออยากให้ส่ง**ทั้งสอง**ช่อง? (ทำแล้วแบบแรก)
2. **อีเมลของร้านล้ม**: ไม่สลับไปอีเมลของระบบ (แบรนด์ผู้ส่ง) — ยืนยัน?
3. **ค่าข้อความ LINE**: push ผ่าน OA กลางกินโควตาข้อความของ NextAcc (แผน LINE OA คิดตามจำนวน) — จะคิดค่าบริการ/จำกัดต่อร้านไหม? ตอนนี้ไม่มีเพดาน
4. **พนักงานรับสลิปผ่าน OA ของร้าน** (พบระหว่างตรวจ): "สลิป 123456" ที่ส่งเข้า OA ร้านยังไม่มีผู้รับ — ต่อสายเข้า webhook ต่อบริษัทในรอบถัดไปไหม?
5. **ร้านสลับ OA**: ลูกค้าเดิมต้องผูกใหม่ (ข้อจำกัดของ LINE) — ต้องการให้ระบบอีเมลเชิญผูกใหม่อัตโนมัติไหม?

## 8. ความเสี่ยงที่ยังเปิด

- ยังไม่ได้คอมไพล์ (CI คือคอมไพเลอร์ตัวแรก) · ไม่มีเทสต์ DB ของ service ไถ่รหัส (เทสต์ pure + ตัวส่งด้วย proxy · ด่านจุดเรียกใน `required_call_site_check`)
- `follow` บน OA ร้านตอบวิธีผูกทุกครั้ง — ซ้อนกับข้อความทักทายที่ร้านตั้งใน LINE OA Manager ได้
- push ไม่ตรวจว่าลูกค้า "เป็นเพื่อน" ก่อน — ยังไม่เพิ่มเพื่อนแต่ส่งรหัส ⇒ ผูกสำเร็จแต่ push ล้ม ⇒ ประทับเหตุ + ไปอีเมล (ไม่เงียบ)
- `Line:BotUserId` ไม่ตั้ง ⇒ ลิงก์ของ OA กลางตัดสินด้วย scope อย่างเดียว (พอ เพราะ OA กลางไม่ถูกสลับ)
