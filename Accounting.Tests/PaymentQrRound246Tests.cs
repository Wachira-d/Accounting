using Accounting.Helpers;
using Accounting.Models.Entities;
using Accounting.Models.Enums;
using Accounting.Services.Implementations;
using Accounting.Services.Implementations.Pdf;
using SkiaSharp;
using UglyToad.PdfPig;
using Xunit;

namespace Accounting.Tests;

/// <summary>
/// รอบ 246 QR — QR ชำระเงินของบัญชีธนาคาร + บนเอกสาร (คำตัดสิน 246-QR-1..8 · DOCUMENT_FLOW §2.12i):
/// payload พร้อมเพย์ EMVCo + CRC16-CCITT เทียบค่าที่คำนวณด้วยตัวคิดอิสระ (ทุกไบต์) · ตัวตรวจเลขพร้อมเพย์ · ลำดับความสำคัญ QR · บัญชีที่ใช้ ·
/// ใบไหนพิมพ์ · ยอดที่ฝัง · QR หน้าชำระ · ตัวตรวจรูป · ตัวเรนเดอร์ทั้งสอง (HTML + QuestPDF) มี QR เมื่อมีกล่อง และไม่มีเมื่อไม่มี
/// </summary>
public class PaymentQrRound246Tests
{
    // ─────────────── payload พร้อมเพย์ (ตัวสร้างเดียวของระบบ PromptPayService) ───────────────

    /// <summary>CRC-16/CCITT-FALSE อิสระจากตัวในโค้ด (ค่าตรวจมาตรฐาน "123456789" = 0x29B1)</summary>
    private static ushort Crc(string s)
    {
        ushort c = 0xFFFF;
        foreach (var b in System.Text.Encoding.ASCII.GetBytes(s))
        {
            c ^= (ushort)(b << 8);
            for (var i = 0; i < 8; i++) c = (c & 0x8000) != 0 ? (ushort)((c << 1) ^ 0x1021) : (ushort)(c << 1);
        }
        return c;
    }

    [Fact]
    public void CRC_ค่าตรวจมาตรฐาน() => Assert.Equal(0x29B1, Crc("123456789"));

    // ค่าคาดหวังคำนวณด้วยตัวคิด EMVCo อิสระ (Python · TLV + CRC16-CCITT-FALSE) ลำดับแท็ก 00·01·29·52·53·(54)·58·63 ของ PromptPayService
    [Theory]
    [InlineData("0812345678", 0, "00020101021129370016A000000677010111011300668123456785204000053037645802TH630474B5")]
    [InlineData("0812345678", 1040, "00020101021229370016A0000006770101110113006681234567852040000530376454071040.005802TH6304BCC0")]
    [InlineData("081-234-5678", 0.01, "00020101021229370016A0000006770101110113006681234567852040000530376454040.015802TH6304B2CC")]
    [InlineData("0105556000009", 0, "00020101021129370016A000000677010111021301055560000095204000053037645802TH6304B062")]
    [InlineData("0105556000009", 15515.5, "00020101021229370016A00000067701011102130105556000009520400005303764540815515.505802TH63048709")]
    public void Payload_พร้อมเพย์_ตรงทุกไบต์_เบอร์และเลขภาษี_มีและไม่มียอด(string id, double amount, string expected)
    {
        var payload = new PromptPayService().GenerateQrPayload(id, (decimal)amount);
        Assert.Equal(expected, payload);
        Assert.Equal(Crc(payload[..^4]).ToString("X4"), payload[^4..]);
        Assert.StartsWith(amount > 0 ? "000201010212" : "000201010211", payload);   // มียอด = dynamic · ไม่มี = static
    }

    // ─────────────── เลขพร้อมเพย์ของบัญชี ───────────────

    [Theory]
    [InlineData("0812345678", "0812345678")]
    [InlineData("081-234-5678", "0812345678")]
    [InlineData("0-1055-56000-00-9", "0105556000009")]
    public void เลขพร้อมเพย์ที่ถูกต้อง_ตัดขีดแล้วเก็บตัวเลขล้วน(string raw, string stored)
    {
        var (id, problem) = PaymentQrPolicy.NormalizePromptPayId(raw);
        Assert.Null(problem);
        Assert.Equal(stored, id);
    }

