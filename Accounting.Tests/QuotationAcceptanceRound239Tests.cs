using System.IO.Compression;
using System.Text;
using Accounting.Helpers;
using Accounting.Services.Implementations;
using Accounting.Services.Implementations.Pdf;
using QuestPDF.Fluent;
using Xunit;
using EntDocument = Accounting.Models.Entities.Document;
using EntTemplate = Accounting.Models.Entities.DocumentTemplate;

namespace Accounting.Tests;

/// <summary>
/// รอบ 239 — ยอมรับใบเสนอราคาออนไลน์ + ลายเซ็น + ดาวน์โหลด PDF (คำตัดสิน 239-1..239-6)
/// <para>ล็อก: ตัวตรวจลายเซ็น (PNG จริง · ขนาด · ขนาดภาพ · ว่าง · filter ทุกชนิด) · ตัวตัดสินการยอมรับ (idempotent · ชื่อบังคับ ·
/// ค่าตั้งบังคับลายเซ็น · ลายเซ็นเสียต้องถูกปฏิเสธไม่ใช่ทิ้งเงียบ) · ตำแหน่งไฟล์ต่อบริษัท · ล้างหลักฐานทั้งชุดตอน Rev ใหม่ ·
/// บล็อกการยอมรับบนกระดาษทั้ง HTML และ QuestPDF (ป้ายจาก DocumentLabels)</para>
/// </summary>
public class QuotationAcceptanceRound239Tests
{
    // ───────────── PNG ตัวอย่าง (ประกอบเองในเทสต์ — ไม่พึ่งไลบรารีรูป) ─────────────

    private static readonly uint[] CrcTable = Enumerable.Range(0, 256).Select(n =>
    {
        var c = (uint)n;
        for (var k = 0; k < 8; k++) c = (c & 1) != 0 ? 0xEDB88320u ^ (c >> 1) : c >> 1;
        return c;
    }).ToArray();

    private static uint Crc(byte[] data)
    {
        var c = 0xFFFFFFFFu;
        foreach (var b in data) c = CrcTable[(c ^ b) & 0xFF] ^ (c >> 8);
        return c ^ 0xFFFFFFFFu;
    }

    private static void Chunk(MemoryStream ms, string type, byte[] data)
    {
        var len = new byte[4];
        System.Buffers.Binary.BinaryPrimitives.WriteUInt32BigEndian(len, (uint)data.Length);
        ms.Write(len);
        var td = Encoding.ASCII.GetBytes(type).Concat(data).ToArray();
        ms.Write(td);
        var crc = new byte[4];
        System.Buffers.Binary.BinaryPrimitives.WriteUInt32BigEndian(crc, Crc(td));
        ms.Write(crc);
    }

    /// <summary>PNG 8 บิต · <paramref name="pixel"/>(x,y) คืนไบต์ของพิกเซลตามจำนวนช่องสี · filter ต่อแถว (0–4) ตาม <paramref name="filter"/></summary>
    internal static byte[] Png(int w, int h, byte colorType, Func<int, int, byte[]> pixel, Func<int, byte>? filter = null,
        bool withIend = true)
    {
        var channels = colorType switch { 0 => 1, 2 => 3, 4 => 2, 6 => 4, 3 => 1, _ => 1 };
        var stride = w * channels;
        var raw = new MemoryStream();
        var prev = new byte[stride];
        for (var y = 0; y < h; y++)
        {
            var cur = new byte[stride];
            for (var x = 0; x < w; x++) Array.Copy(pixel(x, y), 0, cur, x * channels, channels);
            var f = filter?.Invoke(y) ?? 0;
            raw.WriteByte(f);
            for (var i = 0; i < stride; i++)
            {
                int a = i >= channels ? cur[i - channels] : 0, b = prev[i], c = i >= channels ? prev[i - channels] : 0;
                int pred = f switch
                {
                    1 => a, 2 => b, 3 => (a + b) / 2,
                    4 => Math.Abs(b - c) <= Math.Abs(a - c) && Math.Abs(b - c) <= Math.Abs(a + b - 2 * c) ? a
                        : Math.Abs(a - c) <= Math.Abs(a + b - 2 * c) ? b : c,
                    _ => 0,
                };
                raw.WriteByte((byte)(cur[i] - pred));
            }
            prev = cur;
        }
        var z = new MemoryStream();
        using (var zs = new ZLibStream(z, CompressionLevel.Optimal, leaveOpen: true)) zs.Write(raw.ToArray());
        var ms = new MemoryStream();
        ms.Write(new byte[] { 0x89, 0x50, 0x4E, 0x47, 0x0D, 0x0A, 0x1A, 0x0A });
        var ihdr = new byte[13];
        System.Buffers.Binary.BinaryPrimitives.WriteUInt32BigEndian(ihdr.AsSpan(0, 4), (uint)w);
        System.Buffers.Binary.BinaryPrimitives.WriteUInt32BigEndian(ihdr.AsSpan(4, 4), (uint)h);
        ihdr[8] = 8; ihdr[9] = colorType; ihdr[10] = 0; ihdr[11] = 0; ihdr[12] = 0;
        Chunk(ms, "IHDR", ihdr);
        if (colorType == 3) Chunk(ms, "PLTE", new byte[] { 0, 0, 0, 255, 255, 255 });
        Chunk(ms, "IDAT", z.ToArray());
        if (withIend) Chunk(ms, "IEND", Array.Empty<byte>());
        return ms.ToArray();
    }

