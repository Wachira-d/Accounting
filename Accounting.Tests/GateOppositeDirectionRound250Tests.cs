using Accounting.Helpers;
using Accounting.Models.Enums;
using Xunit;

namespace Accounting.Tests;

/// <summary>
/// รอบ 250 WP-04 (erp-review/2026-11-06/AUDIT_249_FOR_OPUS.md §WP-04 ข้อ 5–6 · E L-1 §2 matrix) — ตัวตัดสิน pure ของรอบ 241–249 ที่ matrix ของทีม E
/// นับว่า "ไม่มีทิศตรงข้าม" (N = 0): ทุกข้อในไฟล์นี้ล็อกทั้ง <b>ทิศที่ด่านทำงาน</b> และ <b>ทิศที่ด่านต้องไม่แตะ</b> (F2 ข้อ 6/8) ·
/// ใส่บั๊กกลับ (เช่น ให้ Releases ปล่อยทุกคำเตือน · ให้ UatWarning คืนข้อความเสมอ · ให้ OnlineOnly เก็บโอนเข้าบัญชีไว้) แล้วต้องมีอย่างน้อยหนึ่งบรรทัดล้ม
/// </summary>
public class GateOppositeDirectionRound250Tests
{
    private static readonly string Ar = ArOverdueApprovalWarning.Message(1, 1_070m);
    private static readonly DateTime T0 = new(2026, 11, 6, 3, 0, 0, DateTimeKind.Utc);

    // ═══ AutoApprovePolicy.Releases (เรียกตรง · เดิมทดสอบผ่าน Unacknowledged เท่านั้น) ═══

    [Fact]
    public void Releases_ปล่อยเฉพาะรหัสที่ตั้ง_ทิศตรงข้ามไม่มีนโยบาย_นโยบายว่าง_คำเตือนไม่มีป้าย_ไม่ปล่อย()
    {
        var release = AutoApprovePolicy.Create(new[] { ArOverdueApprovalWarning.Code }, "u1", T0);
        Assert.True(AutoApprovePolicy.Releases(release, Ar));
        // ทิศตรงข้าม
        Assert.False(AutoApprovePolicy.Releases(null, Ar));                                                          // ไม่ตั้งนโยบาย
        Assert.False(AutoApprovePolicy.Releases(AutoApprovePolicy.Create(Array.Empty<string>(), "u1", T0), Ar));      // ตั้งว่าง = หยุดรอคน
        Assert.False(AutoApprovePolicy.Releases(release, null));
        Assert.False(AutoApprovePolicy.Releases(release, "ผู้ติดต่อนี้ยังค้างชำระ 1 ใบ"));                          // ข้อความล้วนไม่มีป้าย
        Assert.False(AutoApprovePolicy.Releases(release, "ใบกำกับภาษีต้องระบุที่อยู่ผู้ซื้อ — ตามมาตรา 86/4"));      // §86/4 ปล่อยไม่ได้เสมอ
        Assert.Equal(new[] { Ar }, AutoApprovePolicy.Released(release, new[] { Ar, "ใบกำกับภาษีต้องระบุที่อยู่ผู้ซื้อ — ตามมาตรา 86/4" }));
    }

    // ═══ BindingHash สองทิศ (C §5.3) ═══

    private const string Tpl = "{\"lines\":[{\"description\":\"ค่าบริการ\",\"quantity\":1,\"unitPrice\":5000,\"vatRate\":7,\"accountId\":\"6f1d2b9e-0000-4000-8000-000000000001\"}]}";

    [Fact]
    public void BindingHash_เปลี่ยนเมื่อแก้ราคา_ผัง_ผู้ติดต่อ_ไม่เปลี่ยนเมื่อแก้คำอธิบาย()
    {
        var c = Guid.Parse("11111111-2222-4333-8444-555555555555");
        var h = AutoApprovePolicy.BindingHash(c, "Invoice", Tpl, true, false);
        Assert.Equal(h, AutoApprovePolicy.BindingHash(c, "Invoice", Tpl, true, false));                                       // deterministic
        // ทิศที่ต้องไม่เปลี่ยน
        Assert.Equal(h, AutoApprovePolicy.BindingHash(c, "Invoice", Tpl.Replace("ค่าบริการ", "ค่าบริการรายเดือน พ.ย."), true, false));
        // ทิศที่ต้องเปลี่ยน (ค่าที่กระทบเงิน/ผู้รับ)
        Assert.NotEqual(h, AutoApprovePolicy.BindingHash(c, "Invoice", Tpl.Replace("\"unitPrice\":5000", "\"unitPrice\":5001"), true, false));
        Assert.NotEqual(h, AutoApprovePolicy.BindingHash(c, "Invoice", Tpl.Replace("000000000001", "000000000002"), true, false));   // accountId
        Assert.NotEqual(h, AutoApprovePolicy.BindingHash(Guid.Parse("11111111-2222-4333-8444-555555555556"), "Invoice", Tpl, true, false));
        Assert.NotEqual(h, AutoApprovePolicy.BindingHash(null, "Invoice", Tpl, true, false));
    }

