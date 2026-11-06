using Accounting.Helpers;
using Accounting.Models.Entities;
using Accounting.Models.Enums;
using Accounting.Services.Implementations;
using Xunit;

namespace Accounting.Tests;

/// <summary>
/// รอบ 244 — e-Tax by Email (erp-review/2026-11-06/ETAX_BY_EMAIL_AUDIT.md) · ตัวตัดสิน pure สามตัว:
/// <see cref="EtaxByEmailFormat"/> (หัวเรื่อง/ผู้รับ/ไฟล์แนบ/ที่อยู่ประทับเวลา) · <see cref="EtaxByEmailSenderGate"/> (ส่งจากบริษัทที่ทดสอบผ่าน + ตรงอีเมลที่ลงทะเบียน) ·
/// <see cref="EtaxDeliveryRouter"/> (ทุกทางส่งเอกสารเลือกช่องทางตัวเดียว) — ทุกกลุ่มมีทิศตรงข้าม (ใบที่ถูกอยู่แล้ว/บริษัทที่ไม่ใช้ by Email ไม่ถูกแตะ)
/// </summary>
public class EtaxByEmailRound244Tests
{
    // วันที่เอกสารเก็บเป็น "วันที่ไทย ณ 00:00 UTC" (ThaiDate.CalendarDateUtc)
    private static readonly DateTime Jan18_2017 = new(2017, 1, 18, 0, 0, 0, DateTimeKind.Utc);

    // ═════════════ หัวเรื่อง (R6) ═════════════

    [Fact]
    public void หัวเรื่องใบกำกับ_ตรงตัวอย่างคู่มือ_วันที่พศ_วงเล็บติดกัน()
    {
        var r = EtaxByEmailFormat.Subject(Doc(DocumentType.TaxInvoice, "IV2560-0001"));
        Assert.Null(r.Problem);
        Assert.Equal("[18012560][INV][IV2560-0001]", r.Subject);
    }

    private static Document Doc(DocumentType t, string no, bool cashReceipt = false, bool combined = false) => new()
    {
        DocumentType = t, DocumentNumber = no, DocumentDate = Jan18_2017, IssuedAsCashReceipt = cashReceipt, CombinedInvoiceTaxInvoice = combined,
    };

    // ฝ่ายค้าน S3: รหัสหัวเรื่องจาก EtaxDocumentTypeMap ตัวเดียวกับ XML — ใบกำกับที่ออกเป็นใบเสร็จในตัว (T03) กับใบเสร็จ (T03) ได้รหัสเดียวกัน
    [Fact]
    public void หัวเรื่อง_รหัสจากEtaxDocumentTypeMap_ใบกำกับออกเป็นใบเสร็จในตัวกับใบเสร็จได้RCTเหมือนกัน()
    {
        var cash = Doc(DocumentType.TaxInvoice, "IV-1", cashReceipt: true);
        var rec = Doc(DocumentType.Receipt, "RE-1");
        Assert.Equal(EtaxDocumentTypeMap.TypeCode(cash), EtaxDocumentTypeMap.TypeCode(rec));
        Assert.Equal("[18012560][RCT][IV-1]", EtaxByEmailFormat.Subject(cash).Subject);
        Assert.Equal("[18012560][RCT][RE-1]", EtaxByEmailFormat.Subject(rec).Subject);
        // ทิศตรงข้าม: ใบแจ้งหนี้/ใบกำกับ (T02) และใบกำกับปกติ (388) = INV · อย่างย่อ (T05) ยังไม่รองรับ
        Assert.Equal("[18012560][INV][IV-2]", EtaxByEmailFormat.Subject(Doc(DocumentType.TaxInvoice, "IV-2", combined: true)).Subject);
        Assert.NotNull(EtaxByEmailFormat.Subject("T05", Jan18_2017, "AB-1").Problem);
    }

