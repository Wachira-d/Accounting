using Accounting.Helpers;
using Accounting.Models.Enums;
using Accounting.Services;
using Accounting.Services.Implementations;
using Accounting.Services.Implementations.Ocr;
using Xunit;
using Xunit.Abstractions;

namespace Accounting.Tests;

/// <summary>
/// **ฝ่ายค้านรอบ 5 (OCR 12 + EC4 บน 36b6125f) ส่วนหมวด/ภาษี — ทีม EC5** · เคสจาก probe ของผู้ตรวจ (ZzR4*Probe) สองทิศ: ใบที่พังกลับมาถูก + ใบที่ถูกอยู่แล้วไม่ถูกแตะ
/// <list type="bullet">
/// <item>P1-2 ผู้ขายต่างประเทศ: เลขไทยผ่าน mod-11 ชนะชื่อ · ต่างประเทศต้องมีหลักฐานเชิงบวก (<see cref="ThaiVatTypeRule.ForeignVendorReason"/>) ·
/// อัตรา VAT รายบรรทัดได้ชื่อผู้ขาย</item>
/// <item>P1-3 ผังที่ผู้ใช้ตั้งเอง: คำจับคู่ชื่อของแต่ละกฎ (<see cref="GlAccountNameMatch"/>) · ห้ามได้เงินเดือน/ค่านายหน้า · คืนทุกคู่ที่ base 7ca8c0d8 จับได้</item>
/// <item>P2-e อัตรา WHT ที่พิมพ์ชนะการคำนวณเกณฑ์ · ฐาน "ตัดสินไม่ได้" มีโน้ต</item>
/// <item>P2-f ค่าขนส่งบนใบซื้อของบริษัทที่ถือสต็อก = 51120 ค่าขนส่งสินค้า (เข้า)</item>
/// </list>
/// </summary>
public class OcrExpenseCategoryEc5Tests
{
    private readonly ITestOutputHelper _out;
    public OcrExpenseCategoryEc5Tests(ITestOutputHelper output) => _out = output;

    /// <summary>เลขผู้เสียภาษีไทยที่ผ่าน mod-11 (เลขเดียวกับ probe ของผู้ตรวจ)</summary>
    private const string ThaiId = "0105553026376";