    // ═══ EtaxByEmailFormat.UatWarning (เดิม 0 เทสต์) · Stage ═══

    [Fact]
    public void UatWarning_เปิดทดสอบมีคำเตือนพร้อมที่อยู่UAT_ปิดไม่มี()
    {
        var w = EtaxByEmailFormat.UatWarning(true);
        Assert.NotNull(w);
        Assert.Contains(EtaxByEmailFormat.UatTimestampAddress, w);
        Assert.Contains("ไม่ถูกประทับเวลาจริง", w);
        Assert.Null(EtaxByEmailFormat.UatWarning(false));   // ทิศตรงข้าม: ใช้งานจริง = ไม่มีคำเตือน
    }

    [Fact]
    public void Stage_ประทับแล้วชนะส่งแล้ว_ไม่มีวันส่ง_ยังไม่ส่งแม้มีวันประทับไม่ได้ย้อน()
    {
        Assert.Equal(EtaxByEmailStage.NotSent, EtaxByEmailFormat.Stage(null, null));
        Assert.Equal(EtaxByEmailStage.SentAwaitingStamp, EtaxByEmailFormat.Stage(T0, null));
        Assert.Equal(EtaxByEmailStage.Stamped, EtaxByEmailFormat.Stage(T0, T0.AddMinutes(1)));
        // ทิศตรงข้าม: มีบันทึกประทับ (ผู้ใช้บันทึกใบที่ ETDA ส่งกลับ) = ประทับแล้ว แม้ไม่มีวันส่งในระบบ (ส่งจากที่อื่น) — ไม่ถอยเป็น "ยังไม่ส่ง"
        Assert.Equal(EtaxByEmailStage.Stamped, EtaxByEmailFormat.Stage(null, T0));
        Assert.NotEqual(EtaxByEmailStage.Stamped, EtaxByEmailFormat.Stage(T0, null));
    }

    // ═══ EtaxDeliveryRouter.Decide — neg "ไม่ใช่ e-Tax by Email ⇒ ส่งปกติ" ═══

    [Fact]
    public void Decide_บริษัทไม่ใช่ByEmail_ทุกขอบเขตทุกสถานะส่งอีเมลธรรมดา_ไม่มีวันเป็นEtax()
    {
        foreach (var enabled in new[] { true, false })
            foreach (var mode in Enum.GetValues<EtaxMode>())
                foreach (var scope in Enum.GetValues<EtaxAutoSkip>())
                    foreach (var sent in new[] { true, false })
                    {
                        var r = EtaxDeliveryRouter.Decide(enabled, mode, scope, sent);
                        var byEmail = enabled && mode is EtaxMode.ByEmail or EtaxMode.Both;
                        if (!byEmail)
                            Assert.Equal(EtaxEmailChannel.PlainEmail, r.Channel);   // ทิศตรงข้าม: ไม่ใช้ by Email = พฤติกรรมเดิมทุกกรณี
                        else if (scope != EtaxAutoSkip.None)
                            Assert.Equal(EtaxEmailChannel.PlainEmail, r.Channel);
                        else
                            Assert.Equal(sent ? EtaxEmailChannel.PlainCopyAfterEtax : EtaxEmailChannel.EtaxByEmail, r.Channel);
                        Assert.False(string.IsNullOrWhiteSpace(r.Reason));
                    }
    }

    // ═══ PostPaymentAutomationPolicy.Effective / IdempotencyKey ═══

