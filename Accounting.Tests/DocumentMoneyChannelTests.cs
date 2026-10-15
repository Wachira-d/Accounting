using Accounting.Helpers;
using Xunit;

namespace Accounting.Tests;

/// <summary>
/// 2026-10-14 ทีม F2 (PAYMENT_CHANNEL_AUDIT PC#4/#5/#15/#21 · ID#7) — ตัวตัดสินเดียว "แหล่งเงิน → ผังบัญชี" (<see cref="DocumentMoneyChannel"/>):
/// ลำดับ (ผังตรง &gt; ธนาคาร) · แหล่งเงินที่เลือกแล้วใช้ไม่ได้ ⇒ ข้อความไทยบอกช่อง + ที่แก้ (ไม่ตกเงินสด) · การชำระสืบทอดแหล่งเงินของเอกสาร
/// ทั้งสองแบบ · เลือกผังตรง ⇒ ไม่ขยับยอดธนาคาร · ทิศตรงข้าม: ค่าที่ถูกผ่านเหมือนเดิมทุกตัว
/// </summary>
public class DocumentMoneyChannelTests
{
    private static readonly Guid Bank = Guid.Parse("11111111-1111-1111-1111-111111111111");
    private static readonly Guid Gl = Guid.Parse("22222222-2222-2222-2222-222222222222");
    private static readonly Guid BankGl = Guid.Parse("33333333-3333-3333-3333-333333333333");
    private const string Fix = "แก้ที่เอกสาร";

    /// <summary>รอบ 206: ข้อเท็จจริงเป็น "มีบัญชีย่อย" (<paramref name="group"/>) ตามเจ้าของกติกา PostingAccountRule — ไม่ใช่ระดับผัง</summary>
    private static MoneyGlFacts GlFacts(bool active = true, bool group = false, Guid? id = null, bool linkedByBank = false)
        => new(id ?? Gl, "21230", "เงินกู้ยืมกรรมการ", active, group, linkedByBank);

    private static MoneyBankFacts BankFacts(bool active = true, Guid? linked = null, MoneyGlFacts? linkedFacts = null)
        => new(Bank, "บัญชีออมทรัพย์ (กสิกร 123)", active, linked, linkedFacts);

    // ── ลำดับเดียวของระบบ ──

    [Fact]
    public void ลำดับ_ผังตรงชนะธนาคาร_และไม่เลือก_คือ_None()
    {
        Assert.Equal(MoneyChannelKind.GlAccount, DocumentMoneyChannel.Pick(Bank, Gl));
        Assert.Equal(MoneyChannelKind.BankAccount, DocumentMoneyChannel.Pick(Bank, null));
        Assert.Equal(MoneyChannelKind.None, DocumentMoneyChannel.Pick(null, null));
        // Guid.Empty = ไม่เลือก (ฟอร์มส่งมาเพื่อปลดค่า)
        Assert.Equal(MoneyChannelKind.BankAccount, DocumentMoneyChannel.Pick(Bank, Guid.Empty));
        Assert.Equal(MoneyChannelKind.None, DocumentMoneyChannel.Pick(Guid.Empty, Guid.Empty));
    }

    [Fact]
    public void ค่าช่องฟอร์ม_ตามลำดับเดียวกับการลงบัญชี_PC15()
    {
        // ใบที่มีทั้งสองค่า (เช่นมัดจำที่พักรับผ่าน gateway) — ฟอร์มต้องโชว์ผังตรง (ตัวที่ JE ลงจริง) ไม่ใช่ธนาคาร
        Assert.Equal("account:" + Gl, DocumentMoneyChannel.ChannelValue(Bank, Gl));
        Assert.Equal("bank:" + Bank, DocumentMoneyChannel.ChannelValue(Bank, null));
        Assert.Null(DocumentMoneyChannel.ChannelValue(null, null));
    }

    // ── การชำระสืบทอดแหล่งเงิน (PC#5 · PC#21) ──

    [Fact]
    public void ชำระโดยไม่เลือก_สืบทอดผังตรงของเอกสาร_ไม่ใช่เงินสด_PC5()
    {
        var ch = DocumentMoneyChannel.ForPayment(null, null, docBankAccountId: null, docPaymentAccountId: Gl);
        Assert.Equal(new PaymentMoneyChannel(null, Gl), ch);
    }

