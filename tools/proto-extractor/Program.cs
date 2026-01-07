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
var rootTypes = rootsArg?.Substring(8).Split(',').Select(s => s.Trim()).ToHashSet();

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
    private readonly HashSet<string> _knownMessages = [];
    private readonly HashSet<string> _knownEnums = [];

    public void LoadAssembly(string path)
    {
        var resolver = new DefaultAssemblyResolver();
        resolver.AddSearchDirectory(Path.GetDirectoryName(path)!);
        _assembly = AssemblyDefinition.ReadAssembly(path, new ReaderParameters { AssemblyResolver = resolver });
    }

    public void ExtractAll()
    {
        if (_assembly == null) return;

        foreach (var type in _assembly.MainModule.Types.SelectMany(GetAllTypes))
        {
            if (type.IsEnum && type.Namespace?.Contains("ProtoBuf") == true)
                _knownEnums.Add(type.Name);
            else if (IsProtoMessage(type))
                _knownMessages.Add(type.Name);
        }

        var addedMessages = new HashSet<string>();
        var addedEnums = new HashSet<string>();
        
        foreach (var type in _assembly.MainModule.Types.SelectMany(GetAllTypes))
        {
            if (type.IsEnum && type.Namespace?.Contains("ProtoBuf") == true)
            {
                if (addedEnums.Add(type.Name))
                {
                    var protoEnum = ExtractEnum(type);
                    if (protoEnum != null) Enums.Add(protoEnum);
                }
            }
            else if (IsProtoMessage(type))
            {
                if (addedMessages.Add(type.Name))
                {
                    var msg = ExtractMessage(type);
                    Messages.Add(msg);
                }
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

    private ProtoEnum? ExtractEnum(TypeDefinition type)
    {
        var values = type.Fields
            .Where(f => f.IsStatic && f.IsLiteral && f.Constant is int)
            .Select(f => (f.Name, (int)f.Constant!))
            .ToList();
        return values.Count > 0 ? new ProtoEnum(type.Name, values) : null;
    }

    private ProtoMessage ExtractMessage(TypeDefinition type)
    {
        var fieldNumbers = ExtractFieldNumbers(type);
        var fields = new List<ProtoField>();

        foreach (var field in type.Fields.Where(f => !f.IsStatic && f.Name is not "ShouldPool" and not "_disposed"))
        {
            var (protoType, repeated) = MapType(field.FieldType);
            var fieldNumber = fieldNumbers.GetValueOrDefault(field.Name, 0);
            fields.Add(new ProtoField(field.Name, protoType, fieldNumber, repeated));
        }

        // Include empty messages too (like AppEmpty, AppSuccess)
        return new ProtoMessage(type.Name, fields);
    }

    private Dictionary<string, int> ExtractFieldNumbers(TypeDefinition type)
    {
        var result = new Dictionary<string, int>();
        var method = type.Methods.FirstOrDefault(m =>
            m.Name == "Serialize" && m.IsStatic && m.Parameters.Count == 2 &&
            m.Parameters[0].ParameterType.Name == "BufferStream");

        if (method?.Body == null) return result;

        var instrs = method.Body.Instructions.ToList();
        var usedWriteBytes = new HashSet<int>();

        // Find all WriteByte calls and collect tag sequences
        for (int i = 0; i < instrs.Count; i++)
        {
            if (usedWriteBytes.Contains(i)) continue;
            if (instrs[i].OpCode.Code != Code.Callvirt) continue;
            if ((instrs[i].Operand as MethodReference)?.Name != "WriteByte") continue;

            // Get the tag byte for this WriteByte
            if (!IsLoadConstant(FindPrecedingLdc(instrs, i), out int firstByte)) continue;
            
            var tagBytes = new List<int> { firstByte };
            int lastWriteIdx = i;
            usedWriteBytes.Add(i);

            // Check for continuation bytes (multi-byte varints)
            // Pattern: first byte has MSB set (>=128), next WriteByte follows
            if (firstByte >= 128)
            {
                // Look for next WriteByte sequence
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
                            if (nextByte < 128) break; // No more continuation
                        }
                        break;
                    }
                }
            }

            // Decode varint tag
            int tag = 0;
            for (int b = 0; b < tagBytes.Count; b++)
                tag |= (tagBytes[b] & 0x7F) << (7 * b);
            
            var fieldNum = tag >> 3;
            if (fieldNum <= 0 || fieldNum > 500) continue;

            string? foundField = null;

            // Look forward FIRST for Ldfld/Ldflda (standard pattern: WriteByte then access field)
            for (int j = lastWriteIdx + 1; j < Math.Min(lastWriteIdx + 12, instrs.Count); j++)
            {
                if (instrs[j].OpCode.Code is Code.Ldfld or Code.Ldflda && instrs[j].Operand is FieldReference fld)
                {
                    if (fld.Name is "ShouldPool" or "_disposed" or "Value" or "Count") continue;
                    foundField = fld.Name;
                    break;
                }
                // Stop if we hit another tag sequence
                if (IsLoadConstant(instrs[j], out int v) && v > 0 && v <= 255)
                {
                    if (j + 1 < instrs.Count && instrs[j + 1].OpCode.Code == Code.Callvirt &&
                        (instrs[j + 1].Operand as MethodReference)?.Name == "WriteByte")
                        break;
                }
            }

            // Only look backward if nothing found forward (handles cases where field is in condition)
            if (foundField == null)
            {
                for (int j = i - 1; j >= Math.Max(0, i - 25); j--)
                {
                    if (instrs[j].OpCode.Code is Code.Ldfld or Code.Ldflda && instrs[j].Operand is FieldReference fldBack)
                    {
                        if (fldBack.Name is "ShouldPool" or "_disposed" or "Value" or "Count" or "Item") continue;
                        if (fldBack.FieldType is GenericInstanceType git && git.ElementType.Name == "List`1")
                        {
                            // This is a List field - it's the repeated field
                            foundField = fldBack.Name;
                            break;
                        }
                        foundField = fldBack.Name;
                        break;
                    }
                }
            }

            if (foundField != null && !result.ContainsKey(foundField))
                result[foundField] = fieldNum;
        }

        return result;
    }

    private static Instruction FindPrecedingLdc(List<Instruction> instrs, int writeByteIdx)
    {
        for (int j = writeByteIdx - 1; j >= Math.Max(0, writeByteIdx - 3); j--)
        {
            if (instrs[j].OpCode.Code is Code.Ldc_I4 or Code.Ldc_I4_S or
                Code.Ldc_I4_0 or Code.Ldc_I4_1 or Code.Ldc_I4_2 or Code.Ldc_I4_3 or
                Code.Ldc_I4_4 or Code.Ldc_I4_5 or Code.Ldc_I4_6 or Code.Ldc_I4_7 or Code.Ldc_I4_8)
                return instrs[j];
        }
        return instrs[0]; // Won't match
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

    private (string Type, bool Repeated) MapType(TypeReference typeRef)
    {
        var name = typeRef.Name;
        var repeated = false;

        if (typeRef is GenericInstanceType git)
        {
            if (git.ElementType.Name == "List`1")
            {
                repeated = true;
                name = git.GenericArguments[0].Name;
            }
            else if (git.ElementType.Name == "ArraySegment`1")
                return ("bytes", false);
            else return (name, false);
        }
        else if (typeRef.IsArray)
        {
            if (name == "Byte[]") return ("bytes", false);
            repeated = true;
            name = name.TrimEnd('[', ']');
        }

        var proto = name switch
        {
            "UInt32" => "uint32", "UInt64" => "uint64", "Int32" => "int32", "Int64" => "int64",
            "Single" => "float", "Double" => "double", "Boolean" => "bool", "String" => "string",
            "NetworkableId" or "ItemId" or "ItemContainerId" => "uint64",
            _ when _knownEnums.Contains(name) || _knownMessages.Contains(name) => name,
            _ => name
        };
        return (proto, repeated);
    }

    public void ApplyRootFilter(HashSet<string> rootTypes)
    {
        // Build a dependency graph: for each message, track what it references and what references it
        // Use first occurrence in case of duplicate names (shouldn't happen in well-formed proto)
        var allMessages = new Dictionary<string, ProtoMessage>();
        foreach (var msg in Messages)
            allMessages.TryAdd(msg.Name, msg);
        
        var dependencies = new Dictionary<string, HashSet<string>>();  // type -> types it references
        var dependants = new Dictionary<string, HashSet<string>>();    // type -> types that reference it
        
        foreach (var msg in Messages)
        {
            if (!dependencies.ContainsKey(msg.Name))
                dependencies[msg.Name] = [];
            if (!dependants.ContainsKey(msg.Name))
                dependants[msg.Name] = [];
            
            foreach (var field in msg.Fields)
            {
                var typeName = field.Type;
                if (!IsPrimitiveType(typeName) && typeName != msg.Name)
                {
                    dependencies[msg.Name].Add(typeName);
                    if (!dependants.ContainsKey(typeName))
                        dependants[typeName] = [];
                    dependants[typeName].Add(msg.Name);
                }
            }
        }
        
        // Strategy: Start from roots, include all their dependencies recursively,
        // but only include dependants that are ALSO roots (prevents fan-out to unrelated types)
        var includedTypes = new HashSet<string>();
        var toProcess = new Queue<string>(rootTypes.Where(r => allMessages.ContainsKey(r)));
        
        // Add direct dependants of root types, but only if they're also roots
        foreach (var root in rootTypes.Where(r => allMessages.ContainsKey(r)))
        {
            if (dependants.TryGetValue(root, out var refs))
            {
                foreach (var refType in refs)
                    if (rootTypes.Contains(refType))
                        toProcess.Enqueue(refType);
            }
        }
        
        // Recursively add all dependencies
        while (toProcess.Count > 0)
        {
            var typeName = toProcess.Dequeue();
            if (includedTypes.Contains(typeName)) continue;
            includedTypes.Add(typeName);
            
            // Add dependencies (types this message references)
            if (dependencies.TryGetValue(typeName, out var deps))
            {
                foreach (var dep in deps)
                    if (!includedTypes.Contains(dep))
                        toProcess.Enqueue(dep);
            }
        }
        
        // Keep only included messages
        Messages.RemoveAll(m => !includedTypes.Contains(m.Name));
        
        // Now resolve any external dependencies (Unity types, enums, etc.)
        IncludeDependencies();
    }

    public void IncludeDependencies()
    {
        // Collect all types referenced by current messages that aren't yet included
        var referencedTypes = new HashSet<string>();
        var processedTypes = new HashSet<string>(Messages.Select(m => m.Name));
        var includedEnums = new HashSet<string>(Enums.Select(e => e.Name));
        
        void CollectReferences(ProtoMessage msg)
        {
            foreach (var field in msg.Fields)
            {
                var typeName = field.Type;
                if (!IsPrimitiveType(typeName) && !processedTypes.Contains(typeName))
                    referencedTypes.Add(typeName);
            }
        }

        // Initial collection
        foreach (var msg in Messages)
            CollectReferences(msg);

        // Iteratively add referenced messages and their dependencies
        while (referencedTypes.Count > 0)
        {
            var toProcess = referencedTypes.ToList();
            referencedTypes.Clear();

            foreach (var typeName in toProcess)
            {
                if (processedTypes.Contains(typeName)) continue;
                processedTypes.Add(typeName);

                // Check for Unity types first (Vector2, Vector3, etc.)
                if (IsUnityType(typeName))
                {
                    var unityMsg = new ProtoMessage(typeName, UnityTypeDefinitions[typeName]);
                    Messages.Add(unityMsg);
                    CollectReferences(unityMsg);
                    continue;
                }

                // Try to find as a message from the assembly
                var msg = FindMessageByName(typeName);
                if (msg != null)
                {
                    Messages.Add(msg);
                    CollectReferences(msg);
                    continue;
                }
                
                // Try to find as an enum
                if (!includedEnums.Contains(typeName))
                {
                    var enumType = FindEnumByName(typeName);
                    if (enumType != null)
                    {
                        Enums.Add(enumType);
                        includedEnums.Add(typeName);
                    }
                }
            }
        }

        // Keep only enums that are referenced
        var usedTypes = Messages.SelectMany(m => m.Fields.Select(f => f.Type)).ToHashSet();
        Enums.RemoveAll(e => !usedTypes.Contains(e.Name));
    }

    private ProtoMessage? FindMessageByName(string name)
    {
        if (_assembly == null) return null;
        
        foreach (var type in _assembly.MainModule.Types.SelectMany(GetAllTypes))
        {
            if (IsProtoMessage(type) && type.Name == name)
                return ExtractMessage(type);
        }
        return null;
    }
    
    private ProtoEnum? FindEnumByName(string name)
    {
        if (_assembly == null) return null;
        
        foreach (var type in _assembly.MainModule.Types.SelectMany(GetAllTypes))
        {
            if (type.IsEnum && type.Name == name)
                return ExtractEnum(type);
        }
        return null;
    }

    private static bool IsPrimitiveType(string typeName) =>
        typeName is "uint32" or "uint64" or "int32" or "int64" or "sint32" or "sint64"
            or "float" or "double" or "bool" or "string" or "bytes"
            or "fixed32" or "fixed64" or "sfixed32" or "sfixed64";

    // Unity types that don't implement IProto but are referenced
    private static readonly Dictionary<string, List<ProtoField>> UnityTypeDefinitions = new()
    {
        ["Vector2"] = [new("x", "float", 1, false), new("y", "float", 2, false)],
        ["Vector3"] = [new("x", "float", 1, false), new("y", "float", 2, false), new("z", "float", 3, false)],
        ["Vector4"] = [new("x", "float", 1, false), new("y", "float", 2, false), new("z", "float", 3, false), new("w", "float", 4, false)],
        ["Color"] = [new("r", "float", 1, false), new("g", "float", 2, false), new("b", "float", 3, false), new("a", "float", 4, false)],
        ["Ray"] = [new("origin", "Vector3", 1, false), new("direction", "Vector3", 2, false)],
    };
    
    private static bool IsUnityType(string typeName) => UnityTypeDefinitions.ContainsKey(typeName);

    public string GenerateProto(string packageName)
    {
        var sb = new StringBuilder();
        sb.AppendLine("syntax = \"proto3\";");
        sb.AppendLine($"\npackage {packageName};\n");

        // Collect all enum values to detect conflicts
        var allEnumValues = new Dictionary<string, List<string>>();
        foreach (var e in Enums)
        {
            foreach (var (name, _) in e.Values)
            {
                if (!allEnumValues.ContainsKey(name))
                    allEnumValues[name] = [];
                allEnumValues[name].Add(e.Name);
            }
        }
        var conflictingValues = allEnumValues.Where(kv => kv.Value.Count > 1).Select(kv => kv.Key).ToHashSet();

        foreach (var e in Enums.OrderBy(e => e.Name))
        {
            sb.AppendLine($"enum {e.Name} {{");
            
            // Proto3 requires first value to be 0 - use enum name prefix to avoid conflicts
            var hasZero = e.Values.Any(v => v.Value == 0);
            if (!hasZero)
            {
                Console.Error.WriteLine($"Warning: Enum '{e.Name}' has no zero value, adding '{e.Name}_Unknown = 0' for proto3 compatibility");
                sb.AppendLine($"  {e.Name}_Unknown = 0;");
            }
            
            foreach (var (name, val) in e.Values)
            {
                // Prefix enum values that conflict with values from other enums
                if (conflictingValues.Contains(name))
                {
                    Console.Error.WriteLine($"Warning: Enum value '{name}' conflicts across enums ({string.Join(", ", allEnumValues[name])}), prefixing as '{e.Name}_{name}'");
                    sb.AppendLine($"  {e.Name}_{name} = {val};");
                }
                else
                {
                    sb.AppendLine($"  {name} = {val};");
                }
            }
            sb.AppendLine("}\n");
        }

        foreach (var msg in Messages.OrderBy(m => m.Name))
        {
            sb.AppendLine($"message {msg.Name} {{");
            foreach (var f in msg.Fields.OrderBy(f => f.FieldNumber))
            {
                var rep = f.Repeated ? "repeated " : "";
                var num = f.FieldNumber > 0 ? f.FieldNumber.ToString() : "?";
                sb.AppendLine($"  {rep}{f.Type} {f.Name} = {num};");
            }
            sb.AppendLine("}\n");
        }
        return sb.ToString();
    }
}

record ProtoField(string Name, string Type, int FieldNumber, bool Repeated);
record ProtoEnum(string Name, List<(string Name, int Value)> Values);
record ProtoMessage(string Name, List<ProtoField> Fields);
