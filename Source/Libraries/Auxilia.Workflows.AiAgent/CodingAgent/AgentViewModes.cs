namespace Auxilia.Workflows.AiAgent.CodingAgent;

/// <summary>
/// The values of the "view-mode" run input an agent workflow may declare: how the operator
/// experiences the session. Advanced = the parsed conversation view (chat renderer);
/// console = the CLI runs interactively in tmux+ttyd behind the platform's web terminal.
/// </summary>
public static class AgentViewModes
{
    public const string Advanced = "advanced";
    public const string Console = "console";

    /// <summary>The input name the terminal gate and dispatch UIs key on.</summary>
    public const string InputName = "view-mode";
}