    [Fact]
    public void Effective_ช่องว่างของตารางตามบริษัท_ช่องที่ตั้งทับได้ทั้งเปิดและปิด()
    {
        var on = new PostPaymentAutomationPolicy.Settings(EmailReceipt: true, IncludePartial: true);
        var off = new PostPaymentAutomationPolicy.Settings(EmailReceipt: false, IncludePartial: false);
        // ตารางไม่ตั้ง (null ทั้งแถว/ทุกช่อง) ⇒ ค่าบริษัทเป๊ะ ทั้งสองทิศ
        Assert.Equal(on, PostPaymentAutomationPolicy.Effective(on, null));
        Assert.Equal(off, PostPaymentAutomationPolicy.Effective(off, new(null, null)));
        // ตารางปิดทับบริษัทที่เปิด · ตารางเปิดทับบริษัทที่ปิด (ทิศตรงข้าม)
        Assert.False(PostPaymentAutomationPolicy.Effective(on, new(false, null)).EmailReceipt);
        Assert.True(PostPaymentAutomationPolicy.Effective(on, new(false, null)).IncludePartial);
        Assert.True(PostPaymentAutomationPolicy.Effective(off, new(true, null)).EmailReceipt);
        Assert.False(PostPaymentAutomationPolicy.Effective(off, new(true, null)).IncludePartial);
        Assert.False(PostPaymentAutomationPolicy.Effective(off, new(null, null)).Any);
        Assert.True(PostPaymentAutomationPolicy.Effective(off, new(true, null)).Any);
    }

    [Fact]
    public void IdempotencyKey_ต่อการชำระ_คงที่_ต่างการชำระต่างคีย์_ไม่ขึ้นกับรูปแบบตัวอักษร()
    {
        var p = Guid.Parse("aaaaaaaa-bbbb-4ccc-8ddd-eeeeeeeeeeee");
        Assert.Equal("postpay:aaaaaaaabbbb4ccc8dddeeeeeeeeeeee", PostPaymentAutomationPolicy.IdempotencyKey(p));
        Assert.Equal(PostPaymentAutomationPolicy.IdempotencyKey(p), PostPaymentAutomationPolicy.IdempotencyKey(Guid.Parse("AAAAAAAA-BBBB-4CCC-8DDD-EEEEEEEEEEEE")));
        Assert.NotEqual(PostPaymentAutomationPolicy.IdempotencyKey(p), PostPaymentAutomationPolicy.IdempotencyKey(Guid.Parse("aaaaaaaa-bbbb-4ccc-8ddd-eeeeeeeeeeef")));
        Assert.NotEqual(PostPaymentAutomationPolicy.IdempotencyKey(p), PostPaymentAutomationPolicy.IdempotencyKey(Guid.Empty));
    }

    // ═══ DocumentPayLinkPolicy.Allowed / OnlineOnly ═══

    [Fact]
    public void Allowed_ห้ามเปิดช่องทางที่บริษัทไม่ได้เปิด_ทิศตรงข้ามเลือกครบได้ครบ()
    {
        var company = new[] { "PromptPay", DocumentPayLinkPolicy.BankTransferCode };
        // ทิศที่ด่านตัด: ตาราง/ค่าบริษัทเลือกช่องทางที่บริษัทไม่ได้เปิด ⇒ ไม่ได้ (ไม่ใช่ "ทุกช่องทาง")
        Assert.Empty(DocumentPayLinkPolicy.Allowed(company, new[] { "Card" }, null));
        Assert.Empty(DocumentPayLinkPolicy.Allowed(company, null, new[] { "TrueMoney" }));
        Assert.Empty(DocumentPayLinkPolicy.Allowed(Array.Empty<string>(), null, null));                 // บริษัทไม่เปิดอะไร = ไม่มี
        // ทิศตรงข้าม: เลือกตรงกับที่เปิด ⇒ ได้ตามลำดับบริษัท · ไม่สนตัวพิมพ์
        Assert.Equal(company, DocumentPayLinkPolicy.Allowed(company, new[] { DocumentPayLinkPolicy.BankTransferCode.ToLowerInvariant(), "promptpay" }, null));
    }

    [Fact]
    public void OnlineOnly_ตัดเฉพาะโอนเข้าบัญชี_ทิศตรงข้ามช่องออนไลน์คงอยู่ครบ()
    {
        Assert.Empty(DocumentPayLinkPolicy.OnlineOnly(new[] { DocumentPayLinkPolicy.BankTransferCode }));
        Assert.Empty(DocumentPayLinkPolicy.OnlineOnly(new[] { DocumentPayLinkPolicy.BankTransferCode.ToUpperInvariant() }));
        Assert.Equal(new[] { "Card", "PromptPay" }, DocumentPayLinkPolicy.OnlineOnly(new[] { "Card", "PromptPay" }));
        Assert.Empty(DocumentPayLinkPolicy.OnlineOnly(Array.Empty<string>()));
    }

    // ═══ PaymentQrPolicy.Pick / PromptPayUsable ═══

