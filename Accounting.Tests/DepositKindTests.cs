using System.Globalization;
using Accounting.Data;
using Accounting.Helpers;
using Accounting.Models.DTOs.Document;
using Accounting.Models.Entities;
using Accounting.Models.Enums;
using Xunit;

namespace Accounting.Tests;

/// <summary>
/// รอบ 194 — ประเภทเงินมัดจำ (spec S1–S4 · S8) · ทีม A (แกนกลาง) · ล็อกสองครึ่ง:
/// <b>ครึ่งแรก</b> ใบเดิม/ค่าเดิมไม่ถูกแตะ (payload ไม่ระบุประเภท = รูปใบเดิมทุกตัวอักษร · ไม่มีประเภทเลย = พฤติกรรม Resolve เดิม ·
/// migration ไม่ UPDATE "Documents") · <b>ครึ่งหลัง</b> ใบใหม่ได้แบบถูก (ลำดับ 6 ชั้น · เต็มยอด/นอกระบบ VAT = VAT 0 + deferred ·
/// ด่านเหตุผล · คำเตือนสินค้าแรงเท่าบริการ · ริบตามลักษณะเงิน · seed ต่อประเภทธุรกิจ)
/// </summary>
public class DepositKindTests
{
    private static readonly Guid Co = Guid.Parse("11111111-1111-1111-1111-111111111111");

    private static DepositKind Kind(string code, DepositNature nature, DepositVatTreatment? t = null,
        bool isDefault = false, bool active = true, Guid? company = null, string? reason = null, string? liability = null)
        => new()
        {
            CompanyId = company ?? Co, Code = code, Name = "ประเภท " + code, Nature = nature, VatTreatment = t,
            IsDefault = isDefault, IsActive = active, PolicyReason = reason, LiabilityAccountCode = liability,
        };

    private static DepositKindDecision Decide(
        DepositKind? doc = null, DepositKind? channel = null, DepositVatTreatment? channelT = null,
        DepositKind? companyDefault = null, DepositVatTreatment? companyT = null,
        DepositSupplyNature supply = DepositSupplyNature.Service, bool chart21530 = false)
        => DepositPolicyResolver.ResolveKind(Co, supply, doc, channel, channelT, companyDefault, companyT, chart21530);

    private static DocumentLineRequest Line(decimal vat, decimal price = 1000m, decimal? vatOverride = null)
        => new("มัดจำ", 1m, null, price, 0m, vat, 0m, null, VatAmountOverride: vatOverride);

    // ═════════════════ ครึ่งแรก — ของเดิมไม่ถูกแตะ ═════════════════

    [Theory]
    [InlineData(DepositSupplyNature.Unknown, true)]
    [InlineData(DepositSupplyNature.Service, false)]
    [InlineData(DepositSupplyNature.Goods, false)]
    public void ไม่มีประเภทไม่มีค่าตั้ง_เท่ากับResolveเดิม_ราคาVATทันที_ธงให้เจ้าของเลือกตามเดิม(DepositSupplyNature supply, bool needsChoice)
    {
        var d = Decide(supply: supply);
        var legacy = DepositPolicyResolver.Resolve(supply, null);
        Assert.Null(d.KindId);
        Assert.Equal(DepositNature.PartOfPrice, d.Nature);
        Assert.Equal(legacy.Treatment, d.Treatment);
        Assert.Equal(DepositVatTreatment.VatImmediate, d.Treatment);
        Assert.Equal(DepositVatTreatmentSource.BusinessTypeDefault, d.Source);
        Assert.Equal(legacy.NeedsOwnerChoice, d.NeedsOwnerChoice);
        Assert.Equal(needsChoice, d.NeedsOwnerChoice);
        Assert.Null(d.LiabilityAccountCode);                // ค่าเดิมของ AutoPost (21712)
        Assert.Null(d.Warning);
        Assert.False(d.RequiresReason);
    }

    [Fact]
    public void ค่าเดิมของบริษัทและของที่พัก_ยังตัดสินโหมดเหมือนResolveเดิม()
    {
        var company = Decide(companyT: DepositVatTreatment.VatPendingUndue);
        Assert.Equal(DepositVatTreatment.VatPendingUndue, company.Treatment);
        Assert.Equal(DepositVatTreatmentSource.CompanySetting, company.Source);

        var channel = Decide(channelT: DepositVatTreatment.FullDeposit, companyT: DepositVatTreatment.VatImmediate);
        Assert.Equal(DepositVatTreatment.FullDeposit, channel.Treatment);
        Assert.Equal(DepositVatTreatmentSource.ChannelOverride, channel.Source);
        Assert.Equal(DepositPolicyResolver.Resolve(DepositSupplyNature.Service, DepositVatTreatment.VatImmediate,
            DepositVatTreatment.FullDeposit).Treatment, channel.Treatment);
    }