    /// <summary>"ลายเซ็น" บนพื้นโปร่ง (RGBA แบบที่ canvas ส่ง) — เส้นทแยง + เส้นแนวนอนหนา 3 พิกเซล</summary>
    internal static byte[] DrawnSignature(int w = 400, int h = 150, Func<int, byte>? filter = null)
        => Png(w, h, 6, (x, y) =>
        {
            var onDiag = Math.Abs(y - (x * (h - 1) / (w - 1))) <= 1;
            var onLine = y >= h / 2 - 1 && y <= h / 2 + 1 && x > 20 && x < w - 20;
            return onDiag || onLine ? new byte[] { 30, 41, 59, 255 } : new byte[] { 0, 0, 0, 0 };
        }, filter);

    /// <summary>ตรวจไบต์ผ่านทางเข้าจริงตัวเดียว (Validate ของข้อความ base64)</summary>
    private static SignatureImageValidator.Result VB(byte[]? bytes)
        => SignatureImageValidator.Validate(bytes == null ? null : Convert.ToBase64String(bytes));

    internal static string DataUri(byte[] png) => "data:image/png;base64," + Convert.ToBase64String(png);

    // ───────────── SignatureImageValidator ─────────────

    [Fact]
    public void ลายเซ็นจริงจากcanvas_ผ่าน_และคืนไบต์ที่ตรวจแล้ว()
    {
        var png = DrawnSignature();
        var r = SignatureImageValidator.Validate(DataUri(png));
        Assert.True(r.Ok, r.Message);
        Assert.Equal(400, r.Width);
        Assert.Equal(150, r.Height);
        Assert.True(r.InkPixels >= SignatureImageValidator.MinInkPixels);
        Assert.Equal(png, r.Png);
        // base64 ล้วน (ไม่มี prefix) ก็รับ — เส้นเซ็นรับสินค้าเดิมส่งแบบนี้ได้
        Assert.True(SignatureImageValidator.Validate(Convert.ToBase64String(png)).Ok);
    }

    [Theory]
    [InlineData(1)]
    [InlineData(2)]
    [InlineData(3)]
    [InlineData(4)]
    public void ทุกfilterของPNG_นับหมึกได้เท่ากับไม่มีfilter(byte f)
    {
        var plain = VB(DrawnSignature());
        var filtered = VB(DrawnSignature(filter: y => (byte)(y % 2 == 0 ? f : 0)));
        Assert.True(filtered.Ok, filtered.Message);
        Assert.Equal(plain.InkPixels, filtered.InkPixels);
    }

    [Fact]
    public void ช่องลายเซ็นว่าง_พื้นโปร่ง_ถูกปฏิเสธว่าว่าง()
    {
        var blank = Png(400, 150, 6, (_, _) => new byte[] { 0, 0, 0, 0 });
        var r = VB(blank);
        Assert.Equal(SignatureImageValidator.Code.Blank, r.Code);
        Assert.Null(r.Png);
    }

    [Fact]
    public void พื้นขาวทึบไม่มีเส้น_ว่าง_แต่พื้นขาวมีเส้นเข้ม_ผ่าน()
    {
        var white = Png(300, 100, 2, (_, _) => new byte[] { 255, 255, 255 });
        Assert.Equal(SignatureImageValidator.Code.Blank, VB(white).Code);
        var inked = Png(300, 100, 2, (x, y) => y is >= 48 and <= 52 ? new byte[] { 0, 0, 0 } : new byte[] { 255, 255, 255 });
        Assert.True(VB(inked).Ok);
    }

    [Fact]
    public void แตะจุดเดียว_ไม่นับเป็นลายเซ็น()
    {
        var dot = Png(400, 150, 6, (x, y) => x is >= 100 and < 104 && y is >= 70 and < 74 ? new byte[] { 0, 0, 0, 255 } : new byte[4]);
        Assert.Equal(SignatureImageValidator.Code.Blank, VB(dot).Code);
    }

