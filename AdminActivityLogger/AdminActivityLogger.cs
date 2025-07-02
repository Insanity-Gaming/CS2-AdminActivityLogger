using System.Collections;
using System.Text;
using System.Text.Json.Serialization;
using CounterStrikeSharp.API;
using CounterStrikeSharp.API.Core;
using CounterStrikeSharp.API.Modules.Admin;
using CounterStrikeSharp.API.Modules.Commands;
using CounterStrikeSharp.API.Modules.Cvars;
using CounterStrikeSharp.API.Modules.Entities;
using CounterStrikeSharp.API.Modules.Events;
using Microsoft.Extensions.Logging;
using Timer = CounterStrikeSharp.API.Modules.Timers.Timer;

namespace AdminActivityLogger;

public class AdminActivityLoggerConfig : BasePluginConfig
{
    [JsonPropertyName("Discord Webhook")] public string Webhook { get; set; } = "https://discord.com/api/webhooks/1389819969591705682/fh0MKRnvhnF93GbYGSj_gtSqkGQLAov-cKWDyjOYiye36NYKDEBKtarLY9U4VIPu8LCi";
    [JsonPropertyName("Command Watchlist")] public List<string> WatchList { get; set; } = ["css_ban", "css_kick", "css_mute", "css_gag", "css_unmute", "css_ungag"];
    [JsonPropertyName("Map Change Lenience")] public float Lenience { get; set; } = 30f;
}

public class AdminActivityLogger : BasePlugin, IPluginConfig<AdminActivityLoggerConfig>
{
    public override string ModuleName => "Admin Activity Logger";
    public override string ModuleVersion => "1.0";
    public override string ModuleAuthor => "Retro";
    public override string ModuleDescription => "Keeps track of admin playtime";

    public AdminActivityLoggerConfig Config { get; set; } = new();

    public void OnConfigParsed(AdminActivityLoggerConfig config) => Config = config;

    private Dictionary<SteamID, float> _adminPlaytime = new();
    private bool _isMapChangeHappening = false;
    private Dictionary<SteamID, Timer> _disconnectLeeway = new();
    private static readonly HttpClient client = new HttpClient();

    private string _serverName = "";
    private List<string> _chatListeners = new();

    public override void Load(bool hotReload)
    {

        RegisterEventHandler<EventPlayerConnectFull>(OnPlayerConnectFull);
        RegisterEventHandler<EventPlayerDisconnect>(OnPlayerDisconnect);
        RegisterEventHandler<EventPlayerChat>(OnPlayerChat);

        AddTimer(5f, () =>
        {
            _serverName = ConVar.Find("hostname")?.StringValue ?? "A server";

            foreach (var cmd in Config.WatchList)
            {
                var noPrefix = cmd.Replace("css_", "");
                _chatListeners.Add($"!{noPrefix}");
                _chatListeners.Add($"/{noPrefix}");
                Logger.LogInformation($"Added {cmd} to watch list");
                AddCommandListener(cmd, CommandLogger, HookMode.Post);
            }
        });

        if (hotReload)
        {
            foreach (var controller in Utilities.GetPlayers())
            {
                if (controller is null || !controller.IsValid || controller.IsBot || controller.IsHLTV) continue;

                var steamid = new SteamID(controller.SteamID);
                var admin = AdminManager.GetPlayerAdminData(steamid);
                if (admin is null) continue;
                if (!admin.GetAllFlags().Contains("@css/generic")) continue;

                Logger.LogInformation($"{controller.PlayerName} has joined the server");

                _adminPlaytime.Add(new SteamID(controller.SteamID), Server.CurrentTime);
                var name = controller.PlayerName;
                Task.Run(async () =>
                {
                    await SendAdminJoinWebhook(name, steamid);
                });
            }
        }
    }

    public override void Unload(bool hotReload)
    {
        DeregisterEventHandler<EventPlayerConnectFull>(OnPlayerConnectFull);
        DeregisterEventHandler<EventPlayerDisconnect>(OnPlayerDisconnect);
        DeregisterEventHandler<EventPlayerChat>(OnPlayerChat);
        foreach (var cmd in Config.WatchList)
        {
            RemoveCommandListener(cmd, CommandLogger, HookMode.Post);
        }        
        foreach (var controller in Utilities.GetPlayers())
        {
            if (controller is null || !controller.IsValid || controller.IsBot || controller.IsHLTV) continue;

            var steamid = new SteamID(controller.SteamID);
            var admin = AdminManager.GetPlayerAdminData(steamid);
            if (admin is null) continue;
            if (!admin.GetAllFlags().Contains("@css/generic")) continue;

            if (!_adminPlaytime.TryGetValue(steamid, out var joinTime))
            {
                Logger.LogWarning($"{controller.PlayerName}[{controller.SteamID}] was not in admin playtime dictionary");
                return;
            }

            Logger.LogInformation($"{controller.PlayerName} has left the server");

            var name = controller.PlayerName;
            var totalTime = Server.CurrentTime - joinTime - Config.Lenience;
            Task.Run(async () =>
            {
                await SendAdminLeaveWebhook(name, steamid, totalTime);
            });
            _adminPlaytime.Remove(steamid);
        }
    }

