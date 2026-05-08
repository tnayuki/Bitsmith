using System;
using System.Collections.Generic;
using System.Linq;

namespace Bitsmith.Llvm.IR;

/// <summary>
/// Owns the module's type table. Primitive and structurally-equal composite types are deduplicated.
/// Each registered type receives a sequential <see cref="LlvmType.Id"/>.
/// </summary>
public sealed class TypeContext
{
    private readonly List<LlvmType> _types = new();

    private readonly Dictionary<int, IntegerType> _integers = new();
    private readonly Dictionary<int, PointerType> _pointers = new();
    private readonly Dictionary<(ulong, LlvmType), ArrayType> _arrays = new();
    private readonly Dictionary<(uint, LlvmType), VectorType> _vectors = new();
    private readonly Dictionary<FunctionKey, FunctionType> _functions = new();
    private readonly Dictionary<StructKey, StructType> _anonStructs = new();

    public VoidType Void { get; }
    public FloatType Float { get; }
    public DoubleType Double { get; }
    public LabelType Label { get; }
    public MetadataType Metadata { get; }

    private HalfType? _half;
    private BFloatType? _bfloat;
    private X86Fp80Type? _x86Fp80;
    private Fp128Type? _fp128;
    private PpcFp128Type? _ppcFp128;
    private X86MmxType? _x86Mmx;
    private X86AmxType? _x86Amx;
    private TokenType? _token;

    /// <summary>16-bit IEEE half-precision floating point.</summary>
    public HalfType Half => _half ??= Register(new HalfType());
    /// <summary>16-bit brain floating point.</summary>
    public BFloatType BFloat => _bfloat ??= Register(new BFloatType());
    /// <summary>80-bit x87 extended precision.</summary>
    public X86Fp80Type X86Fp80 => _x86Fp80 ??= Register(new X86Fp80Type());
    /// <summary>128-bit IEEE quad precision.</summary>
    public Fp128Type Fp128 => _fp128 ??= Register(new Fp128Type());
    /// <summary>128-bit PowerPC double-double.</summary>
    public PpcFp128Type PpcFp128 => _ppcFp128 ??= Register(new PpcFp128Type());
    /// <summary>Legacy 64-bit MMX vector.</summary>
    public X86MmxType X86Mmx => _x86Mmx ??= Register(new X86MmxType());
    /// <summary>AMX tile (LLVM 15).</summary>
    public X86AmxType X86Amx => _x86Amx ??= Register(new X86AmxType());
    /// <summary>Abstract token (used by funclet EH).</summary>
    public TokenType Token => _token ??= Register(new TokenType());

    public TypeContext()
    {
        Void = Register(new VoidType());
        Float = Register(new FloatType());
        Double = Register(new DoubleType());
        Label = Register(new LabelType());
        Metadata = Register(new MetadataType());
    }

    public IReadOnlyList<LlvmType> AllTypes => _types;

    public IntegerType GetInteger(int bits)
    {
        if (!_integers.TryGetValue(bits, out var t))
            _integers[bits] = t = Register(new IntegerType(bits));
        return t;
    }

    public IntegerType Int1 => GetInteger(1);
    public IntegerType Int8 => GetInteger(8);
    public IntegerType Int16 => GetInteger(16);
    public IntegerType Int32 => GetInteger(32);
    public IntegerType Int64 => GetInteger(64);

    public PointerType GetPointer(int addressSpace = 0)
    {
        if (!_pointers.TryGetValue(addressSpace, out var t))
            _pointers[addressSpace] = t = Register(new PointerType(addressSpace));
        return t;
    }

    public ArrayType GetArray(ulong numElements, LlvmType element)
    {
        var key = (numElements, element);
        if (!_arrays.TryGetValue(key, out var t))
            _arrays[key] = t = Register(new ArrayType(numElements, element));
        return t;
    }

    public VectorType GetVector(uint numElements, LlvmType element)
    {
        var key = (numElements, element);
        if (!_vectors.TryGetValue(key, out var t))
            _vectors[key] = t = Register(new VectorType(numElements, element));
        return t;
    }

