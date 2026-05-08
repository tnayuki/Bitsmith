using System.Collections.Generic;
using Bitsmith.Llvm.Bitstream;
using Bitsmith.Llvm.Codes;
using Bitsmith.Llvm.IR;

namespace Bitsmith.Llvm.Writer;

/// <summary>
/// Builds the module's PARAMATTR_GROUP_BLOCK and PARAMATTR_BLOCK.
///
/// Each function aggregates a list of (paramidx, AttributeSet) pairs:
///   paramidx ~0u = function attributes, 0 = return value, i+1 = parameter i.
/// We assign each unique (paramidx, AttributeSet) a 1-based group ID, and each
/// unique list-of-group-ids a 1-based attribute-list index. Functions/calls
/// reference the list index via the <c>paramattr</c> field (0 = none).
/// </summary>
internal sealed class AttributeTableWriter
{
    private const int ParamAttrAbbrevWidth = 3;

    private const uint FnAttrIndex = unchecked((uint)~0);
    private const uint RetAttrIndex = 0;

    private readonly List<(uint Idx, AttributeSet Set)> _groupKeys = new();
    private readonly List<List<uint>> _attrLists = new();

    /// <summary>
    /// Records all attribute sets attached to <paramref name="fn"/> and returns the
    /// 1-based attribute-list index to embed in the function's MODULE record (0 = none).
    /// </summary>
    public uint Record(Function fn)
    {
        var groupIds = new List<uint>();
        if (!fn.FunctionAttributes.IsEmpty)
            groupIds.Add(GetOrAddGroup(FnAttrIndex, fn.FunctionAttributes));
        if (!fn.ReturnAttributes.IsEmpty)
            groupIds.Add(GetOrAddGroup(RetAttrIndex, fn.ReturnAttributes));
        for (int i = 0; i < fn.Parameters.Count; i++)
        {
            var s = fn.GetParameterAttributes(i);
            if (!s.IsEmpty) groupIds.Add(GetOrAddGroup((uint)(i + 1), s));
        }
        if (groupIds.Count == 0) return 0;

        for (int i = 0; i < _attrLists.Count; i++)
            if (ListsEqual(_attrLists[i], groupIds)) return (uint)(i + 1);
        _attrLists.Add(groupIds);
        return (uint)_attrLists.Count;
    }

    public bool HasAny => _groupKeys.Count > 0 || _attrLists.Count > 0;

    public void Write(BitstreamWriter w)
    {
        WriteGroupBlock(w);
        WriteListBlock(w);
    }

    private uint GetOrAddGroup(uint paramIdx, AttributeSet set)
    {
        for (int i = 0; i < _groupKeys.Count; i++)
        {
            var k = _groupKeys[i];
            if (k.Idx == paramIdx && k.Set.StructurallyEquals(set))
                return (uint)(i + 1);
        }
        _groupKeys.Add((paramIdx, set));
        return (uint)_groupKeys.Count;
    }

    private void WriteGroupBlock(BitstreamWriter w)
    {
        w.EnterSubBlock(BlockIds.ParamAttrGroup, ParamAttrAbbrevWidth);
        for (int i = 0; i < _groupKeys.Count; i++)
        {
            var (idx, set) = _groupKeys[i];
            var ops = new List<ulong> { (ulong)(i + 1), idx };
            foreach (var a in set.Attributes)
            {
                switch (a.Shape)
                {
                    case IR.Attribute.Form.Enum:
                        ops.Add(ParamAttrCodes.AttrKindTagEnum);
                        ops.Add(a.Kind);
                        break;
                    case IR.Attribute.Form.Int:
                        ops.Add(ParamAttrCodes.AttrKindTagInt);
                        ops.Add(a.Kind);
                        ops.Add(a.IntValue);
                        break;
                    case IR.Attribute.Form.Type:
                        ops.Add(ParamAttrCodes.AttrKindTagType);
                        ops.Add(a.Kind);
                        ops.Add((ulong)a.TypeValue!.Id);
                        break;
                    case IR.Attribute.Form.String:
                        ops.Add(ParamAttrCodes.AttrKindTagString);
                        AppendNullTerminated(ops, a.StringKey!);
                        break;
                    case IR.Attribute.Form.StringValue:
                        ops.Add(ParamAttrCodes.AttrKindTagStringValue);
                        AppendNullTerminated(ops, a.StringKey!);
                        AppendNullTerminated(ops, a.StringValue!);
                        break;
                }
            }
            w.WriteUnabbrevRecord(ParamAttrCodes.GrpEntry, ops.ToArray());
        }
        w.ExitBlock();
    }

    private void WriteListBlock(BitstreamWriter w)
    {
        w.EnterSubBlock(BlockIds.ParamAttr, ParamAttrAbbrevWidth);
        foreach (var list in _attrLists)
        {
            var ops = new ulong[list.Count];
            for (int i = 0; i < list.Count; i++) ops[i] = list[i];
            w.WriteUnabbrevRecord(ParamAttrCodes.Entry, ops);
        }
        w.ExitBlock();
    }

    private static bool ListsEqual(List<uint> a, List<uint> b)
    {
        if (a.Count != b.Count) return false;
        for (int i = 0; i < a.Count; i++) if (a[i] != b[i]) return false;
        return true;
    }

    private static void AppendNullTerminated(List<ulong> ops, string s)
    {
        var bytes = System.Text.Encoding.UTF8.GetBytes(s);
        foreach (var b in bytes) ops.Add(b);
        ops.Add(0);
    }
}
