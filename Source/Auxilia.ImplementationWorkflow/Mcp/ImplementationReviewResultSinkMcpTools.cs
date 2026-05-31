using Auxilia.Workflows.Mcp;
using Microsoft.Extensions.Logging;
using ModelContextProtocol.Server;

namespace Auxilia.ImplementationWorkflow.Mcp;

public sealed class ImplementationReviewResultSinkMcpTools : CapabilityMcpToolsBase
{
    private readonly List<ReviewNote> _notes;
    private readonly object _lock;

    public ImplementationReviewResultSinkMcpTools(string slotName, ILoggerFactory? loggerFactory = null)
        : this(slotName, [], new object(), loggerFactory) { }

    private ImplementationReviewResultSinkMcpTools(
        string slotName, List<ReviewNote> notes, object notesLock, ILoggerFactory? loggerFactory)
        : base(slotName, BuildOptions(slotName, notes, notesLock), loggerFactory)
    {
        _notes = notes;
        _lock = notesLock;
    }

    internal string RecordReviewNote(string description, string? filePath, ReviewNoteSeverity severity)
    {
        var note = new ReviewNote(description, filePath, severity);
        lock (_lock) { _notes.Add(note); }
        return "Review note recorded.";
    }

    public IReadOnlyList<ReviewNote> DrainNotes()
    {
        lock (_lock)
        {
            var snapshot = _notes.ToList();
            _notes.Clear();
            return snapshot;
        }
    }

    private static McpServerOptions BuildOptions(string slotName, List<ReviewNote> notes, object notesLock)
    {
        var options = new McpServerOptions
        {
            ToolCollection = new McpServerPrimitiveCollection<McpServerTool>(),
            ServerInfo = new ModelContextProtocol.Protocol.Implementation
            {
                Name = slotName,
                Version = "1.0"
            }
        };

        options.ToolCollection.Add(McpServerTool.Create(
            (string description, string? filePath, ReviewNoteSeverity severity) =>
            {
                var note = new ReviewNote(description, filePath, severity);
                lock (notesLock) { notes.Add(note); }
                return "Review note recorded.";
            },
            new McpServerToolCreateOptions
            {
                Name = SlotMcpPrefix.Format(slotName, "record_review_note"),
                Description = "Record a review note for the implementation under review."
            }));

        return options;
    }
}
