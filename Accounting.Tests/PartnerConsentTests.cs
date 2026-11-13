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

    private static PartnerConsent.CapAmount T(decimal v) => PartnerConsent.CapAmount.Thb(v);

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
        var ok = PartnerConsent.Allows(Scope.AutoRecordPayment, Row(), Partner(), B, T(4_999m), true);
        Assert.True(ok.Ok);
        Assert.Equal(PartnerConsent.Codes.Ok, ok.Code);

        Assert.Equal(PartnerConsent.Codes.NoConsent, PartnerConsent.Allows(Scope.AutoRecordPayment, null, Partner(), B, T(1m), true).Code);
        Assert.Equal(PartnerConsent.Codes.NoPartner, PartnerConsent.Allows(Scope.AutoRecordPayment, Row(), null, B, T(1m), true).Code);
        Assert.Equal(PartnerConsent.Codes.NoPartner, PartnerConsent.Allows(Scope.AutoRecordPayment, Row(), Partner(), Guid.NewGuid(), T(1m), true).Code);
        Assert.Equal(PartnerConsent.Codes.PartnerNotActive, PartnerConsent.Allows(Scope.AutoRecordPayment, Row(), Partner(TradingPartnershipStatus.Suspended), B, T(1m), true).Code);
        Assert.Equal(PartnerConsent.Codes.PartnerNotActive, PartnerConsent.Allows(Scope.AutoRecordPayment, Row(), Partner(TradingPartnershipStatus.Pending), B, T(1m), true).Code);

        var w = Row(); w.WithdrawnAt = DateTime.UtcNow;
        Assert.Equal(PartnerConsent.Codes.Withdrawn, PartnerConsent.Allows(Scope.AutoRecordPayment, w, Partner(), B, T(1m), true).Code);

        // แถวของขอบเขตอื่น/บริษัทอื่น ⇒ ไม่ใช่หลักฐาน
        Assert.Equal(PartnerConsent.Codes.WrongRow, PartnerConsent.Allows(Scope.AutoApprovePurchase, Row(), Partner(), B, T(1m), true).Code);
        Assert.Equal(PartnerConsent.Codes.WrongRow, PartnerConsent.Allows(Scope.AutoRecordPayment, Row(), Partner(), A, T(1m), true).Code);

        var t = Row(); t.MaxAmount = 9_999_999m;   // แก้เพดานทับโดยไม่ผ่านเส้นให้
        Assert.Equal(PartnerConsent.Codes.Tampered, PartnerConsent.Allows(Scope.AutoRecordPayment, t, Partner(), B, T(1m), true).Code);

        Assert.Equal(PartnerConsent.Codes.GrantorLostPermission, PartnerConsent.Allows(Scope.AutoRecordPayment, Row(), Partner(), B, T(1m), false).Code);
        Assert.Equal(PartnerConsent.Codes.AmountUnknown, PartnerConsent.Allows(Scope.AutoRecordPayment, Row(), Partner(), B, null, true).Code);
        Assert.Equal(PartnerConsent.Codes.OverCap, PartnerConsent.Allows(Scope.AutoRecordPayment, Row(), Partner(), B, T(5_000.01m), true).Code);
        Assert.True(PartnerConsent.Allows(Scope.AutoRecordPayment, Row(), Partner(), B, T(5_000m), true).Ok);   // เท่าเพดาน = ผ่าน
    }

    [Fact]
    public void Allows_ระงับแล้วเชื่อมกลับ_ความยินยอมเดิมไม่ฟื้นเอง_ทิศตรงข้าม_ให้หลังเชื่อมกลับผ่าน()
    {
        // รอบ 264 P3: ขอบเขตนี้ต่อสายแล้ว (WiredSince 2026-11-12) ⇒ วันที่ของเทสต์อยู่หลังวันต่อสาย (ไม่ให้ GrantedBeforeWired บังผลที่ทดสอบ)
        var granted = new DateTime(2026, 12, 1, 0, 0, 0, DateTimeKind.Utc);
        var suspended = new DateTime(2026, 12, 5, 0, 0, 0, DateTimeKind.Utc);
        Assert.Equal(PartnerConsent.Codes.StaleAfterSuspend,
            PartnerConsent.Allows(Scope.AutoRecordPayment, Row(grantedAt: granted), Partner(TradingPartnershipStatus.Accepted, suspended), B, T(1m), true).Code);
        Assert.True(PartnerConsent.Allows(Scope.AutoRecordPayment, Row(grantedAt: suspended.AddDays(1)), Partner(TradingPartnershipStatus.Accepted, suspended), B, T(1m), true).Ok);
    }

    [Fact]
    public void Allows_ขอบเขตที่ไม่ใช้ยอด_ไม่ต้องรู้ยอด_เพดานว่าง_ไม่จำกัด()
    {
        // รอบ 264 P4: ขอบเขตนี้ต่อสายแล้ว (WiredSince 2026-11-13) ⇒ ให้หลังวันต่อสาย · ทิศตรงข้าม: ให้ก่อนวันต่อสาย ⇒ ต้องยืนยันใหม่
        var r = Row(Scope.ReceiveSlipCopy, max: null, grantedAt: new DateTime(2026, 11, 13, 1, 0, 0, DateTimeKind.Utc));
        Assert.Equal(PartnerConsent.Codes.GrantedBeforeWired,
            PartnerConsent.Allows(Scope.ReceiveSlipCopy, Row(Scope.ReceiveSlipCopy, max: null, grantedAt: new DateTime(2026, 11, 12, 23, 0, 0, DateTimeKind.Utc)), Partner(), B, null, true).Code);
        Assert.True(PartnerConsent.Allows(Scope.ReceiveSlipCopy, r, Partner(), B, null, true).Ok);
        Assert.True(PartnerConsent.Allows(Scope.ReceiveSlipCopy, r, Partner(), B, T(10_000_000m), true).Ok);
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
        var ok = PartnerConsent.UseCheck.From(PartnerConsent.Allows(Scope.AutoRecordPayment, r, Partner(), B, T(10m), true), r);
        Assert.True(ok.Ok);
        Assert.Equal(Bank, ok.DefaultPayBankAccountId);
        Assert.Equal(r.GrantedByUserId, ok.GrantorUserId);
        var no = PartnerConsent.UseCheck.From(PartnerConsent.Allows(Scope.AutoRecordPayment, r, Partner(), B, T(10m), false), r);
        Assert.False(no.Ok);
        Assert.Null(no.DefaultPayBankAccountId);
    }

    [Fact]
    public void ป้ายผล_ขอบเขตที่ยังไม่มีท่ออ่าน_บอกว่ายังไม่มีผล_ขอบเขตทั้งบริษัท_บอกที่ตั้ง()
    {
        Assert.Contains("ยังไม่มีงานอัตโนมัติ", PartnerConsent.EffectNote(PartnerConsent.Def(Scope.AutoApprovePurchase)));
        // รอบ 264 P3: ท่อบันทึกจ่ายอ่านขอบเขตนี้แล้ว · 268-1-1: ชั้นบัญชีตั้งต้นไม่บันทึกเอง ⇒ ป้ายบอกผลจริง
        Assert.Equal(PartnerPaySourceAccount.ConsentDefaultNotAutoNote, PartnerConsent.EffectNote(PartnerConsent.Def(Scope.AutoRecordPayment)));
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

    // ═══ ฝ่ายค้านรอบ 264 P1 ═══

    [Fact]
    public void H1_ยอมรับการเชื่อมใหม่หลังให้ความยินยอม_แถวเดิมไม่ฟื้น_ทิศตรงข้าม_ให้หลังยอมรับผ่าน()
    {
        var granted = new DateTime(2026, 11, 1, 0, 0, 0, DateTimeKind.Utc);
        var p = Partner();
        p.AcceptedAt = granted.AddDays(10);   // เส้นเชิญใหม่ล้าง SuspendedAt แล้วประทับ AcceptedAt ใหม่
        Assert.Equal(PartnerConsent.Codes.StaleAfterSuspend, PartnerConsent.Allows(Scope.AutoRecordPayment, Row(grantedAt: granted), p, B, T(1m), true).Code);
        Assert.True(PartnerConsent.Allows(Scope.AutoRecordPayment, Row(grantedAt: granted.AddDays(11)), p, B, T(1m), true).Ok);
        // ให้ในวินาทีเดียวกับการยอมรับ (เวลาให้ตัดเป็นวินาที) ยังผ่าน
        var acc = new DateTime(2026, 11, 20, 1, 2, 3, DateTimeKind.Utc).AddTicks(4_567_890);
        p.AcceptedAt = acc;
        Assert.True(PartnerConsent.Allows(Scope.AutoRecordPayment, Row(grantedAt: PartnerConsent.TruncateToSecond(acc)), p, B, T(1m), true).Ok);
    }

    [Fact]
    public void M3_ยอดต้องเป็นบาทก่อนหัก_สกุลอื่นหรือติดลบไม่รู้ยอด_L5_ตั้งเพดานไว้แล้วไม่รู้ยอดไม่ผ่าน()
    {
        Assert.Equal(PartnerConsent.Codes.AmountUnknown,
            PartnerConsent.Allows(Scope.AutoRecordPayment, Row(), Partner(), B, new PartnerConsent.CapAmount(100m, "USD"), true).Code);
        Assert.Equal(PartnerConsent.Codes.AmountUnknown, PartnerConsent.Allows(Scope.AutoRecordPayment, Row(), Partner(), B, T(-1m), true).Code);
        Assert.True(PartnerConsent.Allows(Scope.AutoRecordPayment, Row(), Partner(), B, new PartnerConsent.CapAmount(100m, " thb "), true).Ok);
        var wired = new DateTime(2026, 11, 13, 1, 0, 0, DateTimeKind.Utc);   // รอบ 264 P4: ขอบเขตนี้ต่อสายแล้ว (WiredSince 2026-11-13)
        var slipCapped = Row(Scope.ReceiveSlipCopy, max: 1_000m, grantedAt: wired);
        Assert.Equal(PartnerConsent.Codes.AmountUnknown, PartnerConsent.Allows(Scope.ReceiveSlipCopy, slipCapped, Partner(), B, null, true).Code);
        Assert.Equal(PartnerConsent.Codes.OverCap, PartnerConsent.Allows(Scope.ReceiveSlipCopy, slipCapped, Partner(), B, T(1_000.01m), true).Code);
        Assert.True(PartnerConsent.Allows(Scope.ReceiveSlipCopy, Row(Scope.ReceiveSlipCopy, max: null, grantedAt: wired), Partner(), B, null, true).Ok);
    }

    [Fact]
    public void L7_บริษัทคู่ค้าถูกลบหรือระงับ_หยุด()
        => Assert.Equal(PartnerConsent.Codes.PartnerNotActive,
            PartnerConsent.Allows(Scope.AutoRecordPayment, Row(), Partner(), B, T(1m), true, partnerCompanyActive: false).Code);

    [Fact]
    public void M2_บัญชีจ่ายที่ประกาศใช้ไม่ได้แล้ว_ไม่ส่งออกพร้อมเหตุ_ทิศตรงข้าม_ใช้ได้ส่งออก()
    {
        var r = Row(bank: Bank);
        var v = PartnerConsent.Allows(Scope.AutoRecordPayment, r, Partner(), B, T(10m), true);
        var dead = PartnerConsent.UseCheck.From(v, r, payAccountUsable: false);
        Assert.True(dead.Ok);
        Assert.Null(dead.DefaultPayBankAccountId);
        Assert.Equal(PartnerConsent.PayAccountInactive, dead.PayAccountProblem);
        var live = PartnerConsent.UseCheck.From(v, r, payAccountUsable: true);
        Assert.Equal(Bank, live.DefaultPayBankAccountId);
        Assert.Null(live.PayAccountProblem);
        Assert.Null(PartnerConsent.UseCheck.From(v, Row(), payAccountUsable: false).PayAccountProblem);   // ไม่ได้ประกาศ = ไม่มีปัญหา
    }

    [Fact]
    public void L2_PAC2_ผูกผู้ให้_เวลา_คู่ค้า_round_trip_ทิศตรงข้ามแก้แล้วไม่ผ่าน_PAC1_เดิมยังตรวจได้()
    {
        var granted = PartnerConsent.TruncateToSecond(new DateTime(2026, 11, 12, 5, 6, 7, DateTimeKind.Utc).AddTicks(123));
        static PartnerAutomationConsent V2Of(DateTime g)
        {
            var r = Row(bank: Bank, grantedAt: g);
            r.HashVersion = 2;
            r.EvidenceHash = PartnerConsent.HashV2(B, Pid, A, Scope.AutoRecordPayment, r.MaxAmount, Bank, 1,
                PartnerConsent.TextFor(Scope.AutoRecordPayment, 1)!, r.GrantedByUserId, g);
            return r;
        }
        PartnerAutomationConsent V2() => V2Of(granted);
        Assert.True(PartnerConsent.HashMatches(V2()));
        var who = V2(); who.GrantedByUserId = Guid.NewGuid();
        Assert.False(PartnerConsent.HashMatches(who));
        var when = V2(); when.GrantedAt = granted.AddSeconds(1);
        Assert.False(PartnerConsent.HashMatches(when));
        var partner = V2(); partner.PartnerCompanyId = Guid.NewGuid();
        Assert.False(PartnerConsent.HashMatches(partner));
        var unknown = V2(); unknown.HashVersion = 9;
        Assert.False(PartnerConsent.HashMatches(unknown));
        // อ่านกลับจากฐานได้ Kind Unspecified — ผลเท่าเดิม
        var unspec = V2(); unspec.GrantedAt = DateTime.SpecifyKind(granted, DateTimeKind.Unspecified);
        Assert.True(PartnerConsent.HashMatches(unspec));
        Assert.True(PartnerConsent.HashMatches(Row()));   // PAC1
        Assert.Equal(0, PartnerConsent.TruncateToSecond(DateTime.UtcNow).Ticks % TimeSpan.TicksPerSecond);
    }

    [Fact]
    public void L6_อีเมลแจ้งเตือนไม่เข้า_audit_เป็นค่าจริง_แต่ยังเห็นว่าเปลี่ยน()
    {
        var c = new WorkflowAutomationConfig { CompanyId = B, NotifyEmail = "a@x.com" };
        var before = PartnerConsent.ConfigSnapshot(c, false);
        Assert.DoesNotContain("a@x.com", before["notifyEmail"]);
        Assert.StartsWith("sha256:", before["notifyEmail"]);
        c.NotifyEmail = "b@x.com";
        Assert.Equal(new[] { "notifyEmail" }, PartnerConsent.ConfigDiff(before, PartnerConsent.ConfigSnapshot(c, false)));
        c.NotifyEmail = " A@X.com ";
        Assert.Empty(PartnerConsent.ConfigDiff(before, PartnerConsent.ConfigSnapshot(c, false)));
    }

    [Fact]
    public void L3_L4_ทุกขอบเขตมีเวอร์ชันต่ำสุดที่มีข้อความ_ขอบเขตที่ต่อสายแล้วโดยไม่ใช่ค่าตั้งทั้งบริษัทต้องมีวันเริ่มทำงาน()
    {
        foreach (var d in PartnerConsent.Scopes)
        {
            Assert.NotNull(PartnerConsent.TextFor(d.Scope, d.MinTextVersion));
            Assert.InRange(d.MinTextVersion, 1, PartnerConsent.CurrentTextVersion);
            if (d.ReaderWired && !d.CompanyWideToggle) Assert.NotNull(d.WiredSince);   // ด่านของแพ็กที่พลิกธง (L4)
        }
    }
}
