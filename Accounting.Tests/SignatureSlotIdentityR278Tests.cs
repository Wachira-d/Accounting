using Accounting.Helpers;
using Accounting.Models.Entities;
using Accounting.Models.Enums;
using Xunit;

namespace Accounting.Tests;

/// <summary>รอบ 278 — ตัวตัดสิน "ช่องผู้จัดทำกับช่องผู้มีอำนาจลงนามเป็นคนเดียวกันไหม" (<see cref="SignatureSlotIdentity"/>) · กุญแจที่แช่แข็งตอนออกเอกสารเท่านั้น ·
/// ฝ่ายค้าน HIGH-1 (ไม่มีทางคัดลอกรูปเข้าช่อง 1) · HIGH-2 (ชื่อเล่น: คำกลาง/ชื่อของคนอื่น) · HIGH-3 (ใบที่ปรับผู้ลงนามได้)</summary>
public class SignatureSlotIdentityR278Tests
{
    private static readonly Guid Nat = Guid.Parse("11111111-1111-1111-1111-111111111111");
    private static readonly Guid Other = Guid.Parse("22222222-2222-2222-2222-222222222222");
    private static readonly (Guid, string?, string?, bool)[] None = Array.Empty<(Guid, string?, string?, bool)>();

    [Fact]
    public void snapshot_PreparerUserId_ชนะ_ชื่อเล่นไม่ถูกใช้()
    {
        var snap = SignatureSlotIdentity.BuildSnapshot(Other, ("พนักงานขาย", null, false), new[] { (Nat, (string?)"ณัฐพล", (string?)null, false) });
        var c = Assert.Single(snap.C);
        Assert.Equal(Other, c.UserId);
        Assert.Equal(SignatureSlotIdentity.KindPreparerUserId, c.Kind);
        Assert.Null(SignatureSlotIdentity.Link(snap, Nat));   // ช่อง 1 = ณัฐพล ≠ ผู้ที่ผูก ⇒ ไม่รวม
    }

    [Fact]
    public void snapshot_เจ้าของชื่อเล่นคนเดียว_รวม_สองคน_กำกวม_ไม่รวม()
    {
        var one = SignatureSlotIdentity.BuildSnapshot(null, null, new[] { (Nat, (string?)"ณัฐพล ทรัพย์บวรโรจน์", (string?)"กรรมการ", false) }, payloadHasImage: true);
        var c = SignatureSlotIdentity.Link(one, Nat);
        Assert.NotNull(c);
        Assert.Equal("ณัฐพล ทรัพย์บวรโรจน์", c!.Name);
        Assert.Equal(SignatureSlotIdentity.ImagePayload, c.ImageSource);   // 278-9: โปรไฟล์ไม่มีรูป + payload มี ⇒ payload
        var two = SignatureSlotIdentity.BuildSnapshot(null, null, new[] { (Nat, (string?)"ก", (string?)null, false), (Other, (string?)"ข", (string?)null, false) });
        Assert.Empty(two.C);
        Assert.Empty(SignatureSlotIdentity.BuildSnapshot(null, null, None).C);
    }

    [Fact]
    public void link_ไม่มีsnapshot_ช่องลูกค้า_ช่อง1ไม่ใช่ผู้ใช้_ไม่รวม()
    {
        var snap = SignatureSlotIdentity.BuildSnapshot(Nat, ("ณัฐพล", null, true), None);
        Assert.Null(SignatureSlotIdentity.Link(null, Nat));
        Assert.Null(SignatureSlotIdentity.Link(snap, null));
        Assert.Null(SignatureSlotIdentity.Link(snap, Nat, slot1IsCustomer: true));
        Assert.Null(SignatureSlotIdentity.Link(snap, Nat, slot0IsCustomer: true));
        Assert.Null(SignatureSlotIdentity.Link(SignatureSlotIdentity.Parse(SignatureSlotIdentity.EmptySnapshotJson), Nat));
        Assert.NotNull(SignatureSlotIdentity.Link(SignatureSlotIdentity.Parse(SignatureSlotIdentity.Serialize(snap)), Nat));   // round-trip
        Assert.Null(SignatureSlotIdentity.Parse("{oops"));
    }

    [Fact]
    public void แหล่งรูป_278_9_โปรไฟล์เป็นหลัก_ไม่มีจึงใช้payload()
    {
        static string? Src(bool profile, bool payload) =>
            SignatureSlotIdentity.BuildSnapshot(Nat, ("ณัฐพล", null, profile), None, payloadHasImage: payload).C[0].ImageSource;
        Assert.Equal(SignatureSlotIdentity.ImageProfile, Src(true, true));
        Assert.Equal(SignatureSlotIdentity.ImageProfile, Src(true, false));
        Assert.Equal(SignatureSlotIdentity.ImagePayload, Src(false, true));
        Assert.Equal(SignatureSlotIdentity.ImageNone, Src(false, false));
    }

