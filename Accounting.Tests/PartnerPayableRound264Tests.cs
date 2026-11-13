using Accounting.Helpers;
using Accounting.Models.Entities;
using Accounting.Models.Enums;
using Xunit;
using Origin = Accounting.Helpers.PartnerPaySourceAccount.Origin;
using Scope = Accounting.Helpers.PartnerConsent.Scope;

namespace Accounting.Tests;

/// <summary>
/// รอบ 264 P3 (team-264-a2b-design §3.1 · §5.1 · §5.3 · §5.5 · คำตัดสิน 264-3-1…264-3-9) — ตัวตัดสิน pure ของท่อฝั่งผู้ซื้อ:
/// แหล่งเงินออก (<see cref="PartnerPaySourceAccount"/> — ไม่รู้ = ไม่รู้ · ไม่มี "บัญชีแรก" · หลักฐานขัดกันไม่อัตโนมัติ) · ด่านบันทึกเอง (<see cref="PartnerAutoRecordPolicy"/>) ·
/// จ่ายบางส่วน (<see cref="PartnerPaymentNoticeRule.PartialPlan"/> · ยอดเต็มเดิมไม่เปลี่ยน) · เลขอ้างอิง/กุญแจของการแจ้ง · ความยินยอมต่อสายแล้ว (ReaderWired + WiredSince)
/// </summary>
public class PartnerPayableRound264Tests
{
    private static readonly Guid BankA = Guid.Parse("aaaaaaaa-0000-0000-0000-000000000001");
    private static readonly Guid BankB = Guid.Parse("bbbbbbbb-0000-0000-0000-000000000002");
    private static readonly Guid Foreign = Guid.Parse("cccccccc-0000-0000-0000-000000000003");

    private static Dictionary<Guid, string> Usable() => new() { [BankA] = "กสิกร · กระแส · ••1111", [BankB] = "กรุงเทพ · ออมทรัพย์ · ••2222" };

    private static PartnerPaySourceAccount.Decision D(PartnerPaySourceAccount.Facts f) => PartnerPaySourceAccount.Decide(f);

    // ═══ แหล่งเงินออก (264-3-1 · C-01) ═══

    [Fact]
    public void ไม่มีหลักฐาน_ไม่รู้_ไม่มีบัญชี_ไม่อัตโนมัติ_ไม่ใช่บัญชีแรกหรือเงินสด()
    {
        var d = D(new(Usable()));
        Assert.Equal(Origin.Unknown, d.Origin);
        Assert.Null(d.BankAccountId);
        Assert.False(d.AllowsAuto);
        Assert.Equal(0m, d.Confidence);
        Assert.Equal(PartnerPaySourceAccount.UnknownReason, d.Reason);
        Assert.Null(d.HintBankAccountId);
        Assert.False(d.UsedAi);
    }

    [Fact]
    public void ประวัติ_เป็นคำแนะนำเท่านั้น_ไม่_preselect_ทิศตรงข้าม_ใบซื้อที่ตั้งธนาคารไว้ยังได้ธนาคารนั้นเป็นคำแนะนำ()
    {
        var h = D(new(Usable(), History: BankB));
        Assert.Null(h.BankAccountId);
        Assert.False(h.AllowsAuto);
        Assert.Equal(BankB, h.HintBankAccountId);
        Assert.Contains("เคยจ่ายผู้ขายนี้จาก", h.HintLabel);

        // ทิศตรงข้ามของการถอด "บัญชีแรก" (design §10 P3): ใบซื้อตั้งธนาคารไว้ ⇒ ยังเห็นธนาคารนั้น (คำแนะนำ — ไม่ preselect)
        var doc = D(new(Usable(), PurchaseDocBank: BankA));
        Assert.Null(doc.BankAccountId);
        Assert.Equal(BankA, doc.HintBankAccountId);
        Assert.Contains("บัญชีที่ตั้งบนใบซื้อ", doc.HintLabel);
        // บัญชีบนใบซื้อที่ไม่อยู่ในชุดที่ใช้ได้ (ปิด/ของบริษัทอื่น) ⇒ ไม่แนะนำ
        Assert.Null(D(new(Usable(), PurchaseDocBank: Foreign)).HintBankAccountId);
    }