    [Theory]
    [InlineData("0105556000007")]      // checksum ผิด
    [InlineData("8123456789")]         // เบอร์ไม่ขึ้นต้น 0
    [InlineData("081234567")]          // 9 หลัก
    [InlineData("123456789012345")]    // e-Wallet 15 หลัก — ไม่รับบนเอกสาร
    [InlineData("08l2345678")]         // ตัวอักษรปน
    public void เลขพร้อมเพย์ผิดรูป_มีข้อความ_ไม่เก็บ(string raw)
    {
        var (id, problem) = PaymentQrPolicy.NormalizePromptPayId(raw);
        Assert.Null(id);
        Assert.False(string.IsNullOrWhiteSpace(problem));
        Assert.False(PaymentQrPolicy.PromptPayUsable(raw));
    }

    [Fact]
    public void เลขพร้อมเพย์ว่าง_คือล้างค่า_ไม่ใช่ข้อผิดพลาด()
        => Assert.Equal(((string?)null, (string?)null), PaymentQrPolicy.NormalizePromptPayId("  "));

    // ─────────────── ลำดับความสำคัญของ QR บัญชี (246-QR-2) ───────────────

    [Theory]
    [InlineData("auto", true, true, PaymentQrPolicy.Kind.PromptPay)]
    [InlineData("auto", false, true, PaymentQrPolicy.Kind.Upload)]
    [InlineData("promptpay", true, true, PaymentQrPolicy.Kind.PromptPay)]
    [InlineData("promptpay", false, true, PaymentQrPolicy.Kind.Upload)]   // "ก่อน" ไม่ใช่ "เท่านั้น" — ไม่ปล่อยกระดาษว่าง
    [InlineData("upload", true, true, PaymentQrPolicy.Kind.Upload)]
    [InlineData("upload", true, false, PaymentQrPolicy.Kind.PromptPay)]
    [InlineData(null, false, false, PaymentQrPolicy.Kind.None)]
    [InlineData("แปลก", true, false, PaymentQrPolicy.Kind.PromptPay)]       // ค่าที่เก็บไม่รู้จัก = auto
    public void แหล่งQR_ตามทางเลือก(string? source, bool pp, bool upload, PaymentQrPolicy.Kind expected)
        => Assert.Equal(expected, PaymentQrPolicy.Pick(source, pp, upload));

    [Fact]
    public void แหล่งQR_ค่าจากคำขอที่ไม่รู้จัก_ถูกปฏิเสธ_ไม่ตกเป็นค่าตั้งต้นเงียบ()
    {
        Assert.NotNull(PaymentQrPolicy.SourceProblem("qr"));
        Assert.Null(PaymentQrPolicy.SourceProblem("UPLOAD"));
        Assert.Null(PaymentQrPolicy.SourceProblem(null));
    }

    // ─────────────── บัญชีที่ใช้บนใบ (246-QR-3) ───────────────

    private static readonly Guid A = Guid.Parse("a0000000-0000-0000-0000-000000000246");
    private static readonly Guid B = Guid.Parse("b0000000-0000-0000-0000-000000000246");

    private static readonly Guid[] None = Array.Empty<Guid>();

    [Fact]
    public void แหล่งเงินของใบชนะบัญชีของใบ_ตามentity_และทางเลือกที่ระบุชัดไม่ถูกแทน()
    {
        // ฝ่ายค้าน S3: PaymentAccountId "takes precedence over BankAccountId" (Document.cs)
        Assert.Equal(B, PaymentQrPolicy.PickAccount(new[] { B }, A, null, new[] { A, B }).AccountId);
        var chosenNoQr = PaymentQrPolicy.PickAccount(None, A, B, new[] { B });       // ใบเลือก A (ไม่มี QR) ⇒ ไม่พิมพ์ ไม่สลับไป B
        Assert.Null(chosenNoQr.AccountId);
        Assert.Equal(A, chosenNoQr.ChosenId);
        Assert.Equal(PaymentQrPolicy.ProblemChosenNoQr, chosenNoQr.Problem);
        Assert.Equal(B, PaymentQrPolicy.PickAccount(None, null, B, new[] { A, B }).AccountId);
        // แหล่งเงินเป็นเงินสด (ผังไม่ผูกบัญชีธนาคาร) ⇒ ใช้บัญชีของใบ
        Assert.Equal(A, PaymentQrPolicy.PickAccount(None, A, null, new[] { A, B }).AccountId);
    }

