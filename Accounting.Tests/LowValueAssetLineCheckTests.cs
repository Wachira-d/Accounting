using Accounting.Helpers;
using Xunit;
using L = Accounting.Helpers.LowValueAssetLineCheck.Line;

namespace Accounting.Tests;

/// <summary>คำเตือนก่อนอนุมัติ: ของราคาต่ำกว่าเกณฑ์ขึ้นทะเบียนบนผังสินทรัพย์ — เคสจริง 2026-10-19
/// PV-20260903-0001 "อุปกรณ์สำนักงาน" 138.32 ลง 12210 ⇒ ขึ้นทะเบียน+คิดค่าเสื่อม 5 ปีของที่ไม่ใช่สินทรัพย์</summary>
public class LowValueAssetLineCheckTests
{
    private static L Ln(string code, string desc, decimal qty, decimal amount, string name = "อุปกรณ์สำนักงาน")
        => new(Guid.NewGuid(), code, name, desc, qty, amount);

    private static List<string> W(params L[] lines) => LowValueAssetLineCheck.Warnings(lines);

    [Fact]
    public void Reported_voucher_line_warns_with_unit_cost_and_threshold()
    {
        var w = Assert.Single(W(Ln("12210", "อุปกรณ์สำนักงาน - ภิญญาพร หงวนศิริ", 1m, 138.32m)));
        Assert.StartsWith(LowValueAssetLineCheck.Tag, w);
        Assert.Contains("138.32", w);
        Assert.Contains("5,000", w);
        Assert.True(LowValueAssetLineCheck.IsApprovalWarning(w));
    }

    [Fact]
    public void Real_air_conditioner_is_not_flagged()
        // แถวจริงบนหน้าทะเบียนเดียวกัน: แอร์ 26,944.86 บน 12210 — ใบที่ถูกต้องห้ามถูกเตือน (ทิศตรงข้าม)
        => Assert.Empty(W(Ln("12210", "แอร์ติดผนัง MITSUBISHI", 1m, 26_944.86m)));

    [Fact]
    public void Expense_account_is_never_flagged()
        => Assert.Empty(W(Ln("54420", "ปากกา", 10m, 50m, "ค่าวัสดุสำนักงาน")));

    [Fact]
    public void Threshold_is_per_unit_not_per_line()
    {
        // เก้าอี้ 3 ตัว ๆ ละ 4,000 = 12,000 — ราคาต่อตัวต่ำกว่าเกณฑ์ ⇒ เตือน
        Assert.Single(W(Ln("12230", "เก้าอี้", 3m, 12_000m, "เครื่องตกแต่ง")));
        // โน้ตบุ๊ก 2 เครื่อง ๆ ละ 25,000 ⇒ ไม่เตือน
        Assert.Empty(W(Ln("12220", "โน้ตบุ๊ก", 2m, 50_000m, "คอมพิวเตอร์")));
    }

    [Theory]
    [InlineData(4_999.99, true)]
    [InlineData(5_000.00, false)]   // ถึงเกณฑ์พอดี = ขึ้นทะเบียนได้ตามนโยบาย
    public void Boundary_at_threshold(double amount, bool warns)
        => Assert.Equal(warns, W(Ln("12210", "x", 1m, (decimal)amount)).Count == 1);

    [Fact]
    public void Zero_or_credit_lines_and_missing_quantity()
    {
        Assert.Empty(W(Ln("12210", "ส่วนลด", 1m, -100m)));
        Assert.Empty(W(Ln("12210", "x", 1m, 0m)));
        Assert.Equal(300m, LowValueAssetLineCheck.UnitCost(0m, 300m));     // จำนวน 0 ⇒ ทั้งบรรทัดเป็นชิ้นเดียว
    }

