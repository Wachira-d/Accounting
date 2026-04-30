using System.Text.RegularExpressions;

namespace Accounting.Services.Localization;

/// <summary>
/// Translates Thai backend exception messages to English when the
/// Accept-Language request header indicates English. Designed to allow
/// existing throw-new-Thai-message code to remain unchanged.
///
/// Two strategies:
///   1) Exact-match dictionary for common literal messages.
///   2) Regex-pattern dictionary for messages with dynamic data
///      (e.g. "เลขที่สัญญา ABC123 ซ้ำ" → "Contract number ABC123 already exists").
/// Falls back to the original message if no translation is found.
/// </summary>
public static class ErrorMessageTranslator
{
    private static readonly Dictionary<string, string> ExactMatches = new(StringComparer.OrdinalIgnoreCase)
    {
        // Generic
        ["ข้อมูลไม่ถูกต้อง"] = "Invalid data",
        ["เกิดข้อผิดพลาดภายในระบบ"] = "An internal server error occurred",
        ["ไม่ได้รับอนุญาต"] = "Unauthorized",
        ["ไม่พบข้อมูล"] = "Not found",
        ["ไม่พบข้อมูลที่ร้องขอ"] = "The requested resource was not found",
        ["ข้อมูลซ้ำ"] = "Duplicate data",

        // Auth
        ["ผู้ใช้นี้มีอยู่แล้ว"] = "This user already exists",
        ["อีเมลนี้มีอยู่แล้ว"] = "This email is already registered",
        ["รหัสผ่านไม่ถูกต้อง"] = "Incorrect password",
        ["ไม่พบผู้ใช้"] = "User not found",
        ["บัญชีถูกระงับ"] = "Account suspended",
        ["รหัสผ่านไม่ปลอดภัย"] = "Password is not secure",
        ["รหัสผ่านสั้นเกินไป"] = "Password too short",

        // Companies / tenant
        ["ไม่พบบริษัท"] = "Company not found",
        ["ไม่พบข้อมูลบริษัท"] = "Company information not found",
        ["บริษัทนี้ถูกปิดใช้งาน"] = "This company has been deactivated",

        // Contacts
        ["ไม่พบข้อมูลผู้ติดต่อ"] = "Contact not found",
        ["ไม่พบลูกค้า"] = "Customer not found",
        ["ไม่พบคู่ค้า"] = "Supplier not found",

        // Documents
        ["ไม่พบเอกสาร"] = "Document not found",
        ["เอกสารนี้ถูกอนุมัติแล้ว"] = "This document has been approved",
        ["เอกสารนี้ถูกยกเลิกแล้ว"] = "This document has been cancelled",
        ["ไม่สามารถแก้ไขเอกสารที่อนุมัติแล้ว"] = "Cannot edit an approved document",
        ["ไม่สามารถลบเอกสารที่อนุมัติแล้ว"] = "Cannot delete an approved document",

        // POS
        ["ไม่พบ POS Terminal"] = "POS Terminal not found",
        ["Terminal นี้ถูกปิดใช้งาน"] = "This Terminal is deactivated",
        ["มีกะที่เปิดอยู่แล้ว กรุณาปิดกะก่อน"] = "There is already an open shift. Please close it first.",
        ["กรุณาเลือก Terminal ก่อนเปิดกะ"] = "Please select a Terminal before opening a shift",
        ["ไม่พบกะการขาย"] = "Sales shift not found",
        ["กะนี้ปิดไปแล้ว"] = "This shift has already been closed",
        ["ยังมีออเดอร์ที่ยังไม่เสร็จ กรุณาปิดออเดอร์ก่อน"] = "There are unfinished orders. Please close them first.",
        ["ไม่พบออเดอร์"] = "Order not found",
        ["ไม่พบรายการ"] = "Item not found",
        ["ออเดอร์นี้เสร็จสิ้นแล้ว"] = "This order is already completed",
        ["ออเดอร์นี้ถูกยกเลิกไปแล้ว"] = "This order has already been voided",
        ["ไม่สามารถแก้ไขออเดอร์ที่เสร็จสิ้นหรือยกเลิกแล้ว"] = "Cannot modify a completed or voided order",
        ["ไม่สามารถเพิ่มรายการในออเดอร์ที่เสร็จสิ้น"] = "Cannot add items to a completed order",
        ["ไม่สามารถชำระเงินออเดอร์ที่ถูกยกเลิก"] = "Cannot pay for a voided order",
        ["ไม่พบกะการขายที่เปิดอยู่"] = "No open sales shift found",
        ["ไม่พบเงินมัดจำ"] = "Deposit not found",
        ["จำนวนเงินคืนมากกว่ายอดคงเหลือ"] = "Refund amount exceeds remaining balance",

        // Accounting
        ["ยอดเดบิตและเครดิตต้องไม่ติดลบ"] = "Debit and credit amounts must not be negative",
        ["แต่ละรายการต้องมียอดเดบิตหรือเครดิตเพียงด้านเดียว"] = "Each line must have either a debit OR a credit, not both",
        ["ต้องมียอดเดบิต/เครดิตอย่างน้อย 1 รายการ"] = "At least one debit/credit line is required",
        ["ผังบัญชียังไม่ถูกตั้งค่า"] = "Chart of accounts is not configured",

        // Tax / e-Tax
        ["ปีภาษีไม่ถูกต้อง (ต้องอยู่ระหว่าง 2020 ถึงปีปัจจุบัน+1)"] = "Invalid tax year (must be between 2020 and current year + 1)",
        ["เดือนภาษีไม่ถูกต้อง (ต้องอยู่ระหว่าง 1 ถึง 12)"] = "Invalid tax month (must be between 1 and 12)",
        ["รายงานภาษีเดือนนี้มีอยู่แล้ว"] = "Tax report for this month already exists",

        // Revenue Recognition
        ["ไม่พบสัญญา"] = "Contract not found",

        // Subscription / billing
        ["ฟีเจอร์นี้ไม่อยู่ในแพ็กเกจ"] = "This feature is not in your plan",
        ["จำนวนผู้ใช้เกินจำกัดของแพ็กเกจ"] = "User count exceeds plan limit",
        ["จำนวนบริษัทเกินจำกัดของแพ็กเกจ"] = "Company count exceeds plan limit",
    };

