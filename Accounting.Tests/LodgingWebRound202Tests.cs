using System.Globalization;
using System.Text;
using System.Text.RegularExpressions;
using Accounting.Data;
using Accounting.Helpers;
using Accounting.Models.Enums;
using Accounting.Services.Implementations.Cms;
using Xunit;

namespace Accounting.Tests;

/// <summary>รอบ 202 ทีม LW — เว็บที่พักอ้างข้อมูลห้องจริง + จองเอง (W-02/W-03/W-05 · คำตัดสินข้อ 117/118)
/// ทุกกลุ่มมีสองทิศ: ของที่ต้องเปลี่ยนเปลี่ยน · ของที่ถูกอยู่แล้ว (บล็อกที่เจ้าของแก้ · ที่พักที่ผูกแล้ว) ไม่ถูกแตะ.
/// เรพไม่มี PostgreSQL ในเทสต์ ⇒ ล็อก "รูป" ของคำสั่ง migration ที่ระบบรันจริง (ถอด base64 ในคำสั่งกลับมาเทียบ snapshot ทุกไบต์)</summary>
public class LodgingWebRound202Tests
{
    // ───────────── W-02 / ข้อ 117: เว็บใหม่ได้บล็อกข้อมูลสด ไม่ใช่ราคา seed ─────────────

    private static List<Accounting.Models.Entities.SitePage> Hotel() =>
        CmsSiteTemplateSeeder.BuildSeed(Guid.NewGuid(), Guid.NewGuid(), IndustryType.Hotel, "t", SiteType.Booking);

    private static string Decode(string json) =>
        Regex.Replace(json, @"\\u([0-9a-fA-F]{4})", m => ((char)Convert.ToInt32(m.Groups[1].Value, 16)).ToString());

    [Fact]
    public void เว็บที่พักใหม่_หน้าแรกและหน้า_rooms_ใช้บล็อก_LodgingRooms()
    {
        var pages = Hotel();
        foreach (var slug in new[] { "home", "rooms" })
        {
            var page = pages.Single(p => p.Slug == slug);
            Assert.Single(page.Blocks, b => b.BlockType == CmsBlockType.LodgingRooms);
        }
        Assert.DoesNotContain(pages.SelectMany(p => p.Blocks), b => b.BlockType == CmsBlockType.PricingTable);
    }

    [Fact]
    public void เว็บที่พักใหม่_ไม่มีราคาห้อง_seed_และไม่มีจำนวนประเภท_ห้อง_seed()
    {
        var text = Decode(string.Join("\n", Hotel().SelectMany(p => p.Blocks).Select(b => b.ConfigJson)));
        // ชื่อห้องไม่ล็อก — FAQ เตียงเสริมพูดถึง "Deluxe/Suite" ซึ่งเป็นกติกาบริการ ไม่ใช่รายการราคา
        foreach (var r in LodgingSeedDefaults.RoomTypes)
            Assert.DoesNotContain(LodgingSeedDefaults.Baht(r.Rate), text);
        Assert.DoesNotContain($"{LodgingSeedDefaults.RoomTypes.Length} ประเภท", text);
        Assert.DoesNotContain($"{LodgingSeedDefaults.TotalUnits} ห้อง", text);
    }

    [Fact]
    public void snapshot_รุ่นเดิม_ยังเป็นรายการห้องและราคา_seed_ครบ_ถ้าไม่ใช่_migration_จะจับอะไรไม่ได้()
    {
        var legacy = Decode(CmsSiteTemplateSeeder.LegacyHotelRoomsRichTextConfig() + CmsSiteTemplateSeeder.LegacyHotelPricingTableConfig());
        foreach (var r in LodgingSeedDefaults.RoomTypes)
        {
            Assert.Contains(r.Name, legacy);
            Assert.Contains(LodgingSeedDefaults.Baht(r.Rate), legacy);
        }
        Assert.Contains($"{LodgingSeedDefaults.TotalUnits} ห้อง", Decode(CmsSiteTemplateSeeder.LegacyHotelRoomsHeroConfig()));
    }

    [Fact]
    public void เว็บใหม่ไม่มีบล็อกที่ตรง_snapshot_รุ่นเดิม()
    {
        var configs = Hotel().SelectMany(p => p.Blocks).Select(b => b.ConfigJson).ToHashSet(StringComparer.Ordinal);
        Assert.False(configs.Contains(CmsSiteTemplateSeeder.LegacyHotelRoomsRichTextConfig()));
        Assert.False(configs.Contains(CmsSiteTemplateSeeder.LegacyHotelPricingTableConfig()));
        Assert.False(configs.Contains(CmsSiteTemplateSeeder.LegacyHotelRoomsHeroConfig()));
        Assert.True(configs.Contains(CmsSiteTemplateSeeder.HotelLiveRoomsConfig()));
        Assert.True(configs.Contains(CmsSiteTemplateSeeder.HotelRoomsHeroConfig()));
    }