    [Fact]
    public void ชำระโดยไม่เลือก_สืบทอดธนาคารของเอกสาร_เหมือนเดิม()
    {
        Assert.Equal(new PaymentMoneyChannel(Bank, null), DocumentMoneyChannel.ForPayment(null, null, Bank, null));
        Assert.Equal(new PaymentMoneyChannel(null, null), DocumentMoneyChannel.ForPayment(null, null, null, null));
    }

    [Fact]
    public void เลือกจ่ายจากผังอื่น_ไม่ขยับยอดธนาคารของเอกสาร_PC21()
    {
        var ch = DocumentMoneyChannel.ForPayment(null, Gl, docBankAccountId: Bank, docPaymentAccountId: null);
        Assert.Null(ch.BankAccountId);
        Assert.Equal(Gl, ch.PaymentAccountId);
    }

    [Fact]
    public void เลือกธนาคารอื่นในหน้าชำระ_ชนะแหล่งเงินของเอกสาร()
    {
        var other = Guid.NewGuid();
        Assert.Equal(new PaymentMoneyChannel(other, null), DocumentMoneyChannel.ForPayment(other, null, Bank, Gl));
    }

    [Fact]
    public void เลือกสองทางพร้อมกัน_ถูกจับได้()
    {
        Assert.True(DocumentMoneyChannel.HasBothOverrides(Bank, Gl));
        Assert.False(DocumentMoneyChannel.HasBothOverrides(Bank, null));
        Assert.False(DocumentMoneyChannel.HasBothOverrides(Bank, Guid.Empty));
        Assert.Contains("เลือกได้ทางเดียว", DocumentMoneyChannel.BothOverridesMessage);
    }

    // ── ตัวตัดสิน: ใช้ไม่ได้ ⇒ ข้อความไทย · ใช้ได้ ⇒ ผังที่ถูก ──

    [Fact]
    public void ธนาคารยังไม่ผูกผัง_ปฏิเสธพร้อมทางไปต่อ_ไม่ตกเงินสด()
    {
        var d = DocumentMoneyChannel.Decide(MoneyChannelKind.BankAccount, null, false, BankFacts(linked: null), false,
            MoneyChannelCheck.Post, Fix);
        Assert.False(d.Ok);
        Assert.Null(d.GlAccountId);
        Assert.Contains("ยังไม่ได้ผูกผังบัญชี", d.Problem);
        Assert.Contains("หน้า “บัญชีธนาคาร”", d.Problem);
        Assert.Contains("กสิกร", d.Problem);
        Assert.DoesNotContain(Bank.ToString(), d.Problem);   // ห้ามโชว์รหัสดิบ
    }

    [Fact]
    public void ธนาคารผูกผังแล้ว_ได้ผังที่ผูก()
    {
        var d = DocumentMoneyChannel.Decide(MoneyChannelKind.BankAccount, null, false,
            BankFacts(linked: BankGl, linkedFacts: GlFacts(id: BankGl)), false, MoneyChannelCheck.Post, Fix);
        Assert.True(d.Ok);
        Assert.Equal(BankGl, d.GlAccountId);
    }

    [Fact]
    public void ธนาคารปิดใช้_ห้ามเลือกใหม่ตอนบันทึก_แต่ใบเดิมยังลงบัญชีได้()
    {
        var bank = BankFacts(active: false, linked: BankGl, linkedFacts: GlFacts(id: BankGl));
        var save = DocumentMoneyChannel.Decide(MoneyChannelKind.BankAccount, null, false, bank, false, MoneyChannelCheck.Save, Fix);
        Assert.False(save.Ok);
        Assert.Contains("ถูกปิดใช้งาน", save.Problem);
        var post = DocumentMoneyChannel.Decide(MoneyChannelKind.BankAccount, null, false, bank, false, MoneyChannelCheck.Post, Fix);
        Assert.True(post.Ok);
        Assert.Equal(BankGl, post.GlAccountId);
    }

