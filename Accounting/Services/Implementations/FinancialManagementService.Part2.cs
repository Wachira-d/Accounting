using Accounting.Data;
using Accounting.Helpers;
using Accounting.Models.DTOs.FinancialManagement;
using Accounting.Models.Entities;
using Accounting.Models.Enums;
using Microsoft.EntityFrameworkCore;

namespace Accounting.Services.Implementations;

// Partial class for features 6-9
public partial class FinancialManagementService
{
    // ==================== 6. CORPORATE INCOME TAX ====================

    public async Task<CITResponse> CalculateCITAsync(Guid companyId, CalculateCITRequest request, string userId)
    {
        // ══ รอบ 230 (TAX_PENALTY_AUDIT §6 · กติกาที่ Helpers/CitCalculation ตัวเดียว) ══
        // เดิม: ขั้นบันได SME กับทุกบริษัท · รอบ ม.ค.–ธ.ค. เสมอ · ภ.ง.ด.51 คิดจากกำไร 6 เดือนตรง ๆ · หักค่าใช้จ่ายภาษี 580xx · ไม่บวกกลับ §65 ตรี
        var period = CitCalculation.NormalizePeriod(request.TaxPeriod)
            ?? throw new BusinessRuleException($"รอบภาษี \"{request.TaxPeriod}\" ไม่รู้จัก (HalfYear/Annual)", "CIT-POST-PERIOD");
        var ceYear = CitPayment.CeYear(request.TaxYear)
            ?? throw new BusinessRuleException($"ปีภาษี \"{request.TaxYear}\" อ่านไม่ได้ — กรอกปี พ.ศ. หรือ ค.ศ. ของรอบบัญชี", "CIT-POST-PERIOD");
        var isHalf = period == CitCalculation.HalfYear;
        var negative = CitCalculation.RejectNegative(("รายจ่ายบวกกลับ", request.AddBackItems), ("รายจ่ายหักเพิ่ม", request.DeductionItems),
            ("รายได้ที่ได้รับยกเว้น", request.ExemptIncome), ("ภาษีหัก ณ ที่จ่าย", request.WithholdingTaxCredit), ("ภาษีจ่ายล่วงหน้า", request.PrepaidTaxCredit), ("ผลขาดทุนยกมา", request.LossCarryForward));
        if (negative != null) throw new BusinessRuleException(negative, "CIT-NEGATIVE");
        if (isHalf && request.PrepaidTaxCredit is > 0m)
            throw new BusinessRuleException("ภ.ง.ด.51 ไม่มี \"ภาษีจ่ายล่วงหน้า\" ให้หัก (ใช้ตอน ภ.ง.ด.50) — ล้างช่องนี้แล้วคำนวณใหม่", "CIT-PND51-PREPAID");
        if (isHalf && request.LossCarryForward is not null)
            throw new BusinessRuleException("ผลขาดทุนยกมาใช้กับ ภ.ง.ด.50 (ทั้งรอบ) — ล้างช่องนี้สำหรับ ภ.ง.ด.51", "CIT-PND51-LOSS");
        if (!isHalf && (request.EstimatedAnnualProfit != null || request.HalfYearBasis != null || request.ShortFiscalYear == true))
            throw new BusinessRuleException("ช่องประมาณการกำไร / วิธียื่น / รอบไม่ถึง 12 เดือน ใช้กับ ภ.ง.ด.51 (ครึ่งปี) เท่านั้น", "CIT-PND51-ONLY");

        var company = await _db.Companies.AsNoTracking().Where(c => c.Id == companyId)
            .Select(c => new { c.FiscalYearStartMonth, c.PaidUpCapital, c.CreatedAt }).FirstOrDefaultAsync()
            ?? throw new KeyNotFoundException("ไม่พบบริษัท");
        // (2) รอบบัญชีของบริษัท (Company.FiscalYearStartMonth) — ไม่ใช่ ม.ค.–ธ.ค.
        var range = CitCalculation.PeriodFor(ceYear, company.FiscalYearStartMonth, period);
        var notes = new List<string>();
        if (FiscalYear.NormalizeStartMonth(company.FiscalYearStartMonth) != 1)
            notes.Add($"รอบบัญชีบริษัท {range.FullYear.Start:dd/MM/yyyy}–{range.FullYear.EndInclusive:dd/MM/yyyy} (หน้านำส่งคิดกำหนดยื่น/เงินเพิ่มจากวันสิ้นงวดนี้)");
        if (!CitRateTable.HasScheduleFor(ceYear))
            notes.Add($"⚠️ ตารางอัตราไม่มีรอบบัญชี {ceYear + 543} — ใช้ชุดที่ใกล้ที่สุด (ต้องยืนยันกับผู้ทำบัญชี)");

        // (3) รอบแรก/ไม่ถึง 12 เดือน — ไม่ต้องยื่น ภ.ง.ด.51 (ผู้ใช้ประกาศ · ระบบเตือนเมื่อเริ่มใช้ระบบในรอบนี้)
        if (isHalf)
        {
            var (exempt, exemptNote) = CitCalculation.Pnd51Exemption(request.ShortFiscalYear, company.CreatedAt, range.FullYear);
            if (exempt)
                throw new BusinessRuleException($"{exemptNote} — ไม่ต้องคำนวณ/ตั้งหนี้ ภ.ง.ด.51 ของรอบนี้ (ภาษีทั้งรอบคำนวณที่ ภ.ง.ด.50)", CitCalculation.Pnd51ExemptRuleCode);
            if (exemptNote != null) notes.Add(exemptNote);
        }

        // ฐาน GL + บวกกลับ §65 ตรี + ส่วนเกินค่ารับรอง — ตัวโหลดตัวเดียวกับไฟล์ ภ.ง.ด.51 (Tax/CitPeriodFigures · ฝ่ายค้าน S1) · ไม่หัก 580xx (4)
        var fig = await Tax.CitPeriodFigures.LoadAsync(_db, _tax, companyId, range, company.PaidUpCapital);
        var revenue = fig.Revenue;
        var expenses = fig.Expenses;
        var citExpenseExcluded = fig.CitExpenseExcluded;
        var accountingProfit = fig.AccountingProfit;
        var section65Ter = fig.Section65TerAddBack;
        var entertainmentExcess = fig.EntertainmentExcess;
        var autoAddBack = fig.AutoAddBack;

        var addBack = request.AddBackItems ?? 0m;
        var deductions = request.DeductionItems ?? 0m;
        var exemptIncome = request.ExemptIncome ?? 0m;
        // ฝ่ายค้าน S4: ผลขาดทุนยกมา (ภ.ง.ด.50) — ว่าง = ตัวตั้งเดียวกับรายงาน ภ.ง.ด.50 (FIFO 5 รอบ จากรายงานที่ยื่นแล้ว) · ครึ่งปีไม่หัก (ประมาณการ)
        var profitBeforeLoss = CitCalculation.TaxableProfit(accountingProfit, autoAddBack, addBack, exemptIncome, deductions);
        decimal lossUsed = 0m;
        if (!isHalf)
        {
            if (request.LossCarryForward is decimal userLoss)
                lossUsed = CitCalculation.LossCarryForwardUsed(userLoss, profitBeforeLoss);
            else
            {
                var prior = await _db.TaxReports.AsNoTracking()
                    .Where(t => t.CompanyId == companyId && t.TaxType == TaxType.CorporateIncomeTax
                        && t.Year >= ceYear - 5 && t.Year < ceYear
                        && TaxFilingLockPolicy.DeclaredOrFiledStatuses.Contains(t.Status))
                    .Select(t => new { t.Year, t.NetVat }).ToListAsync();
                var available = CitCalculation.LossCarryForwardAvailable(prior.Select(p => (p.Year, p.NetVat)), ceYear);
                lossUsed = CitCalculation.LossCarryForwardUsed(available, profitBeforeLoss);
                if (lossUsed > 0m)
                    notes.Add($"หักผลขาดทุนยกมา {lossUsed:N2} (จากรายงาน ภ.ง.ด.50 ที่ยื่นแล้ว 5 รอบก่อน — ตัวเดียวกับหน้ารายงานภาษี · แก้ได้ที่ช่องผลขาดทุนยกมา)");
            }
        }
        var taxableProfit = CitCalculation.TaxableProfit(accountingProfit, autoAddBack, addBack, exemptIncome, deductions + lossUsed);

        // (1) SME ต่อบริษัท — ทุนที่ชำระแล้ว ≤ 5 ล. และรายได้ทั้งรอบ ≤ 30 ล. (ครึ่งปี = รายได้ครึ่งปี × 2) · ผู้ใช้กำหนดเองได้พร้อมเหตุผล
        var sme = CitCalculation.DecideSme(company.PaidUpCapital, isHalf ? revenue * 2m : revenue, request.SmeOverride, request.SmeOverrideReason);

        decimal taxAmount;
        decimal? estimatedAnnual = null;
        string? basis = null;
        if (isHalf)
        {
            // (3) §67 ทวิ — ประมาณการทั้งรอบ × อัตรา ÷ 2 (หรือกำไรจริงครึ่งปี §67 ทวิ(2))
            var h = CitCalculation.HalfYearTax(taxableProfit, request.EstimatedAnnualProfit, request.HalfYearBasis, sme.IsSme, ceYear);
            taxAmount = h.HalfYearTax;
            estimatedAnnual = h.EstimatedAnnualProfit;
            basis = h.Basis;
            notes.Insert(0, h.Note);
        }
        else
        {
            taxAmount = CitRateTable.Compute(taxableProfit, sme.IsSme, ceYear);
        }

        // เครดิต: ว่าง = ระบบดึง — ภาษีถูกหัก (หนังสือรับรองที่ได้รับแล้ว ลงวันที่ในงวด) · ภ.ง.ด.50 หัก ภ.ง.ด.51 ที่ตั้งหนี้แล้วของรอบเดียวกัน
        var whtCredit = request.WithholdingTaxCredit ?? await _db.WhtCreditsReceived.AsNoTracking()
            .CreditableInPeriod(companyId, range.Start, range.EndInclusive).SumAsync(w => (decimal?)w.WhtAmount) ?? 0m;
        if (request.WithholdingTaxCredit is null)
        {
            if (whtCredit > 0m) notes.Add($"ภาษีถูกหัก ณ ที่จ่าย {whtCredit:N2} ดึงจากหนังสือรับรองที่ได้รับแล้วซึ่งลงวันที่ในงวด (กติกาเดียวกับ ภ.ง.ด.50)");
            var undated = await _db.WhtCreditsReceived.AsNoTracking().CreditableUndated(companyId, ceYear).CountAsync();
            if (undated > 0) notes.Add($"⚠️ หนังสือรับรองที่ได้รับแล้ว {undated} ใบไม่มีวันที่ — ไม่ได้นับเป็นเครดิต (กรอกวันที่ในทะเบียน หรือกรอกยอดเอง)");
        }
        var prepaidCredit = isHalf ? 0m : request.PrepaidTaxCredit ?? await DefaultCitPrepaidCreditAsync(companyId, ceYear);
        if (!isHalf && request.PrepaidTaxCredit is null && prepaidCredit > 0m)
            notes.Add($"ภาษีจ่ายล่วงหน้า {prepaidCredit:N2} = ภ.ง.ด.51 ที่ตั้งหนี้แล้วของรอบเดียวกัน");
        var netPayable = CitPayment.Split(taxAmount, prepaidCredit, whtCredit).Payable;

        var effectiveRate = taxableProfit > 0m && !isHalf ? Math.Round(taxAmount / taxableProfit * 100m, 2, MidpointRounding.AwayFromZero)
            : estimatedAnnual is > 0m ? Math.Round(taxAmount * 2m / estimatedAnnual.Value * 100m, 2, MidpointRounding.AwayFromZero) : 0m;
        notes.Insert(0, sme.Reason);
        notes.Add($"กำไรทางบัญชี {accountingProfit:N2} (ไม่หักค่าใช้จ่ายภาษีเงินได้ 580xx {citExpenseExcluded:N2}) + บวกกลับ §65 ตรี {section65Ter:N2}"
            + $" + ส่วนเกินค่ารับรอง {entertainmentExcess:N2} + บวกกลับที่กรอก {addBack:N2} − รายได้ยกเว้น {exemptIncome:N2} − หักเพิ่ม {deductions:N2}"
            + $" − ผลขาดทุนยกมา {lossUsed:N2} = กำไรสุทธิทางภาษี {taxableProfit:N2}");

        var entity = new CorporateIncomeTax
        {
            CompanyId = companyId, TaxYear = request.TaxYear.Trim(), TaxPeriod = period,
            TotalRevenue = revenue, TotalExpenses = expenses, AccountingProfit = accountingProfit,
            AddBackItems = addBack, DeductionItems = deductions, TaxableProfit = taxableProfit,
            TaxRate = effectiveRate, TaxAmount = taxAmount,
            WithholdingTaxCredit = whtCredit, PrepaidTaxCredit = prepaidCredit,
            NetTaxPayable = netPayable,
            Notes = request.Notes, CreatedBy = userId,
            PeriodStart = range.Start, PeriodEnd = range.EndInclusive,
            IsSme = sme.IsSme, SmeReason = sme.Reason, SmeOverrideReason = sme.Overridden ? request.SmeOverrideReason!.Trim() : null,
            AutoAddBack = autoAddBack, ExemptIncome = exemptIncome, CitExpenseExcluded = citExpenseExcluded, LossCarryForwardUsed = lossUsed,
            EstimatedAnnualProfit = estimatedAnnual, HalfYearBasis = basis,
            CalculationNote = string.Join("\n", notes),
        };

        _db.Set<CorporateIncomeTax>().Add(entity);
        await _db.SaveChangesAsync();
        return MapCIT(entity);
    }

