namespace AgentX.App;

/// <summary>
/// Pure focus routing for the single Add flyout: focus must follow the
/// visible mode and never land on an invisible control. The GUI feeds the
/// row visibilities; this function picks the target. Headless-testable —
/// real Tab/Enter/Escape behaviour still needs a human-driven GUI pass.
/// </summary>
public static class AddFlyoutFocus
{
    public enum Target
    {
        Existing,
        Open,
        Name,
    }

    /// <param name="choiceVisible">Existing Project / Quick Setup row.</param>
    /// <param name="openVisible">Open project button (confirm mode only).</param>
    /// <param name="quickVisible">Editable name + Create row.</param>
    public static Target Pick(bool choiceVisible, bool openVisible, bool quickVisible)
    {
        if (choiceVisible)
        {
            return Target.Existing;
        }

        if (quickVisible)
        {
            return Target.Name;
        }

        if (openVisible)
        {
            return Target.Open;
        }

        return Target.Existing;
    }
}