    [Fact]
    public void ผังที่ธนาคารผูกไว้ใช้ไม่ได้_ปฏิเสธ()
    {
        Assert.Contains("ไม่มีในบริษัทนี้แล้ว", DocumentMoneyChannel.Decide(MoneyChannelKind.BankAccount, null, false,
            BankFacts(linked: BankGl, linkedFacts: null), false, MoneyChannelCheck.Post, Fix).Problem);
        Assert.Contains("ถูกปิดใช้งาน", DocumentMoneyChannel.Decide(MoneyChannelKind.BankAccount, null, false,
            BankFacts(linked: BankGl, linkedFacts: GlFacts(active: false, id: BankGl)), false, MoneyChannelCheck.Post, Fix).Problem);
    }

    [Fact]
    public void รอบ206_ผังที่ธนาคารผูกเป็นบัญชีคุม_ยอมได้เหมือนทางรับจ่ายชำระ()
    {
        // เดิม "ผูกกับผังกลุ่ม" ปฏิเสธที่นี่ ขณะที่ MoneyAccountDirectory.TryResolveBankGlAsync (ทางรับ/จ่ายชำระ) ยอม ⇒ ธนาคารเดียวกันลงได้ทางหนึ่งไม่ได้อีกทาง
        var d = DocumentMoneyChannel.Decide(MoneyChannelKind.BankAccount, null, false,
            BankFacts(linked: BankGl, linkedFacts: GlFacts(group: true, id: BankGl)), false, MoneyChannelCheck.Post, Fix);
        Assert.True(d.Ok);
        Assert.Equal(BankGl, d.GlAccountId);
        // ทิศตรงข้าม: ผังที่ผูกปิดใช้ ยังปฏิเสธ
        Assert.Contains("ถูกปิดใช้งาน", DocumentMoneyChannel.Decide(MoneyChannelKind.BankAccount, null, false,
            BankFacts(linked: BankGl, linkedFacts: GlFacts(active: false, group: true, id: BankGl)), false, MoneyChannelCheck.Post, Fix).Problem);
    }

    [Fact]
    public void รหัสสลับตาราง_บอกช่องที่ผิด_ID7()
    {
        // รหัสบัญชีธนาคารมาอยู่ในช่องผังบัญชี
        var swappedGl = DocumentMoneyChannel.Decide(MoneyChannelKind.GlAccount, null, glIdIsBank: true, null, false, MoneyChannelCheck.Save, Fix);
        Assert.Contains("ได้รหัสบัญชีธนาคารมาแทนรหัสผังบัญชี", swappedGl.Problem);
        // รหัสผังบัญชีมาอยู่ในช่องบัญชีธนาคาร (บั๊กคลาสเดียวกับรายการประจำ)
        var swappedBank = DocumentMoneyChannel.Decide(MoneyChannelKind.BankAccount, null, false, null, bankIdIsGl: true, MoneyChannelCheck.Save, Fix);
        Assert.Contains("ได้รหัสผังบัญชีมาแทนรหัสบัญชีธนาคาร", swappedBank.Problem);
        // ไม่มีในบริษัทนี้ (ข้ามบริษัท)
        Assert.Contains("ไม่มีในบริษัทนี้", DocumentMoneyChannel.Decide(MoneyChannelKind.GlAccount, null, false, null, false, MoneyChannelCheck.Save, Fix).Problem);
        Assert.Contains("ไม่มีในบริษัทนี้", DocumentMoneyChannel.Decide(MoneyChannelKind.BankAccount, null, false, null, false, MoneyChannelCheck.Save, Fix).Problem);
    }

    [Fact]
    public void ผังตรง_ใช้ได้_ปิดใช้_บัญชีกลุ่ม()
    {
        var ok = DocumentMoneyChannel.Decide(MoneyChannelKind.GlAccount, GlFacts(), false, null, false, MoneyChannelCheck.Save, Fix);
        Assert.True(ok.Ok);
        Assert.Equal(Gl, ok.GlAccountId);
        Assert.Contains("ถูกปิดใช้งาน", DocumentMoneyChannel.Decide(MoneyChannelKind.GlAccount, GlFacts(active: false), false, null, false, MoneyChannelCheck.Post, Fix).Problem);
        Assert.Contains("บัญชีคุม (มีบัญชีย่อย)", DocumentMoneyChannel.Decide(MoneyChannelKind.GlAccount, GlFacts(group: true), false, null, false, MoneyChannelCheck.Post, Fix).Problem);
    }

