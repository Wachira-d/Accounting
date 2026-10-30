using Accounting.Data;
using Accounting.Helpers;
using Accounting.Models.Enums;
using Microsoft.EntityFrameworkCore;

namespace Accounting.Services.Implementations;

/// <summary>ช่องทางชำระค่าบริการ NextAcc ที่หน้าจ่ายแสดง — เซิร์ฟเวอร์ตัดสิน หน้าเว็บแสดงอย่างเดียว</summary>
/// <param name="Online">เปิดชำระออนไลน์ (PromptPay/บัตร ผ่าน gateway ของ NextAcc) ได้แล้วหรือยัง</param>
/// <param name="OnlineNote">เหตุที่ยังไม่เปิด (แสดงให้ผู้จ่ายเห็น — ทางไปต่อ = โอน+แนบสลิป)</param>
public sealed record PlatformPayOptions(bool Online, string? OnlineNote, string? BankTransferInstructions, string? FallbackNote);

/// <summary>ผลการเริ่มชำระออนไลน์ — URL ไปหน้าชำระของ gateway หรือ QR</summary>
public sealed record PlatformOnlinePayStart(Guid IntentId, string? RedirectUrl, string? QrImageUrl, decimal Amount);

/// <summary>
/// รอบ 219 เฟส 2 — <b>ช่องเสียบ (hook) ของการชำระออนไลน์ค่าบริการ NextAcc</b> · งานจริง (intent ใต้บริษัทผู้ให้บริการ + <c>PayerCompanyId</c> +
/// Omise ของ NextAcc · <c>IPlatformPaymentService</c>) เป็นของทีม part B ที่ทำขนานกัน ⇒ ศูนย์บิล/ลิงก์ชำระเรียกผ่าน interface เล็กนี้เท่านั้น ·
/// part B ลงแล้ว = ลงทะเบียน adapter ของตัวเองแทน <see cref="UnconfiguredPlatformOnlinePayment"/> ใน <c>Program.cs</c> (ตัวสุดท้ายที่ลงทะเบียนชนะ)
/// <para>สัญญา: ยอด = <c>BalanceDue</c> ของใบ ณ ตอนกด (เซิร์ฟเวอร์คำนวณ) · intent <c>SourceKind = Document</c>, <c>SourceId = ใบแจ้งหนี้</c> ใน tenant
/// ผู้ให้บริการ · เงินเข้า ⇒ <c>DocumentPaymentHandler</c> ตัดชำระใบเดิม (ไม่ออกขายสดใบใหม่) · ห้ามใช้ gateway ของร้าน (คำตัดสินข้อ 178 ค)</para>
/// </summary>
public interface IPlatformOnlinePaymentPort
{
    /// <summary>(เปิดใช้ได้ไหม, เหตุที่ยังไม่เปิด)</summary>
    Task<(bool Available, string? Note)> AvailabilityAsync(CancellationToken ct = default);

    Task<PlatformOnlinePayStart> StartInvoicePaymentAsync(Guid buyerCompanyId, Guid invoiceDocumentId, PaymentMethodKind method,
        string? returnUrl, string actor, CancellationToken ct = default);
}

/// <summary>ค่าตั้งต้นจนกว่า part B ลง: ยังไม่มีช่องออนไลน์ของ NextAcc (Omise ของ NextAcc ยังไม่ตั้ง — คำตัดสินเจ้าของข้อ 178 ⑨) ⇒ โอน + แนบสลิป</summary>
public sealed class UnconfiguredPlatformOnlinePayment : IPlatformOnlinePaymentPort
{
    public const string Note = "ยังไม่เปิดชำระค่าบริการ NextAcc ออนไลน์ — กรุณาโอนเงินตามรายละเอียดแล้วแนบสลิป ผู้ดูแลระบบจะตรวจสอบและยืนยันให้";

    public Task<(bool Available, string? Note)> AvailabilityAsync(CancellationToken ct = default)
        => Task.FromResult<(bool, string?)>((false, Note));

    public Task<PlatformOnlinePayStart> StartInvoicePaymentAsync(Guid buyerCompanyId, Guid invoiceDocumentId, PaymentMethodKind method,
        string? returnUrl, string actor, CancellationToken ct = default)
        => throw new BusinessRuleException(Note, "PLATFORM-ONLINE-PAY-UNAVAILABLE", 409);
}

/// <summary>ตัวประกอบ "ช่องทางชำระค่าบริการ NextAcc" ตัวเดียว (ศูนย์บิล · ลิงก์ชำระ · ส่วนเสริม) — วิธีโอนจาก
/// <c>SiteSettings.PlatformBankTransferInstructions</c> ที่แอดมินตั้ง + ช่องออนไลน์จาก <see cref="IPlatformOnlinePaymentPort"/></summary>
public interface IPlatformPayOptionsService
{
    Task<PlatformPayOptions> GetAsync(CancellationToken ct = default);
}

public class PlatformPayOptionsService : IPlatformPayOptionsService
{
    public const string NoBankFallbackNote = "ยังไม่ได้ประกาศเลขบัญชีรับโอน — กรุณาติดต่อผู้ดูแลระบบ NextAcc ก่อนโอนเงิน";
    private readonly AccountingDbContext _db;
    private readonly IPlatformOnlinePaymentPort _online;

    public PlatformPayOptionsService(AccountingDbContext db, IPlatformOnlinePaymentPort online) { _db = db; _online = online; }

    public async Task<PlatformPayOptions> GetAsync(CancellationToken ct = default)
    {
        var text = await _db.SiteSettings.AsNoTracking().OrderBy(x => x.CreatedAt)
            .Select(x => x.PlatformBankTransferInstructions).FirstOrDefaultAsync(ct);
        var (available, note) = await _online.AvailabilityAsync(ct);
        return new PlatformPayOptions(available, available ? null : note,
            string.IsNullOrWhiteSpace(text) ? null : text.Trim(),
            string.IsNullOrWhiteSpace(text) ? NoBankFallbackNote : null);
    }
}
