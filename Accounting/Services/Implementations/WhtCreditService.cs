using Accounting.Helpers;
using Accounting.Data;
using Accounting.Models.Entities;
using Accounting.Models.Enums;
using Microsoft.EntityFrameworkCore;

namespace Accounting.Services.Implementations;

/// <summary>
/// ทะเบียน "ภาษีที่เราถูกหัก ณ ที่จ่าย" → เครดิตใน ภ.ง.ด.51/50
/// ดูภาพรวมทั้งกระบวนการที่ <c>WHT_CREDIT_PLAN.md</c>
///
/// <para><b>กฎที่คุมตัวเลขทั้งไฟล์:</b> เครดิตภาษีได้เฉพาะรายการที่<b>มีหนังสือ
/// รับรองจริง</b> (Received/Claimed) — รายการ Pending คือยอดที่ถูกหักไปแล้ว
/// (มีใน 11910) แต่ยังไม่ได้รับใบ จึงยัง<b>เครดิตไม่ได้ตามกฎหมาย</b></para>
/// </summary>
public class WhtCreditService
{
    private readonly AccountingDbContext _db;
    private readonly ILogger<WhtCreditService> _logger;

    public WhtCreditService(AccountingDbContext db, ILogger<WhtCreditService> logger)
    { _db = db; _logger = logger; }

    public record WhtCreditRow(
        Guid Id, int TaxYear, string? CertificateNumber, DateTime? CertificateDate,
        Guid? PayerContactId, string PayerName, string? PayerTaxId,
        string PayerFormType, string? IncomeTypeCode,
        decimal IncomeAmount, decimal WhtRate, decimal WhtAmount,
        string Status, Guid? DocumentId, string? DocumentNumber,
        Guid? AttachmentId, Guid? ClaimedInTaxReportId, DateTime? ClaimedAt,
        int? CarriedFromTaxYear, string? Notes,
        // เตือนเมื่ออัตราที่ถูกหักไม่ตรงประเภทเงินได้ (ท.ป.4/2528)
        string? RateWarning,
        // รอบ 203: รหัสประเภทในตารางกลาง (null = ค่าที่เก็บไว้ชี้ขาดไม่ได้)
        string? IncomeTypeCodeResolved = null,
        // รอบ 243a: ตัดสูญพร้อมรายการบัญชี — หน้าแยกป้าย/ปุ่ม (ตัดสูญ ↔ ได้รับใบภายหลัง) จากค่าที่เซิร์ฟเวอร์เก็บ
        Guid? WriteOffJournalEntryId = null, string? WriteOffJournalNumber = null, DateTime? WrittenOffAt = null, string? WriteOffReason = null,
        // = WhtCreditWriteOff.NeedsReversal — ตัดสูญพร้อม JE อยู่ตอนนี้ (หน้าไม่ประกอบเงื่อนไขเอง)
        bool WrittenOffWithJournal = false,
        // รอบ 252 (WP-09): ยอดที่ได้ใบแล้ว (Σ allocation ยืนยัน) · เครดิตที่ใช้ได้ของแถว (Helpers/WhtCreditableAmount) ·
        // สถานะ 50 ทวิ จากตัวตัดสินเดียว (Helpers/WhtCertIntakeStatus ผ่าน WhtCertTracking) — หน้าเว็บแสดงป้าย/โทนอย่างเดียว
        decimal ReceivedAmount = 0m, decimal CreditableAmount = 0m,
        string? WhtCertState = null, string? WhtCertLabel = null, string? WhtCertTone = null, int? WhtCertDaysWaiting = null,
        // ฝ่ายค้าน WP-09 รอบสอง: ปุ่ม "ตัดสูญ" แสดงเฉพาะแถวที่ยังมียอดขาดใบ (WhtCreditWriteOff.AmountOf > 0) · เลขที่/วันที่ใบแรกถูกล็อกเมื่อได้ใบผ่านกล่องรับแล้ว
        // (H-B — ใบถัดไปเข้ากล่องรับ) — เซิร์ฟเวอร์ตัดสิน หน้าเว็บแสดง
        bool CanWriteOff = false, string? CertificateLockReason = null,
        // รอบ 257 ชุด 2 (B-H1): ปุ่ม "ได้รับใบแล้ว" ใช้ได้ไหม (WhtCreditWriteOff.CanMarkReceived — ตัวเดียวกับด่านใน MarkReceivedAsync) ·
        // แถวตัดสูญที่ได้ใบบางส่วนแล้ว ⇒ false (ใบถัดไปเข้ากล่องรับ — ระบบกลับรายการตัดสูญให้) — หน้าไม่แสดงปุ่มที่ถูกปฏิเสธเสมอ
        bool CanMarkReceived = false,
        // รอบ 257 ชุด 2 (B-M3): ผู้ขายแจ้ง "ผู้จ่ายหักผ่าน e-WHT · รอหลักฐาน" ได้ไหม (WhtCertIntakeStatus.CanMarkEWhtPending) · แจ้งไว้แล้วหรือยัง
        bool CanMarkEWht = false, bool EWhtPending = false);

    public record WhtCreditSummary(
        int TaxYear,
        decimal PendingAmount,      // ถูกหักแล้วแต่ยังไม่มีใบ → เครดิตไม่ได้
        decimal ReceivedAmount,     // มีใบแล้ว พร้อมใช้
        decimal ClaimedAmount,      // ใช้ไปแล้ว
        decimal ExpiredAmount,
        decimal CreditableAmount,   // = เครดิตที่ใช้ได้ของรอบ — กติกางวดเดียวกับ ภ.ง.ด.50 (CreditableAmountAsync · รอบ 252 L9)
        int PendingCount,
        decimal GlBalance,          // ยอดคงเหลือบัญชี 11910 ตาม GL (ไม่นับ JE ปิดปี · ไม่นับ JE ตัดสูญรายแถว)
        decimal Difference,         // ทะเบียน (Pending+Received+Claimed+ตัดสูญพร้อม JE) − GL
        // รอบ 243a: ตัดสูญพร้อมรายการบัญชี (Dr 58010 / Cr 11910) — ส่วนหนึ่งของ ExpiredAmount
        decimal WrittenOffAmount = 0m,
        // N8: ป้าย KPI ใช้รหัสผังจากเซิร์ฟเวอร์ (หน้าไม่พิมพ์ "58010" เอง)
        string WriteOffAccountCode = WhtCreditWriteOff.ExpenseAccountCode);

    // ═══════════ อ่าน ═══════════

    /// <summary>ใบต้นทางที่ "ปิดยอดแล้ว แต่ WHT ที่บันทึกไว้ไม่ครบ" — ตรวจย้อนหลัง
    ///
    /// <para>เกิดจากใบเสร็จที่ตัดลูกหนี้ด้วย <b>ยอดเงินที่รับจริง</b> (net) แทน
    /// <b>ยอด gross</b> ใต้เกณฑ์ Cash ⇒ Cr AR ขาดไปเท่ากับ WHT ⇒ ลูกหนี้ค้างใน GL
    /// ทั้งที่เอกสารขึ้นว่าชำระครบ และเครดิตภาษี (11910) ไม่เคยถูกบันทึก
    /// ⇒ ใช้ใน ภ.ง.ด.50 ไม่ได้. ตอนนี้มี guard กันตอนอนุมัติแล้ว แต่ใบที่ออกไป
    /// ก่อนหน้าต้องตามเก็บเอง — ตัวนี้ไว้หาว่ามีใบไหนบ้าง</para></summary>
    public record StrandedArRow(Guid DocumentId, string DocumentNumber, DateTime DocumentDate,
        string ContactName, decimal SourceWht, decimal RecordedWht, decimal StrandedAr);

    public async Task<List<StrandedArRow>> FindStrandedArFromMissingWhtAsync(Guid companyId)
    {
        var basis = await _db.CompanySettings.AsNoTracking()
            .Where(c => c.CompanyId == companyId && !c.IsDeleted)
            .Select(c => (WhtRecognitionBasis?)c.WhtRecognitionBasis)
            .FirstOrDefaultAsync() ?? WhtRecognitionBasis.Cash;
        // เกณฑ์ Accrual ตั้ง AR ไว้ net อยู่แล้ว — ไม่มีเคสนี้
        if (basis != WhtRecognitionBasis.Cash) return new List<StrandedArRow>();

        var settled = await _db.Documents.AsNoTracking()
            .Where(d => d.CompanyId == companyId && !d.IsDeleted
                && d.WithholdingTaxAmount > 0m
                && (d.DocumentType == DocumentType.Invoice || d.DocumentType == DocumentType.TaxInvoice
                    || d.DocumentType == DocumentType.DebitNote)
                && d.Status != DocumentStatus.Draft && d.Status != DocumentStatus.Voided
                && d.Status != DocumentStatus.Rejected
                && d.PaidAmount >= d.TotalAmount - 0.01m)
            .Select(d => new { d.Id, d.DocumentNumber, d.DocumentDate, d.WithholdingTaxAmount,
                ContactName = d.Contact != null ? d.Contact.Name : "" })
            .ToListAsync();
        if (settled.Count == 0) return new List<StrandedArRow>();

        var ids = settled.Select(x => x.Id).ToList();
        var settleTypes = new[] { DocumentType.Receipt, DocumentType.ReceiptVoucher,
            DocumentType.PaymentVoucher, DocumentType.CertificateInLieu };
        var viaDocs = (await _db.Documents.AsNoTracking()
            .Where(r => r.CompanyId == companyId && !r.IsDeleted
                && r.RelatedDocumentId != null && ids.Contains(r.RelatedDocumentId.Value)
                && settleTypes.Contains(r.DocumentType)
                && r.Status != DocumentStatus.Voided && r.Status != DocumentStatus.Draft
                && r.Status != DocumentStatus.Rejected)
            .GroupBy(r => r.RelatedDocumentId!.Value)
            .Select(g => new { Id = g.Key, Wht = g.Sum(x => x.WithholdingTaxAmount) })
            .ToListAsync()).ToDictionary(x => x.Id, x => x.Wht);
        var viaPays = (await _db.Payments.AsNoTracking()
            .Where(p => !p.IsDeleted && ids.Contains(p.DocumentId))
            .GroupBy(p => p.DocumentId)
            .Select(g => new { Id = g.Key, Wht = g.Sum(x => x.WithholdingTaxAmount) })
            .ToListAsync()).ToDictionary(x => x.Id, x => x.Wht);

        var rows = new List<StrandedArRow>();
        foreach (var d in settled)
        {
            var recorded = (viaDocs.TryGetValue(d.Id, out var a) ? a : 0m)
                         + (viaPays.TryGetValue(d.Id, out var b) ? b : 0m);
            var stranded = Math.Round(d.WithholdingTaxAmount - recorded, 2);
            if (stranded > 0.01m)
                rows.Add(new StrandedArRow(d.Id, d.DocumentNumber, d.DocumentDate,
                    d.ContactName, d.WithholdingTaxAmount, recorded, stranded));
        }
        return rows.OrderByDescending(r => r.StrandedAr).ToList();
    }

    public async Task<List<WhtCreditRow>> ListAsync(Guid companyId, int? taxYear, WhtCreditStatus? status)
    {
        var q = _db.WhtCreditsReceived.AsNoTracking().Where(w => w.CompanyId == companyId);
        if (taxYear.HasValue) q = q.Where(w => w.TaxYear == taxYear.Value);
        if (status.HasValue) q = q.Where(w => w.Status == status.Value);

        var rows = await q
            .OrderByDescending(w => w.TaxYear)
            .ThenBy(w => w.Status)
            .ThenByDescending(w => w.CertificateDate ?? w.CreatedAt)
            .Select(w => new
            {
                w.Id, w.TaxYear, w.CertificateNumber, w.CertificateDate,
                w.PayerContactId, w.PayerName, w.PayerTaxId, w.PayerFormType,
                w.IncomeTypeCode, w.IncomeAmount, w.WhtRate, w.WhtAmount, w.Status,
                w.DocumentId, DocNo = w.Document != null ? w.Document.DocumentNumber : null,
                w.AttachmentId, w.ClaimedInTaxReportId, w.ClaimedAt, w.CarriedFromTaxYear, w.Notes,
                w.WriteOffJournalEntryId, w.WrittenOffAt, w.WriteOffReason, w.WriteOffReversalJournalEntryId, w.ReceivedAmount, w.EvidenceKind,
                WriteOffNo = w.WriteOffJournalEntryId == null ? null
                    : _db.JournalEntries.Where(j => j.Id == w.WriteOffJournalEntryId && j.CompanyId == companyId).Select(j => j.EntryNumber).FirstOrDefault(),
            })
            .ToListAsync();
        // รอบ 252: สถานะ 50 ทวิ ต่อแถว — ตัวโหลด/ตัวตัดสินเดียวกับรายการเอกสาร (ไม่ประกอบป้ายจาก Status เอง)
        var tracking = rows.Count == 0 ? new Dictionary<Guid, WhtCertIntakeStatus.Verdict>()
            : (await WhtCertTracking.LoadAsync(_db, companyId, DateTime.UtcNow, creditIds: rows.Select(r => r.Id).ToList()))
                .ToDictionary(t => t.CreditId, t => t.Verdict);

        return rows.Select(w => new WhtCreditRow(
            w.Id, w.TaxYear, w.CertificateNumber, w.CertificateDate,
            w.PayerContactId, w.PayerName, w.PayerTaxId,
            w.PayerFormType == WhtPayerFormType.Pnd3 ? "ภ.ง.ด.3" : "ภ.ง.ด.53",
            w.IncomeTypeCode, w.IncomeAmount, w.WhtRate, w.WhtAmount,
            w.Status.ToString(), w.DocumentId, w.DocNo,
            w.AttachmentId, w.ClaimedInTaxReportId, w.ClaimedAt, w.CarriedFromTaxYear, w.Notes,
            CheckRate(w.IncomeTypeCode, w.WhtRate, w.CertificateDate ?? DateTime.UtcNow),
            // รหัสในตารางกลางของค่าที่เก็บไว้ (ข้อมูลเก่าเก็บเป็นเลขมาตรา/ข้อความจากตัวอ่าน 50 ทวิ) — หน้าแก้ไขใช้เลือก
            // ตัวเลือกให้ตรง ไม่งั้น select ว่าง ⇒ บันทึกแล้วประเภทเดิมหายเงียบ ๆ
            ThaiWhtRateTable.Resolve(w.IncomeTypeCode)?.Code,
            w.WriteOffJournalEntryId, w.WriteOffNo, w.WrittenOffAt, w.WriteOffReason,
            WhtCreditWriteOff.NeedsReversal(w.Status, w.WriteOffJournalEntryId, w.WriteOffReversalJournalEntryId),
            w.ReceivedAmount, WhtCreditableAmount.Of(w.Status, w.WhtAmount, w.ReceivedAmount),
            tracking.TryGetValue(w.Id, out var v) ? v.State.ToString() : null,
            tracking.TryGetValue(w.Id, out var v2) && v2.Tone != "none" ? v2.Label : null,
            tracking.TryGetValue(w.Id, out var v3) ? v3.Tone : null,
            tracking.TryGetValue(w.Id, out var v4) ? v4.DaysWaiting : null,
            w.Status is WhtCreditStatus.Pending or WhtCreditStatus.Received
                && WhtCreditWriteOff.AmountOf(w.Status, w.WhtAmount, w.ReceivedAmount) > 0.005m,
            w.ReceivedAmount > 0.005m ? CertificateLockedMessage : null,
            WhtCreditWriteOff.CanMarkReceived(w.Status, w.WriteOffJournalEntryId, w.WriteOffReversalJournalEntryId, w.ReceivedAmount),
            WhtCertIntakeStatus.CanMarkEWhtPending(w.Status, w.ReceivedAmount),
            w.EvidenceKind == WhtEvidenceKind.EWithholding && w.Status == WhtCreditStatus.Pending)).ToList();
    }

