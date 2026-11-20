using Accounting.Helpers;
using Xunit;

namespace Accounting.Tests;

/// <summary>รอบ 278 — ตัวตัดสิน "ช่องผู้จัดทำกับช่องผู้มีอำนาจลงนามเป็นคนเดียวกันไหม" (<see cref="SignatureSlotIdentity"/>) · กุญแจเท่านั้น ห้ามเทียบความคล้ายชื่อ ·
/// ที่มา: QT-20261119-0001 ช่อง 1 "K.นัท" (มีลายเซ็นจาก payload) · ช่อง 2 "ณัฐพล ทรัพย์บวรโรจน์" (ผู้ออก API key · ไม่มีลายเซ็น)</summary>
public class SignatureSlotIdentityR278Tests
{
    private static readonly Guid Nat = Guid.Parse("11111111-1111-1111-1111-111111111111");
    private static readonly Guid Other = Guid.Parse("22222222-2222-2222-2222-222222222222");

    [Fact]
    public void ชื่อเล่นที่ผู้ใช้ประกาศในโปรไฟล์_ตรงทั้งคำ_ถือเป็นคนเดียวกัน()
    {
        var r = SignatureSlotIdentity.Link(new(null, null, "K.นัท"), new(Nat, "K.นัท\nนัท"));
        Assert.Equal(SignatureSlotIdentity.LinkKind.ProfileAlias, r.Kind);
        Assert.Equal(Nat, r.UserId);
        // ตัดช่องว่าง/ตัวพิมพ์เท่านั้น
        Assert.Equal(SignatureSlotIdentity.LinkKind.ProfileAlias,
            SignatureSlotIdentity.Link(new(null, null, "  k.นัท "), new(Nat, "K.นัท")).Kind);
    }

    [Fact]
    public void ชื่อคล้ายแต่ไม่มีกุญแจ_ไม่รวม()
    {
        // ไม่ได้ประกาศชื่อเล่น ⇒ "K.นัท" กับ "ณัฐพล" เทียบด้วยชื่อไม่ได้
        Assert.Equal(SignatureSlotIdentity.LinkKind.None, SignatureSlotIdentity.Link(new(null, null, "K.นัท"), new(Nat, null)).Kind);
        // ใกล้เคียงแต่ไม่ตรงทั้งคำ ⇒ ไม่ fuzzy
        Assert.Equal(SignatureSlotIdentity.LinkKind.None, SignatureSlotIdentity.Link(new(null, null, "K. นัท"), new(Nat, "K.นัท")).Kind);
        Assert.Equal(SignatureSlotIdentity.LinkKind.None, SignatureSlotIdentity.Link(new(null, null, "K.นัทธ์"), new(Nat, "K.นัท")).Kind);
        // ชื่อตรงกับชื่อจริงของผู้ใช้ช่อง 2 แต่ไม่ได้ผูก ⇒ ไม่รวม (ชื่อซ้ำกันได้ในโลกจริง)
        Assert.Equal(SignatureSlotIdentity.LinkKind.None,
            SignatureSlotIdentity.Link(new(null, null, "ณัฐพล ทรัพย์บวรโรจน์"), new(Nat, null)).Kind);
    }

    [Fact]
    public void PreparerUserId_ชนะ_และระบุเป็นคนอื่นแล้วไม่ตกไปเทียบชื่อเล่น()
    {
        Assert.Equal(SignatureSlotIdentity.LinkKind.PreparerUserId,
            SignatureSlotIdentity.Link(new(null, Nat, "ใครก็ได้"), new(Nat, null)).Kind);
        Assert.Equal(SignatureSlotIdentity.LinkKind.None,
            SignatureSlotIdentity.Link(new(null, Other, "K.นัท"), new(Nat, "K.นัท")).Kind);
    }

    [Fact]
    public void ผู้ใช้เดียวกัน_กับ_คนละคน()
    {
        Assert.Equal(SignatureSlotIdentity.LinkKind.SameUser, SignatureSlotIdentity.Link(new(Nat, null, null), new(Nat, null)).Kind);
        Assert.Equal(SignatureSlotIdentity.LinkKind.None, SignatureSlotIdentity.Link(new(Other, null, null), new(Nat, "K.นัท")).Kind);
        // ช่อง 0 เป็นผู้ใช้ในระบบ (ไม่ใช่ payload) ⇒ ชื่อเล่นไม่ใช้
        Assert.Equal(SignatureSlotIdentity.LinkKind.None, SignatureSlotIdentity.Link(new(Other, null, "K.นัท"), new(Nat, "K.นัท")).Kind);
    }

