using System.Collections.Generic;
using System.Text;
using Bitsmith.Llvm.Bitstream;
using Bitsmith.Llvm.Codes;
using Bitsmith.Llvm.IR;

namespace Bitsmith.Llvm.Writer;

/// <summary>
/// Writes the module-level METADATA_BLOCK plus the auxiliary
/// METADATA_KIND_BLOCK (registers the "dbg" kind so that
/// METADATA_ATTACHMENT records can refer to kind 0).
/// </summary>
internal sealed class MetadataWriter
{
    private const int MetadataAbbrevWidth = 4;
    private const int KindBlockAbbrevWidth = 3;

    public const uint DbgKind = 0;

    private readonly BitstreamWriter _w;
    private readonly MetadataEnumerator _me;
    private readonly ValueEnumerator _ve;

    private uint _stringOldAbbrev, _nameAbbrev, _valueAbbrev, _namedNodeAbbrev, _stringsAbbrev;

    // Kind-id registry. <c>dbg</c> is permanently bound to id 0 to match
    // LLVM's well-known assignment. Non-dbg kinds (invariant.load,
    // invariant.group, range, nonnull, alias.scope, noalias, ...) get
    // allocated lazily as they're first observed on instruction
    // attachments. The writer emits one METADATA_KIND record per
    // entry from <see cref="WriteKindBlock"/>.
    private readonly Dictionary<string, uint> _kindIds =
        new(System.StringComparer.Ordinal) { ["dbg"] = DbgKind };
    private uint _nextKindId = 1;

    public MetadataWriter(BitstreamWriter w, MetadataEnumerator me, ValueEnumerator ve)
    {
        _w = w;
        _me = me;
        _ve = ve;
    }

    /// <summary>
    /// Look up an existing kind id, or allocate a fresh one if this is
    /// the first time the kind name has been seen. Stable across calls
    /// for the lifetime of this writer (the same name always returns
    /// the same id), so two attachments with the same kind name on
    /// different instructions share a single METADATA_KIND record.
    /// </summary>
    public uint GetOrAllocateKindId(string name)
    {
        if (_kindIds.TryGetValue(name, out var id)) return id;
        id = _nextKindId++;
        _kindIds[name] = id;
        return id;
    }

    /// <summary>True once any instruction or function-level attachment
    /// has been registered. ModuleWriter uses this (alongside
    /// <c>HasDebugInfo</c>) to decide whether to emit the kind block.</summary>
    public bool HasAnyKind => _kindIds.Count > 0;

    public void WriteKindBlock()
    {
        _w.EnterSubBlock(BlockIds.MetadataKind, KindBlockAbbrevWidth);
        // Order by id so the on-wire layout is stable / diff-friendly.
        var ordered = new List<KeyValuePair<string, uint>>(_kindIds);
        ordered.Sort((a, b) => a.Value.CompareTo(b.Value));
        foreach (var kv in ordered)
            WriteKindRecord(kv.Value, kv.Key);
        _w.ExitBlock();
    }

    private void WriteKindRecord(uint kindId, string name)
    {
        var bytes = Encoding.UTF8.GetBytes(name);
        var ops = new ulong[1 + bytes.Length];
        ops[0] = kindId;
        for (int i = 0; i < bytes.Length; i++) ops[1 + i] = bytes[i];
        _w.WriteUnabbrevRecord(MetadataCodes.Kind, ops);
    }

