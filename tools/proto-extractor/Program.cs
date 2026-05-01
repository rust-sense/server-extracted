using System.Text;
using Mono.Cecil;
using Mono.Cecil.Cil;

var cliArgs = Environment.GetCommandLineArgs().Skip(1).ToArray();
if (cliArgs.Length < 2)
{
    Console.Error.WriteLine("Usage: ProtoExtractor <dll-path> <output.proto> [package-name] [--roots=Type1,Type2,...]");
    Console.Error.WriteLine("  --roots: Comma-separated list of root types to start from (includes all dependencies and dependants)");
    return 1;
}

var dllPath = cliArgs[0];
var outputPath = cliArgs[1];
var packageName = cliArgs.Length > 2 && !cliArgs[2].StartsWith("--") ? cliArgs[2] : "rustplus";
var rootsArg = cliArgs.FirstOrDefault(a => a.StartsWith("--roots="));
var rootTypes = rootsArg?.Substring(8).Split(',').Select(s => s.Trim()).Where(s => s.Length > 0).ToHashSet();

var extractor = new ProtoExtractor();
extractor.LoadAssembly(dllPath);
extractor.ExtractAll();
if (rootTypes != null && rootTypes.Count > 0) extractor.ApplyRootFilter(rootTypes);

File.WriteAllText(outputPath, extractor.GenerateProto(packageName));
Console.WriteLine($"Generated {outputPath}: {extractor.Messages.Count} messages, {extractor.Enums.Count} enums");
return 0;

class ProtoExtractor
{
    public List<ProtoMessage> Messages { get; } = [];
    public List<ProtoEnum> Enums { get; } = [];

    private AssemblyDefinition? _assembly;
    private readonly Dictionary<string, TypeDefinition> _messageTypes = [];
    private readonly Dictionary<string, TypeDefinition> _enumTypes = [];
    private readonly Dictionary<string, ProtoMessage> _allMessages = [];
    private readonly Dictionary<string, ProtoEnum> _allEnums = [];
    private readonly Dictionary<string, string> _messageKeysByFullName = [];
    private readonly Dictionary<string, string> _enumKeysByFullName = [];

    public void LoadAssembly(string path)
    {
        var resolver = new DefaultAssemblyResolver();
        resolver.AddSearchDirectory(Path.GetDirectoryName(path)!);
        _assembly = AssemblyDefinition.ReadAssembly(path, new ReaderParameters { AssemblyResolver = resolver });
    }

    public void ExtractAll()
    {
        if (_assembly == null) return;

        BuildTypeRegistry();
        Messages.Clear();
        Enums.Clear();
        _allMessages.Clear();
        _allEnums.Clear();

        foreach (var type in _messageTypes.Values.OrderBy(t => GetTypeKey(t), StringComparer.Ordinal))
            _allMessages[GetTypeKey(type)] = ExtractMessage(type);

        foreach (var type in _enumTypes.Values.OrderBy(t => GetTypeKey(t), StringComparer.Ordinal))
        {
            var protoEnum = ExtractEnum(type);
            if (protoEnum != null) _allEnums[GetTypeKey(type)] = protoEnum;
        }

        Messages.AddRange(_allMessages.Values);
        Enums.AddRange(_allEnums.Values);
        AssignProtoNames();
    }

    private void BuildTypeRegistry()
    {
        _messageTypes.Clear();
        _enumTypes.Clear();
        _messageKeysByFullName.Clear();
        _enumKeysByFullName.Clear();

        if (_assembly == null) return;

        foreach (var type in _assembly.MainModule.Types.SelectMany(GetAllTypes))
        {
            var key = GetTypeKey(type);
            if (IsProtoEnum(type))
            {
                _enumTypes[key] = type;
                _enumKeysByFullName[type.FullName] = key;
            }
            else if (IsProtoMessage(type))
            {
                _messageTypes[key] = type;
                _messageKeysByFullName[type.FullName] = key;
            }
        }
    }