    [Fact]
    public void ไม่ใช่PNG_ถูกปฏิเสธ()
    {
        Assert.Equal(SignatureImageValidator.Code.NotPng,
            SignatureImageValidator.Validate("data:image/jpeg;base64," + Convert.ToBase64String(new byte[] { 0xFF, 0xD8, 0xFF, 0xE0 })).Code);
        Assert.Equal(SignatureImageValidator.Code.NotPng,
            VB(Encoding.ASCII.GetBytes("<svg onload=alert(1)>" + new string('x', 100))).Code);
        Assert.Equal(SignatureImageValidator.Code.NotPng, SignatureImageValidator.Validate("ไม่ใช่ base64 !!!").Code);
        Assert.Equal(SignatureImageValidator.Code.NotPng, SignatureImageValidator.Validate("data:text/html;base64,PGgxPg==").Code);
        Assert.Equal(SignatureImageValidator.Code.Empty, SignatureImageValidator.Validate("   ").Code);
    }

    [Fact]
    public void เกิน200KB_ถูกปฏิเสธ_ทั้งข้อความยาวและไบต์ใหญ่()
    {
        var hugeText = new string('A', (SignatureImageValidator.MaxBytes / 3 + 2) * 4 + 16);
        Assert.Equal(SignatureImageValidator.Code.TooLarge, SignatureImageValidator.Validate(hugeText).Code);
        var hugeBytes = new byte[SignatureImageValidator.MaxBytes + 1];
        Assert.Equal(SignatureImageValidator.Code.TooLarge, VB(hugeBytes).Code);
    }

    [Theory]
    [InlineData(10, 10)]
    [InlineData(3000, 100)]
    [InlineData(400, 1200)]
    public void ขนาดภาพนอกกรอบ_ถูกปฏิเสธ(int w, int h)
    {
        var png = Png(w, h, 0, (_, _) => new byte[] { 0 });
        Assert.Equal(SignatureImageValidator.Code.BadDimensions, VB(png).Code);
    }

    [Fact]
    public void ไฟล์ขาดIEND_หรือชนิดสีพาเลต_ถูกปฏิเสธ()
    {
        var truncated = DrawnSignature().AsSpan(0, 60).ToArray();
        Assert.Equal(SignatureImageValidator.Code.BadStructure, VB(truncated).Code);
        var noIend = Png(400, 150, 6, (_, _) => new byte[] { 0, 0, 0, 255 }, withIend: false);
        Assert.Equal(SignatureImageValidator.Code.BadStructure, VB(noIend).Code);
        var palette = Png(400, 150, 3, (_, _) => new byte[] { 0 });
        Assert.Equal(SignatureImageValidator.Code.Unsupported, VB(palette).Code);
    }

    // ───────────── QuotationAcceptance.DecideAccept ─────────────

    [Fact]
    public void ยอมรับแล้ว_ตอบสถานะเดิม_ไม่ตรวจชื่อหรือลายเซ็นซ้ำ()
    {
        var d = QuotationAcceptance.DecideAccept(DateTime.UtcNow, "", null, requireSignature: true);
        Assert.Equal(QuotationAcceptance.Outcome.AlreadyAccepted, d.Outcome);
        Assert.False(d.Accept);
    }

    [Theory]
    [InlineData(null)]
    [InlineData("")]
    [InlineData("   ")]
    [InlineData("\n\t")]
    public void ไม่มีชื่อ_ถูกปฏิเสธ(string? name)
        => Assert.Equal(QuotationAcceptance.Outcome.NameRequired, QuotationAcceptance.DecideAccept(null, name, null, false).Outcome);

    [Fact]
    public void ค่าตั้งบังคับลายเซ็น_ไม่เซ็น_ถูกปฏิเสธ_ทิศตรงข้ามไม่บังคับ_ยอมรับได้()
    {
        Assert.Equal(QuotationAcceptance.Outcome.SignatureRequired, QuotationAcceptance.DecideAccept(null, "คุณเอ", null, true).Outcome);
        var ok = QuotationAcceptance.DecideAccept(null, "คุณเอ", "  ", false);
        Assert.True(ok.Accept);
        Assert.Null(ok.Signature);
    }

    [Fact]
    public void ส่งลายเซ็นว่างมา_ถูกปฏิเสธ_ไม่ทิ้งเงียบแล้วยอมรับแบบไม่เซ็น()
    {
        var blank = DataUri(Png(400, 150, 6, (_, _) => new byte[4]));
        var d = QuotationAcceptance.DecideAccept(null, "คุณเอ", blank, requireSignature: false);
        Assert.Equal(QuotationAcceptance.Outcome.SignatureInvalid, d.Outcome);
    }

