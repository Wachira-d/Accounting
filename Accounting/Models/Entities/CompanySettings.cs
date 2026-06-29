using Accounting.Models.Enums;

namespace Accounting.Models.Entities;

/// <summary>
/// การตั้งค่าบริษัท (Logo, Templates, Defaults)
/// เทียบเท่า FlowAccount & PEAK: Company settings
/// </summary>
public class CompanySettings : TenantEntity
{
    /// <summary>เมื่อ false (default) — ปฏิเสธการขาย/เบิกของออกถ้าจะทำให้
    /// CurrentStock ติดลบ. true = ปล่อยให้ติดลบ (ใช้สำหรับ pre-order หรือ
    /// บริการที่จับ "ของไม่อยู่ก่อนแล้วเข้า"). ติดลบ stock ทำลาย WAC + COGS
    /// แบบ irreversible — เปิดเฉพาะเมื่อรู้จริง ๆ ว่าทำอะไรอยู่.</summary>
    public bool AllowNegativeStock { get; set; } = false;

    // Branding
    public string? LogoPath { get; set; }
    public string? LogoUrl { get; set; }
    public string? PrimaryColor { get; set; }
    public string? SecondaryColor { get; set; }

    // Document Defaults
    public string? DefaultPaymentTerms { get; set; }     // e.g. "Net 30"
    public int DefaultPaymentDueDays { get; set; } = 30;
    public string? InvoiceNotes { get; set; }
    public string? ReceiptNotes { get; set; }
    public string? QuotationNotes { get; set; }
    public string? InvoiceFooter { get; set; }
    public string? ReceiptFooter { get; set; }

    // Print Layout
    // When true, printed documents (Invoice / Receipt / Expense / etc.)
    // include a compact "GL Posting Summary" (Dr/Cr) table at the very
    // bottom — an internal-audit footer below the signature area, drawn
    // from the document's posted Journal Entry.
    public bool ShowGlEntryOnDocument { get; set; } = false;

    // HR — annual leave quota per LeaveType, stored as JSON:
    //   {"Annual": 6, "Sick": 30, "Personal": 3, "Maternity": 98}
    // Null / missing keys fall back to the Thai labor-law minimums in
    // PayrollService.DefaultLeaveQuotas.
    public string? LeaveQuotasJson { get; set; }

    // HR approval enforcement — when true, Approve / Reject on leaves
    // and salary advances is restricted to the requester's direct manager
    // (resolved via Employee.DirectManagerId) OR users holding a
    // privileged company-role (Owner / SystemAdmin). Default false to
    // preserve the historical "anyone can approve" behaviour.
    public bool EnforceManagerApproval { get; set; } = false;

    // Tax Settings
    public decimal DefaultVatRate { get; set; } = 7;
    public bool VatRegistered { get; set; } = true;
    public string? VatRegistrationDate { get; set; }

    /// <summary>§82/5(6) override — ระบบ default จะเตือนว่า VAT ของ
    /// ค่าน้ำมัน/ซ่อม/เช่ารถยนต์นั่ง ≤10 ที่นั่ง เคลมไม่ได้ (ประกาศอธิบดีฯ
    /// ฉบับที่ 42). เปิด flag นี้ = บริษัทเป็น vehicle dealer / รถยนต์เป็น
    /// inventory → ยกเว้นการเตือน (claimable ตามปกติ). เปิดเฉพาะตอนได้รับ
    /// ใบทะเบียนพาณิชย์ระบุประเภทกิจการจริง.</summary>
    public bool IsVehicleDealer { get; set; } = false;

    /// <summary>Cash (default) = recognize WHT-Asset / WHT-Payable at the
    /// Receipt / PaymentVoucher (strict ประมวลรัษฎากร §50/§52). Accrual =
    /// recognize at Invoice / PurchaseInvoice approval (common SMB practice,
    /// audit-accepted). New tenants default to Cash; existing tenants keep
    /// the historical Accrual posting until they explicitly switch.</summary>
    public WhtRecognitionBasis WhtRecognitionBasis { get; set; } = WhtRecognitionBasis.Cash;

    // Email Templates
    public string? EmailFromName { get; set; }
    public string? EmailReplyTo { get; set; }
    public string? InvoiceEmailSubject { get; set; }
    public string? InvoiceEmailBody { get; set; }

    // Security
    public bool RequireApprovalForDocuments { get; set; } = false;
    public decimal? ApprovalThresholdAmount { get; set; }
    public bool EnableApiAccess { get; set; } = false;
    public int MaxApiKeys { get; set; } = 5;

