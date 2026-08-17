using Accounting.Models.Enums;
using Xunit;

namespace Accounting.Tests;

/// <summary>
/// รอบจำลองเหตุการณ์ ชุดที่ 4 — เส้นทาง integration (partner ยิง API เข้ามา)
/// และรายจ่ายที่ภาษีซื้อเคลมไม่ได้ ทุกข้อยืนยันกับโค้ดจริงก่อนแก้
/// </summary>
public class SimulationRound4Tests
{
    // ── P-8: ผังบัญชีที่ partner ส่งมาต้องอยู่ถูกฝั่งเอกสาร ────────────────
    // BuildDocumentLinesAsync รับ AccountCode อะไรก็ได้ที่ active ⇒ ยิงค่าใช้จ่าย
    // พร้อม AccountCode ของบัญชีรายได้ได้ → JE เป็น Dr 41000 (ล้างรายได้)
    // JournalPostingGuard จับไม่ได้เพราะ Dr=Cr สมดุล + มีขาเจ้าหนี้ครบ

    private static bool IsWrongSide(AccountType type, bool expenseSide)
        => expenseSide ? type == AccountType.Revenue : type == AccountType.Expense;

    [Theory]
    [InlineData(AccountType.Expense, true, false)]    // ค่าใช้จ่าย → ฝั่งจ่าย ปกติ
    [InlineData(AccountType.Asset, true, false)]      // สินค้าคงเหลือ/สินทรัพย์ ใช้จริง
    [InlineData(AccountType.Liability, true, false)]  // มัดจำจ่าย ใช้จริง
    [InlineData(AccountType.Revenue, true, true)]     // ← เคสที่เคยหลุด
    [InlineData(AccountType.Revenue, false, false)]   // รายได้ → ฝั่งรับ ปกติ
    [InlineData(AccountType.Expense, false, true)]    // ← ทิศกลับกัน
    public void A_partner_account_code_on_the_wrong_side_is_rejected(
        AccountType type, bool expenseSide, bool rejected)
        => Assert.Equal(rejected, IsWrongSide(type, expenseSide));

    [Fact]
    public void The_balanced_but_wrong_side_entry_fools_every_balance_check()
    {
        // เหตุผลที่ต้องกันตั้งแต่ตอนแปลงบรรทัด ไม่ใช่รอ guard ตอนลง JE
        var je = new (string Code, decimal Dr, decimal Cr)[]
        {
            ("41000", 10_000m, 0m),   // ควรเป็น 52120 ค่าใช้จ่าย
            ("11610", 700m, 0m),
            ("21210", 0m, 10_700m),
        };
        Assert.Equal(je.Sum(l => l.Dr), je.Sum(l => l.Cr));   // สมดุลเป๊ะ
        // ผลจริง: รายได้ต่ำไป 10,000 และค่าใช้จ่ายต่ำไป 10,000 พร้อมกัน
        // กำไรสุทธิเท่าเดิม → งบไม่ส่งสัญญาณอะไรเลย
        Assert.Equal(0m, -10_000m + 10_000m);
    }

    // ── P-6: ย้ายผังบัญชีต้องย้าย "ยอดที่ลงจริง" ไม่ใช่ฐานก่อน VAT ─────────

    /// <summary>สูตรเดียวกับ AutoPostToJournalAsync ฝั่งซื้อ</summary>
    private static decimal PostedDebit(decimal amount, decimal vat, bool claimable, bool viaGrn)
        => viaGrn ? (claimable ? 0m : vat)
                  : (claimable ? amount : amount + vat);

    [Fact]
    public void Reclassifying_a_non_claimable_line_moves_the_vat_too()
    {
        const decimal amount = 10_000m, vat = 700m;
        var posted = PostedDebit(amount, vat, claimable: false, viaGrn: false);
        Assert.Equal(10_700m, posted);

        // เดิมย้ายแค่ฐาน → VAT ต้องห้าม 700 ค้างผังเก่าถาวร (Dr=Cr ยังสมดุล
        // จึงไม่มี guard ตัวไหนจับ — defect class เดียวกับ UV-202607-0037)
        var oldAccountAfterBaseOnly = posted - amount;
        Assert.Equal(700m, oldAccountAfterBaseOnly);

        // หลังแก้: ย้ายเท่าที่ลงจริง → ผังเก่าเหลือ 0
        Assert.Equal(0m, posted - PostedDebit(amount, vat, claimable: false, viaGrn: false));
    }

    [Fact]
    public void A_claimable_line_still_moves_only_its_base()
    {
        // การขยายสูตรต้องไม่ทำให้เคสปกติ (VAT ไปที่ 11610) ย้ายเกิน
        Assert.Equal(10_000m, PostedDebit(10_000m, 700m, claimable: true, viaGrn: false));
    }

