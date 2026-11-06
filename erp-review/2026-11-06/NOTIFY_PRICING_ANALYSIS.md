# NOTIFY_PRICING_ANALYSIS.md: ราคาและโควตาข้อความ LINE/อีเมลผ่านช่องกลางของ NextAcc (ตอบ Q9)

> ทีมวิจัย 2026-11-06 · ต่อจาก `LINE_MULTICHANNEL_DESIGN.md` §5 และ §10 (Q2 นับตามสมาชิก · Q3 แยกโควตา LINE/อีเมล · Q4 อีเมลหักโควตา · Q7 ถอยไปอีเมลเป็นค่าตั้งต้น)
> **เอกสารวิเคราะห์ ไม่มีการแก้โค้ด** · ตัวเลขทุกตัวเป็น "ค่าที่เสนอให้เจ้าของตั้ง" บนหน้าแอดมิน (`admin-quota.html` · `/admin/addons.html`) ไม่ใช่ค่าที่ระบบตั้งเอง
> (หลัก DECISION_DOCTRINE: ห้ามแต่งค่ากติกาภายนอก ⇒ ทุกแถวมีแหล่งที่มาและระดับความเชื่อมั่น)

## 0. สรุปสั้น

| | ค่าที่แนะนำ |
| --- | --- |
| ต้นทุน LINE ต่อข้อความ (OA กลาง แผน Pro) | **฿0.06 ไม่รวม VAT** เมื่อเกิน 35,000 ข้อความ/เดือน · แผน Basic ฿0.10 · ใช้ ฿0.10 เป็นต้นทุนวางแผน (กรณีแย่) |
| ต้นทุนอีเมลต่อฉบับ | ฿0.004–0.06 แล้วแต่ผู้ให้บริการ · **ยังไม่รู้ว่าโปรดักชันใช้เจ้าใด** (ตั้งที่ `admin/system-email.html` ตอนรันระบบ ไม่อยู่ใน repo) |
| โควตา LINE ที่รวมในแพ็กเกจ/เดือน | Free 100 · Starter 300 · Professional 1,000 · Enterprise 5,000 · ทดลองใช้ 100 (ตรงกับ `NotifyQuotaPolicy.SuggestedLimit` เดิม) |
| โควตาอีเมลที่รวมในแพ็กเกจ/เดือน | ตั้งจากยอดใช้จริงตามสูตร §4 · ค่าตั้งต้นก่อนวัดคือ Free 300 · Starter 1,500 · Professional 6,000 · Enterprise ไม่จำกัด (-1) |
| แพ็ก LINE | **1,000 ข้อความ ฿290** (รวม VAT · อายุ 60 วัน) · ทางเลือกแพ็กใหญ่ 5,000 ข้อความ ฿990 |
| แพ็กอีเมล | **5,000 ฉบับ ฿190** (รวม VAT · อายุ 60 วัน) ใช้ได้เมื่อต้นทุนอีเมล ≤ ฿0.02/ฉบับ · ถ้าใช้ Mailgun Basic หรือเจ้าที่แพงกว่า ⇒ 2,000 ฉบับ ฿190 |
| ความเสี่ยงใหม่ที่ต้องรู้ | ข่าว (แหล่งรองแหล่งเดียว) ว่า LINE ประเทศไทยจะเริ่ม**จำกัด reply ฟรีไว้ที่ 25,000 ข้อความ/เดือนต่อ OA ตั้งแต่ 1 ก.พ. 2570** ซึ่งกระทบสมมติฐาน "reply ไม่เสียเงิน" ใน design §5.1 และบอทบัญชีของ ERP บน OA กลาง (§1.4) |

---

## 1. ต้นทุน LINE Official Account (ประเทศไทย)

### 1.1 แผนและราคา

> ⚠ เซสชันนี้เปิด `lineforbusiness.com`, `developers.line.biz` และ `terms2.line.me` ด้วย WebFetch ไม่ได้ (DNS ถูกบล็อกโดยเครือข่ายของเซสชัน) ·
> **หน้าเอกสาร developers.line.biz อ่านได้จาก repo ต้นฉบับทางการ `github.com/line/line-developers-docs-source`** (commit ล่าสุด 2026-11-02) ⇒ กติกาการนับ (§1.2) เป็น**แหล่งทางการ**
> ส่วนราคาไทยเอกสารทางการชี้ไปที่ `lineforbusiness.com/th/service/line-oa-features/broadcast-message` ซึ่งเปิดไม่ได้ ⇒ ราคาได้จากแหล่งรองหลายแหล่งที่ตรงกัน

| แผน (TH) | ค่าบริการ/เดือน (ไม่รวม VAT 7%) | ข้อความฟรี/เดือน | ข้อความส่วนเกิน | ความเชื่อมั่น |
| --- | --- | --- | --- | --- |
| Free | ฿0 | 300 | **ซื้อเพิ่มไม่ได้** (ส่งเกินแล้ว API ตอบ error) | สูง (แหล่งรอง ≥ 5 แหล่งตรงกัน) |
| Basic | ฿1,280 | 15,000 | ฿0.10/ข้อความ | สูง (cipher · sellsuki · thunder · idmcouncil · suriya) |
| Pro | ฿1,780 | 35,000 | ฿0.06/ข้อความ | สูง (suriya · linebot.pro · goonline) |
| MyCustomer CRM (เสริม) | ฿369 (Free/Basic) · รวมใน Pro | – | – | กลาง |

