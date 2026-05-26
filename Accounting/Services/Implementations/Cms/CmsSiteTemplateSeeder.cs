using System.Text.Json;
using Accounting.Models.Entities;
using Accounting.Models.Enums;

namespace Accounting.Services.Implementations.Cms;

/// <summary>
/// Pre-built site templates per IndustryType so a new tenant starts with
/// a complete, ready-to-use website instead of a blank canvas. Customers
/// only need to swap copy / images / contact info — the structure is done.
///
/// Each template emits:
///   • 5–6 SitePage rows (Home, About, Services/Menu/Products, Contact, etc.)
///   • 7–9 blocks on the home page (Hero → why-us → offerings → social proof
///     → process → pricing/faq → CTA → newsletter) modeled on best-in-class
///     sites for that vertical.
///
/// Adding a new industry: implement BuildPlan() for that IndustryType,
/// returning a list of (Page, [Block]) tuples. The seeder fills the FK
/// graph (CompanyId, SiteId, PageId, SortOrder) for the caller.
/// </summary>
public static class CmsSiteTemplateSeeder
{
    /// <summary>Generate pages + blocks for a freshly-created site.
    /// Caller is responsible for adding them to the DbContext and
    /// SaveChanges. Returns the SitePages with their Blocks already
    /// attached (EF navigates the FK on insert).</summary>
    public static List<SitePage> BuildSeed(Guid companyId, Guid siteId, IndustryType industry, string userId)
    {
        var plan = industry switch
        {
            IndustryType.Restaurant => RestaurantPlan(),
            IndustryType.Cafe => CafePlan(),
            IndustryType.Retail => RetailPlan(),
            IndustryType.Ecommerce => EcommercePlan(),
            IndustryType.Trading => TradingPlan(),
            IndustryType.Agriculture => AgriculturePlan(),
            IndustryType.Beauty => BeautyPlan(),
            IndustryType.Healthcare => HealthcarePlan(),
            IndustryType.Service => ServicePlan(),
            IndustryType.Freelance => FreelancePlan(),
            IndustryType.Construction => ConstructionPlan(),
            IndustryType.RealEstate => RealEstatePlan(),
            IndustryType.Technology => TechnologyPlan(),
            IndustryType.Education => EducationPlan(),
            IndustryType.Transportation => TransportationPlan(),
            IndustryType.Hotel => HotelPlan(),
            IndustryType.Manufacturing => ManufacturingPlan(),
            _ => GeneralPlan()
        };

        var pages = new List<SitePage>();
        var pageOrder = 0;
        foreach (var (pageMeta, blockMetas) in plan)
        {
            var page = new SitePage
            {
                CompanyId = companyId,
                SiteId = siteId,
                Title = pageMeta.Title,
                Slug = pageMeta.Slug,
                Status = PageStatus.Published,
                PageType = pageMeta.PageType,
                SortOrder = pageOrder++,
                MetaDescription = pageMeta.MetaDescription,
                PublishedAt = DateTime.UtcNow,
                CreatedBy = userId
            };
            var blockOrder = 0;
            foreach (var bm in blockMetas)
            {
                page.Blocks.Add(new PageBlock
                {
                    CompanyId = companyId,
                    BlockType = bm.Type,
                    SortOrder = blockOrder++,
                    ConfigJson = bm.ConfigJson,
                    CreatedBy = userId
                });
            }
            pages.Add(page);
        }
        return pages;
    }

    // ===== Plan helpers (industry templates) =====

    private record PageMeta(string Title, string Slug, PageType PageType, string? MetaDescription);
    private record BlockMeta(CmsBlockType Type, string ConfigJson);
    private record Plan(PageMeta Page, List<BlockMeta> Blocks);

    private static string J(object o) => JsonSerializer.Serialize(o);

    // ============================================================
    // GENERAL / DEFAULT — professional services landing
    // ============================================================
    private static List<(PageMeta, List<BlockMeta>)> GeneralPlan() => new()
    {
        (new("หน้าหลัก", "home", PageType.Landing, "เว็บไซต์อย่างเป็นทางการ — บริการครบวงจร"), new() {
            new(CmsBlockType.Hero, J(new {
                headline = "ยินดีต้อนรับสู่ธุรกิจของเรา",
                subheadline = "เราพร้อมให้บริการที่ดีที่สุดแก่คุณ — มืออาชีพ ใส่ใจรายละเอียด ราคาเป็นมิตร",
                ctaText = "ดูบริการของเรา",
                ctaUrl = "/services"
            })),
            new(CmsBlockType.RichText, J(new {
                content = @"<h2>ทำไมต้องเลือกเรา</h2>
<ul>
  <li>✅ <strong>ทีมงานมืออาชีพ</strong> — ประสบการณ์มากกว่า 10 ปีในวงการ</li>
  <li>⚡ <strong>ตอบกลับเร็ว</strong> — ภายใน 1 ชั่วโมงในเวลาทำการ</li>
  <li>💰 <strong>ราคาโปร่งใส</strong> — แจ้งราคาก่อนเริ่มงาน ไม่มีค่าใช้จ่ายแฝง</li>
  <li>🏆 <strong>รับประกันงาน</strong> — ไม่พอใจคืนเงิน 100% ภายใน 7 วัน</li>
  <li>🤝 <strong>ดูแลหลังการขาย</strong> — มีปัญหาเมื่อไหร่ ทีมเราพร้อมช่วยทันที</li>
  <li>📍 <strong>ครอบคลุมทั่วประเทศ</strong> — ออนไซต์ ออนไลน์ เลือกได้ตามสะดวก</li>
</ul>"
            })),
            new(CmsBlockType.PricingTable, J(new {
                headline = "แพ็กเกจที่แนะนำ",
                plans = new[] {
                    new { name = "Starter", price = "฿1,500/เดือน",
                          features = new[] { "บริการพื้นฐาน", "ตอบกลับใน 24 ชม.", "อีเมลสนับสนุน", "รายงานรายเดือน" } },
                    new { name = "Business", price = "฿3,500/เดือน",
                          features = new[] { "บริการครบครัน", "ตอบกลับใน 4 ชม.", "โทรศัพท์ + LINE + อีเมล", "รายงานรายสัปดาห์", "ที่ปรึกษาออนไลน์" } },
                    new { name = "Premium", price = "฿6,000/เดือน",
                          features = new[] { "บริการเฉพาะลูกค้า VIP", "ตอบกลับทันที 24/7", "Account Manager เฉพาะตัว", "รายงานเรียลไทม์", "Onsite ฟรี" } }
                }
            })),
            new(CmsBlockType.Testimonials, J(new {
                headline = "ลูกค้าพูดถึงเรา",
                testimonials = new[] {
                    new { quote = "บริการดีมาก ทีมงานเอาใจใส่ทุกขั้นตอน คุ้มราคาที่จ่าย", author = "คุณสมชาย ก.", role = "ลูกค้าประจำ 3 ปี" },
                    new { quote = "ราคาเป็นธรรม คุณภาพเกินคาด แนะนำต่อให้เพื่อนเลย", author = "คุณวรรณา ส.", role = "เจ้าของกิจการ" },
                    new { quote = "ตอบเร็ว ทำงานเป็นระบบ มีรายงานชัดเจน", author = "คุณภัทรา พ.", role = "ผู้จัดการฝ่ายจัดซื้อ" },
                    new { quote = "ปรึกษาฟรีก่อน ไม่กดดัน ตัดสินใจง่าย", author = "คุณธีระ ว.", role = "SME เจ้าใหม่" }
                }
            })),
            new(CmsBlockType.RichText, J(new {
                content = @"<h2>ขั้นตอนการทำงาน</h2>
<ol>
  <li><strong>1. ปรึกษาฟรี</strong> — โทร / LINE / กรอกฟอร์ม เราตอบกลับใน 1 ชม.</li>
  <li><strong>2. ประเมินงาน + ใบเสนอราคา</strong> — ไม่มีค่าใช้จ่าย ไม่มัดมือชก</li>
  <li><strong>3. เริ่มงาน</strong> — หลังตกลง ทีมงานเริ่มได้ทันที</li>
  <li><strong>4. รายงานความคืบหน้า</strong> — แจ้งทุกขั้นตอน โปร่งใส 100%</li>
  <li><strong>5. ส่งมอบ + รับประกัน</strong> — ดูแลหลังส่งมอบตลอดอายุสัญญา</li>
</ol>"
            })),
            new(CmsBlockType.Faq, J(new {
                headline = "คำถามที่พบบ่อย",
                items = new[] {
                    new { q = "บริการของท่านมีรับประกันไหม?", a = "เรามีรับประกันคุณภาพงานทุกชิ้น ถ้าไม่พอใจคืนเงิน 100% ภายใน 7 วัน" },
                    new { q = "ใช้เวลานานเท่าไหร่ในการเริ่มงาน?", a = "หลังจากชำระเงินมัดจำ เราเริ่มภายใน 1-2 วันทำการ" },
                    new { q = "ชำระเงินอย่างไร?", a = "โอนผ่านธนาคาร, พร้อมเพย์, หรือบัตรเครดิต (ผ่อนได้สูงสุด 10 เดือน)" },
                    new { q = "มีใบกำกับภาษีไหม?", a = "ออกใบกำกับภาษีเต็มรูปแบบทุกออเดอร์" },
                    new { q = "ทำงานนอกเวลาได้ไหม?", a = "ลูกค้า Premium มีบริการ 24/7 ลูกค้าทั่วไปจันทร์-เสาร์ 9:00-18:00" }
                }
            })),
            new(CmsBlockType.CallToAction, J(new {
                headline = "พร้อมเริ่มต้นกับเราแล้วหรือยัง?",
                subheadline = "ปรึกษาฟรี ไม่มีค่าใช้จ่าย ไม่มีข้อผูกมัด",
                ctaText = "ติดต่อเรา",
                ctaUrl = "/contact"
            }))
        }),

        (new("เกี่ยวกับเรา", "about", PageType.Standard, "เรื่องราวของเรา ทีมงาน และวิสัยทัศน์"), new() {
            new(CmsBlockType.Hero, J(new {
                headline = "เรื่องราวของเรา",
                subheadline = "จุดเริ่มต้นเล็ก ๆ ที่เติบโตเป็นแบรนด์ที่ลูกค้าไว้วางใจ"
            })),
            new(CmsBlockType.RichText, J(new {
                content = @"<h2>วิสัยทัศน์</h2>
<p>ส่งมอบบริการที่ดีที่สุด เพื่อความสำเร็จของลูกค้าทุกคน</p>
<h2>พันธกิจ</h2>
<p>เราเชื่อว่าทุกธุรกิจมีศักยภาพ ภารกิจของเราคือช่วยให้ธุรกิจของคุณเติบโตอย่างยั่งยืน
ผ่านบริการที่มีคุณภาพและการดูแลแบบครบวงจร</p>
<h2>ทีมงาน</h2>
<p>ทีมงานของเราประกอบด้วยมืออาชีพในหลากหลายสาขา พร้อมให้คำปรึกษาและให้บริการอย่างเต็มที่
รวม 25 คน · เฉลี่ยประสบการณ์ 8 ปี · ผ่านการอบรมและรับรองมาตรฐานสากล</p>
<h2>ค่านิยมของเรา</h2>
<ul>
  <li>🎯 <strong>ลูกค้าเป็นศูนย์กลาง</strong> — ทุกการตัดสินใจคำนึงถึงลูกค้าก่อน</li>
  <li>🔍 <strong>ความโปร่งใส</strong> — แจ้งทุกอย่างชัดเจน ไม่มีค่าซ่อน</li>
  <li>📈 <strong>พัฒนาไม่หยุด</strong> — เรียนรู้ + ปรับปรุงทุกวัน</li>
</ul>"
            })),
            new(CmsBlockType.CallToAction, J(new {
                headline = "อยากร่วมงานกับเรา?",
                subheadline = "ติดต่อเข้ามาคุยกันได้ตลอด",
                ctaText = "ติดต่อเรา", ctaUrl = "/contact"
            }))
        }),

        (new("บริการ", "services", PageType.Standard, "บริการของเรา"), new() {
            new(CmsBlockType.Hero, J(new {
                headline = "บริการของเรา",
                subheadline = "เลือกบริการที่ตรงใจคุณ — ปรับแต่งได้ตามต้องการ"
            })),
            new(CmsBlockType.RichText, J(new {
                content = @"<h2>หมวดบริการหลัก</h2>
<ul>
  <li>📋 <strong>ที่ปรึกษา</strong> — วิเคราะห์ปัญหา + วางแผนแก้ไข</li>
  <li>⚙️ <strong>ดำเนินการ</strong> — ลงมือทำให้ครบจบงาน</li>
  <li>🛠️ <strong>ดูแลต่อเนื่อง</strong> — บำรุงรักษา + ปรับปรุงประจำ</li>
  <li>🎓 <strong>อบรม / ถ่ายทอด</strong> — สอนทีมงานคุณให้ทำเองได้</li>
</ul>"
            })),
            new(CmsBlockType.PricingTable, J(new {
                headline = "แพ็กเกจราคา",
                plans = new[] {
                    new { name = "Starter", price = "฿1,500/เดือน",
                          features = new[] { "บริการพื้นฐาน", "ตอบกลับใน 24 ชม.", "อีเมลสนับสนุน" } },
                    new { name = "Business", price = "฿3,500/เดือน",
                          features = new[] { "บริการครบครัน", "ตอบกลับใน 4 ชม.", "โทรศัพท์ + LINE", "รายงานรายเดือน" } },
                    new { name = "Premium", price = "฿6,000/เดือน",
                          features = new[] { "บริการเฉพาะลูกค้า VIP", "ตอบกลับทันที", "Account Manager", "รายงานเรียลไทม์" } }
                }
            })),
            new(CmsBlockType.Faq, J(new {
                headline = "คำถามที่พบบ่อย",
                items = new[] {
                    new { q = "รับงานเร่งด่วนได้ไหม?", a = "รับ มีค่าธรรมเนียมเร่งด่วน 20% — โทรเข้าสายตรง 086-XXX-XXXX" },
                    new { q = "ทำงานนอกพื้นที่ได้ไหม?", a = "ได้ มีค่าเดินทางตามจริง + ค่าที่พัก (สำหรับงานต่างจังหวัด)" },
                    new { q = "เซ็น NDA ได้ไหม?", a = "ได้ เราเซ็น NDA ทุกโครงการที่ต้องการความลับ" },
                    new { q = "มีตัวอย่างผลงานไหม?", a = "มี ดูได้ในหน้าผลงาน หรือขอเฉพาะทางอีเมล" }
                }
            }))
        }),

        (new("ผลงาน", "portfolio", PageType.Standard, "ผลงานที่ผ่านมา"), new() {
            new(CmsBlockType.Hero, J(new {
                headline = "ผลงานที่ผ่านมา",
                subheadline = "200+ โครงการสำเร็จ · 95% ลูกค้ากลับมาใช้ซ้ำ"
            })),
            new(CmsBlockType.RichText, J(new {
                content = @"<h2>ตัวอย่างโครงการเด่น</h2>
<ul>
  <li><strong>โครงการ A</strong> — บริษัทค้าปลีก · ลดต้นทุน 30% ใน 6 เดือน</li>
  <li><strong>โครงการ B</strong> — ร้านอาหารเชน 12 สาขา · เพิ่มยอดขาย 45%</li>
  <li><strong>โครงการ C</strong> — โรงงานผลิต · ระบบ ERP ครบวงจร</li>
</ul>
<p style=""color:#94a3b8;font-size:13px"">* แก้ไขรายการผลงานจริงของคุณตรงนี้ ใส่ภาพ Before/After เพื่อความน่าเชื่อถือ</p>"
            }))
        }),

        (new("ติดต่อเรา", "contact", PageType.Standard, "ที่อยู่ เบอร์โทร และแบบฟอร์มติดต่อ"), new() {
            new(CmsBlockType.Hero, J(new {
                headline = "ติดต่อเรา",
                subheadline = "ยินดีให้คำปรึกษาฟรี — เปิดทำการ จันทร์-ศุกร์ 09:00-18:00"
            })),
            new(CmsBlockType.RichText, J(new {
                content = @"<h2>ช่องทางการติดต่อ</h2>
<p>📞 โทร: 02-XXX-XXXX<br>
📱 LINE: @yourshop<br>
📧 อีเมล: info@example.com<br>
📍 ที่อยู่: กรุณาแก้ไขที่อยู่ของคุณตรงนี้</p>"
            })),
            new(CmsBlockType.ContactForm, J(new { headline = "ส่งข้อความถึงเรา", submitText = "ส่งข้อความ", emailTo = "" })),
            new(CmsBlockType.Map, J(new { address = "กรุงเทพมหานคร ประเทศไทย" }))
        })
    };

    // ============================================================
    // RESTAURANT — OpenTable / Resy / After You inspired
    // ============================================================
    private static List<(PageMeta, List<BlockMeta>)> RestaurantPlan() => new()
    {
        (new("หน้าหลัก", "home", PageType.Landing, "ร้านอาหารบรรยากาศดี เมนูเด็ด ๆ จองโต๊ะได้ออนไลน์"), new() {
            new(CmsBlockType.Hero, J(new {
                headline = "อร่อยทุกคำ ทุกจาน",
                subheadline = "บรรยากาศดี วัตถุดิบสดทุกวัน ราคาเป็นกันเอง — เปิดทุกวัน 10:00-22:00",
                ctaText = "จองโต๊ะ",
                ctaUrl = "/booking"
            })),
            new(CmsBlockType.RichText, J(new {
                content = @"<h2>🍜 ทำไมต้องร้านเรา</h2>
<ul>
  <li>👨‍🍳 <strong>เชฟประสบการณ์ 15+ ปี</strong> — ปรุงด้วยสูตรเฉพาะของร้าน</li>
  <li>🌾 <strong>วัตถุดิบสดทุกวัน</strong> — รับตรงจากตลาดเช้า + เกษตรกรท้องถิ่น</li>
  <li>🪑 <strong>บรรยากาศอบอุ่น</strong> — เหมาะครอบครัว เพื่อน คู่รัก งานเลี้ยง</li>
  <li>🅿️ <strong>ที่จอดรถสะดวก</strong> — รองรับ 40 คัน · ฟรี</li>
  <li>📲 <strong>จองโต๊ะออนไลน์</strong> — ไม่ต้องรอคิว ยืนยันใน 30 นาที</li>
  <li>🚗 <strong>เดลิเวอรี่</strong> — สั่งผ่าน LINE / Grab / Foodpanda</li>
</ul>"
            })),
            new(CmsBlockType.RichText, J(new {
                content = @"<h2>🍲 เมนูแนะนำประจำร้าน</h2>
<ul>
  <li><strong>ผัดกะเพราหมูสับไข่ดาว</strong> — กลิ่นใบกะเพราไทยแท้ · 80 บาท</li>
  <li><strong>ผัดไทยกุ้งสด</strong> — กุ้งใหญ่ 4 ตัว · 120 บาท</li>
  <li><strong>ต้มยำกุ้งน้ำข้น</strong> — สูตรเฉพาะร้าน · 180 บาท</li>
  <li><strong>ข้าวมันไก่ทอด</strong> — ไก่ทอดสดใหม่ทุกจาน · 75 บาท</li>
  <li><strong>มะม่วงข้าวเหนียว</strong> — มะม่วงน้ำดอกไม้ · 80 บาท</li>
</ul>
<p><a href=""/menu"" class=""btn"">ดูเมนูทั้งหมด →</a></p>"
            })),
            new(CmsBlockType.Testimonials, J(new {
                headline = "เสียงจากลูกค้า",
                testimonials = new[] {
                    new { quote = "อร่อยที่สุดในย่านนี้! กลับมากินซ้ำทุกอาทิตย์", author = "คุณนิด ส.", role = "ลูกค้าประจำ" },
                    new { quote = "บรรยากาศร้านดีมาก เหมาะพาครอบครัวมา ทานสบาย", author = "คุณตุ้ม จ.", role = "Google Review" },
                    new { quote = "พนักงานบริการดี ทำให้รู้สึกอบอุ่นเหมือนกลับบ้าน", author = "คุณแก้ว ว.", role = "Wongnai 5 ดาว" },
                    new { quote = "ต้มยำกุ้งเด็ดมาก แนะนำต้องลอง!", author = "คุณเอก ภ.", role = "Food Blogger" }
                }
            })),
            new(CmsBlockType.RichText, J(new {
                content = @"<h2>📅 ขั้นตอนการจองโต๊ะ</h2>
<ol>
  <li><strong>1. กรอกฟอร์ม</strong> — เลือกวันเวลา + จำนวนคน</li>
  <li><strong>2. รอยืนยัน</strong> — เจ้าหน้าที่ติดต่อกลับใน 30 นาที</li>
  <li><strong>3. ยืนยันการจอง</strong> — ส่ง SMS/LINE ยืนยันเลขจอง</li>
  <li><strong>4. มาถึงร้าน</strong> — แจ้งชื่อ พนักงานพาเข้าโต๊ะ</li>
</ol>"
            })),
            new(CmsBlockType.Faq, J(new {
                headline = "คำถามที่พบบ่อย",
                items = new[] {
                    new { q = "ต้องจองโต๊ะล่วงหน้าไหม?", a = "วันธรรมดา walk-in ได้ วันเสาร์-อาทิตย์ + เทศกาลแนะนำให้จอง" },
                    new { q = "รับจัดเลี้ยงไหม?", a = "รับ ตั้งแต่ 20 คนขึ้นไป มีห้องส่วนตัว + เมนูพิเศษ" },
                    new { q = "มีเมนูเด็กไหม?", a = "มี เมนูเด็กรสไม่จัด + เก้าอี้เด็กฟรี" },
                    new { q = "มีอาหารเจ / มังสวิรัติไหม?", a = "มี ระบุตอนจองได้ เชฟปรับเมนูให้" },
                    new { q = "ส่งเดลิเวอรี่ไหม?", a = "ส่งผ่าน Grab / Foodpanda / LINE Man ในรัศมี 5 กม." }
                }
            })),
            new(CmsBlockType.CallToAction, J(new {
                headline = "จองโต๊ะล่วงหน้า",
                subheadline = "หลีกเลี่ยงการรอคิว เฉพาะวันเสาร์-อาทิตย์ + เทศกาล",
                ctaText = "จองโต๊ะตอนนี้",
                ctaUrl = "/booking"
            }))
        }),

        (new("เมนู", "menu", PageType.Standard, "เมนูอาหารและเครื่องดื่ม"), new() {
            new(CmsBlockType.Hero, J(new {
                headline = "เมนูของเรา",
                subheadline = "อร่อย คุ้มราคา วัตถุดิบสด — อัพเดทเมนูตามฤดูกาล"
            })),
            new(CmsBlockType.RichText, J(new {
                content = @"<h2>🍲 อาหารจานหลัก</h2>
<ul>
  <li><strong>ผัดกะเพราหมูสับไข่ดาว</strong> — 80 บาท</li>
  <li><strong>ผัดไทยกุ้งสด</strong> — 120 บาท</li>
  <li><strong>ข้าวคลุกกะปิ</strong> — 90 บาท</li>
  <li><strong>ข้าวมันไก่ทอด</strong> — 75 บาท</li>
  <li><strong>ต้มยำกุ้ง</strong> — 180 บาท</li>
  <li><strong>แกงเขียวหวานไก่</strong> — 95 บาท</li>
</ul>
<h2>🥗 สลัด / ของทานเล่น</h2>
<ul>
  <li><strong>ส้มตำไทย</strong> — 60 บาท</li>
  <li><strong>ลาบหมู</strong> — 85 บาท</li>
  <li><strong>ปอเปี๊ยะทอด</strong> — 70 บาท</li>
</ul>
<h2>🥤 เครื่องดื่ม</h2>
<ul>
  <li><strong>ชาเย็น / ชาเขียว</strong> — 35 บาท</li>
  <li><strong>กาแฟเย็น</strong> — 45 บาท</li>
  <li><strong>น้ำผลไม้ปั่น</strong> — 55 บาท</li>
  <li><strong>เบียร์สิงห์ / ลีโอ</strong> — 90 บาท</li>
</ul>
<h2>🍰 ของหวาน</h2>
<ul>
  <li><strong>มะม่วงข้าวเหนียว</strong> — 80 บาท</li>
  <li><strong>บัวลอยไข่หวาน</strong> — 50 บาท</li>
  <li><strong>ไอศกรีมกะทิ</strong> — 45 บาท</li>
</ul>
<p style=""color:#94a3b8;font-size:13px;margin-top:18px"">* ราคาอาจเปลี่ยนแปลงได้ — กรุณาแก้ไขรายการ + ราคาให้ตรงกับร้านของคุณ</p>"
            })),
            new(CmsBlockType.CallToAction, J(new {
                headline = "ชอบเมนูไหน?", subheadline = "จองโต๊ะมาทานที่ร้าน บรรยากาศดีกว่า",
                ctaText = "จองโต๊ะ", ctaUrl = "/booking"
            }))
        }),

        (new("จองโต๊ะ", "booking", PageType.Standard, "จองโต๊ะล่วงหน้า"), new() {
            new(CmsBlockType.Hero, J(new {
                headline = "จองโต๊ะ",
                subheadline = "กรอกรายละเอียด เราจะติดต่อยืนยันภายใน 30 นาที"
            })),
            new(CmsBlockType.RichText, J(new {
                content = @"<h2>📋 ข้อมูลก่อนจอง</h2>
<ul>
  <li>จองล่วงหน้าได้สูงสุด 30 วัน</li>
  <li>โต๊ะส่วนตัว (4-12 ที่นั่ง) ต้องจองล่วงหน้า 1 วัน</li>
  <li>งานเลี้ยง 20+ คน โทรประสานงานก่อน 02-XXX-XXXX</li>
  <li>โต๊ะจะถูกรักษาไว้ 15 นาทีหลังเวลาจอง</li>
</ul>"
            })),
            new(CmsBlockType.ContactForm, J(new {
                headline = "แบบฟอร์มจองโต๊ะ",
                submitText = "ส่งคำขอจอง",
                emailTo = ""
            })),
            new(CmsBlockType.BookingCalendar, "{}")
        }),

        (new("เกี่ยวกับร้าน", "about", PageType.Standard, "เรื่องราวของร้าน"), new() {
            new(CmsBlockType.Hero, J(new {
                headline = "เรื่องราวของร้าน",
                subheadline = "จากครัวบ้านเล็ก ๆ สู่ร้านที่ลูกค้ารัก"
            })),
            new(CmsBlockType.RichText, J(new {
                content = @"<h2>เริ่มต้นจากความรัก</h2>
<p>ร้านของเราเริ่มต้นเมื่อปี 2557 จากความรักในอาหารไทยและความตั้งใจอยากให้คนได้กินอาหารดี ๆ
ในราคาที่จับต้องได้ คุณแม่เป็นเชฟคนแรก สูตรทุกจานยังคงเป็นสูตรของท่านจนถึงวันนี้</p>
<h2>วัตถุดิบ</h2>
<p>เราเลือกซื้อวัตถุดิบจากตลาดเช้าทุกวัน ปลา-กุ้งจากแม่ค้าประจำที่ส่งสดมา 10 ปีติดต่อกัน
ผักออร์แกนิคจากเกษตรกรเชียงใหม่ที่เราไปเยือนเอง</p>
<h2>ทีมเชฟ</h2>
<p>เชฟ 4 คน รวมประสบการณ์กว่า 50 ปี · ทุกจานปรุงสด ไม่มีอาหารแช่แข็ง</p>"
            })),
            new(CmsBlockType.Testimonials, J(new {
                headline = "รางวัล + การันตี",
                testimonials = new[] {
                    new { quote = "Wongnai User's Choice 2566", author = "Wongnai", role = "ร้านอาหารแนะนำ" },
                    new { quote = "ติด TOP 10 ร้านอาหารไทยย่านนี้", author = "Google Reviews", role = "4.8 ดาว · 1,200+ รีวิว" }
                }
            }))
        }),

        (new("ติดต่อเรา", "contact", PageType.Standard, "ที่ตั้ง โทรศัพท์ เวลาเปิด-ปิด"), new() {
            new(CmsBlockType.Hero, J(new {
                headline = "หาเราเจอง่าย",
                subheadline = "เปิดทุกวัน 10:00-22:00 · ที่จอดรถสะดวก"
            })),
            new(CmsBlockType.RichText, J(new {
                content = @"<h2>📍 ที่ตั้ง</h2>
<p>กรุณาแก้ไขที่อยู่ร้านของคุณ<br>
📞 โทร: 02-XXX-XXXX<br>
📱 LINE: @yourshop<br>
📧 อีเมล: hello@restaurant.com</p>
<h2>🕐 เวลาเปิด-ปิด</h2>
<p>จันทร์-ศุกร์: 10:00 - 22:00<br>
เสาร์-อาทิตย์: 09:00 - 23:00<br>
หยุดทุกวันพุธสัปดาห์ที่ 2 ของเดือน</p>
<h2>🚗 การเดินทาง</h2>
<p>BTS: ลงสถานี XXX ทางออก 3 · ที่จอดรถฟรี 40 คัน · มีบริการ Valet</p>"
            })),
            new(CmsBlockType.ContactForm, J(new { headline = "ส่งข้อความ", submitText = "ส่ง", emailTo = "" })),
            new(CmsBlockType.Map, J(new { address = "กรุงเทพมหานคร ประเทศไทย" }))
        })
    };

