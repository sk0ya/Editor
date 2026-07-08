using Editor.Core.Models;

namespace Editor.Core.Engine.Commands;

/// <summary>
/// Handles the LSP-trigger Normal mode commands (gd/gr/ga/gch/gct) — each just
/// emits a request event for the host to act on; no engine state is touched.
/// </summary>
public class LspTriggerCommands
{
    public bool TryHandle(string cmd, List<VimEvent> events)
    {
        switch (cmd)
        {
            case "gd":
                events.Add(VimEvent.GoToDefinitionRequested());
                return true;
            case "gr":
                events.Add(VimEvent.FindReferencesRequested());
                return true;
            case "ga":
                events.Add(VimEvent.CodeActionRequested());
                return true;
            case "gch":
                events.Add(VimEvent.CallHierarchyRequested());
                return true;
            case "gct":
                events.Add(VimEvent.TypeHierarchyRequested());
                return true;
            default:
                return false;
        }
    }
}