    [Fact]
    public void Pick_ไม่มีทั้งเลขและรูป_ไม่มีQR_ทุกทางเลือก_มีอย่างเดียวใช้ตัวนั้นทุกทางเลือก()
    {
        foreach (var src in new[] { null, PaymentQrPolicy.SourceAuto, PaymentQrPolicy.SourcePromptPay, PaymentQrPolicy.SourceUpload, "ค่าแปลก" })
        {
            Assert.Equal(PaymentQrPolicy.Kind.None, PaymentQrPolicy.Pick(src, promptPayUsable: false, hasUpload: false));   // ห้ามแต่ง QR
            Assert.Equal(PaymentQrPolicy.Kind.PromptPay, PaymentQrPolicy.Pick(src, true, false));
            Assert.Equal(PaymentQrPolicy.Kind.Upload, PaymentQrPolicy.Pick(src, false, true));
        }
        // มีทั้งคู่: ผู้ใช้เลือกรูป = รูป · อื่น ๆ = พร้อมเพย์ (ฝังยอดได้)
        Assert.Equal(PaymentQrPolicy.Kind.Upload, PaymentQrPolicy.Pick(PaymentQrPolicy.SourceUpload, true, true));
        Assert.Equal(PaymentQrPolicy.Kind.PromptPay, PaymentQrPolicy.Pick(PaymentQrPolicy.SourceAuto, true, true));
    }

    [Theory]
    [InlineData("0812345678", true)]
    [InlineData("081-234-5678", true)]
    [InlineData("0105556000009", true)]      // เลขผู้เสียภาษี checksum ผ่าน
    [InlineData("0105556000007", false)]     // checksum ไม่ผ่าน
    [InlineData("1812345678", false)]        // 10 หลักไม่ขึ้นต้น 0
    [InlineData("08123456789", false)]       // 11 หลัก
    [InlineData("", false)]
    [InlineData("   ", false)]
    [InlineData(null, false)]
    [InlineData("081234567a", false)]
    public void PromptPayUsable_ค่าที่เก็บไว้ต้องผ่านตัวตรวจเดียวกับตอนตั้ง(string? stored, bool expected)
        => Assert.Equal(expected, PaymentQrPolicy.PromptPayUsable(stored));

    // ═══ ตาราง marker ของงานย้ายครั้งเดียว — ตัวตั้งเดียว (WP-04 ข้อ 1) ═══

    [Fact]
    public void OneShotDataFixes_สร้างที่DatabaseMigrationHelperตัวเดียว_ก่อนบล็อกที่ใช้marker_งานย้ายไม่สร้างซ้ำ()
    {
        var all = Accounting.Data.DatabaseMigrationHelper.GetAlterStatements();
        var central = all.IndexOf(Accounting.Data.DatabaseMigrationHelper.OneShotDataFixesTableSql);
        Assert.True(central >= 0, "ไม่มีคำสั่งสร้างตาราง marker ในชุด ApplyMissingColumns");
        Assert.Single(all, sql => sql == Accounting.Data.DatabaseMigrationHelper.OneShotDataFixesTableSql);
        // ทิศตรงข้าม: งานย้ายของรอบ 245/246 ไม่สร้างตารางเองแล้ว (สร้างซ้ำ = สองตัวตั้งของ schema เดียว)
        Assert.DoesNotContain(ContactNotifyEndpointMigration.Statements(), sql => sql.Contains("OneShotDataFixes"));
        Assert.DoesNotContain(PaymentQrMigration.Statements(), sql => sql.Contains("OneShotDataFixes"));
        // บล็อกที่อ่าน/เขียน marker ใน SQL (รอบ 238) อยู่หลังตารางกลาง
        var firstMarkerUse = all.FindIndex(sql => sql.Contains(LicenseLapseMigration.MarkerKey));
        Assert.True(firstMarkerUse > central, $"ตาราง marker ({central}) ต้องมาก่อนบล็อกที่ใช้ marker ({firstMarkerUse})");
    }

    // ═══ ฝ่ายค้าน WP-04 ข้อ 7: รูป FK ของฐานเทสต์ (DbTestDatabase) ต้องตรงกับ DDL ตอนบูตของ production (Program.cs) — กัน drift ═══

    private static string RepoRoot()
    {
        var d = new DirectoryInfo(AppContext.BaseDirectory);
        while (d != null && !File.Exists(Path.Combine(d.FullName, "Accounting.sln"))) d = d.Parent;
        return d?.FullName ?? throw new InvalidOperationException("ไม่พบ Accounting.sln");
    }

