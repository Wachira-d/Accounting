using Accounting.Helpers;
using Xunit;

namespace Accounting.Tests;

/// <summary>
/// **ประเภทเงินได้ ม.40 ของบรรทัดเอกสาร — ส่งมา → เก็บ** (รอบ 203)
///
/// <para>เดิม <c>DocumentLineRequest</c> ไม่มีช่องนี้ ⇒ ใบคีย์มือระบุประเภทไม่ได้ (50 ทวิ ได้ "8" เสมอ) และใบจาก OCR
/// ที่ระบบเติมประเภทไว้ เปิดแก้แล้วบันทึก ⇒ ประเภทหายเงียบ ๆ. เทสต์ล็อกทั้งทางที่แก้ (ระบุได้ · แก้แล้วไม่หาย) และทิศตรงข้าม
/// (ล้างได้เมื่อผู้ใช้ตั้งใจ · บรรทัดที่ถูกแก้คำอธิบายไม่ถูกเดาว่าเป็นบรรทัดเดิม · ค่าแปลกถูกปฏิเสธ ไม่ตกแถว 6 เงียบ)</para>
/// </summary>
public class DocumentLineIncomeTypeTests
{
    [Fact]
    public void สร้าง_ระบุค่าเบี้ยประกัน_เก็บรหัส()
        => Assert.Equal("8ins", DocumentLineIncomeType.Decide("8ins", null).Code);

    [Fact]
    public void สร้าง_ไม่ส่ง_ไม่ระบุ()
    {
        var v = DocumentLineIncomeType.Decide(null, null);
        Assert.Null(v.Code);
        Assert.Null(v.Error);
    }

    [Fact]
    public void ส่งเลขมาตรา_เก็บเป็นรหัสมาตรฐาน()
    {
        Assert.Equal("5", DocumentLineIncomeType.Decide("40(5)", null).Code);
        Assert.Equal("4a", DocumentLineIncomeType.Decide("40(4)(ก)", null).Code);
    }

    [Fact]
    public void แก้ไข_ไม่ส่ง_คงค่าเดิม()
        // ฟอร์ม/API รุ่นเก่าที่ไม่รู้จักช่องนี้ ต้องไม่ล้างประเภทที่ OCR เติมไว้
        => Assert.Equal("8tr", DocumentLineIncomeType.Decide(null, "8tr").Code);

    [Fact]
    public void แก้ไข_ส่งค่าว่าง_ล้างค่าเดิม()
        // ทิศตรงข้าม: ผู้ใช้เลือก "ไม่ระบุ" เอง = ล้างจริง
        => Assert.Null(DocumentLineIncomeType.Decide("", "8tr").Code);

    [Fact]
    public void แก้ไข_ส่งค่าใหม่_ชนะค่าเดิม()
        => Assert.Equal("8ins", DocumentLineIncomeType.Decide("8ins", "8tr").Code);

    [Theory]
    [InlineData("zzz")]
    [InlineData("40(4)")]     // ดอกเบี้ยหรือปันผล ชี้ขาดไม่ได้
    public void ค่าที่ชี้ขาดไม่ได้_ถูกปฏิเสธพร้อมเหตุผล(string raw)
    {
        var v = DocumentLineIncomeType.Decide(raw, "8");
        Assert.Null(v.Code);
        Assert.Contains(raw, v.Error);
    }

    [Fact]
    public void ค่าเดิมของบรรทัดเดียวกัน_ลำดับและคำอธิบายตรง()
    {
        var prev = new (string?, string?)[] { ("ค่าเบี้ยประกันรถ", "8ins"), ("ค่าขนส่ง", "8tr") };
        Assert.Equal("8ins", DocumentLineIncomeType.KeptFromPrevious(prev, 0, " ค่าเบี้ยประกันรถ "));
        Assert.Equal("8tr", DocumentLineIncomeType.KeptFromPrevious(prev, 1, "ค่าขนส่ง"));
    }

    [Fact]
    public void บรรทัดที่สลับหรือแก้คำอธิบาย_ไม่เดาว่าเป็นบรรทัดเดิม()
    {
        var prev = new (string?, string?)[] { ("ค่าเบี้ยประกันรถ", "8ins") };
        Assert.Null(DocumentLineIncomeType.KeptFromPrevious(prev, 0, "ค่าซ่อมรถ"));
        Assert.Null(DocumentLineIncomeType.KeptFromPrevious(prev, 1, "ค่าเบี้ยประกันรถ"));   // บรรทัดใหม่
        Assert.Null(DocumentLineIncomeType.KeptFromPrevious(prev, -1, "ค่าเบี้ยประกันรถ"));
    }

    // ══════ ทีม A (กระดานตัดสิน 2026-10-13 · A11): รหัสเก่าที่ตารางชี้ขาดไม่ได้ ไม่ขังใบตอนแก้ไข ══════

    private const string Legacy = "LEGACY-ค่าบริการเก่า";

    [Fact]
    public void A11_แก้ไข_ส่งรหัสเก่าตรงตัวกับที่เก็บบนใบนี้_ผ่านและเก็บรูปเดิม()
    {
        Assert.Null(ThaiWhtRateTable.Resolve(Legacy));                              // สมมติฐานของเทสต์: ตารางชี้ขาดไม่ได้จริง
        var v = DocumentLineIncomeType.Decide(Legacy, null, new string?[] { "8", Legacy });
        Assert.Null(v.Error);
        Assert.Equal(Legacy, v.Code);                                               // ไม่ normalize · ไม่ล้าง · ไม่แต่ง
        // ไม่ส่ง (ฟอร์มไม่แตะ) ⇒ คงค่าเดิมของบรรทัดเดียวกันเหมือนเดิม
        Assert.Equal(Legacy, DocumentLineIncomeType.Decide(null, Legacy, new string?[] { Legacy }).Code);
    }

    [Fact]
    public void A11_ทิศตรงข้าม_เลือกใหม่เป็นค่าที่ชี้ขาดไม่ได้_ยังปฏิเสธ()
    {
        // ใบนี้เก็บรหัสเก่าอีกตัว — ค่าใหม่ที่ไม่ตรงตัวยังล้ม 400
        Assert.NotNull(DocumentLineIncomeType.Decide("LEGACY-อื่น", null, new string?[] { Legacy }).Error);
        // ต่างแค่ช่องว่าง/ตัวพิมพ์ ⇒ ไม่ใช่ "ตรงตัว" (ห้ามเดา)
        Assert.NotNull(DocumentLineIncomeType.Decide(" " + Legacy, null, new string?[] { Legacy }).Error);
        // เอกสารใหม่ (ไม่มีรหัสที่เก็บไว้) ⇒ ปฏิเสธเหมือนเดิม · overload สองอาร์กิวเมนต์ = ว่าง
        Assert.NotNull(DocumentLineIncomeType.Decide(Legacy, null, Array.Empty<string?>()).Error);
        Assert.NotNull(DocumentLineIncomeType.Decide(Legacy, null).Error);
        Assert.NotNull(DocumentLineIncomeType.Decide(Legacy, null, new string?[] { null }).Error);
    }

    [Fact]
    public void A11_ทิศเดิม_ค่าที่ชี้ขาดได้_ยังเป็นรหัสมาตรฐาน_ล้างได้()
    {
        Assert.Equal("5", DocumentLineIncomeType.Decide("40(5)", null, new string?[] { Legacy }).Code);
        Assert.Null(DocumentLineIncomeType.Decide("", Legacy, new string?[] { Legacy }).Code);   // ผู้ใช้เลือก "ไม่ระบุ" เอง ⇒ ล้าง
    }
}
