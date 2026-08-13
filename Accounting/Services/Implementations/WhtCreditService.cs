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
        string? RateWarning);

    public record WhtCreditSummary(
        int TaxYear,
        decimal PendingAmount,      // ถูกหักแล้วแต่ยังไม่มีใบ → เครดิตไม่ได้
        decimal ReceivedAmount,     // มีใบแล้ว พร้อมใช้
        decimal ClaimedAmount,      // ใช้ไปแล้ว
        decimal ExpiredAmount,
        decimal CreditableAmount,   // = Received (ที่ยังไม่ถูก claim)
        int PendingCount,
        decimal GlBalance,          // ยอดคงเหลือบัญชี 11910 ตาม GL
        decimal Difference);        // ทะเบียน (Pending+Received+Claimed) − GL

    // ═══════════ อ่าน ═══════════

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
            })
            .ToListAsync();

        return rows.Select(w => new WhtCreditRow(
            w.Id, w.TaxYear, w.CertificateNumber, w.CertificateDate,
            w.PayerContactId, w.PayerName, w.PayerTaxId,
            w.PayerFormType == WhtPayerFormType.Pnd3 ? "ภ.ง.ด.3" : "ภ.ง.ด.53",
            w.IncomeTypeCode, w.IncomeAmount, w.WhtRate, w.WhtAmount,
            w.Status.ToString(), w.DocumentId, w.DocNo,
            w.AttachmentId, w.ClaimedInTaxReportId, w.ClaimedAt, w.CarriedFromTaxYear, w.Notes,
            CheckRate(w.IncomeTypeCode, w.WhtRate))).ToList();
    }

    /// <summary>สรุปยอดต่อปีภาษี + กระทบกับยอดบัญชี 11910 จริง
    ///
    /// <para>ต่างเมื่อไร = มีรายการที่ลงบัญชีแล้วแต่ไม่อยู่ในทะเบียน (หรือกลับกัน)
    /// ซึ่งแปลว่ายอดเครดิตที่จะยื่นไม่ตรงกับสมุดบัญชี — ต้องตามหาก่อนยื่นแบบ</para></summary>
    public async Task<WhtCreditSummary> SummaryAsync(Guid companyId, int taxYear)
    {
        var rows = await _db.WhtCreditsReceived.AsNoTracking()
            .Where(w => w.CompanyId == companyId && w.TaxYear == taxYear)
            .Select(w => new { w.Status, w.WhtAmount })
            .ToListAsync();

        decimal Sum(WhtCreditStatus st) => rows.Where(r => r.Status == st).Sum(r => r.WhtAmount);
        var pending = Sum(WhtCreditStatus.Pending);
        var received = Sum(WhtCreditStatus.Received);
        var claimed = Sum(WhtCreditStatus.Claimed);
        var expired = Sum(WhtCreditStatus.Expired);

        var (start, end) = await FiscalRangeAsync(companyId, taxYear);
        var glBalance = await GlWhtBalanceAsync(companyId, start, end);

        return new WhtCreditSummary(taxYear, pending, received, claimed, expired,
            received, rows.Count(r => r.Status == WhtCreditStatus.Pending),
            glBalance, Math.Round(pending + received + claimed - glBalance, 2));
    }

    /// <summary>ยอด Dr สุทธิของบัญชี 11910 ในช่วงรอบบัญชี (จาก JE ที่ post จริง)</summary>
    public async Task<decimal> GlWhtBalanceAsync(Guid companyId, DateTime start, DateTime end)
    {
        var lines = await (from l in _db.JournalEntryLines.AsNoTracking()
                           join j in _db.JournalEntries.AsNoTracking() on l.JournalEntryId equals j.Id
                           join a in _db.ChartOfAccounts.AsNoTracking() on l.AccountId equals a.Id
                           where j.CompanyId == companyId && !j.IsDeleted && !l.IsDeleted
                                 && j.Status == JournalEntryStatus.Posted
                                 && j.ReversedByEntryId == null
                                 && j.EntryDate >= start && j.EntryDate <= end
                                 && a.AccountCode.StartsWith("11910")
                           select l.DebitAmount - l.CreditAmount).ToListAsync();
        return Math.Round(lines.Sum(), 2);
    }

    public async Task<(DateTime Start, DateTime End)> FiscalRangeAsync(Guid companyId, int taxYear)
    {
        var startMonth = await _db.Companies.AsNoTracking()
            .Where(c => c.Id == companyId).Select(c => c.FiscalYearStartMonth).FirstOrDefaultAsync();
        if (startMonth is < 1 or > 12) startMonth = 1;
        var start = new DateTime(taxYear, startMonth, 1);
        return (start, start.AddYears(1).AddDays(-1));
    }

    // ═══════════ เขียน ═══════════

    public record UpsertRequest(
        int TaxYear, string? CertificateNumber, DateTime? CertificateDate,
        Guid? PayerContactId, string? PayerName, string? PayerTaxId,
        WhtPayerFormType PayerFormType, string? IncomeTypeCode,
        decimal IncomeAmount, decimal WhtRate, decimal WhtAmount,
        Guid? DocumentId, Guid? AttachmentId, string? Notes);

    public async Task<Guid> CreateAsync(Guid companyId, UpsertRequest r)
    {
        if (r.WhtAmount <= 0) throw new InvalidOperationException("ยอดภาษีที่ถูกหักต้องมากกว่า 0");
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
        _db.WhtCreditsReceived.Add(e);
        await _db.SaveChangesAsync();
        return e.Id;
    }

    public async Task UpdateAsync(Guid companyId, Guid id, UpsertRequest r)
    {
        var e = await Load(companyId, id);
        GuardEditable(e);
        e.TaxYear = r.TaxYear;
        e.CertificateNumber = Trim(r.CertificateNumber);
        e.CertificateDate = r.CertificateDate;
        e.PayerContactId = r.PayerContactId;
        if (!string.IsNullOrWhiteSpace(r.PayerName)) e.PayerName = r.PayerName.Trim();
        e.PayerTaxId = Trim(r.PayerTaxId);
        e.PayerFormType = r.PayerFormType;
        e.IncomeTypeCode = Trim(r.IncomeTypeCode);
        e.IncomeAmount = r.IncomeAmount;
        e.WhtRate = r.WhtRate;
        if (r.WhtAmount > 0) e.WhtAmount = r.WhtAmount;
        if (r.AttachmentId.HasValue) e.AttachmentId = r.AttachmentId;
        e.Notes = Trim(r.Notes);
        // มีเลขที่ใบ = ถือว่าได้รับใบแล้ว (เครดิตได้); ลบเลขออก = กลับไปรอใบ
        e.Status = string.IsNullOrWhiteSpace(e.CertificateNumber)
            ? WhtCreditStatus.Pending : WhtCreditStatus.Received;
        e.UpdatedAt = DateTime.UtcNow;
        await _db.SaveChangesAsync();
    }

    /// <summary>แนบสแกนหนังสือรับรอง + ระบุเลขที่/วันที่ → เปลี่ยนเป็น "ได้รับแล้ว"</summary>
    public async Task MarkReceivedAsync(Guid companyId, Guid id,
        string certificateNumber, DateTime? certificateDate, Guid? attachmentId)
    {
        if (string.IsNullOrWhiteSpace(certificateNumber))
            throw new InvalidOperationException("ต้องระบุเลขที่หนังสือรับรอง — กฎหมายให้เครดิตเฉพาะรายการที่มีใบจริง");
        var e = await Load(companyId, id);
        GuardEditable(e);
        e.CertificateNumber = certificateNumber.Trim();
        e.CertificateDate = certificateDate;
        if (attachmentId.HasValue) e.AttachmentId = attachmentId;
        e.Status = WhtCreditStatus.Received;
        e.UpdatedAt = DateTime.UtcNow;
        await _db.SaveChangesAsync();
    }

    public async Task DeleteAsync(Guid companyId, Guid id)
    {
        var e = await Load(companyId, id);
        GuardEditable(e);
        e.IsDeleted = true;
        e.UpdatedAt = DateTime.UtcNow;
        await _db.SaveChangesAsync();
    }

    /// <summary>ตัดรายการที่ไม่ได้รับใบจนพ้นกำหนด — ยอดนี้เครดิตไม่ได้แล้ว</summary>
    public async Task ExpireAsync(Guid companyId, Guid id, string? reason)
    {
        var e = await Load(companyId, id);
        if (e.Status == WhtCreditStatus.Claimed)
            throw new InvalidOperationException("รายการนี้ใช้เครดิตในแบบยื่นไปแล้ว — ตัดทิ้งไม่ได้");
        e.Status = WhtCreditStatus.Expired;
        e.Notes = string.IsNullOrWhiteSpace(reason) ? e.Notes : $"{e.Notes} | ตัดสูญ: {reason}";
        e.UpdatedAt = DateTime.UtcNow;
        await _db.SaveChangesAsync();
    }

    // ═══════════ ใช้เครดิตกับแบบยื่น (เฟส 3) ═══════════

    /// <summary>ยอดเครดิตที่ "ใช้ได้จริง" ของปีภาษีนั้น = มีใบรับรองแล้วและยังไม่ถูก
    /// ใช้ในแบบใด — เรียกจาก <c>TaxService.GenerateCitReport</c></summary>
    public async Task<decimal> CreditableAmountAsync(Guid companyId, int taxYear)
        => await _db.WhtCreditsReceived.AsNoTracking()
            .Where(w => w.CompanyId == companyId && w.TaxYear == taxYear
                && w.Status == WhtCreditStatus.Received)
            .SumAsync(w => (decimal?)w.WhtAmount) ?? 0m;

    /// <summary>ประทับว่าเครดิตถูกใช้ไปกับแบบไหนแล้ว — กันใช้ซ้ำระหว่าง ภ.ง.ด.51
    /// (ครึ่งปี) กับ ภ.ง.ด.50 (สิ้นปี). คืนยอดที่ประทับได้จริง</summary>
    public async Task<decimal> ClaimAsync(Guid companyId, int taxYear, Guid taxReportId)
    {
        var rows = await _db.WhtCreditsReceived
            .Where(w => w.CompanyId == companyId && w.TaxYear == taxYear
                && w.Status == WhtCreditStatus.Received)
            .ToListAsync();
        if (rows.Count == 0) return 0m;
        foreach (var w in rows)
        {
            w.Status = WhtCreditStatus.Claimed;
            w.ClaimedInTaxReportId = taxReportId;
            w.ClaimedAt = DateTime.UtcNow;
            w.UpdatedAt = DateTime.UtcNow;
        }
        await _db.SaveChangesAsync();
        return rows.Sum(w => w.WhtAmount);
    }

    /// <summary>ยกเลิกการใช้เครดิตของแบบหนึ่ง (เช่นลบ/สร้างแบบใหม่) →
    /// คืนสถานะเป็น Received ให้ใช้กับแบบถัดไปได้</summary>
    public async Task<int> UnclaimAsync(Guid companyId, Guid taxReportId)
    {
        var rows = await _db.WhtCreditsReceived
            .Where(w => w.CompanyId == companyId && w.ClaimedInTaxReportId == taxReportId)
            .ToListAsync();
        foreach (var w in rows)
        {
            w.Status = WhtCreditStatus.Received;
            w.ClaimedInTaxReportId = null;
            w.ClaimedAt = null;
            w.UpdatedAt = DateTime.UtcNow;
        }
        if (rows.Count > 0) await _db.SaveChangesAsync();
        return rows.Count;
    }

    // ═══════════ ปิดปี: ล้างบัญชี 11910 (เฟส 5) ═══════════

    public record SettleRequest(int TaxYear, decimal UsedAgainstCit, decimal RefundRequested,
        decimal WriteOff, string? Reason);

    /// <summary>ลง JE ปิดปีสำหรับเครดิตภาษีถูกหัก — ล้างยอดออกจาก 11910 ตามที่ใช้จริง
    ///
    /// <para>ถ้าไม่ทำขั้นนี้ ยอด 11910 จะพองสะสมข้ามปีจนกระทบยอดกับแบบยื่นไม่ได้
    /// (ยอดในงบจะเป็นเครดิตสะสมทุกปีรวมกัน ไม่ใช่ของปีที่ยังใช้ได้จริง)</para>
    ///
    /// <list type="bullet">
    /// <item>ใช้หักกับภาษีที่ต้องเสีย → Dr ภาษีเงินได้ค้างจ่าย / Cr 11910</item>
    /// <item>ขอคืน → Dr ภาษีเงินได้จ่ายล่วงหน้า-รอรับคืน (11920) / Cr 11910</item>
    /// <item>ตัดสูญ (ไม่ได้ใบจนพ้นกำหนด) → Dr ค่าใช้จ่ายภาษีเรียกคืนไม่ได้ / Cr 11910</item>
    /// <item>ยกไปปีหน้า → ไม่ต้องลง JE (คงไว้ที่ 11910)</item>
    /// </list>
    /// </summary>
    public async Task<string> SettleYearEndAsync(Guid companyId, SettleRequest r, string actor)
    {
        var total = r.UsedAgainstCit + r.RefundRequested + r.WriteOff;
        if (total <= 0) throw new InvalidOperationException("ระบุจำนวนที่จะล้างอย่างน้อยหนึ่งช่อง");

        var (start, end) = await FiscalRangeAsync(companyId, r.TaxYear);
        var glBalance = await GlWhtBalanceAsync(companyId, start, end);
        if (total > glBalance + 0.01m)
            throw new InvalidOperationException(
                $"ยอดที่จะล้าง {total:N2} เกินยอดคงเหลือบัญชี 11910 ของปีภาษีนี้ ({glBalance:N2}) — "
                + "ตรวจการกระทบยอดก่อน");

        var wht = await FindAccountAsync(companyId, "11910")
            ?? throw new InvalidOperationException("ไม่พบผัง 11910 ภาษีถูกหัก ณ ที่จ่าย");

        var lines = new List<(Guid AccountId, decimal Dr, decimal Cr, string Desc)>();
        if (r.UsedAgainstCit > 0)
        {
            var citPayable = await FindAccountAsync(companyId, "21810")
                ?? await FindAccountAsync(companyId, "218")
                ?? throw new InvalidOperationException("ไม่พบผังภาษีเงินได้ค้างจ่าย (21810/218)");
            lines.Add((citPayable.Id, r.UsedAgainstCit, 0, $"ใช้เครดิตภาษีถูกหักหักกับ ภ.ง.ด.50 ปี {r.TaxYear}"));
        }
        if (r.RefundRequested > 0)
        {
            var refundAcc = await FindAccountAsync(companyId, "11920")
                ?? throw new InvalidOperationException("ไม่พบผังภาษีเงินได้จ่ายล่วงหน้า (11920)");
            lines.Add((refundAcc.Id, r.RefundRequested, 0, $"ขอคืนภาษีถูกหัก ปี {r.TaxYear}"));
        }
        if (r.WriteOff > 0)
        {
            var expAcc = await FindAccountAsync(companyId, "53")
                ?? await _db.ChartOfAccounts.FirstOrDefaultAsync(a => a.CompanyId == companyId
                        && a.AccountType == AccountType.Expense && a.Level >= 4 && a.IsActive)
                ?? throw new InvalidOperationException("ไม่พบผังค่าใช้จ่ายสำหรับตัดสูญ");
            lines.Add((expAcc.Id, r.WriteOff, 0, $"ตัดภาษีถูกหักที่เรียกคืนไม่ได้ ปี {r.TaxYear}"));
        }
        lines.Add((wht.Id, 0, total, $"ล้างภาษีถูกหัก ณ ที่จ่าย ปี {r.TaxYear}"));

        var entryNumber = await NextJeNumberAsync(companyId);
        var je = new JournalEntry
        {
            CompanyId = companyId,
            EntryNumber = entryNumber,
            EntryDate = end,                       // ลงวันสุดท้ายของรอบบัญชีที่ปิด
            JournalType = JournalType.General,
            Description = $"ปิดปีภาษี {r.TaxYear} — ล้างภาษีถูกหัก ณ ที่จ่าย"
                + (string.IsNullOrWhiteSpace(r.Reason) ? "" : $" ({r.Reason})"),
            Reference = $"WHT-{r.TaxYear}",
            Status = JournalEntryStatus.Posted,
            TotalDebit = total,
            TotalCredit = total,
            CreatedBy = actor,
            IsAutoGenerated = true,
        };
        _db.JournalEntries.Add(je);
        var order = 1;
        foreach (var (accId, dr, cr, desc) in lines)
        {
            _db.JournalEntryLines.Add(new JournalEntryLine
            {
                JournalEntryId = je.Id, AccountId = accId,
                DebitAmount = dr, CreditAmount = cr,
                Description = desc, LineOrder = order++,
            });
        }
        await _db.SaveChangesAsync();
        _logger.LogInformation("ปิดปีภาษี {Year}: ล้าง 11910 รวม {Total} ({Je})", r.TaxYear, total, entryNumber);
        return entryNumber;
    }

    private async Task<ChartOfAccount?> FindAccountAsync(Guid companyId, string code)
        => await _db.ChartOfAccounts.FirstOrDefaultAsync(a =>
            a.CompanyId == companyId && a.AccountCode == code && a.IsActive);

    private async Task<string> NextJeNumberAsync(Guid companyId)
    {
        var pattern = $"JV-{DateTime.UtcNow:yyyyMM}-";
        var last = await _db.JournalEntries.IgnoreQueryFilters()
            .Where(j => j.CompanyId == companyId && j.EntryNumber.StartsWith(pattern))
            .OrderByDescending(j => j.EntryNumber).Select(j => j.EntryNumber).FirstOrDefaultAsync();
        var next = 1;
        if (last != null && int.TryParse(last[pattern.Length..], out var n)) next = n + 1;
        return $"{pattern}{next:D4}";
    }

    // ═══════════ helpers ═══════════

    private async Task<WhtCreditReceived> Load(Guid companyId, Guid id)
        => await _db.WhtCreditsReceived.FirstOrDefaultAsync(w => w.Id == id && w.CompanyId == companyId)
           ?? throw new KeyNotFoundException("ไม่พบรายการภาษีถูกหัก ณ ที่จ่าย");

    private static void GuardEditable(WhtCreditReceived e)
    {
        if (e.Status == WhtCreditStatus.Claimed)
            throw new InvalidOperationException(
                "รายการนี้ถูกใช้เป็นเครดิตในแบบยื่นไปแล้ว — แก้ไขไม่ได้ "
                + "(ถ้าต้องแก้จริง ให้ยกเลิกการใช้เครดิตของแบบนั้นก่อน)");
    }

    private static string? Trim(string? s) => string.IsNullOrWhiteSpace(s) ? null : s.Trim();

    private async Task<string?> PayerNameFromContactAsync(Guid companyId, Guid? contactId)
        => contactId.HasValue
            ? await _db.Contacts.AsNoTracking().Where(c => c.Id == contactId.Value && c.CompanyId == companyId)
                .Select(c => c.Name).FirstOrDefaultAsync()
            : null;

    /// <summary>ตรวจอัตราที่ผู้จ่ายหักมา เทียบกับ ท.ป.4/2528 — ผู้จ่ายหักผิดอัตรา
    /// เกิดขึ้นบ่อย (หัก 5% ค่าบริการแทน 3%) ทำให้เครดิตไม่ตรงกับที่เขานำส่งจริง</summary>
    private static string? CheckRate(string? incomeType, decimal rate)
    {
        if (string.IsNullOrWhiteSpace(incomeType) || rate <= 0) return null;
        var expected = incomeType.Replace(" ", "") switch
        {
            var t when t.Contains("40(2)") || t.Contains("40(7)") || t.Contains("40(8)") => 3m,
            var t when t.Contains("40(3)") || t.Contains("40(6)") => 3m,
            var t when t.Contains("40(5)") => 5m,
            var t when t.Contains("40(4)") => 1m,
            _ => 0m,
        };
        if (expected <= 0) return null;
        return Math.Abs(rate - expected) < 0.01m ? null
            : $"อัตราที่ถูกหัก {rate:N2}% ไม่ตรงกับ {incomeType} (ปกติ {expected:N0}%) — ตรวจกับผู้จ่าย";
    }
}