    [Fact]
    public void ผังเดียวผูกหลายบัญชี_ไม่เดา()
    {
        var r = PaymentQrPolicy.PickAccount(new[] { A, B }, A, A, new[] { A, B });
        Assert.Null(r.AccountId);
        Assert.Equal(PaymentQrPolicy.ProblemGlAmbiguous, r.Problem);
    }

    [Fact]
    public void ไม่มีใครเลือก_บัญชีเดียวที่ตั้งQR_ใช้ได้_หลายบัญชีไม่เดา()
    {
        Assert.Equal(A, PaymentQrPolicy.PickAccount(None, null, null, new[] { A }).AccountId);
        var many = PaymentQrPolicy.PickAccount(None, null, null, new[] { A, B });
        Assert.Null(many.AccountId);
        Assert.Equal(PaymentQrPolicy.ProblemManyConfigured, many.Problem);
        var none = PaymentQrPolicy.PickAccount(None, null, null, None);
        Assert.Null(none.AccountId);
        Assert.Null(none.Problem);   // ยังไม่ตั้ง QR เลย — ไม่ใช่ปัญหาที่ต้องเตือนทุกใบ
    }

    [Fact]
    public void แก้ข้อความใต้QR_ตรวจเฉพาะธนาคารชื่อเลข_เลขเทียบตัวเลข()
    {
        Assert.False(PaymentQrPolicy.CaptionChanged("กสิกรไทย", "บริษัท", "123-4-56789-0", "กสิกรไทย", "บริษัท", "1234567890"));
        Assert.True(PaymentQrPolicy.CaptionChanged("กสิกรไทย", "บริษัท", "1234567890", "กสิกรไทย", "บริษัท", "1234567891"));
        Assert.True(PaymentQrPolicy.CaptionChanged("กสิกรไทย", "บริษัท", "1234567890", "กรุงเทพ", "บริษัท", "1234567890"));
        Assert.True(PaymentQrPolicy.CaptionChanged("กสิกรไทย", "บริษัท", "1234567890", "กสิกรไทย", "บริษัท ใหม่", "1234567890"));
    }

    [Fact]
    public void ข้อความบัญชีของเทมเพลตไม่มีเลขบัญชีของQR_เตือน_มีแล้วไม่เตือน()
    {
        Assert.Null(PaymentQrPolicy.BankTextMismatch("กสิกรไทย 123-4-56789-0 บจก. ทดสอบ", "1234567890", "x"));
        Assert.Null(PaymentQrPolicy.BankTextMismatch("", "1234567890", "x"));                       // ไม่มีข้อความ — ใต้ QR พิมพ์บัญชีจริงอยู่แล้ว
        Assert.NotNull(PaymentQrPolicy.BankTextMismatch("กรุงเทพ 999-9-99999-9", "1234567890", "x"));
    }

    // ─────────────── ใบไหนพิมพ์ QR บัญชี · ยอดที่ฝัง · QR หน้าชำระ ───────────────

    [Theory]
    [InlineData(DocumentType.Invoice, DocumentStatus.Approved, 1040, "THB", true)]
    [InlineData(DocumentType.Quotation, DocumentStatus.Draft, 0, null, true)]          // ใบเสนอราคา — ไม่มียอดค้าง ยังพิมพ์ (QR ไม่ระบุยอด)
    [InlineData(DocumentType.Invoice, DocumentStatus.Paid, 0, "THB", false)]          // ชำระครบ
    [InlineData(DocumentType.Invoice, DocumentStatus.Draft, 0, "THB", true)]          // ร่าง: ยอดค้างยังไม่ตั้ง — พิมพ์แบบไม่ระบุยอด
    [InlineData(DocumentType.Receipt, DocumentStatus.Approved, 0, "THB", false)]
    [InlineData(DocumentType.CreditNote, DocumentStatus.Approved, 500, "THB", false)]
    [InlineData(DocumentType.PurchaseOrder, DocumentStatus.Approved, 9000, "THB", false)]   // ฝั่งซื้อ — ไม่เชิญผู้ขายโอนเข้าบัญชีเรา
    [InlineData(DocumentType.Invoice, DocumentStatus.Approved, 1040, "USD", false)]    // พร้อมเพย์รับแต่บาท
    [InlineData(DocumentType.Invoice, DocumentStatus.Voided, 1040, "THB", false)]
    public void ใบที่พิมพ์QRบัญชี(DocumentType type, DocumentStatus status, double balance, string? currency, bool expected)
        => Assert.Equal(expected, PaymentQrPolicy.ShowBankQr(true, type, null, null, false, status, (decimal)balance, currency));

