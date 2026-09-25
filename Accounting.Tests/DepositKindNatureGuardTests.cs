using Accounting.Data;
using Accounting.Helpers;
using Accounting.Models.Entities;
using Accounting.Models.Enums;
using Xunit;

namespace Accounting.Tests;

/// <summary>
/// รอบ 194 ฝ่ายค้านถดถอย/ความปลอดภัย (<c>erp-review/2026-09-25/review194-regsec.md</c>) — C1 · C3 · P6 · ล็อกสองครึ่ง:
/// <list type="bullet">
/// <item><b>ครึ่งแรก (ของที่ถูกอยู่แล้วไม่ถูกแตะ)</b>: ประเภทค่าห้องที่เป็นราคา · ค่าเริ่มต้น ADVANCE · เงินประกันที่ผู้ใช้เลือกบนใบเอง ·
/// เงินประกันของการจองที่ใบยังมีผล — ตัดสินเหมือนเดิมทุกค่า</item>
/// <item><b>ครึ่งหลัง (ที่เคยพัง)</b>: C1 เงินประกันกลายเป็นมัดจำค่าห้อง/ค่าเริ่มต้นบริษัท (ช่องทาง · ค่าเริ่มต้น · แก้ลักษณะทีหลัง · fallback
/// หยิบใบค่าห้องเป็นเงินประกัน · CMS) · C3 ใบรับเงินประกันถูกยกเลิกแล้วการจองค้างตลอดไป · P6 schema รอบ 194 อยู่ในชุดที่กลืน error</item>
/// </list>
/// </summary>
public class DepositKindNatureGuardTests
{
    private static readonly Guid Co = Guid.Parse("22222222-2222-2222-2222-222222222222");
    private static readonly Guid Guest = Guid.NewGuid();

    private static DepositKind Kind(string code, DepositNature nature, DepositVatTreatment? t = null, bool isDefault = false)
        => new()
        {
            Id = Guid.NewGuid(), CompanyId = Co, Code = code, Name = "ประเภท " + code, Nature = nature, VatTreatment = t,
            IsDefault = isDefault, IsActive = true,
        };

    private static DepositKindDecision Room(DepositKind? channel = null, DepositKind? companyDefault = null,
        DepositVatTreatment? companyT = null, DepositVatTreatment? channelT = null)
        => DepositPolicyResolver.ResolveKind(Co, DepositSupplyNature.Service, documentKind: null, channelKind: channel,
            channelTreatment: channelT, companyDefaultKind: companyDefault, companySetting: companyT, chartHas21530: true, priceChannel: true);

    // ═════════════════ C1 ครึ่งแรก — ของที่ถูกอยู่แล้ว ═════════════════

    [Fact]
    public void C1_ประเภทค่าห้องที่เป็นราคา_ยังถูกใช้ตามเดิม_ไม่มีคำเตือนเพิ่ม()
    {
        var room = Kind("ROOM", DepositNature.PartOfPrice, DepositVatTreatment.VatImmediate);
        var d = Room(channel: room);
        Assert.Equal((room.Id, DepositVatTreatmentSource.ChannelKind, DepositNature.PartOfPrice), (d.KindId!.Value, d.Source, d.Nature));
        Assert.Null(d.Warning);
        Assert.Null(d.RuleCode);
    }

    [Fact]
    public void C1_ค่าห้องนอกระบบVAT_ยังใช้ได้ในช่องมัดจำราคา()
    {
        var rent = Kind("RENT-ADV", DepositNature.NonVatSupply, DepositVatTreatment.FullDeposit);
        var d = Room(channel: rent);
        Assert.Equal(rent.Id, d.KindId);
        Assert.Equal(DepositNature.NonVatSupply, d.Nature);
        Assert.DoesNotContain(DepositPolicyResolver.KindNatureMismatchRuleCode, d.Warning ?? "");
    }

    [Fact]
    public void C1_ADVANCEเป็นค่าเริ่มต้น_ที่พักตามบริษัท_ได้ADVANCEเหมือนเดิม()
    {
        var advance = Kind("ADVANCE", DepositNature.PartOfPrice, null, isDefault: true);
        var d = Room(companyDefault: advance, companyT: DepositVatTreatment.VatImmediate);
        Assert.Equal((advance.Id, DepositVatTreatmentSource.CompanyDefaultKind), (d.KindId!.Value, d.Source));
        Assert.Null(d.Warning);
    }

