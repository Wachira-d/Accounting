using Accounting.Helpers;
using Accounting.Models.Entities;
using Xunit;
using C = Accounting.Tests.SlipCorpusRound272;
using Outcome = Accounting.Helpers.TaxSlipMatch.Outcome;

namespace Accounting.Tests;

/// <summary>
/// รอบ 272 — สลิปชำระภาษีจากกลุ่ม LINE/อีเมล ⇒ บันทึกนำส่งในบัญชีลูกค้า · เทสต์ pure ทั้งสองทิศ (ดีไซน์ §10.2) ·
/// ข้อมูลสลิป = ชุดสังเคราะห์ <see cref="SlipCorpusRound272"/> (ไม่มีค่าจริง) + ชุดกระดาษจริง 264 (redact แล้ว) เป็นทิศตรงข้าม
/// </summary>
public class TaxSlipRound272Tests
{
    private static OcrSlipText.Reading Read(string text) => OcrSlipText.Parse(text, 2026);

    // ═══════════ P1 · OcrSlipText ช่องจ่ายบิล ═══════════

    [Fact]
    public void OcrSlipText_KbizRd_อ่านช่องจ่ายบิลจากป้าย()
    {
        var r = Read(C.KbizRd());
        Assert.NotNull(r.Bill);
        Assert.Equal("98037", r.Bill!.CompCode);
        Assert.Equal(C.ClientTaxId, r.Bill.Ref1);
        Assert.Equal(C.ControlCode, r.Bill.Ref2);
        Assert.True(r.Bill.ControlCodeLabeled);
        Assert.Equal(C.TxnNo, r.Bill.TxnNo);
        Assert.Equal(0m, r.Bill.Fee);
        Assert.Equal(12345.67m, r.Amount);
        Assert.Equal(new DateTime(2026, 11, 16), r.Date);
        Assert.Equal("004", r.Payer?.BankCode);
        // ทิศตรงข้าม: Ref เดิม ("ชุดยาวสุด") ไม่ถูกเปลี่ยนเป็นเลขผู้เสียภาษี
        Assert.NotEqual(C.ClientTaxId, r.Ref);
    }

    [Fact]
    public void OcrSlipText_ชุดกระดาษจริง264_ไม่มีCompCode_และช่องเดิมยังอ่านได้()
    {
        foreach (var s in SlipCorpusRound264.All)
        {
            var r = OcrSlipText.Parse(s.Text, 2026, s.Qr);
            Assert.Null(r.Bill?.CompCode);
            Assert.True(r.HasAnyAmount, s.Layout);
        }
    }

    [Fact]
    public void OcrSlipText_ข้อความว่าง_ไม่มีช่องจ่ายบิล()
    {
        Assert.Null(OcrSlipText.Parse(null).Bill);
        Assert.Null(OcrSlipText.Parse("โอนเงินสำเร็จ 500.00").Bill);
    }

    // ═══════════ P1 · GovPaymentSlip ═══════════

    [Fact]
    public void Gov_98037ที่KBank_และRevenueDepartment_ระบุเป็นกรมสรรพากร()
    {
        var g = GovPaymentSlip.Identify(Read(C.KbizRd()));
        Assert.Equal(GovPaymentSlip.Kind.Identified, g.Kind);
        Assert.Equal(GovPaymentSlip.Agency.RevenueDepartment, g.Row!.Agency);
        Assert.Equal(C.ClientTaxId, g.TaxId);
        Assert.Equal(C.ControlCode, g.ControlCode);
        Assert.False(g.UsedAi);
        Assert.False(GovPaymentSlip.UsedAi);
    }

    [Fact]
    public void Gov_ป้ายPersonalIncomeTax_ไม่จำกัดแบบภาษี()
    {
        var g = GovPaymentSlip.Identify(Read(C.KbizRd()));
        Assert.True(GovPaymentSlip.Covers(g.Row!, "WhtPnd53"));
        Assert.True(GovPaymentSlip.Covers(g.Row!, "VatPp30"));
    }

    [Fact]
    public void Gov_98037จากธนาคารอื่น_ไม่รู้จัก_ไม่ใช่กรมสรรพากร()
    {
        var g = GovPaymentSlip.Identify(Read(C.KbizRd(bankLine: "ไทยพาณิชย์").Replace("K BIZ", "")));
        Assert.Equal(GovPaymentSlip.Kind.UnknownGovernment, g.Kind);
        Assert.Null(g.Row);
    }

    [Fact]
    public void Gov_คำสรรพากรแต่ไม่มีCompCode_ไม่รู้จัก_ไม่ใช่NotGovernment()
    {
        var g = GovPaymentSlip.Identify(Read(C.ThaiRdNoCompCode));
        Assert.Equal(GovPaymentSlip.Kind.UnknownGovernment, g.Kind);
        Assert.True(g.IsGovernment);
    }

    [Fact]
    public void Gov_CompCodeที่ไม่อยู่ในตาราง_ไม่รู้จัก()
        => Assert.Equal(GovPaymentSlip.Kind.UnknownGovernment, GovPaymentSlip.Identify(Read(C.KbizRd(compCode: "12345"))).Kind);

    [Fact]
    public void Gov_เลขภาษีchecksumผิด_และControlCodeสั้น_ไม่ผ่าน()
    {
        Assert.Equal(GovPaymentSlip.Kind.UnknownGovernment, GovPaymentSlip.Identify(Read(C.KbizRd(taxId: "0009990000010"))).Kind);
        Assert.Equal(GovPaymentSlip.Kind.UnknownGovernment, GovPaymentSlip.Identify(Read(C.KbizRd(control: "13579024681357"))).Kind);
    }