    [Fact]
    public void บัญชีในความยินยอม_ใช้ได้_preselect_แต่ไม่อัตโนมัติ_268_1_1_ทิศตรงข้าม_ใช้ไม่ได้แล้ว_ไม่เดาบัญชีอื่น()
    {
        var ok = D(new(Usable(), ConsentDefault: BankA));
        Assert.Equal(Origin.ConsentDefault, ok.Origin);
        Assert.Equal(BankA, ok.BankAccountId);                       // ปุ่มเดียวยัง preselect บัญชีนี้
        Assert.False(ok.AllowsAuto);                                 // 268-1-1: ค่าตั้งตอนยินยอมไม่ใช่หลักฐานว่าเงินออกจริง
        Assert.False(PartnerPaySourceAccount.ConsentDefaultAutoEnabled);
        Assert.StartsWith(PartnerPaySourceAccount.ConsentDefaultNotAutoReason, ok.Reason);
        Assert.Contains("รายการเดินบัญชี", ok.Reason);
        // ทิศตรงข้าม: มีรายการเดินบัญชีตรงบัญชีเดียวกัน ⇒ บันทึกเองได้ (ชั้นที่ 1 ชนะ ไม่ใช่ชั้นที่ 4)
        var withLine = D(new(Usable(), BankLine: BankA, ConsentDefault: BankA));
        Assert.Equal(Origin.BankLine, withLine.Origin);
        Assert.True(withLine.AllowsAuto);
        Assert.DoesNotContain(PartnerPaySourceAccount.ConsentDefaultNotAutoReason, withLine.Reason);

        var gone = D(new(Usable(), ConsentDefault: Foreign));
        Assert.Equal(Origin.Unknown, gone.Origin);
        Assert.Null(gone.BankAccountId);
        var problem = D(new(Usable(), ConsentDefaultProblem: PartnerConsent.PayAccountInactive, History: BankB));
        Assert.Null(problem.BankAccountId);
        Assert.StartsWith(PartnerPaySourceAccount.ConsentAccountUnusableReason, problem.Reason);
        Assert.Equal(BankB, problem.HintBankAccountId);   // ยังเป็นแค่คำแนะนำ
    }

    [Fact]
    public void รายการเดินบัญชี_ชนะที่ผู้จ่ายเลือก_เก็บผู้แพ้_ป้ายเตือน()
    {
        var d = D(new(Usable(), BankLine: BankA, PayerDeclared: BankB, ConsentDefault: BankB));
        Assert.Equal(Origin.BankLine, d.Origin);
        Assert.Equal(BankA, d.BankAccountId);
        Assert.True(d.AllowsAuto);
        Assert.Equal(1.00m, d.Confidence);
        Assert.Contains(d.Losers, l => l.Origin == Origin.PayerDeclared && l.BankAccountId == BankB);
        Assert.Contains(d.Losers, l => l.Origin == Origin.ConsentDefault);
        Assert.Contains("รายการเดินบัญชีชนะ", d.Reason);
    }

    [Fact]
    public void สลิปตรงบัญชีเดียว_ขัดบัญชีในความยินยอม_ไม่อัตโนมัติ_ไม่_preselect_ทิศตรงข้าม_ตรงกันอัตโนมัติ()
    {
        var conflict = D(new(Usable(), SlipMatch: new(PartnerPaySourceAccount.SlipKind.Unique, BankA), ConsentDefault: BankB));
        Assert.Equal(Origin.Unknown, conflict.Origin);
        Assert.Null(conflict.BankAccountId);
        Assert.False(conflict.AllowsAuto);
        Assert.True(conflict.Confidence < PartnerPaySourceAccount.AutoThreshold);
        Assert.Equal(PartnerPaySourceAccount.SlipVsConsentReason, conflict.Reason);
        Assert.Equal(2, conflict.Losers.Count);   // ผู้สมัครทั้งสองให้คนเลือก

        var agree = D(new(Usable(), SlipMatch: new(PartnerPaySourceAccount.SlipKind.Unique, BankA), ConsentDefault: BankA));
        Assert.Equal(Origin.SlipUnique, agree.Origin);
        Assert.True(agree.AllowsAuto);
        Assert.Empty(agree.Losers);
    }

