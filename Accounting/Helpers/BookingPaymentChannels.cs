namespace Accounting.Helpers;

/// <summary>ข้อมูลโอนเงินของเว็บ (แถว <c>SitePaymentGateway</c> ตัวแรกที่ใช้งาน — ตารางเดียวกับคำสั่งซื้อ · ไม่มีสำเนา)</summary>
public sealed record BookingTransferInfo(
    string? BankName,
    string? AccountNumber,
    string? AccountName,
    string? Branch,
    string? PromptPayId,
    string? QrImageUrl);

/// <summary>
/// รอบ 218 (คำตัดสินข้อ 177) — <b>ตัวตัดสินเดียว</b>ของ "ช่องทางชำระล่วงหน้าของการจองพร้อมไหม" + ด่านรูปแบบเลขบัญชี ·
/// gateway = เว็บเปิด (<c>SiteBookingSettings.OnlineDepositEnabled</c>) + บริษัทมี <c>PaymentProviderConfig</c> ที่ใช้งาน ·
/// โอน/QR/สลิป = เว็บเปิด (<c>BankTransferEnabled</c>) + มีเลขบัญชี หรือ พร้อมเพย์ที่ถูกรูปแบบ หรือ รูป QR
/// </summary>
public static class BookingPaymentChannels
{
    public const string RuleCode = "CMS-BOOKING-PAYMENT-CHANNEL";
    public const string ClearConfirmRuleCode = "CMS-BOOKING-PAYMENT-CLEAR-CONFIRM";

    /// <summary>ฝ่ายค้านรอบ 218 (B1): ช่องที่มีค่าอยู่แล้วแต่คำขอส่งมาว่าง (= จะถูกล้าง) — ใช้ร่วมกับคำสั่งซื้อ ⇒ ล้างต้องยืนยันชัด ๆ (ไม่ล้างเงียบ)</summary>
    public static List<string> ClearedFields(BookingTransferInfo? before, string? bankName, string? accountNumber, string? accountName, string? branch,
        string? promptPayId, string? qrUrl)
    {
        var list = new List<string>();
        if (before == null) return list;
        void Chk(string? had, string? now, string label) { if (!string.IsNullOrWhiteSpace(had) && string.IsNullOrWhiteSpace(now)) list.Add(label); }
        Chk(before.BankName, bankName, "ธนาคาร");
        Chk(before.AccountNumber, accountNumber, "เลขบัญชี");
        Chk(before.AccountName, accountName, "ชื่อบัญชี");
        Chk(before.Branch, branch, "สาขา");
        Chk(before.PromptPayId, promptPayId, "พร้อมเพย์");
        Chk(before.QrImageUrl, qrUrl, "รูป QR");
        return list;
    }

    public static string ClearConfirmMessage(IReadOnlyCollection<string> fields)
        => $"จะล้างช่อง {string.Join(" · ", fields)} ของบัญชีรับโอน (ใช้ร่วมกับหน้าคำสั่งซื้อด้วย) — ยืนยันการล้างก่อนบันทึก";

    public static bool GatewayReady(bool siteOnlineEnabled, bool companyGatewayActive) => siteOnlineEnabled && companyGatewayActive;

    public static bool TransferReady(bool siteTransferEnabled, BookingTransferInfo? t, bool promptPayIdValid)
        => siteTransferEnabled && t != null
           && (!string.IsNullOrWhiteSpace(t.AccountNumber) || promptPayIdValid || !string.IsNullOrWhiteSpace(t.QrImageUrl));

    /// <summary>เลขบัญชีเหลือเฉพาะตัวเลข (ตัดขีด/ช่องว่าง) · ว่าง = null</summary>
    public static string? NormalizeAccountNumber(string? raw)
    {
        if (string.IsNullOrWhiteSpace(raw)) return null;
        var digits = new string(raw.Where(char.IsDigit).ToArray());
        return digits.Length == 0 ? null : digits;
    }

    /// <summary>ตรวจข้อมูลบัญชีรับโอน — คืนรายการข้อความไทย (ว่าง = ผ่าน) · ไม่กรอกเลขบัญชีเลย = ผ่าน (ใช้พร้อมเพย์/QR อย่างเดียวได้)</summary>
    public static List<string> ValidateBankAccount(string? bankName, string? accountNumber, string? accountName, string? branch)
    {
        var errs = new List<string>();
        if (string.IsNullOrWhiteSpace(accountNumber)) return errs;
        if (accountNumber.Any(c => !char.IsDigit(c) && c != '-' && c != ' '))
            errs.Add("เลขบัญชีต้องเป็นตัวเลข (ขีด/ช่องว่างได้)");
        var digits = NormalizeAccountNumber(accountNumber) ?? "";
        if (digits.Length is < 10 or > 15) errs.Add("เลขบัญชีต้องมี 10–15 หลัก");
        if (string.IsNullOrWhiteSpace(bankName)) errs.Add("กรุณาระบุชื่อธนาคาร");
        if (string.IsNullOrWhiteSpace(accountName)) errs.Add("กรุณาระบุชื่อบัญชี");
        if (bankName?.Length > 128 || accountName?.Length > 200 || branch?.Length > 128) errs.Add("ชื่อธนาคาร/ชื่อบัญชี/สาขา ยาวเกินไป");
        return errs;
    }

    /// <summary>รูป QR ที่อัปโหลด — รับเฉพาะ URL ภายในระบบ (สื่อของเว็บ <c>/api/…/storefront/media/…</c> หรือ <c>/uploads/cms/…</c>) หรือ https ·
    /// ไม่รับ javascript:/data: (แสดงบนหน้าร้านสาธารณะ)</summary>
    public static bool QrImageUrlAcceptable(string? url)
    {
        if (string.IsNullOrWhiteSpace(url)) return true;
        var u = url.Trim();
        if (u.Length > 500) return false;
        if (u.StartsWith("/api/companies/", StringComparison.Ordinal) || u.StartsWith("/uploads/cms/", StringComparison.Ordinal)) return !u.Contains("..");
        return Uri.TryCreate(u, UriKind.Absolute, out var abs) && abs.Scheme == Uri.UriSchemeHttps;
    }
}
