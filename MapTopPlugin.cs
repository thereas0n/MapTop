using System.IO;
using System.Net;
using System.Text;
using CounterStrikeSharp.API;
using CounterStrikeSharp.API.Core;
using CounterStrikeSharp.API.Core.Attributes;
using CounterStrikeSharp.API.Core.Attributes.Registration;
using CounterStrikeSharp.API.Modules.Commands;
using CounterStrikeSharp.API.Modules.Extensions;
using CounterStrikeSharp.API.Modules.Timers;
using CounterStrikeSharp.API.Modules.Utils;

namespace MapTop;

[MinimumApiVersion(80)]
public sealed class MapTopPlugin : BasePlugin, IPluginConfig<MapTopConfig>
{
    public override string ModuleName => "MapTop";
    public override string ModuleVersion => "1.1.0";
    public override string ModuleAuthor => "thereason";
    public override string ModuleDescription =>
        "Kills leaderboard for the current map.";

    public MapTopConfig Config { get; set; } = new();

    private readonly Dictionary<ulong, PlayerMapStats> _players = new();

    // Показ панели по слотам: каждый показ увеличивает epoch слота.
    // Отложенное скрытие срабатывает только если epoch не изменился —
    // иначе повторный !maptop отменял бы скрытие предыдущего показа
    // или, наоборот, скрывал панель раньше времени.
    private readonly Dictionary<int, int> _showEpochs = new();

    private CCSCustomHudLayout? _hudLayout;

    private const string HudPanelId = "MapTopPanel";
    private const string HudVisibleClass = "MapTopVisible";
    private const string HudLayoutPath = "panorama/layout/custom_game/maptop_hud.vxml_c";

    private static readonly object DiagLock = new();
    private string? _diagLogPath;

    // GameDirectory указывает на .../game/csgo, а папка аддонов — сосед:
    // .../game/csgo_addons. На Linux в момент Load() путь может быть
    // пустым, поэтому резолвим лениво при первой записи.
    private static string? ResolveDiagLogPath()
    {
        string? gameDir = Server.GameDirectory;

        if (string.IsNullOrEmpty(gameDir))
            return null;

        string primary = Path.Combine(gameDir, "csgo_addons");
        string? parent = Path.GetDirectoryName(
            Path.TrimEndingDirectorySeparator(gameDir));

        string candidate =
            Directory.Exists(primary)
                ? primary
                : parent != null
                    ? Path.Combine(parent, "csgo_addons")
                    : primary;

        try
        {
            Directory.CreateDirectory(candidate);
        }
        catch
        {
            return null;
        }

        return Path.Combine(candidate, "maptop_diagnostics.log");
    }

    // Консоль сервера недоступна по SFTP, поэтому все диагностические
    // сообщения дублируются в файл рядом с csgo_addons — его можно
    // забрать и прочитать после запуска сервера.
    private void Diag(string message)
    {
        Server.PrintToConsole($"[MapTop] {message}");

        try
        {
            _diagLogPath ??= ResolveDiagLogPath();

            if (_diagLogPath == null)
                return;

            lock (DiagLock)
            {
                File.AppendAllText(
                    _diagLogPath,
                    $"{DateTime.UtcNow:yyyy-MM-dd HH:mm:ss.fff}Z {message}{Environment.NewLine}");
            }
        }
        catch
        {
            // Логирование не должно ронять плагин.
        }
    }

    public void OnConfigParsed(MapTopConfig config)
    {
        config.CommandDisplaySeconds =
            Math.Clamp(config.CommandDisplaySeconds, 1.0f, 30.0f);

        config.RoundEndDisplaySeconds =
            Math.Clamp(config.RoundEndDisplaySeconds, 1.0f, 30.0f);

        config.TopSize =
            Math.Clamp(config.TopSize, 1, 10);

        config.HudMode =
            config.HudMode
                .Trim()
                .ToLowerInvariant();

        if (config.HudMode != "center" &&
            config.HudMode != "panorama")
        {
            config.HudMode = "center";
        }

        Config = config;
    }