    [Fact]
    public void merge_ชื่อโปรไฟล์ทั้งสองช่อง_ไม่ใช่ความยาว_แหล่งรูปตามที่แช่แข็ง()
    {
        var plan = SignatureSlotIdentity.Merge(new("K.นัท", null, true), new("ณัฐพล ทรัพย์บวรโรจน์", null, false), "ณัฐพล ทรัพย์บวรโรจน์", "กรรมการผู้จัดการ",
            SignatureSlotIdentity.ImagePayload, profileHasImageNow: true);
        Assert.Equal("ณัฐพล ทรัพย์บวรโรจน์", plan.Name0);
        Assert.Equal("ณัฐพล ทรัพย์บวรโรจน์", plan.Name1);
        Assert.Equal(SignatureSlotIdentity.ImagePayload, plan.ImageSource);   // แช่แข็งไว้ ⇒ ไม่เปลี่ยนตามโปรไฟล์ที่อัปโหลดทีหลัง
        Assert.Equal("กรรมการผู้จัดการ", plan.Title1);
        var shorter = SignatureSlotIdentity.Merge(new("ณัฐพล ทรัพย์บวรโรจน์ (ฝ่ายขายภาคตะวันออก)", "ฝ่ายขาย", false), new("ณัฐพล", null, true), "ณัฐพล ท.", null,
            null, profileHasImageNow: true);
        Assert.Equal("ณัฐพล ท.", shorter.Name0);
        Assert.Equal("ฝ่ายขาย", shorter.Title0);
        Assert.Equal(SignatureSlotIdentity.ImageProfile, shorter.ImageSource);   // snapshot รุ่นก่อน (null) ⇒ กติกาเดียวกันตอนพิมพ์
        var keep = SignatureSlotIdentity.Merge(new("K.นัท", null, true), new("ณัฐพล ทรัพย์บวรโรจน์", null, true), null, null, "bogus", profileHasImageNow: false);
        Assert.Equal("K.นัท", keep.Name0);
        Assert.Equal("ณัฐพล ทรัพย์บวรโรจน์", keep.Name1);
        Assert.Equal(SignatureSlotIdentity.ImagePayload, keep.ImageSource);
    }

    [Fact]
    public void ชื่อเล่น_คำกลาง_ชื่อของคนอื่น_ใกล้เคียงไม่นับ()
    {
        var others = new string?[] { "ณัฐพล ทรัพย์บวรโรจน์", "นัท", null };
        Assert.NotNull(SignatureSlotIdentity.AliasProblem("ณัฐพล  ทรัพย์บวรโรจน์", others));   // ชื่อของคนอื่น (ช่องว่างไม่ช่วย)
        Assert.NotNull(SignatureSlotIdentity.AliasProblem("นัท", others));                    // ชื่อเล่นของคนอื่น
        Assert.NotNull(SignatureSlotIdentity.AliasProblem("บัญชี", others));
        Assert.NotNull(SignatureSlotIdentity.AliasProblem("Admin", others));
        Assert.NotNull(SignatureSlotIdentity.AliasProblem("ฝ่ายขาย 2", others));
        Assert.NotNull(SignatureSlotIdentity.AliasProblem(" ", others));
        Assert.NotNull(SignatureSlotIdentity.AliasProblem(new string('ก', SignatureSlotIdentity.MaxAliasLength + 1), others));
        Assert.Null(SignatureSlotIdentity.AliasProblem("K.นัท", others));
        Assert.Null(SignatureSlotIdentity.AliasProblem("นัทธ์", others));   // ไม่ fuzzy — ต่างก็คือต่าง
    }

    [Fact]
    public void อ่านรายการชื่อเล่น_ตัดซ้ำ_เกินเพดานบอกเหตุ()
    {
        Assert.Equal(new[] { "K.นัท", "นัท" }, SignatureSlotIdentity.ParseRequested(" K.นัท ,k.นัท\nนัท;"));
        Assert.Empty(SignatureSlotIdentity.ParseRequested(""));
        var many = string.Join("\n", Enumerable.Range(1, SignatureSlotIdentity.MaxAliases + 1).Select(i => "ชื่อ" + i));
        Assert.Throws<BusinessRuleException>(() => SignatureSlotIdentity.ParseRequested(many));
    }

    [Theory]
    [InlineData(DocumentType.TaxInvoice, false)]
    [InlineData(DocumentType.Receipt, false)]
    [InlineData(DocumentType.CreditNote, false)]
    [InlineData(DocumentType.DebitNote, false)]
    [InlineData(DocumentType.PaymentVoucher, false)]
    [InlineData(DocumentType.Quotation, true)]
    [InlineData(DocumentType.Invoice, true)]
    public void ปุ่มปรับผู้ลงนาม_ตามชนิดเอกสาร(DocumentType t, bool allowed)
    {
        var d = new Document { DocumentType = t, Status = DocumentStatus.Approved };
        Assert.Equal(allowed, SignatureSlotIdentity.RefreshBlockedReason(d) == null);
    }

    [Fact]
    public void ปุ่มปรับผู้ลงนาม_ลูกค้าเซ็นแล้ว_ใบร่าง_ใบยกเลิก_ไม่ได้()
    {
        Assert.NotNull(SignatureSlotIdentity.RefreshBlockedReason(new Document { DocumentType = DocumentType.Quotation, Status = DocumentStatus.Approved, QuotationAcceptedAt = DateTime.UtcNow }));
        Assert.NotNull(SignatureSlotIdentity.RefreshBlockedReason(new Document { DocumentType = DocumentType.Quotation, Status = DocumentStatus.Draft }));
        Assert.NotNull(SignatureSlotIdentity.RefreshBlockedReason(new Document { DocumentType = DocumentType.Quotation, Status = DocumentStatus.Voided }));
        Assert.NotNull(SignatureSlotIdentity.RefreshBlockedReason(new Document { DocumentType = DocumentType.Invoice, Status = DocumentStatus.Approved, IssuedAsCashReceipt = true }));
    }

    [Fact]
    public void ชื่อแสดงตามโปรไฟล์()
    {
        Assert.Equal("นัท", SignatureSlotIdentity.ProfileDisplayName(" นัท ", "ณัฐพล ทรัพย์บวรโรจน์"));
        Assert.Equal("ณัฐพล ทรัพย์บวรโรจน์", SignatureSlotIdentity.ProfileDisplayName(" ", "ณัฐพล ทรัพย์บวรโรจน์"));
        Assert.Null(SignatureSlotIdentity.ProfileDisplayName(null, ""));
    }
}
