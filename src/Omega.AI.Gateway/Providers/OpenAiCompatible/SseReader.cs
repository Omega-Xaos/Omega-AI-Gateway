namespace Omega.AI.Gateway.Providers.OpenAiCompatible;

internal static class SseReader
{
    public static async IAsyncEnumerable<string> ReadDataAsync(
        Stream stream,
        [System.Runtime.CompilerServices.EnumeratorCancellation] CancellationToken cancellationToken = default)
    {
        using var reader = new StreamReader(stream, leaveOpen: true);
        var dataLines = new List<string>();
        while (await reader.ReadLineAsync(cancellationToken).ConfigureAwait(false) is { } line)
        {
            if (line.Length == 0)
            {
                if (dataLines.Count > 0)
                {
                    yield return string.Join("\n", dataLines);
                    dataLines.Clear();
                }

                continue;
            }

            if (line.StartsWith(':'))
            {
                continue;
            }

            if (line.StartsWith("data:", StringComparison.Ordinal))
            {
                var value = line[5..];
                dataLines.Add(value.StartsWith(' ') ? value[1..] : value);
            }
        }

        if (dataLines.Count > 0)
        {
            yield return string.Join("\n", dataLines);
        }
    }
}