- **ข้อมูลที่ขัดกัน:** บทความ sphereagency ให้แผน "Light ฿1,200/5,000 ข้อความ" และ "Standard ฿5,000" ซึ่งเป็นชื่อแผนแบบญี่ปุ่น (Communication/Light/Standard · ตัวอย่างในหน้า pricing ทางการเป็นเงินเยน) ⇒ **ไม่ใช้** · design §7.4 เคยเห็นตัวเลข "ฟรี 500" ⇒ ไม่พบแหล่งปี 2026 ที่ยืนยัน · แหล่งหนึ่งระบุว่าตรวจกับหน้าทางการเมื่อ 29 ก.ย. 2569 และได้ตัวเลขตามตาราง
- **VAT:** ราคาไม่รวม VAT 7% · LINE Company (Thailand) ออกใบกำกับภาษีได้ ⇒ NextAcc ที่จด VAT ขอคืนภาษีซื้อได้ ⇒ **ต้นทุนจริงใช้ราคาไม่รวม VAT** (ถ้า NextAcc ยังไม่จด VAT ให้คูณ 1.07)
- **ค่า break-even ระหว่าง Basic กับ Pro:** `1,280 + 0.10 × (x − 15,000) = 1,780` ⇒ **x = 20,000 ข้อความ/เดือน** · เกินกว่านี้ Pro ถูกกว่า (แหล่งรองให้ตัวเลขเดียวกัน)
- **OA กลางต้องไม่อยู่บนแผน Free:** แผน Free ซื้อข้อความเพิ่มไม่ได้ ⇒ ข้อความที่ 301 ของทั้งแพลตฟอร์มจะถูก LINE ปฏิเสธ (สถานะ HeldQuota ตาม §5.4 ของ design)
- **ต้องตั้ง "เพดานข้อความส่วนเกิน" ใน LINE OA Manager** (ทางการ: *"select a subscription plan that allows you to send additional messages. Here, set a maximum number of additional messages"*) ⇒ ตั้งให้มากกว่าผลรวมโควตาที่ขายไป + 20% และให้งาน `NextAccOaQuotaWatch` อ่าน `GET /v2/bot/message/quota` เทียบกับยอดนี้

### 1.2 กติกาการนับ (แหล่งทางการ · `docs/en/docs/messaging-api/pricing/index.html.md`)

| ประเด็น | ข้อความทางการ | ผลต่อการออกแบบ | ความเชื่อมั่น |
| --- | --- | --- | --- |
| หน่วยนับ | *"The number of messages is counted by the number of people you send a message to. Suppose you send a push message with four message objects in a single request to a chatroom with five people. Here, the number of messages sent is five."* | **ยืนยัน Q2**: push เข้ากลุ่ม = จำนวนสมาชิกที่ได้รับ · ใส่ message object ได้ถึง 5 ชิ้นต่อคำขอโดยไม่เพิ่มต้นทุน ⇒ การ์ด + ข้อความสรุปส่งในคำขอเดียว | สูง (ทางการ) |
| นับวิธีใด | นับ: push · multicast · broadcast · narrowcast · **ไม่นับ: reply** | design §5.1 ถูกต้อง **ณ วันนี้** | สูง (ทางการ) แต่ดู §1.3 |
| ผู้รับที่บล็อก/ไม่มีอยู่ | *"If you send a message to a user who blocked your LINE Official Account or a user ID that doesn't exist, the message isn't counted."* | LINE ไม่เก็บเงิน แต่ระบบเรายังตัดโควตาผู้เช่าเมื่อ API ตอบ 200 ⇒ ยอมรับได้ (ต่างกันเล็กน้อย) · ห้ามส่งซ้ำไปหา userId ที่ไม่มีอยู่ (development guidelines ห้ามไว้) | สูง |
| บอทนับเป็นผู้รับไหม | เอกสารไม่ระบุ · ปลายทาง `members/count` นับเฉพาะผู้ใช้ | design ใช้ `max(1, memberCount − 1)` · **เสนอให้ใช้ `memberCount` ตรง ๆ** (ค่าที่ API คืนไม่รวมบอทอยู่แล้ว ตามความเข้าใจของทีม) ⇒ ⚠ ต้องทดสอบด้วยกลุ่มจริง 3 คนแล้วดู `/quota/consumption` ก่อน-หลัง | ⚠ ต่ำ |
| เกินโควตา | *"an error response will be returned and the messages won't be sent"* | แถวคิว → HeldQuota (เหตุ `nextacc-oa-limit`) ตาม design | สูง |
| ส่งซ้ำ | `X-Line-Retry-Key` อายุ 24 ชม. · ส่งซ้ำได้ 409 (`retrying-api-request`) | ต้องใส่ทุก push ⇒ ไม่เสียเงินสองครั้งเมื่อ timeout | สูง |
| หนึ่งกลุ่มมี OA ได้ครั้งละหนึ่งบัญชี + ต้องเปิด "Allow bot to join group chats" | *"At any time, only one LINE Official Account can be in a group chat or multi-person chat."* (`group-chats`) | ยืนยัน design §3.c-4 | สูง |

### 1.3 การเปลี่ยนแปลงที่กำลังจะมา: reply API ไม่ฟรีทั้งหมด (⚠ แหล่งรองแหล่งเดียว)

