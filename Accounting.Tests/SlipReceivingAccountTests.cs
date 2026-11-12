using Accounting.Helpers;
using Accounting.Models.Enums;
using Xunit;

namespace Accounting.Tests;

/// <summary>
/// รอบ 260-2 — ตัวตัดสิน "บัญชีที่เงินตามสลิปเข้า" (<see cref="SlipReceivingAccount"/>) · สองทิศทุกด่าน:
/// บัญชีที่เลือกถูกต้อง ⇒ ใช้ตามที่เลือก · บัญชีของบริษัทอื่น/ปิดใช้/ฝากประจำ/ไม่มีเลข/สกุลไม่ตรง/ผังใช้ไม่ได้ ⇒ ปฏิเสธพร้อมเหตุ ·
/// ไม่เลือก ⇒ ลำดับค่าแนะนำ (แหล่งเงินของเอกสาร = เท่าเดิม → บัญชีที่แสดงให้ลูกค้า → บัญชีเดียว → ไม่เดา) · เงินผ่านผู้ให้บริการไม่ใช้ตัวนี้
/// </summary>
public class SlipReceivingAccountTests
{
    private static readonly Guid A = Guid.Parse("aaaaaaaa-0000-0000-0000-000000000001");
    private static readonly Guid B = Guid.Parse("bbbbbbbb-0000-0000-0000-000000000002");
    private static readonly Guid C = Guid.Parse("cccccccc-0000-0000-0000-000000000003");

    private static SlipReceivingAccount.AccountFacts Facts(Guid id, bool active = true, string currency = "THB", string type = "Savings",
        bool transfer = true, string? glProblem = null)
        => new(id, "กสิกรไทย 123-4-56789-0 (บริษัท)", active, currency, type, transfer, glProblem);

    // ═══ ใช้กับรายการไหน ═══

    [Fact]
    public void Applies_เฉพาะสลิปโอน_ไม่ใช่เงินผ่านผู้ให้บริการ()
    {
        Assert.True(SlipReceivingAccount.Applies("manual-slip", PaymentMethodKind.ManualSlip));
        Assert.False(SlipReceivingAccount.Applies("omise", PaymentMethodKind.PromptPay));
        Assert.False(SlipReceivingAccount.Applies("omise", PaymentMethodKind.ManualSlip));
        Assert.False(SlipReceivingAccount.Applies("manual-slip", PaymentMethodKind.Card));
    }

    // ═══ บัญชีที่เลือก — ทิศที่ผ่าน ═══

    [Fact]
    public void เลือกบัญชีที่ใช้ได้_ไม่มีปัญหา_และผลตัดสินใช้ตามที่เลือก()
    {
        Assert.Null(SlipReceivingAccount.ChoiceProblem(true, Facts(B), "THB"));
        var s = SlipReceivingAccount.Suggest(true, false, null, A, new[] { A, B });
        var d = SlipReceivingAccount.Decide(true, B, Facts(B), "THB", s);
        Assert.True(d.Ok);
        Assert.Equal(B, d.BankAccountId);
        Assert.Equal(SlipReceivingAccount.Origin.Chosen, d.Origin);
        Assert.True(d.Overridden);   // แนะนำ A แต่เลือก B ⇒ audit ต้องเห็นว่าเลือกต่างจากคำแนะนำ
    }

    [Fact]
    public void เลือกตรงกับคำแนะนำ_ไม่นับว่าแทนคำแนะนำ()
    {
        var s = SlipReceivingAccount.Suggest(true, false, null, A, new[] { A, B });
        var d = SlipReceivingAccount.Decide(true, A, Facts(A), "THB", s);
        Assert.Equal(A, d.BankAccountId);
        Assert.False(d.Overridden);
    }

    [Fact]
    public void เลือกบัญชีเดียวกับแหล่งเงินของเอกสาร_ไม่ส่ง_override_ผลเท่าเดิม()
    {
        var s = SlipReceivingAccount.Suggest(true, true, A, null, new[] { A, B });
        var d = SlipReceivingAccount.Decide(true, A, Facts(A), "THB", s);
        Assert.True(d.Ok);
        Assert.Null(d.BankAccountId);
        Assert.Equal(SlipReceivingAccount.Origin.DocumentChannel, d.Origin);
    }

