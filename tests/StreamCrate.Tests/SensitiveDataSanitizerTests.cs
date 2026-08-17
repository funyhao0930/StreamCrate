using StreamCrate.Infrastructure.Diagnostics;

namespace StreamCrate.Tests;

public sealed class SensitiveDataSanitizerTests
{
    [Fact]
    public void Sanitize_removes_query_strings_from_error_urls()
    {
        const string error = "ERROR: Request failed for https://example.test/watch?id=abc&token=secret";

        var sanitized = SensitiveDataSanitizer.Sanitize(error);

        Assert.Equal("ERROR: Request failed for https://example.test/watch", sanitized);
    }
}
