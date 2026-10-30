# ค่าตั้งการแจ้งเตือนลูกค้า · โควตาข้อความช่องกลาง · เชิญผูก LINE ใหม่ · สรุปประจำวันถึงพนักงาน (รอบ 212 · แบบร่าง)

> คำตัดสินเจ้าของ 5 ข้อ (รอบ 212) ต่อจากรอบ 211 (`LINE_CUSTOMER_NOTIFY_DESIGN.md` · DECISIONS ข้อ 170 · commit 461eeea5) ·
> **เอกสารออกแบบเท่านั้น — ยังไม่มีโค้ด** · ตรวจจากโค้ดที่ `c05bcd97` + diff ฝ่ายค้านรอบ 211 ที่ยังไม่ commit ใน worktree `agent-a5eb332c4a9c9bcce`
> (`CustomerNotifyChannel.EmailPlan` + `BookingSendResult.Warning` = "ถอยไปอีเมลระบบ + ประทับ") — แบบนี้ต่อยอดบนสภาพหลัง diff นั้นเข้า

## §0 สรุป

| ข้อ | สิ่งที่ออกแบบ | ตัวตัดสินเดียว (OWNER) |
| --- | --- | --- |
| 1 | ตาราง `CustomerNotifyRules` (บริษัท/เว็บ × ประเภทข้อความ × LINE/อีเมล · แถว `*` = "ทั้งหมด") · ไม่มีแถว = ส่งเหมือนเดิม | `Helpers/CustomerNotifyPolicy.Resolve` — เรียกที่ `BookingNotifier.SendAsync` จุดเดียว |
| 2 | `CompanySettings.CustomerEmailFallback` 3 ค่า · ค่าว่าง = ผู้ประสานงานตัดสิน (ถอยไปอีเมลระบบ + ประทับ) | `CustomerNotifyChannel.EmailPlan(configured, fallback)` (ขยายของรอบ 211) |
| 3 | โควตา "ข้อความผ่านช่องกลาง" (push ผ่าน OA ของ NextAcc + SMTP ของระบบ) ต่อบริษัทต่อเดือน: เพดานจากแพ็กเกจ + แพ็กซื้อเพิ่ม `notify.topup` · **ใช้ระบบบิลเดิม** (`UsageEvent` → `UsageInvoicingJob`) | `Helpers/NotifyQuotaPolicy` + `INotifyQuotaService.TryReserveAsync` (UPDATE…RETURNING แบบ R-05) |
| 4 | ร้านสลับ OA ⇒ ระบบเชิญลูกค้าที่ผูกไว้ให้ผูกใหม่ (OA เดิมถ้ายัง push ได้ · ไม่งั้นอีเมล) · ทำในงานเบื้องหลัง · กันซ้ำด้วยคีย์ข้อความ | `CustomerRelinkInvitePlan` (pure) + `CustomerNotifyOutboxJob` |
| 5 | สรุปประจำวันทาง LINE ถึงพนักงาน (ผูกผ่าน "ผูก 123456" บน OA กลาง) · ตั้งเวลา/ผู้รับ/หัวข้อต่อบริษัท-เว็บ · กรองตามสิทธิ์ผู้รับรายคน | `Helpers/StaffDailySummaryPlan` + `StaffDailySummaryJob` (JobLock + watermark + log ต่อวันต่อผู้รับ) |

**ข้อค้นพบระหว่างตรวจ (บั๊กแฝง ไม่ใช่ของใหม่):** `NotificationEngine.DispatchLineAsync` (`Services/Implementations/NotificationEngine.cs:352-357`) push หา
`User.LineUserId` ด้วย `PushToUserAsync(companyId, …)` ⇒ `LineNotifyService.ResolveConfigAsync` (`LineNotifyService.cs:36-49`) เลือก **OA ของร้าน**เมื่อร้านเปิด LINE ·
แต่ userId ของพนักงานได้มาจาก webhook ของ **OA กลาง** เท่านั้น (`LineBotService.cs:150-201` "ผูก 123456") — userId ของ LINE แยกตาม provider ⇒ ร้านที่ตั้ง OA เอง
แจ้งเตือนพนักงานทาง LINE ล้มทุกฉบับ (log Error อย่างเดียว) · ข้อ 5 ต้อง push ผ่าน OA กลางแบบระบุชัด และควรแก้ engine ในแพ็กเดียวกัน (§3 · §8 แพ็ก C)

## §1 สภาพเดิม (หลักฐาน)

### 1.1 จุดส่งข้อความถึงลูกค้าปลายทาง (ทุกจุดที่พบ)

