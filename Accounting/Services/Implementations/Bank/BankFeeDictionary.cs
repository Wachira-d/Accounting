namespace Accounting.Services.Implementations.Bank;

/// <summary>
/// Classifies a (bank amount − candidate gross) delta against the
/// catalogue of EXPECTED Thai-bank deductions, so a small mismatch is
/// labelled with its real cause ("ค่าธรรมเนียมโอนข้ามธนาคาร 25 บาท",
/// "หัก ณ ที่จ่าย 3%", "FX variance 1.8%") instead of just being
/// flagged as "ยอดไม่ตรง". When the delta is explained the UI can
/// surface a single-click "สร้าง JE ค่าธรรมเนียม" follow-up, and the
/// confidence ceiling can stay high because the difference is
/// understood — only an UNEXPLAINED delta should drop confidence.
/// </summary>
public static class BankFeeDictionary
{
    public sealed record DeltaExplanation(
        DeltaKind Kind,
        string Label,
        decimal AbsoluteAmount,
        decimal? Percentage,
        string? SuggestedGlAccountHint);

    public enum DeltaKind
    {
        None,           // no delta or ≤0.01 → exact
        Rounding,       // sub-baht VAT/satang noise, ≤0.50
        FixedFee,       // matches one of the standard bank fee tiers
        WithholdingTax, // delta ≈ X% of gross at standard WHT rates
        FxVariance,     // delta within typical FX swing for inward TT
        Unexplained,    // we can't account for the difference
    }

    /// <summary>Fixed-baht fees seen across Thai banks. Bank takes the fee
    /// at source so the receiver's deposit = invoice − fee. When delta
    /// matches one of these to the satang AND the bank-flow category is
    /// consistent (cross-bank transfer / bill payment / SWIFT in), label
    /// it as that fee. Round numbers; channel hints help disambiguate.</summary>
    private static readonly (decimal Amount, string Label, BankFlowCategory[] Channels)[] _fixedFees = new[]
    {
        (10m,     "ค่าธรรมเนียม Bill Payment", new[] { BankFlowCategory.BillPayment }),
        (15m,     "ค่าธรรมเนียมชำระบิลข้ามธนาคาร", new[] { BankFlowCategory.BillPayment }),
        (20m,     "ค่าธรรมเนียม ATM ต่างธนาคาร", new[] { BankFlowCategory.Transfer, BankFlowCategory.CounterCash }),
        (25m,     "ค่าธรรมเนียมโอนข้ามธนาคาร (ต่ำกว่า 1 หมื่น)", new[] { BankFlowCategory.Transfer, BankFlowCategory.AutoCredit }),
        (35m,     "ค่าธรรมเนียมโอนข้ามธนาคาร (1 หมื่น–5 หมื่น)", new[] { BankFlowCategory.Transfer, BankFlowCategory.AutoCredit }),
        (50m,     "ค่าธรรมเนียม SMART ภายในประเทศ", new[] { BankFlowCategory.AutoCredit }),
        (75m,     "ค่าธรรมเนียมโอนข้ามธนาคาร (5 หมื่น+)", new[] { BankFlowCategory.Transfer, BankFlowCategory.AutoCredit }),
        (100m,    "ค่าธรรมเนียม Counter / นำฝากต่างจังหวัด", new[] { BankFlowCategory.CounterCash, BankFlowCategory.Cheque }),
        (150m,    "ค่าธรรมเนียมเช็คเรียกเก็บต่างจังหวัด", new[] { BankFlowCategory.Cheque }),
        (200m,    "ค่าธรรมเนียมโอนต่างจังหวัด", new[] { BankFlowCategory.Transfer, BankFlowCategory.CounterCash }),
        (300m,    "ค่าธรรมเนียม Inward TT (ขั้นต่ำ)", new[] { BankFlowCategory.InwardTT }),
        (500m,    "ค่าธรรมเนียม Inward TT", new[] { BankFlowCategory.InwardTT }),
        (1_000m,  "ค่าธรรมเนียม Inward TT (กลาง)", new[] { BankFlowCategory.InwardTT }),
    };

    // Standard Thai WHT rates that show up as a delta vs gross.
    private static readonly decimal[] _whtRates = new[] { 0.01m, 0.02m, 0.03m, 0.05m, 0.10m, 0.15m };

    /// <summary>Try to label a delta. `bankAmount` is the absolute deposit /
    /// withdrawal; `candidateGross` is the candidate document's true face
    /// value. `category` comes from BankFlowClassifier; only channels in
    /// the fee's whitelist are considered.</summary>
    public static DeltaExplanation Classify(decimal bankAmount, decimal candidateGross, BankFlowCategory category)
    {
        var delta = candidateGross - bankAmount;       // SIGNED: positive = bank received less
        var absD = Math.Abs(delta);
        if (absD <= 0.01m)
            return new(DeltaKind.None, "ยอดตรงพอดี", 0m, 0m, null);

        // 1) Sub-baht / VAT rounding noise.
        if (absD <= 0.50m)
            return new(DeltaKind.Rounding, $"ปัดเศษ {absD:N2} บาท", absD, null, "5901 - Misc Adjustment");

        // 2) WHT — only meaningful when bank received LESS than gross (delta>0).
        if (delta > 0 && candidateGross > 0)
        {
            var pct = delta / candidateGross;
            foreach (var r in _whtRates)
            {
                if (Math.Abs(pct - r) <= 0.0015m)   // ±0.15% tolerance
                    return new(DeltaKind.WithholdingTax,
                        $"หัก ณ ที่จ่าย {r * 100:N1}% = {absD:N2} บาท",
                        absD, r, "1304 - WHT Receivable");
            }
        }

        // 3) Fixed bank fee — match within ±0.50 baht and the channel makes sense.
        if (delta > 0)
        {
            foreach (var (amt, label, channels) in _fixedFees)
            {
                if (Math.Abs(absD - amt) <= 0.50m && channels.Contains(category))
                    return new(DeltaKind.FixedFee, label, absD, null, "5503 - Bank Charges");
            }
        }

        // 4) FX variance — Inward TT routinely swings ±3% due to spot vs booking rate.
        if (category == BankFlowCategory.InwardTT && candidateGross > 0)
        {
            var pct = absD / candidateGross;
            if (pct <= 0.03m)
                return new(DeltaKind.FxVariance,
                    $"FX variance {pct * 100:N2}% ({(delta > 0 ? "ขาดทุน" : "กำไร")}อัตราแลกเปลี่ยน {absD:N2} บาท)",
                    absD, pct,
                    delta > 0 ? "5505 - FX Loss" : "4901 - FX Gain");
        }

        return new(DeltaKind.Unexplained,
            $"ยอดต่าง {absD:N2} บาท (อธิบายไม่ได้)", absD, null, null);
    }

    /// <summary>True when the delta is explained by an expected deduction
    /// — confidence shouldn't drop because the difference is understood
    /// (a fee/WHT line is just an extra JE the user will create).</summary>
    public static bool IsExplained(DeltaKind kind)
        => kind == DeltaKind.None
        || kind == DeltaKind.Rounding
        || kind == DeltaKind.FixedFee
        || kind == DeltaKind.WithholdingTax
        || kind == DeltaKind.FxVariance;
}
