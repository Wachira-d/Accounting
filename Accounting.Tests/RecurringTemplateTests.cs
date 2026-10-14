using System.Text.Json;
using Accounting.Helpers;
using Accounting.Models.Enums;
using Xunit;

namespace Accounting.Tests;

/// <summary>
/// ทีมตรวจรายการประจำ 2026-10-14 — ตัวอ่านเทมเพลต/ตารางเวลา/ข้อความล้ม (pure) ·
/// ที่มา: ผู้ใช้เลือกช่องทางรับ/จ่ายเป็นบัญชีธนาคารแล้วบันทึกไม่ได้ "Template มี accountId ที่ไม่พบในผังบัญชี" (bankAccountId ถูกค้นในผังบัญชี) ·
/// ทุกข้อล็อกทั้งทิศที่แก้และทิศตรงข้าม · ผลผ่านเมธอดจริงบน PostgreSQL อยู่ที่ <c>Db/RecurringTransactionDbTests</c>
/// </summary>
public class RecurringTemplateTests
{
    private static JsonElement J(string json) => JsonDocument.Parse(json).RootElement;

    private static readonly Guid Bank = Guid.Parse("f411f197-0000-4000-8000-000000000001");
    private static readonly Guid Coa = Guid.Parse("a0000000-0000-4000-8000-000000000002");
    private static readonly Guid Exp = Guid.Parse("a0000000-0000-4000-8000-000000000003");

    // ── defect class "รหัสตารางหนึ่งถูกตรวจกับอีกตาราง" ──

    [Fact]
    public void บัญชีธนาคาร_ถูกจัดเป็นทะเบียนบัญชีธนาคาร_ไม่ใช่ผังบัญชี()
    {
        var scan = RecurringTemplate.Read(J($$"""
            {"lines":[{"description":"ค่าทำบัญชี","quantity":1,"unitPrice":5000,"accountId":"{{Coa}}"}],
             "bankAccountId":"{{Bank}}","expenseCategoryId":"{{Exp}}"}
            """), isJournal: false);
        Assert.Empty(scan.Problems);
        var bank = Assert.Single(scan.Refs, r => r.Id == Bank);
        Assert.Equal(RecurringTemplate.RefKind.BankAccount, bank.Kind);   // ★ จุดที่เคยผิด
        Assert.All(scan.Refs.Where(r => r.Id != Bank), r => Assert.Equal(RecurringTemplate.RefKind.ChartOfAccount, r.Kind));
        Assert.Equal(1, scan.LineCount);
    }

    [Fact]
    public void ช่องทางรับจ่ายแบบผังบัญชี_ยังเป็นผังบัญชี()
    {
        var scan = RecurringTemplate.Read(J($$"""{"lines":[{"unitPrice":1}],"paymentAccountId":"{{Coa}}","bankAccountId":null}"""), false);
        var r = Assert.Single(scan.Refs);
        Assert.Equal(RecurringTemplate.RefKind.ChartOfAccount, r.Kind);
        Assert.Contains("ช่องทางรับ/จ่ายเงิน", r.Field);
    }

    [Fact]
    public void โครงการ_สาขา_ผู้ถูกหัก_ถูกเก็บแยกชนิด()
    {
        var p = Guid.NewGuid(); var b = Guid.NewGuid(); var w = Guid.NewGuid(); var lp = Guid.NewGuid();
        var scan = RecurringTemplate.Read(J($$"""
            {"lines":[{"unitPrice":1,"projectId":"{{lp}}","whtPayeeContactId":"{{w}}"}],"projectId":"{{p}}","branchId":"{{b}}"}
            """), false);
        Assert.Equal(RecurringTemplate.RefKind.Project, scan.Refs.Single(r => r.Id == p).Kind);
        Assert.Equal(RecurringTemplate.RefKind.Project, scan.Refs.Single(r => r.Id == lp).Kind);
        Assert.Equal(RecurringTemplate.RefKind.Branch, scan.Refs.Single(r => r.Id == b).Kind);
        Assert.Equal(RecurringTemplate.RefKind.WhtPayeeContact, scan.Refs.Single(r => r.Id == w).Kind);
        Assert.Contains("บรรทัดที่ 1", scan.Refs.Single(r => r.Id == lp).Field);
    }

    [Fact]
    public void ค่าว่าง_null_GuidEmpty_คือไม่ระบุ_ไม่ใช่ปัญหา()
    {
        var scan = RecurringTemplate.Read(J("""
            {"lines":[{"unitPrice":1,"accountId":""}],"bankAccountId":"","paymentAccountId":null,"expenseCategoryId":"00000000-0000-0000-0000-000000000000"}
            """), false);
        Assert.Empty(scan.Problems);
        Assert.Empty(scan.Refs);
    }