    private static IEnumerable<TypeDefinition> GetAllTypes(TypeDefinition type)
    {
        yield return type;
        foreach (var nested in type.NestedTypes.SelectMany(GetAllTypes))
            yield return nested;
    }

    private static bool IsProtoMessage(TypeDefinition type) =>
        type.IsClass && !type.IsAbstract &&
        type.Interfaces.Any(i => i.InterfaceType.Name == "IProto" || i.InterfaceType.Name.StartsWith("IProto`"));

    private static bool IsProtoEnum(TypeDefinition type) =>
        type.IsEnum && GetTypeNamespace(type).Contains("ProtoBuf", StringComparison.Ordinal);

    private static string GetTypeNamespace(TypeDefinition type)
    {
        for (var current = type; current != null; current = current.DeclaringType)
        {
            if (!string.IsNullOrEmpty(current.Namespace))
                return current.Namespace;
        }

        return "";
    }

    private static string GetTypeKey(TypeDefinition type) => type.FullName;

    private static string? GetDeclaringTypeKey(TypeDefinition type) => type.DeclaringType?.FullName;

    private ProtoEnum? ExtractEnum(TypeDefinition type)
    {
        var values = type.Fields
            .Where(f => f.IsStatic && f.IsLiteral && f.Constant is int)
            .Select(f => (f.Name, (int)f.Constant!))
            .ToList();

        return values.Count > 0
            ? new ProtoEnum(GetTypeKey(type), type.Name, type.Name, GetDeclaringTypeKey(type), values)
            : null;
    }

    private ProtoMessage ExtractMessage(TypeDefinition type)
    {
        var fieldMetadata = ExtractFieldMetadata(type);
        var fields = new List<ProtoField>();

        foreach (var field in type.Fields.Where(f => !f.IsStatic && f.Name is not "ShouldPool" and not "_disposed"))
        {
            var mapped = MapType(field.FieldType);
            fieldMetadata.TryGetValue(field.Name, out var metadata);
            var fieldNumber = metadata?.FieldNumber ?? 0;
            var optional = mapped.IsScalar && !mapped.Repeated && metadata?.Optional == true;
            fields.Add(new ProtoField(field.Name, mapped.Type, fieldNumber, mapped.Repeated, optional, mapped.TypeKey));
        }

        // Include empty messages too (like AppEmpty, AppSuccess)
        return new ProtoMessage(GetTypeKey(type), type.Name, type.Name, GetDeclaringTypeKey(type), fields);
    }