    [Fact]
    public void ลายเซ็นจริง_ยอมรับพร้อมไบต์ที่ตรวจแล้ว()
    {
        var png = DrawnSignature();
        var d = QuotationAcceptance.DecideAccept(null, "คุณเอ", DataUri(png), requireSignature: true);
        Assert.True(d.Accept);
        Assert.Equal(png, d.Signature!.Png);
    }

    [Fact]
    public void CleanText_ตัดอักขระควบคุม_และตัดความยาว()
    {
        Assert.Equal("ก ข", QuotationAcceptance.CleanText("  ก\u0000ข  ", 10));
        Assert.Equal(200, QuotationAcceptance.CleanText(new string('ก', 300), QuotationAcceptance.NameMax)!.Length);
        Assert.Null(QuotationAcceptance.CleanText("\u0001\u0002", 10));
    }

    // ───────────── ที่เก็บ + การแยกบริษัท ─────────────

    [Fact]
    public void ตำแหน่งไฟล์ลายเซ็น_เป็นของบริษัทและเอกสารนั้นเท่านั้น()
    {
        var root = Path.Combine(Path.GetTempPath(), "att");
        Guid cid = Guid.NewGuid(), other = Guid.NewGuid(), doc = Guid.NewGuid(), doc2 = Guid.NewGuid();
        var loc = QuotationAcceptance.SignatureLocation(root, cid, doc, Guid.NewGuid());
        Assert.Contains(QuotationAcceptance.StorageFolder, loc);
        Assert.True(QuotationAcceptance.IsOwnSignatureLocation(loc, root, cid, doc));
        Assert.False(QuotationAcceptance.IsOwnSignatureLocation(loc, root, other, doc));    // token ของบริษัทอื่น
        Assert.False(QuotationAcceptance.IsOwnSignatureLocation(loc, root, cid, doc2));     // เอกสารอื่นของบริษัทเดียวกัน
        Assert.False(QuotationAcceptance.IsOwnSignatureLocation(
            Path.Combine(root, QuotationAcceptance.StorageFolder, cid.ToString("N"), "..", other.ToString("N"), $"{doc:N}-x.png"), root, cid, doc));
        Assert.False(QuotationAcceptance.IsOwnSignatureLocation("/etc/passwd", root, cid, doc));
        Assert.False(QuotationAcceptance.IsOwnSignatureLocation(null, root, cid, doc));
        // ฝ่ายค้าน N: ชื่อโฟลเดอร์ถูกต้องแต่อยู่นอกรากของที่เก็บนี้ ⇒ ไม่ใช่ของเรา
        var elsewhere = QuotationAcceptance.SignatureLocation(Path.Combine(Path.GetTempPath(), "other-root"), cid, doc, Guid.NewGuid());
        Assert.False(QuotationAcceptance.IsOwnSignatureLocation(elsewhere, root, cid, doc));
    }

    [Fact]
    public void โฟลเดอร์ลายเซ็น_ไม่อยู่ในรายการuploadsสาธารณะ()
    {
        var program = File.ReadAllText(Path.Combine(RepoRoot(), "Accounting", "Program.cs"));
        var start = program.IndexOf("var publicUploadPrefixes", StringComparison.Ordinal);
        var end = program.IndexOf("};", start, StringComparison.Ordinal);
        Assert.True(start > 0 && end > start);
        Assert.DoesNotContain(QuotationAcceptance.StorageFolder, program[start..end]);
    }

    [Fact]
    public void ออกRevใหม่_ล้างหลักฐานการยอมรับทุกช่อง()
    {
        var d = new EntDocument
        {
            QuotationAcceptedAt = DateTime.UtcNow, QuotationAcceptedBy = "คุณเอ", QuotationAcceptSignaturePath = "x",
            QuotationAcceptNote = "n", QuotationAcceptContentHash = "v2:x", QuotationAcceptIp = "1.1.1.1",
            QuotationAcceptUserAgentHash = "u", QuotationAcceptTokenHash = "t", QuotationAcceptToken = "keep",
        };
        QuotationAcceptance.ClearOn(d);
        // ทุกช่องหลักฐานที่ขึ้นต้น QuotationAccept ต้องว่าง (เพิ่มช่องใหม่แล้วลืมล้าง ⇒ เทสต์นี้ล้ม) — token/วันหมดอายุเป็นเรื่องของลิงก์ (ล้างแยกใน DocumentService)
        foreach (var p in typeof(EntDocument).GetProperties().Where(p => p.Name.StartsWith("QuotationAccept", StringComparison.Ordinal)
                     && p.Name is not (nameof(EntDocument.QuotationAcceptToken) or nameof(EntDocument.QuotationAcceptTokenExpiresAt))))
            Assert.True(p.GetValue(d) == null, $"{p.Name} ไม่ถูกล้าง");
        Assert.Equal("keep", d.QuotationAcceptToken);
    }

