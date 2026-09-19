using Accounting.Helpers;
using Xunit;

namespace Accounting.Tests;

/// <summary>
/// **ไฟล์ที่เราส่งออกต้องไม่กลายเป็นสูตรบนเครื่องนักบัญชี** (CSV/formula injection)
///
/// <para>ข้อมูลในไฟล์ส่งออกมาจากผู้ใช้ปลายทาง/พาร์ตเนอร์/OCR ทั้งหมด —
/// Excel และ Sheets ตีความช่องที่ขึ้นต้นด้วย <c>= + - @</c> เป็นสูตร และรองรับ
/// DDE (<c>=cmd|'/c calc'!A0</c>) ⇒ ชื่อคู่ค้าที่ sync เข้ามาเป็นโค้ดที่รันได้
/// (กฎเหล็ก #4 C — ข้อมูลที่ผู้ใช้/พาร์ตเนอร์คุมได้ต้องผ่านตัวหนีก่อนเสมอ)</para>
///
/// <para>สองครึ่ง: ครึ่งแรก = ของอันตรายถูกทำให้ไม่ทำงาน · ครึ่งหลัง = ข้อความปกติ
/// (รวมข้อความไทย ตัวเลข วันที่ ยอดเงินติดลบในช่องตัวเลข) <b>ต้องไม่ถูกแตะ</b>
/// — ตัวหนีที่เปลี่ยนทุกช่องทำให้ไฟล์ที่นำกลับเข้ามาไม่ตรงกับของเดิม</para>
/// </summary>
public class ImportExportCsvSafetyTests
{
    // ═══ ครึ่งที่ 1 — ของอันตรายต้องไม่ทำงาน ═══

    [Theory]
    [InlineData("=cmd|'/c calc'!A0")]
    [InlineData("=HYPERLINK(\"http://evil\",\"คลิก\")")]
    [InlineData("+1+1")]
    [InlineData("-2+3")]
    [InlineData("@SUM(A1:A9)")]
    [InlineData(" \t=1+1")]              // ช่องว่าง/แท็บนำหน้า Excel ยังตีความเป็นสูตร
    public void ช่องที่ขึ้นต้นด้วยอักขระสูตร_ต้องถูกทำให้เป็นข้อความ(string raw)
    {
        var e = CsvFieldSafety.Escape(raw);
        // ไม่ว่าจะถูกครอบด้วยเครื่องหมายคำพูดหรือไม่ อักขระแรกของ "เนื้อ" ต้องเป็น '
        var inner = e.StartsWith("\"") ? e[1..^1].Replace("\"\"", "\"") : e;
        Assert.StartsWith("'", inner);
        Assert.Equal(raw, inner[1..]);    // เนื้อความเดิมยังอยู่ครบ ไม่ถูกตัดทิ้ง
    }

    [Fact]
    public void ใส่อัญประกาศเดี่ยวก่อน_แล้วค่อยครอบด้วยเครื่องหมายคำพูด()
    {
        // ลำดับสำคัญ: ถ้าครอบก่อน ' จะไปอยู่นอกเครื่องหมาย แล้ว Excel ยังคำนวณ
        var e = CsvFieldSafety.Escape("=A1,B2");
        Assert.Equal("\"'=A1,B2\"", e);
    }

    [Fact]
    public void ชื่อคู่ค้าที่เป็นสูตร_ต้องไม่หลุดเข้าแถว_CSV_แบบดิบ()
    {
        var row = CsvFieldSafety.Row(new[] { "บริษัท ก จำกัด", "=1+1", "0105540134521" });
        Assert.Equal("บริษัท ก จำกัด,'=1+1,0105540134521", row);
    }

    [Fact]
    public void ตัวขึ้นบรรทัดทุกแบบต้องถูกครอบ_รวม_CR_เดี่ยว()
    {
        // ของเดิมดูแค่ \n ⇒ ข้อความที่มี CR เดี่ยวทำให้ไฟล์แตกแถวกลางคัน
        Assert.Equal("\"บรรทัด1\rบรรทัด2\"", CsvFieldSafety.Escape("บรรทัด1\rบรรทัด2"));
        Assert.Equal("\"บรรทัด1\nบรรทัด2\"", CsvFieldSafety.Escape("บรรทัด1\nบรรทัด2"));
    }

    [Fact]
    public void เครื่องหมายคำพูดในเนื้อความต้องถูกเบิ้ล()
        => Assert.Equal("\"เขาบอกว่า \"\"ได้\"\"\"", CsvFieldSafety.Escape("เขาบอกว่า \"ได้\""));

    // ═══ ครึ่งที่ 2 — ข้อความปกติต้องไม่ถูกแตะ ═══

    [Theory]
    [InlineData("บริษัท ตัวอย่าง จำกัด")]
    [InlineData("0105540134521")]
    [InlineData("2026-09-19")]
    [InlineData("1234.56")]
    [InlineData("")]
    [InlineData("JuristicPerson")]
    [InlineData("ร้านลุงหมี (สาขา 2)")]
    public void ข้อความปกติต้องออกมาเหมือนเดิมทุกตัวอักษร(string raw)
        => Assert.Equal(raw, CsvFieldSafety.Escape(raw));

    [Fact]
    public void ยอดเงินติดลบยังถูกกันไว้_และนั่นคือราคาที่ยอมจ่าย()
    {
        // "-1500.00" ขึ้นต้นด้วย '-' ⇒ ถูกเติม ' เหมือนกัน
        // ยอมรับได้: Excel แสดง -1500.00 เป็นข้อความ (เห็นค่าเดิมครบ)
        // แลกกับการกัน "-2+3" ที่รันได้จริง — ทิศที่ความเสียหายมองเห็นและแก้ทัน (G5)
        Assert.Equal("'-1500.00", CsvFieldSafety.Escape("-1500.00"));
    }

    [Fact]
    public void ค่าว่างและ_null_ต้องได้สตริงว่าง_ไม่ใช่ระเบิด()
    {
        Assert.Equal("", CsvFieldSafety.Escape(null));
        Assert.Equal("", CsvFieldSafety.Escape(""));
        // ช่องว่างล้วนไม่ใช่สูตร — ห้ามเติม ' ให้ทุกช่องที่มีแต่ช่องว่าง
        Assert.Equal("   ", CsvFieldSafety.Escape("   "));
        Assert.Equal(",,", CsvFieldSafety.Row(new string?[] { null, "", null }));
    }
}
