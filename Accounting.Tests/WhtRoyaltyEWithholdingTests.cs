using Accounting.Data;
using Accounting.Helpers;
using Accounting.Models.Entities;
using Accounting.Models.Enums;
using Accounting.Services;
using Accounting.Services.Implementations;
using Xunit;

namespace Accounting.Tests;

/// <summary>
/// รอบ 204 ทีม C — (c2) ค่าสิทธิ ม.40(3) จ่ายบุคคลธรรมดา: ค่าตั้งบริษัท ภ.ง.ด.3 (เดิม) / ภ.ง.ด.2 (แนะนำ · ประกาศอธิบดีฯ ฉบับที่ 58) ·
/// (c3) e-Withholding: ธนาคารหัก/นำส่ง ⇒ ไม่ออก 50 ทวิ อัตโนมัติ · หนี้ลงผัง 21923 · ไม่อยู่ในยอดนำส่ง
///
/// ล็อกทั้งสองทิศ (กฎเหล็ก #4 F2 ข้อ 8): ค่าตั้ง ภ.ง.ด.3 / ธงปิด ⇒ <b>เหมือนเดิมทุกประการ</b> · ค่าตั้ง ภ.ง.ด.2 / ธงเปิด ⇒ ย้ายเฉพาะใบที่ควรย้าย
/// </summary>
public class WhtRoyaltyEWithholdingTests
{
    private const string PersonId = "1103700123456";
    private const string CompanyId13 = "0105556012345";

    private static Contact Person() => new() { Name = "นายสมชาย ใจดี", TaxId = PersonId, ContactType = ContactType.Individual };
    private static Contact Firm() => new() { Name = "บริษัท ทดสอบ จำกัด", TaxId = CompanyId13, ContactType = ContactType.JuristicPerson };

    private static TaxType Form(Contact c, bool royalty, params string?[] codes)
        => WhtPayeeKind.ResolveForm(false, c.CountryCode, c.TaxId, c.ContactType, c.Name, codes, royalty);

    private static readonly DateTime Oct1 = new(2026, 10, 1, 0, 0, 0, DateTimeKind.Utc);

    // ══════════ c2 — ตัวจำแนก ══════════

    [Theory]
    [InlineData("3")]
    [InlineData("40(3)")]
    [InlineData("40(3) ค่าสิทธิ")]
    public void ม40_3_ทุกรูปที่เก็บไว้_คือค่าสิทธิ(string code) => Assert.True(ThaiWhtRateTable.IsSection40_3(code));

    [Theory]
    [InlineData(null)]
    [InlineData("")]
    [InlineData("4a")]
    [InlineData("8")]
    [InlineData("2")]
    public void ไม่ใช่ค่าสิทธิ(string? code) => Assert.False(ThaiWhtRateTable.IsSection40_3(code));

    [Fact]
    public void ค่าตั้ง_ภงด3_ตัวจำแนกเหมือนเดิมทุกกรณี()
    {
        var cases = new[] { new[] { "3" }, new[] { "3", "4a" }, new[] { "3", "8" }, new[] { "4a" }, new[] { "8" }, Array.Empty<string>() };
        foreach (var c in cases)
            Assert.Equal(ThaiWhtRateTable.ClassifySection40_4(c), ThaiWhtRateTable.ClassifySection40_4(c, royaltyAsPnd2: false));
        Assert.Equal(ThaiWhtRateTable.Section40_4Mix.NoSection40_4, ThaiWhtRateTable.ClassifySection40_4(new[] { "3" }));
    }

    [Fact]
    public void ค่าตั้ง_ภงด2_ค่าสิทธินับเป็นตระกูล_ภงด2()
    {
        Assert.Equal(ThaiWhtRateTable.Section40_4Mix.AllSection40_4, ThaiWhtRateTable.ClassifySection40_4(new[] { "3" }, true));
        Assert.Equal(ThaiWhtRateTable.Section40_4Mix.AllSection40_4, ThaiWhtRateTable.ClassifySection40_4(new[] { "3", "4a" }, true));
        Assert.Equal(ThaiWhtRateTable.Section40_4Mix.Mixed, ThaiWhtRateTable.ClassifySection40_4(new[] { "3", "8" }, true));
        Assert.Equal(ThaiWhtRateTable.Section40_4Mix.NoSection40_4, ThaiWhtRateTable.ClassifySection40_4(new[] { "8" }, true));
    }

