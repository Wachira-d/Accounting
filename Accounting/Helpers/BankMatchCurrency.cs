using System;

namespace Accounting.Helpers;

/// <summary>ผลการเทียบสกุลเงินของผู้สมัคร 1 ราย กับบรรทัดธนาคาร</summary>
public enum BankAmountComparability
{
    /// <summary>**เทียบไม่ได้** — ต้องเป็นค่าใน enum ไม่ใช่ `null`/0 เงียบ ๆ
    /// (`DECISION_DOCTRINE` §1 G3: "เงื่อนไขที่เป็นเท็จเพราะไม่มีข้อมูล ห้ามตกเป็นผ่าน")</summary>
    NotComparable = 0,
    /// <summary>สกุลเดียวกัน — ตัวเลขเทียบกันได้ตรง ๆ</summary>
    SameCurrency = 1,
    /// <summary>ต่างสกุล แต่แปลงเป็นสกุลของบัญชีธนาคารได้ด้วยอัตราที่บันทึกไว้บนใบ</summary>
    Converted = 2,
}

/// <summary>
/// **ตัวแปลงยอดของผู้สมัคร ให้อยู่ในสกุลของบัญชีธนาคาร — ตัวเดียวของทั้งระบบ**
/// (OWNER file · pure · `DECISION_AUDIT_2026-09-18.md` §3 D4-8 "FX")
///
/// ═══ ของเดิมพังตรงไหน ═══
/// ทุกเส้นจับคู่ (`BankService.AutoMatchAsync` · `GetMatchCandidatesAsync` ·
/// `ValidateMatchAmountAsync` · `BankFeedService` · `OpenBankingService`) เทียบ
/// `Payment.Amount` ซึ่งเป็น **ยอดในสกุลของเอกสาร** กับ `BankTransaction.Amount`
/// ซึ่งเป็น **ยอดในสกุลของบัญชีธนาคาร** ตรง ๆ ⇒
///
/// 1. **ใบสกุลต่างประเทศกระทบยอดไม่ได้เลย** — ใบ 1,000 USD ที่เงินเข้าบัญชี THB
///    35,000 บาท: ด่าน `ValidateMatchAmountAsync` เห็น 1,000 ≠ 35,000 แล้ว
///    **throw** ⇒ ผู้ใช้จับคู่ด้วยมือก็ไม่ได้ (บั๊กที่เจ็บที่สุดของชุดนี้)
/// 2. **จับคู่ผิดเพราะตัวเลขบังเอิญเท่ากัน** — ใบ 30,780 USD กับเงินเข้า
///    30,780 บาท ได้ "ยอดตรงเป๊ะ 60 คะแนน" ทั้งที่ต่างกัน 35 เท่า
///
/// ═══ กติกา ═══
/// เราถืออัตราเดียวคือ **"1 หน่วยสกุลเอกสาร = X สกุลฐานของบริษัท"**
/// (`Document.ExchangeRate` ตอนตั้งหนี้ / `Payment.ExchangeRate` ตอนชำระจริง)
/// ดังนั้นแปลงได้ **เฉพาะ** เมื่อบัญชีธนาคารอยู่ในสกุลฐาน. กรณีอื่น
/// (บัญชี USD กับใบ EUR · บัญชี USD กับใบ THB) เราไม่มีอัตราให้ใช้จริง ⇒
/// **ตอบว่าเทียบไม่ได้** ไม่ใช่เดาอัตรา 1:1 เงียบ ๆ
///
/// ทิศปลอดภัย (G5 — ความเสียหายมองเห็นและแก้ทัน) ต่างกันตามผู้เรียก:
///  - เส้น **อัตโนมัติ** (ประทับสถานะเอง) → เทียบไม่ได้ = **ข้ามผู้สมัครรายนั้น**
///  - เส้น **ที่มนุษย์เห็น** (ตัวเลือกบนจอ) → ยังโชว์ แต่ตัดคะแนนยอดทิ้ง +
///    ติดป้ายบอกเหตุผล (ถ้าซ่อน ผู้ใช้จะไม่มีทางไปต่อ)
///  - เส้น **ด่านตอนบันทึก** → เทียบไม่ได้ = ปฏิเสธพร้อมบอกว่าต้องกรอกอัตราที่ไหน
/// </summary>
public static class BankMatchCurrency
{
    public const string RuleSame = "BANK-FX-SAME";
    public const string RuleConverted = "BANK-FX-CONVERTED";
    public const string RuleNoRate = "BANK-FX-NO-RATE";
    public const string RuleCross = "BANK-FX-CROSS";
    public const string RuleUnknownCode = "BANK-FX-UNKNOWN-CODE";

