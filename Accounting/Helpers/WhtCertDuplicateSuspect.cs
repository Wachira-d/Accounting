using System.Linq.Expressions;
using Accounting.Models.DTOs.Tax;
using Accounting.Models.Entities;

namespace Accounting.Helpers;

/// <summary>
/// **เจ้าของตัวเดียวของ "อาจนับซ้ำ" บนหน้า 50 ทวิ** (รอบ 271 · ฝ่ายค้าน M5) — ตัวกรอง · การ์ด · ป้ายต่อแถว ใช้คู่เงื่อนไข <see cref="IsPeer"/> ตัวนี้ตัวเดียว
/// (เดิมมีสองสำเนา: Expression ของตัวกรอง + ลูปในหน่วยความจำของป้าย) · อ่านอย่างเดียว ไม่ประทับสถานะใด (หลักการ #3)
///
/// <para>ใบ W "อาจนับซ้ำ" กับใบ Y เมื่อทั้งคู่ไม่ยกเลิก และ:</para>
/// <list type="bullet">
/// <item><b>(ก) ใบมือคู่ใบที่ผูกเอกสาร</b> — W ไม่ผูกเอกสาร/รอบเงินเดือน และยังไม่ยืนยันว่า "ไม่มีใบสำคัญจ่าย" (D5 ปิดป้ายได้) · Y ผูกเอกสาร ·
/// แบบ + ภาษีเท่ากัน · ผู้ถูกหักเดียวกัน <b>หรือ</b> ผู้ติดต่อซ้ำ (เลขภาษี + สาขาเดียวกัน) · เดือนภาษีห่างกันไม่เกิน 1 เดือน (ใบมือลงเดือนอื่น — ที่มาของ R7)</item>
/// <item><b>(ข) สองใบบนการจ่ายเดียวกัน</b> — W และ Y ผูกเอกสารเดียวกัน งวดจ่ายเดียวกัน ผู้ถูกหักเดียวกัน (ยอดใดก็ได้) — นับซ้ำแน่นอน</item>
/// </list>
/// <para>ทิศตรงข้าม: ใบที่ผูกเอกสารต่างใบกัน (การจ่ายสองครั้งยอดเท่ากัน แต่ละครั้งมีใบสำคัญจ่ายของตัวเอง) <b>ไม่</b>ขึ้นป้าย</para>
/// </summary>
public static class WhtCertDuplicateSuspect
{
    private const string HeadOffice = "00000";

    /// <summary>คู่เงื่อนไขตัวเดียว (W = ใบที่ถูกตรวจ · Y = คู่) · <paramref name="contacts"/> = ขอบเขตผู้ติดต่อของบริษัท (ใช้เทียบเลขภาษี+สาขา)</summary>
    internal static Expression<Func<WithholdingTaxCert, WithholdingTaxCert, bool>> IsPeer(IQueryable<Contact> contacts) => (w, y) =>
        y.CompanyId == w.CompanyId && y.Id != w.Id && !y.IsDeleted
        && w.Status != WithholdingTaxCertStatus.Voided && y.Status != WithholdingTaxCertStatus.Voided
        && (
            // (ก) ใบมือ ↔ ใบที่ผูกเอกสาร
            (w.DocumentId == null && w.SourcePayrollRunId == null && w.NoSourceDocumentReason == null && y.DocumentId != null
                && y.TaxFormType == w.TaxFormType && y.TotalTaxAmount == w.TotalTaxAmount
                && (y.TaxYear * 12 + y.TaxMonth) - (w.TaxYear * 12 + w.TaxMonth) <= 1
                && (y.TaxYear * 12 + y.TaxMonth) - (w.TaxYear * 12 + w.TaxMonth) >= -1
                && (y.PayeeContactId == w.PayeeContactId
                    || contacts.Any(c1 => c1.Id == w.PayeeContactId && c1.TaxId != null && c1.TaxId != ""
                        && contacts.Any(c2 => c2.Id == y.PayeeContactId && c2.TaxId == c1.TaxId
                            && (c2.BranchCode ?? HeadOffice) == (c1.BranchCode ?? HeadOffice)))))
            ||
            // (ข) สองใบบนการจ่ายเดียวกัน
            (w.DocumentId != null && y.DocumentId == w.DocumentId && y.SourcePaymentId == w.SourcePaymentId
                && y.PayeeContactId == w.PayeeContactId)
        );

    /// <summary>ตัวกรอง/การ์ด: W มีคู่อย่างน้อยหนึ่งใบใน <paramref name="certs"/> (ขอบเขตบริษัทเดียวกัน)</summary>
    public static Expression<Func<WithholdingTaxCert, bool>> Predicate(IQueryable<WithholdingTaxCert> certs, IQueryable<Contact> contacts)
    {
        var peer = IsPeer(contacts);
        var w = peer.Parameters[0];
        var yLambda = Expression.Lambda<Func<WithholdingTaxCert, bool>>(peer.Body, peer.Parameters[1]);
        var any = Expression.Call(typeof(Queryable), nameof(Queryable.Any), new[] { typeof(WithholdingTaxCert) },
            certs.Expression, Expression.Quote(yLambda));
        return Expression.Lambda<Func<WithholdingTaxCert, bool>>(any, w);
    }

    /// <summary>ป้ายต่อแถว: คู่ของใบ <paramref name="x"/> (เงื่อนไขเดียวกับ <see cref="Predicate"/>)</summary>
    public static IQueryable<WithholdingTaxCert> PeersOf(IQueryable<WithholdingTaxCert> certs, IQueryable<Contact> contacts, WithholdingTaxCert x)
    {
        var peer = IsPeer(contacts);
        var body = new Bind(peer.Parameters[0], Expression.Constant(x, typeof(WithholdingTaxCert))).Visit(peer.Body)!;
        return certs.Where(Expression.Lambda<Func<WithholdingTaxCert, bool>>(body, peer.Parameters[1]));
    }

    /// <summary>ข้อความป้าย (server computes)</summary>
    public static string Label(string peerNumber, string? peerDocumentNumber, bool samePayment)
        => samePayment
            ? $"⚠️ นับซ้ำ: ใบสำคัญจ่าย {peerDocumentNumber ?? "-"} มี 50 ทวิ สองใบ (กับ {peerNumber}) — ยกเลิกใบที่เกินผ่าน \"เปลี่ยน 50 ทวิ ที่ผูก\""
            : $"⚠️ อาจนับซ้ำกับ {peerNumber} ({peerDocumentNumber ?? "-"}) — ผู้ถูกหัก แบบ และภาษีเดียวกัน เดือนใกล้กัน · ถ้าเป็นการจ่ายคนละครั้งที่ไม่มีใบสำคัญจ่าย กด \"ไม่มีใบสำคัญจ่าย\"";

    private sealed class Bind : ExpressionVisitor
    {
        private readonly ParameterExpression _p;
        private readonly Expression _with;
        public Bind(ParameterExpression p, Expression with) { _p = p; _with = with; }
        protected override Expression VisitParameter(ParameterExpression node) => node == _p ? _with : base.VisitParameter(node);
    }
}