    /// <summary>สรุปยอดต่อปีภาษี + กระทบกับยอดบัญชี 11910 จริง
    ///
    /// <para>ต่างเมื่อไร = มีรายการที่ลงบัญชีแล้วแต่ไม่อยู่ในทะเบียน (หรือกลับกัน)
    /// ซึ่งแปลว่ายอดเครดิตที่จะยื่นไม่ตรงกับสมุดบัญชี — ต้องตามหาก่อนยื่นแบบ</para></summary>
    public async Task<WhtCreditSummary> SummaryAsync(Guid companyId, int taxYear)
    {
        var rows = await _db.WhtCreditsReceived.AsNoTracking()
            .Where(w => w.CompanyId == companyId && w.TaxYear == taxYear)
            .Select(w => new
            {
                w.Status, w.WhtAmount, w.ReceivedAmount, w.WriteOffJournalEntryId, w.WriteOffReversalJournalEntryId,
                // ฝ่ายค้าน S3 (ตาข่ายชั้นสอง): JE ตัดสูญยังมีผลไหม — ถูกกลับนอกเส้นทะเบียน ⇒ ไม่นับฝั่งทะเบียน ⇒ ผลต่างฟ้อง (ไม่ปิดบังเงียบ)
                Je = _db.JournalEntries.Where(j => j.Id == w.WriteOffJournalEntryId && j.CompanyId == companyId)
                    .Select(j => new { j.Status, j.ReversedByEntryId, j.IsDeleted }).FirstOrDefault(),
            })
            .ToListAsync();

        decimal Sum(WhtCreditStatus st) => rows.Where(r => r.Status == st).Sum(r => r.WhtAmount);
        // รอบ 252 (WP-09): แถว Pending ที่ได้ใบบางส่วน ⇒ ส่วนที่ได้ใบนับเป็น "ใช้เครดิตได้" ส่วนที่ขาดนับเป็น "รอใบ" (ตัวคำนวณเดียว WhtCreditableAmount) ·
        // ผลรวมสองช่องเท่ายอดถูกหักของแถวเสมอ ⇒ กระทบยอด GL 11910 ด้วยสูตรเดิม
        // ฝ่ายค้าน WP-09 รอบสอง (L2 เศษ): Received ที่ใบต่ำกว่ายอดถูกหัก (≤ ฿1) ⇒ เศษนับเป็น "รอใบ" (Missing) ⇒ ทะเบียน = GL
        var pending = rows.Where(r => r.Status is WhtCreditStatus.Pending or WhtCreditStatus.Received)
            .Sum(r => WhtCreditableAmount.Missing(r.Status, r.WhtAmount, r.ReceivedAmount));
        // ฝ่ายค้าน WP-09 M1: แถวตัดสูญเฉพาะส่วนที่ขาด (Expired + ReceivedAmount > 0) ส่วนที่มีใบยังนับเป็นเครดิต
        var received = rows.Where(r => r.Status is WhtCreditStatus.Received or WhtCreditStatus.Pending or WhtCreditStatus.Expired)
            .Sum(r => WhtCreditableAmount.Of(r.Status, r.WhtAmount, r.ReceivedAmount));
        var claimed = Sum(WhtCreditStatus.Claimed);
        // ฝ่ายค้าน WP-09 รอบสอง: ส่วนที่ได้ใบของแถว Expired นับใน received แล้ว ⇒ expired = เฉพาะส่วนที่ไม่มีใบ (ไม่นับซ้ำ)
        var expired = rows.Where(r => r.Status == WhtCreditStatus.Expired)
            .Sum(r => Math.Max(0m, r.WhtAmount) - WhtCreditableAmount.Of(r.Status, r.WhtAmount, r.ReceivedAmount));
        // รอบ 243a: แถวที่ตัดสูญพร้อม JE ยังนับฝั่งทะเบียน และ JE ตัดสูญ (+ ตัวกลับรายการ) ไม่นับฝั่ง GL — สองฝั่งตัดคู่กันเอง ⇒ JE ตัดสูญลงปีไหนก็กระทบยอดตรง
        // (เดิมถ้าตัดสูญข้ามปี: ปีของแถวขาด X · ปีที่ลง JE เกิน X = แถบแดงหลอกสองปี) · แถว Expired ที่ไม่มี JE (ก่อนรอบนี้) ยังไม่นับ ⇒ 11910 ที่ค้างจริงยังฟ้อง
        var writtenOff = rows.Where(r => WhtCreditWriteOff.NeedsReversal(r.Status, r.WriteOffJournalEntryId, r.WriteOffReversalJournalEntryId)
                && r.Je != null && WhtCreditWriteOff.JournalStillLive(r.Je.Status, r.Je.ReversedByEntryId, r.Je.IsDeleted))
            .Sum(r => Math.Max(0m, r.WhtAmount - Math.Max(0m, r.ReceivedAmount)));   // ยอด JE ตัดสูญ = ส่วนที่ขาดใบ (M1)

        var (start, end) = await FiscalRangeAsync(companyId, taxYear);
        // ⚠️ กระทบยอดต้องเทียบ "ก่อนปิดปี" — JE ปิดปี (`SettleYearEndAsync`) ลง
        // วันสุดท้ายของรอบพอดี จึงอยู่ในช่วงที่นับ ⇒ GL ลดลงเท่ายอดที่ล้าง ขณะที่
        // ฝั่งทะเบียน (pending+received+claimed) เท่าเดิม ⇒ หน้าทะเบียนขึ้นแถบแดง
        // "ไม่ตรงกับบัญชี 11910" **ถาวร** ทุกปีที่ปิดไปแล้ว บนหน้าที่เขียนเองว่า
        // "ต้องตรวจก่อนยื่นแบบ" = คำเตือนที่ฟ้องของถูกทุกครั้ง (ปิดด่านโดยไม่ตั้งใจ)
        var glBalance = await GlWhtBalanceAsync(companyId, start, end,
            excludeYearEndClose: taxYear, excludeWriteOffs: true);

        // ฝ่ายค้าน WP-09 L9: "เครดิตที่ใช้ได้" = กติกางวดเดียวกับ ภ.ง.ด.50 (วันที่บนใบอยู่ในรอบ — CreditableInPeriod) ไม่ใช่ปีภาษีที่กรอก
        var creditable = await CreditableAmountAsync(companyId, taxYear);
        return new WhtCreditSummary(taxYear, pending, received, claimed, expired,
            creditable, rows.Count(r => r.Status == WhtCreditStatus.Pending),
            glBalance, Math.Round(pending + received + claimed + writtenOff - glBalance, 2, MidpointRounding.AwayFromZero),
            writtenOff);
    }

    /// <summary>ยอด Dr สุทธิของบัญชี 11910 ในช่วงรอบบัญชี (จาก JE ที่ post จริง)
    ///
    /// <para><paramref name="excludeYearEndClose"/> = ปีภาษีที่ต้อง **ไม่นับ** JE ปิดปี
    /// เข้ามา (ใช้ตอนกระทบยอดกับทะเบียน ซึ่งฝั่งทะเบียนไม่ได้ลดตามการปิดปี) ·
    /// ปล่อย null ตอนเช็ค "ยังล้างได้อีกเท่าไร" เพราะตรงนั้นต้องการ**ยอดคงเหลือสุทธิ**
    /// ไม่งั้นปิดปีซ้ำครั้งที่สองจะเห็นยอดเต็มแล้วล้างเกิน</para></summary>
    /// <param name="excludeWriteOffs">รอบ 243a: ไม่นับ JE ตัดสูญรายแถว + ตัวกลับรายการของมัน (อ้างอิง <see cref="WhtCreditWriteOff.ReferencePrefix"/>) —
    /// ใช้เฉพาะตอนกระทบยอดกับทะเบียน (ซึ่งนับแถวตัดสูญไว้ฝั่งทะเบียนแทน) · ยอดคงเหลือจริงต้องนับ (false)</param>
    public async Task<decimal> GlWhtBalanceAsync(Guid companyId, DateTime start, DateTime end,
        int? excludeYearEndClose = null, bool excludeWriteOffs = false)
    {
        var writeOffPrefix = WhtCreditWriteOff.ReferencePrefix;
        var closeRef = excludeYearEndClose.HasValue
            ? Accounting.Helpers.WhtYearEndReference.For(excludeYearEndClose.Value)
            : null;
        var lines = await (from l in _db.JournalEntryLines.AsNoTracking()
                           join j in _db.JournalEntries.AsNoTracking() on l.JournalEntryId equals j.Id
                           join a in _db.ChartOfAccounts.AsNoTracking() on l.AccountId equals a.Id
                           // ต้องนับ **ทั้ง Posted และ Reversed** ให้ telescope เป็น 0
                           // (pattern เดียวกับ AccountingService.GetGeneralLedgerAsync):
                           // เดิม `Posted && ReversedByEntryId==null` ตัดใบต้นฉบับออก
                           // (Status→Reversed) แต่ **นับใบ reversal เข้า** (Status=Posted,
                           // ReversedByEntryId=null) ⇒ net = −X แทน 0 ⇒ ยอด GL 11910
                           // ที่ใช้กระทบทะเบียน WHT เพี้ยน (variance หลอก / ปิดบัง
                           // mismatch จริง) ก่อนยื่นเครดิต ภ.ง.ด.50/51
                           where j.CompanyId == companyId && !j.IsDeleted && !l.IsDeleted
                                 && (j.Status == JournalEntryStatus.Posted
                                     || j.Status == JournalEntryStatus.Reversed)
                                 && j.EntryDate >= start && j.EntryDate <= end
                                 && a.AccountCode.StartsWith("11910")
                                 && !(closeRef != null && j.Reference == closeRef)
                                 && !(excludeWriteOffs && j.Reference != null && j.Reference.StartsWith(writeOffPrefix))
                           select l.DebitAmount - l.CreditAmount).ToListAsync();
        return Math.Round(lines.Sum(), 2, MidpointRounding.AwayFromZero);
    }

    public async Task<(DateTime Start, DateTime End)> FiscalRangeAsync(Guid companyId, int taxYear)
    {
        var startMonth = await _db.Companies.AsNoTracking()
            .Where(c => c.Id == companyId).Select(c => c.FiscalYearStartMonth).FirstOrDefaultAsync();
        if (startMonth is < 1 or > 12) startMonth = 1;
        // Kind=Utc (รอบ 243a · N3): แอปตั้ง Npgsql.EnableLegacyTimestampBehavior (Program.cs) ⇒ Unspecified ถูกเขียนเป็นค่าเดิม แต่กระบวนการเทสต์ Db
        // ไม่ได้ตั้งสวิตช์นี้ ⇒ Npgsql โหมดปกติปฏิเสธ Unspecified บน timestamptz · Utc ให้ค่าเดียวกันทั้งสองโหมด (วันที่ล้วน ไม่มีการเลื่อนเวลา)
        var start = new DateTime(taxYear, startMonth, 1, 0, 0, 0, DateTimeKind.Utc);
        return (start, start.AddYears(1).AddDays(-1));
    }

    // ═══════════ เขียน ═══════════

    public record UpsertRequest(
        int TaxYear, string? CertificateNumber, DateTime? CertificateDate,
        Guid? PayerContactId, string? PayerName, string? PayerTaxId,
        WhtPayerFormType PayerFormType, string? IncomeTypeCode,
        decimal IncomeAmount, decimal WhtRate, decimal WhtAmount,
        Guid? DocumentId, Guid? AttachmentId, string? Notes);

    public async Task<Guid> CreateAsync(Guid companyId, UpsertRequest r, AttachmentActor? actor = null)
    {
        if (r.WhtAmount <= 0) throw new BusinessRuleException("ยอดภาษีที่ถูกหักต้องมากกว่า 0 — กรอกยอดตามหนังสือรับรอง 50 ทวิ แล้วบันทึกอีกครั้ง");
        // ฝ่ายค้าน WP-09 รอบสอง L4: มีเลขที่ใบ ⇒ ต้องมีวันที่บนใบ (กำหนดรอบเครดิต ภ.ง.ด.50 — CreditableInPeriod)
        if (!string.IsNullOrWhiteSpace(r.CertificateNumber) && r.CertificateDate is null)
            throw new BusinessRuleException(CertificateDateRequiredMessage, DedupeRuleCode);
        return await InTxAsync(async () =>
        {
        // ฝ่ายค้าน WP-09 รอบสอง M-A/M-D: กันซ้ำสองทาง (ทะเบียน + ใบที่ยืนยันแล้วในกล่องรับ) ปีเดียวกัน — ตัวเดียวกับกล่องรับ
        await EnsureCertificateNotRecordedAsync(companyId, r.CertificateNumber, r.PayerTaxId, r.PayerContactId, r.CertificateDate, r.TaxYear);
        var e = new WhtCreditReceived
        {
            CompanyId = companyId,
            TaxYear = r.TaxYear,
            CertificateNumber = Trim(r.CertificateNumber),
            CertificateDate = r.CertificateDate,
            PayerContactId = r.PayerContactId,
            PayerName = Trim(r.PayerName) ?? await PayerNameFromContactAsync(companyId, r.PayerContactId) ?? "",
            PayerTaxId = Trim(r.PayerTaxId),
            PayerFormType = r.PayerFormType,
            IncomeTypeCode = Trim(r.IncomeTypeCode),
            IncomeAmount = r.IncomeAmount,
            WhtRate = r.WhtRate,
            WhtAmount = r.WhtAmount,
            DocumentId = r.DocumentId,
            AttachmentId = r.AttachmentId,
            Notes = Trim(r.Notes),
            // กรอกเองพร้อมเลขที่ใบ = ได้รับใบแล้ว; ไม่มีเลข = ยังรอใบ
            Status = string.IsNullOrWhiteSpace(r.CertificateNumber)
                ? WhtCreditStatus.Pending : WhtCreditStatus.Received,
        };
        await AdoptUnsavedAttachmentAsync(companyId, e.Id, r.AttachmentId, actor);
        _db.WhtCreditsReceived.Add(e);
        await _db.SaveChangesAsync();
        return e.Id;
        });
    }