    [Fact]
    public void ประเภทADVANCEที่seed_ไม่ตั้งโหมด_ปุ่มตั้งค่าบริษัทเดิมยังมีผล()
    {
        var advance = Kind("ADVANCE", DepositNature.PartOfPrice, t: null, isDefault: true);
        var d = Decide(companyDefault: advance, companyT: DepositVatTreatment.FullDeposit);
        Assert.Equal(advance.Id, d.KindId);
        Assert.Equal(DepositVatTreatment.FullDeposit, d.Treatment);            // จากค่าตั้งบริษัท
        Assert.Equal(DepositVatTreatmentSource.CompanySetting, d.Source);
        // ยังไม่มีใครตั้ง + ธุรกิจไม่ทราบ ⇒ ยังขอให้เจ้าของเลือก แม้มีประเภทเริ่มต้นแล้ว
        var unknown = Decide(companyDefault: advance, supply: DepositSupplyNature.Unknown);
        Assert.True(unknown.NeedsOwnerChoice);
        Assert.Equal(DepositVatTreatment.VatImmediate, unknown.Treatment);
    }

    [Fact]
    public void payloadไม่ระบุประเภท_Shapingไม่แตะอะไรเลย_อ็อบเจ็กต์เดิมทุกตัว()
    {
        var lines = new List<DocumentLineRequest> { Line(7m), Line(0m) };
        var r = DepositDocumentShaping.Apply(lines, null, 7m, requestDepositOutputVatDeferred: true, "21713");
        Assert.Same(lines, r.Lines);
        Assert.True(r.DepositOutputVatDeferred);
        Assert.Equal("21713", r.DepositDeferredAccountCode);
        Assert.Null(r.Note);
        Assert.False(r.VatOverridden);
    }

    [Fact]
    public void migrationไม่UPDATEเอกสาร_ใบเดิมคงNULL_และทุกคำสั่งถูกต่อเข้ารายการที่รันจริง()
    {
        var stmts = DatabaseMigrationHelper.DepositKindMigrationStatements();
        Assert.NotEmpty(stmts);
        Assert.All(stmts, s => Assert.DoesNotContain("UPDATE \"Documents\"", s));
        Assert.All(stmts, s => Assert.DoesNotContain("UPDATE Documents", s));
        // P6 ฝ่ายค้านรอบ 194: schema อยู่เส้นหลัก (log ความล้มเหลว) · ทุกคำสั่งยังอยู่ในชุดหลัง (schema ซ้ำก่อน seed — idempotent)
        var all = DatabaseMigrationHelper.GetAlterStatements();
        Assert.All(DatabaseMigrationHelper.DepositKindSchemaStatements(), s => Assert.Contains(s, all));
        var late = DatabaseMigrationHelper.GetFullTextSearchStatements();
        Assert.All(stmts, s => Assert.Contains(s, late));
        // คอลัมน์ใหม่ของใบ = ADD COLUMN IF NOT EXISTS ค่าเริ่มต้น NULL (ไม่มี DEFAULT ที่แต่งลักษณะให้ใบเดิม)
        var natureCol = Assert.Single(stmts, s => s.Contains("\"DepositNature\"", StringComparison.Ordinal));
        Assert.Contains("ADD COLUMN IF NOT EXISTS \"DepositNature\" integer NULL;", natureCol);
        // ตาราง/ index สร้างก่อน seed (ON CONFLICT อาศัย unique index)
        var list = stmts.ToList();
        var idx = list.FindIndex(s => s.Contains("UX_DepositKinds_Company_SeedKey", StringComparison.Ordinal));
        var seed = list.IndexOf(DepositKindSeed.MigrationSeedSql());
        Assert.True(idx >= 0 && seed > idx);
        Assert.All(stmts.Where(s => s.StartsWith("INSERT", StringComparison.Ordinal)),
            s => Assert.EndsWith("ON CONFLICT DO NOTHING;", s));
    }

    [Fact]
    public void ผูกที่พักกับประเภทlp_เฉพาะเมื่อยังว่าง_ไม่ทับที่ผู้ใช้เลือก()
    {
        var bind = Assert.Single(DatabaseMigrationHelper.DepositKindMigrationStatements(),
            s => s.StartsWith("UPDATE \"LodgingProperties\"", StringComparison.Ordinal));
        Assert.Contains("p.\"RoomDepositKindId\" IS NULL", bind);
        Assert.Contains("'lp:' || p.\"Id\"::text", bind);
        Assert.Contains("k.\"IsDeleted\" = false", bind);
    }