    [Fact]
    public void Gov_สลิปสปส_ไม่รู้จัก_และสลิปโอนทั่วไป_ไม่ใช่สลิปรัฐ()
    {
        Assert.Equal(GovPaymentSlip.Kind.UnknownGovernment, GovPaymentSlip.Identify(OcrSlipText.Parse(SlipCorpusRound264.KbizBillSso.Text, 2026)).Kind);
        foreach (var s in SlipCorpusRound264.All.Where(x => x != SlipCorpusRound264.KbizBillSso))
            Assert.Equal(GovPaymentSlip.Kind.NotGovernment, GovPaymentSlip.Identify(OcrSlipText.Parse(s.Text, 2026, s.Qr)).Kind);
    }

    [Fact]
    public void Gov_ตาราง_แถวยืนยันแล้วมีแถวเดียว_แถวอื่นไม่บันทึกเอง()
    {
        var auto = GovPaymentSlip.Billers.Where(b => b.Auto).ToList();
        Assert.Single(auto);
        Assert.Equal(("004", "98037"), (auto[0].BankCode, auto[0].CompCode));
        Assert.NotNull(auto[0].ConfirmedOn);
        Assert.All(GovPaymentSlip.Billers.Where(b => !b.Auto), b => Assert.Null(b.ConfirmedOn));
    }

    // ═══════════ P4 · TaxSlipMatch ═══════════

    private static readonly Guid A = Guid.NewGuid(), B = Guid.NewGuid();
    private static readonly DateTime SlipDay = new(2026, 11, 14);

    private static TaxSlipMatch.Slip Slip(decimal amount, string? cc = C.ControlCode, decimal? fee = 0m, DateTime? date = null, string taxId = C.ClientTaxId)
    {
        var g = GovPaymentSlip.Identify(Read(C.KbizRd(taxId: taxId)));
        return new TaxSlipMatch.Slip(g, amount, fee, date ?? SlipDay, TaxSlipRefHash.ControlCode(cc));
    }

    private static readonly IReadOnlyList<TaxSlipMatch.CompanyKey> Group = new[] { new TaxSlipMatch.CompanyKey(A, C.ClientTaxId) };
    private static TaxSlipMatch.RequestKey Req(decimal amount = 12000m, decimal surcharge = 345.67m, string status = "Open", bool books = false, DateTime? exp = null, bool amend = false, string type = "VatPp30")
        => new(Guid.NewGuid(), A, type, 2026, 10, amount, surcharge, 0m, TaxSlipRefHash.ControlCode(C.ControlCode)!, status, books, exp, amend);
    private static TaxSlipMatch.PendingKey Pend(string type, decimal amount, int month = 10)
        => new(A, type, 2026, month, amount, new DateTime(2026, month, 1).AddMonths(1), new DateTime(2026, month, 1).AddMonths(1).AddDays(22));
    private static readonly HashSet<(Guid, string, int, int)> NoRemit = new();

    [Fact]
    public void Match_ControlCodeตรง_ยอดตรงทุกสตางค์_One()
    {
        var d = TaxSlipMatch.Decide(Slip(12345.67m), Group, new[] { Req() }, new[] { Pend("VatPp30", 12000m) }, NoRemit, false);
        Assert.Equal(Outcome.One, d.Outcome);
        Assert.True(d.Auto);
        Assert.Equal(TaxSlipMatch.Codes.One, d.RuleCode);
        Assert.Equal(345.67m, d.Chosen!.Surcharge);
        Assert.False(d.UsedAi);
    }

    [Fact]
    public void Match_ยอดต่าง001_AmountMismatch()
        => Assert.Equal(Outcome.AmountMismatch, TaxSlipMatch.Decide(Slip(12345.66m), Group, new[] { Req() }, new[] { Pend("VatPp30", 12000m) }, NoRemit, false).Outcome);

    [Fact]
    public void Match_เลขภาษีบริษัทอื่นนอกกลุ่ม_TaxIdNotInGroup_ไม่ค้นทั้งฐาน()
    {
        var d = TaxSlipMatch.Decide(Slip(12345.67m, taxId: C.OtherClientTaxId), Group, new[] { Req() }, new[] { Pend("VatPp30", 12000m) }, NoRemit, false);
        Assert.Equal(Outcome.TaxIdNotInGroup, d.Outcome);
        Assert.Null(d.CompanyId);
    }

    [Fact]
    public void Match_ไม่มีบริษัทผูก_NoTenant()
        => Assert.Equal(Outcome.NoTenant, TaxSlipMatch.Decide(Slip(1m), Array.Empty<TaxSlipMatch.CompanyKey>(), Array.Empty<TaxSlipMatch.RequestKey>(),
            Array.Empty<TaxSlipMatch.PendingKey>(), NoRemit, false).Outcome);

    [Fact]
    public void Match_ใบขอชำระBooksMismatch_หมดอายุ_ยื่นเพิ่มเติม_ไม่One()
    {
        var p = new[] { Pend("VatPp30", 12000m) };
        Assert.Equal(Outcome.BooksMismatch, TaxSlipMatch.Decide(Slip(12345.67m), Group, new[] { Req(books: true) }, p, NoRemit, false).Outcome);
        Assert.Equal(Outcome.Expired, TaxSlipMatch.Decide(Slip(12345.67m), Group, new[] { Req(exp: SlipDay.AddDays(-1)) }, p, NoRemit, false).Outcome);
        Assert.Equal(Outcome.Amendment, TaxSlipMatch.Decide(Slip(12345.67m), Group, new[] { Req(amend: true) }, p, NoRemit, false).Outcome);
        Assert.Equal(Outcome.Duplicate, TaxSlipMatch.Decide(Slip(12345.67m), Group, new[] { Req(status: "Paid") }, p, NoRemit, false).Outcome);
        Assert.Equal(Outcome.RequestClosed, TaxSlipMatch.Decide(Slip(12345.67m), Group, new[] { Req(status: "Cancelled") }, p, NoRemit, false).Outcome);
    }