    [Fact]
    public void ช่องลูกค้า_และช่อง2ที่ไม่ใช่ผู้ใช้_ไม่แตะ()
    {
        Assert.Equal(SignatureSlotIdentity.LinkKind.None, SignatureSlotIdentity.Link(new(null, null, "K.นัท"), new(Nat, "K.นัท", IsCustomer: true)).Kind);
        Assert.Equal(SignatureSlotIdentity.LinkKind.None, SignatureSlotIdentity.Link(new(Nat, null, null, IsCustomer: true), new(Nat, null)).Kind);
        Assert.Equal(SignatureSlotIdentity.LinkKind.None, SignatureSlotIdentity.Link(new(null, Nat, "K.นัท"), new(null, "K.นัท")).Kind);
    }

    [Fact]
    public void รวม_ใบQT_ลายเซ็นเติมช่องว่าง_ชื่อตามโปรไฟล์ทั้งสองช่อง()
    {
        var plan = SignatureSlotIdentity.Merge(new("K.นัท", null, true), new("ณัฐพล ทรัพย์บวรโรจน์", null, false),
            "ณัฐพล ทรัพย์บวรโรจน์", "กรรมการผู้จัดการ");
        Assert.Equal("ณัฐพล ทรัพย์บวรโรจน์", plan.Name0);
        Assert.Equal("ณัฐพล ทรัพย์บวรโรจน์", plan.Name1);
        Assert.True(plan.CopyImage0To1);
        Assert.False(plan.CopyImage1To0);
        Assert.Equal("กรรมการผู้จัดการ", plan.Title0);
        Assert.Equal("กรรมการผู้จัดการ", plan.Title1);
    }

    [Fact]
    public void ชื่อครบกว่า_คือชื่อโปรไฟล์_ไม่ใช่ความยาวสตริง()
    {
        // ช่อง 0 ยาวกว่า แต่ชื่อที่เจ้าตัวรับรองคือชื่อโปรไฟล์
        var plan = SignatureSlotIdentity.Merge(new("ณัฐพล ทรัพย์บวรโรจน์ (ฝ่ายขายภาคตะวันออก)", "ฝ่ายขาย", false), new("ณัฐพล", null, true),
            "ณัฐพล ท.", null);
        Assert.Equal("ณัฐพล ท.", plan.Name0);
        Assert.Equal("ณัฐพล ท.", plan.Name1);
        Assert.Equal("ฝ่ายขาย", plan.Title0);   // ตำแหน่งของช่องตัวเองคงไว้
        Assert.Null(plan.Title1);
        Assert.True(plan.CopyImage1To0);
        // โปรไฟล์ไม่มีชื่อ ⇒ คงชื่อเดิมของแต่ละช่อง (ไม่เลือกตัวที่ยาวกว่า)
        var keep = SignatureSlotIdentity.Merge(new("K.นัท", null, true), new("ณัฐพล ทรัพย์บวรโรจน์", null, true), null, null);
        Assert.Equal("K.นัท", keep.Name0);
        Assert.Equal("ณัฐพล ทรัพย์บวรโรจน์", keep.Name1);
        Assert.False(keep.CopyImage0To1);
        Assert.False(keep.CopyImage1To0);   // ทั้งสองมีรูป ⇒ ใครของมัน
    }

    [Fact]
    public void ชื่อแสดงตามโปรไฟล์()
    {
        Assert.Equal("นัท", SignatureSlotIdentity.ProfileDisplayName(" นัท ", "ณัฐพล ทรัพย์บวรโรจน์"));
        Assert.Equal("ณัฐพล ทรัพย์บวรโรจน์", SignatureSlotIdentity.ProfileDisplayName(" ", "ณัฐพล ทรัพย์บวรโรจน์"));
        Assert.Null(SignatureSlotIdentity.ProfileDisplayName(null, ""));
    }

    [Fact]
    public void เก็บชื่ออื่น_ตัดซ้ำ_ล้างได้_เกินเพดานบอกเหตุ()
    {
        Assert.Equal("K.นัท\nนัท", SignatureSlotIdentity.SanitizeForStore(" K.นัท ,k.นัท\nนัท;"));
        Assert.Null(SignatureSlotIdentity.SanitizeForStore(""));
        Assert.Null(SignatureSlotIdentity.SanitizeForStore(" \n "));
        var many = string.Join("\n", Enumerable.Range(1, SignatureSlotIdentity.MaxAliases + 1).Select(i => "ชื่อ" + i));
        Assert.Throws<BusinessRuleException>(() => SignatureSlotIdentity.SanitizeForStore(many));
        Assert.Throws<BusinessRuleException>(() => SignatureSlotIdentity.SanitizeForStore(new string('ก', SignatureSlotIdentity.MaxAliasLength + 1)));
        // คอลัมน์ varchar(1100) ต้องพอสำหรับเพดานเต็ม (10 ชื่อ × 100 ตัวอักษร + ตัวคั่น)
        var full = SignatureSlotIdentity.SanitizeForStore(string.Join("\n",
            Enumerable.Range(0, SignatureSlotIdentity.MaxAliases).Select(i => new string('ข', SignatureSlotIdentity.MaxAliasLength - 1) + (char)('0' + i))));
        Assert.NotNull(full);
        Assert.True(full!.Length <= 1100, $"ยาว {full.Length}");
    }
}
