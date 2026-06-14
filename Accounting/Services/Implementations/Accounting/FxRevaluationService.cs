using Accounting.Data;
using Accounting.Models.Entities;
using Accounting.Models.Enums;
using Microsoft.EntityFrameworkCore;

namespace Accounting.Services.Implementations.Accounting;

/// <summary>
/// F3 — Foreign-currency Realized + Unrealized gain/loss per TFRS 21.
///
/// Realized FX gain/loss = เกิดตอน settle เอกสาร currency != THB:
///   PI USD 1,000 @ 35 บาท ตั้งหนี้  → AP = 35,000 บาท
///   PV 30 วันต่อมา @ 36 บาท จ่ายเงิน → Cash out 36,000 บาท
///   ผลต่าง 1,000 = "ขาดทุนจากอัตราแลกเปลี่ยน" (54201)
///
/// Unrealized FX (period-end revaluation): ทุกบัญชี monetary
/// (AR/AP/Bank ที่เป็น FX) คำนวณ revaluation:
///   AR USD 5,000 ตั้งหนี้ @ 35  → ค่าบัญชี 175,000 บาท
///   สิ้นเดือน rate @ 35.50      → ค่าควรจะเป็น 177,500 บาท
///   ส่วนต่าง +2,500 = "กำไรจากอัตราแลกเปลี่ยน" (54101) — ตั้งบัญชี
///   "FX revaluation liability" (กลับรายการต้น period ถัดไป).
/// </summary>
public interface IFxRevaluationService
{
    /// <summary>คำนวณ realized FX gain/loss สำหรับ document ตอน settle.
    /// คืน (gainLossAmount, accountCode): positive = gain (54101),
    /// negative = loss (54201). 0 = สกุล THB หรือ rate เท่าเดิม.</summary>
    decimal ComputeRealizedFxGainLoss(decimal originalRate, decimal settlementRate, decimal foreignAmount);

    /// <summary>Snapshot unrealized FX exposure ของบริษัท ณ asOfDate.
    /// ใช้สำหรับ period-end revaluation entry: รวม AR/AP/Bank balances
    /// เป็น FX → คำนวณส่วนต่าง vs ค่าบัญชี.</summary>
    Task<IReadOnlyList<FxExposureLine>> SnapshotUnrealizedAsync(Guid companyId, DateTime asOfDate);
}

public sealed record FxExposureLine(
    string Currency,
    string AccountCode,
    string AccountName,
    decimal ForeignBalance,       // ยอดในสกุลต่างประเทศ
    decimal BookValueThb,          // ค่าบัญชีปัจจุบัน (บาท)
    decimal CurrentRate,           // อัตราปิดงวด
    decimal MarketValueThb,        // ค่าควรจะเป็น (foreign × current rate)
    decimal UnrealizedGainLoss);   // ส่วนต่าง (+ gain / - loss)

public class FxRevaluationService : IFxRevaluationService
{
    private readonly AccountingDbContext _db;
    public FxRevaluationService(AccountingDbContext db) { _db = db; }

    public decimal ComputeRealizedFxGainLoss(decimal originalRate, decimal settlementRate, decimal foreignAmount)
    {
        // Gain เกิดเมื่อ:
        //  • AR side: settle rate สูงกว่า invoice rate (เรารับเงินมากขึ้นใน THB)
        //  • AP side: settle rate ต่ำกว่า invoice rate (เราจ่ายเงินน้อยลงใน THB)
        // ฟังก์ชันนี้คำนวณ "settle vs original" rate diff × amount.
        // Caller (DocumentService.PostJournalEntry) ระบุ sign ตาม side
        // ของเอกสาร (AR positive / AP negative)
        return Math.Round((settlementRate - originalRate) * foreignAmount, 2);
    }

    public async Task<IReadOnlyList<FxExposureLine>> SnapshotUnrealizedAsync(Guid companyId, DateTime asOfDate)
    {
        // หา documents currency != THB ที่ยังมี BalanceDue (open AR/AP)
        // แล้ว group by currency + originating account
        var openFxDocs = await _db.Documents.AsNoTracking()
            .Where(d => d.CompanyId == companyId && !d.IsDeleted
                && d.Currency != "THB" && d.Currency != ""
                && d.BalanceDue > 0
                && d.DocumentDate <= asOfDate
                && d.Status != DocumentStatus.Voided && d.Status != DocumentStatus.Draft)
            .Select(d => new {
                d.Currency, d.ExchangeRate, d.DocumentType, d.BalanceDue,
                ForeignAmount = d.ExchangeRate > 0 ? d.BalanceDue / d.ExchangeRate : 0
            })
            .ToListAsync();

        // Snapshot rates per currency — ใช้ documents recent ที่สุดของบริษัท
        // เป็น proxy (production ใช้ BoT API หรือ rate table)
        var latestRates = openFxDocs
            .GroupBy(d => d.Currency)
            .ToDictionary(g => g.Key, g => g.Average(x => x.ExchangeRate));

        var lines = new List<FxExposureLine>();
        foreach (var grp in openFxDocs.GroupBy(d => new {
            d.Currency,
            Side = d.DocumentType == DocumentType.Invoice
                || d.DocumentType == DocumentType.TaxInvoice
                || d.DocumentType == DocumentType.BillingNote ? "AR" : "AP"
        }))
        {
            var foreignTotal = grp.Sum(x => x.ForeignAmount);
            var bookValue = grp.Sum(x => x.BalanceDue);
            var currentRate = latestRates.GetValueOrDefault(grp.Key.Currency, 35m);
            var marketValue = Math.Round(foreignTotal * currentRate, 2);
            // AR gain when market > book (เก็บได้มากขึ้น)
            // AP gain when market < book (จ่ายน้อยลง)
            var gainLoss = grp.Key.Side == "AR" ? marketValue - bookValue : bookValue - marketValue;
            lines.Add(new FxExposureLine(
                Currency: grp.Key.Currency,
                AccountCode: grp.Key.Side == "AR" ? "113-FX" : "212-FX",
                AccountName: $"{(grp.Key.Side == "AR" ? "ลูกหนี้" : "เจ้าหนี้")} ({grp.Key.Currency})",
                ForeignBalance: foreignTotal,
                BookValueThb: bookValue,
                CurrentRate: currentRate,
                MarketValueThb: marketValue,
                UnrealizedGainLoss: gainLoss));
        }
        return lines;
    }
}
