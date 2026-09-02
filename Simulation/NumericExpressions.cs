namespace ProxyState.Simulation;

// Facts are resolved once while content is loaded. The kind selects a direct
// runtime accessor; Index is used only by schema-backed attribute facts.
public enum FactKind : byte
{
    AgentAttribute,
    TargetAttribute,
    TimeMinuteOfDay,
    TimeDayOfWeek,
    JobWorkStartMinute,
    JobWorkEndMinute,
    TargetAffinity,
    AgentLocationCurrent,
    AgentLocationHome,
    AgentLocationWork,
    TargetLocationCurrent,
    TargetEntity,
    TravelReachable,
    JobIsWorkDay
}

[Flags]
public enum FactDependencyCategory : ushort
{
    None = 0, Time = 1 << 0, Schedule = 1 << 1, Attributes = 1 << 2,
    Traits = 1 << 3, Location = 1 << 4, Travel = 1 << 5,
    SocialTargets = 1 << 6, TargetAffinity = 1 << 7, TargetLocation = 1 << 8,
    NetworkTargets = 1 << 9, TargetAttributes = 1 << 10, Coordination = 1 << 11,
    All = ushort.MaxValue
}

// Attribute indexes are tracked separately from broad categories so changing
// fatigue, for example, does not wake an intent which only reads wealth.
public readonly record struct FactDependencyMask(FactDependencyCategory Categories, ulong AttributeBits = 0)
{
    public static FactDependencyMask None => default;
    public static FactDependencyMask All => new(FactDependencyCategory.All, ulong.MaxValue);
    public bool Intersects(FactDependencyMask other)
    {
        var shared = Categories & other.Categories;
        return (shared & ~FactDependencyCategory.Attributes) != 0 ||
            ((shared & FactDependencyCategory.Attributes) != 0 && (AttributeBits & other.AttributeBits) != 0);
    }
    public static FactDependencyMask operator |(FactDependencyMask left, FactDependencyMask right) =>
        new(left.Categories | right.Categories, left.AttributeBits | right.AttributeBits);

    internal static FactDependencyMask From(FactId fact) => fact.Kind switch
    {
        FactKind.AgentAttribute => new(FactDependencyCategory.Attributes,
            fact.Index < 64 ? 1UL << fact.Index : ulong.MaxValue),
        FactKind.TargetAttribute => new(FactDependencyCategory.TargetAttributes),
        FactKind.TimeMinuteOfDay or FactKind.TimeDayOfWeek => new(FactDependencyCategory.Time),
        FactKind.JobWorkStartMinute or FactKind.JobWorkEndMinute or FactKind.JobIsWorkDay => new(FactDependencyCategory.Schedule),
        FactKind.AgentLocationCurrent or FactKind.AgentLocationHome or FactKind.AgentLocationWork => new(FactDependencyCategory.Location),
        FactKind.TravelReachable => new(FactDependencyCategory.Travel),
        FactKind.TargetAffinity => new(FactDependencyCategory.TargetAffinity),
        FactKind.TargetEntity => new(FactDependencyCategory.SocialTargets),
        FactKind.TargetLocationCurrent => new(FactDependencyCategory.TargetLocation),
        _ => None
    };
}

public enum FactValueKind : byte { Number, Boolean }

public readonly record struct FactId(FactKind Kind, int Index = 0);

public sealed class FactRegistry
{
    private readonly Dictionary<string, RegisteredFact> _facts;

