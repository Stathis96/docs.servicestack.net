﻿// run node postinstall.js to update to latest version

using System.Text.RegularExpressions;
using Markdig;
using Markdig.Extensions.CustomContainers;
using Markdig.Parsers;
using Markdig.Parsers.Inlines;
using Markdig.Renderers;
using Markdig.Renderers.Html;
using Markdig.Syntax;
using Markdig.Syntax.Inlines;
using ServiceStack.IO;
using ServiceStack.Text;

namespace Ssg;

public class MarkdownFileBase
{
    public string Path { get; set; } = default!;
    public string? Slug { get; set; }
    public string? Layout { get; set; }
    public string? FileName { get; set; }
    public string? HtmlFileName { get; set; }
    /// <summary>
    /// Whether to hide this document in Production
    /// </summary>
    public bool Draft { get; set; }
    public string? Title { get; set; }
    public string? Summary { get; set; }
    public string? Image { get; set; }
    public string? Author { get; set; }
    public List<string> Tags { get; set; } = new();
    /// <summary>
    /// Date document is published. Documents with future Dates are only shown in Development 
    /// </summary>
    public DateTime? Date { get; set; }
    public string? Content { get; set; }
    public string? Url { get; set; }
    /// <summary>
    /// The rendered HTML of the Markdown
    /// </summary>
    public string? Preview { get; set; }
    public string? HtmlPage { get; set; }
    public int? WordCount { get; set; }
    public int? LineCount { get; set; }
    public string? Group { get; set; }
    public int? Order { get; set; }
    public DocumentMap? DocumentMap { get; set; }

    /// <summary>
    /// Update Markdown File to latest version
    /// </summary>
    /// <param name="newDoc"></param>
    public virtual void Update(MarkdownFileBase newDoc)
    {
        Layout = newDoc.Layout;
        Title = newDoc.Title;
        Summary = newDoc.Summary;
        Draft = newDoc.Draft;
        Image = newDoc.Image;
        Author = newDoc.Author;
        Tags = newDoc.Tags;
        Content = newDoc.Content;
        Url = newDoc.Url;
        Preview = newDoc.Preview;
        HtmlPage = newDoc.HtmlPage;
        WordCount = newDoc.WordCount;
        LineCount = newDoc.LineCount;
        Group = newDoc.Group;
        Order = newDoc.Order;
        DocumentMap = newDoc.DocumentMap;

        if (newDoc.Date != null)
            Date = newDoc.Date;
    }
}

public interface IMarkdownPages
{
    string Id { get; }
    IVirtualFiles VirtualFiles { get; set; }
    List<MarkdownFileBase> GetAll();
}
public abstract class MarkdownPagesBase<T> : IMarkdownPages where T : MarkdownFileBase
{
    public abstract string Id { get; }
    protected ILogger Log { get; }
    protected IWebHostEnvironment Environment { get; }

    public MarkdownPagesBase(ILogger log, IWebHostEnvironment env)
    {
        this.Log = log;
        this.Environment = env;
    }
    
    public IVirtualFiles VirtualFiles { get; set; } = default!;
    
    public virtual MarkdownPipeline CreatePipeline()
    {
        var pipeline = new MarkdownPipelineBuilder()
            .UseYamlFrontMatter()
            .UseAdvancedExtensions()
            .UseAutoLinkHeadings()
            .UseHeadingsMap()
            .UseCustomContainers()
            .Build();
        return pipeline;
    }

    public virtual List<T> Fresh(List<T> docs)
    {
        if (docs.IsEmpty())
            return docs;
        foreach (var doc in docs)
        {
            Fresh(doc);
        }
        return docs;
    }
    
    public virtual T? Fresh(T? doc)
    {
        // Ignore reloading source .md if run in production or as AppTask
        if (doc == null || !Environment.IsDevelopment() || AppTasks.IsRunAsAppTask())
            return doc;
        var newDoc = Load(doc.Path);
        doc.Update(newDoc);
        return doc;
    }

