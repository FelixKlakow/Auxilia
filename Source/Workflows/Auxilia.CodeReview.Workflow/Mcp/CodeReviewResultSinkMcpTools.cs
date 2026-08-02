using Auxilia.CodeReview.Workflow.Findings;
using Auxilia.CodeReview.Workflow.Verdicts;
using Auxilia.Workflows.Mcp;
using Microsoft.Extensions.Logging;
using ModelContextProtocol.Server;

namespace Auxilia.CodeReview.Workflow.Mcp;

public sealed class CodeReviewResultSinkMcpTools : CapabilityMcpToolsBase
{
    private readonly List<StagedFinding> _findings;
    private readonly object _findingsLock;
    private readonly VerdictState _verdicts;

    public CodeReviewResultSinkMcpTools(string slotName, ILoggerFactory? loggerFactory = null)
        : this(slotName, [], new object(), new VerdictState(), loggerFactory) { }

    private CodeReviewResultSinkMcpTools(
        string slotName,
        List<StagedFinding> findings,
        object findingsLock,
        VerdictState verdicts,
        ILoggerFactory? loggerFactory)
        : base(slotName, BuildOptions(slotName, findings, findingsLock, verdicts), loggerFactory)
    {
        _findings = findings;
        _findingsLock = findingsLock;
        _verdicts = verdicts;
    }

    internal string RecordFinding(
        string filePath, int lineStart, int lineEnd,
        FindingSeverity severity, string category, string message, string? suggestion)
    {
        var finding = new StagedFinding(filePath, lineStart, lineEnd, severity, category, message, suggestion, "primary-reviewer");
        lock (_findingsLock) { _findings.Add(finding); }
        return "Finding recorded.";
    }

    internal string RecordFileVerdict(FileVerdict verdict)
    {
        _verdicts.FileVerdict = verdict;
        return "Verdict recorded.";
    }

    internal string RecordSecondaryVerdict(SecondaryVerdict verdict)
    {
        _verdicts.SecondaryVerdict = verdict;
        return "Secondary verdict recorded.";
    }

    public IReadOnlyList<StagedFinding> DrainFindings()
    {
        lock (_findingsLock)
        {
            var snapshot = _findings.ToList();
            _findings.Clear();
            return snapshot;
        }
    }

    public FileVerdict? TakeFileVerdict()
    {
        var value = _verdicts.FileVerdict;
        _verdicts.FileVerdict = null;
        return value;
    }

    public SecondaryVerdict? TakeSecondaryVerdict()
    {
        var value = _verdicts.SecondaryVerdict;
        _verdicts.SecondaryVerdict = null;
        return value;
    }

    private static McpServerOptions BuildOptions(
        string slotName, List<StagedFinding> findings, object findingsLock, VerdictState verdicts)
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
            (string filePath, int lineStart, int lineEnd, FindingSeverity severity, string category, string message, string? suggestion) =>
            {
                var finding = new StagedFinding(filePath, lineStart, lineEnd, severity, category, message, suggestion, "primary-reviewer");
                lock (findingsLock) { findings.Add(finding); }
                return "Finding recorded.";
            },
            new McpServerToolCreateOptions
            {
                Name = SlotMcpPrefix.Format(slotName, "record_finding"),
                Description = "Record a code review finding for the file under review."
            }));

        options.ToolCollection.Add(McpServerTool.Create(
            (FileVerdict verdict) =>
            {
                verdicts.FileVerdict = verdict;
                return "Verdict recorded.";
            },
            new McpServerToolCreateOptions
            {
                Name = SlotMcpPrefix.Format(slotName, "record_file_verdict"),
                Description = "Record the verdict for the file under review."
            }));

        options.ToolCollection.Add(McpServerTool.Create(
            (SecondaryVerdict verdict) =>
            {
                verdicts.SecondaryVerdict = verdict;
                return "Secondary verdict recorded.";
            },
            new McpServerToolCreateOptions
            {
                Name = SlotMcpPrefix.Format(slotName, "record_secondary_verdict"),
                Description = "Record the secondary (two-eyes) verdict."
            }));

        return options;
    }

    private sealed class VerdictState
    {
        public FileVerdict? FileVerdict;
        public SecondaryVerdict? SecondaryVerdict;
    }
}