    public override void Load(bool hotReload)
    {
        RegisterEventHandler<EventPlayerDeath>(OnPlayerDeath);
        RegisterEventHandler<EventRoundEnd>(OnRoundEnd);
        RegisterEventHandler<EventPlayerDisconnect>(OnPlayerDisconnect);
        RegisterEventHandler<EventPlayerConnectFull>(OnPlayerConnectFull);

        RegisterListener<Listeners.OnMapStart>(OnMapStart);

        // Чат-триггер: CSS превращает "!maptop" в вызов консольной команды
        // "maptop". Атрибут выше регистрирует только css_maptop, поэтому
        // регистрируем алиас maptop вручную — иначе !maptop из чата молчит.
        AddCommand(
            "maptop",
            "Shows the top kills for the current map.",
            OnMapTopCommand);

        // Свежий файл диагностики на каждый запуск плагина.
        try
        {
            _diagLogPath = ResolveDiagLogPath();

            if (_diagLogPath != null)
            {
                File.WriteAllText(
                    _diagLogPath,
                    $"{DateTime.UtcNow:yyyy-MM-dd HH:mm:ss.fff}Z === MapTop plugin load, hotReload={hotReload} ==={Environment.NewLine}");
            }
        }
        catch
        {
            _diagLogPath = null;
        }

        Diag($"Config: HudMode={Config.HudMode} EnableDynamicHud={Config.EnableDynamicHud}");

        // После hot reload OnMapStart не стреляет, а сущность на текущей
        // карте уже есть/нужна — запускаем привязку сразу.
        StartHudBindingLoop(10);
    }

    public override void Unload(bool hotReload)
    {
        _players.Clear();
        _showEpochs.Clear();
    }

    private void OnMapStart(string mapName)
    {
        _players.Clear();
        _showEpochs.Clear();

        // Сущность HUD живёт на карте, после её смены ссылка протухает.
        _hudLayout = null;

        Diag($"OnMapStart: {mapName}");

        // Сущность появляется не мгновенно, привязка повторяется
        // несколько секунд после старта карты.
        StartHudBindingLoop(10);
    }

    private void StartHudBindingLoop(int attemptsLeft)
    {
        if (attemptsLeft <= 0 || _hudLayout != null)
            return;

        AddTimer(
            1.0f,
            () =>
            {
                if (_hudLayout != null)
                    return;

                SetupHudEntity();

                if (_hudLayout == null)
                    StartHudBindingLoop(attemptsLeft - 1);
            },
            TimerFlags.STOP_ON_MAPCHANGE);
    }

    private void SetupHudEntity()
    {
        if (_hudLayout != null)
            return;

        // Сначала пробуем найти сущность, размещённую в карте аддона:
        // это штатный путь, при котором layout уже прописан в Hammer.
        if (TryBindMapEntity())
        {
            Diag("HUD layout entity bound from map.");
            return;
        }

        // Аварийный выключатель: позволяет изолировать ошибки,
        // связанные с динамическим созданием сущностей.
        if (!Config.EnableDynamicHud)
            return;

        // Фоллбэк: создаём custom_hud_layout сами. Работает на любой
        // карте при условии, что аддон с панорамой смонтирован
        // (MultiAddonManager) и клиент его скачал.
        var kv = new CEntityKeyValues();
        kv.SetString("layout", HudLayoutPath);

        CCSCustomHudLayout? hud =
            Utilities.CreateEntityByName<CCSCustomHudLayout>("custom_hud_layout");

        if (hud == null)
        {
            Diag("Failed to create custom_hud_layout.");
            return;
        }

        _hudLayout = hud;
        _hudLayout.DispatchSpawn(kv);

        Diag($"Dynamic HUD layout entity created (layout={HudLayoutPath}).");
    }

    private bool TryBindMapEntity()
    {
        List<CCSCustomHudLayout> layouts =
            Utilities.FindAllEntitiesByDesignerName<CCSCustomHudLayout>(
                "custom_hud_layout")
                .Where(entity => entity.IsValid)
                .ToList();

        if (layouts.Count == 0)
            return false;

        CCSCustomHudLayout? byName = layouts.FirstOrDefault(
            entity => GetTargetName(entity) == "maptop_layout");

        if (byName != null)
        {
            _hudLayout = byName;
            return true;
        }

        if (layouts.Count == 1)
        {
            Diag(
                "custom_hud_layout: single entity with targetname " +
                $"'{GetTargetName(layouts[0])}', binding anyway");

            _hudLayout = layouts[0];
            return true;
        }

        Diag(
            $"custom_hud_layout: {layouts.Count} entities, none named " +
            "'maptop_layout'");

        return false;
    }