    public async Task UpdateAsync(Guid companyId, Guid id, UpsertRequest r, AttachmentActor? actor = null)
    {
        // ฝ่ายค้าน S5: ทุกทางเขียนแถวถือล็อกเดียวกับการตัดสูญ + อ่านค่าล่าสุด (แก้ไขชนกับตัดสูญ = ทะเบียน "ได้รับแล้ว" ทั้งที่ 11910 ถูกล้าง)
        await InTxAsync(async () =>
            {
                // ลำดับล็อกเดียวกับกล่องรับ (เลขที่ใบ + ผู้จ่าย ก่อนแถว) — ไม่ให้สองทางรอกันเอง (deadlock)
                if (Trim(r.CertificateNumber) is string lockNo) await LockCertificateAsync(companyId, lockNo, r.PayerTaxId, r.PayerContactId);
                await LockCreditAsync(companyId, id);
                var e = await LoadFresh(companyId, id);
                GuardEditable(e);
                var certNo = Trim(r.CertificateNumber);
                var certChanged = !string.Equals(certNo, e.CertificateNumber, StringComparison.Ordinal);
                var dateChanged = r.CertificateDate != e.CertificateDate;
                // ฝ่ายค้าน WP-09 รอบสอง H-A/H-B: แถวที่ได้ใบบางส่วนผ่านกล่องรับ (ReceivedAmount > 0) — เลขที่ใบแรกแก้ไม่ได้ · วันที่แก้ได้เฉพาะในรอบเดียวกัน
                // (ตัวตัดสินเดียวกับกล่องรับ H2) · ยอดถูกหักต่ำกว่ายอดที่ได้ใบไม่ได้ · สถานะ/ยอดที่ได้ใบไม่ถูกเปลี่ยนเพราะ "มีเลขที่ใบ" (ใบถัดไปเข้ากล่องรับ)
                var partCertified = e.ReceivedAmount > 0.005m;
                if (partCertified)
                {
                    if (certChanged)
                        throw new BusinessRuleException(CertificateLockedMessage, DedupeRuleCode, 409);
                    if (dateChanged)
                    {
                        var fyStart = await FiscalStartMonthAsync(companyId);
                        if (r.CertificateDate is not DateTime nd || WhtCertIntakeVerdict.CrossesCreditPeriod(e.CertificateDate, nd, fyStart))
                            throw new BusinessRuleException("แก้วันที่บนใบข้ามรอบบัญชีไม่ได้ — แถวนี้ได้ใบผ่านกล่องรับแล้ว และเครดิตนับงวดตามวันที่ใบแรก · "
                                + "ถ้าวันที่ผิดจริง ให้ตีกลับใบในกล่องรับแล้วส่งใบใหม่", DedupeRuleCode, 409);
                    }
                    if (r.WhtAmount > 0 && r.WhtAmount < e.ReceivedAmount - 0.005m)
                        throw new BusinessRuleException($"ยอดภาษีที่ถูกหัก {r.WhtAmount:N2} ต่ำกว่ายอดที่ได้ใบแล้ว {e.ReceivedAmount:N2} — ตรวจยอดอีกครั้ง", DedupeRuleCode, 409);
                }
                else if (certNo != null && (certChanged || e.Status == WhtCreditStatus.Pending) && r.CertificateDate is null)
                    throw new BusinessRuleException(CertificateDateRequiredMessage, DedupeRuleCode);   // L4
                if (certNo != null && (certChanged || dateChanged || r.TaxYear != e.TaxYear
                        || ThaiTaxId.Normalize(r.PayerTaxId) != ThaiTaxId.Normalize(e.PayerTaxId)))
                    await EnsureCertificateNotRecordedAsync(companyId, certNo, r.PayerTaxId, r.PayerContactId, r.CertificateDate, r.TaxYear, exceptCreditId: e.Id);
                var whtBefore = e.WhtAmount;
                e.TaxYear = r.TaxYear;
                e.CertificateNumber = certNo;
                e.CertificateDate = r.CertificateDate;
                e.PayerContactId = r.PayerContactId;
                if (!string.IsNullOrWhiteSpace(r.PayerName)) e.PayerName = r.PayerName.Trim();
                e.PayerTaxId = Trim(r.PayerTaxId);
                e.PayerFormType = r.PayerFormType;
                e.IncomeTypeCode = Trim(r.IncomeTypeCode);
                e.IncomeAmount = r.IncomeAmount;
                e.WhtRate = r.WhtRate;
                if (r.WhtAmount > 0) e.WhtAmount = r.WhtAmount;
                // ไฟล์เดิมของรายการ (ส่งกลับมาเหมือนเดิมตอนแก้ช่องอื่น) ไม่ต้องตัดสินซ้ำ — แถวเก่าที่ชี้ไฟล์ชนิดอื่นไว้ก่อนมีด่าน Q4
                // ต้องยังแก้ช่องอื่นได้ (ไม่ถูกบังคับเปลี่ยนไฟล์)
                if (r.AttachmentId.HasValue && r.AttachmentId != e.AttachmentId)
                {
                    await AdoptUnsavedAttachmentAsync(companyId, e.Id, r.AttachmentId, actor);
                    e.AttachmentId = r.AttachmentId;
                }
                e.Notes = Trim(r.Notes);
                if (!partCertified)
                {
                    // แถวที่ยังไม่มีใบผ่านกล่องรับ (ReceivedAmount 0): ผู้ใช้กรอกเลขที่ + วันที่ใบเอง = ประกาศว่าได้รับใบแล้ว (เครดิตได้) · ลบเลขออก = กลับไปรอใบ
                    e.Status = string.IsNullOrWhiteSpace(e.CertificateNumber)
                        ? WhtCreditStatus.Pending : WhtCreditStatus.Received;
                }
                else if (e.WhtAmount != whtBefore)
                    e.Status = WhtCreditableAmount.StatusForReceived(e.WhtAmount, e.ReceivedAmount);   // ยอดถูกหักเปลี่ยน ⇒ ตัดสินจากยอดที่ได้ใบ (ไม่แตะ ReceivedAmount)
                e.UpdatedAt = DateTime.UtcNow;
                await _db.SaveChangesAsync();
                return true;
            });
    }

    /// <summary>แนบสแกนหนังสือรับรอง + ระบุเลขที่/วันที่ → เปลี่ยนเป็น "ได้รับแล้ว"
    ///
    /// <para>รอบ 243a (คำตัดสิน 243-5): แถวที่<b>ตัดสูญพร้อมรายการบัญชีแล้ว</b> (<see cref="WhtCreditWriteOff.NeedsReversal"/>) ได้ใบภายหลัง ⇒
    /// กลับรายการ JE ตัดสูญ (<see cref="ReverseWriteOffJournalCoreAsync"/> · ลงวันนี้ในงวดที่เปิด) แล้วคืนสถานะ "ได้รับใบแล้ว" ในธุรกรรมเดียว + audit ·
    /// ต้องมีผู้ลงบัญชี (<paramref name="journalActor"/> = ผู้ถือ Journal.Manage) ไม่งั้นปฏิเสธ 403 · ทุกกรณีถือล็อกแถว + อ่านค่าล่าสุด (ฝ่ายค้าน S5)</para></summary>
    public async Task MarkReceivedAsync(Guid companyId, Guid id,
        string certificateNumber, DateTime? certificateDate, Guid? attachmentId, AttachmentActor? actor = null,
        WriteOffActor? journalActor = null)
    {
        if (string.IsNullOrWhiteSpace(certificateNumber))
            throw new BusinessRuleException("ต้องระบุเลขที่หนังสือรับรอง — กฎหมายให้เครดิตเฉพาะรายการที่มีใบจริง");
        var certNo = certificateNumber.Trim();
        var payerPre = await _db.WhtCreditsReceived.AsNoTracking().Where(w => w.Id == id && w.CompanyId == companyId)
            .Select(w => new { w.PayerTaxId, w.PayerContactId }).FirstOrDefaultAsync();
        await InTxAsync(async () =>
        {
            // ลำดับล็อกเดียวกับกล่องรับ (เลขที่ใบ + ผู้จ่าย ก่อนแถว)
            if (payerPre != null) await LockCertificateAsync(companyId, certNo, payerPre.PayerTaxId, payerPre.PayerContactId);
            await LockCreditAsync(companyId, id);
            var e = await LoadFresh(companyId, id);
            // ฝ่ายค้าน WP-09 รอบสอง H-B: แถวที่ได้ใบบางส่วนผ่านกล่องรับ ⇒ ใบถัดไปต้องเข้ากล่องรับ (จัดสรรส่วนที่เหลือพร้อมวันที่ของใบนั้น) —
            // "ได้รับใบแล้ว" ทับเลขที่/วันที่ใบแรกและประกาศครบทั้งแถวไม่ได้ (เดิม MarkFullyCertified ⇒ เครดิตเต็มแถวด้วยใบเดียว)
            if (e.ReceivedAmount > 0.005m)
                throw new BusinessRuleException(CertificateLockedMessage, DedupeRuleCode, 409);
            var writtenOff = WhtCreditWriteOff.NeedsReversal(e.Status, e.WriteOffJournalEntryId, e.WriteOffReversalJournalEntryId);
            if (writtenOff && journalActor == null)
                throw new BusinessRuleException(WrittenOffNeedsJournalManageMessage, WhtCreditWriteOff.ReversalRuleCode, 403);
            if (certificateDate is null)
                throw new BusinessRuleException(CertificateDateRequiredMessage, DedupeRuleCode);   // L4
            if (!writtenOff) GuardEditable(e);
            // M-A: กันซ้ำสองทาง (รวมเส้นแอดมินแพลตฟอร์ม — PlatformWhtCertificateService เรียกเมธอดนี้)
            await EnsureCertificateNotRecordedAsync(companyId, certNo, e.PayerTaxId, e.PayerContactId, certificateDate, e.TaxYear, exceptCreditId: e.Id);
            if (writtenOff)
            {
                var before = new { status = e.Status.ToString(), e.WriteOffJournalEntryId, e.WhtAmount };
                var (reversalId, reversalNo, writeOffNo) = await ReverseWriteOffJournalCoreAsync(companyId, e,
                    $"ได้รับหนังสือรับรอง {certNo}");
                ApplyCertificate(e, certNo, certificateDate);
                await AttachAsync(companyId, e, attachmentId, actor);
                e.Status = WhtCreditStatus.Received;
                e.WriteOffReversalJournalEntryId = reversalId;
                e.Notes = AppendNote(e.Notes, $"ได้รับใบภายหลัง {certNo} — กลับรายการตัดสูญ {writeOffNo} ด้วย {reversalNo}");
                _db.AddChainedAuditLog(new AuditLog
                {
                    CompanyId = companyId, UserId = journalActor.UserId, UserEmail = journalActor.Name,
                    EntityType = nameof(WhtCreditReceived), EntityId = e.Id.ToString(), Action = AuditAction.Update,
                    OldValues = System.Text.Json.JsonSerializer.Serialize(before),
                    NewValues = System.Text.Json.JsonSerializer.Serialize(new
                    {
                        action = "wht-credit-writeoff-reversed", ruleCode = WhtCreditWriteOff.ReversalRuleCode,
                        legalReference = WhtCreditWriteOff.ReversalLegalReference,
                        status = e.Status.ToString(), certificateNumber = certNo, writeOffJournal = writeOffNo, reversalJournal = reversalNo,
                        reversalJournalEntryId = reversalId, amount = e.WhtAmount,
                    }),
                    Timestamp = DateTime.UtcNow,
                });
            }
            else
            {
                ApplyCertificate(e, certNo, certificateDate);
                await AttachAsync(companyId, e, attachmentId, actor);
                e.Status = WhtCreditStatus.Received;
            }
            await _db.SaveChangesAsync();
            return true;
        });
    }

    /// <summary>
    /// รอบ 257 ชุด 2 (B-M3 · คำตัดสิน 257-2-3) — ผู้ขายแจ้งว่าผู้จ่ายหักผ่านระบบ e-Withholding Tax (ไม่มีกระดาษ 50 ทวิ) · หลักฐานยังไม่มา ⇒
    /// แถวรอใบติดป้าย "e-WHT · รอหลักฐาน" (<see cref="WhtCertIntakeStatus.State.EWhtAwaitingEvidence"/> · ตัวกรอง "ewht") · <paramref name="eWithholding"/> false = ยกเลิกการแจ้ง ·
    /// <b>ไม่ใช่หลักฐาน — ไม่เพิ่มเครดิต</b> (ยังเป็น Pending · หลักฐานจริงแนบเข้ากล่องรับแบบ e-WHT) · เฉพาะแถวรอใบที่ยังไม่ได้ใบเลย
    /// (<see cref="WhtCertIntakeStatus.CanMarkEWhtPending"/>) ไม่งั้น 409 · ล็อกแถว · audit chain
    /// </summary>
    public async Task SetEWhtPendingAsync(Guid companyId, Guid id, bool eWithholding, Guid? userId, string actorName)
    {
        await InTxAsync(async () =>
        {
            await LockCreditAsync(companyId, id);
            var e = await LoadFresh(companyId, id);
            if (!WhtCertIntakeStatus.CanMarkEWhtPending(e.Status, e.ReceivedAmount))
                throw new BusinessRuleException("แจ้ง e-WHT ได้เฉพาะรายการที่ยังรอ 50 ทวิ และยังไม่ได้ใบเลย — รายการนี้ได้ใบแล้ว/ตัดสูญ/ใช้เครดิตแล้ว", EWhtRuleCode, 409);
            var want = eWithholding ? WhtEvidenceKind.EWithholding : WhtEvidenceKind.PaperCertificate;
            if (e.EvidenceKind == want) return true;   // กดซ้ำ = ผลเดิม
            var before = e.EvidenceKind.ToString();
            e.EvidenceKind = want;
            e.Notes = AppendNote(e.Notes, eWithholding ? "ผู้จ่ายแจ้งหักผ่าน e-WHT — รอหลักฐาน" : "ยกเลิกการแจ้ง e-WHT — รอ 50 ทวิ ตามปกติ");
            e.UpdatedAt = DateTime.UtcNow;
            _db.AddChainedAuditLog(new AuditLog
            {
                CompanyId = companyId, UserId = userId, UserEmail = actorName,
                EntityType = nameof(WhtCreditReceived), EntityId = e.Id.ToString(), Action = AuditAction.Update,
                OldValues = System.Text.Json.JsonSerializer.Serialize(new { evidenceKind = before }),
                NewValues = System.Text.Json.JsonSerializer.Serialize(new
                {
                    action = eWithholding ? "wht-credit-ewht-pending" : "wht-credit-ewht-cleared", ruleCode = EWhtRuleCode,
                    legalReference = "ป.รัษฎากร ม.50 ทวิ · ม.69 ทวิ (e-Withholding Tax — หลักฐานแทนหนังสือรับรอง)",
                    evidenceKind = want.ToString(),
                }),
                Timestamp = DateTime.UtcNow,
            });
            await _db.SaveChangesAsync();
            return true;
        });
    }

