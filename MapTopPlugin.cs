using System.IO;
using System.Net;
using System.Text;
using CounterStrikeSharp.API;
using CounterStrikeSharp.API.Core;
using CounterStrikeSharp.API.Core.Attributes;
using CounterStrikeSharp.API.Core.Attributes.Registration;
using CounterStrikeSharp.API.Modules.Commands;
using CounterStrikeSharp.API.Modules.Timers;
using CounterStrikeSharp.API.Modules.Utils;

namespace MapTop;

[MinimumApiVersion(80)]
public sealed class MapTopPlugin : BasePlugin, IPluginConfig<MapTopConfig>
{
    public override string ModuleName => "MapTop";
    public override string ModuleVersion => "1.0.1";
    public override string ModuleAuthor => "thereason";
    public override string ModuleDescription =>
        "Kills leaderboard for the current map.";

    public MapTopConfig Config { get; set; } = new();

    private readonly Dictionary<ulong, PlayerMapStats> _players = new();

    private CBaseEntity? _hudLayoutEntity;
    private CBaseEntity? _scriptEntity;
    private bool _hudCreated;

    private const string WorkshopAddonId = "3796805765";

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

        config.HudSpawnGroup = config.HudSpawnGroup.Trim();

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

        Diag($"Config: HudMode={Config.HudMode} HudSpawnGroup='{Config.HudSpawnGroup}' " +
             $"EnableDynamicHud={Config.EnableDynamicHud} WorkshopMapOnLoad={Config.WorkshopMapOnLoad}");

        // ВРЕМЕННЫЙ тестовый триггер: нативная загрузка карты аддона.
        if (Config.WorkshopMapOnLoad)
        {
            AddTimer(
                5.0f,
                () =>
                {
                    Diag($"Executing host_workshop_map {WorkshopAddonId}");
                    Server.ExecuteCommand($"host_workshop_map {WorkshopAddonId}");
                },
                TimerFlags.STOP_ON_MAPCHANGE);
        }

        // После hot reload OnMapStart не стреляет, а сущности на текущей
        // карте уже есть — перепривязываемся к ним.
        StartHudBindingLoop(10);
    }


    public override void Unload(bool hotReload)
    {
        _players.Clear();
    }

    private void OnMapStart(string mapName)
    {
        _players.Clear();

        // Сущности HUD живут на карте, после её смены ссылки протухают.
        // Существующие entity удалять нельзя: на карте аддона они
        // размещены в .vmap и принадлежат карте, а не плагину.
        _hudCreated = false;
        _hudLayoutEntity = null;
        _scriptEntity = null;

        Diag($"OnMapStart: {mapName}");

        bool onAddonMap = string.Equals(
            mapName,
            Config.HudSpawnGroup,
            StringComparison.OrdinalIgnoreCase);

        if (!onAddonMap && Config.HudSpawnGroup.Length > 0)
        {
            // Сервер крутит стоковую карту: подгружаем entity-слой аддона
            // (карта аддона содержит custom_hud_layout и point_script).
            // Двухсекундная задержка даёт карте доиграть загрузку.
            AddTimer(
                2.0f,
                () =>
                {
                    Diag($"Executing spawn_group_load {Config.HudSpawnGroup}");
                    Server.ExecuteCommand($"spawn_group_load {Config.HudSpawnGroup}");
                },
                TimerFlags.STOP_ON_MAPCHANGE);
        }

        // Сущности появляются не мгновенно (spawn group грузится
        // асинхронно), поэтому привязка повторяется несколько секунд.
        StartHudBindingLoop(onAddonMap ? 10 : 20);
    }

