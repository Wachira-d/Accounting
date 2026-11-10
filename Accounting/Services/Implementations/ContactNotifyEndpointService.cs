using System.Text.Json;
using Accounting.Data;
using Accounting.Helpers;
using Accounting.Models.DTOs.Notification;
using Accounting.Models.Entities;
using Accounting.Models.Enums;
using Accounting.Services.Interfaces;
using Microsoft.EntityFrameworkCore;

namespace Accounting.Services.Implementations;

/// <summary>ดู <see cref="IContactNotifyEndpointService"/> — รอบ 245 L1 เขียนได้เฉพาะ<b>อีเมล</b> (เพิ่ม/แก้/ยกเลิก) + เรื่องที่อีเมลหลักรับ ·
/// ปลายทาง LINE สร้างได้เฉพาะเส้นผูกด้วยรหัส/ความยินยอม (เฟส L3/L5) — ที่นี่แก้เรื่อง/ป้าย/แสดงยอด และยกเลิกได้เท่านั้น ·
/// คำตัดสินทุกข้อมาจาก <see cref="ContactRecipientRule"/> + <see cref="NotifyTopics"/> (ไม่มีเงื่อนไขซ้ำที่นี่)</summary>
public class ContactNotifyEndpointService : IContactNotifyEndpointService
{
    private readonly AccountingDbContext _db;
    private readonly IContactRecipientResolver _resolver;

    /// <summary>รอบ 254 WP-11 L3 (Q8): ยกเลิกกลุ่มแล้วไม่เหลือการผูก ⇒ บอทออกจากกลุ่ม · null (เทสต์/นอก DI) ⇒ ไม่ออก</summary>
    private readonly IContactLineGroupService? _groups;

    public ContactNotifyEndpointService(AccountingDbContext db, IContactRecipientResolver resolver, IContactLineGroupService? groups = null)
    {
        _db = db; _resolver = resolver; _groups = groups;
    }

    public async Task<ContactNotifyOverviewResponse> AddEmailAsync(Guid companyId, Guid contactId, AddContactNotifyEmailRequest request, ContactNotifyActor actor, CancellationToken ct = default)
    {
        await using (var tx = await _db.Database.BeginTransactionAsync(ct))
        {
            var contact = await LockContactAsync(companyId, contactId, ct);
            var active = await ActiveRows(companyId, contactId).Where(e => e.Kind == NotifyEndpointKind.Email && !e.IsPrimary)
                .Select(e => e.Email).ToListAsync(ct);
            var (email, problem, status) = ContactRecipientRule.ValidateNewEmail(request?.Email, contact.Email, active);
            if (problem != null) throw new BusinessRuleException(problem, "NOTIFY-CONTACT-EMAIL", status);
            var (label, labelProblem) = ContactRecipientRule.NormalizeLabel(request?.Label);
            if (labelProblem != null) throw new BusinessRuleException(labelProblem, "NOTIFY-CONTACT-LABEL", 400);
            IReadOnlyList<NotifyTopic> topics;
            if (request?.Topics == null) topics = NotifyTopics.DefaultsFor(NotifyEndpointKind.Email);
            else
            {
                var (parsed, topicProblem) = NotifyTopics.FromRequest(request.Topics, allowEmpty: false);
                if (topicProblem != null) throw new BusinessRuleException(topicProblem, "NOTIFY-CONTACT-TOPIC", 400);
                topics = parsed!;
            }
            var now = DateTime.UtcNow;
            var row = new ContactNotifyEndpoint
            {
                CompanyId = companyId,
                ContactId = contactId,
                Kind = NotifyEndpointKind.Email,
                Email = email,
                Label = label,
                TopicsJson = NotifyTopics.Serialize(topics),
                Source = NotifyEndpointKind.SourceManual,
                BoundByUserId = actor.UserId,
                LinkedAt = now,
                CreatedAt = now,
                CreatedBy = actor.Email,
            };
            _db.ContactNotifyEndpoints.Add(row);
            // ฝ่ายค้าน S3 (PDPA): audit append-only ⇒ ไม่ใส่อีเมล/ป้ายเปล่า — อ้าง endpointId (ค่าอยู่ในแถวปลายทางที่ลบตามคำขอได้)
            Audit(companyId, contactId, actor, AuditAction.Create, "add-email", null,
                // ฝ่ายค้าน WP-08 LOW: แฮชของที่อยู่ (ผูกบริษัท) — พิสูจน์ได้ภายหลังว่าเพิ่มอีเมลใด (เทียบแฮช) โดยไม่เก็บอีเมลเปล่าใน audit ที่ลบไม่ได้
                new { endpointId = row.Id, kind = row.Kind, hasLabel = label != null, topics = topics.Select(t => t.ToString()),
                      emailHash = ContactRecipientRule.EmailAuditHash(companyId, email!) });
            await _db.SaveChangesAsync(ct);
            await tx.CommitAsync(ct);
        }
        return await OverviewAsync(companyId, contactId, ct);
    }