    /// <summary>ภ.ง.ด.51 ที่ตั้งหนี้แล้วของรอบเดียวกัน (ค่าตั้งต้น "ภาษีจ่ายล่วงหน้า" ของ ภ.ง.ด.50) = ยอดที่ JE ตั้งหนี้ลง 11920 (NetTaxPayable) ·
    /// ตัวหารายการเดียวกับไฟล์ ภ.ง.ด.51 (<see cref="Tax.CitPeriodFigures.LivePostedHalfYearAsync"/>)</summary>
    private async Task<decimal> DefaultCitPrepaidCreditAsync(Guid companyId, int ceYear)
        => (await Tax.CitPeriodFigures.LivePostedHalfYearAsync(_db, companyId, ceYear))?.NetTaxPayable ?? 0m;

    public async Task<CitPeriodPreviewResponse> PreviewCITPeriodAsync(Guid companyId, string taxYear, string taxPeriod)
    {
        var period = CitCalculation.NormalizePeriod(taxPeriod)
            ?? throw new BusinessRuleException($"รอบภาษี \"{taxPeriod}\" ไม่รู้จัก (HalfYear/Annual)", "CIT-POST-PERIOD");
        var ceYear = CitPayment.CeYear(taxYear)
            ?? throw new BusinessRuleException($"ปีภาษี \"{taxYear}\" อ่านไม่ได้ — กรอกปี พ.ศ. หรือ ค.ศ. ของรอบบัญชี", "CIT-POST-PERIOD");
        var startMonth = await _db.Companies.AsNoTracking().Where(c => c.Id == companyId).Select(c => c.FiscalYearStartMonth).FirstOrDefaultAsync();
        var range = CitCalculation.PeriodFor(ceYear, startMonth, period);
        var type = CitPayment.TypeOf(period)!;
        // วันครบกำหนดจากตารางวันที่ตัวเดียว (เดือนสิ้นงวดจริง — ตัวเดียวกับหน้านำส่ง · รอบ 230 S2)
        var due = CitPayment.DueKey(type, ceYear, range.EndInclusive, startMonth);
        var paper = TaxFilingDeadline.For(type, due.Year, due.Month).Paper;
        var label = $"{(range.IsHalfYear ? "ภ.ง.ด.51 ครึ่งปีแรก" : "ภ.ง.ด.50 รอบบัญชี")} {ceYear + 543}: {range.Start:dd/MM/yyyy}–{range.EndInclusive:dd/MM/yyyy} · "
            + $"ลงบัญชีตั้งหนี้วันที่ {range.PostingDate:dd/MM/yyyy} · กำหนดยื่น (แบบกระดาษ) {paper:dd/MM/yyyy}";
        var warn = range.PostingDate.Date > DateTime.UtcNow.AddHours(7).Date ? "งวดยังไม่สิ้นสุด — คำนวณดูได้ แต่บันทึก (ตั้งหนี้) ได้หลังสิ้นงวด" : null;
        return new CitPeriodPreviewResponse(period, ceYear, range.Start, range.EndInclusive, range.PostingDate, paper, label, warn);
    }