    private Dictionary<string, FieldMetadata> ExtractFieldMetadata(TypeDefinition type)
    {
        var result = new Dictionary<string, FieldMetadata>();
        var method = type.Methods.FirstOrDefault(m =>
            m.Name == "Serialize" && m.IsStatic && m.Parameters.Count == 2 &&
            m.Parameters[0].ParameterType.Name == "BufferStream");

        if (method?.Body == null) return result;

        var instrs = method.Body.Instructions.ToList();
        var usedWriteBytes = new HashSet<int>();

        // Find all WriteByte calls and collect tag sequences.
        for (int i = 0; i < instrs.Count; i++)
        {
            if (usedWriteBytes.Contains(i)) continue;
            if (instrs[i].OpCode.Code != Code.Callvirt) continue;
            if ((instrs[i].Operand as MethodReference)?.Name != "WriteByte") continue;

            if (!IsLoadConstant(FindPrecedingLdc(instrs, i), out int firstByte)) continue;

            var tagBytes = new List<int> { firstByte };
            int lastWriteIdx = i;
            usedWriteBytes.Add(i);

            if (firstByte >= 128)
            {
                for (int k = i + 1; k < Math.Min(i + 5, instrs.Count); k++)
                {
                    if (instrs[k].OpCode.Code == Code.Callvirt &&
                        (instrs[k].Operand as MethodReference)?.Name == "WriteByte")
                    {
                        if (IsLoadConstant(FindPrecedingLdc(instrs, k), out int nextByte))
                        {
                            tagBytes.Add(nextByte);
                            usedWriteBytes.Add(k);
                            lastWriteIdx = k;
                            if (nextByte < 128) break;
                        }
                        break;
                    }
                }
            }

            int tag = 0;
            for (int b = 0; b < tagBytes.Count; b++)
                tag |= (tagBytes[b] & 0x7F) << (7 * b);

            var fieldNum = tag >> 3;
            if (fieldNum <= 0 || fieldNum > 500) continue;

            string? foundField = null;

            // Look forward first for the standard pattern: write tag, then access field.
            for (int j = lastWriteIdx + 1; j < Math.Min(lastWriteIdx + 12, instrs.Count); j++)
            {
                if (instrs[j].OpCode.Code is Code.Ldfld or Code.Ldflda && instrs[j].Operand is FieldReference fld)
                {
                    if (fld.Name is "ShouldPool" or "_disposed" or "Value" or "Count") continue;
                    foundField = fld.Name;
                    break;
                }

                if (IsLoadConstant(instrs[j], out int v) && v > 0 && v <= 255)
                {
                    if (j + 1 < instrs.Count && instrs[j + 1].OpCode.Code == Code.Callvirt &&
                        (instrs[j + 1].Operand as MethodReference)?.Name == "WriteByte")
                        break;
                }
            }

            // Conditional serializers often load the field before a branch guarding the tag write.
            if (foundField == null)
            {
                for (int j = i - 1; j >= Math.Max(0, i - 25); j--)
                {
                    if (instrs[j].OpCode.Code is Code.Ldfld or Code.Ldflda && instrs[j].Operand is FieldReference fldBack)
                    {
                        if (fldBack.Name is "ShouldPool" or "_disposed" or "Value" or "Count" or "Item") continue;
                        foundField = fldBack.Name;
                        break;
                    }
                }
            }

            if (foundField != null && !result.ContainsKey(foundField))
            {
                var optional = IsGuardedFieldWrite(instrs, i, foundField);
                result[foundField] = new FieldMetadata(fieldNum, optional);
            }
        }

        return result;
    }

    private static bool IsGuardedFieldWrite(List<Instruction> instrs, int writeByteIdx, string fieldName)
    {
        var searchStart = Math.Max(0, writeByteIdx - 45);

        for (int i = writeByteIdx - 1; i >= searchStart; i--)
        {
            if (instrs[i].OpCode.Code is not (Code.Ldfld or Code.Ldflda)) continue;
            if (instrs[i].Operand is not FieldReference fld || fld.Name != fieldName) continue;

            for (int j = i + 1; j < writeByteIdx; j++)
            {
                if (!IsConditionalBranch(instrs[j])) continue;
                if (instrs[j].Operand is not Instruction target) continue;
                if (target.Offset > instrs[writeByteIdx].Offset)
                    return true;
            }
        }

        return false;
    }

    private static bool IsConditionalBranch(Instruction instruction) => instruction.OpCode.Code is
        Code.Brfalse or Code.Brfalse_S or Code.Brtrue or Code.Brtrue_S or
        Code.Beq or Code.Beq_S or Code.Bge or Code.Bge_S or Code.Bge_Un or Code.Bge_Un_S or
        Code.Bgt or Code.Bgt_S or Code.Bgt_Un or Code.Bgt_Un_S or Code.Ble or Code.Ble_S or
        Code.Ble_Un or Code.Ble_Un_S or Code.Blt or Code.Blt_S or Code.Blt_Un or Code.Blt_Un_S or
        Code.Bne_Un or Code.Bne_Un_S;

    private static Instruction FindPrecedingLdc(List<Instruction> instrs, int writeByteIdx)
    {
        for (int j = writeByteIdx - 1; j >= Math.Max(0, writeByteIdx - 3); j--)
        {
            if (instrs[j].OpCode.Code is Code.Ldc_I4 or Code.Ldc_I4_S or
                Code.Ldc_I4_0 or Code.Ldc_I4_1 or Code.Ldc_I4_2 or Code.Ldc_I4_3 or
                Code.Ldc_I4_4 or Code.Ldc_I4_5 or Code.Ldc_I4_6 or Code.Ldc_I4_7 or Code.Ldc_I4_8)
                return instrs[j];
        }
        return instrs[0];
    }

