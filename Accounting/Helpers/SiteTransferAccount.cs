using Accounting.Data;
using Accounting.Models.Entities;
using Microsoft.EntityFrameworkCore;
using Accounting.Models.Enums;

namespace Accounting.Helpers;

/// <summary>ค่าบัญชีรับโอนที่ผ่านการตรวจแล้ว (เลขบัญชีเหลือตัวเลข · พร้อมเพย์เหลือตัวเลข · ช่องว่าง = null)</summary>
public sealed record TransferAccountInput(string? BankName, string? AccountNumber, string? AccountName, string? Branch, string? PromptPayId, string? QrUrl);

/// <summary>
/// รอบ 228 (<c>erp-review/2026-11-02/CMS_SETTINGS_UX_AUDIT.md</c> แพ็ก A · §1 ข้อ 1–2) — <b>บัญชีรับโอน/พร้อมเพย์/รูป QR ของเว็บที่เดียว</b>
/// (แถว <c>SitePaymentGateway</c> ชนิดโอน/พร้อมเพย์) ที่ลูกค้าเห็นทั้ง <b>คำสั่งซื้อ · การจอง · ที่พัก</b>:
/// ตัวตั้งรายชื่อธนาคาร (dropdown จากเซิร์ฟเวอร์ — ค่าเดิมที่ไม่อยู่ในรายการยังคงอยู่ ไม่หายเงียบ) · คำเตือนที่พักที่ต้องให้แขกโอนแต่ไม่มีบัญชี
/// <para>เดิม: (ก) modal หน้าคำสั่งซื้อส่ง payload ไม่มีรูป QR ⇒ เซิร์ฟเวอร์ล้างรูป QR ที่ตั้งจากหน้าการชำระเงิน · ชื่อธนาคารพิมพ์เอง ⇒ เปิด modal แล้วเป็น
/// "— เลือก —" ⇒ บันทึกแล้วชื่อหาย (ข) เว็บที่พักไม่มีที่ตั้งเลย ⇒ แขกเห็น "ติดต่อที่พักเพื่อขอเลขบัญชีโอนเงิน"</para>
/// </summary>
public static class SiteTransferAccount
{
    public const string RuleCode = "CMS-SITE-TRANSFER-ACCOUNT";

    /// <summary>ฝ่ายค้านรอบ 228 S-1: <b>ตัวอ่านเดียว</b>ของ "แถวบัญชีรับโอนของเว็บ" — เฉพาะชนิดโอน/พร้อมเพย์ที่ใช้งาน · เรียง SortOrder → CreatedAt → Id (ตายตัว) ·
    /// ผู้อ่านทุกตัว (ตัวอ่านค่าตั้งการจอง · การ์ดบัญชีรับโอน · ตัวเลือกชำระของหน้าร้าน · QR พร้อมเพย์ · พอร์ทัล) ใช้ตัวนี้ ⇒ เห็นแถวเดียวกันเสมอ ·
    /// <paramref name="siteId"/> null = ระดับบริษัท (พอร์ทัลลูกค้า)</summary>
    public static IQueryable<SitePaymentGateway> Rows(IQueryable<SitePaymentGateway> set, Guid companyId, Guid? siteId)
    {
        var q = set.Where(g => g.CompanyId == companyId && g.IsActive && !g.IsDeleted
            && (g.GatewayType == PaymentGatewayType.BankTransfer || g.GatewayType == PaymentGatewayType.PromptPay));
        if (siteId is Guid sid) q = q.Where(g => g.SiteId == sid);
        return q.OrderBy(g => g.SortOrder).ThenBy(g => g.CreatedAt).ThenBy(g => g.Id);
    }