    // ============================================================
    // CAFE — Blue Bottle / Roots / Pacamara inspired
    // ============================================================
    private static List<(PageMeta, List<BlockMeta>)> CafePlan() => new()
    {
        (new("หน้าหลัก", "home", PageType.Landing, "คาเฟ่ บรรยากาศดี กาแฟพิเศษ มุมนั่งทำงาน WiFi ฟรี"), new() {
            new(CmsBlockType.Hero, J(new {
                headline = "Coffee. Calm. Create.",
                subheadline = "Specialty coffee · มุมนั่งทำงานสบาย · WiFi เร็ว · ปลั๊กไฟครบทุกโต๊ะ",
                ctaText = "ดูเมนู",
                ctaUrl = "/menu"
            })),
            new(CmsBlockType.RichText, J(new {
                content = @"<h2>☕ ทำไมต้องคาเฟ่เรา</h2>
<ul>
  <li>🫘 <strong>Single Origin</strong> — เมล็ดคัดสรรจากไร่ที่ระดับความสูง 1,200m+</li>
  <li>🔥 <strong>คั่วสดทุกสัปดาห์</strong> — โรงคั่วของเราเอง ไม่ใช้สต็อกเก่า</li>
  <li>📶 <strong>WiFi 1Gbps</strong> — เร็วพอสำหรับ video call / Zoom</li>
  <li>🔌 <strong>ปลั๊กไฟทุกโต๊ะ</strong> — นั่งทำงานได้ทั้งวัน ไม่ต้องแย่ง</li>
  <li>🌿 <strong>บรรยากาศสบาย</strong> — โซนเงียบ + โซนคุยงาน แยกชัดเจน</li>
  <li>🥐 <strong>ขนมโฮมเมด</strong> — ครัวซองต์ คุกกี้ เค้ก ทำเองทุกวัน</li>
</ul>"
            })),
            new(CmsBlockType.RichText, J(new {
                content = @"<h2>☕ เมนูยอดนิยม</h2>
<ul>
  <li><strong>Espresso</strong> — แก้วเล็ก กลิ่นหอมเข้ม · 60 บาท</li>
  <li><strong>Latte (Hot/Iced)</strong> — นมสด + ครีมาฟองนุ่ม · 85 บาท</li>
  <li><strong>Flat White</strong> — เข้มกว่า latte นมน้อยกว่า · 90 บาท</li>
  <li><strong>Dirty Coffee</strong> — เอสเปรสโซ่ราดบนนมเย็น · 110 บาท</li>
  <li><strong>Drip Coffee (Single Origin)</strong> — เปลี่ยนเมล็ดทุก 2 สัปดาห์ · 120 บาท</li>
  <li><strong>Matcha Latte</strong> — มัทฉะอุจิ ระดับ ceremonial · 110 บาท</li>
</ul>
<p><a href=""/menu"" class=""btn"">ดูเมนูทั้งหมด →</a></p>"
            })),
            new(CmsBlockType.Testimonials, J(new {
                headline = "รีวิวจากลูกค้า",
                testimonials = new[] {
                    new { quote = "กาแฟดีจริง ๆ บรรยากาศนั่งทำงานได้ยาว ๆ", author = "คุณนัฐ", role = "Freelancer" },
                    new { quote = "WiFi เร็ว ปลั๊กพร้อม กาแฟอร่อย ครบจบในที่เดียว", author = "คุณเบลล์", role = "Designer" },
                    new { quote = "Drip กับ Dirty ของที่นี่ดีที่สุดที่เคยกินในย่านนี้", author = "คุณโอ๊ต", role = "Coffee Lover" },
                    new { quote = "ขนมโฮมเมดอร่อย ครัวซองต์ดีมาก", author = "คุณมิว", role = "Wongnai Reviewer" }
                }
            })),
            new(CmsBlockType.RichText, J(new {
                content = @"<h2>📖 เรื่องราวเมล็ดของเรา</h2>
<ol>
  <li><strong>1. คัดเลือก</strong> — เดินทางไปไร่ที่เชียงราย · ดอยช้าง · ดอยตุง เลือกจาก cupping score 84+</li>
  <li><strong>2. คั่วสด</strong> — คั่วในโรงคั่วของร้านทุก 7 วัน เก็บได้แค่ 30 วัน</li>
  <li><strong>3. บด ณ จุดสั่ง</strong> — บดเฉพาะตอนชง รสชาติสด ใหม่เสมอ</li>
  <li><strong>4. ชงด้วยใจ</strong> — บาริสต้าผ่านการอบรม SCA · ดูแลทุกแก้ว</li>
</ol>"
            })),
            new(CmsBlockType.Faq, J(new {
                headline = "คำถามที่พบบ่อย",
                items = new[] {
                    new { q = "นั่งทำงานได้นานแค่ไหน?", a = "นั่งได้ทั้งวัน ไม่จำกัดเวลา ขอแค่สั่งเครื่องดื่ม 1 แก้ว/3 ชม." },
                    new { q = "มีห้องประชุมไหม?", a = "มีห้อง pod เล็ก รองรับ 4-6 คน จองล่วงหน้าได้ฟรี" },
                    new { q = "ขายเมล็ดกาแฟไหม?", a = "ขาย ถุง 250g · 350g · 1kg เลือกระดับการคั่วได้ในร้าน" },
                    new { q = "สอน barista ไหม?", a = "มี class home barista ทุกวันเสาร์เช้า 9:00 · 990 บาท" },
                    new { q = "Pet friendly ไหม?", a = "ต้อนรับน้องหมา/แมวที่หิ้วลงตักได้ ในโซน outdoor" }
                }
            })),
            new(CmsBlockType.CallToAction, J(new {
                headline = "แวะจิบกาแฟกับเราวันนี้",
                subheadline = "เปิดทุกวัน 7:30 - 19:00 · ที่จอดรถฟรี",
                ctaText = "ดูเส้นทาง",
                ctaUrl = "/contact"
            }))
        }),

        (new("เมนู", "menu", PageType.Standard, "เมนูกาแฟและขนม"), new() {
            new(CmsBlockType.Hero, J(new { headline = "เมนูของเรา", subheadline = "Specialty coffee · ขนมโฮมเมด · ชาพรีเมียม" })),
            new(CmsBlockType.RichText, J(new {
                content = @"<h2>☕ กาแฟดำ</h2>
<ul>
  <li><strong>Espresso</strong> — 60 บาท</li>
  <li><strong>Americano (Hot/Iced)</strong> — 70 / 80 บาท</li>
  <li><strong>Drip Coffee</strong> — 120 บาท</li>
  <li><strong>Cold Brew</strong> — 110 บาท</li>
</ul>
<h2>🥛 กาแฟใส่นม</h2>
<ul>
  <li><strong>Latte</strong> — 85 บาท</li>
  <li><strong>Cappuccino</strong> — 85 บาท</li>
  <li><strong>Flat White</strong> — 90 บาท</li>
  <li><strong>Mocha</strong> — 95 บาท</li>
  <li><strong>Dirty Coffee</strong> — 110 บาท</li>
</ul>
<h2>🍵 ชา + อื่น ๆ</h2>
<ul>
  <li><strong>Matcha Latte</strong> — 110 บาท</li>
  <li><strong>Hojicha Latte</strong> — 100 บาท</li>
  <li><strong>Chai Latte</strong> — 95 บาท</li>
  <li><strong>Hot Chocolate</strong> — 90 บาท</li>
</ul>
<h2>🥐 ขนม / Brunch</h2>
<ul>
  <li><strong>Butter Croissant</strong> — 65 บาท</li>
  <li><strong>Almond Croissant</strong> — 95 บาท</li>
  <li><strong>Avocado Toast</strong> — 165 บาท</li>
  <li><strong>Big Breakfast</strong> — 220 บาท</li>
</ul>"
            }))
        }),

        (new("เมล็ดกาแฟ", "beans", PageType.Standard, "เมล็ดกาแฟคั่วสด ขายปลีก"), new() {
            new(CmsBlockType.Hero, J(new {
                headline = "เมล็ดกาแฟของเรา",
                subheadline = "Single origin · คั่วสดทุกสัปดาห์ · ส่งทั่วประเทศ"
            })),
            new(CmsBlockType.ProductGrid, J(new { headline = "เมล็ดที่กำลังคั่วอยู่", limit = 12 })),
            new(CmsBlockType.RichText, J(new {
                content = @"<h2>📦 จัดส่ง</h2>
<p>คั่วเสร็จ แพ็คใส่ถุง one-way valve ส่งภายใน 24 ชม.<br>
ส่งฟรีเมื่อซื้อครบ 1,000 บาท · ใช้ Kerry / Flash · 1-3 วันถึง</p>"
            }))
        }),

        (new("เกี่ยวกับเรา", "about", PageType.Standard, "เรื่องราวคาเฟ่"), new() {
            new(CmsBlockType.Hero, J(new {
                headline = "เริ่มต้นจาก 1 เครื่องชง 1 ความฝัน",
                subheadline = "เปิดเมื่อปี 2562 · ตั้งใจให้คาเฟ่เป็นพื้นที่ของทุกคน"
            })),
            new(CmsBlockType.RichText, J(new {
                content = @"<h2>ปรัชญาของเรา</h2>
<p>เราเชื่อว่ากาแฟดีไม่ต้องแพง แค่ตั้งใจตั้งแต่เลือกเมล็ด · คั่ว · ชง<br>
ทุกแก้วเรารู้จักไร่ที่มา รู้จักเกษตรกร และจ่ายราคาที่เป็นธรรมให้พวกเขา</p>
<h2>ทีมเรา</h2>
<p>บาริสต้า 6 คน · ผ่านการอบรม SCA Foundation ทุกคน<br>
2 คนผ่าน Barista Skills Intermediate · 1 คนเป็น Q Grader</p>"
            })),
            new(CmsBlockType.Testimonials, J(new {
                headline = "รางวัล",
                testimonials = new[] {
                    new { quote = "TOP 50 Cafe in Bangkok 2566", author = "BK Magazine", role = "" },
                    new { quote = "Best Drip Coffee in BKK", author = "Time Out", role = "2565" }
                }
            }))
        }),

        (new("ติดต่อ / ที่ตั้ง", "contact", PageType.Standard, "ที่ตั้ง · WiFi · ที่จอดรถ"), new() {
            new(CmsBlockType.Hero, J(new {
                headline = "หาเราเจอง่าย",
                subheadline = "เปิดทุกวัน 7:30 - 19:00 · WiFi ฟรี · ปลั๊กทุกโต๊ะ"
            })),
            new(CmsBlockType.RichText, J(new {
                content = @"<h2>📍 ที่ตั้ง</h2>
<p>กรุณาแก้ไขที่อยู่ของคุณ<br>
📞 02-XXX-XXXX · LINE: @yourcafe · IG: @yourcafe</p>
<h2>📶 WiFi</h2>
<p>SSID: <strong>cafe_guest</strong> · password ติดที่เคาน์เตอร์</p>
<h2>🚗 ที่จอดรถ</h2>
<p>ฟรี 15 คัน · มี Valet เฉพาะวันเสาร์-อาทิตย์</p>"
            })),
            new(CmsBlockType.ContactForm, J(new { headline = "ส่งข้อความ", submitText = "ส่ง", emailTo = "" })),
            new(CmsBlockType.Map, J(new { address = "กรุงเทพมหานคร ประเทศไทย" }))
        })
    };

    // ============================================================
    // RETAIL — Uniqlo / Muji-style retail landing
    // ============================================================
    private static List<(PageMeta, List<BlockMeta>)> RetailPlan() => new()
    {
        (new("หน้าหลัก", "home", PageType.Landing, "ร้านค้า สินค้าคุณภาพ ราคาดี"), new() {
            new(CmsBlockType.Hero, J(new {
                headline = "Simple. Quality. Everyday.",
                subheadline = "สินค้าคุณภาพ ดีไซน์เรียบ ใช้งานได้จริง · มีหน้าร้าน + ส่งทั่วไทย",
                ctaText = "ช้อปเลย",
                ctaUrl = "/products"
            })),
            new(CmsBlockType.RichText, J(new {
                content = @"<h2>✨ ทำไมเลือกเรา</h2>
<ul>
  <li>👕 <strong>เลือกแล้วเลือกอีก</strong> — เราคัดสินค้าทุกชิ้นด้วยตัวเอง</li>
  <li>💰 <strong>ราคายุติธรรม</strong> — ตรงจากโรงงาน ไม่ผ่านคนกลาง</li>
  <li>🏪 <strong>มีหน้าร้านให้ลองจริง</strong> — ลองก่อนซื้อได้ทุกชิ้น</li>
  <li>🚚 <strong>ส่งฟรีเมื่อช้อปครบ 990</strong> — Kerry / Flash 1-3 วันถึง</li>
  <li>♻️ <strong>เปลี่ยน-คืนได้ 30 วัน</strong> — ไม่พอใจคืนเงินเต็มจำนวน</li>
  <li>🌱 <strong>วัสดุยั่งยืน</strong> — เลือกใช้วัสดุ recycle / organic</li>
</ul>"
            })),
            new(CmsBlockType.ProductGrid, J(new { headline = "สินค้ายอดนิยม", featured = true, limit = 8 })),
            new(CmsBlockType.Testimonials, J(new {
                headline = "รีวิวจากลูกค้า",
                testimonials = new[] {
                    new { quote = "ของจริงตรงปก คุณภาพดีกว่าราคา", author = "คุณแอน ส.", role = "Shopee Verified" },
                    new { quote = "ส่งเร็วมาก แพ็คเรียบร้อย", author = "คุณบี ว.", role = "Google 5 ดาว" },
                    new { quote = "หน้าร้านสวย พนักงานน่ารัก ไม่กดดัน", author = "คุณซี พ.", role = "Walk-in" },
                    new { quote = "ของขวัญน่ารักทุกชิ้น ห่อสวยฟรี", author = "คุณดี จ.", role = "Repeat Customer" }
                }
            })),
            new(CmsBlockType.RichText, J(new {
                content = @"<h2>🛍️ ขั้นตอนการสั่งซื้อ</h2>
<ol>
  <li><strong>1. เลือกสินค้า</strong> — กดเพิ่มลงตะกร้า เปรียบเทียบได้</li>
  <li><strong>2. ใส่ที่อยู่</strong> — กรอกครั้งเดียว บันทึกไว้สำหรับครั้งหน้า</li>
  <li><strong>3. ชำระเงิน</strong> — โอน / พร้อมเพย์ / บัตรเครดิต / COD</li>
  <li><strong>4. รอรับของ</strong> — ส่งภายใน 24 ชม. ติดตามสถานะออนไลน์</li>
</ol>"
            })),
            new(CmsBlockType.Faq, J(new {
                headline = "คำถามที่พบบ่อย",
                items = new[] {
                    new { q = "ส่งของกี่วัน?", a = "ในเขตกรุงเทพ 1-2 วัน, ต่างจังหวัด 2-4 วัน" },
                    new { q = "ส่งเงินสดปลายทางได้ไหม?", a = "รองรับ COD ทั่วประเทศ มีค่าธรรมเนียมเพิ่ม 30 บาท" },
                    new { q = "เปลี่ยน-คืนสินค้าได้ไหม?", a = "คืนได้ภายใน 30 วัน ถ้าสินค้ายังไม่ได้ใช้และอยู่ในสภาพเดิม" },
                    new { q = "มีใบกำกับภาษีไหม?", a = "ออกใบกำกับภาษีเต็มรูปแบบ ระบุตอนสั่งซื้อ" },
                    new { q = "ส่งต่างประเทศได้ไหม?", a = "ส่งได้ ค่าส่งคำนวณตามน้ำหนัก + ปลายทาง" }
                }
            })),
            new(CmsBlockType.CallToAction, J(new {
                headline = "สมัครสมาชิก รับส่วนลด 100 บาท",
                subheadline = "สำหรับการสั่งซื้อครั้งแรก · ใช้ได้ทันที",
                ctaText = "สมัครเลย",
                ctaUrl = "/contact"
            })),
            new(CmsBlockType.Newsletter, J(new {
                headline = "รับข่าวสาร + โปรโมชั่นพิเศษ",
                subheadline = "สมาชิกได้สิทธิ์ pre-order ก่อนใคร",
                buttonText = "สมัครรับข่าว"
            }))
        }),

        (new("สินค้า", "products", PageType.Category, "สินค้าทั้งหมด"), new() {
            new(CmsBlockType.Hero, J(new { headline = "สินค้าทั้งหมด", subheadline = "เลือกซื้อตามหมวดที่คุณสนใจ" })),
            new(CmsBlockType.ProductGrid, J(new { limit = 24 }))
        }),

        (new("หน้าร้าน", "stores", PageType.Standard, "สาขาหน้าร้าน"), new() {
            new(CmsBlockType.Hero, J(new { headline = "สาขาของเรา", subheadline = "แวะมาลองของจริงก่อนตัดสินใจซื้อ" })),
            new(CmsBlockType.RichText, J(new {
                content = @"<h2>📍 สาขาทั้งหมด</h2>
<ul>
  <li><strong>สาขาสีลม</strong> — ถ.สีลม · เปิด 10:00-21:00 · ใกล้ BTS ศาลาแดง</li>
  <li><strong>สาขาเอกมัย</strong> — ซ.เอกมัย 12 · เปิด 11:00-22:00 · มีที่จอดรถ</li>
  <li><strong>สาขาเชียงใหม่</strong> — นิมมานเหมินทร์ · เปิด 10:00-21:00</li>
</ul>
<p style=""color:#94a3b8;font-size:13px"">* แก้ไขรายการสาขาให้ตรงกับร้านของคุณ</p>"
            })),
            new(CmsBlockType.Map, J(new { address = "กรุงเทพมหานคร ประเทศไทย" }))
        }),

        (new("เกี่ยวกับเรา", "about", PageType.Standard, "เรื่องราวของร้าน"), new() {
            new(CmsBlockType.Hero, J(new { headline = "เกี่ยวกับร้านของเรา", subheadline = "เริ่มต้นจากความรักในสิ่งที่ทำ" })),
            new(CmsBlockType.RichText, J(new {
                content = @"<h2>เรื่องราวของเรา</h2>
<p>เราเริ่มต้นจากการเป็นร้านเล็ก ๆ ที่อยากให้คนไทยได้เข้าถึงสินค้าคุณภาพดีในราคาที่จับต้องได้
ด้วยการคัดสรรสินค้าทุกชิ้นด้วยตนเอง รับประกันคุณภาพทุกออเดอร์</p>
<h2>ปรัชญาการเลือกสินค้า</h2>
<ul>
  <li>✓ ใช้ได้นาน · ดีไซน์เรียบที่ไม่ตกเทรนด์</li>
  <li>✓ วัสดุดี · ผ่านมาตรฐาน OEKO-TEX / GOTS</li>
  <li>✓ ราคาเป็นธรรม · ไม่มาร์กอัพเกินจริง</li>
  <li>✓ ผลิตอย่างยั่งยืน · ใส่ใจสิ่งแวดล้อม + แรงงาน</li>
</ul>"
            }))
        }),

        (new("ติดต่อ", "contact", PageType.Standard, "ติดต่อร้านค้า"), new() {
            new(CmsBlockType.Hero, J(new { headline = "ติดต่อเรา", subheadline = "ทีมแอดมินพร้อมตอบทุกคำถาม จ-ส 9:00-20:00" })),
            new(CmsBlockType.RichText, J(new {
                content = @"<h2>📞 ติดต่อสอบถาม</h2>
<p>LINE: @yourshop · โทร: 02-XXX-XXXX · อีเมล: hello@shop.com</p>"
            })),
            new(CmsBlockType.ContactForm, J(new { headline = "สอบถามสินค้า", submitText = "ส่ง", emailTo = "" })),
            new(CmsBlockType.Map, J(new { address = "กรุงเทพมหานคร ประเทศไทย" }))
        })
    };

