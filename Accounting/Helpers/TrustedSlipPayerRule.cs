using System.Security.Cryptography;
using System.Text;

namespace Accounting.Helpers;

/// <summary>
/// **ผู้จ่ายที่ไว้ใจได้ (trusted payer) — ตัวตัดสินเดียว** (รอบ 286 · คำสั่งเจ้าของ + รั้วที่ทีมเพิ่ม · คำตัดสิน 286-1…286-9 · pure · ไม่มี AI/fuzzy ⇒ kill-switch ปลอดภัยโดยโครงสร้าง)
/// <para>เจ้าของ (2026-11-21): "ถ้าสลิปไหนเคยยืนยัน และจับผู้ส่ง และ เลขบัญชีได้ ควรเรียนรู้ที่จะอนุมัติอัตโนมัติครั้งถัดไปได้ ถ้าสลิปเหมือนเดิม ต่างแค่วันที่กับ transaction id"</para>
/// <list type="bullet">
/// <item><b>กุญแจ</b> (<see cref="PayerKey"/>) = บริษัทผู้ขาย + ผู้ติดต่อ (ผู้ซื้อ) + ธนาคารผู้โอน + เลขบัญชีผู้โอนตามที่สลิปแสดง (หลักที่เห็น ≥ 4) + ชื่อผู้โอนตามที่สลิปแสดง
/// (ปรับช่องว่าง/ตัวพิมพ์เท่านั้น — ไม่ fuzzy) + บัญชีผู้รับของเรา · ตัดสินด้วยกุญแจที่อ่านจากสลิป <b>ไม่ใช่ความคล้ายของรูป</b> ·
/// เก็บเป็น HMAC (<see cref="Fingerprint"/>) — <b>ไม่เก็บชื่อผู้โอน</b> (264-2-7 · PDPA)</item>
/// <item><b>เกิดความไว้ใจ</b> เฉพาะเมื่อสลิปของกุญแจนี้ถูกยืนยันด้วยรายการเดินบัญชีจริง (ตัดชำระจากรายการเดินบัญชี หรือผู้ขายกดยืนยันแล้วรายการเดินบัญชีตามมาตรง) —
/// การกดยืนยันอย่างเดียวไม่พอ (ผู้เรียก <c>TrustedSlipVerifyService.LearnAsync</c>)</item>
/// <item><b>ใช้ความไว้ใจ</b> (<see cref="Decide"/>) ⇒ บันทึกรับเงิน<b>ล่วงหน้า</b> (provisional) แล้วต้องตรวจย้อนกับรายการเดินบัญชีภายใน <see cref="ReviewDueUtc"/> —
/// ไม่พบ ⇒ แจ้งแรง + ถอนความไว้ใจของกุญแจอัตโนมัติ + ทางเลือก "ยกเลิกการรับเงินนี้" (ระบบไม่ยกเลิกเอง)</item>
/// </list>
/// </summary>
public static class TrustedSlipPayerRule
{
    public const string RuleCode = "SLIP-TRUSTED-PAYER";
    public const string LegalReference = "คำสั่งเจ้าของ 286 · พ.ร.บ.การบัญชี 2543 ม.8 (บันทึกตามเอกสารประกอบ) · ป.รัษฎากร §78/1 (วันรับเงิน) · พ.ร.บ.คุ้มครองข้อมูลส่วนบุคคล ม.22/37 (เก็บเฉพาะลายนิ้วมือของชื่อ)";