    [Fact]
    public void หัวเรื่อง_วันที่ตามปฏิทินไทย_ใกล้เที่ยงคืน_UTC()
    {
        // 31/12/2025 17:30 UTC = 01/01/2026 00:30 เวลาไทย ⇒ 01012569
        var lateUtc = new DateTime(2025, 12, 31, 17, 30, 0, DateTimeKind.Utc);
        Assert.Equal("01012569", EtaxByEmailFormat.BeDate(lateUtc));
        // 31/12/2025 16:59 UTC = 31/12/2025 23:59 เวลาไทย ⇒ 31122568
        Assert.Equal("31122568", EtaxByEmailFormat.BeDate(new DateTime(2025, 12, 31, 16, 59, 0, DateTimeKind.Utc)));
    }

    [Fact]
    public void หัวเรื่องใบเพิ่มหนี้_ใบลดหนี้_ต้องมีเลขใบเดิม()
    {
        Assert.Equal("[18012560][DBN][DN-0001][IV-0009]",
            EtaxByEmailFormat.Subject(Doc(DocumentType.DebitNote, "DN-0001"), originalNumber: "IV-0009").Subject);
        Assert.Equal("[18012560][CDN][CN-0001][IV-0009]",
            EtaxByEmailFormat.Subject(Doc(DocumentType.CreditNote, "CN-0001"), originalNumber: "IV-0009").Subject);
        var missing = EtaxByEmailFormat.Subject(Doc(DocumentType.CreditNote, "CN-0001"));
        Assert.Null(missing.Subject);
        Assert.Contains("เลขใบกำกับภาษีเดิม", missing.Problem);
    }

    [Fact]
    public void หัวเรื่องใบแทน_ต่อเลขใบเดิมที่ยกเลิก_ใบเสร็จใช้RCT()
    {
        Assert.Equal("[18012560][INV][IV-0010][IV-0009]",
            EtaxByEmailFormat.Subject(Doc(DocumentType.TaxInvoice, "IV-0010"), replacesNumber: "IV-0009").Subject);
        Assert.Equal("[18012560][RCT][RE-0001]", EtaxByEmailFormat.Subject(Doc(DocumentType.Receipt, "RE-0001")).Subject);
    }

    [Theory]
    [InlineData(DocumentType.Quotation)]
    [InlineData(DocumentType.Invoice)]
    [InlineData(DocumentType.PurchaseOrder)]
    public void หัวเรื่อง_ชนิดที่ไม่ใช่eTax_ปฏิเสธ(DocumentType t)
    {
        var r = EtaxByEmailFormat.Subject(Doc(t, "X-1"));
        Assert.Null(r.Subject);
        Assert.Contains("ไม่ใช่ใบกำกับภาษี", r.Problem);
    }

    [Theory]
    [InlineData("DRAFT-1a2b")]
    [InlineData("IV[1]")]
    [InlineData("")]
    public void หัวเรื่อง_เลขร่าง_วงเล็บ_ว่าง_ปฏิเสธ(string no)
        => Assert.NotNull(EtaxByEmailFormat.Subject(Doc(DocumentType.TaxInvoice, no)).Problem);

    // ═════════════ ผู้รับ (R4) + ที่อยู่ประทับเวลา (R8) ═════════════

    [Fact]
    public void ผู้รับ_To1อีเมล_CCเฉพาะETDA_production()
    {
        var e = EtaxByEmailFormat.Recipients("AP@Buyer.co.th", null, null, true, true, false, useUat: false);
        Assert.Null(e.Problem);
        Assert.Equal("ap@buyer.co.th", e.To);
        Assert.Equal(new[] { "csemail@etax.teda.th" }, e.Cc);
    }

    [Fact]
    public void ผู้รับ_โหมดทดสอบ_CCไปUAT()
    {
        var e = EtaxByEmailFormat.Recipients("ap@buyer.co.th", null, null, true, true, false, useUat: true);
        Assert.Equal(new[] { "csemail@uat.etax.teda.th" }, e.Cc);
        Assert.Equal("csemail@uat.etax.teda.th", EtaxByEmailFormat.TimestampAddress(true));
        Assert.Equal("csemail@etax.teda.th", EtaxByEmailFormat.TimestampAddress(false));
    }

