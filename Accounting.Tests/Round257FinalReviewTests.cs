using Accounting.Helpers;
using Accounting.Services.Implementations;
using Xunit;

namespace Accounting.Tests;

/// <summary>
/// รอบ 257 ปิดท้าย (ผลตรวจฝ่ายค้านรอบสุดท้าย · คำตัดสิน 257-5-1…257-5-4) — ตัวตัดสิน pure ของ MED 1–6 + LOW ทั้งสองทิศ
/// (ทางเข้าจริงบน PostgreSQL อยู่ที่ Db/WhtCertRound257DbTests · Db/PartnerExchangeRound257DbTests · Db/PortalPermissionsRound257DbTests)
/// </summary>
public class Round257FinalReviewTests
{
    // ═══════════ MED-1/2 + LOW: เครดิตของปีที่ยื่น ภ.ง.ด.50 แล้ว ═══════════

    [Fact]
    public void Z_LOW_แก้ยอดภาษีถูกหัก_นับว่าแก้เมื่อยอดต่างจริง_ทิศตรงข้ามยอดเดิมหรือส่งศูนย์ไม่นับ()
    {
        Assert.True(WhtCreditFiledYearGuard.AmountChanged(1_000m, 900m));
        Assert.True(WhtCreditFiledYearGuard.AmountChanged(1_000m, 1_000.01m));
        Assert.False(WhtCreditFiledYearGuard.AmountChanged(1_000m, 1_000m));
        Assert.False(WhtCreditFiledYearGuard.AmountChanged(1_000m, 1_000.004m));   // ต่างไม่ถึงสตางค์
        Assert.False(WhtCreditFiledYearGuard.AmountChanged(1_000m, 0m));           // 0 = ผู้เรียกไม่เขียนยอด (คงเดิม)
    }

    [Fact]
    public void Z_MED2_ด่านกล่องรับของปีที่ยื่นแล้ว_บอกปี_พศ_และทางไปต่อ()
    {
        var gate = WhtCreditFiledYearGuard.IntakeReviewGate(2025);
        Assert.Contains("2568", gate);
        Assert.Contains("ปลดล็อก", gate);
        Assert.Contains("ไม่ยืนยันอัตโนมัติ", gate);
        var note = WhtCreditFiledYearGuard.SourceChangedFiledYearMessage(2025, "ลูกค้า ก", 300m, "ใบต้นทางถูกยกเลิก");
        Assert.Contains("2568", note);
        Assert.Contains("ยื่น ภ.ง.ด.50 เพิ่มเติม", note);
        Assert.Contains("ใบต้นทางถูกยกเลิก", note);
    }

    // ═══════════ MED-3: ทาง "ได้ใบกำกับนอกระบบ" ต้องผ่านกติกา VAT เดียวกับตอนแนบ ═══════════

    [Fact]
    public void Z_MED3_สายมีใบกำกับผ่านระบบ_VATไม่ตรง_ปฏิเสธพร้อมทางไปต่อ_ทิศตรงข้ามVATตรงหรือไม่มีใบกำกับผ่านระบบ_ผ่าน()
    {
        var approved = PartnerInputVatEvidence.ExternalVatProblem(50m, 70m, approved: true);
        Assert.NotNull(approved);
        Assert.Contains("ยกเลิกใบซื้อนี้แล้วบันทึกใหม่", approved);
        Assert.Contains("ออกใบแทน", approved);
        var draft = PartnerInputVatEvidence.ExternalVatProblem(50m, 70m, approved: false);
        Assert.Contains("แก้ยอด/VAT ของใบร่าง", draft);
        // ทิศตรงข้าม
        Assert.Null(PartnerInputVatEvidence.ExternalVatProblem(70m, 70.004m, approved: true));   // ตรง (±0.005 — กติกาเดียวกับ VatMatches)
        Assert.Null(PartnerInputVatEvidence.ExternalVatProblem(null, 70m, approved: true));      // ไม่มีใบกำกับผ่านระบบ ⇒ ผู้ขายออกนอกระบบจริง
        // e-Tax ประทับเวลาบนใบที่รอใบกำกับ = ไม่มีไฟล์ลงลายมือชื่อ ⇒ ปฏิเสธ (ตัวตัดสินเดียวกับทางปกติ)
        Assert.NotNull(PartnerInputVatEvidence.StampedEtaxProblem(PartnerInputVatEvidence.AwaitingTaxInvoice));
    }

    // ═══════════ MED-4: คำยืนยันนอกระบบของใบกำกับใบเดียวกัน คงไว้เมื่อใบกำกับผ่านระบบมาถึง ═══════════