    [Fact]
    public void Match_สมุดเปลี่ยนหลังส่ง_หรือนำส่งแล้ว_ไม่One()
    {
        Assert.Equal(Outcome.BooksChanged, TaxSlipMatch.Decide(Slip(12345.67m), Group, new[] { Req() }, new[] { Pend("VatPp30", 11000m) }, NoRemit, false).Outcome);
        var remitted = new HashSet<(Guid, string, int, int)> { (A, "VatPp30", 2026, 10) };
        Assert.Equal(Outcome.AlreadyRemitted, TaxSlipMatch.Decide(Slip(12345.67m), Group, new[] { Req() }, new[] { Pend("VatPp30", 12000m) }, remitted, false).Outcome);
    }

    [Fact]
    public void Match_ค่าธรรมเนียมไม่ศูนย์_และControlCodeบันทึกแล้ว_ไม่One()
    {
        Assert.Equal(Outcome.FeeNotZero, TaxSlipMatch.Decide(Slip(12345.67m, fee: 10m), Group, new[] { Req() }, new[] { Pend("VatPp30", 12000m) }, NoRemit, false).Outcome);
        Assert.Equal(Outcome.Duplicate, TaxSlipMatch.Decide(Slip(12345.67m), Group, new[] { Req() }, new[] { Pend("VatPp30", 12000m) }, NoRemit, true).Outcome);
    }

    [Fact]
    public void Match_ไม่มีใบขอชำระ_เหลือรายการเดียวในช่วงยื่น_One_บันทึกเอง_D3()
    {
        var d = TaxSlipMatch.Decide(Slip(5000m, cc: null), Group, Array.Empty<TaxSlipMatch.RequestKey>(), new[] { Pend("WhtPnd53", 5000m), Pend("VatPp30", 7000m) }, NoRemit, false);
        Assert.Equal(Outcome.One, d.Outcome);
        Assert.True(d.Auto);
        Assert.Equal(TaxSlipMatch.Codes.OneNoControlCode, d.RuleCode);
        Assert.Null(d.Chosen!.RequestId);
    }

    [Fact]
    public void Match_ภงด3กับภงด53ยอดเท่ากัน_Many_ถามในกลุ่ม()
    {
        var d = TaxSlipMatch.Decide(Slip(5000m, cc: null), Group, Array.Empty<TaxSlipMatch.RequestKey>(), new[] { Pend("WhtPnd3", 5000m), Pend("WhtPnd53", 5000m) }, NoRemit, false);
        Assert.Equal(Outcome.Many, d.Outcome);
        Assert.False(d.Auto);
        Assert.Equal(2, d.Candidates.Count);
    }

    [Fact]
    public void Match_ไม่มีใบขอชำระ_นอกช่วงยื่น_หรือชำระหลังกำหนดยอดเกิน_ไม่บันทึกเอง()
    {
        var p = new[] { Pend("WhtPnd53", 5000m) };
        Assert.Equal(Outcome.OutOfWindow, TaxSlipMatch.Decide(Slip(5000m, cc: null, date: new DateTime(2026, 10, 20)), Group, Array.Empty<TaxSlipMatch.RequestKey>(), p, NoRemit, false).Outcome);
        Assert.Equal(Outcome.OutOfWindow, TaxSlipMatch.Decide(Slip(5000m, cc: null, date: new DateTime(2026, 12, 30)), Group, Array.Empty<TaxSlipMatch.RequestKey>(), p, NoRemit, false).Outcome);
        Assert.Equal(Outcome.LateWithoutRequest, TaxSlipMatch.Decide(Slip(5075m, cc: null, date: new DateTime(2026, 12, 30)), Group, Array.Empty<TaxSlipMatch.RequestKey>(), p, NoRemit, false).Outcome);
        Assert.Equal(Outcome.None, TaxSlipMatch.Decide(Slip(4999m, cc: null), Group, Array.Empty<TaxSlipMatch.RequestKey>(), p, NoRemit, false).Outcome);
    }

    [Fact]
    public void Match_ยอดเดียวหลายงวด_กรองช่วงยื่นก่อน_เหลือตัวเดียว_One_M1()
    {
        // ภ.ง.ด.1 ยอดคงที่ทุกเดือน: ก.ย. เลยกำหนดแล้ว · ต.ค. อยู่ในช่วงยื่น ⇒ ต.ค. ตัวเดียว (เดิม Many แล้วปุ่มเลือก ก.ย. ได้)
        var d = TaxSlipMatch.Decide(Slip(4250m, cc: null, date: new DateTime(2026, 11, 7)), Group, Array.Empty<TaxSlipMatch.RequestKey>(),
            new[] { Pend("WhtPnd1", 4250m, 9), Pend("WhtPnd1", 4250m, 10) }, NoRemit, false);
        Assert.Equal(Outcome.One, d.Outcome);
        Assert.Equal(10, d.Chosen!.Month);
        // ค่าธรรมเนียมไม่ศูนย์ ⇒ ไม่ถามในกลุ่ม (ด่านเดียวกับทางตัวเดียว)
        var fee = TaxSlipMatch.Decide(Slip(5000m, cc: null, fee: 15m), Group, Array.Empty<TaxSlipMatch.RequestKey>(), new[] { Pend("WhtPnd3", 5000m), Pend("WhtPnd53", 5000m) }, NoRemit, false);
        Assert.Equal(Outcome.FeeNotZero, fee.Outcome);
    }

