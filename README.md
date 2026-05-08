# Bitsmith

Pure C# LLVM bitcode writer. No `libllvm` dependency.

- Targets **LLVM 15** (opaque pointers).
- .NET 8 / .NET Standard 2.1.
- Generates `.bc` files readable by `llvm-dis`, `lli`, `llc`.

## Status

Early development. The bitstream layer is the first piece being built out.

## Install

```sh
dotnet add package Bitsmith.Llvm
```

## License

MIT