- relevantaudience.com (2026-11) รายงานประกาศของ LINE ประเทศไทยว่า **ตั้งแต่ 1 ก.พ. 2570 แต่ละ OA ได้ reply ฟรี 25,000 ข้อความ/เดือน (ไม่สะสม)** · ใช้ไม่จำกัดได้ในราคา **฿5,000/เดือน** ผ่านเอเจนซีที่ LINE กำหนดบน Agency Global Platform · ไม่ได้ระบุราคาต่อข้อความเมื่อเกิน
- ไม่พบประกาศนี้ในเอกสาร developers ฉบับ 2026-11-02 (หน้า pricing ยังเขียนว่า reply ไม่นับ) ⇒ **ความเชื่อมั่นต่ำ–กลาง** ต้องให้คนเปิด LINE OA Manager/อีเมลจาก LINE ของ @nextacc ตรวจ
- **ผลต่อ NextAcc:** OA กลางตอบด้วย reply ทั้งบอทบัญชีของ ERP (`LineBotService` · ผู้ใช้แชตสร้างเอกสาร/ถามข้อมูล) · `#HeyNextAcc` · ผลการผูก · คำตอบ "ข้อความนี้ไม่ถึง {บริษัท}" ⇒ ต้องวัดจำนวน reply ต่อเดือนตอนนี้ (นับ webhook event ที่มี replyToken แล้วตอบ) ถ้าใกล้ 25,000 ⇒ งบ ฿5,000/เดือนเป็นต้นทุนคงที่ก้อนใหม่ · **ไม่ควรตัดโควตาผู้เช่าด้วย reply** (ต้นทุนคงที่ของแพลตฟอร์ม · ผู้เช่าควบคุมไม่ได้)
- เพิ่มในงาน `NextAccOaQuotaWatch`: บันทึกยอด reply ต่อเดือนของ OA กลาง (ตัวนับของเราเอง เพราะ `/quota/consumption` ไม่นับ reply)

---

## 2. ต้นทุนอีเมล

### 2.1 ระบบใช้ผู้ให้บริการใด

- `appsettings.json` → `Email.SmtpHost = ""` (ค่าว่างใน repo) · `FromAddress = noreply@nextacc.com`
- `Models/Enums/AllEnums.cs` `EmailProvider { Smtp, MicrosoftGraph, GmailApi, SendGrid }` · หน้า `wwwroot/admin/system-email.html` ตั้งค่า SMTP (ตัวอย่าง Gmail/Office 365) และแสดง SendGrid เป็นค่าเดิมที่หน้านี้ตั้งไม่ได้
- ⇒ **repo ไม่บอกว่าโปรดักชันใช้เจ้าใด** · ทีมจึงทำตารางทุกทางเลือก · **เจ้าของต้องบอกเจ้าที่ใช้จริง** ก่อนยืนยันราคาแพ็กอีเมล

### 2.2 ราคาต่อ 1,000 ฉบับ (USD → THB ที่ ฿35/USD · ความเชื่อมั่น: กลาง · แหล่งรองปี 2026 ทั้งหมด)

| ผู้ให้บริการ | ราคา | ต่อ 1,000 ฉบับ (THB) | ต่อฉบับ | หมายเหตุ |
| --- | --- | --- | --- | --- |
| Amazon SES (à la carte เดิม) | $0.10/1,000 + ไฟล์แนบ $0.12/GB | ≈ ฿4.2 (รวมไฟล์แนบ PDF ~150 KB) | ฿0.004 | บัญชีใหม่หลัง 21 ก.ค. 2569 เริ่มที่แผน Essentials $0.16/1,000 (≈ ฿6.2 รวมไฟล์แนบ) |
| SendGrid Essentials | $19.95/เดือน ≈ 50,000 ฉบับ (บางแหล่งว่า 40,000) | ฿14–17 เมื่อใช้เต็ม | ฿0.014–0.017 | Pro $89.95 = 100,000 + IP เฉพาะ |
| Mailgun Basic / Foundation | $15 = 10,000 · $35 = 50,000 · ส่วนเกิน $1.80 / $1.30 ต่อ 1,000 | ฿52–63 (Basic) · ฿24–46 (Foundation) | ฿0.025–0.063 | แพงที่สุดในกลุ่ม |
| Brevo Starter | ตั้งแต่ $9/เดือน (คิดตามจำนวนส่ง) | แล้วแต่ขั้น | ~฿0.01–0.03 | |
| Google Workspace / Microsoft 365 SMTP | ค่าที่นั่งรายเดือน (ต้นทุนคงที่) | ~0 ต่อฉบับ | – | **เพดานรายวัน**: Gmail ~2,000 ฉบับ/ผู้ใช้/วัน · SMTP relay 100 ผู้รับ/ธุรกรรม · Exchange Online 10,000 ผู้รับ/วัน + 30 ฉบับ/นาที + เพดานทั้ง tenant (TERRL) ⇒ ไม่เหมาะกับการส่งแทนผู้เช่าจำนวนมาก |

**ข้อสังเกต:** ต้นทุนต่อฉบับของอีเมลต่ำกว่า LINE 3–15 เท่า ต้นทุนที่สำคัญกว่าคือ**ชื่อเสียงของโดเมน** `nextacc.com` (bounce/spam จากผู้เช่ารายเดียวทำให้อีเมลของทุกบริษัทเข้า spam) ⇒ เหตุผลที่ต้องคง `PlatformEmailQuota` รายวันไว้ตาม design §5.2

### 2.3 เพดานรายวันเดิมกำหนดเพดานรายเดือนที่ใช้จริงได้

