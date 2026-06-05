namespace Accounting.Services.Implementations.Bank;

/// <summary>
/// SINGLE source of truth for classifying a Thai bank-statement line by its
/// memo and deriving the realistic candidate date window. Shared by every
/// matcher (the bulk AI sweep, the basic AutoMatch button, …) so the window /
/// flow rules can never drift apart between code paths.
/// </summary>
public enum BankFlowCategory
{
    Aggregator,    // KSHOP / Thai QR / wallets / marketplaces — daily M:1, [T−1..T]
    Transfer,      // person-to-person via PromptPay / Internet / Mobile — 1:1, [T−1..T+1]
    AutoCredit,    // SMART / ATS / "อัตโนมัติ" — 1:1 or scheduled, [T−2..T+1]
    Cheque,        // เช็ค / B/C / bill collection — 1:1 with clearing delay, [T−7..T+1]
    CounterCash,   // ฝากเงินสด / Counter — 1:1 may be late, [T−2..T+1]
    InwardTT,      // SWIFT / Inward TT / Remittance — 1:1 with FX variance, [T−7..T+2]
    BillPayment,   // ลูกค้าจ่ายผ่านเคาน์เตอร์/ชำระบิล — 1:1 with ref, [T−3..T+1]
    Interest,      // ดอกเบี้ย / Interest earned — 1:1 to interest JE, wide forward
    Refund,        // Refund / กลับรายการ — 1:1 reversal, wide backward
    Loan,          // Loan disbursement / เบิกสินเชื่อ / OD — 1:1, [T−2..T+2]
    CardSettle,    // Card / Visa/MC settlement — daily M:1, [T−2..T]
    TaxRefund,     // คืนภาษี / RD refund — 1:1 wide forward
    Other,         // unclassified — conservative 1:1, [T−3..T+2]
}

public static class BankFlowClassifier
{
    /// <summary>Classify a bank line by memo. Case-insensitive; order matters
    /// (more specific patterns first).</summary>
    public static BankFlowCategory Classify(string? memo, string? payee = null, string? reference = null)
    {
        var s = ((memo ?? "") + " | " + (payee ?? "") + " | " + (reference ?? "")).ToLowerInvariant();
        if (string.IsNullOrWhiteSpace(s)) return BankFlowCategory.Other;

        if (s.Contains("thai qr") || s.Contains("kshop") || s.Contains("k shop")
            || s.Contains("kbank shop") || s.Contains("k-plus shop") || s.Contains("myqr")
            || s.Contains("my qr") || s.Contains("edc") || s.Contains("truemoney")
            || s.Contains("true money") || s.Contains("shopeepay") || s.Contains("shopee pay")
            || s.Contains("grabpay") || s.Contains("grab pay") || s.Contains("lineman")
            || s.Contains("shopee") || s.Contains("lazada") || s.Contains("nextpay")
            || s.Contains("omisego") || s.Contains("stripe") || s.Contains("square")
            || s.Contains("รับเงินจากการขายด้วย"))
            return BankFlowCategory.Aggregator;

        if (s.Contains("visa") || s.Contains("master") || s.Contains("mc settle")
            || s.Contains("card settle") || s.Contains("card net") || s.Contains("merchant settle")
            || s.Contains("posnet"))
            return BankFlowCategory.CardSettle;

        if (s.Contains("smart") || s.Contains(" ats ") || s.Contains("รับโอนเงินอัตโนมัติ")
            || s.Contains("โอนเข้าอัตโนมัติ") || s.Contains("หักบัญชีอัตโนมัติ")
            || s.Contains("scheduled transfer") || s.Contains("direct credit"))
            return BankFlowCategory.AutoCredit;

        if (s.Contains("เช็ค") || s.Contains("cheque") || s.Contains("เรียกเก็บ")
            || s.Contains("bill collection") || s.Contains(" b/c ") || s.Contains("clearing"))
            return BankFlowCategory.Cheque;

        if (s.Contains("ฝากเงินสด") || s.Contains("นำฝาก") || s.Contains("counter")
            || s.Contains("cash deposit") || s.Contains("เคาน์เตอร์"))
            return BankFlowCategory.CounterCash;

        if (s.Contains("inward tt") || s.Contains("inward t/t") || s.Contains("swift")
            || s.Contains("remittance") || s.Contains("inward remit")
            || s.Contains("โอนเข้าจากต่างประเทศ"))
            return BankFlowCategory.InwardTT;

        if (s.Contains("bill payment") || s.Contains("bill pay") || s.Contains("ชำระบิล")
            || s.Contains("รับชำระบิล") || s.Contains("cross-bank bill") || s.Contains("counter pay"))
            return BankFlowCategory.BillPayment;

        if (s.Contains("ดอกเบี้ย") || s.Contains("interest") || s.Contains("int earned"))
            return BankFlowCategory.Interest;

        if (s.Contains("คืนภาษี") || s.Contains("tax refund") || s.Contains("rd refund")
            || s.Contains("กรมสรรพากร"))
            return BankFlowCategory.TaxRefund;

        if (s.Contains("refund") || s.Contains("คืนเงิน") || s.Contains("reverse")
            || s.Contains("กลับรายการ") || s.Contains("rejection"))
            return BankFlowCategory.Refund;

        if (s.Contains("loan disburs") || s.Contains("เบิกสินเชื่อ") || s.Contains(" l/d ")
            || s.Contains(" o/d ") || s.Contains("overdraft"))
            return BankFlowCategory.Loan;

        if (s.Contains("รับโอนเงิน") || s.Contains("internet") || s.Contains("mobile")
            || s.Contains("k plus") || s.Contains("k-plus") || s.Contains("promptpay")
            || s.Contains("พร้อมเพย์") || s.Contains("โอนเงิน"))
            return BankFlowCategory.Transfer;

        return BankFlowCategory.Other;
    }

