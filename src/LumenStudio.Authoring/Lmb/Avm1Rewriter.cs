using System.Buffers.Binary;
using Waddamburo.Formats.Lmb;

namespace LumenStudio.Authoring.Lmb;

/// <summary>
/// Re-encodes Lumen AVM1 bytecode with its string-pool indices remapped (an action imported into
/// another movie's pool). Operands that grow (an 8-bit string push past index 255) are widened, and
/// branches and function bodies are relocated.
/// </summary>
public static class Avm1Rewriter
{
    public static byte[] RemapStrings(Avm1CodeBlock code, Func<int, int> map)
    {
        ArgumentNullException.ThrowIfNull(code);
        ArgumentNullException.ThrowIfNull(map);
        using var output = new MemoryStream();
        var positions = new Dictionary<int, int>();
        var branches = new List<(int Operand, int Target)>();
        emit(code, output, map, positions, branches);
        var bytes = output.ToArray();
        foreach (var (operand, target) in branches)
        {
            if (!positions.TryGetValue(target, out var to)) throw new InvalidDataException($"Branch target {target} is not an instruction.");
            var distance = to - (operand + 2);
            if (distance is < short.MinValue or > short.MaxValue) throw new InvalidDataException("A relocated branch exceeds 16 bits.");
            BinaryPrimitives.WriteInt16LittleEndian(bytes.AsSpan(operand), (short)distance);
        }
        return bytes;
    }

    private static void emit(Avm1CodeBlock block, MemoryStream output, Func<int, int> map,
        Dictionary<int, int> positions, List<(int Operand, int Target)> branches)
    {
        foreach (var instruction in block.Instructions)
        {
            positions[instruction.Offset] = (int)output.Position;
            output.WriteByte(instruction.Opcode);
            if (instruction.Opcode < 0x80) continue;
            switch (instruction.Operand)
            {
                case Avm1BranchOperand:
                    write16(output, 2);
                    branches.Add(((int)output.Position, instruction.BranchTarget!.Value));
                    write16(output, 0);
                    break;
                case Avm1PushOperand push:
                    writePayload(output, pushPayload(push, map));
                    break;
                case Avm1StringIndexOperand index:
                    writePayload(output, u16(map(index.StringIndex)));
                    break;
                case Avm1FunctionOperand or Avm1WithOperand:
                {
                    var header = instruction.Operand is Avm1FunctionOperand function
                        ? functionHeader(instruction.Opcode, function, map) : new byte[2];
                    writePayload(output, header);
                    var lengthAt = (int)output.Position - 2;
                    var bodyStart = (int)output.Position;
                    if (instruction.Body is { } body) emit(body, output, map, positions, branches);
                    var bodyLength = (int)output.Position - bodyStart;
                    if (bodyLength > ushort.MaxValue) throw new InvalidDataException("A function body exceeds 64 KiB.");
                    var end = output.Position;
                    output.Position = lengthAt;
                    write16(output, (ushort)bodyLength);
                    output.Position = end;
                    break;
                }
                default:
                    if (instruction.Body is not null)
                        throw new NotSupportedException($"Opcode 0x{instruction.Opcode:X2} with a body cannot be relocated.");
                    writePayload(output, instruction.OperandBytes.AsSpan());
                    break;
            }
        }
        positions[block.Offset + block.Length] = (int)output.Position;
    }

    private static byte[] pushPayload(Avm1PushOperand push, Func<int, int> map)
    {
        using var payload = new MemoryStream();
        Span<byte> buffer = stackalloc byte[8];
        foreach (var value in push.Values)
        {
            switch (value)
            {
                case Avm1PushStringValue text:
                {
                    var index = map(text.StringIndex);
                    if (index is < 0 or > ushort.MaxValue) throw new InvalidDataException("A string index exceeds 16 bits.");
                    var type = text.Type == 8 && index > byte.MaxValue ? (byte)9 : text.Type;
                    payload.WriteByte(type);
                    if (type == 8) payload.WriteByte((byte)index);
                    else payload.Write(u16(index));
                    break;
                }
                case Avm1PushFloatValue number:
                    payload.WriteByte(1);
                    BinaryPrimitives.WriteSingleLittleEndian(buffer, number.Value); payload.Write(buffer[..4]);
                    break;
                case Avm1PushNullValue: payload.WriteByte(2); break;
                case Avm1PushUndefinedValue: payload.WriteByte(3); break;
                case Avm1PushRegisterValue register: payload.WriteByte(4); payload.WriteByte(register.Register); break;
                case Avm1PushBooleanValue boolean: payload.WriteByte(5); payload.WriteByte(boolean.Value ? (byte)1 : (byte)0); break;
                case Avm1PushDoubleValue number:
                    payload.WriteByte(6);
                    BinaryPrimitives.WriteDoubleLittleEndian(buffer, number.Value); payload.Write(buffer);
                    break;
                case Avm1PushIntegerValue number:
                    payload.WriteByte(7);
                    BinaryPrimitives.WriteInt32LittleEndian(buffer, number.Value); payload.Write(buffer[..4]);
                    break;
                default: throw new NotSupportedException($"Push value type {value.Type}.");
            }
        }
        return payload.ToArray();
    }

    private static byte[] functionHeader(byte opcode, Avm1FunctionOperand function, Func<int, int> map)
    {
        using var header = new MemoryStream();
        header.Write(u16(map(function.NameStringIndex)));
        header.Write(u16(function.Parameters.Length));
        if (opcode == 0x8E)
        {
            header.WriteByte(function.RegisterCount);
            header.Write(u16(function.Flags));
            foreach (var parameter in function.Parameters)
            {
                header.WriteByte(parameter.Register ?? 0);
                header.Write(u16(map(parameter.NameStringIndex)));
            }
        }
        else
        {
            foreach (var parameter in function.Parameters) header.Write(u16(map(parameter.NameStringIndex)));
        }
        header.Write(u16(0)); // body length, patched after the body is written
        return header.ToArray();
    }

    private static void writePayload(MemoryStream output, ReadOnlySpan<byte> payload)
    {
        if (payload.Length > ushort.MaxValue) throw new InvalidDataException("An action operand exceeds 64 KiB.");
        write16(output, (ushort)payload.Length);
        output.Write(payload);
    }

    private static byte[] u16(int value)
    {
        if (value is < 0 or > ushort.MaxValue) throw new InvalidDataException("A 16-bit operand is out of range.");
        var bytes = new byte[2];
        BinaryPrimitives.WriteUInt16LittleEndian(bytes, (ushort)value);
        return bytes;
    }

    private static void write16(MemoryStream output, ushort value)
    {
        Span<byte> bytes = stackalloc byte[2];
        BinaryPrimitives.WriteUInt16LittleEndian(bytes, value);
        output.Write(bytes);
    }
}
