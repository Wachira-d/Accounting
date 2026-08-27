using Accounting.Helpers;
using Xunit;

namespace Accounting.Tests;

/// <summary>
/// สถานประกอบการผู้ออกเอกสาร — §86/4(2) (ชื่อ/ที่อยู่ของผู้ประกอบการจดทะเบียน)
/// + ป.86/2542 (ที่อยู่บนใบกำกับ = ที่ตั้งสถานประกอบการที่ออกใบ)
/// + ประกาศอธิบดีฯ ฉบับที่ 199 (รหัสสาขา 5 หลัก)
///
/// ═══ เทสต์ชุดแรกคือด่านที่สำคัญที่สุดของทั้งเฟส ═══
/// **กิจการสาขาเดียวต้องไม่รู้สึกถึงความเปลี่ยนแปลงใด ๆ** — ไม่มีแถวใน Branches
/// ⇒ ผลลัพธ์ต้องเท่ากับค่าบนตัวบริษัทเป๊ะ และ Address/Phone/Email ต้องเป็น null
/// เพื่อให้ renderer ใช้ของบริษัทเส้นเดิม (ไม่ใช่ค่าที่ resolver ประกอบขึ้นใหม่)
/// </summary>
public class DocumentIssuerBranchTests
{
    private static readonly DocumentBranchView ChiangMai = new(
        TaxBranchCode: "00003",
        Name: "เชียงใหม่",
        NameEn: "Chiang Mai",
        Address: "99/9 ถ.นิมมานเหมินท์ ต.สุเทพ อ.เมือง จ.เชียงใหม่ 50200",
        Phone: "053-111111",
        Email: "cm@example.co.th");

    // ═══════════ 1. กิจการสาขาเดียว — ห้ามเปลี่ยนพฤติกรรม ═══════════

    [Fact]
    public void ไม่มีสาขาเลย_ได้ค่าของบริษัทเป๊ะ_และไม่ทับที่อยู่บริษัท()
    {
        var r = DocumentIssuerBranch.Resolve(
            snapshotCode: null, branch: null,
            companyBranchCode: "00000", companyBranchName: "สำนักงานใหญ่");

        Assert.Equal("00000", r.Code);
        Assert.Equal("สำนักงานใหญ่", r.Label);
        Assert.False(r.FromBranchRegistry);
        // null ทั้งสามช่อง = renderer เดินเส้นเดิม (ที่อยู่/เบอร์/อีเมลของบริษัท)
        Assert.Null(r.Address);
        Assert.Null(r.Phone);
        Assert.Null(r.Email);
    }

    [Fact]
    public void บริษัทยังไม่เคยกรอกรหัสสาขา_ยังได้สำนักงานใหญ่เหมือนเดิม()
    {
        var r = DocumentIssuerBranch.Resolve(null, null, null, null);
        Assert.Equal("00000", r.Code);
        Assert.Equal("สำนักงานใหญ่", r.Label);
    }

    [Fact]
    public void บริษัทสาขาเดียวที่จดเป็นสาขาย่อย_ยังใช้รหัสของบริษัทตามเดิม()
    {
        // เคสจริง: กิจการมีที่เดียวแต่จดเป็น "สาขาที่ 2" (สำนักงานใหญ่ปิดไปแล้ว)
        var r = DocumentIssuerBranch.Resolve(null, null, "00002", "สาขาสีลม");
        Assert.Equal("00002", r.Code);
        Assert.Equal("สาขาที่ 2 (สาขาสีลม)", r.Label);
    }

    // ═══════════ 2. ใบที่ออกจากสาขา ═══════════

    [Fact]
    public void ใบที่ผูกสาขา_ใช้รหัสและที่อยู่ของสาขานั้น()
    {
        var r = DocumentIssuerBranch.Resolve(null, ChiangMai, "00000", "สำนักงานใหญ่");

        Assert.Equal("00003", r.Code);
        Assert.Equal("สาขาที่ 3 (เชียงใหม่)", r.Label);
        Assert.Equal(ChiangMai.Address, r.Address);
        Assert.Equal("053-111111", r.Phone);
        Assert.True(r.FromBranchRegistry);
    }

    [Fact]
    public void โหมดอังกฤษ_ใช้ชื่อสาขาภาษาอังกฤษ()
    {
        var r = DocumentIssuerBranch.Resolve(null, ChiangMai, "00000", null, isEnglish: true);
        Assert.Equal("Branch 3 (Chiang Mai)", r.Label);
    }