    public void WriteMetadataBlock(IReadOnlyList<NamedMetadata> namedMetadata,
        IReadOnlyList<(GlobalVariable gv, DiGlobalVariableExpression dbg)>? globalAttachments = null)
    {
        if (_me.Ordered.Count == 0 && namedMetadata.Count == 0
            && (globalAttachments is null || globalAttachments.Count == 0)) return;

        // Move all MdStrings to the head of Ordered so their IDs are 0..N-1
        // contiguously — METADATA_STRINGS bundles them as one record at the top.
        _me.FinalizeOrdering();

        _w.EnterSubBlock(BlockIds.Metadata, MetadataAbbrevWidth);

        // Block-local abbrevs for the most frequent METADATA_BLOCK records:
        //   stringsAbbrev:    [Literal(Strings), Vbr(6) numStrings, Vbr(6) stringsOffset, Blob]
        //   stringOldAbbrev:  [Literal(StringOld), Array, Fixed(8) byte]   — legacy fallback
        //   nameAbbrev:       [Literal(Name), Array, Fixed(8) byte]        — named-metadata names
        //   valueAbbrev:      [Literal(Value), Vbr(6) typeId, Vbr(8) valueId]  — value-as-metadata
        //   namedNodeAbbrev:  [Literal(NamedNode), Array, Vbr(6) mdid]
        _stringsAbbrev = _w.DefineAbbrev(AbbrevOp.Literal(MetadataCodes.Strings), AbbrevOp.Vbr(6), AbbrevOp.Vbr(6), AbbrevOp.Blob());
        _stringOldAbbrev = _w.DefineAbbrev(AbbrevOp.Literal(MetadataCodes.StringOld), AbbrevOp.Array(), AbbrevOp.Fixed(8));
        _nameAbbrev = _w.DefineAbbrev(AbbrevOp.Literal(MetadataCodes.Name), AbbrevOp.Array(), AbbrevOp.Fixed(8));
        _valueAbbrev = _w.DefineAbbrev(AbbrevOp.Literal(MetadataCodes.Value), AbbrevOp.Vbr(6), AbbrevOp.Vbr(8));
        _namedNodeAbbrev = _w.DefineAbbrev(AbbrevOp.Literal(MetadataCodes.NamedNode), AbbrevOp.Array(), AbbrevOp.Vbr(6));

        // Emit MdStrings as a single METADATA_STRINGS record at the front.
        int stringCount = _me.StringCount;
        if (stringCount > 0) WriteMetadataStrings(stringCount);

        for (int i = stringCount; i < _me.Ordered.Count; i++)
            WriteMetadataRecord(_me.Ordered[i]);

        foreach (var nm in namedMetadata)
            WriteNamedMetadata(nm);

        if (globalAttachments is not null)
        {
            foreach (var (gv, dbg) in globalAttachments)
            {
                // METADATA_GLOBAL_DECL_ATTACHMENT: [gv_value_id, kind_id, md_id (0-based)]
                _w.WriteUnabbrevRecord(MetadataCodes.GlobalDeclAttachment,
                    (ulong)_ve.GetValueId(gv),
                    DbgKind,
                    (ulong)dbg.Id);
            }
        }

        _w.ExitBlock();
    }

    private void WriteMetadataRecord(Metadata md)
    {
        switch (md)
        {
            case MdString s: WriteString(s); return;
            case MdValue v: WriteValue(v); return;
            case MdTuple t: WriteTuple(t); return;
            case DiFile f: WriteFile(f); return;
            case DiCompileUnit cu: WriteCompileUnit(cu); return;
            case DiBasicType bt: WriteBasicType(bt); return;
            case DiSubroutineType st: WriteSubroutineType(st); return;
            case DiSubprogram sp: WriteSubprogram(sp); return;
            case DiLocation loc: WriteLocation(loc); return;
            case DiDerivedType dt: WriteDerivedType(dt); return;
            case DiCompositeType ct: WriteCompositeType(ct); return;
            case DiLexicalBlock lb: WriteLexicalBlock(lb); return;
            case DiLocalVariable lv: WriteLocalVariable(lv); return;
            case DiExpression e: WriteExpression(e); return;
            case DiSubrange sr: WriteSubrange(sr); return;
            case DiEnumerator en: WriteEnumerator(en); return;
            case DiTemplateTypeParameter ttp: WriteTemplateTypeParameter(ttp); return;
            case DiTemplateValueParameter tvp: WriteTemplateValueParameter(tvp); return;
            case DiNamespace ns: WriteNamespace(ns); return;
            case DiImportedEntity ie: WriteImportedEntity(ie); return;
            case DiLexicalBlockFile lbf: WriteLexicalBlockFile(lbf); return;
            case DiLabel l: WriteLabel(l); return;
            case DiGlobalVariable gv: WriteGlobalVariable(gv); return;
            case DiGlobalVariableExpression gve: WriteGlobalVariableExpression(gve); return;
            case DiGenericSubrange gsr: WriteGenericSubrange(gsr); return;
            case DiObjCProperty op: WriteObjCProperty(op); return;
            case DiMacro mac: WriteMacro(mac); return;
            case DiMacroFile mf: WriteMacroFile(mf); return;
            case DiModule mod: WriteModule(mod); return;
            case DiCommonBlock cb: WriteCommonBlock(cb); return;
            case DiStringType st: WriteStringType(st); return;
            case DiArgList al: WriteArgList(al); return;
        }
        throw new System.NotSupportedException($"unsupported metadata {md.GetType().Name}");
    }