`Helpers/SignEmailThrottle.cs`: `DefaultPlatformDailyCap = 200` (บริษัทที่ยืนยันตัวตน) · `UnverifiedPlatformDailyCap = 20` · ปรับได้ที่ config `SignRequestEmail:PlatformDailyCapPerCompany`
⇒ บริษัทหนึ่งส่งผ่าน SMTP กลางได้ไม่เกิน **≈ 6,200 ฉบับ/เดือน** (200 × 31) และบริษัทที่ยังไม่ยืนยันได้ไม่เกิน **≈ 620** ·
**เพดานรายเดือนที่ตั้งสูงกว่านี้ไม่มีผล** จนกว่าจะเพิ่มเพดานรายวัน ⇒ ตาราง §3.1 ตั้ง Professional = 6,000 และ Enterprise = ไม่จำกัด (เพดานรายวันคุมแทน)
⚠ ตรวจด้วยว่าเพดานรายวันนับ "ฉบับ" (แถว log) ขณะที่ `NotifyQuotaPolicy` นับ "ผู้รับ" ⇒ อีเมลที่มี To+Cc 3 คน = 1 ฉบับต่อวัน แต่ 3 หน่วยต่อเดือน (L4 ต้องเลือกหน่วยเดียวกัน · ข้อเสนอ: นับผู้รับเพื่อให้ตรงกับนโยบายเดิม และไม่นับ `csemail@etax.teda.th` ที่ CC ตามกฎหมาย e-Tax)

---

## 3. ตัวเลขที่เสนอ

### 3.1 โควตาที่รวมในแพ็กเกจ (ชื่อแผนตาม `Data/SeedPlanTemplates.cs`)

| แม่แบบ (enum) | ราคา/เดือน (รวม VAT) | LINE/เดือน | ต้นทุน LINE ที่ ฿0.10 | % ของราคา (ไม่รวม VAT) | อีเมล/เดือน (ค่าตั้งต้นก่อนวัด) | ทดลองใช้ LINE/อีเมล |
| --- | --- | --- | --- | --- | --- | --- |
| Free Edition (`FreeTrial`, `IsPermanentFree`) | ฿0 | **100** | ฿10 | – | **300** | – |
| Starter (`Basic`) | ฿590 | **300** | ฿30 | 5.4% | **1,500** | 100 / 300 |
| Professional (`Pro`) | ฿1,490 | **1,000** | ฿100 | 7.2% | **6,000** | 100 / 300 |
| Enterprise (`Enterprise`) | ฿4,990 | **5,000** | ฿500 | 10.7% | **-1 (ไม่จำกัด · เพดานรายวันคุม)** | 100 / 300 |

- LINE ใช้ค่าเดิมของ `NotifyQuotaPolicy.SuggestedLimit` (100/300/1,000/5,000 · ทดลองใช้ 100) ⇒ ไม่ต้องเปลี่ยนคำแนะนำในโค้ด แค่ย้ายจากก้อนรวมไปที่ `MaxSharedLineMessagesPerMonth` ตาม design §5.2
- ที่ต้นทุนจริงของแผน Pro (฿0.06) สัดส่วนต้นทุนลดเหลือ 3.3% / 4.3% / 6.4%
- **ข้อควรรู้เรื่องกลุ่ม:** Starter 300 หน่วย = push เข้ากลุ่ม 200 คนได้ราว 1.5 ครั้ง ⇒ หน้าผูกกลุ่มต้องแสดง "กลุ่มนี้ใช้ {n} หน่วยต่อข้อความ · โควตาเดือนนี้ส่งได้อีก {k} ครั้ง" (เซิร์ฟเวอร์ประกอบ) และแนะนำให้ผู้เช่าเชื่อม OA ของตัวเองสำหรับกลุ่มใหญ่ (ไม่นับโควตา)
- **การทับรายบริษัท (`Subscription.MaxShared*PerMonth`)**: ใช้กลไกเดิม (`-1` = ไม่จำกัด · บังคับเหตุผล + audit chain) · ข้อเสนอแนวปฏิบัติ: ให้ทับได้ไม่เกิน 10 เท่าของค่าแพ็กเกจ โดยไม่ต้องเปลี่ยนราคา · เกินกว่านั้นให้ขายแพ็ก หรือทำสัญญาองค์กร
- อีเมล: ค่าตั้งต้นในตารางเป็น**ค่าก่อนวัด** · ค่าที่ตั้งจริงให้ใช้ `max(ค่าตั้งต้น, ปัดขึ้น(P95 × 1.5))` จาก SQL §4 (ให้ลูกค้า 95% ของแต่ละแผนไม่ชนเพดาน และลูกค้าที่เหลือชนที่ 1.5 เท่าของ P95) · ลูกค้าเดิมที่ยอดสูงกว่าเพดานใหม่ ⇒ ทับรายบริษัทเป็น `ceil(max เดือน × 1.2)` ก่อนเปิดตัด (เหตุผล "คงสิทธิ์ลูกค้าเดิม L4")

### 3.2 แพ็กซื้อเพิ่ม

**ข้อจำกัดของโครงสร้างเดิม:** 1 ทรัพยากรมีขนาดแพ็กได้ค่าเดียว (`ApiFeature.PackSize` · 1–100,000) + ราคาเดียว (`ApiPricingPlans.UnitPrice`) · ซื้อได้ครั้งละ ≤ 100 แพ็ก (`QuotaAddOnPricing.MaxUnitsPerPurchase`) · อายุ 1–730 วัน (ค่าตั้งต้น 60) ⇒ แพ็กหลักต่อช่องหนึ่งแพ็ก ส่วนแพ็กใหญ่ราคาส่วนลดต้องใช้ ResourceKey แยก (`notify.topup.line.xl`) ซึ่งเป็นงานเพิ่มเล็กน้อยใน L4

สมมติฐานในการคิดมาร์จิน: ราคาขายรวม VAT ⇒ รายได้ = ราคา ÷ 1.07 · ค่าธรรมเนียม gateway แย่สุด 3.65% + VAT ของค่าธรรมเนียม (≈ 3.9% ของยอด · บัตรเครดิต Omise · PromptPay ถูกกว่า) · ไม่คิดภาษีเงินได้ · **ภาษีหัก ณ ที่จ่าย 3% ไม่ใช่ต้นทุน** (เป็นเครดิตภาษี · ใช้กติกาเดิม `Helpers/PlatformWhtSuggestion` ประเภทเงินได้ ม.40(2) เกณฑ์ ฿1,000 ต่อสัญญา · รอบ 234/235 · แพ็กเล็กส่วนใหญ่ต่ำกว่าเกณฑ์จึงไม่หัก)

