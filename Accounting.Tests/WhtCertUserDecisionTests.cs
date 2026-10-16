using Accounting.Helpers;
using Accounting.Models.Enums;
using Xunit;

namespace Accounting.Tests;

/// <summary>
/// รอบ 204 ทีม D (คำสั่งเจ้าของ 2026-10-13: ระบบห้ามล็อกตาย · ผู้ใช้ตัดสิน · ระบบแนะนำ/อำนวย · บล็อกเฉพาะที่กฎหมาย/ความถูกต้องบังคับ พร้อมทางไปต่อ) —
/// ตัวตั้งข้อความ/รหัสกฎ <see cref="WhtFiledPeriodAck"/> · <see cref="WhtCertFormCorrection"/> · <see cref="WhtCertPayeeTaxIdDraft"/> ทั้งสองทิศ
/// (ผ่านเมธอดจริงบน PostgreSQL: <c>Db/WhtUserDecisionDbTests</c>)
/// </summary>
public class WhtCertUserDecisionTests
{
    // ── งวดที่ยื่นแล้ว: รับทราบ (ผู้ถือ Tax.File + เหตุผล) ──

    [Fact]
    public void ไม่ได้ขอรับทราบ_ไม่มีปัญหาของการรับทราบ_ผู้เรียกบล็อกด้วยข้อความเดิม()
        => Assert.Null(WhtFiledPeriodAck.AckProblem(false, null, false));

    [Fact]
    public void รับทราบแต่ไม่ถือสิทธิ์ยื่นภาษี_ปฏิเสธ_บอกให้ผู้มีสิทธิ์ทำ()
        => Assert.Contains("Tax.File", WhtFiledPeriodAck.AckProblem(true, "จะยื่นเพิ่มเติม", holdsTaxFile: false));

    [Theory]
    [InlineData(null)]
    [InlineData("")]
    [InlineData("   ")]
    public void รับทราบแต่ไม่มีเหตุผล_ปฏิเสธ(string? reason)
        => Assert.Contains("เหตุผล", WhtFiledPeriodAck.AckProblem(true, reason, holdsTaxFile: true));

    [Fact]
    public void รับทราบครบ_ผ่าน()
        => Assert.Null(WhtFiledPeriodAck.AckProblem(true, "ออกผิดผู้ถูกหัก — ยื่น ภ.ง.ด.53 เพิ่มเติม", holdsTaxFile: true));

    [Fact]
    public void ป้ายงวดที่ต้องยื่นเพิ่มเติม_พศ_เดือนสองหลัก()
    {
        Assert.Equal("ต้องยื่น ภ.ง.ด.53 เพิ่มเติม งวด 09/2569", WhtFiledPeriodAck.SupplementaryFilingLabel(TaxType.WithholdingTax53, 2026, 9));
        Assert.Equal("ต้องยื่น ภ.ง.ด.3 เพิ่มเติม งวด 08/2569 · ต้องยื่น ภ.ง.ด.53 เพิ่มเติม งวด 09/2569",
            WhtFiledPeriodAck.SupplementaryFilingLabels(new[]
            {
                (TaxType.WithholdingTax53, 2026, 9), (TaxType.WithholdingTax3, 2026, 8), (TaxType.WithholdingTax53, 2026, 9),
            }));
        Assert.Null(WhtFiledPeriodAck.SupplementaryFilingLabels(Array.Empty<(TaxType, int, int)>()));
    }

    [Fact]
    public void รหัสกฎ_แยกกันระหว่างคำตอบที่ต้องรับทราบกับ_audit()
    {
        Assert.Equal("RD-50BIS-PAYEE-CHANGE-FILED-ACK", WhtFiledPeriodAck.PayeeChangeAckRuleCode);
        Assert.Equal("RD-50TWI-FILED", WhtFiledPeriodAck.VoidNeedsAckRuleCode);   // รหัส 409 เดิม — หน้าเว็บจับรหัสนี้แล้วเสนอช่องรับทราบ
        Assert.NotEqual(WhtFiledPeriodAck.PayeeChangeNeedsAckRuleCode, WhtPayeeChangePlan.RuleCode);   // ด่านอื่น (เหตุผลว่าง · ใบลดหนี้) ไม่เสนอช่องรับทราบ
        Assert.Contains("ท.ป.4/2528", WhtFiledPeriodAck.LegalReference);
        Assert.Contains("รับทราบ", WhtFiledPeriodAck.WayForward);   // ข้อความ 409 บอกทางไปต่อเสมอ
    }

