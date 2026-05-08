using System.Collections.Generic;
using Bitsmith.Llvm.IR;

namespace Bitsmith.Llvm.Writer;

/// <summary>
/// Assigns sequential IDs to module-level metadata in post-order (operands before users).
/// IDs are 0-based; bitcode references use ID + 1 (0 = null).
/// String fields on DI nodes are auto-interned as <see cref="MdString"/> entries.
/// </summary>
internal sealed class MetadataEnumerator
{
    private readonly List<Metadata> _ordered = new();
    private readonly HashSet<Metadata> _visited = new(ReferenceComparer<Metadata>.Instance);
    private readonly Dictionary<string, MdString> _strings = new();
    private readonly Dictionary<DiBasicType, MdString?> _basicTypeName = new(ReferenceComparer<DiBasicType>.Instance);
    private readonly Dictionary<DiSubprogram, (MdString? name, MdString? linkage, MdString? targetFunc)> _subprogramNames = new(ReferenceComparer<DiSubprogram>.Instance);
    private readonly Dictionary<DiFile, (MdString filename, MdString directory)> _fileStrings = new(ReferenceComparer<DiFile>.Instance);
    private readonly Dictionary<DiCompileUnit, (MdString? producer, MdString? flags, MdString? splitDebugFilename, MdString? sysroot, MdString? sdk)> _cuStrings = new(ReferenceComparer<DiCompileUnit>.Instance);
    private readonly Dictionary<DiDerivedType, MdString?> _derivedName = new(ReferenceComparer<DiDerivedType>.Instance);
    private readonly Dictionary<DiCompositeType, (MdString? name, MdString? identifier)> _compositeStrings = new(ReferenceComparer<DiCompositeType>.Instance);
    private readonly Dictionary<DiLocalVariable, MdString?> _localVarName = new(ReferenceComparer<DiLocalVariable>.Instance);
    private readonly Dictionary<DiEnumerator, MdString> _enumeratorName = new(ReferenceComparer<DiEnumerator>.Instance);
    private readonly Dictionary<DiTemplateTypeParameter, MdString?> _ttpName = new(ReferenceComparer<DiTemplateTypeParameter>.Instance);
    private readonly Dictionary<DiTemplateValueParameter, MdString?> _tvpName = new(ReferenceComparer<DiTemplateValueParameter>.Instance);
    private readonly Dictionary<DiNamespace, MdString?> _nsName = new(ReferenceComparer<DiNamespace>.Instance);
    private readonly Dictionary<DiImportedEntity, MdString?> _ieName = new(ReferenceComparer<DiImportedEntity>.Instance);
    private readonly Dictionary<DiLabel, MdString> _labelName = new(ReferenceComparer<DiLabel>.Instance);
    private readonly Dictionary<DiGlobalVariable, (MdString? name, MdString? linkage)> _gvNames = new(ReferenceComparer<DiGlobalVariable>.Instance);
    private readonly Dictionary<DiObjCProperty, (MdString? name, MdString? getter, MdString? setter)> _objcNames = new(ReferenceComparer<DiObjCProperty>.Instance);
    private readonly Dictionary<DiMacro, (MdString? name, MdString? value)> _macroNames = new(ReferenceComparer<DiMacro>.Instance);
    private readonly Dictionary<DiModule, (MdString? name, MdString? configMacros, MdString? includePath, MdString? apiNotesFile)> _moduleNames = new(ReferenceComparer<DiModule>.Instance);
    private readonly Dictionary<DiCommonBlock, MdString?> _commonName = new(ReferenceComparer<DiCommonBlock>.Instance);
    private readonly Dictionary<DiStringType, MdString?> _stringTypeName = new(ReferenceComparer<DiStringType>.Instance);

    public IReadOnlyList<Metadata> Ordered => _ordered;

    /// <summary>True if this metadata transitively references a function-local Value
    /// (an Argument or Instruction). Such metadata cannot be emitted in the module
    /// METADATA_BLOCK because the wrapped Value's id is only known inside the function.</summary>
    public static bool IsFunctionLocal(Metadata? md) => md switch
    {
        null => false,
        MdValue v => v.Value is Argument or Instruction,
        DiArgList al => al.Args.Count > 0 && AnyArgFunctionLocal(al),
        _ => false,
    };

    private static bool AnyArgFunctionLocal(DiArgList al)
    {
        foreach (var a in al.Args)
            if (IsFunctionLocal(a)) return true;
        return false;
    }