    [Fact]
    public void ผู้รับ_CCที่อยู่ETDAซ้ำ_ตัดซ้ำ_CCอื่น_BCC_หลายTo_ปฏิเสธ()
    {
        var dup = EtaxByEmailFormat.Recipients("ap@buyer.co.th", "CSEMAIL@etax.teda.th", null, true, true, false, false);
        Assert.Null(dup.Problem);
        Assert.Single(dup.Cc);
        Assert.NotNull(EtaxByEmailFormat.Recipients("ap@buyer.co.th", "boss@seller.co.th", null, true, true, false, false).Problem);
        Assert.NotNull(EtaxByEmailFormat.Recipients("ap@buyer.co.th", null, "me@seller.co.th", true, true, false, false).Problem);
        Assert.NotNull(EtaxByEmailFormat.Recipients("a@buyer.co.th, b@buyer.co.th", null, null, true, true, false, false).Problem);
        Assert.NotNull(EtaxByEmailFormat.Recipients("csemail@etax.teda.th", null, null, true, true, false, false).Problem);
        Assert.NotNull(EtaxByEmailFormat.Recipients("not-an-email", null, null, true, true, false, false).Problem);
    }

    [Fact]
    public void ผู้รับ_ไม่CC_แนบXMLแยก_ไม่แนบPDF_ปฏิเสธ()
    {
        Assert.Contains("อีเมลเอกสารธรรมดา", EtaxByEmailFormat.Recipients("ap@buyer.co.th", null, null, false, true, false, false).Problem);
        Assert.Contains("1 ไฟล์", EtaxByEmailFormat.Recipients("ap@buyer.co.th", null, null, true, true, true, false).Problem);
        Assert.NotNull(EtaxByEmailFormat.Recipients("ap@buyer.co.th", null, null, true, false, false, false).Problem);
    }

    // ═════════════ ไฟล์แนบ (R5) ═════════════

    [Fact]
    public void ไฟล์แนบ_1ไฟล์_ไม่เกิน3MB()
    {
        Assert.Null(EtaxByEmailFormat.AttachmentProblem(1, EtaxByEmailFormat.MaxAttachmentBytes));
        Assert.Contains("3 MB", EtaxByEmailFormat.AttachmentProblem(1, EtaxByEmailFormat.MaxAttachmentBytes + 1));
        Assert.NotNull(EtaxByEmailFormat.AttachmentProblem(2, 1000));
        Assert.NotNull(EtaxByEmailFormat.AttachmentProblem(1, 0));
    }

    // ═════════════ ขั้น + คำเตือน ═════════════

    [Fact]
    public void ขั้น_ส่งแล้วรอประทับ_แยกจากประทับแล้ว()
    {
        var t = new DateTime(2026, 11, 6, 3, 0, 0, DateTimeKind.Utc);
        Assert.Equal(EtaxByEmailStage.NotSent, EtaxByEmailFormat.Stage(null, null));
        Assert.Equal(EtaxByEmailStage.SentAwaitingStamp, EtaxByEmailFormat.Stage(t, null));
        Assert.Equal(EtaxByEmailStage.Stamped, EtaxByEmailFormat.Stage(t, t.AddMinutes(3)));
        Assert.Contains("รอประทับเวลา", EtaxByEmailFormat.StageLabel(EtaxByEmailStage.SentAwaitingStamp));
    }

    [Fact]
    public void คำเตือนรายได้เกิน30ล้าน_ไม่มีข้อมูลไม่เตือน()
    {
        Assert.NotNull(EtaxByEmailFormat.RevenueWarning(30_000_000.01m, 2025));
        Assert.Null(EtaxByEmailFormat.RevenueWarning(30_000_000m, 2025));
        Assert.Null(EtaxByEmailFormat.RevenueWarning(null));
        Assert.NotNull(EtaxByEmailFormat.ModeWarning(EtaxMode.Both));
        Assert.Null(EtaxByEmailFormat.ModeWarning(EtaxMode.ByEmail));
    }

