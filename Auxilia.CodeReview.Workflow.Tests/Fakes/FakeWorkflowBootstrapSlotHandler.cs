using Auxilia.CodeReview.Workflow;
using Auxilia.CodeReview.Workflow.Compaction;
using Auxilia.CodeReview.Workflow.Context;
using Auxilia.Workflows;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Options;

namespace Auxilia.CodeReview.Workflow.Tests.Fakes;

public sealed class FakeWorkflowBootstrapSlotHandler : ISlotHandler
{
    private readonly string _outputDirectory;
    private readonly TwoEyesConfiguration? _twoEyes;
    private readonly WriteBackConfiguration? _writeBack;
    private readonly WorkItemRetrievalFailureBehavior _failureBehavior;
    private readonly IReadOnlyList<string>? _criticalPatterns;
    private readonly ContextCompactionOptions? _compactionOptions;

    public FakeWorkflowBootstrapSlotHandler(
        string outputDirectory,
        TwoEyesConfiguration? twoEyes = null,
        WriteBackConfiguration? writeBack = null,
        WorkItemRetrievalFailureBehavior failureBehavior = WorkItemRetrievalFailureBehavior.Ignore,
        IReadOnlyList<string>? criticalPatterns = null,
        ContextCompactionOptions? compactionOptions = null)
    {
        _outputDirectory = outputDirectory;
        _twoEyes = twoEyes;
        _writeBack = writeBack;
        _failureBehavior = failureBehavior;
        _criticalPatterns = criticalPatterns;
        _compactionOptions = compactionOptions;
    }

    public void Register(IServiceCollection services, string slotName, Type serviceType, SlotConfiguration configuration)
    {
        // Pre-register test-specific overrides BEFORE AddCodeReviewWorkflow (which uses TryAdd)
        if (_twoEyes != null)
            services.AddSingleton(_twoEyes);
        if (_writeBack != null)
            services.AddSingleton(_writeBack);

        services.Add(ServiceDescriptor.Singleton(typeof(WorkItemRetrievalFailureBehavior), (object)_failureBehavior));
        services.AddSingleton(new CriticalityClassifier(_criticalPatterns ?? Array.Empty<string>()));

        if (_compactionOptions != null)
            services.AddSingleton<IOptions<ContextCompactionOptions>>(Options.Create(_compactionOptions));

        services.AddCodeReviewWorkflow(_outputDirectory);
    }
}
