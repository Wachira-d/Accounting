using Accounting.Helpers;
using Accounting.Models.Entities;
using Accounting.Models.Enums;
using Xunit;
using Scope = Accounting.Helpers.PartnerConsent.Scope;

namespace Accounting.Tests;

/// <summary>
/// รอบ 288 (คำสั่งเจ้าของ "ทำให้ใช้งานได้ทั้งหมด" — หน้า "งานอัตโนมัติกับคู่ค้า"): ตัวตัดสิน pure ของ (1) อนุมัติใบซื้อจากคู่ค้าเองภายใต้เพดาน
/// (<see cref="PartnerAutoApprovePolicy"/>) · (2) รับสำเนาสลิปจากคู่ค้า (<see cref="PartnerSlipCopyRule"/>) · (3) ข้อความบนหน้าเป็นภาษาผู้ใช้
/// (<see cref="PartnerConsent.ExplainOf"/> · <see cref="PartnerConsent.StatusLabel"/>) — ทุกข้อมีทิศตรงข้าม (ใบที่ควรอนุมัติไม่ถูกกัน)
/// </summary>
public class PartnerAutomationRound288Tests
{
    private static readonly DateTime D = new(2026, 11, 21, 0, 0, 0, DateTimeKind.Utc);

    private static PartnerAutoApprovePolicy.Line L(int i, bool account = true, bool local = true, bool vendor = false, bool vatBlocked = false, string? desc = null)
        => new(i, desc ?? "ค่าบริการ " + i, account, local, vendor, vatBlocked);

    private static PartnerAutoApprovePolicy.Facts Ok(params PartnerAutoApprovePolicy.Line[] lines) => new(
        ConsentOk: true, ConsentReason: "ใช้ได้", GrantorMayFinalize: true, IsDraft: true, SourceVoided: false, Currency: "THB",
        IsTaxInvoice: true, TaxInvoiceMissing: Array.Empty<string>(), Lines: lines.Length == 0 ? new[] { L(1) } : lines, WhtProblem: null,
        ApprovalWarnings: Array.Empty<string>());

    [Fact]
    public void อนุมัติเองเมื่อครบทุกเงื่อนไข_ผังจากโมเดลในบ้านหรือประวัติผู้ขาย()
    {
        var v = PartnerAutoApprovePolicy.Decide(Ok(L(1, local: true), L(2, local: false, vendor: true)));
        Assert.True(v.Auto);
        Assert.Equal(PartnerAutoApprovePolicy.Codes.Approved, v.Code);
        Assert.False(v.UsedAi);
    }

    [Fact]
    public void ไม่ผ่านข้อใดข้อหนึ่ง_คงเป็นร่างพร้อมเหตุภาษาไทย()
    {
        void Hold(PartnerAutoApprovePolicy.Facts f, string code)
        {
            var v = PartnerAutoApprovePolicy.Decide(f);
            Assert.False(v.Auto);
            Assert.Equal(code, v.Code);
            Assert.False(string.IsNullOrWhiteSpace(v.Reason));
        }
        Hold(Ok() with { ConsentOk = false, ConsentReason = "ยอด 9,000.00 บาท เกินเพดานที่ยินยอมไว้ 5,000.00 บาท" }, PartnerAutoApprovePolicy.Codes.Consent);
        Hold(Ok() with { GrantorMayFinalize = false }, PartnerAutoApprovePolicy.Codes.GrantorDraftOnly);
        Hold(Ok() with { IsDraft = false }, PartnerAutoApprovePolicy.Codes.NotDraft);
        Hold(Ok() with { SourceVoided = true }, PartnerAutoApprovePolicy.Codes.SourceVoided);
        Hold(Ok() with { Currency = "USD" }, PartnerAutoApprovePolicy.Codes.Currency);
        Hold(Ok() with { Lines = Array.Empty<PartnerAutoApprovePolicy.Line>() }, PartnerAutoApprovePolicy.Codes.NoLines);
        Hold(Ok() with { TaxInvoiceMissing = new[] { "ที่อยู่ผู้ขาย" } }, PartnerAutoApprovePolicy.Codes.TaxInvoice);
        Hold(Ok(L(1, account: false)), PartnerAutoApprovePolicy.Codes.GlMissing);
        Hold(Ok(L(1, local: false, vendor: false)), PartnerAutoApprovePolicy.Codes.GlUnsure);
        Hold(Ok(L(1, vatBlocked: true)), PartnerAutoApprovePolicy.Codes.InputVatBlocked);
        Hold(Ok() with { WhtProblem = "อัตราไม่ชัด" }, PartnerAutoApprovePolicy.Codes.Wht);
        Hold(Ok() with { ApprovalWarnings = new[] { "[§65 ตรี] ค่ารับรองเกินเพดาน" } }, PartnerAutoApprovePolicy.Codes.Warnings);
        // ทิศตรงข้าม: ใบที่ไม่ใช่ใบกำกับ ไม่ถูกกันด้วยรายการ ม.86/4
        Assert.True(PartnerAutoApprovePolicy.Decide(Ok() with { IsTaxInvoice = false, TaxInvoiceMissing = new[] { "เลขที่ใบกำกับ" } }).Auto);
    }