    /// <summary>ยอดต่อครั้งสูงสุด = ยอดสูงสุดที่เคยยืนยันด้วยรายการเดินบัญชีของกุญแจนั้น × ค่านี้ (รั้ว 286-3)</summary>
    public const decimal GrowthFactor = 1.5m;
    /// <summary>หลักที่เห็นขั้นต่ำของเลขบัญชีผู้โอน (ตัวเดียวกับฝั่งผู้ซื้อ <see cref="SlipMoneySource.PayerAutoMinDigits"/>)</summary>
    public static int MinVisibleDigits => SlipMoneySource.PayerAutoMinDigits;
    public const int DefaultReviewBusinessDays = 3;
    public const decimal DefaultDailyCapPerKey = 50_000m;
    /// <summary>ฝ่ายค้าน HIGH-3: รอรายการเดินบัญชีได้นานสุด 5 วันทำการ (ช่วงที่เงินปลอมค้างในระบบ)</summary>
    public const int MaxReviewBusinessDays = 5;
    /// <summary>ฝ่ายค้าน HIGH-3: เพดานยอดล่วงหน้าที่ยังไม่ยืนยันรวมทั้งบริษัท (ตั้งต้น) — ความเสียหายสูงสุด ณ เวลาใดเวลาหนึ่งตามค่าตั้งต้น</summary>
    public const decimal DefaultCompanyOutstandingCap = 100_000m;
    /// <summary>ฝ่ายค้าน HIGH-3: เพดานยอดล่วงหน้าที่ยังไม่ยืนยันรวมต่อผู้ติดต่อ = ยอดสูงสุดที่เคยยืนยันจริงของผู้ติดต่อนั้น × ค่านี้</summary>
    public const decimal ContactOutstandingFactor = 1m;

    public static class Codes
    {
        /// <summary>บันทึกรับเงินล่วงหน้าแล้ว (รอรายการเดินบัญชียืนยัน)</summary>
        public const string Provisional = "TrustedProvisional";
        public const string SettingOff = "TrustSettingOff";
        public const string NoKey = "TrustNoKey";
        public const string NotTrusted = "TrustNotTrusted";
        public const string Revoked = "TrustRevoked";
        public const string OverMax = "TrustOverMax";
        public const string OverDaily = "TrustOverDaily";
        public const string NoCap = "TrustNoCap";
        /// <summary>ฝ่ายค้าน HIGH-3: ยอดล่วงหน้าที่ยังไม่ยืนยันรวม (ต่อผู้ติดต่อ/ทั้งบริษัท) จะเกินเพดาน</summary>
        public const string OverOutstanding = "TrustOverOutstanding";
        /// <summary>ฝ่ายค้าน HIGH-3: ผู้ติดต่อถูกล็อกหลังรายการล่วงหน้าไม่พบเงินเข้า (ผู้ขายปลดเอง)</summary>
        public const string ContactBlocked = "TrustContactBlocked";
        /// <summary>ครบกำหนดตรวจแล้วไม่พบรายการเดินบัญชี (หรือผู้ขายยกเลิกการรับเงิน) — ความไว้ใจของกุญแจถูกถอน</summary>
        public const string Unverified = "TrustUnverified";
    }

    public const string ProvisionalLabel = "บันทึกรับเงินล่วงหน้า (ผู้จ่ายที่ไว้ใจได้) — รอรายการเดินบัญชียืนยัน";

    /// <param name="AccountMask">เลขบัญชีผู้โอนตามรูปบนสลิป (ปรับรูปแล้ว ตัวปิด = x) — เทียบตรงตัว</param>
    /// <param name="NameNormalized">ชื่อผู้โอนหลัง <see cref="NormalizeName"/> (อยู่ในหน่วยความจำเท่านั้น)</param>
    public sealed record PayerKey(Guid ContactId, string BankCode, string AccountMask, string NameNormalized, Guid ReceivingBankAccountId)
    {
        /// <summary>ป้ายที่ผู้ขายเห็น (ไม่มีชื่อ): "กสิกรไทย xxx-x-x1234-x"</summary>
        public string Label => (ThaiBankCodes.ByCode(BankCode)?.ThaiName ?? "ธนาคารรหัส " + BankCode) + " " + AccountMask;
    }