    public async Task<ContactNotifyOverviewResponse> UpdateAsync(Guid companyId, Guid contactId, Guid endpointId, UpdateContactNotifyEndpointRequest request, ContactNotifyActor actor, CancellationToken ct = default)
    {
        await using (var tx = await _db.Database.BeginTransactionAsync(ct))
        {
            await LockContactAsync(companyId, contactId, ct);
            var row = await _db.ContactNotifyEndpoints
                .FirstOrDefaultAsync(e => e.Id == endpointId && e.CompanyId == companyId && e.ContactId == contactId, ct)
                ?? throw new BusinessRuleException("ไม่พบปลายทางนี้ของผู้ติดต่อ", "NOTIFY-CONTACT-NOT-FOUND", 404);
            if (row.UnlinkedAt != null)
                throw new BusinessRuleException("ปลายทางนี้ยกเลิกแล้ว — แก้ไขไม่ได้ (เพิ่มใหม่แทน)", "NOTIFY-CONTACT-UNLINKED", 409);
            if (row.IsPrimary)
                throw new BusinessRuleException("แถวอีเมลหลักตั้งเรื่องที่รับผ่านคำสั่งอีเมลหลัก", "NOTIFY-CONTACT-PRIMARY", 400);
            var before = new { topics = row.TopicsJson, showAmounts = row.ShowAmounts };
            var labelBefore = row.Label;
            if (request?.Label != null)
            {
                var (label, labelProblem) = ContactRecipientRule.NormalizeLabel(request.Label);
                if (labelProblem != null) throw new BusinessRuleException(labelProblem, "NOTIFY-CONTACT-LABEL", 400);
                row.Label = label;
            }
            if (request?.Topics != null)
            {
                var (parsed, topicProblem) = NotifyTopics.FromRequest(request.Topics, allowEmpty: false);
                if (topicProblem != null) throw new BusinessRuleException(topicProblem, "NOTIFY-CONTACT-TOPIC", 400);
                row.TopicsJson = NotifyTopics.Serialize(parsed!);
            }
            if (request?.ShowAmounts is bool show)
            {
                // ห้าม silent no-op (กฎ #4 A): ช่องที่ชนิดนี้ไม่มีผล ⇒ ปฏิเสธพร้อมเหตุ
                if (row.Kind != NotifyEndpointKind.LineGroup)
                    throw new BusinessRuleException("\"แสดงยอดเงิน\" ใช้ได้กับกลุ่ม LINE เท่านั้น", "NOTIFY-CONTACT-SHOW-AMOUNTS", 400);
                row.ShowAmounts = show;
            }
            row.UpdatedAt = DateTime.UtcNow;
            row.UpdatedBy = actor.Email;
            Audit(companyId, contactId, actor, AuditAction.Update, "update", before,
                new { endpointId = row.Id, kind = row.Kind, labelChanged = !string.Equals(labelBefore, row.Label, StringComparison.Ordinal),
                    topics = row.TopicsJson, showAmounts = row.ShowAmounts });
            await _db.SaveChangesAsync(ct);
            await tx.CommitAsync(ct);
        }
        return await OverviewAsync(companyId, contactId, ct);
    }