    [Fact]
    public void รอบ206_บัญชีคุมตัดสินจากบัญชีย่อย_ไม่ใช่ระดับ_สองทิศ()
    {
        // ผังของบริษัทเองระดับ 2–3 ที่ไม่มีบัญชีย่อย = ลงรายการได้ (เดิม Level < 4 ปฏิเสธ) — facts ไม่มีระดับให้ตัดสินอีกแล้ว
        var leaf = DocumentMoneyChannel.Decide(MoneyChannelKind.GlAccount, GlFacts(group: false), false, null, false, MoneyChannelCheck.Post, Fix);
        Assert.True(leaf.Ok);
        Assert.True(DocumentMoneyChannel.IsPostableGl(GlFacts(group: false)));
        Assert.Null(DocumentMoneyChannel.ExpenseCategoryProblem(GlFacts(group: false), false));
        // ผังระดับ 4 ที่มีบัญชีย่อย (11122 + ผังเงินฝากย่อย) = บัญชีคุม — ทุกทางปฏิเสธเหมือน PostingAccountRule
        Assert.False(DocumentMoneyChannel.Decide(MoneyChannelKind.GlAccount, GlFacts(group: true), false, null, false, MoneyChannelCheck.Save, Fix).Ok);
        Assert.False(DocumentMoneyChannel.IsPostableGl(GlFacts(group: true)));
        Assert.Equal(PostingAccountProblem.Group, PostingAccountRule.Check(GlFacts(group: true).Posting));
        // ผังตรงที่บัญชีธนาคารเปิดใช้ผูกอยู่ ยอมเป็นบัญชีคุม (ตัวเดียวกับ MoneyAccountDirectory.SourceProblem)
        Assert.True(DocumentMoneyChannel.Decide(MoneyChannelKind.GlAccount, GlFacts(group: true, linkedByBank: true), false, null, false,
            MoneyChannelCheck.Post, Fix).Ok);
        // ตัวเติมค่า: null = ไม่มีในบริษัท ⇒ ไม่เติม
        Assert.False(DocumentMoneyChannel.IsPostableGl(null));
    }

    [Fact]
    public void ไม่ได้เลือกแหล่งเงิน_ผ่าน_ผู้เรียกใช้เงินสดตั้งต้น()
    {
        var d = DocumentMoneyChannel.Decide(MoneyChannelKind.None, null, false, null, false, MoneyChannelCheck.Post, Fix);
        Assert.True(d.Ok);
        Assert.Null(d.GlAccountId);
        Assert.Contains("11111", DocumentMoneyChannel.NoDefaultCashMessage(Fix));
    }

    [Fact]
    public void หมวดค่าใช้จ่าย_ตรวจตารางและสถานะ()
    {
        Assert.Null(DocumentMoneyChannel.ExpenseCategoryProblem(GlFacts(), false));
        Assert.Contains("ได้รหัสบัญชีธนาคาร", DocumentMoneyChannel.ExpenseCategoryProblem(null, idIsBank: true));
        Assert.Contains("ไม่มีในบริษัทนี้", DocumentMoneyChannel.ExpenseCategoryProblem(null, false));
        Assert.Contains("ถูกปิดใช้งาน", DocumentMoneyChannel.ExpenseCategoryProblem(GlFacts(active: false), false));
        Assert.Contains("บัญชีคุม", DocumentMoneyChannel.ExpenseCategoryProblem(GlFacts(group: true), false));
        // หมวดค่าใช้จ่ายไม่ใช่ที่อยู่ของเงิน — ผูกธนาคารก็ไม่ปลดข้อบัญชีคุม
        Assert.Contains("บัญชีคุม", DocumentMoneyChannel.ExpenseCategoryProblem(GlFacts(group: true, linkedByBank: true), false));
    }
}