    [Fact]
    public void ปิดในเทมเพลต_ไม่พิมพ์QRบัญชี()
        => Assert.False(PaymentQrPolicy.ShowBankQr(false, DocumentType.Invoice, null, null, false, DocumentStatus.Approved, 1040m, "THB"));

    [Fact]
    public void ใบเพิ่มหนี้ของใบซื้อ_ไม่พิมพ์()
    {
        Assert.False(PaymentQrPolicy.ShowBankQr(true, DocumentType.DebitNote, DocumentType.PurchaseInvoice, null, false, DocumentStatus.Approved, 100m, "THB"));
        Assert.True(PaymentQrPolicy.ShowBankQr(true, DocumentType.DebitNote, DocumentType.TaxInvoice, null, false, DocumentStatus.Approved, 100m, "THB"));
    }

    /// <summary>รอบ 250 WP-04: ใบเพิ่มหนี้ที่ผู้ใช้ย้ายฝั่ง/ผู้ติดต่อเป็นผู้ขายอย่างเดียว — QR ตัดสินด้วยข้อเท็จจริงชุดเดียวกับ JE/ลิงก์ชำระ (สองทิศ)</summary>
    [Fact]
    public void R250_ใบเพิ่มหนี้ย้ายฝั่ง_QRตามฝั่งที่ผู้ใช้สั่ง_ทั้งสองทิศ()
    {
        // ต้นทางเป็นใบขาย แต่ผู้ใช้ย้ายเป็นฝั่งซื้อ ⇒ ห้ามพิมพ์ QR บัญชีเรา (เดิม: ดูแค่ต้นทาง ⇒ พิมพ์ = เชิญผู้ขายโอนเข้าบัญชีเรา)
        Assert.False(PaymentQrPolicy.ShowBankQr(true, DocumentType.DebitNote, DocumentType.TaxInvoice, true, false, DocumentStatus.Approved, 100m, "THB"));
        Assert.False(PaymentQrPolicy.IsBankQrDocument(DocumentType.DebitNote, DocumentType.TaxInvoice, true, false));
        // ทิศตรงข้าม: ไม่มีใบต้นทาง แต่ผู้ใช้ย้ายเป็นฝั่งขาย ⇒ พิมพ์ (เดิม: ไม่รู้ฝั่ง ⇒ ไม่พิมพ์ ทั้งที่ลิงก์ชำระออกได้)
        Assert.True(PaymentQrPolicy.ShowBankQr(true, DocumentType.DebitNote, null, false, false, DocumentStatus.Approved, 100m, "THB"));
        Assert.True(PaymentQrPolicy.IsBankQrDocument(DocumentType.DebitNote, null, false, false));
        // ไม่มีต้นทาง · ไม่สั่งฝั่ง · ผู้ติดต่อเป็นผู้ขายอย่างเดียว ⇒ ฝั่งซื้อ ⇒ ไม่พิมพ์ · ไม่รู้ฝั่งเลย ⇒ ไม่พิมพ์
        Assert.False(PaymentQrPolicy.IsBankQrDocument(DocumentType.DebitNote, null, null, true));
        Assert.False(PaymentQrPolicy.IsBankQrDocument(DocumentType.DebitNote, null, null, false));
        // ตรงกับตัวตัดสินฝั่งของลิงก์ชำระทุกชุดข้อเท็จจริง (ห้าม drift)
        foreach (var rel in new DocumentType?[] { null, DocumentType.TaxInvoice, DocumentType.Invoice, DocumentType.PurchaseInvoice })
            foreach (var ov in new bool?[] { null, true, false })
                foreach (var supplierOnly in new[] { true, false })
                    Assert.Equal(DocumentPayLinkPolicy.IsSaleSide(DocumentType.DebitNote, rel, ov, supplierOnly),
                        PaymentQrPolicy.IsBankQrDocument(DocumentType.DebitNote, rel, ov, supplierOnly));
        // ชนิดอื่นไม่ขึ้นกับข้อเท็จจริงฝั่ง (ใบแจ้งหนี้พิมพ์เสมอ · ใบลดหนี้ไม่พิมพ์เสมอ)
        Assert.True(PaymentQrPolicy.IsBankQrDocument(DocumentType.Invoice, null, true, true));
        Assert.False(PaymentQrPolicy.IsBankQrDocument(DocumentType.CreditNote, DocumentType.TaxInvoice, false, false));
    }