    /// <summary>ฝ่ายค้านรอบ 228 S-1: migration ครั้งเดียว (รันซ้ำได้) — เว็บที่<b>ไม่มี</b>แถวชนิดโอน/พร้อมเพย์ แต่มีเลขบัญชี/พร้อมเพย์อยู่ในแถวชนิดอื่น (Omise ฯลฯ —
    /// เดิม modal หน้าคำสั่งซื้อ/ผู้อ่านเดิมอ่านแถวแรกทุกชนิด) ⇒ คัดลอกเข้าแถวโอนใหม่ (ไม่ลบ/ไม่แก้แถวต้นทาง) · ผู้อ่านตัวเดียวจึงไม่ทำให้บัญชีที่ลูกค้าเคยเห็นหาย</summary>
    public static IReadOnlyList<string> MigrationStatements() => new[]
    {
        """
        INSERT INTO "SitePaymentGateways" ("Id","CompanyId","SiteId","GatewayType","Name","Description","IsTestMode","SupportedCurrencies","IsActive","SortOrder",
            "PromptPayId","PromptPayQrUrl","BankName","BankAccountNumber","BankAccountName","BankBranch","CreatedAt","CreatedBy","IsDeleted")
        SELECT gen_random_uuid(), src."CompanyId", src."SiteId", CASE WHEN src."PromptPayId" IS NOT NULL THEN 2 ELSE 1 END,
               left('โอนเงิน (คัดลอกจาก ' || src."Name" || ')', 128), NULL, false, 'THB', true, 0,
               src."PromptPayId", src."PromptPayQrUrl", src."BankName", src."BankAccountNumber", src."BankAccountName", src."BankBranch", now(), 'migration:round228', false
        FROM (SELECT DISTINCT ON (g."SiteId") g.* FROM "SitePaymentGateways" g
              WHERE g."IsActive" AND NOT g."IsDeleted" AND g."GatewayType" NOT IN (1, 2)
                AND (g."BankAccountNumber" IS NOT NULL OR g."PromptPayId" IS NOT NULL)
              ORDER BY g."SiteId", g."SortOrder", g."CreatedAt", g."Id") src
        WHERE NOT EXISTS (SELECT 1 FROM "SitePaymentGateways" t WHERE t."SiteId" = src."SiteId" AND t."CompanyId" = src."CompanyId"
                          AND t."IsActive" AND NOT t."IsDeleted" AND t."GatewayType" IN (1, 2));
        """,
    };

    /// <summary>ธนาคารในไทยที่ลูกค้าโอนเข้าบ่อย (ป้ายเดียวกับ modal เดิมของหน้าคำสั่งซื้อ) — "อื่น ๆ" พิมพ์เองได้ · ค่าเดิมนอกรายการแสดงคงไว้</summary>
    public static readonly IReadOnlyList<string> BankNames = new[]
    {
        "ธนาคารกรุงเทพ (BBL)", "ธนาคารกสิกรไทย (KBANK)", "ธนาคารกรุงไทย (KTB)", "ธนาคารไทยพาณิชย์ (SCB)",
        "ธนาคารกรุงศรีอยุธยา (BAY)", "ธนาคารทหารไทยธนชาต (TTB)", "ธนาคารออมสิน (GSB)", "ธนาคารเพื่อการเกษตร (BAAC)",
        "ธนาคารอาคารสงเคราะห์ (GHB)", "ธนาคารยูโอบี (UOB)", "ธนาคาร CIMB Thai", "ธนาคารแลนด์ แอนด์ เฮ้าส์ (LH)",
        "ธนาคารไทยเครดิต (TCR)", "ธนาคารเกียรตินาคินภัทร (KKP)", "ธนาคารทิสโก้ (TISCO)",
    };

    /// <summary>ตัวเลือก dropdown: รายชื่อตั้งต้น + ค่าปัจจุบัน (ถ้าอยู่นอกรายการ — กันชื่อธนาคารหายเมื่อบันทึก)</summary>
    public static List<string> BankNameOptions(string? current)
    {
        var list = BankNames.ToList();
        var cur = current?.Trim();
        if (!string.IsNullOrEmpty(cur) && !list.Contains(cur, StringComparer.Ordinal)) list.Insert(0, cur);
        return list;
    }

    /// <summary>มีอะไรให้ลูกค้าโอน/สแกนไหม (เลขบัญชี · พร้อมเพย์ที่ถูกต้อง · รูป QR)</summary>
    public static bool Payable(BookingTransferInfo? t, bool promptPayValid)
        => t != null && (!string.IsNullOrWhiteSpace(t.AccountNumber) || promptPayValid || !string.IsNullOrWhiteSpace(t.QrImageUrl));

    /// <summary>ที่พักที่แขกต้องส่งสลิป/ชำระมัดจำผ่านเว็บ แต่เว็บที่ผูกไม่มีบัญชีรับโอนและไม่มีชำระออนไลน์ ⇒ คำเตือน (null = ไม่ต้องเตือน)</summary>
    public static string? LodgingWarning(bool siteLinked, bool guestMustPay, bool transferPayable, bool onlineReady)
        => siteLinked && guestMustPay && !transferPayable && !onlineReady
            ? "⚠️ ยังไม่มีบัญชีรับโอน/พร้อมเพย์ของเว็บนี้ — แขกจะเห็น “ติดต่อที่พักเพื่อขอเลขบัญชีโอนเงิน” แทนเลขบัญชี/QR · ตั้งที่ ตั้งค่าเว็บ › การชำระเงิน › บัญชีรับโอน"
            : null;

