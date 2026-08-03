using Auxilia.PlatformData;
using Auxilia.UniversalDataAccess;

namespace Auxilia.Core.Api.Data;

/// <summary>One runtime platform setting (see <c>PlatformSettingsService</c>); one record per key.</summary>
public sealed record PlatformSettingRecord : IEntity
{
    public Guid Id { get; init; }

    public required string Key { get; init; }

    public required string Value { get; init; }

    public DateTimeOffset UpdatedUtc { get; init; }

    public Guid? UpdatedBy { get; init; }

    public static Guid IdFor(string key) => DeterministicGuid.For("platform-setting", key);
}