    public virtual T CreateMarkdownFile(string content, TextWriter writer, MarkdownPipeline? pipeline = null)
    {
        pipeline ??= CreatePipeline();
        
        var renderer = new Markdig.Renderers.HtmlRenderer(writer);
        pipeline.Setup(renderer);

        var document = Markdown.Parse(content, pipeline);
        renderer.Render(document);

        var block = document
            .Descendants<Markdig.Extensions.Yaml.YamlFrontMatterBlock>()
            .FirstOrDefault();

        var doc = block?
            .Lines // StringLineGroup[]
            .Lines // StringLine[]
            .Select(x => $"{x}\n")
            .ToList()
            .Select(x => x.Replace("---", string.Empty))
            .Where(x => !string.IsNullOrWhiteSpace(x))
            .Select(x => KeyValuePairs.Create(x.LeftPart(':').Trim(), x.RightPart(':').Trim()))
            .ToObjectDictionary()
            .ConvertTo<T>()
            ?? typeof(T).CreateInstance<T>();

        doc.Content = content;
        doc.DocumentMap = document.GetData(nameof(DocumentMap)) as DocumentMap;

        return doc;
    }

    public virtual T? Load(string path, MarkdownPipeline? pipeline = null)
    {
        var file = VirtualFiles.GetFile(path)
                   ?? throw new FileNotFoundException(path.LastRightPart('/'));
        var content = file.ReadAllText();

        var writer = new StringWriter();

        var doc = CreateMarkdownFile(content, writer, pipeline);
        doc.Title ??= file.Name;

        doc.Path = file.VirtualPath;
        doc.FileName = file.Name;
        doc.Slug = doc.FileName.WithoutExtension().CreateSlug();
        doc.Content = content;
        doc.WordCount = WordCount(content);
        doc.LineCount = LineCount(content);
        writer.Flush();
        doc.Preview = writer.ToString();
        doc.Date ??= file.LastModified;

        return doc;
    }

    public virtual bool IsVisible(T doc) => Environment.IsDevelopment() || 
        !doc.Draft && (doc.Date == null || doc.Date.Value <= DateTime.UtcNow);
    
    public int WordsPerMin { get; set; } = 225;
    public char[] WordBoundaries { get; set; } = { ' ', '.', '?', '!', '(', ')', '[', ']' };
    public virtual int WordCount(string str) => str.Split(WordBoundaries, StringSplitOptions.RemoveEmptyEntries).Length;
    public virtual int LineCount(string str) => str.CountOccurrencesOf('\n');
    public virtual int MinutesToRead(int? words) => (int)Math.Ceiling((words ?? 1) / (double)WordsPerMin);
    
    protected IVirtualFiles AssertVirtualFiles() => 
        VirtualFiles ?? throw new NullReferenceException($"{nameof(VirtualFiles)} is not populated");

    public virtual List<MarkdownFileBase> GetAll() => new();

    public virtual string? StripFrontmatter(string? content)
    {
        if (content == null)
            return null;
        var startPos = content.IndexOf("---", StringComparison.CurrentCulture);
        if (startPos == -1)
            return content;
        var endPos = content.IndexOf("---", startPos + 3, StringComparison.Ordinal);
        if (endPos == -1)
            return content;
        return content.Substring(endPos + 3).Trim();
    }

    public virtual MarkdownFileBase ToMetaDoc(T x, Action<MarkdownFileBase>? fn = null)
    {
        var to = new MarkdownFileBase
        {
            Slug = x.Slug,
            Title = x.Title,
            Summary = x.Summary,
            Date = x.Date,
            Tags = x.Tags,
            Author = x.Author,
            Image = x.Image,
            WordCount = x.WordCount,
            LineCount = x.LineCount,
            Url = x.Url,
            Group = x.Group,
            Order = x.Order,
        };
        fn?.Invoke(to);
        return to;
    }
}

public struct HeadingInfo
{
    public int Level { get; }
    public string Id { get; }
    public string Content { get; }

    public HeadingInfo(int level, string id, string content)
    {
        Level = level;
        Id = id;
        Content = content;
    }
}

/// <summary>
/// An HTML renderer for a <see cref="HeadingBlock"/>.
/// </summary>
/// <seealso cref="HtmlObjectRenderer{TObject}" />
public class AutoLinkHeadingRenderer : HtmlObjectRenderer<HeadingBlock>
{
    private static readonly string[] HeadingTexts = {
        "h1",
        "h2",
        "h3",
        "h4",
        "h5",
        "h6",
    };
    public event Action<HeadingBlock>? OnHeading;