    public async Task<List<CITResponse>> GetCITListAsync(Guid companyId)
    {
        var items = await _db.Set<CorporateIncomeTax>()
            .Where(c => c.CompanyId == companyId && !c.IsDeleted)
            .OrderByDescending(c => c.TaxYear).ThenByDescending(c => c.TaxPeriod)
            .ToListAsync();
        return items.Select(MapCIT).ToList();
    }

    public async Task<CITResponse> PostCITAsync(Guid companyId, Guid id, string userId)
    {
        var entity = await _db.Set<CorporateIncomeTax>()
            .FirstOrDefaultAsync(c => c.Id == id && c.CompanyId == companyId)
            ?? throw new KeyNotFoundException("ไม่พบข้อมูล");

        if (entity.Status is "Filed" or CitCalculation.NoTaxDueStatus)
            throw new BusinessRuleException(entity.Status == "Filed" ? "รายการนี้ตั้งหนี้ภาษีแล้ว" : "รายการนี้บันทึกแล้วว่าไม่มีภาษีต้องชำระ", "CIT-POST-DONE", 409);

        // ══ รอบ 228 (TAX_PENALTY_AUDIT §3 ข้อ 1 · ฝ่ายค้าน S1/S2) — ตั้งหนี้ให้เส้นชำระ (หน้านำส่ง ภ.ง.ด.51/50 · Helpers/CitPayment) ล้างได้พอดี ══
        // รูป JE จากตัวตั้งเดียว CitPayment.AccrualPlan: ภ.ง.ด.51 Dr 11920 = ยอดที่ต้องชำระ / Cr 21920 (ไม่แตะ 11910 — ใช้ครั้งเดียวตอน ภ.ง.ด.50) ·
        // ภ.ง.ด.50 Dr 580xx ภาษีทั้งปี / Cr เครดิต ภ.ง.ด.51 (ผังที่ JE ครึ่งปีลง Dr — ใหม่ 11920 · เดิม 580xx) / Cr 11910 / Cr 21920 สุทธิ
        // (เดิม Cr 21920 เต็มภาษี ⇒ หนี้ค้างเท่าเครดิตตลอดไป · ภ.ง.ด.51 ลงค่าใช้จ่ายซ้ำ) · ผังไม่ครบ ⇒ ปฏิเสธ (เดิมข้ามการลงบัญชีเงียบ ๆ แล้วประทับ "Filed")
        var type = CitPayment.TypeOf(entity.TaxPeriod)
            ?? throw new BusinessRuleException($"รอบภาษี \"{entity.TaxPeriod}\" ไม่รู้จัก (HalfYear/Annual)", "CIT-POST-PERIOD");
        var ceYear = CitPayment.CeYear(entity.TaxYear)
            ?? throw new BusinessRuleException($"ปีภาษี \"{entity.TaxYear}\" อ่านไม่ได้", "CIT-POST-PERIOD");

        // ══ รอบ 230 (5) + ฝ่ายค้าน S3/S8: ลงบัญชี ณ วันสิ้นงวดที่เก็บไว้ตอนคำนวณ (ไม่ใช่วันที่กด) · แถวก่อนรอบ 230 ไม่มีวันสิ้นงวด ⇒ ให้คำนวณใหม่
        // (ไม่เดารอบจากค่าตั้งปัจจุบัน) · ด่านงวด (ยังไม่จบ/ปิดแล้ว) ทำก่อนทุกทาง รวมรายการภาษี 0 · ภาษี 0 ⇒ สถานะ NoTaxDue (ไม่ใช่ "Filed" — ไม่ได้ยื่นอะไร)
        if (entity.PeriodEnd is not DateTime storedEnd)
            throw new BusinessRuleException(
                "รายการคำนวณนี้บันทึกก่อนรอบ 230 (ไม่มีวันสิ้นงวดตามรอบบัญชีบริษัท) — คำนวณใหม่ก่อนลงบัญชี", CitCalculation.RecalculateRuleCode, 409);
        var postingDate = storedEnd.Date;
        var postRange = new CitCalculation.TaxPeriodRange(ceYear, entity.PeriodStart ?? postingDate, postingDate, entity.TaxPeriod == CitCalculation.HalfYear,
            FiscalYear.RangeFor(ceYear, 1));   // FullYear ไม่ถูกใช้ในด่านลงบัญชี (PostingRefusal อ่านแค่วันสิ้นงวด/ชนิด/ปี)
        var closedReason = await Journal.JournalEntryBuilder.ClosedPeriodReasonAsync(_db, companyId, postingDate);
        var (refusal, refusalCode) = CitCalculation.PostingRefusal(postRange, DateTime.UtcNow.AddHours(7).Date, closedReason);
        if (refusal != null) throw new BusinessRuleException(refusal, refusalCode, 409);
        if (entity.TaxAmount <= 0)
        {
            entity.Status = CitCalculation.NoTaxDueStatus;
            await _db.SaveChangesAsync();
            return MapCIT(entity);
        }

        // S2: รอบเดียวกัน (ปี ค.ศ. + ครึ่งปี/ทั้งปี) ตั้งหนี้ซ้ำไม่ได้ ถ้ารายการอื่นยังมี JE ที่มีผล — คำนวณใหม่ต้องกลับรายการ JE เดิมก่อน (ไม่งั้น 21920 ตั้งสองเท่า)
        var otherJeIds = (await _db.Set<CorporateIncomeTax>().AsNoTracking()
                .Where(c => c.CompanyId == companyId && c.Id != entity.Id && c.TaxPeriod == entity.TaxPeriod && c.JournalEntryId != null)
                .Select(c => new { c.TaxYear, c.JournalEntryId }).ToListAsync())
            .Where(c => CitPayment.CeYear(c.TaxYear) == ceYear).Select(c => c.JournalEntryId!.Value).ToList();
        if (otherJeIds.Count > 0)
        {
            var live = await _db.JournalEntries.AsNoTracking()
                .Where(j => j.CompanyId == companyId && otherJeIds.Contains(j.Id) && !j.IsDeleted
                    && j.Status == JournalEntryStatus.Posted && j.ReversedByEntryId == null)
                .Select(j => j.EntryNumber).ToListAsync();
            if (live.Count > 0)
                throw new BusinessRuleException(
                    $"{CitPayment.PeriodLabel(type, ceYear)} ตั้งหนี้ภาษีไว้แล้ว (JE {string.Join(", ", live)}) — "
                    + "ถ้าคำนวณใหม่ ให้กลับรายการ JE เดิมที่หน้าสมุดรายวันก่อน แล้วค่อยบันทึกรายการนี้ (กันหนี้ 21920 ตั้งซ้ำ)", "CIT-POST-DUP", 409);
        }

        // ══ รอบ 230 (5): ลงบัญชี ณ วันสิ้นงวด (สิ้นครึ่งปี/สิ้นรอบบัญชีของบริษัท) ไม่ใช่วันที่กด — ภาษีอยู่ในงบของงวดที่เกิด ·
        // งวดยังไม่จบ/งวดบัญชีปิดแล้ว ⇒ ปฏิเสธพร้อมทางไปต่อ (ตัวตัดสินงวดปิดตัวเดียวกับใบสำคัญทั่วไป) · แถวก่อนรอบ 230 ไม่มี PeriodEnd ⇒ คิดจากเดือนเริ่มรอบปัจจุบัน

        async Task<ChartOfAccount> Req(string code, Func<ChartOfAccount, bool>? fallback, string name)
        {
            var exact = await _db.ChartOfAccounts.FirstOrDefaultAsync(a => a.CompanyId == companyId && a.AccountCode == code && a.IsActive);
            if (exact != null) return exact;
            if (fallback != null)
            {
                var alt = (await _db.ChartOfAccounts.Include(a => a.ParentAccount).Where(a => a.CompanyId == companyId && a.IsActive).ToListAsync()).Where(fallback)
                    .OrderBy(a => a.AccountCode).FirstOrDefault();
                if (alt != null) return alt;
            }
            throw new BusinessRuleException($"ไม่พบผังบัญชี {code} ({name}) — สร้างผังที่หน้า “ผังบัญชี” ก่อนบันทึกภาษีเงินได้นิติบุคคล", "CIT-POST-ACCOUNT");
        }

        var plan = CitPayment.AccrualPlan(type, entity.TaxAmount, entity.PrepaidTaxCredit, entity.WithholdingTaxCredit);
        var lines = new List<JournalEntryLine>();
        int order = 1;
        foreach (var pl in plan)
        {
            Guid accId;
            string desc;
            switch (pl.Role)
            {
                case CitPayment.AccrualRole.Expense:
                    accId = (await Req("58000", a => CitCalculation.IsCitExpenseAccount(a.AccountCode, a.AccountType, a.ParentAccount?.AccountCode), "ภาษีเงินได้นิติบุคคล (ค่าใช้จ่าย)")).Id;
                    desc = "ภาษีเงินได้นิติบุคคล (ภ.ง.ด.50)"; break;
                case CitPayment.AccrualRole.Prepaid:
                    accId = (await Req(CitPayment.PrepaidCode, null, "ภาษีเงินได้นิติบุคคลจ่ายล่วงหน้า")).Id;
                    desc = "ภาษีเงินได้นิติบุคคลจ่ายล่วงหน้า (ภ.ง.ด.51 · ยอดที่ชำระ)"; break;
                case CitPayment.AccrualRole.PrepaidOffset:
                {
                    // ผังเครดิต ภ.ง.ด.51 = ผังที่ JE ตั้งหนี้ครึ่งปีของปีเดียวกันลง Dr (ตั้งแบบเดิมลงค่าใช้จ่าย ⇒ Cr ค่าใช้จ่ายเท่าที่ลงซ้ำ) · ไม่พบ ⇒ 11920
                    var halfJeIds = (await _db.Set<CorporateIncomeTax>().AsNoTracking()
                            .Where(c => c.CompanyId == companyId && c.TaxPeriod == "HalfYear" && c.JournalEntryId != null && c.Id != entity.Id)
                            .Select(c => new { c.TaxYear, c.JournalEntryId, c.CreatedAt }).ToListAsync())
                        .Where(c => CitPayment.CeYear(c.TaxYear) == ceYear).OrderByDescending(c => c.CreatedAt).Select(c => c.JournalEntryId!.Value).ToList();
                    var halfDebitAcc = halfJeIds.Count == 0 ? null : await _db.JournalEntryLines.AsNoTracking()
                        .Where(l => l.JournalEntryId == halfJeIds[0] && l.JournalEntry.CompanyId == companyId && l.DebitAmount > 0)
                        .Select(l => (Guid?)l.AccountId).FirstOrDefaultAsync();
                    accId = halfDebitAcc ?? (await Req(CitPayment.PrepaidCode, null, "ภาษีเงินได้นิติบุคคลจ่ายล่วงหน้า")).Id;
                    desc = "หักภาษีที่ชำระตาม ภ.ง.ด.51"; break;
                }
                case CitPayment.AccrualRole.WhtCredit:
                    accId = (await Req(CitPayment.WhtCreditCode, null, "ภาษีถูกหัก ณ ที่จ่าย")).Id;
                    desc = "หักภาษีถูกหัก ณ ที่จ่าย (เครดิตภาษีทั้งปี)"; break;
                default:
                    accId = (await Req(CitPayment.PayableCode, null, "ภาษีเงินได้นิติบุคคลค้างจ่าย")).Id;
                    desc = "ภาษีเงินได้นิติบุคคลค้างจ่าย (ยอดที่ต้องชำระ)"; break;
            }
            lines.Add(new() { AccountId = accId, DebitAmount = pl.Debit, CreditAmount = pl.Credit, Description = desc, LineOrder = order++ });
        }
        var jeTotal = plan.Sum(x => x.Debit);

        if (lines.Count > 0)
        {
            var jeNum = await Journal.JournalEntryBuilder.NextJournalNumberAsync(_db, companyId, "JV", postingDate);
            var fpId = await GetFiscalPeriodIdAsync(companyId, postingDate);

            var je = new JournalEntry
            {
                CompanyId = companyId, EntryNumber = jeNum, EntryDate = postingDate,
                JournalType = JournalType.General,
                Description = $"ภาษีเงินได้นิติบุคคล {entity.TaxYear} ({entity.TaxPeriod}) — งวดสิ้นสุด {postingDate:dd/MM/yyyy}",
                Reference = $"CIT-{entity.TaxYear}-{entity.TaxPeriod}",
                Status = JournalEntryStatus.Posted, IsAutoGenerated = true, FiscalPeriodId = fpId,
                TotalDebit = jeTotal, TotalCredit = jeTotal,
                Lines = lines
            };
            _db.JournalEntries.Add(je);
            await _db.SaveChangesAsync();
            entity.JournalEntryId = je.Id;
        }

        entity.Status = "Filed";
        await _db.SaveChangesAsync();
        return MapCIT(entity);
    }