    [Fact]
    public void C1_ผู้ใช้เลือกเงินประกันบนใบเอง_ยังได้เงินประกัน_ชั้นบนใบไม่ถูกกรอง()
    {
        var sec = Kind("SECURITY", DepositNature.RefundableSecurity, DepositVatTreatment.FullDeposit);
        var d = DepositPolicyResolver.ResolveKind(Co, DepositSupplyNature.Service, sec, null, null, null, null, chartHas21530: false, priceChannel: true);
        Assert.Equal((sec.Id, DepositNature.RefundableSecurity, "21620"), (d.KindId!.Value, d.Nature, d.LiabilityAccountCode));
        Assert.Null(d.Warning);
    }

    [Fact]
    public void C1_ช่องทางทั่วไปที่ไม่ใช่ช่องราคา_พฤติกรรมเดิม_ประเภทเงินประกันของช่องทางยังถูกใช้()
    {
        var sec = Kind("SEC", DepositNature.RefundableSecurity, DepositVatTreatment.FullDeposit);
        var d = DepositPolicyResolver.ResolveKind(Co, DepositSupplyNature.Service, null, sec, null, null, null);
        Assert.Equal(sec.Id, d.KindId);
    }

    // ═════════════════ C1 ครึ่งหลัง — ที่เคยพัง ═════════════════

    [Fact]
    public void C1_ประเภทของที่พักถูกแก้เป็นเงินประกัน_ถูกข้ามไปค่าเริ่มต้นบริษัท_พร้อมคำเตือนที่มองเห็น()
    {
        var sec = Kind("SEC", DepositNature.RefundableSecurity, DepositVatTreatment.FullDeposit);
        var advance = Kind("ADVANCE", DepositNature.PartOfPrice, null, isDefault: true);
        var d = Room(channel: sec, companyDefault: advance, companyT: DepositVatTreatment.VatImmediate);
        Assert.Equal(advance.Id, d.KindId);
        Assert.Equal(DepositNature.PartOfPrice, d.Nature);                  // ไม่ใช่เงินประกัน ⇒ เกิดภาษีตอนรับเงิน (§78/1)
        Assert.Equal(DepositVatTreatment.VatImmediate, d.Treatment);
        Assert.Null(d.LiabilityAccountCode);                                  // 217xx เดิม ไม่ใช่ 21530
        Assert.Contains("ประเภท SEC", d.Warning);
        Assert.Equal(DepositPolicyResolver.KindNatureMismatchRuleCode, d.RuleCode);
    }

    [Fact]
    public void C1_ค่าเริ่มต้นบริษัทเป็นเงินประกัน_ถูกข้ามทุกทางเข้า_ไม่มีประเภทเลย_ราคาVATทันที()
    {
        var secDefault = Kind("SECURITY", DepositNature.RefundableSecurity, DepositVatTreatment.FullDeposit, isDefault: true);
        var d = Room(companyDefault: secDefault);
        Assert.Null(d.KindId);
        Assert.Equal(DepositNature.PartOfPrice, d.Nature);
        Assert.Equal(DepositVatTreatment.VatImmediate, d.Treatment);
        Assert.Contains("ประเภทเริ่มต้นของบริษัท", d.Warning);
        // ทางเข้าทั่วไป (integration/CMS ผ่าน Decide) ก็ข้ามค่าเริ่มต้นที่เป็นเงินประกันเช่นกัน
        var ctx = new DepositKindCompanyContext(Co, DepositSupplyNature.Service, null, secDefault, false);
        Assert.Null(DepositKindCatalog.Decide(ctx, null).KindId);
    }

