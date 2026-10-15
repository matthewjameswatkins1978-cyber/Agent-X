using System.Diagnostics;
using System.Text;
using System.Text.Json;
using AgentX.App;
using AgentX.Core;
using Avalonia.Media;

namespace AgentX.FeedBench;

/// <summary>
/// Beautiful Feed same-state performance probe. Everything here is UI-free
/// (Avalonia controls construct without a render platform, exactly as the
/// App tests do), so the same binary runs at a baseline checkout and at the
/// PR2 head and prints directly comparable numbers:
///
///   parse-cold / parse-warm   MarkdownLite.Parse over N markdown messages
///                             (fresh string instances vs the feed's stable ones)
///   rebuild-view              RebuildConversationList's UI-free core: feed
///                             snapshot -> view items -> settled Markdown parse
///   inline-rows               MarkdownView.Rebuild for the M rows a viewport
///                             realizes (appearance/size switch cost)
///   appearance-sim            ApplyAppearance + rebuild-view + inline-rows,
///                             i.e. one full feed appearance switch without
///                             the UI thread
///   streaming                 Core streaming path: AppendDelta bursts with a
///                             snapshot per flush tick (SyncFeedView's input)
///
/// Usage: dotnet run -c Release --project tests/AgentX.FeedBench [messages] [iters] [rows] [out.json]
/// </summary>
internal static class Program
{
    private static readonly List<object> Results = [];

    private static void Record(string scenario, int n, int iter, double ms)
        => Results.Add(new { scenario, n, iter, ms = Math.Round(ms, 3) });

    private static double Ms(Action action)
    {
        var sw = Stopwatch.StartNew();
        action();
        sw.Stop();
        return sw.Elapsed.TotalMilliseconds;
    }

    private static string MarkdownMessage(int i) => $$"""
## Change {{i}}

Summary paragraph with **bold**, *italic*, ***bold italic***, ~~struck~~,
`inline code`, a [link](https://pi.dev/docs/agents), a bare URL
https://pi.dev/docs/agents and identity characters 0Oo1lI| with arrows → ⇐.

- path `src/AgentX.App/MarkdownView.cs` at `2ed97c1ba1caf9b4ab36a4ca07a78af2c9f028bc`
- parser `src/AgentX.Core/MarkdownLite.cs`
  - nested detail line for depth
- D:\Projects\wt-agent-x-feed-pr2\src\AgentX.App\AxTheme.cs

```csharp
var sha = "2ed97c1ba1caf9b4ab36a4ca07a78af2c9f028bc";
if (path.Contains("MarkdownView.cs")) { return null; }
```

> quoted aside that spans
> a second source line

Done with change {{i}}.
""";

    private static string[] BuildMessages(int count)
    {
        var messages = new string[count];
        for (var i = 0; i < count; i++)
        {
            messages[i] = MarkdownMessage(i);
        }

        return messages;
    }

    private static ConversationFeed BuildFeed(int pairs)
    {
        var feed = new ConversationFeed();
        for (var i = 0; i < pairs; i++)
        {
            feed.AddUser($"Apply change {i} and verify it.");
            var seq = feed.BeginAssistantTurn();
            feed.AppendDelta(seq, MarkdownMessage(i));
            feed.EndAssistantTurn(seq, null);
            if (i % 4 == 0)
            {
                var tool = feed.AddToolStart("powershell", $"change {i}", null, isMutating: false);
                feed.AddToolEnd("powershell", isError: false);
                _ = tool;
            }
        }

        return feed;
    }

    /// Mirror of MainWindow.ToViewItem's UI-free core (the private app mapper
    /// is not reachable from here): entry -> view item, settled assistant rows
    /// get their Markdown parsed, exactly like RebuildConversationList.
    private static List<ConversationViewItem> BuildViews(IReadOnlyList<FeedEntry> entries)
    {
        var items = new List<ConversationViewItem>(entries.Count);
        foreach (var entry in entries)
        {
            var kind = entry.Role switch
            {
                "user" => FeedBlockKind.User,
                "assistant" => FeedBlockKind.Assistant,
                "tool" or "receipt" => FeedBlockKind.Tool,
                _ => FeedBlockKind.System,
            };

            var item = new ConversationViewItem(
                entry.Seq,
                entry.Role,
                entry.Role,
                FeedEntry.TruncateForDisplay(entry.Text),
                entry.Text,
                kind,
                entry.IsStreaming,
                Brushes.Transparent);
            if (kind == FeedBlockKind.Assistant && !entry.IsStreaming && entry.Text.Length > 0)
            {
                item.Markdown = MarkdownLite.Parse(item.FullText);
            }

            items.Add(item);
        }

        return items;
    }

