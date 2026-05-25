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
            IndustryType.Retail or IndustryType.Ecommerce => RetailPlan(),
            IndustryType.Beauty => BeautyPlan(),
            IndustryType.Healthcare => HealthcarePlan(),
            IndustryType.Service or IndustryType.Freelance => ServicePlan(),
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
        (new("หน้าหลัก", "home", PageType.Home, "เว็บไซต์อย่างเป็นทางการ — บริการครบวงจร"), new() {
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

        (new("ติดต่อเรา", "contact", PageType.Contact, "ที่อยู่ เบอร์โทร และแบบฟอร์มติดต่อ"), new() {
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
        (new("หน้าหลัก", "home", PageType.Home, "ร้านอาหารบรรยากาศดี เมนูเด็ด ๆ"), new() {
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

        (new("ติดต่อเรา", "contact", PageType.Contact, "ที่ตั้ง โทรศัพท์ เวลาเปิด-ปิด"), new() {
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
        (new("หน้าหลัก", "home", PageType.Home, "ร้านค้าออนไลน์ สินค้าคุณภาพดี ส่งไว"), new() {
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

        (new("ติดต่อ", "contact", PageType.Contact, "ติดต่อร้านค้า"), new() {
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
        (new("หน้าหลัก", "home", PageType.Home, "บริการความงาม สปา ทำเล็บ"), new() {
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

        (new("ติดต่อเรา", "contact", PageType.Contact, "ที่ตั้งร้าน เปิด-ปิด"), new() {
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
        (new("หน้าหลัก", "home", PageType.Home, "คลินิกของเรา ดูแลทุกอาการ"), new() {
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

        (new("ติดต่อ", "contact", PageType.Contact, "ที่อยู่คลินิก เปิด-ปิด"), new() {
            new(CmsBlockType.RichText, J(new {
                content = @"<h2>📍 คลินิก · 🕐 เปิด จันทร์-เสาร์ 09:00-19:00</h2>
<p>โทร: 02-XXX-XXXX · ฉุกเฉิน: 086-XXX-XXXX</p>"
            })),
            new(CmsBlockType.Map, J(new { address = "กรุงเทพมหานคร ประเทศไทย" }))
        })
    };

    // ----- Generic Service business -----
    private static List<(PageMeta, List<BlockMeta>)> ServicePlan() => GeneralPlan();
}
