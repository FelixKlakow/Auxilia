namespace Auxilia.Workflows.Views;

/// <summary>Publishes items to one of the workflow's declared views.</summary>
public interface IViewPublisher
{
    /// <summary>Serialises <paramref name="item"/> and publishes it to the named view.</summary>
    Task PublishAsync<TItem>(string viewName, TItem item, CancellationToken ct = default);
}