    private static CITResponse MapCIT(CorporateIncomeTax c) => new(
        c.Id, c.TaxYear, c.TaxPeriod, c.TotalRevenue, c.TotalExpenses, c.AccountingProfit,
        c.AddBackItems, c.DeductionItems, c.TaxableProfit, c.TaxRate, c.TaxAmount,
        c.WithholdingTaxCredit, c.PrepaidTaxCredit, c.NetTaxPayable,
        c.Status, c.Notes, c.JournalEntryId,
        c.PeriodStart, c.PeriodEnd, c.IsSme, c.SmeReason, c.SmeOverrideReason,
        c.AutoAddBack, c.ExemptIncome, c.CitExpenseExcluded, c.EstimatedAnnualProfit, c.HalfYearBasis, c.CalculationNote, c.LossCarryForwardUsed);

    // ==================== 7. PROFIT APPROPRIATION ====================

    public async Task<ProfitAppropriationResponse> CreateProfitAppropriationAsync(Guid companyId, CreateProfitAppropriationRequest request, string userId)
    {
        var refNo = $"DIV-{DateTime.UtcNow:yyyyMM}-{Guid.NewGuid().ToString()[..4].ToUpper()}";

        // Get net profit from retained earnings
        var retainedEarningsAcc = await _db.ChartOfAccounts.FirstOrDefaultAsync(a =>
            a.CompanyId == companyId && a.AccountCode.StartsWith("3202") && a.IsActive);

        decimal netProfit = 0;
        if (retainedEarningsAcc != null)
        {
            netProfit = await _db.JournalEntries
                .Where(j => j.CompanyId == companyId)
                .WhereInLedger()   // ★ ทีม O 2026-10-22 — ยอดคงเหลือกำไรสะสม: Posted + Reversed (คู่กลับรายการหักล้างกัน)
                .SelectMany(j => j.Lines)
                .Where(l => l.AccountId == retainedEarningsAcc.Id)
                .SumAsync(l => l.CreditAmount - l.DebitAmount);
        }

        var retained = netProfit - request.LegalReserve - request.DividendAmount;

        var entity = new ProfitAppropriation
        {
            CompanyId = companyId, ReferenceNo = refNo, ApprovalDate = DateTime.UtcNow,
            FiscalYear = request.FiscalYear, NetProfit = netProfit,
            LegalReserve = request.LegalReserve, DividendAmount = request.DividendAmount,
            RetainedAmount = retained, DividendPerShare = request.DividendPerShare,
            Notes = request.Notes, CreatedBy = userId
        };

        _db.Set<ProfitAppropriation>().Add(entity);
        await _db.SaveChangesAsync();
        return MapAppropriation(entity);
    }

