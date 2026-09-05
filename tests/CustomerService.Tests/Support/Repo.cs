namespace CustomerService.Tests.Support;

/// <summary>Paths into the repository, found from wherever the test binary runs.</summary>
public static class Repo
{
    public static string Root { get; } = FindRoot();

    static string FindRoot()
    {
        var dir = new DirectoryInfo(AppContext.BaseDirectory);
        for (int i = 0; i < 8 && dir is not null; i++, dir = dir.Parent)
            if (File.Exists(Path.Combine(dir.FullName, "CustomerService.sln"))) return dir.FullName;
        throw new InvalidOperationException("could not find the repository root from " + AppContext.BaseDirectory);
    }

    public static string CorpusPath => Path.Combine(Root, "corpus", "faq.json");
    public static string ModelPath => Path.Combine(Root, "model-cache", "multilingual-e5-small", "model.onnx");
    public static string TokenizerPath => Path.Combine(Root, "model-cache", "multilingual-e5-small", "tokenizer.json");
    public static bool ModelPresent => File.Exists(ModelPath) && File.Exists(TokenizerPath);
}
