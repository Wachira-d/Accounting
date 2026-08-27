using Accounting.Helpers;
using Xunit;

namespace Accounting.Tests;

/// <summary>
/// รหัสสาขาสรรพากร 5 หลัก — ป.รัษฎากร §86/4 + ประกาศอธิบดีฯ ฉบับที่ 199
/// (ลว. 26 ธ.ค. 2556): <c>00000</c> = สำนักงานใหญ่ · <c>00001</c>+ = "สาขาที่ n"
///
/// ที่มาของเทสต์: ก่อนมี resolver กลาง มีสูตรคำนวณป้ายสาขาอยู่ **3 ที่**
/// ซึ่งให้ผลไม่ตรงกัน —
///   • <c>PdfGenerationService.PdfA3</c> (ทั้งฝั่งผู้ขายและผู้ซื้อ) พิมพ์ **เลขดิบ**
///     "00003" แทนถ้อยคำ "สาขาที่ 3" ที่ประกาศฯ กำหนด
///   • <c>DocumentBrandController.FormatBranchLabel</c> พิมพ์ "สาขาที่ 00003"
///     (ศูนย์นำหน้าติดมาด้วย)
/// defect class "resolver กลาง ห้ามคำนวณเอง" (CLAUDE.md ข้อ 4.A)
/// </summary>
public class TaxBranchCodeTests
{
    // ---------- IsHeadOffice ----------

    [Theory]
    [InlineData(null)]
    [InlineData("")]
    [InlineData("   ")]
    [InlineData("00000")]
    [InlineData("0")]        // ข้อมูลเก่าที่ยังไม่ pad
    public void ว่างหรือศูนย์ล้วน_ถือเป็นสำนักงานใหญ่(string? code)
        => Assert.True(TaxBranchCode.IsHeadOffice(code));

    [Theory]
    [InlineData("00001")]
    [InlineData("00003")]
    [InlineData("00010")]
    public void รหัสที่ไม่ใช่ศูนย์ล้วน_ไม่ใช่สำนักงานใหญ่(string code)
        => Assert.False(TaxBranchCode.IsHeadOffice(code));

    // ---------- Label: ถ้อยคำที่ต้องพิมพ์บนใบกำกับภาษี ----------

    [Fact]
    public void สำนักงานใหญ่_พิมพ์คำว่าสำนักงานใหญ่()
    {
        Assert.Equal("สำนักงานใหญ่", TaxBranchCode.Label("00000"));
        Assert.Equal("สำนักงานใหญ่", TaxBranchCode.Label(null));
        Assert.Equal("Head Office", TaxBranchCode.Label("00000", isEnglish: true));
    }

    [Fact]
    public void สาขาย่อย_พิมพ์สาขาที่N_ไม่ใช่เลขดิบและไม่มีศูนย์นำหน้า()
    {
        // บั๊กเดิม PdfA3: ได้ "00003"
        Assert.Equal("สาขาที่ 3", TaxBranchCode.Label("00003"));
        // บั๊กเดิม DocumentBrandController: ได้ "สาขาที่ 00003"
        Assert.DoesNotContain("00003", TaxBranchCode.Label("00003"));
        Assert.Equal("สาขาที่ 12", TaxBranchCode.Label("00012"));
        Assert.Equal("Branch 3", TaxBranchCode.Label("00003", isEnglish: true));
    }

    [Fact]
    public void รหัสที่มีอักขระปน_ถือเอาเฉพาะตัวเลข()
    {
        // ยกมาจาก FormatBranch เดิม (พฤติกรรมที่พิมพ์บนเอกสารอยู่แล้ว) — อย่าเปลี่ยน
        Assert.Equal("สาขาที่ 1", TaxBranchCode.Label("A1"));
        Assert.Equal("สาขาที่ 3", TaxBranchCode.Label("0-0-0-0-3"));
    }

