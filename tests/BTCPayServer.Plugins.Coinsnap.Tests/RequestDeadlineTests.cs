using System.Net;

namespace BTCPayServer.Plugins.Coinsnap.Tests;

/// <summary>A response that stalls after its headers must not hold up the caller past the client's timeout.</summary>
public class RequestDeadlineTests
{
    [Fact]
    public async Task ABodyThatStallsAfterTheHeadersFailsWithinTheTimeout()
    {
        using var http = new HttpClient(new StallingHandler()) { Timeout = TimeSpan.FromSeconds(1) };
        var client = new CoinsnapHttpClient(http);

        var call = client.GetAsync(
            new Uri($"https://{CoinsnapConstants.Host}/.well-known/lnurlp/satoshi"),
            "metadata",
            CancellationToken.None);
        var finished = await Task.WhenAny(call, Task.Delay(TimeSpan.FromSeconds(5)));

        Assert.Same(call, finished);
        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => call);
    }

    private sealed class StallingHandler : HttpMessageHandler
    {
        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken) =>
            Task.FromResult(new HttpResponseMessage(HttpStatusCode.OK) { Content = new StreamContent(new StallingStream()) });
    }

    private sealed class StallingStream : Stream
    {
        private bool _sentFirstByte;

        public override async ValueTask<int> ReadAsync(Memory<byte> buffer, CancellationToken cancellationToken = default)
        {
            if (!_sentFirstByte)
            {
                _sentFirstByte = true;
                buffer.Span[0] = (byte)'{';
                return 1;
            }
            await Task.Delay(Timeout.Infinite, cancellationToken);
            return 0;
        }

        public override Task<int> ReadAsync(byte[] buffer, int offset, int count, CancellationToken cancellationToken) =>
            ReadAsync(buffer.AsMemory(offset, count), cancellationToken).AsTask();

        public override int Read(byte[] buffer, int offset, int count) => throw new NotSupportedException();
        public override bool CanRead => true;
        public override bool CanSeek => false;
        public override bool CanWrite => false;
        public override long Length => throw new NotSupportedException();
        public override long Position { get => throw new NotSupportedException(); set => throw new NotSupportedException(); }
        public override void Flush() { }
        public override long Seek(long offset, SeekOrigin origin) => throw new NotSupportedException();
        public override void SetLength(long value) => throw new NotSupportedException();
        public override void Write(byte[] buffer, int offset, int count) => throw new NotSupportedException();
    }
}