    // Pattern-based: capture groups become {0}, {1}, ...
    private static readonly List<(Regex Pattern, string Template)> Patterns = new()
    {
        (new Regex(@"^เลขที่สัญญา (.+) ซ้ำ$", RegexOptions.Compiled),
            "Contract number {0} already exists"),
        (new Regex(@"^ยอดเดบิต \(([^)]+)\) ไม่เท่ากับยอดเครดิต \(([^)]+)\)$", RegexOptions.Compiled),
            "Debit total ({0}) does not equal credit total ({1})"),
        (new Regex(@"^ยอดชำระ \(([^)]+)\) ไม่ครบ ยอดที่ต้องจ่าย \(([^)]+)\)$", RegexOptions.Compiled),
            "Payment received ({0}) is less than amount due ({1})"),
        (new Regex(@"^การบันทึกบัญชีอัตโนมัติไม่สมดุล: เดบิต ([^≠]+) ≠ เครดิต (.+)$", RegexOptions.Compiled),
            "Auto-posted journal is unbalanced: debit {0} ≠ credit {1}"),
        (new Regex(@"^การบันทึกบัญชีชำระเงินไม่สมดุล: เดบิต ([^≠]+) ≠ เครดิต (.+)$", RegexOptions.Compiled),
            "Payment journal is unbalanced: debit {0} ≠ credit {1}"),
    };

    public static string Translate(string message, string locale)
    {
        if (string.IsNullOrEmpty(message)) return message;
        if (!locale.StartsWith("en", StringComparison.OrdinalIgnoreCase)) return message;

        if (ExactMatches.TryGetValue(message.Trim(), out var exact)) return exact;

        foreach (var (pattern, template) in Patterns)
        {
            var match = pattern.Match(message);
            if (match.Success)
            {
                var args = match.Groups.Cast<Group>().Skip(1).Select(g => (object)g.Value).ToArray();
                return string.Format(template, args);
            }
        }
        return message;
    }

    /// <summary>Resolve preferred locale from Accept-Language header. Returns "th" or "en".</summary>
    public static string ResolveLocale(string? acceptLanguage)
    {
        if (string.IsNullOrEmpty(acceptLanguage)) return "th";
        var first = acceptLanguage.Split(',')[0].Trim().ToLowerInvariant();
        if (first.StartsWith("en")) return "en";
        return "th";
    }
}
