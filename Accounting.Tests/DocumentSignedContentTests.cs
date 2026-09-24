using Accounting.Helpers;
using Accounting.Models.Entities;
using Xunit;

namespace Accounting.Tests;

/// <summary>
/// รอบ 193 ฝ่ายค้านรอบสาม R3-3 — <see cref="DocumentSignedContent"/>: ลายเซ็นลูกค้าเดิมใช้ซ้ำได้เฉพาะเมื่อเนื้อหาเอกสารไม่เปลี่ยนตั้งแต่เซ็น
///
/// <para><b>ครึ่งที่ 1</b> (ที่เคยผิด): ลูกค้าเซ็นใบเสนอราคา → อนุมัติไม่ผ่าน → ผู้ใช้แก้ราคา/จำนวน/ผู้ซื้อ/เงื่อนไข → เรียกซ้ำ ⇒
/// เดิมใช้ลายเซ็นเดิม · ตอนนี้ hash ต่าง ⇒ ต้องเซ็นใหม่ · แถวเก่าไม่มี hash ⇒ ใช้ซ้ำไม่ได้ · ลายเซ็น/ชื่อในคำขอใหม่ต่าง ⇒ บันทึกใหม่</para>
/// <para><b>ครึ่งที่ 2</b> (ห้ามแตะ): เรียกซ้ำด้วยเนื้อหาเดิม + ลายเซ็นเดิม ⇒ ใช้ซ้ำได้ (ไม่สร้างลายเซ็นซ้ำ) · ลำดับบรรทัดที่ DB คืน /
/// scale ของทศนิยม / ช่องว่างหัวท้าย / ผังบัญชีภายใน ไม่ใช่การแก้เนื้อหา</para>
/// </summary>
public class DocumentSignedContentTests
{
    private static readonly Guid Buyer = Guid.Parse("11111111-1111-1111-1111-111111111111");

    private static (Document Doc, List<DocumentLine> Lines) Quote()
    {
        var doc = new Document
        {
            ContactId = Buyer, Currency = "THB", ExchangeRate = 1m,
            SubTotal = 1500m, VatAmount = 105m, TotalAmount = 1605m,
            DueDate = new DateTime(2026, 10, 31), CreditDays = 30, PaymentTerms = "Net 30",
            Notes = "ราคานี้ยืนราคา 30 วัน",
        };
        var lines = new List<DocumentLine>
        {
            new() { LineOrder = 1, Description = "ค่าติดตั้ง", Quantity = 1m, Unit = "งาน", UnitPrice = 1000m, Amount = 1000m, VatRate = 7m, VatAmount = 70m },
            new() { LineOrder = 2, Description = "สายไฟ", Quantity = 10m, Unit = "เมตร", UnitPrice = 50m, Amount = 500m, VatRate = 7m, VatAmount = 35m },
        };
        return (doc, lines);
    }

    private static string H((Document Doc, List<DocumentLine> Lines) q) => DocumentSignedContent.Hash(q.Doc, q.Lines);

    // ── ครึ่งที่ 1: เนื้อหาเปลี่ยน ⇒ hash ต่าง ⇒ ต้องเซ็นใหม่ ──

    [Fact]
    public void EditedUnitPrice_AfterSigning_RequiresNewSignature()
    {
        var q = Quote();
        var signed = H(q);
        q.Lines[0].UnitPrice = 900m; q.Lines[0].Amount = 900m;
        q.Doc.SubTotal = 1400m; q.Doc.VatAmount = 98m; q.Doc.TotalAmount = 1498m;

        var now = H(q);
        Assert.NotEqual(signed, now);
        Assert.False(DocumentSignedContent.CanReuseSignature(signed, now, "SIG", "คุณเอ", "SIG", "คุณเอ"));
    }

    [Fact]
    public void EditedQuantity_OnlyLineChanged_StillDetected()
    {
        var q = Quote();
        var signed = H(q);
        q.Lines[1].Quantity = 12m;   // แม้ผู้แก้ลืมคำนวณยอดใหม่ — จำนวนเองก็เป็นเนื้อหาที่ลูกค้าเซ็น
        Assert.NotEqual(signed, H(q));
    }

    [Fact]
    public void ChangedBuyer_RequiresNewSignature()
    {
        var q = Quote();
        var signed = H(q);
        q.Doc.ContactId = Guid.Parse("22222222-2222-2222-2222-222222222222");
        Assert.NotEqual(signed, H(q));
    }