    [Fact]
    public void ริบใบที่ออกใบกำกับแล้ว_VATคงเดิม_ไม่มีธง_พฤติกรรมเดิม()
    {
        foreach (var n in new DepositNature?[] { null, DepositNature.PartOfPrice, DepositNature.RefundableSecurity })
        {
            var f = DepositPolicyResolver.ForfeitVatDecision(n, null, 65.42m, vatPendingUnrecognized: false);
            Assert.Equal(DepositForfeitVatAction.KeepExistingVat, f.Action);
            Assert.False(f.LateVat);
            Assert.Null(f.LateVatNote);
        }
    }

    [Fact]
    public void ค่าenumที่persistคงเลขเดิม()
    {
        Assert.Equal(0, (int)DepositVatTreatmentSource.ChannelOverride);
        Assert.Equal(1, (int)DepositVatTreatmentSource.CompanySetting);
        Assert.Equal(2, (int)DepositVatTreatmentSource.BusinessTypeDefault);
        Assert.Equal(1, (int)DepositNature.PartOfPrice);
        Assert.Equal(2, (int)DepositNature.RefundableSecurity);
        Assert.Equal(3, (int)DepositNature.NonVatSupply);
        Assert.Equal(1, (int)DepositForfeitAs.PriceOrFee);
        Assert.Equal(2, (int)DepositForfeitAs.Compensation);
    }

    // ═════════════════ ครึ่งหลัง — ใบใหม่ได้แบบถูก ═════════════════

    [Fact]
    public void ลำดับ6ชั้น_บนใบ_ช่องทาง_ค่าเดิมช่องทาง_เริ่มต้นบริษัท_ค่าตั้งบริษัท_ประเภทธุรกิจ()
    {
        var doc = Kind("DOC", DepositNature.PartOfPrice, DepositVatTreatment.VatImmediate);
        var ch = Kind("CH", DepositNature.RefundableSecurity, DepositVatTreatment.FullDeposit);
        var def = Kind("DEF", DepositNature.PartOfPrice, DepositVatTreatment.VatPendingUndue, isDefault: true, reason: "สัญญา");

        var d1 = Decide(doc, ch, DepositVatTreatment.FullDeposit, def, DepositVatTreatment.FullDeposit);
        Assert.Equal((doc.Id, DepositVatTreatmentSource.DocumentKind), (d1.KindId!.Value, d1.Source));

        var d2 = Decide(null, ch, DepositVatTreatment.VatPendingUndue, def, DepositVatTreatment.VatImmediate);
        Assert.Equal((ch.Id, DepositVatTreatmentSource.ChannelKind), (d2.KindId!.Value, d2.Source));
        Assert.Equal(DepositVatTreatment.FullDeposit, d2.Treatment);

        // ③ ค่าเดิมของช่องทางชนะประเภทเริ่มต้นบริษัท (ไม่มีประเภทบนใบ)
        var d3 = Decide(null, null, DepositVatTreatment.VatPendingUndue, def, DepositVatTreatment.VatImmediate);
        Assert.Null(d3.KindId);
        Assert.Equal((DepositVatTreatment.VatPendingUndue, DepositVatTreatmentSource.ChannelOverride), (d3.Treatment, d3.Source));

        var d4 = Decide(null, null, null, def, DepositVatTreatment.VatImmediate);
        Assert.Equal((def.Id, DepositVatTreatmentSource.CompanyDefaultKind), (d4.KindId!.Value, d4.Source));
        Assert.Equal(DepositVatTreatment.VatPendingUndue, d4.Treatment);

        var d5 = Decide(companyT: DepositVatTreatment.FullDeposit);
        Assert.Equal(DepositVatTreatmentSource.CompanySetting, d5.Source);

        var d6 = Decide();
        Assert.Equal(DepositVatTreatmentSource.BusinessTypeDefault, d6.Source);
    }