    public async Task<List<ProfitAppropriationResponse>> GetProfitAppropriationsAsync(Guid companyId)
    {
        var items = await _db.Set<ProfitAppropriation>()
            .Where(p => p.CompanyId == companyId && !p.IsDeleted)
            .OrderByDescending(p => p.ApprovalDate).ToListAsync();
        return items.Select(MapAppropriation).ToList();
    }

    public async Task<ProfitAppropriationResponse> ApproveProfitAppropriationAsync(Guid companyId, Guid id, string userId)
    {
        var entity = await _db.Set<ProfitAppropriation>()
            .FirstOrDefaultAsync(p => p.Id == id && p.CompanyId == companyId)
            ?? throw new KeyNotFoundException("ไม่พบข้อมูล");

        if (entity.Status != "Draft") throw new InvalidOperationException("สถานะไม่ถูกต้อง");

        var retainedAcc = await _db.ChartOfAccounts.FirstOrDefaultAsync(a =>
            a.CompanyId == companyId && a.AccountCode.StartsWith("3202") && a.IsActive);
        var reserveAcc = await _db.ChartOfAccounts.FirstOrDefaultAsync(a =>
            a.CompanyId == companyId && a.AccountCode.StartsWith("3201") && a.IsActive);
        var cashAcc = await _db.ChartOfAccounts.FirstOrDefaultAsync(a =>
            a.CompanyId == companyId && a.AccountCode.StartsWith("111") && a.IsActive);

        var fpId = await GetFiscalPeriodIdAsync(companyId, DateTime.UtcNow);

        // 1. Legal reserve: Dr กำไรสะสม 32020 / Cr สำรองฯ 32010
        if (entity.LegalReserve > 0 && retainedAcc != null && reserveAcc != null)
        {
            var jeNum = await NextJvNumberAsync(companyId);
            var je = new JournalEntry
            {
                CompanyId = companyId, EntryNumber = jeNum, EntryDate = DateTime.UtcNow,
                JournalType = JournalType.General,
                Description = $"จัดสรรสำรองตามกฎหมาย ปี {entity.FiscalYear}",
                Reference = entity.ReferenceNo, Status = JournalEntryStatus.Posted,
                IsAutoGenerated = true, FiscalPeriodId = fpId,
                TotalDebit = entity.LegalReserve, TotalCredit = entity.LegalReserve,
                Lines = new List<JournalEntryLine>
                {
                    new() { AccountId = retainedAcc.Id, DebitAmount = entity.LegalReserve, Description = "จัดสรรกำไรสะสม", LineOrder = 1 },
                    new() { AccountId = reserveAcc.Id, CreditAmount = entity.LegalReserve, Description = "สำรองตามกฎหมาย", LineOrder = 2 }
                }
            };
            _db.JournalEntries.Add(je);
            await _db.SaveChangesAsync();
            entity.ReserveJournalId = je.Id;
        }

        // 2. Dividend declaration: Dr กำไรสะสม 32020 / Cr เงินปันผลค้างจ่าย 22xxx (not direct to cash)
        if (entity.DividendAmount > 0 && retainedAcc != null)
        {
            var divPayableAcc = await _db.ChartOfAccounts.DefaultPostingCandidates(_db).FirstOrDefaultAsync(a =>
                a.CompanyId == companyId && a.AccountCode == "22010")
                ?? await _db.ChartOfAccounts.DefaultFallbackCandidates(_db).FirstOrDefaultAsync(a =>
                a.CompanyId == companyId && a.AccountCode.StartsWith("220"));
            var creditAcc = divPayableAcc ?? cashAcc;
            if (creditAcc != null)
            {
                var jeNum = await NextJvNumberAsync(companyId);
                var je = new JournalEntry
                {
                    CompanyId = companyId, EntryNumber = jeNum, EntryDate = DateTime.UtcNow,
                    JournalType = JournalType.General,
                    Description = divPayableAcc != null
                        ? $"ประกาศจ่ายเงินปันผล ปี {entity.FiscalYear}"
                        : $"จ่ายเงินปันผล ปี {entity.FiscalYear}",
                    Reference = entity.ReferenceNo, Status = JournalEntryStatus.Posted,
                    IsAutoGenerated = true, FiscalPeriodId = fpId,
                    TotalDebit = entity.DividendAmount, TotalCredit = entity.DividendAmount,
                    Lines = new List<JournalEntryLine>
                    {
                        new() { AccountId = retainedAcc.Id, DebitAmount = entity.DividendAmount, Description = "จ่ายเงินปันผล", LineOrder = 1 },
                        new() { AccountId = creditAcc.Id, CreditAmount = entity.DividendAmount, Description = divPayableAcc != null ? "เงินปันผลค้างจ่าย" : "จ่ายเงินปันผล", LineOrder = 2 }
                    }
                };
                _db.JournalEntries.Add(je);
                await _db.SaveChangesAsync();
                entity.DividendJournalId = je.Id;
            }
        }

        entity.Status = "Distributed";
        await _db.SaveChangesAsync();
        return MapAppropriation(entity);
    }