| แพ็ก | ราคา (รวม VAT) | ไม่รวม VAT | ต่อหน่วย (รวม VAT) | ต้นทุนที่ ฿0.06 | มาร์จิน @฿0.06 | มาร์จิน @฿0.10 | สถานะ |
| --- | --- | --- | --- | --- | --- | --- | --- |
| `notify.topup` เดิม (รวม) 500 ข้อความ | ฿99 | ฿92.52 | ฿0.198 | ฿30 | 63% | 42% | seed รอบ 212 "รอเจ้าของยืนยัน" ⇒ **ปิดการขาย**เมื่อเปิดแพ็กแยก (Q3 ไม่ใช้ก้อนรวม) โบนัสเดิมให้ใช้จนหมดอายุ |
| **LINE 1,000 ข้อความ** (`notify.topup.line`) | **฿290** | ฿271.03 | ฿0.29 | ฿60 | **74%** | 59% | แนะนำ (แพ็กหลัก) |
| LINE 5,000 ข้อความ (`notify.topup.line.xl` · ทางเลือก) | ฿990 | ฿925.23 | ฿0.198 | ฿300 | 63% | 42% | ทางเลือกสำหรับผู้เช่าที่ใช้กลุ่ม |
| **อีเมล 5,000 ฉบับ** (`notify.topup.email`) | **฿190** | ฿177.57 | ฿0.038 | ฿21 (SES) · ฿85 (SendGrid เต็ม) | 84% (SES) · 48% (SendGrid) | ขาดทุนถ้าเป็น Mailgun Basic (฿315) | ใช้เมื่อผู้ให้บริการ ≤ ฿0.02/ฉบับ |
| อีเมล 2,000 ฉบับ (แทนแถวบน ถ้าใช้ Mailgun/เจ้าแพง) | ฿190 | ฿177.57 | ฿0.095 | ฿126 (Mailgun ส่วนเกิน) | 25% | – | ทางสำรอง |

- ตัวเลขที่ design §5.4 เคยร่าง (LINE 1,000 ฿390 · อีเมล 5,000 ฿190) ⇒ ทีมเสนอลด LINE เป็น **฿290** เพราะ ฿390 แพงกว่า LINE ขายตรงเกือบ 4 เท่าของราคา Basic (฿0.10) และ ~6.5 เท่าของ Pro ⇒ ผู้เช่าที่ใช้มากจะคำนวณได้ว่าเปิด OA ของตัวเองถูกกว่า (ซึ่งเป็นสิ่งที่เราอยากให้เกิดกับผู้ใช้หนัก แต่ไม่ควรเกิดกับผู้ใช้ปานกลาง) · ฿290 ยังให้มาร์จิน ≥ 59% แม้ OA กลางอยู่บนแผน Basic
- **เปรียบเทียบกับทางเลือกของผู้เช่า:** OA ของตัวเองแผน Free = 300 ข้อความ/เดือนฟรี (ไม่นับโควตา NextAcc) ⇒ ผู้เช่าที่ใช้ LINE เกิน ~600/เดือน คุ้มที่จะเชื่อม OA ของตัวเอง · หน้าซื้อแพ็กควรมีข้อความนี้ (ตรงกับ `NotifyQuotaPolicy.FullReason` ที่แนะนำ OA ของร้านอยู่แล้ว)
- ราคาปัดเป็นเลขลงท้าย 90 ตามแบบราคาแผนเดิม (590/1,490/4,990)

### 3.3 สถานการณ์ต้นทุนแย่สุด

| สถานการณ์ | หน่วย | ต้นทุน @฿0.06 | @฿0.10 | การป้องกัน |
| --- | --- | --- | --- | --- |
| push เข้ากลุ่ม 200 คน 1 ครั้ง | 200 | ฿12 | ฿20 | Q2 นับตามสมาชิก ⇒ ผู้เช่าจ่ายผ่านโควตา |
| ทวงชำระรายวันเข้ากลุ่ม 200 คน 30 วัน | 6,000 | ฿360 | ฿600 | ตัดโควตา ⇒ Starter หมดในวันที่ 2 · เสนอ: กฎที่ส่งเข้ากลุ่มบังคับระยะห่างขั้นต่ำ 3 วัน (ค่าตั้งของแอดมิน) |
| ผู้เช่า Enterprise ใช้ครบ 5,000 + ซื้อแพ็ก 100 × 1,000 | 105,000 | ฿6,300 | ฿10,500 | รายได้แพ็ก ฿29,000 ⇒ มีกำไร · เพดาน `MaxUnitsPerPurchase` |
| ทั้งแพลตฟอร์ม: 300 Starter + 150 Pro + 50 Enterprise ใช้โควตาครบ | 490,000 | ≈ ฿29,100 (Pro ฿1,780 + ส่วนเกิน 455,000 × 0.06) | – | ≈ 4.8% ของรายได้แผนรวม ≈ ฿607,000 (ไม่รวม VAT) · ในทางปฏิบัติใช้ไม่ครบ |
| ข้อความระบบที่ไม่ตัดโควตา (ประกาศผูกกลุ่ม · แจ้งปลดผูก · แจ้งโควตา 80/100%) | ~3–5 ต่อบริษัทต่อเดือน | ฿0.3/บริษัท | ฿0.5 | ต้นทุนแพลตฟอร์ม · ประกาศผูกกลุ่ม 200 คน = 200 หน่วย (฿12) ต่อการผูก 1 ครั้ง ⇒ **เสนอให้ส่งประกาศผูกกลุ่มด้วย reply** (ตอบข้อความ `#HeyNextAcc` ถัดไป หรือ postback ยืนยันในกลุ่ม) แทน push |
| reply เกิน 25,000/เดือน หลัง ก.พ. 2570 (ถ้าประกาศจริง) | – | ฿5,000/เดือนคงที่ | – | §1.3 |