    [Fact]
    public void ประเภทที่ปิด_ของบริษัทอื่น_หรือลักษณะไม่นิยาม_ตกชั้นถัดไป()
    {
        var def = Kind("DEF", DepositNature.PartOfPrice, DepositVatTreatment.VatImmediate, isDefault: true);
        Assert.Equal(def.Id, Decide(Kind("OFF", DepositNature.RefundableSecurity, active: false), companyDefault: def).KindId);
        Assert.Equal(def.Id, Decide(Kind("X", DepositNature.RefundableSecurity, company: Guid.NewGuid()), companyDefault: def).KindId);
        Assert.Equal(def.Id, Decide(Kind("Z", (DepositNature)0), companyDefault: def).KindId);
        var deleted = Kind("DEL", DepositNature.RefundableSecurity);
        deleted.IsDeleted = true;
        Assert.Equal(def.Id, Decide(deleted, companyDefault: def).KindId);
    }

    [Fact]
    public void มัดจำเต็มยอด1000_VAT0และdeferred_บรรทัดที่ส่ง7ถูกตั้ง0พร้อมหมายเหตุ()
    {
        var d = Decide(Kind("FULL", DepositNature.PartOfPrice, DepositVatTreatment.FullDeposit, reason: "สัญญาเลขที่ 12"));
        var r = DepositDocumentShaping.Apply(new[] { Line(7m, vatOverride: 65.42m) }, d, 7m, false, null);
        var l = Assert.Single(r.Lines);
        Assert.Equal(0m, l.VatRate);
        Assert.Null(l.VatAmountOverride);
        Assert.Equal(1000m, l.UnitPrice);
        Assert.True(r.DepositOutputVatDeferred);
        Assert.True(r.VatOverridden);
        Assert.NotNull(r.Note);
        Assert.Contains("สัญญาเลขที่ 12", r.Note);            // เหตุผลพิมพ์เป็นหมายเหตุ
        Assert.Equal(new DepositPostingPreview(1000m, 1000m, 0m, 0m),
            DepositPolicyResolver.PreviewReceipt(d.Treatment, 1000m, 7m));
    }

    [Theory]
    [InlineData(DepositVatTreatment.VatImmediate)]
    [InlineData(DepositVatTreatment.VatPendingUndue)]
    [InlineData(DepositVatTreatment.FullDeposit)]
    public void นอกระบบVAT_บังคับ0และdeferredทุกโหมด_หมายเหตุRD81(DepositVatTreatment t)
    {
        var d = Decide(Kind("RENT-ADV", DepositNature.NonVatSupply, t));
        var r = DepositDocumentShaping.Apply(new[] { Line(7m), Line(0m) }, d, 7m, false, "21711");
        Assert.All(r.Lines, l => Assert.Equal(0m, l.VatRate));
        Assert.True(r.DepositOutputVatDeferred);
        Assert.Contains("[RD-81]", r.Note);
        Assert.Equal("21711", r.DepositDeferredAccountCode);   // ประเภทไม่ระบุบัญชี ⇒ ตาม request
        Assert.Equal(DepositPolicyResolver.KindNonVatRuleCode, d.RuleCode);
    }

    [Fact]
    public void นอกระบบVAT_client_ส่ง0มาแล้ว_ไม่มีหมายเหตุตั้งทับ()
    {
        var d = Decide(Kind("RENT-ADV", DepositNature.NonVatSupply, DepositVatTreatment.FullDeposit));
        var r = DepositDocumentShaping.Apply(new[] { Line(0m) }, d, 7m, false, null);
        Assert.False(r.VatOverridden);
        Assert.Null(r.Note);
    }

    [Theory]
    [InlineData(DepositVatTreatment.VatImmediate, false)]
    [InlineData(DepositVatTreatment.VatPendingUndue, true)]
    public void VATทันทีและรอเรียกเก็บ_ไม่แตะอัตราบรรทัด_บรรทัดยกเว้นยังเป็น0(DepositVatTreatment t, bool deferred)
    {
        var d = Decide(Kind("K", DepositNature.PartOfPrice, t, reason: "ตามสัญญา"));
        var lines = new[] { Line(7m), Line(0m) };
        var r = DepositDocumentShaping.Apply(lines, d, 7m, !deferred, null);
        Assert.Equal(new[] { 7m, 0m }, r.Lines.Select(l => l.VatRate).ToArray());   // ห้ามบังคับ 7% ให้บรรทัด 0 (§81)
        Assert.Equal(deferred, r.DepositOutputVatDeferred);
        Assert.False(r.VatOverridden);
    }

    [Fact]
    public void บริษัทไม่จดVAT_ได้0ทุกโหมด()
    {
        var d = Decide(Kind("K", DepositNature.PartOfPrice, DepositVatTreatment.VatImmediate));
        var r = DepositDocumentShaping.Apply(new[] { Line(7m) }, d, 0m, true, null);
        Assert.Equal(0m, Assert.Single(r.Lines).VatRate);
        Assert.False(r.DepositOutputVatDeferred);
        Assert.Contains("ไม่ได้จดทะเบียน VAT", r.Note);
    }