    private static ProfitAppropriationResponse MapAppropriation(ProfitAppropriation p) => new(
        p.Id, p.ReferenceNo, p.ApprovalDate, p.FiscalYear,
        p.NetProfit, p.LegalReserve, p.DividendAmount, p.RetainedAmount, p.DividendPerShare,
        p.Status, p.Notes, p.ReserveJournalId, p.DividendJournalId);

    // ==================== 8. CAPITAL TRANSACTIONS ====================

    public async Task<CapitalTransactionResponse> CreateCapitalTransactionAsync(Guid companyId, CreateCapitalTransactionRequest request, string userId)
    {
        var refNo = $"CAP-{DateTime.UtcNow:yyyyMM}-{Guid.NewGuid().ToString()[..4].ToUpper()}";
        var premium = request.PaidAmount - (request.ShareQuantity * request.ParValue);

        var entity = new CapitalTransaction
        {
            CompanyId = companyId, ReferenceNo = refNo, TransactionDate = request.TransactionDate ?? DateTime.UtcNow,
            TransactionType = request.TransactionType,
            ShareQuantity = request.ShareQuantity, ParValue = request.ParValue,
            PaidAmount = request.PaidAmount, SharePremium = premium,
            BoardResolutionRef = request.BoardResolutionRef,
            DbrRegistrationRef = request.DbrRegistrationRef,
            Notes = request.Notes, CreatedBy = userId
        };

        _db.Set<CapitalTransaction>().Add(entity);
        await _db.SaveChangesAsync();
        return MapCapital(entity);
    }

