namespace Accounting.Helpers;

/// <summary>รอบ 259-3 (ทีม E 3ก-1 · 4.2 · คำตัดสิน 259-3-7) — <b>เช็กลิสต์เริ่มต้นใช้งานตัวเดียว (เซิร์ฟเวอร์)</b>
/// <para>═══ ที่มา ═══ getting-started.html ถือกติกาเองทั้งชุดใน JS (ผังบัญชี "&gt;= 20" · VAT · นับผู้ติดต่อ/สินค้าจากหน้าแรกของรายการ) ·
/// ไม่มีขั้นตั้งอีเมลผู้ส่ง · ช่องทางรับเงิน · LINE · ลิงก์ไปหน้าตั้งค่าไม่ระบุแท็บ · บริษัทที่ไม่จด VAT ค้างเทาตลอดโดยไม่มีคำอธิบาย</para>
/// <para>═══ กติกา ═══ ข้อเท็จจริงมาจากฐาน (ผู้เรียกรวบรวม) · ตัวตัดสิน "เสร็จ/ยัง" + ข้อความ + ลิงก์อยู่ที่นี่ที่เดียว · ขั้น <c>Optional</c> ไม่นับในความคืบหน้า</para></summary>
public static class GettingStartedChecklist
{
    /// <summary>บริษัทมีบัญชีธนาคารที่ลูกค้าโอนเข้าได้ (เลขบัญชีหรือพร้อมเพย์) — ใบแจ้งหนี้/หน้าชำระใช้บัญชีของใบก่อน (TransferForDocumentAsync)
    /// จึงนับคู่กับบัญชีรับโอนของเว็บ (<c>DocumentPayLinkPolicy.CompanyFacts.BankTransfer</c>) · ตัวเดียวของเช็กลิสต์และการ์ด Autopilot</summary>
    public static IQueryable<Accounting.Models.Entities.BankAccount> TransferAccounts(IQueryable<Accounting.Models.Entities.BankAccount> set, Guid companyId)
        => set.Where(b => b.CompanyId == companyId && !b.IsDeleted
            && ((b.AccountNumber != null && b.AccountNumber != "") || (b.PromptPayId != null && b.PromptPayId != "")));

    /// <summary>ผังบัญชีที่ระบบตั้งให้ตอนสร้างบริษัทมีหลายสิบบัญชี — ต่ำกว่านี้ = ยังไม่ได้ตั้ง/ถูกลบเกือบหมด</summary>
    public const int MinAccounts = 20;

    public sealed record Facts(bool CompanyInfoComplete, int FiscalPeriods, int Accounts, bool VatRegistered, decimal VatRate,
        int BankAccounts, int Contacts, int Products, int Documents, bool SenderEmailVerified, bool OnlinePayReady, bool TransferReady, bool LineConfigured);

    public sealed record Step(string Key, string Icon, string Title, string Description, bool Done, bool Optional, string? Hint, string Href);

    public sealed record Result(IReadOnlyList<Step> Steps, int RequiredDone, int RequiredTotal, int Percent);

