using System.Text.Json;
using Manifesta.Core.IR;

namespace Manifesta.Core;

public interface IReferenceDataCapturer
{
    Task<IReadOnlyList<IReadOnlyDictionary<string, JsonElement>>> CaptureAsync(
        TableDefinition table,
        int maxSizeKb,
        CancellationToken ct = default);
}