    [Fact]
    public void สลิปหลายบัญชีหรืออ่านไม่ได้_ไม่ใช่หลักฐาน_สลิปไม่ใช่บัญชีบริษัท_ไม่อัตโนมัติ_เปิดทางกรรมการจ่ายแทน_ยกเว้นรายการเดินบัญชี()
    {
        var many = D(new(Usable(), SlipMatch: new(PartnerPaySourceAccount.SlipKind.Many), ConsentDefault: BankA));
        Assert.Equal(Origin.ConsentDefault, many.Origin);
        Assert.False(many.AllowsAuto);   // 268-1-1
        Assert.Equal(Origin.Unknown, D(new(Usable(), SlipMatch: new(PartnerPaySourceAccount.SlipKind.Unreadable))).Origin);
        // สลิปอ้างบัญชีที่ไม่อยู่ในชุดของบริษัท ⇒ ไม่ใช่หลักฐาน (ไม่เชื่อ id)
        Assert.Equal(Origin.Unknown, D(new(Usable(), SlipMatch: new(PartnerPaySourceAccount.SlipKind.Unique, Foreign))).Origin);

        var notOurs = D(new(Usable(), SlipMatch: new(PartnerPaySourceAccount.SlipKind.NotOurs), ConsentDefault: BankA));
        Assert.Equal(Origin.Unknown, notOurs.Origin);
        Assert.Null(notOurs.BankAccountId);
        Assert.False(notOurs.AllowsAuto);
        Assert.True(notOurs.OfferDirectorAdvance);
        Assert.Equal(PartnerPaySourceAccount.SlipNotOursReason, notOurs.Reason);
        Assert.True(D(new(Usable(), SlipMatch: new(PartnerPaySourceAccount.SlipKind.NotOurs))).OfferDirectorAdvance);
        // เงินออกจริงตามรายการเดินบัญชีชนะสลิป
        var line = D(new(Usable(), BankLine: BankB, SlipMatch: new(PartnerPaySourceAccount.SlipKind.NotOurs)));
        Assert.Equal(Origin.BankLine, line.Origin);
        Assert.True(line.AllowsAuto);
    }

    [Fact]
    public void ผู้จ่ายเลือกบัญชีหนึ่ง_สลิปเป็นอีกบัญชี_ไม่อัตโนมัติ()
    {
        var d = D(new(Usable(), PayerDeclared: BankA, SlipMatch: new(PartnerPaySourceAccount.SlipKind.Unique, BankB)));
        Assert.Equal(Origin.Unknown, d.Origin);
        Assert.False(d.AllowsAuto);
        Assert.Equal(PartnerPaySourceAccount.PayerVsSlipReason, d.Reason);
        Assert.True(D(new(Usable(), PayerDeclared: BankA)).AllowsAuto);
    }

    [Fact]
    public void ตารางลำดับชั้น_เป็นข้อมูล_ชั้นอัตโนมัติคือ_1_ถึง_3_เท่านั้น_ชั้นบัญชีตั้งต้นปิด_268_1_1()
    {
        var t = PartnerPaySourceAccount.Precedence;
        Assert.Equal(Enum.GetValues<Origin>().Length, t.Count);
        Assert.Equal(t.Select(r => r.Tier).OrderBy(x => x), t.Select(r => r.Tier));
        Assert.All(t, r => Assert.Equal(r.Tier <= 3, r.AllowsAuto));
        Assert.All(t.Where(r => r.AllowsAuto), r => Assert.True(r.Confidence >= PartnerPaySourceAccount.AutoThreshold));
        Assert.All(Enum.GetValues<Origin>(), o => Assert.False(string.IsNullOrWhiteSpace(PartnerPaySourceAccount.OriginLabel(o))));
    }

    // ═══ ด่านบันทึกเอง (264-3-2 · 264-2-14) ═══

    private static PartnerAutoRecordPolicy.Facts Pass() => new(
        NoticeOpen: true, SellerPaymentKnown: true, SellerPaymentLive: true, SourceDocumentVoided: false, ConsentOk: true, ConsentReason: "ใช้ได้",
        Plan: new PartnerPaymentNoticeRule.Plan(1_040m, 30m, null), Currency: "THB", SourceIsAbbreviated: false, PayerBorneWht: false, VatMismatch: false,
        Source: PartnerPaySourceAccount.Decide(new(Usable(), PayerDeclared: BankA, ConsentDefault: BankA)));

