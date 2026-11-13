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
        var s = SlipReceivingAccount.Suggest(true, SlipReceivingAccount.DocChannel.None, A, SlipReceivingAccount.SiteRowMatch.NoRow, new[] { A, B });
        var d = SlipReceivingAccount.Decide(true, B, Facts(B), "THB", s);
        Assert.True(d.Ok);
        Assert.Equal(B, d.BankAccountId);
        Assert.Equal(SlipReceivingAccount.Origin.Chosen, d.Origin);
        Assert.True(d.Overridden);   // แนะนำ A แต่เลือก B ⇒ audit ต้องเห็นว่าเลือกต่างจากคำแนะนำ
    }

    [Fact]
    public void เลือกตรงกับคำแนะนำ_ไม่นับว่าแทนคำแนะนำ()
    {
        var s = SlipReceivingAccount.Suggest(true, SlipReceivingAccount.DocChannel.None, A, SlipReceivingAccount.SiteRowMatch.NoRow, new[] { A, B });
        var d = SlipReceivingAccount.Decide(true, A, Facts(A), "THB", s);
        Assert.Equal(A, d.BankAccountId);
        Assert.False(d.Overridden);
    }

    [Fact]
    public void เลือกบัญชีเดียวกับแหล่งเงินของเอกสาร_ไม่ส่ง_override_ผลเท่าเดิม()
    {
        var s = SlipReceivingAccount.Suggest(true, BankDoc(A), null, SlipReceivingAccount.SiteRowMatch.NoRow, new[] { A, B });
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
        var s = SlipReceivingAccount.Suggest(true, SlipReceivingAccount.DocChannel.None, null, SlipReceivingAccount.SiteRowMatch.NoRow, new[] { A });
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
        var s = SlipReceivingAccount.Suggest(false, SlipReceivingAccount.DocChannel.None, A, SlipReceivingAccount.SiteRowMatch.NoRow, new[] { A });
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
        var s = SlipReceivingAccount.Suggest(true, BankDoc(A), B, SlipReceivingAccount.SiteRowMatch.NoRow, new[] { A, B });
        Assert.Equal(SlipReceivingAccount.Origin.DocumentChannel, s.Origin);
        Assert.Null(s.BankAccountId);
        Assert.Equal(A, s.DisplayBankAccountId);
        var d = SlipReceivingAccount.Decide(true, null, null, "THB", s);
        Assert.Null(d.BankAccountId);
    }

    [Fact]
    public void ไม่เลือก_แหล่งเงินของเอกสารเป็นผังตรงไม่ใช่ธนาคาร_สืบทอดตามเดิม_ไม่มีบัญชีให้แสดง()
    {
        var s = SlipReceivingAccount.Suggest(true, new SlipReceivingAccount.DocChannel(true, false, null, null), B, SlipReceivingAccount.SiteRowMatch.NoRow, new[] { A, B });
        Assert.Equal(SlipReceivingAccount.Origin.DocumentChannel, s.Origin);
        Assert.Null(s.BankAccountId);
        Assert.Null(s.DisplayBankAccountId);
    }

    [Fact]
    public void ไม่เลือก_ไม่มีแหล่งเงิน_ใช้บัญชีที่แสดงให้ลูกค้าโอน()
    {
        var s = SlipReceivingAccount.Suggest(true, SlipReceivingAccount.DocChannel.None, B, SlipReceivingAccount.SiteRowMatch.NoRow, new[] { A, B });
        Assert.Equal(SlipReceivingAccount.Origin.ShownToCustomer, s.Origin);
        Assert.Equal(B, s.BankAccountId);
    }

    [Fact]
    public void บัญชีที่แสดงให้ลูกค้าใช้ไม่ได้_ไปชั้นถัดไป_บัญชีเดียว()
    {
        var s = SlipReceivingAccount.Suggest(true, SlipReceivingAccount.DocChannel.None, C, SlipReceivingAccount.SiteRowMatch.NoRow, new[] { A });
        Assert.Equal(SlipReceivingAccount.Origin.SoleTransferAccount, s.Origin);
        Assert.Equal(A, s.BankAccountId);
    }

    [Fact]
    public void หลายบัญชีไม่มีหลักฐาน_ไม่เดา_คงพฤติกรรมเดิม()
    {
        var s = SlipReceivingAccount.Suggest(true, SlipReceivingAccount.DocChannel.None, null, SlipReceivingAccount.SiteRowMatch.NoRow, new[] { A, B });
        Assert.Equal(SlipReceivingAccount.Origin.None, s.Origin);
        Assert.Null(s.BankAccountId);
        Assert.Contains("ไม่เดา", s.Reason);
        var zero = SlipReceivingAccount.Suggest(true, SlipReceivingAccount.DocChannel.None, null, SlipReceivingAccount.SiteRowMatch.NoRow, Array.Empty<Guid>());
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
        var banks = new (Guid, string?, string?)[] { (A, "123-4-56789-0", "กสิกร"), (B, "999 888 7777", null) };
        Assert.Equal(A, SlipReceivingAccount.MatchByAccountNumber("1234567890", "ธนาคารกสิกรไทย (KBANK)", banks));
        Assert.Equal(B, SlipReceivingAccount.MatchByAccountNumber("999-888-7777", "ไทยพาณิชย์", banks));
        Assert.Null(SlipReceivingAccount.MatchByAccountNumber("000", null, banks));
        Assert.Null(SlipReceivingAccount.MatchByAccountNumber(null, null, banks));
        Assert.Null(SlipReceivingAccount.MatchByAccountNumber("1234567890", null, new (Guid, string?, string?)[] { (A, "1234567890", null), (C, "123-456-7890", null) }));
        // ฝ่ายค้าน L4: เลขตรงแต่ธนาคารคนละเจ้า ⇒ ไม่ตรง
        Assert.Null(SlipReceivingAccount.MatchByAccountNumber("1234567890", "ธนาคารกรุงไทย (KTB)", banks));
        Assert.True(SlipReceivingAccount.SameBank("ธนาคารกสิกรไทย (KBANK)", "กสิกร"));
        Assert.False(SlipReceivingAccount.SameBank("ธนาคารกสิกรไทย (KBANK)", "ธนาคารกรุงไทย (KTB)"));
        Assert.True(SlipReceivingAccount.SameBank(null, "อะไรก็ได้"));
    }

    private static SlipReceivingAccount.DocChannel BankDoc(Guid bank) => new(true, true, bank, null);

    // ═══ ฝ่ายค้าน 260-2 ═══

    [Fact]
    public void M1_ผังตรงที่ผูกธนาคาร_ส่งธนาคารเป็น_override_ทั้งไม่เลือกและเลือกตรงค่าแนะนำ()
    {
        var doc = new SlipReceivingAccount.DocChannel(true, false, B, null);
        var s = SlipReceivingAccount.Suggest(true, doc, null, SlipReceivingAccount.SiteRowMatch.NoRow, new[] { A, B });
        Assert.Equal(SlipReceivingAccount.Origin.DocumentChannel, s.Origin);
        Assert.Equal(B, s.BankAccountId);
        Assert.Equal(B, SlipReceivingAccount.Decide(true, null, null, "THB", s).BankAccountId);
        var accepted = SlipReceivingAccount.Decide(true, B, Facts(B), "THB", s);
        Assert.Equal(B, accepted.BankAccountId);
        Assert.True(accepted.AcceptedSuggestion);
        // ทิศตรงข้าม: แหล่งเงินเป็นธนาคารเอง ⇒ สืบทอด (ไม่ override)
        var own = SlipReceivingAccount.Suggest(true, BankDoc(A), null, SlipReceivingAccount.SiteRowMatch.NoRow, new[] { A, B });
        Assert.Null(SlipReceivingAccount.Decide(true, A, Facts(A), "THB", own).BankAccountId);
    }

    [Fact]
    public void M3_บัญชีรับโอนของเว็บมีแต่ไม่ตรง_ไม่ตกไปบัญชีเดียว_ไม่มีแถว_ยังใช้บัญชีเดียว()
    {
        var mismatch = SlipReceivingAccount.Suggest(true, SlipReceivingAccount.DocChannel.None, null, SlipReceivingAccount.SiteRowMatch.Unmatched, new[] { A });
        Assert.Equal(SlipReceivingAccount.Origin.None, mismatch.Origin);
        Assert.Null(mismatch.BankAccountId);
        var noRow = SlipReceivingAccount.Suggest(true, SlipReceivingAccount.DocChannel.None, null, SlipReceivingAccount.SiteRowMatch.NoRow, new[] { A });
        Assert.Equal(SlipReceivingAccount.Origin.SoleTransferAccount, noRow.Origin);
    }

    [Fact]
    public void M4_แหล่งเงินเอกสารใช้ไม่ได้_ไม่เลือก_ปฏิเสธพร้อมเหตุ_เลือกบัญชีอื่นผ่าน()
    {
        var doc = new SlipReceivingAccount.DocChannel(true, true, A, "บัญชีธนาคาร “x” ถูกปิดใช้งาน");
        var s = SlipReceivingAccount.Suggest(true, doc, null, SlipReceivingAccount.SiteRowMatch.NoRow, new[] { B });
        Assert.Equal(SlipReceivingAccount.Origin.None, s.Origin);
        var none = SlipReceivingAccount.Decide(true, null, null, "THB", s);
        Assert.False(none.Ok);
        Assert.Contains("ปิดใช้งาน", none.Problem);
        var other = SlipReceivingAccount.Decide(true, B, Facts(B), "THB", s);
        Assert.True(other.Ok);
        Assert.Equal(B, other.BankAccountId);
    }

    [Fact]
    public void L2_ยอมรับค่าแนะนำ_คงที่มาของค่าแนะนำ()
    {
        var s = SlipReceivingAccount.Suggest(true, SlipReceivingAccount.DocChannel.None, A, SlipReceivingAccount.SiteRowMatch.NoRow, new[] { A, B });
        var d = SlipReceivingAccount.Decide(true, A, Facts(A), "THB", s);
        Assert.Equal(SlipReceivingAccount.Origin.ShownToCustomer, d.Origin);
        Assert.True(d.AcceptedSuggestion);
        Assert.False(d.Overridden);
    }

    [Fact]
    public void migration_เพิ่มสองคอลัมน์แบบ_idempotent()
    {
        var sql = string.Join("\n", Round260SlipReceivingMigration.Statements());
        Assert.Contains("ADD COLUMN IF NOT EXISTS \"ReceivingBankAccountId\"", sql);
        Assert.Contains("ADD COLUMN IF NOT EXISTS \"ReceivingAccountOrigin\"", sql);
    }

    // ═══ รอบ 264 แพ็ก P2 (คำตัดสิน 264-4-6): ชั้นบนสุด "ผู้รับบนสลิป" — สลิปจริงกรุงไทย → กสิกร ••1681 ═══

    private static readonly SlipReceivingAccount.AccountFacts[] Accts =
    {
        new(A, "กสิกร", true, "THB", "Savings", true, null, "ธนาคารกสิกรไทย (KBANK)", "123-4-56168-1"),
        new(B, "กรุงไทย", true, "THB", "Savings", true, null, "กรุงไทย", "987-6-54321-0"),
    };

    private static SlipAccountMatch.Result? PayeeOf(SlipCorpusRound264.Slip s, IReadOnlyList<SlipReceivingAccount.AccountFacts> accts)
        => SlipReceivingAccount.SlipPayeeMatch(OcrSlipText.ToStoredJson(OcrSlipText.Parse(s.Text, 2026, s.Qr)), accts);

    [Fact]
    public void ชั้นสลิป_ผู้รับตรงบัญชีเดียว_ชนะบัญชีที่แสดงให้ลูกค้า_และบัญชีเดียว()
    {
        var slip = PayeeOf(SlipCorpusRound264.KtbToKbank, Accts);
        Assert.Equal(SlipAccountMatch.Kind.Unique, slip!.Kind);
        var s = SlipReceivingAccount.Suggest(true, SlipReceivingAccount.DocChannel.None, B, SlipReceivingAccount.SiteRowMatch.NoRow, new[] { A, B }, slip);
        Assert.Equal((A, A, SlipReceivingAccount.Origin.SlipPayee), (s.BankAccountId!.Value, s.DisplayBankAccountId!.Value, s.Origin));
        Assert.Contains("กสิกรไทย ••1681", s.Reason);
        Assert.Contains("สลิปไม่ใช่หลักฐานว่าเงินเข้า", s.Reason);   // ยังเป็นค่าแนะนำ (260-1-6)
        Assert.Equal("บัญชีผู้รับบนสลิป", SlipReceivingAccount.OriginLabel("SlipPayee"));
        // ชนะแหล่งเงินของเอกสารที่เป็นอีกบัญชี (ปลายทางจริงบนสลิป)
        var overDoc = SlipReceivingAccount.Suggest(true, BankDoc(B), null, SlipReceivingAccount.SiteRowMatch.NoRow, new[] { A, B }, slip);
        Assert.Equal((A, SlipReceivingAccount.Origin.SlipPayee), (overDoc.BankAccountId!.Value, overDoc.Origin));
        // ตรงบัญชีเดียวกับแหล่งเงินของเอกสาร ⇒ กิ่งเดิมทุกประการ (ไม่ส่ง override เพิ่ม)
        var same = SlipReceivingAccount.Suggest(true, BankDoc(A), null, SlipReceivingAccount.SiteRowMatch.NoRow, new[] { A, B }, slip);
        Assert.Equal(SlipReceivingAccount.Suggest(true, BankDoc(A), null, SlipReceivingAccount.SiteRowMatch.NoRow, new[] { A, B }), same);
    }

    [Fact]
    public void ชั้นสลิป_ผู้รับไม่ใช่บัญชีของบริษัท_เตือนและต้องเลือกเอง_ไม่แนะนำบัญชีอื่นแทน()
    {
        var others = new SlipReceivingAccount.AccountFacts[]
        {
            new(A, "กสิกร", true, "THB", "Savings", true, null, "กสิกรไทย", "123-4-56000-0"),
            new(B, "กรุงไทย", true, "THB", "Savings", true, null, "กรุงไทย", "987-6-54321-0"),
        };
        var slip = PayeeOf(SlipCorpusRound264.KtbToKbank, others);
        Assert.Equal(SlipAccountMatch.Kind.NotOurs, slip!.Kind);
        var s = SlipReceivingAccount.Suggest(true, BankDoc(A), A, SlipReceivingAccount.SiteRowMatch.NoRow, new[] { A, B }, slip);
        Assert.Null(s.BankAccountId);
        Assert.Null(s.DisplayBankAccountId);
        Assert.True(s.RequiresChoice);
        Assert.Equal(SlipReceivingAccount.SlipPayeeNotOursReason, s.SlipWarning);
        // ไม่เลือก ⇒ ปฏิเสธพร้อมเหตุ · เลือกเอง ⇒ ผ่าน (ทางไปต่อของพนักงาน)
        Assert.False(SlipReceivingAccount.Decide(true, null, null, "THB", s).Ok);
        var chosen = SlipReceivingAccount.Decide(true, B, Facts(B), "THB", s);
        Assert.True(chosen.Ok);
        Assert.Equal(B, chosen.BankAccountId);
    }

    [Fact]
    public void ชั้นสลิป_ไม่มีผลอ่าน_หรือผู้รับเป็นบิล_พร้อมเพย์_ไม่รู้บทบาท_ผลเท่าเดิมทุกประการ()
    {
        Assert.Null(SlipReceivingAccount.SlipPayeeMatch(null, Accts));
        foreach (var slip in new[] { SlipCorpusRound264.ScbBillStore, SlipCorpusRound264.ScbPromptPayMobile, SlipCorpusRound264.KplusSameBank, SlipCorpusRound264.TtbCardEnglish })
        {
            var m = PayeeOf(slip, Accts);
            foreach (var (doc, shown) in new[] { (SlipReceivingAccount.DocChannel.None, (Guid?)B), (BankDoc(A), (Guid?)null), (SlipReceivingAccount.DocChannel.None, (Guid?)null) })
                Assert.Equal(SlipReceivingAccount.Suggest(true, doc, shown, SlipReceivingAccount.SiteRowMatch.NoRow, new[] { A, B }),
                    SlipReceivingAccount.Suggest(true, doc, shown, SlipReceivingAccount.SiteRowMatch.NoRow, new[] { A, B }, m));
        }
        // ผ่านผู้ให้บริการ ⇒ ไม่ใช่เรื่องของสลิป
        var unique = PayeeOf(SlipCorpusRound264.KtbToKbank, Accts);
        Assert.Equal(SlipReceivingAccount.Origin.NotApplicable,
            SlipReceivingAccount.Suggest(false, SlipReceivingAccount.DocChannel.None, null, SlipReceivingAccount.SiteRowMatch.NoRow, new[] { A }, unique).Origin);
    }

    [Fact]
    public void ชั้นสลิป_ตรงบัญชีที่ใช้รับโอนไม่ได้_ไม่แนะนำบัญชีนั้น_และไม่แนะนำบัญชีอื่นแทน()
    {
        var slip = PayeeOf(SlipCorpusRound264.KtbToKbank, Accts);
        // ฝ่ายค้าน 264-P2 L7: A ไม่อยู่ในชุดที่ใช้ได้ (เช่นปิดใช้) ⇒ เดิมตกไป "บัญชีเดียว" B (อีกบัญชี) · ตอนนี้ต้องเลือกเอง + เหตุ
        var s = SlipReceivingAccount.Suggest(true, SlipReceivingAccount.DocChannel.None, null, SlipReceivingAccount.SiteRowMatch.NoRow, new[] { B }, slip);
        Assert.Null(s.BankAccountId);
        Assert.Null(s.DisplayBankAccountId);
        Assert.True(s.RequiresChoice);
        Assert.Equal(SlipReceivingAccount.SlipPayeeUnusableReason, s.SlipWarning);
        Assert.True(SlipReceivingAccount.Decide(true, B, Facts(B), "THB", s).Ok);   // ทางไปต่อ: เลือกเอง
        // ทิศตรงข้าม: ไม่มีผลอ่าน ⇒ บัญชีเดียวตามเดิม
        Assert.Equal(SlipReceivingAccount.Origin.SoleTransferAccount,
            SlipReceivingAccount.Suggest(true, SlipReceivingAccount.DocChannel.None, null, SlipReceivingAccount.SiteRowMatch.NoRow, new[] { B }).Origin);
    }

    [Fact]
    public void SameBank_ยุบมาใช้ตารางกลาง_ชื่อนอกตารางคงกติกาเดิม()
    {
        Assert.True(SlipReceivingAccount.SameBank("Kasikornbank", "ธนาคารกสิกรไทย (KBANK)"));   // เดิม: ไม่มีรหัสในวงเล็บฝั่งหนึ่ง + ชื่อไม่ซ้อน ⇒ false
        Assert.False(SlipReceivingAccount.SameBank("ttb", "ไทยพาณิชย์"));
        Assert.True(SlipReceivingAccount.SameBank("ธนาคารนอกตาราง จำกัด", "นอกตาราง"));          // กติกาเดิม (ชื่อซ้อนกัน)
    }

    [Fact]
    public void Migration_รอบ264_ผลอ่านสลิป()
    {
        var sql = string.Join("\n", Round264SlipReadMigration.Statements());
        Assert.Contains("\"PaymentIntents\" ADD COLUMN IF NOT EXISTS \"SlipReadJson\"", sql);
        Assert.Contains("\"LineChatSlips\" ADD COLUMN IF NOT EXISTS \"SlipReadJson\"", sql);
        Assert.Contains("\"SlipPayerBankCode\"", sql);
        Assert.Contains("\"SlipPayerAccountMask\"", sql);
    }
}