    [Fact]
    public void C1_ค่าเดิมของที่พักตั้งไว้_ค่าเริ่มต้นเงินประกันไม่ถูกอ่าน_ไม่ฟ้องคำเตือนที่ไม่เกี่ยว()
    {
        var secDefault = Kind("SECURITY", DepositNature.RefundableSecurity, DepositVatTreatment.FullDeposit, isDefault: true);
        var d = Room(companyDefault: secDefault, channelT: DepositVatTreatment.VatPendingUndue);
        Assert.Equal(DepositVatTreatmentSource.ChannelOverride, d.Source);
        Assert.DoesNotContain(DepositPolicyResolver.KindNatureMismatchRuleCode, d.Warning ?? "");
    }

    [Fact]
    public void C1_CMSชำระล่วงหน้า_ไม่ส่งค่าเริ่มต้นที่เป็นเงินประกัน_ส่งADVANCEที่ตั้งโหมดแล้ว()
    {
        var secDefault = Kind("SECURITY", DepositNature.RefundableSecurity, DepositVatTreatment.FullDeposit, isDefault: true);
        Assert.Null(DepositKindCatalog.PrePaymentKindId(new DepositKindCompanyContext(Co, DepositSupplyNature.Service, null, secDefault, false)));
        var advance = Kind("ADVANCE", DepositNature.PartOfPrice, DepositVatTreatment.VatImmediate, isDefault: true);
        Assert.Equal(advance.Id, DepositKindCatalog.PrePaymentKindId(new DepositKindCompanyContext(Co, DepositSupplyNature.Service, null, advance, false)));
        // บริษัทที่ไม่เคยตั้งอะไร (ADVANCE โหมดว่าง + ค่าตั้งว่าง) ⇒ ไม่ส่ง = ใบจองรูปเดิม
        var untouched = Kind("ADVANCE", DepositNature.PartOfPrice, null, isDefault: true);
        Assert.Null(DepositKindCatalog.PrePaymentKindId(new DepositKindCompanyContext(Co, DepositSupplyNature.Service, null, untouched, false)));
    }

    [Fact]
    public void C1_ตั้งเงินประกันเป็นค่าเริ่มต้นไม่ได้_ราคาและนอกระบบVATได้()
    {
        Assert.Null(DepositKindCatalog.DefaultKindProblem("มัดจำ", DepositNature.PartOfPrice));
        Assert.Null(DepositKindCatalog.DefaultKindProblem("ค่าเช่าล่วงหน้า", DepositNature.NonVatSupply));
        var msg = DepositKindCatalog.DefaultKindProblem("เงินประกัน", DepositNature.RefundableSecurity);
        Assert.NotNull(msg);
        Assert.Contains("ทางไปต่อ", msg);
    }

    [Theory]
    // (ลักษณะเดิม, ลักษณะใหม่, เริ่มต้น, ผูกค่าห้อง, ผูกเงินประกัน, ใช้บนใบ, ถูกปฏิเสธ)
    [InlineData(DepositNature.PartOfPrice, DepositNature.PartOfPrice, true, 3, 0, 9, false)]          // ไม่เปลี่ยน = ผ่านเสมอ
    [InlineData(DepositNature.PartOfPrice, DepositNature.NonVatSupply, true, 2, 0, 0, false)]         // ราคา ↔ นอกระบบ ยังเป็นมัดจำราคา
    [InlineData(DepositNature.PartOfPrice, DepositNature.RefundableSecurity, false, 0, 0, 0, false)]  // ไม่มีใครผูก/ใช้ = แก้ได้
    [InlineData(DepositNature.PartOfPrice, DepositNature.RefundableSecurity, false, 1, 0, 0, true)]   // ผูกเป็นค่าห้อง
    [InlineData(DepositNature.RefundableSecurity, DepositNature.PartOfPrice, false, 0, 1, 0, true)]   // ผูกเป็นเงินประกัน
    [InlineData(DepositNature.PartOfPrice, DepositNature.RefundableSecurity, true, 0, 0, 0, true)]    // เป็นค่าเริ่มต้น
    [InlineData(DepositNature.RefundableSecurity, DepositNature.PartOfPrice, false, 0, 0, 1, true)]   // มีใบอ้างแล้ว
    public void C1_เปลี่ยนลักษณะเงิน_ตามการผูกและการใช้(DepositNature from, DepositNature to, bool isDefault,
        int room, int sec, int docs, bool rejected)
    {
        var problem = DepositKindCatalog.NatureChangeProblem("K", from, to, isDefault, room, sec, docs);
        Assert.Equal(rejected, problem != null);
        if (rejected) Assert.Contains("สร้างประเภทใหม่", problem);
    }

