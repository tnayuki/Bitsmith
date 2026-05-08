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

        // Constants referenced from MdValue (metadata-as-value) entries in any
        // reachable metadata graph must also be enumerated so they have value IDs
        // when the METADATA_BLOCK is written.
        var metaSeen = new HashSet<Metadata>(ReferenceComparer<Metadata>.Instance);
        foreach (var nm in module.NamedMetadata)
            foreach (var op in nm.Operands)
                CollectMdValueConstants(op, metaSeen);
        foreach (var f in module.Functions)
        {
            if (f.Subprogram is not null) CollectMdValueConstants(f.Subprogram, metaSeen);
            foreach (var bb in f.BasicBlocks)
                foreach (var inst in bb.Instructions)
                {
                    if (inst.DebugLocation is not null)
                        CollectMdValueConstants(inst.DebugLocation, metaSeen);
                    // Non-dbg attachments (!invariant.load, !range, ...).
                    // Their MdValue-wrapped IntegerConstants must show up
                    // in the module-level value table because the
                    // metadata block writer asks ValueEnumerator for
                    // their value IDs.
                    if (inst.Attachments is { } atts)
                        foreach (var (_, md) in atts)
                            CollectMdValueConstants(md, metaSeen);
                    // MetadataAsValue arguments (dbg.declare/value/label) reach metadata
                    // through the wrapped node; recurse so any embedded constants land
                    // in the module-level value table before the metadata block needs them.
                    foreach (var op in inst.Operands)
                        if (op is MetadataAsValue mav)
                            CollectMdValueConstants(mav.Metadata, metaSeen);
                }
        }

        _moduleValueCount = _values.Count;
    }

    private void CollectMdValueConstants(Metadata? md, HashSet<Metadata> visited)
    {
        if (md is null || !visited.Add(md)) return;
        switch (md)
        {
            case MdValue v:
                if (v.Value is Constant c) AddConstant(c);
                return;
            case MdTuple t:
                foreach (var op in t.Operands) CollectMdValueConstants(op, visited);
                return;
            case DiCompileUnit cu:
                CollectMdValueConstants(cu.File, visited);
                CollectMdValueConstants(cu.EnumTypes, visited);
                CollectMdValueConstants(cu.RetainedTypes, visited);
                CollectMdValueConstants(cu.Globals, visited);
                CollectMdValueConstants(cu.Imports, visited);
                CollectMdValueConstants(cu.Macros, visited);
                return;
            case DiSubroutineType st: CollectMdValueConstants(st.Types, visited); return;
            case DiSubprogram sp:
                CollectMdValueConstants(sp.Scope, visited);
                CollectMdValueConstants(sp.File, visited);
                CollectMdValueConstants(sp.Type, visited);
                CollectMdValueConstants(sp.Unit, visited);
                CollectMdValueConstants(sp.RetainedNodes, visited);
                CollectMdValueConstants(sp.Declaration, visited);
                CollectMdValueConstants(sp.ThrownTypes, visited);
                CollectMdValueConstants(sp.Annotations, visited);
                CollectMdValueConstants(sp.VirtualIndex, visited);
                return;
            case DiLocation loc:
                CollectMdValueConstants(loc.Scope, visited);
                CollectMdValueConstants(loc.InlinedAt, visited);
                return;
            case DiDerivedType dt:
                CollectMdValueConstants(dt.File, visited); CollectMdValueConstants(dt.Scope, visited);
                CollectMdValueConstants(dt.BaseType, visited); CollectMdValueConstants(dt.ExtraData, visited);
                return;
            case DiCompositeType ct:
                CollectMdValueConstants(ct.File, visited); CollectMdValueConstants(ct.Scope, visited);
                CollectMdValueConstants(ct.BaseType, visited); CollectMdValueConstants(ct.Elements, visited);
                CollectMdValueConstants(ct.VTableHolder, visited); CollectMdValueConstants(ct.TemplateParams, visited);
                return;
            case DiLexicalBlock lb:
                CollectMdValueConstants(lb.Scope, visited); CollectMdValueConstants(lb.File, visited);
                return;
            case DiLocalVariable lv:
                CollectMdValueConstants(lv.Scope, visited); CollectMdValueConstants(lv.File, visited);
                CollectMdValueConstants(lv.Type, visited);
                return;
            case DiGenericSubrange gsr:
                CollectMdValueConstants(gsr.Count, visited);
                CollectMdValueConstants(gsr.LowerBound, visited);
                CollectMdValueConstants(gsr.UpperBound, visited);
                CollectMdValueConstants(gsr.Stride, visited);
                return;
            case DiMacroFile mf:
                CollectMdValueConstants(mf.File, visited); CollectMdValueConstants(mf.Elements, visited);
                return;
            case DiModule mod:
                CollectMdValueConstants(mod.Scope, visited); CollectMdValueConstants(mod.File, visited);
                return;
            case DiCommonBlock cb:
                CollectMdValueConstants(cb.Scope, visited); CollectMdValueConstants(cb.Decl, visited);
                CollectMdValueConstants(cb.File, visited);
                return;
            case DiObjCProperty op:
                CollectMdValueConstants(op.File, visited); CollectMdValueConstants(op.Type, visited);
                return;
            case DiTemplateValueParameter tvp:
                CollectMdValueConstants(tvp.Type, visited); CollectMdValueConstants(tvp.Value, visited);
                return;
            case DiTemplateTypeParameter ttp:
                CollectMdValueConstants(ttp.Type, visited);
                return;
            case DiNamespace ns:
                CollectMdValueConstants(ns.Scope, visited);
                return;
            case DiImportedEntity ie:
                CollectMdValueConstants(ie.Scope, visited); CollectMdValueConstants(ie.Entity, visited);
                CollectMdValueConstants(ie.File, visited);
                return;
            case DiLexicalBlockFile lbf:
                CollectMdValueConstants(lbf.Scope, visited); CollectMdValueConstants(lbf.File, visited);
                return;
            case DiLabel l:
                CollectMdValueConstants(l.Scope, visited); CollectMdValueConstants(l.File, visited);
                return;
            case DiGlobalVariable gv:
                CollectMdValueConstants(gv.Scope, visited); CollectMdValueConstants(gv.File, visited);
                CollectMdValueConstants(gv.Type, visited);
                CollectMdValueConstants(gv.StaticDataMember, visited);
                CollectMdValueConstants(gv.TemplateParams, visited);
                return;
            case DiGlobalVariableExpression gve:
                CollectMdValueConstants(gve.Variable, visited);
                CollectMdValueConstants(gve.Expression, visited);
                return;
        }
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