    [Fact]
    public void Z_MED4_ยืนยันนอกระบบ_เลขตรง_VATตรง_คงคำยืนยัน_ทิศตรงข้ามเลขไม่ตรงหรือไม่ได้ยืนยันนอกระบบ_ตั้งกลับ()
    {
        const string Confirmed = PartnerInputVatEvidence.PaperConfirmed;
        Assert.True(PartnerInputVatEvidence.SameTaxInvoiceNumber(" iv-2569/001 ", "IV-2569/001"));
        Assert.False(PartnerInputVatEvidence.SameTaxInvoiceNumber("IV-2569/001", "IV-2569/010"));   // ไม่ fuzzy
        Assert.False(PartnerInputVatEvidence.SameTaxInvoiceNumber(null, "IV-1"));
        Assert.False(PartnerInputVatEvidence.SameTaxInvoiceNumber(" ", " "));

        Assert.True(PartnerInputVatEvidence.ExternalConfirmationCovers(Confirmed, true, "IV-1", "IV-1"));
        Assert.False(PartnerInputVatEvidence.ExternalConfirmationCovers(Confirmed, false, "IV-1", "IV-1"));   // คำยืนยันของใบแจ้งหนี้ (ก่อน D-H4)
        Assert.False(PartnerInputVatEvidence.ExternalConfirmationCovers(Confirmed, true, "IV-1", "IV-2"));
        Assert.False(PartnerInputVatEvidence.ExternalConfirmationCovers(PartnerInputVatEvidence.PaperRequired, true, "IV-1", "IV-1"));   // แก้ยอดหลังยืนยัน

        var offered = PartnerInputVatEvidence.Of(sourceIsTaxInvoice: true, signed: false, signedFileDelivered: false);
        Assert.Equal(Confirmed, PartnerInputVatEvidence.OnNewSource(Confirmed, offered, true, priorSourceWasTaxInvoice: false, vatMatches: true,
            externalConfirmationCovers: true));
        // ทิศตรงข้าม: ไม่ครอบ ⇒ ตั้งกลับ (พฤติกรรมเดิม D-H4) · VAT ไม่ตรง ⇒ รอใบกำกับ แม้เลขตรง
        Assert.Equal(PartnerInputVatEvidence.PaperRequired, PartnerInputVatEvidence.OnNewSource(Confirmed, offered, true, false, true, externalConfirmationCovers: false));
        Assert.Equal(PartnerInputVatEvidence.AwaitingTaxInvoice, PartnerInputVatEvidence.OnNewSource(Confirmed, offered, true, false, vatMatches: false,
            externalConfirmationCovers: true));
        Assert.Equal(PartnerInputVatEvidence.SourceVoided, PartnerInputVatEvidence.OnNewSource(PartnerInputVatEvidence.SourceVoided, offered, true, false, true, true));
    }

    // ═══════════ MED-5: ยืนยันแล้วแต่สายไม่มีใบกำกับเลย ═══════════

    [Fact]
    public void Z_MED5_ยืนยันแล้ว_สายไม่มีใบกำกับ_เข้าเกณฑ์_ทิศตรงข้ามยืนยันนอกระบบ_มีใบกำกับ_หรือข้อมูลไม่ครบ_ไม่แตะ()
    {
        Assert.True(PartnerEvidenceRepair.ConfirmedWithoutCarrier(PartnerInputVatEvidence.PaperConfirmed, 1, 1, 0, confirmedThroughExternalPath: false));
        Assert.True(PartnerEvidenceRepair.ConfirmedWithoutCarrier(PartnerInputVatEvidence.EtaxVerified, 2, 2, 0, false));
        // ทิศตรงข้าม
        Assert.False(PartnerEvidenceRepair.ConfirmedWithoutCarrier(PartnerInputVatEvidence.PaperConfirmed, 1, 1, 0, confirmedThroughExternalPath: true));
        Assert.False(PartnerEvidenceRepair.ConfirmedWithoutCarrier(PartnerInputVatEvidence.PaperConfirmed, 2, 2, 1, false));   // มีใบกำกับในสาย (กลุ่มแรกดูแล)
        Assert.False(PartnerEvidenceRepair.ConfirmedWithoutCarrier(PartnerInputVatEvidence.PaperConfirmed, 0, 0, 0, false));   // ไม่มีแถวกล่องเข้า = ไม่รู้
        Assert.False(PartnerEvidenceRepair.ConfirmedWithoutCarrier(PartnerInputVatEvidence.PaperConfirmed, 2, 1, 0, false));   // หาต้นทางไม่ครบ = ไม่รู้
        Assert.False(PartnerEvidenceRepair.ConfirmedWithoutCarrier(PartnerInputVatEvidence.AwaitingTaxInvoice, 1, 1, 0, false));
        Assert.False(PartnerEvidenceRepair.ConfirmedWithoutCarrier(null, 1, 1, 0, false));
        Assert.NotEqual("r257-3-partner-evidence-reset", PartnerEvidenceRepair.MarkerKey);   // คีย์ใหม่ ⇒ งานรันอีกหนึ่งครั้ง
        Assert.True(PartnerInputVatEvidence.BlocksClaim(PartnerInputVatEvidence.AwaitingTaxInvoice));
        Assert.False(PartnerInputVatEvidence.BlocksClaim(PartnerInputVatEvidence.PaperConfirmed));
        Assert.False(PartnerInputVatEvidence.BlocksClaim(null));
    }