    public FactRegistry(AgentAttributeSchema attributes)
    {
        ArgumentNullException.ThrowIfNull(attributes);
        _facts = new Dictionary<string, RegisteredFact>(StringComparer.OrdinalIgnoreCase)
        {
            ["time.minuteOfDay"] = new(new FactId(FactKind.TimeMinuteOfDay), FactValueKind.Number, 0, SimulationDefaults.SimulationMinutesPerDay),
            ["time.dayOfWeek"] = new(new FactId(FactKind.TimeDayOfWeek), FactValueKind.Number, 1, SimulationDefaults.DaysPerWeek),
            ["job.workStartMinute"] = new(new FactId(FactKind.JobWorkStartMinute), FactValueKind.Number, 0, SimulationDefaults.SimulationMinutesPerDay),
            ["job.workEndMinute"] = new(new FactId(FactKind.JobWorkEndMinute), FactValueKind.Number, 0, SimulationDefaults.SimulationMinutesPerDay),
            ["target.affinity"] = new(new FactId(FactKind.TargetAffinity), FactValueKind.Number, 0, 1),
            ["agent.location.current"] = new(new FactId(FactKind.AgentLocationCurrent), FactValueKind.Number),
            ["agent.location.home"] = new(new FactId(FactKind.AgentLocationHome), FactValueKind.Number),
            ["agent.location.work"] = new(new FactId(FactKind.AgentLocationWork), FactValueKind.Number),
            ["target.location.current"] = new(new FactId(FactKind.TargetLocationCurrent), FactValueKind.Number),
            ["target.entity"] = new(new FactId(FactKind.TargetEntity), FactValueKind.Number),
            ["travel.reachable"] = new(new FactId(FactKind.TravelReachable), FactValueKind.Boolean),
            ["job.isWorkDay"] = new(new FactId(FactKind.JobIsWorkDay), FactValueKind.Boolean)
        };
        for (var index = 0; index < attributes.Count; index++)
        {
            var definition = attributes.Definitions[index];
            _facts.Add($"agent.attribute.{definition.Id}", new RegisteredFact(
                new FactId(FactKind.AgentAttribute, index), FactValueKind.Number, definition.Min, definition.Max));
            _facts.Add($"target.attribute.{definition.Id}", new RegisteredFact(
                new FactId(FactKind.TargetAttribute, index), FactValueKind.Number, definition.Min, definition.Max));
        }
    }

    public FactId Resolve(string reference) => ResolveDefinition(reference).Id;

    internal RegisteredFact ResolveDefinition(string reference)
    {
        if (string.IsNullOrWhiteSpace(reference) || !_facts.TryGetValue(reference, out var fact))
            throw new InvalidDataException($"Unknown numeric fact '{reference ?? "<null>"}'.");
        return fact;
    }

    internal readonly record struct RegisteredFact(FactId Id, FactValueKind ValueKind = FactValueKind.Number, float Min = 0, float Max = 0);
}

// Deliberately small authoring tree. Only fields required by the selected op
// are populated, which keeps actions.json readable while enabling strict compilation.
public sealed record NumericExpressionDefinition
{
    public string? Op { get; init; }
    public string? Fact { get; init; }
    public float? Value { get; init; }
    public NumericExpressionDefinition? Input { get; init; }
    public NumericExpressionDefinition? Left { get; init; }
    public NumericExpressionDefinition? Right { get; init; }
    public float? Min { get; init; }
    public float? Max { get; init; }
}

internal enum NumericOpcode : byte
{
    Fact, Constant, Normalize, NormalizeRange, Add, Subtract, Multiply,
    Divide, Min, Max, Clamp, OneMinus, Abs
}

internal readonly record struct NumericInstruction(NumericOpcode Opcode, FactId Fact, float A = 0, float B = 0);

public sealed class CompiledNumericExpression
{
    public const int MaximumDepth = 16;
    public const int MaximumInstructions = 64;
    private readonly NumericInstruction[] _instructions;
    private readonly int _stackSize;
    public FactDependencyMask Dependencies { get; }

    private CompiledNumericExpression(NumericInstruction[] instructions, int stackSize)
    {
        _instructions = instructions;
        _stackSize = stackSize;
        Dependencies = instructions.Aggregate(FactDependencyMask.None,
            (mask, instruction) => mask | FactDependencyMask.From(instruction.Fact));
    }