    // ============================================================
    // ECOMMERCE — Shopify/Allbirds-style online-only
    // ============================================================
    private static List<(PageMeta, List<BlockMeta>)> EcommercePlan() => new()
    {
        (new("หน้าหลัก", "home", PageType.Landing, "ร้านออนไลน์ ส่งฟรี รีวิวจริง"), new() {
            new(CmsBlockType.Hero, J(new {
                headline = "ช้อปออนไลน์ · ส่งฟรีทั่วไทย",
                subheadline = "สั่งวันนี้ ส่งภายใน 24 ชม. · ฟรีส่งเมื่อช้อปครบ 590 · เปลี่ยน-คืนใน 30 วัน",
                ctaText = "ช้อปเลย",
                ctaUrl = "/products"
            })),
            new(CmsBlockType.RichText, J(new {
                content = @"<div style=""background:#FEF3C7;padding:14px;border-radius:12px;text-align:center;font-weight:600"">
🚚 ส่งฟรีเมื่อช้อปครบ 590 บาท · 📦 ส่งภายใน 24 ชม. · ↩️ เปลี่ยน-คืน 30 วัน · ⭐ รีวิวจริง 4.9/5
</div>"
            })),
            new(CmsBlockType.ProductGrid, J(new { headline = "🔥 ขายดีที่สุดสัปดาห์นี้", featured = true, limit = 8 })),
            new(CmsBlockType.RichText, J(new {
                content = @"<h2>💯 ทำไมต้องเรา</h2>
<ul>
  <li>📦 <strong>ส่งฟรีเมื่อ ≥ 590</strong> — กรุงเทพภายในวัน · ตจว. 1-3 วัน</li>
  <li>↩️ <strong>คืนได้ 30 วัน</strong> — ไม่ถูกใจคืนเงินเต็ม ไม่ถามเหตุผล</li>
  <li>⭐ <strong>รีวิวจริง 10,000+</strong> — เฉลี่ย 4.9/5 · ดูได้ทุกสินค้า</li>
  <li>🛡️ <strong>ปลอดภัย 100%</strong> — Verified by Shopee/Lazada · มีใบ ภพ.20</li>
  <li>💳 <strong>ชำระยืดหยุ่น</strong> — โอน · พร้อมเพย์ · บัตร · COD · ผ่อน 0%</li>
  <li>📱 <strong>แอดมินตอบไว</strong> — LINE OA ตอบใน 5 นาที จ-ศ 9-21</li>
</ul>"
            })),
            new(CmsBlockType.Testimonials, J(new {
                headline = "⭐⭐⭐⭐⭐ รีวิวจากลูกค้า",
                testimonials = new[] {
                    new { quote = "ของตรงปก คุณภาพดีกว่าราคาที่จ่าย ส่งไวมาก", author = "คุณนัท", role = "ซื้อซ้ำครั้งที่ 5" },
                    new { quote = "เปลี่ยนคืนง่ายมาก แอดมินตอบทันที", author = "คุณจอย", role = "Verified Buyer" },
                    new { quote = "แพ็คดี ไม่มีตำหนิเลย ของขวัญด้วย น่ารักมาก", author = "คุณพี่ตู่", role = "Shopee Gold Member" },
                    new { quote = "ราคาดีกว่าหน้าร้าน คุณภาพเหมือนกันเป๊ะ", author = "คุณบัว", role = "Lazada Verified" }
                }
            })),
            new(CmsBlockType.ProductGrid, J(new { headline = "🆕 มาใหม่", limit = 8 })),
            new(CmsBlockType.Faq, J(new {
                headline = "คำถามที่พบบ่อย",
                items = new[] {
                    new { q = "ส่งฟรีจริงไหม?", a = "ฟรีจริง เมื่อยอดสุทธิ ≥ 590 บาท ส่งทั่วประเทศ ไม่จำกัดน้ำหนัก" },
                    new { q = "ส่งกี่วันถึง?", a = "กรุงเทพ 1 วัน · ปริมณฑล 1-2 วัน · ตจว. 2-3 วัน" },
                    new { q = "คืนสินค้าได้ไหม?", a = "ได้ภายใน 30 วัน คืนเงิน 100% สินค้าต้องอยู่ในสภาพเดิม" },
                    new { q = "มี COD ไหม?", a = "มี ค่าธรรมเนียม 30 บาท · จำกัดยอดไม่เกิน 5,000 บาท/ออเดอร์" },
                    new { q = "ใบกำกับภาษี?", a = "ออกได้ ระบุตอนชำระเงิน · ส่งให้ทางอีเมล" },
                    new { q = "ของแท้ไหม?", a = "100% ของแท้ มีใบรับรองจากแบรนด์ · คืน 10 เท่าถ้าเจอของปลอม" }
                }
            })),
            new(CmsBlockType.CallToAction, J(new {
                headline = "สมัครสมาชิก รับส่วนลด 10%",
                subheadline = "ใช้ได้ทันทีกับออเดอร์แรก · สะสมแต้มได้ทุกออเดอร์",
                ctaText = "สมัครฟรี",
                ctaUrl = "/contact"
            })),
            new(CmsBlockType.Newsletter, J(new {
                headline = "Subscribe รับโค้ดส่วนลด 100฿",
                subheadline = "ข่าวโปรโมชั่น · สินค้า pre-order · ส่วนลดเฉพาะสมาชิก",
                buttonText = "รับโค้ด"
            }))
        }),

        (new("สินค้าทั้งหมด", "products", PageType.Category, "สินค้าทั้งหมด"), new() {
            new(CmsBlockType.Hero, J(new { headline = "สินค้าทั้งหมด", subheadline = "กรองตามหมวด · ราคา · รีวิว ได้" })),
            new(CmsBlockType.ProductGrid, J(new { limit = 36 }))
        }),

        (new("ขายดี", "bestsellers", PageType.Category, "สินค้าขายดี"), new() {
            new(CmsBlockType.Hero, J(new { headline = "🔥 ขายดีอันดับ 1", subheadline = "อ้างอิงยอดขายใน 30 วันล่าสุด" })),
            new(CmsBlockType.ProductGrid, J(new { headline = "TOP 24", featured = true, limit = 24 }))
        }),

        (new("เกี่ยวกับเรา", "about", PageType.Standard, "เรื่องราวร้านออนไลน์"), new() {
            new(CmsBlockType.Hero, J(new { headline = "เรื่องราวของเรา", subheadline = "ร้านออนไลน์ที่ลูกค้า 50,000+ ไว้ใจ" })),
            new(CmsBlockType.RichText, J(new {
                content = @"<h2>เริ่มต้นในห้องเล็ก ๆ</h2>
<p>เราเริ่มเมื่อปี 2563 ตอนเป็นโควิด ขายของออนไลน์เพื่อหารายได้เสริม
4 ปีผ่านไป เรามีลูกค้าประจำกว่า 50,000 คน · ขายสินค้าไปแล้วกว่า 300,000 ชิ้น</p>
<h2>คำสัญญาของเรา</h2>
<ul>
  <li>💯 ของแท้ทุกชิ้น มีใบรับรอง</li>
  <li>📦 ส่งภายใน 24 ชม. หลังชำระเงิน</li>
  <li>↩️ คืนได้ 30 วัน เต็มจำนวน</li>
  <li>📞 แอดมินตอบทุกคำถาม ใน 5 นาที</li>
</ul>"
            })),
            new(CmsBlockType.Testimonials, J(new {
                headline = "ตัวเลขที่เราภูมิใจ",
                testimonials = new[] {
                    new { quote = "50,000+", author = "ลูกค้าที่ไว้ใจ", role = "ตั้งแต่ปี 2563" },
                    new { quote = "300,000+", author = "ออเดอร์ที่ส่งสำเร็จ", role = "" },
                    new { quote = "4.9/5", author = "คะแนนเฉลี่ย", role = "จาก 10,000+ รีวิว" }
                }
            }))
        }),

        (new("ติดต่อ", "contact", PageType.Standard, "ติดต่อร้าน"), new() {
            new(CmsBlockType.Hero, J(new { headline = "ติดต่อเรา", subheadline = "ทีมแอดมินตอบทุกวัน 9:00-21:00" })),
            new(CmsBlockType.RichText, J(new {
                content = @"<h2>📞 ช่องทางติดต่อ</h2>
<p>LINE OA: @yourshop (เร็วที่สุด)<br>
อีเมล: hello@shop.com<br>
โทร: 02-XXX-XXXX (จ-ศ 9-18)<br>
Facebook: facebook.com/yourshop<br>
Instagram: @yourshop</p>"
            })),
            new(CmsBlockType.ContactForm, J(new { headline = "ส่งข้อความ", submitText = "ส่ง", emailTo = "" })),
            new(CmsBlockType.Map, J(new { address = "กรุงเทพมหานคร ประเทศไทย" }))
        })
    };

    // ============================================================
    // TRADING — Alibaba-style B2B wholesale catalog
    // ============================================================
    private static List<(PageMeta, List<BlockMeta>)> TradingPlan() => new()
    {
        (new("หน้าหลัก", "home", PageType.Landing, "ผู้นำเข้า ส่งออก ขายส่ง B2B"), new() {
            new(CmsBlockType.Hero, J(new {
                headline = "B2B Wholesale · Verified Supplier",
                subheadline = "ผู้นำเข้า + จัดจำหน่ายในไทย 15+ ปี · MOQ ยืดหยุ่น · ราคาตรงจากโรงงาน",
                ctaText = "ขอใบเสนอราคา",
                ctaUrl = "/rfq"
            })),
            new(CmsBlockType.RichText, J(new {
                content = @"<h2>🏭 ข้อได้เปรียบของเรา</h2>
<ul>
  <li>📦 <strong>MOQ ต่ำ</strong> — สั่งเริ่ม 100 ชิ้นได้ (เทียบกับโรงงาน 1,000+)</li>
  <li>💱 <strong>ราคาตรงจากต้นทาง</strong> — นำเข้าเอง ไม่ผ่านเทรดเดอร์</li>
  <li>📋 <strong>เอกสารครบ</strong> — Invoice · COA · COO · Form D/E พร้อมส่ง</li>
  <li>🚢 <strong>นำเข้า + กระจายสินค้า</strong> — รับจัดส่งทั่วประเทศ + ส่งออก ASEAN</li>
  <li>🤝 <strong>เป็นตัวแทนได้</strong> — มีแพ็กเกจ Distributor ในแต่ละจังหวัด</li>
  <li>🔄 <strong>OEM / Private Label</strong> — บรรจุภัณฑ์โลโก้ลูกค้า</li>
</ul>"
            })),
            new(CmsBlockType.ProductGrid, J(new { headline = "📦 สินค้าหลักที่จัดจำหน่าย", featured = true, limit = 8 })),
            new(CmsBlockType.PricingTable, J(new {
                headline = "💰 ราคาขายส่ง (Bulk Pricing)",
                plans = new[] {
                    new { name = "Sample / Trial", price = "MOQ 50 ชิ้น",
                          features = new[] { "ราคาปลีก", "ตัวอย่างทดลอง 5 ชิ้นฟรี", "ส่งใน 1 วัน" } },
                    new { name = "Wholesale", price = "MOQ 500 ชิ้น",
                          features = new[] { "ราคาส่ง ลด 25%", "Credit term 15 วัน", "ส่งฟรีในไทย", "มี Account Manager" } },
                    new { name = "Distributor", price = "MOQ 5,000 ชิ้น",
                          features = new[] { "ราคาตัวแทน ลด 40%", "Credit term 30 วัน", "Exclusive ในจังหวัด", "Training + การตลาด" } }
                }
            })),
            new(CmsBlockType.Testimonials, J(new {
                headline = "พาร์ทเนอร์ที่ไว้ใจเรา",
                testimonials = new[] {
                    new { quote = "ส่งของตรงเวลา เอกสารครบทุก lot — ทำงานง่ายมาก", author = "ABC Trading", role = "ตัวแทนภาคเหนือ 5 ปี" },
                    new { quote = "ได้ราคาดีกว่าสั่งจากจีนเอง รวมค่าขนส่งแล้ว", author = "XYZ Wholesale", role = "ผู้ค้าส่งกรุงเทพ" },
                    new { quote = "Customer service ตอบเร็วทั้งภาษาไทย-อังกฤษ-จีน", author = "Mega Import", role = "Importer" },
                    new { quote = "ทำ OEM ให้แบรนด์เรา งานสะอาด ตรงสเปก", author = "Brand X", role = "Private Label" }
                }
            })),
            new(CmsBlockType.RichText, J(new {
                content = @"<h2>🔄 ขั้นตอนการสั่งซื้อ B2B</h2>
<ol>
  <li><strong>1. สอบถาม / ขอ Catalog</strong> — ส่ง LINE / อีเมล แจ้งสินค้าที่สนใจ</li>
  <li><strong>2. ส่งตัวอย่าง</strong> — ทดลองก่อน 5-10 ชิ้น (เก็บค่าส่งอย่างเดียว)</li>
  <li><strong>3. เซ็น Trading Agreement</strong> — ราคา · MOQ · credit term · พื้นที่</li>
  <li><strong>4. PO + ชำระเงิน</strong> — โอน 50% มัดจำ · ส่งของ · ชำระส่วนที่เหลือ</li>
  <li><strong>5. ส่งสินค้า + เอกสาร</strong> — Invoice · COA · COO ครบทุก lot</li>
</ol>"
            })),
            new(CmsBlockType.Faq, J(new {
                headline = "คำถามที่พบบ่อย",
                items = new[] {
                    new { q = "MOQ น้อยที่สุดเท่าไหร่?", a = "เริ่ม 50 ชิ้นสำหรับ trial · 500 ชิ้นสำหรับราคาขายส่ง" },
                    new { q = "มี credit term ไหม?", a = "มี ลูกค้าผ่านการตรวจสอบเครดิตได้ 15-30 วัน" },
                    new { q = "ออก Form D/E ได้ไหม?", a = "ออกได้ทุกออเดอร์ส่งออก ASEAN · ใช้เวลา 3-5 วัน" },
                    new { q = "รับทำ OEM ไหม?", a = "รับ เริ่ม MOQ 1,000 ชิ้น · ออกแบบบรรจุภัณฑ์ให้ฟรี" },
                    new { q = "ส่งออกได้ประเทศไหนบ้าง?", a = "ส่งทั่ว ASEAN · จีน · อินเดีย · ตะวันออกกลาง · ยุโรป" }
                }
            })),
            new(CmsBlockType.CallToAction, J(new {
                headline = "พร้อมเป็นพาร์ทเนอร์กับเรา?",
                subheadline = "ขอใบเสนอราคา / Catalog ฟรี · ตอบกลับใน 1 วันทำการ",
                ctaText = "ขอใบเสนอราคา",
                ctaUrl = "/rfq"
            }))
        }),

        (new("Catalog สินค้า", "products", PageType.Category, "สินค้าทั้งหมดใน catalog"), new() {
            new(CmsBlockType.Hero, J(new {
                headline = "Catalog สินค้า",
                subheadline = "ดาวน์โหลด PDF Catalog ฉบับเต็ม · ขอราคาตามจำนวน"
            })),
            new(CmsBlockType.ProductGrid, J(new { limit = 36 })),
            new(CmsBlockType.CallToAction, J(new {
                headline = "ต้องการ Catalog ฉบับเต็ม?", subheadline = "PDF 80+ หน้า ส่งทางอีเมล ขอฟรี",
                ctaText = "ขอ Catalog", ctaUrl = "/rfq"
            }))
        }),

        (new("ขอใบเสนอราคา (RFQ)", "rfq", PageType.Standard, "Request for Quote"), new() {
            new(CmsBlockType.Hero, J(new {
                headline = "Request for Quote (RFQ)",
                subheadline = "แจ้งสินค้า + จำนวน · ทีมขายตอบกลับใบเสนอราคาภายใน 1 วันทำการ"
            })),
            new(CmsBlockType.RichText, J(new {
                content = @"<h2>📋 ข้อมูลที่ควรแจ้ง</h2>
<ul>
  <li>ชื่อสินค้า / รหัสสินค้าจาก catalog</li>
  <li>จำนวน + ระยะเวลาสั่งซื้อ (ครั้งเดียว / รายเดือน)</li>
  <li>ประเทศ / จังหวัดปลายทาง</li>
  <li>ต้องการ OEM / Private Label หรือไม่</li>
  <li>เอกสารที่ต้องการ (Form D/E, COA, COO ฯลฯ)</li>
</ul>"
            })),
            new(CmsBlockType.ContactForm, J(new {
                headline = "ส่งคำขอใบเสนอราคา (RFQ)",
                submitText = "ส่ง RFQ",
                emailTo = "",
                leadType = "Rfq",
                askCompany = true,
                askTaxId = true,
                phoneRequired = true,
                extraFields = new object[] {
                    new { name = "product_category", label = "หมวดสินค้าที่สนใจ", type = "text", required = true },
                    new { name = "quantity", label = "จำนวน / MOQ ที่ต้องการ", type = "text", required = true },
                    new { name = "target_price", label = "ราคาเป้าหมาย (THB ต่อหน่วย)", type = "number" },
                    new { name = "ship_to_country", label = "ประเทศ / จังหวัดปลายทาง", type = "text" },
                    new { name = "needs_oem", label = "ต้องการ OEM / Private Label", type = "select", options = new[] { "ไม่ต้องการ", "ต้องการ OEM", "ต้องการ Private Label" } },
                    new { name = "needs_docs", label = "เอกสารที่ต้องการ", type = "text" }
                }
            }))
        }),

        (new("Distributor Program", "distributor", PageType.Standard, "โปรแกรมตัวแทนจำหน่าย"), new() {
            new(CmsBlockType.Hero, J(new {
                headline = "เป็นตัวแทนจำหน่ายกับเรา",
                subheadline = "Exclusive 1 จังหวัด · มาร์จิ้น 30-40% · มีระบบสนับสนุน"
            })),
            new(CmsBlockType.RichText, J(new {
                content = @"<h2>🎯 สิ่งที่ตัวแทนจะได้รับ</h2>
<ul>
  <li>✓ Exclusive ในจังหวัด/พื้นที่ที่ตกลง</li>
  <li>✓ ราคา distributor ลดสูงสุด 40%</li>
  <li>✓ Credit term 30-45 วัน</li>
  <li>✓ Marketing support (ป้าย · brochure · online ads)</li>
  <li>✓ Training 2 วัน · refresher ทุก 6 เดือน</li>
  <li>✓ Account Manager เฉพาะ</li>
</ul>
<h2>💼 คุณสมบัติตัวแทน</h2>
<ul>
  <li>มีหน้าร้าน / showroom / โกดัง</li>
  <li>มีทีมขายอย่างน้อย 2 คน</li>
  <li>เงินทุนหมุนเวียนขั้นต่ำ 500,000 บาท</li>
  <li>พร้อม commit MOQ รายเดือน</li>
</ul>"
            })),
            new(CmsBlockType.ContactForm, J(new { headline = "สมัครเป็นตัวแทน", submitText = "ส่งใบสมัคร", emailTo = "" }))
        }),

        (new("เกี่ยวกับเรา", "about", PageType.Standard, "บริษัทเทรดดิ้ง"), new() {
            new(CmsBlockType.Hero, J(new { headline = "ผู้นำเข้า + จัดจำหน่ายระดับมืออาชีพ", subheadline = "ก่อตั้งปี 2552 · 15 ปีในวงการ" })),
            new(CmsBlockType.RichText, J(new {
                content = @"<h2>เกี่ยวกับบริษัท</h2>
<p>ก่อตั้งปี 2552 · จดทะเบียนทุน 20 ล้านบาท<br>
ทีมงาน 35 คน · โกดังสินค้า 3,000 ตรม. · สำนักงานในกรุงเทพ + เซินเจิ้น<br>
ลูกค้ามากกว่า 500 ราย · ตัวแทนทั่วประเทศ 45 ราย</p>
<h2>มาตรฐาน + การรับรอง</h2>
<ul>
  <li>✓ ISO 9001:2015 · ISO 14001</li>
  <li>✓ Member of Thai Chamber of Commerce</li>
  <li>✓ Verified Importer · กรมศุลกากร</li>
  <li>✓ Member of Alibaba.com Gold Supplier</li>
</ul>"
            }))
        }),

        (new("ติดต่อ", "contact", PageType.Standard, "ติดต่อทีมขาย"), new() {
            new(CmsBlockType.Hero, J(new { headline = "ติดต่อทีมขาย B2B", subheadline = "ทีมขายไทย · จีน · อังกฤษ พร้อมให้บริการ" })),
            new(CmsBlockType.RichText, J(new {
                content = @"<h2>📞 ติดต่อ</h2>
<p>โทร: 02-XXX-XXXX (จ-ส 8:30-17:30)<br>
อีเมล: sales@trading.com<br>
LINE OA: @trading · WeChat: tradingco<br>
WhatsApp: +66 XX XXX XXXX</p>"
            })),
            new(CmsBlockType.ContactForm, J(new { headline = "ส่งข้อความ", submitText = "ส่ง", emailTo = "" })),
            new(CmsBlockType.Map, J(new { address = "กรุงเทพมหานคร ประเทศไทย" }))
        })
    };

    // ============================================================
    // AGRICULTURE — Farm-to-table / CSA box subscription
    // ============================================================
    private static List<(PageMeta, List<BlockMeta>)> AgriculturePlan() => new()
    {
        (new("หน้าหลัก", "home", PageType.Landing, "ฟาร์มออร์แกนิก ผักสด ส่งตรงถึงบ้าน"), new() {
            new(CmsBlockType.Hero, J(new {
                headline = "From Farm to Your Table",
                subheadline = "ผัก-ผลไม้ออร์แกนิก เก็บเช้า ส่งบ่ายเดียว · ไม่ใช้สารเคมี 100% · สมัครสมาชิกรับกล่องผักรายสัปดาห์",
                ctaText = "สมัครสมาชิก CSA",
                ctaUrl = "/csa"
            })),
            new(CmsBlockType.RichText, J(new {
                content = @"<h2>🌱 ฟาร์มของเราต่างจากที่อื่นยังไง</h2>
<ul>
  <li>🌿 <strong>Certified Organic</strong> — มาตรฐาน Organic Thailand · IFOAM</li>
  <li>🌾 <strong>เก็บเช้า ส่งบ่าย</strong> — สดที่สุดในตลาด เก็บจากแปลงไม่เกิน 12 ชม.</li>
  <li>🚫 <strong>ไม่ใช้สารเคมีใด ๆ</strong> — ปุ๋ยอินทรีย์ · ป้องกันแมลงด้วยวิธีธรรมชาติ</li>
  <li>🐝 <strong>ดีต่อสิ่งแวดล้อม</strong> — รักษาความหลากหลายชีวภาพ · เลี้ยงผึ้ง</li>
  <li>👨‍🌾 <strong>รู้จักเกษตรกร</strong> — ทุกผักมีชื่อคนปลูก · เยี่ยมฟาร์มได้</li>
  <li>📦 <strong>กล่อง CSA รายสัปดาห์</strong> — รับผักหลากหลายตามฤดูกาล</li>
</ul>"
            })),
            new(CmsBlockType.PricingTable, J(new {
                headline = "📦 แพ็กเกจกล่องผัก CSA",
                plans = new[] {
                    new { name = "Single (1-2 คน)", price = "฿390/สัปดาห์",
                          features = new[] { "ผัก 5-6 ชนิด", "1.5-2 kg", "สูตรอาหารแถมฟรี", "ส่งวันพุธ" } },
                    new { name = "Family (3-4 คน)", price = "฿690/สัปดาห์",
                          features = new[] { "ผัก 8-10 ชนิด", "3-4 kg", "ผลไม้ 1 ชนิด", "ไข่ไก่อินทรีย์ 10 ฟอง", "ส่งวันพุธหรือเสาร์" } },
                    new { name = "Premium (4-6 คน)", price = "฿1,290/สัปดาห์",
                          features = new[] { "ผัก 12+ ชนิด", "5-6 kg", "ผลไม้ 2 ชนิด", "ไข่ไก่ 20 ฟอง + น้ำผึ้ง", "เลือกวันส่งได้", "ปรับเมนูได้" } }
                }
            })),
            new(CmsBlockType.RichText, J(new {
                content = @"<h2>📅 ปฏิทินการเก็บเกี่ยว (Harvest Calendar)</h2>
<ul>
  <li><strong>ม.ค. - มี.ค.</strong> — ผักใบเขียว · แตงโม · มะม่วง (ต้นฤดู)</li>
  <li><strong>เม.ย. - มิ.ย.</strong> — มะม่วง · มังคุด · ทุเรียน · ผักหวานป่า</li>
  <li><strong>ก.ค. - ก.ย.</strong> — ลำไย · เงาะ · ลองกอง · ผักบุ้งจีน</li>
  <li><strong>ต.ค. - ธ.ค.</strong> — ส้ม · ฝรั่ง · ผักกาดทุกชนิด · ฟักทอง</li>
</ul>"
            })),
            new(CmsBlockType.Testimonials, J(new {
                headline = "สมาชิกของเราบอก",
                testimonials = new[] {
                    new { quote = "ผักสดมาก ใบยังกรอบ ลูกชอบกินผักขึ้นเยอะ", author = "คุณแม่เนย", role = "สมาชิก 2 ปี" },
                    new { quote = "ได้ลองผักที่ไม่เคยทาน เปิดโลกอาหารใหม่", author = "คุณนุ่น", role = "สมาชิก Family Box" },
                    new { quote = "ไปเยี่ยมฟาร์มจริง สบายใจมาก รู้แหล่งที่มา", author = "คุณเป้", role = "Premium Member" },
                    new { quote = "Sustainable + อร่อย · จ่ายเงินให้เกษตรกรตรง ๆ", author = "คุณอาย", role = "สมาชิก 3 ปี" }
                }
            })),
            new(CmsBlockType.ProductGrid, J(new { headline = "🍎 ของฝากจากฟาร์ม", limit = 8 })),
            new(CmsBlockType.Faq, J(new {
                headline = "คำถามที่พบบ่อย",
                items = new[] {
                    new { q = "CSA คืออะไร?", a = "Community Supported Agriculture — สมัครสมาชิกล่วงหน้า ฟาร์มเก็บผักให้ทุกสัปดาห์" },
                    new { q = "เปลี่ยนผักในกล่องได้ไหม?", a = "Premium member ปรับได้ · Single/Family ฟาร์มจัดให้ตามฤดูกาล" },
                    new { q = "Pause สมาชิกได้ไหม?", a = "ได้ pause ได้ไม่เกิน 4 สัปดาห์/ปี เช่น ไปเที่ยว" },
                    new { q = "ส่งครอบคลุมพื้นที่ไหน?", a = "กรุงเทพ · นนทบุรี · สมุทรปราการ · ปทุมธานี" },
                    new { q = "ออร์แกนิกจริงไหม?", a = "มีใบรับรอง Organic Thailand · IFOAM · ตรวจซ้ำทุก 6 เดือน" }
                }
            })),
            new(CmsBlockType.CallToAction, J(new {
                headline = "เริ่มกินผักดี ๆ ตั้งแต่สัปดาห์นี้",
                subheadline = "สมัครออนไลน์ · เก็บผักจากแปลงให้คุณทุกสัปดาห์",
                ctaText = "สมัครสมาชิก",
                ctaUrl = "/csa"
            }))
        }),

        (new("สมัครสมาชิก CSA", "csa", PageType.Standard, "สมัครรับกล่องผัก CSA"), new() {
            new(CmsBlockType.Hero, J(new {
                headline = "สมัครสมาชิก CSA",
                subheadline = "เลือกขนาดกล่อง · เลือกวันส่ง · เริ่มได้สัปดาห์หน้า"
            })),
            new(CmsBlockType.PricingTable, J(new {
                headline = "เลือกแพ็กเกจ",
                plans = new[] {
                    new { name = "Single", price = "฿390/สัปดาห์", features = new[] { "ผัก 5-6 ชนิด · 1.5-2 kg" } },
                    new { name = "Family", price = "฿690/สัปดาห์", features = new[] { "ผัก 8-10 ชนิด · 3-4 kg + ไข่" } },
                    new { name = "Premium", price = "฿1,290/สัปดาห์", features = new[] { "ผัก 12+ ชนิด + ผลไม้ + น้ำผึ้ง" } }
                }
            })),
            new(CmsBlockType.ContactForm, J(new {
                headline = "สมัครรับผักจากฟาร์ม (CSA)",
                submitText = "ส่งใบสมัคร",
                emailTo = "",
                leadType = "Subscription",
                phoneRequired = true,
                extraFields = new object[] {
                    new { name = "box_size", label = "ขนาด box", type = "select", options = new[] { "Single (1 คน)", "Family (3-4 คน)", "Premium (4-6 คน)" }, required = true },
                    new { name = "delivery_frequency", label = "ความถี่ในการส่ง", type = "select", options = new[] { "ทุกสัปดาห์", "ทุก 2 สัปดาห์", "เดือนละครั้ง" } },
                    new { name = "delivery_address", label = "ที่อยู่จัดส่ง", type = "textarea", required = true },
                    new { name = "allergies", label = "แพ้อาหาร / ไม่กินผักชนิดใด", type = "text" }
                }
            }))
        }),

        (new("สินค้าฟาร์ม", "products", PageType.Category, "ผัก ผลไม้ ไข่ น้ำผึ้ง"), new() {
            new(CmsBlockType.Hero, J(new { headline = "สินค้าจากฟาร์ม", subheadline = "ซื้อแบบครั้งเดียวก็ได้ ไม่ต้องสมัครสมาชิก" })),
            new(CmsBlockType.ProductGrid, J(new { limit = 24 }))
        }),

        (new("เยี่ยมฟาร์ม", "farm-visit", PageType.Standard, "เยี่ยมฟาร์ม · ทัวร์เกษตร"), new() {
            new(CmsBlockType.Hero, J(new { headline = "เยี่ยมฟาร์มกัน", subheadline = "ทัวร์ฟาร์ม + เก็บผักด้วยตัวเอง + อาหารกลางวัน" })),
            new(CmsBlockType.RichText, J(new {
                content = @"<h2>🚜 Farm Tour Experience</h2>
<ul>
  <li><strong>09:00</strong> — ต้อนรับ · ทัวร์แปลงผัก · เก็บผักเอง</li>
  <li><strong>11:00</strong> — เรียนทำปุ๋ยอินทรีย์ · ปลูกเมล็ดให้ตัวเอง</li>
  <li><strong>12:30</strong> — อาหารกลางวัน Farm-to-table</li>
  <li><strong>14:00</strong> — เยี่ยมเล้าไก่อินทรีย์ · บ้านผึ้ง</li>
  <li><strong>15:30</strong> — ของฝากกลับบ้าน (ผัก + ไข่)</li>
</ul>
<p>ราคา 990 บาท/คน · เด็ก ฿490 · จองล่วงหน้า 3 วัน</p>"
            })),
            new(CmsBlockType.ContactForm, J(new { headline = "จอง Farm Tour", submitText = "ส่งคำขอจอง", emailTo = "" }))
        }),

        (new("ติดต่อ", "contact", PageType.Standard, "ติดต่อฟาร์ม"), new() {
            new(CmsBlockType.Hero, J(new { headline = "ติดต่อฟาร์ม", subheadline = "ฟาร์มเปิด จ-ส 8:00-17:00" })),
            new(CmsBlockType.RichText, J(new {
                content = @"<p>📞 โทร: 081-XXX-XXXX · LINE: @yourfarm<br>
📧 hello@farm.com<br>
📍 ฟาร์ม 30 ไร่ · นครปฐม / ราชบุรี (กรุณาแก้ไขที่อยู่)</p>"
            })),
            new(CmsBlockType.ContactForm, J(new { headline = "ส่งข้อความ", submitText = "ส่ง", emailTo = "" })),
            new(CmsBlockType.Map, J(new { address = "นครปฐม ประเทศไทย" }))
        })
    };

