using System.Globalization;
using System.Text;
using Waddamburo.Formats.Lmb;

namespace LumenStudio;

/// <summary>
/// Read-only pseudo-ActionScript for Lumen AVM1 actions: a stack simulation that rebuilds expressions,
/// calls and assignments. Unknown opcodes are shown as comments; editing uses the assembly view.
/// </summary>
internal static class ScriptSummary
{
    public static string OneLine(LmbAction action, IReadOnlyList<LmbString> strings)
    {
        var lines = Pseudocode(action, strings).Split('\n').Select(l => l.Trim()).Where(l => l.Length > 0 && !l.StartsWith("//", StringComparison.Ordinal)).ToArray();
        var text = string.Join("  ", lines.Take(3));
        return lines.Length > 3 ? text + " …" : text;
    }

    public static string Pseudocode(LmbAction action, IReadOnlyList<LmbString> strings)
    {
        var output = new StringBuilder();
        try { block(action.Code, strings, output, 0); }
        catch (Exception exception) when (exception is InvalidOperationException or ArgumentException or IndexOutOfRangeException)
        {
            output.AppendLine("// (decompilation stopped: " + exception.Message + ")");
        }
        return output.ToString();
    }

    private static void block(Avm1CodeBlock code, IReadOnlyList<LmbString> strings, StringBuilder output, int depth)
    {
        var stack = new List<string>();
        var registers = new Dictionary<int, string>();
        var indent = new string(' ', depth * 4);
        var targets = code.Instructions.Where(i => i.BranchTarget.HasValue).Select(i => i.BranchTarget!.Value).ToHashSet();
        string pop() { if (stack.Count == 0) return "?"; var v = stack[^1]; stack.RemoveAt(stack.Count - 1); return v; }
        void line(string text) { output.Append(indent).AppendLine(text); }
        string str(int index) => index < strings.Count ? strings[index].Value : $"s{index}";
        string quote(string s) => "\"" + s.Replace("\\", "\\\\", StringComparison.Ordinal).Replace("\"", "\\\"", StringComparison.Ordinal).Replace("\n", "\\n", StringComparison.Ordinal) + "\"";
        string member(string obj, string name)
        {
            var bare = name.Length > 2 && name[0] == '"' && name[^1] == '"' ? name[1..^1] : null;
            return bare is not null && bare.All(c => char.IsLetterOrDigit(c) || c == '_') && bare.Length > 0 && !char.IsDigit(bare[0]) ? $"{obj}.{bare}" : $"{obj}[{name}]";
        }
        string binary(string op) { var b = pop(); var a = pop(); return $"({a} {op} {b})"; }
        List<string> args(int count) { var list = new List<string>(); for (var i = 0; i < count; i++) list.Add(pop()); return list; }
        int count(string value) => int.TryParse(value, NumberStyles.Integer, CultureInfo.InvariantCulture, out var n) ? Math.Clamp(n, 0, 64) : 0;

        foreach (var instruction in code.Instructions)
        {
            if (targets.Contains(instruction.Offset)) output.Append(indent).AppendLine($"L{instruction.Offset:X4}:");
            switch (instruction.Opcode)
            {
                case 0x00: break;
                case 0x96 when instruction.Operand is Avm1PushOperand push:
                    foreach (var value in push.Values)
                        stack.Add(value switch
                        {
                            Avm1PushStringValue s => quote(str(s.StringIndex)),
                            Avm1PushFloatValue f => f.Value.ToString(CultureInfo.InvariantCulture),
                            Avm1PushDoubleValue d => d.Value.ToString(CultureInfo.InvariantCulture),
                            Avm1PushIntegerValue i => i.Value.ToString(CultureInfo.InvariantCulture),
                            Avm1PushBooleanValue b => b.Value ? "true" : "false",
                            Avm1PushNullValue => "null",
                            Avm1PushUndefinedValue => "undefined",
                            Avm1PushRegisterValue r => registers.GetValueOrDefault(r.Register, $"r{r.Register}"),
                            _ => "?",
                        });
                    break;
                case 0x1C: { var name = pop(); stack.Add(name.Length > 2 && name[0] == '"' ? name[1..^1] : $"eval({name})"); break; }
                case 0x1D: { var value = pop(); var name = pop(); line($"{(name.Length > 2 && name[0] == '"' ? name[1..^1] : $"set({name})")} = {value};"); break; }
                case 0x4E: { var name = pop(); var obj = pop(); stack.Add(member(obj, name)); break; }
                case 0x4F: { var value = pop(); var name = pop(); var obj = pop(); line($"{member(obj, name)} = {value};"); break; }
                case 0x3D: { var name = pop(); var a = args(count(pop())); stack.Add($"{(name.Length > 2 && name[0] == '"' ? name[1..^1] : name)}({string.Join(", ", a)})"); break; }
                case 0x52: { var name = pop(); var obj = pop(); var a = args(count(pop())); stack.Add(name is "\"\"" or "undefined" ? $"{obj}({string.Join(", ", a)})" : $"{member(obj, name)}({string.Join(", ", a)})"); break; }
                case 0x40: { var name = pop(); var a = args(count(pop())); stack.Add($"new {(name.Length > 2 && name[0] == '"' ? name[1..^1] : name)}({string.Join(", ", a)})"); break; }
                case 0x42: { var a = args(count(pop())); stack.Add($"[{string.Join(", ", a)}]"); break; }
                case 0x43: { var n = count(pop()); var pairs = new List<string>(); for (var i = 0; i < n; i++) { var v = pop(); var k = pop(); pairs.Add($"{k}: {v}"); } stack.Add("{" + string.Join(", ", pairs) + "}"); break; }
                case 0x17: { var value = pop(); if (value.EndsWith(')')) line(value + ";"); break; }
                case 0x4C: { var value = pop(); stack.Add(value); stack.Add(value); break; }
                case 0x4D: { var b = pop(); var a = pop(); stack.Add(b); stack.Add(a); break; }
                case 0x87 when instruction.Operand is Avm1RegisterOperand register:
                {
                    var value = stack.Count > 0 ? stack[^1] : "?";
                    line($"r{register.Register} = {value};");
                    registers[register.Register] = $"r{register.Register}";
                    break;
                }
                case 0x0A or 0x47: stack.Add(binary("+")); break;
                case 0x0B: stack.Add(binary("-")); break;
                case 0x0C: stack.Add(binary("*")); break;
                case 0x0D: stack.Add(binary("/")); break;
                case 0x3F: stack.Add(binary("%")); break;
                case 0x0E or 0x49: stack.Add(binary("==")); break;
                case 0x66: stack.Add(binary("===")); break;
                case 0x0F or 0x48: stack.Add(binary("<")); break;
                case 0x67: stack.Add(binary(">")); break;
                case 0x10: stack.Add(binary("&&")); break;
                case 0x11: stack.Add(binary("||")); break;
                case 0x13: stack.Add(binary("eq")); break;
                case 0x21: stack.Add(binary("add")); break;
                case 0x60: stack.Add(binary("&")); break;
                case 0x61: stack.Add(binary("|")); break;
                case 0x62: stack.Add(binary("^")); break;
                case 0x63: stack.Add(binary("<<")); break;
                case 0x64: stack.Add(binary(">>")); break;
                case 0x65: stack.Add(binary(">>>")); break;
                case 0x12: stack.Add($"!{pop()}"); break;
                case 0x50: stack.Add($"({pop()} + 1)"); break;
                case 0x51: stack.Add($"({pop()} - 1)"); break;
                case 0x18: stack.Add($"int({pop()})"); break;
                case 0x4A: stack.Add($"Number({pop()})"); break;
                case 0x4B: stack.Add($"String({pop()})"); break;
                case 0x44: stack.Add($"typeof {pop()}"); break;
                case 0x14: stack.Add($"length({pop()})"); break;
                case 0x22: { var property = pop(); var target = pop(); stack.Add($"getProperty({target}, {property})"); break; }
                case 0x23: { var value = pop(); var property = pop(); var target = pop(); line($"setProperty({target}, {property}, {value});"); break; }
                case 0x26: line($"trace({pop()});"); break;
                case 0x3E: line($"return {pop()};"); break;
                case 0x3C: { var value = pop(); var name = pop(); line($"var {(name.Length > 2 && name[0] == '"' ? name[1..^1] : name)} = {value};"); break; }
                case 0x41: { var name = pop(); line($"var {(name.Length > 2 && name[0] == '"' ? name[1..^1] : name)};"); break; }
                case 0x3A: { var name = pop(); var obj = pop(); line($"delete {member(obj, name)};"); break; }
                case 0x04: line("nextFrame();"); break;
                case 0x05: line("prevFrame();"); break;
                case 0x06: line("play();"); break;
                case 0x07: line("stop();"); break;
                case 0x81 when instruction.Operand is Avm1FrameOperand frame: line($"gotoAndStop({frame.Frame});"); break;
                case 0x8C when instruction.Operand is Avm1StringIndexOperand label: line($"gotoLabel({quote(str(label.StringIndex))});"); break;
                case 0x9F when instruction.Operand is Avm1GotoFrame2Operand go: line($"{((go.Flags & 1) != 0 ? "gotoAndPlay" : "gotoAndStop")}({pop()});"); break;
                case 0x8B when instruction.Operand is Avm1StringIndexOperand target: line($"tellTarget({quote(str(target.StringIndex))}) {{"); break;
                case 0x20: line($"tellTarget({pop()}) {{"); break;
                case 0x99: line($"goto L{instruction.BranchTarget:X4};"); break;
                case 0x9D: line($"if ({pop()}) goto L{instruction.BranchTarget:X4};"); break;
                case 0x88: line("// constant pool"); break;
                case 0x8E or 0x9B when instruction.Operand is Avm1FunctionOperand function:
                {
                    var name = function.NameStringIndex == 0 ? "" : str(function.NameStringIndex);
                    var parameters = string.Join(", ", function.Parameters.Select(p => str(p.NameStringIndex)));
                    if (name.Length == 0) stack.Add($"function({parameters}) {{…}}");
                    line($"function {(name.Length > 0 ? name : "/*anonymous*/")}({parameters}) {{");
                    if (instruction.Body is not null) block(instruction.Body, strings, output, depth + 1);
                    line("}");
                    break;
                }
                case 0x94 when instruction.Body is not null:
                    line($"with ({pop()}) {{");
                    block(instruction.Body, strings, output, depth + 1);
                    line("}");
                    break;
                default:
                    line($"// {instruction.Opcode:X2} {Convert.ToHexString(instruction.OperandBytes.AsSpan())}");
                    break;
            }
        }
        foreach (var leftover in stack.Where(s => s.EndsWith(')'))) line(leftover + ";");
    }
}
