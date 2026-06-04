namespace Accounting.Models.DTOs.Bank;

/// <summary>One resolved counterpart of a matched bank transaction.
/// ItemType is the clean type ("Payment" / "JournalEntry" / "Document");
/// Amount is the value that counts toward the bank line — Payment.Amount,
/// a JournalEntry's NET movement on the bank's own GL account, a Document's
/// total, or (inside an M:N group) the item's signed AllocatedAmount.</summary>
public sealed record ResolvedCounterpartDto(
    string ItemType, Guid ItemId, string Label, System.DateTime? Date, decimal Amount);

/// <summary>The single source of truth for "what did this bank line match to,
/// and does it add up?". Every view (popup, issues list, reconcile detail,
/// the save-time guard) resolves through the same logic so they can never
/// disagree again.</summary>
public sealed record ResolvedMatchDto(
    Guid TxnId,
    System.DateTime Date,
    string Type,
    string? Description,
    decimal BankAmount,
    decimal MatchedAmount,
    bool AmountsAgree,
    bool HasMissingCounterpart,
    bool IsGroup,
    string? GroupNumber,
    System.Collections.Generic.List<ResolvedCounterpartDto> Counterparts);