    private void WriteArgList(DiArgList al)
    {
        // METADATA_ARG_LIST: [arg1_md_id, arg2_md_id, ...] — 0-based MD IDs
        // (the reader uses getMetadataFwdRefOrNull which doesn't subtract 1).
        var ops = new ulong[al.Args.Count];
        for (int i = 0; i < al.Args.Count; i++) ops[i] = (ulong)al.Args[i].Id;
        _w.WriteUnabbrevRecord(MetadataCodes.ArgList, ops);
    }

    /// <summary>Emits a function-local METADATA_BLOCK inside the FUNCTION_BLOCK.
    /// Holds MdValue/DiArgList nodes whose wrapped Values are function-local
    /// (Argument/Instruction). Their IDs continue from <see cref="MetadataEnumerator.ModuleMetadataCount"/>.
    /// MdValues must precede DiArgLists that reference them.</summary>
    public void WriteFunctionLocalMetadataBlock(IReadOnlyList<Metadata> localMds)
    {
        if (localMds.Count == 0) return;
        _w.EnterSubBlock(BlockIds.Metadata, MetadataAbbrevWidth);
        var valueAbbrev = _w.DefineAbbrev(
            AbbrevOp.Literal(MetadataCodes.Value), AbbrevOp.Vbr(6), AbbrevOp.Vbr(8));
        foreach (var md in localMds)
        {
            switch (md)
            {
                case MdValue v:
                    _w.WriteAbbrevRecord(valueAbbrev,
                        (ulong)v.Value.Type.Id, (ulong)_ve.GetValueId(v.Value));
                    break;
                case DiArgList al:
                    var ops = new ulong[al.Args.Count];
                    for (int i = 0; i < al.Args.Count; i++)
                        ops[i] = (ulong)al.Args[i].Id;  // 0-based
                    _w.WriteUnabbrevRecord(MetadataCodes.ArgList, ops);
                    break;
                default:
                    throw new System.NotSupportedException(
                        $"unsupported function-local metadata {md.GetType().Name}");
            }
        }
        _w.ExitBlock();
    }

    private void WriteGenericSubrange(DiGenericSubrange gsr)
    {
        // [distinct, count, lowerBound, upperBound, stride] — all metadata IDs (1-based, 0 = none).
        _w.WriteUnabbrevRecord(MetadataCodes.GenericSubrange,
            0,
            (ulong)_me.GetIdOrNull(gsr.Count),
            (ulong)_me.GetIdOrNull(gsr.LowerBound),
            (ulong)_me.GetIdOrNull(gsr.UpperBound),
            (ulong)_me.GetIdOrNull(gsr.Stride));
    }

    private void WriteObjCProperty(DiObjCProperty op)
    {
        var (name, getter, setter) = _me.GetObjCPropertyNames(op);
        // [distinct, name, file, line, getter, setter, attrs, ty]
        _w.WriteUnabbrevRecord(MetadataCodes.ObjcProperty,
            0,
            (ulong)_me.GetIdOrNull(name),
            (ulong)_me.GetIdOrNull(op.File),
            op.Line,
            (ulong)_me.GetIdOrNull(getter),
            (ulong)_me.GetIdOrNull(setter),
            op.Attributes,
            (ulong)_me.GetIdOrNull(op.Type));
    }