    // ============================================================
    // BEAUTY / SPA / SALON — Sothys / Divana / Let's Relax style
    // ============================================================
    private static List<(PageMeta, List<BlockMeta>)> BeautyPlan() => new()
    {
        (new("หน้าหลัก", "home", PageType.Landing, "สปา ทำเล็บ นวด ทรีตเมนต์ ครบในที่เดียว"), new() {
            new(CmsBlockType.Hero, J(new {
                headline = "ผ่อนคลายเหมือนหลุดจากความวุ่นวาย",
                subheadline = "บริการสปา · นวดแผนไทย · ทรีตเมนต์ · ทำเล็บ — ทีมงานมืออาชีพ บรรยากาศหรูหรา",
                ctaText = "จองคิว",
                ctaUrl = "/booking"
            })),
            new(CmsBlockType.RichText, J(new {
                content = @"<h2>🌸 ทำไมต้องเรา</h2>
<ul>
  <li>💆 <strong>นักบำบัดมืออาชีพ</strong> — ผ่านการอบรม · ประสบการณ์ 5+ ปี</li>
  <li>🌿 <strong>ผลิตภัณฑ์พรีเมียม</strong> — Sothys / Aroma Bali · ออร์แกนิก</li>
  <li>🛁 <strong>ห้องส่วนตัว</strong> — ห้อง single / double พร้อมห้องน้ำในตัว</li>
  <li>🕯️ <strong>บรรยากาศหรู</strong> — อโรมา · ดนตรี · แสงผ่อนคลาย</li>
  <li>🅿️ <strong>ที่จอดรถฟรี</strong> · 🚿 อาบน้ำหลังนวด · ☕ ชาสมุนไพรฟรี</li>
  <li>📅 <strong>จองออนไลน์</strong> — ยืนยันคิวใน 30 นาที</li>
</ul>"
            })),
            new(CmsBlockType.RichText, J(new {
                content = @"<h2>💆 บริการของเรา</h2>
<ul>
  <li><strong>นวดแผนไทย</strong> — 60 นาที 800 บาท · 90 นาที 1,100 บาท</li>
  <li><strong>นวดน้ำมันหอมระเหย</strong> — 60 นาที 1,200 บาท · 90 นาที 1,600 บาท</li>
  <li><strong>นวดหินร้อน</strong> — 90 นาที 1,800 บาท</li>
  <li><strong>Facial Treatment</strong> — 90 นาที 1,500 บาท</li>
  <li><strong>ขัดผิวกาย (Body Scrub)</strong> — 60 นาที 1,200 บาท</li>
  <li><strong>ทำเล็บมือ + ทาสี</strong> — 350 บาท · gel 750 บาท</li>
</ul>"
            })),
            new(CmsBlockType.PricingTable, J(new {
                headline = "💎 แพ็กเกจยอดนิยม",
                plans = new[] {
                    new { name = "Relax Package", price = "฿1,500",
                          features = new[] { "นวดสปาไทย 60 นาที", "ทำเล็บมือ", "ชาสมุนไพร + ของว่าง", "ใช้ได้ภายใน 30 วัน" } },
                    new { name = "Luxury Package", price = "฿2,800",
                          features = new[] { "นวดน้ำมัน 90 นาที", "Facial Treatment", "ทำเล็บมือ + เท้า", "ชา + ของว่าง", "ห้องส่วนตัว" } },
                    new { name = "Couple Package", price = "฿4,500",
                          features = new[] { "นวดคู่ 90 นาที", "ห้อง VIP suite", "อาหารกลางวันคู่", "แชมเปญ 1 ขวด", "ของขวัญกลับบ้าน" } }
                }
            })),
            new(CmsBlockType.Testimonials, J(new {
                headline = "ลูกค้าพึงพอใจ",
                testimonials = new[] {
                    new { quote = "นวดดีมาก หายปวดหลังเลย กลับมานวดเดือนละครั้ง", author = "คุณเอ จ.", role = "ลูกค้าประจำ" },
                    new { quote = "บรรยากาศสบายมาก เหมือนหลุดไปอีกโลก", author = "คุณบี ว.", role = "Wongnai 5 ดาว" },
                    new { quote = "ทีมงานดูแลดี เป็นกันเอง ทำให้รู้สึกพิเศษ", author = "คุณซี พ.", role = "Couple Package" },
                    new { quote = "Facial เห็นผลตั้งแต่ครั้งแรก ผิวสว่างขึ้น", author = "คุณดี ส.", role = "Beauty Member" }
                }
            })),
            new(CmsBlockType.RichText, J(new {
                content = @"<h2>📅 ขั้นตอนการใช้บริการ</h2>
<ol>
  <li><strong>1. จองคิว</strong> — ออนไลน์ / LINE / โทร · เลือกบริการ + เวลา</li>
  <li><strong>2. ยืนยัน</strong> — เจ้าหน้าที่ติดต่อยืนยันใน 30 นาที</li>
  <li><strong>3. มาถึงร้าน</strong> — ก่อนเวลา 10 นาที · เปลี่ยนชุด · ดื่มชา</li>
  <li><strong>4. รับบริการ</strong> — เลือกความแรง · กลิ่นน้ำมัน · ดนตรี</li>
  <li><strong>5. ผ่อนคลาย</strong> — ดื่มชาหลังนวด · ฟรี locker · ห้องอาบน้ำ</li>
</ol>"
            })),
            new(CmsBlockType.Faq, J(new {
                headline = "คำถามที่พบบ่อย",
                items = new[] {
                    new { q = "ต้องจองล่วงหน้าไหม?", a = "แนะนำให้จอง 1-2 วันล่วงหน้า เสาร์-อาทิตย์อาจเต็มเร็ว" },
                    new { q = "คนท้องนวดได้ไหม?", a = "ได้ มีโปรแกรมนวด Prenatal เฉพาะ (อายุครรภ์ 4 เดือนขึ้นไป)" },
                    new { q = "ซื้อ voucher ของขวัญได้ไหม?", a = "ได้ มี gift voucher ออกใบให้ ใช้ได้ภายใน 1 ปี" },
                    new { q = "ยกเลิกได้เมื่อไหร่?", a = "ยกเลิกฟรีก่อนเวลานัด 4 ชม. · หลังจากนั้นคิดค่าธรรมเนียม 50%" },
                    new { q = "Pet friendly ไหม?", a = "ไม่อนุญาต ขออภัยเพื่อความสงบของลูกค้าท่านอื่น" }
                }
            })),
            new(CmsBlockType.CallToAction, J(new {
                headline = "จองคิวล่วงหน้า รับส่วนลด 10%",
                subheadline = "เฉพาะลูกค้าใหม่ จองผ่านเว็บไซต์",
                ctaText = "จองตอนนี้",
                ctaUrl = "/booking"
            }))
        }),

        (new("บริการ", "services", PageType.Standard, "บริการสปาและความงาม"), new() {
            new(CmsBlockType.Hero, J(new { headline = "บริการของเรา", subheadline = "เลือกบริการที่เหมาะกับคุณ" })),
            new(CmsBlockType.RichText, J(new {
                content = @"<h2>หมวดบริการ</h2>
<ul>
  <li>💆 <strong>นวด</strong> — ไทย · น้ำมัน · หินร้อน · ฟุต · Prenatal</li>
  <li>✨ <strong>Facial</strong> — Deep cleansing · Anti-aging · Brightening · Acne</li>
  <li>🛁 <strong>Body</strong> — Scrub · Wrap · Aromatherapy · Slimming</li>
  <li>💅 <strong>Nail</strong> — Manicure · Pedicure · Gel · Nail Art</li>
  <li>👁️ <strong>Eyelash</strong> — Extensions · Lift · Tint</li>
</ul>"
            })),
            new(CmsBlockType.PricingTable, J(new {
                headline = "ราคาเต็มรายการ",
                plans = new[] {
                    new { name = "Massage", price = "เริ่ม ฿800",
                          features = new[] { "นวดไทย 60 นาที", "นวดน้ำมัน 60 นาที", "นวดหินร้อน", "นวด Prenatal" } },
                    new { name = "Facial", price = "เริ่ม ฿1,500",
                          features = new[] { "Deep Cleansing", "Anti-aging", "Acne Treatment", "Brightening" } },
                    new { name = "Body / Nail", price = "เริ่ม ฿350",
                          features = new[] { "Body Scrub", "Manicure", "Pedicure", "Gel Polish" } }
                }
            }))
        }),

        (new("จองคิว", "booking", PageType.Standard, "จองคิวล่วงหน้า"), new() {
            new(CmsBlockType.Hero, J(new {
                headline = "จองคิวล่วงหน้า",
                subheadline = "เลือกบริการและเวลาที่สะดวก เราจะติดต่อยืนยัน"
            })),
            new(CmsBlockType.BookingCalendar, "{}"),
            new(CmsBlockType.ContactForm, J(new { headline = "หรือกรอกฟอร์ม", submitText = "ส่งคำขอ", emailTo = "" }))
        }),

        (new("เกี่ยวกับเรา", "about", PageType.Standard, "เรื่องราวสปา"), new() {
            new(CmsBlockType.Hero, J(new { headline = "เกี่ยวกับเรา", subheadline = "บริการสปาระดับโรงแรม 5 ดาว ในราคาเป็นมิตร" })),
            new(CmsBlockType.RichText, J(new {
                content = @"<h2>เริ่มต้นด้วยความตั้งใจ</h2>
<p>เปิดบริการตั้งแต่ปี 2558 · 9 ปีของการดูแลลูกค้าด้วยใจ<br>
ทีมนักบำบัด 12 คน · ห้องนวด 8 ห้อง · ห้องคู่ 2 ห้อง · ห้อง VIP 1 ห้อง</p>
<h2>มาตรฐาน + การรับรอง</h2>
<ul>
  <li>✓ ใบอนุญาตประกอบกิจการสปา กระทรวงสาธารณสุข</li>
  <li>✓ นักบำบัดผ่านการอบรมจากกรมพัฒนาฝีมือแรงงาน</li>
  <li>✓ Member Thai Spa Association</li>
  <li>✓ Wongnai User's Choice 2566</li>
</ul>"
            })),
            new(CmsBlockType.Testimonials, J(new {
                headline = "รางวัล",
                testimonials = new[] {
                    new { quote = "TOP 10 Best Spa in Bangkok", author = "BK Magazine 2566", role = "" },
                    new { quote = "User's Choice Award", author = "Wongnai 2566", role = "" }
                }
            }))
        }),

        (new("ติดต่อเรา", "contact", PageType.Standard, "ที่ตั้งร้าน เปิด-ปิด"), new() {
            new(CmsBlockType.Hero, J(new { headline = "ติดต่อเรา", subheadline = "เปิดทุกวัน 10:00 - 22:00 · ที่จอดรถฟรี" })),
            new(CmsBlockType.RichText, J(new {
                content = @"<h2>📍 ที่ตั้ง</h2>
<p>กรุณาแก้ไขที่อยู่ของคุณ<br>
📞 02-XXX-XXXX · LINE: @yourspa<br>
📧 hello@spa.com</p>"
            })),
            new(CmsBlockType.ContactForm, J(new { headline = "ส่งข้อความ", submitText = "ส่ง", emailTo = "" })),
            new(CmsBlockType.Map, J(new { address = "กรุงเทพมหานคร ประเทศไทย" }))
        })
    };

    // ============================================================
    // HEALTHCARE — Mayo Clinic / Bumrungrad style
    // ============================================================
    private static List<(PageMeta, List<BlockMeta>)> HealthcarePlan() => new()
    {
        (new("หน้าหลัก", "home", PageType.Landing, "คลินิก / โรงพยาบาล แพทย์เฉพาะทาง"), new() {
            new(CmsBlockType.Hero, J(new {
                headline = "ดูแลสุขภาพคุณอย่างมืออาชีพ",
                subheadline = "แพทย์ผู้เชี่ยวชาญเฉพาะทาง · เครื่องมือทันสมัย · บริการครบวงจร 24 ชม.",
                ctaText = "นัดหมายแพทย์",
                ctaUrl = "/appointment"
            })),
            new(CmsBlockType.RichText, J(new {
                content = @"<h2>🏥 ทำไมต้องเลือกเรา</h2>
<ul>
  <li>👨‍⚕️ <strong>แพทย์เฉพาะทาง 30+ คน</strong> — ผ่านการฝึกอบรมในและต่างประเทศ</li>
  <li>🏥 <strong>เครื่องมือทันสมัย</strong> — MRI · CT · X-ray digital · Lab ภายในวัน</li>
  <li>🌐 <strong>หลายภาษา</strong> — ไทย · อังกฤษ · จีน · ญี่ปุ่น · อาหรับ</li>
  <li>🚑 <strong>ฉุกเฉิน 24 ชม.</strong> — ห้องฉุกเฉิน · รถพยาบาล</li>
  <li>💳 <strong>รับประกันทุกบริษัท</strong> — เคลมตรง ไม่ต้องสำรอง</li>
  <li>📱 <strong>นัดออนไลน์</strong> — เลือกแพทย์ · เลือกเวลาเอง</li>
</ul>"
            })),
            new(CmsBlockType.RichText, J(new {
                content = @"<h2>🩺 ศูนย์การแพทย์เฉพาะทาง</h2>
<ul>
  <li><strong>อายุรกรรม</strong> — ความดัน · เบาหวาน · ระบบทางเดินอาหาร</li>
  <li><strong>ศัลยกรรม</strong> — ผ่าตัดทั่วไป · ส่องกล้อง · Laparoscopic</li>
  <li><strong>กระดูกและข้อ</strong> — ผ่าตัด · กายภาพบำบัด · เปลี่ยนข้อ</li>
  <li><strong>หัวใจ</strong> — ตรวจคัดกรอง · สวนหัวใจ · ผ่าตัด</li>
  <li><strong>สูตินรีเวช</strong> — ฝากครรภ์ · คลอด · ผ่าตัด</li>
  <li><strong>กุมารเวช</strong> — เด็กแรกเกิด - 15 ปี · วัคซีน</li>
  <li><strong>ทันตกรรม</strong> — ถอน · อุด · จัดฟัน · รากเทียม</li>
  <li><strong>ผิวหนัง / ความงาม</strong> — เลเซอร์ · ฉีดฟิลเลอร์ · botox</li>
</ul>"
            })),
            new(CmsBlockType.PricingTable, J(new {
                headline = "💰 แพ็กเกจตรวจสุขภาพ",
                plans = new[] {
                    new { name = "Basic Checkup", price = "฿2,500",
                          features = new[] { "ตรวจร่างกายทั่วไป", "ตรวจเลือดพื้นฐาน", "X-ray ทรวงอก", "EKG", "พบแพทย์ + รายงาน" } },
                    new { name = "Comprehensive", price = "฿6,900",
                          features = new[] { "Basic ครบ + Ultrasound ช่องท้อง", "ตรวจหัวใจ Stress Test", "ตรวจมะเร็งเบื้องต้น", "ตรวจสายตา + การได้ยิน" } },
                    new { name = "Executive", price = "฿15,900",
                          features = new[] { "Comprehensive ครบ + MRI สมอง", "Colonoscopy / Gastroscopy", "ตรวจกระดูกพรุน", "แพ็กเกจอาหารกลางวัน + ที่ปรึกษา" } }
                }
            })),
            new(CmsBlockType.Testimonials, J(new {
                headline = "เสียงจากผู้ป่วย",
                testimonials = new[] {
                    new { quote = "หมอใจดี อธิบายละเอียด ไม่รีบ ทำให้เข้าใจอาการ", author = "คุณรุ่ง", role = "ผู้ป่วยอายุรกรรม" },
                    new { quote = "ผ่าตัดเรียบร้อย ฟื้นตัวเร็ว พยาบาลดูแลใส่ใจ", author = "คุณกวี", role = "ผ่าตัดส่องกล้อง" },
                    new { quote = "นัดง่ายผ่านแอป ไม่ต้องรอนาน", author = "คุณนิดา", role = "ผู้ใช้บริการประจำ" },
                    new { quote = "บริการดี เคลมตรงประกัน ไม่ต้องสำรอง", author = "Mr. James", role = "Expat patient" }
                }
            })),
            new(CmsBlockType.Faq, J(new {
                headline = "คำถามที่พบบ่อย",
                items = new[] {
                    new { q = "นัดหมายล่วงหน้ากี่วัน?", a = "แนะนำนัด 3-7 วัน · ฉุกเฉินเดินเข้าได้ทันที 24 ชม." },
                    new { q = "รับประกันสุขภาพอะไรบ้าง?", a = "AIA · เมืองไทย · BUPA · Allianz · ทุกบริษัทใหญ่ในไทย" },
                    new { q = "พูดอังกฤษได้ไหม?", a = "ได้ มีล่ามฟรี ไทย-อังกฤษ-จีน-ญี่ปุ่น" },
                    new { q = "ที่จอดรถ?", a = "อาคารจอดรถ 500 คัน · 1 ชม.แรกฟรี · ผู้ป่วยฟรีทั้งวัน" },
                    new { q = "ผลตรวจรับเมื่อไหร่?", a = "Lab พื้นฐานใน 2 ชม. · ผลละเอียดส่งทางอีเมล/แอป 1-3 วัน" }
                }
            })),
            new(CmsBlockType.CallToAction, J(new {
                headline = "นัดหมายล่วงหน้าผ่านเว็บไซต์",
                subheadline = "ลดเวลารอคิว · เลือกแพทย์ + เวลาที่สะดวก",
                ctaText = "นัดหมายเลย",
                ctaUrl = "/appointment"
            }))
        }),

        (new("แพทย์ของเรา", "doctors", PageType.Standard, "รายชื่อแพทย์เฉพาะทาง"), new() {
            new(CmsBlockType.Hero, J(new { headline = "ทีมแพทย์ของเรา", subheadline = "แพทย์เฉพาะทาง 30+ คน · ผ่านการฝึกอบรมในและต่างประเทศ" })),
            new(CmsBlockType.RichText, J(new {
                content = @"<h2>👨‍⚕️ แพทย์ตามแผนก</h2>
<p>(แก้ไขรายชื่อแพทย์ของคลินิก/รพ. ของคุณตรงนี้)</p>
<ul>
  <li><strong>นพ. สมชาย ใจดี</strong> — อายุรกรรม · แพทยศาสตร์บัณฑิต จุฬาฯ · ประสบการณ์ 20 ปี</li>
  <li><strong>พญ. สุดา รักดี</strong> — สูตินรีเวช · Royal College of Obstetricians UK</li>
  <li><strong>นพ. ธีระ มีฝีมือ</strong> — ศัลยกรรม · MD Harvard Medical · 15 ปี</li>
  <li><strong>พญ. นภา ใสใส</strong> — ผิวหนัง · Dermatology Board · 12 ปี</li>
</ul>"
            }))
        }),

        (new("แพ็กเกจ + บริการ", "services", PageType.Standard, "แพ็กเกจตรวจสุขภาพ"), new() {
            new(CmsBlockType.Hero, J(new { headline = "แพ็กเกจของเรา" })),
            new(CmsBlockType.RichText, J(new {
                content = @"<h2>🩺 บริการตรวจรักษา</h2>
<ul>
  <li><strong>ตรวจสุขภาพประจำปี</strong> — เริ่มต้น 2,500 บาท</li>
  <li><strong>ตรวจคัดกรองโรคหัวใจ</strong> — 4,500 บาท</li>
  <li><strong>ตรวจคัดกรองมะเร็ง</strong> — 6,500-12,000 บาท</li>
  <li><strong>วัคซีนไข้หวัดใหญ่</strong> — 600 บาท</li>
  <li><strong>วัคซีน HPV</strong> — 1,800 บาท/เข็ม (3 เข็ม)</li>
  <li><strong>วัคซีนไวรัสตับอักเสบ B</strong> — 450 บาท/เข็ม</li>
  <li><strong>Health Screening Premarital</strong> — 3,500 บาท/คู่</li>
</ul>"
            })),
            new(CmsBlockType.PricingTable, J(new {
                headline = "แพ็กเกจตรวจสุขภาพ",
                plans = new[] {
                    new { name = "Basic", price = "฿2,500", features = new[] { "เหมาะสำหรับอายุ < 35", "ตรวจพื้นฐาน 25 รายการ" } },
                    new { name = "Comprehensive", price = "฿6,900", features = new[] { "เหมาะสำหรับอายุ 35-50", "50+ รายการ + Ultrasound" } },
                    new { name = "Executive", price = "฿15,900", features = new[] { "เหมาะสำหรับอายุ 50+", "ครบเครื่อง + MRI + Endoscopy" } }
                }
            }))
        }),

        (new("นัดหมายแพทย์", "appointment", PageType.Standard, "นัดหมายล่วงหน้า"), new() {
            new(CmsBlockType.Hero, J(new {
                headline = "นัดหมายแพทย์",
                subheadline = "กรอกข้อมูลเบื้องต้น เจ้าหน้าที่ติดต่อกลับยืนยันคิวภายใน 1 ชั่วโมง"
            })),
            new(CmsBlockType.RichText, J(new {
                content = @"<h2>📋 ข้อมูลที่ต้องเตรียม</h2>
<ul>
  <li>บัตรประชาชน / passport</li>
  <li>บัตรประกันสุขภาพ (ถ้ามี)</li>
  <li>ประวัติการรักษาเดิม / ยาที่กินอยู่</li>
  <li>มาก่อนเวลา 30 นาที (ผู้ป่วยใหม่)</li>
</ul>"
            })),
            new(CmsBlockType.ContactForm, J(new { headline = "แจ้งนัดหมาย", submitText = "ส่งคำขอนัด", emailTo = "" })),
            new(CmsBlockType.BookingCalendar, "{}")
        }),

        (new("ติดต่อ", "contact", PageType.Standard, "ที่อยู่คลินิก เปิด-ปิด"), new() {
            new(CmsBlockType.Hero, J(new { headline = "ติดต่อเรา", subheadline = "เปิด 24 ชม. · ห้องฉุกเฉินตลอดเวลา" })),
            new(CmsBlockType.RichText, J(new {
                content = @"<h2>📍 ที่ตั้ง · 🕐 เปิด 24 ชั่วโมง</h2>
<p>📞 OPD: 02-XXX-XXXX (จ-ส 7:00-20:00)<br>
🚨 ฉุกเฉิน 24 ชม.: 086-XXX-XXXX<br>
📧 contact@hospital.com<br>
📱 LINE: @hospital</p>"
            })),
            new(CmsBlockType.ContactForm, J(new { headline = "ส่งข้อความ", submitText = "ส่ง", emailTo = "" })),
            new(CmsBlockType.Map, J(new { address = "กรุงเทพมหานคร ประเทศไทย" }))
        })
    };