    [Fact]
    public void ทุกช่องมีหลักฐาน_อัตโนมัติ()
    {
        var v = PartnerAutoRecordPolicy.Decide(Pass());
        Assert.True(v.Auto);
        Assert.Equal(PartnerAutoRecordPolicy.Codes.Ok, v.Code);
    }

    [Fact]
    public void แต่ละด่าน_ไม่ผ่านทีละข้อ_กลับเป็นปุ่มเดียวหรือปฏิเสธ_พร้อมเหตุ()
    {
        void Is(PartnerAutoRecordPolicy.Facts f, PartnerAutoRecordPolicy.Kind kind, string code)
        {
            var v = PartnerAutoRecordPolicy.Decide(f);
            Assert.Equal(kind, v.Kind);
            Assert.Equal(code, v.Code);
            Assert.False(string.IsNullOrWhiteSpace(v.Reason));
        }
        var R = PartnerAutoRecordPolicy.Kind.Refuse;
        var O = PartnerAutoRecordPolicy.Kind.OneClick;
        Is(Pass() with { NoticeOpen = false }, R, PartnerAutoRecordPolicy.Codes.NoticeClosed);
        Is(Pass() with { SellerPaymentLive = false }, R, PartnerAutoRecordPolicy.Codes.SellerVoided);
        Is(Pass() with { SourceDocumentVoided = true }, R, PartnerAutoRecordPolicy.Codes.SourceVoided);
        Is(Pass() with { SellerPaymentKnown = false, SellerPaymentLive = false }, O, PartnerAutoRecordPolicy.Codes.SellerPaymentUnknown);
        Is(Pass() with { ConsentOk = false, ConsentReason = "ถอนความยินยอมแล้ว" }, O, PartnerAutoRecordPolicy.Codes.Consent);
        Is(Pass() with { Plan = new(null, null, "PI ยังเป็นร่าง") }, O, PartnerAutoRecordPolicy.Codes.Plan);
        // B3: จ่ายเต็มทั้งที่ต้องหัก ⇒ ปุ่มเดียวเสมอ
        Is(Pass() with { Plan = new(1_070m, 0m, null, "ต้องหัก 30.00") }, O, PartnerAutoRecordPolicy.Codes.PlanWarning);
        Is(Pass() with { Currency = "USD" }, O, PartnerAutoRecordPolicy.Codes.Currency);
        Is(Pass() with { SourceIsAbbreviated = true }, O, PartnerAutoRecordPolicy.Codes.Abbreviated);
        Is(Pass() with { PayerBorneWht = true }, O, PartnerAutoRecordPolicy.Codes.PayerBorne);
        Is(Pass() with { VatMismatch = true }, O, PartnerAutoRecordPolicy.Codes.VatMismatch);
        Is(Pass() with { Source = PartnerPaySourceAccount.Decide(new(Usable())) }, O, PartnerAutoRecordPolicy.Codes.Source);
        // 268-1-1: หลักฐานบัญชีมีแค่บัญชีตั้งต้นในความยินยอม ⇒ ปุ่มเดียว + เหตุที่บอกว่าทำไม
        var consentOnly = PartnerAutoRecordPolicy.Decide(Pass() with { Source = PartnerPaySourceAccount.Decide(new(Usable(), ConsentDefault: BankA)) });
        Assert.Equal(O, consentOnly.Kind);
        Assert.Equal(PartnerAutoRecordPolicy.Codes.Source, consentOnly.Code);
        Assert.StartsWith(PartnerPaySourceAccount.ConsentDefaultNotAutoReason, consentOnly.Reason);
        // ฝ่ายค้าน H1 / M4
        Is(Pass() with { UnexplainedPurchasePayment = true }, O, PartnerAutoRecordPolicy.Codes.OtherPayment);
        Is(Pass() with { WhtDateInPastMonth = true }, O, PartnerAutoRecordPolicy.Codes.WhtPeriod);
        // ฝ่ายค้าน L7: เหตุของใบ (ร่าง/ชำระครบ · สกุล) มาก่อน "ยังไม่ได้ยินยอม"
        Is(Pass() with { ConsentOk = false, Plan = new(null, null, "PI ยังเป็นร่าง") }, O, PartnerAutoRecordPolicy.Codes.Plan);
        Is(Pass() with { ConsentOk = false, Currency = "USD" }, O, PartnerAutoRecordPolicy.Codes.Currency);
        Assert.DoesNotContain("ไม่ต้องบันทึกจ่าย", PartnerAutoRecordPolicy.SellerVoidedReason);   // L8: ไม่สรุปสิ่งที่ระบบไม่ได้ตรวจ
        Assert.All(PartnerAutoRecordPolicy.Codes.Slow, c => Assert.DoesNotContain(c, new[] { PartnerAutoRecordPolicy.Codes.Consent, PartnerAutoRecordPolicy.Codes.Plan,
            PartnerAutoRecordPolicy.Codes.Source, PartnerAutoRecordPolicy.Codes.PostFailed }));
        Assert.Contains("ถอนความยินยอมแล้ว", PartnerAutoRecordPolicy.Decide(Pass() with { ConsentOk = false, ConsentReason = "ถอนความยินยอมแล้ว" }).Reason);
    }