    private void WriteMacro(DiMacro mac)
    {
        var (name, value) = _me.GetMacroNames(mac);
        // [distinct, macinfo_type, line, name, value]
        _w.WriteUnabbrevRecord(MetadataCodes.Macro,
            0,
            mac.MacInfo,
            mac.Line,
            (ulong)_me.GetIdOrNull(name),
            (ulong)_me.GetIdOrNull(value));
    }

    private void WriteMacroFile(DiMacroFile mf)
    {
        // [distinct, macinfo_type, line, file, elements]
        _w.WriteUnabbrevRecord(MetadataCodes.MacroFile,
            0,
            mf.MacInfo,
            mf.Line,
            (ulong)_me.GetIdOrNull(mf.File),
            (ulong)_me.GetIdOrNull(mf.Elements));
    }

    private void WriteModule(DiModule mod)
    {
        var (name, configMacros, includePath, apiNotesFile) = _me.GetModuleNames(mod);
        // [distinct, scope, name, configMacros, includePath, apiNotesFile, line, isDecl]
        _w.WriteUnabbrevRecord(MetadataCodes.Module,
            0,
            (ulong)_me.GetIdOrNull(mod.Scope),
            (ulong)_me.GetIdOrNull(name),
            (ulong)_me.GetIdOrNull(configMacros),
            (ulong)_me.GetIdOrNull(includePath),
            (ulong)_me.GetIdOrNull(apiNotesFile),
            mod.Line,
            mod.IsDecl ? 1UL : 0UL);
    }

    private void WriteCommonBlock(DiCommonBlock cb)
    {
        var name = _me.GetCommonBlockName(cb);
        // [distinct, scope, decl, name, file, line]
        _w.WriteUnabbrevRecord(MetadataCodes.CommonBlock,
            0,
            (ulong)_me.GetIdOrNull(cb.Scope),
            (ulong)_me.GetIdOrNull(cb.Decl),
            (ulong)_me.GetIdOrNull(name),
            (ulong)_me.GetIdOrNull(cb.File),
            cb.Line);
    }

    private void WriteStringType(DiStringType st)
    {
        var name = _me.GetStringTypeName(st);
        // [distinct, tag, name, stringLength=null, stringLengthExp=null, stringLocationExp=null, sizeInBits, alignInBits, encoding]
        _w.WriteUnabbrevRecord(MetadataCodes.StringType,
            0,
            st.Tag,
            (ulong)_me.GetIdOrNull(name),
            0UL,
            0UL,
            0UL,
            st.SizeInBits,
            st.AlignInBits,
            st.Encoding);
    }

    private void WriteSubrange(DiSubrange sr)
    {
        // Version 0 form: [distinct, sign-rotated count, sign-rotated lowerbound]
        ulong header = 0; // not distinct, version 0
        _w.WriteUnabbrevRecord(MetadataCodes.Subrange,
            header,
            SignRotate(sr.Count),
            SignRotate(sr.LowerBound));
    }

    private void WriteEnumerator(DiEnumerator en)
    {
        // Small form: [distinct|isUnsigned<<1, sign-rotated 64-bit value, name]
        ulong header = (en.IsUnsigned ? 2UL : 0UL);
        _w.WriteUnabbrevRecord(MetadataCodes.Enumerator,
            header,
            SignRotate(en.Value),
            (ulong)_me.GetIdRequired(_me.GetEnumeratorName(en)));
    }

    private void WriteTemplateTypeParameter(DiTemplateTypeParameter ttp)
    {
        // [distinct, name, type, isDefault]
        var name = _me.GetTtpName(ttp);
        _w.WriteUnabbrevRecord(MetadataCodes.TemplateType,
            0,
            (ulong)_me.GetIdOrNull(name),
            (ulong)_me.GetIdOrNull(ttp.Type),
            ttp.IsDefault ? 1UL : 0UL);
    }

