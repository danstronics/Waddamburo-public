using System.Buffers.Binary;

namespace LumenStudio.Authoring.Script;

/// <summary>
/// Compiles LumenScript (an ActionScript 2 subset: var, function, if/else, while, do, for, switch,
/// return, break/continue, the usual operators, member calls, new, array and object literals, import
/// aliases) to Lumen AVM1 bytecode. Strings become indices of the movie's string pool through
/// <c>intern</c>. Only opcodes Waddamburo's interpreter executes are emitted (no And/Or, InitObject,
/// TypeOf or SetTarget): &amp;&amp; and || short-circuit with branches, object literals are built member by member.
/// </summary>
public static class ScriptCompiler
{
    public static byte[] Compile(string source, Func<string, int> intern)
    {
        ArgumentNullException.ThrowIfNull(source);
        ArgumentNullException.ThrowIfNull(intern);
        var parser = new Parser(Lexer.Tokenize(source));
        var program = parser.ParseProgram();
        using var generator = new Generator(intern, parser.Imports, inFunction: false);
        foreach (var statement in program) generator.Statement(statement);
        generator.Op(0x00);
        return generator.Finish();
    }

    private sealed class Label { public int Position = -1; }

    private sealed class Generator(Func<string, int> intern, Dictionary<string, string[]> imports, bool inFunction) : IDisposable
    {
        private readonly MemoryStream _code = new();
        private readonly List<(int Operand, Label Target)> _fixups = [];
        private readonly Stack<(Label Break, Label? Continue)> _loops = new();
        private static int s_switchCounter;

        public void Dispose() => _code.Dispose();

        public byte[] Finish()
        {
            var bytes = _code.ToArray();
            foreach (var (operand, target) in _fixups)
            {
                if (target.Position < 0) throw new InvalidOperationException("Unplaced branch label.");
                var distance = target.Position - (operand + 2);
                if (distance is < short.MinValue or > short.MaxValue) throw new ScriptException("A branch is too long (over 32 KiB of code).", 0, 0);
                BinaryPrimitives.WriteInt16LittleEndian(bytes.AsSpan(operand), (short)distance);
            }
            return bytes;
        }

        // --- Emission primitives ---

        public void Op(byte opcode) => _code.WriteByte(opcode);

        private void op(byte opcode, ReadOnlySpan<byte> payload)
        {
            _code.WriteByte(opcode);
            Span<byte> length = stackalloc byte[2];
            BinaryPrimitives.WriteUInt16LittleEndian(length, checked((ushort)payload.Length));
            _code.Write(length);
            _code.Write(payload);
        }

        private void mark(Label label) => label.Position = (int)_code.Position;

        private void branch(byte opcode, Label target)
        {
            op(opcode, [0, 0]);
            _fixups.Add(((int)_code.Position - 2, target));
        }

        private void jump(Label target) => branch(0x99, target);
        private void jumpIfTrue(Label target) => branch(0x9D, target);

        private ushort str(string value)
        {
            var index = intern(value);
            if (index is < 0 or > ushort.MaxValue) throw new ScriptException("The string pool exceeds 65535 entries.", 0, 0);
            return (ushort)index;
        }

        private void pushString(string value)
        {
            var index = str(value);
            op(0x96, [0, (byte)index, (byte)(index >> 8)]);
        }

        private void pushNumber(double value)
        {
            if (value == Math.Floor(value) && value is >= int.MinValue and <= int.MaxValue && !(value == 0 && double.IsNegative(value)))
            {
                Span<byte> payload = stackalloc byte[5];
                payload[0] = 7;
                BinaryPrimitives.WriteInt32LittleEndian(payload[1..], (int)value);
                op(0x96, payload);
            }
            else
            {
                Span<byte> payload = stackalloc byte[9];
                payload[0] = 6;
                BinaryPrimitives.WriteDoubleLittleEndian(payload[1..], value);
                op(0x96, payload);
            }
        }