    private readonly Dictionary<(uint, LlvmType), VectorType> _scalableVectors = new();
    /// <summary>Scalable vector type — `<vscale x N x T>`. <paramref name="minNumElements"/>
    /// is the minimum (vscale=1) element count.</summary>
    public VectorType GetScalableVector(uint minNumElements, LlvmType element)
    {
        var key = (minNumElements, element);
        if (!_scalableVectors.TryGetValue(key, out var t))
            _scalableVectors[key] = t = Register(new VectorType(minNumElements, element, isScalable: true));
        return t;
    }

    public FunctionType GetFunction(LlvmType returnType, IReadOnlyList<LlvmType> parameterTypes, bool isVarArg = false)
    {
        var key = new FunctionKey(returnType, parameterTypes.ToArray(), isVarArg);
        if (!_functions.TryGetValue(key, out var t))
            _functions[key] = t = Register(new FunctionType(returnType, key.Parameters, isVarArg));
        return t;
    }

    /// <summary>Anonymous (literal) struct; deduplicated by element list and packing.</summary>
    public StructType GetStruct(IReadOnlyList<LlvmType> elements, bool isPacked = false)
    {
        var key = new StructKey(elements.ToArray(), isPacked);
        if (!_anonStructs.TryGetValue(key, out var t))
            _anonStructs[key] = t = Register(new StructType(null, key.Elements, isPacked));
        return t;
    }

    /// <summary>Named (identified) struct. Always distinct, never deduplicated.</summary>
    public StructType CreateNamedStruct(string name, IReadOnlyList<LlvmType> elements, bool isPacked = false)
    {
        if (string.IsNullOrEmpty(name)) throw new ArgumentException("name required", nameof(name));
        return Register(new StructType(name, elements.ToArray(), isPacked));
    }

    /// <summary>Forward-declares an opaque named struct (`%S = type opaque`). Resolve
    /// later via <see cref="StructType.SetBody"/>; supports self-recursive types.</summary>
    public StructType CreateOpaqueNamedStruct(string name)
    {
        if (string.IsNullOrEmpty(name)) throw new ArgumentException("name required", nameof(name));
        return Register(new StructType(name, Array.Empty<LlvmType>(), isPacked: false, isOpaque: true));
    }

    private T Register<T>(T type) where T : LlvmType
    {
        type.Id = _types.Count;
        _types.Add(type);
        return type;
    }

    private readonly struct FunctionKey : IEquatable<FunctionKey>
    {
        public readonly LlvmType Return;
        public readonly LlvmType[] Parameters;
        public readonly bool IsVarArg;
        public FunctionKey(LlvmType ret, LlvmType[] p, bool va) { Return = ret; Parameters = p; IsVarArg = va; }
        public bool Equals(FunctionKey other)
        {
            if (Return != other.Return || IsVarArg != other.IsVarArg) return false;
            if (Parameters.Length != other.Parameters.Length) return false;
            for (int i = 0; i < Parameters.Length; i++)
                if (Parameters[i] != other.Parameters[i]) return false;
            return true;
        }
        public override bool Equals(object? obj) => obj is FunctionKey k && Equals(k);
        public override int GetHashCode()
        {
            var h = HashCode.Combine(Return, IsVarArg, Parameters.Length);
            foreach (var p in Parameters) h = HashCode.Combine(h, p);
            return h;
        }
    }

    private readonly struct StructKey : IEquatable<StructKey>
    {
        public readonly LlvmType[] Elements;
        public readonly bool IsPacked;
        public StructKey(LlvmType[] elements, bool isPacked) { Elements = elements; IsPacked = isPacked; }
        public bool Equals(StructKey other)
        {
            if (IsPacked != other.IsPacked) return false;
            if (Elements.Length != other.Elements.Length) return false;
            for (int i = 0; i < Elements.Length; i++)
                if (Elements[i] != other.Elements[i]) return false;
            return true;
        }
        public override bool Equals(object? obj) => obj is StructKey k && Equals(k);
        public override int GetHashCode()
        {
            var h = HashCode.Combine(IsPacked, Elements.Length);
            foreach (var e in Elements) h = HashCode.Combine(h, e);
            return h;
        }
    }
}