    [Fact]
    public void ผังมั่นใจ_ต้องตรงผังบนบรรทัด_และความมั่นใจของโมเดลในบ้านถึงเกณฑ์()
    {
        Assert.True(PartnerAutoApprovePolicy.LocalConfidentOf("53100", "53100", 0.85m));
        Assert.False(PartnerAutoApprovePolicy.LocalConfidentOf("53100", "53100", 0.84m));
        Assert.False(PartnerAutoApprovePolicy.LocalConfidentOf("53100", "53200", 0.99m));   // คนเปลี่ยนผังแล้ว — คำตอบเดิมไม่ใช่หลักฐานของผังใหม่
        Assert.False(PartnerAutoApprovePolicy.LocalConfidentOf(null, "53100", 0.99m));
        Assert.False(PartnerAutoApprovePolicy.LocalConfidentOf("53100", null, null));       // AI ภายนอกตอบอย่างเดียว (ไม่มีคำตอบโมเดลในบ้าน) ไม่นับ
    }

    [Fact]
    public void ใบกำกับ_ไม่นับข้อยังไม่ได้ต้นฉบับ_อนุมัติไม่ใช่การเคลม()
    {
        var paper = PartnerInputVatEvidence.MissingLabel(PartnerInputVatEvidence.PaperRequired);
        Assert.Empty(PartnerAutoApprovePolicy.TaxInvoiceMissingForApproval(new[] { paper }, paper));
        Assert.Equal(new[] { "ที่อยู่ผู้ขาย" }, PartnerAutoApprovePolicy.TaxInvoiceMissingForApproval(new[] { "ที่อยู่ผู้ขาย", paper }, paper));
        Assert.Equal(new[] { "ที่อยู่ผู้ขาย" }, PartnerAutoApprovePolicy.TaxInvoiceMissingForApproval(new[] { "ที่อยู่ผู้ขาย" }, null));
    }

    [Fact]
    public void ภาษีหัก_ณ_ที่จ่าย_ชัดเจน_กับไม่ชัด()
    {
        var w = (decimal r, decimal a) => new PartnerAutoApprovePolicy.WhtLine(r, a);
        Assert.Null(PartnerAutoApprovePolicy.WhtProblem(new[] { w(0m, 0m) }, 0m, 0m, D));
        Assert.Null(PartnerAutoApprovePolicy.WhtProblem(new[] { w(3m, 30m), w(1m, 5m) }, 35m, 0m, D));   // อัตราผสมตามกฎหมายได้
        Assert.NotNull(PartnerAutoApprovePolicy.WhtProblem(new[] { w(3m, 30m) }, 30m, 30m, D));          // ผู้จ่ายออกแทน
        Assert.NotNull(PartnerAutoApprovePolicy.WhtProblem(new[] { w(4m, 40m) }, 40m, 0m, D));           // อัตราที่ไม่ใช่ของกฎหมาย
        Assert.NotNull(PartnerAutoApprovePolicy.WhtProblem(new[] { w(3m, 30m) }, 31m, 0m, D));           // หัวใบไม่ตรงบรรทัด
        Assert.NotNull(PartnerAutoApprovePolicy.WhtProblem(new[] { w(0m, 0m) }, 30m, 0m, D));            // หัวใบมีแต่ไม่มีอัตรา
    }