    [Fact]
    public void C1_ปิดเงินประกัน_ไม่หยิบใบค่าห้องที่ลักษณะผิดหรือใบเงินประกันเก่ามาแทนใบที่ผูก()
    {
        var linked = Guid.NewGuid();
        var misTypedRoom = Snap("TIV-ROOM", DepositNature.RefundableSecurity);   // ใบค่าห้องที่ถูกตรึงลักษณะผิด (ก่อนแก้ C1)
        var oldSettled = Snap("REC-SEC-OLD", DepositNature.RefundableSecurity);
        // ลิงก์ชี้ใบที่ไม่อยู่ในใบที่มีผล (ถูกยกเลิก) ⇒ ไม่มีเงินประกัน — เดิม fallback หยิบใบแรกที่ลักษณะเงินประกัน
        Assert.Null(LodgingDepositSettlement.SecurityDeposit(new[] { misTypedRoom, oldSettled }, linked));
        // ไม่มีลิงก์ = ไม่มีเงินประกัน
        Assert.Null(LodgingDepositSettlement.SecurityDeposit(new[] { misTypedRoom }, null));
        // ใบที่ผูกแต่ลักษณะเป็นราคา = ไม่ใช่เงินประกัน
        var priceDoc = Snap("TIV-1", DepositNature.PartOfPrice, linked);
        Assert.Null(LodgingDepositSettlement.SecurityDeposit(new[] { priceDoc }, linked));
    }

    [Fact]
    public void C1_ปิดเงินประกัน_ใบที่ผูกยังได้ตามเดิม_รวมใบที่ลักษณะว่าง()
    {
        var id = Guid.NewGuid();
        var sec = Snap("REC-SEC", DepositNature.RefundableSecurity, id);
        Assert.Same(sec, LodgingDepositSettlement.SecurityDeposit(new[] { Snap("TIV-1", DepositNature.PartOfPrice), sec }, id));
        var legacy = Snap("REC-SEC-NULL", null, id);
        Assert.Same(legacy, LodgingDepositSettlement.SecurityDeposit(new[] { legacy }, id));
    }

    // ═════════════════ C3 — ใบรับเงินประกันถูกยกเลิก ═════════════════

    [Fact]
    public void C3_ใบที่ผูกถูกยกเลิก_ไม่มีเงินค้าง_รับใหม่ได้()
    {
        var voidedId = Guid.NewGuid();   // ตัวโหลดตัดใบยกเลิก/ลบทิ้ง ⇒ ไม่อยู่ในรายการ
        var state = LodgingDepositSettlement.SecurityLinkState(voidedId, settledAt: null, new[] { Snap("TIV-ROOM", DepositNature.PartOfPrice) });
        Assert.Equal(LodgingSecurityLinkState.DocumentGone, state);
        Assert.Null(LodgingDepositSettlement.SecurityDeposit(new[] { Snap("TIV-ROOM", DepositNature.PartOfPrice) }, voidedId));
    }

    [Fact]
    public void C3_ใบที่ผูกยังมีผลและยังไม่ปิด_ยังค้าง_รับซ้ำไม่ได้เหมือนเดิม()
    {
        var id = Guid.NewGuid();
        Assert.Equal(LodgingSecurityLinkState.Open,
            LodgingDepositSettlement.SecurityLinkState(id, null, new[] { Snap("REC-SEC", DepositNature.RefundableSecurity, id) }));
        Assert.Equal(LodgingSecurityLinkState.Settled,
            LodgingDepositSettlement.SecurityLinkState(id, DateTime.UtcNow, new[] { Snap("REC-SEC", DepositNature.RefundableSecurity, id) }));
        Assert.Equal(LodgingSecurityLinkState.None,
            LodgingDepositSettlement.SecurityLinkState(null, null, Array.Empty<LodgingDepositSnapshot>()));
    }

    // ═════════════════ C4 — ข้อความต้องตรงความจริง ═════════════════