    // ── #21 แบบ ภ.ง.ด. ที่ระบบแก้ — แจ้ง ไม่เปลี่ยนเงียบ ──

    [Fact]
    public void แบบที่ขอไม่ตรงผู้ถูกหัก_มีข้อความแจ้ง_บอกแบบเดิมแบบใหม่และทางแก้()
    {
        var n = WhtCertFormCorrection.Notice(TaxType.WithholdingTax3, TaxType.WithholdingTax53, "payee เป็นนิติบุคคล");
        Assert.NotNull(n);
        Assert.Contains("ภ.ง.ด.3", n);
        Assert.Contains("ภ.ง.ด.53", n);
        Assert.Contains("ผู้ถูกหักเป็นนิติบุคคล", n);   // ไม่มีคำภาษาอังกฤษ "payee" ถึงผู้ใช้
        Assert.DoesNotContain("payee", n);
        Assert.Contains("แก้", n);
    }

    [Fact]
    public void เหตุผลทุกแบบที่ResolveWhtFormTypeส่งมา_ไม่มีคำอังกฤษ_และไม่เว้นวรรคกลางวลีไทย()
    {
        // เหตุผลจริงจาก resolver (ไม่ใช่สตริงที่เทสต์แต่งเอง) — ต้นทางเป็นภาษาไทยแล้ว ไม่พึ่งการแปลงใน Notice
        var company = new Accounting.Models.Entities.Contact { Name = "บริษัท ก จำกัด", TaxId = "0105561040684", ContactType = ContactType.JuristicPerson };
        var person = new Accounting.Models.Entities.Contact { Name = "นาย ข", TaxId = "1101700230708", ContactType = ContactType.Individual };
        var foreign = new Accounting.Models.Entities.Contact { Name = "Acme Inc.", CountryCode = "US", ContactType = ContactType.JuristicPerson };
        foreach (var (c, asked) in new[] { (company, TaxType.WithholdingTax3), (person, TaxType.WithholdingTax53), (foreign, TaxType.WithholdingTax53) })
        {
            var (form, corrected, reason) = Accounting.Services.Implementations.WithholdingTaxCertService.ResolveWhtFormType(c, asked);
            Assert.True(corrected, c.Name);
            Assert.DoesNotContain("payee", reason);
            Assert.DoesNotContain("ContactType", reason);
            var n = WhtCertFormCorrection.Notice(asked, form, reason)!;
            Assert.DoesNotContain("payee", n);
            Assert.DoesNotContain("ผู้ถูกหัก เป็น", n);   // ไทยไม่เว้นวรรคกลางวลี
        }
        Assert.Contains("ผู้ถูกหักต่างประเทศ", WhtCertFormCorrection.Notice(TaxType.WithholdingTax53, TaxType.WithholdingTax54,
            Accounting.Services.Implementations.WithholdingTaxCertService.ResolveWhtFormType(foreign, TaxType.WithholdingTax53).reason));
        // เหตุผลเก่าที่ยังมีคำ payee (เช่นจากที่อื่น) ยังถูกแปลงไม่เว้นวรรค
        Assert.Contains("ผู้ถูกหักเป็นบุคคลธรรมดา", WhtCertFormCorrection.Notice(TaxType.WithholdingTax53, TaxType.WithholdingTax3, "payee เป็นบุคคลธรรมดา"));
        // ทิศตรงข้าม: ไม่มีเหตุผล = ไม่มี " — "
        var noReason = WhtCertFormCorrection.Notice(TaxType.WithholdingTax53, TaxType.WithholdingTax3, null)!;
        Assert.DoesNotContain(" — ", noReason);
    }