    private static bool IsLoadConstant(Instruction ins, out int value)
    {
        value = ins.OpCode.Code switch
        {
            Code.Ldc_I4_0 => 0, Code.Ldc_I4_1 => 1, Code.Ldc_I4_2 => 2, Code.Ldc_I4_3 => 3,
            Code.Ldc_I4_4 => 4, Code.Ldc_I4_5 => 5, Code.Ldc_I4_6 => 6, Code.Ldc_I4_7 => 7,
            Code.Ldc_I4_8 => 8, Code.Ldc_I4_M1 => -1,
            Code.Ldc_I4_S => (sbyte)ins.Operand,
            Code.Ldc_I4 => (int)ins.Operand,
            _ => int.MinValue
        };
        return value != int.MinValue;
    }

    private MappedType MapType(TypeReference typeRef)
    {
        var repeated = false;
        var effectiveType = typeRef;

        if (typeRef is GenericInstanceType git)
        {
            if (git.ElementType.Name == "List`1")
            {
                repeated = true;
                effectiveType = git.GenericArguments[0];
            }
            else if (git.ElementType.Name == "ArraySegment`1")
                return new MappedType("bytes", null, false, true);
            else
                return new MappedType(typeRef.Name, null, false, false);
        }
        else if (typeRef.IsArray)
        {
            if (typeRef.Name == "Byte[]") return new MappedType("bytes", null, false, true);
            repeated = true;
            effectiveType = typeRef.GetElementType();
        }

        var name = effectiveType.Name;
        var primitive = name switch
        {
            "UInt32" => "uint32", "UInt64" => "uint64", "Int32" => "int32", "Int64" => "int64",
            "Single" => "float", "Double" => "double", "Boolean" => "bool", "String" => "string",
            "NetworkableId" or "ItemId" or "ItemContainerId" => "uint64",
            _ => null
        };

        if (primitive != null)
            return new MappedType(primitive, null, repeated, true);

        var resolvedKey = ResolveKnownTypeKey(effectiveType);
        return resolvedKey != null
            ? new MappedType(name, resolvedKey, repeated, false)
            : new MappedType(name, null, repeated, false);
    }

    private string? ResolveKnownTypeKey(TypeReference typeRef)
    {
        var fullName = typeRef.FullName;
        if (_messageKeysByFullName.TryGetValue(fullName, out var messageKey)) return messageKey;
        if (_enumKeysByFullName.TryGetValue(fullName, out var enumKey)) return enumKey;

        try
        {
            var resolved = typeRef.Resolve();
            if (resolved == null) return null;

            var key = GetTypeKey(resolved);
            if (_messageTypes.ContainsKey(key) || _enumTypes.ContainsKey(key)) return key;
        }
        catch (AssemblyResolutionException)
        {
            return null;
        }

        return null;
    }

