using Accounting.Helpers;
using Accounting.Models.Enums;
using Accounting.Services.Implementations.Migration;
using Accounting.Services.Import;
using Accounting.Services.Import.Targets;
using Microsoft.Extensions.DependencyInjection;
using Xunit;

namespace Accounting.Tests;

/// <summary>
/// รอบ 205 ทีม D — helper pure ของงานบัญชี/ย้ายระบบ: จับคู่รหัสบัญชี (ห้ามเลือกให้ต่ำกว่าเกณฑ์) · อ่านงบทดลองที่วาง ("1,234.50" · แท็บ · คำพูด ·
/// แถวหัว/ยอดรวม) · กติกางบทดลองยกมา (บล็อกเฉพาะข้อ 150 · คำเตือนรับทราบได้) · ตัดสินไฟล์โปรแกรมอื่นด้วยหัวตาราง (ไม่ใช่ชื่อไฟล์)
/// <para>เทสต์สองทิศ: ของที่ควรผ่านต้องผ่าน · ของที่ไม่ควรถูกเลือก/ถูกตัดต้องไม่ถูกแตะ</para>
/// </summary>
public class ImportAccountingHelpersTests
{
    private static readonly Guid Cash = Guid.NewGuid(), Ar = Guid.NewGuid(), Ap = Guid.NewGuid(), Capital = Guid.NewGuid(),
        ArOther = Guid.NewGuid(), CashGroup = Guid.NewGuid();

    private static readonly IReadOnlyList<ImportChartAccount> Chart = new[]
    {
        new ImportChartAccount(CashGroup, "111", "เงินสดและรายการเทียบเท่าเงินสด", AccountType.Asset, false),
        new ImportChartAccount(Cash, "11110", "เงินสด", AccountType.Asset, true),
        new ImportChartAccount(Ar, "11310", "ลูกหนี้การค้า", AccountType.Asset, true),
        new ImportChartAccount(ArOther, "11320", "ลูกหนี้อื่น", AccountType.Asset, true),
        new ImportChartAccount(Ap, "21210", "เจ้าหนี้การค้า", AccountType.Liability, true),
        new ImportChartAccount(Capital, "31100", "ทุนจดทะเบียน", AccountType.Equity, true),
    };

    // ═══ ImportAccountCodeResolver ═══

    [Fact]
    public void รหัสตรงผัง_เลือกให้_ความมั่นใจ_1()
    {
        var r = ImportAccountCodeResolver.Resolve("11110", "เงินสด", Chart);
        Assert.Equal(Cash, r.AccountId);
        Assert.Equal("Code", r.Kind);
        Assert.Equal(1m, r.Score);
    }

    [Fact]
    public void รหัสมีขีด_และเลขไทย_เทียบรูปเดียวกัน()
    {
        Assert.Equal("111001", ImportAccountCodeResolver.NormalizeCode("๑๑๑๐-๐๑"));
        Assert.Equal(Ar, ImportAccountCodeResolver.Resolve("113-10", null, Chart).AccountId);
    }

    [Fact]
    public void ความจำของบริษัท_ทางเดียว_เลือกให้_หลายทาง_แนะนำเท่านั้น()
    {
        var one = new[] { new ImportAccountMemory("1130", "ลูกหนี้", Ar, 3) };
        var r = ImportAccountCodeResolver.Resolve("1130", "ลูกหนี้", Chart, one);
        Assert.Equal(Ar, r.AccountId);
        Assert.Equal("Mapping", r.Kind);

        var split = new[] { new ImportAccountMemory("1130", null, Ar, 2), new ImportAccountMemory("1130", null, ArOther, 1) };
        var r2 = ImportAccountCodeResolver.Resolve("1130", "ลูกหนี้", Chart, split);
        Assert.Null(r2.AccountId);                         // กำกวม ⇒ ห้ามเลือกให้
        Assert.Contains(r2.Candidates, c => c.AccountId == Ar);
        Assert.Contains(r2.Candidates, c => c.AccountId == ArOther);
    }

