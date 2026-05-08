using System;
using System.Collections.Generic;
using System.Text;
using Bitsmith.Llvm.Bitstream;
using Bitsmith.Llvm.Codes;
using Bitsmith.Llvm.IR;

namespace Bitsmith.Llvm.Writer;

internal static class TypeTableWriter
{
    private const int TypeAbbrevWidth = 4;

    public static void Write(BitstreamWriter w, TypeContext types)
    {
        // The bitcode reader assigns sequential type ids in emission order;
        // every type record may only reference operand types that already
        // exist (forward references are tolerated only for *named* structs,
        // because the reader can pre-allocate an opaque shell by name).
        // Bitsmith's TypeContext registers types in *call* order, so a
        // depth-first construction pattern such as
        //   1. CreateOpaqueNamedStruct  -> id N
        //   2. recursively map fields   -> ids N+1, N+2, ...
        //   3. SetBody on the shell at N
        // leaves the id-N record's body referencing N+1+ — fine for the
        // named struct itself but fatal for any *literal* struct (e.g.
        // {ptr, i32} landing-pad type) that might also reference an
        // out-of-order operand. Topologically sort here so every operand
        // type precedes its referencing type, then renumber so downstream
        // writers (ConstantWriter / FunctionWriter / ValueEnumerator) see
        // the same ids the reader will assign.
        var sorted = TopologicallySortAndRenumber(types.AllTypes);

        w.EnterSubBlock(BlockIds.TypeNew, TypeAbbrevWidth);
        w.WriteUnabbrevRecord(TypeCodes.NumEntry, (ulong)sorted.Count);

        // Block-local abbrevs for the most frequent records. Defined once at the
        // top of the type block; subsequent entries for the same code use the
        // shorter abbrev encoding.
        //
        //   integerAbbrev:    [Literal(Integer), Vbr(8) bitwidth]
        //   pointerAbbrev:    [Literal(OpaquePointer), Vbr(2) addrspace]
        //   arrayAbbrev:      [Literal(Array), Vbr(8) numElts, Vbr(6) eltType]
        //   vectorAbbrev:     [Literal(Vector), Vbr(8) numElts, Vbr(6) eltType]
        //   structNameAbbrev: [Literal(StructName), Array, Char6 byte]
        var integerAbbrev = w.DefineAbbrev(AbbrevOp.Literal(TypeCodes.Integer), AbbrevOp.Vbr(8));
        var pointerAbbrev = w.DefineAbbrev(AbbrevOp.Literal(TypeCodes.OpaquePointer), AbbrevOp.Vbr(2));
        var arrayAbbrev = w.DefineAbbrev(AbbrevOp.Literal(TypeCodes.Array), AbbrevOp.Vbr(8), AbbrevOp.Vbr(6));
        var vectorAbbrev = w.DefineAbbrev(AbbrevOp.Literal(TypeCodes.Vector), AbbrevOp.Vbr(8), AbbrevOp.Vbr(6));
        var structNameAbbrev = w.DefineAbbrev(AbbrevOp.Literal(TypeCodes.StructName), AbbrevOp.Array(), AbbrevOp.Char6());

        foreach (var t in sorted)
            WriteType(w, t, integerAbbrev, pointerAbbrev, arrayAbbrev, vectorAbbrev, structNameAbbrev);

        w.ExitBlock();
    }

    /// <summary>
    /// Depth-first topological sort that places every operand type before
    /// its referencing type. Cycles can only occur through *named* structs
    /// (literal structs are content-keyed and therefore acyclic; cycles
    /// through pointers don't show up at the type level under LLVM 15
    /// opaque pointers); we break those by emitting the named struct
    /// first when the cycle closes — the reader resolves it via its name
    /// when the body record is later replayed. <see cref="LlvmType.Id"/>
    /// is rewritten to match the new emission order so downstream writers
    /// observe the same ids the bitcode reader will assign.
    /// </summary>
    private static List<LlvmType> TopologicallySortAndRenumber(IReadOnlyList<LlvmType> all)
    {
        var sorted = new List<LlvmType>(all.Count);
        var state = new Dictionary<LlvmType, byte>(all.Count); // 0=unseen, 1=visiting, 2=done

        void Visit(LlvmType t)
        {
            if (state.TryGetValue(t, out var s))
            {
                if (s == 2) return;
                // s == 1: cycle. Only legal for named structs; the bitcode
                // reader pre-allocates an opaque placeholder when it sees
                // the forward reference and resolves it by name later.
                if (!(t is StructType { IsLiteral: false }))
                    throw new InvalidOperationException(
                        $"Type cycle through non-named-struct '{t.GetType().Name}'.");
                return;
            }
            state[t] = 1;
            switch (t)
            {
                case ArrayType a: Visit(a.ElementType); break;
                case VectorType v: Visit(v.ElementType); break;
                case PointerType:
                    // Opaque pointers carry no element-type dependency
                    // under LLVM 15; nothing to recurse into.
                    break;
                case FunctionType f:
                    Visit(f.ReturnType);
                    for (int i = 0; i < f.ParameterTypes.Count; i++)
                        Visit(f.ParameterTypes[i]);
                    break;
                case StructType st:
                    if (!st.IsOpaque)
                        for (int i = 0; i < st.ElementTypes.Count; i++)
                            Visit(st.ElementTypes[i]);
                    break;
            }
            state[t] = 2;
            sorted.Add(t);
        }

        for (int i = 0; i < all.Count; i++)
            Visit(all[i]);

        for (int i = 0; i < sorted.Count; i++)
            sorted[i].Id = i;

        return sorted;
    }