    public const string EWhtRuleCode = "RD-50BIS-EWHT-PENDING";

    /// <summary>ข้อความเมื่อแถวตัดสูญพร้อม JE ถูกยืนยันใบโดยผู้ไม่ถือ Journal.Manage — ใช้ร่วมกับเส้นแอดมินแพลตฟอร์ม (N4) ให้บอกทางไปต่อเดียวกัน</summary>
    public const string WrittenOffNeedsJournalManageMessage =
        "รายการนี้ตัดสูญพร้อมรายการบัญชีแล้ว — การรับใบภายหลังต้องกลับรายการบัญชีตัดสูญ จึงต้องให้ผู้มีสิทธิ์จัดการใบสำคัญ (Journal.Manage) "
        + "เปิดหน้า \"ภาษีถูกหัก ณ ที่จ่าย\" แล้วกด \"ได้รับใบแล้ว\" ที่รายการนั้น (ระบบกลับรายการและคืนเครดิตให้)";

    /// <summary>ฝ่ายค้าน WP-09 รอบสอง H-B: แถวที่ได้ใบบางส่วนผ่านกล่องรับ — เลขที่/วันที่ใบแรกล็อก · ใบถัดไปเข้ากล่องรับ</summary>
    public const string CertificateLockedMessage =
        "รายการนี้ได้หนังสือรับรองผ่านกล่องรับแล้ว (บางส่วนหรือทั้งหมด) — เลขที่/วันที่ใบแรกแก้ที่นี่ไม่ได้ และ \"ได้รับใบแล้ว\" ใช้กับรายการนี้ไม่ได้ · "
        + "ใบถัดไป (เช่น งวดที่เหลือ) ให้อัปโหลดเข้ากล่องรับ 50 ทวิ ระบบจะจัดสรรส่วนที่เหลือพร้อมวันที่ของใบนั้น "
        + "(รายการที่ตัดสูญส่วนที่เหลือไปแล้ว: ยืนยันใบในกล่องรับ ระบบกลับรายการตัดสูญให้ — ต้องมีสิทธิ์จัดการใบสำคัญ Journal.Manage)";

    /// <summary>ฝ่ายค้าน WP-09 รอบสอง L4</summary>
    public const string CertificateDateRequiredMessage =
        "ต้องระบุวันที่บนหนังสือรับรอง — ใช้กำหนดรอบที่นำเครดิตไปใช้ใน ภ.ง.ด.50 (ไม่มีวันที่ = นับเป็นเครดิตไม่ได้)";

    /// <summary>รหัสกฎของการกันใบซ้ำ/ล็อกใบแรก (audit · tooltip)</summary>
    public const string DedupeRuleCode = "RD-50BIS-DUP";

    private async Task<int> FiscalStartMonthAsync(Guid companyId)
    {
        var m = await _db.Companies.AsNoTracking().Where(c => c.Id == companyId).Select(c => c.FiscalYearStartMonth).FirstOrDefaultAsync();
        return m is < 1 or > 12 ? 1 : m;
    }

    /// <summary>ฝ่ายค้าน WP-09 รอบสอง (races): ล็อกต่อ "เลขที่ใบ + ผู้จ่าย" ของบริษัท — ตัวเดียวของทุกทางที่ตรวจ/เขียนเลขที่ใบ (กล่องรับ ส่ง/ยืนยัน ·
    /// ทะเบียน สร้าง/แก้/ได้รับใบ) ⇒ สองคำขอพร้อมกันไม่ผ่านด่านกันซ้ำทั้งคู่ · ต้องอยู่ในธุรกรรม (xact lock)</summary>
    public Task LockCertificateAsync(Guid companyId, string certificateNumber, string? payerTaxId, Guid? payerContactId)
    {
        var payerTax = ThaiTaxId.Normalize(payerTaxId);
        var part = "cert-" + certificateNumber.Trim().ToUpperInvariant() + "|" + (payerTax.Length == 13 ? payerTax : payerContactId?.ToString("N") ?? "");
        return _db.Database.ExecuteSqlRawAsync("SELECT pg_advisory_xact_lock({0})",
            AdvisoryLockKey.For(companyId, AdvisoryLockKey.WhtCreditWriteOff, part));
    }

    /// <summary>
    /// ฝ่ายค้าน WP-09 H1 + รอบสอง M-A/M-D — <b>ตัวเดียวของการกันใบ 50 ทวิ ซ้ำ</b> ทุกทาง (กล่องรับ ส่ง/ยืนยัน · ทะเบียน สร้าง/แก้/ได้รับใบ · แอดมินแพลตฟอร์ม):
    /// เลขที่ใบ + ผู้จ่าย (เลขผู้เสียภาษีของแถว · ไม่มี ⇒ ของผู้ติดต่อ · ใบไม่มีเลขผู้จ่าย ⇒ ผู้ติดต่อเดียวกัน) + <b>ปีเดียวกัน</b>
    /// (<see cref="WhtCertIntakeVerdict.CertYear"/> — เลขที่เดียวกันของปีถัดไปบันทึกได้) อยู่ใน (ก) ทะเบียน (ไม่ถูกลบ · ไม่ใช่ตัดสูญ) หรือ (ข) ใบที่ยืนยันแล้วในกล่องรับ ⇒ 409
    /// พร้อมทางไปต่อ · <paramref name="exceptCreditId"/> = แถวตัวเอง (รวมใบในกล่องรับที่ลงแถวนี้) · <paramref name="exceptIntakeId"/> = ใบในกล่องรับตัวเอง ·
    /// ถือล็อก <see cref="LockCertificateAsync"/> (ผู้เรียกอยู่ในธุรกรรม)
    /// </summary>
    public async Task EnsureCertificateNotRecordedAsync(Guid companyId, string? certificateNumber, string? payerTaxId, Guid? payerContactId,
        DateTime? certificateDate, int? taxYear, Guid? exceptCreditId = null, Guid? exceptIntakeId = null, CancellationToken ct = default)
    {
        var certNo = Trim(certificateNumber);
        if (certNo == null) return;
        var payerTax = ThaiTaxId.Normalize(payerTaxId);
        await LockCertificateAsync(companyId, certNo, payerTax, payerContactId);
        var fyStart = await FiscalStartMonthAsync(companyId);
        var year = WhtCertIntakeVerdict.CertYear(certificateDate, taxYear, fyStart);
        bool SamePayer(string? rowTax, string? contactTax, Guid? rowContact)
            => payerTax.Length == 13
                ? ThaiTaxId.Normalize(string.IsNullOrWhiteSpace(rowTax) ? contactTax : rowTax) == payerTax
                : payerContactId != null && rowContact == payerContactId;
        BusinessRuleException Duplicate(decimal amount, string where) => new(
            $"หนังสือรับรองเลขที่ {certNo} ของผู้จ่ายรายนี้{(year is int y ? $" (ปีภาษี {y + 543})" : "")} บันทึกไว้แล้วใน{where} ({amount:N2}) — ไม่บันทึกซ้ำ · "
            + "ถ้าเป็นใบคนละใบ: ตรวจเลขที่และวันที่บนใบอีกครั้ง (เลขที่เดียวกันของปีอื่นบันทึกได้) · ถ้าเป็นใบเดียวกัน: ไม่ต้องบันทึกอีก — เปิดรายการเดิมที่หน้า \"ภาษีถูกหัก ณ ที่จ่าย\"",
            DedupeRuleCode, 409);

        var rows = await _db.WhtCreditsReceived.AsNoTracking()
            .Where(w => w.CompanyId == companyId && w.CertificateNumber == certNo && w.Status != WhtCreditStatus.Expired
                && (exceptCreditId == null || w.Id != exceptCreditId))
            .Select(w => new
            {
                w.PayerTaxId, w.PayerContactId, w.WhtAmount, w.CertificateDate, w.TaxYear,
                ContactTaxId = _db.Contacts.Where(c => c.Id == w.PayerContactId && c.CompanyId == companyId).Select(c => c.TaxId).FirstOrDefault(),
            }).ToListAsync(ct);
        var hit = rows.FirstOrDefault(w => SamePayer(w.PayerTaxId, w.ContactTaxId, w.PayerContactId)
            && WhtCertIntakeVerdict.SameCertYear(year, WhtCertIntakeVerdict.CertYear(w.CertificateDate, w.TaxYear, fyStart)));
        if (hit != null) throw Duplicate(hit.WhtAmount, "ทะเบียนภาษีถูกหัก");

        var intakes = await _db.WhtCertIntakes.AsNoTracking()
            .Where(i => i.CompanyId == companyId && i.CertificateNumber == certNo && i.Status == WhtCertIntakeRecordStatus.Confirmed
                && (exceptIntakeId == null || i.Id != exceptIntakeId)
                && (exceptCreditId == null || !_db.WhtCertAllocations.Any(a => a.CompanyId == companyId && a.IntakeId == i.Id
                    && a.WhtCreditId == exceptCreditId && a.Status == WhtCertAllocationStatus.Confirmed)))
            .Select(i => new { i.PayerTaxId, i.PayerContactId, i.WhtAmount, i.CertificateDate })
            .ToListAsync(ct);
        var hitIntake = intakes.FirstOrDefault(i => SamePayer(i.PayerTaxId, null, i.PayerContactId)
            && WhtCertIntakeVerdict.SameCertYear(year, WhtCertIntakeVerdict.CertYear(i.CertificateDate, null, fyStart)));
        if (hitIntake != null) throw Duplicate(hitIntake.WhtAmount, "กล่องรับ 50 ทวิ (ยืนยันแล้ว)");
    }

    private static void ApplyCertificate(WhtCreditReceived e, string certificateNumber, DateTime? certificateDate)
    {
        e.CertificateNumber = certificateNumber;
        e.CertificateDate = certificateDate;
        e.UpdatedAt = DateTime.UtcNow;
    }

    private async Task AttachAsync(Guid companyId, WhtCreditReceived e, Guid? attachmentId, AttachmentActor? actor)
    {
        if (!attachmentId.HasValue || attachmentId == e.AttachmentId) return;
        await AdoptUnsavedAttachmentAsync(companyId, e.Id, attachmentId, actor);
        e.AttachmentId = attachmentId;
    }

    /// <summary>กลับรายการ JE ตัดสูญของแถว — <b>ตัวเดียว</b>ของทุกทาง (ได้รับใบภายหลัง · ยกเลิกต้นทาง · คืนเงินแพลตฟอร์ม) · ผู้เรียกถือล็อกแถวในธุรกรรมแล้ว ·
    /// ลงวันนี้ (งวดต้องเปิด) ผ่าน <see cref="AccountingService.ReverseJournalEntryAsync"/> แบบ systemTriggered (ผ่านด่าน <see cref="WhtWriteOffJournalLock"/>) ·
    /// JE ถูกกลับไว้แล้ว (ข้อมูลเก่าก่อนมีด่าน) ⇒ ผูกตัวกลับเดิม ไม่กลับซ้ำ · หา JE ไม่เจอ/ไม่ใช่ JE ตัดสูญ = ล้มดัง (ไม่ขยับทะเบียนโดยไม่มี GL รองรับ)</summary>
    private async Task<(Guid ReversalId, string ReversalNo, string WriteOffNo)> ReverseWriteOffJournalCoreAsync(Guid companyId, WhtCreditReceived e, string why)
    {
        var today = ThaiDate.CalendarDateUtc(DateTime.UtcNow);
        if (await Journal.JournalEntryBuilder.ClosedPeriodReasonAsync(_db, companyId, today) is string closed)
            throw new BusinessRuleException("กลับรายการตัดสูญไม่ได้ — " + closed, WhtCreditWriteOff.ReversalRuleCode);
        var jeId = e.WriteOffJournalEntryId
            ?? throw new BusinessRuleException("รายการนี้ไม่มีรายการบัญชีตัดสูญ", WhtCreditWriteOff.ReversalRuleCode, 409);
        var je = await _db.JournalEntries.AsNoTracking()
            .Where(j => j.Id == jeId && j.CompanyId == companyId)
            .Select(j => new { j.Status, j.ReversedByEntryId, j.EntryNumber, j.Reference })
            .FirstOrDefaultAsync();
        if (je == null || !WhtCreditWriteOff.IsWriteOffReference(je.Reference))
            throw new BusinessRuleException("ไม่พบรายการบัญชีตัดสูญของรายการนี้ — ตรวจสมุดรายวันก่อน (ระบบไม่คืนเครดิตโดยไม่มีรายการบัญชีรองรับ)",
                WhtCreditWriteOff.ReversalRuleCode, 409);
        if (je.ReversedByEntryId is Guid already)
            return (already, await _db.JournalEntries.AsNoTracking().Where(j => j.Id == already && j.CompanyId == companyId)
                .Select(j => j.EntryNumber).FirstOrDefaultAsync() ?? "", je.EntryNumber);
        var rev = await new AccountingService(_db).ReverseJournalEntryAsync(companyId, jeId, today,
            $"กลับรายการตัดสูญภาษีถูกหัก ณ ที่จ่าย {je.EntryNumber} — {why}", systemTriggered: true);
        return (rev.Id, rev.EntryNumber, je.EntryNumber);
    }