    // ══════════ c2 — แบบ/ผัง ══════════

    [Fact]
    public void ค่าตั้ง_ภงด3_ค่าสิทธิบุคคล_ยังเป็น_ภงด3_ผัง21916()
    {
        Assert.Equal(TaxType.WithholdingTax3, Form(Person(), false, "3"));
        var p = Person();
        Assert.Equal("21916", WhtPayableAccount.CodeChain(false, null, p.TaxId, p.ContactType, p.Name, new[] { "3" })[0]);
        Assert.Equal("21916", WhtPayableAccount.CodeChain(false, null, p.TaxId, p.ContactType, p.Name, new[] { "3" }, royaltyAsPnd2: false)[0]);
    }

    [Fact]
    public void ค่าตั้ง_ภงด2_ค่าสิทธิบุคคล_เป็น_ภงด2_ผัง21915_ชื่อตรงพรีวิว()
    {
        var p = Person();
        Assert.Equal(TaxType.WithholdingTax2, Form(p, true, "3"));
        Assert.Equal("21915", WhtPayableAccount.CodeChain(false, null, p.TaxId, p.ContactType, p.Name, new[] { "3" }, true)[0]);
        Assert.Equal("ภาษีหัก ณ ที่จ่าย - ภ.ง.ด. 2", WhtPayableAccount.PreferredLabel(false, null, p.TaxId, p.ContactType, p.Name, new[] { "3" }, true));
    }

    [Fact]
    public void ค่าตั้ง_ภงด2_ใบที่ไม่ควรแตะ_ไม่ถูกแตะ()
    {
        Assert.Equal(TaxType.WithholdingTax53, Form(Firm(), true, "3"));                 // นิติบุคคล = 53 เสมอ
        Assert.Equal(TaxType.WithholdingTax3, Form(Person(), true, "8"));                // ค่าบริการ = 3
        Assert.Equal(TaxType.WithholdingTax3, Form(Person(), true, "3", "8"));           // ปน ⇒ เดิม (ภ.ง.ด.3) + คำเตือน
        Assert.Equal(TaxType.WithholdingTax3, Form(Person(), true));                     // ไม่รู้ประเภท ⇒ เดิม
        var foreign = new Contact { Name = "Foo Ltd", CountryCode = "SG", ContactType = ContactType.JuristicPerson };
        Assert.Equal(TaxType.WithholdingTax54, Form(foreign, true, "3"));               // ต่างประเทศ = 54
    }

    [Fact]
    public void คำเตือนใบปน_ค่าตั้ง_ภงด3_ข้อความเดิม_ภงด2_ข้อความค่าสิทธิ()
    {
        var p = Person();
        // ค่าตั้ง ภ.ง.ด.3: ค่าสิทธิ + ค่าบริการ ไม่ใช่ใบปน (ทั้งคู่ ภ.ง.ด.3) — เดิม
        Assert.Null(WhtPayeeKind.Pnd2MixWarning(false, null, p.TaxId, p.ContactType, p.Name, new[] { "3", "8" }));
        // ค่าตั้ง ภ.ง.ด.3: ดอกเบี้ย + ค่าบริการ = ข้อความเดิมทุกตัวอักษร
        Assert.Equal(WhtPayeeKind.Pnd2MixWarning(false, null, p.TaxId, p.ContactType, p.Name, new[] { "4a", "8" }),
            WhtPayeeKind.Pnd2MixWarning(false, null, p.TaxId, p.ContactType, p.Name, new[] { "4a", "8" }, royaltyAsPnd2: false));
        // ค่าตั้ง ภ.ง.ด.2: ค่าสิทธิ + ค่าบริการ = ใบปน · ค่าสิทธิ + ดอกเบี้ย = ไม่ปน
        Assert.Contains("ค่าสิทธิ ม.40(3)", WhtPayeeKind.Pnd2MixWarning(false, null, p.TaxId, p.ContactType, p.Name, new[] { "3", "8" }, true));
        Assert.Null(WhtPayeeKind.Pnd2MixWarning(false, null, p.TaxId, p.ContactType, p.Name, new[] { "3", "4a" }, true));
    }

    // ══════════ c2 — ค่าตั้ง + วันเริ่ม ══════════