    [Fact]
    public void ช่องหลักฐานไม่ตามไปใบแทน()
    {
        foreach (var n in new[] { nameof(EntDocument.QuotationAcceptSignaturePath), nameof(EntDocument.QuotationAcceptIp),
                     nameof(EntDocument.QuotationAcceptContentHash), nameof(EntDocument.QuotationAcceptTokenHash) })
            Assert.Contains(n, SettlementPaidReissue.DocumentNotCarriedFields);
    }

    // ───────────── กระดาษ (สอง renderer) ─────────────

    [Theory]
    [InlineData(true, 3, "ลูกค้าอนุมัติ", true)]
    [InlineData(true, 2, "ลูกค้าอนุมัติ", false)]
    [InlineData(false, 3, "ลูกค้าอนุมัติ", false)]
    [InlineData(true, 3, " ", false)]
    public void มีช่องลูกค้าไหม_ตัดสินที่เดียว(bool show, int count, string label3, bool expected)
        => Assert.Equal(expected, QuotationAcceptance.CustomerBoxShown(show, count, label3));

    [Fact]
    public void ป้ายเวลายอมรับ_ไทยเป็นพศ_เวลาไทย_อังกฤษเป็นคศ()
    {
        var utc = new DateTime(2026, 11, 5, 7, 5, 0, DateTimeKind.Utc);   // 14:05 น. เวลาไทย
        Assert.Equal("ยอมรับออนไลน์ 5/11/2569 14:05 น.", DocumentLabels.For("th").QtAcceptedOnline(utc));
        Assert.Equal("Accepted online 05 Nov 2026 14:05 (GMT+7)", DocumentLabels.For("en").QtAcceptedOnline(utc));
    }

    private static PdfGenerationService.DocumentSigner AcceptedSigner(string name, string? note, byte[]? png)
        => new(png == null ? null : PdfGenerationService.ImageDataUri(png), png, name, null,
            AcceptedOnlineAt: new DateTime(2026, 11, 5, 7, 5, 0, DateTimeKind.Utc), AcceptNote: note);

    [Fact]
    public void HTML_บล็อกการยอมรับ_มีชื่อเวลาหมายเหตุลายเซ็น_และescapeค่าที่ลูกค้าพิมพ์()
    {
        var html = PdfGenerationService.QuotationAcceptanceBlockHtml(
            AcceptedSigner("<script>alert(1)</script>", "ok'\"<b>", DrawnSignature()),
            new DateTime(2026, 11, 5, 7, 5, 0, DateTimeKind.Utc), DocumentLabels.For("th"));
        Assert.Contains("ลูกค้ายอมรับใบเสนอราคาออนไลน์", html);
        Assert.Contains("ยอมรับออนไลน์ 5/11/2569 14:05 น.", html);
        Assert.Contains("data:image/png;base64,", html);
        Assert.Contains("หมายเหตุลูกค้า", html);
        Assert.DoesNotContain("<script>", html);
        Assert.DoesNotContain("<b>", html);
        Assert.Contains("&lt;script&gt;", html);
        // ทิศตรงข้าม: ยอมรับโดยไม่เซ็น ⇒ ไม่มีรูป แต่ยังมีชื่อ/เวลา
        var noSig = PdfGenerationService.QuotationAcceptanceBlockHtml(AcceptedSigner("คุณเอ", null, null),
            new DateTime(2026, 11, 5, 7, 5, 0, DateTimeKind.Utc), DocumentLabels.For("th"));
        Assert.DoesNotContain("<img", noSig);
        Assert.Contains("คุณเอ", noSig);
        Assert.DoesNotContain("หมายเหตุลูกค้า", noSig);
    }

