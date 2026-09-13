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

    // По ТЗ убийства ботов не считаются. Оставлено переключателем только
    // для отладки на сервере из одного игрока и ботов, где иначе топ
    // всегда пуст.
    [JsonPropertyName("CountBotKills")]
    public bool CountBotKills { get; set; } = false;

    // Имя spawn group аддона с HUD-сущностями. Если сервер крутит не карту
    // аддона, плагин подгружает entity-слой аддона поверх текущей карты
    // командой spawn_group_load. Пустая строка отключает подгрузку.
    [JsonPropertyName("HudSpawnGroup")]
    public string HudSpawnGroup { get; set; } = "maptop_hud";

    // ВРЕМЕННЫЙ флаг тестовой сборки: на Load() выполнить
    // host_workshop_map <id аддона>. Будет удалён до сдачи.
    [JsonPropertyName("WorkshopMapOnLoad")]
    public bool WorkshopMapOnLoad { get; set; } = false;

    [JsonPropertyName("EnableDynamicHud")]
    public bool EnableDynamicHud { get; set; } = true;
}