    public void ApplyRootFilter(HashSet<string> rootTypes)
    {
        var allMessages = _allMessages.Count > 0 ? _allMessages : Messages.ToDictionary(m => m.SourceKey);
        var rootKeys = ResolveRootKeys(rootTypes, allMessages);

        if (rootKeys.Count == 0)
        {
            Console.Error.WriteLine($"Warning: no root types matched: {string.Join(", ", rootTypes.OrderBy(r => r, StringComparer.Ordinal))}");
            Messages.Clear();
            Enums.Clear();
            return;
        }

        var dependencies = allMessages.Values.ToDictionary(m => m.SourceKey, m => new HashSet<string>());
        var dependants = allMessages.Values.ToDictionary(m => m.SourceKey, m => new HashSet<string>());

        foreach (var msg in allMessages.Values)
        {
            foreach (var field in msg.Fields)
            {
                if (field.TypeKey == null || !allMessages.ContainsKey(field.TypeKey) || field.TypeKey == msg.SourceKey) continue;
                dependencies[msg.SourceKey].Add(field.TypeKey);
                dependants[field.TypeKey].Add(msg.SourceKey);
            }
        }

        var includedTypes = new HashSet<string>();
        var toProcess = new Queue<string>(rootKeys);

        foreach (var root in rootKeys)
        {
            foreach (var refType in dependants[root])
                if (rootKeys.Contains(refType))
                    toProcess.Enqueue(refType);
        }

        while (toProcess.Count > 0)
        {
            var typeKey = toProcess.Dequeue();
            if (!includedTypes.Add(typeKey)) continue;

            foreach (var dep in dependencies.GetValueOrDefault(typeKey) ?? [])
                if (!includedTypes.Contains(dep))
                    toProcess.Enqueue(dep);
        }

        Messages.Clear();
        Messages.AddRange(includedTypes.Select(k => allMessages[k]));

        IncludeDependencies();
    }

    private HashSet<string> ResolveRootKeys(HashSet<string> rootTypes, Dictionary<string, ProtoMessage> allMessages)
    {
        var result = new HashSet<string>();

        foreach (var root in rootTypes)
        {
            var matches = allMessages.Values
                .Where(m => m.SourceKey == root || m.SourceName == root || m.ProtoName == root || m.SourceKey.EndsWith("." + root, StringComparison.Ordinal) || m.SourceKey.EndsWith("/" + root, StringComparison.Ordinal))
                .Select(m => m.SourceKey)
                .OrderBy(k => k, StringComparer.Ordinal)
                .ToList();

            if (matches.Count == 0)
            {
                Console.Error.WriteLine($"Warning: root type '{root}' was not found");
                continue;
            }

            if (matches.Count > 1)
                Console.Error.WriteLine($"Warning: root type '{root}' matched multiple metadata types: {string.Join(", ", matches)}");

            foreach (var match in matches)
                result.Add(match);
        }

        return result;
    }

    public void IncludeDependencies()
    {
        var includedMessages = Messages.Select(m => m.SourceKey).ToHashSet();
        var includedEnums = Enums.Select(e => e.SourceKey).ToHashSet();
        var queue = new Queue<string>(Messages.SelectMany(m => m.Fields).Select(f => f.TypeKey).Where(k => k != null)!);

        while (queue.Count > 0)
        {
            var typeKey = queue.Dequeue();
            if (typeKey == null) continue;

            if (_allMessages.TryGetValue(typeKey, out var msg))
            {
                if (!includedMessages.Add(typeKey)) continue;
                Messages.Add(msg);

                foreach (var dep in msg.Fields.Select(f => f.TypeKey).Where(k => k != null))
                    queue.Enqueue(dep!);
                continue;
            }

            if (_allEnums.TryGetValue(typeKey, out var enumType) && includedEnums.Add(typeKey))
                Enums.Add(enumType);
        }

        var neededUnityTypes = new Queue<string>(Messages.SelectMany(m => m.Fields).Where(f => f.TypeKey == null).Select(f => f.Type));
        var includedUnityTypes = Messages.Where(m => m.SourceKey.StartsWith("Unity:", StringComparison.Ordinal)).Select(m => m.SourceName).ToHashSet();

        while (neededUnityTypes.Count > 0)
        {
            var typeName = neededUnityTypes.Dequeue();
            if (!IsUnityType(typeName) || !includedUnityTypes.Add(typeName)) continue;

            var unityMsg = new ProtoMessage("Unity:" + typeName, typeName, typeName, null, UnityTypeDefinitions[typeName]);
            Messages.Add(unityMsg);

            foreach (var dep in unityMsg.Fields.Select(f => f.Type).Where(IsUnityType))
                neededUnityTypes.Enqueue(dep);
        }

        var usedTypeKeys = Messages.SelectMany(m => m.Fields).Select(f => f.TypeKey).Where(k => k != null).ToHashSet();
        Enums.RemoveAll(e => !usedTypeKeys.Contains(e.SourceKey));
    }

