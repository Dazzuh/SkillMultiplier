using System;
using System.Collections;
using System.Collections.Generic;
using System.Reflection;
using EFT;
using SPT.Reflection.Utils;

namespace SkillMultiplier;

/// <summary>
/// Reads the running client's real action list.
/// <para>
/// This exists because the server's catalog cannot be used for this. The server's keys address numbers in
/// its own <c>globals</c> tables, and those do not correspond one-to-one with actions: an action's
/// expression can read several globals fields (Strength's six entries are Min/Max pairs feeding one
/// lerped action), and one globals field can feed several skills. Pairing the two lists positionally
/// therefore produces silently wrong multipliers, so the client derives its own key space instead:
/// <c>SkillId[index]</c>, where the index is the position in the skill's own <c>Actions</c> array.
/// </para>
/// <para>
/// The index is a sound identifier because <c>BaseSkill</c>'s constructor wires each element of that very
/// array to <c>OnTrigger</c> (<c>actions2[i].ExternalEvent += OnTrigger</c>), so the action object handed
/// to the patch is always an element of <c>skill.Actions</c> and its position is stable for the life of
/// the <c>SkillManager</c>.
/// </para>
/// </summary>
internal static class ActionCatalog
{
    private static readonly FieldInfo FactorValue = typeof(SkillManager.SkillAction).GetField(
        "FactorValue",
        BindingFlags.Instance | BindingFlags.Public | BindingFlags.NonPublic
    );

    /// <summary>The live skill manager, or null while the profile has not loaded yet.</summary>
    internal static SkillManager Resolve()
    {
        try
        {
            return ClientAppUtils.GetClientApp()?.GetClientBackEndSession()?.Profile?.Skills;
        }
        catch (Exception ex)
        {
            Plugin.DebugLog($"[SkillMultiplier] SkillManager not available yet: {ex.Message}");
            return null;
        }
    }

    /// <summary>
    /// Every skill the client actually has.
    /// <para>
    /// The display array is read first because it is the game's own list of player-facing skills, then every
    /// skill-typed field is added, because the array is a curated subset. Vanilla constructs
    /// <c>RecoilControl</c>, <c>Lockpicking</c>, <c>WeaponModding</c>, <c>AdvancedModding</c> and
    /// <c>ProneMovement</c> without putting them in it - and those are exactly the skills a skills mod
    /// gives actions to. Enumerating only the array would silently leave them untunable.
    /// </para>
    /// </summary>
    internal static List<BaseSkill> Collect(SkillManager manager)
    {
        var found = new List<BaseSkill>();
        var seen = new HashSet<BaseSkill>();

        void Add(BaseSkill skill)
        {
            if (skill != null && seen.Add(skill))
            {
                found.Add(skill);
            }
        }

        try
        {
            foreach (var skill in manager.Skills)
            {
                Add(skill);
            }
        }
        catch (Exception ex)
        {
            Plugin.DebugLog($"[SkillMultiplier] Could not read SkillManager.Skills: {ex.Message}");
        }

        foreach (var field in typeof(SkillManager).GetFields(BindingFlags.Instance | BindingFlags.Public))
        {
            if (!typeof(BaseSkill).IsAssignableFrom(field.FieldType))
            {
                continue;
            }

            try
            {
                Add(field.GetValue(manager) as BaseSkill);
            }
            catch (Exception ex)
            {
                Plugin.DebugLog($"[SkillMultiplier] Could not read SkillManager.{field.Name}: {ex.Message}");
            }
        }

        return found;
    }

