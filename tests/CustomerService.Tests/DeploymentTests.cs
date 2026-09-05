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

    // ---- the separately deployed UI -----------------------------------------------------

    /// <summary>Model and customer text reach the page as text. No sink turns a string into markup.</summary>
    [Fact]
    public void TheAdminUiNeverTurnsAStringIntoMarkup()
    {
        var files = Directory.GetFiles(Path.Combine(Repo.Root, "admin-ui", "src"), "*.ts*", SearchOption.AllDirectories);
        Assert.True(files.Length >= 10, "the UI sources should be present");
        foreach (var f in files)
        {
            var code = File.ReadAllText(f);
            foreach (var sink in new[] { "dangerouslySetInnerHTML", "innerHTML", "document.write", "eval(", "new Function(" })
                Assert.DoesNotContain(sink, code);
        }
    }

    /// <summary>The UI's container proxies the API and forbids scripts from anywhere but itself.</summary>
    [Fact]
    public void TheAdminUiIsServedBehindAProxyWithAContentSecurityPolicy()
    {
        var nginx = Read("admin-ui/templates/default.conf.template");
        Assert.Contains("proxy_pass http://${ADMIN_API_UPSTREAM}/api/;", nginx);
        Assert.Contains("ADMIN_API_UPSTREAM: app:8082", Read("docker-compose.yml"));
        Assert.Contains("try_files $uri /index.html;", nginx);
        Assert.Contains("default-src 'self'", nginx);
        Assert.Contains("add_header Content-Security-Policy $csp always;", nginx);
        Assert.Contains("\"8083:8083\"", Read("docker-compose.yml"));
        Assert.Contains("ADMIN_ENABLED", Read("docker-compose.yml"));
    }

    /// <summary>Every state and action the page knows is one the server knows, by name.</summary>
    [Fact]
    public void TheUiAndTheServerAgreeOnTicketStatesAndActions()
    {
        var ui = Read("admin-ui/src/tickets.ts");
        foreach (var state in new[] { "open", "claimed", "resolved", "closed" }) Assert.Contains($"'{state}'", ui);
        var server = Read("src/CustomerService/HttpApi/AdminEndpoints.cs");
        foreach (var action in new[] { "claim", "assign", "release", "resolve", "close", "reopen", "note" })
        {
            Assert.Contains($"'{action}'", ui);
            Assert.Contains($"\"{action}\"", server);
        }
    }

    // ---- Kubernetes ----------------------------------------------------------------------

    /// <summary>
    /// A tunable is described in two places, .env.example and the ConfigMap, and the two
    /// cannot drift: every ConfigMap key is a documented variable, apart from the runtime's
    /// own DOTNET_* knobs, which the service never reads.
    /// </summary>
    [Fact]
    public void EveryConfigMapKeyIsADocumentedVariable()
    {
        var documented = Regex.Matches(Read(".env.example"), @"^([A-Z_]+)=", RegexOptions.Multiline).Select(m => m.Groups[1].Value).ToHashSet();
        var configMap = Read("k8s/configmap.yaml");
        var keys = Regex.Matches(configMap, @"^  ([A-Z][A-Za-z_]+): ", RegexOptions.Multiline).Select(m => m.Groups[1].Value).Where(k => !k.StartsWith("DOTNET_")).ToList();
        Assert.True(keys.Count >= 12, "the ConfigMap should carry the tunables");
        var undocumented = keys.Where(k => !documented.Contains(k)).ToList();
        Assert.True(undocumented.Count == 0, "in the ConfigMap but not in .env.example: " + string.Join(", ", undocumented));
    }

    [Fact]
    public void TheManifestsAgreeWithTheImagesOnPortsAndUsers()
    {
        var deployment = Read("k8s/deployment.yaml");
        Assert.Contains("containerPort: 8082", deployment);
        Assert.Contains("runAsUser: 1654", deployment);      // the aspnet image's app user
        Assert.Contains("readOnlyRootFilesystem: true", deployment);
        Assert.DoesNotContain("volumes:", deployment);        // the API pod needs none
        var ui = Read("k8s/admin-ui.yaml");
        Assert.Contains("containerPort: 8083", ui);
        Assert.Contains("runAsUser: 101", ui);                // nginx-unprivileged's user
        Assert.Contains("ADMIN_API_UPSTREAM", ui);
        Assert.Contains("EXPOSE 8083", Read("admin-ui/Dockerfile"));
        Assert.Contains("nginx-unprivileged", Read("admin-ui/Dockerfile"));
        // The Secret template must not sit where `kubectl apply -f k8s/` would sweep it up.
        Assert.False(File.Exists(Path.Combine(Repo.Root, "k8s", "secret.yaml")));
        Assert.True(File.Exists(Path.Combine(Repo.Root, "k8s", "examples", "secret.yaml")));
    }

    /// <summary>The numbers in the manifest are the ones the sweep table in the same file records.</summary>
    [Fact]
    public void TheResourceNumbersMatchTheSweepTableBesideThem()
    {
        var deployment = Read("k8s/deployment.yaml");
        var request = Regex.Match(deployment, @"requests:\s*\n\s*cpu: ""[^""]+""\s*\n(?:\s*#.*\n)*\s*memory: ""(\w+)""").Groups[1].Value;
        var limit = Regex.Match(deployment, @"limits:\s*\n(?:\s*#.*\n)*\s*cpu: ""[^""]+""\s*\n(?:\s*#.*\n)*\s*memory: ""(\w+)""").Groups[1].Value;
        Assert.Equal("1152Mi", request);
        Assert.Equal("1536Mi", limit);
        Assert.Contains($"#   {request}    started", deployment);
        Assert.Contains($"#   {limit}    started", deployment);
        Assert.Contains("#   896Mi     OOMKilled", deployment);
    }
}