    private static bool IsPrimitiveType(string typeName) =>
        typeName is "uint32" or "uint64" or "int32" or "int64" or "sint32" or "sint64"
            or "float" or "double" or "bool" or "string" or "bytes"
            or "fixed32" or "fixed64" or "sfixed32" or "sfixed64";

    private static readonly Dictionary<string, List<ProtoField>> UnityTypeDefinitions = new()
    {
        ["Vector2"] = [new("x", "float", 1, false, false), new("y", "float", 2, false, false)],
        ["Vector3"] = [new("x", "float", 1, false, false), new("y", "float", 2, false, false), new("z", "float", 3, false, false)],
        ["Vector4"] = [new("x", "float", 1, false, false), new("y", "float", 2, false, false), new("z", "float", 3, false, false), new("w", "float", 4, false, false)],
        ["Color"] = [new("r", "float", 1, false, false), new("g", "float", 2, false, false), new("b", "float", 3, false, false), new("a", "float", 4, false, false)],
        ["Ray"] = [new("origin", "Vector3", 1, false, false), new("direction", "Vector3", 2, false, false)],
    };

    private static bool IsUnityType(string typeName) => UnityTypeDefinitions.ContainsKey(typeName);

    public string GenerateProto(string packageName)
    {
        AssignProtoNames();

        var sb = new StringBuilder();
        sb.AppendLine("syntax = \"proto3\";");
        sb.AppendLine($"\npackage {packageName};\n");

        var includedMessageKeys = Messages.Select(m => m.SourceKey).ToHashSet();
        var includedEnumKeys = Enums.Select(e => e.SourceKey).ToHashSet();

        foreach (var e in Enums.Where(e => !IsNestedInIncludedMessage(e.ParentKey, includedMessageKeys)).OrderBy(e => e.ProtoName, StringComparer.Ordinal))
            EmitEnum(sb, e, 0);

        foreach (var msg in Messages.Where(m => !IsNestedInIncludedMessage(m.ParentKey, includedMessageKeys)).OrderBy(m => m.ProtoName, StringComparer.Ordinal))
            EmitMessage(sb, msg, 0, includedMessageKeys, includedEnumKeys);

        return sb.ToString();
    }

    private void EmitMessage(StringBuilder sb, ProtoMessage msg, int indent, HashSet<string> includedMessageKeys, HashSet<string> includedEnumKeys)
    {
        var pad = new string(' ', indent);
        sb.AppendLine($"{pad}message {msg.ProtoName} {{");

        foreach (var f in msg.Fields.OrderBy(f => f.FieldNumber == 0 ? int.MaxValue : f.FieldNumber).ThenBy(f => f.Name, StringComparer.Ordinal))
        {
            var label = f.Repeated ? "repeated " : f.Optional ? "optional " : "";
            var num = f.FieldNumber > 0 ? f.FieldNumber.ToString() : "?";
            sb.AppendLine($"{pad}  {label}{GetFieldTypeName(f, msg, includedMessageKeys, includedEnumKeys)} {f.Name} = {num};");
        }

        if (msg.Fields.Count > 0 && (Enums.Any(e => e.ParentKey == msg.SourceKey) || Messages.Any(m => m.ParentKey == msg.SourceKey)))
            sb.AppendLine();

        foreach (var nestedEnum in Enums.Where(e => e.ParentKey == msg.SourceKey).OrderBy(e => e.ProtoName, StringComparer.Ordinal))
            EmitEnum(sb, nestedEnum, indent + 2);

        foreach (var nestedMsg in Messages.Where(m => m.ParentKey == msg.SourceKey).OrderBy(m => m.ProtoName, StringComparer.Ordinal))
            EmitMessage(sb, nestedMsg, indent + 2, includedMessageKeys, includedEnumKeys);

        sb.AppendLine($"{pad}}}");
        sb.AppendLine();
    }

