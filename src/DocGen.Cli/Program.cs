using DocGen.Contracts;
using Microsoft.Build.Locator;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;

// Must run before any Roslyn workspace / MSBuild type is loaded (DocGen.Indexer).
MSBuildLocator.RegisterDefaults();
return await DocGen.Cli.App.RunAsync(args);

namespace DocGen.Cli
{
    public static class App
    {
        const string Usage = """
            Usage: docgen [--config appsettings.json] [--out <dir>] <command> [options]

            Commands:
              index                      Roslyn extraction      -> <WorkDir>/index.json
              check-index <expected>     Compare index.json with expected guards/edges (exit 1 on missing)
              cards                      LLM + deterministic    -> <WorkDir>/cards.json (cached per card hash)
              render                     Markdown pages         -> <OutputDir>/ + .manifest.json
              publish [--dry-run]        Sync <OutputDir> to Confluence
              search-index               Index <OutputDir> into Qdrant
              ask "<question>"           Search docs (retriever + reranker + LLM answer)
              run [--publish] [--dry-run] [--search]
                                         index + cards + render (+ publish) (+ search-index)

            --out <dir>   Markdown output directory (overrides Generator:OutputDir; relative to the current directory).
                          Used by render/run (write) and publish/search-index (read).

            Configuration: appsettings.json (gitignored, holds secrets; template: appsettings.Example.json),
            overridable by environment variables DOCGEN__<Section>__<Key> (e.g. DOCGEN__HandlerCard__ApiKey).
            """;

        public static async Task<int> RunAsync(string[] args)
        {
            var list = args.ToList();
            var configPath = TakeOption(list, "--config") ?? "appsettings.json";
            var outDir = TakeOption(list, "--out");
            if (list.Count == 0 || list[0] is "-h" or "--help")
            {
                Console.WriteLine(Usage);
                return list.Count == 0 ? 2 : 0;
            }

            configPath = Path.GetFullPath(configPath);
            if (!File.Exists(configPath))
            {
                Console.Error.WriteLine($"Config not found: {configPath}");
                var example = Path.Combine(Path.GetDirectoryName(configPath)!, "appsettings.Example.json");
                if (File.Exists(example))
                    Console.Error.WriteLine($"Copy {example} to appsettings.json and fill in BaseAddress/ApiKey of the LLM sections.");
                return 2;
            }

            var rootDir = Path.GetDirectoryName(configPath)!;
            var config = new ConfigurationBuilder()
                .AddJsonFile(configPath, optional: false)
                .AddEnvironmentVariables("DOCGEN__")
                .Build();

            var services = new ServiceCollection()
                .AddLogging(b => b.AddSimpleConsole(o => o.SingleLine = true).SetMinimumLevel(LogLevel.Information))
                .AddSingleton(new DocGenPaths(rootDir))
                .Configure<GeneratorOptions>(config.GetSection("Generator"));
            if (outDir is not null)
                services.PostConfigure<GeneratorOptions>(o => o.OutputDir = Path.GetFullPath(outDir));
            services.AddSingleton<IConfiguration>(config);

            using var cts = new CancellationTokenSource();
            Console.CancelKeyPress += (_, e) => { e.Cancel = true; cts.Cancel(); };

            var command = list[0];
            var rest = list.Skip(1).ToList();
            try
            {
                return command switch
                {
                    "index" => await Index(Build(services, config, indexer: true), cts.Token),
                    "check-index" => CheckIndex(Build(services, config), rest),
                    "cards" => await Cards(Build(services, config, cards: true), cts.Token),
                    "render" => await Render(Build(services, config, render: true), cts.Token),
                    "publish" => await Publish(Build(services, config, confluence: true), rest.Contains("--dry-run"), cts.Token),
                    "search-index" => await SearchIndex(Build(services, config, search: true), cts.Token),
                    "ask" => await Ask(Build(services, config, search: true), string.Join(' ', rest), cts.Token),
                    "run" => await RunAll(services, config, rest, cts.Token),
                    _ => Unknown(command)
                };
            }
            catch (Exception ex) when (ex is not OperationCanceledException)
            {
                Console.Error.WriteLine($"docgen {command} failed: {ex.Message}");
                Console.Error.WriteLine(ex);
                return 1;
            }
        }

        static ServiceProvider Build(IServiceCollection services, IConfiguration config,
            bool indexer = false, bool cards = false, bool render = false, bool confluence = false, bool search = false)
        {
            // Stages are registered only when needed, so e.g. `render` works without Roslyn or LLM configuration.
            if (indexer) DocGen.Indexer.ServiceCollectionExtensions.AddDocGenIndexer(services, config);
            if (cards) DocGen.Cards.ServiceCollectionExtensions.AddDocGenCards(services, config);
            if (render) DocGen.Render.ServiceCollectionExtensions.AddDocGenRender(services, config);
            if (confluence) DocGen.Confluence.ServiceCollectionExtensions.AddDocGenConfluence(services, config);
            if (search) DocGen.Search.ServiceCollectionExtensions.AddDocGenSearch(services, config);
            return services.BuildServiceProvider();
        }

        static string WorkFile(IServiceProvider sp, string name)
        {
            var paths = sp.GetRequiredService<DocGenPaths>();
            var options = sp.GetRequiredService<IOptions<GeneratorOptions>>().Value;
            return Path.Combine(paths.Resolve(options.WorkDir), name);
        }

        static string OutputDir(IServiceProvider sp) =>
            sp.GetRequiredService<DocGenPaths>().Resolve(sp.GetRequiredService<IOptions<GeneratorOptions>>().Value.OutputDir);