    /// <summary>รอบ 228 (แพ็ก A · §1 ข้อ 2): ทางเข้าเดิม <c>PUT commerce/payment-gateways/{id}</c> — ช่องที่ไม่ส่งมา (null) = <b>ไม่แตะ</b> · "" = ล้าง ·
    /// ค่าอื่น = ตั้ง (ตัดช่องว่าง) — ผู้เรียกที่ไม่รู้จักช่องรูป QR/ชื่อธนาคารไม่ล้างค่าที่ตั้งจากที่อื่นเงียบ ๆ</summary>
    public static string? KeepUnlessSent(string? current, string? sent)
        => sent == null ? current : string.IsNullOrWhiteSpace(sent) ? null : sent.Trim();

    /// <summary>ฝ่ายค้านรอบ 228 S-4: ตัวตรวจ + ทำให้เป็นรูปมาตรฐาน <b>ตัวเดียว</b> (การ์ดบัญชีรับโอน · ค่าตั้งการชำระของการจอง · ทางเข้าเดิม payment-gateways) ·
    /// ผิด ⇒ ใส่ใน <paramref name="errs"/> (คืนค่าเสมอ)</summary>
    public static TransferAccountInput Normalize(string? bankName, string? accountNumber, string? accountName, string? branch,
        string? promptPayId, string? qrUrl, Func<string, bool> promptPayValid, List<string> errs)
    {
        errs.AddRange(BookingPaymentChannels.ValidateBankAccount(bankName, accountNumber, accountName, branch));
        var acct = BookingPaymentChannels.NormalizeAccountNumber(accountNumber);
        var ppId = string.IsNullOrWhiteSpace(promptPayId) ? null : new string(promptPayId.Where(char.IsDigit).ToArray());
        if (ppId != null && !promptPayValid(ppId))
            errs.Add("พร้อมเพย์ต้องเป็นเบอร์มือถือ 10 หลัก (ขึ้นต้น 0) หรือเลขประจำตัวผู้เสียภาษี/บัตรประชาชน 13 หลักที่ถูกต้อง");
        var qr = string.IsNullOrWhiteSpace(qrUrl) ? null : qrUrl.Trim();
        if (!BookingPaymentChannels.QrImageUrlAcceptable(qr)) errs.Add("รูป QR ต้องอัปโหลดผ่านคลังสื่อของเว็บ หรือเป็นลิงก์ https");
        return new TransferAccountInput(acct == null ? null : bankName?.Trim(), acct, acct == null ? null : accountName?.Trim(),
            acct == null || string.IsNullOrWhiteSpace(branch) ? null : branch.Trim(), ppId, qr);
    }

    public static BookingTransferInfo? InfoOf(SitePaymentGateway? gw)
        => gw == null ? null : new BookingTransferInfo(gw.BankName, gw.BankAccountNumber, gw.BankAccountName, gw.BankBranch, gw.PromptPayId, gw.PromptPayQrUrl);