    /// <summary>
    /// Build the action-to-multiplier map the patch reads, keyed by skill and action index rather than by
    /// action object - see <see cref="Plugin.Multipliers"/>. Keys the client does not know about are ignored,
    /// so a stale table cannot break anything; keys with no matching action simply never fire.
    /// </summary>
    internal static Dictionary<(EFT.ESkillId Skill, int Index), float> BuildMap(
        SkillManager manager,
        IDictionary<string, float> actions
    )
    {
        var map = new Dictionary<(EFT.ESkillId, int), float>();

        if (manager == null || actions == null || actions.Count == 0)
        {
            return map;
        }

        foreach (var skill in Collect(manager))
        {
            var skillActions = skill.Actions;

            if (skillActions == null)
            {
                continue;
            }

            for (var index = 0; index < skillActions.Length; index++)
            {
                var action = skillActions[index];

                if (action == null)
                {
                    continue;
                }

                if (actions.TryGetValue($"{skill.Id}[{index}]", out var multiplier) && multiplier != 1f)
                {
                    map[(skill.Id, index)] = multiplier;
                }
            }
        }

        // The gym is not an action, so walking a skill's action array can never find it. The table names it
        // as <Skill>[Workout]; take those by name, so a workout keeps its own row rather than following
        // whatever else that skill does.
        foreach (var (key, multiplier) in actions)
        {
            if (multiplier == 1f || key == null || !key.EndsWith("[Workout]", StringComparison.Ordinal))
            {
                continue;
            }

            var open = key.IndexOf('[');

            if (open > 0 && Enum.TryParse<ESkillId>(key.Substring(0, open), out var workoutSkill))
            {
                map[(workoutSkill, SkillMultiplier.Patches.WorkoutExperiencePatch.WorkoutIndex)] = multiplier;
            }
        }

        return map;
    }

    /// <summary>
    /// Dump the whole catalog at startup. This is the evidence for whether the client's keys line up with
    /// what the UI is being told: <c>FactorValue</c> is the action's own value after the skill-progress
    /// rate is folded in, so it can be compared against the vanilla numbers the server reports.
    /// </summary>
    internal static void LogCatalog(SkillManager manager)
    {
        var count = 0;

        foreach (var skill in Collect(manager))
        {
            var skillActions = skill.Actions ?? [];
            var lines = new List<string>();

            for (var index = 0; index < skillActions.Length; index++)
            {
                var action = skillActions[index];
                var factor = action == null ? "?" : ReadFactor(action);
                lines.Add($"{skill.Id}[{index}] factor={factor}");
                count++;
            }

            if (lines.Count > 0)
            {
                Plugin.DebugLog($"[SkillMultiplier] {skill.Id} ({skillActions.Length} action(s)): {string.Join(", ", lines)}");
            }
        }

        Plugin.DebugLog($"[SkillMultiplier] Client action catalog: {count} action(s) across all skills.");
    }

    /// <summary>
    /// Report every action with the name the game itself gives it.
    /// <para>
    /// Every action is held in a named public field on <see cref="SkillManager"/> - <c>SprintAction</c>,
    /// <c>ExamineAction</c>, <c>KillAction</c>, <c>WeaponReloadAction</c> - and the constructor puts those
    /// same instances into each skill's <c>Actions</c> array. That field name is the only per-action identity
    /// the game has, and it is available for anything built this way, including skills another mod adds. It is
    /// what makes an action nameable without a hand-written table.
    /// </para>
    /// </summary>
    internal static List<ClientActionInfo> Describe(SkillManager manager)
    {
        var described = new List<ClientActionInfo>();
        var observed = ActionObservations.Snapshot();
        var members = MemberMap(manager);

        foreach (var skill in Collect(manager))
        {
            var skillActions = skill.Actions;

            if (skillActions == null)
            {
                continue;
            }

            // Read once per skill: it is a property of the skill, not of each action it holds.
            var serverAuthoritative = IsServerAuthoritative(skill);

            for (var index = 0; index < skillActions.Length; index++)
            {
                var stats = observed.TryGetValue((skill.Id, index), out var found) ? found : default;

                described.Add(new ClientActionInfo
                {
                    Key = $"{skill.Id}[{index}]",
                    Skill = skill.Id.ToString(),
                    Index = index,
                    Member = members.TryGetValue(skillActions[index], out var member) ? member : null,
                    Factor = ReadFactorValue(skillActions[index]) ?? 0d,
                    ObservedXp = stats.Max,
                    ObservedCount = stats.Count,
                    ServerAuthoritative = serverAuthoritative,
                });
            }
        }

        return described;
    }

    /// <summary>
    /// Whether the game itself refuses to progress this skill from the client.
    /// <para>
    /// <c>ClientAuthorizedSkill.OnTrigger</c> is a one-line override that logs "can not be progressed
    /// locally" and returns, so it never calls <c>Skill.OnTrigger</c> - which is where this mod patches. No
    /// client-side multiplier, per-row or global, can reach such a skill. Asked of the running skill rather
    /// than answered from a list of names, so a mod that builds a skill the same way gets the same treatment
    /// without being named here.
    /// </para>
    /// </summary>
    private static bool IsServerAuthoritative(BaseSkill skill) => skill is ClientAuthorizedSkill;