    private static string GetTargetName(CBaseEntity entity)
    {
        try
        {
            return entity.Entity?.Name ?? string.Empty;
        }
        catch
        {
            return string.Empty;
        }
    }

    [ConsoleCommand(
        "css_maptop_debug",
        "Dumps MapTop HUD binding state and retries entity binding.")]
    public void OnMapTopDebugCommand(
        CCSPlayerController? player,
        CommandInfo command)
    {
        command.ReplyToCommand(
            $"[MapTop] HudMode={Config.HudMode} " +
            $"EnableDynamicHud={Config.EnableDynamicHud}");

        command.ReplyToCommand(
            $"[MapTop] layout={(_hudLayout != null && _hudLayout.IsValid ? "valid" : "null/invalid")}");

        Diag("css_maptop_debug: retrying entity binding");

        if (TryBindMapEntity())
        {
            Diag("HUD layout entity bound from map.");
            command.ReplyToCommand("[MapTop] Binding OK: layout entity bound.");
        }
        else
        {
            command.ReplyToCommand(
                "[MapTop] Binding failed: no custom_hud_layout on map.");
        }
    }

    [ConsoleCommand(
        "css_maptop",
        "Shows the top kills for the current map.")]
    public void OnMapTopCommand(
        CCSPlayerController? player,
        CommandInfo command)
    {
        if (player == null ||
            !player.IsValid)
        {
            command.ReplyToCommand(
                "[MapTop] Command can only be used by a player.");

            return;
        }

        int requestedSize =
            Config.TopSize;

        if (command.ArgCount >= 2 &&
            int.TryParse(
                command.GetArg(1),
                out int argumentSize))
        {
            requestedSize =
                Math.Clamp(
                    argumentSize,
                    1,
                    Config.TopSize);
        }

        ShowTopToPlayer(
            player,
            requestedSize);
    }

    private HookResult OnPlayerDeath(
        EventPlayerDeath @event,
        GameEventInfo info)
    {
        if (IsWarmup())
            return HookResult.Continue;

        CCSPlayerController? attacker =
            @event.Attacker;

        CCSPlayerController? victim =
            @event.Userid;

        if (attacker == null ||
            victim == null)
        {
            return HookResult.Continue;
        }

        if (!attacker.IsValid ||
            !victim.IsValid)
        {
            return HookResult.Continue;
        }

        if (attacker.IsBot)
            return HookResult.Continue;

        // Убийства ботов по умолчанию не считаются. На сервере, где играет
        // один человек против ботов, из-за этого топ остаётся пустым и обе
        // ветки показа молчат. Если такой сервер нужен — включить
        // CountBotKills в конфиге.
        if (victim.IsBot && !Config.CountBotKills)
            return HookResult.Continue;


        if (attacker == victim)
            return HookResult.Continue;

        if (attacker.TeamNum ==
            victim.TeamNum)
        {
            return HookResult.Continue;
        }

        ulong steamId =
            attacker.SteamID;

        if (steamId == 0)
            return HookResult.Continue;

        PlayerMapStats stats =
            GetOrCreatePlayer(attacker);

        stats.Name =
            attacker.PlayerName;

        stats.HasLeft = false;

        stats.Kills++;

        return HookResult.Continue;
    }

    private HookResult OnPlayerDisconnect(
        EventPlayerDisconnect @event,
        GameEventInfo info)
    {
        CCSPlayerController? player =
            @event.Userid;

        if (player == null)
            return HookResult.Continue;

        ulong steamId =
            player.SteamID;

        if (steamId == 0)
            return HookResult.Continue;

        if (_players.TryGetValue(
                steamId,
                out PlayerMapStats? stats))
        {
            stats.Name =
                player.PlayerName;

            stats.HasLeft = true;
        }

        return HookResult.Continue;
    }