    // ───────────── migration เว็บเดิม: แทนเฉพาะที่ตรงทุกไบต์ ─────────────

    private static readonly Regex B64 = new(@"decode\('([A-Za-z0-9+/=]+)','base64'\)");

    private static HashSet<string> Literals(string sql) =>
        B64.Matches(sql).Select(m => Encoding.UTF8.GetString(Convert.FromBase64String(m.Groups[1].Value))).ToHashSet(StringComparer.Ordinal);

    [Fact]
    public void migration_เทียบ_snapshot_ที่สร้างจากฟังก์ชัน_seed_ตัวเดียวกัน_ทุกไบต์()
    {
        var lits = Literals(LodgingSiteSeedMigration.RoomBlocksSql());
        Assert.True(lits.Contains(CmsSiteTemplateSeeder.LegacyHotelRoomsRichTextConfig()));
        Assert.True(lits.Contains(CmsSiteTemplateSeeder.LegacyHotelPricingTableConfig()));
        Assert.True(lits.Contains(CmsSiteTemplateSeeder.LegacyHotelRoomsHeroConfig()));
        // ปลายทาง = config ของเว็บใหม่ตัวเดียวกัน
        Assert.True(lits.Contains(CmsSiteTemplateSeeder.HotelLiveRoomsConfig()));
        Assert.True(lits.Contains(CmsSiteTemplateSeeder.HotelRoomsHeroConfig()));
    }

    [Theory]
    [InlineData(" ")]          // ช่องว่างท้าย
    [InlineData("x")]          // แก้ตัวอักษรเดียว
    public void บล็อกที่เจ้าของแก้แม้ตัวเดียว_ไม่อยู่ในชุดที่_migration_แทน(string edit)
    {
        var lits = Literals(LodgingSiteSeedMigration.RoomBlocksSql());
        foreach (var snap in new[] { CmsSiteTemplateSeeder.LegacyHotelRoomsRichTextConfig(), CmsSiteTemplateSeeder.LegacyHotelPricingTableConfig(), CmsSiteTemplateSeeder.LegacyHotelRoomsHeroConfig() })
        {
            Assert.True(lits.Contains(snap));                                            // ต้นฉบับ seed = ถูกแทน
            Assert.False(lits.Contains(snap + edit));                               // แก้ท้าย = ไม่แตะ
            Assert.False(lits.Contains(snap[..(snap.Length - 2)] + edit + snap[(snap.Length - 2)..]));   // แก้กลาง = ไม่แตะ
        }
    }

    [Fact]
    public void migration_เทียบแบบเท่ากันตรงตัวเท่านั้น_ไม่มีการเทียบหลวม()
    {
        var sql = LodgingSiteSeedMigration.RoomBlocksSql();
        foreach (var loose in new[] { "LIKE", "ILIKE", "trim(", "lower(", "jsonb", "::json", "~" })
            Assert.DoesNotContain(loose, sql, StringComparison.OrdinalIgnoreCase);
        Assert.Contains("b.\"ConfigJson\" = convert_from(", sql);
    }