    [Fact]
    public void AppliesTo_ภงด3_ไม่มีผลเสมอ_ภงด2_ตามวันเริ่ม()
    {
        Assert.False(WhtRoyaltyPolicy.AppliesTo(WhtRoyaltyIndividualForm.Pnd3, null, Oct1));
        Assert.False(WhtRoyaltyPolicy.AppliesTo(WhtRoyaltyIndividualForm.Pnd3, Oct1, Oct1.AddDays(5)));
        Assert.True(WhtRoyaltyPolicy.AppliesTo(WhtRoyaltyIndividualForm.Pnd2, null, Oct1));
        Assert.True(WhtRoyaltyPolicy.AppliesTo(WhtRoyaltyIndividualForm.Pnd2, Oct1, Oct1));               // วันเริ่ม = มีผล
        Assert.False(WhtRoyaltyPolicy.AppliesTo(WhtRoyaltyIndividualForm.Pnd2, Oct1, Oct1.AddDays(-1)));  // ก่อนวันเริ่ม = เดิม
        Assert.False(default(WhtRoyaltySetting).AppliesTo(Oct1));                                          // ไม่พบบริษัท = เดิม
    }

    [Fact]
    public void เปิด_ภงด2_ไม่ระบุวัน_ได้วันที่1ของเดือนนี้_แก้ได้_คงเดิมเมื่อไม่ส่ง()
    {
        var now = new DateTime(2026, 10, 13, 5, 0, 0, DateTimeKind.Utc);
        var legacy = default(WhtRoyaltySetting);
        var on = WhtRoyaltyPolicy.ApplyUpdate(legacy, WhtRoyaltyIndividualForm.Pnd2, null, now);
        Assert.Equal(WhtRoyaltyIndividualForm.Pnd2, on.Form);
        Assert.Equal(Oct1, on.Pnd2From);

        var edited = WhtRoyaltyPolicy.ApplyUpdate(on, WhtRoyaltyIndividualForm.Pnd2, new DateTime(2026, 9, 15), now);
        Assert.Equal(new DateTime(2026, 9, 15, 0, 0, 0, DateTimeKind.Utc), edited.Pnd2From);
        // บันทึกเรื่องอื่น (ไม่ส่งวัน) ⇒ วันที่ผู้ใช้แก้ไว้ไม่ถูกทับ
        Assert.Equal(edited.Pnd2From, WhtRoyaltyPolicy.ApplyUpdate(edited, WhtRoyaltyIndividualForm.Pnd2, null, now.AddMonths(2)).Pnd2From);
        // ไม่ส่งอะไรเลย = ไม่แตะ
        Assert.Equal(legacy, WhtRoyaltyPolicy.ApplyUpdate(legacy, null, null, now));
        // ปิดกลับเป็น ภ.ง.ด.3 ⇒ ไม่มีผล (วันเก็บไว้เป็นประวัติ)
        var off = WhtRoyaltyPolicy.ApplyUpdate(edited, WhtRoyaltyIndividualForm.Pnd3, null, now);
        Assert.False(off.AppliesTo(now));
    }

    [Fact]
    public void คำแนะนำก่อนอนุมัติ_เฉพาะค่าตั้ง_ภงด3_และบุคคล_ค่าสิทธิทั้งใบ()
    {
        var p = Person();
        var w = WhtRoyaltyPolicy.ApprovalWarning(WhtRoyaltyIndividualForm.Pnd3, false, null, p.TaxId, p.ContactType, p.Name, new[] { "3" });
        Assert.NotNull(w);
        Assert.True(WhtRoyaltyPolicy.IsApprovalWarning(w!));
        Assert.Contains("ฉบับที่ 58", w);
        Assert.Contains("ตั้งค่า > ภาษี", w);
        // ทิศตรงข้าม — เงียบ
        Assert.Null(WhtRoyaltyPolicy.ApprovalWarning(WhtRoyaltyIndividualForm.Pnd2, false, null, p.TaxId, p.ContactType, p.Name, new[] { "3" }));
        Assert.Null(WhtRoyaltyPolicy.ApprovalWarning(WhtRoyaltyIndividualForm.Pnd3, false, null, p.TaxId, p.ContactType, p.Name, new[] { "3", "8" }));
        Assert.Null(WhtRoyaltyPolicy.ApprovalWarning(WhtRoyaltyIndividualForm.Pnd3, false, null, p.TaxId, p.ContactType, p.Name, new[] { "8" }));
        Assert.Null(WhtRoyaltyPolicy.ApprovalWarning(WhtRoyaltyIndividualForm.Pnd3, false, null, p.TaxId, p.ContactType, p.Name, Array.Empty<string>()));
        var f = Firm();
        Assert.Null(WhtRoyaltyPolicy.ApprovalWarning(WhtRoyaltyIndividualForm.Pnd3, false, null, f.TaxId, f.ContactType, f.Name, new[] { "3" }));
        Assert.Null(WhtRoyaltyPolicy.ApprovalWarning(WhtRoyaltyIndividualForm.Pnd3, true, null, p.TaxId, p.ContactType, p.Name, new[] { "3" }));
    }

