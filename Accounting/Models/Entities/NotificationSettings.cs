namespace Accounting.Models.Entities;

/// <summary>
/// Per-company configuration matrix: for each (event, recipient role)
/// pair, which channels are enabled? Drives the centralized
/// NotificationEngine. When no row exists for a (company, event, role)
/// combination the engine treats it as "all channels off" — admins
/// must explicitly opt-in.
/// </summary>
public class NotificationSetting : TenantEntity
{
    /// <summary>Event key from <see cref="Constants.NotificationEvents"/>
    /// (e.g. "leave.submitted"). Free-form string so new events can be
    /// added without an enum migration.</summary>
    public string EventKey { get; set; } = "";

    /// <summary>Logical recipient role from
    /// <see cref="Constants.NotificationRecipientRoles"/> (Requester /
    /// DirectManager / HrAdmin etc.) — resolved to concrete user IDs
    /// at dispatch time.</summary>
    public string RecipientRole { get; set; } = "";

    public bool EnableSystem { get; set; } = false;
    public bool EnableEmail { get; set; } = false;
    public bool EnableLine { get; set; } = false;
}

/// <summary>
/// Per-user override of the company-level NotificationSetting.
/// "Mute Email notifications for leaves, use LINE only" — sets the
/// matching row with SuppressEmail = true. Suppression is the only
/// operation a user can do; they cannot grant themselves channels
/// the company hasn't enabled. Missing row means "use company defaults".
/// </summary>
public class NotificationPreference : TenantEntity
{
    public Guid UserId { get; set; }
    public User User { get; set; } = null!;

    public string EventKey { get; set; } = "";

    public bool SuppressSystem { get; set; } = false;
    public bool SuppressEmail { get; set; } = false;
    public bool SuppressLine { get; set; } = false;
}