    [Fact]
    public void Match_มีใบขอชำระOpenงวดเดียวกันแต่CCไม่ตรง_ไม่บันทึกเอง_M2()
    {
        var d = TaxSlipMatch.Decide(Slip(5000m, cc: "999999999999999"), Group, new[] { Req(amount: 5000m, surcharge: 0m, type: "WhtPnd53") },
            new[] { Pend("WhtPnd53", 5000m) }, NoRemit, false);
        Assert.Equal(Outcome.RequestCcMismatch, d.Outcome);
        Assert.False(d.Auto);
        // ทิศตรงข้าม: ใบขอชำระของงวดนั้นถูกยกเลิก ⇒ ทาง D-3 ใช้ได้
        Assert.Equal(Outcome.One, TaxSlipMatch.Decide(Slip(5000m, cc: "999999999999999"), Group, new[] { Req(amount: 5000m, surcharge: 0m, type: "WhtPnd53", status: "Cancelled") },
            new[] { Pend("WhtPnd53", 5000m) }, NoRemit, false).Outcome);
    }

    [Fact]
    public void Match_สลิปไม่รู้จักหน่วยงาน_UnknownAgency()
    {
        var g = GovPaymentSlip.Identify(Read(C.ThaiRdNoCompCode));
        var d = TaxSlipMatch.Decide(new TaxSlipMatch.Slip(g, 5000m, null, SlipDay, null), Group, Array.Empty<TaxSlipMatch.RequestKey>(), new[] { Pend("WhtPnd53", 5000m) }, NoRemit, false);
        Assert.Equal(Outcome.UnknownAgency, d.Outcome);
        Assert.False(d.Auto);
    }

    [Fact]
    public void Match_สปสไม่อยู่ในแบบของกรมสรรพากร_ไม่เป็นผู้สมัคร()
        => Assert.Equal(Outcome.None, TaxSlipMatch.Decide(Slip(5000m, cc: null), Group, Array.Empty<TaxSlipMatch.RequestKey>(), new[] { Pend("SsoSps110", 5000m) }, NoRemit, false).Outcome);

    [Fact]
    public void RequestProblem_สองทิศ()
    {
        Assert.Null(TaxSlipMatch.RequestProblem("VatPp30", 12000m, 0m, 0m, C.ControlCode, 12000m, false, false).Problem);
        var mismatch = TaxSlipMatch.RequestProblem("VatPp30", 12000m, 0m, 0m, C.ControlCode, 11000m, false, false);
        Assert.NotNull(mismatch.Problem);
        var anyway = TaxSlipMatch.RequestProblem("VatPp30", 12000m, 0m, 0m, C.ControlCode, 11000m, true, false);
        Assert.Null(anyway.Problem);
        Assert.True(anyway.BooksMismatch);
        Assert.NotNull(TaxSlipMatch.RequestProblem("VatPp30", 12000m, 0m, 0m, "12345", 12000m, false, false).Problem);
        Assert.NotNull(TaxSlipMatch.RequestProblem("SsoSps110", 12000m, 0m, 0m, C.ControlCode, 12000m, false, false).Problem);
        Assert.NotNull(TaxSlipMatch.RequestProblem("VatPp30", 0m, 0m, 0m, C.ControlCode, 0m, false, false).Problem);
    }

    // ═══════════ P4 · TaxSlipBankAccount ═══════════

    private static readonly Guid Acc1 = Guid.NewGuid(), Acc2 = Guid.NewGuid();
    private static readonly IReadOnlyList<SlipAccountMatch.AccountKey> Accounts = new[]
    {
        new SlipAccountMatch.AccountKey(Acc1, "กสิกรไทย", "123-4-51234-0"), new SlipAccountMatch.AccountKey(Acc2, "กสิกรไทย", "123-4-59999-0"),
    };

    [Fact]
    public void Bank_เลขบนสลิปตรงบัญชีเดียว_บันทึกเองได้()
    {
        var r = Read(C.KbizRd());
        var b = TaxSlipBankAccount.Decide(SlipAccountMatch.Match(r.Payer, Accounts), r.Payer, null, null);
        Assert.Equal(TaxSlipBankAccount.Kind.FromSlip, b.Kind);
        Assert.Equal(Acc1, b.AccountId);
        Assert.True(b.CanAuto);
    }

    [Fact]
    public void Bank_บัญชีในความยินยอมและธนาคารตรง_ได้_ธนาคารต่าง_ไม่ได้()
    {
        var unreadable = new OcrSlipText.SlipParty(OcrSlipText.PartyKind.BankAccount, "004", false, null, null, null);
        var m = SlipAccountMatch.Match(unreadable, Accounts);
        Assert.Equal(TaxSlipBankAccount.Kind.FromConsent, TaxSlipBankAccount.Decide(m, unreadable, Acc2, "004").Kind);
        Assert.Equal(TaxSlipBankAccount.Kind.NeedsAccount, TaxSlipBankAccount.Decide(m, unreadable, Acc2, "014").Kind);
    }

    [Fact]
    public void Bank_บัตร_NeedsAccount_และเลขไม่ใช่ของบริษัท_NotOurs()
    {
        var card = new OcrSlipText.SlipParty(OcrSlipText.PartyKind.Card, "004", false, null, null, null);
        var b = TaxSlipBankAccount.Decide(SlipAccountMatch.Match(card, Accounts), card, Acc1, "004");
        Assert.Equal(TaxSlipBankAccount.Kind.NeedsAccount, b.Kind);
        Assert.False(b.CanAuto);
        var r = Read(C.KbizRd());
        var other = new[] { new SlipAccountMatch.AccountKey(Acc1, "กสิกรไทย", "123-4-58888-0") };
        Assert.Equal(TaxSlipBankAccount.Kind.NotOurs, TaxSlipBankAccount.Decide(SlipAccountMatch.Match(r.Payer, other), r.Payer, null, null).Kind);
    }