    [Fact]
    public void LabelWithName_ต่อชื่อสาขาในวงเล็บเฉพาะสาขาย่อย()
    {
        Assert.Equal("สาขาที่ 3 (เชียงใหม่)", TaxBranchCode.LabelWithName("00003", "เชียงใหม่"));
        Assert.Equal("สาขาที่ 3", TaxBranchCode.LabelWithName("00003", "  "));
        // สำนักงานใหญ่ไม่ต่อชื่อ — §86/4 ต้องการคำว่า "สำนักงานใหญ่" ล้วน
        Assert.Equal("สำนักงานใหญ่", TaxBranchCode.LabelWithName("00000", "อาคารสาทร"));
    }

    [Theory]
    [InlineData("สำนักงานใหญ่")]
    [InlineData("สนญ")]
    [InlineData("Head Office")]
    [InlineData("HO")]
    public void ชื่อสาขาที่แปลว่าสำนักงานใหญ่_ไม่ต่อท้ายรหัสสาขาย่อย(string name)
    {
        // ฟอร์ม auto-เติมชื่อ "สำนักงานใหญ่" ไว้ ⇒ ถ้าต่อท้ายจะได้ "สาขาที่ 3 (สำนักงานใหญ่)"
        Assert.Equal("สาขาที่ 3", TaxBranchCode.LabelWithName("00003", name));
    }

    [Fact]
    public void ผู้ใช้กรอกเลขสาขาผิดช่อง_ถือตามเลขที่กรอกในช่องชื่อ()
    {
        // รหัส 00000 (ค่า default ของฟอร์ม) + ชื่อเป็นเลขล้วน = กรอกสลับช่อง
        Assert.Equal("สาขาที่ 1", TaxBranchCode.LabelWithName("00000", "00001"));
        // แต่ข้อความที่มีเลขปนต้องไม่โดนแปลง
        Assert.Equal("สำนักงานใหญ่", TaxBranchCode.LabelWithName("00000", "สำนักงานใหญ่ ชั้น 5"));
    }

    [Theory]
    [InlineData(null, "00000")]
    [InlineData("", "00000")]
    [InlineData("3", "00003")]
    [InlineData("00003", "00003")]
    [InlineData("เพี้ยน", "00000")]
    public void Normalize_สำหรับส่งเข้าไฟล์อีแท็กซ์_ต้องได้ห้าหลักเสมอ(string? raw, string expected)
        => Assert.Equal(expected, TaxBranchCode.Normalize(raw));

    // ---------- TryNormalize: ด่านตอนบันทึกทะเบียนสาขา ----------

    [Theory]
    [InlineData("1", "00001")]
    [InlineData("3", "00003")]
    [InlineData("12", "00012")]
    [InlineData("00007", "00007")]
    [InlineData(" 00007 ", "00007")]
    public void พิมพ์สั้นกว่า5หลัก_เติมศูนย์ให้อัตโนมัติ(string raw, string expected)
    {
        Assert.True(TaxBranchCode.TryNormalize(raw, out var code, out var error));
        Assert.Null(error);
        Assert.Equal(expected, code);
    }

    [Theory]
    [InlineData(null)]
    [InlineData("")]
    [InlineData("   ")]
    public void เว้นว่าง_ไม่ใช่ข้อผิดพลาด_แต่ได้ค่าว่าง(string? raw)
    {
        // กิจการสาขาเดียวไม่ต้องกรอกอะไรเลย — ต้องไม่เด้ง error ใส่หน้าผู้ใช้
        Assert.True(TaxBranchCode.TryNormalize(raw, out var code, out var error));
        Assert.Null(code);
        Assert.Null(error);
    }

    [Theory]
    [InlineData("ABCDE")]
    [InlineData("123456")]
    [InlineData("00-01")]
    [InlineData("1234A")]
    public void รูปแบบผิด_คืนเท็จพร้อมข้อความไทยที่อ้างประกาศฯ(string raw)
    {
        Assert.False(TaxBranchCode.TryNormalize(raw, out var code, out var error));
        Assert.Null(code);
        Assert.NotNull(error);
        Assert.Contains("199", error!);         // อ้างอิงกฎหมายในข้อความถึงผู้ใช้
    }

    [Fact]
    public void จัดรูปแล้วนำไปทำป้ายต่อได้ทันที()
    {
        Assert.True(TaxBranchCode.TryNormalize("3", out var code, out _));
        Assert.Equal("สาขาที่ 3", TaxBranchCode.Label(code));
    }
}
