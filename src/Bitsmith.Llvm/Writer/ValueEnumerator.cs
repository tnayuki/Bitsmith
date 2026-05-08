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
/// Module-level values come first; <see cref="IncorporateFunction"/> appends arguments
/// and instruction values, and <see cref="DeincorporateFunction"/> trims them.
/// </summary>
internal sealed class ValueEnumerator
{
    private readonly List<Value> _values = new();
    private readonly Dictionary<Value, int> _ids = new(ReferenceComparer<Value>.Instance);
    private int _moduleValueCount;

    public ValueEnumerator(Module module)
    {
        // Module-level values: globals first (none yet), then functions.
        foreach (var f in module.Functions)
            Add(f);

        // Module-level constants would be enumerated here when we add them.

        _moduleValueCount = _values.Count;
    }

    public int ModuleValueCount => _moduleValueCount;

    public int GetValueId(Value v) => _ids[v];

    public void IncorporateFunction(Function fn)
    {
        foreach (var arg in fn.Parameters)
            Add(arg);

        // Function-local constants will be enumerated here when added.

        foreach (var bb in fn.BasicBlocks)
            foreach (var inst in bb.Instructions)
                if (inst.Type is not VoidType)
                    Add(inst);
    }

    public void DeincorporateFunction()
    {
        for (int i = _values.Count - 1; i >= _moduleValueCount; i--)
        {
            _ids.Remove(_values[i]);
            _values.RemoveAt(i);
        }
    }

    private void Add(Value v)
    {
        _ids[v] = _values.Count;
        _values.Add(v);
    }
}