    [Fact]
    public void Auxiliary_cost_capitalized_into_a_real_asset_is_not_flagged()
    {
        // ฝ่ายค้าน P1-1: เครื่องจักร 80,000 + ค่าขนส่ง 1,500 + ค่าติดตั้ง 3,000 บนผังเดียวกัน — ค่าประกอบต้องรวมเข้าต้นทุน (TFRS บทที่ 10)
        // เดิมเทียบรายบรรทัด ⇒ เตือนให้ย้ายค่าขนส่ง/ติดตั้งไปค่าใช้จ่าย = แนะนำผิดมาตรฐาน
        Assert.Empty(W(
            Ln("12240", "เครื่องจักรบรรจุ", 1m, 80_000m, "เครื่องจักร"),
            Ln("12240", "ค่าขนส่ง", 1m, 1_500m, "เครื่องจักร"),
            Ln("12240", "ค่าติดตั้ง", 1m, 3_000m, "เครื่องจักร")));
    }

    [Fact]
    public void Auxiliary_share_counts_toward_unit_cost()
    {
        // ชั้นวาง 4,800 + ค่าส่ง 400 ⇒ ทะเบียนได้ต้นทุน 5,200 ≥ เกณฑ์ ⇒ ไม่เตือน (ใช้ต้นทุนเดียวกับที่ทะเบียนจะได้)
        Assert.Empty(W(Ln("12230", "ชั้นวางเหล็ก", 1m, 4_800m), Ln("12230", "ค่าจัดส่ง", 1m, 400m)));
        // ผังต่างกัน ⇒ ไม่แชร์ค่าส่ง (ตัววางแผนจัดกลุ่มต่อผัง) — ชั้นวางเดี่ยว 4,800 ยังเตือน
        Assert.Single(W(Ln("12230", "ชั้นวางเหล็ก", 1m, 4_800m), Ln("12210", "ค่าจัดส่ง", 1m, 8_000m)));
    }

    [Theory]
    [InlineData("12280")]   // งานระหว่างก่อสร้าง — ประกอบจากวัสดุ/ค่าแรงก้อนเล็กทุกใบ
    [InlineData("12290")]   // ที่ดิน — ไม่คิดค่าเสื่อม
    public void Non_depreciable_asset_accounts_are_not_flagged(string code)
        // ฝ่ายค้าน P1-2: เตือนทุกใบของ CIP = คำเตือนที่ฟ้องใบถูกทุกใบ (ปิดด่านโดยไม่ตั้งใจ)
        => Assert.Empty(W(Ln(code, "ปูน 10 ถุง", 10m, 1_450m, "งานระหว่างก่อสร้าง")));

    [Theory]
    [InlineData(ApprovalAckSource.Unattended)]
    [InlineData(ApprovalAckSource.ApiClient)]
    public void Unattended_and_api_approvals_are_not_blocked_by_this_warning(ApprovalAckSource source)
    {
        // เตือน ไม่บล็อก — ทางเข้าที่ไม่มีคน (ใบประจำ · API) ต้องไม่ค้างร่างเพราะคำเตือนนี้
        var w = W(Ln("12210", "x", 1m, 138.32m));
        Assert.Empty(ApprovalAcknowledgement.Unacknowledged(source, w));
    }

    [Fact]
    public void Approval_trail_cites_the_asset_standard_not_the_general_vat_sections()
        // ฝ่ายค้าน P2-1 (กฎ M): ร่องรอย "อนุมัติทั้งที่มีคำเตือน" ต้องอ้างมาตราของคำเตือนนี้
        => Assert.Equal(LowValueAssetLineCheck.LegalReference,
            ApprovalAcknowledgement.LegalReference(W(Ln("12210", "x", 1m, 138.32m))));

    [Fact]
    public void Passed_notice_reaches_unattended_approvers_and_is_silent_otherwise()
    {
        // ฝ่ายค้าน P2-2: OCR สร้าง+อนุมัติ · LINE · รายการประจำ ไม่มีหน้าจอรับทราบ ⇒ ต้องบอกคนหลังอนุมัติ
        var notice = LowValueAssetLineCheck.PassedNotice(W(Ln("12210", "อุปกรณ์สำนักงาน", 1m, 138.32m)));
        Assert.NotNull(notice);
        Assert.Contains("ไม่ใช่สินทรัพย์", notice);
        Assert.Contains("138.32", notice);
        Assert.DoesNotContain(LowValueAssetLineCheck.Tag, notice);
        Assert.Null(LowValueAssetLineCheck.PassedNotice(new[] { "คำเตือนชนิดอื่น" }));
        Assert.Null(LowValueAssetLineCheck.PassedNotice(null));
    }
}
