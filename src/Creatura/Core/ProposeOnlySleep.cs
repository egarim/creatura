namespace Creatura;

/// <summary>
/// Consolidation that proposes a set-point change and never applies it: goals are rewritable but a human signs
/// off (Levin, 01:02:21; the hybrid's sleep). The proposal is a note the owner reads and a row in
/// <see cref="Proposals"/> the run can hand back; the set point itself is never touched. Lived inside the
/// hybrid's loop; a component now, so the orienting agent's revaluation can use the same mechanism.
/// </summary>
public sealed class ProposeOnlySleep
{
    private readonly List<string> _proposals = [];

    /// <param name="setPoint">The set point the proposal is about (the hybrid's: the contract).</param>
    public ProposeOnlySleep(string setPoint = "contract") => SetPoint = setPoint;

    public string SetPoint { get; }

    /// <summary>Every proposal made, in order. None is ever applied.</summary>
    public IReadOnlyList<string> Proposals => _proposals;

    /// <summary>The proposal for a run that reached its goal, or did not.</summary>
    public string Proposal(bool goalReached) => goalReached
        ? $"propose: keep the {SetPoint} set point as it is"
        : $"propose: renegotiate the {SetPoint} set point — unreachable with current tools";

    /// <summary>Make the proposal at sleep: a note, kept here as well. The set point is not changed.</summary>
    public string Propose(SleepContext context)
    {
        var proposal = Proposal(context.GoalReached);
        _proposals.Add(proposal);
        context.Notes.Add(proposal);
        return proposal;
    }

    /// <summary>A proposal made mid-run (the orienting agent's revaluation under profound punishment): a note, kept here, never applied.</summary>
    public string Propose(RegulatorContext context, string why)
    {
        var proposal = $"propose: renegotiate the {SetPoint} set point — {why}";
        _proposals.Add(proposal);
        context.Note(proposal);
        return proposal;
    }
}