    [Fact]
    public void แจ้ง_ยังไม่อนุมัติเอง_ครั้งเดียวต่อเหตุ_หมายเหตุมีป้าย()
    {
        Assert.True(PartnerAutoApprovePolicy.ShouldAnnounceHold(null, PartnerAutoApprovePolicy.Codes.GlUnsure));
        Assert.False(PartnerAutoApprovePolicy.ShouldAnnounceHold(PartnerAutoApprovePolicy.Codes.GlUnsure, PartnerAutoApprovePolicy.Codes.GlUnsure));
        Assert.True(PartnerAutoApprovePolicy.ShouldAnnounceHold(PartnerAutoApprovePolicy.Codes.GlUnsure, PartnerAutoApprovePolicy.Codes.Consent));
        Assert.StartsWith(PartnerAutoApprovePolicy.HoldMarker, PartnerAutoApprovePolicy.HoldNote("เหตุ", D));
        var ok = PartnerAutoApprovePolicy.ApprovedNote("คุณเอ", 5_000m, D);
        Assert.StartsWith(PartnerAutoApprovePolicy.ApprovedMarker, ok);
        Assert.Contains("5,000.00", ok);
        Assert.Contains("ภาษีซื้อยังรอต้นฉบับ", ok);
    }

    // ═══ ReceiveSlipCopy ═══

    private static PartnerSlipCopyRule.Facts Slip() => new(
        ConsentOk: true, ConsentGrantedAt: D, NoticeCreatedAt: D.AddHours(1), HasIntent: true, IntentIsSlip: true, IntentSucceeded: true,
        SlipAttachmentId: Guid.NewGuid(), AttachmentFound: true, AttachmentChannel: "portal-customer");

    [Fact]
    public void สำเนาสลิป_ยินยอม_ผู้ขายยืนยันแล้ว_มีไฟล์_คัดสำเนา()
        => Assert.Equal(PartnerSlipCopyRule.Outcome.Copy, PartnerSlipCopyRule.Decide(Slip()).Outcome);

    [Fact]
    public void สำเนาสลิป_ไม่ยินยอม_รอ_ไม่ส่งรูป_กรณีอื่นจบตามเหตุ()
    {
        Assert.Equal(PartnerSlipCopyRule.Outcome.Wait, PartnerSlipCopyRule.Decide(Slip() with { ConsentOk = false }).Outcome);
        Assert.Equal(PartnerSlipCopyRule.Outcome.Wait, PartnerSlipCopyRule.Decide(Slip() with { IntentSucceeded = false }).Outcome);
        Assert.Equal(PartnerSlipCopyRule.Outcome.Done, PartnerSlipCopyRule.Decide(Slip() with { NoticeCreatedAt = D.AddHours(-1) }).Outcome);   // ก่อนยินยอม — ไม่ย้อน
        Assert.Equal(PartnerSlipCopyRule.Outcome.Done, PartnerSlipCopyRule.Decide(Slip() with { IntentIsSlip = false }).Outcome);
        Assert.Equal(PartnerSlipCopyRule.Outcome.Done, PartnerSlipCopyRule.Decide(Slip() with { SlipAttachmentId = null }).Outcome);   // รุ่นเก่า — ไม่เดาไฟล์
        Assert.Equal(PartnerSlipCopyRule.Outcome.Done, PartnerSlipCopyRule.Decide(Slip() with { AttachmentFound = false }).Outcome);
        Assert.Equal(PartnerSlipCopyRule.Outcome.Done, PartnerSlipCopyRule.Decide(Slip() with { AttachmentChannel = PartnerSlipCopyRule.OwnChannel }).Outcome);
        Assert.Equal(Accounting.Services.Implementations.SlipEvidenceService.ChannelPartnerNextAcc, PartnerSlipCopyRule.OwnChannel);
    }

