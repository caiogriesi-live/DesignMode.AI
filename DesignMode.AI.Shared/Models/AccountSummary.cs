namespace DesignMode.AI.Shared.Models;

public sealed record AccountSummary(string Username, string Plan, string Status, bool CanCancel, bool CancellationScheduled, int SubscriptionSignupCount = 0);
