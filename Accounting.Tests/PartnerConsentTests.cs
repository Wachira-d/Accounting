using Accounting.Helpers;
using Accounting.Models.Constants;
using Accounting.Models.Entities;
using Accounting.Models.Enums;
using Xunit;
using Scope = Accounting.Helpers.PartnerConsent.Scope;
using Need = Accounting.Helpers.PartnerConsent.Need;

namespace Accounting.Tests;

/// <summary>รอบ 264 P1 — ตัวตัดสินความยินยอม "งานอัตโนมัติกับคู่ค้า" (Helpers/PartnerConsent · คำตัดสิน 264-2-1…3) · ทุกด่านมีทิศตรงข้าม</summary>
public class PartnerConsentTests
{
    private static readonly Guid B = Guid.Parse("00000000-0000-0000-0000-0000000000b0");
    private static readonly Guid A = Guid.Parse("00000000-0000-0000-0000-0000000000a0");
    private static readonly Guid Pid = Guid.Parse("11111111-1111-1111-1111-111111111111");
    private static readonly Guid Bank = Guid.Parse("22222222-2222-2222-2222-222222222222");

    private static TradingPartnership Partner(TradingPartnershipStatus st = TradingPartnershipStatus.Accepted, DateTime? suspendedAt = null)
        => new() { Id = Pid, CompanyAId = A, CompanyBId = B, InvitedByCompanyId = A, Status = st, SuspendedAt = suspendedAt };

    private static PartnerAutomationConsent Row(Scope s = Scope.AutoRecordPayment, decimal? max = 5_000m, Guid? bank = null, DateTime? grantedAt = null)
    {
        var text = PartnerConsent.TextFor(s, PartnerConsent.CurrentTextVersion)!;
        return new PartnerAutomationConsent
        {
            CompanyId = B, TradingPartnershipId = Pid, PartnerCompanyId = A, Scope = s.ToString(), MaxAmount = max, DefaultPayBankAccountId = bank,
            TextVersion = PartnerConsent.CurrentTextVersion, EvidenceHash = PartnerConsent.Hash(B, Pid, s, max, bank, PartnerConsent.CurrentTextVersion, text),
            GrantedByUserId = Guid.NewGuid(), GrantedAt = grantedAt ?? new DateTime(2026, 11, 12, 3, 0, 0, DateTimeKind.Utc),
        };
    }

    // ═══ hash canonical ═══

    [Fact]
    public void Hash_เขียนแล้วตรวจผ่าน_round_trip_ทิศตรงข้าม_แก้เพดาน_บัญชี_เวอร์ชัน_ขอบเขต_แล้วไม่ผ่าน()
    {
        var r = Row(bank: Bank);
        Assert.True(PartnerConsent.HashMatches(r));
        Assert.Equal(64, r.EvidenceHash.Length);
        Assert.Equal(r.EvidenceHash, r.EvidenceHash.ToLowerInvariant());

        var cap = Row(bank: Bank); cap.MaxAmount = 50_000m;
        Assert.False(PartnerConsent.HashMatches(cap));
        var acct = Row(bank: Bank); acct.DefaultPayBankAccountId = Guid.NewGuid();
        Assert.False(PartnerConsent.HashMatches(acct));
        var ver = Row(bank: Bank); ver.TextVersion = 99;
        Assert.False(PartnerConsent.HashMatches(ver));
        var sc = Row(bank: Bank); sc.Scope = nameof(Scope.AutoApprovePurchase);
        Assert.False(PartnerConsent.HashMatches(sc));
        var co = Row(bank: Bank); co.CompanyId = A;
        Assert.False(PartnerConsent.HashMatches(co));
    }