    private HookResult OnPlayerChat(EventPlayerChat @event, GameEventInfo info)
    {
        var msg = @event.Text;
        bool isListener = true;
        foreach (var cmd in _chatListeners)
        {
            if (!msg.StartsWith(cmd)) continue;
            isListener = true;
            Logger.LogInformation($"{cmd} was found a valid command for {msg}");
            break;
        }
        if (!isListener) return HookResult.Continue;
        
        var controller = Utilities.GetPlayerFromUserid(@event.Userid);
        if (controller is null || !controller.IsValid || controller.IsBot || controller.IsHLTV) return HookResult.Continue;

        var steamid = new SteamID(controller.SteamID);
        var admin = AdminManager.GetPlayerAdminData(steamid);
        if (admin is null) return HookResult.Continue;
        if (!admin.GetAllFlags().Contains("@css/generic")) return HookResult.Continue;

        var name = controller?.PlayerName ?? "Server";

        Task.Run(async () =>
        {
            await SendAdminCommandWebhook(name, steamid, msg);
        });

        return HookResult.Continue;
    }

    private HookResult CommandLogger(CCSPlayerController? controller, CommandInfo commandInfo)
    {
        Logger.LogInformation($"{controller?.PlayerName ?? "SERVER"} ran command {commandInfo.GetCommandString}");
        if (controller is null || !controller.IsValid || controller.IsBot || controller.IsHLTV) return HookResult.Continue;
        Logger.LogInformation("controller was valid");
        var steamid = new SteamID(controller.SteamID);
        var admin = AdminManager.GetPlayerAdminData(controller);
        if (admin is null) return HookResult.Continue;
        Logger.LogInformation("admin was found");
        if (!admin.GetAllFlags().Contains("@css/generic")) return HookResult.Continue;
        Logger.LogInformation("Has generic");


        var command = commandInfo.GetCommandString;
        var name = controller?.PlayerName ?? "Server";

        Task.Run(async () =>
        {
            await SendAdminCommandWebhook(name, steamid, command);
        });

        return HookResult.Continue;
    }

    private HookResult OnPlayerConnectFull(EventPlayerConnectFull @event, GameEventInfo info)
    {
        var controller = @event.Userid;
        if (controller is null || !controller.IsValid || controller.IsBot || controller.IsHLTV) return HookResult.Continue;

        var steamid = new SteamID(controller.SteamID);
        var admin = AdminManager.GetPlayerAdminData(steamid);
        if (admin is null) return HookResult.Continue;
        if (!admin.GetAllFlags().Contains("@css/generic")) return HookResult.Continue;

        if (_adminPlaytime.ContainsKey(steamid) && !_disconnectLeeway.TryGetValue(steamid, out var _))
        {
            _adminPlaytime.Remove(steamid);
            Logger.LogWarning($"{controller.PlayerName}[{controller.SteamID}] was already in dictionary when they joined");
        }
        else if (_adminPlaytime.ContainsKey(steamid) && _disconnectLeeway.TryGetValue(steamid, out var timer))
        {
            timer.Kill();
            _disconnectLeeway.Remove(steamid);
            return HookResult.Continue;
        }

        Logger.LogInformation($"{controller.PlayerName} has joined the server");

        _adminPlaytime.Add(new SteamID(controller.SteamID), Server.CurrentTime);
        var name = controller.PlayerName;
        Task.Run(async () =>
        {
            await SendAdminJoinWebhook(name, steamid);
        });

        return HookResult.Continue;
    }


    private HookResult OnPlayerDisconnect(EventPlayerDisconnect @event, GameEventInfo info)
    {
        if (_isMapChangeHappening) return HookResult.Continue;

        var controller = @event.Userid;
        if (controller is null || !controller.IsValid || controller.IsBot || controller.IsHLTV) return HookResult.Continue;

        var steamid = new SteamID(controller.SteamID);
        var admin = AdminManager.GetPlayerAdminData(steamid);
        if (admin is null) return HookResult.Continue;
        if (!admin.GetAllFlags().Contains("@css/generic")) return HookResult.Continue;

        var timer = AddTimer(Config.Lenience, () =>
        {
            if (!_adminPlaytime.TryGetValue(steamid, out var joinTime))
            {
                Logger.LogWarning($"{controller.PlayerName}[{controller.SteamID}] was not in admin playtime dictionary");
                return;
            }

            Logger.LogInformation($"{controller.PlayerName} has left the server");

            var name = controller.PlayerName;
            var totalTime = Server.CurrentTime - joinTime;
            Task.Run(async () =>
            {
                await SendAdminLeaveWebhook(name, steamid, totalTime);
            });
            _adminPlaytime.Remove(steamid);
        });
        _disconnectLeeway.Add(steamid, timer);

        return HookResult.Continue;
    }

    private async Task SendAdminLeaveWebhook(string name, SteamID steamid, float playTime)
    {
        var message = new
        {
            content = $"`{name}[{steamid.SteamId64}]` has left the `{_serverName}` after being on for `{Math.Floor(playTime / 60)}`"
        };

        var json = System.Text.Json.JsonSerializer.Serialize(message);
        var content = new StringContent(json, Encoding.UTF8, "application/json");

        await client.PostAsync(Config.Webhook, content);
    }

    private async Task SendAdminJoinWebhook(string name, SteamID steamid)
    {
        var message = new
        {
            content = $"`{name}[{steamid.SteamId64}]` has joined the `{_serverName}`"
        };

        var json = System.Text.Json.JsonSerializer.Serialize(message);
        var content = new StringContent(json, Encoding.UTF8, "application/json");

        await client.PostAsync(Config.Webhook, content);
    }
    
    private async Task SendAdminCommandWebhook(string name, SteamID steamid, string command)
    {
        var message = new
        {
            content = $"`{name}[{steamid.SteamId64}]` has ran `{command}` on `{_serverName}`"
        };

        var json = System.Text.Json.JsonSerializer.Serialize(message);
        var content = new StringContent(json, Encoding.UTF8, "application/json");

        await client.PostAsync(Config.Webhook, content);
    }
}