    public MdString GetEnumeratorName(DiEnumerator e) => _enumeratorName[e];
    public MdString? GetTtpName(DiTemplateTypeParameter t) => _ttpName[t];
    public MdString? GetTvpName(DiTemplateValueParameter t) => _tvpName[t];
    public MdString? GetNamespaceName(DiNamespace n) => _nsName[n];
    public MdString? GetImportedEntityName(DiImportedEntity ie) => _ieName[ie];
    public MdString GetLabelName(DiLabel l) => _labelName[l];
    public (MdString? name, MdString? linkage) GetGlobalVarNames(DiGlobalVariable gv) => _gvNames[gv];
    public (MdString? name, MdString? getter, MdString? setter) GetObjCPropertyNames(DiObjCProperty op) => _objcNames[op];
    public (MdString? name, MdString? value) GetMacroNames(DiMacro mac) => _macroNames[mac];
    public (MdString? name, MdString? configMacros, MdString? includePath, MdString? apiNotesFile) GetModuleNames(DiModule m) => _moduleNames[m];
    public MdString? GetCommonBlockName(DiCommonBlock cb) => _commonName[cb];
    public MdString? GetStringTypeName(DiStringType st) => _stringTypeName[st];

    public MdString InternString(string s)
    {
        if (!_strings.TryGetValue(s, out var ms))
        {
            ms = new MdString(s);
            _strings[s] = ms;
            Add(ms);
        }
        return ms;
    }

    public MdString? InternStringOrNull(string? s) => s is null ? null : InternString(s);

    public void Add(Metadata? md)
    {
        if (md is null) return;
        if (!_visited.Add(md)) return;

        // Resolve string fields (creates new MdStrings as a side-effect, also added).
        switch (md)
        {
            case DiFile f:
                _fileStrings[f] = (InternString(f.Filename), InternString(f.Directory));
                break;
            case DiCompileUnit cu:
                _cuStrings[cu] = (InternStringOrNull(cu.Producer), InternStringOrNull(cu.Flags),
                    InternStringOrNull(cu.SplitDebugFilename),
                    InternStringOrNull(cu.Sysroot),
                    InternStringOrNull(cu.Sdk));
                Add(cu.File);
                Add(cu.EnumTypes); Add(cu.RetainedTypes); Add(cu.Globals);
                Add(cu.Imports); Add(cu.Macros);
                break;
            case DiBasicType bt:
                _basicTypeName[bt] = InternStringOrNull(bt.Name);
                break;
            case DiSubprogram sp:
                _subprogramNames[sp] = (InternStringOrNull(sp.Name), InternStringOrNull(sp.LinkageName),
                    InternStringOrNull(sp.TargetFuncName));
                Add(sp.Scope); Add(sp.File); Add(sp.Type); Add(sp.Unit); Add(sp.RetainedNodes);
                Add(sp.Declaration); Add(sp.ThrownTypes); Add(sp.Annotations);
                Add(sp.VirtualIndex);
                break;
            case DiSubroutineType st:
                Add(st.Types);
                break;
            case DiLocation loc:
                Add(loc.Scope);
                Add(loc.InlinedAt);
                break;
            case DiDerivedType dt:
                _derivedName[dt] = InternStringOrNull(dt.Name);
                Add(dt.File); Add(dt.Scope); Add(dt.BaseType); Add(dt.ExtraData);
                break;
            case DiCompositeType ct:
                _compositeStrings[ct] = (InternStringOrNull(ct.Name), InternStringOrNull(ct.Identifier));
                Add(ct.File); Add(ct.Scope); Add(ct.BaseType); Add(ct.Elements);
                Add(ct.VTableHolder); Add(ct.TemplateParams);
                break;
            case DiLexicalBlock lb:
                Add(lb.Scope); Add(lb.File);
                break;
            case DiLocalVariable lv:
                _localVarName[lv] = InternStringOrNull(lv.Name);
                Add(lv.Scope); Add(lv.File); Add(lv.Type);
                break;
            case MdTuple t:
                foreach (var op in t.Operands) Add(op);
                break;
            case DiEnumerator en:
                _enumeratorName[en] = InternString(en.Name);
                break;
            case DiTemplateTypeParameter ttp:
                _ttpName[ttp] = InternStringOrNull(ttp.Name);
                Add(ttp.Type);
                break;
            case DiTemplateValueParameter tvp:
                _tvpName[tvp] = InternStringOrNull(tvp.Name);
                Add(tvp.Type); Add(tvp.Value);
                break;
            case DiNamespace ns:
                _nsName[ns] = InternStringOrNull(ns.Name);
                Add(ns.Scope);
                break;
            case DiImportedEntity ie:
                _ieName[ie] = InternStringOrNull(ie.Name);
                Add(ie.Scope); Add(ie.Entity); Add(ie.File);
                break;
            case DiLexicalBlockFile lbf:
                Add(lbf.Scope); Add(lbf.File);
                break;
            case DiLabel l:
                _labelName[l] = InternString(l.Name);
                Add(l.Scope); Add(l.File);
                break;
            case DiGlobalVariable gv:
                _gvNames[gv] = (InternStringOrNull(gv.Name), InternStringOrNull(gv.LinkageName));
                Add(gv.Scope); Add(gv.File); Add(gv.Type);
                Add(gv.StaticDataMember); Add(gv.TemplateParams);
                break;
            case DiGlobalVariableExpression gve:
                Add(gve.Variable); Add(gve.Expression);
                break;
            case DiSubrange:
                // No nested metadata in our simplified version-0 form.
                break;
            case DiGenericSubrange gsr:
                Add(gsr.Count); Add(gsr.LowerBound); Add(gsr.UpperBound); Add(gsr.Stride);
                break;
            case DiObjCProperty op:
                _objcNames[op] = (InternStringOrNull(op.Name),
                                  InternStringOrNull(op.GetterName),
                                  InternStringOrNull(op.SetterName));
                Add(op.File); Add(op.Type);
                break;
            case DiMacro mac:
                _macroNames[mac] = (InternStringOrNull(mac.Name), InternStringOrNull(mac.Value));
                break;
            case DiMacroFile mf:
                Add(mf.File); Add(mf.Elements);
                break;
            case DiModule mod:
                _moduleNames[mod] = (InternStringOrNull(mod.Name),
                                      InternStringOrNull(mod.ConfigMacros),
                                      InternStringOrNull(mod.IncludePath),
                                      InternStringOrNull(mod.ApiNotesFile));
                Add(mod.Scope); Add(mod.File);
                break;
            case DiCommonBlock cb:
                _commonName[cb] = InternStringOrNull(cb.Name);
                Add(cb.Scope); Add(cb.Decl); Add(cb.File);
                break;
            case DiStringType st2:
                _stringTypeName[st2] = InternStringOrNull(st2.Name);
                break;
            case DiArgList al:
                foreach (var a in al.Args) Add(a);
                break;
            // MdString, MdValue, DiExpression have no metadata operands.
        }

        // Function-local MdValue/DiArgList are deferred to the per-function
        // METADATA_BLOCK; their IDs are assigned in FunctionWriter.
        if (IsFunctionLocal(md))
        {
            _visited.Remove(md);
            return;
        }

        md.Id = _ordered.Count;
        _ordered.Add(md);
    }