    private void EmitEnum(StringBuilder sb, ProtoEnum e, int indent)
    {
        var pad = new string(' ', indent);
        sb.AppendLine($"{pad}enum {e.ProtoName} {{");

        var values = GetEnumValuesForScope(e);
        var hasZero = values.Any(v => v.Value == 0);
        if (!hasZero)
        {
            Console.Error.WriteLine($"Warning: Enum '{e.SourceKey}' has no zero value, adding '{e.ProtoName}_Unknown = 0' for proto3 compatibility");
            sb.AppendLine($"{pad}  {e.ProtoName}_Unknown = 0;");
        }

        foreach (var (name, val) in values)
            sb.AppendLine($"{pad}  {name} = {val};");

        sb.AppendLine($"{pad}}}");
        sb.AppendLine();
    }

    private List<(string Name, int Value)> GetEnumValuesForScope(ProtoEnum e)
    {
        var includedMessageKeys = Messages.Select(m => m.SourceKey).ToHashSet();
        var scopeKey = IsNestedInIncludedMessage(e.ParentKey, includedMessageKeys) ? e.ParentKey : null;
        var scopedEnums = Enums.Where(other => (IsNestedInIncludedMessage(other.ParentKey, includedMessageKeys) ? other.ParentKey : null) == scopeKey);
        var conflicts = scopedEnums
            .SelectMany(other => other.Values.Select(v => (v.Name, EnumName: other.ProtoName)))
            .GroupBy(v => v.Name, StringComparer.Ordinal)
            .Where(g => g.Select(v => v.EnumName).Distinct(StringComparer.Ordinal).Count() > 1)
            .Select(g => g.Key)
            .ToHashSet(StringComparer.Ordinal);

        return e.Values
            .Select(v =>
            {
                if (!conflicts.Contains(v.Name)) return v;
                var renamed = $"{e.ProtoName}_{v.Name}";
                Console.Error.WriteLine($"Warning: Enum value '{v.Name}' conflicts in proto scope, prefixing as '{renamed}'");
                return (renamed, v.Value);
            })
            .ToList();
    }

    private string GetFieldTypeName(ProtoField field, ProtoMessage currentMessage, HashSet<string> includedMessageKeys, HashSet<string> includedEnumKeys)
    {
        if (field.TypeKey == null)
            return field.Type;

        if (_allMessages.TryGetValue(field.TypeKey, out var msg) && includedMessageKeys.Contains(field.TypeKey))
            return GetRelativeTypeName(msg.SourceKey, msg.ProtoName, msg.ParentKey, currentMessage, includedMessageKeys);

        if (_allEnums.TryGetValue(field.TypeKey, out var enumType) && includedEnumKeys.Contains(field.TypeKey))
            return GetRelativeTypeName(enumType.SourceKey, enumType.ProtoName, enumType.ParentKey, currentMessage, includedMessageKeys);

        return field.Type;
    }

    private string GetRelativeTypeName(string targetKey, string targetProtoName, string? targetParentKey, ProtoMessage currentMessage, HashSet<string> includedMessageKeys)
    {
        if (targetParentKey == currentMessage.SourceKey)
            return targetProtoName;

        if (targetParentKey == currentMessage.ParentKey)
            return targetProtoName;

        if (targetParentKey != null && includedMessageKeys.Contains(targetParentKey) && _allMessages.TryGetValue(targetParentKey, out var parent))
            return $"{parent.ProtoName}.{targetProtoName}";

        return targetProtoName;
    }

    private static bool IsNestedInIncludedMessage(string? parentKey, HashSet<string> includedMessageKeys) =>
        parentKey != null && includedMessageKeys.Contains(parentKey);

    private void AssignProtoNames()
    {
        var includedMessageKeys = Messages.Select(x => x.SourceKey).ToHashSet();
        string? ScopeFor(string? parentKey) => IsNestedInIncludedMessage(parentKey, includedMessageKeys) ? parentKey : null;

        AssignNames(Messages, m => ScopeFor(m.ParentKey), m => m.SourceName, (m, name) => m.ProtoName = name);
        AssignNames(Enums, e => ScopeFor(e.ParentKey), e => e.SourceName, (e, name) => e.ProtoName = name);
        RenameCrossKindCollisions(ScopeFor);
    }