    public static Result Build(Facts f)
    {
        var vatDone = f.VatRegistered && f.VatRate > 0;
        var steps = new List<Step>
        {
            new("company", "🏢", "ข้อมูลบริษัท", "ชื่อ เลขผู้เสียภาษี ที่อยู่ — แสดงบนใบกำกับภาษี/ใบเสร็จทุกใบ",
                f.CompanyInfoComplete, false, f.CompanyInfoComplete ? null : "ยังขาดชื่อ เลขผู้เสียภาษี หรือที่อยู่", "/pages/settings.html?tab=company"),
            new("fiscal", "📅", "งวดบัญชี (ปีและเดือน)", "สร้างงวดบัญชีของปีปัจจุบัน — จำเป็นสำหรับบันทึกรายการและปิดงวด",
                f.FiscalPeriods > 0, false, null, "/pages/fiscal.html"),
            new("coa", "📚", "ผังบัญชี", "ผังบัญชีถูกตั้งให้อัตโนมัติเมื่อสร้างบริษัท — ตรวจและปรับให้เหมาะกับธุรกิจ",
                f.Accounts >= MinAccounts, false, f.Accounts >= MinAccounts ? null : $"มีบัญชี {f.Accounts} บัญชี — น้อยกว่าผังมาตรฐาน", "/pages/accounts.html"),
            new("vat", "💸", "การจดทะเบียน VAT", "ระบุว่าบริษัทจด VAT แล้วหรือยัง + อัตรา VAT (7%)",
                vatDone, false,
                vatDone ? null : f.VatRegistered
                    ? "ตั้งว่าจด VAT แล้วแต่อัตราเป็น 0 — ตรวจอัตราที่หน้าตั้งค่า"
                    : "ตั้งว่าไม่ได้จด VAT — ถ้าถูกต้องข้ามขั้นนี้ได้ · รายได้เกิน 1.8 ล้านบาท/ปี ต้องจด VAT ภายใน 30 วัน (ป.รัษฎากร ม.81/1 · ม.85/1)",
                "/pages/settings.html?tab=tax"),
            new("bank", "🏦", "บัญชีธนาคาร", "เพิ่มบัญชีธนาคารที่ใช้รับ-จ่าย — ใช้กระทบยอดและแสดงบนใบแจ้งหนี้",
                f.BankAccounts > 0, false, null, "/pages/bank.html"),
            new("contact", "👥", "ลูกค้า / ผู้ขาย (อย่างน้อย 1 ราย)", "จำเป็นสำหรับใบเสนอราคา / ใบกำกับภาษี",
                f.Contacts > 0, false, null, "/pages/contacts.html"),
            new("product", "📦", "สินค้า / บริการ", "ไม่บังคับ — ช่วยให้สร้างเอกสารได้เร็วขึ้น",
                f.Products > 0, true, null, "/pages/products.html"),
            new("senderEmail", "📧", "อีเมลผู้ส่งของบริษัท", "ส่งเอกสาร/ใบเสร็จถึงลูกค้าในนามบริษัท (ทดสอบส่งผ่านแล้ว)",
                f.SenderEmailVerified, false, f.SenderEmailVerified ? null : "ยังไม่ได้ตั้งหรือยังไม่ได้ทดสอบส่ง — ระบบส่งจากอีเมลกลางแทน", "/pages/settings.html?tab=email"),
            new("receiveMoney", "💰", "ช่องทางรับเงินจากลูกค้า", "บัญชีรับโอน/QR พร้อมเพย์ + แจ้งสลิป หรือรับชำระออนไลน์",
                f.TransferReady || f.OnlinePayReady, false,
                f.TransferReady || f.OnlinePayReady ? null : "ยังไม่มีบัญชีรับโอน/พร้อมเพย์ และยังไม่ได้ตั้งรับชำระออนไลน์",
                f.TransferReady || !f.OnlinePayReady ? "/pages/bank.html" : "/pages/payment-settings.html"),
            new("line", "💬", "LINE ของร้าน", "ไม่บังคับ — ส่งเอกสาร/แจ้งเตือนผ่าน LINE OA ของร้านเอง",
                f.LineConfigured, true, null, "/pages/settings.html?tab=line"),
            new("firstDoc", "🧾", "สร้างเอกสารใบแรก", "ลองสร้างใบเสนอราคาหรือใบแจ้งหนี้ใบแรกเพื่อทดสอบทั้งเส้นทาง",
                f.Documents > 0, false, null, "/pages/documents.html?openCreate=revenue&type=Quotation"),
        };
        var required = steps.Where(s => !s.Optional).ToList();
        var done = required.Count(s => s.Done);
        var pct = required.Count == 0 ? 100 : (int)Math.Round(done * 100m / required.Count, MidpointRounding.AwayFromZero);
        return new Result(steps, done, required.Count, pct);
    }
}