**จุดคุ้มทุน:** ค่าคงที่ของ OA กลางแผน Pro ฿1,780/เดือน ÷ กำไรต่อแพ็ก LINE 1,000 (≈ ฿200 หลังต้นทุนและค่าธรรมเนียม) ⇒ **ขาย ~9 แพ็ก/เดือน** คุ้มค่าคงที่ของ OA · ถ้าไม่ขายแพ็กเลย ต้นทุน LINE ของโควตาที่รวมในแผนอยู่ที่ 3–11% ของราคาแผน (§3.1) ซึ่งรับได้

---

## 4. วัดยอดอีเมลปัจจุบันเพื่อตั้งเพดาน (Q4: "สูงพอไม่ให้ลูกค้าปัจจุบันสะดุด")

ข้อเท็จจริงของตาราง (จากโค้ด): `"DocumentEmailLogs"` (`Models/Entities/DocumentEmailLog.cs`) · `"Status"` เป็น integer (`EmailLogStatus.Sent = 1`) · `"SenderMode"` = `'platform'`/`'company'` เพิ่มในรอบ 240
(`Helpers/SignRequestMigration.cs:29`) ⇒ **แถวก่อนรอบ 240 เป็น NULL** (ไม่รู้ว่าส่งทางใด) · `"IsDeleted"` (query filter) · ผู้รับอยู่ใน `"ToEmail"`/`"CcEmail"`/`"BccEmail"` คั่นด้วย `,` หรือ `;` ·
แผนอยู่ที่ `"Subscriptions"."Plan"` (0 FreeTrial · 1 Basic · 2 Pro · 3 Enterprise) · อีเมลจากเส้น CMS (`"CustomerNotifyLogs"` · `"Source" = 'system-smtp'`) นับแยกเพราะอยู่อีกตาราง

```sql
-- Q4-A: ยอดอีเมลผ่าน SMTP กลางต่อบริษัทต่อเดือนไทย (12 เดือนล่าสุด) · หน่วย = ผู้รับ (ตรงกับ NotifyQuotaPolicy) · ไม่นับ CC ของ e-Tax (csemail@etax.teda.th)
WITH logs AS (
    SELECT l."CompanyId",
           to_char((COALESCE(l."SentAt", l."CreatedAt") AT TIME ZONE 'Asia/Bangkok'), 'YYYY-MM') AS period,
           COALESCE(l."SenderMode", 'unknown') AS sender_mode,
           l."IsEtaxByEmail",
           (SELECT count(*) FROM regexp_split_to_table(
                concat_ws(',', l."ToEmail", l."CcEmail", l."BccEmail"), '\s*[,;]\s*') AS r(addr)
             WHERE btrim(r.addr) <> '' AND lower(btrim(r.addr)) <> 'csemail@etax.teda.th') AS recipients
    FROM "DocumentEmailLogs" l
    WHERE l."IsDeleted" = false
      AND l."Status" = 1                                    -- EmailLogStatus.Sent
      AND COALESCE(l."SentAt", l."CreatedAt") >= now() - interval '12 months'
),
cms AS (                                                    -- อีเมลเส้นแจ้งลูกค้า CMS ผ่าน SMTP กลาง (นับโควตาอยู่แล้วตั้งแต่รอบ 212)
    SELECT n."CompanyId",
           to_char((COALESCE(n."SentAt", n."CreatedAt") AT TIME ZONE 'Asia/Bangkok'), 'YYYY-MM') AS period,
           count(*) AS cms_emails
    FROM "CustomerNotifyLogs" n
    WHERE n."IsDeleted" = false AND n."Status" = 'Sent' AND n."Channel" = 'email' AND n."Source" = 'system-smtp'
      AND COALESCE(n."SentAt", n."CreatedAt") >= now() - interval '12 months'
    GROUP BY 1, 2
),
monthly AS (
    SELECT g."CompanyId", g.period,
           sum(g.recipients) FILTER (WHERE g.sender_mode = 'platform')  AS platform_recipients,
           sum(g.recipients) FILTER (WHERE g.sender_mode = 'unknown')   AS unknown_recipients,   -- ก่อนรอบ 240
           sum(g.recipients) FILTER (WHERE g.sender_mode = 'company')   AS company_recipients,   -- ไม่นับโควตา (ใช้ดูสัดส่วน)
           count(*)          FILTER (WHERE g.sender_mode = 'platform')  AS platform_messages     -- หน่วยของเพดานรายวัน
    FROM logs g GROUP BY 1, 2
),
plan AS (                                                   -- แผนล่าสุดของแต่ละบริษัท
    SELECT DISTINCT ON (s."CompanyId") s."CompanyId", s."Plan", s."Status", s."MaxSharedMessagesPerMonth"
    FROM "Subscriptions" s WHERE s."IsDeleted" = false
    ORDER BY s."CompanyId", s."CreatedAt" DESC
)
SELECT m."CompanyId", c."Name", p."Plan", m.period,
       COALESCE(m.platform_recipients, 0) + COALESCE(cm.cms_emails, 0) AS metered_now,      -- ค่าที่จะถูกตัดเมื่อเปิด L4
       COALESCE(m.unknown_recipients, 0)  AS legacy_unknown,
       COALESCE(m.company_recipients, 0)  AS own_smtp,
       COALESCE(m.platform_messages, 0)   AS platform_messages
FROM monthly m
LEFT JOIN cms cm ON cm."CompanyId" = m."CompanyId" AND cm.period = m.period
LEFT JOIN plan p ON p."CompanyId" = m."CompanyId"
JOIN "Companies" c ON c."Id" = m."CompanyId"
ORDER BY metered_now DESC;
```

