using System.Text.RegularExpressions;
using CustomerService.Tests.Support;

namespace CustomerService.Tests;

/// <summary>The files that are read by people and by Compose, held to what the code promises.</summary>
public class DeploymentTests
{
    static string Read(string relative) => File.ReadAllText(Path.Combine(Repo.Root, relative));

    /// <summary>
    /// Compose does not inject an undeclared variable. Anything documented in .env.example
    /// has to be listed in the app service's environment, or the symptom is a default quietly
    /// taking effect in the container.
    /// </summary>
    [Fact]
    public void EveryDocumentedVariableReachesTheContainer()
    {
        var documented = Regex.Matches(Read(".env.example"), @"^([A-Z_]+)=", RegexOptions.Multiline).Select(m => m.Groups[1].Value).ToHashSet();
        var compose = Read("docker-compose.yml");
        var appEnv = compose[compose.IndexOf("  app:")..];
        var declared = Regex.Matches(appEnv, @"^\s{6}([A-Z_]+):", RegexOptions.Multiline).Select(m => m.Groups[1].Value).ToHashSet();
        Assert.True(documented.Count >= 15, "the example file should document the tunables");
        var missing = documented.Except(declared).ToList();
        Assert.True(missing.Count == 0, "documented in .env.example but not passed into the container: " + string.Join(", ", missing));
    }

    [Fact]
    public void TheDefaultPortMatchesWhatTheDocumentsPromise()
    {
        Assert.Contains("HTTP_ADDR=:8082", Read(".env.example"));
        Assert.Contains("EXPOSE 8082", Read("Dockerfile"));
        Assert.Contains("\"8082:8082\"", Read("docker-compose.yml"));
        Assert.Contains("localhost:8082", Read("README.md"));
        Assert.Contains("localhost:8082", Read("README.zh.md"));
        Assert.Contains("localhost:16688", Read("README.md"));
    }

    /// <summary>
    /// Nothing re-derives a translation, so the check compares heading-level sequences -- the
    /// drift that actually happens is a section added to one file and not the other.
    /// </summary>
    [Fact]
    public void BothReadmesHaveTheSameSectionStructure()
    {
        static List<int> Headings(string md) => Regex.Matches(md, @"^(#{1,4}) ", RegexOptions.Multiline).Select(m => m.Groups[1].Value.Length).ToList();
        var en = Headings(Read("README.md"));
        var zh = Headings(Read("README.zh.md"));
        Assert.True(en.Count >= 8, "the README should have sections");
        Assert.Equal(en, zh);
    }

    [Fact]
    public void TheReadmesLinkToEachOther()
    {
        Assert.Contains("](README.zh.md)", Read("README.md"));
        Assert.Contains("](README.md)", Read("README.zh.md"));
        foreach (var doc in Directory.GetFiles(Path.Combine(Repo.Root, "docs"), "*.md"))
            Assert.Contains("](../README.md)", File.ReadAllText(doc));
    }

    [Fact]
    public void TheSystemPromptIsTheSharedOne()
    {
        // Prompt parity with the Java and Go implementations is part of what makes the three
        // comparable. The hash is of the Go implementation's constant on 2026-09-05.
        var hash = Convert.ToHexStringLower(System.Security.Cryptography.SHA256.HashData(
            System.Text.Encoding.UTF8.GetBytes(Chat.ChatService.SystemPrompt)));
        Assert.Equal("c3fc1181c3df5d85bfbaeeaa05c1aeac554235b5b6b08170527b1a7255ab5614", hash);
    }
}
