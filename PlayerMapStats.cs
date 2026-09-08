namespace MapTop;

public sealed class PlayerMapStats
{
    public ulong SteamId { get; set; }

    public string Name { get; set; } = string.Empty;

    public int Kills { get; set; }

    public bool HasLeft { get; set; }
}