    [Fact]
    public void Hash_คงรูป_เงิน_100_กับ_100_00_เท่ากัน_ไม่มีค่า_กับ_ศูนย์_ต่างกัน()
    {
        var t = PartnerConsent.TextFor(Scope.AutoApprovePurchase, 1)!;
        Assert.Equal(PartnerConsent.Hash(B, Pid, Scope.AutoApprovePurchase, 100m, null, 1, t), PartnerConsent.Hash(B, Pid, Scope.AutoApprovePurchase, 100.00m, null, 1, t));
        Assert.NotEqual(PartnerConsent.Hash(B, Pid, Scope.AutoApprovePurchase, null, null, 1, t), PartnerConsent.Hash(B, Pid, Scope.AutoApprovePurchase, 0m, null, 1, t));
        Assert.NotEqual(PartnerConsent.Hash(B, Pid, Scope.AutoApprovePurchase, 100m, null, 1, t), PartnerConsent.Hash(B, Pid, Scope.AutoApprovePurchase, 100m, null, 1, t + " "));
        Assert.Contains("\n100.00\n", PartnerConsent.Canonical(B, Pid, Scope.AutoApprovePurchase, 100m, null, 1, t));
    }

    [Theory]
    [InlineData("AutoRecordPayment", true)]
    [InlineData("ReturnWhtCerts", true)]
    [InlineData("3", false)]
    [InlineData("-1", false)]
    [InlineData("autorecordpayment", false)]
    [InlineData("", false)]
    [InlineData(null, false)]
    [InlineData("Everything", false)]
    public void TryParse_รับเฉพาะชื่อ(string? name, bool ok) => Assert.Equal(ok, PartnerConsent.TryParse(name, out _));

    [Fact]
    public void ข้อความยินยอม_มีทุกขอบเขต_บอกวิธีถอน_เวอร์ชันที่ไม่มีคืน_null()
    {
        foreach (var d in PartnerConsent.Scopes)
        {
            var t = PartnerConsent.TextFor(d.Scope, PartnerConsent.CurrentTextVersion);
            Assert.False(string.IsNullOrWhiteSpace(t));
            Assert.Contains("ถอนได้ทุกเมื่อ", t);
        }
        Assert.Null(PartnerConsent.TextFor(Scope.AutoRecordPayment, 0));
        Assert.Contains("ไม่เดาบัญชีที่จ่าย", PartnerConsent.TextFor(Scope.AutoRecordPayment, 1));
        Assert.Equal(5, PartnerConsent.Scopes.Count);
    }

    // ═══ ความยินยอมกว้างกว่าสิทธิ์ผู้ให้ไม่ได้ (F5) ═══

    [Fact]
    public void GrantProblem_ไม่มีสิทธิ์อนุมัติใบซื้อ_เปิดบันทึกจ่ายเองไม่ได้_ทิศตรงข้าม_มีครบผ่าน()
    {
        var only = new HashSet<Need> { Need.CompanySettingsEdit };
        var p = PartnerConsent.GrantProblem(Scope.AutoRecordPayment, only.Contains);
        Assert.NotNull(p);
        Assert.Contains("Document.Purchase.Approve", p);
        Assert.Null(PartnerConsent.GrantProblem(Scope.AutoRecordPayment, new HashSet<Need> { Need.CompanySettingsEdit, Need.PurchaseApprove }.Contains));
        // ไม่มีสิทธิ์ตั้งค่าบริษัท ⇒ ไม่มีขอบเขตไหนให้ได้
        foreach (var d in PartnerConsent.Scopes)
            Assert.NotNull(PartnerConsent.GrantProblem(d.Scope, n => n != Need.CompanySettingsEdit));
        // ขอบเขตแคบ (สลิป) ไม่ต้องมีสิทธิ์อนุมัติ
        Assert.Null(PartnerConsent.GrantProblem(Scope.ReceiveSlipCopy, new HashSet<Need> { Need.CompanySettingsEdit, Need.PurchaseView }.Contains));
    }

    [Fact]
    public void KeyOf_ทุก_Need_เป็นคีย์สิทธิ์จริง()
    {
        foreach (var n in Enum.GetValues<Need>()) Assert.True(PermissionKeys.IsPermissionKey(PartnerConsent.KeyOf(n)));
    }

    // ═══ รูปคำขอ + สองขั้น ═══