    // ═══ จ่ายบางส่วน (264-3-5 · C-05 · D1/D2) ═══

    [Fact]
    public void จ่ายบางส่วน_ไม่มีภาษีหักค้าง_บันทึกตามเงินจริง_หักศูนย์()
    {
        var p = PartnerPaymentNoticeRule.For(DocumentStatus.Approved, 1_070m, 0m, 500m, "PI-1");
        Assert.Null(p.Problem);
        Assert.Equal(500m, p.Amount);
        Assert.Equal(0m, p.Wht);
        // จ่ายเกินยอดค้าง ⇒ ไม่เดา (เดิม)
        Assert.NotNull(PartnerPaymentNoticeRule.For(DocumentStatus.Approved, 1_070m, 0m, 1_100m, "PI-1").Problem);
    }

    [Fact]
    public void จ่ายบางส่วน_มีภาษีหัก_อัตราเดียวและตรงยอดที่ผู้ขายลง_หักยอดนั้น_ทิศตรงข้าม_ไม่ตรงหรือหลายอัตรา_ไม่เดา()
    {
        // 485 × 3/97 = 15.00 = ที่ผู้ขายลง ⇒ จ่าย 485 + หัก 15
        var p = PartnerPaymentNoticeRule.For(DocumentStatus.Approved, 1_070m, 30m, 485m, "PI-2", sellerRecordedWht: 15m, singleWhtRatePercent: 3m);
        Assert.Null(p.Problem);
        Assert.Equal(485m, p.Amount);
        Assert.Equal(15m, p.Wht);
        Assert.NotNull(PartnerPaymentNoticeRule.For(DocumentStatus.Approved, 1_070m, 30m, 485m, "PI-2", sellerRecordedWht: 14m, singleWhtRatePercent: 3m).Problem);
        Assert.NotNull(PartnerPaymentNoticeRule.For(DocumentStatus.Approved, 1_070m, 30m, 485m, "PI-2", sellerRecordedWht: 15m, singleWhtRatePercent: null).Problem);
        Assert.NotNull(PartnerPaymentNoticeRule.For(DocumentStatus.Approved, 1_070m, 30m, 485m, "PI-2").Problem);   // เส้นเดิม (ไม่ส่งข้อมูลไขว้) ไม่เดา
        // ภาษีหักที่คำนวณเกินที่เหลือ ⇒ ไม่เดา
        Assert.NotNull(PartnerPaymentNoticeRule.For(DocumentStatus.Approved, 1_070m, 10m, 485m, "PI-2", sellerRecordedWht: 15m, singleWhtRatePercent: 3m).Problem);
    }

    [Fact]
    public void ทิศตรงข้าม_ยอดเต็มเดิมทุกเคสไม่เปลี่ยน_แม้ส่งข้อมูลจ่ายบางส่วน()
    {
        foreach (var (wr, cash, sw) in new[] { (30m, 1_040m, 30m), (0m, 1_070m, 0m), (30m, 1_070m, 0m) })
        {
            var oldP = PartnerPaymentNoticeRule.For(DocumentStatus.Approved, 1_070m, wr, cash, "PI-3");
            var newP = PartnerPaymentNoticeRule.For(DocumentStatus.Approved, 1_070m, wr, cash, "PI-3", sellerRecordedWht: sw, singleWhtRatePercent: 3m);
            Assert.Equal(oldP, newP);
        }
    }

