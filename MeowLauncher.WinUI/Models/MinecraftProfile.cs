namespace MeowLauncher.WinUI.Models;

public sealed class CapeInfo
{
    public string Id { get; set; } = "";
    public string Alias { get; set; } = "";
    public string State { get; set; } = "";
    public string Url { get; set; } = "";

    public bool IsActive => string.Equals(State, "ACTIVE", StringComparison.OrdinalIgnoreCase);
    public string Label => IsActive ? $"{Alias} · 当前" : Alias;
    public override string ToString() => Label;
}

public sealed class MinecraftProfile
{
    public bool Ok { get; set; }
    public bool NeedLogin { get; set; }
    public string Message { get; set; } = "";
    public string Name { get; set; } = "";
    public string Uuid { get; set; } = "";
    public string Variant { get; set; } = "";
    public string SkinUrl { get; set; } = "";
    public string ActiveCapeId { get; set; } = "";
    public List<CapeInfo> Capes { get; set; } = new();

    public string VariantText => Variant.Equals("SLIM", StringComparison.OrdinalIgnoreCase)
        ? "纤细模型 · Slim"
        : Variant.Length == 0 ? "模型未知" : "经典模型 · Classic";
}