    [Fact]
    public void รหัสอ่านไม่ได้_และตัวเลขอ่านไม่ได้_รายงานพร้อมบรรทัดและชื่อช่องไทย_ไม่มีGUID()
    {
        var scan = RecurringTemplate.Read(J("""
            {"lines":[{"unitPrice":"abc","accountId":"not-a-guid","vatRate":true}],"bankAccountId":"zzz"}
            """), false);
        Assert.Contains(scan.Problems, x => x.Contains("บรรทัดที่ 1") && x.Contains("ราคาต่อหน่วย"));
        Assert.Contains(scan.Problems, x => x.Contains("บรรทัดที่ 1") && x.Contains("บัญชี"));
        Assert.Contains(scan.Problems, x => x.Contains("VAT"));
        Assert.Contains(scan.Problems, x => x.Contains("บัญชีธนาคาร"));
        Assert.DoesNotContain(scan.Problems, x => x.Contains("unitPrice") || x.Contains("bankAccountId"));
    }

    [Fact]
    public void ตัวเลขสตริงมีจุลภาค_อ่านได้เหมือนยอดบนหน้าจอ()
    {
        var line = J("""{"quantity":"2","unitPrice":"1,200.50","discountAmount":null}""");
        Assert.Equal(2m, RecurringTemplate.TryNumber(line, "quantity", 1m));
        Assert.Equal(1200.50m, RecurringTemplate.TryNumber(line, "unitPrice", 0m));
        Assert.Equal(0m, RecurringTemplate.TryNumber(line, "discountAmount", 0m));
        Assert.Equal(7m, RecurringTemplate.TryNumber(line, "vatRate", 7m));     // ไม่มี key = ค่าตั้งต้น
        Assert.Null(RecurringTemplate.TryNumber(J("""{"q":"x"}"""), "q", 1m));   // อ่านไม่ได้ ≠ ค่าตั้งต้น
        Assert.Empty(RecurringTemplate.Read(J("""{"lines":[{"quantity":"2","unitPrice":"1,200.50"}]}"""), false).Problems);
    }

    [Fact]
    public void ตอนสร้างเอกสาร_ค่าอ่านไม่ได้โยนกฎธุรกิจพร้อมบรรทัด_ไม่ใช่FormatException()
    {
        var line = J("""{"accountId":"bad","unitPrice":"x"}""");
        var ex1 = Assert.Throws<BusinessRuleException>(() => RecurringTemplate.ReadGuid(line, "accountId", 2));
        Assert.Contains("บรรทัดที่ 2", ex1.Message);
        var ex2 = Assert.Throws<BusinessRuleException>(() => RecurringTemplate.Number(line, "unitPrice", 0m, 2));
        Assert.Contains("ราคาต่อหน่วย", ex2.Message);
        Assert.Equal(RecurringTemplate.RuleCode, ex2.RuleCode);
        Assert.Equal(Bank, RecurringTemplate.ReadGuid(J($$"""{"bankAccountId":"{{Bank}}"}"""), "bankAccountId", 0));
    }

    [Fact]
    public void สมุดรายวัน_บรรทัดไม่มีบัญชี_เป็นปัญหา_เอกสารไม่ใช่()
    {
        var json = """{"lines":[{"debitAmount":100},{"creditAmount":100,"accountId":"a0000000-0000-4000-8000-000000000002"}]}""";
        Assert.Contains(RecurringTemplate.Read(J(json), isJournal: true).Problems, x => x.Contains("บรรทัดที่ 1") && x.Contains("ยังไม่ได้เลือกบัญชี"));
        // ชนิดเอกสาร: บรรทัดไม่มีบัญชี = ใช้บัญชีตั้งต้นของเอกสาร (ไม่ใช่ปัญหา)
        Assert.DoesNotContain(RecurringTemplate.Read(J("""{"lines":[{"unitPrice":1}]}"""), isJournal: false).Problems, x => x.Contains("ยังไม่ได้เลือกบัญชี"));
        // สมุดรายวันไม่มีช่องทางรับจ่าย — bankAccountId ไม่ถูกนับ
        Assert.DoesNotContain(RecurringTemplate.Read(J($$"""{"lines":[],"bankAccountId":"{{Bank}}"}"""), true).Refs, r => r.Kind == RecurringTemplate.RefKind.BankAccount);
    }

    [Fact]
    public void รูปแบบรากไม่ใช่ออบเจ็กต์_รายงานปัญหา()
        => Assert.NotEmpty(RecurringTemplate.Read(J("[]"), false).Problems);

    // ── ตารางเวลา ──

    private static DateTime D(int y, int m, int d) => new(y, m, d, 0, 0, 0, DateTimeKind.Utc);

