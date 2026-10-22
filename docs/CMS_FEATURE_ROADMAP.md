# CMS (เว็บไซต์ของฉัน) — ฟีเจอร์ที่มีแล้ว / backlog

> สถานะ ณ 2026-10-22 (ทีม C) · โค้ดเป็น ground truth — ถ้าตารางนี้ไม่ตรงโค้ด แก้ตารางนี้ทันที
> ไฟล์หลัก: `wwwroot/pages/cms-sites.html` (รายการเว็บ) · `cms-edit.html` (ตัวแก้ไข) · `wwwroot/storefront.html` (หน้าเว็บสาธารณะ) ·
> `Services/Implementations/CmsRenderingService.cs` · `CmsContentService.cs` · `CmsSiteService.cs` · ตัวตัดสินโมดูล `Helpers/CmsModuleResolver.cs`

## 1. โครงสร้างการเข้าถึง (รอบนี้)

| เรื่อง | สถานะ |
| --- | --- |
| เมนูซ้าย "ขายออนไลน์ & Portal" เหลือ **เว็บไซต์ของฉัน** + Portal ลูกค้า + Vendor Portal — คำสั่งซื้อ/จองคิว/Front desk/ตั้งค่าที่พัก/Lead พับเป็นเมนูย่อย (`navParent: 'cms-sites'` ใน `js/layout.js`) โผล่เยื้องเฉพาะตอนอยู่ในพื้นที่ CMS · ช่องค้นหาเมนูยังหาเจอ · ผู้ใช้ที่ไม่เห็นเมนูแม่ (role ที่ให้แค่ Front desk) เห็นเมนูลูกตามเดิม | ✅ |
| ปุ่มงานรายเว็บบนการ์ด (ตาม `site.modules` จาก `CmsModuleResolver`): ร้านค้า → คำสั่งซื้อ · จองคิว → การจองคิว · ที่พัก → Front desk / ตั้งค่าที่พัก · ทุกเว็บ → Lead · มีตัวเลขงานค้างบนปุ่ม | ✅ |
| `js/cms-site-context.js` — ตัวเดียวของ: ตรวจ `?siteId=` กับรายการเว็บจริง · dropdown · breadcrumb "เว็บไซต์ของฉัน › เว็บ › งาน" · จด `?siteId=` กลับลง URL · ตารางลิงก์โมดูล | ✅ |
| ภาพรวม **ทุกไซต์**: แถบบน cms-sites + `?siteId=all` ใน คำสั่งซื้อ / Lead (รวมผลจาก endpoint รายเว็บเดิม — สิทธิ์ CmsRbac รายเว็บยังบังคับ · เว็บที่โหลดไม่ได้แจ้งชื่อบนจอ) | ✅ |
| "ทุกไซต์" ในหน้าการจองคิว | backlog — หน้านี้ทีม B กำลังแก้ส่วนระบบจอง (รอบนี้ใส่แค่ breadcrumb + ตรวจ siteId) |
| endpoint รวมข้ามเว็บฝั่งเซิร์ฟเวอร์ (แบ่งหน้า/ค้นหาย้อนหลังข้ามเว็บ) | backlog — ต้องกรอง "เว็บที่ผู้ใช้มี SiteStaffAccess" เอง (middleware ตรวจสิทธิ์ต่อ `{siteId}` ใน route เท่านั้น) |

## 2. บล็อกเนื้อหา (`CmsBlockType`)

| บล็อก | ตัวแก้ไข | เรนเดอร์ | หมายเหตุ |
| --- | --- | --- | --- |
| Hero · RichText · Image · CTA · Divider · HTML (sanitizer allowlist) | ✅ | storefront วาดเอง | server renderer อ่านคีย์ไม่ตรงตัวแก้ไข (Hero `title` vs `headline`) — ไม่มีผลเพราะ storefront ไม่ใช้ HTML ของ server สำหรับบล็อกพวกนี้ · ควรรวมเป็นตัวเรนเดอร์เดียวแบบแกลเลอรี (backlog) |
| **Gallery / Slider** — ตาราง + lightbox หรือสไลด์โชว์ (autoplay · interval 2–30 วิ · ปุ่ม/จุด/ปัดนิ้ว/คีย์บอร์ด · หยุดเมื่อชี้/โฟกัส/prefers-reduced-motion) · หลายรูป · เรียง ↑↓ · alt · caption · คอลัมน์ · สัดส่วนรูป · responsive | ✅ อัปโหลดหลายไฟล์ + เลือกจากคลัง + URL | **server ตัวเดียว** `Helpers/CmsGalleryBlock` (escape ทุกช่อง · กรอง URL) · storefront แค่ติดพฤติกรรม | รอบนี้ · เทสต์ `CmsGalleryBlockTests` |
| หน้าแกลเลอรีใหม่ ("เริ่มจาก: หน้าแกลเลอรี/สไลด์โชว์" ตอนเพิ่มหน้า) | ✅ | — | สร้างหน้า + บล็อก Gallery แล้วเปิดฟอร์มอัปโหลดทันที |
| Video (YouTube / MP4) | ✅ | storefront (YouTube id → embed) | backlog: Vimeo/Facebook/TikTok embed · server renderer ใส่ URL ลง iframe ตรง ๆ (ไม่ได้ใช้บน storefront แต่ควรกรอง scheme) |
| Map (ที่อยู่/พิกัด → Google Maps embed) | ✅ | ✅ | backlog: ปักหมุดจากแผนที่ · หลายสาขา |
| Testimonials · FAQ | ✅ (บรรทัดละรายการ) | ✅ | backlog: FAQ → JSON-LD `FAQPage` (SEO) · รีวิวดึงจาก `CmsReview` จริง + ดาว |
| PricingTable | ✅ (JSON) | ✅ | backlog: ฟอร์มแทน JSON |
| ContactForm / RFQ / lead types | ✅ | ✅ → `CmsLead` | |
| ProductGrid · Cart · BookingCalendar · LodgingRooms | ✅ | ✅ (ข้อมูลสด) | |
| Newsletter | ✅ | ✅ | backlog: double opt-in + consent record (PDPA §J) |
| SocialFeed · Countdown · BlogList · CategoryList · SearchResults · Nav/Footer block | enum มี | placeholder | backlog |