    private void WriteTemplateValueParameter(DiTemplateValueParameter tvp)
    {
        // [distinct, tag, name, type, isDefault, value]
        var name = _me.GetTvpName(tvp);
        _w.WriteUnabbrevRecord(MetadataCodes.TemplateValue,
            0,
            tvp.Tag,
            (ulong)_me.GetIdOrNull(name),
            (ulong)_me.GetIdOrNull(tvp.Type),
            tvp.IsDefault ? 1UL : 0UL,
            (ulong)_me.GetIdOrNull(tvp.Value));
    }

    private void WriteNamespace(DiNamespace ns)
    {
        // [distinct|exportSymbols<<1, scope, name]
        var name = _me.GetNamespaceName(ns);
        ulong header = ns.ExportSymbols ? 2UL : 0UL;
        _w.WriteUnabbrevRecord(MetadataCodes.Namespace,
            header,
            (ulong)_me.GetIdOrNull(ns.Scope),
            (ulong)_me.GetIdOrNull(name));
    }

    private void WriteImportedEntity(DiImportedEntity ie)
    {
        // [distinct, tag, scope, entity, line, name, file]
        var name = _me.GetImportedEntityName(ie);
        _w.WriteUnabbrevRecord(MetadataCodes.ImportedEntity,
            0,
            ie.Tag,
            (ulong)_me.GetIdOrNull(ie.Scope),
            (ulong)_me.GetIdOrNull(ie.Entity),
            ie.Line,
            (ulong)_me.GetIdOrNull(name),
            (ulong)_me.GetIdOrNull(ie.File));
    }

    private void WriteLexicalBlockFile(DiLexicalBlockFile lbf)
    {
        _w.WriteUnabbrevRecord(MetadataCodes.LexicalBlockFile,
            0,
            (ulong)_me.GetIdRequired(lbf.Scope),
            (ulong)_me.GetIdOrNull(lbf.File),
            lbf.Discriminator);
    }

    private void WriteLabel(DiLabel l)
    {
        _w.WriteUnabbrevRecord(MetadataCodes.Label,
            0,
            (ulong)_me.GetIdRequired(l.Scope),
            (ulong)_me.GetIdRequired(_me.GetLabelName(l)),
            (ulong)_me.GetIdOrNull(l.File),
            l.Line);
    }

    private void WriteGlobalVariable(DiGlobalVariable gv)
    {
        // First op packs [distinct, version<<1].
        // Version 1 expects: [scope, name, linkage, file, line, type, isLocal,
        //                     isDefinition, staticDataMember, templateParams, alignInBits]
        var (name, linkage) = _me.GetGlobalVarNames(gv);
        const ulong Version = 1;
        ulong header = (Version << 1);
        _w.WriteUnabbrevRecord(MetadataCodes.GlobalVar,
            header,
            (ulong)_me.GetIdOrNull(gv.Scope),
            (ulong)_me.GetIdOrNull(name),
            (ulong)_me.GetIdOrNull(linkage),
            (ulong)_me.GetIdOrNull(gv.File),
            gv.Line,
            (ulong)_me.GetIdOrNull(gv.Type),
            gv.IsLocalToUnit ? 1UL : 0UL,
            gv.IsDefinition ? 1UL : 0UL,
            (ulong)_me.GetIdOrNull(gv.StaticDataMember),
            (ulong)_me.GetIdOrNull(gv.TemplateParams),
            gv.AlignInBits);
    }

    private void WriteGlobalVariableExpression(DiGlobalVariableExpression gve)
    {
        _w.WriteUnabbrevRecord(MetadataCodes.GlobalVarExpr,
            0,
            (ulong)_me.GetIdRequired(gve.Variable),
            (ulong)_me.GetIdRequired(gve.Expression));
    }