    [Fact]
    public void เงินประกัน_บัญชี21530เมื่อผังมี_ไม่งั้น21620_ประเภทระบุเองชนะ()
    {
        var sec = Kind("SECURITY", DepositNature.RefundableSecurity, DepositVatTreatment.FullDeposit);
        Assert.Equal("21530", Decide(sec, chart21530: true).LiabilityAccountCode);
        Assert.Equal("21620", Decide(sec, chart21530: false).LiabilityAccountCode);
        var own = Kind("SEC2", DepositNature.RefundableSecurity, DepositVatTreatment.FullDeposit, liability: "21610");
        Assert.Equal("21610", Decide(own, chart21530: true).LiabilityAccountCode);
        var r = DepositDocumentShaping.Apply(new[] { Line(0m) }, Decide(sec, chart21530: true), 7m, false, "21712");
        Assert.Equal("21530", r.DepositDeferredAccountCode);            // เงินประกันห้ามไหลเข้า 217xx
    }

    [Fact]
    public void ราคาเลือกเลื่อนVAT_ต้องมีเหตุผล_ทางไปต่อครบ()
    {
        var msg = DepositPolicyResolver.KindProblem(DepositNature.PartOfPrice, DepositVatTreatment.FullDeposit, "  ");
        Assert.NotNull(msg);
        Assert.Contains("เงินประกัน", msg);
        Assert.Contains("เหตุผล", msg);
        Assert.NotNull(DepositPolicyResolver.KindProblem(DepositNature.PartOfPrice, DepositVatTreatment.VatPendingUndue, null));
        // ทิศตรงข้าม: มีเหตุผล/VAT ทันที/เงินประกัน/นอกระบบ/ตามค่าตั้งบริษัท = ผ่าน (ไม่ปิดตัวเลือก — คำตัดสิน #34)
        Assert.Null(DepositPolicyResolver.KindProblem(DepositNature.PartOfPrice, DepositVatTreatment.FullDeposit, "สัญญาเลขที่ 7"));
        Assert.Null(DepositPolicyResolver.KindProblem(DepositNature.PartOfPrice, DepositVatTreatment.VatImmediate, null));
        Assert.Null(DepositPolicyResolver.KindProblem(DepositNature.RefundableSecurity, DepositVatTreatment.FullDeposit, null));
        Assert.Null(DepositPolicyResolver.KindProblem(DepositNature.NonVatSupply, DepositVatTreatment.VatImmediate, null));
        Assert.Null(DepositPolicyResolver.KindProblem(DepositNature.PartOfPrice, null, null));
        Assert.NotNull(DepositPolicyResolver.KindProblem((DepositNature)9, null, null));
        Assert.NotNull(DepositPolicyResolver.KindProblem(DepositNature.PartOfPrice, (DepositVatTreatment)9, "x"));
        Assert.True(Decide(Kind("K", DepositNature.PartOfPrice, DepositVatTreatment.FullDeposit)).RequiresReason);
        Assert.False(Decide(Kind("S", DepositNature.RefundableSecurity, DepositVatTreatment.FullDeposit)).RequiresReason);
    }

    [Fact]
    public void คำเตือน_สินค้าแรงเท่าบริการ_รหัสแยกตามมาตรา()
    {
        var goods = DepositPolicyResolver.KindWarning(DepositNature.PartOfPrice, DepositVatTreatment.FullDeposit, DepositSupplyNature.Goods);
        var service = DepositPolicyResolver.KindWarning(DepositNature.PartOfPrice, DepositVatTreatment.VatPendingUndue, DepositSupplyNature.Service);
        Assert.Equal(DepositPolicyResolver.KindPriceGoodsRuleCode, goods.RuleCode);
        Assert.Equal("RD-78(1)(b)", goods.RuleCode);
        Assert.Equal("RD-78/1", service.RuleCode);
        Assert.StartsWith("⚠️", goods.Warning);
        Assert.StartsWith("⚠️", service.Warning);
        Assert.Contains("78(1)(ข)", goods.Warning);
        Assert.Contains("1.5%", goods.Warning);
        // Resolve เดิม: คำเตือนสินค้าก็แรงขึ้นแล้ว (เดิม ℹ️)
        Assert.StartsWith("⚠️", DepositPolicyResolver.Resolve(DepositSupplyNature.Goods, DepositVatTreatment.FullDeposit).Warning);

        var none = DepositPolicyResolver.KindWarning(DepositNature.PartOfPrice, DepositVatTreatment.VatImmediate);
        Assert.Null(none.Warning);
        Assert.Null(none.RuleCode);
        Assert.Equal("RD-PO73-SEC", DepositPolicyResolver.KindWarning(DepositNature.RefundableSecurity, DepositVatTreatment.VatImmediate).RuleCode);
        Assert.Equal("RD-PO73-SEC", DepositPolicyResolver.KindWarning(DepositNature.RefundableSecurity, DepositVatTreatment.VatPendingUndue).RuleCode);
        var secFull = DepositPolicyResolver.KindWarning(DepositNature.RefundableSecurity, DepositVatTreatment.FullDeposit);
        Assert.Null(secFull.Warning);
        Assert.Null(secFull.RuleCode);
        Assert.Equal("RD-81", DepositPolicyResolver.KindWarning(DepositNature.NonVatSupply, DepositVatTreatment.FullDeposit).RuleCode);
    }