    private OcrExtractedData Run(string? vendor, string desc, decimal amount, string? taxId = null,
        IndustryType? industry = null, bool freightIsInbound = false, string docType = "Receipt")
    {
        var raw = $"{vendor}\nใบเสร็จรับเงิน\nเลขที่ RC-001\n{desc} {amount:N2}\nรวมทั้งสิ้น {amount:N2}\n";
        var data = new OcrExtractedData { VendorName = vendor, VendorTaxId = taxId, TotalAmount = amount, VatAmount = 0m, DocumentType = docType };
        data.Items.Add(new OcrExtractedLineItem { Description = desc, Quantity = 1, UnitPrice = amount, Amount = amount });
        var text = ThaiTextNormalizer.Normalize(raw);
        var result = ExpenseCategoryResolver.Resolve(vendorName: vendor, headerDescription: null, lineDescriptions: new List<string?> { desc },
            rawText: text, industry: industry, zeroAmountLineDescriptions: new List<string?>(), pricedLines: new List<OcrPricedLine> { new(desc, amount) },
            diagnostics: new List<string>(), minorityWhtParts: new List<OcrMinorityWhtPart>(), freightIsInbound: freightIsInbound);
        if (result != null) ExpenseCategoryResolver.ApplyTo(data, result, text);
        _out.WriteLine($"| {vendor} | {taxId} | {desc} {amount} | {data.ExpenseCategory ?? "-"} | {data.DebitAccountCode ?? "-"} | "
            + $"{data.SuggestedWhtRate?.ToString("0.##") ?? "-"} | {data.WhtIncomeTypeCode ?? "-"} |");
        foreach (var t in data.ReasoningTrace) _out.WriteLine("    " + t);
        return data;
    }

    // ═══ P1-2 ผู้ขายต่างประเทศ ═══════════════════════════════════════════════════════════════════════

    [Fact]
    public void เลขไทยของเทสต์ผ่าน_mod11() => Assert.True(ThaiTaxId.IsValid(ThaiId));

    [Theory]
    // ผู้ตรวจ: head ตัดเป็นต่างประเทศเพราะชื่อตรงก่อนเลข (ทั้งหมดมีเลขไทยถูกต้อง)
    [InlineData("Amazon Web Services (Thailand) Limited")]
    [InlineData("ByteDance (Thailand) Co., Ltd.")]
    [InlineData("Zoom Video Communications (Thailand)")]
    [InlineData("Siam Japan Limited")]
    [InlineData("Thai Japan Ltd.")]
    [InlineData("Bangkok UK Limited")]
    [InlineData("Thai Hong Kong Limited")]
    [InlineData("Creative Inc. Co., Ltd.")]
    [InlineData("Nuvo Inc (Thailand) Co., Ltd.")]
    // ตรวจแล้วถูก (ห้ามถดถอย)
    [InlineData("Google (Thailand) Co., Ltd.")]
    [InlineData("Microsoft (Thailand) Limited")]
    [InlineData("Meta Thailand")]
    [InlineData("Thai Union Group Public Company Limited")]
    [InlineData("XYZ Limited")]
    [InlineData("บริษัท ไทยเจแปน จำกัด")]
    public void P1_2_เลขไทยผ่าน_mod11_ชนะชื่อเสมอ(string name)
    {
        Assert.Null(ThaiVatTypeRule.ForeignVendorReason(null, ThaiId, name));
        Assert.Null(ThaiVatTypeRule.ForeignPayeeReason(null, ThaiId, name));
        Assert.Equal("7", ThaiVatTypeRule.Suggest("ค่าบริการโฆษณา", null, ThaiId, name));
    }

    [Theory]
    [InlineData("Amazon Web Services (Thailand) Limited", null)]   // ไม่มีเลข แต่ชื่อบอก Thailand
    [InlineData("Zoom Video Communications (Thailand)", null)]
    [InlineData("Some Vendor", "010556123456")]                    // หลักหาย = ไม่รู้ = ในประเทศ (EC3 P1-3)
    [InlineData("Some Vendor", ThaiId + " 00000")]                 // สาขาติด
    [InlineData("Some Vendor", "0105561234567")]                   // 13 หลัก checksum ไม่ผ่าน = อ่านเพี้ยน
    [InlineData("Google Asia Pacific Pte. Ltd.", "010556123456")]  // เลขอ่านเพี้ยนจากเลขไทย — ชื่อไม่พลิกเป็นต่างประเทศ
    [InlineData(null, "OI05561234567")]                            // ตัวอักษรหน้าตาเหมือนตัวเลขล้วน
    [InlineData("Thai Hong Kong Limited", null)]                   // ชื่อมีคำไทย
    [InlineData("Siam Japan Limited", null)]
    [InlineData("Creative Inc. Co., Ltd.", null)]                  // Inc. คู่ Co., Ltd. = บริษัทไทย
    [InlineData("Some Vendor", "80158929938")]                     // 11 หลัก ไม่มีสัญญาณชื่อ = ไม่รู้
    [InlineData(null, null)]
    public void P1_2_ไม่มีหลักฐานเชิงบวก_เป็นในประเทศ(string? name, string? taxId)
        => Assert.Null(ThaiVatTypeRule.ForeignVendorReason(null, taxId, name));

    [Theory]
    // ผู้ตรวจ: base ต่างประเทศ · head ตรวจไม่เจอ (reason=-)
    [InlineData("Airbnb Ireland UC", "9827384L")]
    [InlineData("Fiverr International Ltd.", "514440874")]
    [InlineData("Alibaba.com Singapore E-Commerce Private Limited", "200413128M")]
    [InlineData("Shenzhen Huaqiang Technology Co., Ltd.", "91440300MA5F12345X")]
    [InlineData("Google Asia Pacific Pte. Ltd.", "200817984R")]
    [InlineData("Canva Pty Ltd", "80158929938")]
    // ตรวจแล้วถูก (ห้ามถดถอย)
    [InlineData("Meta Platforms Ireland Limited", "IE9692928F")]
    [InlineData("Spotify AB", "SE556703748501")]
    [InlineData("Hostinger International Ltd.", "CY10301365E")]
    // เลขอย่างเดียว / ชื่ออย่างเดียว
    [InlineData(null, "9827384L")]
    [InlineData(null, "514440874")]
    [InlineData(null, "200413128M")]
    [InlineData(null, "91440300MA5F12345X")]
    [InlineData(null, "US-99-1234567")]
    [InlineData("Some Vendor", "123456789")]
    [InlineData("Airbnb Ireland UC", null)]
    [InlineData("Example Trading Sdn Bhd", null)]
    [InlineData("Example Tools GmbH", null)]
    [InlineData("Example B.V.", null)]
    [InlineData("Example S.A.", null)]
    [InlineData("Example LLC", null)]
    [InlineData("Example Inc.", null)]
    [InlineData("Example K.K.", null)]
    [InlineData("Example Software India Private Limited", null)]
    [InlineData("Shenzhen Huaqiang Technology Co., Ltd.", null)]
    [InlineData("Acme Data Singapore Ltd.", null)]
    public void P1_2_หลักฐานเชิงบวก_เป็นต่างประเทศ(string? name, string? taxId)
    {
        Assert.NotNull(ThaiVatTypeRule.ForeignVendorReason(null, taxId, name));
        Assert.Equal("0", ThaiVatTypeRule.Suggest("ค่าบริการโฆษณา", null, taxId, name));
    }

    [Fact]
    public void P1_2_ทิศตรงข้าม_คำนิติบุคคลต่างประเทศต้องเป็นคำท้ายชื่อ_ไม่ใช่คำกลาง()
    {
        // AB / SE / UC สั้น — ถ้านับกลางชื่อ "AB Foods Trading Co., Ltd." (บริษัทไทยชื่ออังกฤษ) จะเป็นต่างประเทศ
        Assert.Null(ThaiVatTypeRule.ForeignVendorReason(null, null, "AB Foods Trading Co., Ltd."));
        Assert.Null(ThaiVatTypeRule.ForeignVendorReason(null, null, "SE Asia Logistics Co., Ltd."));
    }

    [Fact]
    public void P1_2_ประเทศที่ผู้ใช้ตั้งยังชนะ_และ_TH_ไม่ใช่หลักฐาน()
    {
        Assert.NotNull(ThaiVatTypeRule.ForeignVendorReason("SG", ThaiId, "บริษัท ทดสอบ จำกัด"));
        Assert.Null(ThaiVatTypeRule.ForeignVendorReason("TH", null, null));
    }

    [Theory]
    [InlineData("Meta Platforms Ireland Limited", "0993000454995")]
    [InlineData("Google Asia Pacific Pte. Ltd.", "0993000453000")]
    public void P1_2_นิติบุคคลต่างประเทศชัดที่มีเลขไทย_VAT_ในประเทศ_แต่ไม่เสนอหักในประเทศแบบมั่นใจ(string name, string taxId)
    {
        Assert.True(ThaiTaxId.IsValid(taxId));
        Assert.Null(ThaiVatTypeRule.ForeignVendorReason(null, taxId, name));           // เลขไทยชนะชื่อ ⇒ VAT รายบรรทัด 7%
        Assert.Equal("7", ThaiVatTypeRule.Suggest("Facebook ads", null, taxId, name));
        Assert.Contains("ที่มีเลขผู้เสียภาษีไทย", ThaiVatTypeRule.ForeignPayeeReason(null, taxId, name));   // ขอบเขต ม.70 ไม่รู้
    }

    [Fact]
    public void P1_2_ByteDance_Thailand_ใบโฆษณา_ได้ข้อเสนอ2เปอร์เซ็นต์คืน()
    {
        var d = Run("ByteDance (Thailand) Co., Ltd.", "ค่าโฆษณา TikTok Ads", 30000m, ThaiId);
        Assert.Equal(2m, d.SuggestedWhtRate);
        Assert.Equal("8ad", d.WhtIncomeTypeCode);
        Assert.DoesNotContain(d.ReasoningTrace, t => t.Contains("ผู้ขายต่างประเทศ"));
    }

    [Fact]
    public void P1_2_Siam_Japan_ใบซ่อมเครื่องจักร_ได้ข้อเสนอ3เปอร์เซ็นต์คืน()
    {
        var d = Run("Siam Japan Limited", "ค่าบริการซ่อมบำรุงเครื่องจักร", 15000m, ThaiId);
        Assert.Equal(3m, d.SuggestedWhtRate);
        Assert.Equal("8", d.WhtIncomeTypeCode);
    }

    [Theory]
    [InlineData("Fiverr International Ltd.", "Service fee - logo design", 5000, "514440874")]
    [InlineData("Airbnb Ireland UC", "Host service fee", 3000, "9827384L")]
    public void P1_2_Fiverr_Airbnb_ไม่ได้ข้อเสนอในประเทศ_ชี้ไปม70(string vendor, string line, int amount, string taxId)
    {
        var d = Run(vendor, line, amount, taxId);
        Assert.Null(d.SuggestedWhtRate);
        Assert.Null(d.WhtIncomeTypeCode);
        Assert.Contains(d.ReasoningTrace, t => t.Contains("ผู้ขายต่างประเทศ") && t.Contains("ม.70") && t.Contains("ภ.พ.36"));
    }

    // ═══ P1-3 ผังที่ผู้ใช้ตั้งเอง ═══════════════════════════════════════════════════════════════════════

    private static readonly GlAccountCandidate[] CustomChart =
    {
        new("6001", "เงินเดือน (ฝ่ายขาย)", AccountType.Expense),
        new("6002", "ค่านายหน้า (ฝ่ายขาย)", AccountType.Expense),
        new("6003", "เงินเดือน (ฝ่ายบริหาร)", AccountType.Expense),
        new("6101", "ค่าขนส่ง", AccountType.Expense),
        new("6102", "ค่าน้ำมันเชื้อเพลิง", AccountType.Expense),
        new("6103", "ค่าเช่า", AccountType.Expense),
        new("6104", "ค่าเช่าอาคาร", AccountType.Expense),
        new("6105", "ค่าที่ปรึกษา", AccountType.Expense),
        new("6106", "ค่าวัสดุสำนักงาน", AccountType.Expense),
        new("6107", "ค่าทำความสะอาด", AccountType.Expense),
        new("6108", "ค่าเบี้ยประกัน", AccountType.Expense),
        new("6109", "ค่าเดินทาง", AccountType.Expense),
        new("6110", "ค่าไฟฟ้า", AccountType.Expense),
        new("6111", "ค่าน้ำประปา", AccountType.Expense),
        new("6112", "ค่าโทรศัพท์และอินเทอร์เน็ต", AccountType.Expense),
        new("6113", "ค่าโฆษณา", AccountType.Expense),
        new("6114", "ค่าธรรมเนียมธนาคาร", AccountType.Expense),
        new("6115", "ค่าซ่อมแซม", AccountType.Expense),
        new("6116", "ค่ารับรอง", AccountType.Expense),
        new("6117", "ดอกเบี้ยจ่าย", AccountType.Expense),
        new("6118", "ค่าใช้จ่ายเบ็ดเตล็ด", AccountType.Expense),
        new("2101", "ค่าเช่าค้างจ่าย", AccountType.Liability),
        new("2102", "เงินเดือนค้างจ่าย", AccountType.Liability),
    };

    private static GlAccountNameMatch.Spec SpecOf(string category)
        => ExpenseCategoryResolver.RuleNameMatches.Single(r => r.Category == category).NameMatch;

    [Theory]
    [InlineData("ค่าขนส่ง / ค่าจัดส่ง", "6101")]                // ก่อน: "เงินเดือน (ฝ่ายขาย)"/"ค่านายหน้า (ฝ่ายขาย)" (คำ "(ฝ่ายขาย)")
    [InlineData("ค่าขนส่ง / ค่าจัดส่ง (เข้า)", "6101")]
    [InlineData("ค่าน้ำมันเชื้อเพลิง", "6102")]                  // ก่อน: "ค่าเดินทาง" (คำ "เดินทาง")
    [InlineData("ค่าเช่า", "6104")]                              // ก่อน: ไม่ตรง (คำ "เช่าสำนักงาน")
    [InlineData("ค่าที่ปรึกษากฎหมาย / บัญชี", "6105")]           // ก่อน: ไม่ตรง ("ที่ปรึกษาอื่นๆ")
    [InlineData("ค่าวัสดุสำนักงาน", "6106")]                     // ก่อน: ไม่ตรง ("วัสดุสิ้นเปลืองสำนักงาน")
    [InlineData("ค่าทำความสะอาด", "6107")]                       // ก่อน: ไม่ตรง ("บริการอื่นๆ")
    [InlineData("ค่าประกัน", "6108")]                            // ก่อน: ไม่ตรง ("เบี้ยประกันภัย")
    [InlineData("ค่าเดินทาง", "6109")]
    [InlineData("ค่าไฟฟ้า", "6110")]
    [InlineData("ค่าน้ำประปา", "6111")]
    [InlineData("ค่าโทรศัพท์ / อินเทอร์เน็ต", "6112")]
    [InlineData("ค่าโฆษณาและส่งเสริมการขาย", "6113")]
    [InlineData("ค่าธรรมเนียมธนาคาร", "6114")]
    [InlineData("ค่าซ่อมแซมและบำรุงรักษา", "6115")]
    [InlineData("ค่ารับรอง", "6116")]
    [InlineData("ดอกเบี้ยจ่าย", "6117")]
    [InlineData("ค่าบริการ", "6118")]                             // ก่อน (base): "บริหาร" ⇒ "เงินเดือน (ฝ่ายบริหาร)"
    [InlineData("ค่ารับเหมา", "6118")]
    [InlineData("ค่าส่วนกลาง", "6118")]
    public void P1_3_ผังตั้งเองชื่อไทยทั่วไป_ได้บัญชีที่ตรงความหมาย(string category, string expectedCode)
    {
        var r = GlAccountNameMatch.Pick(CustomChart, SpecOf(category), seededCode: null);
        _out.WriteLine($"{category} ⇒ {r.Picked?.Code} {r.Picked?.Name} (คำ {r.Keyword})");
        Assert.Equal(expectedCode, r.Picked?.Code);
    }

    [Fact]
    public void P1_3_ผังซื้อสินค้า_ไม่มีบัญชีต้นทุนในผังตั้งเอง_ปล่อยว่าง_ไม่แต่ง()
        => Assert.Null(GlAccountNameMatch.Pick(CustomChart, SpecOf("ซื้อสินค้า / วัตถุดิบ"), null).Picked);

    [Fact]
    public void P1_3_ทุกกฎ_ห้ามได้บัญชีเงินเดือนหรือค่านายหน้า_แม้ผังมีแต่บัญชีพวกนั้นกับค่าเดินทาง()
    {
        var hostile = new[]
        {
            new GlAccountCandidate("6001", "เงินเดือน (ฝ่ายขาย)", AccountType.Expense),
            new GlAccountCandidate("6002", "ค่านายหน้า (ฝ่ายขาย)", AccountType.Expense),
            new GlAccountCandidate("6003", "เงินเดือน/ค่าจ้าง (บริหาร)", AccountType.Expense),
            new GlAccountCandidate("6004", "ค่าล่วงเวลาและโบนัส", AccountType.Expense),
            new GlAccountCandidate("6005", "เงินสมทบประกันสังคม", AccountType.Expense),
            new GlAccountCandidate("6006", "ค่าคอมมิชชั่นการขาย", AccountType.Expense),
            new GlAccountCandidate("6109", "ค่าเดินทาง", AccountType.Expense),
        };
        foreach (var (category, _, spec) in ExpenseCategoryResolver.RuleNameMatches)
        {
            var picked = GlAccountNameMatch.Pick(hostile, spec, null).Picked;
            _out.WriteLine($"{category} ⇒ {picked?.Code ?? "-"} {picked?.Name}");
            Assert.True(picked == null || !GlAccountNameMatch.IsPayrollOrCommissionAccount(picked.Value.Name),
                $"กฎ '{category}' ได้บัญชีเงินเดือน/ค่านายหน้า {picked?.Code}");
            if (category is not ("ค่าเดินทาง" or "ค่าน้ำมันเชื้อเพลิง"))
                Assert.True(picked == null, $"กฎ '{category}' ได้ {picked?.Code} {picked?.Name}");
        }
        // น้ำมันห้ามได้ "ค่าเดินทาง" (ก่อน: คำ "เดินทาง")
        Assert.Null(GlAccountNameMatch.Pick(hostile, SpecOf("ค่าน้ำมันเชื้อเพลิง"), null).Picked);
    }

    /// <summary>ชื่อบัญชีของกฎที่ base 7ca8c0d8 (ก่อน EC4 ย้ายรหัส) — <c>git show 7ca8c0d8:Accounting/Services/Implementations/Ocr/ExpenseCategoryResolver.cs</c></summary>
    public static IEnumerable<object[]> BaseRuleNames() => new[]
    {
        new object[] { "ค่าน้ำมันเชื้อเพลิง", "ค่าน้ำมันเชื้อเพลิง" }, new object[] { "ค่าไฟฟ้า", "ค่าไฟฟ้า" },
        new object[] { "ค่าน้ำประปา", "ค่าน้ำประปา" }, new object[] { "ค่าโทรศัพท์ / อินเทอร์เน็ต", "ค่าโทรศัพท์และอินเทอร์เน็ต" },
        new object[] { "ค่าเช่า", "ค่าเช่า" }, new object[] { "ค่าโฆษณาและส่งเสริมการขาย", "ค่าโฆษณาและส่งเสริมการขาย" },
        new object[] { "ค่าขนส่ง / ค่าจัดส่ง", "ค่าขนส่ง" }, new object[] { "ซื้อสินค้า / วัตถุดิบ", "ต้นทุนสินค้า" },
        new object[] { "ค่าบริการ", "ค่าใช้จ่ายบริหาร" }, new object[] { "ค่ารับเหมา", "ค่าใช้จ่ายบริหาร" },
        new object[] { "ค่าที่ปรึกษากฎหมาย / บัญชี", "ค่าที่ปรึกษา" }, new object[] { "ค่าธรรมเนียมธนาคาร", "ค่าธรรมเนียมธนาคาร" },
        new object[] { "ค่าวัสดุสำนักงาน", "ค่าวัสดุสำนักงาน" }, new object[] { "ค่าซ่อมแซมและบำรุงรักษา", "ค่าซ่อมแซมและบำรุงรักษา" },
        new object[] { "ค่าส่วนกลาง", "ค่าใช้จ่ายบริหาร" }, new object[] { "ค่าเดินทาง", "ค่าเดินทาง" },
        new object[] { "ค่ารับรอง", "ค่ารับรอง" }, new object[] { "ค่าทำความสะอาด", "ค่าทำความสะอาด" },
        new object[] { "ค่าประกัน", "ค่าใช้จ่ายในการประกัน" }, new object[] { "ดอกเบี้ยจ่าย", "ดอกเบี้ยจ่าย" },
    };

    [Theory]
    [MemberData(nameof(BaseRuleNames))]
    public void P1_3_คืนทุกคู่ที่_base_เคยจับได้_เว้นเงินเดือน(string category, string baseAccountName)
    {
        // คำของ base = คำที่ดึงจากชื่อบัญชีของกฎ base (ตัวดึงเดียวกัน) · คู่ที่ base จับได้ (ไม่นับบัญชีเงินเดือน/ค่านายหน้า — ผิดตั้งแต่ base) ต้องยังได้
        var kw = GlAccountNameMatch.DistinctiveKeyword(baseAccountName);
        var baseHits = CustomChart.Where(a => kw != null && a.Name.Contains(kw) && a.Type == AccountType.Expense
            && !GlAccountNameMatch.IsPayrollOrCommissionAccount(a.Name)).Select(a => a.Code).ToList();
        var now = GlAccountNameMatch.Pick(CustomChart, SpecOf(category), null).Picked?.Code;
        _out.WriteLine($"{category}: base คำ '{kw}' ⇒ [{string.Join(",", baseHits)}] · ตอนนี้ {now ?? "-"}");
        if (baseHits.Count > 0) Assert.Contains(now, baseHits);
    }

    [Fact]
    public void P1_3_ตัวจัดหมวดส่งคำจับคู่ของกฎที่ชนะ()
    {
        var r = ExpenseCategoryResolver.Resolve("บริษัท ขนส่งดี จำกัด", null, new[] { "ค่าขนส่งสินค้า" }, "ค่าขนส่งสินค้า 3,000.00");
        Assert.NotNull(r?.AccountNameMatch);
        Assert.Contains("ค่าขนส่ง", r!.AccountNameMatch!.Keywords);
        Assert.DoesNotContain(r.AccountNameMatch.Keywords, k => k.Contains("ฝ่ายขาย"));
    }

    [Fact]
    public void P1_3_ทุกกฎมีคำจับคู่ชื่อ_และไม่มีคำของบัญชีเงินเดือน()
    {
        foreach (var (category, _, spec) in ExpenseCategoryResolver.RuleNameMatches)
        {
            Assert.NotEmpty(spec.Keywords);
            Assert.DoesNotContain(spec.Keywords, k => GlAccountNameMatch.IsPayrollOrCommissionAccount(k) || k.Contains("ฝ่ายขาย"));
            _out.WriteLine($"{category}: {string.Join(" · ", spec.Keywords)}");
        }
    }

    [Fact]
    public void P1_3_ชื่อที่ไม่ได้มาจากกฎ_ยังใช้คำจากชื่อ_แต่กันเงินเดือน()
    {
        var spec = GlAccountNameMatch.FromAccountName("ค่าขนส่ง (ฝ่ายขาย)");   // คำ "(ฝ่ายขาย)" แบบเดิม
        Assert.Null(GlAccountNameMatch.Pick(CustomChart.Where(a => a.Code != "6101"), spec, null).Picked);
    }

    // ═══ P2-e WHT ═══════════════════════════════════════════════════════════════════════════════════

    [Fact]
    public void P2_e_ใบมีส่วนลด_อัตราที่พิมพ์ชนะการคำนวณเกณฑ์()
    {
        // ผู้ตรวจ: 1,050 − 100 · VAT 66.50 · "หัก ณ ที่จ่าย 3%" — base 3% ⇒ head HasWht=false (อัตราที่พิมพ์หาย)
        var d = OcrService.ParseThaiDocument(ThaiTextNormalizer.Normalize(
            "ใบกำกับภาษี\nเลขที่ INV-2\nค่าบริการ 1,050.00\nรวมเงิน 1,050.00\nส่วนลด 100.00\nภาษีมูลค่าเพิ่ม 7% 66.50\nรวมทั้งสิ้น 1,016.50\nหัก ณ ที่จ่าย 3% 28.50\n"));
        _out.WriteLine($"sub={d.SubTotal} vat={d.VatAmount} total={d.TotalAmount} hasWht={d.HasWht} rate={d.WhtRate}");
        Assert.True(d.HasWht);
        Assert.Equal(3m, d.WhtRate);
        // ไม่เงียบ: บอกว่าฐานของใบนี้ต่ำกว่าเกณฑ์ + ช่องอัตราขึ้นเหลือง
        Assert.Contains(d.ReasoningTrace, t => t.Contains("[WHT]") && t.Contains("ต่ำกว่าเกณฑ์") && t.Contains("ตั้งตามอัตราที่กระดาษพิมพ์"));
        Assert.True(d.FieldConfidence[OcrFieldKeys.WhtRate] < 0.85);
    }

    [Fact]
    public void P2_e_ทิศตรงข้าม_ใบถึงเกณฑ์_ไม่มีคำเตือน_ความมั่นใจไม่ถูกกด()
    {
        var d = OcrService.ParseThaiDocument(ThaiTextNormalizer.Normalize(
            "ใบเสร็จรับเงิน/ใบกำกับภาษี\nเลขที่ INV-1\nค่าบริการ 1,000.00\nรวมเงิน 1,000.00\nภาษีมูลค่าเพิ่ม 7% 70.00\nรวมทั้งสิ้น 1,070.00\nหัก ณ ที่จ่าย 3% 30.00\n"));
        Assert.True(d.HasWht);
        Assert.Equal(3m, d.WhtRate);
        Assert.DoesNotContain(d.ReasoningTrace, t => t.Contains("ต่ำกว่าเกณฑ์"));
        Assert.False(d.FieldConfidence.ContainsKey(OcrFieldKeys.WhtRate));
    }

    [Fact]
    public void P2_e_กระดาษไม่พิมพ์อัตรา_ไม่ตั้งหักเอง()
    {
        var d = OcrService.ParseThaiDocument("ใบกำกับภาษี\nเลขที่ INV-3\nค่าบริการ 950.00\nภาษีมูลค่าเพิ่ม 66.50\nรวมทั้งสิ้น 1,016.50\n");
        Assert.False(d.HasWht);
        Assert.Null(d.WhtRate);
    }

    [Fact]
    public void P2_e_ฐานตัดสินไม่ได้_โน้ตบอกตรงว่าระบบตัดสินไม่ได้_ให้ตรวจเกณฑ์เอง()
    {
        var basis = OcrWhtThresholdBase.FromScan(null, null, 1050m, null, "TaxInvoice");
        Assert.Equal(OcrWhtThresholdVerdict.Unsure, basis.Verdict);
        var note = OcrWhtThresholdBase.UnsureNote(3m, basis);
        _out.WriteLine(note);
        Assert.Contains("ระบบตัดสินไม่ได้", note);
        Assert.Contains("1,000", note);
        Assert.Contains("981.31", note);   // 1,050 × 100/107
        Assert.Contains("3%", note);
    }

    [Theory]
    [InlineData("ค่าบริการ", 3)]
    [InlineData("ค่าขนส่ง / ค่าจัดส่ง", 1)]
    [InlineData("ค่าโฆษณาและส่งเสริมการขาย", 2)]
    [InlineData("ค่าเช่า", 5)]
    public void P2_e_อัตราตามกฎหมายของหมวด_สำหรับโน้ตตัดสินไม่ได้(string category, int rate)
        => Assert.Equal(rate, ExpenseCategoryResolver.StatutoryWhtRateOfCategory(category));

    [Theory]
    [InlineData("ดอกเบี้ยจ่าย")]      // อัตราขึ้นกับชนิดผู้รับ — ห้ามเดา
    [InlineData("ค่าน้ำประปา")]       // ไม่อยู่ในข่าย
    [InlineData("ไม่มีหมวดนี้")]
    [InlineData(null)]
    public void P2_e_ทิศตรงข้าม_หมวดที่ไม่อยู่ในข่าย_ไม่มีอัตรา(string? category)
        => Assert.Null(ExpenseCategoryResolver.StatutoryWhtRateOfCategory(category));

    [Fact]
    public void P2_e_เส้นลงบัญชี_อ่าน_VAT_ไม่ได้_ฐานคือยอดก่อน_VAT_ที่พิมพ์_300_ไม่ใช่_321()
    {
        // เปลี่ยนตั้งใจ (DOCUMENT_FLOW §2.2): SubTotal 10,000 · Total 10,700 · VAT อ่านไม่ได้ ⇒ ฐาน 10,000 (ไม่รวม VAT) ⇒ 3% = 300.00 (เดิม 321.00 บนยอดรวม VAT)
        var b = OcrWhtThresholdBase.FromScan(10000m, null, 10700m, null, "TaxInvoice");
        Assert.Equal(10000m, b.Amount);
        Assert.Equal(300.00m, Math.Round(b.Amount!.Value * 3m / 100m, 2, MidpointRounding.AwayFromZero));
    }

    // ═══ P2-f ค่าขนส่งเข้า ══════════════════════════════════════════════════════════════════════════

    [Fact]
    public void P2_f_ใบซื้อของบริษัทถือสต็อก_ค่าขนส่งสินค้า_ได้51120()
    {
        var d = Run("บริษัท ขนส่งดี จำกัด", "ค่าขนส่งสินค้า", 3000m, industry: IndustryType.Trading, freightIsInbound: true, docType: "TaxInvoice");
        Assert.Equal("51120", d.DebitAccountCode);
        Assert.Equal("ค่าขนส่งสินค้า", d.DebitAccountName);
        Assert.Equal("ค่าขนส่ง / ค่าจัดส่ง", d.ExpenseCategory);
        Assert.Equal(1m, d.SuggestedWhtRate);          // ประเภทเงินได้ไม่เปลี่ยน (8tr)
        Assert.Equal("8tr", d.WhtIncomeTypeCode);
        Assert.Contains(d.ReasoningTrace, t => t.Contains("51120") && t.Contains("53130"));
    }

    [Fact]
    public void P2_f_ทิศตรงข้าม_ไม่ใช่ค่าขนส่งเข้า_ยัง53130()
    {
        var d = Run("บริษัท ขนส่งดี จำกัด", "ค่าขนส่งสินค้า", 3000m, freightIsInbound: false);
        Assert.Equal("53130", d.DebitAccountCode);
    }

    [Fact]
    public void P2_f_ทิศตรงข้าม_หมวดอื่นไม่ถูกแตะ()
    {
        var d = Run("บริษัท อาคารดี จำกัด", "ค่าเช่าสำนักงาน", 20000m, industry: IndustryType.Trading, freightIsInbound: true, docType: "TaxInvoice");
        Assert.Equal("54410", d.DebitAccountCode);
    }

    [Theory]
    [InlineData(true, "Buyer", "TaxInvoice", true)]
    [InlineData(true, "Buyer", "Invoice", true)]
    [InlineData(true, "Buyer", "DebitNote", true)]
    [InlineData(true, "Buyer", "Receipt", false)]      // ค่าส่งพัสดุด้วยใบเสร็จ — ส่วนใหญ่ส่งของให้ลูกค้า (ขนส่งออก)
    [InlineData(true, "Seller", "TaxInvoice", false)]  // ฝั่งขาย
    [InlineData(true, null, "TaxInvoice", false)]      // ไม่รู้บทบาท ⇒ ไม่เดา
    [InlineData(false, "Buyer", "TaxInvoice", false)]  // บริษัทไม่ถือสต็อก ⇒ ห้ามได้ต้นทุนขาย (EC3 P2-7)
    [InlineData(true, "Buyer", null, false)]
    public void P2_f_ตัวตัดสินค่าขนส่งเข้า(bool keeps, string? role, string? docType, bool expected)
        => Assert.Equal(expected, OcrDocTypeDefaultAccounts.FreightIsInbound(keeps, role, docType));

    [Fact]
    public void P2_f_ผังค่าขนส่งเข้ามีอยู่จริงในแม่แบบ_ลงรายการได้_ชื่อตรง()
    {
        var tpl = ChartOfAccountTemplates.GetCommonAccounts();
        var row = tpl.Single(t => t.Code == ExpenseCategoryResolver.FreightInAccount.Code);
        Assert.Equal(AccountType.Expense, row.Type);
        Assert.Equal(ExpenseCategoryResolver.FreightInAccount.Name, row.NameTh);
        Assert.DoesNotContain(tpl, o => o.Code != row.Code && o.Code.StartsWith(row.Code, StringComparison.Ordinal));
    }

    [Fact]
    public void P2_f_ผังตั้งเอง_ค่าขนส่งเข้าห้ามได้ค่าขนส่งฝ่ายขาย()
    {
        var chart = new[]
        {
            new GlAccountCandidate("6201", "ค่าขนส่ง (ฝ่ายขาย)", AccountType.Expense),
            new GlAccountCandidate("6202", "ค่าขนส่งเข้า", AccountType.Expense),
        };
        Assert.Equal("6202", GlAccountNameMatch.Pick(chart, ExpenseCategoryResolver.FreightInAccount.NameMatch, null).Picked?.Code);
        Assert.Null(GlAccountNameMatch.Pick(chart.Take(1), ExpenseCategoryResolver.FreightInAccount.NameMatch, null).Picked);
        // ทิศขาออก: ค่าขนส่งออกห้ามได้ค่าขนส่งเข้า
        Assert.Equal("6201", GlAccountNameMatch.Pick(chart, SpecOf("ค่าขนส่ง / ค่าจัดส่ง"), null).Picked?.Code);
    }
}