    public static CompiledNumericExpression Compile(NumericExpressionDefinition? expression, FactRegistry facts)
    {
        ArgumentNullException.ThrowIfNull(facts);
        if (expression is null) throw new InvalidDataException("A numeric expression is required.");
        var instructions = new List<NumericInstruction>();
        var stackSize = CompileNode(expression, facts, instructions, 1);
        if (instructions.Count > MaximumInstructions)
            throw new InvalidDataException($"Numeric expression exceeds maximum complexity {MaximumInstructions}.");
        return new CompiledNumericExpression(instructions.ToArray(), stackSize);
    }

    internal float Evaluate(in DecisionFactContext context)
    {
        Span<float> stack = stackalloc float[_stackSize];
        var top = 0;
        foreach (var instruction in _instructions)
        {
            switch (instruction.Opcode)
            {
                case NumericOpcode.Fact: stack[top++] = context.Read(instruction.Fact); break;
                case NumericOpcode.Constant: stack[top++] = instruction.A; break;
                case NumericOpcode.Normalize:
                case NumericOpcode.NormalizeRange:
                    stack[top - 1] = Normalize(stack[top - 1], instruction.A, instruction.B); break;
                case NumericOpcode.OneMinus: stack[top - 1] = 1f - stack[top - 1]; break;
                case NumericOpcode.Abs: stack[top - 1] = MathF.Abs(stack[top - 1]); break;
                case NumericOpcode.Clamp: stack[top - 1] = Math.Clamp(stack[top - 1], instruction.A, instruction.B); break;
                default:
                    var right = stack[--top];
                    ref var left = ref stack[top - 1];
                    left = instruction.Opcode switch
                    {
                        NumericOpcode.Add => left + right,
                        NumericOpcode.Subtract => left - right,
                        NumericOpcode.Multiply => left * right,
                        NumericOpcode.Divide => right == 0 ? 0 : left / right,
                        NumericOpcode.Min => MathF.Min(left, right),
                        NumericOpcode.Max => MathF.Max(left, right),
                        _ => throw new InvalidOperationException("Unsupported compiled numeric opcode.")
                    };
                    break;
            }
        }
        return stack[0];
    }

    private static int CompileNode(NumericExpressionDefinition node, FactRegistry facts,
        List<NumericInstruction> output, int depth)
    {
        if (depth > MaximumDepth) throw new InvalidDataException($"Numeric expression exceeds maximum depth {MaximumDepth}.");
        if (output.Count >= MaximumInstructions) throw new InvalidDataException($"Numeric expression exceeds maximum complexity {MaximumInstructions}.");
        var op = node.Op?.ToLowerInvariant() ?? throw new InvalidDataException("Numeric expression op is required.");
        int stack;
        switch (op)
        {
            case "fact":
                var fact = facts.ResolveDefinition(node.Fact ?? string.Empty);
                if (fact.ValueKind != FactValueKind.Number)
                    throw new InvalidDataException($"Fact '{node.Fact}' is boolean and cannot be used in a numeric expression.");
                output.Add(new NumericInstruction(NumericOpcode.Fact, fact.Id));
                return 1;
            case "constant":
                RequireFinite(node.Value, "constant");
                output.Add(new NumericInstruction(NumericOpcode.Constant, default, node.Value!.Value));
                return 1;
            case "normalize":
                if (node.Input?.Op?.Equals("fact", StringComparison.OrdinalIgnoreCase) != true)
                    throw new InvalidDataException("normalize requires a direct fact input; use normalizeRange for computed values.");
                var normalizedFact = facts.ResolveDefinition(node.Input.Fact ?? string.Empty);
                stack = CompileNode(node.Input, facts, output, depth + 1);
                if (normalizedFact.Min == normalizedFact.Max) throw new InvalidDataException("normalize requires a fact with a non-zero range.");
                output.Add(new NumericInstruction(NumericOpcode.Normalize, default, normalizedFact.Min, normalizedFact.Max));
                return stack;
            case "normalizerange":
                ValidateRange(node.Min, node.Max, op);
                stack = CompileUnary(node, facts, output, depth);
                output.Add(new NumericInstruction(NumericOpcode.NormalizeRange, default, node.Min!.Value, node.Max!.Value));
                return stack;
            case "clamp":
                ValidateRange(node.Min, node.Max, op, allowEqual: true);
                stack = CompileUnary(node, facts, output, depth);
                output.Add(new NumericInstruction(NumericOpcode.Clamp, default, node.Min!.Value, node.Max!.Value));
                return stack;
            case "oneminus": case "abs":
                stack = CompileUnary(node, facts, output, depth);
                output.Add(new NumericInstruction(op == "oneminus" ? NumericOpcode.OneMinus : NumericOpcode.Abs, default));
                return stack;
            default:
                var opcode = op switch
                {
                    "add" => NumericOpcode.Add, "subtract" => NumericOpcode.Subtract,
                    "multiply" => NumericOpcode.Multiply, "divide" => NumericOpcode.Divide,
                    "min" => NumericOpcode.Min, "max" => NumericOpcode.Max,
                    _ => throw new InvalidDataException($"Unknown numeric expression op '{node.Op}'.")
                };
                if (node.Left is null || node.Right is null) throw new InvalidDataException($"{op} requires left and right expressions.");
                var leftStack = CompileNode(node.Left, facts, output, depth + 1);
                var rightStack = CompileNode(node.Right, facts, output, depth + 1);
                output.Add(new NumericInstruction(opcode, default));
                return Math.Max(leftStack, 1 + rightStack);
        }
    }

