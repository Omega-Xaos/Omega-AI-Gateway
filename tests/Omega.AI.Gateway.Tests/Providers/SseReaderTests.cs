using System.Text;
using Omega.AI.Gateway.Providers.OpenAiCompatible;
using Xunit;

namespace Omega.AI.Gateway.Tests.Providers;

public sealed class SseReaderTests
{
    [Fact]
    public async Task Reader_ignores_comments_and_preserves_multiline_data()
    {
        await using var stream = new MemoryStream(Encoding.UTF8.GetBytes(": heartbeat\ndata: first\ndata: second\n\ndata: [DONE]\n\n"));

        var events = new List<string>();
        await foreach (var item in SseReader.ReadDataAsync(stream)) events.Add(item);

        Assert.Equal(new[] { "first\nsecond", "[DONE]" }, events);
    }

    [Fact]
    public async Task Reader_flushes_last_event_when_stream_closes_without_blank_line()
    {
        await using var stream = new MemoryStream(Encoding.UTF8.GetBytes("data: final"));

        var events = new List<string>();
        await foreach (var item in SseReader.ReadDataAsync(stream)) events.Add(item);

        Assert.Equal("final", Assert.Single(events));
    }
}