    [Fact]
    public void QuestPDF_บล็อกการยอมรับ_พิมพ์ข้อความชุดเดียวกับHTML()
    {
        QuestPDF.Settings.License = QuestPDF.Infrastructure.LicenseType.Community;
        var template = new EntTemplate { ShowSignature = true, SignatureCount = 2 };   // ไม่มีช่องลูกค้า ⇒ ต้องพิมพ์บล็อกแยก
        var signers = new List<PdfGenerationService.DocumentSigner>
        {
            new(null, null, "Seller", null), new(null, null, "Approver", null),
            AcceptedSigner("John Smith", "Deliver on Monday", DrawnSignature()),
        };
        var pdf = QuestPDF.Fluent.Document.Create(c => c.Page(p =>
            p.Content().Column(col => PdfGenerationService.ComposeQuotationAcceptanceBlock(col, template, signers, "en")))).GeneratePdf();
        using var doc = UglyToad.PdfPig.PdfDocument.Open(pdf);
        // รอบ 250 WP-01 (เทสต์นี้เพิ่งรันครั้งแรก): QuestPDF จัดรูปอักษรด้วย ligature ("ti" ใน Quotation เป็น glyph เดียว) ซึ่ง PdfPig ถอดเป็น '\0'
        // ⇒ เทียบหลังตัดช่องว่าง/อักษรที่ถอดไม่ได้ และตัดคู่อักษรที่ฟอนต์รวมเป็น ligature ทั้งสองฝั่ง (เทียบทางเดียวกันทั้งคู่ — ไม่ขึ้นกับฟอนต์ในเครื่อง)
        static string Squash(string s) => s.Replace(" ", "").Replace("\0", "").Replace("ti", "").Replace("fi", "").Replace("fl", "").Replace("ff", "");
        var text = Squash(string.Concat(doc.GetPage(1).Letters.Select(l => l.Value)));
        var L = DocumentLabels.For("en");
        Assert.Contains(Squash(L.QtAcceptHeading), text, StringComparison.Ordinal);
        Assert.Contains(Squash("JohnSmith"), text, StringComparison.Ordinal);
        Assert.Contains(Squash("05Nov202614:05"), text, StringComparison.Ordinal);
        Assert.Contains(Squash("DeliveronMonday"), text, StringComparison.Ordinal);

        // ทิศตรงข้าม: เทมเพลตมีช่องลูกค้า (ช่องที่ 3) ⇒ ไม่พิมพ์บล็อกซ้ำ (ข้อมูลอยู่ในช่องลายเซ็นแล้ว)
        var withBox = new EntTemplate { ShowSignature = true, SignatureCount = 3, SignatureLabel3 = "ลูกค้าอนุมัติ" };
        var pdf2 = QuestPDF.Fluent.Document.Create(c => c.Page(p =>
            p.Content().Column(col =>
            {
                col.Item().Text("x");
                PdfGenerationService.ComposeQuotationAcceptanceBlock(col, withBox, signers, "en");
            }))).GeneratePdf();
        using var doc2 = UglyToad.PdfPig.PdfDocument.Open(pdf2);
        Assert.DoesNotContain(Squash("JohnSmith"), Squash(string.Concat(doc2.GetPage(1).Letters.Select(l => l.Value))), StringComparison.Ordinal);
    }

    [Fact]
    public void สองrenderer_อ่านช่องการยอมรับจากsignersตัวเดียว_ไม่อ่านDocumentเอง()
    {
        var root = RepoRoot();
        var html = File.ReadAllText(Path.Combine(root, "Accounting/Services/Implementations/PdfGenerationService.cs"));
        var quest = File.ReadAllText(Path.Combine(root, "Accounting/Services/Implementations/PdfGenerationService.DocumentRenderer.cs"));
        Assert.Contains("L.QtAcceptedOnline(", html);
        Assert.Contains("L.QtAcceptedOnline(", quest);
        Assert.Contains("QuotationAcceptanceBlockHtml(", html);
        Assert.Contains("ComposeQuotationAcceptanceBlock(col, template, signers, lang)", quest);
        // renderer ห้ามอ่านคอลัมน์การยอมรับจาก Document ตรง (ค่ามาจาก ResolveSignersAsync เท่านั้น)
        Assert.DoesNotContain("doc.QuotationAccept", quest);
    }

    // ───────────── ฝ่ายค้านรอบ 239 ─────────────

    [Fact]
    public void S1_RGBAขาวทึบ_ว่าง_แต่RGBAมีเส้นดำ_ผ่าน()
    {
        var white = Png(400, 150, 6, (_, _) => new byte[] { 255, 255, 255, 255 });
        Assert.Equal(SignatureImageValidator.Code.Blank, VB(white).Code);
        var inked = Png(400, 150, 6, (x, y) => y is >= 70 and <= 74 && x > 20 && x < 380
            ? new byte[] { 0, 0, 0, 255 } : new byte[] { 255, 255, 255, 255 });
        Assert.True(VB(inked).Ok);
        // เทา+อัลฟา ขาวทึบ ⇒ ว่างเช่นกัน
        Assert.Equal(SignatureImageValidator.Code.Blank,
            VB(Png(400, 150, 4, (_, _) => new byte[] { 255, 255 })).Code);
    }

