using Accounting.Helpers;
using Accounting.Models.Enums;
using Xunit;

namespace Accounting.Tests;

/// <summary>
/// นโยบาย "การขายหนึ่งครั้งออกกระดาษกี่ใบ" — ตัวเดียวของระบบ
///
/// ═══ ที่มา ═══
/// เลขที่เอกสารผูกกับ <c>DocumentType</c> (TIV/REC) แต่หัวกระดาษผูกกับบทบาท
/// ทางกฎหมายที่คำนวณจากธงคนละชุด ⇒ ใบที่หัวพิมพ์ "ใบกำกับภาษี/ใบเสร็จรับเงิน"
/// เหมือนกันเป๊ะ ได้เลขคนละ series แล้วแต่ทางที่ผู้ใช้กดเข้า ("เดี๋ยว TIV เดี๋ยว REC")
///
/// เทสต์นี้ล็อกสิ่งที่ทำให้ regression กลับมาไม่ได้:
///   1. **Combined ต้องเท่ากับพฤติกรรมเดิมทุกข้อ** — เป็น default ของทุก tenant
///      ที่มีอยู่ ถ้าเผลอเปลี่ยนค่าใดค่าหนึ่ง ลูกค้าเดิมจะเห็นระบบเปลี่ยนพฤติกรรม
///      เองโดยไม่ได้ตั้งอะไร
///   2. โหมดแยกใบต้อง**ปิดหัวรวม**ด้วย ไม่ใช่แค่ออกใบเสร็จเพิ่ม (ไม่งั้นได้
///      กระดาษสองใบที่ต่างพูดว่า "ใบเสร็จรับเงิน" จากการรับเงินก้อนเดียว)
/// </summary>
public class ReceiptIssuePolicyTests
{
    [Fact]
    public void โหมดเริ่มต้นต้องเหมือนพฤติกรรมเดิมทุกข้อ()
    {
        const ReceiptIssueMode m = ReceiptIssueMode.Combined;
        Assert.True(ReceiptIssuePolicy.AllowsCombinedReceiptHeader(m));   // ยกหัวรวมได้
        Assert.True(ReceiptIssuePolicy.DefaultIssueSeparateReceipt(m));   // server เดิม `?? true`
        Assert.False(ReceiptIssuePolicy.ForcesSeparateReceipt(m));        // ไม่บังคับ
        Assert.True(ReceiptIssuePolicy.AllowsCashReceiptPapers(m));       // กระดาษขายสดยังเลือกได้
    }

    [Fact]
    public void โหมดแยกใบต้องปิดหัวรวมและบังคับออกใบเสร็จ()
    {
        const ReceiptIssueMode m = ReceiptIssueMode.SeparateReceipt;
        // ปิดหัวรวมเป็นหัวใจของโหมดนี้ — ถ้าออกใบเสร็จแยกแล้วยังยกหัวใบกำกับด้วย
        // ลูกค้าจะถือกระดาษสองใบที่ต่างบอกว่าตัวเองเป็นหลักฐานรับเงิน
        Assert.False(ReceiptIssuePolicy.AllowsCombinedReceiptHeader(m));
        Assert.True(ReceiptIssuePolicy.ForcesSeparateReceipt(m));
        Assert.True(ReceiptIssuePolicy.DefaultIssueSeparateReceipt(m));
        Assert.False(ReceiptIssuePolicy.AllowsCashReceiptPapers(m));
    }

    [Fact]
    public void โหมดค้าปลีกไม่ออกใบเสร็จตามหลังการรับชำระ()
    {
        const ReceiptIssueMode m = ReceiptIssueMode.RetailReceipt;
        // รับเงินพร้อมออกใบอยู่แล้ว → ใบเสร็จตามหลังอีกใบคือกระดาษเกิน
        Assert.False(ReceiptIssuePolicy.DefaultIssueSeparateReceipt(m));
        // แต่ยังยกหัวรวมได้ (ใบเดียวที่จุดขายคือทั้งใบกำกับและใบเสร็จ)
        Assert.True(ReceiptIssuePolicy.AllowsCombinedReceiptHeader(m));
        Assert.False(ReceiptIssuePolicy.ForcesSeparateReceipt(m));
    }

    [Theory]
    [InlineData(ReceiptIssueMode.Combined)]
    [InlineData(ReceiptIssueMode.SeparateReceipt)]
    [InlineData(ReceiptIssueMode.RetailReceipt)]
    public void ทุกโหมดต้องมีคำอธิบายและชื่อที่เอาไปโชว์ได้(ReceiptIssueMode mode)
    {
        // หน้าเว็บ "แสดง" ข้อความจากเซิร์ฟเวอร์อย่างเดียว — ว่างเมื่อไรผู้ใช้จะ
        // เจอช่องที่ถูกล็อกโดยไม่มีเหตุผล (defect class "ห้าม silent no-op")
        Assert.False(string.IsNullOrWhiteSpace(ReceiptIssuePolicy.Describe(mode)));
        Assert.False(string.IsNullOrWhiteSpace(ReceiptIssuePolicy.Label(mode)));
    }

    [Fact]
    public void คำอธิบายของแต่ละโหมดต้องไม่ซ้ำกัน()
    {
        // ถ้าสองโหมดอธิบายเหมือนกัน ผู้ใช้เลือกไม่ถูกและเราจะไม่รู้ว่าตกหล่นเคสไหน
        var texts = new[]
        {
            ReceiptIssuePolicy.Describe(ReceiptIssueMode.Combined),
            ReceiptIssuePolicy.Describe(ReceiptIssueMode.SeparateReceipt),
            ReceiptIssuePolicy.Describe(ReceiptIssueMode.RetailReceipt),
        };
        Assert.Equal(3, texts.Distinct().Count());
    }

    [Fact]
    public void ค่าตัวเลขของ_enum_ต้องคงที่เพราะ_persist_ลงฐานข้อมูล()
    {
        // แถวเดิมใน CompanySettings เก็บเป็น integer — สลับค่าเมื่อไร บริษัทที่
        // ตั้ง "แยกใบ" ไว้จะกลายเป็นโหมดอื่นเงียบ ๆ
        Assert.Equal(0, (int)ReceiptIssueMode.Combined);
        Assert.Equal(1, (int)ReceiptIssueMode.SeparateReceipt);
        Assert.Equal(2, (int)ReceiptIssueMode.RetailReceipt);
    }
}