    [Fact]
    public void H2_ใบซื้อไม่มีภาษีหัก_แต่ผู้ขายบันทึกว่าถูกหัก_ไม่บันทึกหักศูนย์เงียบๆ_ทิศตรงข้าม_ผู้ขายไม่ได้ลงหัก_ยังได้แผน()
    {
        // ใบ 10,000 + VAT 700 · ผู้ขายรับ 10,400 + ถูกหัก 300 · ใบซื้อของเราไม่มีภาษีหัก ⇒ สองฝั่งขัดกัน ⇒ ไม่เดา
        var p = PartnerPaymentNoticeRule.For(DocumentStatus.Approved, 10_700m, 0m, 10_400m, "PI-H2", sellerRecordedWht: 300m, singleWhtRatePercent: null, purchaseHasVat: true);
        Assert.Null(p.Amount);
        Assert.NotNull(p.Problem);
        var ok = PartnerPaymentNoticeRule.For(DocumentStatus.Approved, 10_700m, 0m, 10_400m, "PI-H2", sellerRecordedWht: 0m, singleWhtRatePercent: null, purchaseHasVat: true);
        Assert.Equal(10_400m, ok.Amount);
        Assert.Equal(0m, ok.Wht);
    }

    [Fact]
    public void L9_จ่ายบางส่วนมีภาษีหัก_ใบซื้อมี_VAT_ไม่เดา_แม้ยอดสองฝั่งตรงกันด้วยสูตรเดียวกัน()
    {
        // 520 × 3/97 = 16.08 — ถ้าใบมี VAT ฐานภาษีหักไม่ใช่เงินที่จ่ายทั้งก้อน ⇒ สองฝั่ง "ตรงกัน" บนเลขผิดได้ ⇒ ไม่เดา
        Assert.NotNull(PartnerPaymentNoticeRule.For(DocumentStatus.Approved, 1_070m, 30m, 520m, "PI-L9", sellerRecordedWht: 16.08m, singleWhtRatePercent: 3m, purchaseHasVat: true).Problem);
        Assert.Null(PartnerPaymentNoticeRule.For(DocumentStatus.Approved, 1_070m, 30m, 520m, "PI-L9", sellerRecordedWht: 16.08m, singleWhtRatePercent: 3m, purchaseHasVat: false).Problem);
        // ยอดเต็ม (เงิน + ภาษีหักที่เหลือ = ยอดค้าง) ไม่ถูกแตะด้วยธง VAT
        Assert.Equal(1_040m, PartnerPaymentNoticeRule.For(DocumentStatus.Approved, 1_070m, 30m, 1_040m, "PI-L9", sellerRecordedWht: 30m, singleWhtRatePercent: 3m, purchaseHasVat: true).Amount);
    }

    [Fact]
    public void M4_วันที่จ่ายอยู่เดือนภาษีก่อน_และมีภาษีหัก_ต้องให้คนเลือกวันที่_ทิศตรงข้าม_เดือนเดียวกันหรือไม่มีภาษีหักผ่าน()
    {
        var now = new DateTime(2026, 12, 20, 3, 0, 0, DateTimeKind.Utc);
        Assert.True(PartnerAutoRecordPolicy.WhtDateInPastMonthOf(new DateTime(2026, 11, 20, 0, 0, 0, DateTimeKind.Utc), 3_000m, now));
        Assert.False(PartnerAutoRecordPolicy.WhtDateInPastMonthOf(new DateTime(2026, 11, 20, 0, 0, 0, DateTimeKind.Utc), 0m, now));
        Assert.False(PartnerAutoRecordPolicy.WhtDateInPastMonthOf(new DateTime(2026, 11, 20, 0, 0, 0, DateTimeKind.Utc), null, now));
        Assert.False(PartnerAutoRecordPolicy.WhtDateInPastMonthOf(new DateTime(2026, 12, 1, 0, 0, 0, DateTimeKind.Utc), 3_000m, now));
        // ขอบเดือนตามเวลาไทย: 30 พ.ย. 18:00 UTC = 1 ธ.ค. 01:00 ไทย ⇒ เดือนเดียวกับ "ตอนนี้" (ธ.ค.)
        Assert.False(PartnerAutoRecordPolicy.WhtDateInPastMonthOf(new DateTime(2026, 11, 30, 18, 0, 0, DateTimeKind.Utc), 3_000m, now));
        Assert.True(PartnerAutoRecordPolicy.WhtDateInPastMonthOf(new DateTime(2025, 12, 25, 0, 0, 0, DateTimeKind.Utc), 3_000m, now));
    }