    [Fact]
    public void ตอนอ่านไฟล์ที่เก็บไว้_ตรวจแค่magicและขนาด()
    {
        Assert.True(SignatureImageValidator.LooksLikeStoredPng(DrawnSignature()));
        Assert.False(SignatureImageValidator.LooksLikeStoredPng(Encoding.ASCII.GetBytes(new string('x', 100))));
        Assert.False(SignatureImageValidator.LooksLikeStoredPng(null));
        Assert.False(SignatureImageValidator.LooksLikeStoredPng(new byte[SignatureImageValidator.MaxBytes + 1]));
    }

    [Fact]
    public void CleanText_ตัดsurrogateที่ไม่มีคู่_คงอีโมจิครบคู่()
    {
        Assert.Equal("ab", QuotationAcceptance.CleanText("a\uD83Db", 50));
        Assert.Equal("ab", QuotationAcceptance.CleanText("a\uDE00b", 50));
        Assert.Equal("a\uD83D\uDE00", QuotationAcceptance.CleanText("a\uD83D\uDE00", 50));
        // ตัดความยาวไม่ผ่ากลางคู่
        Assert.Equal("a", QuotationAcceptance.CleanText("a\uD83D\uDE00", 2));
    }

    private static EntDocument AcceptedQuotation(out List<Accounting.Models.Entities.DocumentLine> lines)
    {
        var d = new EntDocument
        {
            Id = Guid.NewGuid(), CompanyId = Guid.NewGuid(), ContactId = Guid.NewGuid(), DocumentNumber = "QT-1",
            DocumentType = Accounting.Models.Enums.DocumentType.Quotation, DocumentDate = new DateTime(2026, 11, 1),
            SubTotal = 1000m, TotalAmount = 1070m, VatAmount = 70m, Currency = "THB",
            QuotationAcceptedAt = new DateTime(2026, 11, 5, 7, 5, 0, DateTimeKind.Utc), QuotationAcceptedBy = "คุณเอ",
        };
        lines = new() { new() { LineOrder = 1, Description = "งาน", Quantity = 1, UnitPrice = 1000m, Amount = 1000m } };
        d.QuotationAcceptContentHash = DocumentSignedContentHash(d, lines);
        return d;
    }

    private static string DocumentSignedContentHash(EntDocument d, List<Accounting.Models.Entities.DocumentLine> lines)
        => DocumentSignedContent.Hash(d, lines);

    [Fact]
    public void S3_เนื้อหาตรง_พิมพ์รูป_เนื้อหาเปลี่ยน_ไม่พิมพ์รูปและติดป้าย()
    {
        var png = DrawnSignature();
        var d = AcceptedQuotation(out var lines);
        var ok = PdfGenerationService.QuotationAcceptanceSigner(d, lines, png);
        Assert.Equal(png, ok.SignatureImageBytes);
        Assert.False(ok.AcceptContentChanged);
        Assert.True(QuotationAcceptance.ContentMatches(d, lines));

        d.TotalAmount = 2140m;   // แก้ยอดหลังลูกค้ายอมรับ
        var changed = PdfGenerationService.QuotationAcceptanceSigner(d, lines, png);
        Assert.Null(changed.SignatureImageBytes);
        Assert.Null(changed.SignatureImageDataUri);
        Assert.True(changed.AcceptContentChanged);
        Assert.Equal("คุณเอ", changed.Name);
        Assert.False(QuotationAcceptance.ContentMatches(d, lines));
        var html = PdfGenerationService.QuotationAcceptanceBlockHtml(changed, d.QuotationAcceptedAt!.Value, DocumentLabels.For("th"));
        Assert.Contains(DocumentLabels.For("th").QtAcceptContentChanged, html);
        Assert.DoesNotContain("<img", html);
        Assert.Contains("Content changed", DocumentLabels.For("en").QtAcceptContentChanged);
    }

    [Fact]
    public void S3_ไม่มีhash_ใบก่อนรอบ239_พิมพ์ตามเดิม_และไม่โหลดบรรทัด_ไม่ใช่ไม่ตรง()
    {
        var png = DrawnSignature();
        var d = AcceptedQuotation(out var lines);
        Assert.Null(QuotationAcceptance.ContentMatches(d, new List<Accounting.Models.Entities.DocumentLine>()));
        d.QuotationAcceptContentHash = null;
        d.TotalAmount = 99m;
        var s = PdfGenerationService.QuotationAcceptanceSigner(d, lines, png);
        Assert.Equal(png, s.SignatureImageBytes);
        Assert.False(s.AcceptContentChanged);
        Assert.Null(QuotationAcceptance.ContentMatches(d, lines));
    }