    [Fact]
    public void RequestProblem_ขอบเขตลงบัญชีอัตโนมัติต้องมีเพดาน_บัญชีจ่ายเฉพาะบันทึกจ่าย_ขอบเขตทั้งบริษัทยังไม่เปิดต่อคู่()
    {
        Assert.NotNull(PartnerConsent.RequestProblem(Scope.AutoRecordPayment, null, null));
        Assert.Null(PartnerConsent.RequestProblem(Scope.AutoRecordPayment, 1_000m, Bank));
        Assert.Null(PartnerConsent.RequestProblem(Scope.AutoRecordPayment, 1_000m, null));
        Assert.NotNull(PartnerConsent.RequestProblem(Scope.AutoApprovePurchase, 1_000m, Bank));
        Assert.NotNull(PartnerConsent.RequestProblem(Scope.AutoApprovePurchase, 0m, null));
        Assert.NotNull(PartnerConsent.RequestProblem(Scope.AutoApprovePurchase, -5m, null));
        Assert.NotNull(PartnerConsent.RequestProblem(Scope.AutoRecordPayment, 1_000m, Guid.Empty));
        Assert.Null(PartnerConsent.RequestProblem(Scope.ReceiveSlipCopy, null, null));
        Assert.Contains("264-2-1", PartnerConsent.RequestProblem(Scope.PurchaseDrafts, null, null));
        Assert.Contains("264-2-1", PartnerConsent.RequestProblem(Scope.ReturnWhtCerts, null, null));
    }

    [Fact]
    public void ConfirmProblem_เวอร์ชันเก่า_hash_ไม่ตรง_ไม่พิมพ์ยืนยัน_ปฏิเสธ_ทิศตรงข้าม_ครบผ่าน()
    {
        var h = Row().EvidenceHash;
        Assert.NotNull(PartnerConsent.ConfirmProblem(Scope.AutoRecordPayment, 0, h, h, PartnerConsent.ConfirmPhrase));
        Assert.NotNull(PartnerConsent.ConfirmProblem(Scope.AutoRecordPayment, 1, new string('0', 64), h, PartnerConsent.ConfirmPhrase));
        Assert.NotNull(PartnerConsent.ConfirmProblem(Scope.AutoRecordPayment, 1, null, h, PartnerConsent.ConfirmPhrase));
        Assert.NotNull(PartnerConsent.ConfirmProblem(Scope.AutoRecordPayment, 1, h, h, null));
        Assert.NotNull(PartnerConsent.ConfirmProblem(Scope.AutoRecordPayment, 1, h, h, "ok"));
        Assert.Null(PartnerConsent.ConfirmProblem(Scope.AutoRecordPayment, 1, h.ToUpperInvariant(), h, " " + PartnerConsent.ConfirmPhrase + " "));
        // ขอบเขตที่ไม่ลงบัญชีไม่ต้องพิมพ์
        Assert.Null(PartnerConsent.ConfirmProblem(Scope.ReceiveSlipCopy, 1, h, h, null));
    }

    // ═══ ตอนใช้ ═══