## 3. หน้า · SEO · ภาษา · โดเมน

| เรื่อง | สถานะ |
| --- | --- |
| หน้า (Standard/Landing/Blog/Product/Checkout) · slug · ลำดับ · เผยแพร่/ตั้งเวลา | ✅ |
| Meta title/description/keywords · OG image · canonical · noindex/nofollow · JSON-LD ต่อหน้า · sitemap.xml · 301/302 redirects | ✅ |
| **OG/meta ถูกตั้งด้วย JS ฝั่ง client** (storefront เป็น SPA) ⇒ crawler ของ LINE/Facebook/X ที่ไม่รัน JS ไม่เห็นภาพ/คำอธิบาย | backlog สำคัญ — ให้ `CmsSiteRoutingMiddleware` ฉีด `<title>`/OG ฝั่งเซิร์ฟเวอร์ก่อนส่ง storefront.html |
| หลายภาษา (`SiteLocale` · `SitePageTranslation` · `PageBlockTranslation`) | ✅ โครงสร้าง + แท็บภาษา · backlog: ตัวแก้ไขเนื้อหาบล็อกรายภาษาในฟอร์มบล็อก · hreflang |
| โดเมนเอง + SSL | ✅ |
| ธีม (สี/ฟอนต์/โลโก้) · cookie consent · GA/GTM/Pixel · CAPTCHA | ✅ |

## 4. ข้อมูล/สื่อ

| เรื่อง | สถานะ |
| --- | --- |
| คลังสื่อ — ตรวจชนิดจากไบต์ (`IImageProcessingService.ProcessAndSaveAsync`) · บีบรูป · โควตาพื้นที่ License · `PublicUrl` ใน `MediaResponse` (รอบนี้ — เดิมหน้าแก้ไขตกไปใช้ path บนดิสก์) | ✅ |
| แก้ alt/caption ในคลังสื่อ (endpoint `PUT media/{id}` มีแล้ว ไม่มีจอ) | backlog |
| ย้ายไฟล์ไป object storage (S3/R2) — ตอนนี้ทุกเส้นอัปโหลดของระบบเขียนโฟลเดอร์ `uploads/` ผ่าน `ProcessAndSaveAsync` | backlog (กฎเหล็ก #4 D multi-instance) |
| สร้าง srcset/WebP หลายขนาดสำหรับแกลเลอรี | backlog (`SiteMedia.WebPPath/ThumbnailPath` มีช่องแล้ว ยังไม่ถูกเติม) |
| Form builder (แท็บฟอร์ม · submissions) | ✅ พื้นฐาน · backlog: ช่องไฟล์แนบ · เงื่อนไขแสดงช่อง · แจ้งเตือน LINE |

## 5. ความปลอดภัย (แก้รอบนี้)

- บล็อก: สร้าง/แก้/ลบ/เรียง ตรวจ "หน้าเป็นของเว็บ+บริษัทนี้" ก่อน (`CmsContentService.EnsurePageOwnedAsync`) — เดิมใช้ pageId/blockId ล้วน ไม่มีตัวกรองบริษัท
  และ `PageBlock` ไม่มี global tenant filter ⇒ แทรกบล็อกลงหน้าเว็บบริษัทอื่นได้
- บล็อก + สื่อ ต้องมีบทบาทเว็บ Admin/Editor/ContentWriter (`RequireSiteRole` — เท่ากับการสร้างหน้า) · เจ้าของบริษัทผ่านเสมอ
- อัปโหลดสื่อตรวจว่าเว็บเป็นของบริษัทก่อนเขียนไฟล์
- `PageBlock.CssClasses` กรองเหลือโทเคน `[A-Za-z0-9_-]` (`Helpers/CmsBlockCss`) ฝั่งเซิร์ฟเวอร์ และ escape ฝั่ง storefront — เดิมต่อดิบเข้า `class="…"`

## 6. backlog อื่นที่ควรมี (เรียงตามคุณค่า)

1. SSR meta/OG (ข้อ 3) — แชร์ลิงก์ร้านใน LINE แล้วไม่มีรูป/ชื่อ = เสียยอดขายตรง ๆ
2. ตัวเรนเดอร์บล็อกตัวเดียว (ย้าย Hero/Image/FAQ/Testimonials ไปแบบ `CmsGalleryBlock`) — ปิด defect class "สองตัวเรนเดอร์ drift"
3. FAQ/Product/LocalBusiness JSON-LD อัตโนมัติจากบล็อก/ข้อมูลบริษัท
4. Blog (โพสต์ · หมวด · RSS) — enum/PageType มีแล้ว ไม่มีตัวแก้ไขรายการโพสต์
5. Popup/แบนเนอร์ประกาศ · ปุ่มแชท LINE/Messenger ลอย
6. ปฏิทินกิจกรรม/อีเวนต์ · Before/After slider · Team/Staff block
7. A/B test หัวข้อ Hero · heatmap — ต่ำ
8. ประวัติเวอร์ชันหน้า (`SitePage.Version` มีช่อง) + ย้อนกลับ