    [Fact]
    public void ทิศตรงข้าม_ไม่ได้แก้_หรือไม่ได้ขอแบบ_ไม่มีข้อความ()
    {
        Assert.Null(WhtCertFormCorrection.Notice(TaxType.WithholdingTax53, TaxType.WithholdingTax53, "payee เป็นนิติบุคคล"));
        Assert.Null(WhtCertFormCorrection.Notice(null, TaxType.WithholdingTax3, "จาก ContactType"));
    }

    // ── #20 ผู้ถูกหักไม่มีเลขผู้เสียภาษี — คนกด "สร้างใบ" ได้ร่าง · ทางอัตโนมัติ/ออกทันทีคงเดิม ──

    [Fact]
    public void คนกดสร้างใบ_ไม่ออกทันที_ได้ร่างแทนการปฏิเสธ()
        => Assert.True(WhtCertPayeeTaxIdDraft.DraftInstead("ไม่มีเลข", manualRequest: true, autoIssue: false));

    [Theory]
    [InlineData(false, false)]   // ทางอัตโนมัติ (อนุมัติ/จ่าย/ซิงค์/ทั้งเดือน) — ล้มดังเข้าลิสต์รอออกใบเหมือนเดิม
    [InlineData(false, true)]
    [InlineData(true, true)]     // คนขอให้ออกทันที — ใบเข้าแบบยื่น ต้องมีเลข
    public void ทิศตรงข้าม_ทางอัตโนมัติหรือออกทันที_ยังปฏิเสธ(bool manual, bool autoIssue)
        => Assert.False(WhtCertPayeeTaxIdDraft.DraftInstead("ไม่มีเลข", manual, autoIssue));

    [Fact]
    public void มีเลขครบ_ไม่ใช่ร่าง()
        => Assert.False(WhtCertPayeeTaxIdDraft.DraftInstead(null, true, false));

    // ── #7 หัวเอกสารขัดกับรายบรรทัด — รหัสแยก + ผู้ถูกหักสองฝั่ง ให้ผู้ใช้เลือก ──

    private static readonly Guid X = Guid.Parse("11111111-1111-1111-1111-111111111111");
    private static readonly Guid Y = Guid.Parse("22222222-2222-2222-2222-222222222222");

    [Fact]
    public void หัวเอกสารขัดกับรายบรรทัด_รหัสแยก_details_ข้อความเดิม_400()
    {
        var lines = new[] { new WhtPayeeLine(Y, 30m), new WhtPayeeLine(null, 0m) };
        var ex = WhtPayeeSplit.HeaderConflictException(X, lines);
        Assert.NotNull(ex);
        Assert.Equal(WhtPayeeSplit.HeaderConflictRuleCode, ex!.RuleCode);
        Assert.Equal("RD-50BIS-PAYEE-HEADER-CONFLICT", ex.RuleCode);
        Assert.Equal(400, ex.StatusCode);
        Assert.StartsWith("ผู้ถูกหักภาษีหัวเอกสารที่เลือกไม่ตรงกับผู้ถูกหักรายบรรทัด", ex.Message);   // ข้อความเดิมทุกตัวอักษร (ผู้เรียก API เดิม)
        Assert.Contains("ทางไปต่อ", ex.Message);
        var det = Assert.IsType<Dictionary<string, object?>>(ex.Data[WhtPayeeSplit.DetailsKey]);
        Assert.Equal(X, det["headerPayeeId"]);
        Assert.Equal(Y, det["linePayeeId"]);
    }

    [Fact]
    public void ทิศตรงข้าม_ไม่ระบุหัว_หรือมีบรรทัดตามหัว_หรือหลายราย_ไม่มีข้อผิดพลาด()
    {
        Assert.Null(WhtPayeeSplit.HeaderConflictException(null, new[] { new WhtPayeeLine(Y, 30m) }));
        Assert.Null(WhtPayeeSplit.HeaderConflictException(Guid.Empty, new[] { new WhtPayeeLine(Y, 30m) }));
        Assert.Null(WhtPayeeSplit.HeaderConflictException(X, new[] { new WhtPayeeLine(Y, 30m), new WhtPayeeLine(null, 10m) }));
        Assert.Null(WhtPayeeSplit.HeaderConflictException(X, new[] { new WhtPayeeLine(Y, 30m), new WhtPayeeLine(Guid.NewGuid(), 10m) }));
        Assert.Null(WhtPayeeSplit.HeaderConflictException(X, new[] { new WhtPayeeLine(null, 30m) })?.Message);
    }

