using System.Reflection;
using SPTarkov.Common.Models.Logging;
using SPTarkov.DI.Annotations;
using SPTarkov.Reflection.Patching;
using SPTarkov.Server.Core.DI;
using SPTarkov.Server.Core.Helpers.Profile;
using SPTarkov.Server.Core.Models.Enums;

namespace SkillMultiplier;

/// <summary>
/// Scales the skill XP the server pays out itself, which no client-side multiplier can reach.
/// <para>
/// The game client computes most XP and this mod's client half multiplies it at <c>EFT.Skill.OnTrigger</c>.
/// Everything else is decided by SPT and written into the profile here: repairing pays LightVests,
/// HeavyVests, WeaponTreatment, Charisma and Intellect, and the same call is how quest rewards, hideout and
/// crafting XP are paid. None of it passes through the client, so a slider had nothing to multiply.
/// </para>
/// <para>
/// <b>One place per number.</b> A grant is multiplied only for skills this mod does not already scale through
/// the globals (<see cref="SkillMultiplierMod.ServerGrantMultiplier"/> answers null for those). Crafting and
/// hideout XP are computed from globals fields this mod scales in place, so multiplying their grants as well
/// would square the effect - the same trap as the global multiplier, one layer down.
/// </para>
/// </summary>
[Injectable]
public sealed class ServerSkillGrantPatch : AbstractPatch
{
    private static readonly HashSet<string> Noted = new(StringComparer.OrdinalIgnoreCase);

    private static SkillMultiplierMod _mod = null!;
    private static ISptLogger<ServerSkillGrantPatch> _logger = null!;

    public ServerSkillGrantPatch(SkillMultiplierMod mod, ISptLogger<ServerSkillGrantPatch> logger)
    {
        _mod = mod;
        _logger = logger;
    }

    protected override MethodBase GetTargetMethod()
    {
        // By parameter count: the name is shared by both overloads and the compiler warns about picking an
        // overload by name alone for exactly this reason. The four-argument form forwards to this one, so
        // patching this catches both - patching both would apply twice.
        return typeof(ProfileHelper)
                .GetMethods(BindingFlags.Public | BindingFlags.Instance)
                .FirstOrDefault(m => m.Name == nameof(ProfileHelper.AddSkillPointsToPlayer)
                    && m.GetParameters().Length == 5)
            ?? throw new InvalidOperationException("ProfileHelper.AddSkillPointsToPlayer(5 args) was not found.");
    }

    [PatchPrefix]
    public static bool Prefix(SkillTypes skill, ref double pointsToAddToSkill)
    {
        var multiplier = _mod?.ServerGrantMultiplier(skill);

        if (multiplier is null || Math.Abs(multiplier.Value - 1.0) < 1e-9)
        {
            return true;
        }

        pointsToAddToSkill *= multiplier.Value;

        // Once per skill at Info, so the first grant after a change is visible evidence that the row is live;
        // every later one at Debug, because a session can hold many grants and a log line each is noise.
        if (Noted.Add(skill.ToString()))
        {
            _logger.Info(
                $"[SkillMultiplier] Server-granted {skill} XP is multiplied by {multiplier.Value} "
                + $"(this grant: {pointsToAddToSkill})."
            );
        }
        else
        {
            _logger.Debug($"[SkillMultiplier] Server-granted {skill} XP multiplied by {multiplier.Value}.");
        }

        return true;
    }
}

/// <summary>
/// Enables this mod's server-side patches.
/// <para>
/// A separate class from <see cref="SkillMultiplierMod"/> on purpose. The patch needs the mod (to read the
/// configured multipliers), so a mod that also took the patches would close a loop the container refuses to
/// build at all: <c>mod -> IRuntimePatch -> mod</c> fails startup outright, which is how this was found.
/// Nothing depends on this loader, so the cycle has nowhere to form.
/// </para>
/// </summary>
[Injectable(TypePriority = OnLoadOrder.Preload)]
public sealed class SkillMultiplierPatchLoader(
    IEnumerable<IRuntimePatch> patches,
    ISptLogger<SkillMultiplierPatchLoader> logger
) : IOnLoad
{
    public Task OnLoadAsync(CancellationToken cancellationToken)
    {
        // Only this assembly's own. The container hands back every registered patch, and enabling SPT's own
        // is not ours to do.
        var mine = patches.Where(p => p.GetType().Assembly == Assembly.GetExecutingAssembly()).ToList();

        foreach (var patch in mine)
        {
            patch.Enable();
        }

        logger.Success(
            $"[SkillMultiplier] {mine.Count} server patch(es) enabled: "
            + $"{string.Join(", ", mine.Select(p => p.GetType().Name))}. Server-granted XP follows the skill's row."
        );

        return Task.CompletedTask;
    }
}