    /// <summary>ชื่อผู้โอนตามที่สลิปแสดง ⇒ รูปเทียบ: ตัด zero-width · ช่องว่างหลายตัวเป็นตัวเดียว · ตัวพิมพ์ใหญ่ · <b>ไม่ตัดคำนำหน้า ไม่ fuzzy</b> ·
    /// ว่าง ⇒ null</summary>
    public static string? NormalizeName(string? name)
    {
        if (string.IsNullOrWhiteSpace(name)) return null;
        var sb = new StringBuilder(name.Length);
        var space = false;
        foreach (var ch in name)
        {
            if (ch is '​' or '‌' or '‍' or '﻿') continue;
            if (char.IsWhiteSpace(ch)) { space = sb.Length > 0; continue; }
            if (space) { sb.Append(' '); space = false; }
            sb.Append(char.ToUpperInvariant(ch));
        }
        return sb.Length == 0 ? null : sb.ToString();
    }

    /// <summary>กุญแจจากผลอ่านสลิป · ขาดชิ้นใด (ไม่ใช่บัญชีธนาคาร · ไม่รู้ธนาคาร · เห็นเลขน้อยกว่า <see cref="MinVisibleDigits"/> หลัก · ไม่มีชื่อ · ไม่รู้ผู้ติดต่อ/บัญชีผู้รับ) ⇒ null</summary>
    public static PayerKey? KeyFrom(Guid? contactId, OcrSlipText.SlipParty? payer, Guid? receivingBankAccountId)
    {
        if (contactId is not Guid contact || contact == Guid.Empty || receivingBankAccountId is not Guid recv || recv == Guid.Empty || payer == null) return null;
        if (payer.Kind != OcrSlipText.PartyKind.BankAccount) return null;
        if (payer.BankCode is not { Length: 3 } bank || !bank.All(char.IsAsciiDigit)) return null;
        if (string.IsNullOrWhiteSpace(payer.AccountMask) || payer.VisibleDigits.Count < MinVisibleDigits) return null;
        var name = NormalizeName(payer.Name);
        if (name == null) return null;
        return new PayerKey(contact, bank, payer.AccountMask.Trim().ToLowerInvariant(), name, recv);
    }

    /// <summary>กุญแจ HMAC อนุพันธ์จากความลับของเซิร์ฟเวอร์ (Jwt:Secret · fail-fast ใน production ที่ Program.cs) · ว่าง ⇒ โยน (กฎ #4 C ห้ามถอยไปกุญแจ dev เงียบ ๆ)</summary>
    public static byte[] DeriveHmacKey(string? serverSecret)
    {
        if (string.IsNullOrWhiteSpace(serverSecret))
            throw new InvalidOperationException("ไม่ได้ตั้งความลับของเซิร์ฟเวอร์ (Jwt:Secret) — คำนวณกุญแจผู้จ่ายที่ไว้ใจได้ไม่ได้");
        return HMACSHA256.HashData(Encoding.UTF8.GetBytes(serverSecret), Encoding.UTF8.GetBytes("trusted-slip-payer-v1"));
    }

    /// <summary>ลายนิ้วมือของกุญแจ (canonical function เดียว — ฝั่งเขียนและฝั่งค้นเรียกที่นี่ · ผูกบริษัท ⇒ เทียบข้ามบริษัทไม่ได้) ·
    /// วันที่/เวลา/เลขอ้างอิง/ยอด <b>ไม่อยู่ในกุญแจ</b> (คำสั่งเจ้าของ: ต่างได้แค่นั้น)</summary>
    public static string Fingerprint(byte[] key, Guid companyId, PayerKey k)
        => Convert.ToHexString(HMACSHA256.HashData(key, Encoding.UTF8.GetBytes(
            $"trusted-payer-v1|{companyId:N}|{k.ContactId:N}|{k.BankCode}|{k.AccountMask}|{k.NameNormalized}|{k.ReceivingBankAccountId:N}"))).ToLowerInvariant();

    /// <summary>ยอดต่อครั้งสูงสุดของกุญแจ (ปัดสตางค์ AwayFromZero)</summary>
    public static decimal PerSlipCeiling(decimal maxConfirmedAmount)
        => Math.Round(maxConfirmedAmount * GrowthFactor, 2, MidpointRounding.AwayFromZero);