    /// <summary>Realistic candidate date window per flow category, DIRECTIONAL.
    /// Returns (backDays, fwdDays) relative to the bank transaction date: a
    /// candidate is eligible when  bank.date − Back ≤ candidate.date ≤
    /// bank.date + Fwd. KEY INSIGHT: the accounting document (RV / receipt /
    /// cheque) is created AT or BEFORE the money lands, so the window looks
    /// mostly BACKWARD — e.g. a KSHOP deposit on 2 Apr matches RVs dated 1 Apr
    /// (after cut-off) + 2 Apr, NEVER 3 Apr.</summary>
    public static (int Back, int Fwd) Window(string? memo, string? payee = null, string? reference = null)
        => Classify(memo, payee, reference) switch
        {
            BankFlowCategory.Aggregator  => (1, 0),
            BankFlowCategory.CardSettle  => (2, 0),
            BankFlowCategory.Transfer    => (1, 1),
            BankFlowCategory.AutoCredit  => (2, 1),
            BankFlowCategory.BillPayment => (3, 1),
            BankFlowCategory.Loan        => (2, 2),
            BankFlowCategory.CounterCash => (2, 1),
            BankFlowCategory.Cheque      => (7, 1),
            BankFlowCategory.InwardTT    => (7, 2),
            BankFlowCategory.Interest    => (2, 31),
            BankFlowCategory.Refund      => (60, 2),
            BankFlowCategory.TaxRefund   => (2, 90),
            _                            => (3, 2),
        };

    /// <summary>True when candidate.date falls inside the directional window.</summary>
    public static bool InWindow(DateTime candidateDate, DateTime bankDate, (int Back, int Fwd) w)
    {
        var gap = (candidateDate.Date - bankDate.Date).TotalDays;   // <0 = candidate before bank
        return gap >= -w.Back && gap <= w.Fwd;
    }

    /// <summary>True when this category aggregates many same-day items into one
    /// bank line (M:1 expected). Other categories prefer 1:1.</summary>
    public static bool IsAggregatorFlow(BankFlowCategory c)
        => c == BankFlowCategory.Aggregator || c == BankFlowCategory.CardSettle;

    public static bool IsAggregatorMemo(string? memo, string? payee = null, string? reference = null)
        => IsAggregatorFlow(Classify(memo, payee, reference));
}
