namespace Deedbox;

/// <summary>
/// The base type of every error Deedbox raises. <see cref="Code"/> identifies the error; the message
/// states the fix and ends with a link to the error's docs page.
/// </summary>
public class DeedboxException : Exception
{
    /// <summary>Creates an error with a DBX code.</summary>
    /// <param name="code">The DBX code, such as <c>DBX001</c>.</param>
    /// <param name="message">What went wrong and how to fix it, without the docs link.</param>
    /// <param name="innerException">The underlying error, if any.</param>
    public DeedboxException(string code, string message, Exception? innerException = null)
        : base($"{code}: {message} See {DocsUrl(code)}", innerException)
    {
        Code = code;
    }

    /// <summary>The DBX code, such as <c>DBX001</c>.</summary>
    public string Code { get; }

    internal static string DocsUrl(string code) => $"https://deedbox.dev/reference/errors/{code.ToLowerInvariant()}/";
}