        private void pushLiteral(byte type, byte? value = null)
        {
            if (value is byte b) op(0x96, [type, b]);
            else op(0x96, [type]);
        }

        // --- Statements ---

        public void Statement(Stmt statement)
        {
            switch (statement)
            {
                case EmptyStmt: break;
                case BlockStmt block:
                    foreach (var inner in block.Body) Statement(inner);
                    break;
                case ExprStmt { Expression: var expression }:
                    effect(expression);
                    break;
                case VarStmt declaration:
                    foreach (var (name, value) in declaration.Declarations)
                    {
                        pushString(name);
                        if (inFunction)
                        {
                            if (value is null) Op(0x41); // DefineLocal2: declares without a value
                            else { Expression(value); Op(0x3C); }
                        }
                        else
                        {
                            if (value is null) { pushLiteral(3); }
                            else Expression(value);
                            Op(0x1D);
                        }
                    }
                    break;
                case FunctionStmt { Function: var function }:
                    defineFunction(function);
                    break;
                case IfStmt conditional:
                {
                    var otherwise = new Label();
                    var end = new Label();
                    Expression(conditional.Test);
                    Op(0x12);
                    jumpIfTrue(otherwise);
                    Statement(conditional.Then);
                    if (conditional.Else is not null) jump(end);
                    mark(otherwise);
                    if (conditional.Else is not null) { Statement(conditional.Else); mark(end); }
                    break;
                }
                case WhileStmt loop:
                {
                    var start = new Label();
                    var test = new Label();
                    var end = new Label();
                    if (!loop.TestAfter) jump(test);
                    mark(start);
                    _loops.Push((end, test));
                    Statement(loop.Body);
                    _loops.Pop();
                    mark(test);
                    Expression(loop.Test);
                    jumpIfTrue(start);
                    mark(end);
                    break;
                }
                case ForStmt loop:
                {
                    if (loop.Init is not null) Statement(loop.Init);
                    var start = new Label();
                    var step = new Label();
                    var test = new Label();
                    var end = new Label();
                    jump(test);
                    mark(start);
                    _loops.Push((end, step));
                    Statement(loop.Body);
                    _loops.Pop();
                    mark(step);
                    if (loop.Step is not null) effect(loop.Step);
                    mark(test);
                    if (loop.Test is null) jump(start);
                    else { Expression(loop.Test); jumpIfTrue(start); }
                    mark(end);
                    break;
                }
                case SwitchStmt choice:
                {
                    // The discriminant is evaluated once, into a hidden variable.
                    var hidden = $"$switch{Interlocked.Increment(ref s_switchCounter)}";
                    pushString(hidden);
                    Expression(choice.Discriminant);
                    Op(inFunction ? (byte)0x3C : (byte)0x1D);
                    var end = new Label();
                    var bodies = choice.Cases.Select(_ => new Label()).ToArray();
                    for (var i = 0; i < choice.Cases.Count; i++)
                    {
                        if (choice.Cases[i].Test is not { } test) continue;
                        pushString(hidden); Op(0x1C);
                        Expression(test);
                        Op(0x66);
                        jumpIfTrue(bodies[i]);
                    }
                    var fallback = choice.Cases.ToList().FindIndex(c => c.Test is null);
                    jump(fallback >= 0 ? bodies[fallback] : end);
                    _loops.Push((end, _loops.Count > 0 ? _loops.Peek().Continue : null));
                    for (var i = 0; i < choice.Cases.Count; i++)
                    {
                        mark(bodies[i]);
                        foreach (var inner in choice.Cases[i].Body) Statement(inner);
                    }
                    _loops.Pop();
                    mark(end);
                    break;
                }
                case ReturnStmt result:
                    if (!inFunction) throw new ScriptException("'return' outside a function.", result.Line, result.Column);
                    if (result.Value is null) pushLiteral(3);
                    else Expression(result.Value);
                    Op(0x3E);
                    break;
                case JumpStmt jumpStatement:
                {
                    if (_loops.Count == 0)
                        throw new ScriptException($"'{(jumpStatement.IsBreak ? "break" : "continue")}' outside a loop.", jumpStatement.Line, jumpStatement.Column);
                    var (exit, next) = _loops.Peek();
                    if (jumpStatement.IsBreak) jump(exit);
                    else jump(next ?? throw new ScriptException("'continue' outside a loop.", jumpStatement.Line, jumpStatement.Column));
                    break;
                }
                default:
                    throw new ScriptException($"Unsupported statement {statement.GetType().Name}.", statement.Line, statement.Column);
            }
        }

