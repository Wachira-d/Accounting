namespace Accounting.Services.Implementations.Bank;

/// <summary>
/// Probability-based combiner for 1:1 match signals — replaces the old
/// hand-tuned ladder ("signal≥4 → 0.95, signal≥2 → 0.90, …") with a
/// noisy-OR aggregation so MULTIPLE independent signals corroborate
/// correctly. The ladder said "name match alone = 0.90" and "name+TIN
/// = also 0.90" which is wrong — two independent confirmations should
/// produce a STRONGER posterior than either alone.
///
/// Formula (noisy-OR / probabilistic-OR):
///   P_remaining_uncertainty = (1 - P_base) × ∏(1 - p_i)
///   P_combined = 1 − P_remaining_uncertainty
///
/// Each signal's p_i is "given this evidence alone holds + the amount is
/// exact + the candidate is in window, how often is the match TRUE?".
/// All values are educated estimates anchored on observed Thai-bank
/// data (e.g. a TIN match is essentially certain — TINs are unique;
/// a substring contact-name match is weaker because tokens collide).
/// Cap at 0.99 — we never claim absolute certainty without human review.
/// </summary>
public static class BankMatchScoring
{
    // P(true match | this evidence) — the SINGLE-SIGNAL posterior given
    // amount+window already hold. Higher = more reliable.
    public const double P_REF_CODE_CITED     = 0.95;  // memo cites the exact ref/doc code
    public const double P_DOC_NUMBER_CITED   = 0.93;
    public const double P_DOC_SUBSTRING      = 0.82;  // doc-no substring in memo
    public const double P_REF_SUBSTRING      = 0.55;  // ref substring in memo (weaker — could be coincidence)
    public const double P_CONTACT_NAME       = 0.78;  // contact name token in memo
    public const double P_TAX_ID             = 0.97;  // 13-digit Thai TIN — unique
    public const double P_ACCOUNT_TAIL       = 0.90;  // payer account-tail match
    public const double P_PHONE              = 0.93;  // PromptPay phone match
    public const double P_CHANNEL_COMPATIBLE = 0.55;  // payment method matches bank flow category

    // Base prior — "sole in-window candidate at this amount, no identity
    // signal". After the ambiguity guard this is itself a near-certain
    // 1:1, so the base is high; identity signals push it toward 0.99.
    public const decimal BASE_SOLE_CANDIDATE = 0.85m;
    public const decimal BASE_SAMEDAY        = 0.87m;   // same-day adds ~+2 bp

    /// <summary>Combine independent signals via noisy-OR. Bounded [0,0.99].</summary>
    public static decimal Combine(decimal basePrior, params double[] signalProbabilities)
        => Combine(basePrior, (IEnumerable<double>)signalProbabilities);

    public static decimal Combine(decimal basePrior, IEnumerable<double> signalProbabilities)
    {
        double remaining = 1.0 - (double)basePrior;
        foreach (var sp in signalProbabilities)
        {
            if (sp <= 0 || sp >= 1) continue;
            remaining *= (1.0 - sp);
        }
        var p = 1.0 - remaining;
        if (p < 0) p = 0;
        if (p > 0.99) p = 0.99;
        return (decimal)p;
    }

    /// <summary>Apply a multiplicative DOWNWARD adjustment when a signal
    /// CONTRADICTS the match (e.g. memo cites a different contact's account
    /// tail). penalty 0..1 — 0.3 means "30% of remaining confidence is
    /// pulled toward zero". Bounded [0.05, 0.99].</summary>
    public static decimal Penalise(decimal confidence, double penalty)
    {
        if (penalty <= 0) return confidence;
        if (penalty > 1) penalty = 1;
        var adj = (double)confidence * (1.0 - penalty);
        if (adj < 0.05) adj = 0.05;
        return (decimal)adj;
    }
}
