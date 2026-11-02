using Vortice.D3DCompiler;

namespace UskEffect.Graphics;

public sealed record ShaderCompileResult(byte[]? Bytecode, string? Errors)
{
    public bool Succeeded => Bytecode is not null;
}

/// <summary>HLSL ファイルを実行時コンパイルする。エラーは例外ではなく文字列で返し UI に表示する (FR-10)。</summary>
public static class ShaderCompiler
{
    public static ShaderCompileResult CompileFile(string path, string entryPoint, string profile)
    {
        string source;
        try
        {
            source = ReadShared(path);
        }
        catch (IOException ex)
        {
            return new ShaderCompileResult(null, $"{path}: {ex.Message}");
        }

        var flags = ShaderFlags.OptimizationLevel3 | ShaderFlags.EnableStrictness;
#if DEBUG
        flags = ShaderFlags.Debug | ShaderFlags.SkipOptimization | ShaderFlags.EnableStrictness;
#endif
        var result = Compiler.Compile(source, null!, null!, entryPoint, path, profile, flags, out var blob, out var errorBlob);
        using (blob)
        using (errorBlob)
        {
            string? messages = errorBlob is null ? null : errorBlob.AsString();
            if (result.Failure || blob is null)
                return new ShaderCompileResult(null, messages ?? $"{path}: compile failed (0x{result.Code:X8})");
            return new ShaderCompileResult(blob.AsBytes(), null);
        }
    }

    // エディタが保存中でも読めるよう共有モードで開く
    private static string ReadShared(string path)
    {
        using var stream = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.ReadWrite | FileShare.Delete);
        using var reader = new StreamReader(stream);
        // Compiler.Compile(string) は文字数を ANSI バイト数として渡すため、日本語コメントがあると末尾が切れる。
        // HLSL の識別子は ASCII のみなので、非 ASCII 文字は空白に置き換える (行・列番号は保たれる)。
        var chars = reader.ReadToEnd().ToCharArray();
        for (int i = 0; i < chars.Length; i++)
        {
            if (chars[i] > 0x7F)
                chars[i] = ' ';
        }
        return new string(chars);
    }
}
