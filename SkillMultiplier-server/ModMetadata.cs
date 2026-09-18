namespace SkillMultiplier;

using SPTarkov.Server.Core.Models.Spt.Mod;

public record ModMetadata : AbstractModMetadata
{
    public override string ModGuid { get; init; } = "dazzuh.skillmultiplier";
    public override string Name { get; init; } = "SkillMultiplier";
    public override string Author { get; init; } = "Dazzuh";
    public override List<string> Contributors { get; init; } = [];
    public override SemanticVersioning.Version Version { get; init; } = new("2.0.0");
    public override SemanticVersioning.Range SptVersion { get; init; } = new("~4.0.13");
    public override List<string> Incompatibilities { get; init; } = [];
    public override Dictionary<string, SemanticVersioning.Range> ModDependencies { get; init; } = new();
    public override string Url { get; init; } = "https://github.com/sp-tushonka/server-mod-examples";
    public override bool? IsBundleMod { get; init; } = false;
    public override string License { get; init; } = "MIT";
}
