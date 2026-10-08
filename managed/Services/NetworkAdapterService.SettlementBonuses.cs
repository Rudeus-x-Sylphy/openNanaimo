namespace OpenNanaimo.Adapter.Services;

public sealed partial class NetworkAdapterService
{
    private async Task<uint> ScaleSettlementScoreAsync(ConnectionSession session, uint score,
        uint ringCode, bool partnerPresent, CancellationToken token)
    {
        if (session.Character is null) return score;
        var scaled = DungeonExperiencePolicy.ScaleEquipment(score, session.Character);
        var entitlement = await _database.GetExperienceCardAsync(session.Character.Id, token);
        scaled = ExperienceCardPolicy.ScaleScore(scaled, entitlement, DateTime.Now);
        scaled = CoupleBenefitPolicy.ScaleExperience(scaled, ringCode, partnerPresent);
        scaled = await ScaleMentorshipExperienceAsync(session, scaled, token);
        // Both paths feed signed-32 client score displays. EXP is converted only
        // after all score bonuses and this shared saturation boundary.
        return Math.Min(int.MaxValue, scaled);
    }
}