    private HookResult OnPlayerConnectFull(
        EventPlayerConnectFull @event,
        GameEventInfo info)
    {
        CCSPlayerController? player =
            @event.Userid;

        if (player == null ||
            !player.IsValid)
        {
            return HookResult.Continue;
        }

        ulong steamId =
            player.SteamID;

        if (steamId == 0)
            return HookResult.Continue;

        if (_players.TryGetValue(
                steamId,
                out PlayerMapStats? stats))
        {
            stats.Name =
                player.PlayerName;

            stats.HasLeft = false;
        }

        return HookResult.Continue;
    }

    private HookResult OnRoundEnd(
        EventRoundEnd @event,
        GameEventInfo info)
    {
        if (!Config.AutoShowAtRoundEnd)
            return HookResult.Continue;

        List<PlayerMapStats> top =
            GetTop(Config.TopSize);

        if (top.Count == 0)
            return HookResult.Continue;

        ShowRoundEnd(top);

        return HookResult.Continue;
    }

    private void ShowRoundEnd(
        List<PlayerMapStats> top)
    {
        if (top.Count == 0)
            return;

        if (Config.HudMode == "panorama" &&
            IsPanoramaHudAvailable())
        {
            ShowRoundEndPanorama(top);
        }
        else
        {
            // Center-ветка: явный режим center или фоллбек,
            // когда panorama-сущность недоступна.
            ShowRoundEndCenterImpl(top);
        }
    }

    private void ShowRoundEndCenterImpl(
        List<PlayerMapStats> top)
    {
        PlayerMapStats leader =
            top[0];

        string name =
            WebUtility.HtmlEncode(
                FormatPlayerName(leader));

        string message =
            $"Лидер карты: {name} — {leader.Kills} убийств";

        int duration =
            GetDisplayDuration(
                Config.RoundEndDisplaySeconds);

        AddTimer(
            0.8f,
            () =>
            {
                foreach (
                    CCSPlayerController player
                    in Utilities.GetPlayers())
                {
                    if (!player.IsValid)
                        continue;

                    player.PrintToCenterHtml(
                        message,
                        duration);
                }
            },
            TimerFlags.STOP_ON_MAPCHANGE);
    }

    private void ShowRoundEndPanorama(
        List<PlayerMapStats> top)
    {
        int duration =
            GetDisplayDuration(
                Config.RoundEndDisplaySeconds);

        foreach (
            CCSPlayerController player
            in Utilities.GetPlayers())
        {
            if (!player.IsValid)
                continue;

            ShowPanoramaTopForPlayer(
                player,
                top,
                duration);
        }
    }

    private void ShowTopToPlayer(
        CCSPlayerController player,
        int requestedSize)
    {
        List<PlayerMapStats> top =
            GetTop(requestedSize);

        if (top.Count == 0)
            return;

        if (Config.HudMode == "panorama" &&
            IsPanoramaHudAvailable())
        {
            int duration =
                GetDisplayDuration(
                    Config.CommandDisplaySeconds);

            ShowPanoramaTopForPlayer(
                player,
                top,
                duration);
        }
        else
        {
            // Center-ветка: явный режим center или фоллбек,
            // когда panorama-сущность недоступна.
            ShowTopToPlayerCenter(player, top);
        }
    }

    private bool IsPanoramaHudAvailable()
    {
        return _hudLayout != null && _hudLayout.IsValid;
    }

    private void ShowTopToPlayerCenter(
        CCSPlayerController player,
        List<PlayerMapStats> top)
    {

        int duration =
            GetDisplayDuration(
                Config.CommandDisplaySeconds);

        StringBuilder message =
            new();

        message.Append(
            "<font color='#FFFFFF'><b>ТОП КАРТЫ</b></font>");

        for (
            int i = 0;
            i < top.Count;
            i++)
        {
            PlayerMapStats stats =
                top[i];

            string name =
                WebUtility.HtmlEncode(
                    FormatPlayerName(stats));

            message.Append("<br>");

            message.Append(
                $"{i + 1}. {name} — {stats.Kills} {FormatKillsWord(stats.Kills)}");
        }

        player.PrintToCenterHtml(
            message.ToString(),
            duration);
    }