    [Fact]
    public void ชื่อเหมือน_เลือกให้_ชื่อแค่คล้าย_แนะนำแต่ไม่เลือก()
    {
        var exact = ImportAccountCodeResolver.Resolve("2110", "เจ้าหนี้การค้า", Chart);
        Assert.Equal(Ap, exact.AccountId);
        Assert.Equal("Ngram", exact.Kind);

        var near = ImportAccountCodeResolver.Resolve("1135", "ลูกหนี้การค้า-ในประเทศ", Chart);
        Assert.Null(near.AccountId);                       // ต่ำกว่าเกณฑ์ ⇒ ไม่เลือกให้
        Assert.True(near.Score < ImportAccountCodeResolver.AutoApplyThreshold);
        Assert.Equal(Ar, near.Candidates[0].AccountId);    // แต่คำแนะนำอันดับหนึ่งถูก
    }

    [Fact]
    public void ไม่มีอะไรตรง_เสนอเฉพาะหมวดเลขหน้า_ไม่เลือกให้()
    {
        var r = ImportAccountCodeResolver.Resolve("3999", "บัญชีพิเศษ", Chart);
        Assert.Null(r.AccountId);
        Assert.All(r.Candidates, c => Assert.Equal("Category", c.Kind));
        Assert.Contains(r.Candidates, c => c.AccountId == Capital);
        Assert.DoesNotContain(r.Candidates, c => c.AccountId == Cash);
    }

    [Fact]
    public void รหัสตรงบัญชีคุม_ไม่เลือกบัญชีคุม_เสนอบัญชีย่อย()
    {
        var r = ImportAccountCodeResolver.Resolve("111", null, Chart);
        Assert.Null(r.AccountId);
        Assert.Contains(r.Candidates, c => c.AccountId == Cash);
        Assert.DoesNotContain(r.Candidates, c => c.AccountId == CashGroup);
    }

    [Fact]
    public void ป้ายแหล่ง_AI_เฉพาะชั้น_Ai()
    {
        Assert.Equal("🤖 AI แนะนำ", ImportAccountCodeResolver.KindLabel("Ai"));
        Assert.DoesNotContain("AI", ImportAccountCodeResolver.KindLabel("Ngram"));
        Assert.Equal(AccountType.Liability, ImportAccountCodeResolver.ClassFromCode("2110"));
        Assert.Null(ImportAccountCodeResolver.ClassFromCode("9000"));
    }

    // ═══ TrialBalancePasteParser ═══

    [Fact]
    public void วางจาก_Excel_แท็บ_คั่นหลักพัน_หัวตาราง_ยอดรวม()
    {
        var text = "รหัส\tชื่อบัญชี\tเดบิต\tเครดิต\n1110\tเงินสด\t1,234.50\t\n2110\tเจ้าหนี้การค้า\t\t1,234.50\n\tรวม\t1,234.50\t1,234.50\n";
        var r = TrialBalancePasteParser.Parse(text);
        Assert.Equal("tab", r.Delimiter);
        Assert.Equal(2, r.Rows.Count);
        Assert.Equal(1234.50m, r.Rows[0].Debit);
        Assert.Equal(1234.50m, r.Rows[1].Credit);
        Assert.Contains(r.Skipped, s => s.Reason == "แถวหัวตาราง");
        Assert.Contains(r.Skipped, s => s.Reason.StartsWith("แถวยอดรวม"));
    }

    [Fact]
    public void CSV_มีคำพูด_และ_CSV_ไม่มีคำพูดแต่คั่นหลักพัน_อ่านยอดเดียวกัน()
    {
        var quoted = TrialBalancePasteParser.Parse("1110,เงินสด,\"1,234.50\",0");
        var bare = TrialBalancePasteParser.Parse("1110,เงินสด,1,234.50,0");
        Assert.Equal(1234.50m, Assert.Single(quoted.Rows).Debit);
        Assert.Equal(1234.50m, Assert.Single(bare.Rows).Debit);   // เดิม split+parseFloat ได้ 1
    }