    public async Task<List<CapitalTransactionResponse>> GetCapitalTransactionsAsync(Guid companyId)
    {
        var items = await _db.Set<CapitalTransaction>()
            .Where(c => c.CompanyId == companyId && !c.IsDeleted)
            .OrderByDescending(c => c.TransactionDate).ToListAsync();
        return items.Select(MapCapital).ToList();
    }

    public async Task<CapitalTransactionResponse> CompleteCapitalTransactionAsync(Guid companyId, Guid id, string userId)
    {
        var entity = await _db.Set<CapitalTransaction>()
            .FirstOrDefaultAsync(c => c.Id == id && c.CompanyId == companyId)
            ?? throw new KeyNotFoundException("ไม่พบข้อมูล");

        if (entity.Status == "Completed") throw new InvalidOperationException("ดำเนินการแล้ว");

        var cashAcc = await _db.ChartOfAccounts.FirstOrDefaultAsync(a =>
            a.CompanyId == companyId && a.AccountCode.StartsWith("111") && a.IsActive);
        var capitalAcc = await _db.ChartOfAccounts.FirstOrDefaultAsync(a =>
            a.CompanyId == companyId && a.AccountCode.StartsWith("3102") && a.IsActive); // ทุนชำระแล้ว
        var premiumAcc = await _db.ChartOfAccounts.FirstOrDefaultAsync(a =>
            a.CompanyId == companyId && a.AccountCode.StartsWith("3103") && a.IsActive); // ส่วนเกินมูลค่าหุ้น

        if (cashAcc != null && capitalAcc != null)
        {
            var jeNum = await NextJvNumberAsync(companyId);
            var fpId = await GetFiscalPeriodIdAsync(companyId, DateTime.UtcNow);
            var lines = new List<JournalEntryLine>();
            var capitalAmount = entity.ShareQuantity * entity.ParValue;

            if (entity.TransactionType == "Increase")
            {
                // Dr Cash / Cr Capital + Premium
                lines.Add(new() { AccountId = cashAcc.Id, DebitAmount = entity.PaidAmount, Description = "รับเงินเพิ่มทุน", LineOrder = 1 });
                lines.Add(new() { AccountId = capitalAcc.Id, CreditAmount = capitalAmount, Description = "เพิ่มทุนชำระแล้ว", LineOrder = 2 });
                if (entity.SharePremium > 0 && premiumAcc != null)
                    lines.Add(new() { AccountId = premiumAcc.Id, CreditAmount = entity.SharePremium, Description = "ส่วนเกินมูลค่าหุ้น", LineOrder = 3 });
            }
            else // Decrease
            {
                lines.Add(new() { AccountId = capitalAcc.Id, DebitAmount = capitalAmount, Description = "ลดทุนชำระแล้ว", LineOrder = 1 });
                lines.Add(new() { AccountId = cashAcc.Id, CreditAmount = entity.PaidAmount, Description = "จ่ายคืนทุน", LineOrder = 2 });
            }

            var totalDr = lines.Sum(l => l.DebitAmount);
            var totalCr = lines.Sum(l => l.CreditAmount);

            var je = new JournalEntry
            {
                CompanyId = companyId, EntryNumber = jeNum, EntryDate = DateTime.UtcNow,
                JournalType = JournalType.General,
                Description = $"{(entity.TransactionType == "Increase" ? "เพิ่มทุน" : "ลดทุน")} {entity.ShareQuantity:N0} หุ้น",
                Reference = entity.ReferenceNo, Status = JournalEntryStatus.Posted,
                IsAutoGenerated = true, FiscalPeriodId = fpId,
                TotalDebit = totalDr, TotalCredit = totalCr, Lines = lines
            };
            _db.JournalEntries.Add(je);
            await _db.SaveChangesAsync();
            entity.JournalEntryId = je.Id;
        }

        entity.Status = "Completed";
        await _db.SaveChangesAsync();
        return MapCapital(entity);
    }

    private static CapitalTransactionResponse MapCapital(CapitalTransaction c) => new(
        c.Id, c.ReferenceNo, c.TransactionDate, c.TransactionType,
        c.ShareQuantity, c.ParValue, c.PaidAmount, c.SharePremium,
        c.Status, c.BoardResolutionRef, c.DbrRegistrationRef, c.Notes, c.JournalEntryId);

    // ==================== 9. SHORT-TERM INVESTMENT ====================