    private static ulong SignRotate(long v) =>
        v < 0 ? ((unchecked((ulong)-v)) << 1) | 1UL : ((ulong)v) << 1;

    private void WriteString(MdString s)
    {
        var bytes = Encoding.UTF8.GetBytes(s.Value);
        var ops = new ulong[bytes.Length];
        for (int i = 0; i < bytes.Length; i++) ops[i] = bytes[i];
        _w.WriteAbbrevRecord(_stringOldAbbrev, ops);
    }

    /// <summary>
    /// Emits all MdStrings (the first <paramref name="count"/> entries of Ordered) as
    /// a single METADATA_STRINGS record. Format: [numStrings, stringsOffset, blob]
    /// where blob = [VBR6 length per string ...] then [all string bytes concatenated].
    /// stringsOffset is the byte offset within the blob where the concatenated bytes begin.
    /// </summary>
    private void WriteMetadataStrings(int count)
    {
        // Encode each string's UTF-8 bytes once.
        var encoded = new byte[count][];
        for (int i = 0; i < count; i++)
            encoded[i] = Encoding.UTF8.GetBytes(((MdString)_me.Ordered[i]).Value);

        // First half: VBR6 lengths packed bit-tight into bytes.
        var lengthBuf = new BitstreamWriter();
        for (int i = 0; i < count; i++)
            lengthBuf.WriteVBR((uint)encoded[i].Length, 6);
        lengthBuf.FlushToByte();
        var lengthBytes = lengthBuf.ToArray();

        // Concatenate: [length-bits bytes][all string bytes].
        int totalStringBytes = 0;
        for (int i = 0; i < count; i++) totalStringBytes += encoded[i].Length;
        var blob = new byte[lengthBytes.Length + totalStringBytes];
        System.Buffer.BlockCopy(lengthBytes, 0, blob, 0, lengthBytes.Length);
        int pos = lengthBytes.Length;
        for (int i = 0; i < count; i++)
        {
            System.Buffer.BlockCopy(encoded[i], 0, blob, pos, encoded[i].Length);
            pos += encoded[i].Length;
        }

        ulong[] preamble = { (ulong)count, (ulong)lengthBytes.Length };
        _w.WriteBlobAbbrevRecord(_stringsAbbrev, preamble, blob);
    }

    private void WriteValue(MdValue v)
    {
        // [type_id, value_id] (absolute)
        _w.WriteAbbrevRecord(_valueAbbrev, (ulong)v.Value.Type.Id, (ulong)_ve.GetValueId(v.Value));
    }

    private void WriteTuple(MdTuple t)
    {
        var ops = new ulong[t.Operands.Count];
        for (int i = 0; i < t.Operands.Count; i++)
            ops[i] = (ulong)_me.GetIdOrNull(t.Operands[i]);
        _w.WriteUnabbrevRecord(t.IsDistinct ? MetadataCodes.DistinctNode : MetadataCodes.Node, ops);
    }

    private void WriteFile(DiFile f)
    {
        var (filename, directory) = _me.GetFileStrings(f);
        // [distinct, filename, directory] — the reader accepts 3, 5, or 6 ops.
        _w.WriteUnabbrevRecord(MetadataCodes.File,
            0,
            (ulong)_me.GetIdRequired(filename),
            (ulong)_me.GetIdRequired(directory));
    }