    /// <summary>
    /// รอบ 257 ชุด 2 (B-H1) — แถวที่ได้ใบบางส่วนแล้ว<b>ตัดสูญส่วนที่เหลือ</b>พร้อม JE ได้ใบถัดไปผ่านกล่องรับ ⇒ กลับรายการ JE ตัดสูญ
    /// (<see cref="ReverseWriteOffJournalCoreAsync"/> ตัวเดียวของทุกทาง) แล้วคืนสถานะ "รอใบ" ให้กล่องรับลงใบต่อ (<c>WhtCertIntakeService.ConfirmAsync</c>) ·
    /// ผู้เรียกถือล็อกแถว (<see cref="AdvisoryLockKey.WhtCreditWriteOff"/>) + อยู่ในธุรกรรมแล้ว · ไม่แตะ <c>ReceivedAmount</c> (กล่องรับเป็นผู้เขียนเดียว) ·
    /// สิทธิ์ลงบัญชี (Journal.Manage) ตรวจที่ผู้เรียก (<paramref name="actor"/> ต้องไม่ null) · audit chain
    /// </summary>
    /// <param name="why">เหตุ (ลง JE กลับรายการ + หมายเหตุ + audit) — เช่น "ได้รับหนังสือรับรอง X ผ่านกล่องรับ" · "ถอนการยืนยันใบ X"</param>
    public async Task<(string ReversalNo, string WriteOffNo)> ReopenWrittenOffAsync(Guid companyId, WhtCreditReceived e, string why, WriteOffActor actor)
    {
        if (!WhtCreditWriteOff.ReopensForLateCertificate(e.Status, e.WriteOffJournalEntryId, e.WriteOffReversalJournalEntryId, e.WhtAmount, e.ReceivedAmount))
            throw new BusinessRuleException("รายการนี้ไม่ได้ตัดสูญส่วนที่เหลือพร้อมรายการบัญชี — ไม่มีอะไรให้กลับรายการ", WhtCreditWriteOff.ReversalRuleCode, 409);
        var before = new { status = e.Status.ToString(), e.WriteOffJournalEntryId, e.WhtAmount, e.ReceivedAmount };
        var (reversalId, reversalNo, writeOffNo) = await ReverseWriteOffJournalCoreAsync(companyId, e, why);
        e.WriteOffReversalJournalEntryId = reversalId;
        e.Status = WhtCreditStatus.Pending;   // กล่องรับตัดสินสถานะสุดท้ายหลังลงใบ/ถอนใบ (StatusForReceived / StatusAfterUnconfirm)
        e.Notes = AppendNote(e.Notes, $"{why} — กลับรายการตัดสูญ {writeOffNo} ด้วย {reversalNo}");
        e.UpdatedAt = DateTime.UtcNow;
        _db.AddChainedAuditLog(new AuditLog
        {
            CompanyId = companyId, UserEmail = actor.Name, UserId = actor.UserId,
            EntityType = nameof(WhtCreditReceived), EntityId = e.Id.ToString(), Action = AuditAction.Update,
            OldValues = System.Text.Json.JsonSerializer.Serialize(before),
            NewValues = System.Text.Json.JsonSerializer.Serialize(new
            {
                action = "wht-credit-writeoff-reversed-intake", ruleCode = WhtCreditWriteOff.ReversalRuleCode,
                legalReference = WhtCreditWriteOff.ReversalLegalReference,
                status = e.Status.ToString(), reason = why, writeOffJournal = writeOffNo, reversalJournal = reversalNo,
                reversalJournalEntryId = reversalId,
            }),
            Timestamp = DateTime.UtcNow,
        });
        return (reversalNo, writeOffNo);
    }

    /// <summary>
    /// <b>ต้นทางของเครดิตหายไปหลังตัดสูญ</b> (ฝ่ายค้าน S4) — ยกเลิกใบขายต้นทาง (<c>DocumentService.SyncWhtCreditReceivedAsync</c>) หรือคืนเงินแพลตฟอร์ม
    /// (<c>PlatformBillingDocumentIssuer.AdjustWhtCreditAfterRefundAsync</c>) ⇒ กลับรายการ JE ตัดสูญ (ไม่งั้น 11910 ติดลบ: ต้นทางกลับ Cr 11910 ซ้ำกับ JE ตัดสูญ) ·
    /// <paramref name="remainingAmount"/> = ยอดภาษีถูกหักที่ยังเหลือของแถว (≤ 0 ⇒ เครดิตหมด: แถวคง Expired ไม่มี JE ที่มีผล · <paramref name="removeRow"/> ⇒ ถอดแถว
    /// เหมือนแถวรอใบของใบที่ถูกยกเลิก) · เหลือ &gt; 0 ⇒ ลดยอด + กลับไปรอใบ (ผู้ทำบัญชีตัดสินใหม่) · ล็อกแถว · ธุรกรรมเดียว (ร่วมธุรกรรมผู้เรียกถ้ามี) · audit ·
    /// คืน false เมื่อแถวไม่ได้ตัดสูญพร้อม JE (ไม่ทำอะไร)
    /// </summary>
    public async Task<bool> UndoWriteOffAsync(Guid companyId, Guid id, string reason, decimal remainingAmount, bool removeRow, string actorName)
    {
        return await InTxAsync(async () =>
            {
                await LockCreditAsync(companyId, id);
                // รอบ 244 (แก้ CI ข้อ 3): แถวที่กลับรายการแล้วพร้อมถอดแถว (removeRow ⇒ IsDeleted) ถูกตัวกรอง soft-delete ซ่อน ⇒ Load โยน KeyNotFound ·
                // เรียกซ้ำ (ยกเลิกใบต้นทางซ้ำ/ลองใหม่) ต้อง "ไม่ทำอะไร" ไม่ใช่ 404 ⇒ อ่านรวมแถวที่ถอดแล้ว (กรองบริษัทเอง) · ไม่มีจริง = KeyNotFound เหมือนเดิม
                var e = await LoadFresh(companyId, id, includeRemoved: true);
                if (!WhtCreditWriteOff.NeedsReversal(e.Status, e.WriteOffJournalEntryId, e.WriteOffReversalJournalEntryId)) return false;
                var before = new { status = e.Status.ToString(), e.WhtAmount, e.WriteOffJournalEntryId };
                var (reversalId, reversalNo, writeOffNo) = await ReverseWriteOffJournalCoreAsync(companyId, e, reason);
                var remaining = Math.Max(0m, Math.Round(remainingAmount, 2, MidpointRounding.AwayFromZero));
                e.WriteOffReversalJournalEntryId = reversalId;
                if (remaining <= 0.005m)
                {
                    // ฝ่ายค้าน WP-09 รอบสอง M-B: หมดสิทธิ์โดยไม่ใช่การตัดสูญ (ต้นทางหาย/คืนเงิน) ⇒ ส่วนที่ได้ใบก็ไม่เป็นเครดิตแล้ว (Of(Expired) = 0)
                    e.Status = WhtCreditStatus.Expired;
                    e.ReceivedAmount = 0m;
                    if (removeRow) e.IsDeleted = true;
                }
                else
                {
                    // ฝ่ายค้าน WP-09 รอบสอง: ตัดสินสถานะใหม่จากยอดที่ได้ใบ (เดิมตั้ง Pending ตายตัว — แถวที่ได้ใบครบส่วนที่เหลือกลายเป็น "รอใบ")
                    e.WhtAmount = remaining;
                    if (e.ReceivedAmount > remaining) e.ReceivedAmount = remaining;
                    e.Status = e.ReceivedAmount > 0.005m
                        ? WhtCreditableAmount.StatusForReceived(remaining, e.ReceivedAmount) : WhtCreditStatus.Pending;
                }
                e.Notes = AppendNote(e.Notes, $"กลับรายการตัดสูญ {writeOffNo} ด้วย {reversalNo}: {reason}");
                e.UpdatedAt = DateTime.UtcNow;
                _db.AddChainedAuditLog(new AuditLog
                {
                    CompanyId = companyId, UserEmail = actorName,
                    EntityType = nameof(WhtCreditReceived), EntityId = e.Id.ToString(), Action = AuditAction.Update,
                    OldValues = System.Text.Json.JsonSerializer.Serialize(before),
                    NewValues = System.Text.Json.JsonSerializer.Serialize(new
                    {
                        action = "wht-credit-writeoff-undone", ruleCode = WhtCreditWriteOff.UndoRuleCode, legalReference = WhtCreditWriteOff.LegalReference,
                        reason, status = e.Status.ToString(), removed = e.IsDeleted, remaining, writeOffJournal = writeOffNo, reversalJournal = reversalNo,
                        reversalJournalEntryId = reversalId,
                    }),
                    Timestamp = DateTime.UtcNow,
                });
                await _db.SaveChangesAsync();
                return true;
            });
    }

    /// <summary>
    /// ไฟล์ 50 ทวิ ที่แนบ<b>ก่อน</b>บันทึกรายการ (เจ้าของว่าง) → ผูกกับรายการนี้ทันทีที่บันทึก
    ///
    /// <para>═══ ที่มา (ฝ่ายค้านรอบ 193 · P7) ═══ หน้า wht-credit แนบไฟล์ด้วย <c>entityId = Guid.Empty</c> แล้วส่งแค่
    /// <c>AttachmentId</c> มากับรายการ · ไม่มีใครย้ายแถวไฟล์ไปผูกกับรายการ ⇒ ไฟล์ทุกใบของทั้งบริษัทค้างอยู่ในถัง
    /// <c>WhtCredit/0000…</c> ตลอดไป (ถังนี้เปิดได้เฉพาะผู้อัปโหลด — <c>AttachmentPermissionScope.UnsavedFileVisible</c>)</para>
    ///
    /// <para>ไฟล์ต้องเป็นของบริษัทนี้ (ไม่พบ = ปฏิเสธพร้อมทางไปต่อ — เดิมรับ Guid อะไรก็ได้เก็บเป็นตัวชี้) · ไฟล์ที่ผูกกับ
    /// รายการอื่นอยู่แล้วไม่ถูกย้าย (ไม่แย่งหลักฐานของรายการอื่น) · บันทึกพร้อม SaveChanges ของผู้เรียก</para>
    /// </summary>
    /// <summary>ผู้บันทึกรายการ — ใช้ตัดสินว่าผูกไฟล์ในถังก่อนบันทึกเข้ารายการได้ไหม
    /// (<see cref="Accounting.Helpers.AttachmentPermissionScope.UnsavedFileVisible"/>)</summary>
    public sealed record AttachmentActor(Guid UserId, bool HoldsTaxFile);

    private async Task AdoptUnsavedAttachmentAsync(Guid companyId, Guid creditId, Guid? attachmentId, AttachmentActor? actor)
    {
        if (attachmentId is not { } fileId) return;
        var file = await _db.FileAttachments
            .FirstOrDefaultAsync(f => f.Id == fileId && f.CompanyId == companyId)
            ?? throw new Accounting.Helpers.BusinessRuleException(
                "ไม่พบไฟล์หนังสือรับรองที่แนบไว้ในบริษัทนี้ — เลือกไฟล์ใหม่แล้วกดบันทึกอีกครั้ง");
        // ฝ่ายค้านรอบสอง (Q4): ตัวชี้ AttachmentId ต้องเป็นไฟล์ 50 ทวิ ของรายการนี้ หรือไฟล์ในถังก่อนบันทึกเท่านั้น — เดิมไฟล์ของ
        // รายการอื่น (สลิปเงินเดือน · เอกสารใบอื่น · 50 ทวิ ของอีกรายการ) ถูกเก็บเป็นหลักฐานของเครดิตนี้ได้ (หลักฐานผิดชิ้น)
        var adopt = Accounting.Helpers.AttachmentPermissionScope.WhtCreditFileLink(file.EntityType, file.EntityId, creditId);
        if (adopt == Accounting.Helpers.WhtCreditFileLinkKind.Foreign)
            throw new Accounting.Helpers.BusinessRuleException(
                "ไฟล์นี้เป็นไฟล์ของรายการอื่น — ใช้เป็นหนังสือรับรอง 50 ทวิ ของรายการนี้ไม่ได้ · "
                + "แนบไฟล์หนังสือรับรองจากหน้านี้แล้วกดบันทึกอีกครั้ง", "WHT-CREDIT-FOREIGN-FILE");
        if (adopt == Accounting.Helpers.WhtCreditFileLinkKind.AdoptFromBucket)
        {
            // ฝ่ายค้านรอบ 193 (S2-P7): ไฟล์ในถังของคนอื่นผูกเข้ารายการของตัวเองไม่ได้ (เดิมรู้ id ก็ผูกได้ แล้วไฟล์กลายเป็นระดับสมาชิก)
            // · ผู้อัปโหลดเอง หรือผู้ถือ Tax.File (เก็บกวาดไฟล์ค้าง) · ไม่รู้ผู้บันทึก (actor null) = ปฏิเสธ
            if (actor == null
                || !Accounting.Helpers.AttachmentPermissionScope.UnsavedFileVisible(file.UploadedByUserId ?? Guid.Empty, actor.UserId, actor.HoldsTaxFile))
                throw new Accounting.Helpers.BusinessRuleException(
                    "ไฟล์หนังสือรับรองนี้ถูกแนบโดยผู้ใช้คนอื่นและยังไม่ได้ผูกกับรายการใด — ผูกเข้ารายการของคุณไม่ได้ · "
                    + "เลือกไฟล์ของคุณเองแล้วกดบันทึกอีกครั้ง หรือให้ผู้มีสิทธิ์ยื่นภาษี (Tax.File) ทำให้", statusCode: 403);
            file.EntityId = creditId;
            file.UpdatedAt = DateTime.UtcNow;
        }
    }

    public async Task DeleteAsync(Guid companyId, Guid id)
    {
        await InTxAsync(async () =>
            {
                await LockCreditAsync(companyId, id);
                var e = await LoadFresh(companyId, id);
                GuardEditable(e);
                e.IsDeleted = true;
                e.UpdatedAt = DateTime.UtcNow;
                await _db.SaveChangesAsync();
                return true;
            });
    }

    /// <summary>ผู้ลงบัญชีตัดสูญ/กลับรายการ — controller สร้างจาก JWT (ผู้ถือ Journal.Manage) · ชื่อใช้เป็น CreatedBy ของ JE + audit</summary>
    public sealed record WriteOffActor(Guid? UserId, string Name);

    public sealed record WriteOffResult(Guid JournalEntryId, string EntryNumber, decimal Amount, bool AlreadyWrittenOff);

    /// <summary>หน้าจอยืนยันตัดสูญ (server computes · page displays) — ยอด · ผัง Dr/Cr · ผลทางภาษี · เหตุที่ทำไม่ได้ (null = ทำได้)</summary>
    public sealed record WriteOffPreview(Guid Id, decimal Amount, string DebitAccountCode, string DebitAccountName,
        string CreditAccountCode, string CreditAccountName, decimal NonDeductibleAmount, string TaxEffect, string LegalReference,
        string? BlockedReason, DateTime DefaultDate, int MinReasonLength);

    /// <summary>รายการที่สิ้นรอบควรตัดสินใจ (ตามต่อ/ตัดสูญ) — ไม่ตัดอัตโนมัติ (<see cref="WhtCreditWriteOff.IsYearEndCandidate"/>)</summary>
    public sealed record WriteOffCandidate(Guid Id, int TaxYear, string PayerName, decimal WhtAmount, string? DocumentNumber,
        DateTime FiscalYearEnd, int DaysSinceYearEnd);