    private static void Main(string[] args)
    {
        var messages = args.Length > 0 ? int.Parse(args[0]) : 500;
        var iters = args.Length > 1 ? int.Parse(args[1]) : 5;
        var rows = args.Length > 2 ? int.Parse(args[2]) : 40;
        var outFile = args.Length > 3 ? args[3] : null;

        Console.WriteLine($"feed-bench messages={messages} iters={iters} viewportRows={rows}");
        Console.WriteLine($"machine={Environment.MachineName} os={Environment.OSVersion} cores={Environment.ProcessorCount}");

        var texts = BuildMessages(messages);

        // parse-cold: fresh string instances every iteration (cache-cold).
        for (var iter = 0; iter < iters; iter++)
        {
            var fresh = texts.Select(t => new string(t.ToCharArray())).ToArray();
            Record("parse-cold", messages, iter, Ms(() =>
            {
                foreach (var text in fresh)
                {
                    _ = MarkdownLite.Parse(text);
                }
            }));
        }

        // parse-warm: the feed's stable string instances (cache-warm).
        for (var iter = 0; iter < iters; iter++)
        {
            Record("parse-warm", messages, iter, Ms(() =>
            {
                foreach (var text in texts)
                {
                    _ = MarkdownLite.Parse(text);
                }
            }));
        }

        var feed = BuildFeed(messages);
        var snapshot = feed.Snapshot();

        // rebuild-view: snapshot -> view items -> markdown parse per settled row.
        for (var iter = 0; iter < iters; iter++)
        {
            List<ConversationViewItem> items = null!;
            Record("rebuild-view", snapshot.Count, iter, Ms(() => items = BuildViews(snapshot)));
            GC.KeepAlive(items);
        }

        // inline-rows: MarkdownView.Rebuild for the realized viewport rows.
        var docs = texts.Take(rows).Select(MarkdownLite.Parse).ToArray();
        for (var iter = 0; iter < iters; iter++)
        {
            Record("inline-rows", rows, iter, Ms(() =>
            {
                for (var i = 0; i < rows; i++)
                {
                    var view = new MarkdownView { Markdown = docs[i % docs.Length] };
                    GC.KeepAlive(view);
                }
            }));
        }

        // appearance-sim: fonts repoint + full rebuild + realized-row inlines.
        for (var iter = 0; iter < iters; iter++)
        {
            var target = iter % 2 == 0 ? AxFeedAppearance.Operator : AxFeedAppearance.Studio;
            Record("appearance-sim", snapshot.Count, iter, Ms(() =>
            {
                AxTheme.ApplyAppearance(target, AxFeed.DefaultSize);
                _ = BuildViews(snapshot);
                for (var i = 0; i < rows; i++)
                {
                    GC.KeepAlive(new MarkdownView { Markdown = docs[i % docs.Length] });
                }
            }));
        }

        // streaming: delta bursts + a snapshot per flush tick (10 Hz proxy).
        for (var iter = 0; iter < iters; iter++)
        {
            var streamFeed = new ConversationFeed();
            var seq = streamFeed.BeginAssistantTurn();
            var delta = new string('x', 60);
            var snapshots = 0;
            Record("streaming", 2000, iter, Ms(() =>
            {
                for (var i = 0; i < 2000; i++)
                {
                    streamFeed.AppendDelta(seq, delta);
                    if (i % 50 == 49)
                    {
                        _ = streamFeed.Snapshot();
                        snapshots++;
                    }
                }

                streamFeed.EndAssistantTurn(seq, null);
            }));
            GC.KeepAlive(snapshots);
        }

        foreach (var group in Results.Cast<dynamic>().GroupBy(r => r.scenario))
        {
            var values = group.Select(r => (double)r.ms).OrderBy(v => v).ToArray();
            var median = values[values.Length / 2];
            Console.WriteLine($"{group.Key,-16} n={group.First().n,-6} min={values[0],8:F2}ms median={median,8:F2}ms max={values[^1],8:F2}ms");
        }

        if (outFile is not null)
        {
            var payload = JsonSerializer.Serialize(new
            {
                machine = Environment.MachineName,
                os = Environment.OSVersion.ToString(),
                cores = Environment.ProcessorCount,
                messages,
                iters,
                viewportRows = rows,
                results = Results,
            }, new JsonSerializerOptions { WriteIndented = true });
            File.WriteAllText(outFile, payload);
            Console.WriteLine($"wrote {outFile}");
        }
    }
}
