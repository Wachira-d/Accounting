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
///   • 4–6 SitePage rows (Home, About, Services/Menu/Products, Contact, etc.)
///   • A composed list of PageBlock rows per page with sensible defaults
///     (Hero with Thai copy, RichText story, Contact form, Map placeholder).
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
            IndustryType.Restaurant or IndustryType.Cafe => RestaurantPlan(),
            IndustryType.Retail or IndustryType.Ecommerce or IndustryType.Trading
                or IndustryType.Agriculture => RetailPlan(),
            IndustryType.Beauty => BeautyPlan(),
            IndustryType.Healthcare => HealthcarePlan(),
            IndustryType.Service or IndustryType.Freelance => ServicePlan(),
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

    // ----- General / Service / Freelance / Tech / Default -----
    private static List<(PageMeta, List<BlockMeta>)> GeneralPlan() => new()
    {
        (new("หน้าหลัก", "home", PageType.Landing, "เว็บไซต์อย่างเป็นทางการ — บริการครบวงจร"), new() {
            new(CmsBlockType.Hero, J(new {
                headline = "ยินดีต้อนรับ",
                subheadline = "เราพร้อมให้บริการที่ดีที่สุดแก่คุณ — มืออาชีพ ราคาเป็นมิตร",
                ctaText = "ดูบริการของเรา",
                ctaUrl = "/services"
            })),
            new(CmsBlockType.RichText, J(new {
                content = @"<h2>เกี่ยวกับเรา</h2>
<p>เราคือทีมงานมืออาชีพที่มีประสบการณ์ในการให้บริการมายาวนาน
เน้นความใส่ใจในรายละเอียด คุณภาพงานเป็นเลิศ และความพอใจของลูกค้า</p>
<ul>
  <li>✓ ทีมงานมืออาชีพ ประสบการณ์มากกว่า 10 ปี</li>
  <li>✓ บริการครบวงจร ตอบโจทย์ทุกความต้องการ</li>
  <li>✓ ราคาเป็นมิตร โปร่งใส ไม่มีค่าใช้จ่ายแฝง</li>
</ul>"
            })),
            new(CmsBlockType.Testimonials, J(new {
                headline = "ลูกค้าพูดถึงเรา",
                testimonials = new[] {
                    new { quote = "บริการดีมาก ทีมงานเอาใจใส่ทุกขั้นตอน", author = "คุณสมชาย", role = "ลูกค้าประจำ" },
                    new { quote = "ราคาเป็นธรรม คุณภาพเกินคาด", author = "คุณวรรณา", role = "เจ้าของกิจการ" },
                    new { quote = "แนะนำต่อให้เพื่อนเลย ไม่ผิดหวัง", author = "คุณภัทรา", role = "ผู้จัดการ" }
                }
            })),
            new(CmsBlockType.CallToAction, J(new {
                headline = "สนใจบริการของเรา?",
                subheadline = "ติดต่อสอบถามฟรี ไม่มีค่าใช้จ่ายในการประเมินงาน",
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
<p>ทีมงานของเราประกอบด้วยมืออาชีพในหลากหลายสาขา
พร้อมให้คำปรึกษาและให้บริการอย่างเต็มที่</p>"
            }))
        }),

        (new("บริการ", "services", PageType.Standard, "บริการของเรา"), new() {
            new(CmsBlockType.Hero, J(new {
                headline = "บริการของเรา",
                subheadline = "เลือกบริการที่ตรงใจคุณ"
            })),
            new(CmsBlockType.PricingTable, J(new {
                headline = "แพ็กเกจที่แนะนำ",
                plans = new[] {
                    new { name = "แพ็กเกจเริ่มต้น", price = "฿1,500/เดือน",
                          features = new[] { "บริการพื้นฐาน", "ตอบกลับใน 24 ชม.", "อีเมลสนับสนุน" } },
                    new { name = "แพ็กเกจมาตรฐาน", price = "฿3,500/เดือน",
                          features = new[] { "บริการครบครัน", "ตอบกลับใน 4 ชม.", "โทรศัพท์ + อีเมล", "รายงานรายเดือน" } },
                    new { name = "แพ็กเกจพรีเมียม", price = "฿6,000/เดือน",
                          features = new[] { "บริการเฉพาะลูกค้า VIP", "ตอบกลับทันที", "ที่ปรึกษาเฉพาะตัว", "รายงานเรียลไทม์" } }
                }
            })),
            new(CmsBlockType.Faq, J(new {
                headline = "คำถามที่พบบ่อย",
                items = new[] {
                    new { q = "บริการของท่านมีรับประกันไหม?", a = "เรามีรับประกันคุณภาพงานทุกชิ้น ถ้าไม่พอใจคืนเงิน 100%" },
                    new { q = "ใช้เวลานานเท่าไหร่ในการเริ่มงาน?", a = "หลังจากชำระเงินมัดจำ เราเริ่มภายใน 1-2 วันทำการ" },
                    new { q = "ชำระเงินอย่างไร?", a = "โอนผ่านธนาคาร, พร้อมเพย์, หรือบัตรเครดิต" }
                }
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
            new(CmsBlockType.ContactForm, J(new { headline = "ส่งข้อความถึงเรา", submitText = "ส่งข้อความ" })),
            new(CmsBlockType.Map, J(new { address = "กรุงเทพมหานคร ประเทศไทย" }))
        })
    };

    // ----- Restaurant / Cafe -----
    private static List<(PageMeta, List<BlockMeta>)> RestaurantPlan() => new()
    {
        (new("หน้าหลัก", "home", PageType.Landing, "ร้านอาหารบรรยากาศดี เมนูเด็ด ๆ"), new() {
            new(CmsBlockType.Hero, J(new {
                headline = "อร่อยทุกคำ ทุกจาน",
                subheadline = "บรรยากาศดี วัตถุดิบสด ราคาเป็นกันเอง — เปิดทุกวัน 10:00-22:00",
                ctaText = "ดูเมนู",
                ctaUrl = "/menu"
            })),
            new(CmsBlockType.RichText, J(new {
                content = @"<h2>🍜 เมนูเด่นประจำร้าน</h2>
<p>เราคัดสรรวัตถุดิบสดใหม่ทุกวัน ปรุงด้วยสูตรเฉพาะของร้าน
รสชาติคงเส้นคงวา ลูกค้าประจำติดใจมานานกว่า 10 ปี</p>"
            })),
            new(CmsBlockType.Testimonials, J(new {
                headline = "เสียงจากลูกค้า",
                testimonials = new[] {
                    new { quote = "อร่อยที่สุดในย่านนี้! กลับมากินซ้ำทุกอาทิตย์", author = "คุณนิด" },
                    new { quote = "บรรยากาศร้านดี เหมาะพาครอบครัวมา", author = "คุณตุ้ม" },
                    new { quote = "พนักงานบริการดี ทำให้รู้สึกอบอุ่น", author = "คุณแก้ว" }
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
                subheadline = "อร่อย คุ้มราคา วัตถุดิบสด"
            })),
            new(CmsBlockType.RichText, J(new {
                content = @"<h2>🍲 อาหารจานหลัก</h2>
<ul>
  <li><strong>ผัดกะเพราหมูสับไข่ดาว</strong> — 80 บาท</li>
  <li><strong>ผัดไทยกุ้งสด</strong> — 120 บาท</li>
  <li><strong>ข้าวคลุกกะปิ</strong> — 90 บาท</li>
  <li><strong>ข้าวมันไก่ทอด</strong> — 75 บาท</li>
</ul>
<h2>🥤 เครื่องดื่ม</h2>
<ul>
  <li><strong>ชาเย็น</strong> — 35 บาท</li>
  <li><strong>กาแฟเย็น</strong> — 45 บาท</li>
  <li><strong>น้ำผลไม้ปั่น</strong> — 55 บาท</li>
</ul>
<h2>🍰 ของหวาน</h2>
<ul>
  <li><strong>มะม่วงข้าวเหนียว</strong> — 80 บาท</li>
  <li><strong>บัวลอยไข่หวาน</strong> — 50 บาท</li>
</ul>
<p style=""color:#94a3b8;font-size:13px;margin-top:18px"">* ราคาอาจเปลี่ยนแปลงได้ — กรุณาแก้ไขรายการ + ราคาให้ตรงกับร้านของคุณ</p>"
            }))
        }),

        (new("จองโต๊ะ", "booking", PageType.Standard, "จองโต๊ะล่วงหน้า"), new() {
            new(CmsBlockType.Hero, J(new {
                headline = "จองโต๊ะ",
                subheadline = "กรอกรายละเอียด เราจะติดต่อยืนยันภายใน 30 นาที"
            })),
            new(CmsBlockType.ContactForm, J(new {
                headline = "แบบฟอร์มจองโต๊ะ",
                submitText = "ส่งคำขอจอง"
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
📱 LINE: @yourshop</p>
<h2>🕐 เวลาเปิด-ปิด</h2>
<p>จันทร์-ศุกร์: 10:00 - 22:00<br>
เสาร์-อาทิตย์: 09:00 - 23:00</p>"
            })),
            new(CmsBlockType.Map, J(new { address = "กรุงเทพมหานคร ประเทศไทย" })),
            new(CmsBlockType.ContactForm, J(new { headline = "ส่งข้อความ", submitText = "ส่ง" }))
        })
    };

    // ----- Retail / Ecommerce -----
    private static List<(PageMeta, List<BlockMeta>)> RetailPlan() => new()
    {
        (new("หน้าหลัก", "home", PageType.Landing, "ร้านค้าออนไลน์ สินค้าคุณภาพดี ส่งไว"), new() {
            new(CmsBlockType.Hero, J(new {
                headline = "ช้อปออนไลน์ ส่งทั่วไทย",
                subheadline = "สินค้าคุณภาพดี ราคาดี รับประกันความพอใจ",
                ctaText = "ช้อปเลย",
                ctaUrl = "/products"
            })),
            new(CmsBlockType.ProductGrid, J(new { headline = "สินค้าแนะนำ", featured = true, limit = 8 })),
            new(CmsBlockType.Testimonials, J(new {
                headline = "รีวิวจากลูกค้า",
                testimonials = new[] {
                    new { quote = "ของจริงตรงปก ส่งเร็วมาก", author = "คุณแอน" },
                    new { quote = "ราคาดี แพ็คดีไม่มีเสียหาย", author = "คุณบี" },
                    new { quote = "เจอปัญหาแอดมินช่วยแก้เร็วมาก", author = "คุณซี" }
                }
            })),
            new(CmsBlockType.Faq, J(new {
                headline = "คำถามที่พบบ่อย",
                items = new[] {
                    new { q = "ส่งของกี่วัน?", a = "ในเขตกรุงเทพ 1-2 วัน, ต่างจังหวัด 2-4 วัน" },
                    new { q = "ส่งเงินสดปลายทางได้ไหม?", a = "รองรับ COD ทั่วประเทศ มีค่าธรรมเนียมเพิ่ม 30 บาท" },
                    new { q = "เปลี่ยน-คืนสินค้าได้ไหม?", a = "คืนได้ภายใน 7 วัน ถ้าสินค้ายังไม่ได้ใช้และอยู่ในสภาพเดิม" }
                }
            }))
        }),

        (new("สินค้า", "products", PageType.Standard, "สินค้าทั้งหมด"), new() {
            new(CmsBlockType.Hero, J(new { headline = "สินค้าทั้งหมด", subheadline = "เลือกซื้อตามหมวดที่คุณสนใจ" })),
            new(CmsBlockType.ProductGrid, J(new { limit = 24 }))
        }),

        (new("เกี่ยวกับเรา", "about", PageType.Standard, "เรื่องราวของร้าน"), new() {
            new(CmsBlockType.Hero, J(new { headline = "เกี่ยวกับร้านของเรา", subheadline = "เริ่มต้นจากความรักในสิ่งที่ทำ" })),
            new(CmsBlockType.RichText, J(new {
                content = @"<h2>เรื่องราวของเรา</h2>
<p>เราเริ่มต้นจากการเป็นร้านเล็ก ๆ ที่อยากให้คนไทยได้เข้าถึงสินค้าคุณภาพดีในราคาที่จับต้องได้
ด้วยการคัดสรรสินค้าทุกชิ้นด้วยตนเอง รับประกันคุณภาพทุกออเดอร์</p>
<h2>ทำไมต้องเลือกเรา</h2>
<ul>
  <li>✓ สินค้าของแท้ 100% มีใบกำกับภาษี</li>
  <li>✓ ส่งไว ภายใน 24 ชม. ในเขตกรุงเทพ</li>
  <li>✓ พร้อมเปลี่ยน-คืนสินค้าภายใน 7 วัน</li>
  <li>✓ ทีมแอดมินตอบเร็ว ปรึกษาฟรี</li>
</ul>"
            }))
        }),

        (new("ติดต่อ", "contact", PageType.Standard, "ติดต่อร้านค้า"), new() {
            new(CmsBlockType.RichText, J(new {
                content = @"<h2>📞 ติดต่อสอบถาม</h2>
<p>LINE: @yourshop · โทร: 02-XXX-XXXX · อีเมล: info@example.com</p>"
            })),
            new(CmsBlockType.ContactForm, J(new { headline = "สอบถามสินค้า", submitText = "ส่ง" }))
        })
    };

    // ----- Beauty / Spa / Salon -----
    private static List<(PageMeta, List<BlockMeta>)> BeautyPlan() => new()
    {
        (new("หน้าหลัก", "home", PageType.Landing, "บริการความงาม สปา ทำเล็บ"), new() {
            new(CmsBlockType.Hero, J(new {
                headline = "ผ่อนคลายเหมือนหลุดจากความวุ่นวาย",
                subheadline = "บริการสปา นวดแผนไทย ทำเล็บ — ทีมงานมืออาชีพ บรรยากาศหรูหรา",
                ctaText = "จองคิว",
                ctaUrl = "/booking"
            })),
            new(CmsBlockType.RichText, J(new {
                content = @"<h2>🌸 บริการของเรา</h2>
<ul>
  <li><strong>นวดสปาแบบไทย</strong> — 60 นาที 800 บาท</li>
  <li><strong>นวดน้ำมันหอมระเหย</strong> — 60 นาที 1,200 บาท</li>
  <li><strong>ทรีตเมนต์หน้า</strong> — 90 นาที 1,500 บาท</li>
  <li><strong>ทำเล็บมือ + ทาสี</strong> — 350 บาท</li>
</ul>"
            })),
            new(CmsBlockType.Testimonials, J(new {
                headline = "ลูกค้าพึงพอใจ",
                testimonials = new[] {
                    new { quote = "นวดดีมาก หายปวดเลย", author = "คุณเอ" },
                    new { quote = "บรรยากาศสบายมาก เหมือนหลุดไปอีกโลก", author = "คุณบี" },
                    new { quote = "ทีมงานดูแลดี เป็นกันเอง", author = "คุณซี" }
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
            new(CmsBlockType.Hero, J(new { headline = "บริการของเรา" })),
            new(CmsBlockType.PricingTable, J(new {
                headline = "แพ็กเกจที่นิยม",
                plans = new[] {
                    new { name = "Relax Package", price = "฿1,500",
                          features = new[] { "นวดสปาไทย 60 นาที", "ทำเล็บมือ", "ชาสมุนไพร" } },
                    new { name = "Luxury Package", price = "฿2,800",
                          features = new[] { "นวดน้ำมัน 90 นาที", "ทรีตเมนต์หน้า", "ทำเล็บมือ + เท้า", "ชา + ของว่าง" } },
                    new { name = "Couple Package", price = "฿4,500",
                          features = new[] { "นวดคู่ 90 นาที", "ห้อง VIP", "อาหารกลางวัน", "แชมเปญ" } }
                }
            }))
        }),

        (new("จองคิว", "booking", PageType.Standard, "จองคิวล่วงหน้า"), new() {
            new(CmsBlockType.Hero, J(new {
                headline = "จองคิวล่วงหน้า",
                subheadline = "เลือกบริการและเวลาที่สะดวก เราจะติดต่อยืนยัน"
            })),
            new(CmsBlockType.ContactForm, J(new { headline = "แจ้งจองคิว", submitText = "ส่งคำขอ" }))
        }),

        (new("ติดต่อเรา", "contact", PageType.Standard, "ที่ตั้งร้าน เปิด-ปิด"), new() {
            new(CmsBlockType.RichText, J(new {
                content = @"<h2>📍 ที่ตั้ง · 🕐 เปิดทุกวัน 10:00-22:00</h2>
<p>โทร: 02-XXX-XXXX · LINE: @yourspa</p>"
            })),
            new(CmsBlockType.Map, J(new { address = "กรุงเทพมหานคร ประเทศไทย" })),
            new(CmsBlockType.ContactForm, J(new { submitText = "ส่ง" }))
        })
    };

    // ----- Healthcare / Clinic -----
    private static List<(PageMeta, List<BlockMeta>)> HealthcarePlan() => new()
    {
        (new("หน้าหลัก", "home", PageType.Landing, "คลินิกของเรา ดูแลทุกอาการ"), new() {
            new(CmsBlockType.Hero, J(new {
                headline = "ดูแลสุขภาพคุณอย่างมืออาชีพ",
                subheadline = "แพทย์ผู้เชี่ยวชาญ เครื่องมือทันสมัย บริการครบวงจร",
                ctaText = "นัดหมายแพทย์",
                ctaUrl = "/appointment"
            })),
            new(CmsBlockType.RichText, J(new {
                content = @"<h2>🏥 บริการของเรา</h2>
<ul>
  <li>ตรวจสุขภาพประจำปี</li>
  <li>ตรวจรักษาโรคทั่วไป</li>
  <li>วัคซีนป้องกันโรค</li>
  <li>ปรึกษาแพทย์ผู้เชี่ยวชาญ</li>
</ul>"
            })),
            new(CmsBlockType.CallToAction, J(new {
                headline = "นัดหมายล่วงหน้าผ่านเว็บไซต์",
                subheadline = "ลดเวลารอคิว เลือกเวลาที่สะดวก",
                ctaText = "นัดหมายเลย",
                ctaUrl = "/appointment"
            }))
        }),

        (new("บริการ", "services", PageType.Standard, "บริการทางการแพทย์"), new() {
            new(CmsBlockType.Hero, J(new { headline = "บริการของเรา" })),
            new(CmsBlockType.RichText, J(new {
                content = @"<h2>🩺 บริการตรวจรักษา</h2>
<ul>
  <li><strong>ตรวจสุขภาพประจำปี</strong> — แพ็กเกจมาตรฐาน 2,500 บาท</li>
  <li><strong>ตรวจคัดกรองโรคหัวใจ</strong> — 4,500 บาท</li>
  <li><strong>ตรวจคัดกรองมะเร็ง</strong> — สอบถามราคาเฉพาะรายการ</li>
  <li><strong>วัคซีนไข้หวัดใหญ่</strong> — 600 บาท</li>
  <li><strong>วัคซีน HPV</strong> — 1,800 บาท/เข็ม</li>
</ul>"
            }))
        }),

        (new("นัดหมายแพทย์", "appointment", PageType.Standard, "นัดหมายล่วงหน้า"), new() {
            new(CmsBlockType.Hero, J(new {
                headline = "นัดหมายแพทย์",
                subheadline = "กรอกข้อมูลเบื้องต้น เจ้าหน้าที่ติดต่อกลับยืนยันคิวภายใน 1 ชั่วโมง"
            })),
            new(CmsBlockType.ContactForm, J(new { headline = "แจ้งนัดหมาย", submitText = "ส่งคำขอนัด" }))
        }),

        (new("ติดต่อ", "contact", PageType.Standard, "ที่อยู่คลินิก เปิด-ปิด"), new() {
            new(CmsBlockType.RichText, J(new {
                content = @"<h2>📍 คลินิก · 🕐 เปิด จันทร์-เสาร์ 09:00-19:00</h2>
<p>โทร: 02-XXX-XXXX · ฉุกเฉิน: 086-XXX-XXXX</p>"
            })),
            new(CmsBlockType.Map, J(new { address = "กรุงเทพมหานคร ประเทศไทย" }))
        })
    };

    // ----- Generic Service business -----
    private static List<(PageMeta, List<BlockMeta>)> ServicePlan() => GeneralPlan();

    // ----- Construction / Contractor -----
    private static List<(PageMeta, List<BlockMeta>)> ConstructionPlan() => new()
    {
        (new("หน้าหลัก", "home", PageType.Landing, "บริการรับเหมาก่อสร้าง ครบวงจร"), new() {
            new(CmsBlockType.Hero, J(new {
                headline = "รับเหมาก่อสร้าง คุณภาพ ตรงเวลา",
                subheadline = "ทีมงานวิศวกร + ช่างมืออาชีพ · ทำงานตามแบบ ภายในงบ ครบกำหนด",
                ctaText = "ขอใบเสนอราคา", ctaUrl = "/quote"
            })),
            new(CmsBlockType.RichText, J(new {
                content = @"<h2>🏗️ ประเภทงานที่รับ</h2>
<ul>
  <li>สร้างบ้านพักอาศัย / ทาวน์เฮาส์ / อาคารพาณิชย์</li>
  <li>ปรับปรุง / ต่อเติม / รีโนเวท</li>
  <li>งานโครงสร้างเหล็ก / คอนกรีต</li>
  <li>งานออกแบบ + รับเหมาเบ็ดเสร็จ (Design + Build)</li>
</ul>"
            })),
            new(CmsBlockType.Testimonials, J(new {
                headline = "ผลงานจากลูกค้าจริง",
                testimonials = new[] {
                    new { quote = "งานเสร็จก่อนกำหนด คุณภาพดี", author = "คุณสุริยา", role = "บ้านพักอาศัย 2 ชั้น" },
                    new { quote = "ทีมงานสุภาพ ทำงานเป็นระบบ", author = "คุณนภา", role = "ต่อเติมร้านอาหาร" }
                }
            })),
            new(CmsBlockType.CallToAction, J(new {
                headline = "มีโครงการในใจ?", subheadline = "ปรึกษาฟรี ส่งภาพหน้างาน ตอบใบเสนอราคาภายใน 3 วัน",
                ctaText = "ขอใบเสนอราคาฟรี", ctaUrl = "/quote"
            }))
        }),
        (new("ผลงานที่ผ่านมา", "portfolio", PageType.Standard, "ตัวอย่างงานก่อสร้างของเรา"), new() {
            new(CmsBlockType.Hero, J(new { headline = "ผลงานที่ผ่านมา", subheadline = "ทุกโครงการคือเครื่องการันตีคุณภาพ" })),
            new(CmsBlockType.RichText, J(new {
                content = @"<h2>📸 ผลงานเด่น</h2>
<p>เพิ่มรูปผลงานของคุณตรงนี้ (ใช้ block Image หรือ Gallery จาก editor)</p>
<ul>
  <li><strong>บ้านเดี่ยว สไตล์โมเดิร์น</strong> — 2 ชั้น · 240 ตรม. · 8 เดือน</li>
  <li><strong>ปรับปรุงร้านกาแฟ</strong> — 80 ตรม. · 1 เดือน</li>
  <li><strong>สำนักงาน 3 ชั้น</strong> — 600 ตรม. · 14 เดือน</li>
</ul>"
            }))
        }),
        (new("ขอใบเสนอราคา", "quote", PageType.Standard, "ขอใบเสนอราคาฟรี"), new() {
            new(CmsBlockType.Hero, J(new {
                headline = "ขอใบเสนอราคา (ฟรี)",
                subheadline = "กรอกรายละเอียดงาน ทีมงานจะติดต่อสำรวจหน้างานภายใน 24 ชม."
            })),
            new(CmsBlockType.ContactForm, J(new { headline = "รายละเอียดโครงการ", submitText = "ส่งคำขอใบเสนอราคา" }))
        }),
        (new("ติดต่อ", "contact", PageType.Standard, "ติดต่อเรา"), new() {
            new(CmsBlockType.RichText, J(new {
                content = "<h2>📞 ติดต่อทีมงาน</h2><p>โทร: 02-XXX-XXXX · LINE: @construct</p>"
            })),
            new(CmsBlockType.Map, J(new { address = "กรุงเทพมหานคร ประเทศไทย" }))
        })
    };

    // ----- Real Estate -----
    private static List<(PageMeta, List<BlockMeta>)> RealEstatePlan() => new()
    {
        (new("หน้าหลัก", "home", PageType.Landing, "อสังหาฯ ที่ดิน บ้าน คอนโด"), new() {
            new(CmsBlockType.Hero, J(new {
                headline = "บ้าน / ที่ดิน / คอนโด ทำเลดี",
                subheadline = "นายหน้ามืออาชีพ ให้คำแนะนำตรงไปตรงมา ปิดดีลเร็ว",
                ctaText = "ดูรายการขาย", ctaUrl = "/listings"
            })),
            new(CmsBlockType.RichText, J(new {
                content = @"<h2>🏘️ บริการของเรา</h2>
<ul>
  <li>ซื้อ-ขาย-เช่า บ้าน คอนโด ที่ดิน</li>
  <li>ประเมินราคาทรัพย์สิน (ฟรี)</li>
  <li>ที่ปรึกษาด้านสินเชื่อ + กฎหมาย</li>
  <li>บริหารโครงการเช่า (สำหรับเจ้าของ)</li>
</ul>"
            })),
            new(CmsBlockType.Testimonials, J(new {
                headline = "ลูกค้าที่ปิดดีลแล้ว",
                testimonials = new[] {
                    new { quote = "ขายบ้านได้ภายใน 3 อาทิตย์ ราคาดี", author = "คุณวีระ" },
                    new { quote = "ตรงไปตรงมา ดูแลทุกขั้นตอนจบที่กรมที่ดิน", author = "คุณมาลี" }
                }
            }))
        }),
        (new("รายการขาย / เช่า", "listings", PageType.Standard, "อสังหาฯ ทั้งหมดที่กำลังขาย"), new() {
            new(CmsBlockType.Hero, J(new { headline = "รายการทรัพย์" })),
            new(CmsBlockType.RichText, J(new {
                content = @"<h2>🏠 ทรัพย์เด่น</h2>
<p>เพิ่มรายการทรัพย์จริงตรงนี้ (ใช้ block Image / Gallery / RichText)</p>
<ul>
  <li><strong>บ้านเดี่ยว ซอย XX</strong> — 4 ห้องนอน · 120 ตรว. · 8.5 ล้าน</li>
  <li><strong>คอนโด ใจกลางเมือง</strong> — 1 ห้องนอน · 35 ตรม. · 3.2 ล้าน</li>
  <li><strong>ที่ดิน ติดถนน</strong> — 200 ตรว. · 15 ล้าน</li>
</ul>"
            }))
        }),
        (new("นัดดูทรัพย์", "viewing", PageType.Standard, "นัดดูบ้าน คอนโด ที่ดิน"), new() {
            new(CmsBlockType.Hero, J(new { headline = "นัดดูทรัพย์", subheadline = "เลือกวันเวลาที่สะดวก เราจะพาชมทรัพย์ตามที่ระบุ" })),
            new(CmsBlockType.ContactForm, J(new { headline = "นัดหมาย", submitText = "ส่งคำขอนัด" }))
        }),
        (new("ติดต่อ", "contact", PageType.Standard, "ติดต่อนายหน้า"), new() {
            new(CmsBlockType.RichText, J(new { content = "<p>📞 02-XXX-XXXX · LINE: @realestate</p>" })),
            new(CmsBlockType.Map, J(new { address = "กรุงเทพมหานคร ประเทศไทย" }))
        })
    };

    // ----- Technology / SaaS / Software house -----
    private static List<(PageMeta, List<BlockMeta>)> TechnologyPlan() => new()
    {
        (new("หน้าหลัก", "home", PageType.Landing, "ซอฟต์แวร์ที่ทำให้ธุรกิจคุณเติบโต"), new() {
            new(CmsBlockType.Hero, J(new {
                headline = "ซอฟต์แวร์ที่ลูกค้ารัก",
                subheadline = "ออกแบบมาเพื่อ SME ไทย · ใช้งานง่าย · ราคาเป็นมิตร · ทีมซัพพอร์ตคนไทย",
                ctaText = "ทดลองใช้ฟรี 14 วัน", ctaUrl = "/signup"
            })),
            new(CmsBlockType.RichText, J(new {
                content = @"<h2>✨ ฟีเจอร์เด่น</h2>
<ul>
  <li>⚡ ใช้งานได้ทันที — ไม่ต้องติดตั้ง ไม่ต้องอัพเดท</li>
  <li>🔒 ปลอดภัยมาตรฐานสากล (SSL, encryption, daily backup)</li>
  <li>📱 ใช้ได้ทุกอุปกรณ์ — มือถือ แท็บเล็ต คอม</li>
  <li>🤝 ทีมซัพพอร์ตภาษาไทย ตอบ LINE / โทร / อีเมล</li>
</ul>"
            })),
            new(CmsBlockType.PricingTable, J(new {
                headline = "แพ็กเกจสำหรับทุกขนาดธุรกิจ",
                plans = new[] {
                    new { name = "Starter", price = "ฟรี",
                          features = new[] { "ผู้ใช้ 1 คน", "เก็บข้อมูล 100 รายการ/เดือน", "ซัพพอร์ตอีเมล" } },
                    new { name = "Business", price = "฿990/เดือน",
                          features = new[] { "ผู้ใช้ 10 คน", "ไม่จำกัดข้อมูล", "ซัพพอร์ต LINE + โทร", "Export Excel" } },
                    new { name = "Enterprise", price = "ติดต่อขอราคา",
                          features = new[] { "ไม่จำกัดผู้ใช้", "API integration", "Account Manager เฉพาะคุณ", "SLA 99.9%" } }
                }
            })),
            new(CmsBlockType.Testimonials, J(new {
                headline = "ลูกค้าที่ใช้แล้วบอกต่อ",
                testimonials = new[] {
                    new { quote = "ลดเวลาทำงานได้ครึ่งหนึ่ง", author = "คุณวรพล", role = "ร้านขายส่ง" },
                    new { quote = "ใช้งานง่ายมาก พนักงานเรียนรู้ได้ในวันเดียว", author = "คุณวรรณา", role = "ร้านอาหาร" }
                }
            })),
            new(CmsBlockType.Faq, J(new {
                headline = "คำถามที่พบบ่อย",
                items = new[] {
                    new { q = "มีให้ทดลองใช้ฟรีไหม?", a = "ทดลองใช้ฟีเจอร์ครบทั้งหมด 14 วัน ไม่ต้องใส่บัตรเครดิต" },
                    new { q = "ยกเลิกได้เมื่อไหร่?", a = "ยกเลิกได้ทุกเวลา ไม่มีค่าธรรมเนียม" },
                    new { q = "ข้อมูลของฉันปลอดภัยไหม?", a = "Encrypt ทั้งหมด + backup ทุกวัน + เซิร์ฟเวอร์ในไทย" }
                }
            }))
        }),
        (new("ฟีเจอร์", "features", PageType.Standard, "ฟีเจอร์ทั้งหมด"), new() {
            new(CmsBlockType.Hero, J(new { headline = "ฟีเจอร์ทั้งหมด" })),
            new(CmsBlockType.RichText, J(new {
                content = @"<p>อธิบายแต่ละฟีเจอร์ของผลิตภัณฑ์ของคุณตรงนี้ (ใช้ block RichText / Image / Video เพิ่มได้)</p>"
            }))
        }),
        (new("ราคา", "pricing", PageType.Standard, "แพ็กเกจราคา"), new() {
            new(CmsBlockType.Hero, J(new { headline = "ราคาโปร่งใส ไม่มีค่าใช้จ่ายแฝง" })),
            new(CmsBlockType.PricingTable, J(new {
                plans = new[] {
                    new { name = "Starter", price = "ฟรี", features = new[] { "ผู้ใช้ 1 คน", "100 รายการ/เดือน" } },
                    new { name = "Business", price = "฿990/เดือน", features = new[] { "ผู้ใช้ 10 คน", "ไม่จำกัดข้อมูล" } },
                    new { name = "Enterprise", price = "ติดต่อ", features = new[] { "ไม่จำกัดผู้ใช้", "API" } }
                }
            }))
        }),
        (new("ติดต่อขาย / เดโม", "contact", PageType.Standard, "นัดเดโม / ขอใบเสนอราคา"), new() {
            new(CmsBlockType.Hero, J(new { headline = "อยากดูเดโม?", subheadline = "ทีมขายจะติดต่อภายใน 1 วันทำการ" })),
            new(CmsBlockType.ContactForm, J(new { headline = "นัดเดโม / สอบถามราคา", submitText = "ส่ง" }))
        })
    };

    // ----- Education / Training / School -----
    private static List<(PageMeta, List<BlockMeta>)> EducationPlan() => new()
    {
        (new("หน้าหลัก", "home", PageType.Landing, "หลักสูตร / สถาบันสอน"), new() {
            new(CmsBlockType.Hero, J(new {
                headline = "เรียนรู้กับผู้เชี่ยวชาญ",
                subheadline = "หลักสูตรครอบคลุม · ผู้สอนมืออาชีพ · ใบประกาศนียบัตรหลังเรียนจบ",
                ctaText = "ดูหลักสูตรทั้งหมด", ctaUrl = "/courses"
            })),
            new(CmsBlockType.RichText, J(new {
                content = @"<h2>🎓 หลักสูตรเปิดสอน</h2>
<ul>
  <li>หลักสูตรพื้นฐาน — สำหรับผู้เริ่มต้น</li>
  <li>หลักสูตรขั้นสูง — สำหรับมืออาชีพ</li>
  <li>คอร์สเฉพาะทาง — ออกแบบให้บริษัท / ทีมงาน</li>
</ul>
<h2>🏆 ทำไมเลือกเรา</h2>
<ul>
  <li>ผู้สอนมีประสบการณ์จริงในอุตสาหกรรม 10+ ปี</li>
  <li>สอนแบบ workshop ลงมือทำจริง ไม่ใช่แค่บรรยาย</li>
  <li>มี community ของศิษย์เก่าให้ปรึกษาต่อเนื่อง</li>
</ul>"
            })),
            new(CmsBlockType.Testimonials, J(new {
                headline = "ความคิดเห็นจากผู้เรียน",
                testimonials = new[] {
                    new { quote = "เปลี่ยนชีวิตการทำงานเลย", author = "คุณเจมส์", role = "ศิษย์รุ่น 8" },
                    new { quote = "ได้งานตรงสายภายใน 3 เดือนหลังจบคอร์ส", author = "คุณดาว", role = "ศิษย์รุ่น 12" }
                }
            }))
        }),
        (new("หลักสูตร", "courses", PageType.Standard, "หลักสูตรทั้งหมด"), new() {
            new(CmsBlockType.Hero, J(new { headline = "หลักสูตรทั้งหมด" })),
            new(CmsBlockType.PricingTable, J(new {
                headline = "เลือกหลักสูตรที่ใช่",
                plans = new[] {
                    new { name = "พื้นฐาน", price = "฿4,900", features = new[] { "เรียน 8 ชม.", "เอกสารประกอบ", "ใบประกาศ" } },
                    new { name = "ขั้นกลาง", price = "฿9,900", features = new[] { "เรียน 16 ชม.", "workshop", "ที่ปรึกษา 1:1 1 ครั้ง", "ใบประกาศ" } },
                    new { name = "ขั้นสูง", price = "฿18,900", features = new[] { "เรียน 32 ชม.", "Project จริง", "Mentor 3 เดือน", "ใบประกาศ + แนะนำงาน" } }
                }
            }))
        }),
        (new("สมัครเรียน", "register", PageType.Standard, "ลงทะเบียนเรียน"), new() {
            new(CmsBlockType.Hero, J(new { headline = "สมัครเรียน", subheadline = "เลือกหลักสูตร + รอบเรียน → กรอกข้อมูล → ชำระเงิน" })),
            new(CmsBlockType.ContactForm, J(new { headline = "ลงทะเบียน", submitText = "ส่งใบสมัคร" }))
        }),
        (new("ติดต่อ", "contact", PageType.Standard, "ติดต่อสถาบัน"), new() {
            new(CmsBlockType.RichText, J(new { content = "<p>📞 02-XXX-XXXX · LINE: @school · 🕐 จ-ส 9:00-18:00</p>" })),
            new(CmsBlockType.Map, J(new { address = "กรุงเทพมหานคร ประเทศไทย" }))
        })
    };

    // ----- Transportation / Logistics -----
    private static List<(PageMeta, List<BlockMeta>)> TransportationPlan() => new()
    {
        (new("หน้าหลัก", "home", PageType.Landing, "บริการขนส่ง โลจิสติกส์"), new() {
            new(CmsBlockType.Hero, J(new {
                headline = "ขนส่งทั่วไทย ตรงเวลา ปลอดภัย",
                subheadline = "รับ-ส่งทั่วประเทศ · ติดตามสถานะออนไลน์ · ประกันความเสียหาย",
                ctaText = "ขอราคา / จองรถ", ctaUrl = "/quote"
            })),
            new(CmsBlockType.RichText, J(new {
                content = @"<h2>🚚 บริการของเรา</h2>
<ul>
  <li><strong>ส่งของพัสดุ</strong> — ทั่วประเทศ · 1-3 วัน</li>
  <li><strong>รถบรรทุก 4 ล้อ / 6 ล้อ / 10 ล้อ</strong> — เหมารายเที่ยว / รายวัน / รายเดือน</li>
  <li><strong>ขนย้ายบ้าน / สำนักงาน</strong> — ครบทีมงาน รถ + คนยก</li>
  <li><strong>ขนส่งสินค้าเย็น</strong> — ห้องเย็นอุณหภูมิควบคุม</li>
</ul>"
            })),
            new(CmsBlockType.Faq, J(new {
                headline = "คำถามที่พบบ่อย",
                items = new[] {
                    new { q = "ส่งของเสียหายต้องทำอย่างไร?", a = "เรามีประกันสูงสุด 100,000 บาทต่อเที่ยว แจ้งภายใน 24 ชม." },
                    new { q = "มี COD ไหม?", a = "รองรับเก็บเงินปลายทาง คืนยอดภายใน 2 วันทำการ" },
                    new { q = "เช็คสถานะของได้ที่ไหน?", a = "ผ่านระบบ tracking ออนไลน์ — กรอกเลข tracking" }
                }
            }))
        }),
        (new("บริการ", "services", PageType.Standard, "บริการขนส่งทั้งหมด"), new() {
            new(CmsBlockType.Hero, J(new { headline = "บริการของเรา" })),
            new(CmsBlockType.PricingTable, J(new {
                headline = "ราคาเริ่มต้น",
                plans = new[] {
                    new { name = "พัสดุ", price = "เริ่ม ฿35", features = new[] { "ในเขตกรุงเทพ", "1 วัน", "ไม่เกิน 5 กก." } },
                    new { name = "รถ 4 ล้อ", price = "เริ่ม ฿1,500/เที่ยว", features = new[] { "บรรทุกได้ 1 ตัน", "คนขับ + คนยก 1 คน" } },
                    new { name = "รถ 6 ล้อ", price = "เริ่ม ฿3,500/เที่ยว", features = new[] { "บรรทุกได้ 4 ตัน", "คนขับ + คนยก 2 คน" } }
                }
            }))
        }),
        (new("ขอราคา / จองรถ", "quote", PageType.Standard, "ขอราคาขนส่ง"), new() {
            new(CmsBlockType.Hero, J(new { headline = "ขอราคา / จองรถ", subheadline = "ตอบกลับภายใน 30 นาที" })),
            new(CmsBlockType.ContactForm, J(new { headline = "รายละเอียดงานขนส่ง", submitText = "ส่งคำขอ" }))
        }),
        (new("ติดต่อ", "contact", PageType.Standard, "ติดต่อ"), new() {
            new(CmsBlockType.RichText, J(new {
                content = "<p>📞 02-XXX-XXXX · 📱 081-XXX-XXXX (24 ชม.) · LINE: @logistics</p>"
            })),
            new(CmsBlockType.Map, J(new { address = "กรุงเทพมหานคร ประเทศไทย" }))
        })
    };

    // ----- Hotel / Resort / Homestay -----
    private static List<(PageMeta, List<BlockMeta>)> HotelPlan() => new()
    {
        (new("หน้าหลัก", "home", PageType.Landing, "โรงแรม / ที่พัก"), new() {
            new(CmsBlockType.Hero, J(new {
                headline = "พักผ่อนเหมือนกลับบ้าน",
                subheadline = "ห้องสะอาด บรรยากาศดี ทำเลใจกลางเมือง · จองตรงรับส่วนลด 10%",
                ctaText = "จองห้องพัก", ctaUrl = "/booking"
            })),
            new(CmsBlockType.RichText, J(new {
                content = @"<h2>🏨 ห้องพักของเรา</h2>
<ul>
  <li><strong>Standard Room</strong> — 1 เตียง 6 ฟุต · ห้องน้ำในตัว · WiFi</li>
  <li><strong>Deluxe Room</strong> — 1 เตียงคิงไซส์ · วิวเมือง · เครื่องชงกาแฟ</li>
  <li><strong>Suite</strong> — ห้องนอน + นั่งเล่นแยก · อ่างอาบน้ำ · มินิบาร์</li>
</ul>
<h2>🎁 สิ่งอำนวยความสะดวก</h2>
<ul>
  <li>WiFi ฟรีทุกพื้นที่ · อาหารเช้าฟรี · ที่จอดรถ · 24/7 ฟร้อนท์</li>
  <li>สระว่ายน้ำ · ฟิตเนส · สปา · บริการรถรับส่งสนามบิน</li>
</ul>"
            })),
            new(CmsBlockType.Testimonials, J(new {
                headline = "รีวิวจากแขก",
                testimonials = new[] {
                    new { quote = "ห้องสะอาดมาก พนักงานน่ารัก จะกลับมาแน่นอน", author = "Sarah J.", role = "อังกฤษ" },
                    new { quote = "ทำเลดีมาก เดินไปกินข้าวได้รอบ ๆ", author = "คุณภัทร", role = "กรุงเทพฯ" }
                }
            }))
        }),
        (new("ห้องพัก", "rooms", PageType.Standard, "ห้องพักและราคา"), new() {
            new(CmsBlockType.Hero, J(new { headline = "ห้องพัก" })),
            new(CmsBlockType.PricingTable, J(new {
                headline = "ราคาห้องพัก (ต่อคืน)",
                plans = new[] {
                    new { name = "Standard", price = "฿1,500", features = new[] { "เตียง 6 ฟุต", "อาหารเช้า 2 ท่าน", "WiFi" } },
                    new { name = "Deluxe", price = "฿2,500", features = new[] { "คิงไซส์", "วิวเมือง", "อาหารเช้า 2 ท่าน", "เครื่องชงกาแฟ" } },
                    new { name = "Suite", price = "฿4,500", features = new[] { "ห้องนั่งเล่นแยก", "อ่างอาบน้ำ", "มินิบาร์", "Late checkout" } }
                }
            }))
        }),
        (new("จองห้องพัก", "booking", PageType.Standard, "จองห้อง"), new() {
            new(CmsBlockType.Hero, J(new { headline = "จองห้องพัก", subheadline = "จองตรงรับส่วนลด 10% — ไม่มีค่าธรรมเนียม" })),
            new(CmsBlockType.ContactForm, J(new { headline = "จองห้อง", submitText = "ส่งคำขอจอง" }))
        }),
        (new("ติดต่อ / ที่ตั้ง", "contact", PageType.Standard, "ที่ตั้ง"), new() {
            new(CmsBlockType.RichText, J(new {
                content = "<p>📞 02-XXX-XXXX · 24 ชม. · LINE: @hotel</p>"
            })),
            new(CmsBlockType.Map, J(new { address = "กรุงเทพมหานคร ประเทศไทย" }))
        })
    };

    // ----- Manufacturing / Factory / OEM -----
    private static List<(PageMeta, List<BlockMeta>)> ManufacturingPlan() => new()
    {
        (new("หน้าหลัก", "home", PageType.Landing, "โรงงานผลิต OEM"), new() {
            new(CmsBlockType.Hero, J(new {
                headline = "โรงงานผลิตคุณภาพ มาตรฐานสากล",
                subheadline = "รับ OEM / ODM · ผลิตตามแบบ · ส่งออกทั่วโลก · ISO 9001",
                ctaText = "ส่งแบบขอใบเสนอราคา", ctaUrl = "/rfq"
            })),
            new(CmsBlockType.RichText, J(new {
                content = @"<h2>🏭 ความสามารถในการผลิต</h2>
<ul>
  <li>กำลังการผลิต — 100,000 ชิ้น/เดือน</li>
  <li>โรงงานพื้นที่ 5,000 ตรม. · เครื่องจักรอัตโนมัติ</li>
  <li>QC ทุกขั้นตอน · ส่งของตรงเวลา 99%</li>
  <li>มาตรฐาน ISO 9001 · GMP · มอก.</li>
</ul>
<h2>📦 ประเภทสินค้าที่ผลิต</h2>
<ul>
  <li>แก้ไขรายการให้ตรงกับโรงงานของคุณ</li>
  <li>สินค้าหมวด A / B / C</li>
  <li>บรรจุภัณฑ์พร้อมโลโก้ลูกค้า</li>
</ul>"
            })),
            new(CmsBlockType.Testimonials, J(new {
                headline = "ลูกค้าที่ไว้วางใจ",
                testimonials = new[] {
                    new { quote = "ส่งของตรงเวลา ทุก lot คุณภาพคงที่", author = "ABC Trading", role = "5 ปีติดต่อกัน" },
                    new { quote = "ทีมงานช่วยพัฒนาสูตรให้เราด้วย", author = "XYZ Brand", role = "" }
                }
            }))
        }),
        (new("ความสามารถ", "capabilities", PageType.Standard, "ความสามารถในการผลิต"), new() {
            new(CmsBlockType.Hero, J(new { headline = "ความสามารถในการผลิต" })),
            new(CmsBlockType.RichText, J(new {
                content = @"<h2>เครื่องจักร + กำลังผลิต</h2>
<p>(แก้ไขรายละเอียดเครื่องจักร · กำลังผลิตต่อเดือน · มาตรฐาน)</p>"
            }))
        }),
        (new("ส่งแบบขอใบเสนอราคา (RFQ)", "rfq", PageType.Standard, "RFQ"), new() {
            new(CmsBlockType.Hero, J(new {
                headline = "Request for Quote (RFQ)",
                subheadline = "ส่งแบบ / spec / จำนวน — ทีมงานติดต่อกลับภายใน 2 วันทำการ"
            })),
            new(CmsBlockType.ContactForm, J(new { headline = "รายละเอียดผลิตภัณฑ์", submitText = "ส่ง RFQ" }))
        }),
        (new("ติดต่อ", "contact", PageType.Standard, "ติดต่อโรงงาน"), new() {
            new(CmsBlockType.RichText, J(new {
                content = "<p>📞 02-XXX-XXXX · 📧 sales@factory.com · LINE: @factory</p>"
            })),
            new(CmsBlockType.Map, J(new { address = "นิคมอุตสาหกรรม ประเทศไทย" }))
        })
    };
}