    /// <summary>
    /// <b>ตัดสูญเครดิตภาษีถูกหัก (เส้นเดียว · รอบ 243a · DESIGN G-9)</b> — ลูกค้าไม่ออก 50 ทวิ (และไม่มีหลักฐาน e-WHT) ⇒ เครดิตไม่ได้ ⇒ ลง JE
    /// Dr 58010 ภาษีเงินได้ถูกหักที่ขอเครดิตไม่ได้ / Cr 11910 ทั้งยอดของแถว ผ่าน <see cref="Journal.JournalEntryBuilder"/> (เลข JE + ด่านงวดปิด) ·
    /// ผูก JE กับแถว · สถานะ Expired · audit chain (RuleCode/LegalReference) — ธุรกรรมเดียว (รวมการสร้างผัง 58010 · N5) ล็อกต่อแถว ⇒ กดซ้ำ/สองแท็บได้ JE เดียว
    ///
    /// <para>ตัวตัดสิน <see cref="WhtCreditWriteOff.Decide"/> · ด่านยอดตามปีภาษีของแถว (<see cref="TaxYearWhtAvailableAsync"/> · ฝ่ายค้าน S1) ·
    /// ช่องตัดสูญของปิดปีเดินเส้นนี้ทีละแถว (<see cref="SettleYearEndAsync"/>) · สิทธิ์ (Journal.Manage) ตรวจที่ controller ·
    /// คืนเงินอ้างใบลดหนี้ (11910 ถูกล้างโดย JE ใบลดหนี้แล้ว) ใช้ <see cref="CancelAfterRefundAsync"/> ไม่ใช่เส้นนี้</para>
    /// </summary>
    public async Task<WriteOffResult> ExpireAsync(Guid companyId, Guid id, string? reason, DateTime? writeOffDate, WriteOffActor actor)
    {
        var today = ThaiDate.CalendarDateUtc(DateTime.UtcNow);
        var date = writeOffDate.HasValue ? ThaiDate.CalendarDateUtc(writeOffDate.Value) : today;
        return await InTxAsync(() => ExpireCoreAsync(companyId, id, reason, date, today, actor));
    }

    /// <summary>แกนของการตัดสูญหนึ่งแถว — ผู้เรียกอยู่ในธุรกรรมแล้ว (<see cref="ExpireAsync"/> · <see cref="SettleYearEndAsync"/>)</summary>
    private async Task<WriteOffResult> ExpireCoreAsync(Guid companyId, Guid id, string? reason, DateTime date, DateTime today, WriteOffActor actor)
    {
        await LockCreditAsync(companyId, id);
        var e = await LoadFresh(companyId, id);   // ไม่พบในบริษัทนี้ = 404 ก่อนสร้างผังใด ๆ
        var expense = await WriteOffExpenseAccountAsync(companyId);
        var wht = await FindAccountAsync(companyId, WhtCreditWriteOff.WhtAssetAccountCode)
            ?? throw new BusinessRuleException("ไม่พบผัง 11910 ภาษีถูกหัก ณ ที่จ่าย — ตรวจผังบัญชีก่อนตัดสูญ", WhtCreditWriteOff.RuleCode);
        var closed = await Journal.JournalEntryBuilder.ClosedPeriodReasonAsync(_db, companyId, date);
        var available = await TaxYearWhtAvailableAsync(companyId, e.TaxYear);
        // ฝ่ายค้าน WP-09 M1: ตัดสูญเฉพาะส่วนที่ไม่มีใบ (WhtCreditWriteOff.AmountOf) — ส่วนที่ได้ใบผ่านกล่องรับแล้วคงเป็นเครดิต (WhtCreditableAmount.Of(Expired) = ส่วนที่ได้ใบ)
        var writeOffAmount = WhtCreditWriteOff.AmountOf(e.Status, e.WhtAmount, e.ReceivedAmount);
        var decision = WhtCreditWriteOff.Decide(e.Status, WhtCreditWriteOff.NeedsReversal(e.Status, e.WriteOffJournalEntryId, e.WriteOffReversalJournalEntryId),
            writeOffAmount, reason, date, today, closed, available);
        if (decision.Outcome == WhtCreditWriteOff.Outcome.AlreadyWrittenOff)
        {
            var doneId = e.WriteOffJournalEntryId!.Value;
            var doneNo = await _db.JournalEntries.AsNoTracking().Where(j => j.Id == doneId && j.CompanyId == companyId)
                .Select(j => j.EntryNumber).FirstOrDefaultAsync() ?? "";
            // ฝ่ายค้าน WP-09 รอบสอง: ยอดของ JE ตัดสูญจริง (ไม่คำนวณจากแถวปัจจุบัน — ยอดถูกหัก/ได้ใบอาจเปลี่ยนหลังตัดสูญ)
            var doneAmount = await (from l in _db.JournalEntryLines.AsNoTracking()
                                    join j in _db.JournalEntries.AsNoTracking() on l.JournalEntryId equals j.Id
                                    where j.Id == doneId && j.CompanyId == companyId && !l.IsDeleted
                                    select l.DebitAmount).SumAsync();
            return new WriteOffResult(doneId, doneNo, Math.Round(doneAmount, 2, MidpointRounding.AwayFromZero), true);
        }
        if (!decision.Allowed)
            throw new BusinessRuleException(decision.Problem!, WhtCreditWriteOff.RuleCode);

        var amount = writeOffAmount;
        var why = reason!.Trim();
        var before = new { status = e.Status.ToString(), e.WhtAmount, e.ReceivedAmount, e.CertificateNumber };
        var je = await Journal.JournalEntryBuilder.For(_db, companyId, date)
            .Description($"ตัดสูญภาษีถูกหัก ณ ที่จ่ายที่ขอเครดิตไม่ได้ — {e.PayerName} ปีภาษี {e.TaxYear} ({why})")
            .Reference(WhtCreditWriteOff.Reference(e.Id))
            .Debit(expense.Id, amount, $"ภาษีถูกหักที่ไม่ได้รับหนังสือรับรอง 50 ทวิ — {e.PayerName} (ม.65 ตรี (6 ทวิ) ไม่ใช่รายจ่าย)")
            .Credit(wht.Id, amount, $"ล้างภาษีถูกหัก ณ ที่จ่าย ปีภาษี {e.TaxYear} — {e.PayerName}")
            .PostAsync(actor.Name);
        e.Status = WhtCreditStatus.Expired;
        e.WriteOffJournalEntryId = je.Id;
        e.WriteOffReversalJournalEntryId = null;
        e.WrittenOffAt = DateTime.UtcNow;
        e.WriteOffReason = why;
        e.Notes = AppendNote(e.Notes, $"ตัดสูญ {je.EntryNumber}: {why}");
        e.UpdatedAt = DateTime.UtcNow;
        _db.AddChainedAuditLog(new AuditLog
        {
            CompanyId = companyId, UserId = actor.UserId, UserEmail = actor.Name,
            EntityType = nameof(WhtCreditReceived), EntityId = e.Id.ToString(), Action = AuditAction.Update,
            OldValues = System.Text.Json.JsonSerializer.Serialize(before),
            NewValues = System.Text.Json.JsonSerializer.Serialize(new
            {
                action = "wht-credit-writeoff", ruleCode = WhtCreditWriteOff.RuleCode, legalReference = WhtCreditWriteOff.LegalReference,
                status = e.Status.ToString(), amount, reason = why, writeOffDate = date, journal = je.EntryNumber, journalEntryId = je.Id,
                debitAccount = expense.AccountCode, creditAccount = wht.AccountCode,
                nonDeductible = WhtCreditWriteOff.NonDeductibleAmount(amount, expense.AccountCode, expense.AccountType, expense.ParentAccount?.AccountCode),
            }),
            Timestamp = DateTime.UtcNow,
        });
        await _db.SaveChangesAsync();
        _logger.LogInformation("ตัดสูญภาษีถูกหัก {Id} ยอด {Amount} ด้วย {Je}", e.Id, amount, je.EntryNumber);
        return new WriteOffResult(je.Id, je.EntryNumber, amount, false);
    }

    /// <summary>
    /// <b>ยอด 11910 ที่ยังล้างได้ของปีภาษีหนึ่ง</b> (ฝ่ายค้าน S1 — เดิมใช้ยอดสะสมทั้งบริษัท ⇒ ปิดปีแล้วยังตัดสูญรายแถวซ้ำได้จากยอดปีอื่น) =
    /// Dr 11910 สุทธิในรอบบัญชีนั้น (ไม่นับ JE ปิดปี/ตัดสูญที่ลงในช่วง) + ผลของ JE ปิดปีของปีนั้น + JE ตัดสูญของแถวปีนั้น (ลงวันไหนก็ตาม · นับคู่ตัวกลับ ⇒ หักล้าง) ·
    /// ตัวเดียวของด่านตัดสูญรายแถว ตัดสูญในปิดปี และยอดรวมที่ปิดปีล้างได้
    /// </summary>
    public async Task<decimal> TaxYearWhtAvailableAsync(Guid companyId, int taxYear)
    {
        var (start, end) = await FiscalRangeAsync(companyId, taxYear);
        var activity = await GlWhtBalanceAsync(companyId, start, end, excludeYearEndClose: taxYear, excludeWriteOffs: true);
        var refs = (await _db.WhtCreditsReceived.IgnoreQueryFilters().AsNoTracking()
                .Where(w => w.CompanyId == companyId && w.TaxYear == taxYear && w.WriteOffJournalEntryId != null)
                .Select(w => w.Id).ToListAsync())
            .Select(WhtCreditWriteOff.Reference).ToList();
        refs.Add(Accounting.Helpers.WhtYearEndReference.For(taxYear));
        var cleared = await (from l in _db.JournalEntryLines.AsNoTracking()
                             join j in _db.JournalEntries.AsNoTracking() on l.JournalEntryId equals j.Id
                             join a in _db.ChartOfAccounts.AsNoTracking() on l.AccountId equals a.Id
                             where j.CompanyId == companyId && !j.IsDeleted && !l.IsDeleted
                                   && (j.Status == JournalEntryStatus.Posted || j.Status == JournalEntryStatus.Reversed)
                                   && j.Reference != null && refs.Contains(j.Reference)
                                   && a.AccountCode.StartsWith("11910")
                             select l.DebitAmount - l.CreditAmount).ToListAsync();
        return Math.Round(activity + cleared.Sum(), 2, MidpointRounding.AwayFromZero);
    }

    /// <summary>ข้อมูลหน้ายืนยันตัดสูญ — ไม่สร้างผัง/ไม่เขียนอะไร (GET) · เหตุผลยังไม่ถูกตรวจ (ผู้ใช้ยังไม่ได้พิมพ์)</summary>
    public async Task<WriteOffPreview> WriteOffPreviewAsync(Guid companyId, Guid id, DateTime? writeOffDate = null)
    {
        var e = await Load(companyId, id);
        var today = ThaiDate.CalendarDateUtc(DateTime.UtcNow);
        var date = writeOffDate.HasValue ? ThaiDate.CalendarDateUtc(writeOffDate.Value) : today;
        var tpl = ChartOfAccountTemplates.GetCommonAccounts().Single(a => a.Code == WhtCreditWriteOff.ExpenseAccountCode);
        var exp = await _db.ChartOfAccounts.AsNoTracking()
            .Where(a => a.CompanyId == companyId && a.AccountCode == WhtCreditWriteOff.ExpenseAccountCode && a.IsActive)
            .Select(a => new { a.AccountCode, a.AccountName, a.AccountType, Parent = a.ParentAccount != null ? a.ParentAccount.AccountCode : null })
            .FirstOrDefaultAsync();
        var whtName = await _db.ChartOfAccounts.AsNoTracking()
            .Where(a => a.CompanyId == companyId && a.AccountCode == WhtCreditWriteOff.WhtAssetAccountCode && a.IsActive)
            .Select(a => a.AccountName).FirstOrDefaultAsync();
        var amount = WhtCreditWriteOff.AmountOf(e.Status, e.WhtAmount, e.ReceivedAmount);   // M1: เฉพาะส่วนที่ไม่มีใบ
        var closed = await Journal.JournalEntryBuilder.ClosedPeriodReasonAsync(_db, companyId, date);
        var available = await TaxYearWhtAvailableAsync(companyId, e.TaxYear);
        var decision = WhtCreditWriteOff.Decide(e.Status, WhtCreditWriteOff.NeedsReversal(e.Status, e.WriteOffJournalEntryId, e.WriteOffReversalJournalEntryId),
            amount, null, date, today, closed, available, checkReason: false);
        string? blocked = decision.Outcome switch
        {
            WhtCreditWriteOff.Outcome.AlreadyWrittenOff => "รายการนี้ตัดสูญพร้อมรายการบัญชีแล้ว",
            WhtCreditWriteOff.Outcome.Blocked => decision.Problem,
            _ => null,
        };
        if (blocked == null && whtName == null) blocked = "ไม่พบผัง 11910 ภาษีถูกหัก ณ ที่จ่าย — ตรวจผังบัญชีก่อนตัดสูญ";
        if (blocked == null && exp != null && !WhtCreditWriteOff.IsNonDeductibleAccount(exp.AccountCode, exp.AccountType, exp.Parent))
            blocked = NotTaxGroupMessage;
        var nonDeductible = exp == null
            ? WhtCreditWriteOff.NonDeductibleAmount(amount, tpl.Code, tpl.Type, Accounting.Helpers.SettlementChartSeed.ParentGroupCode(tpl.Code))
            : WhtCreditWriteOff.NonDeductibleAmount(amount, exp.AccountCode, exp.AccountType, exp.Parent);
        return new WriteOffPreview(e.Id, amount, WhtCreditWriteOff.ExpenseAccountCode,
            exp?.AccountName ?? tpl.NameTh + " (ระบบสร้างผังให้ตอนยืนยัน)",
            WhtCreditWriteOff.WhtAssetAccountCode, whtName ?? "ภาษีถูกหัก ณ ที่จ่าย",
            nonDeductible, WhtCreditWriteOff.TaxEffectText(amount), WhtCreditWriteOff.LegalReference, blocked, date, WhtCreditWriteOff.MinReasonLength);
    }

    /// <summary>สิ้นรอบ: รายการที่ยังรอใบในรอบบัญชีที่สิ้นสุดแล้ว — เสนอให้ผู้ทำบัญชีเลือก ตามต่อ (ขอคืนได้ภายใน 3 ปี ม.63) หรือ ตัดสูญ · ไม่ตัดเอง</summary>
    public async Task<List<WriteOffCandidate>> WriteOffCandidatesAsync(Guid companyId)
    {
        var rows = await _db.WhtCreditsReceived.AsNoTracking()
            .Where(w => w.CompanyId == companyId && w.Status == WhtCreditStatus.Pending)
            .Select(w => new { w.Id, w.TaxYear, w.PayerName, w.WhtAmount, w.ReceivedAmount, DocNo = w.Document != null ? w.Document.DocumentNumber : null })
            .ToListAsync();
        var today = ThaiDate.CalendarDateUtc(DateTime.UtcNow);
        var ends = new Dictionary<int, DateTime>();
        var list = new List<WriteOffCandidate>();
        foreach (var r in rows)
        {
            if (!ends.TryGetValue(r.TaxYear, out var end))
                ends[r.TaxYear] = end = (await FiscalRangeAsync(companyId, r.TaxYear)).End;
            // ฝ่ายค้าน WP-09 M1: เสนอเฉพาะยอดที่ยังขาดใบ (ส่วนที่ได้ใบแล้วไม่ใช่ของที่ต้องตัดสินใจตัดสูญ)
            var missing = WhtCreditableAmount.Missing(WhtCreditStatus.Pending, r.WhtAmount, r.ReceivedAmount);
            if (WhtCreditWriteOff.IsYearEndCandidate(WhtCreditStatus.Pending, missing, end, today))
                list.Add(new WriteOffCandidate(r.Id, r.TaxYear, r.PayerName, missing, r.DocNo, end, (int)(today.Date - end.Date).TotalDays));
        }
        return list.OrderByDescending(c => c.DaysSinceYearEnd).ThenByDescending(c => c.WhtAmount).ToList();
    }