    [Fact]
    public void ยอดที่ฝัง_เฉพาะใบที่ออกลิงก์ได้_ยอดสุทธิของลิงก์()
    {
        Assert.Equal(1040m, PaymentQrPolicy.EmbedAmount(null, 1040m));
        Assert.Equal(1040.01m, PaymentQrPolicy.EmbedAmount(null, 1040.005m));          // AwayFromZero
        Assert.Null(PaymentQrPolicy.EmbedAmount("ต้องอนุมัติเอกสารก่อน", 1040m));
        Assert.Null(PaymentQrPolicy.EmbedAmount(null, 0m));
        Assert.Null(PaymentQrPolicy.EmbedAmount(null, null));
        Assert.Null(PaymentQrPolicy.EmbedAmount(null, 1040m, supersededByTaxInvoice: true));   // N6 ลูกหนี้ย้ายไปใบกำกับแล้ว
    }

    [Theory]
    [InlineData("08123456789")]      // 11 หลัก — เดิมกลายเป็นเบอร์
    [InlineData("123456789012")]     // 12 หลัก
    [InlineData("81234567")]         // 8 หลัก — เดิมกลายเป็น e-Wallet
    [InlineData("1812345678")]       // 10 หลักไม่ขึ้นต้น 0
    public void ตัวสร้างpayload_ความยาวผิดชนิด_โยน_ไม่สร้างQRที่โอนเข้าใครไม่รู้(string id)
        => Assert.Throws<ArgumentException>(() => new PromptPayService().GenerateQrPayload(id, 10m));

    [Fact]
    public void ตัวสร้างpayload_eWallet15หลัก_ยังใช้ได้()
        => Assert.Contains("0315123456789012345", new PromptPayService().GenerateQrPayload("123456789012345", 0m));

    [Fact]
    public void QRหน้าชำระ_ต้องมีลิงก์ใช้ได้_มีช่องทางออนไลน์_ไม่ใช่ไฟล์eTax()
    {
        const string url = "https://app.nextacc.test/pages/pay-document.html?c=x&t=y";
        Assert.True(PaymentQrPolicy.ShowPayLinkQr(true, false, url, 1));
        Assert.False(PaymentQrPolicy.ShowPayLinkQr(true, true, url, 1));     // PDF/A-3 ของ e-Tax
        Assert.False(PaymentQrPolicy.ShowPayLinkQr(true, false, null, 1));   // เพิกถอน/หมดอายุ ⇒ ไม่มี URL
        Assert.False(PaymentQrPolicy.ShowPayLinkQr(true, false, url, 0));    // ช่องทางของใบไม่มีออนไลน์
        Assert.False(PaymentQrPolicy.ShowPayLinkQr(false, false, url, 1));
    }

    [Fact]
    public void กล่องของไฟล์eTax_ตัดQRหน้าชำระทิ้ง_คงQRบัญชี()
    {
        var bank = new BankPaymentQr(new byte[] { 1 }, PaymentQrPolicy.Kind.PromptPay, "กสิกรไทย", "บริษัท", "123", "0812345678", 10m);
        var block = new PaymentQrBlock(bank, new PayLinkQr(new byte[] { 2 }, null));
        Assert.Null(block.WithoutPayLink()!.PayLink);
        Assert.Same(bank, block.WithoutPayLink()!.Bank);
        Assert.Null(new PaymentQrBlock(null, new PayLinkQr(new byte[] { 2 }, null)).WithoutPayLink());
    }