    [Fact]
    public void Bank_อ่านเลขไม่ได้และมีหลายบัญชีธนาคารเดียวกัน_ไม่ใช้บัญชีในความยินยอม_L7()
    {
        var unreadable = new OcrSlipText.SlipParty(OcrSlipText.PartyKind.BankAccount, "004", false, null, null, null);
        var m = SlipAccountMatch.Match(unreadable, Accounts);
        Assert.Equal(TaxSlipBankAccount.Kind.NeedsAccount, TaxSlipBankAccount.Decide(m, unreadable, Acc2, "004", sameBankAccounts: 2).Kind);
        Assert.Equal(TaxSlipBankAccount.Kind.FromConsent, TaxSlipBankAccount.Decide(m, unreadable, Acc2, "004", sameBankAccounts: 1).Kind);
    }

    [Fact]
    public void Bank_เส้นคู่ค้า_SlipUnique_อ่านนโยบายจากตัวตั้งเดียวของรอบ282()
        => Assert.Equal(SlipMoneySource.AllowsAuto(SlipMoneySource.Evidence.SlipUnique),
            PartnerPaySourceAccount.Precedence.Single(p => p.Origin == PartnerPaySourceAccount.Origin.SlipUnique).AllowsAuto);

    // ═══════════ P2 · ProviderConsent ═══════════

    private static readonly Guid Client = Guid.NewGuid(), Provider = Guid.NewGuid(), Grantor = Guid.NewGuid();

    private static readonly Guid PoaFile = Guid.NewGuid(), SecondStaff = Guid.NewGuid();

    private static ProviderAutomationConsent Row(string types = "VatPp30,WhtPnd53", string source = ProviderConsent.SourceWeb, decimal? cap = null, Guid? approvedBy = null)
    {
        var at = PartnerConsent.TruncateToSecond(DateTime.UtcNow);
        var poa = source == ProviderConsent.SourcePowerOfAttorney;
        var row = new ProviderAutomationConsent
        {
            CompanyId = Client, ProviderCompanyId = Provider, Scope = ProviderConsent.Scope, TaxTypes = types, MaxAmount = cap, TextVersion = 1,
            HashVersion = poa ? ProviderConsent.HashVersionWithEvidence : ProviderConsent.HashVersion,
            EvidenceSource = source, GrantedByUserId = Grantor, GrantedAt = at, EvidenceAttachmentId = poa ? PoaFile : null,
            ApprovedByUserId = approvedBy, ApprovedAt = approvedBy == null ? null : at,
        };
        row.EvidenceHash = poa
            ? ProviderConsent.HashV2(Client, Provider, types, cap, null, false, 1, ProviderConsent.TextFor(1)!, Grantor, at, source, PoaFile)
            : ProviderConsent.Hash(Client, Provider, types, cap, null, false, 1, ProviderConsent.TextFor(1)!, Grantor, at);
        return row;
    }

    [Fact]
    public void Consent_ผู้ให้_เจ้าของได้_TaxFileและSettingsได้()
    {
        Assert.Null(ProviderConsent.GrantProblem(true, true, _ => false, false));
        Assert.Null(ProviderConsent.GrantProblem(true, false, _ => true, false));
    }

    [Fact]
    public void Consent_ผู้ให้_สิทธิ์ไม่ครบ_พนักงานผู้ให้บริการ_ผู้ใช้ไม่ใช้งาน_ให้ไม่ได้()
    {
        Assert.NotNull(ProviderConsent.GrantProblem(true, false, n => n == PartnerConsent.Need.TaxFile, false));
        Assert.NotNull(ProviderConsent.GrantProblem(true, true, _ => true, isProviderMember: true));
        Assert.NotNull(ProviderConsent.GrantProblem(false, true, _ => true, false));
    }

    [Fact]
    public void Consent_hash_เขียนแล้วตรวจผ่าน_แก้แถวแล้วไม่ผ่าน()
    {
        var row = Row();
        Assert.True(ProviderConsent.HashMatches(row));
        Assert.True(ProviderConsent.Allows(row, Client, Provider, "VatPp30", 100m, true).Ok);
        row.TaxTypes = "VatPp30,WhtPnd53,WhtPnd3";
        Assert.False(ProviderConsent.HashMatches(row));
        Assert.Equal(ProviderConsent.Codes.Tampered, ProviderConsent.Allows(row, Client, Provider, "VatPp30", 100m, true).Code);
    }

    [Fact]
    public void Consent_ถอน_แบบไม่ติ๊ก_เกินเพดาน_ผู้ให้หมดสิทธิ์_ไม่มีแถว_ไม่อนุญาต()
    {
        Assert.Equal(ProviderConsent.Codes.NoConsent, ProviderConsent.Allows(null, Client, Provider, null, null, true).Code);
        var w = Row(); w.WithdrawnAt = DateTime.UtcNow;
        Assert.Equal(ProviderConsent.Codes.Withdrawn, ProviderConsent.Allows(w, Client, Provider, null, null, true).Code);
        Assert.Equal(ProviderConsent.Codes.TaxTypeNotCovered, ProviderConsent.Allows(Row(), Client, Provider, "WhtPnd3", 1m, true).Code);
        Assert.Equal(ProviderConsent.Codes.OverCap, ProviderConsent.Allows(Row(cap: 1000m), Client, Provider, "VatPp30", 1000.01m, true).Code);
        Assert.Equal(ProviderConsent.Codes.GrantorLostPermission, ProviderConsent.Allows(Row(), Client, Provider, "VatPp30", 1m, false).Code);
        Assert.Equal(ProviderConsent.Codes.ProviderMismatch, ProviderConsent.Allows(Row(), Client, Guid.NewGuid(), "VatPp30", 1m, true).Code);
    }