    public int GetIdOrNull(Metadata? md) => md is null ? 0 : md.Id + 1;
    public int GetIdRequired(Metadata md) => md.Id + 1;

    /// <summary>Total module-level metadata count after <see cref="FinalizeOrdering"/>.
    /// Function-local metadata IDs start from this value.</summary>
    public int ModuleMetadataCount => _ordered.Count;

    /// <summary>
    /// Reorders <see cref="Ordered"/> so all <see cref="MdString"/>s come first
    /// (preserving relative order within each group) and reassigns Ids by position.
    /// METADATA_STRINGS bundles all strings into a single record at the head of
    /// the metadata array, so their IDs must be 0..N-1 contiguously.
    /// </summary>
    public void FinalizeOrdering()
    {
        var strings = new List<Metadata>();
        var others = new List<Metadata>();
        foreach (var m in _ordered)
            (m is MdString ? strings : others).Add(m);
        _ordered.Clear();
        _ordered.AddRange(strings);
        _ordered.AddRange(others);
        for (int i = 0; i < _ordered.Count; i++) _ordered[i].Id = i;
    }

    public int StringCount
    {
        get
        {
            int n = 0;
            foreach (var m in _ordered) if (m is MdString) n++; else break;
            return n;
        }
    }

    public MdString? GetBasicTypeName(DiBasicType bt) => _basicTypeName[bt];
    public (MdString? name, MdString? linkage, MdString? targetFunc) GetSubprogramNames(DiSubprogram sp) => _subprogramNames[sp];
    public (MdString filename, MdString directory) GetFileStrings(DiFile f) => _fileStrings[f];
    public (MdString? producer, MdString? flags, MdString? splitDebugFilename, MdString? sysroot, MdString? sdk) GetCompileUnitStrings(DiCompileUnit cu) => _cuStrings[cu];
    public MdString? GetDerivedName(DiDerivedType dt) => _derivedName[dt];
    public (MdString? name, MdString? identifier) GetCompositeStrings(DiCompositeType ct) => _compositeStrings[ct];
    public MdString? GetLocalVarName(DiLocalVariable lv) => _localVarName[lv];
}