    // Closing Settings
    public bool AutoCloseMonthEnd { get; set; } = false;
    public int MonthEndClosingDay { get; set; } = 15;    // วันสุดท้ายที่บันทึกเดือนก่อนได้
    public bool PreventPostToClosedPeriod { get; set; } = true;

    /// <summary>แนบ PDF หนังสือรับรองหัก ณ ที่จ่าย (50ทวิ) เข้าใบสำคัญจ่าย
    /// อัตโนมัติตอนออกใบ. default ปิด — ผู้ใช้กด download/พิมพ์เองจากหน้า WHT
    /// (ไฟล์สวยกว่า). เปิดได้ถ้าต้องการให้แนบให้อัตโนมัติ.</summary>
    public bool AutoAttachWhtCertPdf { get; set; } = false;

    // e-Tax Invoice Settings (per-company)
    public bool EtaxEnabled { get; set; } = false;
    public EtaxMode EtaxMode { get; set; } = EtaxMode.None;  // ByEmail, Direct, or Both
    public string? EtaxCertificatePath { get; set; }        // path to .p12/.pfx certificate (Direct mode)
    public string? EtaxCertificatePassword { get; set; }    // encrypted certificate password
    public string? EtaxRdApiKey { get; set; }               // Revenue Department API Key (Direct mode)
    public string? EtaxRdApiSecret { get; set; }            // Revenue Department API Secret
    public bool EtaxTestMode { get; set; } = true;          // true = ทดสอบ, false = production
    public bool EtaxAutoSign { get; set; } = false;         // ลงนามอัตโนมัติเมื่อสร้าง
    public bool EtaxAutoSubmit { get; set; } = false;       // ส่งสรรพากรอัตโนมัติหลังลงนาม
    public string? EtaxServiceProvider { get; set; }        // "RD" or third-party provider
    public string? EtaxXmlOutputPath { get; set; }          // custom XML output path

    // e-Tax by Email Settings (Mode = ByEmail) — ต้องสมัครกับสรรพากรก่อน (รสภ.01-1)
    public bool EtaxByEmailRdRegistered { get; set; } = false;        // ลงทะเบียนกับสรรพากรแล้วหรือยัง
    public DateTime? EtaxByEmailRegistrationDate { get; set; }        // วันที่ได้รับอนุมัติ
    public string? EtaxByEmailRegistrationNumber { get; set; }        // เลขรหัสรับรอง (ถ้ามี)
    public string? EtaxByEmailSenderEmail { get; set; }               // อีเมลผู้ส่งที่ลงทะเบียนกับสรรพากร
    public string EtaxByEmailRdTimestampAddress { get; set; } = "csemail@etax.teda.th"; // CC ไป RD timestamp
    public bool EtaxByEmailEmbedXml { get; set; } = true;             // ฝัง XML ใน PDF/A-3 (สรรพากรกำหนด)
    public bool EtaxByEmailAutoSendOnApprove { get; set; } = false;   // ส่งอัตโนมัติเมื่อ approve เอกสาร

    // ===== Email Sending Configuration (per-company) =====
    public EmailProvider EmailProvider { get; set; } = EmailProvider.Smtp;
    public string? EmailFromAddress { get; set; }
    public bool EmailConfigured { get; set; } = false;                // ตั้งค่าและ test ผ่านแล้ว
    public DateTime? EmailLastTestedAt { get; set; }
    public string? EmailLastTestStatus { get; set; }                  // "OK" or error message

    // SMTP-specific (works with Gmail App Password, Office365, etc.)
    public string? EmailSmtpHost { get; set; }
    public int EmailSmtpPort { get; set; } = 587;
    public string? EmailSmtpUsername { get; set; }
    public string? EmailSmtpPassword { get; set; }                    // AES-256-GCM at rest via SecretProtector
    public bool EmailSmtpUseSsl { get; set; } = true;

    // Microsoft Graph API (OAuth2 client credentials, app-only)
    public string? EmailMsTenantId { get; set; }
    public string? EmailMsClientId { get; set; }
    public string? EmailMsClientSecret { get; set; }                  // AES-256-GCM at rest via SecretProtector
    public string? EmailMsSenderUpn { get; set; }                     // mailbox to send from (UserPrincipalName)

