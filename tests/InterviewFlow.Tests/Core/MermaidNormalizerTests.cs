using InterviewFlow.Core.Markdown;

namespace InterviewFlow.Tests.Core;

public sealed class MermaidNormalizerTests
{
    [Fact]
    public void Decodes_html_entities()
    {
        Assert.Contains("A --> B", MermaidNormalizer.Normalize("graph LR\nA --&gt; B"));
    }

    [Fact]
    public void Rewrites_first_graph_td_to_lr_only()
    {
        var result = MermaidNormalizer.Normalize("graph TD\nA --> B\n%% graph TD in comment");
        Assert.StartsWith("graph LR", result);
        Assert.Contains("%% graph TD in comment", result); // only the FIRST match is rewritten
    }

    [Fact]
    public void Leaves_flowchart_td_alone()
    {
        var result = MermaidNormalizer.Normalize("flowchart TD\nA --> B");
        Assert.StartsWith("flowchart TD", result);
    }

    [Fact]
    public void Strips_backtick_labels_and_debullets()
    {
        var result = MermaidNormalizer.Normalize("graph LR\nA[\"`- one\n- two`\"]");
        Assert.Contains("A[\"one<br/>two\"]", result);
    }

    [Fact]
    public void Normalizes_br_variants_and_literal_newlines()
    {
        var result = MermaidNormalizer.Normalize("graph LR\nA[\"x\\ny\"] --> B[\"p<BR>q<br />r\"]");
        Assert.Contains("A[\"x<br/>y\"]", result);
        Assert.Contains("B[\"p<br/>q<br/>r\"]", result);
    }

    [Fact]
    public void Balances_unclosed_subgraphs()
    {
        var result = MermaidNormalizer.Normalize("graph LR\nsubgraph One\nA --> B\nsubgraph Two\nC --> D\nend");
        var opens = result.Split('\n').Count(l => l.TrimStart().StartsWith("subgraph"));
        var ends = result.Split('\n').Count(l => l.Trim() == "end");
        Assert.Equal(opens, ends);
    }
}
