using System;
using System.Collections.Generic;
using Accounting.Helpers;
using Accounting.Models.Enums;
using Xunit;

namespace Accounting.Tests;

/// <summary>
/// ฝ่ายค้านรอบ 1–2 (2026-10-14 · PAYMENT_CHANNEL_AUDIT ส่วนที่ 3) — ตัวตัดสิน pure ทั้งสองทิศ:
/// ค่าที่ยกมาจากต้นทาง (บล็อกเฉพาะข้ามบริษัท) · ขาเงินเดิมของการเปลี่ยนแหล่งเงิน (ยอดตาม JE ไม่ใช่ PaidAmount × อัตรา) ·
/// ผังที่เติมให้ได้ (เปิดใช้ + ไม่ใช่บัญชีกลุ่ม) · ยอดการ์ดธนาคารหลังลบ (ไม่แต่ง 0) · ที่พักโอน/พร้อมเพย์ต้องมีบัญชีธนาคาร ·
/// ข้อความ "ขั้นหลังสร้างเอกสารล้ม" ห้ามชวนกดซ้ำ · คำขอรูปเดิมที่ส่งสองช่อง (ผังตรงชนะ)
/// </summary>
public class AdversarialRound1Tests
{
    private static readonly Guid A = Guid.NewGuid();
    private static readonly Guid B = Guid.NewGuid();

    [Fact]
    public void ค่าที่ยกมา_เทียบกับต้นทางตรงตัว_ค่าว่างไม่นับ()
    {
        Assert.True(DocumentMoneyChannel.IsCarried(A, A));
        Assert.False(DocumentMoneyChannel.IsCarried(A, B));        // ผู้ใช้เปลี่ยน ⇒ ตรวจแบบเพิ่งเลือก
        Assert.False(DocumentMoneyChannel.IsCarried(A, null));
        Assert.False(DocumentMoneyChannel.IsCarried(null, null));
        Assert.False(DocumentMoneyChannel.IsCarried(Guid.Empty, Guid.Empty));
    }

    [Fact]
    public void ค่าที่ยกมา_บล็อกเฉพาะรหัสที่ไม่ใช่ของบริษัทนี้_บอกทางไปต่อ_ไม่มีGUID()
    {
        Assert.Null(DocumentMoneyChannel.CarriedProblem(ownedByCompany: true, "แหล่งเงิน (บัญชีธนาคาร)"));
        var m = DocumentMoneyChannel.CarriedProblem(ownedByCompany: false, "แหล่งเงิน (บัญชีธนาคาร)");
        Assert.NotNull(m);
        Assert.Contains("ไม่ใช่ของบริษัทนี้", m);
        Assert.Contains("“แหล่งเงิน (บัญชีธนาคาร)”", m);
        Assert.Contains("สร้างเอกสารใหม่", m);
    }

    [Fact]
    public void เปลี่ยนแหล่งเงิน_ใช้ยอดตามJEของผังหัวเอกสาร_แม้ไม่เท่าPaidAmount()
    {
        // ใบที่หักมัดจำ: PaidAmount×อัตรา = 1,000 แต่ขาเงินจริงใน JE = 600 (ส่วนที่เหลือตัดด้วยมัดจำ) — เดิมบล็อก
        var leg = DocumentMoneyChannel.PickReclassifySource(A,
            new List<(Guid, decimal)> { (A, 600m), (B, 400m) }, expectedAmount: 1_000m);
        Assert.Equal(A, leg.AccountId);
        Assert.Equal(600m, leg.Amount);
    }

    [Fact]
    public void เปลี่ยนแหล่งเงิน_ผังหัวเอกสารไม่มีเงินไหลผ่าน_ใช้ผังเดียวที่ยอดตรง_กำกวมไม่เดา()
    {
        // ใบเก่าที่ตกเงินสดเงียบ ๆ: หัวเอกสารชี้ธนาคาร (A) แต่ JE ลงเงินสด (B)
        var leg = DocumentMoneyChannel.PickReclassifySource(A, new List<(Guid, decimal)> { (B, 1_000m) }, 1_000m);
        Assert.Equal(B, leg.AccountId);
        Assert.Equal(1_000m, leg.Amount);
        // ทิศตรงข้าม: สองผังยอดเท่ากัน ⇒ ไม่เดา
        var ambiguous = DocumentMoneyChannel.PickReclassifySource(null,
            new List<(Guid, decimal)> { (A, 1_000m), (B, 1_000m) }, 1_000m);
        Assert.Null(ambiguous.AccountId);
        // ผังหัวเอกสารถูกย้ายออกหมดแล้ว (ยอดสุทธิ 0) ⇒ ไม่ใช้ผังนั้น
        Assert.Null(DocumentMoneyChannel.PickReclassifySource(A, new List<(Guid, decimal)> { (A, 0m) }, 1_000m).AccountId);
    }

