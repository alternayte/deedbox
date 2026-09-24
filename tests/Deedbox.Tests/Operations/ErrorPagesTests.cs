namespace Deedbox.Tests.Operations;

public sealed class ErrorPagesTests
{
    [Fact]
    public void Every_error_code_has_a_docs_page_with_its_catalogue_title()
    {
        var folder = Path.Combine(Root(), "site", "src", "content", "docs", "reference", "errors");

        foreach (var (code, title) in Errors.Titles)
        {
            var page = Path.Combine(folder, code.ToLowerInvariant() + ".md");
            Assert.True(File.Exists(page), $"{code} has no docs page at {page}.");
            Assert.Contains($"title: \"{code}: {title}\"", File.ReadAllText(page), StringComparison.Ordinal);
        }
    }

    private static string Root([System.Runtime.CompilerServices.CallerFilePath] string file = "") =>
        Path.GetFullPath(Path.Combine(Testing.EventContracts.CallerDirectory(file), "..", "..", ".."));
}