    // ─────────────── ตัวตรวจรูปที่อัปโหลด (246-QR-6) ───────────────

    private static byte[] Image(int w, int h, SKEncodedImageFormat fmt)
    {
        using var bmp = new SKBitmap(w, h);
        using (var c = new SKCanvas(bmp)) { c.Clear(SKColors.White); c.DrawRect(10, 10, w / 3f, h / 3f, new SKPaint { Color = SKColors.Black }); }
        using var img = SKImage.FromBitmap(bmp);
        using var data = img.Encode(fmt, 90);
        return data.ToArray();
    }

    [Fact]
    public void รูปPNGและJPEGขนาดปกติ_ผ่าน_อ่านขนาดถูก()
    {
        var png = PaymentQrImageValidator.Validate(Image(400, 400, SKEncodedImageFormat.Png));
        Assert.True(png.Ok, png.Message);
        Assert.Equal(("image/png", 400, 400), (png.ContentType, png.Width, png.Height));
        var jpg = PaymentQrImageValidator.Validate(Image(720, 1280, SKEncodedImageFormat.Jpeg));   // ภาพหน้าจอแอปธนาคารแนวตั้ง
        Assert.True(jpg.Ok, jpg.Message);
        Assert.Equal(("image/jpeg", 720, 1280), (jpg.ContentType, jpg.Width, jpg.Height));
    }

    [Fact]
    public void ไฟล์HTMLที่ตั้งชื่อเป็นรูป_ไม่ผ่าน()
        => Assert.Equal(PaymentQrImageValidator.Code.NotImage,
            PaymentQrImageValidator.Validate(System.Text.Encoding.UTF8.GetBytes("<html><script>alert(1)</script></html>")).Code);

    [Fact]
    public void ใหญ่เกิน1MB_เล็กเกิน_ยาวผิดรูป_หัวไฟล์เสีย_ไม่ผ่าน()
    {
        var big = new byte[PaymentQrImageValidator.MaxBytes + 1];
        Image(200, 200, SKEncodedImageFormat.Png).CopyTo(big, 0);
        Assert.Equal(PaymentQrImageValidator.Code.TooLarge, PaymentQrImageValidator.Validate(big).Code);
        Assert.Equal(PaymentQrImageValidator.Code.BadDimensions, PaymentQrImageValidator.Validate(Image(60, 60, SKEncodedImageFormat.Png)).Code);
        Assert.Equal(PaymentQrImageValidator.Code.BadDimensions, PaymentQrImageValidator.Validate(Image(1600, 200, SKEncodedImageFormat.Png)).Code);
        Assert.Equal(PaymentQrImageValidator.Code.BadStructure, PaymentQrImageValidator.Validate(Image(400, 400, SKEncodedImageFormat.Png)[..20]).Code);
        Assert.Equal(PaymentQrImageValidator.Code.Empty, PaymentQrImageValidator.Validate(Array.Empty<byte>()).Code);
        Assert.Equal(PaymentQrImageValidator.Code.NotImage, PaymentQrImageValidator.Validate(Image(400, 400, SKEncodedImageFormat.Webp)).Code);
    }

    [Fact]
    public void เข้ารหัสใหม่_ตัดข้อมูลแฝงท้ายไฟล์ทิ้ง()
    {
        var polyglot = Image(300, 300, SKEncodedImageFormat.Png).Concat(System.Text.Encoding.ASCII.GetBytes("<script>steal()</script>")).ToArray();
        Assert.True(PaymentQrImageValidator.Validate(polyglot).Ok);   // หัวไฟล์ถูก — ด่านถัดไปต้องทำงาน
        var clean = PaymentQrService.Reencode(polyglot)!;
        Assert.DoesNotContain("<script>", System.Text.Encoding.ASCII.GetString(clean));
        Assert.True(PaymentQrImageValidator.Validate(clean).Ok);
    }