    /// <summary>ความไว้ใจของกุญแจ ณ ตอนนี้ · null = ไม่เคยยืนยันด้วยรายการเดินบัญชี</summary>
    public sealed record Trust(bool Revoked, decimal MaxConfirmedAmount);

    /// <param name="SettingOn">ค่าตั้ง <c>CompanySettings.SlipTrustedAutoEnabled</c> (ค่าตั้งต้นปิด) <b>และ</b> <c>SlipAutoRecordEnabled</c> ของ 282</param>
    /// <param name="SlipReady">ด่านอื่นของ 282 ผ่านครบ เหลือแต่ "ไม่มีรายการเดินบัญชี" (<c>SlipAutoRecordRule.Codes.NoBankLine</c>)</param>
    /// <param name="TodayForKey">ยอดที่บันทึกล่วงหน้าแล้ววันนี้ (วันไทย) ของกุญแจนี้ (ไม่นับที่ถูกถอน/ล้ม)</param>
    /// <param name="DailyCapPerKey">เพดานต่อวันต่อกุญแจที่บริษัทตั้ง · ≤ 0 = ยังไม่ตั้ง ⇒ ไม่ใช้ความไว้ใจ</param>
    /// <param name="ContactBlocked">ผู้ติดต่อถูกล็อกหลังรายการล่วงหน้าไม่พบเงินเข้า</param>
    /// <param name="ContactOutstanding">ยอดล่วงหน้าที่ยังไม่ยืนยันของผู้ติดต่อ (ไม่นับรายการนี้)</param>
    /// <param name="ContactMaxConfirmed">ยอดสูงสุดที่เคยยืนยันจริงในกุญแจที่ไม่ถูกถอนของผู้ติดต่อนี้</param>
    /// <param name="CompanyOutstanding">ยอดล่วงหน้าที่ยังไม่ยืนยันของทั้งบริษัท (ไม่นับรายการนี้)</param>
    /// <param name="CompanyOutstandingCap">ค่าตั้งของบริษัท · ≤ 0 = ไม่ใช้ความไว้ใจ</param>
    public sealed record Facts(bool SettingOn, bool SlipReady, PayerKey? Key, Trust? Trust, decimal Amount, decimal TodayForKey, decimal DailyCapPerKey,
        bool ContactBlocked = false, decimal ContactOutstanding = 0m, decimal ContactMaxConfirmed = decimal.MaxValue, decimal CompanyOutstanding = 0m,
        decimal CompanyOutstandingCap = DefaultCompanyOutstandingCap);

    public sealed record Verdict(bool Provisional, string Code, string Reason)
    {
        public bool UsedAi => false;
    }