    // ═════════════ ด่านผู้ส่ง ═════════════

    private static EtaxByEmailSenderGate.Input Ok() =>
        new(EtaxMode.ByEmail, RdRegistered: true, RegisteredSenderEmail: "etax@seller.co.th", HasTenantSender: true, EmailConfigured: true,
            ActualMailbox: "ETAX@Seller.co.th");

    [Fact]
    public void ด่านผู้ส่ง_ตั้งครบ_ทดสอบผ่าน_ที่อยู่ตรง_ผ่าน()
        => Assert.Null(EtaxByEmailSenderGate.Refusal(Ok()));

    [Fact]
    public void ด่านผู้ส่ง_SMTPยังไม่ทดสอบ_ปฏิเสธพร้อมคำแนะนำ_ไม่ถอยไปแพลตฟอร์ม()
    {
        var r = EtaxByEmailSenderGate.Refusal(Ok() with { EmailConfigured = false });
        Assert.Contains("ทดสอบ", r);
        Assert.Contains("ไม่ส่งแทนจากอีเมลของ NextAcc", r);
        Assert.Contains("ตั้งค่า > อีเมล", EtaxByEmailSenderGate.Refusal(Ok() with { HasTenantSender = false }));
    }

    [Fact]
    public void ด่านผู้ส่ง_กล่องจริงไม่ตรงอีเมลที่ลงทะเบียน_ไม่ลงทะเบียน_โหมดอื่น_ปฏิเสธ()
    {
        Assert.Contains("ไม่ตรง", EtaxByEmailSenderGate.Refusal(Ok() with { ActualMailbox = "sales@seller.co.th" }));
        Assert.NotNull(EtaxByEmailSenderGate.Refusal(Ok() with { ActualMailbox = null }));
        Assert.NotNull(EtaxByEmailSenderGate.Refusal(Ok() with { RdRegistered = false }));
        Assert.NotNull(EtaxByEmailSenderGate.Refusal(Ok() with { RegisteredSenderEmail = " " }));
        Assert.NotNull(EtaxByEmailSenderGate.Refusal(Ok() with { Mode = EtaxMode.Direct }));
        Assert.Null(EtaxByEmailSenderGate.Refusal(Ok() with { Mode = EtaxMode.Both }));   // ค่าเดิม (คำตัดสิน 244-1) ยังส่งได้พร้อมคำเตือน
    }

    [Fact]
    public void ด่านผู้ส่ง_กล่องจริง_Graphใช้UPN_SMTPใช้Fromแล้วชื่อผู้ใช้_ไม่ใช้ค่าสำรองของระบบ()
    {
        static string? Mailbox(CompanySettings s) => EtaxByEmailSenderGate.From(s, hasTenantSender: true).ActualMailbox;
        Assert.Equal("upn@seller.co.th", Mailbox(new CompanySettings
            { EmailProvider = EmailProvider.MicrosoftGraph, EmailMsSenderUpn = "upn@seller.co.th", EmailFromAddress = "other@seller.co.th" }));
        Assert.Equal("from@seller.co.th", Mailbox(new CompanySettings
            { EmailProvider = EmailProvider.Smtp, EmailFromAddress = "from@seller.co.th", EmailSmtpUsername = "user@seller.co.th" }));
        Assert.Equal("user@seller.co.th", Mailbox(new CompanySettings
            { EmailProvider = EmailProvider.Smtp, EmailSmtpUsername = "user@seller.co.th" }));
        Assert.Null(Mailbox(new CompanySettings { EmailProvider = EmailProvider.Smtp }));
    }

    // ═════════════ ทางเลือกช่องทาง ═════════════