    // ============================================================
    // SERVICE — Consulting / Agency landing
    // ============================================================
    private static List<(PageMeta, List<BlockMeta>)> ServicePlan() => new()
    {
        (new("หน้าหลัก", "home", PageType.Landing, "บริการที่ปรึกษา / เอเจนซี่ครบวงจร"), new() {
            new(CmsBlockType.Hero, J(new {
                headline = "ที่ปรึกษามืออาชีพ เพื่อความสำเร็จของธุรกิจคุณ",
                subheadline = "บริการให้คำปรึกษา + ดำเนินการครบวงจร · เน้นผลลัพธ์ที่จับต้องได้",
                ctaText = "ปรึกษาฟรี",
                ctaUrl = "/contact"
            })),
            new(CmsBlockType.RichText, J(new {
                content = @"<h2>🎯 ทำไมต้องเลือกเรา</h2>
<ul>
  <li>🏆 <strong>ผลงาน 200+ โครงการ</strong> — ตั้งแต่ SME ถึงบริษัทมหาชน</li>
  <li>📈 <strong>เน้นผลลัพธ์</strong> — KPI ชัดเจน · รายงานทุกเดือน</li>
  <li>👥 <strong>ทีมงาน 25+ ผู้เชี่ยวชาญ</strong> — ไม่ใช่แค่ที่ปรึกษา ลงมือทำให้</li>
  <li>💯 <strong>รับประกันความพอใจ</strong> — ไม่บรรลุเป้า คืนเงิน 100%</li>
  <li>🌐 <strong>ครอบคลุมทุก function</strong> — Marketing · Sales · Finance · HR</li>
  <li>🤝 <strong>เป็นมิตร โปร่งใส</strong> — ราคาแจ้งล่วงหน้า ไม่มีค่าแฝง</li>
</ul>"
            })),
            new(CmsBlockType.RichText, J(new {
                content = @"<h2>💼 บริการของเรา</h2>
<ul>
  <li><strong>Business Strategy</strong> — วางแผนธุรกิจ · เข้าตลาดใหม่ · ขยายสาขา</li>
  <li><strong>Digital Marketing</strong> — Facebook / Google Ads · SEO · Content</li>
  <li><strong>Sales Optimization</strong> — CRM · Lead generation · ปิดการขาย</li>
  <li><strong>Operations & Process</strong> — Lean · ลดต้นทุน · เพิ่มประสิทธิภาพ</li>
  <li><strong>HR & Talent</strong> — Recruit · Train · Performance system</li>
</ul>"
            })),
            new(CmsBlockType.PricingTable, J(new {
                headline = "แพ็กเกจที่ปรึกษา",
                plans = new[] {
                    new { name = "Consultation", price = "฿15,000/เดือน",
                          features = new[] { "ที่ปรึกษา 8 ชม./เดือน", "ประชุม Online", "Action plan รายเดือน", "เหมาะ SME เริ่มต้น" } },
                    new { name = "Growth", price = "฿45,000/เดือน",
                          features = new[] { "ทีมงาน 2 คน full-time", "ลงมือทำให้", "รายงานรายสัปดาห์", "On-site visit เดือนละ 2 ครั้ง" } },
                    new { name = "Enterprise", price = "Custom",
                          features = new[] { "ทีมงาน 5+ คน เฉพาะคุณ", "Account Director", "KPI Guarantee", "Quarterly Business Review" } }
                }
            })),
            new(CmsBlockType.Testimonials, J(new {
                headline = "ลูกค้าที่เราช่วยเติบโต",
                testimonials = new[] {
                    new { quote = "ยอดขายโตขึ้น 3 เท่าใน 6 เดือน ทีมงานมืออาชีพมาก", author = "คุณวิชัย", role = "CEO บริษัทค้าปลีก" },
                    new { quote = "ลดต้นทุนได้ 30% โดยไม่กระทบคุณภาพ", author = "คุณนภา", role = "MD โรงงานผลิต" },
                    new { quote = "ทำให้เห็นภาพชัด ตัดสินใจได้เร็ว", author = "คุณกิตติ", role = "เจ้าของ SME" },
                    new { quote = "ROI กลับมา 5 เท่าของค่าที่ปรึกษา", author = "คุณภัทร", role = "Founder Startup" }
                }
            })),
            new(CmsBlockType.RichText, J(new {
                content = @"<h2>🔄 วิธีทำงานของเรา</h2>
<ol>
  <li><strong>1. Discovery</strong> — ฟังปัญหา · เข้าใจธุรกิจ · ปรึกษาฟรี 1 ชม.</li>
  <li><strong>2. Diagnosis</strong> — วิเคราะห์ + เสนอแนวทาง (1-2 สัปดาห์)</li>
  <li><strong>3. Proposal</strong> — แผนงาน · timeline · ราคา ชัดเจน</li>
  <li><strong>4. Execute</strong> — ลงมือทำ · รายงานทุกสัปดาห์</li>
  <li><strong>5. Optimize</strong> — วัดผล · ปรับ · ทำซ้ำให้ดีขึ้น</li>
</ol>"
            })),
            new(CmsBlockType.Faq, J(new {
                headline = "คำถามที่พบบ่อย",
                items = new[] {
                    new { q = "ทำงานกับ SME เล็ก ๆ ไหม?", a = "ทำ มีแพ็กเกจ Consultation เริ่ม 15,000/เดือน เหมาะ SME" },
                    new { q = "เซ็น NDA ไหม?", a = "เซ็นทุกโครงการ ข้อมูลลูกค้าเป็นความลับ 100%" },
                    new { q = "Onsite ได้ไหม?", a = "ได้ Growth + Enterprise มี onsite visit · ต่างจังหวัดได้" },
                    new { q = "เลิกสัญญาเมื่อไหร่?", a = "ไม่มีสัญญาผูกมัด แจ้งล่วงหน้า 30 วันยกเลิกได้" },
                    new { q = "วัดผลยังไง?", a = "ตั้ง KPI ร่วมกันก่อนเริ่ม · รายงานทุกเดือน + QBR" }
                }
            })),
            new(CmsBlockType.CallToAction, J(new {
                headline = "พร้อมยกระดับธุรกิจของคุณ?",
                subheadline = "ปรึกษาฟรี 1 ชั่วโมง ไม่มีข้อผูกมัด",
                ctaText = "นัดปรึกษา",
                ctaUrl = "/contact"
            }))
        }),

        (new("บริการ", "services", PageType.Standard, "บริการให้คำปรึกษาทั้งหมด"), new() {
            new(CmsBlockType.Hero, J(new { headline = "บริการของเรา" })),
            new(CmsBlockType.RichText, J(new {
                content = @"<h2>5 หมวดบริการหลัก</h2>
<ul>
  <li>📊 <strong>Strategy</strong></li>
  <li>📣 <strong>Marketing</strong></li>
  <li>💰 <strong>Sales</strong></li>
  <li>⚙️ <strong>Operations</strong></li>
  <li>👥 <strong>HR & Talent</strong></li>
</ul>"
            })),
            new(CmsBlockType.PricingTable, J(new {
                headline = "แพ็กเกจ",
                plans = new[] {
                    new { name = "Consultation", price = "฿15,000/ด.", features = new[] { "8 ชม./เดือน", "Online" } },
                    new { name = "Growth", price = "฿45,000/ด.", features = new[] { "Full-time 2 คน", "Onsite" } },
                    new { name = "Enterprise", price = "Custom", features = new[] { "5+ คน", "Account Director" } }
                }
            }))
        }),

        (new("ผลงาน", "case-studies", PageType.Standard, "ผลงาน case studies"), new() {
            new(CmsBlockType.Hero, J(new { headline = "Case Studies", subheadline = "ตัวอย่างโครงการที่เราทำสำเร็จ" })),
            new(CmsBlockType.RichText, J(new {
                content = @"<h2>โครงการเด่น</h2>
<ul>
  <li><strong>ร้านค้าปลีก A</strong> — ยอดขายโต 200% ใน 6 เดือน</li>
  <li><strong>โรงงาน B</strong> — ลดของเสีย 40% · เพิ่มกำลังผลิต 25%</li>
  <li><strong>SaaS Startup C</strong> — Funnel conversion เพิ่ม 3 เท่า</li>
  <li><strong>F&B Chain D</strong> — ขยายจาก 5 → 18 สาขา ใน 18 เดือน</li>
</ul>"
            }))
        }),

        (new("เกี่ยวกับเรา", "about", PageType.Standard, "ทีมงาน"), new() {
            new(CmsBlockType.Hero, J(new { headline = "ทีมเรา", subheadline = "25+ ผู้เชี่ยวชาญที่ผ่านงานบริษัทระดับโลก" })),
            new(CmsBlockType.RichText, J(new {
                content = @"<h2>ผู้ก่อตั้ง</h2>
<p>ทีมก่อตั้งมาจาก McKinsey · BCG · Google · Unilever<br>
รวมประสบการณ์ 60+ ปี ในตลาด SEA · จีน · ยุโรป</p>
<h2>วัฒนธรรมเรา</h2>
<ul>
  <li>🎯 Results over hours</li>
  <li>🔍 Truth over comfort</li>
  <li>🤝 Client success first</li>
</ul>"
            }))
        }),

        (new("ติดต่อ", "contact", PageType.Standard, "ติดต่อทีม"), new() {
            new(CmsBlockType.Hero, J(new { headline = "ปรึกษาฟรี 1 ชั่วโมง", subheadline = "ตอบกลับใน 1 วันทำการ" })),
            new(CmsBlockType.ContactForm, J(new {
                headline = "นัดปรึกษา / นัด demo",
                submitText = "ส่งคำขอนัด",
                emailTo = "",
                leadType = "Demo",
                askCompany = true,
                extraFields = new object[] {
                    new { name = "company_size", label = "ขนาดบริษัท", type = "select", options = new[] { "1-10 คน", "11-50 คน", "51-200 คน", "201+ คน" } },
                    new { name = "use_case", label = "วัตถุประสงค์ที่ต้องการใช้งาน", type = "textarea" },
                    new { name = "current_tool", label = "ปัจจุบันใช้ระบบ / เครื่องมืออะไรอยู่", type = "text" },
                    new { name = "preferred_time", label = "ช่วงเวลาที่สะดวก", type = "text" }
                }
            })),
            new(CmsBlockType.RichText, J(new { content = "<p>📞 02-XXX-XXXX · LINE: @consulting · 📧 hello@consulting.com</p>" })),
            new(CmsBlockType.Map, J(new { address = "กรุงเทพมหานคร ประเทศไทย" }))
        })
    };

    // ============================================================
    // FREELANCE — Portfolio-first / Dribbble-style
    // ============================================================
    private static List<(PageMeta, List<BlockMeta>)> FreelancePlan() => new()
    {
        (new("หน้าหลัก", "home", PageType.Landing, "ฟรีแลนซ์มืออาชีพ · ดูผลงาน · ติดต่อจ้าง"), new() {
            new(CmsBlockType.Hero, J(new {
                headline = "Hi, I'm a freelancer who makes great work.",
                subheadline = "Designer / Developer / Writer · 7 ปีในวงการ · 80+ โปรเจ็กต์สำเร็จ",
                ctaText = "ดูผลงาน",
                ctaUrl = "/portfolio"
            })),
            new(CmsBlockType.RichText, J(new {
                content = @"<h2>✨ ทำไมต้องจ้างผม/ดิฉัน</h2>
<ul>
  <li>🎨 <strong>คุณภาพระดับเอเจนซี่</strong> — เคยทำงานให้แบรนด์ใหญ่</li>
  <li>⚡ <strong>ส่งงานเร็ว ตรงเวลา</strong> — มีตารางชัดเจน · update ทุกอาทิตย์</li>
  <li>💬 <strong>คุยง่าย ไม่ทิ้งงาน</strong> — ตอบ LINE ใน 2 ชม. · ทำงานเป็นทีม</li>
  <li>💰 <strong>ราคายุติธรรม</strong> — ตามขอบเขตงาน ไม่เก็บแอบหลัง</li>
  <li>🔧 <strong>แก้ไขฟรี 3 ครั้ง</strong> — ภายใน scope ที่ตกลง</li>
  <li>📄 <strong>มีสัญญา + ใบเสร็จ</strong> — ออกใบกำกับภาษีได้</li>
</ul>"
            })),
            new(CmsBlockType.RichText, J(new {
                content = @"<h2>🎨 ผลงานเด่น</h2>
<p>(ใส่ภาพผลงาน 6-9 ชิ้นด้วย Gallery block ในแผง edit)</p>
<ul>
  <li><strong>Brand Identity</strong> — Logo + Brand book · บริษัท startup 3 แบรนด์</li>
  <li><strong>Mobile App UI</strong> — แอป e-commerce · 35 หน้า</li>
  <li><strong>Website Design</strong> — Corporate site · 12 หน้า + responsive</li>
  <li><strong>Marketing Material</strong> — Brochure · social media kit</li>
</ul>
<p><a href=""/portfolio"" class=""btn"">ดูผลงานทั้งหมด →</a></p>"
            })),
            new(CmsBlockType.PricingTable, J(new {
                headline = "💰 ราคางาน (เริ่มต้น)",
                plans = new[] {
                    new { name = "Small Project", price = "฿8,000",
                          features = new[] { "Logo / Banner / Single Page", "ส่งงาน 1 สัปดาห์", "แก้ 3 ครั้ง", "ไฟล์ source ครบ" } },
                    new { name = "Medium Project", price = "฿35,000",
                          features = new[] { "Website 5-10 หน้า / App MVP", "ส่งงาน 3-4 สัปดาห์", "แก้ 5 ครั้ง", "Hosting setup ฟรี" } },
                    new { name = "Large / Retainer", price = "฿80,000+",
                          features = new[] { "Full project / รายเดือน", "Dedicated 20+ ชม./สัปดาห์", "ที่ปรึกษาเฉพาะ", "ดูแลหลังส่งมอบ" } }
                }
            })),
            new(CmsBlockType.Testimonials, J(new {
                headline = "ลูกค้าที่เคยร่วมงาน",
                testimonials = new[] {
                    new { quote = "งานละเอียด สวย ส่งตรงเวลาเป๊ะ จ้างซ้ำแน่นอน", author = "คุณนัฐ", role = "Startup Founder" },
                    new { quote = "Communication ดีมาก คิดเองต่อยอดให้ด้วย", author = "คุณภัทร", role = "Marketing Manager" },
                    new { quote = "เคารพ deadline · ส่งของก่อนกำหนดด้วย", author = "Mr. John", role = "Singapore Client" },
                    new { quote = "ราคาคุ้มมาก คุณภาพเหนือคาด", author = "คุณบี", role = "SME Owner" }
                }
            })),
            new(CmsBlockType.RichText, J(new {
                content = @"<h2>🔄 ขั้นตอนการทำงาน</h2>
<ol>
  <li><strong>1. คุยขอบเขต</strong> — Brief 30 นาที · เข้าใจงานชัดเจน</li>
  <li><strong>2. ใบเสนอราคา</strong> — ราคา · timeline · scope ระบุชัด</li>
  <li><strong>3. มัดจำ 50%</strong> — เริ่มงานหลังโอน</li>
  <li><strong>4. ส่งงาน + รีวิว</strong> — อัปเดททุกอาทิตย์ · feedback ปรับ</li>
  <li><strong>5. ส่งมอบ + ชำระส่วนที่เหลือ</strong> — ไฟล์ต้นฉบับครบ</li>
</ol>"
            })),
            new(CmsBlockType.Faq, J(new {
                headline = "คำถามที่พบบ่อย",
                items = new[] {
                    new { q = "ทำงานนอกเวลาได้ไหม?", a = "ได้ คิดค่า rush 30% สำหรับงานด่วน" },
                    new { q = "เซ็น NDA ไหม?", a = "เซ็นได้ ขอ template มาเลย / ใช้ของเราก็ได้" },
                    new { q = "ออกใบกำกับภาษีไหม?", a = "ออกได้ ระบุ VAT 7% · มี ภพ.20" },
                    new { q = "ทำงานต่างประเทศได้?", a = "ได้ รับงานจากทั่ว ASEAN · จ่าย USD/SGD ได้" },
                    new { q = "ใช้เครื่องมืออะไรบ้าง?", a = "Figma · Adobe CC · Notion · Slack · GitHub" }
                }
            })),
            new(CmsBlockType.CallToAction, J(new {
                headline = "มีโปรเจ็กต์อยากปรึกษา?",
                subheadline = "Brief ฟรี 30 นาที · ตอบกลับใน 1 วัน",
                ctaText = "ติดต่อจ้างงาน",
                ctaUrl = "/contact"
            }))
        }),

        (new("ผลงาน", "portfolio", PageType.Standard, "ผลงานทั้งหมด"), new() {
            new(CmsBlockType.Hero, J(new { headline = "Portfolio", subheadline = "ผลงาน 80+ ชิ้น · 7 ปีในวงการ" })),
            new(CmsBlockType.Gallery, J(new {
                images = new[] {
                    new { url = "https://placehold.co/600x400?text=Project+1", alt = "Project 1" },
                    new { url = "https://placehold.co/600x400?text=Project+2", alt = "Project 2" },
                    new { url = "https://placehold.co/600x400?text=Project+3", alt = "Project 3" },
                    new { url = "https://placehold.co/600x400?text=Project+4", alt = "Project 4" },
                    new { url = "https://placehold.co/600x400?text=Project+5", alt = "Project 5" },
                    new { url = "https://placehold.co/600x400?text=Project+6", alt = "Project 6" }
                }
            })),
            new(CmsBlockType.RichText, J(new {
                content = @"<p style=""color:#94a3b8"">* แทนภาพด้วยผลงานจริงของคุณ ใช้ block Image / Gallery</p>"
            }))
        }),

        (new("บริการ", "services", PageType.Standard, "บริการรับงาน"), new() {
            new(CmsBlockType.Hero, J(new { headline = "บริการรับงาน" })),
            new(CmsBlockType.RichText, J(new {
                content = @"<h2>🛠️ รับงานประเภท</h2>
<ul>
  <li><strong>UI/UX Design</strong> — Web · App · Dashboard</li>
  <li><strong>Brand Identity</strong> — Logo · Brand book · Style guide</li>
  <li><strong>Frontend Dev</strong> — React · Next.js · TailwindCSS</li>
  <li><strong>Mobile Dev</strong> — React Native · Flutter</li>
  <li><strong>Content Writing</strong> — Blog · Website copy · SEO</li>
</ul>"
            })),
            new(CmsBlockType.PricingTable, J(new {
                headline = "ราคาเริ่มต้น",
                plans = new[] {
                    new { name = "Small", price = "฿8,000", features = new[] { "1 สัปดาห์", "Logo / Single page" } },
                    new { name = "Medium", price = "฿35,000", features = new[] { "3-4 สัปดาห์", "Website / App MVP" } },
                    new { name = "Retainer", price = "฿80,000+", features = new[] { "รายเดือน", "Dedicated time" } }
                }
            }))
        }),

        (new("เกี่ยวกับฉัน", "about", PageType.Standard, "เกี่ยวกับฟรีแลนซ์"), new() {
            new(CmsBlockType.Hero, J(new { headline = "About Me", subheadline = "7 ปีในวงการ · 80+ โปรเจ็กต์ · 50+ ลูกค้า" })),
            new(CmsBlockType.RichText, J(new {
                content = @"<h2>เรื่องของฉัน</h2>
<p>เริ่มต้นเป็น designer ที่เอเจนซี่ในกรุงเทพ · ต่อมา freelance เต็มตัวตั้งแต่ปี 2562<br>
ทำงานให้แบรนด์ทั้งเล็ก · กลาง · ใหญ่ ตั้งแต่ startup ถึง corporate</p>
<h2>เครื่องมือที่ใช้</h2>
<ul>
  <li>Design: Figma · Adobe CC · Sketch</li>
  <li>Dev: VS Code · GitHub · Vercel</li>
  <li>Comm: Slack · Notion · LINE</li>
</ul>
<h2>ลูกค้าที่เคยร่วมงาน</h2>
<p>SCB · Bumrungrad · Central · Wongnai · LINE Thailand · และ startup อีกมาก</p>"
            }))
        }),

        (new("ติดต่อจ้างงาน", "contact", PageType.Standard, "ติดต่อจ้างงาน · brief ฟรี"), new() {
            new(CmsBlockType.Hero, J(new { headline = "Let's work together", subheadline = "Brief ฟรี 30 นาที · ตอบกลับใน 1 วัน" })),
            new(CmsBlockType.ContactForm, J(new {
                headline = "ส่ง Brief โปรเจ็ค — ขอใบเสนอราคา",
                submitText = "ส่ง Brief",
                emailTo = "",
                leadType = "Quote",
                askCompany = true,
                extraFields = new object[] {
                    new { name = "project_type", label = "ประเภทงาน", type = "select",
                          options = new[] { "Web Design", "Logo / Branding", "Graphic Design", "Photography", "Copywriting", "Video", "อื่นๆ" }, required = true },
                    new { name = "budget_range", label = "งบประมาณ", type = "select",
                          options = new[] { "<฿5,000", "฿5,000-15,000", "฿15,000-50,000", "฿50,000-200,000", "฿200,000+" } },
                    new { name = "deadline", label = "deadline ที่ต้องการ", type = "date" },
                    new { name = "deliverables", label = "ผลลัพธ์ที่ต้องการ", type = "textarea" }
                }
            })),
            new(CmsBlockType.RichText, J(new {
                content = @"<p>📧 hello@freelancer.com · 📱 LINE: @freelancer<br>
🌐 Behance · Dribbble · GitHub: @yourhandle</p>"
            })),
            new(CmsBlockType.Map, J(new { address = "กรุงเทพมหานคร ประเทศไทย" }))
        })
    };