    private void WriteCompileUnit(DiCompileUnit cu)
    {
        var (producer, flags, splitDebugFilename, sysroot, sdk) = _me.GetCompileUnitStrings(cu);
        // [distinct, lang, file, producer, isOptimized, flags, runtimeVersion,
        //  splitDebugFilename, emissionKind, enumTypes, retainedTypes, subprograms(deprecated)=0,
        //  globals, imports, dwoId, macros, splitDebugInlining, debugInfoForProfiling,
        //  nameTableKind, rangesBaseAddress, sysroot, sdk]
        _w.WriteUnabbrevRecord(MetadataCodes.CompileUnit,
            1,                                              // CompileUnits are always distinct
            cu.SourceLanguage,
            (ulong)_me.GetIdRequired(cu.File),
            (ulong)_me.GetIdOrNull(producer),
            cu.IsOptimized ? 1UL : 0UL,
            (ulong)_me.GetIdOrNull(flags),
            cu.RuntimeVersion,
            (ulong)_me.GetIdOrNull(splitDebugFilename),
            (ulong)cu.EmissionKind,
            (ulong)_me.GetIdOrNull(cu.EnumTypes),
            (ulong)_me.GetIdOrNull(cu.RetainedTypes),
            0,                                              // subprograms (deprecated)
            (ulong)_me.GetIdOrNull(cu.Globals),
            (ulong)_me.GetIdOrNull(cu.Imports),
            cu.DwoId,
            (ulong)_me.GetIdOrNull(cu.Macros),
            cu.SplitDebugInlining ? 1UL : 0UL,
            cu.DebugInfoForProfiling ? 1UL : 0UL,
            (ulong)cu.NameTableKind,
            cu.RangesBaseAddress ? 1UL : 0UL,
            (ulong)_me.GetIdOrNull(sysroot),
            (ulong)_me.GetIdOrNull(sdk));
    }

    private void WriteBasicType(DiBasicType bt)
    {
        // [distinct, tag, name, size, align, encoding, flags]
        var name = _me.GetBasicTypeName(bt);
        _w.WriteUnabbrevRecord(MetadataCodes.BasicType,
            0,
            bt.Tag,
            (ulong)_me.GetIdOrNull(name),
            bt.SizeInBits,
            bt.AlignInBits,
            bt.Encoding,
            bt.Flags);
    }

    private void WriteSubroutineType(DiSubroutineType st)
    {
        // [distinct, flags, types, cc]
        _w.WriteUnabbrevRecord(MetadataCodes.SubroutineType,
            0,
            0,
            (ulong)_me.GetIdRequired(st.Types),
            0);
    }

    private void WriteSubprogram(DiSubprogram sp)
    {
        var (name, linkage, targetFunc) = _me.GetSubprogramNames(sp);
        bool hasTargetFunc = targetFunc is not null;
        // First byte (header): bit0 = isDistinct, bit1 = hasUnit, bit2 = hasSPFlags, bit3 = hasTargetFuncName.
        ulong header = 1UL | (1UL << 1) | (1UL << 2) | (hasTargetFunc ? (1UL << 3) : 0UL);
        var ops = new System.Collections.Generic.List<ulong>(20)
        {
            header,
            (ulong)_me.GetIdOrNull(sp.Scope),
            (ulong)_me.GetIdOrNull(name),
            (ulong)_me.GetIdOrNull(linkage),
            (ulong)_me.GetIdRequired(sp.File),
            sp.Line,
            (ulong)_me.GetIdOrNull(sp.Type),
            sp.ScopeLine,
            (ulong)_me.GetIdOrNull(sp.VirtualIndex),
            (ulong)sp.SpFlags,
            sp.VirtualIndexValue,
            sp.Flags,
            (ulong)_me.GetIdOrNull(sp.Unit),
            sp.ThisAdjustment,
            (ulong)_me.GetIdOrNull(sp.Declaration),
            (ulong)_me.GetIdOrNull(sp.RetainedNodes),
            (ulong)_me.GetIdOrNull(sp.ThrownTypes),
            (ulong)_me.GetIdOrNull(sp.Annotations),
        };
        if (hasTargetFunc) ops.Add((ulong)_me.GetIdRequired(targetFunc!));
        _w.WriteUnabbrevRecord(MetadataCodes.Subprogram, ops.ToArray());
    }

    private void WriteLocation(DiLocation loc)
    {
        _w.WriteUnabbrevRecord(MetadataCodes.Location,
            0,
            loc.Line,
            loc.Column,
            (ulong)_me.GetIdRequired(loc.Scope),
            (ulong)_me.GetIdOrNull(loc.InlinedAt),
            loc.IsImplicitCode ? 1UL : 0UL);
    }