| # | เหตุการณ์ (คีย์ที่เสนอ) | จุดเรียก | ช่อง | สวิตช์เดิม |
| --- | --- | --- | --- | --- |
| 1 | จองสำเร็จ `booking.confirmed` | `CmsBookingService.ShopNotify.cs:45` `NotifyBookingCreatedAsync` → `_shopNotifier.NotifyCustomerAsync` (บรรทัด ~59) | `IBookingNotifier` | — |
| 2 | ร้านยกเลิก `booking.cancelled` | `ShopNotify.cs:77` `NotifyBookingCancelledByShopAsync` | 〃 | — |
| 3 | เลื่อนนัด `booking.rescheduled` | `ShopNotify.cs:102` `NotifyBookingRescheduledAsync` | 〃 | — |
| 4 | เตือนก่อนนัด `booking.reminder` | `Services/Background/BookingReminderJob.cs:127` `notifier.SendAsync` | 〃 | `SiteBookingSettings.ReminderEnabled` (`CmsBookingResource.cs:26`) + ชั่วโมง |
| 5 | ได้คิวจากคิวสำรอง `booking.waitlist-promoted` | `CmsBookingService.Notify.cs:77` (ส่งซ้ำจาก `BookingReminderJob.cs:162`) | 〃 | — |
| 6 | คลาสยกเลิก/เปลี่ยนครู `class.cancelled` · `class.substitute` | `CmsBookingService.Classes.cs:647` `NotifyClassBookingsAsync` → `:689` (ผู้เรียก `:518` · `:591`) | 〃 | ติ๊ก "แจ้งลูกค้า" ต่อคำสั่ง |
| 7 | สั่งซื้อสำเร็จ `order.confirmation` | `CmsCommerceService.Notify.cs:54` → `:~76` | `ICmsShopNotifier` → `IBookingNotifier` | — |
| 8 | สถานะออเดอร์ `order.shipped` · `order.ready` · `order.delivered` | `CmsCommerceService.Notify.cs:108` → `:~121` (ข้อความจาก `CmsNotificationText.CustomerOrderStatus` `:120` — คืนข้อความเฉพาะ Shipped/ReadyForPickup/Delivered) | 〃 | — |
| 9 | บัตรคอร์สใกล้หมดอายุ `credit.expiring` | `CourseCreditService.Membership.cs:358` `SendExpiryRemindersAsync` → `:389` (งาน `CourseCreditExpiryJob.cs:84`) | `IBookingNotifier` | `CreditPackage.ExpiryReminderDays` (`CourseCredit.cs:81`) |
| 10 | ที่พัก: สร้าง/สลิป/ยืนยัน/สลิปไม่ผ่าน `lodging.*` | `Lodging/LodgingService.Operations.cs:519` `TryNotifyAsync` (ผู้เรียก `Lifecycle.cs:266,267,367,671,807` · `Reservations.cs:577`) | **`IEmailService` ของระบบตรง** (`:544,554,568`) — ไม่ผ่าน notifier · ไม่มี LINE | ค่าตั้งที่พัก |
| 11 | ตะกร้าค้าง `cart.abandoned` (การตลาด) | `AbandonedCartService.cs:120` `emailService.SendAsync` | อีเมลระบบตรง | `EnableAbandonedCartRecovery` (`CmsSite.cs:156`) |
| 12 | ตอบกลับฟอร์มอัตโนมัติ `form.autoreply` | `CmsCustomerService.cs:511` `_email.TrySendAsync` | อีเมลระบบตรง | `SendAutoReply` ของฟอร์ม |

ข้อความถึง**ร้าน** (`ICmsShopNotifier.NotifyShopAsync` · `NotifyLodgingBookingAsync` ฯลฯ) และอีเมลแพลตฟอร์ม→ร้าน (บิล `SaasBillingDocumentService.cs:213,431` · `AddOnPurchaseService.cs:245` ·
แจ้งผูก LINE `LineBotService.cs:121`) **ไม่อยู่ในขอบเขตค่าตั้งข้อ 1** (ไม่ใช่ลูกค้าปลายทาง) — แต่ข้อความถึงร้านที่ผ่านช่องกลางนับโควตาข้อ 3 หรือไม่ ดู §2.3

### 1.2 ช่องทาง/ตัวตัดสินที่มีแล้ว

| เรื่อง | หลักฐาน |
| --- | --- |
| ลำดับช่อง LINE↔อีเมล หยุดที่ช่องแรกที่สำเร็จ | `Helpers/CustomerNotifyChannel.cs:62` `Order` · ใช้ที่ `Cms/BookingNotifier.cs:51` |
| อีเมลของร้านเมื่อ `EmailConfigured` · (diff ฝ่ายค้าน 211) ล้ม ⇒ ถอยไประบบ + `Warning` | `CustomerNotifyChannel.EmailPlan` · `BookingReminderPlan.cs:17` `BookingSendResult.Warning` (worktree) |
| OA ที่ใช้ push: ร้านเปิด+มี token ⇒ OA ร้าน ไม่งั้น OA กลาง | `LineNotifyService.cs:36-49` · `CustomerNotifyChannel.ResolveLine` `:35` · ลิงก์เก็บ `OaScope`/`OaDestination` (`Models/Entities/CustomerLineLink.cs:18-20`) |
| ลิงก์เก่าเมื่อสลับ OA = ใช้ไม่ได้ (`LinkUsable`) · หน้าตั้งค่านับ `StaleLinks` | `CustomerNotifyChannel.cs:56` · `ICustomerLineLinkService.GetChannelInfoAsync` |
| PUT ค่าตั้ง LINE ของร้าน | `Controllers/LineConfigController.cs:46-49` `Update` |
| ค่าตั้งแจ้งเตือนของ**พนักงาน** (event × role × ช่อง) — คนละเรื่องกับลูกค้า | `Models/Entities/NotificationSettings.cs:10-25` · `NotificationConfigController` |
| ผู้ใช้ ERP ผูก LINE กับ OA กลาง | `LineBotService.cs:67` `IssueBindCodeAsync` · `:160-201` · `User.LineUserId` (`Models/Entities/User.cs:60`) |

### 1.3 แพ็กเกจ · โควตา · add-on · บิล (ใช้ต่อ ไม่สร้างใหม่)

| ชิ้น | หลักฐาน | ใช้ในแบบนี้อย่างไร |
| --- | --- | --- |
| เพดานต่อแพ็กเกจ: `PlanTemplate.Max*` → คัดลอกลง `Subscription.Max*` → `SubscriptionService.EffectivePlan` | `Models/Entities/Subscription.cs:57,225` · `SubscriptionService.cs:787,807` | เพิ่ม `MaxSharedMessagesPerMonth` ทางเดียวกัน |
| ตัวนับรายเดือนแบบ atomic `UPDATE … +1 RETURNING` (R-05) | `SubscriptionService.cs:1196-1225` `IncrementDocumentCounterAtomicAsync` | ลอกรูปแบบ แต่เก็บในตารางตัวนับต่องวด (ไม่ต้องมีงาน reset) |
| ซื้อโควตาเพิ่ม: advisory xact lock ต่อบริษัท → `UsageEvent` คิดเงิน (กันซ้ำด้วย IdempotencyKey) → บวกโบนัส + อายุ → audit chain | `QuotaService.cs:94-165` `PurchaseTopUpAsync` · `AddOnCodes.DocumentsTopUp` · `AdvisoryLockKey.QuotaGrant` | ลอกเป็น `PurchaseNotifyTopUpAsync` ใช้คีย์ล็อก/ตัวเรียกเดียวกัน |
| มิเตอร์ระบบไม่ต้องให้ลูกค้าเปิด | `AddOnCodes.SystemMeters` `:59-62` · `UsageMeteringService.cs:53` | เพิ่ม `notify.topup` ในชุดนี้ |
| ปิดรอบบิล → ใบแจ้งหนี้หลายบรรทัด (Postpaid) / ตัดเครดิตตอนบันทึก (Prepaid) | `UsageInvoicingJob.cs:141` → `PlatformBillingDocumentIssuer.IssueUsageInvoiceAsync` `:217` · ACCOUNT_STRUCTURE §6.1a | **ใบแจ้งหนี้/ใบกำกับของแพ็กเสริมไหลเส้นเดิม — ไม่มีเส้นภาษีใหม่** |
| สถานะโควตาที่เซิร์ฟเวอร์คำนวณ + แบนเนอร์ | `MeteringController.cs:279` `GET quota` · `:286` `POST quota/topup` (`RequireBillingAsync`) · `wwwroot/pages/addons.html:178` | เพิ่ม `GET/POST metering/notify-quota[/topup]` คู่กัน |
| ราคาแพ็ก/แคตตาล็อก (`ApiFeature`/`ApiPricingPlan`) | `/pages/admin-addons.html` · ACCOUNT_STRUCTURE §6.1b | ตั้งราคา `notify.topup` ที่หน้าเดิม |
| `lodging.email.overage` · `lodging.notify.sms` มีรหัสแต่**ไม่มีผู้เขียน** | `AddOnCodes.cs:39-41` (grep ทั้งเรพ = 0 ผู้เรียก) | ไม่ใช้ซ้อน — มิเตอร์ใหม่ครอบอีเมลที่พักแทน (§9 ข้อ 2) |