    [Fact]
    public void ไฟล์นอกโฟลเดอร์ของบริษัท_ไม่ถูกอ่าน()
    {
        var root = Path.Combine(Path.GetTempPath(), "qr246");
        var cid = Guid.NewGuid();
        var own = Path.Combine(root, PaymentQrService.StorageFolder, cid.ToString("N"), "x.png");
        Assert.True(PaymentQrService.IsOwnLocation(own, root, cid));
        Assert.False(PaymentQrService.IsOwnLocation(own, root, Guid.NewGuid()));
        Assert.False(PaymentQrService.IsOwnLocation(Path.Combine(root, PaymentQrService.StorageFolder, cid.ToString("N"), "..", "..", "y.png"), root, cid));
        Assert.False(PaymentQrService.IsOwnLocation(null, root, cid));
    }

    // ─────────────── ตัวเรนเดอร์ทั้งสอง (HTML + QuestPDF) ───────────────

    private static (Document Doc, Company Co, DocumentTemplate T) Sample()
    {
        var co = new Company { Name = "บริษัท ทดสอบคิวอาร์ จำกัด", TaxId = "0105556000009", Address = "1 ถนนสีลม กรุงเทพฯ" };
        var t = new DocumentTemplate { DocumentType = DocumentType.Invoice, ShowLogo = false, BankDetailsText = "กสิกรไทย 123-4-56789-0" };
        var doc = new Document
        {
            DocumentNumber = "INV-246-0001", DocumentType = DocumentType.Invoice, Status = DocumentStatus.Approved,
            DocumentDate = new DateTime(2026, 11, 6), Contact = new Contact { Name = "ลูกค้า", TaxId = "0107537000017", Address = "กรุงเทพฯ" },
            SubTotal = 1000m, VatAmount = 70m, TotalAmount = 1070m, BalanceDue = 1070m,
        };
        doc.Lines.Add(new DocumentLine { LineOrder = 1, Description = "ค่าบริการ", Quantity = 1, UnitPrice = 1000m, Amount = 1000m, VatRate = 7m, VatAmount = 70m });
        return (doc, co, t);
    }

    private static PaymentQrBlock Block(string accountName = "บริษัท ทดสอบคิวอาร์ จำกัด") => new(
        new BankPaymentQr(new PromptPayService().GenerateQrImage("0812345678", 1070m), PaymentQrPolicy.Kind.PromptPay,
            "กสิกรไทย", accountName, "123-4-56789-0", "0812345678", 1070m, new DateTime(2026, 11, 6, 3, 0, 0, DateTimeKind.Utc)),
        new PayLinkQr(PaymentQrService.UrlQrPng("https://app.nextacc.test/pages/pay-document.html?c=1&t=2"), new DateTime(2027, 1, 5, 3, 0, 0, DateTimeKind.Utc)));

    private static PdfGenerationService Renderer() => new(null!, null!);

    [Fact]
    public void HTML_มีกล่องQRเมื่อมีกล่อง_ไม่มีเมื่อไม่มี_ข้อความจากป้ายกลาง()
    {
        var (doc, co, t) = Sample();
        var L = DocumentLabels.For("th");
        var withQr = Renderer().BuildDocumentHtml(doc, co, null, t, null, "th", paymentQr: Block());
        Assert.Contains("class='payment-qr'", withQr);
        Assert.Equal(2, System.Text.RegularExpressions.Regex.Matches(withQr, "class='payment-qr-box'").Count);
        Assert.Contains(L.PayQrPromptPayTitle, withQr);
        Assert.Contains(L.PayQrAmount(1070m), withQr);
        Assert.Contains("081-234-5678", withQr);
        Assert.Contains(L.PayQrLinkTitle, withQr);
        Assert.Contains("5/1/2570", withQr);                         // พ.ศ. · เวลาไทย
        Assert.Contains(L.PayQrAmountAsOf(new DateTime(2026, 11, 6, 3, 0, 0, DateTimeKind.Utc)), withQr);   // N1 "ยอด ณ วันที่พิมพ์ 6/11/2569"
        Assert.Contains("6/11/2569", withQr);
        Assert.Contains(L.PayQrAccountNo("123-4-56789-0"), withQr);  // S2(ข) เลขบัญชีของบัญชีที่ QR ใช้
        Assert.DoesNotContain("pay-document.html", withQr);          // ไม่พิมพ์ URL ที่มี token เป็นข้อความ
        var without = Renderer().BuildDocumentHtml(doc, co, null, t, null, "th", paymentQr: null);
        Assert.DoesNotContain("payment-qr", without);
    }