    private void WriteDerivedType(DiDerivedType dt)
    {
        var name = _me.GetDerivedName(dt);
        _w.WriteUnabbrevRecord(MetadataCodes.DerivedType,
            0,
            dt.Tag,
            (ulong)_me.GetIdOrNull(name),
            (ulong)_me.GetIdOrNull(dt.File),
            dt.Line,
            (ulong)_me.GetIdOrNull(dt.Scope),
            (ulong)_me.GetIdOrNull(dt.BaseType),
            dt.SizeInBits,
            dt.AlignInBits,
            dt.OffsetInBits,
            dt.Flags,
            (ulong)_me.GetIdOrNull(dt.ExtraData),
            dt.DwarfAddressSpace.HasValue ? (ulong)(dt.DwarfAddressSpace.Value + 1) : 0UL);
    }

    private void WriteCompositeType(DiCompositeType ct)
    {
        var (name, identifier) = _me.GetCompositeStrings(ct);
        _w.WriteUnabbrevRecord(MetadataCodes.CompositeType,
            0,
            ct.Tag,
            (ulong)_me.GetIdOrNull(name),
            (ulong)_me.GetIdOrNull(ct.File),
            ct.Line,
            (ulong)_me.GetIdOrNull(ct.Scope),
            (ulong)_me.GetIdOrNull(ct.BaseType),
            ct.SizeInBits,
            ct.AlignInBits,
            ct.OffsetInBits,
            ct.Flags,
            (ulong)_me.GetIdOrNull(ct.Elements),
            ct.RuntimeLang,
            (ulong)_me.GetIdOrNull(ct.VTableHolder),
            (ulong)_me.GetIdOrNull(ct.TemplateParams),
            (ulong)_me.GetIdOrNull(identifier),
            0,                          // discriminator
            0, 0, 0, 0);
    }

    private void WriteLexicalBlock(DiLexicalBlock lb)
    {
        _w.WriteUnabbrevRecord(MetadataCodes.LexicalBlock,
            0,
            (ulong)_me.GetIdRequired(lb.Scope),
            (ulong)_me.GetIdOrNull(lb.File),
            lb.Line,
            lb.Column);
    }

    private void WriteLocalVariable(DiLocalVariable lv)
    {
        var name = _me.GetLocalVarName(lv);
        // First op packs [distinct, hasAlignment]. We always include alignment slot.
        ulong header = 0UL | (1UL << 1);
        _w.WriteUnabbrevRecord(MetadataCodes.LocalVar,
            header,
            (ulong)_me.GetIdRequired(lv.Scope),
            (ulong)_me.GetIdOrNull(name),
            (ulong)_me.GetIdOrNull(lv.File),
            lv.Line,
            (ulong)_me.GetIdOrNull(lv.Type),
            lv.Arg,
            lv.Flags,
            lv.AlignInBits,
            0);
    }

    private void WriteExpression(DiExpression e)
    {
        // First op packs [distinct, version<<1]. Current version = 3.
        ulong header = (3UL << 1);
        var ops = new ulong[1 + e.Elements.Count];
        ops[0] = header;
        for (int i = 0; i < e.Elements.Count; i++) ops[i + 1] = e.Elements[i];
        _w.WriteUnabbrevRecord(MetadataCodes.Expression, ops);
    }

    private void WriteNamedMetadata(NamedMetadata nm)
    {
        var nameBytes = Encoding.UTF8.GetBytes(nm.Name);
        var nameOps = new ulong[nameBytes.Length];
        for (int i = 0; i < nameBytes.Length; i++) nameOps[i] = nameBytes[i];
        _w.WriteAbbrevRecord(_nameAbbrev, nameOps);

        var ops = new ulong[nm.Operands.Count];
        for (int i = 0; i < nm.Operands.Count; i++)
            ops[i] = (ulong)nm.Operands[i].Id;
        _w.WriteAbbrevRecord(_namedNodeAbbrev, ops);
    }
}