    [Fact]
    public void ขีดตั้ง_ยอดคงเหลือคอลัมน์เดียว_วงเล็บ_เลขไทย()
    {
        var r = TrialBalancePasteParser.Parse("1110|เงินสด|๑๐,๐๐๐.๐๐\n2110|เจ้าหนี้|(500.00)\n4100|รายได้|-250");
        Assert.Equal(new[] { 10000m, 0m, 0m }, r.Rows.Select(x => x.Debit));
        Assert.Equal(new[] { 0m, 500m, 250m }, r.Rows.Select(x => x.Credit));
    }

    [Fact]
    public void ชื่อบัญชีขึ้นต้นด้วยรวม_ไม่ถูกตัดเป็นแถวยอดรวม()
    {
        var r = TrialBalancePasteParser.Parse("5110\tรวมมิตร ค่าอาหาร\t300\t0");
        Assert.Single(r.Rows);
        Assert.Empty(r.Skipped);
    }

    [Fact]
    public void เดบิตติดลบ_ย้ายไปเครดิต_ยอดอ่านไม่ได้_รายงานเหตุผล()
    {
        var r = TrialBalancePasteParser.Parse("1110\tเงินสด\t100\t0\n2110\tเจ้าหนี้\t-300\t0\n3110\tทุน\tabc\t0");
        Assert.Equal(300m, r.Rows[1].Credit);
        Assert.Equal(0m, r.Rows[1].Debit);
        Assert.Contains(r.Skipped, s => s.LineNo == 3 && s.Reason.StartsWith("อ่านยอดเงินไม่ได้"));
    }

    // ═══ OpeningTrialBalanceRules ═══

    private static readonly OpeningTbContext OpenPeriod = new(true, "2569/01", FiscalPeriodStatus.Open, new HashSet<Guid>());

    [Fact]
    public void ไม่ลงตัว_บล็อกพร้อมทางไปต่อ_ลงตัว_ไม่บล็อก()
    {
        var bad = OpeningTrialBalanceRules.Evaluate(new[]
        {
            new OpeningTbLine("1110", "เงินสด", 10_000m, 0m, Chart[1]),
            new OpeningTbLine("3110", "ทุน", 0m, 9_000m, Chart[5]),
        }, OpenPeriod);
        var e = Assert.Single(bad.Errors);
        Assert.Equal(OpeningTrialBalanceRules.Unbalanced, e.Code);
        Assert.Contains("บัญชีผลต่างยกมา", e.WayForward);
        Assert.Equal((0m, 1_000m), OpeningTrialBalanceRules.BalancingAmounts(bad.TotalDebit, bad.TotalCredit));

        var ok = OpeningTrialBalanceRules.Evaluate(new[]
        {
            new OpeningTbLine("1110", "เงินสด", 10_000m, 0m, Chart[1]),
            new OpeningTbLine("3110", "ทุน", 0m, 10_000m, Chart[5]),
        }, OpenPeriod);
        Assert.True(ok.CanCommit);
        Assert.Empty(ok.Warnings);
    }

    [Fact]
    public void คำเตือน_ไม่บล็อก_แต่ต้องรับทราบ()
    {
        var v = OpeningTrialBalanceRules.Evaluate(new[]
        {
            new OpeningTbLine("1110", "เงินสด", 6_000m, 0m, Chart[1]),
            new OpeningTbLine("1111", "เงินสดย่อย", 4_000m, 0m, Chart[1]),     // สองรหัสรวมบัญชีเดียว
            new OpeningTbLine("2110", "ทุน?", 0m, 10_000m, Chart[5]),          // หมวด 2 แต่บัญชีทุน
        }, OpenPeriod with { AccountsWithExistingOpening = new HashSet<Guid> { Cash } });
        Assert.True(v.CanCommit);                                              // เดิม "⚠️" นับเป็น issue ที่บล็อก commit
        var codes = v.Warnings.Select(w => w.Code).ToHashSet();
        Assert.Contains(OpeningTrialBalanceRules.DuplicateTarget, codes);
        Assert.Contains(OpeningTrialBalanceRules.OverwriteExisting, codes);
        Assert.Contains(OpeningTrialBalanceRules.ClassMismatch, codes);
        Assert.NotEmpty(OpeningTrialBalanceRules.Unacknowledged(v, new[] { OpeningTrialBalanceRules.DuplicateTarget }));
        Assert.Empty(OpeningTrialBalanceRules.Unacknowledged(v, codes));
    }