    [Fact]
    public void HTML_ข้อความจากผู้ใช้ถูกHtmlEncode()
    {
        var (doc, co, t) = Sample();
        var html = Renderer().BuildDocumentHtml(doc, co, null, t, null, "th", paymentQr: Block("<img src=x onerror=alert(1)>"));
        Assert.DoesNotContain("<img src=x onerror", html);
        Assert.Contains("&lt;img src=x onerror=alert(1)&gt;", html);
    }

    [Fact]
    public void ภาษาอังกฤษ_ใช้ป้ายอังกฤษ()
    {
        var (doc, co, t) = Sample();
        var html = Renderer().BuildDocumentHtml(doc, co, null, t, null, "en", paymentQr: Block());
        Assert.Contains(DocumentLabels.For("en").PayQrPromptPayTitle, html);
        Assert.DoesNotContain(DocumentLabels.For("th").PayQrPromptPayTitle, html);
    }

    /// <summary>ข้อความทั้งไฟล์แบบตัดช่องว่าง (ฝ่ายค้าน: นับป้ายใต้ QR แทนนับรูป — ไม่เปราะต่อรูปอื่นบนกระดาษ) · ใช้ป้ายอังกฤษ (ไม่ขึ้นกับการตัดคำไทยของตัวอ่าน PDF)</summary>
    private static string PdfText(byte[] pdf)
    {
        using var d = PdfDocument.Open(pdf);
        return new string(string.Concat(d.GetPages().Select(p => string.Concat(p.Letters.Select(l => l.Value)))).Where(c => !char.IsWhiteSpace(c)).ToArray());
    }

    private static string NoSpace(string s) => new(s.Where(c => !char.IsWhiteSpace(c)).ToArray());

    [Fact]
    public void QuestPDF_มีป้ายQRเมื่อมีกล่อง_ไม่มีเมื่อไม่มี()
    {
        QuestPDF.Settings.License = QuestPDF.Infrastructure.LicenseType.Community;
        var (doc, co, t) = Sample();
        var L = DocumentLabels.For("en");
        var withQr = PdfText(Renderer().RenderDocumentPdfNative(doc, co, null, t, null, "en", paymentQr: Block("Test Co., Ltd.")));
        var without = PdfText(Renderer().RenderDocumentPdfNative(doc, co, null, t, null, "en", paymentQr: null));
        Assert.Contains(NoSpace(L.PayQrPromptPayTitle), withQr);
        Assert.Contains(NoSpace(L.PayQrLinkTitle), withQr);
        Assert.Contains(NoSpace(L.PayQrAccountNo("123-4-56789-0")), withQr);
        Assert.DoesNotContain(NoSpace(L.PayQrPromptPayTitle), without);
        Assert.DoesNotContain(NoSpace(L.PayQrLinkTitle), without);
    }

    [Fact]
    public void ข้อความใต้QR_ตัวเดียวของสองrenderer()
    {
        var L = DocumentLabels.For("th");
        var cap = PdfGenerationService.PaymentQrCaption(Block().Bank!, L);
        Assert.Equal(L.PayQrPromptPayTitle, cap.Title);
        Assert.Equal(new[]
        {
            "พร้อมเพย์ 081-234-5678", "ธนาคาร กสิกรไทย", "ชื่อบัญชี บริษัท ทดสอบคิวอาร์ จำกัด", "เลขที่บัญชี 123-4-56789-0",
            L.PayQrAmount(1070m), "ยอด ณ วันที่พิมพ์ 6/11/2569",
        }, cap.Lines);
        var upload = PdfGenerationService.PaymentQrCaption(Block().Bank! with { Kind = PaymentQrPolicy.Kind.Upload, PromptPayId = null, Amount = null }, L);
        Assert.Equal(L.PayQrUploadTitle, upload.Title);
        Assert.Equal(L.PayQrNoAmount, upload.Lines[^1]);
    }
}