    // ============================================================
    // CONSTRUCTION — Sansiri / Pruksa contractor style
    // ============================================================
    private static List<(PageMeta, List<BlockMeta>)> ConstructionPlan() => new()
    {
        (new("หน้าหลัก", "home", PageType.Landing, "รับเหมาก่อสร้าง · ครบวงจร · ราคาตรง"), new() {
            new(CmsBlockType.Hero, J(new {
                headline = "รับเหมาก่อสร้าง คุณภาพ ตรงเวลา",
                subheadline = "ทีมงานวิศวกร + ช่างมืออาชีพ · ทำงานตามแบบ · ภายในงบ · ครบกำหนด · รับประกัน 5 ปี",
                ctaText = "ขอใบเสนอราคา",
                ctaUrl = "/quote"
            })),
            new(CmsBlockType.RichText, J(new {
                content = @"<h2>🏗️ ทำไมต้องเรา</h2>
<ul>
  <li>👷 <strong>ทีมงาน In-house 50+ คน</strong> — ไม่ subcon ออก ทำเอง ควบคุมคุณภาพ</li>
  <li>📐 <strong>วิศวกรประจำโครงการ</strong> — มี วศ.บ. ใบ กว. ทุกโครงการ</li>
  <li>📅 <strong>รับประกันเสร็จตรงเวลา</strong> — ค่าปรับชดเชยถ้าล่าช้า</li>
  <li>💰 <strong>ราคาตรง โปร่งใส</strong> — BOQ ละเอียด · ไม่บวกกลาง</li>
  <li>🛡️ <strong>รับประกัน 5 ปี</strong> — โครงสร้าง · รั่ว · ร้าว · ระบบไฟ-น้ำ</li>
  <li>📷 <strong>รายงานหน้างานทุกสัปดาห์</strong> — รูป + วิดีโอ · ดูออนไลน์ได้</li>
</ul>"
            })),
            new(CmsBlockType.RichText, J(new {
                content = @"<h2>🔨 ประเภทงานที่รับ</h2>
<ul>
  <li><strong>บ้านพักอาศัย</strong> — บ้านเดี่ยว · ทาวน์เฮาส์ · บ้านแฝด</li>
  <li><strong>อาคารพาณิชย์</strong> — ร้านค้า · สำนักงาน · โกดัง</li>
  <li><strong>ปรับปรุง / ต่อเติม / รีโนเวท</strong> — บ้านเก่า · ห้อง · สำนักงาน</li>
  <li><strong>งานโครงสร้าง</strong> — เหล็ก · คอนกรีต · เสาเข็ม</li>
  <li><strong>Design + Build</strong> — ออกแบบ + รับเหมาเบ็ดเสร็จ</li>
</ul>"
            })),
            new(CmsBlockType.PricingTable, J(new {
                headline = "💰 ราคาประเมิน",
                plans = new[] {
                    new { name = "Standard", price = "฿15,000/ตรม.",
                          features = new[] { "วัสดุมาตรฐาน · แบบโครงการ", "ก่อสร้างทั่วไป", "รับประกันโครงสร้าง 5 ปี", "เหมาะบ้านเริ่มต้น" } },
                    new { name = "Premium", price = "฿22,000/ตรม.",
                          features = new[] { "วัสดุพรีเมียม · ออกแบบให้", "ทีมเฉพาะ · QA ละเอียด", "รับประกัน 10 ปี", "เหมาะบ้านครอบครัว" } },
                    new { name = "Luxury", price = "฿35,000+/ตรม.",
                          features = new[] { "วัสดุนำเข้า · Architect", "Custom design", "Project Manager ประจำ", "Smart home ready" } }
                }
            })),
            new(CmsBlockType.Testimonials, J(new {
                headline = "ผลงานจากลูกค้าจริง",
                testimonials = new[] {
                    new { quote = "งานเสร็จก่อนกำหนด 2 สัปดาห์ คุณภาพดี ราคาตรง", author = "คุณสุริยา", role = "บ้านพักอาศัย 2 ชั้น 240 ตรม." },
                    new { quote = "ทีมงานสุภาพ ทำงานเป็นระบบ มีรายงานทุกสัปดาห์", author = "คุณนภา", role = "ต่อเติมร้านอาหาร" },
                    new { quote = "รับประกันเรียลจริง ปีที่ 3 ยังมาดูแลให้ฟรี", author = "คุณเอก", role = "บ้านเดี่ยว 350 ตรม." },
                    new { quote = "เก็บงานละเอียด ไม่เก็บเงินเพิ่มนอก contract", author = "คุณมาลี", role = "ทาวน์โฮม" }
                }
            })),
            new(CmsBlockType.RichText, J(new {
                content = @"<h2>📋 ขั้นตอนการทำงาน</h2>
<ol>
  <li><strong>1. ปรึกษาฟรี + สำรวจหน้างาน</strong> — วิศวกรไปดูที่ในรัศมี 50 กม.</li>
  <li><strong>2. ใบเสนอราคา + BOQ</strong> — แยกรายการ ราคา ส่งใน 3 วัน</li>
  <li><strong>3. เซ็นสัญญา + จ่ายงวด 1</strong> — งวดแรก 20-30%</li>
  <li><strong>4. เริ่มก่อสร้าง</strong> — รายงานรูป + วิดีโอทุกสัปดาห์</li>
  <li><strong>5. ตรวจรับงาน + รับประกัน</strong> — ส่งมอบครบ · ปก. 5 ปี</li>
</ol>"
            })),
            new(CmsBlockType.Faq, J(new {
                headline = "คำถามที่พบบ่อย",
                items = new[] {
                    new { q = "ใช้เวลาก่อสร้างนานไหม?", a = "บ้านเดี่ยว 200-300 ตรม. ประมาณ 8-10 เดือน · ต่อเติม 1-3 เดือน" },
                    new { q = "ต้องจ่ายเงินยังไง?", a = "แบ่ง 4-6 งวดตามความคืบหน้า · จ่ายตามเปอร์เซ็นต์งาน" },
                    new { q = "รับประกันครอบคลุมอะไร?", a = "โครงสร้าง · กันรั่ว · ระบบไฟ-น้ำ · ผนัง 5 ปี" },
                    new { q = "ทำงานต่างจังหวัดได้ไหม?", a = "ได้ทั่วประเทศ · มีค่าเดินทาง + ค่าที่พักทีมงาน" },
                    new { q = "ออกแบบเองได้ไหม?", a = "ใช้แบบลูกค้าได้ · หรือเราออกแบบให้ในแพ็กเกจ Premium" }
                }
            })),
            new(CmsBlockType.CallToAction, J(new {
                headline = "มีโครงการในใจ?",
                subheadline = "ปรึกษาฟรี · ส่งภาพหน้างาน · ตอบใบเสนอราคาภายใน 3 วัน",
                ctaText = "ขอใบเสนอราคาฟรี",
                ctaUrl = "/quote"
            }))
        }),

        (new("ผลงาน", "portfolio", PageType.Standard, "ผลงานก่อสร้างที่ผ่านมา"), new() {
            new(CmsBlockType.Hero, J(new { headline = "ผลงานที่ผ่านมา", subheadline = "150+ โครงการสำเร็จในรอบ 10 ปี" })),
            new(CmsBlockType.RichText, J(new {
                content = @"<h2>📸 ผลงานเด่น</h2>
<ul>
  <li><strong>บ้านเดี่ยว สไตล์โมเดิร์น</strong> — 2 ชั้น · 240 ตรม. · 8 เดือน · 4.5 ล้าน</li>
  <li><strong>ปรับปรุงร้านกาแฟ</strong> — 80 ตรม. · 1 เดือน · 350,000</li>
  <li><strong>สำนักงาน 3 ชั้น</strong> — 600 ตรม. · 14 เดือน · 12 ล้าน</li>
  <li><strong>โกดังโรงงาน</strong> — 2,000 ตรม. · 6 เดือน · 8 ล้าน</li>
</ul>
<p style=""color:#94a3b8;font-size:13px"">* แก้ไขผลงานจริง · ใส่ภาพ Before/After ผ่าน Gallery block</p>"
            })),
            new(CmsBlockType.Gallery, J(new {
                images = new[] {
                    new { url = "https://placehold.co/600x400?text=Project+1", alt = "บ้านโมเดิร์น" },
                    new { url = "https://placehold.co/600x400?text=Project+2", alt = "ร้านกาแฟ" },
                    new { url = "https://placehold.co/600x400?text=Project+3", alt = "สำนักงาน" }
                }
            }))
        }),

        (new("ขอใบเสนอราคา", "quote", PageType.Standard, "ขอใบเสนอราคาฟรี"), new() {
            new(CmsBlockType.Hero, J(new {
                headline = "ขอใบเสนอราคา (ฟรี)",
                subheadline = "กรอกรายละเอียดงาน · ทีมงานจะติดต่อสำรวจหน้างานภายใน 24 ชม."
            })),
            new(CmsBlockType.RichText, J(new {
                content = @"<h2>📋 ข้อมูลที่ต้องเตรียม</h2>
<ul>
  <li>ที่ตั้ง / ที่ดิน · พื้นที่ใช้สอย (ตรม.)</li>
  <li>ประเภทงาน · จำนวนชั้น · เป้าหมายงบ</li>
  <li>แบบ (ถ้ามี) · ภาพ inspiration</li>
  <li>เวลาที่ต้องการเสร็จ</li>
</ul>"
            })),
            new(CmsBlockType.ContactForm, J(new {
                headline = "รายละเอียดโครงการ — ขอใบเสนอราคา",
                submitText = "ส่งคำขอใบเสนอราคา",
                emailTo = "",
                leadType = "Quote",
                askCompany = true,
                phoneRequired = true,
                extraFields = new object[] {
                    new { name = "project_type", label = "ประเภทโครงการ", type = "select",
                          options = new[] { "บ้านพักอาศัย", "ทาวน์เฮาส์", "อาคารพาณิชย์", "ต่อเติม/รีโนเวท", "สำนักงาน", "โรงงาน", "อื่นๆ" }, required = true },
                    new { name = "project_size_sqm", label = "พื้นที่ก่อสร้าง (ตร.ม.)", type = "number" },
                    new { name = "budget_baht", label = "งบประมาณ (บาท)", type = "number" },
                    new { name = "timeline", label = "ระยะเวลาที่ต้องการ", type = "text" },
                    new { name = "site_location", label = "ที่ตั้งโครงการ (จังหวัด/อำเภอ)", type = "text" }
                }
            }))
        }),

        (new("เกี่ยวกับเรา", "about", PageType.Standard, "บริษัทรับเหมา"), new() {
            new(CmsBlockType.Hero, J(new { headline = "เกี่ยวกับเรา", subheadline = "10 ปีในวงการ · 150+ โครงการ · ทีม 50+ คน" })),
            new(CmsBlockType.RichText, J(new {
                content = @"<h2>ใบอนุญาต + การรับรอง</h2>
<ul>
  <li>✓ ใบอนุญาตประกอบกิจการก่อสร้าง · กรมโยธาธิการ</li>
  <li>✓ ขึ้นทะเบียนผู้รับเหมา · กระทรวงพาณิชย์</li>
  <li>✓ Member · สมาคมก่อสร้างไทย</li>
  <li>✓ ISO 9001:2015</li>
</ul>
<h2>วิศวกร</h2>
<p>วิศวกรโครงสร้าง 4 คน · วิศวกรไฟฟ้า 2 คน · วิศวกรประปา 2 คน · มีใบ กว. ทุกคน</p>"
            }))
        }),

        (new("ติดต่อ", "contact", PageType.Standard, "ติดต่อรับเหมา"), new() {
            new(CmsBlockType.Hero, J(new { headline = "ติดต่อทีมงาน", subheadline = "เปิด จ-ส 8:00-17:30" })),
            new(CmsBlockType.RichText, J(new {
                content = "<p>📞 02-XXX-XXXX · 📱 081-XXX-XXXX · LINE: @construct · 📧 contact@construct.com</p>"
            })),
            new(CmsBlockType.ContactForm, J(new { headline = "ส่งข้อความ", submitText = "ส่ง", emailTo = "" })),
            new(CmsBlockType.Map, J(new { address = "กรุงเทพมหานคร ประเทศไทย" }))
        })
    };

    // ============================================================
    // REAL ESTATE — DDproperty / Zillow style
    // ============================================================
    private static List<(PageMeta, List<BlockMeta>)> RealEstatePlan() => new()
    {
        (new("หน้าหลัก", "home", PageType.Landing, "อสังหาฯ ขาย-เช่า บ้าน คอนโด ที่ดิน"), new() {
            new(CmsBlockType.Hero, J(new {
                headline = "บ้านในฝัน · ทำเลที่ใช่ · ราคาที่ถูกต้อง",
                subheadline = "นายหน้ามืออาชีพ · ฐานทรัพย์ 500+ รายการ · ปิดดีลเร็ว · ค่าธรรมเนียมโปร่งใส",
                ctaText = "ดูรายการขาย",
                ctaUrl = "/listings"
            })),
            new(CmsBlockType.RichText, J(new {
                content = @"<h2>🏘️ บริการของเรา</h2>
<ul>
  <li>🏠 <strong>ซื้อ-ขาย-เช่า</strong> — บ้าน · คอนโด · ที่ดิน · อาคารพาณิชย์</li>
  <li>💰 <strong>ประเมินราคาฟรี</strong> — ทรัพย์สินคุณราคาเท่าไหร่ ตอบใน 3 วัน</li>
  <li>📋 <strong>ที่ปรึกษาสินเชื่อ</strong> — ติดต่อทุกธนาคาร · ขอกู้ผ่านง่ายขึ้น</li>
  <li>⚖️ <strong>กฎหมาย + นิติกรรม</strong> — ดูแลจบที่กรมที่ดิน</li>
  <li>🏢 <strong>บริหารเช่า</strong> — รับดูแลแทนเจ้าของ · 8-10%</li>
  <li>🌐 <strong>การตลาด multi-channel</strong> — DDproperty · Hipflat · FB · Line</li>
</ul>"
            })),
            new(CmsBlockType.RichText, J(new {
                content = @"<h2>🏠 ทรัพย์เด่นประจำสัปดาห์</h2>
<ul>
  <li><strong>บ้านเดี่ยว ลาดพร้าว 87</strong> — 4 ห้องนอน · 120 ตรว. · 8.5 ล้าน</li>
  <li><strong>คอนโด อโศก</strong> — 1 ห้องนอน · 35 ตรม. · 3.2 ล้าน</li>
  <li><strong>ที่ดิน บางนา</strong> — 200 ตรว. · ติดถนน · 15 ล้าน</li>
  <li><strong>ทาวน์โฮม รามอินทรา</strong> — 3 ห้องนอน · 22 ตรว. · 4.2 ล้าน</li>
</ul>
<p><a href=""/listings"" class=""btn"">ดูทั้งหมด →</a></p>"
            })),
            new(CmsBlockType.Testimonials, J(new {
                headline = "ลูกค้าที่ปิดดีลแล้ว",
                testimonials = new[] {
                    new { quote = "ขายบ้านได้ภายใน 3 อาทิตย์ ราคาดี เกินคาด", author = "คุณวีระ", role = "ขายบ้าน 8.2 ล้าน" },
                    new { quote = "ตรงไปตรงมา ดูแลทุกขั้นตอนจบที่กรมที่ดิน", author = "คุณมาลี", role = "ซื้อคอนโด" },
                    new { quote = "หาที่ตรง spec ให้ใน 2 สัปดาห์ ประหยัดเวลามาก", author = "คุณนัฐ", role = "ซื้อบ้านเดี่ยว" },
                    new { quote = "ช่วยขอสินเชื่อผ่านได้แม้เครดิตไม่สูงมาก", author = "คุณภัทร", role = "First-time buyer" }
                }
            })),
            new(CmsBlockType.RichText, J(new {
                content = @"<h2>📅 ขั้นตอนการซื้อ-ขาย</h2>
<ol>
  <li><strong>1. คุยความต้องการ</strong> — งบ · ทำเล · ประเภท · timeline</li>
  <li><strong>2. คัดเลือกทรัพย์</strong> — เสนอ 5-10 ตัวที่ตรง spec</li>
  <li><strong>3. นัดดู</strong> — พาชม · ให้คำแนะนำ</li>
  <li><strong>4. ตกลงราคา + ทำสัญญา</strong> — ดูแลเอกสารทั้งหมด</li>
  <li><strong>5. โอนกรรมสิทธิ์</strong> — ไปกรมที่ดินด้วยกัน · ใช้เวลาครึ่งวัน</li>
</ol>"
            })),
            new(CmsBlockType.Faq, J(new {
                headline = "คำถามที่พบบ่อย",
                items = new[] {
                    new { q = "ค่านายหน้าคิดยังไง?", a = "ผู้ขายจ่าย 3% ของราคาขาย · ผู้ซื้อ + ผู้เช่าไม่เก็บค่าบริการ" },
                    new { q = "ประเมินราคาทรัพย์ฟรีจริงไหม?", a = "ฟรีจริง ส่ง 3 วัน · ไม่บังคับใช้บริการต่อ" },
                    new { q = "ขายเร็วไหม?", a = "เฉลี่ย 45-90 วันสำหรับราคา market · ราคาแพงกว่า market ใช้เวลานานขึ้น" },
                    new { q = "ดูแลต่างชาติได้ไหม?", a = "ได้ มีทีมพูดอังกฤษ · จีน · ดูแลเรื่อง FDI / ครอบครองคอนโด" },
                    new { q = "ขายผ่านช่องทางไหนบ้าง?", a = "DDproperty · Hipflat · Bahtsold · Facebook · LINE · ฐานลูกค้าเรา" }
                }
            })),
            new(CmsBlockType.CallToAction, J(new {
                headline = "อยากรู้ราคาทรัพย์ของคุณ?",
                subheadline = "ประเมินฟรี · ตอบใน 3 วัน · ไม่มีข้อผูกมัด",
                ctaText = "ประเมินฟรี",
                ctaUrl = "/contact"
            }))
        }),

        (new("รายการขาย / เช่า", "listings", PageType.Category, "อสังหาฯ ทั้งหมด"), new() {
            new(CmsBlockType.Hero, J(new { headline = "รายการทรัพย์", subheadline = "500+ รายการในระบบ · กรองตามทำเล · ราคา · ประเภท" })),
            new(CmsBlockType.RichText, J(new {
                content = @"<h2>🏠 ทรัพย์ตามหมวด</h2>
<h3>บ้านเดี่ยว</h3>
<ul>
  <li><strong>บ้านเดี่ยว ลาดพร้าว 87</strong> — 4 BR · 120 ตรว. · 8.5 ลบ.</li>
  <li><strong>บ้านเดี่ยว นวมินทร์</strong> — 3 BR · 65 ตรว. · 6.8 ลบ.</li>
</ul>
<h3>คอนโด</h3>
<ul>
  <li><strong>The Line Sukhumvit 71</strong> — 1 BR · 35 ตรม. · 3.2 ลบ.</li>
  <li><strong>Ideo Q Chula</strong> — Studio · 28 ตรม. · 2.9 ลบ.</li>
</ul>
<h3>ที่ดิน</h3>
<ul>
  <li><strong>ที่ดิน บางนา</strong> — 200 ตรว. ติดถนน · 15 ลบ.</li>
</ul>
<p style=""color:#94a3b8;font-size:13px"">* แก้ไขรายการจริง · ใช้ block Image/Gallery เพิ่มรูป</p>"
            }))
        }),

        (new("นัดดูทรัพย์", "viewing", PageType.Standard, "นัดดูบ้าน / คอนโด"), new() {
            new(CmsBlockType.Hero, J(new { headline = "นัดดูทรัพย์", subheadline = "เลือกวัน · นายหน้าพาชม · ตอบทุกคำถาม" })),
            new(CmsBlockType.ContactForm, J(new {
                headline = "ระบุทรัพย์ที่สนใจ — นัดดู",
                submitText = "ส่งคำขอนัด",
                emailTo = "",
                leadType = "Viewing",
                phoneRequired = true,
                extraFields = new object[] {
                    new { name = "property_type", label = "ประเภททรัพย์", type = "select",
                          options = new[] { "บ้านเดี่ยว", "ทาวน์เฮาส์/ทาวน์โฮม", "คอนโด", "ที่ดิน", "อาคารพาณิชย์" }, required = true },
                    new { name = "property_id_or_url", label = "เลขประกาศ / URL ทรัพย์ที่สนใจ", type = "text" },
                    new { name = "budget_range", label = "งบประมาณ", type = "select",
                          options = new[] { "<2 ล้าน", "2-5 ล้าน", "5-10 ล้าน", "10-30 ล้าน", "30 ล้านขึ้นไป" } },
                    new { name = "preferred_date", label = "วันที่สะดวกนัดดู", type = "date" },
                    new { name = "move_in_timeline", label = "ระยะเวลาที่ต้องการย้ายเข้า", type = "text" }
                }
            })),
            new(CmsBlockType.BookingCalendar, "{}")
        }),

        (new("ฝากขาย / เช่า", "list-property", PageType.Standard, "ฝากขาย ฝากเช่า"), new() {
            new(CmsBlockType.Hero, J(new { headline = "ฝากขาย / ฝากเช่ากับเรา", subheadline = "ประเมินราคา · การตลาดครบ · ปิดดีลเร็ว" })),
            new(CmsBlockType.RichText, J(new {
                content = @"<h2>🎯 สิ่งที่คุณจะได้</h2>
<ul>
  <li>✓ ประเมินราคาฟรี ส่งใน 3 วัน</li>
  <li>✓ ถ่ายภาพมืออาชีพ + Virtual tour 360°</li>
  <li>✓ ลงประกาศ DDproperty · Hipflat · FB · LINE</li>
  <li>✓ คัดกรองลูกค้า · พาชม · ต่อรอง</li>
  <li>✓ ดูแลโอน-เอกสารจนจบ</li>
  <li>✓ ค่านายหน้า 3% เฉพาะปิดดีลสำเร็จ</li>
</ul>"
            })),
            new(CmsBlockType.ContactForm, J(new { headline = "กรอกรายละเอียดทรัพย์", submitText = "ส่ง", emailTo = "" }))
        }),

        (new("ติดต่อ", "contact", PageType.Standard, "ติดต่อนายหน้า"), new() {
            new(CmsBlockType.Hero, J(new { headline = "ติดต่อนายหน้า", subheadline = "เปิดทุกวัน 9:00-20:00" })),
            new(CmsBlockType.RichText, J(new { content = "<p>📞 02-XXX-XXXX · 081-XXX-XXXX · LINE: @realestate · 📧 hello@realestate.com</p>" })),
            new(CmsBlockType.ContactForm, J(new { headline = "ส่งข้อความ", submitText = "ส่ง", emailTo = "" })),
            new(CmsBlockType.Map, J(new { address = "กรุงเทพมหานคร ประเทศไทย" }))
        })
    };

    // ============================================================
    // TECHNOLOGY — Linear / Stripe / Notion SaaS landing
    // ============================================================
    private static List<(PageMeta, List<BlockMeta>)> TechnologyPlan() => new()
    {
        (new("หน้าหลัก", "home", PageType.Landing, "ซอฟต์แวร์ที่ลูกค้ารัก · ทดลองฟรี 14 วัน"), new() {
            new(CmsBlockType.Hero, J(new {
                headline = "ซอฟต์แวร์ที่ทำให้ทีมคุณทำงานได้เร็วกว่าเดิม",
                subheadline = "ออกแบบมาเพื่อ SME ไทย · ใช้งานง่าย · ราคาเป็นมิตร · ทีมซัพพอร์ตคนไทย",
                ctaText = "ทดลองใช้ฟรี 14 วัน",
                ctaUrl = "/contact"
            })),
            new(CmsBlockType.RichText, J(new {
                content = @"<h2>✨ ฟีเจอร์เด่น</h2>
<ul>
  <li>⚡ <strong>ใช้งานได้ทันที</strong> — ไม่ต้องติดตั้ง · ไม่ต้องอัพเดท · เปิดใช้ใน 5 นาที</li>
  <li>🔒 <strong>ปลอดภัยมาตรฐานสากล</strong> — SSL · End-to-end encryption · daily backup</li>
  <li>📱 <strong>ทุกอุปกรณ์</strong> — Web · iOS · Android · ใช้ร่วมกันได้แบบ real-time</li>
  <li>🔗 <strong>เชื่อมต่อระบบเดิม</strong> — Excel · Google Sheets · Line OA · Shopee · Lazada</li>
  <li>🤝 <strong>ทีมซัพพอร์ตไทย</strong> — ตอบ LINE / โทร / อีเมล ใน 5 นาที</li>
  <li>📊 <strong>Insights + Reports</strong> — Dashboard real-time · ส่งออก Excel/PDF ได้</li>
</ul>"
            })),
            new(CmsBlockType.RichText, J(new {
                content = @"<h2>🚀 ทำไมต้อง [ชื่อ Product]</h2>
<ol>
  <li><strong>1. Setup ใน 5 นาที</strong> — ไม่ต้องเขียนโค้ด ไม่ต้องเรียน</li>
  <li><strong>2. Migrate ข้อมูลฟรี</strong> — ทีมเรา import จาก Excel / ระบบเก่าให้</li>
  <li><strong>3. Train ทีมงานคุณฟรี</strong> — Onboarding session 2 ชม. ออนไลน์</li>
  <li><strong>4. ใช้งานทันที</strong> — มี template สำเร็จรูป + best practices</li>
</ol>"
            })),
            new(CmsBlockType.PricingTable, J(new {
                headline = "💰 ราคาโปร่งใส · ไม่มี hidden cost",
                plans = new[] {
                    new { name = "Starter", price = "ฟรีตลอดชีพ",
                          features = new[] { "ผู้ใช้ 1 คน", "100 รายการ/เดือน", "ฟีเจอร์พื้นฐานครบ", "Email support" } },
                    new { name = "Business", price = "฿990/เดือน",
                          features = new[] { "ผู้ใช้ 10 คน", "ไม่จำกัดข้อมูล", "Reports + Export Excel", "LINE + โทรซัพพอร์ต", "Integrations" } },
                    new { name = "Enterprise", price = "ติดต่อขอราคา",
                          features = new[] { "ไม่จำกัดผู้ใช้", "API + SSO", "Dedicated Account Manager", "SLA 99.9%", "Custom features" } }
                }
            })),
            new(CmsBlockType.Testimonials, J(new {
                headline = "ลูกค้าที่ใช้แล้วบอกต่อ",
                testimonials = new[] {
                    new { quote = "ลดเวลาทำงานได้ครึ่งหนึ่ง ทีมงานชอบมาก", author = "คุณวรพล", role = "MD ร้านขายส่ง" },
                    new { quote = "ใช้งานง่ายมาก พนักงานเรียนรู้ได้ในวันเดียว", author = "คุณวรรณา", role = "เจ้าของร้านอาหาร 5 สาขา" },
                    new { quote = "Migrate ข้อมูลให้ฟรี ทีมซัพพอร์ตช่วยจริง", author = "คุณธนา", role = "Founder Startup" },
                    new { quote = "ราคาดีกว่าคู่แข่งต่างชาติ ฟีเจอร์ครบกว่า", author = "คุณภัทร", role = "CIO บริษัทมหาชน" }
                }
            })),
            new(CmsBlockType.Faq, J(new {
                headline = "คำถามที่พบบ่อย",
                items = new[] {
                    new { q = "มีให้ทดลองใช้ฟรีไหม?", a = "ทดลองใช้ฟีเจอร์ครบทั้งหมด 14 วัน ไม่ต้องใส่บัตรเครดิต" },
                    new { q = "ยกเลิกได้เมื่อไหร่?", a = "ยกเลิกได้ทุกเวลา ไม่มีค่าธรรมเนียม · เงินคืน prorate" },
                    new { q = "ข้อมูลของฉันปลอดภัยไหม?", a = "Encrypt ทั้งหมด · backup ทุกวัน · เซิร์ฟเวอร์ในไทย · ผ่าน ISO 27001" },
                    new { q = "ออก API ให้ไหม?", a = "มี REST API + Webhooks ครบทุกแพ็กเกจ Business ขึ้นไป" },
                    new { q = "Migrate จากระบบเก่าได้ไหม?", a = "ได้ ทีมเรา import ให้ฟรี จาก Excel / ระบบเดิม" },
                    new { q = "มี mobile app ไหม?", a = "มี iOS + Android · ดาวน์โหลดฟรี · ใช้พร้อมกับ web ได้" }
                }
            })),
            new(CmsBlockType.CallToAction, J(new {
                headline = "เริ่มทดลองใช้ฟรีวันนี้",
                subheadline = "14 วันเต็ม · ฟีเจอร์ครบ · ไม่ต้องใส่บัตรเครดิต",
                ctaText = "เริ่มฟรี",
                ctaUrl = "/contact"
            })),
            new(CmsBlockType.Newsletter, J(new {
                headline = "Updates & Product News",
                subheadline = "อัปเดทฟีเจอร์ใหม่ · เคล็ดลับใช้งาน · ส่วนลดพิเศษ",
                buttonText = "Subscribe"
            }))
        }),

        (new("ฟีเจอร์", "features", PageType.Standard, "ฟีเจอร์ทั้งหมด"), new() {
            new(CmsBlockType.Hero, J(new { headline = "ฟีเจอร์ทั้งหมด", subheadline = "ครบทุกอย่างที่ทีมคุณต้องการ" })),
            new(CmsBlockType.RichText, J(new {
                content = @"<h2>หมวดฟีเจอร์</h2>
<ul>
  <li>📋 <strong>Workflow</strong> — Project · Tasks · Calendar · Gantt</li>
  <li>💬 <strong>Collaboration</strong> — Comments · Mentions · Notifications</li>
  <li>📊 <strong>Analytics</strong> — Dashboard · Custom reports · KPI tracking</li>
  <li>🔗 <strong>Integrations</strong> — LINE · Slack · Google · Excel · Shopify</li>
  <li>🤖 <strong>Automation</strong> — Triggers · Rules · AI suggestions</li>
  <li>🔐 <strong>Security</strong> — SSO · 2FA · Audit log · Role-based access</li>
</ul>"
            })),
            new(CmsBlockType.CallToAction, J(new {
                headline = "อยากดู demo เฉพาะธุรกิจคุณ?", subheadline = "ทีมขายจัด live demo 30 นาที",
                ctaText = "นัด demo", ctaUrl = "/contact"
            }))
        }),

        (new("ราคา", "pricing", PageType.Standard, "ราคาทุกแพ็กเกจ"), new() {
            new(CmsBlockType.Hero, J(new { headline = "ราคาโปร่งใส · ไม่มีค่าใช้จ่ายแฝง" })),
            new(CmsBlockType.PricingTable, J(new {
                headline = "เลือกแพ็กเกจที่ใช่",
                plans = new[] {
                    new { name = "Starter", price = "ฟรี",
                          features = new[] { "1 user", "100 รายการ/เดือน", "ฟีเจอร์พื้นฐาน", "Email support" } },
                    new { name = "Business", price = "฿990/เดือน",
                          features = new[] { "10 users", "Unlimited records", "Reports + Export", "LINE + Phone support" } },
                    new { name = "Enterprise", price = "ติดต่อ",
                          features = new[] { "Unlimited users", "API + SSO", "Account Manager", "99.9% SLA" } }
                }
            })),
            new(CmsBlockType.Faq, J(new {
                headline = "คำถามเรื่องราคา",
                items = new[] {
                    new { q = "จ่ายรายปีถูกกว่าไหม?", a = "ใช่ จ่ายรายปี ลด 20%" },
                    new { q = "เปลี่ยนแพ็กเกจระหว่างใช้ได้?", a = "ได้ upgrade ทันที · downgrade รอจบรอบบิล" },
                    new { q = "มี student / NGO discount?", a = "มี ลด 50% สำหรับการศึกษา · ฟรีสำหรับ NGO" }
                }
            }))
        }),

        (new("เกี่ยวกับเรา", "about", PageType.Standard, "เกี่ยวกับบริษัท"), new() {
            new(CmsBlockType.Hero, J(new { headline = "ทีมเล็กที่ตั้งใจสร้างของดี", subheadline = "ก่อตั้งปี 2563 · ทีม 25 คน · ลูกค้า 1,000+" })),
            new(CmsBlockType.RichText, J(new {
                content = @"<h2>เรื่องราวของเรา</h2>
<p>เริ่มจาก 2 คน · ปัจจุบัน 25 คน · ลูกค้า 1,000+ บริษัทใช้งานทุกวัน</p>
<h2>วิสัยทัศน์</h2>
<p>ทำให้ SME ไทยทุกที่มีเครื่องมือดีพอ ๆ กับบริษัทใหญ่ ในราคาที่จับต้องได้</p>
<h2>นักลงทุน</h2>
<p>500 Global · A round 50M บาท · นักลงทุนไทย + สิงคโปร์</p>"
            }))
        }),

        (new("ติดต่อขาย / นัด demo", "contact", PageType.Standard, "นัด demo / ขอใบเสนอราคา"), new() {
            new(CmsBlockType.Hero, J(new { headline = "อยากดู demo?", subheadline = "ทีมขายติดต่อใน 1 วันทำการ · demo 30 นาที live" })),
            new(CmsBlockType.ContactForm, J(new { headline = "นัด demo / สอบถามราคา", submitText = "ส่ง", emailTo = "" })),
            new(CmsBlockType.RichText, J(new {
                content = "<p>📧 sales@product.com · 📱 LINE: @product · 💬 Live chat ในหน้าเว็บ</p>"
            })),
            new(CmsBlockType.Map, J(new { address = "กรุงเทพมหานคร ประเทศไทย" }))
        })
    };