    /// <summary>คำเตือนเรื่องผัง/JE ที่กระทบ ภ.ง.ด.50 (อ่านอย่างเดียว · รอบ 243a ฝ่ายค้าน N1/N2) — Kind · ข้อความพร้อมทางไปต่อ · รายการที่เกี่ยว</summary>
    public sealed record AccountWarning(string Kind, string Message, List<string> Items);

    /// <summary>
    /// <list type="bullet">
    /// <item><b>N1</b> ผังค่าใช้จ่ายรหัส 58000–58099 ที่ไม่อยู่ใต้กลุ่ม 580 — ภ.ง.ด.50/51 ตัดออกจากรายจ่ายทั้งบรรทัด (กติกา <see cref="CitCalculation.IsCitExpenseAccount"/>
    /// ขยายเป็น 58000–58099 ในรอบนี้) ⇒ ถ้าไม่ใช่ภาษีเงินได้ ต้องเปลี่ยนรหัส</item>
    /// <item><b>N2</b> JE ปิดปี <c>WHT-{ปี}</c> รุ่นก่อนรอบ 243a ที่ช่องตัดสูญลงผังค่าใช้จ่ายนอกกลุ่ม 580 (เดิม "53") ในงวดที่ยังเปิด ⇒ ภ.ง.ด.50 นับเป็นรายจ่ายหักได้ ·
    /// ไม่ย้ายให้อัตโนมัติ (สมุดบัญชีเป็นของผู้ทำบัญชี)</item>
    /// </list>
    /// </summary>
    public async Task<List<AccountWarning>> AccountWarningsAsync(Guid companyId)
    {
        var list = new List<AccountWarning>();
        var cit = CitCalculation.CitExpensePrefix;
        var outside = (await _db.ChartOfAccounts.AsNoTracking()
                .Where(a => a.CompanyId == companyId && a.AccountType == AccountType.Expense && a.AccountCode.Length == 5
                    && a.AccountCode.StartsWith(cit))
                .Select(a => new { a.AccountCode, a.AccountName, a.AccountType, Parent = a.ParentAccount != null ? a.ParentAccount.AccountCode : null })
                .ToListAsync())
            .Where(a => a.Parent != cit && CitCalculation.IsCitExpenseAccount(a.AccountCode, a.AccountType, a.Parent))
            .OrderBy(a => a.AccountCode, StringComparer.Ordinal)
            .Select(a => $"{a.AccountCode} {a.AccountName}").ToList();
        if (outside.Count > 0)
            list.Add(new AccountWarning("CitCodeOutsideGroup",
                "ผังเหล่านี้ใช้รหัสช่วง 58000–58099 แต่ไม่อยู่ใต้กลุ่ม 580 ภาษีเงินได้ — ภ.ง.ด.50/51 ตัดยอดในผังเหล่านี้ออกจากรายจ่ายทั้งหมด "
                + "(ถือเป็นภาษีเงินได้ ม.65 ตรี (6 ทวิ)) · ถ้าไม่ใช่ภาษีเงินได้ ให้เปลี่ยนรหัสผังที่หน้า \"ผังบัญชี\" · ถ้าใช่ ให้ย้ายไปใต้กลุ่ม 580", outside));

        var yeLines = await _db.JournalEntryLines.AsNoTracking()
            .Where(l => l.JournalEntry.CompanyId == companyId && !l.JournalEntry.IsDeleted && !l.IsDeleted
                && l.JournalEntry.Status == JournalEntryStatus.Posted && l.JournalEntry.ReversedByEntryId == null
                && l.JournalEntry.Reference != null && l.JournalEntry.Reference.StartsWith("WHT-") && l.JournalEntry.Reference.Length == 8
                && l.DebitAmount > 0 && l.Account!.AccountType == AccountType.Expense)
            .Select(l => new
            {
                l.JournalEntry.EntryNumber, l.JournalEntry.EntryDate, l.JournalEntry.Reference, l.DebitAmount,
                Code = l.Account!.AccountCode, l.Account.AccountType, Parent = l.Account.ParentAccount != null ? l.Account.ParentAccount.AccountCode : null,
            })
            .ToListAsync();
        var stale = new List<string>();
        foreach (var l in yeLines.Where(l => int.TryParse(l.Reference![4..], out var y) && Accounting.Helpers.WhtYearEndReference.For(y) == l.Reference
                     && !CitCalculation.IsCitExpenseAccount(l.Code, l.AccountType, l.Parent)))
            if (await Journal.JournalEntryBuilder.ClosedPeriodReasonAsync(_db, companyId, l.EntryDate) == null)
                stale.Add($"{l.EntryNumber} ({l.Reference}) ผัง {l.Code} {l.DebitAmount:N2}");
        if (stale.Count > 0)
            list.Add(new AccountWarning("YearEndWriteOffDeductible",
                "ใบปิดปีภาษีถูกหักรุ่นเก่าลงช่อง \"ตัดสูญ\" ที่ผังค่าใช้จ่ายนอกกลุ่ม 580 — ภ.ง.ด.50 นับยอดนี้เป็นรายจ่ายหักได้ (ผิด ม.65 ตรี (6 ทวิ)) · "
                + "งวดยังเปิด: กลับรายการใบนี้ที่สมุดรายวัน แล้วปิดปีใหม่โดยเลือกรายการที่ตัดสูญทีละรายการ (ระบบลง 58010) · ระบบไม่ย้ายให้อัตโนมัติ", stale));
        return list;
    }

    /// <summary>หมดสิทธิ์เครดิต<b>โดยไม่ลงบัญชี</b> — เฉพาะกรณียอด 11910 ถูกล้างโดย JE อื่นแล้ว: คืนเงินเต็มจำนวนอ้างใบลดหนี้
    /// (ใบลดหนี้ Cr 11910 ส่วนที่คืน — <c>PlatformBillingDocumentIssuer.AdjustWhtCreditAfterRefundAsync</c>) · ห้ามใช้แทนการตัดสูญ
    /// (ตัดสูญ = <see cref="ExpireAsync"/> ซึ่งลง JE) · ผู้เรียกเขียน audit เอง (ในธุรกรรมเดียวกัน)</summary>
    public async Task CancelAfterRefundAsync(Guid companyId, Guid id, string reason)
    {
        await InTxAsync(async () =>
            {
                await LockCreditAsync(companyId, id);
                var e = await LoadFresh(companyId, id);
                if (e.Status == WhtCreditStatus.Claimed)
                    throw new BusinessRuleException("รายการนี้ใช้เครดิตในแบบยื่นไปแล้ว — ตัดทิ้งไม่ได้");
                // แถวตัดสูญพร้อม JE ต้องเดิน UndoWriteOffAsync (กลับ JE ก่อน) — ไม่งั้น 11910 ติดลบ (ใบลดหนี้ Cr ซ้ำกับ JE ตัดสูญ)
                if (WhtCreditWriteOff.NeedsReversal(e.Status, e.WriteOffJournalEntryId, e.WriteOffReversalJournalEntryId))
                    throw new BusinessRuleException("รายการนี้ตัดสูญพร้อมรายการบัญชีแล้ว — ต้องกลับรายการตัดสูญก่อน (UndoWriteOffAsync)", WhtCreditWriteOff.UndoRuleCode, 409);
                e.Status = WhtCreditStatus.Expired;
                e.ReceivedAmount = 0m;   // ฝ่ายค้าน WP-09 รอบสอง M-B: คืนเงินแล้ว ⇒ ส่วนที่ได้ใบไม่เป็นเครดิต (Of(Expired) = 0)
                e.Notes = AppendNote(e.Notes, $"หมดสิทธิ์ (ไม่ลงบัญชี — ใบลดหนี้ล้าง 11910 แล้ว): {reason}");
                e.UpdatedAt = DateTime.UtcNow;
                await _db.SaveChangesAsync();
                return true;
            });
    }

    private const string NotTaxGroupMessage = "ผัง 58010 ของบริษัทนี้ไม่อยู่ใต้กลุ่ม 580 ภาษีเงินได้ — ถ้าลงบัญชีไป ภ.ง.ด.50 จะนับเป็นรายจ่ายหักได้ (ผิด ม.65 ตรี (6 ทวิ)) · "
        + "ย้ายผัง 58010 ไปใต้กลุ่ม 580 ที่หน้า \"ผังบัญชี\" แล้วลองอีกครั้ง";

    /// <summary>ผังค่าใช้จ่ายตัดสูญ — สร้างจากผังมาตรฐานถ้ายังไม่มี (ใต้กลุ่ม 580) · ต้องเป็นผังกลุ่มภาษีเงินได้ ไม่งั้นปฏิเสธ (ไม่ลงผังอื่นเงียบ ๆ)</summary>
    private async Task<ChartOfAccount> WriteOffExpenseAccountAsync(Guid companyId)
    {
        var accId = await Accounting.Helpers.SettlementChartSeed.EnsureStandardAccountAsync(_db, companyId, WhtCreditWriteOff.ExpenseAccountCode)
            ?? throw new BusinessRuleException("บริษัทนี้ไม่มีกลุ่มผัง 580 ภาษีเงินได้ ให้ระบบสร้างผัง 58010 \"ภาษีเงินได้ถูกหัก ณ ที่จ่ายที่ขอเครดิตไม่ได้\" — "
                + "เพิ่มผัง 58010 ใต้กลุ่ม 580 ที่หน้า \"ผังบัญชี\" แล้วลองอีกครั้ง", WhtCreditWriteOff.RuleCode);
        var acc = await _db.ChartOfAccounts.Include(a => a.ParentAccount)
            .FirstAsync(a => a.Id == accId && a.CompanyId == companyId);
        if (!WhtCreditWriteOff.IsNonDeductibleAccount(acc.AccountCode, acc.AccountType, acc.ParentAccount?.AccountCode))
            throw new BusinessRuleException(NotTaxGroupMessage, WhtCreditWriteOff.RuleCode);
        return acc;
    }

    private Task LockCreditAsync(Guid companyId, Guid id)
        => _db.Database.ExecuteSqlRawAsync("SELECT pg_advisory_xact_lock({0})",
            AdvisoryLockKey.For(companyId, AdvisoryLockKey.WhtCreditWriteOff, id.ToString("N")));

    /// <summary>โหลดแถวหลังได้ล็อก — แถวที่ติดตามอยู่แล้วต้องอ่านค่าล่าสุดจากฐาน (อีกคำขอเพิ่งตัดสูญไป)</summary>
    /// <param name="includeRemoved">รอบ 244: รวมแถวที่ถอดแล้ว (soft-delete) — เฉพาะ <see cref="UndoWriteOffAsync"/> ที่เรียกซ้ำได้หลังถอดแถว</param>
    private async Task<WhtCreditReceived> LoadFresh(Guid companyId, Guid id, bool includeRemoved = false)
    {
        var e = includeRemoved
            ? await _db.WhtCreditsReceived.IgnoreQueryFilters().FirstOrDefaultAsync(w => w.Id == id && w.CompanyId == companyId)
              ?? throw new KeyNotFoundException("ไม่พบรายการภาษีถูกหัก ณ ที่จ่าย")
            : await Load(companyId, id);
        if (_db.Entry(e).State == EntityState.Unchanged) await _db.Entry(e).ReloadAsync();
        return e;
    }

    private async Task<T> InTxAsync<T>(Func<Task<T>> work)
    {
        if (_db.Database.CurrentTransaction != null) return await work();
        var strategy = _db.Database.CreateExecutionStrategy();
        return await strategy.ExecuteAsync(async () =>
        {
            await using var tx = await _db.Database.BeginTransactionAsync();
            try
            {
                var result = await work();
                await tx.CommitAsync();
                return result;
            }
            catch
            {
                await tx.RollbackAsync(CancellationToken.None);
                _db.ChangeTracker.Clear();   // ถอด JE/แถว/audit ที่ค้าง — ไม่ให้ไหลไปกับ SaveChanges ถัดไป
                throw;
            }
        });
    }

    private static string AppendNote(string? notes, string add)
        => string.IsNullOrWhiteSpace(notes) ? add : $"{notes} | {add}";

    // ═══════════ ใช้เครดิตกับแบบยื่น (เฟส 3) ═══════════

    /// <summary>ยอดเครดิตที่ใช้ได้ของรอบบัญชี <paramref name="taxYear"/> — <b>กติกางวดเดียวกับ ภ.ง.ด.50</b> (<c>TaxService.GenerateCitReport</c>):
    /// วันที่บนหนังสือรับรองอยู่ในรอบ (<see cref="CitCalculation.CreditableInPeriod"/>) + ตัวรวมเดียว (<see cref="CitCalculation.CreditableSumAsync"/>) ·
    /// ผู้เรียก: <see cref="SummaryAsync"/> (ฝ่ายค้าน WP-09 L9 — เดิมไม่มีผู้เรียกในระบบ · <c>ClaimAsync/UnclaimAsync</c> ที่ไม่มีผู้เรียกถูกลบ)</summary>
    public async Task<decimal> CreditableAmountAsync(Guid companyId, int taxYear)
    {
        var (start, end) = await FiscalRangeAsync(companyId, taxYear);
        return await _db.WhtCreditsReceived.AsNoTracking().CreditableInPeriod(companyId, start, end).CreditableSumAsync();
    }

    /// <summary>ฝ่ายค้าน WP-09 L9: เครดิตของรอบตามกติกางวด ภ.ง.ด.50 (ตัวเดียวกับ <see cref="CreditableAmountAsync"/>) แยก "ยังไม่ใช้" / "ใช้แล้ว (Claimed)" —
    /// รายงานปลายปี "50 ทวิ ที่ยังขาด" ใช้ตัวนี้ ไม่รวมตามปีภาษีของแถวเอง</summary>
    public async Task<(decimal Unclaimed, decimal Claimed)> CreditableSplitAsync(Guid companyId, int taxYear)
    {
        var (start, end) = await FiscalRangeAsync(companyId, taxYear);
        var q = _db.WhtCreditsReceived.AsNoTracking().CreditableInPeriod(companyId, start, end);
        var all = await q.CreditableSumAsync();
        var claimed = await q.Where(w => w.Status == WhtCreditStatus.Claimed).CreditableSumAsync();
        return (Math.Round(all - claimed, 2, MidpointRounding.AwayFromZero), claimed);
    }

