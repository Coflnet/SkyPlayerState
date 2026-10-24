using System;
using System.Security.Cryptography;
using System.Text;
using MessagePack;
using Coflnet.Sky.PlayerState.Services;

namespace Coflnet.Sky.PlayerState.Bazaar;

/// <summary>
/// One observation of a player's bazaar order (placed, filled, cancelled, claimed, seen in a menu, ...).
/// Keys are mirrored by the consumer in another repository, do not renumber.
/// <para>
/// Unlike <see cref="BazaarFlipEvent"/> (which divides stored tenths by 10), <see cref="PricePerUnit"/> here is
/// the value exactly as stored on <see cref="Offer.PricePerUnit"/>, which is already in whole coins.
/// </para>
/// </summary>
[MessagePackObject]
public class BazaarOrderLifecycleEvent
{
    public const string SourceChatSetup = "chat-setup";
    public const string SourceChatFilled = "chat-filled";
    public const string SourceChatCancelled = "chat-cancelled";
    public const string SourceChatClaim = "chat-claim";
    public const string SourceFlipDetected = "flip-detected";
    public const string SourceGuiView = "gui-view";
    public const string SourceGuiFullResend = "gui-full-resend";

    public const string StateOpen = "Open";
    public const string StateFilled = "Filled";
    public const string StateCancelled = "Cancelled";
    public const string StateExpired = "Expired";
    public const string StateClaimed = "Claimed";
    public const string StateClosed = "Closed";

    [Key(0)] public int SchemaVersion { get; set; } = 1;
    /// <summary>Lowercase sha256 hex of user, player, tag, side, created ticks, revision, observed ticks and source.</summary>
    [Key(1)] public string EventId { get; set; }
    [Key(2)] public Guid PlayerUuid { get; set; }
    [Key(3)] public string UserId { get; set; }
    [Key(4)] public string ItemTag { get; set; }
    [Key(5)] public string ItemName { get; set; }
    [Key(6)] public bool IsSell { get; set; }
    [Key(7)] public DateTime Created { get; set; }
    [Key(8)] public long Amount { get; set; }
    [Key(9)] public long FilledAmount { get; set; }
    [Key(10)] public long? ClaimedAmount { get; set; }
    [Key(11)] public long PendingObservedClaims { get; set; }
    /// <summary>Coins per unit as stored on <see cref="Offer"/> (NOT divided by 10).</summary>
    [Key(12)] public double PricePerUnit { get; set; }
    [Key(13)] public bool IsExpired { get; set; }
    [Key(14)] public DateTime? ClosedAt { get; set; }
    [Key(15)] public DateTime ObservedAt { get; set; }
    [Key(16)] public string Source { get; set; }
    [Key(17)] public string State { get; set; }
    /// <summary>Fingerprint of the order contents, see <see cref="BazaarOrderSync.Revision"/>.</summary>
    [Key(18)] public long Revision { get; set; }

    public static string ComputeEventId(string userId, Guid playerUuid, string itemTag, bool isSell, DateTime created, long revision, DateTime observedAt, string source)
    {
        var text = $"{userId}|{playerUuid}|{itemTag}|{isSell}|{created.Ticks}|{revision}|{observedAt.Ticks}|{source}";
        return Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(text))).ToLowerInvariant();
    }

    /// <summary>
    /// Builds the event, or returns null when it must not be emitted (logged out, unknown player, unresolved tag).
    /// </summary>
    public static BazaarOrderLifecycleEvent TryCreate(UpdateArgs args, Offer offer, string source, string state, string itemTagOverride = null)
    {
        if (string.IsNullOrEmpty(args.msg.UserId))
            return null;
        var tag = string.IsNullOrWhiteSpace(itemTagOverride) ? offer.ItemTag : itemTagOverride;
        if (string.IsNullOrWhiteSpace(tag))
            return null;
        var uuid = args.currentState.McInfo?.Uuid ?? Guid.Empty;
        if (uuid == Guid.Empty)
            return null;
        var observedAt = args.msg.ReceivedAt;
        var closedAt = offer.ClosedAt ?? (state is StateCancelled or StateClosed ? observedAt : null);
        var revision = BazaarOrderSync.Revision(args, offer, tag, closedAt);
        return new BazaarOrderLifecycleEvent
        {
            EventId = ComputeEventId(args.msg.UserId, uuid, tag, offer.IsSell, offer.Created, revision, observedAt, source),
            PlayerUuid = uuid,
            UserId = args.msg.UserId,
            ItemTag = tag,
            ItemName = offer.ItemName,
            IsSell = offer.IsSell,
            Created = offer.Created,
            Amount = offer.Amount,
            FilledAmount = offer.FilledAmount,
            ClaimedAmount = offer.ClaimedAmount,
            PendingObservedClaims = offer.PendingObservedClaims,
            PricePerUnit = offer.PricePerUnit,
            IsExpired = offer.IsExpired,
            ClosedAt = closedAt,
            ObservedAt = observedAt,
            Source = source,
            State = state,
            Revision = revision
        };
    }
}