    [Fact]
    public void Allows_ผ่านเมื่อครบ_ทิศตรงข้าม_ทีละด่าน()
    {
        var ok = PartnerConsent.Allows(Scope.AutoRecordPayment, Row(), Partner(), B, 4_999m, true);
        Assert.True(ok.Ok);
        Assert.Equal(PartnerConsent.Codes.Ok, ok.Code);

        Assert.Equal(PartnerConsent.Codes.NoConsent, PartnerConsent.Allows(Scope.AutoRecordPayment, null, Partner(), B, 1m, true).Code);
        Assert.Equal(PartnerConsent.Codes.NoPartner, PartnerConsent.Allows(Scope.AutoRecordPayment, Row(), null, B, 1m, true).Code);
        Assert.Equal(PartnerConsent.Codes.NoPartner, PartnerConsent.Allows(Scope.AutoRecordPayment, Row(), Partner(), Guid.NewGuid(), 1m, true).Code);
        Assert.Equal(PartnerConsent.Codes.PartnerNotActive, PartnerConsent.Allows(Scope.AutoRecordPayment, Row(), Partner(TradingPartnershipStatus.Suspended), B, 1m, true).Code);
        Assert.Equal(PartnerConsent.Codes.PartnerNotActive, PartnerConsent.Allows(Scope.AutoRecordPayment, Row(), Partner(TradingPartnershipStatus.Pending), B, 1m, true).Code);

        var w = Row(); w.WithdrawnAt = DateTime.UtcNow;
        Assert.Equal(PartnerConsent.Codes.Withdrawn, PartnerConsent.Allows(Scope.AutoRecordPayment, w, Partner(), B, 1m, true).Code);

        // แถวของขอบเขตอื่น/บริษัทอื่น ⇒ ไม่ใช่หลักฐาน
        Assert.Equal(PartnerConsent.Codes.WrongRow, PartnerConsent.Allows(Scope.AutoApprovePurchase, Row(), Partner(), B, 1m, true).Code);
        Assert.Equal(PartnerConsent.Codes.WrongRow, PartnerConsent.Allows(Scope.AutoRecordPayment, Row(), Partner(), A, 1m, true).Code);

        var t = Row(); t.MaxAmount = 9_999_999m;   // แก้เพดานทับโดยไม่ผ่านเส้นให้
        Assert.Equal(PartnerConsent.Codes.Tampered, PartnerConsent.Allows(Scope.AutoRecordPayment, t, Partner(), B, 1m, true).Code);

        Assert.Equal(PartnerConsent.Codes.GrantorLostPermission, PartnerConsent.Allows(Scope.AutoRecordPayment, Row(), Partner(), B, 1m, false).Code);
        Assert.Equal(PartnerConsent.Codes.AmountUnknown, PartnerConsent.Allows(Scope.AutoRecordPayment, Row(), Partner(), B, null, true).Code);
        Assert.Equal(PartnerConsent.Codes.OverCap, PartnerConsent.Allows(Scope.AutoRecordPayment, Row(), Partner(), B, 5_000.01m, true).Code);
        Assert.True(PartnerConsent.Allows(Scope.AutoRecordPayment, Row(), Partner(), B, 5_000m, true).Ok);   // เท่าเพดาน = ผ่าน
    }

    [Fact]
    public void Allows_ระงับแล้วเชื่อมกลับ_ความยินยอมเดิมไม่ฟื้นเอง_ทิศตรงข้าม_ให้หลังเชื่อมกลับผ่าน()
    {
        var granted = new DateTime(2026, 11, 1, 0, 0, 0, DateTimeKind.Utc);
        var suspended = new DateTime(2026, 11, 5, 0, 0, 0, DateTimeKind.Utc);
        Assert.Equal(PartnerConsent.Codes.StaleAfterSuspend,
            PartnerConsent.Allows(Scope.AutoRecordPayment, Row(grantedAt: granted), Partner(TradingPartnershipStatus.Accepted, suspended), B, 1m, true).Code);
        Assert.True(PartnerConsent.Allows(Scope.AutoRecordPayment, Row(grantedAt: suspended.AddDays(1)), Partner(TradingPartnershipStatus.Accepted, suspended), B, 1m, true).Ok);
    }

    [Fact]
    public void Allows_ขอบเขตที่ไม่ใช้ยอด_ไม่ต้องรู้ยอด_เพดานว่าง_ไม่จำกัด()
    {
        var r = Row(Scope.ReceiveSlipCopy, max: null);
        Assert.True(PartnerConsent.Allows(Scope.ReceiveSlipCopy, r, Partner(), B, null, true).Ok);
        Assert.True(PartnerConsent.Allows(Scope.ReceiveSlipCopy, r, Partner(), B, 10_000_000m, true).Ok);
    }