    [Fact]
    public void เงินประกันห้ามหักเป็นฐานภาษี_ใบเดิมและราคาไม่บล็อก()
    {
        var msg = DepositPolicyResolver.SecurityDeductionProblem(DepositNature.RefundableSecurity);
        Assert.NotNull(msg);
        Assert.Contains("DEP-SEC-DEDUCT", msg);
        Assert.Contains("ตัดชำระด้วยเงินประกัน", msg);   // ทางไปต่อ
        Assert.Null(DepositPolicyResolver.SecurityDeductionProblem(null));
        Assert.Null(DepositPolicyResolver.SecurityDeductionProblem(DepositNature.PartOfPrice));
        Assert.Null(DepositPolicyResolver.SecurityDeductionProblem(DepositNature.NonVatSupply));
    }

    [Fact]
    public void ริบ_ราคาที่VATพัก21913_ย้ายเข้า21911พร้อมธง()
    {
        var f = DepositPolicyResolver.ForfeitVatDecision(DepositNature.PartOfPrice, null, 65.42m, vatPendingUnrecognized: true,
            depositReceivedDate: new DateTime(2026, 7, 3));
        Assert.Equal(DepositForfeitVatAction.ReclassifyUndueToDue, f.Action);
        Assert.True(f.LateVat);
        Assert.Equal("[DEPOSIT-LATE-VAT] ภาษีถึงกำหนดตั้งแต่เดือนที่รับเงิน (03/07/2026) — ต้องยื่น ภ.พ.30 เพิ่มเติมของเดือนนั้น", f.LateVatNote);
    }

    [Fact]
    public void ริบ_มัดจำเต็มยอดที่เป็นราคา_ต้องออกใบกำกับของยอดที่ริบ_ห้ามรายได้ไม่มีVATเงียบ()
    {
        var f = DepositPolicyResolver.ForfeitVatDecision(DepositNature.PartOfPrice, null, 0m, vatPendingUnrecognized: true);
        Assert.Equal(DepositForfeitVatAction.IssueTaxInvoiceForForfeit, f.Action);
        Assert.Equal(7m, f.ForfeitInvoiceVatRate);
        Assert.True(f.LateVat);
        Assert.StartsWith(DepositPolicyResolver.LateVatMarker, f.LateVatNote);
        // ขอ "ค่าเสียหาย" กับเงินที่เป็นราคา = ไม่มีผล และต้องบอก (ห้าม silent no-op)
        var asked = DepositPolicyResolver.ForfeitVatDecision(DepositNature.PartOfPrice, DepositForfeitAs.Compensation, 0m, false);
        Assert.Equal(DepositForfeitVatAction.IssueTaxInvoiceForForfeit, asked.Action);
        Assert.Equal(DepositForfeitAs.PriceOrFee, asked.EffectiveAs);
        Assert.True(asked.RequestIgnored);
        Assert.Contains("ไม่มีผล", asked.Explanation);
    }

    [Fact]
    public void ริบใบเดิมไม่ทราบลักษณะ_ไม่ระบุ_คิดVAT_ทิศปลอดภัย_ระบุค่าเสียหาย_ไม่มีVAT()
    {
        var none = DepositPolicyResolver.ForfeitVatDecision(null, null, 0m, false);
        Assert.Equal(DepositForfeitAs.PriceOrFee, none.EffectiveAs);
        Assert.Equal(DepositForfeitVatAction.IssueTaxInvoiceForForfeit, none.Action);
        var comp = DepositPolicyResolver.ForfeitVatDecision(null, DepositForfeitAs.Compensation, 0m, false);
        Assert.Equal(DepositForfeitVatAction.CompensationNoVat, comp.Action);
        Assert.False(comp.LateVat);
        Assert.Null(comp.LateVatNote);
        // ค่าขยะจาก client = ไม่ระบุ
        Assert.Equal(DepositForfeitAs.PriceOrFee, DepositPolicyResolver.ForfeitVatDecision(null, (DepositForfeitAs)7, 0m, false).EffectiveAs);
    }