private void StartHudBindingLoop(int attemptsLeft)
    {
        if (attemptsLeft <= 0 || _hudCreated)
            return;

        AddTimer(
            1.0f,
            () =>
            {
                if (_hudCreated)
                    return;

                SetupHudEntities();

                if (!_hudCreated)
                    StartHudBindingLoop(attemptsLeft - 1);
            },
            TimerFlags.STOP_ON_MAPCHANGE);
    }

    private void SetupHudEntities()
    {
        if (_hudCreated)
            return;

        // Сначала пробуем найти сущности, размещённые в карте аддона:
        // это штатный путь, при котором ресурс-система резолвит
        // cs_script и layout из смонтированного аддона.
        if (TryBindMapEntities())
        {
            _hudCreated = true;
            Diag("HUD entities bound from map/spawn group.");
            return;
        }

        // Аварийный выключатель: позволяет изолировать краши,
        // связанные с динамическим созданием сущностей.
        if (!Config.EnableDynamicHud)
            return;

        // Фоллбэк: создаём сущности сами. На стоковой карте без
        // spawn group ресурс-система не видит аддон, поэтому этот
        // путь считается запасным, а не основным.
        var layoutKv = new CEntityKeyValues();
        // Имя обязательно: maptop_hud.vjs ищет сущность через
        // Instance.FindEntityByName("maptop_layout"). Без targetname
        // скрипт не находит layout и молча не выводит ничего.
        layoutKv.SetString("targetname", "maptop_layout");
        layoutKv.SetString("layout", "panorama/layout/custom_game/maptop_hud.vxml");

        CBaseEntity? hudLayoutEntity =
            Utilities.CreateEntityByName<CBaseEntity>("custom_hud_layout");

        if (hudLayoutEntity == null)
        {
            Diag("Failed to create custom_hud_layout.");
            return;
        }

        _hudLayoutEntity = hudLayoutEntity;
        _hudLayoutEntity.DispatchSpawn(layoutKv);

        // Создаём сущность point_script для выполнения JS
        var scriptKv = new CEntityKeyValues();
        scriptKv.SetString("cs_script", "maps/scripts/maptop_hud.vjs");

        CBaseEntity? scriptEntity =
            Utilities.CreateEntityByName<CBaseEntity>("point_script");

        if (scriptEntity == null)
        {
            Diag("Failed to create point_script.");
            return;
        }

        _scriptEntity = scriptEntity;
        _scriptEntity.DispatchSpawn(scriptKv);

        _hudCreated = true;

        Diag("Dynamic HUD entities created.");
    }

    private bool TryBindMapEntities()
    {
        List<CBaseEntity> layouts =
            CollectEntities("custom_hud_layout");

        List<CBaseEntity> scripts =
            CollectEntities("point_script");

        CBaseEntity? layout =
            PickEntity(layouts, "maptop_layout", "custom_hud_layout");

        CBaseEntity? script =
            PickEntity(scripts, "maptop_script", "point_script");

        if (layout == null || script == null)
            return false;

        _hudLayoutEntity = layout;
        _scriptEntity = script;

        return true;
    }

    private List<CBaseEntity> CollectEntities(string designerName)
    {
        var found = new List<CBaseEntity>();

        foreach (
            CBaseEntity entity
            in Utilities.FindAllEntitiesByDesignerName<CBaseEntity>(
                designerName))
        {
            if (!entity.IsValid)
                continue;

            string targetName = GetTargetName(entity);

            Diag($"probe {designerName}: targetname='{targetName}'");
            found.Add(entity);
        }

        return found;
    }

    // Сначала ищем по ожидаемому targetname. Если имён не знаем или они
    // не совпали, но сущность типа ровно одна — берём её: на карте
    // аддона других custom_hud_layout/point_script быть не может.
    private CBaseEntity? PickEntity(
        List<CBaseEntity> candidates,
        string expectedTargetName,
        string designerName)
    {
        if (candidates.Count == 0)
        {
            Diag($"no {designerName} entities found");
            return null;
        }

        CBaseEntity? byName = candidates.FirstOrDefault(
            entity => GetTargetName(entity) == expectedTargetName);

        if (byName != null)
            return byName;

        if (candidates.Count == 1)
        {
            Diag(
                $"{designerName}: single entity with targetname " +
                $"'{GetTargetName(candidates[0])}', binding anyway");
            return candidates[0];
        }

        Diag(
            $"{designerName}: {candidates.Count} entities, none named " +
            $"'{expectedTargetName}'");

        return null;
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
            $"HudSpawnGroup='{Config.HudSpawnGroup}' " +
            $"EnableDynamicHud={Config.EnableDynamicHud}");

        command.ReplyToCommand(
            $"[MapTop] _hudCreated={_hudCreated} " +
            $"layout={(_hudLayoutEntity != null && _hudLayoutEntity.IsValid ? "valid" : "null/invalid")} " +
            $"script={(_scriptEntity != null && _scriptEntity.IsValid ? "valid" : "null/invalid")}");

        Diag("css_maptop_debug: retrying entity binding");

        if (TryBindMapEntities())
        {
            _hudCreated = true;
            Diag("HUD entities bound from map/spawn group.");
            command.ReplyToCommand("[MapTop] Binding OK: entities bound.");
        }
        else
        {
            command.ReplyToCommand(
                "[MapTop] Binding failed: see [MapTop] probe lines above.");
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
            // когда panorama-сущности нет на текущей карте.
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

            int viewerSlot =
                player.Slot;

            // Установить строки топа
            SendTopRows(viewerSlot, top);

            // Показать HUD; скрытие гарантирует сам JS через duration
            TriggerHudInput($"MapTopShowFor_{viewerSlot}_{duration}");
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
            ShowTopToPlayerPanorama(player, top);
        }
        else
        {
            // Center-ветка: явный режим center или фоллбек,
            // когда panorama-сущности нет на текущей карте.
            ShowTopToPlayerCenter(player, top);
        }
    }

    private bool IsPanoramaHudAvailable()
    {
        return _hudCreated &&
               _hudLayoutEntity != null &&
               _hudLayoutEntity.IsValid &&
               _scriptEntity != null &&
               _scriptEntity.IsValid;
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

    private void ShowTopToPlayerPanorama(
        CCSPlayerController player,
        List<PlayerMapStats> top)
    {
        int viewerSlot = player.Slot;
        int duration =
            GetDisplayDuration(
                Config.CommandDisplaySeconds);

        // Установить строки топа через числовой протокол
        SendTopRows(viewerSlot, top);

        // Показать HUD; скрытие гарантирует сам JS через duration
        TriggerHudInput($"MapTopShowFor_{viewerSlot}_{duration}");
    }

    private void SendTopRows(
        int viewerSlot,
        List<PlayerMapStats> top)
    {
        TriggerHudInput($"MapTopViewer_{viewerSlot}");
        TriggerHudInput("MapTopClear");

        for (
            int i = 0;
            i < top.Count && i < 3;
            i++)
        {
            PlayerMapStats stats =
                top[i];

            // Имя передаём посимвольно: плагин знает и имя, и статус "(вышел)",
            // JS не зависит от контроллеров игроков.
            string displayName =
                FormatPlayerName(stats);

            TriggerHudInput("MapTopNameReset");

            foreach (char c in displayName)
            {
                TriggerHudInput(
                    $"MapTopNameChar_{(int)c}");
            }

            TriggerHudInput($"MapTopPlace_{i + 1}");

            int kills =
                Math.Min(stats.Kills, 999);

            if (kills == 0)
            {
                TriggerHudInput("MapTopKillDigit_0");
            }
            else
            {
                string digits =
                    kills.ToString();

                foreach (char digit in digits)
                {
                    TriggerHudInput(
                        $"MapTopKillDigit_{digit}");
                }
            }

            TriggerHudInput("MapTopRowCommit");
        }
    }

    private void TriggerHudInput(string inputName)
    {
        if (Config.HudMode != "panorama")
            return;

        if (_scriptEntity is null || !_scriptEntity.IsValid)
        {
            // Фоллбек на center, если сущности не созданы
            return;
        }

        // Отправляем вход на point_script, который выполнит JS
        _scriptEntity.AcceptInput(
            "RunScriptInput",
            activator: null,
            caller: _scriptEntity,
            value: inputName);
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
                SteamId = steamId,
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