    [Theory]
    [InlineData("FK_DocumentSignatures_Approvals", "DocumentApprovalId", "DocumentApprovals")]
    [InlineData("FK_DocumentSignatures_Documents", "DocumentId", "Documents")]
    [InlineData("FK_DocumentApprovals_Documents", "DocumentId", "Documents")]
    public void ฐานเทสต์รูปFKเดียวกับDDLตอนบูต_ONDELETERESTRICT(string name, string column, string parent)
    {
        // Program.cs เป็น verbatim string ("" = ") ⇒ ถอดก่อนเทียบ
        var program = File.ReadAllText(Path.Combine(RepoRoot(), "Accounting", "Program.cs")).Replace("\"\"", "\"");
        var testDb = Accounting.Tests.Db.DbTestDatabase.ProductionFkShapeSql.Replace("\"\"", "\"");
        var shape = new System.Text.RegularExpressions.Regex(
            $"\"{name}\"\\s+FOREIGN KEY \\(\"{column}\"\\) REFERENCES \"{parent}\"\\(\"Id\"\\) ON DELETE (\\w+)");
        var prod = shape.Match(program);
        var test = shape.Match(testDb);
        Assert.True(prod.Success, $"Program.cs ไม่มี {name} รูปเดิม — แก้ DDL แล้วต้องแก้ DbTestDatabase.ProductionFkShapeSql ด้วย");
        Assert.True(test.Success, $"DbTestDatabase.ProductionFkShapeSql ไม่มี {name}");
        Assert.Equal("RESTRICT", prod.Groups[1].Value);
        Assert.Equal(prod.Groups[1].Value, test.Groups[1].Value);   // ทิศตรงข้าม: ฐานเทสต์ไม่ใช่ CASCADE ตามธรรมเนียม EF
    }

    // ═══ AssetCostSourceRules.PlaceInServiceProblem — ขอบทุกด่าน (ผ่านพอดี/ล้มพอดี) ═══

    private static readonly DateTime Today = new(2026, 11, 6);

    private static string? Place(decimal cost = 100_000m, decimal salvage = 0m, bool depreciable = true,
        DepreciationMethod m = DepreciationMethod.StraightLine, int life = 60, DateTime? date = null, DateTime? latest = null,
        AssetCostSourceRules.AccountRole role = AssetCostSourceRules.AccountRole.Asset)
        => AssetCostSourceRules.PlaceInServiceProblem(true, 1, 0, 0, cost, salvage, role, depreciable, m, life, date ?? Today, Today, latest);

    [Fact]
    public void PlaceInService_ขอบทุกด่าน_ผ่านพอดีและล้มพอดี()
    {
        Assert.Null(Place(date: Today));                                  // วันนี้ = ผ่าน
        Assert.NotNull(Place(date: Today.AddDays(1)));                    // พรุ่งนี้ = อนาคต
        Assert.Null(Place(date: Today, latest: Today));                   // วันเดียวกับใบล่าสุด = ผ่าน
        Assert.NotNull(Place(date: Today.AddDays(-1), latest: Today));    // ก่อนใบล่าสุด 1 วัน = ล้ม
        Assert.Null(Place(salvage: 99_999.99m));                          // ซากต่ำกว่าทุน 1 สตางค์ = ผ่าน
        Assert.NotNull(Place(salvage: 100_000m));                         // ซาก = ทุน = ล้ม
        Assert.NotNull(Place(salvage: -0.01m));
        Assert.Null(Place(cost: 0.01m));
        Assert.NotNull(Place(cost: 0m));
        Assert.NotNull(Place(life: 0));
        Assert.Null(Place(m: DepreciationMethod.None, life: 0, salvage: 200_000m, depreciable: false));   // ที่ดิน: ไม่คิดค่าเสื่อม · ซาก/อายุไม่เกี่ยว
        Assert.NotNull(Place(depreciable: false));                        // ที่ดิน + วิธีคิดค่าเสื่อม = ล้ม
        Assert.NotNull(Place(role: AssetCostSourceRules.AccountRole.ConstructionInProgress));
        Assert.NotNull(Place(role: AssetCostSourceRules.AccountRole.Deposit));
        Assert.Null(AssetCostSourceRules.PlaceInServiceProblem(true, 1, 0, 0, 1m, 0m, AssetCostSourceRules.AccountRole.Asset, true,
            DepreciationMethod.StraightLine, 1, Today, Today));
        Assert.NotNull(AssetCostSourceRules.PlaceInServiceProblem(true, 0, 0, 0, 1m, 0m, AssetCostSourceRules.AccountRole.Asset, true,
            DepreciationMethod.StraightLine, 1, Today, Today));          // ไม่มีเอกสารต้นทุน
    }
}