    [Fact]
    public void N_ยอมรับแบบไม่เซ็น_ไม่ทับรูปลายเซ็นลูกค้าเดิม_ทิศตรงข้ามเซ็นแล้วทับ()
    {
        var oldSig = DrawnSignature(300, 100);
        PdfGenerationService.DocumentSigner Ext() => new(PdfGenerationService.ImageDataUri(oldSig), oldSig, "ลูกค้าเซ็นผ่านขั้นอนุมัติ", null);
        var signers = new List<PdfGenerationService.DocumentSigner> { new(null, null, "a", null), new(null, null, "b", null), Ext() };
        PdfGenerationService.PlaceQuotationAcceptance(signers, AcceptedSigner("คุณเอ", null, null));
        Assert.Equal("ลูกค้าเซ็นผ่านขั้นอนุมัติ", signers[2].Name);
        Assert.Equal(oldSig, signers[2].SignatureImageBytes);

        var withSig = new List<PdfGenerationService.DocumentSigner> { new(null, null, "a", null), new(null, null, "b", null), Ext() };
        PdfGenerationService.PlaceQuotationAcceptance(withSig, AcceptedSigner("คุณเอ", null, DrawnSignature()));
        Assert.Equal("คุณเอ", withSig[2].Name);

        // ไม่มีช่องลูกค้าเดิม ⇒ เพิ่มเป็นช่องที่ 3 แม้ไม่เซ็น (ชื่อ/เวลายังต้องขึ้นกระดาษ)
        var two = new List<PdfGenerationService.DocumentSigner> { new(null, null, "a", null), new(null, null, "b", null) };
        PdfGenerationService.PlaceQuotationAcceptance(two, AcceptedSigner("คุณเอ", null, null));
        Assert.Equal(3, two.Count);
        Assert.Equal("คุณเอ", two[2].Name);
    }

    [Fact]
    public void N_PDPA_พ้นอายุเก็บ_ล้างข้อมูลส่วนบุคคล_ในอายุเก็บ_legalhold()
    {
        var now = new DateTime(2032, 1, 1, 0, 0, 0, DateTimeKind.Utc);
        var d = AcceptedQuotation(out _);
        d.QuotationAcceptSignaturePath = "/x/quotation-acceptances/a/b.png";
        d.QuotationAcceptIp = "1.2.3.4"; d.QuotationAcceptNote = "n"; d.QuotationAcceptUserAgentHash = "u";
        d.DeliverySignedBy = "ผู้รับ"; d.DeliverySignedAt = d.QuotationAcceptedAt; d.DeliverySignatureBase64 = "data:image/png;base64,AA==";
        Assert.True(QuotationAcceptance.UnderLegalHold(d, new DateTime(2031, 10, 31, 0, 0, 0, DateTimeKind.Utc)));   // ภายใน 5 ปี
        Assert.False(QuotationAcceptance.UnderLegalHold(d, now));
        d.RetentionUntil = new DateTime(2033, 1, 1);   // MAX(retention) ชนะ
        Assert.True(QuotationAcceptance.UnderLegalHold(d, now));

        var (changed, file) = QuotationAcceptance.ErasePii(d);
        Assert.True(changed);
        Assert.Equal("/x/quotation-acceptances/a/b.png", file);
        Assert.Equal(QuotationAcceptance.Anonymized, d.QuotationAcceptedBy);
        Assert.Null(d.QuotationAcceptIp); Assert.Null(d.QuotationAcceptNote); Assert.Null(d.QuotationAcceptSignaturePath);
        Assert.Null(d.DeliverySignatureBase64); Assert.Equal(QuotationAcceptance.Anonymized, d.DeliverySignedBy);
        Assert.NotNull(d.QuotationAcceptedAt);           // เวลา/hash เนื้อหาไม่ระบุตัวบุคคล — คงไว้
        Assert.NotNull(d.QuotationAcceptContentHash);
        Assert.False(QuotationAcceptance.ErasePii(d).Changed);   // ซ้ำ = ไม่มีอะไรเปลี่ยน
    }

    private static string RepoRoot()
    {
        var dir = new DirectoryInfo(AppContext.BaseDirectory);
        while (dir != null && !File.Exists(Path.Combine(dir.FullName, "Accounting.sln"))) dir = dir.Parent;
        return dir?.FullName ?? throw new InvalidOperationException("ไม่พบรากเรพ");
    }
}