    [Fact]
    public void ริบเงินประกัน_ค่าเสียหายไม่มีVAT_ค่าของ_ค่าธรรมเนียมมีVAT_VATพักต้องกลับ()
    {
        var comp = DepositPolicyResolver.ForfeitVatDecision(DepositNature.RefundableSecurity, DepositForfeitAs.Compensation, 0m, false);
        Assert.Equal(DepositForfeitVatAction.CompensationNoVat, comp.Action);
        Assert.False(comp.ReverseUndueVat);
        Assert.Contains("ให้เลือกแบบมี VAT", comp.Explanation);
        var fee = DepositPolicyResolver.ForfeitVatDecision(DepositNature.RefundableSecurity, DepositForfeitAs.PriceOrFee, 0m, false,
            depositReceivedDate: new DateTime(2026, 7, 3));
        Assert.Equal(DepositForfeitVatAction.IssueTaxInvoiceForForfeit, fee.Action);
        // เงินประกันที่หักเป็นค่าของ/ค่าธรรมเนียม: จุดความรับผิด = วันที่หัก ไม่ใช่ภาษีค้างของเดือนที่รับเงิน ⇒ ไม่มีธงย้อนหลัง
        Assert.False(fee.LateVat);
        Assert.Null(fee.LateVatNote);
        var feePending = DepositPolicyResolver.ForfeitVatDecision(DepositNature.RefundableSecurity, DepositForfeitAs.PriceOrFee, 65.42m, true);
        Assert.Equal(DepositForfeitVatAction.ReclassifyUndueToDue, feePending.Action);
        Assert.False(feePending.LateVat);
        // ทิศตรงข้าม: ใบเดิมไม่ทราบลักษณะ ยังติดธง (ทิศปลอดภัย)
        Assert.True(DepositPolicyResolver.ForfeitVatDecision(null, null, 0m, false).LateVat);
        var pending = DepositPolicyResolver.ForfeitVatDecision(DepositNature.RefundableSecurity, DepositForfeitAs.Compensation, 65.42m, true);
        Assert.True(pending.ReverseUndueVat);
    }

    [Fact]
    public void ริบนอกระบบVAT_และบริษัทไม่จดVAT_ไม่มีVATไม่มีธง()
    {
        var nv = DepositPolicyResolver.ForfeitVatDecision(DepositNature.NonVatSupply, null, 0m, false);
        Assert.Equal(DepositForfeitVatAction.NonVatNoVat, nv.Action);
        Assert.False(nv.LateVat);
        var nr = DepositPolicyResolver.ForfeitVatDecision(DepositNature.PartOfPrice, null, 0m, false, companyVatRate: 0m);
        Assert.Equal(DepositForfeitVatAction.CompanyNotVatRegistered, nr.Action);
        Assert.False(nr.LateVat);
        Assert.Equal(0m, nr.ForfeitInvoiceVatRate);
    }

    [Fact]
    public void ธงภาษีย้อนหลัง_วันที่เป็นคศ_แม้เครื่องตั้งวัฒนธรรมไทย()
    {
        var old = CultureInfo.CurrentCulture;
        try
        {
            CultureInfo.CurrentCulture = new CultureInfo("th-TH");
            var f = DepositPolicyResolver.ForfeitVatDecision(null, null, 0m, false, depositReceivedDate: new DateTime(2026, 1, 31));
            Assert.Contains("(31/01/2026)", f.LateVatNote);
        }
        finally { CultureInfo.CurrentCulture = old; }
    }

    [Fact]
    public void อสังหาฯ_ไม่เดาเป็นบริการอีก_ให้เจ้าของเลือก()
    {
        Assert.Equal(DepositSupplyNature.Unknown, DepositPolicyResolver.NatureOf(IndustryType.RealEstate));
        Assert.True(DepositPolicyResolver.Resolve(DepositPolicyResolver.NatureOf(IndustryType.RealEstate), null).NeedsOwnerChoice);
    }

