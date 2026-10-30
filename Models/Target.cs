namespace CompactLoader;

internal sealed record Target(int Id, string Name, long Started, string Architecture, string WindowTitle = "");