    [Fact]
    public void ยังไม่จับคู่_และงวดปิด_บล็อก()
    {
        var v = OpeningTrialBalanceRules.Evaluate(new[] { new OpeningTbLine("1110", "เงินสด", 0m, 0m, null) },
            new OpeningTbContext(true, "2568/12", FiscalPeriodStatus.Closed, new HashSet<Guid>()));
        Assert.Contains(v.Errors, e => e.Code == OpeningTrialBalanceRules.Unmapped);
        Assert.Contains(v.Errors, e => e.Code == OpeningTrialBalanceRules.PeriodClosed);
    }

    // ═══ โปรแกรมอื่น: ตัดสินด้วยหัวตาราง ═══

    [Fact]
    public void หัวตารางสินค้าของ_Express_ได้คะแนน_adapter_สินค้า_ไม่ใช่ผู้ติดต่อ()
    {
        var headers = new[] { "รหัสสินค้า", "ชื่อสินค้า", "หน่วย", "ราคาขาย" };
        Assert.Equal(0, new ExpressContactsAdapter(null!).Score(headers));
        Assert.True(new ExpressProductsAdapter(null!).Score(headers) > 0);
        Assert.True(new ExpressContactsAdapter(null!).Score(new[] { "รหัสลูกค้า", "ชื่อลูกค้า", "เลขประจำตัวผู้เสียภาษี" }) > 0);
    }

    [Fact]
    public void บทบาทลูกค้า_ผู้ขาย_จากข้อความ()
    {
        Assert.Equal(((bool, bool)?)(true, false), ContactImportAdapterBase.ParseRole("ลูกค้า"));
        Assert.Equal(((bool, bool)?)(false, true), ContactImportAdapterBase.ParseRole("Vendor"));
        Assert.Equal(((bool, bool)?)(true, true), ContactImportAdapterBase.ParseRole("ทั้งสอง"));
        Assert.Null(ContactImportAdapterBase.ParseRole("อื่น ๆ"));
    }

    [Fact]
    public void ชนิดเอกสารนำเข้า_อ่านชื่อไทย_และกันชนิดที่ร่างไม่ได้()
    {
        Assert.Equal(DocumentType.PurchaseInvoice, DocumentImportTypes.Parse("ใบแจ้งหนี้ซื้อ"));
        Assert.Equal(DocumentType.TaxInvoice, DocumentImportTypes.Parse("Tax Invoice"));
        Assert.Null(DocumentImportTypes.Parse("ใบอะไรสักอย่าง"));
        Assert.DoesNotContain(DocumentType.PaymentVoucher, DocumentImportTypes.Allowed);   // อนุมัติอัตโนมัติตอนสร้าง = ขัด "ร่างเสมอ"
        Assert.Contains("ใบลดหนี้", DocumentImportTypes.UnsupportedWayForward(DocumentType.CreditNote));
    }

    [Fact]
    public void ทะเบียน_DI_ทีม_D_มีตัวหาบัญชี_และ_target_ไม่ชนชนิดกัน()
    {
        var services = new ServiceCollection();
        services.AddImportHub();
        Assert.Contains(services, d => d.ServiceType == typeof(ImportAccountMatcher) && d.Lifetime == ServiceLifetime.Scoped);
        var keys = ImportHubServiceRegistration.ImportTargetTypes(typeof(IImportTarget).Assembly)
            .Where(t => t.Namespace == typeof(DocumentImportTarget).Namespace)
            .Select(t => t.Name).ToList();
        Assert.Contains(nameof(OpeningTrialBalanceImportTarget), keys);
        Assert.Contains(nameof(DocumentImportTarget), keys);
        Assert.Contains(nameof(PaymentImportTarget), keys);
        Assert.Contains(nameof(FixedAssetImportTarget), keys);
        var entityKeys = new[] { OpeningTrialBalanceImportTarget.Key, DocumentImportTarget.Key, PaymentImportTarget.Key, FixedAssetImportTarget.Key }
            .Select(ImportExportPermissionScope.NormalizeEntityType).ToList();
        Assert.Equal(entityKeys.Count, entityKeys.Distinct().Count());
    }
}