    public async Task<ContactNotifyOverviewResponse> UpdatePrimaryAsync(Guid companyId, Guid contactId, UpdateContactNotifyPrimaryRequest request, ContactNotifyActor actor, CancellationToken ct = default)
    {
        await using (var tx = await _db.Database.BeginTransactionAsync(ct))
        {
            await LockContactAsync(companyId, contactId, ct);
            var (parsed, topicProblem) = NotifyTopics.FromRequest(request?.Topics, allowEmpty: true);
            if (topicProblem != null) throw new BusinessRuleException(topicProblem, "NOTIFY-CONTACT-TOPIC", 400);
            var json = NotifyTopics.Serialize(parsed!);
            var now = DateTime.UtcNow;
            // ฝ่ายค้าน S4: ตัวเลือกแถวหลักเดียวกับตัวหาผู้รับ (ผูกก่อนชนะ · ไม่ใช่ตาม Guid) · แถวหลักเกิน (ข้อมูลก่อนแก้การรวม) ⇒ ยกเลิก (เหตุ merge)
            var primaries = await ActiveRows(companyId, contactId).Where(e => e.IsPrimary && e.Kind == NotifyEndpointKind.Email).ToListAsync(ct);
            var row = ContactRecipientRule.PickPrimary(primaries, e => e.LinkedAt, e => e.CreatedAt);
            foreach (var extra in primaries.Where(p => !ReferenceEquals(p, row)))
                NotifyEndpointKind.MarkUnlinked(extra, NotifyEndpointKind.UnlinkMerge, now, actor.Email);   // ฝ่ายค้าน fix2 M3: ตัวยกเลิกเดียว
            var before = row?.TopicsJson;
            if (row == null)
            {
                row = new ContactNotifyEndpoint
                {
                    CompanyId = companyId,
                    ContactId = contactId,
                    Kind = NotifyEndpointKind.Email,
                    IsPrimary = true,
                    Email = null,   // ที่อยู่อ่านสดจาก Contact.Email เสมอ (ไม่เก็บซ้ำ)
                    TopicsJson = json,
                    Source = NotifyEndpointKind.SourceContactEmail,
                    BoundByUserId = actor.UserId,
                    LinkedAt = now,
                    CreatedAt = now,
                    CreatedBy = actor.Email,
                };
                _db.ContactNotifyEndpoints.Add(row);
            }
            else
            {
                row.TopicsJson = json;
                row.UpdatedAt = now;
                row.UpdatedBy = actor.Email;
            }
            Audit(companyId, contactId, actor, AuditAction.Update, "primary-topics", before == null ? null : new { topics = before },
                new { endpointId = row.Id, topics = json });
            await _db.SaveChangesAsync(ct);
            await tx.CommitAsync(ct);
        }
        return await OverviewAsync(companyId, contactId, ct);
    }

    public async Task<ContactNotifyOverviewResponse> UnlinkAsync(Guid companyId, Guid contactId, Guid endpointId, ContactNotifyActor actor, CancellationToken ct = default)
    {
        string? groupProtected = null, groupHash = null, groupScope = null;
        await using (var tx = await _db.Database.BeginTransactionAsync(ct))
        {
            await LockContactAsync(companyId, contactId, ct);
            var row = await _db.ContactNotifyEndpoints
                .FirstOrDefaultAsync(e => e.Id == endpointId && e.CompanyId == companyId && e.ContactId == contactId, ct)
                ?? throw new BusinessRuleException("ไม่พบปลายทางนี้ของผู้ติดต่อ", "NOTIFY-CONTACT-NOT-FOUND", 404);
            if (row.IsPrimary)
                throw new BusinessRuleException("อีเมลหลักยกเลิกที่นี่ไม่ได้ — แก้/ลบอีเมลที่แท็บข้อมูลทั่วไป หรือเอาเครื่องหมายเรื่องที่รับออกทั้งหมด",
                    "NOTIFY-CONTACT-PRIMARY", 400);
            if (row.UnlinkedAt != null)
                throw new BusinessRuleException("ปลายทางนี้ยกเลิกไปแล้ว", "NOTIFY-CONTACT-UNLINKED", 409);
            var now = DateTime.UtcNow;
            if (row.Kind == NotifyEndpointKind.LineGroup) { groupProtected = row.LineGroupId; groupHash = row.LineIdHash; groupScope = row.OaScope; }
            // ส่งให้น้อยที่สุด (PDPA ม.22): ยกเลิกแล้วไม่ใช้ id อีก ⇒ ล้างค่าที่เข้ารหัส + ป้ายจาก LINE + ชื่อกลุ่ม (คงแฮชไว้เป็นร่องรอยว่าเคยผูก) — ตัวยกเลิกเดียว
            NotifyEndpointKind.MarkUnlinked(row, NotifyEndpointKind.UnlinkCompany, now, actor.Email);
            // รอบ 257 ชุด 4 (C LOW · PDPA ม.19 วรรคห้า): ยกเลิกปลายทางจากบริษัท ⇒ ความยินยอมของปลายทางนี้หมดการใช้ ⇒ ประทับ WithdrawnAt (เดิมค้าง "ยังยินยอม" ·
            // เส้น "หยุด" ของผู้รับประทับอยู่แล้ว ContactLineBindingService) · ความยินยอมที่ปลายทางอื่นที่ยังใช้งานอ้างอยู่ไม่แตะ ·
            // ฝ่ายค้าน LOW-5: ExecuteUpdateAsync วิ่งในธุรกรรม tx เดียวกับการยกเลิก (EF ใช้ธุรกรรมที่เปิดอยู่) — commit/ล้มพร้อมกัน
            if (row.ConsentRecordId is Guid consentId
                && !await _db.ContactNotifyEndpoints.AnyAsync(e => e.CompanyId == companyId && e.Id != row.Id && e.ConsentRecordId == consentId && e.UnlinkedAt == null, ct))
                await _db.PdpaConsentRecords.Where(c => c.CompanyId == companyId && c.Id == consentId && c.WithdrawnAt == null)
                    .ExecuteUpdateAsync(x => x.SetProperty(c => c.WithdrawnAt, (DateTime?)now).SetProperty(c => c.WithdrawnReason, NotifyEndpointKind.ConsentWithdrawnByCompany), ct);
            Audit(companyId, contactId, actor, AuditAction.Delete, "unlink", null,
                new { endpointId = row.Id, kind = row.Kind, reason = row.UnlinkReason });
            await _db.SaveChangesAsync(ct);
            await tx.CommitAsync(ct);
        }
        // Q8: ไม่เหลือการผูกของกลุ่มนี้กับบริษัทนี้ ⇒ บอทออกจากกลุ่ม (หลัง commit — ออกไม่สำเร็จไม่ย้อนการยกเลิก)
        var leaveNote = _groups != null && groupHash != null ? await _groups.LeaveIfUnboundAsync(companyId, groupProtected, groupHash, ct, groupScope) : null;
        var overview = await OverviewAsync(companyId, contactId, ct);
        return leaveNote == null ? overview : overview with { ActionNote = leaveNote };   // รอบ 256-2 L2: บอทกลางยังอยู่ในกลุ่ม ⇒ บอกทางเอาออก (ไม่เงียบ)
    }