    public async Task<InvestmentResponse> CreateInvestmentAsync(Guid companyId, CreateInvestmentRequest request, string userId)
    {
        // ผังบัญชีจากคำขอ: ของบริษัทนี้ · เปิดใช้ · ไม่ใช่บัญชีคุม — ตัวตัดสินเดียว PostingAccountGuard (2026-10-14 · ID#13) ·
        // เดิมเขียนลง JournalEntryLine.AccountId ดิบ ๆ ⇒ ผังบริษัทอื่น/ปิดใช้/บัญชีคุมลงสมุดได้
        await _db.EnsurePostingAccountAsync(companyId, request.InvestmentAccountId, "บัญชีเงินลงทุน");
        var refNo = $"INV-{DateTime.UtcNow:yyyyMM}-{Guid.NewGuid().ToString()[..4].ToUpper()}";

        var entity = new ShortTermInvestment
        {
            CompanyId = companyId, ReferenceNo = refNo, InvestmentType = request.InvestmentType,
            Description = request.Description, PurchaseDate = request.PurchaseDate,
            MaturityDate = request.MaturityDate, PurchaseCost = request.PurchaseCost,
            CurrentValue = request.PurchaseCost, InterestRate = request.InterestRate,
            InvestmentAccountId = request.InvestmentAccountId,
            InstitutionName = request.InstitutionName, AccountNumber = request.AccountNumber,
            CreatedBy = userId
        };

        // Auto journal: Dr Investment 112xx / Cr Cash 111xx
        var cashAcc = await _db.ChartOfAccounts.FirstOrDefaultAsync(a =>
            a.CompanyId == companyId && a.AccountCode.StartsWith("111") && a.IsActive);

        if (cashAcc != null)
        {
            var jeNum = await NextJvNumberAsync(companyId);
            var fpId = await GetFiscalPeriodIdAsync(companyId, request.PurchaseDate);

            var je = new JournalEntry
            {
                CompanyId = companyId, EntryNumber = jeNum, EntryDate = request.PurchaseDate,
                JournalType = JournalType.General,
                Description = $"ซื้อเงินลงทุนชั่วคราว: {request.Description}",
                Reference = refNo, Status = JournalEntryStatus.Posted,
                IsAutoGenerated = true, FiscalPeriodId = fpId,
                TotalDebit = request.PurchaseCost, TotalCredit = request.PurchaseCost,
                Lines = new List<JournalEntryLine>
                {
                    new() { AccountId = request.InvestmentAccountId, DebitAmount = request.PurchaseCost, Description = $"เงินลงทุน - {request.Description}", LineOrder = 1 },
                    new() { AccountId = cashAcc.Id, CreditAmount = request.PurchaseCost, Description = "จ่ายเงินซื้อลงทุน", LineOrder = 2 }
                }
            };
            _db.JournalEntries.Add(je);
            await _db.SaveChangesAsync();
            entity.PurchaseJournalId = je.Id;
        }

        _db.Set<ShortTermInvestment>().Add(entity);
        await _db.SaveChangesAsync();
        return MapInvestment(entity);
    }

    public async Task<List<InvestmentResponse>> GetInvestmentsAsync(Guid companyId)
    {
        var items = await _db.Set<ShortTermInvestment>()
            .Include(i => i.InvestmentAccount)
            .Where(i => i.CompanyId == companyId && !i.IsDeleted)
            .OrderByDescending(i => i.PurchaseDate).ToListAsync();
        return items.Select(MapInvestment).ToList();
    }

    public async Task<InvestmentResponse> SellInvestmentAsync(Guid companyId, Guid id, SellInvestmentRequest request, string userId)
    {
        var entity = await _db.Set<ShortTermInvestment>()
            .Include(i => i.InvestmentAccount)
            .FirstOrDefaultAsync(i => i.Id == id && i.CompanyId == companyId)
            ?? throw new KeyNotFoundException("ไม่พบข้อมูล");

        if (entity.Status != "Active") throw new InvalidOperationException("สถานะไม่ถูกต้อง");

        entity.SaleDate = request.SaleDate ?? DateTime.UtcNow;
        entity.SaleProceeds = request.SaleProceeds;
        entity.GainLoss = request.SaleProceeds - entity.PurchaseCost;
        entity.Status = "Sold";

        // Journal: Dr Cash / Cr Investment + Gain or Loss
        var cashAcc = await _db.ChartOfAccounts.FirstOrDefaultAsync(a =>
            a.CompanyId == companyId && a.AccountCode.StartsWith("111") && a.IsActive);

        if (cashAcc != null)
        {
            var jeNum = await NextJvNumberAsync(companyId);
            var fpId = await GetFiscalPeriodIdAsync(companyId, DateTime.UtcNow);
            var lines = new List<JournalEntryLine>
            {
                new() { AccountId = cashAcc.Id, DebitAmount = request.SaleProceeds, Description = "รับเงินจากขายเงินลงทุน", LineOrder = 1 },
                new() { AccountId = entity.InvestmentAccountId, CreditAmount = entity.PurchaseCost, Description = $"ขายเงินลงทุน - {entity.Description}", LineOrder = 2 }
            };

            if (entity.GainLoss > 0) // Gain
            {
                var gainAcc = await _db.ChartOfAccounts.FirstOrDefaultAsync(a =>
                    a.CompanyId == companyId && a.AccountCode.StartsWith("4303") && a.IsActive); // กำไรจากขายสินทรัพย์
                if (gainAcc != null)
                    lines.Add(new() { AccountId = gainAcc.Id, CreditAmount = entity.GainLoss.Value, Description = "กำไรจากขายเงินลงทุน", LineOrder = 3 });
            }
            else if (entity.GainLoss < 0) // Loss
            {
                var lossAcc = await _db.ChartOfAccounts.FirstOrDefaultAsync(a =>
                    a.CompanyId == companyId && a.AccountCode.StartsWith("5711") && a.IsActive); // ขาดทุน
                if (lossAcc != null)
                    lines.Add(new() { AccountId = lossAcc.Id, DebitAmount = Math.Abs(entity.GainLoss.Value), Description = "ขาดทุนจากขายเงินลงทุน", LineOrder = 3 });
            }

            var totalDr = lines.Sum(l => l.DebitAmount);
            var totalCr = lines.Sum(l => l.CreditAmount);

            var je = new JournalEntry
            {
                CompanyId = companyId, EntryNumber = jeNum, EntryDate = DateTime.UtcNow,
                JournalType = JournalType.General,
                Description = $"ขายเงินลงทุน: {entity.Description}",
                Reference = entity.ReferenceNo, Status = JournalEntryStatus.Posted,
                IsAutoGenerated = true, FiscalPeriodId = fpId,
                TotalDebit = totalDr, TotalCredit = totalCr, Lines = lines
            };
            _db.JournalEntries.Add(je);
            await _db.SaveChangesAsync();
            entity.SaleJournalId = je.Id;
        }

        await _db.SaveChangesAsync();
        return MapInvestment(entity);
    }

    private static InvestmentResponse MapInvestment(ShortTermInvestment i) => new(
        i.Id, i.ReferenceNo, i.InvestmentType, i.Description,
        i.PurchaseDate, i.MaturityDate, i.SaleDate,
        i.PurchaseCost, i.CurrentValue, i.SaleProceeds, i.GainLoss, i.InterestRate,
        i.Status, i.InstitutionName, i.AccountNumber,
        i.InvestmentAccountId, i.InvestmentAccount?.AccountName ?? "",
        i.PurchaseJournalId, i.SaleJournalId);
}
