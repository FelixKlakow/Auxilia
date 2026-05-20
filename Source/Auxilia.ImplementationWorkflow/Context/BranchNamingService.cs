using System.Text.RegularExpressions;
using Auxilia.Workflows.TaskSource;

namespace Auxilia.ImplementationWorkflow.Context;

public sealed class BranchNamingService
{
    public string Compute(WorkItem workItem)
    {
        var slug = workItem.Title.ToLowerInvariant();
        slug = Regex.Replace(slug, @"[^a-z0-9]+", "-");
        slug = slug.Trim('-');
        if (slug.Length > 50)
            slug = slug[..50].TrimEnd('-');

        return string.IsNullOrEmpty(slug)
            ? $"impl/{workItem.Id}"
            : $"impl/{workItem.Id}-{slug}";
    }
}