        /// <summary>An expression evaluated for its effect (no value left on the stack).</summary>
        private void effect(Expr expression)
        {
            switch (expression)
            {
                case AssignExpr assignment:
                    assign(assignment, keepValue: false);
                    return;
                case UpdateExpr update:
                    this.update(update, keepValue: false);
                    return;
                case CallExpr call when timelineCall(call):
                    return;
            }
            Expression(expression);
            Op(0x17);
        }

        // stop(), play(), gotoAndPlay/Stop(literal) and trace(x) on the current timeline have their own opcodes.
        private bool timelineCall(CallExpr call)
        {
            if (call.Callee is not NameExpr { Name: var name }) return false;
            switch (name)
            {
                case "stop" when call.Arguments.Count == 0: Op(0x07); return true;
                case "play" when call.Arguments.Count == 0: Op(0x06); return true;
                case "trace" when call.Arguments.Count == 1: Expression(call.Arguments[0]); Op(0x26); return true;
                case "gotoAndPlay" or "gotoAndStop" when call.Arguments.Count == 1:
                    switch (call.Arguments[0])
                    {
                        case StringExpr label:
                        {
                            var index = str(label.Value);
                            op(0x8C, [(byte)index, (byte)(index >> 8)]);
                            break;
                        }
                        case NumberExpr { Value: var frame } when frame >= 1 && frame <= ushort.MaxValue && frame == Math.Floor(frame):
                        {
                            var zeroBased = (ushort)(frame - 1);
                            op(0x81, [(byte)zeroBased, (byte)(zeroBased >> 8)]);
                            break;
                        }
                        default:
                            return false; // a computed target: this.gotoAndX(value)
                    }
                    Op(name == "gotoAndPlay" ? (byte)0x06 : (byte)0x07);
                    return true;
                default: return false;
            }
        }

        // --- Expressions (each leaves exactly one value) ---

        public void Expression(Expr expression)
        {
            switch (expression)
            {
                case NumberExpr number: pushNumber(number.Value); break;
                case StringExpr text: pushString(text.Value); break;
                case LiteralExpr { Kind: "true" }: pushLiteral(5, 1); break;
                case LiteralExpr { Kind: "false" }: pushLiteral(5, 0); break;
                case LiteralExpr { Kind: "null" }: pushLiteral(2); break;
                case LiteralExpr: pushLiteral(3); break;
                case NameExpr name: variable(name.Name); break;
                case MemberExpr member:
                    Expression(member.Target);
                    pushString(member.Name);
                    Op(0x4E);
                    break;
                case IndexExpr index:
                    Expression(index.Target);
                    Expression(index.Index);
                    Op(0x4E);
                    break;
                case CallExpr call: this.call(call); break;
                case NewExpr creation:
                    arguments(creation.Arguments);
                    switch (resolve(creation.Constructor))
                    {
                        case NameExpr { Name: var constructor }:
                            pushString(constructor);
                            Op(0x40);
                            break;
                        case MemberExpr member:
                            Expression(member.Target);
                            pushString(member.Name);
                            Op(0x53);
                            break;
                        default:
                            throw new ScriptException("'new' needs a constructor name.", creation.Line, creation.Column);
                    }
                    break;
                case UnaryExpr unary:
                    switch (unary.Operator)
                    {
                        case "!": Expression(unary.Operand); Op(0x12); break;
                        case "-": pushNumber(0); Expression(unary.Operand); Op(0x0B); break;
                        case "+": Expression(unary.Operand); Op(0x4A); break;
                        case "~": Expression(unary.Operand); pushNumber(-1); Op(0x62); break;
                    }
                    break;
                case UpdateExpr update: this.update(update, keepValue: true); break;
                case BinaryExpr binary: this.binary(binary); break;
                case ConditionalExpr conditional:
                {
                    var otherwise = new Label();
                    var end = new Label();
                    Expression(conditional.Test);
                    Op(0x12);
                    jumpIfTrue(otherwise);
                    Expression(conditional.Then);
                    jump(end);
                    mark(otherwise);
                    Expression(conditional.Else);
                    mark(end);
                    break;
                }
                case AssignExpr assignment: assign(assignment, keepValue: true); break;
                case ArrayExpr array:
                    for (var i = array.Items.Count - 1; i >= 0; i--) Expression(array.Items[i]);
                    pushNumber(array.Items.Count);
                    Op(0x42);
                    break;
                case ObjectExpr literal:
                    pushNumber(0);
                    pushString("Object");
                    Op(0x40);
                    foreach (var (key, value) in literal.Properties)
                    {
                        Op(0x4C);
                        pushString(key);
                        Expression(value);
                        Op(0x4F);
                    }
                    break;
                case FunctionExpr function:
                    if (function.Name is not null) throw new ScriptException("A function expression cannot be named here.", function.Line, function.Column);
                    defineFunction(function);
                    break;
                default:
                    throw new ScriptException($"Unsupported expression {expression.GetType().Name}.", expression.Line, expression.Column);
            }
        }