    // ═══ บัญชีที่เลือก — ทิศที่ถูกปฏิเสธ (พร้อมเหตุ) ═══

    [Fact]
    public void บัญชีของบริษัทอื่นหรือถูกลบ_ปฏิเสธ()
    {
        var p = SlipReceivingAccount.ChoiceProblem(true, null, "THB");
        Assert.Contains("ไม่พบบัญชีธนาคาร", p);
        var s = SlipReceivingAccount.Suggest(true, false, null, null, new[] { A });
        var d = SlipReceivingAccount.Decide(true, C, null, "THB", s);
        Assert.False(d.Ok);
        Assert.Null(d.BankAccountId);
    }

    [Theory]
    [InlineData(false, "THB", "Savings", true, null, "ปิดใช้งาน")]
    [InlineData(true, "USD", "Savings", true, null, "สกุล")]
    [InlineData(true, "THB", "Fixed", true, null, "ฝากประจำ")]
    [InlineData(true, "THB", "Savings", false, null, "ไม่มีเลขบัญชี")]
    [InlineData(true, "THB", "Savings", true, "ยังไม่ได้ผูกผังบัญชี", "ผูกผัง")]
    public void บัญชีที่ใช้รับโอนไม่ได้_ปฏิเสธพร้อมเหตุ(bool active, string currency, string type, bool transfer, string? gl, string expect)
    {
        var p = SlipReceivingAccount.ChoiceProblem(true, Facts(B, active, currency, type, transfer, gl), "THB");
        Assert.NotNull(p);
        Assert.Contains(expect, p);
    }

    [Fact]
    public void เงินผ่านผู้ให้บริการ_เลือกธนาคารไม่ได้_แต่ไม่เลือกยังยืนยันได้()
    {
        Assert.Contains("ผู้ให้บริการ", SlipReceivingAccount.ChoiceProblem(false, Facts(A), "THB"));
        var s = SlipReceivingAccount.Suggest(false, false, null, A, new[] { A });
        Assert.Equal(SlipReceivingAccount.Origin.NotApplicable, s.Origin);
        Assert.Null(s.BankAccountId);
        Assert.False(SlipReceivingAccount.Decide(false, A, Facts(A), "THB", s).Ok);
        var none = SlipReceivingAccount.Decide(false, null, null, "THB", s);
        Assert.True(none.Ok);
        Assert.Null(none.BankAccountId);   // ⇒ handler ใช้บัญชีพักของ IGatewayAccountResolver ตามเดิม
    }

    // ═══ ค่าแนะนำ (ไม่เลือก) — ลำดับตายตัว ═══

    [Fact]
    public void ไม่เลือก_เอกสารมีแหล่งเงิน_สืบทอดตามเดิม_ไม่ส่ง_override()
    {
        var s = SlipReceivingAccount.Suggest(true, true, A, B, new[] { A, B });
        Assert.Equal(SlipReceivingAccount.Origin.DocumentChannel, s.Origin);
        Assert.Null(s.BankAccountId);
        Assert.Equal(A, s.DisplayBankAccountId);
        var d = SlipReceivingAccount.Decide(true, null, null, "THB", s);
        Assert.Null(d.BankAccountId);
    }

    [Fact]
    public void ไม่เลือก_แหล่งเงินของเอกสารเป็นผังตรงไม่ใช่ธนาคาร_สืบทอดตามเดิม_ไม่มีบัญชีให้แสดง()
    {
        var s = SlipReceivingAccount.Suggest(true, true, null, B, new[] { A, B });
        Assert.Equal(SlipReceivingAccount.Origin.DocumentChannel, s.Origin);
        Assert.Null(s.BankAccountId);
        Assert.Null(s.DisplayBankAccountId);
    }