    [Fact]
    public void รายเดือนวันที่31_ไม่ไหล_กุมภาแล้วกลับ31()
    {
        var feb = RecurringSchedule.Next(RecurringFrequency.Monthly, D(2026, 1, 31), 31, D(2026, 1, 31));
        Assert.Equal(D(2026, 2, 28), feb);
        Assert.Equal(D(2026, 3, 31), RecurringSchedule.Next(RecurringFrequency.Monthly, feb, 31, D(2026, 1, 31)));
        Assert.Equal(D(2028, 2, 29), RecurringSchedule.Next(RecurringFrequency.Annual, D(2027, 2, 28), 29, D(2024, 2, 29)));
        Assert.Equal(D(2026, 1, 8), RecurringSchedule.Next(RecurringFrequency.Weekly, D(2026, 1, 1), 1, D(2026, 1, 1)));
    }

    [Fact]
    public void วันสิ้นสุดนับรวมวันนั้น_แม้cronเดินหลัง7โมงเช้า()
    {
        var end = D(2026, 12, 31);
        // 31 ธ.ค. 08:15 น. เวลาไทย = 01:15 UTC — เดิม EndDate(00:00 UTC) >= now เป็นเท็จ ⇒ งวดสุดท้ายไม่เคยออก
        Assert.True(RecurringSchedule.WithinEnd(new DateTime(2026, 12, 31, 1, 15, 0, DateTimeKind.Utc), end));
        Assert.True(RecurringSchedule.WithinEnd(D(2026, 12, 31), end));
        // ทิศตรงข้าม: วันถัดไป (เวลาไทย) = เลยแล้ว
        Assert.False(RecurringSchedule.WithinEnd(new DateTime(2026, 12, 31, 17, 30, 0, DateTimeKind.Utc), end));
        Assert.False(RecurringSchedule.WithinEnd(D(2027, 1, 1), end));
        Assert.True(RecurringSchedule.WithinEnd(D(2099, 1, 1), null));
    }

    [Fact]
    public void จบเมื่อครบจำนวนครั้งหรือกำหนดถัดไปเลยวันสิ้นสุด()
    {
        Assert.True(RecurringSchedule.IsFinished(D(2026, 5, 1), null, 3, 3));
        Assert.False(RecurringSchedule.IsFinished(D(2026, 5, 1), null, 2, 3));
        Assert.True(RecurringSchedule.IsFinished(D(2026, 5, 1), D(2026, 4, 30), 0, null));
        Assert.False(RecurringSchedule.IsFinished(D(2026, 4, 30), D(2026, 4, 30), 0, null));
    }

    [Fact]
    public void วันฐานตัวแปรเดือน_รอบตามงวดใช้วันของงวด_รอบพิเศษใช้วันนี้()
    {
        var now = new DateTime(2026, 10, 14, 3, 0, 0, DateTimeKind.Utc);
        // ใบตามหลังของงวด ส.ค. ⇒ <<month>> = สิงหาคม (เดิมได้ ตุลาคม ทุกใบ)
        Assert.Equal(D(2026, 8, 1), RecurringSchedule.PlaceholderBaseDate(D(2026, 8, 1), now, consumesPeriod: true));
        Assert.Equal(D(2026, 10, 14), RecurringSchedule.PlaceholderBaseDate(D(2026, 11, 1), now, consumesPeriod: false));
        // งวดวันนี้ (ปกติ) = วันนี้เหมือนเดิม
        Assert.Equal(D(2026, 10, 14), RecurringSchedule.PlaceholderBaseDate(D(2026, 10, 14), now, consumesPeriod: true));
    }

    // ── ข้อความล้มถึงผู้ใช้ ──

    [Fact]
    public void ข้อความล้ม_กฎธุรกิจไทยผ่าน_ข้อความเทคนิคอังกฤษถูกแทน()
    {
        Assert.Equal("บัญชีธนาคารถูกปิดใช้งาน", RecurringRunFailure.UserMessage(new BusinessRuleException("บัญชีธนาคารถูกปิดใช้งาน")));
        Assert.Equal("ไม่พบผู้ติดต่อในบริษัทนี้", RecurringRunFailure.UserMessage(new InvalidOperationException("ไม่พบผู้ติดต่อในบริษัทนี้")));
        var tech = RecurringRunFailure.UserMessage(new InvalidOperationException(
            "The connection is already in a transaction and cannot participate in another transaction."));
        Assert.DoesNotContain("connection", tech);
        Assert.Contains("สร้างทันที", tech);
        Assert.DoesNotContain("Format", RecurringRunFailure.UserMessage(new FormatException("Guid should contain 32 digits")));
        Assert.True(RecurringRunFailure.UserMessage(new BusinessRuleException(new string('ก', 5000))).Length <= 1001);
    }
}