    private static void WriteType(BitstreamWriter w, LlvmType t,
        uint integerAbbrev, uint pointerAbbrev, uint arrayAbbrev, uint vectorAbbrev, uint structNameAbbrev)
    {
        switch (t)
        {
            case VoidType: w.WriteUnabbrevRecord(TypeCodes.Void); return;
            case FloatType: w.WriteUnabbrevRecord(TypeCodes.Float); return;
            case DoubleType: w.WriteUnabbrevRecord(TypeCodes.Double); return;
            case LabelType: w.WriteUnabbrevRecord(TypeCodes.Label); return;
            case MetadataType: w.WriteUnabbrevRecord(TypeCodes.Metadata); return;
            case HalfType: w.WriteUnabbrevRecord(TypeCodes.Half); return;
            case BFloatType: w.WriteUnabbrevRecord(TypeCodes.BFloat); return;
            case X86Fp80Type: w.WriteUnabbrevRecord(TypeCodes.X86Fp80); return;
            case Fp128Type: w.WriteUnabbrevRecord(TypeCodes.Fp128); return;
            case PpcFp128Type: w.WriteUnabbrevRecord(TypeCodes.PpcFp128); return;
            case X86MmxType: w.WriteUnabbrevRecord(TypeCodes.X86Mmx); return;
            case X86AmxType: w.WriteUnabbrevRecord(TypeCodes.X86Amx); return;
            case TokenType: w.WriteUnabbrevRecord(TypeCodes.Token); return;

            case IntegerType i:
                w.WriteAbbrevRecord(integerAbbrev, (ulong)i.BitWidth);
                return;

            case PointerType p:
                // Use abbrev only when addrspace fits in 2 bits (0..3); fall back to unabbrev otherwise.
                if (p.AddressSpace >= 0 && p.AddressSpace < 4)
                    w.WriteAbbrevRecord(pointerAbbrev, (ulong)p.AddressSpace);
                else
                    w.WriteUnabbrevRecord(TypeCodes.OpaquePointer, (ulong)p.AddressSpace);
                return;

            case ArrayType a:
                if (a.NumElements < (1UL << 32))
                    w.WriteAbbrevRecord(arrayAbbrev, a.NumElements, (ulong)a.ElementType.Id);
                else
                    w.WriteUnabbrevRecord(TypeCodes.Array, a.NumElements, (ulong)a.ElementType.Id);
                return;

            case VectorType v:
                // Scalable bit needs an extra operand the abbrev doesn't carry; fall back to unabbrev.
                if (v.IsScalable)
                    w.WriteUnabbrevRecord(TypeCodes.Vector, v.NumElements, (ulong)v.ElementType.Id, 1UL);
                else
                    w.WriteAbbrevRecord(vectorAbbrev, v.NumElements, (ulong)v.ElementType.Id);
                return;

            case FunctionType f:
                {
                    var ops = new ulong[2 + f.ParameterTypes.Count];
                    ops[0] = f.IsVarArg ? 1u : 0u;
                    ops[1] = (ulong)f.ReturnType.Id;
                    for (int i = 0; i < f.ParameterTypes.Count; i++)
                        ops[2 + i] = (ulong)f.ParameterTypes[i].Id;
                    w.WriteUnabbrevRecord(TypeCodes.Function, ops);
                    return;
                }

            case StructType s:
                {
                    if (!s.IsLiteral)
                    {
                        var nameBytes = Encoding.UTF8.GetBytes(s.Name!);
                        if (IsAllChar6(nameBytes))
                        {
                            var nameOps = new ulong[nameBytes.Length];
                            for (int i = 0; i < nameBytes.Length; i++) nameOps[i] = nameBytes[i];
                            w.WriteAbbrevRecord(structNameAbbrev, nameOps);
                        }
                        else
                        {
                            var nameOps = new ulong[nameBytes.Length];
                            for (int i = 0; i < nameBytes.Length; i++) nameOps[i] = nameBytes[i];
                            w.WriteUnabbrevRecord(TypeCodes.StructName, nameOps);
                        }
                    }

                    if (s.IsOpaque)
                    {
                        // OPAQUE record marks a named struct with no body resolved yet.
                        w.WriteUnabbrevRecord(TypeCodes.Opaque, 0UL);
                        return;
                    }

                    var ops = new ulong[1 + s.ElementTypes.Count];
                    ops[0] = s.IsPacked ? 1u : 0u;
                    for (int i = 0; i < s.ElementTypes.Count; i++)
                        ops[1 + i] = (ulong)s.ElementTypes[i].Id;
                    w.WriteUnabbrevRecord(s.IsLiteral ? TypeCodes.StructAnon : TypeCodes.StructNamed, ops);
                    return;
                }
        }
        throw new NotSupportedException($"unsupported type {t.GetType().Name}");
    }

    /// <summary>True iff every byte is in the Char6 alphabet (a–z, A–Z, 0–9, '.', '_').</summary>
    private static bool IsAllChar6(byte[] bytes)
    {
        foreach (var b in bytes)
        {
            bool ok = (b >= 'a' && b <= 'z')
                   || (b >= 'A' && b <= 'Z')
                   || (b >= '0' && b <= '9')
                   || b == '.'
                   || b == '_';
            if (!ok) return false;
        }
        return true;
    }
}