        static async Task<int> Index(ServiceProvider sp, CancellationToken ct)
        {
            var index = await sp.GetRequiredService<ICodeIndexer>().IndexAsync(ct);
            var path = WorkFile(sp, "index.json");
            DocGenJson.Write(path, index);
            Console.WriteLine($"index: {index.Entries.Count} entry points, {index.Entities.Count} entities -> {path}");
            return 0;
        }

        static int CheckIndex(ServiceProvider sp, List<string> rest)
        {
            if (rest.Count != 1)
                return Unknown("check-index (expected file argument missing)");
            var index = DocGenJson.Read<CodeIndex>(WorkFile(sp, "index.json"));
            return DocGen.Indexer.IndexCheck.Run(index, Path.GetFullPath(rest[0]));
        }

        static async Task<int> Cards(ServiceProvider sp, CancellationToken ct)
        {
            var index = DocGenJson.Read<CodeIndex>(WorkFile(sp, "index.json"));
            var cards = await sp.GetRequiredService<ICardGenerator>().GenerateAsync(index, ct);
            var path = WorkFile(sp, "cards.json");
            DocGenJson.Write(path, cards);
            var review = AllMeta(cards).Count(m => m.Status == "needs_review");
            var offline = AllMeta(cards).Count(m => m.Status == "offline");
            Console.WriteLine($"cards: {cards.UseCases.Count} use cases, {cards.Entities.Count} entities, {cards.Flows.Count} flows, " +
                              $"{cards.GlobalRules.Count} global rules, {cards.Modules.Count} modules " +
                              $"(needs_review: {review}, offline: {offline}) -> {path}");
            return 0;
        }

        static async Task<int> Render(ServiceProvider sp, CancellationToken ct)
        {
            var cards = DocGenJson.Read<CardSet>(WorkFile(sp, "cards.json"));
            var manifest = await sp.GetRequiredService<IPageRenderer>().RenderAsync(cards, ct);
            Console.WriteLine($"render: {manifest.Pages.Count} pages -> {OutputDir(sp)}");
            return 0;
        }

        static async Task<int> Publish(ServiceProvider sp, bool dryRun, CancellationToken ct)
        {
            var docs = OutputDir(sp);
            var manifest = DocGenJson.Read<Manifest>(Path.Combine(docs, ".manifest.json"));
            var report = await sp.GetRequiredService<IConfluencePublisher>().PublishAsync(docs, manifest, dryRun, ct);
            report.Messages.ForEach(Console.WriteLine);
            Console.WriteLine($"publish{(dryRun ? " (dry-run)" : "")}: created {report.Created}, updated {report.Updated}, unchanged {report.Unchanged}");
            return 0;
        }

        static async Task<int> SearchIndex(ServiceProvider sp, CancellationToken ct)
        {
            var docs = OutputDir(sp);
            var manifest = DocGenJson.Read<Manifest>(Path.Combine(docs, ".manifest.json"));
            var count = await sp.GetRequiredService<IDocsSearchIndexer>().IndexAsync(docs, manifest, ct);
            Console.WriteLine($"search-index: {count} chunks indexed");
            return 0;
        }

        static async Task<int> Ask(ServiceProvider sp, string question, CancellationToken ct)
        {
            if (string.IsNullOrWhiteSpace(question))
                return Unknown("ask (question missing)");
            var answer = await sp.GetRequiredService<IDocsSearch>().AskAsync(question, ct);
            Console.WriteLine(answer.Answer);
            Console.WriteLine();
            foreach (var hit in answer.Sources)
                Console.WriteLine($"  [{hit.Score:0.000}] {hit.Title} › {hit.Section} ({hit.PageId})");
            return 0;
        }

        static async Task<int> RunAll(IServiceCollection services, IConfiguration config, List<string> rest, CancellationToken ct)
        {
            var publish = rest.Contains("--publish");
            var search = rest.Contains("--search");
            var sp = Build(services, config, indexer: true, cards: true, render: true, confluence: publish, search: search);
            foreach (var step in new Func<Task<int>>[]
                     {
                         () => Index(sp, ct),
                         () => Cards(sp, ct),
                         () => Render(sp, ct),
                         () => publish ? Publish(sp, rest.Contains("--dry-run"), ct) : Task.FromResult(0),
                         () => search ? SearchIndex(sp, ct) : Task.FromResult(0)
                     })
            {
                var code = await step();
                if (code != 0)
                    return code;
            }
            return 0;
        }

        static IEnumerable<CardMeta> AllMeta(CardSet c) =>
            c.UseCases.Select(x => x.Meta)
                .Concat(c.Entities.Select(x => x.Meta))
                .Concat(c.Flows.Select(x => x.Meta))
                .Concat(c.GlobalRules.Select(x => x.Meta))
                .Concat(c.Modules.Select(x => x.Meta))
                .Concat(c.System is null ? [] : [c.System.Meta])
                .Concat(c.Glossary is null ? [] : [c.Glossary.Meta]);

        static int Unknown(string what)
        {
            Console.Error.WriteLine($"Unknown or invalid command: {what}");
            Console.Error.WriteLine(Usage);
            return 2;
        }

        static string? TakeOption(List<string> args, string name)
        {
            var i = args.IndexOf(name);
            if (i < 0 || i + 1 >= args.Count)
                return null;
            var value = args[i + 1];
            args.RemoveRange(i, 2);
            return value;
        }
    }
}
