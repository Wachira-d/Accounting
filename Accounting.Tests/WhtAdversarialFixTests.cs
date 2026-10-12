using Accounting.Helpers;
using Accounting.Models.Enums;
using Xunit;

namespace Accounting.Tests;

/// <summary>
/// ผลตรวจฝ่ายค้านงาน WHT/50 ทวิ (2026-10-12 · diff cbc0f705..56ff4998) — นโยบายเจ้าของ: <b>เตือน ไม่บล็อก</b> ·
/// ผู้ถูกหัก (เช่น บริษัทประกัน) ที่ต่างจากผู้รับเงินบนใบสำคัญจ่าย (นายหน้า) ต้องถูกใช้ทุกที่ ·
/// แต่ละข้อล็อกทั้งทิศที่แก้และทิศตรงข้าม (ใบที่ถูกอยู่แล้วไม่ถูกแตะ) · จุดเรียกใน service ล็อกด้วย tools/required_call_site_check.py
/// </summary>
public class WhtAdversarialFixTests
{
    // นายหน้าบุคคลธรรมดา (ผู้รับเงินบน PV) · บริษัทประกัน/สถาบันการเงินนิติบุคคล (ผู้ถูกหัก)
    private const string BrokerTaxId = "1234567890123";
    private const string InsurerTaxId = "0105536000011";
    private static readonly DateTime PayDate = new(2026, 10, 1);

    // ── M1: ผัง 2191x ของพรีวิว GL ตาม "ผู้ถูกหัก" — ผู้รับเงินกับผู้ถูกหักให้คำตอบต่างกันจริง (เหตุที่พรีวิวต้องโหลดผู้ถูกหัก) ──

    [Fact]
    public void M1_ผู้ถูกหักนิติบุคคล_ผัง21917_ผู้รับเงินบุคคล_ผัง21916()
    {
        Assert.Equal(WhtPayableAccount.Pnd53Code,
            WhtPayableAccount.CodeChain(false, null, InsurerTaxId, ContactType.Unknown, "บริษัท ประกันภัย จำกัด")[0]);
        Assert.Equal(WhtPayableAccount.Pnd3Code,
            WhtPayableAccount.CodeChain(false, null, BrokerTaxId, ContactType.Individual, "นาย นายหน้า")[0]);
    }

    // ── M2: ด่านอัตราเทียบประเภทเงินได้ใช้ชนิด "ผู้ถูกหัก" ──

    private static readonly (string? Description, string? IncomeTypeCode, decimal Rate)[] InterestLine =
        { ("ดอกเบี้ยเงินกู้ผ่านตัวแทน", "4a", 1m) };

    [Fact]
    public void M2_ผู้ถูกหักนิติบุคคล_ดอกเบี้ย1เปอร์เซ็นต์_ไม่เตือน()
        => Assert.Empty(WhtLineRateWarning.ForDomesticPayee(InterestLine, InsurerTaxId, ContactType.Unknown,
            "บริษัท เงินทุน จำกัด", PayDate));

    [Fact]
    public void M2_ทิศตรงข้าม_ถ้าตัดสินจากนายหน้าบุคคลธรรมดา_เตือน()
    {
        // ตัวเลขเดียวกันแต่ชนิดผู้รับ = บุคคลธรรมดา (ผู้รับเงินบน PV) ⇒ ด่านเดิมเตือนผิดใบที่ถูก — เหตุที่ผู้เรียกต้องส่งผู้ถูกหัก
        var w = WhtLineRateWarning.ForDomesticPayee(InterestLine, BrokerTaxId, ContactType.Individual, "นาย นายหน้า", PayDate);
        Assert.Single(w);
        Assert.StartsWith(WhtLineRateWarning.Prefix, w[0]);
        Assert.Contains("15%", w[0]);
    }

    [Fact]
    public void M2_บรรทัดไม่หักหรือไม่ระบุประเภท_ไม่ตรวจ()
    {
        var lines = new (string?, string?, decimal)[] { ("ไม่หัก", "8", 0m), ("ไม่ระบุประเภท", null, 3m), ("ว่าง", " ", 3m) };
        Assert.Empty(WhtLineRateWarning.ForDomesticPayee(lines, BrokerTaxId, ContactType.Individual, null, PayDate));
    }

    [Fact]
    public void M2_ไม่รู้ชนิดผู้ถูกหัก_ยอมรับอัตราของชนิดใดชนิดหนึ่ง()
        => Assert.Empty(WhtLineRateWarning.ForDomesticPayee(InterestLine, null, ContactType.Unknown, null, PayDate));

    // ── M3: ป้าย [WHT-RATE] ผ่านทางเข้าที่ไม่มีคน (ใบประจำ · LINE · OCR อนุมัติอัตโนมัติ · API) · เว็บ/มือถือยังหยุดให้กดรับทราบ ──

    [Fact]
    public void M3_คำเตือนอัตรา_ทางไม่มีคนผ่าน_เว็บยังหยุด()
    {
        var w = WhtLineRateWarning.ForDomesticPayee(InterestLine, BrokerTaxId, ContactType.Individual, null, PayDate);
        Assert.Empty(ApprovalAcknowledgement.Unacknowledged(ApprovalAckSource.Unattended, w));
        Assert.Empty(ApprovalAcknowledgement.Unacknowledged(ApprovalAckSource.ApiClient, w));
        Assert.Single(ApprovalAcknowledgement.Unacknowledged(ApprovalAckSource.None, w));
        Assert.Empty(ApprovalAcknowledgement.Unacknowledged(ApprovalAckSource.User, w));
    }