    protected override void Write(HtmlRenderer renderer, HeadingBlock obj)
    {
        int index = obj.Level - 1;
        string[] headings = HeadingTexts;
        string headingText = ((uint)index < (uint)headings.Length)
            ? headings[index]
            : $"h{obj.Level}";

        if (renderer.EnableHtmlForBlock)
        {
            renderer.Write('<');
            renderer.Write(headingText);
            renderer.WriteAttributes(obj);
            renderer.Write('>');
        }

        renderer.WriteLeafInline(obj);

        var attrs = obj.TryGetAttributes();
        if (attrs?.Id != null && obj.Level <= 4)
        {
            renderer.Write("<a class=\"header-anchor\" href=\"javascript:;\" onclick=\"location.hash='#");
            renderer.Write(attrs.Id);
            renderer.Write("'\" aria-label=\"Permalink\">&ZeroWidthSpace;</a>");
        }

        if (renderer.EnableHtmlForBlock)
        {
            renderer.Write("</");
            renderer.Write(headingText);
            renderer.WriteLine('>');
        }

        renderer.EnsureLine();

        OnHeading?.Invoke(obj);
    }
}
public class AutoLinkHeadingsExtension : IMarkdownExtension
{
    public void Setup(MarkdownPipelineBuilder pipeline)
    {
    }

    public void Setup(MarkdownPipeline pipeline, IMarkdownRenderer renderer)
    {
        renderer.ObjectRenderers.Replace<HeadingRenderer>(new AutoLinkHeadingRenderer());
    }
}