```sql
-- Q4-B: สรุปต่อแผน — P50/P90/P95/P99/MAX ของเดือนที่หนักที่สุดของแต่ละบริษัท (ใช้ "เดือนสูงสุดต่อบริษัท" เพื่อไม่ให้ลูกค้าเดิมสะดุดในเดือนพีค)
-- ใส่ CTE logs/cms/monthly/plan จาก Q4-A ไว้ก่อนส่วนนี้
, per_company AS (
    SELECT m."CompanyId",
           max(COALESCE(m.platform_recipients,0) + COALESCE(m.unknown_recipients,0) + COALESCE(cm.cms_emails,0)) AS peak_month  -- รวม unknown = ประมาณแบบระวัง (สูงไว้ก่อน)
    FROM monthly m LEFT JOIN cms cm ON cm."CompanyId" = m."CompanyId" AND cm.period = m.period
    GROUP BY 1
)
SELECT p."Plan",
       count(*)                                                       AS companies,
       percentile_disc(0.50) WITHIN GROUP (ORDER BY pc.peak_month)     AS p50,
       percentile_disc(0.90) WITHIN GROUP (ORDER BY pc.peak_month)     AS p90,
       percentile_disc(0.95) WITHIN GROUP (ORDER BY pc.peak_month)     AS p95,
       percentile_disc(0.99) WITHIN GROUP (ORDER BY pc.peak_month)     AS p99,
       max(pc.peak_month)                                              AS max_peak,
       ceil(percentile_disc(0.95) WITHIN GROUP (ORDER BY pc.peak_month) * 1.5) AS suggested_cap_raw
FROM per_company pc LEFT JOIN plan p ON p."CompanyId" = pc."CompanyId"
GROUP BY p."Plan" ORDER BY p."Plan";
```

```sql
-- Q4-C: บริษัทที่ยอดเดือนสูงสุดเกินเพดานที่จะตั้ง ⇒ ทับรายบริษัทก่อนเปิดตัด (แทนค่า :cap_* ด้วยเพดานที่เจ้าของเลือก)
-- ใส่ CTE ทั้งหมดจาก Q4-B ไว้ก่อนส่วนนี้
SELECT pc."CompanyId", c."Name", p."Plan", pc.peak_month,
       ceil(pc.peak_month * 1.2) AS override_suggestion
FROM per_company pc JOIN plan p ON p."CompanyId" = pc."CompanyId" JOIN "Companies" c ON c."Id" = pc."CompanyId"
WHERE pc.peak_month > CASE p."Plan" WHEN 0 THEN :cap_free WHEN 1 THEN :cap_starter WHEN 2 THEN :cap_pro ELSE 2147483647 END
ORDER BY pc.peak_month DESC;
```

**ข้อควรระวังในการอ่านผล**
- ทุก query มี `"CompanyId"` เป็นแกนและอ่านอย่างเดียว · รันบนสำเนาอ่าน (read replica) หรือนอกเวลางาน (นับผู้รับด้วย `regexp_split_to_table` ทั้ง 12 เดือน)
- `legacy_unknown` (ก่อนรอบ 240) รวมเข้า P95 แบบระวัง ⇒ เพดานอาจสูงกว่าจำเป็นเล็กน้อย ซึ่งตรงกับเจตนา "ไม่ให้ลูกค้าเดิมสะดุด"
- ตาม design §5.1 L4 จะเริ่มนับอีเมลเชิญผูก LINE และอีเมลตามตาราง ⇒ เพดานควรเผื่อ 1.5 เท่า (สูตร §3.1) ไว้สำหรับปริมาณที่เพิ่มขึ้นหลังเปิด LINE ด้วย (ผู้รับบางรายย้ายจากอีเมลไป LINE ⇒ ปริมาณอีเมลน่าจะลด)
- ชื่อคอลัมน์ `"CustomerNotifyLogs"."CreatedAt"`/`"IsDeleted"` มาจาก `TenantEntity`/`BaseEntity` · ตรวจด้วย `\d "CustomerNotifyLogs"` ก่อนรัน

---

## 5. ค่าที่แนะนำให้ตั้ง (รายการทำ)