        private Expr resolve(Expr expression)
        {
            if (expression is NameExpr { Name: var name } && imports.TryGetValue(name, out var path))
            {
                Expr resolved = new NameExpr(path[0], expression.Line, expression.Column);
                foreach (var part in path.Skip(1)) resolved = new MemberExpr(resolved, part, expression.Line, expression.Column);
                return resolved;
            }
            return expression;
        }

        private void variable(string name)
        {
            if (imports.ContainsKey(name)) { Expression(resolve(new NameExpr(name, 0, 0))); return; }
            pushString(name);
            Op(0x1C);
        }

        private void arguments(IReadOnlyList<Expr> list)
        {
            for (var i = list.Count - 1; i >= 0; i--) Expression(list[i]);
            pushNumber(list.Count);
        }

        private void call(CallExpr call)
        {
            if (timelineCall(call)) { pushLiteral(3); return; }
            var callee = resolve(call.Callee);
            switch (callee)
            {
                case NameExpr { Name: "gotoAndPlay" or "gotoAndStop" } name:
                    // Computed frame or label: the current clip's method.
                    arguments(call.Arguments);
                    variable("this");
                    pushString(name.Name);
                    Op(0x52);
                    break;
                case NameExpr name:
                    arguments(call.Arguments);
                    pushString(name.Name);
                    Op(0x3D);
                    break;
                case MemberExpr member:
                    arguments(call.Arguments);
                    Expression(member.Target);
                    pushString(member.Name);
                    Op(0x52);
                    break;
                case IndexExpr index:
                    arguments(call.Arguments);
                    Expression(index.Target);
                    Expression(index.Index);
                    Op(0x52);
                    break;
                default:
                    throw new ScriptException("Only named functions and methods can be called.", call.Line, call.Column);
            }
        }

        private static readonly Dictionary<string, byte[]> Operators = new(StringComparer.Ordinal)
        {
            ["+"] = [0x47], ["-"] = [0x0B], ["*"] = [0x0C], ["/"] = [0x0D], ["%"] = [0x3F],
            ["<"] = [0x48], [">"] = [0x67], ["<="] = [0x67, 0x12], [">="] = [0x48, 0x12],
            ["=="] = [0x49], ["!="] = [0x49, 0x12], ["==="] = [0x66], ["!=="] = [0x66, 0x12],
            ["&"] = [0x60], ["|"] = [0x61], ["^"] = [0x62], ["<<"] = [0x63], [">>"] = [0x64], [">>>"] = [0x65],
        };

