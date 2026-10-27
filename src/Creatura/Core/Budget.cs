namespace Creatura;

/// <summary>
/// What error buys: the retry budget. Plasticity widens the number of distinct attempts a level may make
/// before it hands the goal to a human; depth stays fixed. One rule, used by every design and by the host
/// (it was four copies of the same clamp).
/// </summary>
public static class Budget
{
    /// <summary>1 to 4 attempts: <c>clamp(1 + round(freedom * 3), 1, 4)</c>.</summary>
    public static int Attempts(double freedom) => Math.Clamp(1 + (int)Math.Round(freedom * 3), 1, 4);
}