    private IQueryable<ContactNotifyEndpoint> ActiveRows(Guid companyId, Guid contactId)
        => _db.ContactNotifyEndpoints.Where(e => e.CompanyId == companyId && e.ContactId == contactId && e.UnlinkedAt == null);

    /// <summary>ล็อกต่อผู้ติดต่อ (สองคำขอพร้อมกันเพิ่มอีเมลเดียวกัน/แถวหลักสองแถว) แล้วโหลดผู้ติดต่อของบริษัทนี้ — ไม่พบ = 404 (tenant)</summary>
    private async Task<Contact> LockContactAsync(Guid companyId, Guid contactId, CancellationToken ct)
    {
        await _db.Database.ExecuteSqlRawAsync("SELECT pg_advisory_xact_lock({0})",
            new object[] { AdvisoryLockKey.For(companyId, AdvisoryLockKey.ContactNotifyEndpoint, contactId.ToString()) }, ct);
        return await _db.Contacts.AsNoTracking().FirstOrDefaultAsync(c => c.Id == contactId && c.CompanyId == companyId, ct)
            ?? throw new BusinessRuleException("ไม่พบผู้ติดต่อ", "NOTIFY-CONTACT-NOT-FOUND", 404);
    }

    private void Audit(Guid companyId, Guid contactId, ContactNotifyActor actor, AuditAction action, string op, object? before, object after)
    {
        _db.AddChainedAuditLog(new AuditLog
        {
            CompanyId = companyId,
            UserId = actor.UserId,
            UserEmail = actor.Email,
            Action = action,
            EntityType = ContactRecipientRule.AuditEntityType,
            EntityId = contactId.ToString(),
            OldValues = before == null ? null : JsonSerializer.Serialize(before),
            NewValues = JsonSerializer.Serialize(new { op, change = after }),
            IpAddress = actor.IpAddress,
            Timestamp = DateTime.UtcNow,
        });
    }

    private async Task<ContactNotifyOverviewResponse> OverviewAsync(Guid companyId, Guid contactId, CancellationToken ct)
        => await _resolver.GetOverviewAsync(companyId, contactId, canEdit: true, ct)
            ?? throw new BusinessRuleException("ไม่พบผู้ติดต่อ", "NOTIFY-CONTACT-NOT-FOUND", 404);
}