    private static string ReadFactor(SkillManager.SkillAction action) =>
        ReadFactorValue(action)?.ToString() ?? "?";

    /// <summary>
    /// Which field each action is held in, by identity, as far as it can be read from the objects the client
    /// already has. Empty rather than fatal if anything goes wrong: a name is a nicety, and the action still
    /// has a key and an observed amount without one.
    /// <para>
    /// Two kinds of holder, both reached from the manager itself: a field directly (the base game's
    /// arrangement - one public readonly field per action), and a collection field (which is how the game
    /// stores HideoutManagement's per-area actions, and how a mod may store its own). Each skill's own object
    /// is scanned too, for a mod that keeps an action on the skill rather than on the manager.
    /// </para>
    /// <para>
    /// Nothing outside these objects is touched. An action a mod holds in its own object behind its own
    /// statics is not reachable this way and keeps its position as a label, which is why the map prefers the
    /// game's own arrangement first and never guesses.
    /// </para>
    /// </summary>
    private static Dictionary<SkillManager.SkillAction, string> MemberMap(SkillManager manager)
    {
        var map = new Dictionary<SkillManager.SkillAction, string>();

        try
        {
            var holders = new List<(object Instance, Type Type)> { (manager, typeof(SkillManager)) };

            // The array an action was collected from is its position, not its name - naming every action
            // after the skill's own action list would be worse than leaving it positional, because it reads
            // like a real name. Skipped by identity, so no field name is hardcoded.
            var containers = new HashSet<object>();

            foreach (var skill in Collect(manager))
            {
                if (skill == null)
                {
                    continue;
                }

                holders.Add((skill, skill.GetType()));

                if (skill.Actions != null)
                {
                    containers.Add(skill.Actions);
                }
            }

            foreach (var (instance, type) in holders)
            {
                foreach (var member in Hierarchy(type))
                {
                    foreach (var field in member.GetFields(BindingFlags.Instance | BindingFlags.Public | BindingFlags.NonPublic))
                    {
                        Bind(map, field, instance, containers);
                    }
                }
            }
        }
        catch (Exception ex)
        {
            Plugin.DebugLog($"[SkillMultiplier] Could not map actions to their holders: {ex.Message}");
        }

        return map;
    }

    /// <summary>
    /// Record every action reachable through one field: the action itself when the field is one, or the
    /// elements when it is a collection of them.
    /// </summary>
    private static void Bind(
        Dictionary<SkillManager.SkillAction, string> map,
        FieldInfo field,
        object instance,
        HashSet<object> containers)
    {
        object value;

        try
        {
            value = field.GetValue(instance);
        }
        catch (Exception ex)
        {
            // A property-backed or otherwise unreadable field is not worth failing the whole map over, and
            // reading one can run a getter that throws early in a session.
            Plugin.DebugLog($"[SkillMultiplier] Could not read {field.Name}: {ex.Message}");
            return;
        }

        if (value is SkillManager.SkillAction direct)
        {
            Add(map, field.Name, direct);
            return;
        }

        // Only a collection whose element type could be an action, decided from the type before any value is
        // touched - so this never enumerates anything large, on a background thread, or not ours.
        if (value is not IEnumerable elements || containers.Contains(value) || !HoldsActions(field.FieldType))
        {
            return;
        }

        try
        {
            foreach (var element in elements)
            {
                if (element is SkillManager.SkillAction held)
                {
                    Add(map, field.Name, held);
                }
            }
        }
        catch (Exception ex)
        {
            // A collection another thread is mutating can throw mid-enumeration. Losing one holder's names
            // is a display gap; losing the whole map over it would be a failure.
            Plugin.DebugLog($"[SkillMultiplier] Could not enumerate {field.Name}: {ex.Message}");
        }
    }

    /// <summary>
    /// Whether a field's type could hold actions: the action type, an array of it, or a generic collection of
    /// it. <c>Dictionary&lt;EAreaType, SkillDependency&lt;(float, bool)&gt;&gt;</c> is the shape this is for.
    /// </summary>
    private static bool HoldsActions(Type fieldType)
    {
        if (typeof(SkillManager.SkillAction).IsAssignableFrom(fieldType))
        {
            return true;
        }

        if (fieldType.IsArray)
        {
            return typeof(SkillManager.SkillAction).IsAssignableFrom(fieldType.GetElementType());
        }

        if (!fieldType.IsGenericType)
        {
            return false;
        }

        foreach (var argument in fieldType.GetGenericArguments())
        {
            if (typeof(SkillManager.SkillAction).IsAssignableFrom(argument))
            {
                return true;
            }
        }

        return false;
    }