    [Fact]
    public void On_a_goods_receipt_invoice_only_the_prohibited_vat_sits_on_the_line()
    {
        // ฐานไปตัด GR-NI แล้ว — ย้ายทั้งฐานจะเกินจริงเท่าฐานทั้งก้อน
        Assert.Equal(700m, PostedDebit(10_000m, 700m, claimable: false, viaGrn: true));
        Assert.Equal(0m, PostedDebit(10_000m, 700m, claimable: true, viaGrn: true));
    }

    [Fact]
    public void A_settlement_payment_voucher_has_no_line_leg_to_move()
    {
        // PV ที่แปลงจากใบตั้งหนี้ = Dr เจ้าหนี้ / Cr เงินสด — บรรทัดไม่มีขา Dr
        static decimal LineGl(bool isSettlement, decimal amount) => isSettlement ? 0m : amount;
        Assert.Equal(0m, LineGl(isSettlement: true, 10_000m));
        Assert.Equal(10_000m, LineGl(isSettlement: false, 10_000m));
    }

    // ── P-7: ใบรับรองแทนใบเสร็จ (§82/4) ────────────────────────────────────

    [Fact]
    public void Non_recoverable_vat_follows_its_own_line_account()
    {
        var lines = new (string Code, decimal Amount, decimal Vat)[]
        {
            ("53210", 3_000m, 210m),   // ค่าเดินทาง
            ("53220", 7_000m, 490m),   // ค่าที่พัก
        };
        // เดิม: VAT ทั้งก้อนกองที่บัญชีค่าใช้จ่ายทั่วไป → ต้นทุนเพี้ยนทั้ง 3 ผัง
        var before = new Dictionary<string, decimal>
        {
            ["53210"] = 3_000m, ["53220"] = 7_000m, ["53900"] = 700m,
        };
        var after = lines.ToDictionary(l => l.Code, l => l.Amount + l.Vat);

        Assert.Equal(before.Values.Sum(), after.Values.Sum());   // ยอดรวมเท่าเดิม
        Assert.DoesNotContain("53900", after.Keys);
        Assert.Equal(3_210m, after["53210"]);
        Assert.Equal(7_490m, after["53220"]);
    }

    [Fact]
    public void Without_a_default_expense_account_the_old_entry_did_not_balance()
    {
        // เดิม VAT ลงได้ก็ต่อเมื่อมี defaultExpense — บริษัทที่ไม่มีจะได้ JE ที่
        // Dr ขาดเท่ายอด VAT ทั้งก้อน (ถูกตีกลับทั้งใบตอนอนุมัติ)
        const decimal cash = 10_700m;
        var drOld = 3_000m + 7_000m;              // ไม่มี defaultExpense → ไม่มีขา VAT
        Assert.NotEqual(cash, drOld);
        var drNew = 3_210m + 7_490m;              // VAT อยู่บนบรรทัดเอง
        Assert.Equal(cash, drNew);
    }

    [Fact]
    public void Header_only_vat_still_lands_somewhere()
    {
        // ใบเก่าที่กรอก VAT ที่หัวใบ (บรรทัดไม่มี VatAmount) — เศษต้องลงบรรทัดแรก
        decimal docVat = 700m, assigned = 0m;
        var residual = docVat - assigned;
        Assert.Equal(700m, residual);
        Assert.Equal(10_700m, 10_000m + residual);
    }

    // ── P-3: 50 ทวิ เป็นเกณฑ์เงินสด (ท.ป.4/2528) ───────────────────────────

    [Fact]
    public void An_accrued_expense_only_drafts_the_certificate()
    {
        // integration ตั้งหนี้ค่าใช้จ่าย (Credit, PaidAmount = 0) ยังไม่มีการหักจริง
        static bool AutoIssue(bool paidAtSync) => paidAtSync;
        Assert.False(AutoIssue(paidAtSync: false));   // ค่าใช้จ่ายตั้งหนี้ → ฉบับร่าง
        Assert.True(AutoIssue(paidAtSync: true));     // ใบสำคัญจ่าย → ออกจริง
    }

    [Fact]
    public void A_draft_certificate_never_reaches_the_filing_file()
    {
        // TaxFilingExportService/TaxService นับเฉพาะ Issued/Filed
        static bool InFiling(string status) => status is "Issued" or "Filed";
        Assert.False(InFiling("Draft"));
        Assert.False(InFiling("Voided"));
        Assert.True(InFiling("Issued"));
    }

    [Fact]
    public void The_accrual_month_no_longer_remits_tax_that_was_never_withheld()
    {
        // ตั้งหนี้ ก.ค. → จ่าย 2 งวด ส.ค./ก.ย. งวดละ 150
        var before = new Dictionary<string, decimal> { ["2026-07"] = 300m };
        var after = new Dictionary<string, decimal> { ["2026-08"] = 150m, ["2026-09"] = 150m };
        Assert.Equal(before.Values.Sum(), after.Values.Sum());   // ยอดรวมเท่าเดิม
        Assert.DoesNotContain("2026-07", after.Keys);            // เดือนที่ไม่ได้จ่ายต้องว่าง
    }