    // Gmail API (OAuth2 with refresh token, or service account)
    public string? EmailGmailClientId { get; set; }
    public string? EmailGmailClientSecret { get; set; }
    public string? EmailGmailRefreshToken { get; set; }
    public string? EmailGmailServiceAccountJson { get; set; }         // alt: service account credentials JSON

    // ===== LINE Messaging API (per-company) =====
    // ระบบเดิมใช้ token global จาก appsettings — ปรับให้ตั้งต่อบริษัทได้
    // ถ้าฟิลด์เหล่านี้ว่าง LineNotifyService จะ fallback ไปอ่าน appsettings
    // (สำหรับการใช้งานเก่า) เพื่อไม่ break ระบบเก่า.
    public bool LineEnabled { get; set; } = false;
    /// <summary>Channel Access Token จาก LINE Developers (Messaging API).
    /// เข้ารหัสด้วย SecretProtector ก่อนเก็บลง DB.</summary>
    public string? LineChannelAccessToken { get; set; }
    /// <summary>Channel Secret สำหรับตรวจ webhook signature (เข้ารหัส).</summary>
    public string? LineChannelSecret { get; set; }
    /// <summary>LINE Group/Room ID สำหรับส่งแจ้งเตือนกลุ่ม (เช่น ทีมบัญชี).
    /// optional — ถ้าใส่ จะใช้แทน global GroupId เดิม.</summary>
    public string? LineDefaultGroupId { get; set; }
    public bool LineConfigured { get; set; } = false;
    public DateTime? LineLastTestedAt { get; set; }
    public string? LineLastTestStatus { get; set; }

    // ─── Cross-tenant knowledge sharing (OCR learning) ───
    // When true (default), this company's per-tenant training data
    // (OcrCategoryMappings + OcrVendorIntelligence) is anonymously
    // aggregated into the system-wide tables after at least
    // CrossTenantAggregateMinTenants distinct companies have used the
    // same (vendor, keyword → account) pattern. Only the aggregate is
    // promoted — no single-company detail is exposed. Companies that
    // want their training kept entirely private can opt out here.
    public bool ShareTrainingDataAnonymously { get; set; } = true;

    // Per-tenant bonus multiplier applied to this company's OWN learned
    // mappings at prediction time. Higher = own data wins more
    // decisively over system aggregates. Default 2.0 means own training
    // counts twice; range 1.0–10.0.
    public decimal OwnTrainingBonusMultiplier { get; set; } = 2.0m;

    // ─── OCR document-target preference ───
    // When OCR scans a Buyer-side TaxInvoice / Invoice and the system
    // has no vendor-specific history yet, what does THIS company usually
    // book it as? Three common Thai SME flows:
    //
    //   • PaymentVoucher (default) — cash-basis. User uploads tax-invoice,
    //     books PaymentVoucher (Dr Expense / Cr Cash) immediately because
    //     they pay on receipt. Most one-person / small-team businesses.
    //
    //   • PurchaseInvoice — accrual A/P workflow. User books the
    //     PurchaseInvoice (Dr Expense / Cr A/P), pays later via a
    //     separate PaymentVoucher. Larger businesses with month-end close.
    //
    //   • Expense — quick-and-dirty journal. Skip the document workflow
    //     entirely, post directly to Expense. Used for petty-cash flow.
    //
    // VendorIntelligenceService still overrides this when it has high-
    // confidence history for a specific vendor — the setting is the
    // FALLBACK when there's no learned preference yet.
    public DocumentType OcrBuyerInvoiceDefaultTarget { get; set; } = DocumentType.PaymentVoucher;

    // ── แหล่งเงิน default สำหรับ OCR / auto-create (ฝั่ง Cr ของ PV/Receipt) ──
    // เมื่อ OCR สร้างใบสำคัญจ่ายแบบจ่ายสด ต้องเลือก "แหล่งเงิน" (บัญชี Cr ที่
    // เงินไหลออก). เดิมไม่มีค่านี้ → auto-fill หยิบบัญชีธนาคารรหัสต่ำสุดมั่ว ๆ
    // (กรุงไทย 11110 ชนะ กสิกร 11120 เสมอ) ทำให้ลงผิดบัญชี. ตั้งค่านี้ =
    // ChartOfAccount.Id ของบัญชีเงินสด/ธนาคารที่บริษัทใช้จ่ายเป็นหลัก →
    // OcrService ใช้เป็น default แทนการเดา. null = พฤติกรรมเดิม (lowest-code).
    // Partner ที่ส่ง paymentAccountCode/bankCode ผ่าน metadata จะชนะค่านี้อีกที.
    public Guid? DefaultPaymentAccountId { get; set; }