        private void binary(BinaryExpr binary)
        {
            if (binary.Operator is "&&" or "||")
            {
                var end = new Label();
                Expression(binary.Left);
                Op(0x4C);
                if (binary.Operator == "&&") Op(0x12);
                jumpIfTrue(end);
                Op(0x17);
                Expression(binary.Right);
                mark(end);
                return;
            }
            Expression(binary.Left);
            Expression(binary.Right);
            foreach (var opcode in Operators[binary.Operator]) Op(opcode);
        }

        private void applyCompound(string assignmentOperator)
        {
            foreach (var opcode in Operators[assignmentOperator[..^1]]) Op(opcode);
        }

        private void assign(AssignExpr assignment, bool keepValue)
        {
            var target = resolve(assignment.Target);
            var compound = assignment.Operator != "=";
            switch (target)
            {
                case NameExpr name:
                    pushString(name.Name);
                    if (compound) { variable(name.Name); Expression(assignment.Value); applyCompound(assignment.Operator); }
                    else Expression(assignment.Value);
                    Op(0x1D);
                    if (keepValue) variable(name.Name);
                    break;
                case MemberExpr or IndexExpr:
                {
                    if (keepValue) throw new ScriptException("Assigning a member inside an expression is not supported; use a statement.", assignment.Line, assignment.Column);
                    var (objectExpr, key) = target is MemberExpr member ? (member.Target, (Expr)new StringExpr(member.Name, 0, 0))
                        : (((IndexExpr)target).Target, ((IndexExpr)target).Index);
                    Expression(objectExpr);
                    if (compound)
                    {
                        // [o] → [o, o] → [o, o[k]] → [o, k, o[k]] → [o, k, o[k] op v]
                        Op(0x4C);
                        Expression(key);
                        Op(0x4E);
                        Expression(key);
                        Op(0x4D);
                        Expression(assignment.Value);
                        applyCompound(assignment.Operator);
                    }
                    else
                    {
                        Expression(key);
                        Expression(assignment.Value);
                    }
                    Op(0x4F);
                    break;
                }
                default:
                    throw new ScriptException("Invalid assignment target.", assignment.Line, assignment.Column);
            }
        }

        private void update(UpdateExpr update, bool keepValue)
        {
            var opcode = update.Operator == "++" ? (byte)0x50 : (byte)0x51;
            switch (resolve(update.Target))
            {
                case NameExpr name:
                    if (keepValue && !update.Prefix) variable(name.Name);
                    pushString(name.Name);
                    variable(name.Name);
                    Op(opcode);
                    Op(0x1D);
                    if (keepValue && update.Prefix) variable(name.Name);
                    break;
                case MemberExpr member when !keepValue:
                    Expression(member.Target);
                    Op(0x4C);
                    pushString(member.Name);
                    Op(0x4E);
                    pushString(member.Name);
                    Op(0x4D);
                    Op(opcode);
                    Op(0x4F);
                    break;
                default:
                    throw new ScriptException("++/-- inside an expression works on variables only.", update.Line, update.Column);
            }
        }

        private void defineFunction(FunctionExpr function)
        {
            using var body = new Generator(intern, imports, inFunction: true);
            foreach (var statement in function.Body) body.Statement(statement);
            body.Op(0x00); // Lumen ends function bodies with End too (Waddamburo requires it)
            var code = body.Finish();
            if (code.Length > ushort.MaxValue) throw new ScriptException("A function body exceeds 64 KiB.", function.Line, function.Column);
            using var header = new MemoryStream();
            void u16(int value) { header.WriteByte((byte)value); header.WriteByte((byte)(value >> 8)); }
            u16(function.Name is null ? 0 : str(function.Name));
            u16(function.Parameters.Count);
            foreach (var parameter in function.Parameters) u16(str(parameter));
            u16(code.Length);
            op(0x9B, header.ToArray());
            _code.Write(code);
        }
    }
}