    /// <summary>
    /// Record an action under a holder's name, and everything built from it. Both the direct uses of a field
    /// and every conditioned variant of it point at the same thing, so the walk follows the subscriptions out
    /// - MovementAction is used by Endurance directly and by CovertMovement through Factor(condition), and
    /// without this the variant would have no name at all.
    /// <para>Bounded, so a cycle the game does not create cannot spin, and each action is walked once.</para>
    /// </summary>
    private static void Add(Dictionary<SkillManager.SkillAction, string> map, string name, SkillManager.SkillAction action)
    {
        if (!map.TryAdd(action, name))
        {
            return;
        }

        var pending = new Queue<SkillManager.SkillAction>();
        pending.Enqueue(action);

        for (var depth = 0; depth < 4 && pending.Count > 0; depth++)
        {
            foreach (var subscriber in Subscribers(pending.Dequeue()))
            {
                if (map.TryAdd(subscriber, name))
                {
                    pending.Enqueue(subscriber);
                }
            }
        }
    }

    /// <summary>
    /// The actions subscribed to <paramref name="action"/>'s events, which is how the game builds a
    /// conditioned action: <c>SkillAction&lt;TFilter&gt;.Where</c> and <c>SkillDependency&lt;TFilter&gt;.Factor</c> each
    /// construct a new action that subscribes to the one it wraps. The subscriber is reachable through the
    /// closure it was captured in, and the handler's target is that closure.
    /// </summary>
    private static IEnumerable<SkillManager.SkillAction> Subscribers(SkillManager.SkillAction action)
    {
        // Every type in the chain, not just the runtime one: the two events are declared on different types -
        // ExternalEvent on SkillAction, InnerEvent on the generic subclasses - and reflection returns only the
        // fields declared on the type it is asked about. Asking about one type finds the direct uses of a field
        // and misses every conditioned variant of it, which is most of them.
        foreach (var type in Hierarchy(action.GetType()))
        {
            foreach (var field in type.GetFields(BindingFlags.Instance | BindingFlags.Public | BindingFlags.NonPublic))
            {
                // Both events are held in compiler-generated delegate fields, which is all this needs to find -
                // matching on the field's type rather than its name survives a rename of either.
                if (!typeof(Delegate).IsAssignableFrom(field.FieldType))
                {
                    continue;
                }

                Delegate handler;

                try
                {
                    handler = field.GetValue(action) as Delegate;
                }
                catch (Exception ex)
                {
                    Plugin.DebugLog($"[SkillMultiplier] Could not read an action's subscribers: {ex.Message}");
                    continue;
                }

                foreach (var invocation in handler?.GetInvocationList() ?? [])
                {
                    var target = invocation.Target;

                    if (target == null)
                    {
                        continue;
                    }

                    foreach (var captured in target.GetType().GetFields(BindingFlags.Instance | BindingFlags.Public | BindingFlags.NonPublic))
                    {
                        SkillManager.SkillAction wrapped;

                        try
                        {
                            wrapped = captured.GetValue(target) as SkillManager.SkillAction;
                        }
                        catch
                        {
                            // A closure field that cannot be read costs one candidate subscriber, not the walk.
                            continue;
                        }

                        if (wrapped != null && !ReferenceEquals(wrapped, action))
                        {
                            yield return wrapped;
                        }
                    }
                }
            }
        }
    }

    /// <summary><paramref name="type"/> and everything it derives from, stopping before <c>object</c>.</summary>
    private static IEnumerable<Type> Hierarchy(Type type)
    {
        for (var current = type; current != null && current != typeof(object); current = current.BaseType)
        {
            yield return current;
        }
    }

    /// <summary>
    /// <c>FactorValue</c> as a number, or null when it cannot be read. <c>FactorValue</c> is a property whose
    /// getter can throw early in a session, which is why this is defensive rather than direct.
    /// </summary>
    private static double? ReadFactorValue(SkillManager.SkillAction action)
    {
        try
        {
            var value = action == null ? null : FactorValue?.GetValue(action);

            return value == null ? null : Convert.ToDouble(value);
        }
        catch
        {
            return null;
        }
    }
}