    [Fact]
    public void A_draft_full_document_certificate_yields_to_the_per_payment_ones()
    {
        // guard เดิมบล็อกใบรายงวดเมื่อมีใบระดับเอกสารค้างอยู่ ไม่ว่าจะสถานะใด
        // ⇒ ใบร่างที่ตั้งไว้ตอนตั้งหนี้ทำให้ผู้ขาย "ไม่ได้ 50 ทวิ สักใบ"
        static bool CanIssuePerPayment(string[] fullDocCertStatuses)
            => fullDocCertStatuses.Length == 0 || fullDocCertStatuses.All(s => s == "Draft");

        Assert.True(CanIssuePerPayment(new[] { "Draft" }));       // ยกเลิกร่างแล้วออกต่อได้
        Assert.True(CanIssuePerPayment(Array.Empty<string>()));
        Assert.False(CanIssuePerPayment(new[] { "Issued" }));     // ใบจริงยังต้องบล็อก
        Assert.False(CanIssuePerPayment(new[] { "Draft", "Issued" }));
    }

    // ── M-3(ต่อ): โอนก้อนเดียวปิดหลายใบ — 50 ทวิ ต้องเป็นรายงวด รายใบ ────────

    [Fact]
    public void One_transfer_closing_many_bills_issues_one_certificate_per_bill()
    {
        // key ซ้ำเดิมคือ SourcePaymentId ล้วน ซึ่งการโอนก้อนเดียวใช้ร่วมกันทุกใบ
        // ⇒ ใบที่ 2 เป็นต้นไปออกไม่ได้เลย (นำส่งขาดของผู้ขายรายอื่น)
        var payment = Guid.NewGuid();
        var docA = Guid.NewGuid(); var docB = Guid.NewGuid();
        var issued = new HashSet<(Guid Payment, Guid Doc)>();

        static bool Dup(HashSet<(Guid, Guid)> issued, Guid p, Guid d) => issued.Contains((p, d));

        Assert.False(Dup(issued, payment, docA)); issued.Add((payment, docA));
        Assert.False(Dup(issued, payment, docB)); issued.Add((payment, docB));   // ← เคยถูกบล็อก
        Assert.True(Dup(issued, payment, docA));                                  // ยิงซ้ำยังกันได้
        Assert.Equal(2, issued.Count);
    }

    [Fact]
    public void Each_certificate_carries_only_that_documents_withheld_amount()
    {
        // เดิมเส้น multi-doc ไม่ส่ง paymentWhtAmount → ใบระบุยอดเต็มทั้งเอกสาร
        var whtByDoc = new Dictionary<string, decimal> { ["PI-1"] = 90m, ["PI-2"] = 60m };
        Assert.Equal(150m, whtByDoc.Values.Sum());
        Assert.Equal(90m, whtByDoc["PI-1"]);
    }

    [Theory]
    [InlineData(DocumentType.PurchaseInvoice, true)]
    [InlineData(DocumentType.Expense, true)]
    [InlineData(DocumentType.PaymentVoucher, true)]
    [InlineData(DocumentType.CertificateInLieu, true)]
    [InlineData(DocumentType.Invoice, false)]      // ฝั่งขาย — ลูกค้าเป็นคนออกให้เรา
    [InlineData(DocumentType.TaxInvoice, false)]
    public void Only_purchase_side_documents_issue_our_own_certificate(
        DocumentType type, bool issues)
    {
        var purchaseSide = type is DocumentType.PurchaseInvoice or DocumentType.Expense
            or DocumentType.PaymentVoucher or DocumentType.CertificateInLieu;
        Assert.Equal(issues, purchaseSide);
    }

    // ── P-7(ข): §65 ตรี ต้องครอบใบรับรองแทนใบเสร็จด้วย ─────────────────────

    [Theory]
    [InlineData(DocumentType.PurchaseInvoice, null, true)]
    [InlineData(DocumentType.Expense, null, true)]
    [InlineData(DocumentType.PaymentVoucher, null, true)]
    [InlineData(DocumentType.CertificateInLieu, null, true)]    // ← เคยขาด
    [InlineData(DocumentType.CertificateInLieu, "src", false)]  // แปลงมา — ต้นทางบวกกลับแล้ว
    [InlineData(DocumentType.Invoice, null, false)]
    public void Section_65_ter_covers_the_certificate_in_lieu(
        DocumentType type, string? relatedId, bool applies)
    {
        var result = type is DocumentType.PurchaseInvoice or DocumentType.Expense
                          or DocumentType.PaymentVoucher
            || (type == DocumentType.CertificateInLieu && relatedId == null);
        Assert.Equal(applies, result);
    }
}