public class CopyContainerRenderer : HtmlObjectRenderer<CustomContainer>
{
    public string Class { get; set; } = "";
    public string BoxClass { get; set; } = "bg-gray-700";
    public string IconClass { get; set; } = "";
    public string TextClass { get; set; } = "text-lg text-white";
    protected override void Write(HtmlRenderer renderer, CustomContainer obj)
    {
        renderer.EnsureLine();
        if (renderer.EnableHtmlForBlock)
        {
            renderer.Write(@$"<div class=""{Class} flex cursor-pointer mb-3"" onclick=""copy(this)"">
                <div class=""flex-grow {BoxClass}"">
                    <div class=""pl-4 py-1 pb-1.5 align-middle {TextClass}"">");
        }
        // We don't escape a CustomContainer
        renderer.WriteChildren(obj);
        if (renderer.EnableHtmlForBlock)
        {
            renderer.WriteLine(@$"</div>
                    </div>
                <div class=""flex"">
                    <div class=""{IconClass} text-white p-1.5 pb-0"">
                        <svg class=""copied w-6 h-6"" fill=""none"" stroke=""currentColor"" viewBox=""0 0 24 24"" xmlns=""http://www.w3.org/2000/svg""><path stroke-linecap=""round"" stroke-linejoin=""round"" stroke-width=""2"" d=""M5 13l4 4L19 7""></path></svg>
                        <svg class=""nocopy w-6 h-6"" title=""copy"" fill='none' stroke='white' viewBox='0 0 24 24' xmlns='http://www.w3.org/2000/svg'>
                            <path stroke-linecap='round' stroke-linejoin='round' stroke-width='1' d='M8 7v8a2 2 0 002 2h6M8 7V5a2 2 0 012-2h4.586a1 1 0 01.707.293l4.414 4.414a1 1 0 01.293.707V15a2 2 0 01-2 2h-2M8 7H6a2 2 0 00-2 2v10a2 2 0 002 2h8a2 2 0 002-2v-2'></path>
                        </svg>
                    </div>
                </div>
            </div>");
        }
    }    
}

public class CustomInfoRenderer : HtmlObjectRenderer<CustomContainer>
{
    public string Title { get; set; } = "TIP";
    public string Class { get; set; } = "tip";
    protected override void Write(HtmlRenderer renderer, CustomContainer obj)
    {
        renderer.EnsureLine();
        if (renderer.EnableHtmlForBlock)
        {
            var title = obj.Arguments ?? obj.Info;
            if (string.IsNullOrEmpty(title))
                title = Title;
            renderer.Write(@$"<div class=""{Class} custom-block"">
                <p class=""custom-block-title"">{title}</p>");
        }
        // We don't escape a CustomContainer
        renderer.WriteChildren(obj);
        if (renderer.EnableHtmlForBlock)
        {
            renderer.WriteLine("</div>");
        }
    }
}

public class IncludeContainerInlineRenderer : HtmlObjectRenderer<CustomContainerInline>
{
    protected override void Write(HtmlRenderer renderer, CustomContainerInline obj)
    {
        var include = obj.FirstChild is LiteralInline literalInline
            ? literalInline.Content.AsSpan().RightPart(' ').ToString()
            : null;
        if (string.IsNullOrEmpty(include))
            return;
        
        renderer.Write("<div").WriteAttributes(obj).Write('>');
        MarkdownFileBase? doc = null;
        if (include.EndsWith(".md"))
        {
            var markdown = HostContext.Resolve<MarkdownPages>();
            include = include.TrimStart('/');
            var prefix = include.LeftPart('/');
            var slug = include.LeftPart('.');
            doc = markdown.GetVisiblePages(prefix, allDirectories: true)
                .FirstOrDefault(x => x.Slug == slug);
        }
        renderer.WriteLine(doc != null ? doc.Preview! : $"Could not find: {include}");
        renderer.Write("</div>");
    }
}

public class CustomContainerRenderers : HtmlObjectRenderer<CustomContainer>
{
    public Dictionary<string, HtmlObjectRenderer<CustomContainer>> Renderers { get; set; } = new();
    protected override void Write(HtmlRenderer renderer, CustomContainer obj)
    {
        var useRenderer = obj.Info != null && Renderers.TryGetValue(obj.Info, out var customRenderer)
            ? customRenderer
            : new HtmlCustomContainerRenderer();
        useRenderer.Write(renderer, obj);
    }
}

public class CustomContainerInlineRenderers : HtmlObjectRenderer<CustomContainerInline>
{
    public Dictionary<string, HtmlObjectRenderer<CustomContainerInline>> Renderers { get; set; } = new();
    protected override void Write(HtmlRenderer renderer, CustomContainerInline obj)
    {
        var firstWord = obj.FirstChild is LiteralInline literalInline
            ? literalInline.Content.AsSpan().LeftPart(' ').ToString()
            : null;
        var useRenderer = firstWord != null && Renderers.TryGetValue(firstWord, out var customRenderer)
            ? customRenderer
            : new HtmlCustomContainerInlineRenderer();
        useRenderer.Write(renderer, obj);
    }
}

public class ContainerExtensions : IMarkdownExtension
{
    public void Setup(MarkdownPipelineBuilder pipeline)
    {
        if (!pipeline.BlockParsers.Contains<CustomContainerParser>())
        {
            // Insert the parser before any other parsers
            pipeline.BlockParsers.Insert(0, new CustomContainerParser());
        }

        // Plug the inline parser for CustomContainerInline
        var inlineParser = pipeline.InlineParsers.Find<EmphasisInlineParser>();
        if (inlineParser != null && !inlineParser.HasEmphasisChar(':'))
        {
            inlineParser.EmphasisDescriptors.Add(new EmphasisDescriptor(':', 2, 2, true));
            inlineParser.TryCreateEmphasisInlineList.Add((emphasisChar, delimiterCount) =>
            {
                if (delimiterCount >= 2 && emphasisChar == ':')
                {
                    return new CustomContainerInline();
                }
                return null;
            });
        }        
    }

    public void Setup(MarkdownPipeline pipeline, IMarkdownRenderer renderer)
    {
        if (renderer is HtmlRenderer htmlRenderer)
        {
            if (!htmlRenderer.ObjectRenderers.Contains<CustomContainerRenderers>())
            {
                // Must be inserted before CodeBlockRenderer
                htmlRenderer.ObjectRenderers.Insert(0, new CustomContainerRenderers
                {
                    Renderers =
                    {
                        ["sh"] = new CopyContainerRenderer
                        {
                            Class = "not-prose sh-copy cp",
                            BoxClass = "bg-gray-800",
                            IconClass = "bg-green-600",
                            TextClass = "whitespace-pre text-base text-gray-100",
                        },
                        ["nuget"] = new CopyContainerRenderer
                        {
                            Class = "not-prose nuget-copy cp",
                            IconClass = "bg-sky-500",
                        },
                        ["tip"] = new CustomInfoRenderer(),
                        ["info"] = new CustomInfoRenderer
                        {
                            Class = "info",
                            Title = "INFO",
                        },
                        ["warning"] = new CustomInfoRenderer
                        {
                            Class = "warning",
                            Title = "WARNING",
                        },
                        ["danger"] = new CustomInfoRenderer
                        {
                            Class = "danger",
                            Title = "DANGER",
                        },
                    }
                });
            }
            
            htmlRenderer.ObjectRenderers.TryRemove<HtmlCustomContainerInlineRenderer>();
            // Must be inserted before EmphasisRenderer
            htmlRenderer.ObjectRenderers.Insert(0, new CustomContainerInlineRenderers
            {
                Renderers =
                {
                    ["include"] = new IncludeContainerInlineRenderer(),
                }
            });
        }
    }
}

public class HeadingsMapExtension : IMarkdownExtension
{
    public void Setup(MarkdownPipelineBuilder pipeline)
    {
        var headingBlockParser = pipeline.BlockParsers.Find<HeadingBlockParser>();
        if (headingBlockParser != null)
        {
            // Install a hook on the HeadingBlockParser when a HeadingBlock is actually processed
            // headingBlockParser.Closed -= HeadingBlockParser_Closed;
            // headingBlockParser.Closed += HeadingBlockParser_Closed;
        }
    }

    public void Setup(MarkdownPipeline pipeline, IMarkdownRenderer renderer)
    {
        if (renderer.ObjectRenderers.TryFind<AutoLinkHeadingRenderer>(out var customHeader))
        {
            customHeader.OnHeading += OnHeading;
        }
    }

    private void OnHeading(HeadingBlock headingBlock)
    {
        if (headingBlock.Parent is not MarkdownDocument document)
            return;
        
        if (document.GetData(nameof(DocumentMap)) is not DocumentMap docMap)
        {
            docMap = new();
            document.SetData(nameof(DocumentMap), docMap);
        }

        var text = headingBlock.Inline?.FirstChild is LiteralInline literalInline
            ? literalInline.ToString()
            : null;
        var attrs = headingBlock.TryGetAttributes();
            
        if (!string.IsNullOrEmpty(text) && attrs?.Id != null)
        {
            if (headingBlock.Level == 2)
            {
                docMap.Headings.Add(new MarkdownMenu {
                    Text = text,
                    Link = $"#{attrs.Id}",
                });
            }
            else if (headingBlock.Level == 3)
            {
                var lastHeading = docMap.Headings.LastOrDefault();
                if (lastHeading != null)
                {
                    lastHeading.Children ??= new();
                    lastHeading.Children.Add(new MarkdownMenuItem {
                        Text = text,
                        Link = $"#{attrs.Id}",
                    });
                }
            }
        }
    }
}

public static class MarkdigExtensions
{
    private static readonly Regex InvalidCharsRegex = new(@"[^a-z0-9\s-_]", RegexOptions.Compiled);
    private static readonly Regex SpacesRegex = new(@"\s", RegexOptions.Compiled);
    private static readonly Regex CollapseHyphensRegex = new("-+", RegexOptions.Compiled);
    private static readonly Regex RemoveNonAsciiRegex = new(@"[^\u0000-\u007F]+", RegexOptions.Compiled);
    public static string CreateSlug(this string phrase, int maxLength = 100)
    {
        if (string.IsNullOrEmpty(phrase))
            return string.Empty;

        var str = phrase.ToLower()
            .Replace("#", "sharp")  // c#, f# => csharp, fsharp
            .Replace("++", "pp");   // c++ => cpp

        str = RemoveNonAsciiRegex.Replace(str, "");
        str = InvalidCharsRegex.Replace(str, "-");
        str = str.Substring(0, Math.Min(str.Length, maxLength)).Trim();
        str = SpacesRegex.Replace(str, "-");
        str = CollapseHyphensRegex.Replace(str, "-");

        if (string.IsNullOrEmpty(str))
            return str;

        if (str[0] == '-')
            str = str.Substring(1);
        if (str.Length > 0 && str[str.Length - 1] == '-')
            str = str.Substring(0, str.Length - 1);

        return str;
    }
    
    
    /// <summary>
    /// Uses the auto-identifier extension.
    /// </summary>
    public static MarkdownPipelineBuilder UseAutoLinkHeadings(this MarkdownPipelineBuilder pipeline)
    {
        pipeline.Extensions.AddIfNotAlready(new AutoLinkHeadingsExtension());
        return pipeline;
    }
    
    public static MarkdownPipelineBuilder UseHeadingsMap(this MarkdownPipelineBuilder pipeline)
    {
        pipeline.Extensions.AddIfNotAlready(new HeadingsMapExtension());
        return pipeline;
    }
    
    public static MarkdownPipelineBuilder UseCustomContainers(this MarkdownPipelineBuilder pipeline)
    {
        pipeline.Extensions.AddIfNotAlready(new ContainerExtensions());
        return pipeline;
    }
}

public class DocumentMap
{
    public List<MarkdownMenu> Headings { get; } = new();
}

public class MarkdownMenu
{
    public string? Icon { get; set; }
    public string? Text { get; set; }
    public string? Link { get; set; }
    public List<MarkdownMenuItem>? Children { get; set; }
}
public class MarkdownMenuItem
{
    public string Text { get; set; } 
    public string Link { get; set; } 
}