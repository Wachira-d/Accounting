using Accounting.Models.Enums;

namespace Accounting.Models.Entities;

/// <summary>
/// Table reservation. Restaurants use it to hold a table for a customer at a
/// specific time. Links to PosTable when a specific table is pre-assigned,
/// otherwise the table is picked on arrival.
///
/// Status flow: Pending → Confirmed (host confirmed by phone/LINE) → Seated
/// (customer arrived, order opened) → Completed (bill paid). Cancelled and
/// NoShow are terminal states reported for analytics.
/// </summary>
public class PosReservation : TenantEntity
{
    /// <summary>Specific table pre-assigned, or null for "any suitable table".</summary>
    public Guid? TableId { get; set; }
    public PosTable? Table { get; set; }
    /// <summary>Denormalized for fast list rendering — kept in sync via service layer.</summary>
    public string? TableNumber { get; set; }

    /// <summary>Optional link to a Contact for loyalty points + history. When
    /// null we still keep the contact info as free-text below.</summary>
    public Guid? ContactId { get; set; }
    public Contact? Contact { get; set; }

    public string CustomerName { get; set; } = null!;
    public string? Phone { get; set; }
    public string? Email { get; set; }

    public int PartySize { get; set; } = 2;

    /// <summary>Start time of the reservation.</summary>
    public DateTime ReservedAt { get; set; }

    /// <summary>How long the table is held. Defaults to 90 min for fine dining,
    /// 60 for fast turn, 120 for special-occasion bookings.</summary>
    public int DurationMinutes { get; set; } = 90;

    public ReservationStatus Status { get; set; } = ReservationStatus.Pending;

    /// <summary>If the customer arrived and we opened a POS order, we link it
    /// here so the host can flip status → Completed straight from the order.</summary>
    public Guid? PosOrderId { get; set; }

    public string? Notes { get; set; }                  // เช่น "ทานเค้กวันเกิด", "ขอที่เงียบ"
    public string? Source { get; set; } = "Walk-in";    // "LINE", "Phone", "Web", "Walk-in"

    /// <summary>How many times we tried to remind the customer (LINE / email).
    /// 0 = no reminder yet.</summary>
    public int ReminderCount { get; set; } = 0;
    public DateTime? LastReminderAt { get; set; }
}