    /// <summary>เขียนแถวบัญชีรับโอนของเว็บ (แถวแรกจาก <see cref="Rows"/> · ไม่มี ⇒ สร้างเมื่อมีค่า — <b>ไม่มีแถวโอนที่สองเกิด</b>) — ไม่บันทึก (ผู้เรียก SaveChanges) ·
    /// ฝ่ายค้านรอบ 218 (B1): ไม่แตะแถว Omise/2C2P · ไม่เปลี่ยนชนิด/ชื่อแถวเดิม · ช่องที่มีค่าแล้วส่งมาว่าง ⇒ ต้องยืนยันการล้าง (409)</summary>
    public static async Task<SitePaymentGateway?> WriteRowAsync(AccountingDbContext db, Guid companyId, Guid siteId, TransferAccountInput t, bool confirmClear, string userId)
    {
        // รีวิวรอบ 228: ล็อกต่อเว็บ (xact) — สองคำขอ "บันทึกครั้งแรก" พร้อมกันต้องไม่สร้างแถวโอนสองแถว · ผู้เรียกต้องเปิดธุรกรรม (ไม่งั้นล็อกปล่อยทันทีหลังคำสั่ง)
        if (db.Database.CurrentTransaction == null)
            throw new InvalidOperationException("SiteTransferAccount.WriteRowAsync ต้องเรียกภายในธุรกรรม (ล็อกต่อเว็บ)");
        await db.Database.ExecuteSqlRawAsync("SELECT pg_advisory_xact_lock({0})", LockKey(companyId, siteId));
        var gw = await Rows(db.SitePaymentGateways, companyId, siteId).FirstOrDefaultAsync();
        var cleared = BookingPaymentChannels.ClearedFields(InfoOf(gw), t.BankName, t.AccountNumber, t.AccountName, t.Branch, t.PromptPayId, t.QrUrl);
        if (cleared.Count > 0 && !confirmClear)
            throw new BusinessRuleException(BookingPaymentChannels.ClearConfirmMessage(cleared), BookingPaymentChannels.ClearConfirmRuleCode, 409);
        if (gw == null && (t.AccountNumber != null || t.PromptPayId != null || t.QrUrl != null))
        {
            gw = new SitePaymentGateway
            {
                CompanyId = companyId, SiteId = siteId, CreatedBy = userId, IsTestMode = false,
                GatewayType = t.PromptPayId != null ? PaymentGatewayType.PromptPay : PaymentGatewayType.BankTransfer,
                Name = t.PromptPayId != null ? "PromptPay" : (t.BankName ?? "โอนเงิน"),
            };
            db.SitePaymentGateways.Add(gw);
        }
        if (gw != null)
        {
            gw.BankName = t.BankName; gw.BankAccountNumber = t.AccountNumber; gw.BankAccountName = t.AccountName; gw.BankBranch = t.Branch;
            gw.PromptPayId = t.PromptPayId; gw.PromptPayQrUrl = t.QrUrl;
            gw.UpdatedBy = userId;
        }
        return gw;
    }

    /// <summary>คีย์ล็อกของบัญชีรับโอนต่อเว็บ (ตัวเดียว)</summary>
    public static long LockKey(Guid companyId, Guid siteId) => AdvisoryLockKey.For(companyId, AdvisoryLockKey.CmsBookingInvoice, "transfer:" + siteId.ToString("N"));

    /// <summary>ฝ่ายค้านรอบ 228 S-3: ค่าที่ลง audit (เลขบัญชี/พร้อมเพย์ปิดเหลือ 4 ตัวท้าย · รูป QR = มี/ไม่มี) — PII ไม่ลงเต็ม</summary>
    public static object? MaskForAudit(BookingTransferInfo? t)
    {
        if (t == null) return null;
        static string? Last4(string? v) => string.IsNullOrWhiteSpace(v) ? null : "••••" + (v.Length > 4 ? v[^4..] : v);
        return new { bank = t.BankName, account = Last4(t.AccountNumber), accountName = t.AccountName, branch = t.Branch,
            promptPay = Last4(t.PromptPayId), qr = !string.IsNullOrWhiteSpace(t.QrImageUrl) };
    }

    /// <summary>ฝ่ายค้านรอบ 228 S-7: ผลของการล้างบัญชีกับงานอื่นของเว็บ (คำสั่งซื้อที่เปิดรับโอน · ที่พักที่แขกต้องโอน) — ไม่ว่าง ⇒ 409 พร้อมเหตุ (ทางไปต่อ: ปิดรับโอนของงานนั้นก่อน / ตั้งชำระออนไลน์)</summary>
    public static string? ClearBreaksOtherWorkProblem(bool payableAfter, bool onlineReady, bool ordersTakeTransfer, bool lodgingGuestPays)
    {
        if (payableAfter || onlineReady) return null;
        var hits = new List<string>();
        if (ordersTakeTransfer) hits.Add("คำสั่งซื้อที่เปิด “โอนเงิน” (ลูกค้าจะเห็นแค่ชำระภายหลัง)");
        if (lodgingGuestPays) hits.Add("ที่พักที่แขกต้องส่งสลิป/ชำระมัดจำ (แขกจะเห็น “ติดต่อที่พักเพื่อขอเลขบัญชี”)");
        return hits.Count == 0 ? null
            : "ล้างบัญชีรับโอนแล้วงานต่อไปนี้ของเว็บจะรับเงินไม่ได้: " + string.Join(" · ", hits)
              + " — ปิด “รับโอน” ของงานนั้นก่อน หรือตั้งชำระออนไลน์ แล้วค่อยล้าง";
    }
}