    [Fact]
    public void Consent_หนังสือมอบอำนาจ_ไม่เปิดบันทึกเอง_แต่พนักงานกดได้()
    {
        var v = ProviderConsent.Allows(Row(source: ProviderConsent.SourcePowerOfAttorney, approvedBy: SecondStaff), Client, Provider, "VatPp30", 1m, true);
        Assert.False(v.Ok);
        Assert.Equal(ProviderConsent.Codes.PowerOfAttorneyOnly, v.Code);
        Assert.True(v.AllowsStaffClick);
        Assert.False(ProviderConsent.Allows(null, Client, Provider, "VatPp30", 1m, true).AllowsStaffClick);
    }

    [Fact]
    public void Consent_หนังสือมอบอำนาจ_ยังไม่อนุมัติหรือผู้แนบอนุมัติเอง_กดไม่ได้_สลับไฟล์แล้วhashไม่ตรง_M4()
    {
        var pending = ProviderConsent.Allows(Row(source: ProviderConsent.SourcePowerOfAttorney), Client, Provider, "VatPp30", 1m, true);
        Assert.Equal(ProviderConsent.Codes.PowerOfAttorneyPendingApproval, pending.Code);
        Assert.False(pending.AllowsStaffClick);
        var self = ProviderConsent.Allows(Row(source: ProviderConsent.SourcePowerOfAttorney, approvedBy: Grantor), Client, Provider, "VatPp30", 1m, true);
        Assert.False(self.AllowsStaffClick);
        var swapped = Row(source: ProviderConsent.SourcePowerOfAttorney, approvedBy: SecondStaff);
        swapped.EvidenceAttachmentId = Guid.NewGuid();
        Assert.Equal(ProviderConsent.Codes.Tampered, ProviderConsent.Allows(swapped, Client, Provider, "VatPp30", 1m, true).Code);
        // ทิศตรงข้าม: แถวเว็บรุ่น 1 ยังตรวจผ่าน · แถวหนังสือมอบอำนาจที่ใช้ hash รุ่น 1 (ไม่มีไฟล์ใน hash) ไม่ผ่าน
        Assert.True(ProviderConsent.HashMatches(Row()));
        var v1Poa = Row(source: ProviderConsent.SourcePowerOfAttorney, approvedBy: SecondStaff);
        v1Poa.HashVersion = ProviderConsent.HashVersion;
        Assert.False(ProviderConsent.HashMatches(v1Poa));
    }

    [Fact]
    public void Consent_ชุดแบบ_เรียง_ปฏิเสธสปสและชื่อที่ไม่รู้จัก()
    {
        Assert.Equal(("VatPp30,WhtPnd53", (string?)null), ProviderConsent.NormalizeTypes(new[] { "WhtPnd53", "VatPp30", "VatPp30" }));
        Assert.NotNull(ProviderConsent.NormalizeTypes(new[] { "SsoSps110" }).Problem);
        Assert.NotNull(ProviderConsent.NormalizeTypes(new[] { "Nope" }).Problem);
        Assert.NotNull(ProviderConsent.NormalizeTypes(Array.Empty<string>()).Problem);
    }

    [Fact]
    public void Consent_ขั้นยืนยัน_ต้องพิมพ์ยินยอม_และhashตรงที่แสดง()
    {
        var h = ProviderConsent.PreviewHash(Client, Provider, "VatPp30", null, null, true, Grantor);
        Assert.Null(ProviderConsent.ConfirmProblem(1, h, h, "ยินยอม"));
        Assert.NotNull(ProviderConsent.ConfirmProblem(1, h, h, "ok"));
        Assert.NotNull(ProviderConsent.ConfirmProblem(1, h, ProviderConsent.PreviewHash(Client, Provider, "VatPp30,WhtPnd3", null, null, true, Grantor), "ยินยอม"));
        Assert.NotNull(ProviderConsent.ConfirmProblem(0, h, h, "ยินยอม"));
    }

    // ═══════════ การ์ดตอบกลับ · ปิดบัง ═══════════

    private static TaxSlipReplyCard.Facts Full() => new(CompanyName: "บริษัท ตัวอย่างสังเคราะห์ จำกัด", FormLabel: "ภ.พ.30 งวด ต.ค. 2569", Amount: 12345.67m, Tax: 12000m,
        Surcharge: 345.67m, AccountLabel: "กสิกรไทย ••1234", PayDate: SlipDay, ControlLast4: "3579", TaxIdOnSlip: C.ClientTaxId, Reason: "เหตุทดสอบ",
        RecordedAt: SlipDay, HeldCount: 2, Choices: new[] { "ภ.ง.ด.3 งวด ต.ค. 2569", "ภ.ง.ด.53 งวด ต.ค. 2569" }, ExpectedAmount: 12000m, DueDate: SlipDay);