    // ══════════ c2 — 50 ทวิ (ResolveWhtFormType) ══════════

    private static IReadOnlyList<string?> Royalty => new[] { "3" };

    [Fact]
    public void ใบอัตโนมัติ_ค่าตั้ง_ภงด3_ค่าสิทธิ_ภงด3_เดิม()
    {
        var (form, corrected, _) = WithholdingTaxCertService.ResolveWhtFormType(Person(), null, false, Royalty);
        Assert.Equal(TaxType.WithholdingTax3, form);
        Assert.False(corrected);
        Assert.Equal(form, WithholdingTaxCertService.ResolveWhtFormType(Person(), null, false, Royalty, royaltyAsPnd2: false).formType);
    }

    [Fact]
    public void ใบอัตโนมัติ_ค่าตั้ง_ภงด2_ค่าสิทธิ_ภงด2()
        => Assert.Equal(TaxType.WithholdingTax2, WithholdingTaxCertService.ResolveWhtFormType(Person(), null, false, Royalty, true).formType);

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public void ใบคีย์มือ_ค่าสิทธิ_เคารพที่ผู้ใช้เลือก_2_หรือ_3_ทั้งสองค่าตั้ง(bool royalty)
    {
        var (f3, c3, _) = WithholdingTaxCertService.ResolveWhtFormType(Person(), TaxType.WithholdingTax3, false, Royalty, royalty);
        Assert.Equal(TaxType.WithholdingTax3, f3);
        Assert.False(c3);
        var (f2, c2, _) = WithholdingTaxCertService.ResolveWhtFormType(Person(), TaxType.WithholdingTax2, false, Royalty, royalty);
        Assert.Equal(TaxType.WithholdingTax2, f2);
        Assert.False(c2);
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public void ใบคีย์มือ_ดอกเบี้ยทั้งใบ_บังคับ_ภงด2_ทั้งสองค่าตั้ง(bool royalty)
    {
        var (form, corrected, _) = WithholdingTaxCertService.ResolveWhtFormType(Person(), TaxType.WithholdingTax3, false, new[] { "4a" }, royalty);
        Assert.Equal(TaxType.WithholdingTax2, form);
        Assert.True(corrected);
    }

    [Fact]
    public void ใบคีย์มือ_ค่าตั้ง_ภงด2_นิติบุคคลยังเป็น53()
        => Assert.Equal(TaxType.WithholdingTax53, WithholdingTaxCertService.ResolveWhtFormType(Firm(), TaxType.WithholdingTax2, false, Royalty, true).formType);

    // ══════════ c3 — ตัดสินออก 50 ทวิ ══════════

    private static WhtCertIssueFacts Facts(WhtCertIssueTrigger trigger, bool eWht)
        => new(trigger, DocumentType.PaymentVoucher, DocumentStatus.Paid, 300m, true, Guid.NewGuid(), null,
            true, false, 0, 0, 0, ViaEWithholding: eWht);

    [Theory]
    [InlineData(WhtCertIssueTrigger.Approve)]
    [InlineData(WhtCertIssueTrigger.Payment)]
    [InlineData(WhtCertIssueTrigger.IntegrationSync)]
    [InlineData(WhtCertIssueTrigger.Bulk)]
    public void eWithholding_ทางอัตโนมัติ_ไม่ออกใบ_และไม่นับว่ารอออก(WhtCertIssueTrigger trigger)
    {
        var d = WhtCertIssuePlan.Decide(Facts(trigger, eWht: true));
        Assert.Equal(WhtCertIssueAction.NotApplicable, d.Action);
        Assert.Equal(WhtCertIssuePlan.EWithholdingLabel, d.Reason);
    }

    [Fact]
    public void eWithholding_กดทีละใบ_ออกได้_พร้อมคำเตือน()
    {
        var d = WhtCertIssuePlan.Decide(Facts(WhtCertIssueTrigger.Manual, eWht: true));
        Assert.Equal(WhtCertIssueAction.IssueNow, d.Action);
        Assert.Equal(WhtCertIssuePlan.EWithholdingManualWarning, d.Reason);
    }

    [Theory]
    [InlineData(WhtCertIssueTrigger.Approve)]
    [InlineData(WhtCertIssueTrigger.Payment)]
    [InlineData(WhtCertIssueTrigger.Bulk)]
    [InlineData(WhtCertIssueTrigger.Manual)]
    public void ธงปิด_ตัดสินเหมือนเดิมทุกประการ(WhtCertIssueTrigger trigger)
    {
        var flagOff = Facts(trigger, eWht: false);
        var legacy = new WhtCertIssueFacts(trigger, DocumentType.PaymentVoucher, DocumentStatus.Paid, 300m, true,
            flagOff.ContactId, null, true, false, 0, 0, 0);   // ไม่ส่งพารามิเตอร์ใหม่เลย = ผู้เรียกเดิม
        Assert.Equal(WhtCertIssuePlan.Decide(legacy), WhtCertIssuePlan.Decide(flagOff));
        Assert.Equal(WhtCertIssueAction.IssueNow, WhtCertIssuePlan.Decide(flagOff).Action);
    }

    [Fact]
    public void eWithholding_มีใบอยู่แล้ว_ยังตอบว่ามีใบแล้ว()
        => Assert.Equal(WhtCertIssueAction.AlreadyIssued,
            WhtCertIssuePlan.Decide(Facts(WhtCertIssueTrigger.Approve, true) with { ActiveCertsAny = 1 }).Action);

    [Fact]
    public void ป้ายลิสต์รอออกใบ_eWithholding_ไม่ใช่ขาด_ธงปิด_ป้ายเดิม()
    {
        Assert.Equal(WhtCertIssuePlan.EWithholdingLabel, WhtCertIssuePlan.PendingLabel(true, null, true, viaEWithholding: true));
        Assert.Equal(WhtCertIssuePlan.PendingLabel(true, null, true), WhtCertIssuePlan.PendingLabel(true, null, true, viaEWithholding: false));
        Assert.Equal("จ่ายแล้วแต่ยังไม่มีใบ", WhtCertIssuePlan.PendingLabel(true, null, true));
    }

    [Fact]
    public void ตัวตัดสินใบลูก_ธงของใบเอง_หรือใบต้นทางฝั่งผู้จ่าย()
    {
        Assert.True(EWithholdingScope.IsVia(true, null, false));
        Assert.True(EWithholdingScope.IsVia(false, DocumentType.PurchaseInvoice, true));
        Assert.False(EWithholdingScope.IsVia(false, DocumentType.PurchaseInvoice, false));
        Assert.False(EWithholdingScope.IsVia(false, null, false));
        Assert.False(EWithholdingScope.IsVia(false, DocumentType.Invoice, true));   // ใบฝั่งรับเงิน — ไม่ใช่เราเป็นผู้หัก
    }

    // ══════════ c3 — ผัง 21923 ══════════

    [Fact]
    public void ผัง21923_อยู่ในผังมาตรฐาน_ชื่อตรง_ไม่ชน21920_และไม่อยู่ในชุดหนี้ภงด()
    {
        var chart = ChartOfAccountTemplates.GetCommonAccounts();
        var row = chart.Single(a => a.Code == WhtPayableAccount.EWithholdingCode);
        Assert.Equal("21923", row.Code);
        Assert.Contains("e-Withholding", row.NameTh);
        Assert.Equal(AccountType.Liability, row.Type);
        Assert.Contains("นิติบุคคล", chart.Single(a => a.Code == "21920").NameTh);   // 21920 = CIT — ห้ามใช้
        Assert.DoesNotContain(WhtPayableAccount.EWithholdingCode, WhtPayableAccount.DocumentPayableCodes);
    }

    [Fact]
    public void migration_ใส่ผัง21923ให้บริษัทเดิม_และยังใส่21915เหมือนเดิม()
    {
        var sql = WhtPayableAccount.MigrationSeedSql();
        Assert.Contains("'21915','219'", sql);
        Assert.Contains("'21923','219'", sql);
        Assert.EndsWith("ON CONFLICT DO NOTHING;", sql);
        Assert.DoesNotContain("UPDATE", sql);
        Assert.Contains(sql, DatabaseMigrationHelper.GetAlterStatements());
    }

    // ══════════ c3 — ยอดค้าง 21923 (ปฏิทินภาษี) ══════════

    [Fact]
    public void ยอดค้าง_FIFO_ล้างรายการเก่าก่อน()
    {
        var lines = new[]
        {
            (new DateTime(2026, 8, 1), 0m, 300m),
            (new DateTime(2026, 8, 20), 0m, 200m),
            (new DateTime(2026, 9, 1), 300m, 0m),   // ล้างก้อน 1 ส.ค.
        };
        var (oldest, outstanding) = EWithholdingClearing.Oldest(lines);
        Assert.Equal(new DateTime(2026, 8, 20), oldest);
        Assert.Equal(200m, outstanding);
    }

    [Fact]
    public void ล้างครบ_ไม่มียอดค้าง_ไม่เตือน()
    {
        var (oldest, outstanding) = EWithholdingClearing.Oldest(new[]
        {
            (new DateTime(2026, 8, 1), 0m, 300m),
            (new DateTime(2026, 8, 5), 300m, 0m),
        });
        Assert.Null(oldest);
        Assert.Equal(0m, outstanding);
        Assert.False(EWithholdingClearing.NeedsReminder(oldest, new DateTime(2026, 12, 1)));
        Assert.Null(EWithholdingClearing.Oldest(Array.Empty<(DateTime, decimal, decimal)>()).OldestOpenDate);
    }

    [Fact]
    public void เตือนเมื่อค้างเกิน30วัน_ไม่เตือนเมื่อยังไม่ถึง()
    {
        var open = new DateTime(2026, 9, 1, 0, 0, 0, DateTimeKind.Utc);
        Assert.False(EWithholdingClearing.NeedsReminder(open, open.AddDays(30)));
        Assert.True(EWithholdingClearing.NeedsReminder(open, open.AddDays(31)));
        Assert.Contains("21923", EWithholdingClearing.ReminderTitle(open));
        Assert.Contains("2569", EWithholdingClearing.ReminderTitle(open));   // พ.ศ.
    }

    // ══════════ ฝ่ายค้านรอบ 204 L3 — ใบ e-Withholding ไม่มีผัง 21923 ⇒ ล้มดัง (ไม่ตก 2191x เงียบ) ══════════

    [Fact]
    public void ใบeWithholdingไม่มีผัง21923_ล้มดังพร้อมทางไปต่อ_มีผังได้ผังนั้น()
    {
        var acc = new ChartOfAccount { AccountCode = WhtPayableAccount.EWithholdingCode, AccountName = "ภาษีหัก ณ ที่จ่ายรอธนาคารนำส่ง (e-Withholding)" };
        Assert.Same(acc, EWithholdingScope.RequireAccount(true, acc, "PV-1"));
        var ex = Assert.Throws<BusinessRuleException>(() => EWithholdingScope.RequireAccount<ChartOfAccount>(true, null, "PV-1"));
        Assert.Equal(EWithholdingScope.MissingAccountRuleCode, ex.RuleCode);
        Assert.Contains("PV-1", ex.Message);
        Assert.Contains("21923", ex.Message);
        Assert.Contains("ทางไปต่อ", ex.Message);
    }

    [Fact]
    public void ใบไม่ใช่eWithholding_ไม่ล้มและใช้ผังปกติ_ทิศตรงข้าม()
    {
        // ไม่ใช่ e-Withholding ⇒ null (ผู้เรียกใช้ CodeChain ปกติ) ทั้งที่บริษัทมีหรือไม่มีผัง 21923
        Assert.Null(EWithholdingScope.RequireAccount<ChartOfAccount>(false, null, "PV-2"));
        Assert.Null(EWithholdingScope.RequireAccount(false, new ChartOfAccount { AccountCode = "21923" }, "PV-2"));
    }
}