    [Fact]
    public void migration_ไม่แตะบล็อกที่ซ่อน_มีคำแปล_หรือหน้าที่มีบล็อกสดแล้ว_และเฉพาะเว็บที่พักของบริษัทเดียวกัน()
    {
        var sql = LodgingSiteSeedMigration.RoomBlocksSql();
        Assert.Contains("b.\"IsVisible\" = true", sql);
        Assert.Contains("\"PageBlockTranslations\"", sql);
        Assert.Contains($"e.\"BlockType\" = {(int)CmsBlockType.LodgingRooms})", sql);
        Assert.Contains($"s.\"IndustryType\" = {(int)IndustryType.Hotel}", sql);
        Assert.Contains("p.\"CompanyId\" = b.\"CompanyId\"", sql);
        Assert.Contains("s.\"CompanyId\" = p.\"CompanyId\"", sql);
        // ตัวแรกต่อหน้า → บล็อกสด · ที่เหลือ → ซ่อน (ไม่ลบ)
        Assert.Contains("PARTITION BY b.\"PageId\"", sql);
        Assert.Contains("ELSE false END", sql);
        Assert.DoesNotContain("DELETE", sql, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public void migration_ล็อก_advisory_คีย์คงที่ข้ามเครื่อง_และไม่มีวงเล็บปีกกาให้_ExecuteSqlRaw_ตีความ()
    {
        var key = AdvisoryLockKey.For("db-migration", "lodging-site-seed-r202").ToString(CultureInfo.InvariantCulture);
        Assert.Equal(key, LodgingSiteSeedMigration.LockKey);
        foreach (var sql in new[] { LodgingSiteSeedMigration.RoomBlocksSql(), LodgingSiteSeedMigration.AutoSeedServiceCleanupSql() })
        {
            Assert.StartsWith($"DO $mig$ BEGIN PERFORM pg_advisory_xact_lock({key}); ", sql);
            Assert.DoesNotContain("{", sql);
            Assert.DoesNotContain("}", sql);
        }
    }

    [Fact]
    public void migration_ทั้งสองคำสั่งถูกต่อเข้าเส้นหลักที่รันตอนบูต()
    {
        var all = DatabaseMigrationHelper.GetAlterStatements();
        Assert.Contains(LodgingSiteSeedMigration.RoomBlocksSql(), all);
        Assert.Contains(LodgingSiteSeedMigration.AutoSeedServiceCleanupSql(), all);
    }

    // ───────────── W-03: ล้างบริการ ฿0 ที่ GET สาธารณะเคยสร้าง — เฉพาะแถวที่ไม่มีใครใช้/ไม่มีใครแก้ ─────────────

    [Fact]
    public void ล้างบริการ_auto_seed_เฉพาะเว็บที่พัก_ไม่มีการจอง_ไม่เคยแก้_แบบ_soft()
    {
        var sql = LodgingSiteSeedMigration.AutoSeedServiceCleanupSql();
        Assert.True(Literals(sql).Contains("auto-seed"));
        Assert.Contains("v.\"UpdatedBy\" IS NULL", sql);
        Assert.Contains("NOT EXISTS (SELECT 1 FROM \"SiteBookings\" k WHERE k.\"BookingServiceId\" = v.\"Id\" AND k.\"CompanyId\" = v.\"CompanyId\")", sql);
        Assert.Contains($"s.\"IndustryType\" = {(int)IndustryType.Hotel}", sql);
        Assert.Contains("s.\"CompanyId\" = v.\"CompanyId\"", sql);
        Assert.Contains("SET \"IsDeleted\" = true", sql);
        Assert.DoesNotContain("DELETE", sql, StringComparison.OrdinalIgnoreCase);
    }

    // ───────────── W-05 / ข้อ 118: ไม่สร้างที่พักแห่งที่สอง ─────────────

    [Fact]
    public void seed_ที่พัก_บริษัทมีที่พักไม่ผูกเว็บ_ไม่สร้าง_เสนอผูกที่พักเดิม()
    {
        var (o, msg) = LodgingSeedDecision.Decide(false, new[] { "บ้านริมน้ำ" }, null);
        Assert.Equal(LodgingSeedOutcome.ExistingUnlinked, o);
        Assert.Contains("บ้านริมน้ำ", msg);
        Assert.Contains("ผูก", msg);
    }

    [Fact]
    public void seed_ที่พัก_มีที่พักไม่ผูก_ชนะด่านโควตา_ไม่สร้างแม้มี_add_on()
    {
        Assert.Equal(LodgingSeedOutcome.ExistingUnlinked, LodgingSeedDecision.Decide(false, new[] { "A", "B", "C", "D" }, null).Outcome);
        var (_, msg) = LodgingSeedDecision.Decide(false, new[] { "A", "B", "C", "D" }, "ติดด่าน");
        Assert.Contains("อีก 1 แห่ง", msg);
    }

    [Fact]
    public void seed_ที่พัก_ติดด่านที่พักหลายแห่ง_บอกเหตุผล_ไม่สร้าง()
    {
        var (o, msg) = LodgingSeedDecision.Decide(false, Array.Empty<string>(), "เปิดที่พักได้ 1 แห่งในแพ็กเกจปัจจุบัน");
        Assert.Equal(LodgingSeedOutcome.QuotaBlocked, o);
        Assert.Contains("เปิดที่พักได้ 1 แห่ง", msg);
    }

    [Fact]
    public void seed_ที่พัก_ทิศตรงข้าม_บริษัทใหม่สร้างได้_และเว็บที่ผูกแล้วไม่แตะ()
    {
        Assert.Equal((LodgingSeedOutcome.Created, (string?)null), LodgingSeedDecision.Decide(false, Array.Empty<string>(), null));
        Assert.Equal((LodgingSeedOutcome.AlreadyBound, (string?)null), LodgingSeedDecision.Decide(true, new[] { "อื่น" }, "ติดด่าน"));
    }

    [Fact]
    public void ด่านที่พักหลายแห่ง_มีรหัสกฎเดียวกับที่หน้าเว็บอ่าน()
    {
        Assert.Equal("ADDON-REQUIRED:lodging.multi-property", LodgingPropertyQuota.RuleCode);
    }

    [Fact]
    public void enum_LodgingRooms_ต่อท้าย_ไม่เลื่อนเลขเดิม()
    {
        Assert.Equal(27, (int)CmsBlockType.LodgingRooms);
        Assert.Equal(26, (int)CmsBlockType.CategoryList);
        Assert.Equal(99, (int)CmsBlockType.Custom);
    }
}