    [Theory]
    [InlineData("due")]
    [InlineData("credit")]
    [InlineData("terms")]
    [InlineData("notes")]
    [InlineData("addline")]
    [InlineData("removeline")]
    [InlineData("billdiscount")]
    [InlineData("vatinclusive")]
    public void ChangedTermsOrLines_RequiresNewSignature(string what)
    {
        var q = Quote();
        var signed = H(q);
        switch (what)
        {
            case "due": q.Doc.DueDate = new DateTime(2026, 11, 30); break;
            case "credit": q.Doc.CreditDays = 60; break;
            case "terms": q.Doc.PaymentTerms = "Net 60"; break;
            case "notes": q.Doc.Notes = "ยืนราคา 60 วัน"; break;
            case "addline": q.Lines.Add(new DocumentLine { LineOrder = 3, Description = "ค่าเดินทาง", Quantity = 1m, UnitPrice = 0m }); break;
            case "removeline": q.Lines.RemoveAt(1); break;
            case "billdiscount": q.Doc.BillDiscountAmount = 100m; break;
            case "vatinclusive": q.Doc.PricesIncludeVat = true; break;
        }
        Assert.NotEqual(signed, H(q));
    }

    [Fact]
    public void LegacyApprovalWithoutHash_CannotBeReused()
    {
        var now = H(Quote());
        Assert.False(DocumentSignedContent.CanReuseSignature(null, now, "SIG", "คุณเอ", "SIG", "คุณเอ"));
        Assert.False(DocumentSignedContent.CanReuseSignature("", now, "SIG", "คุณเอ", "SIG", "คุณเอ"));
    }

    [Fact]
    public void SameContent_DifferentSignatureOrSigner_IsANewSignature_NotDroppedSilently()
    {
        var h = H(Quote());
        Assert.False(DocumentSignedContent.CanReuseSignature(h, h, "SIG-OLD", "คุณเอ", "SIG-NEW", "คุณเอ"));
        Assert.False(DocumentSignedContent.CanReuseSignature(h, h, "SIG", "คุณเอ", "SIG", "คุณบี"));
    }

    [Fact]
    public void DeletedLine_IsNotContent()
    {
        var q = Quote();
        var before = H(q);
        q.Lines.Add(new DocumentLine { LineOrder = 3, Description = "ลบแล้ว", Quantity = 1m, UnitPrice = 999m, Amount = 999m, IsDeleted = true });
        Assert.Equal(before, H(q));
    }

    [Fact]
    public void FieldBoundaries_CannotShiftBetweenFields()
    {
        // "a|b" + "c" ต้องไม่เท่ากับ "a" + "b|c" — ความยาวนำหน้าทุกช่อง
        var a = Quote(); a.Lines[0].Description = "ค่า|ติดตั้ง"; a.Lines[0].Unit = "งาน";
        var b = Quote(); b.Lines[0].Description = "ค่า"; b.Lines[0].Unit = "ติดตั้ง|งาน";
        Assert.NotEqual(H(a), H(b));
    }

    // ── ครึ่งที่ 2: เนื้อหาเดิม ⇒ ใช้ซ้ำได้ · สิ่งที่ไม่ใช่เนื้อหาไม่ทำให้ต้องเซ็นใหม่ ──

    [Fact]
    public void SameContent_SameSignature_Retry_ReusesSignature()
    {
        var signed = H(Quote());
        var now = H(Quote());   // โหลดใหม่จาก DB — เนื้อหาเดิม
        Assert.Equal(signed, now);
        Assert.True(DocumentSignedContent.CanReuseSignature(signed, now, "SIG", "คุณเอ", "SIG", " คุณเอ "));
    }

    [Fact]
    public void LineOrderFromDatabase_DoesNotMatter()
    {
        var a = Quote();
        var b = Quote(); b.Lines.Reverse();
        Assert.Equal(H(a), H(b));
    }

    [Fact]
    public void DecimalScaleAndWhitespace_AreNotEdits()
    {
        var a = Quote();
        var b = Quote();
        b.Lines[0].UnitPrice = 1000.00m; b.Lines[0].Quantity = 1.000m; b.Doc.SubTotal = 1500.00m;
        b.Lines[1].Description = "  สายไฟ  "; b.Doc.Notes = "ราคานี้ยืนราคา 30 วัน\r\n";
        Assert.Equal(H(a), H(b));
    }

    [Fact]
    public void InternalBookkeeping_GlAccountChange_DoesNotRequireResign()
    {
        var a = Quote();
        var b = Quote(); b.Lines[0].AccountId = Guid.NewGuid();
        Assert.Equal(H(a), H(b));
    }

    [Fact]
    public void Hash_IsVersionedSha256()
    {
        var h = H(Quote());
        Assert.StartsWith(DocumentSignedContent.Version + ":", h);
        Assert.Equal(DocumentSignedContent.Version.Length + 1 + 64, h.Length);
    }
}
