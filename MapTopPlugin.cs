using System.Net;
using System.Text;
using CounterStrikeSharp.API;
using CounterStrikeSharp.API.Core;
using CounterStrikeSharp.API.Core.Attributes;
using CounterStrikeSharp.API.Core.Attributes.Registration;
using CounterStrikeSharp.API.Modules.Commands;
using CounterStrikeSharp.API.Modules.Timers;

namespace MapTop;

[MinimumApiVersion(80)]
public sealed class MapTopPlugin : BasePlugin, IPluginConfig<MapTopConfig>
{
    public override string ModuleName => "MapTop";
    public override string ModuleVersion => "1.0.0";
    public override string ModuleAuthor => "thereason";
    public override string ModuleDescription =>
        "Kills leaderboard for the current map.";

    public MapTopConfig Config { get; set; } = new();

    private readonly Dictionary<ulong, PlayerMapStats> _players = new();

    private CCSGameRules? _gameRules;
    private bool _gameRulesInitialized;

    private CBaseEntity? _mapTopScript;
    private bool _mapTopScriptLookupDone;

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
        RegisterListener<Listeners.OnTick>(OnTick);

        if (hotReload)
        {
            InitializeGameRules();
        }
    }

    public override void Unload(bool hotReload)
    {
        _players.Clear();

        _gameRules = null;
        _gameRulesInitialized = false;
    }

    private void OnMapStart(string mapName)
    {
        _players.Clear();

        _gameRules = null;
        _gameRulesInitialized = false;

        _mapTopScript = null;
        _mapTopScriptLookupDone = false;
    }

    private void InitializeGameRules()
    {
        if (_gameRulesInitialized)
            return;

        CCSGameRulesProxy? gameRulesProxy =
            Utilities
                .FindAllEntitiesByDesignerName<CCSGameRulesProxy>(
                    "cs_gamerules")
                .FirstOrDefault();

        _gameRules =
            gameRulesProxy?.GameRules;

        _gameRulesInitialized =
            _gameRules != null;
    }

    private void OnTick()
    {
        if (!_gameRulesInitialized)
        {
            InitializeGameRules();
            return;
        }

        if (_gameRules == null)
            return;

        _gameRules.GameRestart =
            _gameRules.RestartRoundTime <
            Server.CurrentTime;
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

        if (victim.IsBot)
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
        FindMapTopScript();

        return _mapTopScript != null &&
               _mapTopScript.IsValid;
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

    private void FindMapTopScript()
    {
        if (_mapTopScriptLookupDone)
            return;

        _mapTopScriptLookupDone = true;

        _mapTopScript = Utilities
            .FindAllEntitiesByDesignerName<CBaseEntity>(
                "point_script")
            .FirstOrDefault(entity =>
                entity.IsValid &&
                entity.Entity?.Name == "maptop_script");

        if (_mapTopScript == null)
        {
            List<string> scriptNames =
                Utilities
                    .FindAllEntitiesByDesignerName<CBaseEntity>(
                        "point_script")
                    .Select(entity => entity.Entity?.Name ?? "<unnamed>")
                    .ToList();

            Server.PrintToConsole(
                $"[MapTop] maptop_script NOT found. " +
                $"point_script entities on map: [{string.Join(", ", scriptNames)}]");
        }
        else
        {
            Server.PrintToConsole(
                "[MapTop] maptop_script entity found.");
        }
    }

    private void TriggerHudInput(
        string inputName)
    {
        if (Config.HudMode != "panorama")
            return;

        FindMapTopScript();

        if (_mapTopScript != null &&
            _mapTopScript.IsValid)
        {
            // Вход entity называется RunScriptInput, имя скрипт-входа передаётся
            // в value (эквивалент "ent_fire maptop_script RunScriptInput <input>",
            // который не работает из server console на выделенном сервере).
            _mapTopScript.AcceptInput(
                "RunScriptInput",
                activator: null,
                caller: _mapTopScript,
                value: inputName);

            return;
        }

        // Запасной путь: консольная команда (работает локально в Hammer).
        Server.PrintToConsole(
            $"[MapTop] maptop_script not found, falling back to ent_fire for '{inputName}'.");

        Server.ExecuteCommand(
            $"ent_fire maptop_script RunScriptInput {inputName}");
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