    public static Verdict Decide(Facts f)
    {
        if (!f.SettingOn) return No(Codes.SettingOff, "บริษัทยังไม่เปิด “อนุมัติอัตโนมัติจากผู้จ่ายที่ไว้ใจได้”");
        if (!f.SlipReady) return No(Codes.NoKey, "สลิปยังไม่ผ่านด่านพื้นฐาน (ยอด/QR/วันที่/บัญชีผู้รับ) — ไม่ใช้ความไว้ใจ");
        if (f.Key == null) return No(Codes.NoKey, $"สลิปไม่แสดงผู้โอนครบ (ธนาคาร · เลขบัญชีอย่างน้อย {MinVisibleDigits} หลัก · ชื่อ) — เทียบกับผู้จ่ายที่ไว้ใจได้ไม่ได้");
        if (f.ContactBlocked) return No(Codes.ContactBlocked, "ผู้ติดต่อนี้ถูกล็อกความไว้ใจ (รายการรับเงินล่วงหน้าก่อนหน้าไม่พบเงินเข้า) — ตรวจแล้วกดยืนยันเอง · ปลดล็อกได้ที่หน้าตั้งค่า LINE");
        if (f.Trust == null) return No(Codes.NotTrusted, "ผู้โอนนี้ (ธนาคาร/เลขบัญชี/ชื่อ/บัญชีผู้รับ) ยังไม่เคยถูกยืนยันด้วยรายการเดินบัญชี — ยังไม่ใช่ผู้จ่ายที่ไว้ใจได้");
        if (f.Trust.Revoked) return No(Codes.Revoked, "ความไว้ใจของผู้โอนนี้ถูกถอนแล้ว — ตรวจแล้วกดยืนยันเอง (ให้ความไว้ใจอีกครั้งได้ที่หน้าตั้งค่า LINE)");
        if (f.DailyCapPerKey <= 0m || f.CompanyOutstandingCap <= 0m) return No(Codes.NoCap, "ยังไม่ได้ตั้งเพดานต่อวันของผู้จ่ายที่ไว้ใจได้ — ระบบไม่บันทึกล่วงหน้า");
        var ceiling = PerSlipCeiling(f.Trust.MaxConfirmedAmount);
        if (f.Amount > ceiling)
            return No(Codes.OverMax, $"ยอด {f.Amount:N2} บาท เกินเพดานต่อครั้งของผู้โอนนี้ ({ceiling:N2} = ยอดสูงสุดที่เคยยืนยัน {f.Trust.MaxConfirmedAmount:N2} × {GrowthFactor}) — ตรวจแล้วกดยืนยันเอง");
        if (f.TodayForKey + f.Amount > f.DailyCapPerKey)
            return No(Codes.OverDaily, $"ยอดรวมวันนี้ของผู้โอนนี้จะเกินเพดานต่อวัน {f.DailyCapPerKey:N2} บาท — ตรวจแล้วกดยืนยันเอง");
        var contactCap = f.ContactMaxConfirmed == decimal.MaxValue ? decimal.MaxValue
            : Math.Round(f.ContactMaxConfirmed * ContactOutstandingFactor, 2, MidpointRounding.AwayFromZero);
        if (f.ContactOutstanding + f.Amount > contactCap)
            return No(Codes.OverOutstanding, $"ยอดรับเงินล่วงหน้าที่ยังรอยืนยันของผู้ติดต่อนี้จะเกิน {contactCap:N2} บาท (ยอดสูงสุดที่เคยยืนยันจริง) — ตรวจแล้วกดยืนยันเอง");
        if (f.CompanyOutstanding + f.Amount > f.CompanyOutstandingCap)
            return No(Codes.OverOutstanding, $"ยอดรับเงินล่วงหน้าที่ยังรอยืนยันทั้งบริษัทจะเกินเพดาน {f.CompanyOutstandingCap:N2} บาท — ตรวจแล้วกดยืนยันเอง");
        return new Verdict(true, Codes.Provisional, $"ผู้จ่ายที่ไว้ใจได้ ({f.Key.Label}) · ยอดไม่เกิน {ceiling:N2} · " + ProvisionalLabel);
    }

    private static Verdict No(string code, string reason) => new(false, code, reason);

    /// <summary>กำหนดตรวจย้อนกับรายการเดินบัญชี: สิ้นวันทำการที่ N หลังวันที่บนสลิป (วันไทย · เสาร์/อาทิตย์ + วันหยุดราชการของแพลตฟอร์ม) ⇒ เวลา UTC</summary>
    public static DateTime ReviewDueUtc(DateTime slipDayTh, int businessDays, IReadOnlySet<DateTime>? holidays)
    {
        var n = Math.Clamp(businessDays, 1, MaxReviewBusinessDays);
        var dueDayTh = BusinessDayCalendar.AddBusinessDays(slipDayTh.Date, n, holidays);
        return dueDayTh.AddDays(1).AddHours(-7);   // สิ้นวันไทย = 00:00 วันถัดไป (ไทย) = 17:00 UTC
    }