    [Fact]
    public void M3_ทิศตรงข้าม_คำเตือนไม่มีป้าย_ทางไม่มีคนยังหยุด()
    {
        // ข้อความรูปเดิม (ไม่มีป้าย) และคำเตือนชนิดอื่น ต้องไม่ถูกส่งผ่านโดยไม่ตั้งใจ
        var w = new[] { "อัตรา WHT ของ 'x': อัตรา 1% ไม่ตรงกับ \"ดอกเบี้ย\"", "ยอดรวมเอกสาร 600,000.00 THB — ตรวจตัวเลขก่อนยืนยัน" };
        Assert.False(WhtLineRateWarning.IsApprovalWarning(w[0]));
        Assert.Equal(2, ApprovalAcknowledgement.Unacknowledged(ApprovalAckSource.Unattended, w).Count);
        Assert.Equal(2, ApprovalAcknowledgement.Unacknowledged(ApprovalAckSource.ApiClient, w).Count);
    }

    // ── M4: เอกสารลูกสืบทอดประเภทเงินได้ (แปลง · คัดลอก · รายการประจำ) ──

    [Theory]
    [InlineData("8ins", "8ins")]
    [InlineData("40(8)", "8")]
    [InlineData("40(5)", "5")]
    [InlineData(null, null)]
    [InlineData("", null)]
    [InlineData("ไม่มีในตาราง", null)]   // ค่าเก่าก่อนมีด่าน — ไม่ทำให้การแปลง/คัดลอก/รอบประจำล้มทั้งใบ
    public void M4_สืบทอด_null_เป็น_null_รหัสมาตรฐาน_ค่าเก่าที่ชี้ขาดไม่ได้เป็นไม่ระบุ(string? stored, string? expected)
        => Assert.Equal(expected, DocumentLineIncomeType.Inherit(stored));

    // ── M5: ติ๊กสร้าง 50 ทวิ อัตโนมัติของรายการประจำ = ติ๊กบนเอกสาร (ตัวออกใบทางเดียว) ──

    [Theory]
    [InlineData(null, null, null)]     // เทมเพลตเก่าไม่มีทั้งคู่ ⇒ ค่าตั้งต้นของเอกสาร (เปิด)
    [InlineData(null, true, true)]
    [InlineData(null, false, false)]   // ผู้ใช้ติ๊กออกบนหน้ารายการประจำ ⇒ ใบไปรอในลิสต์ ไม่ออกเอง (เดิมติ๊กนี้ไม่มีผลกับการออกตอนอนุมัติ)
    [InlineData(false, true, false)]   // ค่าใหม่ชนะ
    [InlineData(true, false, true)]
    public void M5_ติ๊กรายการประจำ(bool? whtCertAutoIssue, bool? legacy, bool? expected)
        => Assert.Equal(expected, WhtCertIssuePlan.RecurringTemplateAutoIssue(whtCertAutoIssue, legacy));

    // ── M6: แถว 50 ทวิ ที่หน้าเว็บใช้ = ตัวตัดสินเดียวกับ PDF (รหัสแบบมาตราไม่ตกแถว 6) ──

    [Theory]
    [InlineData("40(8)", "5")]
    [InlineData("8ins", "5")]
    [InlineData("40(4)(ก)", "4a")]
    [InlineData("รหัสแปลก", "6")]
    public void M6_แถวบนแบบจากเซิร์ฟเวอร์(string code, string row)
        => Assert.Equal(row, ThaiWhtRateTable.CertificateRow(code));

    // ── L3: เอกสารฝั่งผู้จ่ายเลือก gov1 (ม.69 ทวิ) / 1 (เงินเดือน) ใหม่ไม่ได้ — ค่าเดิมยังโหลด/บันทึกซ้ำได้ ──

    [Theory]
    [InlineData("gov1")]
    [InlineData("1")]
    [InlineData("40(1)")]
    public void L3_ฝั่งผู้จ่าย_เลือกใหม่_ปฏิเสธพร้อมเหตุผล(string requested)
    {
        var err = DocumentLineIncomeType.PayerSideProblem(true, requested, null);
        Assert.NotNull(err);
        Assert.Contains("เลือกประเภท", err);
    }

    [Fact]
    public void L3_ทิศตรงข้าม_ค่าเดิมของบรรทัดเดียวกัน_บันทึกซ้ำได้()
    {
        Assert.Null(DocumentLineIncomeType.PayerSideProblem(true, null, "gov1"));     // ฟอร์มรุ่นเก่าไม่ส่ง = คงค่าเดิม
        Assert.Null(DocumentLineIncomeType.PayerSideProblem(true, "gov1", "gov1"));   // ส่งค่าเดิมกลับมา
        Assert.Null(DocumentLineIncomeType.PayerSideProblem(true, "", "gov1"));       // ล้างได้เสมอ
        Assert.NotNull(DocumentLineIncomeType.PayerSideProblem(true, "gov1", "8"));   // เปลี่ยนจากประเภทอื่น = ปฏิเสธ
    }

    [Fact]
    public void L3_ทิศตรงข้าม_ฝั่งรับเงินและประเภทอื่น_ไม่แตะ()
    {
        Assert.Null(DocumentLineIncomeType.PayerSideProblem(false, "gov1", null));    // ใบแจ้งหนี้ให้หน่วยงานรัฐ = gov1 ถูก
        Assert.Null(DocumentLineIncomeType.PayerSideProblem(true, "8ins", null));
        Assert.Null(DocumentLineIncomeType.PayerSideProblem(true, "8", null));
        Assert.Null(DocumentLineIncomeType.PayerSideProblem(true, null, null));
    }
}