    [Fact]
    public void ผังที่เติมให้ได้_เปิดใช้และไม่ใช่บัญชีกลุ่ม()
    {
        // รอบ 206: ตัดสินด้วย PostingAccountRule (มีบัญชีย่อย) — ไม่ใช่ระดับผัง
        MoneyGlFacts G(bool active, bool group) => new(Guid.NewGuid(), "53110", "ค่าเครื่องเขียน", active, group);
        Assert.True(DocumentMoneyChannel.IsPostableGl(G(true, false)));
        Assert.False(DocumentMoneyChannel.IsPostableGl(G(false, false)));   // ปิดใช้ ⇒ อนุมัติล้ม (กฎเหล็ก #3)
        Assert.False(DocumentMoneyChannel.IsPostableGl(G(true, true)));     // บัญชีคุม (มีบัญชีย่อย)
    }

    [Fact]
    public void ยอดการ์ดธนาคารหลังลบ_บรรทัดที่รู้ยอด_แล้วยอดตามธนาคาร_แล้วคงเดิม_ไม่แต่ง0()
    {
        Assert.Equal(53_000m, BankBalanceAfterDelete.Resolve(53_000m, 52_500m, 10m));
        Assert.Equal(52_500m, BankBalanceAfterDelete.Resolve(null, 52_500m, 10m));
        Assert.Equal(50_000m, BankBalanceAfterDelete.Resolve(null, null, 50_000m));   // เดิม: 0
        Assert.Equal(0m, BankBalanceAfterDelete.Resolve(0m, 52_500m, 10m));           // ยอดจริงที่เป็น 0 ใช้ได้
    }

    [Theory]
    [InlineData(PaymentMethod.BankTransfer, 1000, false, true)]
    [InlineData(PaymentMethod.PromptPay, 1000, false, true)]
    [InlineData(PaymentMethod.BankTransfer, 1000, true, false)]
    [InlineData(PaymentMethod.Cash, 1000, false, false)]
    [InlineData(PaymentMethod.CreditCard, 1000, false, false)]
    [InlineData(PaymentMethod.EWallet, 1000, false, false)]
    [InlineData(PaymentMethod.BankTransfer, 0, false, false)]   // ไม่มีเงินเข้า ⇒ ไม่บังคับ
    public void ที่พัก_โอนหรือพร้อมเพย์ต้องมีบัญชีธนาคาร(PaymentMethod method, int amount, bool hasBank, bool blocked)
    {
        var p = LodgingPaymentBank.Problem(method, amount, hasBank ? (Guid?)A : null);
        Assert.Equal(blocked, p != null);
        if (blocked) Assert.Contains("รับเข้าบัญชีธนาคาร", p);
    }

    [Fact]
    public void ขั้นหลังสร้างเอกสารล้ม_ข้อความบอกว่านับรอบแล้ว_ไม่ชวนกดสร้างทันทีซ้ำ_ไม่หลุดข้อความเทคนิค()
    {
        var biz = RecurringRunFailure.AfterCreateNotice("INV-0001", new BusinessRuleException("ไม่พบบัญชีรายได้", "X"));
        Assert.Contains("INV-0001", biz);
        Assert.Contains("ไม่ต้องกด", biz);
        Assert.Contains("ไม่พบบัญชีรายได้", biz);
        var tech = RecurringRunFailure.AfterCreateNotice("INV-0002", new InvalidOperationException("The connection is already in a transaction"));
        Assert.DoesNotContain("connection", tech);
        Assert.Contains("ไม่ต้องกด", tech);
        // ทิศตรงข้าม: รอบที่ล้มจริง (ยังไม่สร้างเอกสาร) ยังชวนกดสร้างทันทีได้ตามเดิม
        Assert.Contains("ลองใหม่", RecurringRunFailure.UserMessage(new InvalidOperationException("The connection is already in a transaction")));
    }

    [Fact]
    public void คำขอรูปเดิมส่งสองช่อง_ผังตรงชนะ_ไม่ปฏิเสธ()
    {
        Assert.Equal(((Guid?)null, (Guid?)B), PickedAccountGuard.PreferDirectAccount(A, B));
        Assert.Equal(((Guid?)A, (Guid?)null), PickedAccountGuard.PreferDirectAccount(A, null));
        Assert.Equal(((Guid?)null, (Guid?)null), PickedAccountGuard.PreferDirectAccount(Guid.Empty, null));
    }
}