    /// <summary>ค่าตั้งที่หน้าตั้งค่าส่งมา ⇒ ปัญหา (ข้อความไทย) · null = ใช้ได้</summary>
    public static string? SettingsProblem(decimal? dailyCap, int? reviewDays, decimal? companyCap = null)
    {
        if (companyCap is decimal cc && (cc < 0m || cc > 100_000_000m)) return "เพดานยอดล่วงหน้าที่รอยืนยันรวมทั้งบริษัทต้องอยู่ระหว่าง 0 – 100,000,000 บาท";
        if (dailyCap is decimal c && (c < 0m || c > 100_000_000m)) return "เพดานต่อวันของผู้จ่ายที่ไว้ใจได้ต้องอยู่ระหว่าง 0 – 100,000,000 บาท";
        if (reviewDays is int d && (d < 1 || d > MaxReviewBusinessDays)) return $"จำนวนวันทำการที่รอรายการเดินบัญชีต้องอยู่ระหว่าง 1 – {MaxReviewBusinessDays}";
        return null;
    }

    /// <summary>ป้ายสถานะของรายการรับเงินล่วงหน้า (server computes · หน้ารายการรับชำระ/เอกสาร) · null = ไม่ใช่รายการล่วงหน้า</summary>
    public static string? StatusLabel(DateTime? provisionalAt, DateTime? verifiedAt, DateTime? failedAt)
        => provisionalAt == null ? null
            : verifiedAt != null ? "รับล่วงหน้า — ธนาคารยืนยันแล้ว"
            : failedAt != null ? "รับล่วงหน้า — ไม่พบเงินเข้าตามกำหนด (ตรวจ/ยกเลิกการรับเงิน)"
            : "รับล่วงหน้า — รอยืนยันจากธนาคาร";

    /// <summary>ล่วงหน้าที่ยังไม่ยืนยัน (ตัวตั้งเดียวของตัวกรอง "ล่วงหน้าที่ยังไม่ยืนยัน" + การกักการส่งเอกสารถึงผู้ซื้อ)</summary>
    public static bool Unverified(DateTime? provisionalAt, DateTime? verifiedAt) => provisionalAt != null && verifiedAt == null;

    public const string DeliveryHeldNote = "ออกใบเสร็จ/ใบกำกับในระบบแล้ว (ภ.พ.30) — ยังไม่ส่งถึงผู้ซื้อจนกว่ารายการเดินบัญชียืนยัน (รับเงินล่วงหน้า)";

    /// <summary>ประโยคความเสี่ยงบนหน้าตั้งค่า (server computes · ภาษาคน)</summary>
    public const string RiskNotice =
        "ความเสี่ยง: ผู้ที่เคยโอนจริงหนึ่งครั้งอาจแต่งรูปสลิปครั้งถัดไปให้เหมือนเดิมได้ (QR ไม่มียอดและเลขบัญชี) — ระบบจึงบันทึกรับเงินแบบ \"ล่วงหน้า\" เท่านั้น " +
        "ยอดที่รอยืนยันรวมต่อผู้ติดต่อไม่เกินยอดสูงสุดที่เคยยืนยันจริง และรวมทั้งบริษัทไม่เกินเพดานที่ตั้ง (= ความเสียหายสูงสุด ณ เวลาใดเวลาหนึ่ง) แล้วตรวจย้อนกับรายการเดินบัญชี " +
        "(ความไว้ใจเกิดเฉพาะเมื่อเลขอ้างอิงบนรายการเดินบัญชีตรงสลิป) — ไม่พบในเวลาที่ตั้ง (สูงสุด 5 วันทำการ) ⇒ แจ้งเตือนแรง ถอนความไว้ใจทุกผู้โอนของผู้ติดต่อนั้นและล็อกจนคุณปลดเอง " +
        "และให้คุณเลือกยกเลิกการรับเงิน · ใบเสร็จ/ใบกำกับออกในระบบทันที (ภ.พ.30) แต่ไม่ส่งถึงผู้ซื้อจนกว่าธนาคารยืนยัน · ต้องนำเข้ารายการเดินบัญชีสม่ำเสมอ";
}