## §2 โครงข้อมูล (migration: `DatabaseMigrationHelper.CmsRound212NotifyStatements()` — CREATE TABLE/INDEX IF NOT EXISTS + ADD COLUMN IF NOT EXISTS · ไม่ UPDATE แถวเดิม)

### 2.1 ข้อ 1 — ค่าตั้งต่อประเภทข้อความ

`CustomerNotifyRule` (TenantEntity): `SiteId Guid?` (null = ระดับบริษัท) · `EventKey string` (`*` = ทั้งหมด หรือคีย์ใน §1.1) · `LineEnabled bool?` · `EmailEnabled bool?` (null = สืบทอด) ·
`UpdatedBy` · unique `(CompanyId, SiteId, EventKey)` (partial index สำหรับ SiteId null)

ลำดับ resolve (ช่องละอิสระ): เว็บ+คีย์ → เว็บ+`*` → บริษัท+คีย์ → บริษัท+`*` → **ค่าเริ่มต้น = เปิดทั้งสอง** (= พฤติกรรมเดิม) ·
แคตตาล็อกคีย์ + ป้ายไทย + กลุ่ม (การจอง/คลาส/ออเดอร์/บัตร/ที่พัก/ระบบ) อยู่ใน `Helpers/CustomerNotifyEvents.cs` (ตัวตั้งเดียว · ส่งให้หน้าเว็บเป็นชื่อ F2 #5) ·
สวิตช์ "สร้างข้อความไหม" เดิม (`ReminderEnabled` · `ExpiryReminderDays` · `EnableAbandonedCartRecovery` · `SendAutoReply`) **คงเป็นเจ้าของเรื่องเปิด/ปิด+เวลา** —
ตาราง matrix แสดงแถวเหล่านี้เป็น "ช่องทาง" อย่างเดียว + ลิงก์ไปค่าตั้งเดิม (ห้ามมีสวิตช์สองตัวคุมเรื่องเดียว F2 #4)

### 2.2 ข้อ 2 — ถอยไปอีเมลระบบ

`CompanySettings.CustomerEmailFallback string?` ∈ `system-on-fail-or-unset` (null = ค่านี้ = คำตัดสินผู้ประสานงาน) · `system-when-unset-only` (ร้านตั้งแล้วล้ม ⇒ ไม่ถอย) · `tenant-only` (ไม่ตั้ง ⇒ ไม่ส่งอีเมล) ·
echo ใน `GET/PUT email-config` (`EmailConfigController`) ตามกฎ "เก็บแล้วต้อง echo"

### 2.3 ข้อ 3 — โควตาข้อความช่องกลาง

| ที่ | ฟิลด์ใหม่ |
| --- | --- |
| `PlanTemplate` · `Subscription` · `AccountSubscription` · `TrialConfig` | `MaxSharedMessagesPerMonth int` (ค่าตั้งต้นที่เสนอ: Trial 100 · ต่ำสุด 300 — **ตัวเลขรอเจ้าของ** · `-1` = ไม่จำกัด) · คัดลอกตามเส้นเดิมของ `MaxDocumentsPerMonth` |
| `Subscription` | `NotifyBonusQuota int` · `NotifyBonusExpiresAt DateTime?` (เหมือน `DocumentBonusQuota`) |
| `SubscriptionService.EffectivePlan` | `MaxSharedMessagesPerMonth` (ต่อบริษัท — **ไม่ pool** ข้ามกลุ่มบัญชี · §9 ข้อ 1) |
| ตารางใหม่ `NotifyUsageCounter` | `(CompanyId, Period "yyyy-MM" ตามเวลาไทย)` PK · `Used int` · `BonusUsed int` · `Warned80At` · `Warned100At` — ไม่มีงาน reset (งวดใหม่ = แถวใหม่ `INSERT … ON CONFLICT DO NOTHING`) |
| ตารางใหม่ `CustomerNotifyLog` (TenantEntity) | `MessageKey string` unique `(CompanyId, MessageKey)` · `EventKey` · `TargetKind/TargetId` · `Channel` (line/email) · `Source` (nextacc-oa/company-oa/system-smtp/tenant-smtp) · `Metered bool` · `Status` (Claimed/Sent/Failed/Suppressed) · `ClaimedUntil` · `Attempts` · `SentAt` · `Note` — อายุ 13 เดือนแล้วลบ (§6) |
| `AddOnCodes` | `NotifyTopUp = "notify.topup"` (ใน `SystemMeters`) · `MessagesPerNotifyTopUpPack = 500` (ต้องตรง `UnitLabel` ที่ seed — เหมือนคำเตือนของ `DocumentsPerTopUpPack`) · อายุ = `TopUpValidDays` (60) |
| seed `ApiFeature`/`ApiPricingPlan` | `notify.topup` "แพ็กข้อความแจ้งเตือน 500 ข้อความ" ราคา **รอเจ้าของ** (ปิดขายจนตั้งราคา) |

**นับอะไร** (`NotifyQuotaPolicy.IsMetered(source)`): push ผ่าน **OA กลาง** + อีเมลผ่าน **SMTP ของระบบ** ในนามร้าน (ลูกค้าปลายทาง + สรุปประจำวันถึงพนักงาน + ข้อความถึงร้านจาก
`NotifyShopAsync` ที่ออกทางอีเมลระบบ) · **ไม่นับ**: OA/SMTP ของร้านเอง · reply API ของ LINE (ฟรี) · ข้อความแพลตฟอร์ม→ร้าน (บิล · แจ้งเตือนโควตา · ความปลอดภัย) ·
1 หน่วย = 1 คำขอ push ต่อผู้รับ (LINE คิดต่อผู้รับต่อคำขอ ไม่ใช่ต่อ message object) หรือ 1 อีเมลต่อผู้รับ

### 2.4 ข้อ 4 — เชิญผูกใหม่

`LineOaSwitchEvent` (TenantEntity): `FromScope` · `FromDestination` · `ToScope` · `ToDestination` · `DetectedAt` · `Source` (config-put/webhook-destination) · `ProcessedAt` · `Invited` · `Skipped` ·
ใช้ `CustomerNotifyLog` (คีย์ `relink:{linkId}:{switchEventId}`) กันซ้ำ — ไม่มีตารางเชิญแยก

### 2.5 ข้อ 5 — สรุปประจำวัน

| ตาราง | ฟิลด์ |
| --- | --- |
| `StaffDailySummaryConfig` (TenantEntity) | `SiteId Guid?` (null = ทุกเว็บที่ผู้รับเห็น) · `Enabled` · `SendAtLocal TimeOnly` (Asia/Bangkok · ค่าเริ่ม 07:30 · ปัดเป็นช่วง 5 นาที) · `Sections string` (csv จาก `StaffDailySummaryPlan.Sections`) · `RecipientUserIds string?` · `RecipientRoles string?` (บทบาทบริษัท `CompanyUser.Role`) · `LastSentLocalDate DateOnly?` (watermark) · `LastRunNote` |
| `StaffDailySummaryDelivery` | `(ConfigId, LocalDate, UserId)` unique · `Status` · `SentAt` · `Units` · `Error` — กันส่งซ้ำต่อวันต่อคน |

## §3 ตัวตัดสิน/บริการ และจุดเรียกที่ต้องแก้

### 3.1 ตัวตัดสินใหม่ (pure · `Helpers/` · เทสต์ในคอมมิตเดียวกัน)

| ไฟล์ | หน้าที่ |
| --- | --- |
| `CustomerNotifyEvents.cs` | แคตตาล็อกคีย์ §1.1 + ป้าย + กลุ่ม + "สวิตช์สร้างข้อความอยู่ที่ไหน" |
| `CustomerNotifyPolicy.cs` | `Resolve(rules, siteId, eventKey) → (LineOn, EmailOn, Source)` · `Plan(policy, hasEmail, hasLine, optedIn, lineSource, emailPlan, quotaLeft)` → รายการขั้น `(channel, source, metered)` ตามลำดับ — เรียก `CustomerNotifyChannel.Order` เดิมข้างใน (ไม่เขียนลำดับซ้ำ) |
| `CustomerNotifyChannel.EmailPlan` (ขยาย) | รับ `fallback` ข้อ 2 · `tenant-only` + ไม่ตั้ง ⇒ ว่าง |
| `NotifyQuotaPolicy.cs` | `IsMetered(source)` · `EffectiveLimit(plan, bonusAlive)` · `WarnLevel(used, limit)` (80/100 เดียวกับ `DocumentQuotaPolicy.WarnLevel` — เรียกตัวนั้น) · `PeriodOf(utc)` (เวลาไทย) · ข้อความ "เกินโควตา" |
| `CustomerRelinkInvitePlan.cs` | ใครควรได้คำเชิญ (สมาชิก active · การจองที่ยังไม่ถึง/ไม่ยกเลิก · ออเดอร์ยังไม่จบ) · ช่องไหน (OA เดิมถ้ายัง push ได้ ⇒ ไม่งั้นอีเมล) · ข้อความ + ลิงก์หน้าผูก |
| `StaffDailySummaryPlan.cs` | หัวข้อ · สิทธิ์ที่แต่ละหัวข้อต้องมี · เวลาถึงกำหนด (`Due(nowBkk, sendAt, lastSent)` · ส่งตามหลังได้ถึง 23:00 · ไม่ backfill ข้ามวัน) · ประกอบข้อความ + ตัดเป็น ≤5 ก้อน × ≤5,000 ตัวอักษร ใน **คำขอ push เดียว** (เกิน ⇒ "…และอีก N รายการ ดูที่ {url}") |

### 3.2 บริการ

| บริการ | หน้าที่ |
| --- | --- |
| `INotifyQuotaService` (ใหม่ · `Services/Implementations/NotifyQuotaService.cs`) | `TryReserveAsync(companyId, messageKey, ct)`: (1) claim แถว `CustomerNotifyLog` (`INSERT … ON CONFLICT DO NOTHING` แล้ว `UPDATE … SET ClaimedUntil=now+2m WHERE Status<>'Sent' AND (ClaimedUntil IS NULL OR ClaimedUntil<now)` — นับแถว 0 = ส่งแล้ว/คนอื่นกำลังส่ง) (2) ถ้าช่อง metered: `UPDATE "NotifyUsageCounters" SET "Used"="Used"+1 WHERE … AND "Used" < @limit RETURNING` (ADO.NET ตรงแบบ R-05) — 0 แถว ⇒ ลองใช้โบนัส (`UPDATE Subscriptions SET NotifyBonusQuota-1 WHERE >0 AND ยังไม่หมดอายุ`) ⇒ ไม่ได้ = เต็ม · `CompleteAsync(sent/failed)`: ล้ม ⇒ คืนหน่วย (`Used-1`) · ข้าม 80%/100% ⇒ `UPDATE … SET Warned80At=now WHERE Warned80At IS NULL` (นับแถว = 1 ⇒ ส่งเตือนเจ้าของครั้งเดียวต่องวด) · `GetStatusAsync` (จอ) · `PurchaseTopUpAsync` (ลอก `QuotaService.PurchaseTopUpAsync` — ล็อก `AdvisoryLockKey.QuotaGrant` คีย์ "notify-topup" · `UsageEvent notify.topup` ก่อนให้ของ) · **ไม่เรียก `IUsageMeteringService` ต่อข้อความ** (ตาราง UsageEvent ไม่ใช่ตัวนับรายข้อความ · SaveChanges ของมันจะ flush งานค้างของผู้เรียก) · ทุกคำสั่งใช้ connection/scope แยกจากธุรกรรมของผู้เรียก |
| `BookingNotifier.SendAsync` (แก้ — **จุดตัดสินเดียว**) | เมธอดใหม่ `SendAsync(CustomerNotifyRequest)` = `(CompanyId, SiteId?, EventKey, MessageKey, Contact, Subject, Text)` · โหลดกติกา → `CustomerNotifyPolicy.Plan` → ต่อขั้น: `TryReserveAsync` → ส่ง → `CompleteAsync` · ผลใหม่ `BookingSendResult.Suppressed(reason)` (ปิดตามค่าตั้ง/โควตาเต็ม) แยกจาก `Fail` · ช่องกลางเต็มแต่มีช่องของร้าน ⇒ ไปช่องของร้าน + `Warning` · ลบ overload เดิมเมื่อผู้เรียกย้ายครบ (`tools/required_call_site_check.py` ล็อก "ต้องส่ง EventKey") |
| `ICmsShopNotifier.NotifyCustomerAsync` | เพิ่มพารามิเตอร์ `siteId, eventKey, messageKey` ส่งต่อ · `NotifyShopAsync` ทางอีเมลระบบ ⇒ นับโควตา (คีย์ `shop:{event}:{id}`) แต่**ไม่ถูกบล็อก**ที่ 100% ถ้าเป็นเรื่องเงินค้าง (`payment-problem`) — `NotifyQuotaPolicy.Essential` |
| `ILineNotifyService` | เพิ่ม `TryPushViaAsync(LineOaRef oa, lineUserId, messages[])` (ระบุ OA ชัด: กลาง/ร้าน · ส่งได้ ≤5 message objects) · `TryPushToUserAsync` คืนเหตุ (`LinePushOutcome`: ok/blocked/not-friend/no-token/http) แทน bool |
| `CustomerLineLinkService` | `ListRelinkCandidatesAsync(companyId, switchEvent)` · บันทึก `LineOaSwitchEvent` เมื่อ `destination` ของ webhook เปลี่ยน |

### 3.3 จุดเรียกที่ต้องแก้ (ทุกจุด)

| ไฟล์:เมธอด | แก้ |
| --- | --- |
| `CmsBookingService.ShopNotify.cs` `NotifyBookingCreatedAsync` / `NotifyBookingCancelledByShopAsync` / `NotifyBookingRescheduledAsync` | ส่ง `booking.confirmed/cancelled/rescheduled` + `MessageKey = booking:{id}:{event}[:{oldStart}]` |
| `BookingReminderJob.ProcessAsync` (`:113-127`) | `booking.reminder` · คีย์ `booking:{id}:reminder:{stage}` · `Suppressed` ⇒ ประทับเหตุ + ไม่ประทับ `ReminderSentAt` (ลองใหม่ตามรอบ 30 นาทีเดิม — เติมโควตาแล้วส่งต่อได้) · กรองแถวที่ policy ปิดทั้งสองช่องก่อนวน |
| `CmsBookingService.Notify.cs` `NotifyWaitlistPromotedAsync` (`:77`) | `booking.waitlist-promoted` · คีย์ `booking:{id}:waitlist` |
| `CmsBookingService.Classes.cs` `NotifyClassBookingsAsync` (`:647/689`) | `class.cancelled`/`class.substitute` · คีย์ `class:{sessionId}:{bookingId}:{kind}:{teacherId?}` |
| `CmsCommerceService.Notify.cs` `NotifyOrderCreatedAsync` / `NotifyOrderStatusAsync` | `order.confirmation` / `order.{shipped,ready,delivered}` · คีย์ `order:{id}:{event}` |
| `CourseCreditService.Membership.cs` `SendExpiryRemindersAsync` (`:389`) | `credit.expiring` · คีย์ `credit:{passId}:expiring:{expiryDate}` |
| `LodgingService.Operations.cs` `TryNotifyAsync` (`:519-575` ฝั่งแขก) | ย้ายอีเมลถึงแขกไปผ่าน `IBookingNotifier` (อีเมลอย่างเดียว · `lodging.{created,slip-required,confirmed,slip-rejected}`) — ได้ policy + อีเมลร้าน + โควตาในเส้นเดียว · ข้อความถึงที่พัก (`:583,591`) คงเดิมแต่นับโควตา |
| `AbandonedCartService.ProcessAbandonedCarts` (`:120`) · `CmsCustomerService` ตอบกลับฟอร์ม (`:511`) | ผ่าน `IBookingNotifier` (อีเมล) · `cart.abandoned`/`form.autoreply` |
| `NotificationEngine.DispatchLineAsync` (`:352`) | push ผ่าน **OA กลาง** ระบุชัด (`TryPushViaAsync(LineOaRef.NextAcc, …)`) — แก้บั๊กแฝง §0 · ไม่นับโควตา (แจ้งเตือนงานภายในรายเหตุการณ์ · §9 ข้อ 3) |
| `Controllers/LineConfigController.Update` (`:49`) | อ่าน `ResolveChannelAsync` ก่อน/หลังบันทึก · scope/destination เปลี่ยน ⇒ เพิ่ม `LineOaSwitchEvent` (ไม่ส่งในคำขอ HTTP) · ตอบกลับ `relinkInvitePending` |
| `CustomerLineWebhookController` / `LineWebhookController` | `destination` ใหม่ไม่ตรง `CompanySettings.LineOaDestination` เดิม ⇒ `LineOaSwitchEvent(Source=webhook)` |
| `MeteringController` | `GET notify-quota` · `POST notify-quota/topup` (`RequireBillingAsync` เดิม) |
| `NotificationEvents` | `notify.quota.80` · `notify.quota.100` · `staff.summary.failed` → ส่งถึง Owner ผ่าน `INotificationEngine` เดิม |

## §4 หน้าจอ

| หน้า | เปลี่ยน |
| --- | --- |
| `wwwroot/pages/settings.html` (แท็บ LINE/อีเมล) | ส่วนใหม่ "การแจ้งเตือนลูกค้า": ตาราง ประเภท × LINE/อีเมล (แถว "ทั้งหมด" บนสุด · สถานะ "ตามค่าเริ่มต้น/ตั้งเอง") จาก `GET customer-notify/rules` (เซิร์ฟเวอร์ส่งแคตตาล็อก+ค่าที่ resolve แล้ว) · select ข้อ 2 · การ์ดโควตาข้อความ (ใช้ไป/เพดาน/โบนัส/บอก "ช่องของร้านไม่นับ") · ป้าย "จะส่งคำเชิญผูกใหม่ N ราย" หลังสลับ OA |
| `wwwroot/pages/cms-store-settings.html` (ต่อเว็บ) | ตารางเดียวกันโหมด override ต่อเว็บ (ปุ่ม "ใช้ค่าของบริษัท" = ลบแถว) |
| `wwwroot/pages/usage.html` · `subscription.html` · `addons.html` | การ์ดโควตาข้อความ + ปุ่มซื้อแพ็ก (เส้นเดียวกับ `quota/topup` `addons.html:178`) · แบนเนอร์ 80/100% ผ่าน `Layout.showQuotaBanner` (เซิร์ฟเวอร์ส่งข้อความ) |
| `wwwroot/admin/plans.html` · `admin/account-subscriptions.html` | ช่อง `MaxSharedMessagesPerMonth` · `admin-addons.html`: ราคา `notify.topup` + ให้โบนัสโดยแอดมิน (audit) |
| หน้าใหม่ `wwwroot/pages/staff-daily-summary.html` (เมนูตั้งค่า → แจ้งเตือน) | เปิด/ปิด · เวลา · เว็บ · ผู้รับ (ผู้ใช้/บทบาท — แสดงสถานะ "ผูก LINE แล้ว/ยัง" + ลิงก์ `line-bind.html`) · หัวข้อ (ติ๊ก) · ปุ่ม "ส่งตัวอย่างถึงฉัน" (นับโควตา 1) · ประวัติ 7 วัน |
| `cms-bookings.html` / ออเดอร์ / บัตร | หมายเหตุที่ประทับ (`FailureNote`) แสดงเหตุ "ปิดตามค่าตั้ง" / "โควตาข้อความเต็ม" อยู่แล้วทางเดิม — ไม่ต้องแก้ markup |

## §5 งานเบื้องหลัง (ทุกตัวผ่าน `Helpers/JobLock` · F4 #6 · `AdvisoryLockKey.BackgroundJob`)

| งาน | รอบ | พฤติกรรม |
| --- | --- | --- |
| `CustomerNotifyOutboxJob` (ใหม่) | 5 นาที | ประมวล `LineOaSwitchEvent` ที่ `ProcessedAt IS NULL` ทีละ 200 ราย/บริษัท/รอบ · ส่งผ่าน `IBookingNotifier` (`line.relink-invite` · คีย์ `relink:{linkId}:{eventId}`) · ประทับ `Invited/Skipped` · watermark = ProcessedAt ต่อ event |
| `StaffDailySummaryJob` (ใหม่) | 5 นาที | config ที่ `Enabled && Due(nowBkk)` · ต่อผู้รับ: claim `StaffDailySummaryDelivery` (unique) → ตรวจสิทธิ์ ณ วันส่ง → ประกอบ (อ่านอย่างเดียว · ทุก query `CompanyId == cid` + `siteIds.Contains`) → `TryReserveAsync(staffsum:{configId}:{date}:{userId})` → push OA กลาง → ประทับ · ครบทุกผู้รับ ⇒ `LastSentLocalDate = วันนี้` (watermark ต่อบริษัท) · ล้มทั้งชุด ⇒ `staff.summary.failed` ถึง Owner |
| `BookingReminderJob` · `CourseCreditExpiryJob` | เดิม | เปลี่ยนแค่จุดเรียก §3.3 |
| ล้าง `CustomerNotifyLog` > 13 เดือน | ต่อท้าย `PdpaRetentionPurgeJob` | ลบจริง (ไม่ใช่เอกสารบัญชี) |

หัวข้อสรุปและสิทธิ์ที่ต้องมี (ตรวจรายผู้รับ · ไม่มีสิทธิ์ = ไม่ใส่หัวข้อ ไม่ใช่ใส่ว่าง):

| หัวข้อ | แหล่ง | สิทธิ์ |
| --- | --- | --- |
| การจองวันนี้/พรุ่งนี้ ตามผู้ให้บริการ/บริการ | `SiteBookings` + `SiteBookingAssignment` | เว็บใน `CmsSiteScope.AllowedSiteIds(…, AnyRole)` |
| คลาส + ที่นั่งเต็มกี่ % | รอบคลาส (`ClassScheduleGenerator`) | 〃 |
| คิวผลิตตามสั่ง (วันพร้อมรับ · ค้างชำระ) | `SiteOrders` ที่ `RequestedReadyDate/EarliestReadyDate` (`CmsCommerce.cs:251,265`) สถานะยังไม่จบ + ยอดค้าง | `CmsOrderManage` + `CmsSiteScope.OrderRoles` |
| ออเดอร์รอจัดส่ง | `SiteOrders` Confirmed/Processing | 〃 |
| เช็คอิน/เช็คเอาต์ที่พัก (เฉพาะบริษัทที่มีที่พัก active) | `LodgingReservations` | `LodgingManage` |
| บัตรคอร์สหมดอายุใน 7 วัน | `CreditPasses` | `CmsOrderManage` (หรือสิทธิ์บัตรที่ CourseCreditController ใช้ — ใช้ตัวเดียวกัน) |

## §6 ความปลอดภัย · PDPA · ภาษี

- **Tenant isolation**: ทุก query ใหม่ `CompanyId == cid` (raw SQL ใส่เอง) · ตัวนับ/คีย์ล็อกต่อบริษัท · สรุปประจำวันประกอบ**ต่อผู้รับ** (ไม่ประกอบครั้งเดียวแล้วแจกทุกคน — ผู้รับเห็นต่างกัน)
- **หลาย instance**: ไม่มี static/IMemoryCache · claim + counter เป็น `UPDATE … WHERE … RETURNING` · unique index กันซ้ำ · งานผ่าน JobLock
- **ผู้รับสรุปต้องยังเป็นสมาชิกบริษัท** (`CompanyUser` active) + `User.LineUserId` ไม่ว่าง — ตรวจ ณ เวลาส่ง ไม่ใช่ตอนตั้งค่า · ข้อมูลลูกค้าในสรุป: ชื่อ + เวลา + บริการ เท่านั้น (ไม่ใส่เบอร์/อีเมล — ลดข้อมูลส่วนบุคคลที่ไหลผ่าน LINE · ม.37)
- **PDPA ข้อ 4 (คำเชิญผูกใหม่)**: ฐาน = ความต่อเนื่องของบริการที่ลูกค้ายินยอมไว้ (`PdpaConsentRecord` purpose `cms-line-notify:messaging` ของลิงก์เดิม) + ข้อความบริการ ไม่ใช่การตลาด ·
  ส่ง**ครั้งเดียว**ต่อการสลับ · มีประโยค "ไม่ต้องการรับ — ไม่ต้องทำอะไร" · ลิงก์ถูกถอน/ลบ (DSR erase) ⇒ ไม่เชิญ · ลิงก์ที่ `BlockedAt` ⇒ ไม่เชิญทาง LINE
- **`cart.abandoned` เป็นการตลาด** ⇒ ต้องมีความยินยอมการตลาดก่อน (เดิมไม่ตรวจ — บันทึกเป็นคำถาม §9 ข้อ 4 · แบบนี้แค่ย้ายช่องส่ง ไม่ขยายผู้รับ)
- `CustomerNotifyLog` เก็บ userId/อีเมล **ไม่ได้** — เก็บแค่ `TargetKind/TargetId` + ช่อง (เลี่ยงสำเนา PII ที่สาม) · log ใช้ `CustomerLineLinkRule.Mask`
- **ภาษี/เอกสาร**: แพ็กเสริมคิดเงินผ่าน `UsageEvent` → `UsageInvoicingJob` → `PlatformBillingDocumentIssuer` (ใบแจ้งหนี้ในบริษัทผู้ให้บริการ · Prepaid ตัดเครดิตตอนบันทึก) — **เส้นเดิมของ `documents.topup`** ไม่มีเลขเอกสาร/ใบกำกับเส้นใหม่ ·
  ไม่มี throw ใหม่ในเส้นเอกสารบัญชี · โควตาเต็ม**ไม่บล็อกงานธุรกิจ**ใด ๆ (บล็อกแค่การส่งข้อความผ่านช่องกลาง)
- **ล้มดัง 3 ที่**: Suppressed/Fail ประทับบนรายการ (`FailureNote`) · คำตอบผู้กด (เส้นเดิม) · เจ้าของได้แจ้งเตือน 80/100% ครั้งเดียวต่องวด

## §7 แผนเทสต์

| ชั้น | เทสต์ (ชื่อที่เสนอ) |
| --- | --- |
| pure | `CustomerNotifyPolicyTests`: ไม่มีแถว = เปิดทั้งสอง (ล็อกพฤติกรรมเดิม) · เว็บชนะบริษัท · คีย์ชนะ `*` · ปิด LINE แต่ลูกค้าผูก ⇒ อีเมลเท่านั้น · ปิดทั้งสอง ⇒ Suppressed · **ทิศตรงข้าม**: เว็บ A ตั้งปิด เว็บ B ไม่ถูกแตะ |
| pure | `EmailPlan` 3 ค่า × ตั้ง/ไม่ตั้ง (6 เคส) · null = ถอยไประบบ (ตรงกับ diff ฝ่ายค้าน 211) |
| pure | `NotifyQuotaPolicyTests`: ช่องร้านไม่นับ · 80/100 · งวดตามเวลาไทย (16:30 UTC 31 ต.ค. = 23:30 ไทย ⇒ งวด ต.ค. · 17:30 UTC 31 ต.ค. = 00:30 ไทย 1 พ.ย. ⇒ งวด พ.ย.) · โบนัสหมดอายุไม่นับ |
| pure | `StaffDailySummaryPlanTests`: ถึงกำหนด/ส่งตามหลัง/ไม่ backfill · ตัด 5,000 ตัว ≤5 ก้อน · ไม่มีสิทธิ์ = ไม่มีหัวข้อ |
| pure | `CustomerRelinkInvitePlanTests`: สมาชิก/การจองในอนาคต/ออเดอร์ค้าง ได้เชิญ · การจองผ่านไปแล้ว/ถอนยินยอม/บล็อก ไม่ได้ · OA เดิม = กลาง ⇒ LINE · OA เดิม = ร้าน ⇒ อีเมล |
| proxy (แบบ `CmsRound211LineNotifyTests`) | `BookingNotifier`: โควตาเต็ม + มีอีเมลร้าน ⇒ ส่งทางร้าน + Warning · โควตาเต็ม ไม่มีช่องร้าน ⇒ Suppressed (ไม่ส่ง) · ส่งล้ม ⇒ คืนหน่วย |
| Db (`Accounting.Tests/Db`) | `NotifyQuotaDbTests`: 2 คำขอพร้อมกันที่ `Used = limit-1` ⇒ ผ่าน 1 · `MessageKey` ซ้ำ ⇒ ส่งครั้งเดียว · claim หมดเวลาแล้วรับช่วงได้ · เตือน 80% ครั้งเดียว · `PurchaseNotifyTopUp` ⇒ UsageEvent 1 แถว + โบนัส |
| Db | `StaffDailySummaryDbTests`: บริษัท B ไม่โผล่ในสรุปของ A · ผู้รับไม่มีสิทธิ์ออเดอร์ไม่เห็นหัวข้อออเดอร์ · รันซ้ำวันเดียวกัน = ไม่ส่งซ้ำ |
| ด่านจุดเรียก | `tools/required_call_site_check.py`: ทุกผู้เรียก `IBookingNotifier.SendAsync` ส่ง `EventKey` ที่อยู่ในแคตตาล็อก · `LodgingService.TryNotifyAsync`/`AbandonedCartService`/ตอบกลับฟอร์ม ห้ามเรียก `IEmailService` ถึงลูกค้าตรง · `NotificationEngine.DispatchLineAsync` ต้องใช้ OA กลาง |

## §8 แบ่งแพ็กลงมือ (ไฟล์ไม่ชนกัน · ทำหลัง diff ฝ่ายค้านรอบ 211 เข้า branch)

| แพ็ก | ขอบเขต | ไฟล์ที่เป็นเจ้าของ (คนเดียว) |
| --- | --- | --- |
| **A — ค่าตั้ง + โควตา + จุดเรียก** (ข้อ 1·2·3) | §2.1–2.3 · `CustomerNotifyEvents/Policy` · `NotifyQuotaPolicy` · `NotifyQuotaService` · แก้ `BookingNotifier`/`CmsShopNotifier`/`CustomerNotifyChannel` · จุดเรียก §3.3 แถว booking/class/order/credit/lodging/cart/form · `MeteringController` · `SubscriptionService.EffectivePlan` · `AddOnCodes` · หน้า settings/cms-store-settings/usage/subscription/addons/admin plans/admin-addons | `Helpers/CustomerNotify*.cs` · `Helpers/NotifyQuotaPolicy.cs` · `Services/Implementations/Cms/BookingNotifier.cs` · `CmsShopNotifier.cs` · `*Notify.cs` ของ CmsBooking/CmsCommerce · `CourseCreditService.Membership.cs` · `LodgingService.Operations.cs` · `AbandonedCartService.cs` · `CmsCustomerService.cs` · `QuotaService.cs`/`NotifyQuotaService.cs` · `Subscription.cs` · `DatabaseMigrationHelper` (บล็อก `CmsRound212NotifyStatements` — แพ็กเดียวที่แตะ) |
| **B — เชิญผูกใหม่** (ข้อ 4) · เริ่มหลัง A merge (ใช้ `SendAsync(CustomerNotifyRequest)`) | `LineOaSwitchEvent` (ตารางเพิ่มในบล็อก migration ของ A ล่วงหน้า) · `CustomerRelinkInvitePlan` · `CustomerNotifyOutboxJob` · `LineConfigController.Update` · webhook ×2 · ป้ายในหน้า LINE | `Helpers/CustomerRelinkInvitePlan.cs` · `Services/Background/CustomerNotifyOutboxJob.cs` · `CustomerLineLinkService.cs` · `LineConfigController.cs` · `CustomerLineWebhookController.cs` · `LineWebhookController.cs` |
| **C — สรุปประจำวัน + แก้ push พนักงาน** (ข้อ 5 + บั๊ก §0) · ขนานกับ B ได้ | `StaffDailySummary*` · `ILineNotifyService.TryPushViaAsync` · `NotificationEngine.DispatchLineAsync` · หน้าใหม่ + เมนู · ตาราง §2.5 (อยู่ในบล็อก migration ของ A) | `Helpers/StaffDailySummaryPlan.cs` · `Services/Background/StaffDailySummaryJob.cs` · `Services/Implementations/StaffDailySummaryService.cs` · `LineNotifyService.cs`/`ILineNotifyService.cs` · `NotificationEngine.cs` · `Controllers/StaffDailySummaryController.cs` · `wwwroot/pages/staff-daily-summary.html` |

ทุกแพ็ก: `DOCUMENT_FLOW.md` (§CMS แจ้งเตือน) · `ACCOUNT_STRUCTURE.md` §5/§6.1b (มิเตอร์ใหม่ + เพดานแพ็กเกจ — แพ็ก A) · `TEST_PLAN.md` · DECISIONS แถวใหม่ (เสนอ 171) · `bash tools/check_all.sh` · ส่ง diff ฝ่ายค้าน (แตะเงิน/สิทธิ์)

## §9 คำถามที่ต้องให้เจ้าของตอบจริง

1. **ตัวเลข**: เพดานข้อความช่องกลางต่อแพ็กเกจ (เสนอ Trial 100 · แพ็กต่ำสุด 300) · ราคาแพ็กเสริม 500 ข้อความ · กลุ่มบัญชี (AccountSubscription) นับต่อบริษัทหรือรวมกลุ่ม (แบบนี้: ต่อบริษัท)
2. **อีเมลถึงแขกที่พัก** ย้ายเข้าเส้นนี้ ⇒ ออกทางอีเมลของร้านเมื่อร้านตั้งไว้ + นับโควตาเมื่อใช้ SMTP ระบบ — ยอมรับ? (ทางเลือก: ยกเว้นที่พักจากโควตา)
3. **สรุปประจำวัน/แจ้งเตือนพนักงานนับโควตาไหม** — แบบนี้: สรุปประจำวัน**นับ** (1 หน่วย/ผู้รับ/วัน · ต้นทุนจริงของ OA กลาง) · แจ้งเตือนงานรายเหตุการณ์ของ `NotificationEngine` **ไม่นับ** (เป็นงานภายในที่เดิมฟรี — ถ้านับ ผู้อนุมัติจะพลาดงานเมื่อโควตาเต็ม)
4. **`cart.abandoned`** เป็นการตลาด — จะบังคับความยินยอมการตลาด (PDPA) ก่อนส่งในรอบนี้ หรือแยกรอบ?

## §10 คำตัดสินผู้ประสานงาน (2026-10-30 · ตอบ §9)
- ตัวเลขโควตา/ราคาแพ็ก add-on: เก็บใน `PlanTemplate` + ตาราง add-on เดิม (แก้ได้ที่หน้าแอดมินแพลตฟอร์ม) — ไม่ฝังในโค้ด · ค่าตั้งต้นเสนอใน migration ให้เจ้าของปรับ
- อีเมลแขกที่พัก (`LodgingService.Operations.TryNotifyAsync`) ย้ายเข้าเส้นกลาง (นโยบาย/โควตา/ช่องทางเดียวกัน)
- ข้อความถึงพนักงาน: สรุปรายวันนับโควตา · แจ้งเตือนเหตุการณ์ไม่นับ
- อีเมลกู้ตะกร้า: ต้องยินยอมการตลาด (ทำแล้วรอบ 209 P6) + เข้านโยบายเหตุการณ์ `cart.abandoned`