    [Fact]
    public void M3_ป้ายของบัญชีในความยินยอม_บอกว่าเป็นค่าตั้งบวกคำบอกของผู้ขาย_ยังไม่ตรวจรายการเดินบัญชี()
    {
        var d = D(new(Usable(), ConsentDefault: BankA));
        Assert.StartsWith(PartnerPaySourceAccount.ConsentDefaultLabelPrefix, d.Label);
        Assert.EndsWith(PartnerPaySourceAccount.ConsentDefaultCaveat, d.Label);
        Assert.Contains("ผู้ขายแจ้งรับเงิน", d.Label);
        Assert.Contains("ยังไม่ได้ตรวจกับรายการเดินบัญชี", d.Label);
    }

    // ═══ กุญแจ/เลขอ้างอิงของการแจ้ง (264-3-6) ═══

    [Fact]
    public void เลขอ้างอิง_รูปใหม่ต่อการรับชำระ_แถวเก่าคงรูปเดิม_กันซ้ำตรวจทั้งสองรูป()
    {
        var pay = Guid.NewGuid();
        var intent = Guid.NewGuid();
        Assert.Equal("PARTNER-PAID-P" + pay.ToString("N"), PartnerPaymentNoticeRule.PaymentReferenceFor(pay, intent));
        Assert.Equal(PartnerPaymentNoticeRule.PaymentReference(intent), PartnerPaymentNoticeRule.PaymentReferenceFor(null, intent));
        Assert.Null(PartnerPaymentNoticeRule.PaymentReferenceFor(null, null));
        Assert.Equal(new[] { PartnerPaymentNoticeRule.PaymentReferenceForSellerPayment(pay), PartnerPaymentNoticeRule.PaymentReference(intent) },
            PartnerPaymentNoticeRule.PaymentReferencesToCheck(pay, intent));
        Assert.NotEqual(PartnerPaymentNoticeRule.PaymentReferenceForSellerPayment(pay), PartnerPaymentNoticeRule.PaymentReference(pay));   // สองรูปไม่ชนกัน
    }

    [Fact]
    public void intent_จากเลขอ้างอิงของผู้ขาย_รูปเดียวกับ_GatewayPaymentRecord_อื่นๆไม่เดา()
    {
        var intent = Guid.NewGuid();
        Assert.Equal(intent, PartnerPaymentNoticeRule.IntentFromSellerReference(GatewayPaymentRecord.Reference(intent)));
        Assert.Null(PartnerPaymentNoticeRule.IntentFromSellerReference(null));
        Assert.Null(PartnerPaymentNoticeRule.IntentFromSellerReference("PAY-INTENT-xyz"));
        Assert.Null(PartnerPaymentNoticeRule.IntentFromSellerReference("RC-0001"));
        Assert.Null(PartnerPaymentNoticeRule.IntentFromSellerReference(PartnerPaymentNoticeRule.PaymentReference(intent)));
    }

