using System.Collections.Generic;
using System.Runtime.CompilerServices;
using Bitsmith.Llvm.IR;

namespace Bitsmith.Llvm.Writer;

internal sealed class ReferenceComparer<T> : IEqualityComparer<T> where T : class
{
    public static readonly ReferenceComparer<T> Instance = new();
    public bool Equals(T? x, T? y) => ReferenceEquals(x, y);
    public int GetHashCode(T obj) => RuntimeHelpers.GetHashCode(obj);
}

/// <summary>
/// Assigns sequential value IDs across module-level values and function-local values.
/// Module-level order matches LLVM's ValueEnumerator: globals, then functions, then
/// module-level constants (initializers). <see cref="IncorporateFunction"/> appends
/// arguments and instruction values; <see cref="DeincorporateFunction"/> trims them.
/// </summary>
internal sealed class ValueEnumerator
{
    private readonly List<Value> _values = new();
    private readonly Dictionary<Value, int> _ids = new(ReferenceComparer<Value>.Instance);
    private readonly List<Constant> _moduleConstants = new();
    private readonly int _moduleValueCount;

    public ValueEnumerator(Module module)
    {
        foreach (var g in module.Globals)
            Add(g);

        foreach (var f in module.Functions)
            Add(f);

        foreach (var a in module.Aliases)
            Add(a);

        foreach (var ifn in module.IFuncs)
            Add(ifn);

        // Initializers come after named module-level values, matching LLVM's order.
        foreach (var g in module.Globals)
            if (g.Initializer is not null)
                AddConstant(g.Initializer);

        // Function prefix/prologue data are also module-level constants.
        foreach (var f in module.Functions)
        {
            if (f.PrefixData is not null) AddConstant(f.PrefixData);
            if (f.PrologueData is not null) AddConstant(f.PrologueData);
        }

        _moduleValueCount = _values.Count;
    }

    public int ModuleValueCount => _moduleValueCount;
    public IReadOnlyList<Constant> ModuleConstants => _moduleConstants;

    private readonly List<Constant> _functionConstants = new();
    /// <summary>Constants discovered as instruction operands of the currently-incorporated
    /// function, to be emitted in the function-local CONSTANTS_BLOCK.</summary>
    public IReadOnlyList<Constant> FunctionConstants => _functionConstants;

    public int GetValueId(Value v) => _ids[v];

    public void IncorporateFunction(Function fn)
    {
        foreach (var arg in fn.Parameters)
            Add(arg);

        // Pass 1: discover constants used as operands. They live in the function's
        // local CONSTANTS_BLOCK and get IDs sandwiched between args and instructions.
        _functionConstants.Clear();
        foreach (var bb in fn.BasicBlocks)
            foreach (var inst in bb.Instructions)
                foreach (var op in inst.Operands)
                    if (op is Constant c) AddFunctionConstant(c);

        // Pass 2: assign IDs to non-void instructions.
        foreach (var bb in fn.BasicBlocks)
            foreach (var inst in bb.Instructions)
                if (inst.Type is not VoidType)
                    Add(inst);
    }

    private void AddFunctionConstant(Constant c)
    {
        if (_ids.ContainsKey(c)) return;
        if (c is AggregateConstant agg)
            foreach (var e in agg.Elements)
                AddFunctionConstant(e);
        Add(c);
        _functionConstants.Add(c);
    }

    public void DeincorporateFunction()
    {
        for (int i = _values.Count - 1; i >= _moduleValueCount; i--)
        {
            _ids.Remove(_values[i]);
            _values.RemoveAt(i);
        }
        _functionConstants.Clear();
    }

    private void Add(Value v)
    {
        _ids[v] = _values.Count;
        _values.Add(v);
    }

    private void AddConstant(Constant c)
    {
        if (_ids.ContainsKey(c)) return;
        // Operand-side constants must precede their parent in the value table
        // so the reader resolves them by ID before applying the parent record.
        switch (c)
        {
            case AggregateConstant agg:
                foreach (var element in agg.Elements) AddConstant(element);
                break;
            case ConstantCast cc: AddConstant(cc.Operand); break;
            case ConstantGep cg:
                AddConstant(cg.Pointer);
                foreach (var i in cg.Indices) AddConstant(i);
                break;
            case ConstantCmp ccmp:
                AddConstant(ccmp.Left); AddConstant(ccmp.Right);
                break;
        }
        Add(c);
        _moduleConstants.Add(c);
    }
}