    [Fact]
    public void ไม่เลือก_ไม่มีแหล่งเงิน_ใช้บัญชีที่แสดงให้ลูกค้าโอน()
    {
        var s = SlipReceivingAccount.Suggest(true, false, null, B, new[] { A, B });
        Assert.Equal(SlipReceivingAccount.Origin.ShownToCustomer, s.Origin);
        Assert.Equal(B, s.BankAccountId);
    }

    [Fact]
    public void บัญชีที่แสดงให้ลูกค้าใช้ไม่ได้_ไปชั้นถัดไป_บัญชีเดียว()
    {
        var s = SlipReceivingAccount.Suggest(true, false, null, C, new[] { A });
        Assert.Equal(SlipReceivingAccount.Origin.SoleTransferAccount, s.Origin);
        Assert.Equal(A, s.BankAccountId);
    }

    [Fact]
    public void หลายบัญชีไม่มีหลักฐาน_ไม่เดา_คงพฤติกรรมเดิม()
    {
        var s = SlipReceivingAccount.Suggest(true, false, null, null, new[] { A, B });
        Assert.Equal(SlipReceivingAccount.Origin.None, s.Origin);
        Assert.Null(s.BankAccountId);
        Assert.Contains("ไม่เดา", s.Reason);
        var zero = SlipReceivingAccount.Suggest(true, false, null, null, Array.Empty<Guid>());
        Assert.Equal(SlipReceivingAccount.Origin.None, zero.Origin);
        Assert.Contains("ยังไม่มีบัญชีธนาคารที่รับโอนได้", zero.Reason);
    }

    // ═══ ค่าที่ประทับ / ป้าย / จับคู่เลขบัญชี ═══

    [Fact]
    public void ค่าที่ประทับ_อ่านกลับได้_ไม่ประทับ_คืน_null()
    {
        var chosen = SlipReceivingAccount.StampedDecision(B, "Chosen");
        Assert.NotNull(chosen);
        Assert.Equal(B, chosen!.Value.BankAccountId);
        Assert.Equal(SlipReceivingAccount.Origin.Chosen, chosen.Value.Origin);
        var inherit = SlipReceivingAccount.StampedDecision(null, "DocumentChannel");
        Assert.NotNull(inherit);
        Assert.Null(inherit!.Value.BankAccountId);
        Assert.Equal(SlipReceivingAccount.Origin.DocumentChannel, inherit.Value.Origin);
        Assert.Null(SlipReceivingAccount.StampedDecision(B, null));
        Assert.Null(SlipReceivingAccount.StampedDecision(B, "ไม่รู้จัก"));
        foreach (var o in Enum.GetValues<SlipReceivingAccount.Origin>())
            Assert.False(string.IsNullOrWhiteSpace(SlipReceivingAccount.OriginLabel(o.ToString())));
        Assert.Null(SlipReceivingAccount.OriginLabel(null));
    }

    [Fact]
    public void เลขบัญชีของเว็บ_จับคู่ด้วยตัวเลขเท่านั้น_ซ้ำหรือสั้นไม่เดา()
    {
        var banks = new (Guid, string?)[] { (A, "123-4-56789-0"), (B, "999 888 7777") };
        Assert.Equal(A, SlipReceivingAccount.MatchByAccountNumber("1234567890", banks));
        Assert.Equal(B, SlipReceivingAccount.MatchByAccountNumber("999-888-7777", banks));
        Assert.Null(SlipReceivingAccount.MatchByAccountNumber("000", banks));
        Assert.Null(SlipReceivingAccount.MatchByAccountNumber(null, banks));
        Assert.Null(SlipReceivingAccount.MatchByAccountNumber("1234567890", new (Guid, string?)[] { (A, "1234567890"), (C, "123-456-7890") }));
    }

    [Fact]
    public void migration_เพิ่มสองคอลัมน์แบบ_idempotent()
    {
        var sql = string.Join("\n", Round260SlipReceivingMigration.Statements());
        Assert.Contains("ADD COLUMN IF NOT EXISTS \"ReceivingBankAccountId\"", sql);
        Assert.Contains("ADD COLUMN IF NOT EXISTS \"ReceivingAccountOrigin\"", sql);
    }
}