    // ── พ.ร.บ.การบัญชี ม.7 — ผู้ทำบัญชี (Bookkeeper / CPD) ──
    // กฎหมาย: งบการเงินที่นำส่ง DBD ต้องระบุชื่อ + เลขทะเบียนผู้ทำบัญชี (CPD)
    // ที่ขึ้นทะเบียนกับสภาวิชาชีพบัญชี. ไม่มี = ยื่นไม่ได้ (ผู้บริหารรับผิด
    // ทางอาญา). ระบบใช้ field นี้เป็น gate ก่อน finalize งบ + XBRL export.
    // null = ยังไม่เซ็ตค่า → ระบบ block การ export งบ/XBRL จนกว่าจะตั้ง.
    public string? BookkeeperName { get; set; }
    public string? BookkeeperCpdNumber { get; set; }

    // ── §86/4 hard-enforcement (opt-in) ──
    // เมื่อ true: ApproveDocumentAsync จะ block (throw) ถ้าใบกำกับ/ใบเสร็จ/CN/DN
    // ขาด field บังคับ §86/4 (BuyerTaxId 13 หลัก, BuyerAddress, BuyerBranchCode
    // 5 หลัก). default false = พฤติกรรมเดิม (soft warning, ผู้ใช้กด acknowledge
    // ผ่านได้). บริษัทที่ต้องการเข้มเปิด flag นี้ → กัน operator-error ที่
    // approve ใบไม่ครบ §86/4 ก่อนจะถูกตรวจสรรพากร.
    public bool EnforceFullTaxInvoiceFields { get; set; } = false;

    // ── กองทุนเงินทดแทน (กท.20ก, พ.ร.บ.เงินทดแทน §44) ──
    // นายจ้างฝ่ายเดียวสมทบ 0.2%–1.0% ของค่าจ้างต่อปี (cap 240,000 บาท/คน/ปี)
    // อัตราตามประเภทกิจการ 10 หมวด: สำนักงาน 0.2%, ค้าปลีก 0.4%, ก่อสร้าง 1.0%
    // เปิด `WorkersCompensationEnabled` ตามที่บริษัทอยู่ในประกาศกระทรวงแรงงาน
    // → ระบบคิดรายเดือนลง Dr 54121 / Cr 21816 ในรอบเงินเดือน → ยอดสรุปยื่น
    // กท.20ก รายปี (มี.ค.). default ปิดไว้ (บริษัท SME ส่วนใหญ่ไม่ได้ลงทะเบียน
    // จนกว่ามีลูกจ้าง) เพื่อไม่ทำให้ฐานข้อมูลเดิมโดน double-post.
    public decimal WorkersCompensationRatePercent { get; set; } = 0.2m;
    public bool WorkersCompensationEnabled { get; set; } = false;

    // Landing Page – Accounting Services
    public string? LandingContactPhone { get; set; }           // เบอร์ติดต่อแสดงหน้าแรก
    public string? LandingContactLine { get; set; }            // LINE ID
    public string? LandingContactEmail { get; set; }           // อีเมลติดต่อ
    public string? LandingServicesJson { get; set; }           // JSON array of accounting service packages

    // ───── Owner-level feature & menu overrides ─────
    // Subscription.EnabledFeatures says "what the PLAN allows"; the two
    // columns below let the OWNER opt out of features / hide menu items
    // they don't use, on top of the plan. SystemAdmin still has the
    // final say via Subscription — these are subtractive only.
    //
    // OwnerDisabledFeatures: a FeatureFlags bitmask of features the
    // owner has switched off. Effective features =
    //     Subscription.EnabledFeatures & ~OwnerDisabledFeatures
    // Default 0 = nothing disabled (every paid feature behaves as today).
    public FeatureFlags OwnerDisabledFeatures { get; set; } = FeatureFlags.None;

    /// <summary>JSON array of menu nav ids the owner has hidden from
    /// the sidebar. Independent of feature flags — even when the
    /// underlying feature is on, the menu disappears for everyone in
    /// this company. Used when the owner doesn't want a specific
    /// nav entry visible (e.g. "ปฏิทินการลา" for a small team that
    /// doesn't need it). NULL or "[]" = nothing hidden.</summary>
    public string? OwnerHiddenMenuIdsJson { get; set; }
}