    [Fact]
    public void Card_ทุกผลมีข้อความ_และไม่มีเลขภาษีเต็ม_Controlเต็ม_ชื่อบริษัทเต็ม()
    {
        foreach (var k in Enum.GetValues<TaxSlipReplyCard.Kind>())
            foreach (var show in new[] { true, false })
            {
                var t = TaxSlipReplyCard.Text(k, Full(), show);
                Assert.False(string.IsNullOrWhiteSpace(t), k.ToString());
                Assert.DoesNotContain(C.ClientTaxId, t);
                Assert.DoesNotContain(C.ControlCode, t);
                Assert.DoesNotContain("ตัวอย่างสังเคราะห์", t);
                Assert.DoesNotContain("<PAYER_NAME>", t);
            }
    }

    [Fact]
    public void Card_ShowAmountsปิด_ไม่มีตัวเลขเงิน_เปิด_มี()
    {
        foreach (var k in Enum.GetValues<TaxSlipReplyCard.Kind>())
            Assert.DoesNotContain("12,345.67", TaxSlipReplyCard.Text(k, Full(), false));
        Assert.Contains("12,345.67", TaxSlipReplyCard.Text(TaxSlipReplyCard.Kind.Remitted, Full(), true));
        Assert.Contains("ยังไม่ได้บันทึกเลขรับ", TaxSlipReplyCard.Text(TaxSlipReplyCard.Kind.Remitted, Full(), true));
    }

    [Fact]
    public void Card_ShowAmountsปิด_เหตุมีตัวเลข_ไม่มีตัวเลขในการ์ด_M3()
    {
        var f = Full() with { Reason = "ยอด 85,000.00 บาท เกินเพดานที่อนุญาต 50,000.00 บาท · เอกสาร 3 ใบ" };
        var hidden = TaxSlipReplyCard.Text(TaxSlipReplyCard.Kind.ToStaff, f, false);
        Assert.DoesNotContain("85,000.00", hidden);
        Assert.DoesNotContain("50,000.00", hidden);
        Assert.Contains("เกินเพดาน", hidden);
        Assert.Contains("85,000.00", TaxSlipReplyCard.Text(TaxSlipReplyCard.Kind.ToStaff, f, true));
        Assert.Contains("ยอด ••• บาท", TaxSlipReplyCard.Text(TaxSlipReplyCard.Kind.ToStaff, Full() with { Reason = "ยอด 1,234.50 บาท" }, false));
    }

    [Fact]
    public void Card_ผลที่ยังไม่บันทึก_ไม่อ้างว่าบันทึกแล้ว()
    {
        foreach (var k in new[] { TaxSlipReplyCard.Kind.NeedsAccount, TaxSlipReplyCard.Kind.NoMatch, TaxSlipReplyCard.Kind.AwaitingConsent, TaxSlipReplyCard.Kind.ToStaff,
                     TaxSlipReplyCard.Kind.TaxIdMismatch, TaxSlipReplyCard.Kind.AmountMismatch })
            Assert.Contains("ยังไม่ได้บันทึก", TaxSlipReplyCard.Text(k, Full(), true));
        Assert.Equal("0009990XXXXXX", TaxSlipReplyCard.MaskTaxId(C.ClientTaxId));
        Assert.Equal("3-XXXX-XXXXX-XX-9", TaxSlipReplyCard.MaskTaxId("3000999000009"));
        Assert.DoesNotContain(TaxSlipReplyCard.Clean("a\u0007b\nc"), ch => ch == (char)7);
        Assert.Contains("\n", TaxSlipReplyCard.Clean("a\nb"));
    }

    // ═══════════ แฮช/ลายเซ็น · ป้ายหน้าเว็บ · อีเมลขาเข้า · เส้น webhook ═══════════

    [Fact]
    public void RefHash_ค่าเดียวกันได้แฮชเดียว_ต่างวัตถุประสงค์ต่างกัน_ปุ่มปลอมไม่ผ่าน()
    {
        Assert.Equal(TaxSlipRefHash.ControlCode("1357-9024 6813579"), TaxSlipRefHash.ControlCode(C.ControlCode));
        Assert.NotEqual(TaxSlipRefHash.ControlCode(C.ControlCode), TaxSlipRefHash.BankTxn("004", C.ControlCode));
        Assert.NotEqual(TaxSlipRefHash.BankTxn("004", C.TxnNo), TaxSlipRefHash.BankTxn("014", C.TxnNo));
        Assert.Null(TaxSlipRefHash.ControlCode(" "));
        Assert.Equal("3579", TaxSlipRefHash.Last4(C.ControlCode));
        var id = Guid.NewGuid();
        var pb = TaxSlipRefHash.TryParse(TaxSlipRefHash.PostbackData(id, 1, "key-a"))!;
        Assert.True(TaxSlipRefHash.Verify(pb, "key-a"));
        Assert.False(TaxSlipRefHash.Verify(pb, "key-b"));
        Assert.False(TaxSlipRefHash.Verify(pb with { Index = 0 }, "key-a"));
        Assert.Null(TaxSlipRefHash.TryParse("tsc|bad"));
        Assert.Null(TaxSlipRefHash.TryParse("lcs|x|y|z"));
    }

    [Fact]
    public void RemitView_ป้ายสลิป_เลขรับ_ตรวจทาน_เฉพาะแถวจากสลิป()
    {
        Assert.Equal("ยืนยันจากสลิป · NextAcc · Control ••3579", TaxSlipRemitView.SourceLabel("Slip", "3579"));
        Assert.Null(TaxSlipRemitView.SourceLabel(null, "3579"));
        Assert.Equal(TaxSlipRemitView.NoFilingNumber, TaxSlipRemitView.FilingNote("Slip", null));
        Assert.Null(TaxSlipRemitView.FilingNote("Slip", "RD-1"));
        Assert.Null(TaxSlipRemitView.FilingNote(null, null));
        Assert.Null(TaxSlipRemitView.ReviewLabel("Manual", null));
        Assert.Contains("✓", TaxSlipRemitView.ReviewLabel("Slip", TaxSlipRemitView.Review.StatementFound));
    }