    // ============================================================
    // EDUCATION — Skooldio / Coursera / Khan Academy style
    // ============================================================
    private static List<(PageMeta, List<BlockMeta>)> EducationPlan() => new()
    {
        (new("หน้าหลัก", "home", PageType.Landing, "หลักสูตร · สถาบันสอน · เรียนกับมืออาชีพ"), new() {
            new(CmsBlockType.Hero, J(new {
                headline = "เรียนรู้กับผู้เชี่ยวชาญตัวจริง",
                subheadline = "หลักสูตรครอบคลุม · ผู้สอนมืออาชีพประสบการณ์ 10+ ปี · ใบประกาศนียบัตรหลังจบ",
                ctaText = "ดูหลักสูตรทั้งหมด",
                ctaUrl = "/courses"
            })),
            new(CmsBlockType.RichText, J(new {
                content = @"<h2>🎓 ทำไมต้องเรียนกับเรา</h2>
<ul>
  <li>👨‍🏫 <strong>ผู้สอนมืออาชีพจริง</strong> — ทำงานในอุตสาหกรรม 10+ ปี ไม่ใช่นักทฤษฎี</li>
  <li>🛠️ <strong>เรียนแบบ workshop</strong> — ลงมือทำจริง 70% บรรยาย 30%</li>
  <li>🏆 <strong>ใบประกาศนียบัตร</strong> — รับรองโดยสถาบัน · แชร์ LinkedIn ได้</li>
  <li>👥 <strong>Community ศิษย์เก่า</strong> — 5,000+ คน · ปรึกษางาน · หางาน</li>
  <li>💼 <strong>Career support</strong> — แนะนำงาน · CV review · mock interview</li>
  <li>♻️ <strong>เรียนซ้ำได้ฟรี</strong> — รุ่นถัดไป ภายใน 1 ปี ไม่จำกัดครั้ง</li>
</ul>"
            })),
            new(CmsBlockType.RichText, J(new {
                content = @"<h2>📚 หลักสูตรเปิดสอน</h2>
<ul>
  <li><strong>หลักสูตรพื้นฐาน</strong> — สำหรับผู้เริ่มต้น · 8 ชั่วโมง</li>
  <li><strong>หลักสูตรขั้นกลาง</strong> — workshop ลงมือทำ · 16 ชั่วโมง</li>
  <li><strong>หลักสูตรขั้นสูง</strong> — Project จริง + Mentor · 32 ชั่วโมง</li>
  <li><strong>คอร์สเฉพาะองค์กร</strong> — ออกแบบให้บริษัท · จัดในที่</li>
</ul>
<p><a href=""/courses"" class=""btn"">ดูทั้งหมด →</a></p>"
            })),
            new(CmsBlockType.PricingTable, J(new {
                headline = "💰 ราคาหลักสูตร",
                plans = new[] {
                    new { name = "Foundation", price = "฿4,900",
                          features = new[] { "เรียน 8 ชม. (2 วัน)", "เอกสาร + แบบฝึก", "ใบประกาศ", "เรียนซ้ำฟรี 1 ปี" } },
                    new { name = "Intermediate", price = "฿9,900",
                          features = new[] { "เรียน 16 ชม. (4 วัน)", "Workshop ลงมือทำ", "ที่ปรึกษา 1:1 1 ครั้ง", "ใบประกาศ + Community" } },
                    new { name = "Advanced", price = "฿18,900",
                          features = new[] { "เรียน 32 ชม. (8 วัน)", "Project จริง", "Mentor 3 เดือน", "ใบประกาศ + แนะนำงาน" } }
                }
            })),
            new(CmsBlockType.Testimonials, J(new {
                headline = "ความคิดเห็นจากผู้เรียน",
                testimonials = new[] {
                    new { quote = "เปลี่ยนชีวิตการทำงานเลย · เพิ่มเงินเดือนได้ 40%", author = "คุณเจมส์", role = "ศิษย์รุ่น 8 · Marketer" },
                    new { quote = "ได้งานตรงสายภายใน 3 เดือนหลังจบคอร์ส", author = "คุณดาว", role = "ศิษย์รุ่น 12 · Designer" },
                    new { quote = "ผู้สอนใจดี ตอบทุกคำถาม · ได้ความรู้จริง", author = "คุณนัฐ", role = "Advanced Track" },
                    new { quote = "Network ดี · เจอเพื่อนทำธุรกิจร่วมกัน", author = "คุณพี", role = "Entrepreneur" }
                }
            })),
            new(CmsBlockType.RichText, J(new {
                content = @"<h2>📅 รอบเรียน</h2>
<ol>
  <li><strong>1. ดูหลักสูตร</strong> — เลือกที่สนใจ · ดู syllabus</li>
  <li><strong>2. สมัครเรียน</strong> — กรอกใบสมัคร · ชำระเงิน</li>
  <li><strong>3. รับเอกสาร</strong> — Welcome kit · เพิ่มกลุ่ม community</li>
  <li><strong>4. เรียน Online / Onsite</strong> — เลือก track ที่สะดวก</li>
  <li><strong>5. จบหลักสูตร</strong> — รับใบประกาศ · เข้า alumni network</li>
</ol>"
            })),
            new(CmsBlockType.Faq, J(new {
                headline = "คำถามที่พบบ่อย",
                items = new[] {
                    new { q = "ผ่อนชำระได้ไหม?", a = "ได้ บัตรเครดิต 0% สูงสุด 10 เดือน · มี Pay Later ผ่าน Atome / Kerry" },
                    new { q = "ขาดเรียนได้ไหม?", a = "ได้ มีคลิป record · เรียนซ้ำฟรีรุ่นถัดไป" },
                    new { q = "รับประกันงานไหม?", a = "ไม่ได้รับประกัน แต่มี career support · CV review · refer งาน" },
                    new { q = "Refund ได้ไหม?", a = "ก่อนเริ่มเรียน 7 วัน คืน 100% · หลังเริ่มแล้วไม่คืน แต่เลื่อนได้" },
                    new { q = "บริษัทจ่ายให้พนักงานได้ไหม?", a = "ได้ ออก ภพ.20 · 75% ของค่าเรียนหักภาษีได้ตาม กม." }
                }
            })),
            new(CmsBlockType.CallToAction, J(new {
                headline = "เริ่มเรียนกับเราวันนี้",
                subheadline = "รุ่นถัดไปเปิด · ที่นั่งจำกัด 30 คน/รุ่น",
                ctaText = "สมัครเลย",
                ctaUrl = "/register"
            })),
            new(CmsBlockType.Newsletter, J(new {
                headline = "รับข่าวเปิดรุ่น + ส่วนลด early bird",
                subheadline = "สมาชิกได้สิทธิ์จองก่อน · ส่วนลด 20%",
                buttonText = "สมัครรับข่าว"
            }))
        }),

        (new("หลักสูตร", "courses", PageType.Standard, "หลักสูตรทั้งหมด"), new() {
            new(CmsBlockType.Hero, J(new { headline = "หลักสูตรทั้งหมด", subheadline = "เลือกหลักสูตรที่ใช่กับเป้าหมายคุณ" })),
            new(CmsBlockType.RichText, J(new {
                content = @"<h2>🎓 หลักสูตรเปิดสอน</h2>
<h3>Track พื้นฐาน</h3>
<ul>
  <li>Foundation A — เริ่มต้น · 8 ชม. · 4,900</li>
  <li>Foundation B — สำหรับมือใหม่ · 8 ชม. · 4,900</li>
</ul>
<h3>Track ขั้นกลาง</h3>
<ul>
  <li>Workshop X — ลงมือทำ · 16 ชม. · 9,900</li>
  <li>Workshop Y — Project-based · 16 ชม. · 9,900</li>
</ul>
<h3>Track ขั้นสูง</h3>
<ul>
  <li>Master Z — 32 ชม. + Mentor · 18,900</li>
</ul>"
            })),
            new(CmsBlockType.PricingTable, J(new {
                headline = "เลือกหลักสูตรที่ใช่",
                plans = new[] {
                    new { name = "Foundation", price = "฿4,900", features = new[] { "8 ชม.", "ใบประกาศ", "เรียนซ้ำฟรี" } },
                    new { name = "Workshop", price = "฿9,900", features = new[] { "16 ชม.", "ลงมือทำจริง", "Mentor 1:1" } },
                    new { name = "Master", price = "฿18,900", features = new[] { "32 ชม.", "Project", "Career support" } }
                }
            }))
        }),

        (new("สมัครเรียน", "register", PageType.Standard, "สมัครเรียน · ลงทะเบียน"), new() {
            new(CmsBlockType.Hero, J(new { headline = "สมัครเรียน", subheadline = "เลือกหลักสูตร → รอบเรียน → ชำระเงิน → เริ่มเรียน" })),
            new(CmsBlockType.RichText, J(new {
                content = @"<h2>📅 รอบเรียนถัดไป</h2>
<ul>
  <li><strong>Foundation</strong> — รุ่น 25 · เริ่ม 15 มี.ค. · เหลือ 12 ที่</li>
  <li><strong>Workshop</strong> — รุ่น 18 · เริ่ม 22 มี.ค. · เหลือ 6 ที่</li>
  <li><strong>Master</strong> — รุ่น 10 · เริ่ม 5 เม.ย. · เหลือ 3 ที่</li>
</ul>"
            })),
            new(CmsBlockType.ContactForm, J(new {
                headline = "ลงทะเบียนเรียน",
                submitText = "ส่งใบสมัคร",
                emailTo = "",
                leadType = "Enrollment",
                phoneRequired = true,
                extraFields = new object[] {
                    new { name = "course", label = "หลักสูตรที่สมัคร", type = "select",
                          options = new[] { "Foundation (พื้นฐาน)", "Workshop (ขั้นกลาง)", "Master (ขั้นสูง)" }, required = true },
                    new { name = "batch", label = "รอบเรียนที่สนใจ", type = "text" },
                    new { name = "experience_level", label = "ระดับประสบการณ์ปัจจุบัน", type = "select",
                          options = new[] { "ยังไม่มีพื้นฐาน", "เริ่มต้น", "ระดับกลาง", "ระดับสูง" } },
                    new { name = "payment_method", label = "วิธีชำระเงิน", type = "select",
                          options = new[] { "โอนครั้งเดียว", "ผ่อน 0% 3 เดือน", "ผ่อน 6 เดือน (มีดอกเบี้ย)" } },
                    new { name = "id_card_or_passport", label = "เลขบัตรประชาชน / Passport (สำหรับออกใบเสร็จ)", type = "text" }
                }
            }))
        }),

        (new("ผู้สอน", "instructors", PageType.Standard, "ทีมผู้สอน"), new() {
            new(CmsBlockType.Hero, J(new { headline = "ทีมผู้สอน", subheadline = "มืออาชีพตัวจริงในวงการ" })),
            new(CmsBlockType.RichText, J(new {
                content = @"<h2>👨‍🏫 ผู้สอนหลัก</h2>
<ul>
  <li><strong>อ.สมชาย ดีดี</strong> — 15 ปีใน Google · MIT Master · สอน Foundation</li>
  <li><strong>อ.ภัทรา ใสใส</strong> — Head of Design Wongnai · สอน Workshop</li>
  <li><strong>อ.วีระ มีฝีมือ</strong> — Founder Startup ที่ exit · สอน Master</li>
</ul>
<p style=""color:#94a3b8;font-size:13px"">* แก้ไขรายชื่อผู้สอนจริงของสถาบัน</p>"
            }))
        }),

        (new("ติดต่อ", "contact", PageType.Standard, "ติดต่อสถาบัน"), new() {
            new(CmsBlockType.Hero, J(new { headline = "ติดต่อสถาบัน", subheadline = "จ-ส 9:00-18:00" })),
            new(CmsBlockType.RichText, J(new {
                content = "<p>📞 02-XXX-XXXX · 📱 LINE: @school · 📧 hello@school.com</p>"
            })),
            new(CmsBlockType.ContactForm, J(new { headline = "ส่งข้อความ", submitText = "ส่ง", emailTo = "" })),
            new(CmsBlockType.Map, J(new { address = "กรุงเทพมหานคร ประเทศไทย" }))
        })
    };

    // ============================================================
    // TRANSPORTATION — Kerry / Flash / J&T Express style
    // ============================================================
    private static List<(PageMeta, List<BlockMeta>)> TransportationPlan() => new()
    {
        (new("หน้าหลัก", "home", PageType.Landing, "บริการขนส่ง · พัสดุ · รถบรรทุก · ทั่วประเทศ"), new() {
            new(CmsBlockType.Hero, J(new {
                headline = "ขนส่งทั่วไทย · ตรงเวลา · ปลอดภัย",
                subheadline = "รับ-ส่งทั่วประเทศ · ติดตามสถานะออนไลน์ · ประกันความเสียหายสูงสุด 100,000",
                ctaText = "ขอราคา · จองรถ",
                ctaUrl = "/quote"
            })),
            new(CmsBlockType.RichText, J(new {
                content = @"<h2>🚚 ทำไมต้องเรา</h2>
<ul>
  <li>⏰ <strong>ตรงเวลา 99%</strong> — มีระบบ track real-time</li>
  <li>🛡️ <strong>ประกันความเสียหาย</strong> — สูงสุด 100,000 บาท/เที่ยว</li>
  <li>📦 <strong>COD ทั่วประเทศ</strong> — คืนยอดใน 2 วันทำการ</li>
  <li>🌍 <strong>ครอบคลุม 77 จังหวัด</strong> — สาขา + จุดรับฝาก 500+ แห่ง</li>
  <li>📱 <strong>จองออนไลน์</strong> — แอป + เว็บ · ติดตามสถานะแบบ live</li>
  <li>🚛 <strong>รถทุกขนาด</strong> — 4 ล้อ · 6 ล้อ · 10 ล้อ · รถเย็น</li>
</ul>"
            })),
            new(CmsBlockType.RichText, J(new {
                content = @"<h2>📦 บริการของเรา</h2>
<ul>
  <li><strong>พัสดุย่อย</strong> — ทั่วประเทศ · 1-3 วัน · เริ่ม 35 บาท</li>
  <li><strong>รถบรรทุก 4/6/10 ล้อ</strong> — เหมารายเที่ยว / รายวัน / รายเดือน</li>
  <li><strong>ขนย้ายบ้าน · สำนักงาน</strong> — รวมคนยก · บรรจุภัณฑ์ · ประกัน</li>
  <li><strong>ขนส่งสินค้าเย็น</strong> — ห้องเย็นอุณหภูมิควบคุม -18°C ถึง +5°C</li>
  <li><strong>Express same-day</strong> — ในกรุงเทพ · ภายใน 4 ชม.</li>
</ul>"
            })),
            new(CmsBlockType.PricingTable, J(new {
                headline = "💰 ราคาเริ่มต้น",
                plans = new[] {
                    new { name = "พัสดุย่อย", price = "เริ่ม ฿35",
                          features = new[] { "น้ำหนัก ≤ 5 kg", "ในเขตกรุงเทพ 1 วัน", "ต่างจังหวัด 2-3 วัน", "COD ได้" } },
                    new { name = "รถ 4 ล้อ", price = "เริ่ม ฿1,500/เที่ยว",
                          features = new[] { "บรรทุก 1 ตัน", "คนขับ + คนยก 1 คน", "ในเขตกรุงเทพ", "ติดตาม live" } },
                    new { name = "รถ 6 ล้อ", price = "เริ่ม ฿3,500/เที่ยว",
                          features = new[] { "บรรทุก 4 ตัน", "คนขับ + คนยก 2 คน", "ในเขตกรุงเทพ + ปริมณฑล", "ประกัน 100,000" } }
                }
            })),
            new(CmsBlockType.Testimonials, J(new {
                headline = "ลูกค้าที่ไว้ใจเรา",
                testimonials = new[] {
                    new { quote = "ส่งของถึงเร็วทุกครั้ง · ระบบ track ใช้ง่าย", author = "ร้านออนไลน์ A", role = "ลูกค้าประจำ 4 ปี" },
                    new { quote = "ขนย้ายสำนักงานเรียบร้อย ไม่มีของเสียหาย", author = "บริษัท B", role = "ขนย้ายสำนักงาน 200 ตรม." },
                    new { quote = "COD คืนเงินตรงเวลา · ราคายุติธรรม", author = "ร้าน Shopee", role = "ส่ง 500 ออเดอร์/เดือน" },
                    new { quote = "รถเย็นรักษาคุณภาพสินค้าได้ดี", author = "Cold Chain", role = "ผู้นำเข้าอาหารเย็น" }
                }
            })),
            new(CmsBlockType.RichText, J(new {
                content = @"<h2>🔄 ขั้นตอนการส่งของ</h2>
<ol>
  <li><strong>1. กรอกรายละเอียด</strong> — ต้นทาง · ปลายทาง · น้ำหนัก · ขนาด</li>
  <li><strong>2. รับใบเสนอราคา</strong> — ราคาแสดงทันที · ยืนยันใน 5 นาที</li>
  <li><strong>3. เข้ารับของ</strong> — ทีมงานไปรับถึงที่ (ในเขตบริการ)</li>
  <li><strong>4. ติดตามออนไลน์</strong> — เลข tracking · live location</li>
  <li><strong>5. ส่งถึงปลายทาง</strong> — แจ้งผู้รับ · ลายเซ็นยืนยัน</li>
</ol>"
            })),
            new(CmsBlockType.Faq, J(new {
                headline = "คำถามที่พบบ่อย",
                items = new[] {
                    new { q = "ส่งของเสียหายต้องทำอย่างไร?", a = "เรามีประกันสูงสุด 100,000 บาทต่อเที่ยว · แจ้งภายใน 24 ชม. พร้อมรูป" },
                    new { q = "มี COD ไหม?", a = "รองรับ COD ทั่วประเทศ · ค่าธรรมเนียม 30 บาท · คืนยอดใน 2 วันทำการ" },
                    new { q = "เช็คสถานะของได้ที่ไหน?", a = "ผ่านระบบ tracking ออนไลน์ · กรอกเลขที่ส่ง · หรือดูใน LINE OA" },
                    new { q = "ลูกค้าธุรกิจมีส่วนลดไหม?", a = "มี ส่งเกิน 100 ออเดอร์/เดือน ลดสูงสุด 30%" },
                    new { q = "มีของห้ามส่งไหม?", a = "อาวุธ · ยาเสพติด · ของผิดกฎหมาย · สัตว์มีชีวิต · ดูรายละเอียดในเว็บ" }
                }
            })),
            new(CmsBlockType.CallToAction, J(new {
                headline = "ส่งของวันนี้ ถึงพรุ่งนี้",
                subheadline = "เปิดรับ-ส่งทุกวัน · จองออนไลน์ · ติดตาม live",
                ctaText = "ขอราคา · จองรถ",
                ctaUrl = "/quote"
            }))
        }),

        (new("บริการ", "services", PageType.Standard, "บริการขนส่งทั้งหมด"), new() {
            new(CmsBlockType.Hero, J(new { headline = "บริการของเรา" })),
            new(CmsBlockType.RichText, J(new {
                content = @"<h2>หมวดบริการ</h2>
<ul>
  <li>📦 <strong>Parcel</strong> — พัสดุย่อย ทั่วประเทศ</li>
  <li>🚚 <strong>Truck</strong> — รถ 4/6/10 ล้อ เหมาเที่ยว</li>
  <li>🏠 <strong>Moving</strong> — ขนย้ายบ้าน/สำนักงาน</li>
  <li>🧊 <strong>Cold Chain</strong> — ขนส่งสินค้าเย็น</li>
  <li>⚡ <strong>Express</strong> — Same-day ในกรุงเทพ</li>
  <li>🌏 <strong>International</strong> — ส่งออก ASEAN · จีน · ทั่วโลก</li>
</ul>"
            })),
            new(CmsBlockType.PricingTable, J(new {
                headline = "ราคา",
                plans = new[] {
                    new { name = "Parcel", price = "฿35+", features = new[] { "ทั่วประเทศ", "1-3 วัน", "COD ได้" } },
                    new { name = "Truck 4 ล้อ", price = "฿1,500+/เที่ยว", features = new[] { "1 ตัน", "+ คนยก 1" } },
                    new { name = "Truck 6 ล้อ", price = "฿3,500+/เที่ยว", features = new[] { "4 ตัน", "+ คนยก 2" } }
                }
            }))
        }),

        (new("ขอราคา / จองรถ", "quote", PageType.Standard, "ขอราคาขนส่ง"), new() {
            new(CmsBlockType.Hero, J(new { headline = "ขอราคา / จองรถ", subheadline = "ตอบกลับภายใน 30 นาที · พร้อมรับงานทันที" })),
            new(CmsBlockType.RichText, J(new {
                content = @"<h2>📋 ข้อมูลที่ต้องแจ้ง</h2>
<ul>
  <li>ต้นทาง · ปลายทาง (จังหวัด/อำเภอ)</li>
  <li>ประเภทสินค้า · น้ำหนัก · ขนาด</li>
  <li>วัน-เวลาที่ต้องการขนส่ง</li>
  <li>ต้องการคนยกไหม</li>
</ul>"
            })),
            new(CmsBlockType.ContactForm, J(new {
                headline = "รายละเอียดงานขนส่ง",
                submitText = "ส่งคำขอ",
                emailTo = "",
                leadType = "ShipmentQuote",
                askCompany = true,
                phoneRequired = true,
                extraFields = new object[] {
                    new { name = "service_type", label = "ประเภทบริการ", type = "select",
                          options = new[] { "Parcel (พัสดุ)", "Truck 4 ล้อ", "Truck 6 ล้อ", "Truck 10 ล้อ", "Moving (ขนย้าย)", "Cold Chain", "Express same-day", "International" }, required = true },
                    new { name = "origin", label = "ต้นทาง (จังหวัด/อำเภอ)", type = "text", required = true },
                    new { name = "destination", label = "ปลายทาง (จังหวัด/อำเภอ)", type = "text", required = true },
                    new { name = "weight_kg", label = "น้ำหนัก (กก.)", type = "number" },
                    new { name = "dimensions", label = "ขนาด (กว้าง × ยาว × สูง ซม.)", type = "text" },
                    new { name = "pickup_date", label = "วันที่ต้องการขนส่ง", type = "date" },
                    new { name = "needs_loaders", label = "ต้องการคนยกของ", type = "select", options = new[] { "ไม่ต้องการ", "1 คน", "2 คน", "3 คนขึ้นไป" } }
                }
            }))
        }),

        (new("Tracking", "tracking", PageType.Standard, "ติดตามพัสดุ"), new() {
            new(CmsBlockType.Hero, J(new { headline = "ติดตามพัสดุ", subheadline = "กรอกเลข tracking ดูสถานะแบบ real-time" })),
            new(CmsBlockType.RichText, J(new {
                content = @"<p>หากต้องการติดตามสถานะการจัดส่ง กรุณาติดต่อทีมงานพร้อมเลข tracking
หรือใช้ระบบ tracking ในแอป (ติดต่อ admin เพื่อรับลิงก์)</p>"
            })),
            new(CmsBlockType.ContactForm, J(new { headline = "สอบถามสถานะ", submitText = "ส่ง", emailTo = "" }))
        }),

        (new("ติดต่อ", "contact", PageType.Standard, "ติดต่อขนส่ง"), new() {
            new(CmsBlockType.Hero, J(new { headline = "ติดต่อทีมงาน", subheadline = "Call center 24 ชม." })),
            new(CmsBlockType.RichText, J(new {
                content = "<p>📞 02-XXX-XXXX · 📱 081-XXX-XXXX (24 ชม.) · LINE: @logistics · 📧 ops@logistics.com</p>"
            })),
            new(CmsBlockType.ContactForm, J(new { headline = "ส่งข้อความ", submitText = "ส่ง", emailTo = "" })),
            new(CmsBlockType.Map, J(new { address = "กรุงเทพมหานคร ประเทศไทย" }))
        })
    };