    // ═══════════ ปิดปี: ล้างบัญชี 11910 (เฟส 5) ═══════════

    /// <param name="WriteOffCreditIds">ฝ่ายค้าน S1: รายการที่จะตัดสูญ (ปีภาษีเดียวกัน) — ช่อง <paramref name="WriteOff"/> ต้องเท่ายอดรวมของรายการเหล่านี้ ·
    /// แต่ละรายการเดินเส้นตัดสูญรายแถว (<see cref="ExpireAsync"/> แกนเดียวกัน) ไม่มี JE ก้อนรวม</param>
    public record SettleRequest(int TaxYear, decimal UsedAgainstCit, decimal RefundRequested,
        decimal WriteOff, string? Reason, IReadOnlyList<Guid>? WriteOffCreditIds = null);

    /// <summary>ปิดปีสำหรับเครดิตภาษีถูกหัก — ล้างยอดออกจาก 11910 ตามที่ใช้จริง (ธุรกรรมเดียว)
    ///
    /// <para>ถ้าไม่ทำขั้นนี้ ยอด 11910 จะพองสะสมข้ามปีจนกระทบยอดกับแบบยื่นไม่ได้
    /// (ยอดในงบจะเป็นเครดิตสะสมทุกปีรวมกัน ไม่ใช่ของปีที่ยังใช้ได้จริง)</para>
    ///
    /// <list type="bullet">
    /// <item>ใช้หักกับภาษีที่ต้องเสีย → Dr ภาษีเงินได้ค้างจ่าย / Cr 11910 (JE ปิดปี <c>WHT-{ปี}</c>)</item>
    /// <item>ขอคืน → Dr ภาษีเงินได้จ่ายล่วงหน้า-รอรับคืน (11920) / Cr 11910 (JE ปิดปีใบเดียวกัน)</item>
    /// <item>ตัดสูญ → <b>ทีละรายการ</b>ผ่านแกนตัดสูญรายแถว (Dr 58010 / Cr 11910 · แถวเปลี่ยนสถานะ · รอบ 243a ฝ่ายค้าน S1) ลงวันสิ้นรอบ</item>
    /// <item>ยกไปปีหน้า → ไม่ต้องลง JE (คงไว้ที่ 11910)</item>
    /// </list>
    /// <para>ยอดรวมต้องไม่เกินยอดที่ยังล้างได้ของปีนั้น (<see cref="TaxYearWhtAvailableAsync"/> — นับการตัดสูญรายแถวที่ลงปีถัดไปแล้วด้วย) ·
    /// JE ปิดปีผ่าน <see cref="Journal.JournalEntryBuilder"/> + ด่านงวดปิด (ฝ่ายค้าน S2) · สิทธิ์ Journal.Manage ที่ controller</para>
    /// </summary>
    public async Task<string> SettleYearEndAsync(Guid companyId, SettleRequest r, string actor, Guid? actorUserId = null)
    {
        var ids = (r.WriteOffCreditIds ?? Array.Empty<Guid>()).Distinct().ToList();
        if (r.WriteOff > 0m && ids.Count == 0)
            throw new BusinessRuleException("ช่องตัดสูญต้องเลือกรายการที่จะตัดสูญ (ทีละรายการ) — เลือกรายการที่ไม่ได้รับหนังสือรับรองแล้วลองอีกครั้ง",
                WhtCreditWriteOff.RuleCode);
        var total = r.UsedAgainstCit + r.RefundRequested + r.WriteOff;
        if (total <= 0 && ids.Count == 0) throw new BusinessRuleException("ระบุจำนวนที่จะล้างอย่างน้อยหนึ่งช่อง");
        var (_, end) = await FiscalRangeAsync(companyId, r.TaxYear);
        var today = ThaiDate.CalendarDateUtc(DateTime.UtcNow);
        return await InTxAsync(async () =>
        {
            if (await Journal.JournalEntryBuilder.ClosedPeriodReasonAsync(_db, companyId, end) is string closed)
                throw new BusinessRuleException("ปิดปีภาษีไม่ได้ — " + closed);
            // รอบ 250 (WP-02 §2.4 ข้อ 9 · E L-2): เรียง Id ⇒ ExpireCoreAsync ล็อกแถว (LockCreditAsync) ตามลำดับคงที่ — สองคำขอปิดปี/ตัดสูญรายแถวพร้อมกันไม่ deadlock
            var rows = await _db.WhtCreditsReceived.AsNoTracking()
                .Where(w => w.CompanyId == companyId && ids.Contains(w.Id))
                .OrderBy(w => w.Id)
                .Select(w => new { w.Id, w.TaxYear, w.WhtAmount, w.ReceivedAmount, w.Status, w.WriteOffJournalEntryId, w.WriteOffReversalJournalEntryId })
                .ToListAsync();
            if (rows.Count != ids.Count)
                throw new KeyNotFoundException("ไม่พบรายการภาษีถูกหัก ณ ที่จ่ายบางรายการที่เลือกตัดสูญ");
            if (rows.FirstOrDefault(w => w.TaxYear != r.TaxYear) is { } otherYear)
                throw new BusinessRuleException($"รายการที่เลือกตัดสูญอยู่ปีภาษี {otherYear.TaxYear} ไม่ใช่ปี {r.TaxYear} — ปิดปีตัดได้เฉพาะรายการของปีนั้น",
                    WhtCreditWriteOff.RuleCode);
            // ฝ่ายค้าน S1 ทิศตรงข้าม: รายการที่ตัดสูญรายแถวไปแล้ว (ปีไหนก็ตาม) ห้ามนับซ้ำในการปิดปี
            if (rows.Where(w => WhtCreditWriteOff.NeedsReversal(w.Status, w.WriteOffJournalEntryId, w.WriteOffReversalJournalEntryId)).ToList() is { Count: > 0 } done)
                throw new BusinessRuleException($"มี {done.Count} รายการที่ตัดสูญพร้อมรายการบัญชีไปแล้ว — ไม่ตัดซ้ำในการปิดปี · เอารายการนั้นออกจากช่องตัดสูญ",
                    WhtCreditWriteOff.RuleCode, 409);
            // ฝ่ายค้าน WP-09 M1: ยอดที่ตัดสูญได้ = ส่วนที่ไม่มีใบ (ตัวเดียวกับ ExpireCoreAsync)
            var selected = Math.Round(rows.Sum(w => WhtCreditWriteOff.AmountOf(w.Status, w.WhtAmount, w.ReceivedAmount)), 2, MidpointRounding.AwayFromZero);
            if (ids.Count > 0 && Math.Abs(selected - r.WriteOff) > 0.01m)
                throw new BusinessRuleException($"ช่องตัดสูญ {r.WriteOff:N2} ไม่เท่ายอดรวมของรายการที่เลือก ({selected:N2}) — ตรวจรายการที่เลือกอีกครั้ง",
                    WhtCreditWriteOff.RuleCode);
            var available = await TaxYearWhtAvailableAsync(companyId, r.TaxYear);
            var grand = r.UsedAgainstCit + r.RefundRequested + selected;
            if (grand > available + 0.01m)
                throw new BusinessRuleException(
                    $"ยอดที่จะล้าง {grand:N2} เกินยอด 11910 ของปีภาษีนี้ที่ยังล้างได้ ({available:N2} — หักการปิดปี/ตัดสูญรายแถวที่ลงไปแล้ว) — ตรวจการกระทบยอดก่อน");

            var numbers = new List<string>();
            var who = new WriteOffActor(actorUserId, actor);
            foreach (var w in rows)
                numbers.Add((await ExpireCoreAsync(companyId, w.Id, r.Reason, end, today, who)).EntryNumber);

            var close = r.UsedAgainstCit + r.RefundRequested;
            if (close > 0m)
            {
                var wht = await FindAccountAsync(companyId, "11910")
                    ?? throw new BusinessRuleException("ไม่พบผัง 11910 ภาษีถูกหัก ณ ที่จ่าย — ตรวจผังบัญชีก่อนปิดปี");
                var b = Journal.JournalEntryBuilder.For(_db, companyId, end)   // ลงวันสุดท้ายของรอบบัญชีที่ปิด
                    .Description($"ปิดปีภาษี {r.TaxYear} — ล้างภาษีถูกหัก ณ ที่จ่าย" + (string.IsNullOrWhiteSpace(r.Reason) ? "" : $" ({r.Reason})"))
                    .Reference(Accounting.Helpers.WhtYearEndReference.For(r.TaxYear));
                if (r.UsedAgainstCit > 0)
                {
                    var citPayable = await FindAccountAsync(companyId, "21810")
                        ?? await FindAccountAsync(companyId, "218")
                        ?? throw new BusinessRuleException("ไม่พบผังภาษีเงินได้ค้างจ่าย (21810/218) — เพิ่มผังที่หน้า \"ผังบัญชี\" แล้วลองอีกครั้ง");
                    b.Debit(citPayable.Id, r.UsedAgainstCit, $"ใช้เครดิตภาษีถูกหักหักกับ ภ.ง.ด.50 ปี {r.TaxYear}");
                }
                if (r.RefundRequested > 0)
                {
                    var refundAcc = await FindAccountAsync(companyId, "11920")
                        ?? throw new BusinessRuleException("ไม่พบผังภาษีเงินได้จ่ายล่วงหน้า (11920) — เพิ่มผังที่หน้า \"ผังบัญชี\" แล้วลองอีกครั้ง");
                    b.Debit(refundAcc.Id, r.RefundRequested, $"ขอคืนภาษีถูกหัก ปี {r.TaxYear}");
                }
                b.Credit(wht.Id, close, $"ล้างภาษีถูกหัก ณ ที่จ่าย ปี {r.TaxYear}");
                var je = await b.PostAsync(actor);
                await _db.SaveChangesAsync();
                numbers.Insert(0, je.EntryNumber);
            }
            _logger.LogInformation("ปิดปีภาษี {Year}: ล้าง 11910 รวม {Total} ({Je})", r.TaxYear, grand, string.Join(",", numbers));
            return string.Join(" · ", numbers);
        });
    }

    private async Task<ChartOfAccount?> FindAccountAsync(Guid companyId, string code)
        => await _db.ChartOfAccounts.FirstOrDefaultAsync(a =>
            a.CompanyId == companyId && a.AccountCode == code && a.IsActive);

    // ═══════════ helpers ═══════════

    private async Task<WhtCreditReceived> Load(Guid companyId, Guid id)
        => await _db.WhtCreditsReceived.FirstOrDefaultAsync(w => w.Id == id && w.CompanyId == companyId)
           ?? throw new KeyNotFoundException("ไม่พบรายการภาษีถูกหัก ณ ที่จ่าย");

    private static void GuardEditable(WhtCreditReceived e)
    {
        if (e.Status == WhtCreditStatus.Claimed)
            throw new BusinessRuleException(
                "รายการนี้ถูกใช้เป็นเครดิตในแบบยื่นไปแล้ว — แก้ไขไม่ได้ "
                + "(ถ้าต้องแก้จริง ให้ยกเลิกการใช้เครดิตของแบบนั้นก่อน)");
        // รอบ 243a: แก้/ลบแถวที่ตัดสูญพร้อม JE = ทะเบียนเปลี่ยนแต่ GL ไม่ขยับ (แก้เลขที่ใบ ⇒ "ได้รับแล้ว" ทั้งที่ 11910 ถูกล้างไปแล้ว)
        if (WhtCreditWriteOff.NeedsReversal(e.Status, e.WriteOffJournalEntryId, e.WriteOffReversalJournalEntryId))
            throw new BusinessRuleException(
                // รอบ 257 ชุด 2 (B-H1): แถวที่ได้ใบบางส่วนแล้ว "ได้รับใบแล้ว" ใช้ไม่ได้ — บอกทางที่ใช้ได้จริง (กล่องรับ)
                e.ReceivedAmount > 0.005m
                    ? "รายการนี้ตัดสูญส่วนที่เหลือพร้อมรายการบัญชีแล้ว — แก้ไข/ลบไม่ได้ · ถ้าได้รับหนังสือรับรองส่วนที่เหลือภายหลัง ให้อัปโหลดเข้ากล่องรับ 50 ทวิ "
                      + "แล้วกดยืนยัน (ระบบกลับรายการบัญชีตัดสูญและคืนเครดิตให้ · ต้องมีสิทธิ์จัดการใบสำคัญ)"
                    : "รายการนี้ตัดสูญพร้อมรายการบัญชีแล้ว — แก้ไข/ลบไม่ได้ · ถ้าได้รับหนังสือรับรองภายหลัง ให้กด \"ได้รับใบแล้ว\" "
                      + "(ระบบกลับรายการบัญชีตัดสูญและคืนเครดิตให้)", WhtCreditWriteOff.RuleCode);
    }

    private static string? Trim(string? s) => string.IsNullOrWhiteSpace(s) ? null : s.Trim();

    private async Task<string?> PayerNameFromContactAsync(Guid companyId, Guid? contactId)
        => contactId.HasValue
            ? await _db.Contacts.AsNoTracking().Where(c => c.Id == contactId.Value && c.CompanyId == companyId)
                .Select(c => c.Name).FirstOrDefaultAsync()
            : null;

    /// <summary>ตรวจอัตราที่ผู้จ่ายหักมา เทียบกับ ท.ป.4/2528 — ผู้จ่ายหักผิดอัตรา
    /// เกิดขึ้นบ่อย (หัก 5% ค่าบริการแทน 3%) ทำให้เครดิตไม่ตรงกับที่เขานำส่งจริง
    ///
    /// <para>รอบ 203: ตัวตัดสินกลาง <see cref="ThaiWhtRateTable.CheckRateForType"/> — เดิมเป็นตารางอัตราชุดที่สองที่จับคำเอง
    /// ⇒ รหัสจาก dropdown ("8ad"/"5") ไม่เคยถูกตรวจเลย และค่าที่หน้าเว็บเก็บเป็นเลขมาตรา ("40(8)") ทำให้ใบค่าโฆษณา 2%
    /// ถูกเตือนว่า "ปกติ 3%" · ผู้ถูกหักคือบริษัทเรา (นิติบุคคล) เหมือนเดิม</para></summary>
    internal static string? CheckRate(string? incomeType, decimal rate, DateTime paymentDate)
    {
        return ThaiWhtRateTable.CheckRateForType(incomeType, rate, payeeIsJuristic: true, paymentDate);
    }
}