    [Fact]
    public void ช่องทางของการแจ้ง_ทางเข้าที่ไม่ใช่ลิงก์มีป้ายของตัวเอง_ไม่เรียกว่าลิงก์ชำระ()
    {
        Assert.Equal(nameof(PaymentMethodKind.PromptPay), PartnerPaymentNoticeRule.ChannelOf(PaymentMethodKind.PromptPay, PaymentEntrySource.Gateway));
        Assert.Equal(PartnerPaymentNoticeRule.EntryManual, PartnerPaymentNoticeRule.ChannelOf(null, PaymentEntrySource.Manual));
        Assert.Equal(PartnerPaymentNoticeRule.EntryBank, PartnerPaymentNoticeRule.ChannelOf(null, PaymentEntrySource.BankAutoSettle));
        Assert.Equal(PartnerPaymentNoticeRule.EntryImport, PartnerPaymentNoticeRule.ChannelOf(null, PaymentEntrySource.ImportSendReceipt));
        Assert.Equal(PartnerPaymentNoticeRule.EntryOther, PartnerPaymentNoticeRule.ChannelOf(null, PaymentEntrySource.Integration));
        foreach (var ch in new[] { PartnerPaymentNoticeRule.EntryManual, PartnerPaymentNoticeRule.EntryBank, PartnerPaymentNoticeRule.EntryImport, PartnerPaymentNoticeRule.EntryOther })
        {
            Assert.True(ch.Length <= 32);
            Assert.DoesNotContain("ลิงก์", PartnerPaymentNoticeRule.ChannelLabel(ch));
        }
        Assert.Equal("ผู้ขายยกเลิกการรับเงิน — ถอนการแจ้งแล้ว", PartnerPaymentNoticeRule.StatusLabel(PartnerPaymentNoticeRule.StatusWithdrawn));
        var pay = Guid.NewGuid();
        var key = PartnerPaymentNoticeRule.SellerVoidedKey(pay, Guid.NewGuid());
        Assert.StartsWith(PartnerPaymentNoticeRule.SellerVoidedMarker, key);
        Assert.StartsWith(key, PartnerPaymentNoticeRule.SellerVoidedNote("ผู้ขาย", "INV-1", 100m, DateTime.UtcNow, key));
        Assert.NotEqual(key, PartnerPaymentNoticeRule.SellerVoidedKey(Guid.NewGuid(), Guid.NewGuid()));   // L2: กุญแจต่อการรับชำระ — สองรายการในวันเดียวไม่ชนกัน
    }

    // ═══ ความยินยอมต่อสายแล้ว (P1 contract · L4) ═══

    [Fact]
    public void ขอบเขตบันทึกจ่าย_ต่อสายแล้ว_ให้ไว้ล่วงหน้าก่อนวันต่อสายต้องยืนยันใหม่_ทิศตรงข้าม_ให้หลังวันต่อสายผ่าน()
    {
        var def = PartnerConsent.Def(Scope.AutoRecordPayment);
        Assert.True(def.ReaderWired);
        Assert.Equal(PartnerConsent.AutoRecordPaymentWiredSince, def.WiredSince);
        Assert.Equal(DateTimeKind.Utc, def.WiredSince!.Value.Kind);
        Assert.NotEqual(default, def.WiredSince!.Value);   // ตัวเริ่มค่า static ประกาศก่อน Scopes (ไม่ใช่ 0001-01-01)

        Guid a = Guid.NewGuid(), b = Guid.NewGuid(), pid = Guid.NewGuid();
        var p = new TradingPartnership { Id = pid, CompanyAId = a, CompanyBId = b, InvitedByCompanyId = a, Status = TradingPartnershipStatus.Accepted };
        PartnerAutomationConsent Row(DateTime grantedAt)
        {
            var text = PartnerConsent.TextFor(Scope.AutoRecordPayment, PartnerConsent.CurrentTextVersion)!;
            return new PartnerAutomationConsent
            {
                CompanyId = b, TradingPartnershipId = pid, PartnerCompanyId = a, Scope = nameof(Scope.AutoRecordPayment), MaxAmount = 5_000m,
                TextVersion = PartnerConsent.CurrentTextVersion, EvidenceHash = PartnerConsent.Hash(b, pid, Scope.AutoRecordPayment, 5_000m, null, PartnerConsent.CurrentTextVersion, text),
                GrantedByUserId = Guid.NewGuid(), GrantedAt = grantedAt,
            };
        }
        var before = PartnerConsent.Allows(Scope.AutoRecordPayment, Row(PartnerConsent.AutoRecordPaymentWiredSince.AddDays(-1)), p, b, PartnerConsent.CapAmount.Thb(10m), true);
        Assert.Equal(PartnerConsent.Codes.GrantedBeforeWired, before.Code);
        Assert.True(PartnerConsent.Allows(Scope.AutoRecordPayment, Row(PartnerConsent.AutoRecordPaymentWiredSince.AddHours(1)), p, b, PartnerConsent.CapAmount.Thb(10m), true).Ok);
        Assert.Equal(PartnerPaySourceAccount.ConsentDefaultNotAutoNote, PartnerConsent.EffectNote(def));   // 268-1-1: หน้าความยินยอมบอกผลจริง (ไม่ silent no-op)
    }
}