    /// <param name="Kind">เทียบได้ไหม และเทียบแบบไหน</param>
    /// <param name="BankCurrencyAmount">ยอดของผู้สมัครในสกุลของบัญชีธนาคาร (0 เมื่อเทียบไม่ได้)</param>
    /// <param name="Rate">อัตราที่ใช้ (1 เมื่อสกุลเดียวกัน · 0 เมื่อเทียบไม่ได้)</param>
    /// <param name="RuleCode">รหัสกฎ — ลง audit ได้ตาม กฎเหล็ก #2 M</param>
    /// <param name="Reason">ข้อความไทยที่บอกผู้ใช้ว่า **ทำอะไรต่อได้**</param>
    public sealed record Result(
        BankAmountComparability Kind,
        decimal BankCurrencyAmount,
        decimal Rate,
        string RuleCode,
        string Reason)
    {
        public bool Comparable => Kind != BankAmountComparability.NotComparable;
    }

    /// <summary>ปรับรหัสสกุลเงินให้เทียบกันได้ — ว่าง/null คืนสตริงว่าง (= ไม่รู้)</summary>
    public static string Normalize(string? code)
        => string.IsNullOrWhiteSpace(code) ? string.Empty : code.Trim().ToUpperInvariant();

    /// <summary>
    /// แปลงยอดผู้สมัคร (ในสกุล <paramref name="candidateCurrency"/>) ให้เป็นสกุล
    /// <paramref name="bankCurrency"/>.
    /// </summary>
    /// <param name="bankCurrency">สกุลของบัญชีธนาคาร (`BankAccount.Currency`)</param>
    /// <param name="candidateCurrency">สกุลของเอกสาร/ผู้สมัคร (`Document.Currency`)</param>
    /// <param name="candidateAmount">ยอดในสกุลเอกสาร</param>
    /// <param name="settlementRate">อัตรา ณ วันชำระจริง (`Payment.ExchangeRate`) — ชนะ</param>
    /// <param name="documentRate">อัตรา ณ วันตั้งหนี้ (`Document.ExchangeRate`)</param>
    /// <param name="companyBaseCurrency">สกุลฐานของบริษัท (`Company.BaseCurrency`)</param>
    public static Result Convert(
        string? bankCurrency,
        string? candidateCurrency,
        decimal candidateAmount,
        decimal? settlementRate,
        decimal? documentRate,
        string? companyBaseCurrency)
    {
        var bank = Normalize(bankCurrency);
        var cand = Normalize(candidateCurrency);

        if (bank.Length == 0 || cand.Length == 0)
            return new Result(BankAmountComparability.NotComparable, 0m, 0m, RuleUnknownCode,
                "ไม่ทราบสกุลเงินของบัญชีธนาคารหรือของเอกสาร — "
                + "ตั้งค่าสกุลเงินที่หน้าบัญชีธนาคาร / หัวเอกสาร ก่อนจับคู่");

        if (bank == cand)
            return new Result(BankAmountComparability.SameCurrency, candidateAmount, 1m, RuleSame, "");

        var home = Normalize(companyBaseCurrency);
        if (home.Length == 0 || bank != home)
            return new Result(BankAmountComparability.NotComparable, 0m, 0m, RuleCross,
                $"บัญชีธนาคารเป็นสกุล {bank} แต่เอกสารเป็นสกุล {cand} — "
                + "ระบบเก็บอัตราแลกเปลี่ยนไว้เทียบกับสกุลฐานของบริษัทเท่านั้น "
                + "จึงแปลงยอดให้ไม่ได้. จับคู่ผ่านกลุ่มกระทบยอด M:N แล้วระบุยอดจัดสรรเอง "
                + "หรือบันทึกรายการเดินบัญชีในสกุลเดียวกับเอกสาร");

        var rate = settlementRate.HasValue && settlementRate.Value > 0m
            ? settlementRate.Value
            : (documentRate ?? 0m);
        if (rate <= 0m)
            return new Result(BankAmountComparability.NotComparable, 0m, 0m, RuleNoRate,
                $"เอกสารสกุล {cand} ยังไม่มีอัตราแลกเปลี่ยน — "
                + "กรอกอัตราแลกเปลี่ยนบนเอกสาร (หรือบนรายการชำระ ช่อง \"อัตรา ณ วันชำระ\") ก่อนจับคู่");

        var converted = Math.Round(candidateAmount * rate, 2, MidpointRounding.AwayFromZero);
        return new Result(BankAmountComparability.Converted, converted, rate, RuleConverted,
            $"แปลง {candidateAmount:N2} {cand} × {rate:N6} = {converted:N2} {bank}");
    }
}