    [Fact]
    public void Review_ผูกแล้วหรือเจอบัญชีเดียวกัน_พบ_บัญชีอื่น_เตือน_ไม่พบหลัง10วัน_เตือน()
    {
        var pay = new DateTime(2026, 11, 1);
        Assert.Equal(TaxSlipRemitView.Review.StatementFound, Accounting.Services.Implementations.TaxSlipReviewService.Verdict(true, false, false, pay, pay));
        Assert.Equal(TaxSlipRemitView.Review.OtherAccount, Accounting.Services.Implementations.TaxSlipReviewService.Verdict(false, false, true, pay, pay));
        Assert.Null(Accounting.Services.Implementations.TaxSlipReviewService.Verdict(false, false, false, pay, pay.AddDays(5)));
        Assert.Equal(TaxSlipRemitView.Review.NotFound, Accounting.Services.Implementations.TaxSlipReviewService.Verdict(false, false, false, pay, pay.AddDays(11)));
    }

    [Fact]
    public void InboundEmail_รหัสลับ_production_failfast_ไม่เปิดใช้_null()
    {
        Assert.Throws<InvalidOperationException>(() => InboundEmailRule.RequireSecret(true, true, null));
        Assert.Throws<InvalidOperationException>(() => InboundEmailRule.RequireSecret(true, true, "change-me"));
        Assert.Null(InboundEmailRule.RequireSecret(false, true, null));
        Assert.Equal(new string('k', 32), InboundEmailRule.RequireSecret(true, true, new string('k', 32)));
        Assert.Null(InboundEmailRule.RequireSecret(true, false, null));
    }

    [Fact]
    public void InboundEmail_DKIMผ่านแต่โดเมนไม่ตรงFrom_ไม่ยืนยันผู้ส่ง_H3()
    {
        var a = new JsonInboundEmailAdapter();
        var spoof = a.Parse("""{"from":"owner@client.example","messageId":"<m1>","dkim":"pass","dkimDomain":"attacker.example","spf":"pass","spfDomain":"attacker.example"}""")!;
        Assert.Null(InboundEmailRule.Problem(spoof));              // รับเข้าคิวได้
        Assert.False(InboundEmailRule.SenderVerified(spoof));      // แต่ไม่บันทึกเอง
        var noInfo = a.Parse("""{"from":"owner@client.example","messageId":"<m2>","dkim":"pass"}""")!;
        Assert.False(InboundEmailRule.SenderVerified(noInfo));
        // ทิศตรงข้าม: DKIM d= ตรง/เป็นโดเมนแม่ · SPF โดเมนตรง · DMARC ผ่าน ⇒ ยืนยันได้
        Assert.True(InboundEmailRule.SenderVerified(spoof with { DkimDomain = "client.example" }));
        Assert.True(InboundEmailRule.SenderVerified(spoof with { From = "owner@mail.client.example", DkimDomain = "client.example" }));
        Assert.True(InboundEmailRule.SenderVerified(spoof with { DkimPass = false, SpfDomain = "client.example" }));
        Assert.True(InboundEmailRule.SenderVerified(noInfo with { DmarcPass = true }));
        Assert.False(InboundEmailRule.SenderVerified(spoof with { DkimDomain = "notclient.example" }));
    }

    [Fact]
    public void InboundEmail_SPFและDKIMไม่ผ่าน_ไม่รับ_adapterอ่านไฟล์แนบ()
    {
        var a = new JsonInboundEmailAdapter();
        var m = a.Parse("""{"from":"a@example.test","messageId":"<m1>","spf":"pass","dkim":"fail","attachments":[{"fileName":"s.png","contentBase64":"AQID"}]}""")!;
        Assert.Null(InboundEmailRule.Problem(m));
        Assert.Equal(new byte[] { 1, 2, 3 }, m.Attachments[0].Bytes);
        Assert.NotNull(InboundEmailRule.Problem(m with { SpfPass = false }));
        Assert.Null(a.Parse("not json"));
    }

    [Fact]
    public void Webhook_เส้นสลิปภาษีในกลุ่ม_รูปและปุ่มเท่านั้น()
    {
        Assert.Equal(CentralSlipRoute.Media, LineGroupBindRule.CentralGroupSlipRoute("message", "group", "image", null, null));
        Assert.Equal(CentralSlipRoute.Media, LineGroupBindRule.CentralGroupSlipRoute("message", "group", "file", "slip.PDF", null));
        Assert.Equal(CentralSlipRoute.None, LineGroupBindRule.CentralGroupSlipRoute("message", "group", "file", "a.docx", null));
        Assert.Equal(CentralSlipRoute.None, LineGroupBindRule.CentralGroupSlipRoute("message", "room", "image", null, null));
        Assert.Equal(CentralSlipRoute.None, LineGroupBindRule.CentralGroupSlipRoute("message", "group", "text", null, null));
        Assert.Equal(CentralSlipRoute.Choice, LineGroupBindRule.CentralGroupSlipRoute("postback", "group", null, null, "tsc|x|0|y"));
        Assert.Equal(CentralSlipRoute.None, LineGroupBindRule.CentralGroupSlipRoute("postback", "group", null, null, "lcs|x"));
        // ทิศตรงข้าม: ด่านเดิมของตัวจัดการกลุ่มยังไม่รับรูป (กลุ่มที่ไม่ผูกไม่ถูกอ่าน)
        Assert.False(LineGroupBindRule.CentralGroupEventActionable("message", null));
    }
}
