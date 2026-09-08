using System.Text.Json.Serialization;
using CounterStrikeSharp.API.Core;

namespace MapTop;

public sealed class MapTopConfig : BasePluginConfig
{
    [JsonPropertyName("CommandDisplaySeconds")]
    public float CommandDisplaySeconds { get; set; } = 5.0f;

    [JsonPropertyName("RoundEndDisplaySeconds")]
    public float RoundEndDisplaySeconds { get; set; } = 4.0f;

    [JsonPropertyName("TopSize")]
    public int TopSize { get; set; } = 3;

    [JsonPropertyName("AutoShowAtRoundEnd")]
    public bool AutoShowAtRoundEnd { get; set; } = true;

    [JsonPropertyName("HudMode")]
    public string HudMode { get; set; } = "center";
}