    // ============================================================
    // HOTEL — Booking.com / Marriott / Banyan Tree style
    // ============================================================
    private static List<(PageMeta, List<BlockMeta>)> HotelPlan() => new()
    {
        (new("หน้าหลัก", "home", PageType.Landing, "โรงแรม · รีสอร์ท · ที่พัก · จองตรงรับส่วนลด"), new() {
            new(CmsBlockType.Hero, J(new {
                headline = "พักผ่อนเหมือนกลับบ้าน",
                subheadline = "ห้องสะอาด · บรรยากาศดี · ทำเลใจกลางเมือง · จองตรงรับส่วนลด 10%",
                ctaText = "จองห้องพัก",
                ctaUrl = "/booking"
            })),
            new(CmsBlockType.RichText, J(new {
                content = @"<h2>✨ ทำไมต้องพักกับเรา</h2>
<ul>
  <li>📍 <strong>ทำเลใจกลางเมือง</strong> — เดินถึง BTS · ใกล้แหล่งช้อปปิ้ง</li>
  <li>🛏️ <strong>ห้องพักสะอาด</strong> — เปลี่ยนผ้าทุกวัน · ทีมแม่บ้านมืออาชีพ</li>
  <li>🍳 <strong>อาหารเช้าฟรี</strong> — Buffet 30+ เมนู · 6:30-10:30</li>
  <li>💊 <strong>สิ่งอำนวยความสะดวก</strong> — สระ · ฟิตเนส · สปา · ที่จอดรถ</li>
  <li>🚗 <strong>รับส่งสนามบิน</strong> — บริการพรีเมียม · ราคาเริ่ม 800 บาท</li>
  <li>💰 <strong>จองตรงรับส่วนลด 10%</strong> — ไม่มีค่าธรรมเนียม OTA</li>
</ul>"
            })),
            new(CmsBlockType.RichText, J(new {
                content = @"<h2>🏨 ห้องพักของเรา</h2>
<ul>
  <li><strong>Standard Room</strong> — 25 ตรม. · 1 เตียง 6 ฟุต · ฿1,500/คืน</li>
  <li><strong>Deluxe Room</strong> — 32 ตรม. · King size · วิวเมือง · ฿2,500/คืน</li>
  <li><strong>Junior Suite</strong> — 48 ตรม. · ห้องนั่งเล่นแยก · ฿3,800/คืน</li>
  <li><strong>Executive Suite</strong> — 65 ตรม. · อ่างอาบน้ำ · มินิบาร์ · ฿4,500/คืน</li>
</ul>
<p><a href=""/rooms"" class=""btn"">ดูห้องทั้งหมด →</a></p>"
            })),
            new(CmsBlockType.PricingTable, J(new {
                headline = "💰 ราคาห้องพัก (ต่อคืน · รวมอาหารเช้า)",
                plans = new[] {
                    new { name = "Standard", price = "฿1,500",
                          features = new[] { "25 ตรม.", "เตียง 6 ฟุต", "อาหารเช้า 2 ท่าน", "WiFi · TV · มินิบาร์" } },
                    new { name = "Deluxe", price = "฿2,500",
                          features = new[] { "32 ตรม.", "King size · วิวเมือง", "อาหารเช้า 2 ท่าน", "เครื่องชงกาแฟ · อ่างอาบน้ำ" } },
                    new { name = "Suite", price = "฿4,500",
                          features = new[] { "65 ตรม.", "ห้องนั่งเล่นแยก", "Late checkout 16:00", "มินิบาร์ฟรี · สิทธิ์ Executive Lounge" } }
                }
            })),
            new(CmsBlockType.Testimonials, J(new {
                headline = "รีวิวจากแขก",
                testimonials = new[] {
                    new { quote = "ห้องสะอาดมาก พนักงานน่ารัก จะกลับมาแน่นอน", author = "Sarah J.", role = "Booking.com 9.5/10" },
                    new { quote = "ทำเลดีมาก เดินไปกินข้าวได้รอบ ๆ", author = "คุณภัทร", role = "TripAdvisor 5 ดาว" },
                    new { quote = "อาหารเช้าเยอะมาก สดและอร่อย", author = "Mr. Tanaka", role = "Repeat guest" },
                    new { quote = "สระว่ายน้ำดาดฟ้าสวยมาก วิวเมืองยามค่ำคืน", author = "Agoda Verified", role = "9.2/10" }
                }
            })),
            new(CmsBlockType.RichText, J(new {
                content = @"<h2>📅 ขั้นตอนการจอง</h2>
<ol>
  <li><strong>1. เลือกห้อง + วัน</strong> — Check in / Check out · จำนวนแขก</li>
  <li><strong>2. ยืนยัน + ชำระเงิน</strong> — บัตรเครดิต / โอน · ยอด 50% มัดจำ</li>
  <li><strong>3. รับ confirmation</strong> — Email + SMS · พร้อม voucher</li>
  <li><strong>4. Check-in</strong> — 15:00 · บัตร ปชช./passport</li>
  <li><strong>5. Check-out</strong> — 12:00 · ขยายได้ตามว่าง</li>
</ol>"
            })),
            new(CmsBlockType.Faq, J(new {
                headline = "คำถามที่พบบ่อย",
                items = new[] {
                    new { q = "Check-in / Check-out กี่โมง?", a = "Check-in 15:00 · Check-out 12:00 · ยืดได้ตามว่าง" },
                    new { q = "ยกเลิกได้ฟรีไหม?", a = "ฟรีก่อน check-in 3 วัน · หลังจากนั้นคิด 1 คืน" },
                    new { q = "มีรถรับส่งสนามบินไหม?", a = "มี ราคาเริ่ม 800 บาท · แจ้งล่วงหน้า 24 ชม." },
                    new { q = "อนุญาตสัตว์เลี้ยงไหม?", a = "ห้องพิเศษ pet-friendly 3 ห้อง · มีค่าทำความสะอาด 500 บาท" },
                    new { q = "มีอาหารฮาลาลไหม?", a = "มี · แจ้งล่วงหน้าตอนจอง · เชฟปรับเมนูให้" }
                }
            })),
            new(CmsBlockType.CallToAction, J(new {
                headline = "จองตรงรับส่วนลด 10%",
                subheadline = "ไม่มีค่าธรรมเนียม · ฟรี upgrade ถ้าห้องว่าง",
                ctaText = "จองตอนนี้",
                ctaUrl = "/booking"
            }))
        }),

        (new("ห้องพัก", "rooms", PageType.Standard, "ห้องพักและราคา"), new() {
            new(CmsBlockType.Hero, J(new { headline = "ห้องพักของเรา", subheadline = "4 ประเภท · ตอบทุกความต้องการ" })),
            new(CmsBlockType.RichText, J(new {
                content = @"<h2>🛏️ Standard Room (฿1,500)</h2>
<p>25 ตรม. · เตียง 6 ฟุต · ห้องน้ำในตัว · WiFi · TV · มินิบาร์ · อาหารเช้า 2 ท่าน</p>
<h2>🛏️ Deluxe Room (฿2,500)</h2>
<p>32 ตรม. · King size · วิวเมือง · เครื่องชงกาแฟ · อ่างอาบน้ำ · bathrobe</p>
<h2>🛏️ Junior Suite (฿3,800)</h2>
<p>48 ตรม. · ห้องนั่งเล่นแยก · pantry · เครื่องซักผ้า · เหมาะ stay ยาว</p>
<h2>🛏️ Executive Suite (฿4,500)</h2>
<p>65 ตรม. · 2 ห้องนอน · อ่างน้ำวน · Executive Lounge · late checkout</p>"
            })),
            new(CmsBlockType.Gallery, J(new {
                images = new[] {
                    new { url = "https://placehold.co/600x400?text=Standard", alt = "Standard Room" },
                    new { url = "https://placehold.co/600x400?text=Deluxe", alt = "Deluxe Room" },
                    new { url = "https://placehold.co/600x400?text=Junior+Suite", alt = "Junior Suite" },
                    new { url = "https://placehold.co/600x400?text=Executive", alt = "Executive Suite" }
                }
            }))
        }),

        (new("จองห้องพัก", "booking", PageType.Standard, "จองห้อง"), new() {
            new(CmsBlockType.Hero, J(new { headline = "จองห้องพัก", subheadline = "จองตรงรับส่วนลด 10% · ไม่มีค่าธรรมเนียม" })),
            new(CmsBlockType.BookingCalendar, "{}"),
            new(CmsBlockType.ContactForm, J(new { headline = "หรือกรอกฟอร์มจอง", submitText = "ส่งคำขอจอง", emailTo = "" }))
        }),

        (new("สิ่งอำนวยความสะดวก", "amenities", PageType.Standard, "สิ่งอำนวยความสะดวก"), new() {
            new(CmsBlockType.Hero, J(new { headline = "สิ่งอำนวยความสะดวก" })),
            new(CmsBlockType.RichText, J(new {
                content = @"<h2>🏊 สิ่งอำนวยความสะดวก</h2>
<ul>
  <li><strong>สระว่ายน้ำดาดฟ้า</strong> — เปิด 6:00-22:00 · ผ้าเช็ดตัวฟรี</li>
  <li><strong>ฟิตเนส</strong> — เปิด 24 ชม. · เครื่องทันสมัย</li>
  <li><strong>สปา + นวด</strong> — เปิด 10:00-22:00 · เปิดให้ outside guest</li>
  <li><strong>ห้องอาหาร</strong> — Breakfast buffet · A la carte · Room service 24 ชม.</li>
  <li><strong>ที่จอดรถ</strong> — ฟรี · Valet ตามขอ</li>
  <li><strong>รถรับส่งสนามบิน</strong> — เริ่ม 800 บาท · แจ้งล่วงหน้า 24 ชม.</li>
</ul>"
            }))
        }),

        (new("ติดต่อ / ที่ตั้ง", "contact", PageType.Standard, "ที่ตั้งโรงแรม"), new() {
            new(CmsBlockType.Hero, J(new { headline = "ติดต่อโรงแรม", subheadline = "Front desk เปิด 24 ชม." })),
            new(CmsBlockType.RichText, J(new {
                content = "<p>📞 02-XXX-XXXX (24 ชม.) · LINE: @hotel · 📧 reservations@hotel.com</p>"
            })),
            new(CmsBlockType.ContactForm, J(new { headline = "สอบถามการจอง", submitText = "ส่ง", emailTo = "" })),
            new(CmsBlockType.Map, J(new { address = "กรุงเทพมหานคร ประเทศไทย" }))
        })
    };

    // ============================================================
    // MANUFACTURING — Foxconn / CPF OEM industrial style
    // ============================================================
    private static List<(PageMeta, List<BlockMeta>)> ManufacturingPlan() => new()
    {
        (new("หน้าหลัก", "home", PageType.Landing, "โรงงานผลิต OEM · ODM · มาตรฐาน ISO"), new() {
            new(CmsBlockType.Hero, J(new {
                headline = "โรงงานผลิตคุณภาพ · มาตรฐานสากล",
                subheadline = "รับ OEM / ODM · ผลิตตามแบบ · ส่งออกทั่วโลก · ISO 9001 · GMP · มอก.",
                ctaText = "ส่งแบบขอใบเสนอราคา",
                ctaUrl = "/rfq"
            })),
            new(CmsBlockType.RichText, J(new {
                content = @"<h2>🏭 ทำไมต้องเรา</h2>
<ul>
  <li>📐 <strong>กำลังผลิต 100,000 ชิ้น/เดือน</strong> — รองรับ scale ได้</li>
  <li>🏗️ <strong>โรงงาน 5,000 ตรม.</strong> — เครื่องจักรอัตโนมัติทันสมัย</li>
  <li>✅ <strong>QC 100% ทุกขั้นตอน</strong> — มาตรฐาน Six Sigma</li>
  <li>📅 <strong>ส่งของตรงเวลา 99%</strong> — ผ่าน OTD audit ทุกเดือน</li>
  <li>🏆 <strong>ISO 9001 · 14001 · GMP · มอก.</strong></li>
  <li>🌐 <strong>ส่งออก 25 ประเทศ</strong> — รวม ASEAN · ตะวันออกกลาง · EU</li>
</ul>"
            })),
            new(CmsBlockType.RichText, J(new {
                content = @"<h2>📦 ประเภทสินค้าที่ผลิต</h2>
<ul>
  <li><strong>หมวด A</strong> — (แก้ไขให้ตรงกับโรงงานคุณ)</li>
  <li><strong>หมวด B</strong> — รายละเอียดผลิตภัณฑ์</li>
  <li><strong>หมวด C</strong> — รายละเอียดผลิตภัณฑ์</li>
  <li><strong>Private Label</strong> — บรรจุภัณฑ์โลโก้ลูกค้า</li>
  <li><strong>Custom Formula</strong> — พัฒนาสูตรเฉพาะ</li>
</ul>"
            })),
            new(CmsBlockType.PricingTable, J(new {
                headline = "💰 MOQ + เงื่อนไข",
                plans = new[] {
                    new { name = "Trial Order", price = "MOQ 1,000 ชิ้น",
                          features = new[] { "ทดลองคุณภาพ", "Sample ฟรี 5 ชิ้น", "ส่งของใน 30 วัน", "ราคา trial" } },
                    new { name = "Standard OEM", price = "MOQ 10,000 ชิ้น",
                          features = new[] { "ราคาผลิตปกติ", "Lead time 45-60 วัน", "ออกแบบบรรจุภัณฑ์ฟรี", "Credit term 30 วัน" } },
                    new { name = "Strategic Partner", price = "MOQ 100,000 ชิ้น",
                          features = new[] { "ราคาดีที่สุด", "Dedicated production line", "R&D ร่วมพัฒนา", "Credit term 60-90 วัน" } }
                }
            })),
            new(CmsBlockType.Testimonials, J(new {
                headline = "ลูกค้าที่ไว้วางใจ",
                testimonials = new[] {
                    new { quote = "ส่งของตรงเวลา · ทุก lot คุณภาพคงที่", author = "ABC Trading", role = "5 ปีติดต่อกัน" },
                    new { quote = "ทีมงานช่วยพัฒนาสูตรให้เราด้วย", author = "XYZ Brand", role = "Private Label" },
                    new { quote = "QC ละเอียดมาก reject rate ต่ำกว่า 0.5%", author = "Mega Corp", role = "OEM 3 ปี" },
                    new { quote = "ส่งออก EU ผ่าน CE certification เรียบร้อย", author = "EuroBrand", role = "Exporter" }
                }
            })),
            new(CmsBlockType.RichText, J(new {
                content = @"<h2>🔄 กระบวนการ OEM</h2>
<ol>
  <li><strong>1. ส่ง RFQ</strong> — แบบ · spec · จำนวน · timeline</li>
  <li><strong>2. ทีม R&D พิจารณา</strong> — ทำได้ไหม · ราคาเท่าไหร่ · ใช้เวลานานแค่ไหน</li>
  <li><strong>3. ใบเสนอราคา + Sample</strong> — ทำตัวอย่างให้ตรวจสอบ</li>
  <li><strong>4. PO + Production</strong> — เริ่มผลิต · update ทุกสัปดาห์</li>
  <li><strong>5. QC + Pre-shipment</strong> — ตรวจคุณภาพ · ส่งของ · เอกสาร COA/COO</li>
</ol>"
            })),
            new(CmsBlockType.Faq, J(new {
                headline = "คำถามที่พบบ่อย",
                items = new[] {
                    new { q = "MOQ ต่ำสุดเท่าไหร่?", a = "Trial order 1,000 ชิ้น · ปกติ 10,000 ชิ้น · แล้วแต่ประเภทสินค้า" },
                    new { q = "Lead time นานเท่าไหร่?", a = "Sample 7-14 วัน · Mass production 45-60 วัน หลัง PO" },
                    new { q = "เซ็น NDA ไหม?", a = "เซ็นทุกโครงการ · ข้อมูลลูกค้าเป็นความลับ 100%" },
                    new { q = "ออกเอกสารส่งออกได้?", a = "ได้ Form D/E · COA · COO · CE · FDA ขึ้นกับประเทศ" },
                    new { q = "Visit โรงงานได้ไหม?", a = "ได้ นัดล่วงหน้า 7 วัน · มี tour เต็มรูปแบบ" }
                }
            })),
            new(CmsBlockType.CallToAction, J(new {
                headline = "เริ่มผลิตกับเรา",
                subheadline = "ส่ง RFQ วันนี้ · ทีม R&D ตอบใน 2 วันทำการ",
                ctaText = "ส่ง RFQ",
                ctaUrl = "/rfq"
            }))
        }),

        (new("ความสามารถ", "capabilities", PageType.Standard, "ความสามารถในการผลิต"), new() {
            new(CmsBlockType.Hero, J(new { headline = "Capabilities", subheadline = "เครื่องจักร · บุคลากร · มาตรฐาน" })),
            new(CmsBlockType.RichText, J(new {
                content = @"<h2>🏭 โรงงาน</h2>
<ul>
  <li>พื้นที่ 5,000 ตรม. · สายการผลิต 4 สาย</li>
  <li>กำลังการผลิต 100,000 ชิ้น/เดือน</li>
  <li>คลังสินค้า 1,500 ตรม. · จุดโหลด-อันโหลด 4 จุด</li>
</ul>
<h2>⚙️ เครื่องจักร</h2>
<ul>
  <li>(แก้ไขรายการเครื่องจักรของโรงงานคุณ)</li>
  <li>Mixing tanks · Filling lines · QC lab · Packaging</li>
  <li>Automated robotic arm · Vision inspection</li>
</ul>
<h2>👥 บุคลากร</h2>
<ul>
  <li>วิศวกร 8 คน · QC 6 คน · R&D 4 คน · Production 80 คน</li>
</ul>
<h2>🏆 มาตรฐาน</h2>
<ul>
  <li>ISO 9001:2015 · ISO 14001 · GMP · HACCP · มอก.</li>
  <li>CE · FDA · Halal · Kosher (ตามผลิตภัณฑ์)</li>
</ul>"
            }))
        }),

        (new("ส่งแบบขอใบเสนอราคา (RFQ)", "rfq", PageType.Standard, "RFQ"), new() {
            new(CmsBlockType.Hero, J(new {
                headline = "Request for Quote (RFQ)",
                subheadline = "ส่งแบบ · spec · จำนวน — ทีมงานติดต่อกลับภายใน 2 วันทำการ"
            })),
            new(CmsBlockType.RichText, J(new {
                content = @"<h2>📋 ข้อมูลที่ควรแจ้ง</h2>
<ul>
  <li>ประเภทสินค้า · spec · materials</li>
  <li>จำนวน (MOQ + ระยะเวลา repeat)</li>
  <li>บรรจุภัณฑ์ · labelling · มาตรฐานที่ต้องการ</li>
  <li>ประเทศปลายทาง (สำหรับเอกสารส่งออก)</li>
  <li>Budget · Timeline · Expected delivery</li>
</ul>"
            })),
            new(CmsBlockType.ContactForm, J(new {
                headline = "รายละเอียดผลิตภัณฑ์ — Request For Quote (RFQ)",
                submitText = "ส่ง RFQ",
                emailTo = "",
                leadType = "Rfq",
                askCompany = true,
                askTaxId = true,
                phoneRequired = true,
                extraFields = new object[] {
                    new { name = "product_type", label = "ประเภทผลิตภัณฑ์", type = "text", required = true },
                    new { name = "specifications", label = "Spec / Material ที่ต้องการ", type = "textarea", required = true },
                    new { name = "quantity_moq", label = "จำนวน (MOQ + repeat ต่อเดือน/ปี)", type = "text", required = true },
                    new { name = "packaging", label = "บรรจุภัณฑ์ / Labelling", type = "text" },
                    new { name = "standards", label = "มาตรฐานที่ต้องการ (ISO/GMP/HACCP/มอก./อย./Halal)", type = "text" },
                    new { name = "export_country", label = "ประเทศปลายทาง (ถ้าส่งออก)", type = "text" },
                    new { name = "target_price", label = "Target price (THB / USD ต่อหน่วย)", type = "text" },
                    new { name = "needed_by", label = "Expected delivery date", type = "date" }
                }
            }))
        }),

        (new("เกี่ยวกับเรา", "about", PageType.Standard, "เกี่ยวกับโรงงาน"), new() {
            new(CmsBlockType.Hero, J(new { headline = "เกี่ยวกับโรงงาน", subheadline = "20 ปีในวงการผลิต · 25 ประเทศปลายทาง" })),
            new(CmsBlockType.RichText, J(new {
                content = @"<h2>ประวัติบริษัท</h2>
<p>ก่อตั้งปี 2547 · จดทะเบียนทุน 100 ล้านบาท · BOI Promoted<br>
ผลิตให้แบรนด์มากกว่า 50 แบรนด์ · ส่งออก 25 ประเทศ</p>
<h2>วิสัยทัศน์</h2>
<p>เป็นโรงงานพันธมิตรที่ลูกค้า trust ที่สุดในไทย · ผลิตสินค้าคุณภาพในราคาที่แข่งขันได้</p>
<h2>มาตรฐาน + การรับรอง</h2>
<ul>
  <li>✓ ISO 9001:2015 · 14001 · 45001</li>
  <li>✓ GMP · HACCP (อาหาร)</li>
  <li>✓ มอก. · อย. · Halal</li>
  <li>✓ BOI · Section A1</li>
</ul>"
            }))
        }),

        (new("ติดต่อ", "contact", PageType.Standard, "ติดต่อโรงงาน"), new() {
            new(CmsBlockType.Hero, J(new { headline = "ติดต่อทีมขาย OEM", subheadline = "ทีมขาย ไทย · จีน · อังกฤษ" })),
            new(CmsBlockType.RichText, J(new {
                content = "<p>📞 02-XXX-XXXX · 📧 sales@factory.com · LINE: @factory · WeChat: factoryco</p>"
            })),
            new(CmsBlockType.ContactForm, J(new { headline = "ส่งข้อความ", submitText = "ส่ง", emailTo = "" })),
            new(CmsBlockType.Map, J(new { address = "นิคมอุตสาหกรรม ประเทศไทย" }))
        })
    };
}