    [Fact]
    public void ช่องทาง_บริษัทไม่ใช้ByEmail_อีเมลธรรมดา_ทิศตรงข้าม()
    {
        Assert.Equal(EtaxEmailChannel.PlainEmail, EtaxDeliveryRouter.Decide(true, EtaxMode.Direct, EtaxAutoSkip.None, false).Channel);
        Assert.Equal(EtaxEmailChannel.PlainEmail, EtaxDeliveryRouter.Decide(true, EtaxMode.None, EtaxAutoSkip.None, false).Channel);
        Assert.Equal(EtaxEmailChannel.PlainEmail, EtaxDeliveryRouter.Decide(false, EtaxMode.ByEmail, EtaxAutoSkip.None, false).Channel);
    }

    [Fact]
    public void ช่องทาง_ByEmail_ใบกำกับ_ไปทางeTax_ส่งแล้ว_สำเนาธรรมดา()
    {
        Assert.Equal(EtaxEmailChannel.EtaxByEmail, EtaxDeliveryRouter.Decide(true, EtaxMode.ByEmail, EtaxAutoSkip.None, false).Channel);
        Assert.Equal(EtaxEmailChannel.EtaxByEmail, EtaxDeliveryRouter.Decide(true, EtaxMode.Both, EtaxAutoSkip.None, false).Channel);
        Assert.Equal(EtaxEmailChannel.PlainCopyAfterEtax, EtaxDeliveryRouter.Decide(true, EtaxMode.ByEmail, EtaxAutoSkip.None, true).Channel);
    }

    [Theory]
    [InlineData(EtaxAutoSkip.NotEtaxType)]           // ใบเสนอราคา/ใบแจ้งหนี้
    [InlineData(EtaxAutoSkip.NotFullTaxInvoice)]     // ใบกำกับอย่างย่อ/walk-in
    [InlineData(EtaxAutoSkip.ReceiptSettlesTaxInvoice)]
    [InlineData(EtaxAutoSkip.DeferredDepositReceipt)]
    [InlineData(EtaxAutoSkip.PurchaseSideAdjustment)]
    public void ช่องทาง_ByEmail_ใบที่ไม่ใช่ใบกำกับโดยเจตนา_อีเมลธรรมดา(EtaxAutoSkip skip)
        => Assert.Equal(EtaxEmailChannel.PlainEmail, EtaxDeliveryRouter.Decide(true, EtaxMode.ByEmail, skip, false).Channel);

    [Fact]
    public void ขอบเขต_ใบเสนอราคาและใบกำกับอย่างย่อ_ไม่ใช่eTax()
    {
        Assert.Equal(EtaxAutoSkip.NotEtaxType, EtaxAutoIssueScope.Judge(DocumentType.Quotation, DocumentStatus.Approved, false, false, null, null, null, false));
        Assert.Equal(EtaxAutoSkip.NotFullTaxInvoice, EtaxAutoIssueScope.Judge(DocumentType.Receipt, DocumentStatus.Paid, false, false, null, null, null, notFullTaxInvoice: true));
        Assert.Equal(EtaxAutoSkip.None, EtaxAutoIssueScope.Judge(DocumentType.TaxInvoice, DocumentStatus.Approved, false, false, null, null, true, false));
    }

    // ═════════════ สถานะ ═════════════

    [Fact]
    public void สถานะ_ใบยกเลิก_ร่าง_แถวeTaxยกเลิก_ปฏิเสธ_ใบออกแล้วผ่าน()
    {
        Assert.NotNull(DocumentEmailService.EtaxEmailStateProblem(DocumentStatus.Voided, EtaxStatus.Generated));
        Assert.NotNull(DocumentEmailService.EtaxEmailStateProblem(DocumentStatus.Draft, EtaxStatus.Generated));
        Assert.NotNull(DocumentEmailService.EtaxEmailStateProblem(DocumentStatus.Approved, EtaxStatus.Voided));
        Assert.NotNull(DocumentEmailService.EtaxEmailStateProblem(DocumentStatus.Approved, EtaxStatus.Error));
        Assert.Null(DocumentEmailService.EtaxEmailStateProblem(DocumentStatus.Paid, EtaxStatus.Generated));
        Assert.Null(DocumentEmailService.EtaxEmailStateProblem(DocumentStatus.Approved, EtaxStatus.Signed));
    }
}