    [Fact]
    public void คำอธิบายเต็มยอด_เลิกบอกว่าริบเป็นรายได้ไม่มีVATแบบไม่มีเงื่อนไข_อ้างป73()
    {
        var full = DepositPolicyResolver.Options.Single(o => o.Value == "FullDeposit");
        Assert.DoesNotContain("ริบมัดจำ = รายได้ไม่มี VAT", full.Description);
        Assert.Contains("ออกใบกำกับภาษีของยอดที่ริบ", full.Description);
        Assert.All(DepositPolicyResolver.Options, o => Assert.Contains("ป.73/2541", o.LegalReference));
    }

    // ── seed ต่อประเภทธุรกิจ ──

    [Theory]
    [InlineData(IndustryType.General)]
    [InlineData(IndustryType.Trading)]
    [InlineData(IndustryType.Hotel)]
    [InlineData(IndustryType.RealEstate)]
    [InlineData(IndustryType.Other)]
    public void seedทุกประเภทธุรกิจ_มีADVANCEเริ่มต้นตัวเดียว_และเงินประกัน(IndustryType ind)
    {
        var rows = DepositKindSeed.For(ind);
        var def = Assert.Single(rows, r => r.IsDefault);
        Assert.Equal(DepositKindSeed.AdvanceSeedKey, def.SeedKey);
        Assert.Equal(DepositNature.PartOfPrice, def.Nature);
        Assert.Null(def.VatTreatment);                              // = ค่าตั้งบริษัท (NULL = ตามประเภทธุรกิจ)
        var sec = Assert.Single(rows, r => r.SeedKey == DepositKindSeed.SecuritySeedKey);
        Assert.Equal((DepositNature.RefundableSecurity, DepositVatTreatment.FullDeposit), (sec.Nature, sec.VatTreatment!.Value));
        Assert.Equal(rows.Count, rows.Select(r => r.SeedKey).Distinct().Count());
        Assert.Equal(rows.Count, rows.Select(r => r.Code).Distinct().Count());
        Assert.All(rows, r => Assert.Null(DepositPolicyResolver.KindProblem(r.Nature, r.VatTreatment, null)));
    }

    [Fact]
    public void seedโรงแรม_ชื่อเฉพาะ_เงินประกันลง21530()
    {
        var rows = DepositKindSeed.For(IndustryType.Hotel);
        Assert.Equal("มัดจำค่าห้องพัก", rows.Single(r => r.SeedKey == DepositKindSeed.AdvanceSeedKey).Name);
        var secRow = rows.Single(r => r.SeedKey == DepositKindSeed.SecuritySeedKey);
        Assert.Equal("เงินประกันความเสียหาย", secRow.Name);
        var entity = DepositKindSeed.ToEntity(Co, secRow);
        Assert.Equal(Co, entity.CompanyId);
        Assert.Equal("21530", Decide(entity, chart21530: true).LiabilityAccountCode);   // ผังโรงแรมมี 21530
    }

    [Fact]
    public void seedอสังหาฯ_มีค่าเช่าล่วงหน้านอกระบบVAT_ธุรกิจอื่นไม่มี()
    {
        var rent = Assert.Single(DepositKindSeed.For(IndustryType.RealEstate), r => r.SeedKey == DepositKindSeed.RentAdvanceSeedKey);
        Assert.Equal(DepositNature.NonVatSupply, rent.Nature);
        Assert.DoesNotContain(DepositKindSeed.For(IndustryType.Service), r => r.SeedKey == DepositKindSeed.RentAdvanceSeedKey);
        Assert.Equal(2, DepositKindSeed.For(IndustryType.General).Count);
    }

    [Fact]
    public void seedSQL_สร้างจากตารางเดียว_ทุกประเภทธุรกิจ_idempotent_ไม่แตะตารางอื่น()
    {
        var sql = DepositKindSeed.MigrationSeedSql();
        Assert.StartsWith("INSERT INTO \"DepositKinds\"", sql);
        Assert.EndsWith("ON CONFLICT DO NOTHING;", sql);
        Assert.DoesNotContain("UPDATE", sql);
        Assert.Contains("(15,'sys:ADVANCE','ADVANCE','มัดจำค่าห้องพัก',1,NULL::integer,true,10,", sql);
        Assert.Contains("(8,'sys:RENT-ADV','RENT-ADV','ค่าเช่าล่วงหน้า (ยกเว้น VAT)',3,1,false,30,", sql);
        foreach (var ind in Enum.GetValues<IndustryType>())
            Assert.Contains($"({(int)ind},'sys:ADVANCE'", sql);
        Assert.Contains("ELSE 0 END", sql);                           // ค่า IndustryType นอก enum ⇒ ชุดทั่วไป
    }
}