    [Fact]
    public void C4_ด่านเหตุผล_ไม่อ้างว่าพิมพ์บนเอกสาร()
    {
        var msg = DepositPolicyResolver.KindProblem(DepositNature.PartOfPrice, DepositVatTreatment.FullDeposit, null);
        Assert.NotNull(msg);
        Assert.DoesNotContain("พิมพ์เหตุผลเป็นหมายเหตุบนใบ", msg);
        Assert.Contains("ไม่พิมพ์", msg);
    }

    // ═════════════════ P6 — schema ในเส้นที่ล้มดัง ═════════════════

    [Fact]
    public void P6_คอลัมน์รอบ194บนDocuments_อยู่ในเส้นหลัก_seedไม่อยู่ในเส้นหลัก()
    {
        var main = DatabaseMigrationHelper.GetAlterStatements();
        Assert.Contains(main, s => s.Contains("ADD COLUMN IF NOT EXISTS \"DepositNature\"", StringComparison.Ordinal));
        Assert.Contains(main, s => s.Contains("CREATE TABLE IF NOT EXISTS \"DepositKinds\"", StringComparison.Ordinal));
        Assert.DoesNotContain(DepositKindSeed.MigrationSeedSql(), main);   // seed อ่านคอลัมน์ที่พักที่ชุดหลังสร้าง — อยู่ชุดหลัง
        // ลำดับในเส้นหลัก: ตาราง → index → คอลัมน์
        var schema = DatabaseMigrationHelper.DepositKindSchemaStatements().ToList();
        var table = schema.FindIndex(s => s.StartsWith("CREATE TABLE", StringComparison.Ordinal));
        var index = schema.FindIndex(s => s.StartsWith("CREATE UNIQUE INDEX", StringComparison.Ordinal));
        var column = schema.FindIndex(s => s.StartsWith("ALTER TABLE", StringComparison.Ordinal));
        Assert.True(table == 0 && index > table && column > index);
        // ชุดหลัง: schema ซ้ำก่อน seed (idempotent) ⇒ ฐานที่ตารางที่พักเพิ่งถูกสร้างในชุดหลังได้คอลัมน์ในบูตเดียวกัน
        var late = DatabaseMigrationHelper.GetFullTextSearchStatements().ToList();
        var lateCol = late.IndexOf(schema[^1]);
        var lateSeed = late.IndexOf(DepositKindSeed.MigrationSeedSql());
        Assert.True(lateCol >= 0 && lateSeed > lateCol);
    }

    [Fact]
    public void Migration_ถอดค่าเริ่มต้นที่เป็นเงินประกัน_คืนให้ADVANCEเฉพาะบริษัทที่ถูกถอด_ไม่แตะเอกสาร()
    {
        var sql = DatabaseMigrationHelper.DepositKindSecurityDefaultFixSql();
        Assert.Contains(DatabaseMigrationHelper.DepositKindSeedStatements(), s => s == sql);
        Assert.DoesNotContain("\"Documents\"", sql);
        Assert.Contains("\"Nature\" = " + (int)DepositNature.RefundableSecurity, sql);
        Assert.Contains("'" + DepositKindSeed.AdvanceSeedKey + "'", sql);
        // คืนค่าเริ่มต้นเฉพาะบริษัทในลูป (ที่ถูกถอด) และเมื่อไม่มีค่าเริ่มต้นอื่น — ไม่ปลุก ADVANCE ของบริษัทที่ตั้งใจไม่มีค่าเริ่มต้น
        var loop = sql.IndexOf("FOR c IN", StringComparison.Ordinal);
        var unset = sql.IndexOf("SET \"IsDefault\" = false", StringComparison.Ordinal);
        var set = sql.IndexOf("SET \"IsDefault\" = true", StringComparison.Ordinal);
        Assert.True(loop >= 0 && unset > loop && set > unset);
        Assert.Contains("NOT EXISTS", sql[set..]);
        Assert.DoesNotContain("{", sql);   // ExecuteSqlRaw ตีความ {n} เป็นพารามิเตอร์
    }

    private static LodgingDepositSnapshot Snap(string no, DepositNature? nature, Guid? id = null)
        => new(id ?? Guid.NewGuid(), no, Guest, 1000m, 0m, 1000m, true, 0m, 0m, null, nature);
}
