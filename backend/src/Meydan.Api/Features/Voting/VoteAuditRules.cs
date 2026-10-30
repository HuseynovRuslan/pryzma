using Meydan.Api.Features.Admin;

namespace Meydan.Api.Features.Voting;

/// <summary>
/// Which approved rule a set-aside vote was decided under, told the same way on every screen — the public audit and the
/// administration's vote analytics alike. D14 marks its votes with its policy id
/// (<see cref="VoteReview.ApplyDisposableEmailAsync"/>); of the D12 decisions, a vote whose account shares its phone
/// with <see cref="VoteSuspicion.PhoneClusterSize"/> or more voting accounts of the competition was set aside under the
/// phone rule, any other under R2 (coordinated identity). Only decisions already taken are classified: a vote that
/// still counts is never given a rule, a risk or a label here.
/// </summary>
public static class VoteAuditRules
{
    /// <summary>
    /// The classifier for one competition, given every vote of it (account and phone, set aside or not). The phone is
    /// read on the server only, to tell the rules apart; it never leaves.
    /// </summary>
    public static Func<string?, string?, VotingAuditReason> Classifier(IEnumerable<(Guid UserId, string? Phone)> competitionVotes)
    {
        var accountsByPhone = competitionVotes
            .Select(v => (v.UserId, Phone: VoteSuspicion.Phone(v.Phone)))
            .Where(x => x.Phone.Length > 0)
            .Distinct()
            .GroupBy(x => x.Phone)
            .ToDictionary(g => g.Key, g => g.Count());
        return (reason, phone) =>
            reason?.StartsWith(DisposableEmailRule.PolicyId, StringComparison.Ordinal) == true
                ? VotingAuditReason.DisposableEmail
                : accountsByPhone.GetValueOrDefault(VoteSuspicion.Phone(phone)) >= VoteSuspicion.PhoneClusterSize
                    ? VotingAuditReason.DuplicatePhone
                    : VotingAuditReason.CoordinatedAccounts;
    }
}