    [Fact]
    public void AllowsCompanyWide_ติ๊กปิด_หรือ_คู่ระงับ_ไม่ผ่าน_ทิศตรงข้าม_ติ๊กเปิดและเชื่อมอยู่ผ่าน()
    {
        Assert.True(PartnerConsent.AllowsCompanyWide(Scope.PurchaseDrafts, true, Partner(), B).Ok);
        Assert.Equal(PartnerConsent.Codes.CompanyWideOff, PartnerConsent.AllowsCompanyWide(Scope.PurchaseDrafts, false, Partner(), B).Code);
        Assert.Equal(PartnerConsent.Codes.PartnerNotActive, PartnerConsent.AllowsCompanyWide(Scope.ReturnWhtCerts, true, Partner(TradingPartnershipStatus.Suspended), B).Code);
        var cfg = new WorkflowAutomationConfig { CompanyId = B, CreatePurchaseDraftsFromPartners = true, SendWhtCertsToPartners = false };
        Assert.True(PartnerConsent.CompanyWideToggleOn(Scope.PurchaseDrafts, cfg));
        Assert.False(PartnerConsent.CompanyWideToggleOn(Scope.ReturnWhtCerts, cfg));
        Assert.False(PartnerConsent.CompanyWideToggleOn(Scope.AutoRecordPayment, cfg));
        Assert.False(PartnerConsent.CompanyWideToggleOn(Scope.PurchaseDrafts, null));
    }

    [Fact]
    public void UseCheck_ไม่ผ่าน_ไม่ส่งบัญชีจ่ายออกไป_ผ่าน_ส่งผู้ให้และแถว()
    {
        var r = Row(bank: Bank);
        var ok = PartnerConsent.UseCheck.From(PartnerConsent.Allows(Scope.AutoRecordPayment, r, Partner(), B, 10m, true), r);
        Assert.True(ok.Ok);
        Assert.Equal(Bank, ok.DefaultPayBankAccountId);
        Assert.Equal(r.GrantedByUserId, ok.GrantorUserId);
        var no = PartnerConsent.UseCheck.From(PartnerConsent.Allows(Scope.AutoRecordPayment, r, Partner(), B, 10m, false), r);
        Assert.False(no.Ok);
        Assert.Null(no.DefaultPayBankAccountId);
    }

    [Fact]
    public void ป้ายผล_ขอบเขตที่ยังไม่มีท่ออ่าน_บอกว่ายังไม่มีผล_ขอบเขตทั้งบริษัท_บอกที่ตั้ง()
    {
        Assert.Contains("ยังไม่มีงานอัตโนมัติ", PartnerConsent.EffectNote(PartnerConsent.Def(Scope.AutoRecordPayment)));
        Assert.Contains("ทั้งบริษัท", PartnerConsent.EffectNote(PartnerConsent.Def(Scope.PurchaseDrafts)));
        Assert.Equal("/pages/partner-automation.html?partnership=" + Pid.ToString("D"), PartnerConsent.PageUrl(Pid));
    }

    // ═══ audit ค่าตั้งเดิม (C-06) ═══

    [Fact]
    public void ConfigDiff_ไม่เปลี่ยน_ว่าง_ทิศตรงข้าม_เปลี่ยนติ๊ก_เห็นเฉพาะช่องนั้น()
    {
        var c = new WorkflowAutomationConfig { CompanyId = B };
        var before = PartnerConsent.ConfigSnapshot(c, false);
        Assert.Empty(PartnerConsent.ConfigDiff(before, PartnerConsent.ConfigSnapshot(c, false)));
        c.CreatePurchaseDraftsFromPartners = true;
        var after = PartnerConsent.ConfigSnapshot(c, true);
        var d = PartnerConsent.ConfigDiff(before, after);
        Assert.Equal(new[] { "createPurchaseDraftsFromPartners", "partnerDiscoverable" }, d);
        Assert.Equal("true", after["createPurchaseDraftsFromPartners"]);
        c.AutoApproveMaxAmount = 100m;
        Assert.Equal("100.00", PartnerConsent.ConfigSnapshot(c, true)["autoApproveMaxAmount"]);
    }
}