    // ═══ ขอบเขตต่อสายแล้ว + ข้อความภาษาผู้ใช้ ═══

    [Fact]
    public void ขอบเขตทุกตัวของหน้า_ต่อสายแล้ว_ไม่มีป้ายยังไม่มีผล()
    {
        foreach (var d in PartnerConsent.Scopes)
            Assert.True(d.ReaderWired, d.Scope + " ยังไม่ต่อสาย");
        Assert.Equal(PartnerConsent.AutoApprovePurchaseWiredSince, PartnerConsent.Def(Scope.AutoApprovePurchase).WiredSince);
        Assert.Equal(PartnerConsent.ReceiveSlipCopyWiredSince, PartnerConsent.Def(Scope.ReceiveSlipCopy).WiredSince);
    }

    [Fact]
    public void ข้อความบนหน้า_ไม่มีเลขรอบ_คำตัดสิน_ชื่อแพ็ก_และบอกทำอะไร_เมื่อไร_หยุดเมื่อไร()
    {
        var banned = new[] { "รอบ 2", "คำตัดสิน", "แพ็ก", "ฝ่ายค้าน", "hash", "ReaderWired", "Suspended", "Accepted" };
        foreach (var d in PartnerConsent.Scopes)
        {
            var e = PartnerConsent.ExplainOf(d.Scope);
            Assert.False(string.IsNullOrWhiteSpace(e.What));
            Assert.False(string.IsNullOrWhiteSpace(e.When));
            Assert.False(string.IsNullOrWhiteSpace(e.Stops));
            foreach (var text in new[] { d.Label, e.What, e.When, e.Stops, PartnerConsent.EffectNote(d) ?? "" })
                foreach (var b in banned) Assert.DoesNotContain(b, text);
        }
        // AutoRecordPayment: บันทึกเองเมื่อรู้บัญชีจากรายการเดินบัญชี/ผู้ซื้อเลือกตอนจ่าย · บัญชีตั้งต้น = ค่าเลือกไว้ให้
        var pay = PartnerConsent.ExplainOf(Scope.AutoRecordPayment).When;
        Assert.Contains("รายการเดินบัญชี", pay);
        Assert.Contains("ค่าเลือกไว้ให้", pay);
        Assert.DoesNotContain("คำตัดสิน", PartnerPaySourceAccount.ConsentDefaultNotAutoNote);
    }

    private static readonly Guid B = Guid.Parse("00000000-0000-0000-0000-0000000000b0");
    private static readonly Guid A = Guid.Parse("00000000-0000-0000-0000-0000000000a0");
    private static readonly Guid Pid = Guid.Parse("11111111-1111-1111-1111-111111111111");

    [Fact]
    public void ป้ายสถานะ_ยังไม่ได้ยินยอม_แทนไม่ทำงาน_และสถานะการเชื่อมเป็นภาษาไทย()
    {
        var p = new TradingPartnership { Id = Pid, CompanyAId = A, CompanyBId = B, Status = TradingPartnershipStatus.Accepted };
        var d = PartnerConsent.Def(Scope.AutoApprovePurchase);
        var none = PartnerConsent.Allows(Scope.AutoApprovePurchase, null, p, B, PartnerConsent.CapAmount.Thb(1m), true);
        Assert.Equal("ยังไม่ได้ยินยอม", PartnerConsent.StatusLabel(d, none));
        Assert.Equal("ใช้อยู่", PartnerConsent.StatusLabel(d, new PartnerConsent.Verdict(true, PartnerConsent.Codes.Ok, "")));
        Assert.Equal("ต้องยืนยันใหม่", PartnerConsent.StatusLabel(d, new PartnerConsent.Verdict(false, PartnerConsent.Codes.GrantedBeforeWired, "")));
        var suspended = PartnerConsent.Allows(Scope.AutoApprovePurchase, null, new TradingPartnership
            { Id = Pid, CompanyAId = A, CompanyBId = B, Status = TradingPartnershipStatus.Suspended }, B, null, true);
        Assert.Contains("ระงับอยู่", suspended.Reason);
        Assert.DoesNotContain("Suspended", suspended.Reason);
    }
}