    // ── #10 ใบลด/เพิ่มหนี้ไม่อ้างบรรทัด — ผังเดียวกัน ⇒ ไม่บล็อก (ป้ายต้องตรวจ) · ผังต่างกัน ⇒ คงด่าน ──

    [Fact]
    public void ทุกผู้ถูกหักผังเดียวกัน_คืนรหัส()
        => Assert.Equal("21917", WhtPayeeSplit.SharedPayableCode(new IReadOnlyList<string>[]
        {
            WhtPayableAccount.CodeChain(false, null, "0107537000017", ContactType.JuristicPerson, "บริษัท ประกัน ก จำกัด"),
            WhtPayableAccount.CodeChain(false, null, "0105556000017", ContactType.JuristicPerson, "บริษัท ประกัน ข จำกัด"),
        }));

    [Fact]
    public void ทิศตรงข้าม_นิติบุคคลกับบุคคล_ผังต่างกัน_null_คงด่าน()
    {
        Assert.Null(WhtPayeeSplit.SharedPayableCode(new IReadOnlyList<string>[]
        {
            WhtPayableAccount.CodeChain(false, null, "0107537000017", ContactType.JuristicPerson, "บริษัท ประกัน ก จำกัด"),
            WhtPayableAccount.CodeChain(false, null, "3100600123450", ContactType.Individual, "นายสมชาย ใจดี"),
        }));
        Assert.Null(WhtPayeeSplit.SharedPayableCode(Array.Empty<IReadOnlyList<string>>()));
        Assert.Null(WhtPayeeSplit.SharedPayableCode(new IReadOnlyList<string>[] { Array.Empty<string>() }));
    }

    [Fact]
    public void ป้ายใบต้นทาง_บอกใบ_รายการ_ผัง_และทางไปต่อ()
    {
        var r = WhtPayeeSplit.UnmappedSharedAccountReview("CN-1", "PV-1", new string?[] { "ลดเบี้ย" }, "21917");
        Assert.Contains("CN-1", r);
        Assert.Contains("PV-1", r);
        Assert.Contains("ลดเบี้ย", r);
        Assert.Contains("21917", r);
        Assert.Contains("รับทราบ", r);
    }

    [Fact]
    public void คำเตือนร่าง_ขึ้นต้นข้อความที่ตกลง_ต่อด้วยเหตุ()
    {
        Assert.Equal("ใบร่าง — เติมเลขผู้เสียภาษีก่อนออก/ยื่น", WhtCertPayeeTaxIdDraft.Warning);
        Assert.StartsWith(WhtCertPayeeTaxIdDraft.Warning, WhtCertPayeeTaxIdDraft.WarningFor("ผู้ถูกหักภาษี \"ก\" ยังไม่มีเลข"));
    }

    [Fact]
    public void ออกร่างไม่มีเลข_ทางไปต่อบอกทั้งเติมเลขและรับทราบ_รหัสกฎแยกจากaudit()
    {
        // ฝ่ายค้านรอบ 204 L5 — หน้าเว็บอ่านรหัส 409 นี้แล้วถามรับทราบ (wht.html) · audit ใช้รหัสคนละตัว
        Assert.Equal("RD-50BIS-ISSUE-NO-TAXID", WhtCertPayeeTaxIdDraft.IssueNeedsAckRuleCode);
        Assert.NotEqual(WhtCertPayeeTaxIdDraft.IssueNeedsAckRuleCode, WhtCertPayeeTaxIdDraft.IssueAckRuleCode);
        Assert.Contains("เติมเลขผู้เสียภาษี", WhtCertPayeeTaxIdDraft.IssueWayForward);
        Assert.Contains("รับทราบ — ออกโดยไม่มีเลขผู้เสียภาษี", WhtCertPayeeTaxIdDraft.IssueWayForward);
        Assert.Contains("ผู้ถูกหักภาษี \"ก\"", WhtCertPayeeTaxIdDraft.IssuedWithoutTaxIdWarning("ผู้ถูกหักภาษี \"ก\" ยังไม่มีเลข"));
    }
}