    // Прямое управление custom_hud_layout из плагина (CSSharp 1.0.374+,
    // extension-методы CCSCustomHudLayoutExtensions). Никакого vjs:
    // строки топа пишутся в dialog variables панели, видимость
    // включается/выключается классом — всё per-player.
    private void ShowPanoramaTopForPlayer(
        CCSPlayerController player,
        List<PlayerMapStats> top,
        int duration)
    {
        if (_hudLayout == null || !_hudLayout.IsValid)
            return;

        int slot = player.Slot;

        // Новая эпоха показа слота: отменяет отложенное скрытие
        // предыдущего показа этого же слота.
        _showEpochs.TryGetValue(slot, out int epoch);
        epoch++;
        _showEpochs[slot] = epoch;

        // Очищаем все строки, затем заполняем актуальные — иначе
        // при сокращении топа останутся строки прошлого показа.
        for (int row = 1; row <= 3; row++)
        {
            _hudLayout.SetDialogVariableStringForPlayer(
                player, HudPanelId, $"row{row}", string.Empty);
        }

        for (
            int i = 0;
            i < top.Count && i < 3;
            i++)
        {
            PlayerMapStats stats = top[i];

            string text =
                $"{i + 1}. {FormatPlayerName(stats)} — {stats.Kills} " +
                $"{FormatKillsWord(stats.Kills)}";

            _hudLayout.SetDialogVariableStringForPlayer(
                player, HudPanelId, $"row{i + 1}", text);

            Diag($"MAPTOP_ROW_SET_v{slot}_r{i + 1} [{text}]");
        }

        _hudLayout.SetHasClassForPlayer(
            player, HudPanelId, HudVisibleClass, true);

        AddTimer(
            duration,
            () =>
            {
                // Скрываем только если не было более нового показа
                // этому слоту и сущность ещё жива.
                if (_hudLayout == null || !_hudLayout.IsValid)
                    return;

                if (_showEpochs.TryGetValue(slot, out int current) &&
                    current != epoch)
                {
                    return;
                }

                _hudLayout.SetHasClassForPlayer(
                    player, HudPanelId, HudVisibleClass, false);
            },
            TimerFlags.STOP_ON_MAPCHANGE);
    }

    private List<PlayerMapStats> GetTop(
        int count)
    {
        return _players.Values
            .Where(player =>
                player.Kills > 0)
            .OrderByDescending(player =>
                player.Kills)
            .ThenBy(
                player => player.Name,
                StringComparer.OrdinalIgnoreCase)
            .Take(count)
            .ToList();
    }

    private PlayerMapStats GetOrCreatePlayer(
        CCSPlayerController player)
    {
        ulong steamId =
            player.SteamID;

        if (_players.TryGetValue(
                steamId,
                out PlayerMapStats? existing))
        {
            return existing;
        }

        PlayerMapStats stats =
            new()
            {
                Name = player.PlayerName,
                Kills = 0,
                HasLeft = false
            };

        _players.Add(
            steamId,
            stats);

        return stats;
    }

    private static string FormatPlayerName(
        PlayerMapStats player)
    {
        if (player.HasLeft)
            return $"{player.Name} (вышел)";

        return player.Name;
    }

    // Russian plural: 1 убийство, 2-4 убийства, 5-20 убийств.
    private static string FormatKillsWord(
        int kills)
    {
        int n = Math.Abs(kills) % 100;

        if (n >= 11 && n <= 14)
            return "убийств";

        n %= 10;

        if (n == 1)
            return "убийство";

        if (n >= 2 && n <= 4)
            return "убийства";

        return "убийств";
    }

    private static int GetDisplayDuration(
        float seconds)
    {
        return Math.Max(
            1,
            (int)Math.Ceiling(seconds));
    }

    private static bool IsWarmup()
    {
        CCSGameRulesProxy? gameRulesProxy =
            Utilities
                .FindAllEntitiesByDesignerName<CCSGameRulesProxy>(
                    "cs_gamerules")
                .FirstOrDefault();

        return gameRulesProxy?
                   .GameRules?
                   .WarmupPeriod
               ?? false;
    }
}