    private static int CompileUnary(NumericExpressionDefinition node, FactRegistry facts,
        List<NumericInstruction> output, int depth) => node.Input is null
            ? throw new InvalidDataException($"{node.Op} requires an input expression.")
            : CompileNode(node.Input, facts, output, depth + 1);

    private static void ValidateRange(float? min, float? max, string op, bool allowEqual = false)
    {
        RequireFinite(min, $"{op} min"); RequireFinite(max, $"{op} max");
        if (allowEqual ? min > max : min >= max) throw new InvalidDataException($"{op} requires min {(allowEqual ? "<=" : "<")} max.");
    }

    private static void RequireFinite(float? value, string field)
    {
        if (!value.HasValue || !float.IsFinite(value.Value)) throw new InvalidDataException($"Numeric expression {field} must be finite.");
    }

    private static float Normalize(float value, float min, float max) => Math.Clamp((value - min) / (max - min), 0f, 1f);
}

internal readonly record struct DecisionFactContext(
    WorldTime Time, JobDefinition Job, AgentAttributeValues Attributes, AgentLocation Location,
    AgentTravel Travel, int TargetEntityId, float TargetAffinity, int TargetLocationId = 0,
    AgentAttributeValues? TargetAttributes = null)
{
    public float Read(FactId fact) => fact.Kind switch
    {
        FactKind.AgentAttribute => Attributes[fact.Index],
        FactKind.TargetAttribute => TargetAttributes is not null && fact.Index < 16
            ? TargetAttributes.Value[fact.Index] : 0f,
        FactKind.TimeMinuteOfDay => Time.MinuteOfDay,
        FactKind.TimeDayOfWeek => Time.DayOfWeek,
        FactKind.JobWorkStartMinute => Job.WorkStartMinute,
        FactKind.JobWorkEndMinute => Job.WorkEndMinute,
        FactKind.TargetAffinity => TargetAffinity,
        FactKind.AgentLocationCurrent => Location.CurrentLocationId,
        FactKind.AgentLocationHome => Location.HomeLocationId,
        FactKind.AgentLocationWork => Location.WorkLocationId,
        FactKind.TargetEntity => TargetEntityId,
        FactKind.TargetLocationCurrent => TargetLocationId,
        _ => throw new InvalidOperationException($"Unsupported fact kind '{fact.Kind}'.")
    };

    public bool ReadBoolean(FactId fact) => fact.Kind switch
    {
        FactKind.TravelReachable => Location.CurrentLocationId == Location.HomeLocationId || Travel.RouteLocationIds.Length > 0,
        FactKind.JobIsWorkDay => Job.WorkDays.Contains(Time.DayOfWeek),
        _ => throw new InvalidOperationException($"Unsupported boolean fact kind '{fact.Kind}'.")
    };
}
