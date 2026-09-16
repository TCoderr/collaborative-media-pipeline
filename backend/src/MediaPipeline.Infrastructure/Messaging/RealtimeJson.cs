using System.Text.Json;
using System.Text.Json.Serialization;

namespace MediaPipeline.Infrastructure.Messaging;

/// <summary>Serialisation shared by the notifier and the API-side consumer.</summary>
public static class RealtimeJson
{
    public static readonly JsonSerializerOptions Options = new(JsonSerializerDefaults.Web)
    {
        DefaultIgnoreCondition = JsonIgnoreCondition.WhenWritingNull,
    };
}