    [Fact]
    public void สาขาที่ยังไม่กรอกที่อยู่_ใช้ที่อยู่บริษัทตามเดิม()
    {
        // "ทั้งชุดหรือไม่ใช้เลย" — ห้ามผสมช่องของสาขากับของบริษัท
        var noAddr = ChiangMai with { Address = "   " };
        var r = DocumentIssuerBranch.Resolve(null, noAddr, "00000", null);
        Assert.Equal("00003", r.Code);       // รหัสยังของสาขา
        Assert.Null(r.Address);              // แต่ที่อยู่ตกกลับไปเป็นของบริษัท
    }

    [Fact]
    public void สาขาที่ยังไม่กรอกรหัสสรรพากร_ตกไปใช้รหัสของบริษัท()
    {
        var noCode = ChiangMai with { TaxBranchCode = null };
        var r = DocumentIssuerBranch.Resolve(null, noCode, "00000", "สำนักงานใหญ่");
        Assert.Equal("00000", r.Code);
    }

    // ═══════════ 3. snapshot — ใบที่ออกไปแล้วต้องนิ่ง ═══════════

    [Fact]
    public void snapshot_ชนะทะเบียนเสมอ_แก้ทะเบียนวันนี้ไม่ย้อนไปเปลี่ยนใบเก่า()
    {
        // ใบออกไปตอนสาขายังเป็นรหัส 00003 · วันนี้ทะเบียนถูกแก้เป็น 00007
        var renamed = ChiangMai with { TaxBranchCode = "00007" };
        var r = DocumentIssuerBranch.Resolve("00003", renamed, "00000", null);
        Assert.Equal("00003", r.Code);
    }

    [Fact]
    public void snapshot_ไม่ตรงทะเบียน_ตัดชื่อสาขาออก_เหลือเฉพาะรหัสที่กฎหมายคุม()
    {
        // ชื่อ "เชียงใหม่" ในทะเบียนตอนนี้เป็นของรหัส 00007 แล้ว — พิมพ์
        // "สาขาที่ 3 (เชียงใหม่)" จะเป็นการอ้างสถานประกอบการผิด
        var renamed = ChiangMai with { TaxBranchCode = "00007" };
        var r = DocumentIssuerBranch.Resolve("00003", renamed, "00000", null);
        Assert.Equal("สาขาที่ 3", r.Label);
    }

    [Fact]
    public void snapshot_ตรงกับทะเบียน_ยังพิมพ์ชื่อสาขาต่อท้ายตามปกติ()
    {
        var r = DocumentIssuerBranch.Resolve("00003", ChiangMai, "00000", null);
        Assert.Equal("สาขาที่ 3 (เชียงใหม่)", r.Label);
    }

    [Fact]
    public void snapshot_ของกิจการสาขาเดียว_ยังคงพฤติกรรมเดิม()
    {
        // ใบเก่าที่อนุมัติหลังมีฟีเจอร์นี้ ตรึง "00000" ไว้ — ต่อให้บริษัทไปแก้
        // Company.BranchCode ทีหลัง ใบเดิมต้องพิมพ์เหมือนตอนออก
        var r = DocumentIssuerBranch.Resolve("00000", null, "00009", null);
        Assert.Equal("00000", r.Code);
        Assert.Equal("สำนักงานใหญ่", r.Label);
    }

    // ═══════════ 4. ResolveCode / UseBranchAddress (ใช้ตรงในเส้น e-Tax) ═══════════

    [Fact]
    public void ResolveCode_ใช้ลำดับความสำคัญชุดเดียวกับตัวเต็ม()
    {
        Assert.Equal("00003", DocumentIssuerBranch.ResolveCode("00003", "00007", "00000"));
        Assert.Equal("00007", DocumentIssuerBranch.ResolveCode(null, "00007", "00000"));
        Assert.Equal("00000", DocumentIssuerBranch.ResolveCode(null, null, "00000"));
        // กิจการสาขาเดียวที่ยังไม่เคยกรอกอะไรเลย → 00000 (ส่งเข้า TXID ได้ทันที)
        Assert.Equal("00000", DocumentIssuerBranch.ResolveCode(null, null, null));
    }

    [Theory]
    [InlineData(null, false)]
    [InlineData("", false)]
    [InlineData("   ", false)]
    [InlineData("99/9 ถ.นิมมาน", true)]
    public void UseBranchAddress_กรอกที่อยู่จริงเท่านั้นถึงจะทับของบริษัท(string? addr, bool expected)
        => Assert.Equal(expected, DocumentIssuerBranch.UseBranchAddress(addr));
}