    private void RenameCrossKindCollisions(Func<string?, string?> scopeSelector)
    {
        var scopedNames = Messages
            .Select(m => new ScopedProtoName(scopeSelector(m.ParentKey), m.ProtoName, m.SourceKey, "message"))
            .Concat(Enums.Select(e => new ScopedProtoName(scopeSelector(e.ParentKey), e.ProtoName, e.SourceKey, "enum")))
            .GroupBy(x => $"{x.ScopeKey}\0{x.ProtoName}", StringComparer.Ordinal);

        foreach (var group in scopedNames.Where(g => g.Select(x => x.Kind).Distinct(StringComparer.Ordinal).Count() > 1))
        {
            foreach (var entry in group.Where(x => x.Kind == "enum").OrderBy(x => x.SourceKey, StringComparer.Ordinal))
            {
                var enumType = Enums.First(e => e.SourceKey == entry.SourceKey);
                var renamed = BuildDisambiguatedName(enumType.SourceKey) + "Enum";
                Console.Error.WriteLine($"Warning: Enum '{enumType.SourceKey}' conflicts with another type as '{enumType.ProtoName}', emitting as '{renamed}'");
                enumType.ProtoName = renamed;
            }
        }
    }

    private static void AssignNames<T>(IEnumerable<T> items, Func<T, string?> scopeSelector, Func<T, string> nameSelector, Action<T, string> nameSetter) where T : IProtoType
    {
        foreach (var group in items.GroupBy(scopeSelector))
        {
            var bySimpleName = group.GroupBy(nameSelector, StringComparer.Ordinal);
            foreach (var nameGroup in bySimpleName)
            {
                if (nameGroup.Count() == 1)
                {
                    nameSetter(nameGroup.First(), nameGroup.Key);
                    continue;
                }

                foreach (var item in nameGroup.OrderBy(i => i.SourceKey, StringComparer.Ordinal))
                {
                    var renamed = BuildDisambiguatedName(item.SourceKey);
                    Console.Error.WriteLine($"Warning: Type '{item.SourceKey}' conflicts as '{nameGroup.Key}', emitting as '{renamed}'");
                    nameSetter(item, renamed);
                }
            }
        }
    }

    private static string BuildDisambiguatedName(string sourceKey)
    {
        var parts = sourceKey
            .Split(['.', '/', '+'], StringSplitOptions.RemoveEmptyEntries)
            .Select(SanitizeIdentifierPart)
            .Where(p => p.Length > 0)
            .ToList();

        if (parts.Count == 0) return "Type";
        if (parts.Count == 1) return parts[0];
        return string.Concat(parts.TakeLast(Math.Min(3, parts.Count)));
    }

    private static string SanitizeIdentifierPart(string value)
    {
        var sb = new StringBuilder();
        foreach (var ch in value)
        {
            if (char.IsLetterOrDigit(ch) || ch == '_') sb.Append(ch);
        }
        return sb.ToString();
    }
}

interface IProtoType
{
    string SourceKey { get; }
}

record FieldMetadata(int FieldNumber, bool Optional);
record MappedType(string Type, string? TypeKey, bool Repeated, bool IsScalar);
record ScopedProtoName(string? ScopeKey, string ProtoName, string SourceKey, string Kind);
record ProtoField(string Name, string Type, int FieldNumber, bool Repeated, bool Optional, string? TypeKey = null);
record ProtoEnum(string SourceKey, string SourceName, string ProtoName, string? ParentKey, List<(string Name, int Value)> Values) : IProtoType
{
    public string ProtoName { get; set; } = ProtoName;
}
record ProtoMessage(string SourceKey, string SourceName, string ProtoName, string? ParentKey, List<ProtoField> Fields) : IProtoType
{
    public string ProtoName { get; set; } = ProtoName;
}