1. **OA กลาง @nextacc**: อยู่บนแผน **Basic** จนกว่ายอด push ทั้งแพลตฟอร์มเกิน ~20,000/เดือน แล้วย้ายเป็น **Pro** · ตั้ง "เพดานข้อความส่วนเกิน" ใน OA Manager = ผลรวมโควตาที่ขายไป × 1.2 · ตรวจราคาจริงในหน้า Billing ของ OA Manager (ยืนยัน §1.1)
2. `PlanTemplate.MaxSharedLineMessagesPerMonth`: Free 100 · Starter 300 · Professional 1,000 · Enterprise 5,000 · `TrialMaxSharedLineMessagesPerMonth` = 100
3. `PlanTemplate.MaxSharedEmailsPerMonth`: รัน SQL §4 ก่อน แล้วใช้ `max(ค่าตั้งต้น, ปัดขึ้น(P95 × 1.5))` · ค่าตั้งต้น Free 300 · Starter 1,500 · Professional 6,000 · Enterprise -1 · ทดลองใช้ 300 · บริษัทที่เกิน ⇒ ทับรายบริษัท `ceil(peak × 1.2)` พร้อมเหตุผล "คงสิทธิ์ลูกค้าเดิม L4"
4. แพ็ก `notify.topup.line` = 1,000 ข้อความ ฿290 รวม VAT · อายุ 60 วัน · (ทางเลือก `notify.topup.line.xl` 5,000 ฿990)
5. แพ็ก `notify.topup.email` = 5,000 ฉบับ ฿190 รวม VAT · อายุ 60 วัน · **ยืนยันผู้ให้บริการอีเมลก่อน** ถ้าต้นทุนเกิน ฿0.02/ฉบับ ⇒ 2,000 ฉบับ ฿190
6. ปิดการขาย `notify.topup` (ก้อนรวม ฿99/500) เมื่อสองแพ็กข้างบนเปิด · โบนัสเดิมให้ใช้จนหมดอายุ (ต้องตัดสินว่าโบนัสก้อนรวมเดิมหักจากช่องใด ⇒ เสนอ: ใช้ได้ทั้งสองช่อง จนหมด)
7. คงเพดานรายวัน `SignRequestEmail:PlatformDailyCapPerCompany` = 200 (ยังไม่ยืนยันตัวตน 20) · ถ้าจะให้ Enterprise ส่งเกิน ~6,000/เดือน ต้องขยับค่านี้ด้วย
8. ใส่ `X-Line-Retry-Key` ทุก push ก่อนเปิดการตัดโควตาตามสมาชิกกลุ่ม (กันจ่ายซ้ำ)
9. เพิ่มตัวนับ reply ของ OA กลางใน `NextAccOaQuotaWatch` และตรวจประกาศ "reply ฟรี 25,000/เดือน จาก 1 ก.พ. 2570" กับ LINE โดยตรง
10. ทดสอบหน่วยนับของกลุ่มจริงก่อน L3: กลุ่ม 3 คน + บอท → push 1 ครั้ง → ดู `/v2/bot/message/quota/consumption` เพิ่ม 3 หรือ 4 ⇒ เลือก `memberCount` หรือ `memberCount + 1` (แทน `max(1, memberCount − 1)` ใน design §5.1 ซึ่งน่าจะนับต่ำไป)

## 6. แหล่งอ้างอิง

| แหล่ง | ใช้ยืนยัน | ระดับ |
| --- | --- | --- |
| `github.com/line/line-developers-docs-source` (commit 2026-11-02) `docs/en/docs/messaging-api/pricing` · `group-chats` · `retrying-api-request` · `sending-messages` | กติกาการนับ · reply ไม่นับ · กลุ่มมี OA เดียว · retry key | ทางการ (ต้นฉบับของ developers.line.biz) |
| https://lineforbusiness.com/th/service/line-oa-features/broadcast-message | หน้าราคาทางการของไทย | **เปิดไม่ได้** (DNS ถูกบล็อก) |
| https://suriya.ai/blog/line-oa-what-is-pricing-thailand · https://www.cipher.co.th/en/blogs/line-oa-pricing-guide/ · https://www.sellsuki.co.th/blogs/detail/261/summary-price-line-oa · https://thunder.in.th/news/line-messaging-api-pricing/ · https://www.amjaka.co.th/articles/line-oa · https://linebot.pro/blog/line-official-account-pricing-guide · https://goonlinethailand.com/blog/social-media/line/goonline-update-lineoa/ · https://shantilink.com/blog/what-is-line-oa | ราคาแผนไทย Free/Basic/Pro | รอง (ตรงกัน) |
| https://sphereagency.com/th/articles/line-oa-setup-guide-thailand | Light/Standard (ขัดกับแหล่งอื่น) | รอง · ไม่ใช้ |
| https://www.relevantaudience.com/social-media-marketing/line-reply-api-25000-free-messages-agency-global-platform/ | reply 25,000 ฟรีจาก 1 ก.พ. 2570 | รอง แหล่งเดียว |
| https://blog.campaignhq.co/amazon-ses-pricing/ · https://wraps.dev/tools/ses-calculator · https://smtpedia.com/amazon-aws-ses-pricing/ | ราคา SES | รอง |
| https://www.twilio.com/en-us/products/email-api/pricing · https://costbench.com/software/email-api/sendgrid/ | SendGrid | รอง/ทางการบางส่วน |
| https://automationatlas.io/answers/mailgun-pricing-explained-2026/ · https://costbench.com/software/email-api/mailgun/ | Mailgun | รอง |
| https://automationatlas.io/answers/brevo-pricing-explained-2026/ | Brevo | รอง |
| https://knowledge.workspace.google.com/admin/gmail/advanced/route-outgoing-smtp-relay-messages-through-google · https://www.smtp2go.com/blog/email-sending-limits-by-provider-gmail-microsoft-365-yahoo-and-the-rest/ | เพดาน Google/Microsoft | ทางการ/รอง |
| repo: `Helpers/NotifyQuotaPolicy.cs` · `Helpers/SignEmailThrottle.cs` · `Helpers/PlatformEmailQuota.cs` · `Helpers/QuotaAddOnPricing.cs` · `Helpers/PlatformWhtSuggestion.cs` · `Data/SeedPlanTemplates.cs` · `Data/DatabaseMigrationHelper.cs` (seed `notify.topup` ฿99/500) · `Models/Constants/AddOnCodes.cs` | โครงสร้างโควตาและราคาเดิม | โค้ด (ground truth) |