    // ═══════════ MED-6: ด่านค่าของ OA กลางไม่ขวางการล้าง ═══════════

    [Fact]
    public void Z_MED6_ค่าเดิมที่เก็บอยู่ส่งกลับมา_ไม่ปฏิเสธ_ทิศตรงข้ามค่าใหม่ที่เป็นของOAกลาง_ยังปฏิเสธ()
    {
        var groups = new[] { "Cgroup1" };
        // บริษัทผู้ให้บริการที่มีค่าของ OA กลางค้างอยู่ — ส่งค่าเดิมกลับ (หรือ null จากปุ่มล้าง) ⇒ ผ่าน
        Assert.Null(LineOwnOaGuard.CentralValueRefusal("@810pamnc", "Cgroup1", "@810PAMNC", groups, storedBasicId: "@810pamnc", storedGroupId: "Cgroup1"));
        Assert.Null(LineOwnOaGuard.CentralValueRefusal(null, null, "@810pamnc", groups, storedBasicId: "@810pamnc", storedGroupId: "Cgroup1"));
        // ทิศตรงข้าม: ตั้งค่าใหม่เป็นของ OA กลาง (ที่เก็บอยู่เป็นค่าอื่น/ว่าง) ⇒ ปฏิเสธเหมือนเดิม
        Assert.Equal(LineOwnOaGuard.CentralValueMessage, LineOwnOaGuard.CentralValueRefusal("@810pamnc", null, "@810pamnc", groups, storedBasicId: "@myshop"));
        Assert.Equal(LineOwnOaGuard.CentralValueMessage, LineOwnOaGuard.CentralValueRefusal("@810pamnc", null, "@810pamnc", groups, storedBasicId: null));
        Assert.Equal(LineOwnOaGuard.CentralValueMessage, LineOwnOaGuard.CentralValueRefusal(null, "Cgroup1", null, groups, storedGroupId: "Cmine"));
        // Basic ID เดิมคงไว้ แต่ Group ID เปลี่ยนเป็นของ OA กลาง ⇒ ปฏิเสธ (ตรวจแยกช่อง)
        Assert.Equal(LineOwnOaGuard.CentralValueMessage,
            LineOwnOaGuard.CentralValueRefusal("@810pamnc", "Cgroup1", "@810pamnc", groups, storedBasicId: "@810pamnc", storedGroupId: null));
    }

    // ═══════════ LOW: ข้อความ + ใบแจ้งยอดพอร์ทัล ═══════════

    [Fact]
    public void Z_LOW_ข้อความผู้รับผิดชอบรายการ_และทางไปต่อของแม่แบบสมุดรายวัน()
    {
        Assert.Contains("ผู้รับผิดชอบรายการ (ผู้สร้างหรือผู้ที่รับรองค่าเริ่มต้น)", RecurringApprovePolicyLoader.AutoApproverGoneReason);
        Assert.Contains("Journal.Manage", RecurringApprovePolicyLoader.AutoPostGoneReason);
        Assert.Contains("สร้างรายการประจำ", RecurringApprovePolicyLoader.AutoPostGoneReason);
    }

    [Theory]
    [InlineData(true, false, false)]    // อ้างใบต้นทางที่อยู่ก่อนงวด/ยอดยกมา ⇒ หักไปแล้วในยอดค้างของใบต้นทาง
    [InlineData(true, true, true)]      // ใบต้นทางขึ้นบรรทัดในงวดด้วยยอดเต็ม ⇒ ใบลดหนี้ต้องลดเอง
    [InlineData(false, false, true)]    // ไม่มีใบต้นทาง (เครดิตลอย) ⇒ ลดเอง
    public void Z_LOW_ใบลดหนี้ในใบแจ้งยอด_ไม่หักซ้ำกับยอดค้างของใบต้นทาง(bool hasSource, bool sourceInPeriod, bool expected)
        => Assert.Equal(expected, DocumentPayLinkPolicy.StatementCreditNoteReduces(hasSource, sourceInPeriod));
}
